# CombatSolver 技术债审计与分批清理

## 范围与证据口径

起点为用户新整理的 `fork/chore/code-hygiene`：`43e5942`，独立工作树 `CombatSolver-debt` / `chore/tech-debt`。没有写入主检出、hygiene、potion-odds、base-0410、游戏目录或真实存档。继承上一轮对研究工具、反射入口、设置迁移、InlineArray 的裁决，不重新把这些列为待删候选。本文前半部分在生产代码修改前生成；后半部分随提交补齐证据。

完整清单在本工作树 `.local/debt/before/`；复跑入口见 [CodeDebt](../../tools/CodeDebt/README.md)。原始 SARIF 为 `.local/debt/analyzer.sarif`，诊断日志为 `.local/debt/analyzer.log`。工具与源码各自分开计数；历史性能和原生游戏记录不冒充本轮验收。

## 1. 十项静态分析

### 1.1 全量分析器

`nohup` 全量 latest-all 正常结束，耗时2分46.33秒，退出0，4344警告/0错误；本轮没有因耗时而取消。SARIF共4970条：实际警告4344，被抑制生成代码诊断586，提示40。后两类不计入真实警告。临时配置已退出时移除；普通构建另验0警告/0错误。

| 重点规则 | 实际警告 | 裁决 |
|---|---:|---|
| IDE0051 | 3 | 3个均为上一轮确认的InlineArray，留 |
| IDE0052 | 3 | 3个均为上一轮确认的生命周期/反射初始化，留 |
| IDE0060 | 74 | 仅对private普通调用提案；不改协议/Hook/公开或internal签名 |
| CA1822 | 49 | 仅对private且普通直接调用的候选提案 |
| CA1805 | 4 | 4个显式零初始化计数，保留声明意图，不凑删除量 |
| CA1806 | 0 | 未命中 |
| CA2000 | 295 | 所有权转移候选，不能只凭诊断加Dispose；单列清单供专项审计 |
| CA1031 | 45 | 与全部catch扫描交叉定位；只报告 |
| IDE0059 | 4 | 4项逐项核对；保留可能影响异常时out可见值或求值的语句 |
| IDE0058 | 1685 | 返回值丢弃不等于调用无副作用，不批量删调用 |
| CS0162 | 0 | 未命中 |
| CA1508 | 50 | 含闭包写入、原生/测试状态更新，不能按常量传播结果删除 |
| IDE0079 | 0 | 未命中 |

完整分规则统计见 `diagnostic-summary.json`，逐条消息/位置/抑制状态见 `diagnostics.json`。49条CA1822和74条IDE0060经可见性与调用约束筛选，首批可处理34个方法（26处static、41个未用参数），22个文件；其余原因逐条保留于 `.local/debt/safe-proposal.json`。实参删除仅接受局部变量、参数和字面量；方法组、nameof、限定接收者、命名/ref/省略参数及未绑定调用不自动动。

IDE0059四项：RootCombatCardGenerationPoolSnapshot 的out初值删除可能改变异常时调用者观察值；SimulatedCombatState的monster变量仍承担空值拒绝；ParallelExpansion的snapshot读取保留潜在求值/失败边界；Defect的turns初值调用PowerRemainingTurns，不能因为结果未用就直接删调用。CA1508例：Coordinator的回调会修改currentCompleteAdoptableResult，测试的卡牌/Power方法会修改状态，分析器的局部“不变”不代表真实死分支。

### 1.2 克隆检测

候选2960对：literal 32、identifier-only 1845、structural-review 1083；完整位置、起止行、token相似度、重命名映射见 `clones.json`。literal是代码token一致（忽略空白/注释），并非未检查文本就直接可抽取。

可实施：CyclePlanning的PendingCycleExit比较器与现有CompareCycleExitQuality完整方法体相同，直接委托既有helper；Expansion两处开局SearchNode构造的原文相同，可提取无状态工厂，保持参数求值和新对象创建次序。前后原文证明另存，不合并调用方的筛选或生命周期。

