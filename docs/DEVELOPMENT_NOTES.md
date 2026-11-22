# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

## 未发布：战前同进程 Sts2Sim 桥接与恐惧模式回退

- `PreCombatForecastApi`（`ApiVersion = 7`）在标准模式且满足强制宿主排除、全量 Mod 与 Harmony 审计及存档资格时，通过延迟加载的 `IsolatedBridgeLoadContext` 与校验清单 `sts2sim/sts2sim-bundle.manifest.json` 调用同进程 `Sts2Sim.PreCombatBridge` 完成战前预测；规划请求独立评估规划存档内容，同时继承并先决执行宿主强制排除。
- 恐惧模式（`GameMode.Dread`）及其他不兼容宿主条件固定通过 `dread_mode` 等编码在探测或激活 `sts2sim/` 前直接回退至既有普通求解器路径（Windows 独立子进程，Linux 按既有平台支持口径返回不支持）。
- `PreCombatForecastWorker` 通过统一单 `Gate` 调度器、`admissionEpoch` / `_stopEpoch` 截止栅栏、请求中断与严格期限分离，以及由生产者持有的完成票据管理同进程会话、遗留回退、互斥锁、重启状态与空闲释放；在交接层完成药水标题本地化与默认 `UseBeamWidthPortfolio`（含通用 `CardType.Power` 能力承诺成员）路由。

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
