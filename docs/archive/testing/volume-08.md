# CombatSolver 测试入口历史卷 08

## 0.24.0（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `TURN-SETUP-MANUAL-REFRESH-0240` | 通过（headless 原生选牌页刷新） | 烘焙手套手牌页、工具箱三选一和选择悖论页面都在首次计划就绪后再次请求手动重算，同一页面收到新的 `PlanReady` 并按刷新后的路线完成选择。runId `15f1fedfab9f4a15a8cf224afe6b36fd`、`55f9bea8cf014e94a194eaa8280ab1da`、`635775924e2041e5b8348def2a77fef7`。 | 2026-09-01 |
| `INITIAL-GAMBLING-CHIP-MANUAL-RECALCULATE-0240` | 通过（headless 选择后重算） | 先在赌博筹码页面空选跳过，再立即请求手动重算；求解器进入 Play 后从实际手牌搜索，第 `1` 回合新路线含 `13` 个动作，不再被阶段校验拒绝。runId `f87e11a8a9934e4cb0934ee47cadd4c0`。 | 2026-09-01 |
| `POTION-STRATEGY-FORCED-SEARCH-0240` | 通过（headless 搜索、结构与生命周期） | 主界面从当前药水栏建立图标、官方名称和逐瓶选项，折叠开关生效；新药默认 Smart，Force 的真实短搜使用精确槽位与药水 ID，Disabled 阻止主动用药；新安装/恢复默认解析为 VeryHigh。runId `2b1bef9d976242d09591331db0906466`。 | 2026-09-01 |
| `SMART-POTION-LETHAL-0240-SENTINEL` | 通过（headless 完整自动战斗） | 1 HP 致死场景继续选择格挡药并在第 2 回合获胜，首轮预计用药 `1`、整场战损 `0`、计划外重算 `0`。runId `33fad9cf08aa440eab0bc6dfc790f5b9`。 | 2026-09-01 |
| `STRATEGY-ACTION-ADMISSION-EXPERTISE` | 通过（headless） | 原单节点分支预算压到 `3`，手牌包含升级熟练、升级子弹时间和三张打击；搜索先保留资源/过牌代表，首动作选择熟练并完成三回合短搜。runId `d2edebb4a7094d2fbce5787e02cc849c`。 | 2026-08-31 |
| `STRATEGY-SMART-POTION-INTERVENTION` | 通过（headless） | 四只花园幽灵鳗固定 `15 HP`、纯攻击牌组和一瓶格挡药；Smart 主路线为无药死亡边界，主动强制一瓶反事实找到存活路线，最终采用格挡药并确认省血 `12/9`。runId `fb7b2287a75e442cad01e1ca32f14417`。 | 2026-08-31 |
| `STRATEGY-SEMANTIC-AFTERIMAGE` | 通过（headless） | 单节点分支预算为 `3`，逐次出牌获得格挡的能力与五张 0 费攻击牌同手；能力收益按可达出牌次数形成防伤向量，首动作使用能力牌，路线预计战损 `0`、实际格挡 `5`。runId `35b7698ab74d462bb40182008bf6cd82`。 | 2026-08-31 |
| `STRATEGY-HP-INVESTMENT-DYNAMIC` | 通过（headless） | 一张提供能量和抽牌的牌直接支付 `6 HP`，超过普通战斗原 `5 HP` 阈值；同起点保守路线存在时，至少一条确实换来战斗进度的投资分支获得保护，最终结果仍按整场战损选择零卖血路线。runId `02b131c187d1433c94ea558e485c591d`。 | 2026-08-31 |
| `STRATEGY-ACTION-ADMISSION-COVERAGE` | 通过（headless） | 固定单节点分支预算 `3`，六种不同即时攻击、过牌与费用控制候选竞争；至少一个原即时 Top-N 之外的战略家族代表进入 frontier。runId `06eb33b27c344825b0801d282b7b8df2`。 | 2026-08-31 |
| `STRATEGY-DOP-EQUIVALENCE` | 通过（headless） | 固定 `250` 节点下 DOP1/DOP2 的动作、评分、展开、转移、分族保路、生命投资和全部非时序剪枝统计一致；DOP2 实际形成至少两路并发。runId `64414648aaae4406a570a0ff59ef1f17`。 | 2026-08-31 |
| `STRATEGY-REPLAY-FDDD-MEDIUM` | 通过（headless） | 严格组合永世沙漏报告同检查点的 `run-state` 与 `replay-state`，完整 `ContinuationStamp` 一致；Medium `24/60` Beam、Smart、DOP4、8GB No-GC 下首动独门技术，第 `4` 回合无药击杀，预计战损 `3`，追平并优于玩家 `6` 战损上界。当前源码回归 runId `3e1f05195228471bbea6cafaabcab1d7`。 | 2026-08-31 |
| `STRATEGY-REPLAY-0F7F-VERYHIGH-RETAIN-ROUTING` | 通过（headless） | 严格恢复蜂群术士报告首回合根及完整 RNG；VeryHigh、禁用药水、DOP4、8GB No-GC 下完整路线第 `13` 回合无药击杀，预计整场战损 `0`，从报告原求解器 `57` 追平人工 `0`。runId `34d0742df3c74ee6a9a5006eabf0ece2`。 | 2026-08-31 |
| `STRATEGY-REPLAY-9E8B-HIGH-ORB-LINEAGE` | 通过（headless） | 严格恢复胧光怪报告第二回合、寄生惧魔召唤物、球槽和球队列；High、Smart、DOP4、12GB No-GC 下首步电击，随后飞跃、防御+、防御，完整路线第 `9` 回合无药击杀，预计整场战损 `0`，从报告原求解器 `29` 追平人工 `0`。runId `d7c41a3e64d24fb792d2b45504222e25`。 | 2026-08-31 |
| `STRATEGY-REPLAY-394B-HIGH-SHORT` | 通过（headless） | 严格恢复虔诚雕塑家报告第二回合根；High、禁用药水、DOP4 在 Short 阶段得到第 `4` 回合结束的 `0` 战损路线，从报告原求解器 `22` 追平人工 `0`。runId `75e243420b5b4f5ca48bc850a5e91ff2`。 | 2026-08-31 |
| `STRATEGY-REPLAY-EF3E-HIGH-POTION-COUNTERFACTUAL` | 通过（headless） | 严格恢复寄生蛙报告第三回合根；High、Smart、DOP4 在 Short 阶段找到 `1` 战损无药路线，评估并拒绝 `120` 条药水分支，从报告原求解器 `13` 改善并优于人工爆炸药路线 `2`。runId `a967a8dbce494fa2948f6d4d94211a4d`。 | 2026-08-31 |
| `STRATEGY-REPLAY-99DC-HIGH-CALCULATED-GAMBLE` | 通过（headless） | 严格恢复构装兽群报告首回合根；High、禁用药水、DOP4 在 Short 阶段得到第 `4` 回合结束的 `0` 战损路线，从报告原求解器 `10` 追平人工 `0`。runId `10f27aef7c9941a8820de637ce28c2ab`。 | 2026-08-31 |
| `STRATEGY-REPLAY-8695-HIGH-ENERGY-DEFENSE` | 通过（headless） | 严格恢复感染棱晶报告首回合根；High、Smart、DOP4 得到 `8` 战损，评估并拒绝 `120` 条药水分支，从报告原求解器 `21` 追平人工 `8`。runId `566104854a2448ef95976505746abac2`。 | 2026-08-31 |
| `STRATEGY-REPLAY-EE98-HIGH-DUAL-POTION` | 通过（headless） | 严格恢复寄生蛙报告首回合根；High、Smart、DOP4 在 Short 阶段使用束缚药水和格挡药水，反事实省血 `50/18`，战损从报告原求解器 `27` 降到 `17`，追平人工。runId `c3e91ca55bd3476487d8710e899b12f1`。 | 2026-08-31 |
| `STRATEGY-THE-HUNT-OPPORTUNITY-COST` | 通过（headless） | 感染棱晶严格首根 High、Smart、DOP4 从 `29` 降到 `21`，前三回合战损 `0/6/13` 与人工一致，runId `2ac40cf8aa7240629ccc5fa1a10f894d`；致命狩猎哨兵仍首动使用狩猎、长期资源至少 `30`、战损 `0`，runId `4c445ef747f643c59b0fc437bd161d4d`。 | 2026-09-01 |
| `STRATEGY-REPLAY-941B-GENERATED-RESOURCE-POTION` | 通过（headless） | 直飞产卵虫严格首根 High、Smart、DOP4 选择无色药水生成急躁，随后打击、急躁、群星之子+、战火铸就，战损从当前 `2` 降到 `0`，追平人工；runId `f8bc0f22a1be4c688f75c603528b917d`。感染棱晶哨兵保持 `21`，runId `6fb2503e4bb34db2972b569f7dd944d9`。 | 2026-09-01 |
| `STRATEGY-SMART-POTION-NONDEGRADING` | 通过（headless） | 蔓生伏地虫严格首根 High、Smart、DOP4 保留 `4` 战损无药主路线，拒绝 `14` 战损敏捷药补查路线并追平人工，runId `5db3d24895f045ee888841b2d9ee207a`；无色药生成急躁哨兵仍为 `0` 战损，runId `296884e4797343f8a1a0502da863b778`。 | 2026-09-01 |
| `STRATEGY-SMART-POTION-ENABLER-FOCUS` | 质量改善，待处理 | 残杀千足虫严格首根 High、Smart、DOP4 保留放血→迅捷药、持续设置和分目标首攻后验，战损从未完成路线的 `48` 降到获胜路线 `35`，人工为 `31`，runId `e05ccbcc56a0448e87d58fc075c9fd60`；蔓生伏地虫哨兵保持无药 `4`，runId `f2ab6a5bfb9a486ba244b1cbcf86a075`。 | 2026-09-01 |
| `STRATEGY-OPENING-RESOURCE-DEFENSE` | 通过（headless） | 灵魂枢纽严格首根 High、Smart、DOP4 选择燃烧契约→火焰屏障→好勇斗狠，战损从 `19` 降到 `12`，优于人工 `17`，runId `21c2a331c31641259a6810a7b93704d6`；蔓生伏地虫哨兵保持无药 `4`，runId `f387d4c98a3e478082ece11e79e38e0c`。 | 2026-09-01 |
| `STRATEGY-LONG-TERM-RESOURCE-CHANNEL` | 通过（headless） | 长期资源通道与即时战损主通道使用独立 Beam 排名。虱虫祖先严格中途根 High、Smart、DOP4 为 `16` 战损，追平人工，runId `ab7beaa750714ed28bfcb7a6d5d781fb`；感染棱晶哨兵为 `19` 战损，追平人工，runId `066e5ed6d1c246e5adbd2522261ebcad`。 | 2026-09-01 |
| `STRATEGY-SMART-POTION-COST-ACCOUNTING` | 通过（headless） | Smart 后验按每瓶 `9 HP` 重算强制用药候选成本。鬼祟珊瑚群两瓶药仅省 `9 < 18`，VeryHigh、Smart、DOP4 保留 `10` 战损无药路线，runId `121d37d20fa24430a2528879aebab339`；永世雕像一瓶迅捷药省 `10 >= 9`，保持 `6` 战损并追平人工，runId `5237dd207c044649b44534f3edb0ddf0`。 | 2026-09-01 |
| `LEGACY-REPLAY-BASELIB-EMPTY` | 通过（headless 导入边界） | schema 1 旧包缺少 BaseLib modifier 字段、当前卡牌均明确为 `baselib=-` 时，完整机甲骑士首回合根严格恢复；非空 modifier 仍不兼容。runId `17d735ddcc0f463d95cc4fca1e06253a`。 | 2026-09-01 |
| `AEONGLASS-LISTENER-CACHE-FORK-FINAL5` | 通过（headless 语义门禁） | 首次 COW、结构失效、普通字段缓存复用、父分支/OwnerPile 隔离和完整根快照均通过。runId `932ad9691a204b3c8ea37f795c4a92b7`。 | 2026-09-01 |
| `BASELIB-CARD-MODIFIER-LISTENER-CACHE-FINAL6` | 通过（完整 BaseLib headless 保守兼容门禁） | 动态增删、状态键、continuation、Owner/分支隔离、生成卡复制和空列表功能路径均通过；空路径不创建临时列表由源码审计。动态 `StoreSaveData` 回调在枚举中新增 live modifier，本次完整状态键仍与回调前相等。runId `06c5235a6d0941f69447180517bce7ab`。 | 2026-09-01 |
| `GC-LIFECYCLE-POLICY-MECHA-013` | 通过（headless 合成政策时序门） | 低/高分配与引用屏障通过；obligation 登记后，exhaustion 引用在 Gen2 标记前释放时总 Gen2=`1`，标记后为 `2`，两条弱引用图均死亡；额外正式 release epoch 不触发第三次回收。生产检测入口另经静态审计。runId `9b4f577a08c84800b219cbcb0bc83310`。 | 2026-09-01 |
| `GC-CONTROLLER-RELEASE-MECHA-013` | 通过（headless 控制器生命周期门） | A→B→Reset、搜索/部署 CTS 与旧 Setup epoch 通过；真实 3 秒 Godot timer helper 取消后屏障在 1 秒内完成，正式 Setup/Resume token 接线另经静态审计。runId `ef96c383720b47dbbdcb62075bcb665d`。 | 2026-09-01 |
| `PR19-NOGC-REGION-EXIT-DELAY` | 目标通过；组合门后续失败 | 实际建立 `1 GB` No-GC 区域后请求低分配战斗结束，区域退出延后 `3011.4 ms`，`gen2_delta=0`，确认延迟位于 `GC.EndNoGCRegion` 前。组合夹具随后在无关的DOP2搜索门以 `waves/work_items/max_concurrency=0/0/0` 失败，毛绒虫与机甲输入结果相同；不将整套组合门记为通过。目标 runId `f9615ee6bdf648fcbba17330aecfb9ea`。 | 2026-09-01 |
| `BUGREPORT-UTF8-CURRENT-RECENT` | 通过（headless 输出兼容门） | 当前/最近两类问题包的 metadata/replay 均为无 BOM 严格 UTF-8，并继续通过 JSON、native-state、run-state 与槽位隔离断言；`byte[]` 常驻表示和单次序列化另由源码审计及驻留估算支持。runId `43e3cc399a6b45e8968da5f7af05556d`。 | 2026-09-01 |
| `PRESENTATION-STRINGVAR-STATE-DIFF-FINAL` | 通过（headless 严格差分） | `NightmarePower.Card` 与 `ShrinkPower.ApplierName` 的本地化展示值不再制造假差异，字段/数值基线仍比较，未知字符串仍 fail-fast。runId `1fe98254c4d045f7a6d7872ae0f26c6f` / `fb2d85cd91724310a3772ebf6204e893`。 | 2026-09-01 |
| `HEADLESS-FULL-MATRIX-20260901` | 场景结果全通过；矩阵命令含 1 次已复验的启动器竞态 | 完整 `246` 命令为 `242 Passed / 3 SkippedMissingFixture / 1` 启动器退出身份竞态，因此该次矩阵命令整体非零；该项游戏内已 Passed（`50a3d2aaf3d648498666e9d46ad1b2b9`），同命令独立复验由启动器返回 `0`（`08fdef52d7974d9185b255edafd6395e`）。最终 `243` 条可执行命令（`242` 个唯一 ScenarioId）均有通过结果，无未解决行为失败。 | 2026-09-01 |
| `SEARCH-PERF-IRONCLAD-CLONE-HAVOC-ROOT-A/B` | 通过（清空默认牌组的固定根 A/B） | 同一 `2305` 张战斗牌、`2302` 张永久牌组牌、`4499` listener 的严格 A/B 为 `4570.556 → 2215.689 ms`；最终源码复验根阶段 `2208.239 ms`，runId `7e8a7512406c44e6bd0bb0fec7a788e0`。 | 2026-09-01 |
| `SEARCH-PERF-IRONCLAD-CLONE-HAVOC-1S-A/B` | 通过（清空默认牌组的固定工作量 A/B） | 严格 A/B 的耗时/分配降低 `33.07%/70.68%`；最终源码复验仍为 `1/3/3/2`、同一 `ENTROPY`、`-3752001`、TimeLimit、0 GC，`3717.9 ms / 267,559,992 B`，runId `47c008cb690d4073bfa0781d510d6378`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-KNIGHTS-CANONICAL-DOP1-2S` | 通过（合成首回合） | `5` 种随机攻击、`5` 种随机防御各 `6` 张，加 `INFERNAL_BLADE/STOKE/CATASTROPHE`；DOP1 短搜 `1324.6 ms / 96,126,192 B`，`choice/actions/sold-hp-pruned/turns=0/4/14/4`，0 GC。runId `108c2fd35daf46938d66be241d51283a`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-QUEEN-CANONICAL-DOP1-2S` | 通过（合成首回合） | 同样的 `5+5` 冻结随机填充，加 `BUNDLE_OF_JOY/SPECTRUM_SHIFT/ENTROPY/JACK_OF_ALL_TRADES` 及对应 Power；DOP1 短搜 `2143.5 ms / 250,810,968 B`，`choice/actions/sold-hp-pruned/turns=1146/6/270/2`，0 GC。runId `f590d200f7e0467aaf090c140d2eced8`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-TEST-SUBJECT-CANONICAL-DOP1-2S` | 通过（合成首回合） | 同样的 `5+5` 冻结随机填充，加 `CREATIVE_AI/AUTOMATION/MAYHEM/JACKPOT` 及对应 Power；DOP1 短搜 `2062.0 ms / 160,130,880 B`，`choice/actions/sold-hp-pruned/turns=0/6/0/5`，0 GC。runId `17875c14859c4aedb5f44e5f6539b788`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-AEONGLASS-DOP1-2S` | 通过（合成首回合） | 同样的 `5+5` 冻结随机填充，加 `TRANSFIGURE/SEEKER_STRIKE/CALL_OF_THE_VOID` 及对应 Power；DOP1 短搜 `1393.4 ms / 149,711,016 B`，`choice/actions/sold-hp-pruned/turns=516/4/24/5`，0 GC。runId `11d097fe49764027a69c551616ab5416`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-QUEEN-DOP4/8` | 通过（同工作量并行探索） | DOP4/8 均为 `choice/actions/sold-hp-pruned/turns=2237/6/234/6`；`1764.1 ms / 553,203,344 B` 对 `1489.3 ms / 560,337,384 B`。DOP8 快 `15.58%`，分配多 `1.29%`；runId `329ace6a379748fbb980b22d7d4f2ba3` / `c2b1de7dda994123be24c5fbdeb25b4c`。 | 2026-09-01 |
| `AEONGLASS-PERF-VISIBLE-STEAM` | 未验证 | 当前数字只来自隔离 Linux headless；完整用户 Mod 组合下的主线程帧、根捕获与 GC 仍需可见 Steam 会话验收。 | 2026-09-01 |

## 0.23.0

本版本只汇总 `0.22.1–0.22.11` 的既有改动并同步发布版本，没有修改行为源码；沿用下列各补丁版本已经记录的定向回归，不重复执行行为测试。

## 0.22.11

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-141700-TURN-START-CHOICE-BASELINE-R2` | 预期失败，已修正 | 第 2 回合熵选牌后通用阵营回合开始重复结算，路线产生一次计划外重算。runId `632b60e264064d149ec0e95f45e8a15a`。 | 2026-08-31 |
| `FIX-141700-TURN-START-CHOICE-FIXED` | 通过（headless） | 第 2 回合熵选牌后回合开始效果只结算一次；首轮路线直接复用，计划外重算 `0`。runId `cf5c0c5b057e4e0a80409e87124c7535`。 | 2026-08-31 |

