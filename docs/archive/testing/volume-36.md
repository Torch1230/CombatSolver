# 测试历史卷 36

## 1.0.3 STS2 v0.114.0 适配与安全加固

基线：基于 `1.0.3` 源码与 Release 发布构建（Windows / STS2 `v0.114.0` / RitsuLib `0.6.16` / .NET 9），全部行为验证均在本地 `.local/ritsulib-0.6.16` 与实机 `sts2.dll` (`v0.114.0`) 上执行。

### 构建、发布安全扫描与仓库门禁

- `pwsh -NoProfile -File tools/build/build-local-stack.ps1 -ForPublication` 与 `dotnet build tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release` 均为 **0 警告 0 错误**。
- `Assert-ReleaseConnectionMetadata` + `Assert-ReleaseSecurityInvariants`（`tools/release/verify-release-connection-metadata.ps1`）：已验证 `PresenceEndpoint`/`PresenceCertificateSha256` 有效，且正式 Release `CombatSolver.dll` 零 `Showcase*` 元数据、零 `PerformanceRecording`/`PerformanceSession`/`PerformanceLifecycle`/`CombatShowcaseCollector`/`ShowcaseTransport` 类型、零 `powershell.exe`/`ExecutionPolicy` 等禁用字符串。
- 仓库 L0 门禁与独立检查全通过：`run-independent-checks.py`（`total=6 passed=6 failed=0`）、`verify-refactor-boundaries.ps1`（`REFACTOR_BOUNDARIES_OK hot_kernel_files=42 hot_search_files=11 search_dto_files=17`）、`verify-documentation.py`（`DOCUMENTATION_OK files=521 links=1914 errors=0`）、`verify-tools.py`（`TOOLS_OK files=191 projects=16 errors=0`）、`verify-coverage.py`（`COVERAGE_OK data=618 fixtures=566 groups=38 errors=0`）。

### 离线内核门禁、800 场差分模糊与 200 场部署压测（`OfflineSearchHarness`）

- `--check-hot-layout`：**PASS**（`FixedCore=1128 B`，`Total=17256 B`，2,808/2,808 字节翻转与 sidecar 检出）。
- `--audit-hot-coverage`：**0 缺口**，**185/185** 反射校验通过（覆盖 Cards 674、Powers 289、Relics 314、Potions 67、Monsters+Moves 121、Enchantments 22、Afflictions 7、Orbs 5、Dominions 12）。
- `--verify-hot-parity --dop 8`：**0 failures**（Gate A 19/19、Gate B1 6 角色 246 步、Gate B2 DOP1==DOP8 0 失败）。
- `--verify-strategy-policies`：**4/4 套件、25/25 项全通过**。
- `--fuzz-combat`（2 种子合计 **800/800 PASS**、**19,334 步**、**`diff_buckets = 0`**、**`crash_buckets = 0`**）：
  - `seed=4026 --fights 500 --phase high-risk`：**500/500**（12,411 步，69.56 s，覆盖 Cards 654 / Powers 270 / Relics 178 / Potions 67 / Monsters 101）
  - `seed=11400 --fights 300 --phase all`：**300/300**（6,923 步，37.64 s，覆盖 Cards 674 / Powers 264 / Relics 192 / Potions 67 / Monsters 106）
- `--soak-deploy --fights 200 --seed 4026 --pool high-risk --profile Medium --dop 8`（247.84 s）：**200/200 PASS**（胜率 `96.0%`，`192W/8L`，`hard_error_count = 0`，`choice_mismatch_count = 0`，`unplanned_replan_count = 0 / 994 turns`，后继复用 `794` 次，`p50_solve_ms = 515.4 ms`、`p95_solve_ms = 4180.7 ms`，`custom_beam135_stage2_ok = True`）。

### 未验证范围

- 无头端到端冒烟 `run-unattended-test.ps1 -ScenarioId SMOKE-001` 在孤立实例拉起阶段于 `tools/testing/headless-runtime.ps1:621` 因 `Get-HeadlessProcessIdentity` 读取子进程路径为空失败（退出码 1，实例目录已自动清理）；真实可见 Steam 会话内的交互体验与最终 4 文件发布包的干净安装冒烟验收尚未执行。
