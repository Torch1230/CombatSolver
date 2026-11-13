# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

0.51.0 的标准模式适配、请求预算显示和路线优化见[历史卷 23](archive/development/volume-23.md)，玩家说明见[0.51.0 更新日志](releases/0.51.0-RELEASE_NOTES.md)。

0.51.1 的恐惧／极速模式适配与实例状态修复见[历史卷 24](archive/development/volume-24.md)，玩家说明见[0.51.1 更新日志](releases/0.51.1-RELEASE_NOTES.md)。

0.51.2 的属性削减、诅咒持续时间和仪式兽修复见[历史卷 25](archive/development/volume-25.md)，玩家说明见[0.51.2 更新日志](releases/0.51.2-RELEASE_NOTES.md)。

## 0.51.3：同牌多减益与部署异常

来源：`combatsolver-reports-20261113-084409.zip` 的 8 份 0.51.1 报告。五份疑虑／羞耻持续时间偏差（`06a09968`、`08bb0aad`、`3d80f49f`、`c7d8fe4f`、`ead5975e`）及一份恐惧族母属性削减（`24b97970`）复用 0.51.2 的修复。

- `1a585d8b`：帝皇蟹战斗，不安油灯与升级 `HOBBLING_SHOT` 组合，第 2 回合易伤预测 1、原生 3。`ApplyPowerFromSource` 在外层出牌内建立同来源的嵌套作用域，退出首个减益时提前完成遗物。现在先弹出当前作用域，存在同一卡牌外层作用域时保持触发状态，最外层完成时消费；继续复用既有遗物状态所有权。
- `f2d069a5`：`THE_MOON+ → SHIV+ → VITRIOL+` 的原生飞刀只有 Started 历史，缺少伤害、Finished 和消耗事件，停留在 Play 牌堆；后续牌仍执行，回合末复核为 9 伤而计划为 0。原生 `GameAction.CompletionTask` 在执行失败时也成功完成，部署现在随后读取 `Exception`，保留原异常和动作上下文，并暂停自动搜索／执行、清空续用来源。原始飞刀异常栈在包中缺失，导致其执行中断的具体原因仍待定位。
- 灯笼多减益及相邻间接减益原生严格差分通过；月亮／凡庸／飞刀最小组合也通过，原报告的飞刀中断未复现。Runtime 小改动采用源码检查与 Release 编译，原生故障注入及玩家整场环境未验证。详细 runId 与复跑入口见[测试矩阵](TEST_MATRIX.md#0513-同牌多减益与部署异常)。

本批生成本地 0.51.3 包。玩家日志合并 0.51.2 的三项修复；0.51.2 工坊上传超时，外部渠道状态保留原发布记录。
