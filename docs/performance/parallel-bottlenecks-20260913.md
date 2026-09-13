# 克隆优化后的并行瓶颈定位（2026-09-13）

本轮在更新PR后研究剩余串行边界，未改变生产搜索行为。**优先切口是普通原版Power克隆，其次才是回合尾部作业及同父Fork边界；不优先改转置表或原序提交。** 这是诊断结论，不是新增提速承诺。

## 环境与证据口径

生产行为为 `a117fd2`，研究起点 `469ead2`。复用真实BaseLib 3.4.7 / Ritsu 0.5.20的私有Linux无头环境，DOP16、盛碗虫群同一检查点、3万展开节点、16 GiB NoGC区域。仍使用检查点身份绕行和首个主搜索诊断出口，未把这些入口写入生产。

两次诊断各有不同目的：

| runId | 插桩范围 | 结果 |
|---|---|---|
| `2b5d05d413bb4b75a12924c858bf8134` | 外层模型锁等待/持有、父Fork锁等待/持有、克隆类别计数 | Passed |
| `6b2a67f9b37b46fe87f118d5d5d609f3` | 额外按模型类型计数、派发/接收耗时、直接导出并行作业分布 | Passed |

两次均与之前未插桩A1的85项质量/总工作字段一致，419,813转移、240,306选择分支及22步完整动作/选择与预测/覆盖文本一致。两份诊断构建零警告零错误，临时源码均已恢复；不是再次声称生产编译或完整语义门禁通过。

计时在原Monitor前、获取锁后及退出前取时间戳，以线程局部槽累计；BaseLib只计最外层持有，避免重入重复计数。同父Fork的计时包围原 `PrepareReplayForkSeedCore`。类型计数只覆盖 `PredictionUtils.CloneModelForSimulation`，模型锁计数还包含原版MutableClone入口，二者总数不能强行相等。

线程累计时间互相重叠，包含调度及GC暂停，不等于可节省的墙钟时间。第二次类型字典更新位于锁内，明显增加了锁测量扰动，因此锁时长主要引用第一次较小插桩。两次诊断不用于取代之前0.55%的速度对照。

## 1. 卡牌免锁只覆盖约5.75%的预测模型克隆

两次诊断取得相同的模型类别计数：

| 预测克隆路径 | 次数 | 占全部预测模型克隆 |
|---|---:|---:|
| 独立卡牌克隆 | 329,596 | 5.75% |
| 仍持锁卡牌克隆 | 352,243 | 6.14% |
| Power克隆 | 5,054,479 | 88.11% |
| 合计 | 5,736,318 | 100% |

非卡牌部分全部为Power。高频类型包括VigorPower（609,880）、StrengthPower（489,616）、ImbalancedPower（419,815）、SpectrumShiftPower（415,407）、TheGambitPower（411,608）、PrepTimePower（406,686）和BlockNextTurnPower（406,323）。这解释了为什么仅放开普通卡牌不足以显著改变全局串行覆盖面，但次数占比本身不是耗时占比。

第一次插桩测得模型锁最外层进入5,566,563次，累计持有 **6.05秒**、线程累计等待 **25.54秒**。主要调用链是 `SimulatedCombatState.Fork → ForkPower → PredictionUtils.CloneModelForSimulation`。每个分支必须拥有独立Power实例；不能通过共享可变Power来省掉克隆。

**下一步可实现的有限路径：** 仿照原版卡牌，核对普通原版Power的继承克隆阶段、已物化原版变量、精确元数据补丁，以及 `InitInternalData`。原版Power基础实现克隆变量后调用虚方法 `InitInternalData`，默认返回null；AfterCloned清事件和owner。不能因为类型来自游戏程序集就一律放行，有内部状态初始化/自定义阶段的类型应保守回退；也必须核对AbstractModel基阶段补丁。

对本场出现的Power源码逐类静态检查，**4,413,384次**对应没有覆写克隆阶段或InitInternalData的类型。这个数字只是审计候选覆盖量，不是已经验证的免锁资格。VigorPower等覆写类型仍须单独核对，不纳入默认路径。必需验证真实BaseLib持锁并行、变量/owner/内部状态独占、跨域补丁刷新，再做一次匹配预算的速度对照。

入口：[ForkPower](../../src/Search/SimulatedCombatState.Fork.cs)、[模型克隆](../../src/Engine/Common/PredictionUtils.cs)、[当前卡牌资格核对](../../src/Engine/Common/NativeModelCloneConcurrency.cs)。

