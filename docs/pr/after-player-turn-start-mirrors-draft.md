# feat: 开放玩家回合开始三阶段镜像

基于 **#126**（`523aea57`，0.44.0 + BeforeSideTurnStart）。第三方遗物的回合治疗、Modifier 的回合计数清零目前无法登记到玩家回合开始阶段；新增 `AfterPlayerTurnStartMirrors`，让这些效果进入预测分支。

## 登记与派发

- `RegisterEarly<TModel>`、`Register<TModel>`、`RegisterLate<TModel>` 分别对应原生三个时点，委托为 `Action<TModel, AfterPlayerTurnStartMirrorContext>`，`TModel : AbstractModel`。上下文提供模拟器与当前 Player。
- 沿用 MethodMirrorRegistry：精确类型、空委托/抽象/未覆写/重复登记拒绝、首根冻结、覆盖元数据。未知有效覆写沿晚期表约定记录风险并中止预测。
- 游戏 0.111.0 的 `Hook.AfterPlayerTurnStart` 按 Early → 普通 → Late 三轮执行，每轮重新取监听者。扩展路径保留这个顺序；不把相邻时点合并，以免改变生成/移除效果与抽牌的先后关系。
- 原版普通 20 项、Late 2 项复用现有单项结算体。零第三方有效覆写时直接走原批次和选择续执行路径；存在扩展时按监听顺序调用，选择暂停返回稳定父节点完整重放。

## 验证

- `TurnPhaseMirrorChecks --after-player-start`：40 项；追加 `--seal`：3 项；生产 DLL `--mask`：61 个独立位。覆盖三阶段顺序、Power/遗物/Modifier、轮间新监听者、卡牌 COW、选择暂停、未知覆写与冻结。
- Release 和离线宿主构建 0 警告 / 0 错误；Bash 结构门禁 208 个 Search 文件通过。
- 零第三方监听者的 EQ 10 + FULL 40 + GA 10：**60 对、6341 个确定性字段一致**，全部有效、无时间截断。口径 High 90 / nodes 250000 / 分支 48/28/36 / Coordinator / Smart / DOP 1；一次批次，无补跑。逐根数据见 [等价证据](../../coverage/equivalence/after-player-turn-start/README.md)。
- CoverageCatalog 原始证据表有两个上游未知枚举状态，解析失败；仅在隔离副本排除这两条未引用记录后，3035 条 verify 通过。原表未改，具体记录见 [验证摘要](../../coverage/equivalence/after-player-turn-start/validation.json)。
