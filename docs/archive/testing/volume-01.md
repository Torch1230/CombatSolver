# CombatSolver 测试入口历史卷 01

## 0.48.0 全平台发布定稿（2026-10-03）

用户要求全平台发版，本次只更新发布记录、索引与渠道元数据，版本仍为 0.48.0，行为源码、依赖和测试输入保持。复用下文已经通过的社区 PR 合并、五角色推广、T015 最小交接与两回合插药原生执行结果；从定稿提交进行一次带私有连接配置的正式 Release 构建、五文件最小 ZIP 和本地部署，统一脚本发布三个渠道。不重复行为测试，不执行完整门禁或可见 Steam 测试。构建与渠道完成以本轮命令及发布状态为准。

## B014 T015 智能药水审计追加（2026-10-03）

用户随后点名 PR #201，head `e11ab12e`：相同修复变基到 `45f87cd3` 的正式后续分支。完整合并并继续保留严格回放费用比较；最终 `src/`、`tools/`、`coverage/`、项目与 manifest 对已验证 `a0765da0` 无差异，因此复用下列实际 DLL 的目标和哨兵，不重复构建或测试。

完整分支来源 `yM7-1/CombatSolver:fix/batch-b014` / `a9162efd`。最终生产变动为 `cf10d513` 的预审计无药基线资格修复；完整合并历史保留，维护者移除 `0833dd1b` 按文本缺失自动删除费用子状态的宽松回放比较。直接编译作者 helper 的反例证明当前无修改牌对修改费用牌被误判相等、多张旧格式牌转换不完整；费用与星能严格差异合同加入既有 `REPLAY-BOUNDARY-CONTRACT`。

`SMART-AUDIT-POTION-BASELINE` / `24b1580a6ee5488e93df1df4f18a0a28` Passed（23.42 秒）：原版最小铁甲/爬虫场景，两次真实搜索共用冻结根，DOP1/800节点/strict incremental；在实际主结果发布入口清除插药标记，实际带药交接 1 次，Smart 审计完成，控制与候选 HP 指标相同、1 瓶药、T2 获胜，真实战斗和根未改。随后执行既有历史/原生失败传播及新增费用与星能漂移合同。不是原包完整 SearchOnly 或真实自动部署验收。

准备失败保留：`e2b6bdac…` 将 unattended 请求误传为生成场景配置，启动阶段拒绝 `scenarioId`，未运行生产路径；`2dafc6f0…` 的测试跳过公共 Solve 入口，缺少请求级 `PortfolioTelemetry` 而失败。修正调用参数与夹具上下文后上述最终请求通过，未为此修改生产算法或预算。所有请求超时 120 秒，启动器均成功删除各自隔离实例。

最终 Release 构建 0 警告/0 错误，Windows 门禁 `REFACTOR_BOUNDARIES_OK search_files=246`、diff 空白与中英日志链接检查通过。原始 T015 包、重度矩阵、逐怪回归及可见 Steam 未重跑；作者原包与哨兵数字保留为贡献者证据，不记作本轮实跑。

`BLOCK-POTION-ROUTE-INSERTION` / `d4510f7c9b744a87ae61633b6b1bcce3` Passed（23.50 秒）：沿既有提交夹具执行真实两回合原生部署，确定性格挡药插入为 true、用药 1 瓶、预测节省 9 HP、T2 胜利、实际终局 HP 36、零计划外重算。请求总展开 44、转移 99；不将这次小场景耗时作为提速结论。启动器成功删除 `.local/headless-instances/b014-t015-sentinel-20261003`，前述目标实例也已清理。

## 0.48.0 版本与玩家日志（2026-10-03）

本次版本由 `0.47.3` 按项目“大版本”规则更新至 `0.48.0`，玩家日志以最近已发布 `v0.47.3` 为基线。只改版本、文档与索引；L0 核对项目/manifest 版本一致、中英八项对应、作者与 PR 链接、官方游戏译名及文档引用，再从提交进行一次 Release 构建与五文件本地部署。战斗行为复用下文六 PR 合并及五角色推广的既有结果，不将这些历史结果写成本轮重跑。未执行重度测试、逐个怪物回归、性能配对或可见游戏测试。本版本尚未发布。

## 五角色、所有原版遭遇的已知回血剪枝（2026-10-03）

基线 `bb0e0129`；本次扩大零HP额度遗物目标的通用界及跨成员传递，并把开局胜利界接入既有已知来源政策。Release构建0警告/0错误，Windows结构门禁 `REFACTOR_BOUNDARIES_OK search_files=246`，Bash语法及diff空白检查通过。

`NATIVE-HEALING-ALL-ENCOUNTERS`（`FUZZY_WURM_CRAWLER_WEAK`）五角色全部 Passed：SILENT `5a6aad3d396c499193fe0875328b36c6`、DEFECT `b61f28b99b1440d3853c193c2744e932`、NECROBINDER `2e796b4d7a094194bea77ed5d2c2571e`、IRONCLAD `891c659da1ad45e18a12d46a61e9cba3`、REGENT `ed83221dd8c8429aaf5f36134090a194`。DOP2/800节点/三宽度成员/strict incremental 下，旧证书false、新政策true，零额度PenNib目标的剪枝实际正命中（17/9/17/17/17），后续成员继承完整无药胜利界，控制/候选总转移201/189，完整胜利和损血相同；正额度和成长规则保护，live不变。随后全部执行已有再生/持有药水/明确药水额度、NotYet/Feed、修书刀未持有无余量/持有保留、同血量后续回合保留、Fork/RNG/旧根隔离检查。共同最小牌组使用原版Silent攻击/防御以隔离角色资格差异，不作自然五牌组性能结论。

`KNOWN-HEALING-OPENING` / `c05f780bffb045b18c8d763ae58b0171` Passed：旧闭包以外开局胜利只运行一次，给首成员传界，控制质量/live隔离通过。`KNOWN-HEALING-POLICY` 在 GREMLIN_MERC_NORMAL（SILENT，`adffa7bfb1814dc9b0602d01df0d7c55`）与 QUEEN_BOSS（NECROBINDER，`4328164c951e41fba897e4dab1773261`）Passed，跨普通/首领遭遇的实际剩余治疗与保路合同通过。

八个最小请求复用同一无头进程，最后启动器成功删除 `.local/headless-instances/native-healing-20261003`。没有全怪物逐个回归、长战斗、29根矩阵或生产性能配对；严格增量耗时不用于提速数字。方法、作者原千足虫性能数据及推广边界见 [说明](../performance/native-healing-bound-generalization-20261003.md)。

## 六个正式 PR 与已知回血策略合并（2026-10-03）

完整合并 #194/#197/#190/#199/#200（最新铁甲增量）/#198。Release 构建 0 警告/0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=246`，`git diff --check` 通过。详细审计、最终 head、维护者修正与前半批证据见 [本轮审计](../refactoring/merge-audit-20261003.md)。RunStatisticsTests 通过，早期回合续搜离线合同通过 143 项断言。

- `KNOWN-HEALING-MEMBERS` / `090c88d57b7f4be69bf078599e7b8f04` Passed：旧闭包证书 false、新策略 true 的含消耗区炼制药水原版根；DOP2、800 节点、三宽度成员、strict incremental，对照和候选质量不下降，实际收到 `BEAM_REFINEMENT_INCUMBENT`，成长/禁用规则、根与 live 隔离通过。原 fixture 被防御完全挡住而对照零损血，首次 Failed；诊断 run `e3793cf5cb5e486db819b576041b8687` 明确 `won=True/boundary=None/loss=0`，给敌人增加确定力量以形成有损血两回合边界后通过。修 fixture 不修改生产算法，不报告性能收益。
- `IRON-GENERATION-HEALING` / `ce8ad1cbe030416a860eac7195684954` Passed：地狱之刃/多面手/Jackpot 原生四动作、所有原版零费池、未知消耗区狂宴、再生、未知附着/错误拥有者、完整原生状态/Fork/父分支/live/RNG。
- `DISPLAY-NAME-SUMMON` / `8fed110bf2f847af90a3e92e307d5036` Passed：模型未声明的原生召唤槽位与两个同类实例显示名。
- `PREDICTED-MONSTER-SCALING` / `028c53879b4c4c00917292cde2d295ee` Passed：NECROBINDER 原版单人根，预测 ToughEgg HP=17∈14..18，多人缩放调用 0，live 不变。
- `DYNAMIC-VAR-BRIDGE` / `2c80d103e2394ed4955a29bb46cc3039` Passed：牌/Power 字段快路径与强制公开枚举的键、值引用和顺序一致，live 不变；实际跨版本兼容 Mod 环境未运行。
- `LAMP-INKY-SHIV-ROUTE-CONTINUATION` / `ca12e6563178410d8a3165416b101528` Passed：审计发现 #197 新增方法没有接入 Executor，补上入口后首次实跑。原生墨染小刀来源身份、遗物触发与 T+1 的搜索缓存完整续用戳一致；这证明既有生产修复合同，不能把未接入的场景名按普通默认测试 Passed 视为本项通过。

单请求超时 120 秒以内。没有重跑长战斗、29 根矩阵、可见 FPS/帧时间；B014 T015 仍未验证，#173 保持开放。

最终 `KNOWN-HEALING-POLICY` / `5a3d63a682264b72a2fa2d4c1be694ca` Passed：补齐返回牌附件资格后的实际DLL保留已有再生/持有药水、NotYet/Feed、明确额度及 FrozenRoot/Fork/live/RNG；随机炼药没有潜在回血余量。末项带 `CleanupInstanceOnExit`，启动器清理整个 `.local/headless-instances/pr-audit-20261003-final`。Bash 门禁同步维护，本轮仅执行 Windows 门禁。

来源复核后再次在最终实际DLL执行 `KNOWN-HEALING-POLICY` / `95a89d6861f84a83a62e12b1bea95f01` Passed：`DoomKillRelicProtected` 验证 BookRepairKnife 的已知灾厄击杀回血保留完整余量，旧根不受后来添加遗物影响；原有随机炼药/NotYet/Feed/持有药水与剂量/额度/Fork/live/RNG 断言全部通过。启动器成功删除 `.local/headless-instances/pr-audit-20261003-doom-heal`。`bash -n tools/inspection/verify-refactor-boundaries.sh` 通过，未执行 Linux 原生门禁。

## B016/T023 生成牌附魔来源与一次性遗物消费（2026-10-02）

- 失败基线取自问题包 `21980d83adf740879ddf16d466f8c499`（0.47.0，原版小啃兽遭遇，`diagnostics/logs/combat/000.jsonl:197/199`）：第 2 回合续用对账报 `field=relicCounters expected={UNSETTLING_LAMP/0/0} actual={UNSETTLING_LAMP/1/0}` 与 `field=P[0] expected={<missing>} actual={1:WEAK_POWER=1/0[DamageDecrease=0.75,]}`。两处同源：墨染附魔的 OnPlay 镜像当时没有把生成卡实例作为卡来源，灯的 `BeforePowerAmountChanged` 拿不到 `cardSource`，既不翻倍虚弱也不标记已消费。
- 新增原生夹具 `LAMP-INKY-SHIV-ROUTE-CONTINUATION` / `LampInkyShiv:RouteContinuation:CardSourceIdentitySearchBoundaryMatchesLiveTurn`：墨刃 + 墨染小刀 + 不安之灯，固定前缀 `[BLADE_OF_INK, SHIV]` 走真实搜索，再用实机打出同一前缀并结束回合；核对触发来源就是打出的那张附魔小刀、目标虚弱层数，以及搜索缓存的下一回合边界状态与实机逐字段一致。
- 对照：同一命令在把 `EnchantmentOnPlayMirrors.HandleInky` 回退成 `ApplyPower`（无 cardSource）后 `Failed`，错误与失败基线逐字段一致；恢复 `ApplyPowerFromSource(..., context.PreviewCard)` 后 `Passed`。Release 构建 0 警告 0 错误，实例由启动器输出删除。
- 本主题只覆盖生成牌附魔来源与灯的一次性消费时点；跨回合续用对账的泛化状态一致性仍按 T006 归口，未在此重派生。

