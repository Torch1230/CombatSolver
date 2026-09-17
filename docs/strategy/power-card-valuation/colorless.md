# 无色能力牌

本表列出游戏版本 0.111.0 的 ColorlessCardPool 中全部 13 张能力牌：12 张属于 CombatSolver 单人建模范围，1 张由游戏标记为 MultiplayerOnly。中英文名称与效果原文来自同版本 PCK 的 localization/zhs/cards.json 和 localization/eng/cards.json；数值由同版本原版卡牌实例按普通/升级状态格式化。表中只把能量与星能图片图标写成文字，其余效果语义不改写。

`QuantifiedDraft` 表示已按实现者理解完成逐卡公式、登记和纯合同，尚待玩家复核。`OutOfScopeMultiplayer` 只保留完整卡池资料，不进入单人求解器建模。

| ID / 实现类型 | 官方名称（中 / 英） | 费用（普通→升级） | 游戏效果 | 建模状态 |
|---|---|---|---|---|
| <code>AUTOMATION</code><br><code>Automation</code> | 自动化<br>Automation | 1→0 能量 | 你每抽10张牌，获得1点能量。 | <code>QuantifiedDraft</code> |
| <code>BEACON_OF_HOPE</code><br><code>BeaconOfHope</code> | 希望灯塔<br>Beacon of Hope | 2 能量 | 每当你在你的回合获得格挡时，其他玩家获得相应一半的格挡。 | <code>OutOfScopeMultiplayer</code> |
| <code>CALAMITY</code><br><code>Calamity</code> | 劫难<br>Calamity | 3→2 能量 | 每当你打出一张攻击牌时，将一张随机攻击牌添加到你的手牌。 | <code>QuantifiedDraft</code> |
| <code>ENTROPY</code><br><code>Entropy</code> | 熵<br>Entropy | 1 能量 | 在你的回合开始时，变化你手牌中的1张牌。 | <code>QuantifiedDraft</code> |
| <code>ETERNAL_ARMOR</code><br><code>EternalArmor</code> | 永恒铠甲<br>Eternal Armor | 3 能量 | 普通：获得9层覆甲。<br>升级：获得12层覆甲。 | <code>QuantifiedDraft</code> |
| <code>FASTEN</code><br><code>Fasten</code> | 勒紧<br>Fasten | 1 能量 | 普通：从“防御”牌中额外获得4点格挡。<br>升级：从“防御”牌中额外获得6点格挡。 | <code>QuantifiedDraft</code> |
| <code>MAYHEM</code><br><code>Mayhem</code> | 乱战<br>Mayhem | 2→1 能量 | 在你的回合开始时，打出你抽牌堆顶部的牌。 | <code>QuantifiedDraft</code> |
| <code>NOSTALGIA</code><br><code>Nostalgia</code> | 怀旧<br>Nostalgia | 1→0 能量 | 每回合首次打出攻击或技能牌时，将其置于你的抽牌堆顶端。 | <code>QuantifiedDraft</code> |
| <code>PANACHE</code><br><code>Panache</code> | 神气制胜<br>Panache | 0 能量 | 普通：每当你在一回合内打出五张牌时，对所有敌人造成10点伤害。<br>升级：每当你在一回合内打出五张牌时，对所有敌人造成14点伤害。 | <code>QuantifiedDraft</code> |
| <code>PREP_TIME</code><br><code>PrepTime</code> | 准备时间<br>Prep Time | 1 能量 | 普通：在你的回合开始时，获得4点活力。<br>升级：在你的回合开始时，获得6点活力。 | <code>QuantifiedDraft</code> |
| <code>PROWESS</code><br><code>Prowess</code> | 非凡技艺<br>Prowess | 1 能量 | 普通：获得1点力量。<br>获得1点敏捷。<br>升级：获得2点力量。<br>获得2点敏捷。 | <code>QuantifiedDraft</code> |
| <code>ROLLING_BOULDER</code><br><code>RollingBoulder</code> | 滚石<br>Rolling Boulder | 3 能量 | 普通：在你的回合开始时，对所有敌人造成5点伤害，然后将该伤害增加5点。<br>升级：在你的回合开始时，对所有敌人造成10点伤害，然后将该伤害增加5点。 | <code>QuantifiedDraft</code> |
| <code>STRATAGEM</code><br><code>Stratagem</code> | 计策<br>Stratagem | 1→0 能量 | 每当你的抽牌堆打乱洗牌时，选择一张牌放入你的手牌。 | <code>QuantifiedDraft</code> |

## 逐卡建模（Draft）

