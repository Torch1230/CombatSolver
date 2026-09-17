# 无色能力牌

本表列出游戏版本 0.111.0 的 ColorlessCardPool 中全部 13 张能力牌：12 张属于 CombatSolver 单人建模范围，1 张由游戏标记为 MultiplayerOnly。中英文名称与效果原文来自同版本 PCK 的 localization/zhs/cards.json 和 localization/eng/cards.json；数值由同版本原版卡牌实例按普通/升级状态格式化。表中只把能量与星能图片图标写成文字，其余效果语义不改写。

LegacyFallback 表示尚未建模，必须等用户逐卡给出理解；OutOfScopeMultiplayer 只保留完整卡池资料，不进入单人求解器建模。

| ID / 实现类型 | 官方名称（中 / 英） | 费用（普通→升级） | 游戏效果 | 建模状态 |
|---|---|---|---|---|
| <code>AUTOMATION</code><br><code>Automation</code> | 自动化<br>Automation | 1→0 能量 | 你每抽10张牌，获得1点能量。 | <code>LegacyFallback</code> |
| <code>BEACON_OF_HOPE</code><br><code>BeaconOfHope</code> | 希望灯塔<br>Beacon of Hope | 2 能量 | 每当你在你的回合获得格挡时，其他玩家获得相应一半的格挡。 | <code>OutOfScopeMultiplayer</code> |
| <code>CALAMITY</code><br><code>Calamity</code> | 劫难<br>Calamity | 3→2 能量 | 每当你打出一张攻击牌时，将一张随机攻击牌添加到你的手牌。 | <code>LegacyFallback</code> |
| <code>ENTROPY</code><br><code>Entropy</code> | 熵<br>Entropy | 1 能量 | 在你的回合开始时，变化你手牌中的1张牌。 | <code>LegacyFallback</code> |
| <code>ETERNAL_ARMOR</code><br><code>EternalArmor</code> | 永恒铠甲<br>Eternal Armor | 3 能量 | 普通：获得9层覆甲。<br>升级：获得12层覆甲。 | <code>LegacyFallback</code> |
| <code>FASTEN</code><br><code>Fasten</code> | 勒紧<br>Fasten | 1 能量 | 普通：从“防御”牌中额外获得4点格挡。<br>升级：从“防御”牌中额外获得6点格挡。 | <code>LegacyFallback</code> |
| <code>MAYHEM</code><br><code>Mayhem</code> | 乱战<br>Mayhem | 2→1 能量 | 在你的回合开始时，打出你抽牌堆顶部的牌。 | <code>LegacyFallback</code> |
| <code>NOSTALGIA</code><br><code>Nostalgia</code> | 怀旧<br>Nostalgia | 1→0 能量 | 每回合首次打出攻击或技能牌时，将其置于你的抽牌堆顶端。 | <code>LegacyFallback</code> |
| <code>PANACHE</code><br><code>Panache</code> | 神气制胜<br>Panache | 0 能量 | 普通：每当你在一回合内打出五张牌时，对所有敌人造成10点伤害。<br>升级：每当你在一回合内打出五张牌时，对所有敌人造成14点伤害。 | <code>LegacyFallback</code> |
| <code>PREP_TIME</code><br><code>PrepTime</code> | 准备时间<br>Prep Time | 1 能量 | 普通：在你的回合开始时，获得4点活力。<br>升级：在你的回合开始时，获得6点活力。 | <code>LegacyFallback</code> |
| <code>PROWESS</code><br><code>Prowess</code> | 非凡技艺<br>Prowess | 1 能量 | 普通：获得1点力量。<br>获得1点敏捷。<br>升级：获得2点力量。<br>获得2点敏捷。 | <code>LegacyFallback</code> |
| <code>ROLLING_BOULDER</code><br><code>RollingBoulder</code> | 滚石<br>Rolling Boulder | 3 能量 | 普通：在你的回合开始时，对所有敌人造成5点伤害，然后将该伤害增加5点。<br>升级：在你的回合开始时，对所有敌人造成10点伤害，然后将该伤害增加5点。 | <code>LegacyFallback</code> |
| <code>STRATAGEM</code><br><code>Stratagem</code> | 计策<br>Stratagem | 1→0 能量 | 每当你的抽牌堆打乱洗牌时，选择一张牌放入你的手牌。 | <code>LegacyFallback</code> |
