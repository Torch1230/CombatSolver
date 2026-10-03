# Power 隐藏状态登记合同

[返回适配手册](README.md)

### 2.6 Power 的隐藏状态进指纹

随 PR #58 于 `0.33.0` 发布。登记应在 Mod 初始化、任何根捕获和后台搜索之前完成；搜索期间保持登记表不变。依赖此入口的适配 Mod 应要求 CombatSolver `0.33.0`。

```csharp
// 状态在普通私有字段里：只要这一条。
PowerHiddenStateMirrors.Register<TYourPower>(
    "TotalMantraGained",
    (simulator, power) => power.某个私有计数);

// 状态在 _internalData 里：还要这一条，否则模拟一开始读到的是初值。
PowerHiddenStateMirrors.RegisterRootCapture<TYourPower>(
    (simulator, clone, original) =>
        simulator.StateStore.GetReadOnly(clone, () => new MyState(original)));
PowerHiddenStateMirrors.Register<TYourPower>(
    "InstanceCount",
    (simulator, power) => simulator.StateStore.Peek(power, static p => new MyState(p)).Count);
```

状态指纹里 Power 的通用部分只收 `DynamicVars`。把语义状态放在 `_internalData` 或普通私有字段里
的 Power 走的是另一条路：`AddTurnStartStates` 按原版类型 `switch`，从 `StateStore` 里的预测状态
取一个计数塞进指纹（虚空形态、硬化外壳、自动机、束缚锁链……）。那个 `switch` 没有第三方入口。

**后果和别的缺口不一样，要分清：**

- **续用核对尚未覆盖此状态。** 两侧通用 Power 字段一致不能证明隐藏状态一致；跨回合适配需要单独验证原生与预测状态。
- **对搜索去重有害。** 只在这个状态上不同的两条分支指纹相同，会被当成同一个状态**去掉一条**。
  你的镜像算出来的数值是对的，但搜索可能把算得对的那条丢了。

所以这不是「记个 `Unmirrored` 就行」的事——红字只是显示，不会让被去重掉的分支回来。

#### 续用核对边界

`PowerModel.DeepCloneFields` 会把 `_internalData` 重置成 `InitInternalData()`。续用核对若要覆盖隐藏状态，需要分别读取原生状态和已捕获的预测状态。本入口仅提供搜索指纹与根捕获登记，尚未提供这两侧的续用追加入口。

#### 靠 `_internalData` 的必须登记根捕获

同样因为克隆会重置，这类 Power 必须用 `RegisterRootCapture` 在根捕获时把实机实例的值搬进
`simulator.StateStore`，此后一律读预测状态，**不要再读克隆上的 `GetInternalData`**。这正是原版
`PowerPredictionStateSupport.CaptureRootState` 在做的事，照它的形状写即可。搜索途中新施加的实例
不走根捕获，它们的 `_internalData` 本来就是初值，预测状态首次取用时按初值起算就是对的。

状态放在普通私有字段里的 Power 不受影响（`MemberwiseClone` 会带过去），只登记读取函数就够了。

#### 三条约束

1. **只收整数。** 原版那个隐藏计数段里全部是整数或枚举；字符串只会出现在展示用的名字上，那类
   字段按 `SemanticStateFieldPolicy` 本来就不该进指纹。
2. **读取函数必须是纯读取。** 它在搜索热路径上被调用很多次，不得有副作用，也不要在里面分配。
3. **返回值只能取决于这个 Power 自己的状态**（含它在 `StateStore` 里的预测状态）。它参与状态
   等价判断，读别处会让等价判断不自洽。

登记多个状态就多调几次 `Register`，名字在同一类型内不得重复，下游按名字排序后依次进指纹。

**两个真实例子，都在观者。** 光辉的伤害等于牌面值加上本场战斗累计获得的真言，累计值在
`WatcherStatePower` 的一个普通私有 `int` 里，只需要读取函数；登记之后「先攒真言再打光辉」和
「直接打光辉」不再被当成同一个状态。天人形态的那个 Power 用 `_internalData` 存一个实例表，每回合
给「总和」点能量再把每个实例加一——总和就是 `Amount`，本来就在指纹里，缺的只是**实例个数**，
也就是下一回合总和的增量；它要根捕获加读取函数两条，登记一个 `InstanceCount` 就够了，不需要把
整张表塞进去。
