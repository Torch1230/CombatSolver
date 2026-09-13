# CombatSolver 仓库工作指令

> **当前工作重点（2026-09-13）：** PR #89 已合入上游并发布 0.37.0；当前 `perf/hotspot-exploration-20260913` 从上游 `bcc15da` 建立，继续独立可验证的性能探索。先量化热点与原型，低成本且有实测收益的方案可以落地；高成本方案先报告证据，不自行实施。不移植 Compact 后端、不重写战斗流程、不改变搜索预算或决策质量；本批不提升版本、不发包或上传。

> **当前批次验证约束：** 用户于 2026-09-12 明确停止可见测试，本批后续不启动可见 Steam 会话。性能报告限定为实际取得的无头数据，不外推 FPS、可见帧时间或可见性能收益。

本文件约束所有在本仓库中工作的 coding agent。开始处理任务前完整阅读；子目录若有更具体的 `AGENTS.md`，其规则只补充对应目录，不能放宽这里的硬约束。

## 1. 项目边界

CombatSolver 是《杀戮尖塔 2》的单人战斗路线求解器 Mod，使用 C# / .NET 9 / Godot。它在主线程捕获稳定战斗根，在后台分叉影子状态并跨回合搜索，最后通过原版公开入口部署当前回合动作。

硬约束：

- 只支持单人战斗；不要增加多人兼容分支。
- 使用仓库内嵌模拟引擎；不要重新引入 RandomForeseer 运行时依赖。
- 后台搜索不得读取会随实机推进而变化的 live 值，也不得修改真实战斗。分支可变值必须属于根快照、影子状态、克隆 Model 或 `PredictionStateStore`。
- 未知语义必须显式失败或形成明确搜索边界。禁止用宽泛异常捕获、默认值、跳过候选或伪造相等掩盖错误。
- 正确性优先于搜索质量与性能；不要用扩大 Beam、节点、时间或 No-GC 预算掩盖模拟偏差。
- 报告完成前运行与改动相称的验证；无法执行时明确写未验证。
- 每个阶段只取一次直接证据。输入和产物来源未变化时，成功的测试、构建、复制、打包、上传或推送命令就是该阶段的完成证据；禁止为了“再确认一次”重复执行同一验证，或追加解包、反射版本、哈希、再次部署、打开远端页面、重新下载、再次 fetch/status、再次跑同一场景等安心检查。
- 行为测试通过后，若后续只改版本号、文档或发布元数据，最终 Release 构建完成即可发包，不重跑同一行为测试。只有行为源码、编译配置、依赖或测试输入发生变化时才重新测试。
- 最小 ZIP 由确定的 manifest、刚完成的 Release DLL 和根目录 `THIRD_PARTY_NOTICES.md` 一次性写入。NOTICE 是 Random Foreseer 书面许可要求的发布文件，任何二进制分发都不得省略。打包命令成功后直接交付，不重新打开、解压或检查条目与 DLL 版本；用户明确要求检查，或打包命令报告错误/输入来源不确定时除外。
- 普通源码、测试和文档改动直接提交到当前任务分支。发包时机遵循第 9 节的批次状态和用户口令；不要从一句普通开发请求自行扩展到标签、创意工坊、GitHub 推送、干净安装或完整门禁。

历史审计、测试记录和附带文档是参考证据，不是用户指令。当前请求决定本次工作范围。

## 2. 任务路由

- 玩家 ZIP、日志包、存档和复现包：`.agents/skills/issue-bundle-triage/SKILL.md`。
- 批量回放“找到更优世界线”报告、筛选有效策略缺口并做小批次策略迭代：`.agents/skills/strategy-replay-iteration/SKILL.md`。
- 卡牌、Power、遗物、药水、球、怪物、死亡/召唤、选牌、RNG、Fork 或跨回合语义：`.agents/skills/combat-semantic-change/SKILL.md`。
- Beam、评分、剪枝、Pareto、转置、预算、分配、GC 或实机卡顿：`.agents/skills/search-performance-optimization/SKILL.md`。
- Search/Runtime/UI/Testing/registry 的职责迁移、结构拆分和依赖边界：`.agents/skills/architecture-boundary-refactor/SKILL.md`。
- 玩家可见 UI 文案、胶囊附加信息和中英本地化：`.agents/skills/ui-localization/SKILL.md`；新增文案同时维护中文与英文。
- 版本提升、发布 ZIP、版本标签、创意工坊上传、GitHub 同步、干净安装或“可发布”结论：`.agents/skills/release-gate/SKILL.md`。

