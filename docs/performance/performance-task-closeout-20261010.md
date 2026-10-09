# 重战斗性能优化结项与上游交接

2026-10-10，按用户要求停止继续优化，交付已验证的阶段成果与失败研究记录。**原始全部慢场景至少两倍的总目标没有完成。** 本页是结项入口，不安排下一轮自动实验、合并、升版或发布。

最终研究源码为 `77c8bf37225cf546633c9a2a196d76ed364a629e`，上游基线为 `5c773caa`／0.50.1。拟交接代码采用这份已验证组合，已有 PR #235 的独立监听成果一并保留，按机制组织审阅。原始长报告和逐次 JSON 留在研究分支，避免把全部实验档案塞进功能 PR。[收尾数据](performance-task-closeout-20261010.json)保存最后三次请求、政策纠正、既有质量记录核对和已清理脚本源码。

## 可以利用的成果

| 成果 | 已有整请求实测 | 合入价值与边界 |
| --- | --- | --- |
| 镜像专用监听构造 | 2305张铁甲战士压力根：191.859／158.073秒 → 52.950／52.772秒，中位3.310倍；峰值降低70.13% | 已有独立上游版本验证，是最明确的大幅收益；只省略已证明不参加镜像回调的接收者，完整监听表保持 |
| 牌堆潜在接收者索引 | 在上一已验收版本上，巨型根51.989／52.192秒 → 43.191／42.886秒，1.210倍 | Fork只共享不可变索引，消费当前分支模型；不透明、别名、未知补丁保守回退 |
| 空接收者成员增量与双卡复验 | 以近期增量复用前版本作直接控制，巨型根43.328／42.884秒 → 16.958／17.254秒，2.520倍，峰值比0.9893 | 固定两个分支自有待复验引用，避免整堆重扫；第三张不同牌及非空证书仍完整重建 |
| 小牌组监听构造 | 猎手／无厌沙虫26.271／26.606秒 → 25.352／25.416秒，1.0416倍；储君哨兵0.9979倍，约慢33毫秒 | 已完成固定11根质量／内存回归；只作为有限组件收益，不宣称普遍提速或新两倍场景 |
| 新颖性探索后提前取得胜利见证 | 上传的0.49.1灵魂枢纽原包：343.350秒 → 157.820秒，2.176倍；战损19→6、峰值降低63.562% | 仅已认证、非固定预算的既有阶段复用开局计划／窄先导；完整成本含后续用药审计，原生成长、遗物及保命目标保持 |
| 其他严格胜利界与污染牌续执行 | 零成长HP额度、已付药水成本、已保留完整胜利的后续能力搜索，以及污染牌自身选择前缀已分别验证 | 可复用的机制能力；续执行单项完整亡灵精英约1.020倍，没有大幅通用收益承诺 |

各行来自**不同阶段、不同控制版本**，不得相乘成最终累计倍率。前三行巨型根均为15原始战损、战略差9、零药、3回合获胜；不是恢复输入中写出的999HP敌人，而是实际根中的`MECHA_KNIGHT`。396张猎手／永世沙漏根在监听阶段仅约1.085倍，后续改进很小，两版仍未获胜，不能算完成质量或两倍验收。

各阶段源码、样本及适用前提分别见[独立监听构造](projected-listener-producer-20261008.md)、[牌堆索引](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/pile-hook-projection-20261009.md)、[成员增量](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/empty-receiver-certification-20261009.md)、[双卡复验](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/two-card-empty-receiver-recheck-20261009.md)、[最终小牌组组合](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/member-search-census-and-composition-20261009.md)、[原包灵魂枢纽](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/novelty-certified-opening-incumbent-20261008.md)、[零额度成长](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/zero-credit-growth-hp-pruning-20261007.md)、[已付成本](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/paid-potion-cost-bound-20261008.md)、[保留胜利见证](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/retained-victory-strict-hp-pruning-20261007.md)及[污染牌续执行](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/tainted-card-continuation-20261008.md)。

早期已合入上游的 PR #207／#215 等成果沿用上游，不重复实现。本次监听优化新增回复认证与搜索剪枝均为0；胜利见证机制另按其原政策和来源证明消费。KnownSources忽略尚未生成的随机药水是已授权政策，不冒称严格随机生成闭包证明。

