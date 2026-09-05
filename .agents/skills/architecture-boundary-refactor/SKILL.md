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

写清楚：

- 当前谁创建、持有、修改和销毁该状态；
- 目标类型的单一职责；
- 哪些调用顺序、集合顺序、比较器、日志事件和协议字段必须保持；
- 目标边界允许依赖什么，禁止依赖什么；
- 哪个代表场景能穿过这条边界。

优先迁移所有权，再考虑复用、池化或算法优化。不要在同一批同时移动代码、改变策略和优化分配。

## 2. 当前结构约束

- Search 只接收快照、policy、diagnostics、frame signal 和 cancellation；不引用 Runtime 全局、UI 或 Testing。
- `CombatBeamSolver.cs` 只负责构造和接线；阶段循环、展开、评估、中间保路、终局排序和终局回放各在现有分片。
- 单次搜索可变状态属于 `SearchRunContext`；中间候选属于 `BeamRetentionPolicy`；终局政策属于 `FinalPlanOrdering`。
- controller 状态属于 combat/search/deployment session，不回退为并列静态字段。
- UI renderer 只消费 `SolverOverlay*Snapshot`；结果到 snapshot 的复制发生在主线程边界。
- 设置页自身驱动的后台任务使用控件所有者的完成邮箱收口；问题包上传的成功、失败和取消由 `SolverSettingsPanel._Process` 消费，不借用搜索生命周期的 `SolverDispatcher`。
- unattended 的协议、建局、执行、断言和结果写入分别属于 ProtocolHost、ScenarioBuilder、Executor、Assertions、Writer。
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