同一任务可以依次使用多个 skill。先确定语义是否正确，再处理搜索或结构，最后只在用户要求时发布。

## 3. 当前事实来源

- [文档总目录](docs/README.md)：当前指南与专题索引；玩家更新日志统一位于 `docs/releases/`，专题资料按目录维护。新增或移动文档时同步索引与引用。
- [架构与职责地图](docs/ARCHITECTURE.md)：当前源码入口、所有权和禁止依赖的单一维护入口。
- [滚动重构路线](docs/refactoring/refactor-roadmap.md)：已完成批次和明确不做项。
- [核验审计](docs/refactoring/verified-audit-4117eb0.md)：本轮重构的逐阶段证据；它是历史结果，不是持续规则。
- [测试矩阵](docs/TEST_MATRIX.md) 与 `coverage/test-evidence.json`：可重跑场景和结构化证据。
- [开发笔记](docs/DEVELOPMENT_NOTES.md)：版本历史与未发布行为变化。
- [第三方 Mod 适配手册](docs/THIRD_PARTY_ADAPTERS.md)：面向外部 Mod 作者的登记点总表、登记纪律与验收标准；同时是「哪些位置还是封闭开关」的单一维护入口。
- `tools/verify-refactor-boundaries.ps1`（Windows / PowerShell 7）与 `tools/verify-refactor-boundaries.sh`（Linux / Bash）：当前架构边界的等价可执行门禁。

源码与当前可重跑结果优先于历史说明。职责发生变化时，同一提交更新 `docs/ARCHITECTURE.md`、相关 skill 和结构门禁，避免多份地图继续漂移。

## 4. 核心职责摘要

完整地图见 `docs/ARCHITECTURE.md`。以下边界不可混写：

### 4.1 Runtime 与搜索根

- `src/Runtime/Entry.cs`：Mod 初始化和战斗生命周期入口。
- `src/Runtime/SolverController.cs`：主线程编排；创建搜索请求、处理结果、续用、部署和全自动。
- `src/Runtime/SolverControllerSessions.cs`：`SolverCombatSession`、`SolverSearchSession`、`SolverDeploymentSession` 的生命周期所有权。
- `src/Runtime/CombatRootSnapshot.cs`：只能在主线程捕获并验证 live 状态稳定；后台搜索只接收该根。
- `src/Runtime/ContinuationStamp.cs`：跨回合 live/predicted 一致性和字段级差异。
- `src/Runtime/SearchGcPolicy.cs`：进程级 GC / No-GC 生命周期、搜索内后台回收续搜和跨战斗回收协调，不属于 Search 算法。
- `src/Runtime/SearchMemoryPressureSignal.cs`：Runtime 注入 Search 的分配边界与回收续搜入口；Search 不直接读取设置或操作 GC 模式。
- `src/Runtime/PlayerTurnSetupPatches.cs`：首回合选牌后搜索、全自动后续回合的计划重放，以及单步执行在下一回合原生选牌页交还玩家并允许执行/全自动入口接管既有选择。
- `src/Runtime/NativeChoiceRuntime.cs`：原生选牌页面观察与计划卡牌逐实例匹配；不枚举搜索分支。
- `src/Runtime/BaseLibCloneConcurrencyPatch.cs`：BaseLib 克隆扩展已加载时，串行保护原版 `MutableClone` 的第三方扩展段；预测克隆只允许 `NativeModelCloneConcurrency` 核对过的隔离域普通原版卡牌及默认内部初始化 Power 旁路；不得扩大成整段搜索串行化。
- `src/Runtime/PowerDynamicVarWarmup.cs`：主线程捕获根状态时物化规范 Power 与当前战斗 Power 的显示变量，禁止把惰性本地化工作带入 worker。
- `src/Runtime/PowerDynamicVarMaterializationGuardPatch.cs`：搜索模拟期间禁止惰性创建 Power 显示变量；命中表示根捕获缺少必要实例的物化。

