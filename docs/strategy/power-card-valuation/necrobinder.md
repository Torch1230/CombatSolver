# 亡灵契约师能力牌

本表列出游戏版本 0.111.0 的 NecrobinderCardPool 中全部 20 张能力牌：18 张属于 CombatSolver 单人建模范围，2 张由游戏标记为 MultiplayerOnly。中英文名称与效果原文来自同版本 PCK 的 localization/zhs/cards.json 和 localization/eng/cards.json；数值由同版本原版卡牌实例按普通/升级状态格式化。表中只把能量与星能图片图标写成文字，其余效果语义不改写。

`QuantifiedDraft` 表示已按实现者理解完成逐卡公式、登记和纯合同，尚待玩家复核。`OutOfScopeMultiplayer` 只保留完整卡池资料，不进入单人求解器建模。

| ID / 实现类型 | 官方名称（中 / 英） | 费用（普通→升级） | 游戏效果 | 建模状态 |
|---|---|---|---|---|
| <code>CACOPHONY</code><br><code>Cacophony</code> | 不谐合曲<br>Cacophony | 2 能量 | 普通：所有玩家每抽33张牌，就对随机一名敌人造成66点伤害。<br>升级：所有玩家每抽33张牌，就对随机一名敌人造成99点伤害。 | <code>OutOfScopeMultiplayer</code> |
| <code>CALCIFY</code><br><code>Calcify</code> | 钙化<br>Calcify | 1 能量 | 普通：奥斯提的攻击额外造成4点伤害。<br>升级：奥斯提的攻击额外造成6点伤害。 | <code>QuantifiedDraft</code> |
| <code>CALL_OF_THE_VOID</code><br><code>CallOfTheVoid</code> | 虚空之唤<br>Call of the Void | 1 能量 | 在你的回合开始时，将1张随机牌添加到你的手牌中。添加的牌会获得虚无。 | <code>QuantifiedDraft</code> |
| <code>COUNTDOWN</code><br><code>Countdown</code> | 倒数计时<br>Countdown | 1 能量 | 普通：在你的回合开始时，给予随机敌人6层灾厄。<br>升级：在你的回合开始时，给予随机敌人9层灾厄。 | <code>QuantifiedDraft</code> |
| <code>DANSE_MACABRE</code><br><code>DanseMacabre</code> | 死亡之舞<br>Danse Macabre | 1 能量 | 普通：每当你打出一张耗能大于等于2点能量的牌时，获得4点格挡。<br>升级：每当你打出一张耗能大于等于2点能量的牌时，获得6点格挡。 | <code>QuantifiedDraft</code> |
| <code>DEMESNE</code><br><code>Demesne</code> | 领域<br>Demesne | 3→2 能量 | 在你的回合开始时，获得1点能量并额外多抽1张牌。 | <code>QuantifiedDraft</code> |
| <code>DEVOUR_LIFE</code><br><code>DevourLife</code> | 吞噬生命<br>Devour Life | 1 能量 | 普通：每当你打出一张灵魂时，召唤1。<br>升级：每当你打出一张灵魂时，召唤2。 | <code>QuantifiedDraft</code> |
| <code>FORBIDDEN_GRIMOIRE</code><br><code>ForbiddenGrimoire</code> | 禁忌魔典<br>Forbidden Grimoire | 2→1 能量 | 在战斗结束时，你可以从你的牌组中选一张牌移除。 | <code>QuantifiedDraft</code> |
| <code>FRIENDSHIP</code><br><code>Friendship</code> | 友谊<br>Friendship | 1 能量 | 普通：失去2点力量。<br>在每个回合开始时获得1点能量。<br>升级：失去1点力量。<br>在每个回合开始时获得1点能量。 | <code>QuantifiedDraft</code> |
| <code>HAUNT</code><br><code>Haunt</code> | 纠缠<br>Haunt | 1 能量 | 普通：每当你打出一张灵魂时，随机一名敌人失去7点生命。<br>升级：每当你打出一张灵魂时，随机一名敌人失去9点生命。 | <code>QuantifiedDraft</code> |
| <code>LETHALITY</code><br><code>Lethality</code> | 致死性<br>Lethality | 1 能量 | 普通：每回合的第一张攻击牌会造成50%额外伤害。<br>升级：每回合的第一张攻击牌会造成75%额外伤害。 | <code>QuantifiedDraft</code> |
| <code>NECRO_MASTERY</code><br><code>NecroMastery</code> | 亡灵精通<br>Necro Mastery | 2 能量 | 普通：召唤5。<br>每当奥斯提失去生命值时，<br>所有敌人失去等量生命值。<br>升级：召唤8。<br>每当奥斯提失去生命值时，<br>所有敌人失去等量生命值。 | <code>QuantifiedDraft</code> |
| <code>NEUROSURGE</code><br><code>Neurosurge</code> | 精神过载<br>Neurosurge | 0 能量 | 普通：获得3点能量。<br>抽2张牌。<br>在你的回合开始时，给予自身3层灾厄。<br>升级：获得4点能量。<br>抽2张牌。<br>在你的回合开始时，给予自身3层灾厄。 | <code>QuantifiedDraft</code> |
| <code>PAGESTORM</code><br><code>Pagestorm</code> | 书页风暴<br>Pagestorm | 1→0 能量 | 每当你抽到一张虚无牌时, 抽1张牌。 | <code>QuantifiedDraft</code> |
| <code>REAPER_FORM</code><br><code>ReaperForm</code> | 死神形态<br>Reaper Form | 3 能量 | 每当你的攻击造成伤害时，同时给予等量的灾厄。 | <code>QuantifiedDraft</code> |
| <code>SENTRY_MODE</code><br><code>SentryMode</code> | 哨卫模式<br>Sentry Mode | 2→1 能量 | 在你的回合开始时，将1张扫荡凝视加入你的手牌。 | <code>QuantifiedDraft</code> |
| <code>SHROUD</code><br><code>Shroud</code> | 厄运之衣<br>Shroud | 1 能量 | 普通：每当你给予灾厄时，获得3点格挡。<br>升级：每当你给予灾厄时，获得4点格挡。 | <code>QuantifiedDraft</code> |
| <code>SLEIGHT_OF_FLESH</code><br><code>SleightOfFlesh</code> | 血肉戏法<br>Sleight of Flesh | 2 能量 | 普通：每当你给予一个敌人负面状态时，使其受到9点伤害。<br>升级：每当你给予一个敌人负面状态时，使其受到13点伤害。 | <code>QuantifiedDraft</code> |
| <code>SOULBOUND</code><br><code>Soulbound</code> | 灵魂绑定<br>Soulbound | 1 能量 | 选择一名盟友。<br>每当你生成一张灵魂时，将一张灵魂，添加至他的抽牌堆。 | <code>OutOfScopeMultiplayer</code> |
| <code>SPIRIT_OF_ASH</code><br><code>SpiritOfAsh</code> | 灰烬之灵<br>Spirit of Ash | 1 能量 | 普通：每当你打出一张虚无牌时，获得4点格挡。<br>升级：每当你打出一张虚无牌时，获得5点格挡。 | <code>QuantifiedDraft</code> |

