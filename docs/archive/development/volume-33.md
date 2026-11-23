# 开发历史卷 33

## 1.0.0 HotKernel v2 新一代高速引擎与 0.113.1 全量适配

- **纯值内核与独立热搜引擎（Phase 0–3）**：新增 `src/HotKernel/`（非托管紧凑状态 `HotCombatState`、静态规则目录、主线程根导入 `HotRootBuilder` / `HotRootBundle` 与零分配模拟器 `HotSimulator`）及 `src/HotSearch/`（独立 `HotSearchCoordinator`、确定性多线程展开 `HotParallelExpander`、`HotBeamRetention`、`HotEvaluator`、`HotPlanMaterializer`、`HotEndTurnRiskEvaluator`）。`HotKernel/State|Sim` 与 `HotSearch` 彻底摆脱 `MegaCrit.Sts2.Core.Models` 和托管对象克隆。
- **旧栈删除与运行时瘦身（Phase 4–5）**：移除原 `SimulatedCombatState`、`CombatPredictionSimulator`、`MethodMirrorRegistry`、46 分片 `CombatBeamSolver`、旧 `CombatSearchCoordinator`、`PowerCardValuation`、`SearchGcPolicy` / NoGC / ServerGC / 克隆隔离补丁、`SystemMemoryReleaseService`、`src/Strategy/` 及全部第三方 Mod 专属适配；`src/Engine/`、`src/Prediction/`、`src/Search/` 仅保留对外异常、根捕获辅助与策略 DTO，`SolverController` 与 `CombatRootSnapshot` 直连 `HotRootBundle` + `HotSearchCoordinator`。同步重写两平台等价 `tools/inspection/verify-refactor-boundaries.{ps1,sh}`，并将 `OfflineSearchHarness` 直接接入 `HotSearchCoordinator`。
- **药水决策与 UI 统一（Phase 6）**：统一智能药水策略的等价省血判定口径，在 `SolverPotionStrategyPanel` 为每瓶可用药水开放独立「省血阈值」（按药水类型保存、支持单瓶恢复默认与一键重置），移除隐藏倍率与旧版内存管理界面项。
- **v0.113.1 模拟与零计划外重算收敛（Phase 6–7B）**：新增离线差分模糊器（`--fuzz-combat`）与多回合部署压测（`--soak-deploy`），全面修复 6 角色在标准／恐惧／极速模式下的卡牌、遗物、能力、药水与怪物结算偏差；优化 `HotEvaluator` 与搜索视野；修复战斗结束后预览多余重复行、全死路状态未正确标为「未找到生还路线」以及自定义 Beam 宽度兼容问题；新增 `src/Runtime/SolverWarmup.cs` 后台预热以消除首场战斗启动卡顿。
- **发布工具与文档收口（1.0.0 定版）**：最小发布 ZIP 与统一发布脚本移除已退役的 `CombatSolver.MemoryCleaner.exe` 依赖，保留 4 个发布文件（`CombatSolver.json`、`CombatSolver.dll`、`LICENSE`、`THIRD_PARTY_NOTICES.md`）；`CombatSolver.json` 保持 `min_game_version = 0.113.0`（同时适配 `0.113.1`）；同步清理 `README.md` 与 `docs/workshop/` 中过时的 16 GB 内存预算／NoGC／ServerGC 说明。

测试证据见[测试卷 33](../testing/volume-33.md)。