明确不合并：CardChoiceSupport/Testing的两组38/27代码行是独立基线；PredictionCoverage/Testing的23行是差分基线；Engine/Prediction克隆在禁改语义范围；Testing的18/16/15行多为独立可变fixture；串/并行Expansion的23/19行受不同释放/作业所有权约束；FinalPlanOrdering与保路的12行只有局部键相同；CycleProbe在尾部插入结构重复次数，不得与完整出口规则混为同一政策。Overlay布局中归一化相似只表示控件搭建结构，不证明事件/父容器/所有权可合。

### 1.3 复杂度、形状与partial耦合

669文件、9109个方法/访问器/局部函数、1227个源码类型；语法错误0。复杂度口径见工具README。字段耦合只统计Roslyn绑定到同一partial类型另一文件的private源码字段，主构造参数的编译器捕获不在字段统计中，故不是全部状态耦合的上界。完整表为 `methods.json`、`types.json`、`coupling.json`。

Top 30按圈复杂度排序（行数含签名/方法体；参数、局部和嵌套另列）：

| 方法与位置 | 行数 | 圈复杂度 | 嵌套 | 参数 | 局部声明 |
|---|---:|---:|---:|---:|---:|
| `src/Testing/UnattendedTestRunner.Executor.cs:33` ExecuteAsync | 1394 | 368 | 4 | 1 | 86 |
| `src/Search/CombatBeamSolver.OrderedMutationRetention.cs:1234` VerifyOrderedMutationRetentionPolicyForTesting | 1441 | 235 | 2 | 0 | 148 |
| `src/Prediction/MonsterMoveEffects.cs:229` Apply | 724 | 199 | 3 | 6 | 16 |
| `src/Testing/UnattendedTestRunner.SolverPolicy.cs:182` AssertInitialSolverResultAsync | 708 | 195 | 4 | 1 | 90 |
| `src/Testing/UnattendedTestRunner.cs:885` RunMonsterMoveDifferentialAsync | 885 | 173 | 3 | 3 | 96 |
| `src/Search/CombatBeamSolver.Phases.cs:110` SolveCore | 1957 | 158 | 7 | 0 | 118 |
| `src/Search/CombatBeamSolver.BeamRetentionPolicy.cs:2577` RankBest | 961 | 156 | 5 | 6 | 94 |
| `src/Testing/UnattendedTestRunner.ControllerSessions.cs:55` AssertControllerSessionLifecycleAsync | 727 | 131 | 2 | 1 | 62 |
| `src/Testing/UnattendedTestRunner.ScenarioBuilder.cs:48` BuildAsync | 471 | 102 | 3 | 0 | 57 |
| `src/Testing/UnattendedTestRunner.KnownExoskeletonsRouteReplay.cs:27` RunKnownExoskeletonsRouteReplay | 372 | 102 | 4 | 3 | 52 |
| `src/Prediction/CorePowerSupport.cs:21` ApplyCardPowers | 395 | 100 | 3 | 9 | 23 |
| `src/Testing/UnattendedTestRunner.ControllerSessions.cs:825` AssertBoundedSmartPotionAuditAsync | 639 | 89 | 2 | 1 | 40 |
| `src/Testing/UnattendedTestRunner.cs:521` WaitForPlayableCombatAsync | 254 | 78 | 5 | 0 | 23 |
| `src/Testing/UnattendedTestRunner.SolverPolicy.cs:104` HasInitialSolverExpectation | 77 | 76 | 0 | 0 | 0 |
| `src/Search/CombatBeamSolver.BeamRetentionPolicy.cs:7459` MultiObjectiveDominates | 84 | 75 | 1 | 2 | 2 |
| `src/Search/CombatBeamSolver.BeamRetentionPolicy.cs:5494` VerifyOrderedMutationKeyPolicyForTesting | 717 | 74 | 2 | 0 | 83 |
| `src/Testing/UnattendedTestRunner.BaseLibCardModifier.cs:19` AssertBaseLibCardModifierBoundaryAsync | 457 | 73 | 1 | 2 | 76 |
| `src/Search/CombatBeamSolver.StateEvaluation.cs:43` Snapshot | 552 | 72 | 4 | 6 | 142 |
| `src/Search/CombatBeamSolver.Expansion.cs:463` Expand | 390 | 72 | 4 | 1 | 60 |
| `src/Testing/UnattendedTestRunner.KnownSoulGenerationContext.cs:15` RunKnownSoulGenerationContext | 269 | 72 | 4 | 4 | 32 |
| `src/Search/StrategicEffectModel.cs:122` Build | 258 | 70 | 5 | 6 | 58 |
| `src/Search/CombatBeamSolver.BeamRetentionPolicy.cs:814` AddOrderedMutationPortfolio | 971 | 68 | 3 | 3 | 82 |
| `src/Search/CombatBeamSolver.CycleRegionRetention.cs:1386` VerifyCycleRegionRetentionPolicyForTesting | 460 | 68 | 3 | 0 | 48 |
| `src/Prediction/CardEffectSpecRegistry.cs:127` Apply | 340 | 67 | 5 | 4 | 31 |
| `src/Prediction/PotionOnUseSupport.cs:74` Use | 259 | 67 | 3 | 4 | 51 |
| `src/Testing/UnattendedTestRunner.KnownCustomRouteReplay.cs:19` RunKnownCustomRouteReplay | 188 | 66 | 3 | 4 | 34 |
| `src/UI/SolverOverlay.cs:1157` RefreshControls | 126 | 64 | 5 | 0 | 12 |
| `src/Testing/UnattendedTestRunner.Potions.cs:18` RunPotionDifferentialAsync | 234 | 61 | 4 | 3 | 38 |
| `src/Search/CombatBeamSolver.Expansion.cs:2499` Replay | 284 | 60 | 3 | 14 | 38 |
| `src/Prediction/CardPowerOnPlaySupport.cs:10` Apply | 199 | 60 | 2 | 2 | 5 |

