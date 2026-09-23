# 回合开始入口：0.43.3 等价对照

基线为 `6922828d`，入口实现包含 `0584c4a2` 的监听位修正。语料为 EQ 10、FULL 40、GA 10；`inputs/` 保留原始规格和使用仓库相对路径的请求。游戏 API 为 0.111.0。

本轮一次运行完成 60 对、120 次搜索：EQ 1069、FULL 4212、GA 1060，合计 **6341 个确定性字段一致**。全部根 Passed，无时间截断、缺项或差异根；没有中断或补跑。结果见 `metadata.json`、`results-summary.json`。

两侧固定 High、beam 90、nodes 250000、分支 48/28/36、Coordinator、Smart、DOP 1、600 秒软预算。每侧一个进程；运行前拒绝已有游戏/宿主，期间不构建，结束后核对 DLL 哈希。时间截断或任一根失败会中止验收，不计为相同。

先分别构建基线 DLL、改动 DLL 和改动侧 `tools/OfflineSearchHarness`，再在本仓库执行：

```sh
python3 coverage/equivalence/before-side-turn-start-0433/run.py \
  --base-dll <基线DLL绝对路径> --new-dll <改动DLL绝对路径> \
  --workspace <新批次绝对路径>
python3 coverage/equivalence/before-side-turn-start-0433/summarize.py <批次路径>
```

运行器调用仓库的 `run_plan.py`，最后调用 `compare_results.py`，前缀为 `base`、`new`，均不带末尾短横。批次目录保存完整双侧 result、route、search-policy 与比较明细。`metadata.json`、`results-summary.json` 保存各组字段数及原始产物 SHA256；摘要器验证完整 60 对和零差异后才写入。比较范围沿用仓库比较器：非时序 solverMetrics、路线动作、根 continuation、生成目录指纹及两侧都有的续用戳。

`native-order.json` 记录原版阶段与监听顺序依据。`validation.json` 分开记录生产合同、结构门禁和 CoverageCatalog 的原始失败/隔离输入核验，不将旧 0.42.0 证据当成本轮结果。