## 0.22.10

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-133031-DEPLOY-CARD-IDENTITY-R2` | 通过（headless） | 两个同名卡牌实例具有不同临时状态；前一个实例离手后，实机部署按路线保存的完整状态身份选中剩余计划实例。Fork 边界与首回合实际自动战斗同时通过，runId `9e733b537f8f4eee98a4df4f79389bb6`。 | 2026-08-31 |
| `FIX-133031-TEST-SUBJECT-CURRENT-ROUTE` | 通过（headless） | 从实验体问题包战前跑局恢复种子、牌组、遗物与 RNG，以 12 秒短搜和 DOP8 自动执行到第 3 回合；直接复用首轮路线，计划外重算 `0`，没有部署或原生选牌失败。runId `7a8d7f88e17b4b05af7e99e2139e1044`。 | 2026-08-31 |
| `FIX-133031-TEST-SUBJECT-DEPLOY-IDENTITY` | 夹具断言失败，未计通过 | 同一现场已完成第 3 回合复用且计划外重算 `0`，但请求额外强制必须打出全息影像；当前短搜选择了另一条合法路线，因此只该特定出牌断言失败。runId `d376600caf704cdfb06cd2ef061658a7`。 | 2026-08-31 |

## 0.22.9

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-123849-BOUNDARIES` | 通过（headless） | 同一 L1 夹具覆盖新玩家能力位于战斗卡牌之前、横祸嵌套自动打出虚空形态后产生并消费一次结束回合请求，以及预知之滴在三个同名升级剑柄打击上跨同父 Fork 稳定回放。runId `aadd72eb88f0498a8a125e43dd2a3000`。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID` | 通过（headless） | 固定手牌仅横祸、抽牌堆仅虚空形态；短搜首动作是横祸，路线第 1 回合只有这一项动作并继续搜索到后续回合。runId `0569fa1dddf5479493aab7fe14a352c6`。 | 2026-08-31 |
| `FIX-123849-DROPLET` | 通过（headless） | 固定四张同名升级剑柄打击和预知之滴，DOP1 强制至少使用一瓶药；搜索完成 `12` 个选牌分支、使用一瓶药且没有动作回放失败。runId `5babab76d0de43a3bb73861bb7e40726`。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID-DEPLOY` | 通过（headless） | 全自动实际打出横祸并由内层虚空形态结束第 1 回合；没有尝试同回合后续动作，第 2 回合直接复用首轮预测，计划外重算 `0`。runId `9cc7926cb1814abcabad5ad83dee4bfc`。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID-DEPLOY`（首轮） | 预期失败，已修正 | 首轮 runId `27b504a98ee84ae8b3eb4b3288544c7b` 已证明部署停止同回合动作，但第 2 回合因续用缓存只登记显式结束回合节点而重算；统一动作回合边界后由最终夹具通过。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID`（参数首轮） | 夹具失败，已修正 | runId `c1e23e0c1b444ee1ac05ae80800eb14a` 误用必须同时提供卡牌标题的断言参数；改用首动作卡牌 ID 断言后通过，不属于生产功能失败。 | 2026-08-31 |

## 0.22.8

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `KNIGHTS-GC-NRE-FIX-NEXT` | 通过（headless） | 实际进入一次搜索内内存检查点并完成全代回收后继续；随后 No-GC `1 GB → 2 GB` 生命周期正常，DOP1/DOP2 固定工作量结果一致。首轮短搜完成后停止，runId `f8764bdbe2594c66920ff428fe6425d6`。未完整重放问题包中的三骑士战斗。 | 2026-08-31 |

## 0.22.7

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `TOASTY-SINGLE-STEP-UI-FIXED-NEXT-R2` | 通过（headless） | 执行第 1 回合后停在第 2 回合烘焙手套原生手牌页；求解器没有代选，路线 UI 已同步到第 2 回合。runId `64243d425288491198f9a6bf5f415de0`。 | 2026-08-31 |
| `TOASTY-SINGLE-STEP-EXPLICIT-TAKEOVER-NEXT` | 通过（headless） | 从同一单步边界明确点击执行本回合后才接管烘焙手套；选择到部署间隔 `47 ms`，第 2 回合复用既有路线，计划外重算为 `0`。runId `ae2d5384fa144815b7a250f2442c556c`。 | 2026-08-31 |
| `TOASTY-SINGLE-STEP-UI-FIXED-NEXT` | 预期失败，已修正 | 新增 UI 回合断言首次抓到选牌状态早于原生页面锁回调，面板仍为第 1 回合；将显示同步到回合准备事务建立时后，由 R2 通过。runId `a3f331c2cf0b48c29f42e6b6c3a56b00`。 | 2026-08-31 |

## 0.22.6

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-095952-BOUNDARIES-R2` | 通过（headless） | 同一快速夹具覆盖冻结快照不受实机原牌移除标志污染、牌组原牌与局内生成复制的精确回放身份、流沙坑不存在时狂乱逃离为空操作、毒气炸弹自爆为终止行动且眩晕仍有后继，以及根级 Hook listener 捕获。`ForkBoundaries` 与 `CombatRootSnapshot` 均通过，runId `0c19ded7f13444fe914499f85924b840`。 | 2026-08-31 |
| `BATCH-095952-INCREMENTAL` | 通过（headless） | 固定 1 秒短预算在首个结果停止，增量分叉与完整前缀回放保持一致，runId `dd22d98ed4174af692a27764871314f9`。请求 DOP4，但严格增量模式按现有测试政策强制单线程，因此不计并行性能证据。 | 2026-08-31 |
| `BATCH-095952-BOUNDARIES-R1` | 夹具失败，已修正 | 新增流沙坑断言最初直接构造未挂接战斗状态的模拟对象，runId `52ab5e56bb1744e99aa9b993b3a22306` 在测试准备阶段失败；改为从已挂接模拟器取得战斗状态后由 R2 通过，不属于生产功能失败。 | 2026-08-31 |

## 0.22.5

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `TWO-TAILED-RAT-PENDING-AI-UNIT-0225` | 通过（headless） | 敌方回合生成随机初始分支的双尾鼠时不消费怪物 RNG；回合边界才掷出抓挠、疫病啃咬或尖啸。runId `02120badde45441f99b384437879e517`。 | 2026-08-31 |
| `TWO-TAILED-RAT-PENDING-AI-FINAL-0225` | 通过（headless） | 从问题包恢复同一首回合牌序、三只鼠生命与行动；修复前 runId `941ffb14c3244100b3285336a61db0eb` 在第 3 回合呼叫支援失败，修复后首轮短搜得到 10 回合路线、预计战损 `4`，runId `5649195a84e342c7b8bc8d70dd5281c0`。 | 2026-08-31 |

## 0.22.4

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `STRATEGY-PHANTASMAL-FINAL-0230` | 通过（headless） | 从问题包精确恢复花园幽灵鳗首回合根；4 秒短搜找到两瓶药路线，预计整场战损 `27`，低于改动前同根 `35`。runId `954385d2794049ada20ee0706016486c`。 | 2026-08-31 |
| `STRATEGY-AXEBOTS-MASTER-0230` | 通过（headless） | 从问题包精确恢复巨斧机器人根；必备工具 setup 校准后预计战损 `4`，改动前同根为 `5`。runId `4595ce39f3df4775b008ea75d46d1f20`。 | 2026-08-31 |
| `STRATEGY-KNOWLEDGE-REVERT-GUARD-0230` | 通过（headless） | 知识恶魔精确根维持预计战损 `3`；验证失败的早有准备统一加权已经撤回。runId `3f4973a3ebe64387af2fca6925797354`。 | 2026-08-31 |
| `STRATEGY-KAISER-CURRENT-0230` | 通过（headless） | 帝王蟹精确根维持预计战损 `0`，未因药水策略校准退化。runId `8b27b0f445b24e669189b855e342358a`。 | 2026-08-31 |
| `STRATEGY-NIBBITS-FINAL-GUARD-0230` | 通过（headless） | 固定双小啃兽长线根维持预计战损 `0`，不使用药水。runId `0d8363df9df44164a0ac8f4267d64e31`。 | 2026-08-31 |

## 0.22.3

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-0223-DYNAMIC-VAR` | 通过（headless） | 计算型 Damage 变量按实际 `DynamicVar` 读取基础值，不再强转原版 `DamageVar`，且选牌估值不调用第三方实机求值器。runId `adf440d49b424b659d54c2187f4c5953`。 | 2026-08-31 |
| `FIX-0223-GC-COLLECTION` | 通过（headless） | 高血量、多候选固定根完成首次后台 Gen2 回收、No-GC `1 GB → 2 GB` 切换和 DOP1/DOP2 全字段等价。runId `e8c3222c43d842daa3e2e640155c8d3f`。 | 2026-08-31 |

