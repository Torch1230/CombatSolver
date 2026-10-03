# CombatSolver 测试入口历史卷 06

## 2026-09-09：战前预测请求选项（开发中）

- 独立回归入口：`dotnet run --project tools/testing/checks/PreCombatRequestChecks/PreCombatRequestChecks.csproj -c Release`，Windows / Linux 相同。直接链接实际 `PreCombatForecastApi` 与 contract 源码，替换游戏/进程边界，不启动游戏。
- 覆盖强制重算绕过运行中/已完成结果、同选项去重、不同关闭/空闲要求分开请求、缓存命中应用关闭/有限或无限空闲期限、脱离式取消、独占取消等待清理、已取消缓存命中不改变设置以及 live 状态过期。
- 上述 10 项独立检查通过，Linux 结构门禁返回 `REFACTOR_BOUNDARIES_OK search_files=78`。同一测试链接修复前 API 时，强制重算用例因没有第二个 worker 请求而超时，缓存生命周期用例因未调用生命周期入口而失败。
- 完整 Mod 构建尝试返回 32 个 `CS0246` 缺失游戏类型错误（包含 `CombatTurnState`、`CardLocation`）；Windows worker 进程回收未验证：本机游戏为 `0.107.1`，仓库要求 `0.111.0`。独立 API 检查不代表游戏模拟或进程生命周期验收。

## 2026-09-09：历史敏感 Power 顺序（0.34.5）

- `MIXED-POWER-ACQUISITION-ORDER` 失败基线 `6eba58023b214ead8829ed5effbad6ee`：旧逻辑在已有小刀/攻击/格挡历史后临时停用并恢复 Power，首个差异为 `P[1] expected Strength actual PhantomBlades`。
- 最终 `8f5c8d96d9e342e8a2163e54e359c0d2` Passed，26.26 秒。夹具先在没有目标 Power 时各打一张格挡牌和小刀，再按代表报告顺序获得幻影之刃、力量、第二个轨道、敏捷、致死性和不动；继续各打一张牌后施加虚弱，完整比较生命、格挡、能量、牌堆、有序 Power、ContinuationStamp、Fork 和父分支隔离。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId MIXED-POWER-ACQUISITION-ORDER -EnemyCurrentHp 100 -HeadlessInstance unexpected-replan-power-order-final`。最终 Release 构建 0 警告 / 0 错误。
- 代表报告 `4b188b2e088c4826ba1b14d0252bd3bf` 只直接核验元数据与独立日志；没有整包恢复或正式搜索。其余 19 份按相同首个 Power 顺序差异和相同执行路径静态归组，不能表述为 20 份整场重放通过。

## 2026-09-09：狂战士回合开始自动出牌历史（0.34.5）

- 代表报告 `c68216599d1a439b972d4161a2135988` 的第 2、3 回合均为 `Y expected=0/0/0 actual=0/0/1`；独立日志确认狂战士在抽牌时自动打出 Strike。只读取代表包日志，没有整包恢复。
- `HELLRAISER-TURN-START-HISTORY`：`5bbb6f1081aa4206ae077b8aff733194` Passed，24.82 秒。从第 1 回合推进到第 2 回合，预测与实机都在抽牌前开始新的历史窗口；狂战士自动出牌后严格比较生命、资源、四个牌堆、有序 Power、Power 内部状态、RNG 与 ContinuationStamp，并显式断言本回合出牌开始次数为 1。
- 关联回归 `NORMALITY-AUTOPLAY`：`f014ee4080bd4191805e67096eef7e8b` Passed，10.51 秒，覆盖同一出牌开始计数的根捕获、Fork 隔离、下一回合归零和被阻止自动牌的牌堆结果。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId HELLRAISER-TURN-START-HISTORY -HeadlessInstance unexpected-replan-power-order-final`；回归只替换 ScenarioId 为 `NORMALITY-AUTOPLAY` 并加 `-EnemyCurrentHp 100`。Release 构建 0 警告 / 0 错误。
- 同组共 9 份 / 7 场；代表报告仅日志直接核验，其余按同一 `Y` 首差异与抽牌自动出牌路径归组。没有把正常 `manual_divergence` 纳入修复数，也没有运行正式搜索或逐包整场部署。




## 2026-09-08：凡庸与自动打牌（0.34.4）

- 原报告 `12f213c23ccd4a00abaf7a80c796273e` 的第 6 回合在发现、彼岸咆哮后打出倾泻，Normality 仍在手；原版结束回合复核为 25 HP，计划为 0 HP。日志定位后直接构造最小夹具，没有运行原包恢复。
- `NORMALITY-AUTOPLAY` 失败基线 `b331a29d04dc4587a797ad2c5074eb78`，23.63 秒：两张防御后打倾泻，模拟格挡 27、原版 10。修复后 `5df3975b9e874fb1a34f5f966dc7f54d` Passed，25.97 秒，比较完整 MoveStateSnapshot/ContinuationStamp，包含有序牌堆、资源、能力和 RNG。
- `NORMALITY-AUTOPLAY-REPLAY`：`21766a0d3d3149d485d09baf36885cf3` Passed，26.44 秒。第一张牌被回响形态重复打出，两次开始加倾泻开始达到三次；验证计数不以“手动动作数”或“已完成次数”代替开始次数。
- 两组还比较：凡庸阻止的自动牌去向、诅咒离手后允许自动牌、预测/Fork/原版重捕获的开始次数、子分支回合清零及父分支保持。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId NORMALITY-AUTOPLAY -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance normality -TimeoutSeconds 120 -ExitOnComplete`；第二组替换 ScenarioId 为 `NORMALITY-AUTOPLAY-REPLAY`。Linux 使用同名 `.sh` 与对应长参数。
- Release 行为构建、Windows 结构门禁及 CoverageCatalog 的 effective/state-fields/autoplay-sources 检查通过。未运行正式搜索、整场部署、原包恢复或可见 FPS/交互验收。

## 2026-09-08：卡牌语言往返刷新（0.34.3）

- `UI-LOCALIZATION`：`1e7e7f53067f4c34a1732b6c5b63c033` Passed，24.96 秒。新增英文已保存 PlanAction / UI snapshot，构造一次真实胶囊，依次切 zhs / eng / zhs，断言标题、升级符号、选牌、tooltip 与游戏译名一致；JSON 往返及旧计划内容保持原样。
- 同时验证销毁控件后订阅数量恢复、语言切换不改变搜索状态或计划外重算计数，并继续通过 eng/zhs/zht 的 321 条文案和 20 类遗物摘要等既有合同。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId UI-LOCALIZATION -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance i18n-refresh -TimeoutSeconds 120 -ExitOnComplete`；Linux 使用对应 `.sh` 与同值长参数。
- Release 行为构建 0 警告 / 0 错误；Windows 结构门禁通过。没有启动真实搜索或整场部署，没有可见交互/排版/帧率验收；只新增显示元数据，不改变模拟结算。

## 2026-09-08：胶囊附加信息本地化（0.34.2）

- `UI-LOCALIZATION` 扩展合同 `43bdbcdfc4eb4bf68c1bd65746412e44` Passed，24.58 秒；eng/zhs/zht 分别覆盖 321 条文案、20 个遗物摘要样本、毒/荆棘/能力规范 ID 与类型名/充能球/敌方行动/未知来源、嵌套选择与空选择、药水标记、遗物胶囊和 tooltip 一致性。
- 捕获显示名后切换实时游戏语言，再在 Task.Run 中读取名称，验证 worker 输出仍使用已捕获语言。第三方自定义摘要样本与纯倍数保持原样。未重跑伤害模拟或整场搜索，改动只涉及显示。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId UI-LOCALIZATION -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance i18n-annotations -TimeoutSeconds 120 -ExitOnComplete`；Linux 使用对应 `.sh` 与同值长参数。
- Release 行为构建 0 警告 / 0 错误，Windows 结构门禁通过。无实机交互/视觉验收或帧率测试。Steamworks 对 schinese、english 的两次描述更新各返回 EResult.OK；仅更新元数据，未上传二进制。

## 2026-09-08：简化中英双语 UI（0.34.1）

- `UI-LOCALIZATION`：`5c12f39867df4f75beb28db5a593e840` Passed，23.72 秒。覆盖 319 条资源的占位符/数字格式、嵌入文本保持原样、eng/zhs/zht 语言选择、英文设置和上传弹窗控件/全部下拉选项无中文、设置切页、上传完成/取消状态、动态路线标题和失败引导。没有发送公网上传请求。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId UI-LOCALIZATION -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance i18n -TimeoutSeconds 120 -ExitOnComplete`。Linux 使用对应 `.sh` 与 `--scenario-id UI-LOCALIZATION --encounter-id BYGONE_EFFIGY_ELITE --headless-instance i18n --timeout-seconds 120 --exit-on-complete`。
- 行为构建 0 警告 / 0 错误，Windows 结构门禁通过，双平台门禁同步禁止 Search 引用 SolverText；之后仅同步版本、文档和结构门禁。没有执行可见布局/交互验收、帧率测量或整场搜索，headless 合同不代表这些项目通过。

## 2026-09-08：战斗独立日志（0.34.0）

- `dotnet run --project tools/testing/checks/DiagnosticLogTests/DiagnosticLogTests.csproj -c Release` 通过：冻结提交前缀、跨战斗摘要化、旧 worker 会话隔离、跑局摘要隔离、积压上限与显式不完整状态。1 万次入队调用约 6.91 ms、93.4 B/次，峰值待写计费 2,817,000 B；仅进程内微基准，不代表实机帧率。
- `COMBAT-DIAGNOSTIC-LOG` 验证正常增量/根回放完整状态相等、人为注入 5 HP 差异后的首个动作定位、Damage/Heal 原生严格差分、失败候选日志与异常传播、独立日志 ZIP。`a5abab57628642d584802ba2d275bfce` Passed，23.23 秒；初次运行因旧归档测试仍要求最多一份全局日志而失败，更新到独立日志合同后通过。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId COMBAT-DIAGNOSTIC-LOG -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance diagnostic-log -TimeoutSeconds 120 -ExitOnComplete`；Linux 用对应 `.sh`、`--scenario-id`、`--encounter-id`、`--headless-instance`、`--timeout-seconds`、`--exit-on-complete`。
- Windows 结构门禁通过。未做可见游戏帧率验收、整场部署或公网上传测试；提交协议未变，此场只导出本地问题包。
- 最终合同 `6a1ff383efa84437a87b8eafc92843d1` Passed，23.37 秒，另覆盖正式搜索的最终路线物化；随后仅同步版本和文档。行为验证构建仍标记 0.33.9，发布构建统一为 0.34.0。

## 2026-09-08：在线监控登录持久化

此服务记录已迁至独立私有仓库的 [历史卷](https://github.com/Torch1230/combatsolver-presence-service/blob/main/docs/archive/history-testing-202609.md)；原文与当时验证范围完整保留。

## 2026-09-08：旧日雕像缓慢跨回合分叉（0.33.9）

- `SLOW-TURN-RESET-FORK` 失败基线 `21070eeed4b84c628ae7ac7f6ebe1cdf`：敌方回合开始后 Fork 抛出 `SlowPower has no fork mapping`；最终 `2602bde5ac1c45f7af0344dc7cd6153a` Passed，24 秒。
- 严格比较完整 MoveStateSnapshot / ContinuationStamp：两次打击累积、敌方阶段清零、清零后分叉、再次打击伤害和动态变量；同时断言能力实例身份、状态指纹相等，以及清零和再次出牌均保持父分支隔离。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId SLOW-TURN-RESET-FORK -EncounterId BYGONE_EFFIGY_ELITE -ClearAllPowers -ClearPlayerPiles -CardsPath coverage/fixtures/cards/setups/slow-turn-reset-fork-0339-cards.json -EnemyCurrentHp 500 -InitialPlayerEnergy 10 -TimeoutSeconds 120 -ExitOnComplete`。Linux 使用同名 `.sh` 和对应长参数。
- 51 份报告 / 40 场战斗的异常详情全部相同，只下载代表包 `c6e67f18952f4d1d9c9d3a4ac304b095`。Preflight 为 materials_valid；原生回放尝试 `0b29cb9ed6044ac9b702e53599111f18` 因 `environment_mismatch:mods` 被拒绝，restorationVerified=false。一次夹具启动使用了不存在的 BigDummy 遭遇名，修正为旧日雕像后取得上述基线。
- 行为源码 Release 构建 0 警告 / 0 错误。本轮没有运行整场自动部署、增量搜索、可见 Steam 或完整发布门禁；最小生命周期差分未启动搜索。

