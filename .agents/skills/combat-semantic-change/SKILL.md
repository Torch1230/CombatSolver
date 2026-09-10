---
name: combat-semantic-change
description: 修改 CombatSolver 的卡牌、Power、遗物、药水、球、怪物行动、死亡召唤、选择、RNG、Fork 或跨回合战斗状态时，选择正确语义层并验证根快照、分支状态和实际结算。
---

# CombatSolver 战斗语义修改

## 适用边界

本 skill 处理会改变合法动作或战斗结算的语义。纯 UI、职责移动、Beam/评分调优和发布工作分别使用对应 skill。

开始前读取 `docs/ARCHITECTURE.md` 的 Runtime、Search、模拟引擎与 Prediction 章节。若 actual/simulated、增量回放和续用均一致，问题才可能属于搜索质量，转用 `search-performance-optimization`。

`CombatBeamSolver.RoundLifecycle.cs` 统一回合推进；普通重放与后续检查点必须共用 `AdvancePlayerTurnStart`，不得复制玩家回合开始结算。提取阶段不改变选择事务、回调顺序、RNG 或逻辑预算。 直接 EndTurn 前缀由该次枚举独占；仅已完成阶段的空游标可临时分离，其他 Fork 禁止条件原样执行。所有恢复进入同一玩家开始方法，嵌套/逐实例选择维持原额度与顺序，枚举退出释放冻结图；不得把上下文搬到发布快照或共享 worker 缓存。

## 1. 沿当前调用链定位

玩家问题包先按 `issue-bundle-triage` 读取异常栈、状态差异和动作记录，再检查对应源码。已有证据能证明错误链时直接定位，原包重跑用于解决尚未确定的问题；修复的正确性另由下面的最小行为验证证明。

按实际路径追踪，不从最终战损倒推：

```text
CombatRootSnapshot.Capture（主线程根）
  -> CombatPredictionSimulator / Engine Mirrors
  -> Prediction support / SimulatedCombatState partial
  -> CombatBeamSolver.Expansion（动作入口）
  -> CombatBeamSolver.Phases（跨回合推进）
  -> StateEvaluation / Terminal
  -> ContinuationStamp / actual-simulated 严格差分
```

检查同一效果是否同时存在于：

- `CardOnPlayMirrors` 与各 Hook registry；
- `CardEffectSpecRegistry` / `CalculatedVarSpecRegistry`；
- `CardOnPlaySupport*` / `CardPowerOnPlaySupport*`；
- `CorePowerSupport` 与具体生命周期 support；
- `MonsterMoveEffects` / `MonsterMoveSemantics` / `BranchMonsterAi`；
- `SimulatedCombatState` 的对应 partial。

确定唯一权威结算点后再改代码。不能靠执行顺序抵消双结算。

## 2. 选择实现层

- 通用命令时序、资源、集合、历史、RNG：`src/Engine/InCombat/Simulation`。
- 某个原版 Hook / Model 方法的精确实现：`src/Engine/InCombat/Mirrors`。
- 跨 Hook 生命周期、隐藏状态、怪物 AI、死亡/召唤、异步事务和第三方 subscriber：`src/Prediction` 或对应 `SimulatedCombatState.*.cs`。
- 候选展开入口：`CombatBeamSolver.Expansion.cs`，这里只调用语义，不实现具体结算。
- Beam 保路、最终排序与预算不是语义修复位置。
- live 部署和 UI 不反向修正预测结果。
- 补货在 `AfterDeath` 内、旧个体移出阵容前生成替补，使随机生命判重与原版使用相同候选集合。死亡清理继续处理生命周期，生成动作由该 Hook 镜像独占。
- 温柔等逐次出牌完成效果在对应 `AfterCardPlayed` 镜像内结算；外层效果的历史范围包含内层自动牌，按范围扫描逐牌效果会重复处理内层事件。保留原有分支计数、Fork 与回合末恢复所有权。
- 历史敏感倍率使用冻结的根历史与当前分支新增事件。验证应让实机在根捕获后推进同类事件，再检查父分支、Fork 与跨回合结果，覆盖回合准备根早于实机抽牌的时间窗口。
- 出牌限制先对照原版事件口径：CardPlayStarted 包含仍在执行的外层卡牌及重放，不能换成已完成次数或手动动作数。凡庸的权威入口是 ShouldPlay mirror，手动与自动打牌共用分支手牌/开始计数；只在候选枚举入口拦截会漏掉倾泻等嵌套自动牌。
- 终局回合由模拟器在原版安全检查点首次锁定，Snapshot 按值保留并供标注/排序共用；不从最后动作回合推断、不统一加一，也不在已经开始的 Hook 监听器序列中逐个插入胜利中断。
- 命令本身的终局门仍应在对应调用点核对。例如遗物 AfterCardPlayed 计数会在末击后递增，但 PowerCmd.Apply 在 IsEnding 拒绝加属性；不能省掉命令门，也不能把属性延后到整个监听器序列结束再统一补偿。
- 生产选牌部署通过 `NativeChoiceRuntime` 驱动原版页面；`ICardSelector` 只用于无 UI 测试和原版明确自动选择。
- 计划卡牌按 ID、升级和影响后续结算的逐实例语义状态匹配；附魔、重放、费用、关键词、动态变量或临时标志不同时，不能仅凭同名卡牌的序号回放。
- 首回合准备没有既有路线，原生页面可见后再搜索。全自动后续回合消费上一轮 `EndTurn.TurnStartChoices` 并以 continuation 核对结果，不能为了展示页面重复搜索；单步执行在上一回合路线结束后交还控制，下一回合原生页面默认等待玩家，玩家在该页面请求执行或全自动时接管既有选择并继续复用，仍不得从选择中间态重搜。

