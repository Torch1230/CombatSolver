import http from 'node:http';
import https from 'node:https';
import { readFileSync, mkdirSync } from 'node:fs';
import { createHmac, randomBytes, scryptSync, timingSafeEqual } from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { aggregateHistory } from './history.mjs';
import { comparePeriods } from './comparisons.mjs';
import { createWorkshopCounter } from './workshop.mjs';
import { createRunStatistics, validateRun, validateHistory, validateSnapshot, parseStatisticsFilters } from './run-statistics.mjs';

const root = fileURLToPath(new URL('.', import.meta.url));
export const TTL = 90_000;
export const PAGE_SIZE = 30;
const SESSION_SECONDS = 14 * 86400;
const SESSION_COOKIE = 'cs_presence_session';
export function validate(body) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) return false;
  const fields = ['sessionId','name','character','floor','encounter','hpLoss','version'];
  if (Object.keys(body).some(key => ![...fields,'inCombat','battleUpdatedAt','inRun','runStatistics'].includes(key)) || fields.some(key => !(key in body))) return false;
  if ('runStatistics' in body && !validateSnapshot(body.runStatistics)) return false;
  if ('inRun' in body && typeof body.inRun !== 'boolean') return false;
  if ('inCombat' in body && typeof body.inCombat !== 'boolean') return false;
  if ('battleUpdatedAt' in body && body.battleUpdatedAt !== null && (!Number.isSafeInteger(body.battleUpdatedAt) || body.battleUpdatedAt < 0)) return false;
  if (typeof body.sessionId !== 'string' || !/^[a-f0-9]{32}$/.test(body.sessionId)) return false;
  for (const [key, max] of [['name',128],['character',128],['encounter',512],['version',32]]) {
    if (typeof body[key] !== 'string' || body[key].length > max || /[\u0000-\u001f]/.test(body[key])) return false;
  }
  return (body.floor === null || Number.isInteger(body.floor) && body.floor >= 0 && body.floor <= 10000)
    && (body.hpLoss === null || Number.isInteger(body.hpLoss) && body.hpLoss >= 0 && body.hpLoss <= 10000000);
}

