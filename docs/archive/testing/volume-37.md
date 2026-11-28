# 测试历史卷 37

## 1.0.4 原生 Rust 并行战斗内核集成（PR #278）与审计加固

基线：基于 `1.0.4` 源码与 Windows MSVC Release 构建（`rustc 1.100.1` / `cargo 1.100.1` / .NET 9 / STS2 `v0.114.0` / RitsuLib `0.6.16`），并经两轮独立只读 Codex (`gpt-6.2-astra`, `high`) 外审与 Diff-First 终审收口。

### 本轮 Windows 实测门禁（全部 PASS）

- **Rust 工作区 Release 测试**：
  - `cargo test --release -p sts2-kernel`：**14/14 PASS**（3 unit + 11 `kernel_primitives`）
  - `cargo test --release -p sts2-ffi`：**10/10 PASS**（1 unit `map_policy_clamps_search_budgets_to_overall_timeout_when_positive` + 9 `ffi_smoke`，含 `ffi_host_v1_rejects_unsatisfied_hard_potion_policy_and_propagates_unsupported_expansion` 与全禁药水带 `RequireAtLeastOne` 一致性回归）
  - `cargo test --release -p sts2-search --lib`：**29/29 PASS**（含 `select_final_plan_returns_none_when_hard_potion_constraint_unsatisfied`）
  - `cargo test --release -p sts2-search --test integration_tests`：**35/35 PASS**（含 `hard_potion_policy_unsatisfied_returns_error_instead_of_non_compliant_win`、`unsupported_branch_error_during_expansion_propagates_instead_of_pruning` 与 `determinism_dop_1_4_16_*`）
- **双路径构建与发布安全门禁**：
  - 带原生 Rust DLL 的正式发布构建（`PublicationBuild=true`、`RequireRustCombatKernel=true`、`CopyModOnBuild=false`）：C# **0 警告 0 错误**，编译并落位 `sts2_combat_rs.dll`（`lto = "fat"`, `6.07 MB`），通过 `Assert-ReleaseConnectionMetadata` + `Assert-ReleaseSecurityInvariants`。
  - 纯 C# 托管切换验证（`-p:StageRustCombatKernelEnabled=false`）：**0 警告 0 错误**，自动清理残留 `rust-kernel/`。
  - `OfflineSearchHarness.csproj` Release：**0 警告 0 错误**。
- **静态检查与离线内核对拍门禁**：
  - `run-independent-checks.py`：**6/6 PASS**；`verify-refactor-boundaries.ps1`：**PASS**；`verify-documentation.py`：**PASS（0 errors）**。
  - `OfflineSearchHarness`：`--check-hot-layout` **PASS**、`--audit-hot-coverage` **PASS**、`--verify-strategy-policies` **4/4 suites, 25/25 checks PASS**、`--verify-hot-parity --dop 4`（Gate A 19/19、Gate B1 6 角色 246 步、Gate B2 DOP1==DOP8 零差异、Gate C `1,118,328 trans/s`, `0 B/step`）**PASS**。

### PR #278 历史基线（作者贡献分支提交时在 Linux 上报告，本轮 Windows 未重跑）

- Linux 全量无头合同与 `153 场 / 295 根 RustKernel ↔ HotKernel` 差异对拍（`fallbacks=0, divergences=0`）见 PR #278 记录。

### 未验证范围

- 当前实机 `SlayTheSpire2.exe` 正在运行中，为遵守不强杀游戏进程规范，本轮未覆盖游戏 `mods/CombatSolver/` 目录，也未在可见 Steam 会话中实测 `1.0.4` ZIP 干净安装及 Windows 无头 `InCombatRustKernelContract`；待关闭游戏部署后即可实机复核。
