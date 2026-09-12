# 紧凑状态直接读取与复杂状态补测（2026-09-11）

[性能目录](README.md) · [方案账本](simulation-strategy-ledger-20260911.md) · [上一批候选存储](simulation-candidate-storage-20260911.md)

本轮在既有 34 叶抽牌／弃牌闭包内，完成从紧凑值直接进入完整 Snapshot 的读取边界。每叶不再 Fork 旧 Simulator 或重建事件对象图；每个根仍持有旧引擎捕获的不可变元数据。生产搜索后端、Beam、排序政策、预算、默认并行度和 GC 政策均未切换。这里是封闭实验的性能与语义结论。

## 改动与所有权

- `Search/CompletedStateReadView` 声明同步、已完成候选的读取合同。直接提供能量、格挡、四个有序牌堆、历史条数和历史计数；根提供已证明不变的生物、阵容、卡牌元数据、Power、遗物／药水、九条 RNG 及其他生命周期状态。读取器属于单个 lane，不得保留到候选。
- 旧 Snapshot 和新入口都调用同一 `SnapshotCore`。评分公式、手牌可达价值、卡牌合法性、投影洗牌算法、完整原状态键及排序不另写一套。牌堆键缓存未命中也调用相同值序列编码；旧缓存命中路径保留。
- `Testing/CompactDiscardReadView` 从紧凑牌堆索引借用根卡牌元数据，从已提交事件读取历史变化。准入仍限定 Acrobatics／Prepared 执行与 StrikeSilent／DefendSilent 元数据，没有 Power、额外 Hook、随机结算、附加卡牌修饰或其他遗物。补充拒绝融化的 ToughBandages、保留／回合末卡牌标志和未表达的伤害预测 Hook。它不是通用战斗读取器。
- 原状态键区分字典缺席与显式零值。计数的初值在一次独立的根 Fork 上调用原历史 getter 取得；各叶仅提供确实更新过的值，由原编码器替换对应 owner 的一项，保留其他生物和原键输入顺序。技能既增加计数，也标记本回合技能集合，二者都进入原键。
- 风险分类和去重／排序继续由 `PredictionCoverage` 负责。封闭程序只有两个可能的 OnPlay 风险来源，根级准备至多四份不可变摘要；每叶从事件带选择摘要，并保留重复风险条目对历史总数的影响。没有吞掉已补偿 gap。
- 新入口立即释放结果中的借用 Simulator。冻结候选继续只持私有页表和不可变值页，下一动作从值状态恢复；读取过程没有把值回写旧模型。原投影保留为完整状态、历史与原生动作的对照工具。

## 补测的复杂状态

新增可选 `COMBATSOLVER_WRITE_DENSITY=1` 观察器，仅用于已有测试夹具。采样在稳定动作／生命周期边界，实际调用旧权威语义并与原生完整 `MoveStateSnapshot`／ContinuationStamp 对账；九条 RNG 的 counter 和四个内部状态值另作展开。

| 真实夹具边界 | 变化／并集观测字段 | 含义 |
| --- | ---: | --- |
| 两个 Power 获得前的动作 | 12/94、12/98 | 能量、格挡／伤害、牌堆与逐卡状态变化 |
| 混合 Power 获得 | 12/105 | 多类型／多实例、状态与卡牌派生值变化 |
| 已有 Power 的两次动作 | 12/103、12/99 | 经过原生 Hook 的动作后状态 |
| Power 移除后重新获得 | 2/97 | 数量／完整生命周期状态变化 |
| 召唤后到下一回合 | 52/117 | 阵容、怪物状态、牌堆、历史和 RNG 一起改变 |
| 击杀召唤物后到下一回合 | 24/90 | 死亡、跨回合生命周期与 RNG 改变 |

Power 请求 `c6987417df75415fa60154e7f089051a` Passed（4.08 秒）；召唤／死亡请求 `a48e596bf32c495db7516f36c91016f0` Passed（4.29 秒，T1→T3）。后者两段均改变 MonsterAi 和 Niche RNG 各五个值；其他七条保持对账。两请求复用同一隔离 headless 进程，使用本轮 v2 构建，没有正式搜索或增量验证。

这些数字是 **净观测字段变化**，不是实际写入次数、首写率、dirty page 比例或字节密度。字典项增删各计一项，packed 字符串整体计一个字段；其中还存在重复表达同一状态的观测字段。不能将 52/117 当成紧凑槽写入率，也不能以此把之前的合成密集探针包装成真实新后端基准。它提供了真实复杂消费者的边界证据，支持先完成这些效果的字段所有权与写入口，再决定稀疏／密集表示。当前尚未实现它们的紧凑执行和撤销。

## 同进程性能对照

采用 .NET 9、关闭分层编译、正式 `SimulationNotificationIsolation`，每批同样 34 个物理叶子，全部冻结并按原比较器排序。每组四个样本，正反交错旧图投影／直接读取；区分预热 solver 与每批新建 solver。读取器初始化和一次历史元数据 Fork **每批都计入**，不是从结果中扣掉。初始根捕获与准入是两模式共用的外部输入，不在每叶成本内。

第一份通过的 v2 实现仍逐叶整理风险摘要：预热模式旧图投影 CPU 41.18–42.12 µs／叶、19,758 B／叶，直接读取 23.09–23.48 µs／叶、5,649 B／叶。分配诊断显示直接 Snapshot 比旧 Snapshot 多约 919 B／叶，因此将已准入风险组合移到根级准备。保留该测量作为 v3 的修改依据，不与另一个进程的 CPU 均值直接归因比较。