## 2026-09-08：问题包 v2 与 miaovps（0.33.8）

- `REPORT-V2-CONTRACT` 最终 `932296db26ac4e4ebb7b1432e823fddd` Passed，23 秒。覆盖真实 ZIP 导出、新目录与检查点材料、主线程战斗/角色/怪物元数据、未知和正/零/负战损下降值、multipart 字段、响应与取消边界、正式 HTTPS 证书校验上传及回执。命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId REPORT-V2-CONTRACT -HeadlessInstance report-v2 -TimeoutSeconds 120 -PreserveNativeCombatStateForTest -ForceShortSearchOnly`。
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

- 前四场命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId <场景> -ClearAllPowers -ClearPlayerPiles -EnemyCurrentHp 500 -HeadlessInstance report-fixes -TimeoutSeconds 120`。最后一场使用 `-ScenarioId SUMMON-DEATH-POWER-ORDER -EncounterId FABRICATOR_NORMAL -EnemyCurrentHp 500 -HeadlessInstance report-fixes -TimeoutSeconds 120`。
- 比较完整 MoveStateSnapshot / ContinuationStamp；混合能力夹具覆盖多实例与普通能力交替获得、同类多个实例、Fork、子分支移除后重获及父分支隔离。既有重获回归还检查指纹与实际能量重置 Hook，召唤回归覆盖原生跨回合状态。正式搜索未启动，未开启增量搜索验证。
- 最终行为源码 Release 构建通过，0 警告、0 错误；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=77`。一轮启动因子进程未暴露 executable path 失败，未进入行为断言，重新启动后取得上表证据。基线期间一次并发启动被实例锁拒绝，后续请求全部串行复用同一实例。
- 188 份报告去重为 179 场，三类症状分别匹配 98/5/3 场；计数不是逐包通过率。外层 Preflight 超大小限制；拆分代表包 Preflight materials_valid / restorationVerified=false。未执行原包完整恢复、整场自动部署、可见 Steam 验收或完整发布门禁，详见 [分诊](../issues/report-replans-20260908.md)。

## 2026-09-08：Issue #63 局外收益评分上限（0.33.6 开发中）

- `LONG-TERM-RESOURCE-BEAM-CAP` 失败基线 `08689d31bcec4f6ea3bb90ac40cd94d1`：25 点资源加分 625,000，额外自伤 1 HP 后仍高于父状态 565,000 分。
- 最终 `1dc49c905e36411781d7bf0f1ea66b74` Passed，22 秒。直接调用正式 Snapshot，对 1/25/1000 点资源分别比较无伤与自伤分支，验证总加分小于单项 HP 权重、真实资源值保留且未生成成长额度。
- `GROWTH-POLICY-FREE-FIRST`，`e373a06013e24124a17d1f1f3caa375e` Passed，21 秒；`GROWTH-POLICY-PAID`，`d9aca404d377479aa52d334e2f1ed10e` Passed，21 秒。覆盖免费成长优先、零额度拒绝额外战损、允许额度内付费成长、忽略收益开关、第三方额度合同与增量回放。时间来自测试模式，不用于性能结论。
- 新合同命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId LONG-TERM-RESOURCE-BEAM-CAP -HeadlessInstance issue63 -EnemyCurrentHp 100 -TimeoutSeconds 120 -ExitOnComplete`。两项成长用例沿用本文已有命令并加 `-ExitOnComplete`。
- Release 编译通过。Issue 附件仅 Preflight materials_valid，未完整恢复观者 Mod 跑局、未证明该包原先 21 HP 差距已消除；未跑全角色/全量性能基准，不声称有限搜索绝不漏解。0.33.6 保持未发布。

## 2026-09-08：PR #59–65 整合验证（0.33.6 开发中）

- 合并 #59、#60、#61、#62、#64、#65；#63 是 Issue。本节记录本轮直接证据，下方各 PR 原始记录保留其提交时的验证范围。
- Release 构建通过，0 警告、0 错误；结构检查通过，`REFACTOR_BOUNDARIES_OK search_files=77`。
- `GROWTH-POLICY-FREE-FIRST`，`4da884fe105d41a7865ff403988f392b` Passed，22 秒。覆盖第三方成长来源登记、额度/设置往返、Fork、侧栏总开关及免费收益搜索；整合后第三方额度与原版额度共同置灰。
- `PR60-65-CONTRACT`，`348d44e4a0b24db39cbd712f3570e5a0` Passed，22 秒。直接运行死亡补货胜利判定与第三方移除偏置合同，不扩跑整个 Fork 批次。
- 中间运行 `20f2a7b0333444609de93c417ffdf311` 的补货检查已通过，移除测试失败：替身牌通用估值 12 加 -10 为 2，原断言要求负值。测试偏置改为 -20 后通过，生产估值逻辑不变。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId PR60-65-CONTRACT -HeadlessInstance pr5965 -EnemyCurrentHp 100 -TimeoutSeconds 120 -ExitOnComplete`；成长夹具沿用下方 FREE-FIRST 命令并加 `-ExitOnComplete`。
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
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId TURN-START-DAMAGE-SPITE -EncounterId THE_OBSCURA_NORMAL -EnemyCurrentHp 100 -HeadlessInstance obscurafix -TimeoutSeconds 120 -ExitOnComplete`；另一场替换 ScenarioId 为 `TEAR-ASUNDER-DAMAGE-HISTORY`。
- 初始两次夹具尝试 `50e220ad84fc4deebd62fb70189905f4`、`242abeca01374dafb2d42fc937ee1efb` 使用默认 1 HP 敌人，回合开始伤害已结束战斗，未到目标断言；修正回放参数并设置 EnemyCurrentHp=100 后取得有效基线。一次编译缺少测试 ValueProps 引用，补齐后成功。
- 两包 Preflight 为 materials_valid、restorationVerified=false。制造者包声明 0.33.2，顺序差异复用 0.33.3 同根证据，本轮未重复整场测试。未做完整发布门禁或整场零重算结论。

## 0.33.4 在线战斗缓存

- 服务端 8 项测试通过；新增旧协议空心跳保留、0 损保留、新战斗尚未算完时整组保留、完成后整组替换、新协议状态/采集时间、累计时长与超时清除。
- `ONLINE-PRESENCE-CONTRACT` / `FOGMOG_NORMAL`，`84c8fdb51f67414c9cc74355f558f83b` Passed，23 秒。验证原生标量采集、纯缓存转换的非战斗/待计算/新结果/首次空值、关闭持久化、真实 HTTPS 和错误指纹拒绝。仅向正式采集端发送空对象验证 400，没有注入玩家数据；未跑完整战斗生命周期。
- 本地 Playwright 验证缓存敌人及 0 HP、首次计算占位、真实战斗人数为 0、采集时间提示、桌面与 390px 手机布局，无页面异常。

## 0.33.3 召唤与死亡监听顺序

- `SUMMON-DEATH-POWER-ORDER` / `FOGMOG_NORMAL` 失败基线 `b61c2d6e82ce4b7cb9585e1e18e0315a`：T2 完整状态 P[0] 预测 Strength、原生 Illusion，精确复现问题包同根顺序差异。
- 修复后 `749e6fb069d6499da1e00861c2e0fcfa` Passed，34 秒；T1 至 T3 召唤、击杀与复活，每轮完整原生状态和 Fork 对账一致。
- `OVICOPTER_NORMAL`，`beff275eb28c4e52b9c0e88ebd93a615` Passed，38 秒；召唤后击杀一个蛋并推进到 T3，完整原生状态和 Fork 一致。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId SUMMON-DEATH-POWER-ORDER -EncounterId FOGMOG_NORMAL -HeadlessInstance summonorder -TimeoutSeconds 120 -ExitOnComplete`；另一场替换 EncounterId 为 `OVICOPTER_NORMAL`。
- 中间一次请求因构建尚未结束导致冻结 DLL 失败；另一次 `2ff74b0932904eebb65dc1abf745c0a1` 在原生资源预加载期间退出（0xc0000005），尚未进入目标战斗；相同构建重试通过。原问题包仅材料预检有效，未做整场路线恢复或完整发布门禁。

## 2026-09-07 图表聚合与悬停

此服务记录已迁至独立私有仓库的 [历史卷](https://github.com/Torch1230/combatsolver-presence-service/blob/main/docs/archive/history-testing-202609.md)；原文与当时验证范围完整保留。

## 2026-09-07 排行与分页

此服务记录已迁至独立私有仓库的 [历史卷](https://github.com/Torch1230/combatsolver-presence-service/blob/main/docs/archive/history-testing-202609.md)；原文与当时验证范围完整保留。

## 2026-09-07 后台公网 HTTPS

此服务记录已迁至独立私有仓库的 [历史卷](https://github.com/Torch1230/combatsolver-presence-service/blob/main/docs/archive/history-testing-202609.md)；原文与当时验证范围完整保留。

## 0.33.2 新召唤敌人行动

- `LIVING-FOG-SUMMON-INTENT`：失败基线 `9c5be94ee8ae48aeac82d4ef1b42a5d4` 精确复现 EXPLODE_MOVE 无后继异常；最终 `d54fff51d884479bb39176b0fa38d02f` Passed，34 秒。T1 BLOAT_MOVE 召唤至 T2，再推进自爆至 T3，两处完整原生状态、阵容、牌堆、AI、RNG 与 Fork 一致。中间运行的召唤数量断言修正见问题记录。
- 相邻 `RAT-SUMMON-NEXT-INTENT`，`58495b308c9448e3812284678f3cfdab` Passed，31 秒，确认需要首次 Roll 的新召唤双尾鼠仍正常生成意图。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId LIVING-FOG-SUMMON-INTENT -EncounterId LIVING_FOG_NORMAL -HeadlessInstance fogfix -TimeoutSeconds 120 -ExitOnComplete`；相邻用例替换 ScenarioId 为 `RAT-SUMMON-NEXT-INTENT`、EncounterId 为 `TWO_TAILED_RATS_NORMAL`。
- 原问题包只执行 Preflight，未作完整恢复结论。本次不扩展完整发布门禁；在线统计沿用下节同源行为证据。

## 0.33.1 在线统计

- `ONLINE-PRESENCE-CONTRACT` Passed，runId `76c3b303aee842b687562655577b551b`，23 秒。验证默认开启、关闭值序列化持久化、当前角色/楼层/战斗/未知战损标量快照、真实 .NET HTTPS 校验及错误证书指纹拒绝。网络检查发送无个人字段的空对象，预期 400；没有向正式统计写入测试玩家。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId ONLINE-PRESENCE-CONTRACT -HeadlessInstance presence -TimeoutSeconds 120 -ExitOnComplete`。要求私有端点配置；普通无人请求仍完全隔离上报。
- `services/OnlinePresence` 的 `npm test`：3 项通过，包含字段/数值拒绝、鉴权与 Origin、安装标识去重、TTL、重启后聚合历史持久化及限流。
- Playwright 使用真实服务登录与空列表；注入页面级样例后验证桌面/手机布局、折线画布非空、搜索和昵称作为纯文本渲染。样例未发送至正式采集端。未以此声称已观察真实玩家的战斗路线或精确 Steam 人数。
- 正常 Steam 游戏启动后，正式后台收到 1 个带昵称的菜单心跳，角色为空、楼层和战损为 null；没有进入跑局。此行为检查使用版本元数据调整前的 0.33.0 测试构建，行为源码与 0.33.1 相同。

## 0.33.0 发布集成

- PR #57、#58 已合入本批。`PR57-58-STATE-CONTRACT` Passed，runId `e59d8568334c490c9ad9d488288c2ba7`，22 秒。验证污染叠加保持为 4、火花数量变化后同步为 3；隐藏状态槽排序、重复登记拒绝、根捕获委托参数分派、隐藏值变化区分指纹，以及撤销测试登记后恢复原指纹。
- 命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId PR57-58-STATE-CONTRACT -HeadlessInstance logic0907 -CardId DEFEND_IRONCLAD -TimeoutSeconds 120 -ExitOnComplete`。登记测试仅临时使用原版 StrengthPower，finally 清理测试项，不新增生产注销入口。
- PR 集成 Release 编译通过，0 警告、0 错误。旧批次沿用下节既有证据；本次未执行完整发布门禁、117 份原包完整恢复或第三方角色整场适配。隐藏状态根捕获本次验证委托分派，未声称第三方内部状态的完整捕获、Fork 或续用通过。
- 暂停范围与后续调查见 [交接文档](../issues/report-logic-bugs-20260907-handoff.md)。

