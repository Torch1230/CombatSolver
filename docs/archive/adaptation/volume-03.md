# CombatSolver 适配验证入口历史卷 03

## 静态闭环通过、待实机差分（194 项）

### `STATIC-MONSTER-PRESENTATION-BATCH-058`（2 项）

闭环：逐项核对游戏 `0.111.0` 反编译源码，确认两个回调只写音乐控制器或 Godot 可视节点，不修改战斗状态。官中名称从当前 PCK 的 `localization/zhs` 精确读取。

| 适配项 | 游戏简中名称 | 静态结论 |
|---|---|---|
| `SoulFysh.AfterCardChangedPilesLate(...)` | 灵魂异鱼 | 召唤牌换堆时只更新 `soulfysh_progress` 与 `beckon` 音乐参数，不影响牌堆和求解状态 |
| `TorchHeadAmalgam.OnDieToDoom()` | 火炬头聚合体 | 灾厄死亡时只隐藏三盏附加灯光，不影响死亡、战斗结束或其他数值结算 |

### `STATIC-RELIC-DECK-BATCH-058`（4 项）

闭环：核对游戏 `0.111.0` 四件遗物、`CardPile`、`CardPileCmd`、`ImprovementPower` 及全部 `PileType.Deck` 调用点。这里的 `Deck` 是跑局永久牌组，不是战斗抽牌堆；单场战斗四牌堆分别为 `Hand`、`Draw`、`Discard` 和 `Exhaust`。

| 适配项 | 游戏简中名称 | 静态结论 |
|---|---|---|
| `BingBong.AfterCardChangedPiles(...)` | 宾邦 | 只在牌进入永久牌组时复制；战斗四牌堆移动不满足条件 |
| `BookOfFiveRings.AfterCardChangedPiles(...)` | 五轮书 | 只累计永久牌组加牌并每五张治疗；已经开始的战斗没有该写入入口 |
| `DarkstonePeriapt.AfterCardChangedPiles(...)` | 黑石护符 | 只在诅咒进入永久牌组时增加最大生命，属于单场战斗范围外 |
| `LuckyFysh.AfterCardChangedPiles(...)` | 招财异鱼 | 只在牌进入永久牌组时获得金币，属于奖励、商店、事件或其他战斗外流程 |

### `STATIC-RELIC-REACTIVE-BATCH-057`（17 项）

闭环：逐项核对游戏 `0.111.0` 遗物源码、原生回调时点、首个可操作搜索快照、未来召唤边界、药水原生结算后重搜和纯表现钩子，并完成最终 Release 构建。随机选牌、首回合自动出牌和召唤结果没有冒充数值实机差分；本批药水状态建立和三个动态边界另有可见游戏证据。

| 适配项 | 游戏简中名称 | 静态结论 |
|---|---|---|
| `BeltBuckle.AfterPotionDiscarded(...)` | 腰带扣 | 求解器不把丢弃药水作为路线动作；原生回调补敏捷后由下一次搜索读取实际状态 |
| `BeltBuckle.AfterPotionProcured(...)` | 腰带扣 | 原生获得药水并移除敏捷后重搜，药水栏与 Power 直接进入新快照 |
| `BookRepairKnife.AfterDiedToDoom(...)` | 修书小刀 | 灾厄致死后的按人数治疗已接入确定性死亡支持；本批未单独构造致死实机差分 |
| `ChoicesParadox.AfterPlayerTurnStart(...)` | 选择悖论 | 首回合随机候选与选择在玩家取得正常控制前完成，实际手牌和 RNG 进入初始快照；当前仍需玩家选择，不等于求解器已接管 |
| `FakeOrichalcum.BeforeSideTurnStart(...)` | 奥利哈钢？？？ | 只清理已经由成对回合末钩子消费的内部标志，没有独立数值结果 |
| `FestivePopper.AfterPlayerTurnStart(...)` | 节日拉炮 | 首回合全体伤害在首个可操作快照前完成，未来回合不再触发 |
| `FurCoat.AfterCreatureAddedToCombat(...)` | 皮草大衣 | 初始敌人状态由实战快照继承；未来召唤属于结构变化边界，原生加入后重搜 |
| `GamblingChip.AfterPlayerTurnStart(...)` | 赌博筹码 | 首回合可选弃牌与补抽在正常控制前完成，求解器读取实际选择结果 |
| `GoldPlatedCables.AfterModifyingOrbPassiveTriggerCount(...)` | 镀金缆线 | 该后置钩子只闪烁；被动次数加一由另一个数值钩子处理，并在情感芯片组合中实测 |
| `Orichalcum.BeforeSideTurnStart(...)` | 奥利哈钢 | 只清理已经由成对回合末钩子消费的内部标志，没有独立数值结果 |
| `PetrifiedToad.BeforeCombatStartLate()` | 石化蟾蜍 | 战前获得石头形状的药水，实际药水栏在战斗可搜索前已经确定 |
| `PhilosophersStone.AfterCreatureAddedToCombat(...)` | 贤者之石 | 初始敌人力量进入实战快照；未来召唤先停在结构变化边界，由原生施加力量后重搜 |
| `PowerCell.BeforeSideTurnStart(...)` | 能量电池 | 首回合随机把两张零费抽牌堆牌移入手牌，完成后才有首个正常搜索快照 |
| `RippleBasin.BeforeSideTurnStart(...)` | 波纹水盆 | 只把遗物显示状态设为激活；攻击历史和回合末格挡由其他已登记钩子处理 |
| `RippleBasin.BeforeSideTurnEnd(...)` | 波纹水盆 | 按当前搜索分支本回合实际打出的攻击牌数决定是否获得 `4` 格挡，不读取实机首轮 History；4 HP 墨宝长线第 `2/3` 回合精确复用 |
| `TwistedFunnel.BeforeSideTurnStart(...)` | 扭曲漏斗 | 首回合群体中毒在首个可操作快照前完成，未来回合不再触发 |
| `VexingPuzzlebox.AfterPlayerTurnStart(...)` | 烦人机关盒 | 首回合随机生成本回合零费牌后才进入玩家控制，实际牌与 RNG 由快照继承 |
| `WhisperingEarring.AfterAutoPrePlayPhaseEnteredLate(...)` | 低语耳环 | 首回合最多自动打出十三张牌的循环在玩家正常控制前完成，后续回合直接返回 |

