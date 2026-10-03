# CombatSolver 测试入口历史卷 05

## 0.36.1：遗物计数

- 删除遗物早停说明行：纯展示删除，按 L0 执行 Release 编译，未重跑战斗。

- 遗物总开关默认值调整：仅修改设置数据的初始值，新配置及缺少该字段的配置默认开启，显式保存的关闭状态仍保留；单项默认值和归档快照默认值保持原行为。本次按 L0 选择 Release 编译，不重跑战斗。

- 本次 UI 文案：`UI-LOCALIZATION` / `9bd2e76b41bf446485b32dbc78f5fef8` Passed（25.59 秒），eng/zhs/zht、400 项目录与语言往返通过。

- 三策略面板排版：`RELIC-COUNTER-POLICY` / `eaba35f068ce47b09eca0b8c51990814` Passed（25.82 秒），覆盖原生遗物图标、独立开关、面板互斥/边界，并新增三个面板背景与文字调制均完全不透明、数值输入 16 号字断言；原计数与早停合同保持。未进行可见游戏人工排版验收。

- `SEARCH-HP-TARGET-STOP` / `fdc28b23b38741e3b87c2a143664b7fc` Passed，23.45 秒：零损、累计阈值、并行波次排空、成长存在/不存在、击杀成长兑现、强制一药与保留备用药、至少一药策略。第一次启动器在取得 PID 30748 的可执行路径前失败，尚未进入 fixture；新隔离实例 `relic-early-stop` 完成上述验证，未提高超时。

- `RELIC-COUNTER-POLICY` / `5d123007ff00488fa48ad0278513ccdf` Passed，25.36 秒：十项计数根捕获、Fork、live 后续改变隔离、总/单项开关、设置往返、UI 标题顺序/面板边界/互斥；带增量校验的免费及 3 HP 付费路线，原生末击计数一致。达标早停 2 节点，关闭后 62 节点。初次 `e46d16d197f9426abc54e583f6f2de50` 明确 Failed，暴露补充搜索的前缀父链丢失；修复后通过，未关闭验证。
- `UI-LOCALIZATION` / `d545270315244f54a07f1964f5bb4d74` Passed，25.62 秒：eng/zhs/zht、394 项文本目录、模板及原动态控件语言往返。没有可见人工排版验收。
- Windows 结构门禁通过；Bash 同步新增策略、早停、缓存与前缀所有权规则，未在 Linux 启动游戏。

## 0.36.0：统一搜索预算与进程诊断

### 26356 三层卡顿修复

后续 6020 录制修复：`DYNAMIC-VAR-METADATA` / `57b289699aa045f39cf330a4b157eee7` Passed（22.72 秒）。隔离游戏源补入玩家 BaseLib，验证真实 Clone 的 live 空登记基线、模拟连续 2000 次零空登记、自定义提示、升级数值、两代克隆及父子独立。首个 `18c136b84cbe4cd3b2bbbbdab8980335` 因隔离环境缺 BaseLib 明确 Failed，补齐依赖后运行。复跑命令使用 `-ScenarioId DYNAMIC-VAR-METADATA -Sts2GameRoot <含BaseLib的隔离游戏源> -HeadlessInstance dynamic-var-metadata -TimeoutSeconds 120`；不可把无 BaseLib 环境当该合同通过。独立 `GcPolicyChecks memory`、`checkpoint` 通过，自动模式要求后台请求并确认完成，原高碎片自动压缩已撤回。以下压缩验证为此前历史证据；本轮没有修复后三层可见对照。

- 录制升级独立目标运行 36 秒，测试周期 20 秒（10 秒句柄/10 秒普通段），watcher 三段收尾并压缩，目标 writer/drain/停顿心跳合同通过。第一段 17,338 次句柄创建、17,274 次销毁，EventsLost=0；第二段没有句柄事件，窗口隔离有效。外层临时 PowerShell 包装误把未设置的 LASTEXITCODE 当失败；collector-health 为 complete，目标 stdout 为 PASS，随后按实际产物解析验证，未重复录制。
- EventPipe 栈验证初版错误地要求独立 ClrStackWalk 非零，已修正为读取 ETLX 关联栈：第一段 32/17,338 条句柄创建有栈，540/540 条 GCTriggered 有栈，不能声称每个句柄都有栈。旧第 7 段 102/102 条 GCTriggered 有栈。新增 `trace-stacks` 输出触发时间/来源；句柄事件验证与栈关联验证分别执行。
- `PerformanceRecordingTests registry` 验证后台计数落入 timeline；`PROCESS-DIAGNOSTICS` / `8c08835ad3e64332b3085eb19de2b858` Passed，22.92 秒，真实 Godot 两个弱登记容器可在后台读取数量，原跨战斗摘要与读档心跳合同保持。没有新的玩家长局性能或完整 GC root 证据。

- `NODE-POOL-LIFETIME` / `374081022814401586f01f02b9e8db49` Passed，23.01 秒。实际调用 NCard/NGridCardHolder 的已打补丁泛型方法，各复用 200 次；验证出站、入站、子节点递归、离树目标保留以及包装登记无正增长（-2071 / 0，首项包含同期终结器清理，不能解释成精确释放数量）。没有用静态 helper 替代生产入口。
- `SEARCH-HP-TARGET-STOP` / `9f80fe8fcdba48e7b82aad50b49b965d` Passed，8.58 秒，零损/阈值/成长/药水早停合同保持。没有运行完整三层可见 A/B。
- `CombatSolver.GcPolicyChecks` 默认 20 项通过，覆盖刚回收后少量分配、整层超过区域容量、有效预测可回收、缺失预测、NoGC 丢失及累计指标；`memory` 通过玩家物理压力样本、碎片选择及真实一次压缩/常规收集。碎片选择使用玩家数值作为输入，实际收集发生在小测试进程，不据此推断大堆暂停收益。`scopes` 8 项通过。
- 独立 `checkpoint` 通过生产 1 GB NoGC 区域的建立、回收后续用、收集中取消、确认排空、恢复普通 GC 与完成计数。
- 完整 `GC-CHECKPOINT-BACKGROUND-V0111` / `084d4e27093a445c87e21cd788547d53` 在 120 秒超时，由启动器停止自己的 headless 进程；没有结果文件、最新日志停在创建战斗房间，不能认定夹具断言已经执行。没有增加超时或原样重跑，改用上述独立进程最小合同。完整夹具的延迟手动请求/跨引用释放 epoch 部分本轮未验证。