新增或修改 mirror 注册时，由 `MethodMirrorRegistryDescriptor` 自动向 CoverageCatalog 描述支持状态；不要在工具侧复制 registry 私有布局或另建平行登记。

## 3. 状态所有权清单

新增分支状态必须回答：

1. 根值从哪里、在哪个主线程时点捕获；
2. 所有者是基础 shadow、`SimulatedCombatState`、克隆 Model 还是 `PredictionStateStore`；
3. Fork 是深拷贝、COW 或不可变共享；
4. 内部引用如何通过同一个 `PredictionForkContext` 重映射；
5. 是否改变未来合法动作或结算，因而进入状态键；
6. 是否跨回合存活，因而进入 `ContinuationStamp`；
7. actual/simulated 严格状态如何捕获；
8. 创建、叠加、归零、移除、清空和 Fork 稳定边界。

活动 roster 和已知怪物状态是不同生命周期。怪物死亡或离开可行动阵容后，其正在执行行动仍可能读取根 AI/静态参数；不要随 roster 移除提前删除这些数据。

纯派生搜索启发式不属于战斗状态，不进入状态键或续用文本。

## 4. 隔离与失败语义

- worker 只消费 `CombatRootSnapshot`，不补做 live 捕获。
- 真实 Model 只作稳定身份、类型或根阶段只读元数据。
- 写卡牌前取得 `PredictedCard.MutablePreview`；不得写 `Original`。
- 分支可变对象显式克隆或 `RequireRemap`。
- 不在 worker 推进真实动作队列、牌堆、Power、Creature 或 run RNG。
- 不新增宽泛 catch、静默默认值或“跳过该候选”。未支持行为让搜索明确失败或形成已定义边界。
- gameplay mod subscriber 必须在根阶段识别所有权；未知来源显式拒绝，不做通用浅拷贝。
- 根可达卡牌的第三方 OnPlay Harmony 补丁由 `PredictionModPatchAudit` 检查；跨根读取当前补丁表，避免缓存已卸载或后来安装的补丁。新增适配时明确其来源与语义，不能用未知来源放行代替适配；此入口不代表所有第三方方法已覆盖。

## 5. 验证选择

定位依据和修复验证分别取证。先在未改行为源码上建立最小失败基线，再停在能覆盖根因的最低层；包内日志已说明的异常可以直接转成最小夹具，无需先启动原包恢复来重复确认：

1. 默认只跑目标效果的 actual/simulated 严格差分，比较有序牌堆、逐实例状态、Power、怪物 AI、球与相关 RNG；
2. 新增 Fork 状态时，在同一最小夹具断言 Fork、指纹和重映射；新增跨回合历史或续用字段时，用两回合生命周期或最早 continuation 边界核对 live/predicted；
3. `-VerifyIncrementalSearch` / `--verify-incremental-search` 只加在实际启动正式搜索并回放候选的 fixture 上，不能给纯一步差分增加无效成本；
4. 覆盖根因直接相邻的生命周期，例如回合开始/结束、叠加/移除、死亡/复活或嵌套选择；不要自动扩成整场战斗和全部同类模型；
5. 普通快速迭代的单个 unattended 请求总超时不超过 `120` 秒。搜索使用短预算并在首个目标动作或最早复用回合停止；超时后缩小 fixture 或明确写未验证，不在同一轮延长到 `180/360` 秒；
6. 完整自动 headless 只在改动搜索/部署编排、较小边界无法覆盖、用户明确要求完整回归/门禁，或要声称整场零重算时运行；固定 `Instant / 0 秒`；
7. 改 mirror 支持面、状态字段或 coverage 分类时运行对应 CoverageCatalog verify；只有改变目录覆盖面或明确完整门禁时跑全量；
8. UI、动画或真实卡顿另做可见 Steam 验收。

多敌已知路线的原版对照须在全部正式预测冻结后才推进live，使用固定原始Creature身份逐敌比较；末击在真实清理前取证并等待对应CombatEnded，不能以总敌HP代替死亡/阵容/完整状态。测试选择器未提供的原生来源或上下文参数应明确限定证明范围，不声称直接比对；累计伤害、原生洗牌事件与Search统计也应分别记账。

性能数字不能来自 `-VerifyIncrementalSearch` / `--verify-incremental-search`。通用 helper 改动应覆盖其调用类型族，不只跑最初报告的一个模型。

