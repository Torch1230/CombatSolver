# 极高配置下的等质量热路径优化（2026-09-08）

[返回性能索引](../../performance/README.md)

## 范围与接受条件

基线为上游 `84c12be`（0.33.7）。本地分支 `perf/veryhigh-quality-preserving`。只测 `VeryHigh`，显式固定 DOP4、16 GB No-GC；不调整 Beam、节点、分支、时间、药水或成长预算。战损不能提高；回合数若提高，只有耗时下降超过 50% 才可接受；累计分配及进程峰值占用也需检查。

极高原配置：Short 20,000 ms / Beam54 / 10,000 节点 / 45 动作分支，Deep 300,000 ms / Beam135 / 50,000 节点 / 72 动作分支。当前生产 coordinator 的初始搜索按其原有策略使用这些配置；不能只凭结果 phase 文案判断实际搜索工作量。

## 实现

- 模拟器出牌判定额外返回刚查询得到的原始能量/星能费用。手牌估值直接使用它们，省去同一稳定状态下再次遍历费用 Hook。星能支付超额能量仍仅影响可支付性；估值继续使用此前采用的转换前费用。没有新增跨动作或跨分支缓存。
- `ReachableHandPotential` 保持双资源 0/1 背包的递推顺序与整数溢出行为。常见手牌使用局部栈存储；容量较大时回退到普通数组，堆外没有持久池。全部牌都可容纳且总值可表示时直接求和。
- 候选准入仍使用原排序、代表类别、引用身份去重和配额。移除每次 Add 的捕获闭包、类别扫描的 LINQ 对象及复活窗口首项选择的临时排序链；完全同分仍保留原候选顺序中的首项。

## 正确性证据

`tools/testing/checks/ReachableHandPotentialChecks` 直接编译正式 helper：10,000 组随机小牌组与所有子集的最优解比较；另覆盖零费用、空手牌、整数溢出、超过栈阈值的大容量，以及常见规模 10,000 次调用零分配。

`HAND-POTENTIAL-COSTS` 在原版建局后比较实际与模拟的可出牌性；对可出牌项逐一比较复用费用、原版费用和旧式重复查询费用。覆盖普通卡、能量 X、星能 X、不可打出和条件卡，以及虚空形态；完整 ContinuationStamp 证明查询未改真实根与分支。它不穷举第三方有副作用的费用/可出牌性回调。

两端结构门禁通过（78 个 Search 文件）；Linux Release 构建 0 警告、0 错误。

## Headless 固定工作量 A/B

正式测量关闭详细诊断、阶段计时和增量回放。两个独立构建各自先预热机甲骑士，再采集三次；没有把冷启动、Profiler 样本或正确性模式的时间混进表内。压力场景和小啃兽各取一组同工作量 A/B。

| 场景 | 基线耗时 | 优化耗时 | 耗时下降 | 分配量（基线 → 优化） | 相同质量与工作量 |
|---|---:|---:|---:|---:|---|
| 原生机甲骑士（3 次中位数） | 13.6828 s | 13.0316 s | 4.76% | 6,619,414,904 → 6,500,150,248 B（−1.80%） | 战损8、T7、无药；15,814展开/129,774转移/75,797选择；34步动作一致 |
| 大牌组压力（单次） | 9.9572 s | 8.7152 s | 12.47% | 2,042,823,840 → 1,996,866,832 B（−2.25%） | 同一死亡边界、42步动作、6,958展开/25,483转移；不能作为获胜质量证明 |
| 原生小啃兽（单次） | 3.1377 s | 2.9734 s | 5.23% | 1,445,548,856 → 1,418,867,160 B（−1.85%） | 零损、T3、无药；3,085展开/31,623转移；14步动作一致 |

以上成对结果的所有已输出非时序搜索指标、评分及完整动作日志一致。机甲骑士按 100 ms 采样的进程峰值 RSS 中位数为 8,435,879,936 → 8,217,448,448 B（−2.59%）。复用进程中的小啃兽单次峰值有约 6.4 MB（0.07%）反向波动，不能据此把所有占用指标写成严格逐样本下降；独立进程与可见会话结果另列。

## 可见 Steam 与完整部署

环境：Linux x86_64，AMD Ryzen 7 7840H，游戏 0.111.0，正常 Steam 可见窗口；原版 + STS2-RitsuLib + 本地 CombatSolver。两份构建分别启动独立游戏进程，无其他并行性能请求；可见数据为各一次冷进程样本，不与 headless 中位数混算。

| 机甲骑士完整部署 | 基线 | 优化 |
|---|---:|---:|
| runId | `2375d506a3ac455e94b6ccf9cefbed96` | `c2c1e8f6ee584fd1bbb5066b1bd98d83` |
| 初次搜索墙钟 | 19.5643 s | 18.7779 s（−4.02%） |
| 搜索累计分配 | 6,663,659,080 B | 6,542,946,760 B（−1.81%） |
| 搜索结束 RSS | 8,216,457,216 B | 8,082,313,216 B（−1.63%） |
| 整场结果 RSS | 8,738,631,680 B | 8,611,094,528 B |
| 搜索 GC 总/最大暂停 | 0 / 0 ms | 0 / 0 ms |
| 搜索 observed 最大帧 / >50ms 帧数 | 916.7 ms / 75 | 913.9 ms / 72 |
| 战损 / 结束回合 / 药水 / 非预期重算 | 8 / 7 / 0 / 0 | 8 / 7 / 0 / 0 |

两次均 Passed，完整运行约78秒，小于120秒期限；Instant / 0秒部署，并验证原速度恢复。所有已输出非时序搜索指标一致，27条实际出牌部署日志逐项相同，跨回合仍复用同一路线。没有运行增量搜索模式；这里的整场部署与原生边界校验不等于逐转移增量回放。