## 生命界认证与边际剪枝日志（2026-10-02）
与上游合并后，primary incumbent 使用上游逐分支剩余治疗估计作为基线；仅当本分支的固定战后治疗证书更紧时，再取两者较小值。`primary_incumbent_certified_healing_bound_pruned` 只统计固定战后上界相对该基线新增剪掉的候选；根没有上游动态证书时，基线为完整缺血余量。因此总剪枝数仍包含上游已认证动态上界的收益，而该边际字段不把它们归到本分支。

此轮只增加诊断数据，不改变认证门禁、搜索候选、剪枝条件或预算。`COMBAT_ROOT_CAPTURE` 与自动搜索入口的 `TURN_SETUP_ROOT_CAPTURE` 均记录 `strategic_hp_recovery_bound`（根认证是否通过）、`strategic_hp_recovery_bound_reason`（认证顺序中首个不满足的稳定原因码）、`strategic_hp_recovery_bound_source`（如首因来自特定角色、敌人、遭遇 Modifier、药水、遗物、Power 或卡牌，则记录其 ID；URI 转义；无单一来源时为 `-`）和 `strategic_hp_recovery_bound_postcombat_heal_hp`（已认证根允许计入的固定战后治疗量；未认证时为 `unbounded`）。原因码包括 `unsupported_character`、`non_native_enemy`、`encounter_modifier`、`run_mod_subscriber`、`combat_mod_subscriber`、`base_lib_card_modifier`、`adapted_on_play`、`potion_present`、`unsupported_relic`、`unsupported_player_power`、`card_enchantment`、`card_affliction`、`unsupported_card` 和 `certified`。

搜索结果及 ETC 成员日志中的 `primary_incumbent_certified_healing_bound_pruned`／`incumbent_certified_healing_bound_pruned` 是边际数：同一候选在认证上界下被 primary incumbent 剪掉，而把未来治疗潜力放宽到完整缺血余量时不会被剪掉。原有 `primary_incumbent_pruned`／`incumbent_pruned` 总数不变。边际数只覆盖 incumbent 这一剪枝点检查过的保留节点，不能解释为所有层的剪枝总数、减少的展开数或节省时间；旧日志不会包含这些字段。源码审阅确认该比较不改变原先的剪枝谓词。首次实机验证 session `15052-1603d6f4ec2a4f54b4ebfdefb92e6576` 含26个战斗文件、26个 Begin/End、25个保留事件、无截断；150条搜索结果和292条 ETC 成员记录均带新边际字段且为0。26条根捕获中，18条 `COMBAT_ROOT_CAPTURE` 均旁路（药水8次、未认证遗物10次），另8条自动入口 `TURN_SETUP_ROOT_CAPTURE` 缺少认证字段，故该局不能完整统计根认证原因；字段已补到该入口，旧日志无法回补。最终 Release 构建成功（0警告／0错误），五文件精确部署至 `D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\CombatSolver`。未运行战斗测试或可见游戏；新一局日志可完整核验认证和实际命中。

本轮 Release 使用 `dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false -p:SteamRoot=D:/SteamLibrary`，0 警告／0 错误；构建阶段先因默认 Steam 路径不匹配失败，显式指向已确认的游戏目录后成功。成功构建后精确覆盖 manifest、DLL、MemoryCleaner、LICENSE 与第三方声明文件至本地 Mod 目录。未运行战斗测试或启动可见游戏；来源 ID 字段待新日志核验。

新进程 `52388-9f74d33df71a4a2ab7303229aad4bb09` 加载了带来源字段的本地 0.47.3：24 份战斗 JSONL 均有 Begin/End，23 次保留事件，无截断或 Error；33 次根捕获均有来源字段，但全部因 `encounter_modifier` 旁路且 source 为 `-`。138 条搜索结果均含边际剪枝字段且计数为0。94 条 ETC 成员共展开457,043节点、累计59.805秒、17条 `selected=true`；EndTurn的10条成员耗时11.183秒／76,642节点，有1条严格采用。日志证明来源字段接线完整，但暴露 Modifier 类别尚未记录身份。本次进一步把首个遭遇 Modifier 的 `Id.Entry` 写入来源字段；新改动的构建、部署和未执行项接续记录在本段末尾。

该续接改动 Release 构建 0 警告／0 错误，五文件再次精确覆盖至本地 Mod 目录。未运行战斗测试或可见游戏；上面的新进程早于本次续接，因此还没有 Modifier ID 字段的实机验证。
## 无人测试隔离静音（2026-10-02）

| 验证 | 实际结果 |
| --- | --- |
| PowerShell 修改前配置阶段 | 在临时副本执行启动脚本的真实配置初始化语句，四项输入非零音量全部保留，确认缺少静音。 |
| PowerShell 修改后配置阶段 | 已有音量、缺少音量字段、复用后音量恢复非零三个场景均输出 `AUDIO_MUTE_SMOKE_PASS`；四项音量均为零，语言、窗口位置和既有 Mod 初始化行为保持，源模板未变。 |
| 真实交互游戏设置 | 修改前后 `settings.save` SHA-256 相同；未写入正常游戏用户目录。 |
| Bash 启动脚本 | `bash -n tools/testing/run-unattended-test.sh` 通过；未运行 Linux 配置阶段。 |

配置阶段检查通过 PowerShell AST 截取 `settingsPath` 初始化至 `resolvedProgressSnapshotPath` 前的真实语句，并在临时目录执行；临时目录已清理，没有新增永久测试或复制实现。未启动或停止游戏、未重跑战斗验证；上述证据证明配置生成行为，不替代 FMOD 实际音频输出验收。

