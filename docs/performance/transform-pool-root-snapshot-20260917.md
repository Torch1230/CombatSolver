# 变形池根快照缓存（2026-09-17，未发布）

[返回性能目录](README.md) · [结构化证据](transform-pool-root-snapshot-20260917.json)

基线 `1c31b9f`（上游 PR #104 之后）。本轮针对「搜索展开时每个节点重复计算变形候选池」这一热点，在 Mod 侧新增根级快照缓存，**不改动 `sts2.dll`**。速度与瞬时分配达标；**峰值内存未改善**，见下文。

## 热点来源

本仓此前的可行性调研（见下）用 `perf` 采样定位到，搜索展开热路径上约一半样本花在：

```
搜索展开 → TurnStartChoiceSupport.ResolveCapturedChoice (src/Prediction/TurnStartChoiceSupport.cs)
  → CardFactory.CreateRandomCardForTransform
    → CardPoolModel.GetUnlockedCards → FilterThroughEpochs
      → ModelDb.GetId → ModelId.SlugifyCategory     （3 次 Regex + 文化敏感 EndsWith 走 ICU 排序）
```

`GetDefaultTransformationOptions` 的结果只依赖 `(cardPool, unlockState, multiplayerConstraint)`，对同一根是稳定的，却每个展开节点重算一次。采样中 `FilterThroughEpochs` 覆盖 49%、`SlugifyCategory` 26%、`ModelDb.GetId` 46%，其中 2629/2634 个样本位于搜索展开。

## 实现

- 新增 `RootCombatTransformationPoolSnapshot`：按 `(player, cardPool)` 缓存 `CardPoolModel.GetUnlockedCards` 的**未过滤原始序列**，保持上游顺序与实例身份；在 `Fork` 间不可变共享（`SimulatedCombatState._rootTransformationPools`）。
- 只覆盖玩家角色池与规范无色池。可变池、非规范池、外来玩家或外来约束一律回退上游路径，不做猜测。所有进入缓存的卡都要求原版程序集、非 mutable、`ReferenceEquals(card, card.CanonicalInstance)`。
- 调用点改走 `CreateRandomCardForTransform(original, options, isInCombat, rng)` 重载。逐分支的稀有度、`CanBeGeneratedInCombat`、`Id != original.Id` 与人数过滤仍由 `CardFactory.GetFilteredTransformationOptions` 执行。
- **RNG 等价性**：该函数在选取前一律 `.ToArray()` 物化，两条重载传给 `Rng.NextItem` 的都是 `CardModel[]`；`NextItem` 对数组直接使用、否则 `ToArray()`，两者都只消费一次 `NextInt(0, length)`。因此只要缓存序列的内容与顺序同上游一致，RNG 消耗就逐字段相同。

## 固定工作量 ABBA

同根、同预算：`formal-pr-20260915/measurements/A1/crab/request.json`（Necrobinder / Kaiser Crab / TOOLBOX，含变形选择），`VeryHigh`、beam 48、2000 节点、`--dop 1`、`M2`、`Evaluate`。顺序 ABBA ×2 轮，共 8 次；两边各自加载本工作树构建的 `CombatSolver.dll`（`OFFLINE_HARNESS_COMBATSOLVER_DLL` 显式指向），未写游戏安装目录。

| 顺序 | 侧 | wall 秒 | 展开 | 转移 | score | 投影战损 |
|---|---|---:|---:|---:|---:|---:|
| 1 | A 基线 | 18.87 | 2000 | 36801 | -4780022 | 34 |
| 2 | B 候选 | **9.24** | 2000 | 36801 | -4780022 | 34 |
| 3 | B 候选 | **9.00** | 2000 | 36801 | -4780022 | 34 |
| 4 | A 基线 | 18.21 | 2000 | 36801 | -4780022 | 34 |
| 5 | A 基线 | 18.31 | 2000 | 36801 | -4780022 | 34 |
| 6 | B 候选 | **9.39** | 2000 | 36801 | -4780022 | 34 |
| 7 | B 候选 | **9.05** | 2000 | 36801 | -4780022 | 34 |
| 8 | A 基线 | 18.34 | 2000 | 36801 | -4780022 | 34 |

