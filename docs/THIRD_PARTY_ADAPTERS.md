# 第三方 Mod 适配手册

写给想让战斗路线求解器看懂自家 Mod 的作者。

求解器不认识任何第三方内容。它靠一套**镜像**（mirror）在自己的模拟里重现游戏行为，而镜像是
按类型登记的。你的牌、Power、遗物、药水没有登记，求解器就只能退化处理，路线会算错。

这份文档讲：默认会发生什么、有哪些登记点、登记的纪律、怎么验证自己做对了。

HeavenlyDrill 的 OnPlay 使用精确镜像，先解析分支 X 值及修正，再按卡牌 Energy 阈值决定攻击次数翻倍。修改该卡的阈值或攻击流程需提供对应语义登记，通用“X次攻击”推断不足以表达条件翻倍。EndOfDays 的领域补偿在每次施加灾厄后结算能力数量变化监听器和死亡，再判断处决；登记类似监听器时应保留原版 await 时点。

## 0. 先判断你要不要读下去

| 你的 Mod | 要做什么 |
|---|---|
| 清单里 `affects_gameplay: false`（纯美术、UI、音效） | **什么都不用做**，自动放行 |
| 只改地图、进幕、事件、休息处、商店这类战斗外内容 | **什么都不用做**，求解器会判定它对战斗惰性 |
| 加了牌、Power、遗物、药水、敌人，或改了战斗数值 | 往下读 |

前两条是自动的。第三条不做适配的话，装上你的 Mod 之后求解器会直接停在
「检测到不兼容的第三方 Mod」，玩家用不了。

项目明确拒绝的玩法 Mod 优先于上述通用放行条件。当前 `WheelchairSpire`、`PengoTarot`、`BetterCharacterRelics` 按 Mod ID 或已加载程序集名识别，在根捕获时直接报告名称和不兼容原因；不依据 `affects_gameplay: false` 放行。本批不为这些 Mod 提供战斗适配。搜索、部署和回合准备捕获此异常时均使用专用提示，报告账本只记录不兼容类别，不引导玩家上传日志。包内仅出现其他 Mod 的名字或恢复环境不匹配，均不足以认定该 Mod 是某个偏差的原因。

Power 的原版克隆会重置 `_internalData`。跨根保留的数据必须从原生来源捕获：例如本批苍蓝星球的已触发标记，以及 DarkEmbrace 的虚无消耗延迟计数。DarkEmbrace 后续按实际事件累计并在回合末清零，不能用结束回合前的牌数代替此状态。

## 1. 求解器默认怎么对待未知内容

### 1.1 门禁：先让 Mod 进得来

求解器扫描所有 ModHelper 战斗 hook 订阅者。放行有三条路：

1. 清单 `affects_gameplay: false`；
2. `PredictionModHookSubscriberInertness.IsCombatInert` 判定为战斗惰性——只重写了战斗外的
   hook，或者只重写了战斗开始 / 战斗结束 hook（前者的效果已经落在被捕获的根状态里，后者在
   胜负判定之后才分发，求解器搜到战斗结束就停）；
3. 在 `PredictionModHookSubscriberCapture.KnownPreRootSubscriberTypeNames` 白名单里。

三条都不满足就抛 `IncompatibleGameplayModException`，整个求解器停摆。

> **当前限制。** 第 3 条那份白名单是私有静态集合，没有公开登记入口。目前只能靠 publicizer
> 写进去。这是明确要补的扩展点之一，见第 6 节。

### 1.2 镜像：进来之后每个类型的五种下场

每个被镜像的虚方法都有一张按**精确运行时类型**索引的注册表。查一个类型会得到五种结果之一
（`MirrorDispatchKind`）：

| 结果 | 含义 | 后果 |
|---|---|---|
| `NotOverridden` | 这个类型没有重写该方法 | 走基类行为，**正确**，不用管 |
| `Handled` | 有登记的镜像 | **正确**，这是你要达到的状态 |
| `Inferred` | 没登记，但结构上能推断出一个尽力而为的实现 | 可能对，求解器**记一条风险** |
| `Ignored` | 人工复核过，确认对预测无影响 | 正确，静默 |
| `Unsupported` | 有重写但没有安全的预测实现 | 求解器**记一条风险** |

**风险不是静默错误。** 求解器会在路线上打红字标明「这里有未镜像的效果」，玩家看得见，日志里
也有 `COVERAGE source=... method=... reason=...`。但红字**只是显示**——它不会把不可能的续接从
搜索里去掉。凡是会改变「接下来还能做什么」的效果（强制结束回合、额外回合、让某张牌打不出），
必须真的建模，只记风险不够。

### 1.3 只读 hook 会自动回落

`ModifyUnblockedDamageTargetMirrors` 是显式例外：代伤目标依赖分支存活值，不能回落 live。`DieForYouPower` 已登记精确实现；新增覆盖须登记返回目标的分支状态处理器，未登记覆盖直接抛出不支持。各监听器收到上一个返回目标，分发不受战斗结束保护提前跳过，不得通过临时删除能力来关闭代伤。

`Modify*` / `Should*` 这类只读 hook 中，允许原实现回落的入口会调用 Mod 自己的实现。
适配者仍须核对读取的数据属于当前预测分支；只读方法也可能读到 live 手牌、Power 或费用。
姿态伤害倍率、费用修改等效果只有在完整分支差分通过后，才能认定原实现回落适用。

`CardIsPlayableMirrors` 使用独立的镜像分派：继承基类的牌返回 `true`；有显式登记的重写执行
对应镜像；未登记的重写记录 `MethodNotMirrored`，并返回调用方提供的 `true`。这条覆盖提示
用于暴露缺失语义，适配者必须登记真实可打出条件，才能保证搜索按预测手牌判断合法性。

## 2. 登记点总表

### 2.1 统一形状的镜像注册表（45 张）

绝大多数登记走同一个形状：

```csharp
XxxMirrors.Registry.Register<TYourType>(handler);
```

45 张注册表按域分布在 `src/Engine/InCombat/Mirrors/` 下：

新增 `Hooks/Damage/ModifyUnblockedDamageTargetMirrors.Registry.Register<T>`，处理器接收 `ModifyUnblockedDamageTargetMirrorContext`（分支 State、Target、Amount、Props、Dealer），返回 `Creature`；按精确类型注册，不改变能力实例或当前量。

死亡后生成单位的镜像应保持原生生成时点。例如补货由 `AfterDeathMirrors` 调用分支生成入口，旧个体仍在阵容中，其最大生命参与替补生命判重。把生成延后到阵容清理后，即使 RNG 调用次数相同也会改变抽样结果；登记镜像时应同步移除原领域补偿中的同一生成动作。

