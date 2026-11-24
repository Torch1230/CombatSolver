# 测试历史卷 35

## 1.0.2 搜索策略调优与高风险战斗重算清零

基线：基于 `1.0.2`（Wave 4 收口提交 `a9334fc2`）源码与 Release 构建（Windows / STS2 `v0.113.1` / .NET 9），全部行为/性能对比均相对 `1.0.1` 基线执行。后续仅变更版本号、文档与发布元数据。

### 构建、仓库门禁与离线内核门禁（`OfflineSearchHarness`）

- `dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false` 与 `dotnet build tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release` 均为 **0 警告 0 错误**。
- `--check-hot-layout`：**7/7 PASS**（62 ms，`FixedCore=1128 B`，`Total=17256 B`，`2808/2808` 字节翻转与 sidecar 检出）。
- `--audit-hot-coverage`：**0 缺口**，**183/183** 反射校验通过（3,205 ms；覆盖 Cards 673、Powers 290、Relics 314、Potions 67、Monsters+Moves 121、Enchantments 22、Afflictions 7、Orbs 5、Dominions 12）。
- `--verify-hot-parity --dop 8`：**0 failures**——Gate A 19/19 PASS、Gate B1 6 角色 246 步自洽 0 失败、Gate B2 4/4 `DOP=1 == DOP=8`、Gate C `0.51 us/step`（`1,945,775 trans/s`，`0.0 B/step`）。
- `--verify-strategy-policies`：**4/4 套件、25/25 项全通过**（7,655 ms）。
- 四项 L0 仓库门禁全通过：`verify-refactor-boundaries.ps1`（`hot_kernel_files=42 hot_search_files=11 search_dto_files=17`）、`verify-documentation.py`（`files=515 links=1902 errors=0`）、`verify-tools.py`（`files=191 projects=16 errors=0`）、`verify-coverage.py`（`data=618 fixtures=566 groups=38 errors=0`）。

### 2,600 场差分模糊与 400 场部署压测（`OfflineSearchHarness`）

- `--fuzz-combat`（4 种子合计 **2,600/2,600 PASS**、**62,910 步**、**`diff_buckets = 0`**、**`crash_buckets = 0`**，加 Wave 1–4 全量复现包 `--replay-fuzz` 0 diffs）：
  - `seed=7026 --fights 800 --phase high-risk`：**800/800**（19,945 步，129.77 s）
  - `seed=11026 --fights 800 --phase high-risk`：**800/800**（19,245 步，140.35 s）
  - `seed=12026 --fights 500 --phase all`：**500/500**（11,451 步，94.24 s）
  - `seed=13026 --fights 500 --phase high-risk`：**500/500**（12,269 步，97.23 s）
- `--soak-deploy --fights 200 --seed 7777 --pool all --profile Medium --dop 4`（246.71 s）：胜率 **`95.0% -> 95.5%`**（`191W/9L`，`+1` L->W、`0` W->L），成对 **`B:W = 16:10 (1.60x)`**，**`unplanned_replan_count = 0 / 986 turns`**（较 `1.0.1` `1 -> 0`，`reused = 786`），**`choice_mismatch_count = 0`**，**`hard_error_count = 0`**，`p50_solve_ms` `922.0 -> 728.7 ms (-21.0%)`、`p95_solve_ms` `4170.2 -> 3378.6 ms (-19.0%)`，`custom_beam135_stage2_ok = True`。
- `--soak-deploy --fights 200 --seed 7026 --pool high-risk --profile Medium --dop 4`（383.65 s）：胜率 **`96.5%`**（`193W/7L`，平均 V `-7.96`），**`unplanned_replan_count = 0 / 983 turns`**（后继回合 `783/783` 100% 复用），**`choice_mismatch_count = 0`**，**`hard_error_count = 0`**，`p50_solve_ms = 949.5 ms`、`p95_solve_ms = 5846.6 ms`，`custom_beam135_stage2_ok = True`。

### 170-Root 搜索策略 A/B 对照（`--profile Medium --dop 4` vs `1.0.1`）

- **总计 170 局**：胜场 `164/170`（`PB-097` L->W、`PB-052` W->L），Avg V **`-15.25 -> -13.25 (+2.00)`**，平均掉血 `14.76 -> 14.56 (-0.20)`，总耗时 **`565.95 s -> 458.52 s (-19.0%)`**（`p50 -23.5%`、`p95 -23.4%`），Win+V `B:W = 22:20 (1.10x)`。
- **分套件**：`--bench-corpora`（18 局）胜率 `17/18`、Avg V **`0.00`**（`-18.67 -> -18.67`，Win+V `1/17/0` 零回退、耗时 `-12.6%`）；`--bench-choices`（17 项）**`17/17 OPT_PASS`**（耗时 `-12.9%`）；`--bench-player-corpus`（135 局）`dev`（94 局）Avg V **`+3.84`**（`B:W = 21/15 = 1.40x`、Win+V `1.60x`），`holdout`（41 局）Avg V **`-0.51`**（`PB-075` `+11.00`、`PB-026` `+342.45`、单例 `PB-052` `W->L` 待后续跟进）。

### 未验证范围

- 本批次行为验证在离线 `.NET` 宿主（`OfflineSearchHarness`）中完成，发布准备阶段将对提升至 `1.0.2` 的源码执行正式发布构建并产出最小 ZIP；真实可见 Steam 会话内的交互手感以及从最终发布 ZIP 的干净安装冒烟验收尚未执行。
