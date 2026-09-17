# 亡灵契约师能力牌

本表列出游戏版本 0.111.0 的 NecrobinderCardPool 中全部 20 张能力牌：18 张属于 CombatSolver 单人建模范围，2 张由游戏标记为 MultiplayerOnly。中英文名称与效果原文来自同版本 PCK 的 localization/zhs/cards.json 和 localization/eng/cards.json；数值由同版本原版卡牌实例按普通/升级状态格式化。表中只把能量与星能图片图标写成文字，其余效果语义不改写。

LegacyFallback 表示尚未建模，必须等用户逐卡给出理解；OutOfScopeMultiplayer 只保留完整卡池资料，不进入单人求解器建模。

| ID / 实现类型 | 官方名称（中 / 英） | 费用（普通→升级） | 游戏效果 | 建模状态 |
|---|---|---|---|---|
| <code>CACOPHONY</code><br><code>Cacophony</code> | 不谐合曲<br>Cacophony | 2 能量 | 普通：所有玩家每抽33张牌，就对随机一名敌人造成66点伤害。<br>升级：所有玩家每抽33张牌，就对随机一名敌人造成99点伤害。 | <code>OutOfScopeMultiplayer</code> |
| <code>CALCIFY</code><br><code>Calcify</code> | 钙化<br>Calcify | 1 能量 | 普通：奥斯提的攻击额外造成4点伤害。<br>升级：奥斯提的攻击额外造成6点伤害。 | <code>LegacyFallback</code> |
| <code>CALL_OF_THE_VOID</code><br><code>CallOfTheVoid</code> | 虚空之唤<br>Call of the Void | 1 能量 | 在你的回合开始时，将1张随机牌添加到你的手牌中。添加的牌会获得虚无。 | <code>LegacyFallback</code> |
| <code>COUNTDOWN</code><br><code>Countdown</code> | 倒数计时<br>Countdown | 1 能量 | 普通：在你的回合开始时，给予随机敌人6层灾厄。<br>升级：在你的回合开始时，给予随机敌人9层灾厄。 | <code>LegacyFallback</code> |
| <code>DANSE_MACABRE</code><br><code>DanseMacabre</code> | 死亡之舞<br>Danse Macabre | 1 能量 | 普通：每当你打出一张耗能大于等于2点能量的牌时，获得4点格挡。<br>升级：每当你打出一张耗能大于等于2点能量的牌时，获得6点格挡。 | <code>LegacyFallback</code> |
| <code>DEMESNE</code><br><code>Demesne</code> | 领域<br>Demesne | 3→2 能量 | 在你的回合开始时，获得1点能量并额外多抽1张牌。 | <code>LegacyFallback</code> |
| <code>DEVOUR_LIFE</code><br><code>DevourLife</code> | 吞噬生命<br>Devour Life | 1 能量 | 普通：每当你打出一张灵魂时，召唤1。<br>升级：每当你打出一张灵魂时，召唤2。 | <code>LegacyFallback</code> |
| <code>FORBIDDEN_GRIMOIRE</code><br><code>ForbiddenGrimoire</code> | 禁忌魔典<br>Forbidden Grimoire | 2→1 能量 | 在战斗结束时，你可以从你的牌组中选一张牌移除。 | <code>LegacyFallback</code> |
| <code>FRIENDSHIP</code><br><code>Friendship</code> | 友谊<br>Friendship | 1 能量 | 普通：失去2点力量。<br>在每个回合开始时获得1点能量。<br>升级：失去1点力量。<br>在每个回合开始时获得1点能量。 | <code>LegacyFallback</code> |
| <code>HAUNT</code><br><code>Haunt</code> | 纠缠<br>Haunt | 1 能量 | 普通：每当你打出一张灵魂时，随机一名敌人失去7点生命。<br>升级：每当你打出一张灵魂时，随机一名敌人失去9点生命。 | <code>LegacyFallback</code> |
| <code>LETHALITY</code><br><code>Lethality</code> | 致死性<br>Lethality | 1 能量 | 普通：每回合的第一张攻击牌会造成50%额外伤害。<br>升级：每回合的第一张攻击牌会造成75%额外伤害。 | <code>LegacyFallback</code> |
| <code>NECRO_MASTERY</code><br><code>NecroMastery</code> | 亡灵精通<br>Necro Mastery | 2 能量 | 普通：召唤5。<br>每当奥斯提失去生命值时，<br>所有敌人失去等量生命值。<br>升级：召唤8。<br>每当奥斯提失去生命值时，<br>所有敌人失去等量生命值。 | <code>LegacyFallback</code> |
| <code>NEUROSURGE</code><br><code>Neurosurge</code> | 精神过载<br>Neurosurge | 0 能量 | 普通：获得3点能量。<br>抽2张牌。<br>在你的回合开始时，给予自身3层灾厄。<br>升级：获得4点能量。<br>抽2张牌。<br>在你的回合开始时，给予自身3层灾厄。 | <code>LegacyFallback</code> |
| <code>PAGESTORM</code><br><code>Pagestorm</code> | 书页风暴<br>Pagestorm | 1→0 能量 | 每当你抽到一张虚无牌时, 抽1张牌。 | <code>LegacyFallback</code> |
| <code>REAPER_FORM</code><br><code>ReaperForm</code> | 死神形态<br>Reaper Form | 3 能量 | 每当你的攻击造成伤害时，同时给予等量的灾厄。 | <code>LegacyFallback</code> |
| <code>SENTRY_MODE</code><br><code>SentryMode</code> | 哨卫模式<br>Sentry Mode | 2→1 能量 | 在你的回合开始时，将1张扫荡凝视加入你的手牌。 | <code>LegacyFallback</code> |
| <code>SHROUD</code><br><code>Shroud</code> | 厄运之衣<br>Shroud | 1 能量 | 普通：每当你给予灾厄时，获得3点格挡。<br>升级：每当你给予灾厄时，获得4点格挡。 | <code>LegacyFallback</code> |
| <code>SLEIGHT_OF_FLESH</code><br><code>SleightOfFlesh</code> | 血肉戏法<br>Sleight of Flesh | 2 能量 | 普通：每当你给予一个敌人负面状态时，使其受到9点伤害。<br>升级：每当你给予一个敌人负面状态时，使其受到13点伤害。 | <code>LegacyFallback</code> |
| <code>SOULBOUND</code><br><code>Soulbound</code> | 灵魂绑定<br>Soulbound | 1 能量 | 选择一名盟友。<br>每当你生成一张灵魂时，将一张灵魂，添加至他的抽牌堆。 | <code>OutOfScopeMultiplayer</code> |
| <code>SPIRIT_OF_ASH</code><br><code>SpiritOfAsh</code> | 灰烬之灵<br>Spirit of Ash | 1 能量 | 普通：每当你打出一张虚无牌时，获得4点格挡。<br>升级：每当你打出一张虚无牌时，获得5点格挡。 | <code>LegacyFallback</code> |