玩家死亡现在从通用死亡入口单独通知领域 Power 清理，时点在 AfterDeath 之后、球与宠物处理之前；敌人清扫不会负责玩家。`CompletePlayerDeath` 是内部效果接收合同，不是新的第三方登记点。宠物现同样在 AfterDeath 之后通过 `ICombatPredictionEffectSink.RemovePowersAfterDeath` 调用既有领域清理，保留 `ShouldPowerBeRemovedAfterOwnerDeath` 返回 false 的能力；这也是内部合同，不提供第三方注册口。注册死亡钩子时不得依赖死亡后仍能读取已移除的玩家 Power；[原生证据](performance/simulation-player-death-20260911.md)。

逐次出牌完成效果也应由 `AfterCardPlayedMirrors` 的对应分派独占。温柔在该 Hook 更新计数并扣除属性，回合末仍使用既有领域计数恢复；父牌的历史扫描可能包含已经结算的内层自动牌，不能再通过该范围给内层牌重复施加效果。属性施加需遵守每次原生命令的战斗结束条件。

历史敏感计算变量需要冻结根历史并加上分支新增事件。谋杀的实现读取 `RootCombatHistorySnapshot.CardsDrawn` 与模拟器抽牌事件；原生完成初始抽牌或后续动作后，旧预测根的倍率仍保持不变。只在实机停住时做一次差分会漏掉这类问题，验证时应包含根捕获后的实机推进与 Fork 隔离。

| 目录 | 注册表数 | 覆盖什么 | 你多半要用的 |
|---|---|---|---|
| `Hooks/` | 38 | 战斗 hook：攻击、格挡、伤害、死亡、卡牌、球体、回合边界 | 按你重写了哪个 hook 挑，例如 `AfterDamageGivenMirrors` |
| `Cards/` | 4 | 出牌、可打出性、回合结束留手、结算落点 | `CardOnPlayMirrors`、`CardIsPlayableMirrors` |
| `Potions/` | 1 | 药水使用 | `PotionOnUseMirrors` |
| `Enchantments/`、`Afflictions/` | 各 1 | 附魔与病症的出牌效果 | 少见 |

**注意目录里的文件数比注册表多。** `Cards/` 下有十几个 `*Mirrors.cs`，但注册表只有 4 张——
`BespokeCardMirrors`、`CardGenerationCardMirrors` 这些是**处理器文件**，它们往
`CardOnPlayMirrors.Registry` 这张共享注册表里登记，自己不持有注册表。找登记入口时认
`static readonly Registry Registry` 这个字段，不要认文件名。

**怎么知道自己要登记哪几个。** 把你的每个类型对基类虚方法的重写列出来，和这 45 张表逐一对照。
只重写了求解器不分发的方法，不用登记；重写了它分发的方法，就要登记。这一步不要靠印象，
要交叉核对——漏一个的表现是「效果看起来正常但其实没发生」。

同一张表里 `Register` 用的是 `Dictionary.Add`，**重复登记会抛异常**，不会静默覆盖。

Hook 分发会省略当前原版类型继承的默认空回调，但保留第三方/动态类型的完整回调顺序和既有登记流程。原生与领域监听表仍保留全部成员；关键字查询仅在所有接收者均未参与 `TryModifyKeywordsInCombat` 时省去原生空调用。每次根捕获重新检查相关 `AbstractModel` 基方法和 `Hook.ModifyKeywordsInCombat` 的 Harmony 补丁，有补丁或不透明 BaseLib CardModifier 时旁路。类型布局在同一根的有界表中复用，完整类型顺序逐项相等才命中，只存元数据、不保留任何分支 Model。原生监听表可在内部按前段与卡牌/球后段拼接，但顺序不变；不透明 CardModifier 仍完整重建，附着监听追加器拿到完整列表，不能把新增 Power 的插入位置限定在原生前段。该优化没有增加原本不支持的补丁或 subscriber 适配。

`PowerModel.GetTypeForAmount` 的局部 IL 优化只移除两处同类型枚举比较的装箱。虚拟 `StackType`、`Type`、`AllowNegative` getter 的次数与顺序及 decimal 分支保持原样；方法体不符合精确指令形状或比较内部存在控制流入口时保留原 IL。这没有增加 Power 登记点，也不缓存第三方 getter 的结果。

### 2.2 战略估值：会改变出牌顺序的 Power

```csharp
StrategicEffectMirrors.Register<TYourPower>(requirements, evaluate, host);
```

只有当你的 Power **收益取决于它和别的动作的先后关系**时才需要。详见
[第三方 Power 的战略估值登记](third-party-strategic-effects.md)。

`StrategicEffectRequirements.AttackHits` 可请求可达攻击命中数；`StrategicEffectContext.AttackHits` 在请求后提供估值，未请求时为 null。它包括已审查的原版多段与小刀生成，第三方攻击使用普通单次命中估计，不能当作真实攻击结算。`ExhaustDrawPlays` 是黑暗之拥在禁抽、虚无顺序下的抽牌机会估值；这些字段只服务保路，不改变 Hook 镜像语义。

不登记的后果：求解器按叠加层数记一点 `ScalingPotential` 兜底。对大多数 Power 够用；对
「自己不给甲、但让后续攻击给甲」这类会被排到错误位置。

### 2.3 药水的玩家选择

```csharp
PotionChoiceMirrors.Register<TYourPotion>(spec, apply);
```

只有当你的药水会让玩家当场做选择时才需要。不登记的后果很硬：`PotionChoiceSupport.RequiresChoice`
对第三方类型恒为 `false`，于是求解器**根本不为它开搜索分支**——它会把这瓶药当成一个没有收益的
动作，随手插在路线里的某个位置。药水自己的 `PotionOnUseMirrors` 镜像补不了这个：等那个钩子触发
的时候，「要不要开分支」早就已经被否决了。

两个委托：

- `spec(simulator, potion)` 返回一个 `CardChoiceSpec`：候选、上下界、效果。候选**必须是玩家在
  原生页面上真正看到的那几张，顺序也要一致**，否则部署时按卡牌令牌在页面上定位会错位。
  下界给 0 表示「可以一张都不选」。
- `apply(simulator, potion, choice)` 按选中的结果在模拟里施加效果，返回是否已经结算完
  （还有嵌套选择挂起时返回 `false`，和原版同一口径）。

效果用 `PlanChoiceEffect.ModDefined`。部署侧按卡牌令牌在原生页面上定位，本来就与效果无关；
这个值只是明确表示「结算由登记方负责」，别的效果分支不会误接手。求解器自己从不产生这个值。

登记之后，你的药水和原版带选择的药水走同一条通道：搜索按你的 spec 展开分支、把选中的结果记进
计划、部署时照常应答原生页面，而效果由你的 `apply` 施加——求解器不需要认识任何第三方效果。

**一个真实例子。** 观者的形态药剂让玩家在平静和愤怒之间二选一。原版实现里比的是引用相等
（`val == calmChoice`），但两张选项牌是两个不同的类型、各只有一张，所以按类型判完全等价。

