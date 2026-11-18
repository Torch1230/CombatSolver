# 2026-11-18 PR 合并验收

## 重复卡牌与有序手牌

`ORDERED-HAND-DUPLICATES-FIRST` / `ORDERED-HAND-DUPLICATES-SECOND` 分别执行两处相同打击，再执行未升级坚毅。合同核对完整状态、随机流、续用、父分支及 Fork 隔离，并检查普通枚举、开局能力牌和循环替代动作。

```text
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId ORDERED-HAND-DUPLICATES-FIRST -EnemyCurrentHp 100 -TimeoutSeconds 120 -CleanupInstanceOnExit
./tools/testing/run-unattended-test.sh --scenario-id ORDERED-HAND-DUPLICATES-FIRST --enemy-current-hp 100 --timeout-seconds 120 --cleanup-instance-on-exit
```

将 ScenarioId 改为 `ORDERED-HAND-DUPLICATES-SECOND` 可执行后一张打击。失败基线、固定搜索对照和未验证项见[重复卡牌记录](../issues/ordered-hand-duplicates.md)。

## 地精佣兵的基础金币奖励（#239）

`GREMLIN-ESCAPE-GOLD` 构造未偷钱、胖地精 18 HP、3 张打击与 3 点能量的同根场景。关闭组合搜索与药水，固定 256 节点 / 1500 ms。先跑“放走”哨兵，再跑“保牌/保钱”，导出完整根戳、动作、战损、用药和工作量。

2026-11-15，游戏 0.113.1 / RitsuLib 0.6.13 / Windows：

启动器沿用本机 Mod 栈，另含：

- DamageMeter 1.14.7。
- ModConfig 0.2.3。
- RemoveMultiplayerPlayerLimit 0.0.5。
- Rewind 0.26.19。
- RouteSuggest 1.9.0。

以下场景使用原生卡牌和怪物。尚未在仅安装 CombatSolver 与 RitsuLib 的环境验证。

| 政策 | 修改前 | 修改后 | 展开 / 转移 | 搜索耗时 |
| --- | --- | --- | --- | --- |
| 放走哨兵 | EndTurn，0 战损、0 药水 | 同前 | 1 / 2 → 1 / 2 | 594.11 → 493.15 ms |
| 保牌/保钱 | EndTurn，损失基础奖励 | 3 张攻击牌击杀，0 战损、0 药水 | 1 / 2 → 3 / 6 | 3.52 → 95.86 ms |

两次运行的完整根戳和预算一致。目标修复增加 2 个展开节点与 4 次转移；哨兵动作、质量与工作量保持。耗时各取 1 次，包含不同预热状态，不据此宣称整搜提速。

原生游戏实际完成 3 次攻击；前 2 次动作的完整续用状态与预测一致，末击确认胖地精死亡、基础金币比例为 1。中英文及繁中回退的奖励损失提示合同通过。

| 原生合同 | runId | 结果 / 总耗时 |
| --- | --- | --- |
| 击杀路线与政策哨兵 | `3471e67c95744662ba6d8e491b591325` | Passed / 25.64 秒 |
| 无偷钱逃跑，金币比例 0.5 | `23ebc34b8e274f098795b3cd5b713b66` | Passed / 23.00 秒 |
| 已偷钱逃跑，金币比例 0 | `a73833b8d6c64c3d85f5e1e0ae276b33` | Passed / 24.99 秒 |
| 既有盗窃政策与早停 | `bd527e08ccc243cf9a3165af9c3ec894` | Passed / 26.96 秒 |

两种逃跑合同均完成原生完整续用、状态键和父分支/Fork 隔离对账。所有隔离实例均已清理。

```text
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId GREMLIN-ESCAPE-GOLD -EncounterId GREMLIN_MERC_NORMAL -EvidenceDirectory .local/gremlin-gold/search -TimeoutSeconds 120 -CleanupInstanceOnExit
./tools/testing/run-unattended-test.sh --scenario-id GREMLIN-ESCAPE-GOLD --encounter-id GREMLIN_MERC_NORMAL --evidence-directory .local/gremlin-gold/search --timeout-seconds 120 --cleanup-instance-on-exit
```

场景 ID 可替换为 `GREMLIN-ESCAPE-GOLD-STATE` 或 `GREMLIN-ESCAPE-GOLD-STOLEN`，分别复跑两种逃跑差分。路径可通过 `Sts2GameRoot` 和 `RitsuWorkshopRoot` 覆盖。本机验证使用 PR #247 的启动器。原始 0.111.0 玩家包仅通过材料预检查，未完成原包恢复、搜索或部署；该记录不替代原包回放。