## 验证口径与最终产物

监听系列完整请求使用游戏0.111.0／41cef1ea、RitsuRuntime0.6.6，AMD7840H／8核16线程／约58GiB、离线CLR9.0.20；极高、DOP16、Beam135、500000节点、分支72／42／54、300000ms、组合和生产预算、NoGC16GB。串行独立进程 A1/C1/C2/A2，固定根、牌序、九RNG、政策及运行环境，诊断和正确性插桩另跑。峰值统计进程RSS，不能与累计分配量混用。

灵魂枢纽原包单列：原生CLR9.0.7、Beam512／500M节点／分支100／42／54、原300秒预算和开启的新颖性组合，两侧关闭NoGC，沿用用户仅对该原包及对照授权的900秒外层。其4次原生串行请求与监听系列离线数据不可混算；实际自动战斗5回合获胜，战损6、力量药水1瓶、意外重规划0次。

最终组合DLL SHA256为 `7714f7a34cd73beb6ae56de41178c6178f6b111db60fe1b441b39dcb2fd03dfb`。23张牌原生合同通过：7次实际投影、9次完整状态／指纹／历史／RNG对账、64类／1675模型、22项既有边界、58项空索引检查、16兄弟及第二代、getter补丁回退与父/live隔离。独立实例已清理。

最终固定11根全部14质量字段、完整导出snapshot、根/live、政策、预算及状态与各自控制一致，无时间边界；最高峰值比0.998483。九根新增候选只作回归，两个已成功完整交错组和原生证据复用；没有追加速度样本挑选有利结果。部分猎手总工作有小幅差异，不能声称所有物理计数或固定工作吞吐相同。

结项另只读核对既有 PR #235 的上游控制与最终11根记录：14质量全部相同。跨日期峰值比最大1.017338只作档案核对，**不是新的同环境交错性能组**，亦未据此宣称完整根/RNG逐项等价。原始全目标与当前阶段回归的基线不同，不能用这11根替代全部原始慢场景验收。

源码迁入PR后逐字节核对全部979个当前C#、项目与构建配置输入均相同，清单摘要和归档链接写入收尾JSON，复用对应成功构建／行为证据。精确manifest、DLL、Windows MemoryCleaner及两份许可已部署，本次不重复相同部署。没有修改评分、削减预算、跳过审计、硬编码路线或将实验DLL装入玩家目录。

## 探索过但没有值得合入的收益

下表的“撤回”只针对所列原型，不能把某次微调失败推导成整个机制无效。原生合同通过也不等于性能通过；失败、波动和未证明项都保留。

