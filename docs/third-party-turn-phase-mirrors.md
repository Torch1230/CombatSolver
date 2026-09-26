# 回合阶段镜像

当前开放 `AbstractModel.BeforeSideTurnStart`、`AfterPlayerTurnStart`（Early/普通/Late）和 `AfterSideTurnEndLate`。这是效果登记，与
[模型状态登记](third-party-model-state.md) 分开；不代表其他阶段、ModHelper 订阅者或
Harmony 补丁已经受支持。外部程序集使用与其他内部镜像相同的 publicizer 接入方式。

## 签名与登记

命名空间：`CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd`。

```csharp
AfterSideTurnEndLateMirrors.Register<TModel>(
    Action<TModel, AfterSideTurnEndLateMirrorContext> handler);
// where TModel : AbstractModel
```

`TModel` 必须是重写该原生方法的具体类型；精确匹配，不把基类登记继承给子类。
同类型重复登记、空委托、抽象类型、没有重写的方法均拒绝。
首次根捕获或阶段分发冻结登记，冻结后再登记明确失败。加载期完成所有登记，
不要通过反射修改私有 `Registry`，否则会绕过冻结并使缓存与并行搜索失去一致性。
底层 `MethodMirrorRegistryDescriptor` 自动提供覆盖元数据，无需另填一张扩展注册表。

上下文提供 `Side`、`Participants`，以及继承的 `Simulator`、`State`、`StateStore`、
`Rng`、`History`、`CombatState`。例如，一个已经登记分支计数状态的遗物可以这样消费状态：

```csharp
AfterSideTurnEndLateMirrors.Register<MyRelic>(static (relic, context) =>
{
    if (context.Side != CombatSide.Player || !context.Participants.Contains(relic.Owner.Creature))
        return;
    var state = ModelPredictionStateMirrors.Get<MyCounterState>(context.Simulator, relic);
    state.Turns++;
});
```

这是接口示例，`MyRelic`/`MyCounterState` 由适配者定义，并先登记 capture、writeLive、
writePredicted 与正确 Fork。实际效果用模拟器命令实现，不能调用原生异步 Hook 或真实动作队列。
接收者仅用于稳定身份、元数据和分支映射；不要在遗物／Modifier 实例上写隐藏状态，
也不要从 `Owner` 的 live 战斗字段、静态集合或闭包读取可变值。

### 按运行时类型登记（外部适配 Mod）

泛型入口要求调用方在编译期就持有目标类型。**适配另一个 Mod 的适配器通常做不到这一点**——
它刻意不引用对方程序集，而是运行期用反射（甚至只读 PE 元数据）找到类型，因此只拿得到
`Type`。这三张表为此各有一个按 `Type` 登记的重载，判据与泛型入口逐字相同
（具体类型、必须真的重写该阶段、重复登记拒绝、首根冻结）：

```csharp
BeforeSideTurnStartMirrors.Register(Type modelType, Action<AbstractModel, BeforeSideTurnStartMirrorContext> handler);
BeforeSideTurnStartMirrors.RegisterIgnored(Type modelType);

AfterPlayerTurnStartMirrors.RegisterEarly(Type modelType, Action<AbstractModel, AfterPlayerTurnStartMirrorContext> handler);
AfterPlayerTurnStartMirrors.Register(Type modelType, Action<AbstractModel, AfterPlayerTurnStartMirrorContext> handler);
AfterPlayerTurnStartMirrors.RegisterLate(Type modelType, Action<AbstractModel, AfterPlayerTurnStartMirrorContext> handler);
AfterPlayerTurnStartMirrors.RegisterIgnored(Type modelType);

AfterSideTurnEndLateMirrors.Register(Type modelType, Action<AbstractModel, AfterSideTurnEndLateMirrorContext> handler);
AfterSideTurnEndLateMirrors.RegisterIgnored(Type modelType);
```

`Type` 由 `ThirdPartyMirrorRegistration` 桥回底层注册表的泛型实参；那条桥是**唯一**允许把
`Type` 变成泛型实参的地方，适配层不要自己写 `MakeGenericMethod`。

`RegisterIgnored(Type)` 是「已复核：这个覆写没有任何预测相关行为」的显式结论，用于只有音效、
台词、屏幕特效等纯表现层的第三方覆写。`AfterPlayerTurnStart` 那一版按类型**实际重写的那一个**
阶段写入对应表；三个阶段都没重写的类型会被拒绝，而不是被静默接受。

**这三张表是硬门禁**：战斗监听表里出现未登记的第三方覆写时，派发会抛
`NotSupportedException` 并中止整次搜索。因此每个重写这些阶段的第三方类型都必须三选一——
登记处理器、登记忽略，或明确不进这场战斗（例如让适配自检失败）。漏掉一个的表现是整场给不出
路线，而不是「近似一下继续算」。

## 执行约束

### 回合开始前

`CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart.BeforeSideTurnStartMirrors`
提供 `Register<TModel>(Action<TModel, BeforeSideTurnStartMirrorContext>)`，接收者同样是
`AbstractModel`，适用于 Power、遗物和 Modifier。精确类型、覆写核验、重复拒绝和首根冻结
规则与晚期表相同。上下文的 `Side`、`Participants` 来自调用方，原生 `ICombatState` 参数
对应继承的分支 `CombatState`，禁止读取 live 状态代替它。

玩家和敌方均在 `BeginSideTurn`、回合初 Power 数量快照之后，清除格挡之前进入此阶段。
有第三方监听者时，以原生战斗监听表顺序冻结本次成员，不按参与方提前筛选；每个回调自行
判断 Side 和 Participants。原版遗物重置、Plating、Aggression 及回合计数由同一张表调用
既有单项结算体，无扩展战斗保留原先遗物与 Power 的批次顺序。不会同时执行两条路径。
未知有效覆写记录风险后抛异常；选择、异常传播、卡牌 COW 和成员快照规则与下述晚期入口相同。
本阶段不开放回调内的通用可恢复执行帧，挂起选择仍由既有完整重放处理。

