# OfflineSearchHarness 离线搜索与校验宿主

`tools/search/OfflineSearchHarness` 直接在纯 .NET 进程内引导游戏静态表与 Mod 运行时，脱离 Godot 界面驱动 `HotRootBuilder`、`HotCombatEngine` 与 `HotSearchCoordinator`。日常开发、内核校验、差分测试与 A/B 调优均优先使用本工具。

## 1. 构建与位置

```powershell
dotnet build tools\search\OfflineSearchHarness\OfflineSearchHarness.csproj -c Release
```

默认输出可执行文件：
`.local/tool-build/bin/OfflineSearchHarness/Release/net9.0/OfflineSearchHarness.exe`

也可直接执行 `--help` 查看完整内置说明：
```powershell
.local\tool-build\bin\OfflineSearchHarness\Release\net9.0\OfflineSearchHarness.exe --help
```

## 2. 快速自检子命令（秒级）

| 命令 | 用途 |
| --- | --- |
| `--check-hot-layout` | 校验 `CombatHotState` / `HotStructs` 非托管布局与容量上限 |
| `--audit-hot-coverage` | 枚举当前单人全实体与 `HotKernel` 规则覆盖、反射字段审计 |
| `--dump-high-risk-pool` | 打印高风险池三级分类（Tier 0 Excluded / Tier 1 Moderate / Tier 2 UltraHighRisk） |
| `--verify-hot-parity [--gate A|B|C] [--dop N]` | 校验 Gate A（19 根捕获）、Gate B（多回合自洽与 `DOP=1/N` 确定性）、Gate C（微基准） |
| `--verify-strategy-policies` | 端到端校验 11 项局外成长、成长开关、卡遗物/肉块与 `BrightestFlame` 等策略合同 |

## 3. 差分模糊与实机部署压测

- **`--fuzz-combat`**：在原版实况状态与 `HotKernel` 之间执行覆盖引导单人差分模糊。
  ```powershell
  OfflineSearchHarness.exe --fuzz-combat --fights 500 --seed 4026 --phase high-risk --max-turns 30 --dump-dir .local\tmp\fuzz
  ```
  - `--phase` 支持：`all` | `sweep` | `synergy` | `high-risk`（80% Tier2 / 20% Tier1）
- **`--replay-fuzz <repro.json>`**：确定性回放 `--fuzz-combat` 或压测导出的单个最小复现包。
- **`--soak-deploy`**：整场运行“搜索 → 实机部署 → 跨回合续用”链路，统计计划外重算（目标为 0）与部署失败：
  ```powershell
  OfflineSearchHarness.exe --soak-deploy --fights 200 --seed 4026 --pool high-risk --profile Medium --dop 8 --out .local\tmp\soak
  ```
  - 常用选项：`--pool all|high-risk`、`--profile Medium|High`、`--dop`、`--budget-ms`、`--only-fight <idx>`、`--dump-outcomes <path>`。

## 4. 语料评测、玩家回放与成对 A/B 对比

- **`--replay-bundle <zip>`**：纯 .NET 单包问题排查、锁步差分与部署审计：
  ```powershell
  OfflineSearchHarness.exe --replay-bundle .local\tmp\bundle.zip --lockstep-actions --deploy --dump-diff .local\tmp\diff.json
  ```
- **`--bench-corpora`**：真实固定根语料批量基准：
  - `--tier all|dev|holdout`、`--slice`、`--profile`、`--beam`、`--nodes`、`--dop`、`--budget-ms`、`--search-mode`、`--potion-policy Smart|Disabled`、`--stop-at-zero-loss`、`--filter`、`--dump-outcomes <path>`
- **`--bench-choices`**：Stage 8D 高分支选牌与回合准备基准（`--profile`、`--beam`、`--nodes`、`--dop`、`--budget-ms`、`--filter`、`--strict`、`--dump-outcomes <path>`）。
- **`--bench-player-corpus <manifest.json>`**：玩家击败求解器真实语料批量评测：
  - `--split all|dev|holdout`、`--tier`、`--slice`、`--limit`、`--deploy`、`--no-rollout`、`--dump-outcomes <path>`
- **`--compare-outcomes <base.json> <cand.json> [--out PATH] [--check-exact] [--gate]`**：成对对比两次评测的经济价值 `V`、胜率、战损、中位/`p90` 延迟与重算变化。

## 5. 单根即时求解（无子命令前缀）

直接指定角色与遭遇或生成的测试请求 JSON，执行单次根捕获与搜索：

```powershell
OfflineSearchHarness.exe --character IRONCLAD --encounter GREMLIN_NOB --ascension 20 --profile Medium --dop 8 --potion-policy Smart --out .local\tmp\run1
```

常用参数：`--request <json>`、`--seed <str>`、`--act-index <0..2>`、`--profile`、`--beam`、`--nodes`、`--card-branches`、`--pile-branches`、`--hand-branches`、`--dop`、`--budget-ms`、`--potion-policy`、`--search-mode`、`--stop-at-zero-loss`、`--no-rollout`、`--production-budget`、`--measure-phases`、`--milestone M1|M2`、`--workspace <dir>`、`--out <dir>`、`--language <code>`、`--verbose-game-log`。

## 6. 多代理并发约定

- 并行运行多个 `OfflineSearchHarness` 进程时，不要在 Worker 内重复执行 `dotnet build`；由协调器先统一构建好二进制，再并发调用 `.exe`。
- 每个并发 Worker 建议设置 `--dop 4`（避免 CPU 过度抢占导致耗时指标失真），并通过 `--out` / `--dump-outcomes` / `--dump-dir` / `--workspace` 隔离在各自 `.local/tool-tasks/<task>/` 子目录下。
