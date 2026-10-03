# CombatSolver 测试入口历史卷 05

## 2026-09-10：伤害来源生命状态隔离

- Release 编译通过，0 警告/0 错误；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。未安装或启动游戏。

- `python3 tools/DamageDealerChecks/run.py`：101 项生产伤害入口检查通过。旧代码在分支已死、实机存活的输入下仍产生伤害结果，最小基线失败。
- 编译生产全部 Damage 重载及 DamageSingleTarget；交错实机/分支死亡状态，覆盖禁止 live getter、独立分支、复活、null 来源、空目标及死亡来源不进入伤害/后续 Hook。
- 状态和逐目标/后续 Hook 为确定性替身；没有验证真实 Fork、伤害计算、原生死亡时序或报告中完整递归抽牌链。保留原递归安全上限，不把本合同写成整场验收。



## 2026-09-10：动态目标类型分支隔离

- Release 编译 0 警告/0 错误；Bash 结构门禁通过，`search_files=84`。同步更新两端门禁的分支模式，PowerShell 因当前环境缺少 `pwsh` 未运行。未安装或启动游戏。

- `dotnet run --project tools/CardTargetingChecks -c Release`：37 项通过，直接编译生产 `CombatPredictionSimulator.CardTargeting.cs`；旧源码复现分支无能力时误用实机全体目标。
- 两张动态目标牌分别覆盖分支能力缺失/0/1/2/移除、实机能力有无交错、其他角色能力隔离、独立分支，以及普通卡和非影子状态回退。影子状态用例禁止读取原生目标 getter。
- 游戏 0.111.0 的原版 Shiv / SovereignBlade 目标 getter 已定向核对：对应能力决定 `AnyEnemy` / `AllEnemies`。测试模型和状态为替身，不构成真实根捕获、完整 Fork、原生伤害或原包回放验收。



## 2026-09-10：结束回合循环出口准入

- Release 编译通过，0 警告/0 错误；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。未安装或启动游戏。

- `python3 tools/EndTurnAdmissionChecks/run.py`：33 项通过。直接编译生产结束回合入口、raw 生成、剪枝、准入谓词、materialization 循环和批次所有权容器；旧 `BuildAcceptedEndTurnNodes` 在相同输入下因未结算观测到达转置准入而失败。
- 覆盖有效/撤销/无父租约、多选牌子分支单一出口、终结边界、剪枝、转置拒绝、stand-pat 发布，以及提前退出/生成失败时快照释放。
- 质量计算、单张出口票据签发、模拟与转置判断使用确定性替身；不构成游戏原生差分、预算触发的完整搜索或原报告回放验收。既有展开入口 fail-fast 和循环预算保持不变。



## 下一版本（开发中）：回合末卡牌 Hook 的 COW 接收者

束缚清除可能替换共享卡牌预览，后续Regret Hook 持有旧接收者并找不到手牌，漏记失血张数。常规 BeforeSideTurnEnd 派发先固定卡牌 wrapper 和监听顺序，执行时跟随当前预览，保留原先的挂起检查和非卡牌身份。

本轮生产 COW/牌堆源文件的独立检查：旧派发模式复现失败，修复后 78 项断言通过。Release 构建 0 warnings / 0 errors，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。没有启动游戏、恢复原报告或执行原生伤害/正式搜索差分。报告计数与证明范围见 [专项分诊](../issues/regret-bound-hook-receiver-20260910.md)。


## 0.35.2：回合开始选牌生命周期

最终行为源码使用同一 DLL 完成以下 12 项后台原生页面回归，均 Passed。场景结果位于本地 `outputs/turn-setup-ui/verified/<ScenarioId>/result.json`，同目录保留请求和命令记录。后台实例已退出，性能录制保持关闭。

| ScenarioId | runId | 验证边界 |
| --- | --- | --- |
| TURN-SETUP-UI-TOASTY | f4772d52214c4a028c8e72d1b814d2ba | 初次停止、重算中停止、再次重算、执行进入 Play |
| TURN-SETUP-UI-GAMBLING-PARTIAL | a9bb1d4e4b3d4538b7754e99d47fb801 | 同上，额外覆盖未确认的手动勾选后计划部署 |
| TURN-SETUP-UI-TOOLBOX | abd258ec251f40eebf88d11841c00295 | TOOLBOX 原生选择页停止与恢复 |
| TURN-SETUP-UI-PARADOX | d907f709caba4366b916de1033f630d0 | CHOOSE_A_PARADOX 原生选择页停止与恢复 |
| TURN-SETUP-UI-TOOLS | a15eba75865b41acac5183e7f32c46ee | T2 TOOLS_OF_THE_TRADE，无既有续用选择 |
| TURN-SETUP-UI-TYRANNY | 85ca20ed4d9040d494742e32b1103963 | T2 TYRANNY，无既有续用选择 |
| TURN-SETUP-UI-MIXED | 957c52a9ba574a2480b87d44ad9d7e68 | TOOLBOX、GAMBLING_CHIP、TOASTY_MITTENS 连续选择 |
| TURN-SETUP-UI-TOASTY-MANUAL | 28e4d6508dc04d33a71360f52afa9bd1 | 搜索中手动确认，旧搜索退出且旧结果不安装 |
| TURN-SETUP-UI-TOASTY-FULL-AUTO | efc0e0d46575422dbe09006e0ab4f02f | 停止后立即开启全自动，排空后重算并驱动原生选择 |
| INITIAL-TOASTY-MITTENS-SEARCH-CONTROLS-REGRESSION | 9511fb1ec2b045c89951b43051f93306 | 搜索中采用、执行及手动重算入口，T1 26 动作 |
| TURN-SETUP-STOP-CANDIDATE | dc2da1d6fe0e4ad794da077ebdcc17e6 | 保留停止候选并采用，忙碌标志清除，T1 24 动作 |
| TURN-SETUP-APPLY-CURRENT | 5a0a705091fc46b8a78ed46c6d4bc841 | 应用当前回合，T1 23 动作 |

- 使用 SILENT / GREMLIN_MERC_NORMAL、2000 ms 短搜、`PreserveNativeCombatStateForTest`、单请求 120 秒上限。UI 场景在首次目标 Play 状态停止，断言忙碌标志清除、页面遮挡消失、搜索失败为空；部分勾选场景同时断言计划外重算为 0。
- 迭代中的手动确认 fixture 曾超时，暴露页面等待后的搜索所有者竞争；恢复原子状态转换后通过最终手动确认及全自动场景。未延长超时。
- 这些是后台原生控件与生命周期验证，没有打开可见 Steam 游戏，不代表人工鼠标/动画验收或所有场景完整战斗战损差分。

## 2026-09-10：PR #74 / #75 / #77 合并验证

- #74：直接编译 PR 中生产 profile 与 BuildNarrowBeamRecoveryProfile，剩余 49000 节点/299000 ms 和 2000 节点/1000 ms 正确；预算耗尽与普通 Beam 不产生救援。复用同一行为源码审计阶段的结果，不声明实战战损改善。
- #75：`pwsh -NoProfile -File tools/verify-unattended-map-points.ps1` Passed，4 种输入；从生产请求哈希表的 AST 取出真实字段表达式，检查省略、空数组、单点、多点的 JSON 往返形状，拒绝 null 和嵌套数组。原 PR 的显式空数组和非空数组均已复现多包一层。
- #77：`MODEL-STATE-INTEGRATION` / `444ddec2f3bf49568ece16acf9be80f5` Passed，27.93 秒，T1 → T2。真实游戏身份、CombatRootSnapshot、完整 CombatPredictionSimulator.Fork 和原版回合推进；专用测试为 BurningBlood 与 BigGameHunter 登记状态，持有可变列表及预测卡牌引用。父子对象/列表/卡牌隔离，子分支变更改变指纹与续用文本而父分支保持原值；原生和预测下一回合完整快照（含 continuation）一致。不是第三方 Mod 效果镜像通用正确性的证明。
- 原 PR 独立 32 项状态合同、3 项空登记及 1000 次 0 字节指纹分配检查在审计阶段通过；同一接口行为不重复。合并后的 Release 编译 0 警告/0 错误，PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。没有打开可见 Steam 游戏，没有发布。

