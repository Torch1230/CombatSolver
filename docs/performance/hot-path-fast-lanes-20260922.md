# 策略不变的热路径快速通道（2026-09-22）

目标：同一场战斗，改动后求解器给出**逐字段完全相同**的结果，只是更快。不碰搜索策略、评分、保留规则。
每条快速通道都要能用"跳过的工作在原路径上确实是空操作"这一条来严格验证。

口径：离线宿主、`profile=High`、beam 90、分支 48/28/36、Coordinator、Smart、DOP 1。
语料 EQ 10 + FULL 40 + GA 10 共 60 根。基线是本仓未改动的 `3f4002b`（0.43.2）。

## 1. 先查上游已经做过/否决过什么

`docs/performance/` 下 68 份报告里，以下方向**已有明确结论，本轮不再开**：

| 方向 | 结论出处 |
|---|---|
| 通用写时复制（StateStore / 牌堆包装 / Power） | `gc-issue36-implementation.md` §1、`state-sharing-20260913.md`、`memory-tenfold-20260913.md` §3 |
| 全执行器 undo / 先试后悔 | `gc-issue36-implementation.md` §6（`CompactStatePrototype` 原型：保留 frontier 下 undo 8,474 B/转移，不优于深拷贝 8,594） |
| deferred top-k / 部分排序 | `five-candidates-20260913.md` §4（15 份真实输入里 5 份非平凡输入全部同分、全部改变输出） |
| 逐回调监听位置索引 | `backend-cpu-hotspots-20260908.md`、`perf2-integration-20260909.md` §4（两轮实现两轮撤回） |
| `List.AddRange` 优化 | `backend-cpu-hotspots-20260908.md`:57 明确反驳，inclusive 仅约 1.23% |
| SIMD / 强制内联 / Server GC / NativeAOT | `five-candidates-20260913.md` §6、`surgical-fixes-20260912.md`:87 |
| 跨快照费用缓存 | `pr90-attempts-20260913.md`（需要完整失效证明，热点只占数个百分点） |

**`EndsWith` 线索已经过期**：旧采样里的 `Interop+Globalization.EndsWith` 来自
`ModelDb.GetId → ModelId.SlugifyCategory → category.EndsWith("_MODEL")`（文化敏感、走 ICU）。
上游已用 `RootCombatTransformationPoolSnapshot`（`transform-pool-root-snapshot-20260917.md`）和
`ModelDbGetIdCachePatch`（`defect-modeldb-getid-cache-20260919.md`，单根墙钟 −52.3%）两次把它消掉，
两者都在 0.43.2 里。详见 §3。

## 2. 字符串热点溯源（静态，已完成）

### 2.1 求解器自己的源码

`src/` 下所有 `EndsWith`/`StartsWith`/`IndexOf`/`Contains`/`Compare`/`ToUpper`/`ToLower` 调用**全部显式带
`StringComparison`**（多为 `Ordinal`），没有一处走当前区域性。搜索热路径（`src/Search/`、
`src/Engine/InCombat/`）里也没有 `string.Format` 或插值：
`HookMirrors.FormatSigned/FormatDecimal` 的插值只在 `simulator.IsRecordingActionRelicTriggers`
为真时才求值，而那是部署期标注路径，不是搜索。

结论：**求解器源码不是文化敏感字符串成本的来源。**

### 2.2 原版 `sts2.dll`（IL 全量扫描）

用 `System.Reflection.Metadata` 逐条解码 50,816 个方法体的 IL（0 个无法解析），
找出所有引用了不带 `StringComparison` 的 `String` 重载的调用点。全程序集只有 48 处：

| 被调方法 | 调用点数 |
|---|---|
| `String::EndsWith(String)` | 20 |
| `String::StartsWith(String)` | 27 |
| `String::ToUpper()` | 1 |

其中**唯一可能落在求解器热路径上的**是：

- `MegaCrit.Sts2.Core.Models.ModelId::.ctor` —— 每次 `new ModelId(category, entry)` 都做
  `category.EndsWith("_MODEL")`（构造期的编程错误守卫）。
- `MegaCrit.Sts2.Core.Models.ModelId::SlugifyCategory` —— `StringHelper.Slugify` 之后再
  `text.EndsWith("_MODEL")` 去后缀。

