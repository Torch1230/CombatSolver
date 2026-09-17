# 铁甲战士能力牌

本表列出游戏版本 0.111.0 的 IroncladCardPool 中全部 20 张能力牌：19 张属于 CombatSolver 单人建模范围，1 张由游戏标记为 MultiplayerOnly。中英文名称与效果原文来自同版本 PCK 的 localization/zhs/cards.json 和 localization/eng/cards.json；数值由同版本原版卡牌实例按普通/升级状态格式化。表中只把能量与星能图片图标写成文字，其余效果语义不改写。

LegacyFallback 表示尚未建模，必须等用户逐卡给出理解；OutOfScopeMultiplayer 只保留完整卡池资料，不进入单人求解器建模。

| ID / 实现类型 | 官方名称（中 / 英） | 费用（普通→升级） | 游戏效果 | 建模状态 |
|---|---|---|---|---|
| <code>AGGRESSION</code><br><code>Aggression</code> | 好勇斗狠<br>Aggression | 1 能量 | 在你的回合开始时，将你弃牌堆的一张随机攻击牌放入你的手牌并将其升级。 | <code>LegacyFallback</code> |
| <code>BARRICADE</code><br><code>Barricade</code> | 壁垒<br>Barricade | 3→2 能量 | 格挡不再在你的回合开始时消失。 | <code>LegacyFallback</code> |
| <code>CORRUPTION</code><br><code>Corruption</code> | 腐化<br>Corruption | 3→2 能量 | 技能牌消耗变为0点能量。<br>每当你打出一张技能牌时，将其消耗。 | <code>LegacyFallback</code> |
| <code>CRIMSON_MANTLE</code><br><code>CrimsonMantle</code> | 绯红披风<br>Crimson Mantle | 1 能量 | 普通：在你的回合开始时，失去1点生命并获得7点格挡。<br>升级：在你的回合开始时，失去1点生命并获得10点格挡。 | <code>LegacyFallback</code> |
| <code>CRUELTY</code><br><code>Cruelty</code> | 残酷<br>Cruelty | 1 能量 | 普通：有易伤状态的敌人额外受到25%的伤害。<br>升级：有易伤状态的敌人额外受到50%的伤害。 | <code>LegacyFallback</code> |
| <code>DARK_EMBRACE</code><br><code>DarkEmbrace</code> | 黑暗之拥<br>Dark Embrace | 2→1 能量 | 每当有一张牌被消耗时，<br>抽1张牌。 | <code>LegacyFallback</code> |
| <code>DEMON_FORM</code><br><code>DemonForm</code> | 恶魔形态<br>Demon Form | 3 能量 | 普通：在你的回合开始时，获得3点力量。<br>升级：在你的回合开始时，获得4点力量。 | <code>LegacyFallback</code> |
| <code>FEEL_NO_PAIN</code><br><code>FeelNoPain</code> | 无惧疼痛<br>Feel No Pain | 1 能量 | 普通：每当有一张牌被消耗时，获得3点格挡。<br>升级：每当有一张牌被消耗时，获得4点格挡。 | <code>LegacyFallback</code> |
| <code>HELLRAISER</code><br><code>Hellraiser</code> | 地狱狂徒<br>Hellraiser | 2→1 能量 | 每当你抽到名字中有“打击”的牌时，对一名随机敌人打出这张牌。 | <code>LegacyFallback</code> |
| <code>INFERNO</code><br><code>Inferno</code> | 狱火<br>Inferno | 1 能量 | 普通：在你的回合开始时，失去1点生命。<br>每当你在你的回合内失去生命时，对所有敌人造成6点伤害。<br>升级：在你的回合开始时，失去1点生命。<br>每当你在你的回合内失去生命时，对所有敌人造成9点伤害。 | <code>LegacyFallback</code> |
| <code>INFLAME</code><br><code>Inflame</code> | 燃烧<br>Inflame | 1 能量 | 普通：获得2点力量。<br>升级：获得3点力量。 | <code>LegacyFallback</code> |
| <code>JUGGERNAUT</code><br><code>Juggernaut</code> | 势不可当<br>Juggernaut | 2 能量 | 普通：每当你获得格挡时，对随机敌人造成6点伤害。<br>升级：每当你获得格挡时，对随机敌人造成8点伤害。 | <code>LegacyFallback</code> |
| <code>JUGGLING</code><br><code>Juggling</code> | 杂耍<br>Juggling | 1 能量 | 将你在每回合打出的第三张攻击牌的复制品加入你的手牌。 | <code>LegacyFallback</code> |
| <code>PYRE</code><br><code>Pyre</code> | 薪火之源<br>Pyre | 2 能量 | 普通：在回合开始时，获得1点能量。<br>升级：在回合开始时，获得2点能量。 | <code>LegacyFallback</code> |
| <code>RUPTURE</code><br><code>Rupture</code> | 撕裂<br>Rupture | 1 能量 | 普通：每当你在你的回合失去生命值时, 获得1点力量。<br>升级：每当你在你的回合失去生命值时, 获得2点力量。 | <code>LegacyFallback</code> |
| <code>STAMPEDE</code><br><code>Stampede</code> | 惊逃<br>Stampede | 2→1 能量 | 在你的回合结束时，随机打出你手牌中的1张攻击牌攻击随机敌人。 | <code>LegacyFallback</code> |
| <code>STONE_ARMOR</code><br><code>StoneArmor</code> | 岩石铠甲<br>Stone Armor | 1 能量 | 普通：获得4层覆甲。<br>升级：获得6层覆甲。 | <code>LegacyFallback</code> |
| <code>TANK</code><br><code>Tank</code> | 肉盾<br>Tank | 1→0 能量 | 受到敌人的伤害增加50%。<br>盟友受到敌人的伤害减少50%。 | <code>OutOfScopeMultiplayer</code> |
| <code>UNMOVABLE</code><br><code>Unmovable</code> | 坚定不移<br>Unmovable | 2→1 能量 | 翻倍你每回合第一次从卡牌中获得的格挡。 | <code>LegacyFallback</code> |
| <code>VICIOUS</code><br><code>Vicious</code> | 凶恶<br>Vicious | 1 能量 | 普通：每当你给予易伤时，抽1张牌。<br>升级：每当你给予易伤时，抽2张牌。 | <code>LegacyFallback</code> |
