# CombatSolver 小改动性能研究

## 结论与证据范围

当前最值得投入的方向是消除高频路径上的重复工作：排名期间重复求值、没有状态时的枚举器分配、读取一个标量却先创建状态对象，以及 Fork 辅助字典的扩容。前三类通常只涉及一个消费者或一个局部生命周期；相比替换模拟器，它们更容易保持动作、随机数、状态键和搜索政策一致。

本报告以 `perf/surgical-fixes-20260912` 的 `f592bae` 为源码基线，检索日期为 2026-09-12。文献用于解释机制，源码用于确认切口，本轮独立 .NET 9.0.19 探针用于确认局部分配。没有修改生产代码，没有运行本轮游戏 A/B 或可见 Steam 性能测试，因此没有新增的整场提速、峰值内存或帧时间结论。上一轮的完整结果见[精简移植报告](surgical-fixes-20260912.md)。

优先级按“潜在总收益 × 调用频率 × 等价性可验证程度 ÷ 修改和维护成本”判断。频率尚未采集的项目明确保留未知，不能把局部节省乘全部 transitions，更不能把文献中的加速比套到本项目。

| 优先级 | 候选 | 源码切口 | 目前证据 | 下一步判定量 |
|---|---|---|---|---|
| P1 | 每次排名只计算一次 Beam 分数 | `BeamRetentionPolicy.BeamRankScore`、`RankDeferredCandidates` | 排序比较双方反复执行同一公式 | 实际调用数、CPU 成本、缓存净成本 |
| P1 | 空状态在创建迭代器前返回 | 两个 Card Hook 的 `CompleteOrAbort` | 生产 `ReadEntries` 独立探针：96 → 0 B/空调用 | 空调用次数、分配占比 |
| P1 | 缺失状态直接读取原有标量投影 | 遗物 `AppendFingerprint` 等 Peek 消费者 | 简单 int 状态探针：24 → 0 B/缺失读取 | 缺失比例、原生/影子取值合同 |
| P1 | Fork 计数表按有效类型数预分配 | `PredictionStateStore.Fork` | 8 类型构造：992 → 440 B | 有效类型分布、零计数项数量 |
| P2 | 少量类型内联计数，较多时回退字典 | `_countByType` | 机制合理，尚无本轮候选实现 | 额外对象大小、查询成本、溢出比例 |
| P2 | 重复 key 更新只查一次字典 | `IncrementCount`、状态去重 | 官方提供 ref API；尚未测量 | 实际重复查找占比、引用有效期 |
| P2 | 单次保路块缓存组统计 | 路由/牌堆分组 Max/Min | 部分已有，剩余需定向计数 | 每组重复扫描次数、生命周期 |
| P3 | 局部指纹复用或增量维护 | `StateFingerprintBuilder`、分支状态 | 文献支持方向，当前失效证明成本高 | 重复输入比、全写点覆盖 |
| P3 | deferred 的 top-k | `RankDeferredCandidates` | 确实全排序；同分顺序是阻碍 | N/K、全排序同分行为 |
| P3 | 窄数组、SIMD、JIT 定向优化 | 有值域保证的数组或纯循环 | 需真实热点与布局证据 | 分配、汇编、CPU 计数器 |

## 一、为什么优先减少重复求值

Acar 等人的自调整计算研究把复用已有计算和跟踪依赖组合起来，使输入小变化时不必从头计算。它同时揭示成本：建立依赖结构、维护缓存和 GC 都不是免费的，论文中初次运行显著慢于未插桩版本。对当前项目，适合吸收的是局部复用原则；建设通用依赖图会超过精简分支范围。[^1]

本项目有一个更小的入口。`RankDeferredCandidates` 的比较器每次比较两个节点，都调用两次 `BeamRankScore`。后者读取节点分数、能量、持续增益、潜在铺垫、资源、重放潜力、保留攻击成长、延迟伤害、沙坑、力量压制和虚弱等字段，并执行多次 Min/Max。排序期间同一节点可能参与许多次比较。

设候选数为 N、比较次数为 C，原路径约求值 2C 次。若公式输入在本次排名内不变，预计算可以降为 N 次；排序比较次数不变。假设某个输入实际 C=10N，则公式执行量可少约 95%，这只是条件算术，不是实测速度，更不是整个排序或搜索节省 95%。