两者的唯一热入口是 `ModelDb::GetId(Type) => new ModelId(GetCategory(type), GetEntry(type))`，
而 `GetCategory` 走 `SlugifyCategory`、`GetEntry` 走 `Slugify`（3 条 `GeneratedRegex` + `ToUpperInvariant`）。
**`ModelDbGetIdCachePatch` 已经把整个 `GetId(Type)` 按 `Type` 缓存**，所以 0.43.x 的搜索里这条链
每个类型只跑一次。其余 46 处（存档迁移、云同步、模组目录扫描、本地化文件枚举、开发者控制台、
图鉴、时间线、`NullPlatformUtilStrategy::GetThreeLetterLanguageCode`）都不在战斗模拟路径上。

`StringHelper.Slugify` 本身用 `ToUpperInvariant`（区域无关），不是文化敏感项。

**`EndsWith` 的真相**：它确实存在、确实走 ICU、确实曾经是大头，但**根因在原版 `ModelId` 构造守卫，
而且上游已经用类型级缓存把它从热路径上拿掉了**。本轮不应再把它当候选（阶段 B 采样用来确认）。

## 3. 候选快速通道清单

### C1 —— 补齐剩余未按位图过滤的 hook 派发（已实现，实测低于 3% 门槛）

**省掉的工作**：`MirroredHookListenerFilter` 给每个监听者类型算了一张 54 位的参与位图
（位 = "该类型确实覆写了 `AbstractModel` 的这个 hook"），绝大多数 hook facade 已经用它把
监听表扫描缩短到真正的订阅者。但有 5 个派发点historically 走的是**未过滤**的
`CombatPredictionState.IterateHookListeners()`：

| 位置 | hook | 备注 |
|---|---|---|
| `HookMirrors.cs` `AfterBlockBroken` | `AfterBlockBroken` | 全表一趟 |
| `HookMirrors.cs` `AfterCardPlayed` 第一趟 | `AfterCardPlayed` | 全表一趟 |
| `HookMirrors.cs` `AfterCardPlayed` 第二趟 | `AfterCardPlayedLate` | 全表一趟 |
| `HookMirrors.cs` `AfterAttack` 主循环 | `AfterAttack` | 全表一趟 |
| `HookMirrors.cs` `AfterAttack` 的 `finally` / `AbortAttack` | `AfterAttack` | 全表一趟 |

监听表包含**战斗中的每一张牌**，所以这些是"每次出牌 / 每次攻击 × 牌堆规模"的扫描。

**为什么结果不变**：位图为 0 的类型不覆写该 hook，`MethodMirrorRegistry.Lookup` 对它解析出
`NotOverridden`，`Invoke` 立即返回，不推 trace、不记风险、不改状态。因此跳过它与调用它可观测等价。
`AfterAttack` 的 `finally` 走的不是 registry 而是类型 switch，只处理 `GigantificationPower` 和
`VigorPower`；这两个类型都在 `AfterAttack` registry 里注册过，注册期 `ValidateOverride` 保证它们覆写了
该 hook，所以位图一定包含它们。

**依赖的不变量**：
1. `Filter` 返回的 `MirroredHookListenerSnapshot` 包裹的就是原 source，**元素与顺序完全不变**；
   位图全空时返回空表（等价于全部是空操作）。因此把这 5 处从 `HookListeners` 换成
   `MirroredHookListeners` 只是"同一张表 + 一张布局"，不改内容。
2. 任何 mod 给 54 个基 hook 或 `Hook.ModifyKeywordsInCombat` 打了补丁时，`Capture()` 整体关闭过滤，
   全部退化为完整分发。
3. 非游戏程序集/动态类型一律给 `MirroredHookMask.All`，永不跳过。

**必须排除的情况**：`AfterAttack` 的 `finally` 与 `AbortAttack` 是在**已经挂起**的前提下做配对状态清理，
不能像普通派发那样在 `HasPendingChoice` 时提前结束。为此给枚举器加了"不受挂起边界约束"的变体
（`HookListenerEnumerable.Unsuspended`），只按位图跳过、不看挂起标志。

