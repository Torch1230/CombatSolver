# CombatSolver 重构核验记录

基线为 CombatSolver `0.13.35`、源码提交 `4117eb0`、游戏 `0.111.0`。本文只记录已经与当前源码核对的结论；外部审计原文和 proposed-docs 保留在工作区外，不作为实现事实来源。

## 第一块：失败语义与搜索请求边界

### 已确认

- `CombatPredictionDynamicVarExtensions` 调用未知 `IComputedDynamicVar` 失败后返回 `0m`。这会把不支持的分支伪装成合法数值。
- `CardOnPlayInferrer.Infer` 返回的执行器捕获所有异常并继续，可能在部分动作已经写入影子状态后留下可继续搜索的状态。
- `CombatBeamSolver` 对卡牌回放、药水回放和带选择药水回放分别捕获所有异常并跳过候选。搜索仍可能返回其他路线，无法区分语义故障和正常剪枝。
- `CombatBeamSolver` 与 `CombatSearchCoordinator` 在 worker 中读取 `SolverSettings.Current`，并直接调用 `Entry.Logger`。
- `SearchWorkPacer` 直接调用 `SolverController.WaitForMainThreadRecovery`；搜索算法依赖运行时静态控制器。
- `SolverController` 和首回合入口已经在主线程捕获 `SolverSettingsSnapshot`，但没有把药水政策和详细诊断完整传入搜索，所以 worker 仍回读全局设置。
- 当前 coverage 目录仍标记为 `0.13.34`，不能作为 `0.13.35` 当前工作树的验证结果。

### 对外部审计的修正

- 不移除全部推断 OnPlay。仓库已有 43 张普通推断卡和 12 张带选择推断卡的完整状态差分证据。第一轮只删除执行期吞异常，保持现有推断、补偿和风险登记语义。
- 不采用外部报告的类型和方法数量。该扫描对 237 个 C# 文件识别出 0 个类型和 0 个方法，结果不可用。
- 不把 proposed-docs 直接写入仓库，其中仍有“未定位到声明”占位符，并且多项规则已存在于 `AGENTS.md`。
- 不新增提交哈希、DLL 哈希或生成时间门禁。常规 Git 提交足以承担来源管理；coverage 只需要在当前版本重新生成。

### 第一轮决定

1. 未支持动态变量显式失败，不再返回默认值。
2. 推断动作执行失败直接退出当前搜索。
3. 卡牌和药水回放使用一个统一失败边界；只有取消和明确的无效选择属于可识别业务分支。
4. 主线程形成完整的搜索策略快照；worker 不读取设置、测试 runner 或 Controller 静态状态。
5. Search 通过窄诊断出口记录事件，通过独立的帧压力信号节流，不认识 Runtime/UI/Testing owner。

### 已实现并验证

- 动态变量、推断动作和搜索回放失败边界已经按上述决定修改，Release 编译通过。
- `SearchPolicySnapshot` 已覆盖搜索 profile、药水政策、诊断、首回合、验证/测试选项、偷窃策略、诊断出口和帧压力信号。
- `SearchGcPolicy` 已移动到 Runtime；Search 目录不再直接引用设置、logger、Controller、UI 或无人测试 runner。
- `REFACTOR-FAILURE-BOUNDARIES-001`：runId `94933ce7cbe44f71879440fb1c48ed7d`，失败边界检查和完整自动战斗通过，零非预期重算。
- `REFACTOR-SEARCH-POLICY-002`：runId `e39073d7512d4b9694382be2dd410b5d`，全局设置改变前后复用同一策略快照，两次搜索结果精确一致；失败边界和完整自动战斗同时通过。
- `tools/verify-refactor-boundaries.ps1` 当前检查 43 个 Search 文件并通过。
- 推断卡 43/43、推断选牌 12/12、CalculatedVar 25/25、药水引擎 17/17 通过；自动生成 X 费夹具改用 1 能量，`HELIX_DRILL` 前置打击夹具用敌方格挡避免提前清场。
- Smart 反事实 runId `4c8dc991752d46aab8b5a54ed0c28c15` 和 RequireAtLeastOne runId `7ee8ba1941554531b9b289120980597d` 通过，均为零非预期重算。
- 双小啃兽普通 runId `efa79edb7e48447c81c3840dfc7be60d` 与增量 runId `f03343e6be8b4561890dae0ae930d7f6` 均在第 5 回合结束、两次洗牌、0 药、0 战损并成功复用。

## 待后续逐块核验

- Fork 静止边界遗漏的动作选择、卡牌执行和死亡事务。
- live 根状态和 `CombatManager.History` 在 worker 中的惰性读取。
- fingerprint、continuation 和 strict diff 的状态登记一致性。
- Beam 保路与最终路线排序的实际调用边界。

## 第二块：SolverController 生命周期

### 字段所有权核验

| 生命周期 | 当前字段 | 正确所有者 |
|---|---|---|
| 进程/持久设置 | `_solverDisabled`、三个 full-auto stop 开关、帧压力信号和帧桶上界 | `SolverController` 进程级静态状态 |
| 单场战斗 | `_fullAutoEnabled`、`_theftPolicy`、路线/续用结果、重算基线、重算计数、最后差异、最后部署回合、控制模式 | `SolverCombatSession` |
| 单次搜索 | 搜索取消源、generation、搜索中/完成后部署、请求状态与 stamp、进度、帧桶和 GC/分配起点 | `SolverSearchSession` |
| 单次部署 | 部署取消源、部署中标记 | `SolverDeploymentSession` |
| 测试观测 | 最近结果、最近异常、三个 full-auto stop 结果、已部署卡牌/药水 | 暂留控制器只读出口；后续与测试 runner 一并拆分 |

### 已确认

- `Reset` 同时手写清理搜索、部署、战斗和测试字段；新增字段容易漏掉，并且无法从类型上看出何时失效。
- `_searchState` 同时承担搜索请求身份和战斗存在监控锚点；搜索操作结束后仍保留，名称和生命周期不一致。
- 帧计数、帧桶、进度和 GC 起点属于单次搜索，却以可变静态字段存在；generation 负责防止旧 callback 写回新搜索。
- `_latestResult`、`_latestStamp`、`_continuationSource` 和重算审计属于整场战斗，不属于某次 worker 搜索。
- 部署已经捕获 `SolverSettingsSnapshot`，但详细诊断仍在每个动作后读取 `SolverSettings.Current`，同一次部署的行为可能受中途设置变化影响。