不登记的代价实测过：鬼祟珊瑚群那一场，求解器第 1 回合 `max_block=14 actual_block=3`、掉 11 血；
手打是「爆发+ 进愤怒 → 停顿 3+9=12 甲 → 如水 → 药水选平静退出愤怒」，如水在回合结束因为平静
再给 5 甲，17 甲挡掉 14 点，0 掉血。求解器不肯进愤怒的判断在它自己的世界观里是对的——进去了
退不出来就是挨双倍伤害；它只是不知道那瓶药能退出来。

### 2.4 从给定牌堆候选中弃牌

`ICombatPredictionChoiceSink.ResolvePileDiscardChoice(simulator, sourceId, player, sourcePile, options, maxBranches)`
供镜像处理器提交可选弃牌请求。`options` 是效果当时真正展示的有序候选，例如抽牌堆顶的几张牌。
允许空选，选中牌进入弃牌堆，数量范围为 `0..options.Count`。返回 `false` 表示选择挂起，调用方
应向上传播未完成状态；搜索补齐选择后会从稳定父节点重放，返回 `true` 才继续后续效果。

该入口沿用已有动作选择、计划记录和原生页面部署通道。手牌之外的弃牌排序使用源牌堆平均牌值
减去被弃牌牌值，并加上弃牌触发收益。`maxBranches` 对排序后的候选设置保留上限，省略时沿用
现有枚举策略；传 `1` 只保留排序第一项，会牺牲其他选择路线，适配者应使用目标场景验证取舍。

### 2.5 卡牌的玩家选择

此入口随 PR #56 合入，并于 `0.32.0` 发布。使用此入口的适配 Mod 应将 CombatSolver 最低依赖设为 `0.32.0`。

```csharp
CardChoiceMirrors.Register<TYourCard>(spec, apply);
```

和药水那条是同一堵墙的两面。`CardChoiceSupport.GetSpec` 是按原版卡牌类型写死的 `switch`，
默认分支返回 `null`，也就是「这张牌没有选择」。第三方卡牌落到那里就是这个答案，于是它的选牌
效果**永远不会被展开成搜索分支**：牌照样打得出去，效果在模拟里静默变成空操作。卡牌自己的
`CardOnPlayMirrors` 补不了这个——选择的展开发生在出牌路径上，不在效果镜像里。

两个委托：

- `spec(simulator, playedCard, card)` 返回一个 `CardChoiceSpec`。
- `apply(simulator, combat, playedCard, card, choice)` 施加效果，返回是否已经结算完。

比药水那条多两件要注意的事：

1. **升级等级必须对。** 部署时按 CardId 加升级等级在原生页面上定位选项。三选一这类牌通常会让
   三张选项跟着本牌一起升级，`spec` 里就要把选项牌也升级，否则部署定位不到。
2. **选项牌不在任何模拟牌堆里。** 所以 `apply` 拿到的是计划里的 `PlanCardToken`，不是
   `PredictedCard`；按 CardId 自己认，求解器不会替你解析。数值要读就从选项牌自己的
   `DynamicVars` 上读，不要写死。

效果同样用 `PlanChoiceEffect.ModDefined`。求解器自己从不产生这个值；如果它出现在卡牌选牌上
而没有登记方认领，结算会直接抛，不会静默空操作。

**一个真实例子。** 观者的许愿是 3 费，打出后在「力量 +3」「多层护甲 6」「金币 25」之间三选一
（升级后 4 / 8 / 30，三张选项牌各自的 `MagicNumber` 就是这三个数）。三个选项的单位完全不同，
但都不需要新的估值刻度：力量和多层护甲本来就是 Power，金币走求解器现成的长期资源刻度
（`GainPlayerGold` 加 `RecordLongTermResource`，`贪婪之手` 就是面值直记）。登记成三个真分支之后，
「值不值这 3 点能量」和「三个里挑哪个」都由搜索自己比出来，不需要写任何策略规则。

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

### 2.7 局外成长来源的独立额度

下一版本的有限击杀收益早停只识别原版单一来源 THE_HUNT / FEED / HAND_OF_GREED；命中第三方 `hasTarget` 或混合来源时继续原成长搜索，不把一次收益套成第三方来源的完成条件。接口保持不变。
战损目标早停默认开启；成长来源仅在本场实际卡牌命中 `hasTarget` 且考虑局外收益时阻止早停。只保存该来源的非零额度不算实际目标；第三方无需修改登记签名，继续提供准确的纯卡牌判据。
原版禁忌魔典已包含独立删牌收益额度，按每次成功增加战后删牌奖励计数。至亮之焰的单场最大生命消耗上限属于独立成本约束，不使用成长收益向量表示负收益，也不受 IgnoreLongTermRewards 影响；它不改变第三方成长来源登记接口。

尚未发布，登记入口在下一版本。登记应在 Mod 初始化、任何搜索之前完成；搜索期间保持登记表不变。

```csharp
// 加载时登记一次，把句柄存下来。
private static GrowthSourceHandle _diligence;

_diligence = GrowthSourceMirrors.Register(
    "YourMod.Diligence",                        // 持久化键，建议带 mod 前缀
    () => ModelDb.Card<YourDiligenceCard>(),    // 侧栏这一行的图标和标题，延迟调用
    card => card is YourDiligenceCard && card.DeckVersion != null);

// 第四个参数是标题覆盖，也是延迟调用，参数就是上面那个函数取回来的牌。
// 只在「牌名说明不了这个来源」时才填，比如原版把黏稠强化那一行显示成「防御 + 强化名」。
_wishGold = GrowthSourceMirrors.Register(
    "YourMod.WishGold",
    () => ModelDb.Card<YourGoldWishOption>(),
    card => card is YourWishCard,
    card => ModelDb.Card<YourWishCard>().Title + "·" + card.Title);

// 收益真的到手时记一次。
combat.RecordGrowthReward(_diligence);
```

成长策略解决的是这类问题：贪婪之手、巨镰、遗传算法这些牌，收益落在**这场战斗之外**——金币、
永久升级、局外强化。求解器默认只看本场战斗的血量与胜负，于是「多挨几点伤害换一次永久升级」
一律判成亏。侧栏让玩家给每个来源单独填一份「每次收益允许的额外战损」，搜索据此在打分里给这条
线路记一笔 HP 信用额度。

原版九个来源写死在 `GrowthSource` 枚举里，`GrowthValues` 是与之对应的九个 int 字段。局外成长类
卡牌很多 mod 都有，它们全部落不进那个枚举：既拿不到自己的额度栏，收益也记不进
`SimulatedCombatState.GrowthRewards`。**表现不是「少了个选项」，而是搜索必然避开这张牌**——
付出的血看得见，换回来的东西在打分里根本不存在。

登记之后你会得到四样东西：

- 成长策略侧栏多一行，有自己的图标、标题和额度输入框，排在原版九行之后、按登记顺序；
- 额度按你给的 id 存进设置文件，也进问题包的有效策略和路线缓存；
- `GrowthValues.HasTarget` 认得你的牌，于是「打到可接受战损就提早收手」那条捷径会被关掉——
  否则搜索会在还没摸到你这张牌之前就收手；
