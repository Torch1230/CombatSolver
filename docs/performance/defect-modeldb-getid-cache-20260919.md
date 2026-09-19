# 重型根的 ModelDb.GetId 正则归因与纯值记忆化（2026-09-19）

本轮目标是在不改变决策的前提下降低重型战斗根的真实耗时与内存。**主结果**：`sel-defect-elite-01`、100,000 节点、DOP16、VeryHigh、Evaluate、No-GC 16 GB 下，墙钟 67.46 s → **32.19 s（−52.3%）**、selected 分配 90.83 GiB → **33.47 GiB（−63.2%）**、峰值 RSS 28.68 GiB → **14.03 GiB（−51.1%）**、每转移分配 155.9 KB → **54.3 KB（−65.1%）**，score / 战损 / finalHp / 结束回合 / planActions 逐项不变。

改动只有一处：新增 [ModelDbGetIdCachePatch](../../src/Runtime/ModelDbGetIdCachePatch.cs)，把原生 `ModelDb.GetId(Type)` 的纯类型→`ModelId` 映射缓存起来。

## 1. 归因：不是 fork，也不是容器增长

对基线（ce868b6）在 `sel-defect-elite-01`、20,000 节点、DOP16、No-GC 16 GB 下做 `dotnet-trace` 分配采样（`Microsoft-Windows-DotNETRuntime:0x1:5`），再用 [GcTraceAnalysis](../../tools/GcTraceAnalysis/README.md) 按调用栈聚合：

```bash
mkdir -p /tmp/cstr && TMPDIR=/tmp/cstr ~/.dotnet/tools/dotnet-trace collect \
  --providers Microsoft-Windows-DotNETRuntime:0x1:5 --buffersize 512 \
  --output .local/bench/trace/alloc-20k.nettrace -- \
  dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request "$PWD/.local/learned-selector/data3/requests/sel-defect-elite-01.json" \
  --label tr20k-defect --out .local/bench/trace/out-tr20k \
  --profile VeryHigh --nodes 20000 --dop 16 --budget-ms 600000 --search-mode Evaluate \
  --enable-no-gc-region --no-gc-region-budget-gigabytes 16 --milestone M2 --measure-phases

dotnet tools/GcTraceAnalysis/bin/Release/net9.0/GcTraceAnalysis.dll \
  --input .local/bench/trace/alloc-20k.nettrace --output .local/bench/trace/alloc-20k-all.json --top 100000
```

`TMPDIR` 必须短：`dotnet-trace` 在 `TMPDIR` 下建 Unix domain socket，仓库内路径会超过 108 字符上限而启动失败。

采样覆盖 199,368 个 allocation tick、confirmed search 20.98 GB。**把全部栈按“是否经过 `StringHelper.Slugify` / `ModelId.SlugifyCategory`”分桶后，69%（12.31 GiB / 17.78 GiB 已覆盖栈）落在正则 slug 上**，即 confirmed search 的约 59%。类型侧完全吻合：

| 类型 | 估计字节 | 采样数 | 说明 |
|---|---:|---:|---|
| `System.Int32[]` | 7.49 GiB | 75,441 | `RegexRunner` 的 `runtrack`/`runcrawl`/`matchindexes` |
| `System.String` | 2.20 GiB | 22,128 | slug 结果与被替换片段 |
| `Regex+Runner` | 1.80 GiB | 18,137 | 生成正则每次扫描新建 runner |
| `Regex.Match` | 1.68 GiB | 16,937 | 每次匹配一个对象 |
| `System.Int32[][]` | 0.59 GiB | 5,956 | 同上 |

栈叶子（同一批 slug 栈内）：`RunAllMatchesWithCallback` 7.21 GiB、`RegexRunner.InitializeForScan` 1.89 GiB、`TextInfo.ChangeCaseCommon` 0.97 GiB、`SegmentsToStringAndDispose` 0.64 GiB、`Match.AddMatch`+`Match..ctor` 0.77 GiB、三个源生成正则的 `RunnerFactory.CreateInstance` 合计 0.68 GiB。

