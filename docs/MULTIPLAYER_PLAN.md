# CombatSolver 多人适配实施规划

日期：2026-09-28。规划基线：`72363308`，项目版本 `0.47.2`，原版源码调查版本 `0.111.0`。

状态（2026-09-29）：**P0 的虚拟 2／4 人与 ENet 2／4 端原生链路已通过；P1～P5 均有局部原生证据，但内容闭包、无求解器对端、可见 UI 与 Linux 实机等完成条件未齐。多人适配仍在开发，不能宣布完成。** 本文统一覆盖底层、原版内容建模、搜索、执行、界面和测试。按顺序完成 P0～P5，原先的 DS 分工与双分支协作方案已取消。

下文的阶段、规划场景编号和完成条件不能当作已通过证据。已通过的 P0 场景见第 0.4 节；每阶段只回填实际取得的结果。

## 0. 给接手窗口的执行指令

### 0.1 任务与工作方式

用户将本文交给接手窗口后，任务是**完成全部多人适配 P0～P5**，包括卡牌建模和必要脚手架。直接从当前 P0 原型继续；阶段完成后继续下一阶段，不停下来问“是否继续”。只有继续工作会真正偏离用户意图，或者需要用户提供无法自行取得的信息时才提问。

- 仓库为工作区内唯一的 `CombatSolver/`，实施分支已经建立为 `feat/multiplayer`。先确认实际仓库、分支和改动，再继续；保留无关工作，不重新克隆。
- 首先完整读取项目 `AGENTS.md` 和本文。各阶段只读取相应职责、源码与适用 skill；不用在每次上下文恢复后重复加载所有历史文档或整个仓库。
- 本文记录本轮用户已确定的目标。旧批次、历史审计或其他任务说明不改变本轮范围。
- 采用当前分支按阶段提交，由同一接手者完成。用户取消的是多人协作开发，不要另分 DS 任务或自行派生多个实现者。
- 完整实现必要功能，保留简洁的直接逻辑；禁止用“完善架构”扩大任务。具体禁止项见第 10 节，必须遵守。
- 每次上下文交接记录：当前阶段、最新提交、未提交文件、已通过证据、下一条具体工作。恢复后从断点继续，不重新调研或复跑已通过项目。
- 按用户需求和仓库规则完成本地部署；本批开发不自行提升版本、发包、创建标签或发布远端。

### 0.2 已有代码与实际证据

规划提交为 `cb99678f`，其后的 P0 原型与本文完善内容一起保存。接手时以实际 Git 提交和工作区为准。

| 位置 | 已写内容 | 证据与限制 |
|---|---|---|
| [UnattendedTestProtocol.cs](../src/Testing/UnattendedTestProtocol.cs) | 新增 `MultiplayerProbePath` 请求字段 | 编译通过；协议运行尚未验证 |
| [ScenarioBuilder.Multiplayer.cs](../src/Testing/UnattendedTestRunner.ScenarioBuilder.Multiplayer.cs) | 虚拟 2／4 人建局、ENet 大厅、原版启动、固定牌组和第一回合等待 | 编译通过；连接、建局、动作等待及清理均待运行 |
| [MultiplayerProbe.cs](../src/Testing/UnattendedTestRunner.MultiplayerProbe.cs) | 配置验证、大厅监听、各进程状态检查点、完整 RNG 对比 | P0 检查原生对端一致性，不是 P1 的完整模拟差分器 |
| [Executor.Multiplayer.cs](../src/Testing/UnattendedTestRunner.Executor.Multiplayer.cs) | 指定玩家依次打防御、打击、生存者并选弃牌，结束后等待第二回合 | 虚拟模式用确定选牌器；真实联机使用本地选牌器走远端同步，效果待实测 |
| [ScenarioBuilder.cs](../src/Testing/UnattendedTestRunner.ScenarioBuilder.cs)、[Executor.cs](../src/Testing/UnattendedTestRunner.Executor.cs) | 接入专用原型路径 | 默认单人请求不选择该路径，单人运行回归待做 |
| [run-unattended-test.ps1](../tools/run-unattended-test.ps1)、[run-unattended-test.sh](../tools/run-unattended-test.sh) | 接受多人原型输入文件参数 | Windows 参数尚未运行；Linux 参数亦未运行 |

本轮已经执行并通过一次最终原型构建：`dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false`，零警告、零错误。首次构建曾因选牌器只读属性和缺少命名空间失败，已修正。最后一次构建之后只完善文档，接手时若构建产物存在且行为源码未变化，直接使用该产物运行首个原生场景。

尚未做：启动任何 P0 游戏实例、虚拟多人动作验证、ENet 双进程、四人运行、内容闭包清单、单人运行基线、结构门禁和本地部署。没有正在等待的测试进程，也没有需要延续的无头会话。Windows DLL 的文件版本元数据没有给出可靠的游戏语义版本；实际游戏版本核对在原型运行时使用 `NGame.GetGameVersion()`，不能把这项写成已经完成。

### 0.4 接手后的 P0 实际进展（2026-09-28）

上面的 0.2～0.3 节保留交接时的基线与调用说明，当前证据以本节为准。虚拟双人、虚拟四人均以原版 `0.111.0` 运行，逐玩家完成防御、打击、生存者的原生弃牌选择，进入第二回合；这是单进程动作链路证据。双进程和四进程 ENet 房主／客户端分别操作自己的玩家，完成同一动作序列与回合结束；每个稳定检查点的 `NetFullCombatState`、玩家阶段及九条完整 RNG 在所有端一致。受影响单人基线在原版建局后取得首个短搜结果。以上均未验证生产多人执行。

| 输入与源码 | 实际命令／层级 | 结果及证据 |
|---|---|---|
| `3917f0fa` 原型，虚拟双人 | `run-unattended-test.ps1 -ScenarioId MULTIPLAYER-P0 -MultiplayerProbePath <virtual,2> -CleanupInstanceOnExit`，原生一回合 | Passed；`.local/multiplayer-p0/virtual-2-7eaaef0646a4473dbd4809723abb41ec/peer-0/` |
| 同一原型，虚拟四人 | 同入口，`playerCount=4`，原生一回合 | Passed；`.local/multiplayer-p0/virtual-4-8c7ae052fc30497dac3d70959d98e92f/peer-0/` |
| 修复本地选牌同步和客户端动作等待后的源码，双进程 ENet | 两个独立 `run-unattended-test.ps1`，`host/client`、`seat=0/1`、共享协调目录及端口；各自 `HeadlessExecutionMode=parallel`、`HeadlessMemoryReservationMiB=1536`、`CleanupInstanceOnExit` | 双端 Passed；`.local/multiplayer-p0/enet-2-31a2877191774eb698d783c71e6e4666/peer-0/`、`peer-1/`；实测 DLL SHA-256 为 `DB2D8910ADC9B9655F9B83CBE98AF167E84520A073D906708110D47915EFCAD2` |
| 同一 DLL，单人原生建局与首个短搜 | `-ScenarioId MULTIPLAYER-P0-SINGLEPLAYER -PreserveNativeCombatStateForTest -StopAfterInitialSolverResultAssertion -ShortSearchBudgetOverrideMilliseconds 1000 -DeepSearchBudgetOverrideMilliseconds 1000 -ForceShortSearchOnly -CleanupInstanceOnExit` | Passed；`.local/multiplayer-p0/singleplayer-71d7b4fc39ab49db99c30d74f10b9156/` |
| 双进程最小编排入口 | `pwsh -NoProfile -File tools/run-multiplayer-p0-enet.ps1 -Port 33771`，生成双端输入并调用上述原生请求 | Passed；`.local/multiplayer-p0/enet-2-f51e6160ab0d42e6b4c05a10fa535ead/`；Bash 对应入口 `tools/run-multiplayer-p0-enet.sh` 仅通过 `bash -n`，未运行游戏 |
| 四进程 ENet 房主和三名加入者 | `pwsh -NoProfile -File tools/run-multiplayer-p0-enet.ps1 -PlayerCount 4 -Port 33773`，四端脚本化出牌／选牌／结束回合，全部检查点逐端对账 | 四端 Passed，各 18 条检查；`.local/multiplayer-p0/enet-4-b8912c11b53b4f86847df214c6d328f5/peer-0/result.json` 至 `peer-3/result.json`；实测 DLL `E7F8D91BD5F65093CB8ED907744A161D4E3350F4B643F6EC51AF4A1D77E809BA` |

ENet 初次失败定位为测试 `LocalSelector` 没有发送原版 `SyncLocalChoice`，远端等待弃牌；修复后两端弃牌检查点一致。第二次停在客户端自己请求的动作：原版客户端只发送入队请求，随后执行的是重建动作，原请求对象的 `CompletionTask` 不会完成；测试器现等待原生状态和队列稳定。两次失败是脚手架问题，不作为生产多人功能证据。默认 4096 MiB 预留曾使四人虚拟请求停在主机资源准入；按实测约 1.3 GiB 工作集使用 2048 MiB 预留后通过，场景超时未提高。上述成功实例均由启动器报告删除。

四进程 ENet 首次因无头实例调度器固定至多两个并行而失败；房主和一个加入者准入超时，已启动的客户端等待超时，记录在 `.local/multiplayer-p0/enet-4-85188e0ed1ba4801a307b5aee773ee89/`。将并行上限改为四个，仍受 CPU、可用内存、预留内存和排他实例约束后，按上表通过。Windows 结构门禁此前 `REFACTOR_BOUNDARIES_OK search_files=238`；Bash 入口已同步四人参数与调度上限，Linux 尚未运行游戏。P0 内容闭包、本地部署和 P1～P5 退出条件仍未完成。生产多人门禁保持原状。

### 0.5 P1 通用状态的本轮实证（2026-09-28）

沿原版 `0.111.0` 虚拟场景增加可选根投影、普通动作差分、固定 EndTurn 差分。根捕获先暴露 `MadScienceGrowth` 的 `Players.Single()` 单人假设；修为按本地身份取成长机会后，双人根投影通过。首张防御预测暴露 `MultiplayerScalingModel` 显式拒绝多人；按原版敌方格挡条件与倍率实现后，两人各自防御／打击的完整 `ContinuationStamp` 差分通过。跨回合首先检出队友回合号未递增；按原版参与玩家集合扩展结束、切边与开始处理后，双人及四人默认牌组的固定 EndTurn 差分通过。

