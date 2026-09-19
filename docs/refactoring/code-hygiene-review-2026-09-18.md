# CombatSolver 代码整洁度审查与轻度清理（2026-09-18）

## 范围与基线

上游已执行一次 `git fetch origin`：`origin/main = 0e6cc2d`（release: prepare 0.41.0），项目版本 0.41.0。工作树 `CombatSolver-hygiene`，分支 `chore/code-hygiene`，复制主检出的 `local.props`。主检出及 potion-odds 工作树仅作只读材料来源；不部署、不发包、不推送、不启动可见 Steam，游戏与真实存档只读。

本报告审查部分先于生产代码修改写入。已读取 AGENTS、架构地图、滚动路线、architecture-boundary-refactor skill 与相关两端门禁；保持 fail-fast、后台快照读取、搜索政策、公共 API 和设置 schema。路线明确不做的 DI/事件总线/多程序集/ECS/多人/扩大预算，本轮均不做。

统计覆盖全部受跟踪C#源码，分析器覆盖主工程；人工阅读集中在指定排序热点、诊断定位处、实验工具说明与兼容入口，不声称逐行人工审阅16万行代码。

## 体量与热点（清理前）

口径：`git ls-files` 中 `src/**/*.cs`，按物理行数含空行和注释，排除产物。669 文件、162,891 行。大小不是运行时成本；Testing 占 25.17%，Search 占 37.09%。

| 目录 | 行数 |
|---|---:|
| `src/Search/` | 60,414 |
| `src/Testing/` | 41,001 |
| `src/Engine/` | 21,688 |
| `src/Runtime/` | 18,311 |
| `src/Prediction/` | 9,736 |
| `src/UI/` | 8,538 |
| `src/Api/` | 2,142 |
| `src/Diagnostics/` | 542 |
| `src/Replay/` | 456 |
| `src/Strategy/` | 63 |

全部超过 2,000 行的文件，按行数降序（亦为文件热点排行）：

| 文件 | 行数 | 判断 |
|---|---:|---|
| `src/Search/CombatBeamSolver.BeamRetentionPolicy.cs` | 8,078 | 已有策略对象与 partial 边界，但一个文件混有排名、配额、路由、Pareto、能力承诺；是下一轮真正拆分的首要热点。 |
| `src/Search/CombatBeamSolver.Expansion.cs` | 4,396 | 已有展开边界；动作、选牌、回合尾与续执行仍共居，属于边界内继续膨胀，值得后续按所有权拆分。 |
| `src/Runtime/SolverController.cs` | 3,451 | 会话所有权已抽出；主线程编排、采用、部署、取消集中，当前大小部分自然，后续按生命周期审查。 |
| `src/UI/SolverOverlay.cs` | 3,303 | 已有 snapshot 隔离；布局、交互和状态刷新集中，存在进一步拆分空间。 |
| `src/Testing/UnattendedTestRunner.ForkBoundaries.cs` | 3,132 | 专题测试分片，众多独立 Fork/续执行合同导致自然增长；不是缺少运行时层次。 |
| `src/Search/CombatBeamSolver.CyclePlanning.cs` | 2,958 | 周期识别、进展和出口租约同属策略，但仍复杂；先明确政策合同，再考虑拆分。 |
| `src/Search/SimulatedCombatState.cs` | 2,918 | 领域状态 partial 的主分片，接口和状态所有权集中属自然；不在本轮改镜像或表示。 |
| `src/Search/CombatBeamSolver.OrderedMutationRetention.cs` | 2,675 | 租约与预算账本的单一所有者，大小有语义原因；可读性债务存在，不能机械切文件。 |
| `src/Testing/UnattendedTestRunner.SearchPolicy.cs` | 2,484 | 多个策略合同的测试集合，属于自然测试规模；可按合同分文件，但本轮不做。 |
| `src/Runtime/SearchGcPolicy.cs` | 2,413 | 进程级 GC/NoGC 生命周期边界，Recovery 已分片；职责相连且高风险，不能为行数拆开。 |
| `src/Search/CombatBeamSolver.Phases.cs` | 2,394 | 阶段推进 partial，预算、取消、排空与收尾密切关联；自然边界内偏大。 |
| `src/Testing/UnattendedTestRunner.cs` | 2,363 | 已有 ProtocolHost/ScenarioBuilder/Executor/Assertions/Writer；共享 fixture helper 较多，后续可按语义归属整理。 |
| `src/Search/CombatBeamSolver.Retention.cs` | 2,037 | 保路入口与多组策略比较器混合，存在本轮可抽取的精确重复；其余留待策略合同化。 |
| `src/Search/CombatSearchCoordinator.cs` | 2,034 | 主搜索、Smart 梯度与组合编排的现有所有者；已有 PowerRoutes/FailureRecovery 等分片，大小部分自然。 |

