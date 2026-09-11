import {test} from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import {once} from 'node:events';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {createApp,TTL} from './server.mjs';

test('30-row global ranking, remote search, reconnect accumulation and restart persistence',async()=>{
  const password='pagination-test-password-0123456789';
  const directory=mkdtempSync(join(tmpdir(),'cs-ranking-'));
  const database=join(directory,'presence.sqlite');
  const start=1_800_000_000_000;
  let time=start;
  let app,admin,collector,origin,collectorOrigin,cookie;
  async function boot(){
    app=createApp({database,password,now:()=>time});
    admin=http.createServer(app.admin).listen(0,'127.0.0.1');
    collector=http.createServer(app.collector).listen(0,'127.0.0.1');
    await Promise.all([once(admin,'listening'),once(collector,'listening')]);
    origin=`http://127.0.0.1:${admin.address().port}`;
    collectorOrigin=`http://127.0.0.1:${collector.address().port}`;
    const login=await fetch(origin+'/api/login',{method:'POST',headers:{Origin:origin,'Content-Type':'application/json'},body:JSON.stringify({password})});
    assert.equal(login.status,200);cookie=login.headers.get('set-cookie').split(';')[0];
  }
  async function shutdown(){
    admin.closeAllConnections();collector.closeAllConnections();
    await Promise.all([new Promise(r=>admin.close(r)),new Promise(r=>collector.close(r))]);app.close();
  }
  async function heartbeat(index){
    const data={sessionId:(index+1).toString(16).padStart(32,'0'),name:`玩家 ${index+1}`,character:'铁甲战士',floor:12,encounter:index%2?'':'测试战斗',hpLoss:0,version:'test'};
    const result=await fetch(collectorOrigin+'/v1/heartbeat',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});
    assert.equal(result.status,200);
  }
  const get=path=>fetch(origin+path,{headers:{Cookie:cookie}});
  const page=(query='')=>get('/api/players'+query).then(r=>r.json());
  await boot();
  try {
    assert.equal((await fetch(origin+'/api/players')).status,401);
    for(let i=0;i<62;i++){time=start+i*100;await heartbeat(i);}
    time=start+30000;
    for(let i=61;i>=0;i--)await heartbeat(i);
    const first=await page();const second=await page('?page=2');const third=await page('?page=3');
    assert.equal(first.pageSize,30);assert.equal(first.total,62);assert.equal(first.totalPages,3);
    assert.deepEqual([first.players.length,second.players.length,third.players.length],[30,30,2]);
    assert.deepEqual([...first.players,...second.players,...third.players].map(p=>p.rank),Array.from({length:62},(_,i)=>i+1));
    assert.equal(first.players[0].onlineSeconds,30);assert.equal(third.players[1].name,'玩家 62');
    assert.equal((await page('?page=1&pageSize=1000')).players.length,30);
    const found=await page('?q='+encodeURIComponent('玩家 62'));assert.equal(found.total,1);assert.equal(found.players[0].rank,62);
    assert.equal((await page('?page=999')).page,3);
    assert.equal((await page('?page=999&q=absent')).page,1);
    for(const value of ['0','-1','1.5','NaN','9007199254740992'])assert.equal((await get('/api/players?page='+value)).status,400);
    const overview=await get('/api/overview').then(r=>r.json());assert.equal(overview.onlineCount,62);assert.equal(overview.fightingCount,31);assert.ok(!('players' in overview));
    time+=TTL;await heartbeat(0);assert.equal((await page()).players[0].onlineSeconds,30);
    time+=10000;await heartbeat(0);assert.equal((await page()).players[0].onlineSeconds,40);
    await shutdown();time+=20000;await boot();
    assert.equal((await page()).total,0);
    await heartbeat(0);assert.equal((await page()).players[0].onlineSeconds,60);
  } finally {await shutdown();rmSync(directory,{recursive:true});}
});
