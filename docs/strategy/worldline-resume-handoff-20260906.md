# 世界线策略交接（2026-09-06，窗口切换）

## 当前任务，以此为准

用户已停止自动策略优化，当前只处理监控表“有玩家备注”第 2 包。读取现有证据，对比求解器和人工路线，找出人工产生质变的策略并汇报，随后由用户指定优化路线。此时不要自行修改策略、推进第 7/10 包或自动完成前 10 包。

用户最后明确质疑启动可见实机的必要性：策略与路线对比使用 headless 即可。当前无需再启动 Steam 可见游戏。之前“实机跑一遍”已通过实际部署取得证据，不能据此反复启动可见窗口。只有后续确需验证可见效果或用户明确指定时才考虑可见验证。

原目标“推进完 10 包”尚未完成，自动队列已暂停。以后若用户恢复优化：基线固定 High/180 秒；未达软目标须实际修改策略尝试 2–3 轮后再考虑跳过，单次失败不能直接跳过。死局目标为实际存活且战损合理；其他包目标为玩家减损幅度的一半：S <= O-ceiling((O-H)/2)，不是人工战损减半。包 4 按用户要求跳过。

## 工作区与已完成恢复

- 工作目录：`D:\Desktop\sts2mod\CombatSolver`。分支：`strategy/worldline-resume`，跟踪同名 origin 分支；不在 main 修改。
- 完整读 `AGENTS.md`，按任务读取 issue-bundle-triage、strategy-replay-iteration；修改策略才读 search-performance-optimization，修改语义另读 combat-semantic-change，推送读 release-gate。
- Git 恢复已完成，禁止再次应用旧恢复补丁或整文件覆盖新版本。保留现有日志、缓存、社区贡献和原生战前 API。
- `184a9cd`：恢复已验收策略净改动，迁移两个 fixture，并从原生二进制精确恢复旧包 choice/reward 网络 ID。包 1 当前实际 1 HP；包 2 旧 50 HP 验收本次没有复现。
- `4a607cb`：记录包 3、5、6 有界回放。
- `deddc84`：支持显式长请求并验证 High 基线；默认请求仍 120 秒。
- `a5266b4`：修正 SearchOnly 对开战选牌结果重复 Manual 重算的问题，增加缓存来源标记。生产搜索策略未因此改变。
- 上述提交均已推送。包 2 的两轮实验及包 7 的一轮实验已全部撤回，当前生产策略没有未验收实验。
- 本次交接批次另提交监控表、本文、可复制 prompt、可见测试脚本的政策/显式超时参数及对应开发/测试记录；提交号以 Git 最新提交为准。
- 根目录已有未跟踪发布 ZIP 与 `outputs/` 不删除、不提交；`.local/` 中证据留本机。
- 当前版本仍 0.31.2，行为改动记下一版本开发中，不创建新版本、发布包或上传工坊。

## 包 2：接下来只完成对比报告

包名：`35f36dc49e5c4d1aa17c477d13d9f221-CombatSolver-AEONGLASS_BOSS-20260903-152708-532.zip`。

原包：`.local/issue-bundles/20260905/raw/104714/logs/` 下同名文件。
人工材料根：`.local/issue-bundles/20260905/priority/35f36dc49e5c4d1aa17c477d13d9f221-CombatSolver-AEONGLASS_BOSS-20260903-152708-532/combat-solver/`。
重点读 `forensics/current/logs/godot.log`、`current-route.txt`、`replan-audit.txt`。原始日志有撤销操作，必须按最终保留分支分析，不能把撤销前动作拼成一条路线。

统一证据根：`.local/checkpoint-batch/worldline-resume/`。政策文件 `rank2-high-policy.json`：
High，DOP4，短搜 Beam36/5000 节点/12 秒，深搜 Beam90/25000 节点/180 秒；Smart；slot0 STABLE_SERUM Force，其余 ENERGY_POTION、SWIFT_POTION、DISTILLED_CHAOS Disabled；两项 Boss 政策 ProgressionFirst，阈值 0。旧导出缺政策时使用此显式补齐值，不能称所有字段均来自原包。

### 已有求解器证据

