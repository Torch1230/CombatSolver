# 静默猎手能力牌

本表列出游戏版本 0.111.0 的 SilentCardPool 中全部 18 张能力牌：17 张属于 CombatSolver 单人建模范围，1 张由游戏标记为 MultiplayerOnly。中英文名称与效果原文来自同版本 PCK 的 localization/zhs/cards.json 和 localization/eng/cards.json；数值、固有与奇巧属性由同版本原版卡牌实例按普通/升级状态核对。表中把能量图片图标写成文字，卡牌属性单列在效果中，效果语义不改写。

本轮用户明确授权先按实现者理解量化静默猎手，因此17张单人牌标为 `QuantifiedDraft`。第一版公式和合同已经建立，但经复核确认静态乘法不足以处理跨回合阈值与组合收益，禁止直接接入生产搜索；后续以[第二版量化与路线保护方案](silent-v2-valuation-and-retention-plan-20260917.md)为准。`OutOfScopeMultiplayer` 只保留完整卡池资料，不进入单人求解器建模。

用户已逐张给出17张单人能力牌的强弱、启动条件与兑现理解，权威记录位于第二版方案的“玩家逐卡建模输入”一节。卡池表继续只记录原版效果，避免把官方语义、玩家策略判断和实现状态混成一列。

| ID / 实现类型 | 官方名称（中 / 英） | 费用（普通→升级） | 游戏效果 | 建模状态 |
|---|---|---|---|---|
| <code>ABRASIVE</code><br><code>Abrasive</code> | 磨蚀<br>Abrasive | 3 能量 | 卡牌属性：奇巧。<br>普通：获得1点敏捷。<br>获得4点荆棘。<br>升级：获得1点敏捷。<br>获得6点荆棘。 | <code>QuantifiedDraft</code> |
| <code>ACCELERANT</code><br><code>Accelerant</code> | 触媒<br>Accelerant | 1 能量 | 普通：中毒会额外触发1次。<br>升级：中毒会额外触发2次。 | <code>QuantifiedDraft</code> |
| <code>ACCURACY</code><br><code>Accuracy</code> | 精准<br>Accuracy | 1 能量 | 普通：小刀额外造成4点伤害。<br>升级：小刀额外造成6点伤害。 | <code>QuantifiedDraft</code> |
| <code>AFTERIMAGE</code><br><code>Afterimage</code> | 余像<br>Afterimage | 1 能量 | 普通：你每打出一张牌，都获得1点格挡。<br>升级：固有。你每打出一张牌，都获得1点格挡。 | <code>QuantifiedDraft</code> |
| <code>ENVENOM</code><br><code>Envenom</code> | 涂毒<br>Envenom | 2 能量 | 普通：每有一次攻击造成未被格挡的伤害，就给予1层中毒。<br>升级：每有一次攻击造成未被格挡的伤害，就给予2层中毒。 | <code>QuantifiedDraft</code> |
| <code>FAN_OF_KNIVES</code><br><code>FanOfKnives</code> | 刀扇<br>Fan of Knives | 2 能量 | 普通：小刀现在会攻击所有敌人。<br>将4张小刀添加到你的手牌。<br>升级：小刀现在会攻击所有敌人。<br>将5张小刀添加到你的手牌。 | <code>QuantifiedDraft</code> |
| <code>FOOTWORK</code><br><code>Footwork</code> | 灵动步法<br>Footwork | 1 能量 | 普通：获得2点敏捷。<br>升级：获得3点敏捷。 | <code>QuantifiedDraft</code> |
| <code>INFINITE_BLADES</code><br><code>InfiniteBlades</code> | 无尽刀刃<br>Infinite Blades | 1 能量 | 普通：在你的回合开始时，在你的手牌中加入1张小刀。<br>升级：固有。在你的回合开始时，在你的手牌中加入1张小刀。 | <code>QuantifiedDraft</code> |
| <code>MASTER_PLANNER</code><br><code>MasterPlanner</code> | 谋划专家<br>Master Planner | 2→1 能量 | 当你打出技能牌时，该牌获得奇巧。 | <code>QuantifiedDraft</code> |
| <code>NOXIOUS_FUMES</code><br><code>NoxiousFumes</code> | 毒雾<br>Noxious Fumes | 1 能量 | 普通：在你的回合开始时，给予所有敌人2层中毒。<br>升级：在你的回合开始时，给予所有敌人3层中毒。 | <code>QuantifiedDraft</code> |
| <code>PHANTOM_BLADES</code><br><code>PhantomBlades</code> | 幻影之刃<br>Phantom Blades | 1 能量 | 普通：小刀获得保留。<br>你在每回合打出的第一张小刀额外造成9点伤害。<br>升级：小刀获得保留。<br>你在每回合打出的第一张小刀额外造成12点伤害。 | <code>QuantifiedDraft</code> |
| <code>SERPENT_FORM</code><br><code>SerpentForm</code> | 群蛇形态<br>Serpent Form | 3 能量 | 普通：你每打出一张牌，就对随机一名敌人造成4点伤害。<br>升级：你每打出一张牌，就对随机一名敌人造成6点伤害。 | <code>QuantifiedDraft</code> |
| <code>SNEAKY</code><br><code>Sneaky</code> | 鬼祟<br>Sneaky | 2 能量 | 普通：每当其他玩家攻击一名敌人时，获得1点格挡。<br>升级：每当其他玩家攻击一名敌人时，获得2点格挡。 | <code>OutOfScopeMultiplayer</code> |
| <code>SPEEDSTER</code><br><code>Speedster</code> | 速行者<br>Speedster | 2 能量 | 普通：每当你在回合进行中抽到一张牌时，对所有敌人造成2点伤害。<br>升级：固有。每当你在回合进行中抽到一张牌时，对所有敌人造成2点伤害。 | <code>QuantifiedDraft</code> |
| <code>TOOLS_OF_THE_TRADE</code><br><code>ToolsOfTheTrade</code> | 必备工具<br>Tools of the Trade | 1→0 能量 | 在你的回合开始时，抽1张牌，丢弃1张牌。 | <code>QuantifiedDraft</code> |
| <code>TRACKING</code><br><code>Tracking</code> | 跟踪<br>Tracking | 2→1 能量 | 处于虚弱状态的敌人受到的攻击伤害增加50%。 | <code>QuantifiedDraft</code> |
| <code>WELL_LAID_PLANS</code><br><code>WellLaidPlans</code> | 计划妥当<br>Well-Laid Plans | 2→1 能量 | 在你的回合结束时，你不再丢弃你的手牌。 | <code>QuantifiedDraft</code> |
| <code>WRAITH_FORM</code><br><code>WraithForm</code> | 幽魂形态<br>Wraith Form | 3 能量 | 普通：获得2无实体。<br>在你的回合开始时，失去1点敏捷。<br>升级：获得3无实体。<br>在你的回合开始时，失去1点敏捷。 | <code>QuantifiedDraft</code> |
