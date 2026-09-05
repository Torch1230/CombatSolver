# issue #36 GC 候选实现与验证记录

研究日期：2026-09-05。研究起点：`5c4b69d`。性能 DLL 对应本分支最终保留的核心实现；交付将基于已获取的 `afb732c` 重放提交，上游新增的是回放恢复及世界线记录，未改变本轮核心搜索文件。关联问题：[Torch1230/CombatSolver #36](https://github.com/Torch1230/CombatSolver/issues/36)。

本文件记录按 issue 方案实施的 GC、对象存储与并发候选，以及没有进入生产的实验。所有修改保持固定搜索预算和候选政策；独立容器数据不用于推断完整搜索收益。正式可见 Steam 与 Windows 尚未验证，headless 结果只用于候选取舍。

## 方案覆盖矩阵

| 方向 | 实现/实验状态 | 证据与取舍 |
| --- | --- | --- |
| GC 生命周期及暂停指标 | 已实现 | 准入 scope 冻结，普通 GC 明示共享窗口；observed max 不冒充 trace max |
| 外层 wave 按余量缩容、剪枝后回收 | 已实现 | 饱和容量检查、DOP1/2 等价通过；保持单 parent 与原序提交边界 |
| Smart 层间预测回收 | 已实现并 A/B | 同窗分配/转移预测，减少可选 reset；峰值增加必须与暂停一起报告 |
| 普通 GC 自适应并发 | 真实实验后撤回接线 | 15 项合成检查、55/35 个真实窗口；一次降核探测吞吐退化并恢复，源码与补丁独立归档 |
| StateStore 包装和空容器 | 已实现 | 直接保存 state、按需字典；真实源码工具及游戏 Fork 检查通过 |
| listener 槽位与分页快照 | eager / lazy 两版均已撤回生产 | 长搜同工作量分配增加 20.81%；恢复原 listener 后单次对照回到 2.801 GB，实验保留为独立补丁 |
| 空 dirty 状态同步 | 已实现 | 复用既有类型计数；非空同步与回调快照顺序保持原样 |
| 历史到祖先 wrapper 的引用 | 已实现 | 不可变卡牌快照与精确 frame 身份；实际游戏 WeakReference 与历史边界通过 |
| raw candidates 驻留 | 外层前缀释放已实现 | 所有 lane 排空后失败退出；parent 内完整 aggregate 仍存在 |
| scratch 容器复用 | 已实现 | 每 `_run` 两个有界空 storage、独立 lease；实际源码所有权/并发检查通过 |
| 通用 StateStore COW | 未采用 | 现有 API 允许 Fork 前借出的可变引用继续写入，无法在 Store 延迟拦截；context 生命周期也不兼容 |
| 按类型分桶 | 未采用 | 会重排跨类型 Fork/remap 顺序，当前收益不足以支持状态访问契约重写 |
| raw dehydrated 重放 / parent 内微批筛选 | 审查后未移植 | 前者增加真实重放；后者必须先隔离 cycle/parent 证据的提交副作用 |
| compact root/value、undo、page COW 内核 | 独立原型完成，未接入生产 | 稀疏与密集、DFS 与 retained-frontier 结果分化；完整真实语义迁移暂无依据 |

## 1. StateStore：减少所有权之外的容器开销

实现入口：[PredictionStateStore.cs](../../src/Engine/Common/PredictionStateStore.cs)。

原实现中 `StateEntry` 只有 `OwnedStateEntry` 一种实际子类，`Read()` 和 `Materialize()` 均返回同一个已拥有对象。因此移除这层每条 state 的堆对象包装，保留 `Dictionary<(AbstractModel Model, Type StateType), object>` 的原键及遍历方式。`_states`、`_modelAliases` 和 `_countByType` 在首次需要时创建；空 Fork 不创建三张空字典。

每条 state 的复制策略仍由其 `IPredictionStateForkable.Fork(context)` 决定。Store 按原遍历顺序检查同一 `PredictionForkContext`，先复用已存在映射，否则调用 Fork 后登记映射。该候选未共享分支可变 state，也没有更改根状态捕获时机。

需要继续保持的契约：

- `Get` 和 `GetReadOnly` 首次读取都会登记 state；`Peek` 未命中时只返回投影，不能增加后续 Fork 工作量。
- 同一 state 被多个 model key 持有时，一个 Fork 只复制一次，子分支中的多个 key 仍指向同一子对象。
- 根模型别名及 state 内部模型引用使用同一个 context 重映射；已预先登记的 state 映射必须复用。
- 父分支在 Fork 之前借出的可变 state 引用，之后仍可以直接写父状态；该写入不得污染已存在的子分支和兄弟分支。
- 移除/重新插入沿用原字典行为；跨类型 Fork 顺序、每类型枚举顺序和有效条目计数保持一致。
- 活动事务、缺失必要引用映射和无效 factory 仍显式失败。没有引入新的战斗状态字段、状态键或 continuation 字段。

### 独立检查及分配样本

[PredictionStateStoreChecks](../../tools/PredictionStateStoreChecks/README.md) 直接编译生产 Store 源码，用最小模型身份和 remap context 测试替身隔离游戏初始化。检查覆盖上述契约，最终候选已通过。它不能替代真实模型 Fork、actual/simulated 或增量回放验证。

每个场景预热 100 次，再运行 20,000 次 Fork；测试 context 在循环中清空并复用，避免把其容量增长混入 Store 的分配差异。结果包含 Store 容器和测试 state 的复制，不包含完整模拟器。

| 场景 | 原实现 bytes/Fork | 候选 bytes/Fork | 减少 |
| --- | ---: | ---: | ---: |
| 空 Store | 约 280 | 约 40 | 85.7% |
| 1 条 state | 624 | 520 | 16.7% |
| 16 条同类型 state | 1,848 | 1,384 | 25.1% |
| 16 条、两种类型 state | 1,848 | 1,384 | 25.1% |
| 16 条 state，带模型别名 | 2,376 | 1,992 | 16.2% |

空场景总分配有一次性 128 B 运行时开销，表中按单次对象成本取近似值；其他列为该样本中的整除结果。短微基准时间不作为提速结论。

### 通用 COW 与 typed buckets 的取舍

通用 COW 无法只在 Store 的 `Get` 入口加一层 `Materialize()` 后安全完成。调用者已经能持有可变 state 并直接修改其属性。现有 [ForkBoundaries](../../src/Testing/UnattendedTestRunner.ForkBoundaries.cs) 中，`VambracePredictionState` 在 Fork 前取得，Fork 后仍通过原引用修改 `TriggeringCard` 和 `BlockGainedThisCombat`。这种写入不会重新进入 Store，无法由延迟物化拦截。多种 prediction state 还暴露普通可写属性和事务字段。

此外，[CombatPredictionSimulator.Fork](../../src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs) 用 `using PredictionForkContext` 在一次完整 Fork 内统一 remap，返回后就 Dispose。延迟到动作执行时才克隆 state，必须重新解决原映射生命周期、引用一致性和跨对象依赖；不能借用已归还数组的 context。

按类型分桶虽然可能加快 `ReadEntries<T>()`，却会改变原键表中交错类型 state 的全局 Fork 顺序。当前 state Fork 会读取并补充同一个 context；保持每个类型内部顺序不足以证明整个对象图等价。独立检查现已验证交错类型的 Fork 顺序。现阶段保留原键表，利用原有类型计数优化空查询。

## 2. listener：两版候选均已撤回生产

eager 分页和 lazy 分页都通过了所有权、顺序与实际游戏 Fork 检查，但完整搜索出现分配和耗时退化，因此生产已恢复原 listener 实现。实验保存在 [ExperimentalListenerSlots](../../tools/ExperimentalListenerSlots/README.md)：[ImmutableListenerList.cs](../../tools/ExperimentalListenerSlots/ImmutableListenerList.cs) 是独立容器，[enable.patch](../../tools/ExperimentalListenerSlots/enable.patch) 保存 observer/slot、三种缓存视图、Fork remap 和游戏内测试的接线。原 `src/Search/SimulatedCombatState.ListenerSlots.cs` 及 listener 专用游戏测试已从生产删除。

### 被拒候选的机制与验证范围

原实现遇到 preview 首次物化或 COW 分离时，将 base、effective、run listener 缓存和 Power 投影置为无效，下一次读取才重建。实验曾在 wrapper 上记录相对于 card suffix 的整数 slot；preview 通知携带旧模型身份，验证 owner、移除状态、苦难/附魔拓扑及所有缓存位置后，立即替换卡牌和最多两个附属 listener。不同视图分别更新，别名视图共享结果，旧快照不就地修改。

两版都没有为整副牌增加字典或引入卡牌白名单。卡牌增删、附属模型拓扑变化、orb/potion/roster 变化和 slot 失配仍走完整重建；BaseLib CardModifier 的 opaque membership 仍沿用保守重建和隔离。slot 是派生数据，不进入指纹或 continuation，Fork 后 observer 重新绑定到子分支。

首版 eager 分页在每次完整重建后把 `List` 再复制为数组或页，增加了一套暂存分配。第二版 lazy 分页通过 `TakeOwnership` 接管私有 List；32 项以内保留平坦存储，宽列表在首次真实变化时才提升为每页 32 项的不可变结构。原子发布后的页表供后续兄弟 Fork 复用，无变化操作不提升。每次实际替换仍创建新页目录、变化页和快照 wrapper；Fork remap 仍遍历整个 listener 序列。

[HookListenerSnapshotChecks](../../tools/HookListenerSnapshotChecks/README.md) 现在直接编译归档的实验容器。复制/转移所有权、延迟提升、无变化共享、兄弟复用、跨页替换、remap 顺序，以及并发分支更新期间旧枚举器的稳定性检查均通过。

以下是 20,000 次单点替换的容器样本。其对照是“每次更新都克隆完整引用数组”，并不代表原生产代码的按需重建行为。

| listener 项数 | 复制整数组 bytes/update | 实验 bytes/update |
| --- | ---: | ---: |
| 16 | 152 | 152 |
| 32 | 280 | 280 |
| 96 | 792 | 360 |
| 256 | 2,072 | 400 |
| 1,024 | 8,216 | 592 |

lazy 版另以每组 2,000 次重建检查成本：96 项无后续变化时，Capture / TakeOwnership 为 1,744 / 856 B；若重建后派生两个有变化的兄弟，则两者同为 2,464 B。256、1,024 项的“重建加两个兄弟”也分别同为 5,264、18,704 B，说明初次分页没有在每个兄弟中重复。但这仍未覆盖同一分支连续修改大量卡牌、随后立即使拓扑失效的情形。

归档补丁中的游戏内测试曾用 65 张生成牌覆盖附魔、父/子/孙、combat/run 身份和顺序、旧快照，以及 Power 投影复用，候选3 fixture Passed，runId `ce7391c26d36420897c00c2ea4d15cf7`。这些语义检查不能证明候选值得保留。

### 2500 节点长搜反例与撤回依据

Silent / 普通 GC / DOP4，每 solver 2,500 节点，双方各三次交替冷进程比较；请求均为 5,000 展开 / 19,065 转移，路线和工作量一致。基线与 lazy 候选3的中位总分配为 `2,804,873,992 → 3,388,591,192 B`，增加 **20.81%**；请求耗时 `13,672.35 → 14,156.00 ms`，增加 3.54%。

所选 2,500 节点 solver 的 `SEARCH_PHASE` 分配中位数进一步定位了退化位置：

| 阶段 | 基线 MB | lazy 候选3 MB |
| --- | ---: | ---: |
| fork | 405.60 | 338.99 |
| card_exec | 142.46 | 98.48 |
| round_enemy_moves | 502.92 | 919.61 |

敌方动作阶段增加约 416.69 MB（82.85%），累计阶段时间 `2,920.7 → 4,117.0 ms`。阶段计数含嵌套和并行 worker 累计，不能相加当作请求总耗时或总分配；它也不是 listener 专用 allocation-stack 归因。

源码存在与该阶段吻合的密集更新路径：实验时，永世沙漏 `INCREASING_INTENSITY_MOVE` 遍历 `AllCards`，先访问 `MutablePreview` 再判断是否为 Wither。Fork 共享的普通牌也因此被克隆。旧 listener 的第一次通知只把缓存置为无效，后续通知不分配容器，下一次读取时合并重建；slot 实验则对每张牌立即复制所有已存在且不互为别名的 base/effective/run 快照。同一页中的连续卡牌也会反复复制页目录和该页。循环随后生成新 Wither，又通过 `RegisterGeneratedCombatCard` 使缓存整体失效，中间快照被丢弃。lazy 分页只省掉未消费重建的初次物化，无法消除这种逐次即时更新。

候选4仅撤回 listener、尚未包含下述只读判型修复时，同一长搜单次对照恢复为 `2,801,108,024 B`、`12,416.4 ms`，工作量仍为 5,000 / 19,065，分配回到基线约 2.805 GB。这一控制对照支持将反增归因于 listener 候选；单次时间不作为稳定提速结论。两版候选均不进入生产，短搜的稀疏更新收益不覆盖此反例。

### 最终保留：判型先读，只对 Wither 取得可写预览

生产只在已有 [MonsterMoveEffects](../../src/Prediction/MonsterMoveEffects.cs) 的递加强度分支调整读取/写入时点：先用 `card.Preview is Wither` 判断，再对匹配者取得 `MutablePreview` 并执行 `FakeUpgrade()`。普通牌不再因只读判型发生 COW；Wither 的写入仍经过原所有权边界。没有恢复 listener slot，也没有添加新的卡牌特例。

[两步 native 差分夹具](../../coverage/unattended/gc-aeonglass-preview-ownership.json) Passed，runId `825d477edaa0456b91934583498388ba`。第一次效果生成 Wither，凋零总伤害为 6、敌方力量为 3；第二次升级既有 Wither 并生成下一张，总伤害为 18、力量为 7。完整 actual/simulated 差分继续检查实际结算。

同一夹具显式开启 [预览身份与 Fork 断言](../../src/Testing/UnattendedTestRunner.AeonglassPreviewOwnership.cs)：行动前保留一个未执行的兄弟分支，行动后验证非 Wither 的原 preview 引用不变、已有 Wither 伤害增长，以及兄弟的所有 preview 身份和 Wither 伤害均不变。该检查已通过，不扩展到整场搜索或可见 Steam 验收。

## 3. 空 PowerAmount 同步：复用已有 dirty 集合

入口：[CombatPredictionSimulator.PowerAmounts](../../src/Engine/InCombat/Simulation/CombatPredictionSimulator.PowerAmounts.cs)。

PowerAmount 同步会先对相关 state 生成快照，逐条应用变化后从 Store 移除；因此这些条目本身已形成待同步集合。原类型计数虽然能避免扫描其他 state，却不能避免调用 `yield ReadEntries<T>()` 时创建枚举器。

新增 `HasEntries<T>()` 直接读取同一 `_countByType`，使用与原枚举一致的 `count != 0` 判定。没有目标条目时提前返回；非空时保留原 `ToArray()`、回调顺序和逐条移除，继续满足回调期间的快照语义。没有新增另一份 dirty list 或改变 Power 结算时点。

专用命令：

```bash
dotnet run --project tools/PredictionStateStoreChecks/PredictionStateStoreChecks.csproj -c Release -- --entry-count-only
```

本轮专项检查通过，覆盖空表、Peek 不登记、GetReadOnly 登记、多类型独立计数、Remove 的父子隔离，以及 Fork 后零/非零类型计数。此快路径尚无独立整搜收益归因，归入生产候选 A/B 一并评估。

## 4. GC 时机与计数

Runtime 的 [SearchGcPolicy](../../src/Runtime/SearchGcPolicy.cs) 仍独占进程 GC 生命周期。新增的 [scope 计数](../../src/Runtime/SearchGcLifecycleMetrics.cs) 在取得准入后的 Gate 内开始，在释放准入前冻结：显式 Gen2 调用次数、NoGC start/end 的尝试与成功、restart 和首次观测的意外丢失分别计数。等待旧请求、scope 退出后后台清理不混入该 scope。普通 GC 可以有重叠搜索，明确标为 `SharedProcessWindow`；独占 NoGC 请求标为 `ExclusiveSearchScope`。没有声称按线程精确归属 CLR 的自然 GC。

结果 JSON 新增 `gcLifecycle` 和可空 `gcLifecycleAttribution`，未从 controller 准入的测试直调 solver 不伪造归因。总暂停继续是进程计数差，最大暂停只记观测到的单段；强制 GC 完成后、重新建立 NoGC 之前采样，避免 start API 覆盖最新 GC 信息。没有 EventPipe trace，不能称为严格最大暂停或按类型 retained heap 账本。

外层 wave 使用 [SearchWaveMemoryPolicy](../../src/Search/SearchWaveMemoryPolicy.cs) 从实际分配/系统余量中预约，先缩小批次，单个 parent 也放不下才尝试回收或串行回退。出牌深度末尾先剪枝、释放丢弃模拟器并清空原列表；仍有下一次 parent 准入时才检查回收。没有在动作/选择内部增加 checkpoint。

Smart 层间使用 [SmartLayerMemoryForecast](../../src/Search/SmartLayerMemoryForecast.cs) 的同一完整层进程分配与请求转移数预测下一层：bytes/transition 高水位、至少 2 倍转移增长、1.5 倍余量与低估反馈。NodeLimit 是该有限预算层的完整观测；TimeLimit、取消、失败等样本不能支持跳过回收。预测只决定是否省去可选的层间 reset，现有 parent 安全检查点仍生效。跳过回收会让不可达对象一直占用 NoGC 预算，必须同时看峰值，不能只报告暂停减少。

纯检查：基础 GC/预测 19 项、scope 冻结/重叠/取消 8 项、wave 容量与饱和运算专项均通过。普通 GC 自适应并发的 15 项决策检查只证明合成样本下控制器行为，其真实接线单独实验，默认不启用。

## 5. raw batch 与历史引用

外层 [ParallelExpansion](../../src/Search/CombatBeamSolver.ParallelExpansion.cs) 按输入序号等候已完成前缀，立即提交并释放 raw batch 与父模拟器，后面的 worker 可以继续运行。任一 worker 或提交异常会先排空已经派发的全部工作，再释放并抛出。顺序、transposition、dominance 和预算仍由 coordinator 独占。单个 parent 的 action/round-choice aggregate 仍会累积 raw candidates；这里没有宣称完全消除了峰值驻留。

[OwnedExpansionBatch](../../src/Search/OwnedExpansionBatch.cs) 仅复用三个 List 和两个所有权 HashSet；每个 `_run` 的池最多留两个 storage，每个容器真实容量不得超过 4096，checkpoint 清空闲池。批次使用独立 lease 和原子 Dispose，先清引用再归还，旧批次的重复释放不能接触新租户。没有池化 simulator、node 或 model。[实际源码工具检查](../../tools/ExpansionBatchChecks/README.md) 的 7 组所有权、部分失败、并发与 WeakReference 检查通过。

历史 Started/Finished 的卡牌及 DamageReceived 的卡牌来源改用既有不可变快照，保留原生 `CardPlay` 身份；当前动作是否开始改用精确 trace-frame 身份，不能只凭同一个 Original 认定兄弟 Fork 是同一动作。[历史检查](../../src/Testing/UnattendedTestRunner.HistoryRetention.cs) 覆盖嵌套/重复动作、共享历史 prefix、deferred 事务、快照升级字段，以及仅保留 forkHistory 时祖先 wrapper/分支的 WeakReference 回收。

这解除了一条明确的历史到 PredictedCard observer 的持有路径；召唤 Creature、原生目标和伤害来源仍有合法模型引用，完整图去引用需要稳定实体 ID 与统一重映射，不能靠删除这几个历史字段完成。

旧 dehydrated 原型会先释放 raw simulator，再从父状态重放被接收者；本轮审查后未移植。它增加真实 Fork/action 成本，也需要完整重放等价验证。进一步把单 parent 拆成边生成边筛选的微批，会提前修改 cycle tracker/parent lease，必须先隔离 worker 读取的父证据。当前采用已验证的外层前缀释放与容器复用。

## 6. P2 内核原型的结论

[CompactStatePrototype](../../tools/CompactStatePrototype/README.md) 实现了不可变根、稳定实体 ID、连续标量/RNG、深拷贝、undo journal 与 page COW，对 DFS 和保留完整 frontier 分别做全状态/回放检查及五轮轮换计时。7 组语义检查通过。

在固定 256 实体、5460 转移的 synthetic retained-frontier 样本中，稀疏 page COW 为 1737 B/转移，深拷贝为 8594 B/转移；密集写时 COW 增至 9680 B，时间也约为参考的两倍。Undo 在 DFS 预热后可以零分配，但保留 frontier 仍约 8474 B/转移。原型没有真实 Hook、牌堆、选择或并行 worker，所以本轮不采用完整 P2 生产迁移。

## 7. 被撤回的 listener 候选：短搜 A/B

基线固定 `5c4b69d` / 0.30.0，不合入另一小循环任务的改动。Release 输出只部署到研究目录的独立游戏。环境和隔离方式沿用 [首轮研究](gc-issue36-research.md)。

曾评估的延迟分页候选的 `GC36-CANDIDATE3-BOUNDARIES` Passed，runId `ce7391c26d36420897c00c2ea4d15cf7`。包含完整 Fork 边界、上述历史与宽 listener 检查、搜索请求快照、取消工作量只记一次，以及 DOP1/DOP2 的动作、评分、展开、转移和全部非时序剪枝等价；DOP2 实际并发不少于 2。每 solver 250 节点、Short、请求超时 120 秒。没有运行 headless 增量性能或完整自动战斗。

性能 A/B 改为每 solver 576 节点、60 秒兜底、DOP4、Custom、只运行 Short，每次独立冷进程在首个结果后退出，请求总超时 120 秒；详细诊断关闭，阶段测量开启。两个固定公开 fixture 各跑普通 GC 与 NoGC 4 GB，每配置三次。Silent 请求累计 1152 展开 / 5543 转移，Necrobinder Smart 请求累计 1728 / 22541，均达到 NodeLimit。比较完整 ACTION/TURN 序列、评分、战损、全部工作量及非时序剪枝，不能只凭最终 HP 判定等价。

峰值来自约 100 ms 轮询 `/proc/PID/status` 的 VmRSS/VmHWM，窗口包含游戏启动与建局，不是某个 solver 独占峰值；worker 分配包含搜索线程计数，波次预测则用进程分配，两者不能相减当作精确预算。冷启动/JIT、共享机器负载与计时顺序限制时间结论。一次早期普通 GC 样本出现 15.5 秒离群值，保留在实验记录中，没有删除后宣称稳定提速。

576 节点矩阵的三次中位数如下，时间为请求累计搜索毫秒，分配为累计 worker 十进制 MB。每格 `基线 → 延迟分页候选`：

| 场景 / GC | 分配 MB | 时间 ms | 总暂停 ms | 进程 VmHWM GB |
| --- | ---: | ---: | ---: | ---: |
| 大牌组 / 普通 GC | 542.15 → 457.91（−15.54%） | 4336.6 → 4799.0（+10.66%） | 351.5 → 330.2 | 1.606 → 1.581 |
| 大牌组 / NoGC 4 GB | 543.37 → 457.93（−15.72%） | 4105.4 → 4590.2（+11.81%） | 0 → 0 | 2.233 → 2.103 |
| 药水 / 普通 GC | 1316.22 → 1271.23（−3.42%） | 5910.2 → 6335.9（+7.20%） | 873.4 → 978.2 | 1.570 → 1.557 |
| 药水 / NoGC 4 GB | 1301.92 → 1290.02（−0.91%） | 5113.8 → 5534.8（+8.23%） | 153.0 → 0 | 1.999 → 2.823 |

四组全部保持相同 ACTION/TURN、评分、工作量和非时序剪枝。没有把分配下降称为整体提速：这些短搜样本中时间反而增加。Smart 跳过两次层间整理消除了该窗口的暂停，但峰值增加约 0.82 GB；4 GB 申请预算是上限配置，不能把没有用满预算当作峰值没有代价。

对照的 eager 分页版同样各三次：大牌组分配减少约 14.5%，药水分配增加 0.2%～1.4%，药水耗时增加约 7%～10%；因此没有保留其每次重建都立即分页的实现。延迟分页恢复了药水的分配收益，尚不能仅凭此断言 CPU 改善。

这些是被拒候选的数据，不是最终生产候选。更长的交替反例见第 2 节；最终保留方案见下节。


## 8. 最终生产候选与结论

最终候选保留 StateStore 简化、空 dirty 快路径、历史快照、按余量准入、前缀释放、有界 scratch 池、GC 指标与 Smart 预测，以及第 2 节只读判型修复。listener 已完整恢复基线；普通 GC 自适应与 P2 内核仅归档为实验。全部有限方案已实现或原型评估，不能安全直接移植的通用 COW、typed buckets、dehydrated replay 给出了契约与成本原因，没有把未采用写成已上线。

最终 `GC36-FINAL-BOUNDARIES` Passed，runId `9c4b36665ce240f185e4c722c024ff23`，包括完整 Fork/历史所有权、请求快照、取消计量和 DOP1/DOP2 固定预算等价检查，实际 DOP2 并发至少为 2。只读判型另有两步 native 差分与兄弟隔离检查，见第 2 节。这些检查不依赖被移除的 listener 实验。

### 大牌组长搜：三轮分配与时间均改善

普通 GC、DOP4、每 solver 2500 节点；两侧每轮请求均为 **5000 展开 / 19065 转移**，完整 ACTION/TURN、评分、战损、选择预算和非时序剪枝全部相同。表中为三轮中位数：

| 指标 | 冻结基线 | 最终候选 | 变化 |
| --- | ---: | ---: | ---: |
| 累计 worker 分配 | 2,804,873,992 B | 1,829,391,504 B | −34.78% |
| 累计搜索时间 | 13,672.35 ms | 10,484.74 ms | −23.31% |
| 总 GC 暂停 | 3,007.05 ms | 1,280.34 ms | −57.42% |
| 进程 VmHWM | 2,116,866,048 B | 1,684,283,392 B | −20.44% |

基线三轮原本与被拒的 listener 候选交替运行；最终候选是在定位并回退后连续运行三轮，因而不是最终代码与基线的同期随机交替试验。每次冷进程且固定工作量，分配下降有直接证据；23.31% 的时间收益只描述本机这组 headless 研究样本。candidate4 仅撤回 listener、尚无只读判型修复的单次分配为 2.801 GB，说明最终大幅下降主要出现在判型修复之后；没有单独隔离每个小优化的整搜收益。

### Smart：暂停改善伴随更高峰值

公开药水 fixture、NoGC 4 GB、DOP4、每 solver 576 节点，每侧三轮；请求均为 **1728 展开 / 22541 转移**，全量路线、评分与剪枝等价。

| 指标 | 冻结基线 | 最终候选 | 变化 |
| --- | ---: | ---: | ---: |
| 累计 worker 分配 | 1,301,921,680 B | 1,295,503,440 B | −0.49% |
| 累计搜索时间 | 5,113.78 ms | 5,074.94 ms | −0.76% |
| 总 GC 暂停 | 152.96 ms | 0 ms | 本窗口无暂停 |
| 进程 VmHWM | 1,998,553,088 B | 2,823,548,928 B | +824,995,840 B |

最终每次仅 start 一次，无 forced collection、restart 或 loss；两次层间预测均允许继续。耗时基本持平，不能将省去 reset 表述为显著提速。未回收的垃圾仍占用 NoGC 预算，因此约 0.825 GB 的峰值上升是本方案的实际代价。

合法最低配置 **1 GB / DOP8** 的单次压力检查同样保持 1728 / 22541 与全量路线/剪枝相同，累计时间 5236.77 ms、分配 1,279,327,296 B、暂停 160.36 ms、VmHWM 1,963,995,136 B。两个层间预测均因 `forecast_exceeds_remaining` 回收，scope 记录 forced=2、start attempts/success=3/3、end attempts/success=2/2、restart=2、loss=0。这验证了低余量时仍会回收，不能把单次结果与 DOP4 中位数当作纯 GC 预算 A/B。

波次日志同时受 frontier 宽度与 parent 过滤影响，`admitted_parents < desired_parents` 不能单独归因为内存缩容。本场景未提供 `after_prune` 实际触发证据；饱和容量和 0/1/部分 wave 边界由实际纯函数检查覆盖。

此前尝试 0.6 GB 的请求在设置校验阶段 Failed（合法范围 1–256 GB），runId `a68a6be47d404e7195e45d3fa47af7d8`。没有启动搜索，没有扩大请求超时，也不把失败记录计入性能统计。

普通 GC 药水场景还运行最终候选单次哨兵：工作量/路线比较见机器证据；该单次仅用作后续并行度实验的相同代码对照，不宣称跨场景稳定时间收益。

### 交付边界

研究结果足以支持将通过验证的生产改动提交为 draft PR，并明确 Smart 的空间代价。未验证正常可见 Steam 会话、Windows 游戏、完整自动部署与 EventPipe allocation/retained-heap trace；因此不作玩家可见卡顿或所有遭遇普遍提速结论。外层前缀释放仍不能消除单 parent 原始候选的完整驻留，P2 需要后续统一实体表示和真实 Hook 回放验证。

逐轮数值、runId 和比较口径见 [机器证据](gc-issue36-results.json)，原生 Bash/PowerShell 重现步骤见 [重现说明](gc-issue36-reproduce.md)。


## 9. 普通 GC 自适应并发：真实探测后不启用

[ExperimentalAdaptiveGc](../../tools/ExperimentalAdaptiveGc/README.md) 保存唯一控制器源码与真实接线补丁；生产 `Phases` 已撤销该接线，控制器不再编入 Mod。15 项合成检查继续直接编译归档代码。

最终生产候选上的两个普通 GC 实验，各一次冷进程，均完整比对路线、评分、工作量与非时序剪枝。大牌组2500节点两个 solver 分别产生33/22个完整窗口（共1304个已完成wave，1211个达到目标宽度的样本），没有触发探测。药水576节点三个 solver 有10/18/7个完整窗口，最后层出现一次4→2降核探测：bytes/transition约52,382→52,026，GC duty约0.258→0.146，但吞吐比仅0.638。控制器按规则拒绝并恢复4；各 solver 正常结束，无悬而未决探测、未知内存或中断wave。

完整窗口最少100ms/128转移，排除自然不足宽度，并对大于25%的分配复杂度变化重建比较窗口。真实接线测量的是派发parent宽度，而非CPU实际同时运行线程；跨深度的CPU成本仍可能不同。因此0.638是该次顺序探测的观测比值，不能视为严格随机A/B的降核因果幅度。

大牌组累计时间11,114.48ms，相对同代码未启用三轮中位数10,484.74ms为+6.01%；药水5,994.79ms，相对同代码单次5,893.53ms为+1.72%。分配分别约+0.10%/+0.06%，没有净收益证据。单次不证明稳定退化；结论是本轮不启用自适应。所有窗口计数与决策数字保存在机器证据中，避免将“没有探测日志”误写成有效性证明。
