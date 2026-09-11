# 值 RNG 计量归因与紧凑生成池执行（2026-09-11）

上一批的值生成选择失败断言把“分配 160 字节”和“RNG 消耗改变”写进同一条消息，无法区分。独立纯 .NET 计量程序直接引用真实 `ValueRng.cs`，在 5.5M 次以上调用、全部模式下测得 0 字节；160 字节因此归因为 Godot 游戏进程内测试线程的一次性宿主开销，既不是 `ValueRng` 原语，也不是计量线程上的 JIT 分层。审计尾部的分配检查随后收紧为三阶段：100 次 warmup 调用；与正式测量完全同形（同一个 `RunValueGenerationSelectionBlocks` helper、同样 5 块、每块 5000 次）的稳定化阶段；最后是正式 steady-state 5 块 × 5000 次。5 个正式块必须全部精确 0 字节，没有容差阈值，也不再使用“至少一块为 0 字节”（`min==0`）的放宽。稳定化只把一次性宿主／JIT 形状开销挤出正式区间，它的分配记录不被读取、不参与分配断言（RNG 消耗只并入 warmup 合并断言），因此永远不是通过证据。warmup／稳定化与正式段的 RNG 次数分别精确断言。本批同时落地紧凑池生成值层（`CardGenerationPools` 与 `GenerateFromPool`），原生差分与搜索等价回归通过；生产准入未扩大，仍为 53 种卡牌类型，完整亡灵根仍显式拒绝。

## 所有权与兼容

- `CardGenerationPools` 冻结调用方候选池：构造时对每个池做防御性复制，之后不再读取原数组，因此后续改动准入数组不会影响已捕获的根。五字段生成 RNG 使用 `state.Allocate(5)` 落在 `ReversibleValueState`，撤销、冻结与恢复天然覆盖该流；`SelectOne` 通过 `ValueRng.TakeDistinctIndices` 执行与原生一致的全池洗牌，选区只写回该五字段槽。
- 每个工作区独占 `_generationScratch`（长度取最长池）；共享布局本身不持有可变状态，冻结工作区只读候选与模板。选中项在 scratch 复用前被复制出来，越界或容量不足由洗牌原语拒绝，该层不兜底。
- 虚无由冻结模板定义承担，沿用 BladeOfInk 最终模板先例。已准入的生成钩子在本闭包内没有观察者，因此不把虚无当作逐次选择后的可变字段。
- `GenerateFromPool` 先整批选择（每张一次全池洗牌），再逐张入堆：`Ending` 时进入 `Unplaced`；手牌达到上限 10 时溢出到弃牌堆，落点与模板路径一样追加在目标堆末尾；不触发 `AfterCardDrawn`；同一生成内及跨生成允许重复候选。
- 生产准入未改变：`CompactCardProgramCompiler` 未触碰，精确类型闭包仍为 53，完整亡灵根仍被显式拒绝（审计 `FullRootExplicitlyRejected` 通过）。`ResumableDiscardProgram` 通过可选 `generationPools`／`cardGenerationRng` 构造参数接收该值层，未准入根不创建它；`CardInstructionKind.GenerateFromPool`、`CardInstruction.GenerationPool` 和 `CardEffectProgram.RequiresGenerationRng` 只描述定义与 RNG 需求，不扩大准入。

## 验证

直接结果见[结构化证据](simulation-generation-metering-20260911.json)。失败记录全部保留、不写成“从未失败”：上一批的原始基线 runId `3c479d36a88f4889b9d1f78f9d0c9251`（合并断言打印 160 字节）仍保留在[测试矩阵](../TEST_MATRIX.md) 的“值 RNG 的完整池选取”段；本批先尝试过的单块稳定化形状在 Godot 中同样失败，runId `58268385f95c4d37a3383bfa46768b76`，错误为 `Value generation selection allocated in the steady state: 160/0/0/0/0 bytes; stabilization=0 bytes.`，原始日志与结果保存在 `.local/metering-strength-20260911/audit-run1-failed.log` 与 `.local/metering-strength-20260911/audit/result-run1-failed.json`。

计量归因：独立程序在 `exact`／`percall`／`noinline`／`inline`／`osr` 各模式下 6 个连续 5000 次块、逐调用二分和 JIT／GC 环境矩阵（`TieredCompilation`／`TieredPGO`／`ReadyToRun`／`TC_OnStackReplacement`／`TC_QuickJitForLoops`／`gcServer`／`gcConcurrent`）全部测得 0 字节，5000 次逐调用中 0 次非零，计数器增量 385000 与 5000×(78−1) 完全一致。若选择真会分配，稳态每个块都会出现；只有首块可能是宿主进程向该线程的一次性 JIT 桩／分层开销。原来的“至少一块精确为 0”（`min==0`）只能把原语行为与宿主噪声分开，不能证明正式区间完全无分配；本批改用与正式区间完全同形的稳定化阶段吸收一次性开销，正式 5 块因此必须全部为 0。对应的 scratch 源计量在 `.local/metering-strength-20260911/ValueRngMetering/`，同样直接引用真实 `ValueRng.cs`、不含游戏宿主，以 `oneblock`（单块稳定化）与 `sameshape`（与断言批次完全同形）两种模式各 10 轮、池 78／50 复核：两种模式的 5 个正式块全部为 0 字节并输出 `METERING_STRENGTH_OK`，计数器增量精确等于 `5×5000×(pool−1)`（1925000／1225000）。纯进程计量说明“单块稳定化”本身不会掩盖原语分配，但 Godot 宿主的一次性开销只有在同形稳定化之后才落不到正式块上。