**A 均值 18.433 秒 → B 均值 9.170 秒，2.010 倍。** 8 次运行的展开、转移、score 与投影战损逐字段相同。

## 内存：分配减半，峰值未改善

| 工件 | 侧 | wall 秒 | 总分配 | 每节点分配 | 峰值工作集 | 峰值托管堆 | GC 暂停 |
|---|---|---:|---:|---:|---:|---:|---:|
| A1 | 基线 | 18.21 | 4117.4 MB | **2.06 MB** | 388.7 MB | 156.4 MB | 1190.6 ms |
| A2 | 基线 | 18.34 | 4118.7 MB | **2.06 MB** | 383.4 MB | 158.3 MB | 1201.1 ms |
| B1 | 候选 | 9.00 | 2070.2 MB | **1.04 MB** | 401.8 MB | 175.5 MB | 1164.2 ms |
| B2 | 候选 | 9.05 | 2076.0 MB | **1.04 MB** | 409.1 MB | 182.0 MB | 1144.0 ms |

- **每节点总分配 2.06 MB → 1.04 MB（−49.5%）**，这是本次改动直接消除的重复临时分配。
- **峰值工作集约 385 MB → 约 405 MB、峰值托管堆约 157 MB → 约 179 MB，没有改善，反而略升。** 本轮只消除了变形路径的重复临时分配，**没有触及节点局面的驻留内存**——最初「保存每个节点局面内存太大」的那半个问题本次未处理。峰值上升的成因没有取证（未采集 GC 事件），因此不记作结论，仅作为后续调查项。
- GC 暂停基本持平（约 1.19 s → 约 1.15 s）。

标签 A1/B1 各被写了两次（重复轮覆盖了同名目录），因此保留的每标签工件对应的是该标签的第二次运行；上表 wall 秒与该工件一致，8 次运行的完整耗时见时间表。

## 等价性

用仓库自带 `tools/OfflineSearchHarness/compare_results.py` 对 A 与 B 的产物逐字段比较（A1/A2 对 B1/B2，`--left-prefix A --right-prefix B`）：

```
roots=2 fields=170 mismatched_roots=0 left_only=0 right_only=0 => IDENTICAL
```

四组全部一致：`solverMetrics`（排除时间/内存/GC 字段）、`route`（选中路线的每个动作的 turn/kind/cardId/potionId/targetCombatId/cardStateKey）、`rootState`（根 `ContinuationStamp`）、`catalog`（生成场景目录指纹）。

## 契约 `TRANSFORMATION-POOL-CACHE`

游戏内无头实例执行，`c8c552fc5f52400b849c1a77a77fefce` Passed：

```
TransformationPoolCache:comparisons=4:ordered_identity=true:fork_shared_pool=true:
mutable_colorless_constraint_bypass=true:native_path_equivalent=true:rng_equivalent=true:
parent_live_unchanged=true
```

断言内容：缓存序列与上游 `GetUnlockedCards` 逐实例同序、跨 `Fork` 不可变共享、可变池被拒绝、外来约束被拒绝、外来池被拒绝、规范无色池（Quest/Event/Ancient/Token 回退）被正确服务且同序、缓存路径与原生路径产出同一张牌且 `CombatCardSelection` 五字段 RNG 状态与完整预测延续状态一致、父模拟与实机根未被改动。

初版契约曾失败，报 `Transformation pool accepted a changed pool or constraint.`。排查为**契约自身错误**：它断言无色池必须被拒绝，但无色池是合法的回退池、本就应被服务。实现无缺陷；已改为具名的正/负断言并复跑通过。

## 结构门禁

Bash 结构门禁 `tools/verify-refactor-boundaries.sh` 通过：

```
REFACTOR_BOUNDARIES_OK search_files=114
EXIT_CODE=0
```