调用链唯一：

```
CardGenerationCardMirrors.SplashOnPlay
  → Player.GetUnlockedCards(pool, constraint)        (遍历 UnlockState.CharacterCardPools 的每个角色卡池)
  → CardPoolModel.GetUnlockedCards
  → IroncladCardPool.FilterThroughEpochs
  → Epochs.Ironclad{2,5,7}Epoch.get_Cards()
  → ModelDb.Card<T>() → ModelDb.Get<T>() → ModelDb.GetId<T>() → ModelDb.GetId(Type)
  → ModelDb.GetEntry(Type) → StringHelper.Slugify(type.Name)          (CamelCase/Whitespace/SpecialChar 三次 Regex.Replace)
  → ModelDb.GetCategory(Type) → ModelId.SlugifyCategory(...)          (同一 Slugify + 去 "_MODEL" 后缀)
```

按发起方分桶：`PredictionExtensions.GetUnlockedCards` 6.39 GiB（51.9%）、`SplashOnPlay` 直接调用 5.78 GiB（47.0%）、其余 ≤0.14 GiB。也就是说**一次搜索里反复枚举“每个角色卡池的可生成攻击牌”，每次枚举都把每个模型类型名重新正则 slug 化一遍**。`Epoch.get_Cards()` 与 `ModelDb.GetId` 都没有缓存。

## 2. 核对：`ModelDb.GetId(Type)` 是纯函数

用 `.local/bench/ilprobe`（临时 IL 探针，未入库）反射读取 `sts2.dll` 的方法体：

| 方法 | IL 结论 |
|---|---|
| `ModelDb.GetEntry(Type)` | `Slugify(type.Name)` |
| `ModelDb.GetCategory(Type)` | `SlugifyCategory(GetCategoryType(type).Name)`；`GetCategoryType` 只沿 `BaseType` 上溯到 `AbstractModel` |
| `ModelDb.GetId(Type)` | `new ModelId(GetCategory(type), GetEntry(type))` |
| `StringHelper.Slugify(string)` | 三次 `Regex.Replace`，大小写用 `ToUpperInvariant`（culture 无关） |
| `ModelId` | 不可变 record（`EqualityContract`/`<Clone>$`/`PrintMembers`，只有 getter 与 backing field，无 setter） |

`ModelDb` 的可变状态只有内容字典 `_contentById`、`Inject/Remove/ResetForTest`（增删模型内容）与 `_initialCapacity`；`GetId` 不读它们。因此 `Type → ModelId` 是纯值映射，缓存不改变任何一次查询的结果。

实现（[ModelDbGetIdCachePatch.cs](../../src/Runtime/ModelDbGetIdCachePatch.cs)）：

- `Prefix`：`ConcurrentDictionary<Type, ModelId>` 命中就写回 `__result` 并跳过原方法；`type` 为 null 时放行原方法，不把 `NullReferenceException` 换成字典异常。
- `Postfix`：只在原方法正常返回后写入；原方法抛异常时 `Postfix` 不运行，失败不会被固化。
- 缓存只保存 `Type` 与不可变 `ModelId`，不保存模型实例、不进入战斗状态键、不跨根共享分支值；键空间是模型类型（原生 1,660 个）与模组注入类型，无需淘汰。
- 生产在 [Entry.cs](../../src/Runtime/Entry.cs) 注册；离线宿主把它加进 `SearchPatchTypes`，否则跑批测不到实机路径。宿主行走行随之从 `patches_applied=9/14` 变为 `10/14`，`patchLog` 里基线侧是 `ModelDbGetIdCachePatch: 缺少类型，跳过`、候选侧是 `已装 1 个目标`。

## 3. 同根 A/B

命令模板（每根每种构建各 3 次，交错执行，`--measure-phases` 只在其中一次打开）：

```bash
TMPDIR="$PWD/.local/tmp" timeout 900 dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request "$PWD/.local/learned-selector/data3/requests/<root>.json" --label <label> \
  --out .local/learned-selector/memory-e2e/<label> \
  --profile VeryHigh --nodes 100000 --dop 16 --budget-ms 600000 --search-mode Evaluate \
  --enable-no-gc-region --no-gc-region-budget-gigabytes 16 --milestone M2
```

