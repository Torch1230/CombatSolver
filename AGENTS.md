# CombatSolver 仓库工作指令

本文件约束所有在本仓库中工作的 coding agent。开始处理任务前完整阅读；子目录若有更具体的 `AGENTS.md`，其规则只补充对应目录，不能放宽这里的硬约束。

## 1. 项目边界

CombatSolver 是《杀戮尖塔 2》的战斗路线求解器。本分支 `feat/multiplayer` 面向 2～4 人的本地动作搜索与当前回合部署，单人继续保持整场搜索和严格续用。分支状态与实际验收范围由 [多人状态](docs/MULTIPLAYER_PLAN.md) 维护。

硬约束：

- 多人开发按 [多人适配实施规划](docs/MULTIPLAYER_PLAN.md) 的 2～4 人、本地动作搜索、有限回合及完整原版内容目标推进，由同一开发者完成全部建模与脚手架。现有单人行为保持；正式多人入口在对应实施与验收完成后开放。
- 使用仓库内嵌模拟引擎；不要重新引入 RandomForeseer 运行时依赖。
- 后台搜索不得读取会随实机推进而变化的 live 值，也不得修改真实战斗。分支可变值必须属于根快照、影子状态、克隆 Model 或 `PredictionStateStore`。
- 未知语义必须显式失败或形成明确搜索边界。禁止用宽泛异常捕获、默认值、跳过候选或伪造相等掩盖错误。
- 正确性优先于搜索质量与性能；不要用扩大 Beam、节点、时间或 No-GC 预算掩盖模拟偏差。
- 报告完成前运行与改动相称的验证；无法执行时明确写未验证。
- 每个阶段只取一次直接证据。输入和产物来源未变化时，成功的测试、构建、复制、打包、上传或推送命令就是该阶段的完成证据；禁止为了“再确认一次”重复执行同一验证，或追加解包、反射版本、哈希、再次部署、打开远端页面、重新下载、再次 fetch/status、再次跑同一场景等安心检查。
- 行为测试通过后，若后续只改版本号、文档或发布元数据，最终 Release 构建完成即可发包，不重跑同一行为测试。只有行为源码、编译配置、依赖或测试输入发生变化时才重新测试。
- 最小 ZIP 由确定的 manifest、刚完成的 Release DLL、根目录 `LICENSE` 和 `THIRD_PARTY_NOTICES.md` 一次性写入。两份许可文件必须随每个二进制发布包提供。打包命令成功后直接交付，不重新打开、解压或检查条目与 DLL 版本；用户明确要求检查，或打包命令报告错误/输入来源不确定时除外。
- 普通源码、测试和文档改动直接提交到当前任务分支。发包时机遵循第 9 节的批次状态和用户口令；不要从一句普通开发请求自行扩展到标签、创意工坊、GitHub 推送、干净安装或完整门禁。
- 每次完成本仓库任务前（包括审计并合并远端 PR），把与最终源码一致的本地 Mod 部署到已确认的游戏 `mods/CombatSolver` 目录。成功的同源码构建可复用；否则先构建。精确覆盖 manifest、CombatSolver DLL、Windows MemoryCleaner、LICENSE 和 THIRD_PARTY_NOTICES.md，不清空目录或改动其他 Mod。用户明确要求不部署时跳过；构建或复制失败时报告未部署。本地部署不触发版本提升、ZIP、标签、渠道上传、游戏启动或完整发布门禁。

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
- [滚动重构路线](docs/refactoring/refactor-roadmap.md)：当前状态、待证据项与历史入口。
- [测试矩阵](docs/TEST_MATRIX.md) 与 `coverage/test-evidence.json`：可重跑场景和结构化证据。
- [开发笔记](docs/DEVELOPMENT_NOTES.md)：当前未发布行为变化与历史入口。
- [第三方 Mod 适配手册](docs/third-party/README.md)：面向外部 Mod 作者的登记点总表、登记纪律与验收标准；同时是「哪些位置还是封闭开关」的单一维护入口。
- `tools/verify-refactor-boundaries.ps1`（Windows / PowerShell 7）与 `tools/verify-refactor-boundaries.sh`（Linux / Bash）：当前架构边界的等价可执行门禁。
- `tools/OfflineSearchHarness/`：不启动 Godot、在普通 .NET 进程里批量跑搜索的离线宿主，用法与口径见 [离线搜索宿主](docs/OFFLINE_SEARCH_HARNESS.md)。只产指标，不做正确性验收。

源码与当前可重跑结果优先于历史说明。职责发生变化时，同一提交更新 `docs/ARCHITECTURE.md`、相关 skill 和结构门禁，避免多份地图继续漂移。

## 4. 职责边界

当前源码入口和所有权由 [架构地图](docs/ARCHITECTURE.md) 统一维护。Runtime 捕获根与编排部署，Search 决定候选政策，模拟引擎执行通用语义，Prediction 持有领域补偿，UI 消费只读 snapshot，Testing 分层持有协议、建局、执行、断言和输出。

职责迁移在同一提交更新架构地图、相关 skill 和结构门禁；规则文件只保留长期边界，不复制整份源码目录。

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