## 2. 同父Fork存在真实源写入，不能直接删除锁

第一次插桩记录218,533次受保护Fork：线程累计等待 **12.91秒**，锁内累计 **22.46秒**。其中会包含模型锁等待，与上一节不能相加。

`CombatPredictionSimulator.Fork` 调用 `History.Fork`，后者会 `SealTail`：创建历史段并替换父对象的prefix、tail、完成映射及顺序缓存。Forkable集合也会标记共享存储，第三方StateStore的Fork回调仍须逐项核对。因此当前 `PrepareReplayForkSeed` 的每父锁保护的是实际共享源状态，而非仅多余的防御性锁。

后续方向是将可证明的父状态封存前移到准备阶段，再把只读深拷贝移出窄边界；必须先核对所有源写入和第三方Fork回调。**先优化Power克隆再复测Fork锁**，因为当前锁内包含大量Power克隆，不能预先把它们当成两个独立收益来源。

入口：[Fork seed](../../src/Search/CombatBeamSolver.Expansion.cs)、[模拟器Fork](../../src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs)、[历史封存](../../src/Engine/InCombat/Simulation/CombatPredictionHistory.cs)。

## 3. 回合尾部是当前最粗的展开作业

第二次插桩直接导出既有并行作业分布，避免异步日志文件尚未刷出尾部影响取证：

| 展开作业 | 数量 | 累计作业时间（秒） |
|---|---:|---:|
| Prepare | 29,944 | 1.21 |
| Action | 110,653 | 48.98 |
| Choice | 24,882 | 4.60 |
| PrimaryReplay | 78,973 | 45.19 |
| Tail | 29,944 | **79.83** |

Tail占上述展开作业累计时间的 **44.39%**。它运行 `GenerateRawEndTurnCandidates → BuildEndTurnBranches → ReplayAction / ResolveRoundChoiceBranches`，包含回合推进和回合开始选择。每个父节点必须先完成全部卡牌/选择/药水作业，才能派发单个Tail；同一预约窗口还要全部排空才进入下一窗口。这个依赖关系会限制批次尾部可供派发的工作。

1,190个展开窗口墙钟累计15.745秒，对应工作线程累计179.813秒，**窗口内平均有11.42个lane处于作业中**。作业时间包含阻塞，不能称11.42核CPU利用率，也不能把剩余容量全部归因于Tail；此处没有逐时刻尾部空闲分解。

较具体的研究方向是让EndTurn候选在独立批次中提前计算，仍等卡牌/选择/药水完成后才按原序合并并发布stand-pat基线；或拆出独立的回合开始选择回放。当前实现直接写父Aggregate并发布父节点基线，不能直接提前调用。还需保留同父Fork保护、单条选择链预算、快照独占、内存预约和取消/错误排空。它比Power免锁改动范围大，排在后面。

入口：[作业依赖与执行](../../src/Search/CombatBeamSolver.AdmittedExpansion.cs)、[EndTurn候选生成](../../src/Search/CombatBeamSolver.ParallelExpansion.cs)、[基线发布](../../src/Search/CombatBeamSolver.CrossTurnPlanning.cs)。

## 4. 协调线程和元数据不是当前首要嫌疑

第二次诊断中，展开窗口内协调线程累计等待结果12.959秒，占窗口时间82.31%；原序提交1.130秒、派发1.261秒、接收/汇总0.205秒。派发次数275,586，后续可以减少重复扫描或邮箱往返，但现阶段不值得先改转置/预算的唯一所有者或按完成顺序提交。

元数据RetentionWave墙钟累计0.185秒；待命探针StandPatWave为1.471秒。整个Prune累计4.107秒包含这些子阶段，不能重复相加。镜像注册表命中 `_resolvedSnapshot` 后直接返回，解析锁只保护首次解析/发布；暂无证据支持先重写该锁。

整段诊断窗口25.182秒，进程CPU累计181.222秒，约7.20核当量，包含运行时及GC线程。仍有窗口外工作与回收阶段；本轮没有完整CPU栈或off-CPU归因，不将尚未归类的墙钟差额统称为串行算法。

[机器可读计数、作业分布、GC和静态候选审计](parallel-bottlenecks-20260913.json)。后续顺序：普通原版Power有限免锁 → 重新测锁与尾部占比 → 再决定是否调整Fork封存或EndTurn任务粒度。
