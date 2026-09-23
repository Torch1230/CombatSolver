# 游戏 DLL 克隆与牌堆助手热点审查（2026-09-23）

## 范围与来源

本审查只核对现有三份 Linux 完整请求 CPU 样本、有限的游戏方法反编译结果和当前 Mod 侧补丁；没有重新采样、构建或改生产代码。
CPU 样本来自此前研究，用于定位候选；它们不是本次合入上游后的新性能对照，不计入 PR 复测收益。

样本环境为 Slay the Spire 2 v0.111.0，Linux x86_64，.NET 9；场景为 Ironclad 首领、Silent 精英、Regent
首领，VeryHigh、默认 300 秒／500,000 节点、Coordinator、DOP 8／16、Smart 药水、16 GB No-GC。三份 profile 的 Solver
DLL SHA-256 为 `d8c7a991b1f1cd4615312cccb11464d113f86bf13e9b5f22decf22abd14495af`.

使用的 Linux 游戏 DLL 路径为
`/home/ltlly/.local/share/Steam/steamapps/common/Slay the Spire 2/data_sts2_linuxbsd_x86_64/sts2.dll`，
SHA-256 为 `2b40d2df538db1ceb5fa48d958c80ab730ada1e07db88a870aff01a661768b9f`。有限反编译文件位于本机
`.local/decompiled/installed/`；`DynamicVarSet` 由现有 `ilspycmd` 对上述 DLL 按类型定点输出，没有扫描整个游戏 DLL。

Profile 实际加载的是 Linux 游戏 DLL。本轮另在用户授权的 Windows 主机核验了实际安装文件
`data_sts2_windows_x86_64/sts2.dll`：SHA-256
`0861bfa1df347538d932f22d580e75420f08082792eb914e53b4882764acdbe9`，游戏提交
`41cef1ea4657c524aa50e870df009e56337e8c32`。Windows 对照在该 DLL 上重新构建；Linux 的
CPU 归因仍只代表 Linux 样本，不把同版本的不同平台二进制视为相同文件。

## 采样能说明什么

事件为 `perf cpu-clock:u`，周期加权用户态 on-CPU 栈样本。每场 SearchStack 采样 CPU 分母分别为 62.668 秒、164.779 秒、69.271
秒；原始 profile 栈最多解析到 127 层。

最近的 CombatSolver 帧 `PredictionUtils.CloneModelForSimulation` 的 inclusive 归因分别为 2.724 秒（4.35%
SearchStack）、9.221 秒（5.60%）、2.447 秒（3.53%）。这些样本包括其下游 STS2、.NET 和第三方调用工作，不是该 C# helper
的独占耗时，也不是游戏 DLL 独占比例或完整请求墙钟比例。

原始 JIT 符号栈中能看到游戏方法 `DynamicVarSet.Clone`、`CardModel.DeepCloneFields`、`PowerModel.DeepCloneFields` 和少量 `AfterCloned` 帧；当前完整 CPU 汇总工具没有给这些
sts2 方法产生可信的 exclusive CPU 分账。栈样本出现次数不能替代周期加权 CPU 归因；inclusive 方法之间重叠，不能求和。127 层截断、JIT
内联和未知帧也会影响归属。

同一栈还反复命中 `SimulationCardPileLookupFastPath.Find`。这是已存在的 Mod 侧 fast path，不能把它列作本轮未实现的游戏 DLL
优化机会。

## 已核实的游戏语义与 Mod 边界

游戏 `AbstractModel.MutableClone` 先做 `MemberwiseClone`、再调用虚拟 `DeepCloneFields` 和 `AfterCloned`。`CardModel.DeepCloneFields` 会重建关键词、复制 `DynamicVars`、能量费用和临时星数费用，并复制附魔／诅咒附属模型；`AfterCloned`
会清除观察者并重置目标、出牌索引、牌组版本等瞬时字段。`PowerModel` 的深拷贝会复制动态变量并初始化内部数据；克隆后清空事件与
owner。跳过这些步骤或让分支共享可变模型会改变状态／生命周期语义。

游戏 `CardModel.Pile` 的实现会遍历 `owner.Piles`，再对每个牌堆调用 `Cards.Contains(this)`。当前
[SimulationCardPileLookupPatch.cs](../../src/Runtime/SimulationCardPileLookupPatch.cs)
仅在模拟隔离域、注册冻结且确认不存在扩展牌堆时替换 getter；它按原顺序扫描战斗牌堆，再扫描牌组，扩展堆仍走原版路径。修改其余 `Add`、`Remove`、移动或洗牌入口以维护成员索引，将涉及额外状态一致性与 RitsuLib 堆语义，不是可直接安全叠加的小补丁。

游戏 `DynamicVarSet.Clone(model)` 为每个变量调用 `DynamicVar.Clone()`，构建新的集合后逐值
`InitializeWithOwner(model)`。非空集合必须保留逐分支变量与 owner 关系。先前分支 `perf/dynamic-empty-clone` 的提交
`d1da9ad3` 有一个模拟隔离下的空集前缀：它对 `Count == 0` 跳过 LINQ／owner
初始化，但仍为每次克隆创建新集合。该提交带有基本语义合同，当前主线没有这项补丁，也没有找到它在多场景完整请求中的性能 A/B 证据。

将空集合换成共享实例是另一个未验证方案。虽然正常 API 对空集合没有可变元素，集合身份仍可通过 `ReferenceEquals` 观察，Mod
也可能反射私有字典；不得把“空”直接等同于已证明可共享。若为 `DynamicVarSet.Clone` 新增 Harmony patch，还会触碰
[NativeModelCloneConcurrency.cs](../../src/Engine/Common/NativeModelCloneConcurrency.cs) 的精确 patch
gate：它当前要求 `DynamicVarSet.Clone` 没有其他 Harmony patch，避免第三方克隆行为未审计时启用并行克隆。

## 结论

求解器会反复计算不同候选未来，游戏正常运行只推进当前时间线；两者的总工作量不能按画面或游戏规模直接比较。
原版 C# 助手是搜索调用链的一部分，但当前数据不足以认定游戏本体是主要性能瓶颈。

现有 CPU 数据证明通用模型克隆调用链在三个完整请求中占 SearchStack 约 3.5%—5.6% 的 inclusive on-CPU 样本，但不能证明这些周期都执行于游戏
DLL，也不能推出可获得同等 wall-clock 收益。`DynamicVarSet` 空集路径值得作为独立、受限的候选继续衡量分配与内存；其旧原型没有 A/B
性能证据，共享实例更没有安全或收益证据。

因此 PR #129 本轮不新增游戏 DLL 方法补丁，也不把空集克隆列为已实现优化。后续如要推进，只在隔离分支做 bounded A/B：固定
DLL、输入、搜索工作、药水和路线／质量指标；覆盖五角色及普通／精英／首领，分别测完整请求时间、累计分配和计算期峰值；确认第三方 Harmony patch gate 与克隆
owner／身份合同后再决定是否合并。未经这样的验证，不以 allocation stack、单根样本或 helper inclusive 百分比宣称整体提速。