| 方向 | 实测／证明结论 | 交付决定与证据 |
| --- | --- | --- |
| 排序键、排序排列缓存、分支排序COW | 大牌组排序输入重复约1.875%；比较键改造未取得稳定完整收益，还须保留原生同分次序和RNG | 原型撤回，见[排序比较键](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/native-sort-key-research-20261005.md)、[派生成本](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/derived-state-opportunities-20261005.md) |
| 基础牌值、逐卡指标、牌库聚合／存在缓存 | 观察到80%以上局部命中不等于整请求收益；多版短测接近1倍，压缩牌值版完整仅1.018倍且哨兵峰值+12.158% | 撤回，见[牌值存储](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/intrinsic-value-storage-research-20261006.md)、[逐卡缓存](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/strategic-card-metric-cache-roi-20261009.md)、[牌库聚合](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/deck-derived-reuse-roi-20261009.md) |
| 整快照或整搜索成员复用 | 大容量快照重复在实际慢根较低，累计战损／历史等依赖不随状态键等价；43个完整成员签名重复为0 | 未实施生产缓存，见[快照值机会](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/bounded-snapshot-values-roi-20261009.md)、[成员盘点](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/member-search-census-and-composition-20261009.md) |
| 请求级回放LRU、StandPat试算缓存 | 小LRU覆盖低，StandPat实际可省工作约1%；容量和所有权成本没有换来共同提速 | 撤回，见[公共复用筛选](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/common-reuse-roi-research-20261009.md)、[完整前缀复用](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exact-action-prefix-replay-research-20261007.md) |
| 排名前删除已证明较差的回合快照 | 虽避免上万快照，Beam补位导致展开／转移增加；储君约慢3.6%，亡灵约快4.3%，没有共同收益 | 撤回，见[完成回合前剪枝](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/completed-turn-snapshot-pruning-research-20261006.md)；死亡快照另仅约373ms机会，见[终局观察](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/terminal-defeat-snapshot-opportunity-20261008.md) |
| 原生牌堆位置索引 | 单查询约20倍，完整请求仅约1.032～1.058倍；猎手首领54／69战损波动使质量门槛无法成立，限定版亦未解决 | 撤回，不以节药抵消增损，见[根牌堆索引](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/root-native-pile-index-research-20261009.md) |
| 扩大生成／回复来源认证、延迟回复查询 | 目标新增216次剪枝但短测0.974倍；回复查询减少约36%也没有稳定净收益，部分完整回调闭包尚缺 | 扩展原型撤回，仅保留必要拒绝保护，见[卡牌通知与回复](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/card-notification-and-recovery-roi-20261009.md) |
| Fork哈希、身份登记、容量及默认回调微调 | 局部零分配、查询减少或碰撞下降，多数只换来约0～2.5%差异，未达到投入门槛 | 撤回，见[Fork查询](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/evaluation-demand-and-fork-query-roi-20261009.md)、[身份/JIT](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/choice-identity-jit-roi-20261009.md)、[布局容量](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/hitting-and-layout-roi-20261009.md) |
| NoInlining、AggressiveOptimization、需求专用编译 | 代码尺寸减少与短搜改善未转成整请求收益；需求专用版完整0.982倍；六热点低tier叶侧采样合计约0.1413% | 撤回；没有实现汇编／Rust内核，见[需求专用编译](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/strategic-requirement-specialization-roi-20261009.md)、[硬件/JIT](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/pmu-jit-direction-20261008.md) |
| 直接延迟投影／按状态键提前判重 | 约76%投影值未读，但后续估值依赖投影牌序，存在浮点取整反例和释放后读键；原转置标签还含完整Score与调度豁免 | 没有闭合安全证明，不接入生产；见[消费时点](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/evaluation-demand-and-fork-query-roi-20261009.md)；末轮只读源码审查不计新原型或测试 |

探索中发现的负伤害截零、未来药水最低价格假设、生命／卡牌／药水通知和Ritsu能力绕过回复表等问题，最终保留相应原生对账及保守拒绝修正。它们是正确性边界，**不计成性能收益**，详见[负伤害](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/pruning-source-closure-and-hp-clamp-20261009.md)、[未来价格](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/future-potion-price-bound-correction-20261008.md)、[生命通知](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/health-callback-pruning-guard-20261008.md)、[用药回调](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/pruning-callback-proof-research-20261008.md)和[模型能力](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/model-capability-recovery-proof-20261007.md)。尚未审计来源继续未知，原版程序集身份或治疗文案缺席不能代替证明。

## 未完成项与最后一次补测

- 原始所有超过20秒请求统一至少两倍、峰值最多+10%、战损不增加的全量最终验收未完成。用户接受的感染棱柱约1.98倍及历史猎手精英约1.861倍只限各自场景。
- 396张猎手／永世沙漏仍未获胜且未翻倍；亡灵训练外根、投影根等仍慢。女王／丢路研究按用户要求暂停，历史跨版本质量缺口和同版战损波动未宣称修复。
- 中断前当前组合的 `holdout-04-necrobinder` 整请求120秒超时，已停止，峰值采样13.493GB，没有最终质量；不延长、不推算倍率。
- `screen-necro-projected` 首次误沿宿主默认Smart，77.908秒，单列变体并排除原政策验收。显式恢复原“至少使用一瓶药水”后113.392秒、9战损／2药／9回合；导出`hasRisk=true`及未补偿选择缺口，不能算合格质量或提速验收。两次均保留完整数据，不挑77秒作成绩。
- 上传灵魂枢纽的2.176倍来自对应阶段原生构建；最终叠加版本没有再跑原包新对照。固定生成魂枢根不替代上传原包。Windows、可见Steam帧时间及全部来源的完整闭包未完成。

本次按用户口令在此收尾。已成功证据按其精确源码范围复用，失败原型保留研究记录并退出生产。PR由上游审阅决定是否合入；本任务不自动恢复优化，也不自动合并或发布。
