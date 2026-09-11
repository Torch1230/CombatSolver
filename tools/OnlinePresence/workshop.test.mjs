import {test} from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {createWorkshopCounter,WORKSHOP_ID,REFRESH_MS} from './workshop.mjs';
test('workshop current subscriptions cached, coalesced and retained on failure',async()=>{
  const db=new DatabaseSync(':memory:');let time=100000,calls=0,fail=false;
  const fetchImpl=async()=>{calls++;if(fail)throw new Error('offline');return {ok:true,json:async()=>({response:{publishedfiledetails:[{publishedfileid:WORKSHOP_ID,result:1,subscriptions:123,lifetime_subscriptions:999}]}})};};
  const counter=createWorkshopCounter(db,{now:()=>time,fetchImpl});
  assert.equal(counter.snapshot().subscriptions,null);
  await Promise.all([counter.refresh(),counter.refresh()]);assert.equal(calls,1);assert.equal(counter.snapshot().subscriptions,123);
  await counter.refresh();assert.equal(calls,1);
  const restarted=createWorkshopCounter(db,{now:()=>time,fetchImpl});await restarted.refresh();assert.equal(calls,1);
  time+=REFRESH_MS;fail=true;await counter.refresh();assert.equal(counter.snapshot().subscriptions,123);assert.equal(counter.snapshot().stale,true);
  db.close();
});