- 计数进状态指纹，只在「有没有拿到这次收益」上不同的两条分支不会被当成同一个状态去重。

#### 四条约束

1. **id 要稳定。** 它是持久化键，改 id 等于换来源，玩家原来填的额度不再生效。为此额度按 id
   存而不是按登记序号存：玩家临时停用你的 mod 时，那份额度会原样留在设置文件里，重新启用后
   还在，不需要再填一遍。
2. **`RecordGrowthReward` 只在收益真的到手时调用。** 额度是「每次成功收益」的单价，多记一次
   就等于凭空多出一份额度，搜索会拿它去换真实的血。原版的口径可以照抄：斩杀类要求满足致命
   条件（`WasFatalKill`），永久成长类要求那张牌有局外牌组实例（`card.DeckVersion != null`），
   炼制药水要求成功入槽。
3. **`hasTarget` 必须是纯判断。** 它会对玩家牌组里每张牌调用。永久成长一类记得跟原版一样要求
   `DeckVersion != null`——战斗里临时生成的副本升级了也带不出战斗。
4. **金币一类要两处都记。** 局外成长额度和长期资源刻度是两回事：`RecordLongTermResource` 记的是
   「这条线路带走了多少局外价值」，`RecordGrowthReward` 记的是「为这次收益可以额外付多少血」。
   原版贪婪之手两个都调，第三方的金币收益照做。

取牌或取标题函数抛异常不会连带侧栏起不来：那一行退化成「没有图标、标题显示 id」，额度照样能
填、照样进搜索，日志里留一条 warn。这是这个入口唯一一处「装一半」，因为它只影响显示。

**两个真实例子，都在观者。** 勤学精进是永久升级，和原版遗传算法、巨镰同一类，直接登记就位。
许愿三选一里的金币那一支和贪婪之手同一类，除了原来就有的 `RecordLongTermResource` 还要补一次
`RecordGrowthReward`——只记长期资源的话，搜索知道这条线路带走了金币，却不知道玩家愿意为它付血。
### 2.8 移除估值的偏置

尚未发布，登记入口在下一版本。加载时登记一次即可。

```csharp
CardRemovalValueMirrors.Register<YourStrike>(-10d);
CardRemovalValueMirrors.Register<YourDefend>(-10d);
```

净化、洗炼这类**移除**选择按 `CardChoiceSupport.RemovalPriority` 从低到高排序，估值低的先被
移除。通用估值把伤害记满、格挡打八折，于是一张 6 伤害的起手打击得 `6.0`，比一张 5 格挡的起手
防御（`4.0`）还高——按通用估值排，先被烧掉的会是防御。原版五个角色的实战优先级正相反，所以
`BasicCardRemovalValue` 用一张按类型写死的表把这十张起手牌压回正确的相对位置。

那张表**只列原版十张**。它的注释里写明了理由：其他来源的打击、防御「强弱取决于各自的机制，
这里没有依据替它们排序」。这个判断对求解器成立，**对你不成立**——你知道自己那张牌是不是起手牌。
所以这里开一个登记点，让你自己声明。

**登记的是偏置，不是绝对值。** 最终估值 = 通用估值 + 你给的偏置，所以牌自身的梯度保住了：
升级过的起手打击伤害更高，加同一个偏置之后仍然比未升级的那张更靠后被烧。

**负偏置是这个入口的重点。** `ChoicePriority` 对消耗返回 `-Σ RemovalPriority` 并按降序取分支，
所有估值都是正数时，「一张都不选」（0）永远排第一——消耗在选择排序里从来只有「少亏一点」，
没有正收益。把一张真正的废牌压到负值，「烧它」这条分支才会排到「不烧」前面。

**为什么不是让你声明「这是起手打击」。** 原版那张表把起手防御排在起手打击之后（格挡 × 1.2、
伤害 × 2/3），因为原版五个角色留防御更划算。这个相对顺序**不通用**：观者靠姿态和心灵堡垒起甲，
一张普通防御比一张打击更该烧。类别抽象会把原版的假设强加给你，偏置不会——通用估值本来就把格挡
打了八折，同样偏置下防御自然排在打击前面。

**这个入口不怕被滥用。** 把自己的牌估低等于让求解器优先烧掉它，估高等于让它留在牌库里堵手，
两个方向的代价都由你自己承担。绝对值上限 `100`，够表达「这张牌白占位置」，又不至于一次手滑让
求解器烧光牌库。

**不登记的后果是静默的。** 你的起手打击按通用估值算成一张有伤害的好攻击牌，于是净化永远不会
先烧它——它不报错、不打红字，只是求解器再也不会替你压牌库。实测一场女王：玩家手打消耗掉三张
观者打击、把全知与内心宁静留在牌库里；求解器反过来消耗了全知、内心宁静、痛击，把四张打击留着。
两边同样有疾风连击 4，只有前者的牌库能持续转起来。

**负偏置还有第二个作用：那张牌按牌库杂质计。** 状态牌和诅咒本来就进 `liveDeckClutter`，只要还
占着牌堆就扣分，所以消耗掉它们是正收益。别的牌不进那一项——于是消耗一张非状态非诅咒的牌在打分
里的收益**正好是零**（`retainedAttackValue` 有上限，攻击牌多的时候早就顶满，少一张也不掉），
「打出净化消耗两张废牌」严格劣于「不打净化」，省下那点能量总是更划算。排序偏置排不出一个本来就
不存在的收益，所以负偏置同时表示「这张牌占着牌堆就是负担」。

原版那张写死的表优先：已经列进去的类型不会被登记表改写。登记表为空时下游一行都不多走。

**这个入口解决的是「别烧错、该烧的要烧」，不解决「为了压出无限而主动烧牌」。** 后者要的是对
「移除之后牌库能不能自持」的判断，那是求解器的估值主干，见第 6 节。

### 2.9 遗物与 Modifier 的分支状态

`ModelPredictionStateMirrors.RegisterRelic<TModel, TState>` 与 `RegisterModifier<TModel, TState>`
按精确类型登记根捕获、实机字段和预测字段。状态通过现有 `PredictionStateStore` Fork，
同一字段口径进入搜索指纹与 `ContinuationStamp`，按实例所属位置绑定，不合并同类型计数。
首次根或续用捕获后拒绝继续登记；未捕获状态不回落到 live 值。

此接口不放行 Mod、补丁或 Hook，不扩展遗物／Modifier 的中途增删。
完整签名、对象重映射、字段格式及验证边界见[模型状态适配](third-party-model-state.md)。
与其他内部镜像入口一样，外部程序集仍需要 publicizer；本接口尚未发布。

### 2.10 还没有登记入口的地方

见第 6 节。目前只能 Harmony 打补丁，或者等对应的扩展点合并。

## 3. 登记的纪律

这几条不是风格建议，是踩过的坑。

