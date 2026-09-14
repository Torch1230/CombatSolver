import {test} from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {createDailyActive,dayStart} from './dau.mjs';
const DAY=86400000,MIN=60000,start=Date.parse('2026-09-01T00:00:00+08:00');
test('DAU deduplicates installations across reconnects, persists across runtime recreation and changes at Beijing midnight',()=>{
  const db=new DatabaseSync(':memory:');let t=start+12*3600000;
  let dau=createDailyActive(db,()=>t);
  dau.record('a');dau.record('a');dau.record('b');dau.sample();
  assert.equal(dau.snapshot().today,2);
  dau=createDailyActive(db,()=>t);dau.record('a');assert.equal(dau.snapshot().today,2);
  assert.equal(dau.snapshot().series.at(-1).complete,false);
  assert.equal(dau.snapshot().series.at(-2).count,null);
  assert.equal(dau.snapshot().comparisons.previousDay.ready,false);
  t=start+DAY;assert.equal(dayStart(t),t);dau.record('a');dau.sample();
  assert.equal(dau.snapshot().today,1);assert.equal(dau.snapshot().series.at(-2).count,2);db.close();
});
test('DAU compares the same completed minutes, detects gaps, and retains 90 days',()=>{
  const db=new DatabaseSync(':memory:');let t=start;
  const dau=createDailyActive(db,()=>t),insert=db.prepare('INSERT INTO dau_coverage VALUES(?)');
  for(let m=0;m<8*1440+61;m++)insert.run(start+m*MIN);
  dau.record('old-a',start+DAY+MIN);dau.record('old-b',start+DAY+90*MIN);
  dau.record('yesterday',start+7*DAY+MIN);
  t=start+8*DAY+60*MIN+1000;dau.record('a',t-2*MIN);dau.record('b',t-MIN);dau.record('live',t);
  const snapshot=dau.snapshot();assert.equal(snapshot.today,3);
  for(const key of ['previousDay','previousWeek']) {
    assert.deepEqual(snapshot.comparisons[key],{ready:true,current:2,baseline:1,delta:1,percent:100});
  }
  db.prepare('DELETE FROM dau_coverage WHERE minute=?').run(start+7*DAY+10*MIN);
  assert.equal(dau.snapshot().comparisons.previousDay.ready,false);
  db.prepare('DELETE FROM daily_active WHERE day=?').run(start+DAY);
  assert.deepEqual(dau.snapshot().comparisons.previousWeek,{ready:true,current:2,baseline:0,delta:2,percent:null});
  t=start+100*DAY;dau.sample();assert.equal(db.prepare('SELECT COUNT(*) AS n FROM daily_active').get().n,0);
  assert.equal(dau.snapshot().series.length,90);db.close();
});
