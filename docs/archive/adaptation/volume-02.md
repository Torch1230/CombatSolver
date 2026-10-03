# CombatSolver 适配验证入口历史卷 02

## 已完成实机闭环（899 项）

### `CARD-ON-PLAY-BATCH-059`（2 项）

闭环：最终 `0.7.3` Release DLL 在真实可见游戏中建立静默猎手场景，分别注入中和与带 `1` 层墨影附魔的打击。每条路径由生产预测模拟和游戏原生出牌动作各执行一次，比较生命、格挡、能量、双方 Power、四牌堆、卡牌状态和相关 RNG，并显式断言目标的虚弱层数。

结果：runId `dbc03a2a66bf4640942f55a98de06139` 为 `Passed`、`mainThread=true`，两条 `BigDummy:NOTHING` 差分均完成；中和与墨影各自都使目标获得 `1` 层虚弱，预测与实机完整状态一致。首次 runId `73a5572486884d10bcc77527983a13b2` 的中和差分已经通过，第二条因夹具把两条隔离差分误写成累计 `2` 层而失败；纠正期望为独立场景的 `1` 层后复跑通过。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Neutralize.OnPlay(...)` | 中和 | 造成牌面伤害，并给目标施加 `1` 层虚弱 | 原生出牌与预测完整状态一致 |
| `Inky.OnPlay(...)` | 墨影 | 附魔牌打出时给其目标施加 `1` 层虚弱 | 带墨影的打击原生/预测均得到 `WEAK_POWER:1` |

### `RELIC-POWER-BATCH-058`（4 项）

闭环：最终 Release DLL 在同一个真实可见游戏 PID 中连续执行两份夹具。第一场依次打出两张燃烧和两张上勾拳，比较损毁头盔首次力量翻倍、不安油灯同一张牌的多种减益翻倍，以及两件遗物的一场一次私有状态；第二场重新建立不安油灯并连续打出两张穿透哀嚎，专测临时 Power 的内部力量不会重复翻倍。生产模拟与原生动作队列比较完整状态，第二场结束后才退出进程。

结果：runId `8762193507b84e7384ce5911ea706607`、`db72367cc141437f94e3cff41e9ff22c` 均为 `Passed`、`mainThread=true`；第二场 `reused_process=True`，复用 PID `46872`。损毁头盔组合最终力量为 `6`；两张上勾拳后虚弱和易伤均为 `3`；两张穿透哀嚎后临时 Power 与负力量绝对值均为 `18`，没有出现内部力量再次翻倍。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `RuinedHelmet.TryModifyPowerAmountReceived(...)` | 损毁头盔 | 本场第一次获得正力量时数值翻倍，之后不再修改 | 两次 `2` 点力量依次结算为 `4 + 2` |
| `RuinedHelmet.AfterModifyingPowerAmountReceived(...)` | 损毁头盔 | 第一次修改后消费一场一次状态，并进入分支指纹 | 私有状态文本与第二次不触发均一致 |
| `UnsettlingLamp.BeforePowerAmountChanged(...)` | 不安油灯 | 第一张由持有者打出、对敌方施加可见减益且未被人工制品挡住的牌成为触发牌 | 上勾拳的虚弱先触发，临时 Power 场景也正确识别 |
| `UnsettlingLamp.ModifyPowerAmountGivenMultiplicative(...)` | 不安油灯 | 触发牌施加的所有减益翻倍；已翻倍临时 Power 的内部 Power 不重复翻倍 | 上勾拳两种减益均翻倍，穿透哀嚎最终为 `-18` 而非 `-30` |

### `RELIC-REACTIVE-BATCH-057`（21 项）

闭环：最终 Release DLL 在同一个真实可见游戏进程中连续执行十一份固定夹具。通过原生 `PotionCmd` 建立药水栏后，逐项比较药水使用、回合开始、格挡清空、手牌清空、回合结束、星能消耗、充能球回合末被动、空手抽牌、伤害倍率和三个动态结算边界。模拟与原生动作队列比较生命、格挡、Power、四牌堆、充能球、遗物私有状态及相关 RNG；最后一个场景才退出游戏。官中名称均从当前 PCK 的 `localization/zhs` 精确读取。

结果：runId `42bc155f89db4cdaa06a1291de69740a`、`7e6396ce4a9e4561bd7bda7d36cda63e`、`4042f176abeb41c792b950487acb2d6f`、`ad8fc278c46240149714499181bd6689`、`1632269c11084308908ee8a53abfd797`、`a1e0cfc71cda41af835f461e2d654c66`、`f3c0431c762b43cf871c44ba0ea66ffb`、`4ad51475db574a31b68672c8ff640cf5`、`abf43a46ef9e45cc8a7ad088806c497a`、`c86cb30c6ddb494aabf2345fb13c8d7a`、`32b6996e20ad41bca87ad0a7824e01ac` 全部 `Passed`、`mainThread=true`。情感芯片场景最终产生四次目标 RNG 触发：正常回合末充能球被动和次回合情感芯片各触发一次，二者均由镀金缆线把队首球触发次数加一。开发期差分发现并修正了生产搜索遗漏正常充能球 `BeforeTurnEnd`、金纸触发早于手牌清空、情感芯片跨回合历史未滚动等真实问题；夹具自身的格挡清空、佩尔之眼空手条件和烘焙手套选择注入问题也在最终复跑前纠正。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `BeltBuckle.AfterPotionUsed(...)` | 腰带扣 | 用掉最后一瓶药水后只获得一次 `2` 点敏捷，并记录已生效状态 | 原生药水动作与模拟 Power、私有状态一致 |
| `Bookmark.AfterFlush(...)` | 书签 | 从保留的非 X 正费用牌中按 `CombatCardSelection` 随机选择一张，本次打出前费用 `-1` | 选牌、临时费用和 RNG 一致 |
| `CaptainsWheel.AfterBlockCleared(...)` | 舵盘 | 第 3 回合格挡清空后获得 `18` 格挡 | 回合号、清空顺序和最终格挡一致 |
| `EmotionChip.AfterPlayerTurnStart(...)` | 情感芯片 | 上一轮受到未被格挡的伤害时，下一回合额外触发全部充能球被动 | 正常回合末与额外被动均未遗漏，镀金缆线组合一致 |
| `GalacticDust.AfterStarsSpent(...)` | 星系尘埃 | 每累计消耗 `10` 星能获得 `10` 格挡，余数跨动作保留并进入指纹 | 多次消耗后的格挡、余数和状态一致 |
| `GhostSeed.AfterCardEnteredCombat(...)` | 幽灵种子 | 进入战斗的基础打击与防御牌获得虚无 | 现有牌和新进入战斗牌的关键词一致 |
| `HistoryCourse.AfterAutoPrePlayPhaseEntered(...)` | 历史课 | 第 2 回合起自动打出上回合最后一张非复制攻击牌；未知自动目标前停止静态分支 | 到达 `DynamicResolution` 后由原生结算并重搜 |
| `HornCleat.AfterBlockCleared(...)` | 船夹板 | 第 2 回合格挡清空后获得 `14` 格挡 | 回合号、清空顺序和最终格挡一致 |
| `JossPaper.AfterSideTurnEnd(...)` | 金纸 | 虚无牌先随手牌清空而消耗，再计入每 `5` 张抽 `1` 张；余数进入指纹和跨回合续用文本 | 五张晕眩耗尽后抽 `1`、余数 `0`，实时/模拟完整状态一致 |
| `Kusarigama.AfterSideTurnEnd(...)` | 锁镰 | 所属方回合结束后清零 RF 分支内技能牌计数 | 两回合私有计数文本一致 |
| `LunarPastry.AfterSideTurnEnd(...)` | 月亮糕点 | 所属方回合结束获得 `1` 星能，并继续分派星能获得语义 | 星能与后续状态一致 |
| `MrStruggles.AfterPlayerTurnStart(...)` | 抱抱先生 | 每回合开始对所有敌人造成等于当前回合数的无倍率伤害 | 目标集合、伤害和回合号一致 |
| `PaelsEye.AfterSideTurnStart(...)` | 佩尔之眼 | 记录本轮是否实际包含持有者，供零出牌额外回合判定使用 | 分支状态进入指纹，边界条件一致 |
| `PaelsEye.ShouldTakeExtraTurn(...)` | 佩尔之眼 | 本场尚未使用且本回合没有普通出牌时请求持有者额外回合 | 搜索在消费额外回合前明确停止 |
| `PaelsEye.AfterTakingExtraTurn(...)` | 佩尔之眼 | 额外回合结算后标记本场已使用，不能再次触发 | 原生结算后的状态由下一次搜索读取 |
| `ParryingShield.AfterSideTurnEnd(...)` | 招架盾 | 回合结束至少保留 `10` 格挡时，按 `CombatTargets` 随机对一名敌人造成 `6` 伤害 | 目标 RNG 与伤害一致 |
| `ReptileTrinket.AfterPotionUsed(...)` | 爬行动物饰品 | 每次用药获得 `3` 点临时力量，并在原生时点回收 | 用药后的力量与生命周期一致 |
| `SparklingRouge.AfterBlockCleared(...)` | 闪亮口红 | 第 3 回合格挡清空后获得 `1` 力量和 `1` 敏捷 | 两种 Power 与格挡清空顺序一致 |
| `ToastyMittens.AfterPlayerTurnStart(...)` | 烘焙手套 | 抽牌后选择并消耗一张手牌，再获得 `1` 力量 | 求解器接管后的未来回合会搜索并自动提交；首回合仍在首搜前由原生界面处理，尚未无人值守 |
| `UnceasingTop.AfterHandEmptied(...)` | 不休陀螺 | 出牌后手牌为空则抽 `1` 张；需要洗弃牌堆时停在洗牌边界 | 空手触发、抽牌和边界一致 |
| `UndyingSigil.ModifyDamageMultiplicative(...)` | 不死符文 | 攻击者当前生命不高于其灾厄时，对持有者的有倍率攻击伤害减半 | 原生怪物攻击与模拟最终伤害一致 |

### `RELIC-TURN-LIFECYCLE-BATCH-056`（26 项）

闭环：最终 Release DLL 在两个真实可见游戏进程中连续执行八份固定夹具，共完成十五条生产模拟/原生动作队列完整状态差分并推进至第 3 回合。逐项比较生命、格挡、能量、星能、金币、Power、牌堆、卡牌伤害、充能球、球槽、奥斯蒂及战斗 RNG；RF 私有预测状态同时纳入分支指纹。所有名称均从当前 PCK 的 `localization/zhs` 精确读取。

结果：runId `bc4743ac739149eebba6604b7eba1e57`、`af792f83a8534b24a2e1491700876aa2`、`c7c5bd00e24448028c38ec14a4b8efa0`、`d4bfa86467324c24b3c4f33bbe4f027f`、`865b327f179c49c18ec8c91e5f37995a`、`6f6821e7f23c4465818ef2fddca36757`、`da0ff580d0a54509b020e332c0a6c161`、`0247efea9e0540fba1697ca783b926c9` 全部 `Passed`、`mainThread=true`。苦无、手里剑和彩虹戒指原先只有 RF 风险标记而没有数值效果，本批纠正后每轮均与原生一致；佩尔的士兵完成 `10 → 5 → 10` 格挡三回合冷却；八次放血路径由 `50 HP` 降至 `33 HP`，下一回合首次自伤再次被恶魔之舌回满。开发期四次失败均为夹具值或注入格式错误，保留在机器证据中。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `BeatingRemnant.BeforeSideTurnStart(...)` | 律动残余 | 每回合清零 `20` 点生命损失上限计数 | 八次放血与下一回合重置一致 |
| `BrilliantScarf.BeforeSideTurnStart(...)` | 艳丽围巾 | 每回合清零非自动出牌计数 | 跨回合费用与出牌序列一致 |
| `DemonTongue.BeforeSideTurnStart(...)` | 恶魔之舌 | 每回合允许首次主动受伤后治疗等量生命 | 两回合首次放血均回满该段伤害 |
| `FencingManual.AfterSideTurnStart(...)` | 击剑指南 | 首回合锻造 `10`，无君王之剑时先生成 | 手牌君王之剑伤害为 `20` |
| `Kunai.AfterCardPlayed(...)` | 苦无 | 每打出第 `3` 张攻击获得 `1` 敏捷；RF 原实现只记风险 | 两回合均正确获得敏捷 |
| `Kunai.BeforeSideTurnStart(...)` | 苦无 | 每回合清零攻击计数 | 第 2 回合重新按三张触发 |
| `LetterOpener.AfterSideTurnStart(...)` | 开信刀 | 第 2 回合起清零技能计数 | 两回合各三张技能均造成 `5` 全体伤害 |
| `MiniRegent.AfterStarsSpent(...)` | 迷你储君 | 每回合首次消耗星能获得 `1` 力量 | 同回合两次只触发一次，下一回合再次触发 |
| `MiniRegent.BeforeSideTurnStart(...)` | 迷你储君 | 清零并指纹化本回合使用标志 | 跨回合力量为 `1 → 2` |
| `MusicBox.BeforeSideTurnStart(...)` | 音乐盒 | 清零每回合首次攻击复制状态 | 两回合均生成虚无攻击复制 |
| `OrnamentalFan.BeforeSideTurnStart(...)` | 精致折扇 | 每回合清零攻击计数 | 两回合均按第 3 张攻击获得格挡 |
| `PaelsLegion.AfterSideTurnStart(...)` | 佩尔的士兵 | 双倍格挡触发后冷却 `2` 回合 | 三回合格挡依次 `10/5/10` |
| `PaelsTears.BeforeSideTurnEnd(...)` | 佩尔之泪 | 回合结束前记录是否留有能量；RF ignored 不能算精确 | 分支状态已记录并进入指纹 |
| `PaelsTears.AfterSideTurnStart(...)` | 佩尔之泪 | 上回合留有能量时获得 `2` 能量 | 下一回合总能量为 `5` |
| `PhylacteryUnbound.AfterSideTurnStart(...)` | 无界命匣 | 每回合为奥斯蒂召唤 `2` 点 | 奥斯蒂 `5 → 7 HP` |
| `RainbowRing.AfterCardPlayed(...)` | 彩虹戒指 | 每回合打过攻击、技能、能力后获得 `1` 力量和敏捷；RF 原实现只记风险 | 两回合 Power 均正确增加 |
| `RainbowRing.BeforeSideTurnStart(...)` | 彩虹戒指 | 清零并指纹化四个回合计数 | 第 2 回合可再次触发 |
| `Regalite.BeforeSideTurnStart(...)` | 君王矿石 | 清零每回合首次生成牌获得格挡的状态 | 下一回合生成牌再次获得 `4` 格挡 |
| `RunicCapacitor.AfterSideTurnStart(...)` | 符文电容器 | 首回合增加 `3` 个充能球槽位 | 缺陷机器人球槽为 `6` |
| `Sai.AfterSideTurnStart(...)` | 钗 | 每回合开始获得 `7` 格挡 | 原生/模拟均为 `7` |
| `SealOfGold.AfterSideTurnStart(...)` | 黄金印 | 金币不少于 `3` 时消耗 `3` 金币获得 `1` 能量 | 金币 `10 → 7`、能量 `+1`，资源进入指纹 |
| `Shuriken.AfterCardPlayed(...)` | 手里剑 | 每打出第 `3` 张攻击获得 `1` 力量；RF 原实现只记风险 | 两回合均正确获得力量 |
| `Shuriken.BeforeSideTurnStart(...)` | 手里剑 | 每回合清零攻击计数 | 第 2 回合重新按三张触发 |
| `SymbioticVirus.AfterSideTurnStart(...)` | 共生病毒 | 首回合生成 `1` 个黑暗充能球 | 充能球快照一致 |
| `VelvetChoker.BeforeSideTurnStart(...)` | 天鹅绒颈圈 | 每回合清零六张出牌上限计数 | 第 2 回合未继承上回合计数 |
| `VeryHotCocoa.AfterSideTurnStart(...)` | 烫嘴可可 | 首回合获得 `4` 能量 | 与黄金印组合净获得 `5` 能量 |

### `RELIC-TURN-START-BATCH-055`（25 项）

闭环：最终 Release DLL 在同一个真实可见游戏 PID 中连续执行五份固定夹具，共完成六条生产模拟/原生动作队列完整状态差分。首回合组合同时比较生命、格挡、能量、星能、手牌升级、双方 Power 和全体敌人伤害；周期组合连续推进到第 3 回合并走“未打攻击”和“打出攻击”两条孙子兵法路径；茶具、破损核心和原生精英房轰鸣海螺分别验证一次性状态、充能球和房间条件。所有名称均从当前 PCK 的 `localization/zhs` 精确读取。

结果：runId `15e3ad8361bc4e74abc8711682f5d6f9`、`9a6553d7aa12403ca3e066e04d260372`、`5e73586e1f3147e5bc22f3b1b561378e`、`0ed8b0da9b93491eb67f5c2adcaa3de5`、`47d61ee94cde464da188e2fdc77ad83f` 全部 `Passed`、`mainThread=true`。代码拆分后的最终程序集另以新进程 runId `2e26c0f7951043d480875c082234fe27`、`626fe9138738421480937ce7ba4dd29b` 复跑首回合组合与跨回合周期，三条差分全部通过。首回合组合最终为玩家 `49 HP`、`20` 格挡、`9` 能量、`7` 星能，目标受 `3` 伤害；周期组第 2/3 回合能量分别为 `8/6`，遗物私有计数文本与实机逐回合一致。开发期两次失败均为夹具期望错误，预测与实机快照当时已经一致，错误与修正过程保留在机器证据中。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Akabeko.AfterSideTurnStart(...)` | 赤牛 | 首回合获得 `8` 层活力 | 原生/模拟均为 `8` |
| `ArtOfWar.AfterCardPlayed(...)` | 孙子兵法 | 打出攻击牌时记录本回合攻击历史；RF 的 ignored 登记不能算精确支持 | 打出攻击/不打攻击两条路线已纠正并通过 |
| `ArtOfWar.AfterSideTurnEnd(...)` | 孙子兵法 | 记录本回合是否打出攻击牌并纳入状态指纹 | 两条跨回合路径一致 |
| `ArtOfWar.AfterEnergyReset(...)` | 孙子兵法 | 上回合未打攻击时获得 `1` 能量，打出攻击时不触发 | 第 2 回合触发、第 3 回合不触发 |
| `BagOfMarbles.BeforeSideTurnStart(...)` | 弹珠袋 | 首回合对所有敌人施加 `1` 层易伤 | 全场 Power 快照一致 |
| `Bellows.AfterPlayerTurnStart(...)` | 风箱 | 首回合升级整手牌 | 打击与防御均升级 `1` 次 |
| `BloodVial.AfterPlayerTurnStartLate(...)` | 小血瓶 | 首回合治疗 `2` | 与假小血瓶及王室猛毒组合顺序一致 |
| `BoneTea.AfterPlayerTurnStart(...)` | 骨茶 | 尚有次数时升级首回合整手并消耗一次 | 手牌与剩余计数一致 |
| `BoomingConch.AfterSideTurnStart(...)` | 轰鸣海螺 | 精英战首回合获得 `1` 能量 | 原生 `KnightsElite` 差分通过 |
| `Bread.AfterSideTurnStart(...)` | 面包 | 第 1 回合失去 `2` 能量 | 与灯笼组合后净变化 `-1` |
| `Brimstone.AfterSideTurnStart(...)` | 硫磺 | 每回合自己获得 `2` 力量、所有敌人获得 `1` 力量 | 双方 Power 一致 |
| `Candelabra.AfterSideTurnStart(...)` | 烛台 | 只在第 2 回合获得 `2` 能量 | 第 2 回合触发，第 3 回合不重复 |
| `Chandelier.AfterSideTurnStart(...)` | 吊灯 | 只在第 3 回合获得 `3` 能量 | 第 2 回合不触发，第 3 回合触发 |
| `CrackedCore.BeforeSideTurnStart(...)` | 破损核心 | 每件在首回合生成 `1` 个闪电球 | 原有遗物、注入遗物和初始球合计 `3` 个 |
| `DiamondDiadem.AfterSideTurnStart(...)` | 钻石头冠 | 首回合获得 `20` 格挡与 `1` 层残影 | 格挡与 Power 一致 |
| `DivineDestiny.AfterSideTurnStart(...)` | 天命所归 | 首回合获得 `7` 星能 | 原生/模拟均为 `7` |
| `FakeBloodVial.AfterPlayerTurnStartLate(...)` | 小血瓶？？？ | 首回合治疗 `1` | 组合生命结算一致 |
| `FakeHappyFlower.AfterSideTurnStart(...)` | 开心小花？？？ | 每 `5` 回合获得 `1` 能量 | 回卷为 `0` 后触发，下一回合计数为 `1` |
| `FakeVenerableTeaSet.AfterEnergyReset(...)` | 古茶具套装？？？ | 保存标记存在时获得 `1` 能量并消费标记 | 一次性触发后计数为 `0` |
| `HappyFlower.AfterSideTurnStart(...)` | 开心小花 | 每 `3` 回合获得 `1` 能量 | 回卷为 `0` 后触发，下一回合计数为 `1` |
| `Lantern.AfterSideTurnStart(...)` | 灯笼 | 首回合获得 `1` 能量 | 组合能量结算一致 |
| `MercuryHourglass.AfterPlayerTurnStart(...)` | 水银沙漏 | 每回合对所有敌人造成 `3` 伤害 | 两名敌人均失去 `3 HP` |
| `RedMask.BeforeSideTurnStart(...)` | 红面具 | 首回合对所有敌人施加 `1` 层虚弱 | 全场 Power 快照一致 |
| `RoyalPoison.AfterPlayerTurnStart(...)` | 王室猛毒 | 首回合受到 `4` 点不可格挡伤害 | 与两种血瓶组合后 `50 → 49 HP` |
| `VenerableTeaSet.AfterEnergyReset(...)` | 古茶具套装 | 保存标记存在时获得 `2` 能量并消费标记 | 与仿品组合共获得 `3`，随后均清零 |

### `RELIC-HOOKS-BATCH-054`（18 项）

闭环：最终 Release DLL 在同一真实可见游戏进程中连续运行七份固定夹具。跨回合组从第 1 回合推进到第 2 回合，完整比较能量、格挡、手牌、Power、逐牌状态与费用；其余组实际打出普通、升级、附魔、低伤与仆从牌，并让石甲碗虫执行真实攻击。注能核心在跑局建立前获得，以原生首回合充能球结果作为首次搜索快照，再验证未来回合不重复。所有遗物名均从当前游戏 PCK 的 `localization/zhs` 精确读取。

结果：runId `8fa141b1f6f7471f8c05d0e9edc5a458`、`9e184e60cfcd47aa861d693f78490336`、`8f7123af83214bcfaf565efac1015832`、`9a829e65fef14168a7df6d0d9edab982`、`e3a022e42b164cec983d76e3a2bc2a5f`、`f2ba3444069749878232adc0004038ca`、`fc1bd1b112f249c281e9beceaaa9b1d5` 全部 `Passed`、`mainThread=true`；风险分类改动后的最终程序集另以新进程 runId `12388ca5a3ef443184b391f1e7b7e0bc` 复跑跨回合组合并通过。冰淇淋在第 2 回合把剩余 `2` 能量保留后增加 `4`；三角铃鼓只在第 1 回合保留整手；坚固钳子把 `20` 格挡截为 `10`。攻击组合总伤害 `42`，发条靴把 `4` 点未格挡攻击提高到 `5`，钨合金棍把 `1` 点生命损失降为 `0`，维特鲁威仆从令两张仆从牌分别造成 `26` 伤害和获得 `14` 格挡。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `ChemicalX.ModifyXValue(...)` | 化学物X | 所属玩家的 X 牌额外获得 `2` 点 X 值 | 原生/模拟派生 Hook 均为 `2` |
| `FakeStrikeDummy.ModifyDamageAdditive(...)` | 打击木偶？？？ | 所属玩家的打击牌伤害增加 `1` | 普通与升级打击组合差分通过 |
| `IceCream.ShouldPlayerResetEnergy(...)` | 冰淇淋 | 第 1 回合正常重置，之后保留旧能量并增加最大能量 | 第 2 回合实际能量 `2 + 4 = 6` |
| `InfusedCore.AfterSideTurnStart(...)` | 注能核心 | 首回合原生充能 `3` 个闪电球，未来回合不重复 | 初始快照及第 2 回合一致 |
| `InfusedCore.ModifyOrbValue(...)` | 注能核心 | 所属玩家闪电球数值增加 `1` | 基础 `3` 修正为 `4` |
| `MiniatureCannon.ModifyDamageAdditive(...)` | 微型大炮 | 升级攻击牌伤害增加 `3` | 升级打击路径通过 |
| `MysticLighter.ModifyDamageAdditive(...)` | 神秘打火机 | 附魔攻击牌伤害增加 `9` | 锋利小刀路径通过 |
| `RingingTriangle.ShouldFlush(...)` | 三角铃鼓 | 只在第 1 回合保留整手 | 第 1 回合 `false`、第 2 回合 `true` |
| `RunicPyramid.ShouldFlush(...)` | 符文金字塔 | 每回合保留整手 | 第 1/2 回合均为 `false` |
| `SneckoSkull.ModifyPowerAmountGivenAdditive(...)` | 异蛇头骨 | 所属玩家施加中毒时增加 `1` 层 | 致命毒药施加 `6` 层 |
| `SpikedGauntlets.TryModifyEnergyCostInCombat(...)` | 带刺手甲 | 所属玩家能力牌费用增加 `1` | 致命毒药加燃烧共支付 `3` 能量 |
| `StrikeDummy.ModifyDamageAdditive(...)` | 打击木偶 | 所属玩家的打击牌伤害增加 `3` | 普通与升级打击组合差分通过 |
| `SturdyClamp.ShouldClearBlock(...)` | 坚固钳子 | 阻止所属玩家回合开始清空格挡 | 原生/模拟均返回 `false` |
| `SturdyClamp.AfterPreventingBlockClear(...)` | 坚固钳子 | 保留格挡时最多留下 `10` | `20 → 10` 完整回合差分通过 |
| `TheBoot.ModifyHpLostAfterOstyLate(...)` | 发条靴 | 所属玩家造成的 `1..4` 点攻击生命损失提高到 `5` | 小刀 `4 → 5` |
| `TungstenRod.ModifyHpLostAfterOsty(...)` | 钨合金棍 | 所属玩家每次生命损失减少 `1`，最低为 `0` | 石甲碗虫剩余 `1 → 0` |
| `VitruvianMinion.ModifyDamageMultiplicative(...)` | 维特鲁威仆从 | 所属玩家仆从牌伤害翻倍 | 仆从俯冲轰炸 `13 → 26` |
| `VitruvianMinion.ModifyBlockMultiplicative(...)` | 维特鲁威仆从 | 所属玩家仆从牌格挡翻倍 | 仆从牺牲 `7 → 14` |

### `RELIC-DRAW-STATE-BATCH-053`（12 项）

闭环：最终 Release DLL 在真实可见游戏中运行三份固定夹具。摆动球与花粉核心从指定计数连续推进六次完整玩家回合建立；怀表分别在上回合打出 `4`、`0`、`3` 张牌；赐福鹿角、葬礼面具、忍术卷轴和发光珍珠在开始跑局后由原生遗物命令获得。每一步从同一状态分别执行生产模拟与原生 Hook/动作队列，比较四牌堆逐牌状态、抽牌数、相关 RNG，并直接比较原生私有计数与预测跨回合复用文本。

