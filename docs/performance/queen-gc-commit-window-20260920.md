# 女王搜索的 NoGC 有效窗口（2026-09-20）

基线是本任务已完成花园分配优化的 `967c7c39`，不是原版上游 `8be14100`。本轮修复 NoGC 恢复/重建只判断“下一次操作能装下”、却没有为后续推进留下足够容量的问题。它改变 Runtime 的 GC 模式选择，不删候选、不降低节点预算、不改终局排序，也不删除父节点历史高水位或清空判重表。

## 原包证据

报告 `46b87aaad3b14174a90ab975b0ae637d` 声明 CombatSolver 0.42.0、游戏 v0.111.0；储君、A10、女王，Windows、DOP16、NoGC 配置12 GB。283.174秒后用户停止搜索，搜索内主动回收113次；回收流程累计176.705秒，约占62.4%，其中各回收记录的GC暂停合计37.283秒。最后长帧日志累计进程分配106.019GB、GC暂停47.461秒。回收流程耗时不是主线程冻结时间，也不能直接作为改动可节省的时间。

后段恢复区域仅853,962,871字节、搜索额度569,308,580字节，但父节点预留513,894,480字节；每分配约55.4MB就再次回收。最后18个检查点之间24.609秒只增加242个节点。末次回收后活对象约4.064GB，原包没有对象图，不能将其归因于某个容器或直接判为泄漏。

约5秒记录的无药基线没有获胜，导出没有完成路线；不能认定它早已获胜、只是继续追求零损。

## 实现与边界

- `SearchMemoryPressureSignal.NextCommitReserveBytes` 保存 coordinator 在已排空边界观察的下一次不可分割工作预留。准入时更新；父节点完成后也更新，以覆盖刚观察到的新高水位。它是调度派生值，不进入战斗状态键，不由worker写入。
- Runtime 统一计算既有SOH/区域安全分配额度；公式保持原样。恢复与重建时，扣除预留后必须还剩 `max(64 MiB, allocationLimit/4)`。这沿用层间回收的有效分配下限量级，是保守准入启发式，不声称最优阈值。
- 恢复探针在调用CLR之前拒绝无效小窗口；检查点在重建前检查，CLR尺寸回退使区域进一步缩水后再检查实际容量。拒绝后清除分配限额并恢复普通GC，搜索继续；需求或容量改善后，原有Gen2证据、冷却和每scope最多3次预留尝试机制仍允许恢复。
- `CommitWindowInsufficient` 与真实物理余量不足分开。仅有效窗口小，不额外限制并行度；只有重建前已证实物理余量受限时保留既有保护，不把平台尺寸回退误认为物理压力。
- 仍可能存在大量合理回收与长期保留对象。本轮没有引入按时间强制停止搜索的策略，也没有把普通GC宣称为所有场景更快。

## 直接回归与实际恢复

`GcPolicyChecks -- commit-window` 建立真实853,962,871字节CLR区域，输入原包的513,894,480字节预留，执行生产检查点。将同一测试编译到基线生产源码时，在“不得重新建立55MB有效窗口”的断言失败；修改版通过。它还验证拒绝前不产生新的预留尝试、普通GC限额彻底释放、不误降并行度、恢复探针不能立即绕过拒绝，并在下一工作预留降至64MiB后经冷却恢复、恢复自身不额外强制收集。

`recovery` 10项、`admission` 6项、真实`recovery-lifecycle` 2项、真实`checkpoint` 1项通过；取消、退出请求与作用域释放合同继续通过。该组是生产生命周期复现，不能当作整场女王提速证明。

本机Linux `RestoreOnly/start` 对开战和首个可操作检查点的ContinuationStamp对账通过，状态为`restored_continuation`。旧包缺编号映射，`nativeStateVerified=false`、原因`legacy_model_id_mapping_not_recorded`，不称完整原生二进制恢复。

## 实验口径

原始产物在本工作区`.local/queen-gc/`，原始ZIP与日志不进源码。所有原包headless请求总超时120秒，均使用启动器的实例清理选项。

首次试用0.854GB测试设置，被现行设置最低1GB的校验拒绝，没有运行搜索；因此女王游戏实验使用1GB配置，精确854MB条件由上述独立CLR回归覆盖。首次8,000节点候选短搜达到TimeLimit，不能用于固定工作量比较。

随后4,000节点的Smart对照，选中成员达到NodeLimit，但协调器的补充审计仍受到请求剩余时间影响。关闭主动用药的实验也仍有开局资源/能力审计，不能把“选中成员NodeLimit”误读为整个请求工作量固定。完整请求用于检查行为和记录差异，不据此计算提速百分比；仅将固定主成员的计数与成本作为局部观察。普通GC/小NoGC的真实Windows长搜收益尚需原机验证。

## 女王短搜结果

以下为无主动用药、Beam512、每主成员4,000节点、DOP1、请求预算50秒、NoGC1GB的A-B-B-A。原政策、覆盖值和全部请求指标保存在[结构化证据](queen-gc-commit-window-20260920.json)。

| 顺序 | 主成员耗时 ms | 主成员分配 B | 请求总转移 | 请求主动回收 |
| --- | ---: | ---: | ---: | ---: |
| measure-a1 | 36495 | 10187030840 | 328573 | 28 |
| measure-b1 | 36626 | 10188863720 | 325797 | 28 |
| measure-b2 | 37264 | 10186601264 | 326256 | 28 |
| measure-a2 | 36716 | 10188390744 | 325797 | 28 |

四次主成员均4,000展开、223,269转移，选中结果均没有获胜、预计战损60。局部主成员耗时均值36.606→36.945秒（约+0.93%），分配基本持平，**没有观察到常规短搜提速**。请求均总展开6,685，但补充审计的转移数不同；两对60项比较分别差在总转移/总选牌数、总转移数，不能称完整请求逐字段相同。各次主动回收都是28次，不能将本组当作原包后段113次回收已被消除的证据。

