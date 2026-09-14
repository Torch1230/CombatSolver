# 选牌续执行批量扩展可行性（2026-09-14）

本文保留实施前的研究结论；用户随后要求完成三阶段，当前实现与证据以[批量实施记录](choice-continuation-expansion-implementation-20260914.md)为准。

用户原始请求：“再研究一下 能不能快速为所有这种具备选牌机制的卡牌 遗物 药水等做该优化？”

结论：可以按少数执行阶段成批扩展，大部分自身选牌卡和手动药水不必逐个编写出牌尾部；但不能直接把三张牌的白名单改成“所有选牌来源”。任意嵌套、回合触发循环和第三方状态仍需要新的可复制执行帧。以下为当前源码可行性研究，未启用更多生产来源，也未把静态覆盖当作性能收益。

## 范围与已有覆盖

沿用游戏0.111.0的[原生IL完整清单](choice-source-inventory-20260912.md)和[覆盖目录](../../coverage/combat-choice-sources.json)，本次核对当前选择入口、暂停资格与执行顺序，并定向读取41张单人卡的原生OnPlay前缀；没有重复运行全量IL扫描。原清单扫描1284个模型、5955个方法，读取失败0；85处显式选牌调用分为：

| 来源 | 数量 | 对本优化的意义 |
| --- | ---: | --- |
| 单人卡牌 | 41 | 共用自身选牌入口；当前仅杂技、早有准备、投掷匕首在合格状态下正式复用 |
| 药水 | 9 | 共用定义与应用；4种生成候选，5种直接从牌堆选择 |
| 战斗内遗物 | 5 | 4件玩家选择来源；低语耳环采用固定自动策略 |
| Power | 5 | 3种回合抽牌后、1种抽牌前、1种洗牌内选牌 |
| 获得遗物时的战斗外选牌 | 24 | 不进入本仓库战斗搜索，排除 |
| 多人专用卡“指导” | 1 | 仓库范围外，排除 |

这里的“41/9/5/5”是显式来源数，不能当作所有运行时分支数。旧清单还记录自动出牌、随机生成和授予相关Power等间接来源，共213个相关模型；这些入口可以嵌套打出选牌卡。升级武装没有玩家选择、未升级坚毅由RNG决定、只有一个合法候选等情形，也不一定产生可复用的兄弟分支。

## 最快的扩展方式

1. **先推广手动卡牌的自身选择边界。** [CardChoiceSupport.GetSpec](../../src/Search/CardChoiceSupport.cs)已汇总牌堆移动、弃牌、消耗、变形、复制、关键词与生成选择；[ResolveManualCardChoice](../../src/Search/SimulatedCombatState.ActionChoices.cs)和[CardChoiceSupport.Apply](../../src/Search/CardChoiceResolution.cs)是共同入口。已有续执行直接重入这个入口，并复用原出牌结算尾部，因此无需为41张牌分别复制OnPlay。最接近现状的是深谋远虑、微光、光子切割的抽牌后放回牌顶；再补格挡状态以覆盖生存者、武装、全息影像等同类牌。低成本前缀的牌即使接入也可能收益很小。
2. **给手动药水增加一个共同检查点。** [手动使用路径](../../src/Search/CombatBeamSolver.Expansion.cs)依次消耗槽位、BeforePotionUsed、Use、Apply选择、AfterPotionUsed、Power/死亡补偿及收尾。候选恢复点在Use完成、Apply之前；保存槽位和药水身份、history/shuffle起点与死亡集合，恢复时不再次消耗或重复触发钩子。4种生成药水共用[生成路径](../../src/Prediction/PotionOnUseSupport.cs)，重复随机池筛选与候选构造最值得先测；其余5种的Use前缀较薄，不能保证值得保存检查点。
3. **在现有回合前缀上补来源循环进度。** [RoundTransition](../../src/Search/CombatBeamSolver.RoundTransition.cs)已有抽牌前/后的检查点，不是从零开始。进一步跳过已执行的Power/Relic，需要将阶段、来源序号和源内进度显式存入执行帧。工具箱、既定事项发生在抽牌前；选择悖论、赌博筹码、烘焙手套及3种Power发生在抽牌后。不能简单重入整段foreach，否则先前的抽牌、随机生成、能量或状态触发会重复。