## 2026-09-07：汇总日志硬逻辑批次

- `RAT-SUMMON-NEXT-INTENT`：基线 `6d34dbb147f143d58baaa535d3c4c997` 新个体下一行动预测 SCRATCH / 原生 DISEASE_BITE；修复后 `370dae413cb94195b2f5143a17132ad9` Passed。正式 EndTurn 回放对照原生下一玩家回合，完整状态、阵容数量、AI 日志与 RNG、Fork 一致。命令：`./tools/testing/run-unattended-test.ps1 -ScenarioId RAT-SUMMON-NEXT-INTENT -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId TWO_TAILED_RATS_NORMAL -TimeoutSeconds 120 -ExitOnComplete`。
- 怪物下一行动遍历修复的相邻回归 `mercury-reattach-boundary-v0111`，`d74dcb906e9543d8b1e336456de303dc` Passed，沿用本节同名命令与完整复活/死亡差分断言；没有扩大测试超时。
- `TOASTY-MITTENS-NINE-CARD-SETUP` Passed，`8cdd213e82be4468aee65b200ffa9fe9`：SILENT 初始牌组、BAG_OF_PREPARATION / TOASTY_MITTENS 的原生开局选择与精确状态激活通过。命令：`./tools/testing/run-unattended-test.ps1 -ScenarioId TOASTY-MITTENS-NINE-CARD-SETUP -HeadlessInstance logic0907 -CharacterId SILENT -EncounterId GLOBE_HEAD_NORMAL -RelicsJson '[{"relicId":"BAG_OF_PREPARATION"},{"relicId":"TOASTY_MITTENS"}]' -StopAfterInitialSetupAssertion -ExpectedInitialSetupChoiceSourceId TOASTY_MITTENS -ShortSearchBudgetOverrideMilliseconds 1500 -DeepSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`。未重现三份原包的全部牌组/遗物组合，不表示原报告解决。
- `FUNERARY-MASK-BEFORE-DRAW`：基线 `2cea9bef9f8d4b9198d1d3bf5859d694` 抽牌堆预测 4/原生 7；修复后 `f9fe9225531b4729930707d0688e344c` Passed，完整状态、随机插入顺序、RNG、Fork 及 turn 2 不再生成通过。命令：`./tools/testing/run-unattended-test.ps1 -ScenarioId FUNERARY-MASK-BEFORE-DRAW -HeadlessInstance logic0907 -CharacterId NECROBINDER -EncounterId GLOBE_HEAD_NORMAL -TimeoutSeconds 120 -ExitOnComplete`。第二个回合条件通过原生 IncrementTurnNumber 注入，不代表完整两回合推进。
- 面具与筹码原生开局 `FUNERARY-MASK-TURN-SETUP`，`a7c54bdfd1e44721b30b184a0a8556b9` Passed，原生选择顺序与精确状态激活通过。命令：`./tools/testing/run-unattended-test.ps1 -ScenarioId FUNERARY-MASK-TURN-SETUP -HeadlessInstance logic0907 -CharacterId NECROBINDER -EncounterId GLOBE_HEAD_NORMAL -RelicsJson '[{"relicId":"GAMBLING_CHIP"},{"relicId":"FUNERARY_MASK"}]' -StopAfterInitialSetupAssertion -ExpectedInitialSetupChoiceSourceId GAMBLING_CHIP -ShortSearchBudgetOverrideMilliseconds 1500 -DeepSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`。停在准备阶段，不作完整战斗结论。
- `TURN-SETUP-REFRESH-TAKEOVER`：基线 `34ed1aaed32a4deca433ce2847c43ff7` 重算期间开始执行旧计划；修复后 `94d006288b364d529ae5a579c5625855` Passed，接管排队、新计划发布、原生选择和精确状态激活通过。正常等待重算后接管的 `TURN-SETUP-REFRESH-NORMAL`，`4fc6aa624a5a46b1b061cb7ec35e362a` Passed。命令：`./tools/testing/run-unattended-test.ps1 -ScenarioId TURN-SETUP-REFRESH-TAKEOVER -HeadlessInstance logic0907 -CharacterId SILENT -EncounterId GLOBE_HEAD_NORMAL -RelicsJson '[{"relicId":"GAMBLING_CHIP"}]' -VerifyTurnSetupManualRefresh -StopAfterInitialSetupAssertion -ExpectedInitialSetupChoiceSourceId GAMBLING_CHIP -ShortSearchBudgetOverrideMilliseconds 1500 -DeepSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`；正常流程仅替换 ScenarioId。固定短搜，停在准备阶段验收，不作整场求解质量结论。
- `CARD-ENERGY-GAIN-COMMAND`：基线 `e8b64cd4c1e44bc598e618f581c7273d` ALIGNMENT 能量预测 12/原生 10；修复后 `0647bc3be5824b7691859854e975b80a` Passed，11 张增能卡逐张完整原生差分通过，含动态增能、附加 Power 与生成牌。命令：`./tools/testing/run-unattended-test.ps1 -ScenarioId CARD-ENERGY-GAIN-COMMAND -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId GLOBE_HEAD_NORMAL -PowerId NO_ENERGY_GAIN_POWER -PowerAmount 1 -PowerTarget Player -TimeoutSeconds 120 -ExitOnComplete`。初始建局 `c0dd9ab626ca404e985c6d9f1ee277e1` 遗漏 ALIGNMENT 的星能，原生无法出牌，未计作语义基线。此组只验证禁止回能命令路径，不宣称原报告全场回放通过。
- `SURROUNDED-STATE-IDENTITY` 扩展评分缓存检查 Passed，`99bb428e0c68471ba8a4c4b0d75d1234`：Crusher THRASH / Rocket CHARGE_UP，正式 Snapshot 左右朝向有不同指纹、预估 HP 与评分；同一 solver 先计算左再右，右值与独立 solver 一致，重新计算左值稳定。保留原朝向状态、续用、Fork 和 10/15 背击断言。命令沿用下方同名场景，增加 `-ExitOnComplete`。未执行 27 份蟹皇旧包的完整回放。
- `MELANCHOLY-OSTY-DEATH` Passed，`05b52a6c87034108996792f3e47ccbd6`：四个牌堆的升级忧郁带 SWIFT 2 / BOUND 3，奥斯提死亡后的完整原生差分、直接/分叉等价、父分支隔离及再次 Fork 通过。命令：`./tools/testing/run-unattended-test.ps1 -ScenarioId MELANCHOLY-OSTY-DEATH -HeadlessInstance logic0907 -CharacterId NECROBINDER -EncounterId GLOBE_HEAD_NORMAL -TimeoutSeconds 120 -ExitOnComplete`。仅证明直接死亡通知，不覆盖报告 `c3f8cf86` 的最终路线回放。首次请求误用了不存在的遭遇 ID（`957f8afbf5914bc08621ee843cf5eaa7`），未进入战斗，不属于语义失败基线。
- `QUEEN-INFERNO-TERMINAL`：基线 `ace01a08d43b49ecbf358cb01fc89556` 清理前能量预测 5/原生 3；修复后 `5123f0ad07074cf9926b2c554bf8dc26` Passed，完整状态和 Fork 相等。使用与 `QUEEN-INFERNO-MINION-DEATH` 相同 CLI 参数，仅替换 ScenarioId；两个敌人均保留注入的 9 HP，通过既有原生 `EndCombatInternal` 观察者在战后回血和清理前取样并等待 CombatEnded。原先战后取样的 HP 77/80 不再作为模拟错误证据。
- `QUEEN-INFERNO-MINION-DEATH` Passed，`5e435cccefad4506a75537c2831c136f`：满血女王预置强化随从行动，炼狱击杀 9 HP 随从，原生/模拟完整状态及 Fork 一致，并检查阵容仅保留原女王实例。命令：`./tools/testing/run-unattended-test.ps1 -ScenarioId QUEEN-INFERNO-MINION-DEATH -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId QUEEN_BOSS -CardId BLOODLETTING -ClearPlayerPiles -EnemyCurrentHp 9 -PowerId INFERNO_POWER -PowerAmount 9 -PowerTarget Player -TimeoutSeconds 120 -ExitOnComplete`。仅证明该最小路径，不证明报告 `387a2e1c` 已修复；放血能量命令修复后的相邻回归 `0a5c33b2f0f34339a14ae343338b87e1` 同样通过。
- `KNOWN-GAMEPLAY-MOD-BOUNDARY` Passed，`aa1ab43130824665a2524a107521b75b`：合成清单 ID 命中、清单改名但程序集名命中均抛出含实际 Mod ID 的 `IncompatibleGameplayModException`；明确拒绝策略优先于中性声明，空集合正常通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId KNOWN-GAMEPLAY-MOD-BOUNDARY -HeadlessInstance logic0907 -CharacterId DEFECT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。Release 零警告/错误并同步本地 mods；没有安装或执行第三方 Mod，也没有声称复现其改牌实现。
- `CYCLE-EXIT-REVOKED-PARENT`：基线 `4f2102226f00454396abd02b421ada79` Failed，子节点已有临时出口观测、父租约随后撤销时，生产准入条件跳过处理，观测残留。修复后 `b07bd11d4dac484390a2badc67d5c8fb` Passed，分别覆盖卡牌/药水/结束回合输入列表，清理失效观测且 tracker 不获得 envelope；沿用原混合顺序、反向顺序、64 候选单出口及其他无效观测合同。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId CYCLE-EXIT-REVOKED-PARENT -HeadlessInstance logic0907 -CharacterId REGENT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。这是合成调度元数据的生产准入条件与 materialization 合同，不是原包整场回放，也不构成 DOP 性能或整场质量结论。Release 零警告/错误并同步本地 mods，Windows 结构门禁通过。
- `NARROW-ORDERED-PILE-CAPACITY`：基线 `ec88461ccf314096955767f71754f2d1` Failed，真实 `RankBest` 抛“Beam 容量不足以保留策略必需分支”。夹具给四张偏折设置不同格挡数值，逐一回放 24 种出牌顺序并结束回合，确保至少 8 种有效状态/预计洗牌顺序，交给 6 宽 Deep 怀表通道。修复后 `808eda35b341442081083729dd9881d1` Passed，结果在既有 6–7 容量内、身份无重复；未饱和通道保留全部候选。可重跑命令：`tools/testing/run-unattended-test.ps1 -ScenarioId NARROW-ORDERED-PILE-CAPACITY -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -CardsPath coverage/fixtures/regressions/reports/report-narrow-ordered-pile-cards.json -RelicsJson '[{"relicId":"POCKETWATCH"}]' -EnemyCurrentHp 80 -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120`。本次运行使用内容相同的内联 `CardsJson`，文件是在通过后固化的输入；这是回放生成候选后的局部政策合同，未宣称原包整场搜索通过。
- 本项短搜索回归 `FEED-THORNS-TERMINAL-TWO-CARDS` 的 `ffc089f076784745a8ccb6217b9b4221` Passed，沿用既有 1500 ms 固定预算与增量验证命令：先防御后狂宴、T1 零损胜利、6 展开/15 转移。Release 编译零警告/错误并同步本地 mods，Windows 结构门禁通过。
- `GAMBLERS-BREW-SLY-ORDER`：基线 `4dd54bb5380e4ebd84497e72ccdf2784` Failed，单张连续反弹被弃牌重抽后，预测仍在手牌，原生已经通过狡猾打出并回到弃牌堆。修复后 `e4411503e76b49fd9d76a8c2ab9a9627` Passed，原生用药的牌堆、伤害、历史与 RNG 完整差分一致。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId GAMBLERS-BREW-SLY-ORDER -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -EnemyCurrentHp 80 -PotionCheckPath coverage/fixtures/regressions/reports/report-gamblers-brew-sly.json -TimeoutSeconds 120`。首次夹具 `77d4c3ed342845809c54b3d4b9bf59d4` 因药水差分分支未采用命令行 CardId，候选缺失；改为在药水夹具中显式注入后才取得目标失败基线。
- `GAMBLING-CHIP-SLY-ORDER` Passed，`47ec379ab7f24176b8010eed8e1ac112`：开局弃牌重抽生产选择处理器与其 Fork 完整结果一致，并与原生 `CardCmd.DiscardAndDraw` 对照通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId GAMBLING-CHIP-SLY-ORDER -HeadlessInstance logic0907 -CharacterId SILENT -CardId RICOCHET -ClearPlayerPiles -EnemyCurrentHp 80 -TimeoutSeconds 120`。本测试冻结选择后直接对照原生组合操作，没有重放遗物 UI。
- `DISCARD-DRAW-SLY-PENDING` Passed，`3e182f6452944415b0e6f1850569e017`：抽牌已完成后才进入狡猾自动牌的挂起选择，处理器仍返回未完成。旧断言要求此时抽牌堆不动，实际固化了错误顺序，现按原版改为检查先抽牌。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId DISCARD-DRAW-SLY-PENDING -HeadlessInstance logic0907 -CharacterId SILENT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。Release 零警告/错误并同步本地 mods，Windows 结构门禁通过；原报告整场及全部 Mod 环境未回放。
- `GALVANIC-GENERATED-POWER`：基线 `4a50b407274a47e98c282bd5992c9522` Failed，生成 `AUTOMATION` 时预测没有苦难，原生为 `GALVANIZED:6`。修复后 `9ef200eadb2945e6949c1257c7987875` Passed，比较原生固定生成、Fork 后完整状态、子分支打出不污染父分支，以及原生打出的能量/Power/HP/牌堆/RNG；原有防御作为技能牌保持无此苦难。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId GALVANIC-GENERATED-POWER -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId GLOBE_HEAD_NORMAL -ClearPlayerPiles -CardId DEFEND_IRONCLAD -TimeoutSeconds 120 -ExitOnComplete`。Release 编译零警告/错误并同步本地 mods。测试直接生成原报告所选能力牌，没有重放工具箱随机选项或原包全部 Mod 整场。
- `NO-DRAW-DARK-EMBRACE`：基线 `be6fc75970c240d293327a0fae047013` Failed，先获得禁止抽牌时预测 5 / 原生 6 张手牌。修复后 `7dcb893a3c034a1d9f3d704561e88438` Passed；反向获得顺序 `DARK-EMBRACE-NO-DRAW` 的 `de4051e5c3e14b89b7bf7fc6c33ca55d` Passed，两者均比较回合末及下一回合准备的完整状态。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId NO-DRAW-DARK-EMBRACE -HeadlessInstance logic0907 -CharacterId IRONCLAD -MonsterMoveChecksPath coverage/fixtures/regressions/reports/report-no-draw-dark-embrace.json -TimeoutSeconds 120`；反向使用 `-ScenarioId DARK-EMBRACE-NO-DRAW -MonsterMoveChecksPath coverage/fixtures/regressions/reports/report-dark-embrace-no-draw.json`。
- `TURN-END-POWER-ORDER-FORK` **基线红复核（2026-10-02，B016/T025 期间）**：在 `origin/main c4e0b47d` 原样源码上同样 Failed（`a07d1fa7a51a4d318307df76e35e9a79`），且与 T025 修复前后落在**同一失败点**（修复后 `3332a40ab1374342ae5087dd86d6da12`）；下方 2026-09 的 Passed 记录已过期。该场景按独立问题处理，不作为 B016 的回归或哨兵。
- `TURN-END-FOCUS-EVOKE-ORDER`：基线 `3df25090138846dfa4c5fed451f77691` Failed（`field=E0.hp expected={49} actual={47}`），修复后 `8761188a82b04f78937d73c0c83d90d4` Passed；ConsumingShadow 先于 Hotfix 时末球激发 10 → 8 的 2 点差异消失（实机恒 47，临时属性回收与末球激发按监听器顺序）。相邻哨兵 `TURN-END-ORB-EVOKE-SENTINEL`（Root Focus=0，两版均 8 点）`e3619d6b522649d1979cdb6de70a2a38` Passed。
- `CARD-CLONE-IDENTITY-CONTRACT` Passed：牌组克隆与战斗副本身份合同（根捕获、材质化预览、写时复制、洗牌回抽牌堆、回手、弃牌—洗牌—抽牌、玩法复制、同名牌转换替换、同名牌实例匹配、传球循环不变量）；反向注入 drop-identity 与 retain-transform 均按预期 FAILED。
- `CARD-CLONE-IDENTITY-CONTRACT` 增补（2026-10-03，B016/T024）：`RolloutOwnership` 三分判定在批次尖端 `a96ae785b45a4a599a3bf0e71b6916b6` Passed（`one-to-one:deckcard=deck=true,copy=deck=false`），即跨回合 roll-out（弃牌→抽牌六轮＋末尾洗牌回抽牌堆）后两张同名 `MAKE_IT_SO` 的牌组身份仍与实机一一对应；在 `AddToPile` 注入非转换清空写点时 Failed（`丢失了永久牌组身份…actual=null`），故该 Passed 是有判别力的否证。续接状态差异改为逐处报出后，同名牌身份互换不再被压成单条 `field=D[n]`（`StampReportsEveryDifferingSameNameIndex`；回退 `ContinuationStamp` 到基点时 Failed，`2bdbf437` 后 Passed）。
- 新增 `ORB-VALUE-NATIVE-HOOK-ESCAPE`（B016/T022）：保留 `-RelicsPath` 注入的原版 `INFUSED_CORE` 并施加原版 `FocusPower`，用 Harmony 计数原生 `Hook.ModifyOrbValue` 的进入次数，再反射调用现场失败路径 `CombatBeamSolver.OrbRetentionValue`。修复后 `f8648f71b4094a29891b78acaf267e74` Passed（进入 0 次、预测球值与实机原生差分一致 `MirrorsNative=6/Retention=6`）；把 `OrbMirrors.ModifyValue` 回退成 `v0.47.2` tag 自身的 `Hook.ModifyOrbValue(simulator.State.CombatState, …)` 形态后 `f900d4ac04924039…` Failed（进入 2 次）。原生值未被两个监听器改变时夹具直接抛错，拒绝退化为空断言。现场包 `3e32722d` 的原始 `EntryPointNotFoundException` 本身仍未在本地复现（现场另载 RandomForeseer/QuickSLButton）。
- B016 哨兵在批次尖端 `11ff9db2` 重测（`INITIAL-TOOLBOX-INFUSED-CORE`、1500ms 固定预算、增量等价、首次准备断言后停止）：`2aa39b44` 1063.54ms / `1f490636` 1039.55ms / `9fa2c5ee` 1018.90ms，展开 128、转移 1773、boundary None、score 与 projectedBattleHpLost 恒定；全部落在 `2bdbf437` 之前记录的 T022 基线区间内（1064.38→1086.46，组内极差 151.39），无新增失败与部署漂移。
- `B016-T021-ROOT-COLLECTION`：根捕获窗口内实机卡牌集合写入必须被具名拒绝；把产品文件回退到基点、测试保持修复后版本时 Failed，修复后 Passed（`CombatRootSnapshot`）。
- `TURN-END-POWER-ORDER-FORK` Passed，`f4584f8b9805476aa9bfa83ad84f3d92`：相反获得顺序的指纹及续用文本不同；Fork 保留指纹、完整结算结果，子分支结束回合不改变父分支；抽牌结果分别为 1 / 0。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId TURN-END-POWER-ORDER-FORK -HeadlessInstance logic0907 -CharacterId IRONCLAD -ClearPlayerPiles -CardsJson '[{"cardId":"DEFEND_IRONCLAD","pile":"Draw"}]' -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120`。
- Power 指纹/续用改为完整有序列表后的相邻回归 `ENERGY-RESET-POWER-ORDER-REAPPLY` Passed，`5cb6fd6a83004397b413dbc71db5b59d`，覆盖移除后重新施加、Fork 与原生能量重置完整状态；命令沿用本节既有场景并加 `-ExitOnComplete`。Release 构建零警告/错误并同步本地 mods，Windows 结构门禁通过。该测试不代表其他 Power 的全部 Hook 时序已审计。
- `NO-DRAW-JOSS-END`：基线 `0b8e95d32a5d452daeb31954b38de364` Failed，下一回合手牌预测 5 / 原生 6；修复后 `53fd32235f674b708eeb851c659bdabb` Passed，回合末消耗、纸钱计数、抽牌与下一回合准备的完整状态一致。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId NO-DRAW-JOSS-END -HeadlessInstance logic0907 -CharacterId IRONCLAD -MonsterMoveChecksPath coverage/fixtures/regressions/reports/report-no-draw-joss-paper.json -TimeoutSeconds 120 -ExitOnComplete`。
- `PLAYER-END-PHASE-TWO-PENDING` **基线红（2026-10-02，B016/T025 期间报告）**：在 `origin/main` 原样源码上同样 Failed（`93079b5473534dccb29382417e91426d`），与 B016/T025 修复无关，建议单独立题；下方 Passed 记录已过期。
- `PLAYER-END-PHASE-TWO-PENDING` Passed，`775c8f5b3a4541609857f36daf4dd968`：常规 Power 抽牌挂起时不访问后续 Power；纸钱抽牌挂起时不推进后续遗物及晚期瓦解伤害。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId PLAYER-END-PHASE-TWO-PENDING -HeadlessInstance logic0907 -CharacterId IRONCLAD -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。最后一次编译只增加测试入口与断言，生产代码沿用上条差分证据；Release 零警告/错误并同步本地 mods，Windows/Bash 结构门禁通过。原报告整场及全 Mod 环境未恢复；禁止抽牌与黑暗之拥的 Power 内部相对顺序另查。
- `BOUND-END-DRAW` 基线 `faa861a3c91747de952da67bcef4c82d` Failed：本回合束缚额度已用尽，回合末黑暗之拥抽到的首张防御预测带 BOUND，原生没有。修复后 `a8cfd38c8b74486d8dbda0763e306ff0` Passed，包含回合末抽牌及下一玩家回合准备的完整状态差分。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId BOUND-END-DRAW -HeadlessInstance logic0907 -CharacterId IRONCLAD -MonsterMoveChecksPath coverage/fixtures/regressions/reports/report-bound-end-draw.json -TimeoutSeconds 120`。
- `BOUND-ROOT-HISTORY` Passed，`315a0f6fc47e466fbba6a0a349a79f92`：捕获根前原生已经施加一次束缚，额度在根中保留，后续抽牌、回合末抽牌和下一回合准备的完整差分通过。命令沿用上条，改 `-ScenarioId BOUND-ROOT-HISTORY -MonsterMoveChecksPath coverage/fixtures/regressions/reports/report-bound-root-history.json -ExitOnComplete`。
- `BOUND-COUNTER-FORK` Passed，`bcccb419227141b587959bd39455ddcc`：父/子分支计数 1/2 互不污染，指纹区分已用额度，下一玩家回合重新计数且不修改父分支。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId BOUND-COUNTER-FORK -HeadlessInstance logic0907 -CharacterId IRONCLAD -PowerId CHAINS_OF_BINDING_POWER -PowerAmount 3 -PowerTarget Player -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。最后一次编译只新增此测试入口，前两项生产行为证据继续有效；构建已同步本地 mods，结构门禁通过。原报告的全部 Mod 和整场路线未回放。
- `PAPER-CUTS-THORNS-BOUNDARY` 基线 `78d357172eef4291927b3f9772af622e` Failed，最大生命预测 68 / 原生 70。修复后 `aaf903a158354b1c83ddbb3c097cb21d` Passed：卷轴在承伤前反伤中死亡，已开始的攻击继续命中，纸割退出普通回调；怪物行动完整状态差分通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId PAPER-CUTS-THORNS-BOUNDARY -HeadlessInstance logic0907 -CharacterId SILENT -EncounterId SCROLLS_OF_BITING_NORMAL -MonsterMoveChecksPath coverage/fixtures/regressions/reports/report-paper-cuts-thorns-boundary.json -TimeoutSeconds 120`。原报告整场及全部 Mod 未重放。
- 活动监听视图改动的死亡相邻回归：`DEATH-EFFECTS-ONCE` 的 `ac8a9bf19cae4502a7b3851124903b5c` Passed；`mercury-reattach-boundary-v0111` 的 `7139a36826034e0daae2f9e36c40e6ff` Passed，包含复活、再次死亡、直接/Fork/重新捕获根和清理前原生完整状态。沿用本节对应场景命令，最后一项加 `-ExitOnComplete`。Release 编译零警告/错误并同步本地 mods，结构门禁通过。
- `ENERGY-RESET-POWER-ORDER-REAPPLY` 基线 `5b0b96d193af4a7ba02fc8592f7c38d3` Failed：根中先有引雷能力，移除再获得后预测仍使用旧位置，球序继续颠倒。修复后 `017caab7d1164ae0907c7e0e1b912765` Passed，原生移除/施加、Fork 与能量重置后的完整状态一致。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId ENERGY-RESET-POWER-ORDER-REAPPLY -HeadlessInstance logic0907 -CharacterId DEFECT -CardId DEFEND_DEFECT -ClearPlayerPiles -EnemyCurrentHp 80 -TimeoutSeconds 120`。首次夹具 `412ac9f548ec45a49bb8e4a05226fa1d` 因直接施加后未结算 Power 变化事件，在 Fork 处失败；补上生产 `ResolvePowerAmountChanges` 后才取得目标失败基线。
- `ENERGY-RESET-POWER-ORDER-OVERFLOW` Passed，`a37f9fb36ac1481db00873d62a59360b`：已有闪电球，依次生成 3 个玻璃球和 1 个闪电球，覆盖满槽激发。逐球状态、敌方伤害、RNG、Power 与资源的完整原生差分及 Fork 通过。命令沿用上条并改 `-ScenarioId ENERGY-RESET-POWER-ORDER-OVERFLOW -ExitOnComplete`。
- `ENERGY-RESET-POWER-ORDER` 基线 `cb45a70d288f46a6afa301c08a721ccb` Failed：先施加 `SPINNER_POWER`、后施加 `LIGHTNING_ROD_POWER`，预测球序为闪电/闪电/玻璃，原生为闪电/玻璃/闪电。最终 `daddd03c11b54f02a99780ddabfcee0e` Passed；反向顺序 `7757429a90964e28b5b827e298e125c3` Passed。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId ENERGY-RESET-POWER-ORDER -HeadlessInstance logic0907 -CharacterId DEFECT -CardId DEFEND_DEFECT -ClearPlayerPiles -EnemyCurrentHp 80 -TimeoutSeconds 120`，反向使用 `-ScenarioId ENERGY-RESET-POWER-ORDER-REVERSE -ExitOnComplete`。
- 上述最终夹具在原生 `Hook.AfterEnergyReset` 上对照完整状态，同场覆盖 Genesis/StarNextTurn/Radiance 的星能、能量和层数变化；验证不同获得顺序具有不同分支指纹及续用文本，Fork 保持顺序。满球槽激发与重新获得能力由本节追加夹具覆盖；原包整场回放仍未运行。
- `REPLAY-START-HISTORY` 基线 `0b32fe311c324ba2a83ff026aff42b26` Failed：带 `GLAM` 的切割原生执行两次，预测 `Y=0/1`、原生 `Y=0/2`。最终 `930567f5a18640c39a78c00363d84474` Passed：完整状态差分、Fork、重新捕获根的零费攻击 2 次、出牌系列 1 次、手动操作 1 次一致。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId REPLAY-START-HISTORY -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -CardsJson '[{"cardId":"SLICE","pile":"Hand","enchantmentId":"GLAM"}]' -EnemyCurrentHp 80 -TimeoutSeconds 120 -ExitOnComplete`。
- `REPLAY-START-HISTORY-ECHO` Passed，`1c5954b23be5482497b42d66e6503899`：两层回响形态下依次打出带重放附魔和普通切割，分别执行 3 次和 2 次；逐动作完整原生差分、Fork 与重新捕获根的两种计数通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId REPLAY-START-HISTORY-ECHO -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -CardsJson '[{"cardId":"SLICE","pile":"Hand","enchantmentId":"GLAM"},{"cardId":"SLICE","pile":"Hand"}]' -PowerId ECHO_FORM_POWER -PowerAmount 2 -PowerTarget Player -EnemyCurrentHp 80 -TimeoutSeconds 120`。早期只移除总计数门的方案曾通过普通重放 `3d517d24a8304d0d80dda0d493ecfe85`，静态追踪发现会改变回响形态的系列计数，最终拆清语义并重新验证；不以早期通过结果代表最终代码。
- `DEATH-EFFECTS-ONCE` 基线 `840bc517634f47a3815afc1e95bb4ecf` Failed：使用不同局部集合再次通知同一死亡，尸蛞蝓力量由 4 变为 8。修复后 `f153493cbdb5422fa167c7cf384b9cba` Passed：正式打击回放、重复通知、Fork 后通知及原生打击完整状态差分通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId DEATH-EFFECTS-ONCE -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId CORPSE_SLUGS_WEAK -CardId STRIKE_IRONCLAD -ClearPlayerPiles -EnemyCurrentHp 6 -TimeoutSeconds 120`。该夹具直接覆盖跨调用集合的重复通知，未重放原报告的完整球动作链。
- 死亡去重的相邻复活边界 `61831e7dc4ec47b985ecc6f8590bf2cd` Passed：`REATTACH_MOVE` 与后续 `DEAD_MOVE` 的直接、Fork、重新捕获根及原生清理前完整状态差分通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId mercury-reattach-boundary-v0111 -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId DECIMILLIPEDE_ELITE -TimeoutSeconds 120 -ExitOnComplete`。
- `FEED-THORNS-TERMINAL-TWO-CARDS` 基线 `e81a73db90aa499b9481e90087989c76` Failed：狂宴后继续展开防御，报“回放包含已锁定战斗终局之后的动作”。修复后 `1772dde4c0234433b3c0f0ef756916a8` Passed，先防御后狂宴、T1 零损获胜，6 节点/15 转移，增量回放通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId FEED-THORNS-TERMINAL-TWO-CARDS -HeadlessInstance logic0907 -CharacterId IRONCLAD -ClearPlayerPiles -CardsJson '[{"cardId":"FEED","pile":"Hand"},{"cardId":"DEFEND_IRONCLAD","pile":"Hand"}]' -EnemyCurrentHp 10 -InitialPlayerHp 1 -InitialPlayerEnergy 2 -PowerId THORNS_POWER -PowerAmount 2 -PowerTarget Enemy -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialUnmirroredCount 0 -ExpectedInitialFirstActionCardId DEFEND_IRONCLAD -ExpectedInitialFinalEnemyHpAtMost 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -ExitOnComplete`。
- `FEED-THORNS-TERMINAL-DIFFERENTIAL` Passed，`4881c50d0c164d8f957bf71df61cf096`：唯一狂宴动作致命反伤后恢复正 HP，模拟保持 Defeat；原生 PendingLoss 结束战斗，结束事件中的完整状态差分通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId FEED-THORNS-TERMINAL-DIFFERENTIAL -HeadlessInstance logic0907 -CharacterId IRONCLAD -CardId FEED -ClearPlayerPiles -EnemyCurrentHp 10 -InitialPlayerHp 1 -InitialPlayerEnergy 1 -PowerId THORNS_POWER -PowerAmount 2 -PowerTarget Enemy -TimeoutSeconds 120`。首次差分 `9f1fb8000d2a4b38941f661f9aad7ba7` 在敌方已移出 roster 后按下标取敌人失败，修正测试为保留原始 Creature 身份；首次搜索探针 `6d1a4a7f854e4bb5b53999cf07d64328` 因 CardsJson 覆盖 CardId，仅注入防御，不能作为目标验证。
- `DISTILLED-CHAOS-VOID-FORM-BOUNDARY`：基线 `d470307bcc5b445f8f9e7546fb2bd577` 在药水后的 EndTurn Fork 报未结算结束请求；修复后 `bdd86b08c8ea41df963c0031fe8ccdcf` Passed，32 节点/50 转移、1 瓶药水、未镜像项 0，增量回放通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId DISTILLED-CHAOS-VOID-FORM-BOUNDARY -HeadlessInstance logic0907 -CharacterId REGENT -CardId DEFEND_REGENT -ClearPlayerPiles -CardsJson '[{"cardId":"VOID_FORM","pile":"Draw"}]' -EnemyCurrentHp 80 -InitialPlayerEnergy 0 -PotionId DISTILLED_CHAOS -PotionPolicyForTest RequireAtLeastOne -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialUnmirroredCount 0 -ExpectedInitialPotionCount 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120`。
- `potion-forced-turn-terminal-v0111` Passed，`3bd9d757e90a4e8a9e90975904a1a651`：唯一药水动作自动打出 `VOID_FORM`，T+1 沙漏击杀的根回放、增量回放、Fork、释放后快照、正式标注及原生清理前完整状态差分通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId potion-forced-turn-terminal-v0111 -HeadlessInstance logic0907 -CharacterId REGENT -TimeoutSeconds 120 -ExitOnComplete`。本夹具沿用既有强制结束终局差分工具，不运行正式搜索，不加入无效增量搜索开关。
- `ROOT-CAPTURE-ACTION-BARRIER`：基线 `d01150ef49014dc8ba1931a8802992e6` Failed，真实 `BeforeActionExecuted` 期间调用搜索立即建立了搜索会话。修复后 `00f8c4d6237944dbb95061f695edc44c` Passed，队列执行期间不捕获，原生防御结算后延迟请求完成搜索。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId ROOT-CAPTURE-ACTION-BARRIER -HeadlessInstance logic0907 -CharacterId SILENT -CardId DEFEND_SILENT -ClearPlayerPiles -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`。该最小合同没有重建原报告的全部 Mod、后台回收和帕尔军团动画时序。
- 材料预检：`95e19fb8` 报告为 `materials_valid`。原生 `RestoreOnly` 请求 `4f52cb972f4447c5b87dcd4b1b3a9f2e` 因 `environment_mismatch:mods` 失败；本机与原报告 Mod 集合不同，保持严格拦截，未宣称原包恢复成功。
- `SURROUNDED-STATE-IDENTITY` 基线 `bfa9cc597f6647929f193c9aac98f163` Failed：左右朝向产生相同指纹。修复后 `e25cc4a6d62841ed97bcc3b179e1361a` Passed：状态指纹与 continuation 区分朝向、Fork 修改不回写父分支、背击预测为 10/15。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId SURROUNDED-STATE-IDENTITY -HeadlessInstance logic0907 -EncounterId KAISER_CRAB_BOSS -CharacterId SILENT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120`。
- `SURROUNDED-POTION-DIFFERENTIAL` Passed，`99ee091bfceb42509e49cc62b78c3d7d`：虚弱药水转向左侧的 actual/simulated 严格差分与明确朝向断言通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId SURROUNDED-POTION-DIFFERENTIAL -HeadlessInstance logic0907 -EncounterId KAISER_CRAB_BOSS -CharacterId SILENT -PotionCheckPath https://github.com/Torch1230/CombatSolver/blob/fe3edd2f7b4f3a92b266e6b13293810d31ce2e1b/coverage/fixtures/powers/power-lifecycle-batch-051-surrounded-potion.json -TimeoutSeconds 120 -ExitOnComplete`。
- Release 编译零警告/错误，Windows 结构门禁通过，默认构建同步本地 mods。该合同与单步差分不替代完整蟹皇战斗或整批报告验收。