- SINGLE-SEARCH-PROFILE / `2fb11ffdc6c649bca3838cab87dffbf0` Passed，22.68 秒：旧 deep 自定义参数迁移、保存重载、四档预算、单搜索进度、请求工作累计与固定小预算。
- SEARCH-HP-TARGET-STOP / `8313b1d83703473bb5dc1c751bd2700b` Passed，7.75 秒：零损、阈值、并行、成长目标、必要药水和额外药水保留。
- THEFT-RECOVERY-POLICY / `88dca2a1337d47b3bfef439169922998` Passed，7.27 秒：策略合同与固定小预算搜索。没有据此宣称完整玩家战斗路线质量或内存收益。
- UI-LOCALIZATION / `b794090ac5584fa8a06ab8f18c31844e` Passed，9.78 秒：设置、动态状态和中英切换。PROCESS-DIAGNOSTICS / `75fe89832e6b472c88f82ebec29df2a4` Passed，22.33 秒：模拟 State 存在而 NetService 未就绪的读档窗口，调用真实心跳 Process；验证切换战斗后 GC 摘要仍在进程日志、高频显示采样未被复制。
- CheckpointTool self-test 31 项断言通过，包括新单配置政策比较、旧政策保留及不同代预算不冒充同一政策；Windows 结构门禁通过。Bash 入口同步了协议与结构约束，未在 Linux 实际启动游戏。
- 最新可见进程 27996 的最后一场 combat_ended 回收：managed live 2.157→2.057 GB、private 15.581→14.451 GB，working set 6.746→6.740 GB。原日志删除了前序战斗，不能从该样本证明长局卡顿根因；未开启新 trace 或可见性能测试。

## 0.35.5：偷窃策略

- 定位证据：本机进程 31712 的战斗日志 `combat-7165739b37ab40ecae1120a90a34198a.jsonl` 中 SEARCH_REQUEST 与最终结果均为 PreserveResources，最终零损、outstanding_stolen_resource=20；多次点击保策略也仍为该枚举，按钮没有接反。旧代码的审计合同明确要求先比较战损，已按用户新确认的保资源优先语义修正。
- `THEFT-RECOVERY-POLICY`：地精 `1934513228be4d6eb86333192f568332` Passed，23.08 秒；偷窃草蜢 `fa18ef06b5b340db9e565d79f44c02bc` Passed，7.34 秒。合同覆盖两药/15 战损追回优于零损丢失、放走反向选择、候选展示可接受追回带来的战损增加、失败不能优于胜利、未追回不得 HP 早停；分别跑两种策略的 256 节点/1500 ms 短搜。短搜不是原玩家整场回放，也不证明所有局面都能击杀逃跑怪。
- 普通早停哨兵 `SEARCH-HP-TARGET-STOP` / `3928bbf0e4df41b28700274cd04abaa6` Passed，7.75 秒，零损/阈值/成长与药水数量合同通过；同一 headless 进程复用，末次退出。Release 编译 0 警告 / 0 错误，结构门禁通过。

## 0.35.5：UI 操作区重排

- 搜索摘要取消世界线计数前的强制换行：仅改显示连接符，未改计数或搜索逻辑；本轮验证 Release 编译，未重跑游戏场景或实机视觉验收。

- 状态摘要字号调整：仅将五处字体统一为 16；Release 编译 0 警告 / 0 错误，差异检查通过。没有变更状态/事件逻辑，本轮未重跑游戏场景，实际字号与长文本排版交由用户视觉验收。

- 红框状态摘要局部整理：`UI-PRIORITY-FEEDBACK` / `ca19e6d8275241b5b32abca92ad4c173`，Passed，22.60 秒；既有结果显示、收起恢复、设置伸展与失焦保存合同通过。Release 编译 0 警告 / 0 错误。根据截图调整状态卡片，未改动作列表；未进行新版实机视觉验收。

- 设置与结果摘要第二阶段：`UI-PRIORITY-FEEDBACK` / `7d6ea6dbffc942a194742ce16e440f84`，Passed，22.55 秒。性能页战损阈值失焦保存为 19、切页后恢复；设置高度伸展；结果卡片收起迁入主栈、展开恢复正文首位；失窃仍位于战损之前，新搜索清除旧提示。Release 编译 0 警告 / 0 错误，结构门禁通过，英文词典无重复键。未做实机视觉验收，也未改变搜索或出牌政策。

- 采用顺序与搜索入口简化：`UI-PRIORITY-FEEDBACK` / `34b3478a49a5480bba243234d57a8c46`，Passed，22.51 秒。采用控件固定第二位、搜索时执行控件隐藏，原展开/收起与设置合同通过；Release 编译 0 警告 / 0 错误。未做实机视觉验收。

- 独立释放按钮与右侧自动偏好布局：`UI-PRIORITY-FEEDBACK` / `ee426c9635624b9bbd7ca51578a3e63f`，Passed，22.54 秒；内存条恢复 Pass，释放按钮与内存条同父且位于右侧；展开/收起、主开关和设置伸展合同通过。Release 编译 0 警告 / 0 错误；未进行实机视觉及管理员清理验证。

- 内存条释放入口整合：`UI-PRIORITY-FEEDBACK` / `e74200fa4e634b00b190421b2ac5d739`，Passed，22.46 秒。确认内存条接收鼠标事件、位于主操作区，原布局/启停/设置伸展合同通过。Release 编译 0 警告 / 0 错误。本轮未实际触发管理员授权和系统内存释放，未做真实鼠标点击或视觉验收；该测试不作为系统清理效果证据。

- 用户校正后的最终验证：`UI-PRIORITY-FEEDBACK` / `a9ee0cb8abc34cb384231bc81485b4fa`，Passed，22.61 秒。全自动固定为动作行第一项，展开/收起均保持位置；内存释放入口归属主界面。原有启停、收起战损/失窃和设置伸展合同通过。Release 编译 0 警告 / 0 错误；未做实机视觉验收。以下保留初版证据。

