# 遗物印牌站点的根生成池复用：只保留 Crossbow（2026-09-19）

承接 [ModelDb.GetId 记忆化](defect-modeldb-getid-cache-20260919.md)。那一轮把「按类型解析 ModelId」的正则成本记忆化后，剩下的印牌（生成新卡）路径里只有 6 个**遗物**站点还在直连原生 `CardPoolModel.GetUnlockedCards`，绕过已有的根生成池快照。本轮把 6 个站点按站点分组逐一做单变量 A/B，按测量结果只保留 1 个：**Crossbow**。

**结论**：`Crossbow`（每回合触发）接上已审计的 `GetDistinctUnlockedCharacterAttacksForCombat` 后，构造根 @20k 分配 **−13.6%**、峰值 RSS **−13.1%**、墙钟 **−3.5%**，转移/选择分支/全部剪枝计数/决策逐位相同；另外 5 个 `turn <= 1` 的站点（`Toolbox` / `OrangeDough` / `ChoicesParadox` / `VexingPuzzlebox` / `BigHat`）实测收益在噪声内或为 0，**已按测量回退**。

## 1. 改动

只改一处调用点与其共用的尾部：

- [SimulatedCombatState.RelicTurnStart.cs](../../src/Search/SimulatedCombatState.RelicTurnStart.cs) 的 `case Crossbow`：候选从 `relic.Owner.Character.CardPool.GetUnlockedCards(...).Where(Type == Attack)` 换成 `simulator.GetDistinctUnlockedCharacterAttacksForCombat(relic.Owner, 1, rng, constraint)`，命中根快照时复用已按战斗/人数过滤的角色攻击牌数组，未命中回退同一条原版路径。
- 原 `GenerateRelicCards` 拆成「取候选」与「加牌 + 本回合免费」两段：新 `AddGeneratedRelicCards(simulator, relic, IReadOnlyList<PredictedCard>, setFreeThisTurn)` 保留原版加牌语义，其余 5 个站点继续用原 `GenerateRelicCards`（枚举方式不变）。

等价性前提与原 [生成池复用](dop16-veryhigh-fidelity-20260919.md) 各入口相同：候选集合与顺序不变（两个过滤都保序、可交换）、谓词仍排在随机选择之前、`TakeRandom`/`NextItem` 的 RNG 消耗不变、`PredictedCard.Create` 仍逐分支生成独占卡牌。

## 2. A/B 结果（No-GC 12 GB，DOP16；等价性用 DOP1）

命令形状（两侧同一宿主二进制，仅用 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 切换模组产物）：

```bash
TMPDIR="$PWD/.local/tmp" timeout 900 dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request "$PWD/.local/bench/req/<构造根>.json" --label <label> --out .local/learned-selector/memory-e2e/<label> \
  --profile VeryHigh --nodes 20000 --dop 16 --budget-ms 600000 --search-mode Evaluate \
  --enable-no-gc-region --no-gc-region-budget-gigabytes 12 --milestone M2
```

### 2.1 保留：Crossbow

语料 300 根的可随机遗物池（150 件，Common/Uncommon/Rare/Shop）里**没有 Crossbow**，所以没有语料根覆盖；证据来自构造根：把 `CROSSBOW` 显式注入 `sel-defect-elite-01` 的同一请求（宿主接受，属于单人遗物），其余遗物与牌组不变。

| 指标 | 基线（DOP1） | 候选（DOP1） | 变化 |
|---|---:|---:|---:|
| 转移 | 131,514 | 131,514 | 0 |
| 选择分支 | 8,612 | 8,612 | 0 |
| 分配 GiB | 6.4657 | **5.5030** | **−14.89%** |
| 峰值 RSS GiB | 6.71 | **5.75** | −14.36% |
| 墙钟 s | 18.55 | 17.36 | −6.40% |
| 全部剪枝计数 + score/战损/finalHp/结束回合/planActions | 逐位相同 | 同 | 0 |

