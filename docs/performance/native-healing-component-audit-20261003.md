# 原生回复来源的组件审计：间接来源与回调

接续[第一阶段入口审计](native-health-source-audit-20261003.md)，复用合入上游 `56b6d6ee` 后已验证并部署的 `2c5234ee` 工作。本阶段只补充来源证明，没有修改运行时资格、评分、预算或搜索顺序。通用组件证书尚未接入生产，也没有新增性能验收结论。

## 版本与证据范围

游戏仍为 `v0.111.0` / `41cef1ea`，DLL SHA-256 为 `2b40d2df538db1ceb5fa48d958c80ab730ada1e07db88a870aff01a661768b9f`，MVID 为 `8a76776c-0ce1-4d4f-90bd-8cce653dad8e`。使用现有 IL 清单的 8,340 个模型虚方法、静态调用、委托和异步映射，定向读取实际安装 DLL 的源码；不以治疗文案或 `Heal` 字段作为判据。

[配套 JSON](native-healing-component-audit-20261003.json) 保存 80 项来源类型的提取哈希、44 项具体机制的目标、时点、条件、次数、重复性、来源和剩余证明义务，以及回调和工厂调用清单。原版源码与失败提取日志只留在忽略目录。一次把 SpoilsMap 误写在 Relics 命名空间的提取失败保留；成功来源是 `Models.Cards.SpoilsMap`，没有从失败推导安全结论。

“具体效果已审查”只覆盖注明的方法。直接玩家回复量为零，仍可能产生可回血的牌、药水、遗物或触发其他监听者；只有这些依赖全部闭合，才能把它组合成严格上界。静态无路径、原版程序集身份和已经取得源码均不表示认证通过。

## 固定选牌与普通生成的区别

知识恶魔的三个选择波次为 `[Disintegration, MindRot]`、`[Disintegration, Sloth]`、`[Disintegration, WasteAway]`。对存活玩家明确创建临时牌后调用 `IChoosable.OnChosen`，不走普通出牌或随机生成池。四张牌的 `CanBeGeneratedInCombat` 均为 false，因此不能借八池随机过滤将其排除。

| 选择及后续 Power | 原生结算 | 剩余证明义务 |
| --- | --- | --- |
| Disintegration | 给持有玩家施加 6/7/8 层；所属侧回合晚期造成自身非受增伤影响伤害 | 伤害、死亡、保命和受伤回复监听者必须组合 |
| MindRot | 1 层；减少持有者手牌抽取量，最低为零 | 抽牌及进场回调、初始与取回来源 |
| Sloth | 3 层；按开始出牌计数限制，持有者侧回合开始归零 | 自动牌与重放同样计数，Fork/历史不能换成手动完成次数 |
| WasteAway | 1 层；降低持有者最大能量 | 其他能量结算回调 |

四条选择本身不增加玩家生命；知识恶魔的 Ponder 仍治疗敌人自身 30×玩家人数并加力量。以上方法阅读没有替代完整敌方选择原生/模拟差分，也没有认证整只敌人。

FranticEscape 同样不能普通随机生成，但 TheInsatiable 会明确生成它。出牌找到指向持有玩家的 SandpitPower，加一层并增加自身本场费用。Sandpit 的移除会强制击杀目标玩家及 Osty，必须保持胜负和保命目标，不能因为没有回复就跳过死亡语义。

## 宠物生命、复活与玩家回调

当前原版没有 `AfterSummon` 或 `ModifySummonAmount` 覆盖；`AfterOstyRevived` 唯一覆盖为 SandpitPower，其该方法只更新 UI 位置。该事实不排除其他阶段或扩展监听者。

`AfterCurrentHpChanged` 的五个原版覆盖是 MeatOnTheBone、RedSkull、NecroMasteryPower、Crusher、Rocket。前两项沿用已审查的门槛状态和力量变更；后两项只对自身负变化播放受伤动画。NecroMastery 只在自己玩家的 Osty 生命变化为负时，对可命中的敌人造成 `-delta×Amount` 伤害。因此宠物回复本身不是玩家回复，但宠物受伤导致敌人死亡，仍能触发其他已持有的回复资源。

所有判断需使用正确的 Owner、PetOwner、Target 和分支状态。任意 Power 恰好具有原版类型，不能覆盖错误的玩家持有目标。既有再生仍按玩家有效层数计算；药水禁用、额度耗尽不删除已生效再生。

