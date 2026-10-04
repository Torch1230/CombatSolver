# 0.49.2 全平台发布定稿（2026-10-04）

本卷保留战斗逻辑与移动运行库内存修复的根因、验证和未验证项。玩家更新日志见 [0.49.2](../../releases/0.49.2-RELEASE_NOTES.md)。

### 0.49.1 日志站硬逻辑（2026-10-04）

本批按首因修复强制结束回合的选牌阶段交接、群体 Power 目标序列、死亡分支金币监听、准备阶段延迟能量、等离子球被动次数、NINJA_SCROLL 开局生成、PAELS_EYE 与回合末自动出牌顺序、行动变换后的攻击意图、资源 Hook 的实机隔离、部署界面回合归属和反应伤害后的重复格挡。

规范 Power 预热使用原版来源，第三方实际附着实例继续主线程物化；第三方怪物、已确认修改玩法的 BetterVanillaSTS2、异步 OnPlay 补丁、缺失伤害计算的意图和外部选牌重叠形成明确兼容边界。确认内容来源时提示暂未适配且不要求上传日志。原版 TRANSFIGURE→DIRGE 差分一致，原报告额外能量的具体补丁来源及缩小甲虫原包失效引用尚未实机确认。

104 份固定报告的分类、根因、最小复跑入口与验证范围见 [逐类记录](../../issues/0.49.1-hardbugs-20261004.md)，失败基线和最终原生合同见 [测试矩阵](../../TEST_MATRIX.md#0491-日志站硬逻辑2026-10-04)。缓存缺后续回合一例已确认是 TimeLimit 部分路线到期后的正常重算。

### 移动运行库内存回收（2026-10-04）

#### 首因与处理

固定报告 `bc6ab827557b4dbe8d898e00a3bbdc89` 的四次失败均发生于普通 GC 搜索的内存检查点，入口为 `SearchGcPauseSnapshot.Capture`。环境记录 `gameExecutable=apk`、`processArchitecture=Arm64`、`os=Unix 31.0.0.0`、`.NET 9.0.7`，并加载 `STS2Mobile`。

[.NET 9.0.7 Mono GC 源码](https://github.com/dotnet/runtime/blob/v9.0.7/src/mono/System.Private.CoreLib/src/System/GC.Mono.cs) 中，`GC.GetGCMemoryInfo(GCKind)` 明确抛 `PlatformNotSupportedException`；无参数版本提供基础堆信息，`GetTotalPauseDuration` 返回占位零。[Mono GCSettings](https://github.com/dotnet/runtime/blob/v9.0.7/src/mono/System.Private.CoreLib/src/System/Runtime/GCSettings.Mono.cs) 同样拒绝 `CompactOnce`。原包只有基础限额日志，失败前未发起强制回收。

Runtime 一次探测按类型查询信息的能力，捕获已知的平台能力缺失并冻结选择；其他异常继续传播。具备完整信息时保持原后台回收、完成索引、NoGC 恢复与压缩释放流程。信息有限的运行库执行同步完整回收，用回收后的弱引用哨兵确认完成；`full_blocking_portable` 的 `completion_index` 来自完整代的 `GC.CollectionCount(GC.MaxGeneration)`，与桌面后台 GC 索引分别解释。

普通检查点仍刷新系统余量和分配限额，不可分割提交后由 CLR 接管分配。自动、检查点、请求结束后的回收及手动内存释放共用能力选择；NoGC 恢复以运行库提供的完成索引为准。暂停观测以可空值传递，回收日志使用 `unavailable`，Search 保留实际工作量和既有最大观测值。

#### 本轮证据

直接链接生产代码的 `portable-runtime` 先只加入可替换的 GC 信息读取入口，在原回收逻辑中复现同一 `PlatformNotSupportedException` 和 `Capture → ReclaimWithinSearch → ReclaimAndContinue` 调用链。原来的清理回收也因同一接口失败。修复后 5 项 Passed：

- 一次能力检测后不再访问被拒绝的接口，普通检查点与不可分割提交继续执行并释放准入。
- 取消保留已完成的真实回收和不可用的暂停观测，作用域可以释放。
- 自动及手动路径各完成一次真实阻塞完整回收，完成哨兵已回收；手动日志标记暂停统计不可用。
- 非平台能力错误继续传播。

桌面真实 CLR 相邻合同 Passed：`default-commit` 2 项、`checkpoint` 1 项、`diagnostic-failure` 8 项、`recovery-lifecycle` 3 项。覆盖限额、取消、NoGC 退出恢复、排空和完成链清理。

原生无头 `B013-DEFAULT-GC-LIMIT` Passed：runId `ac4ee495b5ad48158c0d709a49b0abd2`，22.883 秒。默认 GC 限额、真实回收续搜和退出状态通过；夹具同时验证暂停观测缺失时耗时、分配和 GC 次数继续累计，既有最大观测值保持。启动器删除 `.local/headless-instances/wt-f9fe55e52824479a`。

结构门禁与工具检查通过；原 Android 设备和原包整场回放未执行，移动端性能未验证。

复跑入口见 [测试矩阵](../../TEST_MATRIX.md)及 [GC 工具](../../../tools/testing/checks/CombatSolver.GcPolicyChecks/README.md)。
