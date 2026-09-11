# 遗物与 Modifier 的分支状态适配

`ModelPredictionStateMirrors` 为根中已有的遗物和战斗 Modifier 提供精确类型登记。
同一登记包含主线程捕获、实机状态描述和预测状态描述；状态由现有 `PredictionStateStore`
持有并随分支复制，描述字段同时进入搜索指纹与 `ContinuationStamp`。

这是开发中的接口。与现有内部镜像入口一样，外部适配程序集需要 publicizer；没有新增稳定的公开 SDK。
状态登记不授予 Hook 支持，不修改 Harmony 补丁审计或 ModHelper subscriber 门禁。

## 卡牌引用辅助接口

- 根捕获用 `PredictionCardReferences.RequireCard(simulator.State.GetPlayerCombatState(owner), liveCard)`，按 Original／当前 Preview 的对象身份查找；缺失或多匹配明确失败。不要按卡牌 ID 或升级等级回退，也不要保存旧 COW Preview 作为长期句柄。
- 状态保存 `PredictedCard`。Fork 用 `PredictionCardReferences.Remap(references, context)` 复制列表并逐个 `RequireRemap`；null 列表、null 元素、重复引用和顺序均保留。它复制这一层列表，不承诺任意对象图或列表容器之间的别名关系。
- live writer 用 `AddCard(name, CardModel?)` / `AddCards(name, IReadOnlyList<CardModel?>?)`；predicted writer 用对应 `PredictedCard` 重载。两侧不能混用。单个 null 需要显式类型以选择重载。
- 默认有序。只有结算确实无序时才传 `unordered: true`；规范化仍保留重复次数。每次观察按玩家 NetId、手／抽／弃／消耗／打出牌堆和堆内位置编码引用关系，不使用对象 hash、同名序号或新的全局 ID。
- 只支持当前五个战斗牌堆内的引用。已经移除、悬浮、永久牌组、其他分支及无法映射的引用明确失败；适配者应在正确生命周期清除失效引用，不能将其改成 null 来掩盖缺失语义。

位置是当前状态的关系标识，会随移动而改变；与牌堆状态一起参与去重和 continuation，对两张同名牌的引用也可区分。第一次写非 null 引用才建立位置索引，同一次完整观察的所有模型共用一张；复杂度为 O(战斗牌数 + 引用数)，无序列表额外 O(k log k) 及一个临时数组。仅写标量、空列表或 null 不建立索引。不缓存跨观察位置，不逐引用扫描全牌表。回调必须只读且同步，不得修改牌堆或复制并长期持有 writer。

可编译的中性遗物／Modifier 示例见 `tools/ModelPredictionStateChecks/CardReferenceChecks.cs`：同一状态从 live 卡牌列表捕获预测引用，Fork 重映射，两个 writer 分别描述各自一侧。该工具替换游戏身份与模拟器外壳；真实模拟器／COW 断言位于 `MODEL-STATE-INTEGRATION`，本项尚未运行游戏场景。

## 登记与使用

两种入口分别约束模型类型：

```csharp
ModelPredictionStateMirrors.RegisterRelic<TYourRelic, TYourState>(
    schema, capture, writeLive, writePredicted);
ModelPredictionStateMirrors.RegisterModifier<TYourModifier, TYourState>(
    schema, capture, writeLive, writePredicted);
```

- `schema`：非空的状态结构标识，例如 `counter-v1`，参与指纹和续用核对。
- `capture`：`Func<CombatPredictionSimulator, TModel, TState>`，第二个参数为实机实例。
  只在主线程根物化时执行，必须返回独立的状态对象。预测模型仅作身份，不能写其字段。
- `writeLive`：`ModelPredictionStateWrite<TModel>`，主线程描述实机状态。
- `writePredicted`：`ModelPredictionStateWrite<TState>`，只描述捕获后的分支状态。
- `TState`：引用类型，实现 `IPredictionStateForkable`。有未完成事务时还应实现
  `IPredictionForkBoundary`，在不安全边界拒绝 Fork。

所有登记必须在第一个预测根或 continuation 捕获之前完成，随后整张表冻结。
重复、抽象类型和迟到登记均抛异常；登记父类型不会自动匹配子类型。
模型构造、根捕获或状态工厂失败直接向调用方传播，不将部分状态当作可用结果。

