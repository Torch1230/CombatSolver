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

`Contracts/Runtime/UnattendedTestRunner.NativeHealingCallbackBoundary.cs` 使用 `NATIVE-HEALING-CALLBACK-BOUNDARY` / REGENT / NIBBITS_WEAK / 120秒：原生再生界15、合法生成过滤、未知OnPlay/Heal补丁、全局治疗监听器及晚注册live拒绝；16并行Fork检查完整状态/历史/RNG、父/live隔离、未知回调不执行和新根恢复。

`NATIVE-HEALING-CAPABILITY-BOUNDARY`在测试宿主初始化阶段为该明确请求注册原生持久化槽，验证空宿主正例、当前/未来能力与默认注入和未知保存来源拒绝、不执行未知回调，以及清理后16Fork完整状态/历史/RNG。普通请求不激活该夹具；命令与实际范围见[模型能力报告](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/model-capability-recovery-proof-20261007.md)。

`Contracts/Combat/UnattendedTestRunner.FailureBoundaries.cs` 的 `VerifyPredictionFailureBoundaries` 继续覆盖订阅器的十种类型形状，并增加四种实例的完成事件添加/移除、只读检查和派生同名字段拒绝。沿用 `--verify-prediction-failure-boundaries`（PowerShell：`-VerifyPredictionFailureBoundaries`）入口，不注册测试模型到真实战斗。原生默认回调和根准入的最小证据见[实例通知边界](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/inert-subscriber-call-boundary-20261008.md)。

`Contracts/Search/UnattendedTestRunner.PlayerPotionCallbackBound.cs` 使用 `PLAYER-POTION-CALLBACK-BOUND` / IRONCLAD / NIBBITS_WEAK / HP40、MaxHP80 / 120秒：通过原生 `UsePotionAction` 执行公开移除事件的直接生命重设，验证未知来源退回无限界、正常原版界面回调可认证、16个所属Fork完整状态/历史/RNG和父/live隔离；未知悬停事件、同步测试委托及Harmony修改拒绝，不调用未知来源，清理后新根恢复。它不适配未知回调语义，也不证明所有界面通知闭包。

`Contracts/Search/UnattendedTestRunner.HealthCallbackBound.cs` 使用 `HEALTH-CALLBACK-BOUND` / IRONCLAD / NIBBITS_WEAK / HP40、MaxHP80 / 120秒：正常受伤完整续用状态/RNG严格对账；未知玩家/敌人的当前和最大生命事件、独立于生命连接的Tracker通知、Ritsu详情刷新及指定补丁均撤销有限回复资格，每组16所属Fork保留无限界、完整状态/历史/RNG及父隔离。原生受伤事件和移除生命连接后的能量通知各额外回复2点，均被拒绝认证，移除后新根恢复。它只补已确认来源缺口，不认证完整UI或未来注册闭包。

测试选择与平台命令见 [无人测试](../../docs/HEADLESS_TESTING.md)，当前最小哨兵见 [测试矩阵](../../docs/TEST_MATRIX.md)。长期测试有明确断言、最小入口或 fixture；同一机制优先扩展已有合同。

`Contracts/Combat/UnattendedTestRunner.HandDrawRelicQuery.cs` 通过检查点 `RestoreOnly` 与场景名 `HAND-DRAW-RELICS-PROBE` 复用原生回放建局，逐一核对8种抽牌遗物在回合1～4及已有计数下的原生命令、冻结查询、Fork/RNG/父分支和live隔离；它只验证查询，完整生命周期沿用对应遗物合同。

`Contracts/Search/UnattendedTestRunner.CardNotificationBound.cs`使用`CARD-NOTIFICATION-BOUND` / NECROBINDER / NIBBITS_WEAK / 120秒及根快照停止参数。验证原生重放通知直接回复2点时撤销有限回复资格，8事件×五牌堆／floating／永久牌组56拒绝、晚绑定／移除、新旧根隔离及16Fork完整状态／历史／RNG；不认证其余UI与未来来源。已撤回的生成闭包／查询惰性合同仅在[研究证据](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/card-notification-and-recovery-roi-20261009.md)归档。

`Contracts/Combat/UnattendedTestRunner.HpLossClamping.cs` 使用 `HP-LOSS-CLAMP` / IRONCLAD / FUZZY_WURM_CRAWLER_WEAK / 120秒及 `--verify-combat-root-snapshot --stop-after-combat-root-snapshot-assertion`，直接比较原生扣血的168组负数、小数、零、致死、封顶及decimal极值和全部伤害字段／生命通知。16孩子串行Fork后并行修改，检查完整状态、历史、九RNG与父/live隔离；不执行完整伤害Hook管线。失败基线与最终证据见[回复闭包盘点](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/pruning-source-closure-and-hp-clamp-20261009.md)。

