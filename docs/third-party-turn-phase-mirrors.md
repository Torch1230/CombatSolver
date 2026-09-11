# 回合阶段镜像

当前开放 `AbstractModel.AfterSideTurnEndLate`。这是效果登记，与
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

## 执行约束

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

沿用根冻结的 Hook 类型掩码，在类型布局上增加一个 bit；不逐节点反射或扫描程序集。
没有参与监听器时不分配本阶段上下文或接收者列表。单监听器只建立上下文，接收者保留在局部值中；
多个监听器才建立剩余接收者列表，以保留成员快照和 COW 引用；不将列表跨阶段或跨 Fork 缓存。
登记冻结只在首次进入时加锁，后续仅做 volatile 读取；适配者委托自身的成本由其负责。
这些是代码层面的成本边界，未进行本机 A/B 或真实游戏性能测量。

```sh
dotnet run --project tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj -c Release
dotnet run --project tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj -c Release -- --seal
dotnet run --project tools/TurnPhaseMirrorChecks/TurnPhaseMirrorChecks.csproj -c Release -- --allocation
```

独立合同链接生产 registry、晚期 facade 和 CardHookReceiver；游戏模型、模拟器命令与
监听表来源使用最小替身。覆盖精确登记、元数据、冻结、两侧参数、顺序、COW、成员变化、
异常、选择暂停和原版镜像调用次数，不证明真实伤害命令、根捕获或原生两回合等价。

实机复核先用既有 `coverage/unattended/monster-moves-batch-033-disintegration.json`
（5 层、2 格挡、期望 3 HP 损失及层数保留），再覆盖敌方晚期伤害、末击边界，
以及中性测试遗物／Modifier 的根捕获 → Fork → 两回合原生与预测状态严格差分。
这一轮没有执行这些游戏测试，不把历史 fixture 的 Passed 当成本轮证据。