### 4.2 Search

- `CombatSearchCoordinator`：主搜索、无药和强制用药反事实审计。
- `CombatBeamSolver.cs`：构造参数、不可变根配置及各策略对象接线，不承载 `Solve` 循环。
- `CombatBeamSolver.Models.cs`：节点、快照、`SearchFeatures` 和单次运行的 `SearchRunContext`。
- `CombatBeamSolver.Phases.cs`：`Solve` 与阶段推进。
- `CombatBeamSolver.Expansion.cs`：候选展开与动作回放入口。
- `CombatBeamSolver.ParallelExpansion.cs`：固定 worker lane、动作准备/原始候选物化与确定性串行提交。
- `CombatBeamSolver.AdmittedExpansion.cs`：已准入父节点的动作、选择链、药水与回合尾部作业；有界派发、唯一快照所有权和在途排空。
- `CombatBeamSolver.PrimaryChoiceReplay.cs`：保证原预算必经的首层选择回放、快照暂存与原序消费；不并发消费动态选择预算。
- `CombatBeamSolver.Retention.cs`：剪枝调用边界；具体中间保路属于 `BeamRetentionPolicy`。
- `CombatBeamSolver.BeamRetentionPolicy.cs`：状态去重、Beam 排名、多样性通道、动作/回合开始选牌保路、药水配额和小型 Pareto。
- `CombatBeamSolver.FinalPlanOrdering.cs`：终局胜负、战损、药水、偷窃、卖血和边界排序。
- `CombatBeamSolver.StateEvaluation.cs`：快照、威胁与评分特征。
- `CombatBeamSolver.Terminal.cs`：终局回放、回合结果和路线标注。
- `SimulatedCombatState*.cs`：搜索面对的分支战斗领域状态；不得把候选政策塞进这里。

### 4.3 模拟、Mirror 与领域补偿

- `src/Engine/InCombat/Simulation/*`：通用命令时序、历史、RNG、牌堆、伤害和 Fork。
- `src/Engine/InCombat/Mirrors/*`：原版 Hook / Model 方法的精确镜像。
- `src/Engine/Common/Mirrors/MethodMirrorRegistryDescriptor.cs`：registry 对 CoverageCatalog 提供支持元数据的唯一接口。CoverageCatalog 不反射 registry 私有字段。
- `src/Prediction/*`：跨 Hook 生命周期、怪物 AI/行动、隐藏状态、死亡/召唤、选择和第三方 subscriber 捕获等领域补偿。

### 4.4 UI 与测试

- `SolverOverlaySnapshot.Capture` 是搜索结果到 UI 的转换边界；它可以读取 `SolverResult` 和显示元数据。
- `SolverOverlay`、`SolverRouteRow`、`SolverActionPill` 只渲染只读 snapshot，不读取 `SolverResult`、`PlanAction`、`PlanCardChoice` 或 `ModelDb`。
- `UnattendedTestRunner` 负责请求级编排和共享 fixture helper。
- `ProtocolHost` 独占请求循环与每请求开关；`ScenarioBuilder` 独占建局和状态注入；`Executor` 独占差分/搜索/部署执行和临时设置；`Assertions` 独占执行前后断言；`Writer` 独占结果协议与原子写入。

## 5. 状态所有权

真实 `Player`、`Creature`、`CardModel`、`PowerModel`、`RelicModel`、`MonsterModel` 可以作为稳定身份、类型或只读模型元数据。以下值一旦会随分支变化，就必须从根或分支状态读取：

- HP、格挡、能量、星能、金币和最大生命；
- 有序牌堆、卡牌费用、升级、附魔、临时标志和动态变量；
- Power 数量、内部计数、生命周期与 applier/target；
- 怪物下一行动、状态机日志、私有 AI 和行动静态参数；
- 遗物、药水槽、球和内部触发计数；
- 召唤、死亡、复活、逃跑后的阵容；
- 九条战斗 RNG 的状态与计数。

新增分支状态时必须明确：

