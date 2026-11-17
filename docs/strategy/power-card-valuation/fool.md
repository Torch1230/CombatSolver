# 愚者能力牌

本表列出游戏版本 0.113.1 的 FoolCardPool 中全部 20 张能力牌：18 张属于 CombatSolver 单人建模范围，`COMPOST`、`COVERING_FIRE` 由游戏标记为 MultiplayerOnly。费用和数值核对自同版本反编译卡牌类；中文名来自玩家逐卡说明（2026-11-17），英文名暂按实现类型拆写，尚未与 PCK `localization/eng/cards.json` 逐条核对。

| ID / 实现类型 | 名称（中 / 英） | 费用（普通→升级） | 效果（普通→升级） | 建模状态 |
|---|---|---|---|---|
| <code>ALL_IS_WELL</code><br><code>AllIsWell</code> | 万事无忧<br>All Is Well | 1 | 每当你生成一张悲伤，获得 4→5 点格挡 | <code>Specified</code> |
| <code>ARCHERY</code><br><code>Archery</code> | 箭术<br>Archery | 1 | 回合开始时获得 4→5 点瞄准（下一次射箭攻击额外伤害，用后清空） | <code>Specified</code> |
| <code>COMPOST</code><br><code>Compost</code> | —<br>Compost | 0 | 敌人死亡时全体玩家获得能量 | <code>OutOfScopeMultiplayer</code> |
| <code>COVERING_FIRE</code><br><code>CoveringFire</code> | —<br>Covering Fire | 1 | 射箭后队友获得格挡 | <code>OutOfScopeMultiplayer</code> |
| <code>ECSTASY</code><br><code>Ecstasy</code> | 狂喜<br>Ecstasy | 2→1 | 回合开始时把手牌中随机 1 张悲伤变为喜悦 | <code>Specified</code> |
| <code>ENNUI</code><br><code>Ennui</code> | 倦怠<br>Ennui | 1 | 回合开始时加入 1 张悲伤并额外抽 2→3 张牌 | <code>Specified</code> |
| <code>EXPLOSIVE_ARROWS</code><br><code>ExplosiveArrows</code> | 爆炸箭矢<br>Explosive Arrows | 1 | 扎穿敌人时对所有敌人造成 6→8 点伤害 | <code>Specified</code> |
| <code>HIDDEN_ROOTS</code><br><code>HiddenRoots</code> | 潜伏根系<br>Hidden Roots | 1 | 回合开始时获得 3→4 点拉伸（下一张格挡牌额外格挡，用后清空） | <code>Specified</code> |
| <code>HONING</code><br><code>Honing</code> | 磨砺<br>Honing | 3→2 | 箭矢攻击额外造成 50% 伤害 | <code>Specified</code> |
| <code>LOST_LUTE</code><br><code>LostLute</code> | 失落鲁特琴<br>Lost Lute | 3→2 | 每当你获得旋律，打出抽牌堆顶的牌 | <code>Specified</code> |
| <code>LULLABY</code><br><code>Lullaby</code> | 摇篮曲<br>Lullaby | 0（升级获得固有） | 获得全部三种旋律时获得 20 点格挡 | <code>Specified</code> |
| <code>MOTIF</code><br><code>Motif</code> | 曲中意<br>Motif | 2→1 | 每当你获得旋律，抽 1 张牌 | <code>Specified</code> |
| <code>MYTHIC_FORM</code><br><code>MythicForm</code> | 神话形态<br>Mythic Form | 3 | 回合开始时把 1 张随机塔罗牌加入手牌（升级版塔罗牌已升级） | <code>Specified</code> |
| <code>QUIVER</code><br><code>Quiver</code> | 箭袋<br>Quiver | 1 | 额外获得 1→2 个箭矢槽 | <code>Specified</code> |
| <code>SERRATED_ARROWS</code><br><code>SerratedArrows</code> | 锯齿箭刃<br>Serrated Arrows | 1 | 被扎穿的敌人受到你的攻击伤害增加 25%→50% | <code>Specified</code> |
| <code>SIMPLETON</code><br><code>Simpleton</code> | 傻瓜<br>Simpleton | 2→1 | 回合结束时打出手牌中耗能最低的牌 | <code>Specified</code> |
| <code>SONG_PATH</code><br><code>SongPath</code> | 歌之道<br>Song Path | 1 | 每当你获得啾旋律，获得 5→7 点格挡 | <code>Specified</code> |
| <code>TUNE</code><br><code>Tune</code> | 调音<br>Tune | 2→1 | 回合结束时若拥有叽旋律，下一张打出的牌耗能为 0 | <code>Specified</code> |
| <code>VIOLENT_CHORD</code><br><code>ViolentChord</code> | 暴力和弦<br>Violent Chord | 1 | 每当你获得哇旋律，对所有敌人造成 9→12 点伤害 | <code>Specified</code> |
| <code>YAH</code><br><code>Yah</code> | 呀！<br>Yah! | 1 | 每回合第一次让箭矢松脱时获得 1→2 点能量 | <code>Specified</code> |