## 战斗结束、永久牌组与奖励

定位了 45 个原版 `AfterCombatEnd` 覆盖。限定的静态调用图中，只有 ChosenCheese 通向生命写入口；六项经过其他派发或反射入口。该图用于找依赖，零路径不是零回复证明，未逐项审查的正文仍未认证。

| 来源 | 当前原生行为和次数 | 与回复及搜索边界的关系 |
| --- | --- | --- |
| ChosenCheese | 每次结束回调增加玩家生命上限 1，并同步治疗实际增量 | 已有修复保持未知总闭包的保守上界；没有忽略加上限 |
| RoyaltiesPower / HeistPower | 分别在结束或拥有者死亡时增加金币奖励 | 添加奖励不等于 `GainGold`；奖励领取在当前战斗搜索外。资源追回目标仍保留 |
| ForbiddenGrimoirePower | 结束时增加 Amount 份移除卡牌奖励 | 未立即执行永久牌组移除；奖励领取另审 |
| ImprovementPower | 用 CombatCardSelection RNG 随机选取至多 Amount 张可升级的永久牌组卡 | 无直接回复；升级方法、附着效果和完整 RNG 状态仍需闭合 |
| FishingRod | 普通怪物房间每第三次结束，使用 Niche RNG 升级一张永久牌组卡 | 需保留计数、升级与附着链，不能凭静态无生命路径自动放行 |
| PaelsTooth | 玩家存活且保存列表非空时，用 Rewards RNG 返回、升级并加入一张保存牌，随后从列表移除 | `Add Deck` 能触发 BookOfFiveRings、DarkstonePeriapt；需要牌组、反序列化和回调来源证明 |
| ToyBox | 每第三次有效结束熔化第一件未熔化蜡遗物，直到用尽 | `Melt` 调用 `AfterRemoved`；当前原版遗物没有该方法覆盖，基类为空实现，熔化链本身不回复。获得时的五份随机遗物奖励仍需单独证明 |
| Guilty | 只有实际位于永久牌组时累计战斗数，达到五次移除自身 | 战斗临时副本不执行此效果；原生成证书的前提不能直接外推到初始永久牌组 |

当前 `BeforeCardRemoved` 的唯一原版覆盖是 SpoilsMap：只在移除自己、章节与坐标匹配时删除地图任务标记，不调用金币命令。其独立的 `OnQuestComplete` 才会获得金币，因此 Guilty 的移除回调不会隐式完成这个任务。章节任务不在当前战斗搜索边界内；外部回调仍须拒绝或适配。

当前 `AfterCardChangedPiles` 有八个覆盖，其中包括 BookOfFiveRings 和 DarkstonePeriapt；加入永久牌组和加入战斗牌堆不能混为同一入口。YummyCookie 在获得时升级四张永久牌组卡，不调用休息治疗；仍须审核获得和升级可达性。

## 敌方新增来源的递归边界

直接调用清单定位到 18 类怪物的造牌来源：明确状态牌为 Wither、Dazed、Slimed、Burn、Toxic、Infection、Beckon、FranticEscape、Wound，另有上述知识恶魔固定选牌。敌人动作调用之外，PersonalHivePower 明确生成 Dazed，PainfulStabsPower 明确生成 Wound。这些入口同样要经过进场、牌堆及附着回调。

敌人类型中的直接泛型施加引用覆盖 62 种 Power。10 种可复用相同 DLL 的 C58 来源哈希，其余复用已有原生提取或新增读取；这只表示取得来源证据。尚未复核的模型、间接生成和未知目标继续拒绝，不把“62 个直接类型”当成全部可达集合。

| Power | 直接结算 | 闭包要求 |
| --- | --- | --- |
| Infested / Stock / Surprise | 拥有者死亡后分别新增 Wriggler、Axebot、SneakyGremlin/FatGremlin；Stock 子实例层数减一 | 新个体初始化、动作、Power、生成牌和新增回调递归审查 |
| Adaptable / SteamEruption | 进入 TestSubject / WaterfallGiant 的后续状态 | 后续动作和生命重设对象另审，不从当前方法零回复推导整个阶段安全 |
| Illusion / Reattach | 分别补满敌人自身或回复拥有者敌段 | 玩家错误持有未知；死亡、复活和其他玩家持有的回复监听者仍组合 |
| Suck | 根据攻击结果给自身加力量 | 该方法不吸血；Power 施加和其他受伤监听者仍需审查 |
| ChainsOfBinding / Galvanic / Hex / Ringing / Smoggy / Tangled / VitalSpark | 分别附加 Bound / Galvanized / Hexed / Ringing / Smog / Entangled / Tainted | 附着效果是独立组件，不能只认证生成它的 Power |

