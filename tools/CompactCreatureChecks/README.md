# 生物值合同

`dotnet run --project tools/CompactCreatureChecks/CompactCreatureChecks.csproj -c Release`

直接链接真实值内核，不需要游戏。九个手写伤害边界包含小数、全挡／破防、穿透、过量伤害、零生命和数值上限；另测治疗与最大生命限幅。跨 64 槽页的两生物状态覆盖嵌套撤销、零生命仍在阵容、显式离场，以及八个工作区反复恢复冻结候选。

游戏差分入口是 `COMPACT-CREATURE-VALUES-NATIVE`，使用 `CORPSE_SLUGS_NORMAL`／SILENT，120 秒 headless 上限。其完整评估和原生伤害证据、适用范围见[阶段报告](../../docs/performance/simulation-creature-values-20260911.md)。这些合同不验证攻击命令、伤害来源历史或死亡 Hook 的紧凑执行。

新增攻击合同：非法目标在付款前拒绝，取消可撤销，暂停帧保留目标，零伤害保留命中标记，八工作区覆盖部分击杀、最后一击暂留出牌区、显式终局锁定与恢复。对应原生五步击杀及并行完整读取见[攻击阶段报告](../../docs/performance/simulation-compact-attacks-20260911.md)。仍不验证复杂死亡 Hook。

基础 Power 合同新增六个 decimal 修正向量：负力量／负敏捷、数值上限、保留原施加者、根槽退休、重新获得的顺序，以及跨页撤销和八工作区隔离。这里的 `ModifyBlock(0)` 只验证修正公式；完整命令门和原生实际出牌另由 `COMPACT-POWERS-NATIVE` 与 `COMPACT-POWER-BOUNDARIES-NATIVE` 验证。

`EffectProgramChecks` 通过合成有序指令链验证同一父牌的两次选择、嵌套 Sly 选择、返回父牌继续后续格挡／抽牌、空选择、取消撤销、输入数组隔离和未知指令拒绝。八工作区反复从同一暂停候选恢复，比较全部值与有序牌堆。该通用游标合同不等于原生卡牌组合准入；生存者与真实洗牌／自动牌证据见[指令阶段报告](../../docs/performance/simulation-effect-program-20260911.md)。

`CardLifecycleChecks` 在付款后冻结 X 牌，验证恢复时使用已捕获能量计算正负 Power 数量；能力牌离开战斗牌堆后，下一张零基础格挡仍读取当前敏捷。消耗位置、移除集合、X 值、根恢复和八工作区隔离一并比较。原生 12 分支与九步完整对账见[生命周期阶段报告](../../docs/performance/simulation-card-lifecycle-20260911.md)。