`Specified` 表示玩家理解已记录并接入路线保护，整场验证尚在进行；验证通过后改为 `Modeled`。

## 玩家逐卡说明（2026-11-17）

以下按玩家原意整理，未改动判断。

| 卡牌 | 玩家判断 |
|---|---|
| 暴力和弦 | 1 费容易开；牌堆有多张哇旋律牌或本场有多个敌人时才有价值；打出哇旋律能造成显著伤害时优先打出 |
| 歌之道 | 1 费容易开；牌堆有多张啾旋律牌才有价值；额外格挡能有效降低战损，多数情况优先打出；能力药水中优先选取并推算战损 |
| 箭术 | 1 费容易开；牌堆有利用瞄准效率高的牌（如迅捷射击）时增值；长线战斗尤其 Boss 战增值，回合数少的小怪战斗降值 |
| 锯齿箭刃 | 1 费容易开；非射箭攻击牌多时优先打出，价值约等于易伤 |
| 潜伏根系 | 1 费容易开；牌堆防御牌少时优先打出；长线战斗如 Boss 战增值；面对瀑布巨兽等爆发型敌人优先打出；长线战斗中能力药水优先选取 |
| 万事无忧 | 1 费容易开；牌堆有生成悲伤的牌时优先打出；整体表现不佳，多数战斗不推荐优先打出；能力药水中不建议选取；可在使用幸福结晶前打出 |
| 调音 | 2 费偏贵；牌堆有叽旋律牌、有其他高费牌或有保留时优先打出；长线战斗增值 |
| 曲中意 | 2 费偏贵；牌堆加费牌较多时优先打出；多张旋律牌时增值；评分可参考亡灵契约师的书页风暴 |
| 摇篮曲 | 0 费很容易开；任何时候都优先打出（除非用于触发遗物永冻冰晶）；回合内应先于手牌中的旋律牌打出 |
| 爆炸箭矢 | 1 费容易开；面对多个怪物时优先打出；多怪战斗中能力药水优先选取，或用复制药水复制 |
| 箭袋 | 1 费容易开；牌堆有多张射箭牌时优先打出；长线战斗优先打出 |
| 倦怠 | 1 费容易开；短线战斗优先打出，打出后五回合内能结束战斗时优先；牌堆有生长时应极大提高优先级；打出后需要六回合及以上结束战斗时的处理，原始说明在“应优先计”处中断，待补 |
| 呀！ | 1 费容易开；牌堆有拧下、或后续能解除敌人扎穿时优先打出；缺少射箭牌时应计算不打出的战损；默认倾向不打出；短线战斗中能力药水优先选取 |
| 狂喜 | 2 费偏贵；手牌有悲伤时优先打出，没有悲伤时不打出 |
| 傻瓜 | 2 费偏贵；手牌不多于 3 张时优先打出，其余情况不打出 |
| 磨砺 | 3 费偏贵；牌堆有大额箭类攻击（如重复射击、神力射击）时才值得开；费用充足时优先打出；能力药水中优先选取 |
| 神话形态 | 3 费偏贵；长线战斗才值得开，费用充足时优先打出；能力药水中优先选取；优先用复制药水使效果翻倍 |
| 失落鲁特琴（升级） | 2 费偏贵；牌堆有多张旋律牌时优先打出；手牌有旋律牌时应先于旋律牌打出；优先用复制药水使效果翻倍 |

## 实现映射

实现位置：`src/Search/PowerCardValuation/Cards/Fool/`。路线政策在 `FoolPowerRoutePolicy`，触发证据在 `FoolPowerTriggerEvidence`，开局投影与共用事实在 `FoolPowerOpeningProjection`。射箭牌按原版 `CardTag.Shoot`，旋律牌按 `CardTag.Wah` / `Cho` / `Ki`，悲伤按卡牌类型 `Sadness` 识别，均读取打出能力后的分支牌区（手牌、抽牌堆、弃牌堆）。

