# CombatSolver 测试入口历史卷 04

## 0.38.0：计划外重算修复

- 发布范围冻结在已验证行为提交 `d8ae412`：前两批18类机制及2张牌估值。后续木乃伊之手仅有诊断场景，没有验证成立的修复，已从发布源码移出。按用户要求将未发布准备版本0.37.1改为0.38.0，仅同步版本与中英玩家日志，沿用下列已完成的行为证据；官方名称从当前游戏PCK读取。版本输入变化后重新执行一次Release构建和最小ZIP，不重复行为测试或运行完整发布门禁。

- `REPORT-CARDS-ORBIT-ENERGY-GATE`：旧入口失败 `1e0277417f9d457d8aea1d4205a5869c`，4初始能量、4张防御、1层环绕轨道和禁止返能，第四张后预测1/原版0能量。最终扩展为8能量、8张防御、两个独立轨道实例1/2，第四张后移除禁止返能，`2bec4ffb9a164481b5ace1a668f78906` Passed（26.44秒）。每张卡和移除时点比较完整状态/RNG，每步检查Fork；验证禁止返能期间仍消耗触发、解除后继续正常返能。Release零警告/错误，Windows结构门禁通过；前一顺序修复的CoverageCatalog门禁通过，目录仍有22项回放视野外状态写入，不作全量语义正确声明。

