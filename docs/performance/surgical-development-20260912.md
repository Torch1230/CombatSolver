# 精简优化：实际探测与开发结果

本轮基于 `a399992`，在 `perf/surgical-fixes-20260912` 保留两项局部优化。生产差异共 3 个文件、29 行新增与 12 行删除。没有改动搜索预算、排名比较键、候选范围或战斗规则；没有引入另一套模拟器。

## 保留的改动

**空状态提前返回。** `BeforeCardPlayedMirrors.CompleteOrAbort` 和 `AfterCardPlayedMirrors.CompleteOrAbort` 在创建 `ReadEntries` 迭代器之前检查类型计数。缺失时返回，存在时仍执行原来的完整清理循环。两个入口各增加两行；容器的枚举和 Fork 合同不变。

**单次排序预计算 Beam 分数。** `BeamRetentionPolicy.SortByBeamRank` 把节点引用及其分数放入临时连续列表，使用原 `CompareBeamRankOrder` 排序，然后把节点引用原序写回。用于两处普通 Beam 排序以及 deferred 排序；终局质量排序保持原入口。公式依赖的 Snapshot 标量为只读属性，根初始值在求解初始化时确定，排序期间不会更新；缓存不跨排序、不保存在长期节点上，也不包含可变父排名。

两侧继续使用 `List.Sort(Comparison<T>)`。核对本机对应的 .NET 9.0.19 [List 实现](https://github.com/dotnet/runtime/blob/v9.0.19/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/List.cs)及 [ArraySortHelper 实现](https://github.com/dotnet/runtime/blob/v9.0.19/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/ArraySortHelper.cs)，这个重载使用相同的基于比较器的 introsort 路径。候选没有改用稳定排序或添加新的同分键；完整顺序另外通过合同与真实搜索对照验证。

## 真实调用计数

诊断版只用于判断频率，不用于耗时比较。计数为原子计数器，亡灵结果从进程累计数减去前面的机甲数；两个场景均在第一次 solver 结果处停止。

| 计数 | 机甲 VeryHigh | 亡灵 Low + FixedBudget |
|---|---:|---:|
| BeforeCardPlayed 空枚举 | 44,544 | 456,904 |
| AfterCardPlayed 空枚举 | 44,544 | 463,084 |
| 标量 Peek 缺失 | 0 | 0 |
| BeamRankScore 调用 | 1,979,746 | 12,446,862 |
| Fork 有效状态类型数 | 126,507 次均为 0 类 | 2 类 291,861 次；3 类 264,008 次；4 类 32,084 次；5 类 1,649 次 |

结合前轮独立探针的 96 B/空枚举，两个入口的算术空间分别约 8.16 MiB 和 84.23 MiB 累计分配。真实搜索的总分配还受其他路径和分层编译影响，因此实测差值不要求精确等于此算术值。

没有继续开发标量 Peek 改写：当前目标场景没有缺失命中。辅助字典预分配在占绝大多数的 2–3 类型上不能消除扩容，4–5 类型的整体空间很小，暂不增加实现。小类型内联/列表替换尚未取得本轮真实收益证据。这个结论只决定本轮优先级，不代表所有其他场景都没有机会。

排名预计算先在独立模型中筛选表示方式：字典缓存反而更慢，连续的节点/分数对才值得进入真实对照。独立筛选不是整场加速证据，最终判断采用下面的实际搜索结果。

## 无头性能对照

分别进行了两轮单因素 A-B-B-A。每轮四个新游戏进程，每个进程先预热机甲、亡灵各一次，再正式测量各一次。DOP8、NoGC 16 GB、输入牌序和预设一致；机甲使用正常 VeryHigh，亡灵使用 Low + FixedBudget。每次请求总超时 120 秒，在初次 solver 结果处停止。诊断版与增量验证的时间没有混入性能样本。

| 单因素变化 | 机甲 | 亡灵 |
|---|---:|---:|
| 空状态优化：累计分配 | −8.19 MiB，**−0.138%** | −83.73 MiB，**−0.264%** |
| 空状态优化：平均耗时变化 | −0.60% | +0.03% |
| 该轮首尾基线耗时漂移 | +1.46% | −1.68% |
| 排名预计算：额外累计分配 | +1.42 MiB，+0.024% | +8.80 MiB，+0.028% |
| 排名预计算：平均耗时变化 | −2.11% | **−2.31%** |
| 该轮首尾基线耗时漂移 | −2.35% | +0.77% |

空状态优化建立了小而稳定的累计分配收益，未建立耗时收益。仅四行改动，无新增容器或长期缓存，保留。

排名预计算的亡灵两对比较均更快，分别约 −2.53% 和 −2.10%；平均时间从 30.340 秒降至 29.638 秒，约少 0.702 秒。以小幅额外分配换取该目标场景的可观测耗时下降，保留。机甲平均从 3.904 秒降至 3.821 秒，但变化接近基线漂移，不能声称已经证明稳定提速。每个实现只有两个正式样本，没有统计置信区间或全硬件推广结论。

上述两个单因素试验不是一次直接的“原版对最终组合”测速，不能把两列耗时百分比相加。分配节省与排名临时列表的新增成本分别报告；不能把累计分配减少称为 GC 后保留堆或峰值 RSS 减少。RSS 和结束工作集有明显波动，**本轮没有建立稳定的峰值内存改善**。

按照本轮后续指令，可见测试已停止。启动尝试没有产生可用游戏性能数据，测试前的安装文件已恢复；因此这里全部是无头 A/B 结果，不承诺可见会话的 FPS、卡顿或整场速度提升。未执行 Windows/PowerShell 平台验证。

## 决策与清理验证

每个场景在两轮试验中的全部正式样本，96 项非时序字段均一致；机甲 54 行、亡灵 113 行完整 ACTION/TURN_OUTCOME/FORECAST 记录也一致，两轮的参考记录相互一致。比较保留完整路线和工作计数，未仅凭聚合 HP 认定等价。

[排序合同](../../tools/BeamRankSortChecks/README.md)直接提取当前生产排序、评分和比较方法，链接生产 SolverWeights，以不可变标量输入进行 720 组、167,280 条目对照。覆盖全同分、升序/逆序、重复对象、NaN/无穷/正负零、大小边界和根参数组合，逐槽比较引用身份。它证明这些输入上的排序合同，不冒充完整游戏语义证明。

`CARD-PLAY-CLEANUP-CONTRACT` 在真实游戏宿主内使用原版模型和生产清理入口，验证空状态不物化、非空配对只清除目标 CardPlay、其他 CardPlay 不变、成功提交、中止清理、删除后零计数以及子分支移除不影响父状态。Passed，runId `6a59494233984b7582ba6213c528a724`。这是定向所有权合同，不是原版命令与模拟的完整差分。

小型增量搜索在 Low、固定短预算、DOP1 下逐转移校验，第一回合两次攻击结束、预测战损 0。Passed，runId `4685d92766c04f99ace8f6e3e70da3c3`。增量模式的时间不用于性能结论。

原先尝试复用较大的 `--verify-fork-boundaries` 合同，但它在到达相关清理断言之前，停在 `AssertEndTurnPowerChoiceSuspends` 的“回合结束 Power 产生挂起选择后错误完成了 Power 阶段”断言。候选与未修改基线均重现，分别为 `guard-score-hook-boundaries` 和 `baseline-hook-boundaries`。本轮未修改这条既有路径，也没有把完整 Fork 合同报告为通过；定向合同用于独立覆盖本次改动。

Release 构建通过，Linux 结构门禁通过。生产行为在性能 A/B 之后没有再修改；后续仅加入定向测试和文档。构建产物没有发包，版本仍为当前开发批次。

## 证据与复跑

[结构化证据](surgical-development-20260912.json)保存正式样本 runId、单次耗时/分配/GC/工作集、比较字段与排除字段、完整参考路线、原子计数及合同结果。启动失败和原合同失败与通过数据分别记录。实验中的完整请求、日志和 DLL 位于本地 `.local/surgical-dev-20260912/`，不提交二进制或完整运行日志。

定向清理检查通过通用 scenario-id 接口运行，两端脚本均无需增加参数：

```bash
./tools/run-unattended-test.sh --scenario-id CARD-PLAY-CLEANUP-CONTRACT \
  --stop-after-combat-root-snapshot-assertion --timeout-seconds 120
python3 tools/BeamRankSortChecks/run.py
```

性能夹具分别来自 `coverage/unattended/performance-veryhigh-mecha-native.json` 的原生牌组，以及 `search-performance-necrobinder-projected-*` 的牌组、遗物、药水输入。亡灵档位被明确固定为 Low，不能把结果外推到 VeryHigh；完整请求还固定角色、种子、敌人 HP、玩家 HP、药水政策和 DOP，单独复制牌组并不能复现本轮工作量。