### `STATIC-RELIC-TURN-LIFECYCLE-BATCH-056`（2 项）

闭环：逐项核对游戏 `0.111.0` 源码、搜索动态生成牌边界和 Release 构建。以下两项不冒充独立实机差分。

| 适配项 | 游戏简中名称 | 静态预期 | 结论 |
|---|---|---|---|
| `OrangeDough.AfterSideTurnStart(...)` | 橙色团块 | 首回合随机生成两张不同无色牌，搜索等待原生结算 | 已进入 `DynamicResolution` 边界 |
| `StoneCalendar.AfterSideTurnStart(...)` | 历石 | 只更新遗物状态和显示计数，伤害属于回合结束钩子 | 对战斗数值无影响 |

### `STATIC-RELIC-TURN-START-BATCH-055`（2 项）

闭环：逐项核对游戏 `0.111.0` 源码、战斗牌池 RNG 和求解器复杂生成牌边界，并完成 Release 构建。以下两项不枚举随机候选，也不冒充实机差分通过。

| 适配项 | 游戏简中名称 | 静态结论 |
|---|---|---|
| `BigHat.AfterSideTurnStart(...)` | 大帽子 | 首回合随机生成两张不同虚无牌；原生结算后读取实际手牌与 RNG 再重搜 |
| `Crossbow.AfterSideTurnStart(...)` | 十字弓 | 每回合随机生成一张本回合 `0` 费攻击牌；搜索停在动态结算边界，不手写候选池 |

### `STATIC-RELIC-HOOKS-BATCH-054`（2 项）

闭环：逐项核对游戏 `0.111.0` 的异蛇头骨与添水源码，并核对复杂生成效果的原生结算后动态重搜边界；最终 Release 构建零警告零错误。以下两项没有独立实机差分，不计作实机通过。

| 适配项 | 游戏简中名称 | 静态结论 |
|---|---|---|
| `SneckoSkull.AfterModifyingPowerAmountGiven(...)` | 异蛇头骨 | 只播放遗物闪烁；中毒数值由同遗物的加算钩子处理 |
| `Sozu.ShouldProcurePotion(...)` | 添水 | 阻止所属玩家获得药水；战斗内复杂药水生成不静态展开，原生结算后读取实际药水栏并重搜 |

### `STATIC-RELIC-DRAW-BATCH-053`（4 项）

闭环：逐项核对游戏 `0.111.0` 反编译源码与求解器首次可搜索状态的采集时点，并运行 Release 构建。以下条目没有逐项实机差分，不计作实机通过。

| 适配项 | 游戏简中名称 | 源码结论 | 当前证据 |
|---|---|---|---|
| `JeweledMask.BeforeHandDraw(...)` | 宝石面具 | 首回合随机选择抽牌堆中的能力牌，设为本回合免费并移入手牌；结果与 RNG 在搜索前进入快照 | 源码与初始快照边界；未构造含能力牌的首回合随机差分 |
| `Toolbox.BeforeHandDraw(...)` | 工具箱 | 首回合无色牌选择界面在搜索接管前结算，未来回合不重复 | 源码与初始快照边界；未逐项执行候选界面 |
| `Pocketwatch.AfterModifyingHandDraw()` | 怀表 | 只播放遗物闪烁 | 纯表现源码审计 |
| `Pocketwatch.AfterSideTurnStart(...)` | 怀表 | 只刷新计数显示与遗物状态 | 纯表现源码审计 |

### `STATIC-RELIC-SCOPE-BATCH-052`（11 项）

闭环：逐项核对游戏 `0.111.0` 反编译源码并运行 Release 构建。金币、购买、地图移动和胜利后治疗不属于“已经开始且尚未结束的一场战斗”；另外三个方法只更新遗物闪烁、状态或显示计数，不改变求解状态。以下条目没有冒充实机差分通过。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `BowlerHat.ModifyGoldGained(...)` | 圆顶礼帽 | 只修改跑局金币收益 | 单场战斗范围外 |
| `BowlerHat.AfterModifyingGoldGained(...)` | 圆顶礼帽 | 只播放遗物反馈 | 单场战斗范围外 |
| `DragonFruit.AfterGoldGained(...)` | 火龙果 | 获得金币后增加最大生命 | 单场战斗范围外 |
| `Ectoplasm.ModifyGoldGained(...)` | 灵体外质 | 阻止跑局金币收益 | 单场战斗范围外 |
| `Ectoplasm.AfterModifyingGoldGained(...)` | 灵体外质 | 只播放遗物反馈 | 单场战斗范围外 |
| `Fiddle.AfterPreventingDraw()` | 小提琴 | 非回合抽牌被阻止后只闪烁 | 纯表现 |
| `MawBank.AfterItemPurchased(...)` | 巨口储蓄罐 | 商店购买后耗尽 | 单场战斗范围外 |
| `MeatOnTheBone.AfterCombatVictoryEarly(...)` | 带骨肉 | 胜利后按生命阈值治疗 | 战斗结束后处理 |
| `PaelsFlesh.BeforeSideTurnStart(...)` | 佩尔之肉 | 只刷新显示计数 | 纯表现 |
| `PaelsFlesh.AfterSideTurnStart(...)` | 佩尔之肉 | 只切换激活状态与闪烁 | 纯表现 |
| `WingedBoots.ShouldAllowFreeTravel()` | 羽翼之靴 | 控制地图免费移动 | 单场战斗范围外 |