## 部分重战斗场景搜索优化（2026-10-02，上游合并后回归）
上游 `96ee2669` 合并后 Release 构建通过（0 警告／0 错误），PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=239` 通过。最小原生合同 `REMAINING-HEALING-BOUND`（run `40560a794030405686c40d2bcf088373`）Passed：动态剩余治疗估计、Bundle of Joy 未知未来牌回退、再生药水与Power上界、未知分支的保守回退、incumbent剪枝、原生七次再生差分和摄政 strict incremental 搜索均通过；最终 63 HP、零战损、一回合胜利。无头实例 `F:\rider\CombatSolver\.local\headless-instances\wt-e01189cc8f079ad9` 已由脚本清理。该检查覆盖上游动态界及 incumbent 接线；本分支固定战后治疗边际计数仍缺少正命中实机日志，完整 29 根质量／内存回归沿用合并前已记录的证据，不将其称为本次重跑。 另有 `HEAL-BOUND-SAFE-ROOT`（run `01bde09fc40c4a29890936d39bb2be1c`）Passed：Ironclad 固定战后上界根认证、根捕获线程隔离与 incumbent 过滤合同通过；实例已清理。
## 社区批次 B014 修复夹具（2026-10-02，Refs #173）

- `DISPLAY-NAME-SUMMON`：修改前 `e1894470bdce4850a28bd5561094cc97` Failed（`GremlinMercNormal` 死亡召唤后预测 `sneaky` 槽位缺映射，KeyNotFoundException）；修改后 `70c003d27fc9410aa3c86397833af6a4` Passed（`sneaky1=左起1/sneaky2=左起2/fat=左起1`）。同记录策略对 f217 报告做 `SearchOnly`，修复前后指标一致（5 回合胜、finalHp 3、预计战损 5、零药）。
- `PREDICTED-MONSTER-SCALING`：修改前 `40b0ce9f7d0941d58dfb45570901fee0` Failed（单人预测怪构造仍调用 `ScaleMonsterHpForMultiplayer`，搜索 worker 会执行第三方 postfix）；修改后 `3daf40f0d1f441aaa1df9b7edfd5d134` Passed（HP 17∈14..18，多人缩放调用 0 次）。命令入口：`tools/testing/run-unattended-test.ps1 -ScenarioId <ID> -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 999 -TimeoutSeconds 120 -ExitOnComplete -CleanupInstanceOnExit`。
- `CARD-CONTINUATION-EXPANDED` / `af3944d5d9a6495499b87dc4923d2a0e` Passed：原版 HEIRLOOM_HAMMER+0/+1 选牌续执行合同通过；T013 代表包运行环境含 RebalancedSpire，按第三方内容记录。
- `SMOKE-001 -VerifyForkBoundaries` / `7fc97fd1a99c4b61889bc68063919952` Failed：停在既有“回合结束 Power 挂起”检查；去本批改动复跑同样失败。该场景内的生成怪生命检查未执行，T011 由专用夹具覆盖。本轮未提升版本、未发包、未部署可见 Mod。
- `DYNAMIC-VAR-BRIDGE`（T014）`ba6168def5814f94a054ea9b73a2cd5b` Passed：`DynamicVarSetAccess` 用一次性解析的缓存委托读取 `DynamicVarSet` 内部字典（字段缺失时回退公开枚举），28 处直访全部改走桥接；夹具对牌与 Power 各比较键序列、值引用与枚举序列，强制公开回退时逐项一致，live 战斗不变。命令：`tools/testing/run-unattended-test.ps1 -ScenarioId DYNAMIC-VAR-BRIDGE -CharacterId DEFECT -EncounterId FUZZY_WURM_CRAWLER_WEAK -TimeoutSeconds 120 -ExitOnComplete -CleanupInstanceOnExit`。结构门禁新增 `src` 内禁止 `._vars` 直访（桥接文件除外）。编译产物 `System.Reflection.Metadata` 扫描：`_vars` 字段引用 0（基点源码直访 28 处已全部收口）。含 T015 修复的尖端复跑 `b98d4d7b` Passed。
- T011/T012 变基后复检：`PREDICTED-MONSTER-SCALING` `96651321` Passed（HP 17、缩放调用 0）；`DISPLAY-NAME-SUMMON` `9dad3061` Passed（sneaky1 左起1/sneaky2 左起2/fat 左起1）；含 T015 修复的尖端复跑 `e266efe1`/`89abbaec` Passed。
- B014 固定哨兵（`INITIAL-TOOLBOX-INFUSED-CORE`，与 `coverage/fixtures/scenarios/state/initial-toolbox-infused-core.json` 同配置：1500ms 固定预算、增量等价、首次准备断言后停止；同机各 3 次）：基点 `1a3d1a37` 681.91/678.10/719.58ms，均值 693.20ms（组内极差 41.48ms）；批次尖端 714.97/703.43/693.00ms，均值 703.80ms（组内极差 21.97ms）。两组展开 130、转移 1882、boundary None，`InitialPolicy` 除耗时与 GC 采样外逐字段相同（Turns=2、Shuffles=1、HpLost=0、ProjectedBattleHpLost=0、Block=5/5、Pruned=0、ChoiceBranches=16、Actions=4、CombatEndedTurn=2）；均值差 +10.6ms（+1.5%）小于基线组内极差，不构成稳定耗时增加。含 T015 修复后的同窗口复测：基点重跑 797.66/806.14ms（均值 801.90），尖端 780.56/799.93/820.84/787.96ms（均值 797.32），展开/转移/质量字段仍相同，均值差 −0.6% 小于尖端组内极差；两个窗口一致说明无稳定耗时增加。
- T013 结论：代表包 `e7f1cf14e6ad4a60be7f4f3724929b81` 运行环境载入 `RebalancedSpire`/`AutoRebalancedSpire`（日志含 `[AutoRebalancedSpire] 未建模的结算内选择：传家宝锤`），按“不主动适配修改游戏内容的第三方 Mod”归档排除；原版 `CARD-CONTINUATION-EXPANDED` 合同通过。
- T015 修复（Smart 审计无药基线）：旧包 `ba79d87499a4455bbba4a51baf381eea`（0.47.2）的续用戳缺 `cost-state`/`stars` 子状态，测试端在 `ReplayContinuationMatches` 增加同形态 legacy 容错（记录侧整段缺失时从重放文本剥离该子状态）后 `start` 检查点 `RestoreOnly` 恢复通过。`SearchOnly` 复现：修改前 `c1a7645235564bbba22d7406dd32e6fd` Failed（`assert_initial_solver_result`，`Smart 梯度搜索必须从仅满足强制用药的结果开始`）→ 修改后 `75d9d046e7ce4aae85a303cb03341191` Passed（`search_completed`，展开 289,973、转移 2,274,934、boundary None，约 98 秒）。根因：`OPENING_TARGET_VARIANT` 预审计块（`CombatSearchCoordinator.cs`，2026-09-27 `b277fce5` 引入）缺少 `ExplicitPotionCount == 0` 门，从带插入药的首回合前缀派生 continuation，候选 `DeterministicBlockPotionInserted=false` 且带 1 瓶 BLOCK_POTION 回到主路线，Smart 梯度断言拒绝。修复：预审计前对「插入药或 Smart 无强制指令下带药」的主路线统一按 Disabled 重派生无药基线再走补充审计。命令：`dotnet run --project tools/replay/CheckpointTool/CheckpointTool.csproj -c Release -- batch .local/issue-bundles/B014/T015/raw --mode SearchOnly --selector start --timeout 300 --output <目录> --game-root "D:\Steam\steamapps\common\Slay the Spire 2" --ritsu-root "D:\Steam\steamapps\workshop\content\2868840\3747602295"`。

## 部分重战斗场景搜索优化（合并前上游证据）

本 PR 在上游 `88298ae5` 上保留 AfterCardPlayed 捕获参与过滤、安全边界保留、已证明无额外治疗路线的战损下界、组合成员共享无药完整胜利、开局完整路线，以及原生感染棱柱／灵魂枢纽／摄政虱虫场景的受限治疗闭包。未纳入伤害目标过滤原型或已撤回实验。

既有阶段原生证据包括 `MIRRORED-HOOK-FILTER`、`CARD-EXECUTION-CONTINUATION`、`EXECUTION-CHOICE-INCREMENTAL`、`SURVIVABLE-BOUNDARY-CONTRACT`、`REFINEMENT-INCUMBENT-CONTRACT` 和治疗闭包合同；历史通过不等同于合并上游后重新通过。四场阶段 ABBA 的战损与内核峰值门槛通过，保守提速 2.336～5.544 倍。合并后的 Release／结构门禁和受影响原生合同，以及 29 根完整极高／DOP16 的质量／内存回归另行记录。详见[范围与证据](../../performance/veryhigh-dop16-20261001.md)。

## B015统计消费者终止隔离（2026-10-02）

`B015-T016-AFTERIMAGE-ROUTE` / `coverage/fixtures/regressions/community/b015-t016-afterimage-route.json`：同原根按日志STRANGLE→AFTERIMAGE顺序从生产PrepareCardActions取六步动作，每步完整/增量/原生状态与RNG一致；目标自然跨ID1→ID2。生产ReplayAdjustedRoute前移余像使第二张SHIV目标2失效，同输入修前Failed、修后Passed；合法两步前移、无重排控制、共享路径失效目标及原路线保留通过。120秒，0.111.0/Ritsu0.6.3/同MVID BaseLib3.4.7；不是完整Solve胜利路线发布验收。

`B015-FIXED-PREFIX-TARGETS` / `coverage/fixtures/regressions/community/b015-fixed-prefix-targets.json`：直接生产候选→ApplyFixedPrefix单动作，合法目标致胜、缺失目标拒绝、原生Continuation不变，Passed（18.33秒）。既有ADJUSTED-ROUTE-INVALID-SUFFIX Passed（20.12秒）；更广FIXED-PREFIX-TURN-OUTCOMES起初在120秒无结果，未提高预算；后查明是全新隔离档案第一次洗牌时原版洗牌引导等待确认，上游同样卡住。放入只关闭引导的进度档后，上游 `c4e0b47d` 与本分支均Passed（19.42 / 23.86秒）。

整合上游 `c4e0b47d` 后：`B015-T016-AFTERIMAGE-ROUTE` Passed（32.80秒），`B015-FIXED-PREFIX-TARGETS` Passed，统计存储合同通过。PR哨兵按上游→本分支→本分支→上游交替：`TURN-SETUP-FIXED-PREFIX-STAMPEDE` 四次路线（含卡牌状态键）、根续用戳、展开8549/转移17245均相同，搜索耗时8649/3213/6653/6992 ms，四次都在同一条macOS无法建立No-GC区域的断言失败（发生在搜索结果之后，上游相同）；`PROFILE-SHIV-DEPLOY` 四次Passed，路线与续用戳相同、部署后战损0。0.111.0/Ritsu0.6.3，macOS隔离无头，各120秒。

`B015-T016-ORIGINAL-PREFIX` / `coverage/fixtures/regressions/community/b015-t016-original-prefix.json` 是保留失败的诊断入口：原ZIP开战双状态、日志选牌、前五步完整/增量/原生状态与RNG均通过；生产串行兄弟及两个父节点的真实调度器/worker检查通过，2准备/4动作且实测动作并发2，同原报告MVID BaseLib3.4.7。当前生成器未产生非法目标，随后强制历史SHIV→ID2仍失败，故请求总Failed；不是当前生产RED或修复验收。真实0.111.0/Ritsu0.6.3、120秒；差异环境及复跑参数见[后续证据](../community/b015-follow-up.md)。

`RUN-STATISTICS-WORKER-FAILURE` / `coverage/fixtures/runtime/run-statistics-worker-failure.json`：原始NUL JSON导致真实消费者构造失败后，300次入队；修改前Failed（capacity exceeded），修改后Passed（队列空、快照无效、损坏文件保留）。游戏0.111.0、RitsuLib0.6.5、macOS隔离无头，请求均120秒预算。统计聚合/持久化既有合同通过；该阶段尚未覆盖的边界见下方后续记录。命令及边界见[B015阶段证据](../community/b015-stage-one.md)。

`RUN-STATISTICS-SATURATION` / `coverage/fixtures/runtime/run-statistics-saturation.json`：屏障暂停真实健康consumer，256业务信号、满sync合并重试、第257业务事件显式停用；已接收事件排空持久化，partial重开与原生结算保留，禁止重新上传，排空期间I/O错误不被吞。真实0.111.0/Ritsu0.6.5同输入修前Failed、修后Passed；最终故障及饱和请求均在Ritsu0.6.2复核通过；纯Store另覆盖旧full收据撤销和纠正收据不重复上传。详见[B015后续证据](../community/b015-follow-up.md)。

`B015-MAD-SCIENCE` / `coverage/fixtures/regressions/community/b015-mad-science.json`：真实0.111.0/Ritsu0.6.2中Skill/Chaos原生升级、保存恢复、root/Fork、兄弟隔离、合法出牌完整状态/RNG差分通过；None明确拒绝。不是原包坏牌修复，也未定位战前替换调用者。

`B015-BOUNDARIES` / `coverage/fixtures/regressions/community/b015-boundaries.json`：真实0.111.0/Ritsu0.6.2中Stock替补CombatId/Fork/父不变/增量完整回放/两次原生SHIV状态一致；1HP原生CrimsonMantle自伤在T+1死亡，模拟终局/Fork与原生ProcessPendingLoss安全点完整状态一致。另用T016原报告Ritsu0.6.3复核通过；主线最小边界通过。T016原包开战双状态对账通过；cursor0后的自动Hook触发recorded_action_mismatch，整个RestoreOnly失败；原包另缺选牌录制；T019已定位f1461c7多人实验分支的Last(predicate)无人存活异常，详见后续证据中的构建身份更正。

## 0.47.3 版本与发布登记（2026-10-01）

本次小版本由0.47.2更新至0.47.3，整合内存修复分支至main，并同步项目、manifest、开发笔记与中英玩家日志。main整合只新增既有多人规划文档；本次版本登记没有行为源码或测试输入变化，复用本页GC与储君生成路线部署合同及PR #147原生费用/选牌合同。最终发布只执行Release构建、最小ZIP和统一三渠道脚本，不重跑已通过场景、不启动可见Steam或完整发布门禁。恢复后的管理员系统清理与完整重型生成流的可见100%卡死仍保持未实测口径。

## 手动释放内存后全自动（2026-10-01）

用户要求恢复系统和其他进程内存清理：辅助程序重新依次调用 `MemoryEmptyWorkingSets` 与 `MemoryPurgeStandbyList`，恢复工作集清空失败退出码20，中英文说明同步恢复。本次恢复只改辅助程序和提示，游戏GC与部署源码保持既有已验证实现，复用下列成功合同。本次验证为Release构建和调用顺序静态核对；UAC管理员系统清理未实测，既有进程合同不能代替全系统验收。

`GcPolicyChecks -- manual-release` 在普通及ServerGC真实CLR上取得旧实现失败与最终通过；覆盖空闲预留归还、搜索垃圾不可达、保留数据访问后的工作集稳定和NoGC退出。`diagnostic-failure` 8项通过。原生 `MANUAL-MEMORY-RELEASE-AUTO-CONTRACT`（`00f77f0bead44df99cd3de3e80093ce0`）使用储君类星体生成亮剑，在释放后保持原路线和完整live状态，直接全自动击杀，仅1次搜索、零重规划；实际75/75 HP、第1回合。ServerGC启动、DOP2实际并发2，隔离实例已清理。完整命令、内存口径与未验证项见[报告](../performance/manual-memory-release-20261001.md)。

本轮 PR #147 原生费用11组（`839136cb2faf4485bd82cf2f591ef910`）、选牌1200组/5069条（`1bdb389c145747a0a36e272b49562876`）通过；Release 0警告/错误，结构门禁通过。PR原有大矩阵为贡献者证据，没有在本轮重跑。

## 大幅优化原型与归一化反例（2026-09-29）

五职业精英根5次快照诊断、44次原型ABBA、20次不安全归一化研究和1次M1洗牌反例，共70次离线运行。原型组完整路线/根/续用/质量/政策/非时序指标一致，但收益不足，全部撤回；归一化组有质量退化，不能用Passed或局部低战损代表改进。独立StableShuffle反例及live不变断言通过。没有新增原生整场、DOP2或首领结论，详情见[研究与证据](../performance/large-stage-exploration-20260929.md)。

## 等价重复的准入优化（2026-09-29）

20个不同开局、23种配置、54次固定节点离线对照全部通过且未触及时间上限，同配置完整路线、根、续用、质量及73项非计时指标一致。新增两组实际出现循环区域的根和密集选择DOP1/DOP2对照，DOP2最大实际并发为2。自然AB/BA诊断只观察、不证明交换性，不用诊断时间报告性能；原生完整战斗记录及收益范围见[报告](../performance/equivalence-admission-20260929.md)。

## 重复选择的跨场景与整场验证（2026-09-29）

原生五角色完整部署基线/候选实际终局HP、最大HP、结束回合、完整计划、根与续用一致，全部胜利且零计划外重算；选牌1200组/5069条、尾部代表2412组、令牌1024组合同通过。增加NativeOutcome实战终局记录，避免引用预测HP作为实战结果。跨角色/精英/首领固定根矩阵、perf证据及收益限制见[报告](../performance/duplicate-choice-pruning-broad-20260929.md)和[逐次证据](../performance/duplicate-choice-pruning-broad-20260929.json)。

## 重复牌与费用状态（2026-09-29）

`CARD-COST-IDENTITY-CONTRACT` 原生11组费用身份合同通过；`CHOICE-COMBINATION-CONTRACT`（`1cc7c04da24d427b9c9cff7bb0f0080c`）1200组/5069条完整选择与旧枚举器一致。覆盖能量/星能修改的时效、顺序、隐藏层、Fork隔离、实际选牌去重及续用字段定位。目标短搜两对ABBA的73个非时序字段、完整路线、根与剪枝计数一致。命令、原生最终runId、微基准限制和未采用实验见[重复牌研究](../performance/duplicate-choice-pruning-20260929.md)及[证据](../performance/duplicate-choice-pruning-20260929.json)。未作可见性能、Windows游戏或整场质量结论。

## 静默猎手基础根的生命界认证（2026-10-01）

三项最小原生验证在同一隔离进程通过：基础／升级四牌与起始遗物的 `HEAL-BOUND-SAFE-ROOT`（`5f52ea1c811e48528784550f0f773611`）认证true并完成四牌／弃牌／怪物行动的MoveState及RNG差分；鲜血药水根／实际治疗（`e6fe6a56234b45009e141cabb8b7e14d`）与炼金术根（`66b652b2541a4c6b830a0d5e53dc04eb`）认证false。后两请求复用PID47772，最后请求带清理开关并由启动器删除实例。首轮缺旧布局设置未进入行为测试；一次错误目标夹具由Player改为None后通过，没有改生产结算。

复用行为源码512bd7c9已有基线，候选两次独立进程Coordinator完整请求的根、政策和预算相同，主搜／ETC各90秒、外层120秒，未触发时间边界。胜利、战损59／53、0药水、第17回合保持；展开85,950→77,409及62,229→56,485。第一根路线变化且Score尾键降低100,000，生产完整比较器判较差、去尾键判同等，故只报告主要战斗结果保持，不报告完整排序等价。第二根路线／质量相同。当前Release0/0、Windows结构门禁238通过，23:07:19完成五文件本地与既有创意工坊副本部署。原生夹具参数、尾键取舍、实际耗时及未执行项见[证据](../performance/silent-recovery-bound-20261001.md)。

## 早期回合探索的外部生命界（2026-10-01）

用户提供了部署后自行完成的实机 session `55876-53693100b43f42a4a990050c8d4c4ee1`，本轮只读取日志，未由 agent 启动游戏。19 场日志保留完整生命周期事件；12 次探索开始均出现当前外部界标记，60/96 个成员注入界，其中 5 个灵魂异鱼成员命中 2,219 条剪枝。8 次入口跳过、2 次探索内达标停止，rank 从未超过 5；82 次续用无漂移，83 条实机回合损血匹配执行时有效计划。没有本局旧 DLL 同根对照，不作速度、全状态等价或全局质量不降结论；末战全部成员旁路新界。详见[实机复核与未实现候选](../performance/early-turn-log-review-20261001.md)。本轮文档阶段复用下述已成功的同源码构建、合同及部署证据，不重复执行。

当前任务基线为合并版 `c647b1f1`，候选只改变 ETC 成员的外部 incumbent 接线。完整胜利、成长/遗物、死亡保护门禁复用 `BuildPrimarySearchIncumbent`；另检查强制药水、战略额度、风险及失窃资源政策。已选有显式用药而前缀未用药时不注入。外部界的回合设为 `int.MaxValue`，只剪严格更差的已证明战略战损下界；原内部收紧仍可建立自己的回合界。没有降低节点预算、改变候选顺序或终局比较，也没有增加搜索暂停/恢复能力。

`dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --check-early-turn-continuation-bound`：143 条断言通过，覆盖 incumbent 有/无的 128 组门禁组合、严格更差/相等/更低的生命界、相等生命界的晚回合及负战略战损。此纯合同验证门禁和剪枝谓词，不代替真实成长/遗物生命周期测试。

四组均在独立普通 .NET 进程运行生产 Coordinator：Custom、8,000 节点、card/pile/hand 分支 32/18/24、DOP1、主搜/ETC 累计时限均 90,000 ms、depth2、组合/NoGC/增量关闭，外层每根 120 秒截止。先重新运行合并版基线再运行候选；以下不是引用旧提交的历史结果。全部根戳、搜索政策、预算、完整动作路线与完整质量记录相同，四根均完整胜利。

| 固定根 | Beam/药水 | 战损/药水/结束回合 | 总展开 A→B | 总转移 A→B | ETC 尝试 A→B |
|---|---|---|---|---|---|
| SILENT / TERROR_EEL_ELITE / CSOPT20261001D | 96 / Smart | 59 / 0 / 17 | 85,950→85,950 | 247,414→247,414 | 10→10 |
| SILENT / TERROR_EEL_ELITE / CSOPT20261001 | 96 / Smart | 53 / 0 / 17 | 62,229→62,229 | 181,831→181,831 | 8→8 |
| SILENT / TERROR_EEL_ELITE / CSOPT20261001D，注入 DEXTERITY_POTION | 96 / RequireAtLeastOne | 26 / 1 / 14 | 70,255→70,255 | 230,165→230,165 | 8→8 |
| IRONCLAD / TERROR_EEL_ELITE / CSOPT20261001D | 64 / Smart | 66 / 0 / 9 | 28,588→25,853 | 74,198→67,224 | 12→12 |

铁甲战士 ETC 展开 25,900→23,165，请求展开减少 9.6%、转移减少 9.4%；中途严格改进次数 3→2，最终路线保持，不把中途改善次数当作最终质量。三组静默猎手虽然在合资格成员注入了界，但治疗收益上界较宽，均无生命界剪枝、无工作量收益。正药水根四条未用药前缀旁路、四条已用药前缀注入，保护无药资格路径；原始请求另含 `schemaVersion=1`、`enemyCurrentHp=140`、`fixedSearchBudget=true`、`timeoutSeconds=120`，其余为上表角色/遭遇/种子与药水。

一次 A/B 的无头搜索秒数分别为 29.263→30.568、22.879→22.750、29.936→29.260、11.008→10.662。未做 ABBA/可见 Steam 性能验收，不由此报告稳定提速或帧时间收益。新增日志的 `incumbent_pruned` 合计包含成员原本的内部生命界剪枝，不能当作外部界的净新增数量。

基线 DLL、全部命令输出、根/路线/质量、日志与 `comparison.json` 保留于忽略目录 `.local/early-turn-incumbent-20261001/`。最新玩家千足虫只有日志/路线缓存及当前工具无法导入的 `.mcr`，没有导出的开战存档/检查点；本轮没有恢复真实千足虫或女王根。Mod 与离线宿主 Release 构建均 0 警告/0 错误，Windows 结构门禁通过（238 个 Search 文件），最终 manifest、DLL、Windows MemoryCleaner 和两份许可文件已部署至确认的本地 Mod 与既有创意工坊安装目录，原文件保留本地备份。未运行原生整场部署、Linux 或可见游戏验证。

## 下一版本（开发中）：早期回合探索测量入口（2026-10-01）

OfflineSearchHarness 的 Coordinator 模式可用 `--early-turn-exploration-depth 1|2` 开启早期回合探索，并用 `--early-turn-exploration-budget-ms` 指定从请求开始计的 5,000..2,390,000 ms 累计时限；`run_plan.py` 支持相同的 plan 字段并按时限扩充进程超时。结果增加总体尝试/严格改进/首次改进深度与 rank，以及逐 rank 的工作量和成绩。该入口只打开离线测试请求，不改变玩家默认开关。

早期回合探索现在也遵守既有 `HasReachedAcceptableBattleHpLoss` 停止目标：基线已经达标时不启动探索；侦察或续搜得到达标完整路线后结束后续 rank 派发。可接受目标仍要求胜利、战损阈值、成长/遗物目标、失窃资源、死亡保护和药水使用条件全部满足，因此 Smart 下仍保留降低非必需药水消耗的搜索空间。变更后 Release 构建与本地五文件部署通过；本轮未运行战斗场景，当前运行日志/性能影响尚待实机观察。

问题反馈页新增可选的本进程详细战斗日志保留开关。开启后新战斗开始时不再删除上一场的 JSONL 文件，并写入 `COMBAT_LOG_RETAINED` 进程事件；默认关闭，已删除日志无法恢复。只改变本地详细文件保留，不改变候选日志量与搜索并行度；每场日志仍有 32 MiB 上限，问题包仍只导出当前/最近战斗详细文件。Release 编译（0 警告／0 错误）及本地五文件部署通过；未运行游戏交互或跨战斗保留验证。

CombatSolver 与 OfflineSearchHarness 的 Release 编译均通过（0 警告、0 错误；显式使用本机确认的游戏/RitsuLib 路径并关闭构建自动复制），Python 批量运行器语法编译通过。按仓库要求完成最终源码的五文件本地 Mod 部署。本轮没有运行战斗场景或深度 0/2 固定根对照；逐 rank 运行数据和入口门禁尚未获得运行证据。后续策略收益结论仍需独立进程的完整请求 A/B 对照，本轮不做可见 Steam 性能结论。

### 前两回合探索 rank 窗口（2026-10-01）

保留每层前 8 个 rank，继续使用原来的深度交错顺序、共享请求时间/节点账本和完整路线比较；有更多候选时结果写 `stop=rank_limit`，可接受目标／零战损及预算停止原因优先。起始事件写入 `max_ranks_per_depth=8`。这会减少后续候选搜索，不能保证未搜索 rank 不含唯一更优路线。

对已保留的实机日志回溯截断，每层取 rank 0..7 会在 6 次探索中保留 92/231 条续搜、4/4 次既有改进，预计少展开 630,178 个续搜节点（原续搜 1,002,801）；这是按既有结果做的反事实计数，不是重跑后的测量。

OfflineSearchHarness 同根 `Coordinator` 对照：TERROR_EEL_ELITE、SILENT、seed `CSOPT20261001`、Custom beam 96 / 每 solver 8,000 nodes，完整胜利两侧均为 53 战损、0 药水，动作路线完全相同，`boundary=None`；rank 续搜 25→15，展开 181,920→108,788，转移 530,474→317,326。PHANTASMAL_GARDENERS_ELITE、seed `CSOPT20261001C`、Custom beam 64 / 1,500 nodes：两侧路线及 38 战损相同，续搜 48→16、展开 73,820→26,764、转移 375,559→137,345；两侧均触及 `NodeLimit` 且未胜，不能当作完整质量验收。零战损 `CORPSE_SLUGS_WEAK` 哨兵仍在入口跳过探索。

候选 Release 编译成功（0 警告／0 错误）；只运行无头离线搜索，没有启动可见 Steam。实机日志和离线根尚不能排除 rank 8 之后出现独有好解，后续若有更多战斗日志应重点查看 rank 8+ 改进及 `rank_limit` 命中情况。

### 前四 rank 无改进时关闭该层扩展（2026-10-01）

候选策略每层先跑 rank 0..3；只有该层某次续搜产生满足强制用药条件的完整胜利、并严格优于当前结果，才继续 rank 4..7。严格沿用原深度交错、完整路线比较、共享时间／节点额度、可接受目标与零战损停止。起始事件现在写 `initial_ranks_per_depth=4`、`max_ranks_per_depth=8` 和 `rank_extension=after_improvement`；`stop=rank_limit` 表示至少一个前沿层仍有未调度候选。

回算上一轮已分析的 6 组实机探索记录：适应策略会保留 64/231 次续搜，4/4 个已记录改进均保留，预计续搜展开 237,963（比未截断的 1,002,801 少 764,838；比统一 cap 8 再少 134,660）。这是对已完成工作量的反事实筛选，不是重新模拟，也不能证明 rank 4+ 不含未观测到的独有更优解。

与统一 cap 8 的 OfflineSearchHarness 同根 `Coordinator` 对照：TERROR_EEL_ELITE / SILENT / `CSOPT20261001`，beam 96、每 solver 8,000 nodes：根与完整动作路线一致，均完整胜利、战损 53、0 药水、`boundary=None`；总展开 108,788→62,229、总转移 317,326→181,831、续搜 15→8 次（续搜展开 100,813→54,254），离线请求墙钟 16.77→11.48 秒。PHANTASMAL_GARDENERS_ELITE / SILENT / `CSOPT20261001C`，beam 64、每 solver 1,500 nodes：根与路线、38 战损一致，两侧均为 `NodeLimit` 且未胜；总展开 26,764→14,973、总转移 137,345→77,097、续搜 16→8 次（续搜展开 25,264→13,473），离线请求墙钟 10.83→7.24 秒。两根均是前 4 rank 没有严格改进，因此该实验只实测了收在 4 的路径，扩至 8 的路径目前仅由日志回算支持。墙钟仅用于无头同机探索，不外推实机帧时间。

扩展路径单独对照：TERROR_EEL_ELITE / SILENT / `CSOPT20261001D`，beam 96、每 solver 8,000 nodes。统一 cap 8 与自适应候选根、完整路线相同；深度 2 / rank 1 两侧都找到 59 战损的完整胜利，0 药水、`boundary=None`、结束回合 17。候选仍将深度 2 扩到 rank 7、深度 1 收在 rank 3；严格改进保留。续搜 16→12 次、续搜展开 123,170→92,616、总展开 131,170→100,616、总转移 378,010→289,200、离线请求墙钟 20.46→16.31 秒。该根验证了扩展分支与一次真实改进的保留；单一种子仍不能证明整体策略质量不降。

最终恢复自适应源码后的 Release 构建为 0 警告／0 错误，`git diff --check` 通过，本地 `mods/CombatSolver` 五文件部署成功。所有运行均为 OfflineSearchHarness；未启动可见 Steam。

### 改进后尾部 rank 上限收至 5（2026-10-01）

保留每层 rank 0–3；严格改进才启用尾部，最多续搜至 rank 5（`max_ranks_per_depth=6`）。用户最新一局的深度 2 扩展层在 rank 0 与 rank 3 改进，rank 4–7 未改善，因此该窗口保留已观测到的两次改进。

与现行 4/8 自适应策略同根对照：TERROR_EEL_ELITE / SILENT / `CSOPT20261001D`，beam 96、每 solver 8,000 nodes。两边路线动作序列相同（69 项），均完整胜利、战损 59、0 药水、`boundary=None`；首次改进均在深度 2 / rank 1。cap 6 把续搜 12→10、续搜展开 92,616→77,950、总展开 100,616→85,950（−14.6%）、总转移 289,200→247,414（−14.4%）。cap 8 的 rank 6、7 均未改善。此为单次独立进程对照，耗时仅作观察，不构成稳定提速结论；其他战斗中的 rank 6+ 仍可能包含独有更优路线。

候选 Release 构建 0 警告／0 错误；OfflineSearchHarness 在固定根达到 M2、完整胜利且结果边界为 `None`。最终五文件已部署到本机游戏 `mods/CombatSolver`；本轮无可见 Steam 验证。

## PR #144 最终修复与合并验证（2026-09-28）

以 `main@f47c447a` 整合 PR head `1e914b38`，修正 `ReclaimWithinSearch` 主动退出路径的恢复许可，并将复审夹具纳入 `GcRecoveryChecks.RunExplicitDefaultExit`。原候选同一真实 CLR 边界失败：主动退出后 `enabled=True / attempts=1 / restarts=1`；原 main 通过。修复后 `recovery-lifecycle` 3 项、`checkpoint` 1 项通过，主动退出结果 `EXPLICIT_DEFAULT_EXIT_OK attempts=0 restarts=0 forced=0`，正常恢复仍为 starts=1/restarts=1/forced=0，取消与退出清理通过。

复用前一轮候选 `recovery` 11 项成功证据；本次保留同一退避与分类实现，只修复实际主动退出调用处。最终 Mod Release 构建 0 警告／0 错误，关闭自动复制，供合并后的本地五文件部署复用。未运行 Linux、可见游戏性能或全量 GC 套件。红灯与原 main 对照位于 `.local/audit-pr144-latest-20260928/`，最终合同日志位于 `.local/pr144-merge-20260928/`。

## 0.47.2 发布定版（2026-09-28）

用户在版本与日志登记后授权全渠道发布。本次只更新文档中的开发状态，复用 `1b910969` 的成功 Release 构建和本地五文件部署；构建输入及行为源码未变。最小 ZIP 使用该构建及已提交的 manifest、两份许可文件，创意工坊更新说明由同版本中英日志完整转换。标签指向本次定版提交；统一脚本在发布前验证连接元数据，并按渠道记录结果到 `releases/CombatSolver-0.47.2.publish-state.json`。沿用下文已取得的定向行为证据，不重复构建、部署或行为测试，不运行 Linux 与完整发布门禁。

## 0.47.2 版本与日志登记（2026-09-28）

本轮仅修改版本与文档，行为源码及依赖保持 `3ad5ec33` 的已验证状态。复用下文 #140 原生生命周期、#143 四项原生合同、内存条 ServerGC 开／关和相关登记合同，不重复运行行为测试。核对 manifest/csproj 版本一致、中英条目和贡献者链接对应、相对文档链接及 diff；按版本变化执行一次 Release 构建与本地五文件部署，不触发完整门禁、Linux、打包、标签或渠道上传。

结果：版本／日志检查通过（中英各 7 条，5 个 PR 的作者及链接对应），diff 检查通过；从 `1b910969` 构建 Release，0 警告／0 错误，manifest、DLL、Windows MemoryCleaner 和两份许可文件已一次复制至确认的游戏 `mods/CombatSolver` 目录。该登记阶段未打包、建标签或上传渠道；随后发布授权与定版见上节。

## PR #144 正文更新后的复审（2026-09-28）

远端 head 保持 `6498169c`；以 `main@1471c296` 集成后运行候选的 `CombatSolver.GcPolicyChecks -- recovery`（9 项）、`-- recovery-lifecycle`（2 项，实际 CLR starts=1/restarts=1/forced=0）、`-- checkpoint`（1 项），全部通过。覆盖已有恢复、取消、显式退出和释放边界；未声称覆盖 RegionSizeUnsupported/PlatformUnsupported 后重试，或证明取消每 scope 三次限制的收益。没有重跑全部八套件。候选仍未合入，具体调用链审计见 [合并审计](../refactoring/merge-audit-20260928.md)。

## PR #140 资源恢复后的原生复审（2026-09-28）

当前 main `afeb0e01` 上集成原候选；Release 0/0、Windows 边界 238 通过。`PR140-PRECOMBAT-WORKER -VerifyPreCombatForecastApi -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120` 请求 `0a76d46e5d9545d4816840bbd80f0d0f`，75.3 秒 Passed。覆盖完整预测、同进程复用、显式中断活动请求、随后新 worker 模拟成功、自动关闭、Mod 写入隔离、设置令牌失效，以及原跑局/RNG不变；WorkerStarts=2、WorkerReuses=3。启动器已删除实例 `.local/headless-instances/audit-pr140-recheck`，原始证据 `.local/audit-recheck-20260928/pr140-worker`。11 项请求工具合同复用前轮同源码结果，不重复运行；未运行 Linux 或可见性能测试。

## 内存条与 PR 阻塞验证（2026-09-28）

`MEMORY-DISPLAY-CONTRACT` 在两个自有隔离游戏进程通过：`cad74c8a0c664b3d89679cbc967e7469` 实际 ServerGC=False，`90a36a763671460eaed1bf5a2da535fd` 实际 ServerGC=True；各一节点／四转移，25.5/24.8 秒，实例均删除。真实采样的物理已用超过 GC 压力阈值，已用＋可用等于物理总量。固定快照覆盖空闲、搜索、回收、超阈值、未知物理数据和 GC 阈值不改变物理条形；回收显示合同不代表人为制造高压回收。

OfflineSearchHarness 环境变量 `OFFLINE_HARNESS_MEMORY_DISPLAY_CHECKS=1`，`--milestone M1 --language eng|zhs|zht` 分别通过；设为 `baseline` 并通过 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 指向旧 DLL，生产入口直接复现 6 GB／4 GB。Release 与离线宿主 0/0、Windows 边界 238 通过。没有 Linux／可见性能结论。

首轮 #140 修复后的工具合同 11 项通过，原生 worker 生命周期当时受资源阻塞；后续复审已通过并合入，见本文件上方记录。#144 候选在原三次尝试上限合同失败，未合入；最终 main 的 GC `recovery` 9 项、`recovery-lifecycle` 2 项通过，不混用两个版本的结论。详情及证据边界见 [合并审计](../refactoring/merge-audit-20260928.md)。

## PR #143 合并验证（2026-09-28）

本轮最终候选 Release 0/0、Windows 边界 238 通过。离线 Infused Core 14 项与 N4/N8/N17 独立前缀 oracle 通过。资源恢复后原生 FIXED-PREFIX-TURN-OUTCOMES、OPENING-DISCARD-CHOICE-VALUE、TURN-SETUP-FIXED-PREFIX-STAMPEDE、INITIAL-TOOLBOX-INFUSED-CORE 四项通过，分别 47.9/28.6/30.2/26.6 秒；包括固定前缀三回合实际续用、七表缓存、完整续用戳、弃牌 DOP1/DOP2 和初始原生选择。runId 与具体边界见 [合并审计](../refactoring/merge-audit-20260928.md)。实例全部清理，未跑全量 CoverageCatalog 或性能大样本。

## PR #142 合并验证（2026-09-28）

`dotnet run --project tools/testing/checks/ModelIdCacheChecks/ModelIdCacheChecks.csproj -c Release`：7 项通过。链接原 main 生产补丁时明确失败于注册前无前缀缓存；最终补丁核对原版、两个动态程序集同名类型、注册前／后、晚加载、并发与 null 原生入口。替身只提供模型 ID 和补丁元数据，缓存逻辑直接链接生产文件。主项目及离线宿主 Release 0/0，Windows 门禁通过。`PR142-MODEL-REGISTRY` 在主机准入阶段超时、实例删除，Windows 原生初始化事件尚未实测。

## PR #139 合并验证（2026-09-28）

`TurnPhaseMirrorChecks` 的默认／`--seal`／`--start`／`--start --seal`／`--after-player-start`／`--after-player-start --seal` 六组分别 28、3、27、2、52、5 项通过。合并时补齐具体模型忽略登记与复合登记原子性：红灯为 `Ignored accepted an abstract model`；绿灯覆盖抽象、无关类型、重复、失败后 Early 正常登记及派发。Release 0/0；Windows 结构门禁通过。未运行原生第三方 Mod 或 Linux 门禁。

## PR #138 合并验证（2026-09-28）

Release 0/0、Windows 结构门禁 238；GA-SILENT-BOSS-00 与本轮重构基线逐位相同，证据 `.local/audit-20260928/pr138-comparison`。`GENERIC-LOOP-HELLRAISER-PILLAGE-SINGLE-CURRENT-V0111` 原生请求 `0eaff0f74d134a0aa7e4e4bb453cbc8e` Passed，固定 5 秒搜索、DOP1、增量验证，首动作 PILLAGE、0 战损、首回合击杀；1 展开／2 转移，覆盖动作内部循环，不覆盖循环租约。120 秒请求内完成、实例删除。未运行批量性能或 Linux 门禁。

## 合并审计：并行失败作业记账（2026-09-28）

- 最终源码 `StrategyCorpus/run.py --manifest coverage/corpora/strategy/p2.json --case ga-silent-boss --out .local/audit-20260928/refactor-sentinel` 为 comparable；与 `.local/strategy-refactor-p6/final-sentinel` 比较逐位相同。最终 Windows 结构门禁 238 通过。

- `pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId ADMITTED-JOB-FAILURE-ACCOUNTING -EnemyCurrentHp 999 -VerifyPredictionFailureBoundaries -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -HeadlessInstance <独立实例> -EvidenceDirectory <证据目录> -CleanupInstanceOnExit`。
- 未修复生产代码时 `8a6d697c8a404adeb39e6ac12c9a2018` Failed：worker 分配 67,108,888 字节，请求仅记录 116,856。修复后 `747fc356ae864a31b69459e1627ef400` Passed，覆盖取消、原异常、已发生分配记账、排空及同根后续 DOP2；普通预测失败边界也通过。实例均已删除。原始证据 `.local/audit-20260928/refactor-red`、`refactor-green`。
- Release 构建 0/0；Windows 结构门禁 238；StrategyCorpus 比较工具 5 项、first_loss 1 项、PowerCardValuationChecks 104 项通过。历史 P4/P5 六根原始结果仅排除后加归因字段后逐位一致，本次未重跑六根。Linux 与可见性能未执行。

## 策略重构 P7a 单项持续效果权重（2026-09-28）

- `pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -CheckpointArchivePath .local/issue-bundles/worldline-top150-20260926/raw/88619c63f91b48998737d7a9d623e2df.zip -CheckpointSelector start -ReplayMode SearchOnly -FixedSearchBudget -PerformancePresetForTest VeryHigh -SearchBudgetOverrideMilliseconds 180000 -SearchMaxDegreeOfParallelismForTest 8 -EnableNoGcRegionForTest 0 -BeamWeightTermForTest PersistentBuffDelta -BeamWeightScaleForTest 1.5 -TimeoutSeconds 240 -EvidenceDirectory .local/strategy-refactor-p7a/persistent-1p5-100 -CleanupInstanceOnExit` Passed，实例清理。对 `.local/strategy-refactor-p6/cross-turn-100`，根戳记及除该扰动外的政策相同；战损 22→55 HP，药水 2→2，结束回合 17→25。默认值不变，不追加同系数扫描；本轮未运行 Linux 门禁或其他权重组合。

## 策略重构 P6 收口对照（2026-09-28）

- #90、#100、#101 均用 `pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -CheckpointArchivePath <对应原包> -CheckpointSelector start -ReplayMode SearchOnly -FixedSearchBudget -PerformancePresetForTest VeryHigh -SearchBudgetOverrideMilliseconds 180000 -SearchMaxDegreeOfParallelismForTest 8 -EnableNoGcRegionForTest 0 -TimeoutSeconds 240 -EvidenceDirectory <对应目录> -CleanupInstanceOnExit` 顺序运行，均 Passed、实例清理。证据为 `.local/strategy-refactor-p6/potion-plan-90`、`final-100`、`final-101`。#90 对 P7b 原结果的动作、质量、续用、剪枝和请求展开／转移／选择分支全同，0 战损／最终 49 HP；#100、#101 对先前 P6 同根结果的这些字段全同，分别 22 战损／2 药、58 战损／1 药。对 P6 前同根同政策基线，#100 为 41→22 战损，#101 为死亡→胜利。跨回合计划类型改动后单独重跑 #100，证据 `.local/strategy-refactor-p6/cross-turn-100`，动作、质量、续用、剪枝与工作量继续全同。
- `python tools/search/StrategyCorpus/run.py --manifest coverage/corpora/strategy/p0.json --out .local/strategy-refactor-p6/final-sentinel --case ga-silent-boss` 为 `comparable`；`python tools/search/StrategyCorpus/compare.py --left .local/strategy-refactor-p5/final-shared-scheduler-dop1 --right .local/strategy-refactor-p6/final-sentinel --out .local/strategy-refactor-p6/final-sentinel-comparison` 对 GA-SILENT-BOSS-00 判定“逐位相同”。左侧另外五根未在右侧运行，比较器标注“缺少一侧”，不参与本轮哨兵判定。最终源码 Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=238`；Linux 门禁未运行。