`COMPACT-GENERATION-CLOSURE-AUDIT`（严格形状）：单块稳定化尝试 runId `58268385f95c4d37a3383bfa46768b76` Failed（首个正式块 160 字节、稳定化 0 字节）；改为同形稳定化后 runId `e039ec137c8b4f768cf46d7e8fbdfdd9` Passed（24.04 秒），最终记录运行 runId `537d20c96a944b48b505b90f0bf7fd34` Passed（24.15 秒）。warmup 100 次与稳定化 5×5000 次合并精确断言 `(100+5×5000)×(pool−1)`，正式段单独精确断言 `5×5000×(pool−1)`，正式 5 块全部精确 0 字节；五字段比较与边界比较未删除，也没有新增容差。证据目录 `.local/metering-strength-20260911/`（`audit-run1-failed.log`、`audit/result-run1-failed.json`、`audit-run2-passed-shape-stabilization.{log,json}`、`audit-run3-final.log`、`audit/result.json`、`artifact/CombatSolver.dll`）。

`COMPACT-GENERATION-CLOSURE-AUDIT`（`min==0` 旧形状，已被上文的严格形状取代）：artifact-v3 连续两次 Passed，runId `1c9d47f348ea4473ae29037b2601913b`（24026.2432 ms，含冷启动）与 runId `3ca8ef6137284d3dbffea37dce5100dc`（3439.1954 ms，复用进程）；改用新代码后 artifact-v4 再次 Passed，runId `a83926d065f247dc862bf1c1ea8c1304`（3488.4042 ms）。证据目录 `.local/value-generation-selection-20260911/{audit-v3,audit-v3-run2,audit-v4}`。原始 38 牌、19 件遗物注入、两瓶药输入与全部对照协议不变。

`COMPACT-CALL-OF-THE-VOID-GENERATION`：同一完整亡灵输入，注入 4 层 CallOfTheVoid，三个批次；对照紧凑值层、旧 `TurnStartPowerSupport` 分支与原生 `BeforeHandDraw`。冻结池必须为 78 个候选，否则响亮失败；每批的有序生成 ID 三条路径完全一致；五个 RNG 字段在紧凑槽／旧分支／实机之间对齐；计数增量为 308／616／924（每批 4×77）；12 张生成牌全部带虚无；第二批满手溢出两张到弃牌堆（落点 `[Hand,Hand,Discard,Discard]`）；逐实例 ID 严格递增，`BORROWED_TIME` 跨批次重复；撤销恢复全部五个 RNG 槽、实例与牌堆；确定性重放复现最终冻结；实机原生生成后冻结根重放仍然一致。首跑即通过：runId `b715d6a7100c4164b27c8c5bf7932152`（26574.8232 ms），证据 `.local/value-generation-selection-20260911/cotv-v1/`（`result.json` + `compact-call-of-the-void-generation.json`）。

`COMPACT-PAGESTORM-SEARCH` 在最终 artifact-v4 上 Passed：runId `68c51d68693b4e79a1ea03da531e4f2e`（9203.6327 ms），证据 `.local/value-generation-selection-20260911/pagestorm-search-v4/`，确认值层改动未破坏搜索等价。

纯单元检查 `tools/CompactCreatureChecks/GenerationPoolChecks.cs` 以 oracle 驱动：2 批／8 抽／池 3 且保证出现重复，RNG 计数增量等于抽取数×(池−1)，虚无、满手溢出顺序、空池零 RNG 空操作、单候选零 RNG 步、rollback `ContentEquals`、8 个并行冻结工作区和 5 个边界拒绝。`CompactCreatureChecks` 22/22 OK。

本次未运行整场搜索、完整部署、Windows、正常 NoGC 性能基准或 2-3x 加速验收；严格形状改动只改测试计量与文档，不改变生产准入，Release 构建 0 警告／0 错误、Linux 结构门禁 Passed（96 个 Search 文件），PowerShell 对应规则未运行。构建与门禁结果记录在结构化证据中。

生产编译器与回合接线（CallOfTheVoid `BeforeHandDraw`）未完成；106 个未准入生成类型、无色药水执行、19 件原版遗物与 0.36.1 `RelicCounterPolicy` 适配、AEONGLASS AI／意图／召唤，以及最终 `VeryHigh`／DOP8／NoGC-16GB 交错 A/B 与任何 2-3x 加速结论，全部仍未完成。本批只收紧值 RNG 审计计量并保全失败证据，不代表极高配置完整搜索性能重构已经完成。
