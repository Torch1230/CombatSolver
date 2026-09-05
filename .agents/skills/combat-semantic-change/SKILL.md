---
name: combat-semantic-change
description: 修改 CombatSolver 的卡牌、Power、遗物、药水、球、怪物行动、死亡召唤、选择、RNG、Fork 或跨回合战斗状态时，选择正确语义层并验证根快照、分支状态和实际结算。
---

# CombatSolver 战斗语义修改

## 适用边界

本 skill 处理会改变合法动作或战斗结算的语义。纯 UI、职责移动、Beam/评分调优和发布工作分别使用对应 skill。

开始前读取 `docs/ARCHITECTURE.md` 的 Runtime、Search、模拟引擎与 Prediction 章节。若 actual/simulated、增量回放和续用均一致，问题才可能属于搜索质量，转用 `search-performance-optimization`。

## 1. 沿当前调用链定位

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

## 5. 验证选择

先建立未改代码的最小失败基线，再停在能证明根因的最低层：

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

提交前同步受影响的 `docs/DEVELOPMENT_NOTES.md`、`docs/TEST_MATRIX.md` 和必要的结构化证据。职责边界有变化时同时更新 `docs/ARCHITECTURE.md` 与结构门禁。

普通语义修复直接提交。是否随该项提升版本和打包，以 `AGENTS.md` 的活动发布批次和发布口令为准；不要由本 skill 另立发包规则。汇报应说明首个错误状态、权威实现层、状态所有权、实际运行的 fixture 和未执行项。
