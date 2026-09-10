# 可恢复紧凑执行原型：R0–R2 结果（2026-09-10）

[性能目录](README.md) · [全部样本与验证记录](simulation-kernel-prototype-20260910.json) · [原调研方案](simulation-redesign-research-20260910.md)

> 2026-09-11 测量更正：本报告的直接调用 fixture 遗漏了正式搜索的 `SimulationNotificationIsolation` 作用域，未启用既有 Ritsu 空能力快速路径。下文 1.53× / −4.54% 保留为历史数据，不能代表完整生产上下文。已修正 fixture 并重新完成完整对照及分段测量，见[下一瓶颈报告](simulation-evaluation-bottleneck-20260911.md)；其受控实验参考值约 1.64× / −7.66%，仍不是正式搜索收益或全面迁移通过。

> 后续实现：本报告的独占稠密候选已由[不可变值页与工作区复用](simulation-candidate-storage-20260911.md)替换。以下旧数据和表示描述保留为当时版本的证据。

## 结果与决定

**真实纵向原型已经实现并通过最小原生对照。** 34 个物理选择分支与旧引擎的完整状态、ContinuationStamp、原 StateKey、完整 Snapshot 属性及有序历史一致。值执行器能停在选择处，从同一前缀继续多个选择，撤销后恢复执行位置；已冻结候选能独立恢复并继续下一张牌。

固定同根、同 34 个叶子的四组交错测量，包含冻结、旧模型物化、完整 Snapshot 和保留排序的原型，线程 CPU 均值约 **1.53×**，累计分配下降 **4.54%**。**未达到建议的 3× / 分配≤20% 门槛，尚未接入正式搜索。** 这既不是 10×，也不是原完整战斗基准的性能结论。

决定是保留可运行、可复验的实验，暂不开始全卡牌/Power/回合的生产迁移。下一项需要解决的是让完整估值直接读取紧凑状态，省掉每叶的旧对象图投影。纯值执行已经很轻；当前证据不能用来否决完整状态与估值一起迁移的方案。

## 实现边界

基于 `9f37ac2`，新增以下所有者：

| 部件 | 实际职责 |
| --- | --- |
| [ReversibleValueState](../../src/Engine/InCombat/Simulation/Compact/ReversibleValueState.cs) | 独占 `long[]`、撤销日志、拥有者及单次 LIFO 检查点；所有状态写入经过同一入口 |
| [ResumableDiscardProgram](../../src/Engine/InCombat/Simulation/Compact/ResumableDiscardProgram.cs) | 资源、五种有序牌堆、卡牌实例序号、显式执行栈、待选择内容及事件游标；执行和状态一起撤销 |
| 冻结候选 | 自有值数组和不可变卡牌定义；不持有 worker、Model、Simulator、Task 或闭包；可以恢复挂起执行或开始下一动作 |
| [CompactDiscardProjection](../../src/Testing/CompactDiscardProjection.cs) | 测试专用的整根能力检查、事件到旧模型的投影；不再次调用 OnPlay、自动出牌、弃牌 Hook 或选择结算 |
| [无人测试](../../src/Testing/UnattendedTestRunner.CompactKernel.cs) | 完整旧引擎 oracle、原生部署、撤销/冻结合同、全属性比较和固定工作量测量 |

投影仍为每个叶子 Fork 一份原始根，从事件恢复历史和领域计数，再调用原有 `CombatBeamSolver.Snapshot`。**这是明确保留并计价的兼容成本。** 投影不反写值工作区；评分快照在 worker 回滚前释放其 Simulator，保留的候选另有独立数组。没有两个同时驱动效果的可变状态图。

原 `MethodMirrorRegistry` 和 OnPlay facade 仅增加只读 `DescribeDispatch` 查询。它使用现有查找缓存，不执行 handler，也不另建推断规则；用于保留旧引擎已经记录、随后被支持层补偿的风险历史。生产路径的调用和分发行为未改。

