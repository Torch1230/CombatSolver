# Issue #36：GC 与分配静态审计

- 源码基线：`5c4b69d91bcc049781e9de6ddf043799a82c849b`。
- 审计日期：2026-09-05。
- 需求来源：[Issue #36](https://github.com/Torch1230/CombatSolver/issues/36)。Issue 中的性能数字是历史证据，其历史场景的完整复测仍未执行。
- 证据范围：本文件仅记录当前源码、仓库约束和相关 API 文档的静态审计；当前基线采样另见 [研究记录](gc-issue36-research.md)。以下“已实现”只表示代码存在，不表示本轮验证通过。
- 范围：通用 GC 生命周期、分配、对象图持有和并行调度。世界线及小循环搜索策略由独立任务研究，不在此处改变评分、保路或战斗语义。

## P0 对照

表中行号对应上述基线；相对链接用于定位源码文件。

| Issue 项 | 静态结论 | 源码证据与剩余缺口 |
| --- | --- | --- |
| Smart 层预测性回收 | 尚未实现 | [CombatSearchCoordinator.cs](../../src/Search/CombatSearchCoordinator.cs) `1357–1387`：信号启用且已发生分配，就调用 `ReclaimAndContinue`；`1221` 在尝试下一用药层前调用。没有下一层预计工作量、bytes/transition、剩余预算或预测误差判断。 |
| 剩余预算控制 outer parent wave | 部分基础已有，缩容顺序尚未实现 | [SearchMemoryPressureSignal.cs](../../src/Runtime/SearchMemoryPressureSignal.cs) `87–99,145–150` 已提供两种余量的最小值和预约准入；[Phases.cs](../../src/Search/CombatBeamSolver.Phases.cs) `1116–1131` 却按 `AllocationLimitBytes` 总限额计算容量，再在 `1270–1273` 预约整波，余量不足时 `1194–1197` 直接回收。未先尝试缩为可容纳的 4/2/1 个 parent。 |
| 安全检查点在 prune/释放之后 | raw batch 释放已有，prune 顺序仍有缺口 | [Phases.cs](../../src/Search/CombatBeamSolver.Phases.cs) `1317–1335` 在提交后释放 batch 和 parent；`1161–1163` 清可重建缓存再回收。但 `1360` 的 wave 后检查点早于 `1365–1366` 的 `Prune(nextPlays)` / `ReleaseDroppedSnapshots`；串行 parent 后 `1235` 也可先回收。 |
| 普通 GC 模式根据实际吞吐自适应 DOP | 尚未实现吞吐/暂停反馈 | [SearchGcPolicy.cs](../../src/Runtime/SearchGcPolicy.cs) `565` 在用户关闭 NoGC 时禁用信号；[Phases.cs](../../src/Search/CombatBeamSolver.Phases.cs) `1119–1123` 直接使用用户 DOP，仅系统余量受限 fallback 才限制两个 lane。`1354–1358` 容量增减依据是否超出分配预约，不依据吞吐、暂停占比或升核后的收益。 |
| GC 指标 | 已有基础，尚不能严格覆盖 Issue 验收口径 | 已有累计暂停、各代计数、线程分配、阶段指标和回收前后内存；缺少统一的 forced-collect / NoGC 生命周期计数、每波明细、SOH/LOH 分配比例和请求峰值。最大暂停的现有字段还有口径混用，见下文。 |

## 应保留的现有机制

- Runtime 管理 GC，Search 通过信号消费；实际区域预算受用户上限和系统余量约束。`SearchGcPolicy` 的 `1645–1724` 已有尺寸不支持时逐次减半、最低申请预算和系统高内存阈值安全线。
- `SearchGcPolicy` 的 `1517–1527` 在 unexpected NoGC loss 后回退常规 GC，避免同一搜索反复申请失败区域；`1596–1602` 的搜索内回收为 blocking、non-compacting。LOH 压缩位于 `1270–1277` 的手动内存释放入口。
- [ParallelExpansion.cs](../../src/Search/CombatBeamSolver.ParallelExpansion.cs) `729–754,774–794` 的 inner action / choice microbatch 已按 `RemainingBytes` 缩容；outer wave 的缺口不能概括成“所有并行路径都只读总预算”。
- `ParallelExpansion.cs` 的 `86–170` 明确 batch 的 snapshot 所有权与 Dispose 释放；`Phases.cs` 的 `1012–1016` 保留 coordinator 转置顺序，仅清理可重建缓存，具体见 [Models.cs](../../src/Search/CombatBeamSolver.Models.cs) `160–165`。
- `CombatSearchCoordinator.cs` 的 `1389–1423` 已将 Smart 边界开销计入请求，并在回收完成后观察到取消时仍保留这段已发生的开销。

## P1 对照与分配候选

以下顺序是采样优先级，不是已证明的性能收益排名。

| 候选 | 静态证据 | 待测假说与语义约束 |
| --- | --- | --- |
| 出牌历史与完整分支图解耦 | [HistoryEntry.cs](../../src/Engine/InCombat/Simulation/CombatPredictionHistoryEntry.cs) `91–104` 的 `CardPlayStarted/Finished` 仍持有 `PredictedCard` 和 `CardPlay`；[History.cs](../../src/Engine/InCombat/Simulation/CombatPredictionHistory.cs) `251–267,414–425` 写入这些引用并在 Fork 共享前缀。存在历史 → wrapper → observer/owner pile → 分支状态的静态可达路径。 | 历史可能延长祖先分支的存活，是优先做 retained-graph 采样的候选；尚无本轮 retained bytes 或支配树证据。不能直接替换为标量，因为动作内引用身份和历史计数仍有消费者。 |
| 限制完整 raw aggregate 的峰值 | [ParallelExpansion.cs](../../src/Search/CombatBeamSolver.ParallelExpansion.cs) `241–261` 等待整波 barrier；`506–523,703–720` 将 inner microbatch 结果移入同一 aggregate，直到整个 parent / choice frontier 结束才返回。outer 提交见 [Phases.cs](../../src/Search/CombatBeamSolver.Phases.cs) `1300–1325`。 | 小微批限制在途工作，却不等于限制累计 raw graph。需测 outer raw 数量、inner aggregate 峰值、未提交/待剪 snapshot 数量和 retained bytes。流式提交与脱水重放可能有效，但不能改变候选顺序、提前 Beam prune 或重建 coordinator 转置状态。 |
| StateStore 与牌堆 Fork 剩余分配 | [PredictionStateStore.cs](../../src/Engine/Common/PredictionStateStore.cs) `7–21,190–227` 创建三张字典、逐 state Fork，并逐项新建 `OwnedStateEntry`；[SimCardPile.cs](../../src/Engine/Common/SimCardPile.cs) `86–99` 逐牌建 wrapper；[PredictedCard.cs](../../src/Engine/Common/PredictedCard.cs) `150–169` 共享 preview storage 仍新建 wrapper；[CombatPredictionState.cs](../../src/Engine/InCombat/Simulation/CombatPredictionState.cs) `213–234` 复制字典和集合。 | 先按类型建立 bytes/Fork 与 bytes/transition 账本，再判断容器包装或 typed storage/COW 的价值。不能以跨分支浅共享替代 observer、所有权和 Fork remap；通用 StateStore COW 的验证面大于去除容器包装。 |

第一项的路径细节：[PredictedCard.cs](../../src/Engine/Common/PredictedCard.cs) `57–58` 持有 `_ownerPile` / `_mutationObserver`；[SimulatedCombatState.cs](../../src/Search/SimulatedCombatState.cs) `1812–1826` 的 observer 回到分支，`1433–1437` 关联 `_predictionState`。[CombatPlan.cs](../../src/Search/CombatPlan.cs) `1187–1194` 的 `ReleaseSimulator()` 仅断开 snapshot 自身的 simulator 引用，不能切断上述历史路径。其他历史事件已采用 `CombatPredictionCardSnapshot`（`HistoryEntry.cs` `49–68`），因此不能把现状概括成“历史完全没有标量化”或“历史已全部标量化”。

历史方案需先核对消费者：[CombatPredictionSimulator.Card.cs](../../src/Engine/InCombat/Simulation/CombatPredictionSimulator.Card.cs) `156–165`、[CardExecutionSupport.cs](../../src/Prediction/CardExecutionSupport.cs) `26–36`、[PowerLifecycleSupport.cs](../../src/Prediction/PowerLifecycleSupport.cs) `50–60` 使用出牌引用身份；[TriggeredPowerSupport.cs](../../src/Prediction/TriggeredPowerSupport.cs) `79–89` 使用 owner；[CardEventHistory.cs](../../src/Search/SimulatedCombatState.CardEventHistory.cs) `120–123` 和 [CalculatedVarSpecRegistry.cs](../../src/Prediction/CalculatedVarSpecRegistry.cs) `167–175` 消费历史计数。候选方向是分离动作执行中的身份记录与稳定边界后的持久历史，仍需验证 deferred 完成与共享 segment 语义。

P1 已有以下机制，不应重复当成未开发工作：

- [PredictionForking.cs](../../src/Engine/Common/PredictionForking.cs) `304–346,445–458` 已租用数组，64 项后建立索引，归还引用数组时清空。
- [SimulatedCombatState.Fork.cs](../../src/Search/SimulatedCombatState.Fork.cs) `139–205` 的 listener remap 仅在引用实际变化时复制数组，无变化共享序列，并跳过 BaseLib CardModifier 根不可消费的缓存复制。
- `History.cs` 的 `107–134,414–478` 已采用共享不可变 segment 和 tail 快径。
- `PredictionStateStore.cs` 的 `100–122` 已提供不登记状态的 `Peek` 指纹路径；这不表示 `GetReadOnly` 也无登记分配。
- 固定 worker lane 已在一次 Solve 内复用，不应重新引入每 parent 新 Task / solver，或池化带 observer/COW 所有权的完整 simulator。

## 指标陷阱

| 指标或来源 | 当前含义 | 研究时的处理 |
| --- | --- | --- |
| `TotalMaxObservedGcPause` | `CombatSearchCoordinator.cs` 的 `1394,1408–1409,1422–1423` 把整个 Smart 边界的累计暂停同时计入 total 和 max。窗口有多次暂停时，这不是单次最大值。 | 区分累计暂停、轮询观测最大暂停和 trace 严格最大暂停；不能用同一数值填充 total/max。 |
| worker `MaxObservedGcPause` | [SearchWorkPacer.cs](../../src/Search/SearchWorkPacer.cs) `24–26,57–71` 定期发现代计数变化后，只读取最近 GC 的 `PauseDurations`。轮询间可漏过 GC；搜索退出前也不保证再次采样。 | 继续称为 observed max，不能声称等价于 trace。当前 [SolverPolicy.cs](../../src/Testing/UnattendedTestRunner.SolverPolicy.cs) `429–434` 却把总字段解释为“单次 GC 最大暂停”，调整指标时应同步测试口径。 |
| `gc0/gc1/gc2` | `Phases.cs` 的 `493–496` 记录 `CollectionCount` delta，并独立记录 `GetTotalPauseDuration` delta。 | 各代计数变化不能直接解释为强制 Full GC 次数；NoGC 启动的计数影响也不能换算成暂停。 |
| 强制回收次数 | 搜索内实际调用在 `SearchGcPolicy.cs` `1596–1602`；后台日志 `1141–1151` 已区分 `collection_requests` 与 `gen2_delta`。 | 建立统一的调用计数与生命周期事件；一次回收请求、一次完成事件、一次暂停是不同概念。失败/取消也需保留已发生的事件。 |
| `NoGcRegionRolloverCount` | `SearchGcPolicy.cs` 的 `372–386` 仅在下一搜索入口余量不足时增加；不包含搜索内或 Smart 重启 `1507–1515`。 | 保留原名称含义，另计 start/end/restart/loss；不能直接当作 Issue 要求的总 restart。 |
| 结果内存字段 | [Writer.cs](../../src/Testing/UnattendedTestRunner.Writer.cs) `79–90` 是捕获结果时的内存/NoGC 状态。回收日志也只是 before/after 样本。 | 不得称为搜索全过程峰值；需要单独采样 managed、working set，并注明采样间隔与口径。 |
| 阶段时间和分配 | [SearchPerformanceMetrics.cs](../../src/Search/SearchPerformanceMetrics.cs) `46–60,64–75` 按线程计量并合并 worker。阶段可能嵌套，worker 时间可重叠。 | 不把阶段 ticks 的和解释为墙钟；不把嵌套阶段分配相加作为总分配。 |
| 系统内存余量 | `SearchGcPolicy.cs` `1390–1396,1695–1700` 来源是 `GC.GetGCMemoryInfo()`，signal 再加当前区间的过程分配作预测。 | 这是基于最近 GC 的预测，不能直接称实时 OS 内存采样；系统其他进程变化及区域复用时的估计误差应在实验中记录。 |

`GetGCMemoryInfo` 的数据属于最近已完成 GC；`MemoryLoadBytes` 是该次 GC 时的物理内存负载。参见 [Microsoft API 文档](https://learn.microsoft.com/en-us/dotnet/api/system.gcmemoryinfo?view=net-9.0) 和 [官方实现说明](https://devblogs.microsoft.com/dotnet/the-updated-getgcmemoryinfo-api-in-net-5-0-and-how-it-can-help-you/)。这些 API 语义支持上述口径限制，不构成本仓库的实测结果。

## 独立实验顺序

1. **先统一观测。** 为 forced collect、NoGC start/end/restart/loss、触发原因建立可关联的请求/层/波记录；分别记录进程分配与搜索线程分配。缺失数据保留未知，不填零。先证明统计无重复记账、取消不丢失已完成开销。
2. **只改 outer wave 准入。** 以剩余预算计算可容纳 parent 数，仅单 parent 也不能安全提交时回收；保留提交、剪枝顺序。纯决策用例覆盖余量可容纳 8/4/2/1/0、信号禁用、系统约束更紧、冷启动和整数边界。预期减少提前回收是待测假说。
3. **再改 Smart 边界。** 单独引入前层工作量与过程分配估计、安全余量和误差记录；预测不足仍由现有搜索内检查点兜底。不要将 Issue 历史样本数字写成固定阈值。收益与峰值变化尚未测量。
4. **检查点位置另作实验。** 先观察 checkpoint 前 `nextPlays` / raw 数量及堆量，确认缩波能否到达 prune 后安全点。直接引入增量 prune 可能改变保路结果，不能默认属于纯 GC 改动。
5. **普通 GC 自适应 DOP 后置。** 先用固定 DOP 对照取得吞吐、分配率和 pause duty，再设计升降核窗口和滞回；CPU 占用率不作为优化目标。
6. **P1 先采样再选一个表示改动。** 优先看长动作链的历史 retained graph，其次是 raw aggregate 峰值和 Fork 类型分配。每轮仅改一个通用机制；历史身份、COW 或重放表示一旦变化，先做最小语义/Fork 生命周期等价验证，再比较性能，不同时叠加搜索策略变化。

快速性能内环仅用目标短搜与一个不可退化哨兵，单请求总超时不超过 120 秒；性能采样不启用 incremental verification。最终并行候选按固定工作量比较 DOP1/DOP2 的完整动作、评分、展开、转移和非时序剪枝，并证明实际并发。最终性能结论另需正常可见 Steam 会话和至少三次独立复测；Windows Mod 构建及实机验证尚未执行。以上是后续实验顺序，不是本轮通过记录。
