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

0.51.6 的社区 PR 整合与日志上传修复见[历史卷 29](archive/development/volume-29.md)，玩家说明见[0.51.6 更新日志](releases/0.51.6-RELEASE_NOTES.md)。

0.51.7 的薄荷尖塔兼容与诊断取证修复见[历史卷 30](archive/development/volume-30.md)，玩家说明见[0.51.7 更新日志](releases/0.51.7-RELEASE_NOTES.md)。

0.51.8 的治疗补丁启动修复见[历史卷 31](archive/development/volume-31.md)，玩家说明见[0.51.8 更新日志](releases/0.51.8-RELEASE_NOTES.md)。

## 下一版本（开发中）

- 愚者 18 张单人能力牌接入能力牌路线保护（[#257](https://github.com/Torch1230/CombatSolver/issues/257)，父议题 #255）。新增 `PowerCardPool.Fool` 与 `src/Search/PowerCardValuation/Cards/Fool/`，逐卡机制族、优先级、触发证据与开局投影按玩家逐卡说明登记，见[愚者卡池](strategy/power-card-valuation/fool.md)。射箭牌按 `CardTag.Shoot`、旋律牌按哇/啾/叽标签、悲伤按卡牌类型识别，均读取分支牌区。估值只影响中途保路与开局能力专搜，不改终局排序；MultiplayerOnly 的 `COMPOST`、`COVERING_FIRE` 不登记。结构门禁的 Search 卡牌 ID 字面量上限随新卡池登记由 645 调为 711。
- 倦怠收紧：悲伤不能打出且保留，逐回合累积占手牌上限，长线战斗会把手牌塞死。倦怠只在预计五回合内结束（或狂喜已生效）时保护，投影改为逐回合净抽牌（多抽减去被挤掉的正常抽牌），净值不为正不保护，优先级 Strong → Normal，去掉触发保底。
- 尚未覆盖“复制药水 → 能力牌”开局前缀：`0e6b13b6` 人工 7 + 1 依赖该路线，当前仍为 26 + 0。
- 检查点路径追踪新增测试专用参数 `RecordedPlanOverridePathForTest`（Linux：`--recorded-plan-override-path-for-test`），可传入拼接后的目标路线替代索引中的录制预测。