两侧用同一个宿主二进制，只通过 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 切换模组产物（基线 = HEAD ce868b6 的 DLL，候选 = 加补丁后的 DLL）。

### 3.1 主目标 `sel-defect-elite-01` @100k DOP16（各 3 次）

| 指标 | 基线中位 | 基线范围 | 候选中位 | 候选范围 | 变化 |
|---|---:|---|---:|---|---:|
| 墙钟 s | 67.46 | 66.78~67.86 | **32.19** | 31.96~32.82 | **−52.29%** |
| 峰值 RSS GiB | 28.68 | 27.79~28.70 | **14.03** | 14.03~14.11 | **−51.07%** |
| selected 分配 GiB | 90.83 | 88.86~91.04 | **33.47** | 33.47~33.49 | **−63.15%** |
| KB/转移 | 155.86 | 152.48~156.22 | **54.34** | 54.34~54.38 | **−65.13%** |
| CPU s（user+sys） | 426.4 | 426.3~426.5 | **270.7** | 268.7~277.5 | **−36.5%** |
| GC 暂停 ms | 873 | 67~881 | 15 | 13~1300 | 抖动大，不作结论 |
| Gen2 | 20 | 20~20 | 6 | 6~6 | −70% |
| 展开 | 100000 | — | 100000 | — | 0 |
| 转移 | 611114 | 611114~611114 | 645830 | 645830~645830 | +5.68% |
| 选择分支 | 41288 | — | 67554 | — | +63.62% |
| score | 10000514973 | — | 10000514973 | — | 0 |
| 战损 / finalHp / 结束回合 / planActions | 29 / 46 / 6 / 27 | — | 29 / 46 / 6 / 27 | — | 全同 |

（基线的 CPU 只有 2 个样本带 `time` 记录：426.5 s 与 426.3 s。）

### 3.2 串行对照 `sel-defect-elite-01` @20k DOP1（同根，各 1 次）

| 指标 | 基线 | 候选 | 变化 |
|---|---:|---:|---:|
| 墙钟 s | 34.30 | 15.79 | −53.98% |
| 峰值 RSS GiB | 8.30 | 5.61 | −32.36% |
| 分配 GiB | 8.05 | 5.37 | −33.27% |
| CPU s | 40.2 | 21.2 | −47.32% |
| 展开 / 转移 / 选择分支 | 20000 / 113574 / 11460 | 20000 / 113574 / 11460 | **逐项完全相同** |
| score / 战损 / finalHp / 结束回合 / planActions | 10000514973 / 29 / 46 / 6 / 27 | 同 | **逐项完全相同** |

**这是“决策等价”的关键证据**：DOP1 下没有任何并行调度自由度，两侧转移数与选择分支数逐位相同，说明补丁没有改变任何候选生成、剪枝或保路；DOP16 下的轨迹差异只来自并行调度。全部非时序剪枝计数同样逐项相同：

| 剪枝/复用计数 | 基线 | 候选 |
|---|---:|---:|
| `dominatedActionsPruned` | 1,527 | 1,527 |
| `duplicateCardBranchesPruned` | 2,842 | 2,842 |
| `transpositionBranchesPruned` | 6,219 | 6,219 |
| `reusedNodeSnapshots` | 20,547 | 20,547 |
| `standPatProbes` | 4,015 | 4,015 |
| `shuffleBranchesPruned` / `repeatableNoProgressBranchesPruned` | 0 / 0 | 0 / 0 |
| `topQueueActionsDropped` / `choiceBranchesDroppedByBudget` / `transitionCacheHits` | 0 / 0 / 0 | 0 / 0 / 0 |
| `replayCount` / `forkCount` | 7 / 113,574 | 7 / 113,574 |