## 分析器与死代码

临时 `.editorconfig` 将 IDE0051/IDE0052/IDE0060/IDE0005/CS0162/CA1822 设为 warning。全规则 `AnalysisLevel=latest-all` 构建运行约5分钟无诊断输出后主动取消，不计成功；改用 `AnalysisMode=None` 加上述显式规则，35秒完成，132 warning / 0 error。两轮均带 `CopyModOnBuild=false`、`TreatWarningsAsErrors=false`、`EnforceCodeStyleInBuild=true`。原始证据在 `.local/hygiene/targeted-analyzer-build.log`、`analyzer.sarif`。SARIF还含已抑制的生成代码诊断及隐藏提示，不能把762个原始条目当作762个构建警告。临时配置不提交。

| 规则 | 有效警告 | 分类与处理 |
|---|---:|---|
| IDE0051 | 6 | 3个真死方法，3个InlineArray存储字段误报 |
| IDE0052 | 3 | 保留生命周期引用及带反射初始化的字段，见下表 |
| IDE0060 | 74 | 未用参数不等于死方法；保持签名/重载/Hook和测试合同，不做参数重构 |
| CA1822 | 49 | 可static不等于无调用者；全部保留，不扩展到静态化重构 |
| CS0162 / CS0169 / CS0414 | 0 | 没有相应不可达/未使用字段编译警告；不据此声称全仓库无不可达路径 |

IDE0005另用 `dotnet format style CombatSolver.csproj --no-restore --diagnostics IDE0005 --verify-no-changes --severity info --report .local/hygiene/using-report.json` 取证（只读检查，发现问题退出2）。构建模式未输出此规则，不能将其视为零。共175条：Api 2、Engine 19、Prediction 19、Runtime 18、Search 61、Testing 54、UI 2。计划删除Api/Runtime/Search/UI的83条（48文件）；Engine/Prediction镜像与Testing保持原文件，剩余92条不作为行为风险修复。每条删除对应分析器原始文件/行号，完整清单保存在 `.local/hygiene/removed-usings.json`，不顺便格式化文件。

| 私有成员 | 判定与证据 |
|---|---|
| `PreCombatForecastApi.CompleteAfterLiveValidation(snapshot, PreCombatForecastResult, token)` | IDE0051；4个实际调用都传Task，未用重载只是Task.FromResult转发；无字符串/nameof/反射引用。删除该重载，保留Task重载及公共API |
| `CombatBugReportExporter.ReadSharedFile` | IDE0051；src/tools仅定义，仓库无字符串/反射调用。删除14行方法及间隔 |
| `StrategicEffectVector.SaturatingSum` | IDE0051；当前RetentionValue直接固定五项累加，无字符串/反射引用。同名PowerCardValuationMath/DrawDiscardTransition方法属于其他类型且仍有调用，不动 |
| `HandFingerprintBuffer._element0`、`CycleStartupNodeBuckets._element0`、`CycleStartupKeyBuckets._element0` | IDE0051误报：分别是InlineArray(10/5/5)必需的唯一实例字段，通过编译器生成的索引访问。全部留 |
| `PerformanceRecording._lifecyclePatches` | IDE0052：保存PerformanceLifecycle.Start()返回的Harmony对象，赋值有安装补丁副作用；仅未读不足以证明删生命周期持有逐位无影响，留 |
| `CombatPredictionDynamicVarExtensions.GetBaseVar/GetExtraVar` | IDE0052：初始化执行AccessTools.Method/CreateDelegate，删除会改变类型初始化的失败边界；且在本轮禁止语义变动的Engine范围，留 |

容易被误删但本轮保留的反射/测试入口（不是本轮分析器确诊死代码）：`BaseLibCloneConcurrencyPatch.Prefix/Finalizer`、`PowerAmountComparisonPatch.Transpiler` 由IPatchMethod/Harmony发现；`UnattendedTestRunner.HookFilterBasePrefix/HookFilterNativeKeywordPrefix` 由GetMethod(nameof(...))/HarmonyMethod安装；`CloneConcurrencyStagePrefix` 是空方法但被AccessTools.Method取作补丁；`CombatBeamSolver.ActionChoicesForReplay` 在ForkBoundaries用字符串反射；`VerifyCycleExitTicketSettlementPolicyForTesting` 和其私有 `VerifyCycleStartupRetentionPolicyForTesting` 由ForkBoundaries/SearchPolicy测试调用。空方法、private或只供测试都不是删除依据。