| Top 10类型 | 源码成员 | 声明文件数 |
|---|---:|---:|
| `CombatSolver.CombatBeamSolver` | 850 | 52 |
| `CombatSolver.UnattendedTestRunner` | 777 | 166 |
| `CombatSolver.SimulatedCombatState` | 717 | 30 |
| `CombatSolver.SolverOverlay` | 269 | 1 |
| `CombatSolver.UnattendedTestRequest` | 218 | 1 |
| `CombatSolver.SolverController` | 194 | 2 |
| `CombatSolver.CombatBeamSolver.BeamRetentionPolicy` | 190 | 1 |
| `CombatSolver.Engine.InCombat.Simulation.CombatPredictionSimulator` | 186 | 21 |
| `CombatSolver.SolverResult` | 185 | 1 |
| `CombatSolver.SearchGcPolicy` | 167 | 2 |

| 跨partial字段耦合Top 10 | 不同外部私有字段数 |
|---|---:|
| `src/Search/SimulatedCombatState.Fork.cs` | 105 |
| `src/Search/CombatBeamSolver.Phases.cs` | 22 |
| `src/Search/CombatBeamSolver.Expansion.cs` | 19 |
| `src/Runtime/SearchGcPolicy.Recovery.cs` | 18 |
| `src/Search/CombatBeamSolver.Retention.cs` | 16 |
| `src/Search/SimulatedCombatState.CardEventHistory.cs` | 14 |
| `src/Search/SimulatedCombatState.cs` | 14 |
| `src/Search/CombatBeamSolver.BeamRetentionPolicy.cs` | 13 |
| `src/Testing/UnattendedTestRunner.Executor.cs` | 11 |
| `src/Engine/InCombat/Simulation/CombatPredictionHistory.CardContinuation.cs` | 10 |

