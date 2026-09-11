# 性能重构交接（2026-09-11）

本页是为用户新增的额度交接要求预先准备的检查点，不代表整项重构完成。分支 `perf/simulation-profile-20260910`。上游 `c2cc463` 已于 `136365c` 合并；书页风暴批次已验证通过，当前代码以本文件所在提交为准。`766fb27` 是较早的 52 种卡牌检查点。

用户要求：**检测到周额度剩余不超过 1% 时，提交当前更改，写清目标、进度、验证和剩余工作，停止开发并交给另一位 AI。** 2026-09-11 12:58:42 UTC 的最新本地账号额度记录为周窗口已用 97%、剩余约 3%，尚未触发，继续开发并在后续批次检查。应用账号查询接口本次未返回，因此采用该近期记录；这不是未来额度保证。使用额度数据时，`usedPercent`／`used_percent` 是已用比例，剩余为 `max(0, min(100, 100-usedPercent))`；周窗口长度为 10,080 分钟。缺失或查询失败属于未知，不能记作 0%。不要自动购买额度或消耗重置。

## 1. 用户目标与当前授权

- 完成整项战斗模拟与搜索性能重构，同时保证代码质量和游戏决策质量。期望正常完整战斗有显著加速，2–3 倍即可满意；10 倍不是硬指标。不能通过裁剪样例、缩小搜索预算、删除药水审计或降低决策质量达标。
- 正常完整 `VeryHigh / DOP8 / NoGC 16 GB` 是最终性能口径。固定节点和纯值内核实验用于开发验证，不代替该口径。
- 用户已明确只做 **headless**，不要启动可见 Steam 会话；这覆盖仓库关于最终可见性能测试的默认规则。用户还在另一台电脑玩游戏，不要打断其会话。
- 普通源码、测试和文档直接提交当前分支。用户随后明确要求先拉取并合并上游最新更改，本次目标为 `upstream/main c2cc463`；当前仍没有创建 PR、推送、安装、打包、发布或改写远端分支的新增授权。用户之前问“完成百分之多少、距离提 PR 还有多少”，这是状态询问，不是发布命令。
- 上次约 65% 的回答是交付工作量的粗估，不能当作统计覆盖率或可靠工期。剩余工作仍大，尤其动态效果闭包与最终正常性能验收。
- 阅读根目录 [AGENTS.md](../../AGENTS.md)，按任务使用 [战斗语义 skill](../../.agents/skills/combat-semantic-change/SKILL.md)、[搜索性能 skill](../../.agents/skills/search-performance-optimization/SKILL.md) 和 [架构 skill](../../.agents/skills/architecture-boundary-refactor/SKILL.md)。不擅自启用子代理。

## 2. 已完成的整体工作

最早一轮 P1–P6 已完成并保留回合检查点优化，见 [历史结果](simulation-refactor-result-20260910.md)。**它不是当前更大范围重构的完成报告。** 当前目标来自后续 [大幅重构方案](simulation-redesign-research-20260910.md) 的 R0–R4，采用紧凑权威值状态、可撤销执行、可恢复选择和不可变候选。

当前已具备：

1. 主线程冻结完整根、精确能力闭包准入；独占 worker 工作区执行与撤销；冻结候选持有值数据而非可变 Model；完整读取器用于原估值、键与续用。
2. 有序卡牌指令、付款和 X 值、弃牌／消耗／回收、嵌套 Sly、生成卡逐实例身份、随机费用、关键字和迅速附魔；多次选择可挂起恢复。
3. 生物和基础能力完整生命周期、持续与临时能力、独立神气制胜实例、伤害和终局阶段；奥斯蒂承伤、死亡、复活、攻击、每回合召唤；完整回合和机甲骑士 AI。
4. 原始机甲 30 牌完整输入已进入生产紧凑后端；搜索结果、原生整场部署与 T2–T8 精确续用已有直接证据。未知域在求解前选择旧模型后端，执行中不得静默回退或混用两个可变对象图。
5. 当前精确准入 **53 种卡牌类型**，名单以 [编译器](../../src/Prediction/Compact/CompactCardProgramCompiler.cs) 为准。此数字不是原始亡灵输入的全部动态闭包比例。

关键实现入口：

