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

## 7. 限制与未验证项

1. 全部数据来自离线无头宿主，不是可见 Steam 会话；按仓库规则不外推 FPS、帧时间或玩家可感知收益。
2. 100,000 节点是 VeryHigh 预设（Beam 135 / 500,000 节点 / 300 s）的 1/5；`sel-silent-boss-01` 用 20,000 节点。
3. GC 暂停在本机是双峰的（同一侧不同次采样在 10 ms 与 2.4 s 之间跳），因此只报范围不作结论；`MaxGcPauseMilliseconds` 在该路径恒为 0，不能读作“无暂停”。
4. `sel-necrobinder-elite-01` 的墙钟差异落在基线自身漂移内，判为**收益未建立**，不判为回退，也不判为提速。
5. 补丁对实机 live 路径同样生效（`ModelDb.GetId` 是全局方法）。本轮只验证了离线搜索；可见会话、Windows 构建与第三方模组共存未验证。
6. 缓存不做淘汰：键是模型 `Type`，数量由内容注册决定（原生 1,660 个模型类型）。没有观察到无界增长的调用方，但这不是硬上限保证。
7. 未验证：incremental search（`--verify-incremental-search`）、Coordinator/portfolio 主路径、完整部署与原生重放。
