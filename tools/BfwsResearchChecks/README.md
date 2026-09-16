# 有界新颖性搜索检查

```bash
dotnet run --project tools/BfwsResearchChecks/BfwsResearchChecks.csproj -c Release
```

无需游戏依赖，直接编译生产元组表、祖先配额、双端队列和预算代码。字符串表只作为测试参考实现，不进入 Mod DLL。检查覆盖新颖性优先顺序、跨分区新颖性、12,000 个混合事实状态及容量边界、同一新颖祖先下共享熟悉后代配额、稳定队列淘汰与总预算扣减。

完整请求使用 [固定输入](../../coverage/novelty-search) 和 [预先冻结的选择方案](../../docs/strategy/bounded-novelty-search-plan-20260916.json)。Linux 可通过可选包装器测量一个独立进程：

```bash
python3 tools/run-novelty-benchmark.py \
  --build /absolute/path/to/release-build \
  --input coverage/novelty-search/dev-00-ironclad-elite.json \
  --settings coverage/novelty-search/settings.json \
  --options coverage/novelty-search/portfolio.json \
  --output /absolute/path/to/new-evidence-directory \
  --game-root /absolute/path/to/game \
  --ritsu-workshop-root /absolute/path/to/ritsu \
  --instance novelty-benchmark --dop 1 --memory-mib 16384 --smart
```

换成 `beam.json` 测量关闭开关的基线。包装器在 `finally` 中停止自己拥有的实例，记录 Linux `/proc` 的进程峰值与原生启动命令。必须使用全新的输出目录；不要让其他构建、测试或基准同时占用 CPU。`--native` 沿首次结果完整部署，`--control-checks` 单独验证取消与接管，`--gc-scope` 使用 Runtime 的真实 GC 生命周期，`--expect-reclaim` 额外要求发生回收。后两者的内存预算来自 settings；正常性能对照不打开这些诊断标志。

Windows 不运行 `/proc` 包装器。两端原生 `run-unattended-test.ps1/.sh` 都支持共享场景 `GENERATED-NOVELTY-SEARCH`：显式提供 GeneratedScenarioPath、EvidenceDirectory、ScenarioId 及相同设置覆盖；启动前在证据目录写入 `research-options.json`（内容为 `beam.json` 或 `portfolio.json`），完整协调器模式另建 `smart.flag`。其他标志对应 `native.flag`、`control-checks.flag`、`gc-scope.flag`、`expect-reclaim.flag`。保留 `research-*.json` 名称以兼容本轮测量产物，接口只接受 `scheduler: beam/bfws/portfolio`，未知字段显式拒绝。直接 `bfws` 模式不得与 `smart.flag` 混用。

时间受编译预热与预算截断影响，同进程多变体只用于筛选。性能比较使用每份样本独立进程，交错顺序，核对 input/settings/resolved/loadout/opening 五份 JSON；使用 `total_*` 请求指标，不能把选中成员的 expanded 当成整个组合工作量。进程峰值包含初始化；无头数据不代表可见 Steam 帧时间。
