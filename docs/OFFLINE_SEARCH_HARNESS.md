# 离线搜索宿主

`tools/OfflineSearchHarness/` 是一个普通的 .NET 9 控制台程序：它加载 `sts2.dll` 但**不启动 Godot
引擎**，用游戏自己的核心层建出一场战斗、推进到玩家第一回合，再在同一个进程里调
`CombatRootSnapshot.Capture` 与 `CombatSearchCoordinator.Solve`（或单次 `CombatBeamSolver`）跑一次
固定预算搜索，把指标、选中路线和搜索策略写成 JSON。

它解决的是批量测量的成本问题：游戏内无人测试每一根都要起一次 Godot 进程，宿主不用，几十根到上百根
的宽度/预算/保留规则对照可以在一台机器上连着跑。**它不是 `docs/HEADLESS_TESTING.md` 的替代**——
正确性验收仍然走无人测试，宿主只覆盖「同一份 DLL、同一个根、只换搜索参数」这一类测量。

## 构建

```
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release
```

路径解析与 `CombatSolver.csproj` 同一套：先 `Import` 仓库根的 `local.props`，再按操作系统给
`SteamRoot` / `Sts2Dir` / `Sts2DataDir` / `RitsuWorkshopRoot` / `RitsuLibDir` 默认值；RitsuLib 优先走
`RitsuLib.References.props`，没有它才回退 `RitsuLibDir`。模组 DLL 默认取
`.godot/mono/temp/bin/Release/CombatSolver.dll`，可以用 `-p:CombatSolverDll=<path>` 覆盖；运行期还可以
用环境变量 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 换一份（批量对照不同 DLL 时用）。

宿主对 `sts2` 沿用模组自己的公开化（`Publicize`），对模组本体不做公开化——`CombatSolver.csproj` 里加了
`<InternalsVisibleTo Include="OfflineSearchHarness" />`，宿主只经 `internal` 入口进来。

## 单根用法

零训练估值原型可在 `Evaluate` 下显式加 `--outcome-probes 8`，将同一请求额度分给基线与有限首动作续搜，
输出逐动作的完整结果见证及总成本到 `outcome-probes.json`；正常 Runtime 不启用。
加 `--selective-outcome-probes` 保留全预算基线、复用终局见证排序并限制额外节点；也支持禁用药水的 Coordinator 基线。见[第二版研究](strategy/outcome-valuation-v2-20260927.md)，实验不是正式评分开关。
生产多策略路径可用 `Coordinator --novelty-portfolio` 打开，可与 `--use-portfolio` 组合；它与 `--adaptive-novelty` 互斥。
复现、覆盖限制与整批时间上限见 [OutcomeValuation](../tools/OutcomeValuation/README.md)。