## 0.22.2

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-0222-BOUNDARIES` | 通过（headless） | 同一快速夹具覆盖原生选牌页期间卡牌状态变化后的实体定位、出牌结束 Power 提交、免费能力牌遗物消耗、超质量体生成牌创建者、死亡后禁止继续施加 Power、敌方回合召唤怪初始行动与幻象复活，以及清空实机怪物状态机后仍使用根快照推进行动。runId `805266b49faa4435abaae7566edaed64`。未逐场运行本批 `18` 份完整战斗。 | 2026-08-31 |

## 0.22.1

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-225649-ROOT-SEMANTICS` | 通过（headless） | 验证新怪物生命判重读取模拟最大生命；直飞产卵虫已有蛋的模拟/原生最大生命刻意不同时，新蛋仍避开模拟占用值。同步验证 `WHISPERING_EARRING` 在第二回合不再自动出牌及 Fork 边界。runId `a5104a89fd614a8196c17ea86bc2f042`。 | 2026-08-30 |
| `BATCH-225649-SPEED-POTION-END-TURN` | 通过（严格差分） | 从已存在 `DEXTERITY_POWER:8 + SPEED_POTION_POWER:5` 的根开始，回合末原版与预测都回到 `DEXTERITY_POWER:3` 并移除速度药水 Power。runId `bc952a2f52314755b7be8f215e348349`。 | 2026-08-30 |
| `BATCH-225649-COMPACT-BUG-REPORT` | 导出结构通过；后续搜索等待超时 | 实际问题包 `71,470` 字节、`21` 个条目；断言只含当前战斗，没有截图、`saves/` 或 `forensics/recent`，保留战前内存存档和两组完整检查点。结构断言完成后，夹具在等待初始求解结果时达到 `120` 秒；不计整场通过。runId `4b0759b2552a429db4a723058788333b`。 | 2026-08-30 |

## 0.22.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR10-LARGE-DECK-FIXED576-A/B` | 通过（PR 固定工作量 A/B） | 基线与优化 artifact 都保持 `576` 展开、`3463` 转移、`1124` 选牌分支和同一 3 回合、预计战损 `2` 路线；`5116.3 ms / 1,039,502,640 B` 对 `3985.0 ms / 528,876,328 B`，累计分配降低 `49.12%`，单次 headless 墙钟缩短 `22.11%`。不替代可见 Steam 结论。 | 2026-08-30 |
| `PR10-FULL-DEEP-16GB` | 通过（PR 当前 artifact） | 精确进入 `16,000,000,000 B` No-GC 区域，保持 `7018/52644/23196` 工作量、评分、10 回合胜利、预计战损 `56`、卖血 `11` 与两瓶药路线；`53605.8 ms / 11,655,259,632 B`。 | 2026-08-30 |
| `PR10-NOGC-RNG-BUDGET-CONTRACT` | 通过（PR 当前 artifact） | 实际进入 `1 GB` 区域，令并发 `2 GB` 请求等待，释放旧 scope 后精确重建 `2 GB`；同时验证完整 RNG 身份及 DOP1/DOP2 结果全字段一致。runId `5677b8ccffc842d68f2199da964ed610`。 | 2026-08-30 |
| `PR10-SANITIZED-STRESS-FIXTURES` | 通过（PR 当前 artifact） | Silent `396` 张合成牌堆和 Necrobinder 最小战前投影均使用公开合成 seed，断言极高档原 Beam、节点、分支和精确 `16 GB` No-GC；runId `a2aef73ea38345a7b48418f7ff498ffc`、`07427794ff05455886fc7faf2318966e`。 | 2026-08-30 |
| `PR10-POTION-CHOICE-ALLOCATION` | 通过（PR 严格语义门禁） | 生成牌药水只克隆实际选中牌；17 项药水完整原版/预测差分 `17/17`，赌徒特酿专项通过。runId `341bf965156644c4a8fa6e3cd2399682`、`c5f1b95001f542d3b0d536295f914bdc`。 | 2026-08-30 |
| `PR10-CACHE-FORK-BOUNDARIES` | 通过（PR 当前 artifact） | 覆盖 Ritsu capability 缓存失效、选牌键跨 Fork、池化身份哈希、listener observer、所属牌堆、投影洗牌、稀疏 Power affliction 和 `CardPlay` 选择风险隔离。runId `bd24e4eeda0247f99eaac9fa90281e3b`。 | 2026-08-30 |
| `PR10-MERGED-POLICY-FORK-0220` | 通过（本地主线合并态） | 高血量、多候选固定根实际完成 No-GC `1 GB → 2 GB` 切换、DOP1/DOP2 全字段等价、节点上限快照释放、Fork 边界及完整自动战斗；第 6 回合结束，runId `26654574147d4925be016d7295fbee2a`。首次 `1 HP` 烟雾输入因没有形成并行工作量而被门禁拒绝，不计功能失败。 | 2026-08-30 |
| `PR10-VISIBLE-STEAM` | 未验证 | PR 没有可比较的当前 artifact 可见 Steam 性能结果；本轮不把 headless 单次墙钟写成生产帧率结论。 | 2026-08-30 |
| `PR11-THEME-OPACITY-LIFECYCLE-0220` | 通过（headless） | 验证新设置默认深色主题与 100% 不透明度，浅色/55% 设置可回读；切换主题会重建覆盖层、恢复设置页与当前搜索状态，并即时应用 65% 透明度。既有三页设置、通知、上传终态、预设持久化和搜索停止/恢复同时通过，首回合结束。runId `921055897c4c43ceb773c90c79eac953`；不替代 Steam 可见像素、拖动和动画检查。 | 2026-08-30 |
| `PR11-OPACITY-SLIDER-VISIBLE-RAIL-0220` | 待实机确认 | 透明度控件改为固定可见轨道、强调色已选区段和独立悬停拖动圆点；数值回读、即时应用和主题重建由上一项覆盖，像素观感与鼠标拖动留给本地可见游戏确认。 | 2026-08-30 |

## 0.21.7

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `SETTINGS-TABS-LIFECYCLE-NEXT` | 通过（headless） | 实际创建设置控件并验证“常规 / 性能 / 反馈”三页独立切换；通知“关闭 / 仅后台 / 始终”无损回读旧字段，预设重载不误判自定义，上传成功/取消终态和控制器停止/恢复链路同时通过。第 1 回合结束，runId `852a0f03f4724d6598212e309d11a2b4`；不替代人工视觉检查。 | 2026-08-30 |
| `NOTIFICATION-SETTINGS-LIFECYCLE-NEXT` | 通过（headless） | 验证通知默认开启且默认为“仅游戏不在前台”，关闭、仅后台和始终通知的决策正确；设置 UI 按持久化值加载。用户停止搜索产生一次结束通知请求，headless 不进入 Windows 原生调用。runId `99e510b870fc4ad6ab0611ce36f8f3b1` | 2026-08-30 |
| `WINDOWS-NATIVE-NOTIFICATION-ENTRYPOINT-NEXT` | 通过（系统声音量未检测） | 可见游戏日志确认旧入口连续 5 次抛出 `EntryPointNotFoundException`；显式绑定 `Shell_NotifyIconW` / `LoadIconW` 后，独立 Win32 窗口按与 Mod 相同的结构提交通知，返回 `shown=True / win32_error=0`。通知未设置静音标志，实际音量与是否播放由 Windows 通知策略决定。 | 2026-08-30 |

## 0.21.6

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR9-BUG-REPORT-FIFO-VERSION-FINAL` | 通过 | 同一最小战斗覆盖在线描述版本号、控制器自动分类、后台检查点 FIFO 与导出屏障；活动战斗问题包成功生成，结构化状态、RNG、五个牌堆、原生状态、即时跑局存档和 `solver_only` 控制模式断言均通过。runId `7065e4ebabd64bc69700ebf70d323e27` | 2026-08-30 |

## 0.21.5

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-193556-RNG-IDENTITY-FINAL` | 通过 | 人工构造计数相同、内部状态不同的战斗 RNG，续用文本和搜索状态键都能区分；固定 250 节点的 DOP1/DOP2 完整搜索政策继续一致。runId `8d6ae9a5647240a5846a2549cb06c369` | 2026-08-30 |
| `BATCH-193556-SOUL-NEXUS-TURN-SETUP-FALLBACK-FINAL` | 通过 | 从 `SOUL_NEXUS` 问题包战前存档进入赌博筹码原生页面；释放候选后的重建保留开局选择，8 秒短搜返回 6 回合路线，不再要求已经换走的启动流程仍在手牌。runId `1ec4e296e601422b86674f7f7d3d35df` | 2026-08-30 |
| `BATCH-193556-SEEKER-CHOICE-ACTION-SCOPE-FINAL` | 通过 | 同一张牌在一个 `CardPlay` 内能看到自己的待解决选择；开启新的 `CardPlay` 后不会继承上一次选择风险，Fork 边界检查同时通过。runId `7fcf4d2c016f40a6aeeaf6f314b9cf3e` | 2026-08-30 |
| `BATCH-193556-SEEKER-CHOICE-DIFFERENTIAL-FINAL` | 通过 | 探寻打击的随机候选、原生选牌、移入手牌与预测完整状态严格一致。runId `cac18e0431be4bc2a06b474da84460dc` | 2026-08-30 |
| `BATCH-193556-EXOSKELETON-RNG-BASELINE` | 部分，不计通过 | 问题包战前根在 20 秒单线程短搜下运行至第 2 回合后达到 120 秒总上限，没有到达原第 5 回合选牌失败，不能作为问题包整场复现或修复证据。runId `8655f27259fa4d0c822ebef9084f2eac` | 2026-08-30 |

## 0.21.4

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-184344-MAYHEM-STRATAGEM-CHOICE-FINAL` | 通过 | 花样百出自动打出微光，微光抽牌触发洗牌和战略选牌，随后继续微光自己的手牌选择；两层选择按原版顺序完成，牌堆和完整状态严格一致。runId `0583a87ba9504c54b3b9df92f40398b3` | 2026-08-30 |
| `BATCH-184344-ILLUSION-FIRST-MOVE-REVIVE-FINAL3` | 通过 | `FOGMOG` 召唤利齿之眼后登记其首次正式行动，再于行动前击杀；原版与预测均进入 `REVIVE_MOVE`，完整状态严格一致。runId `2c4c0640cedd45bbac812bab31de22ee` | 2026-08-30 |
| `BATCH-184344-PARALLEL-CLONE-FORK` | 通过 | 缩小甲虫固定根以 250 节点比较 DOP1/DOP2，动作、选择、评分、快照和回合标注一致且形成真实并发；同时验证 `PAELS_LEGION` 完成/中止出牌后都能清理瞬时引用并稳定 Fork。runId `9678ee908aac427e9fbc5a6d7b17e4cf` | 2026-08-30 |

## 0.21.3

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-164623-TRANSFORM-ENTERED-COMBAT-CONTINUATION` | 通过 | 先打出一张虚无牌，再把手牌固定变换为费用随本场虚无牌变化的牌；原版与预测的变换入场、费用、打出后牌堆和完整状态严格一致，并启用增量核对。runId `c84456dc9b214cf7a7542579a8cd521f` | 2026-08-30 |
| `BATCH-164623-TURN-SCOPED-CARD-HISTORY-CONTINUATION-FINAL` | 通过 | 同一夹具覆盖两个回合：迭代在新回合重新响应首张状态牌；上一回合零费攻击不污染本回合施加的野性。原版与预测完整状态严格一致，并启用增量核对。runId `98adb2c4b3df4c65aed9cd3fb850512b` | 2026-08-30 |
| `BATCH-164623-ORB-DEATH-POWER-ORDER-CONTINUATION` | 通过 | 双巨斧机器人中，累计 `30` 伤害的暗黑球连续激发：第一击击杀后先触发另一只敌人的 CrabRage Power，第二击再消耗所得格挡；原版与预测完整状态严格一致。runId `99dc41bee83243cf9bad3c6b32826c0b` | 2026-08-30 |
| `BATCH-164623-LAGAVULIN-REUSE-FINAL2` | 超时，不计通过 | 从母体问题包战前存档恢复并使用原高预算；第 1 回合搜索未在 `360` 秒总时限内完成，没有进入自动部署，不能证明第 7 回合复用。runId `c4d32e6bde1d4e01a122dce45caa69b6` | 2026-08-30 |
| `BATCH-164623-LAGAVULIN-REUSE-SHORT` | 超时，不计通过 | 同一战前状态改用固定 `8` 秒短搜并启用增量核对；仍在第 1 回合达到 `180` 秒总时限，没有观察到第 7 回合复用。runId `a5edd7975b4645baa6dd8cde97bfe0d3` | 2026-08-30 |

