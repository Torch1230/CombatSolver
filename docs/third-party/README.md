# 第三方 Mod 适配手册

写给想让战斗路线求解器看懂自家 Mod 的作者。

主项目开发与社区任务面向原版游戏内容，不主动实现修改游戏内容的第三方 Mod 适配。本文记录已有扩展入口，供第三方作者自主实现和验证；适配请求单独留档。

求解器不认识任何第三方内容。它靠一套**镜像**（mirror）在自己的模拟里重现游戏行为，而镜像是
按类型登记的。你的牌、Power、遗物、药水没有登记，求解器就只能退化处理，路线会算错。

这份文档讲：默认会发生什么、有哪些登记点、登记的纪律、怎么验证自己做对了。

HeavenlyDrill 的 OnPlay 使用精确镜像，先解析分支 X 值及修正，再按卡牌 Energy 阈值决定攻击次数翻倍。修改该卡的阈值或攻击流程需提供对应语义登记，通用“X次攻击”推断不足以表达条件翻倍。EndOfDays 的领域补偿在每次施加灾厄后结算能力数量变化监听器和死亡，再判断处决；登记类似监听器时应保留原版 await 时点。

战利品、Adrenaline、Offering、Neurosurge 的完整 OnPlay 由 `CardDrawCardMirrors` 在共享注册表登记，按原版命令顺序处理铸造、扣血、返能、抽牌与能力施加。适配其效果时保留抽牌前后的边界：抽牌可以触发虚空失能量、自动出牌及满手限制。对应的 `CardEffectSpecRegistry` 后置补偿已经移除，第三方应在同一权威镜像内描述有序结算。

苦难（Misery）的完整OnPlay在共享`CardOnPlayMirrors.Registry`登记：攻击前冻结有序减益实例及临时Power的金额调整，攻击后传播克隆。追加格挡的内部`CardEffectSpecRegistry.Apply`要求当前`CardPlay`，续执行用同一Fork上下文重映射；外部镜像同样应保留实际出牌身份。群体减益施加后逐目标完成金额变化Hook，再进入下一目标，抽牌和嵌套自动牌保持原生结算位置。

## 0. 先判断你要不要读下去

内置遗物目标新增 MeatOnTheBone 半血目标与 1～3 优先级，仍属于 RelicCounterCatalog 的封闭表。CardEnchantmentId 是路线显示元数据，当前额外展示原版 Inky；不代表未知附魔已获得战斗模拟支持。

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

格挡清空被阻止后的上限结算目前只显式识别原版 `SturdyClamp`；其他已准入的 preventer 在 `PersistentRelicSupport.BlockAfterPreventingClear` 中按全额保留计算。搜索的保留格挡估值与实际影子清空共用此规则，二者一致不等于已支持第三方的额外上限。新增“保留至多 N 点”等语义时，必须同时适配清空后的结算和估值，并验证原生状态与分支状态；仅登记 `ShouldClearBlock=false` 不足以实现该上限。这是既有适配边界，不表示未知第三方会自动通过兼容门禁。

计算型动态变量必须有分支规则。第三方卡牌进入 `CalculatedVar` 求值且没有 `CalculatedVarSpecRegistry` 支持时，按卡牌所属 Mod 报不兼容，日志包含卡牌 ID；界面和报告账本不引导玩家上传。不能回退调用会读取 live 状态的原生计算器。20260911 的 `LIFEMASTERMOD-TENTACLES` 属于该情况，本次没有为该 Mod 提供适配。

Power 来源也是语义的一部分：精确镜像可通过 `ICombatPredictionEffectSink.ApplyPowerFromSource` 显式提供 `CardModel? cardSource`，原版传 null 时必须保持 null，避免能力附带效果被误判成外层卡牌直接效果。普通 `ApplyPower` 仍沿用当前卡牌作用域；两者不能按调用栈有无卡牌随意替代。