结果：周期组 runId `84ac48d9e35549488eb0483f4e6411a8` 从第 `1` 回合连续完成到第 `7` 回合，六次检查全部 `Passed`；怀表 runId `021ad7eceed44665b99e5e1c145d6fc4` 的 `4/0/3` 张三条检查全部 `Passed`；首回合快照 runId `16cde608f4a541fa90fe1f5834ebe50f` 为 `Passed`。怀表 `AfterCardPlayed` 在 RF `0.13.8` 中被登记为 ignored，不能算精确支持，现已由求解器维护分支计数。三个有状态遗物的计数均进入双 `ulong` 节点指纹和不使用哈希的跨回合完整状态文本。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Pendulum.BeforeHandDraw(...)` | 摆动球 | 抽牌前按 `3` 回合周期推进计数 | 六次连续回合计数一致 |
| `Pendulum.ModifyHandDraw(...)` | 摆动球 | 计数归零的回合多抽 `1` 张 | 第 `3/6` 次周期触发一致 |
| `Pocketwatch.AfterCardPlayed(...)` | 怀表 | 每打出一张牌增加本回合私有计数 | RF 假精确已纠正；实机出牌一致 |
| `Pocketwatch.BeforeSideTurnStart(...)` | 怀表 | 新回合开始前转存上回合计数并清零 | `4/0/3` 三组私有状态一致 |
| `Pocketwatch.ModifyHandDraw(...)` | 怀表 | 上回合打牌数不超过 `3` 时多抽 `3` 张 | `4` 张不触发，`0/3` 张触发 |
| `PollinousCore.BeforeHandDraw(...)` | 花粉核心 | 每次抽牌前增加回合计数 | 六次连续回合计数一致 |
| `PollinousCore.ModifyHandDraw(...)` | 花粉核心 | 累计到第 `4` 回合时多抽 `2` 张 | 第 `2/6` 次检查触发一致 |
| `PollinousCore.AfterModifyingHandDraw()` | 花粉核心 | 实际增加抽牌后把计数清零 | 原生/模拟复用文本均归零 |
| `BlessedAntler.BeforeHandDraw(...)` | 赐福鹿角 | 首回合向抽牌堆随机插入 `3` 张晕眩，后续不重复 | 原生初始快照与下一回合差分通过 |
| `FuneraryMask.BeforeHandDraw(...)` | 葬礼面具 | 首回合向抽牌堆随机插入 `3` 张灵魂，后续不重复 | 原生初始快照与下一回合差分通过 |
| `NinjaScroll.BeforeHandDraw(...)` | 忍术卷轴 | 首回合生成 `3` 张小刀到手牌，后续不重复 | 原生初始快照与下一回合差分通过 |
| `RadiantPearl.BeforeHandDraw(...)` | 发光珍珠 | 首回合生成 `1` 张冷光到手牌，后续不重复 | 原生初始快照与下一回合差分通过 |

### `RELIC-PURE-HOOKS-BATCH-052`（20 项）

闭环：最终 Release DLL 在真实可见游戏中于战斗建立后注入遗物，对真实 `CombatState` 与生产 `SimulatedCombatState` 调用原生抽牌/最大能量 Hook；另把六件带回合条件的遗物在同一场战斗连续推进到第 2、3 回合。精英条件使用原生 `KnightsElite`，没有用普通遭遇伪造房间类型。

结果：仓库固定夹具的组合抽牌 runId `ae6eb92e6fce4d6b8a6e00442ec3989a`、组合最大能量 runId `6bb57c84387a45fd8670dd2fb985ace2`、连续第 2/3 回合 runId `3bafb759027b40a7b67f473d35e500a8`、精英房轰鸣海螺 runId `9273bf4c915b473ca7765a064949a9b2` 均为 `Passed`。最后一项直接使用原生 `KnightsElite` 遭遇，最终测试接口不再暴露无效的房间类型覆盖参数。未来回合开发期失败定位到 RF 模拟玩家状态没有回合号；新增分支回合计数后，第 2 回合抽牌 `7`、最大能量 `4`，第 3 回合抽牌 `7`、最大能量 `5`，与实机一致。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `BagOfPreparation.ModifyHandDraw(...)` | 准备背包 | 仅第 1 回合多抽 `2` 张 | 当前回合及第 2 回合边界通过 |
| `BigMushroom.ModifyHandDraw(...)` | 大蘑菇 | 仅第 1 回合少抽 `2` 张 | 当前回合及第 2 回合边界通过 |
| `BlessedAntler.ModifyMaxEnergy(...)` | 赐福鹿角 | 最大能量增加 `1` | 组合纯 Hook 差分通过 |
| `BloodSoakedRose.ModifyMaxEnergy(...)` | 血染玫瑰 | 最大能量增加 `1` | 组合纯 Hook 差分通过 |
| `BoomingConch.ModifyHandDraw(...)` | 轰鸣海螺 | 精英战第 1 回合多抽 `2` 张 | 原生精英遭遇差分通过 |
| `Bread.ModifyMaxEnergy(...)` | 面包 | 第 1 回合不加能量，之后最大能量增加 `1` | 第 2/3 回合通过 |
| `Ectoplasm.ModifyMaxEnergy(...)` | 灵体外质 | 最大能量增加 `1` | 组合纯 Hook 差分通过 |
| `Fiddle.ModifyHandDraw(...)` | 小提琴 | 每回合多抽 `2` 张 | 组合纯 Hook 差分通过 |
| `PaelsBlood.ModifyHandDraw(...)` | 佩尔之血 | 每回合多抽 `1` 张 | 组合纯 Hook 差分通过 |
| `PaelsFlesh.ModifyMaxEnergy(...)` | 佩尔之肉 | 从第 3 回合起最大能量增加 `1` | 第 2/3 回合阈值通过 |
| `PhilosophersStone.ModifyMaxEnergy(...)` | 贤者之石 | 最大能量增加 `1` | 组合纯 Hook 差分通过 |
| `PrismaticGem.ModifyMaxEnergy(...)` | 棱彩宝石 | 最大能量增加 `1` | 组合纯 Hook 差分通过 |
| `PumpkinCandle.ModifyMaxEnergy(...)` | 南瓜蜡烛 | 剩余点燃次数大于零时最大能量增加 `1` | 获取后计数与纯 Hook 通过 |
| `RingOfTheDrake.ModifyHandDraw(...)` | 长蛇戒指 | 前 `3` 回合多抽 `2` 张 | 第 1/2/3 回合通过 |
| `RingOfTheSnake.ModifyHandDraw(...)` | 蛇之戒指 | 仅第 1 回合多抽 `2` 张 | 当前回合及第 2 回合边界通过 |
| `SneckoEye.ModifyHandDraw(...)` | 异蛇之眼 | 每回合多抽 `2` 张 | 组合纯 Hook 差分通过 |
| `Sozu.ModifyMaxEnergy(...)` | 添水 | 最大能量增加 `1` | 组合纯 Hook 差分通过 |
| `SpikedGauntlets.ModifyMaxEnergy(...)` | 带刺手甲 | 最大能量增加 `1` | 组合纯 Hook 差分通过 |
| `VelvetChoker.ModifyMaxEnergy(...)` | 天鹅绒颈圈 | 最大能量增加 `1` | 组合纯 Hook 差分通过 |
| `WhisperingEarring.ModifyMaxEnergy(...)` | 低语耳环 | 最大能量增加 `1` | 战斗建立后注入，纯 Hook 差分通过 |

### `POWER-LIFECYCLE-BATCH-051`（31 项）

闭环：最终 Release DLL 在真实可见游戏中分组运行数值变化、星能/能量、卡牌入场附魔、出牌限制、回合结束、受伤/破甲、回合开始和药水朝向场景。每项从同一真实状态分别执行生产模拟和原生动作，比较生命、格挡、能量、星能、Power、Power 私有状态、四个牌堆、逐牌附魔与重放次数；动态场景运行正式后台搜索并断言 `DynamicResolution`。杂耍施加时的已有攻击计数复用第 037 批原生出牌差分。

结果：最终通过 runId `de517160fc324bcdb04bd1415cabf0c6`、`70d0117d9bc744f294a20ad805d6171d`、`3e471e2538da473ba3beb6be4211c30b`、`b4a42e6fabb64836827b7639734a9a9c`、`170bb531b7984d47ada722781a5fa88f`、`a2fadab49c4c460fa7a4cf5fd4380e47`、`c1b61bba52364fa98ea922687bf58cb1`、`cc11a2c0965647528ef528597d805cbe`、`cc37c0e298d842bbbfaf2083f6beef9d`、`7933c97a623845ddb49ef8bed077213f`、`a8e5183178a94ed5be609804ab7a5d57`、`a2a96633b4d24eafa7a653fe78ebed06`、`6a45f3865c8b4c8bbfb210dae06b36ec`、`16ff70adcb664ca3b6b45bb83dcdc543`、`19678b232f9f4a0fa46731ab30fd73f6`、`1593c116de2743bc8df9c9ae701dbdc6`、`65e22743e93f4ec885842cf6c828e4d8`、`57ee60626f4a4a24945fd82044b320f4`、`98b2530c17824506990fee587153b957`、`4179188c9ec9479496fefbf9bc9d4f2c`、`7bb6556b962e4500bf66697374292ff7`、`15f40ee3e62847cba73f557b60995f05`，均为 `Passed`。开发期夹具曾暴露组合附魔污染、无效卡牌 ID、错误怪物宿主、Doom 原生帮助器缺钩子，以及“正式最优路线不保证打出测试牌”造成的错误边界预期；全部修正后以最终 DLL 重跑通过，失败 run 未计入结论。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `AmbergrisPower.ShouldTakeExtraTurn(...)` | 龙涎香 | 原生额外回合开始前停止预测 | 正式搜索返回 `DynamicResolution` |
| `AsleepPower.AfterDamageReceived(...)` | 沉睡 | 未格挡伤害移除覆甲与沉睡，原生苏醒行动前停止 | RF 忽略注册已纠正；动态边界通过 |
| `AsleepPower.AfterSideTurnEnd(...)` | 沉睡 | 所属方回合末递减；归零苏醒前停止 | `2 → 1` 差分及 `1` 层边界通过 |
| `BattlewornDummyTimeLimitPower.AfterSideTurnEnd(...)` | 时间限制 | 回合末递减；归零逃跑前停止，按事件失败而非击杀结算 | `2 → 1` 差分及 `EventDefeat` 边界通过；第二档问题包完整执行通过 |
| `BlackHolePower.AfterStarsGained(...)` | 黑洞 | 每次获得星能，对所有可命中敌人造成等于层数的无力量伤害 | 隐秘宝藏获得星能差分一致 |
| `BurrowedPower.AfterBlockBroken(...)` | 埋地 | 破甲后移除埋地并在原生眩晕换招前停止 | RF 忽略注册已纠正；掘地兽实机通过 |
| `BurrowedPower.AfterRemoved(...)` | 埋地 | 移除后清空剩余格挡 | 破甲移除路径模拟/实机一致 |
| `ChildOfTheStarsPower.AfterStarsSpent(...)` | 群星之子 | 每花费 1 星能获得等于层数的格挡 | 星之斗篷总计获得 `9` 格挡 |
| `DarkEmbracePower.AfterSideTurnEnd(...)` | 黑暗之拥 | 按回合末消耗的虚无牌数量抽牌 | 虚无手牌清空时序差分一致 |
| `DoomPower.AfterSideTurnEnd(...)` | 灾厄 | 灾厄不小于当前生命时在正确一方回合末死亡 | 原生 Doom 钩子与模拟均扣至 `0 HP` |
| `FocusPower.ModifyOrbValue(...)` | 集中 | 快照已有集中继续走原生球值；模拟中集中变化后停止远期预测 | 实打碎片整理后动态标记通过 |
| `GalvanicPower.AfterCardEnteredCombat(...)` | 流电 | 新进入战斗且未受苦难的能力牌获得流电苦难 | 新生成能力牌自伤差分一致 |
| `JugglingPower.AfterApplied(...)` | 杂耍 | 中途施加时继承本回合此前攻击数 | 第 037 批第三张愤怒复制闭环通过 |
| `MayhemPower.AfterAutoPrePlayPhaseEntered(...)` | 乱战 | 原生随机自动出牌前停止，结算后重搜 | 正式后台搜索返回 `DynamicResolution` |
| `OrbitPower.AfterEnergySpent(...)` | 环绕轨道 | 每实例累计花费能量，每满 `4` 点按层数回能 | 四张打击后能量差分一致，余数入指纹 |
| `PaleBlueDotPower.AfterCardPlayed(...)` | 暗淡蓝点 | 每回合第 5 张牌只触发一次下回合抽牌 | RF 忽略注册已纠正；第五张牌获得 `2` 层抽牌 |
| `ShroudPower.AfterPowerAmountChanged(...)` | 厄运之衣 | 自己施加灾厄时按层数获得格挡 | 死亡使者原生出牌差分一致 |
| `SleightOfFleshPower.AfterPowerAmountChanged(...)` | 血肉戏法 | 自己施加非临时减益时按层数造成无力量伤害 | 痛击施加易伤差分一致 |
| `SlumberPower.AfterDamageReceived(...)` | 熟睡 | 未格挡伤害递减，归零安装苏醒行动前停止 | RF 忽略注册已纠正；动态边界通过 |
| `SlumberPower.AfterSideTurnEnd(...)` | 熟睡 | 所属方回合末递减，归零苏醒前停止 | `2 → 1` 差分及 `1` 层边界通过 |
| `SmoggyPower.AfterCardEnteredCombat(...)` | 烟雾弥漫 | 本回合已打技能后，新进入战斗的技能获得烟雾 | 新旧防御牌区分正确 |
| `SmoggyPower.AfterSideTurnEnd(...)` | 烟雾弥漫 | 所属方回合末清除全部烟雾 | 回合末后防御恢复可打出 |
| `SmoggyPower.ShouldPlay(...)` | 烟雾弥漫 | 受烟雾影响的牌不可打出 | 原生与模拟均拒绝第二张防御 |
| `SurroundedPower.BeforePotionUsed(...)` | 遭到包围 | 药水目标在另一侧时先更新朝向 | 凯撒巨蟹战向 Crusher 用药后均朝左 |
| `SwordSagePower.AfterCardEnteredCombat(...)` | 剑圣 | 新进入战斗的君王之剑获得对应重放次数 | 新牌逐项状态差分一致 |
| `SwordSagePower.AfterPowerAmountChanged(...)` | 剑圣 | 层数变化同步修正所有非复制君王之剑 | 实打剑圣后既有君王之剑重放次数一致 |
| `ToolsOfTheTradePower.AfterPlayerTurnStart(...)` | 必备工具 | 有手牌时原生选弃界面前停止预测 | 正式后台搜索返回 `DynamicResolution` |
| `TyrannyPower.AfterPlayerTurnStart(...)` | 暴政 | 有手牌时原生选择效果前停止预测 | 正式后台搜索返回 `DynamicResolution` |
| `ViciousPower.AfterPowerAmountChanged(...)` | 凶恶 | 自己施加正数易伤时按层数抽牌 | 痛击后额外抽牌差分一致 |
| `VitalSparkPower.AfterCardEnteredCombat(...)` | 活力火花 | 新进入战斗的技能获得等于总层数的污化 | 新防御获得 `2` 层污化 |
| `VitalSparkPower.AfterCardPlayed(...)` | 活力火花 | 打出污化牌时获得等于总层数的污化状态 | 统一 AfterCardPlayed Mirror 只施加一次；防御后获得 `2` 层 |

### `POWER-DEATH-BATCH-050`（33 项）

闭环：最终 Release DLL 在真实可见游戏中连续运行死亡边界与格挡清空夹具。正式搜索逐个覆盖复活、换怪、分裂、尸体保留、隐藏属性退款和包围朝向修正；原生测试对象、幻象宿主、爪牙宿主与尸蛞蝓均使用正确怪物类型。所有正式搜索都在工作线程运行，死亡、Power 注入、原生 Hook、回合钩子及快照采集在主线程完成。

结果：动态边界与差分 runId `0a67df3fe26340f58a191cb189ba5c93`、`eab19f523636458a8a581481d91fccbd`、`d35d0b3f8d184206b23c6785a8d01abe`、`b789c160e07444b185c273f249fc094e`、`4579cee620964cb1b8ea902585a9cdb6`、`23f0d8d6d1274320a91c599d3c088ea1`、`a6c4564a0e4c4f87836061286e26965a`、`a9ac01723aef42eaa16b0e105439d0d9`、`5b941a8e3bb8460ea38226e81a64a724`、`86756fb45f0544b483db0b63ce1609d7`、`546f686703634fab98064dfcf284dc91`、`1948f8ca5dd740a398eafe5977f71537`、`f8ec75e062fe4b4ab8851158548e3edf`、`a1d3dbb77d614f4ea5a7c9097fa4d74b`、`a9235e456d9442b58ef2f0c6b212e992`、`d6dd06a0fc2d47f895a5ca23e73a6a40` 全部 `Passed`、`mainThread=true`；动态搜索日志均为 `worker_thread=True`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `AdaptablePower.AfterDeath(...)` | 适者生存 | 测试对象进入原生死亡形态前停止预测 | 原生测试对象返回 `DynamicResolution` |
| `AdaptablePower.ShouldAllowHitting(...)` | 适者生存 | 不预测复活期间不可选中的瞬时状态 | 死亡边界后由真实状态重搜 |
| `AdaptablePower.ShouldCreatureBeRemovedFromCombatAfterDeath(...)` | 适者生存 | 不把保留的尸体误当作移除 | 击杀路线未标记战斗胜利 |
| `AdaptablePower.ShouldStopCombatFromEnding()` | 适者生存 | 原生复活完成前阻止错误胜利 | 动态终点通过 |
| `AdaptablePower.ShouldPowerBeRemovedAfterOwnerDeath()` | 适者生存 | 由原生死亡流程保留并在重搜读取 | 动态终点通过 |
| `CrabRagePower.AfterDeath(...)` | 蟹之怒 | 另一同阵营怪物死亡后获得 `6` 力量、`99` 格挡并移除 | 模拟/实机逐字段一致 |
| `DampenPower.AfterDeath(...)` | 抑制 | 任何死亡可能改变私有施法者集合时停止预测 | 正式搜索返回 `DynamicResolution` |
| `IllusionPower.AfterDeath(...)` | 幻象 | 原生复活行动安装前停止预测 | 原生 Parafright 场景通过 |
| `IllusionPower.ShouldAllowHitting(...)` | 幻象 | 不预测复活期间不可选中窗口 | 动态终点通过 |
| `IllusionPower.ShouldCreatureBeRemovedFromCombatAfterDeath(...)` | 幻象 | 不把幻象尸体误当作最终移除 | 未获得错误胜利分 |
| `IllusionPower.ShouldPowerBeRemovedOnDeath(...)` | 幻象 | Buff、Debuff 与临时 Power 的保留由原生流程结算 | 重搜读取真实 Power 集合 |
| `IllusionPower.AfterApplied(...)` | 幻象 | 原生宿主先获得爪牙，再进入搜索 | Parafright 原生出生状态通过 |
| `InfestedPower.AfterDeath(...)` | 寄生物 | 生成四只蠕虫前停止预测 | 正式搜索返回 `DynamicResolution` |
| `InfestedPower.ShouldStopCombatFromEnding()` | 寄生物 | 蠕虫生成前不能标记胜利 | 动态终点通过 |
| `MinionPower.ShouldOwnerDeathTriggerFatal()` | 爪牙 | 爪牙死亡的 Fatal 与主敌人清理由原生流程处理 | 原生 TorchHeadAmalgam 场景通过 |
| `MinionPower.ShouldPowerBeRemovedAfterOwnerDeath()` | 爪牙 | 爪牙 Power 保留由原生死亡流程处理 | 动态终点通过 |
| `PainfulStabsPower.ShouldCreatureBeRemovedFromCombatAfterDeath(...)` | 疼痛戳刺 | 持有者尸体保留时停止预测 | 正式搜索返回 `DynamicResolution` |
| `PainfulStabsPower.ShouldPowerBeRemovedAfterOwnerDeath()` | 疼痛戳刺 | Power 保留由原生死亡流程处理 | 动态终点通过 |
| `PossessSpeedPower.AfterDeath(...)` | 抢夺速度 | 私有敏捷退款前停止预测 | 正式搜索返回 `DynamicResolution` |
| `PossessStrengthPower.AfterDeath(...)` | 抢夺力量 | 私有力量退款前停止预测 | 正式搜索返回 `DynamicResolution` |
| `RavenousPower.AfterDeath(...)` | 饥饿 | 同伴死亡后获得 `4` 力量并跳过下一行动 | 原生尸蛞蝓模拟/实机一致 |
| `ReattachPower.AfterDeath(...)` | 接续 | 百足虫节段死亡状态或全体淡出前停止预测 | 正式搜索返回 `DynamicResolution` |
| `ReattachPower.ShouldAllowHitting(...)` | 接续 | 不预测接续期间不可选中窗口 | 动态终点通过 |
| `ReattachPower.ShouldCreatureBeRemovedFromCombatAfterDeath(...)` | 接续 | 节段尸体不被误判为最终移除 | 未获得错误胜利分 |
| `ReattachPower.ShouldOwnerDeathTriggerFatal()` | 接续 | 是否只剩最后节段由原生流程判断 | 动态终点通过 |
| `ReattachPower.ShouldPowerBeRemovedAfterOwnerDeath()` | 接续 | Power 保留由原生流程处理 | 重搜读取真实状态 |
| `SelfFormingClayPower.AfterBlockCleared(...)` | 自成型黏土 | 格挡清空后获得 `7` 格挡并移除 | 模拟/实机逐字段一致 |
| `StockPower.AfterDeath(...)` | 库存 | 替代斧头机器人生成前停止预测 | 正式搜索返回 `DynamicResolution` |
| `StockPower.ShouldStopCombatFromEnding()` | 库存 | 库存耗尽前不能标记胜利 | 动态终点通过 |
| `SurprisePower.AfterDeath(...)` | 意外 | 两只地精及偷取状态转移前停止预测 | 正式搜索返回 `DynamicResolution` |
| `SurprisePower.ShouldStopCombatFromEnding()` | 意外 | 地精生成前不能标记胜利 | 动态终点通过 |
| `SurroundedPower.AfterDeath(...)` | 遭到包围 | 敌人死亡导致朝向修正前停止预测 | 正式搜索返回 `DynamicResolution` |
| `ToricToughnessPower.AfterBlockCleared(...)` | 坚韧之环 | 获得动态值 `5` 格挡并由 `2` 层减至 `1` 层 | 模拟/实机逐字段一致 |

开发期失败保留：runId `0beadc1f492146eabe43bf0f0d0c7d68` 把幻象注入唯一主敌人，原生 `AfterApplied` 将其变成次要敌人，场景本身不合法；改用原生 Parafright 后通过。runId `2966441c68654cd1bb99f68a4f024c47` 把饥饿注入大型假人，原生回调按设计强转 `CorpseSlug`，改用原生尸蛞蝓后通过。生产代码没有为错误宿主增加兜底。

### `POWER-TURN-START-BATCH-049`（22 项）

闭环：按游戏 `0.111.0` 的 `BeforeSideTurnStart → AfterEnergyResetLate → BeforeHandDraw → AfterPlayerTurnStart → AfterSideTurnStart → AfterSideTurnStartLate` 顺序接入生产模拟。首轮 Release DLL 在同一真实可见游戏进程连续执行 `19` 个请求：固定生成、死灵绑定者生命周期、Loop、狱火、三个 RF 私有计数、倒数计时、沙坑递减/致死、七个正式搜索动态边界，以及夜魇和第 047 批回归。随后修正动态边界后的状态污染和懒惰初始计数，在重新构建的最终 Release DLL、同一可见游戏进程中复跑熵、好勇斗狠、懒惰和第 047 批能力触发回归。全部最终结果为 `Passed`、`mainThread=true`；搜索边界同时断言求解器实际运行在工作线程。

结果：固定差分 runId `a411a920b771496490092ce53654aff4`、`428db40266b548b795bcbad602ea58eb`、`9e2a2522f33c4efeaa0be9f9b4bd2eda`、`27eb5c2da43446b980670ab406a739a4`、`27620418da5744d89ebb9d0454ee9bbc`、`37b0cc9841964987aea81396b1a7e343`、`37a29bdd86974b7180a809bf0325ff9f`、`cd6f8db2b8b54caa92206f31a61e4c38`、`191002c14bba4b8f8460945a90ae3505`、`1295370e90b2440e8415c629359904c2`；动态边界 runId `5019c03debec46a7924ab25b9fe97a51`、`c27584ed15e14fdfb4f8fd289da27785`、`c06381528b4640ed9edff7e56b673b02`、`e8fd65f83987431fab6e95e1f015898a`、`2cc852cde0eb4ae68da2e2977a594027`、`540ee848006f4c3ea3b94d1e5b6f5b4d`、`679ee1274f4348f884b0d311b6e539d6`；夜魇和旧回归 runId `2fbc2455063f4b35a7384b6bd5324cbe`、`e66f48a68fad4562aa73d13dcf52d96d`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `CallOfTheVoidPower.BeforeHandDraw(...)` | 虚空之唤 | 随机生成非基础、非远古角色牌前停止预测，由真实游戏结算后重搜 | 正式后台搜索返回 `DynamicResolution` |
| `CreativeAiPower.BeforeHandDraw(...)` | 创造性AI | 随机生成能力牌前停止预测 | 正式后台搜索返回 `DynamicResolution` |
| `ForegoneConclusionPower.BeforeHandDraw(...)` | 既定事项 | 抽牌堆选牌及可能洗牌前停止预测 | 正式后台搜索返回 `DynamicResolution` |
| `HelloWorldPower.BeforeHandDraw(...)` | 你好世界 | 随机生成普通牌前停止预测 | 正式后台搜索返回 `DynamicResolution` |
| `InfiniteBladesPower.BeforeHandDraw(...)` | 无尽刀刃 | 普通抽牌前向手牌生成等同层数的小刀 | `2` 张小刀差分通过 |
| `NightmarePower.BeforeHandDraw(...)` | 夜魇 | 普通抽牌前生成选中牌的干净复制并移除精确实例 | 跨回合生成 `3` 张愤怒回归通过 |
| `SentryModePower.BeforeHandDraw(...)` | 哨卫模式 | 普通抽牌前向手牌生成等同层数的扫视射线 | `1` 张扫视射线差分通过 |
| `SpectrumShiftPower.BeforeHandDraw(...)` | 光谱偏移 | 随机生成无色牌前停止预测 | 正式后台搜索返回 `DynamicResolution` |
| `EntropyPower.AfterPlayerTurnStart(...)` | 熵 | 手牌选牌并随机变形前停止预测 | 正式后台搜索返回 `DynamicResolution` |
| `HibernatePower.AfterPlayerTurnStart(...)` | 休眠 | 玩家回合开始后层数减 `1` | `2 → 1` 差分通过 |
| `InfernoPower.AfterPlayerTurnStart(...)` | 狱火 | 按私有 `SelfDamage` 造成不可格挡、无力量加成的自伤 | 实际打出后下一回合自伤 `1` 差分通过 |
| `LoopPower.AfterPlayerTurnStart(...)` | 循环 | 队首充能球被动触发等同层数的次数 | 队首闪电球触发两次、总伤害 `6` 差分通过 |
| `RollingBoulderPower.AfterPlayerTurnStart(...)` | 滚石 | 对全部可选敌人造成当前层数伤害，再增加动态伤害值 | 伤害 `5`、层数 `5 → 10` 差分通过 |
| `SummonNextTurnPower.AfterPlayerTurnStart(...)` | 下回合召唤 | 按层数召唤/强化奥斯蒂并移除精确实例 | 与缚魂命匣组合后奥斯蒂 `1 → 6`、Power 移除通过 |
| `BoundPhylactery.AfterEnergyResetLate(...)` | 缚魂命匣 | 非首回合在其他能量重置钩子之后召唤 `1` 点奥斯蒂 | 与下回合召唤的原生顺序差分通过 |
| `AggressionPower.BeforeSideTurnStart(...)` | 好勇斗狠 | 未来弃牌堆存在攻击牌时，在随机选择、移入手牌和升级前停止预测 | 正式后台搜索返回 `DynamicResolution` |
| `HardenedShellPower.BeforeSideTurnStart(...)` | 硬化外壳 | 每次阵营回合开始把 RF 分支内本回合受伤计数归零 | 两个回合各承受上限 `2` 点自伤，累计 `4` 点差分通过 |
| `SlothPower.BeforeSideTurnStart(...)` | 懒惰 | 持有者回合开始把 RF 分支内已出牌数归零 | 上回合达到上限后下一回合仍可出牌，差分通过 |
| `VoidFormPower.BeforeSideTurnStart(...)` | 虚空形态 | 持有者回合开始恢复本回合免费出牌额度 | 连续两回合各免费打出一张打击，能量保持 `3` 通过 |
| `CountdownPower.AfterSideTurnStart(...)` | 倒数计时 | 使用克隆的 `CombatTargets` RNG 选择可选敌人并施加等同层数的灾厄 | 多敌状态、Power 目标和 RNG 计数逐字段一致 |
| `SandpitPower.AfterSideTurnStartLate(...)` | 沙坑 | 所有普通回合开始钩子后，在敌方回合把精确实例减 `1` | `2 → 1` 差分通过 |
| `SandpitPower.AfterRemoved(...)` | 沙坑 | 归零且所有者/目标存活时强制杀死目标玩家及奥斯蒂 | `1 → 0` 后玩家生命归零、Power 移除差分通过 |

开发期失败保留：首次组合召唤用铁甲战士导致不存在奥斯蒂；切换死灵绑定者后差分定位到遗漏的缚魂命匣晚阶段召唤。Loop 首次预期误把后注入的冰霜球当成队首，日志证明模拟与实机都正确触发初始闪电球。三个计数器最初放在同一战斗，上一检查残留的懒惰污染第三项，随后拆成独立请求。侵略首次正式搜索已正确返回动态边界，但登记器错误地只看搜索前弃牌堆；改为只验证 Power 已登记，是否触发仍由未来模拟牌堆决定。最终复测熵时首次沿用启动脚本默认的敌人 `1 HP`，搜索当回合斩杀后正确返回 `None`；改为 `999 HP` 后动态边界通过，该次失败属于夹具调用参数错误。

### `POWER-END-TURN-BATCH-048`（14 项）

闭环：最终 Release DLL 在同一真实可见游戏 PID 中连续运行四份夹具。玩家侧验证真实打出独白后的力量增长与回收、魔法炸弹回合末伤害/移除、幽冥之界敌方回合末移除及魔法炸弹施加者正常死亡移除；敌方侧验证摧残递减、紧追不放与紧勒移除，以及湮灭在施加者每次出牌后施加灾厄并于玩家回合末移除；另外在回合末后继续出牌，验证神气制胜和胆小的私有计数确实重置，而不只是面板数值相同。

结果：最终 runId `9bed94608c52421ba1bb03e728a7fe6b`、`c8d0c19a9c6a40c19ecde04ff5f451b9`、`c75dcdd692774e9e8dee6c99bf63554b`、`ceae52c5def1440db23676401f9f1140` 均为 `Passed`、`mainThread=true`，四场共 `10` 项完整状态差分通过。开发期先后发现测试助手漏分派新 Power、直接注入独白缺失合法动态变量来源，以及本回合新增实例无法被旧类型状态表移除；测试入口和精确实例可变副本修正后整组复跑通过。魔法炸弹死亡用例最终使用 `CheckedEnemy` 被真实打击致死，不把 `force: true` 强制删除当作普通死亡闭环。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `DebilitatePower.AfterSideTurnEnd(...)` | 摧残 | 持有者所属方回合结束时层数减 `1` | 敌方回合末差分通过 |
| `MagicBombPower.AfterSideTurnEnd(...)` | 魔法炸弹 | 施加者存活时对持有者造成层数伤害并移除当前实例 | 玩家回合末伤害与移除通过 |
| `MagicBombPower.AfterDeath(...)` | 魔法炸弹 | 施加者正常死亡且未阻止移除时，移除炸弹 | 真实打击致死差分通过 |
| `MonologuePower.BeforeCardPlayed/AfterCardPlayed(...)` | 独白 | 只记录开始出牌前已存在的实例，每张牌后获得对应力量并累计回收值 | 真实打出独白后两次出牌通过 |
| `MonologuePower.AfterSideTurnEnd(...)` | 独白 | 移除精确实例并回收本回合实际获得的全部力量 | 力量与 Power 同时归零通过 |
| `OblivionPower.BeforeCardPlayed/AfterCardPlayed(...)` | 湮灭 | 施加者每打出一张牌，持有者获得等同当前层数的灾厄 | 出牌配对与灾厄差分通过 |
| `OblivionPower.AfterSideTurnEnd(...)` | 湮灭 | 玩家方回合结束时移除 | 移除差分通过 |
| `PanachePower.AfterSideTurnEnd(...)` | 神气制胜 | 将 RF 分支计数与 `CardsLeft` 可见状态一并重置为 `5` | 回合后继续四次出牌未提前触发，差分通过 |
| `SicEmPower.AfterSideTurnEnd(...)` | 紧追不放 | 持有者所属方回合结束时移除 | 敌方回合末差分通过 |
| `SkittishPower.AfterSideTurnEnd(...)` | 胆小 | 对方回合结束时重置本回合已获得格挡标记 | 回合后第二次受攻击重新获得 `5` 格挡通过 |
| `StranglePower.AfterSideTurnEnd(...)` | 紧勒 | 持有者所属方回合结束时移除精确实例 | 出牌伤害后的移除差分通过 |
| `UnderworldPower.AfterSideTurnEnd(...)` | 幽冥之界 | 敌方回合结束时移除 | 玩家持有状态的敌方回合末差分通过 |

### `POWER-NATIVE-HOOKS-BATCH-047`（11 项）

闭环：最终 Release DLL 在真实可见游戏中向玩家和怪物注入组合 Power，分别以真实 `CombatState` 和生产模拟态调用游戏原生纯计算钩子 `ModifyHandDraw`、`ModifyMaxEnergy`、`ShouldClearBlock` 与 `ShouldFlush`，断言两边均等于固定预期。另实际打出友谊后推进下一玩家回合，确认搜索中新施加的 Power 也会进入原生最大能量计算，并复跑第 031 批下回合资源与手牌清空回归。

结果：runId `a44a6d16729b4b5ba3de06d8c8969f0f` 为 `Passed`，组合结果为抽牌 `8`、最大能量 `6`，壁垒与计划妥当均阻止对应清理，埋地阻止怪物清除格挡。首次资源回归 runId `aae79637c1024c8b9ab8ca04717c87f1` 发现模拟没有经历原版 `DrawCardsNextTurnPower.AmountOnTurnStart` 快照时点，导致预测抽 `5`、原生抽 `7`；补齐时序后，runId `7dc524710afa4d7bbf02754d1f2eee31` 的四项资源回归、runId `482bd7ff1a6e46beabed57b9b57a8639` 的三项手牌生命周期回归均通过。实际打出友谊的 runId `ea6a6e6769704e2d8b213399135e2e8e` 通过，下一回合能量为 `4`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `BarricadePower.ShouldClearBlock(...)` | 壁垒 | 持有者跨回合保留格挡 | 原生纯钩子差分通过 |
| `BurrowedPower.ShouldClearBlock(...)` | 埋地 | 持有者跨回合保留格挡 | 怪物原生纯钩子差分通过 |
| `DemesnePower.ModifyHandDraw/ModifyMaxEnergy(...)` | 领域 | 抽牌数与最大能量均增加层数 | 组合差分通过 |
| `MindRotPower.ModifyHandDraw(...)` | 心灵腐化 | 抽牌数减少层数且不低于零 | 组合差分通过 |
| `FriendshipPower.ModifyMaxEnergy(...)` | 友谊 | 最大能量增加层数 | 组合差分及实际施加后跨回合通过 |
| `PyrePower.ModifyMaxEnergy(...)` | 薪火之源 | 最大能量增加层数 | 组合差分通过 |
| `WasteAwayPower.ModifyMaxEnergy(...)` | 衰朽 | 最大能量减少层数 | 组合差分通过 |
| `ToolsOfTheTradePower.ModifyHandDraw(...)` | 必备工具 | 抽牌数增加层数 | 纯数值钩子通过；回合开始选弃牌仍未分析 |
| `TyrannyPower.ModifyHandDraw(...)` | 暴政 | 抽牌数增加层数 | 纯数值钩子通过；回合开始选消耗牌仍未分析 |
| `WellLaidPlansPower.ShouldFlush(...)` | 计划妥当 | 阻止自动清空整手牌，逐牌整理仍按原版执行 | 原生纯钩子及手牌生命周期回归通过 |

### `POWER-TRIGGER-BATCH-047`（2 项）

闭环：最终 Release DLL 在真实可见游戏中实际打出绯红披风并推进完整下一玩家回合；另先打出零费攻击，再施加野性，再打出零费攻击，逐项比较生命、格挡、牌堆、Power 及私有历史计数。

结果：最终 runId `d6eab7949d05480db82d0ea141b15f26` 为 `Passed`、`mainThread=true`，绯红披风结算 `1` 点自伤与 `7` 点格挡，野性中途施加后的历史计数一致。首次 runId `6ff2dc75eb11443d924eba890b72f97f` 只触发了阵营回合开始，没有触发更早的 `AfterPlayerTurnStart`，属于夹具生命周期不完整；改为完整玩家回合建立后复测通过。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `CrimsonMantlePower.AfterPlayerTurnStart(...)` | 绯红披风 | 按实例私有自伤值失去生命，再按层数获得格挡 | 完整下一玩家回合差分通过 |
| `FeralPower.AfterApplied(...)` | 野性 | 中途施加时继承本回合已经打出的零费攻击数量 | 私有历史计数差分通过 |

### `ENCHANTMENTS-ORB-BATCH-046`（14 项）

闭环：最终 Release DLL 在真实可见游戏中注入游戏原生附魔和等离子球，从同一初始快照分别执行生产模拟与原生附魔/出牌/回合生命周期，比较逐牌附魔 ID、层数、启用状态、私有累计字段、伤害、格挡、费用、重复次数、牌堆位置、玩家能量和完整战斗状态。

结果：runId `70d8e334be0b4d6e94c52c1afbf656a` 为 `Passed`、`mainThread=true`，`13` 种附魔和等离子球回合开始共 `14` 个检查全部完成。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Adroit.RecalculateValues()` | 伶俐 | 原生初始化格挡数值，RF 出牌时读取该值 | 数值与实例状态差分通过 |
| `Corrupted.EnchantDamageMultiplicative(...)` | 腐化 | 有力量攻击伤害乘 `1.5`，出牌后自伤 `2` | 伤害与生命差分通过 |
| `Glam.EnchantPlayCount(...)` | 华彩 | 本场第一次使用额外打出一次，随后禁用 | 重复次数与私有状态差分通过 |
| `Goopy.EnchantBlockAdditive(...)` | 黏糊 | 获得 `Amount - 1` 格挡，使用后层数增长 | 格挡与成长状态差分通过 |
| `Imbued.AfterAutoPrePlayPhaseEntered(...)` | 注能 | 第一回合自动预出牌阶段自动打出该牌 | 自动出牌及牌堆差分通过 |
| `Instinct.EnchantDamageMultiplicative(...)` | 本能 | 有力量攻击伤害乘 `2` | 伤害差分通过 |
| `Momentum.EnchantDamageAdditive(...)` | 动量 | 增加私有累计伤害，出牌后继续成长 | 伤害与私有累计值差分通过 |
| `Nimble.EnchantBlockAdditive(...)` | 灵巧 | 格挡增加附魔层数 | 格挡差分通过 |
| `Sharp.EnchantDamageAdditive(...)` | 锋利 | 有力量攻击增加附魔层数伤害 | 伤害差分通过 |
| `SlumberingEssence.BeforeFlush(...)` | 沉眠精华 | 未打出时在清空手牌前获得本次打出前减费 `1` | 跨回合费用差分通过 |
| `Spiral.EnchantPlayCount(...)` | 涡旋 | 基础打击或防御额外打出一次 | 重复次数差分通过 |
| `TezcatarasEmber.EnchantDamageAdditive(...)` | 特兹卡塔拉的余烬 | 原生设为 `0` 费、永恒，并使有力量攻击增加 `3` 伤害 | 费用、关键词与伤害通过 |
| `Vigorous.EnchantDamageAdditive(...)` | 活力 | 启用时增加附魔层数伤害，出牌后禁用 | 伤害与启用状态差分通过 |
| `PlasmaOrb.AfterTurnStartOrbTrigger(...)` | 等离子 | 每个球在玩家回合开始获得 `1` 能量 | 回合开始能量差分通过 |