### `POWER-LIFECYCLE-BATCH-051-STATIC`（6 项）

| 适配项 | 游戏简中名称 | 源码结论 | 当前证据 |
|---|---|---|---|
| `AmbergrisPower.AfterTakingExtraTurn(...)` | 龙涎香 | 额外回合完成后清理原生状态；求解器已在进入额外回合前停止 | 游戏 `0.111.0` 源码、动态边界与 Release 构建；未单独执行额外回合后回调 |
| `AsleepPower.BeforeSideTurnEndVeryEarly(...)` | 沉睡 | 最后一层沉睡递减前先移除覆甲；RF 的忽略注册不能算精确支持 | 游戏源码、回合末顺序审计与 Release 构建；未单独隔离 VeryEarly 回调 |
| `PaleBlueDotPower.AfterSideTurnEnd(...)` | 暗淡蓝点 | 所属方回合末重置每回合一次的私有开关 | 游戏源码、私有状态镜像与 Release 构建；尚未跨两个完整回合重复触发 |
| `SwordSagePower.AfterRemoved(...)` | 剑圣 | 移除后撤销所有非复制君王之剑的重放加值 | 游戏源码、归一化分支与 Release 构建；尚未在实机强制移除该 Power |
| `VitalSparkPower.AfterPowerAmountChanged(...)` | 活力火花 | 敌方持有的层数变化同步玩家所有污化苦难层数 | 感染棱柱连续辐射/脉动差分通过；活力火花和逐牌污染 `2→4` |
| `VitalSparkPower.AfterRemoved(...)` | 活力火花 | 最后一项活力火花移除后清除盟友全部污化 | 游戏源码、归一化分支与 Release 构建；尚未在实机强制移除该 Power |

结论：`VitalSparkPower.AfterPowerAmountChanged(...)` 已补充实机差分；本表其余 `5` 项仍只记静态闭环。它们的可搜索前置路径已被真实场景覆盖，但对应回调本身尚未被单独强制执行。

### `STATIC-POWER-DEATH-BATCH-050`（4 项）

闭环：逐项核对游戏 `0.111.0` 的抑制、抢夺速度、抢夺力量及其怪物行动源码。抑制只由已经登记为动态边界的 `DAMPEN_MOVE` 施加，原生降级/恢复完成后搜索读取实际卡牌实例；两类抢夺的预测行动精确修改可见属性，但不复制私有退款账本，持有者死亡时由第 050 批动态边界交回真实游戏。

| 适配项 | 游戏简中名称 | 预期 | 结论 |
|---|---|---|---|
| `DampenPower.AfterApplied(...)` | 抑制 | 原生行动完成卡牌降级与施法者登记，随后重搜 | 源码、动态行动边界与构建闭环通过 |
| `DampenPower.AfterRemoved(...)` | 抑制 | 最后施法者死亡后原生恢复原升级等级，随后重搜 | 源码与死亡边界闭环通过 |
| `PossessSpeedPower.AfterPowerAmountChanged(...)` | 抢夺速度 | 预测保留可见敏捷变化，私有退款账本到死亡时交回实机 | 源码与死亡边界闭环通过 |
| `PossessStrengthPower.AfterPowerAmountChanged(...)` | 抢夺力量 | 预测保留可见力量变化，私有退款账本到死亡时交回实机 | 源码与死亡边界闭环通过 |

### `STATIC-POWER-TURN-START-BATCH-049`（2 项）

闭环：核对游戏 `0.111.0` 的 `VoidFormPower.BeforeApplied`、`BeforePowerAmountChanged`，以及 RF `0.13.8` 的 `VoidFormPredictionState`。两者都不是纯 VFX：它们会在虚空形态叠加/施加时，把本回合已出牌计数临时设为 `999999999`，关闭原生强制结束回合前的短暂零费窗口。生产模拟已显式写入同一 RF 分支状态，并由最终 Release 构建验证；下一回合归零由本批可见游戏 runId `37a29bdd86974b7180a809bf0325ff9f` 验证。由于没有单独停留并观测强制结束前的瞬时窗口，这两项不计实机差分。

| 适配项 | 游戏简中名称 | 静态结论 |
|---|---|---|
| `VoidFormPower.BeforeApplied(...)` | 虚空形态 | 施加前把已有虚空形态的 RF 出牌计数设为极大值；下一回合精确归零 |
| `VoidFormPower.BeforePowerAmountChanged(...)` | 虚空形态 | 层数变化前执行相同抑制，避免强制结束前错误保留零费窗口 |

### `STATIC-POWER-BATCH-048`（13 项）

闭环：逐项核对游戏 `0.111.0` 反编译源码及 RF `0.13.8` 对应分支状态。下列 `12` 个游戏钩子只控制闪烁、声音、形态 VFX、节点位置或音乐参数；地狱狂徒回合末会重置影响无限生命敌人自动出牌上限的分支状态，已接入生产代码并通过 Release 构建，但尚未构造九次以上自动出牌的直接实机差分。

| 适配项 | 游戏简中名称 | 静态结论 |
|---|---|---|
| `HellraiserPower.AfterSideTurnEnd(...)` | 地狱狂徒 | 将 RF 分支内无限敌人自动出牌计数归零；实现与构建通过，未做上限实机差分 |
| `HardenedShellPower.AfterModifyingHpLostBeforeOsty()` | 硬化外壳 | 只闪烁图标；伤害上限修改由独立钩子负责 |
| `ReaperFormPower.AfterApplied/AfterRemoved(...)` | 死神形态 | 两个钩子只创建或关闭形态 VFX |
| `SerpentFormPower.AfterApplied/AfterRemoved(...)` | 群蛇形态 | 两个钩子只创建或关闭形态 VFX |
| `SlumberPower.AfterRemoved(...)` | 熟睡 | 只停止睡眠循环音效；苏醒行动仍按独立条目适配 |
| `SandpitPower.AfterApplied(...)` | 沙坑 | 只缓存表现用初始节点位置 |
| `SandpitPower.AfterCreatureAddedToCombat/AfterOstyRevived/AfterPowerAmountChanged(...)` | 沙坑 | 三个钩子只更新节点位置、动画和音乐参数；层数递减与致死移除仍是独立条目 |
| `VoidFormPower.AfterApplied/AfterRemoved(...)` | 虚空形态 | 两个钩子只创建或关闭形态 VFX；费用与出牌计数钩子独立处理 |

