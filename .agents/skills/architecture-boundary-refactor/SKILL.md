---
name: architecture-boundary-refactor
description: 重构 CombatSolver 的 Search、Runtime 会话、UI snapshot、无人测试编排或 mirror registry 职责边界时使用；目标是迁移所有权和依赖，同时保持战斗语义、路线政策与协议行为不变。
---

# CombatSolver 架构边界重构

## 适用边界

本 skill 处理结构和所有权：拆分大类、迁移 run/session state、建立策略对象、隔离 renderer、整理测试编排、为工具提供稳定元数据。

如果任务改变卡牌/怪物结算，叠加 `combat-semantic-change`；改变 Beam 权重或候选政策，叠加 `search-performance-optimization`。纯重构不能借机改变这些行为。

开始前读取 `docs/ARCHITECTURE.md`，并读取 `tools/inspection/verify-refactor-boundaries.ps1` 与 `tools/inspection/verify-refactor-boundaries.sh` 中对应边界，只读取本次涉及的源码分片。两套门禁分别服务 Windows 和 Linux，规则必须保持等价。

## 1. 先定义迁移前后的所有权

搜索预算仅由 `SearchPolicySnapshot.Profile` 表达；固定预算测试/API 使用 `FixedBudget`。旧 short/deep 字段只允许在设置、归档或请求反序列化边界迁移，禁止重新引入 Search 阶段或分段计数。UI 设置与请求总计必须消费单一配置；两端结构门禁同步维护。

写清楚：

- 当前谁创建、持有、修改和销毁该状态；
- 目标类型的单一职责；
- 哪些调用顺序、集合顺序、比较器、日志事件和协议字段必须保持；
- 目标边界允许依赖什么，禁止依赖什么；
- 哪个代表场景能穿过这条边界。

优先迁移所有权，再考虑复用、池化或算法优化。不要在同一批同时移动代码、改变策略和优化分配。

## 2. 当前结构约束

- 预战 worker 身份由 `Entry.IsPreCombatWorker` 在启动时冻结；Dispatcher 保留完成邮箱和无人协议，仅跳过玩家战斗 UI 监控。API 可用性与启动期 Mod 钉住共用该身份，避免嵌套 worker。

- 遗物策略的可控范围由 RelicCounterCatalog 声明，Runtime 冻结本场目标，Search 只读已有分支计数并输出标量评价；UI 独占输入与游戏名称。不要新建第二套可变战斗计数，也不要在 worker 读取设置或原生显示动画计数。