## 逐卡建模（Draft）

实现位置：`src/Search/PowerCardValuation/Cards/Necrobinder/`。18 张单人 `CardType.Power` 已登记；`CACOPHONY`、`SOULBOUND` 为 MultiplayerOnly 排除。`FORBIDDEN_GRIMOIRE` 为战后移除牌收益，登记资料与估值但不创建战斗内承诺。

| ID | 机制族 | 优先级 | 触发证据 | 开局投影口径 | 置信度 | 待玩家复核 |
|---|---|---|---|---|---|---|
| CALCIFY | SummonEngine | Strong | 奥斯提存活 | 奥斯提攻击数×增伤×剩余回合 | 中 | 奥斯提存活窗口 |
| CALL_OF_THE_VOID | CardGenerationEngine | Core | 剩余回合>1 | 每回合随机牌价值（虚无风险未计入） | 中 | 虚无牌的真实代价 |
| COUNTDOWN | DoomEngine | Strong | 仍有敌人且剩余回合>1 | 每回合灾厄×剩余回合 | 中 | 灾厄触发时点与击杀 |
| DANSE_MACABRE | BlockTriggerEngine | Strong | 剩余回合>1且有≥2费牌 | 达标牌次数×格挡 | 中 | 高费牌密度 |
| DEMESNE | EnergyEngine+HandEngine | Core | 剩余回合>1 | 每回合能量+额外抽牌 | 高 | 虚无属性与时机 |
| DEVOUR_LIFE | SummonEngine | Strong | 有灵魂牌或召唤来源 | 灵魂打出次数×召唤量 | 中 | 灵魂来源与召唤上限 |
| FORBIDDEN_GRIMOIRE | CrossCombatGrowth | Low（不承诺） | —（纯战后收益） | 战后移除牌价值 | 中 | 是否值得牺牲战斗节奏 |
| FRIENDSHIP | EnergyEngine | Strong | 剩余回合>1且有攻击 | 每回合能量，扣力量损失 | 中 | 力量损失的反协同 |
| HAUNT | DoomEngine+DamageEngine | Strong | 有灵魂牌 | 灵魂打出次数×随机敌人伤害 | 中 | 随机目标 |
| LETHALITY | DamageEngine | Strong | 有攻击 | 每回合首张攻击的额外伤害×剩余回合 | 中 | 首张攻击选择与重放 |
| NECRO_MASTERY | SummonEngine+DamageEngine | Core | 有奥斯提或召唤来源 | 奥斯提生命损失转群伤 | 中 | 奥斯提掉血频率 |
| NEUROSURGE | EnergyEngine+LifeInvestment | Strong | 有攻击或剩余回合>1 | 立即能量+抽牌，扣自身灾厄 | 中 | 自身灾厄的延迟死亡风险 |
| PAGESTORM | HandEngine | Normal | 有虚无牌 | 虚无牌抽牌次数×抽牌价值 | 中 | 虚无牌来源 |
| REAPER_FORM | DoomEngine+DamageEngine | Core（专搜） | 有攻击 | 攻击伤害等量施加灾厄 | 中 | 灾厄叠加与击杀 |
| SENTRY_MODE | CardGenerationEngine | Strong | 有奥斯提或剩余回合>1 | 每回合扫荡凝视的价值 | 中 | 生成牌依赖奥斯提 |
| SHROUD | BlockTriggerEngine+DoomEngine | Strong | 有灾厄施加来源 | 灾厄施加次数×格挡 | 中 | 灾厄来源密度 |
| SLEIGHT_OF_FLESH | DamageEngine+StatusAmplifier | Strong | 有攻击或减益来源 | 减益施加次数×伤害 | 中 | 非临时减益的判定 |
| SPIRIT_OF_ASH | BlockTriggerEngine | Strong | 有虚无牌 | 虚无牌打出次数×格挡 | 中 | 虚无牌来源 |