partial提供导航而非访问隔离：Phases/Expansion对同一solver私有状态有大量跨文件访问；BeamRetentionPolicy原本还在一个8078行文件里内嵌第二个大类型，内部耦合不会表现为“跨文件”。拆文件不会减少类型成员或大方法复杂度，本轮不宣称完成架构解耦。

### 1.4 依赖方向与门禁缺口

`dependencies.json`逐条保存源码类型引用和目录方向，`namespace-usings.json`单列CombatSolver using；大量类型共用CombatSolver命名空间，仅统计using会漏掉真实目录依赖。缺少游戏程序集而未绑定的名字见parse.json，不算无引用。

| 主要方向 | 已绑定引用次数 |
|---|---:|
| Testing → Search | 2457 |
| Testing → Runtime | 2191 |
| Testing → Engine | 1549 |
| Search → Engine | 1047 |
| Prediction → Engine | 550 |
| UI → Runtime | 473 |
| Runtime → Search | 285 |
| Search → Prediction | 284 |
| Testing → Prediction | 263 |
| Search → Runtime | 260 |
| Prediction → Search | 240 |
| UI → Search | 152 |

| 文档禁止边界 | 当前门禁覆盖 | 缺口 |
|---|---|---|
| Search→设置/Controller/UI/测试全局 | 两端逐文件forbidden Search reference列表 | 是名称黑名单，不能证明所有Runtime全局/新增UI类型均禁止；Runtime的根/只读合同本来允许，不能按目录一刀切 |
| Api→RequestSearch/SetUpCombat/EnterRoomDebug | 两端精确调用名 | 别名、间接调用不由文本证明 |
| Renderer→SolverResult/PlanAction/PlanCardChoice/ModelDb | 指定renderer文件四类文本禁入 | 新增mutable类型或新renderer不会自动被覆盖 |
| Engine→Search候选政策；Prediction不得决定Beam/终局/UI | 有各功能局部合同 | 没有覆盖整个Engine/Prediction目录的反向类型依赖门禁；新增BeamRetentionPolicy/FinalPlanOrdering依赖方向需补独立语义约束 |
| Replay只依赖标准库 | CheckpointArchive拒绝Godot/Controller/RunManager | 未覆盖全部Replay文件与全部非标准库类型，AppendOnlyEventLog等不能靠这一条保证 |
| worker不读live、结果不保留历史Simulator图 | 根/所有权的多条局部合同 | 线程/逃逸性质无法由using或符号黑名单完整证明，需代表性生命周期/差分证据 |

本轮列缺口，不借纯移动新增门禁规则或把合法目录边当违规。

### 1.5 开关与设置

审计137个bool/枚举项/常量及Novelty选项，逐项声明、读取位置、写入位置、测试/UI/工具/文档提及见 `switch-audit.json`。零已绑定读者候选0；84项有编译期常量值，53项依赖运行时输入。常量枚举成员本身只有一个值，不等同于消费枚举的分支恒定；未绑定名字也不能当作无引用。

bool两支可达不等于都在本轮执行。持久化设置、API/测试覆盖值与record with赋值保留，不能按默认值判死。Novelty FamiliarAllowance的生产创建沿默认0，正额度仅研究检查使用；这是生产配置不可达的熟悉节点扩展路径，不是可删除schema或研究证据。UseBeamWidthPortfolio=false仍允许能力成员，注释“只运行基线”已经过时；SecondRankBand/BaseScoreOnly也在PowerRoutes配置，不再只由BeamWidthPortfolio置位。旧设置迁移不重审、不删除。

### 1.6 全部异常处理

242个catch，50个宽泛捕获且无throw（CA1031实际45条）；全量捕获类型、filter、throw/rethrow、return、continue与块原文在 `catches.json`。无throw不自动等于吞错：Runtime出口可能负责日志/终止/失败回执；文件/诊断流程可能保护游戏。优先审阅下表，但全部只报告：

