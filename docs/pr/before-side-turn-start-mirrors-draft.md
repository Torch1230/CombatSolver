# feat: support third-party BeforeSideTurnStart mirrors

新增 `BeforeSideTurnStartMirrors.Register<TModel>`。第三方“每回合恢复格挡”“重置本回合首次触发次数”等效果现在可以登记在原生回合开始前时点，接收 Power、遗物和 Modifier，并获得当前 Side 与参与者。

有第三方监听者时，沿原生 Hook 的监听者顺序派发。原版类型共用现有单项结算体；无第三方监听者时保留原有遗物/Power 批次路径，避免改变历史搜索状态。未知有效覆写沿用晚期回合末的 Unsupported 记录与中止约定。登记按精确类型，拒绝空委托、重复、抽象类型和未覆写类型，首根捕获或首次派发后冻结。

验证：

- `TurnPhaseMirrorChecks --start` 22 项、`--start --seal` 1 项，原晚期回合末 25 项通过。
- Release 构建和结构门禁通过；覆盖目录 3035 条核验通过。
- 当前 main `8be1410` 对本改动，10 根（5 角色 × 精英/首领），High / Coordinator / Smart / DOP 1：992 个确定性字段完全一致。输入、输出及复算命令在 `coverage/equivalence/before-side-turn-start/`。