- `UI-PRIORITY-FEEDBACK`：`5708336d44454fc9b073371c0c4e048a`，Passed，22.69 秒。覆盖动作区展开/收起、搜索/空闲、采用入口组合；全自动控件在模式行与紧凑动作行之间移动；标题栏启停事件、维护按钮归属、偷窃策略位置，以及原有 SL 面板恢复、收起战损/失窃和设置页伸展合同。
- Release 编译 0 警告 / 0 错误，结构门禁通过（85 个 Search 文件）。初次编译发现 Godot 控件缺少 partial，补齐后通过；没有使用失败构建的旧产物进行验证。
- 本轮不改搜索、采用和部署命令实现；未进行实机视觉验收，未宣称具体窗口尺寸下的遮挡或帧率已经验证。没有发布创意工坊或推送远端。

## 0.35.5：P0 / P1 反馈

| 场景 | 最终 runId | 结果与范围 |
| --- | --- | --- |
| NATIVE-HAND-CHOICE-REPLAY | f56df4c5816d490fabbd1ebd8172c8bb | Passed，27.38 秒。燃烧契约原生选牌、投斧两次重放；主动构造选择计划失配，保留原生手牌选择，手动完成后具体出牌动作正常结束。 |
| TURN-SETUP-UI-TOOLS | 2080e6ad6c7e4907b80ecddb919ce9cc | Passed，30.64 秒。必备工具 T2 准备选牌中停止、重算、再次停止、继续原生选择，返回 Play 后忙碌状态和输入锁均清除；短/深预算各 1000 ms。 |
| UI-PRIORITY-FEEDBACK | 166804bf580b428dac522d4dca0fddcc | Passed，23.00 秒。主面板启停事件；清理会话且没有 TurnStarted 时恢复面板并保留手动计算；收起时财物提示在战损左侧、新搜索清除旧提示；设置面板从 440 增至 660 高度，内部滚动区增高至少 180，切页正常。 |
| SEARCH-HP-TARGET-STOP | 60ec179b65e7413a8f3d6b3480d3c60e | Passed，23.50 秒。保留既有零损/阈值/开关/DOP2/禁忌魔典哨兵；狩猎兑现收益后零损停搜（1 节点）；指定一瓶与全局至少一瓶均以一瓶零损获胜并保留额外药水；可重复击杀来源在 3 敌人时保持目标 3。 |

- 所有请求使用独立 headless 实例、120 秒上限并在完成后退出。结构门禁 `search_files=85`，Release 编译 0 警告 / 0 错误。
- 失配基线 `3d174aeadc8942abbe94d88764b30f48` 明确失败于“原生选择被取消”；修复后手动恢复合同通过。此前初版 fixture `d2aca6c8ae944e63915a4b74c6505258` 错把动作队列临时空闲当作重放完成，改为复用生产 GameAction.CompletionTask 捕获后普通/投斧场景 `263cacd38bb74b19a43af1b8a4485927` 已通过；该初版错误不是产品卡死证据。一次编译失败后的旧产物测试启动被中止，未计为验证。
- 未复现反馈中的所有“整个游戏进程无响应”情况；也未实际调用 BetterSpire2 的 SL 操作。无可见布局/鼠标验收，没有整场性能或所有第三方组合兼容结论。财物显示合同验证投影和布局，不新增怪物偷窃效果语义结论。

复跑入口为 `tools/testing/run-unattended-test.ps1 -ScenarioId <上述ID> -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance <独立名称> -ExitOnComplete -TimeoutSeconds 120`；TOOLS 使用 SILENT，并附 `-ShortSearchBudgetOverrideMilliseconds 1000 -DeepSearchBudgetOverrideMilliseconds 1000 -ForceShortSearchOnly`。

## 0.35.4：战损目标早停

- `SEARCH-HP-TARGET-STOP` 最终 `f6b4b94d8a6641bf8a6ec2cdd953776b` Passed，22.85 秒；固定 128 节点、1500 ms 短搜，后台独立实例 `hp-target`，120 秒请求上限，完成后退出。
- 缺少对应卡牌而保存禁忌魔典额度 12 时，HasGrowthTargets=false，默认早停仍有效。同根 1 HP 敌人、打击/防御/痛击/燃烧：开启展开 1 个节点，关闭展开 4 个，均完整零损胜利。此数值仅是最小功能对照，不代表整场性能。
- 合同注入已累计损失 3 HP，阈值 3 达标、阈值 0 不达标、开关关闭不达标；用于检查整场累计口径，不声称原生受伤差分。12 HP 敌人场景实际最大并发 2、展开 5 个节点，排空后返回完整零损胜利。
- 加入禁忌魔典后恢复成长例外，即使能立即零损击杀仍取得 1 次删牌收益；忽略局外收益时恢复早停。默认值和关闭后的序列化往返通过。初轮 `f705ffede7d6435b952a0d6c2b453bd6` 已通过基础合同，最终扩展了真正双 lane 场景并覆盖补充搜索出口源码改动后的运行。
- Release 编译 0 警告 / 0 错误，结构门禁 search_files=85。没有可见游戏测试、完整跑局或独立多药水后验场景结论。

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId SEARCH-HP-TARGET-STOP -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance hp-target -ExitOnComplete -TimeoutSeconds 120
```

## 0.35.4：按钮区与收起显示

- `UI-COMPACT-QOL` / `fe936f87a2764f329398710cbc41c885` Passed，22.42 秒，专用 headless 实例 `compact-qol`，120 秒上限，完成后退出。
- 补齐浅色主题开关状态颜色同步后，最终 0.35.4 构建同场景 `063d30e4cb70455a856524bfabfd0aed` Passed，22.59 秒；构建来源 `a72a669`。设置刷新触发开关外观更新，仅实际偏好变化写盘。
- 检查开关默认关闭、切换事件更新设置及序列化往返、下场开启、自动计算关闭时仍接入全自动、手动停止后刷新不重开、下一场重开、关闭偏好后下一场关闭。
- 直接渲染只读路线投影：收起后原 Label 可见且 Body 隐藏，7 HP 显示危险色，原位更新 0 HP 变为成功色，展开后归位，新搜索隐藏旧战损，未知投影保持问号和灰色。
- Release 编译 0 警告 / 0 错误，结构门禁 `search_files=85`。未做可见布局/鼠标验收；该 fixture 不证明整场自动部署或开局选牌完整链。

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId UI-COMPACT-QOL -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance compact-qol -ExitOnComplete -TimeoutSeconds 120
```