DOP16 侧的剪枝计数随轨迹变化（`dominatedActionsPruned` 6,795 → 8,589、`duplicateCardBranchesPruned` 12,895 → 15,192、`transpositionBranchesPruned` 29,071 → 37,842、`standPatProbes` 28,687 → 26,789），与“展开了另一片同规模子树”一致；这些差异属于 §6 的调度产物，不是策略变化。

### 3.3 第二重型根 `sel-necrobinder-elite-01` @100k（各 3 次）

| 指标 | 基线中位 | 基线范围 | 候选中位 | 候选范围 | 变化 |
|---|---:|---|---:|---|---:|
| 墙钟 s | 35.19 | 32.67~36.51 | 37.14 | 35.08~37.25 | +5.54%（在基线漂移内，收益未建立为回退） |
| 分配 GiB | 43.84 | 36.84~43.93 | 41.99 | 41.98~42.02 | −4.22% |
| KB/转移 | 59.71 | 59.59~60.75 | 54.76 | 54.75~54.79 | −8.30% |
| 转移 | 771425 | **635834~771425** | 804057 | 804057~804057 | +4.23% |
| score / 战损 / finalHp / 结束回合 / planActions | 9998584945 / 54 / 12 / 11 / 55 | — | 同 | — | 全同 |

**该根基线自身是双峰的**：同一份 ce868b6 DLL 三次运行给出 771425 / 635834 / 771425 两种轨迹（候选三次全部 804057）。这同时纠正上一轮汇总的一条归因：`docs/performance/dop16-veryhigh-fidelity-20260919.md` 沿用、且本轮任务描述引用的“HEAD 635834 / 基线 771425”不是源码差异，而是同一版本在节点上限处的运行间非确定性；此前把两组样本分别当成两个版本的代表。

### 3.4 哨兵

| 根 | 节点 | 展开 | 转移（基线/候选） | 选择分支 | 决策 | 墙钟 | 分配 |
|---|---:|---|---|---|---|---|---|
| `sel-defect-elite-02` | 20,000 | 10,814（整树穷尽） | 42,752 / 42,752 | 2,422 / 2,422 | 全同 | 4.43 → 4.43 s | 1.72 → 1.72 GiB |
| `sel-silent-boss-01` | 20,000 | 20,000 | 544,653 / 544,653 | 465,076 / 465,076 | 全同 | 26.71 → 26.56 s（3 次中位，范围重叠） | 24.33 → 24.28 GiB |

两个哨兵的转移数、选择分支数在两侧**逐位相同**，说明它们没有走到会受调度影响的路径；决策逐项相同。这两根也没有 Slugify 支配的路径，所以分配几乎不变，符合“只去掉了那一处纯函数成本”的预期。

## 4. 排他阶段表（defect @100k，`--measure-phases`）

时间是多线程经过时间之和，可超过墙钟；分配是排他增量。

| 阶段 | 基线分配 | 基线线程时间 | 候选分配 | 候选线程时间 | 分配变化 |
|---|---:|---:|---:|---:|---:|
| `card_exec` | 66.81 GiB | 255.6 s | **10.24 GiB** | **68.2 s** | −85% |
| `fork` | 8.19 GiB | 23.1 s | 8.82 GiB | 33.3 s | +8%（转移数 +5.7%） |
| `snapshot` | 2.87 GiB | 29.6 s | 3.10 GiB | 41.0 s | +8% |
| `prune` | 1.26 GiB | 5.0 s | 1.23 GiB | 6.2 s | −3% |
| `threat` | 0.94 GiB | 7.6 s | 0.98 GiB | 10.8 s | +4% |
| 其余各阶段 | ≤0.83 GiB | — | 同量级 | — | ±10% 内 |

正则成本全部落在 `card_exec`（`SplashOnPlay` 在出牌结算里），所以只有该阶段出现数量级下降。

## 5. 修复后的分配结构（同一根、同一命令、20k）

| 类别 | 基线 | 候选 |
|---|---:|---:|
| confirmed search 合计 | 20.98 GB | **6.31 GB** |
| slug 正则 | 12.31 GiB | **0** |
| `SimulatedCombatState.Fork` 自身 | ~2.0 GiB | 2.86 GiB（46.4% 已覆盖栈） |
| `PredictionExtensions.GetUnlockedCards` 剩余（无正则） | 0.55 GiB | 0.63 GiB（10.2%） |

