# 内存缩至十分之一：来源、条件与实测

本次研究基于 `465a8cd`，只做诊断和测试配置实验，没有修改生产实现或默认设置。结论：**峰值占用有明显压缩空间；保持当前搜索工作量时，累计分配降低90%需要覆盖多个主要分配路径，单独优化Fork、快照对象或小容器达不到。当前没有十倍实测。**

## 1. 两种目标必须分开

[上一轮正常VeryHigh](hotspot-exploration-20260913.md)死灵药水样例累计worker分配93.423GB、峰值进程RSS29.343GB、1,705,319次转移。两种“十分之一”分别是累计分配约9.342GB和峰值RSS约2.934GB；前者是整个请求创建对象的累计字节数，后者是某一时刻进程驻留内存，包括运行时、游戏和原生内存。

这次固定短搜在相同模拟实现上产生893,527次转移、560,708个选牌分支，累计60,000个展开节点。单次Solve上限10,000，正常coordinator仍有药水审计和窄Beam恢复，所以不能把总工作量写成只有10,000节点。约48.4GB分配折算每转移约54KB，是整搜摊销值，包含估值、保路、探针和请求附加搜索，并非一次Fork的大小。

## 2. 当前代码的真实分配栈

新增诊断 `9dd48d084bf04394bf059735b1934588` Passed。Linux headless，真实BaseLib/Ritsu，DOP16、16GB No-GC；Custom取VeryHigh的Beam135、分支72/42/54，只把节点设为10,000并开启FixedBudget，单请求120秒，保留正常审计/恢复。使用既有正常生产产物，未加入源码计数器。

通过`dotnet-trace collect --process-id <pid> --providers Microsoft-Windows-DotNETRuntime:0x1:5 --buffersize 512 --output allocation.nettrace`附加进程，结果返回后SIGINT停止采集，收集器exit0。TraceEvent解析464,375个AllocationTick，全部有栈，EventsLost=0；其中454,945个栈包含CombatBeamSolver或CombatSearchCoordinator。按AllocationAmount64加权，这部分为48.927GB，与搜索计数器48.400GB相差约1.09%。**这是采样归因估计，不是逐对象精确记账，也不是优化前后性能基准。** 附加前窗口不在采集内，栈过滤也不等同于worker独占计量；生成代码、内联和采样间隔会影响归因。

按Fork→Snapshot→AdvanceRound→ManualPlay→OtherReplay→Retention→Other顺序，每条栈只归一类：

| 分配发生的路径 | 采样权重占比 | 具体来源 |
|---|---:|---|
| 分支Fork | 29.44% | 牌堆包装对象、分支状态、Power/动态变量克隆、监听表重映射及容器 |
| 快照估值 | 22.02% | 卡牌属性/标签查询、战略效果、威胁、可达手牌、指纹及汇总 |
| 回合推进 | 14.56% | 回合钩子、抽牌/状态结算、跨回合临时对象 |
| 手动出牌模拟 | 13.63% | 效果/Hook、生成与选牌、模型变更、历史记录 |
| 保路与剪枝 | 7.98% | 候选数组、分组、排名和相关暂存 |
| 其他回放 | 3.05% | 未进入上述类别的回放辅助分配 |
| 其他搜索 | 9.32% | 动作/选择准备、编排及未分入前述类别的搜索工作 |

关键对象类型：Ritsu `GetCapabilities<ICardPropertyContributor>`迭代器约5.97%，`PredictedCard`约4.11%，`PredictedCard[]`约3.07%，`Int32[]`约2.84%，`AbstractModel[]`约2.75%，`SimulatedCombatState`约2.15%，动态变量Dictionary约1.98%，`SimulationSnapshot`本体约1.12%。两个高频编译器闭包类型约2.61%/2.48%；其主要栈分别经过Ritsu的`GetModifiers`/`ApplyTags`，简名不能当成所有闭包的唯一完整类型身份。