| 指标 | 基线 DOP16（3 次） | 候选 DOP16（3 次） | 变化（中位） |
|---|---:|---:|---:|
| 墙钟 s | 13.42 / 13.25 / 13.18 | 12.55 / 12.86 / 12.79 | **−3.46%** |
| 分配 GiB | 6.7347 / 6.7338 / 6.7377 | 5.8082 / 5.8225 / 5.8213 | **−13.56%** |
| KB/转移 | 55.53 / 55.52 / 55.55 | 47.89 / 48.01 / 48.00 | −13.56% |
| 峰值 RSS GiB | 7.06 / 7.06 / 7.06 | 6.13 / 6.14 / 6.14 | −13.05% |
| 转移 / 选择分支 | 127180 / 7619（6 次全同） | 127180 / 7619（6 次全同） | 0 |

无 Crossbow 的哨兵 `sel-necrobinder-monster-04`（DOP1）：转移 87,165 与全部剪枝/决策逐位相同，分配 3.2959 → 3.2956 GiB（−0.01%）——即改动没有附带影响。

### 2.2 回退：Toolbox + OrangeDough（无色池）

| 根 | 采样 | 分配 | 墙钟 | 决策/剪枝 |
|---|---|---:|---:|---|
| `sel-necrobinder-monster-04`（语料，含 Toolbox） | DOP1 | 3.299 → 3.293 GiB（−0.2%） | 11.95 → 11.47 s | 全同 |
| 同上 | DOP16 3+3 | 3.817 → 3.819 GiB（+0.1%） | 7.15/7.05/7.18 → 7.18/6.94/7.19（+0.4%） | 全同 |
| `inj-orange_dough-sel-ironclad-elite-01`（构造） | DOP1 | 1.803 → 1.803 GiB（+0.0%） | 8.98 → 8.96 s | 全同 |
| 同上 | DOP16 3+3 | 2.070 → 2.071 GiB（+0.0%） | 4.85/4.81/4.85 → 4.81/4.82/4.80 | 全同 |

排他阶段表（`sel-necrobinder-monster-04`、20k、`--measure-phases`）：`round_player_start` 两侧都是 **0.1845 GiB**，没有可观测差异。

### 2.3 回退：ChoicesParadox + VexingPuzzlebox + BigHat（自己角色池）

| 根 | 采样 | 分配 | 墙钟 | 决策/剪枝 |
|---|---|---:|---:|---|
| `sel-regent-boss-00`（语料，含 VexingPuzzlebox） | DOP1 | 1.5578 → 1.5569 GiB（−0.06%） | 7.98 → 7.53 s | 全同 |
| 同上 | DOP16 3+3 | 1.6768 → 1.6767 GiB（−0.01%） | +0.17% | 全同 |
| `inj-big_hat-sel-regent-boss-00`（构造） | DOP1 | 2.1161 → 2.1169 GiB（+0.04%） | 9.07 → 9.17 s | 全同 |
| 同上 | DOP16 3+3 | 2.3485 → 2.3483 GiB（−0.01%） | +0.78% | 全同 |

四组采样里转移数、选择分支数、全部非时序剪枝计数与 score/战损/finalHp/结束回合/planActions **逐位相同**，说明改写本身是等价的；判「收益未建立」纯看收益。

### 2.4 重型根复测（含遗物的语料重根）

§2.2 / §2.3 用的是怪物/精英级轻根，按「必须测含该遗物的重型场景」的要求，这里用**含这些遗物的语料首领根**（20k 节点、12 GB、DOP1、各 1 次）复测 `all6`（6 站点全部接缓存）对 `Crossbow-only`：

| 根 | 遗物 | 转移 | 墙钟 基线→候选 | 分配 基线→候选 | 决策/剪枝 |
|---|---|---:|---|---|---|
| `sel-silent-boss-04` | Toolbox | 51,160 | 7.77 → 7.17 s | 1.6363 → 1.6321 GiB（−0.25%） | 全同 |
| `sel-necrobinder-boss-04` | Toolbox | 87,521 | 11.18 → 11.18 s | 2.9335 → 2.9310 GiB（−0.08%） | 全同 |
| `sel-silent-boss-10` | VexingPuzzlebox | 89,833 | 11.16 → 11.07 s | 3.4806 → 3.4800 GiB（−0.02%） | 全同 |
| `sel-regent-elite-13` | OrangeDough | 148,872 | 18.33 → 17.92 s | 6.5630 → 6.5623 GiB（−0.01%） | 全同 |