候选侧最大单一类型是 `CardModel[]` 0.244 GiB（占已覆盖栈 4.0%），其后是 `Func<CardModel,bool>` 0.199、`AbstractModel[]` 0.184、`List<CardModel>` 0.178、`StrategicEffectRequirements` 0.146、`Int32[]` 0.140。**归因已经从“单一支配项”变成“长尾”**：本任务提示里的 fork、容器增长、保路 churn 三类目标，现在每类都 ≤4%，没有与 Slugify 同量级的零决策代价目标。第二轮针对卡池枚举（`GetUnlockedCards`/Splash 的 LINQ + 池快照复用）需要按根生成池的逐项核对规则单独审计，不属于本轮。

## 6. DOP16 轨迹差异的解释

补丁把每个父节点的实测分配压低约 65%，而并行展开的外层准入按“实测父分配 × 安全系数 + 突发余量”预约内存（见 [并行准入规则](../ARCHITECTURE.md)）。观测到的直接后果是 `parallel_waves` 从 7,820 降到 3,690（工作项基本不变），即每个准入窗口装下更多父节点；在节点上限处被展开的节点集合因此不同。

- 决策结论不受影响：`sel-defect-elite-01` 的 score / 战损 / finalHp / 结束回合 / planActions 与全部哨兵逐项相同；
- 语义不受影响：DOP1 下转移与选择分支逐位相同；
- 这是既有自适应调度对真实输入变化的反应，不是等价关系被改掉。若后续要消除这类根间差异，应改调度政策本身（固定批量或确定性合并），那属于搜索行为改动，需单独门禁。

## 7. 生产配置（极高 + 16 并行 + No-GC 12 GB）与推广范围

### 7.1 12 GB 区域预算下的主目标

上面 §3 用的是任务模板的 16 GB 区域预算。把 No-GC 区域换成生产常用的 **12 GB**（`--no-gc-region-budget-gigabytes 12`，其余命令不变）后，`sel-defect-elite-01` @100k（各 1 次）：

| 指标 | 基线 | 候选 | 变化 |
|---|---:|---:|---:|
| 墙钟 s | 70.26 | **30.45** | **−56.7%** |
| 分配 GiB | 92.70 | **31.99** | **−65.5%** |
| 峰值 RSS GiB | 21.32 | **10.54** | **−50.6%** |
| KB/转移 | 163.9 | **52.9** | **−67.7%** |
| 转移 | 592,967 | 634,136 | +6.9%（§6 的调度产物） |
| score / 战损 / planActions | 10000514973 / 29 / 27 | 同 | 全同 |

区域预算从 16 GB 降到 12 GB 后两侧的峰值 RSS 都明显下降（基线 28.68 → 21.32 GiB），而补丁的相对收益反而略增：区域越小、回收检查点越频繁，减少分配的价值越高。

### 7.2 收益范围：由“牌组里有没有 SPLASH”精确决定

触发路径在仓库里只有一处：`CardGenerationCardMirrors.SplashOnPlay` 是唯一枚举 `UnlockState.CharacterCardPools`（其它角色卡池）的地方；其余生成类卡牌与药水都走已验证的根生成池快照，不经过 `ModelDb.GetId` 热路径。

用 M1 里程碑（`--milestone M1`，只建局不搜索，约 2.5 s/根）把 `data3` 全部 300 个请求的牌组解析出来：**17 根（5.7%）的无色卡位含 `SPLASH`**，分布覆盖全部五个角色（DEFECT 5 / IRONCLAD 5 / NECROBINDER 3 / REGENT 3 / SILENT 1）与全部三种遭遇类型（精英 6 / 首领 6 / 普通 5）。

随机抽 14 根做同根 A/B（@20k、12 GB、各 1 次）：