基线完成退出时，外层采样器读取了已退出进程而触发 `KeyError: VmRSS`，因此没有保存基线采样峰值；原测试结果与游戏日志完整保留，未重跑成功基线。修正采样器后候选的进程 VmHWM 为 8,614,121,472 B，低于基线已观测的整场结果 RSS，足以排除该场候选峰值高于基线，但不能给出精确的基线峰值降幅。冷进程初始 GC 堆/碎片快照存在小幅波动，不把这些历史堆快照当作搜索存活量。

第一次按用户原有完整 Mod 列表运行基线，`cd8e58d396344dec8e0b2362f216879b` 在搜索前因未适配的 `AveMujica.AveMujicaCode.Ftue.DreamspinFtue` gameplay subscriber 被拒绝，120秒超时；未放宽门禁或延长期限。随后临时选择支持的最小 Mod 集完成上述对照，结束后恢复原设置、设置备份及已安装 DLL/manifest。不能据此宣称用户完整 Mod 组合已通过。

针对 headless 复用进程中 +0.07% 的 RSS 样本，另取两个独立可见进程做内存对照（各一次、首个搜索结果停止）：

| 小啃兽独立可见进程 | 基线 | 优化 |
|---|---:|---:|
| runId | `eb56a536a72a4b1a904153f35eafbbc6` | `60f65ab1bddb410da8de39dcfe26fe12` |
| 搜索墙钟 | 6.1115 s | 5.8478 s（−4.31%） |
| 搜索累计分配 | 1,478,587,712 B | 1,452,086,784 B（−1.79%） |
| 进程 VmHWM / 采样峰值 RSS | 2,957,713,408 B | 2,932,641,792 B（−0.85%） |
| 搜索结束存活托管字节 | 1,557,550,968 B | 1,531,069,640 B |
| 搜索 observed 最大帧 / >50ms 帧数 | 923.5 ms / 25 | 923.3 ms / 24 |

均 Passed，3,085展开、31,623转移、13,827选择分支，14步路线和其余非时序指标完全相同，预测零损/T3/无药，GC总与最大暂停均0。此项没有实际部署整场，不能把预测质量写成实战结束断言。该对照未复现进程峰值上升，但仍保留此前反向样本，不声称全部场景、所有单次内存指标必然下降。

[结构化测量记录](veryhigh-quality-preserving-20260908.json)保留本轮 runId、指标及采样结果；完整 request/result/game.log 和 profiler 留在本地 `.local/veryhigh-perf-20260908`，不提交本机路径与大型跟踪文件。

## 复现

可见原生请求：`coverage/fixtures/runtime/performance-veryhigh-mecha-native.json`。它使用明确的战前牌组与固定种子，独立于旧版本的部分跑局存档。旧 `mecha-knight-memory-run-snapshot.json` 在本轮上游建局时缺少角色 ID，不能当作本轮已通过场景。

```bash
dotnet run --project tools/testing/checks/ReachableHandPotentialChecks -c Release
./tools/performance/run-visible-steam-benchmark.sh --request-fixture-path coverage/fixtures/runtime/performance-veryhigh-mecha-native.json --timeout-seconds 120 --evidence-directory .local/veryhigh-visible
```

```powershell
dotnet run --project tools/testing/checks/ReachableHandPotentialChecks -c Release
pwsh -NoProfile -File tools/performance/run-visible-steam-benchmark.ps1 -RequestFixturePath coverage/fixtures/runtime/performance-veryhigh-mecha-native.json -TimeoutSeconds 120 -EvidenceDirectory .local/veryhigh-visible
```

费用合同使用 `coverage/fixtures/cards/setups/hand-potential-cost-cards.json`：

```bash
./tools/testing/run-unattended-test.sh --scenario-id HAND-POTENTIAL-COSTS --character-id REGENT --enemy-current-hp 999 --initial-player-energy 3 --initial-player-stars 3 --clear-player-piles --cards-path coverage/fixtures/cards/setups/hand-potential-cost-cards.json --performance-preset-for-test VeryHigh --force-short-search-only --short-search-budget-override-milliseconds 100 --stop-after-initial-solver-result-assertion --headless-instance hand-costs --timeout-seconds 120
```

第二项在同一命令增加 `--powers-json '[{"powerId":"VOID_FORM_POWER","target":"Player","amount":1}]'`。100ms仅用于合同检查后的收尾搜索，其时间不属于性能证据。PowerShell 使用同名 PascalCase 参数（`-ScenarioId`、`-CardsPath`、`-PowersJson` 等）。

新增 `RequestFixturePath` / `--request-fixture-path` 读取完整请求并保留其中预设、预算和断言，仅覆盖本次 runId、超时和退出标志；与已有 logging/checkpoint fixture 模式互斥。脚本不负责选择或编译 DLL。每次切换 A/B 构建后都须结束加载旧 DLL 的游戏进程。

小啃兽使用相同请求的30张牌，修改 `scenarioId=PERFORMANCE-VERYHIGH-NIBBITS-NATIVE`、`seed=VH_PERF_NIBBITS`、`encounterId=NIBBITS_NORMAL`、`enemyCurrentHp=40`、`expectedInitialProjectedBattleHpLostAtMost=0`、`expectedInitialCombatEndedTurn=3`。

完整部署将此请求的 `stopAfterInitialSolverResultAssertion` 改为 `false`，设置 `expectedFinishedTurn=7`、`expectedFinishedPlayerHpAtLeast=57`、`expectedUnexpectedReplansAtMost=0` 和 `assertDeploymentSpeedRestored=true`；固定 Instant / 0 秒。

本轮不发布版本、ZIP、标签或远端 PR。性能收益只适用于本次记录的场景与环境，未证明全部战斗的普遍提速。
