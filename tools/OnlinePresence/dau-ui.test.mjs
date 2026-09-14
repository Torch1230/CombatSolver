import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

test('DAU chart follows online styling, opens lazily, retains gaps and reuses its chart',()=>{
  const nodes=new Map();
  const node=()=>({textContent:'',children:[],events:{},append(child){this.children.push(child);},replaceChildren(){this.children=[];},addEventListener(name,fn){this.events[name]=fn;}});
  const get=id=>{if(!nodes.has(id))nodes.set(id,node());return nodes.get(id);};
  get('dau-range').value='7';get('dau-panel').open=false;
  const charts=[];
  class Chart {constructor(canvas,config){Object.assign(this,config);this.updates=0;charts.push(this);}update(){this.updates++;}}
  const context={document:{getElementById:get,createElement:node},window:{},Chart};
  vm.runInNewContext(readFileSync(new URL('./public/dau.js',import.meta.url),'utf8'),context);
  const snapshot={today:12,comparisonThrough:0,comparisons:{previousDay:{ready:false},previousWeek:{ready:false}},series:[{date:'2026-09-13',count:null,complete:false},{date:'2026-09-14',count:12,complete:false,ongoing:true}]};
  context.window.renderDailyActive(snapshot);assert.equal(charts.length,0);
  get('dau-panel').open=true;get('dau-panel').events.toggle();
  assert.equal(charts.length,1);const chart=charts[0],dataset=chart.data.datasets[0];
  assert.equal(dataset.borderColor,'#237c62');assert.equal(dataset.fill,true);
  assert.equal(dataset.pointRadius,3);assert.equal(dataset.data[0].y,null);
  assert.equal(dataset.spanGaps,false);assert.equal(chart.options.maintainAspectRatio,false);
  assert.equal(chart.options.plugins.tooltip.callbacks.afterLabel({raw:{complete:false}}),'采集不完整');
  context.window.renderDailyActive({...snapshot,today:13});assert.equal(charts.length,1);assert.equal(chart.updates,2);
});