建议先给一个排名入口记录 C、公式调用数及候选数，再用采样判断公式是否真的占 CPU。第一候选应保留原 `List.Sort`、原列表顺序、原比较器返回值与原截断操作，只替换公式的重复取值。可以探索本次调用的标量缓存或已有排名元数据槽；新增字典查询、对象字段、临时数组都必须计入成本。不能为所有长期存活节点永久增加字段，却只测一小段排序的收益。

缓存有效期只覆盖已经冻结输入的排名调用；`_run` 初始字段、节点 Snapshot、Score 和 solver 配置都是依赖。不要缓存可变的父排名，也不要把一个 solver 的分数带入反事实 solver。比较必须保留 double 原始计算次序和 `CompareTo` 语义，包含同分、NaN、无穷值等边界。精确求值复用不需要修改决策质量目标，但仍需真实候选集的完整输出顺序验证。

## 二、空路径仍然可能分配

`PredictionStateStore.ReadEntries<TState>` 是 `yield` 方法。内部虽然先判断 `HasEntries`，调用方拿到的迭代器对象仍可已经创建。源码中 `SynchronizePowerAmountPredictionStates` 已在外部进行 HasEntries 检查，该路径不应再算潜在新增收益。

相邻的两个入口尚直接枚举：`BeforeCardPlayedMirrors.CompleteOrAbort` 查询 `CardPlayPairPredictionState`；`AfterCardPlayedMirrors.CompleteOrAbort` 查询 `PaelsLegionPredictionState`。可先在调用侧检查是否存在该类型；空时不调用迭代器，非空时继续原遍历。只改这两个消费者比替换通用枚举器合同更小。

本轮探针直接编译当前生产 `PredictionStateStore.cs`，以最小模型替身测量。每项预热 10,000 次，随后三块各 100,000 次；使用 `GC.GetAllocatedBytesForCurrentThread`，不把时间作为结果。空枚举三块均为 96 B/调用，外部 guard 均为 0 B。这个结果证明该运行时下空路径存在可避免分配；不证明真实游戏中两类状态的空命中率，也不覆盖 Hook 的完整生命周期。

若某请求实际有 100 万次这类空调用，算术上对应约 91.6 MiB 累计分配；只有在两个调用点都确实各空调用一次时，才可以累计两份。该数字既不是已测请求，也不是 RSS 减少。非空时多一次类型查询可能增加成本，需分开测空/非空分布。

## 三、用原有标量投影避免临时状态对象

`Peek` 缺失时执行工厂，但不把结果放入状态表。这保护了 Fork 成本，却不自动避免临时对象。`RelicPredictionStateSupport.AppendFingerprint` 的一些分支只是读取新对象中的一个 bool 或 decimal 字段；相应构造器仅从原有模型投影赋值。

例如 `BurningSticksPredictionState` 构造器赋值 `WasUsedThisCombat`，`BeatingRemnantPredictionState` 赋值 `_damageReceivedThisTurn`。候选可以沿用已存在的 `CounterValueReadOnly` 风格：先 TryGetReadOnly，存在则读取分支状态，不存在则读取与原构造器完全相同的投影。必须证明该模型是合法的根/分支来源、缺失期间原路径本就允许读取它；不能因省分配新增 worker 对变化中的 live 状态的读取。

独立探针使用简单 int 状态。缺失 Peek 为 24 B/次，直接标量读取为 0；存在状态时双方均为 0，且读取已写入的 29 而非模型初值 17。探针只验证这一容器读取机制，不能代替真实遗物值、别名和 Fork 隔离测试。decimal 状态对象大小可能不同，不能把 24 B 当成所有状态的统一节省。

先选择一个只有单字段、构造器无副作用、调用频率高的指纹消费者。检查初始缺失、已产生状态、移除后缺失、同根两代 Fork 和续用文本。具有事务、集合或多个内部依赖的状态暂不纳入。缓存一个全局默认状态对象并不等价，因为默认投影可能随根和分支不同。

## 四、辅助计数表：先消除扩容，再决定是否换容器

当前 Fork 已按 Count 预分配主状态字典与别名字典，而 `_countByType` 在遍历有效项时从空字典逐项加入。官方 Dictionary 容量构造器提供避免后续扩容的入口，但最合适容量必须由真实有效元素数决定。[^2]

