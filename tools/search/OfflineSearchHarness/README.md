# 离线搜索宿主

不启动 Godot、在普通 .NET 9 进程里运行 CombatSolver `HotKernel` + `HotSearchCoordinator` 搜索的离线宿主。

完整背景与指标口径见 [`docs/OFFLINE_SEARCH_HARNESS.md`](../../../docs/OFFLINE_SEARCH_HARNESS.md)。

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release

# Gate 0：HotKernel 非托管布局、结构体尺寸与打包合同检查（无需启动游戏）
dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --check-hot-layout

# Gate A/B/C：19/19 根捕获 + Pack/Unpack 往返 + 多回合自洽 + DOP=1/8 确定性 + 微基准
dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --verify-hot-parity

# 单根求解（Evaluate 或 Coordinator）
dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
    --request <请求.json> --label R1 --out <产物目录> --profile VeryHigh

# 批量计划与双版本质量对比
python3 tools/search/OfflineSearchHarness/run_plan.py --plan <plan.json> --workspace <dir> --workers 3
python3 tools/search/OfflineSearchHarness/compare_results.py \
    --left <A>/runs --left-prefix A --right <B>/runs --right-prefix B --out cmp.json
```

## 文件说明

| 文件 | 作用 |
|---|---|
| `Program.cs` | 命令行解析、分步时间线、单根/批量路由与产物落盘 |
| `AssemblyBootstrap.cs` | 运行期解析 `sts2` / `RitsuLib` / `CombatSolver` 程序集 |
| `GameBootstrap.cs` | 游戏静态状态初始化与全部 Godot 绕过（类头有清单） |
| `ModRuntime.cs` | 模组初始化、稳定根捕获并直接调用 `HotSearchCoordinator`（`Solve` / `SolveSingleBeam` / `CaptureQuality`） |
| `HotKernelChecks.cs` | `--check-hot-layout` 内存布局门禁与 `--verify-hot-parity` Gate A/B/C 核对 |
| `QualityComparison.cs` | 基于 `HotRouteQuality.IsBetter` 的批量结果质量比较 |
| `AncillaryFailureChecks.cs` | 辅助失败边界与缓存合同检查 |
| `GeneratedScenarioSetup.cs` | 生成场景开局（走模组注入入口） |
| `OfflineCombat.cs` / `MainLoopContext.cs` | 建战斗、推进到玩家首回合与消息循环 |
| `OfflineLocalization.cs` / `MemorySaveStore.cs` | 空表本地化与内存存档层 |
| `MemorySampler.cs` | 峰值托管堆与工作集采样 |
| `run_plan.py` / `compare_results.py` | 批量执行与逐字段比较脚本 |

`--request` 接受固定装备/初始战斗状态夹具与生成场景请求；不执行 fixture 的 expected 断言。搜索预算、预设与药水政策以 CLI 为准，旧计划 JSON 中的 `--use-portfolio`、`--enable-no-gc-region` 等参数作为兼容无操作选项保留。特殊原生交互合同仍使用无头游戏测试（`tools/testing/run-unattended-test.{ps1,sh}`）。