每个家族继续使用原候选顺序、选择额度和结算函数；不重写模拟器，不减少搜索预算，不改变路线质量。

## 为什么不能直接全量放开

- [当前资格](../../src/Engine/InCombat/Simulation/CombatPredictionSimulator.CardContinuation.cs)只允许单层、一次手动出牌，并要求没有活动抽牌/伤害/格挡计数等不支持状态；[Search中的请求](../../src/Search/SimulatedCombatState.CardContinuation.cs)还限定自身手牌弃牌。推广效果与牌堆的同时，必须复制实际存在的活动字段，不能仅改类型判断。
- [历史复制](../../src/Engine/InCombat/Simulation/CombatPredictionHistory.CardContinuation.cs)目前只识别出牌开始、攻击/伤害、抽牌与风险记录，并要求trace来源归属本牌。格挡、候选生成、召唤或其他触发来源需要各自增加重映射合同；生成候选还可能没有进入任何牌堆。普通稳定快照不能代表这些暂停状态。
- 药水4种生成选择的[探测路径](../../src/Search/CombatBeamSolver.ParallelExpansion.cs)当前先完整执行无主选择的Use与后置钩子，再从历史取候选；另外5种直接从父状态生成选择。新增检查点必须保留这些原有探测语义；不能为了提前暂停就把GetSpec整体搬到钩子前而改变候选。可先保留原探测，在独立前缀种子上保存Use后边界，再让兄弟恢复。
- 外层恢复后再次遇到选择，可像当前实现一样退出作用域并完整回放。要继续优化内部选择，需保存消耗/弃牌循环、自动出牌次数、子CardPlay以及洗牌抽牌进度。尤其“抉择，抉择”和“计策”不能由一个外层帧解决全部嵌套。
- [低语耳环](../../src/Search/SimulatedCombatState.AutoPlay.cs)当前通过固定Vakuu策略回答选择，不枚举玩家选择兄弟；其自动出牌可以另做重复工作分析，但不能因为原生调用CardSelectCmd就计入此次选牌复用收益。
- 第三方CardChoice/PotionChoice登记只声明选择定义与应用，没有承诺暂停时状态可复制。未知外部状态继续拒绝检查点；若以后扩展第三方，需显式的阶段/状态复制合同及原生差分，不能自动接纳任意委托或Task。

## 建议的准入与收益判断

先做“抽牌后放回牌顶＋格挡前缀”一批，再做4种生成药水，随后评估回合触发循环；生成选牌卡与身份变更尾部按实际前缀状态补齐。每批用一个共同实现覆盖同类来源，逐项声明已支持的前缀状态和未支持回退。

测试应覆盖完整状态、历史和九条RNG、全部选择与再次访问兄弟、身份变化/升级/免费/延迟效果、嵌套回退、DOP隔离及取消排空；新增历史家族另做原生差分。通过最小合同后才测目标整场与未命中哨兵，同工作量严格比较完整动作及质量。

性能取决于跳过的重复前缀是否超过捕获、复制更大暂停状态和延长生命周期的成本。当前三牌的[正式整搜报告](choice-continuation-search-20260914.md)已显示整搜收益远小于单卡原型，不能给其他来源预报同样百分比。若要快速提高覆盖，按阶段推广可行；若要求全部原版、任意嵌套、第三方且都有收益，需要分批验证，不能一次承诺。

## 逐来源矩阵

下面61项包含多人/固定策略两个明确标记；24项战斗外遗物的原始分类另保留在[JSON](choice-continuation-expansion-20260914.json)。除三张牌的既有条件接入外，均为候选研究。

