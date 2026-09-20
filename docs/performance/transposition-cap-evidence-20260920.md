# 转置表上限与标签分布

基线 `8be1410`。默认合并条目上限仍为 1,000,000；触顶后的新状态仍放行但不入表，未改准入、支配、预算、回收和默认值。

## 诊断口径

`TRANSPOSITION_CAP` 每个 solver 通道结束时输出一次。新增表项后仅更新条目峰值和首次达到上限的已展开数；退出时扫描两张表的 frontier 得到总标签与分布。计数属于 SearchRunContext，跨该通道的可重建缓存回收保留峰值与首次触顶位置；不进入 SolverResult、状态键或排序。

`LimitBypasses` 是准入／展开阶段触顶后放行但未写表的次数，重复键可重复计数。标签分布为该通道结束时的驻留值，不是整场标签峰值。Coordinator 顺序通道不能相加成同时驻留峰值。上限统计条目，不是标签数或内存字节。

## 固定预算对照

EQ 10、FULL 40、GA 10，High 90／50000，分支 48／28／36，Coordinator、Smart、DOP 1，固定时间边界 600000 ms。GA 在 EQ 的 colorlessCards.ids 中追加 GOLD_AXE，没有混用 runCards。

60 根均有效且未碰时间边界，对 main 的 5,670 个确定性字段零差异（EQ 992、FULL 3,743、GA 935），额外预算／剪枝／时间边界及 60 份完整 quality 快照一致。High 组最大条目占用 358,403（35.84%）。这组证明诊断未改变这些固定预算根的确定性结果，不用于声称普遍零开销。

## VeryHigh 生产预算观察

从 main 本轮 High 语料选 FULL-SILENT-ELITE-03（216.6 s）、FULL-REGENT-ELITE-00（118.1 s）和 FULL-REGENT-BOSS-00（58.8 s）；前两者是该组最慢的 FULL 精英根，后者补 Boss 场景。

生产档位为 135／500000，分支 72／42／54，300000 ms；Coordinator、Smart、DOP 1。没有覆盖 beam、节点或分支数；启用生产预算流程，允许预算内的无胜利升级。上限参数省略即生产默认。重型根逐个运行，每臂一次，No-GC 关闭；正常时间截止保留为观察结果，不与固定预算逐位对照混用。

宿主的 searchPolicy.Profile 保留基础设置 600000 ms；实际 Coordinator 在 SolveCore 开始时应用 BudgetOverrideMilliseconds。本轮 --budget-ms 为 300000，与生产 VeryHigh 的 300 秒相同。结果数据同时保留基础政策、请求预算和 effectiveBudgetMilliseconds，避免把基础设置当作本轮有效预算。

三个重型根均未触顶，最大占用 832,793 / 1,000,000（83.28%）。没有触顶根，未运行放大上限臂；本轮不提供默认触顶后的质量结论。

| 根 / 上限 | 胜利 | 战损 | 用药 | 总展开 | 墙钟 s | 采样托管堆 MiB | 条目峰值 | 触顶后放行次数 | 协调器 deadline |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| FULL-REGENT-BOSS-00 / 1,000,000 | 是 | 41 | 0 | 70,778 | 73.085 | 247.10 | 83,566 | 0 | 否 |
| FULL-REGENT-ELITE-00 / 1,000,000 | 是 | 50 | 0 | 157,325 | 173.015 | 509.42 | 198,459 | 0 | 否 |
| FULL-SILENT-ELITE-03 / 1,000,000 | 是 | 48 | 0 | 201,046 | 300.185 | 621.78 | 832,793 | 0 | 是 |

墙钟是宿主记录的本次求解耗时；托管堆为 100 ms 采样峰值。没有重复测量，不外推实机帧率或其他根的质量。

FULL-SILENT-ELITE-03 的用药梯度日志为 `SMART_POTION_GRADIENT result stop=deadline`，所选路线来自更早完成的胜利通道。宿主原 timeBoundary 标记没有识别这条协调器消息，原值 false 保留在数据里；表中的 deadline 直接取该日志，不能据 false 声称本根未用尽时间。三根的 SEARCH_SESSION 日志均确认总预算 300000 ms。

