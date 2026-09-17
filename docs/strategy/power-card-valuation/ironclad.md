# 铁甲战士能力牌

本表列出游戏版本 0.111.0 的 IroncladCardPool 中全部 20 张能力牌：19 张属于 CombatSolver 单人建模范围，1 张由游戏标记为 MultiplayerOnly。中英文名称与效果原文来自同版本 PCK 的 localization/zhs/cards.json 和 localization/eng/cards.json；数值由同版本原版卡牌实例按普通/升级状态格式化。表中只把能量与星能图片图标写成文字，其余效果语义不改写。

`QuantifiedDraft` 表示已按实现者理解完成逐卡公式、登记和纯合同，尚待玩家复核。`OutOfScopeMultiplayer` 只保留完整卡池资料，不进入单人求解器建模。

| ID / 实现类型 | 官方名称（中 / 英） | 费用（普通→升级） | 游戏效果 | 建模状态 |
|---|---|---|---|---|
| <code>AGGRESSION</code><br><code>Aggression</code> | 好勇斗狠<br>Aggression | 1 能量 | 在你的回合开始时，将你弃牌堆的一张随机攻击牌放入你的手牌并将其升级。 | <code>QuantifiedDraft</code> |
| <code>BARRICADE</code><br><code>Barricade</code> | 壁垒<br>Barricade | 3→2 能量 | 格挡不再在你的回合开始时消失。 | <code>QuantifiedDraft</code> |
| <code>CORRUPTION</code><br><code>Corruption</code> | 腐化<br>Corruption | 3→2 能量 | 技能牌消耗变为0点能量。<br>每当你打出一张技能牌时，将其消耗。 | <code>QuantifiedDraft</code> |
| <code>CRIMSON_MANTLE</code><br><code>CrimsonMantle</code> | 绯红披风<br>Crimson Mantle | 1 能量 | 普通：在你的回合开始时，失去1点生命并获得7点格挡。<br>升级：在你的回合开始时，失去1点生命并获得10点格挡。 | <code>QuantifiedDraft</code> |
| <code>CRUELTY</code><br><code>Cruelty</code> | 残酷<br>Cruelty | 1 能量 | 普通：有易伤状态的敌人额外受到25%的伤害。<br>升级：有易伤状态的敌人额外受到50%的伤害。 | <code>QuantifiedDraft</code> |
| <code>DARK_EMBRACE</code><br><code>DarkEmbrace</code> | 黑暗之拥<br>Dark Embrace | 2→1 能量 | 每当有一张牌被消耗时，<br>抽1张牌。 | <code>QuantifiedDraft</code> |
| <code>DEMON_FORM</code><br><code>DemonForm</code> | 恶魔形态<br>Demon Form | 3 能量 | 普通：在你的回合开始时，获得3点力量。<br>升级：在你的回合开始时，获得4点力量。 | <code>QuantifiedDraft</code> |
| <code>FEEL_NO_PAIN</code><br><code>FeelNoPain</code> | 无惧疼痛<br>Feel No Pain | 1 能量 | 普通：每当有一张牌被消耗时，获得3点格挡。<br>升级：每当有一张牌被消耗时，获得4点格挡。 | <code>QuantifiedDraft</code> |
| <code>HELLRAISER</code><br><code>Hellraiser</code> | 地狱狂徒<br>Hellraiser | 2→1 能量 | 每当你抽到名字中有“打击”的牌时，对一名随机敌人打出这张牌。 | <code>QuantifiedDraft</code> |
| <code>INFERNO</code><br><code>Inferno</code> | 狱火<br>Inferno | 1 能量 | 普通：在你的回合开始时，失去1点生命。<br>每当你在你的回合内失去生命时，对所有敌人造成6点伤害。<br>升级：在你的回合开始时，失去1点生命。<br>每当你在你的回合内失去生命时，对所有敌人造成9点伤害。 | <code>QuantifiedDraft</code> |
| <code>INFLAME</code><br><code>Inflame</code> | 燃烧<br>Inflame | 1 能量 | 普通：获得2点力量。<br>升级：获得3点力量。 | <code>QuantifiedDraft</code> |
| <code>JUGGERNAUT</code><br><code>Juggernaut</code> | 势不可当<br>Juggernaut | 2 能量 | 普通：每当你获得格挡时，对随机敌人造成6点伤害。<br>升级：每当你获得格挡时，对随机敌人造成8点伤害。 | <code>QuantifiedDraft</code> |
| <code>JUGGLING</code><br><code>Juggling</code> | 杂耍<br>Juggling | 1 能量 | 将你在每回合打出的第三张攻击牌的复制品加入你的手牌。 | <code>QuantifiedDraft</code> |
| <code>PYRE</code><br><code>Pyre</code> | 薪火之源<br>Pyre | 2 能量 | 普通：在回合开始时，获得1点能量。<br>升级：在回合开始时，获得2点能量。 | <code>QuantifiedDraft</code> |
| <code>RUPTURE</code><br><code>Rupture</code> | 撕裂<br>Rupture | 1 能量 | 普通：每当你在你的回合失去生命值时, 获得1点力量。<br>升级：每当你在你的回合失去生命值时, 获得2点力量。 | <code>QuantifiedDraft</code> |
| <code>STAMPEDE</code><br><code>Stampede</code> | 惊逃<br>Stampede | 2→1 能量 | 在你的回合结束时，随机打出你手牌中的1张攻击牌攻击随机敌人。 | <code>QuantifiedDraft</code> |
| <code>STONE_ARMOR</code><br><code>StoneArmor</code> | 岩石铠甲<br>Stone Armor | 1 能量 | 普通：获得4层覆甲。<br>升级：获得6层覆甲。 | <code>QuantifiedDraft</code> |
| <code>TANK</code><br><code>Tank</code> | 肉盾<br>Tank | 1→0 能量 | 受到敌人的伤害增加50%。<br>盟友受到敌人的伤害减少50%。 | <code>OutOfScopeMultiplayer</code> |
| <code>UNMOVABLE</code><br><code>Unmovable</code> | 坚定不移<br>Unmovable | 2→1 能量 | 翻倍你每回合第一次从卡牌中获得的格挡。 | <code>QuantifiedDraft</code> |
| <code>VICIOUS</code><br><code>Vicious</code> | 凶恶<br>Vicious | 1 能量 | 普通：每当你给予易伤时，抽1张牌。<br>升级：每当你给予易伤时，抽2张牌。 | <code>QuantifiedDraft</code> |