v3 将摘要移到根级后，无细分探针的完整直接读取为 19.66–21.46 µs／叶、4,659.5 B／叶。同期旧动作重放 56.03–61.19 µs／叶；具体样本以 JSON 为准。复查指出新 `IReadOnlyList` 的 foreach 相比原 `SimCardPile` 值枚举器使旧路径增加 280 B／叶，因此最终改为共用按索引遍历，保留原迭代顺序。

| 模式（完整批次，无细分探针） | CPU 均值 µs／叶 | 四样本范围 | 分配 B／叶 |
| --- | ---: | ---: | ---: |
| 旧动作重放＋完整 Snapshot | 56.942 | 55.062–59.002 | 25047.7 |
| 紧凑执行＋旧图投影＋完整 Snapshot | 37.250 | 35.621–39.803 | 19357.6 |
| 紧凑执行＋直接完整读取 | 19.845 | 19.746–19.941 | 4139.2 |
| 纯值执行＋冻结，不估值 | 1.151 | 0.988–1.593 | 1060.7 |

直接读取相对旧动作重放约 **2.87×**，分配为其 **16.53%**（下降 83.47%）。相对紧凑执行＋旧图投影，CPU 均值减少 46.72%，分配减少 78.62%。CPU 的 3× 目标仍未达到，分配 ≤20% 目标在此闭包通过；R2 联合门槛未通过，不能据此启用生产后端，更没有 10× 结论。纯内核第三样本有长尾，原数保留。

细分探针的预热 solver：旧图投影 41.415 µs／叶、19,357.9 B；直接读取 22.068 µs、4,139.0 B。每批新建 solver：旧图投影 43.737 µs、20,217.4 B；直接读取 23.229 µs、4,892.9 B。24 个唯一威胁键和同批重复状态都保留。完整批次与带细分探针的数值不混用。

旧路径 v2/v3 的额外 280 B／叶已消除；最终旧动作重放为 25,047.7 B／叶，相比此前 e47cdd0 同输入记录的 25,167.7 B 少 120 B。不同进程 CPU 受漂移影响，该对照不用于声明生产提速。

细分探针包含计时开销，内部 Snapshot 子阶段彼此嵌套，不能相加。另有不启用细分探针的完整批次，对照实际旧动作重放、旧投影、直接读取和纯值内核。完整批次仍是这个封闭场景，不代表整场 Beam、DOP8、RSS 或 GC 暂停。

## 验证与开发失败

- v1 `f35a810d682d4ac49bc29ba66c288c55` 在第一份完整 StateKey 对账失败：遗漏 `_skillsPlayedThisTurn` 集合。补入原 Power 生命周期编码的 owner 集合后，v2 `7b18485c879541f7b699cf4c002025c8` Passed（29.48 秒），所有 34 叶完整键、Snapshot 全属性、根／卡牌不变性、旧完整状态／有序来源历史和原生嵌套链通过。没有为通过测试改变状态键或忽略该字段。
- 最初一次编译因新文件缺少 `Engine.Common` 引用失败，补齐后 v1/v2 Release 均为 0 警告／错误。
- 最终 v4 `0c14283a61414f3eacb81ebfe7f7f277` Passed（29.60 秒）：根与 34 叶全属性／原键／保留排序，根和每张卡元数据不变，外根与挂起读取拒绝，直接下一动作续接；原完整状态／ContinuationStamp／来源历史、8 worker 候选恢复与原生嵌套链继续通过。直接读取的计时仍为单 lane，8 worker 项验证的是存储恢复，不是新读取器的并发性能。
- 最终 Release 0 警告／错误（8.49 秒）；Linux 门禁 `REFACTOR_BOUNDARIES_OK search_files=87`。初次门禁指出新 ReadView partial 未列入职责白名单，已同步双端清单。PowerShell 未执行。测试实例已停止。L0 校验通过：228 个相对文档链接、四版证据、最终 32 个样本、八个真实生命周期阶段及双端七项新增边界规则；`git diff --check` 通过。

Linux／PowerShell 结构门禁同时维护：禁止生产调用新读入口，禁止紧凑内核引用模型，禁止读取器回放动作、物化旧分支或写入卡牌／值状态，并要求旧／新读取共用 SnapshotCore。Search 只依赖自身读取合同，不反向引用 Testing 的实验实现。

## 复跑

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false -o .local/compact-readview/artifact
# isolated artifact 同时放入 CombatSolver.json 和 THIRD_PARTY_NOTICES.md
env DOTNET_TieredCompilation=0 COMBATSOLVER_COMPACT_PROFILE_ITERATIONS=128 COMBATSOLVER_WRITE_DENSITY=1 \
  ./tools/run-unattended-test.sh --scenario-id COMPACT-KERNEL-NATIVE \
  --character-id SILENT --enemy-current-hp 256 --headless-instance compact-readview \
  --combat-solver-build-dir .local/compact-readview/artifact \
  --evidence-directory .local/compact-readview/native --timeout-seconds 120
# 同进程测 Power：替换场景为 MIXED-POWER-ACQUISITION-ORDER，角色为 IRONCLAD，
# 加 --clear-all-powers --clear-player-piles --enemy-current-hp 500，使用独立证据目录。
# 同进程测召唤／死亡：SUMMON-DEATH-POWER-ORDER，IRONCLAD，
# --encounter-id FABRICATOR_NORMAL --enemy-current-hp 500，使用独立证据目录。
./tools/run-unattended-test.sh --headless-instance compact-readview --stop-instance
./tools/verify-refactor-boundaries.sh
```

全部样本、真实变化字段及版本／请求来源保存在[结构化证据](simulation-read-view-20260911.json)。未启动可见 Steam，没有发包、安装、版本提升或远端操作。
