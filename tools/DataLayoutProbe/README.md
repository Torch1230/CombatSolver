# 局部数据布局探针

这是研究工具，不改变生产字段或后端准入。需要 .NET 9 SDK；不启动游戏，也不依赖游戏 DLL。

```bash
DOTNET_TieredCompilation=0 dotnet run --project tools/DataLayoutProbe/DataLayoutProbe.csproj -c Release
```

PowerShell 对应先设置 `$env:DOTNET_TieredCompilation = '0'`，再执行同一条 `dotnet run`。输出 JSON 包含运行时、架构、实际托管值大小和五个分配块；每种形状预热 1,024 次，每块 10,000 次，结果对象写入静态引用以保留分配。它不计时，不报告游戏速度或峰值内存。字典 Entry 大小通过当前运行时的私有数组类型读取，仅用于诊断，运行时内部结构变化时应显式修正探针。

生产 `ForkableCollections`、`CreatureVitals`、`CardInstanceValue` 直接链接编译。其他 Counter / Wrapper / 数组是标明用途的合成布局；`MergedList` 是仅实现本次比较所需操作的研究候选，不能当作生产完整替代。合同检查父/兄弟隔离、COW 前捕获枚举器、缺失删除、独占修改的枚举失效，并验证真实生命值范围和 64 位卡牌编码不能随意缩窄。

## 状态仓库小表候选

用 Python 3 将当前生产源码转换成独立候选和可审阅 patch；脚本不修改生产文件，源码形状不匹配时停止：

```bash
python3 tools/DataLayoutProbe/make_state_store_candidate.py .local/data-layout-probe/PredictionStateStore.ListCounts.cs
DOTNET_TieredCompilation=0 dotnet run --project tools/PredictionStateStoreChecks/PredictionStateStoreChecks.csproj -c Release -- 10000
DOTNET_TieredCompilation=0 dotnet run --project tools/PredictionStateStoreChecks/PredictionStateStoreChecks.csproj -c Release -p:StateStoreSource="$(pwd)/.local/data-layout-probe/PredictionStateStore.ListCounts.cs" -p:DefineConstants=CHECK_ENTRY_COUNTS -- 10000
```

PowerShell 中将候选绝对路径先保存为 `$candidatePath = (Resolve-Path '.local/data-layout-probe/PredictionStateStore.ListCounts.cs').Path`，传入 `"-p:StateStoreSource=$candidatePath"`；保持 `-p:DefineConstants=CHECK_ENTRY_COUNTS`，确保源码覆盖模式仍执行类型计数合同。两次运行顺序执行，同一项目的候选与基线不并行构建。

该候选只将 `_countByType` 改为 `List<KeyValuePair<Type, int>>`，保留原主表、状态对象、深复制、模型 alias 与重映射。复用已有完整 `PredictionStateStoreChecks`，不另建游戏测试框架。辅助表查找由哈希变为线性；必须结合真实不同类型数量、`HasEntries` 调用负载和整场分配再决定生产接入。

结果口径及已运行数据见[局部布局研究](../../docs/performance/simulation-data-layout-20260912.md)和[JSON](../../docs/performance/simulation-data-layout-20260912.json)。存储合同使用替身模型/重映射上下文，不能替代完整模拟器、实际游戏差分或正常 NoGC 性能验证。