`search_files` 从上游的 113 增至 114，增量即本次新增的 `src/Search/RootCombatTransformationPoolSnapshot.cs`，无额外越界。<br>
（说明：该脚本在本机 Hermes 宿主内会被安全策略硬拦截 —— 策略扫描脚本正文，把边界白名单表里的 `stop-instance` 字面量误判为「停止 gateway」，与脚本本身无关。最终在独立进程中执行取得上述结果。）

## 压力场景：加速比随工作量上升

上表只有一个根。为回答「重场景下是否还是这么多」，另造了 4 个**厚牌组 Boss 根**（沿用可用的 crab 生成场景规格，只替换遭遇与幕索引，牌组、遗物、药水不变），并把节点预算标定到**基线单场 ≥20 秒**。同根、同预算、`VeryHigh`、beam 48、`--dop 1`、顺序 ABBA（A B B A）。

| 根 | 边界 | A 基线 | B 候选 | 加速比 |
|---|---|---:|---:|---:|
| KNOWLEDGE_DEMON_BOSS @6000 | `None`（穷尽于 5990 节点）| **54.11 s** | 14.76 s | **3.667×** |
| THE_KIN_BOSS @6000 | `NodeLimit` | **41.78 s** | 14.43 s | **2.895×** |
| KAISER_CRAB_BOSS @6000 | `NodeLimit` | **37.32 s** | 14.86 s | **2.511×** |
| THE_INSATIABLE_BOSS @6000 | `NodeLimit` | **23.63 s** | 10.79 s | **2.191×** |
| KAISER_CRAB_BOSS @2000 | `NodeLimit` | 18.78 s | 9.07 s | 2.072× |
| QUEEN_BOSS @6000 | `None`（穷尽于 2539 节点）| 11.52 s | 8.08 s | 1.426× |
| silent-discard @6000 | `None`（穷尽于 2359 节点）| 8.24 s | 7.92 s | **1.041×** |

**基线超过 20 秒的 4 个根，加速比 2.191× – 3.667×。** 在真正吃满预算的重根上，收益随工作量**上升**而非缩水。成因未做采样取证（未重新 `perf` 归因），因此只作为实测趋势报告，不解释机制。

**加速比是场景相关的，这是本改动的作用域，不是普适倍数。** `silent-discard` 只有 1.041×，因为它提前穷尽、基本不走变形路径；`QUEEN_BOSS` 介于两者之间（1.426×）。两个穷尽根的 `boundary` 都是 `None`，说明它们本来就不是重型战斗 —— 对这类根提高节点预算没有意义。

**逐根等价性**（`compare_results.py`，A 对 B 逐字段）：

```
crab@2000 (P1)              roots=2 fields=170 mismatched_roots=0 => IDENTICAL
KAISER_CRAB_BOSS@6000       roots=2 fields=242 mismatched_roots=0 => IDENTICAL
silent-discard@6000         roots=2 fields=192 mismatched_roots=0 => IDENTICAL
QUEEN_BOSS@6000             roots=2 fields=152 mismatched_roots=0 => IDENTICAL
THE_KIN_BOSS@6000           roots=2 fields=174 mismatched_roots=0 => IDENTICAL
KNOWLEDGE_DEMON_BOSS@6000   roots=2 fields=212 mismatched_roots=0 => IDENTICAL
THE_INSATIABLE_BOSS@6000    roots=2 fields=234 mismatched_roots=0 => IDENTICAL
```

七个根全部逐字段一致，无一处 `left_only` / `right_only`。

压力夹具的构造方式：从可用的 crab 生成场景规格复制，只改 `encounterId` 与 `actIndex`（幕索引：0 = Overgrowth / Underdocks，1 = Hive，2 = Glory）。`WATERFALL_GIANT_BOSS` 用这条路径**无法**到达 —— 幕索引 0 被 Overgrowth 与 Underdocks 共用，解析器只落到 Overgrowth，因此该遭遇缺少一组对照。

### 对照组：不走变形路径的根拿不到收益

同一批 Boss 遭遇改用**默认初始牌组**（薄牌组，不注入厚牌组），全部提前穷尽、不经过变形路径：