## 策略重构 P6 免费药计划证据（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release` 成功，0 警告、0 错误；`pwsh -NoProfile -File tools/inspection/verify-refactor-boundaries.ps1` 输出 `REFACTOR_BOUNDARIES_OK search_files=238`。未运行 Linux 门禁。#90 同根和已达标哨兵尚未执行：用户游戏进程正在运行，依约不启动无头实例。本次改变计划成员的地平线资格，静态与编译结果不能证明行为或质量保持。

## 策略重构 P6 延后复制效果登记（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release` 成功，0 警告、0 错误；`pwsh -NoProfile -File tools/inspection/verify-refactor-boundaries.ps1` 输出 `REFACTOR_BOUNDARIES_OK search_files=238`。未运行 Linux 门禁。用户的游戏进程仍在运行，未启动无头实例；#101 同根行为对照及已达标哨兵仍待执行，编译和结构门禁不构成行为等价证据。

## 策略重构 P6 共用计划成员派发（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release` 成功，0 警告、0 错误；`pwsh -NoProfile -File tools/inspection/verify-refactor-boundaries.ps1` 输出 `REFACTOR_BOUNDARIES_OK search_files=237`。未运行 Linux 门禁。现有游戏进程运行且主机可用内存不足以取得无头实例租约，未执行 #100／#101 同根结果对照；当前仅有静态与编译证据，不宣称行为逐位一致。

