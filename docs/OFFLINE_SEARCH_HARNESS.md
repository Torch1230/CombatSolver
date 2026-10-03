# 离线搜索宿主

`tools/search/OfflineSearchHarness/` 是一个普通的 .NET 9 控制台程序：它加载 `sts2.dll` 但**不启动 Godot
引擎**，用游戏自己的核心层建出一场战斗、推进到玩家第一回合，再在同一个进程里调
`CombatRootSnapshot.Capture` 与 `CombatSearchCoordinator.Solve`（或单次 `CombatBeamSolver`）跑一次
固定预算搜索，把指标、选中路线和搜索策略写成 JSON。

它解决的是批量测量的成本问题：游戏内无人测试每一根都要起一次 Godot 进程，宿主不用，几十根到上百根
的宽度/预算/保留规则对照可以在一台机器上连着跑。**它不是 `docs/HEADLESS_TESTING.md` 的替代**——
正确性验收仍然走无人测试，宿主只覆盖「同一份 DLL、同一个根、只换搜索参数」这一类测量。

## 构建

```
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release
```

路径解析与 `CombatSolver.csproj` 同一套：先 `Import` 仓库根的 `local.props`，再按操作系统给
`SteamRoot` / `Sts2Dir` / `Sts2DataDir` / `RitsuWorkshopRoot` / `RitsuLibDir` 默认值；RitsuLib 优先走
`RitsuLib.References.props`，没有它才回退 `RitsuLibDir`。模组 DLL 默认取
`.godot/mono/temp/bin/Release/CombatSolver.dll`，可以用 `-p:CombatSolverDll=<path>` 覆盖；运行期还可以
用环境变量 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 换一份（批量对照不同 DLL 时用）。

宿主对 `sts2` 沿用模组自己的公开化（`Publicize`），对模组本体不做公开化——`CombatSolver.csproj` 里加了
`<InternalsVisibleTo Include="OfflineSearchHarness" />`，宿主只经 `internal` 入口进来。

## 单根用法

```
dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
    --request <无人测试请求.json> --label R1 --out <产物目录> \
    --profile VeryHigh --beam 135 --nodes 100000 --dop 1 --budget-ms 600000
```

常用选项：

| 选项 | 含义 |
|---|---|
| `--request <path>` | 无人测试请求 JSON（含 `generatedScenarioPath`），走生成场景开局 |
| `--character/--encounter/--seed/--ascension/--act-index` | 不给 `--request` 时直接指定一个新跑局 |
| `--profile` | `Low\|Medium\|High\|VeryHigh\|Custom`（默认 `Custom`），预设值问模组自己要 |
| `--beam/--nodes/--card-branches/--pile-branches/--hand-branches` | 覆盖预设的宽度、节点上限与分支上限 |
| `--dop <int>` | 搜索并行度（默认 1） |
| `--budget-ms <int>` | 搜索软时间预算毫秒（默认 600000） |
| `--potion-policy <p>` | 药水政策（默认 `Smart`） |
| `--search-mode <m>` | `Evaluate`（默认）或 `Coordinator` |
| `--use-portfolio` | 开宽度组合，只对 `Coordinator` 有效 |
| `--out/--label/--language/--verbose-game-log/--milestone` | 产物目录、标签、本地化语言码、是否打游戏日志、跑到 M1 还是 M2 |
| `--measure-phases` | 在运行日志里输出 `SEARCH_PHASE` 逐阶段排他耗时/分配表 |
| `--early-turn-exploration-depth <0|1|2>` | Coordinator 测量时打开早期回合探索；默认 0，离线选项不改变生产默认值 |
| `--early-turn-exploration-budget-ms <5000..2390000>` | 早期探索从请求开始计的累计时限上限，不是阶段外加时长；仅深度大于 0 时使用，默认 2,390,000 ms（请求最大 40 分钟减 10 秒余量） |
| `--memory-no-progress-limit <int>` | 实验：连续多少次无进展内存回收后提前收手；0=关闭（生产默认） |
| `--transposition-entry-limit <int>` | 实验：转置支配表合并条目上限；0=不设上限，缺省=生产默认 1000000 |
| `--enable-no-gc-region` | 让 Runtime 的 No-GC / 回收生命周期真正生效；默认关闭 |
| `--no-gc-region-budget-gigabytes <double>` | No-GC 区域预算（十进制 GB，1..256）；只在开启上一项时生效 |
| `--signal-ballast-mb <int>` | 进 No-GC scope 后先持有 N MiB 活对象；只用于制造受控内存压力，0=关闭 |