专用模型状态合同必须使用新后台实例，并在请求后退出，避免已冻结的测试登记污染其他请求：

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId MODEL-STATE-INTEGRATION -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -ModifierId BIG_GAME_HUNTER -EnemyCurrentHp 500 -HeadlessInstance model-state-integration -ExitOnComplete -TimeoutSeconds 120
```

## 遗物与 Modifier 通用状态接口（开发中）

- `ModelPredictionStateChecks --empty`：Passed，3 项；空登记表保持原指纹与 continuation 文本。
- `ModelPredictionStateChecks`：Passed，32 项；捕获后 live 变化隔离、同类型实例交换、零值与集合顺序、父子分支隔离、引用重映射、事务边界、错误 Fork 拒绝（包含派生运行时状态被切成基类）、缺失/重复捕获、重复/迟到登记、精确类型、区域设置、并发读取，以及独立 live/predicted 更新和重新捕获后的等价描述。
- `--fork-type` 在修正前拒绝断言失败：派生状态被复制为声明的基类且未报错，虚属性从 2 变为 1。修正要求实际运行时类型一致，已由上述 32 项合同覆盖。
- `--allocation` 在修正前检测到 1,000 次无文本指纹调用分配 32,088 字节（其中包括首次比较初始化）；改为下标遍历玩家列表后，预热比较和查询的最终检查 Passed，1,000 次调用分配 0 字节。这是固定调用的分配合同，不是游戏或搜索耗时 A/B。
- 检查直接链接生产 registry、writer、store 和 fingerprint，游戏身份、模拟器外壳与 Fork context 使用替身。没有启动 Godot，也没有据此声称完整模拟器 Fork、原生结算或两回合差分通过。
- 当前游戏 0.111.0 的 macOS ARM64 引用下 Release 构建通过，`CopyModOnBuild=false`，0 错误；最终 `--no-restore` 构建有 1 条 `NU1900` 警告，来自无法访问 NuGet 漏洞数据源的缓存恢复记录。
- Bash 结构门禁通过：`REFACTOR_BOUNDARIES_OK search_files=84`。PowerShell 对应规则已同步，未执行（本机无 `pwsh`）。未执行游戏内单效果/两回合差分、部署、性能 A/B；具体适配的语义验收仍需这些针对性差分。

```sh
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release -- --empty
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release -- --allocation
```

接口及手工验收范围见[模型状态适配](../../third-party/model-state.md)。此记录仅对应本项开发改动，不复用下方历史游戏场景作为本轮证据。

## 0.35.1：回收后堆空间复用

- `dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- memory`：Passed，直接链接生产策略及信号；玩家采样剩余可复用空间 4,172,872,440 字节，区域容量 6,938,945,322 字节。验证复用空间不重复增加物理用量、三 GB 波次可准入、物理上限立即阻止准入、用户预算上限、回收委托、Disable 探针清理及无实时探针模式。
- 同工具默认入口：19 项 GC 策略检查 Passed。Release 编译通过，0 警告/0 错误；版本元数据变化后的最终发布构建不重跑这些合同。
- 证据：`outputs/centipede-gc-20260910/checkpoints.json`、`trace3-raw.json`。原始第三段完整解析，EventsLost=0；目标战斗 36 次主动回收、102 个 SuspendForGC 暂停区间，总暂停约 6244.8 ms、最大约 861.8 ms。此为旧版现场，不能作为修复后的结果。
- 按用户要求没有启动实机或无人游戏；没有实际回收次数下降、FPS 或路线质量的新结论。

## 0.35.0：发布验证

- 复用下列文字特效与战斗速度证据及明确的未验证边界。SpeedX 提示增加中英设置指引，路径与当前控件标签逐项核对，英文 JSON 做语法及文案键匹配检查。
- 遵照用户指令不再启动实机测试，不执行完整发布门禁。发布从本次版本提交执行一次 Release 构建和最小 ZIP，发布包包含许可声明与 Windows 内存清理工具。

## 2026-09-10：文字特效释放与战斗瞬间速度（未发布）

- 正常可见 Steam `COMBAT-TIMING-LIFETIME` / `5e72da97870d452c821f1b46110b0437` Passed，23.38 秒，T1 到 T2。原版 reverse patch 与生产回调对照涵盖两种特效、开关及三个动画时点，比较返回值、颜色、位移、变换和可见性；4 万次回调的 Godot 临时对象登记为 14178 → 14178，原环境字典仍可读。
- 原生 Cmd.Wait 在战斗 Instant 下同步完成，在 FollowGame / Normal 下真实等待；执行原生结束回合动作并完成怪物回合后，战斗仍为 Instant，全局偏好保持 Normal。
- 后续追加战斗结束后的局外等待检查，首次 fixture 直接 Kill 后缺少 CheckWinCondition，120 秒超时；已补齐胜负结算调用。用户要求停止实机测试，修正后的局外检查未执行，不将其记为通过。生产行为与已通过测试相同，最后变更仅为测试补充。
- 最终 Release 编译通过，0 警告、0 错误。本机更新开发 DLL，未发版。未重现玩家完整两层后的长会话，不声明整体卡顿全部消失或提供 FPS 增幅。可复跑输入为 `coverage/unattended/combat-timing-lifetime.json`。

## 2026-09-10：SpeedX 横幅告知

- UI 低影响提示变更：检测已加载程序集名 SpeedX，在原有反馈横幅加中文/英文性能提醒；既有异常信息继续保留。检测结果通过一次枚举和程序集加载事件维护，不在每帧扫描程序集。没有增加 SpeedX 修补逻辑。
- 验证采用 Release 编译；本轮未进行实机横幅排版验证，不声明已修复 SpeedX 或长期卡顿。

## 2026-09-10：进程全程性能诊断（未发布）

- 自动压缩追加合同：`outputs/performance-compression-contract/` 连续 3 段全部成功采集并压缩，原始总计 8,408,334 字节转为 1,458,408 字节；collector 正常 complete。解压第一段后解析出 10043 个线程采样、8707 个分配事件、538 次 GC start，EventsLost=0。玩家已有段一次性从 280,502,570 字节压为 26,455,804 字节；此次只修改外部存储脚本，不改变 DLL、采样配置或搜索行为。

- 独立 `tools/PerformanceRecordingTests` 合同：36 秒进程采样，刻意停止主线程心跳 3 秒；验证后台仍写入、线程/内存/GC 数据、队列无丢弃、退出排空。10 秒分段连续采集并正常退出；带空格输出路径通过。修正 Windows PowerShell 的 File.Replace 空备份路径及 Process.ExitCode 句柄生命周期问题后最终合同通过。
- EventPipe 显式使用 runtime `0x100003C01D:5`，避免多个 profile 合并保留 Informational 而漏掉分配事件。独立样本解析出 10118 个采样事件、8696 个分配事件、538 次 GC start，EventsLost=0；topN 能解析出测试计算方法。采样含等待时间，不当作 CPU 占比。
- Heap dump 合同：独立进程收到现场请求，dotnet-dump exitCode=0，dumpheap 成功读取 PerformanceSession 等对象；连续轨迹继续分段并收尾。对应本地证据 `outputs/performance-snapshot-contract/`。
- 正常可见 Steam 短战斗：`f5cf09910c8b46548444922d768076f4` / `VISIBLE-LOGGING-CAPTURE-0300` Passed，31.75 秒，T6 结束，零计划外重算。时间线包含完整搜索/部署事件、16 个生命周期开始事件、引擎资源与主线程样本；dropped=0。抽检游戏轨迹段 63069 个采样事件、3720 个分配事件、8 个竞争事件，EventsLost=0。
- 最终进程所有权边界：`f6a6db1e04d04bbb9bb19c9b89d0b014` / `PERFORMANCE-RECORDING-LIFETIME` Passed，37.75 秒；生产录制节点移除/重新安装仍引用同一 PerformanceSession。实际时间线只有一个 start、一个 host_reattached、一个 process_recording_end 和 writer_end，采集器 complete，dropped=0。抽检段 67889 个采样、6375 个分配、13 个竞争事件，EventsLost=0。退出后的 ZIP 导出成功。
- 最终 Release 编译 0 警告/0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。本机诊断配置最终使用 300 秒分段；不重复五分钟等待验证仅时间参数变化。
- 复跑节点边界：先在测试安装启用诊断配置，再运行 `tools/run-visible-steam-benchmark.ps1 -RequestFixturePath coverage/unattended/performance-recording-lifetime.json -TimeoutSeconds 120`。这是诊断管线验证，不是对具体快速 SL Mod 的兼容承诺；未复现用户完整两局后的持续卡顿，也未建立诊断开销的完整 A/B 或正式版 FPS 结论。

## 0.34.10：撤回新增搜索目标

删除 0.34.9 的目标模式、目标面板、独立收益排序与达标停止，恢复原成长策略；保留牌堆缓存和组合估值。五项游戏回归通过，单请求 120 秒：

| 场景 | runId | 验证范围 |
| --- | --- | --- |
| GROWTH-POLICY-FREE-FIRST | `d174c57bb03b47e787a827f29a9c9107` | 零额度优先免费成长、忽略收益开关、原侧栏、逐次额度、Fork 与增量回放；旧三种目标配置保留已有额度 |
| GROWTH-POLICY-PAID | `32b7f8852606422f873228bcb2209fa1` | 零额度拒绝付血；允许额度内实际付血成长，超额拒绝，完整获胜优先 |
| PROFILE-STRENGTH-SHIV-DEPLOY | `9806fb4ae82a4c3489ad8cafb92cf649` | 保留力量小刀估值，T1 无损部署、零计划外重算 |
| PROFILE-EXHAUST-DRAW-DEPLOY | `50d71e6fcea34248b7590091e971d543` | 保留消耗抽牌估值，T1 无损部署、零计划外重算 |
| SMART-POTION-INVENTORY-FULL | `d5e87bc97a74438fa75e80092907c4bd` | 满栏按原门槛保留药水，预测战损 3 |

- 复用实例中的首个付血场景 `efae88b299b34e7ca7c06dee7e7dd4c9` 在侧栏开关／边界测试断言失败，尚未进入该场景的战斗搜索；独立实例同时通过 UI 与付血搜索合同。未定位重复使用 UI 测试时的状态干扰，不将其写成生产战斗语义缺陷或已修复项。
- 旧配置读取使用现有反序列化入口测试：忽略已移除的 objective 字段，保留 geneticAlgorithm=7 及原忽略收益设置。没有添加新的迁移默认值或覆盖用户配置。
- 行为构建 0 警告／0 错误；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`；CoverageCatalog `--verify-effective --verify-state-fields --verify-state-writes --verify-branch-state-reads` 3035 项通过。目标专属 SearchObjectiveChecks 随功能删除；调用旧入口报项目不存在，该检查不再适用，由上述原成长合同覆盖。
- 回归之后只改版本与发布文档，执行最终 Release 构建，不重复行为测试。0.34.9 的以下发布和测试记录均保留为历史。玩家更新日志见 [0.34.10](../../releases/0.34.10-RELEASE_NOTES.md)。

## 0.34.9 发布依据

发布行为源码为 `ceea8ef`：保留本批战斗搜索改动，撤回满栏药水优惠。复用下方集成回归及撤回后的药水、部署、UI 配置恢复证据；本次只修改版本与发布文档，执行一次最终 Release 构建，不重复游戏测试。玩家更新日志见 [0.34.9](../../releases/0.34.9-RELEASE_NOTES.md)。

## 2026-09-10：撤回满栏药水优惠

- 用户要求撤回满栏药水策略。终局准入、反事实省血门槛和药水搜索容量恢复原规则，删除 PotionInventoryValue 及其专属纯函数测试。更新满栏和部署夹具：保留药水、预测损血 3，部署结束生命至少 67、零计划外重算。
- 最终 `SMART-POTION-INVENTORY-OPEN` / `64ce2ff94ddd404d89513635bde6e351`、`SMART-POTION-INVENTORY-FULL` / `b7eefa6fe2cd463ebfbdf29c8d50e9da`、`SMART-POTION-INVENTORY-NO-BENEFIT` / `22d1c780d5b749008c1c97c1ae1678a6` 均 Passed；实际部署 `SMART-POTION-INVENTORY-DEPLOY` / `6c87d7aa16114f73997cedb228987df0` Passed。每请求 120 秒，独立测试实例。
- 最初未满栏探针 `342e0bc4fa284dcaad865369f33484d3` Failed：此前 UI 测试把永久培养目标和关闭自动计算写入测试实例配置，测试结束只恢复内存。修正 UI 测试结束时同步恢复持久化配置，并恢复该私有实例的平衡目标；未修改玩家设置。
- `SEARCH-OBJECTIVES-UI-LOCALE` / `b939207ba9a94af2904eff00ba4d9cca` Passed，测试后读取持久化配置确认平衡目标及自动计算已恢复。
- Release 构建 0 警告／0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=85`。以下上一轮满栏折扣测试结果仅作为历史记录，不代表当前策略。

## 2026-09-10：PR #69 / #71 / #73 战斗内部分集成

从三个审查分支提取战斗搜索目标、达标停止、无序牌堆缓存、战斗潜力与满栏 Smart 药水策略，集成到 0.34.8 之后；奖励、商店、删牌评分与画像 UI 未引入。以下为本次集成源码的直接结果，使用独立实例 `combat-pr-integration`，每请求 120 秒。

| 场景 | runId | 结果与范围 |
| --- | --- | --- |
| SEARCH-OBJECTIVES-GROWTH | `88504d6ad582413689ca1e42653bc348` | Passed，四模式正式短搜、收益与零损限制、增量回放、Fork、无序缓存与强制重算一致 |
| SEARCH-OBJECTIVES-RESOURCES | `efa7410eadc644c3a803f2f763aeef77` | Passed，金币收益与生存／平衡／限制模式对照、增量回放 |
| SEARCH-OBJECTIVES-TARGET-STOP | `0b4cb333c5044043a4e43d31955c0213` | Passed，实际培养达标且安全获胜，求解器日志与协调器停止谓词同时命中，增量回放 |
| SEARCH-OBJECTIVES-UI-LOCALE | `9ab2ae3174e642678d3eb7f65c3908d9` | Passed，战斗目标下拉框／限制保存、eng/zhs/zht 切换及面板边界；headless 控件验证 |
| SMART-POTION-INVENTORY-OPEN | `54568b386eaf427ca79698ef58fa292b` | Passed，未满栏保留药水、预测战损 3 |
| SMART-POTION-INVENTORY-FULL | `a4eb30b03d0143588c8dd75c245512f0` | Passed，满栏使用 1 瓶、预测战损 0 |
| SMART-POTION-INVENTORY-NO-BENEFIT | `fe8283a5b6334695a1c56a9ed518cb50` | Passed，满栏但无收益时保留药水 |
| PROFILE-STRENGTH-SHIV-DEPLOY | `0210cd748c894b85a98b020351726173` | Passed，力量与生成小刀，T1 无损部署、零计划外重算 |
| PROFILE-STRENGTH-SHIV-NO-SETUP | `7662f42e678e4a6db3a35e82014c02d2` | Passed，无需力量铺垫，T1 无损部署、零计划外重算 |
| PROFILE-EXHAUST-DRAW-DEPLOY | `07fb7f35c312410d8958a17243cb4e1f` | Passed，消耗抽牌组合，T1 无损部署、零计划外重算 |
| PROFILE-NO-DRAW-NO-SETUP | `6d1cb121f36f4965815454625a174a17` | Passed，禁抽时跳过无效铺垫，T1 无损部署、零计划外重算 |
| TEST-SUBJECT-ORIGINAL-REPORT | `3e6e7f796ffa41fcaa6d52c859a12404` | Passed，0.34.8 天际钻头原包首回合至 T2 完整状态回归 |
| REPORT-ROUND-DOOM-THRESHOLD-CARD | `534b875dd61c47a3a536b32fdde66f3a` | Passed，0.34.8 末日降临临界击杀与复活跨回合完整状态回归 |

- 目标停止探针最初 `3b410fb57dd342a79fc9392c9bfb8950` / `09a49fc244794146be3db2a9c26d82fc` Failed。建局将故障机器人原生 75 HP 降至 70，被累计战损记录为 5 HP，违反探针的零损收益限制，实际选中无收益路线；这是夹具输入问题。固定 75/75 HP 后达标停止通过，提交夹具为 `coverage/unattended/search-objectives-target-stop.json`。没有放宽生产血量限制。
- 本轮未修改被提取功能的生产语义；仅修正缩进、剥离局外入口并新增停止回归。最后两次构建只改变测试诊断，既有成功场景不重复运行。
- Release 构建 0 警告／0 错误；SearchObjectiveChecks 227 项、PotionInventoryChecks 21220 项通过；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=86`；CoverageCatalog `--verify-effective --verify-state-fields --verify-state-writes --verify-branch-state-reads` 3035 项通过。
- 复跑入口为 `tools/run-unattended-test.ps1` / `.sh`：成长用 `SEARCH-OBJECTIVES-GROWTH` / DEFECT，手牌 GENETIC_ALGORITHM、STRIKE_DEFECT；资源用 `SEARCH-OBJECTIVES-RESOURCES` / IRONCLAD，手牌 HAND_OF_GREED、STRIKE_IRONCLAD。两者均 ClearRunDeck、ClearPlayerPiles、初始能量 1、格挡 0、敌生命 6，手牌 TreatAsDeckCard=true，120 秒。UI 用成长建局并指定 `SEARCH-OBJECTIVES-UI-LOCALE`。目标停止输入见上述新 JSON，机制和药水输入见 `coverage/unattended/profile-*.json`、`smart-potion-inventory-*.json`。原包和末日降临沿用 0.34.8 的命令。本轮没有原包整场质量 A/B、可见帧率或发布结论。

## 0.34.8 发布集成

合入已发布 0.34.7 后，补充验证剑圣冻结根与复制牌的重放次数，以及原报告的击杀边界。既有分批失败基线与最终证据保留如下；发布集成结果另记于本节。

