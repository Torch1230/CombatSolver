# 精简性能 fork：选择范围、验证与研究

2026-09-12，工作重点为 `perf/surgical-fixes-20260912`，基于本轮 `git fetch upstream` 得到的 `eff8cf4`（0.36.4）。旧 `perf/simulation-profile-20260910` 在 `bd1e9ee` 写入“暂时不开发”；源码、实验和证据留在旧分支。新分支没有 Compact 目录、第二套战斗执行器或对应准入政策。

## 旧分支做到哪里

暂停前的 `ceafbc6` 相对最新上游有 82 个独有提交，其中 79 个非合并提交；差异覆盖 374 个文件、251,549 行新增与 3,087 行删除。生产 `src`（不含 Testing）为 90 个文件、7,671 行新增与 1,363 行删除；测试为 66 个文件、10,312 行新增与 27 行删除。不能把全部 25 万行当成引擎代码。

| 部分 | 实际进度 | 此次处理 |
|---|---|---|
| 空动态变量克隆、监听缓存、历史脱图、回合开始避免无效 COW 等早期性能优化 | 已在最新上游中存在 | 随上游保留，不重复移植、不重复计算收益 |
| `1358179` / `1787f9d` 回合阶段提取与前缀复用 | 历史机甲分配 −14.62%、亡灵 −1.52%，但采样 RSS 均值反而增加；涉及多处执行、暂停和所有权边界 | 不进本批；虽不属于 Compact，仍超出本次局部修改范围 |
| `9f37ac2` 之后 Compact／读视图／值状态／卡牌与回合迁移 | 在部分闭合场景建立原生差分和固定搜索证据；历史特定机甲普通 GC 对照约 2.20×、分配 −49.6% | 整体暂停；不把特例数据当作新分支收益或全卡支持率 |
| 数据布局探针与候选 | 辅助类型计数表、列表存储包装有局部节省；枚举优化完整场景收益不足，已撤回 | 补齐列表合同并实测；整体收益不足，已撤回 |
| 独立正确性与测试断言修复 | 部分已由上游另行修复，部分仍可重现 | 按下面的清单选择移植 |

历史数字来自旧分支对应报告，不是本轮复测。Compact 的功能覆盖与巨幅局部加速不构成继续整套移植的理由；恢复旧分支需要用户新指令。

## 此次保留的手术切口

| 来源 | 移植内容 | 范围与理由 |
|---|---|---|
| `ef1b96f` | `DodgeAndRoll` 传递实际 GainBlock 修正返回值 | 避免用封顶后的格挡净增量低估下回合效果 |
| `12f9c70` 中的 3 处普通模型修复 | 普通 Power 保留 Target=null，重获时保持模型原目标，显式定向入口不变 | 只抽取模型语义，未移植该提交的 Compact 内容 |
| `672e25e` 的封顶规则，适配上游已有入口 | 临时力量的数量回调比较修正请求偏移与结果计数 | 上游已修正首次回调顺序；只补请求量与净增量不等的边界，不覆盖回旧施加实现 |
| `2d3c308` | 三种原生持续减益的跳过首次扣减标记由 Power 独占，进入状态键与续用文本 | 防止未来结算不同的状态错误合并；不改变剪枝或预算政策 |
| `4b350d6` 中的测试修复 | 四档预设断言更新为当前单一 Profile | 上游测试原先同时要求旧 Short/Deep 两个不同值；只修断言，生产预设不变 |


`0f68dca` 的死亡清理／分支存活判断已由上游保留，当前只复用原生回归验证，没有再加生产代码。后端适配接口、Compact 测试基础设施、庞大 JSON 证据、测试账本和无关工具均未整批带入。

完整角色生成池缓存 `acd412b` 也没有整批带入：它可独立于 Compact，但涉及根资格捕获、原生／自定义池旁路和生成输入，需要先量化完整搜索收益，列为后续独立候选。

其余旧分支的宠物／生成／历史窗口／Hook 时序改动需分别审计组合行为，暂未移植。小 diff 不自动等于小语义风险；本分支不会因旧报告里的待办继续补齐战斗规则。