```
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
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
| `--memory-no-progress-limit <int>` | 实验：连续多少次无进展内存回收后提前收手；0=关闭（生产默认） |
| `--transposition-entry-limit <int>` | 实验：转置支配表合并条目上限；0=不设上限，缺省=生产默认 1000000 |
| `--enable-no-gc-region` | 让 Runtime 的 No-GC / 回收生命周期真正生效；默认关闭 |
| `--no-gc-region-budget-gigabytes <double>` | No-GC 区域预算（十进制 GB，1..256）；只在开启上一项时生效 |
| `--signal-ballast-mb <int>` | 进 No-GC scope 后先持有 N MiB 活对象；只用于制造受控内存压力，0=关闭 |

超时定位可设置 `OFFLINE_HARNESS_STREAM_DIAGNOSTICS=1`：现有 Info 诊断同时写到标准输出；Coordinator 还每秒至多输出一次现有进度消息中的阶段、局部展开、配置额度和回合层等值，进程被外部结束时仍可保留已经写出的记录。进度的 `reviewed_worldlines` 不是模拟转移数，也不能替代完成结果的请求级 `TotalExpanded` / `TotalTransitions`。该模式会启用进度回调及额外输出，可能改变耗时、分配和墙钟截断，只用于定位，不能作为性能或最终质量样本。默认不开启；普通批量对照须保持关闭。

启用阶段测量时，`BEAM_WIDTH_PORTFOLIO_MEMBER_START` 在进入成员前记录实际运行序号、宽度、次段/基础分/能力承诺身份，以及有效节点/时间额度。`run_index` 只计算实际运行的成员，不能当作包含跳过项的最终成员表索引。即使后续成员超时，配合同步诊断也可识别正在执行的成员；不能仅凭“正在精炼路线”的进度文案推断策略身份。

## 批量用法

`tools/OfflineSearchHarness/run_plan.py` 吃一份 plan JSON（数组），起 N 个宿主进程并行消费：

```
python3 tools/OfflineSearchHarness/run_plan.py --plan <plan.json> --workspace <dir> --workers 3
```

plan 每项的字段：`label`（必填，简单目录名）、`request`（必填）、`profile`、`beam`、`nodes`、
`maxCardBranchesPerNode`、`maxPileChoiceBranchesPerAction`、`maxHandChoiceBranchesPerAction`、
`maxDegreeOfParallelism`、`searchBudgetMilliseconds`、`potionPolicy`、`searchMode`、`usePortfolio`、
`dll`（换掉这一根运行时加载的 `CombatSolver.dll`）。

产物在 `<workspace>/runs/<label>/`，另有 `<workspace>/runs.jsonl` 与 `plan-summary.json`。

`tools/OfflineSearchHarness/compare_results.py` 把两份 `runs/` 目录逐字段比较（两侧都提供时还比较选中路径的续用戳；`solverMetrics` 里
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
`tools/OfflineSearchHarness/GameBootstrap.cs` 一个类里，类头有完整的表（目标、为什么必须绕、绕过后
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

`run_loop_boundaries.py` 接受逐 case 的 Evaluate / Coordinator。Evaluate 的局部 time/nodes 计数与日志对账；Coordinator 从全部成员日志提取请求级时间截断，不把所选 solver 的计数当请求总数。新版用 `TotalCycleReplayActions` 检查请求 4096 上限；旧版只在 Evaluate 可回退单 solver 值，旧 Coordinator 缺失请求数明确标为 unavailable。时间截断返回 Inconclusive/2；可比较差异、建局或质量断言失败返回 1，保留全部原始观察。工具的显式 suite 断言不等于原生 expected* 验收。见[完整输入、设计和结果](performance/loop-final-20260921.md)。


### 后置结构探索实验

`--adaptive-novelty` 仅接受 `--search-mode Coordinator --use-portfolio`，通过不可变 `AdaptiveNoveltyRefinement` profile 启用，生产默认关闭。先完整运行原 Beam 组合；达到完整政策目标（含治疗保护）则跳过，否则复用已有新颖性算法。补充额度分别不超过此前实际展开与实际耗时的 1/8，同时受原请求余量及既有 2500 节点/5 秒上限约束。节点额度不等于转移、分配或内存上限；不可分割工作仍可能越过软时间边界。

`ADAPTIVE_NOVELTY_START/END` 记录实际预算、展开/转移和选择结果。`NOVELTY_SEARCH_STOP reason=...` 覆盖所有新颖性搜索；宿主分类器将 `time_limit` 单独记入 `noveltyStops` 并标记 `TimeLimited`，不混入回合层计数。以前没有该事件的 DLL 不能据“没有 SEARCH_TIME_BUDGET”断言该算法没有时间截断。探索预算依赖墙钟，质量观察必须保留时间截断与重复波动，不能称为固定工作量等价。完整取舍与验证结果见上下文排序报告。


算法配置记录补充：`searchPolicy` 现在显式输出 `BeamWidthPortfolioPlainBaselineMember` 与 `UseNoveltyPortfolio`。旧宿主未记录这两项时，比较报告显示 `Unrecorded`，必须结合保存的命令与实际成员表判断，不能把缺失值当作默认值。上下文排序对照允许这两项算法配置作为显式实验差异，仍拒绝根、预算、可接受战损和用药政策等目标差异。

`Coordinator --use-portfolio` 默认启用组合再分配。`--disable-reallocated-refinement` 在同一最终程序集恢复旧默认成员列表，供明确A/B；`--reallocated-refinement` 可显式开启。真实运行是否采用新布局由 `PORTFOLIO_REALLOCATION` 与实际成员表确认，显式成员布局和其他可选实验不被改写。配置保存在 `searchPolicy.Profile.ReallocatedRefinementPortfolio`，对照工具将其视为算法差异而保留目标政策/根/预算核对。


自动调度对照：`--search-mode Coordinator --automatic-search` 运行当前玩家入口，不叠加旧 `--novelty-portfolio`、`--adaptive-novelty` 或 `--outcome-probes`。不指定此参数仍复现历史布局，旧模式开关只存在于测试工具。`python tools/OutcomeValuation/run.py --out <目录> --automatic --seconds 150 --case attack_or_block --case focus_investment --case random-regent` 在相同配置预算下比较旧宽度+新颖性组合与自动入口；旧能力前缀有额外预算，实际工作量不相同。见[报告](strategy/automatic-search-20260927.md)。

共享证据消融：同一 `--search-mode Coordinator --automatic-search` 命令加 `--no-shared-evidence`，仅关闭跨成员跨回合探测复用和终局见证回传。根、节点/时间预算及最终政策不变，开关不出现在玩家设置。配置写入 `searchPolicy.DisableSharedEvidenceForTesting`；日志的 `ranked_candidates` 是见证命中数，`reordered_candidates` 才是实际换位次数。当前反馈只用于既有 Beam 完全平局，未知/终局位置保持。见[合并后验收与失败对照](strategy/shared-search-evidence-20260927.md)。

## 上下文结果估值研究

模型推理支持 schema10 的 `FactorWeights` 二阶交互，并明确兼容没有因子的 schema9；原始观察继续为8。外部因子训练及完整预算口径见 [`tools/OutcomeValuation`](../tools/OutcomeValuation/README.md)，不会成为新的玩家搜索模式。

`--collect-outcome-values --dop 1` 输出 `outcome-context.json` 与同池完整胜利/真实终局死亡见证的 `outcome-rows.json`；未完成/被裁剪状态不标失败。使用 `--fit-outcome-values <路径数组JSON> <模型JSON>` 拟合无需游戏启动的线性基础项和64棵深度6直方图成对残差树（模型schema10，观察schema8）。同时输出 `<模型名>.linear.json`，供同一次拟合的纯线性消融使用。`tools/OutcomeValuation/train.py` 对采集加拟合设置至多1800秒硬进程时限。

`--objective-search --outcome-value-model <模型JSON> --search-mode Coordinator --potion-policy Disabled --dop 1` 仅用于替代排序实验，内部共用自动协调器；CLI不叠加自动搜索或旧组合参数。它未通过默认替代验收，Runtime不会启用。笔尖计数8夹具可以追加 `--verify-outcome-context`，验证实机后续变化隔离、Fork独立及分支费用/计数可见性。具体输入、失败回归、内存和泛化限制见[报告](strategy/contextual-outcome-values-20260927.md)。

`--check-outcome-ranking` 运行44项纯标签/拟合/加载/多策略分组及线性外推/单位变换合同；`--verify-outcome-context` 另验证选择列数值投影与复用清理。替代排序也允许 `--search-mode Evaluate --observe-ordering <上限>` 做首次裁剪诊断；该数据不能作性能基准。训练/测试须先以 `tools/OutcomeValuation/dataset.py` 检查模板、实际遭遇和牌组隔离，不能只换种子。见[成对排序报告](strategy/pairwise-outcome-ranking-20260927.md)。

训练与重拟合必须重复提供 `--evaluation-manifest <验证清单> --evaluation-manifest <封存测试清单>`，执行三组两两结构隔离。最终评测额外提供 `--validation-manifest <开发验证清单>`；检查装备资料不等于运行测试，最终搜索使用记录仍单独冻结。共用保路实验的 `selectedPrimaryIncumbentUpdates` / `selectedPrimaryIncumbentBranchesPruned` 位于 `harness-result.json` 的 `search.solverMetrics`，仅代表所选 solver，不能当成全协调器总量。

`--collect-outcome-values --objective-search --outcome-value-model <模型>` 使用冻结模型采集自身轨迹，主搜索后离线补查最多6个首回合前缀。`outcome-corrections.json` 单独记录6000ms软额度、各次1000节点/1500ms软额度、真实成本和见证变化；采集运行不能作推理性能基准。根准备选择和跨回合前缀不进入此补查。`--audit-outcome-ranking <路径数组JSON> <模型> <输出>` 只审计被选中的训练输入偏好，不是独立验收。路径数组可将一个实际根的多个采集文件放入子数组，组号按文件重映射。见[本轮研究](strategy/histogram-outcome-ranking-20260927.md)。

当前观察 schema8 包括分支奥斯蒂身体/最大HP/可受击状态，Power 区分主人、奥斯蒂和与敌人身体一致的 roster 索引，并有同名敌方 Power 总层数。`resource/current-max-energy` 与 `resource/current-hand-draw` 复用引擎查询当前分支的规则量，不消费延迟资源，不代表下一回合保证收入。旧行没有这些量，必须重新采集，不能靠改 schema 或填零混入。新颖性搜索的兄弟子节点池参与观察（最多64个，计入256总池），完成边界使用原权威终局摘要。`outcome-collection.json` 记录总态/池/新颖性池/有标签/导出条数，用于发现“已有胜利却没有监督”的采集缺口。

`search-budget-boundaries.json` 分别记录回合层和新颖性时间停止，以及所选结果的TimeLimit。两种搜索入口统一纳入 `timeBoundaryObserved`。2026-09-27本轮修正之前，Coordinator入口没有更新该标志，新颖性独立时间停止也漏记；历史false只能表示宿主未记录，不能用来证明所有成员没有时间截断。保留历史原始数据，不将诊断修复当作算法提速。

### 外部结果排序训练器（离线研究）

`--export-outcome-ranking <训练输入> <空输出目录>` 导出共用 C# 准备器裁定的偏好图、float32 零值矩阵和线性基础项，带格式与内容摘要。可选 CPU 训练流程见 [OutcomeValuation](../tools/OutcomeValuation/README.md)。`--predict-outcome-features <模型> <输入 JSON> <输出 JSON>` 只通过现有模型读取/推理边界验证转换；输入数组成员为 `Character` 与 `Features`，非有限数值拒绝。它们均不运行战斗，也不替代独立战斗质量验证。

## 独立获胜路线模仿目标

`--collect-outcome-values` 额外保存 `imitation-rows.json`：原生程序集标识、观察版本、最佳可靠完整胜利、动作数与各观察是否属于该获胜路径。路径最多8,192个脱离状态/政策键；无胜利或路径超过上限时明确标记不可用。不修改原 `outcome-rows.json` 的真实续局标签，未被选中不等于死亡或不可获胜。

离线 `--export-imitation-ranking <inputs.json> <empty-directory>` 共用原数值导出与小型模型；输入必须明确目标：

```json
{
  "schemaVersion": 1,
  "trainingTarget": "winning-route-imitation",
  "maximumRowsPerRoot": 512,
  "roots": ["/absolute/path/to/imitation-rows.json"]
}
```

每个路径代表一个物理根。先验证完整文档，再有界保留路径正例和同池其他观察；图构建按根等权、同池配对并去重。无教师、缺失路线成员标签、重复文件、混合角色和不匹配版本均拒绝。导出清单的 `trainingTarget` 区分两类边，模仿边不能解释成已验证的胜败比较。

`--audit-imitation-ranking <inputs.json> <model.json> <output.json>` 输出各根对专家分支的准确率/损失及显式缺失原因。它只度量模仿，不能证明搜索决策改善。研究协议还必须核对装备/完整根戳、家族与近牌组隔离，单列准入/排除场景，并在冻结模型后执行独立实际战斗对照。默认游戏策略与玩家选项不变。

联合监督使用 `--export-joint-ranking <inputs.json> <empty-directory>`，输入引用已经固定的两个采样清单：

```json
{
  "schemaVersion": 1,
  "trainingTarget": "completed-outcome-and-imitation",
  "outcomeInputs": "/absolute/path/to/outcome-inputs.json",
  "imitationInputs": "/absolute/path/to/imitation-inputs.json"
}
```

两个清单须按同一物理根顺序排列，分别指向同一采集目录的 `outcome-rows.json` 与 `imitation-rows.json`；不接受重复目录或多次roll-in合并。终局清单固定为角色头、全偏好、逐对权重。两个原抽样器保持不变，图准备给目标分开池命名空间，合并后每物理根最多4,096对、总权重1，特征支持也只计一次。`pairKinds` 的前三项仍是已完成结果的胜败/政策/动作数比较，第四项是独立模仿边。相同特征不能证明状态身份相同，不跨目标拼接或比较观察。目录配对只防止操作错配，完整根身份、原文件摘要、教师来源及数据隔离仍必须在研究协议中核验。