- Release source commit：`8caa17f`。最终 Release 构建 0 警告 / 0 错误；以下四项均使用该构建、私有实例 `report-release-0348`、单请求 120 秒。
- `REPORT-CARDS-SWORD-SAGE` 合并基线 `661dd28f012a42cc944d4b7c47a27e6f` Failed：零层时跳过登记，已有复制牌后获得剑圣少一次重放。删除跳过后 `3a3c72ddb6ff4d67a53cc52e32bd216d` Passed，逐牌完整状态与 Fork 一致。
- `BACKEND-SWORD-SAGE-ROOT` / `75152668cc8440b28e4d1fd9b70fb24e` Passed：冻结根、首次移除、从零获得、生成牌和父子分支隔离。
- `TEST-SUBJECT-ORIGINAL-REPORT` / `5e6958f43351426f9836f262d029b2b9` Passed：5cc95 原包首回合全部动作前缀及第二回合完整状态一致，原生开战恢复通过。
- `REPORT-ROUND-DOOM-THRESHOLD-CARD` / `9603604deaf8416d8dd9eec3ea8f4bb7` Passed：末日降临与血肉戏法临界击杀、复活至下一回合完整状态一致。
- 集成结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`，CoverageCatalog `--verify-effective --verify-runtime-evidence` 3035 项通过；随后仅修改剑圣的零层基线登记，使用上述两项剑圣场景验证，覆盖登记与结构未改变。其余已通过的分批行为证据复用，未做整场或可见界面完整验收。
- 更新日志中英文游戏名称分别与当前游戏 PCK 的 `localization/zhs`、`localization/eng` 核对。

实验体批次与此前静默猎手批次的证据分别记录，原包恢复与最小差分分开计。以下分别保留失败基线、最终结果及未整场回放的范围。

## 2026-09-10：两份原始 Boss 复活错误的根因修复

- `TEST-SUBJECT-ORIGINAL-REPORT` 从5cc95原包已验证的首个可操作状态出发，按原始部署顺序使用OROBIC_ACID、FLEX_POTION、LIQUID_BRONZE，打SPECTRUM_SHIFT、TYRANNY、HEAVENLY_DRILL、BULWARK，结束回合并按原记录选择ASCENDERS_BANE。基线 `ebbcbc7c3e8d4f688944b20b87024b66` 在T2复现玩家HP97/103；逐前缀定位 `9516db27445a487ea2cecad9d34e185c` 首错是HEAVENLY_DRILL后敌HP8/0。最终 `2139e56df0aa41f5940ad6bca1cee627` Passed，全部七个动作前缀及T2完整状态一致。只验证原包首回合至第二回合，不声称整场通过。
- HeavenlyDrill 相邻场景：升级牌X=3 `4837b0e8c6a84f15b3fa71bc7f3c79ad` Passed；基础投入2能量加CHEMICAL_X达到4 `8c38386ff1f5428ab60a336aecd9b7a5` Passed。两场均比较原生/预测完整状态和Fork，敌人200HP避免过早击杀掩盖次数误差。
- `REPORT-ROUND-DOOM-THRESHOLD-CARD` 使用e476报告的施放前临界条件：敌134HP（最大213）、Doom34、SleightOfFlesh13、Duplication1、升级EndOfDays37。基线 `d4ffe973627244df921d003d529bdf52` Failed，跨回合玩家HP预测46/原生66，处决晚一阶段。最终 `339f3bf702874325bf13a36ea0b9c37f` Passed，完整状态、Fork、父前缀继续回放一致。夹具显式断言134HP/34Doom，注入Doom在SleightOfFlesh之前，避免建局触发13伤害改变临界点。该夹具采用第一形态验证共用处决/复活链，没有整场恢复e476至T6。
- 最初简化探针使用低HP，或注入顺序使施放前HP已降至121，均无法证明此临界根因；旧Passed不能替代本次失败基线。一次新镜像错误调用live卡ResolveEnergyXValue产生空引用，已改为PredictedCard分支入口，最终证据以上述Passed为准。
- 最终Release构建0警告0错误，CoverageCatalog `--verify-effective --verify-runtime-evidence` 通过；HeavenlyDrill/EndOfDays覆盖条目指向本轮针对性证据，未发布。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId TEST-SUBJECT-ORIGINAL-REPORT -CheckpointArchivePath <5cc95原包.zip> -CheckpointSelector start -ReplayMode RestoreOnly -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-ROUND-DOOM-THRESHOLD-CARD -CharacterId NECROBINDER -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 134 -InitialEnemyMaxHpsJson '[213]' -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 3 -CardsJson '[{"CardId":"END_OF_DAYS","Pile":"Hand","UpgradeLevels":1},{"CardId":"DEFEND_NECROBINDER","Pile":"Draw","Count":7}]' -PowersJson '[{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1},{"PowerId":"DOOM_POWER","Target":"Enemy","Amount":34},{"PowerId":"SLEIGHT_OF_FLESH_POWER","Target":"Player","Amount":13},{"PowerId":"DUPLICATION_POWER","Target":"Player","Amount":1}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
```

## 2026-09-10：原生恢复边界与编码诊断（开发中）

- `REPLAY-BOUNDARY-CONTRACT` / `2ed7ea141baa48748df3b3fcaea69cb7` Passed：旧两项/三项历史匹配、已记录历史和其他字段不一致拒绝、当前四项任一不同拒绝；边界观察器的原异常对象被等待链抛出，不变成缺失边界。
- 5cc95 原包基线 `08cf2adff9b24b8cbf4f9892da607222` 报缺失边界，内部首差异实际是 `Y=0/0/0` 与 `0/0/0/0`。最终 `579796fc4ccc469ebd1de83f618f737f` Passed / restored，开战及首个可操作检查点完整状态、原生二进制均通过，restorationVerified/nativeStateVerified/readyCheckpointVerified=true。
- e476 原包原先停在 hash，移除硬门禁后 `39c37d90f60b411d88cfc9797663729e` 的 ContinuationStamp 通过，但本机表解码旧二进制报 SavedProperty58 越界（本机47项）。最终 `befca951aa014dfd977c22479e2e203e` Passed / restored_continuation，两处已记录战斗状态通过；原生二进制未核验，restorationVerified=false、nativeStateVerified=false，原因明确为旧包未记录模型编号映射。
- 两包均使用原 ZIP、start / RestoreOnly / 120秒，只验证开战至首次可操作检查点（replayedEvents=0），没有回放 Boss 复活错误回合，不表示原始战斗逻辑问题已修复。
- 最终 Release 构建0警告0错误，CheckpointTool self-test 29项通过，结构门禁 REFACTOR_BOUNDARIES_OK search_files=78。未发布。

## 2026-09-10：程序集清单差异取消硬拦截（开发中）

- 基线：两份原包在 `ValidateCheckpointModsAfterStartup` 以 `environment_mismatch:mods` 提前退出。该判断将整个程序集数组直接比较，混入外观 Mod、加载器和辅助库。
- 现在按名称生成缺失、新增和构建变化诊断，写入 `replayVerification.modEnvironmentComparison`；继续原生模型解码、事件恢复和完整状态检查。不会仅因库存不同失败，也没有把未知 Mod 宣称为无影响。
- 原包 `5cc95…` 再次 RestoreOnly：`08cf2adff9b24b8cbf4f9892da607222`，通过库存检查并进入 `native_replay_events`，最终 Failed / `native_replay_missing_combat_start_boundary`。
- 原包 `e476…` 再次 RestoreOnly：`6bbfd2c3cdd141269552ca6b92557ac7`，库存差异完整保存，随后 Failed / `environment_mismatch:modelIdHash`。模型 ID 表校验保留并补充预期/实际 hash 的明确字段诊断。
- 两次都使用原 ZIP、`CheckpointSelector=start`、`ReplayMode=RestoreOnly`、单请求 120 秒；这证明库存差异不再挡住恢复，不证明原包恢复成功。最终 Release 构建 0 警告/0 错误，未发版。

## 2026-09-09–10：实验体汇总包（开发中）

34 份报告的逐包结论见 [分诊记录](../issues/test-subject-reports-20260909.md)。以下均为本任务实际运行结果；语义夹具比较完整 MoveStateSnapshot / ContinuationStamp，包括逐实例有序牌堆、Power、怪物 AI、资源与 RNG，另检查 Fork。第三方和克隆事件场景验证各自的明确边界，不冒充整场差分。

| 场景 | 失败基线 | 最终 Passed |
| --- | --- | --- |
| NIGHTMARE-SELECTION-SNAPSHOT | `9d2befcaf31a418cbd2529142373e0fe`，副本费用 3/0 | `0985fa1d841844db8f2ddd35494424ee` |
| CLONE-EVENT-ISOLATION | `df880d27c9ab4538bafa80039ca96e3d`，调用实时订阅 1 次 | `5fcb0c2afdea4e24b7271b33d9037095`，0 次 |
| REPORT-CARDS-PANACHE | `6e393d8b0b44472ba59de7550756fe71`，实例合并 | `2316903dd0814a9387fc7c71bc1a73cb` |
| REPORT-CARDS-CRUSH-UNDER | `d484ff388a6e4378964ec7f388e9b3f8`，临时 Power/力量顺序 | `9bd9c9d07e614cdda06d86837235b5a4`，首次与叠加 |
| HISTORY-COURSE-EMPTY-TURN | `9bc30ec4b2a94894aeb070184f2386bb`，多重放旧攻击，敌 HP 39/45 | `8c69d71eb49c4a588351ddaaddff68c4` |
| REPORT-CARDS-SWORD-SAGE | `e07652667e314c0782291fd1220b6bb3`，复制牌少一次重放 | `516bd8a4c9534091b640e988ac34d1b4` |
| FOREGONE-IMPLICIT-ORDER | `fbf96f2e4beb41eb974746cd2aa160bd`，两张牌顺序反转 | `bb04cacf2f344e5a8a6c4a5137b8d7bd` |
| REPORT-ROUND-UNCEASING-HELLRAISER | `d64d5f079c8f4487abb573535ccde4f8`，手牌 5/3 | `d05fcc32fb5e4501a3695cb0728f1cab`，最终回合顺序源码 |
| REPORT-ROUND-HAILSTORM-ORBS | `39c741c197c04e1892a665f4c437b9b1`，目标 RNG 2/1 | `8e753acd438e40e89b2bceb873495a7d`；移除实机额外回合列表读取后 `bdcf0989998a4b51b258dc5012359329` |
| REPORT-ROUND-NOSTALGIA-STRIKE | `e1d57aa82cc54fd5890d8628508890a8`，第二回合攻击进错牌堆 | `fa776392afa3413b93ee93ce4e931550`；新增检查点历史校验后 `2bff954cd0a246c4abd406220823b202` |
| REPORT-CARDS-PALE-BLUE-ROOT | `12498ff2098a424eb7f9037a1f2ff5c4`，下回合抽牌 Power 2/1 | `733ed99ebed048e1b9d2933512fd6899` |
| REPORT-ROUND-HOWL-MUSIC-BOX | `5dc660636a54439c9ae036755c11e582`，手牌 5/6 | `88166b32b56541479cb7fa666adeee09` |

相邻与失败边界：FocusedStrike 临时集中 `a7119fb6a1dc4159b0a827333f563b31`；UnceasingTop 正常手动出牌 `0d16b0590bdf4515aea99871715e6a05`；已知 Mod ID/程序集别名、包装异常文案及上传分类 `dae3810cc97f492280d62de99ae04173`；FlexPotion、SpeedPotion 使用与回合末恢复 `a1a7783a7d784e0390a4e51d8806afa1`，全部 Passed。

Nostalgia 最终夹具另校验包含四项 `Y` 的检查点历史能被读取，并明确拒绝被篡改的攻击/技能开始数；只验证历史解析，不替代原包全状态恢复。

