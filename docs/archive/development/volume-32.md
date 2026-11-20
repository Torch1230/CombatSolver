# 开发历史卷 32

## 0.51.9 愚者能力牌路线优化

- 万事无忧按分支牌区里五种单人来源牌的悲伤生成量和已生效倦怠的每回合生成量估算格挡。

- 愚者 18 张单人能力牌接入能力牌路线保护（[#257](https://github.com/Torch1230/CombatSolver/issues/257)，父议题 #255）。新增 `PowerCardPool.Fool` 与 `src/Search/PowerCardValuation/Cards/Fool/`，逐卡机制族、优先级、触发证据与开局投影按玩家逐卡说明登记，见[愚者卡池](../../strategy/power-card-valuation/fool.md)。射箭牌按 `CardTag.Shoot`、旋律牌按哇/啾/叽标签、悲伤按卡牌类型识别，均读取分支牌区。估值只影响中途保路与开局能力专搜，不改终局排序；MultiplayerOnly 的 `COMPOST`、`COVERING_FIRE` 不登记。结构门禁的 Search 卡牌 ID 字面量上限随新卡池登记由 645 调为 711。
- 倦怠收紧：悲伤不能打出且保留，逐回合累积占手牌上限，长线战斗会把手牌塞死。倦怠只在预计五回合内结束（或狂喜已生效）时保护，投影改为逐回合净抽牌（多抽减去被挤掉的正常抽牌），净值不为正不保护，优先级 Strong → Normal，去掉触发保底。
- 尚未覆盖“复制药水 → 能力牌”开局前缀：`0e6b13b6` 人工 7 + 1 依赖该路线，当前仍为 26 + 0。
- 检查点路径追踪新增测试专用参数 `RecordedPlanOverridePathForTest`（Linux：`--recorded-plan-override-path-for-test`），可传入拼接后的目标路线替代索引中的录制预测。

测试证据见[测试卷 32](../testing/volume-32.md)。