## 0.21.2

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POST0211-ADAPTIVE-DOP-PRESET-PERSISTENCE` | 通过 | 默认并行度对 `1/2/3/4/32` 个逻辑处理器分别解析为 `1/2/2/4/4`；高档预设在设置页重新加载并提交未修改数值后仍保持高档。控制器生命周期和首回合自动战斗同时通过。runId `25fd0bdbeaa9450ca13ca6eebec74d95` | 2026-08-30 |

## 0.21.1

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `V0211-PARALLEL-OFF-RECOVERY-UI` | 通过 | 设置面板成功创建并行度选择框；并行搜索失败提示同时包含上传问题包与切换“关闭（单线程）”，串行搜索失败只提示上传，不误报并行恢复建议。控制器停止/恢复生命周期和首回合自动战斗同时通过。runId `598166aaeb19404fa75603c35a5921fa` | 2026-08-30 |
| `POST021-SMARTFORMAT-DIRECT-PATCH-ATTEMPT` | 失败实验，已撤回 | `LocManager.SmartFormat` 含异常过滤器，Harmony 无法生成 Prefix/Finalizer 或 Prefix-only 改写，CombatSolver patch 整体回滚；runId `f15cebdaa235481d970942bbdf5c7232`、`b8f16a2de9ea4779a738604ab8a247c9`、`0e0f3425cc104417bdf083ccf6cdcca0`，不计回归通过 | 2026-08-30 |
| `POST021-POWER-DYNAMIC-WARMUP-SMOKE` | 通过 | 主线程物化与 Power 惰性变量 guard 正常加载；铁甲战士首回合自动结束 1 HP 小爬虫战斗，没有 patch 回滚。runId `8b4446b6e4a34fa9b68bcdbd971441af` | 2026-08-30 |
| `POST021-INFESTED-PRISM-SMARTFORMAT-DOP4` | 通过 | 从玩家包恢复受感染棱镜战前存档、手牌与 RNG；DOP4 短搜约 `2976.8 ms` 返回 5 个可执行动作和第 4 回合路线，没有集合并发异常。runId `cb71d23a0baf4046b65dc0348c355041` | 2026-08-30 |
| `POST021-POWER-DYNAMIC-DOP1-DOP2` | 通过 | 固定长线根比较 DOP1/DOP2；动作、选择、评分、展开、转移、全部非时序剪枝、快照、continuation 与回合标注一致。runId `614d4ac8af3749cd92b9676662956dae` | 2026-08-30 |
| `POST021-BASELIB-PARALLEL-ENCHANTED-TERROR-EEL` | 通过 | 完整加载 BaseLib `3.4.5`，从玩家包恢复骇鳗首回合手牌、牌序、迅捷生存者、螺旋防御与 RNG；DOP4 短搜正常返回第 8 回合可执行路线，没有重复键异常。runId `ad3c8c31ef054a459a3f27dc9c45b16f` | 2026-08-30 |
| `POST021-BASELIB-ENCHANTED-DOP1-DOP2-EQUIVALENCE` | 通过 | 同一附魔牌根在 BaseLib 完整加载时比较 DOP1/DOP2；动作、选择、评分、展开、转移、非时序剪枝、快照、continuation 与回合标注一致。runId `258ec69dbc1b45c8bb6afa809623f4b6` | 2026-08-30 |
| `POST021-BASELIB-PARALLEL-ENCHANTED-TERROR-EEL-FULL-AUTO` | 通过 | 玩家包根以 DOP4、Instant/0 秒完成整场自动部署，第 8 回合结束，计划外重算 `0`，没有重复键异常。runId `75cbc36d31d045778a72fa3acf60c080` | 2026-08-30 |

## 0.21.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POST0201-SCRAPE-NEGATIVE-COST-FINAL` | 通过 | 刮削+依次抽到普通费用牌与负费用贪婪；预测与原生的手牌、弃牌堆及其余完整状态严格一致。runId `a5117c6888d0438693c0334775c720e1` | 2026-08-30 |
| `POST0201-WHISTLE-STUN-FOLLOW-UP-FINAL` | 通过 | 吹哨打断史莱姆狂战士的呕吐脓水；眩晕结束后实机与预测都恢复呕吐脓水，不再跳到狂怒痛击。runId `500c0e56d7fb4dd58977040a6dd92610` | 2026-08-30 |
| `POST0201-BRAND-POST-CHOICE-POWER-FORK-FINAL` | 通过 | 升级烙印完成原生手牌消耗选择并获得力量后，模拟状态立即满足稳定 Fork 边界。runId `7ea11c09cf614e28922104cf10875697` | 2026-08-30 |
| `POST0201-SLIMED-BERSERKER-WHISTLE-REUSE` | 通过 | 从史莱姆狂战士问题包恢复牌组、遗物与 RNG；严格增量搜索实际打出吹哨，连续复用到第 3 回合，计划外重算 `0`。runId `e6232efae6494f2cb91b14d6790c3c23` | 2026-08-30 |
| `EXHAUST-CLUTTER-SENTINEL` | 通过 | 贪婪在第 1 回合消耗一张手牌；候选含虚无与两张防御。用于确认移除排序与牌库杂物计分改动后路线不退化，贪婪仍在第 1 回合打出且随后仍能打出防御。本场景是不可退化哨兵，不是行为翻转证据——基线在同一夹具上已经选择消耗防御。 | 2026-09-05 |
| `POST0201-TEST-SUBJECT-SCRAPE-SCAVENGE-REUSE` | 通过 | 从实验体问题包恢复牌组、遗物与 RNG；第 2 回合打出刮削，内存清理随后在 4 张原生手牌候选中成功选中贪婪，严格增量复用到第 3 回合且计划外重算 `0`。runId `ec1c11fa2a444eda836520d1fe18e829` | 2026-08-30 |
| `POST0201-DECIMILLIPEDE-CURRENT-DEEP-BASELINE` | 部分，不计完整通过 | 当前源码从千足虫问题包根完成搜索，没有复现旧版待结算力量导致的 Fork 异常；结果只有死亡路线，测试因可执行动作下限断言失败，因此不作为完整战斗证据。runId `dbf082665e984821b835e665889cc168` | 2026-08-30 |
| `MULTICORE-NIBBITS-DOP1/DOP2-AB` | 通过（headless 迭代基准） | 固定双小啃兽快照在上游 DOP1、当前 DOP1、当前 DOP2 均为 `573` 展开、`2759` 转移、同一 5 回合零战损/零药路线。独立冷进程求解耗时 `1424.5 / 1449.3 / 955.2 ms`，当前 DOP2 缩短约 `34.1%`；累计分配 `177,425,048 / 177,767,800 / 179,165,392 B`，DOP2 并行遥测为 `286 waves / 572 items / max concurrency 2`。runId `e8bfb02cf9714c98ae9230842c756ff3`、`9314522060164435b551c18f16d7d093`、`ca238f1462874e7d8c67069b4b71077c`；不替代 Steam 可见性能门槛 | 2026-08-30 |
| `MULTICORE-POLICY-EQUIVALENCE` | 通过 | 带初始力量与 `SURVIVOR` 弃牌选择的固定根先执行冷缓存 DOP2，再执行 DOP1；递归核对完整动作/选择、评分、展开、转移、各类剪枝、快照、continuation 与回合标注。DOP1 并行遥测全零，DOP2 实际最大并发不小于 2。runId `3f8d240bda57441c8352bfb749424017` | 2026-08-30 |
| `MULTICORE-FULL-AUTO-DOP2` | 通过 | 默认并行路径完成双小啃兽整场自动部署，第 5 回合结束，第 3 回合精确复用；零药、零预计战损。runId `eda5d69feb774389a8990d788434ac6d` | 2026-08-30 |
| `MULTICORE-EARRING-NESTED-CHOICE-DOP2` | 通过 | 工具盒形成首回合多根，低语耳环连续自动打出高密度 `SURVIVOR` 并消费嵌套弃牌选择；精确原版状态检查通过，搜索记录 `4 waves / 8 items / max concurrency 2`。runId `f577be1a8e0a4ebb84aa115d3ab28734` | 2026-08-30 |
| `MULTICORE-NIBBITS-DOP4` | 通过 | 四条 lane 完成固定双小啃兽搜索，仍为 `573 / 2759`、同一路线与全部剪枝指标；并行遥测 `159 waves / 572 items / max concurrency 4`。runId `54243577aa984e8eb68f2d242a216eb9` | 2026-08-30 |
| `MULTICORE-INCREMENTAL-FORCED-SERIAL` | 通过 | 请求 DOP4 并开启严格增量回放；首轮完整结果的 `parallel_waves / work_items / max_concurrency` 均为 `0`，逐转移回放一致，第 5 回合结束且计划外重算 `0`。runId `96b7d9fdfbb245d68f4effefcd748b1e` | 2026-08-30 |
| `MULTICORE-V020-FINAL-POLICY-EQUIVALENCE` | 通过 | 合并 `upstream/main` 的 `v0.20.0` 后，以固定 250 节点先跑 DOP2 再跑 DOP1；动作、选择、评分、展开、转移、全部非时序剪枝、快照、continuation 与回合标注一致。门禁同时断言两档都实际释放节点上限丢弃的 Simulator；runId `c9052d90aa504ae0ba183a5089aa0e07` | 2026-08-30 |
| `MULTICORE-V020-FULL-AUTO-DOP2-FINAL` | 通过 | 合并态默认 DOP2 完整自动部署双小啃兽，第 5 回合结束、第 3 回合精确复用，零药、零预计战损、计划外重算 `0`；runId `f48b0cebd842466594bd8f30789d589a` | 2026-08-30 |
| `MULTICORE-MECHA-DOP4/6/8-WARM-NOCACHE` | 通过（headless 迭代基准） | 同一暖进程固定 `4319 / 33087 / 18399` 工作量与第 7 回合/28 战损路线；DOP4/6/8 为 `5451.7 / 4281.3 / 3813.0 ms`，累计分配为 `4,404,184,848 / 4,412,016,024 / 4,415,450,000 B`。runId `db33a20aa0764f068b34a3028ec06beb`、`eab8b0f052634b82be807b3af9ddaec9`、`38e6aed2c8834a4fa0cea8a35e18b6f5`；不替代 Steam 可见性能门槛 | 2026-08-30 |
| `MULTICORE-V020-MECHA-DOP8-FINAL` | 通过（headless 冷进程） | 当前合并态 DOP8 保持 `4319` 展开、`33087` 转移、`18399` 选牌分支与同一路线；`6366.8 ms / 4,441,356,192 B`，实际最大并发 `8`，结果时工作集 `6,011,162,624 B`，GC `0 ms`、最大帧 `18.0 ms`、无 `>50 ms` 帧。runId `2c38044b3fb44bef85b66032b488271c`；可见 Steam 启动未形成游戏进程，故不写成生产帧率结论 | 2026-08-30 |
| `PR8-MERGED-DOP1-DOP2-EQUIVALENCE` | 通过 | PR #8 合并到本地主线后，以固定 250 节点比较 DOP1/DOP2；动作、选择、评分、展开、转移、全部非时序剪枝、快照、continuation 与回合标注一致，两档都实际释放节点上限丢弃的 Simulator。runId `c4e1343ad44843229483f97e3d04265a` | 2026-08-30 |
| `PR8-MERGED-DOP2-FULL-AUTO` | 通过 | 合并态默认 DOP2 完整自动部署双小啃兽，第 5 回合结束、第 3 回合精确复用，计划外重算 `0`。runId `21f6375fb9d5499a99d992c2eb7a0878` | 2026-08-30 |

## 0.20.0：在线问题包、跨平台测试与选牌修复

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `LINUX-HEADLESS-INSTANT-AB` | 通过 | 同一 PID `66703` 先后运行 `Normal / Instant / Normal`；内部耗时分别为 `7098.0 / 2917.7 / 7064.5 ms`，Instant 稳定节省约 `59%`。三次均应用并恢复测试速度，runId `e8dede980dbb4daab57cc1c6a1d71730`、`8851e4147f9940e2921088474c8c7e5f`、`42148ebf930243b59d9230d2cb68f913` | 2026-08-29 |
| `LINUX-HEADLESS-REUSE-REGRESSION` | 通过 | 同一 PID 连续通过怪物严格差分、铁甲战士跨回合复用、跨角色切换到星辰、再切回铁甲战士完成精灵药自动战斗；对应 runId `74da3eefb1e841219c5721323dad83d6`、`dbd5cf92f61043b28d38978aa8307a6e`、`8605ff494b604c019323e222f5247314`、`261c9007073d40709a5f388d698063e9`，两项复用场景和完整战斗的计划外重算均为 `0` | 2026-08-29 |
| `LINUX-HEADLESS-LIFECYCLE` | 通过 | 原生日志为 `N/A (headless) / VRAM 0B`；marker 验证 PID starttime、隔离环境及 DLL/manifest 哈希。无变化复用同一 PID；Release 重建后输出 `UNATTENDED_RESTART reason=mod_changed` 并自动换 PID。失败退出约 `510 ms`，最终进程、marker、临时 RitsuLib 投影均清理 | 2026-08-29 |
| `HEADLESS-MATRIX-CANONICAL-0180` | 通过 | Linux 以 `--continue-on-failure` 按文档原有生命周期边界运行全量矩阵：`MATRIX_END total=228 attempted=225 passed=225 failed=0 skipped=3 cleanup_exit_code=0 elapsed_ms=1787606`，即 `29:47.606`；`52` 次冷启动、`173` 次安全复用，仅跳过缺少本机外部快照的 `3` 个场景 | 2026-08-30 |
| `LINUX-MECHA-MEMORY-CALIBRATION-0180` | 通过 | 固定机甲骑士快照在 Linux 原生 headless 的首轮搜索为 `14282.3 ms / 4,390,908,424 B`（累计分配，非峰值内存），第 `7` 回合结束、第 `3` 回合开始复用，runId `531a3a280ab24e89bad2a3536da8ecd6`。Linux 分配门槛按平台差异校准为 `4,500,000,000 B`，余量 `109,091,576 B`（`2.485%`）；Windows 命令的 `4,300,000,000 B` 门槛保持不变 | 2026-08-30 |
| `PR6-ARMAMENTS-IMPLICIT-INTEGRATED` | 通过 | 手牌仅有武装和未升级打击；原版隐式升级唯一候选后，部署器按请求时冻结的身份核销同一实例，再打出升级后的打击并于首回合结束战斗。原生选择 `visible=0 / selected=1 / search=0`，增量回放一致，计划外重算 `0`。runId `d1bc59759edf4c91a9c89f4bd6e6b2d4` | 2026-08-30 |
| `PR6-RNG-DETERMINISTIC-A/B` | 通过 | 同一 PID、相同 seed `PR6DETERMINISTIC` 连续两次建立史莱姆战，敌人均为 `LEAF_SLIME_S / TWIG_SLIME_M / TWIG_SLIME_S` 且生命上限为 `12 / 27 / 7`；两次均第 2 回合结束、计划外重算 `0`，第二次明确复用同一测试进程并正常退出。runId `cc320b89536149398707300e4c9258be`、`38c0fde339ae49e0b45524d3ce2c545d` | 2026-08-30 |
| `PR6-VIGOR/SELF-KILL-INTEGRATED` | 通过 | 骇鳗猛烈摆动携带活力时，完整攻击前/攻击后生命周期与原生严格一致，runId `0eac2a0e2d904b8bb332fb1c76c18d57`；七项怪物行动差分含毒气炸弹自爆并通过死亡结算，runId `edd957f4731445e9a300f9c0cad9646f` | 2026-08-30 |
| `PR6-EMOTION-CHIP-EXTRA-TURN-INTEGRATED` | 通过 | 琥珀灰触发额外回合；当前回合使用放血受伤后，情感芯片的损血窗口在跳过敌方阶段时正确滚动，额外回合开始的充能球被动与原生完整状态一致，实际伤害 `3`。runId `aaa8223a916b4b24b8c2983596010e31` | 2026-08-30 |
| `TOASTY-FIRST-TURN-USER-BOUNDARY-0190` | 通过 | 烘焙手套原生手牌页显示后开始搜索；计划就绪时仍为 `Selected=0 / CardsPlayed=0`，模拟玩家启动后才确认选择。严格增量/完整回放一致，开启结束回合变差复核后完整自动执行到第 2 回合，计划外重算 `0`。runId `a2a1fd688e71465d9458c5cbb1c743d4` | 2026-08-30 |
| `TOASTY-PHANTASMAL-BUNDLE-0190` | 通过 | 使用花园幽灵鳗问题包的战前牌组、遗物与 RNG；首回合计划展示时未选牌、未出牌，玩家启动后完整自动执行到第 5 回合。结束回合复核开启，计划外重算 `0`；严格完整回放从同一份首回合准备选择起步。runId `0451b77331a94c96ae507e2ccdb4603a` | 2026-08-30 |
| `UPLOAD-HARDENING-PR4-FINAL` | 通过 | 真实问题包导出保持完整夹具且移除联系QQ与本机绝对路径；本地假服务验证 multipart 三字段不变、字节进度到 `100%`、非 JSON/数字编号的成功响应回退客户端提交编号、超长描述联网前失败、超长错误响应截断并折叠换行。设置面板同时存在隐藏初始进度条与单实例上传按钮状态。导出/脱敏与上传协议 runId `8a4144cb3a264bb7abf33ea6461ddcb7`；最终 UI/进度状态 runId `c90d6b854c3446ffad4c09b4da1753bf`；最终成功响应兼容矩阵 runId `85861ebc16a04cb09fbd9894bfb3d088` | 2026-08-30 |
| `UPLOAD-PROGRESS-CANCEL-CONFIRMATION-NEXT-FINAL` | 通过 | 取代上一条中“任意成功响应回退客户端编号”的旧口径：文件发送完成只显示到 `95%` 并进入“等待服务器确认”，只有反馈编号与实收字节数匹配才确认成功。假服务分别在正文传输中和等待回执时取消，任务均在两秒内结束；无效回执与大小不一致均保留本地包。真实接收服务 test ZIP 返回 HTTP `201`、反馈编号 `9264c0f65854423e8254de5ff5e5449f` 并确认 `259 B`。runId `f328c2f5fe9f4ccca11826bcbb8b1f6c` | 2026-08-30 |
| `UPLOAD-DIRECT-STATE-OWNERSHIP-NEXT` | 通过 | 正式上传不继承游戏进程中指向失效 `127.0.0.1:7890` 的代理；同一环境下直连 test ZIP 于 `427 ms` 返回 HTTP `201`，反馈编号 `d186e8a6495d4f0291529595667e0a43`，实收 `259 B`。孤立状态转移夹具验证活动态与按钮文字可在同一主线程回调内切回空闲；后续实机证明该全局回调本身可能不被消费，最终实现由下一条面板完成邮箱回归取代。runId `ba1c33c4e68946129314dff1a61928cf` | 2026-08-30 |
| `UPLOAD-PANEL-MAILBOX-LIFECYCLE-NEXT` | 通过 | 实机已经记录 HTTP `201 / 1,340,897 B` 后仍卡等待，证明全局 dispatcher 未消费上传终态。上传会话改由设置面板完成邮箱独占；成功与取消两条路径都在面板进程中消费终态、收起进度条、释放令牌并恢复空闲按钮，“正在取消…”不再等待搜索 dispatcher。runId `fa5ba87bf06d4dac9c17b052192d0be8` | 2026-08-30 |
| `KNOWLEDGE-LIVE-END-RISK-BASELINE` | 失败（修复前基线） | 开启结束回合实时战损复核后，知识恶魔敌方回合诅咒计划被放入普通选牌游标；真正提交结束回合前稳定抛出“回合开始仍有 1 个计划选牌没有触发”，随后测试超时。runId `bc12f0e513ec4634b5c2f3dea0f84a66` | 2026-08-30 |
| `KNOWLEDGE-LIVE-RISK-CHOICE-PHASE-NEXT` | 通过 | `MONSTER-MOVES-BATCH-007` 在既有 10 项严格差分后，额外强制知识恶魔诅咒行动并向实时战损复核提供 `MIND_ROT` 计划；复核按来源和次数消费该计划，诅咒计数精确前进 `1`。runId `901066cdbaaa41329876325cd8a06ad5` | 2026-08-30 |
| `KNOWLEDGE-LIVE-END-RISK-FIXED` | 通过 | 开启结束回合实时战损复核，首轮路线计划 `MIND_ROT`；提交结束回合后原生页面完成选择、玩家获得对应 Power，计划外重算 `0`。runId `a43e0dc90cad444989efde50a99ba33b` | 2026-08-30 |
| `KNOWLEDGE-LIVE-END-RISK-INCREMENTAL` | 通过 | 与上一项相同的实时战损复核路径同时开启严格增量校验；初始搜索、完整回放、结束回合后的原生 `MIND_ROT` 选择一致，计划外重算 `0`。runId `b199bf29f0054c1789e1a2c2d2886435` | 2026-08-30 |
| `KNOWLEDGE-ANGER-BUNDLE-FIXED` | 通过 | 从铁甲战士问题包恢复战前存档、精确牌堆和 RNG；完整自动执行到第 6 回合结束，实机打出愤怒并通过两次知识恶魔原生选牌，计划外重算 `0`。runId `28ac269f1be64674976a8d5075965947` | 2026-08-30 |
| `KNOWLEDGE-TOASTY-BUNDLE-FIXED` | 通过 | 从静默猎手问题包恢复战前存档、精确牌堆和 RNG；开启实时战损复核后完成首个知识恶魔原生选牌并获得瓦解，计划外重算 `0`。runId `9d988afbd8f240e3af519755d35aff3b` | 2026-08-30 |