export function createApp({ database = ':memory:', password, now = Date.now, secureCookie = false, publicHost }) {
  if (!password || password.length < 20) throw new Error('ADMIN_PASSWORD must contain at least 20 characters');
  const db = new DatabaseSync(database);
  const runStatistics = createRunStatistics(db,now);
  const workshop = createWorkshopCounter(db,{now});
  db.exec('PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; CREATE TABLE IF NOT EXISTS history (time INTEGER PRIMARY KEY, count INTEGER NOT NULL) STRICT;');
  db.exec('CREATE TABLE IF NOT EXISTS durations (session_id TEXT PRIMARY KEY, total_ms INTEGER NOT NULL, last_seen INTEGER NOT NULL) STRICT;');
  db.exec('CREATE TABLE IF NOT EXISTS admin_sessions (token_hash TEXT PRIMARY KEY, expires_at INTEGER NOT NULL) STRICT;');
  db.exec('CREATE TABLE IF NOT EXISTS release_settings (id INTEGER PRIMARY KEY CHECK(id=1), version TEXT) STRICT;');
  const releaseRead = db.prepare('SELECT version FROM release_settings WHERE id=1');
  const releaseWrite = db.prepare('INSERT INTO release_settings(id,version) VALUES (1,?) ON CONFLICT(id) DO UPDATE SET version=excluded.version');
  const currentRelease = () => ({latestVersion:releaseRead.get()?.version ?? null});
  // Player nicknames and combat details stay in memory. Admin sessions persist separately.
  const players = new Map(), buckets = new Map();
  // A fresh in-memory roster is incomplete until one full presence lease has elapsed.
  const samplingReadyAt = now() + TTL;
  const sessionKey = token => createHmac('sha256', password).update(token).digest('hex');
  const sessionRead = db.prepare('SELECT expires_at FROM admin_sessions WHERE token_hash=?');
  const sessionWrite = db.prepare('INSERT INTO admin_sessions(token_hash,expires_at) VALUES (?,?)');
  const sessionDelete = db.prepare('DELETE FROM admin_sessions WHERE token_hash=?');
  const sessionExpire = db.prepare('DELETE FROM admin_sessions WHERE expires_at<=?');
  const salt = randomBytes(32), passwordHash = scryptSync(password, salt, 32);
  const allowedHosts = new Set(['127.0.0.1', 'localhost', '[::1]']);
  if (publicHost) allowedHosts.add(publicHost);
  const historyInsert = db.prepare('INSERT INTO history(time,count) VALUES (?,?) ON CONFLICT(time) DO UPDATE SET count=excluded.count');
  const historyRead = db.prepare('SELECT time,count FROM history WHERE time >= ? ORDER BY time');
  const historyDelete = db.prepare('DELETE FROM history WHERE time < ?');
  const durationRead = db.prepare('SELECT total_ms,last_seen FROM durations WHERE session_id = ?');
  const durationWrite = db.prepare('INSERT INTO durations(session_id,total_ms,last_seen) VALUES (?,?,?) ON CONFLICT(session_id) DO UPDATE SET total_ms=excluded.total_ms,last_seen=excluded.last_seen');
  function expire() {
    const t = now();
    for (const [key,p] of players) if (p.lastSeen <= t - TTL) players.delete(key);
    sessionExpire.run(t);
    for (const [key,b] of buckets) if (b.until <= t) buckets.delete(key);
  }
  function sample() {
    expire();
    if (now() >= samplingReadyAt)
      historyInsert.run(Math.floor(now()/60000)*60000, players.size);
    historyDelete.run(now() - 90*86400000);
  }
  function limit(key, max) {
    let b = buckets.get(key);
    if (!b || b.until <= now()) {
      if (buckets.size >= 20000) return false;
      buckets.set(key, b = {until: now()+60000, count: 0});
    }
    return ++b.count <= max;
  }
  function send(res, status, body) {
    res.writeHead(status, {'Content-Type':'application/json; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});
    res.end(body === undefined ? '' : JSON.stringify(body));
  }
  async function json(req, maximum = 4096) {
    if (!req.headers['content-type']?.startsWith('application/json')) throw new InputError(415);
    if (Number(req.headers['content-length']) > maximum) throw new InputError(413);
    const chunks = []; let size = 0;
    for await (const chunk of req) {
      size += chunk.length;
      if (size > maximum) throw new InputError(413);
      chunks.push(chunk);
    }
    try { return JSON.parse(Buffer.concat(chunks).toString('utf8')); }
    catch { throw new InputError(400); }
  }
  class InputError extends Error { constructor(status) { super('Invalid request'); this.status = status; } }
  const handle = fn => async (req,res) => {
    try { await fn(req,res); }
    catch (error) {
      if (error instanceof InputError) send(res,error.status,{error:'invalid_request'});
      else { console.error('request_failure',error.code || error.name); send(res,500,{error:'server_error'}); }
    }
  };
  const collector = handle(async (req,res) => {
    if (req.method === 'POST' && ['/v1/runs','/v1/run-history'].includes(req.url)) {
      expire();
      if (!limit('runs-ip:'+req.socket.remoteAddress,240)) return send(res,429);
      const body=await json(req,65536);
      if (!body || !/^[a-f0-9]{32}$/.test(body.sessionId || '')) return send(res,400);
      if (!limit('runs-player:'+body.sessionId,60)) return send(res,429);
      if(req.url==='/v1/runs') {
        if(!validateRun(body.run))return send(res,400);
        if(!runStatistics.save(body.sessionId,body.run))return send(res,409,{error:'run_conflict'});
      } else {
        if(!validateHistory(body.historical))return send(res,400);
        runStatistics.import(body.sessionId,body.historical);
      }
      return send(res,204);
    }
    if (req.method !== 'POST' || req.url !== '/v1/heartbeat') return send(res,404);
    expire();
    if (!limit('ip:'+req.socket.remoteAddress,240)) return send(res,429);
    const body = await json(req,65536);
    if (!validate(body)) return send(res,400,{error:'invalid_payload'});
    if (!limit('player:'+body.sessionId,6)) return send(res,429);
    if (!players.has(body.sessionId) && players.size >= 10000) return send(res,503);
    const receivedAt = now();
    const previous = durationRead.get(body.sessionId);
    const elapsed = previous ? Math.max(0, receivedAt-previous.last_seen) : 0;
    const totalMs = (previous?.total_ms ?? 0) + (elapsed < TTL ? elapsed : 0);
    durationWrite.run(body.sessionId,totalMs,receivedAt);
    const prior = players.get(body.sessionId);
    const inCombat = body.inCombat ?? Boolean(body.encounter);
    const complete = body.character.length > 0 && body.floor !== null && body.encounter.length > 0 && body.hpLoss !== null;
    const battle = complete
      ? {character:body.character,floor:body.floor,encounter:body.encounter,hpLoss:body.hpLoss,battleUpdatedAt:body.battleUpdatedAt ?? receivedAt}
      : prior && prior.hpLoss !== null
        ? {character:prior.character,floor:prior.floor,encounter:prior.encounter,hpLoss:prior.hpLoss,battleUpdatedAt:prior.battleUpdatedAt}
        : {character:'',floor:null,encounter:'',hpLoss:null,battleUpdatedAt:null};
    players.set(body.sessionId,{...body,...battle,inCombat,inRun:body.inRun ?? null,totalMs,lastSeen:receivedAt});
    send(res,200,currentRelease());
  });
  const files = new Map([
    ['/', ['public/index.html','text/html; charset=utf-8']],
    ['/app.js',['public/app.js','text/javascript; charset=utf-8']],
    ['/style.css',['public/style.css','text/css; charset=utf-8']],
    ['/chart.js',['node_modules/chart.js/dist/chart.umd.js','text/javascript; charset=utf-8']],
  ]);
  const admin = handle(async (req,res) => {
    const host = req.headers.host;
    if (!host) return send(res,400);
    let url;
    const scheme = req.socket.encrypted ? 'https' : 'http';
    try { url = new URL(req.url,`${scheme}://${host}`); }
    catch { return send(res,400); }
    if (!allowedHosts.has(url.hostname)) return send(res,403);
    if (req.method === 'POST' && req.headers.origin !== `${scheme}://${host}`) return send(res,403);
    if (req.method === 'POST' && url.pathname === '/api/login') {
      expire();
      if (!limit('login:'+req.socket.remoteAddress,5)) return send(res,429);
      const body = await json(req);
      if (typeof body?.password !== 'string' || body.password.length > 256) return send(res,400);
      if (!timingSafeEqual(scryptSync(body.password,salt,32),passwordHash)) return send(res,401);
      const token = randomBytes(32).toString('hex');
      sessionWrite.run(sessionKey(token),now()+SESSION_SECONDS*1000);
      res.setHeader('Set-Cookie',`${SESSION_COOKIE}=${token}; HttpOnly; SameSite=Strict; Path=/; Max-Age=${SESSION_SECONDS}${secureCookie || req.socket.encrypted?'; Secure':''}`);
      return send(res,200,{ok:true});
    }
    const token = /(?:^|;\s*)cs_presence_session=([a-f0-9]{64})(?:;|$)/.exec(req.headers.cookie || '')?.[1];
    if (url.pathname.startsWith('/api/')) {
      if (!token || (sessionRead.get(sessionKey(token))?.expires_at ?? 0) <= now()) return send(res,401);
      if (req.method === 'GET' && url.pathname === '/api/release')
        return send(res,200,currentRelease());
      if (req.method === 'POST' && url.pathname === '/api/release') {
        const body = await json(req);
        const version = body?.latestVersion;
        if (!body || Object.keys(body).length !== 1 || !Object.hasOwn(body,'latestVersion')
          || version !== null && (typeof version !== 'string' || !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version)
            || version.length > 32 || version.split('.').some(part => Number(part) > 2147483647))) return send(res,400);
        releaseWrite.run(version);
        return send(res,200,currentRelease());
      }
      if (req.method === 'POST' && url.pathname === '/api/logout') {
        sessionDelete.run(sessionKey(token));
        res.setHeader('Set-Cookie',`${SESSION_COOKIE}=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0${secureCookie || req.socket.encrypted?'; Secure':''}`);
        return send(res,204);
      }
      if (req.method === 'GET' && url.pathname === '/api/overview') {
        expire();
        const hours = Number(url.searchParams.get('hours') || 24);
        const maxPoints = Number(url.searchParams.get('maxPoints') ?? 240);
        if (![1,24,168,720].includes(hours) || !Number.isInteger(maxPoints) || maxPoints < 32 || maxPoints > 240) return send(res,400);
        const at = now();
        const history = aggregateHistory(historyRead.all(at-hours*3600000),hours,maxPoints);
        const comparisons = comparePeriods(historyRead.all(at-Math.max(2*hours*3600000,hours*3600000+7*86400000)-60000),at,hours);
        return send(res,200,{now:at,ttl:TTL,samplingReady:at>=samplingReadyAt,samplingReadyAt,onlineCount:players.size,fightingCount:[...players.values()].filter(player=>player.inCombat).length,inRunCount:[...players.values()].filter(player=>player.inRun === true).length,runStatusUnknownCount:[...players.values()].filter(player=>player.inRun === null).length,...history,comparisons,workshop:workshop.snapshot()});
      }
      if (req.method === 'GET' && url.pathname === '/api/players') {
        expire();
        const pageValue = url.searchParams.get('page') ?? '1';
        const query = (url.searchParams.get('q') ?? '').trim();
        const sort = url.searchParams.get('sort') || 'online';
        const order = url.searchParams.get('order') || 'desc';
        if (!['online','floor','hpLoss','lastSeen'].includes(sort) || !['asc','desc'].includes(order)) return send(res,400);
        if (!/^[1-9]\d*$/.test(pageValue) || !Number.isSafeInteger(Number(pageValue)) || query.length > 128)
          return send(res,400);
        const term = query.toLocaleLowerCase();
        const ranked = [...players.values()]
          .sort((a,b)=>b.totalMs-a.totalMs || a.sessionId.localeCompare(b.sessionId))
          .map(({totalMs,...player},index)=>({...player,rank:index+1,onlineSeconds:Math.floor(totalMs/1000)}));
        const matching = term ? ranked.filter(player=>[player.name,player.character,player.encounter].some(value=>value.toLocaleLowerCase().includes(term))) : ranked;
        const key=sort==='online'?'onlineSeconds':sort;
        matching.sort((a,b)=>{
          if(a[key]===null || b[key]===null)return (a[key]===null)-(b[key]===null) || a.sessionId.localeCompare(b.sessionId);
          return (order==='asc'?1:-1)*(a[key]-b[key]) || a.sessionId.localeCompare(b.sessionId);
        });
        const total = matching.length;
        const totalPages = Math.max(1,Math.ceil(total/PAGE_SIZE));
        const page = Math.min(Number(pageValue),totalPages);
        const offset = (page-1)*PAGE_SIZE;
        return send(res,200,{now:now(),onlineCount:players.size,total,page,pageSize:PAGE_SIZE,totalPages,players:matching.slice(offset,offset+PAGE_SIZE)});
      }
      if (req.method === 'GET' && url.pathname === '/api/run-statistics') {
        let filters;
        try { filters=parseStatisticsFilters(url.searchParams); } catch (error) { if(error instanceof RangeError)return send(res,400,{error:error.message}); throw error; }
        const page=Number(url.searchParams.get('page') || 1);
        if(!Number.isSafeInteger(page) || page<1)return send(res,400);
        const result=runStatistics.query(filters);
        const pages=Math.max(1,Math.ceil(result.total/PAGE_SIZE)), current=Math.min(page,pages);
        result.entries=result.entries.slice((current-1)*PAGE_SIZE,current*PAGE_SIZE).map(entry=>({...entry,name:players.get(entry.sessionId)?.name || '离线玩家',online:players.has(entry.sessionId)}));
        return send(res,200,{...result,page:current,totalPages:pages,pageSize:PAGE_SIZE});
      }
      return send(res,404);
    }
    const file = files.get(url.pathname);
    if (req.method !== 'GET' || !file) return send(res,404);
    res.writeHead(200,{'Content-Type':file[1],'Cache-Control':'no-store','X-Content-Type-Options':'nosniff','Referrer-Policy':'no-referrer','Content-Security-Policy':"default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'"});
    res.end(readFileSync(resolve(root,file[0])));
  });
  return {collector,admin,sample,expire,refreshWorkshop:()=>workshop.refresh(),close:()=>db.close()};
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  mkdirSync(resolve(root,'data'),{recursive:true,mode:0o700});
  const adminTls = process.env.ADMIN_TLS === 'true';
  const adminBind = process.env.ADMIN_BIND || '127.0.0.1';
  if (!['127.0.0.1','::1'].includes(adminBind) && (!adminTls || !process.env.ADMIN_PUBLIC_HOST))
    throw new Error('Public admin listener requires ADMIN_TLS and ADMIN_PUBLIC_HOST');
  const app = createApp({database:process.env.DATABASE_PATH || resolve(root,'data/presence.sqlite'),password:process.env.ADMIN_PASSWORD,publicHost:process.env.ADMIN_PUBLIC_HOST});
  const tls = {key:readFileSync(process.env.TLS_KEY),cert:readFileSync(process.env.TLS_CERT),minVersion:'TLSv1.2'};
  const admin = (adminTls ? https : http).createServer({...(adminTls ? tls : {}),requestTimeout:10000,headersTimeout:10000,maxHeaderSize:8192},app.admin);
  admin.maxConnections = 30;
  admin.listen(Number(process.env.ADMIN_PORT || 12889),adminBind);
  const collector = https.createServer({...tls,requestTimeout:10000,headersTimeout:10000,maxHeaderSize:8192},app.collector);
  collector.maxConnections = 200;
  collector.listen(Number(process.env.COLLECTOR_PORT || 12888),'0.0.0.0');
  app.sample();
  app.refreshWorkshop();
  const workshopTimer = setInterval(app.refreshWorkshop,600000);
  const timer = setInterval(app.sample,60000);
  const expiry = setInterval(app.expire,10000);
  for (const signal of ['SIGINT','SIGTERM']) process.on(signal,()=>{
    clearInterval(timer); clearInterval(expiry); clearInterval(workshopTimer);
    admin.close(); collector.close(); admin.closeAllConnections(); collector.closeAllConnections(); app.close();
  });
  console.log('Presence service started');
}
