# 花园幽灵鳗分配优化（2026-09-19）

基线为本轮刚拉取的上游 `8be14100`（0.42.0），分支为 `perf/garden-allocation-20260919`。两处小范围优化使原包固定工作量搜索的累计分配减少 **3.25%**；A-B-B-A 四次样本平均搜索耗时减少 **0.97%**，变化较小，不宣称显著提速或可见卡顿已解决。[结构化证据](garden-allocation-20260919.json)保存全部样本和比较口径。

## 既有研究与范围

- 本轮在独立 worktree 开发，没有改动旧分支的工作区。`research/memory-guard-boss` 已被当前上游包含；`experiment/learned-selector` 相对当前上游仅余 `f6d04a78` 的枚举装箱否定报告。旧实验分支不整批 cherry-pick。
- 已读[分配归因研究](../research/allocation-attribution-20260917.md)、[通用分配优化](general-allocation-20260914.md)、[保留表研究](search-retention-bounds-20260919.md)，以及旧分支 `f6d04a78` 的 `strategic-requirements-flags-20260919.md`。Power 动态变量的 owner、复位与可变状态使其深拷贝不能直接删除；按类型名猜调用点、低收益装箱替换、全面 COW 等方向不沿用为已证明的优化机会。
- 生成池复用、模型 ID 缓存、选中路线续用文本延迟生成等已经在上游，本轮不重复实现。只优化实际采样命中的静态怪物意图和 Skittish 回调；不改搜索候选、评分、预算、停止条件、GC 策略或版本号。

## 原包恢复与定位

输入为报告 `2dbe8779188b4cbebc6293d4e89c20a1`，包内声明 CombatSolver 0.42.0.0 / 游戏 v0.111.0，原环境 Windows、DOP16、No-GC 配置 12 GB。原请求 Beam 512、节点上限 500,000,000、时间预算 300 秒、零战损早停。

本机 Linux 的 `RestoreOnly/start` 对开战及首个可操作检查点的 ContinuationStamp 对账通过，状态为 `restored_continuation`。旧包缺模型编号映射，`nativeStateVerified=false`、原因 `legacy_model_id_mapping_not_recorded`；不能称完整原生二进制恢复，也不能将本机测量冒充用户 Windows 会话。

第一次采集随测试进程清理而缺少正常收尾，严格解析失败；容错解析虽读到 tick，但栈全未解析，**不采用**。改为在测试退出前结束 30 秒采集，基线和候选的严格解析均通过：缺栈、仅未解析栈、丢事件均为 0。采集只用于找热点，不进入耗时对照。

基线窗口包含约 1.250 GB 已确认搜索分配样本，其中 `BranchMonsterAi.CurrentMove` 调用链约 36.03 MB：每次读当前意图都新建 `ForecastMove`、命中列表、扩容数组和枚举器。`AfterAttackMirrors.HandleSkittishPower` 调用链约 17.27 MB，包含工厂闭包和 LINQ 遍历。

复采样候选窗口包含约 5.024 GB 已确认搜索分配样本，未再出现 `CurrentMove` 的分配栈。用本地 GcTraceAnalysis 副本按**精确类型**过滤原 etlx 后进一步核对：

| 类型与调用栈 | 基线采样字节 | 候选采样字节 |
| --- | ---: | ---: |
| `ForecastMove`，`BranchMonsterAi.CurrentMove` | 11,193,888 | 0 |
| `Func<SkittishPredictionState>`，`HandleSkittishPower` | 5,435,632 | 0 |

该工厂委托类型在候选中仍有来自 `EndTurnPowerSupport.TriggerBatch048` 和 `ResetSkittishTurn` 的样本，不能声称整个类型全局零分配。两次采样覆盖的搜索工作量不同，表格只证明目标调用栈被消除，不用两窗口总字节直接计算收益。

## 实现与所有权

1. `BranchMonsterStaticSnapshot.Capture` 使用本来已经捕获的攻击基础伤害与次数，为根内每个 `MoveState` 建立只读意图，按引用身份查找。列表经只读包装后不再修改，缓存随根及其分支存活，不是全局缓存。Power/强度等最终伤害修正仍走原逐分支结算。
2. `TestSubject.MULTI_CLAW_MOVE` 继续根据本分支 `_extraMultiClawCount` 构造命中序列；分支内新建的 `STUNNED` 等根外行动继续走原回退，不按同名行动错误复用。
3. Skittish 使用已有的 `PredictionStateStore.Get(model, static factory)`；按原顺序直接遍历攻击结果。遇到第一个归属本体的结果便停止，即便该结果被全额格挡，也不能继续寻找后续有伤结果。触发状态的创建时机、写入顺序、每回合重置和 GainBlock 调用保持原样。