| 入口 | 当前责任 |
| --- | --- |
| [SearchBackendPolicy](../../src/Runtime/SearchBackendPolicy.cs) | 主线程整根后端选择和原因日志 |
| [CompactDiscardProjection](../../src/Prediction/Compact/CompactDiscardProjection.cs) | 根捕获、精确类型／Hook 准入、读取模型与来源历史投影 |
| [CompactCombatRoot](../../src/Prediction/Compact/CompactCombatRoot.cs) | 紧凑根和工作区接线 |
| [CompactCardProgramCompiler](../../src/Prediction/Compact/CompactCardProgramCompiler.cs) | 原生卡牌到有序值指令的封闭编译 |
| [ResumableDiscardProgram](../../src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs) 及其 partial | 付款、卡牌／选牌执行位置、抽牌、自动牌；其他文件分管回合、能力、怪物等阶段 |
| [ReversibleValueBuffer](../../src/Engine/InCombat/Simulation/Compact/ReversibleValueBuffer.cs) 与 [ReversibleValueState](../../src/Engine/InCombat/Simulation/Compact/ReversibleValueState.cs) | 可增长值存储、撤销及不可变冻结 |
| [BasicPowerLayout](../../src/Engine/InCombat/Simulation/Compact/BasicPowerLayout.cs) 与 [PanachePowerLayout](../../src/Engine/InCombat/Simulation/Compact/PanachePowerLayout.cs) | 普通四槽能力状态和可增长独立能力实例 |
| [CompactDiscardReadView](../../src/Prediction/Compact/CompactDiscardReadView.cs) | 原搜索政策的完整只读查询与估值 |
| [CompactReplay](../../src/Search/CombatBeamSolver.CompactReplay.cs) | 原 Beam／选择政策下的紧凑重放 |
| [CompactPetCardRoutes](../../src/Testing/UnattendedTestRunner.CompactPetCardRoutes.cs) | 最近批次共享原生、旧引擎与紧凑路线差分 |

## 3. 最近六个已提交批次

| 提交 | 内容与证据 |
| --- | --- |
| `285af0d` | [吊杀](simulation-hang-20260911.md)：卡牌来源倍率和力量削减层数 |
| `edfacdb` | [动态关键字](simulation-keywords-20260911.md)：雕琢打击、响指和逐实例标记 |
| `87447c0` | [出牌前能力／迅速](simulation-card-hooks-20260911.md)：灰烬之灵、死亡之舞、有序费用查询、一次性附魔；修正旧死亡之舞读取已支付资源而非当前费用的问题 |
| `3da9cce` | [致死性](simulation-lethality-20260911.md)：攻击开始计数与倍率；修正旧历史课程从 live 回合读取过期攻击历史，冻结当前／上一回合窗口 |
| `22703c0` | [神气制胜](simulation-panache-20260911.md)：独立实例、首次应用、完整来源与终局分派；修正旧实例错误叠加及待失败时补算新中毒回调 |
| `766fb27` | [命运同担](simulation-shared-fate-20260911.md)：先玩家后敌人的力量削减、人工制品及负力量重新获得；修正紧凑持续标记误用请求量类型 |

最新批次已完成的证据：

- `COMPACT-SHARED-FATE-NATIVE`：`c1c9ad4a063e4bd0be0d0f98212678c1` Passed，30.10 秒（含启动）。三种根，21 次原生动作、42 分支、18 个省略选择边界、六次完整回合，三个迅速原生选择现场。
- `COMPACT-SHARED-FATE-SEARCH`：`7e507c349fa34294ab2cf68f0d578dab` Passed，5.00 秒。固定 250 节点旧／新 DOP1、新 DOP2 完整等价，实际并发 2；另验证取消、异常排空、根复用和未知药水拒绝。
- 纯值合同整套通过；Release v3 11.22 秒、零警告／错误；Linux 结构门禁通过（93 个 Search 文件）。PowerShell 对应规则已同步但未执行。未改旧 registry 分类，因此未运行 CoverageCatalog verify。
- 上述结果和原生样本已提交在 [结构化证据](simulation-shared-fate-20260911.json)。首次失败基线也保留，不能删除或改写成从未失败。
- 每个语义候选比较完整 Snapshot、逐实例能力字段与顺序、有序牌堆、九 RNG、来源历史、全部估值、原 StateKey、ContinuationStamp；覆盖撤销、逆序恢复、八工作区及实机推进后重新计算冻结根。保持比较器严格。