### `MONSTER-MOVES-BATCH-046-EXACT`（22 项）

闭环：最终 Release DLL 在真实可见游戏中逐项强制设置 `22` 个真实怪物行动，从同一状态执行生产预测和原生 `MoveState`，比较生命、格挡、Power、四个牌堆、逐牌状态和相关 RNG。

结果：runId `848e647621224a919eb4b228847d08cb` 为 `Passed`、`mainThread=true`，全部 `22` 个行动完成最终状态差分。

| 怪物与行动 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| 三种 `DecimillipedeSegment` × `WRITHE/BULK/CONSTRICT/DEAD` | 残杀千足虫：扭动／胀大／紧缠／死亡 | 分别结算攻击、`2` 力量、`1` 虚弱或空行动 | `12` 条逐项差分通过 |
| `FatGremlin.SPAWNED_MOVE` | 胖地精：醒来 | 按意图结算攻击 | 差分通过 |
| `GremlinMerc.GIMME/DOUBLE_SMASH/HEHE` | 地精佣兵：拿来／双重猛击／嘿嘿 | 记录偷取金币、施加 `2` 虚弱或获得 `2` 力量 | `3` 条逐项差分通过 |
| `LivingFog.ADVANCED_GAS/SUPER_GAS_BLAST` | 活雾：先进毒气／超级毒气爆炸 | 施加 `1` 层烟雾或结算攻击 | `2` 条逐项差分通过 |
| `TheInsatiable.LIQUIFY_GROUND_MOVE` | 无厌沙虫：液化地面 | 施加 `4` 层沙坑，并向随机抽牌堆和弃牌堆各加入 `3` 张惊逃 | 牌堆、Power 与 RNG 差分通过 |
| `ThievingHopper.NAB/HAT_TRICK/FLUTTER` | 偷窃草蜢：抢夺／帽子戏法／振翅 | 结算攻击，振翅另获得 `5` 层对应 Power | `3` 条逐项差分通过 |

### `MONSTER-DYNAMIC-BOUNDARY-BATCH-046`（3 项）

闭环：主线程在真实可见游戏中设置怪物的真实当前行动，再由工作线程运行正式 `CombatBeamSolver`。分别选择召唤、修改后续 AI 私有状态和改写牌库三种机制，验证已知即时效果执行后，旧路线在下一玩家回合建立前停止。

结果：runId `c2e94b8096a34e23aea2b875af1347ab` 为 `Passed`、`mainThread=true`，三个边界日志均记录 `worker_thread=True`。组装师“组装”、青蛙骑士“甲虫冲锋”、偷窃草蜢“偷盗”均返回 `DynamicResolution`。首次 runId `70ad94f35f8243dc80f1064bc12fd08d` 因测试夹具把真实键 `BEETLE_CHARGE` 写成 `BEETLE_CHARGE_MOVE` 而在设置行动时失败，未进入语义计算；修正后复测通过。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Fabricator.FABRICATE_MOVE` | 组装师：组装 | 原生召唤后从真实敌方阵容重搜 | 正式搜索边界通过 |
| `FrogKnight.BEETLE_CHARGE` | 青蛙骑士：甲虫冲锋 | 结算攻击后，不从过期私有 AI 状态预测下一回合 | 正式搜索边界通过 |
| `ThievingHopper.THIEVERY_MOVE` | 偷窃草蜢：偷盗 | 原生改写牌库后从真实牌堆重搜 | 正式搜索边界通过 |

### `POTION-ON-USE-BATCH-045`（20 项）

闭环：最终 Release DLL 在真实可见游戏中逐项执行剩余 `19` 种药水的生产模拟与原生结算差分。选牌药水由测试器向原生选择器提交与预测一致的牌实例，并比较玩家与全部敌人的生命、最大生命、格挡、能量、星能、Orb、Power、四个牌堆、逐牌费用/升级/实例状态、药水槽及相关 RNG。瓶中精灵通过真实致死伤害自动触发；再生药水另执行完整玩家回合结束。最后让全自动实际使用狡诈药水，验证动态生成边界、旧路线作废、同回合重搜和继续执行生成牌。

结果：`19` 个药水场景全部 `Passed` 且 `mainThread=true`。再生药水第一次有效差分发现 RF `0.13.8` 只执行治疗、没有执行原版递减；求解器补齐后以 runId `ead6eeabd1ca477c99031c4bbd13302b` 复测通过。最终全自动 runId `0663fe0d7335400f8fb81bf8317c95e5` 为 `Passed`、`combatEnded=true`、`finishedTurn=1`；结构化日志依次记录狡诈药水原生部署、`DEPLOY_DYNAMIC_RESOLVED`、`reason=DynamicResolution` 的同回合搜索，以及三张升级小刀实际打出。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Ambergris.OnUse(...)` | 龙涎香 | 回复最大生命一半并获得龙涎香；额外回合由原生结算后同回合重搜承接 | 即时状态差分及动态边界通过 |
| `Ashwater.OnUse(...)` | 灰水 | 从手牌选择任意数量牌消耗，搜索与部署使用同一组实例 | 有界选择及牌堆差分通过 |
| `CunningPotion.OnUse(...)` | 狡诈药水 | 生成三张升级小刀；旧路线在生成处停止，原生生成后立即重搜 | 即时差分及全自动动态闭环通过 |
| `DropletOfPrecognition.OnUse(...)` | 预知之滴 | 从抽牌堆选择一张牌加入手牌 | 选择、实例位置与部署一致 |
| `FairyInABottle.OnUse(...)` | 瓶中精灵 | 不作为手动动作；首次致死时自动消耗并按最大生命比例复活 | 真实致死触发差分通过 |
| `FoulPotion.OnUse(...)` | 污浊药水 | 对玩家和所有存活非宠物敌人造成牌面无力量伤害并结算死亡 | 全场生命差分通过 |
| `FruitJuice.OnUse(...)` | 果汁 | 同时增加最大生命和当前生命；原生结算后从新上限重搜 | 最大/当前生命差分通过 |
| `FyshOil.OnUse(...)` | 异鱼之油 | 给目标玩家施加牌面力量与敏捷 | 两项 Power 一致 |
| `GamblersBrew.OnUse(...)` | 赌徒特酿 | 选择任意数量手牌弃置并抽取等量牌，按真实牌堆处理洗牌边界 | 选择、抽牌与牌堆差分通过 |
| `GigantificationPotion.OnUse(...)` | 超巨化药水 | 获得超巨化，使下一次符合条件的有力量卡牌攻击造成三倍伤害并消耗状态 | 即时 Power 与后续攻击差分通过 |
| `KingsCourage.OnUse(...)` | 王之勇气 | 锻造 `15`；需要时生成未消耗的君王之剑并提升全部非复制实例 | 生成、锻造与逐牌状态一致 |
| `LiquidMemories.OnUse(...)` | 液态记忆 | 从弃牌堆选择一张牌加入手牌，并令其本回合费用为 `0` | 选择、位置和局部费用一致 |
| `PotOfGhouls.OnUse(...)` | 尸鬼瓮 | 生成两张灵魂；旧路线在生成处停止并在原生结算后重搜 | 即时差分及动态边界通过 |
| `PotionOfBinding.OnUse(...)` | 缚魂药水 | 给所有可命中敌人施加牌面虚弱与易伤，并遵守人工制品 | 多目标 Power 差分通过 |
| `PotionShapedRock.OnUse(...)` | 药水形状的石头 | 对选定敌人造成牌面无力量伤害并结算死亡 | 目标生命与死亡一致 |
| `RegenPotion.OnUse(...)` | 再生药水 | 获得再生；每回合结束先回复当前层数生命，再减少 `1` 层 | 即时与完整回合末差分通过 |
| `RegenPower.BeforeSideTurnEndEarly(...)` | 再生：回合结束早期 | 补齐 RF `0.13.8` 遗漏的层数递减，玩家和敌方均保持治疗后递减顺序 | `5 HP` 治疗且 `5 → 4` 通过 |
| `ShipInABottle.OnUse(...)` | 瓶中船 | 立即获得格挡，并获得等量下回合格挡 | 当前与下回合状态一致 |
| `SoldiersStew.OnUse(...)` | 士兵炖汤 | 令全部战斗牌堆中的打击标签牌本场重复次数增加 `1` | 全牌堆逐牌实例状态一致 |
| `TouchOfInsanity.OnUse(...)` | 癫狂之触 | 选择一张符合条件的手牌，令其本场战斗费用为 `0` | 选择、部署、费用及指纹状态一致 |

边界：龙涎香、狡诈药水、果汁和尸鬼瓮在搜索中仍是明确的动态结算边界，不假装能够从旧模拟快照继续；全自动会先执行它们，再等待原生动作队列稳定并在同一回合重新搜索。选牌药水沿用已有 Top-K 局部分支预算，不宣称穷举所有组合。

### `POTION-ON-USE-BATCH-044`（30 项）

闭环：最终 Release DLL 在真实可见游戏中，从相同原生战斗快照分别运行生产药水模拟与游戏原生 `EnqueueManualUse`，逐字段比较玩家与全部敌人的生命、格挡、能量、星能、Orb、Power、四个牌堆、逐牌升级和药水槽。`30` 种确定性药水均完成即时差分；肌肉药水、速度药水、镣铐药水另执行对应方真实回合结束，验证临时力量、临时敏捷和临时降力的回收；能量药水和明耀酊剂另在玩家拥有“无法获得能量”时验证能量修正。最后用火焰药水完成“搜索候选 → 推荐路线 → 全自动原生使用 → 槽位消耗 → 击杀并结束战斗”的完整链路。

结果：`30` 条即时差分、`3` 条生命周期差分及 `2` 条能量阻断交互差分全部 `Passed` 且 `mainThread=true`。能量阻断最终 runId 为 `01293289952f4c58a661963bbc2939d6`、`0a08b1ef99804edf856849a08afe2ca2`。开发期首次格挡药水失败是测试静默注入没有创建原生药水 UI 节点，并非药水语义偏差；改用完整原生注入后通过。固化药水首次有效差分发现原版不是“格挡翻倍”，而是在已有格挡上再增加其两倍，因此 `7 → 21`；生产语义修正后复测通过。最终全自动 runId `9e702986623b40ea99d6663f6503d005` 通过，结构化日志明确记录 `kind=UsePotion`、`potion_id=FIRE_POTION`、原生 `DEPLOY_ACTION`、目标击杀和第 `1` 回合战斗结束。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `BeetleJuice.OnUse(...)` | 甲虫汁 | 给选定敌人施加 `4` 层缩小 | 目标 Power 一致 |
| `BlessingOfTheForge.OnUse(...)` | 熔炉的祝福 | 升级当前手牌中全部可升级牌 | 逐牌升级状态一致 |
| `BlockPotion.OnUse(...)` | 格挡药水 | 获得牌面格挡 | 原生差分一致 |
| `BloodPotion.OnUse(...)` | 鲜血药水 | 按最大生命百分比回复且不超过上限 | 生命一致 |
| `BoneBrew.OnUse(...)` | 骨头酿 | 召唤奥斯蒂并维护当前/最大生命与复活状态 | 亡灵绑定者原生差分一致 |
| `DexterityPotion.OnUse(...)` | 敏捷药水 | 获得敏捷 | Power 一致 |
| `Duplicator.OnUse(...)` | 复制药水 | 获得复制状态 | Power 一致；后续重复出牌钩子独立登记 |
| `EnergyPotion.OnUse(...)` | 能量药水 | 获得能量 | 资源一致 |
| `ExplosiveAmpoule.OnUse(...)` | 爆炸安瓿 | 对所有存活敌人造成固定伤害并结算死亡 | 多敌生命一致 |
| `FirePotion.OnUse(...)` | 火焰药水 | 对选定敌人造成固定伤害并结算死亡 | 差分及全自动斩杀链路通过 |
| `FlexPotion.OnUse(...)` | 肌肉药水 | 本回合获得力量，玩家回合结束完整回收 | 即时与生命周期均一致 |
| `FocusPotion.OnUse(...)` | 集中药水 | 获得集中 | Power 一致 |
| `Fortifier.OnUse(...)` | 固化药水 | 额外获得当前格挡的两倍，使总格挡变为原来的三倍 | `7 → 21` 与原生一致 |
| `GhostInAJar.OnUse(...)` | 罐装幽灵 | 获得无实体 | Power 一致 |
| `HeartOfIron.OnUse(...)` | 铁心药水 | 获得多层护甲 | Power 一致 |
| `LiquidBronze.OnUse(...)` | 流动铜液 | 获得荆棘 | Power 一致 |
| `LuckyTonic.OnUse(...)` | 幸运补剂 | 获得缓冲 | Power 一致 |
| `MazalethsGift.OnUse(...)` | 马萨雷斯的赠礼 | 获得仪式 | Power 一致 |
| `PoisonPotion.OnUse(...)` | 毒药水 | 给选定敌人施加中毒 | 目标 Power 一致 |
| `PotionOfCapacity.OnUse(...)` | 扩容药水 | 增加充能球槽位 | Orb 容量一致 |
| `PotionOfDoom.OnUse(...)` | 灾厄药水 | 给选定敌人施加灾厄 | 目标 Power 一致 |
| `PowderedDemise.OnUse(...)` | 消亡粉末 | 给选定敌人施加消亡 | 目标 Power 一致 |
| `RadiantTincture.OnUse(...)` | 明耀酊剂 | 获得 `1` 能量和 `3` 层明耀 | 资源与 Power 一致；无法获得能量时只施加明耀 |
| `ShacklingPotion.OnUse(...)` | 镣铐药水 | 本回合降低所有可命中敌人的力量，敌方回合结束完整恢复 | 即时与生命周期均一致 |
| `SpeedPotion.OnUse(...)` | 速度药水 | 本回合获得敏捷，玩家回合结束完整回收 | 即时与生命周期均一致 |
| `StableSerum.OnUse(...)` | 稳定血清 | 获得保留手牌 | Power 一致；手牌保留生命周期独立登记 |
| `StarPotion.OnUse(...)` | 星星药水 | 获得星能 | 资源一致 |
| `StrengthPotion.OnUse(...)` | 力量药水 | 获得力量 | Power 一致 |
| `VulnerablePotion.OnUse(...)` | 易伤药水 | 给选定敌人施加易伤 | 目标 Power 一致 |
| `WeakPotion.OnUse(...)` | 虚弱药水 | 给选定敌人施加虚弱 | 目标 Power 一致 |

边界：本批只关闭无选牌、无随机生成的确定性 `OnUse`。药水触发的遗物/Power `BeforePotionUsed`、`AfterPotionUsed` 联动仍按各自目录条目独立适配；能力药水、技能药水、从牌堆选择、随机生成以及自动复活药水没有被本批伪装成支持。

### `CARD-ON-PLAY-BATCH-043`（18 项）

闭环：生产模拟与原生出牌在同一真实可见游戏 PID `8404` 中连续完成 `10` 个场景、`12` 条完整状态差分，中间只返回主菜单。逐字段比较玩家、奥斯蒂与敌人的生命/最大生命/可命中状态、格挡、Power 及实例动态变量、能量、四个牌堆、逐牌费用/升级/实例状态和 `6` 组战斗 RNG；覆盖当前回合、玩家回合结束、下一回合抽牌前和自动预出牌阶段。最后一次测试脚手架改动只修正全自动出牌记录的锁存时机，随后最终 Release DLL 另以 PID `23060` 验证虚空形态实际被搜索和自动部署，并强制从第 `1` 回合推进、第 `2` 回合结束战斗。

结果：差分 runId `5ffea1a325844ccd94d694da40664980`、`8e8f85c867d445379b7c940e5c45c931`、`96e799e96046427ba626806ba03b683c`、`6fe20a35eec84dd28e516ae2a4766693`、`2808d05c669549bb951090d869815651`、`c1b7e667b3404414a955963149e70e56`、`e9c6c94a380a42b9a82e6e576e8ca46e`、`40ea05495d9d4d1caecbea88cb591a7e`、`d0aad5c94e044fbfb8d95dfc38fa5c66`、`f3aa213e66e74e0c95d3ddbd4e4e911a` 全部 `Passed` 且 `mainThread=true`；升级组合包含 `3` 条差分。虚空形态 runId `195e2829b76b46f79f70c4eb9f2a0b` 同样 `Passed` 且 `mainThread=true`，日志同时出现路线 `ACTION VOID_FORM` 与实机 `DEPLOY_ACTION VOID_FORM`。开发期失败暴露了《独白》漏结算下一张牌的真实问题并已修复；其余失败来自夹具牌去向、动态变量键名、测试回合号与战后历史检查时机，不计为最终通过证据。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Afterlife.OnPlay(...)` | 来生 | 按牌面召唤奥斯蒂，支持复活和最大生命增长 | 普通/升级状态一致 |
| `Bodyguard.OnPlay(...)` | 护卫 | 按牌面召唤奥斯蒂 | 普通/升级状态一致 |
| `Bolas.BeforeHandDraw(...)` | 流星锤 | 上回合打出后，在下回合抽牌前回到手牌 | 跨回合牌堆一致 |
| `Bombardment.AfterAutoPrePlayPhaseEnteredEarly(...)` | 轰击 | 位于消耗堆时在自动预出牌早期自动打出 | 伤害、牌堆和阶段一致 |
| `Cleanse.OnPlay(...)` | 洁净 | 从抽牌堆消耗一张所选牌，再召唤奥斯蒂 | 选择、牌堆和召唤一致 |
| `Dirge.OnPlay(...)` | 挽歌 | 按修正后的 X 逐次召唤，并把 X 张灵魂随机放入抽牌堆；升级版生成升级灵魂 | 普通/升级、牌堆和 RNG 一致 |
| `Eidolon.OnPlay(...)` | 幻景 | 自动打出消耗堆中所有可打出的虚无牌 | 幻影与对应 Power 状态一致 |
| `Enthralled.ShouldPlay(...)` | 执迷 | 执迷留在手牌时不能打出其他牌 | 原生与模拟均拒绝打击 |
| `KnifeTrap.OnPlay(...)` | 刀刃陷阱 | 自动打出消耗堆中全部小刀；升级版先升级小刀 | 普通/升级伤害和逐牌升级一致 |
| `Monologue.OnPlay(...)` | 独白 | 创建独立能力实例，下一张牌结算后分别获得力量并累计已施加值 | 与随后打出炸弹的原生状态一致 |
| `NecroMastery.OnPlay(...)` | 亡灵精通 | 召唤奥斯蒂并获得亡灵精通 | 普通/升级召唤和 Power 一致；Power 后续钩子独立登记 |
| `Nightmare.OnPlay(...)` | 夜魇 | 选择一张手牌；下一回合抽牌前生成 `3` 张清除负面附魔的副本并移除本实例 | 选择、次回合三张副本和 Power 移除一致；负面附魔清除尚无独立注入断言 |
| `Normality.ShouldPlay(...)` | 凡庸 | 本回合打出三张牌后禁止继续出牌 | 前三张可打、第四张拒绝，伤害一致 |
| `Reanimate.OnPlay(...)` | 死者苏生 | 大幅召唤奥斯蒂并支持复活 | 普通/升级当前与最大生命一致 |
| `Spur.OnPlay(...)` | 增生 | 先召唤，再治疗奥斯蒂且不超过模拟最大生命 | 普通/升级顺序与数值一致 |
| `TheBomb.OnPlay(...)` | 炸弹 | 创建独立炸弹实例并记录回合数与伤害 | 实例数量和动态伤害一致；倒计时/爆炸属于独立 Power 条目 |
| `ThrummingHatchet.BeforeHandDraw(...)` | 无休手斧 | 上回合打出后，在下回合抽牌前回到手牌 | 跨回合牌堆一致 |
| `VoidForm.OnPlay(...)` | 虚空形态 | 获得虚空形态后立即结束当前回合，搜索路线随即推进 | 真实全自动打出并严格在下一回合继续；Power 后续免费牌计数独立登记 |

### `CARD-ON-PLAY-BATCH-042`（24 项）

闭环：最终 Release DLL 在同一真实可见游戏 PID `46700` 中连续执行 `21` 个隔离场景，中间只返回主菜单。每个场景从同一初始状态分别运行生产模拟与原生出牌/原生选牌，逐字段比较玩家和全部敌人的生命、格挡、Power、能量、星能、Orb、四个牌堆、逐牌费用/升级/实例状态，以及 `CombatCardGeneration`、`CombatCardSelection`、`CombatTargets` 等 `6` 组战斗 RNG 计数；最后一组通过后退出游戏。

结果：最终 `21` 个 runId `b708591c829949a7aeb8e166d77a324c`、`6b6f1f1c07604b30b7c44ba034ef897a`、`0ac02a13a7f640a9860b1ee34221665a`、`254bc3baa4084dbdb7083216de60151c`、`cb232bf2aa5c4e5f8a86c3241dae37df`、`935ea72538ae420fad1aca2a2ca2eac0`、`8a394712a1d141279452d06113aa1f11`、`bc840fe7776b4724a610b9d39b1060e7`、`62b319d249e943f886970c8aee67186f`、`4c4f9b9d8be94a149bfe963e345db22c`、`665645e248494d27b0e142d23b8c31cb`、`a23179fa853d4e738cda4f196c846ec9`、`2a6b4277e1864750a4c1faed929a8e9d`、`9581e0b11b924e5bb5cff46cd8d90c83`、`70387de60b984f05a3bdfe29bd9da8c3`、`27c9d6309a3c40348319f2fedcb58ed1`、`d4ec65d269124a8d879f798af78a1dd8`、`bad0c03bae814f3785a7cd49a8f1bedb`、`b8dfc769e08743df8ebcd876f6a5a11d`、`a2aeffc8fe8a40ea8ef28fd45a05bcb0`、`29683490e49d417882c288ca3af5ab6b` 全部返回 `Passed` 且 `mainThread=true`。开发期万向斩与选牌夹具的预期常量有误，但当时模拟和原生快照已经一致；末日降临首次暴露测试快照没有过滤原生已移除的死亡敌人。修正规范化和夹具后，整批使用最终 DLL 从头复跑。另一次批量命令因 PowerShell 数组参数绑定错误而没有启动游戏、没有执行场景，不计为牌效失败。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Begone.OnPlay(...)` | 下去！ | 从手牌选一张，原位置变形为小怪打击；升级版生成升级牌 | 普通与升级变形、位置和逐牌状态一致 |
| `BouncingFlask.OnPlay(...)` | 弹跳药瓶 | 每段使用战斗目标 RNG 选择存活敌人并施加中毒 | 多敌随机目标、Power 与 RNG 计数一致 |
| `Brand.OnPlay(...)` | 烙印 | 无法格挡地失去生命，消耗一张手牌，再获得力量；无候选也继续结算 | 有选择及空选择两条路径一致 |
| `CaptureSpirit.OnPlay(...)` | 捕捉灵魂 | 造成伤害，再把固定数量灵魂随机插入抽牌堆 | 伤害、随机位置、牌堆与 RNG 一致 |
| `Charge.OnPlay(...)` | 冲锋！！ | 从抽牌堆选牌，原位置变形为小怪俯冲轰炸 | 普通与升级多选变形一致 |
| `Dredge.OnPlay(...)` | 清淤 | 从弃牌堆选择至多可放入手牌的指定数量 | 三张实例移动及手牌上限一致 |
| `DualWield.OnPlay(...)` | 双持 | 从手牌选攻击或能力，生成指定数量的完整实例副本 | 普通与升级数量、费用和实例状态一致 |
| `EchoingSlash.OnPlay(...)` | 回响斩击 | 攻击全部敌人；每击杀一名敌人，整轮攻击再重复一次 | 无击杀与中途击杀递归两条路径一致 |
| `EndOfDays.OnPlay(...)` | 末日降临 | 对全部敌人施加末日，生命不高于末日者死亡并结算死亡联动 | 普通/升级、多敌移除和死亡后状态一致 |
| `FranticEscape.OnPlay(...)` | 狂乱逃离 | 增加沙坑对玩家的目标计数，并令本牌本场费用增加 `1` | Power 私有状态与局部费用一致 |
| `Guards.OnPlay(...)` | 护驾！！！ | 可选择任意数量手牌，原位置变形为小怪牺牲 | `0..N` 可选分支、普通/升级变形一致 |
| `HiddenDaggers.OnPlay(...)` | 隐秘匕首 | 弃掉指定手牌后生成小刀；无候选时仍生成 | 有选择、升级和空选择路径一致 |
| `Inferno.OnPlay(...)` | 狱火 | 获得狱火并令其递增自伤计数增加一次 | Power 数量及 `SelfDamage` 私有状态一致 |
| `Omnislice.OnPlay(...)` | 万向斩 | 先伤害目标，再把实际伤害与溢出伤害之和施加给其他敌人 | 多敌生命、击杀和溢出传播一致 |
| `Outbreak.OnPlay(...)` | 毒性爆发 | 先给所有敌人上毒，再按顺序立即触发各自中毒 | 多敌 Power、生命和死亡顺序一致 |
| `PrimalForce.OnPlay(...)` | 原始力量 | 将手牌中全部可变形攻击原位置变成巨石 | 攻击筛选、普通/升级牌状态一致 |
| `Purity.OnPlay(...)` | 净化 | 可消耗 `0` 到指定数量的手牌 | 普通、升级和零选择路径一致 |
| `Scavenge.OnPlay(...)` | 内存清理 | 消耗一张手牌并获得下回合能量；无候选也获得能量 | 有选择和空选择的 Power/牌堆一致 |
| `Seance.OnPlay(...)` | 降灵 | 从抽牌堆选择指定数量并原位置变形为灵魂 | 选择、位置和替换牌状态一致 |
| `SecretTechnique.OnPlay(...)` | 秘密技法 | 从抽牌堆选择一张技能加入手牌 | 类型过滤和牌实例移动一致 |
| `SecretWeapon.OnPlay(...)` | 秘密武器 | 从抽牌堆选择一张攻击加入手牌 | 类型过滤和牌实例移动一致 |
| `Tracking.OnPlay(...)` | 跟踪 | 获得 `50` 层跟踪 | Power 数量一致；后续触发按独立钩子登记 |
| `Transfigure.OnPlay(...)` | 重构 | 选择一张手牌，本场费用增加 `1` 且基础重复次数增加 `1` | 费用、重复次数及跨回合指纹字段一致 |
| `Wish.OnPlay(...)` | 许愿 | 从抽牌堆选择任意一张牌加入手牌 | 任意类型选择及实例移动一致 |