## 策略重构 P6 类型化收益证据（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。#101 首次同根请求在写结果前以 `-1073741819` 退出，证据 `.local/strategy-refactor-p6/typed-payoff-101/launcher-result.json`，无崩溃堆栈，原因未定位；启动器清理了实例。同源码同配置重试 Passed，根戳记、执行政策、完整动作、冻结质量与 `.local/strategy-refactor-p6/semantic-copy-101` 全同，均为胜利、58 战损／1 药，证据 `.local/strategy-refactor-p6/typed-payoff-101-retry`。重试进程日志中出现 Godot 的 `Invalid Task ID` 与对象终结器断开信号错误，但仍完成请求，无法据此归因首次崩溃。Linux 门禁未运行。

## 策略重构 P6 复制效果提名（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release --no-restore` 成功，0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。#101 `a422c1c56022446c85f6ce00962019c4` 的 `combat_start` 在 VeryHigh／180 秒／DOP8 下严格恢复并 Passed；与 `.local/strategy-refactor-p6/horizon-101` 相比，根戳记、执行政策、完整动作与冻结质量全同，均为胜利、58 战损／1 药、第 16 回合结束。新证据 `.local/strategy-refactor-p6/semantic-copy-101`；实例由启动器清理。尚无新增优化量，未运行 Linux 门禁。