### 3.1 加载时一次性登记完

注册表**按精确运行时类型缓存查询结果，而且 `Register` 不会让缓存失效**。一旦某个类型被查过
一次（拿到 `Inferred` 或 `Unsupported`），之后再登记也不会生效，而且不报错。

所以：在 Mod 初始化时把所有登记做完，绝不在战斗中途登记。

`ModelPredictionStateMirrors` 不使用上述延迟分派缓存，而是在第一次根或续用捕获后冻结整张登记表；
迟到登记明确抛异常。两类入口的共同要求仍是初始化期间一次完成登记。

### 3.2 失败要关死，不要装一半

自检不通过时**一个镜像都不要登记**。装一半比不装更糟：求解器会拿着一部分正确的镜像给出看起来
可信的路线，缺掉的那部分静默变成空操作。全都不装的话，求解器会明确停在门禁上并显示原因，
玩家至少知道出了事。

同理，解析不到 Harmony 目标方法就抛异常让整层注册失败，不要跳过继续。

### 3.3 按反编译出来的实现写，不要照卡面文字猜

卡面文字和实现经常不一致：触发时机、目标选择、数值来源、结算顺序。逐条对照反编译源码写，
一张牌一个方法、一行一效果、按原版的调用顺序排列，这样可以逐行复核。

典型的坑：变量键名。`PowerVar<T>` 单参数构造生成的键是 `typeof(T).Name`（例如
`VulnerablePower`），不是卡面上显示的那个词。写错会让整次搜索失败。

### 3.4 钉死你依赖的版本，并在运行期自检

求解器的内部接口会变。适配层应当：

- 构建期引用确定版本；
- 运行期核对自己用到的那几个方法签名和字段还在不在，不在就干净地拒绝加载。

同理，如果你在适配**别人的** Mod，按文件哈希钉死比按版本号更稳——作者不一定每次改动都升版本
号，而一个没升版本号的签名改动会让某张牌变成「没有效果但看起来正常」。

### 3.5 时机比数值更容易错

抽牌发生在触发它的那张牌离开出牌堆之前还是之后、Power 在这张牌自己结算之前还是之后到位、
「上一张牌」是本回合的还是整场的——这些一错，数值全对但结果不对。写注释说明你选的时机和依据。

## 4. 怎么验证自己做对了

### 4.1 两条验收标准

**不要用胜率或手感做验收。** 镜像低估自己的伤害会让求解器打得保守，于是活得久——这种路线能
通过手感检验，通不过严格 diff。

标准是：

1. **严格 diff 零差异**：模拟的终局状态和真实终局状态逐字段相等。
2. **`PredictionGaps` 里非补偿项为空**：求解器自己不报告任何未镜像效果。

胜率是在这两条都干净**之后**才有意义的指标，用来抓 diff 抓不到的东西，比如某个 Power 在估值
函数里定价错了。反过来先看胜率，会让你在错误的地方停下来。

### 4.2 夹具要能自己验算，而且要有反向对照

好夹具的判据落在能用算术自己验的量上——能量够不够打第二张牌、格挡数值、正好击杀的回合数——
而不是「跑起来不报错」。

**每条夹具都要做一次反向对照**：把你要验的那行登记注释掉重新构建，夹具必须不过；加回来必须
过。没做过反向对照的夹具证明不了任何事。

无头夹具的跑法见 [HEADLESS_TESTING.md](HEADLESS_TESTING.md)。

### 4.3 用玩家的问题包，不要只看描述

求解器自带问题包导出，里面有完整路线、逐检查点状态、日志和一份自动分类（例如
`BetterWorldline 预计战损 11 → 0` 就是「玩家手打比求解器的路线好，好 11 点血」）。
带问题包基本都能定位；只有文字描述通常不够。

## 5. 一个完整例子

观者 Mod 的「以手拒之」：打出后给目标挂一层反弹格挡，之后玩家每打中这个敌人一段就起
`Amount` 点甲。

**症状。** 手里以手拒之 + 两张打击，敌人这回合打 4 点。求解器给的顺序是
「打击 打击 以手拒之」，第 1 回合 `max_block=0`，白挨 4 点。第 2、3、4 回合都是
`max_block=4 actual_block=4`——层数一旦挂上去后面每回合都算得对，唯独挂上去的那一回合被浪费。

**排查。** 先确认镜像本身对不对：反弹格挡的钩子分发和逐条判定（目标判定、施加者判定、
`IsPoweredAttack`、`TotalDamage > 0`、受益者三级回退、`Unpowered` 不吃敏捷、不自减）都和反编译
出来的实现核对过，两条夹具锁住了这一半。**镜像是对的，坏的是排序。**

**根因。** `ClassifyActionOptionFamilies` 判一个动作算不算 `ImmediateDefense`，看四样：这次动作
的格挡增量、`ProjectedPlayerHp`、`PlayerHp`、`StrategicEffects.PreventionPotential`。以手拒之
打出的瞬间这四样一样都不动——它自己不给甲，而反弹格挡这层 Power 挂在**敌人身上**，设置估值那圈
原本只统计玩家自己身上的增益。于是它被归成一张纯 `ImmediateOffense`，和打击同族但伤害更低，
在族内代表里被打击压掉。

**修法。** 用 `StrategicEffectMirrors.Register<BlockReturnPower>(..., StrategicEffectHost.Enemy)`
登记估值。登记之后打出它会让 `PreventionPotential` 从 0 变正，于是它同时进 `ImmediateDefense`
族，不再被压掉。

**验证。** 夹具 `WATCHER-TALK-TO-THE-HAND-ORDERING`：以手拒之加两张打击、3 能量，判
`max_block >= 4`（以手拒之给 2 层，两张打击各 1 段，排最前面 = 4 甲，排中间 = 2，排最后 = 0）。
做过反向对照：注释掉那行登记，夹具不过。

**这个例子的一般教训**：现象是「AI 不会用这张牌」，根因既不在这张牌的镜像里，也不在搜索深度或
估值权重上，而在动作分类那一层。排查顺序应当是：先确认镜像对不对，再看它有没有被搜索看见，
最后才怀疑估值。

## 6. 已知的封闭开关

下面这些位置目前是按原版类型写死的开关，第三方登记不进去。要用只能 Harmony 打补丁，或者等
对应扩展点合并。列在这里是为了让你知道撞上了什么，而不是以为自己写错了。