### `CARD-ON-PLAY-BATCH-041`（24 项）

闭环：最终 Release DLL 在同一真实可见游戏 PID `39540` 中连续执行 `6` 个场景、`7` 条生产模拟/原生出牌与生命周期差分，中间只返回主菜单：两组 Power/资源组合，同步的即时与玩家回合末两条检查，以及内核加速与袖里乾坤、征召上前、暗影步三个隔离场景。逐字段比较生命、格挡、能量、星能、Orb、Power、四个牌堆、逐牌状态、局部费用、附魔和升级等级，最后一组通过后退出游戏。

结果：`runId b5dc4a2455fb4c0b82c244bc3e147374`、`f5bd97ad8cfa4db39c16587751254d98`、`c7057cab1aa04411b5e1f6c12b4f90cc`、`196f9ec87b4c45b9828646aabe2792b9`、`ec80992a3e9b467a808a368738c4b905`、`1b68f49e6a1e4204b945551206b071e6` 全部返回 `Passed` 且 `mainThread=true`。同步在闪电/冰霜两种球下同时得到 `2` 层同步与 `2` 集中，并在玩家回合末完整移除；征召上前把抽牌堆和弃牌堆中的两张君王之剑都移入手牌并锻造 `8`；内核加速先获得能量再把虚无放入弃牌堆；袖里乾坤生成 `3` 张小刀并把自身本场费用降低 `1`；暗影步弃掉剩余整手牌后获得对应 Power。开发期第一次同步请求使用旧式 Orb JSON，反序列化失败并超时；改正后差分发现模拟漏施加集中；增加生命周期检查后又发现无人脚手架实机回合末白名单缺少 `SynchronizePower`。修复两处代码后整批从头复测，开发期失败均不计入通过证据。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `ShadowStep.OnPlay(...)` | 暗影步 | 弃掉剩余手牌，再获得 `1` 层暗影步 | 三张手牌全部进入弃牌堆，Power 一致 |
| `Stampede.OnPlay(...)` | 惊逃 | 获得 `1` 层惊逃 | Power 数值一致；后续自动打出随机攻击独立登记 |
| `StoneArmor.OnPlay(...)` | 岩石铠甲 | 获得 `4` 层多层护甲 | `PlatingPower=4` |
| `Stratagem.OnPlay(...)` | 计策 | 获得 `1` 层计策 | Power 数值一致；洗牌选择独立登记 |
| `Subroutine.OnPlay(...)` | 子程序 | 获得 `1` 层子程序 | 置于组合末尾避免后续能力牌污染；触发逻辑独立登记 |
| `SummonForth.OnPlay(...)` | 征召上前 | 把所有不在手牌的君王之剑移入手牌，再锻造 `8` | 两张跨牌堆实例的位置和锻造后逐牌状态一致 |
| `Supercritical.OnPlay(...)` | 超临界态 | 支付费用后获得 `4` 能量 | 最终能量与原生一致 |
| `SwordSage.OnPlay(...)` | 剑圣 | 获得 `1` 层剑圣 | Power 数值一致；后续君王之剑重复次数联动独立登记 |
| `Synchronize.OnPlay(...)` | 同步 | 按不同充能球种类获得等量临时集中，本回合结束回收 | 两种球得到同步 `2`、集中 `2`；回收接入统一临时集中生命周期 |
| `Tactician.OnPlay(...)` | 战术大师 | 支付费用后获得 `1` 能量 | 最终能量与原生一致 |
| `Terraforming.OnPlay(...)` | 地形改造 | 获得 `7` 活力 | `VigorPower=7` |
| `TheSealedThrone.OnPlay(...)` | 封印王座 | 支付 `3` 星能后获得 `1` 层封印王座 | 置于组合末尾，星能与 Power 一致 |
| `TheSmith.OnPlay(...)` | 铸剑者 | 支付 `4` 星能后把现有君王之剑锻造 `30` | 星能和逐牌伤害状态一致 |
| `Thunder.OnPlay(...)` | 雷霆 | 获得 `8` 层雷霆 | Power 数值一致；后续闪电球联动独立登记 |
| `ToolsOfTheTrade.OnPlay(...)` | 必备工具 | 获得 `1` 层必备工具 | Power 数值一致；回合开始抽弃牌独立登记 |
| `TrashToTreasure.OnPlay(...)` | 化废为宝 | 获得 `1` 层化废为宝 | Power 数值一致；后续状态牌联动独立登记 |
| `Turbo.OnPlay(...)` | 内核加速 | 先获得 `2` 能量，再将一张虚无放入弃牌堆 | 能量、顺序与牌堆一致 |
| `Tyranny.OnPlay(...)` | 暴政 | 获得 `1` 层暴政 | Power 数值一致；回合开始效果独立登记 |
| `Unmovable.OnPlay(...)` | 坚定不移 | 获得 `1` 层坚定不移 | Power 数值一致；格挡联动独立登记 |
| `UpMySleeve.OnPlay(...)` | 袖里乾坤 | 生成 `3` 张小刀，并把自身本场费用降低 `1` | 小刀数量与弃牌堆中的本牌局部费用一致 |
| `Venerate.OnPlay(...)` | 崇拜 | 支付费用后获得 `2` 星能 | 最终星能与原生一致 |
| `Vicious.OnPlay(...)` | 凶恶 | 获得 `1` 层凶恶 | Power 数值一致；易伤联动独立登记 |
| `WellLaidPlans.OnPlay(...)` | 计划妥当 | 获得 `1` 层计划妥当 | Power 数值一致；回合末保留选择独立登记 |
| `Wisp.OnPlay(...)` | 鬼火 | 获得 `1` 能量并消耗 | 能量与牌去向一致 |

### `CARD-ON-PLAY-BATCH-040`（28 项）

闭环：最终 Release DLL 在同一真实可见游戏 PID `31288` 中连续执行 `6` 组生产模拟/原生出牌差分，中间只返回主菜单：两组各 `10` 张确定性 Power/技能牌，以及暗淡蓝点、资源与目标效果、追踪之刃、信号增强四个隔离场景。逐字段比较生命、格挡、能量、星能、Power、四个牌堆、逐牌状态、局部费用、附魔和升级等级，最后一组通过后退出游戏。首次第二组夹具误放 `11` 张手牌，因超过手牌上限而失败；拆为 `10+1` 后重新执行，失败 run 不计入通过证据。

结果：`runId 3b8d5baa3de84244bf9df72ac16c58e7`、`da2d28d62e274b67a3947e381c5ba487`、`8d788c3c70e94d54982b232e28d34eaf`、`bf343dcfd295471cb2758b0a5f64e9e2`、`7122d0a774f04ba59f71594c5fe6b888`、`4f02f317b31345959ac2abf238084d99` 全部返回 `Passed` 且 `mainThread=true`。延伸把当前 `7` 格挡写入 `7` 层下回合格挡；胜券在王支付 `5` 星能后获得 `9`，最终为 `9`；蛇咬施加 `7` 中毒，湮灭施加 `3` 层对应 Power；追踪之刃生成君王之剑并锻造 `7`。这些 RF 不支持的 `OnPlay` 风险在运行时标为求解器已补偿；各 Power 的后续生命周期仍按独立条目登记。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Automation.OnPlay(...)` | 自动化 | 获得 `1` 层自动化 | Power 数值一致；后续抽牌计数独立登记 |
| `ForbiddenGrimoire.OnPlay(...)` | 禁忌魔典 | 获得 `1` 层禁忌魔典 | Power 数值一致；战后奖励不属于已开始的战斗路线 |
| `Oblivion.OnPlay(...)` | 湮灭 | 给目标施加 `3` 层湮灭 | 与蛇咬同组目标差分通过 |
| `Orbit.OnPlay(...)` | 环绕轨道 | 获得 `1` 层环绕轨道 | Power 数值一致；后续耗能计数独立登记 |
| `Outmaneuver.OnPlay(...)` | 抢占先机 | 获得下回合 `2` 能量 | `EnergyNextTurnPower=2` |
| `Pagestorm.OnPlay(...)` | 书页风暴 | 获得 `1` 层书页风暴 | Power 数值一致；后续虚无牌抽取独立登记 |
| `PaleBlueDot.OnPlay(...)` | 暗淡蓝点 | 获得 `1` 层暗淡蓝点 | 隔离场景实机通过；后续五牌计数独立登记 |
| `Panache.OnPlay(...)` | 神气制胜 | 获得 `10` 层神气制胜 | 置于批量场景末尾，未被测试牌污染计数 |
| `Parry.OnPlay(...)` | 招架 | 获得 `10` 层招架 | Power 数值一致；君王之剑格挡修正独立登记 |
| `PhantomBlades.OnPlay(...)` | 幻影之刃 | 获得 `9` 层幻影之刃 | Power 数值一致；小刀后续效果已有独立钩子条目 |
| `PillarOfCreation.OnPlay(...)` | 创世之柱 | 获得 `2` 层创世之柱 | Power 数值一致；生成牌后格挡独立登记 |
| `Production.OnPlay(...)` | 生产制造 | 获得 `2` 能量 | 与支付顺序后的最终能量一致 |
| `Prolong.OnPlay(...)` | 延伸 | 把当前格挡量转为下回合格挡 | 当前 `7` 格挡得到 `BlockNextTurnPower=7` |
| `Pyre.OnPlay(...)` | 薪火之源 | 获得 `1` 层薪火之源 | Power 数值一致；最大能量修正独立登记 |
| `ReaperForm.OnPlay(...)` | 死神形态 | 获得 `1` 层死神形态 | Power 数值一致；攻击施加灾厄独立登记 |
| `RollingBoulder.OnPlay(...)` | 滚石 | 获得初始 `5` 层滚石 | Power 数值一致；回合开始伤害与成长独立登记 |
| `RoyalGamble.OnPlay(...)` | 胜券在王 | 支付 `5` 星能后获得 `9` 星能 | 星能 `5 → 0 → 9` |
| `Royalties.OnPlay(...)` | 王国资产 | 获得 `30` 层王国资产 | Power 数值一致；战后金币不属于已开始的战斗路线 |
| `SeekingEdge.OnPlay(...)` | 追踪之刃 | 获得 `1` 层追踪之刃，生成缺失的君王之剑并锻造 `7` | Power、牌堆与逐牌伤害状态一致 |
| `Shadowmeld.OnPlay(...)` | 融入暗影 | 获得 `1` 层融入暗影 | Power 数值一致；回合末生命周期已另测 |
| `Shroud.OnPlay(...)` | 厄运之衣 | 获得 `3` 层厄运之衣 | Power 数值一致；灾厄格挡修正独立登记 |
| `SignalBoost.OnPlay(...)` | 信号增强 | 获得 `1` 层信号增强 | 隔离场景实机通过；下一张能力重复独立登记 |
| `SleightOfFlesh.OnPlay(...)` | 血肉戏法 | 获得 `9` 层血肉戏法 | Power 数值一致；召唤联动独立登记 |
| `Smokestack.OnPlay(...)` | 烟囱 | 获得 `5` 层烟囱 | Power 数值一致；后续格挡触发独立登记 |
| `Snakebite.OnPlay(...)` | 蛇咬 | 给目标施加 `7` 中毒 | 目标 Power 与原生一致 |
| `SpectrumShift.OnPlay(...)` | 光谱偏移 | 获得 `1` 层光谱偏移 | Power 数值一致；充能球联动独立登记 |
| `Speedster.OnPlay(...)` | 速行者 | 获得 `2` 层速行者 | Power 数值一致；后续触发独立登记 |
| `SpiritOfAsh.OnPlay(...)` | 灰烬之灵 | 获得 `4` 层灰烬之灵 | Power 数值一致；消耗后格挡独立登记 |

### `CARD-ON-PLAY-BATCH-039`（23 项）

闭环：最终 Release DLL 先在同一真实可见游戏 PID `46908` 中连续执行 `11` 组生产模拟/原生出牌差分，中间只返回主菜单；再以 PID `3704` 补充暴露移除两层人工制品和迷雾双敌全体目标两组隔离差分，共 `13` 组。差分快照新增逐牌局部费用、附魔与升级等级，连同生命、格挡、能量、星能、Power、四个牌堆和既有逐牌状态一起严格比较；每个进程最后一组通过后才退出游戏。

结果：上述 `11` 个 runId 加 `da45c7ac994b49129b9dfd3fdeb7035b`、`4e45568da9594103847e186bc74051f3` 全部返回 `Passed` 且 `mainThread=true`。黑暗镣铐与弱化之触合计临时减少 `17` 力量，敌方回合结束后只恢复这 `17` 点并保留怪物原有 `7` 力量；热修复在玩家回合结束后同时移除临时 Power 与 `2` 点集中力；暴露完整移除 `2` 层人工制品；迷雾同时命中盛碗虫和潮湿邪教徒。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Apotheosis.OnPlay(...)` | 神化 | 除自身外，升级五个战斗牌堆中所有可升级牌 | 全牌堆逐牌升级等级一致 |
| `Arsenal.OnPlay(...)` | 武器库 | 获得 `1` 层武器库 | 实机通过；后续生成牌获得力量独立登记 |
| `BladeDance.OnPlay(...)` | 刀刃之舞 | 生成 `3` 张小刀到手牌 | 与刀扇合计生成 `7` 张小刀 |
| `BladeOfInk.OnPlay(...)` | 墨之刃 | 生成 `2` 张小刀并分别施加 `1` 层墨染 | 两张实例的牌堆、附魔 ID 与层数均一致 |
| `Bloodletting.OnPlay(...)` | 放血 | 受到 `3` 点不可格挡、未受 Power 调整的自伤，再获得 `2` 能量 | 生命 `50 → 47`，资源顺序一致 |
| `DarkShackles.OnPlay(...)` | 黑暗镣铐 | 本回合使目标失去 `9` 力量，敌方回合结束恢复 | 应用与恢复两条隔离路径均通过 |
| `DeadlyPoison.OnPlay(...)` | 致命毒药 | 给目标施加 `5` 层中毒 | 与迷雾累计后为 `9` 层 |
| `Debris.OnPlay(...)` | 碎屑 | 牌面效果为空；仍支付费用并因消耗进入消耗堆 | 空效果、资源与牌去向一致 |
| `DevourLife.OnPlay(...)` | 吞噬生命 | 获得 `1` 层吞噬生命 | 实机通过；后续灵魂牌召唤独立登记 |
| `DoubleEnergy.OnPlay(...)` | 双倍能量 | 支付本牌费用后，获得等于当前剩余能量的能量 | 与原生支付/获得顺序一致 |
| `EnfeeblingTouch.OnPlay(...)` | 弱化之触 | 本回合使目标失去 `8` 力量，敌方回合结束恢复 | 应用与恢复两条隔离路径均通过 |
| `Enlightenment.OnPlay(...)` | 开悟 | 剩余手牌费用至多为 `1`；升级版持续整场战斗 | 普通版局部费用一致；升级版过回合仍为 `1` |
| `Entropy.OnPlay(...)` | 熵 | 获得 `1` 层熵 | 实机通过；后续随机转化独立登记 |
| `Expose.OnPlay(...)` | 暴露 | 清除目标全部格挡与完整人工制品，再施加 `2` 层易伤 | 原生顺序与最终状态一致 |
| `FanOfKnives.OnPlay(...)` | 刀扇 | 获得 `1` 层刀扇，再生成 `4` 张小刀 | Power 与生成顺序一致 |
| `ForegoneConclusion.OnPlay(...)` | 既定事项 | 获得 `2` 层既定事项 | 实机通过；后续抽牌堆选择独立登记 |
| `Haze.OnPlay(...)` | 迷雾 | 先给所有可命中敌人施加 `4` 中毒，再施加 `1` 虚弱 | 全体目标与施加顺序一致 |
| `HiddenCache.OnPlay(...)` | 隐秘藏品 | 获得 `1` 星能和 `3` 层下回合星能 | 资源与 Power 一致 |
| `Hotfix.OnPlay(...)` | 热修复 | 本回合获得 `2` 集中力，玩家回合结束完整回收 | 应用与回收隔离差分通过 |
| `Invoke.OnPlay(...)` | 唤起 | 获得下回合 `2` 点召唤与 `2` 点能量 | 两个 Power 均一致 |
| `OneTwoPunch.OnPlay(...)` | 连环拳 | 获得 `1` 层连环拳 | 实机通过；后续攻击重复独立登记 |
| `PrepTime.OnPlay(...)` | 准备时间 | 获得 `4` 层准备时间 | 实机通过；后续回合开始活力独立登记 |
| `StormOfSteel.OnPlay(...)` | 钢铁风暴 | 弃掉全部剩余手牌并生成等量小刀；升级版生成升级小刀 | `3` 张弃牌与 `3` 张升级小刀逐牌一致 |

### `CARD-ON-PLAY-BATCH-038`（25 项）

闭环：最终 Release DLL 在同一真实可见游戏 PID `43080` 中连续执行 `7` 组生产模拟/原生出牌差分，中间只返回主菜单：两组各 `10` 张能力/技能牌，以及死亡之舞、谋划专家、群蛇形态、雷暴、无处可逃五个隔离场景。每组逐字段比较生命、格挡、能量、星能、Power、四个牌堆与逐牌状态；时候未到额外把初始生命设为 `50` 并独立断言最终生命。最后一组通过后才退出游戏。

结果：`runId 31af0d1412ca4d8aa2a3902c6b93c021`、`7642ba141b5d4666943166178fb1ca2f`、`7ab57990fa7845d3addd770432dda7fc`、`7cf4786d39074c488bd4bd7995afb6ed`、`d1dbb711b0394ec3826660831578fae7`、`22d2d6617e3f41a28f5638c9bd75f3cb`、`38a99c4422fb4694a183e48f7f98e79f` 全部返回 `Passed` 且 `mainThread=true`。时候未到把玩家生命从 `50` 恢复至 `60`；中子护盾消耗 `5` 星能并施加 `8` 层多层护甲；被遗忘的仪式、燃料和冷光合计获得 `6` 能量；已有 `20` 层灾厄时，无处可逃再施加 `20`，最终为 `40`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Calamity.OnPlay(...)` | 劫难 | 获得 `1` 层劫难 | 实机通过；后续随机生成攻击牌独立登记 |
| `ChildOfTheStars.OnPlay(...)` | 群星之子 | 获得 `2` 层群星之子 | 实机通过；后续花费星能获得格挡独立登记 |
| `DanseMacabre.OnPlay(...)` | 死亡之舞 | 获得 `4` 层死亡之舞 | 隔离场景实机通过 |
| `Demesne.OnPlay(...)` | 领域 | 获得 `1` 层领域 | Power 与资源一致；后续抽牌和最大能量修正独立登记 |
| `EternalArmor.OnPlay(...)` | 永恒铠甲 | 获得 `9` 层多层护甲 | 实机通过 |
| `Fasten.OnPlay(...)` | 勒紧 | 获得 `4` 层勒紧 | 实机通过 |
| `FeedingFrenzy.OnPlay(...)` | 疯狂进食 | 同时获得 `5` 力量与 `5` 层疯狂进食，供回合结束回收 | 两个 Power 均一致 |
| `ForgottenRitual.OnPlay(...)` | 被遗忘的仪式 | 支付费用后获得 `3` 能量并消耗 | 能量与牌去向一致 |
| `Haunt.OnPlay(...)` | 纠缠 | 获得 `7` 层纠缠 | 实机通过 |
| `Hellraiser.OnPlay(...)` | 地狱狂徒 | 获得 `1` 层地狱狂徒 | 实机通过；后续抽到打击自动打出独立登记 |
| `Iteration.OnPlay(...)` | 迭代 | 获得 `2` 层迭代 | 实机通过 |
| `MasterPlanner.OnPlay(...)` | 谋划专家 | 获得 `1` 层谋划专家 | 隔离场景实机通过；后续技能获得狡黠独立登记 |
| `MonarchsGaze.OnPlay(...)` | 王之凝视 | 获得 `1` 层王之凝视 | 实机通过 |
| `NeutronAegis.OnPlay(...)` | 中子护盾 | 支付 `5` 星能并获得 `8` 层多层护甲 | 星能和 Power 一致 |
| `Nostalgia.OnPlay(...)` | 怀旧 | 获得 `1` 层怀旧 | 实机通过；后续首张攻击/技能回到抽牌堆顶独立登记 |
| `NotYet.OnPlay(...)` | 时候未到 | 回复 `10` 生命并消耗 | `50 → 60`，显式生命断言通过 |
| `Rage.OnPlay(...)` | 狂怒 | 获得 `3` 层狂怒 | 实机通过 |
| `Rupture.OnPlay(...)` | 撕裂 | 获得 `1` 层撕裂 | 实机通过 |
| `SerpentForm.OnPlay(...)` | 群蛇形态 | 获得 `4` 层群蛇形态 | 隔离场景实机通过；后续随机目标伤害独立登记 |
| `SentryMode.OnPlay(...)` | 哨卫模式 | 获得 `1` 层哨卫模式 | 实机通过；抽牌前生成牌独立登记 |
| `Storm.OnPlay(...)` | 雷暴 | 获得 `1` 层雷暴 | 隔离场景实机通过；后续能力牌充能闪电独立登记 |
| `Fuel.OnPlay(...)` | 燃料 | 获得 `1` 能量并消耗 | 能量与牌去向一致 |
| `Genesis.OnPlay(...)` | 创世纪 | 获得 `2` 层创世纪 | 实机通过 |
| `Luminesce.OnPlay(...)` | 冷光 | 获得 `2` 能量并消耗 | 能量与牌去向一致 |
| `NoEscape.OnPlay(...)` | 无处可逃 | 新增 `10 + 5 × floor(目标当前灾厄/10)` 层灾厄 | 已有 `20` 时新增 `20`，最终 `40` |

### `CARD-ON-PLAY-BATCH-037`（26 项）

闭环：最终 Release DLL 在同一真实可见游戏 PID `39500` 中连续执行 `6` 组生产模拟/原生出牌差分，中间只返回主菜单：两组各 `10` 张 Power/技能牌、`4` 张资源与条件牌、子弹时间零费化、野性中途施加计数和杂耍中途施加计数。每组逐字段比较生命、格挡、能量、星能、Power、四个牌堆、逐牌状态、锻造牌伤害和敌方状态；最后一组结束后才退出游戏。

结果：`runId 855bb63be63542d3b50b9827edf13c1a`、`d4cc4398e23842969a89ea8a5f969389`、`9f975159b50948bda8e3818435476459`、`a8870038dfe64637b81d8b7c89a9be3c`、`f531e46e7cc44342add0a044dd7b536c`、`1be531d71546498b9658f4e5cc9dce12` 全部返回 `Passed` 且 `mainThread=true`。子弹时间在能量归零后仍可打出势不可当和爆发；咕嘟冒泡只在已有中毒时把 `1` 层增至 `10`；征服者生成并锻造君王之剑。先打两张愤怒再获得野性时，第三张愤怒不会错误回到手牌，最终弃牌堆为 `6` 张；同样顺序获得杂耍时，第三张攻击额外复制到手牌，最终弃牌堆 `6` 张、手牌 `1` 张。两条私有历史计数均与原版一致。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Alignment.OnPlay(...)` | 星位序列 | 支付 `2` 星能后获得 `2` 能量 | 能量与星能均一致 |
| `BlackHole.OnPlay(...)` | 黑洞 | 获得 `3` 层黑洞 | 实机通过；后续星能触发独立登记 |
| `BorrowedTime.OnPlay(...)` | 预借时间 | 获得 `4` 能量并获得 `1` 层预借时间 | 资源与 Power 一致 |
| `BubbleBubble.OnPlay(...)` | 咕嘟冒泡 | 目标已有中毒时再施加 `9` 层，否则不施加 | `1 → 10` 条件路径通过 |
| `BulletTime.OnPlay(...)` | 子弹时间 | 剩余非 X 手牌本回合变为 `0` 费，并获得无法抽牌 | `0` 能量继续打出两张牌 |
| `Burst.OnPlay(...)` | 爆发 | 获得 `1` 层爆发 | 实机通过 |
| `Calcify.OnPlay(...)` | 钙化 | 获得 `4` 层钙化 | 实机通过 |
| `CallOfTheVoid.OnPlay(...)` | 虚空之唤 | 获得 `1` 层虚空之唤 | `OnPlay` 通过；后续随机生成独立登记 |
| `Conqueror.OnPlay(...)` | 征服者 | 先锻造 `3`，再给目标施加 `1` 层征服者 | 君王之剑与目标 Power 一致 |
| `Convergence.OnPlay(...)` | 汇流 | 获得保留手牌、下回合 `1` 能量和下回合 `1` 星能 | 三个 Power 均一致 |
| `Coolant.OnPlay(...)` | 冷却剂 | 获得 `2` 层冷却剂 | 实机通过 |
| `CorrosiveWave.OnPlay(...)` | 腐蚀波 | 获得 `2` 层腐蚀波 | `OnPlay` 通过；抽牌与回收已另测 |
| `Countdown.OnPlay(...)` | 倒数计时 | 获得 `6` 层倒数计时 | 实机通过 |
| `Cruelty.OnPlay(...)` | 残酷 | 获得 `25` 层残酷 | 实机通过 |
| `Deathbringer.OnPlay(...)` | 死亡使者 | 所有可命中敌人依次获得 `21` 层灾厄和 `1` 层虚弱 | 目标与施加顺序一致 |
| `Feral.OnPlay(...)` | 野性 | 获得 `1` 层野性，并从本回合已有零费攻击初始化私有计数 | 两张愤怒后中途获得的路径通过 |
| `Hailstorm.OnPlay(...)` | 冰雹风暴 | 获得 `6` 层冰雹风暴 | 实机通过 |
| `HelloWorld.OnPlay(...)` | 你好世界 | 获得 `1` 层你好世界 | `OnPlay` 通过；后续随机生成独立登记 |
| `InfiniteBlades.OnPlay(...)` | 无尽刀刃 | 获得 `1` 层无尽刀刃 | `OnPlay` 通过；回合开始生成小刀独立登记 |
| `Juggernaut.OnPlay(...)` | 势不可当 | 获得 `6` 层势不可当 | 实机通过 |
| `Juggling.OnPlay(...)` | 杂耍 | 获得 `1` 层杂耍，并从本回合已有攻击初始化私有计数 | 第三张愤怒额外进入手牌 |
| `Lethality.OnPlay(...)` | 致死性 | 获得 `50` 层致死性 | Power 一致；首张攻击计数已另测 |
| `Loop.OnPlay(...)` | 循环 | 获得 `1` 层循环 | 实机通过 |
| `MachineLearning.OnPlay(...)` | 机器学习 | 获得 `1` 层机器学习 | Power 一致；抽牌生命周期已另测 |
| `Mayhem.OnPlay(...)` | 乱战 | 获得 `1` 层乱战 | `OnPlay` 通过；自动随机出牌独立登记 |
| `NoxiousFumes.OnPlay(...)` | 毒雾 | 获得 `2` 层毒雾 | Power 一致；回合结束上毒已另测 |

### `CARD-ON-PLAY-BATCH-036`（19 项）

闭环：最终 Release DLL 在真实可见游戏 PID `42280` 中连续执行 `3` 组生产模拟/原生出牌差分：`10` 张能力/技能牌、`9` 张持续能力牌，以及预判的玩家回合结束恢复。每组逐字段比较生命、格挡、能量、Power、充能球容量与球体、四个牌堆和逐牌状态。创造性 AI 本批只验证确定性的 `OnPlay`；后续随机生成仍是单独的 `BeforeHandDraw` 待适配项。