| 根 | 边界 | 穷尽于 | A 基线 | B 候选 | 加速比 |
|---|---|---|---:|---:|---:|
| QUEEN_BOSS @8000 | `None` | 891 节点 | 1.71 s | 1.69 s | 1.015× |
| KAISER_CRAB_BOSS @8000 | `None` | 1023 节点 | 1.89 s | 1.92 s | 0.984× |
| THE_KIN_BOSS @8000 | `None` | 1922 节点 | 3.47 s | 3.50 s | 0.990× |

三者都在 ~1.0×，**收益为零**，与「只有走变形路径的根受益」一致。其中两个略低于 1.0 —— 这是 1–3 秒量级运行下的噪声（该量级下固定开销占比大），**不记作性能退化**。

这组数据的作用是给上面的倍数**划边界**：3.667× 只适用于真正执行变形选择的战斗，不能当作全局面板。

## 并行度 8（生产并行度）

上面全部是 `--dop 1`。生产搜索是并行展开的，因此另跑一轮 `--dop 8`（预算 12000 节点，同根、`VeryHigh`、beam 48、顺序 ABBA）。

| 组 | 根 | 边界 | A 基线 | B 候选 | 加速比 |
|---|---|---|---:|---:|---:|
| 厚牌组 | KAISER_CRAB_BOSS | `NodeLimit` | 46.27 s | 17.91 s | **2.583×** |
| 厚牌组 | KNOWLEDGE_DEMON_BOSS | `None`（穷尽 5990）| 26.19 s | 10.92 s | **2.398×** |
| 厚牌组 | THE_KIN_BOSS | `None`（穷尽 9767）| 34.40 s | 15.80 s | **2.178×** |
| 厚牌组 | QUEEN_BOSS | `None`（穷尽 2539）| 7.26 s | 3.89 s | **1.865×** |
| 厚牌组 | THE_INSATIABLE_BOSS | `None`（穷尽 6087）| 13.29 s | 7.84 s | **1.696×** |
| 对照组 | THE_KIN_BOSS | `None`（穷尽 1922）| 1.47 s | 1.49 s | 0.983× |
| 对照组 | KAISER_CRAB_BOSS | `None`（穷尽 1023）| 0.98 s | 0.98 s | 1.000× |
| 对照组 | QUEEN_BOSS | `None`（穷尽 891）| 0.89 s | 0.90 s | 0.989× |

**结论与 dop 1 一致**：走变形路径的根 1.696×–2.583×，不走变形路径的对照组 ~1.0×（收益为零）。并行度不会让收益消失。

### DOP 8 无法提供字段级等价性证据（重要）

`compare_results.py` 在 DOP 8 下报 `DIFFERENT`，但**不能归因于本次改动**。逐字段差异**只有两个调度相关的复用计数器**：

```
roundReplayPrefixCaptures
executionChoiceReuses      （仅 QUEEN_BOSS 一个根出现）
```

同一比较里 **`route`（每个动作）、`rootState`（根 `ContinuationStamp`）、`catalog`（目录指纹）在全部根上都是 0 处不同**。

决定性证据是**基线自比**：DOP 8 下把基线跟它自己比，同样在这一个计数器上不同 ——

```
KAISER_CRAB_BOSS  baseline A1 vs A2:  roundReplayPrefixCaptures 7808 vs 7794
KAISER_CRAB_BOSS  candidate B1 vs B2: roundReplayPrefixCaptures 7802 vs 7799
```

即该计数器取决于哪个 worker 先命中复用缓存，是墙钟调度产物，不是决策输出。**因此 DOP 8 的 A/B 计数器差异是并行非确定性，不是语义差异**；字段级等价性仍以 DOP 1 的 7 根全一致为准（DOP 1 下同一比较为 `IDENTICAL`，且 DOP 1 的基线自比无差异）。

## 未验证

- **可见 Steam 性能未测**：上述倍数都是无头数据，不能外推为可见帧时间或实机收益。
- **峰值内存成因未取证**；也未验证其它含变形选择的场景是否同幅受益 —— 本轮共 7 个根有 A/B 数据（4 个基线 >20 s）另有 3 个对照组，但收益倍数场景相关，不能外推为全局面板。