**验证钩子**：`COMBATSOLVER_VERIFY_HOOK_MASK=1`。开启后每次派发都逐个对账：
遍历未过滤的完整监听表，对每个被位图排除的监听者用 `MethodMirrorRegistry.ResolveDispatchKind`
（纯查表、无副作用）问出原路径会做什么，只要不是 `NotOverridden`/`Ignored` 就立刻抛异常；
`AfterAttack` 额外检查被排除者不是 `GigantificationPower`/`VigorPower`。

**本轮不动**：`HookMirrors.ExecutionContinuation.cs` 的 `CaptureUnfilteredExecutionHookListeners`。
它捕获的列表会存进可 Fork 的执行帧并跨检查点存活，改它等于改一份被持久化、被重映射的结构，
收益与风险不成比例。留作下一批。

### C2 —— 死亡生命周期指纹去 LINQ（已实现，本轮收益主体）

`SimulatedCombatState.AppendDeathLifecycleFingerprint` 每次构状态键（即每展开一个节点）都跑一次
`_deathPhases.OrderBy(entry => entry.Key.CombatId)`，为几条记录分配一个有序迭代器、一个键数组和
一个缓冲数组。采样把这条链的 `ToArray` 叶子定在 `FULL-SILENT-ELITE-03` 的 **10.54%**、
`FULL-DEFECT-BOSS-02` 的 5.79%、`FULL-REGENT-BOSS-00` 的 1.37%、`FULL-IRONCLAD-BOSS-03` 的 0.78%、
`FULL-NECROBINDER-ELITE-01` 的约 0%。

改法：把条目按字典枚举顺序抄进栈上缓冲（`long` 排序键 / `uint` 输出 id / `int` 阶段，都是基元，
`stackalloc` 32 条内联、超出走堆），再做插入排序。

不变量：`OrderBy` 是稳定排序，`CombatId` 相同的生物必须保持字典枚举顺序 —— 插入排序同样稳定；
键是 `uint?`，`Comparer<uint?>.Default` 把 null 排在所有值之前 —— 加宽到 `long` 并把 null 映射成
`-1` 精确复现。输出三元组与重载不变。

验证钩子：`COMBATSOLVER_VERIFY_FAST_LANES=1` 下每次都把原 `OrderBy` 跑一遍，
和快速通道的 `(id, phase)` 序列逐条比对。

### C3 —— 空修正链的惰性 context（候选，采样后否决）

`ModifyDamage` 无条件 `new ModifyDamageMirrorContext`，`ModifyBlock` 无条件建乘法 context，
即使三个监听循环一次都不进。改成首次进入循环时才建。
风险极低（context 只在循环体内被观测），但阶段 B 把整个"hook 派发与镜像"定在 0.87–1.20%，
其中 context 分配只是一小部分；上游同类改动实测 −0.546%。**收益上限远低于 3%，不实现。**

### C4 —— Fork 路径的两处确定性微优化（候选，采样后否决）

- `SimulatedCombatState.Fork.cs` 的 `_powerListenerOrder.Select(context.RequireRemap).ToList()`：
  LINQ 迭代器 + 委托 + List 增长，每次 Fork 都走，可改预留容量的手写循环。
- `SimCardPile.Fork`：先 `card.Fork` 一遍、再 `AttachCards` 设 `OwnerPile` 一遍，对同一牌堆走两遍，
  可合并为一遍。

两者都不改任何值，纯遍历形式变化。但阶段 B 里两者的独占占比都在零点几个百分点，
**达不到 3% 门槛，不实现。**

### C5 —— "先试后悔"代替克隆（只写设计，本轮不实现）

见 §5。

### 已查证但**不是**候选

- **`CombatPredictionHistory.GetRiskThrough`** 的 `[.. this.Take(n).OfType<RiskEntry>()]` 是
  O(整条历史) 的 LINQ，看上去像每节点都要付的成本。但它的唯一入口
  `CombatPredictionSimulator.Snapshot()` 在整个仓库里**没有调用者**，实际是冷路径。
  （如果将来重新启用，`_riskEntryCount == 0` 就是现成的 O(1) 短路。）
- **`ModelId.EndsWith` / `Slugify`**：见 §2.2，已被上游缓存。
- **`List.AddRange`**：上游已量化否定。

## 4. 测量结果