### 古代卡牌成长策略与累计成本（本批此前证据）

- `GROWTH-ANCIENT-POLICY` / `f9031db3e06441dcaa38a76db4c8b693` Passed，26.09 秒，IRONCLAD / FUZZY_WURM_CRAWLER_WEAK。专用后台实例、120 秒上限、1500 ms 短搜；测试完成后退出。
- 真实 CardModel/原生出牌与模拟完整 MoveStateSnapshot 对照：ECHO_FORM_POWER 重放 BRIGHTEST_FLAME 两次，共记 4 点最大生命消耗；随后 FORBIDDEN_GRIMOIRE 记 1 次删牌收益，独立额度 12 HP。原生历史重新捕获保持 4，Fork 增量不污染父分支，BeginSideTurn 保持累计成本。
- 实际搜索及增量回放：已有 4 点消耗、上限 4、手牌含 BRIGHTEST_FLAME / FORBIDDEN_GRIMOIRE / STRIKE_IRONCLAD / CASCADE，抽牌堆含 BRIGHTEST_FLAME。结果获得完整胜利及 1 次删牌收益，路线不使用至亮之焰或会自动打出它的 CASCADE。还检查不限、0、等于上限及手动已超额的准入口径，以及设置往返和成长行重载。
- 初轮 `4ee0b6363f77406aae14eea99c03425c` 已通过前段原生对照，在补充能力审计的 RankFinal 遇到重复对象键；修正对象身份去重后最终场景通过。中间一次启动器未取得已退出进程的 executable path，尚未提交请求；同一 DLL 重新启动后成功。未扩大超时。
- 没有可见鼠标/布局验收，没有完整跑局质量结论，也未验证其他 Mod 修改至亮之焰最大生命变量的历史回写语义。

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId GROWTH-ANCIENT-POLICY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance ancient-growth -ExitOnComplete -TimeoutSeconds 120
```

## 0.35.4：PR #83 合并检查与预设节点上限

- `python -X utf8 tools/testing/checks/NoVictoryRecoveryChecks/run.py`：通过 PR 自带 `AssertNoVictoryEscalationPolicy` 的全部策略断言与新增 8 项请求流程检查。直接编译生产 BuildNoVictoryEscalationProfile / EscalateSearchWhenNoVictory；原入口先复现“追加搜索丢弃明确采用结果”，修正后覆盖接管、已有胜利不重搜、胜利退出、停止、拒绝更差结果、两轮封顶、第二轮饱和及仅分支增长。结果、质量排序和根采用确定性替身，未声明整场搜索验证。
- `python -X utf8 tools/testing/checks/NoVictoryRecoveryChecks/presets.py`：直接编译生产四档声明及 SolverSearchProfile，核对节点加倍、时间及 Beam 保持原值。低/中/高/极高 Short 为 2400/4800/10000/20000，Deep 为 12000/24000/50000/100000。
- PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。未启动游戏，本轮没有新增原包恢复、可见 UI 或实战战损验收；下方 PR 附带的检查点记录来自作者此前验证。

## 无胜利路线时的搜索面升级（贡献者记录）

同一个玩家问题包 `CombatSolver-0.35.3-CEREMONIAL_BEAST_BOSS-610658da…`，观者 A9 第一幕 Boss
仪式兽，玩家 34/79、Boss 262/262、两瓶药（稳定血清、缚魂药水），恢复方式 `native_events`
（`materials_valid` / `canSearch=true`，原生二进制编码未校验）。三次都固定
`-NoGcRegionBudgetGigabytesForTest 10`，避免 NoGC 区域建不起来变成隐藏变量。

| 场景 | runId | 结果 |
|---|---|---|
| 第 1 回合检查点，录制的 High 策略，改动前 | 无（对照，未记录 runId） | `OnlyDeath=True`、战损 34、第 6 回合死、用药 0、`searched_turns=6`、17.0 秒。药水梯度三层 `saved=0`，`selected_potions=0` |
| 第 1 回合检查点，录制的 High 策略，改动后 + `-VerifySearchPolicySnapshot` | `76ed67cdcd504d30b451b230246b667c` | Passed，41.7 秒。一次升级（Beam 90→180、节点 25,000→50,000、出牌分支 48→96）后 `layer=2 won=True`：两瓶药、第 7 回合斩杀、战损 30、`OnlyDeath=False`、`Unmirrored=0`。同一次跑通过 `SearchPolicySnapshot` 全组结构断言（含新增的升级政策纯函数断言） |
| 第 3 回合检查点（改动前就能获胜），不可退化哨兵 | `09f0f4a5ea7541fa818c5389c03fb059` | Passed，16.6 秒。`NO_VICTORY_ESCALATION` 出现 **0 次**；`Potion=2, Saved=5/18, Rejected=170, Turns=4, 战损 29, CombatEndedTurn=6, SoldHp=0/15` 与改动前实机日志逐项相同 |

Beam 与节点是乘的关系，只抬一边都不够，四组顶格实测（`b2131b2c54b243d7a65c7ccffb58e17b`
为其中 Beam 135 / 100,000 那一组）：

| Beam | 节点上限 | 结果 | 胜利层实际展开 |
|---|---|---|---|
| 90 | 25,000 | 输 | 主搜索 2,701–5,206（前沿走空，花不掉预算） |
| 90 | 50,000 | 输 | **与上一行逐个相同**，只抬节点无效 |
| 135 | 50,000 | 输 | 需要 83,423，不够 |
| 135 | 100,000 | 赢（第 9 回合、战损 33） | 83,423 |
| 512 | 100,000 | 赢（第 8 回合、战损 31） | 26,671 |

`tools/inspection/verify-refactor-boundaries.ps1`：`REFACTOR_BOUNDARIES_OK search_files=84`。
`dotnet build CombatSolver.csproj -c Release`：0 警告 0 错误。

边界：这三次都是单一检查点的搜索质量证据，不是整包回放、不是完整自动部署，也不覆盖原版角色。
默认小遭遇战（`FUZZY_WURM_CRAWLER_WEAK` + 起始牌组）跑 `-VerifySearchPolicySnapshot` 会在
既有的「节点上限释放快照」断言上失败（`dop1=0/3`），改动前后一致，属于该组门禁的场景依赖，
与本改动无关。

## 0.35.3：PR 合并与监控版本提醒（本轮验证）

- 合并后的 `dotnet run --project tools/testing/checks/CardHookReceiverChecks -c Release`：78 项通过；`python -X utf8 tools/testing/checks/EndTurnAdmissionChecks/run.py`：33 项通过；`dotnet run --project tools/testing/checks/CardTargetingChecks -c Release`：37 项通过；`python -X utf8 tools/testing/checks/DamageDealerChecks/run.py`：101 项通过；`python -X utf8 tools/testing/checks/PlayerDeathChecks/run.py`：42 项通过。伤害合同替身有一条 CS0649 未赋值警告；共 291 项是生产方法链接合同，尚未执行游戏原包或完整原生结算差分。
- `dotnet run --project tools/testing/checks/ClientUpdateChecks -c Release`：35 项通过。验证三段数字比较（包括 0.35.10）、相等/更旧/主版本升级、204 旧服务、非法/缺失 JSON、HTTP 失败、取消、服务回撤版本及关闭提醒；失败不覆盖最后有效状态。
- 已部署工作台基线 5611856 同步后，在 `services/OnlinePresence` 执行 `npm test`：17 项通过，含管理员登录/Origin、采集端隔离、严格版本验证、旧客户端心跳、设置更新/清除/重启持久化及已有统计逻辑。Node 23.9.0 提示 SQLite experimental warning；线上使用 Node 24.15.0。
- 同目录 `$env:BROWSER_CHANNEL='msedge'; node --test browser.test.mjs`：9 项通过。新增版本维护表单实际 DOM 保存/关闭、刷新不覆盖输入，同时验证现有登录恢复、筛选、布局、延迟请求及统计恢复。默认 Playwright 浏览器未安装导致首轮启动失败，改用已安装的 Edge 后通过；未启动可见浏览器或游戏。
- 下方 PR 附带检查属于贡献者原始记录。本轮不将独立替身合同表述为游戏状态差分或实机修复的完整验收。

## 2026-09-10：玩家死亡能力清理

- Release 编译 0 警告/0 错误；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。没有安装或启动游戏。

- `python3 tools/testing/checks/PlayerDeathChecks/run.py`：42 项通过，提取编译生产 HandlePlayerDeath 和 RemovePowersAfterDeath；旧入口在宠物死亡回调观察到残留能力，失败基线已复现。
- 覆盖有/无奥斯提、宠物清理 pending、先清能力再清球/宠物、正负层数移除、允许死后存续能力、其他 owner、重复清理及现有 Illusion 移除否决规则。
- 测试状态/能力模型/宠物 Kill 为替身；不构成真实根/Fork、AfterRemoved 回调覆盖、死亡阻止或原报告完整回放验收。原版 CreatureCmd 的清理位置及 Creature.RemoveAllPowersAfterDeath 已定向核对。



## 2026-09-10：伤害来源生命状态隔离

- Release 编译通过，0 警告/0 错误；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。未安装或启动游戏。

- `python3 tools/testing/checks/DamageDealerChecks/run.py`：101 项生产伤害入口检查通过。旧代码在分支已死、实机存活的输入下仍产生伤害结果，最小基线失败。
- 编译生产全部 Damage 重载及 DamageSingleTarget；交错实机/分支死亡状态，覆盖禁止 live getter、独立分支、复活、null 来源、空目标及死亡来源不进入伤害/后续 Hook。
- 状态和逐目标/后续 Hook 为确定性替身；没有验证真实 Fork、伤害计算、原生死亡时序或报告中完整递归抽牌链。保留原递归安全上限，不把本合同写成整场验收。



## 2026-09-10：动态目标类型分支隔离

- Release 编译 0 警告/0 错误；Bash 结构门禁通过，`search_files=84`。同步更新两端门禁的分支模式，PowerShell 因当前环境缺少 `pwsh` 未运行。未安装或启动游戏。

- `dotnet run --project tools/testing/checks/CardTargetingChecks -c Release`：37 项通过，直接编译生产 `CombatPredictionSimulator.CardTargeting.cs`；旧源码复现分支无能力时误用实机全体目标。
- 两张动态目标牌分别覆盖分支能力缺失/0/1/2/移除、实机能力有无交错、其他角色能力隔离、独立分支，以及普通卡和非影子状态回退。影子状态用例禁止读取原生目标 getter。
- 游戏 0.111.0 的原版 Shiv / SovereignBlade 目标 getter 已定向核对：对应能力决定 `AnyEnemy` / `AllEnemies`。测试模型和状态为替身，不构成真实根捕获、完整 Fork、原生伤害或原包回放验收。



## 2026-09-10：结束回合循环出口准入

- Release 编译通过，0 警告/0 错误；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。未安装或启动游戏。

- `python3 tools/testing/checks/EndTurnAdmissionChecks/run.py`：33 项通过。直接编译生产结束回合入口、raw 生成、剪枝、准入谓词、materialization 循环和批次所有权容器；旧 `BuildAcceptedEndTurnNodes` 在相同输入下因未结算观测到达转置准入而失败。
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
- #75：`pwsh -NoProfile -File tools/testing/verify-unattended-map-points.ps1` Passed，4 种输入；从生产请求哈希表的 AST 取出真实字段表达式，检查省略、空数组、单点、多点的 JSON 往返形状，拒绝 null 和嵌套数组。原 PR 的显式空数组和非空数组均已复现多包一层。
- #77：`MODEL-STATE-INTEGRATION` / `444ddec2f3bf49568ece16acf9be80f5` Passed，27.93 秒，T1 → T2。真实游戏身份、CombatRootSnapshot、完整 CombatPredictionSimulator.Fork 和原版回合推进；专用测试为 BurningBlood 与 BigGameHunter 登记状态，持有可变列表及预测卡牌引用。父子对象/列表/卡牌隔离，子分支变更改变指纹与续用文本而父分支保持原值；原生和预测下一回合完整快照（含 continuation）一致。不是第三方 Mod 效果镜像通用正确性的证明。
- 原 PR 独立 32 项状态合同、3 项空登记及 1000 次 0 字节指纹分配检查在审计阶段通过；同一接口行为不重复。合并后的 Release 编译 0 警告/0 错误，PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。没有打开可见 Steam 游戏，没有发布。

专用模型状态合同必须使用新后台实例，并在请求后退出，避免已冻结的测试登记污染其他请求：

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId MODEL-STATE-INTEGRATION -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -ModifierId BIG_GAME_HUNTER -EnemyCurrentHp 500 -HeadlessInstance model-state-integration -ExitOnComplete -TimeoutSeconds 120
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
dotnet run --project tools/testing/checks/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release -- --empty
dotnet run --project tools/testing/checks/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release
dotnet run --project tools/testing/checks/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release -- --allocation
```

