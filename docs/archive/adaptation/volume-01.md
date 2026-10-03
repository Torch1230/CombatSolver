# CombatSolver 适配验证入口历史卷 01

## 求解策略回归闭环

### `DECIMILLIPEDE-LATE-DEATH-REATTACH-524`（正常行动后死亡与结束回合复活通道）

闭环：原版 `DEAD_MOVE` 没有 `MustPerformOnceBeforeTransitioning`，肢节即使已经错过本轮行动槽、随后才死亡，也会在下一玩家阶段准备怪物行动时直接进入 `REATTACH_MOVE`。模拟删除复活阶段的额外“必须实际执行当前动作”限制，统一服从行动状态机；严格差分同时采集仍留在阵容中的死亡怪物 Power。搜索层在结束回合状态转换制造复活窗口时登记既有 `RevivalWindow`，让 Doom、毒等回合结算死亡也能进入按药水数分组的复活长线保留。

结果：严格差分 runId `b481833954f741a68faf86c45584755f` 通过。用户 `0.13.27` 亡灵契约师存档在修复前的当前源码 runId `d7fa340f82674cb0847f4a80d301f9af` 出现三次计划外重算；最终 runId `40de641e3af44100a83c63734ffd3544` 第 `6` 回合两药胜利，第 `2-6` 回合全部精确复用，`UnexpectedReplans=0`。上一份千足虫 runId `e4039058025849c893710f917770550e` 和双小啃兽普通/增量回归同样通过。

### `BUG-REPORT-FORENSICS-478 / POTION-USED-UI-479`（逐检查点完整取证与用药计数，历史记录）

闭环：每个 current/recent 检查点同时保存 metadata、结构化 replay-state、游戏原生 `NetFullCombatState` 和检查点时刻的内存跑局存档。metadata 固定当时的设置、结果与全部 RNG；replay-state 固定五个有序牌堆及逐牌可序列化状态/动态字段、Power、遗物、Orb、怪物私有字段、阵容和完整行动历史。结果对象分别保留本战实际用药和路线未来计划用药。这里记录的 `0.13.21` UI 曾只显示实际用药；`0.13.33` 已改为显示二者之和，即预计整场用药数。

结果：runId `b6266146d6094db39846e5cbe08c8216` 在活动战斗和战后各导出一份 ZIP，测试实际打开并解析 current/recent 四类材料，验证完整 Run RNG、玩家 RNG/odds、五牌堆、阵容、历史、设置和非空原生包。runId `a5a3148d2c71484f808bdc4cc7ccd597` 以 Instant/0 秒执行两回合格挡药路线，第 2 回合精确复用、实际已喝药为 `1`、路线未来用药为 `0`，非预期重算为 `0`。

### `BUG-REPORT-FORENSICS-469`（跨战斗时机快速导出）

闭环：导出器不再依赖点击时仍处于问题战斗。生命周期缓存当前与最近一场，每场在战斗开始、搜索/复用、结果和结束边界记录完整 Run RNG、玩家 RNG/odds、ContinuationState、状态摘要、路线与重算审计，并保存内存跑局快照；磁盘开战存档和本场日志片段在可用时附加。导出发生在下一场战斗时，`current` 与 `recent` 同时存在；地图或主菜单导出时仍保留 `recent`。

结果：runId `d749d1e1cb7544968c2ef0b6842bf306` 在活动战斗和返回主菜单后连续导出两份 ZIP。测试逐项解析 current/recent session 和首个 checkpoint，Run RNG 流不少于 9 项，玩家 RNG/odds 均存在；战后 `combat-state.json` 正确为 inactive，recent 仍含开战、搜索请求、搜索完成、战斗结束四个检查点和可加载的内存 `current_run.save`。毫秒文件名允许同秒连续导出。另以 runId `954c2d4a2c17494f926224b8f489a59a` 完整执行 5 回合、跨两次洗牌，第 2-5 回合全部精确复用，确认采集不改变 RNG。

