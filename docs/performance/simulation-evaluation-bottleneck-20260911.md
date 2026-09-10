# 紧凑原型的下一瓶颈：完整估值与读模型（2026-09-11）

[性能目录](README.md) · [全部阶段样本、CPU 采样与计算](simulation-evaluation-bottleneck-20260911.json) · [前一轮原型](simulation-kernel-prototype-20260910.md)

> 后续进度：冻结候选压缩与工作区恢复已实现，剩余五项评估和历史方案见[决策账本](simulation-strategy-ledger-20260911.md)与[本轮实现结果](simulation-candidate-storage-20260911.md)。本报告保留修改前的测量，完整读取边界尚未迁移。

## 结论

**下一项应做完整估值的读取边界迁移，并压缩冻结候选。** 校正测试上下文后，完整 `Snapshot` 占该实验约 **51%–53% 线程 CPU**；逐叶根 Fork 与事件物化合计约 **36%–38% CPU、63%–66% 分配**。Snapshot 内优先处理手牌可达性、完整状态键和投影洗牌。继续优化纯执行循环或最终 34 项排序的空间很小。

本次完成诊断、测试上下文修正和依赖审计，生产 Search/Runtime 未改。所有结论仅限原来的 30 张卡、34 个完整选择叶子，不能外推全卡牌或整场搜索，更没有新的 10× 结论。

## 先更正上一轮测量的适用范围

原型直接调用私有 `Snapshot`，没有进入 [SolveCore](../../src/Search/CombatBeamSolver.Phases.cs) 使用的 `SimulationNotificationIsolation.Enter()`。[既有 Ritsu 空能力快速路径](../../src/Runtime/RitsuEmptyCapabilityFastPathPatches.cs) 在隔离作用域外立即旁路，因此原型重复执行了生产搜索已经省掉的能力查询。

这也解释了本轮第二次采样中 `CardModel.Type` 贡献链占 Snapshot 样本 **30.21%** 的现象。补齐作用域后，该 getter 链约为 **5.98%**，并且原根 30 张卡都符合已有空能力快速路径。**不能把前者当作尚待实现的生产优化，也不应再造一套相同缓存。** 这些是同一原型的上下文差异，不是本次新增的生产加速。

前一报告的 **1.53× / 分配下降 4.54%** 保留为历史原始样本，但其生产上下文不完整；原据此作出的迁移门槛判断需要参考本报告。此前完整状态与原生对照是已发生的历史验证，本次另在正确作用域内重新验证。

`SimulationNotificationIsolation` 使用线程静态深度。本次在同步模拟开始处进入，在八个纯值 worker 的 `await` 前退出；恢复后重新进入，在原生 `TryManualPlay` 前退出并断言未泄漏。测试期间不跨 `await` 持有该作用域。

## 测量设计与结果

源码基线 `a8ce0b3`；本轮仅改变 Testing 诊断及其上下文。隔离 headless：游戏 `0.111.0`、.NET `9.0.7`、RitsuLib `0.5.20`。本地样本目录日期沿用批次开始的 `20260910`；报告于上海时间 9 月 11 日整理。

- `profile-v1` 默认分层编译、遗漏隔离。每样本 544 叶，后段诊断 CPU 从约 37–42 ms 降至 29–31 ms，存在明显漂移。保留全部数据，没有据此宣布提速。
- `profile-v2` 禁用分层编译、每样本扩为 8,704 叶并采集 CPU，但仍遗漏隔离。它用于发现错误上下文；不能作为最终瓶颈依据。
- **`profile-v3`** 修正隔离，保留 v2 的测量配置与相同全部叶子。每种模式预热后做四组正反交错样本，每样本 256 次完整展开。没有改动卡牌、选择、完整评分或原状态键。

禁用分层编译是控制诊断输入的手段；它不是建议给玩家修改的默认设置。分层编译会在后台产生优化代码，因此 v1 的变化可能包含 JIT 影响，但本次没有单独证明漂移的唯一成因。[.NET 编译配置说明](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation)

两种模式分别使用预热后复用的 solver，或每次 34 叶展开新建 solver。后者清空请求缓存，但 **CLR、模型目录、类型元数据和共享根仍已预热**。34 个物理叶子产生 24 个不同威胁缓存键，新 solver 仍有 10 次合法的同批重复命中；预热模式全部命中。新旧缓存模式的全部 Snapshot 属性及排序顺序都与 oracle 对照。

| 阶段 | 预热 solver CPU / 叶 | 新 solver CPU / 叶 | 预热分配 / 叶 | 新 solver 分配 / 叶 |
| --- | ---: | ---: | ---: | ---: |
| 新建 solver / 选择既有实例 | 0.02 μs | 0.22 μs | 0 B | 119 B |
| 原根 Fork | 4.94 μs | 5.18 μs | 6,896 B | 6,896 B |
| 事件到旧模型物化 | 10.38 μs | 10.66 μs | 8,387 B | 8,387 B |
| 独立候选冻结 | 0.90 μs | 0.88 μs | 4,680 B | 4,680 B |
| 完整 Snapshot / 释放模拟器 | 20.80 μs | 22.83 μs | 3,142 B | 3,883 B |
| 按完整 Score / 原 StateKey 排序与消费 | 0.08 μs | 0.08 μs | 0 B | 0 B |
| **全部诊断链路** | **40.76 μs** | **43.35 μs** | **23,240 B** | **24,100 B** |

