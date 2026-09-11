import {test} from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import {once} from 'node:events';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {createApp} from './server.mjs';

test('admin release persists, validates, and reaches existing heartbeat clients', async()=>{
  const dir=mkdtempSync(join(tmpdir(),'cs-release-'));
  const database=join(dir,'presence.sqlite'),password='fixture-release-password-123456';
  const payload={sessionId:'c'.repeat(32),name:'fixture',character:'',floor:null,encounter:'',hpLoss:null,version:'0.35.2'};
  async function start() {
    const app=createApp({database,password});
    const collector=http.createServer(app.collector).listen(0,'127.0.0.1');
    const admin=http.createServer(app.admin).listen(0,'127.0.0.1');
    await Promise.all([once(collector,'listening'),once(admin,'listening')]);
    const c=`http://127.0.0.1:${collector.address().port}`,a=`http://127.0.0.1:${admin.address().port}`;
    return {app,collector,admin,c,a};
  }
  async function close(s) {s.collector.closeAllConnections();s.admin.closeAllConnections();await Promise.all([new Promise(r=>s.collector.close(r)),new Promise(r=>s.admin.close(r))]);s.app.close();}
  const post=(url,body,headers={})=>fetch(url,{method:'POST',headers:{'Content-Type':'application/json',...headers},body:JSON.stringify(body)});
  let s=await start();
  try {
    assert.equal((await fetch(s.c+'/api/release')).status,404);
    assert.equal((await post(s.c+'/api/release',{latestVersion:'9.0.0'})).status,404);
    assert.equal((await fetch(s.a+'/api/release')).status,401);
    assert.equal((await post(s.a+'/api/release',{latestVersion:'9.0.0'},{Origin:s.a})).status,401);
    assert.deepEqual(await (await post(s.c+'/v1/heartbeat',payload)).json(),{latestVersion:null});
    const login=await post(s.a+'/api/login',{password},{Origin:s.a});
    const cookie=login.headers.get('set-cookie').split(';')[0],headers={Cookie:cookie,Origin:s.a};
    assert.equal((await post(s.a+'/api/release',{latestVersion:'9.0.0'},{Cookie:cookie})).status,403);
    for (const body of [{},{latestVersion:4},{latestVersion:'1.0'},{latestVersion:'01.0.0'},{latestVersion:'1.0.0-beta'},{latestVersion:'2147483648.0.0'},{latestVersion:'0.35.3',extra:1}])
      assert.equal((await post(s.a+'/api/release',body,headers)).status,400);
    assert.deepEqual(await (await post(s.a+'/api/release',{latestVersion:'0.35.3'},headers)).json(),{latestVersion:'0.35.3'});
    assert.deepEqual(await (await post(s.c+'/v1/heartbeat',payload)).json(),{latestVersion:'0.35.3'});
    await close(s); s=await start();
    assert.deepEqual(await (await fetch(s.a+'/api/release',{headers:{Cookie:cookie}})).json(),{latestVersion:'0.35.3'});
    assert.deepEqual(await (await post(s.c+'/v1/heartbeat',payload)).json(),{latestVersion:'0.35.3'});
    assert.equal((await post(s.a+'/api/release',{latestVersion:null},{Cookie:cookie,Origin:s.a})).status,200);
    assert.deepEqual(await (await post(s.c+'/v1/heartbeat',payload)).json(),{latestVersion:null});
  } finally {await close(s);rmSync(dir,{recursive:true});}
});