接口及手工验收范围见[模型状态适配](../../third-party/model-state.md)。此记录仅对应本项开发改动，不复用下方历史游戏场景作为本轮证据。

## 0.35.1：回收后堆空间复用

- `dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- memory`：Passed，直接链接生产策略及信号；玩家采样剩余可复用空间 4,172,872,440 字节，区域容量 6,938,945,322 字节。验证复用空间不重复增加物理用量、三 GB 波次可准入、物理上限立即阻止准入、用户预算上限、回收委托、Disable 探针清理及无实时探针模式。
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
- 最终 Release 编译通过，0 警告、0 错误。本机更新开发 DLL，未发版。未重现玩家完整两层后的长会话，不声明整体卡顿全部消失或提供 FPS 增幅。可复跑输入为 `coverage/fixtures/scenarios/state/combat-timing-lifetime.json`。

## 2026-09-10：SpeedX 横幅告知

- UI 低影响提示变更：检测已加载程序集名 SpeedX，在原有反馈横幅加中文/英文性能提醒；既有异常信息继续保留。检测结果通过一次枚举和程序集加载事件维护，不在每帧扫描程序集。没有增加 SpeedX 修补逻辑。
- 验证采用 Release 编译；本轮未进行实机横幅排版验证，不声明已修复 SpeedX 或长期卡顿。