总量还包含执行、遍历、路径数组、保留容器及测量编排。预热模式四个 CPU 样本为 356.32 / 353.45 / 353.54 / 355.95 ms；新 solver 为 379.71 / 377.14 / 376.20 / 376.38 ms，样本完整保留。新 solver 总 CPU 高约 6.4%，改变缓存条件后优先级没有翻转。

线程 CPU 来自 `CLOCK_THREAD_CPUTIME_ID`，墙钟另测。空测量段平均约 **0.519 μs CPU**，没有从结果中偷偷扣除。每叶几个计时调用并非免费，冻结/排序等微小 CPU 数字不能直接当作精确可消除成本。分配为当前线程累计字节，不是存活堆、RSS 或 GC 暂停。

同请求还保留原三种完整循环，关闭内部阶段计时，但仍处于 perf 采样、禁用分层编译的环境：旧重放 57.30 μs / 25,168 B，紧凑完整链路 34.90 μs / 23,240 B，纯内核 1.11 μs / 4,791 B。前两者比约 **1.64×、分配下降 7.66%**，仍未达到先前建议的 3× / 分配≤20% 门槛。它是校正后实验的参考值，不是正常生产性能或相对上一版的提速。

## Snapshot 内部的实际热点

Linux `perf record -e cpu-clock:u -F 499 --call-graph fp`，结合 JIT 符号后，只纳入包含 `ProfileCompactEvaluation` 栈帧的样本。7,870 个进程样本中，1,382 个属于该诊断，753 个包含 Snapshot；后者占诊断 CPU 样本约 54.49%，与分段时钟的优先级一致。

| Snapshot 内调用子树 | Snapshot CPU 样本占比 | 应迁移的读取内容 |
| --- | ---: | --- |
| `CalculateReachableHandPotential` | 20.05% | 手牌、能量/星能、合法性、费用及原背包输入 |
| `BuildStateKey` | 18.46% | 资源、逐实例有序牌堆、完整卡状态、九条 RNG、未来生命周期计数 |
| `BuildProjectedShuffleOrder` | 17.53% | 原拼接顺序、精确比较器、克隆 RNG、卡值与牌指纹 |
| `BuildCyclePileShapeKey` | 8.23% | 各牌堆结构身份摘要，保留现有独立语义 |

前三条彼此为独立调用子树；更细的 `CanPlay` 16.20% 已包含在手牌可达性内，牌堆指纹也包含在 StateKey 内，不能再次相加。`CardValue`、Type getter、关键字查询横跨多个调用方，属于重叠机制，而非独立阶段。

已有内部计时还显示：预热 Snapshot 的“全部指纹相关”墙钟约 9.94 μs，其中投影洗牌约 3.95 μs、战斗域指纹约 1.99 μs；这些是嵌套墙钟，不是互斥 CPU。手牌可达性的纯背包部分已使用 Span/栈内存，主要迁移对象应先看合法性与费用的读取链，不能把全部 20.05% 归给背包算法。

校正后，Snapshot 分配只占完整链路约 13.5%–16.1%。此前 33 KB/叶的大量分配包含未启用空能力快速路径的成本。旧图转换现在才是主要分配来源；其中事件物化仍会产生卡牌可写副本、动态变量克隆、牌堆更新和历史对象。

该诊断的约 25.1% self 样本、Snapshot 约 19.8% self 样本未解析到具体方法，保留了原生运行时与符号缺失。它们通常仍能通过祖先栈归到上述大阶段，但不支持逐指令归因。采样只覆盖这个封闭动作，没有测完整搜索、真实 Beam 准入、DOP8 利用率或可见帧率。

## 下一批重构边界

### 1. 同一完整估值逻辑，两个状态读取器

先把 [StateEvaluation](../../src/Search/CombatBeamSolver.StateEvaluation.cs) 的读取合同按资源、卡牌、牌堆、威胁、生命周期划分。旧对象后端与紧凑后端消费同一套完整公式和指纹追加顺序。紧凑路径从不可变模型目录与当前 worker 的权威值读取，在返回前冻结结果；不能为了读数据再次 `Materialize` 一份完整旧图。

不应只提取最后的 `score +=`，再由旧对象图生成所有输入；那样主要读取成本仍在。也不应独立复制一份简化评分。当前紧凑卡定义只有费用、抽牌/弃牌和 Sly，尚不足以表达完整估值；需补齐实际读取的动态状态、能力依赖和生命周期摘要。

优先迁移手牌的合法性/费用读取与卡牌元数据，再迁移牌堆/指纹与投影洗牌，共用现有 `ReachableHandValue.Calculate`。只有在明确没有相关第三方能力、修改器或未知 Hook 时，才可把已证明不变的读值放入根目录；泛化后需要准确依赖和失效规则，不能按卡牌 Type 永久缓存全部语义。

