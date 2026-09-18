# 储君能力牌

本表列出游戏版本 0.111.0 的 RegentCardPool 中全部 19 张能力牌：18 张属于 CombatSolver 单人建模范围，1 张由游戏标记为 MultiplayerOnly。中英文名称与效果原文来自同版本 PCK 的 localization/zhs/cards.json 和 localization/eng/cards.json；数值由同版本原版卡牌实例按普通/升级状态格式化。表中只把能量与星能图片图标写成文字，其余效果语义不改写。

LegacyFallback 表示尚未建模，必须等用户逐卡给出理解；OutOfScopeMultiplayer 只保留完整卡池资料，不进入单人求解器建模。

| ID / 实现类型 | 官方名称（中 / 英） | 费用（普通→升级） | 游戏效果 | 建模状态 |
|---|---|---|---|---|
| <code>ARSENAL</code><br><code>Arsenal</code> | 武器库<br>Arsenal | 1 能量 | 每当你生成一张牌，就获得1点力量。 | <code>QuantifiedDraft</code> |
| <code>BLACK_HOLE</code><br><code>BlackHole</code> | 黑洞<br>Black Hole | 1 能量 | 普通：每当你花费或获得星能时，对所有敌人造成3点伤害。<br>升级：每当你花费或获得星能时，对所有敌人造成4点伤害。 | <code>QuantifiedDraft</code> |
| <code>CHILD_OF_THE_STARS</code><br><code>ChildOfTheStars</code> | 群星之子<br>Child of the Stars | 1 能量 | 普通：每当你花费星能时，每花费一点星能，获得2点格挡。<br>升级：每当你花费星能时，每花费一点星能，获得3点格挡。 | <code>QuantifiedDraft</code> |
| <code>FURNACE</code><br><code>Furnace</code> | 熔炉<br>Furnace | 1 能量 | 普通：在你的回合开始时，铸造5。<br>升级：在你的回合开始时，铸造7。 | <code>QuantifiedDraft</code> |
| <code>GENESIS</code><br><code>Genesis</code> | 创世纪<br>Genesis | 2 能量 | 普通：在你的回合开始时，获得2点星能。<br>升级：在你的回合开始时，获得3点星能。 | <code>QuantifiedDraft</code> |
| <code>HAMMER_TIME</code><br><code>HammerTime</code> | 锤子时间<br>Hammer Time | 2→1 能量 | 每当你铸造时，所有盟友也都铸造相同的数值。 | <code>OutOfScopeMultiplayer</code> |
| <code>MONARCHS_GAZE</code><br><code>MonarchsGaze</code> | 王之凝视<br>Monarch's Gaze | 2→1 能量 | 每当你攻击敌人的时候，这名敌人在本回合失去1点力量。 | <code>QuantifiedDraft</code> |
| <code>NEUTRON_AEGIS</code><br><code>NeutronAegis</code> | 中子护盾<br>Neutron Aegis | 1 能量；5 星能 | 普通：获得8层覆甲。<br>升级：获得11层覆甲。 | <code>QuantifiedDraft</code> |
| <code>ORBIT</code><br><code>Orbit</code> | 环绕轨道<br>Orbit | 2→1 能量 | 你每花费4点能量，<br>就获得1点能量。 | <code>QuantifiedDraft</code> |
| <code>PALE_BLUE_DOT</code><br><code>PaleBlueDot</code> | 暗淡蓝点<br>Pale Blue Dot | 1 能量 | 普通：如果你在一回合内打出了大于等于5张牌，在下个回合开始时抽1张牌。<br>升级：如果你在一回合内打出了大于等于5张牌，在下个回合开始时抽2张牌。 | <code>QuantifiedDraft</code> |
| <code>PARRY</code><br><code>Parry</code> | 招架<br>Parry | 1 能量 | 普通：君王之剑现在能让你获得10点格挡。<br>升级：君王之剑现在能让你获得14点格挡。 | <code>QuantifiedDraft</code> |
| <code>PILLAR_OF_CREATION</code><br><code>PillarOfCreation</code> | 创世之柱<br>Pillar of Creation | 1 能量 | 普通：你每次生成卡牌时，获得2点格挡。<br>升级：你每次生成卡牌时，获得3点格挡。 | <code>QuantifiedDraft</code> |
| <code>ROYALTIES</code><br><code>Royalties</code> | 王国资产<br>Royalties | 1 能量 | 普通：在战斗结束时，获得30金币。<br>升级：在战斗结束时，获得40金币。 | <code>QuantifiedDraft</code> |
| <code>SEEKING_EDGE</code><br><code>SeekingEdge</code> | 追踪之刃<br>Seeking Edge | 1 能量 | 普通：铸造7。<br>君王之剑现在会对所有敌人造成伤害。<br>升级：铸造11。<br>君王之剑现在会对所有敌人造成伤害。 | <code>QuantifiedDraft</code> |
| <code>SPECTRUM_SHIFT</code><br><code>SpectrumShift</code> | 光谱偏移<br>Spectrum Shift | 2→1 能量 | 在你的回合开始时，将1张随机无色牌添加到你的手牌中。 | <code>QuantifiedDraft</code> |
| <code>SWORD_SAGE</code><br><code>SwordSage</code> | 剑圣<br>Sword Sage | 2→1 能量 | 君王之剑获得重放1。 | <code>QuantifiedDraft</code> |
| <code>THE_SEALED_THRONE</code><br><code>TheSealedThrone</code> | 封印王座<br>The Sealed Throne | 1→0 能量；3 星能 | 你每打出一张牌，获得星能。 | <code>QuantifiedDraft</code> |
| <code>TYRANNY</code><br><code>Tyranny</code> | 暴政<br>Tyranny | 1 能量 | 在你的回合开始时，抽一张牌，并从你的手牌中消耗1张牌。 | <code>QuantifiedDraft</code> |
| <code>VOID_FORM</code><br><code>VoidForm</code> | 虚空形态<br>Void Form | 3 能量 | 结束你的回合。<br>你可以免费打出每回合的前2张牌。 | <code>QuantifiedDraft</code> |

