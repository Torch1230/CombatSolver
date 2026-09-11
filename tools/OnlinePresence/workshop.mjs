export const WORKSHOP_ID='3790899961';
export const REFRESH_MS=10*60000;
export function createWorkshopCounter(db,{fetchImpl=fetch,now=Date.now}={}) {
  db.exec('CREATE TABLE IF NOT EXISTS workshop_counts (id TEXT PRIMARY KEY, subscriptions INTEGER NOT NULL, updated_at INTEGER NOT NULL) STRICT');
  let pending=null,lastAttempt=null,failed=false;
  const read=()=>db.prepare('SELECT subscriptions,updated_at FROM workshop_counts WHERE id=?').get(WORKSHOP_ID);
  return {
    snapshot(){const row=read();return {itemId:WORKSHOP_ID,subscriptions:row?.subscriptions??null,updatedAt:row?.updated_at??null,
      stale:!row||failed||now()-row.updated_at>REFRESH_MS*2};},
    refresh(){
      if(pending)return pending;
      const row=read();
      if(lastAttempt!==null && now()-lastAttempt<REFRESH_MS || lastAttempt===null && row && now()-row.updated_at<REFRESH_MS)return Promise.resolve();
      lastAttempt=now();
      pending=(async()=>{
        try {
          const response=await fetchImpl('https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/',{
            method:'POST',body:new URLSearchParams({itemcount:'1','publishedfileids[0]':WORKSHOP_ID}),signal:AbortSignal.timeout(8000)});
          if(!response.ok)throw new Error('Steam HTTP '+response.status);
          const data=await response.json(),item=data.response?.publishedfiledetails?.[0];
          if(item?.result!==1||item.publishedfileid!==WORKSHOP_ID||!Number.isSafeInteger(item.subscriptions)||item.subscriptions<0)throw new Error('Invalid workshop response');
          db.prepare('INSERT INTO workshop_counts VALUES (?,?,?) ON CONFLICT(id) DO UPDATE SET subscriptions=excluded.subscriptions,updated_at=excluded.updated_at').run(WORKSHOP_ID,item.subscriptions,now());
          failed=false;
        } catch(error) {failed=true;console.warn('workshop_refresh_failed',error.name);}
        finally {pending=null;}
      })();return pending;
    },
  };
}
