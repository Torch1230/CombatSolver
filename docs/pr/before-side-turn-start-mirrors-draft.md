# feat: support third-party BeforeSideTurnStart mirrors

第三方模型目前无法登记“回合开始前获得能力”或“重置本回合首次触发次数”等效果。增加 `BeforeSideTurnStartMirrors.Register<TModel>(Action<TModel, BeforeSideTurnStartMirrorContext>)`，接收任意具体 `AbstractModel`，包括 Power、遗物和 Modifier；上下文提供 Side、Participants 及分支状态。

有第三方监听者时，按原生 `Hook.BeforeSideTurnStart` 的监听表顺序逐个派发，在清格挡前执行；成员快照、卡牌 COW 与选择暂停沿用现有镜像约定。原版登记复用既有单项结算体；零第三方监听者保留原遗物整批→Power 整批路径。顺序依据为游戏 0.111.0 反编译，见 [定位记录](../../coverage/equivalence/before-side-turn-start-0433/native-order.json)。

登记沿用 `MethodMirrorRegistry`：精确类型，拒绝空委托、重复、抽象类型及未覆写类型，首根或首次派发冻结。未知有效覆写记录 Unsupported 并中止，沿用晚期回合末表的约定。变基到 `6922828d` 后分配独立监听位 57，避免与上游 AfterEnergyReset 的位 56 冲突。

验证：

- 对照 `6922828d`，EQ 10 / FULL 40 / GA 10，一次完成 60 对：**6341 个确定性字段一致**（1069 / 4212 / 1060），无失败、缺项或时间截断。High 90 / nodes 250000 / 分支 48/28/36 / Coordinator / Smart / DOP 1；两侧各一个离线宿主，600 秒软预算。输入、逐根结果摘要、DLL 哈希与复算命令见 [等价证据](../../coverage/equivalence/before-side-turn-start-0433/README.md)。
- `TurnPhaseMirrorChecks --start` 22 项、`--start --seal` 1 项、原晚期 25 项通过；`--mask <生产DLL>` 确认 58 个单 bit 互不重叠。Release 与离线宿主构建均 0 警告 / 0 错误，结构门禁 `search_files=208`。
- CoverageCatalog 原始上游表含两个工具尚不识别的 LOOP 状态，原样解析失败。仅在 archive 副本排除两个未被 classifications 引用的记录后，3035 条核验通过；原表未修改。明细见 [检查记录](../../coverage/equivalence/before-side-turn-start-0433/validation.json)。
