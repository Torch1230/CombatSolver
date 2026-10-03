# B015 阶段一：故障基线与统计消费者隔离（2026-10-02）

关联：Refs #174。基线为 `88298ae54fd626e52e87efd568dd19ffd507aafd`（0.47.3）。整批 T016–T020 仍未全部验收。本文保留阶段一历史；更新结论见[后续证据](b015-follow-up.md)。后续发现：构建身份在 checkpoint.json.build 中已存在；T019 实际为多人实验分支的 Last(predicate) 空匹配，下面早期对 First 的推测已撤回。

## 认领与范围

刷新 #174、#171、CONTRIBUTING 与 testing-guide 后，维护者回复仍为“感谢！”，Assignee 为空。规则要求维护者指派唯一负责人，但没有写正式指派前不得本地调查或修复。这里只记录已获积极回应的认领申请，不宣称正式指派。本阶段只在独立分支本地执行；不评论、推送、创建 PR、部署用户游戏目录或发布。PR #188 的邻近性能改动未合入。

## 五主题调查

| 主题 | 本轮可证明事实 | 当前结论与下一边界 |
|---|---|---|
| T016 | 原包 `e63cd12543994bb08b9497e45839005c` 的 combat/000.jsonl:56 记录失败候选 SHIV、TargetCombatId=2、父状态总敌 HP=4。此前依次为 AFTERIMAGE、STRANGLE(target=1)、UP_MY_SLEEVE、SIDESTEP、SHIV(target=1)。原场 Axebot 带 StockPower。 | 调查重点收敛到替补生成后，准备候选与重放的生物身份/目标解析。`Expansion.Replay` 经 `GetCreature(TargetCombatId)` 传给 ManualPlay；尚未取得当前版本失败夹具，不能仅加空目标跳过或改目标。 |
| T017 | `17d363178b9b4a58a478c6ab41d453a5` 的未分类字符串异常属实。但本机正版 0.111.0 原版 SovereignBlade.CanonicalVars 仅有数值/计算变量，整个 arm64/x64 原版程序集均无 SeekingEdgeSuffix 的 ASCII/UTF-16LE 文本。原包加载 RegentFX 等额外 Mod。 | 原资料称“原版字段”尚不能成立；未知字符串继续显式拒绝。需要定位实际字段写入者、核对它是否仅显示后再决定合同；尚未证明具体 Mod 是写入者，也未做原包环境复现。 |
| T018 | `ad752303ab53403faf085c2f7e4b1c83` 的 process/000.jsonl:11 更早记录 RunStatisticsStore 构造时的 JsonException：0x00、offset=0。消费者已经 faulted，旧 `_Process` 只停帧处理；后续 Activity 仍向容量256的队列写入，最终 combat/003.jsonl:41 中断部署。 | 已取得此根因的修改前失败/修改后通过。修复只隔离已经终止的统计消费者；健康消费者跟不上而真实满队列的情况仍沿原显式失败政策，未称所有队列故障已修。 |
| T019 | `fc5a35417db14e428143e8df72750a3e` 的 combat/000.jsonl:372 失败候选为 turn=5 EndTurn，玩家仅1 HP，前缀最后为 UsePotion、CRIMSON_MANTLE。:383 的最早项目帧为 TriggerAfterSideTurnStart。 | 下一步在1 HP下构造 CrimsonMantle 回合开始自伤、参与者移除与状态查找边界。当前及 v0.47.2 源码该函数已分段，源码没有直接 First 调用；可能涉及内联下层查找，不能据此判已修。当前未复现。 |
| T020 | `3f4c6b58670143308359a048b0d4899e` 的战前 save 已保存 MAD_SCIENCE+1 的 TinkerTimeType=0；同一历史房间记录获得 type=2/rider=6、移除该牌及获得升级但 type=0 的牌；这些数组不能确定精确操作先后或调用者。combat_start 与首个搜索根也都为0，live continuation 为 None/None。原版 OnPlay 同样拒绝 None。 | 最早可见无效边界在战前牌组替换/升级过程，早于捕获/Fork/搜索。不能默认补 Attack，也不能称已证明模拟克隆丢状态。下一步追该替换来源与合法原生生成/升级/保存恢复对照；原包有 FreeLoadout 等 Mod，但未归因。 |

