# 现有战斗后端架构审查（2026-09-08）

[返回性能目录](../../performance/README.md) · [结构化证据](backend-architecture-audit-20260908.json)

范围：认真检查现有后端，不大规模实现新后端。检查源码为`9076b19`，生产逻辑与`b168b68`相同。覆盖稳定根→Fork→卡牌/药水/选牌回放→结算补偿→快照/评分的主链；不是全库正确性审计。朋友的同算法、同级节点量后端数据由用户提供，本次没有其源码或共同输入实测，不能计算两实现的准确差距归因。

结论：当前仍有适合局部改进的工作，优先是**按回调直接定位监听成员**和**单个归一化族的精确失效标记**。通用Power/COW、任意选牌点恢复、动作撤销和新状态内核超出本次范围。没有得到新的提速倍率，也没有将旧实验重新包装为新方案。

> 后续实测：以上优先级基于调用计数，未证明CPU收益。[真实CPU采样及局部修复](backend-cpu-hotspots-20260908.md)已撤回两种回调索引，修复SwordSage根基线，并将后续重点调整到快照和有效监听列表重建。此处保留原审计结论的历史时点。

## 本次直接证据

新建独立headless进程，仅执行一次带临时计数的固定短搜：`ba7606885e0a4de4814747b5b22452c1`，Passed；极高/DOP8/NoGC配置16GB、首结果停止、120秒上限。10,000展开、144,368转移、101,808选择，Short/NodeLimit、投影战损3，与已有固定短搜的这些指标一致。计数从coordinator的Solve进入时开启，退出且worker完成后汇总各线程计数；不计主线程根建局。完整指标和20个计数的定义见JSON。

计数会增加开销，且这是冷运行，**11.265秒不用于和此前5.932秒预热基线比较**。本次没有新A/B，也没有由这些粗指标推断完整路线/状态等价。

| 观察项 | 一次固定短搜的计数 | 含义 |
|---|---:|---|
| Simulator Fork / Snapshot | 各144,370 | 包含根等额外操作，不严格等于转移数 |
| 卡牌包装 Fork | 6,851,214 | 平均每转移约47.46个包装；不是同量完整CardModel深克隆 |
| ForkPower调用 | 1,567,382 | 含TryRemap命中可能，不当成精确深克隆数 |
| CloneCardStateForSimulation | 114,062 | 只计此入口，不覆盖其他生成/原生克隆入口 |
| NormalizeCardAfflictions | 305,949 | 每转移约2.12次 |
| 三段归一化牌循环 | 各14,590,762 | 合计43,772,286次访问，平均每转移约303次 |
| 基础/有效监听表构建 | 194,761 / 265,147 | 是缓存miss路径；没有证明全部可以省略 |
| 新建镜像类型布局/条目 | 232,688 / 22,633,410 | 类型或成员序列变化会重建 |
| 已过滤Hook位置检查/成员交出 | 169,490,742 / 3,615,508 | 约46.88次位置检查才交出1个成员 |
| 入参为PendingChoice的Snapshot | 4,898 | 仅占Snapshot约3.39%，不等于所有选择回放成本 |

Hook两项只计`MirroredHookListenerSnapshot`内的位图循环，不覆盖未过滤/原生/第三方枚举；“交出成员”也不代表回调一定执行完成。97.87%的位置检查因该回调位图不匹配而跳过，是**循环检查数量**，不是97.87%的运行时间。

另对上一轮25秒EventPipe采样离线重分组：只取调用栈含CombatBeamSolver的GCAllocationTick，采样估计15.93GB。按Snapshot优先于其内部Fork、其余Fork优先于Replay的规则，分为不重叠的快照13.77%、Fork29.72%、其他回放36.25%、Beam保留10.35%、其他搜索9.91%。这是部分运行的**分配采样估计**，不是墙钟/CPU比例，也不能按比例分摊另一场35.4GB精确分配。没有重新跑同一性能阶段。

## A0：一处直接读取实机能力数量的路径