## 4. 性能结论的真实边界

[生产运行时报告](simulation-runtime-backend-20260911.md) 中，原机甲输入正常 NoGC 首次搜索为模型 **7.209 秒**、紧凑 **7.164 秒**，worker 分配 **5.654 → 2.976 GB（约 −47.4%）**。两次实际进入 NoGC、均无 GC、逻辑工作和路线一致。

这证明分配下降，**尚未证明正常 NoGC 下的大幅加速或 2–3 倍目标**。此前普通 GC 环境约两倍的结果不能外推；结果时内存不是峰值 RSS；headless 不能证明可见帧流畅度。最新卡牌夹具的 5 秒／30 秒包含不同准备和验证，不是性能基准。

## 5. 必须继续完成的范围

### 5.1 原始完整亡灵输入与动态闭包

原输入是 `NECROBINDER / AEONGLASS_BOSS`，种子 `SEARCH_PERF_NECROBINDER_POTION`，A10、act index 2、敌人 HP526、玩家 41/76 HP。必须保留 38 张牌、19 件遗物注入（另保留原生初始遗物，实测共 20 件）、两瓶药及 `RequireAtLeastOne` 药水政策：

- [原始卡牌及附魔](../../coverage/unattended/search-performance-necrobinder-projected-run-cards.json)
- [原始 19 遗物](../../coverage/unattended/search-performance-necrobinder-projected-relics.json)
- [原始两瓶药](../../coverage/unattended/search-performance-necrobinder-projected-potions.json)：`GAMBLERS_BREW`、`COLORLESS_POTION`
- [原始完整命令与采样记录](simulation-profile-20260910.json) 中 `cases.necro.runs.cpu-necro.command` 可恢复建局参数；采样专用开关与 profiler 时间不作为最终基准。

书页风暴已经完成，原牌组仍缺 **CALL_OF_THE_VOID** 的编译、随机生成与相关 Hook／Power。随后仍需审计和迁移所有可达生成池（包括无色药水）、自动出牌、遗物内部状态和监听顺序、两瓶药及各药水审计、AEONGLASS AI／意图／隐藏状态／召唤与终局。不能把“补齐剩余一张牌”误认为闭包完成。

当前遗物准入仅 `ToughBandages / TheAbacus / RingOfTheSnake` 及符合回合／奥斯蒂条件的 `BoundPhylactery`；非空药水槽全部拒绝。原输入的 19 遗物不在该列表：`NEOWS_BONES, LARGE_CAPSULE, ORNAMENTAL_FAN, CLOAK_CLASP, POMANDER, JOSS_PAPER, JUZU_BRACELET, CANDELABRA, GORGET, BONE_FLUTE, BIIIG_HUG, FESTIVE_POPPER, TINY_MAILBOX, VAJRA, BRILLIANT_SCARF, RIPPLE_BASIN, PRAYER_WHEEL, WAR_PAINT, FUNERARY_MASK`。须分别确认哪些仅影响已完成的局外准备、哪些影响未来战斗；有原生证据后才能准入。

### 5.2 最新完成批次与下一步

书页风暴已完成，精确卡牌闭包现为 **53 种**。见[实现及直接结果](simulation-pagestorm-20260911.md)和[结构化数据](simulation-pagestorm-20260911.json)。三根两回合 24 原生动作／63 分支／32 挂起、250 节点旧新串并行和共享抽牌回归通过。抽牌栈、父返回、递归洗牌／来源／Slither、Swift／Sly、动态虚无与九层纯值均已验证；完整正常 NoGC 性能尚未重测。

合并前备份 stash `78fece6d99ffa440d8e509f7548164846bcdf020` 已恢复并整合。它只是旧检查点，**不要重新应用或把它当作更新版本**。本批 v2–v9 失败、夹具修正与真实缺口在结果文档中明确记录；尤其 v4 使用旧产物，不算有效新代码验证。

完整生成池已完成只读审计，见[直接结果](simulation-generation-audit-20260911.md)。CallOfTheVoid 有 78 个候选，仅 19 个类型已准入；无色药水有 50 个候选，仅 3 个已准入。两池直接缺 106 个类型，尚不含进一步生成链；早期约 65% 的工作量估计不能继续用作剩余规模依据。原生每次生成前都洗乱完整池：该输入分别消耗 77／49 次 RNG；24 次选择与旧引擎完整五字段一致。根因非空药水首先显式拒绝。当前尚未扩充紧凑准入，下一步先冻结完整角色生成池，随后完成通用随机生成值执行和全部可达效果。

