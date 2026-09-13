# ForkableList 存储合并合同

**研究候选已撤回，生产列表保持上游实现。** 直接编译生产 `ForkableCollections.cs`，不依赖游戏或 Compact 后端。

```bash
DOTNET_TieredCompilation=0 dotnet run --project tools/ForkableListLayoutChecks -c Release
```

PowerShell 先设置 `$env:DOTNET_TieredCompilation = '0'`，再运行相同 dotnet 命令。

覆盖全部列表入口、10,000 次随机操作与 64 个独立分支、共享前枚举器、独占修改的枚举失效、非泛型枚举，以及 8 个独占 worker 的首次写入复制。随机序列使用固定种子，与独立 `List<int>` 深复制模型对照。

每种形状预热 1,024 次，测 5 块、每块 10,000 次；对象写入静态引用，使用线程累计分配计数。输出的 B/操作是局部分配，不是存活堆、RSS 或完整搜索性能。

用同一合同对照上游源码：

```bash
mkdir -p .local/list-layout-baseline
git show eff8cf4:src/Search/ForkableCollections.cs > .local/list-layout-baseline/ForkableCollections.cs
DOTNET_TieredCompilation=0 dotnet run --project tools/ForkableListLayoutChecks -c Release -p:CollectionsSource="$PWD/.local/list-layout-baseline/ForkableCollections.cs"
```

生成候选并用同一合同测量；生成器不修改生产文件：

```bash
python3 tools/ForkableListLayoutChecks/make_candidate.py .local/list-layout-candidate/ForkableCollections.cs
DOTNET_TieredCompilation=0 dotnet run --project tools/ForkableListLayoutChecks -c Release -p:CollectionsSource="$PWD/.local/list-layout-candidate/ForkableCollections.cs"
```

报告与完整搜索口径见 [精简分支报告](../../docs/performance/surgical-fixes-20260912.md)。