## 策略重构 P5 共享调度收口（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release --no-restore` 成功，0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。`python tools/search/StrategyCorpus/run.py --manifest coverage/corpora/strategy/p0.json --out .local/strategy-refactor-p5/final-shared-scheduler-dop1 --case report-24 --case report-37 --case report-81 --case report-89 --case ga-ironclad-elite --case ga-silent-boss` 六根均 `comparable`。对 `.local/strategy-refactor-p4/after-p4-20260928` 同政策基线，排除后加的 P8a `searchWorkAttributions` 后，六根完整动作、续用、终局、工作量及剪枝逐位相同；原始比较证据 `.local/strategy-refactor-p5/final-shared-scheduler-comparison`。GA-SILENT-BOSS-00 的 DOP8 对 `.local/strategy-refactor-p5/executor-after-dop8` 的 122 个非时序字段、动作和续用全同；搜索耗时 24,053.0502→24,091.1489 ms，worker 分配 11,885,994,992→11,879,940,960 字节，均仅作单样本观测。DOP8 证据 `.local/strategy-refactor-p5/final-shared-scheduler-dop8`；无头实例已清理。Linux 门禁依用户要求未运行。

## 策略重构 P5 回合尾部作业（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。GA-SILENT-BOSS-00 在 VeryHigh／25,000 节点／110 秒配置下，DOP1 对 P4 同政策基线排除后加归因字段后逐位相同；DOP8 对 `.local/strategy-refactor-p5/executor-after-dop8` 的 122 个非时序字段、动作和续用全同，均为 44 战损、69,257 展开、222,131 转移。证据 `.local/strategy-refactor-p5/serial-tail-job-dop1` 与 `tail-job-dop8`。其余玩家根与生成根、Linux 门禁未运行。

## 策略重构 P5 药水作业（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。#24 `f88c625680e64a2c99a2ab8844abcbdd` 的 `combat_start` 严格恢复后，VeryHigh／25,000 节点／DOP1／110 秒搜索 `comparable`；同 P4 基线排除后加归因字段后，完整动作、续用、终局、工作量、剪枝逐位相同。证据 `.local/strategy-refactor-p5/serial-potion-jobs-report24`。DOP8 药水根、其余语料、Linux 门禁均未运行；P5 尚未收口。

## 策略重构 P5 串行挂起选择作业（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。#89 `10d01cc2d1f7445c8ff72e76e783aeb0` 严格恢复 `combat_start` 后，以 VeryHigh／25,000 节点／DOP1／110 秒固定配置取得 `comparable`；同 P4 基线比较，排除后加的 `searchWorkAttributions` 后完整动作、续用、终局、工作量及剪枝逐位相同。证据 `.local/strategy-refactor-p5/serial-choice-jobs-report89`。未跑其余语料、DOP8 或 Linux 门禁；药水与尾部作业尚未迁移。

## 策略重构 P5 串行卡牌作业（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。GA-SILENT-BOSS-00 的 DOP1、VeryHigh／25,000 节点／110 秒搜索 `comparable`；同 P4 同政策基线比较，排除后来新增的工作归因数组后，完整动作、续用、终局、工作量及剪枝逐位相同。证据 `.local/strategy-refactor-p5/serial-card-jobs-dop1`。只覆盖串行卡牌作业；未跑其余五根、DOP8 或 Linux 门禁，P5 未收口。

## 策略重构 P5 作业状态所有权（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release --no-restore` 成功，0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。GA-SILENT-BOSS-00 在 VeryHigh／25,000 节点／DOP8 下同既有 `.local/strategy-refactor-p5/executor-after-dop8` 比较，122 个非时序字段、路线与续用全同：44 战损、69,257 展开、222,131 转移。证据 `.local/strategy-refactor-p5/admitted-parent-outside-executor-dop8`。该结果只验证状态所有权搬迁；串行接入和 P5 全语料尚未执行，Linux 门禁依要求不运行。

## 策略重构 P5 卡牌回放入口（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release --no-restore` 通过，0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。`python tools/search/StrategyCorpus/run.py --manifest coverage/corpora/strategy/p0.json --out .local/strategy-refactor-p5/choice-dispatch-after-dop1 --case ga-silent-boss` 为 comparable。对 `.local/strategy-refactor-p4/after-p4-20260928` 同根 DOP1 基线比较时，新增的 P8a 归因数组是唯一协议字段差异；排除该后加字段后，质量、完整动作、续用、全部其余非时序指标和剪枝逐位相同。证据 `.local/strategy-refactor-p5/choice-dispatch-compare-dop1`。未跑其余五根、DOP8 或 Linux 门禁；P5 尚未收口。

## 策略重构 P8c 同根路线首分歧（2026-09-28）

- `python tools/search/StrategyCorpus/route_divergence.py --baseline .local/strategy-refactor-p7c/baseline-97 --witness .local/strategy-refactor-p7c/target-reps-97-retry --out .local/strategy-refactor-p7c/route-divergence-97.json` 成功；两份旧实验结果的根戳记和执行政策相同，质量顺序判定见证路线更好。共同前缀为首张精神过载，第 2 步从灵体变为致死性；旧路线 21 战损／0 药，见证路线 9 战损／0 药。与 #81 不同根配对时明确拒绝。该命令只读取已有证据，未启动游戏、未验证当前源码可重现 9 战损，也未定位搜索内的首个丢路阶段；Linux 门禁未运行。

## 策略重构 P5 执行器提交合同（2026-09-28）

- Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。GA-SILENT-BOSS-00 同根、VeryHigh／25,000 节点／110 秒：DOP8 对上次源码的 122 个非时序字段及动作全同，均胜利、44 战损／0 药、总展开 69,257、转移 222,131；DOP1 的质量、动作、续用及非时序指标全同。#81 `4eb25e79483c462089f9c6088d650c77` 开战根的无头恢复与固定预算搜索 `comparable`，对上一轮同根源码的所有逐位字段一致，均为胜利、31 战损／0 药／最终 55 HP、NodeLimit。证据 `.local/strategy-refactor-p5/executor-after-dop8`、`executor-after-dop1`、`executor-report81` 及 `executor-report81-comparison`；实例由运行器清理。未运行 Linux 门禁或整批语料。

## 策略重构 P8a 直接成员归因（2026-09-28）

- Release 构建 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=236`。#81 `4eb25e79483c462089f9c6088d650c77` 的 `combat_start` 在 VeryHigh／25,000 节点／110 秒／DOP1 下严格恢复并得到 `comparable`；请求总展开 161,521、转移 680,415、选择分支 0，分项为 `PrimaryBeam` 22,981／93,916、`SmartPotionGradient` 50,000／224,589、`OpeningPowerRouteMember` 88,540／361,910，合计逐项相等。证据 `.local/strategy-refactor-p8a/direct-attribution-report81`，实例由运行器清理。这只直接验证当前触发的三个类别；新颖性、前两回合侦察等未启用成员本轮未运行，旧 13 个超时包未重跑，Linux 门禁未运行。

## 策略重构 P5 回合尾部准入（2026-09-28）

- Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=236`。GA-SILENT-BOSS-00 同根 VeryHigh／25,000 节点／110 秒，DOP8 与本轮改动前的路线及 122 个非时序字段全同，均胜利、44 战损／0 药、请求总展开 69,257、转移 222,131；DOP1 的质量、动作、续用和非时序指标全同。证据 `.local/strategy-refactor-p5/choice-plan-after-dop8`、`endturn-admission-after-dop8`、`choice-plan-after-dop1`、`endturn-admission-after-dop1`。该场景不证明周期出口批次路径命中；未跑 Linux 门禁或其他包。

## 策略重构 P5 普通卡牌选择计划（2026-09-28）

- 改动前单次采集 GA-SILENT-BOSS-00 的 DOP8 固定根；改动后同根、VeryHigh、25,000 节点、110 秒，DOP8 的路线与 122 个非时序字段一致，均胜利、44 战损／0 药、请求总展开 69,257、转移 222,131。当前源码 DOP1 对前次同源码构建的基线，质量、动作、续用和非时序指标全同。证据 `.local/strategy-refactor-p5/choice-plan-baseline-dop8`、`choice-plan-after-dop8`、`choice-plan-after-dop1`。离线与语料比较器已将新工作归因数组里的 GC 次数／暂停作为波动字段排除；首次未经排除的对照只在这些字段报差异，没有重跑搜索。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=236`；Linux 门禁依用户要求不运行。

## 策略重构 P8a 主 Beam 工作归因（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false --no-restore` 成功，0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=236`。离线 GA-SILENT-BOSS-00 当前源码单次请求 `comparable`，70,460 个展开节点归为 `PrimaryBeam` 9,017、`RefinementBeam` 15,983、`OpeningPowerRouteMember` 45,460，合计与请求总数一致。与 `.local/strategy-refactor-p8a/attribution-silent` 相比，身份、动作、质量、结果、旧非时序指标及剪枝计数完全相同，只有 `searchWorkAttributions` 分类变化；证据 `.local/strategy-refactor-p8a/primary-attribution-silent` 与 `primary-attribution-comparison`。未运行 Linux 门禁或超时包。

## 策略重构 P7a 多项权重矩阵（2026-09-28）

- `python -m py_compile tools/search/StrategyCorpus/matrix.py` 通过。GA-SILENT-BOSS-00 使用 `.local/strategy-refactor-p8a/attribution-silent` 的当前源码无扰动基线，分别单次运行 `EnemyHp:0.8`、`PersistentBuffDelta:1.2`，两次均为 `comparable`；矩阵工具核对同根和除扰动外相同政策。基线和两次扰动均为胜利、44 战损／0 药／最终 26 HP，第 9 回合结束；两项扰动的终局 score 都从 9999299954 降到 9999299953，故按冻结质量比较为变差。证据 `.local/strategy-refactor-p7a/matrix-current-v2`。未扩展到整批语料，未调整生产权重；本次只有 Python 工具与文档改动，未重复 C# 构建或运行 Linux 门禁。

## 策略重构 P8c 丢路查询（2026-09-28）

- `python tools/search/ContextualOrdering/test_first_loss.py` 通过 1 项构造测试：两个不同求解器均有编号 1、2 的保路边界，编号 1 的目标前缀分别在全局 Beam 与后续仲裁落选，编号 2 作为各自的截断末边界忽略；查询输出两条独立结果和同状态别名。未运行玩家 ZIP 自动采集或真实路径诊断；Linux 门禁依用户要求不运行。

## 策略重构 P8a 超时进度取证（2026-09-28）

- 请求工作归因：GA-SILENT-BOSS-00 的固定生成根在当前源码可比较，总展开 70,460 = `OpeningPowerRouteMember` 45,460 + `UnattributedDirect` 25,000；总转移 225,665 = 146,815 + 78,850；总选牌 12,296 = 8,874 + 3,422。总搜索耗时约 35,950.7 ms 等于两分项之和。相对 `.local/strategy-refactor-p7b/ga-silent-sentinel`，动作、结果和旧非时序指标一致，只有新增归因字段不同；证据 `.local/strategy-refactor-p8a/attribution-silent` 与 `attribution-comparison`。没有逐个跑 13 个超时包。
- 新超时请求会在停止常驻实例后、覆盖会话监控状态前，将报告 ID 和更新时间均匹配本请求的最近监控快照保存至请求证据；快照补充当前成员节点上限、结束节点和已完成回合层。仅执行 CheckpointTool Release 编译及 Windows 结构门禁；尚未实际制造一次超时，不能声称运行时取证已通过。旧 13 个超时包没有这些新字段，不从历史 `timeout` 状态推断单一主因。Linux 门禁依用户要求不运行。

## 策略重构 P7c 目标代表试验（2026-09-28）

- #97 `5b37246d49354de9a2e8523e8a2c62c8` 的 `combat_start`、VeryHigh／180 秒／DOP 8：旧策略完整胜利 21 战损／0 药／最终 35 HP；提前预约每目标代表的实验为 9 战损／0 药／最终 47 HP，请求总展开 301,145→479,327，搜索耗时约 82.0→133.8 秒。证据 `.local/strategy-refactor-p7c/baseline-97` 与 `target-reps-97-retry`；首次试验启动被私有游戏路径校验拒绝，未进入搜索，实例已清理。
- 已达标多敌 #81 在相同根和政策下，实验为 9 战损／0 药，同源码基底撤下试验后为 8 战损／0 药；证据 `.local/strategy-refactor-p7c/sentinel-81` 与 `sentinel-81-current-baseline`。该 1 HP 退化导致实验源码撤回。两次完整请求均 Passed，实例已清理；Linux 门禁依用户要求不运行。

## 策略重构 P7b 混沌药生成链（2026-09-28）

- #90 `945939a12ac944999302b9b7f1cb34ea` 同一 `combat_start`、VeryHigh／180 秒／DOP 8、强制使用迅捷与混沌的记录政策：基线完整胜利 3 战损／2 瓶原有药／最终 46 HP，当前完整胜利 0 战损／2 瓶原有药加 2 瓶免费生成药／最终 49 HP；请求总展开 460,810→500,000，总搜索耗时约 145.0→157.7 秒。当前路线第 2 回合连续使用四瓶药，结束于第 6 回合。证据 `.local/strategy-refactor-p7b/baseline-90` 与 `chain-attack-90`。中间仅固定原首回合的生成链实验为 3 战损、第 2 回合结束；加合法进攻跟进后才追平人工。两个无头实例均已清理。
- GA-SILENT-BOSS-00 同政策、25,000 节点／110 秒、DOP1，P7a 无扰动基线对当前源码的动作、结果及非时序计数逐位相同，均为 44 战损／0 药；证据 `.local/strategy-refactor-p7a/generated-baseline` 与 `.local/strategy-refactor-p7b/ga-silent-sentinel`。Release 编译和 Windows 结构门禁通过；Linux 门禁依用户要求不运行。未执行完整自动部署，不能据此宣称实机计划回放通过。

## 策略重构 P7a 权重敏感度（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=235`；Python 脚本语法检查通过。`run.py --case` 分别对 #24 玩家根与 GA-SILENT-BOSS-00 采集无扰动基线及 `CurrentEnergy:0.8`，四次均 `comparable`。`sensitivity.py` 接受两组同根对照且核对其余政策相同；#24 前后均胜利、0 战损／1 药／最终 57 HP，生成根前后均胜利、44 战损／0 药／最终 26 HP。两根动作与工作量有差异，质量分类均为不变。证据在 `.local/strategy-refactor-p7a/`；无头实例已由运行器清理。默认权重未调整；Linux 门禁依用户要求不运行。

## 策略重构 P6 首回合计划（2026-09-28）

