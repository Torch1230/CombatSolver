# Issue #36：固定工作量重现

逐轮数值、中位数、runId、生命周期及完整路线/工作量/质量/剪枝比较见 [结果 JSON](gc-issue36-results.json)，候选取舍见 [实现记录](gc-issue36-implementation.md)。以下命令等价于本轮本地 `run-matrix.py` 的请求参数，直接使用已跟踪的原生 launcher；不依赖未提交的研究脚本。

这是 Linux headless 研究，尚无 Windows 实测或可见 Steam 帧时间结论。普通 GC、NoGC、不同 fixture 分组比较；每组串行启动三个独立进程，保存全部结果，报告中位数，不剔除离群值。早期 `candidate1`、撤回 listener 的控制、压力和自适应试验各配置仅一次，不能称为三次复测。中间候选不是独立提交，不能仅 checkout 一个标签复原其组合实现。

## 固定输入与设置

使用公开 [Silent 卡表](../../coverage/unattended/search-performance-silent-large-deck-cards.json) 和 [Necrobinder runSnapshot](../../coverage/unattended/search-performance-necrobinder-potion-heavy-run-snapshot.json)，不需要玩家问题包或私人存档。

[基准设置 JSON](../../coverage/unattended/gc-issue36-benchmark-settings.json) 固定 Custom、每 solver 576 节点、60 秒 solver 兜底、DOP4。长窗口只把 `shortMaxExpandedNodes`、`deepMaxExpandedNodes` 同时改为 2500。60 秒是搜索时间上限，120 秒是整个无人请求超时；达到 TimeLimit 的结果不能混入固定工作量比较。不启用 `VerifyIncrementalSearch`；详细诊断关闭，阶段测量开启，强制仅 Short，并在首个结果断言后退出。

| 配置 | 每 solver 节点 | 请求展开 / 转移 | 药水政策 |
| --- | ---: | ---: | --- |
| Silent 短窗口 | 576 | 1152 / 5543 | Disabled |
| Necrobinder 短窗口 | 576 | 1728 / 22541 | Smart |
| Silent 长窗口 | 2500 | 5000 / 19065 | Disabled |

请求可包含多个 solver；节点设置不是请求累计上限。除上述工作量，还需比较完整有序 ACTION/TURN 路线、所选及累计选牌/转移、边界、战损、HP、药水、回合与全部非时序剪枝/保路计数；只看分数或动作数不足以证明等价。

## 准备

从仓库根运行。使用匹配的游戏/依赖版本；本轮是塔 2 `0.111.0`、.NET SDK `9.0.120`、Linux x86_64、Ryzen 7 7840H / 16 逻辑处理器。基线为 `5c4b69d91bcc049781e9de6ddf043799a82c849b`。更换游戏、依赖或主机后重新建立基线。

研究游戏目录应有独立的可执行文件路径和 `mods` 目录。将环境变量 `GC36_GAME_ROOT`、`GC36_RITSU_ROOT` 指向该目录和匹配的 RitsuLib workshop 目录；不要把研究 mods 指向日常游戏 mods。launcher 会在自己的 headless 数据目录初始化游戏配置。先退出其他游戏进程，再串行运行。

需要构建时使用：

```text
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
```

把该构建的 `.godot/mono/temp/bin/Release/CombatSolver.dll`、根目录 `CombatSolver.json` 和 `THIRD_PARTY_NOTICES.md` 放入研究游戏的 `mods/CombatSolver`。每轮用新输出名；记录实际构建来源，不能在一组中混用 DLL。

## Linux 原生 Bash

`.sh` launcher 使用 Bash，并依赖其现有 `jq` 等工具。下面重现一次两个 fixture；分别以普通 GC 和 NoGC 开启运行，每次换 `reproduce-1` 为新的轮次名。三次独立结果构成一组。长窗口将节点改为 2500，只运行 Silent 命令。

