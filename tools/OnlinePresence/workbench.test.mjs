import {test} from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import {once} from 'node:events';
import {DatabaseSync} from 'node:sqlite';
import {createApp} from './server.mjs';
import {createRunStatistics,parseStatisticsFilters} from './run-statistics.mjs';

test('live sorting is global before pagination and preserves online-time rank',async()=>{
  const password='workbench-test-secret-0123456789',app=createApp({password});
  const collector=http.createServer(app.collector).listen(0,'127.0.0.1'),admin=http.createServer(app.admin).listen(0,'127.0.0.1');
  await Promise.all([once(collector,'listening'),once(admin,'listening')]);
  const c=`http://127.0.0.1:${collector.address().port}`,a=`http://127.0.0.1:${admin.address().port}`;
  const post=(url,body,headers={})=>fetch(url,{method:'POST',headers:{'Content-Type':'application/json',...headers},body:JSON.stringify(body)});
  try{
    for(let n=1;n<=35;n++)assert.equal((await post(c+'/v1/heartbeat',{sessionId:n.toString(16).padStart(32,'0'),name:'player '+n,character:'SILENT',floor:n,encounter:'A',hpLoss:36-n,version:'test'})).status,200);
    assert.equal((await post(c+'/v1/heartbeat',{sessionId:'f'.repeat(32),name:'unknown',character:'',floor:null,encounter:'',hpLoss:null,version:'old'})).status,200);
    const login=await post(a+'/api/login',{password},{Origin:a}),cookie=login.headers.get('set-cookie').split(';')[0];
    const get=path=>fetch(a+path,{headers:{Cookie:cookie}});
    const first=await(await get('/api/players?sort=floor&order=desc')).json();
    assert.equal(first.players[0].floor,35);assert.equal(first.players[0].rank,35);
    const second=await(await get('/api/players?sort=floor&order=desc&page=2')).json();
    assert.deepEqual(second.players.map(p=>p.floor),[5,4,3,2,1,null]);
    const asc=await(await get('/api/players?sort=hpLoss&order=asc')).json();assert.equal(asc.players[0].hpLoss,1);
    const filtered=await(await get('/api/players?q=player%203&sort=floor&order=asc')).json();assert.equal(filtered.players[0].floor,3);assert.equal(filtered.total,7);
    assert.equal((await get('/api/players?sort=invalid')).status,400);assert.equal((await get('/api/players?order=invalid')).status,400);
  }finally{collector.closeAllConnections();admin.closeAllConnections();await Promise.all([new Promise(r=>collector.close(r)),new Promise(r=>admin.close(r))]);app.close();}
});
test('statistics identities, bidirectional sorting and historical unknowns',()=>{
  const db=new DatabaseSync(':memory:'),stats=createRunStatistics(db,()=>10000),installation='a'.repeat(32),profile='b'.repeat(32),otherProfile='c'.repeat(32);
  const run=(n,p,outcome='win')=>({runId:n.toString(16).padStart(32,'0'),profileId:p,startedAt:n*1000,endedAt:n*1000+500,characterId:'SILENT',ascension:10,version:'test',participation:'full',outcome,observedFromStart:true,everEnabled:true,disabledInCombat:false,battles:['1:A'],solvedBattles:[],executedBattles:[],autoBattles:[]});
  stats.save(installation,run(1,profile));stats.save(installation,run(2,profile));stats.save(installation,run(3,otherProfile,'loss'));
  stats.save('d'.repeat(32),run(4,profile));
  const query=q=>stats.query(parseStatisticsFilters(new URLSearchParams(q)));
  assert.equal(query('sessionId='+installation).total,2);
  const exact=query('sessionId='+installation+'&profileId='+profile);assert.equal(exact.total,1);assert.equal(exact.wins,2);assert.equal(exact.losses,0);
  assert.equal(query('sort=streak&order=asc').entries[0].statistics.currentStreak,0);
  assert.equal(query('sort=streak&order=desc').entries[0].statistics.currentStreak,2);
  stats.import(installation,{profileId:profile,capturedAt:10,characters:[{characterId:'SILENT',wins:20,losses:5,currentStreak:0,bestStreak:3}]});
  assert.equal(query('source=historical&sessionId='+installation+'&profileId='+profile).entries[0].statistics.currentStreak,null);
  assert.equal(query('source=historical&character=SILENT&sessionId='+installation+'&profileId='+profile).entries[0].statistics.currentStreak,0);
  assert.throws(()=>parseStatisticsFilters(new URLSearchParams('profileId=bad')));
  assert.throws(()=>parseStatisticsFilters(new URLSearchParams('order=invalid')));
  db.close();
});