### `SMART-POTION-COUNTERFACTUAL-461/463`（条件式无药反事实审计）

闭环：统一 Beam 仍同时搜索无药和用药路线；不为药水常驻单开搜索。只有 Smart 将要采用“用药胜利、无药未胜”，且现有无药代表不足以证明药水达到战略省血门槛时，才用同一配置追加一次 Disabled 审计。审计找到无药胜利后以整场战损重新计算省血；找不到则把用药胜利视为当前预算内的救命路线。审计成本并入总搜索时间、分配和 GC 数据。

结果：用户淤泥旋螺状态 runId `322b6901b1cd48428daf8f9b326bd953` 中，初次混合搜索的三药路线报告省血 0/要求 27；无药审计找到第 5 回合战损 2，最终选择无药，完整执行第 2-5 回合精确复用且零重算。最小致死 runId `178e099396d94aeebee708ad2b4dfcb8` 中，无药路线死亡，格挡药路线第 2 回合胜利；审计没有错误否决救命药。

### `NECROBINDER-OSTY-RAVENOUS-453/454`（奥斯蒂复活与吞食眩晕）

闭环：原版 `OstyCmd.Summon` 区分首次创建和复活已有实体；后者保留不会随死亡移除的 `DieForYouPower`，不得再次叠加。`RavenousPower.AfterDeath` 对幸存蛞蝓调用 `CreatureCmd.Stun`，立即以 `STUNNED` 替换当前行动，并保存该行动为完成眩晕后的后继；不能用不带状态身份的“跳过一次”代替。

结果：定向 runId `f26d95bf947b489188343c369aed296f` 两项状态差分通过。用户种子 `9R4CY7ZZZ0VM` 的 `CORPSE_SLUGS_WEAK` 完整回归 runId `2b090c725e804f1db2a1d420c5570e4e` 在 Custom `5/60s`、Instant/0 秒下第 5 回合结束，预计/实际战损均为 7，第 2-5 回合全部 `SEARCH_REUSED`，状态不匹配和其他非预期重算均为 0。

### `SECONDARY-END-AND-GENERATION-447-450`（终局次要敌人与生成选择）

闭环：原版胜利条件以“没有存活的主要敌人，且没有 Hook 阻止结束”为准；复活等待期继续由 Illusion、Adaptable 和 Reattach 的阻止结束 Hook 控制。搜索快照不再额外要求已经不影响胜利的次要敌人有效生命归零。结束回合已经提交 `IsInProgress=false` 时同样属于终局，不能再扩展空过回合或卡牌动作。单张生成牌接口在已结束战斗中返回失败结果，不再索引批量接口的空数组。

结果：用户 Fogmog 存档 runId `7e27030aafee4994905d8a6c02260fc1` 从 `15000` 节点、`486` 回合、`470` 次洗牌收敛为 `2289` 节点、第 `3` 回合结束，第 2-3 回合精确复用且零重算。定向 runId `260dab8461bf4be78617ed4f6dc68853` 中，一击杀死 1 HP Fogmog 后仍有 6 HP EyeWithTeeth 幻象次要敌人，搜索只展开 `2` 个节点并与实机同在首回合结束。生成选择 runId `da51c916376047a69fc95502a569d1bb` 为 `4/4`，实验体 runId `c562482ad1934635ad1b01abc2b526bd` 仍完整经历三形态、第 `6` 回合结束、零重算。

### `REGENT-PRINT-BRANCH-441/446`（生成牌选择保护窗）

闭环：固化实机种子 `HRQQLH4SM3EB` 的缩小甲虫初始牌序，牌组包含类星体、彰显威权、光谱偏移、隐秘藏品和两张诅咒。所有生成候选仍按真实 RNG 全量展开，但具体候选只额外保留当前及后续两回合，避免生成卡链永久占据 Beam。`Fisticuffs` 显式注册普通攻击 Mirror，等量格挡继续由实际伤害后的求解器补偿结算。