普通能力的 `Owner` 与可空 `Target` 不可混用：无显式目标的施加保持 Target=null，定向施加入口保留真实目标。临时力量族的回调使用经过修正的请求偏移，封顶后的净增量不能替代；其类型检查不扩大第三方能力支持面。内置 Weak/Vulnerable/Frail 的首 tick 标记进入精确状态比较，第三方持续能力仍须登记自己的状态与结算，不自动按这三个类型处理。

受伤唤醒在 `AfterDamageReceivedMirrors` 中立即结算：内置 AsleepPower 和 SlumberPower 对卡牌、遗物及回合效果共享同一 Hook。第三方伤害入口应调用模拟器 Damage，使受伤监听器随该次伤害执行；外层历史扫描不再承担这两个 Power 的唤醒。

原版 `PowerInstanceType.Instanced` 的通用/定向施加每次产生独立分支实例；`GetPower<T>` 与原版一致返回当前第一个实例，逐实例数量更新保持原引用。该行为不替代第三方 BeforeApplied/AfterApplied、内部状态及 Hook 的登记；InstancedPerApplier 的跨来源语义不在本项扩展内。

CrabRagePower 的同伴死亡结算由 `AfterDeathMirrors` 独占：力量、格挡与移除都发生在死亡 Hook 内，后续多段伤害立即消费新格挡。外层死亡清扫不重复该效果。

### 1.1 门禁：先让 Mod 进得来

求解器扫描所有 ModHelper 战斗 hook 订阅者。放行条件包括：

1. 清单 `affects_gameplay: false`；
2. `PredictionModHookSubscriberInertness.IsCombatInert` 判定为战斗惰性——只重写了战斗外的
   hook，或者只重写了战斗开始 / 战斗结束 hook（前者的效果已经落在被捕获的根状态里，后者在
   胜负判定之后才分发，求解器搜到战斗结束就停）；
3. 在 `PredictionModHookSubscriberCapture.KnownPreRootSubscriberTypeNames` 白名单里。
4. Loadout 的 `PowerGiverSummonHook`：主线程检查实际加载的公开计数快照接口及怪物能力配置，把空配置写入续用状态戳。版本号变化不会阻止搜索；接口变化或配置非空时明确失败。这不放行 Loadout 的其他战斗效果。

条件都不满足就抛 `IncompatibleGameplayModException`，整个求解器停摆。

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

`Modify*` / `Should*` 这类只读 hook 中，允许原实现回落的入口会调用 Mod 自己的实现。
适配者仍须核对读取的数据属于当前预测分支；只读方法也可能读到 live 手牌、Power 或费用。
姿态伤害倍率、费用修改等效果只有在完整分支差分通过后，才能认定原实现回落适用。

`CardIsPlayableMirrors` 使用独立的镜像分派：继承基类的牌返回 `true`；有显式登记的重写执行
对应镜像；未登记的重写记录 `MethodNotMirrored`，并返回调用方提供的 `true`。这条覆盖提示
用于暴露缺失语义，适配者必须登记真实可打出条件，才能保证搜索按预测手牌判断合法性。

## 2. 登记点总表

搜索层的 `PotionValuationRegistry` 当前是内部登记表，只迁移原版药水的战略成本档位与开局使用类型，不提供第三方注册入口。未登记的第三方药水仍使用普通战略成本；药水效果、玩家选择和分支状态仍须按下文对应的语义入口登记。

`OpeningActionRegistry` 和 `TargetPlanRegistry` 也只登记求解器内置的开局身份与目标变体，不提供第三方运行时注册；第三方卡牌的实际出牌与选牌语义仍使用下文的镜像登记入口。

### 2.1 统一形状的镜像注册表（46 张）

详细登记、字段和示例见 [third-party-mirrors](mirrors.md)。

### 2.2 战略估值：会改变出牌顺序的 Power

`FirstAttackDamage` 是三层首领特化中填充的首张攻击潜力，普通政策为0，使用范围及限制见下方专文；登记签名与优先级保持。

```csharp
StrategicEffectMirrors.Register<TYourPower>(requirements, evaluate, host);
```