2026-11-17，评审修正后的原生请求 `ab220ac856a142aba2795db5d343656d` Passed，28.13 秒。与原 PR 最终样本的完整根、政策预算、动作、战损及用药一致。放走仍展开 1 / 转移 2，保钱仍展开 3 / 转移 6。两次中间原生续用及击杀后完整奖励通过，实例已删除。单次搜索耗时分别为 565.23 / 115.44 ms，旧样本为 493.15 / 95.86 ms；未据此通过稳定耗时门禁。更大场景及保序突变键的独立负对照尚未验证。

## 结果诊断的 Windows 进程内存读取

2026-10-22 [结果诊断内存读取](../performance/diagnostic-process-memory.md)的确定性原生布局／映射、失败恢复、并发及生产日志成功／失败合同通过，独立查询基准与既有实机证据分开记录。

## 独立检查与 CI

`python -B tools/testing/run-independent-checks.py` 执行 25 个独立 .NET 检查，依赖 Python 3 和 .NET 9 SDK。Windows/Linux 使用同一入口。单项复跑可追加检查名。

入口先扫描 `checks/*/*.csproj`，检查每个项目是否登记。新增项目未登记、已登记项目被删除或项目布局改变时，先报错，不启动 .NET。`--check-catalog` 可单独执行这项检查。

排除项有 3 个：`AdaptedOnPlayChecks` 依赖游戏的 Harmony；`PowerAmountComparisonChecks` 依赖游戏、Godot、Harmony 和 RitsuLib；`PerformanceRecordingTests` 需要输出目录或 trace、运行模式，不能无参数执行。

修复前，卡牌 hook 和回合阶段检查缺少 Fork 合同，无法编译；诊断日志检查仍假定只有 1 条进程事件。回合阶段替身还缺少当前 Power 和未支持异常边界。修复后断言 4 条生命周期事件及冻结前缀，保留战斗日志隔离和过载检查。

2026-11-15，本机 Windows / .NET SDK 9.0.321：全组执行通过 24 项；补齐最后一个回合阶段合同后，定向复跑通过 30 条断言。卡牌 hook 检查含 78 条断言。