### 第二轮决定

1. 保持 `SolverController` 静态入口，不引入容器或事件总线。
2. 用三个小型会话对象承接战斗、搜索和部署字段；以替换整个会话对象完成失效，不再逐字段复位。
3. 搜索 callback 绑定创建时的 `SolverSearchSession` 实例；generation 只用于日志，不再作为状态正确性的唯一依据。
4. 部署详细诊断使用启动时已捕获的设置快照。
5. 本轮只移动所有权和生命周期，不拆部署算法、不改变路线、评分、RNG 或 UI 文案。

### 已实现并验证

- `SolverCombatSession`、`SolverSearchSession`、`SolverDeploymentSession` 已承接对应可变字段；`Reset` 取消活动操作后直接替换战斗会话。
- 搜索进度、帧桶、分配/GC 起点和完成后部署标记随 `SolverSearchSession` 创建；worker 进度和完成 callback 只接受同一会话实例，已取消的旧 callback 无法写回。
- 部署完成只清理创建它的 `SolverDeploymentSession`；部署详细诊断固定使用启动时的设置快照。
- 生命周期测试第一次组合复跑暴露既有 GC 竞态：战斗重置的异步回收仍在进行时，新搜索直接抛出 `Cannot start search while combat-end GC reclaim is active`。`SearchGcPolicy` 现在用完成信号同步回收和新搜索入口；搜索 worker 等待真实回收完成后再建立低延迟区，不跳过回收、不返回默认策略。
- `REFACTOR-CONTROLLER-POLICY-004`：runId `128c54b5da8b4c61973524ed222d253a`，策略快照、搜索取消、旧 callback 隔离、回收后重搜和完整自动部署通过，零非预期重算。
- 双小啃兽普通 runId `21b29399fc294d28bd721378248b167c` 与增量 runId `0f85f6bd05ce484b83691eec0787a74f` 均在第 5 回合结束、两次洗牌、0 药、0 战损，`UnexpectedReplans=0`。

## 第三块：Fork 静止边界与状态 schema

### 已确认

- Fork 禁止条件原本分散在 `CombatPredictionSimulator`、`SimulatedCombatState`、`CombatPredictionHistory` 和六类 Hook 私有状态的 `Fork` 实现中；StateStore 可能在复制一部分状态后才遇到瞬时事务并失败。
- `_activeActionChoices`、`_cardExecutionScopeDepth/_activeCardExecutionDeaths` 没有被复制，也没有被禁止，直接 Fork 会静默丢失选择游标或死亡去重集合。
- `PenNibPredictionState.AttackToDouble`、`VambracePredictionState.TriggeringCard`、`CurlUpPredictionState.PlayedCard` 是动作内引用。它们不属于稳定状态，也未进入稳定指纹；旧 Fork 会原样复制这些引用。

### 状态 schema

| 状态类别 | Fork 规则 |
|---|---|
| 持久战斗状态、回合计数、牌堆、Power、RNG | 克隆并保持父子独立 |
| trace、动作选择、出牌/攻击/Power 结算、成对 Hook 游标 | 必须完成后才能 Fork |
| 待解决选择与延迟历史 | 作为搜索边界处理，不得 Fork |
| 仅用于最终路线注解的遗物触发记录器 | 注解期间独占，不得 Fork |

### 已实现并验证

- 新增 `IPredictionForkBoundary`，模拟器在分配克隆前依次预检 combat state、StateStore 和 history；各私有状态的 Fork 复用同一断言。
- 补齐动作选择、出牌执行、钢笔尖、臂铠和蜷身边界；臂铠在对应 AfterCardPlayed 后清除动作内引用。
- 定向 Fork runId `c2eaca32dedc4280ac94c8b7dee6cc6a`、配对出牌死亡 `2fc6f92950f14078b7cef48d5d5d767c`、钢笔尖增量 `c835e618bf9d49c9860efac6392b22c4`、遗物 Hook 11/11 `e38faf739afc49ffb787de83ecb26a70`、Power 卡牌 Hook 12/12 `9ef90837a332410fa0811801c13d9ab1` 全部通过。
- 双小啃兽增量 runId `f72e7839614e46b098c3ea7ccad20527` 第 5 回合、两次洗牌、0 药、0 战损，`UnexpectedReplans=0`。

## 第四块：主线程搜索根与后台惰性读取

### 已确认

- `NetFullCombatState` 在仓库中只有问题包导出序列化，没有可验证的搜索态导入器，不能充当原生深拷贝。
- 旧 Beam 每次根回放都在 worker 执行 `new SimulatedCombatState(state)`；`CombatPredictionState`、`SimPlayerCombatState`、卡牌 Preview、Power、Hook listener 和 `CombatManager.History` 均含首次访问时的 live 读取。
- 旧 Fork 还会先调用 live 构造函数再用对象初始化器覆盖 roster/round 字段；`SimCreatureState.Fork` 同样先读 live HP/格挡再覆盖。
- 外部审计提出的是分阶段迁移，不支持用仅持有 `CombatState` 的包装类型冒充不可变快照。

### 本轮决定

1. 搜索入口在主线程构建并验证 `CombatRootSnapshot`；Beam 与 Coordinator 不再接受 live `CombatState`。
2. 根对象预先物化当前基础影子状态、Power/listener、怪物状态、遗物/药水计数和当前战斗历史；worker 只从根模拟器 Fork。
3. 捕获前后同时比较 `LiveCombatStamp` 与 `ContinuationStamp`，并比较 live 与根投影的完整 continuation 文本；捕获期间变化或投影差异直接失败。
4. 本轮不宣称已经克隆整棵游戏对象图。`IRunState`、Player/Creature/Monster/Relic identity、模型工厂相关元数据和 `ContainsCard` 仍需下一段迁移。

### 已实现并验证