- Search 只接收快照、policy、diagnostics、frame signal 和 cancellation；不引用 Runtime 全局、UI 或 Testing。
- P2 外层补搜的双药、强制用药开局、回合边界、零费开局、战斗中精炼、回合末选牌及提前复制模式属于 `CombatSearchCoordinator.PostSearch`；`SearchRequestPipeline` 在主 Pass 和升级之后调用后处理，`Solve` 保留预览回调及最终结果字段填充。各模式共用 `SearchPassContext` 与请求账本，候选次序、原终局政策和诊断标签保持不变。
- `SearchBudgetWindow` 只冻结一次请求余量并套用既有成员上限，模式专用准入与候选合法性仍在对应 Pass；迁移预算读取时保持时钟和工作量采样顺序。
- P6 的 `PlanCommitment` 由已登记选牌效果、能力承诺描述及模拟后的开局动作提名；复制候选按 `PlanChoiceEffect.Nightmare` 和分支可达性识别，不用复制牌 ID。前缀经 `FrontierContinuationScheduler` 与同一请求账本有界续搜；Smart 无主动用药梯度先于计划成员完成，终局仍交给既有质量与药水政策。计划专属 Beam 代表不进入状态键或普通搜索。
- P7a 单项 Beam 权重扰动由 Testing 的请求宿主校验，Runtime 捕获为不可变 profile；Search 不读取请求宿主或运行中设置。普通请求不注入扰动，敏感度结果只用于中途保路评估，不改变终局或状态等价。
- P7b 生成药水链从合法早期前缀模拟识别零成本生成药，可在已选首张牌后加入合法进攻跟进，最多提名两条固定前缀并经 `FrontierContinuationScheduler` 消耗请求剩余额度。来源标记属于 `SimulatedCombatState`，Search 只读该分支事实；强制用药仍由原政策校验，终局比较保持单一权威入口。
- 首回合计划发现数量由请求级 `SearchPlanDiscoveryState` 在审计与 `PostSearch` 间共享；常规后验结束后才探测第二回合可打能力并用剩余账本续搜，未改善时保留先前完整结果。不得把该成员提前到 Smart 无药基线之前。
- `PlanHorizonPolicy` 只延长类型化收益证据已在模拟分支兑现的显式计划成员，最多一个当前牌堆周期；普通、未兑现计划及战斗状态键不变。现有复制/能力前缀先保持原后续出牌证据，免费药与已登记能力收益须有实际提名和触发证据；阈值合同不能代替长线请求的实际触发证据。
- 主 Pass 的固定前缀成员从 `SearchPassContext.SliceWindow` 取得轮次时间与请求节点；窗口仍按原顺序先读时间、后读节点。具体模式保留原准入阈值、profile 变体和候选顺序。
- 夜魇开局成员通过 `SearchBudgetLedger.ProfileWindow` 取得请求剩余节点和配置 profile 的原时间帽，不把它改成轮次剩余时间。
- P3 固定前缀迁移按模式逐个进行。双药开局的两种药水槽顺序由 `OpeningPotionPairContinuationSource` 产出，`FrontierContinuationScheduler` 负责用途／完整前缀去重、请求窗口、前缀合法性及求解器派发；模式仍负责原结果比较和诊断标签。
- 宽度组合的运行和成员遥测属于 `CombatSearchCoordinator.BeamPortfolio.cs`，主协调器只保留调用；纯移动时保持原方法体、成员迭代及进度发布顺序。
- 补充审计调度、开局能力／强制药／Smart 药水审计、梯度与层间内存检查属于 `CombatSearchCoordinator.Audits.cs`；主协调器只保留调用和共享质量比较。纯移动不改截止令牌、审计顺序或日志。
- 提前复制药水补搜由独立前缀来源产出。该模式的最低／最多用药数要在每条前缀派发时从当前 `selected` 读取，不能在枚举开始前冻结，因为前一条候选可能更新 `selected`。
- 零费开局来源沿用生成器原候选序列和八次尝试上限；该旧模式未去重，`DeduplicatePrefixes=false`，不能因迁入共享调度器而省略重复候选。
- 回合末选牌的两个候选通道保留来源原序：备选 EndTurn 至多两条，移除回收与随后出牌的短开局至多一条。选牌身份和改进判定留在模式，调度器只执行原预算固定前缀。
- 回合边界首轮锚点先按旧 `PowerPrefixKey` 去重、最多八个后再过滤 EndTurn；当前来源不二次去重。仅该模式声明可选用药诊断，调度器在 `Solve` 抛出 `PotionPolicyUnsatisfiedException` 时按原文本记录并继续，其他异常传播。
- `ContinuationSearchRequest` 承载现有前缀求解器的政策、进度、药水基线、用药上下界、incumbent 和最早用药回合覆盖。`FrontierContinuationScheduler.CreateSolver` 在业务异常捕获之外构造求解器，只有 `Solve` 期间的指定药水条件异常可按模式跳过。
- P5 串行与并行药水作业通过 `PreparedPotionChoiceWork` 持有选项、检查点和分支回放，`AdmittedParent` 登记串行派发与完成；两路径在各自原时点调用 `TryAdmitPlannedPotionChild`。串行逐分支交付、根诊断和直接快照释放，与并行批次所有权及原序提交均保持。
- P5 普通卡牌的已有选择限制和必须空选择由 `ExpansionPlan` 在准备时读取，串行与并行均经 `GeneratePreparedCardAction` 回放；挂起选择由 `AdmittedParent` 派发独立作业。串行作业沿用原完整选择链及预算，并行可将首层回放与续接交给 lane；显示名及候选所有权仍按原执行路径顺序处理。
- P5 回合尾部作业由 `AdmittedParent` 登记 Tail 派发与完成；串行保留原迭代器逐子节点交付，并行保留固定 lane 与原序批次。候选经 `AdmitPlannedEndTurnChildren` 做唯一转置准入与批次移交，未移交快照由其原 `ExpansionBatch` 释放。
- P5 `AdmittedJobScheduler` 从 `AdmittedParent` 统一选择准备、动作、挂起选择、药水和 Tail 作业；串行按卡牌、药水、尾部阶段领取，保留最后预算槽仅产出首个子节点的迭代行为，并行保留固定 lane、原序批次提交和异常排空。`IExpansionExecutor` 只接管子节点接收与父节点完成；阶段循环独占内存准入、波次容量及未派发父节点清理。
- 回合边界的零费开局与次回合防御追击在原位置完成预算准入和前缀合法性检查，再用 `Dispatch` 运行已定 profile；前一条候选改善后的 `selected` 与八次续搜上限仍由原循环控制。
- 强制用药开局的前缀续搜和次回合换序保持原 `try/catch` 包围请求构造与执行，继续只跳过 `PotionPolicyUnsatisfiedException`；请求带入原强制药水基线、最低／最多用药及 Boss 最早用药回合。
- 战斗中精炼与免费追击的可选后验通过 `DispatchOptional` 构造并求解，求解器构造在已定义药水异常捕获之外；前一条改善后，第二条的用药上下界仍从当前 `selected` 读取。
- 主 Pass 六种开局固定前缀使用 `Dispatch`，请求显式覆盖当前 `beamPolicy`，不能误用原 `passContext.Policy`；各模式的窗口采样、profile 标志、派发顺序及短路条件保持原位。
- 开局能力审计的资源防御、生成药水资源、药水能力和防御追击四种前缀保留 `ResetFixedPrefixSchedulingBaseline=false`；可选用药失败诊断、搜索列表与总工作量合并仍在原审计流程。
- 强制至少用药审计的首瓶、双药及双药后防御前缀保持原 `RequireAtLeastOne` 和 `primary.PotionCount` 上限，调度基线不重置；审计原序、提前达标与搜索总计由原函数处理。
- Smart 开局药水前缀保持原键去重、8／12 条限额及按首瓶分类计算的最多用药数；请求显式保留 `ResetFixedPrefixSchedulingBaseline=false`。协调器主文件不再出现直接 `fixedPrefixActions:` 构造。
- 夜魇前缀保留可选用药诊断和原药水上下界；能力路线成员的进度回调仍将阶段设为“正在深搜能力路线”，成员开始时的工作量、分配和时钟采样均在 `Dispatch` 外侧原位置。
- 默认关闭的前两回合实验仍以自身时间与追加节点额度形成续搜 profile；前缀经 `DispatchOptional` 运行，不改变侦察、层间交错、排名限额或药水业务失败诊断。协调器各分片均不得直接构造固定前缀求解器。
- `SearchPassResult` 同时记录所选路线质量与实际最后一轮的范围／边界及请求累计工作量；升级未改善时只把所选路线和质量换回旧值，不能把最后一轮终止原因误写成旧路线的原因。
- 本地策略脚本由 CheckpointTool 单独编译、Testing 请求宿主加载和卸载；Search 只读冻结的策略接口与分支数值特征，不承担脚本文件读取或程序集加载。无脚本请求保持生产排序及预算路径。
- 常驻会话的开发监控由 Testing 最多每秒发布一次纯标量快照，独立窗口进程只读会话文件；Search 不引用监控窗口或文件。关闭窗口不取消请求，stop 由工具层按进程身份清理。
- P8a 常驻会话超时时，在停止实例及覆盖监控状态前，把本请求最新且身份/时间匹配的监控快照保存到请求证据；旧包缺快照时不可从单一 timeout 状态猜节点、阶段或原因。
- P8a 续搜成员归因由 `FrontierContinuationScheduler` 传递既有 Purpose 给 `CombatBeamSolver`，求解结束时随原工作量在同一账本记录；未细分的直接求解器和协调器开销分别标明。归因结果只用于诊断，不改变预算、候选次序或最终政策。
- P8c 离线丢路查询必须按 `(SolverId, BoundaryId)` 配对观察；同状态别名、缺少观察、采集截断和其他成员的同号边界都不能被写成路线在当前成员内被剪的证据。
- `CombatBeamSolver.Transpositions` 独占转置标签及其支配前沿；`Models` 保留运行上下文和搜索特征。纯存储合同通过链接实际partial源码验证，不为测试复制生产判定。
- 玩家回合末第二阶段由 `PlayerTurnEndLifecycle.RunPhaseTwo` 统一安排常规 Power、遗物及晚期 Power；Search、风险预估和无人差分调用同一入口，阶段内的挂起选择立即返回。
- `src/Api/PreCombat*` 拥有公开 API v5 的战前请求、状态/设置令牌与独立游戏 worker。主线程捕获，worker 通过无人协议恢复并复核完整跑局；Mod 使用独立副本、当前账号设置映射到 worker，求解设置变化使缓存及 worker 失效。跨请求停止/期限不能只等待繁忙锁；此边界不把 headless 等同操作系统沙箱。
- `CombatBeamSolver.cs` 只负责构造和接线；阶段循环、展开、评估、中间保路、终局排序和终局回放各在现有分片。
- 单次搜索可变状态属于 `SearchRunContext`；中间候选属于 `BeamRetentionPolicy`；终局政策属于 `FinalPlanOrdering`。
- `BeamRetentionPolicy` 的主构造、字段和初始化顺序留在根分片；OrderedMutation、OrderedMutationScheduling、Routing、Ranking、Testing只分组已有成员，仍共享同一个策略实例，不引入新的状态所有者。
- `Expansion` 根分片保留Expand与跨回合剪枝/结束回合准入；Opening、Choices、Replay、Candidates只迁移既有连续成员段。快照、选择预算和租约所有权仍按原调用链交接，不以文件拆分为由新增缓存或调整调用次序。
- 循环出口的族内、在途和最新候选仅在各自租约前缀同分后共用 `Retention` 内的 `CompareCycleExitQuality`；待准入候选直接使用该质量顺序。区域、跨回合和终局比较器的键次序不同，不因字段相似合并；共享方法不新增状态或依赖。
- `AdmittedExpansion` 中求解器层的 `AdmittedParent` 持有已预约父节点的作业状态和结果，固定 lane 排空并归并后才复用；提交仍按父节点和动作原序。提前 EndTurn 使用同父 Fork gate，独占批次和暂存基线；全部兄弟动作/选择/药水结束后才移交快照并发布基线，不让 worker 写父 Aggregate。`PrimaryChoiceReplayFrontier` 独占必经首层回放的暂存快照，所有生产作业结束后才移交一个续接消费者；动态预算和 occurrence collector 不跨 lane 共享修改。异常先排空，再释放 probe、frontier、batch 与根。
- 已撤回的`AdmittedParent`否定就绪缓存实验只能在coordinator内使用；任何改变选择就绪条件的路径都必须失效，实验入口是`MarkDispatched`和`Receive`。worker不能直接改这些条件，正结果继续保持原扫描优先级。
- `StandPatJobs` 只预计算原保路规则必经的未缓存探针，复用当前固定 lane；worker 释放完整回放的临时快照后只回传标量，缓存按首个原代表和原序由 coordinator 写入。Prune 根在全部作业排空前保持所有权，批内没有新准入或内存检查点；executor Dispose 清除 `SearchRunContext` 的活动引用。
- `RetentionJobs` 只在展开已排空后复用相同 lane 计算本次保路只读输入，索引结果和组内摘要独占；coordinator 统一应用观察请求、更新共享计数并计入包括失败作业在内的后台分配。不得把这个入口用于模拟、准入或与 Prune 重叠的推测展开。
- `RoutingChoiceNodes` 只持有本次 RankBest 的有序组和派生统计；分组完成后冻结排名摘要，在本次排名赋值前消费完毕，不把可变父排名放入跨调用缓存。监听分段属于 `SimulatedCombatState` 的分支派生视图；前段/后段均以不可变形式发布，完整路径保留无锚点与不透明 CardModifier 语义。 有效/活动前段与 Power 投影的保留只适用于卡牌/球失效，完整失效清空全部派生段；前段独立存活时仍由同一 Fork 上下文重映射。
- controller 状态属于 combat/search/deployment session，不回退为并列静态字段。
- 跨 SL 路线记录由 Runtime 的 `SolvedRouteCache` 持有磁盘协议；在普通根捕获和回合开始选择根捕获后按状态与策略匹配。结果中的 Forecast 从新根重新绑定，磁盘和跨会话所有者均不得保留旧 Creature/MoveState。Search 不读取路线文件。
- UI renderer 只消费 `SolverOverlay*Snapshot`；结果到 snapshot 的复制发生在主线程边界。
- `SolverActionBar` 只消费布局状态及已有控件，不读取 Controller 或战斗对象；命令绑定和能力判断仍由 Overlay 接入既有 Runtime 入口。
- 改动 UI 文案及投影时读取 `../ui-localization/SKILL.md`：保留中英模板和胶囊/tooltip 的统一来源；名称与语言在主线程捕获，worker 使用冻结显示表。不要把日志 Describe 重新接回玩家界面或把本地化带入 Search。
- 设置页自身驱动的后台任务使用控件所有者的完成邮箱收口；问题包上传的成功、失败和取消由 `SolverSettingsPanel._Process` 消费，不借用搜索生命周期的 `SolverDispatcher`。
- 问题包 `CombatBugReportMetadata` 在主线程冻结战斗/角色/怪物与比较标量；Uploader 读取归档中的同一份 report.json，发送前核对身份和玩家描述。新包按 report.json、diagnostics/、replay/ 组织，CheckpointArchive 兼容旧包路径，禁止后台重新采样 live 元数据。
- unattended 的协议、建局、执行、断言和结果写入分别属于 ProtocolHost、ScenarioBuilder、Executor、Assertions、Writer。GeneratedCombatScenario只解析原版目录与独立种子流；ScenarioBuilder执行生成配置并独占临时开局选择器，Writer保存完整配置/装备/开局证据。批量脚本不得把生成随机流写入游戏战斗RNG或把选择器延长到正式部署。
- `src/Replay` 只依赖标准库：包校验、顺序事件临时文件；Runtime 冻结原生输入，Testing 重放和对账，CheckpointTool 负责批量调度与结果口径。跨平台脚本只承担本平台启动与进程所有权。
- headless 实例目录、完整游戏/Mod 内容快照和主机资源预约属于 `tools/testing/headless-runtime.ps1/.sh`；默认实例根固定为当前仓库 `.local/headless-instances/<实例>`，不得回到 `%LOCALAPPDATA%` 或 XDG state。用户目录只保留跨任务互斥所需的小型主机租约。启动器保留请求协议、精确 PID/出生身份终止、结果与静稳 ACK。不得把测试协调放入游戏 Search/Runtime，或只删全局进程检查而继续共享 DLL/协议。并行只作正确性/吞吐验证，性能对照使用独占模式。
- CoverageCatalog 只消费 `IMethodMirrorRegistryDescriptorProvider`，不反射 registry 私有字段。
- 覆盖材料按 `coverage/README.md` 收纳；CoverageCatalog 从 catalog/evidence 读取登记，按完整路径消费 fixtures，替换 catalog/generated 快照，候选输入生成到 .local。移动目录保持证据等级与语义输入，运行器继续由 tools 持有。
- Testing 按 `src/Testing/README.md` 的 Host、Support、Replay、Contracts 与 Regressions 收纳；移动保持原 partial 类型与协议。退役调查先检查跨层消费者，提取仍复用的 helper，再删除专用路由。一次性测试在 .local/tool-tasks 显式接入，任务结束清理；只有独立、可复跑的机制合同进入正式源码树。

