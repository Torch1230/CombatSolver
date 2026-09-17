# 搜索分配的实测归因（2026-09-17）

本文件记录一次**有界分配归因**的真实结果，以及由此得出的一条**否定结论**（某处看起来像浪费的深拷贝其实不可去掉）。此前仓库只有 `docs/performance/gc-issue36-code-audit.md` 的**候选清单**，没有按调用栈的实测归因。

## 方法与复现命令

用仓库自带设施，不引入新工具：

```bash
# 1) 构建
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release
dotnet build tools/GcTraceAnalysis/GcTraceAnalysis.csproj -c Release

# 2) 跑一个有界搜索并录制分配事件（窗口 100 秒）
OFFLINE_HARNESS_COMBATSOLVER_DLL=.godot/mono/temp/bin/Release/CombatSolver.dll \
  dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request <crab request.json> --label ALLOC --out .local/alloc-profile/run --milestone M2 \
  --profile VeryHigh --beam 135 --dop 1 --nodes 8000 &

dotnet-trace collect -p <harness pid> \
  --providers 'Microsoft-Windows-DotNETRuntime:0x0000000000000001:5' \
  --duration 00:00:01:40 -o .local/alloc-profile/alloc.nettrace

# 3) 解析每个 GCAllocationTick 的调用栈
dotnet tools/GcTraceAnalysis/bin/Release/net9.0/GcTraceAnalysis.dll \
  --input .local/alloc-profile/alloc.nettrace \
  --output .local/alloc-profile/allocation-analysis.json --top 40
```

注意：`dotnet-trace --duration` 的格式是 `dd:hh:mm:ss`（不是秒数）；`--show-child-io` 是无值开关，给它传值会解析失败并打印帮助。trace 与 etlx 写磁盘（本机 `/tmp`、`/dev/shm` 是 tmpfs）。

## 实测结果

crab 根、`VeryHigh`、beam 135、`--dop 1`、8000 节点、窗口约 11.3 秒：

- 全部样本 35,414 次分配 tick，估算 **3.83 GB**；其中**已确认搜索 35,257 次 / 3.81 GB（99.6%）**；无栈样本 0，丢事件 0。
- 分类：`Other` 2.48 GB、`Fork` 0.68 GB、`SnapshotStateEvaluation` 0.57 GB、`History` 76 MB、`ProjectedShuffle` 3.8 MB。
- 参考吞吐：同配置 6000 节点 16.09 秒；8000 节点约 11.3 秒窗口内跑完（本次节点数由 `--nodes` 固定）。

搜索内分配占比最高的类型（估算字节）：

| 类型 | 估算字节 |
|---|---|
| `CombatSolver.SimulatedCombatState` | 104.2 MB |
| `System.Int32[]` | 103.7 MB |
| `Dictionary<String,DynamicVar>` | 101.0 MB |
| `CombatSolver.Engine.Common.PredictedCard` | 88.9 MB |
| `PredictedCard[]` | 78.2 MB |
| `IEnumerableSelectIterator<DynamicVar,DynamicVar>` | 67.1 MB |
| `Entry<Creature,int>[]` | 65.1 MB |
| `SZGenericArrayEnumerator<CardPile>` | 63.6 MB |
| `SimulationSnapshot` | 59.9 MB |
| `Func<CardPile,bool>` | 59.0 MB |
| `SearchNode[]` | 58.0 MB |
| `Concat2Iterator<CardPile>` | 51.6 MB |
| `Enumerator<String,DynamicVar>` | 44.4 MB |
| `CombatSolver.SearchNode` | 34.9 MB |

## 结论一（否定结论）：Power 深拷贝不可去

占比最高的**单条调用链**出现在 `topSearchStacks` 的第 1/2/3/5/7/9/11 名，全是同一条路径：

```
SimulatedCombatState.ForkPower
  → PredictionUtils.CloneModelForSimulation
    → PowerModel.DeepCloneFields
      → DynamicVarSet.Clone
        → Enumerable.Select + new DynamicVarSet(...)
```

类型层面合计约 `Dictionary<String,DynamicVar>` 101.0 MB + `Entry<String,DynamicVar>[]` 42.7 MB + `Select` 迭代器 67.1 MB + `Enumerator<String,DynamicVar>` 44.4 MB ≈ **255 MB（占搜索分配约 6.7%）**。

**看起来**像纯浪费：mod 对 `card.DynamicVars` 只读不写，`ForkPower` 还显式覆写了克隆体的 `_owner`/`_applier`/`_target`/`_amount`。**但反编译核对后确认它不可去掉**：