- `CombatRootSnapshot` 只允许主线程捕获，并记录耗时、卡牌/Power/listener 数量。正式搜索和首回合搜索均在调度 worker 前捕获；主搜索、禁药反事实和限一瓶审计复用同一根。
- 五个牌堆的所有 `PredictedCard` Preview、球、生物数值、Power overlay、怪物 AI/私有状态、遗物计数、药水槽、九条 RNG 与当前历史条目已在主线程物化。根 Fork 不再执行 live `SimulatedCombatState`/`SimCreatureState` 构造，也不重新枚举 live Hook listeners 或全局战斗历史。
- 定向 runId `091c600ef65b47d79dac265d1bd7054d` 证明后台禁止捕获，且捕获后把实机能量加一，worker Fork 仍保持捕获值并与根 continuation 逐项一致。
- 钢笔尖增量路线 `589a0ec232044d6996c268a7a64c881a` 第 1 回合结束、双倍攻击标注和部署恢复通过。双小啃兽普通 `f5ccd0953e9c4fabb277aaa4cc2f5334` 与最终增量 `695323f49a9f4b2d94312db1faa6fb3e` 均第 5 回合、两次洗牌、0 药、0 战损并成功复用。
- 结构检查通过，当前 Search 文件数为 44。

### 第二阶段：模型清单与 listener 脱离 live

- Beam 仍直接读取 `_player.PotionSlots`、`_player.Relics` 和玩家最大生命；`SimulatedCombatState` 的玩家回合、金币、药水、Power 缺省、`ContainsCard`、Osty 最大生命、Run RNG 和部分怪物私有字段仍有 worker fallback。
- 这些路径现统一改用根值：玩家回合/金币/药水槽、初始 Power 数值、完整 `_allCards` 注册、Osty 数值、Run RNG 序列化快照、幕/房间/单人卡池约束和已知怪物私有字段都在主线程捕获。根怪物私有字段物化完成后，缺项直接失败。
- Relic、Potion 和牌堆 Card listener 改为根克隆；卡牌变更通过 `PredictedCard` observer 使 listener cache 失效。Relic 克隆会运行原模型 `AfterCloned`，首次 11 项严格差分因此真实暴露钢笔尖 `AttacksPlayed=9` 被重置为 `0`。修复不是复制某个字段：`RelicPredictionStateSupport.CaptureRootState` 从 live 原件一次性迁移全部 25 类受跟踪状态到克隆 key，随后清除 source 映射。
- `ContainsCard` 已按原版 `_allCards` 语义拆成根 floating card identity 与分支 `PredictedCard` 注册表；卡牌进入/移出战斗和 Fork 都同步维护，不再委托 live `CombatState`。
- 最终根隔离 runId `9f1a11e8be0749268419d73eb500c41d`、钢笔尖增量 `d7f95d11b2934c7399658d0932a6c992`、Knowledge Demon `0526ed3711604287a692d035c20f11e6`、Smart 救命药 `ee3907732ea147088387209c1fa178ae`、11 项遗物 Hook `f642afb8250c44ab8b2f8399714b5b06` 全部通过。双小啃兽最终增量 `bfb8b3fa03ca4c39a2b8902857f10da1` 第 5 回合、两次洗牌、0 药、0 战损、零非预期重算。
- 仍保留的 `IRunState`、Player/Creature/Monster identity 和 Monster move graph 引用不在本阶段冒充深拷贝；下一段先证明具体可变读取，再迁移对应字段。

### 第三阶段：运行级快照与 Hook 前缀

- worker 路径对 `IRunState` 的具体读取已逐点核对。当前幕、房间类型、地图坐标、多人卡池约束、起始玩家回合和九条战斗 RNG 均在主线程捕获；回合抽牌修正改用根回合与根 Relic 清单。
- 原版 `IRunState.IterateHookListeners(combat)` 的结构是“牌组 Card/Enchantment 与 run 订阅者前缀 + combat listener 后缀”。根捕获会验证这个后缀逐项保持同一身份，再复制前缀中的牌组 Card/Enchantment；worker 的伤害、失血、死亡、药水和其他 run hook 只组合该根前缀与当前分支 combat listener。
- 卡牌解锁池和战斗生成筛选显式接收捕获的 `CardMultiplayerConstraint`。预测 RNG 从序列化根恢复；Beam 威胁投影与 Thrash 伤害也改走分支 Hook mirror，不再把 live `RunState` 作为原版 Hook 的枚举入口。
- 根快照定向 runId `a8b69594be7d4066a21e46a4f4849ec4`、攻击药生成差分 `a3b2629aef3b4459b417a8af38c76939` 通过。双小啃兽增量整战 `a12d3a1d4d8b466a8b1652eee23a85b8` 第 5 回合、两次洗牌、0 药、0 战损，`UnexpectedReplans=0`。CoverageCatalog 3035 项全门禁与 44 个 Search 文件结构检查通过。
- 这一步关闭的是 worker 的 `IRunState` 可变读取，不等于整棵对象图无 live identity；Monster、Modifier、mod subscriber 与 Player/Creature 稳定身份进入下一段清单审计。

### 第四阶段：Monster/Modifier listener identity

- 原版共有 16 类 Modifier。`Hoarder` 的 `_cardsToSkip` 会变化，但相关 Hook 只处理跑局 Deck 变更；`Murderous.AfterCreatureAddedToCombat` 是本轮需要保留的战斗语义。根快照现复制 Modifier 并替换 Hook 前缀中的原件；预测召唤敌人时显式应用 Murderous 的 3 力量。
- 已登记的精确 Monster Hook 只有永世沙漏的生成牌与女王死亡监听。永世沙漏原先从 live `WitherUpgradeCount` 读战斗中可变值，现经 `ICombatPredictionMonsterStateSink` 读取分支计数；女王只用 `Creature` 稳定身份关联分支怪物，没有复制整套行动图。
- 召唤入场的贤者之石与毛皮大衣原先重新枚举 `player.Relics`，现只遍历根 Relic 克隆。结构门禁固定禁止这三条 live 回读复发，并要求 Modifier 克隆和统一根物化入口存在。
- 首次永世沙漏两步差分 `2f2928b9533f445e9cb8bcfa340ecf02` 在第二步失败，预测力量为 4、实机为 7。根因不是生产 `CombatRootSnapshot`，而是直接测试 API 构造 `SimulatedCombatState` 与模拟器后未调用根物化，已有 Power 留在半物化状态。模拟器构造现通过 `ICombatPredictionRootMaterializable` 统一物化；生产入口删除重复调用。
- 最终 0.13.42 DLL 的永世沙漏两步差分 `7360f01e3e3f427a9373df736192c646`、Murderous 根 Modifier 克隆与 Fabricator 召唤 `a80224403eea47e48375581ed34fe376` 均通过。双小啃兽增量 `bbebff8bc2314752aa4d3502bf7f8bcf` 第 5 回合、两次洗牌、0 药、0 战损，`UnexpectedReplans=0`；CoverageCatalog 3035 项全门禁与结构检查通过。
- 本阶段只关闭原版 Monster/Modifier listener 中已证实的可变读取。mod subscriber、Player/Creature/Monster 稳定 identity 与只读运行配置引用尚未完成，因此不能把第四轮标记成“worker 无 live 对象”。