原Smart政策的短搜也保留在`fixed-a1/fixed-b1`：两侧总展开7,948、总转移380,941/381,061，不能当作同工作量提速。该候选包装脚本在完成结果后因本地终止后续实验的脚本编辑报127，游戏结果和实例清理已完成；这组不进入性能收益结论。

## 完整路线哨兵

`sel-defect-elite-02`，Evaluate、VeryHigh、DOP1、20,000节点、60秒上限、NoGC1GB。首次误用了包含已注入配置的导出请求，被生成场景互斥校验拒绝；正式数据使用上一轮保存的原始最小请求。

两对各86项工具比较一致；另单独核对完整`planActions`（含选牌）、全部`cachedContinuations/continuations`、根ContinuationStamp/live stamp、政策和目录指纹，均一致。四次均自然结束，8,700展开、34,331转移、战损2。实际剪枝计数一致；最终`threatProjectionCacheCount`分别为3762/3874、3816/3825，是派生缓存占用差异，不将其隐去后声称全字段一致。

| 顺序 | 墙钟 s | 进程累计分配 B |
| --- | ---: | ---: |
| baseline | 7.5137 | 1845833248 |
| candidate | 8.2908 | 1850968480 |
| candidate2 | 7.7255 | 1852448856 |
| baseline2 | 7.8222 | 1849183176 |

第一对候选较慢约10.3%，为排查稳定退化追加反向顺序一对，候选较快约1.2%；四次均值候选约慢4.4%，方向不一致，不声称提速或已证明稳定退化。第一对进程日志证实两侧都发生两次生产检查点重建，均未触发新窗口拒绝；离线宿主汇总的`gcLifecycle=0`不是零回收证据，使用实际日志说明该限制。

## 分配栈与存活对象

独立诊断运行使用基线、原包start、DOP1、NoGC12GB、节点100,000、搜索预算60秒、总超时120秒，**不进入性能对照**。第一次等待日志标记没有命中，未产生采样；第二次在进程启动约25秒后附加15秒分配采样，然后采集一次gcdump。gcdump本身会触发GC，因此只用于对象图，不用于GC暂停收益。

- nettrace严格转换成功，48,007条已确认搜索分配tick，估算5.268GB；缺栈、纯未解析栈、丢事件均为0，23条样本含部分未解析帧。按互斥栈分类，Fork1.416GB（26.9%）、快照/评估586MB（11.1%）。`SimulatedCombatState.Fork → CombatPredictionState.Fork → Simulator.Fork → ReplayPlannedChoiceBranch`、`Snapshot`、`RecordRelicCardPlayed`的新字典以及Power动态变量克隆均在实际栈中出现；不从类型名推断调用方。
- GC后图为662,388,644字节、8,448,254个对象，数量/大小采样倍率均为1。含19,880个`SimulatedCombatState`、489,626个`PredictedCard`和311,462个动态变量字典。19,880个分支状态与48.9万个牌包装说明复制/保留仍值得继续研究，但不能用这个时刻代替原Windows后段4.064GB活堆。
- 从合成根能到达8,405,561个节点，尚有42,693个节点未取得根路径，不把对象图当作所有根关系已完整解释。示例路径明确经过局部搜索集合→`SearchNode`→`SimulationSnapshot`→Simulator→PredictionState→`SimulatedCombatState`→牌列表→`PredictedCard`，另有根快照和局部临时对象路径。
- 另发现静态`ObjectToIdentity/IdentityToObject`双向字典。通过本机DLL元数据和IL核对，属于RitsuLib的`ModModelIdentityRegistry`；`EnsureRegistered`向两个强字典写入，所查Runtime程序集内直接`Clear`调用来自`ModModelIdentityRunStateCreatePatch`，移除/恢复路径另有`Unregister`。这只说明所查程序集，未追遍外部反射或所有调用方。
- 在**只读堆图分析**中屏蔽这两个字典对象的出边，原先可达的390,265个对象、38,097,452字节不再可达，约为该图大小的5.8%。包括70,322个`Bound`、31,922个动态变量字典及字典自身的大数组。这是图上归属证据，不是实际释放量、删除注册的授权理由或跨战斗泄漏证明；本轮没有修改RitsuLib或清空身份表。

下一步应先追踪分支状态/快照的必要存活范围，以及身份登记的模拟隔离和生命周期合同；优先减少不必要的Fork或释放已淘汰分支。Power动态变量带owner和分支可变值，不能据采样直接取消深拷贝。当前证据不支持为了省内存直接删除搜索表或降低搜索质量。

## 复现与限制

```bash
dotnet run --project tools/CombatSolver.GcPolicyChecks -c Release -- commit-window
dotnet run --project tools/CombatSolver.GcPolicyChecks -c Release -- recovery
dotnet run --project tools/CombatSolver.GcPolicyChecks -c Release -- recovery-lifecycle
dotnet run --project tools/CombatSolver.GcPolicyChecks -c Release -- checkpoint
dotnet run --project tools/CombatSolver.GcPolicyChecks -c Release -- admission
```

Release构建0警告/错误、结构门禁通过。最终只精确化一处拒绝原因日志，之后重跑直接CLR回归并完成Release构建；不重跑GC决策未改变的全部行为实验。所有headless实例均由启动器清理。未验证原机Windows、DOP16、可见帧时间、完整300秒女王请求或女王整场自动部署；未在真实平台上强制制造CLR尺寸回退。当前交付是明确回收缺口的修复和下一阶段归因证据，不宣称女王卡顿已解决。