关键复跑命令（其余使用相同请求入口，夹具专用语义在对应测试文件）：

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-ROUND-NOSTALGIA-STRIKE -CharacterId SILENT -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 200 -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 10 -CardsJson '[{"CardId":"STRIKE_SILENT","Pile":"Hand"},{"CardId":"DEFEND_SILENT","Pile":"Draw","Count":7}]' -PowersJson '[{"PowerId":"NOSTALGIA_POWER","Target":"Player","Amount":1},{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-CARDS-PALE-BLUE-ROOT -CharacterId REGENT -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 200 -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 20 -CardsJson '[{"CardId":"DEFEND_REGENT","Pile":"Hand","Count":6}]' -PowersJson '[{"PowerId":"PALE_BLUE_DOT_POWER","Target":"Player","Amount":1},{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-ROUND-HOWL-MUSIC-BOX -CharacterId IRONCLAD -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 200 -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 20 -CardsJson '[{"CardId":"DEFEND_IRONCLAD","Pile":"Hand","Count":8},{"CardId":"STRIKE_IRONCLAD","Pile":"Discard"},{"CardId":"HOWL_FROM_BEYOND","Pile":"Exhaust","UpgradeLevels":1}]' -PowersJson '[{"PowerId":"DARK_EMBRACE_POWER","Target":"Player","Amount":1},{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1}]' -RelicsJson '[{"RelicId":"MUSIC_BOX"}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-TEMPORARY-STATS -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 100 -ClearAllPowers -PotionChecksJson '[{"PotionId":"FLEX_POTION","TriggerPlayerSideTurnEndAfterUse":true},{"PotionId":"SPEED_POTION","TriggerPlayerSideTurnEndAfterUse":true}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
```

复活问题的未复现探针：`REPORT-ROUND-END-OF-DAYS-CARD` / `dcd3e25a127345c79b9693f292876b6d`、`REPORT-ROUND-ROOT-DEAD` / `f52b03efa1e74883ae3b29dfcea0f466`、`REPORT-ROUND-SECOND-FORM-CARD` / `4e45a941db034c28ae0d766ace29b9b4`、`REPORT-ROUND-SLEIGHT-DOOM-CARD` / `98fe9ed3bc664a0bb10e28f4b18f3e7b` 均 Passed；昨日直接击杀、毒杀、多段攻击探针也未复现。它们证明这些最小输入的原生状态一致，**不证明两份原始复活报告已修复**。原包 Preflight 材料有效，RestoreOnly 均 `environment_mismatch:mods`，没有恢复成功证据。

最终行为源码 Release 构建 0 警告/0 错误；`verify-refactor-boundaries.ps1` 得到 `REFACTOR_BOUNDARIES_OK search_files=78`。CoverageCatalog `--verify-effective --verify-state-fields --verify-state-writes --verify-combat-choices` 通过，3035 项、0 未分类状态字段、0 未解决范围内选牌源。最终移除实机额外回合列表读取后，`--verify-effective --verify-branch-state-reads` 通过。没有整场求解、可见 UI 验收或发布。

## 2026-09-09：0.34.6 静默猎手修复合并验证

- 将 `fix/silent-unexpected-replans` 的 `7f5a984` 合入包含 PR #67 / #68 / #72 的源码。合并后的 Release 构建 0 警告 / 0 错误；`CopyModOnBuild=false`，使用本机现有 .NET 4.8 引用包。结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`；CoverageCatalog `--verify-effective --verify-roster-sources` 通过。
- 以下为本次合并后的直接结果，均完成 T1 → T2 严格差分，比较有序牌堆、逐实例卡牌、Power、怪物状态、RNG、ContinuationStamp 和相关 Fork 状态。它们验证新监听器过滤与既有修复的组合，不代表原报告整场回放。

| 场景 | runId | 结果 |
| --- | --- | --- |
| MURDER-ROOT-HISTORY | `c40bf06fe6d34e669ba6faf6f77fee68` | Passed，28.13 秒；根捕获后实机抽牌、父子分支倍率隔离、原生出牌及下一回合 |
| TENDER-DISCARD-ALL-SLY | `c8492cd07e864f47a7c5cfb1b06c23a2` | Passed，16.59 秒；精密计算与内层狡猾自动牌各结算一次、属性与回合末恢复 |
| STOCK-REPORT-RESPAWN-HP | `9452a1f11d5a4143a7fd383fd4699054` | Passed，12.45 秒；原报告 Niche RNG 边界、反伤死亡与下一回合替补完整状态 |

- 使用下方原场景命令，私有实例改为 `silent-release-0346`，每请求期限 120 秒；证据位于集成工作区 `.local/silent-merge-evidence/`。结束后已停止该实例并精确删除其拥有的 `game` 快照，Steam 游戏目录未写入。
- 本次未重复已通过的无关纯计算/并行调度检查，未运行整场性能 A/B 或可见 FPS 测试。更新日志已审核，0.34.6 定版仅改变版本与发布文档，复用上述行为验证。

本次静默猎手三项根因最终共 5 个最小行为场景通过（3 项根因 + 2 项相邻回归）；Release 构建 0 警告 / 0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=78`，CoverageCatalog `--verify-effective --verify-roster-sources` 通过。以下分别保留失败基线、最终结果及未整场回放的范围。

## 2026-09-09：谋杀根历史隔离（开发中）

- `MURDER-ROOT-HISTORY` 基线 `40301c95dd4b4bfba22aa1abb4e3fa96` Failed，21.22 秒：实机抽一张牌后，冻结父分支倍率从 8 变 9、已抽一张的 Fork 从 9 变 10。最终 `11ea828e2e69419fa522008201fb5e5e` Passed，28.14 秒。
- 最终夹具覆盖原生抽牌前后的父分支/Fork 隔离、实际打出谋杀的伤害与完整状态、T1 至 T2 全量状态和下一回合抽牌后的倍率稳定。完整比较包括有序牌堆、卡牌实例、Power、怪物状态、RNG、ContinuationStamp；未正式搜索、未整场回放。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId MURDER-ROOT-HISTORY -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 100 -ClearAllPowers -ClearPlayerPiles -CardsJson '[{"CardId":"MURDER","Pile":"Hand"},{"CardId":"DEFEND_SILENT","Pile":"Draw","Count":7}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
```

## 2026-09-09：温柔与出牌效果内自动牌（开发中）

- `TENDER-DISCARD-ALL-SLY`：基线 `289cb0270eeb48d7b4ac81e3247c85e3` Failed，23.96 秒，首差异力量预测 -3 / 原生 -2；最终 `94c01fe41a564329a15f54ae62d66dc4` Passed，16.63 秒，验证精密计算与内层 FLICK_FLACK 各触发一次、Fork 完整状态及 T2 属性恢复和计数归零。
- `TENDER-NESTED-SLY` 手动选牌相邻对照最终 `45bf88eef731446cbe7d11d279b2a792` Passed，16.25 秒。旧源码该对照 `fe4d41bbc2ba4e29b9bceb72890c5e80` 已通过，说明错误发生在出牌效果内自动牌历史被父牌扫描的路径。最初 `c77511470ae4469d80038ce74ef7cca3` 因夹具漏放准备而建局失败，只作输入错误记录，不作语义失败基线。
- 两项均比较完整 MoveStateSnapshot/ContinuationStamp、逐实例卡牌、有序牌堆与 Power、敌人状态和 RNG，无正式搜索或整包回放。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId TENDER-DISCARD-ALL-SLY -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 100 -ClearAllPowers -ClearPlayerPiles -CardsJson '[{"CardId":"CALCULATED_GAMBLE","Pile":"Hand"},{"CardId":"FLICK_FLACK","Pile":"Hand"},{"CardId":"DEFEND_SILENT","Pile":"Draw","Count":7}]' -PowersJson '[{"PowerId":"TENDER_POWER","Target":"Player","Amount":1}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
```

相邻对照使用 `TENDER-NESTED-SLY`，把手牌换成 PREPARED 和 UNTOUCHABLE，其他参数相同。

## 2026-09-09：补货原报告 RNG 边界修复（开发中）

- `STOCK-REPORT-RESPAWN-HP` 从报告 `835b630a...` T9 检查点注入 Niche counter=54 及四段内部状态，以旧个体最大生命 96、当前生命 3、ONE_TWO_MOVE 和 3 点荆棘构造两回合边界。基线 `416f94d9708746018de96cb3a37efb26` Failed，26.64 秒，首差异 `E0.hp expected=103 actual=104`；最终 `70aa5edb08e34137b61fc3d2c75d7978` Passed，27.71 秒。
- 相邻直接击杀 `STOCK-RESPAWN-HP` 最终 `403c3b881b8c45f2a4168ced84cdf54a` Passed，12.87 秒。使用下方已有命令；报告 RNG 场景使用荆棘命令并将 ScenarioId 改为 `STOCK-REPORT-RESPAWN-HP`。
- 严格比较死亡后及下一玩家回合的完整 MoveStateSnapshot/ContinuationStamp，包括有序牌堆、卡牌实例、Power、敌人阵容和 AI、九条 RNG；预测 Fork 保持相同快照。未正式搜索、未整包回放。旧通用种子探针的通过只属于旧输入，不能代替本次失败基线。

## 2026-09-09：静默猎手补货最小分诊（开发中）

- 两个夹具均运行在未改生产语义的 `2c7bee5` 基础上，完整 MoveStateSnapshot 比较包含有序牌堆、逐实例卡牌状态、Power、怪物状态、RNG 和 ContinuationStamp，另比较预测结果与 Fork。没有启动正式搜索，不使用增量搜索开关。
- `STOCK-RESPAWN-HP`：`a32c5e203ed54ca281ef9d961c78789e` Passed，28.12 秒，比较直接击杀后的替补状态及 T2 边界。
- `STOCK-THORNS-RESPAWN-HP`：`d2d88c43f8f84e35a4b89f427e5654b4` Passed，27.56 秒，比较敌方攻击被荆棘击杀后 T2 的替补状态。
- 这些是未复现的最小探针；没有失败基线，不能视为 `835b630a19a44c7c897d63dfc56d9a49` 已修复或原包回放通过。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId STOCK-RESPAWN-HP -CharacterId SILENT -EncounterId AXEBOTS_NORMAL -Ascension 10 -EnemyCurrentHp 1 -InitialEnemyMaxHpsJson '[96]' -ClearAllPowers -ClearPlayerPiles -CardId STRIKE_SILENT -PowersJson '[{"PowerId":"STOCK_POWER","Target":"Enemy","Amount":1}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId STOCK-THORNS-RESPAWN-HP -CharacterId SILENT -EncounterId AXEBOTS_NORMAL -Ascension 10 -EnemyCurrentHp 3 -InitialEnemyMaxHpsJson '[96]' -InitialEnemyMoveIdsJson '["ONE_TWO_MOVE"]' -InitialPlayerBlock 20 -ClearAllPowers -ClearPlayerPiles -CardId STRIKE_SILENT -PowersJson '[{"PowerId":"STOCK_POWER","Target":"Enemy","Amount":1},{"PowerId":"THORNS_POWER","Target":"Player","Amount":3}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
```

## 2026-09-09：PR #67 / #68 / #72 的 Windows 合并验证（下一版本开发中）

- 集成基线 `2c7bee5`，PR heads 分别为 `0baaf74`、`3c9edc9`、`e9e6103`。两个背包检查工具均链接最终唯一生产实现 `ReachableHandValue`。
- `dotnet run --project tools/PreCombatRequestChecks -c Release`：10 项通过；这是替换进程边界的 API 合同，未运行真实战前 worker 关闭/空闲期限测试。
- `ReachableHandValueChecks`：32,551 组原二维递推精确对照通过；`ReachableHandPotentialChecks`：10,000 组子集 oracle、溢出/大数组、零分配通过。三类常见 DP 各 100,000 次调用新增托管分配均为 0 B，仅代表纯计算。
- `PowerAmountComparisonChecks`：8 项通过，包括 1,568 组原生输出及 getter 顺序、实际 Harmony 重写、未知 IL 保留、100,000 次调用装箱分配 9,600,000 → 0 B。`ExpansionBatchChecks` 8 项及 `CombatSolver.WavePolicyChecks` 容量/溢出/10,000 组边界检查通过。
- `StateFingerprintChecks`：200,005 个混合/边界输入的原 128 位输出一致；`RitsuTargetTypeLookupChecks` 23 项通过，含程序集加载、动态类型后创建、可回收程序集、并发与 live 查询保留。
- Release 构建 0 警告 / 0 错误，使用 `-p:CopyModOnBuild=false`；首次因未配置 .NET 4.8 引用路径失败，指定本机已有引用包的 `TargetFrameworkRootPath` 后通过。Power 检查工具显式传入当前游戏及 RitsuLib 路径。Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。
- 私有实例 `pr-merge-20260909` 复用同一 DLL，每请求期限 120 秒。下表均为本次直接运行；证据保留在集成工作区 `.local/merge-evidence/`。

| 场景 | runId | 结果与范围 |
| --- | --- | --- |
| STAND-PAT-PROBE-BATCHES | `a8b90d9a00894ff2b941af088221c33f` | Passed，59.94 秒；原投影死灵药水 fixture，Deep 1,000 节点及 Short 250 节点 DOP1/2 动作、评分与非时序计数等价，实际并行、取消/异常排空、复用、257 槽位、Fork 边界 |
| MIRRORED-HOOK-FILTER | `1af13fe1b62f462abb717b86794de11d` | Passed，9.22 秒；1,670 模型 / 55 回调，顺序、重复、外部类型、Fork、失效、补丁刷新、共享布局和监听分段 |
| HAND-POTENTIAL-COSTS | `47ebddc7b39745648eaa754f7c31cc25` | Passed，6.64 秒；REGENT、能量/星能各 3、专用牌组，5 项原生与重复查询费用相等，完整根与分支状态保持 |
| MIXED-POWER-ACQUISITION-ORDER | `5c39acf4594146b8ba4e39874ffe0da3` | Passed，11.34 秒；0.34.5 的有序 Power、牌堆、资源、ContinuationStamp 和 Fork 严格差分 |
| HELLRAISER-TURN-START-HISTORY | `1e91f087d1aa48bb94e0545aacefaf7a` | Passed，9.74 秒；0.34.5 的第 1 → 2 回合抽牌自动出牌历史及完整状态差分 |
| BACKEND-SWORD-SAGE-ROOT | `9f39cb7d612c46628e719fd96c556d4b` | Passed，5.50 秒；REGENT / SOVEREIGN_BLADE / SWORD_SAGE_POWER=2，冻结根、分支增减、生成牌、重复归一和 Fork 隔离 |

- 并行场景沿用 `docs/archive/performance/perf2-integration-20260909.md` 的最小合同命令并加 `-VerifyForkBoundaries`；监听和费用合同在首根断言后停止。两个 0.34.5 回归使用其原场景参数，Power 顺序场景指定 `-EnemyCurrentHp 100`。
- 未运行新一轮整场性能 A/B、可见 Steam / FPS、完整发布门禁；其他章节的作者历史测量不能计入本轮测试。
- 所有请求结束后已停止该私有实例，使用 `Remove-HeadlessOwnedGameTree` 精确清理其带所有权标记的 `game` 快照；Steam 游戏目录未写入，诊断证据保留。

## 2026-09-09：快照/重放复查与上游合并

- 投影洗牌原型预定 B–C–C–B：8 个 Short/normal 测量请求 Passed，90 项原始结果及各 59/64 条动作一致；normal 均值变化 −0.216% 小于 3.190% 基线漂移，原型已撤回。
- 合并 `7ac005e` 后 Release 0 警告/0 错误，两端结构门禁通过（84 个 Search 文件）。只改提示文案的双语部分做资源键、CPU/DOP 档位和占位符 L0 检查。
- 合并版 `STAND-PAT-PROBE-BATCHES`：`4286e80ab6b145ca8e778c03ddb3a7e5` Passed；257 槽位、取消/失败排空、复用及 235,536 字节记账，Deep 1,000 节点 DOP1/2 结果一致、587 次待命探针。
- 上游语义与分支所有权交叉验证：`NORMALITY-AUTOPLAY-REPLAY` / `4c8a33fd786144e5aa57f6d56185d646` 与 `SLOW-TURN-RESET-FORK` / `f876d91c61a04a54aeaa7cb7a2f89157` Passed；前者含完整原生状态与开始次数、父子隔离、回合清零。
- 最新上游/合并版目标 `bdf47f6589014329997f292485c07025` / `ebb2fce7ed124f79a13cd14ad798ec39` 均 Passed：主搜索 10,000/144,276/101,420、评分和战损/药水、57 条完整动作相同；6 项物理调度字段与 3 项共享时限累计工作字段不同。哨兵 `c065f7cf7b9847da967e022940036c0d` / `d8437a044f4242909b42cf20328d6b9f` 的全部 90 字段、9 条动作一致。
- 独立缓冲日志通过正常战斗退役后读取已持有的只读文件句柄取得。首次战后问题包采集在未修改上游 `45f433b9306e4545b22ed18af4a0951d` 的导出阶段失败，未写为通过；后续正常首结果请求不带该不适用的导出断言。详细 runId、原始差异、复跑及未验证范围见[报告](../performance/snapshot-replay-followup-20260909.md)。

## 2026-09-09：perf-2 选择性合入与保路作业

- 最终固定 VeryHigh/DOP8/NoGC16GB，预定 B1–C1–C2–B2–B3–C3–C4–B4，八个独立进程各预热一次再测 Short/正常。正常均值 23.6271→23.2887 秒（−1.43%），Short −2.98%；全部16个正式结果 Passed，88项非时序 RESULT 字段（含7项 deferred-round）与59/64条动作逐项相等。原90字段比较也全部无差异。保留慢样本和GC暂停，原配置与实际申请分开记录。
- 最终政策合同 `2cfdd334274942abbe817d1c670faa07` Passed：两个真实固定lane、257槽位各一次、原始取消token/异常、在途排空、失败后复用及235,536字节成功/失败分配；Deep1000节点/641待命探针的DOP1/2完整结果/评分/动作/非时序指标相等，原根可复用。
- 短搜哨兵 `e7bae27e545942799fe3335c9673c6a6` Passed，与 `a85f3e7` 既有 `9664c01a18084c92afe111f9d47ce7e8` 的90字段/9动作一致。1GB NoGC `3f2ab070c3fa4f88bfe29f0cbe1ae173` Passed：52次重启、0丢失，与既有 `2345c8785fac4c8fb6d9b81e72aa43ae` 的90字段/64动作一致；请求39.96秒，未用跨批次时间计算提速。
- 最终Release零警告/错误；纯容量合同涵盖部分wave、零、奇数上限和精确饱和溢出算术；Bash/PowerShell门禁均 `REFACTOR_BOUNDARIES_OK search_files=83`。本轮没有最终Engine语义改动，Hook索引原型已撤回，未执行的索引专属合同未列为通过。
- 复跑使用现有 `STAND-PAT-PROBE-BATCHES` 政策合同、固定Short哨兵和正常首结果参数，详见[报告](../performance/perf2-integration-20260909.md)及[结构化结果](../performance/perf2-integration-20260909.json)。Profiler不进入倍率；未做本轮可见Steam、Windows游戏、完整部署或增量性能。

## 2026-09-09：回合结束探针与元数据热路径

- 第二组固定 VeryHigh/DOP8/NoGC16GB、B1–C1–C2–B2–B3–C3–C4–B4 全部八个正式结果 Passed：37.6927→25.4926 秒，平均耗时−32.37%，分配−17.14%，采样峰值 RSS−16.10%。全部90项逐项比较；唯一差异为 C2 的 `phase/deep_triggered`，源码证明由20秒耗时检查点派生，原比较和分类修正均保留。其余88项非时序字段与64条完整预测动作全部一致；正常 Deep 预算、主搜索/恢复工作量和选择数相同。第一组−24.74%的失败结果完整保留，未删慢样本；候选仍有约2.1秒GC长暂停。
- 当前候选 `5d194a22bcc8496c998ba396f9873e83` Passed：55 回调/1,670 Model、ForkBoundaries，包含完整类型/接收者顺序、碰撞与并发替换、默认关键字原生对照、无前段锚点回退、Fork 后卡牌变更、有效前段及 Power 投影保留和父分支隔离。新候选 Release 零警告/错误。
- 最终候选 Deep 固定1000节点 `8effcf7a2e804c52867335cf32e636aa` Passed（请求56.99秒）：实际641次探针，DOP1/2全结果/动作/评分/续用/非时序指标相同，双lane同步屏障、取消/异常身份和在途排空、部分工作唯一记账、原根复用。
- Knights Short 哨兵 `9664c01a18084c92afe111f9d47ce7e8` Passed，与基线 `a4683db7ba964e259533372424f94250` 的90项字段/9条动作一致。正常Deep1GB NoGC 基线/候选 `591767d49821417d90d7134ce000d764` / `2345c8785fac4c8fb6d9b81e72aa43ae` 均 Passed：90字段/64动作一致，63/52次回收后保持46,239展开、636,428转移；搜索56.9968/42.4920秒，请求81.80/67.33秒，均在120秒内。
- `dotnet run --project tools/RitsuTargetTypeLookupChecks -c Release` 23项通过：实际生产回调、AssemblyLoad/动态晚建失效、失败不发布、live旁路、弱所有权。`dotnet run --project tools/PowerAmountComparisonChecks -c Release` 8项通过：1,568组实际原生方法对照、getter调用顺序、未知IL/内部标签旁路，10万次分配9.6MB→0。
- 最终源码 Release 构建4.07秒，零警告/错误；Bash和PowerShell结构门禁均输出 `REFACTOR_BOUNDARIES_OK search_files=82`。
- 复跑命令、全部runId、撤回原型、线程/GC口径和未验证范围见[报告](../performance/standpat-and-metadata-20260909.md)及JSON。性能只来自正常headless，未运行本轮整场原生部署或可见Steam；增量回放不用于性能测量。

## 2026-09-09：已准入父节点内的动作与选择作业

- 固定 VeryHigh/DOP8/NoGC设置16GB、首结果停止、每请求120秒；预定A–B–C–C–B–A独立进程，每进程短搜预热一次后测短搜/正常配置。最终正常搜索39.4485/42.2771→36.0760/36.7188秒，均值−10.93%；原版首尾漂移7.17%，仅headless样本。32组探索及正式结果的83项非时序/非物理调度字段和59/64条预测动作一致。
- 最终 `d31a5136765943cda47a5200337310e5`：`SearchPolicySnapshot`、`ForkBoundaries` Passed。覆盖固定节点DOP1/DOP2动作/选择/评分/续用/工作与剪枝等价、DOP2实际并发≥2、根/分支隔离、512回放上限和首层准入保证；新取消/异常注入确认排空、原始token/异常身份、部分工作只记一次，随后复用原根完成求解。
- Release零警告/错误；`ExpansionBatchChecks` 8项通过，包含药水移交顺序、失败所有权和旧租约隔离；最终Linux结构门禁81个Search文件通过，Windows规则同步但未执行。完整配置、所有runId、原型、GC/RSS口径和复跑方法见[报告](../performance/admitted-expansion-jobs-20260908.md)及JSON。未运行本轮整场原生部署、增量性能或可见Steam。

- 最终机甲骑士短搜哨兵 `a4683db7ba964e259533372424f94250` 与本轮原版对照 Passed：83项字段及9条动作一致，3448展开/8796转移。1GB NoGC目标压力 `8fa51a5571924f4d9a888188ad2574fd` 与本轮原版对照 Passed：83项字段及59条动作一致，10000展开/144368转移/101808选择；两版均14次NoGC建立、13次回收后重启、0区域丢失，实际并发8。

## 2026-09-08：CPU 微架构与指纹计算

- 生产基线 DOP8/1/2/4/8 曲线同 10,000 展开/144,368 转移/101,808 选择；正常配置 PMU 请求 `e304caaff9954817ae0e07ca6969277d` Passed，40.3314 秒、平均约 3.02 搜索相关核，仍仅死亡路线。PMU 为含游戏/GC/JIT 的进程用户态窗口，非纯搜索。
- 指纹改写 ABBA 正式请求：`246844f6fc7b44a0b3f7487fe0ea68c5` / `b3c12fffa86246559cf99069e9500466` / `e9b9f16df72442a0ad0e67c045be0463` / `f84d9e08202049338620882443e4bcb8`，全部 Passed；90 项非耗时/非并发调度字段及 59 步预测动作一致，均值约 −1.91%，每版两个样本，不作显著提速结论。
- `dotnet run --project tools/StateFingerprintChecks -c Release`：200,005 个边界/混合输入逐步对照旧 128 位公式，通过。相同本地 FullOpts 标量体 107→83 字节；Release 零警告/错误。未测候选正常配置性能、整场原生部署、可见 Steam、Windows 或 IBS/DRAM/伪共享归因。完整配置与局限见[报告](../performance/cpu-microarchitecture-20260908.md)及 JSON。

## 2026-09-08：较大范围后端性能实验（全部撤回）

- 基线及五项原型均独立进程预热后正式短搜一次，12 个请求 Passed；正式同 10,000 展开/144,368 转移/101,808 选择及投影战损 3。基线 5.6707 秒，候选 5.7442–6.6062 秒；单样本没有明确提速，全部撤回，不计完整状态等价。
- 单条目与三条目 COW 字典各通过原生 Dictionary 对照：9 万次随机操作、32 分支、顺序/异常/枚举/比较器/键身份；三条目最终包含 Keys/Values 枚举失效时机。RNG 与无序牌堆合同草稿未构建或运行。
- 所有原型 Release 编译通过；恢复原生产源码后重新 Release 构建。没有保留行为变更，未追加正常配置 A/B、DOP/原生差分/整场/可见/Windows 测试。全部 runId、指标、范围和复跑输入见[报告](../performance/bold-backend-experiments-20260908.md)及 JSON。

## 2026-09-08：实测CPU热点驱动的路由聚合优化

- 正常配置A/B：`0c7c5b59ab2542ffa06ca40680856281` → `5a64a1d297cf4dcb8060aa69a0ded3cc`，均Passed；41.1816→40.6614秒，同46,239展开/636,428转移/431,140选择、90项非耗时/非并发调度字段及64步完整预测动作，仍仅死亡路线。短搜5.8670→5.6931秒；只有单组正式样本，GC差异明显，不保证倍率。
- 最终策略合同 `9eb710f1f0e44877aa4fd3bdae084ee7` Passed：250节点DOP1/DOP2动作、评分、续用、非时序工作/剪枝等价，并发至少2，取消与冻结策略通过。
- 最终整场 `1e8dec2b7eda4d83b2b851ddd7babd5e` Passed：Instant/0秒、战损8/57HP/T7/无药/零重算，27条实际动作与上一轮一致。最终Release零警告/错误、Bash/PowerShell结构门禁通过。
- 其余九项原型撤回；额外聚合缓存虽更快但4项内部指标不同，本轮未归因，不计等价通过。没有可见、Windows游戏、增量或发布验证。完整请求、输入、指标、撤回理由见[报告](../performance/backend-hotspot-optimization-20260908.md)与JSON。

## 2026-09-08：真实CPU热点与SwordSage根合同

- 根失败基线`08fae83abbc3476ab75c4c6ff6afdd84`确认捕获后live层数影响worker；仅根修复`630b137688304d3196be5c10a874c6bc`通过，最终`97c88a05ed9342528dda268da91836d6`通过首次移除、从零获得、分叉隔离、生成与幂等性，Release零警告/错误。
- Linux perf与EventPipe分开解释CPU、分配、GC和锁等待。正常极高两个诊断请求同46,239/636,428/431,140工作量，CPU调用链23,915个搜索样本；诊断耗时不作A/B，纠正旧线程采样口径。两个Hook索引原型撤回，零加成跳过未证明明显加速。
- 完整runId、最小合同命令、各原型数字、符号缺口和未验证范围见[报告](../performance/backend-cpu-hotspots-20260908.md)及JSON。没有新整场质量或翻倍结论。

## 2026-09-08：现有后端架构审查（诊断，不更换后端）

- 临时线程计数版单请求`ba7606885e0a4de4814747b5b22452c1` Passed：极高/DOP8，10,000展开/144,368转移/101,808选择、Short/NodeLimit、投影战损3。统计1.69亿过滤Hook位置检查、362万成员交出、三段归一化4380万牌访问。冷运行且有计数开销，耗时不与生产性能比较，不据此声称完整状态等价。
- 离线重分析已有25秒分配采样，区分Fork/回放/快照等互斥类别，非墙钟比例。临时Release通过后已恢复全部生产源码并停止独立实例；最终只提交审计文档。源码锚点、计数定义、后续最小合同及未验证事项见[架构审查](../performance/backend-architecture-audit-20260908.md)及JSON。

## 2026-09-08：极高配置状态与缓存实验（全部撤回）

- 基线及四项原型各独立进程冷预热后正式短搜一次：均Passed，10,000展开/144,368转移/101,808选择，投影战损3；正式5.9322/6.1887/5.9039/5.8503/5.8670秒。只有单样本，未证明明显加速或完整语义等价，全部撤回。
- 另做基线采样及一次完整回放重复率诊断，均不计性能倍率。没有保留行为变更，因此没有追加整场/DOP/可见回归；本次只提交记录。全部runId、输入、局限和固定工作量命令见[报告](../performance/veryhigh-state-experiments-20260908.md)及JSON。

## 2026-09-08：极高配置有界父节点队列

- 同一极高药水输入，两版新进程各一次短搜预热后测正常配置。基线`adc1976b425041a291e8e5989f132a0b`与最终`b160b6539cd44ed39ea8aa937b7e89c9`均Passed，45.660→39.915秒、RSS16.735→18.323GB；46,239/636,428/431,140工作量、64步动作/目标/选择及非时序搜索字段一致，仍仅死亡路线。单组正式数据，GC暂停不同，不宣称两倍。
- 最终`SearchPolicySnapshot`：`5eb810366694428eb44d15e7427c63ca` Passed；250节点DOP1/DOP2动作/评分/续用/非时序工作剪枝等价、实际并发≥2，取消记账和冻结设置合同通过。
- 最终完整哨兵`f272c7ba66994d93b811a9001237a709` Passed：战损8/57HP/T7/无药/零计划外重算，27条实际部署动作与上一轮一致，Instant/0秒。Release与两端结构门禁通过；没有可见、Windows游戏或增量性能。撤回实验、复跑命令和所有runId见[报告](../performance/veryhigh-parent-queue-20260908.md)及JSON。

## 2026-09-08：极高配置路由去重优化

- 目标 `a053d59f19074cac8a83644734fe62a0`：与压力基线同46,239展开/636,428转移/431,140选择、Deep/战损投影9/仅死亡路线；58.531→51.219秒、35.810→35.623GB。单样本且GC/短暂后台活动不同，未宣称严格倍率。
- 整场哨兵 `f4007c4a2da645e3b3cbbb22c4687eca`：Passed，战损8/57HP/T7/无药/零计划外重算，27条实际出牌与上一轮一致。
- Fork/路由合同合集最终通过（`7ae66da70a944340b8b1abcc3ad1cc9c`）；初次fixture缺牌组身份，补齐后遇到旧Power重获身份断言，原始基线同样失败。仅修正测试按既有规则要求新实例唯一注册和隔离，未改生产能力语义。Release零警告/错误及两端结构门禁通过。具体runId、输入、峰值和限制见[本轮报告](../performance/veryhigh-routing-order-20260908.md)及JSON。没有跑可见、Windows游戏或增量性能。

## 2026-09-08：极高配置其他战斗压力筛查

- 在`56165ed`上跑10组不同遭遇/牌组的极高短搜层，另对药水组合和灵魂枢纽各跑一次允许深搜的完整配置，共12个有效请求。固定DOP8/NoGC16GB，单请求120秒、首结果停止；按用户要求不测可见会话。本轮没有改生产代码。
- 死灵药水组合正常配置 `0f0f6eaee52b4967a39a771bc8026671`：Passed但仅死亡路线；58.531秒、35.810GB分配、独立峰值RSS16.845GB，636,428转移/431,140选择，3次NoGC重启，观测最长GC暂停1,745.901ms。
- 灵魂枢纽正常配置 `3635c085260f4d97b0254646ae93a284`：Passed，返回Short且未触发Deep；10.948秒、7.856GB分配、独立峰值9.473GB，预测战损6/T9/无药。
- 2305张牌堆 `955b322152c94c4383efad630bf34c6a`：Passed/TimeLimit，搜索50.210秒（请求94.679秒），277展开/3,390转移，12.381GB分配，约3.65MB/转移。女王生成/选牌 `06304b105d6c41978b61ed3c52765d52`：18.950秒/18.136GB，预测有风险，仅作压力探针。
- 同一30张牌组的感染棱柱/花园幽灵鳗/灵魂枢纽/外骨骼虫保留原生敌人HP与开局，短搜约3.586/0.152/3.200/1.470秒。初次错误使用runner默认1HP的四条`screen-*`数据全部排除；旧死灵白名单快照缺角色身份而建局失败，也不计性能。
- 新增明确注入的死灵牌/遗物/药水三个JSON，通过数量、字段与原投影一致性检查；原有启动器无需改动。没有做新一轮A/B、整场部署/原生差分、增量或Windows/可见性能验证。全部runId、指标、修正输入与复跑命令见[压力报告](../performance/veryhigh-pressure-survey-20260908.md)及其JSON。


## 2026-09-08：极高配置第二轮空回调与并行度优化（开发中）

| 验证 | 本轮直接证据 |
|---|---|
| 原版默认 Hook 分发合同 | `7c156ff511cd4581ad7a2748e3ddf7a2` Passed；54回调/1,670 Model，顺序、重复、外部类型、Fork模型身份、能力移除/重获、生成牌、根只读与补丁刷新 |
| 原生费用查询与嵌套选择 | 普通 `b3b157be5c204d51a887fbae08ee31e4` / 虚空形态 `6334a021b90e4387b056e864567cf28c` / pending `4fc3f48112b846f3889d6e99097b7f7b`，均Passed |
| DOP1/DOP2等价 | `63106d5c5894456ca03801bb485288ae` 的 SearchPolicySnapshot 完成：250节点、完整动作/评分/续用/非时序剪枝一致、实际并发≥2；随后原UI尺寸持久化失败，整个请求Failed，不计控制器合同通过 |
| 自动并行度与显式设置优先 | `4dfa3c784aae4241b7e6b8c4889a980e` Passed，CPU默认1/2/4/8及自动/显式设置解析 |
| 最终候选整场原生部署 | `39fae73f72f2477ba0663d511fab8ccb` Passed；46.75秒，战损8/57HP/T7/无药/零重算，Instant/0秒；27条实际出牌与上一轮同基线可见部署一致（仅质量对照） |
| 固定构建性能 | VeryHigh、DOP4→8、NoGC16GB；机甲骑士各预热一次+3正式样本，中位数13.3008→6.1037秒，34动作与83项非时序/非调度日志字段一致；大牌组压力/小啃兽42/14动作及相同字段一致 |

- 机甲骑士同战损8/T7/无药；压力场景仍是死亡边界，小啃兽零损/T3。压力/小啃兽速度2.69/1.79倍。机甲骑士分配+4.10%，每次峰值RSS中位数+10.79%，整批随后的小啃兽复用进程峰值+12.47%，不将分配量当成内存占用。冷搜索只有1.71倍；同4线程的代码收益约1.67倍。
- 启动器在首批未固定artifact的后续请求中切回默认DLL，`formal-a-*`作废；本轮正式表使用每次显式指定artifact的`verified-*`。构建0警告/0错误，两端结构门禁通过。合同复跑参数与全部runId见[第二轮报告](../performance/veryhigh-hook-dispatch-20260908.md)和结构化记录。
- 用户明确暂不测可见会话；本轮可见启动未得到性能结果，临时安装/原生设置已恢复。未跑Windows游戏、完整覆盖/发布门禁、逐转移增量搜索；上面的DOP等价使用固定节点生产搜索，不是增量模式性能。


## 2026-09-08：极高配置等质量性能优化（下一版本开发中）

| 验证 | 基线 → 候选 runId / 结果 |
|---|---|
| 双资源背包独立 oracle | `dotnet run --project tools/ReachableHandPotentialChecks -c Release` Passed：10,000随机子集最优解、原递推溢出/大容量边界、常见规模零分配 |
| HAND-POTENTIAL-COSTS 普通 | `be17b695fd114333baa185bc8e21ba1c` Passed：5张可打出牌费用与原生/重复查询相同，含X费用、条件/不可打出卡，根与分支完整状态不变 |
| HAND-POTENTIAL-COSTS 虚空形态 | `6bfe3e401e3e4a0b8b8dea7d259bdcc2` Passed：相同查询只读合同 |
| Steam 可见机甲骑士完整部署 | `2375d506a3ac455e94b6ccf9cefbed96` → `c2c1e8f6ee584fd1bbb5066b1bd98d83`，均Passed，战损8/T7/无药/零重算，27条实际出牌完全相同；初次搜索19.5643→18.7779秒 |
| Steam 可见小啃兽独立进程内存 | `eb56a536a72a4b1a904153f35eafbbc6` → `60f65ab1bddb410da8de39dcfe26fe12`，均Passed，预测零损/T3/14步路线一致；首结果停止，峰值RSS2,957,713,408→2,932,641,792B |

- 所有性能请求仅VeryHigh，固定DOP4/NoGC16GB，未调搜索预算，未启用增量或详细阶段诊断。Headless机甲骑士各预热一次后各取3次，中位数耗时−4.76%/分配−1.80%；大牌组压力与小啃兽各一组同工作量哨兵，非时序指标/动作完全相同。压力场景仍为死亡边界，不是胜利证明。全部runId及数值见[性能报告](../performance/veryhigh-quality-preserving-20260908.md)与其结构化记录。
- Release编译0警告0错误；Bash/PowerShell结构门禁Passed（`search_files=78`）；两端可见脚本语法检查Passed。PowerShell本轮仅语法及结构检查，未在Windows启动Steam。
- 费用合同的100ms收尾搜索不计性能，复现牌组为 `coverage/unattended/hand-potential-cost-cards.json`；命令见性能报告。可见基准新增 `--request-fixture-path` / `-RequestFixturePath`，完整请求为 `coverage/unattended/performance-veryhigh-mecha-native.json`，同报告记录首结果与整场部署两种断言。
- 完整Mod组合的基线在搜索前因未支持的AveMujica subscriber失败（`cd8e58d396344dec8e0b2362f216879b`，120秒超时）。成功可见结果只覆盖原版+RitsuLib+CombatSolver，未延长超时。基线整场采样器在退出时失败而缺少峰值文件，测试结果有效；其整场结束RSS已高于候选记录的VmHWM。Headless复用进程小啃兽曾有+0.07%峰值反向样本，独立可见进程对照未复现；不作全场景逐样本内存保证。

## 2026-09-09：可达手牌估值分配（开发中）

- `dotnet run --project tools/ReachableHandValueChecks/ReachableHandValueChecks.csproj -c Release`：32,551 组与原二维 DP 精确相等，包含空手牌、免费牌、双资源、较大数组回退、负可用资源、重复牌和价值和溢出。测试直接链接生产纯计算源码，原递推作为对照。
- 固定 100,000 次调用的局部分配检查：单资源受限 5,600,000 → 0 B；双资源受限 13,600,000 → 0 B；全部可负担 29,600,000 → 0 B。仅统计 DP 计算，不包含游戏模型、候选费用捕获和整个 Snapshot；不代表整场分配降幅或可见帧率收益。
- 当前可见会话的一条 RESULT 记录：40,105 次总转移、约 2.00 GB 总 worker 分配、2,424 ms 总搜索时间、最大主线程帧间隔 24.6 ms，未记录超过 33 ms 的帧或 GC 暂停。这是在线观察，未作为隔离 A/B 基准。
- 未启动额外游戏进程，未修改运行中会话；固定战斗搜索 A/B、增量回放和可见性能验收未执行。纯估值等价检查不代替这些项目。

## 2026-09-09：战前预测请求选项（开发中）

- 独立回归入口：`dotnet run --project tools/PreCombatRequestChecks/PreCombatRequestChecks.csproj -c Release`，Windows / Linux 相同。直接链接实际 `PreCombatForecastApi` 与 contract 源码，替换游戏/进程边界，不启动游戏。
- 覆盖强制重算绕过运行中/已完成结果、同选项去重、不同关闭/空闲要求分开请求、缓存命中应用关闭/有限或无限空闲期限、脱离式取消、独占取消等待清理、已取消缓存命中不改变设置以及 live 状态过期。
- 上述 10 项独立检查通过，Linux 结构门禁返回 `REFACTOR_BOUNDARIES_OK search_files=78`。同一测试链接修复前 API 时，强制重算用例因没有第二个 worker 请求而超时，缓存生命周期用例因未调用生命周期入口而失败。
- 完整 Mod 构建尝试返回 32 个 `CS0246` 缺失游戏类型错误（包含 `CombatTurnState`、`CardLocation`）；Windows worker 进程回收未验证：本机游戏为 `0.107.1`，仓库要求 `0.111.0`。独立 API 检查不代表游戏模拟或进程生命周期验收。

## 2026-09-09：历史敏感 Power 顺序（0.34.5）

- `MIXED-POWER-ACQUISITION-ORDER` 失败基线 `6eba58023b214ead8829ed5effbad6ee`：旧逻辑在已有小刀/攻击/格挡历史后临时停用并恢复 Power，首个差异为 `P[1] expected Strength actual PhantomBlades`。
- 最终 `8f5c8d96d9e342e8a2163e54e359c0d2` Passed，26.26 秒。夹具先在没有目标 Power 时各打一张格挡牌和小刀，再按代表报告顺序获得幻影之刃、力量、第二个轨道、敏捷、致死性和不动；继续各打一张牌后施加虚弱，完整比较生命、格挡、能量、牌堆、有序 Power、ContinuationStamp、Fork 和父分支隔离。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId MIXED-POWER-ACQUISITION-ORDER -EnemyCurrentHp 100 -HeadlessInstance unexpected-replan-power-order-final`。最终 Release 构建 0 警告 / 0 错误。
- 代表报告 `4b188b2e088c4826ba1b14d0252bd3bf` 只直接核验元数据与独立日志；没有整包恢复或正式搜索。其余 19 份按相同首个 Power 顺序差异和相同执行路径静态归组，不能表述为 20 份整场重放通过。

## 2026-09-09：狂战士回合开始自动出牌历史（0.34.5）

- 代表报告 `c68216599d1a439b972d4161a2135988` 的第 2、3 回合均为 `Y expected=0/0/0 actual=0/0/1`；独立日志确认狂战士在抽牌时自动打出 Strike。只读取代表包日志，没有整包恢复。
- `HELLRAISER-TURN-START-HISTORY`：`5bbb6f1081aa4206ae077b8aff733194` Passed，24.82 秒。从第 1 回合推进到第 2 回合，预测与实机都在抽牌前开始新的历史窗口；狂战士自动出牌后严格比较生命、资源、四个牌堆、有序 Power、Power 内部状态、RNG 与 ContinuationStamp，并显式断言本回合出牌开始次数为 1。
- 关联回归 `NORMALITY-AUTOPLAY`：`f014ee4080bd4191805e67096eef7e8b` Passed，10.51 秒，覆盖同一出牌开始计数的根捕获、Fork 隔离、下一回合归零和被阻止自动牌的牌堆结果。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId HELLRAISER-TURN-START-HISTORY -HeadlessInstance unexpected-replan-power-order-final`；回归只替换 ScenarioId 为 `NORMALITY-AUTOPLAY` 并加 `-EnemyCurrentHp 100`。Release 构建 0 警告 / 0 错误。
- 同组共 9 份 / 7 场；代表报告仅日志直接核验，其余按同一 `Y` 首差异与抽牌自动出牌路径归组。没有把正常 `manual_divergence` 纳入修复数，也没有运行正式搜索或逐包整场部署。




## 2026-09-08：凡庸与自动打牌（0.34.4）

- 原报告 `12f213c23ccd4a00abaf7a80c796273e` 的第 6 回合在发现、彼岸咆哮后打出倾泻，Normality 仍在手；原版结束回合复核为 25 HP，计划为 0 HP。日志定位后直接构造最小夹具，没有运行原包恢复。
- `NORMALITY-AUTOPLAY` 失败基线 `b331a29d04dc4587a797ad2c5074eb78`，23.63 秒：两张防御后打倾泻，模拟格挡 27、原版 10。修复后 `5df3975b9e874fb1a34f5f966dc7f54d` Passed，25.97 秒，比较完整 MoveStateSnapshot/ContinuationStamp，包含有序牌堆、资源、能力和 RNG。
- `NORMALITY-AUTOPLAY-REPLAY`：`21766a0d3d3149d485d09baf36885cf3` Passed，26.44 秒。第一张牌被回响形态重复打出，两次开始加倾泻开始达到三次；验证计数不以“手动动作数”或“已完成次数”代替开始次数。
- 两组还比较：凡庸阻止的自动牌去向、诅咒离手后允许自动牌、预测/Fork/原版重捕获的开始次数、子分支回合清零及父分支保持。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId NORMALITY-AUTOPLAY -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance normality -TimeoutSeconds 120 -ExitOnComplete`；第二组替换 ScenarioId 为 `NORMALITY-AUTOPLAY-REPLAY`。Linux 使用同名 `.sh` 与对应长参数。
- Release 行为构建、Windows 结构门禁及 CoverageCatalog 的 effective/state-fields/autoplay-sources 检查通过。未运行正式搜索、整场部署、原包恢复或可见 FPS/交互验收。

## 2026-09-08：卡牌语言往返刷新（0.34.3）

- `UI-LOCALIZATION`：`1e7e7f53067f4c34a1732b6c5b63c033` Passed，24.96 秒。新增英文已保存 PlanAction / UI snapshot，构造一次真实胶囊，依次切 zhs / eng / zhs，断言标题、升级符号、选牌、tooltip 与游戏译名一致；JSON 往返及旧计划内容保持原样。
- 同时验证销毁控件后订阅数量恢复、语言切换不改变搜索状态或计划外重算计数，并继续通过 eng/zhs/zht 的 321 条文案和 20 类遗物摘要等既有合同。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId UI-LOCALIZATION -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance i18n-refresh -TimeoutSeconds 120 -ExitOnComplete`；Linux 使用对应 `.sh` 与同值长参数。
- Release 行为构建 0 警告 / 0 错误；Windows 结构门禁通过。没有启动真实搜索或整场部署，没有可见交互/排版/帧率验收；只新增显示元数据，不改变模拟结算。

## 2026-09-08：胶囊附加信息本地化（0.34.2）

- `UI-LOCALIZATION` 扩展合同 `43bdbcdfc4eb4bf68c1bd65746412e44` Passed，24.58 秒；eng/zhs/zht 分别覆盖 321 条文案、20 个遗物摘要样本、毒/荆棘/能力规范 ID 与类型名/充能球/敌方行动/未知来源、嵌套选择与空选择、药水标记、遗物胶囊和 tooltip 一致性。
- 捕获显示名后切换实时游戏语言，再在 Task.Run 中读取名称，验证 worker 输出仍使用已捕获语言。第三方自定义摘要样本与纯倍数保持原样。未重跑伤害模拟或整场搜索，改动只涉及显示。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId UI-LOCALIZATION -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance i18n-annotations -TimeoutSeconds 120 -ExitOnComplete`；Linux 使用对应 `.sh` 与同值长参数。
- Release 行为构建 0 警告 / 0 错误，Windows 结构门禁通过。无实机交互/视觉验收或帧率测试。Steamworks 对 schinese、english 的两次描述更新各返回 EResult.OK；仅更新元数据，未上传二进制。

## 2026-09-08：简化中英双语 UI（0.34.1）

- `UI-LOCALIZATION`：`5c12f39867df4f75beb28db5a593e840` Passed，23.72 秒。覆盖 319 条资源的占位符/数字格式、嵌入文本保持原样、eng/zhs/zht 语言选择、英文设置和上传弹窗控件/全部下拉选项无中文、设置切页、上传完成/取消状态、动态路线标题和失败引导。没有发送公网上传请求。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId UI-LOCALIZATION -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance i18n -TimeoutSeconds 120 -ExitOnComplete`。Linux 使用对应 `.sh` 与 `--scenario-id UI-LOCALIZATION --encounter-id BYGONE_EFFIGY_ELITE --headless-instance i18n --timeout-seconds 120 --exit-on-complete`。
- 行为构建 0 警告 / 0 错误，Windows 结构门禁通过，双平台门禁同步禁止 Search 引用 SolverText；之后仅同步版本、文档和结构门禁。没有执行可见布局/交互验收、帧率测量或整场搜索，headless 合同不代表这些项目通过。

## 2026-09-08：战斗独立日志（0.34.0）

- `dotnet run --project tools/DiagnosticLogTests/DiagnosticLogTests.csproj -c Release` 通过：冻结提交前缀、跨战斗摘要化、旧 worker 会话隔离、跑局摘要隔离、积压上限与显式不完整状态。1 万次入队调用约 6.91 ms、93.4 B/次，峰值待写计费 2,817,000 B；仅进程内微基准，不代表实机帧率。
- `COMBAT-DIAGNOSTIC-LOG` 验证正常增量/根回放完整状态相等、人为注入 5 HP 差异后的首个动作定位、Damage/Heal 原生严格差分、失败候选日志与异常传播、独立日志 ZIP。`a5abab57628642d584802ba2d275bfce` Passed，23.23 秒；初次运行因旧归档测试仍要求最多一份全局日志而失败，更新到独立日志合同后通过。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId COMBAT-DIAGNOSTIC-LOG -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance diagnostic-log -TimeoutSeconds 120 -ExitOnComplete`；Linux 用对应 `.sh`、`--scenario-id`、`--encounter-id`、`--headless-instance`、`--timeout-seconds`、`--exit-on-complete`。
- Windows 结构门禁通过。未做可见游戏帧率验收、整场部署或公网上传测试；提交协议未变，此场只导出本地问题包。
- 最终合同 `6a1ff383efa84437a87b8eafc92843d1` Passed，23.37 秒，另覆盖正式搜索的最终路线物化；随后仅同步版本和文档。行为验证构建仍标记 0.33.9，发布构建统一为 0.34.0。

## 2026-09-08：在线监控登录持久化

- `node --test tools/OnlinePresence/test.mjs tools/OnlinePresence/pagination.test.mjs tools/OnlinePresence/history.test.mjs tools/OnlinePresence/session.test.mjs`：10 项通过。
- 会话接口验证同 IP 日志 Cookie 共存、重启后复用、14 天过期、退出撤销、修改密码失效；前端 VM 事件验证首次等待鉴权、成功直达后台、401 显示登录、网络失败重试。
- 未做交互级验收，未启动游戏；独立服务更新不需要 Mod 构建。

## 2026-09-08：旧日雕像缓慢跨回合分叉（0.33.9）

- `SLOW-TURN-RESET-FORK` 失败基线 `21070eeed4b84c628ae7ac7f6ebe1cdf`：敌方回合开始后 Fork 抛出 `SlowPower has no fork mapping`；最终 `2602bde5ac1c45f7af0344dc7cd6153a` Passed，24 秒。
- 严格比较完整 MoveStateSnapshot / ContinuationStamp：两次打击累积、敌方阶段清零、清零后分叉、再次打击伤害和动态变量；同时断言能力实例身份、状态指纹相等，以及清零和再次出牌均保持父分支隔离。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId SLOW-TURN-RESET-FORK -EncounterId BYGONE_EFFIGY_ELITE -ClearAllPowers -ClearPlayerPiles -CardsPath coverage/unattended/slow-turn-reset-fork-0339-cards.json -EnemyCurrentHp 500 -InitialPlayerEnergy 10 -TimeoutSeconds 120 -ExitOnComplete`。Linux 使用同名 `.sh` 和对应长参数。
- 51 份报告 / 40 场战斗的异常详情全部相同，只下载代表包 `c6e67f18952f4d1d9c9d3a4ac304b095`。Preflight 为 materials_valid；原生回放尝试 `0b29cb9ed6044ac9b702e53599111f18` 因 `environment_mismatch:mods` 被拒绝，restorationVerified=false。一次夹具启动使用了不存在的 BigDummy 遭遇名，修正为旧日雕像后取得上述基线。
- 行为源码 Release 构建 0 警告 / 0 错误。本轮没有运行整场自动部署、增量搜索、可见 Steam 或完整发布门禁；最小生命周期差分未启动搜索。

## 2026-09-08：问题包 v2 与 miaovps（0.33.8）

- `REPORT-V2-CONTRACT` 最终 `932296db26ac4e4ebb7b1432e823fddd` Passed，23 秒。覆盖真实 ZIP 导出、新目录与检查点材料、主线程战斗/角色/怪物元数据、未知和正/零/负战损下降值、multipart 字段、响应与取消边界、正式 HTTPS 证书校验上传及回执。命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-V2-CONTRACT -HeadlessInstance report-v2 -TimeoutSeconds 120 -PreserveNativeCombatStateForTest -ForceShortSearchOnly`。
- 前一轮 `2a434cb1ea244452993488ee635f89b8` 上传合同通过；状态注入包预检明确返回 diagnostic_only:test_fixture_state_injection，不算恢复有效。改用保留原生状态后，新包 `bc43f15bc9234456a1d82ddf52eaa8d8` 的 preflight 为 materials_valid / restorationVerified=false。
- `CheckpointTool self-test` 29 项通过，覆盖旧目录、无索引、材料配对、路径拒绝和批量去重；新实际包路径由上述 preflight 验证。没有执行整场恢复或可见游戏交互。
- 独立后端 `python -m unittest -v test_reports_v2` 6 组通过：单次及并发去重、身份冲突、元数据与 ZIP 一致性、非法包清理、旧上传与管理鉴权、组合筛选及批量清单/删除一致性、未知/零/负差值和范围校验。
- 公网后台按真实 Mod 包元数据组合筛选及鉴权下载字节一致通过，测试报告删除。后端用户服务已更新；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=77`，行为源码 Release 编译 0 警告 / 0 错误。
- 列表按后续要求移除接收时间、联系、大小、已解决和备注列，改为横向元数据列、全宽页面与两行描述。新增列结构测试，并重跑受影响的组合筛选/批量和旧包鉴权测试，共 3 项通过；公网 HTML 已确认更新。未执行浏览器交互或像素验收。

## 2026-09-08：高频计划外重算（0.33.7）

| 场景 | 失败基线 runId / 差异 | 最终 runId / 结果 |
|---|---|---|
| MIXED-POWER-ACQUISITION-ORDER | `b8a4e65825d5470b887a535e7d2ad399`，P[1] Strength / PhantomBlades 顺序不一致 | `cb0fb008d094436fbb611ec62dbc1689`，Passed，24 秒 |
| REMOVED-POWER-REAPPLICATION | `c4cccbfbd04e4d3fbe0dc5dabd8a6b48`，DrawCardsNextTurn 的 AmountOnTurnStart 预测 5 / 原生 0 | `7cb00c33817b4521af540c868f187376`，Passed，8 秒 |
| SUMMONED-ALLY-POWER-ORDER | `a4659ef9aa474e0396b38667fbeeed7e`，敌方 Strength 在奥斯蒂 DieForYou 前面 | `7f93620fda4d4219a5bc49dc2ce37252`，Passed，8 秒 |
| ENERGY-RESET-POWER-ORDER-REAPPLY | 既有相邻回归 | `98d8b8e3ec1e497dbcbcfa74d77e98d5`，Passed，12 秒 |
| SUMMON-DEATH-POWER-ORDER | 既有敌方召唤/死亡两回合回归 | `78120e49e44449f494f275173edd57f5`，Passed，18 秒，T1→T3 |

- 前四场命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId <场景> -ClearAllPowers -ClearPlayerPiles -EnemyCurrentHp 500 -HeadlessInstance report-fixes -TimeoutSeconds 120`。最后一场使用 `-ScenarioId SUMMON-DEATH-POWER-ORDER -EncounterId FABRICATOR_NORMAL -EnemyCurrentHp 500 -HeadlessInstance report-fixes -TimeoutSeconds 120`。
- 比较完整 MoveStateSnapshot / ContinuationStamp；混合能力夹具覆盖多实例与普通能力交替获得、同类多个实例、Fork、子分支移除后重获及父分支隔离。既有重获回归还检查指纹与实际能量重置 Hook，召唤回归覆盖原生跨回合状态。正式搜索未启动，未开启增量搜索验证。
- 最终行为源码 Release 构建通过，0 警告、0 错误；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=77`。一轮启动因子进程未暴露 executable path 失败，未进入行为断言，重新启动后取得上表证据。基线期间一次并发启动被实例锁拒绝，后续请求全部串行复用同一实例。
- 188 份报告去重为 179 场，三类症状分别匹配 98/5/3 场；计数不是逐包通过率。外层 Preflight 超大小限制；拆分代表包 Preflight materials_valid / restorationVerified=false。未执行原包完整恢复、整场自动部署、可见 Steam 验收或完整发布门禁，详见 [分诊](../issues/report-replans-20260908.md)。

## 2026-09-08：Issue #63 局外收益评分上限（0.33.6 开发中）

- `LONG-TERM-RESOURCE-BEAM-CAP` 失败基线 `08689d31bcec4f6ea3bb90ac40cd94d1`：25 点资源加分 625,000，额外自伤 1 HP 后仍高于父状态 565,000 分。
- 最终 `1dc49c905e36411781d7bf0f1ea66b74` Passed，22 秒。直接调用正式 Snapshot，对 1/25/1000 点资源分别比较无伤与自伤分支，验证总加分小于单项 HP 权重、真实资源值保留且未生成成长额度。
- `GROWTH-POLICY-FREE-FIRST`，`e373a06013e24124a17d1f1f3caa375e` Passed，21 秒；`GROWTH-POLICY-PAID`，`d9aca404d377479aa52d334e2f1ed10e` Passed，21 秒。覆盖免费成长优先、零额度拒绝额外战损、允许额度内付费成长、忽略收益开关、第三方额度合同与增量回放。时间来自测试模式，不用于性能结论。
- 新合同命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId LONG-TERM-RESOURCE-BEAM-CAP -HeadlessInstance issue63 -EnemyCurrentHp 100 -TimeoutSeconds 120 -ExitOnComplete`。两项成长用例沿用本文已有命令并加 `-ExitOnComplete`。
- Release 编译通过。Issue 附件仅 Preflight materials_valid，未完整恢复观者 Mod 跑局、未证明该包原先 21 HP 差距已消除；未跑全角色/全量性能基准，不声称有限搜索绝不漏解。0.33.6 保持未发布。

## 2026-09-08：PR #59–65 整合验证（0.33.6 开发中）

- 合并 #59、#60、#61、#62、#64、#65；#63 是 Issue。本节记录本轮直接证据，下方各 PR 原始记录保留其提交时的验证范围。
- Release 构建通过，0 警告、0 错误；结构检查通过，`REFACTOR_BOUNDARIES_OK search_files=77`。
- `GROWTH-POLICY-FREE-FIRST`，`4da884fe105d41a7865ff403988f392b` Passed，22 秒。覆盖第三方成长来源登记、额度/设置往返、Fork、侧栏总开关及免费收益搜索；整合后第三方额度与原版额度共同置灰。
- `PR60-65-CONTRACT`，`348d44e4a0b24db39cbd712f3570e5a0` Passed，22 秒。直接运行死亡补货胜利判定与第三方移除偏置合同，不扩跑整个 Fork 批次。
- 中间运行 `20f2a7b0333444609de93c417ffdf311` 的补货检查已通过，移除测试失败：替身牌通用估值 12 加 -10 为 2，原断言要求负值。测试偏置改为 -20 后通过，生产估值逻辑不变。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId PR60-65-CONTRACT -HeadlessInstance pr5965 -EnemyCurrentHp 100 -TimeoutSeconds 120 -ExitOnComplete`；成长夹具沿用下方 FREE-FIRST 命令并加 `-ExitOnComplete`。
- 本轮未重跑 API v6 的隔离 worker 样本，未跑节点预算的长循环性能基准、付费成长夹具或完整发布门禁；不能将作者历史证据视为本轮通过。未创建标签、发布包或上传工坊。

## 0.33.6 跑局统计

- 服务端 8 项测试通过，增加非战斗但 inRun=true 计入、inRun=false 排除、旧客户端缺失状态及非法字段拒绝。
- ONLINE-PRESENCE-CONTRACT：`5e4e6e2222d94e578103f46821fc3e58` Passed，22 秒；验证原生跑局标志与缓存时保留当前状态、TLS 与关闭持久化。未逐一自动进入地图/商店/事件界面。
## 第三方局外成长来源登记入口（开发中）

- Release 编译（`-p:CopyModOnBuild=false`）0 警告 0 错误，未复制到游戏目录。
- `GROWTH-POLICY-FREE-FIRST` / `GROWTH-POLICY-PAID` **本轮未执行**。新增断言 `AssertThirdPartyGrowthSources` 挂在这两个夹具原有的 `-VerifyGrowthPolicy` 路径上，复跑命令沿用本文《2026-09-07：局外成长策略》一节记录的原命令，不需要新参数。
- 已单独验证 `GrowthValues` 的 System.Text.Json 往返机制（record struct 定位构造函数加 `init` 属性、`JsonIgnore(WhenWritingNull)`、字典键不受 `PropertyNamingPolicy` 影响）：空表不写出 `thirdParty` 字段、有条目时往返相等并保留未登记 id、缺字段与显式 `null` 都还原成空表、`with` 表达式保留第三方部分、第三方条目参与相等判断。该验证在独立控制台工程完成，不进仓库。
## 不考虑局外收益开关（开发中）

- Release 编译（`-p:CopyModOnBuild=false`）0 警告 0 错误，结构门禁通过。
- `GROWTH-POLICY-FREE-FIRST` / `GROWTH-POLICY-PAID` **本轮未执行**。新增断言直接加在这两个夹具原有的 `-VerifyGrowthPolicy` 路径上，复跑命令沿用本文《2026-09-07：局外成长策略》一节记录的原命令，不需要新参数。新增覆盖：开关默认关闭、设置往返、原始额度保留而 `EffectiveGrowthBudgets` 归零、`EffectiveHasGrowthTargets` 归假并让可接受战损早停重新生效、开着开关求解仍然取胜、成长信用为零而快照其余两项保持如实（偏好开关不是状态开关）、战损不超过零额度基线、付费夹具下战损严格小于满额度那次、侧栏开关回读与额度置灰、点击开关翻转。
- 未做原生实机验证、未跑 248 条原版回归、未执行完整发布门禁。
## 节点预算按回合层分配（开发中）

- Release 编译（`-p:CopyModOnBuild=false`）0 警告 0 错误，结构门禁通过。
- 玩家实机复现材料：`CONSTRUCT_MENAGERIE_NORMAL-20260907-170814`（回合层停在 2、`play_depth` 173→248、`ended` 99845、`repeatable_no_progress_pruned=0`、无任何 `RESULT`）与 `CONSTRUCT_MENAGERIE_NORMAL-20260907-173454`（手动降到 `deepMaxExpandedNodes=12000` 后正常收敛，3 回合、战损 6、`boundary=NodeLimit`）。
- **无人测试未跑**：`tools/run-watcher-matrix.ps1` 与观者相关夹具开头会 `Stop-Process SlayTheSpire2`，用户正在实机测试，不能执行。本项由用户实机验证：看 `TURN_LAYER_BUDGET reason=nodes` 是否出现、搜索是否收敛。
## 第三方起手牌移除估值登记入口（开发中）

- Release 编译（`-p:CopyModOnBuild=false`）0 警告 0 错误，结构门禁通过。
- 新增 `AssertThirdPartyBasicCardRemoval`，挂在既有卡牌选择挂起用例路径上：登记表初始为空、未登记的牌排序键高于原版起手打击、登记为起手打击后排序键真的下降、重复登记与未定义类别抛错、原版写死的表不被登记表改写、撤销登记后排序键复原。
- **无人测试未跑**：相关脚本开头会 `Stop-Process SlayTheSpire2`，用户正在实机测试，不能执行。实机由用户验证（净化会不会开始优先烧观者打击）。
- 未做 248 条原版回归、未执行完整发布门禁。

## 0.33.5 受伤历史与攻击次数

- `TURN-START-DAMAGE-SPITE` / `THE_OBSCURA_NORMAL`：失败基线 `6fa9c33e5aca495cad4c1c0a8b662c95`，T2 怨恨后 E1.hp 预测 86、原生 81；最终 `18a3b15c05f94038a6cfb080fe41ef9c` Passed，28 秒。覆盖回合开始 Inferno 自伤后的双次攻击、召唤阵容、完整状态和 Fork，以及伤害记录不泄漏到敌方/额外玩家回合。
- `TEAR-ASUNDER-DAMAGE-HISTORY`：失败基线 `29bcf598a611426aad1d1a90c9810975`，两次根受伤和一次分支回合开始受伤后，预测 86、原生 71；最终 `0fefaa871ab344008481d54e8de10a23` Passed，29 秒。精确验证扯碎四次攻击、完整原生状态与 Fork。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId TURN-START-DAMAGE-SPITE -EncounterId THE_OBSCURA_NORMAL -EnemyCurrentHp 100 -HeadlessInstance obscurafix -TimeoutSeconds 120 -ExitOnComplete`；另一场替换 ScenarioId 为 `TEAR-ASUNDER-DAMAGE-HISTORY`。
- 初始两次夹具尝试 `50e220ad84fc4deebd62fb70189905f4`、`242abeca01374dafb2d42fc937ee1efb` 使用默认 1 HP 敌人，回合开始伤害已结束战斗，未到目标断言；修正回放参数并设置 EnemyCurrentHp=100 后取得有效基线。一次编译缺少测试 ValueProps 引用，补齐后成功。
- 两包 Preflight 为 materials_valid、restorationVerified=false。制造者包声明 0.33.2，顺序差异复用 0.33.3 同根证据，本轮未重复整场测试。未做完整发布门禁或整场零重算结论。

## 0.33.4 在线战斗缓存

- 服务端 8 项测试通过；新增旧协议空心跳保留、0 损保留、新战斗尚未算完时整组保留、完成后整组替换、新协议状态/采集时间、累计时长与超时清除。
- `ONLINE-PRESENCE-CONTRACT` / `FOGMOG_NORMAL`，`84c8fdb51f67414c9cc74355f558f83b` Passed，23 秒。验证原生标量采集、纯缓存转换的非战斗/待计算/新结果/首次空值、关闭持久化、真实 HTTPS 和错误指纹拒绝。仅向正式采集端发送空对象验证 400，没有注入玩家数据；未跑完整战斗生命周期。
- 本地 Playwright 验证缓存敌人及 0 HP、首次计算占位、真实战斗人数为 0、采集时间提示、桌面与 390px 手机布局，无页面异常。