Coding agent 启动无头游戏测试时，PowerShell 必须使用 `-CleanupInstanceOnExit`，Bash 必须使用 `--cleanup-instance-on-exit`。实例默认且必须位于当前仓库 `.local/headless-instances/<实例>`；不得把游戏/Mod 快照放进 `%LOCALAPPDATA%/CombatSolver/headless-instances` 或其他用户目录。只有用户显式指定 `COMBATSOLVER_HEADLESS_ROOT` 时才可改用另一个精确实例目录。同一批次确需复用实例时，只能在批次内部保留，最后一项必须带清理开关并确认启动器成功删除整个实例目录；`ExitOnComplete` 只退出进程，不满足目录清理要求。明确为人工性能分析保留现场时例外，但必须在测试证据中记录实例路径和后续清理责任。

Windows（PowerShell 7）常用命令：

```powershell
dotnet build CombatSolver.csproj -c Release
pwsh -NoProfile -File tools\verify-refactor-boundaries.ps1
pwsh -NoProfile -File tools\run-unattended-test.ps1 <fixture 参数> -CleanupInstanceOnExit
dotnet run --project tools\CoverageCatalog\CoverageCatalog.csproj -c Release -- . <verify 参数>
pwsh -NoProfile -File tools\run-visible-steam-benchmark.ps1 <固定基准参数>
```

Linux（Bash）等价命令：

```bash
dotnet build CombatSolver.csproj -c Release
./tools/verify-refactor-boundaries.sh
./tools/run-unattended-test.sh <fixture 参数> --cleanup-instance-on-exit
dotnet run --project tools/CoverageCatalog/CoverageCatalog.csproj -c Release -- . <verify 参数>
./tools/run-visible-steam-benchmark.sh <固定基准参数>
```

Windows `.ps1` 与 Linux `.sh` 都是受维护的平台原生入口：PowerShell 使用 PascalCase 参数，Bash 使用 GNU 风格长参数；`.sh` 不调用 PowerShell。两端无人测试脚本均允许覆盖游戏和依赖路径，Linux 脚本还会探测标准 Steam 安装；这些本地入口仍不是可移植 CI。修改协议、门禁或测试能力时同步维护两端脚本，不要提交个人绝对路径更新。

## 9. 文档、提交与发布

### 文档维护规则

- 每份文档只有一个职责：规则、现行指南、当前进度、测试证据、玩家日志或历史资料。内容写入对应入口与章节；新增文件前先确认已有入口是否足够。
- AGENTS.md 与 skill 只保存长期约束和任务路由。批次授权、交接、临时禁令、当前分支进度、runId、性能数字与测试流水账留在对话或所属证据中，不在规则开头前插提示块。
- 修改现行行为时直接替换过时说明，合并同主题内容。已发布历史、原始玩家意见、失败证据和未验证项完整保留；归档不代表问题修复，也不代表本轮复测。
- 普通指南达到 500 行或 64 KiB 时按稳定职责拆分；活动开发/测试记录达到 200 行或 32 KiB 时，将完成批次归档并保留短入口。发布定稿后立即滚动归档，不把新工作追加到已发布章节。确需保留的卡池清单、协议 schema、结构化证据及生成报告按数据完整性维护，入口说明其范围与来源。
- 同一专题有多份文档时集中到专题目录，以 README.md 为入口；子文档不散落在 docs 根目录。索引只列当前入口和归档入口，不逐条堆报告结论、复制正文或在表格内插入列表。仓库根目录保持必需文件。
- 历史资料放在 docs/archive/，按专题、版本或批次分卷并冻结。报告与 JSON、fixture 等配套材料一起移动；修正链接或标明历史状态可以更新档案正文，新工作另写当前记录。
- 移动或拆分文档时，同步 Markdown 相对链接、章节锚点、skill、脚本和结构化证据中的路径。固定 commit/tag 链接仍指向当时版本。提交前运行 tools/verify-documentation.py，并处理当前文档长度与链接错误。
- 版本、依赖、部署和渠道信息来自 manifest、权威源码与实际操作结果。生成报告由工具重新生成；工具失败时明确保留历史结果与失败原因，不手改版本冒充新验证。业务源码、文档源码与已发布客户端版本分别记录来源。