### `STATIC-POWER-BATCH-047`（8 项）

闭环：逐项核对游戏 `0.111.0` 反编译源码，确认下列钩子只改变说明文字或表现，或者只作用于单人战斗中不存在的其他玩家；这些条目没有冒充实机差分。

| 适配项 | 游戏简中名称 | 静态结论 |
|---|---|---|
| `BarricadePower.AfterApplied(...)` | 壁垒 | 只把怪物施加者的本地化名称写入说明变量 |
| `MindRotPower.AfterModifyingHandDraw()` | 心灵腐化 | 只闪烁 Power 图标 |
| `DemonFormPower.AfterApplied/AfterRemoved(...)` | 恶魔形态 | 只创建或关闭形态 VFX |
| `EchoFormPower.BeforeSideTurnStart/AfterApplied/AfterRemoved(...)` | 回响形态 | 三个钩子只创建、启用或关闭形态 VFX |
| `HammerTimePower.AfterForge(...)` | 锤子时间 | 只为施法者之外的其他存活玩家锻造；不属于单人战斗数值语义 |

### `STATIC-MONSTER-DYNAMIC-BATCH-046`（17 项）

闭环：逐项核对游戏 `0.111.0` 的怪物行动源码，确认这些行动会召唤、逃跑、替换怪物、改写牌库或修改决定后续行动的私有字段。求解器保留当前攻击和已知确定性效果，但统一在敌方行动与回合末效果结算后、下一玩家回合恢复能量和抽牌前返回 `DynamicResolution`。共享边界机制已有上文三类代表实机通过；下列 `17` 项尚未逐项做原生结果差分，不能计入实机通过。

| 适配项 | 游戏简中名称 | 静态结论 |
|---|---|---|
| 三种 `DecimillipedeSegment.REATTACH_MOVE` | 残杀千足虫：接续 | 重接节段改变敌方结构，真实结算后重搜 |
| `Fabricator.FABRICATING_STRIKE_MOVE` | 组装师：组装打击 | 先计算攻击，召唤结果由原生结算后重搜 |
| `FatGremlin.FLEE_MOVE` | 胖地精：逃跑 | 逃跑改变敌方与奖励状态，原生结算后重搜 |
| `Fogmog.ILLUSION_MOVE` | 雾菇：虚幻孢子 | 生成幻象改变敌方结构，原生结算后重搜 |
| `KnowledgeDemon.CURSE_OF_KNOWLEDGE_MOVE` | 知识恶魔：知识的诅咒 | 按当前诅咒计数生成两条选牌分支，分别施加腐化心智/懒惰/日渐衰弱或对应数值的瓦解；选择进入路线并由自动执行提交 |
| `LivingFog.BLOAT_MOVE` | 活雾：膨胀 | 召唤活雾后重搜 |
| `MagiKnight.DAMPEN_MOVE` | 魔法骑士：抑制 | 降级牌并修改私有集合后，从真实牌实例重搜 |
| `Ovicopter.LAY_EGGS_MOVE` | 直飞产卵虫：产卵 | 生成卵后从真实敌方阵容重搜 |
| `TheObscura.ILLUSION_MOVE` | 胧光怪：幻象 | 生成幻象后重搜 |
| `ThievingHopper.ESCAPE_MOVE` | 偷窃草蜢：逃跑 | 逃跑及偷牌结果由原生完成后重搜 |
| `ToughEgg.HATCH_MOVE` | 结实的卵：孵化 | 使用原生 RNG 孵化并替换怪物后重搜 |
| `TwoTailedRat.CALL_FOR_BACKUP/DISEASE_BITE/SCRATCH/SCREECH` | 双尾鼠：呼唤后援／疾病啃咬／抓挠／尖声嘶吼 | 结算已知攻击或脆弱后，因召唤计数私有状态停止旧路线 |

### `STATIC-AFFLICTION-BATCH-046`（1 项）

| 适配项 | 游戏简中名称 | 静态结论 |
|---|---|---|
| `Tainted.CanAfflictCardType(...)` | 污染 | 源码只允许附着到技能牌；这是原生附着资格，不是战斗中响应式生命周期。求解器读取已经完成附着的真实牌实例，未宣称实机差分 |

### `STATIC-CARD-BATCH-043-NATIVE-STATE`（4 项）

| 适配项 | 游戏简中名称 | 源码结论 |
|---|---|---|
| `BansheesCry.AfterCardEnteredCombat(...)` | 女妖之嚎 | 只在本牌进入战斗时按此前已打出的虚无牌初始化本场费用；求解器捕获初始化后的原生卡牌实例 |
| `Flatten.AfterCardEnteredCombat(...)` | 重压 | 只在本牌进入战斗且奥斯蒂本回合已经攻击时把本回合费用设为 `0`；后续奥斯蒂攻击钩子独立登记 |
| `Pinpoint.AfterCardEnteredCombat(...)` | 精密瞄准 | 只按进入战斗前本回合已打出的技能数初始化费用；后续技能降费钩子独立登记 |
| `Stomp.AfterCardEnteredCombat(...)` | 踩踏 | 只按进入战斗前本回合已打出的攻击数初始化费用；后续攻击降费钩子独立登记 |