### 隐藏状态与时序（Draft 记录）

- `CALCIFY` 只加成奥斯提（宠物）的攻击，不加成玩家自身攻击。
- `COUNTDOWN` 给随机敌人施加灾厄；灾厄在敌方回合结束、且生命值不高于灾厄时击杀。
- `DEVOUR_LIFE`/`HAUNT` 只在打出“灵魂”这张牌时触发，取决于奥斯提/召唤状态。
- `DANSE_MACABRE` 的阈值是“结算费用≥2”，并在触发牌结算前获得格挡。
- `REAPER_FORM` 包含奥斯提攻击造成的伤害。
- `SHROUD` 在每一次施加灾厄时触发，包括自身灾厄。
- `SLEIGHT_OF_FLESH` 排除临时 Power 与零层，只对敌人施加减益时触发。
- `NEUROSURGE` 的自身灾厄是延迟死亡风险。
- `CACOPHONY`、`SOULBOUND` 为 MultiplayerOnly。

### 不确定项

- 灾厄结算、奥斯提生命损失、灵魂循环的收益依赖运行时状态，投影为保守代理。
- 兑现证据暂统一走通用状态改善回退。

## 玩家联合评审采纳（2026-09-17）

| 卡牌 | 变化 | 依据 |
|---|---|---|
| CALL_OF_THE_VOID | 加入专搜 | 回合开始才印牌且带虚无 |
| COUNTDOWN | 加入专搜 | 延时的延时灾厄 |
| DEMESNE | 加入专搜 | 高费且当回合净亏，虚无风险 |
| FRIENDSHIP | 加入专搜 | 扣力量+次回合才回能，双重负面易被劝退 |
| NECRO_MASTERY | Core→Normal，不专搜 | 玩家判定优先级降低 |
| NEUROSURGE | 加入专搜 | 自身灾厄惩罚过大导致假阴性 |
| SENTRY_MODE | 加入专搜 | 玩家要求重搜 |

- LETHALITY 时序核对：按源码，效果作用于**打出当回合的第一张攻击**（能力牌本身不是攻击，不占首攻），并作用于之后每个回合的第一张攻击；不是从下回合才开始。模型沿用该口径。
- SHROUD、SLEIGHT_OF_FLESH、SPIRIT_OF_ASH、HAUNT 等按外部意见采纳为优先开，但维持普通搜索可判，不专搜。
- FORBIDDEN_GRIMOIRE 与储君王国资产同理，按玩家指令不创建战斗内承诺，不为战后收益承担战损。