最大的那根有 148,872 次转移、6.56 GiB 分配，收益仍是 −0.01%。重型根不改变结论：`turn <= 1` 的 5 个站点在搜索里不执行，与树规模无关——这也排除了「轻根把收益摊薄了」这一解释。

## 3. 为什么 5 个 turn ≤ 1 的站点是结构性休眠

宿主在**玩家第一回合 Play 阶段**捕获搜索根（G1.3），也就是说第一回合的回合开始块与「抽牌前」遗物块已经包含在根状态里，搜索只重放根之后的转移。`Toolbox` / `OrangeDough` / `ChoicesParadox` / `VexingPuzzlebox` / `BigHat` 的守卫都是 `turn <= 1`，因此在这条路径上几乎不会命中；`Crossbow` 没有回合守卫，每个回合开始都会触发，所以只有它有量级。

直接量化：用基线（已含 GetId 记忆化）DLL 对含 Toolbox 的 `sel-necrobinder-monster-04` 做 20k 分配 trace（同 §2 命令 + `dotnet-trace`），全栈聚合里

- 整个 `Toolbox` 栈：**0.0002 GiB**（已覆盖栈 4.12 GiB 的 0.005%）；
- `GetUnlockedCards` 全部：0.0003 GiB；
- 同一次 trace 的 `Slugify` 只剩 0.0015 GiB，与上一轮记忆化一致。

按仓库纪律（「收益小且扩大语义验证面的微优化保留简单实现」），这 5 个站点已回退到原实现；将来若出现**在第一个回合开始之前**捕获搜索根的入口（例如战前预测），它们会重新变成热点，届时可以按同一处一行式改写接回缓存 helper。

## 4. 验证与限制

- Release 构建 0 警告 / 0 错误；`./tools/verify-refactor-boundaries.sh` 输出 `REFACTOR_BOUNDARIES_OK`。
- 语料侧：`data3` 全部 300 个请求用 `--milestone M1` 解析过遗物（Toolbox 4.3%、VexingPuzzlebox 2.0%、OrangeDough 0.3%；ChoicesParadox / BigHat / Crossbow 不在可随机池里，用显式注入构造根）。
- 构造根是「同一请求 + 显式注入一件遗物」，不是语料根；构造只影响遗物列表，A/B 两侧完全相同。
- 只测了离线无头宿主、20k 节点、DOP16/DOP1、No-GC 12 GB；没有可见 Steam 会话、没有 Windows 构建、没有 500k 节点完整 VeryHigh。
- Crossbow 在实机的获取频率未知（不在本语料的 Common/Uncommon/Rare/Shop 随机池里），所以这是「该遗物出现时的单根收益」，不是整批期望收益。

## 5. 提案（已评估后放弃）：其它角色卡池的根快照，覆盖 SPLASH 剩余枚举

**决定：不做（2026-09-19）。** 下面保留完整的提案、门禁清单与成本/收益上限，作为将来若出现「战前预测」这类在**第一回合开始之前**捕获搜索根的入口时的备查记录。放弃理由按重要性：

1. **方向与「主线程卡顿优先」相反。** `CombatRootSnapshot.Capture` 在主线程捕获并验证 live 状态（见 [AGENTS.md](../../AGENTS.md) 4.1），搜索是后台任务。这个方案把卡池枚举从后台搜索挪进主线程根捕获：搜索内数字会变好看，但玩家可能感知到主线程卡顿；这属于「把成本挪个地方」，不是优化。
2. **收益上限小且窄。** 同一根分配再降约 10%、墙钟 1~2%，且只对含 `SPLASH` 的根有效（语料 5.7%）；那根已经拿到 −66% 分配 / −57% 墙钟，边际价值低。
3. **要证明它不是负优化，必须扩语义面。** 需要新 `ScenarioId` 夹具 + 五类合同断言 + 逐次无头实例运行，成本大于收益上限。

