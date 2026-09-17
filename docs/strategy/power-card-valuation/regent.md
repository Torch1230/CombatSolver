# 储君能力牌

本表列出游戏版本 0.111.0 的 RegentCardPool 中全部 19 张能力牌：18 张属于 CombatSolver 单人建模范围，1 张由游戏标记为 MultiplayerOnly。中英文名称与效果原文来自同版本 PCK 的 localization/zhs/cards.json 和 localization/eng/cards.json；数值由同版本原版卡牌实例按普通/升级状态格式化。表中只把能量与星能图片图标写成文字，其余效果语义不改写。

LegacyFallback 表示尚未建模，必须等用户逐卡给出理解；OutOfScopeMultiplayer 只保留完整卡池资料，不进入单人求解器建模。

| ID / 实现类型 | 官方名称（中 / 英） | 费用（普通→升级） | 游戏效果 | 建模状态 |
|---|---|---|---|---|
| <code>ARSENAL</code><br><code>Arsenal</code> | 武器库<br>Arsenal | 1 能量 | 每当你生成一张牌，就获得1点力量。 | <code>LegacyFallback</code> |
| <code>BLACK_HOLE</code><br><code>BlackHole</code> | 黑洞<br>Black Hole | 1 能量 | 普通：每当你花费或获得星能时，对所有敌人造成3点伤害。<br>升级：每当你花费或获得星能时，对所有敌人造成4点伤害。 | <code>LegacyFallback</code> |
| <code>CHILD_OF_THE_STARS</code><br><code>ChildOfTheStars</code> | 群星之子<br>Child of the Stars | 1 能量 | 普通：每当你花费星能时，每花费一点星能，获得2点格挡。<br>升级：每当你花费星能时，每花费一点星能，获得3点格挡。 | <code>LegacyFallback</code> |
| <code>FURNACE</code><br><code>Furnace</code> | 熔炉<br>Furnace | 1 能量 | 普通：在你的回合开始时，铸造5。<br>升级：在你的回合开始时，铸造7。 | <code>LegacyFallback</code> |
| <code>GENESIS</code><br><code>Genesis</code> | 创世纪<br>Genesis | 2 能量 | 普通：在你的回合开始时，获得2点星能。<br>升级：在你的回合开始时，获得3点星能。 | <code>LegacyFallback</code> |
| <code>HAMMER_TIME</code><br><code>HammerTime</code> | 锤子时间<br>Hammer Time | 2→1 能量 | 每当你铸造时，所有盟友也都铸造相同的数值。 | <code>OutOfScopeMultiplayer</code> |
| <code>MONARCHS_GAZE</code><br><code>MonarchsGaze</code> | 王之凝视<br>Monarch's Gaze | 2→1 能量 | 每当你攻击敌人的时候，这名敌人在本回合失去1点力量。 | <code>LegacyFallback</code> |
| <code>NEUTRON_AEGIS</code><br><code>NeutronAegis</code> | 中子护盾<br>Neutron Aegis | 1 能量；5 星能 | 普通：获得8层覆甲。<br>升级：获得11层覆甲。 | <code>LegacyFallback</code> |
| <code>ORBIT</code><br><code>Orbit</code> | 环绕轨道<br>Orbit | 2→1 能量 | 你每花费4点能量，<br>就获得1点能量。 | <code>LegacyFallback</code> |
| <code>PALE_BLUE_DOT</code><br><code>PaleBlueDot</code> | 暗淡蓝点<br>Pale Blue Dot | 1 能量 | 普通：如果你在一回合内打出了大于等于5张牌，在下个回合开始时抽1张牌。<br>升级：如果你在一回合内打出了大于等于5张牌，在下个回合开始时抽2张牌。 | <code>LegacyFallback</code> |
| <code>PARRY</code><br><code>Parry</code> | 招架<br>Parry | 1 能量 | 普通：君王之剑现在能让你获得10点格挡。<br>升级：君王之剑现在能让你获得14点格挡。 | <code>LegacyFallback</code> |
| <code>PILLAR_OF_CREATION</code><br><code>PillarOfCreation</code> | 创世之柱<br>Pillar of Creation | 1 能量 | 普通：你每次生成卡牌时，获得2点格挡。<br>升级：你每次生成卡牌时，获得3点格挡。 | <code>LegacyFallback</code> |
| <code>ROYALTIES</code><br><code>Royalties</code> | 王国资产<br>Royalties | 1 能量 | 普通：在战斗结束时，获得30金币。<br>升级：在战斗结束时，获得40金币。 | <code>LegacyFallback</code> |
| <code>SEEKING_EDGE</code><br><code>SeekingEdge</code> | 追踪之刃<br>Seeking Edge | 1 能量 | 普通：铸造7。<br>君王之剑现在会对所有敌人造成伤害。<br>升级：铸造11。<br>君王之剑现在会对所有敌人造成伤害。 | <code>LegacyFallback</code> |
| <code>SPECTRUM_SHIFT</code><br><code>SpectrumShift</code> | 光谱偏移<br>Spectrum Shift | 2→1 能量 | 在你的回合开始时，将1张随机无色牌添加到你的手牌中。 | <code>LegacyFallback</code> |
| <code>SWORD_SAGE</code><br><code>SwordSage</code> | 剑圣<br>Sword Sage | 2→1 能量 | 君王之剑获得重放1。 | <code>LegacyFallback</code> |
| <code>THE_SEALED_THRONE</code><br><code>TheSealedThrone</code> | 封印王座<br>The Sealed Throne | 1→0 能量；3 星能 | 你每打出一张牌，获得星能。 | <code>LegacyFallback</code> |
| <code>TYRANNY</code><br><code>Tyranny</code> | 暴政<br>Tyranny | 1 能量 | 在你的回合开始时，抽一张牌，并从你的手牌中消耗1张牌。 | <code>LegacyFallback</code> |
| <code>VOID_FORM</code><br><code>VoidForm</code> | 虚空形态<br>Void Form | 3 能量 | 结束你的回合。<br>你可以免费打出每回合的前2张牌。 | <code>LegacyFallback</code> |
