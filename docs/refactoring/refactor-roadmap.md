# CombatSolver 滚动重构路线

本页早期路线已完成并合并到 `main`；下列追加批次以各自证据的状态为准。

2026-09-20：[搜索内存生命周期重构](search-memory-lifecycle-20260920.md) 已完成所有权迁移与失败完成链修复，最终对照进行中；不提升版本、不发包。

本重构按小范围滚动推进。每一轮只读取相关源码和夹具，先更新核验记录，再实现、验证和提交。未核验的外部审计建议不直接进入代码。

## 第一轮：失败语义与搜索请求边界

### 目标

- 任何未知模拟错误都不能表现为候选被正常淘汰。
- 一次搜索使用启动前捕获的固定策略，不受运行中设置变化影响。
- Search 不直接依赖 Runtime、UI、Testing 的静态所有者。
- 不改变 Beam、评分、候选顺序、RNG 或最终路线政策。

### 实现批次

1. [x] 增加显式的预测未支持异常和带动作上下文的搜索转移异常。
2. [x] 删除动态变量默认值、推断动作吞异常以及三处 Beam 宽泛跳过。
3. [x] 新增失败边界无人测试，证明未知失败向外传播且不会作为正常候选返回。
4. [x] 新增不可变 `SearchPolicySnapshot`，统一传入搜索 profile、药水政策、诊断开关、首回合模式、验证选项和测试预算。
5. [x] 用 `SearchDiagnosticsSink` 替代 Search 对全局 logger 的调用。
6. [x] 用 `SearchFramePressureSignal` 替代 `SearchWorkPacer` 对 `SolverController` 的调用；进程级 GC 策略移动到 Runtime 所有权。
7. [x] 增加轻量结构检查，阻止 Search 重新引用全局设置、Controller、UI 或无人测试 runner。

### 第一轮完成条件

- [x] Release 编译通过。
- [x] 新增的 computed dynamic var、推断执行、卡牌回放和药水回放失败夹具通过。
- [x] 推断卡牌与选择卡牌严格差分保持通过。
- [x] CalculatedVar、药水 OnUse/选择和 Smart/RequireAtLeastOne 回归通过。
- [x] 双小啃兽普通搜索、增量等价、完整自动部署和零非预期重算通过。
- [x] CoverageCatalog 在当前版本重新生成并通过全部现有 verify。
- [x] 从最终提交构建并生成第一轮 Release 包。

## 第二轮：控制器会话生命周期

### 实现批次

1. [x] 建立进程、战斗、搜索、部署和测试观测字段表。
2. [x] 用 `SolverCombatSession` 收口路线、续用、重算审计与本场控制状态。
3. [x] 用 `SolverSearchSession` 收口取消、进度、帧/GC 指标和完成后部署，并以实例身份拒绝旧 callback。
4. [x] 用 `SolverDeploymentSession` 收口取消和完成；详细诊断使用部署启动快照。
5. [x] 同步战斗结束异步 GC 回收与下一次搜索入口，消除回收进行中直接搜索的竞态。
6. [x] 增加会话取消定向测试和结构检查。

### 第二轮完成条件

- [x] Release 编译和结构检查通过。
- [x] 策略快照、会话取消、旧 callback 隔离、GC 回收后重搜和完整自动部署通过。
- [x] 双小啃兽普通、增量、跨回合复用和零非预期重算通过。
- [x] CoverageCatalog 当前版本全门禁通过。
- [x] 从最终提交构建并生成第二轮 Release 包。

### 第三轮：Fork 静止边界与状态 schema

1. [x] 汇总 combat state、StateStore、history 和 Hook 私有状态的 Fork 条件。
2. [x] 增加统一预检接口，在分配克隆前验证完整静止边界。
3. [x] 补齐动作选择、出牌执行、钢笔尖、臂铠和蜷身的瞬时事务。
4. [x] 增加多层边界定向测试和结构检查。
5. [x] 配对出牌、钢笔尖、臂铠、蜷身严格回归通过。
6. [x] 双小啃兽增量、跨回合复用和零非预期重算通过。
7. [x] CoverageCatalog 当前版本全门禁通过。
8. [x] 从最终提交构建并生成第三轮 Release 包。

### 第四轮：主线程搜索根