CA1822 仅表示可静态化，不证明无人调用；IDE0060 不证明方法死亡。Harmony 按约定名、特性和 `AccessTools.Method`/`GetMethod` 发现入口，不能仅凭 IDE0051 删除。只删编译器/分析器确证且仓库字符串及反射引用核对为空的私有成员；不改可见性或公共签名。

## 废弃与实验遗留裁决

下表工具行数统计包含说明、补丁和数据文件，不等于生产 C# 行数。主工程排除 `tools/**/*.cs`。

| 项目 | 行数/调用者与保留依据 | 建议 |
|---|---|---|
| `CompactStatePrototype` | 1,311；独立 CLI，README 明确保留为表示研究参考；gc-issue36-implementation 链接原始结果 | 留；不能以“未进生产”推导废弃 |
| `ChoiceContinuationPrototype` | 1,451；build.py/run.py，固定 `1ef4601` 重现；DEVELOPMENT_NOTES、TEST_MATRIX、性能报告都有证据链接 | 留；旧版本原型与已接入生产的续执行不是两套同时启用的引擎 |
| `ExperimentalAdaptiveGc` | 564；GcPolicyChecks.csproj 直接编译唯一控制器，文档明确归档与15项合成检查 | 留；删除将破坏检查 |
| `ExperimentalListenerSlots` | 699；HookListenerSnapshotChecks.csproj 直接编译 ImmutableListenerList，README 明确保留长搜退化反例 | 留；删除将破坏检查与反例复现 |
| `ExperimentalSmartSoftLimit` | 80；没有生产调用，保留192/512 MiB实验补丁；DEVELOPMENT_NOTES明确“仅保留实验补丁” | 留；符合用户要求的研究证据豁免 |
| `BfwsResearchChecks` | 188；直接链接生产 BfwsPackedNovelty/BfwsEscapeBudget/BfwsBoundedOpen/NoveltyPortfolioBudget 等，TEST_MATRIX和固定语料有链接 | 留；名字含 Research，但检查的是正式功能 |
| `src/Search/Bfws*` | NoveltySearch 与 SearchRunContext 的生产消费者；UseNoveltyPortfolio 冻结到政策、设置、缓存和问题包 | 留；不能删除当前关闭时未走的正式可选路径 |
| `SecondRankBand` | BeamWidthPortfolio 及 PowerRoutes 构造成员，Retention 启用 BeamRetentionPolicy 的次段规则 | 留；是正式保路策略，不是无消费者实验开关 |
| `SeatPricing` / `TurnCommitmentFloor` | 当前 src/tools/docs 的文本搜索无这两个符号（排除历史 patch/JSON） | 当前基线不存在，不能把其他分支的发现写成本轮缺陷 |
| SolverSettings 的 243/244/246 迁移 | Load/默认设置走 ApplyCurrentPerformanceMigration；旧版本重置预设及 NoGC，244启用组合，246推进版本。没有独立245分支 | 留；删除会改变旧设置加载行为且违反不改 schema/行为 |
| `STS2_UNADAPTED_FEATURES_AUDIT.md` | 明写模组 v0.6.0，旧审计两处明确不得作为当前覆盖结论 | 留历史内容，补醒目的版本边界及当前导航；不伪装成0.41.0审计 |
| `coverage/` | 700 unattended 文件、22 novelty-search 文件、12根目录清单；旧短/深请求仍由 ProtocolHost 兼容迁移 | 留；日期旧或字段旧不足以证明夹具过期，详见后续静态统计 |

本轮没有发现同时满足“无调用者且无文档保留依据”的上述工具目录，不为凑删除量归档，也不删除无人测试夹具。因而不触发工具/夹具删除后的游戏内无人场景要求。

coverage静态检查：全部JSON可解析；unattended下691份JSON（另9份其他文件）。91份有forceShortSearchOnly、92份有shortSearchBudgetOverrideMilliseconds、3份有deepSearchBudgetOverrideMilliseconds，ProtocolHost有明确兼容路径。两份gc-issue36设置文件保留short/deep字段供历史复现，SolverSettings继续读取deep字段。解析和调用核对不等于691份场景本轮重跑；没有把旧夹具直接认定为“过期可删”。

## 重复逻辑与可提取边界

