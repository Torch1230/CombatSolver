# CombatSolver 测试入口历史卷 02

## 策略重构 P2 请求级预算所有权（2026-09-27）

- 合入 0.47.1 后，`python tools/search/StrategyCorpus/run.py --manifest coverage/corpora/strategy/p2.json --out .local/strategy-refactor-p2/baseline-0471` 一次采集 #24、#37、#81、#89 与两个生成场景，六根均为 `comparable`。原始包与完整证据留在 `.local`，实例由启动器清理。
- `python tools/search/StrategyCorpus/run.py --manifest coverage/corpora/strategy/p2.json --out .local/strategy-refactor-p2/ledger-outer` 后，`python tools/search/StrategyCorpus/compare.py --left .local/strategy-refactor-p2/baseline-0471 --right .local/strategy-refactor-p2/ledger-outer --out .local/strategy-refactor-p2/compare-ledger-outer`：六根动作、续用、结果、expanded、transitions、choice branches 和剪枝计数逐位相同。
- 运行器修复后 `python -m py_compile tools/search/StrategyCorpus/run.py tools/search/StrategyCorpus/compare.py tools/search/StrategyCorpus/test_compare.py` 通过；本次 Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=215`。按用户要求未运行 Linux 门禁；未做完整自动战斗或大批量回归。
- 补充审计改用 `SearchPassContext` 后，#24 `start` 严格恢复与 15 秒配置的 SearchOnly 请求 `286768afb4d14118863cdf5e79239cdd` Passed，实例已清理。此请求只验证上下文边界可执行，不与 110 秒固定语料比较，也不声明整场质量等价。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=216`；未运行 Linux 门禁。
- `SearchPassResult` 接管轮次停止状态后，#24 同根 SearchOnly 请求 `7ba221e6f4ef4d48926992768476e17b` Passed，实例已清理；与上一条的预计战损同为 1 HP，请求总展开 157004、转移 460496、选择分支 21493 均一致。这只覆盖普通返回路径，不代替接管或无胜利升级验证。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=217`；未运行 Linux 门禁。
- 无胜利升级改用 `SearchPassContext` / `SearchPassResult` 后，#24 `start` 严格恢复与 SearchOnly 请求 `9bcf08de582f4c67a0b6ea61cd03eba0` Passed。非固定预算 110 秒、首轮节点帽 5000；实际日志 `NO_VICTORY_ESCALATION start attempt=1 beam=135->270 nodes=5000->10000`，随后 `won=False improved=False`，保留首轮路线，实例已清理。60 秒配置的诊断请求没有进入升级，因为首轮约 29 秒、下一轮估计约 58 秒超过剩余时间；未把它算作升级路径验证。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=217`；未运行 Linux 门禁。
- 主搜索改为直接消费 `SearchPassContext` 后，#24 `start` 严格恢复与 15 秒配置的 SearchOnly 请求 `6e9c6c8bfa854a39bd610037c42fb536` Passed，实例已清理；这是执行路径检查，不是整批逐位对照。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=217`；未运行 Linux 门禁。
- 主 Pass 内六处前缀补搜预算读取迁入账本后，Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=217`。上一条短场景没有触发这六处前缀通道；此边界的行为对照留到 P2 收口的固定语料，本条只记录构建与结构证据。按用户要求未运行 Linux 门禁。
- `SearchRequestPipeline` 接管请求级首轮与升级派发后，#24 `start` 严格恢复与 15 秒固定预算 SearchOnly 请求 `23eaac6ccffc4e96896cdeb2aa272227` Passed，实例已清理。该请求覆盖首轮与固定预算返回，不把它写作本次无胜利升级的独立验证。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=218`；未运行 Linux 门禁。
- 开局能力／夜魇和宽度／新颖性成员改用 Pass 上下文后，#81 同根 SearchOnly 请求 `9af903cb5fdb4b1f941ea892863b7082` Passed，实例已清理。与 `baseline-0471/report-81` 同为 VeryHigh、Beam 135、25,000 节点、DOP 1、110 秒固定预算；动作与搜索结果仅有运行环境的 `savedNoGcRegionEnabled` 标记不同，归一化后的 `solverMetrics` 和执行政策无差异。先前请求漏传预设而用了包内 Beam 300，属于不可比较的诊断运行，不计入回归。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=218`；未运行 Linux 门禁。
- 三个补充审计入口共用 Pass 上下文后，#81 同根同政策 SearchOnly 请求 `b00a458217a840b8a4be98e773a75242` Passed，两层 Smart 药水梯度均执行，实例已清理。与 `baseline-0471/report-81` 的执行政策、`search-result.json` 全字段及剔除时间／分配／GC 的 `solverMetrics` 无差异。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=218`；未运行 Linux 门禁。
- Smart 药水梯度的层转移与回收开销改由账本读取后，#81 同根同政策 SearchOnly 请求 `65baeec98b724b2daa8ab9c2ea8894b6` Passed，实例已清理；与 `baseline-0471/report-81` 的执行政策、`search-result.json` 全字段及剔除时间／分配／GC 的 `solverMetrics` 无差异。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=218`；未运行 Linux 门禁。
- 双药死亡路线补搜移入 `PostSearch` 后，#17 `start` 同根 SearchOnly 请求 `7da93842f7d340358962d6c2d7267cd5`（60 秒）及 `cc2b89675b0e46ec80b58809d0d7cd12`（120 秒）均 Passed，实例清理；两次都是 5,000 节点、DOP 1、固定预算，所选仍为死亡路线，日志没有 `EARLY_POTION_PAIR`。因此这两份只证明请求通过，**不证明双药候选派发等价**；不能拿它们与历史 180 秒 / DOP 8 的双药胜利数值对照。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=219`；未运行 Linux 门禁。
- 双药 Pass 使用 `SearchBudgetWindow` 后，Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=219`。上条短请求没有实际双药候选，当时仅取得公式与结构证据；实际候选验证见下一条。未运行 Linux 门禁。
- 随后 #17 按原记录的 VeryHigh／180 秒／DOP 8／非固定预算执行 SearchOnly，请求 `9d5bdd2a400549ad8276bffb234493ac` Passed、实例清理。日志依次出现 `EARLY_POTION_PAIR` 的 `BLOCK_POTION+SWIFT_POTION`（选中）与 `SWIFT_POTION+BLOCK_POTION`（未选中），两条均完整胜利、预计战损 69 HP；最终用药 2 瓶。与历史报告的该机制结果一致，但历史 Mod 版本不同，不宣称完整工作量逐位相等。
- `SearchPassResult` 加入质量、累计工作量和轮次终止状态后，#24 `start` 15 秒固定预算请求 `2fdcb2d77b414b25b7a1b17d9127e46c` Passed，与先前 `pipeline-representative` 的执行政策、完整搜索结果和剔除时间／分配／GC 的指标无差异。另以 VeryHigh／110 秒／5,000 节点／DOP 1 非固定预算请求 `6b9d1d1f3aab4f59a0e6bf86808c0427` 验证升级：日志出现 135→270 Beam、5,000→10,000 节点，`won=False improved=False`，结果保留首轮路线；与此前同政策升级请求的完整搜索结果一致。升级轮能力成员的时间额度因实测时钟相差 127 毫秒，不计入逐位一致。两请求 Passed、实例清理；未传 VeryHigh 的一次诊断请求不参与对照。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=219`，未运行 Linux 门禁。

## 策略重构 P0/P1 固定根对照（2026-09-27）

- P0 对六个 `combat_start` 玩家根及两个生成场景各运行一次 VeryHigh / 每 solver 25,000 节点 / DOP 1 基线；P1 最终源码对同八根各运行一次。六个玩家根的严格恢复、continuation 与原生状态均通过。两轮原始动作及完整证据留在 `.local/strategy-refactor-p0/`；启动器清理无头实例。
- 基线的战损／用药依次为 #24 0/1、#37 1/0、#79 50/0、#81 31/0、#85 74/0、#89 9/0；生成场景 `GA-IRONCLAD-ELITE-00` 74/1、`GA-SILENT-BOSS-00` 44/0。这是固定短搜口径，不能与历史 180 秒策略成果直接比较。
- `python tools/search/StrategyCorpus/compare.py --left .local/strategy-refactor-p0/baseline --right .local/strategy-refactor-p0/after --out .local/strategy-refactor-p0/comparison`：#24、#37、#81、#89 与两个生成场景的动作、结果、续用、expanded、transitions、choice branches 及剪枝逐位相同。#79、#85 的 P0 基线分别用时 109,992 和 109,985 毫秒，贴近 110,000 毫秒限时；两根标为不可比较，不将工作量漂移算作纯重构差异。四个有效玩家根已达到最低门槛，备用 #56/#63 未运行。
- `python tools/search/StrategyCorpus/test_compare.py` 的分类、根身份、时限合同通过；最终 Release 构建 0 警告、0 错误。Windows/Linux 结构门禁均通过，Linux 侧 `rg` 解包于系统临时目录运行，无系统安装。未做完整自动战斗或大批量回归。
## GetId 缓存与模组注册时序（#141，2026-09-27）

- macOS 克隆游戏 + 隔离 HOME + `--force-steam=off`，mod_list 为 RitsuLib → CombatSolver → 探针。探针是最小 RitsuLib 内容模组：一张普通卡，在 `ModelRegistryInitializedEvent` 里打印 `GetId`；另编一个含与原版同名 `Leap` 卡的版本。不装求解器：`CARD.GET_ID_PROBE_CARD_PROBE_UNIQUE`，同名版正常启动；工坊 0.47.0：`CARD.PROBE_UNIQUE`，同名版 `DuplicateModelException` 启动失败；修复版：两种都与不装求解器一致。
- 控制器会话合同新增：进入战斗时注册表初始化信号已送达，且门控对原版类型始终放行、对模组类型只在信号后放行。该合同需要 Linux/Windows 无头入口，本机未重跑；macOS Release 构建 0 警告、0 错误，结构门禁通过。
## 下一版本（开发中）：最终续用单次回放（2026-09-27）

- 基线 `72e0f799`，所有搜索DOP1。抽弃牌／故障机器人两根各四次独立进程ABBA，固定预算且无时间边界，完整根／政策、动作含嵌套选牌、续用文本、非时序指标与剪枝计数一致。根回放计数按真实工作分别4→2、8→2单独断言；展开／转移维持1564／34802及2000／7783。计时区间重叠，不称稳定整体提速。
- `OFFLINE_HARNESS_FIXED_PREFIX_CONTINUATIONS=1` 的最终单牌输入：4／8／17回合固定终局前缀，四个独立进程ABBA，每case预热一次＋测量三次。36份计时结果、完整输出跨版本对账及计时外独立前缀oracle通过；核对完整StateText、回合、offset、数量、顺序和live根不变。17回合18.052→6.899ms只属于人工固定前缀Solve，实际搜索展开0。原始／汇总见 `.local/fixed-dop-20260927/long-prefix-final/` 与[报告](../performance/fixed-dop-20260927.md)。早期多牌误注入批次作废，不计最终证据。
- 原生 `FIXED-PREFIX-TURN-OUTCOMES` 已补充同一独立oracle和三个长路线case，原三回合actual/predicted验证保留。**本轮未执行**：`linear-replay-prefix` 启动前检测到玩家 `SlayTheSpire2.exe` 会话，120秒准入超时；没有停止其他进程或扩大超时，启动器输出 `UNATTENDED_INSTANCE_REMOVED`。forced-end／setup／adoption路径仅静态审阅，未称原生通过。
- Windows Release构建0警告0错误，两端结构门禁均 `REFACTOR_BOUNDARIES_OK search_files=212`；Bash门禁在Windows Git Bash运行，不是Linux游戏验收。只改离线helper输入后重编该宿主，生产DLL未变，因此复用已有普通搜索及构建证据。不运行完整自动部署、可见性能或发布门禁。

## 下一版本（开发中）：注能核心首回合产球（2026-09-27）