## 2026-09-10：进程全程性能诊断（未发布）

- 自动压缩追加合同：`outputs/performance-compression-contract/` 连续 3 段全部成功采集并压缩，原始总计 8,408,334 字节转为 1,458,408 字节；collector 正常 complete。解压第一段后解析出 10043 个线程采样、8707 个分配事件、538 次 GC start，EventsLost=0。玩家已有段一次性从 280,502,570 字节压为 26,455,804 字节；此次只修改外部存储脚本，不改变 DLL、采样配置或搜索行为。

- 独立 `tools/testing/checks/PerformanceRecordingTests` 合同：36 秒进程采样，刻意停止主线程心跳 3 秒；验证后台仍写入、线程/内存/GC 数据、队列无丢弃、退出排空。10 秒分段连续采集并正常退出；带空格输出路径通过。修正 Windows PowerShell 的 File.Replace 空备份路径及 Process.ExitCode 句柄生命周期问题后最终合同通过。
- EventPipe 显式使用 runtime `0x100003C01D:5`，避免多个 profile 合并保留 Informational 而漏掉分配事件。独立样本解析出 10118 个采样事件、8696 个分配事件、538 次 GC start，EventsLost=0；topN 能解析出测试计算方法。采样含等待时间，不当作 CPU 占比。
- Heap dump 合同：独立进程收到现场请求，dotnet-dump exitCode=0，dumpheap 成功读取 PerformanceSession 等对象；连续轨迹继续分段并收尾。对应本地证据 `outputs/performance-snapshot-contract/`。
- 正常可见 Steam 短战斗：`f5cf09910c8b46548444922d768076f4` / `VISIBLE-LOGGING-CAPTURE-0300` Passed，31.75 秒，T6 结束，零计划外重算。时间线包含完整搜索/部署事件、16 个生命周期开始事件、引擎资源与主线程样本；dropped=0。抽检游戏轨迹段 63069 个采样事件、3720 个分配事件、8 个竞争事件，EventsLost=0。
- 最终进程所有权边界：`f6a6db1e04d04bbb9bb19c9b89d0b014` / `PERFORMANCE-RECORDING-LIFETIME` Passed，37.75 秒；生产录制节点移除/重新安装仍引用同一 PerformanceSession。实际时间线只有一个 start、一个 host_reattached、一个 process_recording_end 和 writer_end，采集器 complete，dropped=0。抽检段 67889 个采样、6375 个分配、13 个竞争事件，EventsLost=0。退出后的 ZIP 导出成功。
- 最终 Release 编译 0 警告/0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。本机诊断配置最终使用 300 秒分段；不重复五分钟等待验证仅时间参数变化。
- 复跑节点边界：先在测试安装启用诊断配置，再运行 `tools/performance/run-visible-steam-benchmark.ps1 -RequestFixturePath coverage/fixtures/runtime/performance-recording-lifetime.json -TimeoutSeconds 120`。这是诊断管线验证，不是对具体快速 SL Mod 的兼容承诺；未复现用户完整两局后的持续卡顿，也未建立诊断开销的完整 A/B 或正式版 FPS 结论。

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