| 可疑catch位置 | 类型 | 返回值 | continue |
|---|---|---|---:|
| `src/Api/PreCombatForecastApi.cs:178` | Exception | return Task.FromResult(Failure(PreCombatForecastStatus.Failed, requestId, ex.Message)); | 0 |
| `src/Api/PreCombatForecastApi.cs:263` | Exception | 返回Unsupported失败结果，附根异常消息 | 0 |
| `src/Api/PreCombatForecastApi.cs:310` | Exception | return Task.FromResult(Failure(PreCombatForecastStatus.Failed, requestId, exception.Message)); | 0 |
| `src/Api/PreCombatForecastApi.cs:398` | Exception | return Task.FromResult(Failure(PreCombatForecastStatus.Failed, requestId, ex.Message)); | 0 |
| `src/Api/PreCombatForecastApi.cs:523` | Exception |  | 0 |
| `src/Api/PreCombatForecastApi.cs:560` | Exception |  | 0 |
| `src/Api/PreCombatForecastApi.cs:604` | Exception | return; | 0 |
| `src/Api/PreCombatForecastWorker.cs:92` | Exception |  | 0 |
| `src/Api/PreCombatForecastWorker.cs:376` | Exception | 返回Failed结果，附根异常消息和诊断路径 | 0 |
| `src/Api/PreCombatForecastWorker.cs:580` | Exception |  | 0 |
| `src/Api/PreCombatForecastWorker.cs:652` | <all> |  | 0 |
| `src/Api/PreCombatForecastWorker.cs:956` | Exception |  | 0 |

### 1.7 注释真值抽查

808段含数字、文件/类型形状、remarks或“当前/默认/现在”的注释进入 `comment-audit.json`。文件名缺失候选1处：Offline.cs的result.json是运行产物，并非死文件引用，不删。数字原文/位置完整保留；不会把测试样本数字或历史配置替换成现配置。

核对发现：SearchPolicySnapshot.UseBeamWidthPortfolio关闭行为注释漏掉能力成员；SolverSearchProfile的两个实验成员来源已扩展到PowerRoutes；BaseScoreOnly写“九项”但当前BeamRankScore有十个附加项（含SandpitRemaining），改为不硬编码项数。保留上一轮已标明历史配置的SolverWeights/FailureRecovery实测。抽查IPowerCardValuationModel/Registry仍是合同层、Novelty默认关闭及四档预算与实现一致。808段是完整候选扫描，不冒充每条业务断言都获得原生运行证明。

### 1.8 Testing体检

Executor有129个含ScenarioId的if分派条件、165个命名入口；其中131个没有coverage文本引用，20个未在TEST_MATRIX提及，155个没有tools引用。完整三向对账在 `testing.json`。

691份unattended JSON中，110份顶层有scenarioId，另有卡牌/怪物/Power数组等输入材料；10份命中特判。没有特判的其余文件不能统称失效：默认Executor使用请求字段完成普通搜索/差分/部署。没有证据证明某个仅作标签的ScenarioId已失效，本轮删除夹具0。

UnattendedTestRequest源码字段218个，其中100个未在coverage JSON出现对应键；程序赋值和tools构造另列，不能等同测试缺失。缺少静态覆盖的字段应先补协议契约测试，不拆可变fixture。

### 1.9 tools与文档

48个既有tools子目录逐目录列项目、Python文件、文档引用和最后提交日期，见 `tool-directories.json`。55项构建/py_compile全部通过（33个项目、22个Python文件），仅构建未运行入口；日志在 `.local/debt/tool-builds/`。0个失败工具，因此没有需要用户确认删除的工具。

994条本地inline Markdown链接检查通过，未发现可确认死文件/锚点；详见links.json。检查不覆盖网络、复杂HTML和reference-style语法，复跑工具明确此边界。历史文档不得因版本旧而删除。

### 1.10 本地化

