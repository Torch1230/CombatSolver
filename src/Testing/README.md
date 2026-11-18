# Testing 源码

公共无人测试框架、原生回放、API worker 和离线宿主共用这里的实现；文件归类沿用同一程序集与 partial 类型，不改变状态所有权。

| 目录 | 内容 |
| --- | --- |
| Host/ | 请求协议、循环、建局、执行、断言、结果输出、选择器及离线入口 |
| Support/ | 共享 fixture、完整状态差分、球/药水/回合与冻结快照辅助 |
| Replay/ | 检查点导入、原生事件回放、录制状态与回放断言 |
| Contracts/Combat/ | 卡牌、Power、遗物、RNG、生命周期、Fork 与执行续接机制 |
| Contracts/Search/ | 政策、预算、保路、质量和搜索并发合同 |
| Contracts/Runtime/ | 根捕获、会话、部署所有权、内存与进程边界 |
| Contracts/UI/ | 本地化、路线行与显示身份合同 |
| Contracts/ThirdParty/ | 登记接口及第三方调用边界合同 |
| Regressions/Community/ | 社区问题的独立机制回归 |
| Regressions/Reports/ | 已固定报告的机制检查与保留的原生已知路线回归 |

`Contracts/Runtime/UnattendedTestRunner.NativeHealingCallbackBoundary.cs` 使用 `NATIVE-HEALING-CALLBACK-BOUNDARY` / REGENT / FUZZY_WURM_CRAWLER_WEAK / 120 秒：原生再生界 15、合法生成过滤、Heal 补丁与未审计 Ritsu 版本仅撤销有限回复证书、未知 OnPlay 补丁与晚注册的全局治疗监听器触发拒绝；16 并行 Fork 检查完整状态/历史/RNG、父/live 隔离和新根恢复。

`NATIVE-HEALING-CAPABILITY-BOUNDARY` 在测试宿主初始化阶段为该明确请求注册原生持久化槽，验证空宿主正例、未审计 Ritsu 版本仅撤销有限回复证书、当前/未来能力与默认注入以及未知保存来源拒绝、不执行未知回调，以及清理后 16 Fork 完整状态/历史/RNG。普通请求不激活该夹具；命令与实际范围见[模型能力报告](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/model-capability-recovery-proof-20261007.md)。

`Contracts/Combat/UnattendedTestRunner.FailureBoundaries.cs` 的 `VerifyPredictionFailureBoundaries` 继续覆盖订阅器的十种类型形状，并且增加四种实例的完成事件添加/移除、只读检查和派生同名字段拒绝。沿用 `--verify-prediction-failure-boundaries`（PowerShell：`-VerifyPredictionFailureBoundaries`）入口，不注册测试模型到真实战斗。原生默认回调和根准入的最小证据见[实例通知边界](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/inert-subscriber-call-boundary-20261008.md)。

`Contracts/Search/UnattendedTestRunner.PlayerPotionCallbackBound.cs` 使用 `PLAYER-POTION-CALLBACK-BOUND` / IRONCLAD / FUZZY_WURM_CRAWLER_WEAK / HP 40、MaxHP 80 / 120 秒：通过原生 `UsePotionAction` 执行公开移除事件的直接生命重设，验证未知来源退回无限界、正常原版界面回调可认证、16 个所属 Fork 完整状态/历史/RNG 和父/live 隔离；未知悬停事件、同步测试委托以及 Harmony 修改拒绝，不调用未知来源，清理后新根恢复。它不适配未知回调语义，也不证明所有界面通知闭包。

`Contracts/Search/UnattendedTestRunner.HealthCallbackBound.cs` 使用 `HEALTH-CALLBACK-BOUND` / FOOL / FUZZY_WURM_CRAWLER_WEAK / HP 40、MaxHP 80 / 120 秒：首先核对未改动活体 Fool 界面订阅放行，再验证正常受伤完整续用状态/RNG 严格对账；未知玩家/敌人的当前和最大生命事件、独立于生命连接的 Tracker 通知、Ritsu 详情刷新以及指定补丁均撤销有限回复资格，每组 16 所属 Fork 保留无限界、完整状态/历史/RNG 以及父隔离。原生受伤事件和移除生命连接后的能量通知各额外回复 2 点，均被拒绝认证，移除后新根恢复。它只补齐已确认来源缺口，不认证完整 UI 或未来注册闭包。

`Contracts/Search/UnattendedTestRunner.CardNotificationBound.cs` 使用 `CARD-NOTIFICATION-BOUND` / IRONCLAD / FUZZY_WURM_CRAWLER_WEAK / 120 秒以及根快照停止参数：首先核对未改动活体 Ironclad 界面订阅放行，随后验证原生 `BULLET_TIME` 触发 `StarCostChanged` 直接回复 2 点时撤销有限回复资格，8 事件乘以五牌堆/floating/永久牌组共 56 项拒绝、晚绑定/移除、新旧根隔离以及 16 Fork 完整状态/历史/RNG；不认证其余 UI 与未来来源。已撤回的生成闭包/查询惰性合同仅在[研究证据](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/card-notification-and-recovery-roi-20261009.md)归档。