### 第五阶段：原版 Badge/缩放与第三方 subscriber 分界

- `CombatState.IterateHookListeners()` 在标准战斗模型后追加 Badge、`MultiplayerScalingModel` 和 `ModHelper` combat subscriber；`RunState.IterateHookListeners(combat)` 还会在 combat 后缀前追加 run subscriber。旧根只识别牌组前缀与 combat 后缀，未区分这些来源。
- 原版 BadgeModel 只有 `CccComboModel` 与 `DebufferModel`：前者有每回合出牌计数，后者写进度统计。它们现在以根克隆进入 listener 列表；求解器仍忽略纯成就/进度写入，不让 live Badge 承担分支状态。
- `MultiplayerScalingModel` 的私有字段直接引用 live `RunState` 和 `CombatState`。项目只支持单人战斗，因此根克隆会清空这两个引用；`ModifyBlockMultiplicative` 的单人专用 mirror 精确返回 1，遇到多人玩家数直接失败。根快照测试同时验证引用脱离。
- “worker 不持有任何 live 对象”不是正确完成条件，也与项目规则不一致。Player/Creature/Monster/Encounter 可作为稳定 identity、类型或只读模型元数据；需要逐字段证明的是后台没有读取会随实机推进变化的值。
- 本机安装目录的程序集扫描确认：Loadout `0.4.7` 注册最大手牌、开战效果与伴侣 run/combat 单例；BaseLib `3.4.5` 注册一个从 live 牌堆枚举 CardModifier 的 combat subscriber。通用 `MemberwiseClone` 不能重建这些所有权：BaseLib Modifier 必须关联预测卡牌 Preview 上由 CopyOnClone 产生的克隆，Loadout 全局状态也要按具体 Hook 捕获。第三方 subscriber 因此保留为下一批专用适配，不在本阶段宣称关闭。
- 最终 0.13.43 DLL 的根隔离与双小啃兽增量整战 `8e380d6cdb0c4f54abb312f3afdb8393` 通过：第 5 回合、两次洗牌、0 药、0 战损，`UnexpectedReplans=0`；CoverageCatalog 3035 项全门禁与结构检查通过。

### 第六阶段：第三方 subscriber 第一批实现

- `ModHelper.IterateAllRunStateSubscribers` 与 `IterateAllCombatStateSubscribers` 现在在主线程单独枚举。捕获验证它们分别位于 run 前缀和 combat 列表尾部，再从 worker listener 中移除；非 gameplay 或只在开战前生效的模型不进入预测，未知 gameplay subscriber 抛出 `PredictionUnsupportedException`。
- Loadout 的 `LoadoutMaxHandSizeModifier` 通过其 BaseLib 接口在根捕获时计算每名玩家的最大手牌；两个模拟入手牌入口不再硬编码 `CardPile.MaxCardsInHand`。BaseLib CardModifier 由 live Owner 卡牌登记，`PredictionUtils.CloneCardStateForSimulation` 复制 Modifier 私有状态、重绑预测 Owner 并调用 `AfterClonedOnCard`；listener 从当前分支 Preview 恢复。没有 Modifier 的战斗通过弱身份索引直接跳过反射路径。
- 完整 Mod 栈第一次运行 `07f3093e18044030821d7f6a260ce080` 没有在 subscriber 边界失败，而是在第 8 回合 Knife Trap 嵌套自动出牌进入第二层 `ApplyCardPowers` 时暴露 `_powerCardSource` 单槽限制。该事务现改为卡牌源栈；Unsettling Lamp 使用 `lamp → triggering card` 映射，使内层完成只核销属于内层卡牌的触发。自动出牌差分 `d71206dca40347acb4a474d4dbab4edc` 与升级双 Shiv `8164a323631f467bb4e6e2afdb359f08` 通过。
- 根隔离与双小啃兽增量整战 `9d431748477e49969df981103321d551` 通过，第 5 回合、两次洗牌、0 药、0 战损、零计划外重算。
- 完整 Mod 栈 `7a0ef678883141d292c04b7eb760cc20` 实际捕获 1 个 run subscriber、1 个 combat subscriber、0 个 BaseLib CardModifier，并能够完成搜索、返回胜利路线；但可见机甲仍在搜索中途退出 No-GC 区，产生 709.2 ms GC 暂停。两次试改 LOH 子预算没有消除问题，均已撤回。第三方 subscriber 适配因此只完成第一批代码与语义验证，不发布版本；实际 CardModifier 夹具和搜索分配根因进入下一小批。

### 第七阶段：Orb listener 与稳定 identity 第一批

