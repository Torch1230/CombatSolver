---
name: architecture-boundary-refactor
description: Refactor module ownership, dependency boundaries, or project layout across HotKernel, HotSearch, Search, Prediction, Runtime, UI, Api, and Testing.
---

# Architecture Boundary Refactor

调整源码目录归属、跨层依赖、公共接口或 Runtime/UI 投影边界时使用本指南。目标是让结构清晰、边界少而硬，同时确保构建与双平台边界检查始终同步。

## 1. 当前分层与核心边界

完整入口索引见 [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md)，两平台结构门禁由 `tools/inspection/verify-refactor-boundaries.ps1` 与 `.sh` 共同守护：

- **依赖隔离**：`HotKernel`、`HotSearch`、`Search` 不得引用 `SolverSettings.Current`、`Entry.Logger`、`SolverController`、`SolverOverlay`、`SolverText`、`SolvedRouteCache`、`RunStatistics`、`UnattendedTestRunner` 等外层单例。
- **游戏模型边界**：仅 `src/HotKernel/Import/`、`src/Prediction/` 与 `src/Runtime/`、`src/UI/` 可接触 `MegaCrit.Sts2.Core.Models`；`HotKernel/State/`、`HotKernel/Sim/` 与整个 `HotSearch/` 保持零游戏模型引用。
- **异常处理**：`HotKernel` 与 `HotSearch` 内禁止宽泛 `catch (Exception)` 吞掉语义错误。
- **Runtime 会话所有权**：`SolverController` 通过 `SolverControllerSessions.cs` 中的独立会话类管理生命周期与取消令牌，主线程通过 `CombatRootSnapshot` + `HotRootBuilder.BuildFromLive(` 采集稳定根，交由 `HotSearchCoordinator.Solve(` 求解。
- **UI 只读投影**：`SolverOverlaySnapshot` 是搜索结果到界面渲染的唯一边界，`SolverOverlay` 等控件只读快照，不反查搜索内部状态。
- **不复活旧架构**：`SimulatedCombatState`、`CombatBeamSolver`、`PredictionStateStore`、`SearchGcPolicy` 等旧栈类型已彻底退役，重构时不重新引入。

## 2. 执行与同步要求

1. **保持双平台脚本等价**：若新增核心入口文件、重命名边界类型或调整允许依赖，**在同一提交内同步更新 `verify-refactor-boundaries.ps1`、`verify-refactor-boundaries.sh` 与 `docs/ARCHITECTURE.md`**。
2. **先重构、再统一构建验证**：跨文件拆分或移动类型时先梳理调用链与 namespace，避免零散试编译；全部改动到位后再做 Release 构建与检查。
3. **验证命令**：
   ```powershell
   dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
   dotnet build tools\search\OfflineSearchHarness\OfflineSearchHarness.csproj -c Release
   pwsh -NoProfile -File tools\inspection\verify-refactor-boundaries.ps1
   python -B tools\inspection\verify-documentation.py
   ```
   （在 Linux/macOS 环境同步确认 `verify-refactor-boundaries.sh` 通过。）