```bash
gc36_output="$PWD/.local/gc-issue36/reproduce-1"
gc36_nodes=576
gc36_dop=4
gc36_no_gc=0       # 1 开启 NoGC
gc36_region_gb=4  # 十进制 GB

for gc36_case in silent necro; do
  mkdir -p "$gc36_output/$gc36_case/data/SlayTheSpire2"
  jq --argjson nodes "$gc36_nodes" \
    '.shortMaxExpandedNodes=$nodes | .deepMaxExpandedNodes=$nodes' \
    coverage/unattended/gc-issue36-benchmark-settings.json \
    > "$gc36_output/$gc36_case/data/SlayTheSpire2/combat_solver_settings.json"
done

gc36_common=(--sts2-game-root "$GC36_GAME_ROOT"
  --ritsu-workshop-root "$GC36_RITSU_ROOT"
  --performance-preset-for-test Custom --force-short-search-only
  --measure-search-phases --enable-detailed-diagnostic-logs-for-test 0
  --headless-fast-mode-for-test Instant
  --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0
  --expected-initial-executable-action-count-at-least 1
  --stop-after-initial-solver-result-assertion --timeout-seconds 120 --exit-on-complete)
gc36_mode=(--search-max-degree-of-parallelism-for-test "$gc36_dop"
  --enable-no-gc-region-for-test "$gc36_no_gc"
  --no-gc-region-budget-gigabytes-for-test "$gc36_region_gb")
gc36_silent=(--character-id SILENT --seed SEARCH_PERF_SILENT_LARGE_DECK
  --encounter-id AEONGLASS_BOSS --ascension 5 --act-index-for-test 2
  --enemy-current-hp 512 --initial-enemy-move-ids-json '["EBB_MOVE"]'
  --initial-player-hp 65 --initial-player-max-hp 65 --initial-player-energy 3
  --clear-player-piles
  --cards-path coverage/unattended/search-performance-silent-large-deck-cards.json
  --potion-policy-for-test Disabled)
gc36_necro=(--character-id NECROBINDER --seed SEARCH_PERF_NECROBINDER_POTION
  --run-snapshot-path coverage/unattended/search-performance-necrobinder-potion-heavy-run-snapshot.json
  --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2
  --enemy-current-hp 526 --initial-player-hp 41 --cards-json '[]'
  --potion-policy-for-test Smart)

COMBATSOLVER_HEADLESS_ROOT="$gc36_output/silent" \
  ./tools/run-unattended-test.sh "${gc36_common[@]}" "${gc36_mode[@]}" \
  "${gc36_silent[@]}" --scenario-id GC36-REPRO-SILENT
COMBATSOLVER_HEADLESS_ROOT="$gc36_output/necro" \
  ./tools/run-unattended-test.sh "${gc36_common[@]}" "${gc36_mode[@]}" \
  "${gc36_necro[@]}" --scenario-id GC36-REPRO-NECRO
```

每个 runtime 的 `data/SlayTheSpire2/combat_solver_test_result.json` 保存结构化结果。原始运行目录留在 Git 忽略区；发布结果时仅提取必要数值和比较结论。

## Windows 原生 PowerShell 7

使用 `.ps1` launcher，不通过 WSL 启动 Windows 游戏。该入口目前固定使用系统 `LocalApplicationData/CombatSolver/headless-runtime`，**不读取 Linux 的 `COMBATSOLVER_HEADLESS_ROOT`**。下面仅向它的 headless `Roaming/SlayTheSpire2` 写研究设置；每次进程退出后，把结果复制到新轮次目录。此命令仅完成静态参数核对，尚未在 Windows 执行。