CallOfTheVoid 已读原生卡牌与能力，尚未实现：本机 `.local/compact-lethality-20260911/native/CallOfTheVoid{,Power}.cs`。它在 BeforeHandDraw 从主人角色的解锁池排除 Basic／Ancient，每次用 CombatCardGeneration RNG 独立生成一张，赋虚无，再把这一批加入手牌；不同次数允许重复。升级只加 Innate。已定向核对 CardFactory.GetDistinctForCombat／TakeRandom／UnstableShuffle 及 AddGeneratedCardsToCombat：先生成整批并赋虚无，再逐张记录 Generated、入堆、AfterCardGeneratedForCombat；满手转弃牌，生成不触发 AfterCardDrawn。下一步须把这些顺序写入紧凑执行并做原生行为差分；它不是只加一个 Power 枚举即可完成。原始亡灵池与无色药水的全部可达效果仍须保持合法，不准静默缩池或只选择已支持牌。

### 5.3 完整验收与 PR 准备

1. 两份原始完整输入和所有可达效果通过准确准入。机甲输入见 [维护 fixture](../../coverage/unattended/performance-veryhigh-mecha-native.json)，保留 30 牌、升级／附魔、蛇之戒和 Smart 政策。
2. 原算法下固定节点旧／新 DOP1、新 DOP2 的完整逻辑与路线等价；并行取消／故障排空、挂起续执行和根复用不退化。
3. 两场完整 headless 原生部署，Instant／0 秒，验证逐回合完整状态、精确续用和零计划外重算。已通过且未受后续改动影响的证据可复用，不逐小批重复整场。
4. 最终候选冻结后，运行预先固定的预热交错 A/B；原完整 VeryHigh／DOP8／正常 NoGC16GB，无预算覆盖、无简化输入、无增量诊断。每请求不超过 120 秒，测完整 coordinator（包括药水审计），保留全部样本，比较路线／战损、逻辑与物理工作、分配、峰值 RSS、GC。只在新变化或失败影响结论时追加测试。
5. 若正常 NoGC 仍未加速，根据实际热点继续优化执行／读取／冻结成本；不要用分配下降替代速度证据，也不要只写结项说明。必要架构调整以正确性和直接性能证据决定。
6. 汇总最终范围、性能收益和未迁移域，整理提交与 reviewer 可用说明；推送／创建 PR 等在用户授权范围内处理，不能从本交接自行推断授权。

## 6. 开发验证和本机恢复

文档／skill 用 L0；新语义优先 L1，新增跨回合或 Fork 状态用 L2。固定小搜索一般 250 节点，单请求上限 120 秒。完整场景留给最终质量／性能结论。同一行为、输入和产物已通过时不为安心重跑。

```bash
dotnet build CombatSolver.csproj -c Release
dotnet run --project tools/CompactCreatureChecks/CompactCreatureChecks.csproj -c Release
./tools/verify-refactor-boundaries.sh
```

最近卡牌原生／搜索 fixture 使用以下形状；`<artifact>` 为同批 Release DLL 与 manifest 独立目录，`<evidence>` 为该请求的证据目录：

```bash
./tools/run-unattended-test.sh --scenario-id COMPACT-SHARED-FATE-NATIVE \
  --character-id SILENT --encounter-id MECHA_KNIGHT_ELITE --enemy-current-hp 300 \
  --timeout-seconds 120 --headless-instance compact-full-route-20260911 \
  --combat-solver-build-dir <artifact> --evidence-directory <evidence>
```

搜索场景替换为 `COMPACT-SHARED-FATE-SEARCH`；夹具自行注入手牌和能力，不是原始整场建局。Windows 维护对应 PascalCase 参数入口；修改协议或结构门禁时同步 `.sh`／`.ps1`，没有执行的一端明确记录未验证。

本机资料供当前工作区接手者定位，不作为其他机器的固定配置：