| 类别 | 官方名称 | 机制 | 扩展家族 |
| --- | --- | --- | --- |
| Cards | 富足（`ABUNDANCE`） | 3选1，生成能力牌 | 生成候选 |
| Cards | 杂技（`ACROBATICS`） | 抽牌后手牌N选1弃牌 | 已正式接入（有条件） |
| Cards | 武装（`ARMAMENTS`） | 未升级时N选1升级；升级后全体 | 先获得格挡 |
| Cards | 下去！（`BEGONE`） | 手牌N选1变化 | 其他自身选牌 |
| Cards | 烙印（`BRAND`） | 手牌N选1消耗 | 其他自身选牌 |
| Cards | 燃烧契约（`BURNING_PACT`） | 手牌N选1消耗，再抽牌 | 其他自身选牌 |
| Cards | 冲锋！！（`CHARGE`） | 抽牌堆N选k变化 | 其他自身选牌 |
| Cards | 洁净（`CLEANSE`） | 抽牌堆N选1消耗 | 召唤／奥斯提／随机候选 |
| Cards | 宇宙冷漠（`COSMIC_INDIFFERENCE`） | 弃牌堆N选1置于牌顶 | 先获得格挡 |
| Cards | 投掷匕首（`DAGGER_THROW`） | 攻击、抽牌后N选1弃牌 | 已正式接入（有条件） |
| Cards | 抉择，抉择（`DECISIONS_DECISIONS`） | 选1技能，再重复自动打出 | 选择后重复自动出牌 |
| Cards | 发现（`DISCOVERY`） | 生成候选后至多选1 | 生成候选 |
| Cards | 清淤（`DREDGE`） | 弃牌堆N选k回手，受手牌余量限制 | 其他自身选牌 |
| Cards | 双持（`DUAL_WIELD`） | 攻击/能力N选1，再复制 | 其他自身选牌 |
| Cards | 微光（`GLIMMER`） | 抽牌后手牌N选k放牌顶 | 抽牌／攻击后放回牌顶 |
| Cards | 坟冢爆射（`GRAVEBLAST`） | 弃牌堆N选1回手 | 其他自身选牌 |
| Cards | 护驾！！！（`GUARDS`） | 手牌任意子集变化，理论2^N | 其他自身选牌 |
| Cards | 手上技法（`HAND_TRICK`） | 合法技能N选1添加奇巧 | 先获得格挡 |
| Cards | 头槌（`HEADBUTT`） | 弃牌堆N选1放牌顶 | 其他自身选牌 |
| Cards | 传承之锤（`HEIRLOOM_HAMMER`） | 无色手牌N选1复制 | 其他自身选牌 |
| Cards | 隐秘匕首（`HIDDEN_DAGGERS`） | 手牌N选k弃牌，再生成小刀 | 其他自身选牌 |
| Cards | 全息影像（`HOLOGRAM`） | 弃牌堆N选1回手 | 先获得格挡 |
| Cards | 涅奥之怒（`NEOWS_FURY`） | 弃牌堆选择0..k回手 | 其他自身选牌 |
| Cards | 夜魇（`NIGHTMARE`） | N选1，下回合生成复制品 | 其他自身选牌 |
| Cards | 光子切割（`PHOTON_CUT`） | 抽牌后手牌N选k放牌顶 | 抽牌／攻击后放回牌顶 |
| Cards | 早有准备（`PREPARED`） | 手牌N选k弃牌 | 已正式接入（有条件） |
| Cards | 净化（`PURITY`） | 手牌选择0..k消耗 | 其他自身选牌 |
| Cards | 类星体（`QUASAR`） | 生成候选后至多选1 | 生成候选 |
| Cards | 内存清理（`SCAVENGE`） | 手牌N选1消耗，排除自身 | 其他自身选牌 |
| Cards | 雕琢打击（`SCULPTING_STRIKE`） | 尚无本地虚无的手牌N选1 | 其他自身选牌 |
| Cards | 降灵（`SEANCE`） | 抽牌堆N选k变化 | 其他自身选牌 |
| Cards | 秘密技法（`SECRET_TECHNIQUE`） | 抽牌堆合法技能/攻击N选1 | 其他自身选牌 |
| Cards | 秘密武器（`SECRET_WEAPON`） | 抽牌堆合法技能/攻击N选1 | 其他自身选牌 |
| Cards | 探寻打击（`SEEKER_STRIKE`） | 抽牌堆限定前部候选中选1 | 召唤／奥斯提／随机候选 |
| Cards | 响指（`SNAP`） | 尚未保留的手牌N选1 | 召唤／奥斯提／随机候选 |
| Cards | 飞溅（`SPLASH`） | 生成候选后至多选1 | 生成候选 |
| Cards | 生存者（`SURVIVOR`） | 手牌N选1弃牌 | 先获得格挡 |
| Cards | 深谋远虑（`THINKING_AHEAD`） | 抽牌后手牌N选1放牌顶 | 抽牌／攻击后放回牌顶 |
| Cards | 重构（`TRANSFIGURE`） | 手牌N选1永久修改 | 其他自身选牌 |
| Cards | 坚毅（`TRUE_GRIT`） | 升级后N选1消耗；未升级由RNG决定 | 先获得格挡 |
| Cards | 指导（`TUTOR`） | 多人专用选牌 | 多人范围外 |
| Cards | 许愿（`WISH`） | 抽牌堆N选1回手 | 其他自身选牌 |
| Potions | 灰水（`ASHWATER`） | 手牌任意子集消耗，理论2^N | 药水牌堆选择 |
| Potions | 攻击药水（`ATTACK_POTION`） | 3选1，可空选 | 药水生成候选 |
| Potions | 无色药水（`COLORLESS_POTION`） | 3选1，可空选 | 药水生成候选 |
| Potions | 预知之滴（`DROPLET_OF_PRECOGNITION`） | 抽牌堆N选1回手 | 药水牌堆选择 |
| Potions | 赌徒特酿（`GAMBLERS_BREW`） | 手牌任意子集弃掉并补抽，理论2^N | 药水牌堆选择 |
| Potions | 液态记忆（`LIQUID_MEMORIES`） | 弃牌堆N选1回手并本回合免费 | 药水牌堆选择 |
| Potions | 能力药水（`POWER_POTION`） | 3选1，可空选 | 药水生成候选 |
| Potions | 技能药水（`SKILL_POTION`） | 3选1，可空选 | 药水生成候选 |
| Potions | 癫狂之触（`TOUCH_OF_INSANITY`） | 符合能量/星费条件的N选1变免费 | 药水牌堆选择 |
| Powers | 熵（`ENTROPY_POWER`） | 每回合手牌N选k变化 | 回合抽牌后 |
| Powers | 既定事项（`FOREGONE_CONCLUSION_POWER`） | 下回合抽牌堆N选k回手 | 回合抽牌前 |
| Powers | 计策（`STRATAGEM_POWER`） | 每次洗牌后从抽牌堆N选k回手 | 洗牌内选择 |
| Powers | 必备工具（`TOOLS_OF_THE_TRADE_POWER`） | 每回合抽牌后N选k弃牌 | 回合抽牌后 |
| Powers | 暴政（`TYRANNY_POWER`） | 每回合抽牌后N选k消耗 | 回合抽牌后 |
| Relics | 选择悖论（`CHOICES_PARADOX`） | 仅开战，生成k张候选选1并保留 | 回合抽牌后 |
| Relics | 赌博筹码（`GAMBLING_CHIP`） | 仅开战，手牌任意子集弃牌补抽 | 回合抽牌后 |
| Relics | 烘焙手套（`TOASTY_MITTENS`） | 每回合手牌N选1消耗 | 回合抽牌后 |
| Relics | 工具箱（`TOOLBOX`） | 仅开战，随机候选选1 | 回合抽牌前 |
| Relics | 低语耳环（`WHISPERING_EARRING`） | 首回合瓦库自动选择/出牌 | 固定自动策略 |