## 本轮验证

六组原生合同覆盖封顶、小数、能力获得顺序、施加者／Target、Artifact、首次与叠加、死亡、Fork 隔离、持续时间与完整续用／状态键。上游失败与本分支通过均取自本轮运行，死亡回归在上游也通过。

列表合同直接编译生产文件，与独立 `List<int>` 深复制模型进行 10,000 次随机操作、64 个分支对照；另覆盖共享前枚举器、独占修改的枚举失效、全部操作和 8 个独占 worker。生产版与候选版均通过。

最终生产差异仅 4 个文件、45 行新增与 13 行删除。Release 构建 0 警告／0 错误；Linux 结构门禁及 CoverageCatalog `--verify-state-fields --verify-state-writes` 通过。覆盖目录验证只作为分类／证据门禁，不冒充本轮重新运行全部旧 fixture。PowerShell 与可见 Steam 未运行，因此没有 FPS、渲染卡顿或全平台结论。原生合同是定向生命周期验证，不是全卡牌或全游戏状态的穷举证明。

## 性能与内存

**本次没有建立可承诺的新增整体加速或内存下降。** 新分支保留正确性修复；列表包装实验已撤回生产，只保留独立探针。上游本来就有的优化不能再算成新 fork 的增量收益。

对照 B 为“相同 bug 修复 + 原列表”，C 只改变列表包装；预先固定 B-C-C-B 顺序，每个新进程先预热两场，再各测一次。两种实现各两次正式样本。原输入、DOP8、NoGC 16 GB 相同；机甲为正常 VeryHigh，亡灵 VeryHigh 预热超出 120 秒后改为 **Low + FixedBudget 压力对照**，不能外推为亡灵极高配置结论。

| 指标（候选相对修复基线） | 机甲 VeryHigh | 亡灵 Low 固定预算 |
|---|---:|---:|
| 平均搜索耗时变化 | +0.70% | −1.31% |
| 首尾基线自身耗时漂移 | −0.85% | +4.02% |
| 平均累计 worker 分配变化 | −0.0032%（约 0.19 MiB） | −0.0069%（约 2.18 MiB） |
| 平均采样请求峰值 RSS 变化 | +0.22% | −3.94% |
| 平均搜索结束工作集变化 | +0.54% | −3.99% |

亡灵两对 RSS 变化分别约 −0.84%／−6.85%，GC／驻留堆条件造成明显波动，不能把均值当作确定的列表节省。局部分配确实更少，但两场完整累计分配改善都不到 0.01%；耗时差异小于或接近已观测漂移。因此不为这些数字增加生产实现分叉，也不声称完全没有任何硬件或场景下的潜在收益。

每场四次正式样本的 **96 项非时序字段**全部相同，机甲 54 行／亡灵 113 行完整动作、回合结果与预测记录一致；机甲保持 T7、结束 HP 59、战损 6。没有靠减少逻辑工作换取速度。数值、路线、GC 与排除字段完整保存在[精简证据 JSON](surgical-fixes-20260912.json)。

下面是**已撤回候选**的局部分配（Linux x64、独立 .NET 9.0.19、相同预热与分层编译设置，五块读数相同）：

| 操作 | 上游存储 | 合并存储 | 减少 |
|---|---:|---:|---:|
| 新建空列表 | 88 B | 64 B | 24 B，27.27% |
| 从 16 个 int 构造 | 176 B | 152 B | 24 B，13.64% |
| 普通 Fork | 24 B | 24 B | 0 |
| Fork 后第一次写入 | 176 B | 152 B | 24 B，13.64% |

不能把所有 Fork 次数乘 24 B；只有实际创建存储或发生复制的次数才对应该节省。累计少分配、GC 后保留堆、瞬时占用和峰值 RSS 是不同指标。

## 后续小优化的优先级

