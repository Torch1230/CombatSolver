# 故障机器人能力牌

本表列出游戏版本 0.111.0 的 DefectCardPool 中全部 22 张能力牌：21 张属于 CombatSolver 单人建模范围，1 张由游戏标记为 MultiplayerOnly。中英文名称与效果原文来自同版本 PCK 的 localization/zhs/cards.json 和 localization/eng/cards.json；数值由同版本原版卡牌实例按普通/升级状态格式化。表中只把能量与星能图片图标写成文字，其余效果语义不改写。

LegacyFallback 表示尚未建模，必须等用户逐卡给出理解；OutOfScopeMultiplayer 只保留完整卡池资料，不进入单人求解器建模。

| ID / 实现类型 | 官方名称（中 / 英） | 费用（普通→升级） | 游戏效果 | 建模状态 |
|---|---|---|---|---|
| <code>BIASED_COGNITION</code><br><code>BiasedCognition</code> | 偏差认知<br>Biased Cognition | 1 能量 | 普通：获得5点集中。<br>在你的回合开始时，失去1点集中。<br>升级：获得6点集中。<br>在你的回合开始时，失去1点集中。 | <code>LegacyFallback</code> |
| <code>BUFFER</code><br><code>Buffer</code> | 缓冲<br>Buffer | 2 能量 | 普通：阻止下1次你受到的生命值损伤。<br>升级：阻止下2次你受到的生命值损伤。 | <code>LegacyFallback</code> |
| <code>BULK_UP</code><br><code>BulkUp</code> | 暴涨<br>Bulk Up | 2 能量 | 普通：失去1个充能球栏位。<br>获得2点力量。<br>获得2点敏捷。<br>升级：失去1个充能球栏位。<br>获得3点力量。<br>获得3点敏捷。 | <code>LegacyFallback</code> |
| <code>CAPACITOR</code><br><code>Capacitor</code> | 扩容<br>Capacitor | 1 能量 | 普通：获得2个充能球栏位。<br>升级：获得3个充能球栏位。 | <code>LegacyFallback</code> |
| <code>CONSUMING_SHADOW</code><br><code>ConsumingShadow</code> | 吞噬暗影<br>Consuming Shadow | 2 能量 | 普通：生成2个黑暗充能球。<br>在你的回合结束时，激发你最左侧的充能球。<br>升级：生成3个黑暗充能球。<br>在你的回合结束时，激发你最左侧的充能球。 | <code>LegacyFallback</code> |
| <code>COOLANT</code><br><code>Coolant</code> | 冷却剂<br>Coolant | 1 能量 | 普通：在你的回合开始时，你每有一种不同的充能球，就获得2点格挡。<br>升级：在你的回合开始时，你每有一种不同的充能球，就获得3点格挡。 | <code>LegacyFallback</code> |
| <code>CREATIVE_AI</code><br><code>CreativeAi</code> | 创造性AI<br>Creative AI | 3→2 能量 | 在你的回合开始时，将一张随机能力牌加入你的手牌。 | <code>LegacyFallback</code> |
| <code>DEFRAGMENT</code><br><code>Defragment</code> | 碎片整理<br>Defragment | 1 能量 | 普通：获得1点集中。<br>升级：获得2点集中。 | <code>LegacyFallback</code> |
| <code>ECHO_FORM</code><br><code>EchoForm</code> | 回响形态<br>Echo Form | 3 能量 | 你每回合打出的第一张牌会被打出两次。 | <code>LegacyFallback</code> |
| <code>FERAL</code><br><code>Feral</code> | 野性<br>Feral | 2→1 能量 | 你每回合打出的第一张<br>耗能为0点能量的攻击牌，<br>会放回你的手牌。 | <code>LegacyFallback</code> |
| <code>HAILSTORM</code><br><code>Hailstorm</code> | 冰雹风暴<br>Hailstorm | 1 能量 | 普通：在你的回合结束时，如果你有冰霜充能球，则对所有敌人造成6点伤害。<br>升级：在你的回合结束时，如果你有冰霜充能球，则对所有敌人造成8点伤害。 | <code>LegacyFallback</code> |
| <code>ITERATION</code><br><code>Iteration</code> | 迭代<br>Iteration | 1 能量 | 普通：每回合你第一次抽到状态牌时，抽2张牌。<br>升级：每回合你第一次抽到状态牌时，抽3张牌。 | <code>LegacyFallback</code> |
| <code>LOOP</code><br><code>Loop</code> | 循环<br>Loop | 1 能量 | 普通：在你的回合开始时，触发你最右侧的一个充能球的被动能力。<br>升级：在你的回合开始时，触发你最右侧的一个充能球的被动能力2次。 | <code>LegacyFallback</code> |
| <code>MACHINE_LEARNING</code><br><code>MachineLearning</code> | 机器学习<br>Machine Learning | 1 能量 | 在你的回合开始时，额外抽1张牌。 | <code>LegacyFallback</code> |
| <code>ONE_FOR_ALL</code><br><code>OneForAll</code> | 一心化万<br>One for All | 1 能量 | 普通：所有人的0点能量费攻击牌额外造成3点伤害。<br>升级：所有人的0点能量费攻击牌额外造成4点伤害。 | <code>OutOfScopeMultiplayer</code> |
| <code>SMOKESTACK</code><br><code>Smokestack</code> | 烟囱<br>Smokestack | 1 能量 | 普通：每当你生成一张状态牌时，对所有敌人造成5点伤害。<br>升级：每当你生成一张状态牌时，对所有敌人造成7点伤害。 | <code>LegacyFallback</code> |
| <code>SPINNER</code><br><code>Spinner</code> | 旋转工艺<br>Spinner | 1 能量 | 普通：在你的回合开始时，生成1个玻璃充能球。<br>升级：生成1个玻璃充能球。<br>在你的回合开始时，生成1个玻璃充能球。 | <code>LegacyFallback</code> |
| <code>STORM</code><br><code>Storm</code> | 雷暴<br>Storm | 1 能量 | 普通：每当你打出一张能力牌时，生成1个闪电充能球。<br>升级：每当你打出一张能力牌时，生成2个闪电充能球。 | <code>LegacyFallback</code> |
| <code>SUBROUTINE</code><br><code>Subroutine</code> | 子程序<br>Subroutine | 1→0 能量 | 当你打出一张能力牌时，获得1点能量。 | <code>LegacyFallback</code> |
| <code>THUNDER</code><br><code>Thunder</code> | 雷霆<br>Thunder | 1 能量 | 普通：每当你激发闪电充能球时，对被命中的敌人造成8点伤害。<br>升级：每当你激发闪电充能球时，对被命中的敌人造成11点伤害。 | <code>LegacyFallback</code> |
| <code>TRASH_TO_TREASURE</code><br><code>TrashToTreasure</code> | 化废为宝<br>Trash to Treasure | 1→0 能量 | 每当你生成状态牌的时候，随机生成一个充能球。 | <code>LegacyFallback</code> |
| <code>WHITE_NOISE</code><br><code>WhiteNoise</code> | 白噪声<br>White Noise | 1→0 能量 | 将一张随机能力牌加入你的手牌。这张牌在本回合内免费打出。 | <code>LegacyFallback</code> |