| 行为源码及输入 | 运行层级与结果 | 证据 |
|---|---|---|
| 根捕获修复后，虚拟双人 `verifyRootProjection=true` | 原版建局投影 Passed；不包括动作差分 | `.local/multiplayer-p1/root-2-93861d8dca1741f8bdd1d9753aba4567/peer-0/result.json` |
| 普通动作差分修复后，虚拟双人 `verifyActionDifferential=true` | 两名玩家各一张防御／打击逐字段对账 Passed | `.local/multiplayer-p1/ordinary-diff-2-9e32df7306254ea59a5d8cccd6713cd9/peer-0/result.json` |
| 该次实测 DLL `3360B56D9CA785383F1119F7DA33A2C513D426334A681217C4511B77DCBB6B25`，虚拟四人三个验证开关均开 | 四人各防御／打击与完整 RNG 对账；第二回合固定 EndTurn 对账；队友格挡、卡牌所有者、RNG 人工差异同时被续用戳和状态键检出；兄弟 Fork 保持根状态，全部 Passed | `.local/multiplayer-p1/final-4-9ae71f6086b24ead9abe252894479d9a/peer-0/result.json` |
| 同一 DLL 单人 `MULTIPLAYER-P0-SINGLEPLAYER`、1 秒短搜 | 原版首个搜索结果 Passed；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=238` | `.local/multiplayer-p1/single-sentinel-edc4ce17d5f642ea89dbe1b3c39b3498/result.json` |

失败的首因和原始结果分别位于 `.local/multiplayer-p1/root-2-4bb628f7e3b44dd7ad301a2bd022cca7/`、`ordinary-diff-2-420319584c054931871b96d892b8b812/`、`round-diff-2-82032daee2cf4701ac9769334f65e3f0/`。这些是按具体根因修复前的记录，均不是通过证据。随后用 `Plot`、`Coordinate`、`Fade` 构造队友跨回合能力，首次第二回合差分指出队友 HP 原生 76、预测 80。原版普通怪物攻击面向所有存活玩家，而模拟仅伤本地玩家；沿原版 `AttackCommand.FromMonster` 的多目标语义修复后，同一输入的即时出牌和第二回合完整状态／RNG 均 Passed：`.local/multiplayer-p1/power-round-fixed-ac3e4164274049fda0e7f2b3d8970463/peer-0/result.json`。这只覆盖该固定怪物及这三种能力，未覆盖其他跨玩家监听、额外回合、死亡／复活、跨玩家引用、队友选择及生产搜索／执行。原版多玩家通常把 `AfterSideTurnStart` 对全体参与者派发一次，当前模拟的玩家准备续接仍逐人调用；复杂监听器的调用顺序须在对应机制差分后收口。生产入口仍保留多人门禁，不能据此标为 P1 完成。成功实例由启动器报告清理，本地部署未做。

### 0.6 P2 首个通用机制批次（2026-09-28）

`PowerCmd.Apply` 仅在敌方新施加且能力声明 `ShouldScaleInMultiplayer` 时缩放；模拟沿同一条件调用原版能力倍率函数，`PlatingPower.AfterApplied` 的敌方递减值设为玩家数。虚拟四人原版差分依次施加并移除 `ArtifactPower`、`PlatingPower`、`SlipperyPower`、`SkittishPower`、`CurlUpPower`，每一步对完整续用状态与 RNG，均 Passed：`.local/multiplayer-p2/power-scaling-4-dec86567a8d04ddfb9cf15ef9ca0dcbe/peer-0/result.json`，实测 DLL `44504C3BB0B3BB2E4038D4EC29FD7A57E11A0117476672AF70A101D0E09930E3`，实例已清理。该批证明通用入口和三种特殊公式的代表，不等于 12 项能力的生命周期或全部原版内容完成。

虚拟双人 15 张多人专用卡按机制分三批，基础版与升级版各一次请求，出牌后逐张将所有玩家、敌人、牌堆与完整 RNG 的原生状态对模拟续用戳，六次请求均 Passed；输入、每批卡牌及证据目录见 [多人内容清单](MULTIPLAYER_CONTENT_INVENTORY.md#已通过的卡牌即时差分)。本批仅证明即时效果，`Plot` 等跨回合触发、死亡、伙伴、队友原生选择仍未验。Windows 结构门禁通过 `search_files=238`；更新门禁中根历史依赖的检查以匹配全部玩家卡牌，Bash 脚本仅通过 `bash -n`。P0 内容盘点与 P1 生命周期都未封闭，生产多人门禁仍在。

后续两批新增 `Outrage`、`GlimpseBeyond`、`Largesse`、`GangUp`、`Knockdown`、`TheBall` 的双人基础／升级即时差分，四次请求 Passed，证据详见同一[清单](MULTIPLAYER_CONTENT_INVENTORY.md#已通过的卡牌即时差分)。`Outrage` 的首次红灯显示原版 `GetTeammatesOf` 包含自己，修正后双方都获得生成牌；`TheBall` 首次红灯显示逐实例增伤遗漏，现同步私有累计值、续用戳和状态键。这些修复均按首因重测。累计 21／37 张已通过即时差分，但 `GangUp` 来源历史、`Knockdown` 后续增伤与 `TheBall` 再次打出尚未验证。

再按施加、球／保护关联和守护三批验 `BeaconOfHope`、`Cacophony`、`Concoct`、`Flanking`、`HammerTime`、`Hibernate`、`Intercept`、`Sneaky`、`Soulbound`、`TagTeam`、`Tank`、`Underworld`，基础／升级六次请求 Passed；证据详见同一[清单](MULTIPLAYER_CONTENT_INVENTORY.md#已通过的卡牌即时差分)。首因修复：霜球读取分支 `HibernatePower`；`CoveredPower.AfterApplied` 建立 `InterceptPower` 的保护者集合；`TagTeamPower` 原已有卡牌 spec，删除镜像中的重复施加；`TankPower.AfterApplied` 给其他存活玩家施 `GuardedPower`。累计 33／37 张仅具即时出牌差分，关联监听和四张未测卡仍待完成。

最后四张 `LegionOfBone`、`Midnight`、`ImitationLearning`、`Tutor` 的基础／升级即时差分也各 Passed，累计 37／37；证据详见同一[清单](MULTIPLAYER_CONTENT_INVENTORY.md#已通过的卡牌即时差分)。`Tutor` 的夹具让目标玩家从抽牌堆原生选择一张牌，预测把该目标牌堆作为选择来源并对最终状态／RNG；生产搜索尚不能评价队友未知选牌，遇到该候选明确失败，不把它写成搜索已支持。其他三张的伙伴重召、历史减费和能力复制仍需独立验。P2 的“全部原版内容”与 P1 生命周期仍未完成。

关联机制差分继续沿相同脚手架、不同必要输入运行：`BeaconOfHope` 格挡传播与 `Soulbound` 生成联动 Passed（`.local/multiplayer-p2/propagation-hooks-768aee0d51ac4a2f8a531dde8c34ebfb/peer-0/result.json`）；队友先打击后 `GangUp` 的来源历史增伤 Passed（`.local/multiplayer-p2/teammate-history-fixed-9b3e88b6703441888893c169596b798c/peer-0/result.json`）；`Concoct`、`Underworld`、`Flanking`、`Knockdown`、`TagTeam` 施加后队友打一张打击，攻击次数、伤害、毒、Doom、移除及 RNG 对账 Passed（`.local/multiplayer-p2/teammate-attack-hooks-204fde7085d0451db1147f3bab743d8c/peer-0/result.json`）。`Hibernate`、`Plot`、`Tank`、`Underworld` 组合推进到第二回合的所有玩家状态及 RNG Passed（`.local/multiplayer-p1/linked-round-0fba10cf57994a798d4d55853a91edf4/peer-0/result.json`）。这些是所列条件的实际证据，其他能力联动和回合分支仍未通过。

`Cacophony` 后打 `HuddleUp` 首次抽牌联动差分失败：模拟内部计数已递减，但能力动态变量仍为 33，原版为 29。改为逐次同步模拟能力实例的 `Cards` 动态变量后，常规抽牌与第二回合完整状态／RNG Passed（`.local/multiplayer-p2/cacophony-draw-fixed-5123bbe50c12457cbbbaff71a6f327e6/peer-0/result.json`）。再把原版能力计数置为 2，让群体抽牌跨过阈值，验证随机敌人伤害、归零重置及第二回合完整差分 Passed（`.local/multiplayer-p2/cacophony-threshold-18598c06bb234b70a3f212e57f7d0262/peer-0/result.json`）。这覆盖该抽牌机制，不代表其他监听已验。

`ImitationLearning` 指向队友后，队友原生打出 `Inflame` 触发复制并由本地持有者自动打出；首次差分只差能力剩余层数（预测 2、原版 1），原因是预测内部计数递减没有同步能力实例。修复后，双方力量各为 2、复制能力剩余 1 层，完整状态／RNG 与第二回合均 Passed：`.local/multiplayer-p2/imitation-trigger-fixed-0573378d566f4e2f9590ca523b4ce057/peer-0/result.json`。这是普通能力牌触发一次的证据，不覆盖非首张连打、选择型能力或多次耗尽。

`HammerTime` 后打普通牌 `TheSmith` 触发锻造，首次差分指出队友手牌缺少已锻造的 `SovereignBlade`。沿原版 `ForgeCmd.Forge` 后的 `AfterForge` 调用，在模拟锻造完成后给其他存活玩家同额锻造，且该联动不再次递归。双人原生／模拟全部牌堆、伤害值、完整状态及 RNG Passed：`.local/multiplayer-p2/hammer-forge-fixed-252559287acf463795c462620800ae6c/peer-0/result.json`。仅证明本地持有能力且由自己打 `TheSmith` 的触发。

跨玩家转移再验 `TheBall`：本地出牌后原版随机放入另一玩家抽牌堆，队友抽到并再次打出，同一牌实例的累计增伤、二次转移、全部玩家牌堆与完整 RNG 逐步差分 Passed：`.local/multiplayer-p2/ball-replay-42e35d8052c146ebba56f30befa826fe/peer-0/result.json`。`LegionOfBone` 群体召唤后的第二回合伙伴与各玩家状态差分也 Passed：`.local/multiplayer-p2/legion-round-f7a757a155b6423dbe5440fc287504b0/peer-0/result.json`。两项均为双人固定输入，死亡／复活分支尚未验。

P3 首个真实搜索探针在虚拟双人和四人的普通牌根各跑一次 3 秒单成员搜索，取得只含本地已持有牌的非空路线并完成原生脚本，均 Passed：`.local/multiplayer-p3/search-ordinary-2-e6ccd5056c3949a7af04a448dd3dbaa2/peer-0/result.json`、`.local/multiplayer-p3/search-ordinary-4-ce51792b5b924f7ba2a95c838a2cc2fa/peer-0/result.json`。加入 `BelieveInYou` 后第一次内容根搜索暴露 `AnyAlly` 候选目标被构造为 null；初版按存活玩家生成目标后，同一双人内容根搜索 Passed；后经用户指出并核对原版选人界面，`AnyAlly` 还必须排除出牌者，旧搜索目标合法性结论作废：`.local/multiplayer-p3/search-teammate-card-target-e56288e18a0e4c35bc3beb03a9b2c9f2/peer-0/result.json`。这些只证明有限搜索不崩溃且动作属于本地，不证明三类方案、纯支援余费、两回合深度、预算共享、生产执行或 UI。药水候选仍仅玩家自用。

四人 `BelieveInYou` 初版探针验证目标跨 Fork 固定、游戏 RNG 不变及内容差分，证据 `.local/multiplayer-p3/ally-search-legal-4-0a90f5ba2c8b4290acff8fabf087fd3f/peer-0/result.json`；该版本未断言排除出牌者。按原版 `NTargetManager` 修正后，同一四人根断言目标必为其他存活玩家、三个 Fork 固定、游戏 RNG 不变，且短搜与原生内容差分 Passed：`.local/multiplayer-p3/ally-other-player-4-6c6362c2b3a74cd6b19e6048b2a301d1/peer-0/result.json`。此合同尚未覆盖生产重评估与执行时保持目标，也没有验余费支援分类。

按产品边界对一瓶玩家目标 `StrengthPotion` 进行双人自用原生差分：本地持有者向自己用药，完整续用状态／RNG 与模拟一致，之后仍可给队友打 `BelieveInYou`，Passed：`.local/multiplayer-p2/self-potion-060b3eb9d3de4f8c958b1ab84c19a4d4/peer-0/result.json`。候选源码只枚举本地玩家自身；该样本不代表 51 种玩家目标药水逐瓶通过。

多人设置新增独立的回合深度与时间限制，默认 2 回合／3 秒；持久化值验证范围为深度 1..32、时间 0.1..600 秒。搜索的回合层循环消费该深度，策略快照将时间转成毫秒，单人继续沿用原设置。虚拟双人探针实际断言默认 2／3000、自定义 3／6000，且结果搜索层不超过配置，Passed：`.local/multiplayer-p3/horizon-policy-2-b5e9a8d5762c444d93de124b59b26158/peer-0/result.json`。这是策略与上限合同；设置 UI、三类方案和共享多成员预算尚未完成。

P3 目前在同一次 Beam 搜索的最终候选中按有效伤害、预计自身血量、已有持续收益估值分别选输出／防守／启动，并以当前回合动作及目标去重；只有防守或启动的主指标严格优于已选方案才增加该风格。虚拟双人普通根在无实际取舍时只得到输出一条（`.local/multiplayer-p3/styles-tradeoff-2-5ee6478b80064e97a44717505474681d/peer-0/result.json`）；限制为一回合的两张牌固定根分别验证打击与防御的预计血量取舍（`.local/multiplayer-p3/styles-defense-one-turn-fa5b3709c12a4b098ddd8007b70a335a/peer-0/result.json`），以及打击与 `Inflame` 的启动估值取舍（`.local/multiplayer-p3/styles-inflame-one-turn-0ae20320c4aa4aabbcde57b4c5da1e31/peer-0/result.json`），均 Passed。原 Beam 的单人短搜在新源码下 Passed：`.local/multiplayer-p3/single-style-sentinel-0a34b01dee054e79aa042ab31cda9a30/result.json`。

默认两回合的 `Inflame` 根首次只返回输出，因为原 Beam 的普通末端排名丢掉首回合启动路线。现从第一回合边界保留各风格代表，最后按缺失快照原序回放；启动和防守比较同一首回合边界的指标，再展示各自实际搜索深度。两回合 `Strike`／`Inflame` 的输出＋启动取舍 Passed（`.local/multiplayer-p3/styles-inflame-two-turn-241d392d8bc54345b25e8c20a16fe8e2/peer-0/result.json`）；`Strike`／`Defend` 的输出＋防守取舍 Passed（`.local/multiplayer-p3/styles-defense-two-turn-a0fa1d9e58af46f6917a3c2363aaf2f5/peer-0/result.json`）。这证明两个分别构造的风格差异，不证明同一根必须出现三条。纯支援余费、可见信息边界和生产入口仍未完成。

加入首回合代表后，再以普通双人固定根断言只有一条输出方案且原生动作脚本仍通过，Passed：`.local/multiplayer-p3/styles-ordinary-final-4585002569a6493d8d2b58c4256f549c/peer-0/result.json`。当前多方案只存在于搜索结果，生产控制器与界面尚未消费；搜索末端多个候选的回放耗时尚未单独纳入请求预算验收。

搜索协调器已给多人根接入一次直接 Beam 请求，跳过单人整场深化／药水审计，仍复用请求工作量账本。双人两回合 `Strike`／`Inflame` 根通过协调器取得输出＋启动路线，断言只记一场搜索、总展开量与本场相等、根比较戳一致，Passed：`.local/multiplayer-p3/coordinator-styles-two-turn-a4297d0afd3d43fba976a1ba76e9772a/peer-0/result.json`。这是生产搜索函数的原生进程调用；运行时多人 `CanSolve` 与 UI 入口仍关闭，未验证真实玩家点击。

P3 纯支援第一批按原版效果和目标身份分类：`BeaconOfHope`／`HammerTime` 及指定其他玩家的简单资源、能力、球、生成牌等进入余费补入；自身也受益的群体牌继续普通搜索；原版选人界面不允许 `AnyAlly` 指向出牌者。补入在本地路线形成后、首回合结束前按剩余预算逐张尝试，完整回放原动作序列，并拒绝本地血量、资源、有效伤害或预测边界变差的结果。双人 `Strike` 后余费补 `BeaconOfHope` 的整条原生路线／全状态／RNG Passed：`.local/multiplayer-p3/support-beacon-native-route-b64a4cf8934c411dbfe8b493ca49f212/peer-0/result.json`；余能不足时不补 Passed：`.local/multiplayer-p3/support-no-spare-1ca90c8f1dfd4067849d8746db22de95/peer-0/result.json`。`Strike` 后给队友 `Blaze` 的目标身份、整条原生路线／全状态／RNG 首次 Passed：`.local/multiplayer-p3/support-blaze-target-72cb58a9d9114b3284478df3396f5bec/peer-0/result.json`；排除 `AnyAlly` 自指后，同一机制复验 Passed：`.local/multiplayer-p3/support-blaze-other-player-cf876f7b22a84b29b53729e0d1e9e1bb/peer-0/result.json`。`Rally` 作为群体自身获益牌正常进入防守搜索 Passed（`.local/multiplayer-p3/group-rally-legal-18d19032cefd474f8461146a52c3050a/peer-0/result.json`）。此前以 `Blaze` 自指为条件的 `.local/multiplayer-p3/self-blaze-search-73cc11e6325d42e6922675c0db1e51bd/peer-0/result.json` 违反原版选人规则，作废，不作为通过证据。同一方案补两张支援牌的机制已通过；其他纯支援条件、零费额外成本、保留到未来的资源与队友未知选择尚未收口。

同一固定根中先 `Strike`、再依次补 `BeaconOfHope` 与指向队友的 `Blaze`，两张均消耗余能，完整路线与原版全状态／RNG Passed：`.local/multiplayer-p3/support-two-cards-ca2a44b61ca94328a437837241a5b660/peer-0/result.json`。本批的支援枚举仍以已核对的显式卡牌集合为限，其他条件式收益与所有零费成本组合尚待补齐。

针对 `AnyAlly` 误判核对原版 `NTargetManager`、15 张指定队友牌及 8 张 `AllAllies` 牌：手动 `AnyAlly` 目标排除出牌者，群体目标包含出牌者；直接自身收益的指定队友牌继续正常搜索。`Largesse` 的目标玩家是生成牌牌主，`AddGeneratedCardToCombat(..., base.Owner)` 里的 `base.Owner` 是创建者，早先清单写成给出牌者是错误的。双人 `Largesse` 短搜和原生出牌显式断言牌进入队友手牌、归队友所有，全状态／RNG Passed：`.local/multiplayer-p3/largesse-recipient-and-search-c6a5eb6ba3e64c159a0623d0a234e63c/peer-0/result.json`。为此修复多人历史计数器的按玩家读取；测试进程中显示名提前在主线程捕获。该探针不证明 `Largesse` 余费补入策略已通过。

补充验证 `Largesse` 的余费支援：双人固定根中本地先 `Strike`，再在不额外消耗能量的情况下把 `Largesse` 补给队友；整条路线按原版执行、所有玩家状态与 RNG 对齐，Passed：`.local/multiplayer-p3/largesse-spare-support-7a83bcf691ce4be98528ac869b670ff4/peer-0/result.json`。

P4 首次虚拟双人手动入口探针暴露战报结果记录使用 `Players.Single()`；改为本地玩家后，同一请求在原生战斗内取得可见悬浮窗与本地搜索结果并完成脚本，Passed：`.local/multiplayer-p4/controller-virtual-2-outcome-fix-119a18d17ad54ec38dcac49e76a3f1a6/peer-0/result.json`。这是控制器手动入口证据；该探针未覆盖方案选择、逐步执行和真实联机变化。

同一虚拟双人普通根再实际调用 `RequestDeploy`，执行计划内本地牌并结束本地回合，断言队友未准备结束且没有代出队友牌，Passed：`.local/multiplayer-p4/controller-deploy-virtual-2-e6cc81878d00486cad9ac8e0ebffa4fe/peer-0/result.json`。这只覆盖当前回合的普通牌部署；目标牌、原生队友选择、偏差与 ENet 尚待验证。

战损记录器原来在多人根只返回空的本地 HP／药水账本，已改为按本地玩家记录。双人虚拟原生请求先由队友自用 `StrengthPotion`，本地账本仍为零；再由本地玩家自用一瓶，账本恰为该一瓶，随后给队友打牌并完成全状态／RNG 差分，Passed：`.local/multiplayer-p4/local-potion-accounting-2c8ee81616f944cea0e52decb33179ea/peer-0/result.json`。这是测试队友自己的用药事件，不改变“求解器只给自己用药”的产品边界。

生产控制器在虚拟双人 `Strike`＋`Blaze` 根取得本地方案，按原生动作执行，`Blaze` 力量只落到另一名玩家，本地回合结束而队友仍可行动，Passed：`.local/multiplayer-p4/controller-blaze-deploy-99cc944e1a944ed5ba2ac2d6e950c229/peer-0/result.json`。

目标合法性与出牌资源已分离：模拟分支取得能量后，`Blaze` 在原生 live 根仍不可打，但搜索枚举的目标仍为其他存活玩家；三个 Fork 固定、游戏 RNG 不变，Passed：`.local/multiplayer-p4/ally-after-energy-750666e7869748c097f02a30b8f0b9e3/peer-0/result.json`。先前使用 `CardModel.CanPlayTargeting` 会把 live 能量误用于模拟分支，现目标枚举使用 `IsValidTarget`，出牌资源由模拟状态检查。

控制器真实 ENet 双端部署：两个进程分别手动求解、执行自己的一回合牌，并同步到第二回合，原版两端全状态／RNG 检查点一致，Passed：`.local/multiplayer-p4/enet-controller-2-ready-wait-2daef04b383044949b8ec3cc106b50b7/peer-0/result.json` 与 `peer-1/result.json`。首次探针把尚未传播的原生“准备结束”状态判为失败；等待该状态或进入下回合后通过。四人部署、交错变化、原生队友选择仍未验收。

界面已在现有性能页加入多人专用深度／时间输入，值仍用已验证的策略快照；已加入输出／防守／启动的候选按钮与实际选择。虚拟双人 `Inflame`／`Strike` 固定根出现输出与启动两条不同动作路线，悬浮窗两按钮实际点击启动后控制器切换当前方案，Passed：`.local/multiplayer-p4/overlay-style-selection-332cc73ae73f4bc8af5225dd5b725613/peer-0/result.json`。这是输出／启动按钮的无头交互证据；该轮未人工检查可见窗口，也未验证设置输入保存与防守按钮。

多人设置输入经 `UI-LOCALIZATION` 原生无头场景实际构造，并在 eng／zhs／zht 三种语言下检查控件属于性能页且显示当前设置值；468 条英文目录模板占位符对账 Passed：`.local/multiplayer-p5/ui-localization-phrog-3569a408218a4b3caeeba6478db72f82/result.json`。首次误用默认遭遇时，测试在怪物生成阶段失败，未进入新控件断言；按仓库已有记录改为 `PHROG_PARASITE_ELITE` 后通过。尚未在可见窗口人工检查排版，也未模拟编辑保存。

双人 `Defend`／`Strike` 固定根在现有悬浮窗生成输出与防守两按钮，点击防守后当前动作路线切换，并显示“队友后续不主动出牌；仅安排自己的动作”的条件预测，Passed：`.local/multiplayer-p5/overlay-defense-selection-e074cd147626491dab30e2c4f83a93bc/peer-0/result.json`。与上述输出／启动测试合起来覆盖三类按钮各自的选取入口；还未在可见游戏窗口人工检查观感。

将多人按钮数据限制为不可变的界面快照后，结构边界门禁 `REFACTOR_BOUNDARIES_OK search_files=239`、Release 构建 0 警告／0 错误。随后双人 `Inflame`／`Strike` 根点击启动并实际调用原生部署，断言只出 `Inflame` 而未出输出方案的 `Strike`，Passed：`.local/multiplayer-p5/overlay-setup-deploy-24cde210264443d3bb8d5ffadf2b02bd/peer-0/result.json`。这证明界面所选方案进入执行器；防守路线执行与可见窗口观感仍未验。

P4 队友插入动作的首轮虚拟双人探针：本地求解后，队友原生打出 `Strike`，原控制器因状态戳变化启动了一次完整新搜索，失败证据 `.local/multiplayer-p4/teammate-drift-baseline-a9b160ce4b634e63ab86d16f36ecfc6d/peer-0/result.json`。现改为对已选本地当前回合动作使用现有回放入口，在新根上重算预览；动作仍合法时直接原生部署原序列。相同输入复验 Passed，且无额外完整搜索、本地牌均出手、本地回合结束：`.local/multiplayer-p4/teammate-drift-replay-f3b8a015470948b288908279a983f65c/peer-0/result.json`。Release 构建 0 警告／0 错误。该证据仅覆盖部署前的虚拟双人队友伤害；失效动作暂停、部署中变化、随机流、原生选牌、四人及 ENet 交错尚未通过。

同一变化暴露执行按钮仍按旧状态戳禁用。现在多人同一本地回合保留按钮，由点击后的路线回放决定是否可执行；原生虚拟双人探针在队友 `Strike` 后刷新按钮、确认可点击、实际点击并部署原路线，搜索次数在部署前后均不增加，Passed：`.local/multiplayer-p4/teammate-drift-button-3e1fff5d517d4bdcac1ddb11ec56612c/peer-0/result.json`。这是无头界面交互证据，可见窗口排版未验。

失效分支另用双敌 `CULTISTS_NORMAL`：本地原路线瞄准第一只敌人，队友原生 `Strike` 将其击杀，第二只仍存活。首次直接回放抛出 `CardPlay has no target creature`，原因是固定前缀合法性检查只验手牌与资源，未验目标身份已经消失；失败证据 `.local/multiplayer-p4/teammate-kill-direct-bae6ed6cc99449829187eddbe9da8f3f/peer-0/result.json`。现在回放前检查原目标仍可解析，失效时控制器暂停，未打本地牌、未启动完整新搜索；同一机制复验 Passed：`.local/multiplayer-p4/teammate-kill-guard-c93271858d4f4520a0407264a71d7666/peer-0/result.json`。该证据通过控制器直接请求，不覆盖按钮点击的失效分支。之前的按钮版探针因同步断言先见到旧摘要而失败，不计作该分支通过。

受影响单人固定前缀哨兵 `FIXED-PREFIX-TURN-OUTCOMES` 在原生 0.111.0 跑到第四回合，三回合续用、独立前缀 oracle 与状态文本断言 Passed：`.local/multiplayer-p4/fixed-prefix-single-b9bd85d57578452bae95938e6caf0ce3/result.json`。这覆盖本次固定前缀合法性改动的单人路径；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=239`、Release 0 警告／0 错误。

