---
name: architecture-boundary-refactor
description: 重构 CombatSolver 的 Search、Runtime 会话、UI snapshot、无人测试编排或 mirror registry 职责边界时使用；目标是迁移所有权和依赖，同时保持战斗语义、路线政策与协议行为不变。
---

# CombatSolver 架构边界重构

## 适用边界

本 skill 处理结构和所有权：拆分大类、迁移 run/session state、建立策略对象、隔离 renderer、整理测试编排、为工具提供稳定元数据。

如果任务改变卡牌/怪物结算，叠加 `combat-semantic-change`；改变 Beam 权重或候选政策，叠加 `search-performance-optimization`。纯重构不能借机改变这些行为。

开始前读取 `docs/ARCHITECTURE.md`，并读取 `tools/verify-refactor-boundaries.ps1` 与 `tools/verify-refactor-boundaries.sh` 中对应边界，只读取本次涉及的源码分片。两套门禁分别服务 Windows 和 Linux，规则必须保持等价。

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

- 遗物策略的可控范围由 RelicCounterCatalog 声明，Runtime 冻结本场目标，Search 只读已有分支计数并输出标量评价；UI 独占输入与游戏名称。不要新建第二套可变战斗计数，也不要在 worker 读取设置或原生显示动画计数。

- Search 只接收快照、policy、diagnostics、frame signal 和 cancellation；不引用 Runtime 全局、UI 或 Testing。
- `CombatBeamSolver.Transpositions` 独占转置标签及其支配前沿；`Models` 保留运行上下文和搜索特征。纯存储合同通过链接实际partial源码验证，不为测试复制生产判定。
- 玩家回合末第二阶段由 `PlayerTurnEndLifecycle.RunPhaseTwo` 统一安排常规 Power、遗物及晚期 Power；Search、风险预估和无人差分调用同一入口，阶段内的挂起选择立即返回。
- `src/Api/PreCombat*` 拥有公开 API v5 的战前请求、状态/设置令牌与独立游戏 worker。主线程捕获，worker 通过无人协议恢复并复核完整跑局；Mod 使用独立副本、当前账号设置映射到 worker，求解设置变化使缓存及 worker 失效。跨请求停止/期限不能只等待繁忙锁；此边界不把 headless 等同操作系统沙箱。
- `CombatBeamSolver.cs` 只负责构造和接线；阶段循环、展开、评估、中间保路、终局排序和终局回放各在现有分片。
- 单次搜索可变状态属于 `SearchRunContext`；中间候选属于 `BeamRetentionPolicy`；终局政策属于 `FinalPlanOrdering`。
- `AdmittedExpansion` 只调度已预约父节点内的作业，固定 lane 排空并归并后才复用；提交仍按父节点和动作原序。提前 EndTurn 使用同父 Fork gate，独占批次和暂存基线；全部兄弟动作/选择/药水结束后才移交快照并发布基线，不让worker写父Aggregate。`PrimaryChoiceReplayFrontier` 独占必经首层回放的暂存快照，所有生产作业结束后才移交一个续接消费者；动态预算和 occurrence collector 不跨 lane 共享修改。异常先排空，再释放 probe、frontier、batch 与根。
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
- unattended 的协议、建局、执行、断言和结果写入分别属于 ProtocolHost、ScenarioBuilder、Executor、Assertions、Writer。
- `src/Replay` 只依赖标准库：包校验、顺序事件临时文件；Runtime 冻结原生输入，Testing 重放和对账，CheckpointTool 负责批量调度与结果口径。跨平台脚本只承担本平台启动与进程所有权。
- headless 实例目录、完整游戏/Mod 内容快照和主机资源预约属于 `tools/headless-runtime.ps1/.sh`；启动器保留请求协议、精确 PID/出生身份终止、结果与静稳 ACK。不得把测试协调放入游戏 Search/Runtime，或只删全局进程检查而继续共享 DLL/协议。并行只作正确性/吞吐验证，性能对照使用独占模式。
- CoverageCatalog 只消费 `IMethodMirrorRegistryDescriptorProvider`，不反射 registry 私有字段。

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
- `pwsh -NoProfile -File tools\verify-refactor-boundaries.ps1`（Windows）或 `./tools/verify-refactor-boundaries.sh`（Linux）；
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
