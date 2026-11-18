# CombatSolver 测试入口

## 重复卡牌与有序手牌

`ORDERED-HAND-DUPLICATES-FIRST` / `ORDERED-HAND-DUPLICATES-SECOND` 分别执行两处相同打击，再执行未升级坚毅。合同核对完整状态、随机流、续用、父分支及 Fork 隔离，并检查普通枚举、开局能力牌和循环替代动作。

```text
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId ORDERED-HAND-DUPLICATES-FIRST -EnemyCurrentHp 100 -TimeoutSeconds 120 -CleanupInstanceOnExit
./tools/testing/run-unattended-test.sh --scenario-id ORDERED-HAND-DUPLICATES-FIRST --enemy-current-hp 100 --timeout-seconds 120 --cleanup-instance-on-exit
```

将 ScenarioId 改为 `ORDERED-HAND-DUPLICATES-SECOND` 可执行后一张打击。失败基线、固定搜索对照和未验证项见[重复卡牌记录](issues/ordered-hand-duplicates.md)。

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

2026-10-22 [结果诊断内存读取](performance/diagnostic-process-memory.md)的确定性原生布局／映射、失败恢复、并发及生产日志成功／失败合同通过，独立查询基准与既有实机证据分开记录。

## 独立检查与 CI

`python -B tools/testing/run-independent-checks.py` 执行 25 个独立 .NET 检查，依赖 Python 3 和 .NET 9 SDK。Windows/Linux 使用同一入口。单项复跑可追加检查名。

入口先扫描 `checks/*/*.csproj`，检查每个项目是否登记。新增项目未登记、已登记项目被删除或项目布局改变时，先报错，不启动 .NET。`--check-catalog` 可单独执行这项检查。

排除项有 3 个：`AdaptedOnPlayChecks` 依赖游戏的 Harmony；`PowerAmountComparisonChecks` 依赖游戏、Godot、Harmony 和 RitsuLib；`PerformanceRecordingTests` 需要输出目录或 trace、运行模式，不能无参数执行。

修复前，卡牌 hook 和回合阶段检查缺少 Fork 合同，无法编译；诊断日志检查仍假定只有 1 条进程事件。回合阶段替身还缺少当前 Power 和未支持异常边界。修复后断言 4 条生命周期事件及冻结前缀，保留战斗日志隔离和过载检查。

2026-11-15，本机 Windows / .NET SDK 9.0.321：全组执行通过 24 项；补齐最后一个回合阶段合同后，定向复跑通过 30 条断言。卡牌 hook 检查含 78 条断言。