结果：基线 runId `5a537b5142c14ceda23de5c2d1832fea` 为 `6000` 节点、`12519` 选牌、`37061` 转移、`4.09 GB / 10.39 s`、第 5 回合；最终 runId `947ce5ac5b2647c18de08b9d5e627333` 为 `3182` 节点、`5426` 选牌、`18371` 转移、`2.07 GB / 6.52 s`、第 4 回合。完整自动执行预计/实际掉血均为 `3`，第 2-4 回合全部精确复用，`UnexpectedReplans=0`，日志无 Fisticuffs 假警告。

### `POWER-SHADOW-LIFECYCLE-425-428`（Power 影子状态与整战）

闭环：机甲首轮路线曾在第 2 回合多出 `BURST_POWER:2`。模拟实体 Power 已按原版回合末移除，但 Hook 使用的 `PowerAmountPredictionState` 仍保存旧值并在下一次同步时写回。一次性 Power 清理现在同时消费影子数量；所有 Power 数量影子在一个 Hook 批次同步后立即删除，后续批次从当前模拟 Power 重建。

结果：强制 Burst 重复升级防御 runId `b5d4bff21934430baa8caf847b654e03` 第 2 回合精确续用；重复出牌 11 个 Hook runId `aa11595630ba40f4803719f33892c0b8` 与伤害 Power 十四场 runId `3a05fcb0d68347ce9c6dfa785384909c` 全部通过。最终机甲 runId `c28627123f6241cfa3e9f75fac740ea9` 第 7 回合结束，第 2-7 回合全部精确复用、`UnexpectedReplans=0`。旧 31/28 战损来自 Burst 残留的虚假收益，不再作为正确路线基线。

### `ROSTER-SOURCE-GATE-408`（51 个阵容变化调用点）

闭环：CoverageCatalog 递归扫描正式模型及其异步状态机中 `CreatureCmd.Add/Escape`、`PlayerCmd.AddPet` 与 `OstyCmd.Summon`。正式来源必须匹配 `MonsterSpawnSupport`、`DeathPowerSupport`、Osty 召唤/复活、怪物逃跑或战斗开始快照；Mock 与多人专属单列。测试对象三形态、幻象、千足虫重接属于自定义复活状态机，由既有逐行动差分和整战零重算证据覆盖。

结果：当前 `51` 个调用点中 `47` 个正式单人来源受支持、`3` 个 Mock、`LegionOfBone` 为多人专属，`Unresolved=0`。该检查并入普通 `--verify`。

### `AUTOPLAY-NESTED-CHOICES-403-407`（4 个运行场景 + 19 个源码入口）

闭环：从原程序集反向扫描 `CardCmd.AutoPlay` 与 `CardPileCmd.AutoPlayFromDrawPile`。所有模拟自动出牌在确认卡牌实际开始执行后，立即通过当前动作的有序选择游标解析该牌产生的选牌；多张自动牌链遇到缺失计划即停在准确选择点。完整战斗分别覆盖横祸、破灭和骚动，药水差分覆盖蒸馏混沌。

结果：横祸 runId `5c7ebc3043544c729618408bd608dc8d` 自动打出生存者并弃掉晕眩；破灭 runId `9e923f232ece4fa680a5f687cfb2d272` 从抽牌堆顶自动打出生存者并弃牌；骚动 runId `9dd66522ea4e416db06f2231593d6398` 自动打出觅踪打击并把防御移入手牌。三场后续回合均 `SEARCH_REUSED`、`UnexpectedReplans=0`。蒸馏混沌 runId `7187663997164f0785e87987c3af2fcb` 自动打出生存者后选择晕眩，原版与模拟完整状态和 RNG 一致。IL 门禁识别 `19` 个调用点：`18` 个单人来源受支持、Imitation Learning 为多人专属、未知 `0`。

### `COMBAT-CHOICE-SOURCE-GATE-402`（85 个原版调用点）

