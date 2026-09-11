# 跑局战绩

版本 0.34.7 起独立记录单人标准跑局。历史快照来自当前档案的原生角色累计值，在首次进入受支持跑局时导入一次；不把这些旧成绩标为求解器成绩。首版不跨安装合并身份。数据是客户端自报，不是认证排行榜。

## 统计口径

- 全程开启：从开局观察，且已观察的战斗期间未关闭求解器。只在地图关闭再于战前开启不影响战斗覆盖。
- 部分参与：从中途加入、战斗期间关闭过，或续档时原生战斗数量超过已观察数量。
- 未参与：已观察区间内未开启。求解、实际执行及实际全自动执行按战斗分别记录；开启本身不代表求解器完成操作。
- 通关计胜；死亡、主动放弃计负，放弃另有计数。未结束跑局保持 pending，不因退出或断线判负。
- 默认按全程开启计算胜负与连胜。当前 pending 不改变上局连胜，较早未结算跑局或不匹配参与条件的跑局打断连续段。筛选角色、版本、时间或实际使用后，不跨被排除的跑局拼接连胜。
- 胜率为胜场 /（胜场＋负场）；无结算为 null。后台全站百分比用胜负总和计算，不平均玩家百分比。
- 游戏历史快照只能按角色与累计值筛选，不支持版本、进阶、日期或参与覆盖；跨角色当前/最高连胜显示未知，不能相加。

统计可证明的是已观察区间。删除本地记录、换安装或原生历史已被清理会造成缺口；无法推断 Mod 未运行期间的全部行为，也不用于宣称防作弊。

## 生命周期与传输

RunManager 的新局/Launch 与 SaveManager.SaveRunHistory 补丁只采样主线程标量。战斗边界、求解入口、实际动作完成和设置变化产生不可变事件；Activity 同一战斗去重。Runtime 的 256 条有界队列交给独立 worker，Search 不做战绩 I/O，也不改指纹或路线缓存。

本地 `%LOCALAPPDATA%/CombatSolver/run-statistics-v1` 按跑局写小型原子文件；保留轻量历史，正文与战斗路线不写入。上传收据独立保存，启动重新加载未确认事件；每轮最多补传 20 条，每 30 秒调度。单个请求最多 8 秒，单 worker 保证没有同类并行请求。关闭在线统计会取消在途战绩上传；本地记录继续，重新开启可补传。

续档沿用档案＋原生开局时间＋种子派生的匿名跑局 ID。首次初始化安装标识使用共享 Lazy，在线心跳与战绩使用同一个标识。worker 只读取自己已跟踪跑局的原生 `.run` 结算文件，补回异常退出时未写完的结算，不用历史包猜求解器参与。

开启多人、无头或无人测试不启动跑局统计采集。设置说明有中英文。原生 UI 操作、长时间帧率和真实结算尚需可见游戏验收；自动合同不替代这些项目。

## 服务端

`tools/OnlinePresence/run-statistics.mjs` 维护独立 SQLite 表：`runs` 与 `run_history_snapshots`。复用 HTTPS 和管理登录，兼容旧心跳；昵称保持原在线系统的内存生命周期，离线战绩使用匿名档案标识。

- `POST /v1/runs`：`{sessionId,run}`。同一安装＋跑局 ID 幂等；pending 可更新，已结算冲突返回 409。
- `POST /v1/run-history`：`{sessionId,historical}`。按安装＋档案保留首次历史快照。
- `GET /api/run-statistics`：管理登录必需。分页 30；source=solver/historical，participation=full/partial/none/all，activity=solve/execute/auto，character、version、ascension、since/until（毫秒）；streak_min/max、best_min/max、rate_min/max（百分比）、wins_min、losses_min、abandoned_min、runs_min；sort=streak/best/rate/wins/losses。

问题包 report.json 的可选 `runStatistics` 是提交时快照。日志站数值筛选不匹配旧包 null；不回填、不随后来战绩变化。日志站不以问题包份数统计全站胜率。

## 验证入口

- `dotnet run --project tools/RunStatisticsTests -c Release`：连胜、放弃、缺口、持久化、去重、补传收据、历史隔离、原生结算恢复。
- `npm test`（tools/OnlinePresence）：统计、筛选、权重、幂等、管理鉴权、旧心跳及持久登录。
- `UI-LOCALIZATION`：中英设置和 headless 统计节点隔离。
- 日志站 `test_reports_v2`、`test_agent_api.AgentApiTests`：提交快照、缺失字段、筛选、排序、百分比验证与归档。