| 阶段 | 目录 / runId | 结论 |
|---|---|---|
| 严格恢复 | rank2-restore-fixed / 30148b32632f4ddaae88369730c154a8 | 开战完整状态校验通过 |
| High 基线 | rank2-high-search-resumed / 06e30a3430da450ea4a601b71f4789f4 | 预测死亡，敌 194；rank2-high-search 是中断请求，不用它作结果 |
| R1 潜在 Rage 攻击次数 | rank2-r1-rage-search / 83d99c169f144e8db7ba2dfe7ba0659b | 死亡，敌 125，缓存 false；已撤回 |
| R2 生命投资通道优先预计 HP | rank2-r2-survival-search / 2b04ef3094ca4f87a1e8303358398737 | 死亡，敌 247，缓存 false；已撤回 |
| 可见最小 Mod 栈实际部署 | rank2-visible-core-analysis / 21379368f7f0499bab2cac13f9a5f24e | start 严格校验，T11 死亡，计划外重算 0；预测敌 194，药水 T3 使用 |

实验补丁保存在证据根的 `rank2-r1-rage.patch`、`rank2-r2-survival.patch`，只是失败记录，不自动恢复。两轮均基于 a5266b4 独立测试。敌人剩余 HP 改善不等于实际战损改善。

可见实际日志已保存 `rank2-visible-core-analysis/godot.log`。完整 Mod 栈首次请求 `rank2-visible-analysis/`（2b32cfa1001642ab8da8f94079b2a557）在反序列化阶段因 SavedProperty net ID60/可用49 失败，属于环境恢复失败，不是策略结果。随后仅 CombatSolver/RitsuLib 环境已取得上述部署证据。临时安装 DLL 和账号 settings.save 均已通过 finally 恢复，游戏进程已退出；备份分别为证据根 `rank2-visible-installed-backup.dll`、`rank2-visible-settings-backup.save`。不用再次修改用户 Mod 设置。

### 人工与求解器的关键差异（已有日志证据，因果尚未做隔离实验）

| 时点 | 人工最终保留路线 | 当前求解器实际路线 |
|---|---|---|
| T1 | UNMOVABLE、两张 DEFEND+、PROLONG+；掉血 2 | 同样启动 UNMOVABLE/防御/PROLONG，另打 BREAK；掉血 1 |
| T2 | FINESSE+、TAUNT+，使用 STABLE_SERUM 后结束，保留 BODY_SLAM+、ONE_TWO_PUNCH 等攻击 | FINESSE、ONE_TWO_PUNCH、BODY_SLAM、STRIKE；当回合不用药 |
| T3 | 进入不攻击且无格挡的敌方阶段，手牌 10；RAGE、BLOODLETTING、BURNING_PACT、HAVOC、SHRUG_IT_OFF、FEED、BODY_SLAM、PROLONG；自损 3 | RAGE、FEED、SHRUG_IT_OFF、BURNING_PACT、HAVOC、PROLONG，最后才用 STABLE_SERUM |
| T4 | 起手格挡 39，敌 481；RAGE、BURNING_PACT、FINESSE、TAUNT、BREAK、ONE_TWO_PUNCH，再 BLOODLETTING、两张 DEFEND，最后 BODY_SLAM；自损 3，敌降到 281 | DEFEND、STRIKE、STRIKE，没有人工这一轮 200 点爆发 |

原始人工日志行段：T1 964–985；T2 1506–1525；T3 起始1543–1549，最终路线1741–1789（1756 有撤销）；T4 起始1811–1817，动作1966–2018（1997 有撤销）；T5 起始2036–2042；T8 起始2408–2414。
求解器日志行段：T1 604–638，T2 736–763，T3 860–908，T4 1004–1026。

候选质变策略是：**提前一回合用保留手牌药水，并主动结束回合保住攻击牌，把手牌、额外能量、抽牌与格挡转伤害集中到合适的敌方阶段；通过 PROLONG 跨回合保留格挡，BODY_SLAM 延后到格挡叠好之后。** 原版 StableSerum 源码已确认施加 RetainHandPower2（保留手牌两回合）。求解器已经会打 UNMOVABLE 和 RAGE，不能把差距笼统归结为“不会开能力”。

下一窗口先核对上述撤销后的动作与每回合 HP/格挡/敌 HP，形成简洁对比报告并把候选质变策略告诉用户；不要在用户给建议前写评分或保路改动。现有证据足够时直接分析；确需补执行证据才用 headless，避免同输入重复跑完整基线。

### 人工战损 55 的口径限制