当前程序是有限效果域：整数组牌定义、无随机抽牌、单张/批量弃牌、弃牌格挡、Sly 自动出牌及嵌套选择。卡牌身份按同根实例编号区分，同 ID 的多张牌不合并；当前不生成/删除实例，因此尚未实现生成代次复用。不同根的候选和检查点显式拒绝。

准入检查排除 Power、其他遗物、未知有关 Hook、第三方 subscriber/附加修饰、临时费用、重放、消耗、球和可能洗牌的根。原版徽章/成就的精确无战斗回调沿用现有镜像的忽略依据；单人缩放恒为 1。未知效果不会通过减少候选或中途换回旧后端来处理。

九条 RNG 和其他不变根数据仍属于已捕获的根。当前程序不执行随机操作，测试只证明投影完整保留它们，**没有证明 RNG 写入/回滚、随机目标、洗牌、生成、伤害或跨回合执行**。正式 Search/Runtime 的启用被双端结构门禁禁止。

## 真实场景与正确性

`COMPACT-KERNEL-NATIVE` 在隔离 headless 中构造 30 张真实卡：手牌包含 Acrobatics、Prepared+（本回合 Sly）、一张 StrikeSilent、两张 DefendSilent，抽牌堆 25 张普通打击/防御；唯一遗物为 ToughBandages，初始能量 6、格挡 0。这里保留模型 ID 作为可复现的精确标识。

支付 1 能量打出 Acrobatics，抽 3 后有 7 个物理弃牌选项。弃掉 Prepared+ 时，遗物先加格挡，再自动打出该牌、抽 2，打开二选八的第二次选择。因此有 **28 个嵌套叶子＋6 个普通叶子＝34**。批量弃牌先移动全部选牌并执行各次弃牌效果，然后才开始 Sly 自动牌。

最终请求 `c5c109a3766249aa8885298e5ebdc60c` Passed，总时长 25.26 秒，120 秒超时未扩大。通过项：

- 全部 34 个叶子逐项比较 `CaptureSimulated` 的完整状态和 ContinuationStamp，包括资源、逐实例牌序、卡牌状态、怪物状态与九条 RNG。
- 比较 `SimulationSnapshot` 全部公开属性，仅排除 Simulator 引用和所有权状态；原 StateKey 的两个值、评分、各项派生特征均相同。
- 历史检查包含事件索引、原卡身份/升级/类型、Started/Finished 共用的 CardPlay 身份、资源支付、抽牌开始/完成配对，以及 method/action 来源帧的顺序和共享关系。
- 外层/内层标记、错误 LIFO、重复消费、跨 worker 标记、非法选择、取消后恢复；最终逐槽回到初始值。
- 挂起候选在原 worker 完全回滚后由 8 个 `Task.Run` 任务独立恢复，结果相同；没有采集实际线程并发峰值，这不代替 DOP8 全搜索合同。
- 保留的完成候选继续手动打出下一张 Prepared+，再次与旧引擎完整状态/估值相同，原候选保持不变。
- Power、可能洗牌、临时费用和外根投影被拒绝。
- 一个嵌套叶子经原生 `TryManualPlay` 和逐实例计划选择器执行，完整 actual/predicted 对照通过。它包含两次出牌、三次弃牌效果；不是 34 次原生部署，也不是整场战斗结论。

开发中保留三次失败：首次准入遗漏原版连击徽章的已知非战斗回调；第二次将“没有星能费用”的原版 `-1` 误拒绝；第三次完整 Snapshot 发现少了两条已补偿风险历史。均已修正并纳入最终校验。第三次 managed Failed 后还出现隔离进程退出时的 Segmentation fault；该退出问题未单独诊断，不计为通过。最终请求成功后已显式停止本任务拥有的实例。

## R0：实际写入与候选存储

- 值工作区为 573 个 `long` 槽，冻结数组有效载荷 **4,584 字节**。这不包括 CLR 对象头、评分快照或共享原始根，不能当作完整战斗状态的总内存。
- 普通叶子从根到完成涉及 55 个不同写槽，嵌套叶子 78 个，为该预留工作区的 **9.60% / 13.61%**。分母包括备用帧和事件空间，不外推整套引擎的写密度。
- 本次完整过程峰值撤销日志 249 条；实际变化/尝试写入、累计日志条数与每叶数据保存在 JSON 中。写入次数包含对同一槽的重复修改，与不同写槽数分别记账。
- 外层抽牌前缀执行一次，Prepared 的抽牌前缀也只执行一次，内核沿选择树继续后缀。兼容投影仍逐叶构造历史，这部分成本没有被隐藏。

