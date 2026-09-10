# 紧凑执行扩展：洗牌、战略选择与不变特征复用

2026-09-11，基线 `414a92f`，游戏 `0.111.0`。本批为 Testing 下的能力扩展；生产 Beam、搜索政策、预算、版本和安装均未切换。

## 结果

扩展后的 24 分支原生场景，在每批重新准入、建立值工作区和读取缓存的口径下，线程 CPU **3.31×**、分配 **−80.65%**。只计同根展开时为 **3.62×**、分配 **−85.33%**。这是原生回归机制的有界缩减场景，不能外推为完整战斗搜索倍率。

| 同一捕获根、24 个分支 | 线程 CPU / 叶 | 分配 / 叶 |
|---|---:|---:|
| 旧 Fork + 实际动作重放 + 完整 Snapshot | 67.31 µs | 29,858.79 B |
| 紧凑展开 + 直接完整读取，关闭新特征缓存 | 20.54 µs | 4,536.30 B |
| 紧凑展开 + 直接完整读取，开启缓存 | 18.57 µs | 4,380.76 B |
| 上项再计入每批准入与新建工作区 | 20.32 µs | 5,777.42 B |

缓存本身的 CPU 降幅为 **9.56%**、分配降幅为 **3.43%**；大部分收益仍来自共享执行前缀和删除逐叶旧图构造。最后一行达到这个场景的 3× CPU／分配不超过 20% 联合实验门槛。此前 34 叶的 2.87× 是不同输入，不能与这次 3.31× 相减来归因。

最终四组线程 CPU 样本（µs/叶）：

| 模式 | 1 | 2 | 3 | 4 |
|---|---:|---:|---:|---:|
| 旧重放 | 69.1443 | 67.1127 | 66.5482 | 66.4475 |
| 直接读取，无新缓存 | 20.6583 | 20.2837 | 20.6184 | 20.5829 |
| 直接读取，有缓存 | 18.4466 | 18.5994 | 18.8925 | 18.3536 |
| 有缓存，含每批准入 | 20.4446 | 20.5235 | 19.9990 | 20.3114 |

每组每模式 32 次 × 24 分支，模式顺序正反交错。旧路径已获预计算选择令牌，无须计入发现选择的工作；基线每叶提前释放 Simulator，未要求它保留整批旧对象图。新路径包括执行、冻结、完整估值和快照排序；不包含生产 Beam 的候选淘汰、后续搜索调度、峰值存活量或 GC 暂停测量。计时保持正式通知隔离，不挂 Profiler，也不启用增量验证。

所有模式都从同一已捕获的旧根开始，因此不含通用 CombatRootSnapshot 捕获。新进程第一次准入另观测为 **44.86 ms / 1,375,008 B**，包含首次类型元数据和相关惰性初始化，未与旧引擎冷启动作对照；不能用热测量承诺首搜延迟。第二回合新根准入为 **84.1 µs / 21,184 B**。增加按 CLR 类型共享的只读 Hook 审计后，避免每张同类型卡牌重复反射；每根仍重新验证 Power、遗物、卡牌实例和 subscriber 状态。

## 扩大了什么

- 原来拒绝洗牌的执行器，现在拥有 Shuffle 的 counter 和四个 64 位随机状态；与资源、牌堆、指令位置、选择列表和事件游标使用同一撤销工作区。xoshiro256** 的整数采样与游戏的 53 位 double 缩放保持一致。
- 洗牌排序使用根上捕获的逐实例比较关系。相同卡名的比较相等关系仍交给原 .NET 排序过程，不自行增加实例 ID 作为排序条件。17 张含重复卡的洗牌与原生有序牌堆严格一致。
- 战略的洗牌后取牌有独立暂停指令；恢复继续尚未完成的抽牌。两个遗物的弃牌／洗牌格挡触发按根监听顺序执行，选择取牌不误记为普通抽牌或弃牌。
- 支持防御与后空翻的格挡，以及后空翻抽牌；当前可执行类型为 `ACROBATICS`、`PREPARED`、`BACKFLIP`、`DEFEND_SILENT`。`STRIKE_SILENT` 可存在于牌堆，但攻击执行仍明确拒绝。洗牌闭包最多一个 Sly 实例；事件容量有界，越界抛错，不交付部分候选。
- 原键和下一次投影洗牌都读取分支 Shuffle 状态。其他八条 RNG 只在准入证明它们不变时借用根。
- 可选读取缓存复用敌人摘要、威胁焦点、卡牌基础估值及策略上下文。计算仍由原共用方法完成；准入要求敌人／AI、Power 数量、存活牌集合及其元数据不变。每个稳定根新建缓存，回合换根不沿用。24 个分支中敌人、焦点、策略上下文各构造一次，卡牌值构造 24 项；缓存开关的全部 Snapshot 属性与原键相同。