| 位置 | 症状 | 状态 |
|---|---|---|
| `PowerLifecycleSupport.UsesNativeDurationSkip`／`SemanticallyRelevantSkipNextDurationTick` | 当前原生只有虚弱／易伤／脆弱消费该字段。其首次玩家施加在独占 Power 设置标记，普通、怪物及按类型入口不重复写影子集合；叠加不刷新、移除后重获重新设置、人工制品阻止不留状态。原键与续用文本共用分类，中毒无关标记不拆分状态。第三方新增使用该字段的 Power 需扩展此分类及隐藏状态合同；此处没有新注册表（[证据](performance/simulation-duration-state-20260911.md)） | 原版封闭语义分类 |
| `SimulatedCombatState.ApplyTemporaryStrengthLoss/Gain` | 仅接受原生 `TemporaryStrengthPower` 类型族；首次施加 Strength 在临时计数之前，随后按修正后偏移而非计数净变化处理回调，包含首次及叠加封顶。与普通 `Apply<T>` 共用准备／写入，不重复修正或 Artifact（[证据](performance/simulation-temporary-strength-20260911.md)） | 非登记入口 |
| `CorePowerSupport.ApplyCardPowers` | 部分卡牌 Power 补偿仍按原版类型封闭分发。闪躲翻滚消费该次 `CardPlay` 已记录的格挡命令返回值，按原版整数转换施加下回合格挡；不能用净格挡增量替代，也不能重新执行格挡修正 Hook（[证据](performance/simulation-deferred-block-return-20260911.md)） | 未开放 |
| `PredictionModHookSubscriberCapture.KnownPreRootSubscriberTypeNames` | 私有静态白名单，没有公开登记入口 | 待做 |
| `Prediction/Compact/CompactDiscardProjection` 的整根准入与 `CompactCardProgramCompiler` 的卡牌编译 | 实验支持已准入抽弃牌／洗牌／固定 Power 选牌；`CardEffectProgram` 是不可变有序指令，由独立编译器生成，新增生存者格挡后弃牌（[证据](performance/simulation-effect-program-20260911.md)）；显式攻击域增加主要敌人打击、中和、六种基础 Power 的值状态与死亡清理；现又准入 `FOOTWORK` 移除、`MALAISE` 的 X／消耗、`SUPPRESS`、`ULTIMATE_DEFEND`、`FINESSE` 和亡灵基础卡牌（[生命周期证据](performance/simulation-card-lifecycle-20260911.md)）。现支持三十三种精确卡牌类型，新增单体／群体中毒、虚弱、条件抽牌和虚无历史（[群体与条件指令证据](performance/simulation-conditional-powers-20260911.md)）。新增 `OUTBREAK` 的中毒触发／递减和 `CALCULATED_GAMBLE` 的整手弃抽／Sly 后续（[触发与弃抽证据](performance/simulation-poison-discard-20260911.md)）。新增 `BUBBLE_BUBBLE` 的目标中毒存在条件与 `MIRAGE` 的存活敌人中毒求和格挡（[Power 表达式证据](performance/simulation-power-expressions-20260911.md)）。新增 `DODGE_AND_ROLL` 的格挡返回值施加 `BlockNextTurnPower` 与 `TOOLS_OF_THE_TRADE` 计数／移除；正小数零层实例域由整根检查拒绝（[下回合计数证据](performance/simulation-compact-deferred-powers-20260911.md)）。新增 `PIERCING_WAIL` 的敌方临时力量施加、封顶偏移与消耗；只准入该精确 Power 的数量回调，其他临时属性类型仍拒绝（[证据](performance/simulation-compact-temporary-strength-20260911.md)）。新增 `CLOAK_AND_DAGGER`／`SHIV` 的固定模板生成、满手转弃牌、攻击和消耗；仅放行已证明的卡入场／生成回调，新增模型只在读取器中物化（[生成卡证据](performance/simulation-generated-cards-20260911.md)）。`BLADE_OF_INK` 仅在无中间观察者的闭包折叠为最终 `Inky` 模板，攻击后虚弱与混合模板恢复经[原生差分](performance/simulation-inky-cards-20260911.md)验证。新增正常 1 层 `Slither` 的本场绝对费用列表及 `CombatEnergyCosts` 值状态（[证据](performance/simulation-random-costs-20260911.md)），其他修饰不放行。既有人工制品可阻止负面施加，修改先于临时力量的内部效果，并保留数量／顺序／退休（[证据](performance/simulation-compact-artifact-20260911.md)）。蛇之戒及无已触及运行级覆盖的牌组监听前缀现可保留，牌组与战斗表分开审计（[原始根证据](performance/simulation-full-root-20260911.md)）。新增精确 `BURN` 及独立准入的手牌末尾阶段，保存状态抽取、累计失血、玩家死亡与显式入场顺序；该参数不提供第三方回调登记，也不表示所有动画模式或完整回合已支持（[手牌阶段证据](performance/simulation-hand-end-20260911.md)）。`MonsterEffectProgram` 现仅由 Testing 从单个精确 `MechaKnight` 根捕获四种指令体；无创建者生成、怪物攻击历史与完整状态已有[原生证据](performance/simulation-monster-commands-20260911.md)，没有泛化怪物登记口。确定性 AI 扩展只接收经 Testing 验证的精确四节点图，复制根当前招式、后继和日志；当前／日志与意图从值读取，仍无第三方 AI 登记表。Power 回合初始快照、下回合格挡兑现、敌方临时力量恢复和三种持续减益递减现有独立整根准入，包含 AfterSideTurnEndLate 审计及`StratagemPower`初始字段（[阶段证据](performance/simulation-power-phases-20260911.md)）；该接口没有第三方注册表。完整回合驱动有独立 Testing 准入，增加阶段／时间／历史重置、保留、临时 Sly 清理、起手与 Tools 暂停帧；精确闭包仅含单个机械骑士，玩家中毒、待抽牌与额外行动拒绝，20 分支／7 原生动作及冷根历史通过[完整回合对照](performance/simulation-rounds-20260911.md)。`Prediction/Compact/CompactPlanReplay` 由已准入根的 Search 接线，桥接已准入卡牌／结束回合计划，按正式实例键和选牌 token 匹配；不是第三方登记口。原始 30 牌、44 原生动作及 424 个已准入替代分支通过[完整路线对照](performance/simulation-full-route-20260911.md)，回合末本回合／上回合最后攻击牌分别进入原键。派生历史查询按回合、阵营及分支玩家回合界定窗口，消费者不应将上一阵营的缓存当成本阶段计数。仍拒绝未迁移 Hook、其他 Power、修饰与其他随机操作，没有第三方登记入口。完整状态与原生证据见[Power 阶段报告](performance/simulation-compact-powers-20260911.md)；生产搜索在完整根准入后选择紧凑后端，未准入域继续使用模型后端 | 测试原型，未开放 |
| `PredictionModPatchAudit.ValidateLoadedMods` | 明确拒绝 `WheelchairSpire`，没有外部放行入口 | 项目不兼容策略 |
| `DynamicVarCloneMetadataPatches` | 模拟克隆只优化已核对为空默认值的 BaseLib 提示/升级字段与 Ritsu 提示工厂；非空值照常复制，live 调用保持原框架行为。其他附加字段继续原有克隆逻辑，不属于此优化入口 | 精确框架适配 |
| `PlayerTurnEndLifecycle.RunPhaseTwo`、`CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects`、`FlushPlayerHandAtTurnEnd`、`TurnStartPowerSupport.TriggerAfterPlayerTurnStart`、`SimulatedCombatState.TriggerRelicsAfterPlayerTurnStart` | 回合边界的效果没有注册表 | 待做 |
| `SimulatedCombatState.TryPrepareExtraPlayerTurn` / `TryPrepareLiveExtraPlayerTurn` / `ConsumeExtraTurnSources` | 额外回合的来源硬编码，只认龙涎香和帕尔之眼 | 待做 |
| `CombatPredictionSimulator.OnPlayWrapper` | 出牌后补抽没有挂载点 | 待做 |
| `CardChoiceSupport.RemovalPriority` 的排序口径 | 移除类选择按**单卡**估值排，不看牌库其余部分；弃牌那一侧已经是「源牌堆平均值减本牌估值」的相对口径，消耗与转变没有。表现为求解器不会为了压出无限而主动烧牌。起手牌那一层已由 §2.7 打开，相对口径这一层仍然封闭 | 待做 |
| `ContinuationStamp.AppendCard` 的 `private=` 段与 `CombatBeamSolver.CaptureCardStateFingerprintForTesting` 的 `switch (preview)` | **卡牌**的隐藏字段按原版类型写死（利爪、基因算法、巨锤、狂暴、镰刀、疯狂科学），第三方卡牌的私有计数进不了指纹。Power 那一侧已有 `PowerHiddenStateMirrors`，见 §2.6 | 待做 |
| `SimulatedCombatState.AddTurnStartStates` 的 `switch (power)` | 原版 Power 隐藏计数按类型写死。第三方走 §2.6 的登记表进同一份指纹，本行只是记下原版那个 `switch` 本身仍然封闭 | 第三方已有入口 |
| `RelicPredictionStateSupport` 的原版类型分支 | 内置遗物状态仍按原实现处理；第三方遗物与 Modifier 的独立状态通过 §2.9 登记，不修改原版分支 | 第三方已有入口 |
| `GrowthSource` 枚举与 `SolverGrowthStrategyPanel.SourceCard` 的 `switch` | 原版九类成长来源按类型写死。第三方走 §2.7 的 `GrowthSourceMirrors` 拿独立额度、侧栏行和指纹，本行只是记下原版那个枚举本身仍然封闭 | 第三方已有入口 |