从包含关系另看，`ForkPower`约8.66%、`DynamicVarSet.Clone`约6.94%、`SimCardPile.Fork`约5.92%；这些与上表重叠，不能相加。`ApplyTags`→`StrategicEffectContext.Build`→`Snapshot`是明显重复查询链。它比“快照对象太大”更值得调查，但不能直接缓存跨分支Tags或跳过第三方贡献者。

## 3. 为什么算法会把这些对象放大

**逐分支复制整个已物化牌堆。** [SimCardPile.Fork](../../src/Engine/Common/SimCardPile.cs)为每张卡调用`PredictedCard.Fork`，建立新的包装对象、List和引用数组；[SimPlayerCombatState.Fork](../../src/Engine/InCombat/Simulation/SimPlayerCombatState.cs)覆盖已物化的手牌、抽牌、弃牌、消耗和打出区。因此一次动作只改变几张卡，也会为其余卡重建包装。普通卡牌PreviewStorage已经写时复制，但包装上的所属牌堆仍是分支可变引用；不能把同一个包装浅共享给父子。

历史heap中包装对象为48B；按同类64位布局估算，2305张已物化卡的包装加8B数组引用，单次Fork约129,080B，尚未计列表、Power、其他状态及真实模型克隆。若发生10万次这种Fork，仅该部分累计约12.91GB。这是条件估算，**不是本次2305张样例的实测分配**；当前死灵样例也不具有这一牌数。

**对象图隔离比复制HP昂贵。** [CombatPredictionState.Fork](../../src/Engine/InCombat/Simulation/CombatPredictionState.cs)复制生物/玩家状态表，[SimulatedCombatState.Fork](../../src/Search/SimulatedCombatState.Fork.cs)复制或重映射Power、监听成员和各类分支状态，[PredictionStateStore.Fork](../../src/Engine/Common/PredictionStateStore.cs)还会进入领域自定义Fork。Power克隆包括动态变量集合及其字典。当前[PredictionForkContext](../../src/Engine/Common/PredictionForking.cs)的映射数组已使用ArrayPool，不能把全部引用登记次数都换算成新数组分配。

**先生成、模拟、估值，再判断保留。** [Replay与候选准入](../../src/Search/CombatBeamSolver.Expansion.cs)从父快照分叉并执行动作；很多分配在转置/Beam淘汰之前已经发生。最终只保留少量节点，不代表只为这些节点付费。生成选牌、嵌套选择、回合尾部选牌、stand-pat探针，以及请求级药水反事实都会增加模拟次数；计数口径可能互相包含，不能把选牌数再机械加到转移数上。

**并行窗口和祖先链影响同时存活量。** lane可以同时持有多个正在模拟/待归并的分支；并发数不等于全部存活快照数。[SearchNode.Parent与SimulationSnapshot](../../src/Search/CombatPlan.cs)保留路线和祖先元数据，已淘汰的模拟器有独立释放入口。历史已经采用不可变前缀段共享，[CombatPredictionHistory.Fork](../../src/Engine/InCombat/Simulation/CombatPredictionHistory.cs)不是每次复制全部历史；不能把现有实现描述为全量深拷贝搜索树。

## 4. 实测：先回收能省多少峰值内存

新增 `04991da3f8314bfd9d2db860be7e4c6d` Passed，仅将上述固定工作量的测试No-GC配置设为1GB，使用正常生产产物且未采集EventPipe。与上一轮两个16GB候选样本对照：

| 样本 | No-GC配置 | 累计worker分配GB | 100ms采样峰值RSS GB | 搜索秒 |
|---|---:|---:|---:|---:|
| 既有B1 | 16GB | 48.408 | 19.791 | 44.464 |
| 既有B2 | 16GB | 48.391 | 21.972 | 44.398 |
| 本次单样本 | 1GB | 48.513 | 3.566 | 50.357 |