结果：`runId 681cb14be8694f7aa8441e17246f35db`、`dff5e622f8594b0098b55971f94b7407`、`68218349022c4d91b08d03d97ceb8d37` 全部返回 `Passed` 且 `mainThread=true`。扩容把缺陷机器人的充能球槽位从 `3` 增至 `5`；预判施加 `2` 层及 `2` 敏捷后，在玩家回合结束同时移除；其余牌的 Power 数量、资源和牌去向均与实机一致。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Accelerant.OnPlay(...)` | 触媒 | 获得 `1` 层触媒 | 实机通过 |
| `Accuracy.OnPlay(...)` | 精准 | 获得 `4` 层精准 | 实机通过 |
| `Afterimage.OnPlay(...)` | 余像 | 获得 `1` 层余像，后续出牌获得格挡 | Power 与后续格挡一致 |
| `Aggression.OnPlay(...)` | 好勇斗狠 | 获得 `1` 层好勇斗狠 | 实机通过 |
| `Anticipate.OnPlay(...)` | 预判 | 本回合获得 `2` 敏捷，回合结束同时移除预判和敏捷 | 施加与回收均通过 |
| `Barricade.OnPlay(...)` | 壁垒 | 获得 `1` 层壁垒 | 实机通过 |
| `BiasedCognition.OnPlay(...)` | 偏差认知 | 获得 `5` 集中与 `1` 层偏差认知 | 两个 Power 一致 |
| `Buffer.OnPlay(...)` | 缓冲 | 获得 `1` 层缓冲 | 实机通过 |
| `Caltrops.OnPlay(...)` | 铁蒺藜 | 获得 `3` 荆棘 | 实机通过 |
| `Capacitor.OnPlay(...)` | 扩容 | 增加 `2` 个充能球槽位 | 槽位 `3→5` |
| `Corruption.OnPlay(...)` | 腐化 | 获得 `1` 层腐化 | Power 一致；费用和去向钩子已另测 |
| `CreativeAi.OnPlay(...)` | 创造性AI | 获得 `1` 层创造性 AI | `OnPlay` 实机通过；后续随机生成不借此标记 |
| `DarkEmbrace.OnPlay(...)` | 黑暗之拥 | 获得 `1` 层黑暗之拥 | 实机通过 |
| `Defragment.OnPlay(...)` | 碎片整理 | 获得 `1` 集中 | 实机通过 |
| `DemonForm.OnPlay(...)` | 恶魔形态 | 获得 `3` 层恶魔形态 | 实机通过 |
| `EchoForm.OnPlay(...)` | 回响形态 | 获得 `1` 层回响形态 | 实机通过 |
| `Envenom.OnPlay(...)` | 涂毒 | 获得 `1` 层涂毒 | 实机通过 |
| `FeelNoPain.OnPlay(...)` | 无惧疼痛 | 获得 `3` 层无惧疼痛 | 实机通过 |
| `Furnace.OnPlay(...)` | 熔炉 | 获得 `5` 层熔炉 | 实机通过 |

### `CARD-ON-PLAY-BATCH-035`（20 项）

闭环：最终 Release DLL 在真实可见游戏 PID `15664` 中连续执行 `5` 组生产模拟/原生出牌差分：`8` 张自我状态牌、`8` 张目标减益牌、暴涨槽位、淬炼刀刃/共鸣/侧步，以及尖啸的敌方回合结束恢复。每组逐字段比较生命、格挡、能量、星能、Power、充能球容量与球体、四个牌堆、逐牌状态和伤害。搜刮需要手牌选择，本批没有把它计入通过项，留给选择牌专批。

结果：最终 `runId 436668e0d382440a80c596603ebf6ace`、`bf9ea527f9874eb096d91c9c76e39b0b`、`8decfa5ed85a4c5aa6c06380b15d14e6`、`d29edfde00ca41498a77b8d205a2cf6f`、`d82d75269663451ea638b68db5db1238` 全部返回 `Passed` 且 `mainThread=true`。暴涨把缺陷机器人的充能球槽位从 `3` 减至 `2`；淬炼刀刃生成并锻造出 `18` 伤害的君王之剑；共鸣正确消耗 `2` 星能；尖啸施加 `6` 层后在敌方回合结束移除并恢复力量。第 031 批跨回合资源夹具又以 `runId 07f7575c78cb4ab88264cc5aa2c475e4` 完整回归通过，确认下回合能量改用原生 Power 后仍正确消费和移除。开发期闭环还定位并修复了萎靡在模拟牌副本上读取实机 `CombatState` 的空引用，以及真实回合结束测试白名单漏掉尖啸的问题。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Abrasive.OnPlay(...)` | 磨蚀 | 获得 `1` 敏捷与 `4` 荆棘 | 两个 Power 均与实机一致 |
| `Apparition.OnPlay(...)` | 灵体 | 获得 `1` 层无实体并消耗 | Power 与牌去向一致 |
| `BulkUp.OnPlay(...)` | 暴涨 | 失去 `1` 个充能球槽位，获得 `2` 力量与 `2` 敏捷 | 槽位 `3→2`，Power 一致 |
| `CrimsonMantle.OnPlay(...)` | 绯红披风 | 获得 `7` 层并递增该 Power 的自伤计数 | 数量和内部状态一致 |
| `Dominate.OnPlay(...)` | 主宰 | 先施加易伤，再按目标当前易伤获得力量 | 施加顺序与最终力量一致 |
| `Footwork.OnPlay(...)` | 灵动步法 | 获得 `2` 敏捷 | 实机通过 |
| `Friendship.OnPlay(...)` | 友谊 | 失去 `2` 力量并获得 `1` 层友谊 | 两个 Power 均一致 |
| `Inflame.OnPlay(...)` | 燃烧 | 获得 `2` 力量 | 实机通过 |
| `KnowThyPlace.OnPlay(...)` | 何人僭越 | 施加 `1` 层虚弱与 `1` 层易伤 | 两种减益一致 |
| `Malaise.OnPlay(...)` | 萎靡 | 以模拟战斗钩子结算本次 X 值，等量降低力量并施加虚弱 | 剩余 `4` 能量时施加 `4`，无空引用 |
| `PiercingWail.OnPlay(...)` | 尖啸 | 所有敌人暂时失去 `6` 力量，敌方回合结束恢复 | 施加、Power 移除和力量恢复均通过 |
| `Prowess.OnPlay(...)` | 非凡技艺 | 获得 `1` 力量与 `1` 敏捷 | 实机通过 |
| `Putrefy.OnPlay(...)` | 腐败 | 对目标施加 `2` 层虚弱与 `2` 层易伤 | 实机通过 |
| `RefineBlade.OnPlay(...)` | 淬炼刀刃 | 锻造 `8` 并获得下回合 `1` 能量 | 生成 `18` 伤害君王之剑，Power 一致 |
| `Resonance.OnPlay(...)` | 共鸣 | 消耗 `2` 星能，自身获得力量且所有敌人失去 `1` 力量 | 星能与双方 Power 一致 |
| `SharedFate.OnPlay(...)` | 命运同担 | 玩家和目标分别失去 `2` 力量 | 双方目标一致 |
| `Shockwave.OnPlay(...)` | 震荡波 | 所有敌人获得 `3` 层虚弱与 `3` 层易伤 | 使用共享 `Power` 动态值，实机通过 |
| `Sidestep.OnPlay(...)` | 侧步 | 获得下回合 `1` 能量 | 以原生 Power 保存并正确累计 |
| `Tremble.OnPlay(...)` | 战栗 | 施加 `3` 层易伤 | 实机通过 |
| `WraithForm.OnPlay(...)` | 幽魂形态 | 获得 `2` 层无实体与 `1` 层幽魂形态 | 两个 Power 均一致 |

### `MONSTER-MOVES-BATCH-034`（22 项）

闭环：最终 Release DLL 在真实可见游戏中执行 `13` 组 Power 差分。精准、勒紧、腐化、吊杀、难以杀灭、领袖气质、致死性、一心化万、幻影之刃、翱翔、跟踪和钙化均从同一实机状态分别运行生产模拟与原生出牌/怪物行动，逐字段比较生命、格挡、能量、Power、四个牌堆、逐牌状态和 Orb；进程只在 DLL 更新后重启，其余请求均返回主菜单后复用。为你而死另以 `20 HP` 与 `1 HP` 奥斯蒂验证单段正常承伤和致死溢出，再用幽灵船践踏验证奥斯蒂在多段攻击中死亡后的后续段，并把奥斯蒂生命、可选中状态、是否仍在战斗及死后 Power 纳入快照。

结果：完整 `13` 组均返回 `Passed` 且 `mainThread=true`；历史计数逻辑拆至独立 partial 文件并重新部署后的四组代表性回归也全部通过。最终 `runId efd0c6227a4d487db98d7df120013e1a` 同时通过三条为你而死路径：`20 HP` 奥斯蒂承受 `9` 点后剩 `11`；`1 HP` 奥斯蒂死亡并把 `8` 点溢出交给玩家；幽灵船多段践踏中奥斯蒂首段死亡后，后续段继续伤害玩家。死亡奥斯蒂仍留在战斗中、不可选中且保留该 Power。开发期 `efdf91ed0a7644f382ffd2a95fb8514e` 的模拟与真实快照已一致，但断言把 `20 - 9` 误写为 `12`；另一次请求暴露脚本直接覆写请求文件时与游戏读取竞争，改成同目录临时文件原子发布后连续复用通过。完整 runId 保存在测试证据清单与日志中。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `AccuracyPower.ModifyDamageAdditive(...)` | 精准：伤害加算 | 持有者的小刀有力量攻击增加等于层数的伤害 | 小刀 `4 + 3 = 7`，实机通过 |
| `CalcifyPower.ModifyDamageAdditive(...)` | 钙化：奥斯蒂伤害加算 | 奥斯蒂的有力量攻击增加持有者钙化层数 | 戳刺 `6 + 3 = 9`，实机通过 |
| `CorruptionPower.TryModifyEnergyCostInCombatLate(...)` | 腐化：技能费用 | 持有者技能在晚费用阶段变为 `0` 费 | `0` 能量仍可打出防御，实机通过 |
| `CorruptionPower.ModifyCardPlayResultLocation(...)` | 腐化：技能去向 | 打出的技能结算后进入消耗堆 | 防御进入消耗堆，实机通过 |
| `CuriousPower.TryModifyEnergyCostInCombat(...)` | 好奇：能力费用 | 正费用能力牌按层数减费且不低于 `0` | `0` 能量可打出受减费的燃烧，实机通过 |
| `DieForYouPower.ModifyUnblockedDamageTarget(...)` | 为你而死：转移未格挡伤害 | 存活奥斯蒂先承受玩家将受到的有力量攻击，超过其生命的部分回到玩家；多段攻击后续段不得再次转给已死亡的模拟态奥斯蒂 | 单段正常承伤、致死溢出和多段中途死亡三条路径均通过；后者由求解器补偿实机对象与模拟态的时间差 |
| `DieForYouPower.ShouldAllowHitting(...)` | 为你而死：是否可选中 | 奥斯蒂只有存活时可被命中 | 死亡快照中 `hittable=false`，实机通过 |
| `DieForYouPower.ShouldCreatureBeRemovedFromCombatAfterDeath(...)` | 为你而死：死后保留 | 死亡的奥斯蒂不从战斗移除，供后续召唤复活 | `0 HP` 后仍存在于友方战斗列表 |
| `DieForYouPower.ShouldPowerBeRemovedAfterOwnerDeath()` | 为你而死：Power 死后保留 | 奥斯蒂死亡时该 Power 不移除 | 死亡后仍为 `DIE_FOR_YOU_POWER:1` |
| `FastenPower.ModifyBlockAdditive(...)` | 勒紧：格挡加算 | 防御标签牌和所属者怪物行动的有力量格挡增加层数 | 与不动组合，两张防御最终 `21` 格挡 |
| `HangPower.ModifyDamageMultiplicative(...)` | 吊杀：伤害倍率 | 吊杀对持有该减益的目标按当前层数乘算伤害 | 已有 `2` 层时造成 `20`，随后层数增至 `4` |
| `HardToKillPower.ModifyDamageCap(...)` | 难以杀灭：伤害上限 | 持有者每次受到的伤害不超过层数 | `6` 点打击被压至 `3`，实机通过 |
| `LeadershipPower.ModifyDamageAdditive(...)` | 领袖气质：同阵营伤害加算 | 同阵营其他生物的有力量攻击增加层数 | 树枝史莱姆（小）由 `4` 增至 `7` |
| `LethalityPower.ModifyDamageMultiplicative(...)` | 致死性：首张攻击倍率 | 持有者每回合只有第一张攻击获得百分比增伤 | `100%` 时两张打击合计 `12 + 6 = 18`，预测计数进入状态指纹 |
| `OneForAllPower.ModifyDamageAdditive(...)` | 一心化万：零能量攻击加算 | 非 X、有卡牌来源且实际未花能量的攻击增加层数 | 愤怒 `6 + 3 = 9`，复制仍正确进入弃牌堆 |
| `PhantomBladesPower.AfterCardEnteredCombat(...)` | 幻影之刃：新小刀保留 | 新进入战斗的小刀获得保留 | 模拟牌状态与实机一致 |
| `PhantomBladesPower.ModifyDamageAdditive(...)` | 幻影之刃：首张小刀加伤 | 每回合仅第一张小刀增加层数伤害 | 两张小刀合计 `7 + 4 = 11`，预测计数进入状态指纹 |
| `PhantomBladesPower.AfterApplied(...)` | 幻影之刃：已有小刀保留 | Power 生效时所有现有小刀获得保留 | 已有与新生成路径统一规范化 |
| `SoarPower.ModifyDamageMultiplicative(...)` | 翱翔：受伤倍率 | 持有者受到的有力量攻击乘以动态减伤值 `50%` | `6` 点打击降至 `3`，实机通过 |
| `TrackingPower.ModifyDamageMultiplicative(...)` | 跟踪：虚弱目标增伤 | 持有者或其宠物用卡牌攻击虚弱目标时按层数提高伤害 | `50%` 使打击从 `6` 增至 `9` |
| `UnmovablePower.ModifyBlockMultiplicative(...)` | 不动：前 N 次格挡翻倍 | 每回合前 N 次卡牌/怪物行动格挡翻倍 | `1` 层时第一张防御翻倍、第二张正常，实机通过 |

说明：本批把怪物攻击从手工拆分的伤害/格挡结算改为 RF `CombatPredictionSimulator.Damage` 的完整路径，保留行动特有的动态基础伤害与后处理；因此原生伤害修正顺序、奥斯蒂转移、溢出和受伤后钩子不再重复实现。`HardToKillPower.AfterModifyingDamageAmount` 只闪烁图标，静态登记为纯表现。掩护、护卫、拦截和肉盾的唯一来源是原版标记为 `MultiplayerOnly` 的“拦截”和“肉盾”，对应 `11` 个尚未分类钩子静态登记为单人范围不适用；这 `12` 个排除项不计入上表 `22` 项实机闭环。

### `MONSTER-MOVES-BATCH-033`（20 项）

闭环：最终 Release DLL 先用独立可见进程验证巨像当前生效和敌方行动前移除两条路径，再在同一真实可见游戏 `pid 5084` 内连续投递污染、调制、腐蚀波、消亡、瓦解、复合生命周期、Orb/天罚、柔嫩和杂耍九场差分，最后一场才退出。共执行 `16` 条生产一步模拟/原生回调差分，逐字段比较生命、格挡、能量、Power、Orb 队列、四个牌堆和逐牌状态；天罚、柔嫩和杂耍的私有计数还同步进入模拟状态指纹。

结果：最终 `runId 3ecba632cafa4e7fab34234c02d62b7e`、`210806f2f52c4965953ffd6d93c2e785`、`bfd24de28b13479b8d51ae426d771888`、`2780c33002524d798a44b2d354efa2b8`、`ede74e0e6d1544e1a587d9eb81ce9373`、`86afde15819641fcbf1c4ad0c09d2414`、`d24e10850df643cd9332c0d8ae479a68`、`29615a9a97a14ac7a0bacc21f6559113`、`1d30e09a1f18459fbebc622272ad6800`、`300dc86b83c74dceb65972cf818d4785` 全部返回 `Passed`。新增“巨像在行动前移除”差分后，首次有效运行 `9651a127d9154076a2663314369cde55` 发现修正后的 `3` 点意图被除以 `0.5` 只能错误还原为 `6`，而原始伤害是 `7`；生产攻击快照增加未修正基础伤害后最终通过。`aac12111d23f4d0c98980b73c217af3c` 在 `start_run` 阶段直接退出且没有执行差分，不计为语义结果。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `ColossusPower.ModifyDamageMultiplicative(...)` | 巨像：伤害倍率 | 持有者受到易伤攻击者的有力量攻击时伤害减半 | 打击 `6 → 3`；奇数基础伤害在巨像移除后精确恢复 `7`，实机通过 |
| `ColossusPower.AfterSideTurnEnd(...)` | 巨像：敌方回合结束 | 每次敌方回合结束减少 `1` 层 | 敌方持有时 `1 → 0`；玩家持有时行动前移除路径通过 |
| `ConcoctPower.AfterDamageGiven(...)` | 调制：造成伤害后 | 每段造成未格挡伤害的有力量攻击给目标施加等于层数的中毒 | 打击造成伤害后施加 `2` 中毒，实机通过 |
| `ConcoctPower.AfterSideTurnEnd(...)` | 调制：对方回合结束 | 对方回合结束时移除 | 玩家持有时敌方回合结束移除，实机通过 |
| `CorrosiveWavePower.AfterCardDrawn(...)` | 腐蚀波：抽牌后 | 持有者每抽 `1` 张牌，所有可命中对手获得等于层数的中毒 | `2` 层、抽 `5` 张累计施加 `10` 中毒 |
| `CorrosiveWavePower.AfterSideTurnEnd(...)` | 腐蚀波：所属方回合结束 | 所属方回合结束时移除 | 中毒保留，腐蚀波移除，实机通过 |
| `DemisePower.AfterSideTurnEnd(...)` | 消亡：所属方回合结束 | 造成等于层数的不可格挡、无力量自伤，状态保留 | `10` 格挡不变并失去 `3 HP`，实机通过 |
| `DisintegrationPower.AfterSideTurnEndLate(...)` | 瓦解：所属方回合结束（晚） | 晚回调造成等于层数的可格挡、无力量自伤，状态保留 | `2` 格挡吸收 `5` 点中的 `2` 点，失去 `3 HP` |
| `EscapeArtistPower.AfterSideTurnEnd(...)` | 逃脱大师：所属方回合结束 | 层数大于 `1` 时递减；到 `1` 后保持 | 连续三次为 `3 → 2 → 1 → 1`，实机通过 |
| `GravityPower.AfterSideTurnEnd(...)` | 引力：所属方回合结束 | RF 主动出牌钩子结算后，回合结束移除 | `1 → 0`，实机通过 |
| `HatchPower.AfterSideTurnEnd(...)` | 孵化：所属方回合结束 | 每回合减少 `1` 层并在零层移除 | `2 → 1 → 0`，实机通过 |
| `HighVoltagePower.AfterSideTurnEnd(...)` | 高电压：所属方回合结束 | 每回合获得等于层数的力量，状态不消耗 | 与领地意识组合，三回合累计力量符合预期 |
| `TaintedPower.ModifyDamageAdditive(...)` | 污染：伤害加算 | 持有者每段受到的有力量攻击增加等于层数的伤害 | 缩小甲虫啃咬由 `7` 增至 `10`，实机通过 |
| `TaintedPower.AfterSideTurnEnd(...)` | 污染：敌方回合结束 | 敌方回合结束时移除 | 行动后移除路径为 `10` 伤害；行动前移除路径恢复 `7` 伤害 |
| `TerritorialPower.AfterSideTurnEnd(...)` | 领地意识：所属方回合结束 | 每回合获得等于层数的力量，状态不消耗 | 与高电压组合每回合获得 `5` 力量，累计三回合通过 |
| `ConsumingShadowPower.AfterSideTurnEnd(...)` | 吞噬暗影：所属方回合结束 | 按层数从队尾依次唤起最后一个 Orb | `1` 层唤起冰霜 Orb，获得 `5` 格挡且 Orb 离队 |
| `NemesisPower.AfterSideTurnEnd(...)` | 天罚：所属方回合结束 | 私有开关交替施加和移除无实体 | 连续两次敌方回合结束后无实体先出现再移除，状态指纹一致 |
| `JugglingPower.AfterSideTurnEnd(...)` | 杂耍：所属方回合结束 | 清零本回合攻击计数，RF 第三张攻击主动效果从新回合重新计数 | 回合末前打两张愤怒，回合末后第三张不生成杂耍复制；弃牌堆共 `6` 张愤怒 |
| `TenderPower.AfterCardPlayed(...)` | 柔嫩：出牌后 | 每出一张牌记录 `1` 次，并暂时失去 `1` 力量和 `1` 敏捷 | 两张打击依次造成 `6 + 5 = 11`，私有计数同步 |
| `TenderPower.AfterSideTurnEnd(...)` | 柔嫩：所属方回合结束 | 按本回合出牌数恢复力量和敏捷并清零计数 | 两张牌后完整恢复，柔嫩本身保留，实机通过 |

说明：RF `0.13.7` 对调制 `AfterDamageGiven`、腐蚀波 `AfterCardDrawn`、柔嫩 `AfterCardPlayed` 虽有显式注册，但处理器只记录 `MethodMirrorIncomplete`，并未执行牌效；覆盖目录因此由求解器补偿覆盖其名义上的 `RfExact`。夹击和击倒的唯一来源卡均由原版标记为 `MultiplayerOnly`，对应施加、伤害倍率和回合结束共 `6` 个钩子已静态核对为单人范围不适用，不计入上述 `20` 项实机闭环。

### `MONSTER-MOVES-BATCH-032`（22 项）

闭环：最终 Release DLL 在同一真实可见游戏 `pid 41596` 中连续投递六场状态差分，中间只返回主菜单。资源、辉星、回合开始 Power、冷却剂、全场目标和仪式六组均直接执行生产模拟与游戏原生回调，并逐字段比较生命、格挡、能量、辉星、Power、Orb 队列、四个牌堆和逐牌伤害；最后一场完成后才退出游戏。

结果：最终 `runId 62385fe4906f40459f6dd897077607ba`、`c44f6d01d85143019929be6b8fcb06e0`、`eb91c2a0bd6440439c19ce2c3facf6e2`、`eca09b4bd61841ce9c4e378d201c786f`、`415ee2c6c92b4cf6889ad03576fe2378`、`2c735c3ab864417ca599f2ad2732cff6` 全部返回 `Passed`。开发过程先发现并修复两个生产偏差：愤怒每次打出都应向弃牌堆生成复制，不能因牌堆已有复制而抑制；准备时间给予的活力被 RF 消耗后必须同步回求解器 Power 状态。资源场还因 `1 HP` 敌人被引雷针提前击杀而无法继续原生回调，改为 `999 HP` 后完成生命周期差分；冷却剂场按故障机器人原生自带的闪电 Orb 修正预期；熔炉生成的君王之剑因抽牌后手牌已满进入弃牌堆。这些开发失败均未记为通过结果。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `ClarityPower.ModifyHandDraw(...)` | 明晰：抽牌修正 | 持续期间每次常规手牌抽取增加 `1` 张 | 与机器学习组合后基础 `5` 张抽至 `8` 张，实机通过 |
| `ClarityPower.AfterSideTurnStart(...)` | 明晰：所属方回合开始 | 抽牌结算后减少 `1` 层 | `2 → 1`，实机通过 |
| `MachineLearningPower.ModifyHandDraw(...)` | 机器学习：抽牌修正 | 每次常规手牌抽取增加当前层数 | `2` 层贡献额外 `2` 张，实机通过 |
| `GenesisPower.AfterEnergyReset(...)` | 创世纪：能量重置后 | 获得等于层数的辉星且不消耗自身 | 与下回合辉星合计获得 `5` 辉星，创世纪保持 `2` 层 |
| `LightningRodPower.AfterEnergyReset(...)` | 引雷针：能量重置后 | 充能 `1` 个闪电 Orb，并减少 `1` 层 | `1` 层完全消耗，Orb 队列新增 `1` 个闪电，实机通过 |
| `RadiancePower.AfterEnergyReset(...)` | 明耀：能量重置后 | 获得动态变量指定的能量并减少 `1` 层，受无法获得能量约束 | 当前场从基础 `3` 能量增至 `4`，层数 `2 → 1` |
| `SpinnerPower.AfterEnergyReset(...)` | 旋转工艺：能量重置后 | 充能等于层数的玻璃 Orb | `2` 层充能 `2` 个玻璃 Orb，状态保留 |
| `StarNextTurnPower.AfterEnergyReset(...)` | 下回合辉星：能量重置后 | 获得保存的辉星并移除状态 | 获得 `3` 辉星后状态消失，实机通过 |
| `BiasedCognitionPower.AfterSideTurnStart(...)` | 偏差认知：所属方回合开始 | 每回合开始失去等于层数的集中 | `2` 层施加 `-2` 集中，实机通过 |
| `CoolantPower.AfterSideTurnStart(...)` | 冷却剂：所属方回合开始 | 按 Orb 的不同类型数乘层数获得无力量格挡 | 闪电与冰霜两种 Orb、`3` 层获得 `6` 格挡 |
| `DemonFormPower.AfterSideTurnStart(...)` | 恶魔形态：所属方回合开始 | 获得等于层数的力量 | `3` 层获得 `3` 力量，实机通过 |
| `FeralPower.AfterSideTurnStart(...)` | 野性：所属方回合开始 | 清零本回合零费攻击计数 | 回合开始后愤怒可按首张零费攻击路径打出，逐牌状态通过 |
| `FurnacePower.AfterSideTurnStart(...)` | 熔炉：所属方回合开始 | 没有未消耗的非复制君王之剑时生成一张，并让所有非复制君王之剑增加当前层数伤害 | `4` 层生成的君王之剑总伤害为 `14`；手牌已满时原生进入弃牌堆，模拟一致 |
| `NeurosurgePower.AfterSideTurnStart(...)` | 精神过载：所属方回合开始 | 给持有者施加等于层数的末日 | `2` 层施加 `2` 末日，实机通过 |
| `NoxiousFumesPower.AfterSideTurnStart(...)` | 毒雾：所属方回合开始 | 给所有可命中的对手施加等于层数的中毒 | `2` 层给目标施加 `2` 中毒，实机通过 |
| `PrepTimePower.AfterSideTurnStart(...)` | 准备时间：所属方回合开始 | 获得等于层数的活力 | `2` 层活力被随后真实打出的愤怒消费，RF 与求解状态均归零 |
| `ReflectPower.AfterSideTurnStart(...)` | 倒映：所属方回合开始 | 每回合开始减少 `1` 层 | `2 → 1`，实机通过 |
| `ShadowStepPower.AfterSideTurnStart(...)` | 暗影步：所属方回合开始 | 将全部层数转换为双倍伤害并移除自身 | `2` 层转为 `2` 层双倍伤害，暗影步消失 |
| `WraithFormPower.AfterSideTurnStart(...)` | 幽魂形态：所属方回合开始 | 每回合开始失去等于层数的敏捷 | `1` 层施加 `-1` 敏捷，实机通过 |
| `RampartPower.AfterSideTurnStart(...)` | 盾墙：玩家方回合开始 | 每个盾墙让所有存活的炮塔操作员获得等于层数的无力量格挡 | 第二只怪物为炮塔操作员时获得 `4` 格挡，实机通过 |
| `RitualPower.AfterApplied(...)` | 仪式：施加后 | 敌方新获得仪式时记录一次延迟，首次敌方回合结束不获得力量；玩家获得时不延迟 | 敌方连续两次回合结束只触发一次 `+2`；玩家首次回合结束立即 `+3` |
| `RitualPower.AfterSideTurnEnd(...)` | 仪式：所属方回合结束 | 延迟条件满足后，每次回合结束获得等于层数的力量 | 毛绒伏地虫“吸入”原有 `+7` 力量后，第二次敌方回合结束再增至 `9`；玩家增至 `3` |

### `MONSTER-MOVES-BATCH-031`（21 项）

闭环：最终 Release DLL 在同一真实可见游戏 `pid 42532` 中连续投递资源、数值修正、生命周期三场 `FUZZY_WURM_CRAWLER_WEAK`，中间只返回主菜单。三组共执行 `11` 条生产模拟/实机差分，覆盖完整玩家回合结束、下一回合能量重置与抽牌、双方回合开始/结束回调、真实出牌和手牌清空时序；每条比较生命、格挡、能量、Power、四个牌堆和逐牌状态。毛绒伏地虫固定执行官中“吸入”，其力量变化也同时进入预测与实机快照。

结果：最终 `runId b27ce2db72564ff3b4ce0cf1100ae303`、`47488c37b3084616815ea42b8e63a86f`、`a0b1f24dc97b4bb2b46f372e9c4ac395` 全部返回 `Passed`。开发过程先补齐真实回合开始夹具的 `Creature.BeforeTurnStart`，并把塔 2 中不存在的旧版卡牌 `SEEING_RED` 换为 RF 已明确镜像的官中“心神不宁”；动态加大型假人遇到房间槽位未就绪、Mock 遭遇又未注册进正式 `ModelDb`，最终改用原生毛绒伏地虫。资源组一次因共享场景的前项把能量留在 `0` 而失败，显式设定后通过；这些失败均未记为通过结果。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `BlockNextTurnPower.AfterBlockCleared(...)` | 下回合格挡：清除格挡后 | 正常清除旧格挡后获得当前层数的无力量格挡，并移除状态 | `7` 旧格挡清空后获得 `4` 格挡，实机差分通过 |
| `BorrowedTimePower.TryModifyEnergyCostInCombat(...)` | 预借时间：费用修正 | 持有者所有牌费用增加当前层数 | `1` 层使 `1` 费打击在 `1` 能量时不可打出 |
| `BorrowedTimePower.AfterSideTurnEnd(...)` | 预借时间：所属方回合结束 | 在持有者一方回合结束时移除 | 实机回调差分通过 |
| `BurstPower.AfterSideTurnEnd(...)` | 爆发：所属方回合结束 | 在持有者一方回合结束时移除 | 组合生命周期差分通过 |
| `ConquerorPower.ModifyDamageMultiplicative(...)` | 征服者：伤害倍率 | 持有者的君王之剑有力量攻击伤害翻倍 | `10` 点君王之剑造成 `20` 点，实机差分通过 |
| `ConquerorPower.AfterSideTurnEnd(...)` | 征服者：所属方回合结束 | 每次持有者一方回合结束减少 `1` 层 | `1 → 0` 实机通过 |
| `DrawCardsNextTurnPower.ModifyHandDraw(...)` | 下回合抽牌：手牌抽取修正 | 下一次常规手牌抽取增加当前层数 | `2` 层使基础 `5` 张变为 `7` 张 |
| `DrawCardsNextTurnPower.AfterSideTurnStart(...)` | 下回合抽牌：所属方回合开始 | 额外抽牌结算后移除 | 实机完整回合衔接通过 |
| `DuplicationPower.AfterSideTurnEnd(...)` | 复制：所属方回合结束 | 在持有者一方回合结束时移除 | 组合生命周期差分通过 |
| `EnergyNextTurnPower.AfterEnergyReset(...)` | 下回合能量：能量重置后 | 重置基础能量后获得当前层数能量并移除 | `3 + 2 = 5` 能量，实机完整回合衔接通过 |
| `FlameBarrierPower.AfterSideTurnEnd(...)` | 火焰屏障：对方回合结束 | 在持有者对方回合结束时移除 | 玩家持有时敌方回合结束移除，差分通过 |
| `NoDrawPower.AfterSideTurnEnd(...)` | 不可抽牌：所属方回合结束 | 回合内阻止非手牌抽取，并在持有者一方回合结束时移除 | 耸肩无视无抽牌、随后状态移除，实机通过 |
| `NoEnergyGainPower.ModifyEnergyGain(...)` | 无法获得能量：能量获取修正 | 持有者本回合所有能量获取变为 `0` | 心神不宁应得 `2` 能量，预测与实机均保持 `0` |
| `NoEnergyGainPower.AfterSideTurnEnd(...)` | 无法获得能量：所属方回合结束 | 在持有者一方回合结束时移除 | 同场实机回调通过 |
| `OneTwoPunchPower.AfterSideTurnEnd(...)` | 连环拳：所属方回合结束 | 在持有者一方回合结束时移除 | 组合生命周期差分通过 |
| `RagePower.AfterSideTurnEnd(...)` | 狂怒：所属方回合结束 | 在持有者一方回合结束时移除 | 组合生命周期差分通过 |
| `ReboundPower.AfterSideTurnEnd(...)` | 弹回：所属方回合结束 | 在持有者一方回合结束时移除 | 组合生命周期差分通过 |
| `RetainHandPower.ShouldFlush(...)` | 保留手牌：是否清空手牌 | 状态存在时不把普通未保留手牌移入弃牌堆 | 完整回合结束后 `6` 张手牌仍全部保留 |
| `RetainHandPower.AfterSideTurnEnd(...)` | 保留手牌：所属方回合结束 | 先阻止本次手牌清空，再减少 `1` 层 | 顺序与实机一致，`1 → 0` |
| `ShadowmeldPower.ModifyBlockMultiplicative(...)` | 融入暗影：格挡倍率 | 有力量格挡乘以 `2^层数` | `1` 层使防御从 `5` 格挡变为 `10` |
| `ShadowmeldPower.AfterSideTurnEnd(...)` | 融入暗影：所属方回合结束 | 在持有者一方回合结束时移除 | 实机回调差分通过 |

