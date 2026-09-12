# 独立测试 ledger（`tools/test-ledger.sh`）

只读的测试编排工具：从仓库现有入口生成机器可读 catalog，运行其中的纯 .NET / Python 合同入口，
输出统一四态 JSONL。它**不**重写矩阵 runner，**不**读取 `coverage/test-evidence.json` 作为通过依据，
**不**启动游戏，也**不**写 `coverage/*.json`。

## 用法

```bash
./tools/test-ledger.sh catalog                      # 生成 .local/test-ledger/test-catalog.json
./tools/test-ledger.sh run --scope pure-contract     # 运行纯合同子集，写 ledger.jsonl + summary.json + logs/
./tools/test-ledger.sh selftest                      # 用真实入口校验四态映射
```

`run` 的默认输出目录是 `<repo>/.local/test-ledger/`（已 gitignore）。常用参数：

| 参数 | 说明 |
|---|---|
| `--catalog PATH` | catalog 路径；缺省且文件不存在时自动生成 |
| `--scope pure-contract\|all` | 允许执行的入口类别；`pure-contract` 只含 `PureDotnet` 与 `PythonContract` |
| `--filter REGEX` | 按 `entryId` 过滤；未选中的条目记为 `NotRun` |
| `--ledger-dir PATH` | `ledger.jsonl`、`summary.json`、`logs/` 的输出目录 |
| `--timeout-seconds N` | 单条默认超时；超时记为 `Failed`，永不记为 `Passed` |
| `--allow-game-launch` | 放开 `run-unattended-test` / `run-headless-matrix` / 可见 Steam 基准的策略拒绝（默认关闭） |
| `--allow-coverage-writes` | 放开 `CoverageCatalog` 的策略拒绝（默认关闭） |

## 四态判定

| 状态 | 判定 |
|---|---|
| `Passed` | 真实执行、退出码 `0`，且 catalog 声明的 marker 出现在日志中（未声明 marker 时只看退出码） |
| `Failed` | 真实执行但退出码非 `0`、超时，或退出码 `0` 但缺少声明 marker |
| `Blocked` | 未执行：策略拒绝、命令目标/可执行文件缺失，或声明依赖未满足（平台、游戏、`node_modules`、夹具、环境变量、NuGet 包、显示会话等） |
| `NotRun` | 已登记且依赖满足，但本次 scope/filter 未选中 |

`StaticPassed`、`Skipped`（含矩阵 `SkippedMissingFixture`）、`timeout`、缺失命令一律不记为 `Passed`；
`summary.json` 的 `neverPassed` 字段显式记录这四个不变式。

## catalog 来源

| 来源 | 说明 |
|---|---|
| `tools/*/*.csproj` | 按 `TargetFramework` / `OutputType` / `Reference` / `PackageReference` 分类为 `PureDotnet`、`DotnetProbe`、`DotnetTool`、`DotnetGame`、`DotnetCoverage`、`DotnetWindows` |
| `tools/*/run.py`、`tools/*/presets.py` | `PythonContract`（生成 `.local/<name>/` 临时工程后 `dotnet run`） |
| `tools/*/package.json` | `NodeTest` / `NodeTestBrowser` |
| `docs/TEST_MATRIX.md` Windows/Linux 命令块 | `MatrixWindows/Linux`、`SteamBenchmarkWindows/Linux`，命令逐字保留 |
| `tools/{verify,test}-*.{sh,ps1}` | `GateLinux` / `GateWindows`（L0 结构门禁） |

marker 只从入口文件与同目录 `Program.cs` 提取（`_OK` 优先于 `PASS`/`Passed` 句子），并记录
`markerSource`（`文件:行`）；同目录其它合同文件的 marker 可能只属于非默认开关，不作为判据。

## 策略拒绝

默认拒绝执行会启动游戏（`run-unattended-test.*`、`run-headless-matrix.*`、`run-checkpoint-batch.*`、
`run-visible-steam-benchmark.*`）或重写覆盖目录（`CoverageCatalog`）的命令，记为
`Blocked` + `refused_by_policy:<name>`。因此 `--scope all` 也不会启动游戏或改动 `coverage/*.json`。

## 自测

`selftest` 用两个真实入口加九条控制命令固定四态映射：`tools/verify-refactor-boundaries.sh`
（真实门禁，期望 `Passed`）、`tools/NoVictoryRecoveryChecks/presets.py`（真实合同入口，只校验
“状态与退出码/marker 自洽”，结果如实打印）、以及 marker 缺失、非零退出码、超时、目标缺失、
可执行文件缺失、依赖缺失、游戏策略拒绝、覆盖目录策略拒绝、被 filter 排除等控制项。
自测不会伪装任何生产结果。

## 现状与限制

- 只提供 Linux 入口 `tools/test-ledger.sh`；Windows 命令块会被登记为 `Blocked`（平台不满足），
  `tools/test-ledger.ps1` 在具备 Windows 主机验证前不提供。
- 游戏场景、可见 Steam 基准、Node 服务测试、需要外部 trace 的工具都会被 catalog 记录并按依赖/策略置为
  `Blocked`；ledger 只证明这些入口当前不可由本工具执行，不证明其行为正确。
- 每个执行条目都会在 `logs/<entryId>.log` 留下原始命令、工作目录、完整输出与退出码。
- `sourceRevision`（commit/branch/`trackedDirty`）写进每一行记录与 `summary.json`。