## 0.32.0 定版

行为源码沿用下列成长策略与点击外部保存的通过证据，本次仅同步版本和发布文档，不重复行为场景。PR #22 按维护者决定关闭，其强制收益逻辑与精进成长未纳入版本；PR #56 既有适配入口随本版发布。最终产物从版本提交执行 Release 构建并同步本地 mods，发布使用最小 ZIP；未执行完整发布门禁或可见 Steam 人工验收。

## 2026-09-07：成长设置分离与点击外部保存

- “提前结束搜索的战损阈值”回到常规设置的求解器区域；成长侧栏只编辑成长额度。沿用原设置字段及成长优先策略。
- Release 默认构建零警告/错误，并通过项目 `CopyMod` 目标部署至本地游戏 `mods/CombatSolver`。
- `GROWTH-POLICY-FREE-FIRST` Passed，runId `c8c16e7ea30b4c3095709f79f8ab7338`，复跑使用下节同名命令并加 `-ExitOnComplete`。新增 UI 合同通过：成长输入文本设为 7，外部鼠标按下后失焦、SpinBox 应用且设置保存为 7；打开设置后将阈值输入设为 19，外部鼠标按下后失焦并保存为 19。侧栏边界、互斥、配置重载与零损优先成长检查同时通过。
- 测试调用真实面板输入处理器与 Godot 失焦信号，未进行可见 Steam 人工点击验收。

