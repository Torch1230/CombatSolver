# 有界搜索证据共享与回传（2026-09-27）

当前为研究分支，保持玩家单一自动搜索入口，没有新模型、训练或玩家策略开关。

## 所有权与算法

每个自动主搜索轮次创建 `SharedSearchEvidence`，同一冻结根和药水政策下的成员共享4096个直接映射槽。缓存只持有128位状态/前缀键、四项跨回合探测数值和终局标量，不保留模拟状态、节点、候选队列或可执行闭包。冲突替换仅失去复用机会，不改变准入。

前缀身份包含完整祖先状态、动作、逐实例卡牌/目标/药水、嵌套及回合开始选择、根准备选择、动作数、回合和累计政策标签；不包含显示标题、启发式分数及可变保留排名。超过256动作时旁路证据优化，继续原搜索。

跨回合探测只有正常完成且无风险的结果可以共享。串行端查询及发布，worker仍独占模拟；严格增量模式在共享命中后重新执行转移和完整回放，并比较探测数值。未知、挂起选择或风险结果不缓存。

每次改善的完整胜利回传到沿途所有可用前缀。中间排序交替使用按真实终局政策排列的已知前缀与原启发式排列的未知前缀；终局节点位置保持，最终比较及转置支配不使用该证据。它是可行后续的见证，不能作为最优界，也不是统计期望。原手写估值仍服务于缺少证据的候选；未宣称消除了全部权重。

这里复用的是跨回合探测的计算结果，并未共享所有卡牌转移或整棵搜索树。

## 合并上游前的验证

基线为 `43f98e46`，固定原有输入、DOP1、Beam24、12000节点、30秒软预算、禁用药水。使用此前保存的同配置自动搜索结果作比较，不是交错重复性能测量。

| 场景 | 搜索秒数旧→新 | 转移旧→新 | 工作线程累计分配 GiB 旧→新 | 峰值工作集 MiB 旧→新 |
|---|---:|---:|---:|---:|
| focus_investment | 4.563→3.969 | 16057→14966 | 0.552→0.520 | 256.8→258.4 |
| random-regent | 17.581→24.499 | 106204→159193 | 4.055→6.253 | 405.1→447.1 |

两根战损分别保持0和13。专注根共享探测命中284次，回传148项、142次候选排名命中；摄政王根对应107、86、24。后者更多搜索工作导致耗时和内存上升，不能报告普遍提速或内存下降。这组反例保留供后续判断，不筛除。

- Release 主项目和离线宿主构建通过。
- `--check-shared-evidence`：26项纯合同通过，覆盖沿途回传、历史/选择/目标/政策隔离、未知探索、风险/边界、固定容量和替换。
- 原生 `SHARED-SEARCH-EVIDENCE` 通过：跨宽度真实缓存命中与完整增量回放、实际终局回传、DOP2及缓存命中取消；独立实例由启动器成功清理。
- Linux 结构门禁通过；两平台规则同步，未在Windows执行。
- 所有新测试保守计入原半小时账本，目前累计1784.404秒，训练0秒。数据见[结构化证据](shared-search-evidence-20260927-evidence.json)。

## 复现

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --check-shared-evidence
./tools/run-unattended-test.sh --scenario-id SHARED-SEARCH-EVIDENCE \
  --encounter-id FUZZY_WURM_CRAWLER_WEAK --preserve-native-combat-state-for-test \
  --headless-instance shared-evidence --timeout-seconds 30 \
  --exit-on-complete --cleanup-instance-on-exit
```

PowerShell对应 `-ScenarioId SHARED-SEARCH-EVIDENCE -PreserveNativeCombatStateForTest -HeadlessInstance shared-evidence -TimeoutSeconds 30 -ExitOnComplete -CleanupInstanceOnExit`。

用户随后要求合入上游0.47.0。上面的数字和原生结果属于合并前实现，不能当作合并后的验收结果。