1. 主线程根从哪里、何时读取；
2. 状态属于基础影子、`SimulatedCombatState`、克隆 Model 还是 `PredictionStateStore`；
3. Fork 采用深拷贝、COW 或不可变共享；
4. 对象引用如何通过同一个 `PredictionForkContext` 重映射；
5. 是否影响未来合法动作或结算，进而进入状态键；
6. 是否跨回合存活，进而进入 `ContinuationStamp`；
7. actual/simulated 严格差分如何捕获；
8. 创建、叠加、移除和清空时点；Fork 是否要求事务为空。

状态指纹是搜索等价性机制，不是文件完整性校验。不要把纯 UI、日志或派生启发式值加入战斗状态键。

## 6. 当前不变量

- 一次 Fork 的所有子结构共享同一个 `PredictionForkContext`；分支可变引用优先 `RequireRemap`。
- `PredictedCard.Preview` 可能仍指向根实例；写入必须先取得 `MutablePreview`，不得写 `Original`。
- Fork 前所有动作、选牌、Power、死亡和卡牌执行事务必须处于允许复制的稳定边界。
- 怪物离开活动 roster 不等于其根 AI/静态参数立刻失效。正在执行的行动尾部仍可能读取这些数据；已知怪物状态跟随分支生命周期保留。
- gameplay mod subscriber 在主线程分段捕获。已适配来源消费根/分支状态；未知 gameplay subscriber 显式拒绝，不浅拷贝 live 所有权。
- Search 不得引用 `SolverSettings.Current`、`Entry.Logger`、`SolverController`、UI 或无人测试 runner；通过 `SearchPolicySnapshot`、`SearchDiagnosticsSink` 和 `SearchFramePressureSignal` 注入。
- 同一战斗语义只能有一个权威实现。新增效果前沿完整调用链检查 mirror、spec、support 与 `SimulatedCombatState`，避免双结算。

## 7. 错误处理

- 不新增 `catch (Exception)` 后继续、返回默认值或跳过候选。
- 只捕获明确的取消和已定义业务无效分支；保留动作、事务和状态上下文。
- 推断式 mirror 构建失败可以归类为未支持；执行中失败必须中止当前搜索，不得部分提交后吞异常。
- 运行时为了保护玩家状态而拦截异常时，应停止搜索/部署、清理会话、输出稳定失败事件并让无人测试得到 Failed。

## 8. 测试选择

默认使用快速迭代层，不把完整战斗当作每个修复的固定尾声：

- **L0 静态/构建**：文档、skill、纯结构和明确的编译错误。检查链接、路径、frontmatter、结构门禁或 Release 编译，不启动游戏。
- **L1 最小语义**：普通战斗语义修复的默认层。只跑能覆盖首个错误状态的单效果 actual/simulated 严格差分；同根批量问题只选一个代表，不逐包复跑。
- **L2 最小边界**：新增 Fork 状态、跨回合历史、续用或部署边界时，构造两回合生命周期或在最早预期复用回合停止。只有 fixture 实际启动搜索时才加 `-VerifyIncrementalSearch` / `--verify-incremental-search`；纯一步差分不带该开关。
- **L3 完整场景**：只在改动搜索保路/排序/部署编排、较小 fixture 无法覆盖根因、用户明确要求完整回归/完整门禁，或准备作整场质量结论时运行。普通 Mirror、Power、牌堆和历史修复不自动升级到完整自动战斗。

快速迭代约束：

- 单个 unattended 请求的总超时默认不超过 `120` 秒；搜索型 fixture 使用固定短搜预算，优先在首个结果、首个目标动作或最早复用回合停止。
- 快速场景达到超时，说明当前 fixture 不适合内环。记录未验证，缩小建局、直接注入首个错误边界或改成差分；同一轮不得把超时从 `120` 秒继续放大到 `180/360` 秒等待。
- 同一行为源码、输入和测试层已经通过时不重复。源码变化只重跑会被该变化影响的最小 fixture，不让所有已通过场景连带重跑。
- 批量日志按共享根因去重。每个根因一条失败基线和一条最终证据足够；重复包和同根遭遇不增加测试数量。

具体选择：