- 第二批顺序修复：`REPORT-CARDS-SPOILS-ORDER` 失败 `7f9020a9cdc4412da48e30607c9335c0` → 通过 `1be7ac540f9a4ebcab455c7b77e48457`；满手 `REPORT-CARDS-SPOILS-FULL-HAND` 通过 `edee5895c8cc4ea195cc16d651926827`，只打第一张战利品，核对剑占最后手牌位、两张抽牌仍在抽牌堆。`REPORT-CARDS-ADRENALINE-VOID` 失败 `7c55fe83182f4596b29ccbf357c0883d` → 通过 `8bf7b3754ad74e24af9e47ccf43afbd0`；`REPORT-CARDS-OFFERING-VOID` 失败 `b501384837294a6abbfdb8aed873dfa4` → 通过 `2f6ed6da25e646d3aa584a47982842ef`；`REPORT-CARDS-NEUROSURGE-VOID` 失败 `7e01d53cce3a49f6ba193558402ba0ea` → 通过 `0ae3660ccf914e7f9d55c6fc0dfa738b`。均为一步原生动作完整状态/RNG及分支Fork比较，最长24.62秒；没有运行搜索或整场部署。复跑输入见[第二批记录](../issues/report-replans-20260913.md#第二批继续修复)。首次误填 `AXEBOT` 导致建局失败，不计行为基线。

- 批次收尾：16类可复现机制及2张牌估值已分别取得行为证据；严格合并同根后未达到20–30类高频目标。最终Windows结构门禁 `REFACTOR_BOUNDARIES_OK search_files=89`，CoverageCatalog `--verify-effective --verify-runtime-evidence` 通过（3035项、0未分类/缺关联通过证据；22项处于回放视野外，属于目录边界）。正式版本仍0.37.0；完整频率、范围和未解决项见[批次结果](../issues/report-replans-20260913.md)。本轮没有运行Linux游戏、可见Steam或发布流程。

- `RELIC-DAMAGE-WAKE`：熟睡甲虫失败基线 `3ccdcacbe9aa473e8a777b34ea99391e`，招架盾原生伤害后 SLUMBER_POWER 为2、预测为3。修复后 `252a8f9dc76e4610ac455f05388eebcf` Passed（23.11秒），乐加维林族母 `1012870880214ac79bbc646c5e5d46a2` Passed（9.06秒）；均比较完整一步状态与 RNG。命令使用 `-ScenarioId RELIC-DAMAGE-WAKE -EncounterId SLUMBERING_BEETLE_NORMAL` 或 `LAGAVULIN_MATRIARCH_BOSS -EnemyCurrentHp 100`，无增量搜索。Release零警告/错误；CoverageCatalog `--verify-effective --verify-runtime-evidence` 通过。原包完整部署、可见测试未运行。

- `AUTO-DEPLOYMENT-REQUEST-OWNERSHIP`：有效失败基线 `7378bb2aa3af43219daa7dd48f4a2712` 在部署进行中检测额外搜索。修复后 `8870e33fd0b344b6ae9c2393b59e902e` Passed（23.81秒），原生两张攻击完成击杀；`Instant / 0秒`。早期两次夹具误在战斗清理后比较账本，已修正断言时点，不作为失败基线。嵌套 PowerShell 重定向导致启动器 stdout 句柄未关闭，已结束本任务等待进程并保存游戏完整结果，后续在单层 PowerShell 运行。相邻 `AUTO-TURN-REQUEST-OWNERSHIP` 的 `da5ef97e68e341748a6e4b31f9025fc7` Passed（22.93秒），显式手动重算可用。Release与Windows结构门禁通过。

- `SPAWN-POWER-ORDER`：`FABRICATOR_NORMAL`，`FABRICATOR/FABRICATE_MOVE`，清空遗物后注入 `PHILOSOPHERS_STONE`；使用既有 MonsterMoveChecks 协议。失败 `7196457aa1a640b1840bafaf116764f5` 与修复通过 `8ee41d141e1c40caa71d1adaf4f4d0f8`，核对完整有序能力、阵容、状态与RNG。Release通过，无原包整场/可见验收。

- `INSTANCED-POWER-AUTOMATION`：有效失败 `55983c477ab849169dd1c0c36aca5152`，预测单实例3、原生两个实例1/2；初版 MoveStateSnapshot 的内部状态字典不接受同名实例，改用严格 ContinuationStamp（`95c4ce554d5a44cca5cb9369cb5bdfb0` 是夹具限制）。扩展生命周期后 `3502fcbd0a1a45ce8714bac2bb87a4a0` Passed，`INSTANCED-POWER-BOULDER` 的 `4ca7a4ee808244f6896c0b921952505b` Passed，覆盖Fork、重新捕获、追加、移除。`INSTANCED-POWER-TARGETED` 的 `bb2ac2b460e44c1b867361e5a033cd15` Passed，定向实例/首次查询/逐实例Gold写入合同；该Gold写入断言不是完整原生偷窃回合验收。Release通过。

- `CRAB-RAGE-DEATH-TIMING`：`KAISER_CRAB_BOSS`，一只1HP、另一只100HP，前者先死亡，再对后者造成20点伤害。失败 `94557ad1b38945b297365a0228455829`，修复 `cccecc62cf9d4c15891f02a218d59573` Passed；严格状态/RNG差分，清扫后继续检查。Release通过。覆盖目录将 CrabRage 与前项 Asleep/Slumber 的权威来源更新为精确镜像并关联本轮证据。

- `NIGHTMARE-SELECTION-SNAPSHOT`：静默猎手，手牌夜魇/精密瞄准，先注入2层无限刀刃，完整两回合原版差分。失败 `e3e990169f6a48dc8ec78d26df15d45a`，通过 `fba8186242f44a93835b9f01955d8003`；检查所选牌快照与生成顺序。`NIGHTMARE-CAPTURED-ROOT` 的 `3649d93b13254fe88f34d0f3c5b842b4` Passed，覆盖活动夜魇根捕获、原版副本、Fork与live隔离、fingerprint及ContinuationStamp；根捕获初始失败记录位于本地 nightmare-root-baseline.txt。Release零警告/错误。未运行原包整场部署。

- `SIGNED-GOLD-LOSS`：失败 `0dd6c25a8f1c4f98ae7e96d8c7180481` 复现137/142金币差异；通过 `7877327a996f475db12dede147ffad5d`，依次扣减-5、0、3、200、-5，比较完整状态和RNG。Release通过，未声称修改遗物Mod的整场兼容验证。

- `EMOTION-CHIP-PREVENTED-DAMAGE`：无格挡、缓冲1，受到10点伤害后进入下一回合，等离子球与情感芯片结算。失败 `b865f0b9a3c64989a3050622c32dea55` 为3/4能量；通过 `66c62e28bf1f4833bf15d2fce60681d5`，完整状态/RNG一致。Release通过。

- `SETUP-CAPTURED-HISTORY`：储君在准备根捕获前获得星能，通过实际 `ReplayTurnSetup` 入口继续准备并打出 Radiate。失败 `8b05f81f0b9648d5b35ba8d476f0cc04`，敌人HP57/39；通过 `7c729478f0d14270a5cf8ff4e0d000db`，完整状态/RNG一致。使用固定动作回放，无正式搜索扩展；Release通过。

- `BLOCK-EVENT-HISTORY`：失败 `cb0e848069e04c8283eabcbf29d2e4c7`，残影触发后防御少5格挡。扩展后的 `0f404dbcbe3b4e568e6c89579158774b` Passed：非卡牌格挡、同一次出牌连续两次格挡、下一次出牌，均比较完整状态/RNG。首次扩展构建缺少夹具 ResourceInfo 必填值，补齐后Release通过。该验证覆盖计数机制；原报告额外格挡来源仍待定位。

- `ZERO-BASE-BLOCK`：无中毒目标、敏捷3，打出蜃景。失败 `22dccf98c4c74e528bfdfb117a970515`，通过 `e825941c7fcf4fc4aea55c9e90950def`，原版与模拟完整状态/RNG一致。Release通过。未单独验证第三方零格挡通知监听器。

- `LAMP-INDIRECT-TEMPORARY-STRENGTH`：君王凝视→打击→致命毒药，严格Continuation比较。失败 `8ef8bc5c0a834483a0dad70b06766b5f`，通过 `5e6a572ec826440790e071cb2e931736`；确认附带减力量保留灯笼次数，直接施毒正常翻倍并消耗次数。Release通过。

- `ORBIT-CAPTURED-ROOT`：原版实例已记录2点能量花费后捕获，继续4张防御。失败 `e218076e263c4f8f85153a848a7e75dd`，通过 `656deacd5dbc4afebe71929d6161d94a`；覆盖原版返还能量、重新捕获、Fork隔离、新实例0余数、指纹及续用比较。扩展夹具先补齐nullable断言和命名空间，再取得Release零警告/错误。

- 环绕轨道/自动化估值：固定6000节点、Beam24、DOP1，环绕轨道250HP目标旧路线第16回合死亡→第25回合获胜、65战损；自动化180HP目标旧路线第18回合死亡→第20回合获胜、69战损。原版完整部署 `ef36ab823040497d807b192c6fa8fcec` / `7f8098525c3f4cbebddb3959fa2fc211` Passed，实际HP10/6、重算0、Instant/0秒。14HP短战 `9d69f037632d472385b5d40f6bc3445e` / `13f948c2f025435e8555fe7d4bcf7481` 均首回合无伤获胜；600节点增量检查 `e62f83acb8be4bcbb9e08c4d968f7cae` / `5f928b0014114a78a9e83ccafac3d186` Passed。`AUTOMATION-CAPTURED-ROOT` 的 `a3152fff299c4db9bb1445c2076c3f9f` 检查剩1次抽牌时捕获、返能复位与Fork；`AUTOMATION-NATURAL-DRAWS` 的 `600c0898f3ca4b23a8d73fdc476bdb77` 只靠每回合5张自然抽牌，两回合完整状态/RNG通过。完整参数、失败与未改善场景见[专项记录](../issues/recurring-energy-valuation-20260913.md)。

- `IMPLICIT-HAND-CHOICE-ORDER`：隐秘匕首、打击、防御的固定手牌，正常选择生成器曾将必须全弃的两张牌重排。失败 `a486b3292369471085a61799fcc30eeb`，C[0]预测防御/原版打击；修复 `809f6943021d4578b339b71e3bd53955` Passed，完整牌堆/状态/RNG一致，嵌套自动策略输出相同顺序。Release通过；没有完整搜索、逐包部署或可见测试。

- `ATTACK-START-HISTORY`：致死性75、痛殴与打击。失败 `a9b2864bbb3b46febb3332ccbd38f1b8` 中痛殴成长至14.50、原版10；修复 `8d36d575f6854aaf815a240d28186263` Passed，完整状态/RNG、重新捕获、父子隔离、第二次攻击及下回合计数重置一致。新增 `AttackStarts` 续用字段保留严格比较，旧文本缺少该字段不视为新格式整场回放通过。Release通过，未运行完整搜索。

- `PHANTOM-RETAIN-LIFECYCLE`：升级小刀先获得幻影之刃保留，再施加抑制。失败 `91737ac0e4bf4c1a81284168d0d44be7` 为预测保留True/原版False；扩展后的 `153756412f0640438927d4cf37b70fb5` Passed，降级、叠加、克隆卡入场、移除后重施加均比较完整状态/RNG。两次夹具构建补齐命名空间后Release通过；没有完整搜索或逐包部署。

- `DAMPEN-DEATH-CLAW`：爪击失败 `dbfd298906b4409a9f0b31c0aae60380`，最终伤害6/7、私有成长2/3；修复 `14ec10028e27418eb345a1d9a404e248` Passed。`DAMPEN-DEATH-SCYTHE` 的 `c5305c1973b94c14b75f960eed8faec3` Passed，恢复升级后按7成长。都用1HP魔法骑士与存活旁怪，完整单动作状态/RNG一致；并入已确认的死亡回调延迟根因。Release通过。

- `GHOST-SEED-KEYWORD-LIFECYCLE`：失败 `d2b7fffc0c8641a9b13e36958e1302eb` 的虚无状态不同，但旧ContinuationStamp首差返回none；最终 `ac64922583814c0690378914b6630b4d` Passed，覆盖降级、新卡/克隆入场、次回合状态和RNG，以及只改变本地关键词时续用必须不同。新增`keywords=[...]`字段后，旧报告文本缺少关键词不当作新格式全量回放基线；Release通过。

## 0.37.0：性能更新与 PR #89 合并验证

- 定版范围：PR #89 的已合并行为及已审核更新日志；版本与发布元数据变更复用以下验证，本次不重跑游戏场景，不作完整可见性能验收结论。

- 2026-09-13：合入当前 main，保留双方开发与测试记录；合并结果通过 Windows Release 构建（0 警告、0 错误）及结构门禁（89 个 Search 文件）。首次构建的辅助程序引用程序集解析失败，单独构建辅助程序后整体构建通过。此次仅核对合并衔接，以下游戏行为与性能结果沿用贡献者记录，本轮未重跑。

用户要求的最终VeryHigh压力测试（`e528749`）：四个独立headless进程，DOP16/NoGC16GB，原预设Beam135/100000节点/300000ms，不覆盖预算。灵魂枢纽`be412b0035104e85823b0363ad4e0704`通过（7.968秒、6.474GB分配、8.149GB峰值RSS，预测T9/战损6/零药，零GC）；死灵药水、2305张极端牌堆和女王生成选牌均为120秒启动器超时，不能记为通过或完整性能结果。见[本轮结果与复跑参数](../performance/veryhigh-final-20260913.md)。

五候选最终证据：实际StateStore源码新旧35,896项溢出/分叉/工厂重入检查通过；`8a251861ee034346b8f37788b7ba3aee`压力/取消/异常复用合同Passed。两组各四个新进程（3万/1万节点）分别85字段、22步路线一致；1万节点四次零GC。生成合同`a25ce6aae00e4e729d1adada8edb11f3`的16组完整状态/RNG比较Passed，独立计量`85f3392ba82d4a998c1c0225550a975b`Passed。原型的失败等价、未触发deferred请求、丢失的首版日志计量和被撤回方案均在[五候选报告](../performance/five-candidates-20260913.md)单列。最终Release零警告/错误，两端结构门禁通过；Windows先行部署415da12，不代表本轮新改动已部署。

派发空结果缓存：诊断逐次验证0陈旧结果，可省92.45%内部槽读取；最终选择回放/内存压力合同、Release及两端结构门禁通过。平均22.7681→24.0462秒（耗时变化+5.613%），累计worker分配23.1379→23.1200GB（变化-0.077%）。 85字段及22步路线一致；两对B更慢，撤回生产缓存并保留实验补丁。六方向全部结束，第3、6项撤回、其余保留。[完整记录](../performance/six-directions-20260913.md#6-派发缓存父节点的空选择扫描)。

本轮快照覆盖汇总：`f43fa39f50794e9899ca61ca7c068151`的194次完整列表/顺序、补偿标记、空历史/null trace及父子追加对照Passed，live不变；首版隔离作用域建局失败单列。压力合同`9c5c8b0aa26c4fe7b3619b2aee92daef`Passed；1GB/4万节点ABBA均Passed，85字段和22步路线相同、仍71次回收。分配−0.294%，未宣称0.93%为稳定提速；[数据与限制](../performance/six-directions-20260913.md#5-快照内部覆盖风险结果延后物化)。

本轮转置存储：512,005项冻结旧算法对照与单标签分配检查通过；`c37135f70b104acd95d31c47ae52c9ee`压力/取消/错误复用Passed。1GB/4万节点ABBA四次Passed、85字段与22步路线一致，均71次回收且无NoGC丢失；不能把5.226%均值耗时改善写成GC暂停或回收次数改善。生产Release与两端结构门禁通过；[全部样本与存活图限制](../performance/six-directions-20260913.md#4-小区域存活对象单标签转置前沿内联)。

本轮父状态缩锁：两次资格/原型诊断及四次ABBA均Passed；四次3万节点的85项质量/总工作字段和22步路线一致。候选仅放行3.70%且两对耗时均回退，已撤回；未把原生搜索结果当作第三方/取消缩锁合同。生产无此行为改动，未重复生产构建与已通过门禁。见[实验补丁、runId与GC限制](../performance/six-directions-20260913.md#3-父状态封存与fork锁原型撤回)。

单个EndTurn内部选择回放：只并发原预算保证必经的首层，嵌套预算/实体补充和待命基线仍原序消费。真实BaseLib/DOP16固定3万节点A-B-B-A平均24.1112→22.5695秒（少6.394%），分配增加0.211%；GC暂停44.161–4274.088ms，未建立稳定提速。85项质量/总工作字段与22步路线一致；同父双lane重叠、取消/错误排空、同根复用及104MiB压力合同通过，Release与两端结构门禁通过。见[完整样本与失败建局](../performance/six-directions-20260913.md#2-单个endturn内的首层选择回放)。

选牌组合与评分复用：预计算同层重复位置，直接按张数追加独占组合，评分仅在本次构造中惰性复用。两轮真实BaseLib/DOP16固定3万节点A-B-B-A分别少0.229%/0.223%累计分配；耗时分别+0.386%/−2.267%，两轮配对均不一致，后一轮GC显著波动，未建立稳定提速。1200输入原生完整选牌对照及DOP1/DOP2、104MiB压力/取消/错误复用通过；其余方向见持续追加的报告。见[逐轮数据](../performance/six-directions-20260913.md)。

回合尾部提前计算：初始动作/药水全部派发后即可独占计算EndTurn，全部兄弟工作结束后才按原序转交快照并发布待命基线。真实BaseLib/DOP16固定3万节点A-B-B-A平均24.0974→23.7545秒（少1.42%），两对同方向；85项质量/总工作字段与22步路线一致。并发/取消/错误/同根复用、104 MiB压力、9项批次与36项准入合同通过，生产Release和两端结构门禁通过。单场景小样本，不与Power收益相加；见[尾部并行报告](../performance/early-tail-parallelism-20260913.md)。

普通原版Power克隆并行：沿用精确元数据保护，仅放行继承默认克隆/内部初始化、变量已物化的原版Power，其他路径保留原锁。真实BaseLib/DOP16固定3万节点A-B-B-A平均24.2247→23.7295秒（少2.04%），两对同方向，85项质量/总工作字段与22步路线一致；分配略增。持锁并行及DOP1/DOP2压力/取消/错误合同通过，Release与两端结构门禁通过。理论模型未当作实测收益；见[估算与落地验证](../performance/power-clone-parallelism-20260913.md)。

克隆后的并行定位：两次不同插桩的真实BaseLib/DOP16诊断均Passed，85项质量/总工作字段及22步完整路线一致。505万Power克隆占预测模型克隆88.11%，优先核对普通原版Power免锁；回合尾部占展开作业累计时间44.39%，窗口平均11.42个作业中lane（含锁等待）。线程时间有重叠和插桩扰动，不是提速结论；未改生产行为。见[瓶颈与后续顺序](../performance/parallel-bottlenecks-20260913.md)。

16并行速度对照：真实BaseLib下，优化前后各两个新进程，A-B-B-A固定3万节点全部Passed；平均24.0270→23.8943秒（少0.55%），配对方向不一致且GC波动，未建立明确提速。85项质量/总工作字段及22步完整路线一致，11项调度字段单列。见[16并行数据](../performance/native-clone-parallelism-20260913.md#用户指定16并行速度对照)。

本轮原版克隆并行：真实 BaseLib 3.4.7 / Ritsu 0.5.20 下，`MODEL-CLONE-CONCURRENCY`（`c05503851b6942d2a262334c026fa4ba`）及 `STAND-PAT-MEMORY-BOUNDARY`（`df6f42ada6d14b4da39346df0f534c94`）Passed。覆盖持锁双线程64次克隆、变量独占、第三方变量回退、跨域克隆/变量补丁刷新、live不变、DOP1/DOP2及104MiB人工压力等价和取消/错误排空复用。Release零警告零错误，Bash与PowerShell门禁均通过（Linux）。不作整场提速或可见性能结论；[详情与失败记录](../performance/native-clone-parallelism-20260913.md)。

本轮父节点预约优化：最终3万节点A-B-B-A八次Passed，89项质量/总工作字段和完整动作一致；并行专属调度计数单列。盛碗虫群/亡灵104 MiB压力、DOP1/DOP2、取消/错误排空与同根复用均通过，纯算术突增/溢出及Linux构建/结构门禁通过。详见[最终样本与失败后备反例](../performance/bowlbugs-wave-admission-20260912.md)。

本轮盛碗虫群剪枝边界：最终16GB区域A-B-B-A四次Passed、3万节点完整路线及108字段相等；1GB区域4万节点候选Passed，基线完成同量工作后因NoGC退出而失败，102项非时序字段及完整动作相等。中间5万节点测试失败并以TimeLimit结束，未伪装成通过。小区域有总耗时/最大暂停回退，宽裕区域平均耗时少2.24%、分配少7.44%；全部仅Linux无头诊断首个主搜索。详见[分配、GC和限制](../performance/bowlbugs-slow-search-20260912.md#元数据边界后续与深度反例)与[机器可读指标](../performance/bowlbugs-prune-20260912.json)。

`STAND-PAT-MEMORY-BOUNDARY`最终盛碗虫群`160b0892d7024f27817bd1660a3e1581`（28.845秒）与独立亡灵`94bdb13661cb4d3ba403bada61e6858a`（8.277秒）均Passed：双lane取消/错误排空、原根复用、DOP1/DOP2及104MiB人工压力完整搜索等价、无色生成顺序/RNG/实例和计数Fork隔离。原224MiB人工压力因未穿过剪枝内边界而失败，未删覆盖断言；广域SearchPolicy历史SIGSEGV未解决，不能称完整门禁通过。生产Release构建0警告/0错误，Linux结构门禁通过（87个Search文件）。

本轮盛碗虫群：原生cursor0诊断恢复的完整ContinuationStamp匹配，native编码不可比较。药水谱系合同及A-B-B-A四次固定短搜Passed，57项非时序结果字段和22/19/18动作的三条完整路线一致。长线用药哨兵 `POTION-LINEAGE-NECRO-SENTINEL` / `f63cf487061448c6ae1289b11f0a6d67` Passed：战损4、药水2、第12回合获胜，展开53,236／转移589,526；只作质量哨兵。Release构建零警告零错误、Linux结构门禁通过。可比新进程样本分配少0.50–0.56%，未建立提速或可见性能收益；B2为热进程，不能混算平均提速。见[恢复限制与结果](../performance/bowlbugs-slow-search-20260912.md#后续恢复与药水历史物化优化)。

本轮选牌迁移：1,024组完整令牌合同、15,003次历史查询身份比较，以及赌博筹码/能力药水/发现三条原生严格差分均Passed；五个runId与覆盖见[报告](../performance/choice-migration-20260912.md)。A-B-B-A八个正式无头请求Passed、93项工作字段与54/136行完整路线一致，3项调度字段单列。机甲3.4294→3.5134秒（慢2.45%），瀑布9.3512→9.3059秒；累计分配分别少1.128%/0.645%。Release和Linux结构门禁通过；无可见性能、完整自动部署或Windows新构建验收。

本轮只读来源扫描：`tools/ChoiceSourceAudit` 读取1,284模型/5,955方法，202匹配调用点、0读取失败；85个显式选择调用点与原目录完全一致。能力授予关系补充后213模型逐项静态评估。该证据不等于模型行为或性能验收；见[来源清单](../performance/choice-source-inventory-20260912.md)。

本轮印牌历史查询：`GENERATION-HISTORY-CONTRACT`（`2a1fc39e85884552a90683036f4b3bc2`）通过，覆盖13,328次逐实例查询比较、129个保留历史分支及父子独立追加。瀑布巨兽／机甲A-B-B-A共8个正式无头请求Passed，93项搜索字段与136／54行完整路线一致；3项内存自适应并行批次数单列，不称96项全一致。累计分配下降0.79%／0.30%，稳定提速和峰值内存收益未建立。原包恢复为`restored_continuation`，native-state不可比较；诊断构建MVID绕行未进入生产。Release与结构门禁通过；无完整部署或可见验收。见[报告与数据](../performance/generation-history-20260912.md)。

## 下一版本（开发中）：精简 fork

快照按需读取：`tools/StrategicKeywordChecks/run.py` 134,930组完整策略上下文比较通过，覆盖全部65,536种需求组合、第三方类型和跨Build修改；还原的基线与原源码一致。原生无头A-B-B-A的8个正式结果全部Passed，每场96项非时序字段及完整路线相等；机甲平均快2.86%、亡灵快2.43%，未建立明显内存收益。`STRATEGIC-KEYWORD-INCREMENTAL` / `f868cb327b9c47d29647446880813f60`（力量1、打击/防御/小刀）最小增量回放通过；Release构建及Linux结构门禁通过，无可见测试。详见[范围与数据](../performance/snapshot-reuse-20260912.md)。

热点消除后续：重新采集 `8faa771` 两场CPU栈；快照释放集合候选完成A-B-B-A共8个正式结果，96项非时序字段及完整54／113行路线一致。`tools/SnapshotReleaseChecks/run.py` 864组释放调用序列合同、Release编译和Linux结构门禁通过；`SNAPSHOT-RELEASE-INCREMENTAL` / `d4eeec6d4b7142f59e9f7154ffbd6f4f` 最小DOP1增量回放通过。机甲平均快8.16%，亡灵平均慢1.43%且配对方向不一致，不宣称普遍提速；可见测试未启动，详见[数据与限制](../performance/hotspot-cuts-20260912.md)。

精简开发后续：空状态 guard、排名预计算分别完成四次交错无头正式样本，每场 96 项非时序字段及完整路线一致。排序合同 720 组/167,280 条目通过；`CARD-PLAY-CLEANUP-CONTRACT`（`6a59494233984b7582ba6213c528a724`）与小型 DOP1 增量搜索（`4685d92766c04f99ace8f6e3e70da3c3`）通过。大范围 Fork 合同在 `AssertEndTurnPowerChoiceSuspends` 失败，未修改基线也复现，未声称完整门禁通过。可见测试按指令停止，未取得可见性能结果。详见[结果与复跑](../performance/surgical-development-20260912.md)。

后续研究：`dotnet run --project tools/SurgicalResearchChecks -c Release` 通过。直接链接生产 StateStore，以最小模型替身测得空枚举 96→0 B、缺失 int 状态 Peek 24→0 B、8 类辅助表容量构造 992→440 B；每项三块分配读数一致。仅为独立分配机制探针，不覆盖真实模型、Fork 或游戏搜索；未改生产代码、未重跑下列历史场景。见[研究报告](../performance/surgical-research-20260912.md)。

基于 `eff8cf4`。本轮独立原生对照全部通过（列表候选撤回前执行；最终分支恢复上游列表，两个列表版本的独立合同和正式搜索等价对照均通过）：

- `DEFERRED-BLOCK-RETURN-NATIVE` / `ed52bbacc1384e8ba3ae4a78048743fe`：Passed；DeferredBlockReturn:Native3Roots:CapAndFraction:Stacking:Zero:AllSnapshotFields:PowerMetadata:ForkIsolation:AfterBlockCleared。
- `PLAYER-DEATH-POWERS-NATIVE` / `040ee891c3ca4129b34db3578e895bc3`：Passed；PlayerDeathPowers:BurnNativeDeath:ThreePowersRemoved:FullState:PendingLossThenDefeat:BothDamageOverloads:LiveAliveShadowDeadAndInverse:RootAndSiblingAfterNativeDeath。
- `POWER-DURATION-APPLICATION-NATIVE` / `1192427d2745489289805f814e231d6d`：Passed；PowerDurationApplication:ThreeEntrances:Native16Steps:NewStackExpireReacquire:ArtifactBlockedNoSkip:EquivalentKeys:FullStateAndLifetimeFields:ReplayAfterNative。
- `POWER-DURATION-KEYS-NATIVE` / `d0eb6de7069e40c9822fa2f144bc787c`：Passed；PowerDurationKeys:WeakVulnerableFrail:DistinctKeyContinuationAndFuture:PoisonNeutral:NativeSideEndTick:FullStateAndPowerFields:FrozenAfterNative:RootUnchanged。
- `TEMPORARY-STRENGTH-CAP-NATIVE` / `468cc40fb6be4f3b9233d8f6679bea3f`：Passed；TemporaryStrength:NativeStackAndInitialCounterCap:RequestedOffset:BeforeAppliedAndAmountChanged:AfterSideTurnEnd:FullState:ForkIsolation。
- `TEMPORARY-STRENGTH-ORDER-NATIVE` / `54fe00b2f914450b9921bf89dab9865f`：Passed；TemporaryStrength:NativeLossAndGain:FirstApplicationOrder:StackingAndNegativeOffsets:Artifact:StrengthRetirementReacquisition:AfterSideTurnEnd:FullState:ForkIsolation。

上游基线中，格挡返回、Power Target、临时力量封顶、持续时间键／施加合同重现失败；玩家死亡合同在上游已通过，不重复移植生产修复。初次格挡请求只因外部独占锁排队失败，释放获授权停止的旧实例后才取得真正失败基线。

列表原版／候选均通过全部操作、10,000 次随机分支与 8 个独占 worker 合同；B-C-C-B 正式对照中每场 96 项非时序字段与完整路线一致。候选整体分配收益不足 0.01%，已撤回。最终 Release 构建、Linux 结构门禁及 CoverageCatalog `--verify-state-fields --verify-state-writes` 通过（在隔离输出目录执行，未改写上游覆盖目录）；未运行 PowerShell 或可见 Steam。详见 [选择、数据和限制](../performance/surgical-fixes-20260912.md)。

## 0.36.5：三层首领策略

- 发布范围：已提交 de685ec 行为及文档合入 main；未验收神化工作区实验已撤回，仅私有补丁留档。复用以下既有测试证据，本次不新增整场质量或可见性能结论。

- 用户指定跳过18359灰水包：`act3-queen-exhaust-1835-current` 50/0药/T9/13756展开；`:5`玩家11原生事件后continuation/native-state均通过，后续7/0额外药/T8/6672展开。消耗引擎连续展开候选47/0药/T7/13633展开，未定版已撤回；专属追踪`0d290923a2c143b9a89158d681fa7d0a`在恢复阶段recorded_input_stalled，未跑到路径观察。泛化所有能力候选cc8a未获胜（64/敌方174/6835），已撤回；同候选18359启动器38348未暴露进程路径，不能报告搜索崩溃。恢复de685ec行为后Release构建通过。
- 女王230888玩家后状态恢复失败：Y旧0/0与当前0/0/0/0差异，native编码不可比；没有扩大旧字段迁移权限，不宣称玩家路线已严格复现。

- 取能力后连续展开：`d328e5afa35c43d1a5f15691f9395d0a` 与扩展到T2观察的 `b1433fb7f1584989bc40ed22a3d314d0` Passed，11战损/1药（T1）/T6；五个原始动作都准确生成和展开，第六步EndTurn进入T2为48HP、随后在跨回合PruneInput后未保留。扩展观察首次启动器进程路径读取失败，重试通过。`act3-fetched-followup-f25c` 保持22/0药/T13/12330总展开。`ACT3-BOSS-STRATEGY` / `de3503a330304ed3b771f5679933aa51` Passed；`act3-fetched-followup-48f8-deploy` / `4fdcb50c7c2c4006b350a4d6ebbd25a0` 实际combatEnded=true、UnexpectedReplans:0，预测11/1药/T6/14786总展开。Release和PowerShell结构门禁通过。
- 女王230888新增基线：材料预检通过；原根:1当前30战损/0药/T9/5453展开，continuation通过、旧native-state编码不可比，不以报告37→11直接宣称新改动收益。玩家:3短前缀后续验证另记。

- 失败实验已撤回：手牌可支付致命按同一FirstAttackDamage未来收益替代固定8点，`ACT3-SUBJECT-BUFFER-PATH` / `d8e8f11ae39d448890cc3ed8d6e4e6e1` 路径合同Passed，但质量35战损/0药/T6/11835总展开，较14战损/1药候选退步。仅手牌潜力增加不能证明能力投资的完整路线质量；恢复dcd3864生产行为，不重复已通过同产物验证。

- 首张攻击候选验收：`ACT3-BOSS-STRATEGY` / `c766b94275aa463c86f4fbc54ff28c5b` Passed，新增无攻击致命零收益、可支付根除多段估值及既有必要防御/斩杀/运转/增量合同；`act3-first-attack-48f8-deploy` / `cf45784a25264d8d93685f07ce0c2328` deployment_completed，原根continuation/native-state均通过，预测14战损/1药/T8/14612总展开，实际combatEnded=true、UnexpectedReplans:0。14是solverMetrics预测数，未另建实战HP流水核算。PowerShell结构门禁和Release构建通过，原始玩家零损尚未达到，三类首领整体优化目标仍未完成。

- 首张攻击候选 `ACT3-SUBJECT-BUFFER-PATH` / `0ddca32df475422e9b2532b7c91c2fba` Passed：14战损/1药（T2）/T8/14612总展开，同政策/8k单搜索/20秒请求配置；与有限免伤基线26及旧预测基线20比较，目标改善，尚未原生部署。`act3-first-attack-f25c` 保留包22战损/0药/T13/13388总展开，仍获胜；不将节点上限边界视为未获胜，终局enemyHP=0。

- 有限免伤预测 `ACT3-SUBJECT-BUFFER-PATH` / `25751afb185f4a639ee1325cbe3775d7` Passed：`[1]`、`[40]`、`[40,40]`、`[1,40,40]` 预测与MonsterMoveSemantics.DamagePlayer完整模拟HP一致，预测源分支不变；玩家五步终点、root/live不变及756条事件无丢失通过。此前`ba91283`/`86059c8`/`5c70c72`失败来自测试直接调用底层Damage，绕过已死亡奥斯提处理；逐击证据确认免伤已减为0，不能报告模拟无限免伤。当前整场搜索26战损/1药（T1）/T8/12896总展开，较此前20战损退步；正确威胁估计并不等于搜索质量通过，仍需组合策略优化及保留集/部署验证。Release构建与PowerShell结构门禁通过，未进行原生多段伤害实机差分。

- 溢出减伤候选 `ACT3-SUBJECT-BUFFER-PATH` / `3ab164a1aeed430ab81f2a6c8485d385` Passed（35.30秒），756事件无丢失、原根和玩家五步终点不变。等价第四步ProjectedPlayerHp=48（此前34），排名245/340、未保留；第二步原始动作次序失去保留，第四步由其他顺序生成。此候选尚缺多段有限免伤反例与最终质量/部署验收，不计为已修复。

- 第四步筛选池追踪 `0bde01649668489bb3c56dda86ecd639` Passed（35.95秒），807事件无丢失：许愿取致命状态原始排名304/359，容量60、必保49，未选中。此时实际HP48、有幸运补剂，筛选特征ProjectedPlayerHp=34、Energy=1、LatentSetupValue=6；需要核对威胁预测与待兑现能力的评价，尚不能认定Buffer预测有语义错误。Release编译与PowerShell结构门禁通过。

- 原生二进制先验证后允许旧零计数迁移：`REPLAY-BOUNDARY-CONTRACT` / `52eda9e4c7564049a48fc69755ea8c6e` Passed（22.38秒）；48f87 的 :23 原生7事件、continuation/native-state均通过，后缀搜索2649节点零损/T6，前缀已经使用1瓶幸运补剂。
- `ACT3-SUBJECT-BUFFER-PATH` / `b26204bf3a8c44f697f3305420ad42de` Passed（35.75秒）：原根双状态通过；五个玩家动作逐物理实例模拟匹配记录终点；331条路径事件无丢失，root/live不变。前三步完整生成并展开，第四步许愿取第二张致命进入PruneInput后没有PruneFinal，第五步未生成。仅证明首丢点，不是质量改善或实际部署。前两次测试构造分别填错选择SourceId、自用药目标描述，修正为实际计划格式后通过。

- 未执行事件的旧开局恢复：`act3-boss-48f8-unplayed-root-current` 的 :21/cursor0/turn1通过continuation和native-state；搜索20战损/1瓶LUCKY_TONIC（T2）/T6/12751总展开。记录中的玩家在T1使用同瓶药，随后两次致命及其他铺场，仍待完整前缀/后缀验证。`REPLAY-BOUNDARY-CONTRACT` / `eaf60cfd63154b658430ae3a0c8f8273` Passed（22.23秒），旧历史兼容与新增非零/重复/错位字段拒绝保持。

- 全局门槛删除实际部署 `act3-boss-cc8a-global-unlimited-investment-deploy` / `3565be06219e44a38006100861357a04` Passed：MinimizeHpLoss，预测54战损/0药/T6/6711展开，最终combatEnded=true、UnexpectedReplans:0。旧包起点continuation通过、native编码不可比较仍如实保留；该结果不声称已追平16战损见证。

- 全局删除门槛构建：ACT3-BOSS-STRATEGY `576fff1ea930482ab6d5c0c19a707736` Passed，必要防御/斩杀/能力联动/增量保持；SEARCH-HP-TARGET-STOP `1e12c90762e249629242078b05473313` Passed，零损、关闭、并行、3战损阈值、狩猎兑现、强制一药与保留另一药保持；UI-LOCALIZATION `38b0c5d056d44a81af10eb30c4870257` Passed，eng/zhs/zht及模板/状态/实体快照合同通过。两次启动器进程路径读取失败各重试成功。PowerShell结构门禁通过（87个Search文件），Bash未执行；最终Release编译零警告/错误。

- 全遭遇卖血门槛删除：源码已移除SoldHpThreshold解析、普通/精英/首领常量、累计开战HP裁剪及超门槛候选配额，编译通过。初次ACT3-BOSS-STRATEGY启动器未取得进程路径，未执行测试；重试结果另记。行为与UI合同待下列本次结果，不引用历史通过作为本次完成。

- `ACT3-HOURGLASS-POLICY-AB` / `e67b47933aee4877a678c3373db810c7` Passed（40.51秒）：同进程、同一原根依次ProgressionFirst→MinimizeHpLoss→ProgressionFirst，前后均54战损/6756展开，中间死亡/6801；三次完整路线见证、根/live不变和无诊断丢失均通过。第三步原始排名均104，政策差异出现在后续，不能归因首层能力生成或进程随机性。
- 新定位：BossSoldHpThreshold=15，与最终首领RunEnding的生存上限不同；超阈值路径进入收益证明/小型延迟投资集合。仅三层特化改为原生存上限候选后，`act3-boss-cc8a-minhp-survival-investment` 为54战损/0药/T6/6711展开，仍使用MinimizeHpLoss。保牌保钱保留原门槛逻辑，实际部署和跨样本另验。

- 普通推进复核失败 `act3-boss-cc8a-minhp-ordinary-advance-deploy`：实际搜索为死亡/敌264/6532展开，执行到T7后玩家死亡，断言“战斗结束，但仍存在未死亡敌人”；UnexpectedReplans:0。汇总的restore_mismatch不是准确根因，continuation已通过。此前SearchOnly的52战损没有在部署入口复现，普通墙钟切层不能视为稳定修复，本次Phases改动已撤回。

- 调度隔离 `act3-boss-cc8a-minhp-ordinary-slice`：保留能力估值、恢复普通时间切层，52战损/0药/T6/6733展开；同MinimizeHpLoss原特化死亡/敌252，关闭特化普通60战损/T7。去除准备时间仍死亡/敌342，故该退步早于准备时间改动。普通基线首回合在深度7/399展开因时间切层，节点特化前两层走了不同深度；各自固定同20秒/8000主搜profile，并非相同实际转移量。
- 调度回退保留集 `act3-boss-ordinary-advance-f25f`：领域女王10战损/0药/T15/8000，保持原收益；continuation/native-state均通过。

- 准备时间独立哨兵 `act3-boss-vigor-cd79-holdout`：女王22战损/0药/T8/11817总展开，与原节点分层构建相同，continuation/native-state均通过。总展开含原智能用药审计，不能称为单次主搜索8000节点的硬总上限。

- 准备时间候选实际部署 `act3-boss-highgap-cc8a-vigor-deploy` / `5b05d804b8dc4675bdb04ad6fd99a52e` Passed：最终combatEnded=true、UnexpectedReplans:0，选择路线预测54战损/0药/T6。该输出的solverMetrics仍为预测值，未把它冒充独立实际HP账本；旧包起点native编码不可比较仍单列。候选尚需独立保留集。

- 准备时间估值候选 `ACT3-BOSS-STRATEGY` / `d3b6b6837cf0455a8369dc5580ddedd7` Passed（28.10秒）：空牌组无攻击兑现时DamagePotential为0，有后续攻击时计入多回合收益，关闭特化保持原向量；同时通过既有必要防御、升级攻击斩杀、点烧运转及增量回放合同。整场质量与部署另验。

- 沙漏完整低损见证 `a610aab495e7494fa41295b7caee5ab9` Passed（29.81秒）：原比较根的4步玩家前缀与保存的30步求解后缀均经模拟回放，最终48HP/敌方0，对应16战损；34步没有预测风险或边界，根保持不变。保存后缀不是玩家完整实测战斗；旧包native-state编码不可比较的限制仍存在。当前真实搜索只找到54战损，不把见证注入搜索或当成新算法结果。

- `ACT3-HOURGLASS-OPENING-PATH` / `52f88748c1554745b548c35cc77b3fd1` Passed（30.47秒）：从 cc8a 原比较根 :1 按物理实例回放准备时间、WELL_LAID_PLANS、防御及回合结束，模拟 continuation 匹配记录 :3；268条诊断事件无丢失且根不变。第二步原始排名63/96、有效容量60、49个必保，未被选择。旧包 native-state 编码不可比较，此合同不声称全字段原生等价或部署通过。
- cc8a 当前同配置搜索：原根 :1 死亡/敌270/0药/6997展开；玩家5事件后 :3 为28战损/0药/T6/5573展开。两者是不同根的缺口定位，不能称为新算法改善；报告16战损尚未追平。
- `ACT3-HELLRAISER-PATH` 已完成原包20步后缀的模拟胜利见证（此前 runId `d9f8fb2c347b4636977337a906e304a3`），最终46HP；只用于诊断，相关回能储备及组合保路生产实验均已撤回，43→35小缺口暂停。

- 最终构建策略合同：遗物 `95582a1a4a174b22998adb4ec989e09f` Passed（25.30秒），含10类根读取、冻结/Fork/持久化、总/独立开关、零损达标及早停2/62节点；成长 `ccac8dad93b44640927d2cde58d4bf39` Passed（26.58秒），含原生回放、手动历史、跨回合/Fork、至亮之焰硬上限搜索和禁忌魔典额度。
- 最终节点分层构建 `9B3D4317`：Release 0警告/错误，PowerShell边界门禁通过（87个Search文件），`git diff --check`通过；Bash门禁未执行。达标早停合同 `c0f4aec5b68b4b28b942f72bd441f49b` Passed（23.58秒）：零损/关闭/并行、3战损阈值、成长兑现、强制一药并保留另一药均通过；这些时间不是性能基准。
- 新沙漏最终实际部署 `act3-boss-fresh-9e87-deploy` Passed：Instant/0间隔、45→11 HP、实际掉血34（含自损3）、无回血、零药、敌方0、存活、unexpectedReplans=0。顶层combatEnded=true，账本在战斗结束事件前捕获的combatEnded=false不覆盖顶层完整部署结论。相同搜索结果为34战损/T9，搜索与执行吻合。
- 实验体0530节点分层 `act3-boss-node-layer-0530` 仍未完成：13HP/敌282/0药/8000，onlyDeathRoutes=true。与旧诊断结果一致不等于已找到生还解；此包保留为后续优化目标，不声称本轮解决。
- 新沙漏9e871681同可操作根 :1 严格双状态校验：`act3-boss-fresh-9e87-play-root-ordinary` 为37战损/2药/T10/15657总展开；`act3-boss-fresh-9e87-play-root-strategy` 为34战损/0药/T9/4686总展开。相同8000节点/20秒profile与原ProgressionFirst政策，普通版总展开含既有补充搜索；特化版合法零药胜利按原政策早停，不把展开数差当作固定工作量提速。首次start恢复被LETTER_OPENER记录3/恢复0拦截，改用现存的发牌后、无手动出牌检查点，并未放宽任何对账。特化第一次启动器读取进程路径失败，重试才取得上述正式结果。
- `act3-boss-node-layer-f25f` 领域女王保持10战损/0药/T15/8000展开。`act3-boss-node-layer-e724` 为70HP/敌394/3药未完成；同构建关闭特化的 `act3-boss-final-e724-baseline-recheck` 也未完成（38HP/敌363/3药/8000），旧基线68战损完整胜利本轮未复现。该样本不能宣布稳定改善，也不能单凭旧数字判定当前新调度导致退步。
- 最终构建复核暴露局部时限影响：`act3-boss-final-e435-search` 与 `act3-boss-final-e435-repro` 均8000节点/两指定药，但分别10HP/敌227与66HP/敌382，均未完成；旧 `act3-boss-interactions-bounded-pagestorm` 的5战损胜利不能作为稳定结果。日志首次时间切层分别在第11步/804展开，旧胜利在第12步/864展开，约2493ms局部时限；第二回合也受时间切片影响。相同总节点不等于相同实际分层，不能直接归因新增领域估值（该根不含领域）。
- 单因素 `act3-boss-node-layer-e435`：三层首领按原每层节点份额推进，保留全局20秒/8000节点上限；23战损/两指定药/T10/8000展开完整获胜。首次启动器无法取得进程路径，未执行；重试取得正式结果。该候选仍需最终部署及独立样本，暂不发布。
- 女王cd79保留集 `act3-boss-final-queen-cd79-holdout`（仅联动）和 `act3-boss-node-layer-cd79`（叠加节点分层）均22战损/0药/T8/11817总展开，和普通基线一致。总展开含原有补充搜索，不冒称全请求硬8000。
- 实验体f25最后实际部署开启/关闭特化分别为24/20实际战损，均0药、完整获胜、unexpectedReplans=0（`act3-boss-final-subject-f25-deploy` / `act3-boss-final-subject-f25-baseline`）。根不含本轮三种能力；部署的最终solverMetrics可能为部分或后续结果，不与旧SearchOnly的38战损混比，也不计优化。
- 0530 后续两项单因素仍未通过整场验收：`act3-boss-subject-0530-future-resources` 为死亡/敌365/7020展开；`act3-boss-subject-0530-resource-axis` 为死亡/敌310/7257展开。均0药，未找到完整胜利；FutureResource首领第一场加分和被上限遮蔽资源补分都已撤回，Core/BeamRetention/Phases/Parallel/RunContext恢复原实现。保留有完整执行证据的三种能力联动估值，0530明确未解决。
- 0530人工操作后真实T4 :5的普通搜索 `act3-boss-subject-0530-after-player-baseline` 同8000节点仍未完成（48HP/敌方179/0药）。因此不能假定只找回T3前缀就在该缩小预算下必然获胜；原报告17→1属于更大原设置，本轮不把其预测当作已验证整场收益。
- 双首领第一场整体启用旧首领比较规则的实验未通过：0530仅到5HP/敌方311/8000未完成（`act3-boss-subject-0530-boss-scope`），f25由普通38/0药退步为51/1药/T14（`act3-boss-subject-f25-boss-scope`，12801展开）。整体切换已撤回。正在单独验证把真实FutureResourceValue纳入三层首领第一场比较，原持续增益上限、弱化/力量权重及路由份额保持。
- 0530 玩家17事件前缀原生回放通过。第一版诊断默认取SOUL#0，选成不同升级牌，终点对账失败（`1db2e23da79e4157a3afc22633d7099d`），其首丢点不能当作玩家路径证据。改按原生卡牌编号14/15/38/18/12绑定后，`9a3685ba43ef4b5ab784f940e3360269` Passed：模拟终点与录制原生终点完全匹配、2070条脱离对象的观察、无丢失、原根不变。真实第四步领域进入GlobalRetention但raw rank168、selected缺失；该双首领第一场的HpRelief=None使旧搜索走普通战斗比较。当前将三层首领的搜索分类与战后HP结算分离，不改HP结算或玩家预算，整场A/B待验。
- 排队窗口修正后 0530d728 的原比较点 :3（cursor10、T3）恢复通过 continuation/native-state。普通基线 `act3-boss-subject-0530-baseline` 同8000节点/20秒 profile 为仅死亡路线（0 HP/敌方314/0药/7359展开）；这是本轮实测基线，不能把原报告另一预算的17当本轮基线。新的策略 A/B 与人工17事件终点另验。
- 新实验体 0530d728 的真实比较点T3 :3（cursor10）第一次恢复在事件7停滞：该输入录制于动作6期间，回放时动作6已完成。回放器现监听成功完成的原生动作，仅在对应父动作完成且执行器空闲时允许补送排队输入，继续逐事件payload与最终连续/原生状态校验；边界合同 `464aea17d06c4153ad86dcd06e3457ae` Passed（22.29秒，D450BEA6），包括忙碌/未完成父动作拒绝及原始异常保留。真实包恢复重试另验。
- 新女王 f25f888 实际部署 `act3-boss-queen-demesne-deploy` Passed：Instant/0 间隔执行，10实际战损、0药、完整获胜，unexpectedReplans=0。预测即时结束HP47，实际账本初始57、掉血10、另有回血1、结束48；如实区分即时预测HP与战后账本，未把这1回血记作新增搜索减损。
- 新女王 f25f888 / 领域对照：`act3-boss-queen-demesne-baseline` 严格开战根为18战损/0药/T16/7284展开，`act3-boss-queen-demesne-strategy` 同8000节点/20秒配置为10战损/0药/T15/8000展开，均完整胜利。这里改善8HP；不拿原报告另一时间点/预算的48→0相减。领域的未来能量需求、免费牌组/空牌组反例和既有合同 `59ed1b8e722e4ff8a7ede58557addf6d` Passed（27.24秒，E07535DD）；实际部署另测。
- 仅补一个零资源无新增自损的腾手牌攻击，cd79 / `act3-boss-handspace-queen-holdout-minloss` 仍为45战损/0药/T13/15687展开，劣于普通22/0。该方向撤回，Expansion 完全恢复原分配；不能用组件通过声称真实质量改善。
- 饱和选择时补齐全部普通动作的实验：最小合同 `1249124ab06b4a21bfd7dc2072b02749` Passed（27.37秒，512435B9），但 `act3-boss-saturation-queen-holdout-minloss` 为58战损/0药/T6/11295展开，相比普通22/0仍退步。该广泛补齐已撤回；正在验证仅补一个实际零资源、无新增自损、腾手牌攻击的前置出口，原有选牌候选和排序保持。
- e435 完整部署 `act3-boss-interactions-deploy-pagestorm` Passed：原生恢复后 Instant/0 间隔执行，实际初始66→结束61 HP、掉血5（selfDamage5）、两瓶指定药、敌方0 HP，完整存活获胜，unexpectedReplans=0；与8000节点预测5战损/T10一致。此证据对应仅保留 Pagestorm/Danse 联动，无 Fasten 或通用调度实验。
- Fasten 单因素真实 e724 / `act3-boss-fasten-e724` 未通过：8 已发生战损/68 HP/敌方381/3药/8000 展开，未完整获胜；普通为68战损/3药/T12完整胜利。组件合同通过不能替代整场质量；新增 Fasten 触发价值已撤回，保留有完整胜利证据的 Pagestorm/Danse 实验。
- 新增 Fasten 防御标签联动合同 `ACT3-BOSS-STRATEGY` / `45898817965542d8b0072c74d67ffc1e` Passed（26.53 秒，F238F72A）：非 Defend 标签的格挡牌不计额外收益，真正 Defend 牌计入可达收益，特化关闭时旧向量保持；现有 Pagestorm/Danse 正反例与实际 Solve 反例保持。真实 e724 整场对照另测。
- 联动触发上限修正后的 e435 / `act3-boss-interactions-bounded-pagestorm`：严格恢复同根、同 8000 节点/20 秒 profile/两瓶指定药，5 战损、61 HP、T10 完整获胜、8000 展开。普通基线为未完成（47 HP/敌方 290 HP/同两药同 8000），这是完整胜利改善；不把普通中途 19 战损拿来算“减损 14”。前一版触发估值只有 66 HP/敌方172未完成。新版本修正一次性牌重复估计及跨回合能量合并问题；`ACT3-BOSS-STRATEGY` / `d03ee5c170e54ba4856959ecaf5c21b0` Passed（24.98 秒，E03EADD9）。当前新增 Fasten 另行验证，不与本条混记。
- 普通调度上的联动估值最小合同 `ACT3-BOSS-STRATEGY` / `181c98dfcf6b433f92dd818030dc3f87` Passed（25.77 秒，artifact C72ECBA4）：通过实际 Replay 根快照检查 Pagestorm 抽堆虚无正例、无源/手牌已抽到虚无反例、Danse 实际两费正例及免费攻击反例，关闭 Act3 后向量保持；实际 Solve 点烧保留抽牌、必要防御、直接斩杀及增量回放保持。仅组件/合同通过，整场质量另验；Release 0 警告/错误。
- 同谱系两步保留实验 `act3-boss-lineage-queen-holdout-minloss` 仍退步：cd79 为 57 战损/0 药/T9/12746 展开，普通为 22/0 药/T8。该调度方案同样不验收，Phases 已恢复原普通搜索流程；下一步转向有实际触发机制依据的首领联动估值。
- 沙漏 948 / `act3-boss-hourglass-948-baseline` 开战恢复失败。完整字段归一后真实差异为 `mirrorRelics=KUSARIGAMA/1` 对 `/0`，不是允许迁移的 Y3→Y4；原错误仅显示最早的原始文本差异。其人工 before :1 与 start :0 同 cursor 但遗物启动时点不同，不能替代。e724 的成功 :0 开战基线也不等于人工 before :1，只用于同开战根的搜索 A/B。
- 新独立沙漏 e7246c / `act3-boss-hourglass-e724-baseline`：开战连续状态与原生状态均严格通过，普通搜索 68 战损/3 药/T12/8000 展开，完整获胜。玩家前缀为指定敏捷药后连续两张 FASTEN，前缀后旧字段终点尚未严格验证，不把报告预测 35→0 当成本轮同预算收益。
- 同程序关闭特化复核 cd79：`act3-boss-queen-holdout-minloss-baseline-recheck` 完整复现 22 战损/0 药/T8/11817 展开，排除旧构建/配置解释。当前准入与混层连续展开的质量退步成立，接入方式需要重做。
- cd79 MinimizeHpLoss 反向单因素 `act3-boss-continuations-only-queen-holdout-minloss`：关闭动作代表准入、仅连续展开，76 战损/1 药/T12/14373 展开，明显劣于先前普通 22。两项单独均未通过该保留集，继续用同一程序关闭全部特化复核基线；当前实验不得提交为已完成优化。
- cd79 MinimizeHpLoss 单因素 `act3-boss-admission-only-queen-holdout-minloss`：仅动作代表准入、关闭连续展开，未找到胜利，0 HP/敌方 328 HP/0 药/总展开 13425、onlyDeathRoutes。该准入方案不满足保留集验收；正在反向关闭准入、单测连续展开。临时编译开关不属于最终实现。
- 当前女王保留集 cd79 最小战损对照明显退步：`act3-boss-no-openings-queen-holdout-minloss` 为 70 战损/1 药/T11/总展开 11928，普通为 22/0 药/T8/11817。均胜利但质量不合格，当前候选不能标记验收通过；继续拆分动作准入与组合展开的影响。
- 当前女王保留集 cd79 / `act3-boss-no-openings-queen-holdout` 为 39 战损/0 药/T9/总展开 5056，原 ProgressionFirst 基线为 37/1 药/T10。均完整胜利，省一药但多损 2 HP，不记战损改善；MinimizeHpLoss 配置另行对比。
- 沙漏独立样本 504d061 的真实比较点 T2 / `b2d807d3b8b44ef892acb44eafb68c83:4` 恢复失败：旧记录 `Y=0/0/1`、当前 `Y=0/0/1/1`，事件游标 27。尚未搜索，不计质量结论；现有仅开战边界格式转换不能用于该中途检查点。
- 清理后正式求解夹具 `ACT3-BOSS-STRATEGY` / `74173e65557448b8a08543a5fde21649` Passed（24.95 秒）：真实 Solve 点烧保留抽牌链、禁抽时直接斩杀、饱和选牌仍准入攻击与 REFLEX 选择、第二回合组合展开、升级攻击斩杀及必要防御，启用增量等价核验。Release 构建 0 警告/错误；PowerShell 架构门禁通过（88 Search 文件），Bash 缺少 rg 未运行。
- 当前永世沙漏 e435 / `act3-boss-no-openings-pagestorm` 仍未完成：66 HP、敌方 365 HP、2 瓶指定药、8000 展开。不能把当前已损血 0 记为整场零损，不作为质量改善证据。
- 删除额外开局注入后的同条件完整对照：实验体 f25 第三回合 `act3-boss-subject-f25-turn3-no-openings` 为 18 战损/0 药/T14/总展开 13186，普通为 38/0/T14/13586；女王 `act3-boss-no-openings-queen` 为 48 战损/0 药/T8/总展开 12065，普通为 70/0/9482。均完整获胜；配置节点/时间预算相同，实际总工作量不同。旧前缀组件及其独立夹具已移除，重写为实际 Solve 行为验证，编译通过、该夹具待运行。
- 恢复原 routing 溢出后 f25 仍为 51 战损/1 药（`act3-boss-subject-f25-turn3-routing-preserved`，12538 总展开），因此不能把全部退步归因于选择截断。当前关闭额外开局注入做单因素对照；`ACT3-BOSS-STRATEGY` / `5d41ed360ee34cd399567a9759529169` Passed（25.16 秒，生成器部分仅组件测试），`SEARCH-HP-TARGET-STOP` / `06b558d62e3440f197c2c2ee43eb1268` Passed（7.53 秒，zero_nodes=1、强制一药/成长目标保持）。同根完整对照待完成。
- f25 第三回合正确起点完整对照：`act3-boss-subject-f25-turn3-baseline` 为 38 战损/0 药/T14/总展开 13586；`act3-boss-subject-f25-turn3-strategy` 为 51 战损/1 药/T13/总展开 12609，均获胜但策略明显退步。撤回首领 routing 选择的额外硬截断，恢复旧溢出合同，保留卡牌/目标代表准入；这是待验证修正，不提前归因全部退步。
- 实验体 `f25c4872be5945269e8a6d38a9ab1286` 人工真实比较为第三回合 `:5`（cursor 28）到 `:7`（cursor 38）。`act3-boss-subject-f25-manual-prefix` 完整执行 38 个原生事件，continuation/native-state 均通过，状态为 recorded_prefix_verified；原报告 51→4 是该 T3 前缀前后预测，不能拿开战起点直接比较。当前从 :5 开始做同配置 A/B。
- 当前女王 `60a3a1bcb15948f5ad699330e02b149a` / `act3-boss-action-admission-queen`：50 战损、0 药、T10 完整获胜、总展开 12135，相同配置普通基线 70/0 药；低于基线 20 HP，但未保住早期组合原型的 42 HP。最新结论使用 50，不沿用旧 42。实验体 `act3-boss-free-preparation-subject` 仍未完成（11 HP/敌方 298 HP/0 药，总展开 12218），不记改善。
- 免费前置增加生命/最大生命/星能约束后，`ACT3-BOSS-STRATEGY` / `2b75e89cd680486aa200c69354ff34ca` Passed（25.11 秒），输入含零费 HEMOKINESIS 的真实自损反例及起手易伤。前一次 `b3a08ee2bf7c448db70bddffae067041` 失败于过度指定 BACKSTAB 顺序：加入的 ASSASSINATE 本身不自损且能施加易伤，无初始易伤时另一顺序有实际收益；修正了测试输入与归因，未把失败写成语义修复。真实 dc2 前缀是否因此改善仍待测试。
- 动作分配真实对照：`act3-boss-action-admission-subject`（dc2，新进程）仍无胜局，15 HP/敌方 311 HP/0 药/总展开 13179，onlyDeathRoutes；日志已出现 hand-space-resource，但为 BACKSTAB/ASSASSINATE/BACKFLIP、开局损 2 HP，不是人工前缀。`act3-boss-action-admission-pagestorm`（e435）同 8000 节点/两瓶指定药，66 HP/敌方 95 HP，相比普通 47 HP/敌方 290 HP 是中途进展；双方都未完成，不能记整场零损获胜。
- `InitialPolicy.HpLost` 来自所选路线首回合的 `HpLostByTurn`，不是输入账本；此前怀疑合成测试污染没有依据。新旧 dc2 日志的 `battle_hp_lost_so_far=0`，如实保留失败质量记录。
- 单父动作诊断 `ACT3-OPENING-EFFECTS` / `b5c1e8ffabb640519a0e39826eeca6cd` Passed（24.61 秒，dc2 严格恢复）：返回 8 个 DAGGER_THROW 与 24 个 PREPARED 变体，没有 BACKSTAB，证实卡牌候选名额先被路由选择填满。此诊断只展开一层，不作整场搜索质量或性能声明。
- 修正首领动作分配后，加入 PREPARED 升级使选择饱和的 `ACT3-BOSS-STRATEGY` / `58e3bb9f953445e181a974239531db09` Passed（24.71 秒）。对应政策合同再次通过：早停 `fe883c046d744097bb420db9c8eb7d47`（7.57 秒）、遗物 `3463af16a49e45359aceedf58fd8a336`（9.25 秒）、古代成长 `3d4b0bbb8e904243a217028c8ec6c456`（10.62 秒）；最后一项 ExitOnComplete 后新开进程执行玩家包，避免合成状态影响真实样本。
- 腾手牌前置的真实实验体 `act3-boss-handspace-subject`：continuation/native-state 通过，但仍无胜局，`onlyDeathRoutes=true`、最终快照 23 HP/敌方 312 HP/0 药，总展开 13791；较前一原型退步。当前日志仅生成原 setup/resource/attack 三根，没有 hand-space-resource，最小 fixture 通过不能代替真实包生成证据；需定位资格条件，不作修复声明。
- 腾手牌资源前置 `ACT3-BOSS-STRATEGY` / `09009703fca64109ab3facd7a4df3d1c` Passed（24.92 秒）：满手场景实际生成两张 BACKSTAB 后 DAGGER_THROW 弃 REFLEX 的三动作前缀，回放后手牌 10/能量 2/敌方耐久下降，原 28 父节点初始化额度保持；原增量与反例保持。首版测试误读 SelectedSearchPlan 的内部节点接口导致编译失败，改为回放后数值检查；最终 Release 0 警告/错误。
- 显式启用首领策略的既有政策合同：`SEARCH-HP-TARGET-STOP` / `c7ef5ffe8fe34d8490b96fe8a76c6f5b` Passed（7.57 秒），`RELIC-COUNTER-POLICY` / `b11903328ab04dd5a40ed9ab29132892` Passed（9.27 秒），`GROWTH-ANCIENT-POLICY` / `0ebd80eb079a4613bcc060455ecdf0cb` Passed（10.67 秒）。覆盖零损/阈值早停、成长目标、指定一药且保留备用药、DOP2、遗物未达标、至亮之焰硬上限及禁忌魔典收益；保持原短搜索预算。
- 永世沙漏 `e4350ee2defb4f5db42863cebf345cf8` 的 `act3-boss-pagestorm-bounded-strategy`：continuation/native-state 通过，与普通基线均总展开 8000、2 药，仍未完成；当前 57 HP/敌方 354 HP，普通 47 HP/敌方 290 HP。双方均未完整获胜，不把中途少掉 10 HP 计作优化。
- 永世沙漏 `e4350ee2defb4f5db42863cebf345cf8` 四项 Y 迁移后 `act3-boss-pagestorm-restored-baseline`：continuation/native-state 均通过。第一次请求启动器读取进程路径失败，未执行测试；重试成功。普通搜索总展开 8000、2 药、47 HP、敌方 290 HP，未完成战斗，19 已发生战损不当作整场质量。
- 女王保留样本 `cd79a941fac648708b36eff4d0283e3a` 的 MinimizeHpLoss 对照：普通 `act3-boss-queen-holdout-minloss-baseline` 为 22 战损/0 药/T8；修正预算边界后的 `act3-boss-queen-holdout-minloss-bounded` 为 21 战损/1 药/T8、总展开 13264。均完整胜利；只减 1 HP 却增加用药，不作整体改善声明。
- 补齐组合展开的回合时间边界、内存准入后取消/预算复查及异常子快照释放后，`ACT3-BOSS-STRATEGY` / `cdcdac418518493f9e15d55c08559341` Passed（9.16 秒，复用进程）。原作用范围、点烧资源/进攻出口、第二回合组合、必要防御和增量回放保持；整场质量另测。Release 构建 0 警告/错误，PowerShell 架构门禁通过（89 Search 文件）；Bash 门禁未执行到检查，当前环境缺少 rg。
- 四项旧历史迁移 `REPLAY-BOUNDARY-CONTRACT` / `2de5925c0a9a4e66a0071c99feabdfce` Passed（22.30 秒）：显式 combat-start 的四项 Y 与三项 Y 均允许唯一新增零值 FlameHp；非开战边界、非零、重复、错位和其他字段差异仍拒绝。该测试不代表玩家包的 native-state 已通过。
- 女王保留样本 `cd79a941fac648708b36eff4d0283e3a` 切换 `finalBossHpStrategy=MinimizeHpLoss` 的普通基线 `act3-boss-queen-holdout-minloss-baseline`：22 战损、0 药、T8 完整胜利、总展开 11817。与 ProgressionFirst 结果分开比较。
- 严格恢复女王保留样本 `cd79a941fac648708b36eff4d0283e3a`：普通 `act3-boss-queen-holdout-baseline` 完整胜利、37 战损、1 药、T10；组合展开 `act3-boss-queen-holdout-continuations` 完整胜利、79 战损、0 药、T13。日志明确 `SMART_POTION_GRADIENT stop=no_potion_acceptable maximum=0`；沿用包内 `finalBossHpStrategy=ProgressionFirst`、RunEnding 的旧政策，无药获胜后不为战损加药。不能记为战损改善，也不能将该取舍误判成跳过用药审计；最小战损设置另测。
- 组合展开保留样本 `127b09470a234e8abfba87e0ac2e13d2` / `act3-boss-continuations-hourglass-holdout`：同配置仍完整获胜、0 药，但战损从普通基线 96 增至 98，属于退步；当前最终 HP 2、敌方 HP 0、总展开 4645。该旧包仅 continuation 对账通过，native-state 因缺少旧模型编号映射不可比较，不作为严格恢复质量证明。
- 永世沙漏 `e4350ee2defb4f5db42863cebf345cf8` / `act3-boss-pagestorm-baseline`：恢复失败，`field_order[16] expected O / actual FlameHp=0`，尚未进入搜索，未产生优化结论。
- 点烧候选阶段 `ACT3-BOSS-STRATEGY` / `4d33927f420d416b82a792cfeb6c48b3` Passed（24.08 秒）：作用范围、点烧保留过牌、禁止抽牌时不虚构资源收益但保留进攻出口、升级攻击斩杀、必要防御与增量回放。该证据早于多策略初始化接线，不能代替最终策略或整场战损验证。
- 同搜初始化接线后 `ACT3-BOSS-STRATEGY` / `ff70777e9b7b45eea233161be722b847` Passed（23.89 秒）：新增实际升级与直接攻击分别生成前缀、探针展开量上限；原点烧/防御/斩杀/增量合同保持。尚需覆盖同预算首领 A/B、多前缀后续保留、智能/强制用药、成长/遗物早停以及部署复用。
- 固定前缀分类修正后 `ACT3-BOSS-STRATEGY` / `fd89ce7e901d48ef9ae830176bc59ee7` Passed（24.07 秒）：固定前缀与真实普通展开的分类一致，原最小合同保持。初次 `7fd2c9fa` 因测试把力量增益预设为 Scaling 而失败，改为对比普通展开的实际分类；未改变分类定义。女王 `act3-boss-prefix-traits-queen` 仍为 78 战损、0 药、T11 胜利，不作质量改善声明。
- 组合展开 `ACT3-BOSS-STRATEGY` / `68b8d687dae543cf9e2bebce573f41e0` Passed（24.81 秒）：在第二回合观察到真实额外展开、未超过原节点上限，既有增量、必要防御和早停合同保持。`act3-boss-continuations-queen` 完整胜利、42 战损、0 药、T9，优于普通基线 70；同配置但总展开分别 12478/9482（含原补充审计），不声称实际工作量相同。高收益实验体 dc2 仍未完成（38 HP、敌方253、0药），不作整场质量结论。
- 女王 `60a3a1bc` / `act3-boss-shared-queen`：同 profile 下完整胜利但 78 战损，劣于普通基线 70；双方 0 药。共享起点实现未通过质量验收，不作改善声明。该请求总展开 11489、基线 9482（原协调器补充搜索另外累计）；不能把 profile 的 8000 当作整个请求的实测总展开。
- 女王 `act3-boss-milestone-queen`：启动/资源前缀停止追加攻击的单因素试验仍为 78 战损、0 药，未证明改善，已撤回该试验。
- `ACT3-OPENING-TRACE` / `bf40ca89a359461c8cee612f8a059825`：416 个观察状态、未截断；启动与进攻前缀均进入第二回合，启动前缀第二回合有 32 次实际展开，不能声称它在首次裁剪中消失。该首轮诊断未启用正式 NoGC 条件，其 61 战损不作为质量收益；后续诊断已接入既有 GC 生命周期。
- `ACT3-OPENING-TRACE` / `3e52411aff5b4d35b0513576dfe2b450`：接入原 GC 条件、1248 个观察状态、未截断。两类精确前缀均存活至第 4 回合；启动前缀 T2/T3/T4 分别实际展开 32/47/28 次，进攻为 12/13/18 次。完整结果仍为 78 战损，未证明需要额外开局保留配额。该诊断只证明匹配前缀后继的存在，不证明等价换序路线均已覆盖。
- `REPLAY-BOUNDARY-CONTRACT` / `69c86f8863e14f288a01892668d1113f` Passed（22.38 秒）：新增零计数迁移仅接受显式战斗开始，非零、重复、错位及 HP/历史/RNG 差异拒绝；原异常身份保持。`dc2ffda6` 的 SearchOnly 原生连续状态与 native-state 均通过，普通基线 80 已发生战损、剩 304 敌方 HP、未完成战斗，不能把 80 当完整战损。
- `dc2ffda6` 的策略 SearchOnly 仍未完成（0 药、7 HP、敌方 243 HP），不作改善声明。ReplayRecorded 至 `67c1804ab82e478e8cb8c6fee9652da5:3` 已执行 12 个事件，在末端 continuation 被拒绝；比较已记录字段仅有 `Y=1/4/10` 对 `1/4/10/10` 和缺少新增 `FlameHp=0`，其他字段相同。未越过中途严格门禁，不能记为完整回放通过。
- 高收益旧包 `dc2ffda6`、`94867803`、`ac8078d3` 补齐显式早停政策后仍未严格恢复：旧 continuation 缺少 `FlameHp`，另有旧历史字段；后两份分别出现 KUSARIGAMA / BEATING_REMNANT 计数差异。未跳过对账，不记为搜索改善。

## 0.36.4：摘要标题

- 发布整合：客户端昵称字段与本次策略修正已共同通过 Release 编译；复用本轮已生成的 0.36.4 DLL/ZIP。昵称真实上传端到端未验证，策略与 UI 证据见下。

- 增量收益修正：`RELIC-PRIORITY-MEAT` / `66c61885394a41dd9bcff939af22e313` Passed（23.76 秒）。固定 14 HP 前缀代表已选策略成本，70 最大生命、50 起点下，再付 1 HP 得到 35+12=47；同前缀额外付 12/13 HP 时保留 36 HP。即使旧配置额度 1000 / 优先级 3，带骨肉只记实际回血抵扣。包含原遗物优先级互换哨兵及增量回放，未进行可见游戏验收。首次 `05c12654553e4688bf06ccc8251b5bf1` 回本断言未通过，因为怪物可提供更便宜的 4 HP 卖血路线；加入敌方中毒在行动前结束战斗后，隔离额外卖血边界通过。
- 历史 `55df7081fe40400ab62a297446082838` 验证的“超过根生命”口径已撤回，不能作为当前带骨肉行为依据。

- `RELIC-COUNTER-POLICY` / `573768e7b8044a96896187cdc15c6f15` Passed（25.27 秒）：原十种计数根/Fork/开关和增量回放保持，早停仍为 2 节点、关闭早停为 62 节点。

- `RELIC-PRIORITY-MEAT` / `088aaa72cd2f442cb6b578c24665d2ce` Passed（23.64 秒）：旧规则 Priority=1，互换铁棍/音叉优先级时零损路线相应改变，优先级不增加 HpAllowance；带骨肉主动目标选择半血净回血路线，正式搜索含增量回放。前两次 `7d1d7b28f9a64dd0957680f1d770cebb` / `79396d9a9aeb4500b7d25c43d92d37cb` 暴露旧单调回血排序只选 41 HP 不触发回血，补入已启用目标的实际回血差额后通过。
- `UI-LOCALIZATION` / `a905bc58e27d4aaf8ca05731d80884da` Passed（26.27 秒），实际 DECIMILLIPEDE_ELITE 三段名称可区分，普通/墨染小刀标题与提示可区分，eng/zhs/zht 和 409 项目录通过。默认实例两次未取得启动进程路径（4160/1672），未进入夹具，改用独立 `relic-priority-meat` 实例后正常运行。Windows 结构门禁通过；未进行可见游戏人工验收。

- 纯 UI 标题与容器调整，按 L0 执行 Release 编译；未启动可见游戏做人工排版验收。

## 0.36.3：策略摘要

- 后续摘要样式调整：`UI-LOCALIZATION` / `fc0bb52775dd427c80b61719838b1225` Passed（25.38 秒），校验目标 7/实际 4、成功与未达标状态分组、前缀删除及 405 项目录。右对齐、16 号字体和全自动按钮样式通过编译检查，未作可见游戏人工验收。

- `UI-LOCALIZATION` / `2549ed79f5824695b4e1e6bad238919e` Passed（25.82 秒）：404 项中英目录，已卡小花 2 与未达标笔尖 4、成长次数及不完整路线显示均通过。`RELIC-COUNTER-POLICY` / `e1dcbb80d7a84805984af0f18d6ff1f3` Passed（25.48 秒）：计数快照/Fork、实际终局和早停保持，达标 2 节点、关闭早停 62 节点。

- 在 UI-LOCALIZATION 中覆盖已卡/未达标并存、狩猎与狂宴次数、非终局不宣称已卡、无目标隐藏和 eng/zhs/zht 文案。可见游戏排版未人工验证。

## 0.36.2：重复回合请求与围巾调查

- `THIRD-PARTY-CALCULATED-FAILURE` / `63ec25652bca40888bec99e4506a5238` Passed（22.44 秒）：使用游戏提供的 MockTypes 映射注入第三方来源，实际经过未知 CalculatedVar 求值路径，断言来源异常、包装后的 UI 和报告账本均不要求上传；未执行 LifeMasterMod 原卡。Windows 结构门禁通过。

- `LAMP-INDIRECT-POISON`：`a41a3935eec14e0b8d1415009fd89071` Failed（22.24 秒），复现原版毒 2、预测毒 4 以及遗物已使用标记偏差；修复后 `f68fdc90c1874304b5e667b39291bea1` Passed（26.29 秒），Envenom/Concoct 两条附毒与随后直接 DeadlyPoison 均逐动作比较完整 ContinuationStamp。第一条启动请求因可见游戏仍在运行而入场排队超时，未进入 fixture；用户退出游戏后才运行，未修改准入规则。八个原包未做完整恢复/整场部署。

- `UI-LOCALIZATION` / `dca94aacb31c40ba9ac3b94f9375d9a1` Passed（25.34 秒），21 种遗物标注含“本张免费”在 eng/zhs/zht 正确转换，原胶囊与提示语言往返保持；未进行可见游戏人工验收。

- 免费标注与高费顺序规划：`BRILLIANT-SCARF-COST` / `6e12b03da68f44eb9287bc5d987f3997` Passed（24.80 秒），四张 DEFLECT 后第五张 3 费 BLUDGEON 在零能量下击杀，增量回放一致；仅第五张带 BRILLIANT_SCARF 免费标注，前四张无误标。

- `AUTO-TURN-REQUEST-OWNERSHIP`：失败基线 `0265a65eb55f432ebd3e2d75fe939489`（21.96 秒）在计划完成、能量改变后注入迟到自动请求，证明当前计划被替换。修复后 `954ad9fd0b244609a67105d121dfe624` Passed（23.28 秒），保持原计划与审计计数，允许显式手动重算。未执行两个原包的恢复或整场自动部署。
- `BRILLIANT-SCARF-COST` / `5f13290e92644ab68c6627281df50ab5` Passed（24.80 秒）：四张付费牌后第五张费用为零，实际手动出牌与预测完整状态一致；零能量四张偏折后免费打击首回合击杀，正式搜索含增量回放。最初夹具 `256965395b8d414d9d45f77296b18507` 因默认 1 HP 敌人死亡后索引已移出阵容而失败，增加敌方生命后修正；这不是围巾的失败基线。围巾用户现场仍未复现。

## 0.36.1：遗物计数

- 删除遗物早停说明行：纯展示删除，按 L0 执行 Release 编译，未重跑战斗。

- 遗物总开关默认值调整：仅修改设置数据的初始值，新配置及缺少该字段的配置默认开启，显式保存的关闭状态仍保留；单项默认值和归档快照默认值保持原行为。本次按 L0 选择 Release 编译，不重跑战斗。

- 本次 UI 文案：`UI-LOCALIZATION` / `9bd2e76b41bf446485b32dbc78f5fef8` Passed（25.59 秒），eng/zhs/zht、400 项目录与语言往返通过。

- 三策略面板排版：`RELIC-COUNTER-POLICY` / `eaba35f068ce47b09eca0b8c51990814` Passed（25.82 秒），覆盖原生遗物图标、独立开关、面板互斥/边界，并新增三个面板背景与文字调制均完全不透明、数值输入 16 号字断言；原计数与早停合同保持。未进行可见游戏人工排版验收。

- `SEARCH-HP-TARGET-STOP` / `fdc28b23b38741e3b87c2a143664b7fc` Passed，23.45 秒：零损、累计阈值、并行波次排空、成长存在/不存在、击杀成长兑现、强制一药与保留备用药、至少一药策略。第一次启动器在取得 PID 30748 的可执行路径前失败，尚未进入 fixture；新隔离实例 `relic-early-stop` 完成上述验证，未提高超时。

- `RELIC-COUNTER-POLICY` / `5d123007ff00488fa48ad0278513ccdf` Passed，25.36 秒：十项计数根捕获、Fork、live 后续改变隔离、总/单项开关、设置往返、UI 标题顺序/面板边界/互斥；带增量校验的免费及 3 HP 付费路线，原生末击计数一致。达标早停 2 节点，关闭后 62 节点。初次 `e46d16d197f9426abc54e583f6f2de50` 明确 Failed，暴露补充搜索的前缀父链丢失；修复后通过，未关闭验证。
- `UI-LOCALIZATION` / `d545270315244f54a07f1964f5bb4d74` Passed，25.62 秒：eng/zhs/zht、394 项文本目录、模板及原动态控件语言往返。没有可见人工排版验收。
- Windows 结构门禁通过；Bash 同步新增策略、早停、缓存与前缀所有权规则，未在 Linux 启动游戏。

## 0.36.0：统一搜索预算与进程诊断

### 26356 三层卡顿修复

后续 6020 录制修复：`DYNAMIC-VAR-METADATA` / `57b289699aa045f39cf330a4b157eee7` Passed（22.72 秒）。隔离游戏源补入玩家 BaseLib，验证真实 Clone 的 live 空登记基线、模拟连续 2000 次零空登记、自定义提示、升级数值、两代克隆及父子独立。首个 `18c136b84cbe4cd3b2bbbbdab8980335` 因隔离环境缺 BaseLib 明确 Failed，补齐依赖后运行。复跑命令使用 `-ScenarioId DYNAMIC-VAR-METADATA -Sts2GameRoot <含BaseLib的隔离游戏源> -HeadlessInstance dynamic-var-metadata -TimeoutSeconds 120`；不可把无 BaseLib 环境当该合同通过。独立 `GcPolicyChecks memory`、`checkpoint` 通过，自动模式要求后台请求并确认完成，原高碎片自动压缩已撤回。以下压缩验证为此前历史证据；本轮没有修复后三层可见对照。

- 录制升级独立目标运行 36 秒，测试周期 20 秒（10 秒句柄/10 秒普通段），watcher 三段收尾并压缩，目标 writer/drain/停顿心跳合同通过。第一段 17,338 次句柄创建、17,274 次销毁，EventsLost=0；第二段没有句柄事件，窗口隔离有效。外层临时 PowerShell 包装误把未设置的 LASTEXITCODE 当失败；collector-health 为 complete，目标 stdout 为 PASS，随后按实际产物解析验证，未重复录制。
- EventPipe 栈验证初版错误地要求独立 ClrStackWalk 非零，已修正为读取 ETLX 关联栈：第一段 32/17,338 条句柄创建有栈，540/540 条 GCTriggered 有栈，不能声称每个句柄都有栈。旧第 7 段 102/102 条 GCTriggered 有栈。新增 `trace-stacks` 输出触发时间/来源；句柄事件验证与栈关联验证分别执行。
- `PerformanceRecordingTests registry` 验证后台计数落入 timeline；`PROCESS-DIAGNOSTICS` / `8c08835ad3e64332b3085eb19de2b858` Passed，22.92 秒，真实 Godot 两个弱登记容器可在后台读取数量，原跨战斗摘要与读档心跳合同保持。没有新的玩家长局性能或完整 GC root 证据。

- `NODE-POOL-LIFETIME` / `374081022814401586f01f02b9e8db49` Passed，23.01 秒。实际调用 NCard/NGridCardHolder 的已打补丁泛型方法，各复用 200 次；验证出站、入站、子节点递归、离树目标保留以及包装登记无正增长（-2071 / 0，首项包含同期终结器清理，不能解释成精确释放数量）。没有用静态 helper 替代生产入口。
- `SEARCH-HP-TARGET-STOP` / `9f80fe8fcdba48e7b82aad50b49b965d` Passed，8.58 秒，零损/阈值/成长/药水早停合同保持。没有运行完整三层可见 A/B。
- `CombatSolver.GcPolicyChecks` 默认 20 项通过，覆盖刚回收后少量分配、整层超过区域容量、有效预测可回收、缺失预测、NoGC 丢失及累计指标；`memory` 通过玩家物理压力样本、碎片选择及真实一次压缩/常规收集。碎片选择使用玩家数值作为输入，实际收集发生在小测试进程，不据此推断大堆暂停收益。`scopes` 8 项通过。
- 独立 `checkpoint` 通过生产 1 GB NoGC 区域的建立、回收后续用、收集中取消、确认排空、恢复普通 GC 与完成计数。
- 完整 `GC-CHECKPOINT-BACKGROUND-V0111` / `084d4e27093a445c87e21cd788547d53` 在 120 秒超时，由启动器停止自己的 headless 进程；没有结果文件、最新日志停在创建战斗房间，不能认定夹具断言已经执行。没有增加超时或原样重跑，改用上述独立进程最小合同。完整夹具的延迟手动请求/跨引用释放 epoch 部分本轮未验证。

- SINGLE-SEARCH-PROFILE / `2fb11ffdc6c649bca3838cab87dffbf0` Passed，22.68 秒：旧 deep 自定义参数迁移、保存重载、四档预算、单搜索进度、请求工作累计与固定小预算。
- SEARCH-HP-TARGET-STOP / `8313b1d83703473bb5dc1c751bd2700b` Passed，7.75 秒：零损、阈值、并行、成长目标、必要药水和额外药水保留。
- THEFT-RECOVERY-POLICY / `88dca2a1337d47b3bfef439169922998` Passed，7.27 秒：策略合同与固定小预算搜索。没有据此宣称完整玩家战斗路线质量或内存收益。
- UI-LOCALIZATION / `b794090ac5584fa8a06ab8f18c31844e` Passed，9.78 秒：设置、动态状态和中英切换。PROCESS-DIAGNOSTICS / `75fe89832e6b472c88f82ebec29df2a4` Passed，22.33 秒：模拟 State 存在而 NetService 未就绪的读档窗口，调用真实心跳 Process；验证切换战斗后 GC 摘要仍在进程日志、高频显示采样未被复制。
- CheckpointTool self-test 31 项断言通过，包括新单配置政策比较、旧政策保留及不同代预算不冒充同一政策；Windows 结构门禁通过。Bash 入口同步了协议与结构约束，未在 Linux 实际启动游戏。
- 最新可见进程 27996 的最后一场 combat_ended 回收：managed live 2.157→2.057 GB、private 15.581→14.451 GB，working set 6.746→6.740 GB。原日志删除了前序战斗，不能从该样本证明长局卡顿根因；未开启新 trace 或可见性能测试。

## 0.35.5：偷窃策略

- 定位证据：本机进程 31712 的战斗日志 `combat-7165739b37ab40ecae1120a90a34198a.jsonl` 中 SEARCH_REQUEST 与最终结果均为 PreserveResources，最终零损、outstanding_stolen_resource=20；多次点击保策略也仍为该枚举，按钮没有接反。旧代码的审计合同明确要求先比较战损，已按用户新确认的保资源优先语义修正。
- `THEFT-RECOVERY-POLICY`：地精 `1934513228be4d6eb86333192f568332` Passed，23.08 秒；偷窃草蜢 `fa18ef06b5b340db9e565d79f44c02bc` Passed，7.34 秒。合同覆盖两药/15 战损追回优于零损丢失、放走反向选择、候选展示可接受追回带来的战损增加、失败不能优于胜利、未追回不得 HP 早停；分别跑两种策略的 256 节点/1500 ms 短搜。短搜不是原玩家整场回放，也不证明所有局面都能击杀逃跑怪。
- 普通早停哨兵 `SEARCH-HP-TARGET-STOP` / `3928bbf0e4df41b28700274cd04abaa6` Passed，7.75 秒，零损/阈值/成长与药水数量合同通过；同一 headless 进程复用，末次退出。Release 编译 0 警告 / 0 错误，结构门禁通过。

## 0.35.5：UI 操作区重排

- 搜索摘要取消世界线计数前的强制换行：仅改显示连接符，未改计数或搜索逻辑；本轮验证 Release 编译，未重跑游戏场景或实机视觉验收。

- 状态摘要字号调整：仅将五处字体统一为 16；Release 编译 0 警告 / 0 错误，差异检查通过。没有变更状态/事件逻辑，本轮未重跑游戏场景，实际字号与长文本排版交由用户视觉验收。

- 红框状态摘要局部整理：`UI-PRIORITY-FEEDBACK` / `ca19e6d8275241b5b32abca92ad4c173`，Passed，22.60 秒；既有结果显示、收起恢复、设置伸展与失焦保存合同通过。Release 编译 0 警告 / 0 错误。根据截图调整状态卡片，未改动作列表；未进行新版实机视觉验收。

- 设置与结果摘要第二阶段：`UI-PRIORITY-FEEDBACK` / `7d6ea6dbffc942a194742ce16e440f84`，Passed，22.55 秒。性能页战损阈值失焦保存为 19、切页后恢复；设置高度伸展；结果卡片收起迁入主栈、展开恢复正文首位；失窃仍位于战损之前，新搜索清除旧提示。Release 编译 0 警告 / 0 错误，结构门禁通过，英文词典无重复键。未做实机视觉验收，也未改变搜索或出牌政策。

- 采用顺序与搜索入口简化：`UI-PRIORITY-FEEDBACK` / `34b3478a49a5480bba243234d57a8c46`，Passed，22.51 秒。采用控件固定第二位、搜索时执行控件隐藏，原展开/收起与设置合同通过；Release 编译 0 警告 / 0 错误。未做实机视觉验收。

- 独立释放按钮与右侧自动偏好布局：`UI-PRIORITY-FEEDBACK` / `ee426c9635624b9bbd7ca51578a3e63f`，Passed，22.54 秒；内存条恢复 Pass，释放按钮与内存条同父且位于右侧；展开/收起、主开关和设置伸展合同通过。Release 编译 0 警告 / 0 错误；未进行实机视觉及管理员清理验证。

- 内存条释放入口整合：`UI-PRIORITY-FEEDBACK` / `e74200fa4e634b00b190421b2ac5d739`，Passed，22.46 秒。确认内存条接收鼠标事件、位于主操作区，原布局/启停/设置伸展合同通过。Release 编译 0 警告 / 0 错误。本轮未实际触发管理员授权和系统内存释放，未做真实鼠标点击或视觉验收；该测试不作为系统清理效果证据。

- 用户校正后的最终验证：`UI-PRIORITY-FEEDBACK` / `a9ee0cb8abc34cb384231bc81485b4fa`，Passed，22.61 秒。全自动固定为动作行第一项，展开/收起均保持位置；内存释放入口归属主界面。原有启停、收起战损/失窃和设置伸展合同通过。Release 编译 0 警告 / 0 错误；未做实机视觉验收。以下保留初版证据。

- `UI-PRIORITY-FEEDBACK`：`5708336d44454fc9b073371c0c4e048a`，Passed，22.69 秒。覆盖动作区展开/收起、搜索/空闲、采用入口组合；全自动控件在模式行与紧凑动作行之间移动；标题栏启停事件、维护按钮归属、偷窃策略位置，以及原有 SL 面板恢复、收起战损/失窃和设置页伸展合同。
- Release 编译 0 警告 / 0 错误，结构门禁通过（85 个 Search 文件）。初次编译发现 Godot 控件缺少 partial，补齐后通过；没有使用失败构建的旧产物进行验证。
- 本轮不改搜索、采用和部署命令实现；未进行实机视觉验收，未宣称具体窗口尺寸下的遮挡或帧率已经验证。没有发布创意工坊或推送远端。

## 0.35.5：P0 / P1 反馈

| 场景 | 最终 runId | 结果与范围 |
| --- | --- | --- |
| NATIVE-HAND-CHOICE-REPLAY | f56df4c5816d490fabbd1ebd8172c8bb | Passed，27.38 秒。燃烧契约原生选牌、投斧两次重放；主动构造选择计划失配，保留原生手牌选择，手动完成后具体出牌动作正常结束。 |
| TURN-SETUP-UI-TOOLS | 2080e6ad6c7e4907b80ecddb919ce9cc | Passed，30.64 秒。必备工具 T2 准备选牌中停止、重算、再次停止、继续原生选择，返回 Play 后忙碌状态和输入锁均清除；短/深预算各 1000 ms。 |
| UI-PRIORITY-FEEDBACK | 166804bf580b428dac522d4dca0fddcc | Passed，23.00 秒。主面板启停事件；清理会话且没有 TurnStarted 时恢复面板并保留手动计算；收起时财物提示在战损左侧、新搜索清除旧提示；设置面板从 440 增至 660 高度，内部滚动区增高至少 180，切页正常。 |
| SEARCH-HP-TARGET-STOP | 60ec179b65e7413a8f3d6b3480d3c60e | Passed，23.50 秒。保留既有零损/阈值/开关/DOP2/禁忌魔典哨兵；狩猎兑现收益后零损停搜（1 节点）；指定一瓶与全局至少一瓶均以一瓶零损获胜并保留额外药水；可重复击杀来源在 3 敌人时保持目标 3。 |

- 所有请求使用独立 headless 实例、120 秒上限并在完成后退出。结构门禁 `search_files=85`，Release 编译 0 警告 / 0 错误。
- 失配基线 `3d174aeadc8942abbe94d88764b30f48` 明确失败于“原生选择被取消”；修复后手动恢复合同通过。此前初版 fixture `d2aca6c8ae944e63915a4b74c6505258` 错把动作队列临时空闲当作重放完成，改为复用生产 GameAction.CompletionTask 捕获后普通/投斧场景 `263cacd38bb74b19a43af1b8a4485927` 已通过；该初版错误不是产品卡死证据。一次编译失败后的旧产物测试启动被中止，未计为验证。
- 未复现反馈中的所有“整个游戏进程无响应”情况；也未实际调用 BetterSpire2 的 SL 操作。无可见布局/鼠标验收，没有整场性能或所有第三方组合兼容结论。财物显示合同验证投影和布局，不新增怪物偷窃效果语义结论。

复跑入口为 `tools/run-unattended-test.ps1 -ScenarioId <上述ID> -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance <独立名称> -ExitOnComplete -TimeoutSeconds 120`；TOOLS 使用 SILENT，并附 `-ShortSearchBudgetOverrideMilliseconds 1000 -DeepSearchBudgetOverrideMilliseconds 1000 -ForceShortSearchOnly`。

## 0.35.4：战损目标早停

- `SEARCH-HP-TARGET-STOP` 最终 `f6b4b94d8a6641bf8a6ec2cdd953776b` Passed，22.85 秒；固定 128 节点、1500 ms 短搜，后台独立实例 `hp-target`，120 秒请求上限，完成后退出。
- 缺少对应卡牌而保存禁忌魔典额度 12 时，HasGrowthTargets=false，默认早停仍有效。同根 1 HP 敌人、打击/防御/痛击/燃烧：开启展开 1 个节点，关闭展开 4 个，均完整零损胜利。此数值仅是最小功能对照，不代表整场性能。
- 合同注入已累计损失 3 HP，阈值 3 达标、阈值 0 不达标、开关关闭不达标；用于检查整场累计口径，不声称原生受伤差分。12 HP 敌人场景实际最大并发 2、展开 5 个节点，排空后返回完整零损胜利。
- 加入禁忌魔典后恢复成长例外，即使能立即零损击杀仍取得 1 次删牌收益；忽略局外收益时恢复早停。默认值和关闭后的序列化往返通过。初轮 `f705ffede7d6435b952a0d6c2b453bd6` 已通过基础合同，最终扩展了真正双 lane 场景并覆盖补充搜索出口源码改动后的运行。
- Release 编译 0 警告 / 0 错误，结构门禁 search_files=85。没有可见游戏测试、完整跑局或独立多药水后验场景结论。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId SEARCH-HP-TARGET-STOP -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance hp-target -ExitOnComplete -TimeoutSeconds 120
```

## 0.35.4：按钮区与收起显示

- `UI-COMPACT-QOL` / `fe936f87a2764f329398710cbc41c885` Passed，22.42 秒，专用 headless 实例 `compact-qol`，120 秒上限，完成后退出。
- 补齐浅色主题开关状态颜色同步后，最终 0.35.4 构建同场景 `063d30e4cb70455a856524bfabfd0aed` Passed，22.59 秒；构建来源 `a72a669`。设置刷新触发开关外观更新，仅实际偏好变化写盘。
- 检查开关默认关闭、切换事件更新设置及序列化往返、下场开启、自动计算关闭时仍接入全自动、手动停止后刷新不重开、下一场重开、关闭偏好后下一场关闭。
- 直接渲染只读路线投影：收起后原 Label 可见且 Body 隐藏，7 HP 显示危险色，原位更新 0 HP 变为成功色，展开后归位，新搜索隐藏旧战损，未知投影保持问号和灰色。
- Release 编译 0 警告 / 0 错误，结构门禁 `search_files=85`。未做可见布局/鼠标验收；该 fixture 不证明整场自动部署或开局选牌完整链。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId UI-COMPACT-QOL -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance compact-qol -ExitOnComplete -TimeoutSeconds 120
```

### 古代卡牌成长策略与累计成本（本批此前证据）

- `GROWTH-ANCIENT-POLICY` / `f9031db3e06441dcaa38a76db4c8b693` Passed，26.09 秒，IRONCLAD / FUZZY_WURM_CRAWLER_WEAK。专用后台实例、120 秒上限、1500 ms 短搜；测试完成后退出。
- 真实 CardModel/原生出牌与模拟完整 MoveStateSnapshot 对照：ECHO_FORM_POWER 重放 BRIGHTEST_FLAME 两次，共记 4 点最大生命消耗；随后 FORBIDDEN_GRIMOIRE 记 1 次删牌收益，独立额度 12 HP。原生历史重新捕获保持 4，Fork 增量不污染父分支，BeginSideTurn 保持累计成本。
- 实际搜索及增量回放：已有 4 点消耗、上限 4、手牌含 BRIGHTEST_FLAME / FORBIDDEN_GRIMOIRE / STRIKE_IRONCLAD / CASCADE，抽牌堆含 BRIGHTEST_FLAME。结果获得完整胜利及 1 次删牌收益，路线不使用至亮之焰或会自动打出它的 CASCADE。还检查不限、0、等于上限及手动已超额的准入口径，以及设置往返和成长行重载。
- 初轮 `4ee0b6363f77406aae14eea99c03425c` 已通过前段原生对照，在补充能力审计的 RankFinal 遇到重复对象键；修正对象身份去重后最终场景通过。中间一次启动器未取得已退出进程的 executable path，尚未提交请求；同一 DLL 重新启动后成功。未扩大超时。
- 没有可见鼠标/布局验收，没有完整跑局质量结论，也未验证其他 Mod 修改至亮之焰最大生命变量的历史回写语义。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId GROWTH-ANCIENT-POLICY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance ancient-growth -ExitOnComplete -TimeoutSeconds 120
```

## 0.35.4：PR #83 合并检查与预设节点上限

- `python -X utf8 tools/NoVictoryRecoveryChecks/run.py`：通过 PR 自带 `AssertNoVictoryEscalationPolicy` 的全部策略断言与新增 8 项请求流程检查。直接编译生产 BuildNoVictoryEscalationProfile / EscalateSearchWhenNoVictory；原入口先复现“追加搜索丢弃明确采用结果”，修正后覆盖接管、已有胜利不重搜、胜利退出、停止、拒绝更差结果、两轮封顶、第二轮饱和及仅分支增长。结果、质量排序和根采用确定性替身，未声明整场搜索验证。
- `python -X utf8 tools/NoVictoryRecoveryChecks/presets.py`：直接编译生产四档声明及 SolverSearchProfile，核对节点加倍、时间及 Beam 保持原值。低/中/高/极高 Short 为 2400/4800/10000/20000，Deep 为 12000/24000/50000/100000。
- PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。未启动游戏，本轮没有新增原包恢复、可见 UI 或实战战损验收；下方 PR 附带的检查点记录来自作者此前验证。

## 无胜利路线时的搜索面升级（贡献者记录）

同一个玩家问题包 `CombatSolver-0.35.3-CEREMONIAL_BEAST_BOSS-610658da…`，观者 A9 第一幕 Boss
仪式兽，玩家 34/79、Boss 262/262、两瓶药（稳定血清、缚魂药水），恢复方式 `native_events`
（`materials_valid` / `canSearch=true`，原生二进制编码未校验）。三次都固定
`-NoGcRegionBudgetGigabytesForTest 10`，避免 NoGC 区域建不起来变成隐藏变量。

| 场景 | runId | 结果 |
|---|---|---|
| 第 1 回合检查点，录制的 High 策略，改动前 | 无（对照，未记录 runId） | `OnlyDeath=True`、战损 34、第 6 回合死、用药 0、`searched_turns=6`、17.0 秒。药水梯度三层 `saved=0`，`selected_potions=0` |
| 第 1 回合检查点，录制的 High 策略，改动后 + `-VerifySearchPolicySnapshot` | `76ed67cdcd504d30b451b230246b667c` | Passed，41.7 秒。一次升级（Beam 90→180、节点 25,000→50,000、出牌分支 48→96）后 `layer=2 won=True`：两瓶药、第 7 回合斩杀、战损 30、`OnlyDeath=False`、`Unmirrored=0`。同一次跑通过 `SearchPolicySnapshot` 全组结构断言（含新增的升级政策纯函数断言） |
| 第 3 回合检查点（改动前就能获胜），不可退化哨兵 | `09f0f4a5ea7541fa818c5389c03fb059` | Passed，16.6 秒。`NO_VICTORY_ESCALATION` 出现 **0 次**；`Potion=2, Saved=5/18, Rejected=170, Turns=4, 战损 29, CombatEndedTurn=6, SoldHp=0/15` 与改动前实机日志逐项相同 |

Beam 与节点是乘的关系，只抬一边都不够，四组顶格实测（`b2131b2c54b243d7a65c7ccffb58e17b`
为其中 Beam 135 / 100,000 那一组）：

| Beam | 节点上限 | 结果 | 胜利层实际展开 |
|---|---|---|---|
| 90 | 25,000 | 输 | 主搜索 2,701–5,206（前沿走空，花不掉预算） |
| 90 | 50,000 | 输 | **与上一行逐个相同**，只抬节点无效 |
| 135 | 50,000 | 输 | 需要 83,423，不够 |
| 135 | 100,000 | 赢（第 9 回合、战损 33） | 83,423 |
| 512 | 100,000 | 赢（第 8 回合、战损 31） | 26,671 |

`tools/verify-refactor-boundaries.ps1`：`REFACTOR_BOUNDARIES_OK search_files=84`。
`dotnet build CombatSolver.csproj -c Release`：0 警告 0 错误。

边界：这三次都是单一检查点的搜索质量证据，不是整包回放、不是完整自动部署，也不覆盖原版角色。
默认小遭遇战（`FUZZY_WURM_CRAWLER_WEAK` + 起始牌组）跑 `-VerifySearchPolicySnapshot` 会在
既有的「节点上限释放快照」断言上失败（`dop1=0/3`），改动前后一致，属于该组门禁的场景依赖，
与本改动无关。

## 0.35.3：PR 合并与监控版本提醒（本轮验证）

- 合并后的 `dotnet run --project tools/CardHookReceiverChecks -c Release`：78 项通过；`python -X utf8 tools/EndTurnAdmissionChecks/run.py`：33 项通过；`dotnet run --project tools/CardTargetingChecks -c Release`：37 项通过；`python -X utf8 tools/DamageDealerChecks/run.py`：101 项通过；`python -X utf8 tools/PlayerDeathChecks/run.py`：42 项通过。伤害合同替身有一条 CS0649 未赋值警告；共 291 项是生产方法链接合同，尚未执行游戏原包或完整原生结算差分。
- `dotnet run --project tools/ClientUpdateChecks -c Release`：35 项通过。验证三段数字比较（包括 0.35.10）、相等/更旧/主版本升级、204 旧服务、非法/缺失 JSON、HTTP 失败、取消、服务回撤版本及关闭提醒；失败不覆盖最后有效状态。
- 已部署工作台基线 5611856 同步后，在 `tools/OnlinePresence` 执行 `npm test`：17 项通过，含管理员登录/Origin、采集端隔离、严格版本验证、旧客户端心跳、设置更新/清除/重启持久化及已有统计逻辑。Node 23.9.0 提示 SQLite experimental warning；线上使用 Node 24.15.0。
- 同目录 `$env:BROWSER_CHANNEL='msedge'; node --test browser.test.mjs`：9 项通过。新增版本维护表单实际 DOM 保存/关闭、刷新不覆盖输入，同时验证现有登录恢复、筛选、布局、延迟请求及统计恢复。默认 Playwright 浏览器未安装导致首轮启动失败，改用已安装的 Edge 后通过；未启动可见浏览器或游戏。
- 下方 PR 附带检查属于贡献者原始记录。本轮不将独立替身合同表述为游戏状态差分或实机修复的完整验收。

## 2026-09-10：玩家死亡能力清理

- Release 编译 0 警告/0 错误；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。没有安装或启动游戏。

- `python3 tools/PlayerDeathChecks/run.py`：42 项通过，提取编译生产 HandlePlayerDeath 和 RemovePowersAfterDeath；旧入口在宠物死亡回调观察到残留能力，失败基线已复现。
- 覆盖有/无奥斯提、宠物清理 pending、先清能力再清球/宠物、正负层数移除、允许死后存续能力、其他 owner、重复清理及现有 Illusion 移除否决规则。
- 测试状态/能力模型/宠物 Kill 为替身；不构成真实根/Fork、AfterRemoved 回调覆盖、死亡阻止或原报告完整回放验收。原版 CreatureCmd 的清理位置及 Creature.RemoveAllPowersAfterDeath 已定向核对。
