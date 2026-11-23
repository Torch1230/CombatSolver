# Testing 源码

公共无人测试框架、原生回放、API worker 和离线宿主共用这里的实现；文件归类沿用同一主程序集与 `UnattendedTestRunner` partial 类型，不改变状态所有权。

| 目录 | 内容 |
| --- | --- |
| `Host/` | 请求协议（`UnattendedTestProtocol`）、请求循环（`ProtocolHost`）、建局（`ScenarioBuilder`）、执行（`Executor`）、断言（`Assertions`）、结果写入（`Writer`）、开发监控发布器及离线入口（`Offline`） |
| `Support/` | 共享原生 fixture、出牌与卡牌选择器辅助（`UnattendedTestRunner.Fixtures.cs`、`NativeCardPlay.cs`、`NativeTestCardSelectors.cs`） |
| `Replay/` | 检查点导入、原生事件回放、录制状态恢复与回放断言 |
| `Contracts/Combat/` | 生成场景检查（`GeneratedScenarioChecks`）与问题报告上传合同（`BugReportUpload`） |
| `Contracts/Runtime/` | `CombatRootSnapshot` + `HotRootBundle` 打包/解包与 `HotContinuationStamp` 往返合同、原生选牌面（`NativeChoiceSurfaces`）、进程诊断（`ProcessDiagnostics`）及战绩 worker 失败合同 |
| `Contracts/UI/` | 路线行复用（`RouteRowReuse`）与召唤物显示名（`DisplayNameSummon`）合同 |

## 常用验收入口

- **稳定根与 HotKernel 往返合同**：使用 `-VerifyCombatRootSnapshot -StopAfterCombatRootSnapshotAssertion`（Bash：`--verify-combat-root-snapshot --stop-after-combat-root-snapshot-assertion`）验证主线程根捕获、live 修改隔离及 `HotRootBundle` Pack/Unpack + `HotContinuationStamp` 往返一致性。
- **原版自动部署验收**：使用默认原版遭遇（如 `IRONCLAD` / `FUZZY_WURM_CRAWLER_WEAK`）配合 `-VerifyCombatRootSnapshot -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit`。
- **独立工具与离线核对**：布局、自洽、DOP 确定性与微基准由 `tools/search/OfflineSearchHarness`（`--check-hot-layout` / `--verify-hot-parity`）承载；无游戏依赖的 6 项独立检查由 `python -B tools/testing/run-independent-checks.py` 运行。

测试选择与平台命令见 [无人测试](../../docs/HEADLESS_TESTING.md)，当前矩阵入口见 [测试矩阵](../../docs/TEST_MATRIX.md)。

一次性调查放 `.local/tool-tasks/<任务>/`，验证时显式接入，结束清理代码、路由、参数、输入和产物。普通构建显式排除 `.local` 源码。新增正式文件按上表收纳；根目录只保留本入口。