只有当你的 Power **收益取决于它和别的动作的先后关系**时才需要。详见
[第三方 Power 的战略估值登记](strategic-effects.md)。

外部战略登记表非空时，搜索仍按旧规则填充首领特化的 `FirstAttackDamage`，即使登记声明 `StrategicEffectRequirements.None`。仅原版且没有致命消费者时省略扫描；不要求已有外部登记新增需求标志，普通政策字段仍为0。

`StrategicEffectRequirements.AttackHits` 可请求可达攻击命中数；`StrategicEffectContext.AttackHits` 在请求后提供估值，未请求时为 null。它包括已审查的原版多段与小刀生成，第三方攻击使用普通单次命中估计，不能当作真实攻击结算。`ExhaustDrawPlays` 是黑暗之拥在禁抽、虚无顺序下的抽牌机会估值；这些字段只服务保路，不改变 Hook 镜像语义。

三层指定首领的内置联动估值额外填充 `Act3BossInteractions`、`ReachableCards` 及虚无抽牌、高费出牌、未来能量/抽牌的估计值。专用计数只在对应原版 Power 实际参与该分支时计算，第三方登记不能把默认 0 当作完整可达性分析；登记表仍优先于内置 Power 分支。见下方封闭入口清单。

不登记的后果：求解器按叠加层数记一点 `ScalingPotential` 兜底。对大多数 Power 够用；对
「自己不给甲、但让后续攻击给甲」这类会被排到错误位置。

### 2.3 药水的玩家选择

详细登记、字段和示例见 [third-party-choices](choices.md)。

### 2.4 从给定牌堆候选中弃牌

详细登记、字段和示例见 [third-party-choices](choices.md)。

### 2.5 卡牌的玩家选择

详细登记、字段和示例见 [third-party-choices](choices.md)。

### 2.6 Power 的隐藏状态进指纹

详细登记、字段和示例见 [third-party-power-state](power-state.md)。

### 2.7 局外成长来源的独立额度

详细登记、字段和示例见 [third-party-growth-removal](growth-removal.md)。

### 2.8 移除估值的偏置

详细登记、字段和示例见 [third-party-growth-removal](growth-removal.md)。

### 2.9 遗物与 Modifier 的分支状态

`ModelPredictionStateMirrors.RegisterRelic<TModel, TState>` 与 `RegisterModifier<TModel, TState>`
按精确类型登记根捕获、实机字段和预测字段。状态通过现有 `PredictionStateStore` Fork，
同一字段口径进入搜索指纹与 `ContinuationStamp`，按实例所属位置绑定，不合并同类型计数。
首次根或续用捕获后拒绝继续登记；未捕获状态不回落到 live 值。

此接口不放行 Mod、补丁或 Hook，不扩展遗物／Modifier 的中途增删。
卡牌引用可用 `PredictionCardReferences.RequireCard` / `Remap` 与 writer 的 `AddCard` / `AddCards`；
只支持当前五个战斗牌堆，位置索引按观察惰性建立，缺失或歧义拒绝。无序描述须显式声明。
完整签名、对象重映射、字段格式及验证边界见[模型状态适配](model-state.md)。
与其他内部镜像入口一样，外部程序集仍需要 publicizer；本接口尚未发布。

### 2.10 回合阶段效果

`BeforeSideTurnStartMirrors.Register<TModel>(handler)` 登记 `AbstractModel.BeforeSideTurnStart`，
支持 Power、遗物和 Modifier，玩家与敌方在清格挡前共用入口。上下文包含 `Side`、
`Participants` 和分支 `CombatState`；第三方与原版按监听表顺序派发。没有第三方监听者的
战斗保留原版批次顺序，双方共用单项结算体。入口位于 BeginSideTurn 与回合初 Power
快照之后；它不能替代抽牌后的 AfterSideTurnStart 或其他尚未开放的阶段。