- `CombatState.IterateHookListeners()` 会直接包含实机 `OrbModel`。旧根过滤了卡牌、附魔和 affliction，却保留了球实例；分支激发、移除或生成 Orb 后，Hook 列表仍可能使用根实例。根捕获现在排除 Orb，`CombatPredictionState` 附着到分支战斗后，由当前 `SimOrbQueue` 重新登记；队列容量和内容变化统一使 listener 缓存失效。
- 字段级搜索确认了六处后台可变读取：Ambergris/Blood Potion、瓶中精灵/Lizard Tail 和强制死亡条件读取 live `Creature.MaxHp`；End of Days 的修书小刀补偿读取 live `player.Relics` 与 `creature.Powers`；Bound Phylactery 读取 live 遗物清单；Entropic Brew 读取 live 药水槽容量。它们分别迁移到 `SimCreatureState`、根 Relic 克隆、分支 Power 列表和根玩家限制。
- End of Days 删除了重复的 Doom/修书小刀实现，统一调用已有 `DoomKill`，在死亡前从分支 Power 计算 fatal 计数，再按根遗物克隆结算。
- Orb 根隔离与 Synchronize 的 Orb 变化严格差分通过；Doom/修书小刀、Ambergris、瓶中精灵和 Entropic Brew 的实机差分通过。第一次 Doom 用例未注入修书小刀，实际 50 HP 与夹具预期 53 HP 不符，补齐遗物后通过，该次不计为代码失败。
- 临时 Bound Phylactery 用例中预测与实机都保持 Osty 5 HP，但额外的“应为 6 HP”断言失败，说明这个临时夹具没有执行目标 Late 钩子；本批只把它记为差分一致、专用触发证据待补，不登记为该 Hook 的新实机通过。
- Release 编译为零警告、零错误，结构边界检查通过。Player/Creature/Monster/Encounter identity 审计仍未结束，完整 Mod 栈的 No-GC 中途退出也仍为待处理项。

### 第八阶段：Monster 行动图与 Encounter 槽位

- 根怪物的 `MoveState` 拓扑本身可作为稳定只读图，但其中的 `AttackIntent.DamageCalc`、`RandomBranchState` 权重和 `ConditionalBranchState` 条件是闭包，可能回读 live Monster、Creature 或 CombatState。根捕获现在把每个行动的基础攻击/重复次数、随机分支基础权重和静态条件选择固化到 `BranchMonsterStaticSnapshot`；worker 只使用该快照和分支自己的状态日志/RNG。
- Two-Tailed Rat 的可召唤权重、Test Subject 多段次数、The Forgotten 的敏捷伤害和 Waterfall Giant 压力枪仍使用已有分支动态补偿，不把根值误当成未来固定值。Living Shield 的存活队友条件改读分支 roster；Bowlbug Rock 的全格挡眩晕继续由伤害结算直接建立分支强制行动。未知 `MonsterState` 子类不再调用原生委托，直接报告预测不支持。
- Encounter 的 `Slots` 在主线程复制；召唤取首/末空槽、Two-Tailed Rat 可召唤判断和敌人排序都使用根槽位数组。live Encounter identity 仍保留给 `ICombatState` 和单人倍率 API，但 worker 不再调用其槽位虚方法。
- Bowlbug Rock 三步眩晕状态机、Living Shield/普通攻击模板 13 项、The Forgotten/Queen 等动态伤害与行动 14 项均通过实机差分。Two-Tailed Rat 原夹具在当前临时战斗布置下，原版实际滚到 `SCREECH_MOVE` 而非文件中的 `SCRATCH_MOVE`，因此原断言不计入结果；按同一原版结果改为 `SCREECH_MOVE` 后，预测与实机差分通过。
- 根怪物 AI 和意图在物化后若缺项会直接失败，不再回退读取 live Monster。`MonsterMoveEffects` 中的类型常量/少量私有成员仍需下一批逐项捕获，因此稳定 identity 审计保持未完成。

### 第九阶段：Monster 行动参数与根惰性构造封口

- `MonsterMoveEffects` 原先在 worker 对 35 类 Monster 执行 52 次按名反射读取。多数是难度修正后的行动参数，但 Axebot `RespawnCount` 等值取决于该怪物实例生成时的配置，不能改成全局常量。新增按类型的明确成员清单，在 live 根捕获或预测怪物创建完成时一次读取，并随 `BranchMonsterStaticSnapshot` 进入各分支；行动执行只调用 `GetMonsterStaticInt`。
- Waterfall Giant 压力伤害、Aeonglass 递增力量、Test Subject 多爪次数等会继续变化的量仍由原有分支计数器叠加；静态快照只承担基值。未知成员缺失直接失败，不增加默认值。
- `CombatPredictionState` 创建 `SimCreatureState` / `SimPlayerCombatState` 前经过根捕获边界。根物化完成后，已有玩家或根生物若没有影子状态会直接失败；只有 `CombatState` 属于当前预测的新增怪物可以建立新的生物状态。Monster AI 与意图已有同样的缺项边界。
- Axebot/Aeonglass 等 12 项、Living Shield/Myte/Nibbit 等 13 项、The Forgotten/Queen 等 14 项、Waterfall Giant 等 9 项实机差分通过；Defect 根快照与 Fabricator 预测召唤也通过。Fabricator 首次用默认无槽位遭遇运行时，原版实机因房间槽位为空拒绝召唤；改用有槽位遭遇后通过，该次属于夹具前提错误。
- Release 编译零警告、零错误，结构边界检查禁止 `MonsterMoveEffects` 恢复 live 反射读取。至此 Player/Creature/Monster/Encounter 稳定 identity 字段审计完成；第三方实际 CardModifier 夹具与完整 Mod 栈 GC 卡顿仍是独立未完成项。

### 第十阶段：完整 Mod 栈分配剖析