## 0.19.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `MONSTER-ATTACK-VIGOR-LIFECYCLE-POST018` | 通过 | 骇鳗攻击执行完整攻击前/攻击后生命周期，活力层数与原生严格一致。runId `9994be6016f345f892c6dbc3040384e2` | 2026-08-30 |
| `POST018-TERROR-EEL-CONTINUATION` | 通过 | 骇鳗完整自动执行到第 6 回合，跨回合计划外重算 `0`。runId `acd9c91efb7443edb5a19737d7aba92b` | 2026-08-30 |
| `POST018-TERROR-EEL-CONTINUATION-INCREMENTAL` | 超时，不计通过 | `360s` 上限内仍停留于首回合，没有完成断言；不作为增量等价证据。runId `7c53821b28fd4f84b337b673d9507adb` | 2026-08-30 |
| `POST018-FABRICATOR-CHOICE-AND-AI-REUSE` | 通过 | 条件行动包含自身的队友计数；暴政选择按玩家回合开始阶段接管，完整自动执行到第 9 回合且零重算。runId `766852f4a7574342959cdf9ca7e940b0` | 2026-08-30 |
| `POST018-DECIMILLIPEDE-UPGRADE-CHOICE-DEPLOY` | 通过 | 千足虫升级选牌完成原生部署，完整自动执行到第 4 回合且零重算。runId `dc190ef3c1784f27b0d9e2ef85f49b80` | 2026-08-30 |
| `POST018-OVICOPTER-CONTINUATION` | 通过 | 产卵飞虫完整自动执行到第 5 回合，计划外重算 `0`。runId `c8e98d81c2c24c7d9de1baeb2d67e6ce` | 2026-08-30 |
| `POST018-BYGONE-EFFIGY-TOOLS-CHOICE` | 通过 | 必备工具选择按下一玩家回合阶段消费，完整自动执行到第 7 回合且零重算。runId `1dbadbe59d9147879d62f44d38a55cf0` | 2026-08-30 |
| `POST018-KNOWLEDGE-ANGER-END-RISK` | 通过（历史邻接覆盖） | 完整自动执行到第 8 回合且零重算，但该场景没有让实时战损复核与知识恶魔敌方回合选择同时进入同一个模拟根，不能覆盖本次问题；由下一版本的实时复核专项取代。runId `2612caee23e14c42a274aa7567ae2251` | 2026-08-30 |
| `POST018-SCROLLS-AUTO-CHOICE-PHASE` | 通过（邻接覆盖） | 三卷轴怪当前路线首回合结束战斗，自动执行不中止且零重算；第 2 回合必备工具由独立阶段回归覆盖。runId `a9f7d23ea7d5439c97a78ddf327d520f` | 2026-08-30 |
| `POST018-SOUL-NEXUS-GRID-SCROLL` | 通过 | 原生 37 张卡牌网格滚动到底部并通过真实节点选择主宰，完整自动执行到第 7 回合且零重算。runId `b1a13ffbde4c46a6b64825cb2a3e8049` | 2026-08-30 |
| `POST018-OVERGROWTH-ENTROPIC-CONTINUATION` | 通过（未复现旧重算） | 从蔓生爬虫问题根完整执行到第 2 回合且零重算；旧包在搜索期间实机药水栏与 RNG 已变化，因此保留为证据不足。runId `17fb59dfc1284928912eba1644b1ead5` | 2026-08-30 |
| `POST018-TEST-SUBJECT-LOOT-CURRENT` | 通过 | 满手后的战利品生成与后续回放完整执行到第 12 回合，计划外重算 `0`。runId `7d95638f6d6f4a058221cb3507c05187` | 2026-08-30 |
| `POST018-AEONGLASS-CHOICE-CURRENT` | 通过 | 永世沙漏原生选牌与重新接管完整执行到第 6 回合，计划外重算 `0`；更优路线仍属暂缓项。runId `c51d8cf8f47e43ccb7e4a922361688d8` | 2026-08-30 |
| `POST018-ENTOMANCER-CLUMSY-CONTINUATION` | 通过 | 养蜂人塞入笨拙后的牌堆和洗牌续用一致，完整自动执行到第 4 回合且零重算。runId `066e8d5e64ff4eb3a2b0b2e8c4853685` | 2026-08-30 |
| `POST018-KNIGHTS-FAILURE-CURRENT` | 通过（入口覆盖） | 当前路线首回合结束，最终回放入口不再失败且零重算；不宣称复现旧 9 回合路线。runId `d19d34b506cd4b67a863e124293bfaf1` | 2026-08-30 |
| `POST018-DOMINATE-VICIOUS-ORDER` | 通过 | 主宰施加易伤后，凶恶先抽牌、地狱狂徒先自动打出攻击，随后才按当前易伤获得力量；原生与预测完整状态一致。runId `ad8f66a8594d436dac6a1c5fafb0e313` | 2026-08-30 |
| `POST018-HEX-DEATH-COVERAGE-FINAL` | 通过 | 两只幽灵骑士连续施加恶咒，后施加者死亡保留、初始施加者死亡移除；Power 与逐张卡牌状态三段差分一致。runId `d53826f1d06a487fbca60ffb740892f7` | 2026-08-30 |
| `POST018-CUSTOM-OVERRIDE-ASSERTION` | 通过 | 中档基础上覆盖短搜/深搜单节点出牌分支为 `23/37`、No-GC 为 `7 GB` 后，预设身份与三个实际值均按自定义配置断言。runId `011e9c0e7daa4564867e8195a6299a11` | 2026-08-30 |
| `POST018-BYRDONIS-EXACT-DEFERRED-BASELINE` | 通过（质量基线） | 从问题包回放状态恢复精确牌堆与 RNG，当前路线预计战损仍为 `55`，玩家手操上界为 `41`；只固化差距，不计作策略修复。runId `b4e2837cecc04cb59dad6a4e4be6cb37` | 2026-08-30 |
| `POST018-TEST-SUBJECT-EXACT-DEFERRED-BASELINE` | 通过（质量基线） | 从问题包回放状态恢复精确牌堆与 RNG，当前路线预计战损仍为 `20`，玩家手操上界为 `0`；只固化差距，不计作策略修复。runId `cad36ba7007d40e392ee882c49be2308` | 2026-08-30 |
| `POST018-DETAILED-PLAN-REPLAY-STATE` | 通过 | 详细诊断在最终路线回放与实机部署动作后写出能量、手牌、抽牌堆、弃牌堆、消耗堆及敌方生命/格挡；最小战斗完整结束。runId `1e83a3bc0d864e278fba5a8a20d66b96` | 2026-08-30 |

