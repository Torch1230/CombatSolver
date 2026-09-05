---
name: search-performance-optimization
description: 在战斗语义已证明正确后，审计或修改 CombatSolver 的候选展开、Beam 保路、终局排序、评分、剪枝、Pareto、转置、预算、分配、No-GC 或 Steam 实机卡顿。
---

# CombatSolver 搜索质量与性能优化

## 适用边界

先证明同一起点、同一动作的 actual/simulated 状态一致。存在根快照、语义或续用偏差时转 `combat-semantic-change`，不要调搜索掩盖。

读取 `docs/ARCHITECTURE.md` 的 Search 章节。当前搜索职责已拆开：

- `Expansion` 产生候选；
- `ParallelExpansion` 用固定 lane 并发物化不同父节点的原始候选，并按输入顺序串行提交；
- `StateEvaluation` 计算快照、威胁和评分特征；
- `BeamRetentionPolicy` 决定中间候选保留；
- `FinalPlanOrdering` 决定终局路线；
- `SearchRunContext` 拥有单次运行指标、转置和缓存；
- `CombatSearchCoordinator` 组织主搜索与药水反事实。

不要把所有问题重新塞回 `Solve` 或用一个总分同时承担保路与终局政策。

## 1. 建立可比较基线

固定同一源码版本、快照、预设和测试模式，记录：

- phase / boundary、searched turns / shuffles；
- expanded、transitions、choice branches；
- dominance / transposition / repeatable pruned；
- elapsed、allocated bytes、bytes/transition；
- GC 次数、总/最大暂停、最大帧和 `>50ms` 帧；
- 战损、药水、结束回合、动作序列和 unexpected replans。

快速内环只保留目标质量和一个不可退化哨兵。固定长线性能基线只在最终候选、准备给出性能数字或用户明确要求完整门禁时运行；当前稳定场景从 `docs/TEST_MATRIX.md` 选择，不把历史数字硬编码成永久阈值。

路线质量的首要基线是同一起点、同牌序与 RNG 下的整场战损。回合数、击杀速度和展开深度只作次级信息，除非用户明确把它们设为目标；不得用更少回合替代更低战损宣称优化。

`-VerifyIncrementalSearch`（PowerShell）/ `--verify-incremental-search`（Bash）会逐转移执行完整回放，只用于正确性，不能与性能门槛组合，也不能引用其时间或分配作为生产性能。

## 2. 判断瓶颈所在职责

- **预算不足**：更高预算找到更好合法路线，低预算有明确时间/节点边界。
- **展开截断**：目标动作或选牌未进入 frontier，检查 `Expansion` 和单节点分支预算。
- **中间候选丢失**：候选出现后被状态去重、Beam 通道、配额、Pareto 或转置淘汰，检查 `BeamRetentionPolicy` / `Retention`。
- **终局政策**：完整候选存活但未被选择，检查 `SearchFeatures` 与 `FinalPlanOrdering`。
- **模拟成本**：质量稳定，但 fork/action/round/snapshot/fingerprint 阶段过重。
- **GC / 主线程卡顿**：分别检查 No-GC 是否保持、堆分区、其他 Mod 分配、worker yield 和主线程回调。

先定位候选在哪一层消失，再修改该层。终局政策问题不能通过提高 Beam 分数偷偷解决。

## 3. 保路与终局规则

- 多样性用有上限的显式通道：防守、进攻、能力铺垫、资源、控制、药水状态、集火、复活窗口、破坏性选择等。
- 只有真正独立的目标进入全局 Pareto；增加维度前比较支配剪枝、节点与分配。
- 药水/无药配额替换候选时保护已标记代表；最终药水价值仍由反事实与 `FinalPlanOrdering` 决定。
- 持续能力、未来资源和延迟伤害可帮助中间保路；终局比较实际胜负、战损、药水、卖血和敌方状态。
- 通用上下文可按单敌/多敌、普通/Boss 调整；不要按具体 encounter 或单卡硬编码路线。
- 纯启发式特征不进入战斗状态键或 `ContinuationStamp`。

## 4. 性能所有权