- 失败来源为0.47.1／`36372d40`的问题包 `0762b1da246243f1936a1e8750be8588`，工具箱选牌完成后第一次差异是预测0球／原生3个闪电球（4/9）。同版本游戏的 `InfusedCore.AfterSideTurnStart` IL核对了参与者、首回合条件及3次产球；原包未恢复。
- `tools/search/OfflineSearchHarness/InfusedCoreChecks.cs` 用生产DLL创建根，注入分支空球队列后调用真实遗物Hook。`OFFLINE_HARNESS_INFUSED_CORE_CHECKS=1`、DEFECT、`--milestone M1`：修改前失败 `first turn must channel three orbs; actual=0`；修改后14项Passed，覆盖3球、被动4／激发9、历史新增3次、T2空球不再产球、T2已有球不重复触发、持有者未参与不触发，以及根、父子、兄弟和球Model独占。证据 `.local/issue-bundles/0762b1da246243f1936a1e8750be8588/fix/{baseline,final}/`。这是离线诊断，不是原生actual/simulated验收。
- 新增 [INITIAL-TOOLBOX-INFUSED-CORE](../../../coverage/fixtures/scenarios/state/initial-toolbox-infused-core.json)：原生开局注能核心＋工具箱、1500ms固定搜索、增量等价，在首次准备结果完整状态匹配及原生选择顺序断言后停止，总超时120秒。**本轮未执行**：实际游戏进程仍运行，既有无头准入门禁禁止并行启动；未关闭用户游戏、绕过门禁或创建无头实例。
- 旧 `RELIC-HOOKS-BATCH-054` 从已完成原生产球的Play状态取根，只证明既有球与未来回合，不再作为空球准备根的首次产球证据。覆盖分类已改为显式模拟补偿，保留原生验收未完成的说明。
- Windows Release构建0警告0错误，修改文件JSON解析及格式检查通过。CoverageCatalog原有工程缺少RitsuLib分程序集引用，先因`GetOriginalIl`／`HarmonyIl`编译失败；使用仅本地的额外引用后构建成功，但`--verify-runtime-evidence`在读取既有`LOOP-FINAL-20260921.status=PassedWithDocumentedBoundaries`时抛JsonException，未完成覆盖门禁或重新生成派生目录。此问题不归因于本次产球修复，不伪造Passed状态。本轮不提升版本、不打包或发布；不宣称完整战斗、实机选牌部署或其他Mod组合已验收。

## PR #143 合并上游 0.47.1（2026-09-27）

- 合并基线为上游 `7d9b4bed`，包含 `a59d319d`。手工解决 Opening、Phases 和两份记录文档冲突，保留上游终局／边界候选门禁、前缀异常清理及准备阶段整体补充审计旁路，同时保留本分支选择时点估值、终局前缀统计和七表／缓存校验。代码审阅未发现阻断问题。
- 合并后Windows Release构建0警告0错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=212`，冲突标记与未合并索引清除，`git diff --check` 通过。
- 原生六场最小集在第一场启动前因实机游戏进程仍运行而被启动器的未知游戏进程门禁排队，120秒准入超时；未启动无头游戏，不计行为失败或通过，也未关闭实机游戏、绕过门禁或扩大超时。自有实例 `pr143-merge-20260927` 由清理入口输出 `UNATTENDED_INSTANCE_REMOVED`。证据 `.local/pr143-merge-20260927/prefix-launcher.log` 与 `cleanup.log`；以下此前原生测试均为合并前证据。
- 改用不启动Godot的离线Coordinator／Smart哨兵：故障机器人精英、Low、Beam24、每成员2000节点、DOP1、20秒，完成10000总展开／40482转移、10 HP／0药。与此前同根结果106项非时间字段、完整动作路线、根／续用状态和策略配置一致，均未触发时间边界。证据 `.local/pr143-merge-20260927/offline/defect/` 与 `offline-comparison.json`；只证明该离线根，不替代原生整合或可见性能验收。
- 准备根合并后遵循上游提前返回，不再要求出现此前的Smart补充梯度日志；原始六场集保存在本地运行脚本。未执行合并后的原生六场、Linux或可见Steam验收，没有性能结论。

## 下一版本（开发中）：开局弃牌选择时点估值（2026-09-27）

- 失败基线来自本机 `MYTES_NORMAL` / `057a700fd80b4a65ac7f8641b4ad2cbf` 第1回合generation15，18:40:19.858智能用药审计的 `OpeningDiscardChoiceCardValue` 抛 `FLICK_FLACK+0 source=Hand action=ACROBATICS`。根牌组没有该牌；同战另一成功候选记录攻击药水生成该牌、临时0费与后续恢复1费，不把它当作失败分支完整回放。
- 新增 [OPENING-DISCARD-CHOICE-VALUE](../../../coverage/fixtures/search/opening-discard-choice-value.json)。杂技从抽牌堆抽到两张同名同升级、伤害7／17的临时零费 `FLICK_FLACK` 及升级的中和；逐分支确认弃牌自动打出后旧完整状态键已不在四个牌堆，而估值仍等于选择时的正确物理实例。全部兄弟只建立一份纯值表，命中缓存后伪造状态键或选择上下文继续明确失败。
- `ea5c7b5bb1b34591b58539e8e9dac298` Passed：真实 `BuildOpeningHandSetupActions` 在DOP1及DOP2配置下成功，DOP1启用增量回放；搜索前后live不变，完成分支Fork状态不变，原生杂技选择和弃牌自动打出结束后完整ContinuationStamp与预测一致。这里的DOP2是入口配置覆盖，不宣称该局部Expand实际双lane并发；没有运行整场搜索质量或性能对照。
- Windows Release构建0警告0错误、结构门禁 `REFACTOR_BOUNDARIES_OK search_files=212`、静态代码审阅及 `git diff --check` 通过。实例使用仓库 `.local/headless-instances/opening-discard-value-1`，已由启动器输出删除成功；证据 `.local/opening-discard-value-20260927/native-1/`。未恢复原玩家战斗、未覆盖所有第三方及嵌套前置选择组合，未启动可见Steam。

## 下一版本（开发中）：跨回合固定前缀结果完整性（2026-09-27）

- 原始失败证据是旧日雕像50,537展开的持久路线：预测Continuation为T3 HP70→T4 HP61，末态HP43／累计掉血27，但七张逐回合结果表仅有T4–6。UI求和显示18，结束回合保护缺键读0。没有恢复原玩家战斗或取得已经退休的该场完整live日志，不据此宣称另有怪物／药水模拟偏差。
- 新增 [FIXED-PREFIX-TURN-OUTCOMES](../../../coverage/fixtures/search/fixed-prefix-turn-outcomes.json)，通过现有无人ScenarioId入口运行。最小构造三次EndTurn固定前缀与第四回合后续搜索，内部DOP1／最多100展开／5秒，开启增量等价；验证每个前缀节点在结果投影前已持有Outcome、显式零及正战损、末回合部分前缀／终局前缀、空前缀、不合法回合和终局后动作仍拒绝，live根不变。独立原节点后备投影验证非零卖血差额、原分数不变和既有比较标注优先。
- 对同一真实求解结果逐项移除战损、回血、敌方损血、卖血、最大／实际格挡与能量表的第三回合键：七种磁盘缓存均按未命中处理且内容未改，录像导入拒绝；生成结果序列化、内存续用及结束回合预计值查询均拒绝缺项。正确结果序列化往返通过，未删除玩家缓存。
- 最终 `ea17a1f38de84a3b9b32797923ca1892` Passed，原生从T1逐次推进到T4，每次等待明确的EndPlayerTurnAction完成，完整ContinuationStamp与预测一致，LiveEndTurnRiskEvaluator与计划该回合损失一致，复用及UI求和包含前缀损失。只跑至最早覆盖三段前缀的边界，未执行SolverController全自动停机分支或整场自动部署。证据 `.local/fixed-prefix-outcomes-20260927/prefix-3/`。
- 首次 `d67f326496fa4f749b9c68d990f39bea` 在新配置的首次洗牌教程等待至120秒，未记通过，启动器停止并清理实例。只在后续私有实例中导入进度模板并关闭教程，不改实机设置；`6feda5c2bbae43649327d0645302ba8f` 通过后，因新增节点所有权及精确异常断言运行最终夹具，未扩大超时。所有实例均在仓库 `.local/headless-instances/`，启动器分别输出删除成功。
- 终局归属哨兵 `TERMINAL-TURN-PLAYER-START-V0111` / `6b4fb4ff7e594ebc9741e71155942a28` Passed：T1 EndTurn在T2准备阶段通过 `MERCURY_HOURGLASS` 获胜，增量验证／0战损／终局T2及动作回合结果完整性通过，避免按终局T2误要求第二回合动作统计。其后仅增强测试代码，生产源码未变，不重复该哨兵。
- 最终Windows Release构建0警告0错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=212`，代码审阅通过。未运行Linux、原玩家存档重放或可见Steam测试；不将该最小合同当作全部卡牌语义或全量质量验收。

## 下一版本（开发中）：回合准备固定前缀边界（2026-09-27）

