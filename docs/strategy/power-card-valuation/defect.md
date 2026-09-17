# 故障机器人能力牌

本表列出游戏版本 0.111.0 的 DefectCardPool 中全部 21 张 `CardType.Power` 能力牌：20 张属于 CombatSolver 单人建模范围，1 张由游戏标记为 MultiplayerOnly。中英文名称与效果原文来自同版本 PCK 的 localization/zhs/cards.json 和 localization/eng/cards.json；数值由同版本原版卡牌实例按普通/升级状态格式化。表中只把能量与星能图片图标写成文字，其余效果语义不改写。

`QuantifiedDraft` 表示已按实现者理解完成逐卡公式、登记和纯合同，尚待玩家复核。`OutOfScopeMultiplayer` 只保留完整卡池资料，不进入单人求解器建模。

修正：`WHITE_NOISE`（白噪声）在源码中是 `CardType.Skill`，只是生成一张随机能力牌，本身不是能力牌，因此下表中它只作相关牌保留，不登记为能力牌模型。原目录曾把它计入能力牌，导致“22 张”。

| ID / 实现类型 | 官方名称（中 / 英） | 费用（普通→升级） | 游戏效果 | 建模状态 |
|---|---|---|---|---|
| <code>BIASED_COGNITION</code><br><code>BiasedCognition</code> | 偏差认知<br>Biased Cognition | 1 能量 | 普通：获得5点集中。<br>在你的回合开始时，失去1点集中。<br>升级：获得6点集中。<br>在你的回合开始时，失去1点集中。 | <code>QuantifiedDraft</code> |
| <code>BUFFER</code><br><code>Buffer</code> | 缓冲<br>Buffer | 2 能量 | 普通：阻止下1次你受到的生命值损伤。<br>升级：阻止下2次你受到的生命值损伤。 | <code>QuantifiedDraft</code> |
| <code>BULK_UP</code><br><code>BulkUp</code> | 暴涨<br>Bulk Up | 2 能量 | 普通：失去1个充能球栏位。<br>获得2点力量。<br>获得2点敏捷。<br>升级：失去1个充能球栏位。<br>获得3点力量。<br>获得3点敏捷。 | <code>QuantifiedDraft</code> |
| <code>CAPACITOR</code><br><code>Capacitor</code> | 扩容<br>Capacitor | 1 能量 | 普通：获得2个充能球栏位。<br>升级：获得3个充能球栏位。 | <code>QuantifiedDraft</code> |
| <code>CONSUMING_SHADOW</code><br><code>ConsumingShadow</code> | 吞噬暗影<br>Consuming Shadow | 2 能量 | 普通：生成2个黑暗充能球。<br>在你的回合结束时，激发你最左侧的充能球。<br>升级：生成3个黑暗充能球。<br>在你的回合结束时，激发你最左侧的充能球。 | <code>QuantifiedDraft</code> |
| <code>COOLANT</code><br><code>Coolant</code> | 冷却剂<br>Coolant | 1 能量 | 普通：在你的回合开始时，你每有一种不同的充能球，就获得2点格挡。<br>升级：在你的回合开始时，你每有一种不同的充能球，就获得3点格挡。 | <code>QuantifiedDraft</code> |
| <code>CREATIVE_AI</code><br><code>CreativeAi</code> | 创造性AI<br>Creative AI | 3→2 能量 | 在你的回合开始时，将一张随机能力牌加入你的手牌。 | <code>QuantifiedDraft</code> |
| <code>DEFRAGMENT</code><br><code>Defragment</code> | 碎片整理<br>Defragment | 1 能量 | 普通：获得1点集中。<br>升级：获得2点集中。 | <code>QuantifiedDraft</code> |
| <code>ECHO_FORM</code><br><code>EchoForm</code> | 回响形态<br>Echo Form | 3 能量 | 你每回合打出的第一张牌会被打出两次。 | <code>QuantifiedDraft</code> |
| <code>FERAL</code><br><code>Feral</code> | 野性<br>Feral | 2→1 能量 | 你每回合打出的第一张<br>耗能为0点能量的攻击牌，<br>会放回你的手牌。 | <code>QuantifiedDraft</code> |
| <code>HAILSTORM</code><br><code>Hailstorm</code> | 冰雹风暴<br>Hailstorm | 1 能量 | 普通：在你的回合结束时，如果你有冰霜充能球，则对所有敌人造成6点伤害。<br>升级：在你的回合结束时，如果你有冰霜充能球，则对所有敌人造成8点伤害。 | <code>QuantifiedDraft</code> |
| <code>ITERATION</code><br><code>Iteration</code> | 迭代<br>Iteration | 1 能量 | 普通：每回合你第一次抽到状态牌时，抽2张牌。<br>升级：每回合你第一次抽到状态牌时，抽3张牌。 | <code>QuantifiedDraft</code> |
| <code>LOOP</code><br><code>Loop</code> | 循环<br>Loop | 1 能量 | 普通：在你的回合开始时，触发你最右侧的一个充能球的被动能力。<br>升级：在你的回合开始时，触发你最右侧的一个充能球的被动能力2次。 | <code>QuantifiedDraft</code> |
| <code>MACHINE_LEARNING</code><br><code>MachineLearning</code> | 机器学习<br>Machine Learning | 1 能量 | 在你的回合开始时，额外抽1张牌。 | <code>QuantifiedDraft</code> |
| <code>ONE_FOR_ALL</code><br><code>OneForAll</code> | 一心化万<br>One for All | 1 能量 | 普通：所有人的0点能量费攻击牌额外造成3点伤害。<br>升级：所有人的0点能量费攻击牌额外造成4点伤害。 | <code>OutOfScopeMultiplayer</code> |
| <code>SMOKESTACK</code><br><code>Smokestack</code> | 烟囱<br>Smokestack | 1 能量 | 普通：每当你生成一张状态牌时，对所有敌人造成5点伤害。<br>升级：每当你生成一张状态牌时，对所有敌人造成7点伤害。 | <code>QuantifiedDraft</code> |
| <code>SPINNER</code><br><code>Spinner</code> | 旋转工艺<br>Spinner | 1 能量 | 普通：在你的回合开始时，生成1个玻璃充能球。<br>升级：生成1个玻璃充能球。<br>在你的回合开始时，生成1个玻璃充能球。 | <code>QuantifiedDraft</code> |
| <code>STORM</code><br><code>Storm</code> | 雷暴<br>Storm | 1 能量 | 普通：每当你打出一张能力牌时，生成1个闪电充能球。<br>升级：每当你打出一张能力牌时，生成2个闪电充能球。 | <code>QuantifiedDraft</code> |
| <code>SUBROUTINE</code><br><code>Subroutine</code> | 子程序<br>Subroutine | 1→0 能量 | 当你打出一张能力牌时，获得1点能量。 | <code>QuantifiedDraft</code> |
| <code>THUNDER</code><br><code>Thunder</code> | 雷霆<br>Thunder | 1 能量 | 普通：每当你激发闪电充能球时，对被命中的敌人造成8点伤害。<br>升级：每当你激发闪电充能球时，对被命中的敌人造成11点伤害。 | <code>QuantifiedDraft</code> |
| <code>TRASH_TO_TREASURE</code><br><code>TrashToTreasure</code> | 化废为宝<br>Trash to Treasure | 1→0 能量 | 每当你生成状态牌的时候，随机生成一个充能球。 | <code>QuantifiedDraft</code> |
| <code>WHITE_NOISE</code><br><code>WhiteNoise</code> | 白噪声<br>White Noise | 1→0 能量 | 将一张随机能力牌加入你的手牌。这张牌在本回合内免费打出。 | <code>NotAPowerCard</code>（Skill） |