## 3. 实现方式

- 沿用仓库现有 concrete type、partial 和窄接口，不引入 DI 容器、事件总线或多程序集拆分。
- 纯移动批次保持方法体、可见性、集合类型、迭代顺序和调用顺序。
- 需要新抽象时，让它拥有真实状态或消除实际重复；不要只建 facade 转发旧单体。
- snapshot 在所有权边界一次性复制，不让下游重新追溯 mutable 对象图。
- 工具元数据由被描述对象自己提供，避免工具依赖私有字段名。
- fail-fast 行为、stage 名、结构化日志和请求/result schema 在纯重构中保持不变。

## 4. 滚动批次

一次提交完成一个可解释边界：

1. 记录迁移对象和不变量；
2. 移动或接入具体所有者；
3. 删除旧所有权和双写路径；
4. 同步扩充 `verify-refactor-boundaries.ps1` 与 `verify-refactor-boundaries.sh`，阻止旧结构回流；
5. 运行结构门禁和一个代表场景；
6. 更新架构地图、必要的核验记录并直接提交。

不要等所有目录都看完才写。按职责块读取、修改、验证、记录和提交。

## 5. 验证

纯职责移动的最低验证：

- Release 编译；
- `pwsh -NoProfile -File tools\inspection\verify-refactor-boundaries.ps1`（Windows）或 `./tools/inspection/verify-refactor-boundaries.sh`（Linux）；
- 一个穿过新边界的代表 headless 场景；
- 若移动 Beam 比较器，比较动作序列、expanded/transitions/choice branches 和各剪枝计数；
- 若移动 UI 边界，验证 renderer 签名与 ready/deploying/complete 事件，人工视觉项不冒充 headless 通过；
- 若移动 registry 元数据，比较 CoverageCatalog 前后分类与生成文件；
- 若移动 unattended，覆盖 Passed、Failed、Held 和同进程恢复中受影响的协议分支。