同日，GitHub Actions 的 Windows 与 Ubuntu 均完整执行 25 个项目，各为 `total=25 passed=25 failed=0`。运行记录见 [Independent checks](https://github.com/EEEEEEEEdison/CombatSolver/actions/runs/43688960839)，行为源码为 `6d29975c`。游戏内战斗未复测。

`.github/workflows/independent-checks.yml` 在两个平台执行这些检查。独立替身只验证链接的生产边界，不代替游戏内语义、可见 UI 或战斗路线验收。

2026-11-17，登记扫描识别 25 个执行项和 3 个排除项。临时目录合同覆盖完整登记、未分类项目、缺失项目及异常布局，4 项通过。新入口定向执行 `CardHookReceiverChecks`、`DiagnosticLogTests`，2 项通过；本轮未在本机重跑其余 23 项。

## RitsuLib 无头兼容目标

`pwsh -NoProfile -File tools/testing/test-headless-runtime.ps1 -RitsuOnly` 和 `bash tools/testing/test-headless-runtime.sh --ritsu-only` 覆盖精确版本、补丁升级、最新目标、数字版本排序、全为未来目标、重复及非法目标。

2026-11-15，PowerShell 与 Windows Git Bash 均通过 8 项定向检查。原生启动器已在游戏 0.113.1 / RitsuLib 0.6.13 环境完成 `UI-PRIORITY-FEEDBACK`，结果 Passed，25.46 秒，隔离实例已清理。Linux 系统上的完整启动未验证。

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
命令、逐次数字及原生与离线验证的范围见[记录](performance/shuffle-variable-names-20261115.md)。

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

## 可见测试输入选择

`run-visible-steam-benchmark.ps1` 的默认快照路径已迁移到 `coverage/fixtures/regressions/snapshots/mecha-knight-memory-run-snapshot.json`。两端仅在默认模式检查并解析该路径。

- 修改前：完整 UI 请求在启动游戏前报 `Mecha Knight benchmark snapshot not found`。
- 修改后：Windows 可见 `UI-PRIORITY-FEEDBACK` 请求 Passed。runId 为 `43088d4fc27f41a5869bb72f3a9421a2`，耗时 39.11 秒，含建局。
- 请求准备：PowerShell、Git Bash 各 4 项通过。缺少默认快照时，完整请求、日志夹具和检查点模式通过；默认模式仍明确失败。检查执行生产脚本的准备段，不启动游戏。
- 环境：游戏 0.113.1、RitsuLib 0.6.13、D3D12 / Forward+、RTX 5080 Laptop GPU，启用本机 20 个 Mod。
- 未验证：Linux 原生可见启动、历史机甲骑士快照整场，以及 #223 原报告的存档与卡死。

复跑时将以下 JSON 保存为 `.local/ui-visible-request.json`，通过 `-RequestFixturePath` 或 `--request-fixture-path` 传入。两端均设置 120 秒超时，并使用 `-EvidenceDirectory` 或 `--evidence-directory` 保存结果。

```json
{
  "schemaVersion": 1,
  "scenarioId": "UI-PRIORITY-FEEDBACK",
  "characterId": "IRONCLAD",
  "encounterId": "FUZZY_WURM_CRAWLER_WEAK",
  "seed": "VISIBLEUI223",
  "enemyCurrentHp": 100,
  "timeoutSeconds": 120,
  "exitOnComplete": true
}
```
## 0.51.5 结算顺序与运行时交接

定向原生差分、第二回合续用、便携 GC 合同及未验证项见[历史卷 28](archive/testing/volume-28.md#0515-结算顺序与运行时交接)。

## 0.51.4 战斗逻辑与选牌续接

5 项定向原生验证、复跑入口和未验证项见[历史卷 27](archive/testing/volume-27.md#0514-战斗逻辑与选牌续接)。

## 0.51.3 同牌多减益与部署异常

本轮三项定向原生差分、复跑入口及待定位的原生飞刀异常见[历史卷 26](archive/testing/volume-26.md#0513-同牌多减益与部署异常)。

## 0.51.2 属性削减与诅咒持续时间

0.51.2 的族母、诅咒持续时间、仪式兽最小差分及验证范围见[历史卷 25](archive/testing/volume-25.md#0512-属性削减与诅咒持续时间)。

## 0.113 实例状态与 RNG 问题包

0.51.1 的最小差分、失败记录与验证范围见[历史卷 24](archive/testing/volume-24.md#0113-实例状态与-rng-问题包)。

## 0.113 Part 2：恐惧与极速

0.51.1 的领域、怪物、随机生成、六角色集成、续用与恢复证据见[历史卷 24](archive/testing/volume-24.md#0113-part-2恐惧与极速)。

## 请求预算显示

0.51.0 定稿的验证结果、复跑入口、失败与未验证项见[历史卷 23](archive/testing/volume-23.md#请求预算显示)。

## v0.113.0 标准模式适配

0.51.0 定稿的验证结果、复跑入口、失败与未验证项见[历史卷 23](archive/testing/volume-23.md#v01130-标准模式适配)。

## 首领卖血预算搜索

0.51.0 定稿的验证结果、复跑入口、失败与未验证项见[历史卷 23](archive/testing/volume-23.md#首领卖血预算搜索)。

## 社区批次 Q010 与组合补搜

现行组合入口按共享节点与时间准入，执行期间在每批提交边界处理内存预约、回收和停止。原包 SearchOnly、Beam 组合检查及真实 CLR 合同的结果、失败与未验证项见 [历史卷 20](archive/testing/volume-20.md#社区批次-q010-与组合补搜)。

## 0.50.1 智能药水机会成本

终局单药／双药低收益拒绝与 Force、奖励抵扣、原价高收益哨兵由 `SMART-OPENING-POTION-ADMISSION` 覆盖；跨成员比较由 `python -B tools/testing/checks/BeamWidthPortfolioChecks/run.py` 覆盖（118项通过）。原生 runId `d9629992ac31423586c7465510c18f6d` Passed（23.63秒，含建局），实例已清理。失败基线、同根材料与未验证范围见[问题记录](issues/potion-opportunity-20261006.md)。

## 社区批次 Q015：路线保留

0.51.0 定稿的验证结果、复跑入口、失败与未验证项见[历史卷 23](archive/testing/volume-23.md#社区批次-q015路线保留)。

## 社区批次 Q013 同根哨兵与部署夹具

0.51.0 定稿的验证结果、复跑入口、失败与未验证项见[历史卷 23](archive/testing/volume-23.md#社区批次-q013-同根哨兵与部署夹具)。

## 开局药水补搜准入

固定两槽满栏，使用原版奖励 RNG 捕获确定掉药／不掉药的搜索根。`SMART-OPENING-POTION-ADMISSION` 覆盖低损共同准入与换药抵扣，`SMART-OPENING-POTION-VALUE` 覆盖原价省血边界。失败基线、最终结果与验证范围见 [历史卷 20](archive/testing/volume-20.md#开局药水补搜准入)。

```text
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId SMART-OPENING-POTION-ADMISSION -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
./tools/testing/run-unattended-test.sh --scenario-id SMART-OPENING-POTION-ADMISSION --character-id SILENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --enable-no-gc-region-for-test 0 --timeout-seconds 120 --cleanup-instance-on-exit
```

将 ScenarioId 改为 `SMART-OPENING-POTION-VALUE` 可单独执行原价省血边界。

## 0.49.1 日志站硬逻辑（2026-10-04）

强制结束回合选牌、群体 Power、死亡金币回调、延迟能量／等离子球、开局小刀、回合末自动出牌、行动意图、资源隔离、反应格挡和界面归属的原生差分见 [逐类验收](issues/0.49.1-hardbugs-20261004.md#原生验收证据)。该记录保留失败基线、runId、复跑入口、第三方边界及未验证项；生产部署合同包含增量验证和计划外重算断言。

0.49.2 内容性 Mod 失败分类的原生合同、完整命令及未验证范围见[历史卷19](archive/testing/volume-19.md)。

## 0.49.1 内存提交回归（2026-10-04）

`CombatSolver.GcPolicyChecks` 直接编译生产 Runtime 内存代码。`default-commit` 在未改生产代码时 Failed：显式回退后分配上限仍有效；修复后 2 项 Passed，覆盖普通检查点保留限额、不可分割提交完成回收后续行、后续请求重新建立限额与取消保持原准入。

```bash
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- default-commit
```

相邻真实 CLR 回归：`default-entry` 2 项、`recovery-lifecycle` 3 项 Passed，包含实际 NoGC 退出与恢复、显式退出持续生效及诊断失败清理。每项设有 15 或 20 秒截止时间。未重放 18 份原包整场，也没有可见 Steam 或低内存宿主性能结论；[排查记录](issues/0.49.0-memory-commit-regression-20261004.md)保留固定报告身份与触发窗口。

## PR #203 的机制合同

金币、最大生命回复与遗物间接回复的贡献者证据见 [专题记录](performance/gold-max-hp-healing-20261003.md)。独立场景为 `GOLD-HEALING-MECHANISMS`、`MAX-HP-HEALING-CALLBACKS`、`FEED-MAX-HP-CAP`、`RELIC-MAX-HP-HEALING-BOUNDS` 和 `AXEBOT-JOSS-DEFERRED-FORK`，均从平台原生无人入口运行，使用 IRONCLAD、FUZZY_WURM_CRAWLER_WEAK、120 秒上限及实例清理。原作者结果与本轮合并验证分别记账。

金币和回复合同同时传 `-EvidenceDirectory .local/validation/pr203/<场景>`（Bash：`--evidence-directory`），初始生命为 50/80。金纸合同还传 `-RelicsPath coverage/fixtures/regressions/axebot/axebot-joss-deferred-relics.json`（Bash：`--relics-path`）。搜索阶段顺序合同为 `KNOWN-HEALING-MEMBERS` 和 `KNOWN-HEALING-OPENING`，使用 SILENT；受影响的固定前缀合同使用 IRONCLAD，强制结束回合牌合同使用 REGENT。

## 当前矩阵命令

矩阵启动器只读取本节的平台原生命令；历史记录不作为自动执行清单。以下两项分别检查倾泻手空边界与相邻效果作用域，不运行整场搜索。需要当前游戏及 RitsuLib 路径，矩阵启动器统一管理实例并在结束时清理。

```powershell
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CASCADE-EMPTY-HAND-NATIVE -EnemyCurrentHp 1000 -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId EFFECT-SCOPE-ADJACENT-CONTRACT -EnemyCurrentHp 1000 -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120
```

```bash
./tools/testing/run-unattended-test.sh --scenario-id CASCADE-EMPTY-HAND-NATIVE --enemy-current-hp 1000 --headless-fast-mode-for-test Instant --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 120
./tools/testing/run-unattended-test.sh --scenario-id EFFECT-SCOPE-ADJACENT-CONTRACT --enemy-current-hp 1000 --headless-fast-mode-for-test Instant --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 120
```


## 0.48.0 硬错误机制合同

历史结果和复跑命令见[历史卷 22](archive/testing/volume-22.md)。

## 社区批次 B013 回归夹具（2026-10-03，Refs #172）

以下为贡献者在 PR #204 记录的结果，与本轮验证分别记账。五个主题的独立机制回归在 `src/Testing/Regressions/Community/B013*Checks.cs`，命令统一为
`tools/testing/run-unattended-test.ps1 -ScenarioId <ID> -TimeoutSeconds 120 -CleanupInstanceOnExit`（角色/遭遇按夹具要求，详见 PR #204）：

- `B013-BLOCK-DECIMAL-BOUNDARY`（T010，格挡乘法 Decimal 边界）：修改前 `dec22b8f` Failed（栈同报告）→ 修改后 `e2757311`/`a23dcf21` Passed；代表包 DOP1 哨兵路线与展开/转移一致。
- `B013-FIXED-PREFIX-TERMINAL-BOUNDARY`（T008，固定前缀越过锁定终局）：`971ccdd1` Failed → `0b3f96a6` Passed；代表包哨兵 22809/129762、终局一致。
- `B013-FIXED-PREFIX-TURN-END-CARD`（T007，结束回合卡固定前缀）：`eb64242d` Failed → `899ef881` Passed。
- `B013-RADIANT-PEARL-HAND-DRAW`（T006，抽牌前遗物生成对账）：`cd227ff6` Failed → `15c80923` Passed；代表包 SearchOnly 失配 → `TURN_SETUP_STATE_MATCH`。
- `B013-DEFAULT-GC-LIMIT`（T009，默认 GC 请求分配边界）：`6ac0f466` Failed（`limit=long.MaxValue`）→ `7c6f3b83` Passed。

变基到 0.48.1（`2ead87d9`）后五夹具复跑 Passed：`0f19174f` / `57e2086d` / `9d7173af` / `3892e307` / `fdfc8734`。代表包哨兵在新基线：T010 DOP1 与旧基线逐字段一致（展开 7426、转移 77129、4 回合胜、finalHp 71），耗时 3458.9ms；T009/T006 代表包 7 回合胜、finalHp 88、boundary None（展开 26194、转移 190351，路线随上游搜索改动微调）。

完整修改前后、哨兵与未验证项见 PR #204；批次状态与剩余项（T007 第二样本未复现）同样记录在该 PR。

## PR #203、#204 合并验收（2026-10-04）

以下为本轮直接运行结果；全部使用120秒上限和实例清理，已通过的请求未重复运行。无头验证停在对应机制边界。

| 场景 | runId | 结果与范围 |
| --- | --- | --- |
| `GOLD-HEALING-MECHANISMS` | `3bc7928290c343cca88248c0c28dd95e` | Passed；金币修正、三个回调、完整原生状态/RNG和父分支隔离 |
| `MAX-HP-HEALING-CALLBACKS` | `cd6549478924451882fc600e67724a53` | Passed；最大生命实际增量、封顶与回复回调的原生差分 |
| `FEED-MAX-HP-CAP` | `ddcf661e391c4013a0aa87861f8542f3` | Passed；两次致命出牌的实际增量与成长计数 |
| `RELIC-MAX-HP-HEALING-BOUNDS` | `bee94b31824d449e8f7e9fa55cbe94f5` | Passed；两项直接原生遗物回调与熔化来源排除 |
| `AXEBOT-JOSS-DEFERRED-FORK` | `0fd9902a8a594e36a0a9fbf533ceee65` | Passed；根计数、零状态指纹、父子/兄弟Fork与逐分支消费 |
| `KNOWN-HEALING-MEMBERS` | `fab7a8170a3942e9920e09fa466aa34e` | Passed；严格增量、DOP2、控制质量、成长门与live隔离 |
| `B013-FIXED-PREFIX-TERMINAL-BOUNDARY` | `5b097fd5f17745a0868a6b49d7af3b37` | Passed；终局前缀截断与可交付胜利 |
| `B013-FIXED-PREFIX-TURN-END-CARD` | `c778f285a52a4b9fbbe0e370688e6ec1` | Passed；强制结束回合卡的固定前缀推进 |
| `B013-RADIANT-PEARL-HAND-DRAW` | `ca904a20eafd43b690d8c824f60e2069` | Passed；抽牌前生成的原生数量、升级与归属对账 |
| `B013-DEFAULT-GC-LIMIT` | `3e440c65f9564b5ea6a65d094521b8d7` | Passed；限额、真实Gen2回收续搜、退出清理与CLR模式 |
| `KNOWN-HEALING-OPENING` | `3fecd35f80a34cb792a43ca3b5bc3b1c` | Passed；非认证根延后计划、单次执行与控制质量 |
| `FIXED-PREFIX-TURN-OUTCOMES` | `b18d23d1829b4438a557a5f75acf6425` | Passed；独立前缀完整状态oracle、多回合结果与终局截断 |
| `B013-BLOCK-DECIMAL-BOUNDARY` | `b556026e627c4153bb3d2bf23948a095` | Passed；原生虚弱倍率、完整状态/RNG；模拟96/95/200层与零格挡 |

真实CLR工具：`default-entry` 2项、`diagnostic-failure` 8项、`scopes` 8项 Passed。入口失败基线为日志抛错后信号仍启用；修复后同异常传播、信号清理及后续独占准入均通过。

失败记录：首次无头启动在私有进程身份检查处失败并清理，未进入游戏测试，原因未确定；金币首轮14项对账已通过，但缺EvidenceDirectory导致产物写入失败；金纸首轮缺遗物输入。补齐请求参数后仅重跑失败请求，成功记录在上表。固定前缀09f94286012d420d81242f480ebd1803仍执行旧的终局拒绝断言，合同更新为开局探测和完整搜索共同截断，并断言终局动作数及回合。新增格挡夹具初次编译因原生调用参数及私有setter失败，修正后Release零警告/错误。

L0：原生回复审计工具迁入tools/inspection后构建和真实DLL扫描通过。CoverageCatalog重新生成3035项目录，状态字段未分类为0；`--verify-state-writes`仍因既有InfusedCore.AfterSideTurnStart缺运行证据失败（1项）。PowerShell结构检查247文件通过，工具检查290文件/37项目通过，文档427文件/1635链接及覆盖目录检查通过。额外Bash结构检查因运行耗时停止，未完成；两平台脚本静态语法检查通过。

范围：本轮没有重跑作者长预算全根性能筛查，没有验证低内存玩家宿主、原生整场部署或可见Steam性能；原作者失败与未验证项保留在所属报告。


## 性能研究分支

既有组件、药水、魂枢及未达标原型的合同与历史结果见[历史卷18](archive/testing/volume-18.md)。PR #207最新上游整合、逐次ABBA、23根品质回归及NoGC波动/超时限制见[当前验收](performance/pr207-upstream-0494-integration-20261005.md)。

## 手牌上限状态一致性（PR #224）

原版根／Fork 合同与贡献者的动态上限及耗时证据见[历史卷21](archive/testing/volume-21.md#手牌上限状态一致性pr-224)。