- 计划地平线：`dotnet run --project tools/testing/checks/PowerCardValuationChecks/PowerCardValuationChecks.csproj -c Release` 通过，覆盖未兑现计划不续期、兑现后在第 16 至 20 个无进展回合续期及第 21 回合结束续期（普通上限 16、牌堆周期 5）。Release 编译及 Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=235` 通过。#101 与 #100 最终源码同根复测均 Passed，战损／用药／最终 HP 与接入前相同，请求总 expanded／transitions／choice branches 也相同；证据 `.local/strategy-refactor-p6/horizon-101` 与 `horizon-100`。这两根未直接触发续期，长线搜索触发效果尚未有实战样本；Linux 门禁未运行。
- 跨回合能力计划：#100 `88619c63f91b48998737d7a9d623e2df` 的 `combat_start`、VeryHigh／180 秒／DOP 8，修改前完整胜利 41 战损／2 药、最终 29 HP；修改后完整胜利 22 战损／2 药、最终 48 HP。`PLAN_SEARCH_DISCOVERY count=0` 后，末段 `DEFERRED_POWER_PLAN` 选中第二回合飞刀扇前缀，成员展开 53,316。证据 `.local/strategy-refactor-p6/baseline-100` 与 `.local/strategy-refactor-p6/deferred-100-final-pass`。
- 已达标哨兵 GA-SILENT-BOSS-00：DOP8、VeryHigh、25,000 节点／110 秒，修改前后 121 个非时序字段全同，完整胜利 44 战损／0 药；证据 `.local/strategy-refactor-p5/potion-admission-dop8` 与 `.local/strategy-refactor-p6/deferred-sentinel-dop8`。P6 尚需计划驱动地平线，不能据此宣称阶段全部完成。
- #101 `a422c1c56022446c85f6ce00962019c4`：同一 `combat_start`、VeryHigh／180 秒／DOP 8，基线死亡、预计战损 70／0 药；计划成员完整胜利、战损 58／1 药、最终 12 HP。完整证据在 `.local/strategy-refactor-p6/baseline-101` 与 `.local/strategy-refactor-p6/plan-101-after-gradient`。
- #100 `88619c63f91b48998737d7a9d623e2df`：首回合计划入口阶段结果与基线同为胜利 41 战损／2 药、最终 29 HP；放在 Smart 审计中间的 44 战损跨回合实验已撤回，证据分别在 `.local/strategy-refactor-p6/baseline-100`、`plan-100-v1`、`plan-100-target-payoffs`。末段入口的最终收益见本节首项。
- 当前源码 Release 编译成功，Windows 结构门禁返回 `REFACTOR_BOUNDARIES_OK search_files=234`。未运行 Linux 门禁或整批语料；构造 P6 计划入口后的哨兵尚未重测。

## 策略重构 P5 共享候选准备（2026-09-28）

- 药水候选准入合并：Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=234`。离线 GA-SILENT-BOSS-00 同政策 25,000 节点／110 秒，当前 DOP1 对既有 P5 DOP1、当前 DOP8 对既有 P4 DOP8，各比较 121 个非时序字段全同；两者均完整胜利、44 战损／0 药。证据在 `.local/strategy-refactor-p5/potion-admission-dop1` 与 `potion-admission-dop8`。Linux 门禁依用户要求不运行。
- `ExpansionPlan` 同时供串行展开与并行准备读取卡牌、药水候选。`dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false --no-restore` 成功，0 警告、0 错误；`pwsh -NoProfile -File tools/inspection/verify-refactor-boundaries.ps1` 返回 `REFACTOR_BOUNDARIES_OK search_files=232`。尚未运行搜索语料或 DOP1／DOP8 对照，不能据此认定行为等价。Linux 门禁按用户要求不运行。
- 卡牌／药水子节点字段抽入共享工厂：Release 编译 0 警告、0 错误，Windows 门禁返回 `REFACTOR_BOUNDARIES_OK search_files=232`；本次只核对字段来源及调用位置，尚未运行固定根。Linux 门禁按用户要求不运行。
- 普通卡牌选择分派改走同一入口：Release 编译 0 警告、0 错误，Windows 门禁返回 `REFACTOR_BOUNDARIES_OK search_files=232`；尚未运行阶段语料。Linux 门禁按用户要求不运行。
- 离线 GA-SILENT-BOSS-00：当前 DOP8 与临时 P4 提交 `e248b6e3` 的 DOP8，同政策、25,000 节点、110 秒，`compare_results.py` 的 121 个非时序字段全同，战损 44，expanded 69,257，transitions 222,131。当前 DOP1 对 P4 DOP1 的早期边界对照也全同；但此后共享选择分派发生源码变化，DOP1 最终对照仍待运行。P4 自身的 DOP1／DOP8 已有动作次序与计数差异，两边战损同为 44。Linux 门禁未运行。
- 父节点入场准入合并后，Release 编译 0 警告、0 错误，Windows 门禁 `REFACTOR_BOUNDARIES_OK search_files=232`；仍待最终源码的固定根对照。Linux 门禁不运行。
- 卡牌候选准入与快照所有权合并：Release 编译 0 警告、0 错误，Windows 门禁 `REFACTOR_BOUNDARIES_OK search_files=232`；GA-SILENT-BOSS-00 当前 DOP8 对 P4 同 DOP 基线使用 `compare_results.py` 比较 121 字段全同，战损 44、expanded 69,257、transitions 222,131。其余根和 DOP1 最终源码对照尚未运行。
- P5 候选语义阶段对照：`python tools/search/StrategyCorpus/run.py --manifest coverage/corpora/strategy/p2.json --out .local/strategy-refactor-p5/after-p5-20260928` 的四个玩家根和两个生成场景均 `comparable`；`compare.py --left .local/strategy-refactor-p4/after-p4-20260928 --right .local/strategy-refactor-p5/after-p5-20260928 --out .local/strategy-refactor-p5/compare-p5-20260928` 六根逐位相同。P5 执行器接口和串行／并行调度统一尚未实施，不以该对照宣称 P5 全部完成。

## 策略重构 P4 登记表（2026-09-28）

- 药水成本档位及开局使用类型移至 `PotionValuationRegistry`：`dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false --no-restore` 成功，0 警告、0 错误；`pwsh -NoProfile -File tools/inspection/verify-refactor-boundaries.ps1` 返回 `REFACTOR_BOUNDARIES_OK search_files=229`。未运行无头语料；语义逐位对照留到 P4 收口。Linux 门禁按用户要求不运行。
- 白噪声、夜魇与复制药水的开局身份匹配移至 `OpeningActionRegistry`：Release 编译 0 警告、0 错误，Windows 门禁返回 `REFACTOR_BOUNDARIES_OK search_files=230`。未运行无头语料；Linux 门禁按用户要求不运行。
- 开局目标变体与每目标进攻代表移至 `TargetPlanRegistry`：Release 编译 0 警告、0 错误，Windows 门禁返回 `REFACTOR_BOUNDARIES_OK search_files=231`。原始 3 目标、前 3 次目标动作、最多 2 次改目标以及目标排序保持原值；语料对照尚未运行。Linux 门禁按用户要求不运行。
- P4 收口：`python tools/search/StrategyCorpus/run.py --manifest coverage/corpora/strategy/p2.json --out .local/strategy-refactor-p4/after-p4-20260928` 单次采集四个玩家根和两个生成场景，全部 `comparable`；`python tools/search/StrategyCorpus/compare.py --left .local/strategy-refactor-p3/after-p3-20260928 --right .local/strategy-refactor-p4/after-p4-20260928 --out .local/strategy-refactor-p4/compare-p4-20260928` 六根均逐位相同。无头实例由运行器清理；未运行 Linux 门禁。

## 策略重构 P2 外层补搜迁移（2026-09-28）

- 强制用药开局、回合边界、零费开局、战斗中精炼、回合末选牌和提前复制补搜已从外层 `Solve` 移至 `PostSearch`，前两回合探索改用 `SearchPassContext`。本边界沿用原调用顺序、预算读取与诊断标签。
- 本次只取得 Release 编译和 Windows 结构门禁证据；用户游戏运行期间未启动无头实例。行为逐位对照仍待执行，不能据此宣称搜索结果等价。Linux 门禁按用户要求未运行。
- 强制用药开局补搜三个成员的预算切片迁入 `SearchBudgetWindow`；本轮只检查原公式与调用位置、Release 编译及 Windows 结构门禁。该模式尚无行为对照，统一留到 P2 收口验证。
- 其余 `PostSearch` 成员的预算切片迁入相同窗口；长整型时间预检保留原位。仅做 Release 编译与 Windows 结构门禁，固定语料行为对照待 P2 收口一次运行。
- 请求管线现统一派发后处理 Pass。只检查 Release 编译、Windows 结构门禁和原调用顺序；游戏实例未启动，完整结果、接管时机与工作量的逐位对照仍待执行。
- 主 Pass 六处固定前缀成员预算切片改走账本窗口，保留原采样顺序与常数；仅做 Release 编译及 Windows 结构门禁，实际成员派发与结果对照合并到 P2 收口。
- 夜魇开局成员改走 `ProfileWindow`，仍使用配置时间帽和请求剩余节点；仅做 Release 编译及 Windows 结构门禁，实际路线对照留到 P2 收口。
- P2 收口：`python tools/search/StrategyCorpus/run.py --manifest coverage/corpora/strategy/p2.json --out .local/strategy-refactor-p2/after-p2-20260928` 运行一次，#24、#37、#81、#89 与两个生成场景均为 `comparable`，无头实例已清理。`python tools/search/StrategyCorpus/compare.py --left .local/strategy-refactor-p2/baseline-0471 --right .local/strategy-refactor-p2/after-p2-20260928 --out .local/strategy-refactor-p2/compare-p2-20260928` 报告六根逐位相同。最终行为源码的 Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=219`；Linux 门禁按用户要求未运行。
- P3 双药开局来源迁移：本边界只执行 Release 编译与 Windows 结构门禁；#17 两种顺序和六根语料的行为对照保留至 P3 收口一次运行，未把 P2 的旧证据算作新源码通过。
- P3 提前复制药水来源迁移：用药数仍在逐候选派发时读取，静态检查其捕获时机；行为对照留到 P3 收口，不额外启动一个游戏实例。
- P3 零费开局来源迁移：调度器按来源声明保留重复前缀，八次实际尝试上限仍在模式中；仅做 Release 编译与 Windows 结构门禁，行为对照留到 P3 收口。
- P3 双药调度定向验证：#17 `b8bafe147e09452b97111fb036a619ca` 的 `start` 以 VeryHigh、180 秒、DOP 8 运行 SearchOnly，请求 `98e0aa939d8648cf9b9c790559c16f9c` Passed。诊断依次记录 `BLOCK_POTION+SWIFT_POTION`（选中）与 `SWIFT_POTION+BLOCK_POTION`（未选中），均完整胜利、预计战损 69 HP；最终用药 2 瓶。实例已清理。这只验证双药前缀派发和原结果，不代替 P3 全阶段逐位对照。
- P3 回合末选牌两条固定前缀通道迁移：本边界只执行 Release 编译与 Windows 结构门禁；候选顺序和结果逐位对照合并到 P3 收口。
- P3 回合边界首轮锚点迁移：静态核对旧键去重、八个原序锚点和唯一可跳过的药水业务失败；Release 编译与 Windows 结构门禁后，行为对照仍合并到 P3 收口。
- P3 固定前缀请求构造迁移：静态核对求解器构造位于可选药水异常捕获之外，仅 `Solve` 期间的已定义异常记录原诊断。只执行 Release 编译与 Windows 结构门禁，不另跑一份问题包。
- P3 回合边界后续两条前缀迁移：原预算、可回放检查和八次续搜上限保留；本次仅执行 Release 编译与 Windows 结构门禁，行为对照待 P3 收口。
- P3 强制用药两条前缀迁移：静态核对强制用药基线、上下界、Boss 最早回合和原 `try/catch` 范围；本次仅执行 Release 编译与 Windows 结构门禁，行为对照待 P3 收口。
- P3 战斗中精炼两条前缀迁移：静态核对可选用药异常仍只覆盖 `Solve`、第二条用药数从更新后的 `selected` 读取；本次仅执行 Release 编译和 Windows 结构门禁，行为对照留到 P3 收口。
- P3 主 Pass 六种开局前缀迁移：静态核对每条请求覆盖原 `beamPolicy` 并保留预算、profile 标志与候选顺序；本次仅执行 Release 编译和 Windows 结构门禁，行为逐位对照留到 P3 收口。
- P3 开局能力审计四条前缀迁移：静态核对 `ResetFixedPrefixSchedulingBaseline=false`、可选用药诊断与总计调用顺序；仅执行 Release 编译和 Windows 结构门禁，行为对照留到 P3 收口。
- P3 强制至少用药审计三条前缀迁移：静态核对 `RequireAtLeastOne`、`primary.PotionCount` 上限及不重置调度基线；仅执行 Release 编译与 Windows 结构门禁，行为对照待 P3 收口。
- P3 Smart 开局药水前缀迁移：静态核对原 8／12 候选上限、药水数量和可选后验诊断，双端结构门禁禁止协调器主文件新增直接固定前缀构造；只运行 Release 编译与 Windows 门禁，行为对照待 P3 收口。
- P3 夜魇与能力路线两条前缀迁移：静态核对夜魇可选用药诊断、能力成员的专用进度阶段及工作量／时间采样位置；仅执行 Release 编译与 Windows 结构门禁，行为对照待 P3 收口。
- P3 前两回合实验续搜迁移：静态核对独立时间/追加节点额度与 `EARLY_TURN_CONTINUATION` 的可选用药诊断；仅执行 Release 编译与 Windows 门禁，默认关闭模式不纳入普通语料质量结论。
- P3 宽度组合纯移动：`RunBeamWidthPortfolioPass`、单成员结果包装与成员遥测整段迁入独立 partial 文件；只执行 Release 编译及 Windows 结构门禁，动作与工作量逐位对照并入 P3 收口。
- P3 补充审计纯移动：三种审计、Smart 梯度及内存检查整段迁入独立 partial 文件；只执行 Release 编译与 Windows 结构门禁，动作、诊断和工作量逐位对照并入 P3 收口。
- P3 收口：`python tools/search/StrategyCorpus/run.py --manifest coverage/corpora/strategy/p2.json --out .local/strategy-refactor-p3/after-p3-20260928` 的四个玩家根和两个生成场景均为 `comparable`。`python tools/search/StrategyCorpus/compare.py --left .local/strategy-refactor-p2/after-p2-20260928 --right .local/strategy-refactor-p3/after-p3-20260928 --out .local/strategy-refactor-p3/compare-p3-20260928` 报告六根逐位相同；无头实例已清理。最终 Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=228`；Linux 门禁按用户要求未运行。