结论：4 项登记为 `NativeRuntimeState`。它们依赖求解开始前已完成的原生进入战斗流程，不在每个搜索分支中重复执行；本节只有源码与初始状态捕获审计，没有实机差分。

### `STATIC-CARD-BATCH-043-SCOPE`（8 项）

| 适配项 | 游戏简中名称 | 源码结论 |
|---|---|---|
| `Guilty.AfterCombatEnd(...)` | 愧疚 | 仅在战后累计持久化计数并于第五场后从牌组移除 |
| `MadScience.AddExtraArgsToDescription(...)` | 疯狂科学 | 只给本地化描述填充卡牌类型和附加效果条件 |
| `Midnight.AfterCardEnteredCombat(...)` | 午夜 | 明确为 `MultiplayerOnly`，不进入支持的单人战斗范围 |
| `SovereignBlade.AfterCardChangedPiles(...)` | 君王之剑 | 只播放或移除战斗房间锻造表现 |
| `SovereignBlade.AfterCloned()` | 君王之剑 | 只清除供表现钩子读取的 `CreatedThroughForge` 标记 |
| `SovereignBlade.AfterTransformedFrom()` | 君王之剑 | 只移除君王之剑表现节点 |
| `SpoilsMap.BeforeCardRemoved(...)` | 藏宝图 | 只在牌组移除时清理地图任务 |
| `SpoilsMap.AfterCreated()` | 藏宝图 | 只初始化后续地图生成使用的幕数 |

结论：8 项均登记为 `NotCombatRelevant`，只完成源码静态闭环，不计作实机通过。

### `STATIC-CARD-BATCH-041-MULTIPLAYER`（3 项）

闭环：逐项核对游戏 `0.111.0` 的卡牌源码与 `MultiplayerConstraint`。下列卡牌都明确覆盖为 `MultiplayerOnly`，正常已经开始的单人战斗无法从原版牌池出现，因此登记为单人范围不适用，而不是求解器已模拟。

| 适配项 | 游戏简中名称 | 源码结论 |
|---|---|---|
| `Tank.OnPlay(...)` | 肉盾 | 明确为多人专属；自身获得肉盾 Power，单人战斗不适用 |
| `Tutor.OnPlay(...)` | 指导 | 明确为多人专属并面向盟友选牌，单人战斗不适用 |
| `Underworld.OnPlay(...)` | 幽冥之界 | 明确为多人专属；自身获得幽冥之界 Power，单人战斗不适用 |

### `STATIC-CARD-BATCH-040-MULTIPLAYER`（3 项）

闭环：逐项核对游戏 `0.111.0` 的卡牌源码与 `MultiplayerConstraint`。下列卡牌都明确覆盖为 `MultiplayerOnly`，正常已经开始的单人战斗无法从原版牌池出现，因此登记为单人范围不适用，而不是求解器已模拟。

| 适配项 | 游戏简中名称 | 源码结论 |
|---|---|---|
| `Plot.OnPlay(...)` | 筹划 | 明确为多人专属并作用于全体盟友，单人战斗不适用 |
| `Sneaky.OnPlay(...)` | 鬼祟 | 明确为多人专属，单人战斗不适用 |
| `Soulbound.OnPlay(...)` | 灵魂绑定 | 明确为多人专属且目标为盟友，单人战斗不适用 |

### `STATIC-CARD-BATCH-039-MULTIPLAYER`（3 项）

闭环：逐项核对游戏 `0.111.0` 的卡牌源码与 `MultiplayerConstraint`。下列卡牌都明确覆盖为 `MultiplayerOnly`，正常已经开始的单人战斗无法从原版牌池出现，因此登记为单人范围不适用，而不是求解器已模拟。

| 适配项 | 游戏简中名称 | 源码结论 |
|---|---|---|
| `BladeSymphony.OnPlay(...)` | 刀刃交响曲 | 明确为多人专属并作用于全体盟友，单人战斗不适用 |
| `Fade.OnPlay(...)` | 消影 | 明确为多人专属且目标为盟友，单人战斗不适用 |
| `GlimpseBeyond.OnPlay(...)` | 彼岸一瞥 | 明确为多人专属并作用于全体盟友，单人战斗不适用 |

### `STATIC-CARD-BATCH-038-MULTIPLAYER`（11 项）

闭环：逐项核对游戏 `0.111.0` 的卡牌源码与 `MultiplayerConstraint`。下列卡牌都明确覆盖为 `MultiplayerOnly`，正常已经开始的单人战斗无法从原版牌池出现，因此登记为单人范围不适用，而不是求解器已模拟。

| 适配项 | 游戏简中名称 | 源码结论 |
|---|---|---|
| `Cacophony.OnPlay(...)` | 不谐合曲 | 明确为多人专属，单人战斗不适用 |
| `Concoct.OnPlay(...)` | 调制 | 明确为多人专属且目标为盟友，单人战斗不适用 |
| `HammerTime.OnPlay(...)` | 锤子时间 | 明确为多人专属，单人战斗不适用 |
| `Hibernate.OnPlay(...)` | 休眠 | 明确为多人专属，单人战斗不适用 |
| `ImitationLearning.OnPlay(...)` | 模仿学习 | 明确为多人专属且记录其他玩家目标，单人战斗不适用 |
| `LegionOfBone.OnPlay(...)` | 骸骨军团 | 明确为多人专属并作用于全体盟友，单人战斗不适用 |
| `OneForAll.OnPlay(...)` | 一心化万 | 明确为多人专属并作用于所有玩家，单人战斗不适用 |
| `BelieveInYou.OnPlay(...)` | 相信着你 | 明确为多人专属且目标为盟友，单人战斗不适用 |
| `Coordinate.OnPlay(...)` | 协同配合 | 明确为多人专属且目标为盟友，单人战斗不适用 |
| `Flanking.OnPlay(...)` | 夹击 | 明确为多人专属，单人战斗不适用 |
| `EnergySurge.OnPlay(...)` | 能量涌动 | 明确为多人专属并给队友能量，单人战斗不适用 |

