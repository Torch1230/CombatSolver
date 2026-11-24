# 测试历史卷 33

## 1.0.0 HotKernel v2 新一代高速引擎与 0.113.1 适配

基线提交：`5a859bd6`（Phase 7B 全量验证；Windows / STS2 `v0.113.1` / .NET 9）。

### 构建与结构门禁

- `CombatSolver.csproj -c Release` 与 `tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release` 均为 0 警告 0 错误。
- 全部仓库与独立门禁通过：`verify-refactor-boundaries.ps1`（`REFACTOR_BOUNDARIES_OK`）、`verify-tools.py`（`TOOLS_OK`）、`verify-documentation.py`（`DOCUMENTATION_OK`）、`verify-coverage.py`（`COVERAGE_OK`）及 `run-independent-checks.py`（6/6 通过）。

### 离线内核布局、覆盖、自洽与确定性门禁（`OfflineSearchHarness`）

- `--check-hot-layout`：7/7 通过（33 ms，0 B 分配）。
- `--audit-hot-coverage`：0 缺口，183/183 反射检查通过（1,601 ms）。
- `--verify-hot-parity`：全部通过（0 失败）——Gate A 19/19 根捕获与 Pack/Unpack 往返、Gate B1 六角色 245 步自洽、Gate B2 DOP=1 与 DOP=8 4/4 比特级一致、Gate C `0.45 us/step`（`2,233,449 trans/s`，`0.0 B/step`）。

### 规模化部署压测与差分模糊（`OfflineSearchHarness`）

- `--soak-deploy --fights 120 --dop 8`（Medium、`seed=2026`、29.65 s，全部 6 角色 × 标准／恐惧／极速模式）：`unplanned_replan_count = 0 / 585 turns`（`0.00%`，`reused = 464`，`planned_boundary = 1`，`SOAK_EXIT=0`），`hard_error_count = 0`，`choice_mismatch_count = 0`，`wins = 114 / 120`（`95.0%`），`custom_beam135_stage2_ok = True`（`beam=135: 50ms`，`beam=180: 62ms`），`p50_solve_ms = 64.0 ms`，`p95_solve_ms = 286.5 ms`，`peak_rss_mb = 455.6 MB`，`gc_gen2 = 7`。
- `--fuzz-combat --fights 300 --phase all`（`seed=2026`、38.80 s、6,556 锁步）：`pass_fights = 280 / 300`（`93.3%`，前 40 场 `40/40`），`crash_buckets = 0`，剩余 7 类已知边界差分桶（`diff_buckets = 7`）；100% 单人实体覆盖扫描（Cards 673/673、Powers 266、Relics 194、Potions 67/67、Monsters 106、Enchantments 22/22、Afflictions 7/7、Orbs 5/5、Dominions 12/12）。

### 离线搜索性能对照（Phase 7B vs Phase 0，`--workers 1`）

- Evaluate DOP=1（4 根）：`86.09 s -> 3.21 s`（`26.81x` 提速），最大峰值工作集 `285.6 MB`，总 GC `6.2 ms`；`GA-IRONCLAD-ELITE-00` 正确标记 `OnlyDeathRoutes=True`。
- Coordinator DOP=8（6 根，6/6 `Boundary=None` 且 6/6 胜利）：`277.15 s -> 5.26 s`（`52.70x` 提速），总分配 `114.74 GB -> 2.77 GB`（降低 `41.4x`），最大峰值工作集 `414.4 MB`，总 GC `11.2 ms`（`dev-00` 61.11x、`dev-01` 69.49x、`dev-02` 32.26x、`dev-03` 74.19x、`dev-04` 22.30x 且恢复 22/1/W、`dev-08` 43.43x）。

### 原生无头冒烟与首战卡顿验证

- `IRONCLAD-AUTO-DEPLOY-CONTRACT`（`runId=9d9f6e1801644c6eb5b53bc5bdf5c360`，`-CleanupInstanceOnExit`）：Passed（`CombatRootSnapshot`、`UnexpectedReplans:0`、`NativeOutcome:combatEnded=True:turn=2:hp=80:maxHp=80`，隔离实例已清理）。
- 首战主线程卡顿验证（`generation=1`）：`COMBAT_ROOT_CAPTURE elapsed_ms=2.031 ms`，`MAIN_THREAD_FRAMES max_gap_ms=96.0 ms`（`over_100ms=0`，较此前 `1,163–1,325 ms` 下降超过 12 倍），`enter_encounter` 从 `2,247.9 ms` 降至 `1,152.1 ms`。

### 1.0.0 发版前收口优化与盲测验证（基于 1ca3d6ce 工作树）

