import {test} from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import {once} from 'node:events';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {createApp,validate,TTL} from './server.mjs';
const password='fixture-password-only-0123456789';
const payload={sessionId:'a'.repeat(32),name:'测试玩家 <script>',character:'铁甲战士',floor:12,encounter:'测试战斗',hpLoss:0,version:'test'};
test('strict payload: rejects extra fields, invalid loss, oversized strings',()=>{
  assert.ok(validate(payload));assert.ok(validate({...payload,hpLoss:null}));
  assert.ok(validate({...payload,inCombat:false,battleUpdatedAt:123}));
  assert.equal(validate({...payload,inCombat:'false'}),false);
  assert.equal(validate({...payload,inRun:'true'}),false);
  assert.ok(validate({...payload,inRun:true}));
  assert.equal(validate({...payload,battleUpdatedAt:-1}),false);
  for(const p of [{...payload,route:[]},{...payload,hpLoss:-1},{...payload,hpLoss:'0'},{...payload,floor:1.1},{...payload,name:'x'.repeat(129)},{...payload,sessionId:'invalid'}])assert.equal(validate(p),false);
});
test('collector privacy, login, deduplication, expiry and durable aggregate history',async()=>{
  let time=1_800_000_000_000;const dir=mkdtempSync(join(tmpdir(),'cs-presence-'));const database=join(dir,'history.sqlite');
  const app=createApp({database,password,now:()=>time});
  time+=Math.ceil(TTL/60000)*60000; // Begin steady-state sampling on a minute boundary after recovery.
  const collector=http.createServer(app.collector).listen(0,'127.0.0.1');
  const admin=http.createServer(app.admin).listen(0,'127.0.0.1');
  await Promise.all([once(collector,'listening'),once(admin,'listening')]);
  const c=`http://127.0.0.1:${collector.address().port}`,a=`http://127.0.0.1:${admin.address().port}`;
  const post=(url,body,headers={})=>fetch(url,{method:'POST',headers:{'Content-Type':'application/json',...headers},body:JSON.stringify(body)});
  try{
    assert.equal((await fetch(c+'/api/overview')).status,404);
    assert.equal((await fetch(a+'/api/overview')).status,401);
    assert.equal((await post(a+'/api/login',{password})).status,403);
    const login=await post(a+'/api/login',{password},{Origin:a});assert.equal(login.status,200);const cookie=login.headers.get('set-cookie').split(';')[0];
    const overview=()=>fetch(a+'/api/overview',{headers:{Cookie:cookie}}).then(r=>r.json());
    const playerPage=()=>fetch(a+'/api/players',{headers:{Cookie:cookie}}).then(r=>r.json());
    assert.equal((await post(c+'/v1/heartbeat',{...payload,route:[]})).status,400);
    assert.equal((await post(c+'/v1/heartbeat',payload)).status,200);
    assert.equal((await post(c+'/v1/heartbeat',{...payload,hpLoss:7})).status,200);
    app.sample();const current=await overview();assert.equal(current.onlineCount,1);assert.equal((await playerPage()).players[0].hpLoss,7);assert.equal(current.history.at(-1).count,1);assert.ok(!('players' in current));
    time+=TTL+1;app.sample();assert.equal((await playerPage()).players.length,0);assert.equal((await overview()).history.at(-1).count,0.5);
    assert.equal((await fetch(a+'/api/logout',{method:'POST',headers:{Cookie:cookie,Origin:a}})).status,204);
    assert.equal((await fetch(a+'/api/overview',{headers:{Cookie:cookie}})).status,401);
  }finally{collector.closeAllConnections();admin.closeAllConnections();await Promise.all([new Promise(r=>collector.close(r)),new Promise(r=>admin.close(r))]);app.close();}
  const restored=createApp({database,password,now:()=>time});
  const reopened=http.createServer(restored.admin).listen(0,'127.0.0.1');await once(reopened,'listening');
  const reopenedUrl=`http://127.0.0.1:${reopened.address().port}`;
  try {
    const login=await post(reopenedUrl+'/api/login',{password},{Origin:reopenedUrl});
    const cookie=login.headers.get('set-cookie').split(';')[0];
    const data=await fetch(reopenedUrl+'/api/overview',{headers:{Cookie:cookie}}).then(r=>r.json());
    assert.equal(data.onlineCount,0);assert.equal(data.historyPeak,1);assert.equal(data.history[0].count,0.5);assert.equal(data.history[0].samples,2);
  } finally { reopened.closeAllConnections();await new Promise(r=>reopened.close(r));restored.close();rmSync(dir,{recursive:true}); }
});
test('heartbeat per-identity throttling',async()=>{
  const app=createApp({password});const server=http.createServer(app.collector).listen(0,'127.0.0.1');await once(server,'listening');
  try{for(let i=0;i<7;i++){const r=await fetch(`http://127.0.0.1:${server.address().port}/v1/heartbeat`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});assert.equal(r.status,i<6?200:429);}}
  finally{server.closeAllConnections();await new Promise(r=>server.close(r));app.close();}
});