超时定位可设置 `OFFLINE_HARNESS_STREAM_DIAGNOSTICS=1`：现有 Info 诊断同时写到标准输出；Coordinator 还每秒至多输出一次现有进度消息中的阶段、局部展开、配置额度和回合层等值，进程被外部结束时仍可保留已经写出的记录。进度的 `reviewed_worldlines` 不是模拟转移数，也不能替代完成结果的请求级 `TotalExpanded` / `TotalTransitions`。该模式会启用进度回调及额外输出，可能改变耗时、分配和墙钟截断，只用于定位，不能作为性能或最终质量样本。默认不开启；普通批量对照须保持关闭。

启用阶段测量时，`BEAM_WIDTH_PORTFOLIO_MEMBER_START` 在进入成员前记录实际运行序号、宽度、次段/基础分/能力承诺身份，以及有效节点/时间额度。`run_index` 只计算实际运行的成员，不能当作包含跳过项的最终成员表索引。即使后续成员超时，配合同步诊断也可识别正在执行的成员；不能仅凭“正在精炼路线”的进度文案推断策略身份。

启用 `OFFLINE_HARNESS_INFUSED_CORE_CHECKS=1` 并使用 `--character DEFECT --milestone M1` 可运行注能核心的生产 Hook 诊断：从空球队列检查首回合生成、后续回合不重复、参与者条件、4/9 数值及 Fork 隔离，结果写入 `infused-core-checks.json`。该入口只修改宿主内的测试战斗；它不经过原生工具箱页面，不能替代 `coverage/fixtures/scenarios/state/initial-toolbox-infused-core.json` 的原生准备状态验收。

启用 `OFFLINE_HARNESS_FIXED_PREFIX_CONTINUATIONS=1` 并以 `coverage/fixtures/search/generic-cross-turn-hidden-buffer-positive-v0111.json` 为 `--request`，使用 `--dop 1 --search-mode Evaluate` 且关闭NoGC／增量验证，可运行4／8／17回合完整固定前缀基准。每根预热一次、测量三次生产 `Solve`，计时外用独立前缀重放对账完整续用戳，输出 `fixed-prefix-continuations.json`。它只度量人工长路线的前缀建立与收尾，不代表普通搜索或原生正确性；用 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 交错切换基线／候选，完整比较根、政策和 `annotatedResult`，见[本轮证据](archive/performance/fixed-dop-20260927.md)。