1. **小辅助表和容器容量。** `PredictionStateStore._countByType` 的旧局部候选在一两种类型时少分配 96 B/Fork，但线性查询会随类型数量增长。先统计真实类型数、零计数项与查询频率，再比较预设容量／小表，不改主状态字典、别名和 Fork 重映射；96 B 不是整场内存百分比。
2. **连续数组中的窄类型／位标记。** 只处理有严格全路径值域保证的数组。旧探针中 1,024 个 int → short 的数组少 2,048 B，而一个 int 字段改 short 的对象仍为 24 B；Dictionary 的引用键加 byte 值也可能因填充不省空间。当前 HP／格挡上限达 999,999,999，RNG 使用完整 64 位，不能截断；不能把 decimal 战斗运算换成近似浮点数。
3. **局部循环与精确元数据缓存。** 优先删除重复遍历、预分配已知容量、避免接口枚举装箱；缓存仅保留不可变元数据并有正确失效条件。不能缓存分支变化值，不能用缩小候选集合、近似状态键或降低 Beam 换性能。
   一个可定向研究的算法入口是 `BeamRetentionPolicy.RankDeferredCandidates`：当前全排序后只取前 limit。先测 N/K 与同分比例，再考虑局部选择；任何无法保持原同分顺序的情况继续原排序。主 `RankBest` 还要消费全池做保路，不是可以直接替换的普通 top-k。本轮只完成源码筛查，未实现或宣称这项收益。

4. **JIT 与 PGO。** 项目已使用 Release；游戏是 .NET 9 宿主，运行参数由宿主决定，给 Mod DLL 加属性不代表更换了游戏运行时。动态 PGO 自 .NET 8 默认启用，先检查宿主有无覆盖，再做冷／热搜索对照；不把“再开一次”当收益。[Microsoft 默认行为](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-8/runtime)、[运行时配置](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation)。
5. **内联与 CPU 指令。** 本机 Ryzen 7 7840H 提供 AVX2／AVX-512，但应先检查具体热点的 JIT 汇编。SIMD 适合批量独立、连续的整数操作；状态指纹的串行数据依赖、短列表和分支密集 Hook 未必受益。使用 `IsSupported` 与等价标量回退；本批没有加入汇编或指令集依赖。[.NET SIMD](https://learn.microsoft.com/en-us/dotnet/standard/simd)。

不建议批量标注 `AggressiveInlining`／`AggressiveOptimization`：前者可能因代码膨胀变慢，后者绕过第一层编译，会失去依赖该层的动态 PGO，还可能增加内存。每个候选都应有实际热点、汇编和交错 A/B。[Microsoft MethodImplOptions](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.methodimploptions?view=net-9.0)。

不照搬列表方案去继承 Dictionary：.NET 9 的字典复制只对**精确 Dictionary 类型**采用直接 Entry 复制，子类会回退枚举／重新插入；列表构造则能通过 ICollection 批量复制。这是本轮查阅运行时源码确认的额外成本边界。[Dictionary 实现](https://github.com/dotnet/runtime/blob/v9.0.7/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs)、[List 实现](https://github.com/dotnet/runtime/blob/v9.0.7/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/List.cs)。

`Pack=1` 不是通用开关：减掉 padding 也可能引入非对齐访问，应先测托管对象／数组实际大小与吞吐。[Microsoft Pack](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.structlayoutattribute.pack?view=net-9.0)。NativeAOT 也不适合作为当前 Mod 的小改动：项目依赖动态加载、反射及运行时代码生成，这些受到 AOT 限制。[NativeAOT 限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#limitations-of-native-aot-deployment)。

GPU 暂不实施：按当前对象图、Hook 和动态控制流推断，搬运数据与分支分歧会侵蚀收益；做成高效 GPU 批处理需要重排数据和执行流程，已越过用户要求的范围。这个判断是结合源码结构的工程推断，不是本项目 GPU 实测。[CUDA 关于分支分歧的说明](https://docs.nvidia.com/cuda/cuda-c-best-practices-guide/index.html#branching-and-divergence)。手写汇编同样仅在纯计算热点占比足够、intrinsics/JIT 不能生成理想代码时才有投入理由。