| ID | 机制族 | 优先级 | 触发证据（打出后） | 开局投影 |
|---|---|---|---|---|
| ALL_IS_WELL | BlockTriggerEngine | Low | 牌区有悲伤或倦怠已生效 | 格挡 × 悲伤来源次数，按来伤有界 |
| ARCHERY | DamageEngine | Normal | 有射箭牌且剩余回合 > 2 | 瞄准 × 后续回合数，上限敌方剩余生命 |
| ECSTASY | CardGenerationEngine | Normal | 手牌有悲伤或倦怠已生效 | 每次转化按一张牌价值，次数不超过悲伤来源 |
| ENNUI | HandEngine | Normal | 预计 2–5 回合内结束，或狂喜已生效 | 逐个后续回合比较有无倦怠的实际抽牌数：悲伤不能打出且保留，按回合累积占用手牌上限；累计多抽减去被挤掉的正常抽牌，净值不为正时不保护；不给触发保底 |
| EXPLOSIVE_ARROWS | DamageEngine | Strong | 有射箭牌且存活敌人 ≥ 2 | 群伤 × 敌人数 × 射箭牌数，上限敌方剩余生命 |
| HIDDEN_ROOTS | DefenseEfficiency | Strong | 剩余回合 > 1 | 拉伸 × 后续回合数，按来伤有界 |
| HONING | DamageEngine | Core | 有射箭牌 | 射箭牌总伤害 × 增幅百分比，上限敌方剩余生命；复制药水使增幅翻倍时投影随之翻倍 |
| LOST_LUTE | AutoPlayEngine | Strong | 旋律牌 ≥ 2 | 每张旋律牌按一张牌价值 |
| LULLABY | DefenseEfficiency | Core | 牌区三种旋律齐全 | 20 格挡 × 可凑齐次数，按来伤有界；允许触发保底 |
| MOTIF | HandEngine | Strong | 旋律牌 ≥ 2 | 每张旋律牌抽 1 张，按牌价值 |
| MYTHIC_FORM | CardGenerationEngine、RandomGeneration | Strong | 剩余回合 > 2 | 每回合一张牌价值 × 后续回合数 |
| QUIVER | DamageEngine | Strong | 射箭牌 ≥ 2 且剩余回合 > 1 | 最大射箭伤害 × min(后续回合, 射箭牌数 − 1) |
| SERRATED_ARROWS | StatusAmplifier | Strong | 有射箭牌且非射箭攻击 ≥ 2 | 非射箭攻击总伤害 × 增幅百分比 |
| SIMPLETON | AutoPlayEngine | Low | 剩余回合 > 1 且手牌 ≤ 3 | 每回合一张牌价值 × 后续回合数 |
| SONG_PATH | DefenseEfficiency、BlockTriggerEngine | Core | 啾旋律牌 ≥ 2 | 格挡 × 啾旋律牌数，按来伤有界 |
| TUNE | CostReductionEngine | Normal | 有叽旋律牌且剩余回合 > 1 | 一张牌价值 × min(后续回合, 叽旋律牌数) |
| VIOLENT_CHORD | DamageEngine | Strong | 哇旋律牌 ≥ 2，或 ≥ 1 且存活敌人 ≥ 2 | 群伤 × 敌人数 × 哇旋律牌数 |
| YAH | EnergyEngine | Low | 有射箭牌且剩余回合 > 1 | 每回合一张牌价值 × min(后续回合, 射箭牌数) |

估值只决定中途是否为能力路线保留席位；开局能力专搜对每张手中已登记能力牌跑固定前缀搜索到战斗结束，终局仍只按真实战损和用药比较。

## 待补与边界

- 倦怠：玩家补充（2026-11-17）：实战中求解器有倦怠就打，长线战斗把手牌塞死。悲伤为“不能打出 + 保留”，每回合一张、逐回合累积；基础抽 5、多抽 2、上限 10 时，第 4 回合起多抽被挤掉，第 6 回合起开始挤掉正常抽牌。现按短线才保护、净抽牌为负不保护处理。“生长”联动尚未识别。
- 能力药水、复制药水：玩家对多张牌给出了药水选取与复制建议。当前开局专搜前缀只覆盖“能力牌”“取牌后能力牌”和“能力牌接能力牌”，不含“复制药水 → 能力牌”，该路线依赖普通搜索发现。
- 箭术的“利用瞄准效率高的牌”、潜伏根系的“爆发型敌人”、曲中意的“加费牌”、倦怠的“生长”、呀！的“拧下”尚未单独识别，由通用回合数、射箭牌数与来伤代理。
- 英文名尚未与 PCK 本地化逐条核对。