### `STATIC-CARD-BATCH-037-MULTIPLAYER`（1 项）

| 适配项 | 游戏简中名称 | 源码结论 |
|---|---|---|
| `Blaze.OnPlay(...)` | 炽焰 | 卡牌明确覆盖 `MultiplayerConstraint=MultiplayerOnly` 且目标为盟友，正常单人战斗无法出现，登记为不适用 |

### `STATIC-CARD-BATCH-036-MULTIPLAYER`（1 项）

| 适配项 | 游戏简中名称 | 源码结论 |
|---|---|---|
| `BeaconOfHope.OnPlay(...)` | 希望灯塔 | 卡牌明确覆盖 `MultiplayerConstraint=MultiplayerOnly`，正常单人战斗无法出现，登记为不适用 |

### `STATIC-HEXED-AFFLICTION-028`（1 项）

闭环：核对游戏 `0.111.0` 的 `Hexed.AfterCardEnteredCombat` 源码，并核对求解器卡牌附魔规范化逻辑。游戏要求受咒牌进入战斗时检查拥有者是否仍有恶咒：有则保持受咒，没有则立即清除。求解器在恶咒数值不大于零时清除模拟牌上的受咒；随后完成 Release 构建。

| 适配项 | 游戏简中名称 | 中文预期 |
|---|---|---|
| `Hexed.AfterCardEnteredCombat(...)` | 受咒：卡牌进入战斗 | 拥有者仍有恶咒时保持受咒；恶咒已经消失时立即清除受咒 |

结论：源码与构建静态闭环通过；本批实机覆盖了“有恶咒时新牌保持受咒”，但没有直接向无恶咒战斗注入预先受咒的牌，因此无恶咒自清理分支仍记为静态。

### `STATIC-MONSTER-MOVES-BATCH-025`（3 项）

闭环：逐项核对游戏 `0.111.0` 中三个真实模型的行动回调。仪式兽 `STUN_MOVE` 使用通用眩晕意图，回调只清除 `IsStunnedByPlowRemoval` 动画标志；奥斯提和佩尔士兵的 `NOTHING_MOVE` 均直接返回 `Task.CompletedTask` 且没有意图。随后重建覆盖目录并运行 Release 构建，未启动真实游戏。

| 适配项 | 游戏简中名称 | 中文预期 |
|---|---|---|
| `CeremonialBeast.STUN_MOVE` | 仪式兽：游戏无独立简中词条（`STUN_MOVE`） | 本行动不改变生命、格挡、Power 或牌堆；二阶段标记在进入本行动前由犁地 Power 移除流程设置，属于独立条目 |
| `Osty.NOTHING_MOVE` | 奥斯提：游戏无独立简中词条（`NOTHING_MOVE`） | 宠物空行动，不改变战斗状态 |
| `PaelsLegion.NOTHING_MOVE` | 佩尔的士兵：无 | 宠物空行动，不改变战斗状态 |

结论：以上 `3` 项完成源码和构建静态闭环，尚未在真实可见游戏中逐项执行。同期审计的 `DeprecatedMonster` 及四个以“大型假人”为标题、全库无正常游戏引用的支持模型已登记为 `NotCombatRelevant`，不计入本适配项总数。

### `STATIC-MONSTER-MOVES-BATCH-003`（27 项）

闭环：逐个阅读游戏 `0.111.0` 中潮湿邪教徒、虔诚雕刻师、蜂群术士、外骨骼虫、组装师、商人？？？、连枷骑士、飞蝇菌子、雾菇和化石追踪者的真实行动回调。核对攻击段数、动态模型数值、人体蜂房条件分支及额外 Power 后，运行 Release 构建和覆盖目录生成。召唤行动不在本批次内。