`Contracts/Search/UnattendedTestRunner.PotionCostIncumbent.cs` 使用 `POTION-COST-INCUMBENT` / IRONCLAD / NIBBITS_WEAK / 120秒，验证实际完整14/9成本胜利下同回合和更晚回合的等HP保路及原生EndTurn/用药完整状态。`ZeroAllowanceRelicIncumbent.cs` 使用 `ZERO-ALLOWANCE-RELIC-INCUMBENT` / 同角色遭遇及上限，验证真实完整胜利在零战损让步遗物目标下的本地及协调器后续HP界，保留同HP、正额度、成长和追回，原生HP消耗前缀严格差分。

`Contracts/Search/UnattendedTestRunner.ZeroCreditGrowthDominance.cs` 使用 `ZERO-CREDIT-GROWTH-PROOF` 或 `ZERO-CREDIT-GROWTH-PROOF-REGEN` / NECROBINDER / NIBBITS_WEAK / 120秒，从原生根生成完整成长胜利和真实不同成本用药分支，严格增量回放验证跨成长桶的较差战损剪枝、同战损及更便宜/未知成本保留、16并发见证读、已有再生、正额度/遗物/追回/强制用药拒绝及父/live/RNG隔离。同一入口调用 `Contracts/Search/UnattendedTestRunner.RetainedPrimaryDominance.cs`，用实际完整胜利验证开放用药的P0/P1/P2严格剪枝、相等保留、精确额度及禁药/强制/成长/遗物/追回拒绝、未知消耗堆Feed保留和16个独立消费者。无成长根保留旧消费者；该合同不替代卡牌实际原生执行差分或DOP1/16整协调器验收。

`Contracts/Combat/UnattendedTestRunner.ProjectedHookReceivers.cs` 扩展 `MIRRORED-HOOK-FILTER` / IRONCLAD / FUZZY_WURM_CRAWLER_WEAK / 120秒：23张小牌组必须实际进入投影构造，同根投影和完整监听路径核对完整状态、指纹、历史、RNG及64回调类，覆盖牌堆/附着/Power变化、16兄弟Fork和getter补丁回退。58项空索引复验检查覆盖双卡反复变化、移出任一待复验牌、第三张变化回退、第二张未知附着、成员增删、重复包装对象、嵌套通知及16兄弟／第二代的完整状态oracle。生成回调成员使用Void并先强断言参与性，再核对成员存在及Fork身份；不能以零mask卡牌的物理成员存在代替回调覆盖。小牌组原生成功证据复用同源码，后续九根加已有两根的最终质量／峰值见[组合验证](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/member-search-census-and-composition-20261009.md)；此前严格耗时门槛失败保留。核对模式返回完整源继续执行原facade审计；合同耗时不用于性能验收。

一次性调查放 .local/tool-tasks/<任务>/，验证时显式接入，结束清理代码、路由、参数、输入和产物。普通构建排除 .local 源码。新增正式文件按上表收纳；根目录只保留本入口。

