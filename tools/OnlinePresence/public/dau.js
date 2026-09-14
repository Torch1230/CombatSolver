(() => {
  const panel=document.getElementById('dau-panel');
  if(!panel)return;
  const el=id=>document.getElementById(id);
  let data=null;
  const number=value=>value===null?'—':value.toLocaleString('zh-CN');
  function comparison(value) {
    if(!value.ready)return '数据不足（缺少完整同期采集）';
    const delta=(value.delta>0?'+':'')+number(value.delta)+' 人';
    return delta+' · '+(value.percent===null?'基期为 0，无百分比':(value.percent>0?'+':'')+value.percent.toFixed(1)+'%');
  }
  function render() {
    if(!data)return;
    el('dau-today').textContent=number(data.today);
    el('dau-day').textContent=comparison(data.comparisons.previousDay);
    el('dau-week').textContent=comparison(data.comparisons.previousWeek);
    el('dau-status').textContent=(data.stale?'采集更新已延迟。':'')+'北京时间每日去重安装数；仅统计开启在线统计的玩家。今天为截至当前累计值，比较截至 '+new Date(data.comparisonThrough).toLocaleTimeString('zh-CN',{timeZone:'Asia/Shanghai',hour12:false})+'，与昨天 / 上周同一时刻对齐。';
    const rows=data.series.slice(-Number(el('dau-range').value));
    const svg=el('dau-chart');svg.replaceChildren();
    const ns='http://www.w3.org/2000/svg';
    function add(tag,attrs,text) {const node=document.createElementNS(ns,tag);for(const [k,v] of Object.entries(attrs))node.setAttribute(k,v);if(text!==undefined)node.textContent=text;svg.append(node);return node;}
    const max=Math.max(1,...rows.map(r=>r.count||0)),x=i=>50+i*900/(rows.length-1),y=n=>180-n/max*150;
    for(let i=0;i<=3;i++){const v=Math.round(max*i/3),py=y(v);add('line',{x1:50,x2:950,y1:py,y2:py,stroke:'#303946'});add('text',{x:42,y:py+4,fill:'#8b949e','text-anchor':'end','font-size':12},number(v));}
    let previous=null;
    rows.forEach((r,i)=>{
      if(r.count===null){previous=null;return;}
      const p={x:x(i),y:y(r.count)};
      if(previous){const mid=(p.x+previous.x)/2;add('path',{d:`M ${previous.x} ${previous.y} C ${mid} ${previous.y}, ${mid} ${p.y}, ${p.x} ${p.y}`,fill:'none',stroke:'#58a6ff','stroke-width':2});}
      const dot=add('circle',{cx:p.x,cy:p.y,r:3.5,fill:r.complete?'#58a6ff':'#e3b341'});
      const title=document.createElementNS(ns,'title');title.textContent=`${r.date}：${number(r.count)} 人${r.ongoing?'（今日累计）':''}${r.complete?'':'（采集不完整）'}`;dot.append(title);previous=p;
    });
    [0,Math.floor(rows.length/2),rows.length-1].forEach(i=>add('text',{x:x(i),y:208,fill:'#8b949e','text-anchor':'middle','font-size':12},rows[i].date));
    el('dau-table').replaceChildren();
    for(const r of [...rows].reverse()) {
      const tr=document.createElement('tr');
      for(const text of [r.date,number(r.count),r.count===null?'未采集':r.ongoing?(r.complete?'今日累计':'今日累计 · 采集不完整'):r.complete?'完整':'采集不完整']){const td=document.createElement('td');td.textContent=text;tr.append(td);}
      el('dau-table').append(tr);
    }
  }
  window.renderDailyActive = snapshot => {data=snapshot;render();};
  el('dau-range').addEventListener('change',render);
})();