- 文档/skill：L0。
- 纯职责移动：Release 编译、对应结构门禁，再跑一个穿过该边界的代表场景。
- 战斗语义：L1；确实新增跨回合/Fork/续用状态时升到 L2。
- Beam/排序：目标短搜基准加一个不可退化哨兵；最终候选才跑增量等价或必要的完整自动部署，不在每轮参数尝试后跑整场。
- Mirror/覆盖元数据：相关差分与 CoverageCatalog 对应 verify；只有改变覆盖面或明确完整门禁时跑全量 verify。
- UI/动画/输入：headless 结构事件；只有需要证明真实可见效果时启动 Steam。
- 性能最终结论来自正常可见 Steam 会话；headless 只用于快速 A/B，不运行增量验证。

需要完整部署时固定 `Instant / 0 秒` 并断言计划外重算数量。一个 headless 进程复用同一批最小请求；重新编译后退出仍加载旧 DLL 的进程。

Windows（PowerShell 7）常用命令：

```powershell
dotnet build CombatSolver.csproj -c Release
pwsh -NoProfile -File tools\verify-refactor-boundaries.ps1
pwsh -NoProfile -File tools\run-unattended-test.ps1 <fixture 参数>
dotnet run --project tools\CoverageCatalog\CoverageCatalog.csproj -c Release -- . <verify 参数>
pwsh -NoProfile -File tools\run-visible-steam-benchmark.ps1 <固定基准参数>
```

Linux（Bash）等价命令：

```bash
dotnet build CombatSolver.csproj -c Release
./tools/verify-refactor-boundaries.sh
./tools/run-unattended-test.sh <fixture 参数>
dotnet run --project tools/CoverageCatalog/CoverageCatalog.csproj -c Release -- . <verify 参数>
./tools/run-visible-steam-benchmark.sh <固定基准参数>
```

Windows `.ps1` 与 Linux `.sh` 都是受维护的平台原生入口：PowerShell 使用 PascalCase 参数，Bash 使用 GNU 风格长参数；`.sh` 不调用 PowerShell。两端无人测试脚本均允许覆盖游戏和依赖路径，Linux 脚本还会探测标准 Steam 安装；这些本地入口仍不是可移植 CI。修改协议、门禁或测试能力时同步维护两端脚本，不要提交个人绝对路径更新。

## 9. 文档、提交与发布