当前 Search 中精确文本 `PotionStrategicCost.CompareTo` 命中12处（其中1处是 OptionalPotionStrategicCost）。一行相似不代表整条比较政策相同。

| 位置 | 判定 |
|---|---|
| Retention 的 CompareCycleExitFamilyCandidates / InFlight / Newest | 可提取：保留各自租约/余量/起点前缀，三者共享的后缀逐语句相同：HealthRisk升序 → 药水成本升序 → Turn升序 → ActionCount升序 → HP降序 → Score降序 → 原确定性指纹。只引入同一 partial 内 private static helper |
| CycleRegionRetention 的 StableCandidates | 不与上述合并：ActionCount在Turn之前，尾部包含动作、目标、选择等键 |
| CycleStartup / Purification | 不合并：HealthRisk方向与键的位置不同，purification先比较牌组污染、大小、setup |
| CrossTurn / CycleProbe | 不整体合并：中间插入语义变化次数、进展或结构重复，短路次序必须保留 |
| BeamRetentionPolicy / FinalPlanOrdering / SolverInterimResultOrdering | 不合并：中间保路、完整胜利、强制/可选用药、偷窃、生存与资源交换的输入和顺序不同；已有 ComparePrimaryQuality / IsResourceTradeImprovement 是窄公共规则 |
| PotionUsePolicy.StrategicHpCost(string) 与 PotionStrategicCostLookup | 无双权威规则：Lookup 缓存单solver的(ID, renewableRock)标量，miss调用原Single再委托PotionModel重载；缺失/重复ID仍失败。不改为FirstOrDefault或全局表，不作性能优化 |
| UnattendedTestRunner.* 夹具重复 | ForkBoundaries 多处重复创建 DefendDefect / 选择候选，但对应独立历史、暂停、嵌套和副本隔离状态；共享 AssertSnapshotEqual 等入口已存在。不能把独立状态合为共享对象，本轮不抽测试生命周期 |

等价性计划：私有死代码提交前用EQ 10根；比较器类变更前后用EQ 10 + FULL 40根一次全量。High 90/50000、分支48/28/36、Coordinator、Smart、DOP1、workers=2，指定0.41.0基线DLL；批次中不重建模组DLL。除 compare_results 的 IDENTICAL 外保存有效性与剪枝计数对账；不把离线一致称为原生游戏正确性证明。

## 命名与注释

中英注释并存本身不是行为缺陷；保留准确的所有权/排序/失败理由，不全量翻译。Research/Experimental 名称应结合文档判断。SolverWeights.NoVictoryEscalationFactor 的 remarks 把历史极高50000节点写成当前配置，并由样本推导普遍的Beam/节点关系，已与当前四档60000/120000/250000/500000不符；本轮保留那组实测数字（它是这个参数唯一的实证依据），改为标明“历史实测、当时极高档 50 000、只作量级参考”，并补一句它不是普遍规律。FailureRecovery的remarks存在相同问题，且将五条样本称为“四组”；同样保留数据、补上当前的启动/停止条件说明，把“四组”改为“五组”。TurnCommitmentFloor在本基线不存在，不编造修正。

## 一句话总评

**3/5：大而有序，局部策略文件已明显拥挤，但不是大而失控。** 有明确状态所有者、partial职责地图、失败边界、双平台门禁和可复跑证据；主要债务在8千行保路政策、多重租约/预算短路顺序、4.1万行测试的导航，以及历史文档与当前事实混读风险。仅靠删using无法解决这些问题，也不应借本轮轻清理拆架构。

## 实施与验证记录

### 执行偏差与处理

首批基线50根的最后一根 `FULL-SILENT-ELITE-03` 尚在运行时，误启动了一次候选构建；18.06秒内取消，日志为 `.local/hygiene/dead-build-interrupted.log`。这违反了本轮“批次运行时不构建”要求，不能省略。基线进程加载独立 `CombatSolver-base-0410` 目录的DLL，该DLL未改，但资源条件受到干扰；因此保留受影响产物作记录，排除其验收用途，在无构建并行时重新取得这一根基线，其余49根不重跑。后续本地构建脚本先拒绝任何仍运行的离线宿主请求，再执行带 `CopyModOnBuild=false` 的构建。取消的构建不记作成功。

### 分类别结果