部署中的队友交错已加入逐动作新根回放，仍执行原本地余下序列，失效则在下一动作前暂停。双敌 `CULTISTS_NORMAL` 中本地第一张 `Strike` 后，队友在部署延迟内击杀第二张牌原目标，控制器只执行第一张并暂停、未完整重搜，Passed：`.local/multiplayer-p4/mid-deploy-kill-17962d61dd5d47eab915f6657275e2bd/peer-0/result.json`。同一机制改为队友攻击另一只敌人，两个本地 `Strike` 按原目标完成、本地回合结束、未完整重搜，Passed：`.local/multiplayer-p4/mid-deploy-legal-e58f2483bb964daf87188a8f014b1888/peer-0/result.json`。两项都是虚拟双人；最后一张牌后到结束本地回合、四人和 ENet 交错尚未通过。

逐动作回放已复用到最后一张牌后的原生结束回合前，重算剩余 `EndTurn` 的数值，并用于原有全自动风险对比。合法交错样本在该修改后仍完成两张本地攻击与回合结束，Passed：`.local/multiplayer-p4/mid-deploy-endturn-replay-5c8330980cb8443385df9ca5e0a31570/peer-0/result.json`。该样本的队友动作发生在两张本地牌之间，尚未单独覆盖最后一张牌后的队友动作。

队友动作使旧状态戳失效时，悬浮窗立即把路线数值标为待更新，隐藏旧摘要数值，执行按钮仍可进入重评估；在稳定新根重评估后再显示更新预览。虚拟双人原生 `Strike` 交错后，界面待更新标题和可用按钮断言、点击后的本地部署均 Passed：`.local/multiplayer-p5/stale-hint-f44cce3493a04f49b1908ed78dee8905/peer-0/result.json`。该证据为无头 UI 控件状态，未人工检查可见窗口；RNG 偏差专门提示尚未实现。