`replan-audit.txt` 为 manual_plus_solver，18 次搜索、6 次人工偏离、11 次显式请求。T8 HP68（初始123），实际累计掉血55，敌12且格挡30；导出的剩余路线 FINESSE+、TAUNT+、BODY_SLAM+、STRIKE 预测不再掉血。**没有人工完整完战录制**，只能写“人工已发生55，剩余预测0”，不能写本轮验证人工整场55。历史50 HP部署位于 `.local/strategy-batch/results/20260905-rank2-reported-policy-accepted/`，runId beb01c29aa57492db5c55ce103a593c2，属于旧版本证据。

## 前 10 包状态与监控表

监控表：`docs/strategy/player-worldlines-20260905.md`，只维护表格。“优化后相对人工”填人工报告值减已验证求解器实际掉血，并标“报告参考”；严格更优才填是。缺人工完整录制时不要宣称严格整场优于人工。死亡、无法恢复、人工未完战等明确填不可比较/未验收，不能伪造数字。

| 包 | 本轮结果 | 相对人工报告参考 | 证据根下目录 |
|---|---|---|---|
| 1 | 实际1 HP/4药/T6，余122，计划外重算0；VeryHigh原验收政策 | +5，是（参考） | rank1-deploy |
| 2 | High仍死亡；两轮失败实验已撤回；当前等用户指定策略 | 死亡，否 | 见上文 |
| 3 | High实际6 HP/0药/T6，目标32，计划外重算0 | -3，否（参考） | rank3-high-deploy |
| 4 | 用户明确跳过，未整场验收 | 未验收 | 沿用监控记录 |
| 5 | 新预检有效，严格恢复因缺本回合卡牌历史失败；未搜索 | 不可比较 | rank5-restore |
| 6 | 原Custom/DOP8/短5秒深20秒，实际19 HP/1药/T10，目标22，计划外重算0；不是High重跑 | -19，否（参考） | rank6-deploy |
| 7 | High实际掉血70/回血21/余21，1药/T10，目标27未达；R1未改善，已撤回，按新指令暂停 | -65，否（参考） | rank7-high-opening-deploy |
| 8 | 人工20 HP对应敌还剩420，未完战 | 不可比较 | 原包working/aeonglass053711 |
| 9 | High实际31 HP/1药/T6，目标52，计划外重算0 | -1，否（参考） | rank9-high-deploy |
| 10 | 本轮只做rank10-preflight，材料有效；政策已准备，未恢复/搜索/部署 | 未验收 | rank10-preflight |

包7的17 HP来自开战选牌后额外Manual重算，已撤销其开战基线资格；修正后原开战路线70 HP。R1“手中Footwork防御收益”结果70 HP/T8，未改善战损，缓存false，runId 6c82c594531c4722831f31a4d3f00053，目录 rank7-r1-ready-defense-search，补丁 rank7-r1-ready-defense.patch。R2尚未进行，不能称已优化两三轮。

包10 ZIP在证据根 `CombatSolver-KNIGHTS_ELITE-20260904-034250-040.zip`；`rank10-high-policy.json`仅准备，未执行。原报告9含此前8 HP，比较范围需核对。

## 验证与后续执行约束

- 默认走 `tools/run-checkpoint-batch.ps1`，先读 `docs/CHECKPOINT_REPLAY.md` 与参数。High搜索180秒，整个请求显式 `-TimeoutSeconds 240`，用于覆盖启动/部署开销。优先复用已存 `rank2-high-policy.json`。
- 开战使用 `CheckpointSelector=start`，Preflight不等于恢复成功，SearchOnly不等于实际完战；DeploySolver固定Instant/0秒，核对计划外重算及缓存来源。
- 两个恢复fixture均已通过：unmovable-relic-block-history-0300（ee28e74dab6c4208b627462a5513eaba），smart-potion-qualified-layer-continuation-0300（ac3165ba938642f89e18ca4659928fec）。相关Release构建、结构门禁、CheckpointTool自测29断言已有通过证据。
- 最后实验撤回后的Release构建成功。本文交接只做文档/差异检查，不重新跑战斗。
- 可见测试脚本两端现支持政策覆盖路径与显式超时；PowerShell已用于上述实际请求，Linux脚本未做游戏运行验证。后续包2分析优先headless。
- 每批维护监控表、开发/测试证据，显式暂存本任务文件并提交、推送当前分支；不纳入问题包/日志/输出。
- 可复制的新窗口指令见 [worldline-resume-prompt-20260906.md](worldline-resume-prompt-20260906.md)。旧本地prompt路径已同步为相同最新指令。
