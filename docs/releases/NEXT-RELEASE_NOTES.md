# CombatSolver 下一版本（开发中）

## 简体中文

### 能力牌路线专项优化

- 求解器现在会为能力牌保留专门的完整战斗搜索路线。过去，一些能力牌需要先花费能量、承受少量即时战损，收益却要到后续回合才出现，这类路线很容易在开牌后立刻被高即时伤害或高即时格挡的路线挤掉。现在只要能力牌具备合理的后续兑现条件，求解器就会给这条启动路线一个有限但显著更长的观察窗口，并一路计算到战斗结束后再比较最终战损。
- 能力牌路线不会因为“想开能力”就直接获得最终胜利。所有候选仍然需要真实支付费用、真实执行抽牌与随机结果，并接受敌人行动、手牌上限、药水消耗和战损结算；只有完整获胜且最终结果更好的路线才能替换原路线。没有收益、启动过晚或导致死亡的能力路线仍会被淘汰。
- 新增覆盖铁甲战士、静默猎手、故障机器人、储君、亡灵契约师和无色卡池的统一能力牌识别与估值框架，共登记 104 张单人能力牌。不同卡池分别维护自己的判断，不再把所有能力牌塞进一套通用数值公式；多人专属能力牌不参与单人求解。
- 能力牌不再只按卡面上的伤害或格挡数字估值。求解器会综合考虑启动费用、预计剩余回合、实际可触发次数、未来牌序、抽牌与弃牌、手牌拥塞、后续攻击次数、小刀数量、毒层维持、星能消费、球、消耗牌、召唤物及敌人数等因素。复杂能力采用保守估值来决定“这条路线值得继续搜索”，最终选择仍以真实战斗结果为准。

### 静默猎手的精细建模

- **灵动步法**不再只被理解为“获得几点敏捷”。求解器会检查它是否让一张防御牌跨过格挡阈值、是否因此省下一张牌或一点能量，以及省下的资源能否继续转化为输出。除非后续确实无法获益，否则会明显提高尽早启动的意愿。
- **余像**按照后续实际出牌次数估算格挡。能稳定兑现至少约一张防御牌价值时，会被视为值得启动的强能力；不能出足够牌时不会虚高估值。
- **精准、刀扇、无尽刀刃和幻影之刃**会读取实际可获得、可支付和可打出的小刀数量。估值分别考虑小刀增伤、群体攻击、延迟生成、第一张小刀增伤、保留以及控牌序价值，而不是假设所有潜在小刀都一定能打出。
- **触媒、毒雾和涂毒**按照真实毒层与后续触发窗口计算。触媒会重视至少一次可兑现的额外毒触发；毒雾会考虑长线叠毒和维持毒层；涂毒只计算真正穿过格挡的攻击命中，并保留其高费用、低收益时不应强开的判断。
- **必备工具、速行者、谋划专家和计划妥当**会结合未来抽牌、弃牌收益、弃掉诅咒或减益牌、弃牌触发的免费出牌、保留手牌价值和爆牌风险。计划妥当会更倾向通过专门搜索验证整条控牌路线，而不是依赖一个粗略静态分数。
- **磨蚀**会区分三费硬开和通过弃牌触发免费打出的情况；**群蛇形态**会按实际可打出的牌数评估随机伤害；**跟踪**会计算后续所有处于虚弱状态敌人受到的攻击增伤。
- **幽魂形态**会同时考虑当前保命价值、无实体覆盖的关键回合以及后续持续失去敏捷的代价。长线战斗中不会仅因为它是强牌就过早启动；临近战斗结束或能够避免致死伤害时则会显著提高优先级。

### 其他角色与无色能力牌

- 铁甲战士、故障机器人、储君、亡灵契约师和无色能力牌已经完成第一阶段的逐卡登记、启动倾向、触发条件和保守收益投影。求解器能够识别防御成长、力量与消耗联动、球与集中、星能消费、灾厄、召唤物、牌流和延迟收益等主要机制，并为值得尝试的能力路线保留搜索机会。
- 这一阶段首先解决“能力路线根本没有被算到底”的问题。除静默猎手外，其余卡池目前仍以保守模型为主，精细程度还不完全相同；后续会继续结合玩家对具体卡牌的理解逐张调整，而不会把当前首版估值当作最终答案。
- 战斗开始时牌区内完全没有已登记能力牌、之后才由其他效果生成能力牌的情况，目前仍主要依赖普通搜索和药水搜索处理，尚未完整享受能力牌专用路线保护。例如由生成牌效果临时获得的能力牌，仍可能需要后续专项适配。

### 玩家反馈对局表现

- 使用玩家提交的“不会开能力牌”反馈对局，从战斗开始重新计算后，七场成功产出完整路线的对局中，预计战损分别由报告中的 **38→9、19→8、80→20、45→14、38→19、13→10、48→0**。其中一场追平玩家的零战损路线；一场虽然将 80 战损降到 20，但使用了四瓶药水，因此不视为真正追平手打。
- 在“群星之子”反馈中，普通路线为 12 战损，能力牌专用路线进一步降到 8；在另一场铁甲战士反馈中，普通路线仍然死亡，激进能力路线找到 20 战损的完整胜利。这两场能够直接确认能力牌专项搜索带来了实际收益。
- 其余改善还同时受当前基础搜索、药水反事实和更高性能预设影响，不能全部归功于能力牌模型。本次更新证明了能力牌路线保护方案可行，但不代表所有能力牌与所有战斗都已经达到人工最优；部分反馈对局仍与玩家路线相差 2～13 战损，另有一场复杂战斗在当前时限内未能产出结果。