## 6. 记录与提交

紧凑执行实验位于 `Simulation/Compact/`，目前仅由 Testing 准入。挂起时执行位置、选择实例列表、资源/牌堆和事件游标必须使用同一撤销工作区；冻结候选不能引用原 worker 或旧模型投影。扩展其支持面时先补整根能力闭包与原版差分，未知效果在执行前明确拒绝。旧模型只可从值事件构造读投影，不得重放效果或双写；全状态、原状态键、完整估值、风险/来源历史及下一动作续接均须比较。拒绝洗牌的原型不能声称已经验证 RNG 写入和回滚。

更改冻结表示时覆盖空页、页边界、完整有符号值、非零转零、rollback 后冻结、外根和活动事务拒绝，以及冻结后 8 worker 独立恢复。恢复保留暂停指令与完整历史，不保留旧 checkpoint 令牌；只测整数槽的高密度/长历史探针不能冒充真实 Power、RNG 或死亡生命周期验证。

提交前同步受影响的 `docs/DEVELOPMENT_NOTES.md`、`docs/TEST_MATRIX.md` 和必要的结构化证据。职责边界有变化时同时更新 `docs/ARCHITECTURE.md` 与结构门禁。

普通语义修复直接提交。是否随该项提升版本和打包，以 `AGENTS.md` 的活动发布批次和发布口令为准；不要由本 skill 另立发包规则。汇报应说明首个错误状态、权威实现层、状态所有权、实际运行的 fixture 和未执行项。

完成状态读视图阶段：旧／新入口必须共用完整 SnapshotCore、合法性、原键和排序；Testing 负责能力闭包，Search 只认识读取合同。未迁移字段须证明在闭包内不变，不能从 live 或一份过期旧分支补读。历史 getter 的惰性零项与缺席可能影响原键，根级准备须隔离；技能次数与技能集合等并存语义不能遗漏。风险摘要可在固定来源闭包内准备，但保留每次风险事件的历史计数。读取器不逃入候选，完成快照释放借用 Simulator；每批初始化成本纳入对照。真实原生前后字段净变化只是观测，packed 字段、重复字段和瞬态写入须注明，不能据此宣称实际 dirty page 或紧凑复杂效果已验证。

洗牌／Power 选牌扩展阶段：Shuffle 的 counter 与四个 64 位状态、原排序关系和抽牌剩余位置必须一并保存、冻结、恢复及撤销；从 Power 选择恢复后不能重复抽牌或遗物触发。直接原键与投影洗牌都读取分支 RNG，其他八流只有在能力闭包证明不变时才能借用根。估值缓存仅复用闭包内不变的敌人／AI、Power、存活牌集合和元数据，根级创建成本计入测量，换根即重建；共用原计算公式并逐属性比较缓存开关。原生两回合测试若由旧引擎推进回合，必须明确标明，不能冒充紧凑跨回合执行或 Power 数量迁移。

生物值读取扩展：基础 HP／格挡／治疗算术由 `CreatureVitals` 统一，命令仍独占修正、伤害历史、Hook、死亡与终局安全点；值槽中的零 HP 不等于 roster 移除。完整读取合同同时提供数值、有序活动敌人和可空 TerminalStamp；原键的 AI／沙漏部分使用同一 roster，敌人变化时旁路根敌人／焦点缓存。未迁移的 Hook、宠物和领域公式不能依赖已变化值；Power 数量不变不足以证明这一点。逐属性／原键／排序测试须包含部分离场、全部离场和终局，标量原生差分不能冒充紧凑死亡程序已经实现。

普通攻击读取扩展：攻击目标、三个伤害结果标志、来源与命中历史、上一张攻击、死亡阶段和终局均须属于分支或已提交值事件。最后一击进入 IsEnding 后，结果牌堆移动可能被拒绝，不能在末击后强行送入弃牌堆。原公式的 Simulator scratch 不是只读元数据；并行读取器各自拥有初始化根副本，初始化成本计入测量，禁止每叶投影或让候选保留读取器。扩展到 Power 死亡时同时迁移数量、有序列表与生命周期，不凭简单击杀域通过放行复杂效果。

基础 Power 值迁移：数量、施加者、获得顺序、根槽退休和死亡清理一起进入撤销状态；不能只更新数量再借用旧监听器顺序。新建普通 Power 的额外 Target 默认空，显式定向 Power 保留原目标。原生命令与旧 oracle 不一致时追溯实际命令边界；例如零基础格挡仍应先执行敏捷修正，不能把旧提前返回当作规范。

有序指令扩展：`CardEffectProgram` 只保存私有复制的不可变效果序列，模型准入和编译仍由 Testing 负责。每帧指令索引、抽牌进度、选择和子牌位置写入同一撤销状态；子牌完成后恢复父牌剩余指令。覆盖同牌多次选择、部分抽牌后的洗牌、空选择与八工作区恢复；合成指令合同不能冒充原生效果闭包，两个原完整输入及动态生成仍须分别验证。
