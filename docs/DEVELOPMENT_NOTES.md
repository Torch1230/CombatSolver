# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

0.51.0 的标准模式适配、请求预算显示和路线优化见[历史卷 23](archive/development/volume-23.md)，玩家说明见[0.51.0 更新日志](releases/0.51.0-RELEASE_NOTES.md)。

0.51.1 的恐惧／极速模式适配与实例状态修复见[历史卷 24](archive/development/volume-24.md)，玩家说明见[0.51.1 更新日志](releases/0.51.1-RELEASE_NOTES.md)。

0.51.2 的属性削减、诅咒持续时间和仪式兽修复见[历史卷 25](archive/development/volume-25.md)，玩家说明见[0.51.2 更新日志](releases/0.51.2-RELEASE_NOTES.md)。

0.51.3 的同牌多减益与部署异常修复见[历史卷 26](archive/development/volume-26.md)，玩家说明见[0.51.3 更新日志](releases/0.51.3-RELEASE_NOTES.md)。

0.51.4 的战斗逻辑与选牌续接修复见[历史卷 27](archive/development/volume-27.md)，玩家说明见[0.51.4 更新日志](releases/0.51.4-RELEASE_NOTES.md)。

## 0.51.5（待发布）

107 份玩家报告按首因归并，明细与待定位项见[问题记录](issues/reports-20261114-logic.md)。

- Runtime：便携 GC 以阻塞调用返回确认完成；Windows 系统清理按钮按平台显示。原生回合准备先退出自动出牌阶段，后续续用交给已有战斗延迟操作管理，保留取消、排空和失败登记；手牌计划接管先退出窥视。
- Prediction／Engine：狂喜复用批量变牌，准备时间与潜伏根系按 Power 原序执行；敌方再生／覆甲先于灾厄，普通回合末效果继续在其后；狠揍使用分支动态目标类型。
- Search：能力探测通过既有固定前缀种子入口处理提前胜利，终局后返回空后续动作。
- 验证：便携 GC 6 项合同通过；最小原生差分及第二回合单步接管通过，覆盖完整状态、RNG 和零计划外重算。UI 的可见平台表现、原始玩家环境、并行数组越界与另两条未定位报告保持待验证。详见[测试矩阵](TEST_MATRIX.md#0515-结算顺序与运行时交接)。