说明：爆发、复制、连环拳、狂怒、弹回和火焰屏障的主动效果由 RF `0.13.7` 已注册，本批只把它们缺失的回合末生命周期登记为求解器补偿；其中本批没有重新验证这些主动效果，不能把生命周期结论外推。`NoEnergyGainPower.AfterModifyingEnergyGain` 只闪烁图标，覆盖目录单独登记为纯表现，不计入上表 `21` 项。源码审计还发现搜索推进中敌方中毒被连续触发两次，已删除重复调用；本批差分夹具不经过完整 Beam 展开，因此这项修正尚未作为独立实机闭环计数。

### `MONSTER-MOVES-BATCH-030`（18 项）

闭环：最终 Release DLL 只启动一次真实可见游戏，在同一 `pid 13628` 内连续投递五场 `LivingFogNormal`，中间只回到主菜单。五组共执行 `11` 条生产模拟/实机差分：核心攻击修正、核心格挡修正、中毒多次触发、残影与覆甲生命周期、缓慢累计与重置。测试直接执行真实怪物行动、真实卡牌以及双方回合开始/结束回调，逐字段比较生命、格挡、Power 层数、Power 动态变量、四个牌堆和逐牌状态。

结果：最终 DLL 的 `runId 85caad3182ca42558ce3d19b13ed1ff1`、`406311c7618c4852a84ff272270a5a76`、`7e1415b89bb34038af28ca3b0067118f`、`115a3e343b6f4c119d4d067f4a43b827`、`0b383294cd9f4af78d2d5ba8c87bb7af` 全部返回 `Passed`。开发中格挡组发现“不可格挡”源码使用无条件 `Decrement`，与虚弱/易伤/脆弱的 `TickDownDuration` 不同，修正生产语义后整组通过；缓慢组第一次失败是夹具把 `6 + floor(6 × 1.1)` 错写成 `13`，而预测和实机快照均为 `12`，修正断言后通过。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `StrengthPower.ModifyDamageAdditive(...)` | 力量：伤害加算 | 有力量攻击每段增加等于力量层数的伤害 | 敌方攻击实机差分通过 |
| `DexterityPower.ModifyBlockAdditive(...)` | 敏捷：格挡加算 | 有力量的卡牌或怪物行动格挡增加等于敏捷层数的数值 | 防御牌实机差分通过 |
| `WeakPower.ModifyDamageMultiplicative(...)` | 虚弱：伤害倍率 | 持有者的有力量攻击乘以 `0.75` | 与力量共同作用的敌方攻击通过 |
| `WeakPower.AfterSideTurnEnd(...)` | 虚弱：敌方回合结束 | 敌方回合结束减少 `1` 层；刚施加给玩家的减益首次跳过衰减 | 层数及首次跳过实机通过 |
| `VulnerablePower.ModifyDamageMultiplicative(...)` | 易伤：受伤倍率 | 持有者受到的有力量攻击乘以 `1.5` | 怪物攻击和卡牌攻击均通过 |
| `VulnerablePower.AfterSideTurnEnd(...)` | 易伤：敌方回合结束 | 敌方回合结束减少 `1` 层；刚施加给玩家时首次跳过 | 连续两次回合末回调通过 |
| `FrailPower.ModifyBlockMultiplicative(...)` | 脆弱：格挡倍率 | 持有者从有力量卡牌或怪物行动获得的格挡乘以 `0.75` | 与 `2` 敏捷共同作用时防御获得 `5` 格挡 |
| `FrailPower.AfterSideTurnEnd(...)` | 脆弱：敌方回合结束 | 敌方回合结束减少 `1` 层，并遵守玩家新减益首次跳过 | 生命周期实机通过 |
| `PoisonPower.AfterSideTurnStart(...)` | 中毒：所属方回合开始 | 造成当前层数的不可格挡、无力量伤害，存活时减 `1` 层；催化剂增加触发次数但不超过中毒层数 | `3` 层中毒配 `1` 层催化剂共造成 `5` 伤并剩 `1` 层 |
| `BlurPower.ShouldClearBlock(...)` | 残影：保留格挡 | 所属方回合开始时阻止清除持有者格挡 | `7` 格挡完整保留 |
| `BlurPower.AfterSideTurnStart(...)` | 残影：所属方回合开始 | 保留格挡后减少 `1` 层 | `1 → 0` 实机通过 |
| `DoubleDamagePower.ModifyDamageMultiplicative(...)` | 双倍伤害：卡牌攻击倍率 | 持有者或其宠物打出的有力量卡牌攻击伤害翻倍 | `6` 点打击造成 `12` 点通过 |
| `DoubleDamagePower.AfterSideTurnEnd(...)` | 双倍伤害：所属方回合结束 | 持有者一方回合结束时减少 `1` 层 | 玩家方和敌方分支均纳入模拟，玩家分支实机通过 |
| `NoBlockPower.ModifyBlockMultiplicative(...)` | 不可格挡：卡牌格挡归零 | 有卡牌来源且非无力量的格挡变为 `0`；无来源或无力量格挡不受影响 | 防御牌获得 `0` 格挡通过 |
| `NoBlockPower.AfterSideTurnEnd(...)` | 不可格挡：敌方回合结束 | 无条件减少 `1` 层，不使用其他持续减益的首次跳过规则 | 新施加 `1` 层在同次回调后移除 |
| `PlatingPower.AfterApplied(...)` | 覆甲：施加后 | 单人战斗的敌方每次按 `1` 层递减；克隆现有状态时保留其动态递减值 | 动态变量进入模拟状态并与实机一致 |
| `PlatingPower.AfterSideTurnStart(...)` | 覆甲：所属方回合开始 | 首回合外，敌方按动态递减值减少覆甲，玩家每次减少 `1` | 敌方第 `2` 回合开始 `2 → 1` 通过 |
| `SlowPower.AfterSideTurnStart(...)` | 缓慢：所属方回合开始 | 将本回合已打牌数和显示倍率重置为 `0`，并重置 RF 按 Power 实例保存的计数 | 两张牌累计后从 `2/20` 清零通过 |

说明：缓慢的逐牌计数与每张攻击增加 `10%` 由 RF `0.13.7` 精确镜像，本批补齐其跨回合清零和可比较动态状态；两张 `6` 伤打击的第二张先乘 `1.1` 再单独向下取整，因此总伤仍为 `12`。覆甲的回合末起甲已经由 RF 注册，本批保证搜索推进回合时执行同一时点。另有三个目录条目不计入上表：`BlurPower.AfterPreventingBlockClear` 与 `SlowPower.AfterModifyingDamageAmount` 只闪烁图标，登记为纯表现；`PlatingPower.BeforeSideTurnStart` 只在第一回合玩家方开始前给敌人初始格挡，求解器启动时该原生状态已经存在，登记为 `NativeRuntimeState`。

### `MONSTER-MOVES-BATCH-029`（20 项）

闭环：最终 Release DLL 只启动一次真实可见游戏，在同一 `pid 8240` 中依次投递五场 `LivingFogNormal`，中间只返回主菜单，不退出重进。五组分别验证缩小甲虫与来源死亡、人工制品抵挡负数型减益、藤蔓蹒跚者的缠结、仪式兽的昏眩、无实体的连续受击和敌方回合末衰减。生产模拟与真实行动逐次比较生命、格挡、Power、牌堆、逐牌附魔；纠缠和昏眩还比较实际可出牌结果。

结果：最终 DLL 的 `runId ef124509512846f3bd3a12160122cb73`、`388ad890e07f4ff5a95bdddec298555c`、`d90e4a4276514e86baae4fab4544e80c`、`7f8c7f34dd8e4b459b618a99039c7c9b`、`ef95e668c24b425c868844e3bb34b35c` 全部返回 `Passed`，共完成 `9` 条 `MOVE_DIFF`。较早的纠缠 `runId 9ca0d894301442db8b34b30dd195c27a` 是夹具把基础牌组内四张受影响的打击误断言为一张，改用唯一卡牌后通过，不是生产预测偏差。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `ShrinkerBeetle.SHRINKER_MOVE` | 缩小甲虫：缩小 | 给玩家施加数值为 `-1` 的永久缩小 | 实机行动差分通过 |
| `ShrinkPower.ModifyDamageMultiplicative(...)` | 缩小：攻击伤害修正 | 持有者的有力量攻击只造成原伤害的 `70%` | `6` 点打击在预测与实机中均造成 `4` 点 |
| `ShrinkPower.AfterSideTurnEnd(...)` | 缩小：回合结束 | 负数层数代表永久，不随回合结束递减 | 实机 Power 回调后仍为 `-1` |
| `ShrinkPower.AfterDeath(...)` | 缩小：施加者死亡 | 原始施加者实际死亡时移除缩小 | 实机死亡移除差分通过 |
| `ArtifactPower.TryModifyPowerAmountReceived(...)` | 人工制品：抵挡减益 | 按待施加数值判断 Power 类型；`-1` 的缩小仍是减益并被抵挡 | 实机负数减益分支通过 |
| `ArtifactPower.AfterModifyingPowerAmountReceived(...)` | 人工制品：抵挡后 | 成功抵挡后消耗 `1` 层人工制品 | 实机中人工制品与缩小均不存在 |
| `VineShambler.GRASPING_VINES_MOVE` | 藤蔓蹒跚者：紧绕藤蔓 | 造成 `8` 点攻击伤害，并给玩家施加 `1` 层缠结 | 实机行动差分通过 |
| `TangledPower.AfterApplied(...)` | 缠结：施加后 | 所有已有攻击牌获得官中为“糾纏”的附魔；技能牌不受影响 | 重锤与武装正反分支通过 |
| `TangledPower.AfterCardEnteredCombat(...)` | 缠结：卡牌进入战斗 | 后来进入战斗的攻击牌也获得糾纏 | 生成全身撞击后逐牌差分通过 |
| `TangledPower.TryModifyEnergyCostInCombat(...)` | 缠结：费用修正 | 每层缠结使糾纏牌费用增加 `1` | `3` 费重锤在 `3` 能量时预测与实机均不可打出 |
| `TangledPower.AfterSideTurnEnd(...)` | 缠结：玩家回合结束 | 持有者结束玩家回合时移除缠结 | 实机回调差分通过 |
| `TangledPower.AfterRemoved(...)` | 缠结：移除后 | 清除全部受影响牌的糾纏 | 现有牌与生成牌均恢复无附魔 |
| `RingingPower.AfterApplied(...)` | 昏眩：施加后 | 所有当前未附魔战斗牌获得官中为“鳴響”的附魔 | 攻击、技能逐牌差分通过 |
| `RingingPower.AfterCardEnteredCombat(...)` | 昏眩：卡牌进入战斗 | 后来进入战斗且未附魔的牌也获得鳴響 | 生成全身撞击后逐牌差分通过 |
| `RingingPower.ShouldPlay(...)` | 昏眩：是否可出牌 | 本回合只允许打出第一张鳴響牌；后续鳴響牌不可打出 | 打击可打、全身撞击不可打，预测与实机一致 |
| `RingingPower.AfterSideTurnEnd(...)` | 昏眩：玩家回合结束 | 持有者结束玩家回合时移除昏眩 | 实机回调差分通过 |
| `RingingPower.AfterRemoved(...)` | 昏眩：移除后 | 清除全部受影响牌的鳴響 | 手牌与弃牌堆逐牌状态恢复 |
| `IntangiblePower.ModifyDamageCap(...)` | 无实体：伤害上限 | 对持有者的单次伤害及格挡损失上限为 `1` | 连续两次攻击实机差分通过 |
| `IntangiblePower.ModifyHpLostAfterOsty(...)` | 无实体：生命损失上限 | 实际生命损失大于等于 `1` 时压到 `1` | 两次攻击各只失去 `1 HP` |
| `IntangiblePower.AfterSideTurnEnd(...)` | 无实体：敌方回合结束 | 每次敌方回合结束减少 `1` 层 | `2 → 1 → 0` 实机生命周期通过 |

说明：PCK 简中区里两个卡牌附魔的原文分别是“糾纏”和“鳴響”，本文按游戏原文保留，不自行改成推测译名。覆盖目录同批还关闭了 `ShrinkPower.AfterApplied`、`ShrinkPower.AfterRemoved` 和 `IntangiblePower.AfterModifyingDamageAmount` 三个纯表现条目：它们只缩放模型、填写说明文本或闪烁图标，不计入上表 `20` 项求解器补偿。昏眩的“本回合已出牌”状态按玩家保存、写入搜索状态指纹，并在下一玩家回合开始清除，避免第一张与后续牌路径被错误合并。

### `MONSTER-MOVES-BATCH-028`（6 项）

闭环：最终版 DLL 只启动一次真实可见游戏。第一场 `LivingFogNormal` 中召唤两只幽灵骑士：第一只先施加恶咒并在行动前加入“武装”；第二只再次施加恶咒，在行动后生成“全身撞击”并立刻死亡，验证后施加者死亡不会移除第一只建立的恶咒；最后由第一只再次施加并死亡，验证初始施加者死亡才会清除。每一步比较生命、格挡、Power、四个牌堆，以及每张牌的牌堆位置、附魔 ID、附魔层数和当前虚无关键词。第一场结束后不退出游戏，返回主菜单并向同一 PID 投递第二场；第二场执行恶咒后直接调用完整玩家回合结束两阶段，验证受咒手牌的虚无结算。

结果：一次夹具失败 `runId 4f54ee41da9f4b6889fc59d1f3c07920` 暴露测试器只保证同型号怪物至少一只，第二项尚未执行，未记为通过；修正为按 `monsterOccurrence` 补足实例后，`runId 7f30bc1464c549b7a61d810e78d93359` 连续三项返回 `Passed`，总耗时约 `12.8s`。同一 `pid 26320` 随后复用执行 `runId 3e7205b07be244ae985c49e91e91a945`，完整回合结束差分返回 `Passed`，耗时约 `5.2s`，完成后才退出游戏。首次所有未附魔牌均为 `受咒:2 + 虚无`；恶咒叠到 `4` 后，已有武装仍保持 `受咒:2`，新生成的全身撞击为 `受咒:4`；杀死第二只幽灵骑士后恶咒保持 `4`，杀死最初施加者后所有牌的受咒和虚无才消失；第二场所有受咒手牌均进入消耗堆。预测与实测逐字段一致。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `SpectralKnight.HEX` | 幽灵骑士：恶咒 | 给玩家施加 `2` 层恶咒，并使所有当前未附魔的战斗牌获得同层数受咒 | 双实例连续施加实机差分通过 |
| `HexPower.AfterApplied(...)` | 恶咒：施加后 | 遍历玩家全部战斗牌，只给没有其他附魔的牌添加受咒 | 初次施加后的整副战斗牌状态实机通过 |
| `HexPower.AfterCardEnteredCombat(...)` | 恶咒：卡牌进入战斗 | 恶咒存在时，新进入战斗且未附魔的牌获得当前恶咒层数的受咒 | 恶咒 `4` 层时生成全身撞击，实机通过 |
| `HexPower.TryModifyKeywordsInCombat(...)` | 恶咒：受咒牌获得虚无 | 受咒只在恶咒存在期间获得虚无，不把虚无永久写入卡牌本地关键词 | 逐牌关键词快照及完整回合末消耗实机通过 |
| `HexPower.AfterDeath(...)` | 恶咒：施加者死亡 | 只在记录的初始施加者实际死亡且死亡未被阻止时移除恶咒；后续叠加来源死亡不移除 | RF 未镜像；双来源正反分支实机差分通过 |
| `HexPower.AfterRemoved(...)` | 恶咒：移除后 | 清除玩家全部战斗牌上的受咒；由恶咒提供的虚无随之消失 | 死亡移除后的整副战斗牌状态实机通过 |

说明：日志明确记录 RF `0.13.7` 未注册 `HexPower.AfterDeath`，本批由 CombatSolver 补齐。模拟从真实已有 Power 克隆时保留原 `Applier`，不再被本次叠加来源覆盖；Power 的施加者与目标 ID、卡牌附魔 ID 与层数都进入状态指纹或路线复用戳，避免错误合并。`Hexed.AfterCardEnteredCombat` 的无恶咒自清理分支列在静态闭环，不借本批实机结果标记为通过。

### `MONSTER-MOVES-BATCH-027`（3 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，在同一只蛇行扼杀者上连续执行两次“缠身”。第一次行动后，生产模拟直接运行求解器的玩家方回合结束补偿，真实侧直接调用游戏 `ConstrictPower.AfterSideTurnEnd`；第二次从已有 `3` 层开始再次施加，然后分别击杀模拟和真实施加者，并显式断言玩家身上不再存在紧缠。每一步比较生命、格挡、Power、牌堆和仍存活敌人的汇总。

结果：前两次 `runId ce3651b279c34a88ad624cbfe7bded23`、`4b3300cae4374fdf96566c3297515dcd` 分别暴露测试器无法给已移除怪物取战斗状态、以及模拟汇总仍包含死亡敌人的问题，均没有记为通过。修正测试规范化后，`runId 8c0093c8f439489f837ede079213f74a` 返回 `Passed`：第一次预测/实测均为玩家 `80 → 77 HP` 且保留 `3` 层紧缠；第二次预测/实测均为施加者 `0 HP` 且玩家紧缠消失，总耗时约 `12.4s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `SlitheringStrangler.CONSTRICT` | 蛇行扼杀者：缠身 | 给玩家施加 `3` 层紧缠 | 同一实例两次施加的实机差分通过 |
| `ConstrictPower.AfterSideTurnEnd(...)` | 紧缠：玩家方回合结束 | 持有者结束回合时受到等于层数的无力量伤害；状态不自行递减 | `3` 层造成 `3` 点生命损失，实机回调差分通过 |
| `ConstrictPower.AfterDeath(...)` | 紧缠：施加者死亡 | 施加者实际死亡且死亡未被阻止时移除整项紧缠 | 从已叠加状态击杀施加者，实机死亡移除差分通过 |

说明：RF `0.13.7` 对 `ConstrictPower.AfterDeath` 没有镜像，最终通过日志明确记录了该未注册提示；本条由 CombatSolver 补偿。为使死亡移除能够安全复用，Power 的施加者与目标 Combat ID 现在也进入状态指纹。测试器的敌方汇总改为只比较仍存活敌人，与真实游戏移除死亡怪物后的列表口径一致。

### `MONSTER-MOVES-BATCH-026`（2 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，召唤一只永世沙漏，在同一实例上连续强制两次“加大力度”。每项都从当时的真实状态创建一步预测，再执行真实 `MonsterModel.PerformMove()`；除生命、格挡、双方 Power 和四个牌堆计数外，本批新增按牌堆汇总“凋萎”的伤害数值，以直接比较伪升级等级。整批只启动一次游戏，两个检查结束后统一退出。

结果：`runId 8b0dfb4013be4f2e9af75362d8285682` 返回 `Passed`，两条 `MOVE_DIFF` 的预测和实测逐字段一致，总耗时约 `14.3s`。第一次均为 `3` 力量、弃牌堆 `1` 张伤害 `6` 的凋萎；第二次均为累计 `7` 力量、`2` 张凋萎且伤害总和 `18`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Aeonglass.INCREASING_INTENSITY_MOVE` | 永世沙漏：加大力度 | 每次把所有已有“凋萎”提高一级，再递增生成等级并向弃牌堆加入当前难度数量的凋萎；获得的力量依次为基础值 `3`、`4`、`5`…… | 同一实例连续两次实机差分通过 |
| `Aeonglass.AfterCardGeneratedForCombat(...)` | 永世沙漏：生成凋萎后的等级匹配 | 新生成的凋萎应匹配当前累计等级；RF `0.13.7` 只读取真实实例计数，求解器在前向模拟中按模拟计数补齐差值 | 两次生成牌数量及伤害总和实机差分通过 |

说明：游戏当前 PCK 中卡牌官中为“凋萎”。求解器分别保存额外力量和凋萎等级两个跨行动计数器，并把它们写入状态指纹；每次怪物行动、出牌和回合结束模拟后，只对预测牌副本补齐缺少的凋萎等级，不修改真实怪物或真实卡牌。

### `MONSTER-MOVES-BATCH-024`（4 个新增适配项，6 项连续检查）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，召唤骇鳗、胧光怪、电击机器人和结实的卵。在同一场战斗依次执行 `撕扯 → 撞击 → 起航 → 锐利凝视 → 电击 → 啃咬`；项目间只恢复玩家生命，不清理怪物 Power。每项先调用生产模拟，再执行真实行动，并比较生命、格挡、目标 Power 与全场敌方 Power。

结果：`runId 1728753320d549dc927192fb765b3f11` 返回 `Passed`，完整列出六项 `completedChecks`；六条 `MOVE_DIFF` 的预测与实测逐字段一致，总耗时约 `15.4s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `TerrorEel.THRASH_MOVE` | 骇鳗：撕扯 | 当前难度造成 `3 × 3` 点伤害，随后获得 `6` 点活力；下一次撞击造成 `16 + 6 = 22` 并消费活力 | 与既有撞击组成跨行动实机闭环通过 |
| `TheObscura.SAIL_MOVE` | 胧光怪：起航 | 给所有存活敌人（包括自身）`3` 点力量 | 全场 Power 差分及三个后续攻击通过 |
| `Zapbot.ZAP` | 电击机器人：电击 | 基础伤害 `14`；承接起航后造成 `17`，原有 `2` 层高电压不变 | 实机累计状态差分通过 |
| `ToughEgg.NIBBLE_MOVE` | 结实的卵：啃咬 | 基础伤害 `4`；承接起航后造成 `7`，原有孵化状态不变 | 实机累计状态差分通过 |

说明：`TerrorEel.CRASH_MOVE`（骇鳗：撞击）和 `TheObscura.PIERCING_GAZE_MOVE`（胧光怪：锐利凝视）已在更早批次登记，本批只用它们承接活力和力量，不重复计数。活力在实时状态与模拟状态不一致时的伤害差额及攻击后消费，现在由手写怪物行动层补偿；RF 对普通模拟攻击的活力镜像仍保留原分类。

### `MONSTER-MOVES-BATCH-023`（3 项）

闭环：单次启动真实可见游戏并进入原生 `BowlbugsWeak`。在同一只盛碗虫（石）上先用 `15` 格挡完全挡住头槌，再让测试脚手架直接执行该次真实结算生成的动态 `STUNNED` 行动，最后用 `14` 格挡承受一次头槌。每项均先调用生产模拟，再执行真实 `MonsterModel.PerformMove()`；除生命、格挡和 Power 外，还断言模拟中的待跳过行动状态以及真实怪物的后续行动 ID。

结果：`runId 5d0b8d4cecd24ef9aad8e7b68b28b6cf` 返回 `Passed`，完整列出 `HEADBUTT_MOVE → STUNNED → HEADBUTT_MOVE` 三项检查；三条 `MOVE_DIFF` 的预测与实测逐字段一致，后续行动依次为 `STUNNED`、`HEADBUTT_MOVE`、`HEADBUTT_MOVE`，总耗时约 `12.8s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `BowlbugRock.HEADBUTT_MOVE` | 盛碗虫（石）：头槌 | 当前难度造成 `15` 点伤害；被完全格挡且持有失衡时，登记下一次怪物行动为空行动；只挡住 `14/15` 时不触发 | 完全格挡与部分格挡实机差分通过 |
| `BowlbugRock.DIZZY_MOVE` / 动态 `STUNNED` | 盛碗虫（石）：昏头转向 | 不造成战斗数值变化，执行同一个 `DizzyMove` 回调后回到头槌 | 真实承接上一行动生成的 `STUNNED`，跨行动闭环通过 |
| `ImbalancedPower.AfterDamageGiven(...)` | 失衡：造成伤害后处理 | 持有者的攻击被完全格挡时触发；盛碗虫（石）进入昏头转向，求解器指纹、回合推进和威胁评分均保留该状态 | 盛碗虫（石）分支实机通过；其他怪物的通用眩晕分支完成 0.111.0 源码核对 |

说明：RF `0.13.7` 把 `ImbalancedPower.AfterDamageGiven` 登记为可忽略，但对求解器而言它会改变下一次怪物行动，不能沿用该结论。本批因此以 CombatSolver 补偿覆盖 RF 分类。其他怪物持有失衡时，游戏源码走 `CreatureCmd.Stun` 通用分支；求解器使用同一“跳过下一行动”状态表示，当前没有把这条源码核对写成已单独实机差分。

### `MONSTER-MOVES-BATCH-022`（5 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，召唤一只灵魂异鱼，在同一实例上按 `呼唤 → 排气 → 凝视 → 消逝 → 尖叫` 连续强制五个行动。每项比较玩家和目标生命、格挡、双方及全场敌方 Power 与四个玩家牌堆，保留前项生成的卡牌和无实体状态以验证累计结果。

结果：`runId 6af15e9b81e74e739ca65cea5b5ec913` 返回 `Passed`，完整列出 `5` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_5` 至 `5_of_5`，总耗时约 `15.8s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `SoulFysh.BECKON_MOVE` | 灵魂异鱼：呼唤 | 向抽牌堆随机位置加入 `1` 张“呼喚”，并向弃牌堆加入 `1` 张“呼喚” | 实机累计牌堆差分通过 |
| `SoulFysh.DE_GAS_MOVE` | 灵魂异鱼：排气 | 当前难度造成 `16` 点伤害 | 实机一步差分通过 |
| `SoulFysh.GAZE_MOVE` | 灵魂异鱼：凝视 | 当前难度造成 `7` 点伤害，并向弃牌堆加入 `1` 张“呼喚”；弃牌堆累计为 `2` 张 | 实机累计牌堆差分通过 |
| `SoulFysh.FADE_MOVE` | 灵魂异鱼：消逝 | 获得 `2` 层无实体 | 实机一步差分通过 |
| `SoulFysh.SCREAM_MOVE` | 灵魂异鱼：尖叫 | 当前难度造成 `13` 点伤害并给玩家 `3` 层易伤；保留已有 `2` 层无实体 | 实机累计状态差分通过 |

说明：行动名“呼唤”和状态牌名“呼喚”均按当前游戏 PCK 的简中原文记录。状态牌在手牌中结束回合会失去 `6` 点不可格挡生命，该回调已由 RF `0.13.7` 的 `CardOnTurnEndInHandMirrors` 显式精确注册；本批实机闭环证明的是怪物行动、生成位置及累计状态，没有把 RF 的静态注册冒充为新的实机验证。`IsInvisible` 只被音乐与动画表现读取，因此不进入搜索指纹。

### `MONSTER-MOVES-BATCH-021`（13 项）

闭环：只启动一次真实可见游戏。第一个请求进入原生 `KaiserCrabBoss`，连续强制碾碎爪和火箭的十个行动；完成后返回主菜单但保留进程。第二个请求复用同一 PID，进入 `LivingFogNormal`，召唤劫掠者追踪手和噪音机器人并连续强制三个行动。每项均比较玩家与目标生命、格挡、目标及全场敌方 Power 和四个玩家牌堆；第二个请求结束后才退出游戏。