Power 的触发已经执行，但 Power 数量的增加、减少、移除、伤害、死亡及紧凑回合生命周期尚未迁移。敌方 Strength 只作为本执行闭包内不变的只读元数据准入。旧模型投影仅解码已提交事件和值，用于原生和旧引擎对照，不再次运行效果。

## 原生输入与验证

本次沿用既有战略／准备充足选择顺序回归的机制来源（测试矩阵中的 `STRATAGEM-PREPARED-CHOICE-ORDER-FINAL-0160`、`STRATAGEM-SHUFFLE-CHOICE-365`，及[已有洗牌输入](../../coverage/unattended/stratagem-shuffle-choice-365-cards.json)）。新测试是明确构造的原生缩减样例，没有把完整问题包或原 30 张机甲基准改小后称作整场迁移。

固定 `COMBATSOLVER` seed，单人 Silent／FuzzyWurmCrawler，24 张牌：手牌为杂技、升级准备充足（本回合 Sly）、防御、打击；抽牌堆为防御、打击、防御；弃牌堆为两张后空翻，随后防御／打击交替 15 张。战略为 2 层，遗物为 `TOUGH_BANDAGES`、`THE_ABACUS`。完整输入也记录在[结构化证据](simulation-expanded-chain-20260911.json)；可重跑代码为 [CompactShufflePower](../../src/Testing/UnattendedTestRunner.CompactShufflePower.cs)。

杂技先抽完三张牌，再弃置 Sly 准备充足；自动出牌的抽牌触发 17 张洗牌与战略选牌，之后继续其抽弃流程。24 个选牌样本逐一比较完整旧状态、ContinuationStamp、九流五字段 RNG、历史及来源树、原状态键和全部 Snapshot 属性，并验证卡牌元数据不变、完整撤销及 8 worker 从暂停候选独立恢复。样本不是所有组合的穷举。

原生第 1 回合部署其中一条链，再由冻结候选续接后空翻。随后**现有生产引擎**从这个完成投影推进回合，与原生完整下一回合状态对账；第 2 回合捕获新根，再由紧凑执行器执行防御。这里验证的是紧凑动作的连续两回合使用和旧回合边界兼容，**不是紧凑执行器已经处理回合推进**。

527 个值槽中，24 个 Power 选择之后的后缀实际写过 53–58 个不同槽；这是 journal 的后缀写入计数，不含此前已经完成的洗牌前缀，也不是全战斗写入密度或 dirty page 比例。第 2 回合的后缀为零，因为防御在建立该选择后缀检查点之前已完成。

验证记录：

- 最终扩展 v4：`caf3dd7642e04d678278d1d30bdc0e5d` Passed，28.20 秒，T1→T2；额外验证缓存类型审计后，未支持的 Power 与临时费用仍在执行前拒绝。
- 扩展 v3：`7d576b58d2244b72a5daf177d13de4bf` Passed，27.96 秒；同一完整动作语义及缓存开关验证，随后只优化准入的类型元数据复用和加入“含准入”的测量模式。
- 原 34 叶哨兵：`ce69f05dcfd04e2fb4be938848967191` Passed，4.83 秒，复用 v3 进程；根／外根／暂停读取、完整状态、全部 Snapshot 属性、原排序、冷暖 solver、下一动作、8 worker 和原生嵌套链通过。后续 v4 没有更改执行、读取、评分或排序公式，准入值拒绝由 v4 的目标场景覆盖，未重复该已通过哨兵。
- 初次 v1 失败在测试 RNG 向量的 switch 表达式优先级，补括号修复；v2 两回合断言已走完，但报告目录未创建导致请求 Failed。均保留为失败证据，不算最终通过。
- 最终 Release 构建 0 警告／错误（9.18 秒）；Linux 边界门禁 `REFACTOR_BOUNDARIES_OK search_files=87`。PowerShell 规则同步，未运行 PowerShell。

L0 完成：233 个相对链接、最终 16 个交错样本、v3 的 12 个缓存对照样本与双端五条新增规则一致性；`git diff --check` 通过。测试实例已停止。

未运行完整战斗、生产 Beam、可见 Steam、增量搜索或生产并行性能验证；没有发包、安装、版本变更和远端操作。

## 复跑

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false -o .local/compact-expand/artifact
# isolated artifact 同时放入 CombatSolver.json 和 THIRD_PARTY_NOTICES.md
env DOTNET_TieredCompilation=0 ./tools/run-unattended-test.sh \
  --scenario-id COMPACT-SHUFFLE-POWER-NATIVE --character-id SILENT \
  --enemy-current-hp 256 --headless-instance compact-expand \
  --combat-solver-build-dir .local/compact-expand/artifact \
  --evidence-directory .local/compact-expand/native --timeout-seconds 120
# 同进程运行已有 COMPACT-KERNEL-NATIVE 哨兵；它不验证完整紧凑回合推进。
./tools/run-unattended-test.sh --headless-instance compact-expand --stop-instance
./tools/verify-refactor-boundaries.sh
```
