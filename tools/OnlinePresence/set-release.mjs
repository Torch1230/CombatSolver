// Run on the monitoring host with: node --env-file=private/service.env set-release.mjs 0.35.3
import https from 'node:https';
import {checkServerIdentity} from 'node:tls';
import {readFileSync} from 'node:fs';

const version=process.argv[2];
if (version !== 'off' && !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version || '')) throw new Error('Pass a release version or off');
const tlsEnabled=process.env.ADMIN_TLS==='true';
if (!tlsEnabled || !process.env.ADMIN_PUBLIC_HOST) throw new Error('This deployment tool requires the configured HTTPS admin listener');
const origin=`https://${process.env.ADMIN_PUBLIC_HOST}:${process.env.ADMIN_PORT || 12889}`;
let cookie;
function request(path,body) {
  return new Promise((resolve,reject)=>{
    const req=https.request({hostname:'127.0.0.1',port:Number(process.env.ADMIN_PORT || 12889),path,method:'POST',
      ca:readFileSync(process.env.TLS_CERT),checkServerIdentity:(_,cert)=>checkServerIdentity(process.env.ADMIN_PUBLIC_HOST,cert),
      headers:{Host:new URL(origin).host,Origin:origin,'Content-Type':'application/json',...(cookie?{Cookie:cookie}:{})},timeout:8000},res=>{
        const chunks=[];
        res.on('data',chunk=>chunks.push(chunk));res.on('error',reject);
        res.on('end',()=>{
          if(res.statusCode<200 || res.statusCode>=300)return reject(new Error(`Admin HTTP ${res.statusCode}`));
          resolve({headers:res.headers,body:Buffer.concat(chunks).toString('utf8')});
        });
      });
    req.on('error',reject);req.on('timeout',()=>req.destroy(new Error('Admin request timed out')));
    req.end(JSON.stringify(body));
  });
}
const login=await request('/api/login',{password:process.env.ADMIN_PASSWORD});
cookie=login.headers['set-cookie'][0].split(';')[0];
try {
  const response=await request('/api/release',{latestVersion:version==='off'?null:version});
  console.log(response.body);
} finally {await request('/api/logout',{});}
