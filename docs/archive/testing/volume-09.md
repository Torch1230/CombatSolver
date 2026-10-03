# CombatSolver 测试入口历史卷 09

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