## 逐卡建模（Draft）

实现位置：`src/Search/PowerCardValuation/Cards/Ironclad/`（注册入口、路线政策、触发证据、开局投影、力量/消耗/防御/触发伤害/牌流五个机制族模型）。价值只用于候选准入与中间保路，不进入终局胜负/战损排序。触发证据读取冻结后的真实牌区、Power 与意图；开局投影为有界潜力，未满置信的复杂机制用保守代理。

| ID | 机制族 | 优先级 | 触发证据（须真实存在） | 开局投影口径 | 置信度 | 待玩家复核 |
|---|---|---|---|---|---|---|
| AGGRESSION | CardGenerationEngine | Normal | 牌区有攻击且预计剩余回合>1 | 每回合从弃牌堆取回的攻击价值（有界） | 中 | 是否应更积极保路；升级固有对开局的影响 |
| BARRICADE | DefenseEfficiency | Strong | 牌区有格挡技能 | 每回合保留格挡（按最大单张格挡有界） | 中 | 长线战斗中保留格挡的真实阈值 |
| CORRUPTION | CostReductionEngine | Strong（专搜） | 牌区有技能 | 技能张数×平均费用折算省能 | 中低 | 技能被消耗带来的牌组损失是否应更重 |
| CRIMSON_MANTLE | BlockTriggerEngine+LifeInvestment | Strong | 预计剩余回合>1 | 每回合格挡收益扣自伤 | 中 | 自伤代价与格挡净收益 |
| CRUELTY | StatusAmplifier | Strong | 有易伤来源或敌人已易伤且牌区有攻击 | 易伤目标攻击伤害×25/50% | 中 | 易伤覆盖率与目标选择 |
| DARK_EMBRACE | ExhaustEngine | Core | 有消耗来源且剩余回合>1 | 消耗次数×抽牌价值 | 中 | 与消耗引擎的组合放大 |
| DEMON_FORM | StrengthGrowth | Core | 牌区有攻击且剩余回合>1 | 每回合力量×攻击行动前沿差值 | 高 | 何时应开始保路 |
| FEEL_NO_PAIN | ExhaustEngine | Core | 有消耗来源 | 消耗次数×每层格挡 | 高 | 与黑暗之拥的复合收益 |
| HELLRAISER | AutoPlayEngine | Normal | 牌区有“打击”牌 | 每回合自动打出打击（有界） | 低 | 对全无限生命敌人每回合9次上限与随机性 |
| INFERNO | LifeInvestment+DamageEngine | Strong | 剩余回合>1且仍有敌人 | 每回合群伤×敌人数，扣自伤 | 中 | 自伤与“本回合失去生命”触发窗口 |
| INFLAME | StrengthGrowth | Core | 牌区有攻击 | 力量×攻击行动前沿差值 | 高 | 短战斗是否仍值得开 |
| JUGGERNAUT | BlockTriggerEngine | Strong | 有格挡来源且仍有敌人 | 伤害×格挡来源数（随机目标保守） | 高 | 群怪与随机目标 |
| JUGGLING | AutoPlayEngine+CardGenerationEngine | Normal | 攻击≥3或剩余回合>1 | 每回合第3张攻击的副本价值 | 低 | 副本生成时点与手牌上限 |
| PYRE | EnergyEngine | Core | 剩余回合>1 | 每回合额外能量×剩余回合 | 高 | 何时优先于即时输出 |
| RUPTURE | StrengthGrowth+LifeInvestment | Normal | 牌区有自伤来源 | 力量×攻击行动前沿差值 | 中 | 仅本方回合掉血生效，敌方回合掉血不计 |
| STAMPEDE | AutoPlayEngine | Normal | 牌区有攻击且剩余回合>1 | 每回合随机攻击（有界） | 中低 | 随机目标与不可打出攻击的过滤 |
| STONE_ARMOR | DefenseEfficiency | Strong | 剩余回合>1 | 覆甲×剩余回合（逐回合衰减） | 高 | 衰减后是否仍保路 |
| UNMOVABLE | DefenseEfficiency | Strong | 牌区有格挡技能 | 每回合首张卡牌格挡翻倍 | 中 | 怪物行动格挡是否计入 |
| VICIOUS | CardGenerationEngine+StatusAmplifier | Normal | 有易伤来源且剩余回合>1 | 易伤施加次数×抽牌价值 | 中 | 无每回合上限导致的堆叠 |