- 失败基线来自本机原生 `GAMBLING_CHIP` 开局日志：`CombatBeamSolver.SolveCore → CombatSearchCoordinator.RunSearchPass` 抛 `include_turn_setup=True prefix=+STAMPEDE`，对应延后能力 `[EndTurn, STAMPEDE]`。未恢复完整玩家存档；不把后续同遭遇重试当成同一根。
- 原生短场景先用较强牌组验证页面顺序，`df377b96d5a14c718adbdc21ed3732cd` Passed；该根提前取得零战损，所以另用含6张伤口的 [准备阶段回归夹具](../../../coverage/fixtures/ui/turn-setup-fixed-prefix-stampede.json) 覆盖仍需补充搜索的承伤根。最终 `0c96548a196e449e9fa98a7acd6e7422` Passed：12张牌、`GAMBLING_CHIP` 与惊逃，DOP2、20秒固定时间预算，完整胜利投影21 HP／0药、5回合，总展开8,549、转移17,429；原生 `Visible → SearchStarted → PlanReady → Selected` 顺序通过，在首个准备结果与选择执行后停止，没有部署整场战斗。
- 最终日志包含一次 `OPENING_PREFIX_REFINEMENT skipped reason=TurnSetupRoot`，仍进入正常 Smart 梯度（本根无药，返回 `no_potion_acceptable`）；没有把准备前动作送入可选固定前缀成员。夹具中的性能档位／Beam／节点测试覆盖在准备结束后才应用，本次准备搜索实际基线为 Beam60／120,000节点，不能按请求中的Low／24／6,000解释。证据 `.local/turn-setup-fixed-prefix-20260927/native-setup-loss/`。
- 首次启动在游戏请求提交前因无默认离线 `settings.save` 失败，未计行为验证；只向新建的自有隔离实例复制当前Steam设置作为模板后继续，未修改实机配置或存档。首次失败和两次完成均由启动器输出 `UNATTENDED_INSTANCE_REMOVED` 删除整个实例；所有实例位于仓库 `.local/headless-instances/`。
- 普通 Play 根哨兵复用本轮修改前保存的故障机器人精英 Coordinator / Smart 输入与政策：Low、Beam24、2,000节点、DOP1、20秒。修复后总展开10,000、转移40,482、10 HP／0药，与基线106项非时间对照、完整 `route.json`（包括选择）、根续用文本、后续续用及策略文件相同，均无时间截断。该对照仅证明此根的原路径保持，不宣称准备根的搜索质量不变或性能改善。证据 `.local/turn-setup-fixed-prefix-20260927/play-sentinel-comparison.json`。
- Windows Release 构建0警告、0错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=212`，代码审阅通过。未运行Linux、完整自动部署或可见游戏验收；实机由用户验证。

## 0.47.1 紧急回归修复（2026-09-27）

- `FIXED-PREFIX-TURN-LOSS` / `01ed4188c8804eedab36e7658de62a57` Passed：固定前缀先掉血再结束首回合，所选路线该回合标注与模拟累计掉血一致；实例已清理。
- 永世沙漏报告 `dfcce7302842419987d9039968a76412` / `607138e217574725855adbb6d83b925e`：`start` 严格恢复与 SearchOnly 通过，120 秒上限内实际搜索约 83 秒，所选路线首回合标注 14 HP。原报告是 0→25 HP 的复核暂停；本次路线与搜索上限不同，只验证缺失标注已出现，不称为原路线逐位复现。实例已清理。
- 无厌沙虫报告 `fbb5f72709ba412890063c7df907785d` / `d83be75b0e104d00882b08903058bd9b`：`combat_start` 严格恢复与 15 秒 SearchOnly 通过；准备选牌场景 `NOVELTY-TURN-SETUP-CHOICE-0400` / `2c288a63cd634a6693f50850b456f998` Passed。两实例已清理。
- 蜂群术士报告 `932f3cf624854741a4e5dddd3cb7cdc8` / `4967277af82843dfb28423777c048ec0`：`start` 严格恢复与 15 秒 SearchOnly 通过；此前开局选牌前缀在候选审计中失败。实例已清理。
- `LAMP-INKY-SHIV` / `0acb7fded0ba419eab9b2f64a63eec5f` Passed：墨刃生成的小刀触发不安油灯，逐动作完整续用状态与实机一致；实例已清理。
- 未运行 Linux 门禁；尚未验证所有上报的结束回合复核、选牌及计算失败根因。

## 0.47.0 前两回合实验开关（2026-09-27）

- `NOVELTY-PORTFOLIO-SETTINGS` / `9b087e2edcc54785aeb3922bd80dbb5d` Passed：新安装默认关闭，设置页第三个实验开关、持久化和请求冻结通过；开启时深度 2、整次探索期限 2400000 ms，关闭时深度 0。
- `UI-LOCALIZATION` / `b1fb101ce25c4b4aaa764552645cbd7c` 在 `BYGONE_EFFIGY_ELITE` 的怪物生成阶段报 `No valid next state found`，早于本次新增文案检查；改用既有有效遭遇 `PHROG_PARASITE_ELITE` 后，`3b8cecc6f8f24a1e9f04c88c29ce373b` Passed，eng/zhs/zht 共 451 条文本目录与设置控件检查通过。两次测试实例均由启动器删除；未做可见 UI 排版验收。
- 第 89 包 `10d01cc2d1f7445c8ff72e76e783aeb0`：`combat_start` 严格恢复，开启两回合追加搜索各保留 24 个状态，完整胜利仍为 7 HP / 0 瓶，与此前默认结果相同；总墙钟 413 秒，请求总展开 1110752。证据 `.local/strategy-sessions/worldline-20260925/requests/20260927T0512186253886-run`。
- 第 100 包 `88619c63f91b48998737d7a9d623e2df`：用户指令停止时仍在运行，本地人工终止游戏；工具记 `process_crash` 只是缺少结果文件，不能计为自然崩溃或质量结果。证据 `.local/strategy-sessions/worldline-20260925/requests/20260927T0520096682165-run`。

## 离线前两回合追加搜索（2026-09-27）

- 骑士精英 `fd3b6e70cb9340a3bad6aae94d5b6b0b`：同一 `combat_start`，普通搜索 12 HP / 2 瓶；`--early-turns 2 --deadline-seconds 300` 完整胜利 1 HP / 2 瓶，实际展开 184237 个追加节点、续搜 5 条，仍按药水成本比人工 9 HP / 1 瓶落后 1 HP。证据 `.local/strategy-sessions/worldline-20260925/requests/20260927T0441084227454-run`。
- 夜魇包 `a422c1c56022446c85f6ce00962019c4`：300000 追加节点的诊断搜索完成，但只找到未结束战斗的路线，未计入优化；之后另一请求在原有 `NO_VICTORY_ESCALATION` 阶段发生游戏原生访问冲突。首份转储异常为 `0xC0000005`、执行地址 0；原生间接调用的具体对象缺少符号，不能认定新追加搜索是唯一原因。异常结果选择路径中的候选快照释放缺口已修正，原生崩溃仍需单独定位。证据 `.local/strategy-sessions/worldline-20260925/requests/20260927T0430183686575-run`、`20260927T0437190817753-run`。

## 策略迭代脚手架（开发中，2026-09-25）

- 前 150 第 17 包 `b8bafe147e09452b97111fb036a619ca`：`combat_start` 与第 7 回合玩家检查点严格恢复，原生状态核对通过；报告引用的第 4 回合检查点缺少状态材料。VeryHigh / 180 秒 / DOP 8，同根基线只有死亡路线，结果字段 75 HP / 0 瓶；最终双药成员完整胜利 69 HP / 2 瓶、剩余 6 HP。玩家第 7 回合检查点当前源码续搜为整场 38 HP / 累计 2 瓶。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T2306436226653-run`，双药成员 `20260926T2318178824475-run`，玩家检查点 `20260926T2310192232938-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 15 包 `10d01cc2d1f7445c8ff72e76e783aeb0`：`combat_start` 与玩家首、次回合检查点严格恢复，原生状态核对通过。VeryHigh / 180 秒 / DOP 8，同根基线完整胜利 12 HP / 0 瓶、剩余 69 HP；提前弃牌后 7 HP / 0 瓶、剩余 74 HP；玩家第二回合检查点当前源码续搜 5 HP / 0 瓶，未追平。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T2159092478223-run`，首回合检查点 `20260926T2202141519802-run`，第二回合检查点 `20260926T2201035376947-run`，最终 `20260926T2230339782718-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 11 包 `bb426281cba74e048f2fd79a8e41bb87`：`combat_start` 与玩家首回合检查点严格恢复，原生状态核对通过。VeryHigh / 180 秒 / DOP 8，同根基线完整胜利 51 HP / 0 瓶、剩余 24 HP；持续减费药水的有界开局前缀后为 18 HP / 1 瓶、剩余 57 HP，与旧人工投影一致。最终路线首回合攻击女王，第 3 至 6 回合清火炬，再收女王。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T2100329160712-run`，玩家检查点 `20260926T2102388988037-run`，最终 `20260926T2128515571842-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 8 包 `878d73bbdbed442bb5fcd13a5a4556c5`：`combat_start` 与玩家第二回合检查点严格恢复，continuation 和原生状态核对通过。VeryHigh / 180 秒 / DOP 8，同根修改前完整胜利 39 HP / 0 瓶、剩余 46 HP；换手前缀后完整胜利 9 HP / 0 瓶、剩余 76 HP。玩家检查点当前源码续搜 21 HP / 0 瓶。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1948481061725-run`，检查点 `20260926T1950352880644-run`，最终 `20260926T2004345144992-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 7 包 `4eb25e79483c462089f9c6088d650c77`：`combat_start` 和玩家第二回合检查点严格恢复，continuation 及原生状态核对通过。VeryHigh / 180 秒 / DOP 8，同根修改前完整胜利 34 HP / 0 瓶、剩余 52 HP；新增延后能力成员后 8 HP / 0 瓶、剩余 78 HP，与玩家检查点当前源码续搜一致。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1935528900427-run`，检查点 `20260926T1937517416782-run`，最终 `20260926T1944101350112-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 6 包 `ebeacefdcc49421294a8d66a7535caa7`：`combat_start` 严格恢复。VeryHigh / 180 秒 / DOP 8，同根基线完整胜利 47 HP / 1 瓶格挡药水、剩余 23 HP；取消格挡药水插入后的审计截断后，完整胜利 26 HP / 1 瓶迅捷药水、剩余 44 HP。报告所指玩家检查点 `:3` 没有状态材料，旧人工 4 HP / 2 瓶只作投影参考。有效结果 `.local/strategy-sessions/worldline-20260925/requests/20260926T1902005233467-run`；后续无收益组合实验已撤回，未跑哨兵或 Linux 门禁。
- 前 150 第 5 包 `a9d1a29a2f8b49879a0f2a3f9ad1761a`：开战根和玩家第二回合检查点严格恢复，后者原生状态核对通过。VeryHigh / 180 秒 / DOP 8，默认策略修改前 0 HP / 4 瓶、剩余 74 HP；仅允许无色药水的诊断对照为 16 HP / 1 瓶；最终默认策略为 16 HP / 1 瓶、剩余 58 HP，完整胜利，追平旧人工 16 HP / 1 瓶。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1817332465305-run`，单药对照 `20260926T1826260714679-run`，最终请求 `20260926T1840485624137-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 3 包 `0edb8da283cf4ca1a543de9ffbc8dcbb`：开战根与玩家两处检查点严格恢复；玩家首回合录制事件重放通过。VeryHigh / 180 秒 / DOP 8，同根基线 50 HP / 0 瓶，目标、能力与防御后验后 22 HP / 0 瓶，完整胜利；玩家第二回合后检查点当前源码续搜为整场 18 HP / 0 瓶。最终请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1804375711652-run`。未跑哨兵或 Linux 门禁。
- 第 68 包 `493782770ca44fcaa63560ec98d130f0`：`combat_start` 严格恢复；修改前固定前缀尝试继续 `EndsPlayerTurn=True` 的虚空形态，初始搜索失败。过滤进攻及手牌整理的不可继续动作后，VeryHigh / 180 秒 / DOP 8 从开战根完整获胜，0 HP / 0 瓶、剩余 69 HP，证据 `.local/strategy-sessions/worldline-20260925/requests/20260926T1530557597261-run`。玩家第二回合检查点 `:3` 原生事件重放失败，原因是本地选牌 ID 13 与录制 ID 1 不符，不能作为当前源码续搜对照。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 66 包 `c683e82c46d84c719c8a19ddeed614db`：`combat_start` 严格恢复并搜索完整胜利，VeryHigh / 180 秒 / DOP 8 修改前后均为 4 HP / 0 瓶、剩余 22 HP。玩家第三回合检查点 `:5` 严格恢复；修改前在 `BuildOpeningHandSetupActions` 为先抽后弃的 `NEUTRALIZE+1` 估值时失败，修改后完整搜索为整场 4 HP / 0 瓶、剩余 22 HP。失败证据 `.local/strategy-sessions/worldline-20260925/requests/20260926T1456554816971-run`，修复后检查点 `20260926T1508000873100-run`、开战根 `20260926T1510447645464-run`。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 63 包 `73ab8686a86d41d3bf31230c739ffc7c`：`combat_start` 和玩家第二回合检查点严格恢复。VeryHigh / 180 秒 / DOP 8，同根基线 36 HP / 3 瓶、剩余 22 HP；最终路线 29 HP / 2 瓶、剩余 29 HP。玩家首回合用格挡药水并损失 2 HP，检查点后当前源码续搜再损失 27 HP / 1 瓶，整场同为 29 HP / 2 瓶。诊断确认首回合重放与玩家检查点只有弃牌堆中进攻、防御顺序不同；最终路线按玩家顺序出牌。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1332254526998-run`，检查点请求 `20260926T1333507208018-run`，最终请求 `20260926T1430189918175-run`。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 61 包 `481ecc46c597437d828ba2bf8ebabf79`：`combat_start` 严格恢复，补槽后的完整预测 continuation 与玩家检查点相同。VeryHigh / 180 秒 / DOP 8，原基线 40 HP / 0 瓶且死亡；来源成本规则下开战根 38 HP / 3 瓶、剩余 2 HP，药水要求 27 HP（迅捷 18、混沌 9、生成的能量药 0）；玩家检查点同进程续搜 27 HP / 后续 2 瓶、剩余 13 HP，尚未追平，按用户要求暂跳过。最终开战根请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1316580403773-run`，检查点请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1259226152900-run`。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 59 包 `9daddd281e734097a8f8df32ef1026e5` 的 `combat_start` 与玩家第四回合检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前 47 HP / 1 瓶、剩余 1 HP；扩展双药梯度后为 37 HP / 2 瓶，持续伤害复制目标后验入选后为 5 HP / 2 瓶、剩余 43 HP。玩家检查点之前已用两瓶药水，当前源码续搜再用 0 瓶、22 HP、剩余 26 HP。最终请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1036269299230-run` 记录 `EARLIER_COPY_DELAYED_DAMAGE` 入选，战损 5 HP。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 58 包 `c8f249011b5544ecb3848069037bdf89` 的 `combat_start` 与玩家第四回合检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前完整胜利 27 HP / 0 瓶、剩余 14 HP；修改后 13 HP / 1 瓶力量药水、剩余 28 HP。玩家检查点当前源码续搜为 18 HP / 0 瓶、剩余 23 HP。最终请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1019088538109-run` 记录第二回合在非药水动作后合法使用力量药水。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 57 包 `7ac4366d9f374d9cb2e58fcf47688ba5` 的 `combat_start`、玩家首回合及第三回合后检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前 58 HP / 1 瓶且死亡；最终源码为 55 HP / 2 瓶、剩余 3 HP 且完整胜利。玩家第三回合后检查点当前源码续搜为 56 HP / 1 瓶、剩余 2 HP；战损更低但资源成本未追平。最终请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T0954431463851-run` 记录下一回合换序后验胜利并入选。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 56 包 `5dbdbf0185ee4e3b8ee393d10a35d68d` 的 `combat_start` 与玩家首回合后检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前完整胜利 42 HP / 0 瓶、剩余 33 HP；最终源码 25 HP / 0 瓶、剩余 50 HP，追平玩家检查点当前源码续搜。最终请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T0803330151506-run` 记录另一合法诅咒选项后验 28 HP 入选，缩短首回合前缀后 25 HP 入选。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 54 包 `1706dd3050a44182ba74ed1c842e1fe5` 的 `combat_start` 与玩家第四回合检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根修改前完整胜利为 13 HP / 2 瓶、剩余 2 HP；最终源码为 8 HP / 2 瓶、剩余 7 HP，追平检查点当前源码续搜。中途基线修正与前缀续搜先达到 9 HP，合法同类零费攻击补打后达到 8 HP；末次源码调整后目标结果再次通过。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 46 包 `eb78b8a887b841d884fdec9406ba7313` 的 `combat_start` 严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根完整胜利为 13 HP / 0 瓶、最终剩余 29 HP；玩家旧投影为 14 HP、录制未用药。玩家第 7 回合检查点恢复报 `native_state_mismatch:byte=569`，不能称旧人工路线严格核对。未改策略、未跑哨兵或 Linux 门禁。
- 第 45 包 `326fb1d060fc4e97a7f67e9c2466a995` 的 `combat_start` 与玩家后续检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8 开战根完整胜利为 20 HP / 1 瓶、最终剩余 60 HP；玩家路线录制了两次力量药水使用，从检查点当前源码续搜为 15 HP、最终剩余 65 HP。原始战损仍多 5 HP，但少用 1 瓶，按 9 HP / 瓶折算净省 4 HP。未改策略、未跑哨兵或 Linux 门禁。
- 第 44 包 `ff9b6165cddb4b57bc02b99a3d22b099` 的 `combat_start` 录制状态 continuation 对账通过，旧包原生二进制因模型编号映射缺失不可比较。VeryHigh / 180 秒 / DOP 8 最终源码同根完整胜利为 5 HP / 0 瓶、剩余 58 HP；首回合组合前缀合法性修复后不再因重复物理牌导致请求失败。玩家第二回合检查点的首个原生动作不匹配（录制应打出 `TORIC_TOUGHNESS`，当前回放进入敌方回合准备），人工旧投影 3 HP 未严格验证。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 43 包 `c6907e067c294192b614078f821331be` 的 `combat_start` 与玩家第二回合检查点严格恢复，continuation、原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根修改前 22 HP / 0 瓶、剩余 57 HP；有界零净费用前缀后完整获胜，17 HP / 0 瓶、剩余 62 HP。玩家第二回合检查点当前源码续搜亦为 17 HP / 0 瓶后续用药，仅作定位对照。最终源码同包复跑仍为 17 HP / 0 瓶，Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未跑哨兵或 Linux 门禁。
- 第 42 包 `3742f202cf4146b1a1543ba60c98f3c8` 预检有效，`combat_start` 严格恢复、continuation 与原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根完整获胜，3 HP / 0 瓶、最终剩余 61 HP；玩家旧投影为 3 HP 战损，用药未知。当前源码已追平战损，未修改策略；未跑哨兵或 Linux 门禁。
- 第 41 包 `f73f92c95c3145168ab7bfe96fc848a4` 预检有效，`combat_start` 严格恢复、continuation 与原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根完整获胜，0 HP / 0 瓶、最终剩余 66 HP；玩家旧投影为 0 HP 战损，用药未知。当前源码已追平战损，未修改策略；未跑哨兵或 Linux 门禁。
- 第 40 包 `384119c6bdea454a87bcd74d8574853a` 预检有效，`combat_start` 恢复通过；VeryHigh / 180 秒 / DOP 8 搜索达到上限，状态 `timeout`，没有当前战损结果。
- 第 39 包 `4b28d1e3575c425b96959fd6e1ca7018` 预检有效，但 `combat_start` 严格恢复在 `native_replay_events` 失败：第 9 个遗物为当前 `DEPRECATED_RELIC`，录制状态为 `ANCIENTAFFECTION-DEVOTED_SERE_TALON`。状态为 `restore_mismatch`，没有搜索或当前战损结果。
- 第 38 包 `358700198bb74b90b1942c2916bafb25` 预检有效，`combat_start` 恢复通过；VeryHigh / 180 秒 / DOP 8 搜索于 `assert_initial_solver_result` 阶段超时，记录 `exceeded_180_seconds_package_discarded`，没有当前战损结果。关联包 `5dacf918eb3e4bcd9be7b086dbda3a93` 同战斗会话，未重复运行。
- 第 37 包 `bd580e3209034bb294d77eda34eb8705` 的 `combat_start` 与玩家第 2、3 回合检查点严格恢复，continuation、原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根修改前 1 HP / 0 瓶、剩余 79 HP；修改后完整获胜，0 HP / 0 瓶、剩余 80 HP；玩家第 3 回合检查点当前源码续搜亦为 0 HP / 0 瓶、剩余 80 HP，仅作定位对照。仅修改比较器时仍为 1 HP；加入有界的跨回合防御候选后为 0 HP。Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未跑哨兵或 Linux 门禁。
- 第 36 包 `61a935eb01414ede9833448f1c659e1d` 的 `combat_start` 与玩家首回合后检查点 continuation 对账通过；旧包原生二进制状态不可比。VeryHigh / 180 秒 / DOP 8 同根修改前仅死亡路线，38 HP / 0 瓶；修改后完整获胜，30 HP / 1 瓶、剩余 8 HP，后验入选 `MAZALETHS_GIFT+MASTER_OF_STRATEGY+DISMANTLE`。玩家检查点当前源码续搜 36 HP / 0 瓶后续用药、剩余 2 HP，仅作定位对照。Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未跑哨兵或 Linux 门禁。
- 第 35 包 `9a7c143b932942d0a22ba19e1b074458` 的 `combat_start` 与玩家第 4 回合检查点严格恢复，continuation、原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根修改前 1 HP / 0 瓶、剩余 63 HP；修改后首回合边界复搜找到 0 HP / 0 瓶、剩余 64 HP，完整获胜。玩家第 4 回合检查点当前源码续搜亦为 0 HP / 0 瓶、剩余 64 HP，只作定位对照。Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未跑哨兵或 Linux 门禁。
- 第 34 包 `b0e5689561f6429c8a73766419436b8d` 的 `combat_start` 和玩家首回合后检查点严格恢复，continuation、原生状态对账通过。同根 VeryHigh / 180 秒 / DOP 8 修改前 26 HP / 0 瓶、剩余 38 HP，修改后 0 HP / 1 瓶、剩余 64 HP，完整获胜；后验日志中 `DEXTERITY_POTION+FOOTWORK+DEFEND_SILENT+CLOAK_AND_DAGGER` 前缀入选。玩家喝药后的旧求解投影为 7 HP / 1 瓶、剩余 57 HP；当前源码从玩家后续检查点续搜为 5 HP、剩余 59 HP，仅作定位对照。Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未跑哨兵或 Linux 门禁。
- 第 32 包 `080a80618c124d3c8b4a5dce883a1743` 的 `combat_start` 与玩家首回合后检查点严格恢复，continuation、原生状态均对账通过。VeryHigh / 180 秒 / DOP 8 同根改前仅死亡路线，60 HP / 0 瓶；改后完整获胜，52 HP / 2 瓶、剩余 8 HP。救援成员按首回合不同合法顺序复搜；入选 `SHRUG_IT_OFF+POMMEL_STRIKE+STRIKE_IRONCLAD`，第 3、10 回合分别用攻击药水、虚弱药水。玩家首回合后当前源码续搜为 56 HP / 2 瓶，只作定位对照。未跑哨兵或 Linux 门禁。
- 新批次第 26 包 `f74859853a4b49588770b6ee0f8a4a56` 的 `combat_start` 和玩家首回合后检查点严格恢复，continuation、原生状态均对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前 22 HP / 0 瓶，最终 8 HP / 1 瓶、8 回合、总展开 383,398；后验日志为 `VICIOUS+POTION_OF_BINDING+BRAND`，获胜且入选。当前源码从玩家首回合后检查点单独搜索为 8 HP，属于定位证据，不计入同根优化量。未跑哨兵或 Linux 门禁。
- 常驻会话在可用内存低于默认 4096+2048 MiB 准入条件时 `start` 排队；MemoryCleaner 退出码 0，清理前后可用内存 6307→6308 MiB，随后又降到 5790 MiB。`start --host-memory-mib 2560` 在同一个固定实例成功，PID 33156，随后第 26 包中途检查点请求完成搜索而非宿主准入失败；继续请求仍记录 VeryHigh / 180 秒 / DOP 8。该预留值不是游戏实际内存上限。
- 新批次第 24 包 `f88c625680e64a2c99a2ab8844abcbdd` 的 `combat_start` 及玩家用药后检查点严格恢复，continuation 与原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前 36 HP / 2 瓶，修改后 0 HP / 1 瓶。智能生成选项日志：妙计路线与秘密技法路线未获胜，炸弹路线获胜、0 HP、1 瓶并入选；该路线第 1 回合用无色药水选炸弹并打出。未跑哨兵或 Linux 门禁。
- 第 17 包 `52728767fb5a42db81311a634f74f802` 严格恢复 `combat_start`，continuation 与原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根改前 35 HP / 1 瓶，改后 15 HP / 1 瓶，8 回合，总展开 223,026；能力路线日志记录 `WISH+FEEL_NO_PAIN` 前缀、完整获胜 15 HP，最终入选。玩家记录为操作后旧求解器投影 15 HP / 1 瓶；只确认数值追平，未独立重放完整人工路线。本包逐个运行，未跑哨兵或 Linux 门禁。
- 第 9 包 `ab0b65295edd48bab2d3b5dd53cd14ba` 在载入 Loadout v0.5.8 与 BaseLib 的隔离游戏源中从 `combat_start` 完成搜索；continuation 对账通过，原生二进制因原包未记录旧模型编号映射而不可比较。VeryHigh / 180 秒 / DOP 8、两槽药水均禁用时，改前 38 HP / 0 瓶，最终源码 23 HP / 0 瓶、6 回合、总展开 212,448；首回合“武装 → 燃烧+”，后续完整路线与原包 23 HP / 0 瓶的旧求解投影同序。原包智能药水政策在最终源码下仍为 0 HP / 1 瓶。只验证本包的质量，不将其外推至其他升级牌或其他 Mod 组合；未跑哨兵或 Linux 门禁。
- 夜魇通用入口替换逐包卡名链后，`faa009d05a2f411b9fadb30d46709192` 从同一 `combat_start` 严格恢复并完成 VeryHigh / 180 秒 / DOP 8 搜索，最终 0 HP / 1 瓶、21 回合；路线第 1 回合夜魇复制灵动步法，第 2 回合打出三张。候选提名按手牌整理、可用药水及夜魇复制目标的类型／价值／可支付费用进行，终局仍按完整路线排序。能力、攻击和技能三类均有候选入口；其他复制目标的质量未实测。本次未跑哨兵或 Linux 门禁。
- 新一轮三份可运行世界线包逐个从 `combat_start` 严格恢复并运行 VeryHigh / 180 秒 / DOP 8：`faa009d05a2f411b9fadb30d46709192` 修改前 2 HP / 2 瓶，修改后 0 HP / 1 瓶，路线第 1 回合夜魇复制灵动步法、第 2 回合打出三张灵动步法；`cd3dd71f2bf640108eae6c9ac9521882` 当前 10 HP / 1 瓶，追平站点人工战损 10 HP；`96734908a96f47e6a2fde88dc92cce40` 当前 6 HP / 0 瓶，追平站点人工战损 6 HP。第四份 `ab0b65295edd48bab2d3b5dd53cd14ba` 当时的无头实例未载入 Loadout，在首个原生事件的 `loadout_summon_powers=empty` 字段对账失败；补齐环境后的结果见上一条。本轮未跑哨兵或 Linux 门禁；两份已追平包是在组合改动前测得，最终源码未对它们复测。最终源码 Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。
- 固定实例实测：旧启动墙钟约 120.5 秒、旧暖进程导包约 100.9 秒；旧快照计划每次遍历 4,918 文件、约 3.7 GB。改为固定副本后，首次建副本的启动约 51.5 秒；复用副本但仍跑测试战斗的启动 28.3 秒。去掉启动测试战斗后，`fixed-c` 启动 13.8 秒，监控窗口约 1.1 秒就绪。扩容包随后直接运行，PID 均为 5304，墙钟 26.3 秒，`search_completed`；请求证据目录未产生 `preflight.json`。这些是不同阶段的实测墙钟，不作为受控提速倍率。
- Windows 无游戏快照夹具通过：稳定资产复用哈希缓存、同大小同时间戳的 Mod 重建仍被识别、游戏资产改变能更新固定副本；实际启动日志记录 `UNATTENDED_SNAPSHOT_PATCH changed=2 removed=0`，暖请求记录 `UNATTENDED_REUSE_ONLY ... snapshot_scan=skipped`。轻量启动第一次因游戏管理器尚未初始化而失败，第二次因沿用战斗清理等待而失败；修正后上述固定实例与导包实测通过。Linux 入口已接入复用模式，本轮未运行 Linux 门禁。
- 扩容策略最终 DLL：报告 `b642c1ccc4074802a40e4abcc97396a9` 从严格恢复的 `combat_start` 以 VeryHigh / 180 秒 / DOP 8 搜索完成，预计战损 35、用药 3；旧源码同根为 46、用药 3。最终路线首回合两瓶敏捷药水、白噪声、生成的扩容，四个对应专搜成员均完整获胜，最优 35 战损。独立哨兵 `62707d0e24684e1c827bc7812d8b0878` 同配置完成，原成员 20 战损、0 药入选；新增白噪声生成能力成员 31–45 战损，均未入选。另一个普通战斗 `1cd90a005b0c4c6ca9c0042251ae0424` 完成 38 战损、0 药，但起手没有白噪声，只作旁证。各包单独运行，最终会话 `stop` 成功。Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未运行 Linux 门禁。
- 独立监控窗口验收：`monitor-accept` 启动后游戏 PID 34500，监控进程 PID 39212 有独立 WPF 标题和非零窗口句柄。`b642c1ccc4074802a40e4abcc97396a9` 搜索完成；切换 `7a0eb30ddd6247cf8bcc7efa83721af7` 时状态文件立即显示新报告 ID，并在搜索中更新阶段、15,250 ms、2,492 展开节点、72,676 条已查世界线、4,765.6 条/秒、138 前沿节点、当前最好预计战损 29 与用药 0。搜索中关闭监控窗口后 `status` 为游戏运行、监控关闭；该包仍在 PID 34500 完成，最终预计战损 14、用药 1。`stop` 成功并删除私有实例目录。
- 同包有窗／无窗各一次，均为 `VeryHigh`、180 秒、DOP 8，从 `b642...` 的 `combat_start` 搜索，战损 46、用药 3、展开 8,152、转移 39,691 完全一致。有窗：墙钟 89.135 秒、请求 11.832 秒、搜索总耗时 10.010 秒；无窗：墙钟 88.107 秒、请求 12.071 秒、搜索总耗时 10.232 秒。只有单对样本，时间差小于一次运行的自然波动证据范围，不宣称监控零开销或固定提速。
- 重新 `start` 默认开启监控后，`stop` 同时结束游戏 PID 37312 与监控进程，私有实例目录消失。最终源码 Windows Release 主项目、CheckpointTool 均 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`、`git diff --check` 通过。本轮未运行 Linux 门禁。