本轮只隔离测量相同 `Dictionary<Type,int>`、相同类型序列的两种构造方式，结果如下。三块结果完全相同；未执行生产 Fork，不能声称完整分支隔离已验证。

| 有效类型数 | 逐步增长 | 指定容量 | 少分配 | 局部下降 |
|---:|---:|---:|---:|---:|
| 1 | 216 B | 216 B | 0 B | 0% |
| 2 | 216 B | 216 B | 0 B | 0% |
| 4 | 464 B | 328 B | 136 B | 29.3% |
| 8 | 992 B | 440 B | 552 B | 55.6% |
| 16 | 992 B | 608 B | 384 B | 38.7% |

8 和 16 的结果不是线性关系，因为增长过程与容量取整不同。真实 `_countByType` 保留零计数项，而 Fork 只复制非零项，直接用字典 Count 预分配可能过大。可研究“先计数有效项，再一次分配”，比较额外扫描和扩容节省；不能把上表的 8 项结果套到含很多零项的表。

若真实分布集中在 1–2 类型，预分配没有收益。此时才值得测试“内联一两个 Type/count 槽位，溢出回退 Dictionary”。该方案会增大每个 StateStore，即使从未使用状态表也有成本。需要同时计量空表比例、溢出比例、零计数删除、类型比较、Fork 后修改及父子隔离。直接使用小数组也会创建数组对象，并不自然优于内联字段。

另一个很小的候选是 `IncrementCount` 的读取加写回。`CollectionsMarshal.GetValueRefOrAddDefault` 可以减少重复查找，但取得 ref 后不能发生使字典结构变化的增加或删除。[^3] 这里比 `Get` 工厂入口更合适：不能持有主状态字典 ref 跨过工厂调用，因为工厂可能重入并扩容。先测这个子路径，避免为一次 Type 哈希引入不必要的底层 API。

## 五、排序算法与同分行为

`RankDeferredCandidates` 明确先 ToList、全量 Sort、再 RemoveRange；当 N 远大于 K 时，部分选择存在算法空间。但当前 `List.Sort` 是不稳定排序，不能把“同分按原输入顺序”当作现有合同。[^4] 换成稳定 LINQ、堆或加入原索引作为新 tie-break，可能改变截线代表与后续路线。

正确的第一步是保存真实输入和现有排序输出，覆盖高同分比例、重复对象、NaN、正负零以及 K=0/1/N 等边界。先做公式缓存，之后才考虑部分选择。即便 top-k 集合相同，若其顺序不同，也可能影响后续预算和展开次序。

另一项容易误判的内容是 LINQ。核对 .NET 9 源码可见，`OrderBy().First()` 有扫描选最优的专用路径，`OrderBy().Take(k)` 有区间排序入口，并非所有组合都先完成全量排序。[^5] 因而本文件多处 `group.OrderBy(...).First()` 不能直接记作 O(N log N) 浪费。可能还有迭代器、委托、分组缓冲成本，但必须单独量化。

`RankBest` 在普通排名后还会消费完整池完成保路和配额，不能用一个普通 top-k 整体替换。其分组 Max/Min 可以研究单次调用内的标量缓存；已有 `RoutingChoiceNodes` 冻结统计的实现不应重复计算为新增机会。

## 六、指纹、不可变共享与增量算法

Zobrist 的原始技术报告提出面向棋类的散列方法，其启发是状态局部变化时可以局部更新编码。[^6] 但当前 `StateFingerprintBuilder` 使用两条逐项混合链，后项依赖此前结果，并不是简单 XOR 聚合。不能直接从最终哈希减去旧字段、加上新字段，更不能把旧键算法替换成较短哈希后声称严格等价。

Hash-consing 研究讨论结构共享、输入共享和哈希记忆化如何减少重复处理结构数据。[^7] 本项目更接近的小切口是复用已证明不变的元数据投影，例如规范 Model 的固定字段；先统计重复对象及重复字符串的处理成本。字符串短、对象少时，缓存查询本身可能更贵。

如果某个有序牌堆对象完全不可变，可以研究缓存其派生摘要。但牌堆存储不变不代表其中卡牌的费用、升级、动态变量也不变。必须把内容变更依赖一并纳入。对于状态指纹改变组合结构的方案，还要审计去重、转置、调度签名和持久化消费者，不能仅凭最终 HP 相等判断安全。