- checkout：`/home/ltlly/Code/nmslmod/.tools11/CombatSolver-open-source`；外层目录不是目标仓库。
- 最新只读审计产物 `.local/compact-random-generation-20260911/artifact`（Release v5 零警告错误），结果 `audit-v5`。上一语义产物 `.local/compact-pagestorm-20260911/artifact`，原生／搜索结果为 `native-v9`、`search-v9`，共享回归为 `draw-exhaust-regression-v9`；对应日志为 `.local/compact-pagestorm-*.log`。
- 检查点时任务拥有的 headless 实例 `compact-full-route-20260911`、PID `1846284` 记录为 READY，加载生成池审计 v5 DLL；结果本身标记 processReusable=false，后续以实例协议实际状态为准。PID 仅是记录，使用前须通过实例协议确认所有权，不能按过时 PID 杀进程。
- **重编译后先停止持有旧 DLL 的本任务实例**：`./tools/run-unattended-test.sh --headless-instance compact-full-route-20260911 --stop-instance`。不要用不带正确 artifact 的 `--stop-owned-process` 替代，此前会触发无用游戏副本复制。重新启动时传入本批 artifact。
- 本机磁盘空间紧：测试游戏快照约占 2 GB，最近约剩 1 GB。确实需要回收时，确认本任务实例已退出后，只删除其 `/home/ltlly/.local/state/CombatSolver/headless-instances/compact-full-route-20260911/game` 快照，保留原游戏、存档、其他实例和证据。
- 当前支持游戏 `0.111.0 / 41cef1ea`；本机原生 DLL 在 `/home/ltlly/.local/share/Steam/steamapps/common/Slay the Spire 2/data_sts2_linuxbsd_x86_64/sts2.dll`。旧文档提到的 `.local/decompiled/sts2-v0.111.0` 在本机不存在。优先查已有 `.local/compact-*/native/`，避免反复反编译。
- 现有 ILSpy 入口：`DOTNET_ROLL_FORWARD=Major .local/dotnet-tools/ilspycmd -t <完整类型> <sts2.dll>`（9.1.0.7988，本批重新安装的忽略开发工具；旧 `.tools/ilspy` 路径已失效）。在目标仓库工作目录写本批 `.local`；反编译输出可能带工具版本建议，不提交原生源码。

## 7. 容易重踩的正确性边界

- 原生 `Power.Type` 与 `GetTypeForAmount` 用途不同：负力量／敏捷的请求能被人工制品阻止，但能力类型仍为 Buff，不能给它们创建 Debuff 的首次持续跳过标记。
- 不同 Hook 的终局门禁不相同。`AfterCardPlayed` 是原生明确允许的完成回调；卡牌击杀最后敌人后神气制胜仍完成计数。一般回调则应核对原生派发入口，已开始的一次派发也不能随意逐监听器重查门禁。
- 神气制胜有多个独立实例，`AlreadyApplied` 会影响下一张牌和状态等价；必须进入完整键与续用。普通 Power 和独立实例共用获得顺序。
- 读取模型只导入紧凑值，不能执行原生 Hook 或产生第二套权威可变状态。来源历史必须对应原卡牌／附魔／能力的方法嵌套，不随便用类型名替换模型 ID。
- 正在出牌、Swift、Sly、洗牌选择和回合起手都可能挂起；付款、X、关键字、附魔 Disabled、RNG、父子执行位置要随同候选冻结与撤销。
- 旧 StateKey 数值参与确定性排序，不能换个哈希再声称原政策完全等价；不能通过删字段、放宽比较器或吞异常让测试通过。
- 卡牌和 Power 中文名称从当前游戏本地化或 `NativeAction.CardTitle` 核对，不凭英文翻译。技术研究可以用精确英文类型标识。

新增行为提交须同步相关架构地图、第三方封闭登记点、skill、结构门禁、开发笔记、测试矩阵和结构化证据，范围以实际变化决定。完整资料从 [文档索引](../README.md) 进入。

## 后续检查点：上游合并

已整合 `upstream/main c2cc463`（0.36.0）并完成最小原生／固定节点验证，见[合并记录](simulation-upstream-merge-20260911.md)。以上旧性能数字只代表合并前实现。单一 `Profile`／`FixedBudget` 取代旧 Short／Deep 字段，新性能基线须固定本次上游政策。Pagestorm 进行中改动保存于本地 stash `78fece6d99ffa440d8e509f7548164846bcdf020`，本次合并通过证据不覆盖该改动；已在 `136365c` 合并提交后恢复全部 15 个文件，并保留备份。书页风暴随后已通过验证，后续以第 5.2 节和最新提交为准。
