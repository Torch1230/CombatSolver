# 离线搜索宿主

不启动 Godot、在普通 .NET 9 进程里跑 CombatSolver 搜索的宿主。

完整说明（构建、单根与批量用法、plan 字段、产物、`Evaluate` 与 `Coordinator` 的口径差别、
Godot 绕过表、已知限制、验证证据）见 [`docs/OFFLINE_SEARCH_HARNESS.md`](../../docs/OFFLINE_SEARCH_HARNESS.md)。

```
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release

# 单根
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
    --request <请求.json> --label R1 --out <产物目录> --profile VeryHigh

# 批量
python3 tools/OfflineSearchHarness/run_plan.py --plan <plan.json> --workspace <dir> --workers 3

# 两份结果逐字段比
python3 tools/OfflineSearchHarness/compare_results.py \
    --left <A>/runs --left-prefix A --right <B>/runs --right-prefix B --out cmp.json
```

文件：

| 文件 | 作用 |
|---|---|
| `Program.cs` | 命令行、分步时间线、产物落盘 |
| `AssemblyBootstrap.cs` | 运行期解析 `sts2` / `RitsuLib` / `CombatSolver` |
| `GameBootstrap.cs` | 游戏静态状态初始化与**全部** Godot 绕过（类头有表） |
| `ModRuntime.cs` | 模组侧初始化、离线会话、搜索正确性补丁、一次求解 |
| `GeneratedScenarioSetup.cs` | 生成场景开局（走模组自己的注入方法） |
| `OfflineCombat.cs` / `MainLoopContext.cs` | 建战斗、推进到玩家第一回合、消息循环 |
| `OfflineLocalization.cs` / `MemorySaveStore.cs` | 空表本地化、内存存档层 |
| `MemorySampler.cs` | 峰值托管堆与工作集采样 |
| `run_plan.py` / `compare_results.py` | 批量运行、逐字段比较 |