## 2026-09-07：局外成长策略（未发布）

Release 编译 `-p:CopyModOnBuild=false` 零警告/错误；Windows 结构门禁通过（`search_files=74`），两份修改过的 Bash 入口语法检查通过。所有请求使用隔离的 `growth` headless 实例，超时 120 秒，短搜预算 1500ms；搜索测试开启增量回放，时间数据不代表生产性能。

| 场景 | 结果 |
|---|---|
| `GROWTH-POLICY-FREE-FIRST` | Passed，`4545ca8b2d464137a35f567f77ccccd9`。零额度下遗传算法优先于更快的零损击杀；跨回合路线保留成长。八类配置默认值、序列化往返、不可变捕获、侧栏重载/开关/边界/互斥、分支计数隔离与累计额度检查通过。 |
| `GROWTH-POLICY-PAID` | Passed，`047d44398b1341019670ec6f18b0d756`。去掉初始格挡，零额度拒绝付血成长；额度 100 时取得成长且实际比零额度多损血，额外战损未超过额度。比较合同另检验额度边界及超额拒绝，胜利优先于成长。 |
| `GROWTH-REPLAY-COUNT` | Passed，`5075d66cd698428b8bf1fea9137102e0`。遗传算法附魔重放，两次成功成长计数为 2；先成长再击杀，T1 零损；5 节点/12 转移。 |
| `GROWTH-FATAL-PRIORITY` | Passed，`a23b3500f1a548af9564dec9f0e9162c`。贪婪之手与打击都可零损击杀时选择前者，收益次数 1；4 节点/12 转移。 |
| `GROWTH-NO-TARGET-POTION-SENTINEL` | Passed，`3d2008f608604f3785d41f8aead373cd`。无成长目标时仍按强制药水政策使用火焰药水，T1 零损、1 瓶、收益次数 0、未镜像项 0；1 节点/2 转移。 |