文档或 skill 本身的维护只需路径、链接、frontmatter 和职责一致性检查，不自动跑实机。

## 6. 记录

- `docs/ARCHITECTURE.md` 保存当前事实；
- `docs/refactoring/verified-audit-*.md` 保存阶段证据，不作为永久入口；
- `docs/refactoring/refactor-roadmap.md` 保存批次状态；
- `docs/TEST_MATRIX.md` 保存可重跑场景。

普通架构重构直接提交，不自行提升版本、不计算文件哈希。版本和打包时机以 `AGENTS.md` 的活动发布批次和发布口令为准，再转 `release-gate`。

- `RoundTransition` 只在无计划选择的EndTurn初探中，于普通抽牌和历史补偿完成后保存无挂起选择的前缀；当前仅ToolsOfTheTradePower存在时预留。原Fork事务断言保持，复制前临时关闭空cursor并在finally恢复。前缀匹配父节点引用、EndTurn回合与PlayerTurnStart选择，Knowledge选择完整回放；不跨父/搜索共享。frontier拥有checkpoint，同父gate串行Fork，排空后释放。新增捕获计数包含额外物理Fork，DOP等价比较扣除该项后的转移Fork；完整状态/续用/历史与兄弟隔离须直接对账。

- 正式手动自身选牌续执行支持当前清单中的41张原版单人卡，要求无附魔/污染、单次手动打出、空显式cursor、单层card scope、可重映射活动历史及无不透明/事务StateStore。Engine保存显式CardPlay/frame并复用唯一结算尾部；Prediction独占seed/frame/deaths和Fork锁，Search仅在同父同动作选择链或frontier内持有。普通Fork仍拒绝挂起种子；私有Fork临时移走所属pending request并运行原事务断言，全部模型/trace/play/history使用同一PredictionForkContext。再次挂起时退出全部子scope再完整回放，额外物理Fork单列fallback，不多扣逻辑transition/选择预算。旧路径不得运行捕获诊断或持有检查点；释放必须在生产者/消费者排空后完成。不保留Task/闭包，不跨父、搜索或战斗缓存。完整状态/历史/RNG、兄弟修改、原生结算、DOP、取消/异常排空与增量等价直接验证；各来源的命中和整搜收益分别报告。已生成的请求/spec必须一并捕获，不能在恢复时再次GetSpec（探寻打击会再次消耗RNG）；同一个Fork context复制请求候选、生成历史中的非牌堆wrapper、CardPlay及其格挡金额/事件计数。