闭环：CoverageCatalog 不再只依赖手写的“已知选牌列表”，而是递归读取游戏正式 Card、Potion、Power、Relic 和 Enchantment 模型的原始 IL及异步状态机，定位所有 `CardSelectCmd` 调用。每个调用点必须匹配 `CardChoiceSupport`、`PotionChoiceSupport`、`TurnStartChoiceSupport`、首回合选择接管或 Vakuu 固定选择器；获得遗物、多人专属和 Mock 单独分类。

结果：当前 `85` 个调用点中 `60` 个为受支持的单人战斗选择，`24` 个只在 `AfterObtained` 执行，`Tutor` 为多人专属，`Unresolved=0`。`--verify-combat-choices` 已并入普通 `--verify`，未来游戏版本增加新调用点时不会静默落入玩家界面。

### `INITIAL-NATIVE-START-EFFECTS-400`（7 项首回合遗物语义）

闭环：在工具盒强制启用 Start 阶段搜索的同一战斗中，组合宝石面具、节庆礼炮、烦人谜盒、力量电池、扭曲漏斗、石化蟾蜍和低语耳环。牌组额外加入能力牌燃烧与 0 费攻击愤怒，确保宝石面具和力量电池都有合法候选。原版完成全部前置 Hook 后，以完整状态戳比较玩家资源、敌方生命与中毒、药水、手牌/抽牌、逐牌费用、历史和全部战斗 RNG。

结果：runId `82fb0ef9c424473d92c6d5d6c7e77ce6` 为 `Passed`。低语耳环按原版 Vakuu 固定顺序支付费用并连续自动出牌，首回合直接结束战斗；预测与原版仍命中 `INITIAL_SETUP_STATE_MATCH`，`CombatEndedTurn=1`、`Unmirrored=0`。不含低语耳环的前置状态组合 runId `19b4b156ac0a42e7a94466a4f6ec0289` 同样通过。高密度生存者牌组 runId `8426e2573f71427fb03e31a3b46aecab` 强制让 Vakuu 自动打出两张生存者，原版连续两次选择 `SURVIVOR`，完整状态仍一致。

| 遗物 | 前置语义 | 结论 |
|---|---|---|
| `JeweledMask` | 优先非先天能力牌，`CombatCardSelection` 随机一张，设为本回合 0 费并移入手牌 | 牌、费用、牌堆与 RNG 一致；原版不是玩家选择 |
| `FestivePopper` | 第一回合开始对所有可命中敌人造成 9 点无倍率伤害 | 多目标生命与死亡链一致 |
| `VexingPuzzlebox` | `CombatCardGeneration` 生成一张本职业牌，设为本回合 0 费并触发入场 Hook | 生成牌及 RNG 一致 |
| `PowerCell` | Start 前稳定洗牌全部当前 0 费非 X 牌并取两张入手 | 由 Start 快照精确继承 |
| `TwistedFunnel` | Start 前给全部可命中敌人施加 4 层中毒 | Power 与后续伤害一致 |
| `PetrifiedToad` | 战斗开始晚期取得石头形药水 | 药水槽由 Start 快照精确继承 |
| `WhisperingEarring` | 普通 AutoPrePlay 后最多 13 次：左起首张可打牌、正常付费、最左敌人、Vakuu 行优先选牌 | 自动出牌、嵌套选择、资源、目标和终局一致 |

### `INITIAL-PRE-PLAY-CHOICES-394-398`（5 个场景）

闭环：在原版 `CombatManager.StartTurn` 已进入 `PlayerTurnPhase.Start`、但尚未执行 `SetupPlayerTurn` 时建立搜索根状态，完整模拟能量、首手抽牌、回合开始 Hook 和 `RunAutoPrePlayPhase`。工具盒与选择悖论使用同一战斗牌生成 RNG 枚举生成候选；赌博筹码枚举可选弃牌子集；烘焙手套枚举首手消耗；助能生存者继续展开其弃牌选择。选中路线通过原版 `ICardSelector` 消费，随后比较完整 Play 状态与 RNG。