## English

### Dedicated Power-card route search

- The solver now preserves dedicated full-combat search routes for Power cards. Some Powers require an upfront energy payment or a small immediate HP loss while delivering most of their value on later turns. Those routes used to be crowded out immediately by lines with better short-term damage or Block. When a Power has a credible future payoff, its setup line now receives a bounded but substantially longer evaluation window and is compared only after the route has been searched through the rest of the combat.
- A route does not win simply because it plays a Power. Every candidate still pays the real energy cost, executes real draw and random outcomes, and remains subject to enemy actions, hand limits, potion use, and HP loss. Only a complete winning route with a better final result can replace the incumbent. Powers with no payoff, late setup, or fatal setup costs are still rejected.
- A unified valuation framework now covers 104 single-player Powers across Ironclad, Silent, Defect, Regent, Necrobinder, and Colorless pools. Each pool keeps its own card-specific decisions instead of forcing every Power through one generic numeric formula. Multiplayer-only Powers remain outside single-player solving.
- Power valuation is no longer limited to printed damage or Block. The solver considers setup cost, expected remaining turns, reachable trigger counts, future card order, draw and discard, hand congestion, follow-up attacks, reachable Shivs, poison maintenance, Star spending, Orbs, Exhaust, summons, and enemy count. Conservative projections decide whether a route deserves continued search; the final choice still comes from the actual simulated combat result.

### Detailed Silent modelling

- **Footwork** is no longer treated as only a flat Dexterity number. The solver checks whether it crosses a Block threshold, saves a card or energy, and lets the saved resource become additional offense. It now strongly prefers early setup unless later turns genuinely cannot benefit.
- **Afterimage** uses the number of cards the route can actually play. It is treated as a strong setup when it can reliably produce roughly a Defend's worth of Block, without assuming unreachable card plays.
- **Accuracy, Fan of Knives, Infinite Blades, and Phantom Blades** use Shivs that can actually be generated, afforded, and played. Their models separately account for bonus damage, area damage, delayed generation, first-Shiv bonuses, Retain, and card-order control.
- **Accelerant, Noxious Fumes, and Envenom** use real poison stacks and future trigger windows. Accelerant values at least one reachable extra poison trigger, Noxious Fumes accounts for long-fight stacking and poison maintenance, and Envenom only counts attacks that actually deal unblocked damage while retaining its lower priority when the two-energy setup is inefficient.
- **Tools of the Trade, Speedster, Master Planner, and Well-Laid Plans** account for future draw, beneficial discards, removing curses or status cards from hand, discard-triggered free plays, retained-card value, and hand-overflow risk. Well-Laid Plans is validated through dedicated route search instead of a single static score.
- **Abrasive** distinguishes a hard three-energy setup from a free discard-triggered play. **Serpent Form** uses the number of cards that can really be played, while **Tracking** values attack damage across all future turns in which enemies remain Weak.
- **Wraith Form** balances immediate survival, the turns covered by Intangible, and the long-term Dexterity loss. It is no longer favored too early in long fights merely for being a powerful card, while still receiving high priority near the end of combat or when it prevents lethal damage.

### Other characters and Colorless Powers

- Ironclad, Defect, Regent, Necrobinder, and Colorless Powers now have first-stage card registration, setup preferences, trigger requirements, and conservative payoff projections. The solver recognizes major families such as defensive scaling, Strength and Exhaust interactions, Orbs and Focus, Star spending, Doom, summons, card flow, and delayed value, and preserves search space for credible setup routes.
- This first stage primarily fixes routes that were never searched deeply enough after playing a Power. Pools other than Silent still rely more heavily on conservative first-pass models and do not yet have equal per-card precision. Future updates will continue incorporating player understanding card by card instead of treating the current draft as final.
- Powers generated later in combat, when no registered Power existed in the starting card zones, still rely mainly on normal and potion search and do not yet receive the full dedicated route protection. Temporary Powers created by generation effects may therefore still need additional adaptation.

### Results from player-reported combats

- Re-solving player reports about skipped Power setup from the beginning of combat produced complete routes in seven cases. Their projected losses changed from **38→9, 19→8, 80→20, 45→14, 38→19, 13→10, and 48→0**. One route matched the player's zero-loss result. Another reduced 80 loss to 20 but spent four potions, so it is not considered a true match for manual play.
- In the Child of the Stars report, the normal route lost 12 HP while the dedicated Power route reduced it to 8. In a separate Ironclad report, the normal route still died while the aggressive Power route found a complete 20-loss win. These two cases directly demonstrate gains from the new Power-specific search.
- Other improvements also include contributions from the current base search, potion counterfactuals, and higher performance settings, so they cannot all be attributed to Power modelling alone. This update establishes that dedicated Power-route protection is viable, not that every Power and encounter is now manually optimal. Several reports remain 2–13 HP behind the player's route, and one complex combat did not finish within the current search limit.