## R2：固定全部叶子的成本

每模式预热一次，然后做四组正反交错样本，每样本 16 次完整的 34 叶展开（544 叶）。Linux `CLOCK_THREAD_CPUTIME_ID` 测当前线程实际 CPU；`Stopwatch` 另记墙钟，分配来自 `GC.GetAllocatedBytesForCurrentThread`。没有增量回放、生产 No-GC 或搜索预算调节。

旧引擎得到预先构造的完整选择计划，不向它计入发现待选择前缀的开销，因此这是偏向旧引擎的保守基线。三种模式都保留全部叶子；完整版用相同的 Score、原 StateKey 顺序排序。**未执行真实 Beam 的各项准入、保路和配额政策。**

| 模式 | 每叶线程 CPU 均值 | 每叶分配均值 | 每样本墙钟均值 |
| --- | ---: | ---: | ---: |
| 旧分支 Fork＋完整动作重放＋原 Snapshot＋排序 | 196.41 μs | 61.26 KB | 107.49 ms |
| 紧凑续执行＋冻结＋旧模型投影＋原 Snapshot＋排序 | 128.25 μs | 58.48 KB | 70.95 ms |
| 紧凑执行/枚举＋冻结/保留，省略旧投影/评分/排序 | 5.40 μs | 4.79 KB | 2.95 ms |

完整模式相邻配对线程 CPU 比为 **1.43×–1.71×**；均值比 1.53×。分配配对下降约 3.25%–5.18%，均值下降 4.54%。全部短样本保留，没有删掉较慢基线或更高分配样本。

纯内核 CPU 约为紧凑完整模式的 4.21%，分配约为 8.19%。这是两种模式的实测差额定位：旧投影、完整估值和排序等附加工作仍占主要部分，**不是分别对这三个阶段做出的互斥采样**。不能把纯内核和旧完整链路的比值写成数十倍全局加速。

样本来自一个 headless 进程，范围仅为这个封闭多选择动作。没有 GC 暂停或峰值 RSS 的对照结论，没有改动/缩减原机甲、亡灵基准，也没有运行它们来冒充支持完整效果闭包。

## 下一迁移边界

R0/R1 的这个有限效果域已得到实际证据；R2 已测，但建议门槛未通过。后续应将 `Snapshot` 及状态键的读取边界抽出，使旧对象状态和紧凑值状态能使用同一套完整估值逻辑，保留原 StateKey 数值及排序。不能维护两份独立评分公式，也不能以简化评分代替本次完整对照。

完成无每叶旧图转换的评估后，再测相同全部叶子；之后才决定扩展随机状态、Power/遗物生命周期、生成代次、完整回合及原正常基准。这是后续工程范围，本文不记作已完成。

## 复验

先构建隔离产物，并在该目录放入当前 manifest（不是发布包）：

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false -o .local/simulation-kernel-20260910/artifact --no-restore
cp CombatSolver.json THIRD_PARTY_NOTICES.md .local/simulation-kernel-20260910/artifact/
./tools/run-unattended-test.sh --scenario-id COMPACT-KERNEL-NATIVE --character-id SILENT --enemy-current-hp 256 --headless-instance simulation-kernel-20260910 --combat-solver-build-dir .local/simulation-kernel-20260910/artifact --evidence-directory .local/simulation-kernel-20260910/native-v4 --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --headless-instance simulation-kernel-20260910 --stop-instance
```

最终行为源码 Release 构建 0 警告/错误；Linux 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=85`。PowerShell 对应规则已同步，未在本机执行。原始请求、结果、性能 JSON 位于上述 evidence 目录；可共享的全部样本、失败原因及摘要已提交到本报告的 JSON。没有发布、推送、安装到交互式游戏或启动可见 Steam。