## 逐卡建模（Draft）

实现位置：`src/Search/PowerCardValuation/Cards/Defect/`。20 张单人 `CardType.Power` 已登记；`ONE_FOR_ALL` 为 MultiplayerOnly 排除，`WHITE_NOISE` 是 Skill，均不登记。

| ID | 机制族 | 优先级 | 触发证据（须真实存在） | 开局投影口径 | 置信度 | 待玩家复核 |
|---|---|---|---|---|---|---|
| BIASED_COGNITION | FocusEngine | Core | 有充能球或充能来源 | 集中增量×球数×剩余回合 | 中 | 每回合掉集中对长线的反协同是否应更重 |
| BUFFER | DefenseEfficiency | Strong | 有当前或下回合来袭伤害 | 按层数抵消来袭伤害 | 高 | 何时开启最划算 |
| BULK_UP | StrengthGrowth+DexterityGrowth | Strong | 有攻击且有格挡技能 | 力量/敏捷进入行动前沿，扣球位损失 | 中 | 失去充能球栏位的反协同 |
| CAPACITOR | OrbEngine | Strong | 有球或充能来源 | 新增球位×剩余回合（球吞吐代理） | 中 | 无球时的空转 |
| CONSUMING_SHADOW | OrbEngine | Strong | 有充能球 | 每回合激发暗球的保守价值 | 低 | 暗球激发与集中联动 |
| COOLANT | OrbEngine+DefenseEfficiency | Strong | 有不同种类充能球 | 每回合不同球种数×格挡 | 高 | 球种数量与衰减 |
| CREATIVE_AI | CardGenerationEngine | Core | 剩余回合>1 | 每回合随机能力牌价值 | 中 | 生成随机能力牌的真实收益 |
| DEFRAGMENT | FocusEngine | Core | 有球或充能来源 | 集中×球数×剩余回合 | 高 | 集中对球被动/激发的实际兑现 |
| ECHO_FORM | AutoPlayEngine+CostReductionEngine | Strong（专搜） | 剩余回合>1且有牌可打 | 每回合首张牌翻倍的期望收益 | 中 | 首张牌选择与随机性 |
| FERAL | CostReductionEngine | Normal | 有0费攻击牌 | 返回手牌带来的每回合重复收益 | 中 | 0费攻击的重复兑现 |
| HAILSTORM | OrbEngine+DamageEngine | Strong | 有冰霜充能球 | 每回合群伤×剩余回合 | 高 | 冰霜球维持 |
| ITERATION | HandEngine | Normal | 手牌或牌区有状态牌 | 状态牌抽牌次数×抽牌价值 | 中 | 状态牌来源与每回合一次限制 |
| LOOP | OrbEngine | Core | 有充能球 | 每回合触发最右球被动的保守价值 | 中 | 被动触发与球序 |
| MACHINE_LEARNING | HandEngine | Core | 剩余回合>1 | 每回合额外抽牌价值 | 高 | 手牌上限 |
| SMOKESTACK | CardGenerationEngine+DamageEngine | Normal | 有生成状态牌的来源 | 状态牌生成次数×群伤 | 中 | 状态牌生成的触发时点 |
| SPINNER | OrbEngine | Strong | 剩余回合>1 | 每回合生成玻璃球的保守价值 | 低 | 玻璃球的破碎与重放 |
| STORM | OrbEngine | Core | 牌区有能力牌可作为来源 | 能力牌打出次数×闪电充能价值 | 中 | 与能力牌引擎的组合 |
| SUBROUTINE | EnergyEngine | Core | 牌区有能力牌可作为来源 | 能力牌打出次数×能量价值 | 中 | 能力牌密度 |
| THUNDER | OrbEngine+DamageEngine | Strong | 有闪电充能球 | 闪电激发次数×伤害 | 高 | 激发频率 |
| TRASH_TO_TREASURE | OrbEngine+CardGenerationEngine | Normal | 有生成状态牌的来源 | 状态牌生成次数×随机球价值 | 中 | 随机球类型 |