| 根 | 含 SPLASH | 墙钟变化 | 分配变化 | KB/转移 | 决策 |
|---|---|---:|---:|---|---|
| `sel-defect-elite-01` | 是 | **−31.9%** | **−72.8%** | 197.5 → 53.4 | 全同 |
| `sel-necrobinder-elite-01` | 是 | **−14.9%** | **−27.7%** | 65.2 → 47.1 | 全同 |
| 其余 12 根（defect-elite-03 / defect-monster-05 / defect-boss-02 / ironclad-elite-01 / ironclad-monster-03 / ironclad-boss-04 / silent-elite-01 / silent-monster-02 / silent-boss-01 / regent-elite-01 / regent-monster-11 / necrobinder-monster-04） | 否 | −3.2% ~ +2.1% | −1.6% ~ +0.3% | ±2% | 全同 |

再补测 6 根**其它角色**的 SPLASH 根（@20k、12 GB、各 1 次），全部为正收益且决策全同：

| 根 | 角色 | 墙钟变化 | 分配变化 | KB/转移 |
|---|---|---:|---:|---|
| `sel-defect-boss-12` | DEFECT | −40.1% | −66.8% | 117.4 → 39.0 |
| `sel-ironclad-elite-03` | IRONCLAD | −29.2% | −52.9% | 98.4 → 46.3 |
| `sel-silent-boss-15` | SILENT | −28.6% | −33.4% | 66.7 → 44.4 |
| `sel-ironclad-monster-11` | IRONCLAD | −11.3% | −20.4% | 52.9 → 42.1 |
| `sel-necrobinder-boss-02` | NECROBINDER | −7.9% | −18.2% | 53.0 → 43.3 |
| `sel-regent-monster-05` | REGENT | −5.3% | −10.7% | 41.9 → 38.4 |

结论：已测 **8/8 含 SPLASH 的根**分配下降 10.7%~72.8%、墙钟下降 5.3%~56.7%，**12/12 不含 SPLASH 的根**在抖动内；收益幅度随该局实际打出 SPLASH 的次数变化。`SPLASH` 是无色牌，任何角色、任何遭遇都可能拿到，所以这不是 Defect 专属问题。

### 7.3 为什么这类问题还会再出现，以及本补丁覆盖到哪一层

- 此前的 `CanonicalModels`（[源码](../../src/Engine/Common/CanonicalModels.cs)）只按封闭泛型类型缓存了**我们自己的** `ModelDb.Card<T>()` 调用；vanilla 的调用点（`Epoch.get_Cards()`）与非泛型入口 `ModelDb.GetId(Type)` 不在覆盖内，这就是残留缺口的来源。
- 本补丁放在最底层的 `GetId(Type)`，因此对所有调用方生效：现有卡牌、未来新增的生成路径、原版自身的图鉴/奖励/商店/牌组视图，以及第三方模组。它把“按类型解析 id”这一层的成本降到一次字典查找，后续新路径不会在同一个轴上再退化。
- 残留成本只有卡池枚举本身（修复后约占 defect 已覆盖栈的 10%：`CardModel[]` / `Func<CardModel,bool>` / `List<CardModel>`）。那属于“每个角色卡池的可生成集合”，要按根生成池的逐项核对规则单独做快照复用，不是纯函数缓存能覆盖的。

### 7.4 推广时的注意事项

1. 收益不是“所有根一起变快”，而是“含 SPLASH 的根大幅变快、其余基本不变”。评估整批收益时要按牌组是否含 SPLASH 分层，不能用单根外推。
2. 300 根语料每根只注入 2 张无色牌，5.7% 是该语料下的比例；实机取决于玩家实际抓到的无色牌（商店、事件、无色药水），不能直接当成玩家端比例。
3. 节点预算、DOP、Beam 不影响结论方向：补丁去掉的是每次调用的固定成本，但这些数字只在本机 20k/100k 与 DOP16 下实测过；500,000 节点的完整 VeryHigh 未运行（内存与时长风险）。

## 8. 生产默认路径（Coordinator + portfolio）验证

§3 用的是 `--search-mode Evaluate`（单棵树）。生产默认是 `--search-mode Coordinator --use-portfolio`，主搜索 + 药水反事实审计 + 多成员组合；这一节在同一模板上换到该路径重测。