结果：`runId 5ac74aa7181d438c8e783c2388ab2b76` 在 PID `43648` 完成凯撒蟹 `10/10`，耗时约 `20.8s`；`runId c70c277ad2b942bda979afcd758e604c` 记录 `reused_process=True`、`process_sequence=2`，在同一 PID 完成支援组 `3/3`，耗时约 `8.0s`。两批均返回 `Passed`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Crusher.THRASH_MOVE` | 碾碎爪：撕扯 | 单段攻击；原生遭遇中同时读取包围和左侧背袭状态 | 原生 Boss 战实机差分通过 |
| `Crusher.ENLARGING_STRIKE_MOVE` | 碾碎爪：巨化打击 | 单段攻击，不产生额外战斗状态 | 原生 Boss 战实机差分通过 |
| `Crusher.BUG_STING_MOVE` | 碾碎爪：虫蛰 | 两段攻击，随后给玩家 `2` 层虚弱和 `2` 层脆弱 | 原生 Boss 战实机差分通过 |
| `Crusher.ADAPT_MOVE` | 碾碎爪：适应 | 按当前难度获得 `2` 点力量 | 原生 Boss 战实机差分通过 |
| `Crusher.GUARDED_STRIKE_MOVE` | 碾碎爪：戒备打击 | 单段攻击，随后获得 `18` 格挡；读取前项累计的 `2` 力量 | 原生 Boss 战累计状态差分通过 |
| `Rocket.TARGETING_RETICLE_MOVE` | 火箭：瞄准十字 | 单段攻击；原生遭遇中同时读取包围和右侧背袭状态 | 原生 Boss 战实机差分通过 |
| `Rocket.PRECISION_BEAM_MOVE` | 火箭：精准光束 | 单段攻击，不产生额外战斗状态 | 原生 Boss 战实机差分通过 |
| `Rocket.CHARGE_UP_MOVE` | 火箭：蓄能 | 按当前难度获得 `2` 点力量 | 原生 Boss 战实机差分通过 |
| `Rocket.LASER_MOVE` | 火箭：激光 | 单段攻击，并读取前项累计的 `2` 力量 | 原生 Boss 战累计状态差分通过 |
| `Rocket.RECHARGE_MOVE` | 火箭：重新充能 | 只播放重新充能表现，不改变战斗数值，保留已有力量 | 原生 Boss 战实机差分通过 |
| `TrackerRubyRaider.TRACK_MOVE` | 劫掠者追踪手：追踪 | 给玩家 `2` 层脆弱 | 同进程第二场实机差分通过 |
| `TrackerRubyRaider.HOUNDS_MOVE` | 劫掠者追踪手：放狗 | 当前难度造成 `8 × 1` 点伤害 | 同进程第二场实机差分通过 |
| `Noisebot.NOISE_MOVE` | 噪音机器人：噪音 | 向弃牌堆加入 `1` 张晕眩，并向抽牌堆随机位置加入 `1` 张晕眩 | 同进程牌堆差分通过 |

说明：本批只登记上述行动的完整即时语义。凯撒蟹的包围、左右背袭和蟹怒 Power 来自原生遭遇，并参与真实伤害计算，但它们各自的生命周期仍按独立 Power 条目维护；本批没有借行动测试扩大其覆盖结论。

### `MONSTER-MOVES-BATCH-020`（13 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，召唤虱虫之祖、地道虫、方柱构装体和熟睡甲虫，在同一场战斗中连续强制执行十三个行动。每项比较生命、格挡、目标及全场敌方 Power 和四个玩家牌堆；同一怪物实例保留前序力量与格挡，用于验证后续攻击读取累计状态。另逐项搜索游戏源码中 `Curled`、`IsStunned`、`IsBurrowed`、`IsCharging` 和 `IsAwake` 的全部读写位置。

结果：`runId 3a1ce1c569a64f208d27ddc4573705a9` 返回 `Passed`，完整列出 `13` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_13` 至 `13_of_13`，总耗时约 `20.7s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `LouseProgenitor.WEB_CANNON_MOVE` | 虱虫之祖：吐网炮 | 造成 `9` 点伤害并给玩家 `2` 层脆弱 | 实机批量一步差分通过 |
| `LouseProgenitor.CURL_AND_GROW_MOVE` | 虱虫之祖：蜷身成长 | 获得 `14` 格挡和 `5` 点力量 | 实机批量一步差分通过 |
| `LouseProgenitor.POUNCE_MOVE` | 虱虫之祖：猛扑 | 基础伤害 `14`；受前项 `5` 力量影响实际造成 `19` 点伤害 | 实机批量累计状态差分通过 |
| `Tunneler.BITE_MOVE` | 地道虫：啃咬 | 按意图执行单段攻击；测试难度下造成 `13` 点伤害 | 实机批量一步差分通过 |
| `Tunneler.BURROW_MOVE` | 地道虫：钻洞 | 获得 `1` 层埋地和 `32` 格挡 | 实机批量一步差分通过 |
| `Tunneler.BELOW_MOVE` | 地道虫：下方攻击 | 按意图执行单段攻击；测试难度下造成 `23` 点伤害 | 实机批量一步差分通过 |
| `Tunneler.DIZZY_MOVE` | 地道虫：昏头转向 | 不改变战斗数值；只清除控制受击表现的 `IsStunned` 字段 | 实机批量一步差分通过 |
| `CubexConstruct.EXPEL_MOVE` | 方柱构装体：排出 | 按意图执行两段攻击；测试难度下为 `2 × 5` 点伤害 | 实机批量一步差分通过 |
| `CubexConstruct.CHARGE_UP_MOVE` | 方柱构装体：蓄能 | 获得 `2` 点力量；埋地和充能字段只控制表现 | 实机批量一步差分通过 |
| `CubexConstruct.REPEATER_BLAST_MOVE` | 方柱构装体：重复轰击 | 受已有 `2` 力量影响造成 `9` 点伤害，随后力量增加到 `4` | 实机批量累计状态差分通过 |
| `CubexConstruct.REPEATER_BLAST_MOVE_2` | 方柱构装体：重复轰击（共用词条） | 受已有 `4` 力量影响造成 `11` 点伤害，随后力量增加到 `6` | 实机批量累计状态差分通过 |
| `SlumberingBeetle.SNORE_MOVE` | 熟睡甲虫：打鼾 | 行动回调为空，不改变战斗数值 | 实机批量一步差分通过 |
| `SlumberingBeetle.ROLL_OUT_MOVE` | 熟睡甲虫：出击 | 造成 `16` 点伤害后获得 `2` 点力量；`IsAwake` 只控制表现 | 实机批量一步差分通过 |

说明：虱虫之祖的卷曲字段、地道虫的眩晕字段、方柱构装体的埋地/充能字段和熟睡甲虫的醒来字段本身只控制动画、音效或受击表现，因此不加入搜索指纹。真正影响伤害、状态机或行动转换的卷曲、埋地、沉睡等 Power 生命周期仍是独立适配项，不能借本批行动结论标记为通过。

### `MONSTER-MOVES-BATCH-019`（14 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，先召唤女王入场所依赖的火炬头聚合体，再在剩余槽位召唤遗忘之物、猫头鹰法官、仪式兽和女王，在同一场战斗中连续强制执行十四个行动。测试除比较生命、格挡、目标 Power 和玩家牌堆外，还新增比较全场每种敌人的全部 Power，能够直接检验女王的群体力量增益。

结果：`runId 895f27f13f184b939f1b58e4b80c2f62` 返回 `Passed`，完整列出 `14` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_14` 至 `14_of_14`，总耗时约 `23.9s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `TheForgotten.MIASMA` | 遗忘之物：瘴气 | 玩家失去 `2` 敏捷，怪物获得 `8` 格挡和 `2` 敏捷 | 实机批量状态差分通过 |
| `TheForgotten.DREAD` | 遗忘之物：恐惧 | 伤害随模拟中的自身敏捷变化；紧接瘴气后造成 `13 + 2 = 15` 点伤害 | 实机批量跨行动状态差分通过 |
| `OwlMagistrate.MAGISTRATE_SCRUTINY` | 猫头鹰法官：法官审查 | 按意图执行单段攻击；测试难度下造成 `16` 点伤害 | 实机批量一步差分通过 |
| `OwlMagistrate.PECK_ASSAULT` | 猫头鹰法官：猛啄袭击 | 按意图执行六段攻击；测试难度下为 `6 × 4` 点伤害 | 实机批量一步差分通过 |
| `OwlMagistrate.JUDICIAL_FLIGHT` | 猫头鹰法官：飞法行为 | 获得 `1` 层翱翔；`IsFlying` 只选择音效和动画表现 | 实机批量一步差分通过 |
| `OwlMagistrate.VERDICT` | 猫头鹰法官：裁决 | 造成 `33` 点伤害、给玩家 `4` 层易伤并移除自身翱翔 | 实机批量状态移除差分通过 |
| `CeremonialBeast.STAMP_MOVE` | 仪式兽：跺地 | 按实时模型字段获得 `150` 层耕犁 | 实机批量一步差分通过 |
| `CeremonialBeast.BEAST_CRY_MOVE` | 仪式兽：野兽咆哮 | 给玩家 `1` 层耳鸣 | 实机批量一步差分通过 |
| `Queen.PUPPET_STRINGS_MOVE` | 女王：游戏无独立简中词条（`PUPPET_STRINGS_MOVE`） | 给玩家 `3` 层束缚锁链 | 实机批量一步差分通过 |
| `Queen.YOU_ARE_MINE_MOVE` | 女王：你是我的了 | 给玩家各 `99` 层脆弱、虚弱和易伤；测试中易伤从既有 `4` 层累计到 `103` | 实机批量累计状态差分通过 |
| `Queen.OFF_WITH_YOUR_HEAD_MOVE` | 女王：将头砍下 | 按意图执行五段攻击；测试难度下为 `5 × 3` 点伤害 | 实机批量一步差分通过 |
| `Queen.EXECUTION_MOVE` | 女王：处决 | 按意图执行单段攻击；测试难度下造成 `15` 点伤害 | 实机批量一步差分通过 |
| `Queen.ENRAGE_MOVE` | 女王：游戏无独立简中词条（`ENRAGE_MOVE`） | 女王获得 `2` 点力量 | 实机批量一步差分通过 |
| `Queen.BURN_BRIGHT_FOR_ME_MOVE` | 女王：为我尽力燃烧吧 | 女王获得 `20` 格挡，场上其他四名敌人各获得 `1` 点力量，女王自身不获得该力量 | 实机批量全场 Power 差分通过 |

说明：仪式兽耕犁的受击移除、眩晕和二阶段转换，猫头鹰法官翱翔的伤害限制生命周期，女王在火炬头聚合体死亡后的条件分支，以及束缚锁链和耳鸣的后续触发均是独立语义。本批只证明表中行动的即时结果和恐惧的模拟敏捷联动。

### `MONSTER-MOVES-BATCH-018`（15 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，在五个空槽位中召唤神秘骑士、异蛙寄生虫、墨影幻灵、无厌沙虫和胧光怪，在同一场战斗中连续强制执行十五个行动。每项比较生产预测与真实 `MonsterModel.PerformMove()` 后的生命、格挡、双方 Power 和四个玩家牌堆。各怪的固定伤害行动均安排在本批力量增长之前。

结果：`runId 14083c515d1d4b6a8b225c3c5e3e15a6` 返回 `Passed`，完整列出 `15` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_15` 至 `15_of_15`，总耗时约 `25.9s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `MysteriousKnight.FLAIL_MOVE` | 神秘骑士：连枷 | 继承连枷骑士的两段攻击；加上入场自带 `6` 力量后为 `2 × 15` 点伤害 | 实机批量一步差分通过 |
| `MysteriousKnight.RAM_MOVE` | 神秘骑士：撞击 | 继承连枷骑士的单段攻击；加上入场自带 `6` 力量后造成 `21` 点伤害 | 实机批量一步差分通过 |
| `MysteriousKnight.WAR_CHANT` | 神秘骑士：战争吟唱 | 获得 `3` 点力量；测试中从入场 `6` 点累计到 `9` 点 | 实机批量累计状态差分通过 |
| `PhrogParasite.LASH_MOVE` | 异蛙寄生虫：甩动 | 按意图执行四段攻击；测试难度下为 `4 × 4` 点伤害 | 实机批量一步差分通过 |
| `PhrogParasite.INFECT_MOVE` | 异蛙寄生虫：感染 | 向玩家弃牌堆加入 `3` 张感染 | 实机批量牌堆差分通过 |
| `Vantom.INK_BLOT_MOVE` | 墨影幻灵：墨迹 | 按意图执行单段攻击；测试难度下造成 `7` 点伤害 | 实机批量一步差分通过 |
| `Vantom.INKY_LANCE_MOVE` | 墨影幻灵：墨水长枪 | 按意图执行两段攻击；测试难度下为 `2 × 6` 点伤害 | 实机批量一步差分通过 |
| `Vantom.DISMEMBER_MOVE` | 墨影幻灵：肢解 | 造成 `26` 点伤害，随后向玩家弃牌堆加入 `3` 张伤口 | 实机批量牌堆差分通过 |
| `Vantom.PREPARE_MOVE` | 墨影幻灵：准备 | 获得 `2` 点力量 | 实机批量一步差分通过 |
| `TheInsatiable.THRASH_MOVE` | 无厌沙虫：撕扯 | 按意图执行两段攻击；测试难度下为 `2 × 8` 点伤害 | 实机批量一步差分通过 |
| `TheInsatiable.THRASH_MOVE_2` | 无厌沙虫：撕扯（共用词条） | 与 `THRASH_MOVE` 共用回调，独立强制执行时同样造成 `2 × 8` 点伤害 | 实机批量一步差分通过 |
| `TheInsatiable.LUNGING_BITE_MOVE` | 无厌沙虫：前扑啃咬 | 按意图执行单段攻击；测试难度下造成 `28` 点伤害 | 实机批量一步差分通过 |
| `TheInsatiable.SALIVATE_MOVE` | 无厌沙虫：分泌唾液 | 按实时模型字段获得力量；测试中获得 `2` 点力量 | 实机批量一步差分通过 |
| `TheObscura.PIERCING_GAZE_MOVE` | 胧光怪：锐利凝视 | 隐藏行动，按意图执行单段攻击；测试难度下造成 `10` 点伤害 | 实机批量一步差分通过 |
| `TheObscura.HARDENING_STRIKE_MOVE` | 胧光怪：硬化攻击 | 造成 `6` 点伤害后获得实时模型字段指定的 `6` 点格挡 | 实机批量一步差分通过 |

说明：异蛙寄生虫的感染 Power、墨影幻灵的滑溜 Power、无厌沙虫的液化地面与沙坑、胧光怪的幻象召唤和哀嚎群体增益均为独立语义。本批只证明表中行动即时结果，不借入场状态或同类行动标记这些生命周期、召唤和群体效果通过。

### `MONSTER-MOVES-BATCH-017`（14 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，在五个空槽位中召唤对手1型、对手2型、对手3型、蟾蜍蝌蚪和藤蔓蹒跚者，在同一场战斗中连续强制执行十四个行动。每项比较生产预测与真实 `MonsterModel.PerformMove()` 后的生命、格挡、双方 Power 和四个玩家牌堆。对手系列未出现在 `ModelDb.Monsters` 公开枚举中，脚手架按精确游戏类型取得 `ModelId`，再从 `ModelDb` 读取规范实例，没有自行构造重复模型。

前两次运行 `runId fdf9d24f42b54183b7b1299c64bdffc7`、`runId f5f81a48f7f84c0d8b6efc0a4e5fb184` 均在 `inject_state` 阶段失败且 `completedChecks` 为空，分别暴露公开怪物枚举缺项和重复模型构造问题，不属于行动语义差分失败。修正脚手架后，`runId cbc501394ad94a199dc2ac698961f63d` 返回 `Passed`，完整列出 `14` 个 `completedChecks`，总耗时约 `18.2s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `TheAdversaryMkOne.SMASH_MOVE` | 对手1型：砸击 | 按意图执行单段攻击，造成 `12` 点伤害 | 实机批量一步差分通过 |
| `TheAdversaryMkOne.BEAM_MOVE` | 对手1型：光束 | 按意图执行单段攻击，造成 `15` 点伤害 | 实机批量一步差分通过 |
| `TheAdversaryMkOne.BARRAGE_MOVE` | 对手1型：弹幕 | 造成 `2 × 8` 点伤害后获得 `2` 点力量 | 实机批量一步差分通过 |
| `TheAdversaryMkTwo.BASH_MOVE` | 对手2型：猛击 | 按意图执行单段攻击，造成 `13` 点伤害 | 实机批量一步差分通过 |
| `TheAdversaryMkTwo.FLAME_BEAM_MOVE` | 对手2型：火焰光束 | 造成 `16` 点伤害；`0.111.0` 真实回调不生成状态牌 | 实机批量一步差分通过 |
| `TheAdversaryMkTwo.BARRAGE_MOVE` | 对手2型：弹幕 | 造成 `2 × 9` 点伤害后获得 `3` 点力量 | 实机批量一步差分通过 |
| `TheAdversaryMkThree.CRASH_MOVE` | 对手3型：冲击 | 按意图执行单段攻击，造成 `15` 点伤害 | 实机批量一步差分通过 |
| `TheAdversaryMkThree.FLAME_BEAM_MOVE` | 对手3型：火焰光束 | 按意图执行单段攻击，造成 `18` 点伤害 | 实机批量一步差分通过 |
| `TheAdversaryMkThree.BARRAGE_MOVE` | 对手3型：弹幕 | 造成 `2 × 10` 点伤害后获得 `4` 点力量 | 实机批量一步差分通过 |
| `Toadpole.WHIRL_MOVE` | 蟾蜍蝌蚪：旋转 | 按意图执行单段攻击；测试难度下造成 `7` 点伤害 | 实机批量一步差分通过 |
| `Toadpole.SPIKEN_MOVE` | 蟾蜍蝌蚪：带刺 | 获得 `2` 层荆棘 | 实机批量一步差分通过 |
| `Toadpole.SPIKE_SPIT_MOVE` | 蟾蜍蝌蚪：吐刺 | 先移除 `2` 层荆棘，再造成 `3 × 3` 点伤害 | 实机批量状态移除差分通过 |
| `VineShambler.SWIPE_MOVE` | 藤蔓蹒跚者：挥击 | 按意图执行两段攻击；测试难度下为 `2 × 6` 点伤害 | 实机批量一步差分通过 |
| `VineShambler.CHOMP_MOVE` | 藤蔓蹒跚者：大啃 | 按意图执行单段攻击；测试难度下造成 `16` 点伤害 | 实机批量一步差分通过 |

说明：对手2型源码保留了未被行动回调使用的 `FlameBeamStatusCount` 字段，求解器以真实回调为准，不凭字段名生成状态牌。蟾蜍蝌蚪的荆棘反伤生命周期，以及藤蔓蹒跚者的抓缠藤蔓和纠缠 Power 生命周期，仍是独立条目，本批不把它们标记为通过。

### `MONSTER-MOVES-BATCH-016`（13 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，在五个空槽位中召唤卑鄙地精、树枝史莱姆（中）、树枝史莱姆（小）、火炬头聚合体和高塔炮手，在同一场战斗中连续强制执行十三个行动。每项比较生产预测与真实 `MonsterModel.PerformMove()` 后的生命、格挡、双方 Power 和四个玩家牌堆。

结果：`runId 29cec106cb8d40618161f6e359b62c59` 返回 `Passed`，完整列出 `13` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_13` 至 `13_of_13`，总耗时约 `20.2s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `SneakyGremlin.SPAWNED_MOVE` | 卑鄙地精：醒来 | 不改变战斗数值；`IsAwake` 只选择受击表现，后续固定进入冲撞 | 实机批量一步差分通过 |
| `SneakyGremlin.TACKLE_MOVE` | 卑鄙地精：冲撞 | 按意图执行单段攻击；测试难度下造成 `9` 点伤害 | 实机批量一步差分通过 |
| `TwigSlimeM.POKEY_POUNCE_MOVE` | 树枝史莱姆（中）：戳刺扑击 | 按意图执行单段攻击；测试难度下造成 `11` 点伤害 | 实机批量一步差分通过 |
| `TwigSlimeM.STICKY_SHOT_MOVE` | 树枝史莱姆（中）：黏糊射击 | 向玩家弃牌堆加入 `1` 张黏液覆体 | 实机批量牌堆差分通过 |
| `TwigSlimeS.TACKLE_MOVE` | 树枝史莱姆（小）：冲撞 | 按意图执行单段攻击；测试难度下造成 `4` 点伤害 | 实机批量一步差分通过 |
| `TorchHeadAmalgam.STRONG_TACKLE_MOVE` | 火炬头聚合体：强力冲撞 | 按意图执行单段攻击；测试难度下造成 `26` 点伤害 | 实机批量一步差分通过 |
| `TorchHeadAmalgam.TACKLE_2_MOVE` | 火炬头聚合体：游戏无独立简中词条（`TACKLE_2_MOVE`） | 隐藏行动，造成 `18` 点伤害 | 实机批量一步差分通过 |
| `TorchHeadAmalgam.BEAM_MOVE` | 火炬头聚合体：光束 | 按意图执行三段攻击；测试难度下为 `3 × 8` 点伤害 | 实机批量一步差分通过 |
| `TorchHeadAmalgam.TACKLE_3_MOVE` | 火炬头聚合体：游戏无独立简中词条（`TACKLE_3_MOVE`） | 与弱冲撞回调共用，造成 `14` 点伤害 | 实机批量一步差分通过 |
| `TorchHeadAmalgam.TACKLE_4_MOVE` | 火炬头聚合体：游戏无独立简中词条（`TACKLE_4_MOVE`） | 与弱冲撞回调共用，造成 `14` 点伤害 | 实机批量一步差分通过 |
| `TurretOperator.UNLOAD_MOVE` | 高塔炮手：弹雨！ | 按意图执行五段攻击；测试难度下为 `5 × 3` 点伤害 | 实机批量一步差分通过 |
| `TurretOperator.UNLOAD_MOVE_2` | 高塔炮手：弹雨！（共用词条） | 与 `UNLOAD_MOVE` 共用回调并造成 `5 × 3` 点伤害 | 实机批量一步差分通过 |
| `TurretOperator.RELOAD_MOVE` | 高塔炮手：装弹 | 获得 `1` 点力量 | 实机批量一步差分通过 |

说明：卑鄙地精的醒来字段只影响表现；火炬头聚合体的仆从身份和末日死亡表现、高塔炮手获得力量后对未来攻击的通用力量结算分别属于其他 Power 或生命周期钩子，本批只证明表中行动回调与即时状态一致。

### `MONSTER-MOVES-BATCH-015`（16 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，在五个空槽位中召唤幽灵骑士、灵魂枢纽、鬼祟珊瑚群、史莱姆狂战士和骇鳗，在同一场战斗中连续强制执行十六个行动。固定伤害行动全部安排在玩家获得易伤之前；之后连续施加虚弱和易伤，比较其真实累计规则，并同时比较双方 Power 和玩家四个牌堆。

结果：`runId 7fb220b4e28a481791d94d034d5dd8ba` 返回 `Passed`，完整列出 `16` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_16` 至 `16_of_16`，总耗时约 `21.5s`。玩家虚弱按 `2 + 3 = 5`、易伤按 `2 + 99 = 101` 累计，弃牌堆新增 `10` 张黏液覆体。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `SpectralKnight.SOUL_SLASH` | 幽灵骑士：灵魂斩击 | 按意图执行单段攻击；测试难度下造成 `15` 点伤害 | 实机批量一步差分通过 |
| `SpectralKnight.SOUL_FLAME` | 幽灵骑士：灵魂火焰 | 按意图执行三段攻击；测试难度下为 `3 × 3` 点伤害 | 实机批量一步差分通过 |
| `SoulNexus.SOUL_BURN_MOVE` | 灵魂枢纽：灵魂灼烧 | 按意图执行单段攻击；测试难度下造成 `29` 点伤害 | 实机批量一步差分通过 |
| `SoulNexus.MAELSTROM_MOVE` | 灵魂枢纽：大漩涡 | 按意图执行四段攻击；测试难度下为 `4 × 6` 点伤害 | 实机批量一步差分通过 |
| `SoulNexus.DRAIN_LIFE_MOVE` | 灵魂枢纽：汲取生命 | 造成 `18` 点伤害后给玩家 `2` 层易伤和 `2` 层虚弱 | 实机批量一步差分通过 |
| `SkulkingColony.ZOOM_MOVE` | 鬼祟珊瑚群：猛冲 | 按意图执行单段攻击；测试难度下造成 `14` 点伤害 | 实机批量一步差分通过 |
| `SkulkingColony.ZOOM_MOVE_2` | 鬼祟珊瑚群：猛冲（共用词条） | 与 `ZOOM_MOVE` 共用回调并造成 `14` 点伤害 | 实机批量一步差分通过 |
| `SkulkingColony.PIERCING_STABS_MOVE` | 鬼祟珊瑚群：穿刺戳击 | 按意图执行两段攻击；测试难度下为 `2 × 7` 点伤害 | 实机批量一步差分通过 |
| `SkulkingColony.INERTIA_MOVE` | 鬼祟珊瑚群：惯性 | 造成 `9` 点伤害后按实时 `InertiaStrengthGain` 获得 `2` 力量 | 实机批量一步差分通过 |
| `SlimedBerserker.FURIOUS_PUMMELING_MOVE` | 史莱姆狂战士：狂怒连击 | 按意图执行四段攻击；测试难度下为 `4 × 4` 点伤害 | 实机批量一步差分通过 |
| `SlimedBerserker.SMOTHER_MOVE` | 史莱姆狂战士：游戏无独立简中词条（`SMOTHER_MOVE`） | 隐藏行动，仅执行单段攻击；测试难度下造成 `30` 点伤害 | 实机批量一步差分通过 |
| `SlimedBerserker.VOMIT_ICHOR_MOVE` | 史莱姆狂战士：喷吐脓水 | 向玩家弃牌堆加入 `10` 张黏液覆体 | 实机批量牌堆差分通过 |
| `SlimedBerserker.LEECHING_HUG_MOVE` | 史莱姆狂战士：汲取之拥 | 给玩家 `3` 层虚弱并给自身 `3` 点力量；测试中玩家虚弱从 `2` 累计到 `5` | 实机批量累计状态差分通过 |
| `TerrorEel.CRASH_MOVE` | 骇鳗：撞击 | 按意图执行单段攻击；测试难度下造成 `16` 点伤害 | 实机批量一步差分通过 |
| `TerrorEel.STUN_MOVE` | 骇鳗：游戏无独立简中词条（`STUN_MOVE`） | 隐藏眩晕回调为空，不改变战斗数值 | 实机批量一步差分通过 |
| `TerrorEel.TERROR_MOVE` | 骇鳗：恐吓 | 给玩家 `99` 层易伤；测试中从既有 `2` 层累计到 `101` | 实机批量累计状态差分通过 |

说明：幽灵骑士的妖术会给战斗牌施加苦难并改变虚无关键词，骇鳗的撕扯会留下下一次攻击消耗的活力，这两个行动没有纳入本批。鬼祟珊瑚群的硬化外壳、骇鳗的尖啸及其眩晕转换也都是独立 Power 生命周期，不能借本批行动差分标记为通过。

### `MONSTER-MOVES-BATCH-014`（10 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，在五个空槽位中召唤棘刺蟾蜍、戳刺机器人、咬人卷轴、蛇行扼杀者和闪光贾克斯果，在同一场战斗中连续强制执行十个行动。每项比较生产预测与真实 `MonsterModel.PerformMove()` 后的生命、格挡、双方 Power 和四个玩家牌堆；棘刺蟾蜍依次执行伸出尖刺与尖刺爆破，直接比较荆棘的增加和归零移除。

结果：`runId 19c89b736bc645a79d9964a4a5842346` 返回 `Passed`，完整列出 `10` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_10` 至 `10_of_10`，总耗时约 `17.7s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `SpinyToad.TONGUE_LASH_MOVE` | 棘刺蟾蜍：吐舌 | 按意图执行单段攻击；测试难度下造成 `17` 点伤害 | 实机批量一步差分通过 |
| `SpinyToad.PROTRUDING_SPIKES_MOVE` | 棘刺蟾蜍：伸出尖刺 | 获得 `5` 层荆棘；`IsSpiny` 只选择受击、死亡和动画表现 | 实机批量一步差分通过 |
| `SpinyToad.SPIKE_EXPLOSION_MOVE` | 棘刺蟾蜍：尖刺爆破 | 造成 `23` 点伤害并移除先前的 `5` 层荆棘 | 实机批量状态移除差分通过 |
| `Stabbot.STAB_MOVE` | 戳刺机器人：戳刺 | 造成 `11` 点伤害后给玩家 `1` 层脆弱 | 实机批量一步差分通过 |
| `ScrollOfBiting.CHOMP` | 咬人卷轴：大啃 | 按意图执行单段攻击；测试难度下造成 `14` 点伤害 | 实机批量一步差分通过 |
| `ScrollOfBiting.CHEW` | 咬人卷轴：咀嚼 | 按意图执行两段攻击；测试难度下为 `2 × 5` 点伤害 | 实机批量一步差分通过 |
| `ScrollOfBiting.MORE_TEETH` | 咬人卷轴：更多牙齿 | 获得 `2` 点力量 | 实机批量一步差分通过 |
| `SlitheringStrangler.LASH` | 蛇行扼杀者：甩动 | 按意图执行单段攻击；测试难度下造成 `12` 点伤害 | 实机批量一步差分通过 |
| `SlitheringStrangler.THWACK` | 蛇行扼杀者：重击 | 造成 `7` 点伤害并获得 `5` 格挡 | 实机批量一步差分通过 |
| `SnappingJaxfruit.ENERGY_ORB_MOVE` | 闪光贾克斯果：能量球 | 造成 `3` 点伤害并获得 `2` 点力量 | 实机批量一步差分通过 |

说明：本批只验证棘刺蟾蜍行动对荆棘层数的增减，不借此标记荆棘反伤生命周期通过；咬人卷轴的纸割、蛇行扼杀者的束缚及其回合结束伤害也都是独立 Power 钩子，仍需单独适配和闭环。

### `MONSTER-MOVES-BATCH-013`（9 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，在五个空槽位中召唤盛碗虫（卵）、钙化邪教徒、仪式兽、利齿之眼和直飞产卵虫，在同一场战斗中连续强制执行九个行动。每项比较生产预测与真实 `MonsterModel.PerformMove()` 后的生命、格挡、双方 Power 和四个玩家牌堆；仪式兽三次行动使用同一实例，以验证力量对后续伤害及层数累计的影响。

首次 `runId 44e870953e7b4c2ca807f599515331eb` 在第 `8/9` 项失败，原因为夹具把 `Discard:DAZED` 写成了大小写不符的 `DISCARD:DAZED`。该次 `MOVE_DIFF` 中预测和真实实际上都已有 `3` 张晕眩，因此不把它描述为求解语义失败。修正夹具后，`runId 09a50208c651431684f9ec62e2be1b30` 返回 `Passed`，完整列出 `9` 个 `completedChecks`，总耗时约 `19.2s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `BowlbugEgg.BITE_MOVE` | 盛碗虫（卵）：啃咬 | 造成 `7` 点伤害并获得 `7` 格挡 | 实机批量一步差分通过 |
| `CalcifiedCultist.INCANTATION_MOVE` | 钙化邪教徒：念咒 | 按实时 `IncantationAmount` 获得仪式；测试中获得 `2` 层 | 实机批量一步差分通过 |
| `CeremonialBeast.STOMP_MOVE` | 仪式兽：踩踏 | 按意图执行单段攻击；测试难度下造成 `15` 点伤害 | 实机批量一步差分通过 |
| `CeremonialBeast.CRUSH_MOVE` | 仪式兽：碾碎 | 攻击后按实时 `CrushStrength` 获得力量；测试中造成 `17` 点伤害并获得 `3` 力量 | 实机批量一步差分通过 |
| `CeremonialBeast.PLOW_MOVE` | 仪式兽：横冲直撞 | 攻击后按实时 `PlowStrength` 获得力量；受之前 `3` 力量影响造成 `21` 点伤害，随后力量累计到 `5` | 实机批量累计状态差分通过 |
| `EyeWithTeeth.DISTRACT_MOVE` | 利齿之眼：牵制 | 向玩家弃牌堆加入 `3` 张晕眩 | 实机批量牌堆差分通过 |
| `Ovicopter.SMASH_MOVE` | 直飞产卵虫：猛砸 | 按意图执行单段攻击；测试难度下造成 `16` 点伤害 | 实机批量一步差分通过 |
| `Ovicopter.TENDERIZER_MOVE` | 直飞产卵虫：嫩化 | 攻击后给玩家 `2` 层易伤；测试中造成 `7` 点伤害 | 实机批量一步差分通过 |
| `Ovicopter.NUTRITIONAL_PASTE_MOVE` | 直飞产卵虫：高营养糊糊 | 按实时 `NutritionalPasteStrengthAmount` 获得力量；测试中获得 `3` 点力量 | 实机批量一步差分通过 |

说明：本批不包含仪式兽的耕犁 Power、受移除后眩晕及二阶段状态，也不包含利齿之眼的幻象复活或直飞产卵虫的产卵召唤；这些属于独立生命周期或召唤语义，不能借本批标记为通过。

### `MONSTER-MOVES-BATCH-012`（11 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，在五个空槽位中分别召唤盛碗虫（丝）、盛碗虫（蜜）、噬尸蛞蝓、灵魂异鱼和失落之物，在同一场战斗中连续强制执行十一个行动。每项比较生产预测与真实 `MonsterModel.PerformMove()` 后的生命、格挡、双方 Power 和四个玩家牌堆；攻击行动安排在易伤生效前，避免累计状态改变固定伤害断言。