1. [x] 新增 `CombatRootSnapshot`，限定只能在主线程捕获；同一捕获前后比较完整 live/continuation 文本。
2. [x] 在主线程物化生物数值、五个牌堆及卡牌副本、球、Power、监听器列表、怪物 AI/私有状态、遗物计数、药水槽、RNG 和当前战斗历史。
3. [x] `CombatSearchCoordinator` 和 `CombatBeamSolver` 只接收根快照；所有 Beam 根和反事实审计从该根 Fork，不再在 worker 构造 live `SimulatedCombatState`。
4. [x] Fork 构造不再回读 live roster、回合、生物生命/格挡和 Hook listener 列表；Continuation predicted 捕获直接使用已捕获玩家身份。
5. [x] 增加根快照线程/隔离定向测试、捕获耗时与物化数量日志，并扩充结构检查。
6. [x] 钢笔尖增量路线、双小啃兽普通与增量整战通过，路线质量和零重算不退化。
7. [x] 快照玩家回合/金币、遗物与药水清单、卡牌注册、Osty、初始 Power、Run RNG/幕/房间和已知怪物私有字段；Relic/Potion/Card listener 使用根克隆，`ContainsCard` 改由分支注册表回答。
8. [x] 克隆 Relic 时从 live 原件显式迁移 25 类受跟踪私有状态；根物化结束后清除原 Relic 引用，漏状态直接失败。
9. [x] 完成 worker `IRunState` 读取审计：运行级标量、RNG、起始回合和监听器前缀在主线程捕获；牌组 Card/Enchantment listener 使用根克隆，Hook、卡池筛选和生成逻辑消费分支视图。
10. [x] 完成原版 Monster/Modifier identity listener 字段级审计：Modifier listener 使用根克隆；永世沙漏读取分支计数，Murderous 召唤语义显式补偿；Queen 保留已证明只作稳定关联键的 Creature identity，不深拷贝怪物行动图。
11. [x] 复制原版 Badge listener；将 `MultiplayerScalingModel` 改为无 live RunState/CombatState 的根占位模型，并用单人专用 mirror 保留倍率语义。
12. [x] 完成 Player/Creature/Monster/Encounter 稳定 identity 字段级读取清单：玩家/生物可变值、怪物行动攻击模板/随机权重/条件选择/行动参数和 Encounter 槽位均在根捕获或分支状态中；根物化后的 Creature/Player/Monster AI/意图缺项直接失败。保留的 identity 只用于类型、ID、Owner、槽位名和只读行动拓扑。
13. [x] 为第三方 `ModHelper` subscriber 建独立清单和适配边界：run/combat 分段捕获、Loadout 最大手牌根值、BaseLib CardModifier 卡牌克隆/Owner 重映射及未知 gameplay subscriber 显式拒绝均完成；实际 Modifier 写时复制夹具通过，完整 Mod 栈机甲连续两次保持 No-GC 并通过可见性能门槛。
14. [x] Orb listener 不再保留根战斗中的 live OrbModel；每个分支从自己的 `SimOrbQueue` 重建监听器，并在球队列变化时失效缓存。

### 后续

### 第五轮：CombatBeamSolver 阶段边界

1. [x] 将约 4,133 行、94 个方法的 `CombatBeamSolver` 按 `Entry/Models/Phases/Expansion/Retention/Terminal/StateEvaluation` 拆为同一 partial 类型；字段、方法体、可见性、集合和迭代顺序保持不变。
2. [x] 扩充结构门禁，固定 partial 文件清单，并禁止新的搜索阶段重新合并到 `Entry`。
3. [x] 用固定 Beam 场景比较动作序列、expanded、transitions、choice branches、剪枝计数和终局指标，证明纯移动精确等价。
4. [x] 引入 `SearchRunContext`，逐项迁移单次搜索计数、转置/缓存、工作节流和性能指标；先迁移所有权，不复用 scratch、不改算法。
5. [x] 建立 `SearchFeatures`、`BeamRetentionPolicy` 与 `FinalPlanOrdering` 的具体类型边界；先双路径对照，再切换调用，不调整权重或排序。

### 第六轮：剩余编排与单一事实源

1. [x] 将 unattended runner 从同一 partial 的历史分片整理为 ProtocolHost/ScenarioBuilder/Executor/Assertions/Writer 编排边界。
2. [x] 建立 `SolverOverlaySnapshot`，让 UI renderer 不读取搜索/预测可变类型。
3. [x] 让 mirror registry descriptor 提供 CoverageCatalog 所需支持元数据，逐项比较后删除重复登记。
4. [x] 修复怪物离开活动 roster 时过早删除 AI/静态参数的生命周期错误，并用双小啃兽长线增量/完整回放关闭回归。
5. [x] 从最终提交通过 Release、结构、CoverageCatalog、高档机甲精确轨迹和双小啃兽完整自动战斗门禁；不生成发布包。

## 明确不做

- 不引入 DI 容器、事件总线或多程序集拆分。
- 不重写为 ECS 或完整值类型战斗世界。
- 不支持多人模式，不重新依赖 RandomForeseer。
- 不用扩大搜索预算掩盖模拟错误。