- 三次 Steam 可见机甲基准都完成搜索并返回路线，但最大 GC 暂停分别约为 `584.6 / 555.7 / 502.3 ms`，均未通过 `50 ms` 门槛。原始样本的 worker 分配约 `4.98 GB`，No-GC 区在搜索中途退出；因此本项仍是性能失败，不登记为通过。
- EventPipe 有效追踪覆盖约 `4.50 GB` 采样分配，其中小对象约 `4.48 GB`、大对象约 `20.9 MB`。打断 No-GC 区的是 `InducedLowMemory`，随后出现密集 `AllocSmall` 回收；这证明继续增大 LOH 子预算没有依据，前两次 LOH 比例试改保持撤回。
- 最大外部分配来自 RitsuLib 无 capability 卡牌仍执行的属性贡献管线：`ApplyCardType`、`ApplyFirstOverride` 及其委托/迭代器合计占数百 MB。最大内部热点是 Hook listener 重建：`GetBaseHookListeners`、`GetEffectiveHookListeners` 与 `EffectivePowers` 反复物化 `AbstractModel[]`、`PowerModel[]`；Fork、牌堆 Fork 和快照仍是次级热点。
- 优化按可单独验证的两批推进：先在求解隔离域内为空 capability 卡牌增加 Ritsu 属性快通道，存在 capability 或默认 capability 来源时完整保留 Ritsu 原逻辑；再拆分卡牌 listener 拓扑变化与普通卡牌数值变化的缓存失效。每批都先跑语义/结构门禁，再用相同可见机甲夹具比较分配、No-GC 状态和最大暂停。
- Ritsu 空 capability 快通道把相同 `33,432` 次转移的 worker 分配从约 `4.98 GB` 降至 `3.71 GB`，其中 snapshot 分配从约 `1.63 GB` 降至 `431 MB`。该批单独运行仍因 `InducedLowMemory` 出现 `636.1 ms` 暂停，因此只登记为有效降分配，不提前登记性能通过。
- Fork 现在继承已经物化且仍有效的 listener/Power 视图：没有对象重映射时共享只读缓存，有 Power/Orb 克隆时只复制并替换变化项。球队列变化统一使基础与有效 listener 缓存失效，修正了旧实现只清有效缓存、可能保留旧 Orb listener 的遗漏。Fork 边界和 Lightning/Frost Orb 差分通过。
- 最终相同完整 Mod 栈机甲连续两次独立进程通过：首搜分别为 `12.24 s / 3.57 GB / 0 ms GC / 8.6 ms 最大帧` 与 `12.19 s / 3.57 GB / 0 ms GC / 16.5 ms 最大帧`；均保持第 5 回合胜利、相同展开/转移/选牌分支和预计战损。附加 EventPipe 的一次运行受 profiler 负载影响出现 `475.2 ms` 主线程间隔，不计作无追踪性能样本。
- 实际 CardModifier 夹具在完整 Mod 栈动态建立一个无副作用 BaseLib 子类并挂到实机卡牌。根捕获识别 subscriber，预测 listener 使用独立 Modifier 克隆并保留 `Amount`，Owner 重绑当前 Preview；Fork 后修改卡牌触发 Preview 与 Modifier 再克隆，旧 listener 不再复用。带该夹具的最终可见机甲仍为 `11.87 s / 3.57 GB / 0 ms GC / 13.5 ms 最大帧` 并通过整战。
- Ritsu 反向夹具同时给预测牌临时挂接一个会改写 CardType 的真实 `IModelCapability`：空集合命中快通道，挂接后 `CanSkip=false` 且 Ritsu 原属性贡献生效。第一次夹具使用公开 `Apply`，因隔离 headless 没有注册 capability 持久化而被 Ritsu 正确拒绝；改成测试域内临时挂接、不写保存状态后通过，该次不计为生产失败。

## 第五块：CombatBeamSolver 阶段边界

### 第十一阶段：纯拆分核验

- 外部审计没有定位到 `CombatBeamSolver` 声明，报告为 0 行、0 方法、0 字段；该统计不能作为迁移清单。当前源码实际为单个约 4,133 行文件，包含 94 个具名方法，主 `Solve` 从约第 164 行延续到第 810 行。
- 当前连续职责边界为：类型字段/私有模型、`Solve` 阶段循环、Beam 保留/最终候选排序、终局逐回合标注、动作与回放展开、continuation 构造、快照/威胁/指纹特征、候选分类/转置/目标枚举。部分职责在文件尾交错，不能只按连续行号冒充正确分层。
- 第一批只移动现有成员到 `Entry/Models/Phases/Expansion/Retention/Terminal/StateEvaluation` partial 文件。预算与进度目前是 `Solve` 内局部状态，保留在 Phases；字段所有权、run-local context、策略类型和 scratch 复用不与纯移动混合。
- 纯移动完成条件是固定 Beam 场景的路线、expanded、transitions、choice branches、支配/转置/重复状态剪枝、分配与搜索时间逐项比较；其中路线与计数必须精确一致，性能只允许测量噪声，不接受行为近似。
- 七个 partial 文件已经按上述边界生成，主文件只保留 primary constructor 参数和实例字段；所有方法体仍在同一类型内。结构门禁枚举固定文件集，在全部分片继续检查宽泛异常与 live 根回读，并要求 `Solve/Expand/RankFinal/AnnotateTurnOutcomes/Snapshot` 位于对应阶段文件。
- 固定机甲完整 headless 在第 5 回合胜利，完整动作序列与拆分前一致；`expanded=4624`、`transitions=33432`、`choice_branches=17735`、`dominance_pruned=214`、`transposition_pruned=700`、`repeatable_no_progress_pruned=0` 均精确一致。运行值为 `11.35 s / 3.55 GB / 0 ms GC / 17.2 ms 最大帧`，零非预期重算。
- Defect Lightning/Frost + Synchronize 严格实际/模拟差分通过。至此只证明文件移动等价；SearchRunContext、scratch 复用以及 Beam/终局排序类型仍未开始。

### 第十二阶段：SearchRunContext 所有权

- 原 solver 的 15 个计数器、性能统计、帧压力节流器、两张转置表、stand-pat/威胁/coverage 缓存和路由诊断集合都属于一次 `Solve`，但此前与根配置字段并列，无法从类型上区分生命周期。
- 新增具体 `SearchRunContext`，由 solver 构造时一次创建并持有上述状态。根快照、profile、玩家/敌人基线、药水与偷窃政策仍是 solver 的不可变配置；本批不把它们混入可变 context。
- 本次只替换所有权访问，不清空/复用集合，不建立对象池，不移动 Solve 局部 Beam/frontier scratch，也不修改任意比较器或预算判断。结构门禁禁止四类代表性旧字段返回主类型。
- 固定机甲完整 headless 再次保持第 5 回合、相同动作序列、`4624/33432/17735` 展开/转移/选牌分支和全部剪枝计数；本轮为 `11.52 s / 3.55 GB / 0 ms GC / 17.4 ms 最大帧`，零非预期重算。

### 第十三阶段：Beam 保路与终局排序分界

