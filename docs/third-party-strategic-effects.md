# 第三方 Power 的战略估值登记

## 这是给谁用的

第三方 mod 加了一层 Power，而这层 Power 会改变**玩家该按什么顺序出牌**。

不改变出牌顺序的 Power 不需要登记。求解器对未知 Power 类型有兜底：按叠加层数记一点
`ScalingPotential`。它会让"打出这张 Power 牌"这个动作被归进 `PersistentSetup` 族、不至于
被完全剪掉，对绝大多数 mod 内容够用。

需要登记的是这一类：**这层 Power 的收益取决于它和别的动作的先后关系。**

## 为什么兜底不够

`CombatBeamSolver.ClassifyActionOptionFamilies` 给每个动作打族标签，同族里只留代表。判一个
动作算不算 `ImmediateDefense`，看四样东西：

```csharp
if (block > 0
    || after.ProjectedPlayerHp > before.ProjectedPlayerHp
    || after.PlayerHp > before.PlayerHp
    || after.StrategicEffects.PreventionPotential > before.StrategicEffects.PreventionPotential)
```

四样里唯一能被一层 Power 影响的是 `PreventionPotential`。一张"自己不给甲、但让后续攻击给甲"
的牌，这四样一样都不动，于是被归成纯 `ImmediateOffense`，和攻击牌同族但伤害更低，在族内代表
里被压掉，只能排到攻击后面——而它的收益恰恰依赖排在攻击前面。

还有一层：`CombatBeamSolver.StateEvaluation` 那两圈设置估值原本写死了
`ReferenceEquals(power.Owner, _player.Creature)`，也就是**只看玩家自己身上的增益**。挂在敌人
身上、收益归玩家的 Power 连这一圈都进不来。

## 怎么登记

```csharp
StrategicEffectMirrors.Register<MyPower>(
    StrategicEffectRequirements.AttackPlays,
    static (power, context) => StrategicEffectModel.Prevention(
        Math.Max(1, power.Amount) * context.AttackPlays,
        context),
    StrategicEffectHost.Enemy);
```

三个参数：

| 参数 | 说明 |
|---|---|
| `requirements` | 估值要读 `StrategicEffectContext` 的哪几项。只有被要求的项才会被算出来，没要求的留在便宜的默认值上。如实填：多填浪费，少填读到的是默认值而不是报错 |
| `evaluate` | 由这一层 Power 得出一个 `StrategicEffectVector` |
| `host` | 这层 Power 挂在谁身上 |

`host` 两种：

- `StrategicEffectHost.Player`（默认）——挂在玩家身上的增益，和原版一样的判定：层数为正、
  当前层数下是 `PowerType.Buff`、不是 `ITemporaryPower`。
- `StrategicEffectHost.Enemy`——挂在**敌人**身上但收益归玩家。判定换成：层数为正、不是
  `ITemporaryPower`、宿主不是玩家。**不查增益/减益**，因为它在宿主眼里通常是减益，查了就
  永远进不来。

登记要在 mod 加载时做一次。登记表为空时两处热路径只多一次 `Count` 检查。

## 估值往哪个方向偏

用 `StrategicEffectModel` 的五个工厂方法构造向量，不要自己拼字段——它们各自带着上限逻辑，
比如 `Prevention` 会把值夹在「这回合进来的伤害 × min(2, 剩余回合)」以内。

| 工厂 | 落在哪一项 |
|---|---|
| `Damage(value, enemyHp)` | `DamagePotential`，上限是敌人当前生命 |
| `Prevention(value, context)` | `PreventionPotential`，上限见上 |
| `Resource(value)` | `ResourcePotential` |
| `CardAccess(value)` | `CardAccessPotential` |
| `Scaling(value)` | `ScalingPotential` |

数值分两件事看，两件事对精度的要求差很多：

- **准入**：`ClassifyActionOptionFamilies` 判的是**有没有变大**，只要非零就够。这一半只要求
  你别把值算成 0。
- **排名**：数值决定这条线在收益真正兑现之前能在 Beam 里活多久。估低了会把好线剪掉，估高了
  只是多留几个节点。所以拿不准时**往高了估**，不要往低了估。

## 一个真实例子

观者 mod 的「以手拒之」：打出后给目标挂一层反弹格挡，之后玩家每打中这个敌人**一段**就起
`Amount` 点甲，不自减，整场都在。

实测没登记之前：手里以手拒之加两张打击、敌人这回合打 4 点，求解器给的顺序是
「打击 打击 以手拒之」，第 1 回合 `max_block=0`，白挨 4 点；换成「以手拒之 打击 打击」本可以
4 甲全挡掉。第 2、3、4 回合都是 `max_block=4 actual_block=4`——层数一旦挂上去，后面每回合都
算得对，唯独挂上去的那一回合被浪费。

登记以后，打出以手拒之这个动作会让 `PreventionPotential` 从 0 变正，于是它同时进
`ImmediateDefense` 族，不再被打击在同族里压掉。

估值取「层数 × 可打出的攻击牌张数」。按张不按段是低估——反弹格挡是按伤害段数触发的
（`CombatPredictionSimulator.Attack` 是 `for (i < hitCount) { Damage(...) }`，每段各产生一个
`DamageResult`、各分发一次 `AfterDamageGiven`），而 `StrategicEffectRequirements.AttackPlays`
数的是 `liveCards` 里 `Type == Attack` 的张数。求解器现成的量里没有按段计数，`CardValue` 也
只读 `Damage` 基础值。低估在这里可以接受，因为准入那一半不受影响，而排名那一半有 `Prevention`
的上限兜着：对任何有威胁的局面，层数 × 张数早就顶到上限了。

### 战略命中次数上下文

`StrategicEffectRequirements.AttackHits` 请求玩家攻击预计命中次数，结果为 `StrategicEffectContext.AttackHits`；与 `AttackPlays` 的攻击牌次数分开。未请求时为 null，旧构造调用保持兼容。当前展开已审查多段规则与小刀生成来源，排除 OstyAttack，未知普通攻击仍为单段近似。AttackHits 单独请求时也会计算小刀：已有小刀从普通攻击计数中排除，与生成小刀一起消费行动预算，普通攻击只分配剩余行动；生成器的启动动作不能同时计为攻击。该值仍是潜力估计，不证明能量、过牌或循环可执行。