English.json有426键；直接Get字面调用缺英文0。结合Roslyn插值模板从初筛115个无原文字面命中的键缩到21个无调用候选；逐项见localization.json。Get转发入口只有既有控件文本包装、const/条件字符串及测试逐键遍历，Format接收还原的模板；未发现拼接这些旧键的动态生产入口。候选均为旧布局说明、旧短深搜索档位/提示，计划删除21个，保留405键；不改现用中文、模板、设置schema或测试逻辑。

## 2. 实施与验证（随提交补齐）

### 批次一：静态工具与私有签名

22个生产文件增加57行、删除101行（净减44）：26个方法加static，41个未用private参数及对应纯实参移除，涉及34个方法。工具输出提案后以补丁应用；工具侧字符串/nameof补查记录 `.local/debt/safe-reference-proof.json`。唯一名称提及ProjectHpAfterThreat用于GcTraceAnalysis栈标签匹配，不反射调用，方法名保持不变。

普通Release构建0警告/0错误（5.11秒，`safe-build.log`），Bash门禁 `REFACTOR_BOUNDARIES_OK search_files=192`（`safe-boundaries.log`）。审计工具的小合同验证通过 `CODE_DEBT_SELF_TEST_OK`，覆盖字面量不得归一化掉、纯标识符克隆、圈复杂度与跨partial字段绑定。新增工具只进入tools，主模组排除tools C#；本批无需离线重跑。

先做安全清理，再做克隆复用，最后分两个提交纯移动BeamRetentionPolicy与Expansion。中风险各批EQ10，全部完成后仅一次EQ10+FULL40。基线直接复用hygiene已有50份，不重跑；比较器也复用指定的时序/内存字段排除版本。宿主最多2个进程；所有主工程构建通过build.sh拒绝离线批次并行，固定CopyModOnBuild=false。

批次一本地提交 `5f12c09`（含工具、报告与导航）：38文件，+988/-101。

### 批次二：旧本地化键与注释

删除21个无调用的旧目录键，426→405；其余键值逐项不变（`.local/debt/catalog-proof.json`）。修正SearchPolicySnapshot和SolverSearchProfile的组合成员来源/关闭行为及硬编码项数注释，保留上一轮历史实测。代码与资源3文件+8/-29；没有改现用模板或测试逻辑。Release 0警告/0错误（4.89秒，`catalog-build.log`），Bash门禁通过 `search_files=192`（`catalog-boundaries.log`）。按安全类口径不跑游戏或离线批次。提交 `80fd763`。

### 批次三：相同克隆复用

PendingCycleExit准入比较器的21行方法体与既有质量比较器原文一致，改为调用该helper；两处16行开局SearchNode构造原文一致，提取private static工厂，参数表达式与求值顺序不变。另一处使用_startTurnNumber的相似构造保留。2个源码文件+23/-53，净减30行；原文和比对断言见 `.local/debt/clone-proof.json`。

Release 0警告/0错误（3.71秒，`clones-build.log`）；Bash门禁通过 `search_files=192`（`clones-boundaries.log`）。单次EQ10全部 `IDENTICAL`：983项比较字段、600项剪枝计数，双侧20份结果有效，时间边界0。证据 `clones-comparison.json`、`clones-validity.json`；本批logs已删，结构化结果保留。同步架构与skill中待准入比较器的职责说明，没有添加helper名称门禁。

### 批次四：保路文件纯移动

移动前8075行，拆为6文件；按原连续成员段迁移，没有改方法体、可见性、调用或集合顺序。源码diff +7260/-7120，净增加140行来自文件头与partial外壳。

| 文件 | 行数 |
|---|---:|
| `CombatBeamSolver.BeamRetentionPolicy.cs` | 1819 |
| `CombatBeamSolver.BeamRetentionPolicy.OrderedMutation.cs` | 1788 |
| `CombatBeamSolver.BeamRetentionPolicy.OrderedMutationScheduling.cs` | 1898 |
| `CombatBeamSolver.BeamRetentionPolicy.Testing.cs` | 1256 |
| `CombatBeamSolver.BeamRetentionPolicy.Routing.cs` | 550 |
| `CombatBeamSolver.BeamRetentionPolicy.Ranking.cs` | 904 |