普通内容的多人生成池开始按已盘点机制验收：`InfernalBlade`、`JackOfAllTrades`、`Metamorphosis` 的双人基础／升级即时全状态和完整 RNG 差分 Passed，具体输入与界限见[内容清单](MULTIPLAYER_CONTENT_INVENTORY.md#普通内容的多人差异扫描)。其余普通生成、共享随机目标、怪物与遗物分支仍待逐项建模／差分。

第二批普通生成池 `BundleOfJoy`、`Distraction`、`WhiteNoise` 的双人基础／升级即时全状态和完整 RNG 差分也 Passed，证据及限制见同一清单。复核原版发现清单把 `Fasten` 的悬停说明选牌错列为战斗生成分支，已从该类移除；它的 `OnPlay` 只给自身施加 Power，当前只是源码分类修正，未记为本轮差分通过。

第三批普通生成池 `Jackpot`、`ManifestAuthority` 的双人基础／升级即时全状态和完整 RNG 差分 Passed，证据及限制见同一清单。

普通生成池余下 `Discovery`、`Abundance`、`Quasar`、`Splash` 的指定生成选项，`Stoke` 的三张手牌消耗／补牌，`MadScience` 的 Skill／Chaos 组合，均取得双人基础／升级即时全状态和完整 RNG 差分。14 张结算中使用多人过滤池的普通牌至此都有一组基础／升级即时证据；随机结果全集、其他选择、生成牌后续使用仍未因此验收。详见[内容清单](MULTIPLAYER_CONTENT_INVENTORY.md#普通内容的多人差异扫描)。

普通共享敌人目标三张 `Omnislice`、`BeatDown`、`BouncingFlask` 在双敌根完成基础／升级即时全状态和完整 RNG 差分，包含副目标分配、弃牌自动出牌和随机毒瓶；证据及场景界限见[内容清单](MULTIPLAYER_CONTENT_INVENTORY.md#普通内容的多人差异扫描)。

针对收牌队友归属另加四人代表：本地玩家的 `Largesse` 指定第 3 号队友，牌进入该队友手牌且牌主为该队友，完整状态与 RNG Passed：`.local/multiplayer-p2/largesse-four-target-seat3-eabd09421fe94b42985aeda221619b15/peer-0/result.json`。这补目标身份跨座位的证据，不把生成者参数误作收牌者。

P4 三种计算入口的虚拟双人原生代表：手动入口此前已通过；自动计算只产生本地方案、不擅自部署，Passed：`.local/multiplayer-p4/automatic-calculation-enabled-5b98feffdcb84bd0a7d3581ee8284788/peer-0/result.json`；全自动产生本地方案、按原生动作执行并只结束本地回合，队友未被代操作，Passed：`.local/multiplayer-p4/full-auto-virtual2-6de921827935483c881b174c2d67af09/peer-0/result.json`。首次自动计算探针超时是无人测试宿主默认禁用自动触发，根文件存在但没有搜索结果；按该入口的测试需要显式启用后通过。上述虚拟证据未覆盖 ENet、取消；ENet 自动延续证据见下文。

ENet 双端全自动首回合：房主与加入者分别从本地搜索并原生执行自身牌，双端同步第二回合、完整状态与 RNG 对账 Passed：`.local/multiplayer-p0/enet-2-09e37bb5ce8643039446ee6953ff7359/peer-0/result.json` 及 `peer-1/result.json`。首次双端探针把另一玩家自行进入准备结束误判为“本端代操作”，使对端等待至超时；测试改为逐端检查本地部署牌后通过。

四人 ENet 全自动初次默认并行度请求与改为每端 DOP 1 的请求，四端均在原生建局根后未产出结果，均达到 120 秒并由启动器清理；不计通过。随后修复搜索完成时对同一本地回合队友变化一律丢弃结果的逻辑：在新根重放原当前回合动作，合法则发布更新预览并继续原路线，失效则暂停。虚拟双人搜索期间队友原生 `Strike` 后，原路线重评估、本地部署、无新增完整搜索 Passed：`.local/multiplayer-p4/search-time-drift-virtual2-344e51f9f444480483391e546f5a900f/peer-0/result.json`。在每端 DOP 1 的四人 ENet 复验中，四端本地搜索与部署、同步第二回合、完整状态／RNG 对账 Passed：`.local/multiplayer-p0/enet-4-f316f44de37240058975bc49cd2c9d2e/peer-0/result.json` 至 `peer-3/result.json`。首次四人超时的具体逐端状态未留完整运行日志，不能把它唯一归因为搜索时队友变化；当前证据也不代表默认 DOP 的四人全自动性能已通过。Linux 脚本与 Windows 参数同步，Linux 本轮未运行游戏。

四人玩家目标药水候选复核：`StrengthPotion` 搜索只枚举持有人自用，原生自用后全状态／RNG 与模拟一致，随后 `Largesse` 仍给第 3 号队友生成牌，Passed：`.local/multiplayer-p2/four-self-potion-largesse-ed2714885b2248118fb8d5ef70ec32a3/peer-0/result.json`。这是候选与原生动作代表；生产执行器的格挡药水证据见下文。

同一自用候选规则另以四人 `BlockPotion`、双人 `EnergyPotion` 分别验格挡与能量效果，原生自用即时全状态及完整 RNG 均 Passed；证据与边界见[内容清单](MULTIPLAYER_CONTENT_INVENTORY.md#普通内容的多人差异扫描)。

生产执行器药水代表：虚拟双人把 `BlockPotion` 设为本地必用，搜索方案仅有本地药水动作，原生执行后本地取得格挡、队友格挡不变并只结束本地回合，Passed：`.local/multiplayer-p4/controller-self-block-potion-0b2e57c7926d4cd2aad110ee092f4221/peer-0/result.json`。这证明求解器自身用药执行入口，不证明所有药水或 ENet 投药。

真实 ENet 下一回合自动入口曾被 `Entry.OnTurnStarted` 和本地准备补丁的旧多人门禁阻断；当前只对本地玩家按原有阶段、取消和状态检查运行自动触发。首次双端测试在第二回合开始后等不到自动搜索，因为无人宿主默认关闭自动触发，超时且不计通过；显式启用测试宿主后，双端第二回合重新从当前根搜索并各自原生部署本地牌，第三回合双方完整状态／RNG 对账 Passed：`.local/multiplayer-p0/enet-2-f52d9e1e9246452992994101120ea52e/peer-0/result.json` 与 `peer-1/result.json`。一次中间探针只证明第二回合搜索启动，未证明部署，已由上述更完整输入替代。四人第二回合自动延续、真实 Steam 房间与可见界面仍未验证。

P5 设置输入已在原生无头设置页实际提交多人深度 `3` 与时间 `4.5` 秒，重新读取持久化文件后值保持，测试结束恢复原设置；eng／zhs／zht 构造与 475 条英文目录占位符对账同次 Passed：`.local/multiplayer-p5/ui-settings-edit-509374eb84d540ea80d689f32873b416/result.json`。仍未人工检查可见窗口排版。

P4 RNG 动作边界：虚拟双人双敌根中，本地第一张 `Strike` 后队友原生对本地玩家打 `Largesse`，原版生成 `Shockwave` 归目标玩家所有并推进共享 `CombatCardGeneration`。初试时控制器在队友动作尚未完全结算时观测，未提示偏差；改为每次多人动作边界先等待原生动作队列稳定后，同一场景的 RNG 偏差提示、余下路线重评估、第二张本地 `Strike`、无完整重搜均 Passed：`.local/multiplayer-p4/mid-deploy-rng-fixed-d1f774413f044e81a46697a326d4a5b0/peer-0/result.json`。反向以本地玩家自己对队友打 `Largesse`，原生生成牌牌主为队友、共享 RNG 正常前进且无误报，Passed：`.local/multiplayer-p4/own-largesse-rng-final-1127aa6c05374ca293827af5f3f55a4f/peer-0/result.json`。这两项是虚拟多人生产执行证据；真实 ENet 交错随机流仍未验证。测试在本地回合结束后的队友手牌位置断言曾失败；即时入手与牌主另由内容差分证据确认，回合后仅断言牌仍归队友。

P2 `GremlinMerc`：原版入场给每名玩家建一条 `ThieveryPower`，每次偷窃逐实例扣对应玩家金币。双人根与双方普通牌即时差分 Passed：`.local/multiplayer-p2/gremlin-merc-root-d62f670148804ee8897239a3052bdab5/peer-0/result.json`。首个跨回合差分明确失败于第二名玩家金币原生 79、预测 99；模拟原先只取第一条能力，改为按原版逐实例结算后双人第二回合全状态／RNG Passed：`.local/multiplayer-p2/gremlin-merc-round-fixed-7897a817c6344866a0d997f34eb41ad6/peer-0/result.json`；四人对应回合也 Passed：`.local/multiplayer-p2/gremlin-merc-round-four-ad7a27e68a594586be4b18c861b226e8/peer-0/result.json`。既有定向能力实例合同改为两条均写入金币，Passed：`.local/multiplayer-p2/instanced-thievery-5cbf464691ab46acb566d6eb7644113f/result.json`。仅覆盖 `GIMME_MOVE` 首回合偷窃；其他招式和死亡返还仍待验。

P2 `OvicopterNormal` 双敌及召唤卵：虚拟双人首回合原生结束到第二回合的全玩家、敌人与完整 RNG 差分 Passed：`.local/multiplayer-p2/ovicopter-egg-round-6fd0947191ef4bba8ec663af8f1c4009/peer-0/result.json`。第二回合状态里出现三只带 `HatchPower` 的 `ToughEgg`，本次对齐的是召唤和初始多人 HP；卵尚未孵化，不能把孵化 HP 重设记为通过。

P4 双端 ENet RNG 交错：房主搜索并执行两张本地 `Strike`，加入者在两张之间原生对房主打 `Largesse`。房主收到生成牌，提示共享随机流偏差并从新根重评估，第二张本地攻击继续，无额外完整搜索；双方同一检查点原生状态、玩家阶段及九条完整 RNG 一致，Passed：`.local/multiplayer-p0/enet-2-75c6c88c214a4a7caa289c21b4d183fc/peer-0/result.json` 与 `peer-1/result.json`。首轮测试让加入者等待原始 `PlayCardAction.CompletionTask`，联机同步动作不完成该对象，双方超时；改为等待原生出牌及目标收牌状态后取得有效证据。此项不覆盖四人、Steam 房间或真实网络延迟。

P2 `ToughEgg` 孵化追加第三回合检查：同一 `OVICOPTER_NORMAL` 双人虚拟根推进第二个敌方回合，原版三只卵均转为已孵化，移除 `HatchPower` 并以多人缩放后的随机 HP 重设；预测对所有玩家、敌人、能力及九条 RNG 的完整续用戳一致，Passed：`.local/multiplayer-p2/ovicopter-hatch-assert-f2e4b9fea5b04323b5da3cba71aff9d7/peer-0/result.json`。一次更早的第三回合探针已通过差分但没有显式孵化断言，不单独计为孵化证据。

P4 本地控制权：虚拟双人双敌根中第一张本地 `Strike` 执行后关闭求解器，第二张本地牌未自动打出，队友能量与结束状态不变、无新增完整搜索，Passed：`.local/multiplayer-p4/manual-takeover-fixed-0383b266d34b47cb9071f3fdb3b4fee0/peer-0/result.json`。测试夹具初次把双敌根当单敌调用 `Single()`，在搜索前失败，修为选第一只敌人后通过。另一虚拟双人根启动手动搜索后立即通过用户停止入口取消，双方均未被部署或代结束，Passed：`.local/multiplayer-p4/search-user-stop-9252b7db3f0c4c7999424fb259ef519c/peer-0/result.json`。这些证据不覆盖战斗退出、旧结果回调或真实联机取消。

P4 生命周期清理代表：虚拟双人搜索启动后调用战斗会话 `Reset("multiplayer_probe_exit")`，原搜索／部署停下、求解器界面隐藏，两名玩家均未被代结束，Passed：`.local/multiplayer-p4/lifecycle-reset-f4a212c020ac4bed9ca90c7e9f8a2e7b/peer-0/result.json`。这是退出时调用的清理入口测试，不等于真实房间退出、旧异步回调或 ENet 对端断开全部通过。

P4 旧搜索回调代表：虚拟双人同一原生战斗先启动搜索，立刻 `Reset` 取消，再启动新搜索；新结果可见且可执行，新会话仅计一次搜索，旧任务没有覆盖结果，Passed：`.local/multiplayer-p4/stale-search-callback-5ea7bbb41f244b5a8a790f7282aa9261/peer-0/result.json`。本次在同一战斗重置会话，未模拟真实切换房间或退出进程。

P2 `GremlinMerc` 第二招：双人原生推进第三回合首次差分显示队友缺少 `WeakPower`；原版 `DOUBLE_SMASH_MOVE` 对所有目标玩家施加虚弱，模拟先前只处理本地玩家。按原版遍历存活玩家后，第二个敌方回合的全玩家状态与完整 RNG 差分 Passed：`.local/multiplayer-p2/gremlin-merc-second-fixed-31e35320566f4014b7c3f198bcb80012/peer-0/result.json`。第三招和死亡返还仍未验。

P2 再推进该怪第三个敌方回合到第四回合：原版 `HEHE_MOVE` 后敌人力量 2、两名玩家金币各从 99 降到 39，逐实例偷窃状态与预测全玩家、敌人、九条 RNG 对齐，Passed：`.local/multiplayer-p2/gremlin-merc-third-move-54881f1b85174c2b9c8c329b66fb19af/peer-0/result.json`。死亡返还、玩家途中死亡及其他种子仍未验。

P2 `GremlinMerc` 死亡转移：首轮偷窃后第二回合把怪物血量置 6，由本地玩家原生 `Strike` 击杀；原版生成胖／鬼祟地精，胖地精持有分别指向两名玩家的 `HeistPower` 各 20，完整状态及九条 RNG 对预测一致，Passed：`.local/multiplayer-p2/gremlin-merc-death-fixed-stamp-8bc637ffb298497ba5d8ed6e9da0c927/peer-0/result.json`。首试失败只是差分夹具把第二回合的预测 `turn` 写成第一回合，修为当前根回合后通过。后续击杀胖地精的金币返还仍待验。

P2 随后本地玩家第二张 `Strike` 击杀胖地精，原版房间给两名被偷玩家各加入一份 20 金币追回奖励；预测的全部战斗状态与九条 RNG 仍逐字段一致，Passed：`.local/multiplayer-p2/heist-recovery-c59c5d5a889143068996ab4b51bf28ca/peer-0/result.json`。奖励由原版房间生成，求解器的战斗预测并不构造房间奖励；本项用原版奖励目标断言，不据此宣称战后领奖 UI 已验。

P2 `WaterfallGiant` 四个敌方回合：双人虚拟根的 `PRESSURIZE`、`STOMP`、`RAM`、`SIPHON` 连续结算。首试在第三回合发现 `STOMP` 的虚弱只预测本地玩家，原版给两名目标玩家；按原版群体施加后到第四回合完整状态／RNG Passed：`.local/multiplayer-p2/waterfall-giant-rounds-fixed-5354778cf43b427eb2324cb7fa11d66b/peer-0/result.json`。再在 `SIPHON` 前将敌人血量降低 40，明确断言治疗量为 `SiphonHeal × 玩家数`，到第五回合全状态／RNG Passed：`.local/multiplayer-p2/waterfall-siphon-explicit-e1c7efeb896f4076a1b2418f991a5a69/peer-0/result.json`。这是双人该固定种子的四招证据，其他阶段和死亡路径未验。

P5 防守方案进入执行器：双人一费 `Defend`／`Strike` 固定根在悬浮窗显示输出与防守，点击防守后原生只执行防守牌、不执行输出牌，且本地回合结束，Passed：`.local/multiplayer-p5/defense-style-deploy-fixed-input-c5904e9a0dee42f2a97a165fd2d17cba/peer-0/result.json`。首次误用夹具默认 10 能量，搜索选同时出两张而没有取舍，未通过按钮断言；改用已记录的 1 能量输入后通过。至此三种方案按钮中启动和防守均有选择后执行代表；可见窗口仍未人工检查。

P2 敌人目标药水候选：四人双敌根本地持有 `FirePotion`，搜索候选逐一覆盖两名存活敌人、没有任何玩家目标，Passed：`.local/multiplayer-p2/enemy-potion-targets-0f2fa7cb78094b5ca2b982b876d05897/peer-0/result.json`。本项验证候选合法目标集合；药水实际原生使用与模拟伤害差分未由此证明。

P5 旧预测的随机流说明：求解完成后、点击执行前，队友原生把 `Largesse` 给本地玩家，悬浮窗保留执行按钮并明确提示随机流变化和后续预测不准；点击后从新根重评估原序列并继续本地部署，无完整重搜，Passed：`.local/multiplayer-p5/predeploy-rng-hint-fixed-884797ea14704d88b21fd1729f3501d5/peer-0/result.json`。首次测试发现 `ShowResult` 重建预览时清空已见偏差标记，现重评估后恢复该提示。搜索仍在运行时发生同类队友 `Largesse`，发布结果时也显示偏差已重评估，原路线继续本地执行，Passed：`.local/multiplayer-p5/search-time-rng-hint-9d0c942ebe614adba99abc40bcd3a7e5/peer-0/result.json`。均为无头控件断言，可见窗口排版未验。

P4 四端 ENet 自动执行再补两个不同组合：默认搜索并行设置下，四端各自搜索并部署首回合本地动作、同步第二回合与全状态／九条 RNG 对账 Passed：`.local/multiplayer-p0/enet-4-8574f581756247b9afe12b3794bbcfce/peer-0/result.json` 至 `peer-3/result.json`。每端 DOP 1 且启用下回合自动继续时，四端第二回合均从新根搜索、部署，再同步第三回合全状态／RNG，Passed：`.local/multiplayer-p0/enet-4-eee65da42e15466291de1a0d3ecfe8b7/peer-0/result.json` 至 `peer-3/result.json`。这覆盖本机 ENet 四人两回合，不代表 Steam 邀请、异机延迟或未安装 Mod 的对端通过。

P2 `DecimillipedeElite`：虚拟双人首回合三段敌人与全玩家状态差分首次发现 `CONSTRICT_MOVE` 的虚弱漏给队友，原版对所有目标玩家施加。修正后推进第二回合，三段 HP、能力、意图、玩家状态及九条 RNG 对账 Passed：`.local/multiplayer-p2/decimillipede-round-fixed-b5a8732d07ee4bb7a06e59d920a237ad/peer-0/result.json`。分段死亡、复活及重新附着未在该场景触发。

P2 敌方能力施加缩放：四人虚拟根在同一场景分别从新 Fork 给敌人施加并移除 12 个原版 `ShouldScaleInMultiplayer` 能力，逐项原生／预测完整状态与 RNG 差分 Passed：`.local/multiplayer-p2/all-enemy-power-scaling-3eb5d0a936b342a08bd0970baa5e09fc/peer-0/result.json`。前 5 项已有旧证据，本轮新增 `Plow`、`Reattach`、`Flutter`、`Regen`、`Rampart`、`Shriek`、`HardenedShell` 的即时施加代表。其监听生命周期、怪物专属触发仍待验。

P2 蜈蚣分段死亡／重附：双人第二回合把一段血量置 1 后原生 `Strike` 击杀，死亡后仍留场且其余两段存活；该回合敌方 `DEAD_MOVE` 仍保持死亡，下一敌方回合 `REATTACH_MOVE` 才复活。死亡、两个回合边界、复活 HP、能力、意图与完整 RNG 均和预测逐字段一致，Passed：`.local/multiplayer-p2/segment-reattach-full-14c99fc3007b47a08ab9846392e3a262/peer-0/result.json`。首次夹具把弱化后的打击误认为能击杀 6 HP，第二次误把 `DEAD_MOVE` 当作立即复活；按原版状态机修正测试时点后通过。全段同时死亡的终局路径未验。

P2 `TheObscuraNormal` 双人根到第三回合：原版初始伙伴 `Parafright`、前两次敌方行动后的双方状态、怪物能力与九条 RNG 均与预测对齐，Passed：`.local/multiplayer-p2/obscura-second-round-35ee5bad5bbb4718aed927b9cd2325a1/peer-0/result.json`。伙伴死亡、幻象变化及更后续招式未验。

P2 `QueenBoss` 双人根：首个敌方回合差分发现 `PUPPET_STRINGS_MOVE` 的束缚只预测本地玩家，原版给两名玩家；同时按原版将 `YOU_ARE_MINE_MOVE` 的三种异常状态改为逐存活玩家施加。修复后到第四回合全玩家、女王与怪物伙伴、牌上 `Bound`、能力和完整 RNG 差分 Passed：`.local/multiplayer-p2/queen-third-round-fddc55be530542df8dbbb00efbb3d3ac/peer-0/result.json`。第四回合可见两名玩家均持束缚及三种异常状态；这是该固定种子的前三招，不覆盖女王死亡和后续状态分支。

### 0.3 接手后第一轮的具体操作

1. 保留原型，读这三个新增文件与两个现有接入点，查清输入和等待条件；不要重新实现一套测试系统。
2. 先运行一个虚拟双人请求，定位第一个实际失败点。修正该根因后只重跑该失败请求；通过后补虚拟四人。
3. 用同一配置格式启动双进程 ENet 请求，驱动完整一回合并比较双方检查点；再完成四人建局。多进程启动只补最小编排工具，复用已有无头实例、租约和清理。
4. 同步产出内容清单和一条受影响单人基线，按 P0 退出条件记录结果，再进入 P1。

下面是**尚未实测的原型调用示例**，不是通过记录。工作目录为仓库根；每次新会话使用新目录，避免读到上一轮屏障文件：

```powershell
$probeSession = Join-Path (Get-Location).Path ('.local/multiplayer-p0/virtual-2-' + [Guid]::NewGuid().ToString('N'))
$probePeer = Join-Path $probeSession 'peer-0'
New-Item -ItemType Directory -Path $probePeer -Force | Out-Null
$probeInput = Join-Path $probeSession 'input-0.json'
@{
    mode = 'virtual'
    playerCount = 2
    seat = 0
    port = 33771
    coordinationDirectory = $probeSession
    expectedGameVersion = '0.111.0'
} | ConvertTo-Json | Set-Content -LiteralPath $probeInput -Encoding utf8
pwsh -NoProfile -File tools/run-unattended-test.ps1 `
    -ScenarioId MULTIPLAYER-P0 -MultiplayerProbePath $probeInput `
    -EvidenceDirectory $probePeer -HeadlessInstance ('mp-p0-' + [Guid]::NewGuid().ToString('N')) `
    -TimeoutSeconds 120 -ExitOnComplete -CleanupInstanceOnExit
```

四人虚拟请求只把 `playerCount` 改为 4。ENet 请求每个进程有独立输入：0 号为 `host`，其余为 `client`，`seat` 从 0 递增；共享本次 `coordinationDirectory` 与未占用端口，每个进程的 `EvidenceDirectory` 必须为对应 `peer-<seat>`。各进程使用不同 `HeadlessInstance`，并通过现有 `HeadlessExecutionMode=parallel` 的资源准入机制同时启动。真实动作必须走原版网络；共享文件仅用于测试屏障和对账，不能替代网络同步。

首个运行重点观察：大厅轮询与所有玩家就绪、各进程建局时机、本地选牌是否经过真实网络、虚拟队友动作归属、第二回合等待、对端提前退出，以及失败后的实例清理。这些是需要验证的地方，不是已确认的 bug。

原型故意采用固定角色、五张牌和一个遭遇，足以验证原版入口。P0 不把它扩展成通用场景 DSL；P1/P2 有实际重复需求时，再复用现有请求结构增加必要字段。

## 1. 已确定的功能边界

| 项目 | 实施要求 |
|---|---|
| 人数与身份 | 支持 2～4 人，房主和加入者都能使用；仅决定本地玩家的动作 |
| 安装要求 | 队友无需安装 CombatSolver；通过原版动作及同步流程操作自己的角色 |
| 内容覆盖 | 完整适配原版多人卡牌及关联能力、遗物、药水、生成物与普通内容的多人行为 |
| 药水目标 | 仅把本地玩家持有的玩家目标药水用于自己；不向队友投药。敌人目标药水按原版合法敌人目标使用 |
| 决策信息 | 策略判断只使用队友的可见信息；精确结算允许捕获必要内部状态，不搜索队友牌序 |
| 队友假设 | 预测期间队友不再主动出牌或用药；现有能力、被动触发、抽弃牌及回合结算正常推进 |
| 搜索范围 | 默认 2 回合、3 秒；回合深度与时间预算分别支持玩家自定义 |
| 执行范围 | 只应用当前回合；每个新回合建立新根重新计算 |
| 方案类型 | 最多输出、防守、启动三类；相同或无实际取舍的方案合并，不设置独立支援方案 |
| 纯支援 | 自己的方案确定后，有可支配余费才考虑纯支援牌；群体收益包含自己、或直接使自己获益的牌正常参与搜索 |
| 队友目标 | 需要指定队友时，从合法目标随机选择并固定在方案里；使用独立于游戏 RNG 的选择来源 |
| 计算时机 | 默认手动；可选自己进入出牌阶段自动计算、其他存活队友全部结束后自动计算 |
| 自动操作 | 玩家选定方案后连续执行本回合；自动触发计算不自动替玩家选择方案 |
| 外部变化 | 从新状态重评估剩余原序列；动作仍合法就允许继续，不因队友改变战场一律丢弃方案 |
| RNG 偏差 | 主动监控并提示旧预测可能不准；偏差本身不强制停牌、不自动触发完整搜索 |
| 等待与选择 | 可以提示等待队友配合，由玩家决定；队友自己的选牌由队友完成，执行等待原生动作结算 |
| 展示 | 出牌顺序与目标、伤害、自身战损、队友收益、药水消耗、启动收益、预测假设及可信状态 |

### 1.1 与单人功能的关系

- 单人保留现有整场求解、严格续用和部署行为。多人采用独立的请求政策与结果范围，共用精确模拟引擎。
- 多人完整建模并不承诺能预知队友未来的人类选择；未知外部输入需要有明确的预测边界或条件说明。
- 当前任务聚焦战斗内助手。战前预测 API、战绩站、录像收集、在线统计及第三方玩法 Mod 的多人扩展不自动纳入本批。
- 首次正式开放多人入口之前，必须满足第 9 节完成条件。开发过程中的实验入口明确标识，不能只删除单人门禁就宣布支持多人。
- 本文不触发版本提升、发布 ZIP、标签、上传或远端推送。实际开发按阶段提交，发布按用户后续指令执行。

## 2. 当前证据与待验证项

以下是源码阅读结果，均不等于运行验证。

| 观察 | 依据 | 对实施的影响 |
|---|---|---|
| 当前搜索明确拒绝多人 | [CombatBeamSolver.Phases.cs](../src/Search/CombatBeamSolver.Phases.cs)、[SolverController.cs](../src/Runtime/SolverController.cs) | 核对 Runtime、Search、UI 等所有入口；通用状态完成后才逐层开放 |
| 根在主线程捕获，后台使用隔离状态 | [CombatRootSnapshot.cs](../src/Runtime/CombatRootSnapshot.cs) | 延续所有权设计，扩展到所有相关玩家；后台不得补读 live 值 |
| 计算期间状态变化会丢弃整个结果 | [SolverController.cs](../src/Runtime/SolverController.cs)、[LiveCombatStamp.cs](../src/Runtime/LiveCombatStamp.cs) | 多人需要区分旧数值过期、原序列可重评估、下一步已不可执行 |
| 卡牌目标枚举未展开队友目标 | [CombatBeamSolver.Expansion.Candidates.cs](../src/Search/CombatBeamSolver.Expansion.Candidates.cs) | 使用稳定玩家／生物身份表达目标，区分敌人、队友、自身与群体 |
| 已捕获九条战斗 RNG 的计数与内部状态 | [ContinuationStamp.cs](../src/Runtime/ContinuationStamp.cs) | 复用完整状态比较，增加按已执行前缀判断的多人观测 |
| 现有差分重点围绕一个指定玩家 | [UnattendedTestRunner.StateDiff.cs](../src/Testing/UnattendedTestRunner.StateDiff.cs) | 多人差分必须逐玩家比较，不能仅比较本地玩家与敌人 |
| 原版存在虚拟多人语义 | 原版 `RunManager.IsSingleplayerOrFakeMultiplayer`、`CombatManager.AllPlayersReadyToEndTurn` | 可作为单进程结算测试起点，但会绕过部分多人结束回合同步 |
| 原版动作携带玩家身份 | 原版 `PlayCardAction`、`EndPlayerTurnAction` | 脚本指定玩家执行具有源码基础；选牌和完整动作队列链路仍需实测 |
| 原版提供本机 ENet 联机 | 原版 `NMultiplayerTest` 使用本机地址和房主／客户端服务 | 可验证真实同步；无头启动、多人会话清理与选牌驱动需先做可行性验证 |

原版文件来自本机已有的 `sts2-v0.111.0` 只读反编译目录，未提交进仓库。P0 需核对实际加载游戏版本与参考源码；版本不同先刷新对应证据。不能把虚拟多人描述为正式的同屏多人功能，也不能把测试角色描述为现成的队友 AI。

## 3. 数据与职责设计

### 3.1 多人根与分支状态

复用现有 `SimulatedCombatState`、`CombatPredictionSimulator`、`PredictionStateStore` 和统一 `PredictionForkContext`，按玩家稳定身份扩展必要数据。优先修正现有的单人假设，不另建一套通用战斗引擎。

根与分支至少覆盖：

- 玩家身份、本地玩家身份、存活／死亡／复活状态、当前阶段、回合编号、结束／撤销结束状态。
- 各玩家生命、格挡、能量、星能、有序牌堆、逐实例卡牌状态、球、伙伴、药水、遗物和能力。
- 能力来源、施加者、目标、跨玩家引用、生成卡所有权和卡牌在玩家之间转移后的身份。
- 所有相关怪物的目标、意图、AI、伤害分配、多人缩放、死亡与召唤状态。
- 影响合法动作或结算的历史、九条战斗 RNG、相关模型私有状态及监听者。

所有可变数据在主线程稳定点捕获，后台消费冻结根及其分支。一次 Fork 用同一个上下文重映射所有玩家之间的引用；卡牌、能力、球、伙伴及历史不得残留父分支的可变对象。

多人状态键与差分以稳定身份对齐各玩家；列表顺序只是原版结算顺序，不能替代所有者身份。UI 文本、可信度提示和策略估值不进入战斗等价性键。

### 3.2 精确结算与策略读取分开

精确模拟可使用必要内部快照，策略侧读取受限的队友视图：公开生命、格挡、能力、可见意图及结束状态等。用类型与接口约束信息边界，避免评分函数随意读取队友隐藏手牌、牌序或私有 RNG 信息来优化支援目标。

本地玩家的出牌、对自己或敌人合法目标用药，以及本地选牌可形成候选；玩家目标药水不枚举队友。队友主动动作不形成搜索分支；其已有被动效果与原生回合事件必须继续结算。测试脚本驱动队友只是构造已知动作，不会进入生产搜索。

队友选择属于外部输入：原版等待队友选择完成时，执行器等待同一原生动作完成任务。模拟能完整实现该效果并接受测试提供的确定选择；生产预测遇到未知且会影响本地后继的选择时，标明边界或条件，不能替队友编造答案。仅与队友后续使用有关的收益可单独估值，不能伪装为精确结算。

### 3.3 现有层次的责任

| 层次 | 多人职责 |
|---|---|
| Runtime | 主线程快照、状态变化观察、会话与取消、自动触发、原序列重评估调度、原版动作执行 |
| Engine / Prediction | 精确原版效果、跨玩家状态、引用重映射、RNG、目标与多人生命周期 |
| Search | 本地动作候选、共享预算、有限回合搜索、三类方案、纯支援准入及估值 |
| UI snapshot / UI | 主线程投影与只读渲染、方案选择、设置、假设和过期提示、中英本地化 |
| Testing / tools | 建局、脚本玩家、完整差分、联机编排、断言、证据与进程清理 |

职责变化时更新 [ARCHITECTURE.md](ARCHITECTURE.md)、相关 skill 和两端结构门禁。具体类型名称在实现时按实际所有权确定；本文没有声明新的可调用 API。

## 4. 搜索、支援与执行合同

### 4.1 深度与预算

- 默认深度为本地玩家当前回合加下一个可行动回合。普通回合场景评估相应回合尾及敌方行动的后果，不能用尚未承受攻击的格挡值冒充已保血。
- 深度以本地玩家可行动回合计，当前回合记为 1。额外回合、跳过回合及玩家阶段不同步按原版事件顺序推进，单独测试并在结果中保留实际回合边界。
- 时间是一次请求的共享搜索预算。输出、防守、启动、补充支援和搜索内回放共同消费，不能每个方案重新领取完整时间。
- 到达深度上限、预算耗尽、战斗结束或明确预测边界后收尾。禁用会暗中扩大多人预算的无胜利升级、整场续搜与额外计划地平线。
- 3 秒是搜索预算，根捕获、已派发作业排空和结果投影会有额外耗时；分别记录，不承诺严格的 3 秒端到端响应。设置只接受合法的有限正时间和正整数深度。
- 结果显示实际到达深度。预算不足时交付已验证的合法前缀或较浅方案；尚无合法候选就明确报告，不能补造结果。

### 4.2 三类方案

| 方案 | 主要比较方向 | 需要避免的偏差 |
|---|---|---|
| 输出 | 本地动作实际造成的有效伤害、击杀及减轻的威胁 | 用溢出伤害或击打无效目标抬分 |
| 防守 | 自身实际／条件预测战损、有效格挡和减伤 | 奖励用不上的格挡，或把队友未来不出牌造成的死亡当作真实结局 |
| 启动 | 已兑现的启动收益加明确标注的范围外持续收益估值 | 按打出能力牌张数奖励、把估值加成真实伤害或重复计算收益 |

各方案使用自己的排序目标，沿用真实合法性、生命与资源约束。团队收益可以展示，不以团队最优或支援收益强行覆盖玩家选定风格。队友在“不再出牌”的条件预测中死亡，需要准确影响后续结算，但不能凭此把仍可执行的本地方案统一判为真实失败。

去重比较实际当前回合动作、目标、选择和有意义的收益取舍；不因动作排列不同就凑满三条。只找到一条有效方案就展示一条。

### 4.3 纯支援规则

分类依据为卡牌自身效果及必要条件，不以卡名、颜色或 `AnyAlly` 目标类型一刀切。

| 分类 | 处理 |
|---|---|
| 收益只给队友的纯支援牌 | 自己的候选方案形成后，使用可支配余费考虑补入 |
| 自己也获得收益的群体牌 | 正常参与本地搜索；刀刃交响曲、全体格挡等在此类 |
| 指向队友但直接使自己获益的牌 | 正常参与搜索，例如复制队友格挡给自己 |
| 条件式、自身收益与队友收益混合的牌 | 按已核对的机制登记分类条件，并测试条件两侧 |

余费指不占用选定方案所需资源、也未被计划保留到未来的可支配能量。纯支援补入可在不破坏主方案的合法位置尝试，不能挤掉自己的动作。有余费只是候选准入条件；零费牌仍需检查卖血、星能、消耗、出牌限制、触发与其他实际成本。

补入后完整回放，检查本地收益与资源约束。支援触发的抽牌、能力或 RNG 消耗必须计入；不能把它当作与战斗无关的尾部装饰。纯支援本身的队友收益只作为余费候选之间的参考，不重新驱动一轮独立支援搜索。

需要指定队友时随机选合法对象，固定到候选身份与方案中；同一候选的重放、重评估和执行保持该对象。不同分支不能反复抽取同一候选目标来变相搜索最优队友。选择来源独立于游戏 RNG，可在测试中固定。目标失效且影响剩余动作时暂停，不偷偷换目标。

### 4.4 状态变化与原序列

把“计划动作”“当前数值预测”“下一步可执行性”分别表示：

1. 队友行动或状态改变后，旧数值立即标为待更新，原动作列表仍可保留。
2. 在新的稳定根上重放剩余序列，更新可确定的结果；只消费本地已执行前缀，不重复执行旧动作。
3. 普通血量、格挡、能力变化不强制中断仍合法的原序列。预测更新耗时本身不应变成持续停牌理由；每步仍须做原生合法性检查。
4. 原计划中的正常击杀允许继续。队友击杀怪物，仅在影响剩余动作时暂停。
5. 本地玩家实际死亡、战斗已结束、原回合结束、目标失效、卡牌不在所需位置、资源不足或未完成原生选择等，按真实边界结束或等待，不能强行执行。
6. 重评估不更换风格、不重新排列动作、不等价于完整搜索；完整搜索由玩家或选定自动入口触发。
7. 新状态到来时合并过时重评估请求；旧任务不能覆盖较新根的预测，也不能恢复已取消的执行会话。

队友自己的选择由原生系统处理；队友选牌等待期间保留其原生动作。玩家手动接管、停止或退出战斗时，释放求解器的输入所有权并清理所属搜索／部署任务，不能取消其他玩家的动作。

### 4.5 RNG 提示

主线程在稳定动作边界观测完整 RNG 状态，与当前已执行前缀的预期值比较。自己的正常消耗应推进预期基线；不能把“相对初始根发生变化”全部当作偏差。

偏差提示说明后续抽牌、生成或随机目标等预测可能不准，不凭猜测归因到某个队友。偏差本身只使旧预测过期，不强制停牌、不自动完整重搜。重评估用新 RNG 更新后，可信状态随新证据恢复；尚有未知外部输入时继续保留相应说明。

如果新的随机结果导致后续牌、目标或选择不存在，执行器在首个无法执行的动作前暂停。高频变化合并提示，不重复弹出相同通知。

### 4.6 自动化与展示

- 默认手动计算；另外提供本地出牌阶段开始、其他存活队友全部按结束后的自动计算。
- “队友出完”以原版结束状态为准，不根据短暂无操作推断。队友撤销结束会使条件失效，过时回调不得再次启动同一请求。
- 多个使用者都选择等待其他人结束时可能互等，允许任一人手动先算；本批不增加求解器间协商协议。
- 自动计算完成后由玩家选方案。本回合执行结束后，下回合依据触发设置重新计算，不自动承诺沿用上一回合风格。
- 提示等待队友配合时，显示具体条件与假设，由玩家决定何时继续；首版不自动等待条件满足后替玩家启动出牌。
- 输出事实值、条件预测和持续收益估值分别显示。明确“预测中队友不再主动出牌”，展示实际深度、目标身份与旧预测状态。
- 新增玩家文案同时提供中文和英文，官方名称从对应游戏版本本地化核对。

## 5. 原版内容完整适配

### 5.1 初始盘点

在 0.111.0 参考源码中检出的 37 个直接声明 `MultiplayerOnly` 的卡牌类型如下。这里使用技术类型名定位源码；玩家界面和更新日志使用核对过的官方译名。

| 序号 | 类型 | 当前证据状态 |
|---|---|---|
| 1 | `BeaconOfHope` | 基础／升级即时差分通过；完整机制待验 |
| 2 | `BelieveInYou` | 基础／升级即时差分通过；完整机制待验 |
| 3 | `BladeSymphony` | 基础／升级即时差分通过；完整机制待验 |
| 4 | `Blaze` | 基础／升级即时差分通过；完整机制待验 |
| 5 | `Cacophony` | 基础／升级即时差分通过；完整机制待验 |
| 6 | `Concoct` | 基础／升级即时差分通过；完整机制待验 |
| 7 | `Constellation` | 基础／升级即时差分通过；完整机制待验 |
| 8 | `Coordinate` | 基础／升级即时差分通过；完整机制待验 |
| 9 | `DemonicShield` | 基础／升级即时差分通过；完整机制待验 |
| 10 | `EnergySurge` | 基础／升级即时差分通过；完整机制待验 |
| 11 | `Fade` | 基础／升级即时差分通过；完整机制待验 |
| 12 | `Flanking` | 基础／升级即时差分通过；完整机制待验 |
| 13 | `GangUp` | 基础／升级即时差分通过；完整机制待验 |
| 14 | `GlimpseBeyond` | 基础／升级即时差分通过；完整机制待验 |
| 15 | `HammerTime` | 基础／升级即时差分通过；完整机制待验 |
| 16 | `Hibernate` | 基础／升级即时差分通过；完整机制待验 |
| 17 | `HuddleUp` | 基础／升级即时差分通过；完整机制待验 |
| 18 | `Ignition` | 基础／升级即时差分通过；完整机制待验 |
| 19 | `ImitationLearning` | 基础／升级即时差分通过；完整机制待验 |
| 20 | `Intercept` | 基础／升级即时差分通过；完整机制待验 |
| 21 | `Knockdown` | 基础／升级即时差分通过；完整机制待验 |
| 22 | `Largesse` | 基础／升级即时差分通过；完整机制待验 |
| 23 | `LegionOfBone` | 基础／升级即时差分通过；完整机制待验 |
| 24 | `Lift` | 基础／升级即时差分通过；完整机制待验 |
| 25 | `Midnight` | 基础／升级即时差分通过；完整机制待验 |
| 26 | `Mimic` | 基础／升级即时差分通过；完整机制待验 |
| 27 | `OneForAll` | 基础／升级即时差分通过；完整机制待验 |
| 28 | `Outrage` | 基础／升级即时差分通过；完整机制待验 |
| 29 | `Plot` | 基础／升级即时差分通过；完整机制待验 |
| 30 | `Rally` | 基础／升级即时差分通过；完整机制待验 |
| 31 | `Sneaky` | 基础／升级即时差分通过；完整机制待验 |
| 32 | `Soulbound` | 基础／升级即时差分通过；完整机制待验 |
| 33 | `TagTeam` | 基础／升级即时差分通过；完整机制待验 |
| 34 | `Tank` | 基础／升级即时差分通过；完整机制待验 |
| 35 | `TheBall` | 基础／升级即时差分通过；完整机制待验 |
| 36 | `Tutor` | 基础／升级即时差分通过；完整机制待验 |
| 37 | `Underworld` | 基础／升级即时差分通过；完整机制待验 |

37 张的原版入口、已发现关联机制及普通内容待调查名单见 [多人内容盘点](MULTIPLAYER_CONTENT_INVENTORY.md)。该清单仍在 P0 盘点中，尚未封闭。37 张只是初始集合，不是全部工作量。P0/P2 需沿调用链补齐：

- 卡牌施加的 Power、对应 Hook、私有计数、跨回合状态和派生生成物。
- 多人专用遗物及普通遗物的多人行为；已发现的 `MassiveScroll` 纳入盘点，其余以完整目录审计为准。
- 本地药水仅用于自己或原版合法敌人目标；核对多人生成池、共享随机消耗及可达内容，不能从“没有 MultiplayerOnly 标记”推导无需适配。
- 普通牌、怪物和能力中的玩家枚举、攻击目标分配、死亡／复活、额外回合、伙伴及球的多人差异。
- 卡牌跨玩家转移、所有者变更与引用；例如 `TheBall` 的去向，不能只验伤害数字。

逐项记录“原版入口 → 权威模拟入口 → 状态所有者 → 分支复制 → 差分场景 → 实际结果”。已有实现可复用，但需要多人证据；不能把类型名存在于 registry 当作验收通过。

### 5.2 建模与验收纪律

1. 先核对实际原版调用链，确定唯一权威结算点，避免 mirror、spec、support 重复结算。
2. 原版效果完整实现；纯支援准入放在 Search，不能通过省略牌的效果实现策略。
3. 基础版和升级版逐项覆盖；共享机制复用参数化差分，复杂机制增加生命周期与 Fork 场景。
4. 比较所有受影响玩家及敌人的完整相关状态，包括有序牌堆、逐实例卡牌、能力来源／目标、私有状态、历史和完整 RNG。
5. 覆盖自身／队友／群体目标、不同座位、目标死亡、跨玩家传播、叠加与移除、跨回合及原生选牌等待。
6. 未支持语义显式失败或形成已定义边界；不得默认跳过、吞异常、伪造随机结果或仅验证最终总 HP。
7. 使用现有 registry 描述进入 CoverageCatalog，相关目录验证与原生差分分别留证。覆盖率与实际正确性分别报告。

## 6. 测试脚手架

### 6.1 单进程虚拟多人：结算验收

在现有 unattended 框架上扩展多人建局与脚本，不另造与生产引擎无关的假战斗器。脚本描述玩家身份、角色、装备、初始状态、原版动作顺序与选牌结果；执行器驱动原版动作，等待对应完成任务，在稳定边界取证。

完整差分器按玩家身份记录全部相关字段。增加负向合同：故意只改变队友格挡、某张卡的所有者或一条 RNG 的内部状态时，差分必须定位错误，证明它确实覆盖多人。

预测先从同一稳定根冻结，再推进原版动作；原版结果不通过模拟实现生成。分支父子与兄弟独立性、根捕获后 live 推进仍不影响分支，均需直接断言。

### 6.2 本机真实联机：同步验收

通过原版 ENet 房主／客户端服务建立隔离会话，各进程只发送自己的玩家动作。验证顺序、结束／撤销结束、选牌暂停恢复、目标死亡和状态一致性；不能用单进程切换 `LocalContext.NetId` 冒充真实客户端。

先跑通双进程，再扩到四进程。至少覆盖房主启用求解器、客户端启用求解器、多个使用者以及其他玩家不加载 CombatSolver。若脚本驱动器仍需加载在测试对端，明确它与生产 CombatSolver 的区别，并补齐真实无求解器对端证据。

虚拟多人可验证精确效果，但不能验收“等队友全部结束”和真实网络顺序。本机 ENet 可验证同步链路，但不冒充 Steam 房间发现或真实网络延迟测试；Steam 邀请及可见 UI 的未测项单列。

### 6.3 进程与产物

- 所有无头实例、请求、结果和临时包放在当前仓库忽略目录，沿用 `.local/headless-instances/<实例>` 所有权与清理机制。
- 本机联机的各进程独立保存配置、身份、存档、Mod 快照、日志和协议文件；会话端口由工具统一管理。
- 无头验证构建使用 `-p:CopyModOnBuild=false`，实例钉住所测 DLL；任务完成后的本地 Mod 部署按仓库规则执行。
- 启动器仅清理自己拥有的 PID／出生身份和实例，不操作用户正在运行的游戏。Windows 后台 helper 隐藏启动。
- Windows / Linux 原生脚本同时维护接口；实测的平台分别记录，不能把一端成功写成两端通过。
- 最小请求按现有默认 120 秒总超时执行；超时定位或缩小场景，不反复扩大超时。多进程编排记录各节点与整次会话期限。

## 7. 实施阶段与退出条件

采用一个多人开发分支，按阶段提交。先核对工作区，再建立实施分支；不建立 DS 分支或双工作树协作。阶段以行为和证据收口，不能只按编译成功推进状态。

| 阶段 | 工作与交付 | 进入下一阶段的条件 | 当前状态 |
|---|---|---|---|
| P0 可行性与盘点 | 核对版本；核验虚拟多人与 ENet；两人／四人建局；指定玩家动作与选择；封闭内容清单；记录现有单人基线 | 虚拟多人至少完成指定队友普通出牌与结算；双进程联机完成动作同步和一次完整回合；四人建局可用；剩余限制有具体记录 | 虚拟 2／4 人、ENet 2／4 端和单人短搜通过；内容清单仍未封闭 |
| P1 通用状态与差分 | 所有玩家快照、目标身份、Hook 所有权、共享 RNG、Fork、多人历史、回合与死亡边界；通用差分和参数化测试入口 | 普通已有卡牌在 2／4 人根严格对账；跨两回合、兄弟分支隔离、根/live 隔离与差分负向合同通过 | 普通 2／4 人、兄弟 Fork、多人历史和若干死亡／复活分支通过；剩余生命周期开口见内容清单 |
| P2 原版内容建模 | 完整清单内卡牌、Power、遗物、药水及普通内容多人差异；机制分类和关联测试 | 每项有原版依据及基础／升级差分证据；复杂机制的生命周期、所有者和引用合同通过；无影响范围内未解释缺口 | 37 张专用卡即时、14 张普通生成牌、代表性 Power／怪物／药水／遗物差分通过；其余分支和内容闭包未完成 |
| P3 有限回合与多方案 | 深度／时间配置、共享预算、本地候选、队友无主动动作、三类排序、估值、去重、余费支援 | 固定根短搜证明三类目标与去重；预算和深度上限有效；纯支援分类、随机目标固定及自身收益牌正常搜索通过；单人哨兵通过 | 默认深度／时间、多方案、余费支援、队友目标与单人哨兵有代表证据；完整内容准入仍受 P2 限制 |
| P4 执行与变化处理 | 新状态重评估原序列、RNG 提示、逐步原生执行、选牌等待、当前回合范围、三种计算入口 | 队友交错动作、随机偏差、计划内／外击杀、不可执行暂停及旧任务淘汰在真实联机链路通过 | 虚拟变化／RNG／失效与 ENet 2／4 端部署通过；真实 ENet 交错、无求解器对端等组合未齐 |
| P5 界面与完整验收 | 设置、方案列表、收益／假设／可信状态、中英文本；四人联调、单人回归、文档与本地部署 | 第 9 节检查项有对应证据；实际未测项明确，只有满足范围的功能对外声明完成 | 无头设置编辑／本地化／方案按钮、四端全自动与本地部署通过；可见 UI、Steam 与 Linux 实机未验 |

P2 可按机制小批完成并立即验证，不等待全部内容写完才测试。发现通用根因回到对应职责修复，禁止在单卡里复制底层规则。P0 若某原版测试入口不可用，记录具体失败，调整为可运行的原版建局入口；不能靠自制期望值替代原生验收。

## 8. 计划中的验收场景

下列编号用于规划追踪，目前不是现成 `ScenarioId`。实现时登记到真实测试入口，并在 [TEST_MATRIX.md](TEST_MATRIX.md) 与结构化证据中记录实际名称、命令及结果。

| 计划编号 | 最小覆盖 |
|---|---|
| MP-BOOT | 2／4 人建局、本地玩家不同座位、独立身份与存活状态 |
| MP-ACTOR | 指定队友原版出牌、对自己用药、选牌和结束；对应动作完成后取证 |
| MP-DIFF | 逐玩家差分，队友格挡／卡牌所有权／RNG 人工差异被检出 |
| MP-FORK | 父子／兄弟隔离、跨玩家模型引用、根捕获后 live 改变不污染分支 |
| MP-ROUND | 两回合、额外／跳过回合、各玩家结束状态、死亡／复活与多人攻击目标 |
| MP-CONTENT | 完整清单参数化原版／模拟差分，基础／升级及关联能力、遗物、药水 |
| MP-TRANSFER | 卡牌跨玩家转移、生成所有权、牌堆顺序与私有计数 |
| MP-CHOICE | 队友自己的选牌等待、外部结果注入、继续执行与本地选择隔离 |
| MP-SUPPORT | 余费准入、保留能量、零费附加成本；群体与自身获益牌正常搜索 |
| MP-TARGET | 随机队友合法性与固定身份、重放稳定、游戏 RNG 不被建议选人推进 |
| MP-HORIZON | 默认 2 回合／3 秒、自定义值、共享预算、不足深度与无候选报告 |
| MP-OPTIONS | 输出／防守／启动确有取舍，重复方案合并，估值与实际结果分离 |
| MP-DRIFT | 队友改变血量／格挡／能力后原序列重评估，仍合法时继续 |
| MP-RNG | 自己预期消耗不误报；队友推进共享 RNG 后提示；原序列合法则继续 |
| MP-INVALID | 队友提前击杀目标、随机生成改变后续牌、费用／位置变化时在首个失效动作前暂停 |
| MP-AUTO | 三个触发入口、撤销结束、同回合去重、多人互等时手动入口可用 |
| MP-NET | 房主／加入者／四人交错动作、无求解器对端及同步状态一致性 |
| MP-CANCEL | 停止、手动接管、退出战斗、旧任务完成不覆盖新根、会话及进程清理 |
| SP-SENTINEL | 单人合法动作、完整状态、原有策略及续用／执行路径的受影响代表回归 |

验证层次沿用项目规则：文档 L0；效果默认最小严格差分；新增 Fork／跨回合使用最小生命周期；最终搜索与部署编排再跑代表完整场景。每张内容的映射证据要齐全，共享根因的深度测试按机制去重。

可复用的现有命令包括：

```powershell
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
pwsh -NoProfile -File tools/verify-refactor-boundaries.ps1
```

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
./tools/verify-refactor-boundaries.sh
```

多人建局、联机编排和内容测试参数在 P0/P1 实现并跑通后补入实际命令。普通 `dotnet` 离线宿主只用于搜索指标，不能代替原版结算和联机验收；无头耗时不能外推可见帧率。

## 9. 完成条件与记录方式

- [ ] 2～4 人、房主及加入者均能操作自己的角色，其他玩家无需加载求解器。
- [ ] 内容闭包盘点完成，原版多人内容及关联机制全部有精确建模和对应差分证据。
- [ ] 所有相关玩家、敌人、历史、私有状态与 RNG 都纳入所需快照、Fork、状态键和差分。
- [ ] 默认 2 回合／3 秒及自定义生效，各风格共享预算，结果准确显示实际深度。
- [ ] 输出／防守／启动方案正确表达取舍；纯支援仅用余费；自身获益和群体牌正常搜索。
- [ ] 玩家目标药水只给本地玩家自己使用，敌人目标药水保持原版合法目标。
- [ ] 随机队友目标固定且不消耗游戏 RNG；重评估与执行保持一致。
- [ ] 队友行动与 RNG 偏差按规定提示／更新，合法原序列可继续，失效动作前暂停。
- [ ] 仅执行当前回合，下回合新根重算；原生队友选牌和队友操作不被接管。
- [ ] 三个计算入口、取消／退出／手动接管和旧任务淘汰有测试证据。
- [ ] 虚拟结算与本机真实联机分别验收；Steam 房间流程、真实网络及可见 UI 的结论按实际证据限定。
- [ ] 受影响单人行为通过回归；Windows／Linux 工具入口保持一致，实测端清楚标注。
- [ ] 架构地图、开发笔记、测试矩阵、CoverageCatalog 及受影响的第三方适配文档与最终源码一致。
- [ ] 本地 Mod 按仓库规则部署，测试实例清理完成；发布状态另按用户指令记录。

阶段记录统一填写：源码提交、游戏版本、场景及输入、实际命令、通过／失败／未验证、证据位置、仍有的限制。成功且来源未变化的验证不重复；不能用源码阅读、编译成功或旧记录填勾运行验收项。

## 10. 必须遵守的实现与测试约束

用户明确指出接手模型容易过度设计、防御性编程和反复测试。以下是本任务的工作约束，不是可选建议。

约束针对本轮新增和修改的逻辑；不以此为由扫描重写旧模块，也不删除原版协议或现有无关功能所需的行为。

### 10.1 禁止过度设计

- 使用已有 Runtime、Search、Engine、Prediction、Testing 分层。允许必要的具体类型与 partial；禁止另起战斗引擎、DI 容器、通用事件总线、插件框架、通用工作流、配置语言或多程序集拆分。
- 单个实际调用点直接实现，不为想象中的未来使用者提前设计接口、工厂、策略层或适配层。新增抽象必须消除已经存在的重复或承载明确的状态所有权。
- 只修本阶段涉及的职责。不要顺带清理整个项目、统一所有命名、重排巨大文件、替换无关算法或增加未要求的设置。
- 原版语义只有一份权威实现。不得让单卡 mirror 和通用 support 各结算一次，再靠反向补偿抵消。
- 确认逻辑正确后直接替换错误实现；禁止层层加条件、否定的否定、事后修正数值和特殊场景补丁。
- 不开展独立性能优化。只有实际运行阻塞阶段目标时才定位并修复对应成本；不调大预算掩盖错误，不主动做缓存、池化和并行改造。
- 代码、配置和文档只保留当前采用方案。废弃实验及时撤掉，不建立第二套“备用实现”。

### 10.2 禁止防御性兜底

- 对已经成立的内部合同直接使用；合同破坏时带上下文明确失败。禁止新增宽泛 `catch (Exception)` 后继续、返回空集合、返回零值或跳过候选。
- 缺少玩家、卡牌、模型状态、选择、镜像或 RNG 时，禁止猜测替代对象、默认目标、随机补牌、读取 live 补洞或宣称相等。
- 禁止自动重试、指数退避、层层回退、异常转成功、过期结果伪装为有效结果，以及为假设性风险增加确认流程。
- 保留本任务明确需要的检查：外部配置有效性、原生动作合法性、取消和生命周期所有权、多人状态／RNG 偏差、原生选择等待。这些是真实功能要求，不能以“禁止防御性编程”为由删除。
- 队友改变状态是正常输入。按第 4 节更新预测并继续合法原序列；原版效果未知或内部模拟失败才按既有失败路径终止相应任务。
- 测试清理使用明确的所有权与 `finally`；失败保留原始原因，不能用吞异常的清理代码把测试写成 Passed。

### 10.3 禁止重复测试和无限验证

- 每阶段记录一张证据表：行为源码提交／工作区版本、输入、命令、结果、证据路径和覆盖范围。下一次运行前先查表。
- **同一行为源码、同一输入、同一测试层已通过就停止重跑。** 文档、注释、版本或记录变动不触发同一行为复测；没有编译输入变化且已有构建产物，也不重复构建。
- 失败先读首个错误和最小相关日志，定位根因再修改。禁止原样重试碰碰运气；已查明环境问题时，修复环境后再运行一次。
- 原生差分默认停在第一个需要验证的动作。新增跨回合／Fork 状态才扩展到相应最小生命周期；单卡修复不自动升级为完整战斗。
- 每张卡需要自己的基础／升级覆盖，但通过数据化批次复用测试器。跨座位、2／4 人、网络角色、取消等通用维度挑机制代表，不把全部维度与全部卡牌做笛卡尔积。
- 搜索改动只做目标短搜与一个受影响单人哨兵；最终整合时再运行必要的部署／完整联机场景。不是每改一张卡都跑所有阶段。
- 新源码只重跑受它影响的检查。纯测试器改动先验证该测试器；只有确实影响旧证据可信性时才重做相应证据。
- 单个快速请求默认上限 120 秒。超时先定位阶段，缩小夹具或修复等待；禁止改成 180／360 秒继续盲等。
- 编译、复制、部署、文档检查成功就是该操作的证据。禁止无变化时再做哈希、反射版本、重新解包、重新部署、重新下载或再次完整验证来“确保放心”。
- 多人功能必须完成必要验收；少测、跳过断言、放松差分或只比较总 HP 都不满足省成本要求。目标是一次取得有效证据，而不是制造大量重复成功记录。

## 11. 各阶段的最小实施路线

下面进一步限定实现切口，防止接手者把阶段目标扩展成大重构。文件名是当前入口，不代表必须把新增逻辑堆回同一个文件。

### P0：先把原型跑起来

- 从第 0.3 节继续，修实际阻塞；原生场景通过前保持生产多人门禁。
- 盘点沿“37 张专用卡 → 关联 Power／Hook／生成物 → 遗物／药水 → 普通内容中的多人分支”推进。清单逐项填写类型、原版入口、现有模拟入口、缺口和拟验收机制。
- 用方法调用和字段依赖确认范围，不只搜索 `MultiplayerOnly`。把尚未核对的项标为待核对，不报清单已封闭。
- P0 产物：可重跑入口、运行证据、完整工作清单与已知限制。此时不调搜索评分，不批量建模新卡。

### P1：先修数据所有权，再开放候选

- 从 `CombatRootSnapshot`、`SimulatedCombatState`、`CombatPredictionState`、历史和 `ContinuationStamp` 核对所有单人假设。
- 用“每个玩家都有身份与状态”的正确逻辑扩展现有容器；不要散布第二套本地／远端影子缓存。
- 核对每条跨玩家引用的创建、捕获、Fork、删除与状态键；未发生引用转换不能只补一个字段到快照。
- 先使已有普通牌在多玩家根结算正确，再验证两回合。P0 的原生对端一致性不能代替原版／模拟差分。
- 玩家与敌人目标按原版身份解析。通用目标合法性归模拟边界；随机选哪个队友属于搜索政策，不混进卡牌原版效果。

### P2：按机制小批完成全部建模

- 先做直接伤害／格挡／资源，再做群体与生成，随后做监听型 Power、跨回合、死亡／复活、转移与选择。实际清单决定每批内容，不按未经核对的卡名猜机制。
- 每项只沿实际调用链补缺口。已存在的正确 mirror／spec 复用；基础设施缺口修在通用所有者。
- 每批只有“实现 + 对应差分 + 覆盖记录”，通过就进入下一批，不追加该批的完整整场战斗。
- 最终对照封闭清单逐项收口；禁止用“常见牌都好了”替代完整支持。未知第三方玩法不扩入本批。

### P3：有限搜索与三个结果

- 从 `SearchPolicySnapshot` 冻结多人设置；复用现有请求预算账本，封住升级预算及无限延长回合的入口。
- 只枚举本地动作，队友被动生命周期仍正常推进。先证明单个有限回合结果正确，再增加输出／防守／启动的保留与选择。
- 复用现有评分特征，增加确实缺失的字段；启发式估值不写入真实生命、伤害、状态键或 RNG。
- 三种目标共享一次请求预算。通过有实际差异的场景验收，不要求每场强行凑出三条。
- 最后补余费支援与随机队友目标。分类元数据靠已核对的效果登记；完整重放确认补入动作没有破坏本地方案。

### P4：保留合法动作，更新过期数值

- 从 `SolverController` 的 combat/search/deployment 会话、结果完成入口与逐动作执行入口修改；继续使用原版公开动作入口。
- 把原序列、预测有效期、下一步合法性分开。队友变化走重评估；未知内部错误仍失败，不能由多人宽容逻辑吞掉。
- 先验证本回合手动选择并执行，再接 RNG 提示与自动计算入口。跨回合保存的预测只用于展示，本轮动作执行权限在回合结束时结束。
- 旧根、旧取消源、旧结果和旧回调不能覆盖新会话；用一个最小交错场景证明，不为此建立通用事件系统。

### P5：界面和最终收口

- 结果在主线程转换为 UI snapshot，渲染器只消费只读数据；中英文同时完成。
- 用明确的假设与估值标记表达不确定性，不凭空给置信百分比，也不把未走到的回合显示成已计算。
- 对照第 9 节逐项关联证据，复用前面已通过且仍有效的记录。最终联调验证之前未覆盖的组合，不再从头重跑所有小场景。
- 同步架构、开发笔记、测试矩阵和实际变化的登记文档；本地部署一次并提交。最终汇报已完成功能、实际验证与明确未测项，不把无头数据描述成可见体验。

## 12. 交接与完成记录模板

每次阶段提交或上下文交接只更新下面这些信息，不另写一套平行计划：

```text
当前阶段／子任务：
当前分支及提交：
未提交文件及用途：
本次行为变化：
已通过证据（源码、输入、命令、结果、路径）：
失败／未验证项及具体原因：
下一条实际操作：
实例／进程清理及本地部署状态：
```

只有第 9 节范围全部实现且必要证据齐全，才报告多人适配完成。若遇到真实外部阻塞，报告精确阻塞和已完成部分，继续处理不依赖该阻塞的任务；不能因为单个入口较难就把剩余阶段留给用户安排。

### 2026-09-29 交接记录

- 当前阶段／子任务：P2 原版多人内容分支持续验证，并补 P1 死亡／跨回合证据；P3～P5 既有实现与证据保留。
- 当前分支及提交：`feat/multiplayer`；制造机生命周期探针 `1d3ab35f`，死者回合准备修复随本节提交；实际 HEAD 以接手时 Git 为准。
- 未提交文件及用途：本节提交后无本任务待提交文件；实际状态以接手时 `git status` 为准。
- 本次行为变化：新增四人 `Blaze` 非法目标、双人骑士压制升级牌恢复、知识恶魔外部选牌边界与选择后回合、双尾鼠同伴死亡后召唤、制造机满员及小怪死亡后补位的原生探针；修复模拟在玩家死亡后仍执行其回合准备的问题。
- 已通过证据：游戏 `0.111.0`；相应 Release 构建与 Windows 结构门禁通过。`Blaze` `.local/multiplayer-p2/dead-teammate-blaze-excluded-e22b06175e7f45e7898102c76f9e41e8/peer-0/result.json`；骑士 `.local/multiplayer-p2/knights-dampen-upgraded-7a33347c0eae4f63a7e48b78ac939dcd/peer-0/result.json`；知识恶魔 `.local/multiplayer-p2/knowledge-demon-post-choice-9b9e0925426b40e597846920c9ea90f7/peer-0/result.json`；贪食者四招 `.local/multiplayer-p2/insatiable-four-moves-616a05c3a67c422e8e6318bd3a6f2dff/peer-0/result.json`；双尾鼠召唤 `.local/multiplayer-p2/two-tailed-rat-resummon-43a8f4fd67ef49b291142f95fb097c2b/peer-0/result.json`；制造机满员与补位 `.local/multiplayer-p2/fabricator-minion-refill-cc858a950da44a19ae47fbe31073af28/peer-0/result.json`；ENet 玩家死亡 `.local/multiplayer-p1/enet-player-death-fixed-66898c2c7d5f480a8c87a487d3df1393/peer-0/result.json`、`peer-1/result.json`。内容输入同目录 `input.json`，ENet 输入为 `input-0.json`／`input-1.json`；命令沿用第 8 节无人测试入口并指定 `MULTIPLAYER-CONTENT` 或 `MULTIPLAYER-P0`、对应遭遇。
- 失败／未验证：`CreatureCmd.Kill` 队友后，虚拟多人进入 End 阶段；死者在虚拟回合开始被自动标记结束又连续推进，无法用它稳定验死亡后出牌。当前 `Blaze` 探针只在 Play 阶段直接设 0 HP，未验那张牌与死亡 Hook 的组合；玩家敌方回合死亡已由双进程 ENet 验证。内容闭包、无求解器对端、可见 UI、Steam 邀请、Linux 实机仍未完成。
- 下一条实际操作：核对战斗内多人关联遗物和药水的剩余机制，再补无求解器对端、可见 UI 与 Linux 等第 9 节未验项；不重复已通过输入。
- 实例／进程清理及本地部署：本轮各无头实例由运行脚本清理；五个文件已精确覆盖到 `D:\Steam\steamapps\common\Slay the Spire 2\mods\CombatSolver`。双尾鼠测试源码随后重建了 DLL，结束本批前需再覆盖最终 DLL。未提升版本、发包、打标签或推送。