复跑入口：

```powershell
./tools/testing/run-unattended-test.ps1 -ScenarioId GROWTH-POLICY-FREE-FIRST -HeadlessInstance growth -CharacterId DEFECT -ClearRunDeck -ClearPlayerPiles -CardsJson '[{"cardId":"GENETIC_ALGORITHM","pile":"Hand","treatAsDeckCard":true},{"cardId":"STRIKE_DEFECT","pile":"Hand","treatAsDeckCard":true}]' -EnemyCurrentHp 6 -InitialPlayerEnergy 1 -InitialPlayerBlock 99 -VerifyGrowthPolicy -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120
# 付费场景使用同一参数，改 ScenarioId 为 GROWTH-POLICY-PAID，InitialPlayerBlock 为 0。
./tools/testing/run-unattended-test.ps1 -ScenarioId GROWTH-REPLAY-COUNT -HeadlessInstance growth -CharacterId DEFECT -ClearRunDeck -ClearPlayerPiles -CardsJson '[{"cardId":"GENETIC_ALGORITHM","pile":"Hand","treatAsDeckCard":true,"enchantmentId":"GLAM"},{"cardId":"STRIKE_DEFECT","pile":"Hand"}]' -EnemyCurrentHp 6 -InitialPlayerEnergy 2 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialGrowthRewardCount 2 -ExpectedInitialFirstActionCardId GENETIC_ALGORITHM -ExpectedInitialProjectedBattleHpLost 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120
./tools/testing/run-unattended-test.ps1 -ScenarioId GROWTH-FATAL-PRIORITY -HeadlessInstance growth -CharacterId IRONCLAD -ClearRunDeck -ClearPlayerPiles -CardsJson '[{"cardId":"HAND_OF_GREED","pile":"Hand"},{"cardId":"STRIKE_IRONCLAD","pile":"Hand"}]' -EnemyCurrentHp 6 -InitialPlayerEnergy 2 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialGrowthRewardCount 1 -ExpectedInitialFirstActionCardId HAND_OF_GREED -ExpectedInitialProjectedBattleHpLost 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120
./tools/testing/run-unattended-test.ps1 -ScenarioId GROWTH-NO-TARGET-POTION-SENTINEL -HeadlessInstance growth -CardId DEFEND_IRONCLAD -ClearRunDeck -ClearPlayerPiles -InitialPlayerEnergy 0 -EnemyCurrentHp 20 -PotionId FIRE_POTION -PotionPolicyForTest RequireAtLeastOne -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialFirstActionPotionId FIRE_POTION -ExpectedInitialPotionCount 1 -ExpectedInitialGrowthRewardCount 0 -ExpectedInitialFinalEnemyHpAtMost 0 -ExpectedInitialUnmirroredCount 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -ExitOnComplete
```

迭代中曾命中原有零损早停；已在成长目标存在时关闭。无格挡且额度 2 的初始夹具未选成长，不能作为免费收益验证，后改为注入格挡和独立付费场景。`7f4963d71e11400aa1f9aaf8ab10beec` 因选择了等待正式搜索结果的停止参数而超时，改为根断言后停止；`e34830eb59864bc280802f82bbd7465c` 因 UI 测试未创建 overlay 失败，测试入口现先创建 overlay。编译期修复了类型名遮蔽、空值注解与测试 runner 实例调用；首次 Bash 路径错误后定位实际安装位置通过。

未做八类卡牌逐一原生部署或本轮 actual/simulated 全量差分；计数合同与增量回放不替代原生结算验收。未做可见 Steam UI 检查、重启游戏后的人工设置回读或性能基准，headless 只证明结构状态与设置序列化。本批未复制开发 DLL 到正常游戏目录，未发包或上传。

## 2026-09-07：文档目录整理

L0 文档检查：64 份资料归类移动，增加 8 份导航；整理后共 103 个文档与数据文件，258 个本地文件链接均可解析，全部文件可从文档总入口到达。源码、编译配置和运行行为未变，本轮未构建或启动游戏。

## 2026-09-07：PR #56 合并验证（未发布）

- Release 编译 `-p:CopyModOnBuild=false` 零警告/错误；Windows 结构门禁通过，`search_files=73`。
- `PR56-CARD-CHOICE-REGRESSION` Passed，runId `efc7007a3dc44012b65dafcb0a3e2ff3`：原版两种可选选牌的空选严格差分通过。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId PR56-CARD-CHOICE-REGRESSION -HeadlessInstance pr56 -MonsterMoveChecksPath https://github.com/Torch1230/CombatSolver/blob/fe3edd2f7b4f3a92b266e6b13293810d31ce2e1b/coverage/fixtures/cards/card-on-play-batch-042-choice-zero-optional.json -EnemyCurrentHp 100 -TimeoutSeconds 120 -ExitOnComplete`。
- 未运行第三方许愿的登记委托、三选一实际结算或原生页面部署；原版回归不等于第三方效果验收。本次仅合并源码，保持已发布 `0.31.3` 的产物与标签。

## 0.31.3 定版

PR #49 直接合同在本机 RitsuLib `0.5.19` 上通过全部 10 项：当前真实回调匹配、静态正负查询、live 旁路、动态晚创建、并发、可卸载程序集与模拟后恢复。100000 次缺失类型查询的合同测量为 `18400000 -> 0` 字节，仅表示该查询，不代表整场性能。Windows 结构门禁通过。

本版本收录 PR #49–#55 和已定版 `0.31.2` 元数据。发布源提交 `d71ca2d` 的最终 Release 构建零警告/错误。PR #50–#55 与战前 API 的既有证据见下方；未执行完整发布门禁或可见 Steam 性能 A/B。

- `PR49-FIRE-POTION-0313` Passed，runId `c5c6183f6d424fbd84596ab86e8bef74`：强制火焰药水，Short1500ms，增量回放；1 节点/2 转移，首动作使用目标药水，1 瓶、T1 敌 HP0、未镜像项0。首请求 `3e1a71fc7c24497594eeb81b165475b7` 因空 `CardId` 在建局时报错，改为零能量的防御牌后通过，行为源码未改。
- `PR49-POTION-DIFF-0313` Passed，runId `41a2a580df544528b1585143e5829f3e`：复用同一 headless 进程，火焰药水对敌目标与结算严格差分，最后请求 `-ExitOnComplete` 退出。

```powershell
./tools/testing/run-unattended-test.ps1 -ScenarioId PR49-FIRE-POTION-0313 -HeadlessInstance release0313 -CardId DEFEND_IRONCLAD -ClearPlayerPiles -InitialPlayerEnergy 0 -EnemyCurrentHp 20 -PotionId FIRE_POTION -PotionPolicyForTest RequireAtLeastOne -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialFirstActionPotionId FIRE_POTION -ExpectedInitialPotionCount 1 -ExpectedInitialFinalEnemyHpAtMost 0 -ExpectedInitialUnmirroredCount 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120
./tools/testing/run-unattended-test.ps1 -ScenarioId PR49-POTION-DIFF-0313 -HeadlessInstance release0313 -PotionCheckPath https://github.com/Torch1230/CombatSolver/blob/fe3edd2f7b4f3a92b266e6b13293810d31ce2e1b/coverage/fixtures/potions/potion-batch-044-fire.json -TimeoutSeconds 120 -ExitOnComplete
```

## 2026-09-07：PR #50–#55 合并验证

本轮验证六条 PR 合并后的行为源码。Release 编译零警告/错误，Windows 结构门禁、Git Bash `bash -n tools/testing/run-unattended-test.sh`、CoverageCatalog `--verify-effective --verify-pre-play-choices --verify-combat-choices` 均通过。覆盖目录检查限原版目录，生成的时间戳变化未提交。

| 场景 | 结果与证据 | 复跑参数（共同使用 `tools/testing/run-unattended-test.ps1 -HeadlessInstance pr50-55 -TimeoutSeconds 120`） |
|---|---|---|
| `PR50-55-GAMBLERS-REGRESSION` | Passed，runId `3fc6e575501d4b9596576c5167466cdb`；赌博药水弃牌与补抽严格差分 | `-ScenarioId PR50-55-GAMBLERS-REGRESSION -PotionCheckPath https://github.com/Torch1230/CombatSolver/blob/fe3edd2f7b4f3a92b266e6b13293810d31ce2e1b/coverage/fixtures/potions/potion-batch-045-gamblers.json` |
| `PR50-55-OPTIONAL-CHOICE` | Passed，runId `28167ce3490c41d2bf04bc603732c637`；两种可选选牌空选的严格差分 | `-ScenarioId PR50-55-OPTIONAL-CHOICE -MonsterMoveChecksPath https://github.com/Torch1230/CombatSolver/blob/fe3edd2f7b4f3a92b266e6b13293810d31ce2e1b/coverage/fixtures/cards/card-on-play-batch-042-choice-zero-optional.json -EnemyCurrentHp 100` |
| `PR50-55-CLASH-PLAYABILITY` | Passed，runId `376b830cdecb499aa4a9c0fe9a7a527e`；先出防御再出 Clash，2 动作、2 节点/4 转移、T1 零战损、未镜像项为 0，增量回放通过 | 见下方完整参数 |

