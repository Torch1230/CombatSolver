import {isDeepStrictEqual} from 'node:util';
const id = value => typeof value === 'string' && /^[a-f0-9]{32}$/.test(value);
const integer = (value, max = 10000000) => Number.isSafeInteger(value) && value >= 0 && value <= max;
const text = value => typeof value === 'string' && value.length > 0 && value.length <= 128 && !/[\u0000-\u001f]/.test(value);
export function validateRun(run) {
  if (!run || !id(run.runId) || !id(run.profileId) || !integer(run.startedAt, 1e14)
      || !(run.endedAt === null || integer(run.endedAt, 1e14) && run.endedAt >= run.startedAt)
      || !text(run.characterId) || !text(run.version) || !integer(run.ascension, 100)
      || !['full','partial','none'].includes(run.participation) || !['pending','win','loss','abandoned'].includes(run.outcome)) return false;
  if ((run.outcome === 'pending') !== (run.endedAt === null)) return false;
  if (['observedFromStart','everEnabled','disabledInCombat'].some(k => typeof run[k] !== 'boolean')) return false;
  const expected = !run.everEnabled ? 'none' : run.observedFromStart && !run.disabledInCombat ? 'full' : 'partial';
  if (run.participation !== expected) return false;
  return ['battles','solvedBattles','executedBattles','autoBattles'].every(k => Array.isArray(run[k])
    && run[k].length <= 1000 && run[k].every(text) && new Set(run[k]).size === run[k].length);
}
export function validateHistory(value) {
  return value && id(value.profileId) && integer(value.capturedAt,1e14) && Array.isArray(value.characters)
    && value.characters.length <= 100 && new Set(value.characters.map(c=>c.characterId)).size === value.characters.length
    && value.characters.every(c=>text(c.characterId) && ['wins','losses','currentStreak','bestStreak'].every(k=>integer(c[k])));
}
export function validateSnapshot(value) {
  if (value === null) return true;
  const s = value?.solver;
  return value?.schemaVersion === 1 && id(value.profileId) && integer(value.capturedAt,1e14) && s
    && ['wins','losses','abandoned','currentStreak','bestStreak','completedRuns'].every(k=>integer(s[k]))
    && s.completedRuns === s.wins+s.losses && s.abandoned <= s.losses && s.currentStreak <= s.bestStreak && s.bestStreak <= s.wins
    && (s.completedRuns === 0 ? s.winRate === null : typeof s.winRate==='number' && Number.isFinite(s.winRate) && Math.abs(s.winRate-s.wins/s.completedRuns)<1e-9)
    && (value.historical === null || validateHistory(value.historical) && value.historical.profileId===value.profileId);
}
export function parseStatisticsFilters(params) {
  const filters = {source:params.get('source') || 'solver', participation:params.get('participation') || 'full',
    character:params.get('character') || '', version:params.get('version') || '', activity:params.get('activity') || '', sort:params.get('sort') || 'streak',
    order:params.get('order') || 'desc',sessionId:params.get('sessionId') || '',profileId:params.get('profileId') || ''};
  if(!['asc','desc'].includes(filters.order) || [filters.sessionId,filters.profileId].some(value=>value && !id(value)))throw new RangeError('invalid identity or order');
  if (!['solver','historical'].includes(filters.source) || !['full','partial','none','all'].includes(filters.participation)
    || !['','solve','execute','auto'].includes(filters.activity) || !['streak','best','rate','wins','losses'].includes(filters.sort)
    || filters.character.length>128 || filters.version.length>128) throw new RangeError('invalid statistics filter');
  for (const key of ['streak_min','streak_max','best_min','best_max','rate_min','rate_max','wins_min','losses_min','abandoned_min','runs_min','ascension','since','until']) {
    const raw=params.get(key);
    if (raw===null || raw==='') continue;
    const number=Number(raw);
    if (!Number.isFinite(number) || number<0 || (key.startsWith('rate_') ? number>100 : !Number.isSafeInteger(number))) throw new RangeError('invalid '+key);
    filters[key]=number;
  }
  for (const [lo,hi] of [['streak_min','streak_max'],['best_min','best_max'],['rate_min','rate_max'],['since','until']])
    if (filters[lo]!==undefined && filters[hi]!==undefined && filters[lo]>filters[hi]) throw new RangeError('inverted range');
  if (filters.source==='historical' && (filters.version || filters.activity || ['ascension','since','until'].some(k=>filters[k]!==undefined)))
    throw new RangeError('historical snapshot has no run-level filters');
  return filters;
}
function selected(run,f) {
  return (f.participation==='all' || run.participation===f.participation) && (!f.character || run.characterId===f.character)
    && (!f.version || run.version===f.version) && (f.ascension===undefined || run.ascension===f.ascension)
    && (f.since===undefined || run.endedAt>=f.since) && (f.until===undefined || run.endedAt<=f.until)
    && (!f.activity || run[{solve:'solvedBattles',execute:'executedBattles',auto:'autoBattles'}[f.activity]].length>0);
}
export function summarizeRuns(runs, filters = {participation:'full'}) {
  let wins=0,losses=0,abandoned=0,currentStreak=0,bestStreak=0;
  const sorted=[...runs].sort((a,b)=>a.startedAt-b.startedAt || a.runId.localeCompare(b.runId));
  for (const [index,run] of sorted.entries()) {
    // A currently ongoing run preserves the previous streak; older missing outcomes break it.
    if (run.outcome==='pending' && index===sorted.length-1) continue;
    if (run.outcome==='pending' || !selected(run,filters)) {currentStreak=0;continue;}
    if (run.outcome==='win') {wins++;currentStreak++;bestStreak=Math.max(bestStreak,currentStreak);}
    else {losses++;currentStreak=0;if(run.outcome==='abandoned')abandoned++;}
  }
  return {wins,losses,abandoned,currentStreak,bestStreak,completedRuns:wins+losses,winRate:wins+losses?wins/(wins+losses):null};
}
export function matchesStatistics(s,f) {
  for (const [key,value] of Object.entries({streak:s.currentStreak,best:s.bestStreak,rate:s.winRate===null?null:s.winRate*100,wins:s.wins,losses:s.losses,abandoned:s.abandoned,runs:s.completedRuns})) {
    if (f[key+'_min']!==undefined && (value===null || value<f[key+'_min'])) return false;
    if (f[key+'_max']!==undefined && (value===null || value>f[key+'_max'])) return false;
  }
  return true;
}
export function createRunStatistics(db, now) {
  db.exec(`CREATE TABLE IF NOT EXISTS runs (installation TEXT NOT NULL,run_id TEXT NOT NULL,profile TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(installation,run_id)) STRICT;
    CREATE TABLE IF NOT EXISTS run_history_snapshots (installation TEXT NOT NULL,profile TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(installation,profile)) STRICT;`);
  const read=db.prepare('SELECT payload FROM runs WHERE installation=? AND run_id=?');
  const write=db.prepare('INSERT INTO runs VALUES (?,?,?,?) ON CONFLICT(installation,run_id) DO UPDATE SET payload=excluded.payload');
  return {
    save(installation,run) {
      const prior=read.get(installation,run.runId);
      if(prior) {
        const old=JSON.parse(prior.payload);
        if (old.profileId!==run.profileId || old.startedAt!==run.startedAt || old.characterId!==run.characterId || old.ascension!==run.ascension || old.version!==run.version) return false;
        if (old.outcome!=='pending') return isDeepStrictEqual(old,run);
      }
      write.run(installation,run.runId,run.profileId,JSON.stringify(run));return true;
    },
    import(installation,historical) {
      db.prepare('INSERT INTO run_history_snapshots VALUES (?,?,?) ON CONFLICT DO NOTHING').run(installation,historical.profileId,JSON.stringify(historical));
    },
    query(filters) {
      const groups=new Map();
      const scope=[],args=[];
      for(const [key,column] of [['sessionId','installation'],['profileId','profile']])if(filters[key]){scope.push(column+'=?');args.push(filters[key]);}
      const where=scope.length?' WHERE '+scope.join(' AND '):'';
      if(filters.source==='historical') {
        for(const row of db.prepare('SELECT * FROM run_history_snapshots'+where).all(...args)) {
          const h=JSON.parse(row.payload), chars=h.characters.filter(c=>!filters.character || c.characterId===filters.character);
          if(!chars.length)continue;
          const wins=chars.reduce((s,c)=>s+c.wins,0),losses=chars.reduce((s,c)=>s+c.losses,0);
          groups.set(row.installation+row.profile,{sessionId:row.installation,profileId:row.profile,capturedAt:h.capturedAt,
            statistics:{wins,losses,abandoned:null,currentStreak:filters.character?chars[0].currentStreak:null,
              bestStreak:filters.character?chars[0].bestStreak:null,completedRuns:wins+losses,winRate:wins+losses?wins/(wins+losses):null}});
        }
      } else {
        for(const row of db.prepare('SELECT * FROM runs'+where).all(...args)) {
          const key=row.installation+row.profile;
          if(!groups.has(key))groups.set(key,{sessionId:row.installation,profileId:row.profile,runs:[]});
          groups.get(key).runs.push(JSON.parse(row.payload));
        }
        for(const [key,g] of groups) groups.set(key,{sessionId:g.sessionId,profileId:g.profileId,statistics:summarizeRuns(g.runs,filters)});
      }
      const entries=[...groups.values()].filter(g=>matchesStatistics(g.statistics,filters));
      const sort={streak:'currentStreak',best:'bestStreak',rate:'winRate',wins:'wins',losses:'losses'}[filters.sort];
      entries.sort((a,b)=>{
        const av=a.statistics[sort],bv=b.statistics[sort];
        if(av===null || bv===null)return (av===null)-(bv===null) || a.sessionId.localeCompare(b.sessionId) || a.profileId.localeCompare(b.profileId);
        return (filters.order==='asc'?1:-1)*(av-bv) || a.sessionId.localeCompare(b.sessionId) || a.profileId.localeCompare(b.profileId);
      });
      const wins=entries.reduce((n,g)=>n+g.statistics.wins,0),losses=entries.reduce((n,g)=>n+g.statistics.losses,0);
      return {now:now(),source:filters.source,total:entries.length,wins,losses,winRate:wins+losses?wins/(wins+losses):null,entries};
    },
  };
}
