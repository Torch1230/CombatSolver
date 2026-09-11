const MINUTE = 60000;
const WEEK = 7 * 86400000;

// Compare matching observed minutes, never interpret a missing sample as zero.
export function comparePeriods(rows, now, hours) {
  const end = Math.floor(now / MINUTE) * MINUTE;
  const duration = hours * 3600000, start = end - duration;
  const counts = new Map(rows.map(row => [row.time, row.count]));
  let sum = 0, samples = 0;
  for (let t = start; t < end; t += MINUTE) {
    if (counts.has(t)) { sum += counts.get(t); samples++; }
  }
  function comparison(shift, window = duration) {
    const windowStart = end - window;
    let current = 0, previous = 0, paired = 0;
    for (let t = windowStart; t < end; t += MINUTE) {
      if (counts.has(t) && counts.has(t - shift)) {
        current += counts.get(t); previous += counts.get(t - shift); paired++;
      }
    }
    return {start:windowStart-shift,end:end-shift,pairedMinutes:paired,expectedMinutes:window/MINUTE,
      coverage:paired/(window/MINUTE),currentAverage:paired?current/paired:null,
      previousAverage:paired?previous/paired:null,delta:paired?(current-previous)/paired:null,
      percent:paired && previous>0?(current-previous)/previous*100:null,
      status:paired===0?'missing':previous===0?'zero_baseline':paired<window/MINUTE?'partial':'complete'};
  }
  return {start,end,average:samples?sum/samples:null,sampledMinutes:samples,expectedMinutes:duration/MINUTE,
    previousPeriod:comparison(3600000,3600000),previousDay:comparison(86400000),previousWeek:comparison(WEEK)};
}