结果：`runId fd60800098b64712b58ca1e269224423` 返回 `Passed`，完整列出 `11` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_11` 至 `11_of_11`，总耗时约 `17.3s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `BowlbugSilk.THRASH_MOVE` | 盛碗虫（丝）：撕扯 | 按意图执行两段攻击；测试难度下为 `2 × 4` 点伤害 | 实机批量一步差分通过 |
| `BowlbugSilk.TOXIC_SPIT_MOVE` | 盛碗虫（丝）：毒性喷吐 | 给玩家 `1` 层虚弱 | 实机批量一步差分通过 |
| `BowlbugNectar.THRASH_MOVE` | 盛碗虫（蜜）：撕扯 | 按意图执行单段攻击；测试难度下造成 `3` 点伤害 | 实机批量一步差分通过 |
| `BowlbugNectar.THRASH2_MOVE` | 盛碗虫（蜜）：撕扯（共用词条） | 与 `THRASH_MOVE` 共用回调并造成 `3` 点伤害；游戏 PCK 没有独立 `THRASH2` 词条 | 实机批量一步差分通过 |
| `BowlbugNectar.BUFF_MOVE` | 盛碗虫（蜜）：强化 | 按实时 `BuffStrengthGain` 获得力量；测试中获得 `15` 点力量 | 实机批量一步差分通过 |
| `CorpseSlug.WHIP_SLAP_MOVE` | 噬尸蛞蝓：鞭打 | 按意图执行两段攻击；测试难度下为 `2 × 3` 点伤害 | 实机批量一步差分通过 |
| `CorpseSlug.GLOMP_MOVE` | 噬尸蛞蝓：扑上 | 按意图执行单段攻击；测试难度下造成 `8` 点伤害 | 实机批量一步差分通过 |
| `CorpseSlug.GOOP_MOVE` | 噬尸蛞蝓：黏液 | 按实时 `GoopFrailAmt` 给玩家施加脆弱；测试中施加 `2` 层 | 实机批量一步差分通过 |
| `SoulFysh.SCREAM_MOVE` | 灵魂异鱼：尖叫 | 攻击后按实时 `ScreamMoveAmount` 给玩家施加易伤；测试中造成 `13` 点伤害并施加 `3` 层 | 实机批量一步差分通过 |
| `TheLost.EYE_LASERS` | 失落之物：眼部激光 | 按意图执行两段攻击；测试难度下为 `2 × 4` 点伤害 | 实机批量一步差分通过 |
| `TheLost.DEBILITATING_SMOG` | 失落之物：致残雾霾 | 从玩家移除实时字段指定的力量，并给自身增加等量力量；测试中玩家 `-2`、怪物 `+2` | 实机批量一步差分通过 |

说明：噬尸蛞蝓的贪食生命周期、灵魂异鱼其他阶段及无实体、失落之物的附身力量入场行为都是独立钩子，本批只验证表中行动，不把这些生命周期效果标记为通过。

### `MONSTER-MOVES-BATCH-011`（13 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，在五个空槽位中分别召唤海洋混混、下水道蚌、缩小甲虫、淤泥旋螺和扭动虫，连续强制执行十三个行动。每项比较生产预测与真实 `MonsterModel.PerformMove()` 后的生命、格挡、双方 Power 和四个玩家牌堆；扭动虫使用公开 `ForceCurrentState` 越过遭遇专用初始槽位分支。

结果：`runId 024b04c7b87a426ca5bcfa3554f54dea` 返回 `Passed`，完整列出 `13` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_13` 至 `13_of_13`，总耗时约 `18.5s`。本批发现并修复旧实现遗漏：扭动原本只加力量，现在同时向弃牌堆加入感染。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Seapunk.SEA_KICK_MOVE` | 海洋混混：海洋踢 | 按意图执行单段攻击；测试难度下造成 `11` 点伤害 | 实机批量一步差分通过 |
| `Seapunk.SPINNING_KICK_MOVE` | 海洋混混：回旋踢 | 按意图执行四段攻击；测试难度下为 `4 × 2` 点伤害 | 实机批量一步差分通过 |
| `Seapunk.BUBBLE_BURP_MOVE` | 海洋混混：吐泡泡 | 按实时字段获得格挡和力量；测试中获得 `7` 格挡、`1` 力量 | 实机批量一步差分通过 |
| `SewerClam.JET_MOVE` | 下水道蚌：喷射 | 按意图执行单段攻击；测试难度下造成 `10` 点伤害 | 实机批量一步差分通过 |
| `SewerClam.PRESSURIZE_MOVE` | 下水道蚌：增压 | 获得 `4` 点力量 | 实机批量一步差分通过 |
| `ShrinkerBeetle.CHOMP_MOVE` | 缩小甲虫：大啃 | 按意图执行单段攻击；测试难度下造成 `7` 点伤害 | 实机批量一步差分通过 |
| `ShrinkerBeetle.STOMP_MOVE` | 缩小甲虫：践踏 | 按意图执行单段攻击；测试难度下造成 `13` 点伤害 | 实机批量一步差分通过 |
| `SludgeSpinner.OIL_SPRAY_MOVE` | 淤泥旋螺：喷油 | 攻击后给玩家 `1` 层虚弱；测试中造成 `8` 点伤害 | 实机批量一步差分通过 |
| `SludgeSpinner.SLAM_MOVE` | 淤泥旋螺：砸击 | 按意图执行单段攻击；测试难度下造成 `11` 点伤害 | 实机批量一步差分通过 |
| `SludgeSpinner.RAGE_MOVE` | 淤泥旋螺：狂怒 | 攻击后获得 `3` 点力量；测试中造成 `6` 点伤害 | 实机批量一步差分通过 |
| `Wriggler.NASTY_BITE_MOVE` | 扭动虫：污秽啃咬 | 按意图执行单段攻击；测试难度下造成 `6` 点伤害 | 实机批量一步差分通过 |
| `Wriggler.SPAWNED_MOVE` | 扭动虫：生成 | 回调为空，不改变战斗数值 | 实机批量一步差分通过 |
| `Wriggler.WRIGGLE_MOVE` | 扭动虫：扭动 | 向玩家弃牌堆加入 `1` 张感染，并给自身 `2` 点力量；感染的回合结束伤害由 RF 精确镜像 | 实机批量牌堆差分通过 |

说明：`ShrinkerBeetle.SHRINKER_MOVE` 会施加无限持续的 `ShrinkPower`；其伤害倍率、回合结束和施法者死亡移除尚未适配，所以没有借本批两次攻击标记为通过。下水道蚌的镀层同样属于独立 Power 生命周期。

### `MONSTER-MOVES-BATCH-010`（16 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，在五个空槽位中分别召唤魔法骑士、机甲骑士、拳击构装体、寄生惧魔和花园幽灵鳗，连续强制执行十六个行动。每项比较生产预测与真实 `MonsterModel.PerformMove()` 后的生命、格挡、双方 Power 及四个玩家牌堆；花园幽灵鳗使用公开 `ForceCurrentState` 越过只认专用遭遇槽位的初始条件分支。

结果：`runId cf628c8d1e2f4261b0fbe69f98ab0d5b` 返回 `Passed`，完整列出 `16` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_16` 至 `16_of_16`，总耗时约 `21.0s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `MagiKnight.MAGIC_BOMB` | 魔法骑士：魔法炸弹 | 按意图执行单段攻击；测试难度下造成 `35` 点伤害 | 实机批量一步差分通过 |
| `MagiKnight.RAM_MOVE` | 魔法骑士：撞击 | 按意图执行单段攻击；测试难度下造成 `10` 点伤害 | 实机批量一步差分通过 |
| `MagiKnight.POWER_SHIELD_MOVE` | 魔法骑士：强力护盾 | 攻击后按实时 `PowerShieldBlock` 获得格挡；测试中造成 `6` 点伤害并获得 `5` 格挡 | 实机批量一步差分通过 |
| `MagiKnight.PREP_MOVE` | 魔法骑士：游戏无独立简中词条（`PREP_MOVE`） | 按实时 `PowerShieldBlock` 获得格挡；测试中获得 `5` 格挡 | 实机批量一步差分通过 |
| `MechaKnight.CHARGE_MOVE` | 机甲骑士：冲锋 | 按意图执行单段攻击；测试难度下造成 `25` 点伤害 | 实机批量一步差分通过 |
| `MechaKnight.FLAMETHROWER_MOVE` | 机甲骑士：喷火器 | 攻击后向玩家手牌加入 `4` 张灼傷；测试中造成 `8` 点伤害 | 实机批量牌堆差分通过 |
| `MechaKnight.HEAVY_CLEAVE_MOVE` | 机甲骑士：重斩 | 按意图执行单段攻击；测试难度下造成 `35` 点伤害；`IsWoundUp` 只选择表现 | 实机批量一步差分通过 |
| `MechaKnight.WINDUP_MOVE` | 机甲骑士：举起蓄力 | 获得 `15` 格挡和 `5` 点力量；`IsWoundUp` 只选择表现 | 实机批量一步差分通过 |
| `PunchConstruct.STRONG_PUNCH_MOVE` | 拳击构装体：强力拳 | 按意图执行单段攻击；测试难度下造成 `14` 点伤害 | 实机批量一步差分通过 |
| `PunchConstruct.FAST_PUNCH_MOVE` | 拳击构装体：快速拳 | 按意图执行两段攻击后给玩家 `1` 层脆弱；测试中为 `2 × 5` 点伤害 | 实机批量一步差分通过 |
| `PunchConstruct.READY_MOVE` | 拳击构装体：准备就绪 | 获得 `10` 格挡 | 实机批量一步差分通过 |
| `Parafright.SLAM_MOVE` | 寄生惧魔：砸击 | 按意图执行单段攻击；测试难度下造成 `16` 点伤害 | 实机批量一步差分通过 |
| `PhantasmalGardener.BITE_MOVE` | 花园幽灵鳗：啃咬 | 按意图执行单段攻击；测试难度下造成 `5` 点伤害 | 实机批量一步差分通过 |
| `PhantasmalGardener.LASH_MOVE` | 花园幽灵鳗：甩动 | 按意图执行单段攻击；测试难度下造成 `7` 点伤害 | 实机批量一步差分通过 |
| `PhantasmalGardener.FLAIL_MOVE` | 花园幽灵鳗：猛晃 | 按意图执行三段攻击；测试难度下为 `3 × 1` 点伤害 | 实机批量一步差分通过 |
| `PhantasmalGardener.ENLARGE_MOVE` | 花园幽灵鳗：变大 | 按实时 `EnlargeStr` 获得力量；测试中获得 `2` 点力量；缩放计数只影响表现 | 实机批量一步差分通过 |

说明：`MagiKnight.DAMPEN_MOVE` 会维护 `DampenPower` 的施法者集合，本批没有实现或测试；寄生惧魔的幻象复活、花园幽灵鳗的闪避格挡属于独立 Power 生命周期，也没有借行动结果标记为通过。

### `MONSTER-MOVES-BATCH-009`（13 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，在同一战斗中召唤活体盾、蛮兽、异螨、小啃兽和啃咬机，连续强制执行十三个行动。每项比较生产求解器一步预测与真实 `MonsterModel.PerformMove()` 后的生命、格挡、双方 Power 和四个玩家牌堆。异螨沿用场景合法槽位，在原版状态机初始化后通过公开 `ForceCurrentState` 指定出生初始行动，避免把其只接受 `first`/`second` 的遭遇专用初始分支误当成生产语义。

前三次运行均在 `inject_state` 阶段失败且 `completedChecks` 为空：`c5cb0f26a82140388b5ed0342129a21c` 暴露异螨初始条件分支不接受通用槽位，`f679c4dc118244619bc8cc8aaeeccbfe` 暴露伪造 `first` 槽位没有对应场景节点，`d5fc7ecf0551409dba880301e1365519` 暴露状态机要到原版加怪流程中才初始化。修正测试夹具后，`runId e492717b9f4f402198cb8f826fb3b8e5` 返回 `Passed`，日志包含 `monster_move_differential_1_of_13` 至 `13_of_13`，总耗时约 `19.2s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `LivingShield.SHIELD_SLAM_MOVE` | 活体盾：盾击 | 按意图执行单段攻击；测试难度下造成 `6` 点伤害 | 实机批量一步差分通过 |
| `LivingShield.SMASH_MOVE` | 活体盾：砸击 | 攻击后获得 `3` 点力量；测试难度下造成 `16` 点伤害，入场盾墙保持一致 | 实机批量一步差分通过 |
| `Mawler.RIP_AND_TEAR_MOVE` | 蛮兽：狂乱撕扯 | 按意图执行单段攻击；测试难度下造成 `14` 点伤害 | 实机批量一步差分通过 |
| `Mawler.CLAW_MOVE` | 蛮兽：爪击 | 按意图执行两段攻击；测试难度下为 `2 × 4` 点伤害 | 实机批量一步差分通过 |
| `Mawler.ROAR_MOVE` | 蛮兽：怒吼 | 给玩家施加 `3` 层易伤 | 实机批量一步差分通过 |
| `Myte.BITE_MOVE` | 异螨：啃咬 | 按意图执行单段攻击；测试难度下造成 `13` 点伤害 | 实机批量一步差分通过 |
| `Myte.SUCK_MOVE` | 异螨：吸吮 | 攻击后按实时 `SuckStrength` 获得力量；测试中造成 `4` 点伤害并获得 `2` 点力量 | 实机批量一步差分通过 |
| `Myte.TOXIC_MOVE` | 异螨：浓毒 | 向玩家手牌加入 `2` 张劇毒 | 实机批量牌堆差分通过 |
| `Nibbit.BUTT_MOVE` | 小啃兽：顶撞 | 按意图执行单段攻击；测试难度下造成 `12` 点伤害 | 实机批量一步差分通过 |
| `Nibbit.SLICE_MOVE` | 小啃兽：游戏无独立简中词条（`SLICE_MOVE`） | 攻击后按实时 `SliceBlock` 获得格挡；测试中造成 `6` 点伤害并获得 `5` 格挡 | 实机批量一步差分通过 |
| `Nibbit.HISS_MOVE` | 小啃兽：哈气 | 按实时 `HissStrengthGain` 获得力量；测试中获得 `2` 点力量 | 实机批量一步差分通过 |
| `Chomper.CLAMP_MOVE` | 啃咬机：猛夹 | 按意图执行两段攻击；测试难度下为 `2 × 8` 点伤害 | 实机批量一步差分通过 |
| `Chomper.SCREECH_MOVE` | 啃咬机：尖锐鸣叫 | 向玩家弃牌堆加入 `3` 张暈眩 | 实机批量牌堆差分通过 |

### `MONSTER-MOVES-BATCH-008`（9 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，召唤乐加维林族母、树叶史莱姆（中）和树叶史莱姆（小）后连续强制执行九个行动。测试同时比较正负 Power 和玩家牌堆计数；族母入场的沉睡/镀层保持不变，两种史莱姆生成的黏液在同一弃牌堆累计。

结果：`runId 3346be328b104d048649664db20161f8` 返回 `Passed`，完整列出 `9` 个 `completedChecks`；弃牌堆黏液覆體计数按 `0 → 2 → 3` 推进，总耗时约 `17.3s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `LagavulinMatriarch.SLEEP_MOVE` | 乐加维林族母：沉睡 | 行动回调为空，不改变战斗状态；入场沉睡和镀层属于独立生命周期 | 实机批量一步差分通过 |
| `LagavulinMatriarch.SLASH_MOVE` | 乐加维林族母：斩击 | 按意图执行单段攻击；测试难度下造成 `19` 点伤害 | 实机批量一步差分通过 |
| `LagavulinMatriarch.DISEMBOWEL_MOVE` | 乐加维林族母：开膛破肚 | 按意图执行两段攻击；测试难度下为 `2 × 9` 点伤害 | 实机批量一步差分通过 |
| `LagavulinMatriarch.SLASH2_MOVE` | 乐加维林族母：游戏无独立简中词条（`SLASH2_MOVE`） | 攻击后按实时 `Slash2Block` 获得格挡；测试中造成 `12` 点伤害并获得 `12` 格挡 | 实机批量一步差分通过 |
| `LagavulinMatriarch.SOUL_SIPHON_MOVE` | 乐加维林族母：灵魂汲取 | 给玩家 `-2` 力量和 `-2` 敏捷，再给自身 `+2` 力量 | 实机批量一步差分通过 |
| `LeafSlimeM.CLUMP_SHOT` | 树叶史莱姆（中）：团块射击 | 按意图执行单段攻击；测试难度下造成 `8` 点伤害 | 实机批量一步差分通过 |
| `LeafSlimeM.STICKY_SHOT` | 树叶史莱姆（中）：黏糊射击 | 向玩家弃牌堆加入 `2` 张黏液覆體 | 实机批量牌堆差分通过 |
| `LeafSlimeS.TACKLE_MOVE` | 树叶史莱姆（小）：冲撞 | 按意图执行单段攻击；测试难度下造成 `3` 点伤害 | 实机批量一步差分通过 |
| `LeafSlimeS.GOOP_MOVE` | 树叶史莱姆（小）：黏液 | 向玩家弃牌堆加入 `1` 张黏液覆體 | 实机批量牌堆差分通过 |

说明：`AsleepPower` 的受伤唤醒、镀层移除和回合递减属于独立 Power 生命周期，本批没有借行动差分标记为通过。

### `MONSTER-MOVES-BATCH-007`（10 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，召唤同族信徒、同族神官和知识恶魔后连续强制执行十个行动。每项比较生产预测与真实 `MonsterModel.PerformMove()`；“思考”除既有状态字段外，还显式断言知识恶魔自身生命增加 `30`。

结果：`runId 5c3ca152159a44858940d7f6fed0515c` 返回 `Passed`，结果文件完整列出 `10` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_10` 至 `10_of_10`，总耗时约 `18.6s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `KinFollower.QUICK_SLASH_MOVE` | 同族信徒：快斩 | 按意图执行单段攻击；测试难度下造成 `5` 点伤害 | 实机批量一步差分通过 |
| `KinFollower.BOOMERANG_MOVE` | 同族信徒：回旋镖 | 按意图执行两段攻击；测试难度下为 `2 × 2` 点伤害，位移逻辑只属于表现 | 实机批量一步差分通过 |
| `KinFollower.POWER_DANCE_MOVE` | 同族信徒：力量之舞 | 按实时 `DanceStrength` 获得力量；测试难度下获得 `2` 点力量 | 实机批量一步差分通过 |
| `KinPriest.ORB_OF_FRAILTY_MOVE` | 同族神官：脆弱法球 | 攻击后给玩家 `1` 层脆弱；测试难度下造成 `8` 点伤害 | 实机批量一步差分通过 |
| `KinPriest.ORB_OF_WEAKNESS_MOVE` | 同族神官：虚弱法球 | 攻击后给玩家 `1` 层虚弱；测试难度下造成 `8` 点伤害 | 实机批量一步差分通过 |
| `KinPriest.BEAM_MOVE` | 同族神官：灵魂光束 | 按意图执行三段攻击；测试难度下为 `3 × 3` 点伤害 | 实机批量一步差分通过 |
| `KinPriest.RITUAL_MOVE` | 同族神官：黑暗仪式 | 按实时 `RitualStrength` 获得力量；测试难度下获得 `2` 点力量，对白标记只影响表现 | 实机批量一步差分通过 |
| `KnowledgeDemon.SLAP_MOVE` | 知识恶魔：抽打 | 按意图执行单段攻击；测试难度下造成 `17` 点伤害 | 实机批量一步差分通过 |
| `KnowledgeDemon.KNOWLEDGE_OVERWHELMING_MOVE` | 知识恶魔：知识过载 | 按意图执行三段攻击；测试难度下为 `3 × 8` 点伤害，焦黑标记只选择表现 | 实机批量一步差分通过 |
| `KnowledgeDemon.PONDER_MOVE` | 知识恶魔：思考 | 先攻击，再按玩家数回复 `30` 点生命，最后按实时 `PonderStrength` 获得力量；测试中造成 `11` 点伤害、从 `1 HP` 回复至 `31 HP`、获得 `2` 力量 | 实机批量一步差分通过 |

说明：`KnowledgeDemon.CURSE_OF_KNOWLEDGE_MOVE` 仍以 `DynamicResolution` 结束静态路线，因为原生选择会推进私有三阶段计数器；部署守卫现识别其不可跳过但传入 `minSelect=0` 的 `IChoosable`，强制选择一张并按低伤害顺序处理。`runId cda7d949557940c2933303cb7089184a` 已真实自动选择 `MIND_ROT`、施加 `MIND_ROT_POWER` 并退出到重搜，全程无玩家干预。`KinPriest.AfterDeath(...)` 的信徒死亡监听仍属于独立生命周期钩子。

### `MONSTER-MOVES-BATCH-006`（8 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，召唤守护机器人、感染棱柱和墨宝，并额外召唤不执行行动的组装师作为守护目标。测试连续强制执行八个行动；除生命、目标格挡、双方 Power 和玩家牌堆外，本批新增按怪物模型汇总比较全场敌人格挡，以覆盖“给其他怪物格挡”的语义。

首次 `runId dda1c35296874e5e816028d44cf5854e` 在第 `1` 项失败：测试夹具没有召唤组装师，日志快照也确实不存在 `FABRICATOR`，因此守护没有合法目标。增加 `additionalMonsterIds` 后重新运行，`runId 6511dded0d1a47e09d723a2fd33aeee6` 返回 `Passed`，完整列出 `8` 个 `completedChecks`，耗时约 `15.8s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Guardbot.GUARD_MOVE` | 守护机器人：守护 | 给场上每个组装师 `15` 点无来源格挡；不增加自身格挡 | 实机批量多目标差分通过 |
| `InfestedPrism.JAB_MOVE` | 感染棱柱：刺击 | 按意图执行单段攻击；测试难度下造成 `15` 点伤害 | 实机批量一步差分通过 |
| `InfestedPrism.WHIRLWIND_MOVE` | 感染棱柱：旋风 | 按意图执行三段攻击；测试难度下为 `3 × 5` 点伤害 | 实机批量一步差分通过 |
| `InfestedPrism.RADIATE_MOVE` | 感染棱柱：辐射 | 攻击后按实时 `RadiateBlock` 获得格挡；测试中造成 `11` 点伤害并获得 `11` 格挡 | 实机批量一步差分通过 |
| `InfestedPrism.PULSATE_MOVE` | 感染棱柱：脉动 | 攻击后按实时 `PulsateBlock` 获得格挡，并按实时 `VitalSparkAmount` 增加活力火花；测试中造成 `8` 点伤害、获得 `20` 格挡，活力火花从 `2` 增至 `4` | 实机批量一步差分通过 |
| `Inklet.JAB_MOVE` | 墨宝：刺击 | 按意图执行单段攻击；测试难度下造成 `3` 点伤害 | 实机批量一步差分通过 |
| `Inklet.WHIRLWIND_MOVE` | 墨宝：旋风 | 按意图执行三段攻击；测试难度下为 `3 × 2` 点伤害 | 实机批量一步差分通过 |
| `Inklet.PIERCING_GAZE_MOVE` | 墨宝：锐利凝视 | 按意图执行单段攻击；测试难度下造成 `10` 点伤害 | 实机批量一步差分通过 |

说明：本批证明脉动正确增加活力火花，并确认墨宝行动不会改写滑溜。后续 `runId 0de758b6b69a423f84aa3f5476179fb7` 又在原生感染棱柱战连续执行辐射与脉动：模拟保持玩家技能牌已有污染，并在活力火花 `2→4` 时把全部逐牌污染同步到 `4`，两步完整牌堆、Power 与 RNG 差分一致。滑溜仍按自己的独立生命周期证据计算。

### `MONSTER-MOVES-BATCH-005`（6 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，真实召唤幽灵船和猎人杀手后连续强制执行六个行动。每项先调用生产求解器的一步语义，再执行真实 `MonsterModel.PerformMove()`；除生命、格挡和双方 Power 外，本批新增比较玩家抽牌堆、手牌、弃牌堆及消耗牌堆的卡牌计数。项目之间只恢复玩家生命，整批完成后统一退出。

结果：`runId 59af848e8500463b8cdddacc540404dc` 返回 `Passed`，结果文件完整列出 `6` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_6` 至 `6_of_6` 的六条 `MOVE_DIFF`，总耗时约 `16.1s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `HauntedShip.SWIPE_MOVE` | 幽灵船：扫击 | 按意图执行单段攻击；测试难度下造成 `13` 点伤害 | 实机批量一步差分通过 |
| `HauntedShip.STOMP_MOVE` | 幽灵船：践踏 | 按意图执行三段攻击；测试难度下为 `3 × 4` 点伤害 | 实机批量一步差分通过 |
| `HauntedShip.HAUNT_MOVE` | 幽灵船：纠缠 | 给玩家 `3` 层虚弱，并向弃牌堆加入 `5` 张暈眩 | 实机批量差分通过，包含牌堆计数 |
| `HunterKiller.BITE_MOVE` | 猎人杀手：啃咬 | 按意图执行单段攻击；测试难度下造成 `17` 点伤害 | 实机批量一步差分通过 |
| `HunterKiller.PUNCTURE_MOVE` | 猎人杀手：刺穿 | 按意图执行三段攻击；测试难度下为 `3 × 7` 点伤害 | 实机批量一步差分通过 |
| `HunterKiller.TENDERIZING_GOOP_MOVE` | 猎人杀手：嫩化黏液 | 给玩家 `1` 层柔嫩 | 实机批量一步差分通过 |

说明：本批只证明“嫩化黏液”正确施加柔嫩；柔嫩的出牌后力量/敏捷变化及回合结束恢复属于独立 Power 生命周期钩子，尚未借本条标记为通过。

### `MONSTER-MOVES-BATCH-004`（7 项）

闭环：单次启动真实可见游戏并进入 `LivingFogNormal`，由无人测试脚手架按需真实召唤青蛙骑士、电球头和气态炸弹。测试连续强制执行七个行动；每项先调用生产求解器的一步行动语义，再执行真实 `MonsterModel.PerformMove()`，逐字段比较玩家与目标怪物的生命、格挡及双方 Power。项目之间只恢复玩家生命，整批完成后统一退出游戏。

结果：`runId c5db80f6ea934efeaa30a63203da5079` 返回 `Passed`，结果文件完整列出 `7` 个 `completedChecks`；日志包含 `monster_move_differential_1_of_7` 至 `7_of_7` 的七条 `MOVE_DIFF`，总耗时约 `15.5s`。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `FrogKnight.STRIKE_DOWN_EVIL` | 青蛙骑士：惩恶一击 | 按意图执行单段攻击；测试难度下造成 `21` 点伤害 | 实机批量一步差分通过 |
| `FrogKnight.TONGUE_LASH` | 青蛙骑士：吐舌 | 按意图攻击后给玩家 `2` 层脆弱；测试难度下造成 `13` 点伤害 | 实机批量一步差分通过 |
| `FrogKnight.FOR_THE_QUEEN` | 青蛙骑士：为了女王 | 怪物获得 `5` 点力量 | 实机批量一步差分通过 |
| `GlobeHead.THUNDER_STRIKE` | 电球头：生成闪电 | 按意图执行三段攻击；测试难度下为 `3 × 6` 点伤害 | 实机批量一步差分通过 |
| `GlobeHead.SHOCKING_SLAP` | 电球头：电击掌 | 按意图攻击后给玩家 `2` 层脆弱；测试难度下造成 `13` 点伤害 | 实机批量一步差分通过 |
| `GlobeHead.GALVANIC_BURST` | 电球头：游戏无独立简中词条（`GALVANIC_BURST`） | 按意图攻击后获得 `2` 点力量；测试难度下造成 `16` 点伤害 | 实机批量一步差分通过 |
| `GasBomb.EXPLODE_MOVE` | 气态炸弹：爆炸 | 按意图攻击后强制自身死亡；测试难度下造成 `8` 点伤害 | 实机批量一步差分通过 |

说明：`FrogKnight.BEETLE_CHARGE` 会写入影响未来条件分支的实例状态，本批没有适配或测试该行动，不能借用同一怪物的其他测试结论。

### `MONSTER-WATERFALL-001`

闭环：Steam 启动真实可见游戏，进入 `WATERFALL_GIANT_BOSS`，将瀑布巨兽设为 `1 HP` 并施加 `SteamEruptionPower:10`，由全自动真实打出 Strike。测试持续等待真实行动队列，核对提前致死、蓄爆、爆炸自灭、战斗结束回合及下一回合预测复用。

结果：`0.7.0` 最终 Release 的 `runId dac1ef6be32a4545a32d218ec19659cb` 返回 `Passed`；提前致死没有立即结束战斗，真实执行 `ABOUT_TO_BLOW_MOVE`、`EXPLODE_MOVE`，战斗严格在第 `2` 回合结束。首轮搜索为 `expanded=46`、`replays=116`、`worker_allocated_bytes=74649456`、`elapsed_ms=428`，低于发布上限 `580 replays / 450MB`；第二回合完整状态复用为 `expanded=0`、`replays=0`、`worker_allocated_bytes=0`、`elapsed_ms=0`。日志确认搜索工作线程 `main_thread=False`。旧 `runId 722edc63ce684be1a322df0ee88e1423` 也通过。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `WaterfallGiant.ABOUT_TO_BLOW_MOVE` | 瀑布巨兽：即将爆发 | 保存当前蒸汽喷发层数作为爆炸伤害，消费该状态并进入爆炸阶段 | 实机集成闭环通过 |
| `WaterfallGiant.EXPLODE_MOVE` | 瀑布巨兽：爆炸 | 对玩家造成已保存的爆炸伤害，然后强制自身死亡并允许战斗结束 | 实机集成闭环通过 |
| `SteamEruptionPower.AfterDeath(...)` | 蒸汽喷发：死亡后处理 | 首次降到 `0 HP` 时拦截正常死亡，进入无限生命显示和蓄爆阶段 | 实机集成闭环通过 |
| `SteamEruptionPower.ShouldCreatureBeRemovedFromCombatAfterDeath(...)` | 蒸汽喷发：死亡后是否移出战斗 | 蓄爆和爆炸阶段结束前不移除持有者 | 实机集成闭环通过 |
| `SteamEruptionPower.ShouldStopCombatFromEnding()` | 蒸汽喷发：是否阻止战斗结束 | 爆炸自灭前持续阻止胜利结算 | 实机集成闭环通过 |
| `SteamEruptionPower.ShouldPowerBeRemovedAfterOwnerDeath()` | 蒸汽喷发：持有者死亡后是否移除状态 | 被拦截的死亡过程中保留该状态，直到蓄爆行动消费 | 实机集成闭环通过 |

说明：这个场景没有逐个执行瀑布巨兽的六个常规行动，因此它们列在下一节，不能算作实机通过。

`0.13.29` 重新验证了完整生命值长线：两个用户开战快照分别在第 `13`、`16` 回合结束，蒸汽喷发首次致死后的阵容保留、无限最大/当前生命、强制行动、普通 Power 清理和爆炸自灭均通过跨回合严格状态校验，计划外重算为 `0`；增量分叉与完整前缀回放也保持一致。

### `MONSTER-AXEBOT-HAMMER-001`

闭环：Steam 启动真实可见游戏，进入 `AxebotsNormal`，强制巨斧机器人执行指定行动。测试先调用生产求解器的一步行动语义得到预测状态，再执行真实 `MonsterModel.PerformMove()`，逐字段比较玩家生命、格挡及双方状态层数。

结果：`runId 895b0a8096944a19aa6a290e8e11f9b8` 返回 `Passed`；真实日志确认巨斧机器人执行 `HAMMER_UPPERCUT_MOVE`；预测和实测均为玩家失去 `14 HP`、获得 `2` 层虚弱和 `2` 层脆弱。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `Axebot.HAMMER_UPPERCUT_MOVE` | 巨斧机器人：上勾锤击 | 按攻击意图造成伤害，随后给玩家 `2` 层虚弱、`2` 层脆弱 | 实机一步差分通过 |
