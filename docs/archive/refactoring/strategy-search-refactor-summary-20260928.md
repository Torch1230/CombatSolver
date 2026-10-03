# 策略与搜索重构实施总结（2026-09-28）

## 范围与结论

本轮以 `main@8915a7c0` 上的[原计划](strategy-refactor-plan-20260927.md)为起点，在 `refactor/strategy-search-p0-20260927` 分支实施。计划原定 P0–P8；截至本总结，P0–P6 已按各阶段记录的边界收口，P7 只完成一部分并保留试验结论，P8 尚未达到退出标准。按用户本次指示，重构工作在此收尾，暂不继续 P8，也不把 P7 的剩余目标写成已完成。

重构的主要变化是：固定根对照成为行为迁移的依据；路线比较、请求预算、前缀续搜、开局身份登记、串并行作业调度和跨回合计划有了明确的所有者。P1–P5 是保持原行为的结构迁移，已有同根逐位证据。P6 引入新的计划成员，两个目标包在同根搜索中改善，但仍未追平旧玩家投影。

本文件写实际落地的边界及证据，不替代[架构地图](../../ARCHITECTURE.md)和[测试矩阵](../../TEST_MATRIX.md)。完整玩家检查点、请求协议与逐字段结果存于仓库忽略的 `.local/`，未提交玩家数据。源码提交可从本分支的 `3422a1e8`（P0）至 `cc652a8e`（P6 收口）追溯；其间还包含 P7/P8 的已提交工作及一次 0.47.1 热修合并。

## P0：固定根语料与逐位对照

- `coverage/corpora/strategy/p0.json` 固定六个玩家 `combat_start` 根（#24、#37、#79、#81、#85、#89）、两个生成场景，以及备用 #56/#63。玩家原包只由 `.local/` 读取。后续 0.47.1 热修后，以同政策重新采集 P2 基线。
- `tools/search/StrategyCorpus/run.py` 调现有无头恢复入口和离线生成场景入口，冻结根身份、游戏与 Mod 身份、完整政策、VeryHigh、节点数与 DOP；`compare.py` 先检查根及政策，再逐字段比动作、嵌套选择、续用、终局、请求工作量和剪枝。墙钟、分配、GC 单列，不参与逐位门槛；结果按当时的路线质量顺序分为变好、变差、不变或不可比较。
- P0 基线各跑一次，P1 最终源码各跑一次。#24/#37/#81/#89 和两个生成根的有效对照逐位相同。#79/#85 的基线用时接近 110 秒边界，标为不可比较，未用不稳定的工作量证明等价。备用根没有运行。

相关入口：`tools/search/StrategyCorpus/run.py`、`tools/search/StrategyCorpus/compare.py`、`coverage/corpora/strategy/p0.json`。提交起点：`3422a1e8`。

## P1：路线质量模型

- `RouteQuality` 统一提取胜负、生存、战略战损、成长、药水成本、结束回合等质量字段；`RouteQualityPolicy` 提供比较入口。请求结果、终局选路和保路调用各自的显式投影，保留原有不同的键顺序、同分先后与硬准入规则。
- 迁移了请求级结果选优、终局路线和确实共用字段的保路比较。没有调整权重、预算、动作生成或最终胜负判定。cycle、region、跨回合比较仍以各自的投影表达原语义。
- P0 中六个有效根的完整动作、结果、续用、expanded、transitions、choice branches 与剪枝逐位一致；#79/#85 不计入门槛。

主要源码：`src/Search/RouteQuality.cs`、`RouteQualityPolicy.cs`、`SolverInterimResultOrdering.cs`、`CombatBeamSolver.FinalPlanOrdering.cs`。边界提交：`e12bcb4c`、`370d16d2`、`fc458fad`、`87170825`。

## P2：请求管线与预算账本

- `SearchPassContext` 冻结根、政策、profile、取消与请求账本；`SearchPassResult` 显式返回选中路线、质量、累计工作量、轮次终止和接管状态。`SearchRequestPipeline` 依原先顺序调度主搜索、无胜利升级及外层后处理，不改 `SolverResult` 的终局政策。
- `SearchBudgetLedger` 与 `SearchBudgetWindow` 提供请求剩余时间、节点和原成员切片。主 Pass 前缀、强制用药、Smart 药水梯度、能力/复制路线及后处理成员逐入口迁入账本；采样时点、切片常数、短路顺序、诊断标签保持原值。实际搜索工作量仍由 `SearchRequestWorkTotals` 累计。
- 外层补搜抽入 `CombatSearchCoordinator.PostSearch.cs` 的具体 Pass。此处是编排迁移，不代表改变了原预算分配策略。
- 0.47.1 热修后的 #24/#37/#81/#89 与两个生成根，对同政策 P2 基线的动作、续用、结果、工作量和剪枝逐位一致。语料未触发的模式只具有结构及定向证据，不宣称全部运行覆盖。