| 根 / 上限 | 通道序号 | 首次触顶的已展开数 | 结束时条目 | 结束时标签 | 每条目标签数 → 条目数 |
|---|---:|---:|---:|---:|---|
| FULL-REGENT-BOSS-00 / 1,000,000 | 1 | 未触顶 | 83,499 | 83,519 | 1 → 83,487、2 → 4、3 → 8 |
| FULL-REGENT-BOSS-00 / 1,000,000 | 2 | 未触顶 | 60,007 | 60,008 | 1 → 60,006、2 → 1 |
| FULL-REGENT-BOSS-00 / 1,000,000 | 3 | 未触顶 | 75,051 | 75,088 | 1 → 75,014、2 → 37 |
| FULL-REGENT-BOSS-00 / 1,000,000 | 4 | 未触顶 | 83,566 | 83,570 | 1 → 83,562、2 → 4 |
| FULL-REGENT-BOSS-00 / 1,000,000 | 5 | 未触顶 | 78,388 | 78,388 | 1 → 78,388 |
| FULL-REGENT-BOSS-00 / 1,000,000 | 6 | 未触顶 | 61,260 | 61,262 | 1 → 61,258、2 → 2 |
| FULL-REGENT-ELITE-00 / 1,000,000 | 1 | 未触顶 | 85,012 | 85,046 | 1 → 84,978、2 → 34 |
| FULL-REGENT-ELITE-00 / 1,000,000 | 2 | 未触顶 | 117,267 | 117,277 | 1 → 117,257、2 → 10 |
| FULL-REGENT-ELITE-00 / 1,000,000 | 3 | 未触顶 | 81,090 | 81,095 | 1 → 81,085、2 → 5 |
| FULL-REGENT-ELITE-00 / 1,000,000 | 4 | 未触顶 | 129,918 | 129,961 | 1 → 129,878、2 → 37、3 → 3 |
| FULL-REGENT-ELITE-00 / 1,000,000 | 5 | 未触顶 | 110,104 | 110,123 | 1 → 110,085、2 → 19 |
| FULL-REGENT-ELITE-00 / 1,000,000 | 6 | 未触顶 | 72,887 | 72,887 | 1 → 72,887 |
| FULL-REGENT-ELITE-00 / 1,000,000 | 7 | 未触顶 | 116,253 | 116,303 | 1 → 116,203、2 → 50 |
| FULL-REGENT-ELITE-00 / 1,000,000 | 8 | 未触顶 | 198,459 | 198,459 | 1 → 198,459 |
| FULL-SILENT-ELITE-03 / 1,000,000 | 1 | 未触顶 | 423,863 | 423,863 | 1 → 423,863 |
| FULL-SILENT-ELITE-03 / 1,000,000 | 2 | 未触顶 | 115,004 | 115,004 | 1 → 115,004 |
| FULL-SILENT-ELITE-03 / 1,000,000 | 3 | 未触顶 | 832,793 | 832,793 | 1 → 832,793 |

完整配置、结果和每通道诊断见 [results.json](transposition-cap-evidence-20260920/results.json)。

## 检查与复跑

Release、结构门禁通过。TranspositionFrontierChecks 共 1,024,010 项通过，含独立前沿计数、首次触顶、缓存重建保留峰值和标签分布。单位检查的上限 2 只验证诊断，不作为生产默认触顶证据。未实机验证。

构建完成后再启动宿主；批次前检查 `pgrep -fl OfflineSearchHarness`，存在其他批次就等。最多两个宿主，重型根用一个；批次期间不重建 DLL。

```sh
python3 docs/performance/transposition-cap-evidence-20260920/materialize_corpus.py --out .local/cap-inputs --dll <待测DLL绝对路径> --prefix diagnostic
python3 tools/OfflineSearchHarness/run_plan.py --plan .local/cap-inputs/plan.json --workspace .local/cap-fixed --workers 2 --harness tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll
```

对 main 使用相同输入另跑一臂，`compare_results.py` 的前缀不带结尾短横。VeryHigh 使用 `materialize_corpus.py --heavy --out .local/cap-heavy-inputs --dll <待测DLL绝对路径> --prefix heavy`，再将 run_plan 的 workers 设为 1。指定单根加 `--root FULL-REGENT-ELITE-00`；只有默认臂触顶才加正整数 `--transposition-entry-limit` 跑放大臂，并确认该臂未触顶。
