# 固定工作量性能对照

可选 Linux 研究入口，使用现有 Bash 无人测试启动器和 `/proc`，每次搜索占用一个独占的新进程；不改变生产搜索政策。Windows 继续使用维护中的 [PowerShell 入口](../run-unattended-test.ps1)，本工具没有跨平台峰值采集实现。

先为基线与候选分别准备可删除的独立 checkout，保持相同游戏/依赖、测试支持和 `local.props`。不要在性能搜索期间构建、采样或运行其他测试。

```bash
python3 tools/PerformanceBenchmarks/build_details.py --checkout /path/to/disposable-checkout --output /path/to/new-build
python3 tools/PerformanceBenchmarks/run.py --build /path/to/new-build --input /path/to/resolved-search.json --settings /path/to/settings.json --output /path/to/new-evidence --game-root /path/to/isolated-game --instance owned-performance --timeout 120
python3 tools/PerformanceBenchmarks/compare.py /path/to/baseline-evidence /path/to/candidate-evidence --output /path/to/comparison.json
python3 -m unittest discover -s tools/PerformanceBenchmarks -p 'test_*.py'
```

构建器临时扩展 Testing Writer，在求解指标冻结后导出完整动作、路线、政策；`finally` 恢复源文件，SIGKILL/掉电不保证清理，因此只接受独立 checkout。`CopyModOnBuild=false`，不安装测试产物。

生成输入必须显式为 `mode: "Search"`。内环默认每请求120秒；完整重场景对照必须在开跑前独立确定预算和顺序，不能把超时的同一内环直接延长来改成通过。测量极高正式请求时使用 `fixedSearchBudget:false` 和真实设置；不要用降低节点/分支/药水审计后的速度冒充同工作量提速。

运行器记录：原输入/设置、精确命令、构建DLL标识、GC环境与CPU亲和性、标准生成证据、全部结果及当时可读取的该进程诊断日志。日志可能尚有未刷出的尾部，最终指标以原子结果文件与 `details.json` 为准。内存来自同一结果PID的Linux内核 `VmHWM`，每250毫秒读取，包含建局；`VmSwap` 一并保存。搜索耗时不含建局，`capturedAtElapsedMilliseconds` 另保留请求内捕获时点；两者均不等于玩家可见点击延迟。结果捕获后保持进程直到最后一次峰值读取，随后交给原生启动器停止自己拥有的实例。不要手工复用其他任务的实例名。

生成器的Search执行入口明确发起Manual请求，该入口绕过路线缓存；准备阶段的AutoTurnStart可能只展示磁盘中的旧缓存结果。因此不能用日志中第一条RESULT代替本次请求的 `details.json` / `result.json`，也不能把缓存携带的历史耗时当作本进程发生的搜索。

比较器要求相同的原输入、解析配置、配装、开局、设置、政策、全部动作及路线。文本结果和结构化求解指标中的未知字段默认参与比较；只排除明确列出的运行时与调度计数，并验证 `forks - round_prefix_captures - card_prefix_fallbacks == transitions`。失败、缺失峰值、PID不一致及TimeLimit不能得到相同工作量提速结论。显式排除的运行时信息仍完整保留在测量中，方便核对GC建立、意外退出及恢复。

按预先固定的ABBA或其他交错顺序保留全部样本。`changePercent` 是单对测量值，不能独自证明稳定收益；报告须保留各次时间/峰值、跨轮漂移、失败和质量差异。通过无人请求只代表断言完成，不代表胜利或完整自动部署。Linux无头数据不外推Windows与可见帧时间。
