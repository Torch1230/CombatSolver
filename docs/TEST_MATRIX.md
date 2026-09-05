# CombatSolver 测试清单

> 基线：CombatSolver `0.28.3`（当前创意工坊稳定版）、塔 2 `0.111.0`、RitsuLib 实测 `0.5.18`（清单最低 `0.5.13`）、CombatSolver 内置战斗模拟引擎。无人测试运行隔离的原版 `--headless` 游戏进程，不使用自建 STS CLI；性能最终门槛另由 Steam 可见会话验证。完整战斗基准使用 `Instant / 0 秒` 部署。

单项启动器未请求退出时会保留各平台 marker 精确持有的 headless 游戏进程，供后续身份兼容的请求复用；完整矩阵始终遵守文档命令声明的有界生命周期组。两端都核对请求与实际可执行文件、进程启动身份、隔离数据目录以及 Mod DLL/manifest 的 SHA-256，而不仅依赖 PID；Linux 还通过 `/proc` 核对 starttime 和进程环境。重编译后会安全重启，不会复用内存中的旧程序集；marker 损坏、来自旧协议或无法证明已失效且可能仍有活进程时封闭失败，保留现场并拒绝冒险接管。Windows 通过独立 `APPDATA / LOCALAPPDATA`、Linux 通过独立 XDG 数据目录隔离测试数据；两端都关闭 Steam，只在隔离设置中确认允许加载 Mod，并在 headless 生命周期内临时投影对应平台创意工坊中的 RitsuLib。只有当当前请求的异步工作静稳、主线程稳定并收到匹配 `schemaVersion/runId/held` 的 ready ACK 后，启动器才会复用进程；任何 `Failed`、静稳/ACK 超时或中断都会清理已精确认领的进程。Linux Bash 启动器默认把测试内游戏速度设为 `Instant`，可用 `--headless-fast-mode-for-test` 覆盖；Windows PowerShell 启动器保留既有默认值，可用 `-HeadlessFastModeForTest Instant` 显式启用。同一战斗能容纳的行动继续合并到一个批次夹具中连续执行。

维护时默认使用分层快速回归：普通语义改动跑单效果严格差分；Fork、跨回合历史和续用改动补一个最小两回合或最早复用边界；搜索/部署改动的最终候选才运行必要的完整自动场。快速 unattended 请求总超时不超过 `120` 秒，超时后缩小 fixture 或记为未验证，不在同一轮延长等待。下方完整矩阵是发布门禁和专项审计入口，不是每次修复都要执行的默认清单。

## 0.30.0（开发中）：2026-09-05 玩家更优世界线策略第一批

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `RELEASE-BUILD-0300-ABILITY-ENGINE` | 通过 | `dotnet build CombatSolver.csproj -c Release`，CombatSolver 与 MemoryCleaner 均成功生成，0 警告、0 错误。 | 2026-09-05 |
| `PLAYER-WORLDLINE-TEST-SUBJECT-133704-ABILITY-ENGINE` | 通过，优于人工同状态基线 | 清单第 3 条有备注实验体包，复用玩家完成首轮准备后的第 2 回合状态，在 High/DOP1 下预计剩余战损 `0`，人工同状态剩余战损 `3`，相对人工 `+3`；最终完整搜索耗时约 `49.23 s`，runId `d538dcebac0543f4b93ed28ff0e95704`。此前首回合 `9` 与整场人工 `3` 的比较不属于同一状态，已排除。 | 2026-09-05 |
| `PLAYER-WORLDLINE-AEONGLASS-153416-DYNAMIC-POWER` | 通过，优于人工同状态基线 | 清单第 1 条有备注永世沙漏包，恢复报告 Power 动态变量后，复用第 3 回合状态并使用报告原本的 High/Force 药水约束，预计剩余战损 `3`，人工 `6`，相对人工 `+3`；搜索进入 Deep，runId `5d768646dbb641ea967de0349a18c9ef`。 | 2026-09-05 |
| `PLAYER-WORLDLINE-AEONGLASS-152708-POWER-ORDER` | 通过，优于人工同状态基线 | 清单第 2 条有备注永世沙漏包，恢复第 7 回合状态时先清理多余 Power 并恢复 SOWN 附魔字段，High/Force 预计剩余战损 `0`，人工 `55`，相对人工 `+55`；runId `2a65b5cb43b846809f31838c8e71f7ed`。 | 2026-09-05 |
| `PLAYER-WORLDLINE-AEONGLASS-151202-POISON-HORIZON` | High 未追平，VeryHigh 已追平 | 清单第 4 条有备注永世沙漏包，复用玩家完成第 3 回合后的第 4 回合状态；High/Force 预计剩余战损 `2`，人工 `0`，相对人工 `-2`，runId `fbc9fe2a11a7416abee9d1a542c15931`；VeryHigh/Force 找到 `0` 战损路线，runId `e91590c716aa40548e02eb7931c96d17`。按 Baseline 规则继续修复。 | 2026-09-05 |
| `PLAYER-WORLDLINE-IMPORT-BASELINE-0300` | 部分解除 | 清单第 1、2 条已完成同状态复测，第 4 条已通过正确角色和人工前置回合导入但 High 仍有 2 HP 策略差距；第 5 条缺少回合卡牌历史。导入阻塞与策略缺口分开处理。 | 2026-09-05 |

## 0.29.1（定版准备）：2026-09-04 19 点后问题包硬逻辑修复

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ISSUE-20260904-AFTER-1900-TRIAGE` | 已整理 | 5 批下载包共 173 条记录，单列计算失败、回合准备选牌、部署中止、计划外重算，并重点标出 26 条带玩家备注的原始证据。 | 2026-09-04 |
| `TURN-START-ORDER-0291` | 通过 | Power/遗物回合准备顺序、能量重置顺序和 Sly 嵌套选择保持原版时序；`FIX-141700-TURN-START-CHOICE-FIXED` runId `b366e89a3e1b4f6eb11744430aa66348`，Toasty 回归 runId `4364f489957f49bca864d69ffd708201`。 | 2026-09-04 |
| `KNOWLEDGE-DEMON-NATIVE-CHOICE-0291` | 通过 | 结束回合后原生知识恶魔选牌仍由部署会话驱动，观测 `MIND_ROT_POWER`；runId `8ed16a06900c48ddb048fa89d2fe6fe6`。该夹具最终只有死亡路线，不作为整战质量证据。 | 2026-09-04 |
| `CARD-DERIVED-STATE-IDENTITY-0291` | 已修复，待专用复跑 | 动作身份键与状态指纹排除派生 `CalculatedVar`，针对 `NO_ESCAPE`、`UNLEASH`、`COMET` 的问题包空手牌异常已完成根因修复。 | 2026-09-04 |
| `SPITE-REPEAT-DYNAMIC-VAR-0291` | 已修复，待专用复跑 | `Spite` 缺失 `Repeat` 时按原版固定公式和升级等级恢复重复攻击次数，针对 `Repeat` KeyNotFound 问题包完成根因修复。 | 2026-09-04 |
| `ROOT-HOOK-NULL-0291` | 已修复，待专用复跑 | 根监听器快照过滤空项，针对 Queen `BeforeAttack` 钩子中的 NullReferenceException 完成根因修复。 | 2026-09-04 |
| `POTION-SLOT-DRIFT-0291` | 已修复，待专用漂移夹具 | 部署前药水槽位为空或内容不符时转入 `DeploymentDrift` 重算；未使用宽泛异常吞掉真实执行错误。 | 2026-09-04 |

## 0.29.0（历史开发记录）：2026-09-03 问题包硬逻辑修复

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ISSUE-20260903-MANGLE-COW-FIX` | 通过 | MANGLE + SLITHER 问题包在 `VerifyIncrementalSearch` 下完整回放通过；增量/完整回放的费用 RNG、牌面状态和搜索结果一致。runId `f25dee83d9ba4b039318a4cdef20fbe3`。 | 2026-09-04 |
| `ISSUE-20260903-MANGLE-CARD-DIFFERENTIAL-FIX` | 通过 | MANGLE 单卡严格差分，验证抽牌后带 SLITHER 的攻击牌仍可正确打出并产生预期效果。runId `b7e5d32dff794ef695c93dbc3123b689`。 | 2026-09-04 |
| `ISSUE-20260903-QUEEN-HP-MISMATCH-CURRENT` | 通过 | 复用 `0.28.2` Queen 遗物标注问题包的精确根和 RNG；当前源码短搜完成最终路线物化，没有再次出现 HP 标注回放差异。runId `22f3789304284eb49662d378361940e2`。 | 2026-09-04 |
| `ISSUE-20260903-QUEEN-HAZE-MISMATCH-CURRENT` | 通过 | 复用 `0.28.2` Queen/Haze 问题包的精确根和 RNG；当前源码完成短搜并返回候选，没有再次出现卡牌状态标注差异。runId `5bfc15213c444b449161c1b216ab256d`。 | 2026-09-04 |
| `ISSUE-20260903-SPITE-REPEAT-CURRENT` | 通过 | 当前原版卡牌严格差分覆盖失血后 Spite 的重复攻击语义，13 个动作检查全部通过。runId `cd97d0160dec44d3b35cae9b05ca328f`。 | 2026-09-04 |
| `ISSUE-20260903-DEPLOYMENT-DRIFT-RECOVERY` | 已修复，待实时漂移夹具 | 部署时普通计划手牌缺失现在归类为 `DeploymentDrift` 并重新捕获当前根；保留真实执行失败的显式错误。现有 Fork/部署身份边界通过，尚缺在可见游戏中先改动手牌再部署的专用夹具。 | 2026-09-04 |
| `ISSUE-20260903-OBSCURA-SPAWN-CURRENT` | 通过 | 原生 `THE_OBSCURA_NORMAL` 生成路径短搜覆盖 8 回合和 3 次洗牌；生成新怪物前固定敌人列表快照，没有再次出现 `Collection was modified`。runId `faeec097dee647af8453f9aa00f2c6ab`。 | 2026-09-04 |
| `ISSUE-20260903-KNOWLEDGE-CURSOR-FIX` | 代码修复，场景未通过 | 结束回合部署不再把 `ApplyKnowledgeCurse` 放入原生选牌游标；当前默认知识恶魔建局只有死亡路线，等待战斗结束超时，因此不记录为行为通过。已有 `PR29-KNOWLEDGE-CURSOR` 结构回归覆盖相同过滤边界。 | 2026-09-04 |
| `ISSUE-20260903-NATIVE-CHOICE-DRIFT-RECOVERY` | 已修复，待可见漂移夹具 | 原生选牌候选/页面生命周期不一致现在关闭当前页面并请求 `DeploymentDrift` 重捕获；确认按钮等待布局完成后再提交。尚未有专用可见页面先漂移再重捕获的 unattended 证据。 | 2026-09-04 |
| `ISSUE-20260903-NATIVE-CHOICE-PLAN-SEQUENCE` | 已修复，部分通过 | 原生选牌驱动器在收到计划外请求、计划提前结束或页面要求数量变化时报告选择计划漂移，并由部署层关闭页面后请求 `DeploymentDrift` 重捕获；重复计划仍显式失败。`SCULPTING-STRIKE-CHOICE-151` 严格增量回放和第 2 回合复用通过，runId `a35708eb3bae4aa49ef7769230d59bfa`。 | 2026-09-04 |
| `ISSUE-20260903-DEPLOYMENT-TURN-DRIFT` | 已修复，待专用时序夹具 | 部署动作检测到玩家回合已结束时现在清理旧路线并按 `DeploymentDrift` 重捕获，不再记为自动执行失败；其他部署异常仍显式失败。 | 2026-09-04 |
| `ISSUE-20260903-PENDING-CHOICE-HOOK-BOUNDARY` | 已修复，待双监听器夹具 | 洗牌 Hook 在已有待处理选择时停止继续调用监听器，分支消费后再继续；避免同一模拟事件创建冲突选择。 | 2026-09-04 |
| `ISSUE-20260903-NATIVE-CHOICE-SURFACE-TIMEOUT` | 已修复，待页面消失夹具 | 原生选牌页面或确认按钮等待超时现在按页面漂移关闭并请求 `DeploymentDrift` 重捕获；非原生等待超时仍走原有失败路径。 | 2026-09-04 |

性能指标口径：`selected_*` 只描述最终选中的单个 solver；请求级 `total_expanded_nodes / total_transitions / total_choice_branches`、`total_solver_ms`、分配与 GC 累计对正常、失败和取消的每个 solver 工作区间精确记录一次，包括取消前已发生的部分工作。Smart 有限药水层之间由 coordinator 主动执行的内存整理也计入时间、分配与 GC，但不增加 solver 数；建立开局、层间比较等其他编排工作仍不在这些总值中。因此端到端耗时以请求/阶段外层墙钟为准，峰值内存以进程 `VmHWM` 为准。Smart 多层的取消时点可能令请求总工作量小幅波动，语义验收优先比较胜负、战损、回合和动作路线。峰值工作集是瞬时进程峰值，不能跨阶段相加；`16 GB` NoGC 是运行时请求预算，不等于实际占用或硬上限；NoGC 活跃时 `GC.GetTotalMemory(false)` 不是严格 live-set 测量。

## 0.28.3（已发布）：战损停止与路线信息

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ACCEPTABLE-BATTLE-HP-LOSS-THRESHOLD` | 通过（headless 控制器/UI/搜索生命周期，DOP1） | 设置页阈值 JSON 往返、默认 `0`、完整胜利且预计本局战损 `<=` 阈值才触发早停的边界断言通过；独立短搜在允许范围上限下返回满足阈值的完整胜利路线。runId `aca83616becd422e99558a0d07b970d0`。 | 2026-09-03 |
| `KILL-SOURCE-ANNOTATION` | 待实机确认 | 最终路线回放记录卡牌、药水、毒、荆棘、能力、遗物和球等击杀来源；直接移除仍标记为未知效果，召唤/重建敌人可保留目标名称。Release 构建和 headless 生命周期通过。 | 2026-09-03 |
| `GREMLIN-MERC-PRESERVE-RESOURCE-TARGET` | 待实机确认 | 保钱策略在胖地精携带被盗资源且暂无攻击威胁时保留追回资源的动作分支，避免资源携带者逃跑。Release 构建和 headless 生命周期通过。 | 2026-09-03 |

## 0.28.2（已发布）：搜索热路径与 NoGC 回退

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR37-HOT-PATH-ALLOCATION-A/B` | 贡献者 A/B 通过 | 固定机甲根在 Windows 与 macOS 上保持工作量、路线、评分和战损不变；Windows worker 分配 `7.40 GB → 6.20 GB`，macOS 分配 `10.40 GB → 9.44 GB`，两端搜索时间均下降。 | 2026-09-03 |
| `PR38-NOGC-FALLBACK-PARALLELISM-A/B` | 贡献者 A/B 通过 | macOS 不支持 NoGC 时保持固定工作量与结果，实际并发 `2 → 8`、耗时 `37.2 s → 27.2 s`；Windows 正常 NoGC 路径无可测差异。合并态策略断言覆盖系统余量回退保守并发与普通平台/尺寸回退完整并发。 | 2026-09-03 |

## 0.28.1（已发布）：Smart 药水门槛与手动深度释放系统内存

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `MANUAL-SYSTEM-MEMORY-RELEASE` | 待实机确认 | 主界面内存条右侧提供固定入口；等待搜索退出后压缩托管堆并修剪游戏进程工作集，UAC 辅助程序清空系统工作集与待机列表，不清空修改页列表。 | 2026-09-03 |
| `GENERIC-SMART-POTION-SAME-LOSS-CONSERVE-V0111` | 通过 | 无药零损获胜时，付费药即使更早结束也不绕过每瓶 `9 HP` 门槛；结果为 `24/52` 展开/转移、`0` 药、`0` 战损、T3，且未打出卖血牌。runId `c91f29fcaa9a40129e6f67adfe06a8b9`。 | 2026-09-03 |

## 0.28.0（已发布）：通用周期与跨回合收益

> 这里记录基于上游 `0.27.2` 的最终定向与性能证据。生产算法只使用控制形状、精确动作相位、分支相对 stand-pat 状态和通用收益向量；fixture 中的卡牌、药水或遗物名称只是输入，不是生产特判。周期识别窗口最多 `32` 个动作；跨回合基础观察期为 `max(16, 两个完整牌堆周期所需回合)`，语义变化探针最多 `64` 次回合转移，最近一回合确有通用改善的探针最多 `128` 次。命中节点上限的场景只证明预算内找到路线，不称穷尽或数学全局最优。

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `AFTER-STARS-GAINED-ENGINE-MIRROR-0111` | 通过 | 通用 `GainStars` 在状态变更后分发 `AfterStarsGained`；5 层黑洞配合发光严格对比原版与预测完整状态，并显式断言获得 `1` 星、敌人生命变化 `-5`。最终 runId `b4c281152e5b4468b59f10c665d68d`。 | 2026-09-03 |
| `GENERIC-LOOP-LETTER-OPENER-HIDDEN-PHASE-DOP1/2` | 通过 | 两次表面回到同一牌堆形状后，隐藏相位在后续重复中兑现；DOP1/DOP2 都为 `6/12` 展开/转移、`6` 次洗牌、T1。runId `1ae74fcaf14648a19a773e9be602fa34` / `28d3a383cb3c4fd4944ffcc923f76e7a`。 | 2026-09-03 |
| `GENERIC-CROSS-TURN-HIDDEN-BUFFER-DOP1/2` | 通过 | 表面进展长期停滞但精确状态仍跨回合推进；DOP1/DOP2 都为 `513/770` 展开/转移、`16` 次洗牌、T17；DOP2 最大并发为 `2`。runId `e1aac2f411fa4d58b91466022f5edde7` / `d38be6387ddd4aadb13a1adcdc3864cf`。 | 2026-09-03 |
| `GENERIC-CROSS-TURN-STAGNANT-CONTROL` | 通过（上游合并后定向证据） | 无伤害手段的停滞场在 `78/117` 展开/转移后有界停止，最终路线不采用纯防御空转；与隐藏缓冲场共同约束“不能早停、也不能无限续期”。runId `fbba8ab4c2724a3d8fa2585b448dd713`。 | 2026-09-02 |
| `GENERIC-CROSS-TURN-PURITY-PILLAGE` | 通过（定向证据） | 先净化牌库、下一回合兑现，`123/340` 展开/转移、T2。runId `c4e37ee3249d4a819c27a99e0d15fb8a`。 | 2026-09-02 |
| `GENERIC-LOOP-RAMPAGE-DYNAMIC-GROWTH-DOP1/2` | 通过 | 动态成长值不写入循环特判；DOP1/DOP2 都为 `2400/5354/344` 展开/转移/选牌、`32` 动作、T1，动作和全部非时序工作量一致；DOP2 搜索与动作重放最大并发均为 `2`。runId `2d33c3a4eaee44d5b06004758e8cefb4` / `5100ca05213f455ba6f1dd57f0916fef`。 | 2026-09-03 |
| `GENERIC-SMART-POTION-SAME-LOSS-FASTER` | 通过 | 同为零战损时，Smart 选择 T1 的一药路线；请求累计 `31/74`、选中层 `7/22` 展开/转移。runId `02c81917a3284c269a90e2fec5b65d4e`。 | 2026-09-03 |
| `GENERIC-SMART-POTION-THREE-LAYER-PROGRESS-REBASE` | 通过 | 完成无药、恰好一药、恰好两药三层，两次层间整理后仍选择零战损 T1 的一药路线；请求累计 `52/140`、选中层 `7/19`，Gen0/1/2 均 `4` 次。runId `8e01068bacc049b89acacd66e218f73f`。 | 2026-09-03 |
| `GENERIC-SEARCH-POLICY-BRANCHING-REBASE` | 通过 | 控制器生命周期、三层聚合、NoGC 生命周期、DOP 等价、快照释放及同父节点唯一循环租约/转置边界断言通过。runId `328dbe4322a54815a83f3946563d73b6`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-PILLAGE-SINGLE` | 通过 | 单张掠夺触发自动转移内连锁；`1/2` 展开/转移、T1、零损、一个显式动作。该机制不是循环规划器证明出的数学无限。runId `aed2647646d645abb18e1ef94bf53283`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-POMMEL-FINITE` | 通过 | 有限链控制场为 `6/7`、两次洗牌、T2、战损 `11`，首动剑柄打击；没有被误判为 T1 无限。runId `eb6084633d3744399a3e3422e13e2e8a`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-BLOODLETTING-QUALITY` | 通过 | `186/464`、四次洗牌、T2、战损 `3`；最终排序选择少卖血的 T2，而非战损 `6` 的 T1。runId `b57b545ed9c7415cb6644af2df7e89cc`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-SILENT-DISCARD` | 通过 | 准备/战术大师抽弃链为 `301/837/261` 展开/转移/选牌、16 个动作、T1、零损。runId `1991d55b153f44c8b722546214e57f0a`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-DEFECT-RETRIEVAL` | 节点上限内找到解 | 万物一心/全息影像取回链在 `2400/6584/1714` 后命中 NodeLimit，找到 29 动作、T1、零损路线；不称全量穷尽。runId `a7a3fda4781e4f329a00b39aecdf079b`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-REGENT-PARTICLE-WALL` | 节点上限内找到解 | 粒子墙/照我说的做链在 `2400/6037/2` 后命中 NodeLimit，找到 38 动作、T1、零损路线，实际/路线最大格挡 `243/1125`；不称全量穷尽。runId `5859c64bb16d4a0c8bf1134f45d23861`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-REGENT-SEALED-BLACK-HOLE` | 通过 | 封印王座/黑洞资源链为 `10/20`、10 个动作、90 格挡、T1、零损。runId `3bb92f0684d7414d9d0660f83edb992b`。 | 2026-09-03 |
| `INFESTED-PRISMS-GENERIC-QUALITY-INTERMEDIATE-BASELINE` | 已被最终候选取代 | 历史中间候选为 `80,009/537,025/213,213`、约 `57.10 s`、约 `11.29 GiB`、52 HP/战损 8/T6；它暴露了质量保路导致的工作量膨胀，仅保留作优化过程证据。runId `f234bd8a4c0f4f2a87cb6a27458515e4`。 | 2026-09-02 |
| `INFESTED-PRISMS-GENERIC-QUALITY-FINAL` | 通过（配置搜索完整结束） | 同根 VeryHigh/Smart/DOP8/NoGC 16 GB：累计 `13,516/80,477/33,664`，选中层 `4,437/23,810/8,337`；搜索 `10,548.215 ms`、累计分配 `4,920,306,312 B`、`VmHWM=3,715,840 kB`（约 `3.54 GiB`）。结果 43 HP/战损 17/T5/两药，优于旧 42 HP/战损 18/T7；两次层间 NoGC 回收重建成功，Gen0/1/2 均 `4`，GC 暂停累计/最大 `640.203/404.340 ms`。runId `1e9c735a5c9e42889ab46c6389a66b16`。 | 2026-09-03 |
| `AEONGLASS-LONGLINE-NOGC4` | 通过 | 长线同根累计 `27,905/173,477/74,998`、选中层 `8,687/59,910/26,619`，战损 9/56 HP/T9/两药；`72,151.169 ms`、`VmHWM=4,396,804 kB`（约 `4.19 GiB`），Gen0/1/2 均 `30`，GC 暂停 `4,822.405 ms`。13 个压力检查点和 2 次层间整理全部重建 NoGC，无回退。runId `5f75b1cd2b604f34874be9e6243590db`。 | 2026-09-03 |
| `AEONGLASS-LONGLINE-NOGC-OFF` | 通过（A/B） | 与 NoGC4 节点、路线、战损和回合完全一致；`97,927.457 ms`、`VmHWM=2,745,000 kB`（约 `2.62 GiB`），Gen0/1/2=`3522/1695/88`，GC 暂停 `29,712.935 ms`。关闭 NoGC 省约 `1.57 GiB` 峰值内存，但慢约 `35.7%`（反向口径：NoGC 快约 `26.3%`）。runId `6c187605b7a8451bbd163a800d9f25c4`。 | 2026-09-03 |

### 新增 fixture 清单

| Fixture | 主要边界 |
| --- | --- |
| `coverage/unattended/after-stars-gained-black-hole-glow-0111.json` | 通用星能增加 Hook 分发、黑洞单次伤害与完整状态严格差分 |
| `coverage/unattended/generic-loop-letter-opener-hidden-phase-v0111.json` | 同牌堆形状的隐藏相位收益、DOP 等价 |
| `coverage/unattended/generic-cross-turn-hidden-buffer-positive-v0111.json` | 晚于基础观察期兑现的精确隐藏状态、DOP 等价 |
| `coverage/unattended/generic-cross-turn-stagnant-control-v0111.json` | 真正无收益跨回合路线有界停止 |
| `coverage/unattended/generic-loop-cross-turn-purity-pillage-positive-v0111.json` | 先净化牌库、下一回合兑现的跨回合收益 |
| `coverage/unattended/generic-loop-rampage-dynamic-growth-positive-v0111.json` | 动态成长循环、32 动作路线与 DOP 等价 |
| `coverage/unattended/generic-final-quality-zero-loss-over-faster-blood-sale-v0111.json` | 低战损优先；同战损才比较结束回合 |
| `coverage/unattended/generic-smart-potion-same-loss-faster-v0111.json` | Smart 付费药未省足战略 HP 时保留无药路线 |
| `coverage/unattended/generic-smart-potion-three-layer-progress-v0111.json` | 零损终局不启动无收益的付费药梯度 |
| `coverage/unattended/generic-loop-speedster-discard-draw-positive-v0111.json` | 多动作抽弃循环、洗牌与零损击杀 |
| `coverage/unattended/generic-loop-speedster-startup-positive-v0111.json` | 先建立能力再进入循环 |
| `coverage/unattended/generic-loop-hellraiser-pillage-bloodletting-positive-v0111.json` | 卖血/能量启动后兑现 |
| `coverage/unattended/generic-loop-hellraiser-startup-positive-v0111.json` | 先打能力牌再启动 |
| `coverage/unattended/generic-loop-hellraiser-pillage-defend-breaker-v0111.json` | 循环被非攻击抽牌打断并跨回合求解 |
| `coverage/unattended/generic-loop-pale-blue-dot-threshold-cross-turn-v0111.json` | 阈值状态、药水入口与跨回合/出口收益 |
| `coverage/unattended/generic-loop-regent-star-energy-positive-v0111.json` | 星能与能量循环 |
| `coverage/unattended/generic-loop-regent-black-hole-startup-positive-v0111.json` | 储君能力启动与循环 |
| `coverage/unattended/generic-loop-hellraiser-pillage-single-current-v0111.json` | 当前规则单张掠夺的自动转移内连锁；不是数学无限 |
| `coverage/unattended/generic-loop-hellraiser-pommel-finite-current-v0111.json` | 当前规则有限链负例；不得误判为 T1 无限 |
| `coverage/unattended/generic-loop-bloodletting-double-pommel-quality-v0111.json` | 卖血启动质量排序；低战损优先于少回合 |
| `coverage/unattended/generic-loop-silent-prepared-tactician-current-v0111.json` | 当前规则抽弃重复链 |
| `coverage/unattended/generic-loop-defect-all-for-one-hologram-current-v0111.json` | 当前规则取回重复链；NodeLimit 内有解 |
| `coverage/unattended/generic-loop-regent-particle-wall-make-it-so-current-v0111.json` | 当前规则技能/格挡重复链；NodeLimit 内有解 |
| `coverage/unattended/generic-loop-regent-sealed-throne-black-hole-current-v0111.json` | 当前规则双资源重复链 |

`coverage/unattended/generic-loop-hellraiser-pillage-bloodletting-cards.json` 只是可复用牌堆输入，不是独立场景。未在上表列出 runId 的 fixture 仍须复测，不能因文件存在就宣称当前工作树通过。

## 0.27.2（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `DYNAMIC-ROUTE-HP-LOSS-MONOTONIC` | 待玩家实测（Release 编译） | 未结束战斗的动态路线显示“预计战损 未知”；完整胜利路线才显示数值，并拒绝用更高战损候选覆盖当前展示。逐回合掉血不受影响。按要求不运行 UI 测试。 | 2026-09-02 |

## 0.27.1（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `UI-MEMORY-GC-WALL-IDLE` | 待玩家实测（系统压力修复已部署） | 实机基线在条显示 `20.1%` 时 NoGC 意外退出：本轮分配 `2.15 GB`，但预测系统压力已约 `96%`，随后累计 GC 暂停增至 `19.8 s`。配置预算现作为上限，实际区域按系统安全余量缩小；搜索检查点同时检查分配额度与系统压力。Smart 梯度之间主动清理，最终梯度正常结束后保留战斗级区域，战斗结束再延时清理。本轮按要求不运行 UI 测试，等待玩家实测。 | 2026-09-02 |
| `UI-MEMORY-SYSTEM-PROCESS-SEGMENTS` | 待玩家实测（Release 编译） | 内存条按实时物理内存分为灰色系统占用、彩色游戏进程占用和剩余空间；文字显示“当前内存占用 X GB / 搜索总可用 Y GB”，搜索总可用为 CLR 安全总量减去系统占用。本轮按要求不运行 UI 测试。 | 2026-09-02 |
| `UI-SEARCH-LIMIT-WARNING` | 待玩家实测（此前结构验证通过） | `TimeLimit` 与 `NodeLimit` 结果始终显示不可关闭的顶部警告，以“计算尚未彻底穷尽”解释时间/节点上限；正常结束不显示。此前结构 runId `0482e473ee9b4271ba314c25fa9285a7`；本轮按要求不运行 UI 测试。 | 2026-09-02 |

## 0.27.0（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR31-CONTROLLER-UI-LIFECYCLE` | 通过（headless 控制器/UI/药水生命周期，DOP4） | 自动计算持久化、独立停止/采用/执行控件、候选路线与逐回合对敌伤害、窄药水浮层、三向缩放、内容最小尺寸、折叠恢复及位置/尺寸 JSON 往返均通过。runId `75070ce99c1b48ef9c9608205ac57e19`。 | 2026-09-02 |
| `PR31-INITIAL-TOASTY-CONTROLS` | 通过（headless 烘焙手套开局搜索，DOP4） | 首次回合准备搜索可采用已展示候选，返回第 1 回合 `25` 个动作；采用、执行和后续重算仍由回合准备事务接管。runId `e6cb54b4de4f4110a996c2563c7f96ea`。 | 2026-09-02 |
| `OVERLAY-RESIZE-PERSISTENCE-NEXT` | 通过（headless 真实重排） | 宽/高成对持久化、右/下/右下三向缩放、三斜线抓手、`16 ms` 拖动节流、内容最小尺寸、紧凑收起和展开恢复通过；独立药水浮层不改变主面板持久宽度。可见观感未检查。 | 2026-09-02 |
| `BOSS-HP-STRATEGY-SETTINGS-NEXT` | 通过（headless 设置/UI/搜索策略，DOP4） | 第一、二幕与最终 Boss 两项策略独立 JSON 往返；通关优先分别保留 `45 HP/瓶`、`75 HP` 卖血阈值和最终 Boss 存活边界，最低战损独立恢复 `9 HP/瓶` 与普通 Boss 卖血阈值；两类提示文案和关闭状态互不串联。runId `e52f2ec763ac4361a9a09992ab8ae7d5`。 | 2026-09-02 |
| `PR32-DYNAMIC-PREVIEW-EARLY-FINISH` | 结构验证（Release 编译） | 动态演化预览约每 `100 ms` 更新，当前回合预览与可采用推演路线分离；等价获胜路线在敌方状态之后、总评分之前比较结束回合。零警告；按用户要求未运行行为回归。 | 2026-09-02 |
| `LIVING-FOG-GAS-BOMB-TERMINAL-MOVE` | 通过（headless 最小生命周期） | 毒气弹执行 `EXPLODE_MOVE` 后离开活动阵容；回合收尾保留其 AI 快照但不再解析不存在的后继行动。runId `0450d8534bce46e0b329a22f562d95a5`。 | 2026-09-02 |
| `PR33-DAMAGE-AND-POTION-PREVIEW` | 结构验证（Release 编译） | 逐回合对敌伤害累计实际失血；药水补查只在全局路线真正改善时同步更新预览和采用种子。零警告；按用户要求未追加行为回归。 | 2026-09-02 |

## 0.26.0（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR21-BOSS-HP-RELIEF-0254` | 通过（第一/二幕 Boss、第三幕第二 Boss，DOP4） | 第一、二幕分类为 `ActClearHeal`，普通药按 `45 HP/瓶` 开梯度，Boss 卖血阈值为 `75`；第三幕第二 Boss 分类为 `RunEnding`，血量只保留存活边界。runId `444c43b31b3745ef9d94758b2ed79d96`、`4cb4e5936f9b45a09b1ea4ba3091913d`、`da035809ed9243cc850b97085415b3af`。 | 2026-09-02 |
| `PR30-VOID-FORM-SCARCITY` | 通过（合并态 Fork 边界、DOP4） | 两张同成本牌下，虚空形态一个剩余免费格只计一张的机会价值，两个免费格精确计为两倍；PR #27 的奥斯蒂未来价值同时保留。具体玩家实战选牌未稳定复现。runId `aaaa1a2b47924f8b8774a1b6df0b017c`。 | 2026-09-02 |
| `PR29-KNOWLEDGE-CURSOR` | 通过（Fork 边界、DOP4） | 强制结束回合的出牌回放中，知识恶魔诅咒不进入卡牌选择游标；普通动作选择与普通回合选择仍保留并接受消费校验。runId `bb84ece61ac7453e8befa2bb37220f86`。 | 2026-09-02 |
| `PR27-MERGE-FORK-PARALLEL` | 通过（合并态 Fork 边界、DOP4） | PR #27 的低分配状态、roster、缓存与分支所有权断言通过，同时保留 `0.25.3` 的选牌、复活、自动出牌历史和球死亡召唤断言；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=59`。runId `a1747125352741efa72ec01a2ae64c4a`。 | 2026-09-02 |
| `INFESTED-PRISMS-V0251-FULL-SMART-DOP8-A/B` | 通过（最终低分配候选、最新上游同根完整搜索） | 上游/最终阶段墙钟 `112798.755 → 9846.963 ms`，加速 `11.46×`；结束采样工作集 `11,204,886,528 B`。请求累计 `24109/157893/69006` 展开/转移/选牌分支，选中 solver 为 `5861/31244/12125`；保持 `42 HP`、预计战损 `18`、第 `7` 回合和同一动作路线。runId `80921c75b7224f4b887e096f07505739`。 | 2026-09-02 |
| `LONG-LINE-V0251-FULL-SMART-DOP8-A/B` | 通过（四个公开合成长线根） | Silent 396、Necrobinder、Mecha、Queen 的上游→候选阶段墙钟为 `120464.516→55984.737`、`120399.236→29898.891`、`23853.522→8034.665`、`67789.142→29062.616 ms`，加速 `2.15×/4.03×/2.97×/2.33×`；胜负、战损、回合与动作语义不退化。 | 2026-09-02 |
| `SILENT-396-NOGC-BUDGET-BALANCE` | 通过（同根 16/4/2 GB） | `4 GB` 为 `53440.887 ms`、峰值工作集 `4.60 GB`，同 16 GB 路线和工作量且相对上游加速 `2.25×`；`2 GB` 为 `54006.365 ms`、峰值约 `3.4 GB`，同路线且加速 `2.23×`。证明预算是玩家可见的速度/内存权衡，不被预设改写或静默钳制。runId `86b10633dd78400fb9176877855074d3` / `fb93590ed2c04cc7b602fe16ea33f824`。 | 2026-09-02 |
| `PERF-NOGC-TOGGLE-DOP-LIFECYCLE-V0251` | 通过（headless GC/DOP 时序门） | 实际覆盖 NoGC→常规 GC→NoGC、切换中手动回收、关闭模式活动计数、搜索检查点吸收手动回收、引用释放后生命周期补账、`1→2 GB` 重建、取消工作量精确一次、节点快照释放及 DOP1/DOP2 全字段等价和真实并发。runId `df0ab7f8f52c41a2b856aea39c411f49`。 | 2026-09-02 |
| `SEARCH-GC-CLR-UPSTREAM-SHORT-FINAL` | 通过（关闭态端到端） | 配置保留 `false / 17,000,000,000 B`，实际为区域未激活、预算 `0 B`、latency `Interactive`；CLR 可自主回收，不把 GC 次数或 pause 错断言为零。runId `6473c714239b4f63a8735b9291d47629`。 | 2026-09-02 |
| `NOGC-SETTINGS-CONTROLLER-LIFECYCLE-FINAL` | 通过（设置页与 Reset 生命周期） | 新装默认开关与 16 GB、旧 JSON、关闭后预算保留、UI 控件归属均通过；全程关闭的 Reset 不建立自动 GC 根屏障，已启用模式的旧义务仍安全结清。runId `50b51af7a92f42948862686001b1b2cb`。 | 2026-09-02 |
| `PARALLEL-WAVE-ROUND-CHOICE-FINAL` | 通过（安全准入、玩家根与并行指标） | 每个并发 parent 按全局高水位 `1.5×` 预约，never-fit 纯串行，仅 multi-parent 成功 wave 扩宽；自然 singleton action replay、round-choice 唯一所有权及原序合并均实际命中。EXOSKELETONS DOP8 为 `5,686 / 199,522 / 175,150`、`44.608 s`、T4/掉 1；`max parent/action/round = 8/8/5`，runId `6ae1570a41054d369669d65895d285db`。最终 DOP1/DOP2 全政策字段等价、Fork/根快照边界通过，runId `66ca91f7b0934ea6aefd69d4ff563826`。 | 2026-09-02 |
| `PERF-PLAYER-ROOTS-LOW-ALLOCATION-FINAL` | 通过（最终低分配候选的 3 个性能根） | `16 GB` 下 INFESTED `24,109/157,893/69,006`、`9.847 s / 5.730 GB`、42 HP/T7，runId `80921c75b7224f4b887e096f07505739`；PHANTASMAL `24,526/477,315/353,923`、`74.328 s / 42.860 GB`、4 HP/T7，runId `6be970228d0b477d8da0fa2748523819`；EXOSKELETONS `5,686/199,522/175,150`、`42.150 s / 22.180 GB`、96 HP/T4，runId `a97129a4cc514665ba7d222169ac1aef`。三者胜负、战损、回合和动作路线不退化。AEONGLASS 已转独立质量分支。 | 2026-09-02 |
| `PERF-EXOSKELETONS-NOGC16-32-FINAL` | 通过（同 DLL、同工作量的 CPU/内存权衡） | NoGC `16 → 32 GB` 保持 `5,686/199,522/175,150`、评分和 96 HP/T4 路线，墙钟 `42.150 → 28.242 s`；结束工作集 `7.66 → 12.83 GB`、private `18.60 → 35.64 GB`。runId `a97129a4cc514665ba7d222169ac1aef` / `efd4eb77f20848cbbdd147c4a9a12c5f`。PHANTASMAL 同设置为 `74.328/75.273 s`，32 GB 没有收益且结束工作集升至约 `21.90 GB`，所以不作通用默认。 | 2026-09-02 |
| `PERF-ALLOCATION-ENUMERATOR-FORK-BOUNDARY` | 通过（低分配枚举与 Fork 所有权） | StateStore static factory、牌堆/AllCards/Forkable concrete enumerator、roster sink 与直接 COW Fork 构造已编译；Fork 边界完成 parent/child 隔离、阵容移除和状态存储验证，runId `5004871b37f94cbeaf6986556fd53533`。结构门禁 `REFACTOR_BOUNDARIES_OK search_files=59`。 | 2026-09-02 |
| `POWER-LISTENER-CACHE-FINAL` | 通过（Fork 隔离与三个性能根） | Fork 夹具通过 `1→2` 缓存身份、`2→0→1` 结构失效、父子缓存 Power 身份隔离及新增 Power 唯一/顺序，runId `962946a034004fd88cf7bec055c5a04f`。INFESTED 保持 `24,109/157,893/69,006`、42 HP/T7，`9.945 s / 5.474 GB`；PHANTASMAL 保持 `24,526/477,315/353,923`、4 HP/T7，`75.122 s / 40.015 GB`；EXOSKELETONS 保持 `5,686/199,522/175,150`、96 HP/T4，`42.257 s / 20.261 GB`。相对上一最终低分配根累计分配约降 `4.5%/6.6%/8.7%`，耗时中性。runId `5371edccb4c74f9dab68d85a51daa641`、`71606a471f5a44d9b1315af133c5987c`、`9b811499b12d4d0188f0d8db12e0ee4d`。 | 2026-09-02 |
| `PERF-EXOSKELETONS-DOP-SWEEP` | 通过（最终安全 admission、同结果并行扩展） | DOP4/8/12/16 均返回同一 `5,686 / 199,522 / 175,150`、96 HP/T4 路线，墙钟为 `44.896 / 44.608 / 43.157 / 43.082 s`；runId `53c14038d4984e9cac9c0113c6861991`、`6ae1570a41054d369669d65895d285db`、`d2029212067941978790f232ff126680`、`e53770540a464f4e94dec64d830e17d0`。DOP4→16 只快 `4.0%`，12→16 仅 `0.2%`，因此开放 16 但仍默认 DOP4。 | 2026-09-02 |
| `PERF-NOGC-LONG-ROOT-CHECKPOINTS` | 通过（安全点与观察内存） | 最终 `16 GB` INFESTED/PHANTASMAL/EXOSKELETONS 分别跨越 `0/4/6` 个 `SEARCH_MEMORY_CHECKPOINT/RESUMED` 成对边界；需要回收的两场均退出区域、回收并继续。结果与检查点日志观察到的最大工作集约 `11.25/12.22/7.28 GB`；这是离散观察值，不冒充连续采样的精确峰值。 | 2026-09-02 |
| `PERF-V0251-VISIBLE-STEAM` | 未验证（Steam 客户端阻断） | 可见门已尝试三次，最近一次仍未在 `60 s` 内启动游戏；没有留下游戏进程，协议文件已恢复。当前数据来自隔离 Linux headless，不替代完整 Mod 组合下的主线程 p95/p99/max 和可见搜索吞吐。 | 2026-09-02 |

## 0.25.3（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ENEMY-TURN-START-SPAWN-ACTION-BOUNDARY` | 通过（斧兵毒杀复生与千足虫、DOP4） | 敌方回合开始后才出生的怪物不参与本回合行动、不提前推进初始行动；斧兵与千足虫均在第 2 回合精确复用，计划外重算 `0`。runId `e33244c904394b19a5e85d78eba5dccf`、`eee444019fd440a887f22d4fe7b35de7`。 | 2026-09-02 |
| `BOMBARDMENT-EARLY-BEFORE-MAYHEM` | 通过（虔诚雕刻师实包第 2 回合根、DOP4） | 爆破在乱战抽牌前检查既有消耗区，不再把乱战本轮刚耗尽的爆破重复打出；第 3 回合精确复用，计划外重算 `0`。runId `e4f7b25918a74f819d0f3ef5705dcaa9`。 | 2026-09-02 |
| `HEXED-JOSS-PAPER-TURN-END` | 通过（三骑士实包第 4 回合根、DOP4） | 纸钱按 Power 动态赋予的虚无统计回合末消耗牌，阈值抽牌和后续手牌保持一致；第 5 回合精确复用，计划外重算 `0`。runId `6a26ff90d1de4c7e8238b82d6b335285`。 | 2026-09-02 |
| `PAELS-LEGION-CARDPLAY-COOLDOWN` | 通过（斧兵实包第 1 回合根、DOP4） | 补偿层产生的卡牌格挡保留 CardPlay 身份，佩尔士兵在出牌完成后启动冷却；第 2 回合精确复用，计划外重算 `0`。runId `0a6b24215996448f9204b84c3fc193da`。 | 2026-09-02 |
| `UNSETTLING-LAMP-CARD-POWER-SCOPE` | 通过（女王实包第 4 回合根、DOP4） | 卡牌 OnPlay 的通用 Power 效果与专项补偿共用同一卡牌作用域；躁动之灯由鞭打的 Doom 正确消耗，不再错误翻倍后续弱化之触。第 5 回合精确复用，计划外重算 `0`。runId `018bc519e6204a42be24ed6e92788eda`。 | 2026-09-02 |
| `ORB-SLOT-CAP-10` | 通过（Fork 边界、DOP4） | 增加轨道槽位统一遵守原版容量上限：`9 + 2 = 10`，满槽后继续增加仍为 `10`。永劫之镜问题包的完整回放曾卡在等待玩家回合，未声称整包复现。runId `2eeb4d75036a4f1a8245275efbbcf31f`。 | 2026-09-02 |
| `AUTOPLAY-UNMOVABLE-PRIOR-BLOCK` | 通过（Fork 边界、DOP4） | 自动打出的格挡牌读取本回合此前完整的卡牌格挡历史；坚不可摧生效前已有卡牌格挡时不再重复翻倍。runId `a2e032db9bf641b89d6d295fa1106f2d`。 | 2026-09-02 |
| `ORB-DEATH-SPAWN-BETWEEN-PASSIVES` | 通过（Fork 边界、DOP4） | 闪电球击杀感染目标后先完成四只扭动虫召唤，再结算后续玻璃球；四只新生怪均承受 `4` 点伤害。runId `a2e032db9bf641b89d6d295fa1106f2d`。 | 2026-09-02 |

## 0.25.2（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POTION-STALE-SLOT-AND-DISABLED-CAP-0252` | 通过（headless 设置、根捕获与 Smart 上限） | 旧药水腰带槽位不再导致初始化越界；两瓶中禁用一瓶后最大 Smart 梯度为一药。失败基线 `c0d4e5eda2c846cb84107b973f4a4374`，修复 runId `fb708ae2ba4b474aa9c8cf11d6c9a35e`。 | 2026-09-02 |
| `MAYHEM-EMPTY-REQUIRED-CHOICE-0252` | 通过（两组自动打牌顺序夹具） | 战乱自动打出空候选选牌牌时直接执行空选择语义，不再生成零候选请求。失败基线 `15c4938d272e43038a2968cd998f0d58`，修复 runId `d59b5f0128e34e058a2cdef78d6f1bf4`。 | 2026-09-02 |
| `KNOWLEDGE-INVALID-CHOICE-BRANCH-0252-FINAL` | 通过（问题包根状态、DOP4） | 无效的知识恶魔计划选牌候选只淘汰自身，其他分支在 30 秒短搜内返回可执行路线。runId `5bbd0920aabe4a4ca69e51d4d821867e`。 | 2026-09-02 |
| `POWER-AFFLICTION-FIRST-GENERATED-0252-FINAL` | 通过（Fork 边界与感染棱柱实包全自动） | 根卡牌在搜索物化时冻结，第一张新生成牌会正确获得生命火花/流电等状态；感染棱柱实际打出“发现”后结束战斗。runId `1a76d76419c14fa78fd60c8e46220587`、`a33c599436d74965afbada4582739aab`。 | 2026-09-02 |
| `KNIGHTS-DAMPEN-ROOT-0252-PASS` | 通过（三骑士第 5 回合实包根、DOP4） | 根捕获导入压制施法者和原始升级记录，搜索跨过魔法骑士死亡并返回 6 个可执行动作。runId `98d1af7fd9284e6698eb7deb2f37c51e`。 | 2026-09-02 |
| `BLESSED-ANTLER-GAMBLING-CHIP-0252` | 通过（假商人实包全自动、DOP4） | 受祝鹿角先随机插入晕眩，再计算花粉核心抽牌和筹码候选；原生手牌页只搜索/选择一次并在首回合结束战斗。runId `8a140d914a4848d096b00651ba4f438a`。 | 2026-09-02 |
| `SLIMED-NATIVE-CHOICE-0252-BASELINE` | 通过（黏液狂战士第 2 回合实包全自动、DOP4） | 当前编译版从问题根状态执行到第 9 回合结束战斗，燃烧契约与宇宙漠然的原生选牌未再漂移。runId `4ddbe88bfcc648f9a5ecf36da6a6a67a`。 | 2026-09-02 |
| `REVIVING-CREATURE-POWER-GATE-0252` | 通过（Fork 边界、DOP4） | 复活阶段统一拒绝新 Power，实验体重生时不会保留实机不存在的弱化。runId `daf83b4f2c614f008facdd5f9126ab23`。 | 2026-09-02 |
| `QUEEN-MINION-FATAL-0252-MINIMAL` | 通过（女王随从 Fatal 最小夹具、DOP4） | 狂宴首动作击杀 1 HP 火炬头随从后最大生命保持 `80`，不触发 Fatal。runId `f80ec3725924407a8603741a1e5d78ce`。 | 2026-09-02 |
| `MONSTER-INITIAL-ROLL-ISOLATION-0252` | 通过（Fork 边界、DOP4） | 搜索从分支快照解析怪物初始行动，不再进入实机 `RollMove` 及外部预测补丁；Search/Prediction 结构检查无残留调用。runId `78f1a80afe664d1cbc97a80b70e131ed`。 | 2026-09-02 |
| `FORCED-POTION-INTERIM-ADOPTION-0252` | 通过（控制器生命周期、DOP4） | 强制用药时，中间展示与采用路线必须已使用指定槽位的指定药水；零药完整胜利线不能提前收束搜索。runId `ce7406b333034a61b75c75ee4a5dac75`。 | 2026-09-02 |
| `AEONGLASS-TURN-START-DEPLOY-QUEUE-0252` | 结构验证（Release 编译） | 回合准备 `Start` 阶段的执行请求按战斗回合排队，进入 `Play` 后由同回合搜索消费，不再进入普通部署拒绝路径；问题包依赖真人点击时机，未声称自动复现。 | 2026-09-02 |

## 0.25.1（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `MANUAL-GC-PERFORMANCE-PAGE` | 通过（headless 设置页生命周期） | 主界面不再创建手动 GC 按钮；按钮归属性能页，常规/性能/反馈切换正常。runId `530e502cf9a744c4995c2a56af57a954`。 | 2026-09-01 |
| `ISSUE-213617-SMART-TIME-CLOSURE` | 通过（问题包同根 headless 短搜） | Smart、DOP4、`2.068 s`，生成 `9` 个动作；自动药不再触发主动用药门槛，超时结果保持回合完整。runId `7b2a2cf1e7e34dd7813ec0248733ee8b`。 | 2026-09-01 |
| `ISSUE-213617-MUMMIFIED-HAND-REPEAT-COST` | 通过（`1/1` 实机/模拟差分） | 连续打出 `SWORD_SAGE`、`PARRY` 后，木乃伊手临时费用、随机候选和 RNG 一致。runId `4e42216ff3d145d5a8a19b8dca0c857f`。 | 2026-09-01 |
| `TEST-SUBJECT-TURN-BUDGET-FIXED` | 通过（问题包同首根 headless 搜索） | Medium 搜索的升级早有准备、`Glam` 重放与本能反应/战术大师弃牌链按整张牌共用选择预算；深化 `30 s` 的回合层调度从修复前 `4` 回合推进到 `8` 回合，转移 `131,808 → 127,890`，分配 `12,410,879,432 → 12,246,683,840 B`。runId `116f9e03b1fd4181b7412792a9e8277e`。 | 2026-09-01 |
| `HEADBUTT-CHOICE-SCHEDULING-SENTINEL` | 通过（headless 选牌与跨回合复用） | 普通战斗的头槌牌堆选择正常部署，第 2 回合精确复用，计划外重算 `0`。runId `cb66e56414814c0eba21b176f5de3ce9`。 | 2026-09-01 |
| `STRATAGEM-SHUFFLE-CHOICE-FREEZE` | 通过（问题包同首根 headless 搜索） | 洗牌监听器中的战略选牌按请求时刻冻结候选；跨 `1` 次洗牌搜索到 `3` 回合路线，不再因后续生成的煤灰牌导致计算失败。runId `b8ca4dd0641840c59b7cee9a8c7a393e`。 | 2026-09-01 |
| `SEEKER-RANDOM-CHOICE-REUSE` | 通过（问题包同首根 headless 部署与复用） | 探寻打击在攻击及其触发结算后生成随机候选，候选绑定 RNG 计数与卡牌集合；同回合多次原生选牌全部部署成功，第 `2` 回合精确复用，计划外重算 `0`。runId `17cfabb9045a4935b2dcacb0b1ece959`。 | 2026-09-01 |

## 0.25.0（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POTION-PERSISTENCE-BOUNDED-AUDIT-0244` | 通过（headless 设置、搜索与控制器生命周期） | 强制/保护策略按槽位 + 药水 ID 完成 JSON 往返，同槽新药仍为 Smart，恢复 Smart 后不保留覆盖项；Smart 主搜索和药水后验共享 `1.2 s` 请求预算，累计耗时与进度不倒退。runId `0911df45b8b34a04b761d9239f530e9f`。 | 2026-09-01 |
| `PR25-RUNTIME-GC-INTEGRATION-0244` | 贡献者实测通过；本地集成门禁通过 | 默认关闭求解器 No-GC、补账回收与显式自动收集，保留玩家“手动 GC”入口。贡献者报告实机可行且内存占用下降；本轮不重复性能基准。合并态编译与结构门禁通过，药水/控制器夹具 runId `ffe6bad16592496ea1b02fbc6715930a`。 | 2026-09-01 |
| `POTION-SLIM-SIDEBAR-ANCHOR-0244` | 通过（headless UI 结构与生命周期） | 药水策略为约 `184 px` 单列窄侧栏；展开侧栏时标题栏预留同宽区域，药水策略、设置和收起按钮仍锚定在主面板右缘。runId `7c0d24e7f3b9448da0e596651d853e15`。 | 2026-09-01 |
| `POTION-SEARCH-MULTI-PHASE-LABELS-0244` | 通过（headless UI 文案与搜索阶段） | 战损提示包含性能预设建议，点击后持久关闭且不再跳转；搜索阶段覆盖无药、恰好 `N` 瓶的智能梯度，以及固定政策的单药、双药和三药药名。 | 2026-09-01 |
| `SMART-POTION-GRADIENT-EXACT-0244` | 通过（headless 搜索结构与阈值） | Smart 以无药为唯一基线，普通药按 `9/18/27 HP` 开放恰好 `1/2/3` 瓶额度，同层药水共同竞争并在第一条合格梯度停止。runId `406220b4b3b7482a97ebef4a16a330e9`。 | 2026-09-01 |
| `SMART-POTION-LETHAL-GRADIENT-0244` | 通过（headless 完整自动战斗） | 无药路线死亡时进入恰好一瓶梯度，实际使用格挡药并以零战损生还，计划外重算 `0`。runId `aa7e15b86b3a412c9c8abdea72d6b375`。 | 2026-09-01 |
| `COMPLETE-INTERIM-RESULT-0250` | 通过（headless 搜索与控制器生命周期） | 回合层检查点只发布敌人全灭、玩家存活的完整路线，未结束战斗的边界不能冒充整场预计战损；用药数与战损继续严格递增优，玩家采纳后采用同一结果。runId `646a6ebe134e4253a9693983ae398240`。 | 2026-09-01 |
| `TURN-SETUP-COMPLETE-INTERIM-0250` | 通过（headless 烘焙手套开局搜索） | 回合准备搜索只在完整获胜路线出现后允许采纳；点击后从安全检查点生成 `23` 动作计划。runId `3307d4fe09c544d3a4373b5339fa2991`。 | 2026-09-01 |
| `SEARCH-STATUS-TWO-LINE-0250` | 通过（headless UI 与控制器生命周期） | 搜索状态区使用 `64 px` 双行高度和正常字号；阶段保留在第一行，当前用药/战损及累计世界线显示在横跨面板的第二行。runId `f26539fb24a040c09705fb9f72947198`。 | 2026-09-01 |

## 0.24.3（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `UI-PERFORMANCE-POTION-GRID-0243` | 通过（headless UI 与设置生命周期） | `0.24.3` 一次性迁移到 Medium + `16 GB`，新默认一致；预设与内存独立保存。搜索中显示累计世界线，完成摘要包含耗时和总查阅数；战损提示可直达性能页；药水策略为右侧自适应网格卡片，主界面按钮字体与样式统一。runId `e93ec85ff4de49eea28dfeb5892de013`。 | 2026-09-01 |
| `PERFORMANCE-HIGH-INDEPENDENT-MEMORY-0243` | 通过（headless 实际 No-GC 区域） | High 预设与 `17 GB` 内存同时生效，实际建立 `17,000,000,000` 字节 No-GC 区域；证明切换预设不改写内存，旧 `16 GB` 上限已移除。runId `4d12cac9501248c6b108761450f098e8`。 | 2026-09-01 |
| `UI-VISIBLE-0243` | 未执行（按用户要求） | 不做可见界面观感检查；本轮只以 headless 控件结构、布局属性和生命周期断言作为界面验证。 | 2026-09-01 |

## 0.24.2（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ISSUE-BYRDONIS-FIXED-PREFIX` | 通过（问题包同首根定向回放） | Smart、DOP4、`3 s` 下，修复前把第 `5` 回合赌徒特酿动作作为第 `1` 回合固定前缀并失败，runId `888ebe8510e7499aa9664ee096567dc0`；修复后正常返回预计用药 `1`、省血 `10/9` 的路线，runId `af0cd37576444983987eeb8e91be1dc3`。 | 2026-09-01 |
| `ISSUE-KNOWLEDGE-DEMON-SMART-OPTIONAL` | 通过（问题包同首根定向回放） | 玩家策略为 Smart；内部至少一瓶反事实没有合格路线时按可选候选缺失处理，不改变玩家政策。`1 s`、DOP4 返回无药路线且没有 `PotionPolicyUnsatisfiedException`，runId `df63187c89f748df8829271c8e333560`；原报告 `300 s` 整段未复跑。 | 2026-09-01 |

## 0.24.1（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `INITIAL-TOASTY-MITTENS-SEARCH-CONTROLS-NEXT` | 通过（headless 开局搜索控件） | 烘焙手套首次搜索未发布计划时依次请求“执行”和“重算”；执行进入回合准备接管队列，后续重算等待实际选牌完成并在 Play 阶段产生新的第 `1` 回合 `28` 动作路线，没有普通阶段拒绝。runId `f5b7c0a4749c4baba471d58f0c5fb676`。 | 2026-09-01 |
| `INITIAL-TOASTY-MITTENS-SCENE-EXIT-NEXT` | 通过（headless 场景退出边界） | 原生手牌选择等待期间返回主菜单，场景拆除前取消选择 `1` 次；原报告的 `NPlayerHand.SelectCards / AfterCardsSelected / move_child` 栈未再出现。测试结束后的 RitsuLib 设置页焦点链另有独立离树节点日志，不属于本项。runId `1868ed8d9b3a4de19715438060db287f`。 | 2026-09-01 |
| `ISSUE-GREMLIN-MERC-TOASTY-495-FIXED` | 通过（问题包同状态定向回放） | 修复前智能药水后验在烘焙手套分支报 `找不到手牌 PIERCING_WAIL`，runId `b89edc28d6924bf28ea28b4d7c9436a1`；修复后 `3 s`、DOP4 搜索生成预计战损 `1` 的手套计划并等待玩家确认，没有 `TURN_SETUP_FAILURE`，runId `8e44bc14c2f44574b36ac59a2dd402a2`。 | 2026-09-01 |

## 0.24.0（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `TURN-SETUP-MANUAL-REFRESH-0240` | 通过（headless 原生选牌页刷新） | 烘焙手套手牌页、工具箱三选一和选择悖论页面都在首次计划就绪后再次请求手动重算，同一页面收到新的 `PlanReady` 并按刷新后的路线完成选择。runId `15f1fedfab9f4a15a8cf224afe6b36fd`、`55f9bea8cf014e94a194eaa8280ab1da`、`635775924e2041e5b8348def2a77fef7`。 | 2026-09-01 |
| `INITIAL-GAMBLING-CHIP-MANUAL-RECALCULATE-0240` | 通过（headless 选择后重算） | 先在赌博筹码页面空选跳过，再立即请求手动重算；求解器进入 Play 后从实际手牌搜索，第 `1` 回合新路线含 `13` 个动作，不再被阶段校验拒绝。runId `f87e11a8a9934e4cb0934ee47cadd4c0`。 | 2026-09-01 |
| `POTION-STRATEGY-FORCED-SEARCH-0240` | 通过（headless 搜索、结构与生命周期） | 主界面从当前药水栏建立图标、官方名称和逐瓶选项，折叠开关生效；新药默认 Smart，Force 的真实短搜使用精确槽位与药水 ID，Disabled 阻止主动用药；新安装/恢复默认解析为 VeryHigh。runId `2b1bef9d976242d09591331db0906466`。 | 2026-09-01 |
| `SMART-POTION-LETHAL-0240-SENTINEL` | 通过（headless 完整自动战斗） | 1 HP 致死场景继续选择格挡药并在第 2 回合获胜，首轮预计用药 `1`、整场战损 `0`、计划外重算 `0`。runId `33fad9cf08aa440eab0bc6dfc790f5b9`。 | 2026-09-01 |
| `STRATEGY-ACTION-ADMISSION-EXPERTISE` | 通过（headless） | 原单节点分支预算压到 `3`，手牌包含升级熟练、升级子弹时间和三张打击；搜索先保留资源/过牌代表，首动作选择熟练并完成三回合短搜。runId `d2edebb4a7094d2fbce5787e02cc849c`。 | 2026-08-31 |
| `STRATEGY-SMART-POTION-INTERVENTION` | 通过（headless） | 四只花园幽灵鳗固定 `15 HP`、纯攻击牌组和一瓶格挡药；Smart 主路线为无药死亡边界，主动强制一瓶反事实找到存活路线，最终采用格挡药并确认省血 `12/9`。runId `fb7b2287a75e442cad01e1ca32f14417`。 | 2026-08-31 |
| `STRATEGY-SEMANTIC-AFTERIMAGE` | 通过（headless） | 单节点分支预算为 `3`，逐次出牌获得格挡的能力与五张 0 费攻击牌同手；能力收益按可达出牌次数形成防伤向量，首动作使用能力牌，路线预计战损 `0`、实际格挡 `5`。runId `35b7698ab74d462bb40182008bf6cd82`。 | 2026-08-31 |
| `STRATEGY-HP-INVESTMENT-DYNAMIC` | 通过（headless） | 一张提供能量和抽牌的牌直接支付 `6 HP`，超过普通战斗原 `5 HP` 阈值；同起点保守路线存在时，至少一条确实换来战斗进度的投资分支获得保护，最终结果仍按整场战损选择零卖血路线。runId `02b131c187d1433c94ea558e485c591d`。 | 2026-08-31 |
| `STRATEGY-ACTION-ADMISSION-COVERAGE` | 通过（headless） | 固定单节点分支预算 `3`，六种不同即时攻击、过牌与费用控制候选竞争；至少一个原即时 Top-N 之外的战略家族代表进入 frontier。runId `06eb33b27c344825b0801d282b7b8df2`。 | 2026-08-31 |
| `STRATEGY-DOP-EQUIVALENCE` | 通过（headless） | 固定 `250` 节点下 DOP1/DOP2 的动作、评分、展开、转移、分族保路、生命投资和全部非时序剪枝统计一致；DOP2 实际形成至少两路并发。runId `64414648aaae4406a570a0ff59ef1f17`。 | 2026-08-31 |
| `STRATEGY-REPLAY-FDDD-MEDIUM` | 通过（headless） | 严格组合永世沙漏报告同检查点的 `run-state` 与 `replay-state`，完整 `ContinuationStamp` 一致；Medium `24/60` Beam、Smart、DOP4、8GB No-GC 下首动独门技术，第 `4` 回合无药击杀，预计战损 `3`，追平并优于玩家 `6` 战损上界。当前源码回归 runId `3e1f05195228471bbea6cafaabcab1d7`。 | 2026-08-31 |
| `STRATEGY-REPLAY-0F7F-VERYHIGH-RETAIN-ROUTING` | 通过（headless） | 严格恢复蜂群术士报告首回合根及完整 RNG；VeryHigh、禁用药水、DOP4、8GB No-GC 下完整路线第 `13` 回合无药击杀，预计整场战损 `0`，从报告原求解器 `57` 追平人工 `0`。runId `34d0742df3c74ee6a9a5006eabf0ece2`。 | 2026-08-31 |
| `STRATEGY-REPLAY-9E8B-HIGH-ORB-LINEAGE` | 通过（headless） | 严格恢复胧光怪报告第二回合、寄生惧魔召唤物、球槽和球队列；High、Smart、DOP4、12GB No-GC 下首步电击，随后飞跃、防御+、防御，完整路线第 `9` 回合无药击杀，预计整场战损 `0`，从报告原求解器 `29` 追平人工 `0`。runId `d7c41a3e64d24fb792d2b45504222e25`。 | 2026-08-31 |
| `STRATEGY-REPLAY-394B-HIGH-SHORT` | 通过（headless） | 严格恢复虔诚雕塑家报告第二回合根；High、禁用药水、DOP4 在 Short 阶段得到第 `4` 回合结束的 `0` 战损路线，从报告原求解器 `22` 追平人工 `0`。runId `75e243420b5b4f5ca48bc850a5e91ff2`。 | 2026-08-31 |
| `STRATEGY-REPLAY-EF3E-HIGH-POTION-COUNTERFACTUAL` | 通过（headless） | 严格恢复寄生蛙报告第三回合根；High、Smart、DOP4 在 Short 阶段找到 `1` 战损无药路线，评估并拒绝 `120` 条药水分支，从报告原求解器 `13` 改善并优于人工爆炸药路线 `2`。runId `a967a8dbce494fa2948f6d4d94211a4d`。 | 2026-08-31 |
| `STRATEGY-REPLAY-99DC-HIGH-CALCULATED-GAMBLE` | 通过（headless） | 严格恢复构装兽群报告首回合根；High、禁用药水、DOP4 在 Short 阶段得到第 `4` 回合结束的 `0` 战损路线，从报告原求解器 `10` 追平人工 `0`。runId `10f27aef7c9941a8820de637ce28c2ab`。 | 2026-08-31 |
| `STRATEGY-REPLAY-8695-HIGH-ENERGY-DEFENSE` | 通过（headless） | 严格恢复感染棱晶报告首回合根；High、Smart、DOP4 得到 `8` 战损，评估并拒绝 `120` 条药水分支，从报告原求解器 `21` 追平人工 `8`。runId `566104854a2448ef95976505746abac2`。 | 2026-08-31 |
| `STRATEGY-REPLAY-EE98-HIGH-DUAL-POTION` | 通过（headless） | 严格恢复寄生蛙报告首回合根；High、Smart、DOP4 在 Short 阶段使用束缚药水和格挡药水，反事实省血 `50/18`，战损从报告原求解器 `27` 降到 `17`，追平人工。runId `c3e91ca55bd3476487d8710e899b12f1`。 | 2026-08-31 |
| `STRATEGY-THE-HUNT-OPPORTUNITY-COST` | 通过（headless） | 感染棱晶严格首根 High、Smart、DOP4 从 `29` 降到 `21`，前三回合战损 `0/6/13` 与人工一致，runId `2ac40cf8aa7240629ccc5fa1a10f894d`；致命狩猎哨兵仍首动使用狩猎、长期资源至少 `30`、战损 `0`，runId `4c445ef747f643c59b0fc437bd161d4d`。 | 2026-09-01 |
| `STRATEGY-REPLAY-941B-GENERATED-RESOURCE-POTION` | 通过（headless） | 直飞产卵虫严格首根 High、Smart、DOP4 选择无色药水生成急躁，随后打击、急躁、群星之子+、战火铸就，战损从当前 `2` 降到 `0`，追平人工；runId `f8bc0f22a1be4c688f75c603528b917d`。感染棱晶哨兵保持 `21`，runId `6fb2503e4bb34db2972b569f7dd944d9`。 | 2026-09-01 |
| `STRATEGY-SMART-POTION-NONDEGRADING` | 通过（headless） | 蔓生伏地虫严格首根 High、Smart、DOP4 保留 `4` 战损无药主路线，拒绝 `14` 战损敏捷药补查路线并追平人工，runId `5db3d24895f045ee888841b2d9ee207a`；无色药生成急躁哨兵仍为 `0` 战损，runId `296884e4797343f8a1a0502da863b778`。 | 2026-09-01 |
| `STRATEGY-SMART-POTION-ENABLER-FOCUS` | 质量改善，待处理 | 残杀千足虫严格首根 High、Smart、DOP4 保留放血→迅捷药、持续设置和分目标首攻后验，战损从未完成路线的 `48` 降到获胜路线 `35`，人工为 `31`，runId `e05ccbcc56a0448e87d58fc075c9fd60`；蔓生伏地虫哨兵保持无药 `4`，runId `f2ab6a5bfb9a486ba244b1cbcf86a075`。 | 2026-09-01 |
| `STRATEGY-OPENING-RESOURCE-DEFENSE` | 通过（headless） | 灵魂枢纽严格首根 High、Smart、DOP4 选择燃烧契约→火焰屏障→好勇斗狠，战损从 `19` 降到 `12`，优于人工 `17`，runId `21c2a331c31641259a6810a7b93704d6`；蔓生伏地虫哨兵保持无药 `4`，runId `f387d4c98a3e478082ece11e79e38e0c`。 | 2026-09-01 |
| `STRATEGY-LONG-TERM-RESOURCE-CHANNEL` | 通过（headless） | 长期资源通道与即时战损主通道使用独立 Beam 排名。虱虫祖先严格中途根 High、Smart、DOP4 为 `16` 战损，追平人工，runId `ab7beaa750714ed28bfcb7a6d5d781fb`；感染棱晶哨兵为 `19` 战损，追平人工，runId `066e5ed6d1c246e5adbd2522261ebcad`。 | 2026-09-01 |
| `STRATEGY-SMART-POTION-COST-ACCOUNTING` | 通过（headless） | Smart 后验按每瓶 `9 HP` 重算强制用药候选成本。鬼祟珊瑚群两瓶药仅省 `9 < 18`，VeryHigh、Smart、DOP4 保留 `10` 战损无药路线，runId `121d37d20fa24430a2528879aebab339`；永世雕像一瓶迅捷药省 `10 >= 9`，保持 `6` 战损并追平人工，runId `5237dd207c044649b44534f3edb0ddf0`。 | 2026-09-01 |
| `LEGACY-REPLAY-BASELIB-EMPTY` | 通过（headless 导入边界） | schema 1 旧包缺少 BaseLib modifier 字段、当前卡牌均明确为 `baselib=-` 时，完整机甲骑士首回合根严格恢复；非空 modifier 仍不兼容。runId `17d735ddcc0f463d95cc4fca1e06253a`。 | 2026-09-01 |
| `AEONGLASS-LISTENER-CACHE-FORK-FINAL5` | 通过（headless 语义门禁） | 首次 COW、结构失效、普通字段缓存复用、父分支/OwnerPile 隔离和完整根快照均通过。runId `932ad9691a204b3c8ea37f795c4a92b7`。 | 2026-09-01 |
| `BASELIB-CARD-MODIFIER-LISTENER-CACHE-FINAL6` | 通过（完整 BaseLib headless 保守兼容门禁） | 动态增删、状态键、continuation、Owner/分支隔离、生成卡复制和空列表功能路径均通过；空路径不创建临时列表由源码审计。动态 `StoreSaveData` 回调在枚举中新增 live modifier，本次完整状态键仍与回调前相等。runId `06c5235a6d0941f69447180517bce7ab`。 | 2026-09-01 |
| `GC-LIFECYCLE-POLICY-MECHA-013` | 通过（headless 合成政策时序门） | 低/高分配与引用屏障通过；obligation 登记后，exhaustion 引用在 Gen2 标记前释放时总 Gen2=`1`，标记后为 `2`，两条弱引用图均死亡；额外正式 release epoch 不触发第三次回收。生产检测入口另经静态审计。runId `9b4f577a08c84800b219cbcb0bc83310`。 | 2026-09-01 |
| `GC-CONTROLLER-RELEASE-MECHA-013` | 通过（headless 控制器生命周期门） | A→B→Reset、搜索/部署 CTS 与旧 Setup epoch 通过；真实 3 秒 Godot timer helper 取消后屏障在 1 秒内完成，正式 Setup/Resume token 接线另经静态审计。runId `ef96c383720b47dbbdcb62075bcb665d`。 | 2026-09-01 |
| `PR19-NOGC-REGION-EXIT-DELAY` | 目标通过；组合门后续失败 | 实际建立 `1 GB` No-GC 区域后请求低分配战斗结束，区域退出延后 `3011.4 ms`，`gen2_delta=0`，确认延迟位于 `GC.EndNoGCRegion` 前。组合夹具随后在无关的DOP2搜索门以 `waves/work_items/max_concurrency=0/0/0` 失败，毛绒虫与机甲输入结果相同；不将整套组合门记为通过。目标 runId `f9615ee6bdf648fcbba17330aecfb9ea`。 | 2026-09-01 |
| `BUGREPORT-UTF8-CURRENT-RECENT` | 通过（headless 输出兼容门） | 当前/最近两类问题包的 metadata/replay 均为无 BOM 严格 UTF-8，并继续通过 JSON、native-state、run-state 与槽位隔离断言；`byte[]` 常驻表示和单次序列化另由源码审计及驻留估算支持。runId `43e3cc399a6b45e8968da5f7af05556d`。 | 2026-09-01 |
| `PRESENTATION-STRINGVAR-STATE-DIFF-FINAL` | 通过（headless 严格差分） | `NightmarePower.Card` 与 `ShrinkPower.ApplierName` 的本地化展示值不再制造假差异，字段/数值基线仍比较，未知字符串仍 fail-fast。runId `1fe98254c4d045f7a6d7872ae0f26c6f` / `fb2d85cd91724310a3772ebf6204e893`。 | 2026-09-01 |
| `HEADLESS-FULL-MATRIX-20260901` | 场景结果全通过；矩阵命令含 1 次已复验的启动器竞态 | 完整 `246` 命令为 `242 Passed / 3 SkippedMissingFixture / 1` 启动器退出身份竞态，因此该次矩阵命令整体非零；该项游戏内已 Passed（`50a3d2aaf3d648498666e9d46ad1b2b9`），同命令独立复验由启动器返回 `0`（`08fdef52d7974d9185b255edafd6395e`）。最终 `243` 条可执行命令（`242` 个唯一 ScenarioId）均有通过结果，无未解决行为失败。 | 2026-09-01 |
| `SEARCH-PERF-IRONCLAD-CLONE-HAVOC-ROOT-A/B` | 通过（清空默认牌组的固定根 A/B） | 同一 `2305` 张战斗牌、`2302` 张永久牌组牌、`4499` listener 的严格 A/B 为 `4570.556 → 2215.689 ms`；最终源码复验根阶段 `2208.239 ms`，runId `7e8a7512406c44e6bd0bb0fec7a788e0`。 | 2026-09-01 |
| `SEARCH-PERF-IRONCLAD-CLONE-HAVOC-1S-A/B` | 通过（清空默认牌组的固定工作量 A/B） | 严格 A/B 的耗时/分配降低 `33.07%/70.68%`；最终源码复验仍为 `1/3/3/2`、同一 `ENTROPY`、`-3752001`、TimeLimit、0 GC，`3717.9 ms / 267,559,992 B`，runId `47c008cb690d4073bfa0781d510d6378`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-KNIGHTS-CANONICAL-DOP1-2S` | 通过（合成首回合） | `5` 种随机攻击、`5` 种随机防御各 `6` 张，加 `INFERNAL_BLADE/STOKE/CATASTROPHE`；DOP1 短搜 `1324.6 ms / 96,126,192 B`，`choice/actions/sold-hp-pruned/turns=0/4/14/4`，0 GC。runId `108c2fd35daf46938d66be241d51283a`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-QUEEN-CANONICAL-DOP1-2S` | 通过（合成首回合） | 同样的 `5+5` 冻结随机填充，加 `BUNDLE_OF_JOY/SPECTRUM_SHIFT/ENTROPY/JACK_OF_ALL_TRADES` 及对应 Power；DOP1 短搜 `2143.5 ms / 250,810,968 B`，`choice/actions/sold-hp-pruned/turns=1146/6/270/2`，0 GC。runId `f590d200f7e0467aaf090c140d2eced8`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-TEST-SUBJECT-CANONICAL-DOP1-2S` | 通过（合成首回合） | 同样的 `5+5` 冻结随机填充，加 `CREATIVE_AI/AUTOMATION/MAYHEM/JACKPOT` 及对应 Power；DOP1 短搜 `2062.0 ms / 160,130,880 B`，`choice/actions/sold-hp-pruned/turns=0/6/0/5`，0 GC。runId `17875c14859c4aedb5f44e5f6539b788`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-AEONGLASS-DOP1-2S` | 通过（合成首回合） | 同样的 `5+5` 冻结随机填充，加 `TRANSFIGURE/SEEKER_STRIKE/CALL_OF_THE_VOID` 及对应 Power；DOP1 短搜 `1393.4 ms / 149,711,016 B`，`choice/actions/sold-hp-pruned/turns=516/4/24/5`，0 GC。runId `11d097fe49764027a69c551616ab5416`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-QUEEN-DOP4/8` | 通过（同工作量并行探索） | DOP4/8 均为 `choice/actions/sold-hp-pruned/turns=2237/6/234/6`；`1764.1 ms / 553,203,344 B` 对 `1489.3 ms / 560,337,384 B`。DOP8 快 `15.58%`，分配多 `1.29%`；runId `329ace6a379748fbb980b22d7d4f2ba3` / `c2b1de7dda994123be24c5fbdeb25b4c`。 | 2026-09-01 |
| `AEONGLASS-PERF-VISIBLE-STEAM` | 未验证 | 当前数字只来自隔离 Linux headless；完整用户 Mod 组合下的主线程帧、根捕获与 GC 仍需可见 Steam 会话验收。 | 2026-09-01 |

## 0.23.0

本版本只汇总 `0.22.1–0.22.11` 的既有改动并同步发布版本，没有修改行为源码；沿用下列各补丁版本已经记录的定向回归，不重复执行行为测试。

## 0.22.11

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-141700-TURN-START-CHOICE-BASELINE-R2` | 预期失败，已修正 | 第 2 回合熵选牌后通用阵营回合开始重复结算，路线产生一次计划外重算。runId `632b60e264064d149ec0e95f45e8a15a`。 | 2026-08-31 |
| `FIX-141700-TURN-START-CHOICE-FIXED` | 通过（headless） | 第 2 回合熵选牌后回合开始效果只结算一次；首轮路线直接复用，计划外重算 `0`。runId `cf5c0c5b057e4e0a80409e87124c7535`。 | 2026-08-31 |

## 0.22.10

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-133031-DEPLOY-CARD-IDENTITY-R2` | 通过（headless） | 两个同名卡牌实例具有不同临时状态；前一个实例离手后，实机部署按路线保存的完整状态身份选中剩余计划实例。Fork 边界与首回合实际自动战斗同时通过，runId `9e733b537f8f4eee98a4df4f79389bb6`。 | 2026-08-31 |
| `FIX-133031-TEST-SUBJECT-CURRENT-ROUTE` | 通过（headless） | 从实验体问题包战前跑局恢复种子、牌组、遗物与 RNG，以 12 秒短搜和 DOP8 自动执行到第 3 回合；直接复用首轮路线，计划外重算 `0`，没有部署或原生选牌失败。runId `7a8d7f88e17b4b05af7e99e2139e1044`。 | 2026-08-31 |
| `FIX-133031-TEST-SUBJECT-DEPLOY-IDENTITY` | 夹具断言失败，未计通过 | 同一现场已完成第 3 回合复用且计划外重算 `0`，但请求额外强制必须打出全息影像；当前短搜选择了另一条合法路线，因此只该特定出牌断言失败。runId `d376600caf704cdfb06cd2ef061658a7`。 | 2026-08-31 |

## 0.22.9

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-123849-BOUNDARIES` | 通过（headless） | 同一 L1 夹具覆盖新玩家能力位于战斗卡牌之前、横祸嵌套自动打出虚空形态后产生并消费一次结束回合请求，以及预知之滴在三个同名升级剑柄打击上跨同父 Fork 稳定回放。runId `aadd72eb88f0498a8a125e43dd2a3000`。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID` | 通过（headless） | 固定手牌仅横祸、抽牌堆仅虚空形态；短搜首动作是横祸，路线第 1 回合只有这一项动作并继续搜索到后续回合。runId `0569fa1dddf5479493aab7fe14a352c6`。 | 2026-08-31 |
| `FIX-123849-DROPLET` | 通过（headless） | 固定四张同名升级剑柄打击和预知之滴，DOP1 强制至少使用一瓶药；搜索完成 `12` 个选牌分支、使用一瓶药且没有动作回放失败。runId `5babab76d0de43a3bb73861bb7e40726`。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID-DEPLOY` | 通过（headless） | 全自动实际打出横祸并由内层虚空形态结束第 1 回合；没有尝试同回合后续动作，第 2 回合直接复用首轮预测，计划外重算 `0`。runId `9cc7926cb1814abcabad5ad83dee4bfc`。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID-DEPLOY`（首轮） | 预期失败，已修正 | 首轮 runId `27b504a98ee84ae8b3eb4b3288544c7b` 已证明部署停止同回合动作，但第 2 回合因续用缓存只登记显式结束回合节点而重算；统一动作回合边界后由最终夹具通过。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID`（参数首轮） | 夹具失败，已修正 | runId `c1e23e0c1b444ee1ac05ae80800eb14a` 误用必须同时提供卡牌标题的断言参数；改用首动作卡牌 ID 断言后通过，不属于生产功能失败。 | 2026-08-31 |

## 0.22.8

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `KNIGHTS-GC-NRE-FIX-NEXT` | 通过（headless） | 实际进入一次搜索内内存检查点并完成全代回收后继续；随后 No-GC `1 GB → 2 GB` 生命周期正常，DOP1/DOP2 固定工作量结果一致。首轮短搜完成后停止，runId `f8764bdbe2594c66920ff428fe6425d6`。未完整重放问题包中的三骑士战斗。 | 2026-08-31 |

## 0.22.7

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `TOASTY-SINGLE-STEP-UI-FIXED-NEXT-R2` | 通过（headless） | 执行第 1 回合后停在第 2 回合烘焙手套原生手牌页；求解器没有代选，路线 UI 已同步到第 2 回合。runId `64243d425288491198f9a6bf5f415de0`。 | 2026-08-31 |
| `TOASTY-SINGLE-STEP-EXPLICIT-TAKEOVER-NEXT` | 通过（headless） | 从同一单步边界明确点击执行本回合后才接管烘焙手套；选择到部署间隔 `47 ms`，第 2 回合复用既有路线，计划外重算为 `0`。runId `ae2d5384fa144815b7a250f2442c556c`。 | 2026-08-31 |
| `TOASTY-SINGLE-STEP-UI-FIXED-NEXT` | 预期失败，已修正 | 新增 UI 回合断言首次抓到选牌状态早于原生页面锁回调，面板仍为第 1 回合；将显示同步到回合准备事务建立时后，由 R2 通过。runId `a3f331c2cf0b48c29f42e6b6c3a56b00`。 | 2026-08-31 |

## 0.22.6

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-095952-BOUNDARIES-R2` | 通过（headless） | 同一快速夹具覆盖冻结快照不受实机原牌移除标志污染、牌组原牌与局内生成复制的精确回放身份、流沙坑不存在时狂乱逃离为空操作、毒气炸弹自爆为终止行动且眩晕仍有后继，以及根级 Hook listener 捕获。`ForkBoundaries` 与 `CombatRootSnapshot` 均通过，runId `0c19ded7f13444fe914499f85924b840`。 | 2026-08-31 |
| `BATCH-095952-INCREMENTAL` | 通过（headless） | 固定 1 秒短预算在首个结果停止，增量分叉与完整前缀回放保持一致，runId `dd22d98ed4174af692a27764871314f9`。请求 DOP4，但严格增量模式按现有测试政策强制单线程，因此不计并行性能证据。 | 2026-08-31 |
| `BATCH-095952-BOUNDARIES-R1` | 夹具失败，已修正 | 新增流沙坑断言最初直接构造未挂接战斗状态的模拟对象，runId `52ab5e56bb1744e99aa9b993b3a22306` 在测试准备阶段失败；改为从已挂接模拟器取得战斗状态后由 R2 通过，不属于生产功能失败。 | 2026-08-31 |

## 0.22.5

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `TWO-TAILED-RAT-PENDING-AI-UNIT-0225` | 通过（headless） | 敌方回合生成随机初始分支的双尾鼠时不消费怪物 RNG；回合边界才掷出抓挠、疫病啃咬或尖啸。runId `02120badde45441f99b384437879e517`。 | 2026-08-31 |
| `TWO-TAILED-RAT-PENDING-AI-FINAL-0225` | 通过（headless） | 从问题包恢复同一首回合牌序、三只鼠生命与行动；修复前 runId `941ffb14c3244100b3285336a61db0eb` 在第 3 回合呼叫支援失败，修复后首轮短搜得到 10 回合路线、预计战损 `4`，runId `5649195a84e342c7b8bc8d70dd5281c0`。 | 2026-08-31 |

## 0.22.4

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `STRATEGY-PHANTASMAL-FINAL-0230` | 通过（headless） | 从问题包精确恢复花园幽灵鳗首回合根；4 秒短搜找到两瓶药路线，预计整场战损 `27`，低于改动前同根 `35`。runId `954385d2794049ada20ee0706016486c`。 | 2026-08-31 |
| `STRATEGY-AXEBOTS-MASTER-0230` | 通过（headless） | 从问题包精确恢复巨斧机器人根；必备工具 setup 校准后预计战损 `4`，改动前同根为 `5`。runId `4595ce39f3df4775b008ea75d46d1f20`。 | 2026-08-31 |
| `STRATEGY-KNOWLEDGE-REVERT-GUARD-0230` | 通过（headless） | 知识恶魔精确根维持预计战损 `3`；验证失败的早有准备统一加权已经撤回。runId `3f4973a3ebe64387af2fca6925797354`。 | 2026-08-31 |
| `STRATEGY-KAISER-CURRENT-0230` | 通过（headless） | 帝王蟹精确根维持预计战损 `0`，未因药水策略校准退化。runId `8b27b0f445b24e669189b855e342358a`。 | 2026-08-31 |
| `STRATEGY-NIBBITS-FINAL-GUARD-0230` | 通过（headless） | 固定双小啃兽长线根维持预计战损 `0`，不使用药水。runId `0d8363df9df44164a0ac8f4267d64e31`。 | 2026-08-31 |

## 0.22.3

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-0223-DYNAMIC-VAR` | 通过（headless） | 计算型 Damage 变量按实际 `DynamicVar` 读取基础值，不再强转原版 `DamageVar`，且选牌估值不调用第三方实机求值器。runId `adf440d49b424b659d54c2187f4c5953`。 | 2026-08-31 |
| `FIX-0223-GC-COLLECTION` | 通过（headless） | 高血量、多候选固定根完成首次后台 Gen2 回收、No-GC `1 GB → 2 GB` 切换和 DOP1/DOP2 全字段等价。runId `e8c3222c43d842daa3e2e640155c8d3f`。 | 2026-08-31 |

## 0.22.2

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-0222-BOUNDARIES` | 通过（headless） | 同一快速夹具覆盖原生选牌页期间卡牌状态变化后的实体定位、出牌结束 Power 提交、免费能力牌遗物消耗、超质量体生成牌创建者、死亡后禁止继续施加 Power、敌方回合召唤怪初始行动与幻象复活，以及清空实机怪物状态机后仍使用根快照推进行动。runId `805266b49faa4435abaae7566edaed64`。未逐场运行本批 `18` 份完整战斗。 | 2026-08-31 |

## 0.22.1

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-225649-ROOT-SEMANTICS` | 通过（headless） | 验证新怪物生命判重读取模拟最大生命；直飞产卵虫已有蛋的模拟/原生最大生命刻意不同时，新蛋仍避开模拟占用值。同步验证 `WHISPERING_EARRING` 在第二回合不再自动出牌及 Fork 边界。runId `a5104a89fd614a8196c17ea86bc2f042`。 | 2026-08-30 |
| `BATCH-225649-SPEED-POTION-END-TURN` | 通过（严格差分） | 从已存在 `DEXTERITY_POWER:8 + SPEED_POTION_POWER:5` 的根开始，回合末原版与预测都回到 `DEXTERITY_POWER:3` 并移除速度药水 Power。runId `bc952a2f52314755b7be8f215e348349`。 | 2026-08-30 |
| `BATCH-225649-COMPACT-BUG-REPORT` | 导出结构通过；后续搜索等待超时 | 实际问题包 `71,470` 字节、`21` 个条目；断言只含当前战斗，没有截图、`saves/` 或 `forensics/recent`，保留战前内存存档和两组完整检查点。结构断言完成后，夹具在等待初始求解结果时达到 `120` 秒；不计整场通过。runId `4b0759b2552a429db4a723058788333b`。 | 2026-08-30 |

## 0.22.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR10-LARGE-DECK-FIXED576-A/B` | 通过（PR 固定工作量 A/B） | 基线与优化 artifact 都保持 `576` 展开、`3463` 转移、`1124` 选牌分支和同一 3 回合、预计战损 `2` 路线；`5116.3 ms / 1,039,502,640 B` 对 `3985.0 ms / 528,876,328 B`，累计分配降低 `49.12%`，单次 headless 墙钟缩短 `22.11%`。不替代可见 Steam 结论。 | 2026-08-30 |
| `PR10-FULL-DEEP-16GB` | 通过（PR 当前 artifact） | 精确进入 `16,000,000,000 B` No-GC 区域，保持 `7018/52644/23196` 工作量、评分、10 回合胜利、预计战损 `56`、卖血 `11` 与两瓶药路线；`53605.8 ms / 11,655,259,632 B`。 | 2026-08-30 |
| `PR10-NOGC-RNG-BUDGET-CONTRACT` | 通过（PR 当前 artifact） | 实际进入 `1 GB` 区域，令并发 `2 GB` 请求等待，释放旧 scope 后精确重建 `2 GB`；同时验证完整 RNG 身份及 DOP1/DOP2 结果全字段一致。runId `5677b8ccffc842d68f2199da964ed610`。 | 2026-08-30 |
| `PR10-SANITIZED-STRESS-FIXTURES` | 通过（PR 当前 artifact） | Silent `396` 张合成牌堆和 Necrobinder 最小战前投影均使用公开合成 seed，断言极高档原 Beam、节点、分支和精确 `16 GB` No-GC；runId `a2aef73ea38345a7b48418f7ff498ffc`、`07427794ff05455886fc7faf2318966e`。 | 2026-08-30 |
| `PR10-POTION-CHOICE-ALLOCATION` | 通过（PR 严格语义门禁） | 生成牌药水只克隆实际选中牌；17 项药水完整原版/预测差分 `17/17`，赌徒特酿专项通过。runId `341bf965156644c4a8fa6e3cd2399682`、`c5f1b95001f542d3b0d536295f914bdc`。 | 2026-08-30 |
| `PR10-CACHE-FORK-BOUNDARIES` | 通过（PR 当前 artifact） | 覆盖 Ritsu capability 缓存失效、选牌键跨 Fork、池化身份哈希、listener observer、所属牌堆、投影洗牌、稀疏 Power affliction 和 `CardPlay` 选择风险隔离。runId `bd24e4eeda0247f99eaac9fa90281e3b`。 | 2026-08-30 |
| `PR10-MERGED-POLICY-FORK-0220` | 通过（本地主线合并态） | 高血量、多候选固定根实际完成 No-GC `1 GB → 2 GB` 切换、DOP1/DOP2 全字段等价、节点上限快照释放、Fork 边界及完整自动战斗；第 6 回合结束，runId `26654574147d4925be016d7295fbee2a`。首次 `1 HP` 烟雾输入因没有形成并行工作量而被门禁拒绝，不计功能失败。 | 2026-08-30 |
| `PR10-VISIBLE-STEAM` | 未验证 | PR 没有可比较的当前 artifact 可见 Steam 性能结果；本轮不把 headless 单次墙钟写成生产帧率结论。 | 2026-08-30 |
| `PR11-THEME-OPACITY-LIFECYCLE-0220` | 通过（headless） | 验证新设置默认深色主题与 100% 不透明度，浅色/55% 设置可回读；切换主题会重建覆盖层、恢复设置页与当前搜索状态，并即时应用 65% 透明度。既有三页设置、通知、上传终态、预设持久化和搜索停止/恢复同时通过，首回合结束。runId `921055897c4c43ceb773c90c79eac953`；不替代 Steam 可见像素、拖动和动画检查。 | 2026-08-30 |
| `PR11-OPACITY-SLIDER-VISIBLE-RAIL-0220` | 待实机确认 | 透明度控件改为固定可见轨道、强调色已选区段和独立悬停拖动圆点；数值回读、即时应用和主题重建由上一项覆盖，像素观感与鼠标拖动留给本地可见游戏确认。 | 2026-08-30 |

## 0.21.7

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `SETTINGS-TABS-LIFECYCLE-NEXT` | 通过（headless） | 实际创建设置控件并验证“常规 / 性能 / 反馈”三页独立切换；通知“关闭 / 仅后台 / 始终”无损回读旧字段，预设重载不误判自定义，上传成功/取消终态和控制器停止/恢复链路同时通过。第 1 回合结束，runId `852a0f03f4724d6598212e309d11a2b4`；不替代人工视觉检查。 | 2026-08-30 |
| `NOTIFICATION-SETTINGS-LIFECYCLE-NEXT` | 通过（headless） | 验证通知默认开启且默认为“仅游戏不在前台”，关闭、仅后台和始终通知的决策正确；设置 UI 按持久化值加载。用户停止搜索产生一次结束通知请求，headless 不进入 Windows 原生调用。runId `99e510b870fc4ad6ab0611ce36f8f3b1` | 2026-08-30 |
| `WINDOWS-NATIVE-NOTIFICATION-ENTRYPOINT-NEXT` | 通过（系统声音量未检测） | 可见游戏日志确认旧入口连续 5 次抛出 `EntryPointNotFoundException`；显式绑定 `Shell_NotifyIconW` / `LoadIconW` 后，独立 Win32 窗口按与 Mod 相同的结构提交通知，返回 `shown=True / win32_error=0`。通知未设置静音标志，实际音量与是否播放由 Windows 通知策略决定。 | 2026-08-30 |

## 0.21.6

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR9-BUG-REPORT-FIFO-VERSION-FINAL` | 通过 | 同一最小战斗覆盖在线描述版本号、控制器自动分类、后台检查点 FIFO 与导出屏障；活动战斗问题包成功生成，结构化状态、RNG、五个牌堆、原生状态、即时跑局存档和 `solver_only` 控制模式断言均通过。runId `7065e4ebabd64bc69700ebf70d323e27` | 2026-08-30 |

## 0.21.5

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-193556-RNG-IDENTITY-FINAL` | 通过 | 人工构造计数相同、内部状态不同的战斗 RNG，续用文本和搜索状态键都能区分；固定 250 节点的 DOP1/DOP2 完整搜索政策继续一致。runId `8d6ae9a5647240a5846a2549cb06c369` | 2026-08-30 |
| `BATCH-193556-SOUL-NEXUS-TURN-SETUP-FALLBACK-FINAL` | 通过 | 从 `SOUL_NEXUS` 问题包战前存档进入赌博筹码原生页面；释放候选后的重建保留开局选择，8 秒短搜返回 6 回合路线，不再要求已经换走的启动流程仍在手牌。runId `1ec4e296e601422b86674f7f7d3d35df` | 2026-08-30 |
| `BATCH-193556-SEEKER-CHOICE-ACTION-SCOPE-FINAL` | 通过 | 同一张牌在一个 `CardPlay` 内能看到自己的待解决选择；开启新的 `CardPlay` 后不会继承上一次选择风险，Fork 边界检查同时通过。runId `7fcf4d2c016f40a6aeeaf6f314b9cf3e` | 2026-08-30 |
| `BATCH-193556-SEEKER-CHOICE-DIFFERENTIAL-FINAL` | 通过 | 探寻打击的随机候选、原生选牌、移入手牌与预测完整状态严格一致。runId `cac18e0431be4bc2a06b474da84460dc` | 2026-08-30 |
| `BATCH-193556-EXOSKELETON-RNG-BASELINE` | 部分，不计通过 | 问题包战前根在 20 秒单线程短搜下运行至第 2 回合后达到 120 秒总上限，没有到达原第 5 回合选牌失败，不能作为问题包整场复现或修复证据。runId `8655f27259fa4d0c822ebef9084f2eac` | 2026-08-30 |

## 0.21.4

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-184344-MAYHEM-STRATAGEM-CHOICE-FINAL` | 通过 | 花样百出自动打出微光，微光抽牌触发洗牌和战略选牌，随后继续微光自己的手牌选择；两层选择按原版顺序完成，牌堆和完整状态严格一致。runId `0583a87ba9504c54b3b9df92f40398b3` | 2026-08-30 |
| `BATCH-184344-ILLUSION-FIRST-MOVE-REVIVE-FINAL3` | 通过 | `FOGMOG` 召唤利齿之眼后登记其首次正式行动，再于行动前击杀；原版与预测均进入 `REVIVE_MOVE`，完整状态严格一致。runId `2c4c0640cedd45bbac812bab31de22ee` | 2026-08-30 |
| `BATCH-184344-PARALLEL-CLONE-FORK` | 通过 | 缩小甲虫固定根以 250 节点比较 DOP1/DOP2，动作、选择、评分、快照和回合标注一致且形成真实并发；同时验证 `PAELS_LEGION` 完成/中止出牌后都能清理瞬时引用并稳定 Fork。runId `9678ee908aac427e9fbc5a6d7b17e4cf` | 2026-08-30 |

## 0.21.3

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-164623-TRANSFORM-ENTERED-COMBAT-CONTINUATION` | 通过 | 先打出一张虚无牌，再把手牌固定变换为费用随本场虚无牌变化的牌；原版与预测的变换入场、费用、打出后牌堆和完整状态严格一致，并启用增量核对。runId `c84456dc9b214cf7a7542579a8cd521f` | 2026-08-30 |
| `BATCH-164623-TURN-SCOPED-CARD-HISTORY-CONTINUATION-FINAL` | 通过 | 同一夹具覆盖两个回合：迭代在新回合重新响应首张状态牌；上一回合零费攻击不污染本回合施加的野性。原版与预测完整状态严格一致，并启用增量核对。runId `98adb2c4b3df4c65aed9cd3fb850512b` | 2026-08-30 |
| `BATCH-164623-ORB-DEATH-POWER-ORDER-CONTINUATION` | 通过 | 双巨斧机器人中，累计 `30` 伤害的暗黑球连续激发：第一击击杀后先触发另一只敌人的 CrabRage Power，第二击再消耗所得格挡；原版与预测完整状态严格一致。runId `99dc41bee83243cf9bad3c6b32826c0b` | 2026-08-30 |
| `BATCH-164623-LAGAVULIN-REUSE-FINAL2` | 超时，不计通过 | 从母体问题包战前存档恢复并使用原高预算；第 1 回合搜索未在 `360` 秒总时限内完成，没有进入自动部署，不能证明第 7 回合复用。runId `c4d32e6bde1d4e01a122dce45caa69b6` | 2026-08-30 |
| `BATCH-164623-LAGAVULIN-REUSE-SHORT` | 超时，不计通过 | 同一战前状态改用固定 `8` 秒短搜并启用增量核对；仍在第 1 回合达到 `180` 秒总时限，没有观察到第 7 回合复用。runId `a5edd7975b4645baa6dd8cde97bfe0d3` | 2026-08-30 |

## 0.21.2

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POST0211-ADAPTIVE-DOP-PRESET-PERSISTENCE` | 通过 | 默认并行度对 `1/2/3/4/32` 个逻辑处理器分别解析为 `1/2/2/4/4`；高档预设在设置页重新加载并提交未修改数值后仍保持高档。控制器生命周期和首回合自动战斗同时通过。runId `25fd0bdbeaa9450ca13ca6eebec74d95` | 2026-08-30 |

## 0.21.1

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `V0211-PARALLEL-OFF-RECOVERY-UI` | 通过 | 设置面板成功创建并行度选择框；并行搜索失败提示同时包含上传问题包与切换“关闭（单线程）”，串行搜索失败只提示上传，不误报并行恢复建议。控制器停止/恢复生命周期和首回合自动战斗同时通过。runId `598166aaeb19404fa75603c35a5921fa` | 2026-08-30 |
| `POST021-SMARTFORMAT-DIRECT-PATCH-ATTEMPT` | 失败实验，已撤回 | `LocManager.SmartFormat` 含异常过滤器，Harmony 无法生成 Prefix/Finalizer 或 Prefix-only 改写，CombatSolver patch 整体回滚；runId `f15cebdaa235481d970942bbdf5c7232`、`b8f16a2de9ea4779a738604ab8a247c9`、`0e0f3425cc104417bdf083ccf6cdcca0`，不计回归通过 | 2026-08-30 |
| `POST021-POWER-DYNAMIC-WARMUP-SMOKE` | 通过 | 主线程物化与 Power 惰性变量 guard 正常加载；铁甲战士首回合自动结束 1 HP 小爬虫战斗，没有 patch 回滚。runId `8b4446b6e4a34fa9b68bcdbd971441af` | 2026-08-30 |
| `POST021-INFESTED-PRISM-SMARTFORMAT-DOP4` | 通过 | 从玩家包恢复受感染棱镜战前存档、手牌与 RNG；DOP4 短搜约 `2976.8 ms` 返回 5 个可执行动作和第 4 回合路线，没有集合并发异常。runId `cb71d23a0baf4046b65dc0348c355041` | 2026-08-30 |
| `POST021-POWER-DYNAMIC-DOP1-DOP2` | 通过 | 固定长线根比较 DOP1/DOP2；动作、选择、评分、展开、转移、全部非时序剪枝、快照、continuation 与回合标注一致。runId `614d4ac8af3749cd92b9676662956dae` | 2026-08-30 |
| `POST021-BASELIB-PARALLEL-ENCHANTED-TERROR-EEL` | 通过 | 完整加载 BaseLib `3.4.5`，从玩家包恢复骇鳗首回合手牌、牌序、迅捷生存者、螺旋防御与 RNG；DOP4 短搜正常返回第 8 回合可执行路线，没有重复键异常。runId `ad3c8c31ef054a459a3f27dc9c45b16f` | 2026-08-30 |
| `POST021-BASELIB-ENCHANTED-DOP1-DOP2-EQUIVALENCE` | 通过 | 同一附魔牌根在 BaseLib 完整加载时比较 DOP1/DOP2；动作、选择、评分、展开、转移、非时序剪枝、快照、continuation 与回合标注一致。runId `258ec69dbc1b45c8bb6afa809623f4b6` | 2026-08-30 |
| `POST021-BASELIB-PARALLEL-ENCHANTED-TERROR-EEL-FULL-AUTO` | 通过 | 玩家包根以 DOP4、Instant/0 秒完成整场自动部署，第 8 回合结束，计划外重算 `0`，没有重复键异常。runId `75cbc36d31d045778a72fa3acf60c080` | 2026-08-30 |

## 0.21.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POST0201-SCRAPE-NEGATIVE-COST-FINAL` | 通过 | 刮削+依次抽到普通费用牌与负费用贪婪；预测与原生的手牌、弃牌堆及其余完整状态严格一致。runId `a5117c6888d0438693c0334775c720e1` | 2026-08-30 |
| `POST0201-WHISTLE-STUN-FOLLOW-UP-FINAL` | 通过 | 吹哨打断史莱姆狂战士的呕吐脓水；眩晕结束后实机与预测都恢复呕吐脓水，不再跳到狂怒痛击。runId `500c0e56d7fb4dd58977040a6dd92610` | 2026-08-30 |
| `POST0201-BRAND-POST-CHOICE-POWER-FORK-FINAL` | 通过 | 升级烙印完成原生手牌消耗选择并获得力量后，模拟状态立即满足稳定 Fork 边界。runId `7ea11c09cf614e28922104cf10875697` | 2026-08-30 |
| `POST0201-SLIMED-BERSERKER-WHISTLE-REUSE` | 通过 | 从史莱姆狂战士问题包恢复牌组、遗物与 RNG；严格增量搜索实际打出吹哨，连续复用到第 3 回合，计划外重算 `0`。runId `e6232efae6494f2cb91b14d6790c3c23` | 2026-08-30 |
| `POST0201-TEST-SUBJECT-SCRAPE-SCAVENGE-REUSE` | 通过 | 从实验体问题包恢复牌组、遗物与 RNG；第 2 回合打出刮削，内存清理随后在 4 张原生手牌候选中成功选中贪婪，严格增量复用到第 3 回合且计划外重算 `0`。runId `ec1c11fa2a444eda836520d1fe18e829` | 2026-08-30 |
| `POST0201-DECIMILLIPEDE-CURRENT-DEEP-BASELINE` | 部分，不计完整通过 | 当前源码从千足虫问题包根完成搜索，没有复现旧版待结算力量导致的 Fork 异常；结果只有死亡路线，测试因可执行动作下限断言失败，因此不作为完整战斗证据。runId `dbf082665e984821b835e665889cc168` | 2026-08-30 |
| `MULTICORE-NIBBITS-DOP1/DOP2-AB` | 通过（headless 迭代基准） | 固定双小啃兽快照在上游 DOP1、当前 DOP1、当前 DOP2 均为 `573` 展开、`2759` 转移、同一 5 回合零战损/零药路线。独立冷进程求解耗时 `1424.5 / 1449.3 / 955.2 ms`，当前 DOP2 缩短约 `34.1%`；累计分配 `177,425,048 / 177,767,800 / 179,165,392 B`，DOP2 并行遥测为 `286 waves / 572 items / max concurrency 2`。runId `e8bfb02cf9714c98ae9230842c756ff3`、`9314522060164435b551c18f16d7d093`、`ca238f1462874e7d8c67069b4b71077c`；不替代 Steam 可见性能门槛 | 2026-08-30 |
| `MULTICORE-POLICY-EQUIVALENCE` | 通过 | 带初始力量与 `SURVIVOR` 弃牌选择的固定根先执行冷缓存 DOP2，再执行 DOP1；递归核对完整动作/选择、评分、展开、转移、各类剪枝、快照、continuation 与回合标注。DOP1 并行遥测全零，DOP2 实际最大并发不小于 2。runId `3f8d240bda57441c8352bfb749424017` | 2026-08-30 |
| `MULTICORE-FULL-AUTO-DOP2` | 通过 | 默认并行路径完成双小啃兽整场自动部署，第 5 回合结束，第 3 回合精确复用；零药、零预计战损。runId `eda5d69feb774389a8990d788434ac6d` | 2026-08-30 |
| `MULTICORE-EARRING-NESTED-CHOICE-DOP2` | 通过 | 工具盒形成首回合多根，低语耳环连续自动打出高密度 `SURVIVOR` 并消费嵌套弃牌选择；精确原版状态检查通过，搜索记录 `4 waves / 8 items / max concurrency 2`。runId `f577be1a8e0a4ebb84aa115d3ab28734` | 2026-08-30 |
| `MULTICORE-NIBBITS-DOP4` | 通过 | 四条 lane 完成固定双小啃兽搜索，仍为 `573 / 2759`、同一路线与全部剪枝指标；并行遥测 `159 waves / 572 items / max concurrency 4`。runId `54243577aa984e8eb68f2d242a216eb9` | 2026-08-30 |
| `MULTICORE-INCREMENTAL-FORCED-SERIAL` | 通过 | 请求 DOP4 并开启严格增量回放；首轮完整结果的 `parallel_waves / work_items / max_concurrency` 均为 `0`，逐转移回放一致，第 5 回合结束且计划外重算 `0`。runId `96b7d9fdfbb245d68f4effefcd748b1e` | 2026-08-30 |
| `MULTICORE-V020-FINAL-POLICY-EQUIVALENCE` | 通过 | 合并 `upstream/main` 的 `v0.20.0` 后，以固定 250 节点先跑 DOP2 再跑 DOP1；动作、选择、评分、展开、转移、全部非时序剪枝、快照、continuation 与回合标注一致。门禁同时断言两档都实际释放节点上限丢弃的 Simulator；runId `c9052d90aa504ae0ba183a5089aa0e07` | 2026-08-30 |
| `MULTICORE-V020-FULL-AUTO-DOP2-FINAL` | 通过 | 合并态默认 DOP2 完整自动部署双小啃兽，第 5 回合结束、第 3 回合精确复用，零药、零预计战损、计划外重算 `0`；runId `f48b0cebd842466594bd8f30789d589a` | 2026-08-30 |
| `MULTICORE-MECHA-DOP4/6/8-WARM-NOCACHE` | 通过（headless 迭代基准） | 同一暖进程固定 `4319 / 33087 / 18399` 工作量与第 7 回合/28 战损路线；DOP4/6/8 为 `5451.7 / 4281.3 / 3813.0 ms`，累计分配为 `4,404,184,848 / 4,412,016,024 / 4,415,450,000 B`。runId `db33a20aa0764f068b34a3028ec06beb`、`eab8b0f052634b82be807b3af9ddaec9`、`38e6aed2c8834a4fa0cea8a35e18b6f5`；不替代 Steam 可见性能门槛 | 2026-08-30 |
| `MULTICORE-V020-MECHA-DOP8-FINAL` | 通过（headless 冷进程） | 当前合并态 DOP8 保持 `4319` 展开、`33087` 转移、`18399` 选牌分支与同一路线；`6366.8 ms / 4,441,356,192 B`，实际最大并发 `8`，结果时工作集 `6,011,162,624 B`，GC `0 ms`、最大帧 `18.0 ms`、无 `>50 ms` 帧。runId `2c38044b3fb44bef85b66032b488271c`；可见 Steam 启动未形成游戏进程，故不写成生产帧率结论 | 2026-08-30 |
| `PR8-MERGED-DOP1-DOP2-EQUIVALENCE` | 通过 | PR #8 合并到本地主线后，以固定 250 节点比较 DOP1/DOP2；动作、选择、评分、展开、转移、全部非时序剪枝、快照、continuation 与回合标注一致，两档都实际释放节点上限丢弃的 Simulator。runId `c4e1343ad44843229483f97e3d04265a` | 2026-08-30 |
| `PR8-MERGED-DOP2-FULL-AUTO` | 通过 | 合并态默认 DOP2 完整自动部署双小啃兽，第 5 回合结束、第 3 回合精确复用，计划外重算 `0`。runId `21f6375fb9d5499a99d992c2eb7a0878` | 2026-08-30 |

## 0.20.0：在线问题包、跨平台测试与选牌修复

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `LINUX-HEADLESS-INSTANT-AB` | 通过 | 同一 PID `66703` 先后运行 `Normal / Instant / Normal`；内部耗时分别为 `7098.0 / 2917.7 / 7064.5 ms`，Instant 稳定节省约 `59%`。三次均应用并恢复测试速度，runId `e8dede980dbb4daab57cc1c6a1d71730`、`8851e4147f9940e2921088474c8c7e5f`、`42148ebf930243b59d9230d2cb68f913` | 2026-08-29 |
| `LINUX-HEADLESS-REUSE-REGRESSION` | 通过 | 同一 PID 连续通过怪物严格差分、铁甲战士跨回合复用、跨角色切换到星辰、再切回铁甲战士完成精灵药自动战斗；对应 runId `74da3eefb1e841219c5721323dad83d6`、`dbd5cf92f61043b28d38978aa8307a6e`、`8605ff494b604c019323e222f5247314`、`261c9007073d40709a5f388d698063e9`，两项复用场景和完整战斗的计划外重算均为 `0` | 2026-08-29 |
| `LINUX-HEADLESS-LIFECYCLE` | 通过 | 原生日志为 `N/A (headless) / VRAM 0B`；marker 验证 PID starttime、隔离环境及 DLL/manifest 哈希。无变化复用同一 PID；Release 重建后输出 `UNATTENDED_RESTART reason=mod_changed` 并自动换 PID。失败退出约 `510 ms`，最终进程、marker、临时 RitsuLib 投影均清理 | 2026-08-29 |
| `HEADLESS-MATRIX-CANONICAL-0180` | 通过 | Linux 以 `--continue-on-failure` 按文档原有生命周期边界运行全量矩阵：`MATRIX_END total=228 attempted=225 passed=225 failed=0 skipped=3 cleanup_exit_code=0 elapsed_ms=1787606`，即 `29:47.606`；`52` 次冷启动、`173` 次安全复用，仅跳过缺少本机外部快照的 `3` 个场景 | 2026-08-30 |
| `LINUX-MECHA-MEMORY-CALIBRATION-0180` | 通过 | 固定机甲骑士快照在 Linux 原生 headless 的首轮搜索为 `14282.3 ms / 4,390,908,424 B`（累计分配，非峰值内存），第 `7` 回合结束、第 `3` 回合开始复用，runId `531a3a280ab24e89bad2a3536da8ecd6`。Linux 分配门槛按平台差异校准为 `4,500,000,000 B`，余量 `109,091,576 B`（`2.485%`）；Windows 命令的 `4,300,000,000 B` 门槛保持不变 | 2026-08-30 |
| `PR6-ARMAMENTS-IMPLICIT-INTEGRATED` | 通过 | 手牌仅有武装和未升级打击；原版隐式升级唯一候选后，部署器按请求时冻结的身份核销同一实例，再打出升级后的打击并于首回合结束战斗。原生选择 `visible=0 / selected=1 / search=0`，增量回放一致，计划外重算 `0`。runId `d1bc59759edf4c91a9c89f4bd6e6b2d4` | 2026-08-30 |
| `PR6-RNG-DETERMINISTIC-A/B` | 通过 | 同一 PID、相同 seed `PR6DETERMINISTIC` 连续两次建立史莱姆战，敌人均为 `LEAF_SLIME_S / TWIG_SLIME_M / TWIG_SLIME_S` 且生命上限为 `12 / 27 / 7`；两次均第 2 回合结束、计划外重算 `0`，第二次明确复用同一测试进程并正常退出。runId `cc320b89536149398707300e4c9258be`、`38c0fde339ae49e0b45524d3ce2c545d` | 2026-08-30 |
| `PR6-VIGOR/SELF-KILL-INTEGRATED` | 通过 | 骇鳗猛烈摆动携带活力时，完整攻击前/攻击后生命周期与原生严格一致，runId `0eac2a0e2d904b8bb332fb1c76c18d57`；七项怪物行动差分含毒气炸弹自爆并通过死亡结算，runId `edd957f4731445e9a300f9c0cad9646f` | 2026-08-30 |
| `PR6-EMOTION-CHIP-EXTRA-TURN-INTEGRATED` | 通过 | 琥珀灰触发额外回合；当前回合使用放血受伤后，情感芯片的损血窗口在跳过敌方阶段时正确滚动，额外回合开始的充能球被动与原生完整状态一致，实际伤害 `3`。runId `aaa8223a916b4b24b8c2983596010e31` | 2026-08-30 |
| `TOASTY-FIRST-TURN-USER-BOUNDARY-0190` | 通过 | 烘焙手套原生手牌页显示后开始搜索；计划就绪时仍为 `Selected=0 / CardsPlayed=0`，模拟玩家启动后才确认选择。严格增量/完整回放一致，开启结束回合变差复核后完整自动执行到第 2 回合，计划外重算 `0`。runId `a2a1fd688e71465d9458c5cbb1c743d4` | 2026-08-30 |
| `TOASTY-PHANTASMAL-BUNDLE-0190` | 通过 | 使用花园幽灵鳗问题包的战前牌组、遗物与 RNG；首回合计划展示时未选牌、未出牌，玩家启动后完整自动执行到第 5 回合。结束回合复核开启，计划外重算 `0`；严格完整回放从同一份首回合准备选择起步。runId `0451b77331a94c96ae507e2ccdb4603a` | 2026-08-30 |
| `UPLOAD-HARDENING-PR4-FINAL` | 通过 | 真实问题包导出保持完整夹具且移除联系QQ与本机绝对路径；本地假服务验证 multipart 三字段不变、字节进度到 `100%`、非 JSON/数字编号的成功响应回退客户端提交编号、超长描述联网前失败、超长错误响应截断并折叠换行。设置面板同时存在隐藏初始进度条与单实例上传按钮状态。导出/脱敏与上传协议 runId `8a4144cb3a264bb7abf33ea6461ddcb7`；最终 UI/进度状态 runId `c90d6b854c3446ffad4c09b4da1753bf`；最终成功响应兼容矩阵 runId `85861ebc16a04cb09fbd9894bfb3d088` | 2026-08-30 |
| `UPLOAD-PROGRESS-CANCEL-CONFIRMATION-NEXT-FINAL` | 通过 | 取代上一条中“任意成功响应回退客户端编号”的旧口径：文件发送完成只显示到 `95%` 并进入“等待服务器确认”，只有反馈编号与实收字节数匹配才确认成功。假服务分别在正文传输中和等待回执时取消，任务均在两秒内结束；无效回执与大小不一致均保留本地包。真实接收服务 test ZIP 返回 HTTP `201`、反馈编号 `9264c0f65854423e8254de5ff5e5449f` 并确认 `259 B`。runId `f328c2f5fe9f4ccca11826bcbb8b1f6c` | 2026-08-30 |
| `UPLOAD-DIRECT-STATE-OWNERSHIP-NEXT` | 通过 | 正式上传不继承游戏进程中指向失效 `127.0.0.1:7890` 的代理；同一环境下直连 test ZIP 于 `427 ms` 返回 HTTP `201`，反馈编号 `d186e8a6495d4f0291529595667e0a43`，实收 `259 B`。孤立状态转移夹具验证活动态与按钮文字可在同一主线程回调内切回空闲；后续实机证明该全局回调本身可能不被消费，最终实现由下一条面板完成邮箱回归取代。runId `ba1c33c4e68946129314dff1a61928cf` | 2026-08-30 |
| `UPLOAD-PANEL-MAILBOX-LIFECYCLE-NEXT` | 通过 | 实机已经记录 HTTP `201 / 1,340,897 B` 后仍卡等待，证明全局 dispatcher 未消费上传终态。上传会话改由设置面板完成邮箱独占；成功与取消两条路径都在面板进程中消费终态、收起进度条、释放令牌并恢复空闲按钮，“正在取消…”不再等待搜索 dispatcher。runId `fa5ba87bf06d4dac9c17b052192d0be8` | 2026-08-30 |
| `KNOWLEDGE-LIVE-END-RISK-BASELINE` | 失败（修复前基线） | 开启结束回合实时战损复核后，知识恶魔敌方回合诅咒计划被放入普通选牌游标；真正提交结束回合前稳定抛出“回合开始仍有 1 个计划选牌没有触发”，随后测试超时。runId `bc12f0e513ec4634b5c2f3dea0f84a66` | 2026-08-30 |
| `KNOWLEDGE-LIVE-RISK-CHOICE-PHASE-NEXT` | 通过 | `MONSTER-MOVES-BATCH-007` 在既有 10 项严格差分后，额外强制知识恶魔诅咒行动并向实时战损复核提供 `MIND_ROT` 计划；复核按来源和次数消费该计划，诅咒计数精确前进 `1`。runId `901066cdbaaa41329876325cd8a06ad5` | 2026-08-30 |
| `KNOWLEDGE-LIVE-END-RISK-FIXED` | 通过 | 开启结束回合实时战损复核，首轮路线计划 `MIND_ROT`；提交结束回合后原生页面完成选择、玩家获得对应 Power，计划外重算 `0`。runId `a43e0dc90cad444989efde50a99ba33b` | 2026-08-30 |
| `KNOWLEDGE-LIVE-END-RISK-INCREMENTAL` | 通过 | 与上一项相同的实时战损复核路径同时开启严格增量校验；初始搜索、完整回放、结束回合后的原生 `MIND_ROT` 选择一致，计划外重算 `0`。runId `b199bf29f0054c1789e1a2c2d2886435` | 2026-08-30 |
| `KNOWLEDGE-ANGER-BUNDLE-FIXED` | 通过 | 从铁甲战士问题包恢复战前存档、精确牌堆和 RNG；完整自动执行到第 6 回合结束，实机打出愤怒并通过两次知识恶魔原生选牌，计划外重算 `0`。runId `28ac269f1be64674976a8d5075965947` | 2026-08-30 |
| `KNOWLEDGE-TOASTY-BUNDLE-FIXED` | 通过 | 从静默猎手问题包恢复战前存档、精确牌堆和 RNG；开启实时战损复核后完成首个知识恶魔原生选牌并获得瓦解，计划外重算 `0`。runId `9d988afbd8f240e3af519755d35aff3b` | 2026-08-30 |

## 0.19.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `MONSTER-ATTACK-VIGOR-LIFECYCLE-POST018` | 通过 | 骇鳗攻击执行完整攻击前/攻击后生命周期，活力层数与原生严格一致。runId `9994be6016f345f892c6dbc3040384e2` | 2026-08-30 |
| `POST018-TERROR-EEL-CONTINUATION` | 通过 | 骇鳗完整自动执行到第 6 回合，跨回合计划外重算 `0`。runId `acd9c91efb7443edb5a19737d7aba92b` | 2026-08-30 |
| `POST018-TERROR-EEL-CONTINUATION-INCREMENTAL` | 超时，不计通过 | `360s` 上限内仍停留于首回合，没有完成断言；不作为增量等价证据。runId `7c53821b28fd4f84b337b673d9507adb` | 2026-08-30 |
| `POST018-FABRICATOR-CHOICE-AND-AI-REUSE` | 通过 | 条件行动包含自身的队友计数；暴政选择按玩家回合开始阶段接管，完整自动执行到第 9 回合且零重算。runId `766852f4a7574342959cdf9ca7e940b0` | 2026-08-30 |
| `POST018-DECIMILLIPEDE-UPGRADE-CHOICE-DEPLOY` | 通过 | 千足虫升级选牌完成原生部署，完整自动执行到第 4 回合且零重算。runId `dc190ef3c1784f27b0d9e2ef85f49b80` | 2026-08-30 |
| `POST018-OVICOPTER-CONTINUATION` | 通过 | 产卵飞虫完整自动执行到第 5 回合，计划外重算 `0`。runId `c8e98d81c2c24c7d9de1baeb2d67e6ce` | 2026-08-30 |
| `POST018-BYGONE-EFFIGY-TOOLS-CHOICE` | 通过 | 必备工具选择按下一玩家回合阶段消费，完整自动执行到第 7 回合且零重算。runId `1dbadbe59d9147879d62f44d38a55cf0` | 2026-08-30 |
| `POST018-KNOWLEDGE-ANGER-END-RISK` | 通过（历史邻接覆盖） | 完整自动执行到第 8 回合且零重算，但该场景没有让实时战损复核与知识恶魔敌方回合选择同时进入同一个模拟根，不能覆盖本次问题；由下一版本的实时复核专项取代。runId `2612caee23e14c42a274aa7567ae2251` | 2026-08-30 |
| `POST018-SCROLLS-AUTO-CHOICE-PHASE` | 通过（邻接覆盖） | 三卷轴怪当前路线首回合结束战斗，自动执行不中止且零重算；第 2 回合必备工具由独立阶段回归覆盖。runId `a9f7d23ea7d5439c97a78ddf327d520f` | 2026-08-30 |
| `POST018-SOUL-NEXUS-GRID-SCROLL` | 通过 | 原生 37 张卡牌网格滚动到底部并通过真实节点选择主宰，完整自动执行到第 7 回合且零重算。runId `b1a13ffbde4c46a6b64825cb2a3e8049` | 2026-08-30 |
| `POST018-OVERGROWTH-ENTROPIC-CONTINUATION` | 通过（未复现旧重算） | 从蔓生爬虫问题根完整执行到第 2 回合且零重算；旧包在搜索期间实机药水栏与 RNG 已变化，因此保留为证据不足。runId `17fb59dfc1284928912eba1644b1ead5` | 2026-08-30 |
| `POST018-TEST-SUBJECT-LOOT-CURRENT` | 通过 | 满手后的战利品生成与后续回放完整执行到第 12 回合，计划外重算 `0`。runId `7d95638f6d6f4a058221cb3507c05187` | 2026-08-30 |
| `POST018-AEONGLASS-CHOICE-CURRENT` | 通过 | 永世沙漏原生选牌与重新接管完整执行到第 6 回合，计划外重算 `0`；更优路线仍属暂缓项。runId `c51d8cf8f47e43ccb7e4a922361688d8` | 2026-08-30 |
| `POST018-ENTOMANCER-CLUMSY-CONTINUATION` | 通过 | 养蜂人塞入笨拙后的牌堆和洗牌续用一致，完整自动执行到第 4 回合且零重算。runId `066e8d5e64ff4eb3a2b0b2e8c4853685` | 2026-08-30 |
| `POST018-KNIGHTS-FAILURE-CURRENT` | 通过（入口覆盖） | 当前路线首回合结束，最终回放入口不再失败且零重算；不宣称复现旧 9 回合路线。runId `d19d34b506cd4b67a863e124293bfaf1` | 2026-08-30 |
| `POST018-DOMINATE-VICIOUS-ORDER` | 通过 | 主宰施加易伤后，凶恶先抽牌、地狱狂徒先自动打出攻击，随后才按当前易伤获得力量；原生与预测完整状态一致。runId `ad8f66a8594d436dac6a1c5fafb0e313` | 2026-08-30 |
| `POST018-HEX-DEATH-COVERAGE-FINAL` | 通过 | 两只幽灵骑士连续施加恶咒，后施加者死亡保留、初始施加者死亡移除；Power 与逐张卡牌状态三段差分一致。runId `d53826f1d06a487fbca60ffb740892f7` | 2026-08-30 |
| `POST018-CUSTOM-OVERRIDE-ASSERTION` | 通过 | 中档基础上覆盖短搜/深搜单节点出牌分支为 `23/37`、No-GC 为 `7 GB` 后，预设身份与三个实际值均按自定义配置断言。runId `011e9c0e7daa4564867e8195a6299a11` | 2026-08-30 |
| `POST018-BYRDONIS-EXACT-DEFERRED-BASELINE` | 通过（质量基线） | 从问题包回放状态恢复精确牌堆与 RNG，当前路线预计战损仍为 `55`，玩家手操上界为 `41`；只固化差距，不计作策略修复。runId `b4e2837cecc04cb59dad6a4e4be6cb37` | 2026-08-30 |
| `POST018-TEST-SUBJECT-EXACT-DEFERRED-BASELINE` | 通过（质量基线） | 从问题包回放状态恢复精确牌堆与 RNG，当前路线预计战损仍为 `20`，玩家手操上界为 `0`；只固化差距，不计作策略修复。runId `cad36ba7007d40e392ee882c49be2308` | 2026-08-30 |
| `POST018-DETAILED-PLAN-REPLAY-STATE` | 通过 | 详细诊断在最终路线回放与实机部署动作后写出能量、手牌、抽牌堆、弃牌堆、消耗堆及敌方生命/格挡；最小战斗完整结束。runId `1e83a3bc0d864e278fba5a8a20d66b96` | 2026-08-30 |

## 0.18.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `MAKE-IT-SO-FINISHED-HISTORY-018` | 通过 | 独立回合从 0 次技能历史开始，逐张断言如此甚好在前两张技能后留在弃牌堆、第 3 张后回手，实机与模拟一致。runId `5d226c100ea949f1bc498a01a7961106` | 2026-08-29 |
| `NEUROSURGE-MUTABLE-POWER-018` | 通过 | 精神过载及同批 47 项卡牌施加 Power 后，生命、能量、牌堆、Power 和怪物状态实机差分一致。runId `fa74bbbe3c4245188cde369d7bcf6144` | 2026-08-29 |
| `LIVE-END-TURN-RISK-CHOICE-REUSE-018` | 通过 | 惊逃在结束回合风险复核中自动打出头槌，复用路线选择将盛怒置顶，选择顺序与消费数严格一致。runId `216d8643891f442689074fa5f6f7954e` | 2026-08-29 |
| `FOREGONE-CONCLUSION-DELAYED-DRAW-018` | 通过 | 既定事项页面暂停回合准备时，先执行原版 `AfterSideTurnStart` 再确认选牌；下回合多抽一张在正式抽牌前移除，选中 3 张后总手牌为 8。实机与模拟严格一致。runId `a65c0460238c467c803394b5c07a59c5` | 2026-08-29 |
| `OWL-MAGISTRATE-TURN-SETUP-REUSE-018` | 通过 | 从猫头鹰法官问题包战前状态完整自动执行，既定事项、辉光和回合准备选牌跨回合保持一致，第 5 回合结束前计划外重算 `0`。runId `cd2f163cd0cd41f5af6183fe9b4fec5c` | 2026-08-29 |
| `SPECTRUM-FOREGONE-ORDER-018` | 通过 | 光谱偏移先生成随机无色牌，既定事项随后把 3 张牌移入手牌，再执行普通抽牌；有序牌堆、Power 和 RNG 与实机严格一致。runId `96f54d71babf4c3ba4871dd5400953a8` | 2026-08-29 |
| `KNOWLEDGE-POWER-REAPPLICATION-ORDER-018` | 通过 | 从知识恶魔问题包战前状态完整自动执行；敌方诅咒选择会话正常退出，第 11 回合重新施加既定事项后保持正确 Power 监听顺序，第 12 回合结束战斗，计划外重算 `0`。runId `bdf89c2bf77341ee8da1c7f68b2d2161` | 2026-08-29 |
| `CURRENT-BUNDLE-DIRECT-COVERAGE-018` | 通过 | 当前源码直接重放地道虫、外骨骼虫、感染棱柱、活体盾与高塔炮手，以及连枷骑士、幽灵骑士与魔法骑士问题包，分别越过原初始化、计划外选牌、实时风险选牌、精神过载动作回放及如此甚好部署找牌错误；runId `edce410703414934a3f3259429b10d26`、`6a32338eb2f24b44aa45cf731c845163`、`417c1b5707924606816351b1aa339c21`、`b2d4cb91395e46ccb944da5a8558192b`、`0d737a4acb9e4a85a9365f296562311c` | 2026-08-29 |
| `POWER-ROOT-INTERNAL-STATE-018` | 通过 | 鬼祟珊瑚群第 2 回合继承本回合已受到的 `9` 点伤害，搜索与实机的回合伤害上限一致；完整自动战斗在第 5 回合结束，计划外重算 `0`。runId `b126492a542c4c3d85bc47f0bffe0b1c` | 2026-08-29 |
| `EMOTION-CHIP-HISTORY-ROLL-018` | 通过 | 情感芯片触发充能球后保留上回合失去生命的记录，直到敌方回合结束再滚动；完整自动战斗在第 5 回合结束，计划外重算 `0`。runId `e265c0a6887f48ea892c2e5489236721` | 2026-08-29 |
| `FAN-OF-KNIVES-SHIV-TARGET-018` | 通过 | 刀扇生效后，小刀按全体攻击生成无目标动作并与实机一致；永世沙漏问题包完整自动执行到第 7 回合，计划外重算 `0`。runId `e825a7e0fe244d5b8be45086c3f3d7ef` | 2026-08-29 |
| `UPROAR-ECHO-FORM-AUTOPLAY-018` | 通过 | 回响形态重放骚动时，骚动自动打出的集中打击读取已经开始的外层出牌系列，因此只结算一次；敌人生命、集中、牌堆、能量与 RNG 的原生/预测完整状态一致。runId `06550cd755b246c7b044865469541422` | 2026-08-29 |
| `TEST-SUBJECT-GC-ECHO-FINAL-018` | 通过 | 实验体原问题包在首轮搜索与全自动请求重叠时只执行一次 No-GC 滚动回收，不再循环触发 `before_next_search`；随后连续复用并在第 8 回合结束，计划外重算 `0`。runId `913d3393e919438fbf2d7635ce318b2b` | 2026-08-29 |
| `DECISIONS-REPEATED-CHOICE-BUDGET-018` | 通过 | 抉择，抉择的三次自动出牌共享整张牌的手牌选择分支预算；储君实验体原包首轮短搜返回后连续精确复用 10 回合，第 11 回合结束，计划外重算 `0`。runId `c8c9f6f2edda40bd87bc2bf5e6b20520` | 2026-08-29 |
| `TURRET-RELIC-ANNOTATION-018` | 通过 | 活体盾与高塔炮手原问题包的首轮最终遗物标注正常完成；完整自动执行到第 4 回合，计划外重算 `0`。runId `6d1e48b181824f0fbebc43525c959403` | 2026-08-29 |
| `MECHA/SOUL-NEXUS-REPLAN-018` | 通过 | 机甲骑士与静默猎手对灵魂枢纽的两份原问题包分别完整自动执行到第 4、5 回合，计划外重算均为 `0`。runId `cb31ec9272284f579bbf6efa942281e0`、`2b11153f74af4766848d585e8372d62f` | 2026-08-29 |
| `WATERFALL-SMART-MARGINAL-POTION-018` | 通过 | 瀑布巨兽原问题包的智能用药路线只保留痊愈药水；独立无药反事实确认预计省血 `10/9`，再生药水不再借用另一瓶药水的收益通过门槛。runId `dbce824e7919490882bd6022f2ebc394` | 2026-08-29 |
| `MYTES-SMART-BLOCK-POTION-018` | 通过 | 异螨原问题包在第 2 回合实际使用格挡药水，完整自动执行到第 5 回合，预计省血 `9/9`，计划外重算 `0`。runId `1959c1dd79c743958a6f921f727d6cae` | 2026-08-29 |
| `LOST-FORGOTTEN-REQUIRED-POTION-BOUNDARY-018` | 通过 | 失落之物与遗忘之物原问题包在“至少使用一瓶”下正常返回一瓶药水的边界路线，不再把已展开的流动铜液与能力药水误报为没有可执行路线；该短预算结果仍为死亡边界。runId `d3fa5886b45e46948093c0d42518f79d` | 2026-08-29 |
| `QUEEN-POTION-POLICY-DISABLED-018` | 通过 | 女王原问题包全程记录“禁用药水”，路线按设置保留稳定血清与固化药水；玩家手动使用稳定血清后，本局已用药数正确增加。该项是设置行为，不是药水适配失败 | 2026-08-29 |
| `TEST-SUBJECT-REQUIRED-POTION-QUALITY-018` | 通过 | 实验体同一起点、高档预算成对复跑：智能模式 0 瓶、预计战损 `78`；至少使用一瓶时选择肌肉药水、预计战损 `74`，不再发生强制用药后战损上升。runId `12dc53ed6dc1469d9e0bae71a1b14b2e`、`6a4b981c9c7c4fb7999304980c33f00c` | 2026-08-29 |
| `INSATIABLE-REQUIRED-POTION-BOUNDARY-018` | 通过 | 无厌沙虫原问题包在“至少使用一瓶”下返回包含第 4 回合易伤药水的边界路线，不再因长战斗尚未搜索到完整胜利而误报没有可执行路线；该结果仍为死亡边界。runId `6563e660595d4b35932719281320b5c3` | 2026-08-29 |
| `HISTORY-COURSE-STAMPEDE-PAELS-EYE-018` | 通过 | 历史课在惊逃的回合末自动攻击之后记录上一回合最后一张非复制攻击牌；佩尔之眼只统计玩家主动出牌。永世沙漏原问题包完整全自动执行到第 5 回合，包含额外回合，计划外重算 `0`。runId `0b1d38c391d04a36a7a9cc14c5122d5f` | 2026-08-29 |
| `AEONGLASS-EXACT-PILE-ART-ROUTING-018` | 通过 | 从永世沙漏问题包战前存档恢复跑局与 RNG，并固定首手和 29 张有序抽牌；路线实际打出灵动步法+，预计战损 `27`，低于包内原路线的 `42`。runId `0c2c35a7510b4e0f9846fca740aa80c1` | 2026-08-30 |
| `QUEEN-ART-OF-WAR-LANE-REGRESSION-018` | 通过（邻接回归） | 女王战前重建在中档预算、智能用药下预计战损 `18`，低于回归上限 `26`；该场景只证明孙子兵法专用通道没有影响无关战斗，不作为第 18 项“更优解”的同根证明。runId `8d69269258924485aef31a0325d1d3c1` | 2026-08-30 |
| `QUEEN-EXACT-PILE-MANUAL-QUALITY-018` | 通过（未追平手操） | 固定女王问题包的 7 张首手和 32 张有序抽牌后，当前路线主动打出余像与计划妥当，预计战损 `9`；包内旧求解路线为 `26`，玩家手操路线实测为 `5`。runId `598bc5d8e86b4094bbf96c04942e192e` | 2026-08-30 |

## 0.17.2

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `QOL-CONTROLLER-STOP-172` | 通过 | 搜索时主操作按钮为“停止计算”；停止后当前搜索取消且自动回合入口不能重启，点击“重新计算”恢复。手操预计战损 `7 -> 3` 时记录差值并显示绿色反馈；消息区域启用整行自动换行。runId `bbbffd3cf1cd4d8686472175a44ed64e` | 2026-08-29 |
| `PERFORMANCE-PRESET-LOW/MEDIUM/HIGH/VERY-HIGH-172` | 通过 | 四档固定解析依次为 `5/60s + 6GB`、`8/120s + 8GB`、`12/180s + 12GB`、`20/300s + 16GB`，Beam、节点与出牌分支均匹配规格并完成首回合战斗。runId `c18b796053064ffb89eebde8da49fa69`、`516c303b65d547fb9e60fa34d79ca3b5`、`ce61ed362f5143fc9d69ee8b9763eb2c`、`9791b831f1ac4b6ca296fe28811f81c4` | 2026-08-29 |

## 0.17.1

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `SLY-AUTOPLAY-NESTED-CHOICE-0171` | 通过 | 手牌戏法给杂技附加奇巧，生存者弃掉杂技后触发自动出牌与杂技自身弃牌；有序牌堆、逐牌状态、Power、RNG 和续用状态严格一致。runId `97af4be8cb104d15baac75fe1e4c3701` | 2026-08-29 |
| `TRIGGERED-SHUFFLE-CHOICE-ORDER-0171` | 通过 | 既有早有准备与升级杂技洗牌选牌顺序保持严格一致，确认 PR 没有覆盖当前 0.17 的战略选择修复。runId `1cd79d4025cd4c5698ec2ac0edc39f4e` | 2026-08-29 |
| `OSTY-RATTLE-TURN-COUNTER-0171` | 通过 | 第一回合让奥斯提攻击，完整结束回合后在第二回合打出猛晃；上回合攻击与命中计数均已清零，伤害及完整状态与实机一致。runId `1d3f201d5c75487f9cf9b70d67635dcf` | 2026-08-29 |
| `AUTOPLAY-ADJACENT-REGRESSION-0171` | 通过 | 抽牌触发、回合末自动出牌和 32 项卡牌完成生命周期严格差分全部通过。runId `2552b97f8b024ebebf8fbe268377086b`、`03b7d9432aeb4024bf11c2d5cc3a8ed3`、`03944b3c91844c8799965f40166b261f` | 2026-08-29 |

## 0.17.0

本节只登记本批次实际运行的验证。需求原文和计划不作为测试通过证据。

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `OPENING-STRENGTH/DEXTERITY-POTION-0170` | 通过 | 力量药与敏捷药均为最终路线首个动作、位于首张牌之前；runId `a65e0ce7c1e1478c949052d86ed799a7`、`c6217b9d975e4ecbaf0e26a4a3dd7a5d` | 2026-08-29 |
| `LIZARD-TAIL-LIVE-REUSE-0170` | 通过 | 1 HP 触发蜥蜴尾巴后，首轮与第 2 回合复用均保留整场战损 1；路线有“蜥蜴尾巴：复活”，计划外重算 0。runId `735a14adb16241719f220badb89f00a9` | 2026-08-29 |
| `BRIGHTEST-FLAME-TERMINAL/NECESSARY-0170` | 通过 | 同样无伤可胜时不打至亮之焰；必须用它完成当回合击杀时，路线保留 78 最大生命与 2 点当前损失。runId `c6b2b17afea34e9d8befaf3f32401f36`、`207af957f0664c478201bcd4c49bffd2` | 2026-08-29 |
| `BATTLEWORN-DUMMY-V1/V2/V3-KILL-0170` | 通过 | 三档训练假人均以自伤攻击完成击杀，不用安全停滞替代目标；runId `319a22b784414d4d8f75559ecaa21779`、`9f7666436f4c4451907971ae732211b1`、`b61f285e96ce40e2bef847e1c708250c` | 2026-08-29 |
| `BATTLEWORN-DUMMY-EVENT-DEFEAT-0170` | 通过 | 倒计时耗尽返回 `EventDefeat`，不授予胜利。runId `963ddd67b7db402da7a46f17a73cd7a3` | 2026-08-29 |
| `TWO-CARD-INFINITE-DEPLOY-0170` | 通过 | 亮剑/亮技双卡无限执行 19 个动作、18 次洗牌，当回合零战损击杀；完整自动执行计划外重算 0。runId `54b78ec8e2ef4baf80a452ff0744a81f` | 2026-08-29 |
| `ANGER-COMPACT-ALTERNATIVE/REQUIRED-0170` | 通过 | 等价击杀选择切割且不打愤怒；只有愤怒可击杀时仍使用。runId `03bfc9e0b0d44389aaba29c74f6a99fa`、`64a3b6d6d4304bdd9c6db48386983122` | 2026-08-29 |
| `AEONGLASS-ANGER-MIDCOMBAT-0170` | 通过（近似重建） | 按问题包第 9 回合手牌、生命、格挡、Power 和行动历史近似重建，路线不再加入愤怒。该夹具仍只找到死亡路线，省略完整消耗堆与部分历史，不作为原包战损复放。runId `58125330c52a4552b196021df614298e` | 2026-08-29 |
| `BECKON-CROSS-TURN-DEPLOY-0170` | 通过 | 首动打出呼唤，预计整场战损 4，第 2 回合自动击杀，计划外重算 0。runId `41198704657b42d284ff24113dbc429b` | 2026-08-29 |
| `GENETIC-ALGORITHM-REPLAY / GOOPY / SCYTHE-0170` | 通过 | 遗传算法华彩重放累计成长 6，并在第 2 回合继续执行、计划外重算 0；黏糊防御成长 1；巨镰成长 5，三者均在同战损胜利路线中主动培养。runId `6097ecc0ab3142a0a6c0ee187c1eda54`、`4b36668f89c449ca8eeb5ea6e6e1d2e4`、`4420c21826404907b33a1e9543949cdc` | 2026-08-29 |
| `NIGHTMARE-CLONE-GROWTH-BOUNDARY / SOULS-POWER-GROWTH-0170` | 通过 | 梦魇 `Clone` 不带 `DeckVersion`，因此不虚构跑局成长；灵魂之力跨回合培养至少 6。边界验证 runId `6e616ddf5c9b45b9a7c20434a8f912c1`，灵魂之力 runId `4e265950217046f886890458d2728220`；错误保留跑局版本会在第 2 回合产生状态差异，失败证据 runId `6112275ee763406abe03f23dfdc5238c` | 2026-08-29 |
| `FEED / THE-HUNT / HAND-OF-GREED-FATAL-0170` | 通过 | 三类斩杀分别获取最大生命、卡牌奖励和金币，且优先于普通等价击杀。runId `1e25b558793e445cbfa2394b23e2ef7a`、`41205c552552424197c3dde4827fa0f7`、`716c7e94d0c44b659672a8954b47de20` | 2026-08-29 |
| `NOT-YET / ROYALTIES / FORBIDDEN-GRIMOIRE / ALCHEMIZE-0170` | 通过 | 同战损胜利中依次保留治疗、金币奖励、移除奖励和生成药水；runId `72e453a92041468e8b041df280512ecb`、`3d1538b146e249b6b9debbb1a84ee54c`、`62943077e9a241ff90097518571d6dfc`、`532ddda82beb48fe815a0f66d8528d06` | 2026-08-29 |

## 0.16.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `NIBBITS-DUPLICATE-TORIC-0160` | 通过 | 从啃咬兽问题包战前状态直接注入两份坚韧之环。正式搜索跨 8 回合返回，runId `254746adc3a54c52ad894279e310d1e6`；严格差分验证两份实例以 `Block=5/8` 分别触发，合计获得 13 格挡并剩余总层数 1，runId `1eedfd2234a847378a0e79c600fb1012`；问题包世界线完整全自动在第 8 回合结束、计划外重算 0，runId `bcd844191a81453d8d21702024aa0fb9` | 2026-08-29 |
| `KNIGHTS-RELIC-ANNOTATION-REGRESSION-0160` | 通过 | 从三骑士问题包战前存档恢复种子、A10、`108/97/89` HP 与首行动；最终遗物标注的完整路线回放正常完成，返回 3 回合候选，不再出现两张 `BOOST_AWAY` 升级/保留状态交换。当前路线与旧包不同，不记作旧路线逐动作回放。runId `dfdb18f2036e42ecb6beda299d808028` | 2026-08-29 |
| `CHOICES-PARADOX-SCROLLS-0160` | 通过 | 使用咬人卷轴问题包的战前存档、种子、A10、四敌生命和行动重建首回合；验证选择悖论原生页面先显示、搜索后启动、Mod 自动选牌，路线第一组胶囊以“选择悖论：选择 ”开头。短搜 `5891.5 ms`，比较 `5883` 个选牌分支，runId `b9fa371a5b29479bb97c19da7980526f`；最小五候选夹具 runId `220482eb7fbc4f459c6f970748b0e033` 同样通过 | 2026-08-28 |
| `RINGING-HAVOC-AUTOPLAY-0160-FINAL` | 通过 | 仪式兽施加昏眩后，破灭作为本回合第一张牌正常结算；其翻出的重振被原版 `CardPlaysStarted` 规则阻止，不获得格挡、不消耗手牌防御，重振按破灭规则进入消耗堆。原生与预测完整状态一致。runId `bbc71ec201d34e16a114e2a1769ceb52`；修复前基线 runId `c24277b02b414dc08fdcb59fc7cec21e` 为模拟 5 格挡、实机 0 格挡 | 2026-08-28 |
| `MONSTER-MOVES-BATCH-029-RINGING-0160-FINAL` | 通过 | 既有昏眩相邻回归升级为当前逐实例状态键后，两次 `BEAST_CRY_MOVE` 严格差分通过：第一张牌可打、后续带昏眩的牌不可打，玩家回合末 Power 与全部昏眩状态清除。runId `0b73e8ca09374b0dbb27e41f6f021ec9` | 2026-08-28 |
| `HEADBUTT-EMPTY-DISCARD-0160` | 通过 | 清空全部牌堆后实际打出头槌；弃牌堆为空产生的 `0` 选项原生牌堆请求按空选择完成，第 2 回合精确复用，计划外重算 0。runId `53d9a793c14040d790183727ab0a88cd` | 2026-08-28 |
| `COSMIC-INDIFFERENCE-EMPTY-DISCARD-0160` | 通过 | 清空全部牌堆后实际打出宇宙冷漠；空弃牌堆选择不再中止部署，第 2 回合精确复用，计划外重算 0。runId `b09e6ef038604469b73680803c6916e7` | 2026-08-28 |
| `TORIC-TOUGHNESS-FRAIL-BLOCK-0160` | 通过 | 虚弱 1 层下打出坚韧之环，角色实际获得 3 格挡，但 Power 内部精确保存 `Block=3.75`；原生与预测完整状态一致。runId `e0e74f8d044e4026b9484ae78d03a622` | 2026-08-28 |
| `JAXFRUIT-TORIC-TOUGHNESS-REUSE-0160` | 通过 | 从啪嗒果问题包战前存档恢复种子、A10、双敌生命、首行动与 RNG；第 4 回合精确复用，计划外重算 0。runId `ed4f9715bde9405bab9655fd83701aba` | 2026-08-28 |
| `PAINFUL-STABS-MONSTER-ATTACK-0160` | 通过 | 给酸液攻击怪物注入荆棘，单次穿透格挡的命中后弃牌堆精确加入 1 张伤口；原生与预测完整状态一致。runId `b89e025cf429450595e4d38f2e603c90` | 2026-08-28 |
| `POWER-DAMAGE-HOOKS-REGRESSION-0160` | 通过 | 14 组伤害与攻击钩子严格差分全部通过，覆盖荆棘、吸取、活力、缓冲等，确认怪物攻击接入共享 `AfterAttack` 后没有重复结算。runId `50849d4ecff04661a7254b529611c74e` | 2026-08-28 |
| `TEST-SUBJECT-PAINFUL-STABS-REUSE-0160` | 通过 | 从实验体问题包战前存档恢复种子、A10、牌组、遗物、首行动与 RNG；越过第二形态多爪与荆棘，至第 6 回合持续精确复用，计划外重算 0。runId `a267600d977549f1a492d36479394f60` | 2026-08-28 |
| `VANTOM-UPGRADED-CARD-SHUFFLE-0160` | 通过 | 从 Vantom 问题包战前存档恢复种子、A2、牌组、首行动与 RNG；普通/升级打击跨洗牌顺序一致，第 5 回合精确复用，计划外重算 0。runId `e877c8239def4647a36c7d5102c940f3` | 2026-08-28 |
| `INSATIABLE-INVOKE-CROSS-CHARACTER-0160` | 通过 | 静默猎手打出召唤后推进到第 2 回合；原生与预测均创建 `2/2` 奥斯提并施加 1 层“为你而死”，两项下回合 Power 被消费，额外能量与 5 张手牌严格一致。runId `ec2f0a77e09a424fad6b8f78f2460c7e`；既有亡灵契约师奥斯提卡牌与伤害转移回归 runId `037a7a3ec6bc48f797913d398f0dfde1`、`c09e18e9f28b47e1a5c528495c62c124` 同时通过 | 2026-08-28 |
| `INSATIABLE-INVOKE-SEARCH-0160` | 通过 | 无厌沙虫固定为液化地面，静默猎手只有召唤与 5 张防御；正式 Short 搜索越过原 `EndTurn → SUMMON_NEXT_TURN_POWER` 初始化错误，正常返回 5 回合候选、1 个可执行动作、未镜像项 0。runId `0765ed5133604dcb9fab017fa8e30f42` | 2026-08-28 |
| `PALE-BLUE-DOT-FIFTH-CARD-DRAW-0160` | 通过 | 注入 2 层暗淡蓝点后恰好打出 5 张牌并进入下一回合；原生与预测都在第五张触发，下回合均抽基础 5 张加额外 2 张，瞬时抽牌 Power 均已消费。runId `cc6470a2161c417bbf64e5a672a67367` | 2026-08-28 |
| `TRIGGERED-SHUFFLE-CHOICE-ORDER-0160` | 通过 | 两个严格差分场景分别用早有准备和升级杂技触发空抽牌堆洗牌；战略选择先从洗牌后的抽牌堆拿走打击，随后卡牌自身选择把同一张打击置顶或弃掉，原生/模拟完整状态一致。runId `8da0adbda1484b8f8131cadd30e60d2c` | 2026-08-28 |
| `DECIMILLIPEDE-TRIGGERED-CHOICE-REPLAY-0160` | 通过 | 从千足虫问题包战前存档、种子、三段生命和三个首行动重建初始搜索，正常返回候选且没有再次出现第 17 回合早有准备双 pending 异常。当前路线与包内旧失败分支不同，不记逐动作回放。runId `967571405a5c4a78851c5310fcdd303a` | 2026-08-28 |
| `AEONGLASS-TRIGGERED-CHOICE-REPLAY-0160` | 通过 | 从永世沙漏问题包战前存档、种子、A10 和 `EBB_MOVE` 重建强制短搜，越过原第 6 回合杂技双 pending 边界并返回候选。当前路线与包内旧失败分支不同，不记逐动作回放。runId `d85101a57dcc4d4d910a2e159e02e6c0` | 2026-08-28 |
| `KNOWLEDGE-DEMON-GLAM-POCKETWATCH-0160` | 通过 | 注入怀表和带华彩的升级后空翻，后空翻以一个路线动作完成两次 CardPlay；推进到第 2 回合后原生/模拟牌堆、抽牌及怀表私有计数严格一致，均为 `POCKETWATCH/0/2`。runId `c82c4fcbadda41dc96df6d65cf0e0d63`；问题包 Custom/Low 战前跑局均未在夹具上限内完成首搜，不记通过 | 2026-08-28 |
| `CARD-UPGRADE-STABLE-SHUFFLE-0160` | 通过 | 武装只升级两张同名防御中的一张，两张牌以升级/普通顺序进入弃牌堆后触发洗牌；修复前第 2 回合严格差分稳定得到普通/升级防御错位，runId `1718b01532d94f34b65acc79a246482a`；改为按分支当前预览排序后原生/模拟完整状态一致，runId `854065893bc742c5ac04e3d6f59e8cdf` | 2026-08-28 |
| `CHOMPERS-UPGRADED-CARD-SHUFFLE-0160` | 通过 | 从啃咬者问题包战前存档重建，完整自动战斗在第 5 回合结束；武装升级后的同名牌跨洗牌顺序与实机一致，计划外重算 0。runId `ddebe062128845f9a3f73fbb6992e3ff` | 2026-08-28 |
| `CHOMPERS-UPGRADED-CARD-SHUFFLE-INCREMENTAL-0160` | 通过 | 同一问题包状态强制短搜并启用增量/完整前缀核对，覆盖 12 回合、3 次洗牌，未镜像项 0，前缀回放一致。runId `0c32515cb2944570a6a748febf928737` | 2026-08-28 |
| `STRATAGEM-PREPARED-CHOICE-ORDER-FINAL-0160` | 通过 | 升级准备充足在空抽牌堆时触发洗牌，战略选择先从三张抽牌堆选一张，随后准备充足抽两张、弃两张并留下打击完成 1 HP 斩杀；增量/完整回放一致，真实原生页面按两次选择顺序完成，计划外重算 0。runId `d305379b208841b68e25f8987e2e1967` | 2026-08-28 |
| `TEST-SUBJECT-PREPARED-CHOICE-SHORT-0160` | 通过 | 从问题包搜索请求检查点固化 5 张手牌、27 张有序抽牌、玩家状态及 `BITE_MOVE` 状态日志；强制短搜越过原准备充足双 pending 失败点，返回 7 回合候选，未镜像项 0。runId `d4903310604044ae8fa0c689a82f8b8d`；整包增量与普通深搜均在 180 秒达到夹具上限，不记通过 | 2026-08-28 |
| `CROSS-TURN-NO-PROGRESS-0150` | 通过 | 仅有一张防御、100 敏捷且完全没有伤害手段；修复前耗满短搜约 22 秒并搜索 54 回合，修复后搜索本体 175.3 ms 结束、剪掉 18 条跨回合无进展分支。runId `5e3fa09b18094a77a07492098e204785`，修复前 runId `ddcb886becc84a28aa8b56dbb067bea9` | 2026-08-28 |
| `BOWLBUGS-CROSS-TURN-NO-PROGRESS-0150` | 通过 | 从问题包战前存档、种子、敌人生命与首轮意图近似重建，仍找到第 6 回合胜利、预计战损 3、零药水；当前没有原生战斗状态导入器，不记作问题包逐动作回放。runId `33c814b90b6b4bdda47fe5b9c98961f9` | 2026-08-28 |
| `SURVIVOR-REPLAY-EMPTY-CHOICE-0150` | 通过 | 爆发与复制使升级生存者执行三次；前两次实际弃完两张牌，第三次原版 `options=0 / select=0..0` 请求按无操作完成，不消费虚构计划。首回合结束、计划外重算 0，runId `207cdd4927f74188948ec903574a3c7c`；修复前 runId `026a459931b24b58be85d644d3778d25` 在同一请求报错 | 2026-08-28 |
| `NATIVE-EMPTY-PLAN-ADJACENT-0150` | 通过 | 复制拾荒在空手时发出两次原生空请求；搜索生成的两条显式空计划逐条核销，首回合结束、计划外重算 0。runId `c73b5302e55e4b06bf56dc35169f3e20` | 2026-08-28 |
| `POCKETWATCH-REPLAY-REUSE-0150` | 通过 | 手牌中的螺旋打击实际结算两次 `CardPlay`，路线仍保持一个出牌动作；怀表逐次计数后第 2 回合命中精确复用，增量分叉与完整前缀回放一致，计划外重算为 0。runId `93d934679e8746de95bebd9dd5ce58e2`；修复前基线 runId `89792141b109409aa2aa5adcc7d2a846` 稳定得到 `expected=1 / actual=2` | 2026-08-28 |
| `POCKETWATCH-REPLAY-FULL-COMBAT-0150` | 通过 | 手牌为螺旋打击、抽牌堆为普通打击，敌人 13 HP；完整自动部署在第 2 回合结束，增量分叉与完整前缀回放一致，计划外重算为 0。runId `106f0b2966dd4225ac9ce1213e123712` | 2026-08-28 |
| `INCOMPATIBLE-GAMEPLAY-MOD-MESSAGE-0150` | 通过 | 预测失败边界断言验证未知第三方玩法 Mod 的玩家提示包含 Mod 名称、标识和卸载建议，不暴露内部订阅器类型；详细异常仍保留 Mod 与订阅器上下文。runId `3b920fd04bb64fdeba536ee825219ea4` | 2026-08-28 |
| `BATTLEWORN-DUMMY-TIMEOUT-BOUNDARY-0150` | 通过 | 第二档假人 150 HP、时间限制 1 层；正式后台搜索在原生逃跑前返回 `EventDefeat`，不移除假人、不授予胜利。runId `1b1d321d7ac941adb5d515efa861d6ee` | 2026-08-28 |
| `BATTLEWORN-DUMMY-V2-EXACT-FINAL-0150` | 通过 | 从第二档训练假人问题包的战前存档、固定牌序和 150 HP 重建，开启增量分叉/完整前缀回放核对并完整自动执行。未击杀分支正确为 `won=False / EventDefeat`，击杀路线为 `won=True / None`；第 2、3 回合精确复用，计划外重算 0。runId `7fbd338febef40668a4980555cc51971` | 2026-08-28 |
| `FAIRY-AUTOMATIC-RESCUE-FINAL2-0150` | 通过 | 1 HP 铁甲战士持瓶中仙女，手牌/抽牌堆各一张重锤；求解器不再判定仅有死亡路线，第 1 回合精灵药自动复活，第 2 回合击杀。首轮路线记录 1 瓶药，实机消耗 `FAIRY_IN_A_BOTTLE`，增量/完整回放一致，计划外重算 0。runId `bbddfcc1e1e54be2a4405e58cd7f557e` | 2026-08-28 |
| `FAIRY-DEATH-LIFECYCLE-FINAL2-0150` | 通过 | 瓶中仙女的自动防死、消耗槽位和 30% 回复与原版完整状态严格一致，已消耗实例不会再次进入死亡监听。runId `e601bec430ea49318ef57a550d8284f8` | 2026-08-28 |
| `ONLY-DEATH-NO-FAIRY-REGRESSION-0150` | 通过 | 相同 1 HP 与酸液攻击下不注入精灵药，首轮仍正确报告仅死亡路线并在第 1 回合死亡，用药数 0。runId `53ad9b78496646b196aa4844794766ef` | 2026-08-28 |

## 0.15.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `VAMBRACE-STABLE-FORK-FINAL-0150` | 通过 | 原版臂铠已经获得本场首次格挡后仍保留触发卡引用；修复后状态可 Fork，触发卡身份和 `BlockGainedThisCombat=true` 均保持。runId `31793a0e83df4656aa0ea3b9182c4c29`；修复前基线 runId `0e1c56fe922c4b4a87109dbd1c06acd0` 稳定抛出问题包同款异常 | 2026-08-28 |
| `TUNNELER-IMBUED-GLACIER-VAMBRACE-0150` | 通过 | 缺陷机器人持臂铠，注能冰川开局自动打出并进入弃牌堆，原版得到 12 格挡；首轮搜索正常返回，未镜像项为 0。runId `a9ebb7b36f554948a8de0f658385aa34` | 2026-08-28 |
| `TUNNELER-IMBUED-GLACIER-VAMBRACE-INCREMENTAL-0150` | 通过 | 同一组合启用增量搜索核对，增量分叉与完整前缀回放一致；开局 12 格挡、未镜像项 0，搜索正常返回。runId `689793cd6343465393fdd567a4a7c41e` | 2026-08-28 |
| `RELIC-CARD-HOOKS-AUDIT-PART-2-VAMBRACE-FINAL-0150` | 通过 | 臂铠连续打出两张防御，第一张 5 格挡翻倍为 10，第二张按普通值获得 5，最终严格为 15；同批遗物 Hook 11/11 通过。runId `eddeca24b4544e918299e4b4bb2a401b` | 2026-08-28 |
| `AXEBOT-THORNS-MULTIHIT-FINAL-0150` | 通过 | 巨斧机器人以 `2 HP`、`2` 层库存执行两连击；玩家持有 `3` 点荆棘和 `12` 格挡。修复前模拟继续执行第二段并产生 `8` 点虚构战损；修复后第一段反伤致死即中止剩余攻击，玩家保持 `75 HP / 2` 格挡，库存重生后的完整实机/模拟状态一致。相邻上勾锤击同时核对攻击者死亡后仍结算虚弱/脆弱。runId `fac22ea0270a4996afa276df384ba370`，基线 runId `fc33aad095054c37a3de730d89472d2a` | 2026-08-28 |
| `AXEBOTS-BUNDLE-FULL-AUTO-FINAL2-0150` | 通过 | 从问题包战前存档重建，以 Low、Instant/0 秒完整自动结束于第 11 回合，`UnexpectedReplans:0`。当前路线与原包不同，不记作逐动作回放。runId `fca4e9e3a09d4ab490271ec6d38ad10a` | 2026-08-28 |
| `AXEBOTS-BUNDLE-INCREMENTAL-FINAL2-0150` | 通过 | 同一问题包状态以 Low/Short 完成增量分叉与完整前缀回放一致性，覆盖 11 回合、3 次洗牌，未镜像效果为 0。runId `c8c8f3675a934f028704ab82e2f7dd4d` | 2026-08-28 |
| `FTL-CROSS-TURN-STATE-0150` | 通过 | 修复前跨回合严格差分稳定复现第 3 张 FTL 少抽一张；最终分支状态实现下，第 3 张抽牌、第 4 张不抽均与实机一致。runId `add8e54810d54f41b5cb6b55dc410892`，基线 runId `6c069ce0891a484091481bd8b3387e35` | 2026-08-28 |
| `CURRENT-TURN-CARD-HISTORY-ADJACENT-FINAL-0150` | 通过 | Fetch 在下一回合经全息影像取回同一实例后重新允许抽牌；Make It So 在本回合第 3 张技能后返回手牌，实机/模拟严格一致。runId `f478c96ec3144c81847c3b225f95866e` | 2026-08-28 |
| `BYRDONIS-BUNDLE-REUSE-FINAL-0150` | 通过 | 从多尼斯异鸟问题包的战前跑局状态重建；第 3 回合精确复用，计划外重算 0，增量分叉与完整前缀回放一致。首抽路线与原包不同，因此不记作逐动作回放。runId `a0c1808cfae745188ae5a3f8d1f28270` | 2026-08-28 |
| `SLITHERING-STRANGLER-BUNDLE-REUSE-FINAL-0150` | 通过 | 从蛇行扼杀者问题包的战前跑局状态重建；越过原第 4 回合重算点并精确复用，计划外重算 0。首抽路线与原包不同，因此不记作逐动作回放。runId `a6b72e6f81f24aa096833902d6860046` | 2026-08-28 |
| `CUBEX-ROOT-CAPTURE-150` | 通过 | 修复前同场景稳定复现不存在的 `CubexConstruct.ChargeUpStrengthGain`；移除多余捕获后根快照成功物化。runId `0c39d6aa84904c5b994bf8f985bfd316`，基线 runId `f96241297c7a443a8a1fe50d0a7b5414` | 2026-08-28 |
| `CUBEX-SEARCH-INITIALIZATION-150` | 通过 | 方柱构装体正常首轮搜索覆盖 4 回合，返回 3 个可执行动作，未镜像效果为 0。runId `2f37d97d045743aea8d68ebf99db0e57` | 2026-08-28 |
| `MONSTER-MOVES-BATCH-020-CUBEX-150` | 通过 | 既有 13 项实机/模拟差分全部通过；方柱构装体排出、蓄能和两次重复轰击分别验证多段伤害及力量 `2 → 4 → 6` 累计。runId `1d382781dfc2402581af0383e093b5ea` | 2026-08-28 |
| `TOASTY-MITTENS-BUNDLE-FINAL-0150` | 通过 | 从异螨问题包的战前跑局状态重建首回合烘焙手套；原生手牌页按 `Visible → SearchStarted → Selected` 由 Mod 自动接管，搜索返回 1 个 `TOASTY_MITTENS` 选择并严格进入 Play 状态。runId `097e957b46b941e1b4eb0165862d5493` | 2026-08-28 |
| `KNOWLEDGE-DEMON-NATIVE-CHOICE-0150` | 通过 | 知识恶魔首轮路线计划 `MIND_ROT`；提交结束回合后原生 `ChooseCard` 页面 `visible=1 / selected=1 / search=0`，玩家获得 `MIND_ROT_POWER`，计划外重算 0，增量/完整回放一致。runId `5b5d61d595c249c0a4861151460cc490` | 2026-08-28 |
| `KNOWLEDGE-DEMON-NATIVE-CHOICE-REUSE-0150` | 通过 | 同一路线完成敌方回合二选一后，第 2 回合直接复用；知识恶魔选择没有被错误留给下一回合准备器，计划外重算 0。runId `27a42f3669fb479fafde8e10e3d499f3` | 2026-08-28 |
| `TOASTY-KNOWLEDGE-CROSS-PHASE-0150` | 通过 | 知识恶魔战同时持有烘焙手套；首回合手套保持 `Visible → SearchStarted → Selected`，结束回合后自动完成 `MIND_ROT` 二选一，第 2 回合只重放准备选择并精确复用，计划外重算 0。runId `39de2fb177ce43db95c1c2209c390330` | 2026-08-28 |
| `BURNING-PACT-AUTO-COMPLETE-0150` | 通过 | 固定手牌为燃烧契约+、升格者之灾、防御，抽牌堆为打击；Normal 部署先显示原生手牌页并选择升格者之灾，再打出抽到的打击结束战斗。请求记录 `manual_confirmation=False`，页面 `visible=1 / selected=1 / search=0`，增量/完整回放一致。runId `660b6ba4b2a044938d3960208639b5ef` | 2026-08-28 |
| `ARMAMENTS-AUTO-COMPLETE-ADJACENT-0150` | 通过 | 未升级武装从打击、防御中选择升级目标，原生手牌升级页完成后继续打出升级打击；页面 `visible=1 / selected=1 / search=0`。runId `793fd329c05340d98a775b173dd3b8c9` | 2026-08-28 |
| `CHOMPERS-BURNING-PACT-BUNDLE-FIXED-0150` | 本问题路径通过，整战断言失败 | 从问题包战前跑局状态重建同族小队，燃烧契约原生手牌页完成且未出现确认按钮异常，战斗第 5 回合结束；第 4 回合另有防御升级状态不一致并触发 1 次计划外重算，故不记为整场通过。runId `99127886a8c54dfe8941239186b5ddea` | 2026-08-28 |
| `TOADPOLES-WEAK-20260828-BUNDLE` | 根因确认，待 macOS 实机复测 | `0.14.11`、macOS ARM64 的两次搜索均在 `GC.TryStartNoGCRegion(6 GB, 1 GB)` 抛出 `ArgumentOutOfRangeException(totalSize)`；根快照已成功，尚未进入 Beam。当前代码只把该精确异常分类为 CLR 区域上限，其余异常保持失败 | 2026-08-28 |
| `GC-NOGC-REGION-LIMIT-0150` | 通过（正常 No-GC 路径） | Windows headless 设置 `16 GB` No-GC 预算；本机 CLR 成功进入 No-GC，首轮 Short 搜索在 `168.7 ms / 2.20 MB` 内产出 1 个可执行动作，GC 暂停 `0 ms`，场景 Passed。runId `04450f09159d48d9bfaca0ba9ba049e0`；该结果不覆盖 macOS 的区域拒绝分支 | 2026-08-28 |
| `KAISER-CRAB-SEARCH-REPLAY-FINAL-0150` | 通过 | 从帝王蟹问题包战前存档、原 seed 与两只怪物的 `209/199` HP 重建；修复前在第 3 回合 EndTurn 回放稳定复现缺失 `Rocket.ChargeUpStrengthGain`，修复后搜索覆盖 7 回合、未镜像效果为 0。runId `7236617d45b54d17b98bb2a8a68fcf21`，基线 runId `42db83c00fbb43548135efadaed5604d` | 2026-08-28 |
| `KAISER-CRAB-INCREMENTAL-SHORT-FINAL-0150` | 通过 | 同一问题包状态以 Low/Short 完成增量分叉与完整前缀回放对照，搜索覆盖 9 回合、未镜像效果为 0。runId `2067fe850f4d4cd0b011c7e1ce05e40a`；High 完整验证因仪器开销在 120 秒超时，runId `343c4c6b3ba54ef58050f6d1a898ac05` | 2026-08-28 |
| `MONSTER-MOVES-BATCH-021-KAISER-0150` | 通过 | 帝王蟹 10 个行动的实机/模拟严格差分全部通过；火箭蓄能获得 `2` 点力量，激光与重新充能保留累计状态。runId `7bcbece1c9e04a36a72b8ddddb2db361` | 2026-08-28 |
| `CALCULATED-VAR-ROOT-CAPTURE-FINAL-0150` | 通过 | 耗尽堆 `EXPECT_A_FIGHT` 固定 `CalculatedBlock=16 / CalculationBase=15`；修复前根投影稳定复现 `16 → 15` 失败，计算缓存改为派生字段后根快照通过。runId `e243b913a1a44aa8ba67e692da88d1b0`，基线 runId `0ea043478335482c84408617ab91e38a` | 2026-08-28 |
| `EXPECT-A-FIGHT-CALCULATED-BLOCK-FINAL-0150` | 通过 | 玩家持有 5 点力量时打出 `EXPECT_A_FIGHT`，实机与模拟完整状态严格一致，证明移除派生缓存没有丢失公式输入或实际格挡。runId `bccf3de8a91f4aad94af02b589a77d1a` | 2026-08-28 |
| `CARD-DOWNGRADE-STATE-AUDIT-382-0150` | 通过 | 魔法骑士抑制对手牌、抽牌堆和弃牌堆的 8 类升级牌执行降级，并在施法者死亡后恢复；实机与模拟逐实例状态一致。runId `95795debe881414e8d8179921061e20e` | 2026-08-28 |
| `KNIGHTS-ELITE-SEARCH-FINAL-0150` | 通过 | 从三骑士问题包战前存档、原 seed、进阶与 `108/97/89` HP 重建，首轮搜索正常返回可部署路线；复杂嵌套随机选牌的失效候选没有再中止搜索。runId `411ab4cdfe514a7cab2bac384354beb5` | 2026-08-28 |
| `KNIGHTS-ELITE-BUNDLE-FULL-AUTO-FINAL-0150` | 通过 | 同一战前存档以 Instant/0 秒完整自动部署，第 1 回合结束战斗，计划外重算 0。当前源码首抽路线与 `0.14.11` 原包不同，不记作原包逐动作回放。runId `bc508c1d2a75438599fc4cb26656acf4` | 2026-08-28 |
| `KNIGHTS-ELITE-INCREMENTAL-FINAL-0150` | 通过 | 同一问题包重建状态以 Low/Short 完成增量分叉与完整前缀回放一致性，首轮返回 11 个动作并结束战斗。runId `b0740c38be024c61a73a7c7aa281164a` | 2026-08-28 |
| `STATE-FIELDS-DERIVED-CALCULATED-0150` | 通过 | CoverageCatalog 将 43 个原版 `CalculatedVar` 字段登记为 `Derived`，未分类状态字段为 0；真实基础变量、私有状态和字符串显示字段分类保持不变 | 2026-08-28 |

## 0.14.13 Loadout 战斗费用兼容

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `LOADOUT-EVERY-CARD-FREE-ROOT-1413` | 目标路径通过，完整断言受限 | 投影实际 Loadout `0.4.10` 与 BaseLib `3.4.5` 后进入小啃兽战斗。第一轮成功创建并 Fork 根快照，未再出现 `LoadoutEveryCardFreeCombatHook` 的 `SEARCH_SETUP_FAILURE`；随后旧测试把 ModHelper 运行级 subscriber 误算进原版前缀，runId `7c5c868146194a05a7d038d93c31feb3` 在外围计数断言失败。修正断言后的第二轮在 Loadout 的 headless 战斗房间资源预载处超时，未进入战斗断言，不记为完整通过 | 2026-08-28 |

## 0.14.12 同族小队压缩连锁

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `kin-boss-route-clean` | 通过 | 固定 `oldE0VXH9PVN8`、进阶 10、第一幕同族小队、三敌 `63/62/199` HP、铁甲战士 `56/85` HP 与存档牌序；Smart、Instant 完整自动执行。第 4 回合燃烧契约、愤怒、余烬后使用灰水耗尽六张牌，第 5 回合以愤怒、放血、燃烧契约及连续攻击击杀三敌；最终 3 HP，战损 53，第 5 回合获胜，计划外重算 0。runId `41108853af1640fa8ee3379793469fc9` | 2026-08-28 |

## 0.14.10 敌方攻击压制保路

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `INSATIABLE-PARETO-CONTROL-150` | 通过 | 固定无厌沙虫“液化地面”、日志首手与有序牌堆；不再把某张牌的固定顺序当作质量代理，而是同时门禁首回合 `0` 掉血、整场预计掉血不超过 `7`、不卖血、不用药、至少搜索/存活至第 `7` 回合且终局敌方总生命不高于 `204`。隔离的上游 `v0.17.2` 与当前分支均复现旧 `MALAISE` 首动作断言失真，因此未修改生产搜索排序。runId `207ad2c8d02f489f9ba1aa41287d6966` | 2026-08-30 |
| `MALAISE-CONTROL-NIBBITS-REGRESSION-150` | 战损通过，回合断言失败 | 双小啃兽仍为 `0` 战损、`0` 计划外重算、两次洗牌；实际第 6 回合结束，请求沿用了第 5 回合精确断言，因此结果状态为 Failed，不计作整场通过。runId `a6ef1d75a48d46f2be7b98ce7ef4def5` | 2026-08-27 |

## 0.14.9 Tender 出牌完成结算

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `HUNTER-KILLER-TENDER-CARD-SEQUENCE-149` | 通过 | 猎人杀手场景注入 Tender，依次打出后空翻、中和+、打击；敌方实际只损失 `7` HP，力量/敏捷各降 `3`，逐字段 actual/simulated 一致。runId `79929fef88b3495cbe60e4d529594a31` | 2026-08-27 |
| `TENDER-INCREMENTAL-CARD-COMPLETION-149` | 通过 | 两张打击覆盖 Tender 的逐次出牌完成结算，增量分叉与完整前缀回放一致，首回合结束且计划外重算 `0`。runId `bb70d9239f78495e988682b40bda9bec` | 2026-08-27 |
| `TENDER-FULL-AUTO-REUSE-149` | 通过 | 猎人杀手完整自动部署后进入第 2 回合，continuation 精确复用，计划外重算 `0`。runId `c242e1f6287c484cbae5925b36a995f5` | 2026-08-27 |
| `MONSTER-MOVES-BATCH-033-TENDER-149` | 通过 | 旧 Tender 双打击与玩家回合末力量/敏捷恢复严格差分继续通过。runId `5da676be79aa45e7b4f6cff40b353fa4` | 2026-08-27 |
| 问题包战前存档重建 | 未进入战斗 | 现有无人入口在原版 `NOverlayStack` 初始化阶段空引用；不计作问题包回放通过 | 2026-08-27 |

## 0.14.8 回合首张牌出牌间隔

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| 回合抽牌完成到首张牌 | 未执行 | 按用户要求不运行测试；实现让全自动在原版抽牌及回合准备动作完成后，等待“牌间额外停顿”再恢复路线或部署首张牌 | 2026-08-26 |

`0.10.0` headless 接通阶段保留三条未计为通过的开发证据：首次隔离启动因未确认 Mod 警告而跳过全部 Mod；允许 Mod 后因关闭 Steam 而找不到创意工坊 RitsuLib；首次长线因无窗口“战斗基础”教学节点空引用而停住。启动器现分别通过隔离设置、临时 RitsuLib 投影和仅无人请求活动时跳过纯 UI 教学解决。熵的两个前置夹具也未冒充通过：低血双敌在第 `2` 回合先发生减员，导致第 `3` 回合按死亡敌人状态差异保守重搜；单敌夹具则被怪物自身 `2` 项未镜像效果的严格断言提前拒绝。最终通过项使用满血小啃兽，隔离了熵与 RNG 本身。

## 0.14.7 内存检查点续搜

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `GC-CHECKPOINT-RESUME-0147` | 通过 | 1 GB No-GC 压力下触发 5 次 Beam 检查点；每次从原回合层/出牌深度续搜，不从根重算。后台全代非压缩回收暂停 `3.1-4.0 ms`，托管存活量降至 `100-205 MB`；完整 6 回合获胜、零非预期重算、无 `>50 ms` 帧和 No-GC 耗尽 | 2026-08-26 |

## 0.14.6 动作选牌与部署高亮时序

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `AEONGLASS-WITHER-CHOICE-TIMING` | 通过 | 凋零气场 `CardsLeft=1` 时打出杂技+；模拟与原生选牌候选都断言不含尚未由 `AfterCardPlayed` 生成的凋零，选择真实防御后完整状态一致。runId `4e3576a6ffc546c6979c768ea6f46f60` | 2026-08-26 |
| `MANUAL-CHOICE-TRANSACTION-ADJACENT` | 通过 | 生存者、杂技+、早有准备+、燃烧契约依次覆盖弃牌、抽后弃、抽二弃二和耗尽后抽牌；四项 actual/simulated 有序牌堆、Power、逐牌状态与 RNG 严格一致。runId `bcdaab927b3d405c8b8d20e3d4de4c93` | 2026-08-26 |
| `AEONGLASS-WITHER-CHOICE-FULL-AUTO-FINAL` | 通过 | 搜索在凋零气场 `CardsLeft=1` 下规划杂技+弃防御，再打出抽到的打击击杀永世沙漏；原生页面只请求并完成一次弃牌，增量/完整回放一致，计划外重算 0。runId `3ad4ff73aefd4136b51d7596741a7795` | 2026-08-26 |
| `TOOLS-UI-ACTION-ALIGNMENT` | 通过 | 第 2 回合必备工具页面完成后精确复用；回合准备胶囊不占部署索引，真实第一张牌为 `active_action_index=0`，牌完成后 500 ms 间隔内活动索引为空，原生页面 `search=0`，计划外重算 0。runId `268f83840ffc40fdb182edf1c03ff2f3` | 2026-08-26 |
| `PAELS-EYE-TOOLS-UI-ALIGNMENT-FINAL2` | 通过 | 首回合 0 张出牌直接结束并触发佩尔之眼，直接结束胶囊经历 active/complete；额外回合出现必备工具页面后复用第 2 回合，第一张牌仍从动作索引 0 开始，原生页面不搜索且计划外重算 0。runId `5e0966d1ce114496b2c4292a60d3871b` | 2026-08-26 |

## 0.14.5 佩尔之眼与路线重放胶囊

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `PAELS-EYE-LIVE-END-TURN` | 通过 | 静默猎手只持有佩尔之眼，首回合 0 张出牌并直接结束；开启全自动“重算后战损增加暂停”以强制经过实机结束回合风险复核。路线与 Overlay 均标注 `PAELS_EYE:额外回合`，实际未触发 `live_end_turn_risk` 暂停，直接进入额外玩家回合并 `Reuse:Turn=2`，`UnexpectedReplans=0`。runId `ee7607c122bf4623a785daa13a3dc993` | 2026-08-26 |
| `OVERLAY-REPLAY-BADGE` | 通过 | 手牌只有螺旋附魔打击；搜索计划记录该实例附魔后重放次数为 1，Overlay 动作快照在牌名后显示 `重放×1`，随后实际打出该牌。runId `77cc4df7265640d9b78f93768a333f15` | 2026-08-26 |

## 0.14.4 单步选牌页接管与间隔

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `SINGLE-STEP-TOOLS-TAKEOVER-EXECUTE` | 通过 | 单步先停在第 2 回合必备工具原生手牌页，求解器尚未选择；随后请求“执行本回合”，按既有计划完成选择，直接复用第 2 回合且计划外重算 0。设置 500 ms 牌间停顿，选择完成到下一张牌实测 610 ms。runId `48ec35fe5d4e45e38ed6fbed3fc012e4` | 2026-08-26 |
| `SINGLE-STEP-TOOLS-TAKEOVER-FULL-AUTO` | 通过 | 同一停住边界在原生页面开启全自动；既有计划完成选择后复用第 2 回合，计划外重算 0，500 ms 设置下实测间隔 609 ms。runId `f82172f5aa264f5d9d73b6c59e79a2a9` | 2026-08-26 |
| `FULL-AUTO-TOOLS-CROSS-TURN-0144-FINAL` | 通过 | 开启增量/完整回放核对并以 `Instant / 0 秒` 完整自动部署；第 2、3 回合必备工具页面均为 `visible=2 / selected=2 / search=0`，两回合都复用首轮路线，计划外重算 0，第 3 回合结束。runId `cfb96d6f67b74b7797dec580983b9bbb` | 2026-08-26 |

## 0.14.3 部署动作完成边界

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `MONSTER-WATERFALL-SLY-AFTER-DEATH-ORDER` | 通过 | 双怪局中回响斩击先把 1 HP、带蒸汽爆发的瀑布巨兽转入蓄爆，再同回合由原生杂技页面弃升级战术大师。动作完成态为能量 2，战术大师与杂技均在弃牌堆；第 2 回合直接 `Reuse:Turn=2`，`UnexpectedReplans=0`。runId `cabb121a5a1544a196dd1bda013884b2` | 2026-08-26 |
| `MONSTER-WATERFALL-DEPLOYMENT-SETTLEMENT` | 通过 | 按玩家日志重建静默猎手 22 张有序牌堆与瀑布巨兽长线，`Instant / 0 秒` 全自动于第 10 回合结束，完整经过 `ABOUT_TO_BLOW_MOVE` 与 `EXPLODE_MOVE`；1 次搜索、9 次续用、计划外重算 0。runId `24ecb35d75b4417aa6cd7a44652dcc65` | 2026-08-26 |
| `DEPLOY-EXACT-POTION-ACTION` | 通过 | 强制至少使用一瓶药水，实际入队并使用弱化药后打出攻击，于首回合结束；验证药水部署也能捕获并等待本次 `UsePotionAction`。runId `dfed9e7bbf884d7f8fdf07960831bef4` | 2026-08-26 |

## 0.14.2 单步边界与同名重放卡牌

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `SINGLE-STEP-TOOLS-SPIRAL` | 通过 | PUNCH Construct，抽牌堆仅有普通防御和螺旋附魔防御，玩家已有必备工具。初始路线的 `EndTurn.TurnStartChoices` 精确指向普通防御；执行本回合后停在第 2 回合原生手牌页，全自动关闭且 `turn_setup:2` 没有 Selected 记录。runId `07d96613e313442a99275ee969e1c02b` | 2026-08-26 |
| `FULL-AUTO-TOOLS-SPIRAL` | 通过 | 同一逐实例牌组开启全自动。第 2 回合原生手牌页 `visible=1 / selected=1 / search=0`，选择普通防御后直接 `Reuse:Turn=2`，`UnexpectedReplans=0`；本次日志没有“原生选牌会话没有位于活动栈顶”。runId `80552c268daf450cbce05aeb9314b844` | 2026-08-26 |
| `PUNCH-CONSTRUCT-20260826-BUNDLE` | 受限 | 问题包确认旧版在后续回合重复报告 `turn_setup:N` 会话栈异常，并记录第 3 回合普通防御只提供 3 点格挡。包内检查点位于必备工具选择之后，无法精确恢复选择前手牌；不记为整战复现通过，逐实例选择由上述两个定向夹具覆盖 | 2026-08-26 |

## 0.14.1 原生选牌定版

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `NATIVE-CHOICE-REPLAY-NO-SEARCH-556/557` | 通过 | 首回合工具盒先显示原生页面，再搜索三个候选并按 `Visible → SearchStarted → Selected` 完成；第 2 回合必备工具读取上一轮 `EndTurn.TurnStartChoices`，原生手牌页 `visible=1 / selected=1 / search=0`，随后直接 `SEARCH_REUSED turn=2`。Steam 可见机甲整战共显示并完成 6 次手牌选择，页面期间 `search=0`，第 3 回合复用恢复通过，第 7 回合结束且计划外重算 0 | 2026-08-26 |
| `NATIVE-CHOICE-SURFACES-553/560` | 通过 | 当前工作树把求解器接管的选牌改为原版可见页面：工具盒使用 ChooseCard；选择悖论使用简易网格；烘焙手套、赌博筹码、助能生存者、出牌弃牌使用手牌页面；全息影像使用战斗牌堆页面；武装使用手牌升级页面。首回合页面后搜索，后续回合只重放既有路线；动作内选择在对应事务中播放，各场景保持精确 Play 状态或零计划外重算 | 2026-08-26 |
| `NATIVE-CHOICE-STRICT-DIFF-554` | 通过 | 无 UI 严格差分仍使用测试专用选择器，生存者、杂技、早有准备等推断选牌 12/12 完整状态一致；生产 `Runtime/` 除原生观察驱动外禁止调用 `CardSelectCmd.PushSelector`，覆盖扫描 85 个调用点、0 未解析 | 2026-08-26 |

## 0.14.0 重构验收

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `HIDDEN-GEM-REPLAY-552` | 通过 | 从玩家 `0.13.35` 问题包恢复猫头鹰法官首轮的 7 张手牌、30 张有序抽牌、跑局快照与 RNG。High 固定根主动打出未掘宝石，使灵体获得 2 次额外重放，并从原“仅死亡路线”改为第 8 回合胜利；第 2-8 回合精确复用、0 药、零计划外重算。独立一步差分通过；Low 增量/完整前缀核对同样获胜（第 10 回合）；双小啃兽增量长线保持第 5 回合、两次洗牌、0 药、0 战损 | 2026-08-26 |
| `REFACTOR-FINAL-NIBBITS-551` | 通过 | 从最终提交构建的 Release DLL 开启根快照与增量/完整回放核对；双小啃兽第 5 回合结束、两次洗牌、0 药、0 战损，第 2-5 回合精确复用且零非预期重算。首轮 `6.21 s / 2.47 GB / 0 ms GC / 17.2 ms 最大帧` | 2026-08-26 |
| `REFACTOR-FINAL-MECHA-HIGH-550` | 通过 | 从最终提交以原固定快照和 High 预设复跑：第 5 回合结束，第 2-5 回合精确复用；`expanded=4624`、`transitions=33432`、`choice_branches=17735`、`dominance/transposition/repeatable=214/700/0`，`11.45 s / 3.55 GB / 0 ms GC / 17.6 ms 最大帧`。此前把增量全回放诊断与性能门槛组合的请求因 `100.4 s / 34.4 GB` 正确失败；中档请求因第 7 回合结束正确失败，二者均未计为通过证据 | 2026-08-26 |
| `REFACTOR-FINAL-NIBBITS-549` | 通过 | 根怪物从活动 roster 移除后仍保留本分支 AI/静态参数，允许正在执行的怪物行动完成尾部结算；原第 4 回合稳定崩溃夹具现于第 5 回合结束、两次洗牌、0 药、0 战损、逐回合复用且零非预期重算 | 2026-08-26 |
| `MIRROR-REGISTRY-DESCRIPTOR-548` | 通过 | action/result registry 统一提供支持 descriptor，CoverageCatalog 删除对三个私有字段及 MethodSpec 布局的反射；切换前后 3035 项及全部门禁/生成文件一致，钢笔尖 Hook 增量路线与真实部署通过 | 2026-08-26 |
| `SOLVER-OVERLAY-SNAPSHOT-547` | 通过 | 控制器一次性捕获 Overlay/Turn/Action 只读快照，三个 Renderer 不再读取搜索/预测可变类型；钢笔尖两动作路线真实渲染并部署，遗物后缀、击杀路线、ready/deploying/complete 状态和速度恢复均通过。人工布局与字体仍按 UI 人工项执行 | 2026-08-26 |
| `UNATTENDED-EXECUTOR-546` | 通过 | 差分分派、设置覆盖、搜索/部署等待、提前停止与完整自动战斗进入 `Executor`；双球两项严格差分、强制一瓶药首回合击杀、速度恢复和 Held 结果均通过，普通请求复用同一进程 | 2026-08-26 |
| `UNATTENDED-ASSERTIONS-545` | 通过 | 执行前预测/Fork/根快照/会话/CardModifier 检查及执行后回合、生命、出牌、用药、Power 断言进入 `Assertions`；根快照检查、实际打出指定卡和首回合结束在同一场景通过 | 2026-08-26 |
| `UNATTENDED-SCENARIO-BUILDER-544` | 通过 | 建局、进入遭遇、怪物/生命/牌堆/球/药水/遗物/Power/RNG 注入进入 `ScenarioBuilder`；Defect 双球两项严格差分通过。故意注入错误敌人数时仍记录 `inject_state` 与真实第 1 回合，随后同进程恢复成功 | 2026-08-26 |
| `UNATTENDED-WRITER-543` | 通过 | Passed/Held/Failed 的公共协议字段、内存采集和临时文件原子替换进入 `Writer`；同一进程依次写出成功、故意断言失败和失败后恢复成功三份结果，状态、阶段、错误与进程复用均正确 | 2026-08-26 |
| `UNATTENDED-PROTOCOL-HOST-542` | 通过 | 请求文件接收、协议版本、每请求测试开关、状态漂移和清理进入 `ProtocolHost`；同一 headless PID 连续完成两场首回合击杀，第二场明确 `UNATTENDED_REUSED`，最后按请求退出 | 2026-08-26 |
| `FINAL-ORDERING-POLICIES-541` | 通过 | 同一击杀夹具依次验证 Disabled/Smart 均保留 0 药路线，RequireAtLeastOne 选择并实机使用 1 瓶弱化药；固定防御牌组保持主动卖血 `5/5` 上限并剪除超预算路线 | 2026-08-26 |
| `FINAL-PLAN-ORDERING-540` | 通过 | `Solve` 的终局胜负、药水、卖血和边界排序迁入 `FinalPlanOrdering`，候选通过 `SearchFeatures` 读取固定特征。机甲保持第 5 回合、同动作序列、`4624/33432/17735` 与全部剪枝计数，`11.51 s / 3.55 GB / 0 ms GC / 18.8 ms`，零重算 | 2026-08-26 |
| `FINAL-ORDERING-DUAL-539` | 通过 | 切换前由旧排序和 `FinalPlanOrdering` 对同一候选集合逐字段比较选中节点、得分、药水与卖血统计；钢笔尖增量路线一致并首回合无损击杀 | 2026-08-26 |
| `BEAM-RETENTION-POLICY-538` | 通过 | 状态去重、Beam 排名、多样性通道、药水配额和 Pareto 保留进入具体策略；只通过 stand-pat 委托访问模拟。机甲保持第 5 回合、同动作序列、`4624/33432/17735` 与全部剪枝计数，`11.64 s / 3.55 GB / 0 ms GC / 17.1 ms`，零重算 | 2026-08-26 |
| `SEARCH-RUN-CONTEXT-537` | 通过 | 15 个搜索计数器、性能/节流、转置及四类缓存收口到单次 `SearchRunContext`，不池化或改算法。固定机甲保持第 5 回合、同动作序列、`4624/33432/17735` 与全部剪枝计数，`11.52 s / 3.55 GB / 0 ms GC / 17.4 ms`，零重算 | 2026-08-26 |
| `BEAM-PARTIAL-SPLIT-536` | 通过 | `CombatBeamSolver` 纯移动为七个阶段 partial；结构门禁固定文件和代表方法归属。机甲完整 headless 保持第 5 回合、同动作序列、4624 展开、33432 转移、17735 选牌分支与全部剪枝计数，`11.35 s / 3.55 GB / 0 ms GC / 17.2 ms`，零重算；Defect 球/Synchronize 严格差分通过 | 2026-08-26 |
| `MOD-SUBSCRIBER-BOUNDARY-534` | 通过 | BaseLib/Loadout subscriber 分段捕获；实际 CardModifier 夹具验证克隆、Owner 重绑和写时复制，Ritsu capability 反向夹具验证非空集合仍走原属性贡献。空 capability 快通道及 Fork listener 缓存继承把机甲分配从约 `4.98 GB` 降至 `3.57 GB`；连续两次完整 Mod 可见整战均为第 5 回合胜利、`0 ms GC`，最大帧 `8.6/16.5 ms`。带实际 Modifier 的最终轮仍为 `11.87 s / 3.57 GB / 0 ms / 13.5 ms` | 2026-08-26 |

## 已通过的无人场景

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `CARD-POWER-NESTED-534` | 通过 | 卡牌 Power 结算改为可嵌套源栈；Unsettling Lamp 按触发卡关联。Knife Trap/Eidolon 自动出牌和升级 Knife Trap 双 Shiv 的模拟/原生差分通过 | 2026-08-26 |
| `BUILTIN-LISTENER-IDENTITY-533` | 通过 | Badge listener 以根克隆进入预测；多人缩放 listener 清除 live RunState/CombatState，并由单人 mirror 返回精确倍率。根隔离和双小啃兽增量整战通过；Loadout/BaseLib 第三方订阅另列适配项 | 2026-08-26 |
| `MONSTER-MODIFIER-IDENTITY-532` | 通过 | Modifier 以根克隆进入 Hook；永世沙漏生成凋零读取分支升级计数，Murderous 对预测召唤敌人施加 3 力量，召唤遗物读取根清单。首次差分暴露直接构造模拟器未物化根，统一构造边界后定向差分、根隔离和双小啃兽增量整战通过 | 2026-08-26 |
| `RUN-SNAPSHOT-HOOK-PREFIX-531` | 通过 | Run 标量、RNG、起始回合和 Hook 前缀进入主线程根；牌组 Card/Enchantment listener 使用克隆，卡池筛选显式消费捕获约束。根隔离、攻击药生成差分和双小啃兽增量整战通过 | 2026-08-26 |
| `ROOT-MODEL-INVENTORY-530` | 通过 | 玩家回合/金币、Relic/Potion、卡牌注册、Osty、初始 Power、Run RNG 和怪物私有字段进入主线程根；listener 使用克隆。首次严格复跑抓到并根修复 Relic `AfterCloned` 重置私有计数；最终遗物 Hook 11/11、钢笔尖、Knowledge Demon、Smart 救命药和双小啃兽增量整战通过 | 2026-08-26 |
| `COMBAT-ROOT-SNAPSHOT-529` | 通过 | 搜索根只能在主线程捕获；live 与根投影 continuation 逐项一致。捕获后修改实机能量，后台 Fork 仍保持捕获值。Beam 根、当前历史和 Hook listeners 不再从 worker 惰性构造；钢笔尖增量与双小啃兽普通/增量整战保持通过 | 2026-08-26 |
| `FORK-BOUNDARIES-528` | 通过 | Fork 在克隆前统一拒绝未完成 trace、选择、出牌、Hook 私有事务、延迟历史和遗物记录；钢笔尖与蜷身的瞬时引用不进入稳定节点。臂铠触发卡由 `0.15.0` 依据原版生命周期纠正为可 Fork 的持续状态。配对中途死亡、钢笔尖增量、两组 Hook 差分及双小啃兽增量整战全部通过并零重算 | 2026-08-28 |
| `CONTROLLER-SESSIONS-527` | 通过 | 战斗、搜索和部署状态进入独立会话；取消搜索后旧 callback 不得写回。战斗结束异步 GC 回收与新搜索按完成信号串行。策略快照/取消/重搜/完整部署通过；双小啃兽普通与增量均第 5 回合、两次洗牌、0 药、0 战损并零重算 | 2026-08-26 |
| `REFACTOR-BOUNDARIES-526` | 通过 | 不支持的动态数值、推断 OnPlay 异常与搜索转移异常均 fail-fast 且保留搜索上下文；搜索只消费主线程捕获的策略快照。双小啃兽普通/增量均第 5 回合、两次洗牌、0 药、0 战损并成功复用；药水 17/17、推断选牌 12/12、推断卡 43/43、CalculatedVar 25/25 通过；Smart 与至少一瓶策略均零重算 | 2026-08-26 |
| `DECIMILLIPEDE-LATE-DEATH-REATTACH-524` | 通过 | 肢节先执行正常行动、再于同一敌方回合死亡时，`DEAD_MOVE` 按原状态机直接过渡到 `REATTACH_MOVE`，死亡保留 Power、行动历史、私有死亡阶段和九条 RNG 严格一致；结束回合产生的复活窗口进入通用 Beam 保留。亡灵契约师问题包第 6 回合两药胜利，第 2-6 回合精确复用、零计划外重算；上一份千足虫和双小啃兽普通/增量均保持通过 | 2026-08-25 |
| `DECIMILLIPEDE-DEAD-TO-REATTACH-521` | 通过 | 反馈包中复活肢节实际执行 `DEAD_MOVE` 后，模拟与原版一同推进重接后继，不再把 0 HP 的 `Reviving` 肢节永久冻结在死亡动作。修复前重建整战出现 1 次计划外重算；最终仓库夹具 Smart、Instant/0 秒第 4 回合结束，第 2-4 回合精确复用、零计划外重算，首轮三药计划完整执行且不再反复变化 | 2026-08-25 |
| `MYTES-SMART-INDEPENDENT-POTION-AUDIT-519` | 通过 | 同一异螨开局的统一 Smart Beam 错把无药战损估为 31，选择三瓶药掉 1；独立 Disabled 搜索实际为 0 药掉 11。Smart 选中药水后固定运行独立禁药反事实，纠正为三药只省 10、低于 27 门槛；最终 0 药、第 8 回合、预计/实测均掉 11，第 2-8 回合精确复用、零计划外重算。无药必死救药与既有低损无药回归保持通过 | 2026-08-25 |
| `TWO-TAILED-RAT-RAND-WEIGHT-507` | 通过 | 原版尖啸参数 `3` 按三回合冷却而非三倍权重处理；固定问题包种子的一步差分中，疾病啃咬后的原版与模拟均选择抓挠。用户存档 Medium、Smart、Instant/0 完整自动战斗第 6 回合结束，预计/实测均掉 9，第 2-6 回合精确复用、零计划外重算；500 ms 短搜增量等价与第 2 回合续用通过 | 2026-08-25 |
| `WATERFALL-HORIZON-LIFECYCLE-506` | 通过 | 两个 0.13.27 用户问题包复现节点上限未完成路线在第 12 回合空过，以及蒸汽喷发致死后阵容、无限生命、AI 与 Power 生命周期偏差；0.13.29 以原 seed/250 HP、中档、Smart、Instant/0 秒完整自动执行，分别第 13/16 回合结束且零计划外重算；定向两回合与增量等价回归通过 | 2026-08-25 |
| `RAVENOUS-IMMEDIATE-STUN-505` | 通过 | 玩家回合击杀尸蛞蝓同伴后，幸存者立即以带原行动后继的 `STUNNED` 替换当前意图；敌方 Doom 触发与盛碗虫眩晕循环保持一致。尸蛞蝓完整战斗第 7 回合结束、零计划外重算；增量分叉与完整前缀回放一致 | 2026-08-25 |
| `CHOMPERS-PAIR-TRANSACTION-504` | 通过 | 0.13.25 啃咬机问题包首回合搜索因 pending CardPlay 配对状态在 Fork 处失败；0.13.27 恢复原开战前存档、种子、A10、第二幕、64/67 HP 和首轮行动，以 Medium、Smart、Instant/0 完成普通与增量整战，均第 11 回合结束、零非预期重算、无 cannot fork | 2026-08-25 |
| `SLUMBERING-PAIR-OBLIVION-498-503` | 通过 | 0.13.25 睡眠甲虫问题包的 CardPlay 配对状态在监听 Power 被移除后仍于动作完成边界核销；连续两次湮灭严格使用出牌前 3 层快照而非叠加后的 6 层。0.13.27 最小差分预测/实机灾厄均为 6；原存档 Medium、Smart、Instant/0 完整战斗与增量等价均第 7 回合结束、零非预期重算、无 cannot fork | 2026-08-25 |
| `USER-BUNDLES-PAIRED-THEFT-DEBILITATE-492-497` | 通过 | 外骨骼结算中移除监听器后卡牌配对事务在动作完成边界清空，开启增量等价的原存档整战第 5 回合结束；偷窃草蜢保留已修改牌的 DeckVersion，三候选偷牌 RNG、振翅归零眩晕和后继行动与原版一致，原存档第 5 回合结束；仪式兽虚弱/易伤读取分支 Debilitate，原存档第 9 回合结束；三场完整自动战斗均为 Medium、Smart、Instant/0 且零非预期重算 | 2026-08-25 |
| `QUEEN-ROUTING-OPT-491` | 通过 | 幕末 Boss 深搜为选牌历史保留 50% 策略位，普通深搜 40%，并联合保留威胁集火、潜在能力、下回合资源和关键攻击；女王中档 1 瓶敏捷药、第 11 回合、预计/实测 0 战损、零重算；双小啃兽维持 0/0；发布 ZIP 干净安装后 Steam 可见机甲 12.23s/3.82GB、战损 30、GC 0、最大帧 9.4ms | 2026-08-25 |
| `RAVENOUS-QUEEN-LONGLINE-488-490` | 通过 | 蛞蝓玩家侧击杀与敌方回合末 Doom 都应立即建立 `STUNNED`；`0.13.28` 已纠正旧证据中的玩家侧延迟时序。Buffer/生成牌/球/虚无/Echo Form 状态修复；女王日志重建夹具由 0.13.24 的预计掉 25 作为本轮优化基线 | 2026-08-25 |
| `USER-REPORT-PAELS-ROUTES-484 / CONTROL-MODE-485-486` | 通过 | 熟睡甲虫、虫术师、炮台操作员精确还原佩尔之眼额外回合并全程零重算；凯撒蟹与永世沙漏找到生还路线；计划外重算告警固定在标题右侧，手操后再由求解器接管仍会告警；问题包区分 `solver_only` 与 `manual_plus_solver` 并记录最近完整自动执行回合 | 2026-08-25 |
| `THEFT-ILLUSION-CHOMPERS-480-482` | 通过 | 偷钱地精/偷窃草蜢仅在对应遭遇显示“保牌/保钱、放走”，分支内追踪被盗资源并按模式决定卖血/用药；幻象被灾厄回合末击杀后保留复活意图，佩尔之眼额外回合有遗物标注；啃咬机精确初手与 17 张抽牌堆第 6 回合获胜、预计/实际掉 21、零重算；甲虫汁先消耗人工制品且不施加缩小 | 2026-08-24 |
| `BUG-REPORT-FORENSICS-478` | 通过 | 活动战斗和战后错误时机导出均逐检查点包含 metadata、结构化中途状态、原生战斗包和即时跑局存档，并解析完整 RNG、五牌堆、阵容、历史和当时设置；真实喝药后第 2 回合结果分别记录已喝 1、未来 0 | 2026-08-24 |
| `RADIATE-AND-REQUIRE-ONE-472-477` | 通过 | 崇拜/胜券在王/辉光正确累计本回合星能，辐射连击完整；女王真实第 2 回合检查点当前回合 0 战损斩杀；“至少一瓶”对多瓶路线追加无药反事实并只保留一瓶，喝过药后的重算不重复强制；速度药不残留负敏捷，Smart 致死救药不退化 | 2026-08-24 |
| `INITIAL-OSTY-AND-PAIRED-FORK-465/466` | 通过 | 亡灵契约师持绑定护命匣和赌博筹码时，首回合不重复召唤奥斯蒂，选择后完整状态一致；独白配对状态下攻击 99 荆棘导致中途死亡时清理本次瞬时配对，搜索可继续分叉并按死亡回合暂停 | 2026-08-24 |
| `BUG-REPORT-FORENSICS-469/471` | 通过 | 同一战斗先活动导出、击杀并回主菜单后再次导出；current/recent 均含内存跑局快照、完整 Run RNG、玩家 RNG/odds、检查点、路线和重算审计；战后无当前战斗仍可还原最近一场，同秒连续导出不撞名；5 回合两次洗牌保持零重算 | 2026-08-24 |
| `SMART-POTION-COUNTERFACTUAL-461/463` | 通过 | 淤泥旋螺 Smart 首搜三药但无可信无药终局时追加纯无药审计，找到无药掉 2 后拒绝三药并第 5 回合结束；1 HP 致死反向场景审计确认无药不胜，保留格挡药并第 2 回合获胜 | 2026-08-24 |
| `NECROBINDER-OSTY-RAVENOUS-453/454` | 通过 | 奥斯蒂被连击击杀后由护卫复活，“为你而死”保持 1；蛞蝓同伴死亡后幸存者获得 5 力量并立即进入带原行动后继的 STUNNED；用户完整战斗第 5 回合结束，第 2-5 回合精确复用、零重算 | 2026-08-24 |
| `SECONDARY-END-AND-GENERATION-447-450` | 通过 | 用户储君 Fogmog 存档从错误的 486 回合/470 次洗牌改为第 3 回合结束，完整自动战斗零重算、无生成牌越界；定向主怪击杀+幻象次要敌人首回合正确结束；生成选择 4/4 与实验体三形态保持通过 | 2026-08-24 |
| `REGENT-PRINT-BRANCH-441/446` | 通过 | 固化实机缩小甲虫的储君牌序和生成牌链；两回合具体候选保护窗将节点/选牌/转移/分配约减半，并从基线第 5 回合改善为第 4 回合；完整自动战斗预计/实际掉 3，第 2-4 回合精确复用，零重算；Fisticuffs 日志洪泛为 0 | 2026-08-24 |
| `PRINT-PRUNE-REGRESSIONS-442-445` | 通过 | 生成三选一 4/4、推断卡 15/15、双小啃兽 0 药 0 战损及机甲第 7 回合全部通过；机甲和双小啃兽均零非预期重算 | 2026-08-24 |
| `COMPLETION-AUDIT-428-432` | 通过 | 当前最终 DLL：机甲第 7 回合、预计掉血 36，第 2-7 回合全部精确复用；双小啃兽第 5 回合、两次洗牌、0 药、0 战损；工具盒+烘焙手套+助能三段首回合选择、横祸嵌套选择和千足虫复活均零非预期重算；完整战斗统一使用 Instant/0 秒 | 2026-08-24 |
| `POWER-SHADOW-LIFECYCLE-425-427` | 通过 | Power 数量影子在每次 Hook 批次同步后删除；Burst 回合末不再复活旧层数。重复出牌 11/11 Hook、伤害 Power 十四场及强制 Burst 跨回合续用全部通过 | 2026-08-24 |
| `ROSTER-SOURCE-GATE-408` | 通过 | 原程序集阵容变化共 51 个调用点：47 个单人召唤/逃跑/Osty/宠物来源受支持、3 个 Mock、1 个多人来源、0 未解析；新入口会使普通覆盖门禁失败 | 2026-08-24 |
| `AUTOPLAY-SOURCE-GATE-407` | 通过 | 原程序集 `AutoPlay/AutoPlayFromDrawPile` 共 19 个调用点：18 个单人来源受支持、1 个多人来源、0 未解析；新入口会使普通覆盖门禁失败 | 2026-08-24 |
| `AUTOPLAY-NESTED-CHOICES-403-406` | 通过 | 横祸、破灭、骚动和蒸馏混沌自动打出带选择的牌；搜索规划并实机提交嵌套选择，三场整战零重算并精确复用，药水场完整状态/RNG 差分一致 | 2026-08-24 |
| `COMBAT-CHOICE-SOURCE-GATE-402` | 通过 | 扫描原程序集全部正式模型的 `CardSelectCmd` 调用：85 个调用点中 60 个单人战斗来源受支持、24 个获得遗物流程、1 个多人来源、0 未解析；新来源会使普通覆盖门禁失败 | 2026-08-24 |
| `INITIAL-NATIVE-START-EFFECTS-400/401` | 通过 | 工具盒与七件首回合遗物同场；精确覆盖宝石面具 RNG 移牌、礼炮伤害、谜盒生成、力量电池、扭曲漏斗、石化蟾蜍及低语耳环最多 13 张付费自动出牌；高密度生存者场景另强制覆盖 Vakuu 连续嵌套选牌 | 2026-08-24 |
| `INITIAL-PRE-PLAY-CHOICES-394-398` | 通过 | 从原版首回合 `Start` 阶段搜索并实际提交工具盒、选择悖论、烘焙手套、赌博筹码及助能生存者选择；五场均无玩家界面且进入 `Play` 后完整状态戳一致 | 2026-08-24 |
| `IMBUED-NESTED-CHOICE-393` | 通过 | 助能生存者首回合自动打出，计划并实际弃掉打击；模拟与原版完整牌堆、逐牌状态、资源及 RNG 一致，无玩家默认选择 | 2026-08-24 |
| `SLUMBERING-BEETLE-SILENT-392` | 通过 | 固化用户 6 HP 静默猎手、进阶 10、46/42/89 HP 三敌和原 RNG；盛碗虫完全格挡后进入可见 `STUNNED`，毒伤正确递减熟睡甲虫并切换 `ROLL_OUT_MOVE`；第 2-6 回合全部精确复用，零重算、零战损 | 2026-08-24 |
| `BOWLBUGS-AUTO-CHOICE-391` | 通过 | 直接恢复两个问题包的 BOWLBUGS 开战存档；Mayhem 先固定整批自动牌并给嵌套选择绑定牌身份，牌堆选择使用稳定快照；Custom 5/60s、Instant/0 第 2 回合结束，无选择异常、集合修改和非预期重算 | 2026-08-24 |
| `DECIMILLIPEDE-CONTINUATION-390` | 通过 | 千足虫复活段在下回合可被命中并获得毒雾，第 2 回合精确续用；另使两个 `CONSTRICT_MOVE` 连续两回合叠加 Weak，第 3 回合精确续用；两项均零非预期重算 | 2026-08-24 |
| `KIN-FULL-AUDIT-386D` | 通过 | 用户同族存档按原种子、进阶 10 和满血敌人恢复；中档预算、Instant/0 从首轮执行到第 16 回合结束，追踪之环全程读取分支虚弱，非预期重算为 0 | 2026-08-24 |
| `STATE-CATALOG-GATES-389` | 通过 | `3035` 个 Hook 门禁、分支实机读取、语义动态字段、搜索期状态写入、运行证据和原生重扫边界均为 0 缺口；首回合根状态补齐后剩余 22 个求解接管前快照写入，另有 115 个静态行动图构造器 | 2026-08-24 |
| `CARD-STATE-MUTATION-381-384` | 通过 | 98 张升级卡、8 张降级/恢复卡、5 种附魔及女妖哀嚎/精准打击/践踏/Flatten 入场状态逐字段一致；生成卡保留后续 Hook 监听 | 2026-08-24 |
| `LIFECYCLE-ORDER-378-388` | 通过 | 能量/星费、空手、药水前后、死亡阻止递归均按原版顺序；资源 4 项、药水 17 项、死亡/伤害遗物 11 项通过 | 2026-08-24 |
| `SOLVER-MONSTER-MOVE-AUDIT-387` | 通过 | 57 个补偿怪物行动按永世沙漏、旧日雕像、外骨骼及普通合法宿主分片复跑，完整状态与 RNG 全部一致 | 2026-08-24 |
| `SHRINKER-APPLIER-REUSE-373` | 通过 | 41 HP 缩小甲虫执行无限缩小后，Power 施加者名称、层数和减伤动态值与实机一致；第 2-5 回合全部精确复用，零重算、零未补偿且无错误终局边界 | 2026-08-24 |
| `OBSCURA-VIGOR-EXACT-372` | 通过 | 用户 Obscura 开战前存档恢复进阶 10、第二幕、牌组/遗物/RNG；万向斩组合攻击只消费一次 Vigor，幻象反复复活不产生非法后继；第 10 回合结束且全程零非预期重算。龙涎香 40% 门槛另完成 9 组边界计算 | 2026-08-24 |
| `VANTOM-STRATAGEM-SEARCH-364` | 通过 | 问题包的 Vantom 开战前存档恢复牌组、遗物和 RNG；战略跨洗牌不再抛错，首搜完成 7 回合、2 次洗牌、2623 个选择分支。当前结果仍为死亡线，不冒充生还 | 2026-08-24 |
| `STRATAGEM-SHUFFLE-CHOICE-365` | 通过 | 强制战略 Power 在下回合抽牌时跨洗牌；搜索计划选择打击，实机自动提交，第 2 回合状态精确复用，零重算且无玩家界面 | 2026-08-24 |
| `TEST-SUBJECT-LIVE-END-RISK-362` | 通过 | 开启“战损变差时暂停”并使用用户实验体存档；完整回合末复核计入山铜等遗物格挡，不再把预计 11 误算成 20；第 6 回合剩 66 HP，零误停、零重算 | 2026-08-23 |
| `LIVE-END-TURN-RISK-MINIMAL-363` | 通过 | 原路线以 5 格挡承受 6 点攻击、预计掉 1；提交结束回合前清零格挡后仍正确识别致死，关闭全自动并保留玩家回合 | 2026-08-23 |
| `TEST-SUBJECT-USER-RUN-360` | 通过 | 用户猎手开战前存档精确恢复牌组、遗物、四药水槽与 RNG；实验体三形态第 6 回合结束、实际剩 66 HP，第 2-6 回合全部精确复用，未镜像与非预期重算均为 0 | 2026-08-23 |
| `TEST-SUBJECT-REPTILE-TURN-END-361` | 通过 | 复制药触发爬虫饰品后推进玩家回合末；原版与模拟完整状态一致，复制、临时力量来源和附加力量均按原版移除 | 2026-08-23 |
| `TEMP-PLAN-IMPLEMENTATION-357` | 通过 | 腐臭药水、零权重 RAND、变牌生成 Hook、狠揍嵌套选牌和 Begone 部署事务定向通过；同族智能两药第 13 回合生还，双小啃兽 0 药 0 战损；4 GB No-GC 在第二次搜索前轮换 | 2026-08-23 |
| `DECIMILLIPEDE-REVIVE-350` | 通过 | 从一节 0 HP、下一行动 `REATTACH_MOVE` 开始，实机恢复 25 HP；第 2 回合全体真正死亡，首轮缓存精确复用且零重算 | 2026-08-23 |
| `TEMP-SCULPTOR-MID-358` | 通过 | 精确恢复虔诚雕刻师第 4 回合牌堆、Power、行动历史和 RNG；同回合 0 战损 0 药击杀，未镜像与重算均为 0 | 2026-08-23 |
| `TEMP-KNOWLEDGE-MID-359` | 通过 | 精确恢复知识恶魔第 11 回合状态；同回合 0 战损击杀、零重算，另由 `KNOWLEDGE-DEMON-SEARCH-CHOICE-162` 验证诅咒选择 | 2026-08-23 |
| `LIVE-END-TURN-RISK-PAUSE-270` | 通过 | 同构墨宝安全路线第 2 回合原计划产生 5 格挡；测试在执行后清零格挡，结束回合实机复核得到路线预计 0、当前预计 4 且致死，关闭全自动并保持第 2 回合，不提交结束回合 | 2026-08-23 |
| `INKLETS-RIPPLE-BASIN-269` | 通过 | 复原用户 4 HP、三只墨宝、完整手牌/抽牌、两瓶药和涟漪盆；修复前第 2 回合漏防御并死亡，修复后补打防御，第 2/3 回合精确复用、零战损且第 3 回合结束 | 2026-08-23 |
| `WORSE-RECALCULATION-PAUSE-267` | 通过 | 对一条已算到第 9 回合击杀的旧日雕像完整路线在第 2 回合注入 `4 HP` 状态漂移；记录首个差异、重算预计战损 `32→38`、界面劣化标记，并在执行该回合前关闭全自动 | 2026-08-23 |
| `BUG-REPORT-EXPORT-266` | 通过 | 设置页问题包按游戏口径在后台收集日志、档案、版本和截图，并追加当前战斗精确状态、当前路线、求解器设置和说明；Headless 回归实际创建 ZIP 并逐项验证四个附加条目 | 2026-08-23 |
| `DETAILED-DIAGNOSTIC-LOGS-268` | 通过 | 无设置文件时详细诊断默认关闭且普通日志不含 `[CombatSolver/Debug]`；测试覆盖开启后写出药水槽、分支、层与最终候选诊断 | 2026-08-23 |
| `BYGONE-EFFIGY-CONTINUATION-264` | 通过 | 复原用户旧日雕像的 16 张牌、初始手牌/抽牌顺序、进阶 10、38 HP 与初始弱化；当前版第 2-9 回合全部精确复用并按首轮预测结束。单步 `25` 攻击、`13` 格挡、1 层弱化差分同样通过 | 2026-08-23 |
| `NO-NATIVE-RESCAN-244` | 通过 | `3035` 个钩子中未分析、待实现、缺证据、非通过证据和 `NativeAutoRescan` 均为 `0`；随机生成/选牌、召唤/替换/逃跑、死亡/复活、自动出牌、额外回合、药水槽与私有 AI 均有原生差分或跨回合复用证据 | 2026-08-23 |
| `NIBBITS-NO-RESCAN-246` | 通过 | 固定双小啃兽普通搜索 `1.957s / 360.7MB`，第 `6` 回合、两次洗牌、`0` 药、`0` 战损，第 `2-6` 回合精确复用；增量分叉对完整前缀回放验证同样通过 | 2026-08-23 |
| `MECHA-NO-RESCAN-247` | 通过 | 固定机甲 `5s/60s`：headless `8.207s / 2.212GB`，Steam 正常可见完整 Mod 栈 `9.208s / 2.291GB`，均第 `8` 回合、预计战损 `31`；可见会话 GC `0ms`、最大帧间隔 `11.0ms` | 2026-08-23 |
| `PARTICLE-WALL-TOUCH-176` | 历史通过（旧算法已退役） | `0.12.5` 的同构牌堆从修复前 `1200` 节点、`2` 回合、`NodeLimit` 改为 `619` 节点、`17` 次无进展循环剪枝并第 `3` 回合零损击杀；反向场景保留 `9` 动作首回合击杀。该命名兑现例外不再属于现行设计，下一版本由顶部通用周期/出口 fixture 接替门禁。 | 2026-08-22 |
| `LAGAVULIN-DEPLOY-REPLAN-175` | 通过 | 乐加维林族母睡眠阶段第 `2` 回合精确复用；`BEAT_INTO_SHAPE` 正常路线首回合真实打出；部署中实机拒绝动作会从当前状态重搜而非中止 | 2026-08-22 |
| `PERFORMANCE-PRESETS-170` | 通过 | 无设置文件时默认中档 `5/60s + 6GB`、死亡暂停开、战斗结束暂停关；低档和高档分别完整断言 `2/20s + 4GB` 与 `8/120s + 8GB` 及对应 Beam、节点、出牌分支；自定义保持独立预设身份；双小啃兽维持第 `6` 回合 `0/0` | 2026-08-22 |
| `KNOWLEDGE-BOSS-POLICY-162` | 通过 | 知识恶魔评估 `396` 个选牌分支并计划/执行 `MIND_ROT`，选择结算后第 `2` 回合精确复用；二幕 Boss 与三幕第二 Boss 标记战后回血，三幕首 Boss 与普通战斗不标记；死亡回合暂停保持战斗进行并交还操作权 | 2026-08-22 |
| `NIBBITS-0.12.2-REGRESSION-163` | 通过 | 普通战斗策略不受幕末 Boss 权重影响：固定双小啃兽第 `6` 回合、两次洗牌、`0` 药、`0` 战损、`0` 卖血并第 `3` 回合复用；首轮 `2.119 s / 386 MB` | 2026-08-22 |
| `LONGLINE-0.12.1-161` | 通过 | 双小啃兽第 `6` 回合、两次洗牌、`0` 药、`0` 战损、`0` 卖血并第 `3` 回合复用，首轮 `2.288 s / 386 MB`；机甲 headless `9.820 s / 2.266 GB`，Steam 可见完整 Mod 栈 `9.818 s / 2.350 GB / 0 ms GC / 8.4 ms 最大帧间隔`，均第 `8` 回合、预计战损 `31`、`0` 药并第 `3` 回合复用 | 2026-08-22 |
| `VITAL-SPARK-LIFECYCLE-160` | 通过 | 感染棱柱连续执行 `RADIATE_MOVE → PULSATE_MOVE`；玩家既有技能牌污染保持不丢失，活力火花和逐牌污染从 `2` 同步到 `4`，两步完整牌堆、Power 与 RNG 差分一致 | 2026-08-22 |
| `UNATTENDED-CHOICE-FIXES-159` | 通过 | 雕琢打击唯一候选按实际虚无状态核销、宇宙冷漠按抽牌堆顶核销，二者均进入后续回合精确复用；知识恶魔不可跳过但 `minSelect=0` 的选择自动提交 `MIND_ROT`，玩家获得腐化心智且无界面干预 | 2026-08-22 |
| `RELIC-HIDDEN-STATE-158` | 通过 | 钢笔尖按“愤怒第 `9` 击 → 重锤第 `10` 击×2”首回合 `0` 战损击杀并生成 `钢笔尖×2` 胶囊；百年积木覆盖完全格挡、首次抽 `3` 和不重复触发；金纸覆盖 `5` 次耗尽抽 `1` 与余数 `0`；持久遗物状态门禁为 `0` 缺口 | 2026-08-22 |
| `SOLVER-RUNTIME-ROBUSTNESS-157` | 通过 | 女王战同时持有两瓶迅捷药水时 UI/搜索正常且首回合结束；仅死亡候选显示 `OnlyDeath=True` 并真实死亡；`Instant + 0.05s` 仅内存覆盖，自动执行后恢复 `Normal` 且不生成测试设置文件；Steam 截图确认固定状态列与钢笔尖、音乐盒遗物后缀 | 2026-08-22 |
| `MECHA-VISIBLE-STEAM-145` | 通过 | `0.12.0`、Steam 正常可见会话、用户完整 Mod、固定机甲 `5s/60s`：首轮 `14.736 s / 2.438 GB`、GC `0 ms`、最大帧间隔 `42.5 ms` 且无 `>50 ms` 帧；第 `8` 回合、1 次洗牌、`0` 药、`0` 卖血、Unmirrored=`0`，预计战损从旧基线 `43` 降至 `31`；结束后托管堆约 `259 MB`、工作集约 `2.30 GB` | 2026-08-22 |
| `NIBBITS-ADAPTATION-REGRESSION-139` | 通过 | `0.12.0` 适配层完成后复跑固定双小啃兽：首轮 `1880.2 ms`、第 `6` 回合、两次洗牌、`0` 药、`0` 战损、`0` 卖血、Unmirrored=`0`；第 `3` 回合命中精确复用并无人值守结束战斗 | 2026-08-22 |
| `CARD-EFFECT-SPEC-BATCH-137` | 通过 | 参数化 Power、资源、自伤、最大生命和一次性 Power 消耗共 `46` 条模拟/原生完整快照差分通过 | 2026-08-22 |
| `CARD-COMPLETION-BATCH-123` | 通过 | 补齐卡牌、既有牌选择、击杀奖励、Osty、永久牌面成长和 X 费共 `32` 条差分通过；修复选择后抽牌时来源牌过早进入弃牌堆并参与同次洗牌 | 2026-08-22 |
| `CALCULATED-CARD-BATCH-136` | 通过 | `25` 个代表场景验证牌堆、历史、Power、Osty、能量、弃牌、抽牌、星能和格挡的分支内 CalculatedVar；目录强制全部 `43` 张相关卡牌有公式 | 2026-08-22 |
| `POWER-EFFECT-COMPLETION-135` | 通过 | 毒、灾厄、临时力量、撕裂、吸取、召唤、抽牌/生成触发、墨染、眩晕动态边界、毁灭和必死共 `13` 条差分通过 | 2026-08-22 |
| `CARD-GENERATION-SPEC-BATCH-138` | 通过 | `11` 类固定生成、复制、升级和随机牌堆插入效果的牌堆、牌面及 RNG 差分通过 | 2026-08-22 |
| `CARD-GENERATED-CHOICE-BATCH-121` | 通过 | 富足、发现、类星体和飞溅的生成三选一由求解器分支并自动驱动原生选择界面，`4/4` 完整差分通过 | 2026-08-22 |
| `RELIC-COMPLETION-133` | 通过 | 自成型黏土、破甲钻、螺旋飞镖、苦无、彩虹戒指、手里剑和红头骨组合触发 `6/6` 差分通过 | 2026-08-22 |
| `MECHA-VISIBLE-STEAM-103` | 通过 | 两轮 Steam 正常可见会话、完整用户 Mod 组合、`5s/60s` 与默认 `6 GB` No-GC；首轮 `7.80 s / 1.806 GB`，最终复核轮 `8.49 s / 1.818 GB`，GC 均为 `0 ms`，p95/p99 `16.7 ms`、最大帧间隔不超过 `23.6 ms`，无 `>33/50/100 ms` 帧；第 `6` 回合 `0` 药、预计掉血 `43`，第 `2-6` 回合精确复用。Reset 后托管堆约 `259 MB`、工作集约 `2.51-2.56 GB` | 2026-08-22 |
| `MECHA-FINAL-OPT-097` | 通过 | 固定机甲 `5s/60s`、统一 `12/30` Beam 保持 `1453` 展开、`13338` 转移、第 `6` 回合、`0` 药、预计掉血 `43` 和第 `2-6` 回合精确复用；headless 首轮 `7.02 s / 1.73 GB`、GC `0 ms`、最大帧间隔 `20.3 ms`，相对 `0.11.2` 的 `2.90 GB` 分配下降约 `40.2%` | 2026-08-22 |
| `LONGLINE-DIFF-OPT-101` | 通过 | 双小啃兽固定快照在验证模式对 `2712` 个增量转移同步执行完整前缀回放；状态文本、双指纹、边界、风险、死亡集合与 RNG 全部一致，最终第 `6` 回合 `0` 药、`0` 战损并逐回合复用 | 2026-08-22 |
| `NIBBITS-SNAPSHOT-RELEASE-096` | 通过 | 紧凑 History、稀疏回合末卡牌清理和历史 Simulator 释放后，双小啃兽仍为第 `6` 回合 `0/0`、跨两次洗牌并精确复用；普通搜索约 `1.83 s / 380 MB` | 2026-08-22 |
| `PERF-END-TURN-CLEANUP-FINAL-099` | 通过 | 子弹时间把未打出的打击费用降为 `0` 后执行完整玩家回合结束；模拟与原版均把牌移入弃牌堆并将打击恢复到 `1` 费，验证稀疏清理不会漏掉 EndOfTurn 费用修正 | 2026-08-22 |
| `PERF-DAMAGE-PIPELINE-100` | 通过 | 缩小甲虫两组真实/模拟伤害差分通过，覆盖力量、虚弱、易伤、格挡、回合末伤害与单目标无批量容器路径 | 2026-08-22 |
| `QUEEN-CHAINS-OPT-102` | 通过 | StateStore eager fork、ForkContext 及时释放和历史 Simulator 释放后，女王束缚锁链仍在第 `2/3` 回合逐字段一致，第 `3` 回合命中精确续用 | 2026-08-22 |
| `CORPSE-SLUGS-OPT-103` | 通过 | 紧凑 History、StateStore eager fork 与单目标伤害路径下恢复用户噬尸蛞蝓快照，全自动第 `4` 回合结束，无 pending Power 变化或 Fork 异常 | 2026-08-22 |
| `MECHA-VISIBLE-STEAM-086` | 通过 | Steam 正常可见会话、用户完整 Mod 组合、统一 `12/30` Beam 与默认 `6 GB` No-GC；首轮 `9.57 s / 2.90 GB` 求解线程分配、GC `0 ms`，最大帧间隔 `88.7 ms`、`>50 ms` 为 `1`、无 `>100 ms` 帧；第 `6` 回合 `0` 药、预计掉血 `43`，第 `2-6` 回合精确复用。必备工具第 `2-6` 回合均消费 `1` 个计划选择，每回合末胶囊完成态与部署完成日志齐全；战斗 Reset 后托管堆约 `372 MB`、工作集约 `423 MB` | 2026-08-22 |
| `NIBBITS-UNIFIED30-SOLD-CAP-084` | 通过 | 双小啃兽固定快照验证取消药水独立 Beam 后的统一 `30` 宽度与恢复后的卖血硬剪枝；首轮约 `2.25 s / 439 MB`，剪掉 `102` 条超卖血预算路线，第 `6` 回合 `0` 药、`0` 战损，第 `2-6` 回合精确复用 | 2026-08-22 |
| `QUEEN-CHAINS-REUSE-FINAL-085` | 通过 | 女王与火炬头场景强制女王首轮使用 `PUPPET_STRINGS_MOVE`；束缚锁链施加后第 `2/3` 回合均与首轮预测状态逐字段一致，第 `3` 回合命中 `SEARCH_REUSED`，夹具在命中目标续用后退出战斗 | 2026-08-22 |
| `CORPSE-SLUGS-USER-RUN-073` | 通过 | 从用户 `Y883BRPFJZ05` 跑局快照恢复噬尸蛞蝓战；同伴死亡后的 `RAVENOUS_POWER` 力量变化完成 Power 生命周期结算后再分叉，不再出现 `Cannot fork with pending Power amount changes`，全自动第 `6` 回合结束 | 2026-08-22 |
| `MECHA-VISIBLE-STEAM-071` | 通过 | 由 Steam `-applaunch 2868840` 启动正常可见游戏，加载用户完整 Mod 组合并恢复固定机甲快照；默认 `6 GB` No-GC 下首轮 `9.62 s / 2.32 GB` 求解线程分配、GC `0 ms`，最大帧间隔 `39.4 ms`、`>50/100 ms` 均为 `0`；第 `7` 回合 `0` 药、预计掉血 `40`，第 `2-7` 回合精确复用。战斗 Reset 后托管堆约 `359 MB`、工作集约 `2.78 GB`、私有提交约 `6.04 GB` | 2026-08-22 |
| `NIBBITS-FINAL-071` | 通过 | 默认 `22+7` Beam 下恢复双小啃兽固定快照；首轮约 `1.76 s / 389 MB`，第 `6` 回合 `0` 战损、`0` 药损，第 `2-6` 回合精确复用，最大帧间隔 `22.0 ms`、无 `>50 ms` 帧 | 2026-08-22 |
| `SETTINGS-NOGC-071` | 通过 | 隔离设置写入 `5.5 GB` No-GC 预算后，启动日志解析为 `5,500,000,000 B`，实际区域使用 `5.5 GB / 1.1 GB LOH`；首回合烟雾战斗正常结束，测试设置文件随后删除 | 2026-08-22 |
| `MECHA-FINAL-071` | 通过 | 固定 `MECHA_KNIGHT_ELITE` 跑局快照与 `5s/60s` 配置验证单会话 anytime Beam；首轮约 `11.7 s / 3.14 GB`，第 `9` 回合结束、`0` 药、预计掉血 `40`，第 `2-9` 回合精确续用；GC 约 `3.26 s`、单次低于 `30 ms`，headless 无 `>50 ms` 帧，战斗 Reset 后统一压缩 | 2026-08-22 |
| `SOLVER-ROUTE-HISTORY-071` | 通过 | 固定双小啃兽 `19` 张牌与 RNG 快照；历史固定计数器和单会话搜索保持第 `6` 回合 `0` 战损、`0` 药损、第 `2-6` 回合精确续用；首轮约 `2.04 s / 427 MB` | 2026-08-22 |
| `LONGLINE-DIFF-071` | 通过 | 双小啃兽固定快照对 `5748` 个增量转移同步执行完整前缀回放；状态文本、双指纹、边界、风险、牌堆与 RNG 全部一致，最终第 `6` 回合 `0/0` 并逐回合续用 | 2026-08-22 |
| `SMOKE-FINAL-071` | 通过 | `0.11.0` 最终 Release 部署后，铁甲战士在原版 headless 进程搜索并真实打出打击，首回合结束战斗 | 2026-08-22 |
| `MECHA-RF-SUSTAINED-071` | 通过 | 隔离 headless 同时加载官方 RF `0.13.8`、RitsuMetrics `0.1.37` 和 RitsuLib；`SustainedLowLatency` 下机甲首轮 `11.64 s / 3.15 GB`、GC `3.26 s/22.8 ms max`，无 `>50 ms` 帧，并保持第 `9` 回合、`0` 药、预计掉血 `40`。同栈 `Interactive` 对照出现一次 `142.5 ms` GC/`>100 ms` 帧 | 2026-08-22 |
| `MECHA-MEMORY-FULL-AUTO-FINAL-070` | 通过 | 从用户最新 `current_run.save` 提取牌组、遗物与 RNG，复现 `MECHA_KNIGHT_ELITE` 和 `5s/60s`：首轮 `16.61 s / 4.20 GB` 分配，GC 累计 `5.12 s`、单次最大 `30.5 ms`，主线程最大帧间隔 `43.9 ms` 且 `>50 ms` 为 `0`；第 `2-9` 回合精确续用并真实全自动结束。战斗 Reset 后压缩 `145.5 ms`，托管堆 `110.8 MB`、碎片 `0.16 MB`、工作集约 `2.04 GB` | 2026-08-21 |
| `GC-FREEPLAY-BULLET-TIME-070` | 通过 | 求解作用域隔离 Ritsu 免费出牌全局状态后，子弹时间、整手费用与原生结算差分继续一致 | 2026-08-21 |
| `GC-LONGLINE-DIFF-FINAL-070` | 通过 | 内存修复后长线增量/完整前缀回放逐字段一致，第 `6` 回合 `0/0`、第 `2-6` 回合精确续用；验证模式 GC 累计 `877 ms`、单次最大 `11.6 ms`、主线程最大帧间隔 `28.4 ms` | 2026-08-21 |
| `EMBEDDED-ENGINE-LONGLINE-DIFF-069` | 通过 | RF 本地版共同加载时，从只读快照恢复种子 `BJCZX3J13PZJ`；内置引擎对 `2540` 个实际增量转移逐一执行旧完整前缀回放，状态文本、双指纹、边界、风险和 RNG 全部一致；第 `6` 回合 `0` 战损、`0` 药损，第 `2-6` 回合精确复用 | 2026-08-21 |
| `EMBEDDED-NO-RF-TOOLS-TURN-START-069` | 通过 | 游戏目录已移除 RF；必备工具第 `2-5` 回合的抽 `1` 弃 `1` 全部由首轮 Beam 规划并自动提交、逐回合精确复用，跨边界后的计划外选择由守卫自动处理并重搜，最终第 `11` 回合结束且无玩家干预 | 2026-08-21 |
| `EMBEDDED-NO-RF-ENTROPY-FINAL-069` | 通过 | 游戏目录已移除 RF；熵按路线逐回合选择并变换手牌，真实 `CombatCardSelection` RNG 与预测一致，第 `2-6` 回合精确复用；跨边界后继续自动选择与重搜，最终第 `14` 回合结束 | 2026-08-21 |
| `DECOUPLED-HEADLESS-SMOKE-069` | 通过 | 独立 `APPDATA/LOCALAPPDATA`、Steam 关闭、临时 RitsuLib 投影的原版 `--headless` 进程加载 CombatSolver `0.10.0`，搜索并真实自动打出打击首回合结束战斗；进程退出后临时依赖目录被删除 | 2026-08-21 |
| `TOOLS-TURN-START-FINAL-068` | 通过 | 注入 `1` 层必备工具，真实全自动战斗每回合按搜索结果弃牌；第 `2-4` 回合逐字段精确续用，首轮未补偿项为 `0`，全程无玩家选牌 | 2026-08-21 |
| `ENTROPY-TURN-START-LIVE-068D` | 通过 | 注入 `1` 层熵，真实全自动战斗按路线逐回合选择并随机变换手牌；第 `2-4` 回合牌序、变换结果和 `CombatCardSelection` RNG 精确续用，首轮未补偿项为 `0` | 2026-08-21 |
| `UNPLANNED-TURN-CHOICE-GUARD-068` | 通过 | 注入未进入长线镜像的既定事项回合开始选牌；部署守卫从抽牌堆自动选择最高价值牌、清除旧续用并于第 `2` 回合重搜，随后继续全自动至战斗结束，无玩家选牌 | 2026-08-21 |
| `CARD-ON-PLAY-GAPS-068` | 通过 | 斗篷与匕首、闪躲翻滚连续实机/模拟差分；验证 `10` 格挡、`4` 层下回合格挡、手牌生成小刀及弃牌堆顺序 | 2026-08-21 |
| `CARD-CHOICE-TRANSFORM-FINAL-068` | 通过 | 熵接入通用原位置变换后重跑固定变换选牌实机/模拟差分，牌堆位置、牌状态和变换结果一致 | 2026-08-21 |
| `HUNTER-KILLER-TENDER-067` | 通过 | 猎人杀手战斗中给玩家注入 `1` 层 Tender 和 `8` 张零费小刀；增量分叉/完整回放一致，求解器规划并真实执行 `3` 张后首回合击杀，无 pending Power 队列或搜索失败 | 2026-08-21 |
| `RF-FORK-DIFF-067` | 通过 | Tender 历史补偿循环结算修复后，主长线完整增量差分继续通过；第 `6` 回合 `0` 战损、`0` 药损且第 `2-6` 回合精确续用 | 2026-08-21 |
| `SETTINGS-PERSISTENCE-066` | 通过 | 备份/恢复范围内写入自定义设置，跨进程加载 `1.25/7.5 s`、Beam `7/16`、节点/分支预算及 UI 坐标 `111,77`；搜索 `WEIGHTS` 使用自定义值，运行前后文件 SHA256 一致，测试文件随后删除 | 2026-08-21 |
| `SETTINGS-FINAL-066` | 通过 | 无配置文件时按默认值创建完整设置 UI 并完成搜索/自动出牌；无人测试的非持久暂停开关同步不会创建或误写用户设置文件 | 2026-08-21 |
| `RF-FORK-PERF-065-FINAL` | 通过 | 用户要求两轮最终独立进程样本通过后停止继续统计并定版；两轮均为第 `6` 回合 `0/0`、逐回合精确续用、Gen2 `0`，中点 `208,448,400 B / 1.550 s / 218.7 ms GC`，约 `198.8 MiB` | 2026-08-21 |
| `RF-FORK-DIFF-065-FINAL` | 通过 | 无行动上限与最终 COW/Hook 缓存版本在长线快照比较 `2540` 个实际增量转移和完整前缀回放；完整状态文本、双指纹、边界、风险、死亡集合和 RNG 一致，最终第 `6` 回合 `0` 战损、`0` 药损并逐回合续用 | 2026-08-21 |
| `TWO-STAGE-AGGRESSIVE-064` | 通过 | 测试态将短搜压到 `1 s` 触发深化，生产同款 `24+8` Beam 深化展开 `1219` 节点、命中 `670` 次转移缓存，在搜索空间耗尽时提前返回并将有战损路线改善为第 `6` 回合 `0/0`；默认预算仍为短搜 `3 s`、深化 `20 s` | 2026-08-21 |
| `UNBOUNDED-ACTIONS-065` | 通过 | 清空牌堆后注入 `8` 张零费小刀，求解器在同一回合规划并真实执行全部 `8` 个动作后击杀；证明原 `7` 次回合内行动上限已删除 | 2026-08-21 |
| `TWO-STAGE-UNAVOIDABLE-064` | 通过 | 固定不可避免伤害牌组触发深化；深化无严格改善时保留短结果，主动卖血仍为 `0` | 2026-08-21 |
| `RF-FORK-DIFF-061` | 通过 | 创意工坊 RF 已取消订阅，游戏只加载本地 API `1` / 上游 `598dce0` fork；长线固定快照对 `2541` 个候选同时执行增量分叉和完整前缀回放，状态文本、指纹、边界、风险和 RNG 全部一致，最终第 `6` 回合结束、`0` 战损、`0` 药损 | 2026-08-21 |
| `RF-FORK-PERF-061` | 通过 | 相同长线固定快照在三次干净游戏进程中均通过；性能中位数 `605,129,120 B / 2.394 s / 505.6 ms GC 暂停 / gc2=0`，相对旧基线分别降低约 `89.9% / 89.3% / 95.1%`，通过 `0.9 GB / 5.6 s / 暂停降低 80%` 门槛 | 2026-08-21 |
| `RF-FORK-REGRESSION-061` | 通过 | 本地 fork 最终 DLL 复跑三条卖血策略与瀑布巨兽：防御选择、不可避免伤害、稳定不卖血均保持原断言；瀑布巨兽严格第 `2` 回合结束；日志无 RF 错误、Fork 映射遗漏或搜索失败 | 2026-08-21 |
| `SOLVER-ROUTE-POLICY-060` | 通过 | 从只读快照恢复种子 `BJCZX3J13PZJ` 的完整 `19` 张牌、四件遗物和全部 RNG；首手 `7`、抽牌堆 `12`、敌人 `42/46` 与 `SLICE/HISS` 均与原局日志一致。首轮找到第 `6` 回合结束的 `0` 战损、`0` 药损路线，第 `2-6` 回合全部精确复用；同时验证生存者选牌先于来源牌进入弃牌堆。性能记录为 `2542 replays / 5.99 GB / 22.4 s`，不登记为性能通过 | 2026-08-21 |
| `SOLD-HP-POLICY-BATCH-059` | 通过 | 三份固定牌组验证稳健卖血策略：能直接击杀威胁时选择 `0/5` 而不故意卖 `4`，有防御选择时保持 `0/5` 并剪除超预算路线，无防御时实际掉血但卖血仍为 `0`；跨回合精确复用继续保留累计值 | 2026-08-21 |
| `RELIC-POWER-BATCH-058` | 通过 | 最终 Release DLL 在同一可见游戏 PID 完成两场差分：损毁头盔首次力量翻倍后正确消费状态；不安油灯使首张有效减益牌的全部减益翻倍，并跳过已翻倍临时 Power 的内部力量。另 `4` 个永久 `Deck` 遗物钩子完成全调用点静态审计；覆盖目录达到 `3035/3035`、未分析 `0` | 2026-08-21 |
| `RELIC-REACTIVE-BATCH-057` | 通过 | 最终 Release DLL 在同一真实可见游戏进程连续完成 `11` 个最终请求，关闭 `38` 个未分析遗物条目：`21` 项覆盖药水响应、格挡清空、手牌清空、星能、回合结束、充能球、空手抽牌、伤害倍率及三个动态边界，`17` 项完成源码、初始快照、召唤边界、药水重搜和纯表现静态闭环；覆盖未分析降至 `8` | 2026-08-21 |
| `RELIC-TURN-LIFECYCLE-BATCH-056` | 通过 | 最终 Release DLL 在两个真实可见游戏进程中完成 `8` 个最终请求、`15` 条完整状态差分，关闭 `24` 个未分析遗物条目并纠正 `4` 个 RF 风险/ignored 假精确条目；覆盖私有计数重置、攻击/技能/能力触发、金币与星能、跨回合能量、奥斯蒂、充能球、生成牌、格挡冷却及受伤上限，另 `2` 项完成动态边界与纯表现静态闭环；覆盖未分析降至 `46` | 2026-08-21 |
| `RELIC-TURN-START-BATCH-055` | 通过 | 最终 Release DLL 在同一真实可见游戏 PID 中完成 `5` 个最终请求、`6` 条完整状态差分，关闭 `26` 个未分析遗物条目并纠正孙子兵法 `1` 个 RF ignored 假精确条目：`25` 项覆盖首回合资源/Power/升级/伤害、攻击历史、第 `2/3` 回合能量、私有计数、充能球和精英房条件，`2` 项随机生成牌完成静态边界闭环；覆盖未分析降至 `70` | 2026-08-21 |
| `RELIC-HOOKS-BATCH-054` | 通过 | 最终 Release DLL 在同一真实可见游戏 PID 中连续完成 `7` 个请求、`8` 条完整状态差分，关闭 `20` 个遗物条目：`18` 项覆盖未来回合能量/手牌/格挡、X 值、Power 层数、费用、充能球、伤害、失血与仆从牌倍增，`2` 项完成源码与动态边界静态闭环；覆盖未分析降至 `96` | 2026-08-21 |
| `RELIC-DRAW-STATE-BATCH-053` | 通过 | 最终 Release DLL 在真实可见游戏中完成摆动球/花粉核心六回合周期、怀表 `4/0/3` 张阈值和四件首回合生成遗物快照，共 `10` 条完整状态差分；遗物私有计数另与跨回合复用文本逐回合比较；关闭 `15` 个未分析项并纠正 `1` 个 RF ignored 假精确项 | 2026-08-21 |
| `RELIC-PURE-HOOKS-BATCH-052` | 通过 | 最终 Release DLL 完成组合抽牌、组合最大能量、原生精英房轰鸣海螺及连续第 2/3 回合共 `4` 个最终请求；关闭 `20` 个遗物纯 Hook，定位并修复未来搜索仍读取实时回合号的问题；另 `11` 个范围外/纯表现钩子完成源码与构建静态闭环 | 2026-08-21 |
| `POWER-LIFECYCLE-BATCH-051` | 通过 | 最终 Release DLL 完成 `22` 个最终真实游戏请求，关闭最后 `31` 个未分析 Power 钩子并纠正 `6` 个 RF 忽略/空处理造成的假精确项；`31` 项实机闭环覆盖 Power 数值触发、资源、入场附魔、私有计数、回合末、唤醒/逃跑/选牌动态边界及凯撒巨蟹药水朝向，另 `6` 项仅完成源码与构建静态闭环 | 2026-08-20 |
| `POWER-DEATH-BATCH-050` | 通过 | 最终 Release DLL 在真实可见游戏中完成 `16` 个最终请求，关闭 `37` 个 Power 死亡、移除与清格挡条目：`33` 项实机闭环覆盖蟹之怒、自成型黏土、坚韧之环、饥饿及死亡后复活/召唤/换位等动态边界，`4` 项完成源码与构建静态闭环；开发期向错误宿主注入幻象和饥饿的两次失败保留为夹具审计证据，改用原生宿主后通过 | 2026-08-20 |
| `POWER-TURN-START-BATCH-049` | 通过 | 最终 Release DLL 在同一可见游戏进程连续完成 `19` 个请求，关闭 `22` 个回合开始/生成/随机边界/致死语义并复跑夜魇、绯红披风和野性；固定生成、奥斯蒂、充能球、私有计数、随机目标 RNG 与沙坑致死逐字段一致，七种随机生成/选牌效果均由正式搜索返回 `DynamicResolution`；另 `2` 个虚空形态瞬时时序完成源码与构建静态闭环 | 2026-08-20 |
| `POWER-END-TURN-BATCH-048` | 通过 | 同一可见游戏 PID 连续完成四场、`10` 项差分，关闭 `14` 个 Power 配对/回合末/死亡钩子；覆盖独白力量回收、湮灭施加灾厄、魔法炸弹伤害与施加者死亡，以及神气制胜和胆小的跨回合私有计数 | 2026-08-20 |
| `POWER-NATIVE-HOOKS-BATCH-047` | 通过 | 真实可见游戏对真实态与模拟态调用原生抽牌、最大能量、清格挡和清手牌纯钩子；组合 Power、实际打出友谊后的下一回合、第 031 批资源及手牌生命周期回归全部通过 | 2026-08-20 |
| `POWER-TRIGGER-BATCH-047` | 通过 | 实际打出绯红披风后推进完整下一玩家回合，验证 `1` 点自伤与 `7` 点格挡；另验证野性中途施加会继承本回合已有零费攻击历史 | 2026-08-20 |
| `ENCHANTMENTS-ORB-BATCH-046` | 通过 | 真实可见游戏完成 `13` 种附魔与等离子球的生产模拟/原生生命周期差分；覆盖附魔数值、启用状态、私有成长、重复次数、自动预出牌、清空手牌前降费和回合开始能量 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-046-EXACT` | 通过 | 真实可见游戏逐项强制并差分 `22` 个怪物行动；覆盖攻击、力量、虚弱、烟雾、偷取状态、沙坑、随机向抽牌堆/弃牌堆插牌和相关 RNG | 2026-08-20 |
| `MONSTER-DYNAMIC-BOUNDARY-BATCH-046` | 通过 | 真实可见游戏以召唤、后续 AI 私有状态和牌库改写三类代表运行正式后台搜索；三个行动均在敌方结算后、下一玩家回合建立前返回 `DynamicResolution` | 2026-08-20 |
| `POTION-ON-USE-BATCH-045` | 通过 | 真实可见游戏关闭剩余 `19` 个药水入口及再生生命周期：覆盖手牌/抽牌堆/弃牌堆选择、自动复活、最大生命、锻造、整副打击重复次数和动态生成后同回合重搜；最终狡诈药水由全自动原生使用，作废旧路线后同回合重搜并打出三张升级小刀结束战斗 | 2026-08-20 |
| `POTION-ON-USE-BATCH-044` | 通过 | 真实可见游戏完成 `30` 种确定性药水即时生产模拟/原生使用差分，另完成 `3` 条临时属性生命周期及 `2` 条无法获得能量交互差分；最终火焰药水由搜索选中、全自动通过原生队列使用、消耗槽位并在第 `1` 回合结束战斗 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-043` | 通过 | 同一可见游戏 PID `8404` 连续执行 `10` 个场景、`12` 条生产模拟/原生状态差分，关闭 `17` 个即时与跨回合卡牌条目；另以最终 DLL 的 PID `23060` 验证虚空形态实际被搜索、自动打出并强制推进至第 `2` 回合。覆盖奥斯蒂当前/最大生命、复活、X 费生成与 RNG、选牌、消耗堆连锁、实例 Power、出牌限制、回合结束/抽牌前/自动预出牌阶段和升级分支；另静态关闭 `12` 项 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-042` | 通过 | 同一可见游戏 PID `46700` 连续执行 `21` 个最终场景，关闭 `24` 个确定性 `OnPlay`；验证随机目标/插牌、击杀递归、毒触发、多敌伤害、私有计数，以及选牌、可选空选择、跨牌堆移动、变形、复制、局部费用、重复次数和 `6` 组战斗 RNG | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-041` | 通过 | 同一可见游戏 PID `39540` 连续执行 `6` 个场景、`7` 条最终差分，关闭 `24` 个确定性 `OnPlay`；验证 Power、能量/星能、临时集中及回收、Orb 种类计数、跨牌堆君王之剑、锻造、虚无、小刀、局部费用和整手弃牌；另静态排除 `3` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-040` | 通过 | 同一可见游戏 PID `31288` 连续执行 `6` 组最终差分，关闭 `28` 个确定性 `OnPlay`；验证 Power、能量/星能、按当前格挡延迟获得格挡、目标中毒/湮灭、追踪之刃生成并锻造君王之剑；另静态排除 `3` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-039` | 通过 | 两个可见游戏进程共执行 `13` 组差分，关闭 `23` 个确定性 `OnPlay`；验证临时力量/集中力、自伤资源顺序、多层人工制品移除、双敌全体减益、小刀生成、墨染附魔、全牌堆升级、普通/升级费用持续时间和整手弃牌替换；另静态排除 `3` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-038` | 通过 | 同一可见游戏 PID `43080` 连续执行 `7` 组差分，关闭 `25` 个确定性 `OnPlay`；验证 Power、治疗、能量/星能、疯狂进食的临时力量双 Power，以及无处可逃按已有灾厄分段计算；另静态排除 `11` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-037` | 通过 | 同一可见游戏 PID `39500` 连续执行 `6` 组差分，关闭 `26` 个确定性 `OnPlay`；验证 Power、能量/星能、条件中毒、锻造、子弹时间零费化，以及野性/杂耍中途施加时继承已有攻击计数；另静态排除 `1` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-036` | 通过 | 同一可见游戏 PID 连续执行 `3` 组差分，关闭 `19` 个确定性 `OnPlay`；验证 Power、余像后续格挡、扩容槽位 `3→5`，以及预判临时敏捷的玩家回合末回收；另静态排除 `1` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-035` | 通过 | 同一可见游戏 PID 连续执行 `5` 组差分，关闭 `20` 个 RF 未镜像的卡牌 `OnPlay`；验证 Power 顺序、X 费用、星能、充能球槽位、锻造与君王之剑伤害、下回合能量及尖啸回合末恢复 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-034` | 通过 | `13` 组可见游戏差分关闭 `22` 个单人 Power 钩子；验证伤害/格挡/费用/牌去向、首次攻击/小刀/格挡预测计数，以及为你而死的单段承伤、`8` 点溢出、多段中途死亡、死亡保留、不可选中和 Power 保留 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-033` | 通过 | 最终 DLL 共执行 `16` 条生产模拟/原生回调差分；覆盖 `20` 个单人 Power 钩子，包括触发上毒、伤害修正、自伤、生命周期、Orb 唤起和私有计数；巨像奇数伤害行动前移除的首次偏差已修复并复测 | 2026-08-20 |
| `SMOKE-002` | 通过 | 铁甲战士进入 `FUZZY_WURM_CRAWLER_WEAK`；敌人 `1 HP`；向手牌注入 `STRIKE_IRONCLAD`；全自动实际出牌并结束战斗；胜利进度写入被隔离 | 2026-08-20 |
| `MONSTER-WATERFALL-001` | 通过 | `0.7.0` 最终 Release：`WATERFALL_GIANT_BOSS` 为 `1 HP` 且拥有 `SteamEruptionPower:10`；提前击杀后依次进入蓄爆与爆炸，严格在第 `2` 回合结束；首轮 `116 replays / 74.65MB`，第二回合精确复用 `0 replays / 0 bytes / 0ms` | 2026-08-21 |
| `MONSTER-AXEBOT-HAMMER-001` | 通过 | 在 `AxebotsNormal` 强制执行 `HAMMER_UPPERCUT_MOVE`；生产预测与真实 `PerformMove` 逐字段比较；确认 `14` 点伤害、`2` 层虚弱、`2` 层脆弱完全一致 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-004` | 通过 | 单次进入 `LivingFogNormal`，按需召唤青蛙骑士、电球头和气态炸弹；连续完成 `7` 个生产预测与真实 `PerformMove` 差分；全部逐字段一致 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-005` | 通过 | 单次进入 `LivingFogNormal`，召唤幽灵船和猎人杀手；连续完成 `6` 个行动差分，并新增四个战斗牌堆的卡牌计数比较；纠缠的 `5` 张暈眩与真实弃牌堆一致 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-006` | 通过 | 单次进入 `LivingFogNormal`，召唤守护机器人、感染棱柱、墨宝和环境组装师；连续完成 `8` 项差分，并按模型比较全场敌人格挡；首次缺少组装师的夹具失败已保留 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-007` | 通过 | 单次进入 `LivingFogNormal`，召唤同族信徒、同族神官和知识恶魔；连续完成 `10` 项差分；思考额外验证攻击后治疗 `30` 及力量增加，随后验证实时战损复核按敌方回合语义消费知识恶魔诅咒计划。最新 runId `901066cdbaaa41329876325cd8a06ad5` | 2026-08-30 |
| `MONSTER-MOVES-BATCH-008` | 通过 | 单次进入 `LivingFogNormal`，召唤乐加维林族母和两种树叶史莱姆；连续完成 `9` 项差分；验证负数力量/敏捷、族母格挡和弃牌堆黏液 `0 → 2 → 3` | 2026-08-20 |
| `MONSTER-MOVES-BATCH-009` | 通过 | 单次进入 `LivingFogNormal`，召唤活体盾、蛮兽、异螨、小啃兽和啃咬机；连续完成 `13` 项差分；验证多段攻击、格挡、力量、易伤及手牌/弃牌堆状态牌 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-010` | 通过 | 单次进入 `LivingFogNormal`，在五个空槽位召唤五类怪物；连续完成 `16` 项差分；验证攻击、格挡、力量、脆弱、手牌灼傷和条件初始状态机 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-011` | 通过 | 单次进入 `LivingFogNormal`，在五个空槽位召唤五类怪物；连续完成 `13` 项差分；修复并验证扭动同时生成感染与力量 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-012` | 通过 | 单次进入 `LivingFogNormal`，在五个空槽位召唤五类怪物；同一场战斗连续完成 `11` 项差分；覆盖攻击、虚弱、脆弱、易伤和正负力量 | 2026-08-20 |
| `UNATTENDED-PROCESS-REUSE-001` | 通过 | 同一 PID `35048` 先执行第 012 批 `11` 项差分，再从主菜单接收巨斧机器人差分；日志依次为 `process_sequence=1/2`，第二批 `reused_process=True`，最后一批才退出 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-013` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `9` 项差分；验证攻击、格挡、仪式、易伤、力量累计和弃牌堆晕眩 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-014` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `10` 项差分；验证多段攻击、格挡、力量、脆弱及荆棘的增加和移除 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-015` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `16` 项差分；验证多段攻击、力量、十张黏液覆体及虚弱/易伤累计 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-016` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `13` 项差分；验证隐藏醒来行动、攻击段数、一张黏液覆体和装弹力量 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-017` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `14` 项差分；验证三代对手的弹幕力量、蟾蜍蝌蚪荆棘增减和藤蔓蹒跚者攻击 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-018` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `15` 项差分；验证入场力量后的攻击、力量累计、格挡以及弃牌堆感染和伤口 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-019` | 通过 | 单次进入 `LivingFogNormal`，先召唤女王依赖怪物，再连续完成 `14` 项差分；新增全场敌方 Power 比较，验证动态敏捷伤害、状态移除和女王群体增益 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-020` | 通过 | 单次进入 `LivingFogNormal`，四类怪物连续完成 `13` 项差分；验证累计力量、格挡、脆弱、埋地，并审计五个实例布尔字段只影响表现 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-021` | 通过 | 同一 PID 先在原生凯撒蟹 Boss 战完成双臂 `10` 项差分，再复用进程进入 `LivingFogNormal` 完成追踪手/噪音机器人 `3` 项；最后才退出游戏 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-022` | 通过 | 单次进入 `LivingFogNormal`，同一只灵魂异鱼按固定顺序完成 `5` 项差分；验证抽牌堆/弃牌堆“呼喚”累计、无实体和易伤 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-023` | 通过 | 单次进入原生 `BowlbugsWeak`，同一只盛碗虫（石）连续验证完全格挡触发 `STUNNED`、昏头转向后恢复头槌、部分格挡不触发 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-024` | 通过 | 单次进入 `LivingFogNormal` 完成 `6` 项连续差分；验证骇鳗活力获得/下一击消费，以及胧光怪全队力量进入三只怪物的后续伤害 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-026` | 通过 | 单次进入 `LivingFogNormal`，同一只永世沙漏连续两次执行“加大力度”；验证力量 `3 → 7`、弃牌堆凋萎 `1 → 2`、伤害总和 `6 → 18`，以及模拟计数器与生成牌等级一致 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-027` | 通过 | 单次进入 `LivingFogNormal`，同一只蛇行扼杀者连续两次执行“缠身”；验证 `3` 层紧缠在玩家回合结束造成 `3 HP`，再次施加后随施加者死亡完整移除 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-028` | 通过 | 同一可见游戏 PID 连续执行两场：第一场两只幽灵骑士验证现有牌/新牌受咒、后施加者死亡不清除、初始施加者死亡才清除；第二场复用进程验证完整回合结束时受咒手牌因虚无进入消耗堆 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-029` | 通过 | 同一 PID `8240` 连续执行五场、共 `9` 条差分；验证缩小/人工制品、缠结附魔与费用、昏眩每回合首张牌限制、无实体伤害上限及 `2 → 1 → 0` 生命周期 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-030` | 通过 | 同一 PID `13628` 连续执行五场、共 `11` 条差分；验证力量/虚弱/易伤伤害、敏捷/脆弱/不可格挡格挡、中毒与催化剂、残影/覆甲生命周期、双倍伤害及缓慢累计清零 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-031` | 通过 | 同一 PID `42532` 连续执行三场、共 `11` 条差分；关闭 `22` 个目录条目，验证下回合能量/抽牌/格挡、禁止抽牌/回能、费用/伤害/格挡修正、保留手牌时序和一次性 Power 生命周期 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-032` | 通过 | 同一 PID `41596` 连续执行六场；关闭 `22` 个持续 Power 生命周期条目，验证抽牌、能量、辉星、Orb、全场目标、生成牌、仪式延迟，以及愤怒复制与活力消费回归 | 2026-08-20 |

运行命令按平台分列。两组命令覆盖同一批场景和断言：Windows 使用仓库保留的 PowerShell 启动器及 PascalCase 参数；Linux 使用原生 Bash 启动器及 GNU kebab-case 参数。修改场景时必须同步更新两组命令，并保持 `ScenarioId / --scenario-id` 集合一致。当前每端各有 `246` 条命令、`245` 个唯一 ScenarioId（`PERFORMANCE-PRESET-HIGH-172` 作为不同历史门禁重复一次）；未提供本机问题包时，矩阵只跳过 `CHOICES-PARADOX-SCROLLS-0160`、`QUEEN-CHAINS-REUSE-FINAL-085` 和 `CORPSE-SLUGS-USER-RUN-073` 这 `3` 个外部快照场景。2026-09-01 的最终 Linux 执行中，唯一非零项发生在游戏内结果已写为 Passed 后的进程身份退出确认；相同命令独立复验返回 `0`，详见顶部未发布门禁表。

全量运行优先使用两端等价的 `tools/run-headless-matrix.ps1` / `tools/run-headless-matrix.sh`：

```text
pwsh -NoProfile -File tools\run-headless-matrix.ps1 -ContinueOnFailure
./tools/run-headless-matrix.sh --continue-on-failure
```

矩阵启动首项前会先精确清理上一次命令留下的 managed marker 进程，再保留下方每条命令声明的 `KeepGameOpen / ExitOnComplete`：同一文档组内，每条都必须收到匹配的静稳 ready ACK 才能复用进程；跨组边界则启动新进程；带退出边界的命令即使因缺少外部夹具而跳过，也会显式清理已有进程。探索性地删除全部边界会越过游戏资源缓存已经验证的安全域，因此不作为可选发布模式。失败或中断时矩阵先清理已认领进程；`ContinueOnFailure / --continue-on-failure` 只决定清理后是否继续下一条，不会重试并掩盖失败。

### Windows（PowerShell 7）

本机问题包不进入仓库：运行 `CHOICES-PARADOX-SCROLLS-0160` 前必须设置 `$env:CHOICES_PARADOX_RUN_SNAPSHOT_PATH` 和 `$env:CHOICES_PARADOX_PROGRESS_SNAPSHOT_PATH`；运行 `QUEEN-CHAINS-REUSE-FINAL-085` 或 `CORPSE-SLUGS-USER-RUN-073` 前必须设置 `$env:RUN_SNAPSHOT_PATH`。

```powershell
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId FIX-141700-TURN-START-CHOICE-FIXED -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 999 -ClearPlayerPiles -CardsPath coverage\unattended\turn-start-choice-once-141700-cards.json -CombatRelicsPath coverage\unattended\turn-start-choice-once-141700-relics.json -PowersPath coverage\unattended\turn-start-choice-once-141700-powers.json -InitialPlayerEnergy 0 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 3000 -SearchMaxDegreeOfParallelismForTest 1 -ExpectedInitialTurnStartChoiceTurn 2 -ExpectedInitialTurnStartChoiceSourceId ENTROPY_POWER -ExpectedInitialTurnStartChoiceCardId STRIKE_IRONCLAD -ExpectedReusedTurn 2 -ExpectedUnexpectedReplansAtMost 0 -StopAfterExpectedReuse -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-SILENT-LARGE-DECK-5S -CharacterId SILENT -Seed SEARCH_PERF_SILENT_LARGE_DECK -EncounterId AEONGLASS_BOSS -Ascension 5 -ActIndexForTest 2 -EnemyCurrentHp 512 -InitialEnemyMoveIdsJson '["EBB_MOVE"]' -InitialPlayerHp 65 -InitialPlayerMaxHp 65 -InitialPlayerEnergy 3 -ClearPlayerPiles -CardsPath coverage\unattended\search-performance-silent-large-deck-cards.json -PerformancePresetForTest VeryHigh -PotionPolicyForTest Smart -SearchMaxDegreeOfParallelismForTest 8 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 5000 -MeasureSearchPhases -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-NECROBINDER-POTION-QUICK -CharacterId NECROBINDER -Seed SEARCH_PERF_NECROBINDER_POTION -RunSnapshotPath coverage\unattended\search-performance-necrobinder-potion-heavy-run-snapshot.json -EncounterId AEONGLASS_BOSS -Ascension 10 -ActIndexForTest 2 -EnemyCurrentHp 526 -InitialPlayerHp 41 -CardsJson '[]' -SearchMaxDegreeOfParallelismForTest 8 -PerformancePresetForTest VeryHigh -PotionPolicyForTest RequireAtLeastOne -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 5000 -MeasureSearchPhases -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-COMPLEX-RANDOM-KNIGHTS-CANONICAL-DOP1-2S -CharacterId IRONCLAD -Seed SEARCH_PERF_COMPLEX_RANDOM_KNIGHTS -EncounterId KNIGHTS_ELITE -Ascension 10 -ActIndexForTest 2 -InitialEnemyCurrentHpsJson '[108,97,89]' -InitialEnemyMoveIdsJson '["RAM_MOVE","HEX","POWER_SHIELD_MOVE"]' -InitialPlayerHp 80 -InitialPlayerMaxHp 80 -InitialPlayerEnergy 5 -ClearRunDeck -ClearPlayerPiles -CardsPath coverage\unattended\search-performance-complex-random-knights-cards.json -PerformancePresetForTest Low -PotionPolicyForTest Smart -SearchMaxDegreeOfParallelismForTest 1 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 2000 -MeasureSearchPhases -EnableDetailedDiagnosticLogsForTest 0 -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-COMPLEX-RANDOM-QUEEN-CANONICAL-DOP1-2S -CharacterId REGENT -Seed SEARCH_PERF_COMPLEX_RANDOM_QUEEN -EncounterId QUEEN_BOSS -Ascension 10 -ActIndexForTest 2 -InitialEnemyCurrentHpsJson '[211,419]' -InitialEnemyMoveIdsJson '["STRONG_TACKLE_MOVE","PUPPET_STRINGS_MOVE"]' -InitialPlayerHp 80 -InitialPlayerMaxHp 80 -InitialPlayerEnergy 5 -InitialPlayerStars 3 -ClearRunDeck -ClearPlayerPiles -CardsPath coverage\unattended\search-performance-complex-random-queen-cards.json -PowersPath coverage\unattended\search-performance-complex-random-queen-powers.json -PerformancePresetForTest Low -PotionPolicyForTest Smart -SearchMaxDegreeOfParallelismForTest 1 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 2000 -MeasureSearchPhases -EnableDetailedDiagnosticLogsForTest 0 -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-COMPLEX-RANDOM-TEST-SUBJECT-CANONICAL-DOP1-2S -CharacterId DEFECT -Seed SEARCH_PERF_COMPLEX_RANDOM_TEST_SUBJECT -EncounterId TEST_SUBJECT_BOSS -Ascension 10 -ActIndexForTest 2 -MarkEncounterAsSecondBossForTest -InitialEnemyCurrentHpsJson '[111]' -InitialEnemyMoveIdsJson '["BITE_MOVE"]' -InitialPlayerHp 80 -InitialPlayerMaxHp 80 -InitialPlayerEnergy 5 -ClearRunDeck -ClearPlayerPiles -CardsPath coverage\unattended\search-performance-complex-random-test-subject-cards.json -PowersPath coverage\unattended\search-performance-complex-random-test-subject-powers.json -PerformancePresetForTest Low -PotionPolicyForTest Smart -SearchMaxDegreeOfParallelismForTest 1 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 2000 -MeasureSearchPhases -EnableDetailedDiagnosticLogsForTest 0 -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-COMPLEX-RANDOM-AEONGLASS-DOP1-2S -CharacterId NECROBINDER -Seed SEARCH_PERF_COMPLEX_RANDOM_AEONGLASS -EncounterId AEONGLASS_BOSS -Ascension 10 -ActIndexForTest 2 -EnemyCurrentHp 526 -InitialEnemyMoveIdsJson '["EBB_MOVE"]' -InitialPlayerHp 80 -InitialPlayerMaxHp 80 -InitialPlayerEnergy 5 -ClearRunDeck -ClearPlayerPiles -CardsPath coverage\unattended\search-performance-complex-random-aeonglass-cards.json -PowersPath coverage\unattended\search-performance-complex-random-aeonglass-powers.json -PerformancePresetForTest Low -PotionPolicyForTest Smart -SearchMaxDegreeOfParallelismForTest 1 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 2000 -MeasureSearchPhases -EnableDetailedDiagnosticLogsForTest 0 -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId BATCH-164623-TRANSFORM-ENTERED-COMBAT-CONTINUATION -CharacterId IRONCLAD -EncounterId LivingFogNormal -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\batch-164623-transform-entered-combat.json -VerifyIncrementalSearch -KeepGameOpen -TimeoutSeconds 180
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId BATCH-164623-TURN-SCOPED-CARD-HISTORY-CONTINUATION-FINAL -CharacterId IRONCLAD -EncounterId LivingFogNormal -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\batch-164623-turn-scoped-card-history.json -VerifyIncrementalSearch -KeepGameOpen -TimeoutSeconds 180
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId BATCH-164623-ORB-DEATH-POWER-ORDER-CONTINUATION -CharacterId DEFECT -EncounterId AXEBOTS_NORMAL -InitialEnemyCurrentHpsJson '[5,50]' -MonsterMoveChecksPath coverage\unattended\batch-164623-orb-death-power-order.json -VerifyIncrementalSearch -ExitOnComplete -TimeoutSeconds 180
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId POST0201-SCRAPE-NEGATIVE-COST-FINAL -CharacterId DEFECT -EncounterId LivingFogNormal -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\scrape-negative-cost-discard-0201.json -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId POST0201-WHISTLE-STUN-FOLLOW-UP-FINAL -CharacterId DEFECT -EncounterId THE_INSATIABLE_BOSS -EnemyCurrentHp 281 -MonsterMoveChecksPath coverage\unattended\whistle-stun-follow-up-0201.json -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId POST0201-BRAND-POST-CHOICE-POWER-FORK-FINAL -CharacterId IRONCLAD -EncounterId LivingFogNormal -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\brand-post-choice-power-fork-0201.json -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId QOL-CONTROLLER-STOP-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SETTINGS-TABS-LIFECYCLE-NEXT -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId UPLOAD-PROGRESS-CANCEL-CONFIRMATION-NEXT-FINAL -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId UPLOAD-DIRECT-STATE-OWNERSHIP-NEXT -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId UPLOAD-PANEL-MAILBOX-LIFECYCLE-NEXT -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-LOW-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest Low -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-MEDIUM-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest Medium -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-HIGH-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest High -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-VERY-HIGH-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest VeryHigh -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CHOICES-PARADOX-SCROLLS-0160 -CharacterId SILENT -Seed YS41WKT7ZUXS -RunSnapshotPath "$env:CHOICES_PARADOX_RUN_SNAPSHOT_PATH" -ProgressSnapshotPath "$env:CHOICES_PARADOX_PROGRESS_SNAPSHOT_PATH" -EncounterId SCROLLS_OF_BITING_NORMAL -Ascension 10 -ActIndexForTest 2 -InitialPlayerHp 60 -InitialEnemyCurrentHpsJson '[33,37,38,36]' -InitialEnemyMoveIdsJson '["CHEW","MORE_TEETH","CHOMP","MORE_TEETH"]' -ReloadRunRngAfterStateInjection -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 8000 -ExpectedInitialSetupChoiceCountAtLeast 1 -ExpectedInitialSetupChoiceSourceId CHOICES_PARADOX -ExpectedInitialSetupChoiceTextStartsWith '选择悖论：选择 ' -ExpectedInitialChoiceBranchesEvaluatedAtLeast 5 -StopAfterInitialSetupAssertion -TimeoutSeconds 150 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RINGING-HAVOC-AUTOPLAY-0160-FINAL -CharacterId IRONCLAD -EncounterId LivingFogNormal -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\ringing-havoc-autoplay-0160.json -VerifyIncrementalSearch -ExitOnComplete -TimeoutSeconds 120
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId HEADBUTT-EMPTY-DISCARD-0160 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 50 -ClearPlayerPiles -CardsPath coverage\unattended\headbutt-empty-discard-0160-cards.json -ExpectedPlayedCardId HEADBUTT -ExpectedReusedTurn 2 -StopAfterExpectedReuse -ExpectedUnexpectedReplansAtMost 0 -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId COSMIC-INDIFFERENCE-EMPTY-DISCARD-0160 -CharacterId REGENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 50 -ClearPlayerPiles -CardsPath coverage\unattended\cosmic-indifference-empty-discard-0160-cards.json -ExpectedPlayedCardId COSMIC_INDIFFERENCE -ExpectedReusedTurn 2 -StopAfterExpectedReuse -ExpectedUnexpectedReplansAtMost 0 -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -ExitOnComplete -TimeoutSeconds 120
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId FAIRY-AUTOMATIC-RESCUE-FINAL2-0150 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 57 -InitialPlayerHp 1 -PotionId FairyInABottle -ClearPlayerPiles -CardsPath coverage\unattended\fairy-automatic-rescue-0150-cards.json -InitialEnemyMoveIdsJson '["FIRST_ACID_GOOP"]' -ExpectedInitialOnlyDeathRoutesFound 0 -ExpectedInitialCombatEndedTurn 2 -ExpectedInitialPotionCount 1 -ExpectedUsedPotionId FAIRY_IN_A_BOTTLE -ExpectedFinishedTurn 2 -ExpectedUnexpectedReplansAtMost 0 -VerifyIncrementalSearch -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 180 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId INSATIABLE-PARETO-CONTROL-150 -CharacterId SILENT -Seed 2DJ8M7EAKQUS -EncounterId THE_INSATIABLE_BOSS -EnemyCurrentHp 341 -InitialPlayerHp 24 -InitialPlayerMaxHp 57 -InitialPlayerEnergy 4 -InitialEnemyMoveIdsJson '["LIQUIFY_GROUND_MOVE"]' -ClearPlayerPiles -CardsPath coverage\unattended\insatiable-malaise-control-150-cards.json -PerformancePresetForTest High -ForceShortSearchOnly -ExpectedInitialHpLostAtMost 0 -ExpectedInitialProjectedBattleHpLostAtMost 7 -ExpectedInitialSoldHp 0 -ExpectedInitialPotionCount 0 -ExpectedInitialSearchedTurnsAtLeast 7 -ExpectedInitialDeathTurnAtLeast 7 -ExpectedInitialFinalEnemyHpAtMost 204 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 180 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId BUILTIN-LISTENER-IDENTITY-533 -CharacterId SILENT -Seed BJCZX3J13PZJ -RunSnapshotPath coverage\unattended\solver-longline-run-snapshot.json -EncounterId NIBBITS_NORMAL -EnemyCurrentHp 999 -InitialPlayerHp 35 -PotionId WeakPotion -ExpectedInitialPotionCount 0 -ExpectedInitialHpLostAtMost 0 -ExpectedInitialProjectedBattleHpLostAtMost 0 -ExpectedInitialShufflesCrossedAtLeast 2 -ExpectedUnexpectedReplansAtMost 0 -ExpectedFinishedTurn 5 -VerifyCombatRootSnapshot -VerifyIncrementalSearch -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MODIFIER-IDENTITY-532 -EncounterId LivingFogNormal -ModifierId MURDEROUS -MonsterMoveChecksPath coverage\unattended\murderous-fabricator-spawn-532.json -ExitOnComplete
pwsh -NoProfile -File tools\run-visible-steam-benchmark.ps1 -TimeoutSeconds 360
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId LAGAVULIN-SLEEP-REUSE-176 -CharacterId REGENT -EncounterId LAGAVULIN_MATRIARCH_BOSS -EnemyCurrentHp 233 -ClearPlayerPiles -CardsJson '[{"cardId":"DEFEND_REGENT","pile":"Hand","count":1}]' -InitialEnemyMoveIdsJson '["SLEEP_MOVE"]' -ExpectedReusedTurn 2 -StopAfterExpectedReuse -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId BEAT-INTO-SHAPE-PLAYABLE-175 -CharacterId REGENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -ClearPlayerPiles -CardsJson '[{"cardId":"BEAT_INTO_SHAPE","pile":"Hand","count":1}]' -ExpectedPlayedCardId BEAT_INTO_SHAPE -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-LOW-171 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest Low -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-HIGH-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest High -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-CUSTOM-173 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest Custom -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId INFESTED-PRISM-VITAL-SPARK-152 -CharacterId REGENT -EncounterId INFESTED_PRISMS_ELITE -EnemyCurrentHp 171 -MonsterMoveChecksPath coverage\unattended\infested-prism-vital-spark-152.json -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId KNOWLEDGE-DEMON-SEARCH-CHOICE-162 -CharacterId REGENT -EncounterId KNOWLEDGE_DEMON_BOSS -EnemyCurrentHp 399 -ExpectedInitialChoiceBranchesEvaluatedAtLeast 2 -ExpectedInitialPlannedChoiceCardId MIND_ROT -ExpectedInitialActEndingBoss 1 -ExpectedObservedPlayerPowerId MIND_ROT_POWER -StopAfterExpectedPlayerPower -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId KNOWLEDGE-LIVE-END-RISK-INCREMENTAL -CharacterId REGENT -EncounterId KNOWLEDGE_DEMON_BOSS -EnemyCurrentHp 399 -ExpectedInitialChoiceBranchesEvaluatedAtLeast 2 -ExpectedInitialPlannedChoiceCardId MIND_ROT -ExpectedInitialActEndingBoss 1 -ExpectedObservedPlayerPowerId MIND_ROT_POWER -StopAfterExpectedPlayerPower -EnableStopOnWorseRecalculationForTest -ExpectedUnexpectedReplansAtMost 0 -VerifyIncrementalSearch -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId DEATH-TURN-PAUSE-165 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 57 -InitialPlayerHp 1 -ClearPlayerPiles -CardsJson '[]' -InitialEnemyMoveIdsJson '["FIRST_ACID_GOOP"]' -ExpectedInitialOnlyDeathRoutesFound 1 -ExpectedInitialDeathTurn 1 -ExpectedFullAutoPausedAtDeathTurn -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SCULPTING-STRIKE-CHOICE-151 -CharacterId NECROBINDER -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 50 -ClearPlayerPiles -CardsPath coverage\unattended\sculpting-strike-choice-151-cards.json -ExpectedPlayedCardId SCULPTING_STRIKE -ExpectedReusedTurn 2 -StopAfterExpectedReuse -VerifyIncrementalSearch -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId ARMAMENTS-IMPLICIT-UPGRADE-OBSERVATION-534 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 9 -ClearPlayerPiles -CardsJson '[{"cardId":"ARMAMENTS","pile":"Hand","count":1},{"cardId":"STRIKE_IRONCLAD","pile":"Hand","count":1}]' -ExpectedPlayedCardId ARMAMENTS -ExpectedFinishedTurn 1 -ExpectedUnexpectedReplansAtMost 0 -VerifyIncrementalSearch -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId COSMIC-INDIFFERENCE-IMPLICIT-151B -CharacterId REGENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 50 -ClearPlayerPiles -CardsPath coverage\unattended\cosmic-indifference-choice-151b-cards.json -ExpectedPlayedCardId COSMIC_INDIFFERENCE -ExpectedReusedTurn 2 -StopAfterExpectedReuse -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId DUPLICATE-POTION-SEARCH-147 -CharacterId REGENT -EncounterId QUEEN_BOSS -EnemyCurrentHp 1 -PotionsPath coverage\unattended\duplicate-potions-147.json -ExpectedInitialExecutableActionCountAtLeast 1 -ExpectedFinishedTurnAtMost 5 -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PEN-NIB-ROUTE-148 -CharacterId IRONCLAD -EncounterId MECHA_KNIGHT_ELITE -EnemyCurrentHp 70 -ClearPlayerPiles -CardsPath coverage\unattended\pen-nib-route-148-cards.json -RelicsPath coverage\unattended\pen-nib-route-148-relics.json -VerifyIncrementalSearch -ExpectedInitialExecutableActionCountAtLeast 2 -ExpectedInitialRelicEffectId PEN_NIB -ExpectedInitialRelicEffectSummary '×2' -ExpectedInitialHpLostAtMost 0 -ExpectedInitialProjectedBattleHpLostAtMost 0 -ExpectedInitialOnlyDeathRoutesFound 0 -ExpectedInitialCombatEndedTurn 1 -ExpectedFinishedTurn 1 -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0.05 -AssertDeploymentSpeedRestored -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CENTENNIAL-PUZZLE-STATE-149 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 57 -CombatRelicsPath coverage\unattended\centennial-puzzle-state-149-relics.json -MonsterMoveChecksPath coverage\unattended\centennial-puzzle-state-149.json -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId JOSS-PAPER-STATE-150 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\joss-paper-state-150-relics.json -MonsterMoveChecksPath coverage\unattended\joss-paper-state-150.json -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId ONLY-DEATH-ROUTES-150 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 57 -InitialPlayerHp 1 -ClearPlayerPiles -CardsJson '[]' -InitialEnemyMoveIdsJson '["FIRST_ACID_GOOP"]' -ExpectedInitialOnlyDeathRoutesFound 1 -ExpectedPlayerDeath -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERF-END-TURN-CLEANUP-FINAL-099 -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\performance-end-turn-cleanup-098.json -TimeoutSeconds 180 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERF-DAMAGE-PIPELINE-100 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-damage.json -TimeoutSeconds 240 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId NIBBITS-UNIFIED30-SOLD-CAP-084 -CharacterId SILENT -Seed BJCZX3J13PZJ -RunSnapshotPath coverage\unattended\solver-longline-run-snapshot.json -EncounterId NIBBITS_NORMAL -EnemyCurrentHp 999 -InitialPlayerHp 35 -PotionId WeakPotion -ExpectedInitialPotionCount 0 -ExpectedInitialProjectedBattleHpLostAtMost 0 -ExpectedInitialSoldHp 0 -ExpectedInitialSoldHpBranchesPrunedAtLeast 1 -ExpectedReusedTurn 3 -ExpectedFinishedTurnAtMost 8 -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId QUEEN-CHAINS-REUSE-FINAL-085 -CharacterId IRONCLAD -Seed Y883BRPFJZ05 -RunSnapshotPath "$env:RUN_SNAPSHOT_PATH" -EncounterId QUEEN_BOSS -EnemyCurrentHp 70 -InitialPlayerHp 80 -InitialEnemyMoveIdsJson '["","PUPPET_STRINGS_MOVE"]' -ExpectedReusedTurn 3 -StopAfterExpectedReuse -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CORPSE-SLUGS-USER-RUN-073 -CharacterId IRONCLAD -Seed Y883BRPFJZ05 -RunSnapshotPath "$env:RUN_SNAPSHOT_PATH" -EncounterId CORPSE_SLUGS_WEAK -EnemyCurrentHp 999 -InitialPlayerHp 80 -ExpectedFinishedTurnAtMost 20 -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MECHA-MEMORY-FULL-AUTO-FINAL-070 -CharacterId SILENT -Seed BJCZX3J13PZJ -RunSnapshotPath coverage\unattended\mecha-knight-memory-run-snapshot.json -EncounterId MECHA_KNIGHT_ELITE -EnemyCurrentHp 300 -InitialPlayerHp 65 -ShortSearchBudgetOverrideMilliseconds 5000 -DeepSearchBudgetOverrideMilliseconds 60000 -ExpectedInitialSearchPhase Deep -ExpectedInitialDeepSearchTriggered 1 -ExpectedInitialTotalElapsedMillisecondsAtMost 25000 -ExpectedInitialTotalAllocatedBytesAtMost 4300000000 -ExpectedInitialGen2CollectionsAtMost 6 -ExpectedInitialTotalGcPauseMillisecondsAtMost 8000 -ExpectedInitialMaxGcPauseMillisecondsAtMost 50 -ExpectedInitialMaxMainThreadFrameGapMillisecondsAtMost 100 -ExpectedReusedTurn 3 -ExpectedFinishedTurnAtMost 9 -MeasureSearchPhases -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SOLVER-ROUTE-POLICY-060 -CharacterId SILENT -Seed BJCZX3J13PZJ -RunSnapshotPath coverage\unattended\solver-longline-run-snapshot.json -EncounterId NIBBITS_NORMAL -EnemyCurrentHp 999 -InitialPlayerHp 35 -PotionId WeakPotion -ExpectedInitialPotionCount 0 -ExpectedInitialProjectedBattleHpLostAtMost 0 -ExpectedReusedTurn 3 -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SOLD-HP-POLICY-BATCH-059-DEFENSE-CHOICE -CharacterId IRONCLAD -EncounterId NIBBITS_WEAK -EnemyCurrentHp 43 -CardsPath coverage\unattended\sold-hp-policy-batch-059-defense-choice.json -ClearPlayerPiles -ExpectedInitialSoldHpAtMost 5 -ExpectedInitialSoldHpBranchesPrunedAtLeast 1 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SOLD-HP-POLICY-BATCH-059-UNAVOIDABLE -CharacterId IRONCLAD -EncounterId NIBBITS_WEAK -EnemyCurrentHp 43 -CardsPath coverage\unattended\sold-hp-policy-batch-059-unavoidable.json -ClearPlayerPiles -ExpectedInitialSoldHp 0 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SOLD-HP-POLICY-BATCH-059-STABLE-NO-SALE -CharacterId IRONCLAD -EncounterId SLIMES_WEAK -EnemyCurrentHp 999 -CardsPath coverage\unattended\sold-hp-policy-batch-059-active-sale.json -ClearPlayerPiles -ExpectedInitialSoldHp 0 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-POWER-BATCH-058 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-power-batch-058-relics.json -MonsterMoveChecksPath coverage\unattended\relic-power-batch-058.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-POWER-TEMPORARY-BATCH-058 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-power-batch-058-temporary-relics.json -MonsterMoveChecksPath coverage\unattended\relic-power-batch-058-temporary.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-POTION-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-potion-relics.json -PotionCheckPath coverage\unattended\relic-reactive-batch-057-potion.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-TURNS-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-turns-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-turns.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-TURN-END-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -RelicsPath coverage\unattended\relic-reactive-batch-057-turn-end-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-turn-end.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-KUSARIGAMA-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-kusarigama-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-kusarigama.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-STARS-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-stars-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-stars.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-EMOTION-FINAL -CharacterId DEFECT -EnemyCurrentHp 999 -OrbsJson '[{"orbId":"LIGHTNING_ORB","count":1}]' -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-emotion-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-emotion.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-TOP-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-top-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-top.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-UNDYING-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-undying-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-undying.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-PAELS-EYE-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-paels-eye-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-boundary.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-HISTORY-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-history-course-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-boundary.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-TOASTY-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-toasty-mittens-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-boundary.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-DETERMINISTIC-056 -CharacterId DEFECT -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-deterministic-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-deterministic-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-OSTY-056 -CharacterId NECROBINDER -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-osty-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-osty-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-CYCLES-056 -CharacterId REGENT -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-cycles-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-cycles-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-ATTACKS-056 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-attacks-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-attacks-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-LETTER-056 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-letter-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-letter-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-LEGION-056 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-legion-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-legion-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-GENERATION-056 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-generation-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-generation-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-DAMAGE-056 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-damage-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-damage-checks.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-START-FIRST-055 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-start-batch-055-first-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-start-batch-055-first-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-START-CYCLES-055 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-start-batch-055-cycles-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-start-batch-055-cycles-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-START-TEA-055 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-start-batch-055-tea-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-start-batch-055-tea-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-START-CORE-055 -CharacterId DEFECT -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-start-batch-055-core-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-start-batch-055-core-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-START-CONCH-055 -CharacterId IRONCLAD -EncounterId KnightsElite -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-start-batch-055-conch-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-start-batch-055-conch-checks.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-PERSISTENT-054 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-persistent-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-persistent-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-RUNIC-PYRAMID-054 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-runic-pyramid-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-runic-pyramid-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-INFUSED-CORE-054 -CharacterId DEFECT -EnemyCurrentHp 999 -RelicsPath coverage\unattended\relic-infused-core-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-infused-core-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-DAMAGE-054 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-damage-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-damage-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-BOOT-054 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-boot-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-boot-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TUNGSTEN-054 -EnemyCurrentHp 999 -AdditionalMonsterId BowlbugRock -CombatRelicsPath coverage\unattended\relic-tungsten-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-tungsten-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-VITRUVIAN-054 -CharacterId NECROBINDER -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-vitruvian-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-vitruvian-batch-054-checks.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-DRAW-CYCLES-053 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-draw-state-batch-053-cycles-relics.json -MonsterMoveChecksPath coverage\unattended\relic-draw-state-batch-053-cycles-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-POCKETWATCH-053 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-draw-state-batch-053-pocketwatch-relics.json -MonsterMoveChecksPath coverage\unattended\relic-draw-state-batch-053-pocketwatch-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-FIRST-TURN-SNAPSHOT-053 -EnemyCurrentHp 999 -RelicsPath coverage\unattended\relic-draw-state-batch-053-first-turn-relics.json -MonsterMoveChecksPath coverage\unattended\relic-draw-state-batch-053-first-turn-checks.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-PURE-DRAW-052 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-pure-batch-052-draw-relics.json -MonsterMoveChecksPath coverage\unattended\relic-pure-batch-052-draw-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-PURE-ENERGY-052 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-pure-batch-052-energy-relics.json -MonsterMoveChecksPath coverage\unattended\relic-pure-batch-052-energy-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-CONDITIONS-052 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-pure-batch-052-turn-relics.json -MonsterMoveChecksPath coverage\unattended\relic-pure-batch-052-turn-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-BOOMING-CONCH-052 -EncounterId KnightsElite -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-pure-batch-052-booming-relics.json -MonsterMoveChecksPath coverage\unattended\relic-pure-batch-052-booming-checks.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-OSTY -CharacterId NECROBINDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-osty.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-DIRGE -CharacterId NECROBINDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-dirge.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-CHOICES -CharacterId NECROBINDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-choices.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-AUTOPLAY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-autoplay.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-POWERS -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-powers.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-NORMALITY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-normality.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-ENTHRALLED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-enthralled.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-RETURN-AUTOPLAY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-return-autoplay.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-UPGRADED -CharacterId NECROBINDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-NIGHTMARE-LIFECYCLE -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-nightmare-lifecycle.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-VOID-FORM -CharacterId IRONCLAD -EnemyCurrentHp 18 -CardId VOID_FORM -ClearPlayerHand -ExpectedPlayedCardId VOID_FORM -ExpectedFinishedTurn 2 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-BOUNCING-FLASK -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-bouncing-flask.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-DIRECT-A -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-direct-a.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-OUTBREAK -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-outbreak.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-ECHOING-SLASH -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-echoing-slash.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-OMNISLICE -CharacterId IRONCLAD -EnemyCurrentHp 999 -AdditionalMonsterId CalcifiedCultist -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-omnislice.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-TRANSFORM -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-transform.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-HAND -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-hand.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-STATE -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-state.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-PILES -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-piles.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-TRANSFORM-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-transform-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-PURITY-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-purity-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-ZERO-OPTIONAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-zero-optional.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-HIDDEN-DAGGERS-EMPTY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-hidden-daggers-empty.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-BRAND-EMPTY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-brand-empty.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-SCAVENGE-EMPTY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-scavenge-empty.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-FRANTIC-ESCAPE -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-frantic-escape.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-ECHOING-SLASH-KILL -CharacterId IRONCLAD -EnemyCurrentHp 999 -AdditionalMonsterId CalcifiedCultist -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-echoing-slash-kill.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-DIRECT-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-direct-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-END-OF-DAYS-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -AdditionalMonsterId CalcifiedCultist -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-end-of-days-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-END-OF-DAYS -CharacterId IRONCLAD -EnemyCurrentHp 999 -AdditionalMonsterId CalcifiedCultist -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-end-of-days.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-POWER-A -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-power-set-a.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-POWER-B -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-power-set-b.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-SYNCHRONIZE -CharacterId DEFECT -EnemyCurrentHp 999 -OrbsJson '[{"orbId":"LIGHTNING_ORB","count":1},{"orbId":"FROST_ORB","count":1}]' -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-synchronize.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-TURBO-SLEEVE -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-turbo-up-my-sleeve.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-SUMMON-FORTH -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-summon-forth.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-SHADOW-STEP -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-shadow-step.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-POWER-A -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-power-set-a.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-POWER-B -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-power-set-b.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-PALE-BLUE-DOT -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-pale-blue-dot.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-RESOURCES -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-resources-and-targets.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-SEEKING-EDGE -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-seeking-edge.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-SIGNAL-BOOST -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-signal-boost.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-POWER -CharacterId NECROBINDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-power-set.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-TARGET -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-target-effects.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-TARGET-LIFECYCLE -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-target-lifecycle.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-RESOURCES -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-resources.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-SHIVS -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-shivs.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-INK -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-blade-of-ink.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-APOTHEOSIS -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-apotheosis.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-ENLIGHTENMENT -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-enlightenment.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-ENLIGHTENMENT-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-enlightenment-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-STORM -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-storm-of-steel.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-HOTFIX-LIFECYCLE -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-hotfix-lifecycle.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-EXPOSE-ARTIFACT -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-expose-artifact.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-HAZE-MULTI -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-haze-multi.json -AdditionalMonsterId DampCultist -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-A -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-power-set-a.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-B -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-power-set-b.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-DANSE -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-danse-macabre.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-PLANNER -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-master-planner.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-SERPENT -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-serpent-form.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-STORM -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-storm.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-NO-ESCAPE -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-no-escape.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-POWER-A -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-power-set-a.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-POWER-B -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-power-set-b.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-SPECIAL -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-special-effects.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-BULLET-TIME -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-bullet-time.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-FERAL-HISTORY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-feral-history.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-JUGGLING-HISTORY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-juggling-history.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-036-A -CharacterId DEFECT -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-036-power-set-a.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-036-B -CharacterId DEFECT -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-036-power-set-b.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-036-ANTICIPATE -CharacterId DEFECT -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-036-anticipate-lifecycle.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-035-SELF -CharacterId IRONCLAD -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-035-self-powers.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-035-TARGET -CharacterId IRONCLAD -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-035-target-powers.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-035-BULK-UP -CharacterId DEFECT -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-035-bulk-up.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-035-REGENT -CharacterId REGENT -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-035-regent.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-035-PIERCING-WAIL -CharacterId IRONCLAD -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-035-piercing-wail-lifecycle.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-ACCURACY -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-accuracy.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-BLOCK -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-block.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-COST-LOCATION -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-cost-location.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-HANG -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-hang.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-HARD-TO-KILL -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-hard-to-kill.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-LEADERSHIP -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-leadership.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-LETHALITY -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-lethality.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-ONE-FOR-ALL -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-one-for-all.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-PHANTOM-BLADES -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-phantom-blades.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-SOAR -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-soar.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-TRACKING -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-tracking.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-CALCIFY -CharacterId NECROBINDER -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-calcify.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-DIE-FOR-YOU -CharacterId NECROBINDER -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-die-for-you.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SMOKE-002
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-WATERFALL-001 -EncounterId WATERFALL_GIANT_BOSS -PowerId STEAM_ERUPTION_POWER -PowerAmount 10 -ExpectedFinishedTurn 2
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-AXEBOT-HAMMER-001 -EncounterId AxebotsNormal -MonsterMoveId HAMMER_UPPERCUT_MOVE -ExpectedPlayerHpLoss 14 -ExpectedPlayerPowersJson '{"WEAK_POWER":2,"FRAIL_POWER":2}'
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-004 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-004.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-005 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-005.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-006 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-006.json -AdditionalMonsterId Fabricator
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-007 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-007.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-008 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-008.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-009 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-009.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-010 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-010.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-011 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-011.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-012 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-012.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-013 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-013.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-014 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-014.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-015 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-015.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-016 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-016.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-017 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-017.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-018 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-018.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-019 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-019.json -AdditionalMonsterId TorchHeadAmalgam
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-020 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-020.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-021-KAISER -EncounterId KaiserCrabBoss -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-021-kaiser.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-021-SUPPORT -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-021-support.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-022 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-022.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-023 -EncounterId BowlbugsWeak -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-023.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-024 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-024.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-026 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-026.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-027 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-027.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-028-LIFECYCLE -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-028-lifecycle.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-028-TURN-END -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-028-turn-end.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-029-SHRINK -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-029-lifecycle.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-029-ARTIFACT -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-029-artifact.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-029-TANGLED -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-029-tangled.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-029-RINGING -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-029-ringing.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-029-INTANGIBLE -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-029-intangible.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-030-DAMAGE -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-damage.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-030-BLOCK -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-block.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-030-POISON -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-poison.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-030-BLOCK-LIFECYCLE -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-block-lifecycle.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-030-SLOW -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-slow.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-031-RESOURCES -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-031-resources.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-031-MODIFIERS -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-031-modifiers.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-031-LIFECYCLE -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-031-lifecycle.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-RESOURCES -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-resources.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-STARS -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-stars.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-START-POWERS -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-start-powers.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-COOLANT -CharacterId DEFECT -EnemyCurrentHp 999 -OrbsJson '[{"orbId":"LIGHTNING_ORB","count":1},{"orbId":"FROST_ORB","count":1}]' -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-coolant.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-GLOBAL -AdditionalMonsterId TurretOperator -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-global.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-RITUAL -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-ritual.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-COLOSSUS -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-colossus.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-TAINTED -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-tainted.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-CONCOCT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-concoct.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-CORROSIVE-WAVE -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-corrosive-wave.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-DEMISE -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-demise.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-DISINTEGRATION -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-disintegration.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-LIFECYCLE -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-lifecycle.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-ORBS-NEMESIS -CharacterId DEFECT -EnemyCurrentHp 999 -OrbsJson '[{"orbId":"FROST_ORB","count":1}]' -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-orbs-nemesis.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-TENDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-tender.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-JUGGLING -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-juggling.json -ExitOnComplete
```

### Linux（Bash）

```bash
./tools/run-unattended-test.sh --scenario-id FIX-141700-TURN-START-CHOICE-FIXED --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 999 --clear-player-piles --cards-path coverage/unattended/turn-start-choice-once-141700-cards.json --combat-relics-path coverage/unattended/turn-start-choice-once-141700-relics.json --powers-path coverage/unattended/turn-start-choice-once-141700-powers.json --initial-player-energy 0 --force-short-search-only --short-search-budget-override-milliseconds 3000 --search-max-degree-of-parallelism-for-test 1 --expected-initial-turn-start-choice-turn 2 --expected-initial-turn-start-choice-source-id ENTROPY_POWER --expected-initial-turn-start-choice-card-id STRIKE_IRONCLAD --expected-reused-turn 2 --expected-unexpected-replans-at-most 0 --stop-after-expected-reuse --headless-fast-mode-for-test Instant --deployment-fast-mode-for-test Instant --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-SILENT-LARGE-DECK-5S --character-id SILENT --seed SEARCH_PERF_SILENT_LARGE_DECK --encounter-id AEONGLASS_BOSS --ascension 5 --act-index-for-test 2 --enemy-current-hp 512 --initial-enemy-move-ids-json '["EBB_MOVE"]' --initial-player-hp 65 --initial-player-max-hp 65 --initial-player-energy 3 --clear-player-piles --cards-path coverage/unattended/search-performance-silent-large-deck-cards.json --performance-preset-for-test VeryHigh --potion-policy-for-test Smart --search-max-degree-of-parallelism-for-test 8 --force-short-search-only --short-search-budget-override-milliseconds 5000 --measure-search-phases --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-NECROBINDER-POTION-QUICK --character-id NECROBINDER --seed SEARCH_PERF_NECROBINDER_POTION --run-snapshot-path coverage/unattended/search-performance-necrobinder-potion-heavy-run-snapshot.json --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2 --enemy-current-hp 526 --initial-player-hp 41 --cards-json '[]' --search-max-degree-of-parallelism-for-test 8 --performance-preset-for-test VeryHigh --potion-policy-for-test RequireAtLeastOne --force-short-search-only --short-search-budget-override-milliseconds 5000 --measure-search-phases --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-COMPLEX-RANDOM-KNIGHTS-CANONICAL-DOP1-2S --character-id IRONCLAD --seed SEARCH_PERF_COMPLEX_RANDOM_KNIGHTS --encounter-id KNIGHTS_ELITE --ascension 10 --act-index-for-test 2 --initial-enemy-current-hps-json '[108,97,89]' --initial-enemy-move-ids-json '["RAM_MOVE","HEX","POWER_SHIELD_MOVE"]' --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 5 --clear-run-deck --clear-player-piles --cards-path coverage/unattended/search-performance-complex-random-knights-cards.json --performance-preset-for-test Low --potion-policy-for-test Smart --search-max-degree-of-parallelism-for-test 1 --force-short-search-only --short-search-budget-override-milliseconds 2000 --measure-search-phases --enable-detailed-diagnostic-logs-for-test 0 --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-COMPLEX-RANDOM-QUEEN-CANONICAL-DOP1-2S --character-id REGENT --seed SEARCH_PERF_COMPLEX_RANDOM_QUEEN --encounter-id QUEEN_BOSS --ascension 10 --act-index-for-test 2 --initial-enemy-current-hps-json '[211,419]' --initial-enemy-move-ids-json '["STRONG_TACKLE_MOVE","PUPPET_STRINGS_MOVE"]' --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 5 --initial-player-stars 3 --clear-run-deck --clear-player-piles --cards-path coverage/unattended/search-performance-complex-random-queen-cards.json --powers-path coverage/unattended/search-performance-complex-random-queen-powers.json --performance-preset-for-test Low --potion-policy-for-test Smart --search-max-degree-of-parallelism-for-test 1 --force-short-search-only --short-search-budget-override-milliseconds 2000 --measure-search-phases --enable-detailed-diagnostic-logs-for-test 0 --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-COMPLEX-RANDOM-TEST-SUBJECT-CANONICAL-DOP1-2S --character-id DEFECT --seed SEARCH_PERF_COMPLEX_RANDOM_TEST_SUBJECT --encounter-id TEST_SUBJECT_BOSS --ascension 10 --act-index-for-test 2 --mark-encounter-as-second-boss-for-test --initial-enemy-current-hps-json '[111]' --initial-enemy-move-ids-json '["BITE_MOVE"]' --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 5 --clear-run-deck --clear-player-piles --cards-path coverage/unattended/search-performance-complex-random-test-subject-cards.json --powers-path coverage/unattended/search-performance-complex-random-test-subject-powers.json --performance-preset-for-test Low --potion-policy-for-test Smart --search-max-degree-of-parallelism-for-test 1 --force-short-search-only --short-search-budget-override-milliseconds 2000 --measure-search-phases --enable-detailed-diagnostic-logs-for-test 0 --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-COMPLEX-RANDOM-AEONGLASS-DOP1-2S --character-id NECROBINDER --seed SEARCH_PERF_COMPLEX_RANDOM_AEONGLASS --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2 --enemy-current-hp 526 --initial-enemy-move-ids-json '["EBB_MOVE"]' --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 5 --clear-run-deck --clear-player-piles --cards-path coverage/unattended/search-performance-complex-random-aeonglass-cards.json --powers-path coverage/unattended/search-performance-complex-random-aeonglass-powers.json --performance-preset-for-test Low --potion-policy-for-test Smart --search-max-degree-of-parallelism-for-test 1 --force-short-search-only --short-search-budget-override-milliseconds 2000 --measure-search-phases --enable-detailed-diagnostic-logs-for-test 0 --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id BATCH-164623-TRANSFORM-ENTERED-COMBAT-CONTINUATION --character-id IRONCLAD --encounter-id LivingFogNormal --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/batch-164623-transform-entered-combat.json --verify-incremental-search --keep-game-open --timeout-seconds 180
./tools/run-unattended-test.sh --scenario-id BATCH-164623-TURN-SCOPED-CARD-HISTORY-CONTINUATION-FINAL --character-id IRONCLAD --encounter-id LivingFogNormal --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/batch-164623-turn-scoped-card-history.json --verify-incremental-search --keep-game-open --timeout-seconds 180
./tools/run-unattended-test.sh --scenario-id BATCH-164623-ORB-DEATH-POWER-ORDER-CONTINUATION --character-id DEFECT --encounter-id AXEBOTS_NORMAL --initial-enemy-current-hps-json '[5,50]' --monster-move-checks-path coverage/unattended/batch-164623-orb-death-power-order.json --verify-incremental-search --exit-on-complete --timeout-seconds 180
./tools/run-unattended-test.sh --scenario-id POST0201-SCRAPE-NEGATIVE-COST-FINAL --character-id DEFECT --encounter-id LivingFogNormal --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/scrape-negative-cost-discard-0201.json --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id POST0201-WHISTLE-STUN-FOLLOW-UP-FINAL --character-id DEFECT --encounter-id THE_INSATIABLE_BOSS --enemy-current-hp 281 --monster-move-checks-path coverage/unattended/whistle-stun-follow-up-0201.json --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id POST0201-BRAND-POST-CHOICE-POWER-FORK-FINAL --character-id IRONCLAD --encounter-id LivingFogNormal --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/brand-post-choice-power-fork-0201.json --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id QOL-CONTROLLER-STOP-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --verify-controller-session-lifecycle --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SETTINGS-TABS-LIFECYCLE-NEXT --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --verify-controller-session-lifecycle --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id UPLOAD-PROGRESS-CANCEL-CONFIRMATION-NEXT-FINAL --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --verify-controller-session-lifecycle --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id UPLOAD-DIRECT-STATE-OWNERSHIP-NEXT --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --verify-controller-session-lifecycle --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id UPLOAD-PANEL-MAILBOX-LIFECYCLE-NEXT --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --verify-controller-session-lifecycle --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-LOW-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test Low --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-MEDIUM-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test Medium --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-HIGH-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test High --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-VERY-HIGH-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test VeryHigh --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CHOICES-PARADOX-SCROLLS-0160 --character-id SILENT --seed YS41WKT7ZUXS --run-snapshot-path "${CHOICES_PARADOX_RUN_SNAPSHOT_PATH:?set CHOICES_PARADOX_RUN_SNAPSHOT_PATH}" --progress-snapshot-path "${CHOICES_PARADOX_PROGRESS_SNAPSHOT_PATH:?set CHOICES_PARADOX_PROGRESS_SNAPSHOT_PATH}" --encounter-id SCROLLS_OF_BITING_NORMAL --ascension 10 --act-index-for-test 2 --initial-player-hp 60 --initial-enemy-current-hps-json '[33,37,38,36]' --initial-enemy-move-ids-json '["CHEW","MORE_TEETH","CHOMP","MORE_TEETH"]' --reload-run-rng-after-state-injection --force-short-search-only --short-search-budget-override-milliseconds 8000 --expected-initial-setup-choice-count-at-least 1 --expected-initial-setup-choice-source-id CHOICES_PARADOX --expected-initial-setup-choice-text-starts-with '选择悖论：选择 ' --expected-initial-choice-branches-evaluated-at-least 5 --stop-after-initial-setup-assertion --timeout-seconds 150 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RINGING-HAVOC-AUTOPLAY-0160-FINAL --character-id IRONCLAD --encounter-id LivingFogNormal --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/ringing-havoc-autoplay-0160.json --verify-incremental-search --exit-on-complete --timeout-seconds 120
./tools/run-unattended-test.sh --scenario-id HEADBUTT-EMPTY-DISCARD-0160 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 50 --clear-player-piles --cards-path coverage/unattended/headbutt-empty-discard-0160-cards.json --expected-played-card-id HEADBUTT --expected-reused-turn 2 --stop-after-expected-reuse --expected-unexpected-replans-at-most 0 --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 120
./tools/run-unattended-test.sh --scenario-id COSMIC-INDIFFERENCE-EMPTY-DISCARD-0160 --character-id REGENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 50 --clear-player-piles --cards-path coverage/unattended/cosmic-indifference-empty-discard-0160-cards.json --expected-played-card-id COSMIC_INDIFFERENCE --expected-reused-turn 2 --stop-after-expected-reuse --expected-unexpected-replans-at-most 0 --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --exit-on-complete --timeout-seconds 120
./tools/run-unattended-test.sh --scenario-id FAIRY-AUTOMATIC-RESCUE-FINAL2-0150 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 57 --initial-player-hp 1 --potion-id FairyInABottle --clear-player-piles --cards-path coverage/unattended/fairy-automatic-rescue-0150-cards.json --initial-enemy-move-ids-json '["FIRST_ACID_GOOP"]' --expected-initial-only-death-routes-found 0 --expected-initial-combat-ended-turn 2 --expected-initial-potion-count 1 --expected-used-potion-id FAIRY_IN_A_BOTTLE --expected-finished-turn 2 --expected-unexpected-replans-at-most 0 --verify-incremental-search --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 180 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id INSATIABLE-PARETO-CONTROL-150 --character-id SILENT --seed 2DJ8M7EAKQUS --encounter-id THE_INSATIABLE_BOSS --enemy-current-hp 341 --initial-player-hp 24 --initial-player-max-hp 57 --initial-player-energy 4 --initial-enemy-move-ids-json '["LIQUIFY_GROUND_MOVE"]' --clear-player-piles --cards-path coverage/unattended/insatiable-malaise-control-150-cards.json --performance-preset-for-test High --force-short-search-only --expected-initial-hp-lost-at-most 0 --expected-initial-projected-battle-hp-lost-at-most 7 --expected-initial-sold-hp 0 --expected-initial-potion-count 0 --expected-initial-searched-turns-at-least 7 --expected-initial-death-turn-at-least 7 --expected-initial-final-enemy-hp-at-most 204 --stop-after-initial-solver-result-assertion --timeout-seconds 180 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id BUILTIN-LISTENER-IDENTITY-533 --character-id SILENT --seed BJCZX3J13PZJ --run-snapshot-path coverage/unattended/solver-longline-run-snapshot.json --encounter-id NIBBITS_NORMAL --enemy-current-hp 999 --initial-player-hp 35 --potion-id WeakPotion --expected-initial-potion-count 0 --expected-initial-hp-lost-at-most 0 --expected-initial-projected-battle-hp-lost-at-most 0 --expected-initial-shuffles-crossed-at-least 2 --expected-unexpected-replans-at-most 0 --expected-finished-turn 5 --verify-combat-root-snapshot --verify-incremental-search --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MODIFIER-IDENTITY-532 --encounter-id LivingFogNormal --modifier-id MURDEROUS --monster-move-checks-path coverage/unattended/murderous-fabricator-spawn-532.json --exit-on-complete
./tools/run-visible-steam-benchmark.sh --timeout-seconds 360
./tools/run-unattended-test.sh --scenario-id LAGAVULIN-SLEEP-REUSE-176 --character-id REGENT --encounter-id LAGAVULIN_MATRIARCH_BOSS --enemy-current-hp 233 --clear-player-piles --cards-json '[{"cardId":"DEFEND_REGENT","pile":"Hand","count":1}]' --initial-enemy-move-ids-json '["SLEEP_MOVE"]' --expected-reused-turn 2 --stop-after-expected-reuse --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id BEAT-INTO-SHAPE-PLAYABLE-175 --character-id REGENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --clear-player-piles --cards-json '[{"cardId":"BEAT_INTO_SHAPE","pile":"Hand","count":1}]' --expected-played-card-id BEAT_INTO_SHAPE --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-LOW-171 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test Low --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-HIGH-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test High --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-CUSTOM-173 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test Custom --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id INFESTED-PRISM-VITAL-SPARK-152 --character-id REGENT --encounter-id INFESTED_PRISMS_ELITE --enemy-current-hp 171 --monster-move-checks-path coverage/unattended/infested-prism-vital-spark-152.json --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id KNOWLEDGE-DEMON-SEARCH-CHOICE-162 --character-id REGENT --encounter-id KNOWLEDGE_DEMON_BOSS --enemy-current-hp 399 --expected-initial-choice-branches-evaluated-at-least 2 --expected-initial-planned-choice-card-id MIND_ROT --expected-initial-act-ending-boss 1 --expected-observed-player-power-id MIND_ROT_POWER --stop-after-expected-player-power --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id KNOWLEDGE-LIVE-END-RISK-INCREMENTAL --character-id REGENT --encounter-id KNOWLEDGE_DEMON_BOSS --enemy-current-hp 399 --expected-initial-choice-branches-evaluated-at-least 2 --expected-initial-planned-choice-card-id MIND_ROT --expected-initial-act-ending-boss 1 --expected-observed-player-power-id MIND_ROT_POWER --stop-after-expected-player-power --enable-stop-on-worse-recalculation-for-test --expected-unexpected-replans-at-most 0 --verify-incremental-search --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id DEATH-TURN-PAUSE-165 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 57 --initial-player-hp 1 --clear-player-piles --cards-json '[]' --initial-enemy-move-ids-json '["FIRST_ACID_GOOP"]' --expected-initial-only-death-routes-found 1 --expected-initial-death-turn 1 --expected-full-auto-paused-at-death-turn --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SCULPTING-STRIKE-CHOICE-151 --character-id NECROBINDER --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 50 --clear-player-piles --cards-path coverage/unattended/sculpting-strike-choice-151-cards.json --expected-played-card-id SCULPTING_STRIKE --expected-reused-turn 2 --stop-after-expected-reuse --verify-incremental-search --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id ARMAMENTS-IMPLICIT-UPGRADE-OBSERVATION-534 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 9 --clear-player-piles --cards-json '[{"cardId":"ARMAMENTS","pile":"Hand","count":1},{"cardId":"STRIKE_IRONCLAD","pile":"Hand","count":1}]' --expected-played-card-id ARMAMENTS --expected-finished-turn 1 --expected-unexpected-replans-at-most 0 --verify-incremental-search --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id COSMIC-INDIFFERENCE-IMPLICIT-151B --character-id REGENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 50 --clear-player-piles --cards-path coverage/unattended/cosmic-indifference-choice-151b-cards.json --expected-played-card-id COSMIC_INDIFFERENCE --expected-reused-turn 2 --stop-after-expected-reuse --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id DUPLICATE-POTION-SEARCH-147 --character-id REGENT --encounter-id QUEEN_BOSS --enemy-current-hp 1 --potions-path coverage/unattended/duplicate-potions-147.json --expected-initial-executable-action-count-at-least 1 --expected-finished-turn-at-most 5 --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id PEN-NIB-ROUTE-148 --character-id IRONCLAD --encounter-id MECHA_KNIGHT_ELITE --enemy-current-hp 70 --clear-player-piles --cards-path coverage/unattended/pen-nib-route-148-cards.json --relics-path coverage/unattended/pen-nib-route-148-relics.json --verify-incremental-search --expected-initial-executable-action-count-at-least 2 --expected-initial-relic-effect-id PEN_NIB --expected-initial-relic-effect-summary '×2' --expected-initial-hp-lost-at-most 0 --expected-initial-projected-battle-hp-lost-at-most 0 --expected-initial-only-death-routes-found 0 --expected-initial-combat-ended-turn 1 --expected-finished-turn 1 --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0.05 --assert-deployment-speed-restored --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id CENTENNIAL-PUZZLE-STATE-149 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 57 --combat-relics-path coverage/unattended/centennial-puzzle-state-149-relics.json --monster-move-checks-path coverage/unattended/centennial-puzzle-state-149.json --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id JOSS-PAPER-STATE-150 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/joss-paper-state-150-relics.json --monster-move-checks-path coverage/unattended/joss-paper-state-150.json --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id ONLY-DEATH-ROUTES-150 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 57 --initial-player-hp 1 --clear-player-piles --cards-json '[]' --initial-enemy-move-ids-json '["FIRST_ACID_GOOP"]' --expected-initial-only-death-routes-found 1 --expected-player-death --expected-finished-turn 1 --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id PERF-END-TURN-CLEANUP-FINAL-099 --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/performance-end-turn-cleanup-098.json --timeout-seconds 180 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERF-DAMAGE-PIPELINE-100 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-damage.json --timeout-seconds 240 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id NIBBITS-UNIFIED30-SOLD-CAP-084 --character-id SILENT --seed BJCZX3J13PZJ --run-snapshot-path coverage/unattended/solver-longline-run-snapshot.json --encounter-id NIBBITS_NORMAL --enemy-current-hp 999 --initial-player-hp 35 --potion-id WeakPotion --expected-initial-potion-count 0 --expected-initial-projected-battle-hp-lost-at-most 0 --expected-initial-sold-hp 0 --expected-initial-sold-hp-branches-pruned-at-least 1 --expected-reused-turn 3 --expected-finished-turn-at-most 8 --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id QUEEN-CHAINS-REUSE-FINAL-085 --character-id IRONCLAD --seed Y883BRPFJZ05 --run-snapshot-path "${RUN_SNAPSHOT_PATH:?set RUN_SNAPSHOT_PATH to current_run.save}" --encounter-id QUEEN_BOSS --enemy-current-hp 70 --initial-player-hp 80 --initial-enemy-move-ids-json '["","PUPPET_STRINGS_MOVE"]' --expected-reused-turn 3 --stop-after-expected-reuse --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CORPSE-SLUGS-USER-RUN-073 --character-id IRONCLAD --seed Y883BRPFJZ05 --run-snapshot-path "${RUN_SNAPSHOT_PATH:?set RUN_SNAPSHOT_PATH to current_run.save}" --encounter-id CORPSE_SLUGS_WEAK --enemy-current-hp 999 --initial-player-hp 80 --expected-finished-turn-at-most 20 --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MECHA-MEMORY-FULL-AUTO-FINAL-070 --character-id SILENT --seed BJCZX3J13PZJ --run-snapshot-path coverage/unattended/mecha-knight-memory-run-snapshot.json --encounter-id MECHA_KNIGHT_ELITE --enemy-current-hp 300 --initial-player-hp 65 --short-search-budget-override-milliseconds 5000 --deep-search-budget-override-milliseconds 60000 --expected-initial-search-phase Deep --expected-initial-deep-search-triggered 1 --expected-initial-total-elapsed-milliseconds-at-most 25000 --expected-initial-total-allocated-bytes-at-most 4500000000 --expected-initial-gen2-collections-at-most 6 --expected-initial-total-gc-pause-milliseconds-at-most 8000 --expected-initial-max-gc-pause-milliseconds-at-most 50 --expected-initial-max-main-thread-frame-gap-milliseconds-at-most 100 --expected-reused-turn 3 --expected-finished-turn-at-most 9 --measure-search-phases --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SOLVER-ROUTE-POLICY-060 --character-id SILENT --seed BJCZX3J13PZJ --run-snapshot-path coverage/unattended/solver-longline-run-snapshot.json --encounter-id NIBBITS_NORMAL --enemy-current-hp 999 --initial-player-hp 35 --potion-id WeakPotion --expected-initial-potion-count 0 --expected-initial-projected-battle-hp-lost-at-most 0 --expected-reused-turn 3 --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SOLD-HP-POLICY-BATCH-059-DEFENSE-CHOICE --character-id IRONCLAD --encounter-id NIBBITS_WEAK --enemy-current-hp 43 --cards-path coverage/unattended/sold-hp-policy-batch-059-defense-choice.json --clear-player-piles --expected-initial-sold-hp-at-most 5 --expected-initial-sold-hp-branches-pruned-at-least 1 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SOLD-HP-POLICY-BATCH-059-UNAVOIDABLE --character-id IRONCLAD --encounter-id NIBBITS_WEAK --enemy-current-hp 43 --cards-path coverage/unattended/sold-hp-policy-batch-059-unavoidable.json --clear-player-piles --expected-initial-sold-hp 0 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SOLD-HP-POLICY-BATCH-059-STABLE-NO-SALE --character-id IRONCLAD --encounter-id SLIMES_WEAK --enemy-current-hp 999 --cards-path coverage/unattended/sold-hp-policy-batch-059-active-sale.json --clear-player-piles --expected-initial-sold-hp 0 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-POWER-BATCH-058 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-power-batch-058-relics.json --monster-move-checks-path coverage/unattended/relic-power-batch-058.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-POWER-TEMPORARY-BATCH-058 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-power-batch-058-temporary-relics.json --monster-move-checks-path coverage/unattended/relic-power-batch-058-temporary.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-POTION-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-potion-relics.json --potion-check-path coverage/unattended/relic-reactive-batch-057-potion.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-TURNS-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-turns-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-turns.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-TURN-END-FINAL --character-id IRONCLAD --enemy-current-hp 999 --relics-path coverage/unattended/relic-reactive-batch-057-turn-end-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-turn-end.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-KUSARIGAMA-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-kusarigama-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-kusarigama.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-STARS-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-stars-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-stars.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-EMOTION-FINAL --character-id DEFECT --enemy-current-hp 999 --orbs-json '[{"orbId":"LIGHTNING_ORB","count":1}]' --combat-relics-path coverage/unattended/relic-reactive-batch-057-emotion-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-emotion.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-TOP-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-top-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-top.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-UNDYING-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-undying-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-undying.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-PAELS-EYE-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-paels-eye-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-boundary.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-HISTORY-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-history-course-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-boundary.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-TOASTY-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-toasty-mittens-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-boundary.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-DETERMINISTIC-056 --character-id DEFECT --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-deterministic-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-deterministic-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-OSTY-056 --character-id NECROBINDER --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-osty-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-osty-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-CYCLES-056 --character-id REGENT --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-cycles-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-cycles-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-ATTACKS-056 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-attacks-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-attacks-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-LETTER-056 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-letter-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-letter-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-LEGION-056 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-legion-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-legion-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-GENERATION-056 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-generation-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-generation-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-DAMAGE-056 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-damage-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-damage-checks.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-START-FIRST-055 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-start-batch-055-first-relics.json --monster-move-checks-path coverage/unattended/relic-turn-start-batch-055-first-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-START-CYCLES-055 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-start-batch-055-cycles-relics.json --monster-move-checks-path coverage/unattended/relic-turn-start-batch-055-cycles-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-START-TEA-055 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-start-batch-055-tea-relics.json --monster-move-checks-path coverage/unattended/relic-turn-start-batch-055-tea-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-START-CORE-055 --character-id DEFECT --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-start-batch-055-core-relics.json --monster-move-checks-path coverage/unattended/relic-turn-start-batch-055-core-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-START-CONCH-055 --character-id IRONCLAD --encounter-id KnightsElite --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-start-batch-055-conch-relics.json --monster-move-checks-path coverage/unattended/relic-turn-start-batch-055-conch-checks.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-PERSISTENT-054 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-persistent-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-persistent-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-RUNIC-PYRAMID-054 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-runic-pyramid-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-runic-pyramid-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-INFUSED-CORE-054 --character-id DEFECT --enemy-current-hp 999 --relics-path coverage/unattended/relic-infused-core-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-infused-core-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-DAMAGE-054 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-damage-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-damage-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-BOOT-054 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-boot-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-boot-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TUNGSTEN-054 --enemy-current-hp 999 --additional-monster-id BowlbugRock --combat-relics-path coverage/unattended/relic-tungsten-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-tungsten-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-VITRUVIAN-054 --character-id NECROBINDER --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-vitruvian-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-vitruvian-batch-054-checks.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-DRAW-CYCLES-053 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-draw-state-batch-053-cycles-relics.json --monster-move-checks-path coverage/unattended/relic-draw-state-batch-053-cycles-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-POCKETWATCH-053 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-draw-state-batch-053-pocketwatch-relics.json --monster-move-checks-path coverage/unattended/relic-draw-state-batch-053-pocketwatch-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-FIRST-TURN-SNAPSHOT-053 --enemy-current-hp 999 --relics-path coverage/unattended/relic-draw-state-batch-053-first-turn-relics.json --monster-move-checks-path coverage/unattended/relic-draw-state-batch-053-first-turn-checks.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-PURE-DRAW-052 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-pure-batch-052-draw-relics.json --monster-move-checks-path coverage/unattended/relic-pure-batch-052-draw-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-PURE-ENERGY-052 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-pure-batch-052-energy-relics.json --monster-move-checks-path coverage/unattended/relic-pure-batch-052-energy-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-CONDITIONS-052 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-pure-batch-052-turn-relics.json --monster-move-checks-path coverage/unattended/relic-pure-batch-052-turn-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-BOOMING-CONCH-052 --encounter-id KnightsElite --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-pure-batch-052-booming-relics.json --monster-move-checks-path coverage/unattended/relic-pure-batch-052-booming-checks.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-OSTY --character-id NECROBINDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-osty.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-DIRGE --character-id NECROBINDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-dirge.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-CHOICES --character-id NECROBINDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-choices.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-AUTOPLAY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-autoplay.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-POWERS --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-powers.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-NORMALITY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-normality.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-ENTHRALLED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-enthralled.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-RETURN-AUTOPLAY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-return-autoplay.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-UPGRADED --character-id NECROBINDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-NIGHTMARE-LIFECYCLE --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-nightmare-lifecycle.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-VOID-FORM --character-id IRONCLAD --enemy-current-hp 18 --card-id VOID_FORM --clear-player-hand --expected-played-card-id VOID_FORM --expected-finished-turn 2 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-BOUNCING-FLASK --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-bouncing-flask.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-DIRECT-A --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-direct-a.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-OUTBREAK --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-outbreak.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-ECHOING-SLASH --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-echoing-slash.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-OMNISLICE --character-id IRONCLAD --enemy-current-hp 999 --additional-monster-id CalcifiedCultist --monster-move-checks-path coverage/unattended/card-on-play-batch-042-omnislice.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-TRANSFORM --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-transform.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-HAND --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-hand.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-STATE --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-state.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-PILES --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-piles.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-TRANSFORM-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-transform-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-PURITY-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-purity-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-ZERO-OPTIONAL --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-zero-optional.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-HIDDEN-DAGGERS-EMPTY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-hidden-daggers-empty.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-BRAND-EMPTY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-brand-empty.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-SCAVENGE-EMPTY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-scavenge-empty.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-FRANTIC-ESCAPE --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-frantic-escape.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-ECHOING-SLASH-KILL --character-id IRONCLAD --enemy-current-hp 999 --additional-monster-id CalcifiedCultist --monster-move-checks-path coverage/unattended/card-on-play-batch-042-echoing-slash-kill.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-DIRECT-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-direct-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-END-OF-DAYS-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --additional-monster-id CalcifiedCultist --monster-move-checks-path coverage/unattended/card-on-play-batch-042-end-of-days-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-END-OF-DAYS --character-id IRONCLAD --enemy-current-hp 999 --additional-monster-id CalcifiedCultist --monster-move-checks-path coverage/unattended/card-on-play-batch-042-end-of-days.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-POWER-A --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-041-power-set-a.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-POWER-B --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-041-power-set-b.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-SYNCHRONIZE --character-id DEFECT --enemy-current-hp 999 --orbs-json '[{"orbId":"LIGHTNING_ORB","count":1},{"orbId":"FROST_ORB","count":1}]' --monster-move-checks-path coverage/unattended/card-on-play-batch-041-synchronize.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-TURBO-SLEEVE --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-041-turbo-up-my-sleeve.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-SUMMON-FORTH --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-041-summon-forth.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-SHADOW-STEP --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-041-shadow-step.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-POWER-A --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-power-set-a.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-POWER-B --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-power-set-b.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-PALE-BLUE-DOT --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-pale-blue-dot.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-RESOURCES --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-resources-and-targets.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-SEEKING-EDGE --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-seeking-edge.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-SIGNAL-BOOST --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-signal-boost.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-POWER --character-id NECROBINDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-power-set.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-TARGET --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-target-effects.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-TARGET-LIFECYCLE --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-target-lifecycle.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-RESOURCES --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-resources.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-SHIVS --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-shivs.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-INK --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-blade-of-ink.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-APOTHEOSIS --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-apotheosis.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-ENLIGHTENMENT --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-enlightenment.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-ENLIGHTENMENT-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-enlightenment-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-STORM --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-storm-of-steel.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-HOTFIX-LIFECYCLE --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-hotfix-lifecycle.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-EXPOSE-ARTIFACT --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-expose-artifact.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-HAZE-MULTI --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-haze-multi.json --additional-monster-id DampCultist --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-A --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-power-set-a.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-B --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-power-set-b.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-DANSE --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-danse-macabre.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-PLANNER --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-master-planner.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-SERPENT --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-serpent-form.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-STORM --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-storm.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-NO-ESCAPE --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-no-escape.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-POWER-A --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-power-set-a.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-POWER-B --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-power-set-b.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-SPECIAL --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-special-effects.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-BULLET-TIME --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-bullet-time.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-FERAL-HISTORY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-feral-history.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-JUGGLING-HISTORY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-juggling-history.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-036-A --character-id DEFECT --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-036-power-set-a.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-036-B --character-id DEFECT --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-036-power-set-b.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-036-ANTICIPATE --character-id DEFECT --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-036-anticipate-lifecycle.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-035-SELF --character-id IRONCLAD --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-035-self-powers.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-035-TARGET --character-id IRONCLAD --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-035-target-powers.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-035-BULK-UP --character-id DEFECT --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-035-bulk-up.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-035-REGENT --character-id REGENT --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-035-regent.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-035-PIERCING-WAIL --character-id IRONCLAD --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-035-piercing-wail-lifecycle.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-ACCURACY --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-accuracy.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-BLOCK --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-block.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-COST-LOCATION --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-cost-location.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-HANG --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-hang.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-HARD-TO-KILL --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-hard-to-kill.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-LEADERSHIP --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-leadership.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-LETHALITY --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-lethality.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-ONE-FOR-ALL --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-one-for-all.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-PHANTOM-BLADES --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-phantom-blades.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-SOAR --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-soar.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-TRACKING --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-tracking.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-CALCIFY --character-id NECROBINDER --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-calcify.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-DIE-FOR-YOU --character-id NECROBINDER --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-die-for-you.json
./tools/run-unattended-test.sh --scenario-id SMOKE-002
./tools/run-unattended-test.sh --scenario-id MONSTER-WATERFALL-001 --encounter-id WATERFALL_GIANT_BOSS --power-id STEAM_ERUPTION_POWER --power-amount 10 --expected-finished-turn 2
./tools/run-unattended-test.sh --scenario-id MONSTER-AXEBOT-HAMMER-001 --encounter-id AxebotsNormal --monster-move-id HAMMER_UPPERCUT_MOVE --expected-player-hp-loss 14 --expected-player-powers-json '{"WEAK_POWER":2,"FRAIL_POWER":2}'
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-004 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-004.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-005 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-005.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-006 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-006.json --additional-monster-id Fabricator
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-007 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-007.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-008 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-008.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-009 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-009.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-010 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-010.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-011 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-011.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-012 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-012.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-013 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-013.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-014 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-014.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-015 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-015.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-016 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-016.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-017 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-017.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-018 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-018.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-019 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-019.json --additional-monster-id TorchHeadAmalgam
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-020 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-020.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-021-KAISER --encounter-id KaiserCrabBoss --monster-move-checks-path coverage/unattended/monster-moves-batch-021-kaiser.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-021-SUPPORT --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-021-support.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-022 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-022.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-023 --encounter-id BowlbugsWeak --monster-move-checks-path coverage/unattended/monster-moves-batch-023.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-024 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-024.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-026 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-026.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-027 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-027.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-028-LIFECYCLE --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-028-lifecycle.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-028-TURN-END --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-028-turn-end.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-029-SHRINK --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-029-lifecycle.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-029-ARTIFACT --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-029-artifact.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-029-TANGLED --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-029-tangled.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-029-RINGING --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-029-ringing.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-029-INTANGIBLE --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-029-intangible.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-030-DAMAGE --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-damage.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-030-BLOCK --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-block.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-030-POISON --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-poison.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-030-BLOCK-LIFECYCLE --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-block-lifecycle.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-030-SLOW --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-slow.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-031-RESOURCES --monster-move-checks-path coverage/unattended/monster-moves-batch-031-resources.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-031-MODIFIERS --monster-move-checks-path coverage/unattended/monster-moves-batch-031-modifiers.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-031-LIFECYCLE --monster-move-checks-path coverage/unattended/monster-moves-batch-031-lifecycle.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-RESOURCES --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-032-resources.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-STARS --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-032-stars.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-START-POWERS --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-032-start-powers.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-COOLANT --character-id DEFECT --enemy-current-hp 999 --orbs-json '[{"orbId":"LIGHTNING_ORB","count":1},{"orbId":"FROST_ORB","count":1}]' --monster-move-checks-path coverage/unattended/monster-moves-batch-032-coolant.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-GLOBAL --additional-monster-id TurretOperator --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-032-global.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-RITUAL --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-032-ritual.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-COLOSSUS --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-colossus.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-TAINTED --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-tainted.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-CONCOCT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-concoct.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-CORROSIVE-WAVE --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-corrosive-wave.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-DEMISE --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-demise.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-DISINTEGRATION --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-disintegration.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-LIFECYCLE --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-lifecycle.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-ORBS-NEMESIS --character-id DEFECT --enemy-current-hp 999 --orbs-json '[{"orbId":"FROST_ORB","count":1}]' --monster-move-checks-path coverage/unattended/monster-moves-batch-033-orbs-nemesis.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-TENDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-tender.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-JUGGLING --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-juggling.json --exit-on-complete
```

成功请求尊重原命令的生命周期：Windows 的 `-ExitOnComplete` 和 Linux 的 `--exit-on-complete` 会在本条后退出；没有退出标志时，启动器必须等待当前 runId 的静稳 ready ACK 才返回并允许复用。`-KeepGameOpen / --keep-game-open` 为旧命令兼容保留；两端现在不传退出参数也会默认保持进程。任何 `Failed`、异步工作无法静稳、ready ACK 超时或启动器中断都会清理已精确认领的进程，不会把失败进程交给后续场景；Windows 与 Linux 都按可执行文件、启动身份、隔离目录和 DLL/manifest 哈希安全复用或重启。

## 自动覆盖范围

| 范围 | 状态 | 说明 |
|---|---|---|
| 单人战斗卡牌/选牌/生成牌 | 通过 | 既有牌选择、嵌套选择、随机生成、局内变换、升级/降级/附魔及生成卡后续监听均有严格差分 |
| Power、遗物、药水、充能球 | 通过 | 当前游戏 `0.111.0` 的单人战斗行为目录无未分类、无静态行为证据缺口、无原生重扫边界 |
| 怪物行动、死亡、复活、召唤 | 通过 | 57 个补偿行动全量分片复跑；结构性复活、召唤、替换、特殊移除另有整战与定向生命周期回归 |
| 跨回合算到底 | 通过 | 同族、实验体、花园鳗、旧日雕像、女王、双小啃兽等整战在预算覆盖范围内逐回合复用；生产搜索只允许时间和节点预算终止，回合上限只用于增量验证模式 |
| 多人模式及多人专属内容 | 不在范围 | 不把多人专属选择、队友死亡后的 Hook 活性或多人卡牌记为单人适配缺口 |

## 人工待测

| ID | 状态 | 检查项 |
|---|---|---|
| `SOLVER-DISABLE-525` | 待测 | 设置中禁用后立即取消后台搜索与自动部署、关闭全自动并清除旧路线；后续回合和首回合选牌阶段均不自动求解，手操不产生重算；新战斗仍可打开设置，重新启用后按当前真实状态搜索 |
| `UI-OVERLAY-001` | 待测 | 拖动、轻量收起按钮、单行 `14px` 粗体概览、无计数的状态详情按钮、无键位提示的重新计算/执行按钮、纯“推荐路线”标题、HP/费用固定双列、始终显示“余 0 费”、完整搜索回合滚动及底栏对齐；路线用药显示为 `预计用x瓶药`，数量等于已喝加路线剩余并在跨回合复用时保持；页面不使用中圆点拼接信息 |
| `UI-FULL-AUTO-001` | 待测 | 全自动关闭时为暗色次级按钮、运行中为绿色正向按钮，战斗结束暂停开关与退出战斗清理 |
| `UI-FONT-001` | 待测 | 中文字体使用游戏思源黑体、不回退到默认日文字形；普通/富文本/按钮 `2px` 描边清晰且箭头、展开符号不缺字 |
| `PERF-FRAME-001` | 待测 | 发牌动画和多回合后台搜索期间的实际帧时间体感；日志仅作为分配与 GC 辅助证据 |
| `RF-OFFICIAL-WORKSHOP-COEXIST-069` | 通过 | RF 本地 fork 已与 `0.10.0` 共同跑完完整长线；用户随后订阅创意工坊原版 RF 并完成一次实机启动，未出现初始化或共存问题 | 2026-08-21 |

## 判定规则

- “通过”必须有同一 `runId` 的 `Passed` 结果，并核对对应 `SEARCH_REQUEST`、`RESULT`、`ACTION`、`DEPLOY_*` 和真实怪物行动日志。
- 只编译通过、只看到最终胜利或只看模拟结果都不能标记为通过。
- `RID/resources still in use at exit` 当前记录为 Godot 退出噪音；任何 `CombatSolver/Unattended FAILED`、`SEARCH_FAILURE`、`DEPLOY_FAILURE` 或状态断言失败均判定场景失败。