- 改动职责边界：更新 `docs/ARCHITECTURE.md`、相关 skill、结构门禁及必要的重构路线/核验记录。
- 改动语义、搜索、性能、UI 或测试方式：更新 `docs/DEVELOPMENT_NOTES.md` 与 `docs/TEST_MATRIX.md`；需要进入覆盖目录时同步结构化证据。
- 改动任何第三方登记点：在同一提交更新 `docs/third-party/README.md`。登记点指外部 Mod 能写入的入口——镜像注册表、`StrategicEffectMirrors` 这类按类型登记的表、订阅者门禁，以及手册第 6 节列出的封闭开关。新增登记入口要写进第 2 节并从第 6 节移除对应行；改动既有入口的签名、语义或登记时机要更新对应章节；发现新的封闭开关要补进第 6 节。登记点有专属子文档时（例如 `docs/third-party/strategic-effects.md`）一并更新，手册只保留概述和链接。
- 功能修复顺带暴露出手册没讲清的行为时，把它补进手册，不要只写进开发笔记——手册是外部作者唯一会读的那份。
- 面向玩家的更新日志和开发文档使用当前支持游戏版本的官方中文译名；名称从游戏内本地化或实机路线日志核对，不沿用玩家口语、旧译名或自行翻译。原始问题摘录保持用户原文，并明确标记为原始描述。
- 版本术语按本项目约定：`小版本`（即补丁版本）只将末位加一，即 `0.a.b → 0.a.(b+1)`；`大版本`将中间位加一、末位归零，即 `0.a.b → 0.(a+1).0`。不要按通用语义化版本术语改写这两个含义。已发布版本不追溯改号。
- 用户声明“这一批不发版”“直到我说发版都记入 `X`”或等价要求时，建立活动发布批次。批次内每项改动均写入 `X（开发中）` 并正常提交，不逐项提升版本、构建、打包、创建标签或上传；直到用户明确结束批次。该批次声明优先于“修复后默认最小发包”。
- 版本创建标签或成功上传创意工坊后即冻结。后续行为改动进入新的“下一版本（开发中）”记录，不追加到已发布版本的更新日志或开发章节；用户尚未指定新版本号时不擅自编造，等下次版本指令再统一命名。
- 没有活动发布批次时，玩家问题包修复和用户提出的功能修改默认以小版本（末位加一）、提交、一次 Release 构建和一次最小 ZIP 定版；用户明确说不发包时停止在提交。
- 发布口令按字面分层执行：`准备发版` 完成版本同步、玩家更新日志、提交、一次 Release 构建和一次最小 ZIP，不创建标签、不上传、不推送；带有“给我审核/我拍板后”的请求只整理并提交更新日志草案，等用户批准后再构建定版。`发版/发布` 在必要时补齐准备步骤、创建当前版本的 annotated tag，再用 `tools/publish-release.ps1` 同步发布创意工坊、GitHub Release 与夸克网盘。`上传/更新创意工坊` 只发布当前已定版版本；`推送/同步远端` 只提交明确属于当前任务的跟踪文件并推送当前分支及已存在的当前版本标签。监控后台最新版提示由用户维护，发布流程不读写。只有用户明确要求“完整发布门禁/完整验收/干净安装”才执行完整门禁。
- 最小发包链固定为：完成必要行为验证、提交、一次 Release 构建、一次最小 ZIP 创建。后续没有行为源码或构建输入变化时，到 ZIP 创建成功即结束，不追加发布后复测或包内容复核；前一阶段已有成功证据时直接复用，不重做。
- GitHub Release 的最小 ZIP 统一写入仓库根目录的 `releases/CombatSolver-<版本号>.zip`。夸克网盘由统一脚本另建 `releases/CombatSolver-<版本号>-Quark.zip`：只保留 CombatSolver 最小包内容，不加入 RitsuLib 或其他依赖；不足时增加无压缩的 `QUARK_UPLOAD_PADDING.bin`，使总大小严格超过 15 MiB。填充条目不参与 Mod 加载，解压后可以删除。当前工作区发布目录为 `D:\Desktop\sts2mod\CombatSolver\releases`，所有发布产物均由 Git 忽略。
- 用户明确要求“上传/更新创意工坊”时，直接上传仓库当前已经定版的最新版，并在创意工坊 `changeNote` 中使用同版本玩家更新日志的完整中英正文，只转换 Steam 排版语法；逐项核对，不能缩写、合并或删减玩家变动、限制与致谢。创意工坊暂存目录中的旧 DLL、manifest 或旧 `changeNote` 不是最新版来源；存在尚未定版的当前改动时，只补齐缺失的最小发包阶段。上传成功后不打开页面或重新下载确认。
- 创意工坊更新说明与 `docs/DEVELOPMENT_NOTES.md` 的开发记录用途不同。开发记录用于保留根因、内部职责、测试证据和性能数据；玩家更新日志从中选取玩家在游戏中能感知的新增、优化、修复、UI/操作、兼容性与必要限制，一旦定稿，各发布渠道均完整使用该正文。禁止写类名、方法名、算法内部、内存/GC 实现、runId、提交、构建、测试和打包细节，也不要直接复制开发记录。跨多个版本更新时先合并同类玩家改动写成完整版本日志，不逐版堆技术流水账。
- 玩家更新日志以最近已发布版本为对比基线，每项只写玩家升级后能感知的最终变化。同一未发布批次的界面试稿、样式微调、测试发现的问题和修复过程留在开发记录；新增功能在发布前修好的问题归入该功能，不单列为玩家经历过的缺陷。按功能合并条目，用游戏中的场景、操作和结果表达，省去手牌索引、合并阈值、像素与容器等实现细节。定稿逐项检查：旧版玩家是否遇到过这个问题、是否需要了解这项变化、是否与其他条目重复；用户已删去的内容保持删除。具体撰写规则见 release-gate skill。
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