项目使用 C# 13，可评估用局部 `readonly ref struct` / `ReadOnlySpan` 表达借用视图、以泛型约束访问共同读取合同。`ref struct` 不能装箱成接口，借用视图也不能逃入候选或跨越 `await`；这些语言机制帮助约束所有权，并不保证 JIT 生成更快代码。[ref struct 规则](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/ref-struct)、[复制与分配成本](https://learn.microsoft.com/en-us/dotnet/csharp/advanced-topics/performance/)

### 2. 保留原键和投影语义，再做局部增量摘要

[StateFingerprintBuilder](../../src/Search/StateFingerprint.cs) 是两条顺序混合链，原数值参与同分排序。迁移必须保持 marker、追加顺序、默认/空容器编码、逐实例顺序与九条 RNG。`SimulatedCombatState.AppendFingerprint` 还读取过去出牌、弃牌、资源消费等未来语义，不能只比较 HP 和牌堆。

现有格式已经将单卡、单牌堆摘要嵌入完整键，这些边界可以共享或按精确变化重算。不能把任意不可变后缀简单换成一个预计算哈希，也不能直接换成 Zobrist 后声称原排名未变。

投影洗牌必须保留 Discard → Draw → Hand 的原输入、原比较器和克隆 RNG。可尝试在精确相同的读取依赖下复用摘要；本次没有测新的跨叶命中率，不应据此承诺全局缓存收益。

### 3. 同时缩小冻结候选

若假设根 Fork 与事件物化完全免费、其余开销不变，诊断链路的代数上限仅约 **1.58–1.60×**；剩余分配仍约 **7.96–8.82 KB/叶**。这是忽略替代成本的理想上限，不是已实现收益。

现有冻结操作每叶分配 4,680 B，相当于校正后旧链路每叶分配的 **18.60%**。如果目标仍是≤20%，只给所有其他工作留下约 **354 B/叶**。因此，“只删除旧图投影”不足以支持原来的分配门槛。

下一原型应评估以共享不可变根加自有差量/已用区间冻结候选，分别编码完成状态和仍挂起的执行帧；保留后续执行必需的历史和来源数据。候选不能引用可变 worker，不能通过丢弃必要计数来压缩。需要把写入标记、冻结、恢复、独立 worker 使用和保留内存全部计价，再决定是否扩展到通用效果域。

## 验证与交付

最终 `profile-v3` 请求 `22e98eed11404f2cbb9287d29c7e889a` **Passed，30.48 秒**，没有扩大 120 秒超时：34 叶完整状态/ContinuationStamp/原 StateKey/全部 Snapshot 属性与历史对照；新旧缓存模式排序一致；嵌套撤销、冻结独立恢复、候选下一动作及一个原生嵌套链继续通过。原生部署前隔离已退出。

新增 [分段计量](../../src/Testing/CompactPhaseProbe.cs) 与 [估值诊断](../../src/Testing/UnattendedTestRunner.CompactKernelProfile.cs) 均由 Testing 拥有。阶段指标只观察调用，不替代评分或执行。双端结构规则禁止生产 Search/Runtime 引用诊断器，并要求测试保留隔离上下文边界。Windows 的线程 CPU 实现仍可用，但本次只在 Linux 执行；PowerShell 对应规则未运行。

最终行为源码 Release 编译 0 警告/错误；Linux 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=85`。L0 校验 24 个阶段样本、逐叶调用数、198 个相对 Markdown 链接及隔离作用域边界；Search/Runtime 与原完整基准输入差异为空。三个请求及其不足均保留，所有测试实例已停止。没有发布、推送或交互式游戏安装。

复跑：先构建到隔离目录并放入当前 manifest/NOTICE，再通过原无人入口运行。默认诊断每样本 16 次展开；下面用 256 次控制诊断输入，不代表生产默认配置。

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false -o .local/simulation-evaluation-20260910/artifact --no-restore
cp CombatSolver.json THIRD_PARTY_NOTICES.md .local/simulation-evaluation-20260910/artifact/
DOTNET_TieredCompilation=0 COMBATSOLVER_COMPACT_PROFILE_ITERATIONS=256 \
  ./tools/run-unattended-test.sh --scenario-id COMPACT-KERNEL-NATIVE --character-id SILENT \
  --enemy-current-hp 256 --headless-instance simulation-evaluation-20260910 \
  --combat-solver-build-dir .local/simulation-evaluation-20260910/artifact \
  --evidence-directory .local/simulation-evaluation-20260910/reproduce --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --headless-instance simulation-evaluation-20260910 --stop-instance
```

该命令产出完整状态检查、原循环数据与 `compact-evaluation-profile.json`。CPU 栈采样另外需要在启动时启用 perf map/JIT dump 并附加 perf；上表采样配置及过滤条件已单列，不能把不附加 profiler 的新时间与旧采样时间直接解释为代码收益。