### 隐藏状态与时序（Draft 记录）

- `BIASED_COGNITION` 的掉集中计数不递减，每个本方回合开始触发；`FocusPower` 允许负值。
- `CAPACITOR` 没有对应 Power，球位是永久且不可撤销的隐藏状态。
- `COOLANT` 统计的是不同球种数量，不是球的数量。
- `CONSUMING_SHADOW` 在回合末激发最左侧（队列最前）的球；暗球的价值随集中变化。
- `LOOP` 触发最右侧球的被动，不是激发。
- `SPINNER` 在能量重置（回合开始）时生成球；升级后打出时额外立即生成1个。
- `STORM`/`SUBROUTINE` 根据每张能力牌记录内部数量并在结算后触发。
- `THUNDER` 只在闪电球被激发（非被动/充能）时触发。
- `ONE_FOR_ALL` 为 MultiplayerOnly。

### 不确定项

- 球被动/激发、集中与玻璃球的精确收益依赖运行时球状态，投影使用保守代理，需玩家复核。
- 兑现证据暂统一走通用状态改善回退。

## 玩家联合评审采纳（2026-09-17）

| 卡牌 | 变化 | 依据 |
|---|---|---|
| BIASED_COGNITION | Core→Normal，加入专搜 | 适合短线斩杀，长线在后面几回合开；通常不抓 |
| CAPACITOR | 加入专搜 | 求解器不区分被动流与激发流的球位时序 |
| CONSUMING_SHADOW | Strong→Low，仅在免费或有余费时开 | 玩家判定为弱牌，不专搜 |
| COOLANT | Strong→Low，不卖血开 | 牌效比低，通常不抓 |
| CREATIVE_AI | Core→Normal，加入专搜 | 2-3费当回合零收益 |
| ITERATION | Normal→Strong | 状态机体系很强 |
| MACHINE_LEARNING | 加入专搜 | 多抽1很强，当回合纯亏费易被剪 |
| SPINNER | Strong→Normal | 与关键球抢占球位的风险 |
| STORM | 加入专搜 | 必须先于其他能力打出 |
| SUBROUTINE | 加入专搜 | 作为能力链起手 |

- LOOP 按玩家意见不专搜。
- 数值：ITERATION 按能力牌实际抽牌量计，不再用每回合固定值。