```powershell
./tools/testing/run-unattended-test.ps1 -ScenarioId PR50-55-CLASH-PLAYABILITY -HeadlessInstance pr50-55 -TimeoutSeconds 120 -CardId "" -ClearPlayerPiles -CardsJson '[{"cardId":"CLASH","pile":"Hand"},{"cardId":"DEFEND_IRONCLAD","pile":"Hand"}]' -EnemyCurrentHp 10 -InitialPlayerEnergy 1 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialFirstActionCardId DEFEND_IRONCLAD -ExpectedInitialExecutableActionCountAtLeast 2 -StopAfterInitialSolverResultAssertion
```

首次 Clash 请求 `990db5f805574a8c8c050d44a0fa136a` 因同时要求卡牌 ID 与标题的测试参数被单独使用而失败；改为首动作与动作数断言后通过，行为源码未改。首次 Bash 语法检查使用了不存在的安装路径，定位本机 Git Bash 后通过。

上述用例证明原版相关通道回归通过。第三方战略估值委托、形态药剂、预视弃牌和未登记第三方可打出条件的专属夹具，本轮未执行；PR 作者提供的观者结果仍为作者历史证据。未运行可见 Steam 联动或完整战斗回归。测试实例已停止。

## 2026-09-07：Ritsu目标类型查询缓存

10项直接合同覆盖静态正负查询、live旁路、动态晚创建、并发和程序集卸载。固定0.31.0研究基线的Headless目标分配−34.4%、可见Steam−33.1%，完整动作/126非时序字段一致；Aeon哨兵行为一致但分配+4.5%，保留未解释限制。当前PR基于main `0552b33`，不将历史A/B标为新上游政策下的结果；详见 [验收与复现](../performance/metadata-target-type-cache-20260907.md)。

## 0.31.2 定版

收录 PR #15、#18、#43。本次仅修改版本与发布资料，沿用下列已完成的定向验证，执行最终 Release 构建；未追加完整发布门禁或可见 Steam 双 Mod 联动验收。
## 2026-09-08：SeedOracle 规划快照 API v6

伴生 SeedOracle 的 `smoke/run-planning-smoke.ps1` 调用真实独立 worker。`PLANCOMBAT01` 普通战斗、`PLANCOMBAT02 -SimulationCase unknown` 问号点战斗、`PLANCOMBAT03 -SimulationCase event -SimulationEvent DenseVegetation` 多页事件回血后战斗，均 3/3 完成且严格恢复检查通过；样本结果可写回规划，主进程状态/RNG/地图指纹不变。单样本短搜 2000ms、整体 30000ms，批次上限 120 秒。

报告保存在伴生仓库 `docs/validation/planning-combat-2026-09-08.md`。没有逐个端到端验证所有事件或作可见 Steam 性能结论；失败或未完成的样本不作为规划参照。

补测 `PLANCOMBAT04` 木偶事件战斗后原生 Resume、奖励重放与 `PLANCOMBAT05` 普通战斗各 3/3 完成；累计 15 个样本。伴生面板 Debug 自检通过。完整 AutoSlay 在 120 秒内未完成，未计为完整跑局通过；无头正常退出仍报告 Godot 资源释放告警。

## 2026-09-06：PR #43 集成

`PR43-PRECOMBAT-API-INTEGRATION`，runId `d44c83b14da04695b79f218e5056d32d`，68.0秒 Passed：规范化恢复、独立 Mod 文件、设置令牌、取消、确定/假设预测、2次 worker 创建和3次复用、静音与主跑局不变。`PR43-EXIT-CLEANUP`，runId `b2d1e2d25beb47eeb976f8062657a4a3`，18.9秒 Passed，另查正常退出后 startup-mods 已清除。Seed Oracle main `29cee875` 对新 DLL 编译通过。详见 [审查记录](../pr/pr43-review.md)；未执行可见 Steam 双 Mod 联动。作者原 0.29.x 测试数据保留为历史证据。

复跑：`tools/testing/run-unattended-test.ps1 -ScenarioId PR43-PRECOMBAT-API-INTEGRATION -HeadlessInstance pr-api -VerifyPreCombatForecastApi -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -StopAfterInitialSolverResultAssertion -HeadlessFastModeForTest Instant -TimeoutSeconds 120 -ExitOnComplete`。Bash 对应 `--verify-pre-combat-forecast-api`，其余使用现有 kebab-case 参数。

## 2026-09-06：PR #18 第三方 OnPlay 边界

`PR18-FOREIGN-ONPLAY-BOUNDARY` Passed，runId `c374c02c64a34423b8f99a1da976a7b0`：真实 Harmony Prefix 安装/卸载，覆盖首次根捕获后新增补丁、已声明非玩法来源、未知来源、移除补丁后的正常捕获及 live 状态不变。既有 `PredictionFailureBoundaries` 和首结果增量短搜通过，3 节点/11 转移、无药零损 T1。Release 编译零警告/错误。夹具 `coverage/fixtures/regressions/community/pr18-foreign-onplay-boundary.json`；未逐一覆盖 Prefix/Postfix/Transpiler/Finalizer，也未运行完整战斗或可见第三方 Mod 组合。

## 2026-09-06：PR #15 药水分档

`PR15-POTION-VALUE-TIERS` Passed，runId `11959921f03041e9a9f6fe7001023315`：校验 9/14/18 HP 准入门槛、Token/可再生免费、龙涎香独立计价、救命和强制用药，以及根快照中的高档成本。Short1500ms、增量验证、首结果停止；3 节点/11 转移，无药零损 T1。Release 编译零警告/错误。夹具 `coverage/fixtures/regressions/community/pr15-potion-value-tiers.json`，不代表静态分档在所有情境都优于原规则。

## 2026-09-06：SL 路线记录

- `ROUTE-CACHE-RECORD-V0111`：`47626b91d2834703a403819e5ef2ae2e` Passed，验证独立磁盘副本、动作/选择/预测一致、策略与真实 HP 变化隔离、首次记录保留、Reset 后命中及手动重算。
- `ROUTE-CACHE-RESTORE-V0111`：新游戏进程 `5a23389a9b8e4ed485b28a912711974b` Passed，从上一进程文件恢复路线并于 T2 完成原生部署；部署过程断言没有额外搜索。结果协议的节点/耗时仍是被恢复路线的历史指标，实际恢复事件和搜索次数由 `ROUTE_CACHE_HIT` / `restored` 审计记录。
- 回合开始原生选牌：`70c2a626c1614211b05b867beac7588b` 记录、`9085ab68abd14ae49333cb51327ebd28` 新建战斗后恢复，均 Passed；恢复项断言 `InitialRouteCacheRestore`、`TurnSetupNativeChoiceOrder` 和界面恢复状态。夹具沿用 `initial-gambling-chip-397.json` 的遗物与断言，将 seed 固定为 `ROUTECACHESETUP031`，scenario 分别设为 `ROUTE-CACHE-SETUP-RECORD-V0111` / `ROUTE-CACHE-SETUP-RESTORE-V0111`，同一实例依序运行。
- 夹具：`coverage/fixtures/scenarios/state/route-cache-record-v0111.json`、`route-cache-restore-v0111.json`。在同一 headless 实例和同一 DLL 上依序运行，第一项退出进程、第二项重新启动。固定 seed `ROUTECACHE031`、敌 HP12、起始能量1、Short1500ms、Instant/0秒、每请求120秒上限。
- 早期测试两次失败来自夹具：首次删除尚不存在的缓存目录，以及未启用无人测试的后续回合自动搜索。均修正后取得上述证据。原生保存菜单和各快速 SL Mod 的按钮未逐项操作验证；这里验证同根跨进程重建和会话生命周期恢复。
- Release 编译零警告/错误、Windows 结构门禁通过。未运行 Linux 游戏或可见 UI 验收。
- 缓存命中场景使用普通搜索模式；增量语义验证及阶段性能测量显式跳过缓存读取，保证它们实际执行搜索。最后只补充了该测试模式准入条件，普通恢复的行为证据沿用上述结果。

## 0.31.0 定版

收录九个玩家 PR，逐项说明见 [0.31.0 更新日志](../../releases/0.31.0-RELEASE_NOTES.md)。本次仅变更版本和发布资料，沿用下列本会话已完成的集成与碎骨定向验证，执行一次最终 Release 构建；未追加完整发布门禁或可见性能验收。

## 2026-09-06：PR #48 碎骨

`BONE-SHARDS-OSTY-REPLAY-0300` 通过，runId `a6540dba59014aeb8ee79f00dee4f050`：奥斯提 10 HP，连续打出两张碎骨，逐动作 actual/simulated 严格差分一致；第二张不会额外加盾。夹具 `coverage/fixtures/scenarios/state/bone-shards-osty-replay-0300.json`。Release 与 CoverageCatalog `--verify-effective` 通过；未单独构造攻击触发待选牌的碎骨场景。

## 2026-09-06：八个 PR 集成验证

本轮构建、结构门禁、容器与 GC 合同、Windows headless 资源隔离、失败边界、Fork/根/控制器、DOP1/DOP2、终局增量回放、长循环与保命遗物完整自动部署通过。[直接证据与失败修正](../pr/integration-39-47-review.md) 单独记录，不覆盖下方原 PR 历史证据。新增可重跑夹具：`coverage/fixtures/regressions/community/pr-integration-lizard-tail-rescue.json`。

> 当前发布：CombatSolver `0.31.0`、塔 2 `0.111.0`、RitsuLib 实测 `0.5.18`（清单最低 `0.5.13`）、CombatSolver 内置战斗模拟引擎。下方历史版本记录保留各自验证范围。无人测试运行隔离的原版 `--headless` 游戏进程，不使用自建 STS CLI；性能最终门槛另由 Steam 可见会话验证。完整战斗基准使用 `Instant / 0 秒` 部署。

单项启动器使用本 worktree 的构建产物与私有游戏/Mod 快照，各实例独立保存 Windows APPDATA/LOCALAPPDATA 或 Linux XDG 数据及协议。内容变化只重启当前精确认领的实例，不能按进程名结束其他任务。实例目录/主机租约由平台 `headless-runtime` helper 管理，请求及静稳 Ready ACK 仍由原启动器管理。默认 exclusive；双方显式 parallel 时，主机资源允许最多两个游戏。预约是准入记账，不是硬配额，暖进程与 Held 仍占名额。批次最后一个请求须 ExitOnComplete；取消、Failed、超时清理本实例。Linux 默认 Instant；Windows 可显式 `-HeadlessFastModeForTest Instant`。参数、资料隔离、队列规则与检查入口见 [Headless 实例与并行测试](../../HEADLESS_TESTING.md)。并行样本不能用于单场速度、GC 暂停或峰值内存 A/B。

单项启动器未请求退出时会保留 marker 精确持有的 headless 游戏，供身份兼容的请求复用；完整矩阵遵守有界生命周期组。当前实例、产物冻结、并行预约与参数见 [Headless 测试隔离](../../HEADLESS_TESTING.md)。游戏/Mod 内容、私有数据目录、实际可执行文件和进程出生身份共同约束复用，不仅依赖 PID 或版本号；Linux 另核对进程环境。只有异步工作静稳且收到匹配 `schemaVersion/runId/held` 的 Ready ACK 才能复用；Failed、超时或取消只清理精确认领的进程。Windows 使用私有 APPDATA/LOCALAPPDATA，Linux 使用私有 XDG；两端关闭 Steam，从各自冻结快照加载 RitsuLib，不临时写入源游戏目录。Linux 默认测试速度 Instant，Windows 可显式 `-HeadlessFastModeForTest Instant`。并行数据不作为单场性能 A/B。

## PR #45 原分支验收摘要（2026-09-06，历史证据）

用户本次明确允许少量战损或回合数回退，停止为恢复全部旧回合目标继续试验；验收重点是通用正确性、避免明显战损及严重性能退化。最终灵魂枢纽、Phantasmal、受感染棱镜相对原历史样本分别多损 2、5、2 HP。按用户允许小幅回退的要求，本次以所测样本最多 +5 HP 且存活获胜进行验收，并披露实际差距；并非用户指定了精确 5 HP 阈值，也不是全部场景不退化。外骨骼虫仍零损 T10，旧零损 T5 不再作为必达目标。Smart 药水价值门槛与可接受战损停止规则保持上游政策；不为追求旧零损强制额外用药。下方历史 FAIL、实验撤回和暂停结论仍按当时标准保留；协议 Passed、模拟回放通过、首结果质量、实际部署与性能证据互不替代。