- Windows `scaffold-accept` 会话启动后，已下载的 `b642c1ccc4074802a40e4abcc97396a9` 在同一开战根搜索两次。首次脚本哈希 `059a8b23`、参数哈希 `14d3d4fc`、PID 36064、墙钟 136.2 秒；修改脚本和参数后哈希为 `c6c13b35` / `dcb10d68`，PID 仍为 36064，`reusedProcess=true`、墙钟 85.2 秒。两次均 `search_completed`、预计战损 46。墙钟包含准备和清理，不以两份样本宣称固定提速率。
- 同进程无脚本哨兵同包 `search_completed`、PID 36064、预计战损 46、墙钟 83.3 秒。错误 C# 脚本 1.1 秒内明确记为 `strategy_or_input_failed`，留下编译日志，未向游戏提交旧脚本结果。`stop` 结束 PID 并清理私有实例；第一次停止曾因清理顺序与启动器自身所有权标记冲突失败，修正为启动器停止、独立所有权校验清理后成功。仓库忽略目录中保留会话请求证据，451 份原 ZIP 未删除。
- Windows Release 主项目和 CheckpointTool 构建均 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；`git diff --check` 通过。Linux 脚本入口已同步，本轮未运行 Linux 门禁。180 秒超时分类未用真包等到上限，仅静态核对工具分支，不能称为实测通过。
- 本轮结束前精确覆盖本地游戏 `mods/CombatSolver` 的 manifest、Release DLL、Windows MemoryCleaner、许可证与第三方声明；未启动可见 Steam。
- 最终接线 `scaffold-final`：启动后 PID 25648，`b642c1ccc4074802a40e4abcc97396a9` 使用示例 C# 脚本和空参数搜索 `search_completed`，`reusedProcess=true`，墙钟 89.1 秒；开战检查点恢复通过。结果中的主 DLL／脚本／参数哈希与请求逐项一致，有效政策记录 `VeryHigh`、DOP 8、`softTimeBudgetMilliseconds=180000`。`stop` 成功，私有实例目录不存在。最终接线没有再重复做两版脚本 A/B；那项证据见上一条。
## 未发布：搜索热路径 CPU 复查（2026-09-26）

