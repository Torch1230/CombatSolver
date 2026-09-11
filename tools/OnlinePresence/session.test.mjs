import {test} from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import {once} from 'node:events';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {createApp} from './server.mjs';

test('dedicated durable session survives restart, expires, revokes and follows password changes',async()=>{
  const dir=mkdtempSync(join(tmpdir(),'presence-session-'));
  const password='session-fixture-password-0123456789';
  let time=1_800_000_000_000,app,server,base;
  async function start(secret=password) {
    app=createApp({database:join(dir,'data.sqlite'),password:secret,now:()=>time,secureCookie:true});
    server=http.createServer(app.admin).listen(0,'127.0.0.1');
    await once(server,'listening'); base=`http://127.0.0.1:${server.address().port}`;
  }
  async function stop(){server.closeAllConnections();await new Promise(r=>server.close(r));app.close();}
  const get=cookie=>fetch(base+'/api/overview',{headers:{Cookie:cookie}});
  async function login(){
    const result=await fetch(base+'/api/login',{method:'POST',headers:{Origin:base,'Content-Type':'application/json'},body:JSON.stringify({password})});
    assert.equal(result.status,200);
    const cookie=result.headers.get('set-cookie');
    assert.match(cookie,/^cs_presence_session=/); assert.match(cookie,/Max-Age=1209600/);
    assert.match(cookie,/HttpOnly/); assert.match(cookie,/Secure/);
    return cookie.split(';')[0];
  }
  await start();
  try {
    const cookie=await login();
    assert.equal((await get('session=log-site-cookie; '+cookie)).status,200);
    await stop(); await start();
    assert.equal((await get(cookie)).status,200);
    time+=9*3600000;
    assert.equal((await get(cookie)).status,200);
    assert.equal((await fetch(base+'/api/logout',{method:'POST',headers:{Origin:base,Cookie:cookie}})).status,204);
    assert.equal((await get(cookie)).status,401);
    const expiring=await login(); time+=14*86400000;
    assert.equal((await get(expiring)).status,401);
    app.expire();
    const rotated=await login(); await stop(); await start(password+'changed');
    assert.equal((await get(rotated)).status,401);
  } finally {await stop();rmSync(dir,{recursive:true});}
});

// Authentication rendering, request races and user interactions are exercised in browser.test.mjs
// against the real DOM rather than a hand-written subset of browser APIs.