- 目标停止探针最初 `3b410fb57dd342a79fc9392c9bfb8950` / `09a49fc244794146be3db2a9c26d82fc` Failed。建局将故障机器人原生 75 HP 降至 70，被累计战损记录为 5 HP，违反探针的零损收益限制，实际选中无收益路线；这是夹具输入问题。固定 75/75 HP 后达标停止通过，当时记录的夹具名为 `search-objectives-target-stop.json`（整理前已不在当前树中；该段是历史证据）。没有放宽生产血量限制。
- 本轮未修改被提取功能的生产语义；仅修正缩进、剥离局外入口并新增停止回归。最后两次构建只改变测试诊断，既有成功场景不重复运行。
- Release 构建 0 警告／0 错误；SearchObjectiveChecks 227 项、PotionInventoryChecks 21220 项通过；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=86`；CoverageCatalog `--verify-effective --verify-state-fields --verify-state-writes --verify-branch-state-reads` 3035 项通过。
- 复跑入口为 `tools/testing/run-unattended-test.ps1` / `.sh`：成长用 `SEARCH-OBJECTIVES-GROWTH` / DEFECT，手牌 GENETIC_ALGORITHM、STRIKE_DEFECT；资源用 `SEARCH-OBJECTIVES-RESOURCES` / IRONCLAD，手牌 HAND_OF_GREED、STRIKE_IRONCLAD。两者均 ClearRunDeck、ClearPlayerPiles、初始能量 1、格挡 0、敌生命 6，手牌 TreatAsDeckCard=true，120 秒。UI 用成长建局并指定 `SEARCH-OBJECTIVES-UI-LOCALE`。目标停止输入见上述新 JSON，机制和药水输入见 `coverage/fixtures/runtime/profile-*.json`、`smart-potion-inventory-*.json`。原包和末日降临沿用 0.34.8 的命令。本轮没有原包整场质量 A/B、可见帧率或发布结论。

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
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId TEST-SUBJECT-ORIGINAL-REPORT -CheckpointArchivePath <5cc95原包.zip> -CheckpointSelector start -ReplayMode RestoreOnly -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId REPORT-ROUND-DOOM-THRESHOLD-CARD -CharacterId NECROBINDER -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 134 -InitialEnemyMaxHpsJson '[213]' -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 3 -CardsJson '[{"CardId":"END_OF_DAYS","Pile":"Hand","UpgradeLevels":1},{"CardId":"DEFEND_NECROBINDER","Pile":"Draw","Count":7}]' -PowersJson '[{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1},{"PowerId":"DOOM_POWER","Target":"Enemy","Amount":34},{"PowerId":"SLEIGHT_OF_FLESH_POWER","Target":"Player","Amount":13},{"PowerId":"DUPLICATION_POWER","Target":"Player","Amount":1}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
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
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId REPORT-ROUND-NOSTALGIA-STRIKE -CharacterId SILENT -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 200 -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 10 -CardsJson '[{"CardId":"STRIKE_SILENT","Pile":"Hand"},{"CardId":"DEFEND_SILENT","Pile":"Draw","Count":7}]' -PowersJson '[{"PowerId":"NOSTALGIA_POWER","Target":"Player","Amount":1},{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId REPORT-CARDS-PALE-BLUE-ROOT -CharacterId REGENT -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 200 -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 20 -CardsJson '[{"CardId":"DEFEND_REGENT","Pile":"Hand","Count":6}]' -PowersJson '[{"PowerId":"PALE_BLUE_DOT_POWER","Target":"Player","Amount":1},{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId REPORT-ROUND-HOWL-MUSIC-BOX -CharacterId IRONCLAD -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 200 -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 20 -CardsJson '[{"CardId":"DEFEND_IRONCLAD","Pile":"Hand","Count":8},{"CardId":"STRIKE_IRONCLAD","Pile":"Discard"},{"CardId":"HOWL_FROM_BEYOND","Pile":"Exhaust","UpgradeLevels":1}]' -PowersJson '[{"PowerId":"DARK_EMBRACE_POWER","Target":"Player","Amount":1},{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1}]' -RelicsJson '[{"RelicId":"MUSIC_BOX"}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId REPORT-TEMPORARY-STATS -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 100 -ClearAllPowers -PotionChecksJson '[{"PotionId":"FLEX_POTION","TriggerPlayerSideTurnEndAfterUse":true},{"PotionId":"SPEED_POTION","TriggerPlayerSideTurnEndAfterUse":true}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
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
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId MURDER-ROOT-HISTORY -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 100 -ClearAllPowers -ClearPlayerPiles -CardsJson '[{"CardId":"MURDER","Pile":"Hand"},{"CardId":"DEFEND_SILENT","Pile":"Draw","Count":7}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
```

## 2026-09-09：温柔与出牌效果内自动牌（开发中）