```bash
TMPDIR="$PWD/.local/tmp" timeout 900 dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request "$PWD/.local/learned-selector/data3/requests/sel-defect-elite-01.json" --label <label> \
  --out .local/learned-selector/memory-e2e/<label> \
  --profile VeryHigh --nodes 100000 --dop 16 --budget-ms 600000 \
  --search-mode Coordinator --use-portfolio \
  --enable-no-gc-region --no-gc-region-budget-gigabytes 12 --milestone M2
```

### 8.1 `sel-defect-elite-01` @100k，交错 3+3

| 指标 | 基线中位（范围） | 候选中位（范围） | 变化 |
|---|---:|---:|---:|
| **请求级墙钟 s** | 233.25（206.31~266.09） | **74.81**（73.37~74.95） | **−67.93%** |
| 搜索 s（`ElapsedMilliseconds`） | 73.27（71.04~73.71） | 28.36（27.88~28.87） | −61.29% |
| 全进程分配 GiB（`totalAllocatedBytes`） | 312.80（292.40~335.07） | **98.44**（98.30~98.65） | **−68.53%** |
| 搜索分配 GiB（`WorkerAllocatedBytes`） | 118.42（116.99~119.00） | **30.54**（30.05~31.00） | **−74.21%** |
| 峰值 RSS GiB | 35.47（31.83~35.70） | **20.78**（17.03~22.12） | **−41.43%** |
| GC 暂停 ms | 4393（4352~4891） | 2207（2072~3825） | −49.76% |
| 选中成员展开 | 100000 | 100000 | 0 |
| 选中成员转移 | 567979 | 552377（548803~561774） | −2.75%（§6 的调度产物） |
| score / 战损 | 10000374964 / 31 | 10000374964 / 31 | 全同 |
| planActions 条数 | 36 | 36 | 全同 |
| `cachedContinuations` 条数 | 8 | 8 | 全同 |
| 被跳过成员数 | 4 | 4 | 0 |
| 其中 `MemoryHeadroomInsufficient` | **0** | **0** | 0 |

**公平性门禁**：两侧被跳过成员都是 4 个，原因在模组日志里都是 `skipped=BaselineNotFrontierExhausted`（基线成员 frontier 未穷尽，组合器按原规则跳过其余席位），**没有** `MemoryHeadroomInsufficient` 的成员；成员结构与预算逐项相同，因此这一对比是公平的，没有为了好看的数字调预算或节点数。

### 8.2 生产路径的语义等价（DOP1 @20k）

`--dop 1`、20,000 节点、其余同上：`planActions`(36) 与全部 `cachedContinuations` 文本、score `10000374964`、战损 31、选中展开 20000、成员结构与跳过明细（`BaselineNotFrontierExhausted`×4、`PowerMemberNotTerminal`×1）**逐项相同**；请求墙钟 124.20 → 41.59 s、分配 29.74 → 17.97 GiB。

### 8.3 路线/续用戳一致性抽检（Coordinator 路径，@20k）

| 根 | 类型 | planActions | cachedContinuations | score / 战损 | 成员与跳过结构 |
|---|---|---|---|---|---|
| `sel-defect-boss-12` | 含 SPLASH | 30/30 全同 | 6/6 全同 | 全同 | **不同（见下）** |
| `sel-ironclad-elite-03` | 含 SPLASH | 18/18 全同 | 4/4 全同 | 全同 | **不同（见下）** |
| `sel-silent-boss-15` | 含 SPLASH | 39/39 全同 | 5/5 全同 | 全同 | 全同 |
| `sel-defect-elite-02` | 哨兵（无 SPLASH） | 23/23 全同 | 3/3 全同 | 全同 | 全同 |
| `sel-silent-boss-01` | 哨兵（无 SPLASH） | 55/55 全同 | 9/9 全同 | 全同 | 全同 |