工具 `dotnet-trace --profile dotnet-sampled-thread-time`（约 100 Hz 托管线程栈采样），
DOP 1、单进程、机器确认空闲。该 profile 的每条栈都以 `CPU_TIME`/`UNMANAGED_CODE_TIME` 伪帧结尾，
归因时折进下面最深的真实帧。采样器对进程内每条线程采样，只有 DOP 1 的搜索工作线程有意义，
下表是该线程非等待时间的占比。采样开销实测约 +6.8% 墙钟（同一根交替跑 2 次采样 / 2 次不采样）。

### 4.1 独占时间（5 根范围）

| 类别 | 范围 |
|---|---|
| 集合操作 | **38.25–43.09%** |
| 状态克隆 / Fork | **28.96–42.56%** |
| 其他（未归类） | 4.96–11.29% |
| 字符串与全球化 | 2.50–7.20% |
| 游戏本体代码 | 1.25–5.93% |
| 保留排序与试探 | 1.63–3.78% |
| 求解器搜索骨架 | 1.97–3.44% |
| hook 派发与镜像 | 0.87–1.20% |
| 状态键 / 指纹 | 0.40–1.57% |
| 模拟器 / 命令时序 | 0.23–1.29% |
| 评分 / 快照估值 | 0.39–0.79% |
| GC / 分配 | 0.10–1.46% |
| 历史记录 | 0.00–0.10% |

独占时间意味着被调用方不算在调用方头上，所以"集合操作"排第一是因为 Fork、克隆和指纹最终都
落在 `List`/数组/LINQ 的叶子上。前两类其实是同一件事：分叉状态的代价 = 列表数组复制 +
`MemberwiseClone`（后者独占 9.14–15.03%）。

字符串与全球化整类只占 2.50–7.20%，**没有任何 `Interop`/`Globalization`/ICU 帧进入热点表**，
`ModelId`、`Slugify`、`EndsWith` 一个都没出现 —— 确认 §2 的结论。

### 4.2 等价性

| 对照 | 结果 |
|---|---|
| 60 根，0.43.2 vs C1 | `roots=60 fields=5590 mismatched_roots=0` → IDENTICAL |
| 60 根，0.43.2 vs C1+C2 | `roots=60 fields=5590 mismatched_roots=0` → IDENTICAL |
| 10 根校验模式 vs 基线 | 927 字段、`mismatched_roots=0`，校验零失败 |
| `totalExpanded` 合计 | 2,130,780 → 2,130,780 |

三批 60 根都是 `invalid=0`、`timeBoundaryObserved=0`。

**负对照证明校验不是空转**：把 `AfterCardPlayed` 早期一趟的位图与它的校验一起改成
`AfterCardPlayedLate`，校验在 208 ms 内抛出并点名 `Kusarigama`；同一个错位图在不开校验时
把 `EQ-IRONCLAD-ELITE-00` 从 `expanded=12190/score=9999279964/hpLost=52` 改成
`12785/9997897957/74`，说明这些循环确实承重。

### 4.3 计时（A B B A，每臂 4 次，机器空闲，单进程）

A = 未改动 0.43.2，B = C1+C2。进程墙钟秒：

| 根 | A 逐次 | B 逐次 | A 中位 | B 中位 | 差 |
|---|---|---|---:|---:|---:|
| FULL-SILENT-ELITE-03 | 120.1 126.2 127.0 132.0 | 118.8 119.1 123.7 124.6 | 126.60 | 121.39 | −4.11% |
| FULL-IRONCLAD-BOSS-03 | 60.9 61.7 62.4 64.5 | 60.9 61.2 62.1 63.3 | 62.05 | 61.66 | −0.63% |
| FULL-REGENT-BOSS-00 | 49.4 49.6 49.7 50.1 | 47.6 47.9 48.1 48.2 | 49.67 | 48.00 | −3.35% |
| FULL-NECROBINDER-ELITE-01 | 34.8 34.9 35.1 35.6 | 33.5 33.7 34.3 34.4 | 34.99 | 34.00 | −2.81% |
| FULL-DEFECT-BOSS-02 | 17.9 17.9 18.0 18.7 | 17.2 17.3 17.4 17.7 | 17.97 | 17.39 | −3.27% |