- `TENDER-DISCARD-ALL-SLY`：基线 `289cb0270eeb48d7b4ac81e3247c85e3` Failed，23.96 秒，首差异力量预测 -3 / 原生 -2；最终 `94c01fe41a564329a15f54ae62d66dc4` Passed，16.63 秒，验证精密计算与内层 FLICK_FLACK 各触发一次、Fork 完整状态及 T2 属性恢复和计数归零。
- `TENDER-NESTED-SLY` 手动选牌相邻对照最终 `45bf88eef731446cbe7d11d279b2a792` Passed，16.25 秒。旧源码该对照 `fe4d41bbc2ba4e29b9bceb72890c5e80` 已通过，说明错误发生在出牌效果内自动牌历史被父牌扫描的路径。最初 `c77511470ae4469d80038ce74ef7cca3` 因夹具漏放准备而建局失败，只作输入错误记录，不作语义失败基线。
- 两项均比较完整 MoveStateSnapshot/ContinuationStamp、逐实例卡牌、有序牌堆与 Power、敌人状态和 RNG，无正式搜索或整包回放。

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId TENDER-DISCARD-ALL-SLY -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 100 -ClearAllPowers -ClearPlayerPiles -CardsJson '[{"CardId":"CALCULATED_GAMBLE","Pile":"Hand"},{"CardId":"FLICK_FLACK","Pile":"Hand"},{"CardId":"DEFEND_SILENT","Pile":"Draw","Count":7}]' -PowersJson '[{"PowerId":"TENDER_POWER","Target":"Player","Amount":1}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
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
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId STOCK-RESPAWN-HP -CharacterId SILENT -EncounterId AXEBOTS_NORMAL -Ascension 10 -EnemyCurrentHp 1 -InitialEnemyMaxHpsJson '[96]' -ClearAllPowers -ClearPlayerPiles -CardId STRIKE_SILENT -PowersJson '[{"PowerId":"STOCK_POWER","Target":"Enemy","Amount":1}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId STOCK-THORNS-RESPAWN-HP -CharacterId SILENT -EncounterId AXEBOTS_NORMAL -Ascension 10 -EnemyCurrentHp 3 -InitialEnemyMaxHpsJson '[96]' -InitialEnemyMoveIdsJson '["ONE_TWO_MOVE"]' -InitialPlayerBlock 20 -ClearAllPowers -ClearPlayerPiles -CardId STRIKE_SILENT -PowersJson '[{"PowerId":"STOCK_POWER","Target":"Enemy","Amount":1},{"PowerId":"THORNS_POWER","Target":"Player","Amount":3}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
```

## 2026-09-09：PR #67 / #68 / #72 的 Windows 合并验证（下一版本开发中）

- 集成基线 `2c7bee5`，PR heads 分别为 `0baaf74`、`3c9edc9`、`e9e6103`。两个背包检查工具均链接最终唯一生产实现 `ReachableHandValue`。
- `dotnet run --project tools/testing/checks/PreCombatRequestChecks -c Release`：10 项通过；这是替换进程边界的 API 合同，未运行真实战前 worker 关闭/空闲期限测试。
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
- `dotnet run --project tools/RitsuTargetTypeLookupChecks -c Release` 23项通过：实际生产回调、AssemblyLoad/动态晚建失效、失败不发布、live旁路、弱所有权。`dotnet run --project tools/testing/checks/PowerAmountComparisonChecks -c Release` 8项通过：1,568组实际原生方法对照、getter调用顺序、未知IL/内部标签旁路，10万次分配9.6MB→0。
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
- `dotnet run --project tools/testing/checks/StateFingerprintChecks -c Release`：200,005 个边界/混合输入逐步对照旧 128 位公式，通过。相同本地 FullOpts 标量体 107→83 字节；Release 零警告/错误。未测候选正常配置性能、整场原生部署、可见 Steam、Windows 或 IBS/DRAM/伪共享归因。完整配置与局限见[报告](../performance/cpu-microarchitecture-20260908.md)及 JSON。

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
| 双资源背包独立 oracle | `dotnet run --project tools/testing/checks/ReachableHandPotentialChecks -c Release` Passed：10,000随机子集最优解、原递推溢出/大容量边界、常见规模零分配 |
| HAND-POTENTIAL-COSTS 普通 | `be17b695fd114333baa185bc8e21ba1c` Passed：5张可打出牌费用与原生/重复查询相同，含X费用、条件/不可打出卡，根与分支完整状态不变 |
| HAND-POTENTIAL-COSTS 虚空形态 | `6bfe3e401e3e4a0b8b8dea7d259bdcc2` Passed：相同查询只读合同 |
| Steam 可见机甲骑士完整部署 | `2375d506a3ac455e94b6ccf9cefbed96` → `c2c1e8f6ee584fd1bbb5066b1bd98d83`，均Passed，战损8/T7/无药/零重算，27条实际出牌完全相同；初次搜索19.5643→18.7779秒 |
| Steam 可见小啃兽独立进程内存 | `eb56a536a72a4b1a904153f35eafbbc6` → `60f65ab1bddb410da8de39dcfe26fe12`，均Passed，预测零损/T3/14步路线一致；首结果停止，峰值RSS2,957,713,408→2,932,641,792B |

- 所有性能请求仅VeryHigh，固定DOP4/NoGC16GB，未调搜索预算，未启用增量或详细阶段诊断。Headless机甲骑士各预热一次后各取3次，中位数耗时−4.76%/分配−1.80%；大牌组压力与小啃兽各一组同工作量哨兵，非时序指标/动作完全相同。压力场景仍为死亡边界，不是胜利证明。全部runId及数值见[性能报告](../performance/veryhigh-quality-preserving-20260908.md)与其结构化记录。
- Release编译0警告0错误；Bash/PowerShell结构门禁Passed（`search_files=78`）；两端可见脚本语法检查Passed。PowerShell本轮仅语法及结构检查，未在Windows启动Steam。
- 费用合同的100ms收尾搜索不计性能，复现牌组为 `coverage/fixtures/cards/setups/hand-potential-cost-cards.json`；命令见性能报告。可见基准新增 `--request-fixture-path` / `-RequestFixturePath`，完整请求为 `coverage/fixtures/runtime/performance-veryhigh-mecha-native.json`，同报告记录首结果与整场部署两种断言。
- 完整Mod组合的基线在搜索前因未支持的AveMujica subscriber失败（`cd8e58d396344dec8e0b2362f216879b`，120秒超时）。成功可见结果只覆盖原版+RitsuLib+CombatSolver，未延长超时。基线整场采样器在退出时失败而缺少峰值文件，测试结果有效；其整场结束RSS已高于候选记录的VmHWM。Headless复用进程小啃兽曾有+0.07%峰值反向样本，独立可见进程对照未复现；不作全场景逐样本内存保证。

## 2026-09-09：可达手牌估值分配（开发中）

- `dotnet run --project tools/testing/checks/ReachableHandValueChecks/ReachableHandValueChecks.csproj -c Release`：32,551 组与原二维 DP 精确相等，包含空手牌、免费牌、双资源、较大数组回退、负可用资源、重复牌和价值和溢出。测试直接链接生产纯计算源码，原递推作为对照。
- 固定 100,000 次调用的局部分配检查：单资源受限 5,600,000 → 0 B；双资源受限 13,600,000 → 0 B；全部可负担 29,600,000 → 0 B。仅统计 DP 计算，不包含游戏模型、候选费用捕获和整个 Snapshot；不代表整场分配降幅或可见帧率收益。
- 当前可见会话的一条 RESULT 记录：40,105 次总转移、约 2.00 GB 总 worker 分配、2,424 ms 总搜索时间、最大主线程帧间隔 24.6 ms，未记录超过 33 ms 的帧或 GC 暂停。这是在线观察，未作为隔离 A/B 基准。
- 未启动额外游戏进程，未修改运行中会话；固定战斗搜索 A/B、增量回放和可见性能验收未执行。纯估值等价检查不代替这些项目。