test('retains complete battles atomically through idle and pending results, with accurate presence',async()=>{
  let time=1_800_000_000_000;
  const app=createApp({password,now:()=>time});
  const collector=http.createServer(app.collector).listen(0,'127.0.0.1');
  const admin=http.createServer(app.admin).listen(0,'127.0.0.1');
  await Promise.all([once(collector,'listening'),once(admin,'listening')]);
  const c=`http://127.0.0.1:${collector.address().port}`,a=`http://127.0.0.1:${admin.address().port}`;
  const post=(url,body,headers={})=>fetch(url,{method:'POST',headers:{'Content-Type':'application/json',...headers},body:JSON.stringify(body)});
  try {
    const login=await post(a+'/api/login',{password},{Origin:a});
    const cookie=login.headers.get('set-cookie').split(';')[0];
    const get=path=>fetch(a+path,{headers:{Cookie:cookie}}).then(r=>r.json());
    const send=async body=>{time+=30000;assert.equal((await post(c+'/v1/heartbeat',body)).status,200);return (await get('/api/players')).players[0];};
    const idle={...payload,character:'',floor:null,encounter:'',hpLoss:null};
    assert.equal((await send(idle)).hpLoss,null);
    const first=await send(payload);
    const cached=await send(idle);
    for(const key of ['character','floor','encounter','hpLoss','battleUpdatedAt']) assert.equal(cached[key],first[key]);
    assert.equal((await get('/api/overview')).fightingCount,0);
    assert.equal(cached.onlineSeconds,60);
    const next={...payload,character:'下一角色',floor:2,encounter:'下一战斗',hpLoss:null};
    const pending=await send(next);
    assert.equal(pending.encounter,payload.encounter);assert.equal(pending.hpLoss,0);
    assert.equal((await get('/api/overview')).fightingCount,1);
    const completed=await send({...next,hpLoss:5,inCombat:true,battleUpdatedAt:time});
    assert.equal(completed.encounter,next.encounter);assert.equal(completed.hpLoss,5);assert.equal(completed.character,next.character);assert.equal(completed.floor,2);
    const cachedClient=await send({...next,hpLoss:5,inCombat:false,battleUpdatedAt:completed.battleUpdatedAt});
    assert.equal(cachedClient.battleUpdatedAt,completed.battleUpdatedAt);
    assert.equal((await get('/api/overview')).fightingCount,0);
    time+=TTL+1;
    await send({...next,hpLoss:5,inCombat:false,inRun:true});
    assert.equal((await get('/api/overview')).inRunCount,1);
    await send({...next,hpLoss:5,inCombat:false,inRun:false});
    assert.equal((await get('/api/overview')).inRunCount,0);
    await send({...next,hpLoss:5,inCombat:false});
    assert.equal((await get('/api/overview')).runStatusUnknownCount,1);
    time+=TTL+1;
    assert.equal((await get('/api/players')).total,0);
    assert.equal((await send(idle)).hpLoss,null);
  } finally {
    collector.closeAllConnections();admin.closeAllConnections();
    await Promise.all([new Promise(r=>collector.close(r)),new Promise(r=>admin.close(r))]);app.close();
  }
});