- 基于 PR #138 的最终生产 DLL，Linux `perf record` 在静默猎手精英固定预算根得到 84,345 个无丢样 CPU 样本；另对 Regent 首领生产预算根得到 3,107,070 个无丢样样本。采样只用于热点归因，不用于耗时 A/B。
- 单因素试验将 `ReplayAction` 的捕获委托改为直接异常守卫，12 个五角色固定根各 ABBA（48 次独立进程，High、DOP8、Coordinator/组合、Smart、Server GC、5000 节点、120 秒）全部 Passed、无时间边界；路线哈希、展开、转移、战损、分数逐根一致。分配中位数之和少 0.41%，墙钟中位数之和多 2.37%；试验已撤回，未修改当前生产行为。逐根口径见[性能报告](../performance/search-hotpath-allocation-20260925.md#后续-perf-cpu-复查2026-09-26)，[48 份逐次结果](../performance/search-hotpath-cpu-20260926-rejected-trial.json)可复算。
- 本批没有启动可见 Steam 会话，也没有把无头样本当作帧时间或玩家可感知提速证据。

## 0.46.4：战损路线筛选与 Loadout 兼容（2026-09-25）

- 本机最新独立战斗日志：`SEARCH_SETUP_FAILURE stage=combat_root_snapshot`，异常是 `PowerGiver summon powers are configured or this Loadout version is not verified`；`godot.log` 证实求解器 `0.46.4` 与 Loadout `v0.5.8` 均已加载。实际 `v0.5.8` 的召唤钩子和公开怪物能力计数读取，与保留的 `v0.5.6` 程序集反编译结果一致。
- 修改前用实际 Loadout `v0.5.8`、BaseLib 和隔离游戏源运行 `LOADOUT-EMPTY-ROOT` / `05c8f3d560324d2aa91baca0a8697ffd`，在根快照断言失败，错误为 `Loadout PowerGiver summon powers are not inactive`。中间版对同一场景的 `d571e61d9bbe496fa91379743c140df8` 报 `Passed`：真实订阅者加载、空配置捕获和 Fork 通过，5 秒固定预算内取得首回合一动作零战损胜利路线。测试脚本退出后清理首次遇到文件占用；原生进程退出后通过仓库的所有权校验清理函数删除该实例。
- 最终实现不再以 Loadout 清单版本判定：仅在忽略目录的隔离游戏源把真实 `v0.5.8` 程序集对应清单临时改为模拟的 `v0.5.9`，`LOADOUT-EMPTY-ROOT` / `dd278b11f3f74d63a194e207e4d512fa` Passed。真实订阅者加载、公开怪物能力计数为空、根捕获与 Fork 均通过，5 秒固定预算内取得首回合一动作零战损胜利路线。测试实例由启动器删除，清单已恢复 `v0.5.8`。这证明版本号变化不会单独拒绝；没有取得真实未来版程序集，也未运行非空怪物能力配置差分或可见 Steam 实机。
- 根证书与数值合同：`HEAL-BOUND-SAFE-ROOT` 铁甲战士 `502f0df041fd460d8355dd7fd8102c38`、含精神过载的亡灵契约师 `1aedd4e7daba44fcb81b92e530e6a6db` 均 Passed；带鲜血药水的 `HEAL-BOUND-UNKNOWN-ROOT` `fccb56555b6d41fb9541c85f31a1bc8a` Passed，确认退回完整缺血余量。三次无头实例均由启动器清理。
- 战斗路径：放血短搜 `HEAL-BOUND-SEARCH` `1df1846a042948229de58f3088e67e5b` Passed，3 回合零战损获胜；该根提前达到可接受战损，剪枝数为 0，不作为提速证据。高灾厄、5 HP、敌 1 HP 的 `HEAL-BOUND-DOOM-TIMING` `39db271e55834aa8bfc23ab2773750ce` Passed，仍能在玩家回合结束前获胜；首次尝试因测试参数要求同时给卡牌 ID 与标题而未进入行为断言，修正输入后通过。实例均已清理。
- 带鲜血药水的强制用药搜索夹具 `HEAL-BOUND-POTION-ROUTE` `428aad7ece014d2cb40cdc73e3f9410a` 未找到可执行的必用药路线，故没有取得该路线的行为证据；根证书退回宽松界已由上一项独立验证。尚未做同根 A/B、可见 Steam 帧时间或 GC 暂停测量。
- 最终行为源码的合并哨兵 `HEAL-BOUND-SAFE-ROOT` `50b1cdcc907f424d894010ad48cd7e2f` Passed：亡灵契约师手中有精神过载、玩家 5 HP／10 层灾厄、敌 1 HP，根证书与数值合同通过，搜索仍在玩家回合结束前完成零战损胜利；实例已清理。该源码的 Windows Release 开发构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=209`，`git diff --check` 通过。

## 0.46.3：搜索速度指标与状态行去噪（2026-09-24）

- Windows Release 构建 0 警告、0 错误；PowerShell 结构门禁 `tools\inspection\verify-refactor-boundaries.ps1` 校验通过（`REFACTOR_BOUNDARIES_OK search_files=208`）。
- `English.json` 447 项词条格式与参数占位符校验全部通过。
- 控制器会话与 UI 状态生命周期无头测试通过：`pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId QOL-CONTROLLER-STOP-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -CleanupInstanceOnExit` 执行 Passed，验证了世界线数字、速度读数（xx 条/s）与平滑缓动结算断言，临时测试实例已由启动器清理。未做可见 Steam 实机人工验收。

## 0.46.3：内存回收设置说明（2026-09-24）

- 设置页回收相关的 32 个中英文词条均已精确映射对齐，面向玩家的文案清晰直观、消除术语堆砌。Windows Release 构建 0 警告、0 错误，PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=208`，`RuntimeGcProfileChecks` 54 项检查全数通过，`git diff --check` 通过。
- 仅修改 UI 文本与状态显示结构，未修改底层 GC 策略；未启动可见 Steam，真实设置页实机排版由用户验收。

## 0.46.3：新鲜资源保路通道探测上限（2026-09-24）

- Windows Release 构建 0 警告、0 错误（`-p:CopyModOnBuild=false`，不写实机 Mod 目录）；PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=208`；`git diff --check` 通过。
- 旗舰根 `EQ-IRONCLAD-ELITE-00` 串行 8 次 ABBA（执行前固定 A B B A A B B A，两臂各 4 次，全部样本保留）：墙钟均值 12.448 → 10.488 s（−15.7%），两臂区间不重叠（9.711–11.191 对 11.769–13.005）；`standPatProbes` −41.2%、展开 −22.7%、转移 −27.7%、`forkCount` −27.2%、累计分配 −27.8%；8 次预计战损、分数与终止边界逐项相同（52 / 9999279964 / `None`）。
- 60 根 `coverage/corpora/equivalence/turn-start-60` 语料的上限扫描（VeryHigh / beam 135 / nodes 60000 / DOP1 / 60 s，workers 4）：8 名额三种预排与 32 档前缀均被否决（0～2 根存活/阵亡翻转、净战损 −45～+20），采用的 64 档在 58 可比根上 0 翻转、净战损 −7、更差 1 根（`EQ-DEFECT-ELITE-00` 0→2）、更好 2 根，探测 −13.7%、展开 −2.7%。`FULL-SILENT-ELITE-03` 两臂与 `FULL-DEFECT-ELITE-00` 候选臂为 `TimeLimit`，不计入判决。
- `tools/search/OfflineSearchHarness/compare_results.py` 逐字段对照基线臂与采用臂：60 根对齐、无缺根、6447 个非时间/非内存字段；`rootState` 与 `catalog` 差异 0，`route` 231 处/15 根，`continuations` 15 根，`solverMetrics` 非时间字段 399 处/34 根。结构化样本：[fresh-resource-standpat-probe-cap-20260924.json](../performance/fresh-resource-standpat-probe-cap-20260924.json)。
- 未执行：游戏内 `UnattendedTestRunner.StandPatProbes` 契约（双车道探测、注入异常传播、并行与串行等价）、玩家检查点批量回放、DOP>1 与组合（Coordinator/portfolio）路径、可见 Steam 帧时间与 GC 暂停、No-GC 区域行为。`tools/testing/checks/BeamRankSortChecks` 在未改动的 `main` 上即因 `Snapshot.PlayerDead` 报错，本轮未修改。
- 合并审查追加：PR #134 的 Windows Release 构建和结构门禁通过。`STAND-PAT-PROBE-BATCHES` 在默认小牌组未到达剪枝检查点；改用既有死灵药水输入后，PR head `aaf3ab0ce9124430a554535f232c2aa2` 与未改动 `main` `339d90af220949d8aa49fd8ed861c247` 均因同一 DOP1／DOP2 非时序计数差异失败，路线、评分、预计战损及边界相同。因此该合同未通过，失败不能归因于 PR #134；两次私有实例已清理。未由此取得 DOP>1 质量结论。

## 0.46.3：ServerGC 普通启动自动接入（2026-09-24）

- PR #133 两平台配置／真实 CLR 合同各 54 项通过；Windows 私有实例首次准备、下次激活及另一次恢复启动均 Passed，详见[结构化证据](../performance/server-gc-auto-startup-20260924.json)。本轮合并修正了空路径检查顺序，相关配置合同与 Release 构建另以最终合并源码为准。
- 自动配置默认无头及显式启动器跳过；正式 Steam 设置开关、工坊更新链路和云存档未验证。此前可选 profile 的性能取舍沿用 PR #132 的五根原生宿主证据，不把启动配置合同当作可见性能验收。

## 0.46.2：可选 ServerGC 启动配置（2026-09-24）

- PR #132 的 `RuntimeGcProfileChecks` 25 项纯值合同、跨平台启动器合同及五根十个原生宿主请求证据见[专项报告](../performance/server-gc-launch-profile-20260924.md)；本轮集成验证另列于下。
- 合入当前 `main` 后，Windows Release 构建 0 警告、0 错误，PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=208`，25 项 GC profile 纯值检查与 PowerShell 启动器合同通过。
- 默认模式 `PR131-SIDEBAR-PR132-UI` / `cdf2a4755c3a4ba4a183e5548445d826` Passed；ServerGC 模式 `PR132-SERVER-GC-UI` / `890a55679cc546b293d4c17c37900fe7` Passed。两次均穿过侧栏四项坐标断言、设置页和控制器生命周期，首回合结束；默认模式有效 NoGC 为 true，ServerGC 模式为 false，私有无头实例均被启动器清理。此项验证不含可见排版或长线性能对照。

## 0.46.2：策略侧栏展开方向（2026-09-24）

- 控制器会话合同新增四个 1920 宽视口的侧栏横坐标断言：右侧可放、主面板贴右缘改放左侧且不相交、两侧都放不下时分别贴右缘和左缘。

## 0.46.1：在线连接配置恢复（2026-09-24）

- 0.46.0 发布 worktree 缺少 `presence.props`、`showcase.props`，其 MSBuild 监控端点属性为空；主仓库私有配置存在且属性非空。0.46.0 发布 DLL 的监控和战斗展示连接元数据均不存在，线上监控容器正常运行。
- 新门禁拒绝缺少私有配置的发布构建，也拒绝 0.46.0 旧 DLL 缺失 `PresenceEndpoint`。从明确指定的私有配置目录执行 Windows Release 发布构建通过，0 警告、0 错误，产物通过两项连接元数据检查。
- `ONLINE-PRESENCE-CONTRACT` / `db7129b3f04b41f38a9d1307670c114a` Passed：默认与关闭设置、当前战斗标量快照、真实 HTTPS 连接及错误证书指纹拒绝通过；只向端点发送应返回 400 的空请求，不生成在线玩家记录。无头实例已清理。
- `git diff --check`、Bash 构建脚本语法与 PowerShell 脚本解析通过。未进行可见 Steam 在线状态验收；正式 0.46.1 构建将在最终提交后执行，不重复相同源码的行为场景。

## 0.46.0 定版验证范围（2026-09-23）

- 本次合并后只同步版本与发布文档，UI 行为源码沿用下列 0.46.0 无头路线场景与结构门禁证据；发布构建从最终提交执行，不重复相同输入的行为场景。

## 0.46.0：UI 视觉层级重构与排版布局优化（2026-09-23）

- Windows Release 构建通过，0 警告、0 错误。
- PowerShell 结构门禁 `tools\inspection\verify-refactor-boundaries.ps1` 校验通过，`REFACTOR_BOUNDARIES_OK search_files=208`。
- `git diff --check` 格式门禁通过，无空白行或悬挂空格。
- `ROUTE-ROW-REUSE` / `3f480bd1bd3c468a8c0d73799ea1486d`、`e242a10e56dd4ddb8526912435a0c3b8`、`78160d7af1d040f9918539fc22a74f6b`、`8b1cf2751de34bf3a772d3f224501704`、`e1b78931a6644d0683789afdca4fe5f9` 与 `5f37adf0fff947188e17323a4823c926` Passed：动作块构造、路线行复用、执行状态、语言往返等既有布局与状态合同全部通过；已验证回合开始选牌胶囊专属色标与动画、循环组 `LoopBadge` 徽章随卡牌流式排版、消除下沉对齐与大框自适应贴合、循环结束胶囊淡化熄灭生命周期；已指定 `EvidenceDirectory`，无头实例由 `CleanupInstanceOnExit` 自动清理删除。
- `UI-LOCALIZATION`：在基线提交（0c5f677b）夹具生成怪物时即因 `ConditionalBranchState.GetNextState` 抛出 `No valid next state found`，无法在当前夹具环境完整通过，如实记录未标记为通过。
- 本轮只验证代码编译、结构门禁与无头交互合同；浅色、深色主题的可见画面排版与交互未进行 Steam 实机观感验收。

## 0.45.0 定版验证范围（2026-09-23）

- 本次仅同步版本与玩家更新日志，行为源码沿用下列 PR #130 Windows 集成和位置持久化成功证据；发布构建从最终提交执行，不重复相同行为场景。

## 求解器窗口位置持久化（2026-09-23）

- `UI-POSITION-PERSISTENCE-20260923` / `e9be7c39948343ecb6d1c5d886b1350e` Passed：无头原生战斗中检查位置写盘后重新加载、鼠标释放经输入桥保存，以及大面板临时挤压后恢复原位置；同场既有窗口缩放、折叠、设置和控制器生命周期合同通过，首回合战斗正常结束。引入位置回归断言后的初次运行 `6e8df725fd6d441a90fa272eb3f46baa` 在窗口持久化合同失败；修复后通过。两次实例均由启动器删除。
- Windows Release 构建 0 警告、0 错误。未进行可见 Steam 鼠标拖动和跨战斗视觉验收；无头结果只证明事件与设置文件、布局状态的合同。

## 搜索读数过渡动画（2026-09-23）

- Windows 集成：Release 构建 0 警告、0 错误，PowerShell 结构门禁 `search_files=208`。控制器会话 `b173172965a4434cb468b87502369aab` Passed；`UI-LOCALIZATION` 在原生 `PHROG_PARASITE_ELITE` 场景首次进入新增节奏合同后指出呼吸峰值断言时刻错误，修正断言后的 `a45dd7eae3274b208d36ebd555f0b19b` Passed，中英简繁的循环高亮、节奏和行复用合同均通过。实例由启动器删除；未做 Windows 可见观感验收。
- macOS Release 构建 0 警告、0 错误（RitsuLib 0.6.2 工坊引用）；Bash 结构门禁 `search_files=208`。
- 控制器会话合同 `AssertControllerSessionLifecycleAsync` 在读取世界线摘要和进度比例前先让读数收敛，继续核对“已查阅 42 条世界线”与 `0.05` 进度；PR 作者在 macOS 未运行该合同，Windows 集成结果见上。
- 追加已用时间走表与上传进度缓动后重新构建 0 警告、0 错误，结构门禁 `search_files=208`；测试收敛入口只做缓动、不推进走表，既有 `0.05` 进度断言不受走表影响。
- 部署高亮过渡：`UI-LOCALIZATION` 的循环高亮合同在比较颜色前先收敛过渡，并新增节奏合同（未知节奏与 `1.5 秒` 间隔取 `0.08 秒`、`0.2 秒` 间隔取 `0.04 秒`、`0.02 秒` 间隔直接切换、呼吸延迟内为 0、满幅峰值 >0.99）；行复用合同仍要求复用后立即全白。PR 作者在 macOS 未运行该合同；Windows 集成结果见上。
- 本地部署后 macOS headless 加载：本地 0.44.1 副本初始化成功，68 个补丁全部应用，工坊副本按设置跳过。缓动、呼吸与计数滚动的可见观感未进行 Steam 实机验收。
- 进度条取整修复后在 macOS 实机观察：搜索进度条、已用时间、世界线计数、内存条与执行高亮的过渡观感已确认。

## 0.44.1 定版验证范围（2026-09-23）

- 版本号与中英更新日志已同步；本次仅变更版本和文档。最终 Release 构建通过，0 警告、0 错误；行为验证沿用下列回合开始镜像、Power 施加差分和 Loadout 空配置实测结果，未重复运行。

## Loadout 空怪物能力配置（2026-09-23）

- 日志基线：本机最新战斗日志在根捕获拒绝 `Loadout.Services.PowerGiver.PowerGiverSummonHook`；Loadout `v0.5.6` 的公开实现显示其怪物能力计数非空时会在召唤及部分怪物阶段切换时施加 Power。当前跑局侧文件的 `monsterCounters` 和 `combatStartSnapshot.monsterCounters` 均为空。
- 使用隔离游戏源载入实际 `Loadout.dll/.pck`、BaseLib 与求解器，`LOADOUT-EMPTY-ROOT` / `c3f95d2453954479aa5ad790691a1c3a` Passed：断言真实订阅者已加载、公开计数快照为空、根状态戳和 Fork 均保留空配置。`LOADOUT-EMPTY-SEARCH` / `cc1a04534280423497ae2db5cc96939d` Passed：固定 5 秒预算的首个搜索得到 1 动作、零损、首回合胜利路线；两个无头实例均由启动器删除。
- Release 构建 0 警告、0 错误；结构门禁通过。未运行怪物能力计数非空的语义差分；该配置仍明确拒绝。

## 0.44.1：玩家回合开始三阶段镜像

- `TurnPhaseMirrorChecks --after-player-start` 原有 40 项，审计补充普通阶段生成 Late 监听者后为 41 项；`--after-player-start --vanilla` 1 项、`--after-player-start --seal` 3 项。覆盖三表登记拒绝、精确类型、冻结、三阶段监听顺序、轮间成员变动、卡牌 COW、选择暂停与未知覆写拒绝；已登记但入口尚无外部监听者时仍进入三轮派发。`--mask <生产 DLL>` 确认 61 个独立 bit。
- 主 DLL 与离线宿主 Release 0 警告 / 0 错误；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=208`。
- CoverageCatalog 原表两条上游旧证据状态无法解析；仅在隔离副本排除未引用记录后，3035 条通过，无未分类、缺失或非通过引用。生产源码与原证据表未改，详见 [检查摘要](../../../coverage/archive/equivalence/after-player-turn-start/validation.json)。
- 对照 `523aea57` 的 EQ 10 / FULL 40 / GA 10：60 对有效、无时间截断，6341 个确定性字段 `IDENTICAL`（1069/4212/1060），一次批次无补跑。口径 High 90 / nodes 250000 / 分支 48/28/36 / Coordinator / Smart / DOP 1，逐根数据与命令见 [等价证据](../../../coverage/archive/equivalence/after-player-turn-start/README.md)。

## 0.44.1：回合开始前镜像

- `TurnPhaseMirrorChecks --start` 22 项、`--start --seal` 1 项通过：精确类型、空/重复/抽象/未覆写拒绝、首次派发与首根冻结、Power/遗物/Modifier/卡牌混合顺序、参与者、选择暂停、监听者快照。原晚期回合末 25 项通过。
- 变基至 `6922828d` 后，主 DLL、宿主及检查工具 Release 均 0 警告 / 0 错误；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=208`。`--mask <生产 DLL>` 确认 58 个单 bit 互不重叠，BeforeSideTurnStart 不复用 AfterEnergyReset 的 bit 56。
- CoverageCatalog 原始表解析失败：上游两个未被 classifications 引用的 LOOP 证据使用未知枚举状态。仅在 archive 隔离副本排除两项后，3035 条核验通过、无未分类/缺失/非通过引用；没有改动原表。工具的 RitsuLib 分拆与 SmartFormat 引用只在本地构建补齐。
- `6922828d` 对照本入口：EQ 10 / FULL 40 / GA 10 一次完成，High 90 / nodes 250000 / 分支 48/28/36 / Coordinator / Smart / DOP 1；60 对均 Passed，6341 个确定性字段 `IDENTICAL`，无时间截断。两侧各一个离线宿主，600 秒软预算，未中断或补跑；输入、DLL 哈希、逐根摘要及复算命令见 [0.43.3 等价证据](../../../coverage/archive/equivalence/before-side-turn-start-0433/README.md)。 变基到 0.44.0（`42e09028`）后重跑 EQ 10 根：`compare_results.py` 1,069 字段 `IDENTICAL`；`TurnPhaseMirrorChecks --start` 22、`--start --seal` 1、`--mask` 58 个单 bit 互不重叠。
- 历史 `8be1410` 对照：5 角色 × 精英/首领共 10 根，High（90/50000）、Coordinator、Smart、DOP 1，992 个确定性字段 `IDENTICAL`。变基前的历史数字，原始产物未随分支保留；当前基线证据见下一条。

## 0.44.0 发布验证范围（2026-09-22）

- 本次定版仅变更版本与文档，复用下列集成、UI 本地化、左起编号及完整部署的成功证据；由最终提交执行一次 Release 构建并生成最小包，统一脚本记录三个渠道的发布结果。
- 未追加可见 Steam 验收或性能测试，不将已有无头结果扩展为可见效果或通用性能保证。

## 生成敌人编号与循环分组（2026-09-22）

- 左起编号修正：`UI-LOCALIZATION` / `3f56d808fea54349a12b4d92cc560427` 在原生 `PHROG_PARASITE_ELITE` 场景 Passed（26.40 秒）。eng/zhs/zht 下倒序创建四只扭动虫，编号逐项对齐原版 Marker2D 横坐标顺序；Fork 与移除已死敌人后标签保持。此项替代此前内部战斗 ID 后缀的显示约定，既有根敌人标签保持。
- `SPAWNED-LEFT-ORDER-DEPLOY` / `ac863c2c1bc1463b99986ce8c535bdc7` Passed（23.45 秒）：原生寄生虫场景，五张打击/五能量/100力量，先杀寄生虫再杀四只扭动虫；严格增量、Instant/0 完整执行，5动作零损T1获胜、0计划外重算。该最小场景经过目标生成与最终击杀注释的生产路径，不代表截图原局面的完整复现。
- 最终 Windows Release 0 警告、0 错误，PowerShell 结构门禁 `search_files=208`；两个实例均由启动器删除，未进行可见 Steam 验收。
- `UI-LOCALIZATION` / `d63691bed27a4f8faf9a84ba41a3ac9f` Passed（26.22 秒）：每种 eng/zhs/zht 语言下，从冻结根的模拟器创建四只扭动虫，核对标签各异、含战斗 ID、Creature 与击杀记录名称入口一致。新增显示身份合同核对物理手牌序号/阵容索引归一化、不同 CombatId 即使同名也分开、Search 原键仍区分物理手牌序号；既有高亮、宽窄布局与复用合同通过。
- 循环展示纯合同 125473 项通过，含两次重复直接展开、三次折叠、完整序列还原与动作索引唯一映射。Windows Release 0 警告、0 错误，PowerShell 结构门禁 `search_files=208`。
- 首次新增测试 `0aebe0e839d74f599900a260452212d7` 在测试准备中把主线程根捕获放进隔离域，被 Power 惰性物化保护拒绝；仅修正测试的捕获顺序后通过。两次实例均已清理。未获得截图原局面的完整路线日志，未进行该原局面逐动作复现或可见 Steam 验收。

## 紧凑循环动作组（2026-09-22）

- 虚线在上次位置基础上再下移 2 像素：仅调整绘制坐标，Windows Release 构建 0 警告、0 错误；未重跑行为测试，可见观感未验收。
- 虚线按实机截图下移 1 像素：仅修改绘制坐标，Windows Release 构建 0 警告、0 错误；未重跑行为测试，调整后的可见观感未验收。
- 蓝色虚线追加：Windows Release 0 警告、0 错误；`UI-LOCALIZATION` / `1299d057871f4c2aac47486b1297c7f1` Passed（26.36 秒），既有中英简繁、紧凑高度、宽窄往返、高亮与复用合同通过。源码仅在现有空间绘制下划线，未改变布局尺寸；未进行可见观感验收。实例 `loop-dashed-underline` 已由启动器删除。
- `UI-LOCALIZATION` / `1ccc7277cdba46279dc5c1e295f496b0` Passed（26.39 秒）：eng/zhs/zht 中 41 个动作折叠为一个双动作循环组与独立末击；次数位于动作右侧，宽布局贴合内容、组高仅增加外框留白、末击同排，窄布局内部换行且动作/次数均被外框包围，宽→窄→宽恢复通过。循环中间/末次/后缀高亮及行复用通过，既有完整本地化合同通过。
- Windows Release 构建 0 警告、0 错误，PowerShell 结构门禁 `search_files=208`。无头实例 `compact-loop-ui` 已由启动器删除；仅 UI 布局与显示变化，未重跑搜索语义或性能基准，未进行可见 Steam 观感验收。

## 能力驱动的 Power 施加不再继承外层卡牌来源（问题包 c4e28f3b）（2026-09-22）

- 本体 Release 构建通过（0 error）。
- 新增严格差分夹具 `LAMP-POWER-SOURCED-DEBUFF`：向战斗注入 `CorrosiveWavePower`，手牌给后空翻、
  并向**抽牌堆注入 2 张**保证抽牌真的发生（第一版夹具只清空牌堆，抽牌不发生、断言空过，已修正）。
  断言能力驱动的这层毒不按卡牌来源记账——不安油灯不触发、毒不被增幅。
- 改动前对照：本问题包的实机证据即修改前状态（预测毒 11／实机 5、油灯 1／0）。本次未在改动前的
  构建上重跑该夹具的反向对照：无头宿主当时被用户的可见游戏进程占用，未排队等待。
- 结构门禁 `tools/inspection/verify-refactor-boundaries.ps1` 通过；受影响的原版组合（腐蚀波 + 后空翻、吸取、
  手里剑/激怒/湮灭/撕裂/温柔等遗物与 Power 触发）走既有夹具与同一差分路径，未新增逐项夹具。

## 击杀后不再向已离场个体施加 Power（问题包 24b8f299）（2026-09-22）

- 本体 Release 构建通过（0 error）。
- 新增严格差分夹具 `LAMP-DEBUFF-ON-KILL`（不安油灯 + 中和打在会被这一击打死的目标上；
  `-EncounterId CULTISTS_NORMAL -CharacterId IRONCLAD`，需要两个敌人，否则一击杀就结束战斗）：
  - **改动前**（把 `CanReceivePredictedPowers` 还原成只看死亡阶段）**Failed**，错误逐字复现问题包：
    `Lamp kill mismatch: field=relicCounters expected={UNSETTLING_LAMP/1/0} actual={UNSETTLING_LAMP/0/0}`；
  - **改动后 Passed**，完成检查
    `LampDebuffOnKilledTarget:SkipsDebuffOnRemovedTarget:KeepsCharge:FullContinuationState`
    （预测与实机逐字比较完整 `ContinuationStamp`）。
- 哨兵（同一构建）：`LAMP-INDIRECT-POISON`、`LAMP-INDIRECT-TEMPORARY-STRENGTH`、`CRAB-RAGE-DEATH-TIMING` 通过。
- 结构门禁 `tools/inspection/verify-refactor-boundaries.ps1` 通过。
- **未建模**：实机里正在执行自己行动的怪物（`IsPerformingMove`）在死亡当时不离场，这个例外求解器不模拟
  （怪物行动不在预测范围内），代码注释已记明。

## PR #123 / #124 / #125 合并验证（2026-09-22）

- 行为基线为计算失败修复 `94254728` 加三个原 PR，合并提交 `0f7d6935`；两处计算失败生产修复文件与 `94254728` 完全一致。Windows Release 构建 0 警告、0 错误（`CopyModOnBuild=false`）；PowerShell 结构门禁 `search_files=208`、108 项组合合同、122505 项循环显示索引断言、12 项循环预算分类与 2 项比较上下文测试通过。
- 同一个无头进程启用 `COMBATSOLVER_VERIFY_FAST_LANES=1`，以下定向合同通过；这是合并组合的运行证据，作者的离线性能与大语料结果仍按各自原基线引用。

| 场景 | runId | 核对边界 |
| --- | --- | --- |
| ARSENAL-HAND-DRAW-SHUFFLE-CHOICE-REPLAY | `b70cc6575b474bbb8280fd3e0800466f` | 回合开始 Power 通知、选牌检查点、DOP1/DOP2、取消和异常 |
| ADJUSTED-ROUTE-INVALID-SUFFIX | `7a29239c6c5341e2a83b6eb1919f5cfc` | 失效手牌后缀、终局后缀舍弃与合法路线保留 |
| SEARCH-HP-TARGET-STOP | `39a66450ae2b4e3a94ec2d52ec788cc6` | 组合早停、治疗保护、成长、强制与智能用药、DOP2 |
| LOOP-HISTORY-DEPENDENCIES | `c8977ec522cd4660b143f4c085bd262e` | 三类牌堆历史键、未来生成读者、Fork 与并发共享额度 |
| LOOP-REPLAY-REQUEST-BUDGET | `515228c6cb8e4e94915bcc3676f101ec` | 两个真实 solver 分别消费 96/0 次回放、请求总额4096、前缀续搜、严格增量与 live 不变 |

- 反伤完整部署首次运行 `72d0e86119044d9bb515872f356f41b7` 在 NoGC 配置断言失败：搜索得到了零损 T1 胜利，生命周期统计有一次区域建立与结束，但断言时区域已结束，尚未完成部署验收。保留失败记录，未将其计为通过；本批无头实例 `pr123-125-integration` 已由启动器删除。
- 同一反伤夹具显式关闭 NoGC 后，`LOOP-DEFENSE-REPLAYED-THORNS-RESERVE` / `f35a94cf82c2456e8b161a8e2cf41bff` Passed：快速通道对账模式、严格增量、Instant/0 完整部署，3 动作、零损 T1 胜利、零计划外重算。独立实例 `pr123-125-deploy` 已由启动器删除。该结果证明本场战斗语义与部署，未解决或验收前述 NoGC 保留断言。
- 本轮未验证可见 Steam、性能收益或任意第三方补丁回退；未发包或更新本地游戏 Mod。

## 下一版本（开发中）：计算失败

- 未结清 Power：原玩家包 `716a294f…` 的 `search_request_AutoTurnStart` 检查点，修复前 `SearchOnly` 在第 5 回合的预抽牌前缀以 `STRENGTH_POWER:1` Failed（runId `3219ddc70d9e420e99b61a8762825b11`）；来源追踪确认为“军火库”在摸牌前生成牌时加力量。修复后同一包同一检查点 Passed（runId `ec5a5bc9f00749eeb1afdb7117fd759f`），搜索包括 2050 次回合前缀捕获与 1001 次复用。独立 `ARSENAL-HAND-DRAW-SHUFFLE-CHOICE-REPLAY` Passed（runId `38d1275a230f4b2e800a9838a0eb03fb`），覆盖生成牌、力量变更、预抽牌检查点、选牌兄弟分支及 DOP1/DOP2 等价；无头，不代表玩家战斗的可见部署。
- 插入路线：`ADJUSTED-ROUTE-INVALID-SUFFIX` Passed（runId `038c4e9ef71943f28559f3d91c6ea1e1`），同一真实模拟根分别验证计划手牌状态失效、致胜后仍有 EndTurn 的后缀被舍弃，合法致胜动作保留。终局原玩家包 `5b184b7d…` 在本机原生还原阶段因第三方模型的 `SavedProperty net ID 51` 与当前可用的 47 项不匹配而 Failed（非搜索断言）；未宣称原包搜索通过。20/9 两组的其他原包、可见 Steam 和长期路线质量未逐份复测。
- 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=206`；当前行为源码 Release 构建 0 警告、0 错误。各无人实例均已停止；本地游戏 Mods 目录的部署不等于可见 Steam 验收。

## 下一版本：热路径快速通道

- 等价性（High 90/50000、Coordinator、Smart、DOP 1）：EQ 10 + FULL 40 + GA 10 对未改动 0.43.2，`compare_results.py` 5,590 字段 `IDENTICAL`。变基到 `8826a333` 后加 5 个能力哨兵根共 65 根重跑：64 根 6,016 字段一致；`POWER-REGENT-ELITE` 在 600 秒批次预算下两臂各自撞上用药梯度的到期停止（机器降频，展开数 110000 对 106716），把预算放到 1,800,000 ms 单进程各跑两次后四次都停在节点上限，展开、转移、选牌分支、路线、分数逐字段一致。
- 校验模式 `COMBATSOLVER_VERIFY_FAST_LANES=1` 10 根零失败；负对照：故意改错 `AfterCardPlayed` 早期一趟的位图，校验 208 ms 内抛出并点名 `Kusarigama`，同一错位图不开校验时把 `EQ-IRONCLAD-ELITE-00` 从 `expanded=12190 / hpLost=52` 改成 `12785 / 74`。
- 计时（A B B A，每臂 4 次，机器空闲，单进程）：5 根跨根中位 −3.27%（−0.63% ~ −4.11%），REGENT、NECROBINDER、DEFECT 三根两臂完全分离；60 根分配总量 −0.41%，65 根批次 −0.56%（55/64 根下降）。批次并行时的墙钟受降频影响（同根同 DLL 满载 40 分钟后漂 20% 以上），不作计时依据。
- 只在离线宿主验证，未在游戏内验证；未测 DOP > 1；带第三方 mod 时位图关闭的回退只做了代码审查。

## 下一版本：默认组合再分配

- 默认组合再分配最终验收：新种子REALLOCATED-HOLDOUT35根/70进程，34对Comparable、1基线超时；2早结束/31同/1多损2 HP，无胜负翻转。Low/High两代表4对全部Comparable，Low防御多1回合，High消耗高压少7 HP。最终4根16次独立进程ABBA全部Comparable，同配置重复路线/质量/剪枝/工作完全一致；新接线与冻结双开关、关闭接线与冻结基线的代表根等价。108项组合合同、2组比较上下文、两端208结构边界、Release/宿主0警告错误通过。
- 原生 `CONTEXTUAL-REALLOCATED-DEPLOY` / `f51aa294fe2749d3b1f6b30e00e0fc8a` Passed，42.78秒：默认候选10 HP/0药/T9路线完整执行至胜利，0意外重算，Instant/0，实例清理；17742展开/75006转移。独立test没有战损改善，Regent+2 HP及ABBA托管采样均值+13.31%等代价如实保留；不外推Windows或可见性能。详见[完整再分配证据](../strategy/contextual-portfolio-reallocation-20260922-evidence.json)。

普通基线消融/有界进攻组合：开发32根均Comparable，战损/用药不变、结束回合1好/1差；组合保留训练代表消耗高压17→10 HP的离线收益，开发转移−4.73%、分配−4.11%，明确保留内存与发布延迟尾项。这是独立test前的开发阶段记录；最终独立测试、默认接受、原生和ABBA结果见本页顶部。宿主新元数据编译0/0、2组比较上下文检查通过；见[证据与协议](../strategy/contextual-portfolio-reallocation-20260922-evidence.json)。

已撤回条件窄成员替换原型：训练5根1好/4同；开发验证32根1好/31同，均未见时间截断，无胜负翻转，总转移−0.92%、分配+0.63%。原型115项组合合同、Release/宿主及两端208门禁通过；源码/合同已归档，不能把活动检查程序说成包含这些新合同。未运行新独立test、原生或ABBA；见[证据](../strategy/contextual-structural-refinement-20260922-evidence.json)。

后置结构探索实验（默认关闭）：5个训练代表1好/4同；validation32根实际预算观察1好/31同，无胜负翻转，其中8根新颖性时间截断，未截断24根1好/23同。原Beam成员工作/质量32根全部保持，唯一改善少1 HP；新增11项预算合同、12项停止原因分类检查、2项CLI拒绝通过，两端结构门禁208。没有新独立test、原生部署或ABBA验收；成本与尾项见[结构化证据](../strategy/contextual-adaptive-novelty-20260922-evidence.json)。

## 上下文排序与组合成本（2026-09-22，排序模型仍关闭）

- 单进展值同分截线实验：V1训练Evaluate20根5好/11同/4差、各1次胜负翻转；完整8代表2好/6同，但validation出现候选独有超时。仅基础分V3的validation32根2好/30同；冻结后test34可比根0好/32同/2差、无胜负翻转，另1基线超时并省略候选。实际战损+9、战略战损+15 HP，包含少回血与少用药的反例；默认关闭，不声明原生/ABBA通过。纯值生产方法合同覆盖单组排序、必保位置及旧旁路；最终默认/显式入口控制与构建/门禁见[结构化证据](../strategy/contextual-tactical-ties-20260922-evidence.json)。

- 有界追加进攻成员：五个完整训练请求1好/4同，32个validation可比请求0好/32同/0差（3对双方120秒超时排除），原成员的预算/准入/结果/展开/转移逐条保持。追加成员21次运行、11次达标跳过，validation无赢家；总转移+3.36%，默认关闭。合同106项、非法CLI组合5项、构建0/0、两端门禁208通过；不声明该排序原生或ABBA通过，test划分未使用。

- 窄进攻精炼替换宽成员：五个完整请求训练代表1好/4同，消耗高压17→10 HP；新种子validation35根32可比/3双方120秒超时，0实质改善/28同/4差，三根各+1 HP、一根0损多3回合，总转移−6.69%，拒绝默认启用。32根原普通主搜/窄成员工作与结果保持；两根关闭实验的完整路线/质量/工作保持。生产组合合同100项、四个非法组合、构建0/0、两端门禁208通过。独立2426节点探针仍找到10 HP见证，但有界追加尚未接入，不声明该候选原生或ABBA通过。

- 三项Beam敏感度140次训练测量全Comparable：敌方血量1.5倍在20根为8项实质改善/12同，但完整Coordinator五根仅少一回合且出现消耗高压胜转败，拒绝默认启用；没有为该候选声明原生/ABBA通过。20根默认结果与工作量保持；2根1倍扰动完整路线及工作量保持；9个非法/冲突参数拒绝，Release/宿主0/0，两端门禁208。见上下文实验记录及结构化敏感度证据。

- 组合目标早停初版35根：33可比/2双方120秒进程超时；31项实质相同、2个防御根多1/3回合，无胜负翻转/战损/回血差异，11根工作减少。7个定向治疗边界暴露少回血及延后卖血回血机会；加入主线程冻结的Heal变量/已有再生与实际回血保护后，11触发根质量/工作保持，7个边界与原基线完整政策和展开/转移相同。其余22个未触发重根未重新运行，不混称最终整批复测。
- 最终 `SEARCH-HP-TARGET-STOP` / `042d6ed60cdf47de9681a9c16e14ffeb` Passed，25.72秒：达标跳过、关闭开关保留审计、待抽治疗牌未实际回血时保留审计、DOP2、阈值3、成长/固定重放与混合用药合同通过，实例清理。此前未保护版runId `36c798602c1847709a2b3aebea302c1d` 只作为初版记录。最终Release/宿主0/0，两端结构门禁208通过；正式ABBA及默认配置完整部署见实验记录。

- 最终4根16次独立进程ABBA全部Comparable；两种收益根耗时−76.72%/−75.00%，防御根0损但T11→T13；未触发根耗时约0%/+0.39%，采样托管峰值+12.04%/+19.56%、工作集+1.43%/+3.27%，明确保留这些内存尾项。重复质量和展开/转移一致。
- 默认配置原生完整执行 `CONTEXTUAL-TARGET-STOP-DEPLOY` / `6dfaaebbb5784a0c896fe88c8136c288` Passed，29.85秒，预测0损路线执行至T6、0意外重算、结束HP下限65，Instant/0，实例清理。没有Windows或可见Steam性能验收。

- 自生成 105 根；训练 35 根的 105 次排序观察中 101 Comparable、4 TimeLimited，全部进程成功；时间截断不进固定工作量质量/性能结论。20 个定向根采集 60 次，得到 94 个有实质政策差异的候选配对，13 个根贡献标签；未知被剪分支不标负样本。
- `RankingChecks`：514 组真实/边界特征的 Python/C# 对齐，连同模型版本、范围、截断与终局旁路共 14921 个断言通过。
- 未注入模型的候选 DLL，在 `exhaust_resources-train-high`、`target_order-train-low` 两根与冻结基线的根、完整路线和所有非时序 pruneCounters 相同。
- 第一个模型的 20 根复搜：原政策含尾分为 7 好/4 差/9 同；去旧 Score 尾键为 6 好/4 差/10 同。消耗牌高压根由胜转败，拒绝；未进入 validation/test 或原生部署验收。不得把这些数字表述为已完成优化或普遍不退化。
- 两端结构门禁通过（208 Search 文件），Python 工具编译检查通过；追加三根六次外层保路诊断无时间边界。
- 连续威胁排序：全部中途节点版本20根4好/1差，拒绝；仅新回合起点版本20根3好/0差/17同，验证集35根3好/3差/29同；最终test35根2好/3差/30同，含两次胜转败。105根中104可比，1根墙钟截断排除；完整Coordinator另测，不声称训练外零退化。两版源码均编译0/0，终局/转置政策保持，默认关闭。
- 新增无人预算参数：Release与宿主构建0/0，Bash/PowerShell参数语法与非法范围拒绝通过；原生 `CONTEXTUAL-BUDGET-OVERRIDES` / `0ded12cc74694c3d90ea8bdb8a0f8992` Passed，Beam24/20000实际注入，0 HP/T1完整执行，0意外重算，实例删除。宿主M1元数据核对真实请求种子通过。此项未启用实验排序。
- 完整Coordinator test35根：33Comparable、2TimeLimited排除，主要质量2好/1差/30同，无胜负翻转；默认Medium/组合开启3个代表主要质量全同。候选仍关闭，未作最终ABBA；全部工作量与反例见下述记录。
- 当前排序证据是离线测量与纯值合同，没有可见 Steam 或原生新排序行为验收；复现命令和后续结果见[实验记录](../strategy/contextual-ordering-20260922.md)。

## 循环外层胶囊（2026-09-21）

- `UI-LOCALIZATION` / `ffaa5652dcfe42e89eea9e7cbd10e789` Passed：eng/zhs/zht 的 41 动作显示为「外框含 2 动作 ×20」加独立末击；在真实 Godot 容器中宽→窄→宽，断言框内换行、标题及动作完整包围、宽度不超父流；周期首/中/末次执行高亮、后缀高亮、重复填充复用通过。
- `ROUTE-ROW-REUSE` / `254c96d24a6d45eb9031971398696611` Passed：完整显示身份、失败填充重试、语言往返及订阅清理。既有非循环 16 动作行的两组 64 次不变填充分别 0.2099 / 0.1784 ms、各 48 B；这只证明既有复用路径，不作为循环新建布局或可见帧率的数据。
- Release 0 警告/0 错误；两项均用 120 秒上限和 `--cleanup-instance-on-exit`，实例已删除。没有改动搜索/模拟/执行数组；每个循环仅多 2 个容器节点。尚未验收可见 Steam 排版。

```bash
./tools/testing/run-unattended-test.sh --scenario-id UI-LOCALIZATION --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id ROUTE-ROW-REUSE --evidence-directory "$PWD/.local/loop-group-row" --timeout-seconds 120 --cleanup-instance-on-exit
```

- PR #123 / 上游 8826a333 整合：Release 0/0、两端门禁 207；UI-LOCALIZATION `70dae8a331234fcbb6e3a51a40a089f1` 与必要格挡原生部署 `19f8f8ad583b4b029a5c559f759243fa` Passed，实例清理；cap / 多 solver 两组与 656a9608 完整路线、质量 Equivalent。

- [循环请求额度与历史依赖收尾](../performance/loop-final-20260921.md)：28 组（26 根）/23 完整同路线/5 改善；4 个最终原生场景 Passed（历史依赖、两 solver 共享额度、两种投影范围外伤害必要格挡）；严格增量与完整部署分别记录，实例全部清理。Python 10 项与两端结构门禁通过；ABBA 将时间切层组标为 Inconclusive，另列无时间切层的固定节点实验。
