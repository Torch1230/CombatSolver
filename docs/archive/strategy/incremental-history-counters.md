# 历史计数增量维护

基线 `8be1410`。原 `CombatHistoryCounterKey.Append` 每次扫描完整模拟历史，统计六个数；启用条件是根手牌、抽牌堆、弃牌堆含读者牌。

`CombatPredictionHistory.Record` 现在维护六个整数。单人玩家身份在模拟器建立时固定；普通 Fork、手动选牌 Fork 与执行续接 Fork 按值继承，不复制计数容器。构键保留原标记、字段顺序和整数宽度，只读累计值。原始历史保留，启用条件不变。

## 生命周期

| 边界 | 处理与证据 |
|---|---|
| 暂停/恢复 | Draw/Generated 在原始事件入账，Resolved 不重复计数；续接复制已有总数与待完成事件。定向夹具检查挂起抽牌和生成后 Fork、恢复与重复完成拒绝。 |
| 嵌套自动出牌 | 每个 Finished 事件分别计数；夹具包含外层出牌和嵌套自动出牌。 |
| 开始/完成 | Started 不计完成数，Finished 才计数；抽牌/生成恰好相反，计原始事件，不计 Resolved。 |
| 失败回放 | Replay 从根或稳定父快照 Fork，不回写父历史；无效分支被丢弃。定向夹具验证重复完成失败后计数不变、子分支追加不改变父值。 |
| 根与新增历史 | `RootCombatHistorySnapshot` 的实况前缀按原实现共享；六个计数只统计 `simulator.History`。新根从零开始，分支已累计的模拟前缀只通过 Fork 继承一次。 |

## 检查

`-p:VerifyHistoryCounters=true` 在每次 Record、三类 Fork 完成和构键读取处，把累计值与独立全扫描实现逐项比较。普通构建移除这些调用；扫描基准保留在 `CombatHistoryCounters.Scan`。

离线宿主设置 `OFFLINE_HARNESS_HISTORY_CHECKS=1`，使用 DEFECT 初始根与 `--milestone M1`：21 项通过，覆盖六类事件、两种暂停续接、普通 Fork、父/根隔离和哈希位一致性。该夹具构造历史事件，不执行原生战斗动作。

`history-checks.json` 同时记录同一 523 事件历史上的构键历史后缀计时。每臂 20,000 次，交替执行六轮；包含两轮明显的 JIT 预热变化。只衡量这段构键，不外推整场耗时。

EQ 10、FULL 40、GA 10 对照 `8be1410`：60/60 有效，5,670 个确定性字段一致（992 / 3,743 / 935），预算、时间边界、剪枝计数也一致。B 使用逐事件断言构建；普通构建的启用条件、后缀位布局相同。

普通构建另以 DOP 1、宿主串行运行两个根，每臂一次。下表是选中搜索通道的 `FingerprintMetric`，不是 Coordinator 整个请求耗时。

| 根 | 通道展开 / 转移 | main 构键 ms | 增量构键 ms |
|---|---:|---:|---:|
| GA-DEFECT-ELITE-00 | 8301 / 38164 | 271.8 | 96.0 |
| GA-NECROBINDER-ELITE-00 | 8112 / 56577 | 99.5 | 70.8 |

同一 523 事件后缀、每臂 20,000 次的六轮数据与逐根对照保存在 [results.json](../performance/incremental-history-20260920/results.json)。后缀测量使用不可内联调用边界，防止重复相同输入使整个构键被移出循环；保留预热变化，不据此推算整场收益。语料和生成计划脚本位于同目录。未实机验证。