以下是一个整数计数器的适配形状，`ExampleRelic.Counter` 代表适配方自己的实机状态：

```csharp
sealed class CounterState(int count) : IPredictionStateForkable
{
    public int Count = count;
    public object Fork(PredictionForkContext context) => new CounterState(Count);
}

ModelPredictionStateMirrors.RegisterRelic<ExampleRelic, CounterState>(
    "counter-v1",
    static (_, live) => new CounterState(live.Counter),
    static (ExampleRelic live, ref ModelPredictionStateWriter writer) =>
        writer.Add("count", (long)live.Counter),
    static (CounterState state, ref ModelPredictionStateWriter writer) =>
        writer.Add("count", (long)state.Count));

// 在已登记的模拟 Hook 中更新分支状态；不读写 relic 的实机计数。
CounterState state = ModelPredictionStateMirrors.Get<CounterState>(simulator, relic);
state.Count++;
```

`Get` 不捕获实机、不制造默认状态。用错模型身份、状态类型或遗漏根捕获时立即失败。
接口内部包装适配状态，复用 `PredictionStateStore` 的别名和同一个 `PredictionForkContext`；
适配方不应绕过 `Get` 向 store 写入另一份同名状态。

## 复制与状态描述契约

1. 根捕获在内置状态物化完成后执行。若状态持有卡牌等可变对象，应在捕获时解析为预测对象；
   Fork 时使用传入的 `PredictionForkContext.RequireRemap`，不能复制集合后仍引用父分支对象。
   活跃模型的互相引用只作稳定身份，跨模型状态通过 `Get` 读取；捕获工厂不依赖其他适配器的执行顺序。
2. `Fork` 必须返回新的同类状态，实际运行时类型也必须一致；仅能转换成登记时声明的基类还不够。
   返回自身、错误类型或缺少必要映射都拒绝复制。
   可变集合仍由适配方深拷贝；接口不能通过反射证明任意对象图没有共享可变引用。
3. `writeLive` 与 `writePredicted` 使用相同的字段名、字段类型和顺序。writer 支持 `long`、
   `ulong`、`bool`、可空 `string`；数组先写长度，再按顺序写元素。集合需自行按稳定语义键排序。
   字段只包含会影响未来合法动作或结算的值；不得加入日志、UI、对象地址或进程随机哈希。
4. 描述回调必须纯读取。指纹热路径不创建文本；续用文本使用不受区域设置影响的数字格式，
   并转义分号、字段分隔符、反斜杠和控制字符，保留 null 与空字符串的区别。
5. 每个实例按所属类别、玩家身份、原有列表位置、模型类型及 schema 绑定字段。
   两个同类型实例交换计数会改变指纹，值为零的状态也保留。表为空或根没有匹配实例时，
   不追加指纹或续用字段。

续用核对会发现实机与预测描述不同；重新捕获根会从实机重建适配状态。搜索分支的跨回合保留
由现有 store 生命周期负责。此接口不增加游戏存档字段，也不提供将状态文本反序列化回实机的入口。

## 范围与验证

本入口仅覆盖捕获根中已有的遗物和 Modifier 状态，不增加中途获得／移除这些模型的能力。
卡牌、Power、ModHelper subscriber、自定义回合 Hook、原版方法替换及策略估值仍需各自适配。
不能因状态接口已登记，就认定该模型的实际结算已受支持。

独立检查：

```sh
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release -- --empty
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release -- --allocation
```

这些命令使用生产 registry、writer、store 和 fingerprint，替代游戏身份与 Fork context 外壳。
它们覆盖捕获隔离、实例绑定、零值、Fork、必要映射、事务边界、续用描述与错误传播，
不证明完整模拟器、回合 Hook 或游戏运行正确。具体适配仍须执行单效果 actual/simulated 差分，
并在存在跨回合状态时比较最早续用边界，检查完整状态而非仅 HP。

仓库另有 `MODEL-STATE-INTEGRATION` 专用后台合同，直接使用真实模型、完整模拟器 Fork 和
原生两回合推进，覆盖遗物/Modifier 状态、卡牌引用重映射及完整 continuation；命令和证据见
[测试矩阵](TEST_MATRIX.md)。该夹具验证接口接线，不代替具体第三方效果镜像的语义回归。
