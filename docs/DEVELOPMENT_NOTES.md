# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

## 下一版本（开发中）：原生 Rust 战斗内核集成

- 移植独立 `rust/` 原生战斗内核与并行搜索引擎（`sts2-types` / `sts2-kernel` / `sts2-content` / `sts2-search` / `sts2-ffi`），经 `tools/build/RustCombatKernel.targets` 自动构建并随 Mod 发布至 `rust-kernel/`，Release 配置启用 `lto = "fat"` 与 `codegen-units = 1`。
- 内核热路径采用 `ActiveHookCache` SoA 扁平监听数组（`flat_masks_w0`..`w2` 与 `flat_listeners`）、0/1 监听器无返回值快路径与纯查询 SoA 直扫、`CategoryHookMasks::deck_cards` 牌库直跳、`powers/batch_n_z` 下沉 `owner_side` 判定，以及 `commands/damage` 轻量数组优化。
- 搜索与状态序列化采用同根共享 `RootStaticContext`、进程内 `Cow<'static, str>` 指针编码、`u16` 卡牌句柄紧凑编码、批量切片解码、零分配流式 128 位规范哈希（纳入 `semantic_history`、`damage_history` 空历史快路径）、16 字节 `Copy` `CompactRolloutStep` + `RolloutChoiceSlab` + `PilotIntentCache` 推演缓存、`ChoiceArena` 扁平选牌暂存区、40 字节 `StoredFastEval` + 160 字节 `SearchNode` 布局、紧凑 `PruneKey` 与快速有界堆多桶并行剪枝，以及批量阈值 `total_cands >= 1024` 的并行分片 Pareto 转置表；全程保留全部搜索算法超参数并保证 `DOP 1 / 4 / 8 / 16` 严格确定性。
- 新增纯 Rust 战斗内主机桥接与准入链（`InCombatRustKernelBridge` / `Eligibility` / `PolicyMapper` / `InCombatLiveStateCapturer` / `InCombatSolverBackendState`），完整捕获已解锁角色/无色/诅咒/跨角色/塔罗卡牌池与药水池，覆盖 `SolverSettings.UseRustCombatKernel`（默认开启）、全部 1.0.0 搜索/药水/成长/遗物/首领血量政策映射，并在 FFI 边界输出结构化诊断结果与失败码。
- 新增 `InCombatRustKernelResultMaterializer`（回传 `ChoiceBranchesEvaluated`）与 `HotPlanMaterializer.MaterializeFromSteps`，在采纳前于 `HotRootBundle` 上逐动作与终局状态回放校验 Rust 最优路线；任一排除、损坏、超时或失配自动回退至 `HotKernel`。
- 性能设置页新增“战斗内优先使用 Rust 内核”开关，主界面摘要栏新增引擎状态徽章与双语本地化。

1.0.0 的 HotKernel v2 新一代高速引擎、药水省血阈值与 0.113.1 全量适配见[历史卷 33](archive/development/volume-33.md)，玩家说明见[1.0.0 更新日志](releases/1.0.0-RELEASE_NOTES.md)。

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
