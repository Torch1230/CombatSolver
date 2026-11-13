# 0.51.3 报告批次：逻辑与初始化

来源：`combatsolver-reports-20261113-122234.zip`，25 份报告均声明 CombatSolver 0.51.3、游戏 0.113.0。本批实现进入 0.51.4。按用户要求静态归并，以最小机制验证；玩家完整环境与整场路线未恢复。

## 报告归并

| 报告前缀 | 首个问题 | 处理 |
|---|---|---|
| `046debf8`、`52e6bbbc`、`88a1e668` | 女王生命预测 337／原生 330；另两包终局回放提前结束，均经过大树将倾 | 修正击杀后群伤的触发依据 |
| `f9fa0707` | 铁甲战士得到花样射击，箭槽预测空／原生 1 | 同步单张和批量入堆的首个箭槽 |
| `12506cc2`、`467840ab` | 洪流巨人转阶段后，预测缺少插箭和减益 | 转阶段进入原生死亡 Hook 时点 |
| `6e1f2ffd` | 横祸之后 CombatCardSelection 预测 9／原生 11 | 空牌堆剩余循环继续消费随机流 |
| `dc9cfe57` | 安可参与探寻打击的选牌 Fork，活动卡缺少映射 | 复制 Power 前准备活动 CardPlay 与卡牌映射 |
| `7dd74816` | 智能药水的两瓶补搜未找到合规路线，异常中止整次搜索 | 使用已有可选药水后验派发，保留主搜结果 |
| `93632b35` | 机甲骑士 T4 手／抽牌堆顺序和状态牌抽牌数偏差 | 待验证；见下节 |
| `03a325b0`、`7fa404d4` | `continuation_missing`，前一结果 `NodeLimit` | 有限预算路线耗尽后的正常续算 |
| `e77cb0ad`、`a53c094b` | `LexNinja2...DirtyDataSingleton` 缺少模拟语义 | 按范围跳过第三方内容 |
| `d0d84d12`、`7cafa5ca`、`54d6e540`、`87775d8e`、`25f4daa1`、`d716a829`、`ba05aa56`、`e524912e`、`4028bbb7`、`768fc785`、`fc6fec68` | BetterWorldline／手操路线比较 | 按范围留待策略专题 |

合计：9 份归入本批六类修复，1 份待验证，2 份预算续算，13 份范围外材料。

## 实现依据

- 大树将倾使用当次 `AttackCommand.Results` 的 `WasTargetKilled`。此前按历史 Trace 的 Source 引用查杀敌，COW 卡牌与完整标注回放的引用口径不同，搜索漏群伤、最终回放却触发群伤。直接消费原生同口径的攻击结果，使普通搜索和完整回放共用判定。
- `CardPile.AddInternal` 会给基础箭槽为零的角色，在首张 Shoot 卡进入战斗牌堆时创建箭槽。单张与批量 `AddToPile` 共用 `EnsureArrowSlotForAddedCard`；既有箭槽的 Fork、指纹和续用字段继续承担所有权。
- `SteamEruptionPower.AfterDeath` 在攻击尾部之前恢复洪流巨人生命和行动；死亡 Hook 结束后清理复活个体的死亡 Power，射箭尾部才添加新的 Impaled、虚弱和易伤。删除 `CorePowerSupport` 中延迟转阶段的重复入口，更新覆盖分类。
- 原版横祸每次循环都会 StableShuffle，空池也消费种子。预测在首次空池跳出循环，少消耗后续空池的随机种子；改为继续下一次循环。
- 安可的 `cardsBeingPlayed` 持有活动卡。手动选牌 Fork 先沿已有 `PrepareExecutionCardPlay` 建立卡牌与 CardPlay 映射，再复制 Power。严格历史合同同时发现 0.113 新增 `DamageResult.IntendedHpLoss` 在选牌历史复制中丢失，两个历史续接入口一并保留该字段和 `OriginalTarget`。
- 两瓶药水补搜沿已有 `DispatchOptional` 仅处理明确的 `PotionPolicyUnsatisfiedException`。可选成员未找到路线时继续既有结果比较；主请求的强制用药约束仍沿原入口检查。

## 原生证据

所有请求上限 120 秒，实例最终均由启动器清理；通过的场景按受影响范围复用。

| 场景 | runId | 结果与范围 |
|---|---|---|
| TIMBER-KILL-SPLASH | `4b2d2c2e358a4c4696eb046ed0b29ecd` | Passed；双敌中杀死首目标，核对群伤及完整状态 |
| CATASTROPHE-EMPTY-DRAW | `0d9cfcb954c04f1da0cd6d34207b1f55` | Passed；普通／升级横祸空牌堆的完整 RNG 与状态 |
| WATERFALL-ARROW-TRANSITION | `23dad02b2bb04e0a925aafedd00cdcd0` | Passed；蹒跚射击击穿阶段后 Impaled1、Weak1、Vulnerable1、完整状态与 Fork |
| CROSS-CHARACTER-ARROW | `51a7b646dc1b4f2caf876e5e56a0ff26` | Passed；单张／批量入堆、重复移动、箭槽身份、父子／兄弟隔离和 live 推进 |
| CARD-CONTINUATION-ENCORE | `0209ec0d87654471a4c85dc81faa7433` | Passed；安可下投掷匕首的全部选择、完整状态／RNG／历史、兄弟修改、DOP2、取消及原生选牌完成 |

调查失败保留：`07c9f6356add4cb0ab01ef3619cb3006` 的夹具把未升级蹒跚射击的减益写成 2，按原生 1 修正后通过；`7bdd53c98aea4d418f930aaa2d090ed1` 暴露单张入堆入口仍缺箭槽，统一两个入口后通过；`5f7db07240224ba5a5243e9dc476b837` 在严格历史对照得到 IntendedHpLoss 9／0，修正复制字段后通过。

`OriginalTarget` 的转移目标复制经源码核对，本轮合同目标与受击者相同；转移伤害专门夹具未运行。两瓶可选补搜采用源码调用链检查和 Release 构建，原药水组合未重放。六类修复均以报告首差异定位，修改前原版失败夹具未逐项运行。

## 机甲骑士牌序调查

`93632b35` 的 T3 结束前手牌含 4 张烧伤；首张烧伤触发百年积木。预测 T4 状态牌抽牌数 1，原生 2，牌堆 RNG 完整状态相同，区别集中在洗牌前进入牌堆的卡。按原包牌组、遗物和 `BOUNCE → APPARITION → ENCORE` 抽取的最小回合末／次回合准备对照 `38024539cf71438985b6a1a8644f4290` Passed，未复现原偏差。

原版 `DoTurnEndCards` 并发安排卡牌移入 Play，同时串行等待效果；动画等待期间后续烧伤可能先离手。百年积木逐次抽三张牌，手牌空位随该时序变化。此路径能解释多抽一张后洗牌输入不同，仍需可见原生时序证据确认。报告包含 SpeedX，当前证据未将偏差归因于具体 Mod。本批保留原始差异，未改预测牌序或放宽续用核对。