另外两条前置事实也削弱了它的必要性：触发路径全仓只有 `SplashOnPlay` 一处；GetId 记忆化之后那条路径的剩余成本是纯 LINQ/池遍历，没有第二个「正则级」的支配项。

**若将来要做，下面是当时的完整设计。**



**目标**：把 `SplashOnPlay` 对 `UnlockState.CharacterCardPools`（其它角色卡池）的每次枚举也换成根级只读快照。上一轮的分配 trace 显示，GetId 记忆化之后这条路径仍占 defect 根已覆盖栈的约 10%（`CardModel[]` 0.244 + `Func<CardModel,bool>` 0.199 + `AbstractModel[]` 0.184 + `List<CardModel>` 0.178 GiB，共 6.16 GiB）。**收益上限**：`sel-defect-elite-01` @100k 分配 33.47 GiB → 约 30 GiB（≈ −10%），墙钟估计 −1%~−2%；只对含 `SPLASH` 的根（语料 5.7%）有效。

**需要逐项核对的点**（按既有根生成池合同）：

1. `player.UnlockState.CharacterCardPools` 的**列表身份与顺序**：根捕获时冻结该序列；查询时要求 `ReferenceEquals(player.UnlockState, capturedUnlockState)` 且逐项引用相等。顺序参与 `SelectMany` 的拼接顺序，不能只比集合。
2. `pools.Count > 1` 时移除 `player.Character.CardPool`：必须在冻结列表上复现同一移除（按引用），保证拼接顺序与原实现一致。
3. 每个池都过 `TryCaptureNativeCharacterGenerationPool` 同款门禁：原生程序集、`!IsMutable`、`!IsMock`、`ReferenceEquals(pool, ModelDb.GetById<CardPoolModel>(pool.Id))`、`ReferenceEquals(pool.AllCards, capturedAllCardsIdentity)`。
4. 约束相等（`multiplayerConstraint == _multiplayerConstraint`）与角色身份（`ReferenceEquals(player.Character, capturedCharacter)`）。
5. 每张卡：`ReferenceEquals(card, card.CanonicalInstance)`、`!card.IsMutable`、原生程序集。
6. 任一门禁失败 → 回退整条原版 `GetUnlockedCards` 路径；不允许部分使用缓存数组。
7. 过滤顺序：原实现是 `concat(所有池的 GetUnlockedCards) → Where(Attack) → FilterForCombatAndPlayerCount → TakeRandom`；缓存数组已按池 `FilterForCombatAndPlayerCount`，因此拼接后是 `concat(filter(A_i)) → Where(Attack)`。两者保序且过滤可交换，但**必须在合同测试里逐项证明**，不能只靠推理。
8. `UpgradeIf(card.IsUpgraded)` 与 `GetDistinctForCombat` 的 `PredictedCard.Create` 逐分支独占语义不变；RNG 仍是 `TakeRandom(count, rng)`。

**需要补的合同测试**（参照既有 `UnattendedTestRunner.CharacterGenerationPoolCache` / `TurnStartGenerationCache` / `ForkBoundaries`）：

- 同 RNG 种子下缓存路径与原路径选中序列**逐位相同**（含 0 候选、恰好 1 个池、池列表含可变/第三方池）；
- 门禁逐条失效各回退一次：池变 mutable、`AllCards` 换成新数组、约束不同、解锁状态换实例、卡被 clone 成非规范实例；
- 第三方池（`ModCharacterPoolProbe`）必须回退，不得进入缓存；
- 多玩家与 Fork：子分支改 RNG/池不清空父的缓存，`Fork` 后两边各自独立；
- 捕获成本：根捕获时多枚举 4~5 个角色池（每池 ~85 张），需要报出 root capture 的增量时间与内存（约几 KB/玩家）。

**风险点**：解锁状态在战斗中被改写（例如获得新卡池的遗物/事件）会让快照失效——用第 1、2 条门禁挡住并回退；第三方角色/卡池必须整体旁路；该特化会扩大 `SplashOnPlay` 的语义验证面，需要与既有生成池复用同级的完整 A/B（同根 DOP1 逐位 + DOP16 交错 + 全部剪枝计数 + `compare_results.py`）。
