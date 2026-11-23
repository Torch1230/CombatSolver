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

### 未验证范围

- 1.0.0 仅在 `5a859bd6` 行为源码之后修改版本号、发布文档与脚本元数据，沿用上述行为验证证据，未执行可见 Steam 交互排版/帧率完整发布门禁与从 ZIP 干净安装验收。