1. **死代码与using**：50个生产文件删除112行，包含3个私有方法、83个using及间隔空行。Release构建0 warning / 0 error（9.44秒），Bash门禁 `REFACTOR_BOUNDARIES_OK search_files=192`。EQ 10根双侧20份结果均有效且无时间边界；600项剪枝计数全等；修正比较器口径后 `roots=10 fields=983 mismatched_roots=0 left_only=0 right_only=0 => IDENTICAL`。对应 `.local/hygiene/dead-build.log`、`dead-boundaries.log`、`dead-validity.json`、`dead-comparison.json`。本类没有删除源码文件，search_files计数不变。

该类本地提交为 `87f6666`，共53文件、增加142行/删除112行；新增主要是审查报告和导航。

2. **循环出口比较器复用**：仅修改Retention分片，增加12行、删除43行，净减少31行。三个原后缀各21行，提取前脚本逐文本比较确认一致，记录 `.local/hygiene/extraction-proof.json`；新helper仅把首个赋值改为局部变量声明，其余比较/短路/指纹调用不变。调用实参均为原局部值，没有新增读取或转换。同步架构地图与skill说明；不在结构门禁里钉死这个私有方法的名字或调用次数（门禁管的是边界，不是某个 helper 的存在）。未引入状态、公共API或抽象层。Release构建0 warning / 0 error（9.47秒），Bash门禁 `REFACTOR_BOUNDARIES_OK search_files=192`。复用已验收的50根基线，EQ 10 + FULL 40根候选只跑一次，`roots=50 fields=4673 mismatched_roots=0 left_only=0 right_only=0 => IDENTICAL`；双侧100份结果有效、0时间边界、3000项剪枝计数全等。对应 `.local/hygiene/ordering-build.log`、`ordering-boundaries.log`、`ordering-validity.json`、`ordering-comparison.json`；宿主退出码0后清理50个候选logs目录，保留结构化结果。

该类本地提交为 `9ac20e3`，共8文件、增加53行/删除44行；其余改动为边界说明、双端门禁和验证记录。

3. **注释与历史文档**：`SolverWeights` 与 `FailureRecovery` 只改注释：保留历史实测数字并标明当时配置，补充补搜的启动/停止条件与非单调性说明；根目录旧审计增加历史快照提示及当前导航。3个文件增加11行、删除32行，净减少21行；没有改变可执行语句。最终Release构建0 warning / 0 error（10.11秒），带 `CopyModOnBuild=false`，日志 `.local/hygiene/comments-build.log`。Bash门禁 `REFACTOR_BOUNDARIES_OK search_files=192`，日志 `comments-boundaries.log`；新增文档链接的本地目标均存在。本类不重跑已通过的10/50根离线证据。本类提交说明为“修正文档中的历史覆盖边界与过时搜索注释”。

### 比较器口径

上游0.41.0的compare_results仅对顶层排除时序字段，漏掉新遥测的 `firstRoutePublishedMilliseconds`、`peakManagedHeapBytes` 和 portfolioMembers/powerRouteMembers 内的耗时、分配及堆大小，原始比较因此10根均报DIFFERENT（`.local/hygiene/dead-comparison-original-tool.json`），动作与剪枝并无差异。只读核对并复制用户指定potion-odds工作树已存在的compare_results到 `.local/hygiene/compare_results.py`：增加上述4个时间/内存字段（另2个是allocatedBytes、managedHeapBytesAfter），并递归应用既有排除表。搜索预算、成员选择/顺序、展开/转移、终止原因、路线质量与动作全部继续比较；未修改原始结果或被保护工作树，也未把整个成员列表排除。两轮候选统一使用此比较器，不为口径修正重跑搜索。IDENTICAL指约定的确定性字段相同，不包括墙钟/GC/分配字节，也不代替原生游戏正确性验收。

替换基线的采纳记录见 `.local/hygiene/baseline-accepted-summary.json`：49份原结果加1份无构建并行的重跑结果，共50份有效结果。受干扰原结果另存在 `.local/hygiene/excluded-build-overlap/`。已清理第一阶段61个本任务创建的logs目录，保留结果、路线、计划和比较JSON。

宿主Passed/valid表示搜索执行正常且未触及时间边界，不表示该根必胜；死亡路线也是需要原样保持的基线结果。

## 后续建议（本轮不做）

1. 优先给 BeamRetentionPolicy 的各保路通道建立输入/输出/排序合同，再按具体政策所有者分批拆分；保持原迭代与短路顺序。
2. Expansion 按动作准备、选择续执行、回合尾排空边界拆分，保留唯一快照归属。
3. 测试按生命周期合同改善导航，公共fixture仅复用无状态构造，不合并独立可变实例。
4. 统一历史证据的版本标识；设置迁移需另行定义兼容窗口，不能在代码整洁度任务里删除。