更大胆的候选是把昂贵的纯派生值延迟到真正消费时计算。然而必须证明被省略的计算没有验证、异常或状态物化副作用；不能提前按不完整状态判定重复候选，也不能跳过现有必经合法性检查。当前尚未发现一个可以不扩展语义边界就省掉整个 Fork 的新入口，因此不把“提前去重”列为可立即合入的方案。

完整的自调整计算或全局 hash-consing 需要维护依赖与存活关系，已有文献也讨论内存管理成本。它们适合作为设计参考，不适合把本分支重新带回第二套状态后端。

## 七、类型、编译器与硬件

类型压缩应优先选择连续数组中的值，而不是孤立对象的单字段。当前 `StateFingerprint` 是两个 ulong；把它缩为一个 ulong 会改变碰撞条件，不能列作无损压缩。HP、格挡、RNG 和 decimal 运算也不能靠经验改窄。对有限集合的布尔标志可以研究位图，但要明确总数、扩展上限、布局和所有序列化消费者。

把 class 改成 struct 可能消除对象，却也会增加复制成本或通过 object/接口装箱。应先观察字段在哪里存放、怎样传参、是否进入集合，而不是按类型声明行数判断。统一 `Pack=1`、全对象紧凑布局或重新安排整个模拟图的成本与验证面都偏大。

运行时方面，Microsoft 的 .NET 9 性能文章说明不少优化来自 JIT、库和 GC 的组合，不能仅靠重新编译 Mod DLL 获得宿主版本升级。[^8] 编译设置由游戏宿主决定，当前 .NET 配置支持分层编译与 PGO 控制，应先检查宿主实际配置和冷/热差异。[^9] .NET 10 的新特性可以用于未来宿主升级比较，目前不当作这个 net9 Mod 可单独启用的增量收益。

`AggressiveInlining` 可能因代码膨胀降低性能；`AggressiveOptimization` 会绕过第一层编译，使依赖第一层的动态 PGO 无法发挥作用，官方文档明确提醒谨慎使用。[^10] 合理试验是选一个采样热点，对比生成汇编与固定工作量，不是给大量短方法统一加属性。

SIMD 优先处理独立的连续数值批次，例如已存在数组上的纯值比较。当前指纹每个元素依赖前一个混合结果，不能简单地把循环换成 AVX2/AVX-512 而保留算法。跨多个独立状态做向量化又需要重新组织数据，暂时超出小改动。手写汇编只有在一个纯热点已经证明受指令序列限制且 JIT/intrinsics 无法改善时再考虑。

GPU 的适用性取决于批量规模、内存传输、访问布局与分支一致性。NVIDIA 指南明确讨论这些约束。[^11] 结合当前模型对象、虚调用、Hook 和分支事务，本报告推断 GPU 需要较大重排；没有本项目 GPU 实测，不承诺加速，也不启动迁移。

## 八、如何把局部收益换成可审核的整体结果

令 p 为被优化子路径的真实耗时占比，s 为该子路径加速倍数，理想整体加速为 `1 / (1 - p + p / s)`。这是串行成本模型；并行重叠、锁、GC 与内存带宽会改变实际结果。若 p=20%、s=3，整体时间理论减少 13.3%，加速 1.154 倍；如果 p=1%，即使完全消除也最多省 1% 时间。

分配单独核算：`节省字节 = Σ(命中次数 × 每次实际节省) - 新增缓存/元数据分配`。峰值保留内存还要考虑同时存活的对象、缓存容量与生命周期。缓存可能降低累计分配同时提高峰值，不应只报告好看的一项。

建议下一轮只试一个因素，按下面顺序进行：

1. 在真实短搜记录两个空枚举入口、标量 Peek 缺失、Fork 有效类型数和 BeamRankScore 调用次数。诊断计数不改变搜索政策，详细诊断运行的时间不作为性能数据。
2. 按实际成本选一个候选；先验证目标接口的原行为，包括别名、缺失/存在、父子分支、同分排序和异常边界中受影响的部分。
3. 固定相同根、RNG、DOP、节点预算和编译配置，预先安排交错 A/B 与首尾基线。比较完整动作、评分、非时序剪枝指标和工作量，不通过少算候选换取速度。
4. 用对应哨兵检查质量，再以正常可见 Steam 会话给出最终耗时、GC、峰值内存与帧时间结论。若差异低于基线波动，记录收益未建立并撤回候选。