主要源码：`src/Search/SearchRequestPipeline.cs`、`SearchBudgetLedger.cs`、`CombatSearchCoordinator.PostSearch.cs`。收口证据：`348c1b8e`；详细结果见测试矩阵的 P2 收口条目。

## P3：前沿续搜调度

- `ContinuationSearchRequest` 携带前缀、用途、成员 profile、政策覆盖和用药界限；`FrontierContinuationScheduler` 统一构造固定前缀求解器，在原采样点读取共享请求窗口，并按完整动作身份处理需要去重的来源。各模式仍在原位置生成候选、检查准入并比较结果；可选药水未达标只按已有业务异常处理。
- 双药开局、回合边界、零费开局、强制药、Smart 药水、能力路线和默认关闭的前两回合实验等固定前缀入口已迁入调度器。`ContinuationPurpose` 显式标记用途；原诊断标签保留。`CombatSearchCoordinator.cs` 主文件约降到 1,000 行，宽度组合及审计搬入各自 partial 文件。
- #24/#37/#81/#89 和两个生成根对 P2 基线逐位相同。#17 另验证双药两种顺序均实际派发；此定向结果不等同于所有可选模式的逐位证明。

主要源码：`src/Search/FrontierContinuationScheduler.cs`、`CombatSearchCoordinator.BeamPortfolio.cs`、`CombatSearchCoordinator.Audits.cs`。收口提交：`83774879`。

## P4：开局、药水和目标登记

- `PotionValuationRegistry` 持有内置药水的原有成本档位与开局使用类别；`PotionUsePolicy` 继续处理自由药基线、奖励抵扣及使用资格。未登记的第三方药水沿用原普通成本。
- `OpeningActionRegistry` 收口白噪声、夜魇、复制药水等特殊开局身份匹配；候选模拟、排序和保路仍归开局扩展。`TargetPlanRegistry` 持有集火前缀、前两次改目标及每目标进攻代表的枚举规则。
- 本阶段只迁移已有身份和分类，不宣称所有 Search 硬编码 ID 已去除。收口时其余 ID 字面量仍有 645 处的结构上限；登记表是内部机制，没有新增第三方运行时注册 API。
- 四个玩家根及两个生成根对 P3 同政策结果的动作、续用、终局、工作量及剪枝逐位相同。

主要源码：`src/Search/PotionValuationRegistry.cs`、`OpeningActionRegistry.cs`、`TargetPlanRegistry.cs`。收口提交：`e248b6e3`。

## P5：串行和并行展开的共享作业调度

- `ExpansionPlan` 统一卡牌、药水候选枚举和普通选择要求；共享入口负责父节点、卡牌、药水、回合尾部准入及子节点基础构造。`AdmittedParent` 保存已准入父节点的作业状态，`AdmittedJobScheduler` 以同一状态机派发准备、卡牌、挂起/嵌套选择、药水和尾部作业。
- `IExpansionExecutor` 统一子节点接收与完成提交合同。串行仍逐子节点即时交付，并行仍使用固定 worker lane、批次原序提交和既有快照释放时点。这是保留执行方式差异的共同调度，并非把 DOP1 和 DOP8 强行变成同一动作轨迹。
- 保持父节点 Fork 所有权、最后节点预算槽的首子节点语义、512 次回放上限、选择预算、物理实例补充、周期租约、转置和支配顺序。迁移的作业状态不再由并行执行器独占。
- #24/#37/#81/#89 与两个生成根按 DOP1 对 P4 同政策基线，剔除后来增加的 P8a 工作归因字段后，完整动作、续用、终局、工作量与剪枝逐位相同。GA-SILENT-BOSS-00 的 DOP8 对自身旧 DOP8 基线 122 个非时序字段相同。P4 时 DOP1/DOP8 已有动作次序和计数差异，不能把跨 DOP 的逐位一致当作门槛。DOP8 的单次墙钟约 24.053→24.091 秒、worker 分配约 11.886→11.880 GB，仅供观测，不构成性能改善结论。

主要源码：`src/Search/CombatBeamSolver.AdmittedExpansion.cs`、`CombatBeamSolver.ParallelExpansion.cs` 及 `IExpansionExecutor` 实现。收口提交：`b05fd8f5`；证据目录 `.local/strategy-refactor-p5/final-shared-scheduler-*`。

## P6：有界计划续搜