最终行为源码 `6ea7dc4` 已正常合并 `upstream/main` 的 `b04d3ec`；任务改动提交 `3864f2c`。全部上游 worldline 回滚已接受，移除失去 latent 消费者的 `AttackPlays` 闭包及专属 `StrategicContext` 测试并恢复上游惰性 `Build`。最终收敛阶段只做上游集成，不再追加策略实验；v77 结果只属于合并前源码。

| 最终集成验证 | 当前直接证据 | 结论与限制 |
| --- | --- | --- |
| Release / 结构 / 入口解析 | `6ea7dc4`：Release 7.79 秒、零警告/零错误；Bash / PowerShell 66 文件门禁、PowerShell launcher 解析通过 | 静态与构建通过，不能等同完整行为门禁 |
| 定向语义与搜索 | 原 22 项为 21 Passed / 1 Failed；Kaiser 修正预期的独立请求随后 Passed | 22 项适用用例通过；唯一旧失败是 T2 必须复用断言，原 Failed 保留，不能改称原批次 22 Passed |
| 正常可见 Steam | 未验证性能；`bad1543e22c54a2a966179e3d98e498a` 根捕获被现有 Ave Mujica subscriber 保护拒绝，Solve 未开始，launcher 120 秒超时退出 1 | 非 headless、真实正常聚焦窗口；120 秒不是搜索耗时。兼容阻塞不作性能通过，不绕过现有保护 |

### 最终定向结果（行为源码 `6ea7dc4`）

以下数据取各请求结果 JSON；除明确标注原版部署/差分的项目外，质量为正常搜索首结果。展开/转移/选择均为请求级总工作，而非选中 solver 指标；不把参数标为 DOP2 或聚合字段相同自动等同于逐转移、完整动作或性能 A/B 证明。

| 项目 | 结果与 runId | 证明边界 |
| --- | --- | --- |
| 策略合同 | Passed `68de2db369034ba7aebb9126140f7516` | 实际执行 SearchPolicySnapshot 合同 |
| Kaiser 嵌套边界 | Passed `9f490fedaf5a4b0691b4cdd949c96bd0` | 最小嵌套边界，不是整场部署 |
| 长隐藏相位 / 长增长伤害 | Passed `f3a76c24d4d14d988e610fd9f2b6fb7e` / `b6cc4ef97305421da996a5974a1ad9de` | 1200 动作/1200 洗牌与 892 动作/445 洗牌，均预计零损 T1；请求工作分别 1200/2400/0 与 1784/3570/2 |
| 同回合停滞 DOP1 / DOP2 | Passed `3d719ea0f46c4b44bd8fd057bd312d97` / `6c9c76e6ea7c4a73975519c30bc6ec14` | 均 10/20/0 后有界停止，不选空转；不是用零损终局证明停滞无限正确 |
| 有限成长 / 低损优先 | Passed `385a23bdc02048fdb15aa8542b09ac1b` / `8f711cc1874e424781f7e2e7f60cd917` | 前者 32 动作、64/130/2；后者 20/57/0、零损，不采用卖血动作 |
| 跨回合正例 / 停滞对照 | Passed `1ccb5c0066a14a729a2ce7fafc795c9a` / `dd6cf5c2f2734e6cacfc4c8e6f3ea857` | 正例 513/770/0、预计零损 T17；对照 78/117/0、敌 HP57 保留且不选无收益防御 |
| Persistent DOP1 / DOP2 | Passed `7e2d2545c85f4f46b8cf03593436567d` / `57815552b7674413a688596863c34d3a` | 均零损 / 6 HP / T1 / 零药、39 动作、4439/17886/4190；DOP2 实际最大并发 2 |
| 灵魂枢纽 DOP1 / DOP2 | Passed `5e399067e94849ec90b2a0c27a970905` / `85d3c75cfb2b42cd8904aa340e04f75b` | 均损 3 / 95 HP / T7 / 1 药、9919/67097/32140；相对原损 1 多 2 HP，接受；不是 v77 的损 1 / T4 / 零药 |
| 自定义战斗正常搜索及原版部署 | Passed `545078fcfef5433c93a2f742a320a02b` | 实际 T1、损 3 / 1 HP / 1 药 / 7 洗牌 / 19 动作，UnexpectedReplans=0、Instant 速度恢复通过；总 19859/66214/7910，不能用选中层 1820 展开隐藏请求总成本 |
| 外骨骼虫 | Passed `48e0f5095716411f8203b2a65792afe1` | 损 0 / 97 HP / T10 / 零药、14672/157206/97502；T10 作为已知限制接受，未找到旧 T5 |
| Phantasmal | Passed `bc980a2eb8044d71acf92b5f4f49f5cc` | 损 5 / 5 HP / T8 / 零药、10230/124024/83377；对旧零损 T3 / 1 药多 5 HP、少 1 药，按本次放宽口径接受，不回写旧严格门槛 |
| 受感染棱镜 | Passed `f82f35236d2f4cb6bbcb5db9e75f2306` | 损 6 / 54 HP / T6 / 1 药、18441/125939/64932；对旧损 4 / T5 多 2 HP，接受 |
| Kaiser 原整场请求 | Failed `ee51e6f651244ef6b89b57285d4c1a9f` | 实际 combatEnded=true / T2；仅旧“第 2 回合必须复用”断言失败。预测为 12 张 T1 牌及 EndTurn，T2 开头 Mayhem 自动出牌已胜，无 T2 PlayCard；searches=1、reused=0、各 Unexpected=0，不是状态漂移或计划外重算 |
| Kaiser 终局部署修正验收 | Passed `f220b8f2822942e28fa48d00093597d9`，实际 combatEnded=true / T2、UnexpectedReplans=0 | 仅去掉 expectedReusedTurn 与对应复用 HP 断言；原输入/预算、expectedFinishedPlayerHpAtLeast=80、expectedFinishedTurnAtMost=2、expectedUnexpectedReplansAtMost=0 全部保留且通过。独立请求不覆写上行 Failed |
| 永世沙漏 | Passed `2cb489b2b8ad4a9d9d5ccfc869f0d7dc` | 损 14 / 40 HP / T7 / 零药、32136/641116/488387；优于原历史损 33 / T9，首结果并非原版整场部署 |
| 长线 4 GB No-GC / 常规 GC | Passed `224b2d2f1c1247f7a20bd085c8a82a0d` / `996f5a6169b342bc9fe1442265adedf5` | 两者均损 0 / 65 HP / T9 / 零药、7097/41023/18723；质量和请求总工作相同，不因此声称完整状态等价 |

### Headless 性能严重退化筛查

时间为请求级搜索计账时间，GB 为十进制累计 worker 分配，不是端到端墙钟、存活堆或进程峰值。当前与历史记录的源码、路线、工作量及冷暖/GC 条件并非全部受控一致，以下只用于排除所测样本的明显失控，不宣传性能收益百分比或受控加速倍率；可见 Steam 另列证据。

| 样本 | 本轮 秒 / GB | 历史对照与限制 |
| --- | --- | --- |
| 灵魂枢纽 DOP1 / DOP2 | 17.386 / 3.740；9.610 / 3.743 | 原 v9 分别 20.357 / 3.009、17.236 / 4.528。合并前 v77 DOP1 仅 3.828 / 0.730，本轮明显慢且分配更多；上游 worldline 已回滚，路线和工作量已变，不隐藏代价，也不称同工作量退化归因 |
| 自定义战斗 | 2.877 / 2.429 | 包含失败/恢复/药水层的全部请求工作；本轮实际部署通过，不取选中 solver 的较小指标冒充总值 |
| 外骨骼虫 | 15.989 / 8.820 | 原 v9 17.698 / 7.032；v77 25.165 / 12.507。当前分配仍高于原 v9，不声称所有维度改善 |
| Phantasmal | 9.365 / 6.562 | 原 v9 14.921 / 6.399；本轮战损多 5、药量少 1，不能当同质量 A/B |
| 受感染棱镜 | 10.822 / 6.905 | 原 v9 19.592 / 10.538；本轮战损多 2，不能称无质量代价优化 |
| 永世沙漏 | 73.548 / 37.642 | 原历史 84.281 / 41.718、损 33 / T9；本轮损 14 / T7，仍是高分配长搜，不推广为低内存或无卡顿 |
| 长线 4 GB No-GC / 常规 GC | 16.511 / 5.578；29.178 / 5.570 | 原同根 v0272 72.151 / 27.102、97.927 / 27.161，均损 9；当前均零损，搜索工作不同，不宣传加速倍率 |

本轮长线 4 GB No-GC 的累计/最大 GC 暂停为 `125.682 / 61.900 ms`，常规 GC（No-GC 关闭）的对应值为 `12213.517 / 180.877 ms`。两侧请求工作一致，仅记录本次观察，不等于重复受控基准，也不替代可见帧卡顿与峰值内存测量。

### 正常可见 Steam：现有兼容保护阻塞

runId `bad1543e22c54a2a966179e3d98e498a` 由真实 Steam AppId `2868840` 启动，PID `409793`，X11 `WM_CLASS=Slay the Spire2`、`WM_STATE=Normal` 且聚焦，没有 `--headless`。约 78.67 秒进入初始结果断言前发生 `SEARCH_SETUP_FAILURE stage=combat_root_snapshot`：`AveMujica.AveMujicaCode.Ftue.DreamspinFtue` 是尚未支持的 Ave Mujica gameplay ModHelper subscriber。对应 `PredictionModHookSubscriberCapture` guard 相对上游无改动；搜索根未建立、Solve 未开始。

launcher 在 120 秒超时退出 1，不是 solver 搜索 120 秒，也不构成可见性能或卡顿通过。玩家原 DLL 与 manifest 已由清理恢复并记录 `PLAYER_MOD_RESTORED`，完整游戏日志保留。本次不改变兼容策略、不绕过保护、不扩大第三方适配范围。正常可见性能仍未验证；headless 数据仅用于严重退化筛查。

据用户放宽后的质量要求、定向行为结果和上述 headless 基准提交正式 PR，公开质量差距与可见性能限制。完整发布门禁、全量 CoverageCatalog 和 Windows 原生游戏验收未在本轮完成。旧版最小事务/Fork/终局差分、v52 GC 合同与固定工作量 A/B、v67 恢复合同、v66 自动部署均按各自源码阶段引用。PR 交付说明见 [通用搜索与正确性后续 PR](../pr/generic-search-correctness-followup.md)。

### 合并前 v77 摘要（历史证据）

| 项目 | 合并前直接证据 | 结论与限制 |
| --- | --- | --- |
| v77 L0 / 策略合同 | Release 零警告/零错误；Bash / PowerShell 66 文件门禁；`373de14258414197b22e534152302b47` | 通过。验证普通同分排序的完整政策标签隔离与既有路由位置不动，不等同完整行为门禁 |
| v77 灵魂枢纽 DOP1 / DOP2 | `45cd4d4d81ee42498d9efd83e9e8cf9f` / `feefa42bb2d54ea99b1eb8ac08e4e54d`；均损 1 / T4 / 零药 | 原配置首结果通过；2112/13480/6996 工作量、80 项结果字段、26 日志动作、4 回合结果一致。不同 GC/冷暖条件，非性能 A/B、逐转移严格等价或本轮整场部署 |
| v77 外骨骼虫 | `00d1a845cfa74866a4f7e12910db2e18`；损 0 / T10 / 零药，13746/208100/147857 | 通过当前首结果哨兵；T10 为用户放宽后接受的质量限制，不声称旧 T5 已恢复 |
| v77 自定义战斗 | `9bc0a475157246e4a351a149e5ca012b`；损 3 / T1 / 1 药 / 7 洗牌 / 19 动作 | 原配置首结果通过；请求 28726/97786/10486，不把选中 solver 工作量当全部工作。旧 v66 部署证据并非本轮复跑 |
| v77 有序派生键清理 | `02354eecb8c145368efdf5ca083c3f08`；26 基础前缀与五个牌序变体 | 完整/增量与根/live 不变通过；完整 StateKey / Continuation 保留。不启动 Solve，不作独立性能收益结论 |

以上均为合并前证据，不构成 `6ea7dc4` 的最终验证；历史质量限制也不能覆盖合并后产生的新结果。
