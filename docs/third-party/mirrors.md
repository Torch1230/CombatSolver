# 镜像登记合同

[返回适配手册](README.md)

### 2.1 统一形状的镜像注册表（46 张）

绝大多数登记走同一个形状：

```csharp
XxxMirrors.Registry.Register<TYourType>(handler);
```

46 张注册表按域分布在 `src/Engine/InCombat/Mirrors/` 下：

死亡后生成单位的镜像应保持原生生成时点。例如补货由 `AfterDeathMirrors` 调用分支生成入口，旧个体仍在阵容中，其最大生命参与替补生命判重。把生成延后到阵容清理后，即使 RNG 调用次数相同也会改变抽样结果；登记镜像时应同步移除原领域补偿中的同一生成动作。

逐次出牌完成效果也应由 `AfterCardPlayedMirrors` 的对应分派独占。温柔在该 Hook 更新计数并扣除属性，回合末仍使用既有领域计数恢复；父牌的历史扫描可能包含已经结算的内层自动牌，不能再通过该范围给内层牌重复施加效果。属性施加需遵守每次原生命令的战斗结束条件。

历史敏感计算变量需要冻结根历史并加上分支新增事件。谋杀的实现读取 `RootCombatHistorySnapshot.CardsDrawn` 与模拟器抽牌事件；原生完成初始抽牌或后续动作后，旧预测根的倍率仍保持不变。只在实机停住时做一次差分会漏掉这类问题，验证时应包含根捕获后的实机推进与 Fork 隔离。

| 目录 | 注册表数 | 覆盖什么 | 你多半要用的 |
|---|---|---|---|
| `Hooks/` | 39 | 战斗 hook：攻击、格挡、伤害、死亡、卡牌、球体、回合边界 | 按你重写了哪个 hook 挑，例如 `AfterDamageGivenMirrors` |
| `Cards/` | 4 | 出牌、可打出性、回合结束留手、结算落点 | `CardOnPlayMirrors`、`CardIsPlayableMirrors` |
| `Potions/` | 1 | 药水使用 | `PotionOnUseMirrors` |
| `Enchantments/`、`Afflictions/` | 各 1 | 附魔与病症的出牌效果 | 少见 |

**回合开始重置能量之后的能力结算走 `Hooks/Resources/AfterEnergyResetMirrors`。** 这一张是从
`PersistentPowerSupport` 里那个写死五个原版类型的 switch 改过来的，所以以前第三方能力在这个
时点既没有登记入口，漏了也不报——别的钩子漏登记会记一条 `MethodNotMirrored` 风险，那个 switch
不经过注册表，只是静默跳过。重写了 `AfterEnergyReset` 的能力（每回合少一点能量、多一点能量、
多抽一张这一类）现在必须在这里登记。层数为零的能力不分发，和原版每个重写第一件事都是空转一致。

**注意目录里的文件数比注册表多。** `Cards/` 下有十几个 `*Mirrors.cs`，但注册表只有 4 张——
`BespokeCardMirrors`、`CardGenerationCardMirrors` 这些是**处理器文件**，它们往
`CardOnPlayMirrors.Registry` 这张共享注册表里登记，自己不持有注册表。找登记入口时认
`static readonly Registry Registry` 这个字段，不要认文件名。

**怎么知道自己要登记哪几个。** 把你的每个类型对基类虚方法的重写列出来，和这 46 张表逐一对照。
只重写了求解器不分发的方法，不用登记；重写了它分发的方法，就要登记。这一步不要靠印象，
要交叉核对——漏一个的表现是「效果看起来正常但其实没发生」。

同一张表里 `Register` 用的是 `Dictionary.Add`，**重复登记会抛异常**，不会静默覆盖。

Hook 分发会省略当前原版类型继承的默认空回调，但保留第三方/动态类型的完整回调顺序和既有登记流程。原生与领域监听表仍保留全部成员；关键字查询仅在所有接收者均未参与 `TryModifyKeywordsInCombat` 时省去原生空调用。每次根捕获重新检查相关 `AbstractModel` 基方法和 `Hook.ModifyKeywordsInCombat` 的 Harmony 补丁，有补丁或不透明 BaseLib CardModifier 时旁路。类型布局在同一根的有界表中复用，完整类型顺序逐项相等才命中，只存元数据、不保留任何分支 Model。原生监听表可在内部按前段与卡牌/球后段拼接，但顺序不变；不透明 CardModifier 仍完整重建，附着监听追加器拿到完整列表，不能把新增 Power 的插入位置限定在原生前段。该优化没有增加原本不支持的补丁或 subscriber 适配。

`PowerModel.GetTypeForAmount` 的局部 IL 优化只移除两处同类型枚举比较的装箱。虚拟 `StackType`、`Type`、`AllowNegative` getter 的次数与顺序及 decimal 分支保持原样；方法体不符合精确指令形状或比较内部存在控制流入口时保留原 IL。这没有增加 Power 登记点，也不缓存第三方 getter 的结果。
