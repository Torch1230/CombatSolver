# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

0.51.0 的标准模式适配、请求预算显示和路线优化见[历史卷 23](archive/development/volume-23.md)，玩家说明见[0.51.0 更新日志](releases/0.51.0-RELEASE_NOTES.md)。

0.51.1 的恐惧／极速模式适配与实例状态修复见[历史卷 24](archive/development/volume-24.md)，玩家说明见[0.51.1 更新日志](releases/0.51.1-RELEASE_NOTES.md)。

0.51.2 的属性削减、诅咒持续时间和仪式兽修复见[历史卷 25](archive/development/volume-25.md)，玩家说明见[0.51.2 更新日志](releases/0.51.2-RELEASE_NOTES.md)。

0.51.3 的同牌多减益与部署异常修复见[历史卷 26](archive/development/volume-26.md)，玩家说明见[0.51.3 更新日志](releases/0.51.3-RELEASE_NOTES.md)。

## 0.51.4：战斗逻辑与选牌续接

25 份 0.51.3 报告按范围归并，9 份对应六类修复：大树将倾击杀群伤、旧角色首次获得箭槽、洪流巨人死亡转阶段时点、横祸空池 RNG、安可活动卡 Fork、可选双药补搜失败。选牌严格历史检查同时补齐 DamageResult 新字段复制。

5 项定向原生合同通过；机甲骑士的多烧伤／百年积木牌序问题仍待可见时序验证。两份预算耗尽续算保持原行为；世界线与第三方内容按本批范围跳过。首差异、实现职责、失败及未验证项见[问题记录](issues/reports-20261113-logic.md)，复跑入口见[测试矩阵](TEST_MATRIX.md#0514-战斗逻辑与选牌续接)。