纯 ETC 外部生命界合同可运行 `dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --check-early-turn-continuation-bound`，直接调用生产门禁及剪枝谓词，不建游戏状态。当前覆盖 143 条断言。普通 ETC 诊断起始行新增 `incumbent_bound=eligible_strict_hp`，已完成续搜行输出 `incumbent_hp`（`-` 表示旁路）及 `incumbent_pruned`；后者包含原有内部生命界剪枝，不能直接视为外部界的净收益。新版本另输出 `incumbent_certified_healing_bound_pruned`，只归因于根认证治疗上界的边际剪枝；根捕获行记录认证状态、首个拒绝原因和固定战后治疗量。两个计数的口径不同，前者是成员内合计，后者只统计认证上界相对完整缺血余量增加剪掉的候选节点。它不是所有阶段的总剪枝量或节省的节点数。固定根对照及实际适用范围见 [测试矩阵](archive/testing/volume-01.md#早期回合探索的外部生命界2026-10-01)。

## 批量用法

`OFFLINE_HARNESS_EQUIVALENCE_PROBE=1` 可在小预算 `Evaluate` 请求中观察已有转置拒绝、候选分类次数和自然出现的两步反向动作，输出 `equivalence-probe.json`。每个求解器最多保存20,000个分离出的两步索引，不持有节点/模型，也不改变剪枝结果；指纹相同只是研究线索，不是交换性证明。该模式有额外锁和序列化开销，不能用于时间或分配评测。适用范围和复现命令见[准入优化与采样](archive/performance/equivalence-admission-20260929.md)。

`tools/search/OfflineSearchHarness/run_plan.py` 吃一份 plan JSON（数组），起 N 个宿主进程并行消费：

```
python3 tools/search/OfflineSearchHarness/run_plan.py --plan <plan.json> --workspace <dir> --workers 3
```

plan 每项的字段：`label`（必填，简单目录名）、`request`（必填）、`profile`、`beam`、`nodes`、
`maxCardBranchesPerNode`、`maxPileChoiceBranchesPerAction`、`maxHandChoiceBranchesPerAction`、
`maxDegreeOfParallelism`、`searchBudgetMilliseconds`、`potionPolicy`、`searchMode`、`usePortfolio`、
`earlyTurnExplorationDepth`、`earlyTurnExplorationBudgetMilliseconds`、`dll`（换掉这一根运行时加载的 `CombatSolver.dll`）。
早期探索只在 `Coordinator` + M2 搜索中启用；不给单独时限时使用上限。它与 `--budget-ms` 共用请求时钟，只把 ETC 自身的截止点从主搜索软预算中分开。批量运行器会确保进程超时至少覆盖两个时限中的较大值再加 300 秒，避免把有界探索误判为异常终止。

产物在 `<workspace>/runs/<label>/`，另有 `<workspace>/runs.jsonl` 与 `plan-summary.json`。

`tools/search/OfflineSearchHarness/compare_results.py` 把两份 `runs/` 目录逐字段比较（两侧都提供时还比较选中路径的续用戳；`solverMetrics` 里
与时间/内存/GC 无关的字段、选中路线每个动作的 `turn/kind/cardId/potionId/targetCombatId/cardStateKey`、
根 `ContinuationStamp`、生成场景目录指纹），全等返回 0，有差异返回 1 并把明细写进 `--out`。

## 产物

每根一个目录：

- `result.json`：`label` / `status` / `profile` / `searchMode` / `budget` / `solverMetrics`（与游戏内
  无人测试 `result.json` 同名同形，由游戏自己的 Writer 构造）/ `pruneCounters`（宿主从 `SolverResult`
  读的剪枝与复用计数，游戏内那份没有）/ `timeBoundaryObserved` / `wallSeconds` / 峰值托管堆、峰值
  工作集、总分配字节 / `rootContinuationStamp` / `catalogFingerprint`。
- `route.json`：选中路线的动作序列。
- `root-diagnostics.txt`：`SolverDiagnostics.DescribeStart` 的根局面描述。
- `search-policy.json`：这一次求解实际用的 `SearchPolicySnapshot`。
- `harness-result.json`：上面全部加上分步时间线、绕过清单、补丁装载记录。

## 口径

**`Evaluate` 与 `Coordinator` 的区别。** `Evaluate` 是单次求解：直接建一个 `CombatBeamSolver` 跑，不经
协调器，也就没有组合成员和审计通道，`totalExpanded` 等于这一棵树自己的展开量。`Coordinator` 走生产
路径 `CombatSearchCoordinator.Solve`，`solverMetrics` 里的 `total*` 字段是**协调器把各条通道（含审计
通道与组合成员）加总**后的值，所以同一个根同样的宽度，`Coordinator` 的 `totalExpanded` 会明显大于
`Evaluate`。要量「一个宽度值到底搜了多少」用 `Evaluate`；要量「玩家实际会等多久、实际选哪条路线」
用 `Coordinator`。

`solverMetrics.searchWorkAttributions` 另列经前沿调度器派发的各 `ContinuationPurpose` 工作量、`UnattributedDirect`（尚未细分的主搜和审计）及 `CoordinatorOverhead`。这是同一请求账本的诊断分解；直接成员尚未全部标记，不能由 `UnattributedDirect` 推断单一瓶颈。早期探索开启时，`solverMetrics.earlyTurnExploration` 还记录侦察消耗、续搜次数、严格改进次数、首次改进深度/rank，以及逐 rank 的搜索时长、展开和结果；零改进与未运行用对象存在性区分。若已满足配置的可接受目标，探索会在入口跳过，或在侦察/续搜后以 `stop=acceptable_target` 结束；完整零战损特例保留为 `zero_damage`。它只改变后续探索是否继续，不改变路线比较。超时未产结果时使用常驻会话保存的 `timeout-progress.json`，旧包缺该文件就没有可追溯的末段工作量。

**固定预算口径。** 宿主总是以 `fixedSearchBudget=true` 起一段离线会话
（`UnattendedTestRunner.BeginOfflineSession`），`--budget-ms` 落在 `searchBudgetOverrideMilliseconds`
上，`--dop` 落在 `searchMaxDegreeOfParallelismForTest` 上——与游戏内无人测试请求里的同名字段走同一段
代码（`ProtocolHost.ConfigureSearchOverrides`）。默认 `EnableNoGcRegion` 关闭；显式传
`--enable-no-gc-region` 且目标是验证 Runtime 的内存回收/截断路径时，才由 `SearchGcPolicy` 管理模式
并将回收回调注入搜索。该模式只用于诊断，不替代游戏内无人测试的正确性断言。

**宽度组合。** `--use-portfolio` 把 `useBeamWidthPortfolioForTest` 打开，与无人测试请求里那个开关同义；
成员宽度不指定时用协调器自己的默认成员集，成员明细在 `solverMetrics.portfolioMembers`。

## Godot 绕过

宿主不启动引擎，凡是会打到 Godot 原生层的入口都要绕开。绕过点全部集中在
`tools/search/OfflineSearchHarness/GameBootstrap.cs` 一个类里，类头有完整的表（目标、为什么必须绕、绕过后
返回什么、对搜索结果有没有影响），结果 JSON 的 `bypasses` 字段列出实际装上的那些。摘要：

| 目标 | 绕过后 | 对搜索结果 |
|---|---|---|
| `Logger.GetIsRunningFromGodotEditor`、`ConsoleLogPrinter.Print` | 不判编辑器、打到 `System.Console` | 无，只决定日志去向 |
| `LocString.GetRawText/GetFormattedText/Exists` | 返回本地化键名 / `true` | 无，搜索不读文案（见下方限制） |
| `PreloadManager.Load{Run,Act,RoomCombat}Assets` | `Task.CompletedTask` | 无，战斗建立不需要立绘与节点 |
| `NCombatRulesFtue.Create` | `null` | 无，与游戏内无人测试同语义 |
| `MigrationRegistry.RegisterAllMigrations` | 跳过注册 | 无，离线不读存档 |
| `NGame.GetGameVersion`、`PlatformUtil.GetPlatformBranch/GetPlayerNameRaw` | 固定值 | 无，只进联机握手信息与显示名 |
| `Godot.Node` 及其 304 个子类的静态构造 | 跳过 | 无，离线不建节点树 |

还有一处不是补丁：`SolverController.DisplayServerNameProvider` 被设成固定返回 `"headless"`，与游戏内
`--headless` 取到的值一致，帧压力恢复照样关闭。

## 已知限制

- **本地化返回键名**：`LocManager.Initialize` 要用 `Godot.FileAccess` 读 `res://localization`，离线装的是
  一张空表，所有文案取到的是键名。显示字段（`cardTitle` / `targetName` / …）因此不能与游戏内直接比，
  `compare_results.py` 已把它们排除。搜索本身不读文案。
- **只覆盖生成场景与新跑局**：`runSnapshotPath`、`replayStatePath`、`checkpointArchivePath` 这几条
  「从存档/回放恢复战斗态」的入口没有接，宿主只能从新跑局或生成场景开局。
- **RitsuLib 未初始化**：宿主装的是模组里与搜索正确性相关的那 13 个 Harmony 补丁，RitsuLib 自己的运行期
  初始化没跑。实测 30 根里有 2 根的探索量与游戏内不同，结论字段（选中路线、`score`、
  `projectedBattleHpLost`）相同。
- **不做正确性验收**：宿主没有无人测试的断言体系，它只产指标。行为改动仍然要过
  `docs/HEADLESS_TESTING.md` 的流程。

## 验证证据

**新宿主对旧研究版宿主，同一份 0.39.0 DLL，逐字段一致。** 两边跑同一批生成场景请求
（`VeryHigh`、beam 135、nodes 100000、分支上限 72/42/54、`--dop 1`、`--budget-ms 600000`、
`potionPolicy=Smart`、`searchMode=Evaluate`），用 `compare_results.py` 比 `solverMetrics`
（排除时间/内存/GC 字段）、选中路线每个动作、根 `ContinuationStamp` 与目录指纹：

| 批次 | 根数 | 比较字段数 | 不一致的根 |
|---|---:|---:|---:|
| BASE5 | 5 | 466 | 0 |
| 30 根子集 | 30 | 2719 | 0 |

**`Coordinator` + `--use-portfolio`。** 3 根走生产协调器并开宽度组合，全部跑通，
`solverMetrics.portfolioMembers` 各有 3 个成员（当时 0.39.0 基线的默认成员集是 `[W, 2W/3, 3W/2]`）；
开关关闭时只有 1 个成员。

**建根流程本身与游戏内的一致性**（宿主刚做出来时测的，基于研究分支 `4287e03`）：30 根生成场景，
宿主与游戏内无人测试逐字段对照，`solverMetrics` 的可比字段、选中路线、装备与开局产物全部相同，
2 根探索量不同（RitsuLib 未初始化，见上）。


## 生产预算与转置表观测

`--production-budget` 使用现有生产预算流程，包括剩余预算允许的无胜利升级。默认仍是固定预算，用于确定性逐位对照。批量计划的 `productionBudget: true` 允许正常时间边界作为有效观测，仍保留 `timeBoundary` 字段；固定预算计划撞到时间边界仍作废。

批量计划现在支持 `transpositionEntryLimit`，映射已有 CLI 的同名上限。省略字段使用生产默认一百万条；实验放大上限不修改生产值。

每次求解的 `TRANSPOSITION_CAP` 行记录首次触顶展开数、跨缓存重建保留的峰值条目、结束时标签数和分布。`LimitBypasses` 按未入表的准入/展开事件计数，包含重复键；标签分布为单通道结束值。Coordinator 多个通道分别输出，不合并成虚假的同时驻留峰值。

## 循环边界对照

`run_loop_boundaries.py` 接受逐 case 的 Evaluate / Coordinator。Evaluate 的局部 time/nodes 计数与日志对账；Coordinator 从全部成员日志提取请求级时间截断，不把所选 solver 的计数当请求总数。新版用 `TotalCycleReplayActions` 检查请求 4096 上限；旧版只在 Evaluate 可回退单 solver 值，旧 Coordinator 缺失请求数明确标为 unavailable。时间截断返回 Inconclusive/2；可比较差异、建局或质量断言失败返回 1，保留全部原始观察。工具的显式 suite 断言不等于原生 expected* 验收。见[完整输入、设计和结果](archive/performance/loop-final-20260921.md)。


### 后置结构探索实验

`--adaptive-novelty` 仅接受 `--search-mode Coordinator --use-portfolio`，通过不可变 `AdaptiveNoveltyRefinement` profile 启用，生产默认关闭。先完整运行原 Beam 组合；达到完整政策目标（含治疗保护）则跳过，否则复用已有新颖性算法。补充额度分别不超过此前实际展开与实际耗时的 1/8，同时受原请求余量及既有 2500 节点/5 秒上限约束。节点额度不等于转移、分配或内存上限；不可分割工作仍可能越过软时间边界。

`ADAPTIVE_NOVELTY_START/END` 记录实际预算、展开/转移和选择结果。`NOVELTY_SEARCH_STOP reason=...` 覆盖所有新颖性搜索；宿主分类器将 `time_limit` 单独记入 `noveltyStops` 并标记 `TimeLimited`，不混入回合层计数。以前没有该事件的 DLL 不能据“没有 SEARCH_TIME_BUDGET”断言该算法没有时间截断。探索预算依赖墙钟，质量观察必须保留时间截断与重复波动，不能称为固定工作量等价。完整取舍与验证结果见上下文排序报告。


算法配置记录补充：`searchPolicy` 现在显式输出 `BeamWidthPortfolioPlainBaselineMember` 与 `UseNoveltyPortfolio`。旧宿主未记录这两项时，比较报告显示 `Unrecorded`，必须结合保存的命令与实际成员表判断，不能把缺失值当作默认值。上下文排序对照允许这两项算法配置作为显式实验差异，仍拒绝根、预算、可接受战损和用药政策等目标差异。

`Coordinator --use-portfolio` 默认启用组合再分配。`--disable-reallocated-refinement` 在同一最终程序集恢复旧默认成员列表，供明确A/B；`--reallocated-refinement` 可显式开启。真实运行是否采用新布局由 `PORTFOLIO_REALLOCATION` 与实际成员表确认，显式成员布局和其他可选实验不被改写。配置保存在 `searchPolicy.Profile.ReallocatedRefinementPortfolio`，对照工具将其视为算法差异而保留目标政策/根/预算核对。