原段按原位置拼回逐字符相同（`retention-split-proof.json`）；Roslyn核对229项完整成员文本及所有者一致，5项字段声明顺序一致，语法错误0（`retention-member-proof.json`）。`retention-color-moved.diff`留存Git移动着色；短括号/空行受Git匹配阈值影响，完整成员文本证据覆盖这些行。

两套门禁仅迁移既有检查路径/文件清单，原禁止规则覆盖所有新分片，没有新增策略规则或helper名称检查；架构文件表和skill同步。Release 0警告/0错误；Bash门禁 `REFACTOR_BOUNDARIES_OK search_files=197`。本批仅一次EQ10，983项字段与600项剪枝计数一致，双侧20份有效、无时间边界，`IDENTICAL`。日志见 `retention-build.log`、`retention-boundaries.log`，等价证据见 `retention-comparison.json` / `retention-validity.json`；本批宿主logs已删。

## 3. 没动的与原因

不改Engine/Prediction语义、公开/internal签名、设置schema、搜索政策或预算；不动catch语义；不拆Testing可变夹具。CA2000/CA2213的原生和租约所有权必须另作生命周期审计，不能机械Dispose。归一化克隆不等价于同一政策。沿用上一轮研究证据与反射/InlineArray裁决，删除实验目录0、设置迁移0、测试夹具0。

## 4. 值得作者做的真正重构

| 优先级与目标 | 目标职责 | 必须保持的不变量 | 验证 | 工作量估计 |
|---|---|---|---|---|
| P1 BeamRetentionPolicy 的 AddOrderedMutationPortfolio / RankBest | 将单次保留输入、候选提案、共享额度仲裁、最终结算拆成现有所有者内的明确阶段 | 原迭代/短路/稳定同分顺序；未保留者不扣账、不赚进展；每层共享额度与异常排空 | 固定根EQ+FULL、每原因准入账本与剪枝计数、取消/失败排空 | 5–8工程日 |
| P1 Executor.ExecuteAsync | 以现有ProtocolHost/ScenarioBuilder/Executor/Assertions边界建立场景登记与协议契约清单 | 每请求开关恢复、原分派顺序、Passed/Failed/Held与通用fallback；不共享可变fixture | 每类最小场景、非法字段和复用/失败协议合同 | 4–6工程日 |
| P1 Runtime/Api宽泛catch及CA2000集中区 | 按拥有资源/结果承诺逐入口核对失败与释放责任 | 玩家状态保护、失败可观测、未交付资源只释放一次、取消不吞 | 故障注入、dispose/rethrow顺序、协议Failed | 3–5工程日 |
| P2 Expansion选择/回放/准入 | 在本轮文件导航之上明确暂停种子、选择租约、已准入候选的交接合同 | 唯一快照所有权、原选择顺序、RNG、预算计数、异常排空 | 全状态差分、兄弟隔离、固定工作等价、取消路径 | 4–7工程日 |
| P2 SimulatedCombatState.Fork | 为105个跨文件私有字段建立生命周期与复制方式清单，按现有状态域核对 | 同一ForkContext、别名/顺序/注册身份、事务稳定边界 | 完整状态/RNG与兄弟修改差分 | 3–5工程日 |
| P2 SolverOverlay | 按现有snapshot/布局/输入生命周期逐段压缩大方法 | 主线程模型投影、语言切换、控件所有权、事件解绑 | UI合同加作者可见布局与交互验收 | 2–4工程日 |
| P3 门禁与文档 | 补Engine/Prediction反向政策依赖、Replay全目录标准库约束，历史证据与当前导航分开 | 不把合法冻结合同当全局依赖违规；不钉死私有helper名 | 违规样例能被拒绝、合法类型引用通过 | 1–2工程日 |

以上是作者可单独排期的语义/职责任务，不在本轮机械移动中顺手实施。文件数增加不代表完成这些拆分。