```csharp
// MegaCrit.Sts2.Core.Models.PowerModel
protected override void DeepCloneFields()
{
    base.DeepCloneFields();
    _dynamicVars = DynamicVars.Clone(this);   // 昂贵点
    _internalData = InitInternalData();
}

// MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet
public DynamicVarSet Clone(AbstractModel model)
{
    DynamicVarSet set = new DynamicVarSet(Values.Select(v => v.Clone()));
    set.InitializeWithOwner(model);           // 逐项 SetOwner(model)
    return set;
}

// DynamicVar
public DynamicVar Clone() { var v = (DynamicVar)MemberwiseClone(); v.ResetToBase(); return v; }
```

三条独立证据说明这个克隆是**承载语义的**，不是冗余拷贝：

1. `InitializeWithOwner` 把每个 `DynamicVar` 的 `_owner` 重新绑定到新模型。共享会让变量的反向引用指回**父**Power。
2. `ResetToBase()` 把 `EnchantedValue`/`PreviewValue` 复位到 `BaseValue`，即克隆**故意丢弃**源对象的预览/升级态。
3. `BaseValue`、`EnchantedValue`、`PreviewValue` **都有 setter 且在运行时被写**（`UpgradeValueBy` 改 `BaseValue`，`BaseValue` 的 setter 会触发 `ResetToBase`，`UpdateDynamicVarPreview` 改 `PreviewValue`。

**因此：跨分支共享 `DynamicVarSet` 会让可变状态在搜索分支之间串味，属于会改变决策的改动。** 本仓库对该区域有 `PowerCloneConcurrency`、`ModelCloneConcurrency`、`ForkBoundaries` 三组守卫测试，也印证了它的敏感度。

反编译产物位置（只读引用）：`CombatSolver-open-source/.local/decompiled-tmp/sts2/`。

### 若要继续推进（提案，未实现）

唯一在**原理上**仍可能安全的减法是**惰性克隆 / 写时复制**：分支未修改的 Power 继续共享父实例，直到某处真正写入才克隆。它在构造上保持语义（任何写都不会碰到共享实例），但要求**穷尽拦截 PowerModel 的全部可变面**（amount、dynamic vars、internal data，外加游戏 Hook 侧的写入路径）。漏掉任一路径就会产生静默的跨分支别名 → 错误决策。因此它是**调研提案，不是可直接落地的改动**，必须先枚举并证明全部写入点。

## 结论二（可做）：热路径上的 LINQ 分配

上表里 `Func<CardPile,bool>` 59.0 MB、`SZGenericArrayEnumerator<CardPile>` 63.6 MB、`Concat2Iterator<CardPile>` 51.6 MB、`Enumerator<String,DynamicVar>` 44.4 MB 是**委托 + 闭包 + 装箱枚举器 + LINQ 迭代器**的形态：为了算出一个整数而分配一串临时对象。

定位在 `src/Prediction/CalculatedVarSpecRegistry.cs`（183 行、22 处 LINQ）。两条关键事实：

- `TryCalculate` 第 45 行用 `card.Preview.DynamicVars.FirstOrDefault(pair => ReferenceEquals(pair.Value, calculatedVar)).Key` 探测字典：每次调用分配一个委托 + 一个**装箱的字典枚举器**。而 `Dictionary` 的 `foreach` 用结构体枚举器，**零分配**。
- `SimPlayerCombatState.AllCards` 返回**结构体** `AllCardsEnumerable`（带结构体枚举器，`foreach` 零分配），但传给 LINQ 时会被**装箱**，`Enumerable.Count` 内部再取一次接口枚举器。

仓库已对同类问题给出过既有做法：`src/Runtime/SimulationCardPileLookupPatch.cs:11–18` 的注释写明 vanilla `CardModel.Pile` getter 会构造两个 Concat 迭代器、一个谓词闭包和接口枚举器，并给出直接循环的快路径。本次按同一思路处理 `CalculatedVarSpecRegistry`（见后续实施记录）。

## 未验证 / 不宣称

- 未做 live/retained 堆的**结构分摊**：本文件是**分配流量**（allocation flow），不是存活集（retained）。表中占比只说明"这段时间内分配了多少"，不能推成"堆里谁占得多"。仓库既有审计也强调过这一区别。
- 只测了一个根（crab）、一个并行度（dop 1）。其它遭遇/牌组/并行度下的占比未测。
- 采样窗口内的 `estimatedBytes` 由 allocation tick 的估算量累计而来，是**估算**而非精确计数。
- 未测可见帧时间、Windows 或 Steam 环境。
- `--dop 8` 下本仓库已知 `roundReplayPrefixCaptures` / `executionChoiceReuses` 两个调度计数器会自然不同（基线自比亦不同），因此等价性判定只用 `--dop 1`。
