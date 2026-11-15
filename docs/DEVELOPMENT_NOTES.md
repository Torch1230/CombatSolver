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

- 无头启动器按 RitsuLib manifest 选择不高于游戏版本的最新兼容目标，修复 0.113.1 游戏配合 0.113.0 API 包时被路径检查拒绝的问题。Windows/Linux 同步维护，依赖模块仍由原加载器验证。
- 预测洗牌在单次排序内复用动态变量名的有序数组。0 个变量直接返回，1 个变量直接比较；费用和变量值仍逐次读取分支状态。
- 扩展 `NATIVE-CARD-ORDERING`，覆盖 0/1/2/3 个变量、后续排序、重复引用及父子分支隔离。
- 新增 8 张富足、42 张牌的固定输入。搜索分配约减少 8.1%；路线与工作量一致，原报告崩溃仍未复现。见[验证记录](performance/shuffle-variable-names-20261115.md)。
