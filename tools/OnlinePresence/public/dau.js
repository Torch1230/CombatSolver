(() => {
  const panel=document.getElementById('dau-panel');
  if(!panel)return;
  const el=id=>document.getElementById(id);
  let data=null,chart;
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
    if(panel.open) {
      const points=rows.map(r=>({...r,x:Date.parse(r.date+'T00:00:00+08:00'),y:r.count}));
      const radius=points.filter(p=>p.y!==null).length===1?3:0;
      if(!chart)chart=new Chart(el('dau-chart'),{
        type:'line',
        data:{datasets:[{label:'日活人数',data:points,borderColor:'#237c62',backgroundColor:'#237c6210',fill:true,borderWidth:2,pointRadius:radius,spanGaps:false,cubicInterpolationMode:'monotone'}]},
        options:{animation:false,maintainAspectRatio:false,parsing:false,
          interaction:{mode:'nearest',axis:'x',intersect:false},
          plugins:{legend:{display:false},tooltip:{callbacks:{
            title:items=>items[0].raw.date,
            label:item=>`日活：${number(item.parsed.y)} 人${item.raw.ongoing?'（今日累计）':''}`,
            afterLabel:item=>item.raw.complete?'完整采集':'采集不完整',
          }}},
          scales:{x:{type:'linear',grid:{display:false},ticks:{maxTicksLimit:6,callback:value=>new Date(value).toLocaleDateString('zh-CN',{timeZone:'Asia/Shanghai',month:'numeric',day:'numeric'})}},
            y:{beginAtZero:true,suggestedMax:5,ticks:{precision:0},grid:{color:'#edf1ee'}}},
        },
      });
      chart.data.datasets[0].data=points;
      chart.data.datasets[0].pointRadius=radius;
      chart.options.scales.x.min=points[0].x;
      chart.options.scales.x.max=points.at(-1).x;
      chart.update();
    }
    el('dau-table').replaceChildren();
    for(const r of [...rows].reverse()) {
      const tr=document.createElement('tr');
      for(const text of [r.date,number(r.count),r.count===null?'未采集':r.ongoing?(r.complete?'今日累计':'今日累计 · 采集不完整'):r.complete?'完整':'采集不完整']){const td=document.createElement('td');td.textContent=text;tr.append(td);}
      el('dau-table').append(tr);
    }
  }
  window.renderDailyActive = snapshot => {data=snapshot;render();};
  el('dau-range').addEventListener('change',render);
  panel.addEventListener('toggle',()=>{if(panel.open)render();});
})();