**这些开关新增或改动时，必须在同一个提交里更新这张表和本文档对应章节。** 见
[AGENTS.md](../AGENTS.md) 第 9 节。

## 7. 相关文档

- [架构与职责地图](ARCHITECTURE.md)：源码入口和所有权，`§4.2 Mirror` 是镜像层的位置。
- [战斗钩子覆盖目录](COMBAT_HOOK_COVERAGE.md)：求解器分发哪些 hook。
- [第三方 Power 的战略估值登记](third-party-strategic-effects.md)。
- [无头测试](HEADLESS_TESTING.md)：夹具怎么跑。
- [检查点回放](CHECKPOINT_REPLAY.md)：问题包怎么导入。

紧凑执行的封闭开关补充：`Prediction/Compact/CompactCombatRoot` 捕获完整模型准入后，由 Runtime/SearchBackendPolicy 把冻结状态交给 `CombatBeamSolver.CompactReplay`；未准入根明确报告模型后端。该构造器不是外部注册 API，不能通过只设置 policy 绕过 Hook、卡牌、遗物、Power 或动态生成闭包。兼容投影也不表示外部效果已迁移；[搜索集成证据](performance/simulation-search-backend-20260911.md)。

紧凑政策读取只消费该封闭根：合法性查询需要的新动态手牌、特殊目标或可出牌条件必须先纳入读取合同和准入，不能把 lane 上的旧根手牌当作变化后的手牌。当前准入没有 Enthralled 或相应动态条件；目标门仍使用同一领域合法性实现。

紧凑计划的待选状态现使用原 `TurnStartChoiceCursor` 协议；来源、效果、牌堆、上下文或阶段不符形成既有业务无效分支。只支持当前封闭域的固定弃牌／抽牌堆选择，没有新增第三方选择注册口。请求拥有私有预览，外部代码不能持有或修改 lane 模型；怪物意图的发布时点仍遵循原回合生命周期。

共享动作准备不新增第三方合法性入口，仍走原卡牌／目标条件。续用的可选完成读视图只接收经过准入的分支值与配套元数据上下文；增加新的可变药水、球、遗物或 Power 字段时须扩充合同，不能依赖当前闭包的不变假设。

运行时准入现在要求已表示模型的精确原生类型；第三方派生类不能借用基础类型的效果声明。非空药水槽当前使整个根选择模型后端，包含主动与自动药水；不能只检查 SearchablePotions。后端不适用只在准入时报告，执行出错仍停止搜索，没有逐候选回退或第三方绕过入口。[运行时证据](performance/simulation-runtime-backend-20260911.md)。

精神过载已移入显式 OnPlay registry，能量先于抽牌、能力后于抽牌；外部适配不得再在 `CardEffectSpecRegistry` 重复结算。通用新实例的玩家负面类型决定原生持续跳过标记，计数标记本身不进入持续等价键。紧凑封闭开关新增精确 `Neurosurge`／`NeurosurgePower`／`DoomPower`，但只允许已表示的能量、阵营开始和双方毁灭死亡相位；新增 `ModifyEnergyGain`、`AfterModifyingEnergyGain`、`AfterDiedToDoom` 观察者仍拒绝。没有开放第三方登记表；宠物、复活和未迁移遗物／药水不能借此准入。[完整范围](performance/simulation-necro-resources-20260911.md)。

奥斯蒂的根身份和“没有宠物”现在显式捕获，分支生成身份由原召唤映射持有；外部适配不能从 `player.Osty` 补读分支状态。死亡阵容保留判定包含 DieForYou，普通能力退休不延后至下一次召唤。[五步原生与双根隔离证据](performance/simulation-osty-ownership-20260911.md)。

紧凑宠物闭包现支持已捕获的精确奥斯蒂身份，以及其一层代伤能力和可选力量。`BODYGUARD`／`UNLEASH` 普通及升级值程序分别负责复活／增长和按存活宠物当前 HP／力量攻击；代伤使用玩家格挡，宠物死亡保留身份和代伤能力，其他能力退休，群体敌方效果仅遍历主敌人区间。`CompletedOstyReadBinding` 是内部值导入合同，保留最大生命映射的缺席／零值差别；不能用它执行自定义召唤。`ModifySummonAmount`、`AfterOstyRevived`、`AfterSummon` 新观察者须重新证明完整闭包；首次创建、其他宠物能力及未迁移遗物／药水仍拒绝，没有新增外部紧凑注册表。普通玩家回合包含保留的死宠物，额外回合只包含玩家，外部回合钩子不能假设参与者永远只有玩家。[范围和原生证据](performance/simulation-osty-values-20260911.md)。