## 0.18.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `MAKE-IT-SO-FINISHED-HISTORY-018` | 通过 | 独立回合从 0 次技能历史开始，逐张断言如此甚好在前两张技能后留在弃牌堆、第 3 张后回手，实机与模拟一致。runId `5d226c100ea949f1bc498a01a7961106` | 2026-08-29 |
| `NEUROSURGE-MUTABLE-POWER-018` | 通过 | 精神过载及同批 47 项卡牌施加 Power 后，生命、能量、牌堆、Power 和怪物状态实机差分一致。runId `fa74bbbe3c4245188cde369d7bcf6144` | 2026-08-29 |
| `LIVE-END-TURN-RISK-CHOICE-REUSE-018` | 通过 | 惊逃在结束回合风险复核中自动打出头槌，复用路线选择将盛怒置顶，选择顺序与消费数严格一致。runId `216d8643891f442689074fa5f6f7954e` | 2026-08-29 |
| `FOREGONE-CONCLUSION-DELAYED-DRAW-018` | 通过 | 既定事项页面暂停回合准备时，先执行原版 `AfterSideTurnStart` 再确认选牌；下回合多抽一张在正式抽牌前移除，选中 3 张后总手牌为 8。实机与模拟严格一致。runId `a65c0460238c467c803394b5c07a59c5` | 2026-08-29 |
| `OWL-MAGISTRATE-TURN-SETUP-REUSE-018` | 通过 | 从猫头鹰法官问题包战前状态完整自动执行，既定事项、辉光和回合准备选牌跨回合保持一致，第 5 回合结束前计划外重算 `0`。runId `cd2f163cd0cd41f5af6183fe9b4fec5c` | 2026-08-29 |
| `SPECTRUM-FOREGONE-ORDER-018` | 通过 | 光谱偏移先生成随机无色牌，既定事项随后把 3 张牌移入手牌，再执行普通抽牌；有序牌堆、Power 和 RNG 与实机严格一致。runId `96f54d71babf4c3ba4871dd5400953a8` | 2026-08-29 |
| `KNOWLEDGE-POWER-REAPPLICATION-ORDER-018` | 通过 | 从知识恶魔问题包战前状态完整自动执行；敌方诅咒选择会话正常退出，第 11 回合重新施加既定事项后保持正确 Power 监听顺序，第 12 回合结束战斗，计划外重算 `0`。runId `bdf89c2bf77341ee8da1c7f68b2d2161` | 2026-08-29 |
| `CURRENT-BUNDLE-DIRECT-COVERAGE-018` | 通过 | 当前源码直接重放地道虫、外骨骼虫、感染棱柱、活体盾与高塔炮手，以及连枷骑士、幽灵骑士与魔法骑士问题包，分别越过原初始化、计划外选牌、实时风险选牌、精神过载动作回放及如此甚好部署找牌错误；runId `edce410703414934a3f3259429b10d26`、`6a32338eb2f24b44aa45cf731c845163`、`417c1b5707924606816351b1aa339c21`、`b2d4cb91395e46ccb944da5a8558192b`、`0d737a4acb9e4a85a9365f296562311c` | 2026-08-29 |
| `POWER-ROOT-INTERNAL-STATE-018` | 通过 | 鬼祟珊瑚群第 2 回合继承本回合已受到的 `9` 点伤害，搜索与实机的回合伤害上限一致；完整自动战斗在第 5 回合结束，计划外重算 `0`。runId `b126492a542c4c3d85bc47f0bffe0b1c` | 2026-08-29 |
| `EMOTION-CHIP-HISTORY-ROLL-018` | 通过 | 情感芯片触发充能球后保留上回合失去生命的记录，直到敌方回合结束再滚动；完整自动战斗在第 5 回合结束，计划外重算 `0`。runId `e265c0a6887f48ea892c2e5489236721` | 2026-08-29 |
| `FAN-OF-KNIVES-SHIV-TARGET-018` | 通过 | 刀扇生效后，小刀按全体攻击生成无目标动作并与实机一致；永世沙漏问题包完整自动执行到第 7 回合，计划外重算 `0`。runId `e825a7e0fe244d5b8be45086c3f3d7ef` | 2026-08-29 |
| `UPROAR-ECHO-FORM-AUTOPLAY-018` | 通过 | 回响形态重放骚动时，骚动自动打出的集中打击读取已经开始的外层出牌系列，因此只结算一次；敌人生命、集中、牌堆、能量与 RNG 的原生/预测完整状态一致。runId `06550cd755b246c7b044865469541422` | 2026-08-29 |
| `TEST-SUBJECT-GC-ECHO-FINAL-018` | 通过 | 实验体原问题包在首轮搜索与全自动请求重叠时只执行一次 No-GC 滚动回收，不再循环触发 `before_next_search`；随后连续复用并在第 8 回合结束，计划外重算 `0`。runId `913d3393e919438fbf2d7635ce318b2b` | 2026-08-29 |
| `DECISIONS-REPEATED-CHOICE-BUDGET-018` | 通过 | 抉择，抉择的三次自动出牌共享整张牌的手牌选择分支预算；储君实验体原包首轮短搜返回后连续精确复用 10 回合，第 11 回合结束，计划外重算 `0`。runId `c8c9f6f2edda40bd87bc2bf5e6b20520` | 2026-08-29 |
| `TURRET-RELIC-ANNOTATION-018` | 通过 | 活体盾与高塔炮手原问题包的首轮最终遗物标注正常完成；完整自动执行到第 4 回合，计划外重算 `0`。runId `6d1e48b181824f0fbebc43525c959403` | 2026-08-29 |
| `MECHA/SOUL-NEXUS-REPLAN-018` | 通过 | 机甲骑士与静默猎手对灵魂枢纽的两份原问题包分别完整自动执行到第 4、5 回合，计划外重算均为 `0`。runId `cb31ec9272284f579bbf6efa942281e0`、`2b11153f74af4766848d585e8372d62f` | 2026-08-29 |
| `WATERFALL-SMART-MARGINAL-POTION-018` | 通过 | 瀑布巨兽原问题包的智能用药路线只保留痊愈药水；独立无药反事实确认预计省血 `10/9`，再生药水不再借用另一瓶药水的收益通过门槛。runId `dbce824e7919490882bd6022f2ebc394` | 2026-08-29 |
| `MYTES-SMART-BLOCK-POTION-018` | 通过 | 异螨原问题包在第 2 回合实际使用格挡药水，完整自动执行到第 5 回合，预计省血 `9/9`，计划外重算 `0`。runId `1959c1dd79c743958a6f921f727d6cae` | 2026-08-29 |
| `LOST-FORGOTTEN-REQUIRED-POTION-BOUNDARY-018` | 通过 | 失落之物与遗忘之物原问题包在“至少使用一瓶”下正常返回一瓶药水的边界路线，不再把已展开的流动铜液与能力药水误报为没有可执行路线；该短预算结果仍为死亡边界。runId `d3fa5886b45e46948093c0d42518f79d` | 2026-08-29 |
| `QUEEN-POTION-POLICY-DISABLED-018` | 通过 | 女王原问题包全程记录“禁用药水”，路线按设置保留稳定血清与固化药水；玩家手动使用稳定血清后，本局已用药数正确增加。该项是设置行为，不是药水适配失败 | 2026-08-29 |
| `TEST-SUBJECT-REQUIRED-POTION-QUALITY-018` | 通过 | 实验体同一起点、高档预算成对复跑：智能模式 0 瓶、预计战损 `78`；至少使用一瓶时选择肌肉药水、预计战损 `74`，不再发生强制用药后战损上升。runId `12dc53ed6dc1469d9e0bae71a1b14b2e`、`6a4b981c9c7c4fb7999304980c33f00c` | 2026-08-29 |
| `INSATIABLE-REQUIRED-POTION-BOUNDARY-018` | 通过 | 无厌沙虫原问题包在“至少使用一瓶”下返回包含第 4 回合易伤药水的边界路线，不再因长战斗尚未搜索到完整胜利而误报没有可执行路线；该结果仍为死亡边界。runId `6563e660595d4b35932719281320b5c3` | 2026-08-29 |
| `HISTORY-COURSE-STAMPEDE-PAELS-EYE-018` | 通过 | 历史课在惊逃的回合末自动攻击之后记录上一回合最后一张非复制攻击牌；佩尔之眼只统计玩家主动出牌。永世沙漏原问题包完整全自动执行到第 5 回合，包含额外回合，计划外重算 `0`。runId `0b1d38c391d04a36a7a9cc14c5122d5f` | 2026-08-29 |
| `AEONGLASS-EXACT-PILE-ART-ROUTING-018` | 通过 | 从永世沙漏问题包战前存档恢复跑局与 RNG，并固定首手和 29 张有序抽牌；路线实际打出灵动步法+，预计战损 `27`，低于包内原路线的 `42`。runId `0c2c35a7510b4e0f9846fca740aa80c1` | 2026-08-30 |
| `QUEEN-ART-OF-WAR-LANE-REGRESSION-018` | 通过（邻接回归） | 女王战前重建在中档预算、智能用药下预计战损 `18`，低于回归上限 `26`；该场景只证明孙子兵法专用通道没有影响无关战斗，不作为第 18 项“更优解”的同根证明。runId `8d69269258924485aef31a0325d1d3c1` | 2026-08-30 |
| `QUEEN-EXACT-PILE-MANUAL-QUALITY-018` | 通过（未追平手操） | 固定女王问题包的 7 张首手和 32 张有序抽牌后，当前路线主动打出余像与计划妥当，预计战损 `9`；包内旧求解路线为 `26`，玩家手操路线实测为 `5`。runId `598bc5d8e86b4094bbf96c04942e192e` | 2026-08-30 |

## 0.17.2

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `QOL-CONTROLLER-STOP-172` | 通过 | 搜索时主操作按钮为“停止计算”；停止后当前搜索取消且自动回合入口不能重启，点击“重新计算”恢复。手操预计战损 `7 -> 3` 时记录差值并显示绿色反馈；消息区域启用整行自动换行。runId `bbbffd3cf1cd4d8686472175a44ed64e` | 2026-08-29 |
| `PERFORMANCE-PRESET-LOW/MEDIUM/HIGH/VERY-HIGH-172` | 通过 | 四档固定解析依次为 `5/60s + 6GB`、`8/120s + 8GB`、`12/180s + 12GB`、`20/300s + 16GB`，Beam、节点与出牌分支均匹配规格并完成首回合战斗。runId `c18b796053064ffb89eebde8da49fa69`、`516c303b65d547fb9e60fa34d79ca3b5`、`ce61ed362f5143fc9d69ee8b9763eb2c`、`9791b831f1ac4b6ca296fe28811f81c4` | 2026-08-29 |

## 0.17.1

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `SLY-AUTOPLAY-NESTED-CHOICE-0171` | 通过 | 手牌戏法给杂技附加奇巧，生存者弃掉杂技后触发自动出牌与杂技自身弃牌；有序牌堆、逐牌状态、Power、RNG 和续用状态严格一致。runId `97af4be8cb104d15baac75fe1e4c3701` | 2026-08-29 |
| `TRIGGERED-SHUFFLE-CHOICE-ORDER-0171` | 通过 | 既有早有准备与升级杂技洗牌选牌顺序保持严格一致，确认 PR 没有覆盖当前 0.17 的战略选择修复。runId `1cd79d4025cd4c5698ec2ac0edc39f4e` | 2026-08-29 |
| `OSTY-RATTLE-TURN-COUNTER-0171` | 通过 | 第一回合让奥斯提攻击，完整结束回合后在第二回合打出猛晃；上回合攻击与命中计数均已清零，伤害及完整状态与实机一致。runId `1d3f201d5c75487f9cf9b70d67635dcf` | 2026-08-29 |
| `AUTOPLAY-ADJACENT-REGRESSION-0171` | 通过 | 抽牌触发、回合末自动出牌和 32 项卡牌完成生命周期严格差分全部通过。runId `2552b97f8b024ebebf8fbe268377086b`、`03b7d9432aeb4024bf11c2d5cc3a8ed3`、`03944b3c91844c8799965f40166b261f` | 2026-08-29 |

## 0.17.0

本节只登记本批次实际运行的验证。需求原文和计划不作为测试通过证据。

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `OPENING-STRENGTH/DEXTERITY-POTION-0170` | 通过 | 力量药与敏捷药均为最终路线首个动作、位于首张牌之前；runId `a65e0ce7c1e1478c949052d86ed799a7`、`c6217b9d975e4ecbaf0e26a4a3dd7a5d` | 2026-08-29 |
| `LIZARD-TAIL-LIVE-REUSE-0170` | 通过 | 1 HP 触发蜥蜴尾巴后，首轮与第 2 回合复用均保留整场战损 1；路线有“蜥蜴尾巴：复活”，计划外重算 0。runId `735a14adb16241719f220badb89f00a9` | 2026-08-29 |
| `BRIGHTEST-FLAME-TERMINAL/NECESSARY-0170` | 通过 | 同样无伤可胜时不打至亮之焰；必须用它完成当回合击杀时，路线保留 78 最大生命与 2 点当前损失。runId `c6b2b17afea34e9d8befaf3f32401f36`、`207af957f0664c478201bcd4c49bffd2` | 2026-08-29 |
| `BATTLEWORN-DUMMY-V1/V2/V3-KILL-0170` | 通过 | 三档训练假人均以自伤攻击完成击杀，不用安全停滞替代目标；runId `319a22b784414d4d8f75559ecaa21779`、`9f7666436f4c4451907971ae732211b1`、`b61f285e96ce40e2bef847e1c708250c` | 2026-08-29 |
| `BATTLEWORN-DUMMY-EVENT-DEFEAT-0170` | 通过 | 倒计时耗尽返回 `EventDefeat`，不授予胜利。runId `963ddd67b7db402da7a46f17a73cd7a3` | 2026-08-29 |
| `TWO-CARD-INFINITE-DEPLOY-0170` | 通过 | 亮剑/亮技双卡无限执行 19 个动作、18 次洗牌，当回合零战损击杀；完整自动执行计划外重算 0。runId `54b78ec8e2ef4baf80a452ff0744a81f` | 2026-08-29 |
| `ANGER-COMPACT-ALTERNATIVE/REQUIRED-0170` | 通过 | 等价击杀选择切割且不打愤怒；只有愤怒可击杀时仍使用。runId `03bfc9e0b0d44389aaba29c74f6a99fa`、`64a3b6d6d4304bdd9c6db48386983122` | 2026-08-29 |
| `AEONGLASS-ANGER-MIDCOMBAT-0170` | 通过（近似重建） | 按问题包第 9 回合手牌、生命、格挡、Power 和行动历史近似重建，路线不再加入愤怒。该夹具仍只找到死亡路线，省略完整消耗堆与部分历史，不作为原包战损复放。runId `58125330c52a4552b196021df614298e` | 2026-08-29 |
| `BECKON-CROSS-TURN-DEPLOY-0170` | 通过 | 首动打出呼唤，预计整场战损 4，第 2 回合自动击杀，计划外重算 0。runId `41198704657b42d284ff24113dbc429b` | 2026-08-29 |
| `GENETIC-ALGORITHM-REPLAY / GOOPY / SCYTHE-0170` | 通过 | 遗传算法华彩重放累计成长 6，并在第 2 回合继续执行、计划外重算 0；黏糊防御成长 1；巨镰成长 5，三者均在同战损胜利路线中主动培养。runId `6097ecc0ab3142a0a6c0ee187c1eda54`、`4b36668f89c449ca8eeb5ea6e6e1d2e4`、`4420c21826404907b33a1e9543949cdc` | 2026-08-29 |
| `NIGHTMARE-CLONE-GROWTH-BOUNDARY / SOULS-POWER-GROWTH-0170` | 通过 | 梦魇 `Clone` 不带 `DeckVersion`，因此不虚构跑局成长；灵魂之力跨回合培养至少 6。边界验证 runId `6e616ddf5c9b45b9a7c20434a8f912c1`，灵魂之力 runId `4e265950217046f886890458d2728220`；错误保留跑局版本会在第 2 回合产生状态差异，失败证据 runId `6112275ee763406abe03f23dfdc5238c` | 2026-08-29 |
| `FEED / THE-HUNT / HAND-OF-GREED-FATAL-0170` | 通过 | 三类斩杀分别获取最大生命、卡牌奖励和金币，且优先于普通等价击杀。runId `1e25b558793e445cbfa2394b23e2ef7a`、`41205c552552424197c3dde4827fa0f7`、`716c7e94d0c44b659672a8954b47de20` | 2026-08-29 |
| `NOT-YET / ROYALTIES / FORBIDDEN-GRIMOIRE / ALCHEMIZE-0170` | 通过 | 同战损胜利中依次保留治疗、金币奖励、移除奖励和生成药水；runId `72e453a92041468e8b041df280512ecb`、`3d1538b146e249b6b9debbb1a84ee54c`、`62943077e9a241ff90097518571d6dfc`、`532ddda82beb48fe815a0f66d8528d06` | 2026-08-29 |

