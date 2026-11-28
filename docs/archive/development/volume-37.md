# 开发历史卷 37

## 1.0.4 原生 Rust 并行战斗内核集成（PR #278）、隐身直播 API 与离线批测升级

- **原生 Rust 并行战斗内核与主机桥接集成（合并 PR #278，贡献者 `ltlly`）**：
  - 集成独立 `rust/` 工作区（`sts2-types` / `sts2-kernel` / `sts2-content` / `sts2-search` / `sts2-ffi`），经 `tools/build/RustCombatKernel.targets` 构建并随 Mod 分发至 `rust-kernel/`，Release 启用 `lto = "fat"` 与 `codegen-units = 1`。
  - 同步适配 STS2 `v0.114.0` 全实体（717 卡、303 Power、315 遗物、67 药水、112 怪物、22 附魔、7 灾厄、12 领域与 3 跑局模型，FFI 版本锁 `v0.114.0`）、Daily 模式及全部 16 项原版 `ModifierModel`、`SurroundedPower` 朝向与回合初暂停交错时序。
  - 性能设置页新增“战斗内优先使用 Rust 内核”开关，主界面摘要栏新增引擎状态徽章与双语本地化；采用 **C# 主干权威 + Rust 社区跟进加速** 维护分工。
- **PR #278 审计加固与构建/打包治理（两阶段独立 Codex 外审收口）**：
  - **硬药水策略严格闭环**：Rust 侧在强制药水指令或 `RequireAtLeastOne` 未被满足时显式返回 `Failed / hard_potion_policy_unsatisfied`（含空/全禁药水带，与 C# `HotSearchCoordinator` 一致）；C# `InCombatRustKernelPolicyMapper` 在中盘已用药后按 `Smart` 映射有效策略，且 `InCombatRustKernelResultMaterializer` 在采纳/缓存前强制校验 `HotPotionPolicy.SatisfiesHardConstraints` 与单槽位准入。
  - **严格回放、不支持分支传播与异常兜底**：移除 `InCombatRustKernelResultMaterializer` 中静默退选第一张同名牌与 `targetIndex` 回退，补全 `SourcePile`/`Timing`、击杀敌人身份与 `CumulativePlayerHpLost`/`RecoveredPlayerHp`/`Energy`/`Stars`/`HandCount`/`ShufflesCrossed` 终局校验；Rust 扩展循环上抛 `KernelErrorKind::Unsupported`（如 `DistilledChaos` 触发选牌）为 `CapabilityMiss` 触发 `HotKernel` 回退；修复 `coordinator.rs` 战前掉血双算、`host_wire.rs` 总超时夹紧及用户停止/超时回放竞态；C# 激活校验 `SupportedGameVersion`、禁止卸载活跃 Rayon 线程池 DLL、代际隔离 `InCombatSolverBackendState` 并从 `SolverOverlaySnapshot.BackendDisplay` 渲染就绪态徽章。
  - **无 Cargo 容错构建与发布打包对齐**：`RustCombatKernel.targets` 自动探测 `.cargo/bin`、锁定 `--target-dir`，非发布无 Cargo 构建发出告警并平滑退回纯 C#，发布构建（`PublicationBuild=true`）默认要求原生库；`build-local-stack.ps1` 支持 `-SkipRustKernel` / `-SkipCopyMod`，`publish-release.ps1` 创意工坊暂存同步 `rust-kernel/`。
- **本地主干能力整理**：
  - `STS2_AI_LIVE_MODE` 可选隐身自动驾驶 API（零 UI、纯内存配置、`Instant` 瞬间出牌速度；默认保守关闭 `UseRustCombatKernel`，支持显式会话启用）。
  - `OfflineSearchHarness` 新增单进程 `--eval-specs` 与持久化 NDJSON `--eval-stdio` 批测协议、v4 战斗事件遥测及三幕 `Boss1EncounterId`/`Boss2EncounterId` 注入与 `boss_hp_relief` 输出。

测试证据见[测试卷 37](../testing/volume-37.md)。