旧 Soul/Custom/外骨骼虫路径追踪及 ACT3 硬编码路线观察入口已退出当前树，调查代码见 [固定提交](https://github.com/Torch1230/CombatSolver/tree/fe3edd2f7b4f3a92b266e6b13293810d31ce2e1b/src/Testing)。原生已知路线回归、生成上下文合同及其公共快照辅助继续维护。历史质量缺口与失败记录保持原结论，源码精简不代表问题修复。

[精确用药续搜实验](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exact-retained-victory-pruning-research-20261007.md)的两个扩展合同及首轮截止见配套源码/结果；原型撤回后现行合同范围见上文，不将实验覆盖计为生产覆盖。

[见证来源及库存闭包研究](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/pruning-witness-provenance-20261007.md)仅有两次完整只读诊断、静态间接调用定位和原生缓存源码复核；1818次潜在访问使用同一战损57胜利，相等候选为零。没有新增原生合同、生产认证或纯性能验收，插桩撤回。

[转置标签与存储研究](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/transposition-label-pruning-research-20261007.md)归档两版生产文件链接的托管oracle/存储检查，以及`ZERO-CREDIT-GROWTH-PROOF`两个原生合同和16次完整交错。原型与测试扩展均撤回；本轮不扩大现行合同或生产认证范围。

[历史计数门槛研究](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/saturated-history-key-research-20261007.md)只读统计必须包含未调用Solve的并行展开worker；leader日志或leader导出不能代表完整请求。三根最终诊断保持14项质量/根/政策/预算，未观察到新增状态合并，未做新增原生语义合同或纯性能验收；三处探针源码及一次性入口已撤回。此前不完整导出只保留调查记录。

[消耗堆等价研究](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-order-pruning-research-20261007.md)另外观察真实准入/展开字典、原六项标签和租约旁路；五次完整只读请求14质量/根/政策/预算保持。潜在访问并非实际剪枝或速度证明，两个入口重叠不可相加。原生有序自动出牌/回调/返回引用尚需闭包证明，临时源码和构建已归档清理。

[消耗堆原生认证研究](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-equivalence-certification-20261007.md)执行两种Eidolon消耗顺序：同完整无序键和九RNG但实际伤害8/6，原生/模拟完整续用及快照相等。32孩子串行Fork后并发修改，富历史/父/兄弟/live/RNG隔离保持；不是并发Fork或完整可达认证。临时合同路由和源码已撤回，源码/输入/失败保存在报告，两实例删除。

[消耗堆保序投影](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-projection-subsets-20261007.md)保存三版六次完整只读请求，14项质量/根/政策/预算保持。回调名称元数据仅作测量先验，潜在准入/展开访问不可相加或计为实际剪枝；无新原生合同、安全证书或性能验收，任务插桩和构建已清理。

[消耗堆来源闭包](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-reader-closure-20261007.md)记录实际虚槽与基类事件入口、九项Cecil元数据边界和未完成的组件证明；不执行游戏语义，异常元数据拒绝不是新原生/Fork通过。一次性项目和构建归档清理，既有游戏行为证据复用。

[消耗堆保序原生正例](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-positive-native-contract-20261007.md)覆盖两种顺序、四次真实出牌、未来Shiv/Inky和下一回合完整状态；32孩子串行Fork后并行修改附着效果，父/兄弟/live/RNG保持。只证明指定轨迹，没有未知来源门禁或完整动作闭包，不计生产认证/剪枝/提速；任务路由逐字恢复，两实例及源码/构建清理。

[相同前缀原型](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exact-action-prefix-replay-research-20261007.md)真实测试冷/热/不同前缀回退、生成牌/随机目标、当前评分和16缓存Fork；测试持有弱模板，未知风险为注入条目。完整请求变慢撤回，缺根绑定/GC/交互/最终回归，不作通用缓存或新速度结论。

[完整动作重复回放研究](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/action-transition-opportunity-20261007.md)扩展离线宿主可选观察，按冻结根、完整动作、capture模式及实际worker拥有者分组；仅检查选定特征，未执行新原生/Fork合同，不替代完整历史或全部政策的安全证明。生产搜索及部署保持。 后续[历史前缀边界](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/transition-prefix-boundary-research-20261007.md)以临时夹具复现同键/同数量不同历史，并核对16个已Fork子分支并行修改及原生完整状态；不代表完整原合同、并发Fork或缓存安全。首版Power排列失败保留，临时路由恢复。

[生命支配诊断](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/hp-state-dominance-research-20261007.md)只投影CurrentHp并观察真实转置表，覆盖全部并行worker；两个完整请求质量/根/政策/预算保持但工作量变化。计数含终局、准入和展开重叠，不能计作实际剪枝；条件战后回复反例仅为源码算术结论，未跑新原生/Fork。三处插桩恢复，源码/构建清理、正式部署复用。

2026-10-07 [剪枝根与扩展审计](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-root-extension-audit-20261007.md)保存真实生成器根的一次性盘点与16初始Fork读取证据；临时入口/源码已撤回，未形成生产认证或性能验收。

[扩展注册原生合同](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-extension-registry-contract-20261007.md)补齐真实根的生命周期、克隆、默认能力与保存槽盘点；最终登记变化/原生克隆/16初始Fork通过，临时入口撤回。完整证书和性能未验证，失败/崩溃保留。

[克隆真实绑定合同](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-clone-binding-contract-20261007.md)核对实际内部方法、同名同数量替换、零费用层拒绝及原生资源复制隔离；初始54根牌/20原型无资源层。临时入口撤回，无新Fork或整请求验收。

[读牌调用边界](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-reader-footprint-20261007.md)仅从既有IL清单形成候选方法与核对正文；不是原生/Fork或整根证书。保守图包含非搜索阶段及潜在委托，未知回调和未来写入继续保留有序语义；临时脚本归档清理，运行时未变。

[清理回调合同](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-cleanup-contract-20261007.md)用脱离牌堆的原生两牌验证局部清理、跨牌订阅反例与16路两代Fork费用隔离；未执行完整EndTurn或整根证书。临时入口逐字恢复，实例/源码/构建清理；生产及部署复用。

[扩展写入审计](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-extension-writer-audit-20261007.md)的14项检查只验证Cecil元数据边界及调用token，不执行CLR/游戏语义；已有游戏合同按原范围复用。一次性项目、夹具、生成脚本和构建已归档清理，未扩生产认证。

[初始生成/有限回复剪枝研究](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/initial-generation-finite-recovery-pruning-20261007.md)保留实验原生完整状态、32并行Fork、稳定P0门禁及11根44次交错结果；额外原生实验复现未登记未来生成牌OnPlay被认证接纳，原型与专属合同/路由撤回。完整源码及构建/启动参数在配套JSON中，旧实验名不是当前生产测试入口。