| 适配项 | 游戏简中名称 | 中文预期 |
|---|---|---|
| `DampCultist.DARK_STRIKE_MOVE` | 潮湿邪教徒：黑暗打击 | 按意图执行单段攻击；递增字段 `AttackSfxStrength` 只改变音效表现 |
| `DampCultist.INCANTATION_MOVE` | 潮湿邪教徒：念咒 | 按实时模型字段 `IncantationAmount` 获得仪式 |
| `DevotedSculptor.SAVAGE_MOVE` | 虔诚雕刻师：猛烈攻击 | 按意图执行单段攻击，不产生额外战斗状态 |
| `DevotedSculptor.FORBIDDEN_INCANTATION_MOVE` | 虔诚雕刻师：禁忌唱诵 | 获得 `9` 层仪式；音效、动画、对白和等待只属于表现 |
| `Entomancer.BEES_MOVE` | 蜂群术士：蜜——蜂——！ | 按当前难度的真实重复次数执行多段攻击 |
| `Entomancer.SPEAR_MOVE` | 蜂群术士：矛击！ | 按意图执行单段攻击，不产生额外战斗状态 |
| `Entomancer.PHEROMONE_SPIT_MOVE` | 蜂群术士：喷射信息素 | 人体蜂房低于 `3` 层时获得 `1` 层人体蜂房和 `1` 点力量；达到 `3` 层或没有该 Power 时获得 `2` 点力量 |
| `Exoskeleton.MANDIBLES_MOVE` | 外骨骼虫：啃食 | 按意图执行单段攻击，不产生额外战斗状态 |
| `Exoskeleton.SKITTER_MOVE` | 外骨骼虫：忙乱 | 按当前难度的真实重复次数执行多段攻击 |
| `Exoskeleton.ENRAGE_MOVE` | 外骨骼虫：激怒 | 获得 `2` 点力量 |
| `Fabricator.DISINTEGRATE_MOVE` | 组装师：瓦解 | 按意图执行单段攻击；组装召唤属于其他行动，不在本条内 |
| `FakeMerchantMonster.SWIPE_MOVE` | 商人？？？：顺走 | 按意图执行单段攻击；混沌 RNG 选择的对白只属于表现 |
| `FakeMerchantMonster.SPEW_COINS_MOVE` | 商人？？？：喷吐硬币 | 按意图执行八段攻击；混沌 RNG 选择的对白只属于表现 |
| `FakeMerchantMonster.THROW_RELIC_MOVE` | 商人？？？：投掷遗物 | 按意图攻击后给玩家 `1` 层脆弱；混沌 RNG 选择的对白只属于表现 |
| `FakeMerchantMonster.ENRAGE_MOVE` | 商人？？？：激怒 | 获得 `2` 点力量；混沌 RNG 选择的对白只属于表现 |
| `FlailKnight.FLAIL_MOVE` | 连枷骑士：连枷 | 按意图执行两段攻击，不产生额外战斗状态 |
| `FlailKnight.RAM_MOVE` | 连枷骑士：撞击 | 按意图执行单段攻击，不产生额外战斗状态 |
| `FlailKnight.WAR_CHANT` | 连枷骑士：战争吟唱 | 获得 `3` 点力量 |
| `Flyconid.SMASH_MOVE` | 飞蝇菌子：猛砸 | 按意图执行单段攻击，不产生额外战斗状态 |
| `Flyconid.VULNERABLE_SPORES_MOVE` | 飞蝇菌子：易伤孢子 | 给玩家 `2` 层易伤 |
| `Flyconid.FRAIL_SPORES_MOVE` | 飞蝇菌子：脆弱孢子 | 按意图攻击后给玩家 `2` 层脆弱 |
| `Fogmog.HEADBUTT_MOVE` | 雾菇：游戏无独立简中词条（`HEADBUTT_MOVE`） | 按意图执行单段攻击，不产生额外战斗状态 |
| `Fogmog.SWIPE_MOVE` | 雾菇：重击 | 按意图攻击后获得 `1` 点力量 |
| `Fogmog.SWIPE_RANDOM_MOVE` | 雾菇：重击（共用词条） | 与 `SWIPE_MOVE` 共用回调：按意图攻击后获得 `1` 点力量 |
| `FossilStalker.LATCH_MOVE` | 化石追踪者：缠上 | 按意图执行单段攻击；吸取 Power 由独立入场钩子提供，不在本条内 |
| `FossilStalker.LASH_MOVE` | 化石追踪者：甩动 | 按意图执行两段攻击，不产生额外战斗状态 |
| `FossilStalker.TACKLE_MOVE` | 化石追踪者：冲撞 | 按意图攻击后给玩家 `1` 层脆弱 |

结论：以上 `27` 项已完成反编译源码和构建静态闭环，其中 `23` 项为新增覆盖、`4` 项为已有实现补齐证据；仍待真实游戏一步差分。

### `STATIC-MONSTER-MOVES-BATCH-002`（18 项）

闭环：逐个阅读游戏 `0.111.0` 的真实行动回调，当前仍在本节的 `15` 个纯攻击或空行动没有隐藏的状态、牌堆、计数器和阶段变化；另外核对三个确定性 Buff/格挡行动的数值。随后运行 Release 构建和覆盖目录生成。尚未逐项执行真实 `PerformMove()` 差分。已通过批量实机差分的行动不再列于本节。

| 适配项 | 游戏简中名称 | 中文预期 |
|---|---|---|
| `AssassinRubyRaider.KILLSHOT_MOVE` | 劫掠者刺客：致命射击 | 按意图执行单段攻击，不产生额外战斗状态 |
| `Architect.NOTHING` | 建筑师：无 | 隐藏行动，不改变战斗状态 |
| `BattleFriendV1.NOTHING_MOVE` | 战斗好伙伴V1.0：无 | 不改变战斗状态；其入场时限 Power 属于独立钩子，不在本条结论内 |
| `BattleFriendV2.NOTHING_MOVE` | 战斗好伙伴V2.0：无 | 不改变战斗状态；其入场时限 Power 属于独立钩子，不在本条结论内 |
| `BattleFriendV3.NOTHING_MOVE` | 战斗好伙伴V3.0：无 | 不改变战斗状态；其入场时限 Power 属于独立钩子，不在本条结论内 |
| `BigDummy.NOTHING` | 大型假人：无 | 隐藏行动，不改变战斗状态 |
| `BruteRubyRaider.BEAT_MOVE` | 劫掠者暴徒：殴打 | 按意图执行单段攻击，不产生额外战斗状态 |
| `BruteRubyRaider.ROAR_MOVE` | 劫掠者暴徒：怒吼 | 怪物获得 `3` 点力量 |
| `BygoneEffigy.SLEEP_MOVE` | 旧日雕像：沉睡 | 只播放对白并等待，不改变战斗状态 |
| `BygoneEffigy.SLEEP_MOVE_2` | 旧日雕像：沉睡（共用词条） | 不改变战斗状态 |
| `BygoneEffigy.WAKE_MOVE` | 旧日雕像：苏醒 | 怪物获得 `10` 点力量；音乐、对白和等待只属于表现 |
| `BygoneEffigy.SLASHES_MOVE` | 旧日雕像：斩击 | 按意图执行单段攻击；位移、模糊和等待只属于表现 |
| `Byrdonis.PECK_MOVE` | 多尼斯异鸟：啄击 | 按意图执行三段攻击，不产生额外战斗状态 |
| `Byrdonis.SWOOP_MOVE` | 多尼斯异鸟：飞扑 | 按意图执行单段攻击，不产生额外战斗状态 |
| `Byrdpip.NOTHING_MOVE` | 异鸟宝宝：无 | 不改变战斗状态 |
| `CalcifiedCultist.DARK_STRIKE_MOVE` | 钙化邪教徒：黑暗打击 | 按意图执行单段攻击；递增字段 `AttackSfxStrength` 只改变音效表现 |
| `CrossbowRubyRaider.FIRE_MOVE` | 劫掠者弩手：射击！ | 按意图执行单段攻击；装填标记只选择表现，行动状态机仍固定交替 |
| `CrossbowRubyRaider.RELOAD_MOVE` | 劫掠者弩手：装填 | 怪物获得 `3` 点格挡；装填标记只选择表现，行动状态机仍固定交替 |