- **构建与门禁**：`CombatSolver.csproj` 与 `OfflineSearchHarness.csproj` Release 构建均 `0` 警告 `0` 错误；`verify-refactor-boundaries.ps1`（`pwsh`）、`verify-coverage.py`、`verify-tools.py`、`verify-documentation.py`、`--check-hot-layout`（7/7，0 B）、`--audit-hot-coverage`（0 缺口，183/183）、`--verify-hot-parity --dop 8`（`DOP=1 == DOP=8`，`2.12M trans/s`，`0.0 B/step`）全部通过。
- **定向证明**：`req_arcane.json`（Medium，`--production-budget`，`--dop 8`）0 战损第 3 回合获胜（`finalHp=60`）；`--bench-choices --dop 8` `17/17 100% OPT_PASS`（0 重算）；Pillar 2 前期 `24` 份最小差分复现 JSON `24/24 PASS`。
- **既有真实根与压测**：
  - Tier-1 18 根（`--dop 8`）：`17/18 (94.4%)` 胜（`GA-IRONCLAD-ELITE-00` 获胜，`dev-06-silent-boss` 转胜，`13B/3E/2W` vs `v100`，`6B/8E/4W` Avg V `+3.34` vs `w2`，`Peak RSS=648.9 MB`，0 重算）。
  - Tier-2 `seed=2026` 120 场（`--dop 8`）：`115/120 (95.8%)` 胜、`unplanned_replan_count=0/583`（`0.00%`）、`hard_error_count=0`、`choice_mismatch_count=0`、`Avg HP Loss=13.24`、`Avg V=-17.75`、`Peak RSS=508.4 MB`（`33B/76E/11W` vs `w2`）。
- **新种子千场扫测（`seed=3026`）**：
  - 500 场 `--soak-deploy --dop 6`：`488/500 (97.6%)` 胜、`hard_error_count=0`、`choice_mismatch_count=0`、`unplanned_replan_count=9/2305 (0.39%)`（由 57 降至 9，降幅 84.2%）、`Avg HP Loss=11.53`、`Avg V=-14.67`、`Peak RSS=1156.2 MB`。
  - 500 场 `--fuzz-combat --phase all`：`491/500 (98.2%)` 通过（由 `434/500` 提升 `+11.4 pp`）、`crash_buckets=0`、剩余 `diff_buckets=5`（由 24 降至 5），11,103 步全单人实体覆盖（Cards 673, Powers 267, Relics 194, Potions 67, Monsters 107, Enchantments 22, Afflictions 7, Orbs 5, Dominions 12）。
- **真实玩家击败语料（135 例：`94 dev` + `41 holdout` 盲测解封，`--profile Medium --dop 8 --budget-ms 15000`）**：
  - `dev (94)`：`90/94 (95.7%)` 胜、Raw `>= 玩家 51/20/23 (75.5%)`、Canonical V `>= 玩家 50/20/24 (74.5%)`（Avg V `-19.76 vs -23.76`）、`vs Old Solver 83B/5T/6Bh (93.6%)`、0 错 0 重算。
  - `holdout 盲测 (41)`：`41/41 (100.0%)` 胜、Raw `>= 玩家 22/9/10 (75.6%)`（较 `s8bcd holdout 46.3%` 提升 `+29.3 pp`）、Canonical V `>= 玩家 24/9/8 (80.5%)`（Avg V `-10.59 vs -12.54`）、Avg HP Loss `10.51 vs 10.93`（Pot `0.56 vs 0.78`）、`vs Old Solver 37B/1T/3Bh (92.7%)`、0 错 0 重算。
  - `合计 (135)`：`131/135 (97.0%)` 胜、Raw `>= 玩家 73/29/33 (75.6%)`（较 `s8bcd ALL 45.2%` 提升 `+30.4 pp`）、Canonical V `>= 玩家 74/29/32 (76.3%)`（Avg V `-16.98 vs -20.35`）、Avg HP Loss `14.94 vs 15.09`（Pot `0.53 vs 0.75`）、`vs Old Solver 120B/6T/9Bh (93.3%)`、0 错 0 重算。
- **盲测 `seed=9001` Soak（120 场，`--profile Medium --dop 8`）**：`116/120 (96.7%)` 胜、`hard_error_count=0`、`choice_mismatch_count=0`（`v100` 与 `s8bcd` 原各含 1 次硬错误与 1 次选牌错配）、`unplanned_replan_count=2/574 (0.35%, reused=452)`（`s8bcd` 为 4 次）、`Avg HP Loss=13.72`、`Avg V=-16.34`、`Peak RSS=524.8 MB <= 650 MB`（`vs v100: 52B/59E/9W, +3 W`；`vs s8bcd: 39B/67E/14W, +1 W`）。

### 未验证范围

- `5a859bd6` Phase 7B 定稿与上述 `1ca3d6ce` 发版前收口验证均基于离线宿主与无头合同；可见 Steam 会话下的交互排版、动画、实机帧率以及从最终发布 ZIP 的干净安装验收尚未执行，将在最终发版前／发版门禁中核验。