新增测试 helper 在怪物差分入口枚举所有原生 MoveState，逐项核对伤害与次数，并验证父/兄弟分支、已返回序列、分支眩晕回退；实验体额外覆盖次数增量 −100、0、3 的最小一次攻击约束。它不进入正式搜索路径。

## 原包固定工作量对照

两侧都从原始 ZIP 的 `start` 恢复，经原生首次准备后运行 `SearchOnly`。只为测量覆盖 profile 的 `maxExpandedNodes=8000`、软时间 60 秒、`fixedBudget=true`、DOP1；Beam 512 和分支额度 100/42/54 不变。协调器的能力成员预留等仍生效，所以请求总展开为 17,600，而非 8,000。

两侧都使用本机测试默认 **16 GB No-GC**，并非原包的 12 GB；四次均保持到指标捕获，搜索内 GC 暂停为 0。这是隔离分配/执行成本的对照，不是原机内存压力或回收改善验证。没有开采样器、增量验证或并行构建。每份样本独立进程、总超时 120 秒，启动器均完成实例目录清理。

| 执行顺序 | 搜索累计耗时 s | 请求搜索分配 B | 展开 | 转移 | 预计战损 |
| --- | ---: | ---: | ---: | ---: | ---: |
| A1 上游 | 19.0376 | 9,199,664,368 | 17,600 | 217,538 | 1 |
| B1 候选 | 18.9588 | 8,900,438,704 | 17,600 | 217,538 | 1 |
| B2 候选 | 18.9577 | 8,900,468,640 | 17,600 | 217,538 | 1 |
| A2 上游 | 19.2497 | 9,199,450,856 | 17,600 | 217,538 | 1 |

分配使用 `totalWorkerAllocatedBytes`，时间使用 `totalElapsedMilliseconds`，都是请求内实际各 solver 的累计值，不是启动/恢复在内的进程墙钟或同时占用内存。两对各 60 项非时序 `solverMetrics`（含组合成员递归去除时间/内存字段）全部相同，排除表保存在 JSON；均由节点边界结束。该原包测试输出未保存完整最终动作链，因此不以这些指标单独声称全路线逐字段等价。

## 哨兵与原生验证

- 离线哨兵 `sel-defect-elite-02`：VeryHigh、DOP1、20,000 节点、60 秒上限、Evaluate、普通 GC。两侧均自然终止，8,700 展开、34,331 转移、战损 2。工具的 86 项比较一致，另核对**完整** `planActions`（包括选牌）、全部 `cachedContinuations/continuations`、预测/live 根戳、所有 pruneCounters、政策及目录指纹均相同。
- 哨兵进程累计分配 1,857,097,568 → 1,846,904,032 B，请求墙钟 8.6012 → 8.6707 秒（约 +0.8%）；只是一对，不能判断稳定速度收益或回退，明确保留这一结果。
- [7 项原生差分输入](../../coverage/unattended/garden-allocation-native.json)：花园幽灵鳗 BITE/LASH/FLAIL/ENLARGE；双重打击首击全额格挡而后击有伤、首击有伤两例；实验体 MULTI_CLAW。全部通过，测试总耗时 24.46 秒。两种多段攻击另显式断言敌方最终格挡 0/7。差分前同时执行上述全部静态/动态意图及父子隔离合同。
- 基线、候选 Release 构建均 0 警告/0 错误；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=204`。开发过程中曾对 `AttackCommand.Results` 错用索引，编译指出其为 IEnumerable 后改回外层 foreach，失败构建未用于测试。

复现原生差分：

```bash
bash tools/run-unattended-test.sh \
  --scenario-id GARDEN-ALLOCATION-NATIVE --character-id IRONCLAD \
  --encounter-id PHANTASMAL_GARDENERS_ELITE --enemy-current-hp 100 \
  --clear-all-powers \
  --monster-move-checks-path coverage/unattended/garden-allocation-native.json \
  --evidence-directory .local/garden/native \
  --timeout-seconds 120 --cleanup-instance-on-exit
```

完整请求、采样、DLL、比较脚本与原始结果保留在本 worktree 的 `.local/garden/`，不进入源码。未测原包完整 300 秒、DOP16、Windows、可见 Steam、完整自动部署或原机器内存压力；未推送、发包或修改已发布版本。