紧凑遗物封闭开关新增精确 `BoundPhylactery.AfterEnergyResetLate`：只在完整回合且已有宠物根准入，捕获非负整数召唤量，能量重置后、抽牌前调用既有值召唤。`BeforeCombatStart` 的首次创建属于捕获前效果，不能据此移植任意召唤或早期宠物存在观察者。登记点未开放；原能量重置顺序及未知 Hook 拒绝保持。[原生及搜索证据](performance/simulation-pet-turns-20260911.md)。

紧凑卡牌封闭编译新增精确 `Cleanse`／`Afterlife`（共 35 类型），分别编译为召唤→抽牌堆消耗选择及召唤→自身消耗。消耗和洗牌检索虽来自相同牌堆，使用独立效果／来源合同；未知消耗观察者继续拒绝。没有新增第三方注册入口，已有 `CardChoiceMirrors` 和旧语义入口不变。[证据](performance/simulation-draw-exhaust-20260911.md)。


紧凑封闭编译新增精确 `Dirge`／`Soul`（共 37 类型）；生成位置属于不可变指令配置，普通／升级灵魂模板在根捕获，X 修正与生成观察者仍通过整根门禁。随机插入使用现有 Shuffle 流，兼容事件的生成记录现在携带目标牌堆和位置，模板从实例定义解析；此内部格式不构成第三方注册 API。没有新增外部登记入口；首次宠物创建及未表示的生成／升级观察者仍须单独适配。[证据](performance/simulation-dirge-20260911.md)。


紧凑封闭编译新增精确 `CaptureSpirit`／`Graveblast`／`Defile`／`Wisp`／`AscendersBane`（共 42 类型），没有新增第三方登记入口。直接失血与普通攻击分别保留属性及来源；生成命令和怪物预览生成入口有不同的结束门禁。未入堆生成身份不会进入战斗牌堆／监听器集合，也不标记为已移除。既有 `CardChoiceSupport.GetSpec` 的坟冢爆射分支现在在 `IsEnding` 时返回无选择，与原版 `FromCombatPile` 一致；外部 `CardChoiceMirrors` 的优先级保持原合同。外部适配不能通过追加无法由原生请求的选牌来对齐旧计划。[证据](performance/simulation-necro-card-operations-20260911.md)。

紧凑费用闭包新增精确 `BorrowedTime`／`Veilpiercer`（共 44 卡牌类型）及玩家的对应能力；没有新增第三方登记入口。`ICompletedEnergyCostReadSource` 仅供已准入值执行器向私有评估上下文提供费用，不是第三方效果覆盖表，也不能注册未迁移效果。原有能量费用 Hook 的同步读取在选牌挂起时继续运行，仍遵守战斗结束门禁；异步效果派发保持原暂停合同。费用镜像必须读取分支状态，不能将原生 CardModel.Pile 或其他 live 变化值当作搜索分支。局部修饰列表与全局查询费用分别对账。[证据](performance/simulation-cost-powers-20260911.md)。

`CardEffectSpecRegistry.PowerEffects` 的共同施加器在每条原生能力命令前检查 `IsEnding`。攻击击杀最后一个敌人后不再施加后续能力，但该表之外的资源／生成命令仍分别遵循自己的原生命令门禁，不能把全部卡牌尾部统一截断。刺破帷幕终局对照覆盖此共同规则。


紧凑封闭编译新增精确吊杀 `Hang`（共 45 卡牌类型）和敌方 `HangPower`，没有新增第三方登记入口。倍率仅携带在该卡攻击指令上，不由引擎识别 CLR 卡牌类型，不影响其他来源的攻击。当前能力增长只准入人工制品这一施加修饰；提交封顶与原生限制请求量的等价依赖“没有观察请求量的其他 Hook”。未来增加施加修饰或能力历史观察者时须重新核验封顶零请求及层数，不能仅补类型白名单。[证据](performance/simulation-hang-20260911.md)。


紧凑卡牌封闭编译新增精确雕琢打击 `SculptingStrike`、响指 `Snap`（共 47 类型），接受已表示牌上的局部虚无／保留。`CardKeywordFlags` 只表示这两种永久增加；临时保留、任意关键字增删和全局关键字修改并未因此开放。全局修改 Hook 仍在整根准入中拒绝，所以雕琢打击的局部过滤与响指的全部关键字过滤可在当前闭包使用同一状态来源。模型导入不是第三方关键字执行入口。旧 `CardChoiceSupport.GetSpec` 中两张牌在结束时不生成选择，符合原生 FromHand；外部优先登记合同不变。[证据](performance/simulation-keywords-20260911.md)。

紧凑封闭编译新增精确灰烬之灵 `SpiritOfAsh` 与死亡之舞 `DanseMacabre`（共 49 卡牌类型），及玩家的对应计数能力；迅速 `Swift` 只接受 0–10 层及 Normal／Disabled 状态。普通牌费用和 X 捕获值分开查询，不能把付款值代替当前费用。出牌前能力按分支获得顺序触发，不对开放观察者承诺可重排；附魔的状态、抽牌执行位置与历史来源均独立保留。状态导入不会调用真实事件，第三方附魔／能力回调仍由整根门禁拒绝；没有新增第三方登记入口。旧死亡之舞镜像修正为当前 GetResolved 语义，镜像登记及覆盖分类未改。[证据](performance/simulation-card-hooks-20260911.md)。


紧凑封闭编译新增精确致死性 `Lethality` 与玩家 `LethalityPower`，现为 50 种卡牌；只准入其原生乘算 Hook。倍率读取卡牌主人、当前分支攻击开始次数，不能以宠物攻击者身份或攻击完成次数替代。没有新增外部登记入口，未知重放／伤害回调仍拒绝。历史课的最后攻击映射在根阶段捕获，空窗口不再按实机回合补读；该修复不表示紧凑后端已准入历史课遗物。[证据](performance/simulation-lethality-20260911.md)。


紧凑封闭编译新增精确神气制胜 `Panache`（51 种卡牌）及玩家 `PanachePower`，只准入其出牌后和回合末 Hook。独立实例使用可增长值布局，不支持外部类型登记或用普通槽叠加；其他独立能力仍在根准入时明确拒绝。旧 `CardPowerOnPlaySupport` 的精确 Panache 分支改为独立施加，共用既有门禁和修改；`PowerPredictionStateSupport.PanacheAlreadyApplied` 供指纹与续用读取 StateStore／实机内部标记。第三方内部标记仍应走手册已有隐藏状态登记，不能依赖此封闭识别。[证据](performance/simulation-panache-20260911.md)。

紧凑封闭编译新增命运同担 `SharedFate`（52 种精确卡牌），复用原有两个基础能力指令，先玩家再指定敌人分别施加负力量；原生消耗及迅速抽牌沿用既有实例执行。不新增外部登记口或变量槽。基础力量／敏捷的请求量类型与原生能力类型分开判定，人工制品与持续标记不共用符号分类。[证据](performance/simulation-shared-fate-20260911.md)。