## 逐卡建模（Draft）

实现位置：`src/Search/PowerCardValuation/Cards/Regent/`。18 张单人 `CardType.Power` 已登记；`HAMMER_TIME` 为 MultiplayerOnly 排除。`ROYALTIES` 为战后金币收益，登记资料与估值但不创建战斗内承诺。

| ID | 机制族 | 优先级 | 触发证据 | 开局投影口径 | 置信度 | 待玩家复核 |
|---|---|---|---|---|---|---|
| ARSENAL | StrengthGrowth+CardGenerationEngine | Strong | 有生成牌的来源且剩余回合>1 | 力量×攻击前沿，按生成次数放大 | 中 | 生成牌密度 |
| BLACK_HOLE | StarEngine+DamageEngine | Strong | 有花费/获得星能来源且仍有敌人 | 星能事件次数×群伤 | 中 | 星能循环频率 |
| CHILD_OF_THE_STARS | StarEngine+DefenseEfficiency | Strong | 有星能花费来源 | 花费星能点数×格挡 | 中 | 星能消耗量 |
| FURNACE | StarEngine | Core | 有铸造来源或剩余回合>1 | 每回合铸造量×剩余回合（铸造资源代理） | 中 | 铸造/君王之剑联动 |
| GENESIS | StarEngine+EnergyEngine | Core | 剩余回合>1 | 每回合星能量×剩余回合 | 高 | 星能与花费窗口 |
| MONARCHS_GAZE | StatusAmplifier | Strong | 有攻击且仍有敌人 | 每次攻击降低目标1力量 | 中 | 减力量的防伤折算 |
| NEUTRON_AEGIS | DefenseEfficiency+StarEngine | Strong | 剩余回合>1 | 覆甲×逐回合格挡 | 高 | 覆甲衰减 |
| ORBIT | EnergyEngine | Normal | 剩余回合>1 | 每累计4点能量花费返还1能量 | 中 | 能量花费节奏 |
| PALE_BLUE_DOT | HandEngine | Normal | 剩余回合>1且有可打出手牌 | 每回合达标后下回合抽牌 | 中 | 5张/回合阈值达成率 |
| PARRY | DefenseEfficiency | Strong | 有君王之剑来源 | 君王之剑命中时获得格挡 | 中 | 君王之剑生成频率 |
| PILLAR_OF_CREATION | BlockTriggerEngine+CardGenerationEngine | Strong | 有生成牌的来源 | 生成牌次数×格挡 | 中 | 生成牌密度 |
| ROYALTIES | CrossCombatGrowth | Low（不承诺） | —（纯战后收益） | 战后金币（30/40） | 高 | 是否值得为战后收益牺牲战斗节奏 |
| SEEKING_EDGE | DamageEngine+StarEngine | Strong | 有铸造来源或剩余回合>1 | 铸造资源与君王之剑群攻 | 中 | 群攻与重放 |
| SPECTRUM_SHIFT | CardGenerationEngine | Core | 剩余回合>1 | 每回合随机无色牌价值 | 中 | 随机无色牌质量 |
| SWORD_SAGE | DamageEngine | Strong | 有君王之剑来源 | 君王之剑重放的每回合伤害 | 中 | 重放与铸造 |
| THE_SEALED_THRONE | StarEngine | Core | 剩余回合>1且牌区有牌 | 每张牌获得星能 | 中 | 星能溢出与花费 |
| TYRANNY | HandEngine | Strong | 剩余回合>1且手牌非空 | 每回合抽1并强制消耗1 | 中 | 强制消耗选牌的损失 |
| VOID_FORM | CostReductionEngine+AutoPlayEngine | Core（专搜） | 剩余回合>1 | 每回合前2张免费 | 中 | 结束回合的时机代价 |

