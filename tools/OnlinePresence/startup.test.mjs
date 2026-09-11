import {test} from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import http from 'node:http';
import {once} from 'node:events';
import {createApp,TTL} from './server.mjs';

test('startup skips incomplete rosters for a full TTL, preserves the previous bucket and later records real zero',async()=>{
  const dir=mkdtempSync(join(tmpdir(),'presence-startup-')),database=join(dir,'presence.sqlite');
  let time=1800000020000;
  const db=new DatabaseSync(database),previousBucket=Math.floor(time/60000)*60000;
  db.exec('CREATE TABLE history(time INTEGER PRIMARY KEY,count INTEGER NOT NULL) STRICT');db.prepare('INSERT INTO history VALUES (?,400)').run(previousBucket);
  const password='startup-test-password-0123456789',app=createApp({database,password,now:()=>time});
  const collector=http.createServer(app.collector).listen(0,'127.0.0.1'),admin=http.createServer(app.admin).listen(0,'127.0.0.1');
  await Promise.all([once(collector,'listening'),once(admin,'listening')]);
  const c=`http://127.0.0.1:${collector.address().port}`,a=`http://127.0.0.1:${admin.address().port}`;
  const post=(url,body,headers={})=>fetch(url,{method:'POST',headers:{'Content-Type':'application/json',...headers},body:JSON.stringify(body)});
  let reboot;
  try{
    app.sample();assert.deepEqual(db.prepare('SELECT * FROM history').all().map(r=>({...r})),[{time:previousBucket,count:400}]);
    const login=await post(a+'/api/login',{password},{Origin:a}),cookie=login.headers.get('set-cookie').split(';')[0];
    const overview=()=>fetch(a+'/api/overview',{headers:{Cookie:cookie}}).then(r=>r.json());
    const initial=await overview();assert.equal(initial.samplingReady,false);assert.equal(initial.samplingReadyAt,time+TTL);
    time+=60000;
    assert.equal((await post(c+'/v1/heartbeat',{sessionId:'a'.repeat(32),name:'test',character:'SILENT',floor:1,encounter:'A',hpLoss:0,version:'test'})).status,200);
    app.sample();assert.equal(db.prepare('SELECT COUNT(*) AS n FROM history').get().n,1);
    time+=TTL-60000-1;app.sample();assert.equal(db.prepare('SELECT COUNT(*) AS n FROM history').get().n,1);
    time++;app.sample();assert.equal((await overview()).samplingReady,true);
    const recoveredBucket=Math.floor(time/60000)*60000;
    assert.equal(db.prepare('SELECT count FROM history WHERE time=?').get(recoveredBucket).count,1);
    collector.closeAllConnections();admin.closeAllConnections();await Promise.all([new Promise(r=>collector.close(r)),new Promise(r=>admin.close(r))]);app.close();
    reboot=createApp({database,password,now:()=>time});reboot.sample();
    assert.equal(db.prepare('SELECT count FROM history WHERE time=?').get(recoveredBucket).count,1);
    time+=TTL-1;reboot.sample();assert.equal(db.prepare('SELECT COUNT(*) AS n FROM history').get().n,2);
    time++;reboot.sample();assert.equal(db.prepare('SELECT count FROM history ORDER BY time DESC LIMIT 1').get().count,0);
  }finally{
    if(collector.listening){collector.closeAllConnections();admin.closeAllConnections();await Promise.all([new Promise(r=>collector.close(r)),new Promise(r=>admin.close(r))]);app.close();}
    reboot?.close();db.close();rmSync(dir,{recursive:true});
  }
});