同日，GitHub Actions 的 Windows 与 Ubuntu 均完整执行 25 个项目，各为 `total=25 passed=25 failed=0`。运行记录见 [Independent checks](https://github.com/EEEEEEEEdison/CombatSolver/actions/runs/43688960839)，行为源码为 `6d29975c`。游戏内战斗未复测。

`.github/workflows/independent-checks.yml` 在两个平台执行这些检查。独立替身只验证链接的生产边界，不代替游戏内语义、可见 UI 或战斗路线验收。

2026-11-17，登记扫描识别 25 个执行项和 3 个排除项。临时目录合同覆盖完整登记、未分类项目、缺失项目及异常布局，4 项通过。新入口定向执行 `CardHookReceiverChecks`、`DiagnosticLogTests`，2 项通过；本轮未在本机重跑其余 23 项。

## 同名牌的洗牌排序分配（#241）

`NATIVE-CARD-ORDERING` 覆盖 ABUNDANCE、DEFEND_IRONCLAD、FINESSE、OFFERING 的 0/1/2/3 个动态变量。
原生牌序、完整 RNG、费用、重放、变量值、实机推进及父子分支隔离均通过。
后续排序重新读取变量值，重复引用保留原身份。

2026-11-15，Windows / 游戏 0.113.1 / RitsuLib 0.6.13：
runId `f72bd5fd308d48c9a3c2d7e502673365`，Passed，26.35 秒，隔离实例已清理。
启动器使用 PR #247 的兼容性修改。
本机另加载 DamageMeter、ModConfig、RemoveMultiplayerPlayerLimit、Rewind、RouteSuggest。

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId NATIVE-CARD-ORDERING -EncounterId FUZZY_WURM_CRAWLER_WEAK -TimeoutSeconds 120 -CleanupInstanceOnExit
```

8 张富足、42 张牌的固定根及循环路线哨兵均完成 A1/C1/C2/A2 独立进程对照。
完整根、政策、动作、续用、质量和剪枝计数一致，无时间截断。
目标搜索分配约减少 8.1%，尚未证明稳定提速或原报告崩溃已修复。
命令、逐次数字及原生与离线验证的范围见[记录](../performance/shuffle-variable-names-20261115.md)。

## 预战 worker 空闲取消源

`PreCombatRequestChecks` 直接编译 API、contracts 与 `PreCombatForecastWorker.Idle.cs`。
游戏状态、进程和会话数据使用替身；计时、取消、释放及等待屏障使用生产代码。

2026-11-15，Windows / .NET SDK 9.0.321：
修改前，并发用例确认 `Cancel` 执行中取消源已被释放。
修改后，18 项检查全部通过。

新增 7 项覆盖：

- 当前计时器到期只停止所属会话。
- 取消计时器后保留可复用会话。
- 取消正在等待请求屏障的计时器。
- 旧任务清理保留替代计时器与会话。
- 取消执行中，后台清理不释放取消者持有的源。
- 任务启动前取消并释放后，仍可使用提前捕获的 token。
- 无限空闲期限取消旧计时器。

```sh
dotnet run --project tools/testing/checks/PreCombatRequestChecks/PreCombatRequestChecks.csproj -c Release
```

主工程 Release 构建通过，0 个警告、0 个错误。
本轮未启动真实预测子进程，也未验证 Linux 运行。
本结果不用于归因 #212 的原始崩溃。

## 完整胜利严格战损见证与已付药水成本下界（2026-11-17）

- 离线宿主单元合同（STS2 `0.113.1` / `RitsuLib 0.6.14`）：`--check-primary-incumbents` `20` 项、`--check-boss-tempo` `12` 项、`--check-early-turn-continuation-bound` `143` 项均 Passed。
- 原生无头合同（120 秒上限、退出清理隔离实例）：
  - `POTION-COST-INCUMBENT`（`IRONCLAD` / `NIBBITS_WEAK`，覆盖未付精确层保留以及 `BossTempo` 等 HP／晚回合保留）：`runId` `58e020a137c44e579b82260cd111633d` Passed（`26.71 s`）。
  - `ZERO-CREDIT-GROWTH-PROOF`（`NECROBINDER` / `FUZZY_WURM_CRAWLER_WEAK`）：`runId` `4d01960c36004369bd7f9e57451e85d0` Passed（`26.45 s`）。
  - `ZERO-CREDIT-GROWTH-PROOF-REGEN`（`NECROBINDER` / `NIBBITS_WEAK`，`Regen=3` 上界 `6`）：`runId` `367d96f439d049b3a7b276759a11ef69` Passed（`26.75 s`；前次使用 `FUZZY_WURM_CRAWLER_WEAK` 因先手蓄力后继展开超过 `500` 节点未完成而失败）。
  - `COMPONENT-HEALING-BOUND`（`IRONCLAD` / `FUZZY_WURM_CRAWLER_WEAK`）：`runId` `7904304d5ceb42659a429fc9864ab9a6` Passed（`27.39 s`）。
- 离线搜索宿主成对 `A1/C1/C2/A2` 哨兵（`Coordinator` / `--use-portfolio` / `High` / `nodes=5000` / `DOP=1`，基线 `main` `78e4545e` 对照当前 PR）：`shared-growth-incumbent-reuse.json` 两侧均为 `63` 展开、`149` 转移、`projectedBattleHpLost=0`、`score=10002274994`（`wallSeconds` 基线 `0.8880/0.9425 s`、候选 `0.8927/0.8993 s`）；`smart-audit-potion-baseline.json` 两侧均为 `19` 展开、`36` 转移、`projectedBattleHpLost=0`、`score=10000899996`（`wallSeconds` 基线 `0.8514/0.8540 s`、候选 `0.9324/0.9187 s`）。本次未重测整场可见 Steam 会话以及原版 4 根问题包全量基准。

## 负伤害截零与回复通知以及 Ritsu 边界审计（2026-11-17）

- `tools/inspection/HealingSourceAudit` schema 8 对 STS2 `0.113.1`（`sts2.dll` SHA-256 `c2b866e7c789860700ce8ae05ba88b69da7b93861cd49df096a8a9a54f97bdbf`）完成构建以及扫描：`nativeHookModelTargetCount=189`、`nativeHookBodyCount=189`、`virtualModelSlotCount=9423`。
- 原生无头合同（STS2 `0.113.1` / `RitsuLib 0.6.14`，120 秒上限、退出清理隔离实例）：
  - `HP-LOSS-CLAMP`（`IRONCLAD` / `FUZZY_WURM_CRAWLER_WEAK`，`--verify-prediction-failure-boundaries --verify-combat-root-snapshot --stop-after-combat-root-snapshot-assertion`）：`runId` `f9eb7c8962fb4785b0188267c7adf4df` Passed（`26.23 s`，`NativeCases=168`，包含 4 项 `ExecutionFinished` 实例受众检查与 `CombatRootSnapshot`）。
  - `CARD-NOTIFICATION-BOUND`（`IRONCLAD` / `FUZZY_WURM_CRAWLER_WEAK`，`--verify-combat-root-snapshot --stop-after-combat-root-snapshot-assertion`）：`runId` `206f336a3ed74a1b879f93f63f0bfc07` Passed（`27.67 s`，验证未改动 `IRONCLAD` 活体 UI 放行、原生 `BULLET_TIME` 触发 `StarCostChanged` 加 2 点 HP 拒绝、`Refusals=56` 与 `CombatRootSnapshot`）。
  - `NATIVE-HEALING-CALLBACK-BOUNDARY`（`REGENT` / `FUZZY_WURM_CRAWLER_WEAK`）：`runId` `6a5b4115c6e24461a150db948bcfa2e8` Passed（`26.51 s`，包含 `FoolCardPool` 生成池过滤以及 `ritsu-heal-version` 仅撤销证书检查）。
  - `NATIVE-HEALING-CAPABILITY-BOUNDARY`（`REGENT` / `FUZZY_WURM_CRAWLER_WEAK`）：`runId` `922f0e42c329492f9f218bb3859d2e7f` Passed（`25.64 s`，包含 `ritsu-capability-version` 仅撤销证书检查）。
  - `PLAYER-POTION-CALLBACK-BOUND`（`IRONCLAD` / `FUZZY_WURM_CRAWLER_WEAK`）：`runId` `74052f919c9549cca843d765612780f9` Passed（`26.60 s`）。
  - `HEALTH-CALLBACK-BOUND`（`FOOL` / `FUZZY_WURM_CRAWLER_WEAK`）：`runId` `d7eab8ddfcce4f0f84afbe9a6b6ec5c1` Passed（`26.12 s`，包含未改动 `FOOL` 活体 UI 放行、`NFoolCombatUi` / `SecondaryResourceCombatUiStateTracker` / `RitsuDebugToolsPanel` 与 5 项补丁拒绝）。
- 离线搜索宿主成对 `A1/C1/C2/A2` 哨兵（`Coordinator` / `--use-portfolio` / `High` / `nodes=5000` / `DOP=1`，基线 `main` `78e4545e` 对照当前 PR）：`compare_results.py` 在 `688` 项比较字段上逐字段一致（`mismatched_roots=0`）；`fool-hooks` 为 `247` 展开、`882` 转移、`projectedBattleHpLost=0`、`score=10002309992`（`wallSeconds` 基线 `0.9673/0.9594 s`、候选 `0.9980/1.0052 s`），`smart-audit-potion-baseline` 为 `19` 展开、`36` 转移、`projectedBattleHpLost=0`、`score=10000899996`（`wallSeconds` 基线 `0.7329/0.7346 s`、候选 `0.7866/0.7963 s`）。本次未重测整场可见 Steam 帧时间。

## 镜像监听后缀投影与牌堆接收者索引（2026-11-17）

- 原生合同 `MIRRORED-HOOK-FILTER`（STS2 `0.113.1` / `IRONCLAD` / `FUZZY_WURM_CRAWLER_WEAK`，同步补齐 `MirroredHookMask.AdditionalCallbacks` / `ShouldTakeExtraTurnEarly` 等位 `1UL << 63` 合同方法名列表后运行，120 秒上限、退出清理隔离实例）：
  - 常规模式：`runId` `fb38742ce33f46db9e434707da1b3057` Passed（`30.60 s`，`ProjectedBuilds=7`、`Comparisons=9`、`PileCacheChecks=22`、`EmptyRecheckChecks=58`、`GetterPatchFallbackAndLiveStampInvalidation`）。
  - 双轨验证模式（`COMBATSOLVER_VERIFY_FAST_LANES=1`）：`runId` `aeea9c5e409b43308423453c1af396b7` Passed（`29.96 s`，`ProjectedBuilds=0`、`Comparisons=9`）。
- 离线搜索宿主成对 `A1/C1/C2/A2` 哨兵（`High` 宽度组合 5000 节点 `beta-standard-fool-hooks.json` 与 `VeryHigh` Beam 54 / 1500 节点 396 牌大牌组）：`compare_results.py` 在 `A1 vs C1` 与 `A2 vs C2` 共 `175` 项根戳记、工作量以及选中路线字段上逐字段一致（`mismatched_roots=0`；`fool-hooks` 均值为 `247` 展开、`882` 转移、`projectedBattleHpLost=0`、`wallSeconds` 基线 `0.9759/0.9597 s` 对比候选 `0.9712/0.9661 s`，峰值托管堆由 `26.85/27.05 MB` 降至 `25.31/25.03 MB`）。可见 Steam 帧时间未在本轮重测。

## Windows 合并源码验证

- 25 个独立检查均通过。整批运行通过 24 项，更新独立卡牌检查的牌堆替身后，该项的 78 条断言通过。
- `MIRRORED-HOOK-FILTER` 核对 Windows Steam 0.113.1 构建 `d09d48e5`。7 次镜像后缀构造、9 次监听表对账与 getter 补丁回退均通过。
- `HP-LOSS-CLAMP`、`GREMLIN-ESCAPE-GOLD` 与 `ORDERED-HAND-DUPLICATES-FIRST` 均通过。范围包括 168 组扣血原生差分、胖地精完整金币奖励与逐步续用，以及手牌顺序、RNG 和 Fork。
- 离线宿主的共享战损见证、BossTempo、续搜上界及诊断日志检查通过。当前 Windows 构建的有限回复证书保留版本门禁，原生证书正例见各 PR 的来源记录。