峰值约缩至原来的1/5.55–1/6.16，**未达1/10**；累计分配没有降低。单样本搜索时间约增加13.4%，不作为稳定回退幅度。旧B1/B2仅在搜索指标捕获后由Writer额外导出路线，运行时机也不同；这不是本轮交错ABBA实验，不能据此发布稳定内存/速度承诺。带EventPipe的16GB诊断峰值17.750GB另列JSON，不用其扰动值做普通配置基线。

四者的33项非时序工作/质量指标一致，包含展开、转移、选择、循环/有序操作计数、评分、HP和药水使用；**本轮没有逐步完整动作对照或native全战部署，不声称完整语义等价**。没有把设置1GB写成进程硬上限。该样本可用日志中的回收后托管存活量为0.109–1.077 GB，低于RSS；GC提交空间、碎片、原生内存和基础运行时仍占空间。JSON保留可用完整日志记录，不将日志条数冒充保证无缺失的总回收次数。

历史`b0d684b`的另一场景，在第3个排空检查点dump中有147.072MB非Free对象载荷、698.278MB Free块；250,525个RuntimeMethodInfo占26.055MB，字符串20.517MB，而2944个SimulationSnapshot本体只有1.837MB。它说明运行时基础对象、空洞和分支图必须分开看；这是旧版本的单点对象大小，既不是当前搜索分配排行，也不是每类对象经GC根保留的整张图大小，不能直接外推到VeryHigh峰值。原证据见[存活图研究](six-directions-20260913.md#4-小区域存活对象单标签转置前沿内联)。

## 5. 什么条件下才可能真正少十倍

累计分配可粗分为“模拟/回放次数×每次平均分配＋其他固定工作”。如果只优化占总分配比例f的部分，使这部分缩小r倍，则剩余比例为`(1-f)+f/r`。即使把某部分完全消掉，f小于90%也无法让整体剩10%。例如覆盖95%的分配路径，还要让这些路径约缩小19倍，才能使总量缩至十分之一。

| 方向 | 接近十倍所需条件 | 当前证据/限制 |
|---|---|---|
| 小No-GC区域、及时排空和回收 | 原峰值绝大部分是未收集垃圾/额外提交空间，基础进程与必要存活对象远低于目标上限 | 本次RSS约少5.5–6.2倍，累计分配不降；GC频率与吞吐需要一起取舍 |
| 未变牌堆/状态共享，按修改部分复制 | 原开销主要随全牌数增长，而每次真正变更很少；完整分配中可压缩部分超过90%或与其他路径共同改造 | 极大牌堆最值得测，但本次死灵Fork只有29.44%，将其全部消除也仅约1.42倍整体收益上限 |
| 紧凑字段、稀疏增量、lane内部可撤销执行 | 普遍避免模型/动态变量/容器重建，同时压低估值、Hook、历史和选择物化成本 | 高成本架构研究；要保持RNG、实例身份、第三方副作用、异常和Fork隔离，尚无实现或十倍证据 |
| 少做重复物理回放、提前证明等价 | 大部分回放确实可合并，且不改变选择身份、历史、策略标签、预算消费和结果质量 | 次数减少5倍再配合单次分配减半，数学上可到10倍；目前只是条件模型，不能靠直接削减Beam/节点实现原质量承诺 |
| 降低并行度/缩小在途窗口 | 峰值几乎全由在途分支组成 | 只减少同时持有量，通常不减少总分配；基础堆和祖先/转置状态不会随DOP等比例下降 |

最值得继续的顺序：先把“目标RSS”与“可接受搜索时间”定为同一工作量的指标；再对2305张牌样例测每次Fork牌数、实际变更比例及包装分配占比，判断牌堆结构共享是否达到值得承担高成本的规模；同时量化Ritsu属性查询及Power动态变量克隆的可省部分。跨分支缓存、可撤销执行和状态表示重构均只列研究方向，本次未实施。

完整指标与采样类型/栈见[结构化证据](memory-tenfold-20260913.json)。本轮仅新增两次headless请求、诊断解析和文档；沿用已有VeryHigh结果，没有重跑四项极端场景、可见Steam、Windows部署或生产构建。