推荐的工作优先级是：先以空枚举/预分配找到最小可验证增量，同时测量排名重复求值是否足以成为主要收益来源。排名缓存和标量投影比再次压缩一个列表包装更值得测；全量增量指纹、GPU 和通用状态重写暂不进入实施队列。

## 可复跑证据

探针源码位于 [SurgicalResearchChecks](../../tools/SurgicalResearchChecks/README.md)，原始结果见 [results.json](../../tools/SurgicalResearchChecks/results.json)。生产 StateStore 直接作为链接源码编译；模型和 Fork 上下文使用最小替身，Fork 方法显式抛出异常。测量只覆盖分配机制和简单读取值，未声称测试真实模型、生产 Fork 或完整战斗。宿主是独立 Linux x64 .NET 9.0.19，与游戏进程分开。

## 文献与官方来源

[^1]: Umut A. Acar, Guy E. Blelloch, Matthias Blume, Kanat Tangwongsan. [An Experimental Analysis of Self-Adjusting Computation](https://www.cs.cmu.edu/~guyb/papers/ABBT06.pdf). PLDI, 2006，尤其第 1、3、6 节。用于局部复用、依赖维护和初次运行成本分析，未借用其加速比预测游戏。
[^2]: Microsoft. [Dictionary<TKey,TValue> Constructors](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2.-ctor?view=net-9.0). .NET 9 文档，访问于 2026-09-12。用于容量构造语义，具体字节数来自本轮探针。
[^3]: Microsoft. [CollectionsMarshal.GetValueRefOrAddDefault](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.collectionsmarshal.getvaluereforadddefault?view=net-9.0). .NET 9 文档，访问于 2026-09-12。用于单次查找 API 和 ref 生命周期限制。
[^4]: Microsoft. [List<T>.Sort](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.sort?view=net-9.0). .NET 9 文档，访问于 2026-09-12。用于排序不稳定性说明。
[^5]: .NET Foundation. [OrderedEnumerable.SpeedOpt.cs](https://github.com/dotnet/runtime/blob/v9.0.7/src/libraries/System.Linq/src/System/Linq/OrderedEnumerable.SpeedOpt.cs) 与 [OrderedEnumerable.cs](https://github.com/dotnet/runtime/blob/v9.0.7/src/libraries/System.Linq/src/System/Linq/OrderedEnumerable.cs). 固定 v9.0.7 源码；用于 First 专用扫描与区间排序的实现事实，未将该补丁版本当作本机运行时版本。
[^6]: Albert L. Zobrist. [A New Hashing Method With Application for Game Playing](https://research.cs.wisc.edu/techreports/1970/TR88.pdf). University of Wisconsin, Technical Report 88, April 1970；[馆藏记录](https://minds.wisconsin.edu/handle/1793/57624)。用于增量散列方向，不提供本项目无碰撞保证。
[^7]: Neng-Fa Zhou, Christian Theil Have. [Efficient Tabling of Structured Data with Enhanced Hash-Consing](https://arxiv.org/abs/1210.1611). 2012。用于结构共享、输入共享和哈希记忆化方向；项目适用性属于工程推断。
[^8]: Stephen Toub / Microsoft. [Performance Improvements in .NET 9](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-9/). 2024-09-12。用于 JIT/库/GC 优化分类与宿主版本边界，不引用其微基准为本项目收益。
[^9]: Microsoft. [Run-time configuration options for compilation](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation). 访问于 2026-09-12。用于分层编译、PGO 及宿主配置入口。
[^10]: Microsoft. [MethodImplOptions Enum](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.methodimploptions?view=net-9.0). 访问于 2026-09-12。用于内联和 AggressiveOptimization 的限制。
[^11]: NVIDIA. [CUDA C++ Best Practices Guide](https://docs.nvidia.com/cuda/cuda-c-best-practices-guide/index.html). 访问于 2026-09-12，Memory Optimizations、Control Flow 章节。用于传输、布局和分支分歧约束；本项目 GPU 可行性为源码结构推断。