跨根中位 −3.27%。同臂内 max−min 占中位的比例（本次会话噪声底）：REGENT 1.2–1.4%、
NECROBINDER 2.4%、DEFECT 2.5–4.6%、IRONCLAD 4.0–5.8%、SILENT 4.8–9.4%。
REGENT、NECROBINDER、DEFECT 三根上两臂**完全分离**（B 的四次全部快于 A 的四次）；
IRONCLAD 上完全重叠，区分不出来。

分配总量（确定性）60 根合计 `totalAllocatedBytes` −0.41%，其中只算 C1 是 −0.16%。
分配降幅小于时间降幅，是因为 `OrderBy` 的代价主要是排序、委托调用和泛型派发这些 CPU 工作，
它分配的只是几个小对象，而分配总量被 Fork 的深克隆主导。

**没有分离 C1 与 C2 的单独计时**：C1 的收益上限已由采样定在 0.87–1.20%、分配实测 −0.16%，
低于噪声底，单独计时给不出可信数字。上表是两者合并的效果，绝大部分应归于 C2。

### 4.4 机器状态

同一根同一份 DLL：刚空闲时 14.26 / 14.47 s，连续满载约 40 分钟后的 ABBA 里是 17.9–18.7 s。
**绝对墙钟在一次会话里会漂 20% 以上**（持续满载降频），只有交错对照有意义。

## 5. 设计（本轮不实现）：先试后悔代替克隆

**想法**：展开一个候选动作时不 Fork 父状态，而是在父状态上直接执行、记录一条撤销日志，
读完快照后回滚，再执行下一个候选。

**为什么在本项目行不通（已有证据）**：
- `gc-issue36-implementation.md` §6 的 `CompactStatePrototype` 已经实现过 undo journal 和 page COW
  并做了 7 组语义检查。结论是 **DFS 与 retained-frontier 的结果分化**：undo 在 DFS 下可以零分配，
  但 Beam 搜索天生要保留整层 frontier，256 实体 / 5460 转移样本上 undo 仍要 8,474 B/转移，
  与深拷贝的 8,594 B 基本持平。Beam 必须同时持有一层里的所有状态，撤销日志换不回这个。
- 撤销面不止 `SimulatedCombatState`：`PredictionStateStore` 的 `Get`/`GetReadOnly` 返回**可变对象**，
  拿到引用的一方在 Fork 之后仍可能直接写（`gc-issue36-implementation.md` §1 举的
  `VambracePredictionState.TriggeringCard`）。这种写入不重新进 Store，撤销日志拦不住。
- 原版 Model 的可变面（`PowerModel` 的 `_internalData`、`DynamicVars`、卡牌 event 字段）由
  `CloneModelForSimulation` 的 `MemberwiseClone` + `DeepCloneFields` 携带，
  要撤销必须逐字段镜像原版的全部写入点，等价于重写模拟器。
- 并行展开（`ParallelExpansion`）需要同一父节点的多个候选同时执行；共享一个可变父状态
  与撤销日志直接冲突，只能退回串行。

**结论**：这条路线要么退化成"每层仍然要 Fork 一份保留态"，要么要求重写模拟器并放弃并行展开。
不建议在没有新证据前再开。

本轮采样补的一条新证据：`MemberwiseClone` 的独占时间是 9.14–15.03%。即使撤销日志能把它全部
消掉（它不能），按 Amdahl 上限也只有约 1.11–1.18 倍。**天花板配不上风险。**

## 6. 跨 Fork 写时复制（本轮不实现）

同样已有量化否决，见 §1 表。补充一条本轮新确认的事实：`Filter` 的监听布局
（`MirroredHookListenerLayout`）已经是跨 Fork 共享的不可变结构，只存 `(Type, Mask)`，不持模型；
`PredictedCard.PreviewStorage`、`CombatPredictionHistory` 前缀链、`ForkableList/Dictionary/Set`
也都已经是 COW。**Fork 路径上剩下的深拷贝只有 `PowerModel` 和 `OrbModel`**，
而 `PowerModel` 的 COW 被 `docs/research/allocation-attribution-20260917.md` 定性为
"必须穷尽拦截 amount / dynamic vars / internal data / 游戏 Hook 侧写入"，属于调研提案而非可落地改动。