**如实写出差异（不调参凑对比）**：`sel-defect-boss-12` 与 `sel-ironclad-elite-03` 两侧的**成员运行集合不同**——基线因内存余量不足跳过的席位（`skipped=MemoryHeadroomInsufficient`），候选在同样预算下有富余、于是真的跑了（`sel-defect-boss-12` 多跑 Beam 90；`sel-ironclad-elite-03` 多跑 Beam 203 并触及 NodeLimit）。这是**优化的直接后果**（释放出来的内存余量被组合器用掉），不是把两次运行调成同一个形状：两侧的路线、全部续用戳、score、战损仍然逐项相同。除这两根外，其余三根（含两个哨兵）成员与跳过明细完全一致。

因为这两根不可直接比墙钟，它们的墙钟数字只作参考：`defect-boss-12` 18.44 → 11.41 s、`ironclad-elite-03` 26.05 → 16.50 s、`silent-boss-15`（可比）21.13 → 18.35 s（−13.1%，分配 23.10 → 17.78 GiB）。两个哨兵可比且持平：`defect-elite-02` 11.38 → 11.39 s、分配 8.69 → 8.65 GiB；`silent-boss-01` 91.73 → 90.70 s、分配 104.97 → 104.85 GiB、搜索分配 21.30 → 21.30 GiB。

### 8.4 总账口径：缓存没有把成本挪到主线程

Coordinator 路径下 `totalAllocatedBytes − WorkerAllocatedBytes` 与 `wallSeconds − ElapsedMilliseconds` 都是「搜索之外」的残差（含主线程根捕获、建局、成员调度与组合器审计），不是纯根捕获，但**它的增量**正好回答「成本有没有被搬到主线程」：

| 口径 | 基线中位 | 候选中位 | 变化 |
|---|---:|---:|---:|
| 搜索之外分配 GiB | 194.38 | **67.90** | −65.1% |
| 搜索之外墙钟 s | 159.98 | **46.45** | −71.0% |

两侧同宿主、同建局、同组合器配置，唯一变量是模组 DLL。搜索之外的分配与墙钟**都大幅下降**（不是不变、更不是上升）：GetId 记忆化同时削掉了主线程根捕获期间以及协调器/成员调度期间走过的同一条 `ModelDb.GetId` 路径。因此不存在「把枚举从搜索挪进主线程」的形态——那正是方案 B 被放弃的理由，而本轮保留的改动没有这个问题。

### 8.5 生产路径的限制

1. 仍是离线无头宿主；不是可见 Steam 会话，不外推帧时间或玩家可感知卡顿。
2. 只有 `sel-defect-elite-01` 用了 100,000 节点；§8.3 的抽检是 20,000 节点。
3. 生产预设的 500,000 节点与 300 s 软预算没有跑（内存与时长风险）。
4. 组合器在内存富余时会启用更多成员，因此组合成员数不是常量；比较必须同时看成员集合与跳过原因，本轮已如实列出。

## 9. 限制与未验证项

1. 全部数据来自离线无头宿主，不是可见 Steam 会话；按仓库规则不外推 FPS、帧时间或玩家可感知收益。
2. 100,000 节点是 VeryHigh 预设（Beam 135 / 500,000 节点 / 300 s）的 1/5；`sel-silent-boss-01` 用 20,000 节点。
3. GC 暂停在本机是双峰的（同一侧不同次采样在 10 ms 与 2.4 s 之间跳），因此只报范围不作结论；`MaxGcPauseMilliseconds` 在该路径恒为 0，不能读作“无暂停”。
4. `sel-necrobinder-elite-01` 的墙钟差异落在基线自身漂移内，判为**收益未建立**，不判为回退，也不判为提速。
5. 补丁对实机 live 路径同样生效（`ModelDb.GetId` 是全局方法）。本轮只验证了离线搜索；可见会话、Windows 构建与第三方模组共存未验证。
6. 缓存不做淘汰：键是模型 `Type`，数量由内容注册决定（原生 1,660 个模型类型）。没有观察到无界增长的调用方，但这不是硬上限保证。
7. 未验证：incremental search（`--verify-incremental-search`）、Coordinator/portfolio 主路径、完整部署与原生重放。