- 改动职责边界：更新 `docs/ARCHITECTURE.md`、相关 skill、结构门禁及必要的重构路线/核验记录。
- 改动语义、搜索、性能、UI 或测试方式：更新 `docs/DEVELOPMENT_NOTES.md` 与 `docs/TEST_MATRIX.md`；需要进入覆盖目录时同步结构化证据。
- 改动任何第三方登记点：在同一提交更新 `docs/THIRD_PARTY_ADAPTERS.md`。登记点指外部 Mod 能写入的入口——镜像注册表、`StrategicEffectMirrors` 这类按类型登记的表、订阅者门禁，以及手册第 6 节列出的封闭开关。新增登记入口要写进第 2 节并从第 6 节移除对应行；改动既有入口的签名、语义或登记时机要更新对应章节；发现新的封闭开关要补进第 6 节。登记点有专属子文档时（例如 `docs/third-party-strategic-effects.md`）一并更新，手册只保留概述和链接。
- 功能修复顺带暴露出手册没讲清的行为时，把它补进手册，不要只写进开发笔记——手册是外部作者唯一会读的那份。
- 面向玩家的更新日志和开发文档使用当前支持游戏版本的官方中文译名；名称从游戏内本地化或实机路线日志核对，不沿用玩家口语、旧译名或自行翻译。原始问题摘录保持用户原文，并明确标记为原始描述。
- 用户声明“这一批不发版”“直到我说发版都记入 `X`”或等价要求时，建立活动发布批次。批次内每项改动均写入 `X（开发中）` 并正常提交，不逐项提升版本、构建、打包、创建标签或上传；直到用户明确结束批次。该批次声明优先于“修复后默认最小发包”。
- 版本创建标签或成功上传创意工坊后即冻结。后续行为改动进入新的“下一版本（开发中）”记录，不追加到已发布版本的更新日志或开发章节；用户尚未指定新版本号时不擅自编造，等下次版本指令再统一命名。
- 没有活动发布批次时，玩家问题包修复和用户提出的功能修改默认以补丁版本、提交、一次 Release 构建和一次最小 ZIP 定版；用户明确说不发包时停止在提交。
- 发布口令按字面分层执行：`准备发版` 完成版本同步、玩家更新日志、提交、一次 Release 构建和一次最小 ZIP，不创建标签、不上传、不推送；带有“给我审核/我拍板后”的请求只整理并提交更新日志草案，等用户批准后再构建定版。`发版/发布` 在必要时补齐准备步骤并创建当前版本的 annotated tag，不自动上传创意工坊或推送 GitHub。`上传/更新创意工坊` 只发布当前已定版版本；`推送/同步远端` 只提交明确属于当前任务的跟踪文件并推送当前分支及已存在的当前版本标签。只有用户明确要求“完整发布门禁/完整验收/干净安装”才执行完整门禁。
- 最小发包链固定为：完成必要行为验证、提交、一次 Release 构建、一次最小 ZIP 创建。后续没有行为源码或构建输入变化时，到 ZIP 创建成功即结束，不追加发布后复测或包内容复核；前一阶段已有成功证据时直接复用，不重做。
- Release ZIP 统一写入仓库根目录的 `releases/`，命名为 `CombatSolver-<版本号>.zip`。当前工作区对应 `D:\Desktop\sts2mod\CombatSolver\releases`；目录不存在时先创建，交付链接也指向该目录。
- 用户明确要求“上传/更新创意工坊”时，直接上传仓库当前已经定版的最新版，并在创意工坊 `changeNote` 中附本次面向玩家的更新说明。创意工坊暂存目录中的旧 DLL、manifest 或旧 `changeNote` 不是最新版来源；存在尚未定版的当前改动时，只补齐缺失的最小发包阶段。上传成功后不打开页面或重新下载确认。
- 创意工坊更新说明与 `docs/DEVELOPMENT_NOTES.md` 的开发记录用途不同。开发记录用于保留根因、内部职责、测试证据和性能数据；更新说明只提炼玩家在游戏中能感知的新增、优化、修复、UI/操作、兼容性与必要限制。禁止写类名、方法名、算法内部、内存/GC 实现、runId、提交、构建、测试和打包细节，也不要直接复制开发记录。跨多个版本更新时合并同类玩家改动，不逐版堆技术流水账。
- 普通开发完成后直接提交当前任务改动；“干净提交”表示显式暂存本任务文件、保留用户其他改动并排除构建产物、发布包和暂存内容，不表示清空工作区。没有新改动但已有本地提交领先远端时，直接推送，不创建空提交。
- 版本号、manifest、发布 ZIP 和创意工坊上传使用 `release-gate`；干净安装和 Steam 发布验收只在用户明确要求完整发布门禁时执行。创意工坊上传成功本身不触发重新打开页面、下载订阅内容或重复核对远端版本。
- 发布包、完整日志、问题包、Profiler、`.local/`、`bin/`、`obj/` 和 `.godot/` 不进入源码提交。

## 10. 已知外部边界

- 问题包通过 `CheckpointArchivePath` 或 `run-checkpoint-batch` 导入，`ReplayMode=RestoreOnly` 默认只验证检查点；`Preflight` 只验证材料，不能视作恢复成功。新包从原生战前存档和事件恢复，完整 ContinuationStamp 与 native-state 分别对账；旧包支持开战前注入及检查点恢复，缺失历史与政策明确报告。批量工具口径与限制见 `docs/CHECKPOINT_REPLAY.md`。
- Overlay 的人工布局、字体、拖动和真实动画需要可见游戏验证；headless 只证明结构化状态与部署事件。
- No-GC 和卡顿受完整 Mod 栈及渲染分配影响；headless 数据不能替代可见 Steam 性能口径。
- `.local/decompiled/sts2-v0.111.0/` 是当前游戏版本的只读原版源码参考。只有调查原版语义时定向读取，游戏版本变化后重新建立对应版本目录；不要在普通仓库扫描中载入它。

## 11. 完成汇报

汇报功能层面的变化、修改所在职责层、实际执行的验证和未执行项。禁止把静态阅读或编译写成语义修复，把旧测试记录写成本轮通过，或只比较聚合 HP 就声称状态等价。