实现位置：`src/Search/PowerCardValuation/Cards/Colorless/`。12 张单人 `CardType.Power` 已登记；`BEACON_OF_HOPE` 为 MultiplayerOnly 排除。无色能力按实际 CardId 识别，可在任意角色持有；跨色获得的原版能力按卡牌身份建模。

| ID | 机制族 | 优先级 | 触发证据 | 开局投影口径 | 置信度 | 待玩家复核 |
|---|---|---|---|---|---|---|
| AUTOMATION | EnergyEngine | Normal | 剩余回合>1 | 每 10 次抽牌获得能量的保守折算 | 中 | 抽牌速度 |
| CALAMITY | CardGenerationEngine | Strong | 有攻击 | 攻击打出次数×随机攻击牌价值 | 中 | 随机攻击牌质量 |
| ENTROPY | CardGenerationEngine | Normal | 剩余回合>1且手牌非空 | 每回合变化一张牌的期望价值 | 低 | 变化牌的随机性 |
| ETERNAL_ARMOR | DefenseEfficiency | Strong | 剩余回合>1 | 覆甲×逐回合格挡 | 高 | 覆甲衰减 |
| FASTEN | DexterityGrowth+DefenseEfficiency | Strong | 有“防御”标签牌 | “防御”牌格挡增量进入行动前沿 | 高 | 防御牌密度 |
| MAYHEM | AutoPlayEngine | Core | 剩余回合>1且抽牌堆非空 | 每回合自动打出牌顶的价值 | 中 | 牌顶随机性与不可打出牌 |
| NOSTALGIA | RetainEngine+CardGenerationEngine | Normal | 有攻击或技能 | 每回合首张攻击/技能回抽牌堆顶 | 中 | 回抽的重复利用价值 |
| PANACHE | DamageEngine | Strong（专搜） | 手牌非空 | 每 5 张牌触发一次群伤 | 中 | 5张/回合阈值达成率 |
| PREP_TIME | StrengthGrowth+DexterityGrowth | Normal | 有攻击 | 每回合活力进入下一次攻击 | 中 | 活力消耗时点 |
| PROWESS | StrengthGrowth+DexterityGrowth | Core | 有攻击或格挡技能 | 力量/敏捷进入行动前沿 | 高 | 先力量还是先敏捷 |
| ROLLING_BOULDER | DamageEngine | Core | 仍有敌人且剩余回合>1 | 每回合递增的群伤×剩余回合 | 中 | 递增伤害的累计兑现 |
| STRATAGEM | HandEngine | Normal | 剩余回合>1 | 洗牌时选牌的价值 | 中 | 洗牌频率 |

### 隐藏状态与时序（Draft 记录）

- `AUTOMATION` 的内部计数每 10 次抽牌独立循环，与层数无关。
- `ETERNAL_ARMOR` 使用与岩石铠甲相同的覆甲语义，逐回合递减。
- `FASTEN` 只加成“防御”标签来源的格挡，且允许无卡牌来源。
- `MAYHEM` 在自动前置阶段打出牌堆顶，随机性强。
- `NOSTALGIA` 只把每回合前若干张攻击/技能放回抽牌堆顶。
- `PANACHE` 在打出第 5 张牌后触发并每回合重置计数。
- `PREP_TIME` 的活力由下一次攻击消耗。
- `PROWESS` 固定先力量后敏捷。
- `STRATAGEM` 在洗牌时触发选牌。
- `BEACON_OF_HOPE` 为 MultiplayerOnly。

### 不确定项

- 随机生成（灾难、熵）、洗牌与自动出牌的收益依赖运行时牌序，投影为保守代理。
- 兑现证据暂统一走通用状态改善回退。

## 玩家联合评审采纳（2026-09-17）

| 卡牌 | 变化 | 依据 |
|---|---|---|
| AUTOMATION | Normal→Core，加入专搜，按实际抽牌折算每10抽返能 | 玩家判定为超强、优先级极高 |
| CALAMITY | Strong→Normal，加入专搜 | 3费且随机攻击牌，当回合易亏费卡手 |
| ENTROPY | Normal→Strong | 玩家判定很强、提供多变数 |
| FASTEN | Strong→Core | 很强，优先开 |
| MAYHEM | 加入专搜 | 回合开始免费打牌且控顶 |
| NOSTALGIA | Normal→Strong，加入专搜 | 定点回抽核心牌，跨回合收益高 |
| PANACHE | 移除专搜 | 0费即时反馈，普通搜索可识别 |
| ROLLING_BOULDER | Core→Normal，不专搜 | 玩家判定一般没人抓 |
| STRATAGEM | Normal→Strong，加入专搜 | 洗牌时点在未来，涉及分支选牌 |
