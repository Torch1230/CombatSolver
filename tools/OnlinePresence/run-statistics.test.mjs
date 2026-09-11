import {test} from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {createRunStatistics,validateRun,validateSnapshot,summarizeRuns,parseStatisticsFilters} from './run-statistics.mjs';
import {createApp,validate} from './server.mjs';
import http from 'node:http';
import {once} from 'node:events';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
const id='a'.repeat(32),profile='b'.repeat(32);
function run(n,outcome='win',participation='full') {return {runId:n.toString(16).padStart(32,'0'),profileId:profile,startedAt:n*1000,endedAt:outcome==='pending'?null:n*1000+500,characterId:'SILENT',ascension:10,version:'test',participation,outcome,observedFromStart:participation==='full',everEnabled:participation!=='none',disabledInCombat:false,battles:['1:A'],solvedBattles:['1:A'],executedBattles:[],autoBattles:[]};}
test('streaks, abandonments, unknown gaps, current pending and filters',()=>{
  const records=[run(1),run(2),run(3,'abandoned'),run(4),run(5,'win','partial'),run(6),run(7,'pending')];
  assert.deepEqual(summarizeRuns(records),{wins:4,losses:1,abandoned:1,currentStreak:1,bestStreak:2,completedRuns:5,winRate:.8});
  assert.equal(summarizeRuns([run(1),run(2,'pending'),run(3)]).bestStreak,1);
  assert.equal(summarizeRuns([run(1),{...run(2,'loss'),version:'other'},run(3)],{participation:'full',version:'test'}).bestStreak,1);
  assert.equal(summarizeRuns([]).winRate,null);
  assert.throws(()=>parseStatisticsFilters(new URLSearchParams('rate_min=80&rate_max=20')));
  assert.throws(()=>parseStatisticsFilters(new URLSearchParams('source=historical&version=test')));
});
test('validation, duplicate delivery, conflicts and weighted totals',()=>{
  const db=new DatabaseSync(':memory:'),stats=createRunStatistics(db,()=>10000);
  assert.ok(validateRun(run(1)));assert.ok(!validateRun({...run(1),observedFromStart:false}));
  assert.ok(stats.save(id,run(1)));assert.ok(stats.save(id,run(1)));assert.equal(stats.save(id,run(1,'loss')),false);
  stats.save(id,run(2,'loss'));stats.save('c'.repeat(32),run(3));
  const data=stats.query(parseStatisticsFilters(new URLSearchParams()));
  assert.equal(data.wins,2);assert.equal(data.losses,1);assert.equal(data.winRate,2/3);
  assert.equal(stats.query(parseStatisticsFilters(new URLSearchParams('streak_min=1'))).total,1);
  const snapshot={schemaVersion:1,profileId:profile,capturedAt:10000,solver:summarizeRuns([run(1)]),historical:null};
  assert.ok(validateSnapshot(snapshot));assert.ok(!validateSnapshot({...snapshot,solver:{...snapshot.solver,completedRuns:2}}));
  db.close();
});
test('public run collection, admin auth, persistence and old heartbeat compatibility',async()=>{
  const app=createApp({password:'test-long-secret-for-run-statistics'});
  const server=http.createServer(app.collector).listen(0,'127.0.0.1'),admin=http.createServer(app.admin).listen(0,'127.0.0.1');
  await Promise.all([once(server,'listening'),once(admin,'listening')]);
  const c=`http://127.0.0.1:${server.address().port}`,a=`http://127.0.0.1:${admin.address().port}`;
  const post=(url,body,headers={})=>fetch(url,{method:'POST',headers:{'Content-Type':'application/json',...headers},body:JSON.stringify(body)});
  try {
    assert.equal((await post(c+'/v1/runs',{sessionId:id,run:run(1)})).status,204);
    assert.equal((await post(c+'/v1/runs',{sessionId:id,run:run(1)})).status,204);
    assert.equal((await fetch(a+'/api/run-statistics')).status,401);
    const login=await post(a+'/api/login',{password:'test-long-secret-for-run-statistics'},{Origin:a});
    const cookie=login.headers.get('set-cookie').split(';')[0];
    const data=await fetch(a+'/api/run-statistics?streak_min=1',{headers:{Cookie:cookie}}).then(r=>r.json());
    assert.equal(data.total,1);assert.equal(data.wins,1);
    const old={sessionId:id,name:'player',character:'SILENT',floor:1,encounter:'A',hpLoss:0,version:'old'};
    assert.ok(validate(old));assert.equal((await post(c+'/v1/heartbeat',old)).status,200);
  } finally {server.closeAllConnections();admin.closeAllConnections();await Promise.all([new Promise(r=>server.close(r)),new Promise(r=>admin.close(r))]);app.close();}
});
test('settled runs and historical snapshots survive database restart',()=>{
  const dir=mkdtempSync(join(tmpdir(),'cs-runs-')),file=join(dir,'runs.sqlite');
  let db=new DatabaseSync(file);
  let stats=createRunStatistics(db,()=>10000);
  stats.save(id,run(1));
  stats.import(id,{profileId:profile,capturedAt:500,characters:[{characterId:'SILENT',wins:20,losses:10,currentStreak:3,bestStreak:6}]});
  db.close();db=new DatabaseSync(file);stats=createRunStatistics(db,()=>11000);
  assert.equal(stats.query(parseStatisticsFilters(new URLSearchParams())).wins,1);
  assert.equal(stats.query(parseStatisticsFilters(new URLSearchParams('source=historical'))).entries[0].statistics.currentStreak,null);
  assert.equal(stats.query(parseStatisticsFilters(new URLSearchParams('source=historical&character=SILENT'))).entries[0].statistics.currentStreak,3);
  db.close();rmSync(dir,{recursive:true});
});