- `RankBest` 原先同时承担状态去重、Beam 分数排序、选牌路由通道、攻防/铺垫/资源代表、药水配额和小型 Pareto 保留；`Solve` 后半则另有生存/胜利/药水/卖血/边界的终局政策排序。两者虽然都比较节点，但目标不同，不能继续用“总分”概念混称。
- 第一小批把 `RankBest/RankFinal` 及其保留通道、比较器和诊断整体移入具体 `BeamRetentionPolicy`。该类型只接收捕获后的 profile、Boss/敌人数、药水/偷窃政策、诊断出口、`SearchRunContext` 和 stand-pat 评估委托；它不读取全局设置，也不执行除显式 stand-pat 委托之外的模拟。
- `Prune` 和阶段循环只通过 `Retention.RankBest/RankFinal` 进入保留政策；结构门禁要求旧 `RankBest` 不得返回 facade。固定机甲仍为第 5 回合、同动作序列和精确相同的 `4624/33432/17735` 及全部剪枝计数，本轮 `11.64 s / 3.55 GB / 0 ms GC / 17.1 ms 最大帧`，零重算。
- 第二小批新增只读 `SearchFeatures`，把终局候选依赖的节点/快照标量收成明确输入；`FinalPlanOrdering` 独立拥有胜负、药水、偷窃、卖血和边界排序，并只返回选中候选与三项药水统计。`Solve` 不再含 `PotionUsePolicy` 比较器和药水无使用基线实现。
- 切换前，旧排序和新排序在钢笔尖增量场景对同一候选集合逐字段比较选中节点、得分、卖血与药水统计，结果一致；随后删除旧实现并切换正式调用。结构门禁固定九个 partial 文件，并阻止终局排序回流到 `Phases`。
- 固定机甲正式路径仍在第 5 回合以相同动作序列结束；`expanded=4624`、`transitions=33432`、`choice_branches=17735`、`dominance_pruned=214`、`transposition_pruned=700`、`repeatable_no_progress_pruned=0` 精确一致。本轮为 `11.51 s / 3.55 GB / 0 ms GC / 18.8 ms 最大帧`，零非预期重算。
- Disabled、Smart 和 RequireAtLeastOne 分别得到 `0/0/1` 瓶路线，至少一瓶场景实际使用弱化药后首回合结束；固定防御牌组保持主动卖血 `5/5` 上限并剪除超预算路线。第五轮第 5 项完成。
- 原双小啃兽长线双路径请求未进入终局排序，在第 4 回合分支回放时因“小啃兽根 AI 状态未捕获”失败；该失败未作为排序证据。第十七阶段已定位并关闭这条独立根状态问题。

## 第六块：剩余编排与单一事实源

### 第十四阶段：无人值守流程边界

- 当前 `UnattendedTestRunner.cs` 为 2,275 行；`RunAsync` 从请求解析后的游戏启动一路处理跑局建立、战斗注入、差分/全自动执行、最终断言、清理和结果写入。另有 12 个按历史功能增长的 partial 文件，但它们仍共享 runner 的全部私有状态，没有形成编排边界。
- 进程级请求循环、每请求测试开关、计划状态漂移和场景实例字段位于同一类型；三处 Passed/Failed 结果构造重复采集内存与协议字段。`Fixtures` 已经承担多数注入原语，`SolverPolicy` 已经承担首轮结果断言，这些现有分片可以保留，不需要重写夹具语义。
- 本轮按 `ProtocolHost → ScenarioBuilder → Executor → Assertions → Writer` 推进。每一小批先移动所有权并保持请求 JSON、stage 名、完成检查文本和失败语义不变；不借编排重构调整任何战斗夹具或搜索政策。
- 第一小批建立具体 `ProtocolHost`，独占请求文件循环、协议校验、每请求搜索开关、计划状态漂移与复位。`UnattendedTestRunner` 只保留静态只读 facade，Runtime/Controller 的既有调用不变。两个请求在同一 headless PID 连续通过，第二个请求命中进程复用，最后正常退出。
- 第二小批建立 `Writer`，统一 Passed/Held/Failed 的协议字段、内存快照和临时文件原子替换。测试在同一进程依次得到成功结果、故意回合断言失败的完整 Failed 结果，以及失败后恢复成功的结果；错误文本、失败阶段和请求复用均保持原语义。
- 第三小批建立 `ScenarioBuilder` 与 `ScenarioContext`。建局、跑局快照、进入遭遇及怪物/生命/牌堆/球/药水/遗物/Power/RNG 注入由 builder 独占；Runner 只接收角色、遭遇、战斗、玩家、起始回合和三类执行清单。builder 同时保留中途战斗与回合，使注入失败仍能写出真实失败现场。
- Defect Lightning/Frost + Synchronize 两项严格差分通过；故意传入错误逐敌生命数量时，Failed 结果保持 `stage=inject_state`、起止均为第 1 回合，随后同一进程恢复请求通过并退出。
- 第四小批建立 `Assertions`，执行前的失败边界、策略快照、控制器会话、Fork、根快照、CardModifier 和问题包检查，以及执行后的回合、生命、出牌、用药和 Power 断言不再散落在 `RunAsync`。根快照、指定卡实际打出和首回合结束在同一无人场景通过。
- 第五小批建立 `Executor`，独占 Orb/药水/怪物差分分派、临时设置覆盖、全自动启动、搜索/部署等待和提前停止条件；临时设置由 Executor 记录并在请求结束统一恢复。Held 不再从执行深处直接写结果，而是作为显式 `ExecutionOutcome` 返回主编排。
- 双球两项严格差分通过；同一进程随后以 RequireAtLeastOne 实际使用弱化药并打出指定卡首回合结束，部署速度恢复为 Normal。独立 Held 请求写出 `stage=initial_search_held`、保留战斗并由释放信号正常结束。至此第六轮第 1 项完成。

### 第十五阶段：Overlay 一次性快照