```powershell
$ErrorActionPreference = 'Stop'
$gc36Output = Join-Path $PWD '.local/gc-issue36/reproduce-1'
$gc36Nodes = 576
$gc36Dop = 4
$gc36NoGc = 0
$gc36RegionGb = 4
$gc36Runtime = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CombatSolver/headless-runtime'
$gc36Data = Join-Path $gc36Runtime 'Roaming/SlayTheSpire2'
New-Item -ItemType Directory -Force -Path $gc36Data, $gc36Output | Out-Null
$gc36Settings = Get-Content coverage/unattended/gc-issue36-benchmark-settings.json -Raw | ConvertFrom-Json
$gc36Settings.shortMaxExpandedNodes = $gc36Nodes
$gc36Settings.deepMaxExpandedNodes = $gc36Nodes
$gc36Settings | ConvertTo-Json | Set-Content (Join-Path $gc36Data 'combat_solver_settings.json') -Encoding utf8NoBOM

$gc36Common = @{
    Sts2GameRoot = $env:GC36_GAME_ROOT; RitsuWorkshopRoot = $env:GC36_RITSU_ROOT
    SearchMaxDegreeOfParallelismForTest = $gc36Dop
    EnableNoGcRegionForTest = $gc36NoGc; NoGcRegionBudgetGigabytesForTest = $gc36RegionGb
    PerformancePresetForTest = 'Custom'; ForceShortSearchOnly = $true
    MeasureSearchPhases = $true; EnableDetailedDiagnosticLogsForTest = 0
    HeadlessFastModeForTest = 'Instant'
    ExpectedInitialSearchPhase = 'Short'; ExpectedInitialDeepSearchTriggered = 0
    ExpectedInitialExecutableActionCountAtLeast = 1
    StopAfterInitialSolverResultAssertion = $true; TimeoutSeconds = 120; ExitOnComplete = $true
}
$gc36Silent = @{
    CharacterId = 'SILENT'; Seed = 'SEARCH_PERF_SILENT_LARGE_DECK'
    EncounterId = 'AEONGLASS_BOSS'; Ascension = 5; ActIndexForTest = 2
    EnemyCurrentHp = 512; InitialEnemyMoveIdsJson = '["EBB_MOVE"]'
    InitialPlayerHp = 65; InitialPlayerMaxHp = 65; InitialPlayerEnergy = 3
    ClearPlayerPiles = $true
    CardsPath = 'coverage/unattended/search-performance-silent-large-deck-cards.json'
    PotionPolicyForTest = 'Disabled'
}
$gc36Necro = @{
    CharacterId = 'NECROBINDER'; Seed = 'SEARCH_PERF_NECROBINDER_POTION'
    RunSnapshotPath = 'coverage/unattended/search-performance-necrobinder-potion-heavy-run-snapshot.json'
    EncounterId = 'AEONGLASS_BOSS'; Ascension = 10; ActIndexForTest = 2
    EnemyCurrentHp = 526; InitialPlayerHp = 41; CardsJson = '[]'
    PotionPolicyForTest = 'Smart'
}
& ./tools/run-unattended-test.ps1 @gc36Common @gc36Silent -ScenarioId GC36-REPRO-SILENT
Copy-Item (Join-Path $gc36Data 'combat_solver_test_result.json') (Join-Path $gc36Output 'silent-result.json')
& ./tools/run-unattended-test.ps1 @gc36Common @gc36Necro -ScenarioId GC36-REPRO-NECRO
Copy-Item (Join-Path $gc36Data 'combat_solver_test_result.json') (Join-Path $gc36Output 'necro-result.json')
```

普通 GC/NoGC 切换、重复次数及长窗口规则与 Linux 相同。压力检查只运行 Necrobinder，576 节点、DOP8、NoGC 开启、预算 1 GB。设置合法范围是 1–256 GB；本轮 0.6 GB 请求在设置校验阶段失败，未开始搜索，结果 JSON 将其单列并保留失败原因。

## 最终行为与 native 差分

最终 Fork/历史所有权和 DOP1/DOP2 等价检查使用 250 节点、30 秒兜底的 [pilot 设置](../../coverage/unattended/gc-issue36-pilot-settings.json)，不是 576 节点性能矩阵。复用上面的 Silent fixture 与公共参数，写入该设置后，以 DOP2、普通 GC 加两个验证开关：