结果：工具盒、选择悖论、烘焙手套、赌博筹码、助能生存者 runId 分别为 `5e65744c7e4c4f59ac601fd50b561573`、`86368fdb8c8849dda82f006cbb176f05`、`106f9700f17a41c5b2661ee41a1b3315`、`e6f6c11b612e4dcda9c77ef554038f29`、`2163afe3501b439eaf866bad22b3934b`，全部 `Passed`。五场均记录计划选择和原版实际选牌，并以 `INITIAL_SETUP_STATE_MATCH validation=exact_state_text` 完成；助能场实际弃掉 `DEFEND_IRONCLAD`，没有玩家干预。

| 前置来源 | 搜索语义 | 结论 |
|---|---|---|
| `Toolbox` | 三张无色生成牌选一，候选与后续牌序共用原战斗 RNG | 选择 `PRODUCTION` 后完整状态一致 |
| `ChoicesParadox` | 五张本职业生成牌全部先获得保留，再选一入手 | 五个候选进入搜索，原版提交一致 |
| `ToastyMittens` | 从首手选择一张消耗，再获得力量 | 首回合和既有未来回合均由计划选择 |
| `GamblingChip` | 可选弃任意张，再按相同数量抽牌 | 空选择和保留子集均可形成路线，选中子集原版一致 |
| `Imbued` | 首回合自动打出技能，并递归展开该技能的嵌套选择 | 生存者弃牌由搜索决定并自动提交 |

### `SOLVER-REGRESSION-BATCH-061`（2 个场景）

闭环：最终 `0.7.4` Release DLL 在真实可见游戏中分别运行防御路线保留和生成牌显示名场景。防御场景固定两只小啃兽的 `BUTT_MOVE + SLICE_MOVE`，给玩家 `3` 敏捷、三张防御及两张攻击牌，直接断言首轮预计掉血、最高可起防、实际起防与卖血；显示名场景由原生卡牌实例生成小刀，断言首个行动的内部 ID 和界面标题，再由全自动打完整场。

结果：runId `0c5ebe1db9ea47489ebe745da454be67`、`fbe66514252c43d988e3ece3081390df` 均为 `Passed`、`mainThread=true`。防御场景面对 `22` 点来袭选择三张防御，结果为 `HpLost=0`、`Block=24/24`、`SoldHp=0/5`；生成牌场景的行动内部 ID 保持 `SHIV`，显示标题为官方简中“小刀”，未镜像数为 `0`，并由全自动在第 `3` 回合结束战斗。开发期第一次生成牌测试 runId `d31de153bea54237bc44adf6f16a47e5` 在场景注入前失败，原因是 PowerShell 把空行动 ID 数组序列化为 `null`；修正测试协议后才完成上述生产逻辑闭环，失败不计为功能验证。

| 回归项 | 预期 | 结论 |
|---|---|---|
| 防御分支保留 | 高输出评分不能在 Beam 中挤掉当前可达到的最低战损路线 | 连续三张防御保留至回合结束，完整挡住 `22` 点来袭 |
| 主动卖血基线 | 可防住却少防的掉血必须计入卖血，不能被误写成不得不掉血 | 最低战损基线恢复为 `0`；入选路线 `0` 掉血、`0` 卖血 |
| 生成牌官中名称 | 不在初始牌堆中的牌也按当前语言和升级等级显示原生标题 | `SHIV` 显示为“小刀”；同一缓存路径覆盖升级标题“小刀+” |

### `POTION-SHUFFLE-POLICY-BATCH-060`（3 个场景）

闭环：最终 `0.7.3` Release DLL 在同一个真实可见游戏 PID 中连续运行低收益药水、高收益药水和跨洗牌三份固定场景。前两场分别断言首轮路线的药水数量、省血值、门槛淘汰数和未镜像数，并由全自动打完整场；第三场清空四牌堆后注入五张防御与五张打击，要求首轮至少搜索三回合、跨过一次洗牌，并在真实第 `3` 回合命中首轮缓存状态。

