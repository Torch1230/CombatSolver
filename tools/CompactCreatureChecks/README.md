# 生物值合同

`dotnet run --project tools/CompactCreatureChecks/CompactCreatureChecks.csproj -c Release`

直接链接真实值内核，不需要游戏。九个手写伤害边界包含小数、全挡／破防、穿透、过量伤害、零生命和数值上限；另测治疗与最大生命限幅。跨 64 槽页的两生物状态覆盖嵌套撤销、零生命仍在阵容、显式离场，以及八个工作区反复恢复冻结候选。

游戏差分入口是 `COMPACT-CREATURE-VALUES-NATIVE`，使用 `CORPSE_SLUGS_NORMAL`／SILENT，120 秒 headless 上限。其完整评估和原生伤害证据、适用范围见[阶段报告](../../docs/performance/simulation-creature-values-20260911.md)。这些合同不验证攻击命令、伤害来源历史或死亡 Hook 的紧凑执行。

新增攻击合同：非法目标在付款前拒绝，取消可撤销，暂停帧保留目标，零伤害保留命中标记，八工作区覆盖部分击杀、最后一击暂留出牌区、显式终局锁定与恢复。对应原生五步击杀及并行完整读取见[攻击阶段报告](../../docs/performance/simulation-compact-attacks-20260911.md)。仍不验证复杂死亡 Hook。

基础 Power 合同新增六个 decimal 修正向量：负力量／负敏捷、数值上限、保留原施加者、根槽退休、重新获得的顺序，以及跨页撤销和八工作区隔离。这里的 `ModifyBlock(0)` 只验证修正公式；完整命令门和原生实际出牌另由 `COMPACT-POWERS-NATIVE` 与 `COMPACT-POWER-BOUNDARIES-NATIVE` 验证。

`EffectProgramChecks` 通过合成有序指令链验证同一父牌的两次选择、嵌套 Sly 选择、返回父牌继续后续格挡／抽牌、空选择、取消撤销、输入数组隔离和未知指令拒绝。八工作区反复从同一暂停候选恢复，比较全部值与有序牌堆。该通用游标合同不等于原生卡牌组合准入；生存者与真实洗牌／自动牌证据见[指令阶段报告](../../docs/performance/simulation-effect-program-20260911.md)。

`CardLifecycleChecks` 在付款后冻结 X 牌，验证恢复时使用已捕获能量计算正负 Power 数量；能力牌离开战斗牌堆后，下一张零基础格挡仍读取当前敏捷。消耗位置、移除集合、X 值、根恢复和八工作区隔离一并比较。原生 12 分支与九步完整对账见[生命周期阶段报告](../../docs/performance/simulation-card-lifecycle-20260911.md)。

`ConditionalPowerChecks` 覆盖四种卡牌类别、空抽牌、洗牌检索与实际抽牌返回值、八工作区恢复，以及群体中毒／虚弱的指令与目标顺序、已离场目标和撤销。条件跳转超过程序末尾时拒绝；这些是纯值合同，原生十九牌准入和抽牌边界见[阶段报告](../../docs/performance/simulation-conditional-powers-20260911.md)。

`PoisonDiscardChecks` 直接验证无攻击者／卡牌来源的中毒伤害属性、穿透、递减、退休／重获和最终击杀；整手弃抽在部分抽牌的洗牌选择与后续 Sly 选择处冻结，八工作区核对原手牌列表、重新抽回的 Sly、后续指令、消耗与撤销。另测空手牌。完整原生与读取模型重获证据见[中毒／弃抽报告](../../docs/performance/simulation-poison-discard-20260911.md)。

`PowerExpressionChecks` 验证条件读取当前中毒状态、归零／重获、移除敌人后的求和、基础值／额外倍率与敏捷／脆弱取整，以及逐槽撤销和八工作区重算；未知目标域、越界跳转与无效倍率必须拒绝。原生组合和完整读取见[表达式报告](../../docs/performance/simulation-power-expressions-20260911.md)。

`DeferredPowerChecks` 覆盖格挡命令小数返回值与封顶净增量的区别、叠加、零返回、能力牌移除、施加者、撤销和八工作区恢复；根初始或后续敏捷增长可能产生正小数零层 Power 时拒绝整根。真实卡牌与全部估值见[下回合计数报告](../../docs/performance/simulation-compact-deferred-powers-20260911.md)。

`TemporaryStrengthChecks` 验证首次顺序、叠加封顶偏移、力量归零重获、施加者与获得顺序、消耗、敌人死亡清理、整段撤销和八工作区恢复。原生卡牌与完整估值见[临时力量迁移报告](../../docs/performance/simulation-compact-temporary-strength-20260911.md)。

`GeneratedCardChecks` 从十张满手牌生成 300 个实例，以完整列表比较手牌／弃牌的次序，验证模板绑定、完整编号事件、生成后攻击／消耗／洗牌、撤销与八工作区重新生成。原生状态与读取器模型池见[生成卡报告](../../docs/performance/simulation-generated-cards-20260911.md)。

`RandomCostChecks` 覆盖 130 次抽牌费用列表／付款、四种结果、满手停止、撤销与八工作区续接；原生证据见[随机费用报告](../../docs/performance/simulation-random-costs-20260911.md)。

`ArtifactChecks` 核对施加修改在临时力量内部效果之前生效，覆盖首次／叠加阻止、耗尽、零值、负属性、死亡、撤销和八工作区；[原生证据](../../docs/performance/simulation-compact-artifact-20260911.md)。

`HandEndChecks` 覆盖十二组生命／格挡／入场顺序、不可打出、虚无先消耗、玩家阵容保留、Power 清理、待失败／终局、撤销和八工作区；未准入阶段必须无写入地拒绝。真实动画模式和完整读视图见[手牌末尾报告](../../docs/performance/simulation-hand-end-20260911.md)。

确定性 AI 合同另覆盖非初始根、复制图、300 次增长日志、撤销和八工作区续接；原生 AI／意图由 `COMPACT-MONSTER-AI-NATIVE` 对照。

`NeurosurgeChecks` 覆盖能量封顶、抽牌前资源、选择后能力与人工制品、阵营开始一次性标记、玩家／敌方毁灭相位、终局前时钟／AI、撤销和八工作区恢复；[原生与搜索证据](../../docs/performance/simulation-necro-resources-20260911.md)。


关键字迁移同步修复两处合同接口漂移：能力表达式使用当前 `Multiplier` 名称，生成事件按位置／牌堆／创建者核对，模板从实例定义单独核对。整套值合同与[动态关键字原生测试](../../docs/performance/simulation-keywords-20260911.md)共同验证当前内核；原生测试另覆盖实例编码的定义编号／X 上限／两高位和生成牌标记。