## 0.16.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `NIBBITS-DUPLICATE-TORIC-0160` | 通过 | 从啃咬兽问题包战前状态直接注入两份坚韧之环。正式搜索跨 8 回合返回，runId `254746adc3a54c52ad894279e310d1e6`；严格差分验证两份实例以 `Block=5/8` 分别触发，合计获得 13 格挡并剩余总层数 1，runId `1eedfd2234a847378a0e79c600fb1012`；问题包世界线完整全自动在第 8 回合结束、计划外重算 0，runId `bcd844191a81453d8d21702024aa0fb9` | 2026-08-29 |
| `KNIGHTS-RELIC-ANNOTATION-REGRESSION-0160` | 通过 | 从三骑士问题包战前存档恢复种子、A10、`108/97/89` HP 与首行动；最终遗物标注的完整路线回放正常完成，返回 3 回合候选，不再出现两张 `BOOST_AWAY` 升级/保留状态交换。当前路线与旧包不同，不记作旧路线逐动作回放。runId `dfdb18f2036e42ecb6beda299d808028` | 2026-08-29 |
| `CHOICES-PARADOX-SCROLLS-0160` | 通过 | 使用咬人卷轴问题包的战前存档、种子、A10、四敌生命和行动重建首回合；验证选择悖论原生页面先显示、搜索后启动、Mod 自动选牌，路线第一组胶囊以“选择悖论：选择 ”开头。短搜 `5891.5 ms`，比较 `5883` 个选牌分支，runId `b9fa371a5b29479bb97c19da7980526f`；最小五候选夹具 runId `220482eb7fbc4f459c6f970748b0e033` 同样通过 | 2026-08-28 |
| `RINGING-HAVOC-AUTOPLAY-0160-FINAL` | 通过 | 仪式兽施加昏眩后，破灭作为本回合第一张牌正常结算；其翻出的重振被原版 `CardPlaysStarted` 规则阻止，不获得格挡、不消耗手牌防御，重振按破灭规则进入消耗堆。原生与预测完整状态一致。runId `bbc71ec201d34e16a114e2a1769ceb52`；修复前基线 runId `c24277b02b414dc08fdcb59fc7cec21e` 为模拟 5 格挡、实机 0 格挡 | 2026-08-28 |
| `MONSTER-MOVES-BATCH-029-RINGING-0160-FINAL` | 通过 | 既有昏眩相邻回归升级为当前逐实例状态键后，两次 `BEAST_CRY_MOVE` 严格差分通过：第一张牌可打、后续带昏眩的牌不可打，玩家回合末 Power 与全部昏眩状态清除。runId `0b73e8ca09374b0dbb27e41f6f021ec9` | 2026-08-28 |
| `HEADBUTT-EMPTY-DISCARD-0160` | 通过 | 清空全部牌堆后实际打出头槌；弃牌堆为空产生的 `0` 选项原生牌堆请求按空选择完成，第 2 回合精确复用，计划外重算 0。runId `53d9a793c14040d790183727ab0a88cd` | 2026-08-28 |
| `COSMIC-INDIFFERENCE-EMPTY-DISCARD-0160` | 通过 | 清空全部牌堆后实际打出宇宙冷漠；空弃牌堆选择不再中止部署，第 2 回合精确复用，计划外重算 0。runId `b09e6ef038604469b73680803c6916e7` | 2026-08-28 |
| `TORIC-TOUGHNESS-FRAIL-BLOCK-0160` | 通过 | 虚弱 1 层下打出坚韧之环，角色实际获得 3 格挡，但 Power 内部精确保存 `Block=3.75`；原生与预测完整状态一致。runId `e0e74f8d044e4026b9484ae78d03a622` | 2026-08-28 |
| `JAXFRUIT-TORIC-TOUGHNESS-REUSE-0160` | 通过 | 从啪嗒果问题包战前存档恢复种子、A10、双敌生命、首行动与 RNG；第 4 回合精确复用，计划外重算 0。runId `ed4f9715bde9405bab9655fd83701aba` | 2026-08-28 |
| `PAINFUL-STABS-MONSTER-ATTACK-0160` | 通过 | 给酸液攻击怪物注入荆棘，单次穿透格挡的命中后弃牌堆精确加入 1 张伤口；原生与预测完整状态一致。runId `b89e025cf429450595e4d38f2e603c90` | 2026-08-28 |
| `POWER-DAMAGE-HOOKS-REGRESSION-0160` | 通过 | 14 组伤害与攻击钩子严格差分全部通过，覆盖荆棘、吸取、活力、缓冲等，确认怪物攻击接入共享 `AfterAttack` 后没有重复结算。runId `50849d4ecff04661a7254b529611c74e` | 2026-08-28 |
| `TEST-SUBJECT-PAINFUL-STABS-REUSE-0160` | 通过 | 从实验体问题包战前存档恢复种子、A10、牌组、遗物、首行动与 RNG；越过第二形态多爪与荆棘，至第 6 回合持续精确复用，计划外重算 0。runId `a267600d977549f1a492d36479394f60` | 2026-08-28 |
| `VANTOM-UPGRADED-CARD-SHUFFLE-0160` | 通过 | 从 Vantom 问题包战前存档恢复种子、A2、牌组、首行动与 RNG；普通/升级打击跨洗牌顺序一致，第 5 回合精确复用，计划外重算 0。runId `e877c8239def4647a36c7d5102c940f3` | 2026-08-28 |
| `INSATIABLE-INVOKE-CROSS-CHARACTER-0160` | 通过 | 静默猎手打出召唤后推进到第 2 回合；原生与预测均创建 `2/2` 奥斯提并施加 1 层“为你而死”，两项下回合 Power 被消费，额外能量与 5 张手牌严格一致。runId `ec2f0a77e09a424fad6b8f78f2460c7e`；既有亡灵契约师奥斯提卡牌与伤害转移回归 runId `037a7a3ec6bc48f797913d398f0dfde1`、`c09e18e9f28b47e1a5c528495c62c124` 同时通过 | 2026-08-28 |
| `INSATIABLE-INVOKE-SEARCH-0160` | 通过 | 无厌沙虫固定为液化地面，静默猎手只有召唤与 5 张防御；正式 Short 搜索越过原 `EndTurn → SUMMON_NEXT_TURN_POWER` 初始化错误，正常返回 5 回合候选、1 个可执行动作、未镜像项 0。runId `0765ed5133604dcb9fab017fa8e30f42` | 2026-08-28 |
| `PALE-BLUE-DOT-FIFTH-CARD-DRAW-0160` | 通过 | 注入 2 层暗淡蓝点后恰好打出 5 张牌并进入下一回合；原生与预测都在第五张触发，下回合均抽基础 5 张加额外 2 张，瞬时抽牌 Power 均已消费。runId `cc6470a2161c417bbf64e5a672a67367` | 2026-08-28 |
| `TRIGGERED-SHUFFLE-CHOICE-ORDER-0160` | 通过 | 两个严格差分场景分别用早有准备和升级杂技触发空抽牌堆洗牌；战略选择先从洗牌后的抽牌堆拿走打击，随后卡牌自身选择把同一张打击置顶或弃掉，原生/模拟完整状态一致。runId `8da0adbda1484b8f8131cadd30e60d2c` | 2026-08-28 |
| `DECIMILLIPEDE-TRIGGERED-CHOICE-REPLAY-0160` | 通过 | 从千足虫问题包战前存档、种子、三段生命和三个首行动重建初始搜索，正常返回候选且没有再次出现第 17 回合早有准备双 pending 异常。当前路线与包内旧失败分支不同，不记逐动作回放。runId `967571405a5c4a78851c5310fcdd303a` | 2026-08-28 |
| `AEONGLASS-TRIGGERED-CHOICE-REPLAY-0160` | 通过 | 从永世沙漏问题包战前存档、种子、A10 和 `EBB_MOVE` 重建强制短搜，越过原第 6 回合杂技双 pending 边界并返回候选。当前路线与包内旧失败分支不同，不记逐动作回放。runId `d85101a57dcc4d4d910a2e159e02e6c0` | 2026-08-28 |
| `KNOWLEDGE-DEMON-GLAM-POCKETWATCH-0160` | 通过 | 注入怀表和带华彩的升级后空翻，后空翻以一个路线动作完成两次 CardPlay；推进到第 2 回合后原生/模拟牌堆、抽牌及怀表私有计数严格一致，均为 `POCKETWATCH/0/2`。runId `c82c4fcbadda41dc96df6d65cf0e0d63`；问题包 Custom/Low 战前跑局均未在夹具上限内完成首搜，不记通过 | 2026-08-28 |
| `CARD-UPGRADE-STABLE-SHUFFLE-0160` | 通过 | 武装只升级两张同名防御中的一张，两张牌以升级/普通顺序进入弃牌堆后触发洗牌；修复前第 2 回合严格差分稳定得到普通/升级防御错位，runId `1718b01532d94f34b65acc79a246482a`；改为按分支当前预览排序后原生/模拟完整状态一致，runId `854065893bc742c5ac04e3d6f59e8cdf` | 2026-08-28 |
| `CHOMPERS-UPGRADED-CARD-SHUFFLE-0160` | 通过 | 从啃咬者问题包战前存档重建，完整自动战斗在第 5 回合结束；武装升级后的同名牌跨洗牌顺序与实机一致，计划外重算 0。runId `ddebe062128845f9a3f73fbb6992e3ff` | 2026-08-28 |
| `CHOMPERS-UPGRADED-CARD-SHUFFLE-INCREMENTAL-0160` | 通过 | 同一问题包状态强制短搜并启用增量/完整前缀核对，覆盖 12 回合、3 次洗牌，未镜像项 0，前缀回放一致。runId `0c32515cb2944570a6a748febf928737` | 2026-08-28 |
| `STRATAGEM-PREPARED-CHOICE-ORDER-FINAL-0160` | 通过 | 升级准备充足在空抽牌堆时触发洗牌，战略选择先从三张抽牌堆选一张，随后准备充足抽两张、弃两张并留下打击完成 1 HP 斩杀；增量/完整回放一致，真实原生页面按两次选择顺序完成，计划外重算 0。runId `d305379b208841b68e25f8987e2e1967` | 2026-08-28 |
| `TEST-SUBJECT-PREPARED-CHOICE-SHORT-0160` | 通过 | 从问题包搜索请求检查点固化 5 张手牌、27 张有序抽牌、玩家状态及 `BITE_MOVE` 状态日志；强制短搜越过原准备充足双 pending 失败点，返回 7 回合候选，未镜像项 0。runId `d4903310604044ae8fa0c689a82f8b8d`；整包增量与普通深搜均在 180 秒达到夹具上限，不记通过 | 2026-08-28 |
| `CROSS-TURN-NO-PROGRESS-0150` | 通过 | 仅有一张防御、100 敏捷且完全没有伤害手段；修复前耗满短搜约 22 秒并搜索 54 回合，修复后搜索本体 175.3 ms 结束、剪掉 18 条跨回合无进展分支。runId `5e3fa09b18094a77a07492098e204785`，修复前 runId `ddcb886becc84a28aa8b56dbb067bea9` | 2026-08-28 |
| `BOWLBUGS-CROSS-TURN-NO-PROGRESS-0150` | 通过 | 从问题包战前存档、种子、敌人生命与首轮意图近似重建，仍找到第 6 回合胜利、预计战损 3、零药水；当前没有原生战斗状态导入器，不记作问题包逐动作回放。runId `33c814b90b6b4bdda47fe5b9c98961f9` | 2026-08-28 |
| `SURVIVOR-REPLAY-EMPTY-CHOICE-0150` | 通过 | 爆发与复制使升级生存者执行三次；前两次实际弃完两张牌，第三次原版 `options=0 / select=0..0` 请求按无操作完成，不消费虚构计划。首回合结束、计划外重算 0，runId `207cdd4927f74188948ec903574a3c7c`；修复前 runId `026a459931b24b58be85d644d3778d25` 在同一请求报错 | 2026-08-28 |
| `NATIVE-EMPTY-PLAN-ADJACENT-0150` | 通过 | 复制拾荒在空手时发出两次原生空请求；搜索生成的两条显式空计划逐条核销，首回合结束、计划外重算 0。runId `c73b5302e55e4b06bf56dc35169f3e20` | 2026-08-28 |
| `POCKETWATCH-REPLAY-REUSE-0150` | 通过 | 手牌中的螺旋打击实际结算两次 `CardPlay`，路线仍保持一个出牌动作；怀表逐次计数后第 2 回合命中精确复用，增量分叉与完整前缀回放一致，计划外重算为 0。runId `93d934679e8746de95bebd9dd5ce58e2`；修复前基线 runId `89792141b109409aa2aa5adcc7d2a846` 稳定得到 `expected=1 / actual=2` | 2026-08-28 |
| `POCKETWATCH-REPLAY-FULL-COMBAT-0150` | 通过 | 手牌为螺旋打击、抽牌堆为普通打击，敌人 13 HP；完整自动部署在第 2 回合结束，增量分叉与完整前缀回放一致，计划外重算为 0。runId `106f0b2966dd4225ac9ce1213e123712` | 2026-08-28 |
| `INCOMPATIBLE-GAMEPLAY-MOD-MESSAGE-0150` | 通过 | 预测失败边界断言验证未知第三方玩法 Mod 的玩家提示包含 Mod 名称、标识和卸载建议，不暴露内部订阅器类型；详细异常仍保留 Mod 与订阅器上下文。runId `3b920fd04bb64fdeba536ee825219ea4` | 2026-08-28 |
| `BATTLEWORN-DUMMY-TIMEOUT-BOUNDARY-0150` | 通过 | 第二档假人 150 HP、时间限制 1 层；正式后台搜索在原生逃跑前返回 `EventDefeat`，不移除假人、不授予胜利。runId `1b1d321d7ac941adb5d515efa861d6ee` | 2026-08-28 |
| `BATTLEWORN-DUMMY-V2-EXACT-FINAL-0150` | 通过 | 从第二档训练假人问题包的战前存档、固定牌序和 150 HP 重建，开启增量分叉/完整前缀回放核对并完整自动执行。未击杀分支正确为 `won=False / EventDefeat`，击杀路线为 `won=True / None`；第 2、3 回合精确复用，计划外重算 0。runId `7fbd338febef40668a4980555cc51971` | 2026-08-28 |
| `FAIRY-AUTOMATIC-RESCUE-FINAL2-0150` | 通过 | 1 HP 铁甲战士持瓶中仙女，手牌/抽牌堆各一张重锤；求解器不再判定仅有死亡路线，第 1 回合精灵药自动复活，第 2 回合击杀。首轮路线记录 1 瓶药，实机消耗 `FAIRY_IN_A_BOTTLE`，增量/完整回放一致，计划外重算 0。runId `bbddfcc1e1e54be2a4405e58cd7f557e` | 2026-08-28 |
| `FAIRY-DEATH-LIFECYCLE-FINAL2-0150` | 通过 | 瓶中仙女的自动防死、消耗槽位和 30% 回复与原版完整状态严格一致，已消耗实例不会再次进入死亡监听。runId `e601bec430ea49318ef57a550d8284f8` | 2026-08-28 |
| `ONLY-DEATH-NO-FAIRY-REGRESSION-0150` | 通过 | 相同 1 HP 与酸液攻击下不注入精灵药，首轮仍正确报告仅死亡路线并在第 1 回合死亡，用药数 0。runId `53ad9b78496646b196aa4844794766ef` | 2026-08-28 |

