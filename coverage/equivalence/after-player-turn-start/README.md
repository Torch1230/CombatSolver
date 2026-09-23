# 玩家回合开始三阶段镜像：等价对照

基线 `523aea571f109426a3c7fb015207b863df78548b`（0.44.0 + #126），候选派发实现 `641f28b8`。比较零第三方有效覆写时的既有搜索路径。原生顺序依据见 [native-order.json](native-order.json)，合同与结构检查见 [validation.json](validation.json)。

一次批次完成 **60 对、6341 个确定性字段，IDENTICAL**；两侧 120 次运行均 Passed/valid，无时间截断，未中断或补跑。

| 组 | 根数 | 比较字段 | 差异根 |
|---|---:|---:|---:|
| EQ | 10 | 1069 | 0 |
| FULL | 40 | 4212 | 0 |
| GA | 10 | 1060 | 0 |

## 口径与产物

EQ 10、FULL 40、GA 10；High / Beam 90 / nodes 250000 / 分支 48/28/36 / Coordinator / Smart / DOP 1，组合开关沿语料为 false，软预算 600000 ms。两臂各一个离线宿主，批次中不构建。未启动游戏。

`metadata.json` 保存最终比较计数、两侧 DLL/宿主及语料哈希；`results-summary.json` 保存每根确定性比较字段数、展开/转移数和原始结果/路线哈希。两份相同原始路线不进入提交。

原始请求与场景位于 `CombatSolver-weight-fit/.local/search-quality/corpus`，本机运行目录为 `CombatSolver-turn-start-2/.local/stage6-equivalence`。运行器拒绝覆盖已有批次目录；输入不变且已通过时不重复执行。

## 复现

先按 [离线宿主文档](../../../docs/OFFLINE_SEARCH_HARNESS.md) 构建宿主，提供匹配游戏 0.111.0 和 RitsuLib 的隔离资源路径。已有批次使用隔离 APFS 游戏副本作程序集来源，完成后可清理副本；不依赖真实存档。

```bash
python3 coverage/equivalence/after-player-turn-start/run.py \
  --corpus /absolute/path/to/corpus \
  --base-dll /absolute/path/to/base/CombatSolver.dll \
  --new-dll /absolute/path/to/new/CombatSolver.dll \
  --workspace /absolute/path/to/fresh-output
python3 coverage/equivalence/after-player-turn-start/summarize.py /absolute/path/to/fresh-output
```

运行器调用仓库 `run_plan.py` 与 `compare_results.py`，比较前缀为 `base`、`new`。比较器排除墙钟、分配量和 GC 等非确定性字段；该结果验证路线与搜索等价，不是性能结论。
