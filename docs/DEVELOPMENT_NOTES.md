# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

## HotKernel v2 架构重写与旧栈清理（开发中）

- **纯值内核与独立热搜引擎（Phase 0–3）**：新增 `src/HotKernel/`（非托管紧凑状态 `HotCombatState`、静态规则目录、主线程根导入 `HotRootBuilder` / `HotRootBundle` 与零分配模拟器 `HotSimulator`）及 `src/HotSearch/`（独立 `HotSearchCoordinator`、确定性多线程展开 `HotParallelExpander`、`HotBeamRetention`、`HotEvaluator`、`HotPlanMaterializer`、`HotEndTurnRiskEvaluator`）。`HotKernel/State|Sim` 与 `HotSearch` 彻底摆脱 `MegaCrit.Sts2.Core.Models` 和托管对象克隆。
- **旧栈删除与运行时瘦身（Phase 4）**：移除原 `SimulatedCombatState`、`CombatPredictionSimulator`、`MethodMirrorRegistry`、46 分片 `CombatBeamSolver`、旧 `CombatSearchCoordinator`、`PowerCardValuation`、`SearchGcPolicy` / NoGC / ServerGC / 克隆隔离补丁、`src/Strategy/` 及全部第三方 Mod 专属适配；`src/Engine/`、`src/Prediction/`、`src/Search/` 仅保留对外异常、根捕获辅助与策略 DTO，`SolverController` 与 `CombatRootSnapshot` 直连 `HotRootBundle` + `HotSearchCoordinator`。
- **门禁、测试与离线宿主收口（Phase 4–5）**：重写两平台等价 `tools/inspection/verify-refactor-boundaries.{ps1,sh}`，清理 `src/Testing/` 过时旧内部合同与 29 个失效独立检查项目，将 `OfflineSearchHarness` 直接接入 `HotSearchCoordinator`（保留 `--check-hot-layout` 并将 `--verify-hot-parity` 切换为 Gate A/B/C 自洽与确定性/微基准验证），删除 `.local/tool-tasks/ref-sts2-solver` 并同步架构与测试文档。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

0.51.0 的标准模式适配、请求预算显示和路线优化见[历史卷 23](archive/development/volume-23.md)，玩家说明见[0.51.0 更新日志](releases/0.51.0-RELEASE_NOTES.md)。

0.51.1 的恐惧／极速模式适配与实例状态修复见[历史卷 24](archive/development/volume-24.md)，玩家说明见[0.51.1 更新日志](releases/0.51.1-RELEASE_NOTES.md)。

0.51.2 的属性削减、诅咒持续时间和仪式兽修复见[历史卷 25](archive/development/volume-25.md)，玩家说明见[0.51.2 更新日志](releases/0.51.2-RELEASE_NOTES.md)。

0.51.3 的同牌多减益与部署异常修复见[历史卷 26](archive/development/volume-26.md)，玩家说明见[0.51.3 更新日志](releases/0.51.3-RELEASE_NOTES.md)。

0.51.4 的战斗逻辑与选牌续接修复见[历史卷 27](archive/development/volume-27.md)，玩家说明见[0.51.4 更新日志](releases/0.51.4-RELEASE_NOTES.md)。

0.51.5 的结算顺序与运行时交接修复见[历史卷 28](archive/development/volume-28.md)，玩家说明见[0.51.5 更新日志](releases/0.51.5-RELEASE_NOTES.md)。

0.51.6 的社区 PR 整合与日志上传修复见[历史卷 29](archive/development/volume-29.md)，玩家说明见[0.51.6 更新日志](releases/0.51.6-RELEASE_NOTES.md)。

0.51.7 的薄荷尖塔兼容与诊断取证修复见[历史卷 30](archive/development/volume-30.md)，玩家说明见[0.51.7 更新日志](releases/0.51.7-RELEASE_NOTES.md)。

0.51.8 的治疗补丁启动修复见[历史卷 31](archive/development/volume-31.md)，玩家说明见[0.51.8 更新日志](releases/0.51.8-RELEASE_NOTES.md)。

0.51.9 的愚者能力牌路线优化见[历史卷 32](archive/development/volume-32.md)，玩家说明见[0.51.9 更新日志](releases/0.51.9-RELEASE_NOTES.md)。
