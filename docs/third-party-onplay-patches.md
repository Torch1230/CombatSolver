# 已适配 OnPlay 补丁组合

`AdaptedCardOnPlayMirrors.Register<TCard>` 为精确卡牌类型登记一组已审阅的 Harmony 补丁和唯一的完整预测实现。它是内部开发接口，外部程序集需要 publicizer，不是按 Mod ID 的放行名单。

```csharp
AdaptedCardOnPlayMirrors.Register<MyCard>(
    "my-card-patched-v1",
    exactOnPlayMethod,
    [new AdaptedOnPlayPatch(HarmonyPatchType.Prefix, exactPatchMethod,
        "my-harmony-owner", Priority.Normal, [], [])],
    (card, context) => ApplyCompletePredictedEffect(card, context));
```

调用必须在首次根／live continuation 捕获前完成。目标必须是该精确类型的 OnPlay 方法；不能传同名重载或其他类型的方法。每个类型只能登记一个完整组合，不组合多份局部补偿。补丁声明在登记时复制为不可变签名，调用方后来修改数组不会改变声明。

四类补丁分别为 Prefix、Postfix、Transpiler、Finalizer。同类声明按实际执行顺序填写；审计用 Harmony 自身的排序器核对方法、所属模块、类别、owner、priority、before／after 和完整顺序。组合包括目标上的全部补丁，即使其中某个来源原本被视为非 gameplay。额外同 owner 补丁也会失配。多个相同补丁方法、动态补丁工厂及 Inner Prefix／Postfix 暂不支持。

根审计仍逐个识别来源；未知来源与项目明确拒绝的 Mod 无法通过登记解除。没有补丁时选回普通镜像；非空组合不匹配时明确拒绝。适配声明不解除 subscriber、隐藏状态、目标类型或其他方法的门禁。

完整预测实现通过现有 `MethodMirrorRegistry` 执行，保留卡牌 trace 与状态上下文。匹配后 `CardOnPlayMirrors` 直接返回，**不会再执行原版 OnPlay 镜像和 CardEffectSpec 补偿**。因此 handler 必须覆盖原方法及整组补丁的效果，包含原 spec 中仍然需要保留的部分。写入只通过分支状态或 MutablePreview；不得执行原生补丁、捕获 live 状态或在静态闭包里保存可变分支状态。

`DescribeRegisteredCompositions()` 提供条件签名及标准 `MethodMirrorRegistryDescriptor`，无需反射私有表。条件支持不写成原版 CoverageCatalog 的无条件覆盖；当前没有内置第三方适配声明。

## 根、Fork 与旧路线

`PredictionModHookSubscriberCapture` 拥有根内选择表，`SimulatedCombatState` 的 Fork 只共享不可变选择和配置标记。worker 每次 OnPlay 只查表，不扫描 Harmony，也不调用原生补丁。启用登记后，首次根审计未见过的卡牌类型即使后来生成也明确失败；下一次捕获可重新审计。

live continuation 在主线程读取当前所有 CardModel.OnPlay 补丁的配置标记，包括登记 schema、目标与预测实现的方法身份。predicted continuation 和状态指纹使用冻结标记。安装、卸载、顺序变化及后来出现的卡牌补丁会改变 live 标记，沿现有 LiveCombatStamp／ContinuationStamp 的结果采用、缓存和部署前核对淘汰旧路线。没有登记时不增加配置字段或全局扫描。

这是边界核对，不支持在捕获途中或正在执行原生动作时并发修改 Harmony。适配者应在初始化阶段安装补丁和登记。

已登记 OnPlay 的 async MoveNext 若另有补丁会直接拒绝，包括旧根采用前；它不属于四类组合的隐含覆盖。没有登记的其他状态机、其他原版方法以及任意动态 detour 仍不在此入口的完整审计范围内。

## 验证与成本

`tools/AdaptedOnPlayChecks` 使用游戏自带 Harmony，在独立 .NET 进程里真实安装／卸载补丁，比较中性托管模型的原生替换、前后缀组合与生产镜像分派。游戏实体和模拟器外壳由替身提供；该证明不等于真实战斗、跨回合续用或完整部署通过。

游戏级夹具另外覆盖原生 OnPlay 完整替换、完整模拟器 Fork／增量回放、T1→T2 状态对账、控制器缓存执行资格和最早跨回合续用，输入在 `coverage/unattended/adapted-*-integration.json`，运行证据见测试矩阵。注册与测试补丁只在专用 scenario 中启用，必须使用隔离的新游戏进程；不在正常存档或玩家正在进行的战斗里运行。

主线程配置核对成本随补丁数量和登记数量增长；每次 live stamp 会重新读取，不缓存可能过期的 Harmony 表。worker 增加一次类型查询，命中后沿现有精确 registry 分派。未做性能 A/B 或真实游戏性能结论。
