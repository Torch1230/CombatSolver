# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

0.51.0 的标准模式适配、请求预算显示和路线优化见[历史卷 23](archive/development/volume-23.md)，玩家说明见[0.51.0 更新日志](releases/0.51.0-RELEASE_NOTES.md)。

## 下一版本（开发中）

0.113 适配 Part 2：计划适配恐惧和极速模式。

### 恐惧与极速模式（实施中）

三个模式共用搜索与部署。`CombatRulesSnapshot` 冻结模式、原生进阶集合和领域身份，进入状态键、续用、缓存及问题包；领域克隆解除跑局引用。恐惧／极速 RunModel 保留原生监听顺序。新增两个 Hook registry：`BeforeHandDraw` 复用 Power、遗物、返回手牌和选择续执行体；`AfterCreatureAddedToCombat` 汇合遗物、Modifier 与领域的入场效果。

| 原版差异 | 适配位置与复用方式 | 验证入口 |
| --- | --- | --- |
| 12 种 Dominion 的战前施加、抽牌及增援效果 | 原生战前建局、根捕获、两个共享 Hook registry | DREAD-DOMINION-HOOKS |
| Aeonglass／Insatiable／LagavulinMatriarch 领域 Power | 原有抽牌、回合阶段和晚期伤害结算 | DREAD-DOMINION-POWERS |
| 女王、沙漏、灵魂鱼、知识恶魔的模式差异 | 原有怪物动作与诅咒选择，读取冻结规则 | 怪物差分待完成 |
| 其余恐惧怪物数值与生成怪物 | 原有静态行动快照；同步原生构建通过 NativeCombatRulesScope 读取根规则 | MODE-RULES-ISOLATION；增援验证待完成 |
| 极速生成、变牌与偷窃 | 原有生成池缓存、统一资格过滤和地精佣兵动作 | 生成／变牌合同已通过；偷窃待验证 |
| 极速战后药水 | 原有 PotionRewardOutlook，复制奖励流推演前置卡牌奖励及后续药水 | 三幕普通、弱怪、精英和首领各 8 个样本通过；遗物组合待验证 |

根捕获、领域组合与 Hook 分批原生证据见[测试矩阵](TEST_MATRIX.md#0113-part-2恐惧与极速)。十二种领域与知识恶魔两个选择完成差分。变牌旧合同的超时来自等待初始求解结果的退出参数；纯合同出口已修正。女王恐惧开场与强化完成原生对照；沙漏差分发现随机插入缺少洗牌流消耗，按原生位置参数修复后继续验证。完整集成、恢复及覆盖目录收口进行中。
