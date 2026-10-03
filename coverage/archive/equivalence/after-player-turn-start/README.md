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

## 历史复现

该批次的专用运行器已经退出当前目录，源码保留在 [历史提交](https://github.com/Torch1230/CombatSolver/tree/556e72994303e45ca2b2833aa09ba793d1b096cb/coverage/equivalence/after-player-turn-start)。当前等价对照使用 [离线宿主](../../../../tools/search/OfflineSearchHarness/README.md) 的运行器和比较器；历史结果不作为本轮复测。

运行器调用仓库 `run_plan.py` 与 `compare_results.py`，比较前缀为 `base`、`new`。比较器排除墙钟、分配量和 GC 等非确定性字段；该结果验证路线与搜索等价，不是性能结论。

目录归并仅调整材料位置和请求路径。历史哈希与通过数字保留为当时记录，不描述当前文件哈希，也不代表本轮重新运行。