## 0.15.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `VAMBRACE-STABLE-FORK-FINAL-0150` | 通过 | 原版臂铠已经获得本场首次格挡后仍保留触发卡引用；修复后状态可 Fork，触发卡身份和 `BlockGainedThisCombat=true` 均保持。runId `31793a0e83df4656aa0ea3b9182c4c29`；修复前基线 runId `0e1c56fe922c4b4a87109dbd1c06acd0` 稳定抛出问题包同款异常 | 2026-08-28 |
| `TUNNELER-IMBUED-GLACIER-VAMBRACE-0150` | 通过 | 缺陷机器人持臂铠，注能冰川开局自动打出并进入弃牌堆，原版得到 12 格挡；首轮搜索正常返回，未镜像项为 0。runId `a9ebb7b36f554948a8de0f658385aa34` | 2026-08-28 |
| `TUNNELER-IMBUED-GLACIER-VAMBRACE-INCREMENTAL-0150` | 通过 | 同一组合启用增量搜索核对，增量分叉与完整前缀回放一致；开局 12 格挡、未镜像项 0，搜索正常返回。runId `689793cd6343465393fdd567a4a7c41e` | 2026-08-28 |
| `RELIC-CARD-HOOKS-AUDIT-PART-2-VAMBRACE-FINAL-0150` | 通过 | 臂铠连续打出两张防御，第一张 5 格挡翻倍为 10，第二张按普通值获得 5，最终严格为 15；同批遗物 Hook 11/11 通过。runId `eddeca24b4544e918299e4b4bb2a401b` | 2026-08-28 |
| `AXEBOT-THORNS-MULTIHIT-FINAL-0150` | 通过 | 巨斧机器人以 `2 HP`、`2` 层库存执行两连击；玩家持有 `3` 点荆棘和 `12` 格挡。修复前模拟继续执行第二段并产生 `8` 点虚构战损；修复后第一段反伤致死即中止剩余攻击，玩家保持 `75 HP / 2` 格挡，库存重生后的完整实机/模拟状态一致。相邻上勾锤击同时核对攻击者死亡后仍结算虚弱/脆弱。runId `fac22ea0270a4996afa276df384ba370`，基线 runId `fc33aad095054c37a3de730d89472d2a` | 2026-08-28 |
| `AXEBOTS-BUNDLE-FULL-AUTO-FINAL2-0150` | 通过 | 从问题包战前存档重建，以 Low、Instant/0 秒完整自动结束于第 11 回合，`UnexpectedReplans:0`。当前路线与原包不同，不记作逐动作回放。runId `fca4e9e3a09d4ab490271ec6d38ad10a` | 2026-08-28 |
| `AXEBOTS-BUNDLE-INCREMENTAL-FINAL2-0150` | 通过 | 同一问题包状态以 Low/Short 完成增量分叉与完整前缀回放一致性，覆盖 11 回合、3 次洗牌，未镜像效果为 0。runId `c8c8f3675a934f028704ab82e2f7dd4d` | 2026-08-28 |
| `FTL-CROSS-TURN-STATE-0150` | 通过 | 修复前跨回合严格差分稳定复现第 3 张 FTL 少抽一张；最终分支状态实现下，第 3 张抽牌、第 4 张不抽均与实机一致。runId `add8e54810d54f41b5cb6b55dc410892`，基线 runId `6c069ce0891a484091481bd8b3387e35` | 2026-08-28 |
| `CURRENT-TURN-CARD-HISTORY-ADJACENT-FINAL-0150` | 通过 | Fetch 在下一回合经全息影像取回同一实例后重新允许抽牌；Make It So 在本回合第 3 张技能后返回手牌，实机/模拟严格一致。runId `f478c96ec3144c81847c3b225f95866e` | 2026-08-28 |
| `BYRDONIS-BUNDLE-REUSE-FINAL-0150` | 通过 | 从多尼斯异鸟问题包的战前跑局状态重建；第 3 回合精确复用，计划外重算 0，增量分叉与完整前缀回放一致。首抽路线与原包不同，因此不记作逐动作回放。runId `a0c1808cfae745188ae5a3f8d1f28270` | 2026-08-28 |
| `SLITHERING-STRANGLER-BUNDLE-REUSE-FINAL-0150` | 通过 | 从蛇行扼杀者问题包的战前跑局状态重建；越过原第 4 回合重算点并精确复用，计划外重算 0。首抽路线与原包不同，因此不记作逐动作回放。runId `a6b72e6f81f24aa096833902d6860046` | 2026-08-28 |
| `CUBEX-ROOT-CAPTURE-150` | 通过 | 修复前同场景稳定复现不存在的 `CubexConstruct.ChargeUpStrengthGain`；移除多余捕获后根快照成功物化。runId `0c39d6aa84904c5b994bf8f985bfd316`，基线 runId `f96241297c7a443a8a1fe50d0a7b5414` | 2026-08-28 |
| `CUBEX-SEARCH-INITIALIZATION-150` | 通过 | 方柱构装体正常首轮搜索覆盖 4 回合，返回 3 个可执行动作，未镜像效果为 0。runId `2f37d97d045743aea8d68ebf99db0e57` | 2026-08-28 |
| `MONSTER-MOVES-BATCH-020-CUBEX-150` | 通过 | 既有 13 项实机/模拟差分全部通过；方柱构装体排出、蓄能和两次重复轰击分别验证多段伤害及力量 `2 → 4 → 6` 累计。runId `1d382781dfc2402581af0383e093b5ea` | 2026-08-28 |
| `TOASTY-MITTENS-BUNDLE-FINAL-0150` | 通过 | 从异螨问题包的战前跑局状态重建首回合烘焙手套；原生手牌页按 `Visible → SearchStarted → Selected` 由 Mod 自动接管，搜索返回 1 个 `TOASTY_MITTENS` 选择并严格进入 Play 状态。runId `097e957b46b941e1b4eb0165862d5493` | 2026-08-28 |
| `KNOWLEDGE-DEMON-NATIVE-CHOICE-0150` | 通过 | 知识恶魔首轮路线计划 `MIND_ROT`；提交结束回合后原生 `ChooseCard` 页面 `visible=1 / selected=1 / search=0`，玩家获得 `MIND_ROT_POWER`，计划外重算 0，增量/完整回放一致。runId `5b5d61d595c249c0a4861151460cc490` | 2026-08-28 |
| `KNOWLEDGE-DEMON-NATIVE-CHOICE-REUSE-0150` | 通过 | 同一路线完成敌方回合二选一后，第 2 回合直接复用；知识恶魔选择没有被错误留给下一回合准备器，计划外重算 0。runId `27a42f3669fb479fafde8e10e3d499f3` | 2026-08-28 |
| `TOASTY-KNOWLEDGE-CROSS-PHASE-0150` | 通过 | 知识恶魔战同时持有烘焙手套；首回合手套保持 `Visible → SearchStarted → Selected`，结束回合后自动完成 `MIND_ROT` 二选一，第 2 回合只重放准备选择并精确复用，计划外重算 0。runId `39de2fb177ce43db95c1c2209c390330` | 2026-08-28 |
| `BURNING-PACT-AUTO-COMPLETE-0150` | 通过 | 固定手牌为燃烧契约+、升格者之灾、防御，抽牌堆为打击；Normal 部署先显示原生手牌页并选择升格者之灾，再打出抽到的打击结束战斗。请求记录 `manual_confirmation=False`，页面 `visible=1 / selected=1 / search=0`，增量/完整回放一致。runId `660b6ba4b2a044938d3960208639b5ef` | 2026-08-28 |
| `ARMAMENTS-AUTO-COMPLETE-ADJACENT-0150` | 通过 | 未升级武装从打击、防御中选择升级目标，原生手牌升级页完成后继续打出升级打击；页面 `visible=1 / selected=1 / search=0`。runId `793fd329c05340d98a775b173dd3b8c9` | 2026-08-28 |
| `CHOMPERS-BURNING-PACT-BUNDLE-FIXED-0150` | 本问题路径通过，整战断言失败 | 从问题包战前跑局状态重建同族小队，燃烧契约原生手牌页完成且未出现确认按钮异常，战斗第 5 回合结束；第 4 回合另有防御升级状态不一致并触发 1 次计划外重算，故不记为整场通过。runId `99127886a8c54dfe8941239186b5ddea` | 2026-08-28 |
| `TOADPOLES-WEAK-20260828-BUNDLE` | 根因确认，待 macOS 实机复测 | `0.14.11`、macOS ARM64 的两次搜索均在 `GC.TryStartNoGCRegion(6 GB, 1 GB)` 抛出 `ArgumentOutOfRangeException(totalSize)`；根快照已成功，尚未进入 Beam。当前代码只把该精确异常分类为 CLR 区域上限，其余异常保持失败 | 2026-08-28 |
| `GC-NOGC-REGION-LIMIT-0150` | 通过（正常 No-GC 路径） | Windows headless 设置 `16 GB` No-GC 预算；本机 CLR 成功进入 No-GC，首轮 Short 搜索在 `168.7 ms / 2.20 MB` 内产出 1 个可执行动作，GC 暂停 `0 ms`，场景 Passed。runId `04450f09159d48d9bfaca0ba9ba049e0`；该结果不覆盖 macOS 的区域拒绝分支 | 2026-08-28 |
| `KAISER-CRAB-SEARCH-REPLAY-FINAL-0150` | 通过 | 从帝王蟹问题包战前存档、原 seed 与两只怪物的 `209/199` HP 重建；修复前在第 3 回合 EndTurn 回放稳定复现缺失 `Rocket.ChargeUpStrengthGain`，修复后搜索覆盖 7 回合、未镜像效果为 0。runId `7236617d45b54d17b98bb2a8a68fcf21`，基线 runId `42db83c00fbb43548135efadaed5604d` | 2026-08-28 |
| `KAISER-CRAB-INCREMENTAL-SHORT-FINAL-0150` | 通过 | 同一问题包状态以 Low/Short 完成增量分叉与完整前缀回放对照，搜索覆盖 9 回合、未镜像效果为 0。runId `2067fe850f4d4cd0b011c7e1ce05e40a`；High 完整验证因仪器开销在 120 秒超时，runId `343c4c6b3ba54ef58050f6d1a898ac05` | 2026-08-28 |
| `MONSTER-MOVES-BATCH-021-KAISER-0150` | 通过 | 帝王蟹 10 个行动的实机/模拟严格差分全部通过；火箭蓄能获得 `2` 点力量，激光与重新充能保留累计状态。runId `7bcbece1c9e04a36a72b8ddddb2db361` | 2026-08-28 |
| `CALCULATED-VAR-ROOT-CAPTURE-FINAL-0150` | 通过 | 耗尽堆 `EXPECT_A_FIGHT` 固定 `CalculatedBlock=16 / CalculationBase=15`；修复前根投影稳定复现 `16 → 15` 失败，计算缓存改为派生字段后根快照通过。runId `e243b913a1a44aa8ba67e692da88d1b0`，基线 runId `0ea043478335482c84408617ab91e38a` | 2026-08-28 |
| `EXPECT-A-FIGHT-CALCULATED-BLOCK-FINAL-0150` | 通过 | 玩家持有 5 点力量时打出 `EXPECT_A_FIGHT`，实机与模拟完整状态严格一致，证明移除派生缓存没有丢失公式输入或实际格挡。runId `bccf3de8a91f4aad94af02b589a77d1a` | 2026-08-28 |
| `CARD-DOWNGRADE-STATE-AUDIT-382-0150` | 通过 | 魔法骑士抑制对手牌、抽牌堆和弃牌堆的 8 类升级牌执行降级，并在施法者死亡后恢复；实机与模拟逐实例状态一致。runId `95795debe881414e8d8179921061e20e` | 2026-08-28 |
| `KNIGHTS-ELITE-SEARCH-FINAL-0150` | 通过 | 从三骑士问题包战前存档、原 seed、进阶与 `108/97/89` HP 重建，首轮搜索正常返回可部署路线；复杂嵌套随机选牌的失效候选没有再中止搜索。runId `411ab4cdfe514a7cab2bac384354beb5` | 2026-08-28 |
| `KNIGHTS-ELITE-BUNDLE-FULL-AUTO-FINAL-0150` | 通过 | 同一战前存档以 Instant/0 秒完整自动部署，第 1 回合结束战斗，计划外重算 0。当前源码首抽路线与 `0.14.11` 原包不同，不记作原包逐动作回放。runId `bc508c1d2a75438599fc4cb26656acf4` | 2026-08-28 |
| `KNIGHTS-ELITE-INCREMENTAL-FINAL-0150` | 通过 | 同一问题包重建状态以 Low/Short 完成增量分叉与完整前缀回放一致性，首轮返回 11 个动作并结束战斗。runId `b0740c38be024c61a73a7c7aa281164a` | 2026-08-28 |
| `STATE-FIELDS-DERIVED-CALCULATED-0150` | 通过 | CoverageCatalog 将 43 个原版 `CalculatedVar` 字段登记为 `Derived`，未分类状态字段为 0；真实基础变量、私有状态和字符串显示字段分类保持不变 | 2026-08-28 |

## 0.14.13 Loadout 战斗费用兼容

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `LOADOUT-EVERY-CARD-FREE-ROOT-1413` | 目标路径通过，完整断言受限 | 投影实际 Loadout `0.4.10` 与 BaseLib `3.4.5` 后进入小啃兽战斗。第一轮成功创建并 Fork 根快照，未再出现 `LoadoutEveryCardFreeCombatHook` 的 `SEARCH_SETUP_FAILURE`；随后旧测试把 ModHelper 运行级 subscriber 误算进原版前缀，runId `7c5c868146194a05a7d038d93c31feb3` 在外围计数断言失败。修正断言后的第二轮在 Loadout 的 headless 战斗房间资源预载处超时，未进入战斗断言，不记为完整通过 | 2026-08-28 |

## 0.14.12 同族小队压缩连锁

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `kin-boss-route-clean` | 通过 | 固定 `oldE0VXH9PVN8`、进阶 10、第一幕同族小队、三敌 `63/62/199` HP、铁甲战士 `56/85` HP 与存档牌序；Smart、Instant 完整自动执行。第 4 回合燃烧契约、愤怒、余烬后使用灰水耗尽六张牌，第 5 回合以愤怒、放血、燃烧契约及连续攻击击杀三敌；最终 3 HP，战损 53，第 5 回合获胜，计划外重算 0。runId `41108853af1640fa8ee3379793469fc9` | 2026-08-28 |

## 0.14.10 敌方攻击压制保路

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `INSATIABLE-PARETO-CONTROL-150` | 通过 | 固定无厌沙虫“液化地面”、日志首手与有序牌堆；不再把某张牌的固定顺序当作质量代理，而是同时门禁首回合 `0` 掉血、整场预计掉血不超过 `7`、不卖血、不用药、至少搜索/存活至第 `7` 回合且终局敌方总生命不高于 `204`。隔离的上游 `v0.17.2` 与当前分支均复现旧 `MALAISE` 首动作断言失真，因此未修改生产搜索排序。runId `207ad2c8d02f489f9ba1aa41287d6966` | 2026-08-30 |
| `MALAISE-CONTROL-NIBBITS-REGRESSION-150` | 战损通过，回合断言失败 | 双小啃兽仍为 `0` 战损、`0` 计划外重算、两次洗牌；实际第 6 回合结束，请求沿用了第 5 回合精确断言，因此结果状态为 Failed，不计作整场通过。runId `a6ef1d75a48d46f2be7b98ce7ef4def5` | 2026-08-27 |
