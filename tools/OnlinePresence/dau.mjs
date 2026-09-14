const DAY=86400000, MINUTE=60000, OFFSET=8*3600000;
export const dayStart=t=>Math.floor((t+OFFSET)/DAY)*DAY-OFFSET;
const label=t=>new Date(t+OFFSET).toISOString().slice(0,10);
export function createDailyActive(db,now=Date.now) {
  db.exec(`CREATE TABLE IF NOT EXISTS daily_active(day INTEGER NOT NULL,installation TEXT NOT NULL,first_minute INTEGER NOT NULL,PRIMARY KEY(day,installation)) WITHOUT ROWID;
    CREATE INDEX IF NOT EXISTS daily_active_minute ON daily_active(day,first_minute);
    CREATE TABLE IF NOT EXISTS dau_coverage(minute INTEGER PRIMARY KEY) STRICT;
    CREATE TABLE IF NOT EXISTS dau_settings(id INTEGER PRIMARY KEY CHECK(id=1),started_at INTEGER NOT NULL) STRICT;`);
  db.prepare('INSERT OR IGNORE INTO dau_settings VALUES(1,?)').run(now());
  const startedAt=db.prepare('SELECT started_at FROM dau_settings WHERE id=1').get().started_at;
  const insert=db.prepare('INSERT OR IGNORE INTO daily_active VALUES(?,?,?)');
  const coverage=db.prepare('INSERT OR IGNORE INTO dau_coverage VALUES(?)');
  const count=db.prepare('SELECT count(*) AS n FROM daily_active WHERE day=? AND first_minute<?');
  const covered=db.prepare('SELECT count(*) AS n FROM dau_coverage WHERE minute>=? AND minute<?');
  const value=(day,minutes)=>{
    const expected=Math.min(1440,minutes),end=day+expected*MINUTE;
    if(end<=startedAt)return {count:null,complete:false};
    return {count:count.get(day,expected).n,complete:startedAt<=day && covered.get(day,end).n===expected};
  };
  return {
    record(installation,t=now()) {const day=dayStart(t);insert.run(day,installation,Math.floor((t-day)/MINUTE));},
    sample() {
      const t=now();coverage.run(Math.floor(t/MINUTE)*MINUTE);
      const cutoff=dayStart(t)-90*DAY;
      db.prepare('DELETE FROM daily_active WHERE day<?').run(cutoff);
      db.prepare('DELETE FROM dau_coverage WHERE minute<?').run(cutoff);
    },
    snapshot() {
      const t=now(),today=dayStart(t),minutes=Math.floor((t-today)/MINUTE);
      // Use completed minutes for both sides of comparisons; current DAU includes the live minute.
      const current=value(today,minutes),live=value(today,minutes+1);
      const compare=days=>{
        const baseline=value(today-days*DAY,minutes);
        const ready=minutes>0 && current.complete && baseline.complete;
        const delta=ready?current.count-baseline.count:null;
        return {ready,current:current.count,baseline:baseline.count,delta,percent:ready && baseline.count>0?delta/baseline.count*100:null};
      };
      return {schemaVersion:1,generatedAt:t,startedAt,timezone:'Asia/Shanghai',today:live.count,comparisonThrough:today+minutes*MINUTE,
        comparisons:{previousDay:compare(1),previousWeek:compare(7)},
        series:Array.from({length:90},(_,i)=>{const day=today-(89-i)*DAY;return {date:label(day),...value(day,day===today?minutes+1:1440),ongoing:day===today};})};
    },
  };
}