- 旧 `SolverOverlay.ShowResult` 在主线程渲染期间直接遍历 `SolverResult.BestNode.Actions`，读取 Forecast、逐回合字典、药水/卖血统计与搜索性能字段；`SolverRouteRow` 和 `SolverActionPill` 继续接收 `PlanAction`，动作胶囊还会在创建控件时查询 `ModelDb`。这使 UI 的读取时机与搜索结果对象的可变生命周期绑在一起。
- 新增只读 `SolverOverlaySnapshot`、`SolverOverlayTurnSnapshot` 和 `SolverOverlayActionSnapshot`。控制器把搜索结果交给 UI 前一次性复制状态、概览、详情、逐回合结局、选牌文字、动作标题/目标、遗物标签、击杀、tooltip 和视觉类别；Renderer 只消费这些快照或部署所需标量。
- `SolverOverlay.cs`、`SolverRouteRow.cs`、`SolverActionPill.cs` 已不再引用 `SolverResult`、`PlanAction`、`PlanCardChoice` 或 `ModelDb`。结构门禁固定快照入口与三个 Renderer 签名，并阻止这些可变搜索类型回流。
- `SOLVER-OVERLAY-SNAPSHOT-547` 使用钢笔尖两动作击杀路线完成真实搜索与全自动部署：首轮两张可执行牌、`PEN_NIB:×2` 标注、0 战损、第 1 回合结束，日志依次出现 `UI_STATE state=ready`、`state=deploying`、两次 `DEPLOY_ACTION`、`deployment_complete` 和速度恢复；同一 runId 状态为 Passed。人工布局、字体与拖动检查仍保留在原 UI 人工测试项，不借本次结构重构宣称通过。

### 第十六阶段：Mirror registry 支持元数据单一来源

- 旧 CoverageCatalog 先扫描 mirror 静态字段，再按私有字段名反射 `_registrations`、`_strictInferrer`、`_inferrer` 和 `MirrorMethodSpec`，并自行推断 AllowInference。Registry 内部重命名或策略变化时，目录工具可能静默漏掉支持信息。
- `MethodMirrorRegistry` 的 action/result 两种实现现在统一提供 `MethodMirrorRegistryDescriptor`：基础方法、receiver 类型、显式 Handled/Ignored 登记及当前生效的严格/尽力 inferrer 都由 registry 自身一次性描述。CoverageCatalog 只发现 descriptor provider 并消费该稳定元数据，不再读取 registry 私有布局。
- 切换前后分别从当前 Release DLL 运行全部 CoverageCatalog verify 开关。两次均为 3035 项、未分析/待实现/运行证据缺口/原生重扫/分支 live 读取/状态字段与状态写入缺口全为 0，22 项仍是已知 replay horizon 外快照写入，115 项仍是静态行动图构造；所有生成文件逐项不变。
- 结构门禁要求两类 registry 实现 descriptor provider、CoverageCatalog 使用 `DescribeMirrorSupport`，并禁止三个私有字段名反射回流。`MIRROR-REGISTRY-DESCRIPTOR-548` 随后以钢笔尖 Hook 路线完成严格增量搜索和真实部署：两张牌、第 1 回合击杀、`PEN_NIB:×2`、0 战损、速度恢复，结果 Passed。此次只移动支持元数据所有权，不新增分支状态，不改变 Fork、指纹、续用或实际 mirror 派发。

### 第十七阶段：已移除怪物的行动尾部状态

- 双小啃兽长线失败可稳定复现于第 4 回合 `EndTurn` 回放。两只根怪物的 AI/静态参数已在主线程完整捕获；缺失发生在后续分支生命周期，不是捕获遗漏。
- `CombatPredictionState.RemoveCreature` 会在死亡结算时从活动 roster 移除怪物，旧 `SimulatedCombatState.RemoveCreature` 同时删除 `_monsterAiStates`。怪物可能在自己的攻击或同一敌方阶段中死亡，而该行动后半仍需按原版顺序读取 `SLICE_MOVE` 的静态格挡等参数；提前删除使同一行动变成半结算状态。
- Monster AI/静态参数现在跟随 `KnownEnemies` 和整个分支生命周期保留，活动 roster 移除只影响可行动/可选目标与 Hook listener。新增结构门禁禁止在 `RemoveCreature` 恢复 AI 删除；没有新增默认值或 live 回读，Fork 继续复制同一已捕获字典，AI 已在指纹与 continuation 中按活动敌人记录。
- `REFACTOR-FINAL-NIBBITS-549` 在原失败夹具上重新通过：根隔离与增量验证开启，第 5 回合结束、两次洗牌、0 药、0 战损、逐回合精确复用、`UnexpectedReplans=0`。全量 CoverageCatalog 3035 项与全部有效支持门禁继续通过。

### 第十八阶段：最终提交门禁

- 从当前提交重新构建 Release，结果为 0 警告、0 错误；结构门禁通过并覆盖 52 个 Search 文件。CoverageCatalog 3035 项的未分析、待实现、缺证据、NativeAutoRescan、live 分支读取、状态字段和必需状态写入缺口均为 0；22 项 replay horizon 外快照写入、115 项静态行动图构造器以及 85/19/51 个选牌/自动出牌/阵容来源计数保持不变。
- 首次机甲最终请求错误地同时开启 `VerifyIncrementalSearch` 和性能门槛。该诊断会逐转移执行完整前缀回放，实际在 19150 次转移中分配 34.4 GB、耗时 100.4 秒并耗尽 No-GC 区；请求按 25 秒上限在 `assert_initial_solver_result` 正确失败，不作为生产性能证据。第二次生产请求使用 Medium 预设，性能为 `7.09 s / 2.02 GB / 0 ms GC / 17.3 ms`，但路线第 7 回合结束，与 High 基线不同，因此也按精确回合断言失败。
- `REFACTOR-FINAL-MECHA-HIGH-550` 恢复原 High 固定基准并通过：第 5 回合结束，第 2-5 回合均按完整状态文本复用；`expanded=4624`、`transitions=33432`、`choice_branches=17735`、`dominance_pruned=214`、`transposition_pruned=700`、`repeatable_no_progress_pruned=0`。本轮为 `11.45 s / 3.55 GB / 0 ms GC / 17.6 ms 最大帧`，`UnexpectedReplans=0`。
- `REFACTOR-FINAL-NIBBITS-551` 从同一最终 Release DLL 开启根快照和增量/完整回放核对并通过：第 5 回合结束、两次洗牌、0 药、0 战损、逐回合精确复用，`UnexpectedReplans=0`；首轮为 `6.21 s / 2.47 GB / 0 ms GC / 17.2 ms 最大帧`。本轮没有提升版本号、生成发布 ZIP 或部署到玩家游戏目录。