### 隐藏状态与时序（Draft 记录）

- `BLACK_HOLE` 只在“牌序列最后一张且该牌花费星能”以及“获得星能”时触发，避免重放重复结算。
- `CHILD_OF_THE_STARS` 按单次花费事件中的星能点数结算。
- `ORBIT` 的花费进度跨整个战斗累计，不在每回合重置。
- `PALE_BLUE_DOT` 每回合只触发一次，抽牌是临时 Power，下个回合开始移除。
- `PARRY`/`SEEKING_EDGE`/`SWORD_SAGE` 的效果实际落在君王之剑这张卡上，Power 本身多为标记。
- `THE_SEALED_THRONE` 在每张牌结算前获得星能，可支撑同一张牌的星能支付。
- `TYRANNY` 的消耗需要玩家从手牌选择。
- `VOID_FORM` 打出时结束当前回合。
- `HAMMER_TIME` 为 MultiplayerOnly。

### 不确定项

- 星星/铸造/君王之剑的远期收益依赖运行时资源，投影使用保守代理。
- 兑现证据暂统一走通用状态改善回退。

## 玩家联合评审采纳（2026-09-17）

| 卡牌 | 变化 | 依据 |
|---|---|---|
| ARSENAL | 加入专搜 | 必须先开才能吃满后续生成牌力量 |
| CHILD_OF_THE_STARS | 加入专搜 | 先开后出耗星牌填补防守 |
| GENESIS | 加入专搜 | 次回合才给星能 |
| ORBIT | Normal→Core，加入专搜 | 回费价值被低估，升级后好开 |
| PALE_BLUE_DOT | 加入专搜 | 跨回合过牌易被低估 |
| PILLAR_OF_CREATION | Strong→Core | 玩家要求优先级调高 |
| SPECTRUM_SHIFT | 加入专搜 | 下回合才给牌且随机 |
| SWORD_SAGE | 加入专搜 | 收益延后到出剑瞬间 |
| THE_SEALED_THRONE | 加入专搜 | 3星能启动门槛，避免因省星能错过永动 |
| TYRANNY | 加入专搜 | 精简牌库价值在后续洗牌才爆发 |

- FURNACE 按玩家意见不专搜。
- VOID_FORM 维持专搜；打出即结束回合，常规评估必被剪。
- ROYALTIES：按玩家明确指令，完全不改动其局外成长兑现机制，不创建战斗内承诺，也不得为贪金币承担战损。