- `SearchRunContext` 是单次运行可变指标、转置和缓存的所有者；不要把这些字段退回 solver 入口或静态全局。
- 并行 worker 只能拥有 lane-local 模拟、缓存、节流和原始候选；transposition、dominance、fallback、预算与最终接收顺序仍由 coordinator 独占。固定 lane 应在一次 `Solve` 内复用，禁止回到每父节点 `Task.Run` / 新建 solver。
- 外层 wave 的已完成连续前缀由 coordinator 按输入序号提交并释放，异常退出仍先排空全部 lane；parent 内 aggregate 仍可能保留多组 raw snapshots。提高 DOP 时检查高目标/高选择场景的峰值 live graph，不能只看总分配或平均 bytes/transition。
- 只有容器进入 `SearchRunContext` 的有界空闲池；每个发布批次必须持有独立 lease，归还前清空引用，旧 Dispose 不得触碰后来租户。不得池化 simulator/model。
- GC 生命周期计数由 Runtime 在准入 Gate 内冻结。普通 GC 的共享进程窗口不得称为独占请求归因；总暂停、observed max 与 trace max 必须区分。Smart 预测只决定可选层间回收，不能改层预算或候选策略。
- worker 阶段 ticks 合并后是累计 CPU 时间，不是墙钟占比；同时记录 `parallel_waves`、`parallel_work_items` 与 `parallel_max_concurrency`，避免只凭配置值宣称已并行。
- BaseLib `3.4.5` 的克隆扩展会以非原子的“先查后加”访问全局弱表。并行搜索必须保留 `BaseLibCloneConcurrencyPatch` 对原版 `MutableClone` 第三方扩展段的窄串行边界；不要删除该边界，也不要把它扩大到候选生成、模拟、剪枝或提交阶段。
- 游戏 `0.111.0` 的 `LocManager.SmartFormat` 复用同一个 SmartFormat 实例及对象池，不支持并发调用。`PowerDynamicVarWarmup` 必须在主线程根捕获时物化规范 Power 与当前战斗 Power 的显示变量；`PowerDynamicVarMaterializationGuardPatch` 保证 worker 不再惰性创建 Power 显示变量。命中 guard 时补齐主线程物化边界，不给全局格式化器加锁，也不在 worker 内提供默认文本。`LocManager.SmartFormat` 本身含异常过滤器，禁止直接用 Harmony 改写。
- Runtime 拥有 `SearchGcPolicy`，Search 只通过 `SearchFramePressureSignal` / `SearchWorkPacer` 消费节流信号。
- 优先避免无价值候选、Fork 和快照产生；No-GC 区内释放引用不会返还预算。
- 区分 transitions 增长与 bytes/transition 增长，用阶段指标定位实际热点。
- No-GC 同时观察配置预算、SOH/LOH、是否保持到搜索退出和首次长帧时的 expanded。
- 收益小且扩大语义验证面的微优化保留简单实现。

## 5. 实验与验证

1. 每轮只改变一个可解释因素，失败实验立即撤回；
2. 快速内环用固定短预算或固定小节点工作量，只跑目标首轮质量；单个 unattended 请求总超时不超过 `120` 秒，并在首个 solver 结果处停止；
3. 目标改善后跑一个不可退化哨兵。药水、卖血、延迟伤害、复活和选择只挑受本次因素影响的代表，不枚举全部类型族；
4. 只有最终候选才做一次 Release 构建、结构门禁和必要的增量等价；增量数字不用于性能比较；
5. 完整自动战斗只在搜索行为确实改变且需要证明部署/复用、用户明确要求完整回归，或准备给出整场质量结论时运行；固定 `Instant / 0 秒`、零非预期重算。超时后不在同一轮扩大等待时间；
6. 固定长线性能与正常可见 Steam 会话只用于最终性能结论，不进入每次参数尝试；
7. 并行改动的最终候选固定节点工作量，至少比较 DOP1/DOP2 的动作、评分、展开、转移和全部非时序剪枝指标，并断言 DOP2 的实际最大并发不小于 2；详细诊断和增量模式继续用 DOP1；
8. 改动涉及 coverage/state 分类时运行对应 CoverageCatalog verify。

职责迁移时同步 `docs/ARCHITECTURE.md` 和结构门禁。搜索行为或指标变化同步开发笔记与测试矩阵。普通优化直接提交；版本和打包时机以 `AGENTS.md` 的活动发布批次和发布口令为准，再转 `release-gate`。