`AfterPlayerTurnStartMirrors.RegisterEarly/Register/RegisterLate<TModel>(handler)` 分别登记
抽牌后的 Early、普通、Late；接收者为 AbstractModel，上下文包含 `Player`。扩展路径逐轮
重新捕获监听表，轮内保持顺序；已有外部登记时始终按三轮派发，以覆盖普通阶段新增的监听者。没有外部登记且入口没有第三方覆写时保留原 Power/遗物批次与续执行帧。
三张表共用冻结门；回调挂起时完整重放，不复用未知第三方内部的局部执行帧。

`AfterSideTurnEndLateMirrors.Register<TModel>(handler)` 为精确运行时类型登记
`AbstractModel.AfterSideTurnEndLate` 的预测实现，适用于遗物、Modifier、Power 等模型。
玩家与敌方回合末共用入口，回调自行根据 `Side`、`Participants` 判断是否生效。
底层沿用 `MethodMirrorRegistry` 和覆盖描述元数据，外部仍需 publicizer。

登记必须在首次 `CombatRootSnapshot.Capture` 或本阶段分发之前完成，此后明确拒绝登记。
与多数旧镜像不同，这些阶段遇到未登记且非纯表现的重写会记录风险并抛出
`NotSupportedException`，不会只标记风险后继续生成路线。
**只拿得到 `Type` 的适配器（不引用目标 Mod 程序集、运行期反射找类型）用同一张表的按 `Type`
重载**：`Register(Type, handler)`／`RegisterEarly`／`RegisterLate` 与 `RegisterIgnored(Type)`，
判据与泛型入口相同；`RegisterIgnored` 用于已复核的纯表现层覆写。
完整签名、暂停和状态约束见[回合阶段镜像](turn-phase-mirrors.md)。

### 2.11 已适配 OnPlay 补丁组合

`AdaptedCardOnPlayMirrors.Register<TCard>` 登记精确目标、完整补丁组合与唯一完整预测实现。
首次根／续用捕获后冻结；根选择通过标准 registry 分派，命中后不再执行原版 OnPlay/spec。
组合核对包含实际顺序、owner、优先级和 before／after；不放行未知来源或明确不兼容 Mod。
配置进入 continuation，旧根及路线沿既有边界核对失效。建根时冻结所有已补丁 OnPlay 方法，
并审完全部已登记的卡牌类型。战斗中首次出现的未登记类型只按冻结方法集合判定：
无补丁就交回普通镜像，有补丁则明确拒绝；worker 不读取实时 Harmony 表。
支持面、条件 descriptor、async／动态卡牌限制及测试见[OnPlay 补丁适配](onplay-patches.md)。

### 2.12 还没有登记入口的地方

见第 6 节。目前只能 Harmony 打补丁，或者等对应的扩展点合并。

## 3. 登记的纪律

这几条不是风格建议，是踩过的坑。

### 3.1 加载时一次性登记完

注册表**按精确运行时类型缓存查询结果，而且 `Register` 不会让缓存失效**。一旦某个类型被查过
一次（拿到 `Inferred` 或 `Unsupported`），之后再登记也不会生效，而且不报错。

所以：在 Mod 初始化时把所有登记做完，绝不在战斗中途登记。

`ModelPredictionStateMirrors` 不使用上述延迟分派缓存，而是在第一次根或续用捕获后冻结整张登记表；
迟到登记明确抛异常。两类入口的共同要求仍是初始化期间一次完成登记。

`AfterSideTurnEndLateMirrors.Register` 在标准 registry 外提供冻结检查，首次根捕获或分发后
也会明确拒绝迟到登记；外部调用此入口，不绕过它直接写入内部 registry。

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

无头夹具的跑法见 [HEADLESS_TESTING.md](../HEADLESS_TESTING.md)。

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

