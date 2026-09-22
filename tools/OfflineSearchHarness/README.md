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

`--request` 也接受本仓库的固定装备/初始战斗状态夹具，不再强制 generatedScenarioPath；仍不执行 fixture 的 expected 断言。搜索预算、预设与药水政策以宿主 CLI 为准，例如成长循环须显式传 `--potion-policy RequireAtLeastOne`。恢复快照、追加怪物、自定义规则不支持并明确拒绝；特殊 ScenarioId 的原生合同请使用无人游戏测试。`--stop-at-zero-loss` 启用生产零战损达标停止；`--verify-incremental` 对小根逐步完整回放，不用于性能测量。见[循环对照](../../docs/performance/loop-optimization-20260921.md)。

循环固定边界集使用 `run_loop_boundaries.py`：串行双 DLL A/B，120 秒进程上限，拒绝覆盖已有结果，显式检查 suite 的质量／结构条件并比较完整路线。仅支持 Evaluate，命令固定该模式并拒绝 Coordinator 配置；4096 检查属于单 solver。计数优先从新 `TurnLayerTimeBudgetStops` / `TurnLayerNodeBudgetStops` 读取，旧 DLL 从完整诊断日志回退解析，二者与总数及日志相互核对。局部／全局时间边界或证据不足标为 Inconclusive、退出 2，原始差异／断言观察仍保留，不能用于有效性能均值；可比较差异、断言或宿主失败退出 1，等价退出 0。不自动把不同路线判作质量退化；见[19 根边界与预算审计](../../docs/performance/loop-boundaries-20260921.md)。