五个包均声明游戏0.111.0；T016使用RitsuLib0.6.3，其余0.6.2。原包完整环境、索引、静态证据与安全解包清单保留在忽略目录 `.local/issue-bundles/174/`。未执行原包 Preflight、RestoreOnly、ReplayRecorded 或整场部署。

## T018 改动

Runtime 的 `CanRecord` 在帧更新、入队及跑局启动入口识别已结束的消费者。首次命中时终止本次统计、清空内存队列与有效快照、关闭上传并保留原异常诊断和原始文件。后续事件不能继续累积到失效队列；不会以零统计或成功恢复替代损坏数据。存储解析仍显式失败，未改变统计聚合、存储格式、健康队列容量、战斗模拟或搜索预算。

最小夹具 `coverage/fixtures/runtime/run-statistics-worker-failure.json` 启动真实 `RunStatistics.ProcessAsync` 读取四个NUL字节的 `.run.json`，确认原始 JsonException，经过实际 `_Process` 观察点，再提交300个 execute 信号；断言有效跑局与快照被清除、队列为空、损坏文件字节未变。它不启用线上遥测，也不依赖玩家真实统计目录。

## 实际验证

本机：macOS arm64、.NET SDK9.0.109、正版游戏0.111.0（release commit41cef1ea）、RitsuLib0.6.5（0.111.0兼容分支）、CombatSolver0.47.3基线。0.6.5满足主线最低0.6.0，但不代表历史0.6.2/0.6.3组合已验证。构建使用忽略的 local.props 显式指向macOS程序集，CopyModOnBuild=false。

| 验证 | 结果 |
|---|---|
| 基线与夹具 Release 构建 | 0警告/0错误 |
| 同输入 T018 修改前，runId=b015-t018-before | Failed；Run statistics queue capacity exceeded；请求21.97秒 |
| 同输入 T018 修改后，runId=b015-t018-after | Passed；请求25.15秒；300次后续事件、快照失效及源文件保留断言通过 |
| 最终行为源码 Release 构建 | 0警告/0错误 |
| `dotnet run --project tools/testing/checks/RunStatisticsTests/RunStatisticsTests.csproj -c Release` | streaks/gaps/abandonment/dedup/persistence/historical separation/recovery合同通过 |

两个请求均使用120秒总预算，夹具与输入相同，仅runId和证据目录不同。macOS隔离启动器保留在 `.local/run-macos.py`，实际命令：

```sh
python3 .local/run-macos.py coverage/fixtures/runtime/run-statistics-worker-failure.json t018-before
python3 .local/run-macos.py coverage/fixtures/runtime/run-statistics-worker-failure.json t018-after
```

它参考仓库现有macOS启动器，克隆正版app到本仓库 `.local/headless-instances/b015`，使用隔离HOME、只装RitsuLib及当前构建、禁用Steam，并在每次结束删除整个实例。沙箱内第一次启动在进入测试前退出，随后获允许在沙箱外运行；这次启动失败不计语义基线。正式两个请求都得到结构化结果；Godot退出时仍有静态字符串/RID清理错误，因此不声称游戏进程退出健康性通过。两次实例目录均已删除。

Windows/Linux可使用既有无人测试脚本，设置 ScenarioId/--scenario-id 为 RUN-STATISTICS-WORKER-FAILURE，空Cards、EnemyCurrentHp=50、EvidenceDirectory、TimeoutSeconds=120及清理实例开关；本轮未运行这两个平台。

详细结果在 `.local/evidence/t018-before/result.json` 和 `t018-after/result.json`，构建及统计合同日志同目录保存。当前结论限T018已终止消费者的故障隔离；未验证真实Activity→整场部署、真实上传、健康消费者饱和、损坏统计修复或其余四主题语义验收。发布应使用Refs #174并保留剩余主题，不能关闭全批。