- 9种原版手动选牌药水共用 `PotionExecutionSupport.Prepare/Complete`；检查点在消费槽位和Use完成后、选择应用及AfterPotionUsed之前，种子仍须通过普通Fork断言。四种生成药水从检查点运行原空选择探测，使用后钩子执行完才读取候选；其他五种仍从父状态准备候选。Search的串行/并行准备共用入口，同父完整动作匹配且仅Choice可替换；frontier或串行枚举拥有检查点并在排空后释放。生成候选历史只读共享，Apply继续Clone选中牌；分支可变牌/RNG由普通Fork隔离。嵌套再次挂起从原父完整回放；额外前缀Fork与fallback分别记账，不改变transition/choice预算；worker合并和归零须包含四个药水计数。第三方药水或登记覆盖原版选择的药水不进入此特化。不保存Task或闭包。验证全部九种原生结算、完整状态/历史/RNG、消耗/后置钩子、兄弟修改及DOP/取消/异常/增量对账。

- 嵌套执行检查点保存纯数据帧与明确程序阶段/下一循环序号。所有CLR作用域退出后，核对领域事务、StateStore、活动CardPlay及延迟抽牌/生成历史的精确配对；普通Fork继续拒绝捕获/挂起/已准备种子。一次PredictionForkContext重映射状态、帧、候选、历史、CardPlay、Power来源及共享死亡集合，保留trace来源身份和抽牌深度限制；外层列表所持但已离开所有牌堆的wrapper也必须显式Fork，不能假设State已登记。未知派发必须拒绝整次捕获，继续原完整回放，不能默认缺失尾部已执行。已确认的抽牌、弃牌、Hook、重复子出牌与回合来源循环复用唯一普通执行体，恢复可以再次挂起。Search匹配同父完整动作及已消费选择前缀，只追加下一选择；选择层/frontier排空后释放全部图引用。不保存Task/闭包，不跨搜索缓存；严格增量基线禁用捕获。ExecutionChoiceCaptures/Reuses不扣选择预算，reuse替代一次原转移Fork，不能作为额外物理Fork从比较器扣除。源循环、深层选牌、DOP/取消/异常、有限预算耗尽与原生完整状态分别验证。

- `CombatRootSnapshot.HistoryDependencies` 在主线程按全牌堆、监听者及药水冻结，Search 不读 live 来源；随机生成/变牌及第三方来源保守保留六类历史。`SearchRequestWorkTotals` 拥有请求共享的额外循环回放额度，子 solver/worker 不得重新领额；独立 Evaluate 才创建私有额度。结果区分所选 solver 回放数与请求回放总数，普通 Expanded/Transitions 口径保持。