结果：runId `8e212ecbca6d4f35adea5fa0a23b1ccd`、`6d2ec24dfd3142ee834cf885f089eee5`、`2fbe1dd919a449138b86417055d0ba40` 均为 `Passed`、`mainThread=true`。`1 HP` 毛绒伏地虫场景拒绝两条火焰药水候选并保留药水；玩家带易伤面对两只尼比特时，格挡药水使完整路线少掉 `12 HP`，高于 `9 HP` 门槛并由全自动真实使用。跨洗牌场景首轮为 `Turns=3;Shuffles=1`，第 `2/3` 回合日志均记录 `SEARCH_REUSED validation=exact_state_text` 和 `expanded=0`，第三回合按预测结束战斗。开发期首个高收益场景暴露卖血比较把用药与不用药混组、进而剪光无药基线的问题；按本回合消耗槽位隔离卖血基线后复测通过。

| 策略项 | 预期 | 结论 |
|---|---|---|
| 药水最低收益 | 相对同批最佳无药路线，每瓶药至少减少 `9 HP` 的整场预计战损 | 省血 `0` 的火焰药水被拒绝；省血 `12` 的格挡药水被选择并真实使用 |
| 药水与卖血解耦 | 不喝消耗品不能因为另一条路线喝了格挡药水而被算作主动卖血 | 无药基线稳定保留，尼比特场景最终卖血为 `0/5` |
| 一次洗牌预测 | 克隆 `Shuffle` RNG 后按原生 `StableShuffle` 推进，允许跨一次普通洗牌，第二次前停止 | 洗牌后第 `2/3` 回合均与真实完整状态一致并零节点复用 |
| RNG 状态去重 | 可见牌堆相同但 RNG 游标不同的节点不能被合并 | 七组战斗 RNG 游标与洗牌次数进入双 64 位状态指纹 |

### `SOLD-HP-POLICY-BATCH-059`（3 个场景）

闭环：最终 `0.7.1` Release DLL 在真实可见游戏中运行三份固定牌组。夹具通过原生 `CardPileCmd.RemoveFromCombat` 清空手牌、抽牌堆、弃牌堆和消耗堆，再注入精确手牌与牌序并开启全自动。首轮后台求解结果由无人脚手架直接断言，随后由原生出牌与怪物行动打完整场；三史莱姆场景额外核对第二回合 `SEARCH_REUSED` 后的历史卖血记录和本局累计值。

结果：runId `e3089dac22b34566bbc614adc16f6e55`、`a467e0c01b7746668b264fdbf0c1fad2`、`31fb243e71744ae4b46baac36f4bdcb3` 均为 `Passed`、`mainThread=true`。有防御选择的毛绒伏地虫场景首轮为 `0/5`，剪掉 `6` 条超预算路线；全攻击对照场景实际连续掉血 `12 + 6`，卖血仍为 `0`；三史莱姆场景选择重锤放弃防御，首轮精确记为 `4/5`，剪掉 `10` 条超预算路线。其第二回合复用后仍输出 `turn=1 hp_lost=4 sold_hp=4`，本局累计卖血保持 `4`、未来卖血为 `0`。测试断言拆分到独立 partial 文件后，又以最终 DLL 运行 runId `c6215f5d5876415bbf5a4025b82cb02a`，主动卖血仍为 `4/5` 并完整结束战斗。

| 策略项 | 预期 | 结论 |
|---|---|---|
| 同回合最低可达到战损基线 | 只把相对可达到最低战损多承受的生命损失算作主动卖血 | 无防御的 `18` 点实际战损全部归为不得不掉血；放弃可用防御承受 `4` 点时精确归为卖血 |
| 整场硬预算 | 普通战斗累计卖血不得超过 `5`，超额路线在回合边界直接剪枝 | 两个含防御场景分别剪掉 `6/10` 条超预算路线，最终均未超阈值 |
| 跨回合历史保留 | 路线复用只重算未来卖血，不删除已发生回合的逐回合记录 | 第二回合零节点复用后第一回合 `sold_hp=4` 仍在，累计值没有归零或重复计算 |