### 隐藏状态与时序（Draft 记录）

- `CRIMSON_MANTLE`/`INFERNO` 的自伤来自卡牌应用时置位的 `SelfDamage` 变量（0→1），不是逐回合递增。
- `RUPTURE` 只在本方回合、且按“当前正在结算的卡牌”延迟到该牌结算后加力量；非卡牌来源立即结算。
- `HELLRAISER` 对全无限生命敌人每回合最多自动打出9次，内部计数在回合结束重置。
- `JUGGLING` 只在每回合第3张攻击的精确时点触发一次。
- `BARRICADE` 为单层不可叠加；`UNMOVABLE` 只对卡牌/怪物行动来源的格挡翻倍并受每回合次数限制。
- `TANK` 为 MultiplayerOnly，仅保留资料。

### 不确定项

- 触发证据与投影读取真实状态，但兑现证据暂统一走通用状态改善回退，尚未逐卡专用。
- `CORRUPTION` 的牌组消耗损失、`HELLRAISER`/`STAMPEDE` 的随机目标只做保守代理。

## 玩家联合评审采纳（2026-09-17）

按玩家最终意见（优先于实现者与外部 AI）更新路线优先级与专搜，并同步数值口径：

| 卡牌 | 变化 | 依据 |
|---|---|---|
| AGGRESSION | 加入专搜 | 长线回收并升级攻击，当回合0收益 |
| BARRICADE | Strong→Core，加入专搜 | 防战核心，升级后2费可开 |
| CRIMSON_MANTLE | 加入专搜 | 优质防御来源，自伤触发联动 |
| CRUELTY | Strong→Normal | 依赖易伤覆盖率，普通搜索可算清 |
| DARK_EMBRACE | 加入专搜 | 当回合不给牌，需消耗链展开 |
| DEMON_FORM | 加入专搜 | 3费空过，长线价值高 |
| HELLRAISER | Normal→Low | 猴戏牌，不值得专搜 |
| JUGGERNAUT | Strong→Normal | 仅在无惧疼痛/重振精神配合下强 |
| JUGGLING | Normal→Low | 仅0费连打体系可用 |
| PYRE | 加入专搜 | 当回合纯亏费 |
| RUPTURE | 加入专搜 | 与扣血牌配合强，求解器易规避自残 |
| UNMOVABLE | 加入专搜 | 时序敏感，避免先打微量格挡浪费翻倍 |
| VICIOUS | Normal→Core，群体/重复易伤叠加抽牌 | 玩家判定为优质过牌 |

- 岩石铠甲：敲过（升级）才值得开，未升级视为弱；数值沿用普通/升级两档。
- 燃烧：吃多段攻击，多段时优先；狱火：群怪优先，单体需配合撕裂。
- 专搜在本实现中已由“当前可打能力固定前缀后验”覆盖：每张可打出的已登记能力都会从同一根跑一次完整搜索。`PreferDedicatedSearch` 只记录玩家希望优先保障的牌；实测把它接入前缀构造或承诺席位排序会让 `BARRICADE` 场景从 43 变成 62 战损，因此不改变路由，仅作评审标记保留。
