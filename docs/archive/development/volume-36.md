# 开发历史卷 36

## 1.0.3 STS2 v0.114.0 全量适配、发布安全加固与 30s/600s 求解时长调整

- **STS2 `v0.114.0` 语义与目录全面适配（RitsuLib `0.6.16`）**：
  - **目录、状态与导入层**：更新 `CombatSolver.csproj` / `OfflineSearchHarness.csproj` / `CoverageCatalog.csproj` / `CombatSolver.json` 至 `0.114.0` + RitsuLib `0.6.16`。`HotEnums`/`HotCatalog`/`HotRootBuilder`/`HotContinuationStamp` 新增 `Optimism`、以 `TwinShot` 替换 `RapidShot`，移除已删除的 `FreeCardPower`、`ArrowSlot.DestroyAfterImpaled`、`ArrowStatus.Destroyed`、`TheShot._cardsExhaustedByFlorescence` 与 `HotCardPrivateKind.TheShot`；按源码实值校正 51 张修改卡（含 `ChoReady` `7(+2)` 格挡 + `3(+1)` Aim 等）与 `HotFoolState.ArrowValidMask` 掩码，补全 `HappyFlower`/`FakeHappyFlower` 跨 `BeforeSideTurnStart`→`AfterPlayerTurnStartEarly` 的 `_grantEnergyThisTurn` 反射导入、`StretchPower` 的 `amountWhenCardStarted` 导入、`UnmovablePower` 按独立 `CardPlay` 计数及战前阵亡 `Wuppy` 导入。
  - **Fool / 塔罗与核心模拟内核**：`HotFoolTarot` 对齐 `Optimism`、`TwinShot`、`Growth`（格挡 + 克隆变悲伤）、`Florescence`→`TheShot(+)`、`Tune`/`TunePower`（首次打 Ki 旋律升级手牌）、`ArrowStab`、`Recall`、`TrickShot`、`WahKi`（弃牌置顶）、`WahChoKi`（先伤后盾）、`PunchOfJoy`、`Vitriol`、`QuietusPower`（锁存首张来自箭矢的攻击且不再销毁箭矢）、`HeavyShot`（Aim 3/4 倍）、`WahSymphony`/`KiVersePower` 标签收缩、`HoningPower`（+100%）、`SerratedArrowsPower`（50%/75%）、`MelodyCmd` 改看 `MelodyStarted`、`Timber` 单 `AttackContext`、`BlotOutTheSky` 跨职业拉取 Fool 卡池、`ArrowCmd` 目标阵亡立即收回箭矢、`TheFool` 跳过不可打牌、`TheHierophant` 移除减费、`TheSun` 洗 2 张 `Soot` 入抽牌堆，并修正 `HotEvaluator` 就绪箭矢判断极性；同步更新 `HotPlanMaterializer` 与 `HotRolloutEvaluator` 选牌/变形映射。`HotCombatEngine` 将 `HardToKillPower`/`HardenedShellPower` 迁至 `ModifyHpLostBeforeOstyLate`，新增 `AttackingCreatureSlotMask`/`PerformingMoveEnemyMask`/`DeferredDeathCleanupSlotMask` 延迟死亡清理，移除 `PainfulStabsPower` 死后残留，并修复 `EnsemblePower` 敌我减费/末段减层与召唤怪时遗物早于 Dominion 的钩子顺序。
- **发布包安全与隐私加固（P0-1 / P0-2 / P0-3 / P1-1 / P1-2）**：
  - 删除 `CombatShowcaseCollector.cs`、`CombatShowcaseModEligibility.cs` 及 `CombatSolver.csproj` / 构建脚本 / UI 中的自动录像上传与 `Showcase*` 元数据（保留本地回放 `ProtocolVersion = 1` 于 `CombatShowcaseRuntime.cs`）。
  - 在 `CombatSolver.csproj` 默认关闭 `EnablePerformanceRecording` 并移除 `src/Diagnostics/**/*.cs`，用 `#if ENABLE_PERFORMANCE_RECORDING` 门控日志、调度器与诊断测试入口，并在 `verify-release-connection-metadata.ps1` 新增 `Assert-ReleaseSecurityInvariants`（`PEReader` + UTF-8/UTF-16LE 原生扫描禁用类型、元数据与 `powershell.exe` / `ExecutionPolicy` 等字符串）。
  - 在 `Entry.cs` 增加 `IsHeadlessAutomationEnabled` 门控无头测试补丁与 `UnattendedTestRunner` 轮询；在 `UnattendedTestRunner.ProtocolHost.cs` 与 `PreCombatForecastWorker.cs` 限制预战 Worker 仅接收预战场景并锁定 `RunSnapshotPath` 于 `OS.GetUserDataDir()` 内；将 `PinMainProcessModSources()` 改为首次调用预战 API 时懒加载，并在 `CombatBugReportExporter.cs` 脱敏 `%USERPROFILE%` 路径。
- **最大求解时长调整（默认 `30s` / 上限 `600s`）**：
  - 将 `SolverSettings`（`CurrentPerformanceMigrationVersion` 升至 `248`，将仍为 `247` 且处于旧默认 `4` 秒的配置迁移为 `30` 秒，保留自定义时长）、`SearchPolicySnapshot`、`SolverSearchProfile` 与性能页 UI/双语文案统一更新为默认 `30` 秒、范围 `1–600` 秒。

测试证据见[测试卷 36](../testing/volume-36.md)。