顺序依据是游戏 0.111.0 的 `Hook.BeforeSideTurnStart`：枚举
`IterateCombatHookListeners(combatState)`，逐个调用模型方法，不提前按 Side 筛选。
求解器的玩家入口位于 `CombatBeamSolver.RoundTransition`，敌方入口位于
`CombatBeamSolver.Expansion.Replay`。空扩展路径和有扩展路径互斥，避免原版效果重复结算。

### 玩家抽牌后

命名空间 `CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart`：

```csharp
AfterPlayerTurnStartMirrors.RegisterEarly<TModel>(handler);
AfterPlayerTurnStartMirrors.Register<TModel>(handler);
AfterPlayerTurnStartMirrors.RegisterLate<TModel>(handler);
// handler: Action<TModel, AfterPlayerTurnStartMirrorContext>, TModel : AbstractModel
```

三张独立 MethodMirrorRegistry 对应三个原生方法；每种登记都要求具体类型重写对应时点，
精确类型、重复/空委托拒绝、首根冻结和未知覆写拒绝规则与上面的入口一致。
上下文增加 `Player`，分支状态与命令仍由继承的 Simulator/State/CombatState 提供。
接收者可以是 Power、遗物、Modifier 或其他 AbstractModel，不提前按拥有者分组。

游戏 0.111.0 `Hook.AfterPlayerTurnStart` 的三轮顺序是 Early → 普通 → Late，每轮重新
调用 `IterateCombatHookListeners`。本入口在常规抽牌后、AfterSideTurnStart 前执行：
轮内固定成员并跟随 COW Preview，轮间重新取快照；选择立即停止剩余轮，异常直接传播。
不在同一监听者上穿插三个时点，也不在轮内提前终止已选中的监听者。

没有外部登记且入口没有第三方有效覆写时保留原 Power→遗物硬编码路径及其续执行帧。已有外部登记时从 Early 开始按三轮派发，确保普通阶段生成的第三方监听者进入 Late。
有扩展时由三张表按监听顺序调用相同原版单项结算体，BloodVial/FakeBloodVial 位于 Late；
当前正式原版模型没有 Early 覆写。任意适配回调挂起时拒绝原版局部帧复用，从稳定父节点
完整重放，不能在部分执行的分支上直接重调。本接口不新增选择类型、状态捕获或准入豁免。

### 回合结束晚期

- 玩家流程：常规 Power → 常规遗物 → 本晚期阶段 → 词条规范化与阶段收尾。
  敌方流程在既有常规效果和持续时间处理后进入同一晚期入口。
- 分发使用当前分支战斗监听表的原序；不预先按拥有者或阵营筛选。
  `Participants` 可以为空，`Side` 仍由调用者显式传入，回调自行决定适用条件。
- 阶段入口按现有 Hook facade 排除已结束的战斗。开始后固定监听成员，
  不在每个监听器之间插入胜利中断。卡牌接收者跟随所属 `PredictedCard` 的 COW Preview。
- 已挂起选择时不进入本阶段；回调产生选择后立即停止后续监听器，返回未完成。
  调用者依照现有动作重放机制处理选择，不可在部分执行后的同一分支上直接重调以“续跑”。
  本接口没有新增通用选牌 UI 或选择类型；不支持的选择仍需单独建模。
- 回调异常直接传播。没有原生重写则保持基类空操作；纯表现 Mod 沿用既有镜像忽略政策；
  其他未知重写先记录未镜像风险，再抛出带类型名的 `NotSupportedException`。
- 原版 `DisintegrationPower` 只由本镜像结算；旧 `TriggerLate` 补偿已移除。
  注册只覆盖该方法的原生重写，不自动表示其 Harmony 前后缀也已镜像。

## 成本与验证

沿用根冻结的 Hook 类型掩码，每个时点使用独立 bit；不逐节点反射或扫描程序集。
没有参与监听器时不分配本阶段上下文或接收者列表。单监听器只建立上下文，接收者保留在局部值中；
多个监听器才建立剩余接收者列表，以保留成员快照和 COW 引用；不将列表跨阶段或跨 Fork 缓存。
登记冻结只在首次进入时加锁，后续仅做 volatile 读取；适配者委托自身的成本由其负责。
以上局部分配优化适用于 BeforeSideTurnStart/AfterSideTurnEndLate；AfterPlayerTurnStart
只在扩展路径分配每轮接收者列表，普通战斗保留旧结算体。未作性能验证。

```sh
dotnet run --project tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj -c Release
dotnet run --project tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj -c Release -- --seal
dotnet run --project tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj -c Release -- --allocation
dotnet run --project tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj -c Release -- --start
dotnet run --project tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj -c Release -- --start --seal
dotnet run --project tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj -c Release -- --after-player-start
dotnet run --project tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj -c Release -- --after-player-start --seal
dotnet run --project tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj -c Release -- --mask .godot/mono/temp/bin/Release/CombatSolver.dll
```

独立合同链接生产 registry、晚期 facade 和 CardHookReceiver；游戏模型、模拟器命令与
监听表来源使用最小替身。覆盖精确登记、元数据、冻结、两侧参数、顺序、COW、成员变化、
异常、选择暂停和原版镜像调用次数，不证明真实伤害命令、根捕获或原生两回合等价。

玩家晚期伤害及敌我双方 T1→T2 原生完整状态对账通过；末击和多监听器原生顺序未覆盖。
场景输入与验证范围见[测试清单](TEST_MATRIX.md)。