药水工厂有 14 条定位引用，`TryToProcure` 有 12 条；没有直接怪物调用。此事实不排除经卡牌、遗物或回调间接获取。EntropicBrew 使用战外池的已有结论保持，不能套用战斗生成过滤。RelicCmd.Obtain/Replace 执行实际 `AfterObtained`，获取链没有形成通用证书时保留完整余量。

## 固定根的实际覆盖与首批组件目标

追加[29根来源诊断](native-healing-root-coverage-20261003.json)，使用已验证 DLL `89a8e2c2…` 的独立离线根诊断进程；不运行 Solve，不把诊断耗时或内存作为性能验收。先取得的两个目标根直接复用，另外27根只捕获一次。29个根文本全部与既有最终回归相等，live状态均未变化。

严格剩余回复环境资格为 **6/29**，六根都取得有限的根上界；已知原生来源策略资格为 **29/29**。旧的固定战后回复资格为0/29，其首个拒绝理由分布为药水13、角色8、卡牌7、玩家Power1。该旧理由不是严格证书逐组件的拒绝原因，后者目前没有完整诊断入口。

复用原29根回归的选定结果计数：21根记录了累计5,922次主胜利界剪枝。它包含已知来源策略，不是严格证书独占计数，也不是整个协调器所有成员的总计；原始DLL与逐根路径单列。根捕获耗时含克隆和JIT，不能当作认证本身开销。此批没有重新跑性能对照。

| 目标根 | 诊断发现 | 下一步边界 |
| --- | --- | --- |
| `dev-10-ironclad-monster` | Nibbit；全部初始牌在C58生成集合或已有AscendersBane证明内，无初始成长牌；持有BurningBlood/EternalFeather/Cauldron/Brimstone及ColorlessPotion/BottledPotential | 原生Nibbit全类、Brimstone、Cauldron和两药水已定向审查，仍需组合全部监听来源、生成与政策证明，不能仅凭这些目标都不直接治疗就认证 |
| `holdout-04-necrobinder` | 原生千足虫；THE_SCYTHE成长目标；HiddenGem和AscendersBane不在普通可生成集合；SpeedPotion/BottledPotential | 现有策略明确保留成长评价，纯战损主界与提前计划都受成长门禁；不能通过删除目标获得提速。SpeedPotionPower未在C58 Power集合中，其依赖尚未审查 |

本次额外读取八个目标来源的哈希单列在覆盖JSON：Nibbit只攻击、格挡及施加力量；Brimstone每个玩家侧回合施加力量；Cauldron获取时提供药水奖励，普通模式与TestMode的奖励生成不同。BottledPotential是洗回手牌、洗牌并抽五张，**不生成牌**；ColorlessPotion按原生解锁、玩家数与战斗过滤取得三个不同候选并选择或跳过。两者仍须保留进场/牌堆/抽牌回调、RNG和原始未知来源。

## 九项审计的状态与下一步

直接牌/药水、持续和触发回复、战前战后、上限增长、复活重设均有[第一阶段](native-health-source-audit-20261003.md)及[金币与上限修复](gold-max-hp-healing-20261003.md)的当前版本证据。本轮推进间接生成、敌人/宠物、战外边界和回调；全部九类均已有调查入口与明确未审范围，**完整可达来源审计尚未完成**。

下一批组件认证优先组合已有八池和已审查敌人动作、遗物、药水、Power、附着证明，逐来源取零、有限值或未知。未知初始来源即使暂时离场或在消耗堆仍永久拒绝。多份再生、药水额度、成长、条件回复及复活资源沿用既有政策；无法证明其他目标安全时继续原搜索。

本阶段新增运行时证书为 0。现有环境的根资格覆盖已诊断，旧主界剪枝计数已按其原版本复用；逐组件拒绝计数、严格证书独占剪枝和认证开销尚未测量。新审查机制也尚未新增原生/模拟合同或整请求性能测试。只做根来源、文档结构和数据口径检查，复用未变化源码的已验证构建及五文件部署，不重复跑既有成功场景。下一候选须先完成相关最小原生差分，再进行无插桩固定根性能对照。