```bash
gc36_behavior="$gc36_output/behavior"
mkdir -p "$gc36_behavior/data/SlayTheSpire2"
cp coverage/unattended/gc-issue36-pilot-settings.json "$gc36_behavior/data/SlayTheSpire2/combat_solver_settings.json"
COMBATSOLVER_HEADLESS_ROOT="$gc36_behavior" \
  ./tools/run-unattended-test.sh "${gc36_common[@]}" "${gc36_silent[@]}" \
  --scenario-id GC36-FINAL-BOUNDARIES --search-max-degree-of-parallelism-for-test 2 \
  --enable-no-gc-region-for-test 0 --no-gc-region-budget-gigabytes-for-test 4 \
  --verify-fork-boundaries --verify-search-policy-snapshot
```

```powershell
Copy-Item coverage/unattended/gc-issue36-pilot-settings.json (Join-Path $gc36Data 'combat_solver_settings.json') -Force
$gc36Common.SearchMaxDegreeOfParallelismForTest = 2
$gc36Common.EnableNoGcRegionForTest = 0
$gc36Common.NoGcRegionBudgetGigabytesForTest = 4
& ./tools/run-unattended-test.ps1 @gc36Common @gc36Silent -ScenarioId GC36-FINAL-BOUNDARIES -VerifyForkBoundaries -VerifySearchPolicySnapshot
```

[Aeonglass 两步 native fixture](../../coverage/unattended/gc-aeonglass-preview-ownership.json) 同时检查实际结算、普通牌 preview 身份、Wither 更新及兄弟分支隔离：

```bash
COMBATSOLVER_HEADLESS_ROOT="$gc36_output/aeonglass" \
  ./tools/run-unattended-test.sh --sts2-game-root "$GC36_GAME_ROOT" \
  --ritsu-workshop-root "$GC36_RITSU_ROOT" --scenario-id GC36-AEONGLASS-PREVIEW-OWNERSHIP \
  --encounter-id LivingFogNormal --clear-player-piles \
  --cards-json '[{"cardId":"STRIKE_IRONCLAD","pile":"Hand"}]' \
  --monster-move-checks-path coverage/unattended/gc-aeonglass-preview-ownership.json \
  --timeout-seconds 120 --exit-on-complete
```

```powershell
& ./tools/run-unattended-test.ps1 -Sts2GameRoot $env:GC36_GAME_ROOT -RitsuWorkshopRoot $env:GC36_RITSU_ROOT `
  -ScenarioId GC36-AEONGLASS-PREVIEW-OWNERSHIP -EncounterId LivingFogNormal -ClearPlayerPiles `
  -CardsJson '[{"cardId":"STRIKE_IRONCLAD","pile":"Hand"}]' `
  -MonsterMoveChecksPath coverage/unattended/gc-aeonglass-preview-ownership.json -TimeoutSeconds 120 -ExitOnComplete
```

## 指标边界与实验归档

请求累计 worker 分配不是进程总分配或 retained heap。`totalGcPauseMilliseconds` 是累计暂停，`maxGcPauseMilliseconds` 是抽样观察到的最大值，不是完整 trace 最大单次暂停；旧基线 Smart 最大值口径还可能混入累计边界暂停。Linux VmHWM 来自约 100 ms 轮询，覆盖启动、建局和搜索全过程，不能称为 solver 独占峰值。Windows 如另采 PeakWorkingSet64，应单独注明平台与窗口，不能直接并入 Linux HWM 组。

NoGC 生命周期旧构建缺字段时记录 null；普通 GC 的 `SharedProcessWindow` 不能声称只属于该请求，独占 NoGC scope 才有准入前后冻结归因。`noGcRegionRolloverCount` 不等于全部 NoGC restart 次数。

普通 GC 自适应已经从生产撤回，源码及临时接线见 [ExperimentalAdaptiveGc](../../tools/ExperimentalAdaptiveGc/README.md)。其单次 A/B 需要相同最终组合的静态 DOP 控制；判读时保留每 Solve 的完成窗口、探测、拒绝/接受、最终容量与未完成探测数。本轮 Silent 有有效窗口但无探测，Necrobinder 一次降核探测遭拒绝；不能凭没有事件或单次耗时推断稳定收益。
