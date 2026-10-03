# CombatSolver 测试入口

按改动选择最小验证层，方法见 [无人测试](../../HEADLESS_TESTING.md) 与 [社区验收](https://github.com/Torch1230/CombatSolver/blob/main/docs/community/testing-guide.md)。以下记录保留取得证据时的源码和范围，不能视作本轮重新通过。

历史记录见 [归档索引](README.md)。

四进程 ENet P0 链路：房主和三名加入者各自完成普通出牌、原生弃牌选择和结束回合；各检查点的战斗状态、玩家阶段及完整 RNG 四端相等，四端均 Passed、各 18 条检查。证据 `.local/multiplayer-p0/enet-4-b8912c11b53b4f86847df214c6d328f5/peer-0/result.json` 至 `peer-3/result.json`。无头调度器并行上限由二调为四，仍由资源准入限制；Linux 入口只做脚本语法检查，未运行游戏。

双进程 ENet 无求解器对端：房主加载 CombatSolver，加入者战斗期模组列表为 `NoSolverPeerProbe`、QuickRightPlay、RitsuLib，明确没有 CombatSolver。两端各自出牌、原生联网弃牌选择和结束回合，进入第二回合；九个检查点的全状态、玩家阶段与九条完整 RNG 相等，两端均 Passed：`.local/multiplayer-p0/no-solver-peer-ftue-fixed-4e86ba14daa6402b8b9afea185e92098/peer-0/result.json`、`peer-1/result.json`，装载清单见 `peer-1/environment.json`。测试对端使用独立轻量模组驱动原版接口，且仅在无界面环境跳过教学 UI；不是纯原版可见 Steam 房间或真实网络延迟验收。首次驱动在初始化期错误判断自身未加载，第二次过早读取内容库，第三次无界面教学 UI 阻断回合循环；均未计通过，逐因修正后本次通过。

P4 无求解器对端的生产全自动：房主 CombatSolver 实际搜索／部署自己第一回合的本地牌，加入者只有独立测试驱动并原生结束自己的回合；双端到第二回合，两个检查点的全状态、阶段与九条完整 RNG 一致，两端 Passed：`.local/multiplayer-p4/no-solver-peer-full-auto-5fc2f9442dc54c4a976e5552ad907185/peer-0/result.json`、`peer-1/result.json`。加入者装载清单见同目录 `peer-1/environment.json`。队友在部署中交错出牌的证据见下文；Steam 房间和公网延迟未验。

P4 无求解器对端部署中交错：`.local/multiplayer-p4/no-solver-peer-rng-9988911741a54045b4652313e315cc0c/peer-0/result.json`、`peer-1/result.json` 均 `Passed`。房主搜索并部署两张本地 `Strike`；加入者战斗期不加载 CombatSolver，在两张之间原生对房主打 `Largesse`。房主收到生成牌，提示共享 RNG 偏差，从新根重评估且不新增完整搜索，继续第二张本地攻击；加入者保持自己的回合控制权。双端检查点全状态、玩家阶段及九条 RNG 一致，加入者装载清单见 `peer-1/environment.json`。首试夹具要求两张攻击同目标，但合法搜索分别指定两只敌人，失败证据与修正见[规划](../../MULTIPLAYER_PLAN.md)。

P4 `Tutor` 生产求解与执行：房主搜索选中余费 `Tutor`，结果以 `PendingChoice` 截断、没有计划队友选项；房主实际部署并等待，未加载 CombatSolver 的加入者从两张抽牌堆卡中原生选择。选中牌进入加入者手牌并保持其所有权，双端 `enet-tutor-choice` 全状态、玩家阶段、九条 RNG 一致，双方 `Passed`：`.local/multiplayer-p4/no-solver-tutor-827b68462d3d4aac8b63a8423d5865c5/peer-0/result.json`、`peer-1/result.json`。加入者 `peer-1/environment.json` 的装载清单不含 CombatSolver。队友具体选哪张不由搜索器预测。

P4 预测死亡仍执行合法当前动作：虚拟双人先搜索仅持一张 `Strike` 的路线，再把本地玩家生命设为 1；原版结束回合预测为死亡。原序列重评估仍保留 `Strike`，控制器打出该牌；测试在实际结束回合前手动接管，`Passed`：`.local/multiplayer-p4/projected-death/peer-0/result.json`。这只验“预测结果不佳与当前动作合法性分离”，不把实际死亡视为可继续执行。

最终受影响单人哨兵：原版单人建局后一秒短搜得到合法初始路线，`Passed`：`.local/multiplayer-final/single/result.json`。本轮行为变更集中于多人 `Tutor` 与重评估；已有固定前缀单人回归见规划第 0 节。

多人路线混合动作修复（2026-09-29）：首回合代表在本风格主指标相同后比较自身血量、有效伤害和启动收益。虚拟双人一回合固定根：`Defend`／`Strike`／`Uppercut`、2 费的防守路线同时出防御和攻击；`Inflame`／`Strike`／`Uppercut`、2 费的启动路线同时启动和攻击；`Defend`／`Uppercut`、3 费的输出路线同时攻击和防御，三者都用完费用且 `Passed`。证据分别为 `.local/multiplayer-hybrid-fix/defense-attack/peer-0/result.json`、`.local/multiplayer-hybrid-fix/setup-attack/peer-0/result.json`、`.local/multiplayer-hybrid-fix/output-defense/peer-0/result.json`。受影响单人哨兵 `SMOKE-001` Passed；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=239`；原用户截图无完整战斗根，未逐动作复现。

多人 4.1A 并列看板（2026-09-29）：双人输出／启动固定根检查两张对比卡同排、当前回合伤害／血量变化／余血／余费与各自结果一致，后续第二回合默认收起并可展开、收回，点选启动仍切换原路线和条件预测，`Passed`：`.local/multiplayer-dashboard-4-1a/peer-0/result.json`。`UI-LOCALIZATION` 在 `PHROG_PARASITE_ELITE` 的 eng／zhs／zht 三种语言目录及模板占位符检查 Passed，runId `aadef770e89d4c16aeab0baf2d12745e`。Release 构建与 Windows 结构门禁通过。无头截图入口因视口图像为空失败，未计可见观感通过；实际 Steam 窗口排版仍未验。

多人续线、自动计算与静默反馈修正：虚拟双人 `Defend`／`Strike` 两回合搜索的输出和防守都含第二回合动作，`Passed`：`.local/multiplayer-20260929-followup/defense-two-turn/peer-0/result.json`。普通虚拟双人关闭单人自动计算设置后，本地第 1、2 回合各自动搜索一次、不自动出牌；手操更优标记保留内部记录，但界面没有更优路线或上传日志提醒，初始化与搜索错误文案、路线详情也没有上传提示，`Passed`：`.local/multiplayer-20260929-followup/auto-quiet/peer-0/result.json`。并列看板重新检查当前回合滚动区至少保留原 148 像素高度、后续回合展开及方案切换，`Passed`：`.local/multiplayer-dashboard-4-1a/peer-0/result.json`。三语言目录 489 项与占位符 `UI-LOCALIZATION` Passed，runId `a3145d898c624d60ab9d00be6bfcb3b2`；Release 编译和结构门禁通过。可见 Steam 截图的最终观感未验。

## 多人 P2 首批内容差分（2026-09-28）

虚拟四人对敌方新施加 `Artifact`、`Plating`、`Slippery`、`Skittish`、`CurlUp` 的缩放原生差分 Passed，包含 `Plating` 递减值；证据 `.local/multiplayer-p2/power-scaling-4-dec86567a8d04ddfb9cf15ef9ca0dcbe/peer-0/result.json`。虚拟双人 15 张多人专用卡按指定队友、群体、混合机制分三批，基础版和升级版共六次请求均 Passed，即时全状态与完整 RNG 对账；卡牌和证据目录见 [多人内容清单](../../MULTIPLAYER_CONTENT_INVENTORY.md#已通过的卡牌即时差分)。跨回合 Hook、剩余 22 张专用卡、普通多人内容及网络搜索／执行未通过。Windows 结构门禁 238 通过；Bash 门禁仅完成语法检查。

追加 `Outrage`、`GlimpseBeyond`、`Largesse`、`GangUp`、`Knockdown`、`TheBall` 的双人基础版／升级版即时差分，四次请求 Passed；累计 21／37 张。证据及尚未覆盖的触发条件见同一[清单](../../MULTIPLAYER_CONTENT_INVENTORY.md#已通过的卡牌即时差分)。

再追加 12 张持续能力与关联卡的基础版／升级版即时差分，六次请求 Passed，累计 33／37 张。`Hibernate`、`Intercept`、`TagTeam`、`Tank` 的首因修复链及成功证据见[规划第 0.6 节](../../MULTIPLAYER_PLAN.md)和[内容清单](../../MULTIPLAYER_CONTENT_INVENTORY.md#已通过的卡牌即时差分)。这些请求没有触发全部关联监听。

`LegionOfBone`、`Midnight`、`ImitationLearning`、`Tutor` 的双人基础／升级即时差分再六次 Passed，37／37 张专用卡即时效果均有证据。`Tutor` 的即时差分选择来自队友抽牌堆；本轮后续生产求解／执行及无求解器队友原生选择证据见上方 P4 记录。该即时批次未证明后续触发，关联机制的证据见内容清单。

关联机制再按必要输入差分：格挡传播与生成联动、队友先攻击后的 `GangUp`、多种能力施加后的队友攻击、`Hibernate`／`Plot`／`Tank`／`Underworld` 组合的第二回合，四次虚拟双人请求 Passed，路径见[规划 0.6 节](../../MULTIPLAYER_PLAN.md)。未据此推断其他触发组合通过。

`Cacophony` 抽牌计数与归零重置：双人 `Cacophony`→`HuddleUp` 的通常抽牌、计数置 2 后跨阈值随机伤害，以及两者各自第二回合全状态／RNG 差分 Passed；证据见[规划 0.6 节](../../MULTIPLAYER_PLAN.md)。首轮失败是预测计数未同步到能力动态变量，已按原版状态所有者修复。

`ImitationLearning` 指向队友，队友打 `Inflame` 后本地自动复制、双方力量各 2、能力减 1 层及第二回合全状态／RNG 差分 Passed；证据 `.local/multiplayer-p2/imitation-trigger-fixed-0573378d566f4e2f9590ca523b4ce057/peer-0/result.json`。单次普通能力触发不覆盖选择型能力或多次耗尽。

`HammerTime`→`TheSmith`：本地锻造后队友按原版联动锻造，其手牌生成与刀刃伤害、全状态／RNG 差分 Passed；证据 `.local/multiplayer-p2/hammer-forge-fixed-252559287acf463795c462620800ae6c/peer-0/result.json`。

`TheBall` 跨玩家实例转移、队友抽牌后再次打出及逐次增伤，全状态／RNG 差分 Passed；证据 `.local/multiplayer-p2/ball-replay-42e35d8052c146ebba56f30befa826fe/peer-0/result.json`。`LegionOfBone` 群体召唤并推进第二回合的伙伴与所有玩家状态差分 Passed；证据 `.local/multiplayer-p2/legion-round-f7a757a155b6423dbe5440fc287504b0/peer-0/result.json`。

P3 首次搜索探针：虚拟双人、四人普通牌根和含 `BelieveInYou` 的双人内容根各运行 3 秒单成员搜索，非空路线的一回合出牌均属于本地玩家，三次 Passed，证据见[规划 0.6 节](../../MULTIPLAYER_PLAN.md)。`AnyAlly` 起初产生 null 目标导致搜索失败，初版按存活玩家生成目标后通过搜索，但未排除出牌者；后续目标合法性已修正并单独复验。三类方案和生产入口尚未验收。

P3 选人合同：初版四人探针没有断言目标必须是其他玩家，旧证据 `.local/multiplayer-p3/ally-search-legal-4-0a90f5ba2c8b4290acff8fabf087fd3f/peer-0/result.json` 不覆盖此规则。修正后四人 `BelieveInYou` 在三个 Fork 中固定同一其他存活玩家，不推进游戏 RNG，随后短搜与原生出牌差分 Passed；证据 `.local/multiplayer-p3/ally-other-player-4-6c6362c2b3a74cd6b19e6048b2a301d1/peer-0/result.json`。生产重评估保持目标仍待验。

玩家目标药水自用代表：双人 `StrengthPotion` 本地持有者自用后完整状态／RNG 差分 Passed，随后队友目标牌仍可执行；证据 `.local/multiplayer-p2/self-potion-060b3eb9d3de4f8c958b1ab84c19a4d4/peer-0/result.json`。此项不覆盖所有药水或生产自动执行。

多人深度／时间策略：默认 2 回合／3000 毫秒及自定义 3／6000 毫秒快照断言、实际搜索层上限，虚拟双人请求 Passed，证据 `.local/multiplayer-p3/horizon-policy-2-b5e9a8d5762c444d93de124b59b26158/peer-0/result.json`。设置 UI 和多成员共享预算仍待验。

多人方案候选：同一次搜索的末端候选按输出／防守／启动取不同目标，当前回合动作相同或该风格无主指标增益时合并。普通双人根仅输出一条 Passed；一回合限制的打击／防御、打击／`Inflame` 两根分别拿到输出＋防守、输出＋启动，主指标与动作取舍断言 Passed；单人短搜 Passed。两回合首次缺首回合启动候选，现保留首回合风格代表并完整回放；打击／`Inflame` 和打击／防御两根分别取得有差异的输出＋启动、输出＋防守，均 Passed。路径见[规划 0.6 节](../../MULTIPLAYER_PLAN.md)。纯支援及生产多方案还未完成。

多人生产搜索协调器：双人 `Strike`／`Inflame` 两回合根通过 `CombatSearchCoordinator.Solve` 获得输出＋启动，单场搜索统计与根比较戳对账 Passed；证据 `.local/multiplayer-p3/coordinator-styles-two-turn-a4297d0afd3d43fba976a1ba76e9772a/peer-0/result.json`。控制器、界面与真实玩家交互仍未接入。

纯支援第一批：`BeaconOfHope` 有余能时在 `Strike` 后补入并按原生执行整条路线；余能不足时不补；独立固定目标的 `Blaze` 补给队友并按原生执行整条路线；`Rally` 群体自身收益走正常搜索。此前 `Blaze` 自指测试违反原版 `AnyAlly` 选人规则，结果作废；修正后 `Blaze` 给队友的原生路线复验 Passed：`.local/multiplayer-p3/support-blaze-other-player-cf876f7b22a84b29b53729e0d1e9e1bb/peer-0/result.json`，其余对应虚拟双人请求 Passed，路径见[规划 0.6 节](../../MULTIPLAYER_PLAN.md)。两张补入在固定机制中已通过，不能推出所有纯支援或额外成本已通过。

P3 `Mimic` 自身收益搜索：双人 1 能量、队友 10 格挡时，生产搜索把指向队友的 `Mimic` 作为正常本地动作纳入唯一输出方案，没有记为余费纯支援，Passed：`.local/multiplayer-p3/mimic-self-benefit-search-31ce8825da574c9e928c660a07f77871/peer-0/result.json`。首试错误要求另有防守方案而 Failed；诊断显示同动作方案被正确合并，路径见[内容清单](../../MULTIPLAYER_CONTENT_INVENTORY.md)。该请求只验搜索分类和目标，原生即时结算证据另见内容清单。

两张余费支援组合：`Strike` 后依次补 `BeaconOfHope` 和给队友的 `Blaze`，整条原生执行路线／所有玩家状态／RNG Passed；证据 `.local/multiplayer-p3/support-two-cards-ca2a44b61ca94328a437837241a5b660/peer-0/result.json`。

目标与受益归属复核：原版 `NTargetManager` 排除 `AnyAlly` 自指，15 张此类型牌的搜索候选统一排除出牌者；8 张 `AllAllies` 牌包含出牌者。`Largesse` 的目标玩家拥有新牌，`base.Owner` 是创建者。双人 `Largesse` 短搜、原生出牌、队友手牌／牌主显式断言及全状态／RNG 差分 Passed：`.local/multiplayer-p3/largesse-recipient-and-search-c6a5eb6ba3e64c159a0623d0a234e63c/peer-0/result.json`。该证据不覆盖余费补入。

`Largesse` 余费支援：双人 `Strike` 后补给队友，整条原生路线／所有玩家状态／RNG Passed；证据 `.local/multiplayer-p3/largesse-spare-support-7a83bcf691ce4be98528ac869b670ff4/peer-0/result.json`。

P4 控制器手动入口：首次虚拟双人请求因战报结果记录的 `Players.Single()` 失败；修为本地玩家后，同一脚本取得可见悬浮窗与本地搜索结果并完成原生一回合，Passed：`.local/multiplayer-p4/controller-virtual-2-outcome-fix-119a18d17ad54ec38dcac49e76a3f1a6/peer-0/result.json`。该探针未覆盖逐步部署与联机变化。

P4 本地部署：虚拟双人普通根在手动搜索后实际调用 `RequestDeploy`，本地牌出手、只结束本地回合、队友未被代操作，Passed：`.local/multiplayer-p4/controller-deploy-virtual-2-e6cc81878d00486cad9ac8e0ebffa4fe/peer-0/result.json`。目标牌与联机交错仍未验。

多人本地药水账本：原生虚拟双人先由队友自用 `StrengthPotion`，本地账本计零；再由本地自用，同一账本只计本地一瓶，原生内容牌及完整状态／RNG 差分 Passed：`.local/multiplayer-p4/local-potion-accounting-2c8ee81616f944cea0e52decb33179ea/peer-0/result.json`。该探针没有让求解器替队友用药。

P4 指定队友部署：生产控制器对虚拟双人 `Strike`＋`Blaze` 根搜索并执行，力量只施给队友且只结束本地回合，Passed：`.local/multiplayer-p4/controller-blaze-deploy-99cc944e1a944ed5ba2ac2d6e950c229/peer-0/result.json`。这是本地虚拟多人证据，未覆盖 ENet。

模拟能量变化后的目标候选：live 能量为零时 `Blaze` 不可打；Fork 内补足能量后模拟器可打且目标仍为另一名玩家，三个 Fork 固定、游戏 RNG 不变，Passed：`.local/multiplayer-p4/ally-after-energy-750666e7869748c097f02a30b8f0b9e3/peer-0/result.json`。

P4 ENet 双端控制器部署：房主与加入者分别本地搜索并原生执行自己的牌，两端同步进入第二回合且全状态／RNG 一致，Passed：`.local/multiplayer-p4/enet-controller-2-ready-wait-2daef04b383044949b8ec3cc106b50b7/peer-0/result.json`、`peer-1/result.json`。首次失败是测试在原生准备结束状态传播前断言，修正等待条件后通过；四人和交错变化未验。

P5 方案按钮：虚拟双人 `Inflame`／`Strike` 根的输出／启动两条实际动作路线同时显示，点击启动按钮后当前结果切到启动，Passed：`.local/multiplayer-p4/overlay-style-selection-332cc73ae73f4bc8af5225dd5b725613/peer-0/result.json`。多人深度／时间输入已接入性能页；该轮仅验证构建及英文 JSON 解析，输入保存与可见窗口排版未验。

P5 多人设置页：`UI-LOCALIZATION` 在原生 `PHROG_PARASITE_ELITE` 中对 eng／zhs／zht 实际构造设置面板、检查多人深度与时间输入显示值，并对账 468 条目录占位符，Passed：`.local/multiplayer-p5/ui-localization-phrog-3569a408218a4b3caeeba6478db72f82/result.json`。默认遭遇的首次尝试在既有怪物生成夹具处失败，未计为设置验证；可见排版与输入保存尚未测。

P5 防守按钮与假设文案：双人 `Defend`／`Strike` 根显示输出／防守两条不同动作路线，点击防守后当前方案切换，摘要显示队友不再主动出牌的条件预测，Passed：`.local/multiplayer-p5/overlay-defense-selection-e074cd147626491dab30e2c4f83a93bc/peer-0/result.json`。与输出／启动探针合并覆盖三种风格按钮；可见窗口排版未验。

P5 所选方案原生部署：界面只接收不可变选项快照，结构边界门禁通过。双人输出／启动固定根点击启动后原生执行 `Inflame`，未执行输出方案的 `Strike`，Passed：`.local/multiplayer-p5/overlay-setup-deploy-24cde210264443d3bb8d5ffadf2b02bd/peer-0/result.json`。防守方案部署与可见窗口观感未验。

P4 部署前队友伤害：虚拟双人本地求解后队友原生打出 `Strike`，原逻辑会重新完整搜索（失败证据 `.local/multiplayer-p4/teammate-drift-baseline-a9b160ce4b634e63ab86d16f36ecfc6d/peer-0/result.json`）；当前逻辑在新根重放原路线并原生部署，无额外完整搜索且本地回合完成，Passed：`.local/multiplayer-p4/teammate-drift-replay-f3b8a015470948b288908279a983f65c/peer-0/result.json`。仅覆盖部署前仍合法的队友伤害。

P4 队友变化后的执行按钮：相同机制的原生虚拟双人探针刷新悬浮窗、断言执行按钮可点击、模拟实际点击、原生部署并在部署前后断言无新增完整搜索，Passed：`.local/multiplayer-p4/teammate-drift-button-3e1fff5d517d4bdcac1ddb11ec56612c/peer-0/result.json`。

P4 队友击杀原计划目标：双敌 `CULTISTS_NORMAL` 中，队友原生击杀本地已计划攻击的敌人，另一敌仍存活。原回放因目标已消失而抛出 `CardPlay has no target creature`，失败证据 `.local/multiplayer-p4/teammate-kill-direct-bae6ed6cc99449829187eddbe9da8f3f/peer-0/result.json`；加入固定前缀的目标身份合法性检查后，控制器直接请求时显示路线失效并在本地动作前暂停，无完整新搜索，Passed：`.local/multiplayer-p4/teammate-kill-guard-c93271858d4f4520a0407264a71d7666/peer-0/result.json`。该请求未验证按钮点击的失效分支。

受影响单人固定前缀哨兵：原生 `FIXED-PREFIX-TURN-OUTCOMES` 三回合续用与独立 oracle，Passed：`.local/multiplayer-p4/fixed-prefix-single-b9bd85d57578452bae95938e6caf0ce3/result.json`；Release 0 警告／0 错误，Windows 结构门禁 239 通过。

P4 部署中队友交错：双敌 `CULTISTS_NORMAL`，本地第一张 `Strike` 后队友在原生动作间隙击杀第二张原目标，控制器只打出第一张，未完整重搜并暂停，Passed：`.local/multiplayer-p4/mid-deploy-kill-17962d61dd5d47eab915f6657275e2bd/peer-0/result.json`。队友改攻击另一只敌人时，两张本地 `Strike` 仍按原目标打出并结束本地回合，未完整重搜，Passed：`.local/multiplayer-p4/mid-deploy-legal-e58f2483bb964daf87188a8f014b1888/peer-0/result.json`。未覆盖最后一张牌到本地回合结束之间的变化。

P4 结束回合前余下 `EndTurn` 回放接入后，双敌合法交错样本仍按原路线完成并结束本地回合，Passed：`.local/multiplayer-p4/mid-deploy-endturn-replay-5c8330980cb8443385df9ca5e0a31570/peer-0/result.json`。该输入的队友动作发生在两张本地牌之间，不覆盖最后一张牌之后的队友动作。

P5 队友变化过期提示：原生虚拟双人队友打出 `Strike` 后刷新悬浮窗，断言标题为数值待更新、执行按钮可用；点击后原路线重评估并本地部署完成，Passed：`.local/multiplayer-p5/stale-hint-f44cce3493a04f49b1908ed78dee8905/peer-0/result.json`。无头控件状态不等于可见窗口排版验收；RNG 偏差提示未覆盖。

P2 普通卡多人生成池首批：虚拟双人 `InfernalBlade`、`JackOfAllTrades`、`Metamorphosis` 基础／升级六次即时原生／模拟全状态与完整 RNG 差分 Passed：`.local/multiplayer-p2/ordinary-generation-base-0c7da3f98c164553b69268974a58fc30/peer-0/result.json`、`.local/multiplayer-p2/ordinary-generation-upgrade-caed2c83160540dbbe9d99fc15e0d7b6/peer-0/result.json`。只覆盖实际抽中的生成物与即时牌堆状态。

P2 普通卡多人生成池第二批：虚拟双人 `BundleOfJoy`、`Distraction`、`WhiteNoise` 基础／升级六次即时原生／模拟全状态与完整 RNG 差分 Passed：`.local/multiplayer-p2/ordinary-generation-hand-base-f0686a01182f4a6c94632392cab215a7/peer-0/result.json`、`.local/multiplayer-p2/ordinary-generation-hand-upgrade-8b164232336d4b52ba80637493c81fe0/peer-0/result.json`。生成池只覆盖本输入实际抽中对象。`Fasten` 原版约束调用仅在悬停说明，不计为战斗生成分支。

P2 普通卡多人生成池第三批：虚拟双人 `Jackpot`、`ManifestAuthority` 基础／升级四次即时原生／模拟全状态与完整 RNG 差分 Passed：`.local/multiplayer-p2/ordinary-generation-attack-base-e550f1c902f648c89132e47b785f5618/peer-0/result.json`、`.local/multiplayer-p2/ordinary-generation-attack-upgrade-c8716d189491435b8d77890fe79188a9/peer-0/result.json`。仅覆盖本输入实际生成的牌。

P2 普通生成池选择批：`Discovery` 基础版 `.local/multiplayer-p2/ordinary-choice-discovery-base-2cc263cdf62146e68a62ce728dc0b893/peer-0/result.json`，`Abundance`／`Quasar`／`Splash` 基础版 `.local/multiplayer-p2/ordinary-choice-rest-base-7aca4e54e83d4f9a904b430d6bde89c3/peer-0/result.json`，四张升级版 `.local/multiplayer-p2/ordinary-choice-upgrade-4bae4dbe2a0245c9b50d836a7468ace9/peer-0/result.json` 均 Passed；同根生成候选选择第一个并在原版界面执行，逐张即时全状态／完整 RNG 对账。跳过与其余候选未验。

P2 `Stoke` 三张手牌消耗／补牌基础及升级即时全状态／RNG Passed：`.local/multiplayer-p2/ordinary-stoke-base-4fca535f6f224b06a11baa7bcf8d4b23/peer-0/result.json`、`.local/multiplayer-p2/ordinary-stoke-upgrade-687fefeb63a14764a0e8637f9e8ae571/peer-0/result.json`。`MadScience` 的 Skill／Chaos 可达组合基础及升级即时全状态／RNG Passed：`.local/multiplayer-p2/ordinary-mad-science-base-34c7be43f6a7487fb15be48380e94904/peer-0/result.json`、`.local/multiplayer-p2/ordinary-mad-science-upgrade-357fe34b0b5a45f9b0a0c70b76231391/peer-0/result.json`。其他 Rider 与后续使用生成牌未验。

P2 普通共享目标：双敌 `CULTISTS_NORMAL` 上 `Omnislice`、`BeatDown`（弃牌两张攻击自动打出）、`BouncingFlask` 基础／升级六次即时原生／模拟全状态和完整 RNG 差分 Passed：`.local/multiplayer-p2/ordinary-shared-target-base-9221f0b073ec4ee5b3e654ae761ec833/peer-0/result.json`、`.local/multiplayer-p2/ordinary-shared-target-upgrade-5602bef999cd4eeba20e79f044afe779/peer-0/result.json`。敌人中途死亡与其他自动牌类型未验。

P2 `Largesse` 四人目标归属：本地 0 号给 3 号队友出牌，显式断言新牌进入目标队友手牌且牌主为该队友，完整状态／RNG Passed：`.local/multiplayer-p2/largesse-four-target-seat3-eabd09421fe94b42985aeda221619b15/peer-0/result.json`。这次是基础版单次出牌。

P4 自动计算入口：无人宿主默认关闭自动触发，首次请求没有搜索结果并超时，未计通过；测试显式启用该入口后，虚拟双人得到本地方案、未部署，Passed：`.local/multiplayer-p4/automatic-calculation-enabled-5b98feffdcb84bd0a7d3581ee8284788/peer-0/result.json`。全自动入口在虚拟双人自动搜索并原生执行本地牌、只结束本地回合，队友未被操作，Passed：`.local/multiplayer-p4/full-auto-virtual2-6de921827935483c881b174c2d67af09/peer-0/result.json`。与既有手动入口合起来覆盖三入口虚拟双人代表；ENet 全自动证据见下文，取消未覆盖。

P4 ENet 全自动双端：每端本地搜索、原生部署自己牌、同步第二回合、两端完整状态／RNG 一致，Passed：`.local/multiplayer-p0/enet-2-09e37bb5ce8643039446ee6953ff7359/peer-0/result.json` 与 `peer-1/result.json`。最初失败来自测试把另一端自行结束回合当作本端越权；已改为每端核本地实际部署牌。

P4 搜索期间队友变化：虚拟双人开始手动搜索后，队友原生 `Strike` 改变根；新根重评估原本地路线并部署、没有第二次完整搜索，Passed：`.local/multiplayer-p4/search-time-drift-virtual2-344e51f9f444480483391e546f5a900f/peer-0/result.json`。四人 ENet 全自动两次初试在建局根后 120 秒内无结果，第一次为默认 DOP、第二次每端 DOP 1，均未通过；搜索完成后的同回合变化处理加入后，每端 DOP 1 的四人 ENet 搜索与部署、第二回合全状态／RNG 对账四端 Passed：`.local/multiplayer-p0/enet-4-f316f44de37240058975bc49cd2c9d2e/peer-0/result.json` 至 `peer-3/result.json`。无法单凭该前后对比确定初试唯一根因；默认 DOP 四人全自动与 Linux 游戏运行仍未验证。

P2 四人玩家目标药水自用候选：本地 `StrengthPotion` 搜索只产生一个无队友目标候选，原生自用即时全状态／RNG 对齐；之后 `Largesse` 给 3 号队友生成牌且牌主属于该队友，Passed：`.local/multiplayer-p2/four-self-potion-largesse-ed2714885b2248118fb8d5ef70ec32a3/peer-0/result.json`。未验证生产执行器实际投药。

P2 玩家目标药水自用机制：四人 `BlockPotion` `.local/multiplayer-p2/self-block-potion-four-cbc90f92c6f64218a5bd0cda95b38ab1/peer-0/result.json` 和双人 `EnergyPotion` `.local/multiplayer-p2/self-energy-potion-two-a56b08d453e743228b3ce67070940b77/peer-0/result.json`，每次候选仅本地自用一项，原生动作／模拟即时全状态与完整 RNG 差分 Passed。选牌、治疗、球的后续代表见下文；其他药水机制仍未封闭。

P4 生产控制器本地药水执行：虚拟双人必用 `BlockPotion`，方案只为本地持有人用药，原生部署后仅本地玩家得格挡、队友不变，Passed：`.local/multiplayer-p4/controller-self-block-potion-0b2e57c7926d4cd2aad110ee092f4221/peer-0/result.json`。其他药水与 ENet 执行未验。

P4 ENet 双端下一回合全自动：旧多人门禁移除后，首探针仍因无人宿主默认关自动触发而在第二回合等待到 120 秒，未通过；测试显式启用后，双端在第二回合自动从新根搜索并原生部署本地牌，第三回合完整状态／RNG 双端一致，Passed：`.local/multiplayer-p0/enet-2-f52d9e1e9246452992994101120ea52e/peer-0/result.json`、`peer-1/result.json`。四人自动延续、Steam 房间与可见 UI 未验。

P5 多人设置编辑／保存：原生无头设置页提交深度 `3`、时间 `4.5` 秒，再从设置文件重载并确认值保持，最后恢复原设置；同次三语言 UI 与 475 条英文目录占位符 Passed：`.local/multiplayer-p5/ui-settings-edit-509374eb84d540ea80d689f32873b416/result.json`。可见窗口布局未验。

P4 随机流观测：双人虚拟 `CULTISTS_NORMAL`，队友在两张本地 `Strike` 之间打 `Largesse` 给本地玩家，原生生成牌归目标、共享 RNG 前进；生产控制器提示偏差、从新根重评估并继续第二张本地攻击，无完整重搜，Passed：`.local/multiplayer-p4/mid-deploy-rng-fixed-d1f774413f044e81a46697a326d4a5b0/peer-0/result.json`。首次失败根因为读取 RNG 时队友动作仍在队列中。反向本地 `Largesse` 给队友时，正常自身 RNG 消耗不误报，队友获得生成牌所有权，Passed：`.local/multiplayer-p4/own-largesse-rng-final-1127aa6c05374ca293827af5f3f55a4f/peer-0/result.json`；回合结束后不以仍在手牌作为断言。ENet 交错未验。

P2 `GremlinMerc` 的定向偷窃：双人根与双方普通牌即时全状态／RNG 差分 Passed：`.local/multiplayer-p2/gremlin-merc-root-d62f670148804ee8897239a3052bdab5/peer-0/result.json`；跨回合首试漏扣第二名玩家 20 金币，修复为按每条 `ThieveryPower` 实例结算后双人 `.local/multiplayer-p2/gremlin-merc-round-fixed-7897a817c6344866a0d997f34eb41ad6/peer-0/result.json`、四人 `.local/multiplayer-p2/gremlin-merc-round-four-ad7a27e68a594586be4b18c861b226e8/peer-0/result.json` 均 Passed。相应定向实例合同 `.local/multiplayer-p2/instanced-thievery-5cbf464691ab46acb566d6eb7644113f/result.json` Passed。只验首回合 `GIMME_MOVE`，后续招式与死亡返还未验。

P2 `OvicopterNormal` 双敌召唤：虚拟双人到第二回合的完整状态／RNG 差分 Passed：`.local/multiplayer-p2/ovicopter-egg-round-6fd0947191ef4bba8ec663af8f1c4009/peer-0/result.json`。三只 `ToughEgg` 已按多人 HP 生成并携带 `HatchPower`；没有推进到孵化动作，孵化 HP 待验。

P4 ENet 双端交错随机流：加入者在房主两张本地 `Strike` 之间用 `Largesse` 给房主生成牌；房主显示随机偏差、重评估后继续攻击且无完整重搜，双方稳定检查点原生全状态／九条 RNG 一致，Passed：`.local/multiplayer-p0/enet-2-75c6c88c214a4a7caa289c21b4d183fc/peer-0/result.json` 与 `peer-1/result.json`。最初两端超时来自测试等待加入者原始联机动作对象的 CompletionTask，修为等待原生可观察状态后通过。四人、Steam 房间与真实延迟未验。

P2 `ToughEgg` 孵化：在 `OVICOPTER_NORMAL` 虚拟双人根推进第二个敌方回合，显式断言三只卵均已孵化、`HatchPower` 移除；完整全玩家／敌人状态及九条 RNG 与预测一致，Passed：`.local/multiplayer-p2/ovicopter-hatch-assert-f2e4b9fea5b04323b5da3cba71aff9d7/peer-0/result.json`。覆盖该种子对应的三次随机 HP 与多人缩放，不代表其他卵死亡路径通过。

P4 取消与接管代表：双人虚拟双敌根第一张本地攻击后关闭求解器，第二张没有打出，队友未被操作，Passed：`.local/multiplayer-p4/manual-takeover-fixed-0383b266d34b47cb9071f3fdb3b4fee0/peer-0/result.json`。双人虚拟根启动搜索后用户立即停止，没有部署也没有结束任一玩家回合，Passed：`.local/multiplayer-p4/search-user-stop-9252b7db3f0c4c7999424fb259ef519c/peer-0/result.json`。战斗退出与旧任务回调仍待验。

P4 生命周期 Reset 代表：双人虚拟搜索期间主动重置战斗会话，搜索和部署均停下、悬浮窗隐藏、两名玩家都未被代结束，Passed：`.local/multiplayer-p4/lifecycle-reset-f4a212c020ac4bed9ca90c7e9f8a2e7b/peer-0/result.json`。实际房间退出与旧结果回调时序仍未覆盖。

P4 旧任务覆盖：同一虚拟双人战斗中启动搜索、立刻重置会话、再启动新搜索；新结果显示且可执行，旧任务未覆盖，Passed：`.local/multiplayer-p4/stale-search-callback-5ea7bbb41f244b5a8a790f7282aa9261/peer-0/result.json`。真实切房和对端断线未验。

P2 `GremlinMerc` 第二招：双人虚拟第三回合差分首试发现 `DOUBLE_SMASH_MOVE` 没给队友施加原版虚弱；模拟改为给所有存活玩家施加后完整状态／RNG Passed：`.local/multiplayer-p2/gremlin-merc-second-fixed-31e35320566f4014b7c3f198bcb80012/peer-0/result.json`。第三招及死亡返还未验。

P2 `GremlinMerc` 第三招：双人虚拟到第四回合，原版 `HEHE_MOVE` 的力量 2、双方各失 60 金币及能力实例状态与预测完整状态／RNG 对齐，Passed：`.local/multiplayer-p2/gremlin-merc-third-move-54881f1b85174c2b9c8c329b66fb19af/peer-0/result.json`。死亡返还和途中玩家死亡未验。

P2 `GremlinMerc` 死亡能力转移：双人首轮各被偷 20 金币后，本地第二回合击杀怪物，原版生成胖／鬼祟地精；胖地精两条 `HeistPower` 各指向原被偷玩家且金额 20，原生／预测全状态与九条 RNG 一致，Passed：`.local/multiplayer-p2/gremlin-merc-death-fixed-stamp-8bc637ffb298497ba5d8ed6e9da0c927/peer-0/result.json`。最初仅因夹具续用戳仍用首回合编号而失败；返还金币的后续死亡未验。

P2 胖地精死亡追回：第二张本地 `Strike` 后，房间原版奖励分别为两名被偷玩家各 20 金币，战斗预测的全状态与九条 RNG 仍对齐，Passed：`.local/multiplayer-p2/heist-recovery-c59c5d5a889143068996ab4b51bf28ca/peer-0/result.json`。原版房间奖励只做目标／金额断言，战后 UI 未验。

P2 `WaterfallGiant` 群体虚弱与人数治疗：双人固定种子四个敌方回合，首次 `STOMP` 差分发现模拟漏给队友虚弱；修复后到第四回合完整状态／RNG Passed：`.local/multiplayer-p2/waterfall-giant-rounds-fixed-5354778cf43b427eb2324cb7fa11d66b/peer-0/result.json`。继续降低敌人 40 HP 后明确断言 `SIPHON` 按两人份治疗，到第五回合全状态／RNG Passed：`.local/multiplayer-p2/waterfall-siphon-explicit-e1c7efeb896f4076a1b2418f991a5a69/peer-0/result.json`。其他阶段未验。

P5 防守按钮执行：一费 `Defend`／`Strike` 根，点击防守后生产控制器原生执行防守路线而未打出输出牌，Passed：`.local/multiplayer-p5/defense-style-deploy-fixed-input-c5904e9a0dee42f2a97a165fd2d17cba/peer-0/result.json`。最初沿用默认 10 能量时两张都可打，缺少真实取舍，夹具未通过；改用既有一费输入后通过。可见窗口排版未验。

P2 敌人药水目标：四人双敌根 `FirePotion` 的搜索候选恰覆盖两名存活敌人且不含玩家，Passed：`.local/multiplayer-p2/enemy-potion-targets-0f2fa7cb78094b5ca2b982b876d05897/peer-0/result.json`。原生投药的后续证据见下文。

P5 旧结果随机流提示：虚拟双人队友在求解后用 `Largesse` 推进共享 RNG，悬浮窗明确提示旧预测可能不准、执行按钮保留；重评估原路线后继续部署，无完整重搜，Passed：`.local/multiplayer-p5/predeploy-rng-hint-fixed-884797ea14704d88b21fd1729f3501d5/peer-0/result.json`。求解进行时队友同样推进 RNG，结果发布后的偏差／重评估提示及本地原生部署 Passed：`.local/multiplayer-p5/search-time-rng-hint-9d0c942ebe614adba99abc40bcd3a7e5/peer-0/result.json`。均为无头 UI 状态，未看可见窗口。

P4 四端 ENet：默认 DOP 首回合全自动执行并同步第二回合，四端状态与完整 RNG 对账 Passed：`.local/multiplayer-p0/enet-4-8574f581756247b9afe12b3794bbcfce/peer-0/result.json` 至 `peer-3/result.json`。DOP 1 的两回合全自动执行后同步第三回合，四端状态／RNG Passed：`.local/multiplayer-p0/enet-4-eee65da42e15466291de1a0d3ecfe8b7/peer-0/result.json` 至 `peer-3/result.json`。Steam 房间、异机网络和未装 Mod 对端未验。

P2 蜈蚣分段首轮：双人 `DECIMILLIPEDE_ELITE` 第一次第二回合差分发现 `CONSTRICT_MOVE` 漏给队友虚弱；改为全体存活玩家后完整状态／RNG Passed：`.local/multiplayer-p2/decimillipede-round-fixed-b5a8732d07ee4bb7a06e59d920a237ad/peer-0/result.json`。死亡／复活未验。

P2 四人敌方能力缩放全 12 项：各自原生施加、预测同根 Fork、完整状态／RNG 差分 Passed：`.local/multiplayer-p2/all-enemy-power-scaling-3eb5d0a936b342a08bd0970baa5e09fc/peer-0/result.json`。本轮追加 `Plow`、`Reattach`、`Flutter`、`Regen`、`Rampart`、`Shriek`、`HardenedShell`；仅即时应用，不覆盖后续监听。

P2 敌方 `PlatingPower` 生命周期：双人 `SlumberingBeetleNormal` 根及两个敌方回合的甲虫镀层／格挡为 45／45 → 45／45 → 43／43，完整状态和九条 RNG 差分 Passed：`.local/multiplayer-p2/slumbering-beetle-plating-bc43bf334ea24b7ca47fd14bb490fef2/peer-0/result.json`。未触发苏醒移除。

P2 甲虫自然苏醒：同类双人根继续第三敌方回合，`SlumberPower` 耗尽、镀层移除，第四回合甲虫无 Power、格挡 41；完整状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/slumbering-beetle-awake-1df17160e621452bbd78ca02240370e5/peer-0/result.json`。受伤提前唤醒未测。

P2 甲虫受伤提前苏醒：把睡眠层数置 1 并清格挡，本地 `Strike` 打出 `STUNNED`，下一敌方回合执行苏醒并移除镀层；两处完整状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/beetle-damage-stun-round-cfe4ccec565b479fa2828a6bc2bfed06/peer-0/result.json`。睡眠层数由测试注入。

P2 玩家侧 Doom 群体死亡 Hook：双人直接触发队友 Doom，持 `BookRepairKnife` 的本地玩家由 70 治疗到 73，预测与原版的队友死亡、治疗和九条 RNG 一致，证据 `.local/multiplayer-p2/player-doom-repair-knife-scope-5cf6ac1cd0a94c9789d5c97b79fdacb1/peer-0/result.json`。中途 Hook 的严格全状态因虚拟原版自动切换阶段／移牌而失败，证据 `.local/multiplayer-p2/player-doom-repair-knife-3a883173aff94661bea03a7db5779764/peer-0/result.json`。完整回合改用双进程 ENet，队友 Doom 死亡、房主回血并承受敌方攻击后 HP 69，到第二回合房主全状态／九条 RNG 严格差分及两端原生检查点 Passed：`.local/multiplayer-p2/enet-player-doom-final-d8ff8a78501e43459922097f05076545/peer-0/result.json`、`peer-1/result.json`。

P2 蜈蚣分段复活：双人第二回合击杀一段，原生 `DEAD_MOVE` 保持死亡，随后 `REATTACH_MOVE` 才复活；击杀动作与两次敌方回合的全状态／RNG 均与预测一致，Passed：`.local/multiplayer-p2/segment-reattach-full-14c99fc3007b47a08ab9846392e3a262/peer-0/result.json`。最初 6 HP 夹具未考虑玩家虚弱、下一次夹具误以为死亡回合即复活，均为测试前提错误；全段死亡未验。

P2 `TheObscuraNormal` 双人前两回合：原版 `Parafright` 伙伴及全部玩家／敌人状态和九条 RNG 到第三回合与预测一致，Passed：`.local/multiplayer-p2/obscura-second-round-35ee5bad5bbb4718aed927b9cd2325a1/peer-0/result.json`。伙伴死亡与后续幻象分支未验。

P2 `QueenBoss` 双人前三招：首试 `PUPPET_STRINGS_MOVE` 漏给队友束缚；同步修复 `YOU_ARE_MINE_MOVE` 的群体异常状态后，到第四回合两名玩家的束缚、虚弱、易伤、脆弱及牌上 `Bound`、怪物伙伴和完整 RNG 均对账 Passed：`.local/multiplayer-p2/queen-third-round-fddc55be530542df8dbbb00efbb3d3ac/peer-0/result.json`。`TorchHeadAmalgam` 在预排 `BURN_BRIGHT_FOR_ME_MOVE` 后死亡，女王换为 `ENRAGE_MOVE`；双人原生击杀及下一敌方回合的完整状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/queen-amalgam-death-fc1b28efee2b4d618d2e4c784e441c17/peer-0/result.json`。女王自身死亡及其他后续招式未验。

P2 怪物多人目标批：双尾鼠前两回合群体脆弱 `.local/multiplayer-p2/two-tailed-rats-round-fixed-9193a1f28b1c4cb6bb0ea3d40685386c/peer-0/result.json`、祭司前两回合群体脆弱／虚弱 `.local/multiplayer-p2/the-kin-round-6ed76c8562964caf8d05f9e560ae4660/peer-0/result.json`、幽灵船首轮群体虚弱加晕眩牌 `.local/multiplayer-p2/haunted-ship-round-0653a8921b9249158aeee7769dd155da/peer-0/result.json`、永世沙漏前三招含逐玩家枯萎升级 `.local/multiplayer-p2/aeonglass-third-93674076c0b3426ca8d9210d192407fa/peer-0/result.json`、噬魂鱼前三招含逐玩家塞牌 `.local/multiplayer-p2/soul-fysh-third-9b2f8e71a58846eeba17ced810298d01/peer-0/result.json`、贪食者首招逐玩家沙坑及随机塞牌 `.local/multiplayer-p2/insatiable-liquify-eb2b7791f691445b8da3d897e97d3492/peer-0/result.json` 均为双人全状态／九条 RNG 差分 Passed。飞贼首招显式断言双方各被偷一张自己的牌并各有一条 `SwipePower`，同层差分 Passed：`.local/multiplayer-p2/thieving-hopper-explicit-29c1d391025548ccacad6a46fca9b27c/peer-0/result.json`。其余按原版 `targets` 源码修正的招式只完成目标范围审核，未声称原生差分通过。

P2 `KnightsElite` 双人前两招含 `MagiKnight.DAMPEN_MOVE`：全状态／RNG 到第三回合 Passed：`.local/multiplayer-p2/knights-dampen-616a0b39006647fb88adf270df9352b5/peer-0/result.json`。本输入没有预先升级牌，尚未验降级与恢复。知识恶魔多人诅咒原版要求每名玩家独立选择；当前预测已改为明确报错，未取得原生差分通过证据。

P2 `FabricatorNormal` 双人到第三回合，原生随机召唤、召唤物行动及完整状态／RNG 差分 Passed：`.local/multiplayer-p2/fabricator-second-ac4152fa82d140e8abc8b816308118bc/peer-0/result.json`。只覆盖该种子实际选择的分支。`TurretOperatorWeak` 双人原生 `RampartPower` 首次揭示模拟重复触发，炮手预测格挡 110、原版 55；修正为玩家方一次后，击杀炮手、保留已排盾击、下一轮转为狂暴并获 3 力量的完整状态／RNG Passed：`.local/multiplayer-p2/living-shield-following-smash-153491e8a62c448a8c81702af9a3f369/peer-0/result.json`。中间两次夹具错误分别漏清炮手格挡、误判预排招式时点，均未列为通过。

P2 `TestSubjectBoss` 双人第一阶段两招到第三回合全状态／RNG Passed：`.local/multiplayer-p2/test-subject-second-567130576bb64c859c8d9860b1853170/peer-0/result.json`。连续两次本地击杀、第二及第三形态 HP 缩放／能力变化、最后击杀结束战斗，每个稳定边界全状态与九条 RNG 差分 Passed：`.local/multiplayer-p2/test-subject-final-death-fixed-d16c44bc416442c0b6a305f240630a93/peer-0/result.json`。初版最后一击未考虑第三形态的无实体减伤，夹具 6 HP 未击杀；改为 1 HP 后通过。

P2 敌方目标药水原生结算：四人双敌 `FirePotion` 候选仅两名存活敌人，实际向第一名敌人投药后全部状态和九条 RNG 与模拟一致，Passed：`.local/multiplayer-p2/enemy-potion-native-11c3d4df66734ff4b8bc726ef1960c6e/peer-0/result.json`。本次未覆盖其他敌方目标药水。

P2 玩家目标药水自用机制再补四类：治疗 `BloodPotion` `.local/multiplayer-p2/self-blood-potion-7d6f84aee81f41bca80978c306850a21/peer-0/result.json`、能力 `FocusPotion` `.local/multiplayer-p2/self-focus-potion-dab6c067e16048528012a8b01a5fb3fb/peer-0/result.json`、原生选牌并归持有者的 `AttackPotion` `.local/multiplayer-p2/self-attack-potion-choice-f4ecacaab0b34c0ba63b17baa75fba92/peer-0/result.json`、显式验证两格暗球只归持有者的 `EssenceOfDarkness` `.local/multiplayer-p2/self-dark-orb-explicit-31d9a5a5aa194533a3dc52e3f079270f/peer-0/result.json`。四个虚拟双人请求均只有本地自用候选，原生投药后全玩家状态和完整 RNG 差分 Passed；其他选牌、溢球及被动药水机制未验。

P2 `WhisperingEarring` 四人随机队友自动出牌：本地手牌只保留 `Blaze`，显式断言持有人没有力量、恰一名其他玩家得到 5 力量，原版遗物入口与模拟全部状态及九条 RNG 对齐，Passed：`.local/multiplayer-p2/earring-blaze-other-81734c90e5694861bcfbc628c07048e6/peer-0/result.json`。只覆盖这一张牌与首回合入口。

P2 `Blaze` 四人目标排除：Play 阶段直接把一名队友生命设为 0；出牌者和该队友的固定目标候选均被拒绝，指定另一存活队友原版出牌后全部状态及完整 RNG 差分 Passed：`.local/multiplayer-p2/dead-teammate-blaze-excluded-e22b06175e7f45e7898102c76f9e41e8/peer-0/result.json`。死亡 Hook 和后续回合未测。

P2 `OneForAll` 死亡队友群体范围：双人将队友生命直接置 0 后原生出牌，存活出牌者与死亡队友都取得 3 层能力；全状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/one-for-all-dead-ally-e1e846c834dc4fe4bf9ce34db069c4b3/peer-0/result.json`。只验证仍在战斗中的死亡玩家，未运行死亡 Hook。

P2 `EnergySurge` 死亡队友排除：同类双人 Play 根中，死者能量保持 3，出牌者支付 1 点并获得 2 点后为 4；全状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/energy-surge-dead-ally-b3728b1c11814e1c9253a15284ccbd06/peer-0/result.json`。与 `OneForAll` 的死亡玩家仍获能力分开验收；未运行死亡 Hook。

P2 `KnightsElite` 升级牌压制：双人各一张升级 `Bash`，第二招后两张均降级；击杀 `MagiKnight` 后均恢复，原版／模拟两个敌方回合及击杀的完整状态、RNG 差分 Passed：`.local/multiplayer-p2/knights-dampen-upgraded-7a33347c0eae4f63a7e48b78ac939dcd/peer-0/result.json`。多施法者未测。

P2 `KnowledgeDemonBoss` 队友选择边界：预测在每名玩家各自选诅咒前明确停止，原版两人各选第一项后从新根预测接下来的三招；第四招按两人治疗 60 HP，逐回合完整状态及 RNG 差分 Passed：`.local/multiplayer-p2/knowledge-demon-post-choice-9b9e0925426b40e597846920c9ea90f7/peer-0/result.json`。第二／第三组选择未测。

P2 `TheInsatiableBoss` 固定双人前四招：逐玩家沙坑／逃生牌、两次攻击和加力量，从首回合到第五回合的全部状态与完整 RNG 差分 Passed：`.local/multiplayer-p2/insatiable-four-moves-616a05c3a67c422e8e6318bd3a6f2dff/peer-0/result.json`。玩家死亡和沙坑移除未测。

P2 `TwoTailedRatsNormal` 同伴死亡与召唤：双人第二回合杀一只鼠，余鼠排出召唤并补回空位；击杀、排招与新鼠入场的完整状态／RNG 差分 Passed：`.local/multiplayer-p2/two-tailed-rat-resummon-43a8f4fd67ef49b291142f95fb097c2b/peer-0/result.json`。再将两只存活鼠的原版召唤计数设为 3，有空位时下一回合不再排召唤，完整状态／RNG 差分 Passed：`.local/multiplayer-p2/two-tailed-rat-limit-9c30001f1922426f9e87ca0dab9dcf67/peer-0/result.json`。计数是夹具注入，不代表三次自然召唤已测。

P2 `FabricatorNormal` 满员、召唤物死亡和补位：制造机加三名召唤物时预排解离；击杀一只后先执行预排解离，再重新召唤补回空位。双人到第七回合各动作／回合的完整状态和 RNG 差分 Passed：`.local/multiplayer-p2/fabricator-minion-refill-cc858a950da44a19ae47fbe31073af28/peer-0/result.json`。其他召唤类型未测。

P1／P2 双进程 ENet 玩家死亡：加入者 1 HP 被 `FabricatorNormal` 首招击杀，房主存活到第二回合；死者阶段停在 `Start` 且牌与资源清空。房主原版／模拟全玩家状态和完整 RNG 严格差分、双端原生检查点一致，Passed：`.local/multiplayer-p1/enet-player-death-fixed-66898c2c7d5f480a8c87a487d3df1393/peer-0/result.json`、`peer-1/result.json`。虚拟多人在死者自动准备后连续推进，故未用作此项运行证据。

P3 零能量星能支援：双人同根 0 星能时 `Constellation` 不进入搜索路线；2 星能时本地先 `Strike`，再把该牌补给队友，整条原生路线的全玩家状态与完整 RNG 差分 Passed：`.local/multiplayer-p3/constellation-star-support-fixed-cdc8b8d0ed7b415ab0f99cccf159f809/peer-0/result.json`。未来已计划动作由完整路线回放检查，未穷举所有星能组合。

P5 可见 Steam 中文多人方案窗：1920×1080 游戏视口里输出／启动两方案、两回合动作、收益和队友不再主动出牌的条件说明均可见，按钮没有裁切；随后方案按钮选择断言 Passed：`.local/multiplayer-p5/visible-overlay-settled-a70f3bfc5cae4a44abd1c8ffde30d679/peer-0/result.json`，截图 `.local/multiplayer-p5/visible-overlay-settled-a70f3bfc5cae4a44abd1c8ffde30d679/overlay-visible.png`。画面仍包含原版回合入场字样；设置页的独立可见证据见下段，其他分辨率、真实联机房间与人工鼠标操作未验。

P5 可见 Steam 中文多人设置页：同为 1920×1080 游戏视口，打开“性能”页并滚动至“多人搜索”，截图中深度 `2`、时间上限 `3` 秒及说明均可见，未裁切；原生脚本 Passed：`.local/multiplayer-p5/visible-settings-658d5a9c41704fd6847250964c85f0e1/peer-0/result.json`，截图在上级目录 `settings-visible.png`。这是虚拟双人状态；实际 Steam 联机房间、其他分辨率和人工鼠标操作仍未验。

P2 战斗内多人生成遗物：`VexingPuzzlebox`、`OrangeDough`、`Toolbox`、`ChoicesParadox` 在虚拟双人第一回合分别对同根原版 Hook 与预测 Fork 做全玩家／卡牌归属／完整 RNG 差分，四项独立请求均 Passed；后两项选择原版第一项，`ChoicesParadox` 显式核对保留关键词。证据逐项见[多人内容清单](../../MULTIPLAYER_CONTENT_INVENTORY.md)。首个合并探针因把模拟 `AfterSideTurnStart` 的成功返回值误判为等待选择而整体 Failed；改为独立输入后取得四个完整通过结果。原版开战时序及其他选择尚未验证。

P2 队友关联能力跨回合：`Intercept` 首次第二回合差分发现保护者／被保护者能力未按原版敌方回合结束清理；修复后双人全状态／完整 RNG Passed：`.local/multiplayer-p2/intercept-round-fixed-ecfc1cef8fdc435ab87d8ad6639b37ba/peer-0/result.json`。同一入口的 `Flanking`、`Knockdown` 两种敌方能力到第二回合也 Passed：`.local/multiplayer-p2/ally-attack-debuff-expiry-247fefdbb5774c30b1df63dd5203c463/peer-0/result.json`。另验 `Sneaky` 持有者在队友原生攻击后获得 1 格挡，完整状态／RNG Passed：`.local/multiplayer-p2/sneaky-teammate-attack-6674f7a9b4c44d6c8a47c97c85016f67/peer-0/result.json`。`Intercept` 保护者死亡路径、其他叠层与来源组合仍未测。

P2 `Midnight` 跨玩家消耗历史：先两次本地消耗，费用 12→11→10 并原生出牌；新 Fork 内再消耗一张队友牌，然后本地生成新 `Midnight`，原版与预测费用均为 9。各步全部状态／完整 RNG Passed：`.local/multiplayer-p2/midnight-teammate-exhaust-5208e28eda814bf9b930a3b8a93a54c8/peer-0/result.json`。此前预测的卡牌入场镜像漏用累计消耗次数，现从根历史和 Fork 新增计数构成全战斗次数，计数进入 Fork 与状态键。更多跨回合历史未验。

P2 `Intercept` 保护者死亡 Hook：双人出牌后建立保护引用，原版和模拟直接调用 `AfterDeath`，队友 `CoveredPower` 消失且全状态／完整 RNG 一致，Passed：`.local/multiplayer-p2/intercept-applier-death-hook-8d0cd5c52a234cdda18932c4633ffa3f/peer-0/result.json`。这是 Hook 本身的差分；未实际杀死玩家，完整死亡／后续回合不由此覆盖。

P2 `TheObscura` 幻象伙伴死亡／复活：双人第二回合击倒 `Parafright` 后保留场上并排出 `REVIVE_MOVE`，下一敌方回合复活满血。击倒和第三回合全玩家／怪物状态及九条 RNG 原版／预测一致，Passed：`.local/multiplayer-p2/obscura-illusion-revive-ad5858ba568e47a1be822d9ba08b48f9/peer-0/result.json`。主怪先死亡和其他随机招式未验。

P2 共享随机敌人遗物：双人双敌 `Tingsha` 弃牌、`ForgottenSoul` 耗牌、`ParryingShield` 10 格挡结束回合分别只打中一名敌人，造成 3／1／6 伤害，三个边界全状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/random-relic-hooks-assert-8d11c080bfc14a92a60d3199243f3aad/peer-0/result.json`。`Kusarigama` 本地三张攻击前两次各 6、第三次合计 12，逐次全状态／RNG 差分 Passed：`.local/multiplayer-p2/kusarigama-owner-target-1ba4e8ebebe947648e6f26b6b2b15b37/peer-0/result.json`；队友攻击计数和跨回合重置见下文补充证据。

P2 多人过滤生成池药水：双人 `SkillPotion`、`PowerPotion`、`ColorlessPotion` 原生三选一及 `OrobicAcid` 三类牌、`CosmicConcoction` 三张升级无色牌，均只枚举持有人自用候选；实际生成张数、牌主及队友手牌不变，完整状态／九条 RNG 差分 Passed。五个证据目录按药水名列于[多人内容清单](../../MULTIPLAYER_CONTENT_INVENTORY.md)；三选一只覆盖第一项。

P2 共享随机敌人的球与能力：双人双敌 `LightningOrb` 回合结束被动随机打 3，第二回合激发随机打 8 并离开持有人球队列，完整状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/lightning-passive-evoke-862b76b77ff64aad9c84c283926f8576/peer-0/result.json`。`SerpentForm` 原版出牌本身无追加伤害，下一张 `Strike` 造成普通 6 加随机 4，逐步完整状态／RNG 差分 Passed：`.local/multiplayer-p2/serpent-form-shared-target-ac081f843cb54638823e7207bd2dd99d/peer-0/result.json`。多持有者和更多触发未测。

P2 Power 生成池：双人本地各施加 1 层 `CreativeAiPower`、`HelloWorldPower`、`SpectrumShiftPower`、`CallOfTheVoidPower` 后进入第二回合，四张新牌归持有人，队友手牌不变；全状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/power-generation-pools-28f739c456b040ecad3bc8327516a516/peer-0/result.json`。另在独立根验证 `CalamityPower` 持有人打攻击后生成一张持有人攻击牌，全状态／九条 RNG Passed：`.local/multiplayer-p2/calamity-owner-generation-bd1a235a9367428a8073e83076ed002c/peer-0/result.json`。其他池结果和非持有者动作未验。

P2 随机敌人 Power：双人双敌同根，`JuggernautPower` 随持有人得格挡随机打 3、`HauntPower` 随持有人打 `Soul` 随机打 4、`CountdownPower` 下一回合给一名随机敌人 5 Doom；三处原版／预测全状态和九条 RNG 差分 Passed：`.local/multiplayer-p2/random-power-hooks-3aa9cbd891574d8195c59390cea24618/peer-0/result.json`。更高叠层与死人目标变化未测。

P2 自用药水的自动出牌／费用随机化：双人 `DistilledChaos` 原版只自动打出持有人三张防御，持有人得 15 格挡且队友不变，全状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/self-distilled-chaos-cf0defee7a3245cbb7b5c405bfea5e57/peer-0/result.json`。`SneckoOil` 持有人手牌变为四张并消费战斗费用 RNG，队友手牌不变，全状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/self-snecko-oil-835f2a6fb351474f853b4646591159f2/peer-0/result.json`。均只验当前输入。

P2 自用抽牌／资源药水：`BottledPotential`、`Clarity`、`CureAll`、`GlowwaterPotion`、`SwiftPotion` 各自只有持有人候选，原版效果、队友手牌隔离及全状态／九条 RNG 差分 Passed；逐瓶证据见[内容清单](../../MULTIPLAYER_CONTENT_INVENTORY.md)。Glowwater 首次整体 Failed 是夹具在药水耗尽 `Strike` 后继续寻找该牌，修正夹具后 Passed；首试证据保留在内容清单。

P2 自用药水副作用：`EntropicBrew` 的随机药水填槽、`SoldiersStew` 的持有人打击牌重播，以及 `BoneBrew` 的持有人 Osty 召唤分别核对队友隔离，三项原版／预测全状态和九条 RNG 差分 Passed；逐项证据见[内容清单](../../MULTIPLAYER_CONTENT_INVENTORY.md)。后续牌／伙伴效果及其他随机结果未验。

P2 `FoulPotion` 群体效果：原版战斗目标类型 `AllEnemies`，一个无指定生物的搜索候选；实际效果伤及两名玩家和敌人，全部 HP 下降，原版／预测全状态和九条 RNG 差分 Passed：`.local/multiplayer-p2/foul-potion-all-creatures-4119dcc543c24eb392bd38abec48e561/peer-0/result.json`。没有对队友指定药水目标；死亡和伙伴例外未验。

P2 `PotionOfBinding` 群体敌方目标：双人双敌原版用药后两名敌人各得 1 层虚弱和易伤，两名玩家不受减益；唯一无指定生物候选及全状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/binding-potion-two-enemies-ca38a02a2c114c2c811ab8cd0c5a035d/peer-0/result.json`。人工制品与死亡敌人未验。

P2 收尾两瓶普通自用药水：`ShipInABottle` 即时格挡与下回合完整差分 Passed，`FruitJuice` 持有人最大生命及当前生命各加 5、队友不变且即时差分 Passed；证据见[内容清单](../../MULTIPLAYER_CONTENT_INVENTORY.md)。后续测试优先跨玩家专属机制。

P2 `Soulbound` 两层跨玩家生成：本地出牌向队友施加能力，再原生叠到 2 层；本地生成一张 `Soul` 后队友的抽牌堆新增两张队友持有的 `Soul`，全状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/soulbound-two-stack-generation-503930d530244c098bc41d176a640d4a/peer-0/result.json`。第二层是夹具原生施加，未运行死亡引用。

P2 `Soulbound` 的生命为 0 目标：夹具直接置零目标队友生命，再由施加者生成 `Soul`；原版拒绝给死者抽牌堆插牌，预测全状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/soulbound-hp0-target-generation-f62865ab0e11420e88cfbe1de281f5da/peer-0/result.json`。首试因错误断言死者应入牌而 Failed，见[内容清单](../../MULTIPLAYER_CONTENT_INVENTORY.md)；死亡 Hook 未验。

P2 `ImitationLearning` 两次队友能力复制：队友原生依次打 `Inflame`、`StoneArmor`，本地分别自动复制 2 力量、4 镀层，能力从 2 层降为 1 层再移除；逐动作全状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/imitation-two-teammate-powers-49829cf9a2854e1c81d2eecb54ba66cf/peer-0/result.json`。选择型能力及死亡引用未验。

P2 `Kusarigama` 所有权和重置：本地两次攻击后队友插入攻击不增加本地计数，本地第三次仍触发；下一回合本地第一张攻击只造成普通伤害。交错出牌及跨回合完整状态／九条 RNG 差分 Passed：`.local/multiplayer-p2/kusarigama-owner-reset-4c503274feb94487b533f2d81e7fea14/peer-0/result.json`。

该全战斗消耗计数修改后的受影响单人哨兵：原版单人 `IRONCLAD`／`FUZZY_WURM_CRAWLER_WEAK`，1 秒短搜取得首个有效结果，Passed：`.local/multiplayer-p1/single-after-midnight-401bb519dd0e42e8bd1be853bca91382/result.json`。该输入没有生成 `Midnight`，只验通用搜索未因状态字段新增而停止。

P1 `PaelsEye` 虚拟双人额外回合探针未通过：注入持有人遗物后，预测转到其第 2 回合；虚拟原生端未在 120 秒内到达稳定额外回合边界。三次定位的严格等待超时、过早取样和放宽目标条件后仍超时证据见[规划最新记录](../../MULTIPLAYER_PLAN.md)。原版源码定位根因：虚拟多人用单人 NetService，`AllPlayersReadyToEndTurn` 恒真；额外回合开始标记被排除的队友结束后提前返回，持有人进不了 `Play`。临时虚拟探针已撤回，真实双端证据如下。

双进程 ENet `PaelsEye` 已通过：`.local/multiplayer-p0/enet-2-18a7ba08e08442f6b5fc2c9cbfac17cb/peer-0/result.json`、`peer-1/result.json` 均 `Passed`。两端逐检查点核对原生完整状态、玩家阶段和九条 RNG；原版／预测差分确认房主独自进入额外回合 `Play/2`、队友保持 `Start/1`，随后额外回合结束、敌方行动，两人进入普通回合 `Play/3` 与 `Play/2`。该测试先后暴露客户端动作实例等待错误、预测把非参与者置为 `End`、预测错误保留队友手牌；对应失败与修复链见[规划最新记录](../../MULTIPLAYER_PLAN.md)。先前内存准入失败的证据仍为 `.local/multiplayer-p0/enet-2-83af4982d6bd4723865877d37710f5c1/`。Windows 与 Bash 入口均支持探针；Bash 只做语法检查，Linux 游戏未运行。

双持有人 `PaelsEye` 独立分支也已通过：`.local/multiplayer-p0/enet-2-63c67f84932b41f6ad578241f830edfd/peer-0/result.json`、`peer-1/result.json` 均 `Passed`。加入者先打攻击牌，房主独自进入额外回合；加入者遗物的“上一回合参与”标记变假且仍未使用，回到共同回合后变真。被排除队友的上一回合出牌历史不再污染预测；两端原生检查点和原版／预测完整状态、九条 RNG 均一致。修正前首次差分在 `.local/multiplayer-p0/enet-2-17834e2d7eb14018aa43740ade12e6e7/peer-0/result.json` 失败，根因记录见规划。

P2 `HibernatePower`／`FrostOrb` 跨玩家格挡：双人本地球主的霜球被动让两名玩家各得 2 格挡，激发让两人各再得 5，队友没有球、持有人队列清空；两次原版／预测全状态与九条 RNG 差分 Passed：`.local/multiplayer-p2/frost-hibernate-both-players-a809108ca0b64db19510bc03387da4ad/peer-0/result.json`。能力和球由夹具施加，跨回合能力递减未验。

## 多人 P1 普通状态差分（2026-09-28）

原版 `0.111.0` 虚拟双人／四人：逐玩家普通防御、打击和第二回合固定 EndTurn 的实际／预测完整续用状态一致，包含每名玩家资源、牌堆、球、药水、遗物计数、敌人及九条完整 RNG。四人根中人工改变队友格挡、卡牌所有者和 RNG，续用戳与搜索状态键均检出；兄弟 Fork 未污染根。最终四人请求 `.local/multiplayer-p1/final-4-9ae71f6086b24ead9abe252894479d9a/peer-0/result.json` Passed；单人短搜 `.local/multiplayer-p1/single-sentinel-edc4ce17d5f642ea89dbe1b3c39b3498/result.json` Passed，DLL SHA-256 `3360B56D9CA785383F1119F7DA33A2C513D426334A681217C4511B77DCBB6B25`。Release 0 警告／0 错误，Windows 结构门禁 238 通过。首因失败和修复链见 [规划 0.5 节](../../MULTIPLAYER_PLAN.md)；复杂 Hook 顺序、额外回合、死亡／复活、跨玩家选牌与正式联机搜索仍未通过。

另以 `Plot`、`Coordinate`、`Fade` 验队友跨回合能力，先定位普通怪物多目标攻击缺失，再修复；双人基础版即时结算及第二回合完整状态／RNG Passed，证据 `.local/multiplayer-p1/power-round-fixed-ac3e4164274049fda0e7f2b3d8970463/peer-0/result.json`。范围限于这三张牌和固定遭遇。

## 多人 P0 原生链路（2026-09-28）

实测游戏版本 `0.111.0`。交接原型的虚拟双人、虚拟四人请求分别 Passed：每名玩家防御、攻击、生存者原生弃牌选择后进入第二回合。修正测试选牌未发送 `SyncLocalChoice`、客户端等待未入队的原请求动作后，同一源码双进程 ENet 房主／客户端均 Passed：双方各自操作本地玩家，逐动作原生状态、玩家阶段及九条完整 RNG 一致，结束回合进入第二回合。原版单人建局后首个短搜结果 Passed。证据与确切输入目录见 [多人规划第 0.4 节](../../MULTIPLAYER_PLAN.md)；成功实例均由启动器报告删除。Windows 双进程编排入口 `tools/run-multiplayer-p0-enet.ps1 -Port 33771` 本轮 Passed，Bash 对应入口只通过 `bash -n` 语法检查，未在 Linux 运行。

上述虚拟测试是单进程原生结算；ENet 测试两端均加载测试 Mod。尚未验证四进程网络、无求解器对端、Linux、生产模拟差分、搜索、执行和可见 UI；原生对端一致不构成预测差分。修复后的 `dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false` 为 0 警告、0 错误。Windows 结构门禁同步 `Executor partial` 声明检查后通过，`REFACTOR_BOUNDARIES_OK search_files=238`；内容清单仍待完成，P0 未收口。

## PR #144 最终修复与合并验证（2026-09-28）

以 `main@f47c447a` 整合 PR head `1e914b38`，修正 `ReclaimWithinSearch` 主动退出路径的恢复许可，并将复审夹具纳入 `GcRecoveryChecks.RunExplicitDefaultExit`。原候选同一真实 CLR 边界失败：主动退出后 `enabled=True / attempts=1 / restarts=1`；原 main 通过。修复后 `recovery-lifecycle` 3 项、`checkpoint` 1 项通过，主动退出结果 `EXPLICIT_DEFAULT_EXIT_OK attempts=0 restarts=0 forced=0`，正常恢复仍为 starts=1/restarts=1/forced=0，取消与退出清理通过。

复用前一轮候选 `recovery` 11 项成功证据；本次保留同一退避与分类实现，只修复实际主动退出调用处。最终 Mod Release 构建 0 警告／0 错误，关闭自动复制，供合并后的本地五文件部署复用。未运行 Linux、可见游戏性能或全量 GC 套件。红灯与原 main 对照位于 `.local/audit-pr144-latest-20260928/`，最终合同日志位于 `.local/pr144-merge-20260928/`。
