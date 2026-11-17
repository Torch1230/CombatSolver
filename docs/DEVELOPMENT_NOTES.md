# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

0.51.0 的标准模式适配、请求预算显示和路线优化见[历史卷 23](archive/development/volume-23.md)，玩家说明见[0.51.0 更新日志](releases/0.51.0-RELEASE_NOTES.md)。

0.51.1 的恐惧／极速模式适配与实例状态修复见[历史卷 24](archive/development/volume-24.md)，玩家说明见[0.51.1 更新日志](releases/0.51.1-RELEASE_NOTES.md)。

0.51.2 的属性削减、诅咒持续时间和仪式兽修复见[历史卷 25](archive/development/volume-25.md)，玩家说明见[0.51.2 更新日志](releases/0.51.2-RELEASE_NOTES.md)。

0.51.3 的同牌多减益与部署异常修复见[历史卷 26](archive/development/volume-26.md)，玩家说明见[0.51.3 更新日志](releases/0.51.3-RELEASE_NOTES.md)。

0.51.4 的战斗逻辑与选牌续接修复见[历史卷 27](archive/development/volume-27.md)，玩家说明见[0.51.4 更新日志](releases/0.51.4-RELEASE_NOTES.md)。

0.51.5 的结算顺序与运行时交接修复见[历史卷 28](archive/development/volume-28.md)，玩家说明见[0.51.5 更新日志](releases/0.51.5-RELEASE_NOTES.md)。

## 下一版本（开发中）

- `SimCreatureState.LoseHp` 将负伤害截断为零，惰性订阅器实例准入额外检查 `AbstractModel.ExecutionFinished`，同时新增 `Player.UsedPotionRemoved`、生命/界面事件、8 项 `CardModel` 通知、`RitsuLib` 治疗监听器以及模型能力宿主的根捕获审计；未审计 `STS2`/`RitsuLib` MVID 只撤销有限回复剪枝证书，不阻断游戏流程，而 `OnPlay` 根审计纳入 `FoolCardPool` 原生战斗生成超集。`tools/testing/run-unattended-test.sh` 将 `source "$script_dir/headless-runtime.sh"` 前移到 `headless_ritsu_compatibility_target` 调用之前。
- 无头启动器按 RitsuLib manifest 选择不高于游戏版本的最新兼容目标，修复 0.113.1 游戏配合 0.113.0 API 包时被路径检查拒绝的问题。Windows/Linux 同步维护，依赖模块仍由原加载器验证。