- `PlanCommitment` 以类型化前缀、阶段和收益证据表达复制、能力启动/循环、药水链与跨回合收益。`PlanMechanismRegistry` 目前登记已有的延后复制效果；发现器从模拟选牌结果、已登记机制和下一回合可打出的能力提名计划，不在发现器按问题包或复制牌 ID 写特例。
- 计划成员经 `FrontierContinuationScheduler` 派发，从 P2 请求账本取得原成员上限内的时间与节点，复用 P3 固定前缀求解。终局仍由原路线质量与用药政策裁决，没有新增独立预算池。常规后验先完成，跨回合能力发现再使用请求剩余额度。
- 已兑现收益证据的计划成员可在原无进展上限之后至多续行一个牌堆周期；无计划和未兑现的成员沿原上限，总请求预算不变。阈值合同通过，但 #100/#101 没有实际命中额外续期，改善不能归因于该地平线。
- VeryHigh／180 秒／DOP8 的同根对照：#100 从 41 战损、2 药、最终 29 HP 改为 22 战损、2 药、最终 48 HP；#101 从死亡路线改为胜利、58 战损、1 药、最终 12 HP。两包仍分别比旧玩家投影多损 18、13 HP；旧投影不是本轮同根人工回放。已达标 #90 保持 0 战损、最终 49 HP，其动作、质量和工作量不变；GA-SILENT-BOSS-00 的 DOP1 对 P5 基线逐位相同。
- `PowerCommitment.RealizedEvidence` 表示节点局部战术进展，并非能力效果的因果收益。`PlanPayoffEvidenceKind.RegisteredPowerBenefit` 虽可表达，当前没有生产提名者；能力计划目前依实际打出能力牌作阶段证据。不能据此声称能力长期收益建模已经完成。

主要源码：`src/Search/PlanCommitment.cs`、`PlanMechanismRegistry.cs`、`CombatSearchCoordinator.PlanSearch.cs`、`CombatBeamSolver.CrossTurnPlanning.cs`。收口提交：`cc652a8e`；证据目录 `.local/strategy-refactor-p6/`。

## P7：已落地的部分与保留结论

P7 没有收口，也没有完成原计划的默认权重校准、动态药水机会成本或每目标代表机制。已提交的改动和有用证据如下：

- **P7a 中途权重探针。** 可在固定根请求中只扰动一个 Beam 权重并汇总同根质量。默认权重未改。#100 将 `PersistentBuffDelta` 乘以 1.5 后，战损从 22 升到 55，仍用 2 药，因此该倍率没有进入生产默认值。较小样本的质量不变也不足以证明更广的权重调整安全。
- **P7b 免费生成药链。** 混沌药生成药原本已有零机会成本来源；新成员从模拟分支提名连续用免费药及合法进攻跟进，经共享账本完整续搜。#90 同根由 3 战损、最终 46 HP 改为 0 战损、最终 49 HP，仍消耗原有两瓶药，另使用两瓶免费生成药；生成根哨兵结果不变。现有战后掉药前景提供部分根上下文，普通药水的 9/14/18 档位仍在，原计划的完整动态机会成本尚未实现。
- **P7c 每目标代表试验撤回。** 提前预约目标代表使 #97 从 21 降到 9 战损，却使已达标 #81 从 8 升到 9，故生产源码撤回。两份 #97 路线的首个分歧是第 2 张能力牌的次序，早于不同攻击目标，不能把收益直接归因于集火。随后尝试通用的晚段计划前缀，目标 #97 仍为 21 战损；该未提交试验也已撤回。当前没有 P7c 生产行为变化。

## P8：此前产物与本次决定

用户要求本次不做 P8。此前已经提交的 P8a 请求工作来源归因和超时末段监控快照、P8c 离线丢路查询及首分歧工具仍在仓库；P8b 仅核对了计划列举的旧故障，未建立新的失败场景。它们是局部产物，不代表 P8 完成：13 个历史超时包尚无逐包主因，玩家 ZIP 到丢路层级的一键查询、模型编号映射和离线 ABBA 未完成。本次没有新增或验证 P8 代码。

## 验证范围与交付状态

P0–P5 的逐位结论只适用于上述固定根、对应 DOP 与政策；触及时限或未触发的模式不算通过。P6/P7 的质量结论来自明确列出的同根搜索与哨兵，不是完整自动部署或全体问题包回归。此前 P6 最终源码的 Release 构建及 Windows 结构门禁已通过；用户要求不运行 Linux 门禁，因此本批后续未运行。具体命令和原始结果见[测试矩阵](../../TEST_MATRIX.md)，本次文档收尾没有重新运行搜索、构建或结构门禁。

最后一次 P6 源码构建已部署到确认的本地 `mods/CombatSolver`，其后只提交文档并撤回未提交的 P7c 试验，当前源码没有再变化。本次不提升版本、不发包，也不继续运行问题包。