`Contracts/Combat/UnattendedTestRunner.HpLossClamping.cs` 使用 `HP-LOSS-CLAMP` / IRONCLAD / FUZZY_WURM_CRAWLER_WEAK / 120 秒以及 `--verify-combat-root-snapshot --stop-after-combat-root-snapshot-assertion`，直接比较原生扣血的 168 组负数、小数、零、致死、封顶以及 decimal 极值和全部伤害字段/生命通知。16 孩子串行 Fork 后并行修改，检查完整状态、历史、九 RNG 与父/live 隔离；不执行完整伤害 Hook 管线。失败基线与最终证据见[回复闭包盘点](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/pruning-source-closure-and-hp-clamp-20261009.md)。

测试选择与平台命令见 [无人测试](../../docs/HEADLESS_TESTING.md)，当前最小哨兵见 [测试矩阵](../../docs/TEST_MATRIX.md)。长期测试有明确断言、最小入口或 fixture；同一机制优先扩展已有合同。

`Contracts/Combat/UnattendedTestRunner.MirroredHookFilter.cs` 与 `Contracts/Combat/UnattendedTestRunner.ProjectedHookReceivers.cs` 共用 `MIRRORED-HOOK-FILTER`（IRONCLAD / FUZZY_WURM_CRAWLER_WEAK / `--verify-combat-root-snapshot --stop-after-combat-root-snapshot-assertion`）：覆盖小牌组镜像投影与完整监听表对账、64 位参与位图、`SimCardPile` 非空索引与空索引双卡复验、`RequireRemap` 16 兄弟及第二代 Fork、不透明/别名包裹回退，以及相关 getter 补丁回退与 `LiveCombatStamp` 失效断言。

`Contracts/Combat/UnattendedTestRunner.OrderedHandDuplicates.cs` 使用 `ORDERED-HAND-DUPLICATES-FIRST` / `SECOND`，核对两处相同打击留下的有序手牌、坚毅随机消耗及完整原生状态。相邻重复与不可打出的间隔牌同时覆盖普通候选、开局能力牌及循环替代动作，复跑命令见测试矩阵。
`Contracts/Combat/UnattendedTestRunner.HandDrawRelicQuery.cs` 通过检查点 `RestoreOnly` 与场景名 `HAND-DRAW-RELICS-PROBE` 复用原生回放建局，逐一核对8种抽牌遗物在回合1～4及已有计数下的原生命令、冻结查询、Fork/RNG/父分支和live隔离；它只验证查询，完整生命周期沿用对应遗物合同。

`Contracts/Search/UnattendedTestRunner.PotionCostIncumbent.cs` 使用 `POTION-COST-INCUMBENT` / IRONCLAD / NIBBITS_WEAK / 120秒，验证实际完整14/9成本胜利下仅按分支已付成本剪枝（未付层成本下界保持为 `0`）、`BossTempo` 等 HP 与晚回合保留、以及原生 EndTurn／用药完整状态。`UnattendedTestRunner.ZeroCreditGrowthDominance.cs` 与 `UnattendedTestRunner.RetainedPrimaryDominance.cs` 共用 `ZERO-CREDIT-GROWTH-PROOF`（以及 `-REGEN` 变体，NECROBINDER / NIBBITS_WEAK / 120秒），验证扩展初始来源证明下跨成长桶严格战损否证、已保留完整胜利在追加计划中的严格支配消费、相等战损保留、已付成本下界、以及完整增量与 live 隔离。`ZeroAllowanceRelicIncumbent.cs` 使用 `ZERO-ALLOWANCE-RELIC-INCUMBENT` / 同角色遭遇及上限，验证真实完整胜利在零战损让步遗物目标下的本地及协调器后续HP界，保留同HP、正额度、成长和追回，原生HP消耗前缀严格差分。

一次性调查放 .local/tool-tasks/<任务>/，验证时显式接入，结束清理代码、路由、参数、输入和产物。普通构建排除 .local 源码。新增正式文件按上表收纳；根目录只保留本入口。

`Contracts/Search/UnattendedTestRunner.NoveltySearch.cs` 的 `GENERATED-NOVELTY-SEARCH` 支持完整请求对照及固定胜路验证：`research-options.json` 选择 `request` 或 `replay`；后者读取 `frozen-plan.json` 并要求零展开、完整终局一致，可配合 `native.flag` 验证原生部署。复跑参数和范围见[首领策略证据](../../docs/strategy/hp-loss-pruning/README.md#原生兑现与独立包复测)。

旧 Soul/Custom/外骨骼虫路径追踪及 ACT3 硬编码路线观察入口已退出当前树，调查代码见 [固定提交](https://github.com/Torch1230/CombatSolver/tree/fe3edd2f7b4f3a92b266e6b13293810d31ce2e1b/src/Testing)。原生已知路线回归、生成上下文合同及其公共快照辅助继续维护。历史质量缺口与失败记录保持原结论，源码精简不代表问题修复。
