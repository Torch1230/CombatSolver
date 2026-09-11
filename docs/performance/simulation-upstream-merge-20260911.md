# 合并上游 0.36.0 与紧凑后端集成（2026-09-11）

按用户要求，拉取并合并 `Torch1230/CombatSolver` 的 `main`，目标 `c2cc463`。合并前本地为 `b34d09a`，已完成卡牌闭包为 52 种。Pagestorm 进行中改动先完整暂存，待本次合并提交后恢复；它不属于以下通过结果。

## 整合内容

- 保留上游 0.34.7–0.36.0 的搜索单一预算、成长／偷窃目标、无胜利恢复、GC／进程诊断、克隆隔离、第三方登记和界面更改。测试夹具迁移至 `Profile`／`FixedBudget`，不恢复旧短搜／深搜字段。
- 保留本分支已验证的独立能力施加、临时力量封顶偏移、根历史窗口和玩家／宠物死亡顺序；上游临时敏捷／集中在加入计数前应用属性的行为接入同一准备／提交入口。
- 合并上游回合结束顺序，保留紧凑手牌效果的显式 staging 参数。紧凑执行以纯值阶段事件记录 End／None／Start／Play；物化和每 lane 读视图消费相同事件，逆序读取先恢复根阶段，不依赖 live 玩家状态。
- 新增攻击／技能开始计数沿根捕获、Fork、双方回合窗口、读视图、完整指纹及续用文本传递；保留 BrightestFlame 根累计值。
- 估值沿用上游消耗抽牌时序，战略摘要缓存增加 `skillsExhaust` 条件；无序牌堆指纹保留上游模型缓存，紧凑读视图直接遍历权威牌堆。缓存失效时先清零累加器。
- 两端结构门禁同步；CoverageCatalog 由合并程序集重新生成，测试证据按键保留双方记录。上游玩家死亡独立检查工具接入实际效果接口及死亡完成方法，继续验证能力清理在宠物死亡之前。

## 本轮直接证据

| 检查 | 结果 |
|---|---|
| Release v3 | Passed，零警告／错误 |
| CompactCreatureChecks | Passed，包含冻结／撤销、跨回合、能力及独立实例合同 |
| Linux 结构门禁 | Passed，94 个 Search 文件；PowerShell 规则同步但未执行 |
| CoverageCatalog `--verify` | Passed，生成当前 0.36.0 目录；选择／自动牌／阵容未解决数均为 0 |
| COMPACT-SHARED-FATE-NATIVE | `1afd0d4535574cbc862c4081f6a96206` Passed；三根、21 原生动作／42 分支／18 挂起，完整状态／能力／历史／九 RNG／原键／续用／八工作区 |
| COMPACT-SHARED-FATE-SEARCH | `a017c6546bd241d6b07115b2f6ddf482` Passed；250 节点、旧／新 DOP1 与紧凑 DOP2 完整结果一致，取消／失败排空、根复用 |
| COMPACT-PANACHE-PLAYER-DEATH | `af98ce1a1a3243d884cc2b519b570e9b` Passed；原生清理前完整状态、玩家及宠物能力退休、敌方阶段安全点 |
| PANACHE-INSTANCES | `746fca9b886648cfb7b9f86cce05e56f` Passed；八牌、独立数量／计数、逐步 Fork、实机推进后冻结根 |
| CardHookReceiverChecks | Passed，78 项 |
| PlayerDeathChecks | Passed，48 项；首次失败仅为测试 stub 缺少现有效果接口，已修正工具 |

证据位于本机 `.local/upstream-merge-20260911/`；构建、纯值、门禁和覆盖日志为 `.local/upstream-merge-*.log`。Release v1 仅因旧测试预算字段编译失败；v2 编译通过，v3 修正了无序指纹失效后的累加器，全部原生及搜索请求使用 v3 产物。

以上为合并集成验证，未重新跑原始两场正常 NoGC 整场性能验收，未做可见 Steam、发布、安装或远端推送。历史 0.34.6 耗时和计数仍是历史证据；上游已改变搜索政策，下一轮性能 A/B 必须在本次合并后的同一政策下比较，不能直接拿旧数字作提速结论。