[SimulatedCombatState.PowerLifecycle.cs:243](../../../src/Search/SimulatedCombatState.PowerLifecycle.cs#L243)在归一化时读取`player.Creature.GetPower<SwordSagePower>()?.Amount`。Player来自根保存的身份引用；分支期第一次遇到尚未登记的非克隆SovereignBlade时，这个值参与`BaseReplayCount`差值计算。后续即使初始化标志已置位，方法仍先做这次live读取。

[SimulatedCombatState.cs:304](../../../src/Search/SimulatedCombatState.cs#L304)已经在主线程按owner/type保存了能力数量；Fork共享该不可变根值。可在现有类内把首次基线改为根捕获值，并避免后续live读取，不需要新后端。必须区分初始卡与之后新生成卡的初始applied值；普通能力和多实例的数量口径也要核对。

本次静态确认了数据路径，**未构造“捕获后实机变化”的差分复现**，不声称已观察到错误战损或并发失败。建议先做对应根隔离合同并修复该小边界；它是正确性事项，不承诺明显性能收益。

## A1：默认空回调已跳过，但仍逐槽寻找参与者

[HookMirrors.cs:1286](../../../src/Engine/InCombat/Mirrors/HookMirrors.cs#L1286)在每次相关回调中扫描布局条目，逐项检查位图。已有整体`HasAny`快速空路径；当至少一个成员参与时，仍扫描中间大量不参与的位置。计数显示这是一个频繁发生的结构成本，而不只是几个LINQ分配。

**建议的有限改动**：只在既有不可变`MirroredHookListenerLayout`内，为实际使用的回调按需保存有序位置索引；枚举器按这些位置访问当前分支Model。只涉及布局和枚举器，不改变Power/卡牌模型、效果实现、搜索算法或成员顺序。

必须保留：重复成员、原生位置顺序、当前分支模型引用、每次MoveNext的PendingChoice停止、第三方/动态类型与基方法补丁旁路。索引不能保存父分支Model；按需索引本身会分配，布局本次重建23万次，因此必须限制构建成本、测内存和复用次数，不能预建所有回调的数组。计数只支持优先验证该方案，不支持保证某个倍率。

这与上一轮失败的“根内缓存整张类型布局”不同：后者想减少布局分配，本项针对**回调每次枚举时的空位置检查**。也不能把本次新建布局次数全部视为可避免，因为真实成员/类型变化确实要求重建。

## A2：归一化依赖粒度太粗，适合从一个族局部收紧

[SimulatedCombatState.cs:1374](../../../src/Search/SimulatedCombatState.cs#L1374)先处理卡牌附着效果，再调用Power相关归一化。[SimulatedCombatState.PowerLifecycle.cs:158](../../../src/Search/SimulatedCombatState.PowerLifecycle.cs#L158)与SwordSage归一化各自又遍历所有牌。当前至少三段每次全牌堆循环；还存在GhostSeed等其他路径，本次没有把它们计入三段总数。

调用位于Card效果补偿、生成卡入场及[CombatBeamSolver.Expansion.cs:2954](../../../src/Search/CombatBeamSolver.Expansion.cs#L2954)等不同结算点。两点之间可能有死亡效果、附着效果改变、自动出牌或嵌套选择，因此“看上去重复”不能证明可以删除调用。

**建议的有限改动**：先挑一个归一化族，维护这个族的依赖版本/待处理卡集合，保留现有调用时点，只在依赖未变化时快速返回。优先评估来源数量、牌的进入/离开、类型/附着效果变化均能明确跟踪的族。首次根处理、新生成卡、升级/变形、能力归零后的清理、跨回合和嵌套选择都必须触发恰当失效；不能只看“当前没有对应Power”就跳过历史残留效果的移除。

本次计数的是访问次数，**没有记录每次访问是否实际写入**，所以不称4380万次全部无效。需要在选定族增加变化命中统计及最小生命周期差分，再做单因素固定工作量A/B。

## A3：Fork昂贵，但并非整副原版牌每次都深拷贝

[CombatPredictionSimulator.cs:151](../../../src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs#L151)复制基础状态、状态仓库、RNG和历史。[PredictedCard.cs:150](../../../src/Engine/Common/PredictedCard.cs#L150)已有预览COW：普通牌Fork创建分支包装，共享PreviewStorage，写时才复制模型；第三方附着模型隔离另走保守路径。[CombatPredictionHistory.cs:438](../../../src/Engine/InCombat/Simulation/CombatPredictionHistory.cs#L438)已有不可变历史前缀共享。纯值集合也已使用Forkable容器。不能再把这些已经实现的机制作为新方案推荐。

仍需要每个分支拥有卡包装的OwnerPile与mutation observer、Power实例和对象映射。[SimulatedCombatState.Fork.cs:271](../../../src/Search/SimulatedCombatState.Fork.cs#L271)先TryRemap再深克隆，防止同一对象多重复制。[PredictionStateStore.cs:70](../../../src/Engine/Common/PredictionStateStore.cs#L70)即使名为GetReadOnly，返回的仍是可变state对象，而且缺失时会登记新状态；调用者可能已借出引用。RNG属性也直接返回可变Rng。对这些API直接套通用COW会遗漏写入屏障或破坏别名。

因此本次不提议共享所有Power、池化模拟器、替换全部字典或改造为动作撤销执行。局部容器优化可以继续，但之前的空变量共享仅省约3%分配且无明显时间收益；它不足以支持重新扩大克隆改造面。

## A4：选牌的公共前缀重复，已确认；任意中点续执行不属于小修

[CombatBeamSolver.Expansion.cs:1442](../../../src/Search/CombatBeamSolver.Expansion.cs#L1442)从稳定父节点重新执行带已确定选择的动作。每种选择都能重复执行付款、前置回调等公共前缀；本轮101,808选择分支不能等同“重复执行完全相同的动作”。上一轮完整状态+完整动作缓存键重复率约2.52%，也不能用来否定**不同动作选择共享前缀**的可能性。

当前PendingChoice让同步镜像退出，保留选择描述而不保存可继续运行的调用栈；Fork明确禁止复制活动卡牌、选牌、死亡等事务。把挂起状态直接当普通节点继续执行会缺失后续回调位置和局部变量。通用续执行需要明确的阶段/continuation协议，属于较大结算改造，本次保留现有回放方式。

轻量probe快照与中点续执行是两回事。上一轮省PendingChoice完整快照只有约3.8%短搜收益、分配省约26MB，已撤回。本次PendingChoice入参Snapshot仅4,898次，也没有新证据支持立即重启同一实验。

## A5：快照/评分有成本，但不能随意晚算或提前剪枝

[CombatBeamSolver.StateEvaluation.cs:735](../../../src/Search/CombatBeamSolver.StateEvaluation.cs#L735)每次构建投影洗牌序列；牌/牌堆指纹与临时列表复用已经存在。投影值和顺序键参与多种路由和保留比较，节点释放模拟器后仍可能作为父链元数据被读取。它不是纯展示信息。

可以审计一个纯派生组件的完整依赖，复用未变化的结果；但不能只按卡牌ID缓存，也不能让快照懒读取一个已释放或被修改的模拟器。把转置移到完整快照之前还会涉及候选准入顺序、标签和预算，不能视为后端的等价局部优化。本次不改评估公式或剪枝顺序。

## 可执行的后续顺序

| 顺序 | 范围 | 进入条件/验收 |
|---|---|---|
| 0 | 根SwordSage数量读取的小修 | 根捕获后live变更、初始/新生成牌及Fork隔离合同 |
| 1 | 现有Hook布局/枚举器 | 有序索引的命中与构建成本；位图、重复项、第三方、补丁、暂停和Fork合同；同工作量A/B |
| 2 | 一个归一化族 | 先记录真实变化比例；完整失效边界；对应L1/L2差分及同工作量A/B |
| 3 | 单个派生评估组件 | 依赖清单和所有消费者核对后才试；禁止评分或保路退化 |
| 暂缓 | 通用COW、任意选牌续执行、动作撤销、新状态内核 | 超出用户限定，本次不实现 |

优化候选只有固定工作量明显改善才保留，再做受影响的质量/并行合同。不得把位置检查减少46倍称为搜索快46倍，不预估没有测量支持的10%/50%或毫秒级结论。

## 产物与验证边界

临时计数版Release编译通过，0警告/错误，`CopyModOnBuild=false`。独立请求完成后停止所属headless实例；11个临时改动文件全部恢复，计数helper删除。只提交本报告、JSON及文档索引/测试记录，没有生产源码、规则、职责迁移或测试协议改动，没有更新原生安装和用户设置。

下面是**相同建局/预算的普通搜索命令**；当前生产代码不含临时计数，单独执行它不会输出BACKEND_AUDIT。精确计数点和启停范围见JSON；临时patch/reader/原始采样保留在本地诊断目录，不纳入源码提交。

```bash
./tools/run-unattended-test.sh --scenario-id VH-BACKEND-AUDIT-BASELINE --character-id NECROBINDER --seed SEARCH_PERF_NECROBINDER_POTION --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2 --enemy-current-hp 526 --initial-player-hp 41 --initial-player-max-hp 76 --clear-run-deck --run-cards-path coverage/unattended/search-performance-necrobinder-projected-run-cards.json --relics-path coverage/unattended/search-performance-necrobinder-projected-relics.json --potions-path coverage/unattended/search-performance-necrobinder-projected-potions.json --cards-json '[]' --potion-policy-for-test RequireAtLeastOne --performance-preset-for-test VeryHigh --search-max-degree-of-parallelism-for-test 8 --enable-no-gc-region-for-test 1 --no-gc-region-budget-gigabytes-for-test 16 --enable-detailed-diagnostic-logs-for-test 0 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --keep-game-open --force-short-search-only
```

最终仅做文档JSON、代码锚点/链接、计数/工作量和差异检查；不追加生产行为未改变情况下的整场、DOP或完整门禁。未做新的性能A/B、捕获后live变更差分、可见Steam或Windows游戏验证，也没有发布/推送。