组合达标早停额外读取冻结的 `CombatRootSnapshot.HasVisibleHealingSource`：牌、玩家 Power 和可搜索药水的 `Heal` / `HealPercent` / `RegenPower` 变量，以及已有 `RegenPower`，会保守保留追加搜索；已选路线实际回血也保留追加搜索。变量在主线程完成物化后读取，治疗随从同样可能触发保守回退。这不是完整治疗来源登记或可达收益上界；没有这些元数据的自定义治疗、后续生成的治疗来源，仍可能因玩家战损目标已经达标而少做追加审计。关闭战损达标早停可保留原追加搜索；不改变模拟执行和既有第三方适配合同。

下面这些位置目前是按原版类型写死的开关，第三方登记不进去。要用只能 Harmony 打补丁，或者等
对应扩展点合并。列在这里是为了让你知道撞上了什么，而不是以为自己写错了。

| 位置 | 症状 | 状态 |
|---|---|---|
| `StrategicHpRecoveryBound.CanCertifyRemainingHealingEnvironment` / `RemainingHealingUpperBound` | 只在已审计的原版角色、敌人、卡牌、持续效果、遗物和药水闭包内收紧剩余治疗上界；包括固定Shiv来源、Slither费用随机化及Inky虚弱；敌人集合包含逐项审计的精确SoulNexus，其三个行动与生命周期不授予玩家治疗；另含精确Regent／LouseProgenitor闭包，BurningSticks复制消耗技能的例外仍保守处理。未知来源、附魔／苦难、消耗牌被动与取回来源保守回退无限余量；再生及战后治疗继续计入。第三方语义登记不等于治疗上界证明，没有外部证书注册入口；原战斗模拟支持范围不因此扩大 | 封闭性能证明 |
| `CombatSearchCoordinator.CanFinishNativeLouseZeroDamageRoute` / `CombatRootSnapshot.InitialRemainingHealingUpperBound` | 精确原生Regent／Louse闭包的初始治疗上界为零，且无风险满血零损无药完整胜利才停止可选药水后验；固定预算、强制药水、死亡保护、成长／遗物目标与未追回资源阻止退出。未知初始Power／药水／生成牌和剩余再生保守拒绝；没有外部证书登记入口。BurningSticks存在时拒绝消耗BundleOfJoy快捷证书；新增7牌／5Power／3遗物／2药水只在此闭包，其他环境的原表不变 | 封闭性能证明 |
| `CombatHistoryCounterKey.ForCard` / `OpenGenerationSources` | 原版历史读者按所读计数入键，随机生成、变牌及间接生成药水来源保守全量入键；新增原版入口必须同步该表。根包含消耗堆。第三方模型、已捕获 Mod 订阅者、BaseLib 修饰器或存在 AdaptedOnPlay 快照时自动回退六项全量，不能据此支持六项之外的新历史语义；新计数仍须显式扩展历史、Fork 和指纹合同。 | 封闭语义依赖表 |
| `CombatPredictionSimulator.SupportsManualCardChoiceContinuation` / `PredictionStateStore.SupportsManualCardChoiceContinuation` | 自身选牌续执行覆盖清单中的41张原版单人卡，要求无附魔/污染、手动单次执行；已生成的请求、候选、历史与活动格挡计数有显式复制合同，不能据此接纳第三方选牌委托；拒绝不透明外部状态以及所有 `IPredictionForkBoundary` 状态（包括模型状态适配器包装）。不符合时保留原完整回放，已有第三方战斗支持范围不因此扩大；无注册入口 | 封闭性能特化 |
| `CombatPredictionSimulator.ExecutionContinuation` / `ExecutionDispatchScope` | 回合来源、抽牌、Hook及嵌套子出牌使用内部纯数据帧。未知派发未确认协议、未知历史、不可复制事务或不透明StateStore时拒绝捕获，继续既有完整回放；不会跳过游戏效果，也不把既有第三方登记等同于可复制回调。原Fork稳定断言保持；没有外部续跑注册入口 | 封闭性能特化 |
| `PotionChoiceContinuation.Supports` | 9种原版手动选牌药水的稳定前缀特化；第三方类型与通过PotionChoiceMirrors登记覆盖原版选择者继续完整重放，无额外注册入口。普通Fork/StateStore断言保持，不能用此入口接纳不透明回调或事务 | 封闭性能特化 |
| `SimulatedCombatState.AfterCardEnteredCombat` → `GhostSeedMirrors` | 幽灵种子按本地基础牌标签处理真实入场；已捕获根卡的关键词不会由后续归一化重新改写，入场镜像仍为原版封闭派发 | 原版封闭派发 |
| `SimulatedCombatState.ApplyWithBeforeApplied` / `AfterCardEnteredCombat` → `PhantomBladesPowerMirrors` | 幻影之刃的首次施加和卡牌入场直接派发精确镜像体，尚未提供通用 Power.AfterApplied 注册入口；其他来源不得依赖全局归一化重新赋予关键词 | 原版封闭派发 |
| `CardChoiceSupport.Spec` / `CardChoiceSpec.IsImplicitAllSelection` | 原版固定数量选择在候选不足或恰好全部时，按候选顺序生成唯一计划。第三方使用原版隐式全选规则时必须设置该标记；普通手动确认选择保持自己的顺序策略，Runtime对隐式选择严格核对实例和顺序 | 原版特化；第三方选择已有入口 |
| `CombatBeamSolver.CaptureEnergyRefundWindow` / `StrategicEffectContext.RecurringEnergyGain` | 原版环绕轨道按花费余数、自动化按剩余抽牌数估计未来返能，包含自然抽牌；与可消费能量缺口共用上限。第三方仍通过 §2.2 登记，详见[估值上下文](strategic-effects.md) | 原版特化；第三方估值已有入口 |
| `RelicCounterCatalog` / `SimulatedCombatState.ReadRelicCounter` | 战斗末卡数仅覆盖已核对的十种原版计数；第三方显示计数只列出“尚未适配”，不会被自动当作跨战斗目标。见[计数策略说明](../relic-counters.md) | 精确原版适配 |
| `SearchPolicySnapshot.IsAct3BossEncounter` / `CombatBeamSolver.CaptureAct3BossInteractionPotential` | 首领范围只含第三幕实验体、永世沙漏、女王；联动上下文只适配原版 Pagestorm、DanseMacabre、Demesne；StrategicEffectModel 对 PrepTimePower 按未来攻击与回合视野估计重复精力收益。这些不是通用第三方触发次数分析。第三方 Power 仍使用 §2.2 登记 | 原版特化；第三方估值已有入口 |
| `GoldGainedMirrors` / `GoldGainSupport` | 三个标准 descriptor 描述金币 Modify、AfterModify、AfterGain；当前只有原版 BowlerHat、Ectoplasm、DragonFruit 的精确登记，registry 未开放外部注册。仅明确审计的展示通知可 Ignored；未知 override 即使非 gameplay manifest 也拒绝。修改用 combat child，获得后使用根冻结的 null-child 跑局序列；金币/遗物/药水/HP 属当前分支。 | 原版封闭派发 |
| `PredictionModHookSubscriberCapture.KnownPreRootSubscriberTypeNames` | 私有静态白名单，没有公开登记入口 | 待做 |
| `PredictionModPatchAudit.ValidateLoadedMods` | 明确拒绝 `WheelchairSpire`，没有外部放行入口 | 项目不兼容策略 |
| `NativeModelCloneConcurrency` | 预测克隆只放行已核对原版阶段、原版变量及 BaseLib/Ritsu 稀疏元数据复制补丁组合的普通原版卡牌；附魔/灾厄、第三方模型/变量和未知补丁保留原锁。Power 只放行已物化原版变量、继承默认克隆及 InitInternalData 的原版类型，同时核对基阶段与变量 getter 补丁；自定义初始化保持原锁。每个线程最外层模拟隔离域重新核对，不支持求解中安装补丁；原版 MutableClone 保护不变。没有新增外部注册入口 | 精确框架适配 |
| `RitsuEmptyCapabilityFastPathPatches` | 模拟隔离域的空 capability 集可直接保留原卡牌标签序列；不枚举/复制标签，不缓存分支值。非空贡献者与精确类型默认来源继续框架入口；晚注册刷新来源代次，已物化的空集合仍按框架语义处理。live 不旁路，无新增登记入口 | 精确框架适配 |
| `DynamicVarCloneMetadataPatches` | 模拟克隆只优化已核对为空默认值的 BaseLib 提示/升级字段与 Ritsu 提示工厂；非空值照常复制，live 调用保持原框架行为。其他附加字段继续原有克隆逻辑，不属于此优化入口 | 精确框架适配 |
| `CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects`、`FlushPlayerHandAtTurnEnd`、BeforeHandDraw、AfterSideTurnStart | 常规回合末及这些抽牌/阵营时点仍无通用登记；注能核心的首回合产球由 `TriggerRelicsAfterSideTurnStart` 显式结算，准备选牌根可能早于产球，不能认为所有开局效果已在根内。其闪电伤害加成仍走只读 `ModifyOrbValue`，只读数值支持不代表产球生命周期已适配。BeforeSideTurnStart、AfterPlayerTurnStart（Early/普通/Late）及 AfterSideTurnEndLate 已开放，见 §2.10，不能互相替代 | 部分开放 |
| `SimulatedCombatState.TryPrepareExtraPlayerTurn` / `TryPrepareLiveExtraPlayerTurn` / `ConsumeExtraTurnSources` | 额外回合的来源硬编码，只认龙涎香和帕尔之眼 | 待做 |
| `CombatPredictionSimulator.OnPlayWrapper` | 出牌后补抽没有挂载点 | 待做 |
| `CardChoiceSupport.RemovalPriority` 的排序口径 | 移除类选择按**单卡**估值排，不看牌库其余部分；弃牌那一侧已经是「源牌堆平均值减本牌估值」的相对口径，消耗与转变没有。表现为求解器不会为了压出无限而主动烧牌。起手牌那一层已由 §2.7 打开，相对口径这一层仍然封闭 | 待做 |
| `ContinuationStamp.AppendCard` 的 `private=` 段与 `CombatBeamSolver.CaptureCardStateFingerprintForTesting` 的 `switch (preview)` | **卡牌**的隐藏字段按原版类型写死（利爪、基因算法、巨锤、狂暴、镰刀、疯狂科学），第三方卡牌的私有计数进不了指纹。Power 那一侧已有 `PowerHiddenStateMirrors`，见 §2.6 | 待做 |
| `SimulatedCombatState.AddTurnStartStates` 的 `switch (power)` | 原版 Power 隐藏计数按类型写死。第三方走 §2.6 的登记表进同一份指纹，本行只是记下原版那个 `switch` 本身仍然封闭 | 第三方已有入口 |
| `RelicPredictionStateSupport` 的原版类型分支 | 内置遗物状态仍按原实现处理；第三方遗物与 Modifier 的独立状态通过 §2.9 登记，不修改原版分支 | 第三方已有入口 |
| `GrowthSource` 枚举与 `SolverGrowthStrategyPanel.SourceCard` 的 `switch` | 原版十类成长来源按类型写死。第三方走 §2.7 的 `GrowthSourceMirrors` 拿独立额度、侧栏行和指纹，本行只是记下原版那个枚举本身仍然封闭 | 第三方已有入口 |

**这些开关新增或改动时，必须在同一个提交里更新这张表和本文档对应章节。** 见
[AGENTS.md](../../AGENTS.md) 第 9 节。

## 7. 相关文档

- [架构与职责地图](../ARCHITECTURE.md)：源码入口和所有权，`§4.2 Mirror` 是镜像层的位置。
- [战斗钩子覆盖目录](../COMBAT_HOOK_COVERAGE.md)：求解器分发哪些 hook。
- [第三方 Power 的战略估值登记](strategic-effects.md)。
- [无头测试](../HEADLESS_TESTING.md)：夹具怎么跑。
- [检查点回放](../CHECKPOINT_REPLAY.md)：问题包怎么导入。