结论：以上 `18` 项已完成反编译源码和构建静态闭环，仍待对应遭遇中的真实游戏一步差分。盛碗虫（丝）撕扯及噬尸蛞蝓两次攻击已升级为 `MONSTER-MOVES-BATCH-012` 实机闭环。

### `STATIC-WATERFALL-MOVES-001`（6 项）

闭环：逐项核对 `WaterfallGiant.cs` 中六个行动回调与 `MonsterMoveEffects` 的补偿分支，并运行 Release 构建。攻击伤害由通用意图结算负责，表中只列行动额外语义。尚未逐个强制执行真实行动。

| 适配项 | 游戏简中名称 | 中文预期 |
|---|---|---|
| `WaterfallGiant.PRESSURIZE_MOVE` | 瀑布巨兽：增压 | 增加行动模型 `PressurizeAmount` 指定的蒸汽喷发层数 |
| `WaterfallGiant.STOMP_MOVE` | 瀑布巨兽：践踏 | 攻击后给玩家 `1` 层虚弱，并给自身增加 `3` 层蒸汽喷发 |
| `WaterfallGiant.RAM_MOVE` | 瀑布巨兽：撞击 | 攻击后给自身增加 `3` 层蒸汽喷发 |
| `WaterfallGiant.SIPHON_MOVE` | 瀑布巨兽：虹吸 | 回复 `SiphonHeal × 玩家数` 的生命，并增加 `3` 层蒸汽喷发 |
| `WaterfallGiant.PRESSURE_GUN_MOVE` | 瀑布巨兽：压力炮 | 以当前压力炮伤害攻击，之后永久增加 `PressureGunIncrease` 点该行动伤害，并增加 `3` 层蒸汽喷发 |
| `WaterfallGiant.PRESSURE_UP_MOVE` | 瀑布巨兽：增压 | 攻击后给自身增加 `3` 层蒸汽喷发 |

结论：以上 `6` 项静态核对通过；`MONSTER-WATERFALL-001` 未直接执行它们，所以仍待真实游戏一步差分。

### `STATIC-MONSTER-MOVES-001`（10 项）

闭环：逐项核对游戏 `0.111.0` 反编译行动回调、`MoveState` 意图和求解器实现；再运行 Release 构建及覆盖目录生成。结果为构建 `0` 错误、`0` 警告，分类键均能被反射目录解析。尚未执行真实 `PerformMove()` 一步差分。

| 适配项 | 游戏简中名称 | 中文预期 |
|---|---|---|
| `FuzzyWurmCrawler.FIRST_ACID_GOOP` | 毛绒伏地虫：酸液黏球（共用词条） | 仅执行意图声明的单段攻击，不额外添加状态 |
| `FuzzyWurmCrawler.ACID_GOOP` | 毛绒伏地虫：酸液黏球 | 仅执行意图声明的单段攻击，不额外添加状态 |
| `FuzzyWurmCrawler.INHALE` | 毛绒伏地虫：吸入 | 怪物获得 `7` 点力量；`IsPuffed` 只影响表现，不进入求解状态 |
| `Axebot.BOOT_UP_MOVE` | 巨斧机器人：启动 | 获得行动模型中的格挡，并获得 `BootUpStrGain × RespawnCount` 点力量 |
| `Axebot.ONE_TWO_MOVE` | 巨斧机器人：两连击 | 按意图执行两段攻击 |
| `Aeonglass.EBB_MOVE` | 永世沙漏：消退 | 按意图攻击，并获得行动模型中的 `EbbBlock` 格挡 |
| `Aeonglass.EYE_LASERS_MOVE` | 永世沙漏：眼部激光 | 按意图执行两段攻击 |
| `AxeRubyRaider.SWING_1` | 劫掠者斧手：游戏无独立简中词条（`SWING_1`） | 按意图攻击，并获得行动模型中的 `SwingBlock` 格挡 |
| `AxeRubyRaider.SWING_2` | 劫掠者斧手：游戏无独立简中词条（`SWING_2`） | 按意图攻击，并获得行动模型中的 `SwingBlock` 格挡 |
| `AxeRubyRaider.BIG_SWING` | 劫掠者斧手：大力挥舞 | 仅执行意图声明的单段攻击 |

结论：以上 `10` 项静态核对通过，但在对应无人实机差分场景通过前不得标记为实机通过。盛碗虫（蜜）的两次撕扯已升级为 `MONSTER-MOVES-BATCH-012` 实机闭环。

## 已实现、尚未完成独立闭环（0 项）

当前没有已经实现却缺少独立静态或实机闭环的条目。新补偿必须先登记到本节，完成相应闭环后才能移入上方章节。

## 维护规则

- 新增求解器补偿时，由开发者在本文中手工增加中文名称、预期行为和当前闭环状态。
- 实机闭环与静态闭环章节都按批次号降序排列，最新适配固定写在最前。
- 只有真实可见游戏进程中完成“生产预测 → 真实结算 → 逐字段比较”，并有同一 `runId` 的 `Passed` 结果，才标记为实机闭环通过。
- 反编译源码核对与 Release 构建通过只记为静态闭环；未直接执行的行动不能借用同场景其他行动的实机结论。
- 失败、未运行或只看最终胜负的场景不记为通过。机器可读证据保存在 `coverage/evidence/test-evidence.json`，人工结论以本文档为准。
