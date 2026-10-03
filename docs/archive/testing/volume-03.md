# CombatSolver 测试入口历史卷 03

## 循环预算审计跟进（2026-09-21）

- [F1/F2/F3 修正及证据](../performance/loop-boundaries-20260921.md)：8 项 Python 分类合同；正常预算 cap＋finisher 的 A/B Equivalent；主动 2000 ms 时间切层两侧各 time=3/node=0，原始未击杀观察保留，工具正确返回 Inconclusive／2。无时间边界的差异仍退出 1，不放宽为自动通过。
- 新 `generic-loop-replay-estimate-margin.json` 离线单 solver：6835 HP，6 展开／4107 转移、4095 额外回放、零损 T1；估算 4101 并不证明超过 4096 无法完成。非原生验证，不加入原 19 根 A/B 等价集。
- Release 0 警告／0 错误；遥测拆分不改变搜索判断或战斗语义，未重跑之前通过的四项原生部署。请求级累计额度仍未验证，不将 Evaluate 的 4096 检查外推到 Coordinator。

```bash
python3 -m unittest discover -s tools/search/OfflineSearchHarness -p test_loop_boundaries.py -v
```

## 追加循环边界与审计（2026-09-21）

- [19 根边界集及审计复核](../performance/loop-boundaries-20260921.md)：同根串行 A/B；质量指标均相同，18 根完整路线相同，1 根同质量异路线。包含隐藏相位、Buffer、格挡、4096 耗尽、替代出牌、付费抽牌、低血卖血、三目标、选牌、星能和 BansheesCry。离线显式检查见 `coverage/fixtures/search/loops/loop-boundaries-20260921/suite.json`。
- 耗尽反例 ABBA：展开/转移 4675/9375 → 4675/13471，路线及 4 HP/T2 相同；求解 +11.3%、累计分配 +14.9%、采样活对象峰值 -18.6%。不是无退化验收。
- 下列前三项严格增量及完整原生部署 Passed、0 计划外重算；最后一项在首次结果断言后停止。runId 与实例清理记录见报告。不是历史 EQ10/FULL40；P4 投影低估反例尚未验证。

```bash
./tools/testing/run-unattended-test.sh --scenario-id LOOP-BOUNDARY-LETTER-BRANCH-FINESSE --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --seed LOOPLETTEROPENERPHASE0111 --enemy-current-hp 61 --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 0 --clear-player-piles --clear-all-powers --cards-json '[{"cardId": "IMPATIENCE", "pile": "Hand"}, {"cardId": "FINESSE", "pile": "Hand"}, {"cardId": "IMPATIENCE", "pile": "Discard"}]' --relics-json '[{"relicId": "LETTER_OPENER"}]' --force-short-search-only --short-search-budget-override-milliseconds 10000 --search-max-degree-of-parallelism-for-test 1 --measure-search-phases --timeout-seconds 120 --performance-preset-for-test Low --initial-enemy-max-hps-json '[61]' --initial-enemy-current-hps-json '[61]' --expected-initial-projected-battle-hp-lost 0 --expected-initial-combat-ended-turn 1 --expected-initial-final-enemy-hp-at-most 0 --verify-incremental-search --expected-unexpected-replans-at-most 0 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id LOOP-BOUNDARY-MULTI-TARGET --character-id IRONCLAD --encounter-id CORPSE_SLUGS_NORMAL --seed LOOPLETTEROPENERPHASE0111 --enemy-current-hp 9 --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 0 --clear-player-piles --clear-all-powers --cards-json '[{"cardId": "FLASH_OF_STEEL", "pile": "Hand"}, {"cardId": "FINESSE", "pile": "Discard"}]' --relics-json '[]' --force-short-search-only --short-search-budget-override-milliseconds 10000 --search-max-degree-of-parallelism-for-test 1 --measure-search-phases --timeout-seconds 120 --performance-preset-for-test Low --expected-initial-projected-battle-hp-lost 0 --expected-initial-combat-ended-turn 1 --expected-initial-final-enemy-hp-at-most 0 --verify-incremental-search --expected-unexpected-replans-at-most 0 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id LOOP-BOUNDARY-BLOCK-BODY-SLAM --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --seed LOOPLETTEROPENERPHASE0111 --enemy-current-hp 37 --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 0 --clear-player-piles --clear-all-powers --cards-json '[{"cardId": "FINESSE", "pile": "Hand"}, {"cardId": "FINESSE", "pile": "Discard"}, {"cardId": "BODY_SLAM", "pile": "Discard", "upgradeLevels": 1}]' --relics-json '[]' --force-short-search-only --short-search-budget-override-milliseconds 10000 --search-max-degree-of-parallelism-for-test 1 --measure-search-phases --timeout-seconds 120 --performance-preset-for-test Low --expected-initial-projected-battle-hp-lost 0 --expected-initial-combat-ended-turn 2 --expected-initial-final-enemy-hp-at-most 0 --verify-incremental-search --expected-unexpected-replans-at-most 0 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id LOOP-BOUNDARY-BLOOD-LOW-HP --character-id SILENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --seed LOOPBLOODPOMMELQUALITY0111 --enemy-current-hp 40 --initial-enemy-move-ids-json '["INHALE"]' --initial-player-hp 4 --initial-player-max-hp 57 --initial-player-energy 0 --clear-run-deck --clear-player-piles --clear-all-powers --cards-json '[{"cardId": "BLOODLETTING", "pile": "Hand", "treatAsDeckCard": true}, {"cardId": "POMMEL_STRIKE", "pile": "Hand", "count": 2, "upgradeLevels": 1, "treatAsDeckCard": true}]' --force-short-search-only --short-search-budget-override-milliseconds 10000 --search-max-degree-of-parallelism-for-test 1 --measure-search-phases --stop-after-initial-solver-result-assertion --timeout-seconds 120 --performance-preset-for-test Low --expected-initial-projected-battle-hp-lost 3 --expected-initial-combat-ended-turn 2 --expected-initial-final-enemy-hp-at-most 0 --cleanup-instance-on-exit
```

## 下一版本（开发中）：循环验证（2026-09-21）

- [循环报告及逐次数据](../performance/loop-optimization-20260921.md)：2000 HP、1200 动作 A/B 路线逐字段相同，零损 T1；最终 DOP1/DOP2 6 展开/1206 转移一致，最大父节点并发 2。分支根关闭早停时 101/256 一致，动作并发 2；仅调度计数、lane 局部缓存条目数不同。
- `LOOP-DEFENSIVE-VALUE` / `50cfafd5acc642508e18840ab459f69a` Passed：100→200 格挡饱和、Barricade 原生保留、固定保留上限、BansheesCry 历史读者门控。Release 0 警告/0 错误，Bash/PowerShell 两端结构门禁均通过（search_files=206）。
- `LoopDisplayChecks`：122505 断言通过。`UI-LOCALIZATION` / `275d2dcafd214c62b675f5832a11993d` Passed；`ROUTE-ROW-REUSE` / `5ec0d3f0b90c450b8c43a87b0cdaee25` Passed。41 动作折叠、额外重放与末击后缀、循环高亮、失败重试、完整显示身份复用、中英简繁及订阅释放通过；未做可见排版验收。
- `LOOP-REPLAY-DEPLOY` / `d4867a3cb9154340a85fe848d1139a56` Passed：40 HP、24 动作、零战损 T1、严格增量/全前缀核对、原生部署、0 非预期重算。严格回放的时间不用于性能表。
- 新 `generic-loop-bloodletting-no-postcombat-heal-quality.json` 排除燃烧之血战后回血干扰，A/B 675/1820、3 HP/T2，完整路线相同；原生 `41f101db99fa4f8fb9a55bd6494cc456` 同断言 Passed。旧铁甲战士夹具仍会给 6 HP/T1（战后补满），不能将旧 3 HP 断言误报为通过。
- 50 万 HP 成长循环按 Low 60000 节点、RequireAtLeastOne 对照：两侧 10555/22248、零损 T1、完整路线相同。6000 节点的早期内环两侧都失败，只说明该预算不足。


## 0.43.3：余像路线与战后掉药预测

- 药水奖励机会成本：更新纯合同，概率 100%/40% 但结果未知的满栏情形额度均为 0；确定掉药按预测药水档位抵扣一次，确定不掉、药栏未满和禁用获得药水时额度为 0。UI 本地化合同新增搜索刚开始即显示预测掉药/不掉药，关闭预测时清空；英/简/繁分别核对。本地 Release 构建 0 警告、0 错误，自动部署的 DLL 与构建 DLL SHA256 一致。没有运行无人游戏合同或可见实机。结构门禁本次失败：`CombatBeamSolver.BlockPotionInsertion.cs` 缺少脚本硬编码的 `ReplayInsertedRoute(`，但当前 HEAD 的该文件原本就命名为 `ReplayAdjustedRoute(`，本次未改动该文件；不把门禁当作通过。
- 依据玩家本机 `combat-a6529611f1c3484bafe6d8a7d4f07f1d.jsonl` 的第 2 回合计划，原路线先撕咬／暴政、后打两张余像，原计划的逐动作格挡与原生 Hook 口径对得上；前置路径尚无同根实机执行证据。用户明确要求停止测试，已终止隔离场景并清理实例；本次只执行 Release 编译（0 警告、0 错误），没有将编译当作路线改善验证。
- `CombatSolver.GcPolicyChecks`：Linux / Windows 各 54 项通过（26 + 8 + 9 + 2 + 1 + 8）。新增 `diagnostic-failure` 用工具侧日志替身验证八条故障路径、异常身份、排队手动任务和后续搜索准入；上游检查点失败与中间版本漏结清手动任务均有 12 秒超时基线。
- Release 构建及 Bash / PowerShell 结构门禁通过。当前上游 `3f4002bd` 的组合候选 ABBA：三场 12 份有效搜索，6 对非时序指标和完整记录路线一致；因耗时/峰值代价撤掉根缓存。另保留一份系统压力导致 No-GC 未建立的失败基线，不计入性能均值。
- Skittish 单独筛选两场 8 份搜索、4 对对账一致，2 项原生差分通过；收益/代价仍不足，一并撤掉。最终纯修复版花园冒烟 Passed（`bed2628af2e94abd843a0c45103bbc05`），与上游非时序指标/路线对账无差异，最大 GC 暂停仍为 1645.559 ms。验证与清理证据见 [本轮报告](../performance/gc-completion-allocation-20260921.md) 和 [结构化数据](../performance/gc-completion-allocation-20260921.json)。均为无头或 CLR 证据，未验证可见 Steam 帧时间或整场自动部署。
- #122 合入 0.43.3 时处理两项审查意见：多次失败合并时展平既有 `AggregateException`，未取得回收后堆快照时三个指标用 `-1` 明确表示未知，不再伪装为真实 0。合并组合在 Windows 上运行 `diagnostic-failure` 8 项通过，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=206`，Release 构建 0 警告、0 错误并自动部署本地 Mod；没有重跑作者已完成的 20 份性能对照、Linux 合同或可见 Steam。

## 0.43.2：混合用药、生成牌、路线缓存与增量历史计数

- 强制／智能混合用药：`SEARCH-HP-TARGET-STOP` / `312cb8cd77fb470eaac9bbc48cd19506` Passed，强制能量药与智能力量药的真实搜索在零战损胜利时仅用一瓶，DOP1/DOP2 完整结果和非时序指标逐字段一致；改为只持防御牌与 15 HP 敌人时，仅强制药无法获胜，智能火焰药作为第二瓶救命且不被误拦。纯合同核对强制基线只允许指定槽位、额外一瓶仅比强制基线多省 1 HP 时不满足 9 HP 门槛、强制药本身不计入额外药机会成本及梯度瓶数。隔离实例已清理。中间正向场景曾 Failed：初始接线把只允许强制药的临时策略传给后续 Smart 审计，使 `maximum=0`；改由审计读取原始逐瓶策略后通过。结构门禁 `REFACTOR_BOUNDARIES_OK search_files=205`，Windows Release 0 警告／错误。短根验证了混合策略、早停和救命路径；未取得玩家原战斗同根对照，也未实测非零但不足门槛的实际两药胜利比较。启动器曾报告一次 `Import-Clixml` 解析警告，随后游戏请求 Passed、目标断言完成；未把警告当成产品行为结论。
- #105 原提交合入后的集成修正：`AdaptedOnPlayChecks` 40 项和空登记 2 项通过，涵盖已登记生成牌根前预审、未登记生成牌由根冻结的补丁集合拒绝、根捕获后安装／卸载补丁不改变旧根及新根恢复普通镜像。`ADAPTED-ONPLAY-INTEGRATION-CARD` / `5ae3ade71d1e4e9c9eb0d9aaff6ce209` Passed，真实游戏的替换只执行一次、完整快照／增量回放／Fork／第 1 至 2 回合对账及晚装补丁拒绝通过；最终构建的 `ADAPTED-ONPLAY-INTEGRATION-REUSE` / `1b16e4e994f74934ba79ecf044a324f9` Passed，精确续用到第 2 回合、计划外重算 0。两场隔离实例均已清理。Windows Release 0 警告／错误，`REFACTOR_BOUNDARIES_OK search_files=205`。首次把请求 JSON 误传给 `-GeneratedScenarioPath`，启动阶段报未知 `scenarioId`，属于命令输入错误，不计为产品断言；改为显式测试选项后上述场景通过。未跑可见 Steam、任意第三方 Mod 或执行中并发热换补丁；生成牌的根冻结边界由独立合同覆盖，而非真实游戏生成牌场景。
- #117/#118/#119 均以原 PR 提交 merge 到已发布的 0.43.1 基线上，仅手工并列解决版本文档与双平台结构门禁冲突。合并组合 Windows Release 构建 0 警告/错误，`REFACTOR_BOUNDARIES_OK search_files=205`。#117 的辅助失败边界收窄后，Windows 离线单根 `OFFLINE_HARNESS_ANCILLARY_CHECKS=1` / `ancillary-integration` Passed：原 17 项磁盘/取消合同与新增程序错误传播断言合计 18 项；固定 150 节点、DOP 1、实际展开 119、转移 405。作者的 60 根逐字段对照、#118 的逐事件验证构建、#119 的百万项前沿检查及三根 VeryHigh 观察均是各 PR 的原有证据，本次未重跑，不等同于 0.43.1 三 PR 合并组合的整场等价性或可见实机验收。
- 离线宿主 `OFFLINE_HARNESS_ANCILLARY_CHECKS=1`（DEFECT、`FUZZY_WURM_CRAWLER_WEAK`、High、DOP 1）在一次真实求解结果上注入磁盘故障，17 项通过：正常写读往返、临时文件清理、坏 JSON 与空路线按未命中处理并改名 `.bad`、隔离后同一键可重新写入、独占文件锁按未命中处理且不隔离、解锁后可读、缓存目录被文件占位、Unix 只读目录、目标路径被目录占用、结果序列化字节不变、取消异常传播、普通失败只记一次日志。作者在 macOS 本机运行；PR 阶段未验证 Windows 文件锁，本轮短根已覆盖 Windows 独占锁分支。
- 录像采集与打包的隔离、打包调用顺序调整只经过编译和结构门禁，未在游戏内验证；符合录像条件的进阶 10 第三幕 Boss 无伤路线本机没有复现条件。
- `OFFLINE_HARNESS_HISTORY_CHECKS=1`，DEFECT、`--milestone M1`：21 项通过。检查原始/完成事件、嵌套自动出牌、两种暂停续接、普通 Fork、父/根隔离和键位一致性。
- `-p:VerifyHistoryCounters=true` 逐事件及 Fork/构键读取核对独立全扫描。语料、构键计时与验证范围见 [专题](../strategy/incremental-history-counters.md)。
- EQ 10 / FULL 40 / GA 10 对照 `8be1410`：60 根有效，5,670 个确定性字段及补充预算/剪枝字段一致。普通构建另测两个根的选中通道构键阶段，数据见专题。变基到 0.43.0（`cccc270`）后重跑 EQ 10 根，`compare_results.py` 992 字段 `IDENTICAL`。
- 前沿与观测检查 1,024,010 项通过，新增标签数、首次触顶、峰值跨重建保留和分布检查；原支配决策逐项对照独立 List 基准。
- EQ 10、FULL 40、GA 10 对照 `8be1410`：60 根有效，5,670 个确定性字段、额外预算／剪枝字段及完整结果快照一致。变基到 0.43.0（`cccc270`）后重跑 EQ 10 根，`compare_results.py` 992 字段 `IDENTICAL`。
- VeryHigh 生产预算观察：三个重型根均未触顶，最大占用 832,793 / 1,000,000（83.28%）。没有触顶根，未运行放大上限臂；本轮不提供默认触顶后的质量结论。数据见 [专题](../performance/transposition-cap-evidence-20260920.md)。未实机验证。

## 0.43.1：英文界面启动与疯狂科学成长策略

- `GROWTH-POLICY-FREE-FIRST` / `fb11ce8e1d95410b80d58555a26f6b07` Passed（22.80 秒）。在 Defect 独立原生战斗中注入疯狂科学能力／改进变体，冻结可升级正式牌组三张的目标，预测出牌产生一层改进及一次独立成长额度，随后真实出牌并逐字段对照；另核对非改进／非能力变体不计成长、两张牌与一张可升级目标时目标封顶、零容量不产生目标、额度设置往返、侧栏独立行、Fork 隔离及重复记录封顶。修复前 `630075e609644c418a9c5eece3b23330` 在变体识别断言按预期 Failed；中间 `45bee93749284fff8eba0dc8839755c7` 为夹具错误地重复转可变卡，`4c962a31b75f4a53a278e4b2c4373474` 与 `d3a9d2720aba442e8e3ae03eff03538d` 是反射回放参数及未重编 DLL 的夹具失败，均非产品断言失败。所有隔离实例已删除。未覆盖事件实际生成选项页及正式战后随机升级的可见动画。
- `UI-LOCALIZATION` / `db0cc94e1f604a21843514d4c5bd1610` Passed（25.76 秒），eng/zhs/zht 子面板构造和动态标题未因新增成长行失败；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=204`。均仅为无头／静态证据，可见排版尚未验收。
- `SEARCH-HP-TARGET-STOP` / `e60944f238684bc1a41135317e7d10d6` Passed（23.55 秒）：零损/阈值、成长达标、可重复致命来源及相关回收与动态重放回归，隔离实例已删除。
- `GROWTH-ANCIENT-POLICY` / `16e87117632749b783df9b46909f8581` Passed（26.93 秒）：原有成长牌手动历史、跨回合/Fork、至亮之焰硬上限及禁忌魔典额度合同继续成立，隔离实例已删除。
- `UI-LOCALIZATION` 增加 eng/zhs/zht 药水、成长、遗物子面板的实际构造与“收起”按钮文案合同。0.43.0 源码增加断言后，在英文药水面板构造处按玩家异常栈 Failed（runId `391a52cdd52942d9a45e8b514f9034b9`，`KeyNotFoundException: 收起`）；补齐英文词典后 Passed（runId `333583c32c81407bbcd7b3171e872198`），三种语言的三个子面板均完成检查。两次都使用 120 秒上限、独立无头实例及 `-CleanupInstanceOnExit`，实例已删除。该合同覆盖建窗对象与本地化，不等于可见 Steam 排版验收。
## 0.43.0：路线连续性与操作体验（2026-09-19）

- 两回合原生场景 `TOASTY-QOL-MANUAL-SAME` Passed（runId `c82b3f8125cc4b8998047ccabaf3ca7a`）：第 2 回合烘焙手套手牌页按计划手动删牌，精确续用，新增搜索 0、计划外重算 0。`TOASTY-QOL-MANUAL-DIFFERENT` Passed（runId `f42cc57ca55e4c8e8a91a64a42f951a8`）：选另一张牌严格失配并重新计算。固定夹具位于 `coverage/fixtures/ui/toasty-qol-*.json`，可用 `pwsh -NoProfile -File tools/run-qol-contracts.ps1 -Case manual-same`（或 `manual-different`）重跑。最初误将生成场景输出路径用作输入的启动失败不计入上述通过结果，隔离实例已清理。
- `TOASTY-QOL-FROZEN-SAME` Passed（runId `4726fb1c0b1444c58e15a912f604be06`）：第 2 回合原生手牌页冻结后，玩家按计划手动选牌，精确续用且无新搜索。`TOASTY-QOL-FROZEN-DIFFERENT` Passed（runId `9a9e04b913f64057806f8e849255d223`）：异选后旧路线仅供参考，直接请求执行也不搜索、不执行；手动重新计算解除冻结并从真实状态得到可执行路线。
- `TOASTY-QOL-AUTO-OFF-FULLAUTO` Passed（runId `f19b059e55d5488e9971fa20b3e5a042`）：第 2 回合原生选牌页关闭自动计算后明确开启全自动，计划选择后无搜索续用并开始出牌，计划外重算 0；全自动保持开、自动计算保持关。上述五场均为隔离无头实例，退出后实例删除；未测试可见 Steam。
- 上一版 `UI-COMPACT-QOL` 曾 Passed（runId `44dd21643d604c6a95c1a0527aa816fe`），但把战损拆成累计受伤、回血和净变化后，界面实际过于冗长。恢复旧摘要前先增加“无回血时仍紧凑”的断言；旧实现按预期失败（runId `6bd1cb09ba2d4785a4c869656de0da17`）。上一轮修正后 `UI-COMPACT-QOL` Passed（runId `a9fafeccff0e45c4956d21c61b93f6ee`），但后来玩家可见实机显示首回合“路线回血 14”，续用到下一回合显示“路线回血 9”；旧实现把实际回血从后续预测中删去、并从“已扣／预计扣”扣除。新增“回血不改累计受伤”断言后原实现按预期 Failed（runId `537783e8716a43f784fa2d8257af71c1`），隔离实例已删除。修正后 `UI-COMPACT-QOL` Passed（runId `ebd827a759f84a0fab2d74aeef662b0c`）：核对本场已受伤加未来逐回合受伤、本场已恢复加未来逐回合恢复；精确续用的纯显示合同中，已发生的 5 HP 回血与余下 9 HP 合计仍是 14，累计扣血不因单纯回血跳变；折叠及无回血时的标签规则也通过。此合同没有真实打完两回合再生药战斗，可见会话新画面仍待玩家验收。模拟不支持 NoGC 的入口仍核对未调用区域启动且未取得/恢复延迟模式所有权；该模拟不等于 Android/iOS 真机验证。`UI-PRIORITY-FEEDBACK` Passed（runId `508238b1cbd74c21bbb41f7459118d52`）：展开和小窗“采用／执行／冻结”各自的可见与禁用状态合同通过。
- `UI-LOCALIZATION` 修正后 Passed（runId `af8f282e828c4bc486105ff617975849`）：中英简繁 426 项目录占位符与原有路线标签一致；两项隔离无头实例都已删除。可见界面的遮挡和点击体验未验收。
- 花园幽灵鳗问题定位：本机 CombatSolver 独立日志 `combat-4b7cdbd07e434de0b9f448456daca5be.jsonl` 中首次 `projected_battle_hp_lost=12`，随后第 2–5 回合 `SEARCH_REUSED validation=exact_state_text`，逐回合受伤 7/0/2/0/3；第 3 回合先 `TURN_SETUP_RESULT_PREVIEW` 后 `SEARCH_REUSED`，旧选牌预览源已受伤 0 + 后续 5 与实况 7 + 后续 5 不同。`UI-COMPACT-QOL` 补充当前选牌预览需保留已观察 7 HP 的断言后，旧代码按预期 Failed（runId `d9ccbebf72b343479b6b62cc7ff8448e`，实例已删除）。修正后 `UI-COMPACT-QOL` Passed（runId `0f41076362304db9886cccbc0ab36432`），`TOASTY-QOL-MANUAL-SAME` 的真实第 2 回合原生选牌与精确续用 Passed（runId `28d2a9acedfb48a6ac4387c6745d80da`），其中定向注入“已受伤 7、已回血 2”的预览快照断言通过，新增搜索 0；均用 `-CleanupInstanceOnExit` 删除隔离实例。改动只涉及 UI 主线程快照，原日志不含界面文字或遗物逐效果结算；花园幽灵鳗可见会话新画面仍待玩家验收。
- 单步执行后原生选牌页按钮回归：本机日志 `combat-5fe601b09c3d4aaab663622187640132.jsonl` 中第 3 回合 `TURN_SETUP_RESULT_PREVIEW` 先于 `NATIVE_CHOICE_VISIBLE` / `TURN_SETUP_PLAN_READY driving=false`，期间没有 `UI_ACTION action=deploy`。新增固定 `coverage/fixtures/ui/toasty-qol-single-step-execute.json`，命令 `pwsh -NoProfile -File tools/run-qol-contracts.ps1 -Case single-step-execute`：等待选牌表面准备完毕后，旧版按钮仍禁用，Failed（runId `d69e63aedbde424191ccc2c3d74102f9`）；在准备完成时刷新控件后，触发按钮本身，原生选牌先完成且下一回合开始部署，精确续用、搜索次数不增加、计划外重算 0、全自动保持关闭，最终 Passed（runId `0ae649cacf8d4dc6876f19113764db9e`），实例已删除。首次红灯 `8a42cd9b0bdd47ab961f3040f5a9725c` 发生于页面可见而计划尚未准备好的更早时点，不计为最终失败基线；未追加零受伤场景以外的整场质量结论。可见 Steam 点击与该花园幽灵鳗原场景未复跑。
- 实时路线的回合开始选牌：`UI-LOCALIZATION` 扩展合同用候选根遗物选牌、下一回合能力选牌、再下一回合遗物选牌核对中英简繁显示；同时核对前沿预览和候选切换后移除旧选择。修复前 Failed（runId `1e1930dbf02b4625b2e50d8e59390ada`，英文的能力/遗物选择均为空），修复后 Passed（runId `4410cb175cef4f569f0efec3b561b4d0`），隔离实例已删除。该合成投影合同不等于可见实机中整场搜索帧时序验收。
- 路线有效性定时刷新及小窗独立禁用状态接线后，`UI-PRIORITY-FEEDBACK` 再次 Passed（runId `508238b1cbd74c21bbb41f7459118d52`）；`TOASTY-QOL-FROZEN-DIFFERENT` 再次 Passed（runId `b1a30ec68a76481ab16058b2fec312d2`），过期路线没有出牌或计划外搜索，手动重算仍恢复可执行路线。
- 本机目前没有连接的 Android 设备，也没有本次玩家异常栈；手机端实机未验证。可见界面的实际遮挡、长译文排版与点击体验留作人工验收。未验证的场景不计为通过。

## 0.42.0：发布构建

- 合入 #109、#111、#112、#113、#114 与 #115 后，相关语义、界面、搜索和内存定向验证见下方各节；版本提升只改 manifest、项目版本与发布文案，不重复行为场景。已知未验证范围包括默认转置表触顶后的广泛整场质量，以及可见 Steam 会话性能与排版。

## 未发布：PR #114 原始分支与 #115 集成

- 原分支的 `PortfolioSelectorChecks` 12 组 410 断言、`BeamOrderingKeyChecks` 8 组 549747 断言、`BeamWidthPortfolioChecks` 93 项，以及 No-GC 和内存截断、转置表上下限的原始对照均属 PR #114 基线证据，见各研究报告；不能当作现行主线组合已通过。当前整合后的验证和未通过项在本节续记。
- 默认值核对：转置支配表合计上限 1,000,000 为唯一新增的默认搜索决策；无进展截断=0、基线组合成员=true、状态键盐/牌堆顺序商/转置消融=0；无有效环境模型时学习型门控不启用。低于上限的原分支对照不能证明触顶后的路线质量，完整关闭剪枝的消融也不是质量代价上界。
- 本次原分支并入现行 main 后，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=204`；主 DLL Release 编译 0 警告，完整工程只因本机缺 .NET Framework 4.8 引用程序集停在 MemoryCleaner；离线宿主 Release 0 警告/错误。`GcPolicyChecks` 基础 26 项和 `recovery` 9 项、`PortfolioSelectorChecks` 12 组 410 断言、`BeamOrderingKeyChecks` 8 组 549747 断言、`BeamWidthPortfolioChecks` 93 项通过。
- 当前合并组合的固定故障机器人/FUZZY_WURM 根（Custom、beam 30、nodes 2000、DOP1、Coordinator+组合）对已合入 #115 的同根产物比较 72 字段 `IDENTICAL`，包含路线和续用文本；默认 100 万条对同一代码的无限制版也是 72 字段 `IDENTICAL`、展开 236、转移 832。测试上限设为 50 时两表恰好 34+16 条、`transpositionLimitBypasses=957`；这一个根的 72 个决策字段仍相同，不代表其他根的触顶质量。原 #114 新宿主不能直接加载旧 #115 DLL（实验策略接口不同），跨版本对照沿用此前 #115 宿主产物，不把失败的直接加载记为通过。
- 内存截断受控样本（铁甲战士/FUZZY_WURM、1 GB No-GC、600 MiB 活压力、阈值 1）得到 `MemoryNoProgress`、展开 1、可执行的防御牌 + EndTurn 两步路线、组合成员 `MemoryTruncated`；这是注入压力，不代表真实长搜的质量。DOP2 组合测量完整结束，实际最大并发 2，`phasePerformance` 写入宿主结果；首次整合时该字段为空，已修复并复测。尚无默认 100 万条触顶后的广泛整场质量对照，也未跑可见 Steam/正常会话性能。

## 未发布：选中路线续用戳与诊断指标收口（从 PR #114 提取）

- 本轮只提取最终选中路径的续用戳构造和显式度量失败路径；不引入内存无进展截断、转置表默认上限或实验开关。Windows 隔离无头 `SINGLE-SEARCH-PROFILE -MeasureSearchPhases` Passed，覆盖四档预设、单一进度阶段与固定工作量；离线宿主当前 main 对提取组合的故障机器人/FUZZY_WURM 单根（DOP 1、beam 30、2000 节点）72 个字段 `IDENTICAL`，包含第 2/3 回合两份非空续用状态文本，双方展开 236、转移 832。重型 `SEARCH-POLICY-SNAPSHOT` 在 120 秒上限内未完成，未将失败 lane 排空的原生合同记作通过；原生实例已清理。

## 未发布：生成卡池复用（从 PR #114 提取）

- 原分支 Crossbow 站点的定向 A/B 与其它遗物站点回退依据见[遗物印牌站点复用](../performance/relic-generation-pool-reuse-20260919.md)。本轮 Windows 隔离无头 `TURN-START-GENERATION-CACHE` Passed（27 项对照：顺序、Fork 共享、可变约束回退、完整 RNG/历史、独立生成卡和父/实况不变）；`POTION-GENERATION-CACHE` Passed（8 项对照：卡牌状态、五字段 RNG、升级和父/实况不变）。这两项不代表当前 main 的受控提速或可见帧结论。

## 未发布：模型 ID 纯值缓存（从 PR #114 提取）

- 原分支的定向根及 A/B 范围见[ModelDb.GetId 记忆化](../performance/defect-modeldb-getid-cache-20260919.md)；本轮组合离线单根字段级对照见上方，不将旧分支的时间、分配数字外推到本次 main 或可见帧。

## 未发布：No-GC 区域准入下限（从 PR #114 提取）

- `tools/testing/checks/CombatSolver.GcPolicyChecks` 在本轮提取组合上全部 26 项通过，其中 `admission` 6 项覆盖 12 GiB 配置在系统余量下只得到 2.97 GiB 时拒绝、平台尺寸回退保持准入及边界值；原分支结果见[报告](../performance/no-gc-region-admission-20260917.md)。Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=203`，主 DLL Release 编译 0 警告；完整工程构建因本机缺少 .NET Framework 4.8 参考程序集停在 MemoryCleaner 辅助程序。未做可见 Steam、广泛战斗质量或受控墙钟对照。

## 未发布：状态键补整场历史计数（2026-09-19）

- 离线宿主对上游 0.41.0（High 90/50000、Coordinator、Smart、DOP 1、`fixedSearchBudget`，`compare_results.py` 排除耗时/内存字段）：EQ 10 根只有 `NECROBINDER-ELITE-00`（牌组含亡魂牵引）不一致，其余 9 根 983 字段一致；FULL 40 根只有 4 根不一致（`NECROBINDER-ELITE-00`、`SILENT-BOSS-01`、`SILENT-ELITE-03`、`SILENT-BOSS-03`），按生成场景 loadout 核对正是全部含金斧/亡魂牵引/谋杀的根，其余 36 根一致；GA 10 根（EQ 规格 + 无色牌固定含一张金斧）全部不一致。15 根受影响根：战损 2 根下降（48→29、9→8）、0 根上升、0 根胜负翻转，展开量比 0.997–1.000。
- 无条件追加的对照（未采用）：46/50 根路线变化、胜负 3 负 1 正，见[状态键历史计数报告](../strategy/state-key-history-counters-20260919.md)。
- PR 原分支 Release 编译 0 警告 0 错误（`CopyModOnBuild=false`）；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=193`。以上是合并前证据；本次合并后的验证另记于下方。
- 合并到含 #111/#112 的 main 后，`COMBAT-HISTORY-COUNTER-KEY` 在 Windows 仓库内隔离无人实例 Passed：根手牌含金斧与防御，两个同根分支只有子分支追加一次已完成出牌历史；金斧动态伤害相差 1，完整搜索状态键不同，Fork 后子键不变、父键不变。命令：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId COMBAT-HISTORY-COUNTER-KEY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 100 -ClearPlayerPiles -CardsJson '[{"CardId":"GOLD_AXE","Pile":"Hand"},{"CardId":"DEFEND_IRONCLAD","Pile":"Hand"}]' -CleanupInstanceOnExit -TimeoutSeconds 120`；结果 `UNATTENDED_INSTANCE_REMOVED`。夹具比较搜索 Snapshot 的真实 `StateKey`，不声称这份合成历史是原生完整出牌差分。
- 本轮 Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=203`。主 DLL Release 编译 0 警告；完整 `dotnet build` 因本机缺少 .NET Framework 4.8 参考程序集，停在 MemoryCleaner 辅助程序。未跑整场、可见 Steam 或合并组合的离线 EQ/FULL 对照；历史性能没有受控墙钟结论。

## 未发布：预测战后掉药与满栏用药门槛（2026-09-18）

- `POTION-REWARD-FORECAST` 新场景（`coverage/fixtures/potions/potion-reward-forecast.json`）：开战捕获根后，让原版 `RewardsSet` 在同一条奖励 RNG 上真实生成奖励并逐项比对掉落结论与药水 ID。macOS 隔离无头实例（`.local/headless-mac/run_mac_unattended.sh`，`--headless --force-steam=off`，HOME 隔离）8/8 Passed：SILENT / FUZZY_WURM_CRAWLER_WEAK / Monster 四个种子（Drop FRUIT_JUICE、NoDrop、Drop FLEX_POTION、NoDrop）、BYGONE_EFFIGY_ELITE / Elite 两个种子（Drop FRUIT_JUICE、NoDrop）、QUEEN_BOSS / Boss（Drop FRUIT_JUICE）、未满栏 1 瓶（NoDrop）。铁甲战士在全新 profile 下前几场是教程奖励集，镜像退回 `Unknown`，场景据此改用静默猎手。
- `PR15-POTION-VALUE-TIERS` Passed（同一无头实例）：新增概率镜像（精英 +0.125、夹在 [0,1]）、额度（Drop 按档位、NoDrop/NoRewards 为 0、Unknown 按概率 × 9、未满栏或 Sozu 为 0）、路线级扣减（只扣一次、下限 1 HP）与 1 HP 门槛挡住零收益用药的断言，以及根快照前景字段与实况一致。
- 离线宿主等价性：`EQ` 10 根（5 角色 × 精英/Boss，A10，1 瓶药未满栏，High 90/50000，Coordinator，Smart，DOP 1），上游 0.41.0 DLL 对本分支 DLL `compare_results.py` 983 字段 `IDENTICAL`。比较脚本本轮新增排除首条路线发布时间、峰值堆与组合成员内嵌的耗时/分配字段。
- 离线宿主满栏对照：`FULL` 40 根（同语料 × 4 种子，2 瓶药 = A10 满栏）：3 根变化，全部战损下降（27→22、52→32、18→14，合计 −29），用药 +4，完整胜利 25/25 不变，两版搜索工作量逐根相同。额度扣到 0 的第一版有 2 根坏变化（多掉 15 血；白用一瓶），改为下限 1 HP 后消失。详见[战后掉药预测报告](../strategy/potion-reward-outlook-20260918.md)。
- Release 编译 0 警告 0 错误；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=193`。未运行可见 Steam、未运行 Windows/Linux 无人入口。
- 合并接入（2026-09-19，Windows 仓库内隔离无头）：`PR15-POTION-VALUE-TIERS` 静默猎手默认关闭 1 场 Passed，断言设置默认/持久化、关闭时根无前景、零成本药水不被抬价；同场 `RequireAtLeastOne` 强制一瓶另跑 1 场 Passed，终局回放与摘要的用药身份/数量一致。`POTION-REWARD-FORECAST` 静默猎手、A10、满栏两瓶 1 场 Passed：原生奖励与预测同为 `FRUIT_JUICE`，开启时完整胜利摘要显示掉药；`UI-LOCALIZATION` 1 场 Passed，eng/zhs/zht 共 438 个模板并核对新设置控件。四场均报告 `UNATTENDED_INSTANCE_REMOVED`。首次使用全新铁甲战士档案跑 PR15 时旧断言把教程 `Unknown` 当失败；改用非教程角色后通过，本批未改教程规则。
- 主 DLL 使用 Windows 游戏依赖、跳过本机未安装的 .NET Framework 4.8 辅助程序目标完成 Release 编译，0 警告 0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=193`。完整 Windows `dotnet build` 因缺少 .NET Framework 4.8 引用程序集失败；无头入口使用同仓库现成的 MemoryCleaner 辅助程序副本。未做可见 Steam 人工排版验收，历史离线 FULL 对照是原始无开关的开启前景实验，不作为本次默认关闭质量结论。

## 未发布：技术债静态审计与分片整理

- 安全清理仅普通Release与门禁；克隆复用、保路纯移动、展开纯移动各一次EQ10，均IDENTICAL（每批983项字段、600项剪枝计数）。
- 最终仅一次EQ10+FULL40，IDENTICAL（4673项字段、3000项剪枝计数），双侧100份有效、无时间边界；复用指定0.41.0基线结果与比较器，未重跑基线。固定High 90/50000、Coordinator、Smart、DOP1、workers=2。
- 229/100项保路/展开成员文本分别由Roslyn核对；保路5项字段声明顺序不变。BeamRankSortChecks独立合同720组/167280条目通过。最终Release 0警告/0错误，CopyModOnBuild=false；Bash门禁search_files=201。
- 本轮没有原生游戏/无人场景验收；审计工具复跑说明在[CodeDebt](../../../tools/inspection/CodeDebt/README.md)，逐批产物位置见[技术债审计](../refactoring/tech-debt-audit-2026-09-18.md)。

## 未发布：代码整洁度清理

- 私有死代码与多余using清理：EQ 10根对上游0.41.0为 `IDENTICAL`（983项比较字段、600项剪枝计数）；循环出口共享排序后缀：EQ 10 + FULL 40根为 `IDENTICAL`（4673项比较字段、3000项剪枝计数），双侧100份结果有效且未触及时间边界。
- 两阶段Release构建均为0警告/0错误，均带 `CopyModOnBuild=false`；Bash结构门禁均为 `REFACTOR_BOUNDARIES_OK search_files=192`。固定High 90/50000、Coordinator、Smart、DOP1，离线宿主workers=2；未运行原生游戏场景。
- 比较器沿用已修正的递归遥测排除口径，保留路线、根状态、目录指纹、组合成员选择和确定性工作指标；不比较时间/内存。基线末根曾受一次误启动后取消的构建干扰，已排除并仅补跑该根。详细范围、原始失败记录、保留项及本地证据路径见[代码整洁度报告](../refactoring/code-hygiene-review-2026-09-18.md)。

## 0.41.0：问题包开战默认与仓库内无头实例（2026-09-18）

- `dotnet run --project tools/replay/CheckpointTool/CheckpointTool.csproj -c Release -- self-test` 通过，输出 `archive_contract_tests_passed assertions=35`：省略选择器命中 `combat_start`，显式 `latest` 命中最近可搜索检查点，显式 `end` / `recorded` 命中结束检查点；批处理运行目录保持仓库内且不跨卷。
- `pwsh -NoProfile -File tools/testing/test-headless-runtime.ps1` 通过，输出 `HEADLESS_RUNTIME_SELFTEST_PASS repository-local-default/parallel2/exclusive/resource/unknown/ownership/stale/warm/instance-cleanup`：不启动游戏，默认实例根位于传入仓库的 `.local/headless-instances/<实例>`，并与仓库处于同一文件系统根。
- Release 编译通过，0 警告、0 错误；`pwsh -NoProfile -File tools/inspection/verify-refactor-boundaries.ps1` 通过，输出 `REFACTOR_BOUNDARIES_OK search_files=192`：固定问题包默认 `start`，要求 Windows/Linux 启动器使用仓库内实例根，并拒绝旧的用户目录实例路径回流。
- Native 开战恢复修正由真实包验证：`c2cc9348214042d9b94222298e76ea9c` 的唯一初始差异是战斗外 `UnknownMapPoint` RNG（记录 counter 8，调试入场恢复 counter 7）。进入战斗后从配对检查点恢复 `UpFront`、`UnknownMapPoint`、`TreasureRoomRelics`，保留真实入场对 Shuffle／Niche／战斗 RNG 的推进；RestoreOnly 返回 `restored`，原生二进制和 continuation 均通过。曾尝试在入场前恢复整组 RNG，导致敌方124→130、洗牌331→347等重复推进，已撤回且不计为通过。
- 8 个非静默猎手能力反馈包使用 selector `start`、当前 VeryHigh（Beam135、500000节点、卡牌/牌堆选择72/42/54、软时限300秒）与外层300秒运行。7个 `search_completed` 且均为 `combat_start` / cursor 0、严格恢复、敌方0HP完整胜利：`70b7d21a` 9战损/0药/T8/227130展开；`79d3f7e2` 8/0/T11/176545；`869658e2` 20/4/T12/58889；`9a1e882c` 14/1/T17/92263；`bebfa1d7` 19/0/T10/71460；`c2cc9348` 10/1/T8/38487；`f25f8886` 0/0/T10/19656。`dc708a1f` 在300秒外层超时、没有结果，不计质量，也未提高预算或重跑。
- 未运行可见 Steam。测试结束后游戏进程为0，`D:\Desktop\sts2mod\CombatSolver\.local\headless-instances` 为空，`C:\Users\The_M\AppData\Local\CombatSolver\headless-instances` 不存在。

## 0.41.0：全卡池单人能力牌建模（2026-09-17）

- `dotnet run --project tools/testing/checks/PowerCardValuationChecks/PowerCardValuationChecks.csproj -c Release` 通过，输出 `POWER_CARD_VALUATION_CHECKS_OK total=104 silent=17 ironclad=19 defect=20 regent=18 necrobinder=18 colorless=12`：覆盖六个卡池登记总数与各池数量、每张牌唯一登记、卡池与推导 CardId 一致、MultiplayerOnly 七张明确排除、`WhiteNoise` 不作为能力牌登记、未登记牌不创建承诺、纯战后收益的 `ROYALTIES`/`FORBIDDEN_GRIMOIRE` 不创建战斗内承诺、无登记能力不增加组合成员；每池覆盖防御/成长、资源/牌流、延迟收益、反协同或启动风险、需专搜五类代表；路线准入覆盖零触发拒绝、当前/未来触发窗口与阈值边界、免费启动与高费硬开差异；承诺生命周期覆盖单能力与双能力（家族 OR、优先级取高、卡牌去重、真实兑现退出、越回合到期）；逐卡估值覆盖燃烧升级差异、倒数计时灾厄延迟、冰雹风暴零冰霜球拒绝、非凡技艺双属性、王国资产战后金币、碎片整理集中与球数。
- Release 编译通过，0 个编译警告、0 个错误。
- `pwsh -NoProfile -File tools/inspection/verify-refactor-boundaries.ps1` 通过，输出 `REFACTOR_BOUNDARIES_OK search_files=191`：门禁已更新为通用多卡池承诺边界，并确认能力估值仍未进入 `CombatBeamSolver.FinalPlanOrdering.cs`。按用户约束未运行 WSL/Bash 门禁，不记为通过。
- 集成验收（每个角色一个 `coverage/corpora/novelty` 精英短场景，`-GeneratedScenarioPath` + `-EvidenceDirectory` + `-CleanupInstanceOnExit`）：`dev-00-ironclad-elite`、`dev-01-silent-elite`、`dev-02-defect-elite`、`dev-03-regent-elite`、`dev-04-necrobinder-elite` 全部 `status=Passed` 且 `error=null`，均完成一次完整搜索并给出 `InitialPolicy` 结果。每次调用后实例被删除，最终 `C:\Users\The_M\AppData\Local\CombatSolver\headless-instances` 为空。
- 未执行：逐卡玩家复核、复杂机制逐卡专用兑现证据、可见 Steam 会话性能与战损对照，均在文档中明确标为未验证。
- 玩家联合评审采纳后复跑纯合同：`POWER_CARD_VALUATION_CHECKS_OK total=104 ...`，新增断言覆盖 `BARRICADE`、`AUTOMATION`、`DARK_EMBRACE`、`VICIOUS`、`CONSUMING_SHADOW`、`COOLANT`、`ORBIT`、`PANACHE`、`FURNACE` 等评审结论；Release 编译与 PowerShell 结构门禁仍通过。
- 回归哨兵：铁甲战士 `dev-00-ironclad-elite` 短场景在评审接线前为 43 战损，把专搜标记接入前缀构造顺序/承诺席位排序后劣化为 62，撤回接线后恢复 43 并 `Passed`；`headless-instances` 为空。其余四角色沿用先前通过的短场景，未重复运行。
- 审计后复跑（2026-09-17）：Release 编译 0 错误；纯合同 `POWER_CARD_VALUATION_CHECKS_OK total=104 silent=17 ironclad=19 defect=20 regent=18 necrobinder=18 colorless=12`；PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=192`（新增 `src/Search/PowerCardValuation/Projection/PowerCardProjectionMath.cs`）。`PowerLiveCards` 排除消耗堆；群星之子改为按实际可花费星能点数（`PowerStarSpendCapacity`）而非牌张数；缓冲、凶恶、自动化、环绕轨道投影改用纯数学并加入合同断言。
- 承诺证据语义改为“路线进展／解除保护信号”，撤回按卡池隔离与铁甲专用因果兑现（`NewPoolPowerRealizedEvidence`、迟到启用 `HasRegisteredPowerPlay`），恢复 `540e4cb6` 的通用进展释放。因果版哨兵为铁甲 62、静默 40、故障 13、储君 52、亡灵 25；单因素回退后铁甲 43、储君 45，再补跑故障 13、亡灵 25。最终五哨兵为铁甲 43、静默 40（行为未变沿用）、故障 13、储君 45、亡灵 25，全部与原结果一致，`headless-instances` 为空。未启动后台 8 包与可见 Steam。

## 0.41.0：能力牌估值框架与实例清理（2026-09-17）

- 卡池目录静态核对通过：同版本六个 `CardPool` 的 `CardType.Power` 共112张，全部命中 `zhs/eng` 官方标题与中文效果；原版约束分为105张单人范围和7张 `MultiplayerOnly`，六份文档行数分别为20/18/22/19/20/13。普通与升级描述由对应原版卡牌实例格式化，未留下未解析变量或颜色标签。
- `dotnet run --project tools/testing/checks/PowerCardValuationChecks/PowerCardValuationChecks.csproj -c Release` 通过，输出 `POWER_CARD_VALUATION_CHECKS_OK silent_models=17`：除原有17张登记和首版公式合同外，覆盖17张卡的机制族与独立身份映射、灵动步法 5→8 跨阈值后的省能转攻和同等输出防伤、余像出牌格挡、速行者回合内抽牌群伤、精准按两张实际小刀逐张增伤、幻影之刃两张小刀只触发一次首刀增伤、群蛇按两张实际出牌触发、涂毒按两次未格挡命中、触媒当前毒层额外触发、毒雾三回合上毒/衰减滚动、必备工具抽弃替换/满手损失/奇巧弃牌收益、计划妥当高价值留牌与垃圾牌塞手，以及谋划专家早开/晚开差值、两回合未来播种、无弃牌窗口、来不及重新入手。逐卡路线合同另覆盖磨蚀3费硬开与奇巧0费启动、余像5点准入边界、涂毒耗尽能量拒绝、计划妥当专搜优先级、幽魂无当前防伤拒绝、幽魂长线早开/尾段覆盖/致死救场，以及谋划专家无完整兑现链拒绝；生命周期断言奇巧附着只增加进展，真实自动出牌完成兑现，越过回合上限则到期。
- `python tools/testing/checks/BeamWidthPortfolioChecks/run.py` 通过，输出 `BEAM_WIDTH_PORTFOLIO_OK checks=87`：能力成员固定排在基线之后；共享余量耗尽时仍取得请求节点上限20%的专用预留，完整低战损终局可以接管，同分保留基线，未到终局的能力成员不参与比较；无可达能力的专用 Gate 拒绝成员。普通/激进席位合同分别覆盖 Beam 60 的5席/20席及小 Beam 至少保留一半普通席位。
- `pwsh -NoProfile -File tools/testing/test-headless-runtime.ps1` 通过，输出 `HEADLESS_RUNTIME_SELFTEST_PASS ... /instance-cleanup`；无游戏替身验证在租约释放、无存活私有游戏且所有权匹配时删除完整嵌套实例目录。
- 第二版第二批生产版本加入后，Release 编译通过，0 个编译警告、0 个错误；PowerShell 结构门禁通过，确认未来灵动投影、楼层投资、能力成员预留和逐能力后验职责存在，能力估值仍未进入终局排序。
- 本轮按用户要求不处理 WSL，没有执行 Bash 结构门禁或 Linux helper 自测，不记为通过。
- `ISSUE-3881-FOOTWORK-MULTI-POSTERIOR` / `170c25910ea0433baa695aa8fa8d7015` Passed：恢复知识恶魔原始 `:3` 根；普通与激进能力成员均从旧版跳过改为完整运行，基线15战损；灵动固定前缀的普通/宽/次段/基础分后验分别为1/3/24/2战损，普通后验21,596展开并以1战损接管，总工作55.14秒、42.82GB累计分配。原包旧版本玩家手动后为0战损，本次仍差1点，不写成完全解决。`:5` 同版本上界复跑在搜索前因原生事件药水槽3/玩家2槽不匹配失败，失败证据保留。全部无头调用使用 `-CleanupInstanceOnExit`，结束时实例目录为空；未运行可见 Steam或WSL。
- `REPLAY-BOUNDARY-CONTRACT` / `39e3c670680749bc893d8e8a0451e187` Passed：旧指纹缺失卡牌关键词时必须逐张匹配 `replay-state` 保存值；旧开战边界允许规范位置缺失的零值 `FlameHp` / `AttackStarts`，非零、重复、错位及其他牌状态差异仍拒绝。Release编译0警告/错误。
- 5份能力世界线主包全部恢复并完成当前VeryHigh搜索。`57144c6f`、`bfbb533b`、`c11060be` 的 continuation/native-state 均通过；`429834c8`、`5355faf5` continuation通过，旧模型编号映射未记录使native-state不可比。当前战损依次为16/24/5/15/1，旧报告为43/39/25/55/20，玩家投影为16/14/1/36/2；全部0药、完整胜利。能力固定前缀分别取得16/24/5/24/1；除实验体由15战损普通成员胜出外，其余四份的能力前缀就是当前最优。
- 用户要求停止后未继续运行部署；已启动的代表部署请求在产出结果前终止，不计为通过。其进程与实例目录已删除，最终 `headless-instances` 为空。


## 0.40.2：多策略路线搜索默认关闭与大战损引导（2026-09-17）

- 设置与 UI 合同已更新：新安装默认关闭多策略路线搜索；245→246 迁移只推进版本，完整保留玩家已有的开启／关闭状态与永久隐藏横幅选择。多宽度路线精炼仍默认开启且没有独立横幅。
- 两条玩家引导统一以预计损失至少 8 HP 为「大战损」门槛：7 HP 及以下不显示，8 HP 起显示。多策略横幅还要求功能关闭，点击可永久隐藏；主动开启功能同样不再提示。
- `NOVELTY-PORTFOLIO-SETTINGS` / `e53b50614756461481b31d5902f5f01b` Passed，22.47 秒：验证新安装默认关闭、246 迁移分别保留玩家已有的开启和关闭状态、设置往返、性能页控件、请求冻结，以及多策略与性能预设两条引导共同采用 7／8 HP 边界。
- `UI-LOCALIZATION` / `484adb3b62f34561b54ef4a4609dbde0` Passed，25.67 秒：eng/zhs/zht 共 426 项目录，「大战损」引导中英文文本、功能关闭/开启、7／8 HP 边界、点击永久隐藏与 SpeedX 引导合同通过。Release 编译 0 警告、0 错误；PowerShell 结构门禁 `search_files=114` 通过；未启动可见 Steam。

## 0.40.2：请求级搜索进度（2026-09-17）

- 控制器 UI 合同已更新：同一请求从主搜索切到后续搜索时，即使当前子搜索节点数重置，进度仍按 10 秒请求预算从 5% 推进到 6%；累计世界线与候选路线展示保持原口径。合同另断言超过软时间预算后仍固定显示 95%，避免排空与最终复核被显示为已经完成。
- 本轮只执行 Release 编译与结构门禁；按用户要求未运行无人战斗或可见 Steam 测试，以上合同改动已编译但未在游戏进程中执行。

## 0.40.2：变形池根快照缓存（2026-09-17）

- `TRANSFORMATION-POOL-CACHE` / `c8c552fc5f52400b849c1a77a77fefce` Passed，23.89 秒：断言缓存序列与上游 `GetUnlockedCards` 逐实例同序、跨 `Fork` 不可变共享、可变池被拒绝、外来约束被拒绝、外来池被拒绝、规范无色池（Quest/Event/Ancient/Token 回退）被正确服务且同序、缓存路径与原生路径产出同一张牌且 `CombatCardSelection` 五字段 RNG 状态与完整预测延续状态一致、父模拟与实机根未被改动（`comparisons=4`）。使用隔离无头实例并在完成后退出，未启动可见 Steam。
- 该契约初版在 `Transformation pool accepted a changed pool or constraint.` 失败。排查为**契约自身错误**：它断言无色池必须被拒绝，但无色池是合法回退池、本就应被服务；实现无缺陷。已改为具名的正/负断言并复跑通过。不把这次失败记作实现缺陷，也不把修正前的运行记作通过。
- 等价性：`tools/search/OfflineSearchHarness/compare_results.py` 对基线 `41f9478` 与候选产物逐字段比较，**7 个根全部一致**：crab@2000 170 字段、KAISER_CRAB_BOSS@6000 242、silent-discard@6000 192、QUEEN_BOSS@6000 152、THE_KIN_BOSS@6000 174、KNOWLEDGE_DEMON_BOSS@6000 212、THE_INSATIABLE_BOSS@6000 234，全部 `mismatched_roots=0`、无 `left_only`/`right_only`，覆盖 `solverMetrics`（排除时间/内存/GC）、`route` 每个动作、根 `ContinuationStamp` 与 `catalogFingerprint`。
- 固定工作量 A/B：同根、`VeryHigh`、beam 48、`--dop 1`、顺序 ABBA。KAISER_CRAB_BOSS @2000 节点 18.78 秒 → 9.07 秒（2.072 倍）。**压力场景**（沿用 crab 生成场景规格只换遭遇与幕索引，预算标定到基线 ≥20 秒）：KNOWLEDGE_DEMON_BOSS 54.11→14.76 秒（3.667 倍）、THE_KIN_BOSS 41.78→14.43 秒（2.895 倍）、KAISER_CRAB_BOSS 37.32→14.86 秒（2.511 倍）、THE_INSATIABLE_BOSS 23.63→10.79 秒（2.191 倍）；**基线 >20 秒的 4 个根加速比 2.191–3.667 倍**。不走变形路径的提前穷尽根为 1.041 倍（silent-discard）、1.426 倍（QUEEN_BOSS）；**对照组**把同批 Boss 遭遇改用默认薄牌组后三者全部提前穷尽、加速比 0.984 / 1.015 / 0.990 倍（收益为零，略低于 1.0 属 1–3 秒量级噪声，不记作退化）。Release 构建 0 警告、0 错误。
- 内存：每节点总分配 2.06 MB → 1.04 MB；但峰值工作集约 385 MB → 约 405 MB、峰值托管堆约 157 MB → 约 179 MB，**未改善**。峰值成因未取证，不作为通过项。
- **并行度 8** 复测（12000 节点、同根、顺序 ABBA）：厚牌组 2.583 / 2.398 / 2.178 / 1.865 / 1.696 倍（KAISER_CRAB_BOSS / KNOWLEDGE_DEMON_BOSS / THE_KIN_BOSS / QUEEN_BOSS / THE_INSATIABLE_BOSS），薄牌组对照组 1.000 / 0.989 / 0.983 倍。并行度不改变结论。
- **DOP 8 的字段级等价性不可用**：`compare_results.py` 报 `DIFFERENT`，但差异仅 `roundReplayPrefixCaptures` / `executionChoiceReuses` 两个调度计数器，`route` / `rootState` / `catalog` 全为 0 处；且**基线自比**在 DOP 8 下同样在这一个计数器上不同（A1 vs A2 7808 vs 7794），证明是并行调度非确定性而非语义差异。不把 DOP 8 的 `DIFFERENT` 记作实现缺陷，也不把它记作通过；字段级等价性以 DOP 1 的 7 根全一致为准。
- 结构门禁：Bash `tools/inspection/verify-refactor-boundaries.sh` 通过，`REFACTOR_BOUNDARIES_OK search_files=114`、退出码 0（增量 1 即本次新增的 `src/Search/RootCombatTransformationPoolSnapshot.cs`）。Release 构建 0 警告、0 错误。
- 未执行：可见 Steam 性能未测，上述倍数只是无头数据，不外推为实机收益。详见[性能报告](../performance/transform-pool-root-snapshot-20260917.md)。

## 0.40.1：多策略回合准备选牌修复（2026-09-16）

- 夸克打包结构合同通过：只用现有 `CombatSolver-0.40.0.zip` 调用独立打包函数，临时产物为 15,728,765 字节，保留 5 个 CombatSolver 根条目并仅新增一个 13,663,763 字节的无压缩 `QUARK_UPLOAD_PADDING.bin`；RitsuLib 条目与嵌套 ZIP 均为 0，文件严格超过 15 MiB。未执行上传、网盘移动或正式发布。
- 日志站基线：0.40.0 共取得 12 份 `TurnSetupFailure` 问题包，覆盖烤手套、能力牌及多个职业/遭遇；12 份异常栈均进入 `RunNoveltyPortfolioPass -> CombatBeamSolver.RunNoveltyOpen`。11 份在 `BuildContinuations -> Replay` 因未回放准备选牌而找不到首张手牌，1 份由终结准备根进入 `Expand`。服务端筛选结果是玩家主动提交的问题包，不作为总体发生率统计。
- `NOVELTY-TURN-SETUP-CHOICE-0400` / `26117906a7a4464c83cc1a9a10ac803f` Passed：显式强制多策略路线搜索、固定 5 秒预算、DOP2，真实烤手套准备选牌被路线保留；最终搜索 1,578 个节点、10,669 次转移，3 回合零战损获胜，没有准备阶段失败。Release 构建 0 警告、0 错误。
- 两个全新无头实例在建局时停在原生 `There's another modal already open`，均到 120 秒后由启动器停止，未进入搜索且不计为回归失败或通过；改用此前已完成初始化的隔离实例后，同一请求正常通过。

```powershell
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId NOVELTY-TURN-SETUP-CHOICE-0400 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -Seed NOVELTY-TURN-SETUP-CHOICE-0400 -RelicsJson '[{"relicId":"TOASTY_MITTENS","addWithoutObtainedEffects":true}]' -FixedSearchBudget -SearchBudgetOverrideMilliseconds 5000 -SearchMaxDegreeOfParallelismForTest 2 -UseNoveltyPortfolioForTest -PerformancePresetForTest Low -ExpectedInitialSetupChoiceCountAtLeast 1 -ExpectedInitialSetupChoiceSourceId TOASTY_MITTENS -StopAfterInitialSetupAssertion -TimeoutSeconds 120 -ExitOnComplete
```

## 0.40.0：有界新颖性组合与设置迁移（2026-09-16）

- 引导横幅回归：`UI-LOCALIZATION` / `56c829a25d294a95bed3959f98322c7f` Passed，eng/zhs/zht 共 426 项目录，验证多策略与皮皮极速横幅的当前语言文案、点击永久隐藏和设置往返；`NOVELTY-PORTFOLIO-SETTINGS` / `1bb50f409cd843e797a2b16669219da4` Passed，验证精炼默认开启、多策略默认关闭、旧设置缺失横幅字段时采用显示默认值、两类横幅关闭选择持久化及搜索请求冻结。Release 构建 0 警告、0 错误；两项均使用隔离无头实例并在完成后退出，未启动可见 Steam，因此不把无头结果写成真实排版验收。
- 节点预算与强制精炼迁移后，离线预设合同通过：四档节点预算为 60,000 / 120,000 / 250,000 / 500,000，时间与 Beam 保持原值；自定义 1,000,001 节点的迁移断言已编译，迁移 243→244 强制开启精炼并保留 Custom、多策略、NoGC 与内存值，244 后再次关闭保持关闭。无胜利追加搜索原策略和 8 项请求合同通过，`BEAM_WIDTH_PORTFOLIO_OK checks=73`、PowerShell 结构门禁（`search_files=113`）及 Release 构建通过，构建 0 警告、0 错误。独占与并行无头模式各尝试一次 `NOVELTY-PORTFOLIO-SETTINGS`，均在 120 秒内未取得宿主资源，测试未启动且未停止现有实例，因此游戏内设置断言未记为通过。
- 合并 PR #102/#103 后的本轮审计：修正次段成员误触发长期资源 `RankBest` 的作用域，并把多宽度路线精炼改为默认开启。`BEAM_WIDTH_PORTFOLIO_OK checks=73`、新颖性 21+6+12,000+7+9 项离线合同、PowerShell 结构门禁（`search_files=113`）和 Release 构建均通过，构建 0 警告、0 错误。`NOVELTY-PORTFOLIO-SETTINGS` 已更新默认值断言并成功编译；本轮执行时独占无头槽持续被其他实例占用，120 秒准入超时，测试未启动，未记为通过，也未停止现有实例。
- 新增默认关闭的多策略开关，当前上游 `7f806de`、游戏 0.111.0、RitsuLib 0.6.2。14 个固定根的 28 份完整 Smart 请求全部运行成功，两边均 12 个完整胜利；其中 3 根投影战损下降，其他根战损相同。每份样本独立进程、交替 AB/BA，核对五份输入/原生开局 JSON，使用请求 `total_*` 指标。具体成本、反例与未完成胜利见[报告](../strategy/bounded-novelty-search-20260916.md)。
- 离线合同：21 项参考调度 + 6 项祖先配额 + 7 项有界队列 + 9 项共享预算；12,000 个混合状态与参考新颖性完全一致。两个原生结构门禁均通过，`search_files=113`；最终 Release 11.48 秒、0 警告/错误。
- `GENERATED-NOVELTY-SEARCH` 加 `control-checks.flag` / `2d59455a85d34d82b28540efe4b4a12b` Passed：实际 DOP2 接管当前回合、逐动作接管已显示路线、取消向外传播、工作只计一次及 live/shadow 根不变。
- `UI-LOCALIZATION` / `49c873258c7f4a32a68311311f5078a7` Passed，eng/zhs/zht、424 项目录；`NOVELTY-PORTFOLIO-SETTINGS` / `60e2e21739604b578dbaa6c2e197a9d5` Passed，默认关闭、持久化、性能页控件与请求冻结。
- `NOVELTY-HP-TARGET-STOP` / `07da6f2abdd0486f947f02fe1e4c922a` Passed：真实前置探索、目标战损、固定重放成长、致命成长、强制一药/保留备用药及至少一药；`ROUTE-CACHE-RECORD-V0111` / `0f890408d5784ecca93e939f84f079ae` Passed，新增策略隔离缓存身份并保留恢复/手动重算语义。
- 综合 `CONTROLLER-SESSIONS-527` / `51d1ea6438c646bca26081bc9f5c9a89` 在窗口缩放/尺寸持久化断言失败（`configured=True, persistence=False`），尚未到新增设置断言。完整综合场景未通过，新增设置改用上述独立同源合同验证；不把失败归因为新搜索或记成通过。
- 双组合开关 / `1bb9e9c368824ce892b3efef1bef228b` Passed：30秒请求中实际运行3个Beam宽度，探索加全部Beam成员11,443节点≤24,000主搜索上限；上游药水审计仍按每层节点预算及请求截止时间执行。
- 原生两端 ScenarioId 参数通用；[复跑方式](../../../tools/testing/checks/BfwsResearchChecks/README.md) 同时说明 PowerShell/Bash 协议与 Linux 独立进程包装器。5 个新根的 10 份对照完整获胜且终局策略摘要相同，但多数成本更高；另有两个场景8份独立ABBA。三场原生部署与首次预测的战损/药水一致且计划外重算0，包含DOP2与真实1GB NoGC预算4次回收续搜；runId和全部代价见报告。没有可见 Steam、FPS 或 Windows 实机性能结论。

## 录像回放临时费用与充能球恢复（2026-09-15，未发布）

- 亡灵契约师/女王原包修复前 `71f63f33ad1d4e65bb52e66ba1cc50e8` 在严格导入时失败：手牌第 8 张 `SPUR` 记录为带 `EndOfTurn, WhenPlayed` 清除时机的 0 费，导入后为基础 1 费。修复后同一原包 `SHOWCASE-BUNDLE-IMPORT-V0111` / `d65e84b66d3f40319cc9495822aaa7f0` Passed，23.78 秒；8 张模型手牌与界面节点一致，牌堆计数一致，录像路线接纳且本地搜索 0 次。
- 故障机器人/女王原包的实机日志在首张 `DUALCAST` 进入 `NOrbManager.EvokeOrbAnim` 时抛出“Sequence contains no matching element”，随后路线在第 19 步因首张牌未完成而失配。修复后同一原包 `SHOWCASE-DEFECT-DUALCAST-0390` / `e0969979ed5d4373aef7a96cf615e44d` Passed，20.11 秒；球队列 1 个模型与 3 个原生可见槽位引用一致，首张双重释放正常结算，完整预计算路线第一回合无伤击杀，计划外重算 0，并经原生终端按钮返回主菜单。
- 最新实机日志 `combat-09f187ae444d4c538f6ba63efb85f308.jsonl` 显示同一机器人路线 38 步完整结束、四次 `DUALCAST` 均完成且状态失配为 0，确认新增反馈属于可见节点生命周期。补充容器检查后，同一原包基线 `SHOWCASE-BUNDLE-IMPORT-V0111` / `170ad140151c43769d9cce3c2b1ab7bc` 明确失败：管理列表外仍有旧 `NOrb` 留在容器。即时清理后 `SHOWCASE-DEFECT-DUALCAST-ORPHAN-UI` / `474124f84c1c4e2fafbf04f5c08275a1` Passed，35.67 秒；容器节点与管理列表一一对应，完整 38 步路线第一回合无伤击杀，四次双重释放及中间推球完成，计划外重算 0。
- Release 构建通过，0 警告、0 错误。两项均使用 Windows 隔离无头实例和玩家本次实际下载的原始 `ShowcaseBundleV1`；未启动可见 Steam，未执行 Bash 门禁。

## 0.39.0：多宽度路线精炼与 RitsuLib 0.6.0 适配（2026-09-15）

- 使用本次实际下载的 `ShowcaseBundleV1` 在 Windows 上验证五个协议文件全部解压，其中四个负载文件均在关闭写句柄后通过大小与 SHA-256 校验，未再出现共享冲突。
- `SHOWCASE-BUNDLE-IMPORT-V0111` 先复现旧包原生状态仅有选牌/奖励网络序号差异；在完整 22 Mod 栈下进一步复现 BaseLib 把旧二进制误读为非法字典容量。最终同一份静默猎手/永世沙漏旧包在基础 Mod 栈和当前完整 22 Mod 栈均通过整包入口，进入第 1 回合并接纳 18 个预计算动作，本地搜索 0 次；新规范原生状态的格式标识、自身匹配和单字节损坏拒绝合同同时通过。完整栈 runId `b356adc2efaf4979af145903d9feca85`，48.20 秒；最终源码基础栈 runId `ba22e3212df0424f87c949685dd9b3cc`，22.27 秒。
- `SHOWCASE-HAND-VISUAL-RESTORE` / `fdf489b0f12b491a878d52711d04cfc3` Passed，57.74 秒。使用玩家刚回放的静默猎手/永世沙漏原包恢复第一回合，模型手牌 9 张、界面手牌节点 9 张且引用顺序一致；随后本地搜索 0 次，沿包内路线第一回合击杀 Boss。修复前截图中的 18 张来自旧 9 个手牌节点未释放后与恢复手牌重叠，不是录像包记录了 18 张模型手牌。
- `SHOWCASE-NATIVE-TERMINAL-RETURN` / `4ada3b8429d9447b9a4231e514d062b6` Passed，64.75 秒。同一静默猎手/永世沙漏原包由预计算路线第一回合击杀后，测试触发实际原生终端奖励页的 `ProceedButton.Released`，确认跑局已清理、主菜单已加载、原生转场完成且终端覆盖入口已移除；本地搜索仍为 0。音乐切换复用原生继续游戏入口的 `StopMusic` 与角色转场流程，未做可听音频验收。
- `SHOWCASE-PILE-VISUAL-RESTORE` / `dda17ba3d52947cab53699441bfd77b4` Passed，83.56 秒。使用玩家本次实际下载的最新静默猎手/永世沙漏包恢复：手牌模型 7 张、可见 holder 7 个；抽牌堆模型与按钮均为 4，弃牌堆与消耗堆模型/按钮均为 0。随后沿包内路线第一回合无伤击杀，本地搜索 0 次，并通过原生终端按钮返回主菜单。恢复源码已移除可见 `RemoveFromCombat` 路径并将新 holder 同帧放到最终位置；无头测试不构成肉眼动画验收。
- 日志后台 Python 3.12 全部 65 项测试通过，其中录像库合同覆盖收藏读写、独立筛选、收藏阻止同根替换，以及收藏不占分组自动清理额度；生产默认普通录像上限为每组 2000。退出后既有 `test_reports_v2` 临时 SQLite 句柄出现一次 Windows 清理告警，不影响测试退出码和断言结果。CombatShowcaseRecorder 1.0.2 与 CombatSolver Release 构建均为 0 警告、0 错误；Windows PowerShell 结构门禁通过（`search_files=91`）。
- 本轮只运行隔离无头恢复和 Release 构建，不使用 Computer Use、不启动可见 Steam、不执行 Bash 门禁。测试结束后已停止隔离游戏进程。
- 合入 PR #94/#96 后，Windows Release 构建通过，0 警告、0 错误；PowerShell 结构门禁通过（`search_files=105`）；Beam 宽度组合离线合同通过（`BEAM_WIDTH_PORTFOLIO_OK checks=59`），其中包含默认关闭、门控、预算和最终质量仲裁。
- `ROUTE-ROW-REUSE` / `91fe2c42bc3c44d6bdec080606ac9967` Passed，23.97 秒：覆盖同值复用、显示字段变化、选择/击杀/顺序、空路线、失败重试、部署状态和语言往返。
- `CARD-CONTINUATION-EXPANDED-SEARCH` / `56398494c8b24ff9abe44bef784d7a0c` Passed，8.95 秒：实际穿过扩展后的卡牌选牌续执行和搜索边界。两项均在 Windows 隔离无头实例执行；完成后实例已停止，本次没有运行 Bash 门禁或可见 Steam 测试。
- RitsuLib 0.6.0 失败基线 `UI-LOCALIZATION` / `1a41b720610146afb894f3c9a25c5c18` 在 120 秒上限退出；日志直接定位为旧 `RitsuBaseLibTargetTypeLookupPatch` 找不到已经被框架改写的 `Assembly -> Type` 私有闭包，CombatSolver 初始化在应用自身补丁前中断。删除重复适配后，同一完整 0.6.0 分包启动并运行 `UI-LOCALIZATION` / `8d5c7bcdac8d438396cc12fb4d3459a4` Passed，28.53 秒，eng/zhs/zht 与 420 项目录通过。
- `NATIVE-HAND-CHOICE-REPLAY` / `fd5b50d371c041129e4c0e82d266a858` Passed，29.82 秒：RitsuLib 0.6.0 下两组燃烧契约选择、失配后人工恢复保持；新增生存者在 `Instant` 模式打出、选择防御弃牌、退出原生手牌选择并完成动作的直接合同。
- `BEAM-PORTFOLIO-SETTINGS-0387` / `0064d9a37309472db95ba9c1fd7fe353` Passed，29.35 秒：开关默认关闭，设置往返、性能页控件与搜索请求冻结一致；首回合原生部署完成。组合器与门控离线检查 `BEAM_WIDTH_PORTFOLIO_OK checks=59`，Windows PowerShell 结构门禁通过（`search_files=105`）。没有运行 Bash 门禁或可见 Steam 测试。

## 多宽度路线精炼扩展成员类型（2026-09-16，未发布）

- 组合器与门控离线检查 `python3 tools/testing/checks/BeamWidthPortfolioChecks/run.py`：`BEAM_WIDTH_PORTFOLIO_OK checks=73`，新增默认成员含且仅含一个次段成员和一个基础分成员、基线成员是普通宽度成员、两种成员的 Profile 各只多一个标志、显式宽度列表不追加、`MoveLeadingBandToTail` 四种情形。Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=105`，Release 构建 0 警告、0 错误。
- 一致性：本分支 DLL 在两个标志都未置位时，与 0.39.0 main（`7f806de`）的 DLL 在同一离线宿主、同一 5 根生成场景（Very High、固定节点预算、DOP 1）上 61 项 `solverMetrics`、全部动作与根戳记逐字段相同。
- 开关对照：同一 DLL、120 根生成场景每 4 根取 1 的 30 根，基线（两个标志都关）与次段开、基础分开各跑一次。次段作为组合成员：Very High 净 +51 HP 当量（变好 5、变差 0，1 根死转活），Medium 净 +21（4 / 1，1 根死转活）。基础分作为组合成员：Very High 净 +41（4 / 0，1 根死转活），Medium 净 +86（9 / 0，1 根死转活）。次段两组与基础分 Medium 组 30 根全部有效；基础分 Very High 组有一根（IRONCLAD-ELITE-04）撞 600 秒时间保险，该根在基线下同样撞保险。
- 本轮只运行离线宿主与离线检查，没有可见 Steam、Windows 无人测试或生产路径计时。

## 离线搜索宿主（2026-09-16，未发布）

- macOS Release 构建：`CombatSolver.csproj` 与 `tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj` 均 0 警告、0 错误。
- Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=105`；Beam 宽度组合离线检查 `BEAM_WIDTH_PORTFOLIO_OK checks=59`。两条 `partial` 边界声明已同步到 `.sh` 与 `.ps1`。
- 新宿主对旧研究版宿主逐字段一致（同一份 0.39.0 DLL、同一批生成场景请求、`VeryHigh`/beam 135/nodes 100000/分支 72-42-54、`--dop 1`、`--budget-ms 600000`、`searchMode=Evaluate`）：BASE5 五根比 466 个字段，30 根子集比 2719 个字段，全部相同，没有单边多出来的根。比较口径见 `tools/search/OfflineSearchHarness/compare_results.py`（`solverMetrics` 排除时间/内存/GC 字段、选中路线逐动作、根 `ContinuationStamp`、目录指纹）。
- `--search-mode Coordinator --use-portfolio` 三根（`High` 预设、60 秒预算）全部 Passed，`solverMetrics.portfolioMembers` 各 3 个成员，宽度 `[90, 60, 135]`，即默认的 `[W, 2W/3, 3W/2]`；开关关闭时只有 1 个成员。
- 本轮只在 macOS 上跑离线宿主与 Bash 门禁，没有启动游戏、没有跑无人测试、没有 Windows 验证。
- 合并到当前主线后的 Windows 首次验证发现宿主工程缺少多版本 RitsuLib 的 `0.111.0` 引用目标，补齐后编译通过；首次运行随后发现解析器只查旧单目录，无法加载 `STS2-RitsuLib.Runtime`，已改为同时解析版本兼容目录与共享程序集目录。宿主原默认遭遇 `JAW_WORM` 在当前目录不存在，已改用项目现有的 `FUZZY_WURM_CRAWLER_WEAK`。最终运行结果记录在本次合并提交。
- Windows 合并验证：CombatSolver Release 与 OfflineSearchHarness Release 均 0 警告、0 错误；PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=113`。离线宿主默认场景最小烟测通过，推进到玩家第一回合并完成 Evaluate 搜索：182 展开、507 转移、预计战损 4，未启动 Godot 或可见 Steam。

## 路线界面复用与派生计算实验（2026-09-15，未发布）

- 交付场景 [`ROUTE-ROW-REUSE`](../../../coverage/fixtures/ui/route-row-reuse.json)，runId `83d3d63b552f4393b8ffc03e8fba9060` Passed（25.374秒）：实际Godot控件身份、同值新数组、全部显示/本地化字段变化、选牌/击杀/顺序、空路线、状态页、构建失败后重试、部署索引/高亮、语言往返和订阅清理。原生双端ScenarioId入口，IRONCLAD、FUZZY_WURM_CRAWLER_WEAK、敌HP999、NoGC关闭、120秒、显式EvidenceDirectory；不启动搜索。
- `UI-LOCALIZATION` / `483a2e7173044a26a730997020c911b3` Passed（6.812秒）：eng/zhs/zht、415条目录、保留/恢复路线卡名、升级/嵌套选牌、序列化和无计划外重算。卡牌投影、名称与语言通知源码与交付源码相同；行缓存成功后发布的边界由交付合同另行覆盖。首次复用进程运行暴露同帧语言通知遗漏，失败与修正后证据同时保留。
- 交付Release 11.10秒、0警告/错误；Bash/PowerShell结构门禁均为 `search_files=102`。搜索层没有追加差异，投影洗牌缓存及专用缓存合同已从生产源树撤回。
- 已撤回实验的两场8份完整请求、原生120份牌序对照及严格增量结果仍记录于[正式PR追加报告](../performance/performance-pr-20260915.md)和[结构化证据](../performance/derived-work-reuse-20260915.json)；不把这些实验数字称为交付搜索提速。不启动可见Steam，未验证FPS或可见帧时间。

## 0.38.6 上游合并后的性能 PR 验证（2026-09-15，未发布）

- 对当前上游三场12份完整ABBA均通过严格oracle；蟹战耗时−15.95%、分配−15.51%、峰值−1.64%，扩展弃牌耗时−5.42%，携药轻场景−2.25%；后两场分配与峰值均下降。仅限本机无头样本。

- 正常Release 14.56秒、0警告/错误；Bash与PowerShell结构门禁通过，`search_files=102`。比较器源码未修改，复用此前16项通过证据。
- 四组严格增量：卡牌扩展 `ae00c0b92e1b485b83c04c65328d5441`、药水 `a8a8a40b79b548659aab223a2220caf4`、动作/EndTurn嵌套 `5feed9b8a4ad481096bd03980af551d0`、首回合准备 `358e83a8965841db9f40e7c5d2562ef8`，均Passed。
- 原生跨回合连续选择 `98135e17d0c84660b9bc5958547180af` Passed，35分支与完整状态对账；上游成长/药水早停 `3a599fbe370848258b538fa12fde2e7a` Passed。
- 格挡药夹具首次 `7f79df25a74f486aa7388e33a20fcc28` Failed：本场只掉4血，未达到断言所需9血。仅补敌方5层力量并将上限设为120秒；修正后 `36e20d691de1424e9b5e7e14196623ce` Passed，实际省9血、T2无伤获胜、零计划外重算。Linux启动器支持与PowerShell相同的 `expected-initial-deterministic-block-potion-inserted` 三态断言。
- 全部合同上限120秒，实际部署Instant/0秒；当前上游完整请求ABBA、基线Testing支持补齐及所有失败见[正式 PR 验收](../performance/performance-pr-20260915.md)。无可见Steam或Windows帧时间结论。

## 选牌续执行批量实施（2026-09-14，未发布）

- `CARD-CONTINUATION-EXPANDED` / `15ed72aac4504a0f8c56133251fe1c2c` Passed：NECROBINDER、41来源82普通/升级分支；80种选择共380候选、2种无选择；完整状态/历史/RNG/身份、兄弟与DOP2、10种代表原生结算。
- `CARD-CONTINUATION-CONTRACT` / `18085ba4d6b044b58a0aef69bb4605b9` Passed：扩展后的原三牌边界、洗牌及原生合同。
- `CARD-CONTINUATION-EXPANDED-SEARCH` / `5e70bf69886741e6abdc0dd97bfce87c`，`CARD-CONTINUATION-EXPANDED-INCREMENTAL` / `151a8484b2094687a8e28780453c855b` Passed：SILENT、实际搜索前缀逐分支及完整结果对照、取消/错误排空、严格增量。
- `POTION-CONTINUATION-CONTRACT` / `bcf7b7f30d5c4b0691c08c6707a3060f` Passed：SILENT、九种药水41个选择、50次生产分支/再次访问、九种原生完整结算；状态/历史/RNG、消耗、BeltBuckle/ReptileTrinket、兄弟修改及DOP2。
- `POTION-CONTINUATION-SEARCH` / `856b8d5db4604d5fa9bb27e2070d1174` 与 `POTION-CONTINUATION-INCREMENTAL` / `0d002f4702184b2da1e106666d60b131` Passed：旧路径/DOP1/DOP2完整结果，真实嵌套回退、同父并发取消/错误排空、严格增量。
- 均用两端已有ScenarioId协议、FUZZY_WURM_CRAWLER_WEAK、敌HP999、NoGC关闭、120秒、根合同后停止；实际执行Linux无头。回合/嵌套、最终完整测量及原生部署见下；详见[阶段记录](../performance/choice-continuation-expansion-implementation-20260914.md)。

### 第三阶段与共享尾部回归

以下场景仍使用同一双端ScenarioId协议，SILENT（全41卡合同使用NECROBINDER）、FUZZY_WURM_CRAWLER_WEAK、敌HP999、NoGC关闭、120秒、根合同后停止；原生跨回合额外使用Instant/0秒。

| 场景 | 直接证据及范围 |
| --- | --- |
| `DRAW-EXECUTION-CONTINUATION` / `NESTED-DRAW-EXECUTION-CONTINUATION` | `fbdea0b824774494bc02e962669015d9` / `9deb4e09364545fab0195a71ef203273` Passed；部分抽牌、洗牌、再次捕获、全状态/历史/RNG、DOP2及原生 |
| `TURN-AFTER-EXECUTION-CONTINUATION` / `TURN-NESTED-EXECUTION-CONTINUATION` | `076aece95fa041bfad376621e623cef4` / `74e26c01154f473eaa443b322f08596e` Passed；六来源及消耗/弃牌后的深层抽牌 |
| `CARD-DECISIONS-EXECUTION-CONTINUATION` | `7e6063ff8bae4fa4af0d6e8d10094c17` Passed；重复子出牌、历史别名与原生。其他Before/Havoc/Cascade/Repeat独立完成项所在请求整体Failed，按[实施记录](../performance/choice-continuation-expansion-implementation-20260914.md)的部分请求范围引用 |
| `EXECUTION-CHOICE-SEARCH-CONTRACT` | `e23c68bf438b429c90a50c00ba435724` Passed；全来源92、Mayhem14、Cascade10、后续回合35分支，全部状态/历史/洗牌/一次transition与父/live隔离 |
| `EXECUTION-CHOICE-SEARCH` / `EXECUTION-CHOICE-INCREMENTAL` | `e00560d9502d4faaaf1a6ccdbcbd59c1` / `71cd4c23b66e499e9ae05aeb626f81b5` Passed；完整Solve旧路径/DOP1/2、同父取消/异常排空及严格增量 |
| `EXECUTION-CHOICE-SETUP-SEARCH` / `EXECUTION-CHOICE-SETUP-INCREMENTAL` | `3b5c082e119e4414ae1187f82c45db30` / `fe41c0e260a84ccfa2ec281013b21a4c` Passed；首回合三来源真实Solve及严格增量 |
| `EXECUTION-CHOICE-SETUP-BUDGET` | `402c6f1cc87444d99332248b15bb892c` Passed；九层压力在两模式均到达相同有限预算边界，不以扩大预算获得完成根 |
| `CARD-REMOVED-PREFIX-EXECUTION-CONTINUATION` | `3b6ca0a3f7154dc2b99128822c6d9fc6` Passed；Cascade先打出并移除能力牌，再两次选牌；原完整回放/历史/DOP/原生一致，覆盖完整蟹战暴露的非牌堆列表成员 |
| `HAND-DRAW-SHUFFLE-CHOICE-REPLAY` | `c81dafb9cd84472db5e78a3bbb8f5b1b` Passed；关闭新执行续跑，保留旧稳定前缀的完整状态、DOP/取消/异常验证 |
| `EXECUTION-CHOICE-ROUND-NATIVE` | `47f1e0e03065438abb271475d31aca5c` Passed；真实EndTurn进入第二回合、连续原生选择、完整StateText一致 |
| 最终41卡/9药水与各自严格增量回归 | `b368988cf058462d8f52a1391f4d9e51` / `4866f778cb404aadb7b590f05aa4c503` / `e101b6d9dea14560a71c84d7f7a79800` / `22148a231efe482cacadb8dbe965041a` 均Passed |
| L0 | Release 0警告/错误；Bash/PowerShell结构门禁search_files=101；16项性能比较器检查通过 |

`CHOICE-CONTINUATION-STEP-AUDIT`：`88236e7e4ef748f0bead85422be84c67` Passed，73.689秒；以报告的原蟹战输入改为`mode:Setup`、VeryHigh、DOP2、NoGC关闭、120秒请求运行。内部固定20,000节点、两次主动用药，56,211次执行续接逐步对账完整状态、待选请求/有序候选、历史数量和洗牌；错误接受/拒绝分支均检查，并比较关闭续接的完整搜索结果。它是独立诊断搜索，不等同于协调器的完整三层药水审计，也不计入性能成绩。

最终三场12份正常完整请求均Passed，完整动作/路线和决策质量一致；弃牌/携药轻场景的严格工作量也一致，原蟹战保留工作量差异，按用户要求不继续归因、不标为同工作量提速。全部样本、输入错误和比较结果见[结构化证据](../performance/choice-continuation-expansion-implementation-20260914.json)。`CHOICE-EXPANSION-NATIVE-DEPLOY` / `ef6fd35b159a4aee974c826319755371` Passed，60.440秒，39动作原生执行到T1无伤胜利，HP56→56、敌HP0、`UnexpectedReplans:0`；Instant/0秒、120秒上限，使用最终正常Release。

## 自身弃牌续执行正式接入（2026-09-14，未发布）

原生两端无人启动器均可使用以下 `ScenarioId`，固定SILENT / FUZZY_WURM_CRAWLER_WEAK、敌HP999、NoGC关闭、120秒、根合同后停止：

| 场景 | 本轮证据 |
| --- | --- |
| `CARD-CONTINUATION-CONTRACT` | `7bc12837a9de419095d2ed238da8dc1b` Passed；三张牌、杂技/早有准备普通与升级、原生完整结算、全部选择、历史/RNG/洗牌、兄弟/DOP2、取消与错误 |
| `CARD-CONTINUATION-SEARCH` | 最终源码 `c37f7aea45c942d2be284d67c920dd99` Passed；真实选择链、嵌套回退、关闭复用/DOP1/DOP2完整结果、并发取消/异常排空 |
| `CARD-CONTINUATION-INCREMENTAL` | 最终源码 `91e987fc05804e28a75099bf546ae5f7` Passed；严格增量，复用与回退均命中 |
| Release、结构门禁、性能比较器 | 0警告/0错误；Bash/PowerShell `search_files=92`；12项比较器测试通过 |
| 独立完整原生部署 | `5520c13005d24e43ab9f1a3ac92f5f44` Passed；初始39动作计划，原生T1结束、HP56→56、敌HP0、计划外重算0；Instant/0秒，120秒上限 |

完整极高同工作量测量使用正常Search与独占新进程，不带增量开关；原蟹战、静默起始牌组死亡场景与独立弃牌获胜场景，共12个最终样本和9项完整对账通过。原两场保留A1后采最终F1/F2/A2，获胜场景独立ABBA；数据、GC不利变化、部署日志限制和全部runId见[报告](../performance/choice-continuation-search-20260914.md)及[JSON](../performance/choice-continuation-search-20260914.json)。既有CoverageCatalog分类与外部注册签名未变化，没有全量覆盖门禁或可见Steam测试。其他选牌来源的[扩展研究](../performance/choice-continuation-expansion-20260914.md)仅做源码和清单核对，未写为通过语义或性能测试。

## 选牌暂停与恢复窄原型（2026-09-14，独立实验）

基于 `1ef4601` 的实验 Release 构建 0 警告/错误，默认搜索与生产源码未修改。[报告](../performance/choice-continuation-prototype-20260914.md)和[结构化证据](../performance/choice-continuation-prototype-20260914.json)保存全部原始样本与失败尝试。此前投掷匕首计时受旧路径拒绝诊断污染，性能结论作废；以下三次均使用修正后的同一实验 DLL。

| 验证 | runId / 结果 |
| --- | --- |
| 投掷匕首全部9选择、完整历史/RNG/身份、兄弟与DOP2、取消/异常/释放、升级/历史前缀/真实洗牌、拒绝/嵌套回退、原生完整结算；固定工作量及受控保留堆 | `6c9670133f8242dcb2f29b4089eaa89a` Passed |
| 杂技普通/升级全部9/10选择，抽3/4弃1，真实洗牌、历史/RNG/兄弟/DOP2/嵌套回退，两版分别原生完整结算及固定工作量 | `13cb642b0ac5459c897daf032503c078` Passed |
| 早有准备普通/升级全部8/36选择或组合，抽弃1/1及2/2，真实洗牌、历史/RNG/兄弟/DOP2/嵌套回退，两版分别原生完整结算及固定工作量 | `f4559b26b5484ed5be592c706053c2fb` Passed |

复跑先按[工具说明](https://github.com/Torch1230/CombatSolver/blob/556e72994303e45ca2b2833aa09ba793d1b096cb/tools/ChoiceContinuationPrototype/README.md)在固定版本的独立 worktree 构建，使用新证据目录；runner 的 `--card dagger|acrobatics|prepared` 选择场景。Linux 无头、SILENT、FUZZY_WURM_CRAWLER_WEAK、敌HP999、关闭NoGC、每请求120秒；专属进程在结束/失败时清理。原生检查等待精确动作完成并核对完整 continuation。未执行默认 Search、完整蟹战、可见 Steam、Windows 或全量发布门禁，不作对应收益结论。

## 蟹战后续延迟优化（2026-09-14，未发布）

沿用3afbdd3的完整VeryHigh输入、DOP16与16GB NoGC，新增专用洗牌短fixture；全部非时序质量字段、开局、政策、动作/路线直接对照。原型、增量峰值反例、整批初始基线、每次数据与复现参数见[报告](../performance/crab-latency-20260914.md)及[结构化证据](../performance/crab-latency-20260914.json)。

| 验证 | runId / 结果 |
| --- | --- |
| 最终生成池v3：27组有序候选/Power状态/RNG与历史事件类型顺序、兄弟/live隔离；三类可变池一次解锁读取回退 | `e9dc2207bfe34624807e8d95a4b3ea70` Passed |
| 最终抽牌前缀v2：洗牌选择后继续变牌、延迟抽牌只消费一次、来源失效、完整状态/增量/兄弟隔离及洗牌次数/历史条目数、DOP1/2与取消/失败排空 | `edc5c7fac70244168f66a92e092179d5` Passed |
| 最终组合既有即时/抽牌后学习前缀 | `dcdc9b5ef80d4db8821f22ac351fba86`、`0f51aa3c657b45389efe733355815257` Passed |
| 最终完整蟹战A/F/F/A、轻场景A/C/C/A及C/A/A/C、专用短场景C/C及最初基线U/U | 全部严格oracle一致；不把原型速度或诊断时间当最终数字 |
| 最终正常Release、双端结构门禁 | Release 0警告/0错误；Bash与PowerShell均通过，`search_files=90`；结果写入JSON verification |

最小合同均用原生无人启动器、IRONCLAD、FUZZY_WURM_CRAWLER_WEAK、敌HP999、NoGC关闭、120秒上限、建局合同后停止。scenario-id分别为`TURN-START-GENERATION-CACHE`、`HAND-DRAW-SHUFFLE-CHOICE-REPLAY`、`END-TURN-CHOICE-REPLAY`与`ADAPTIVE-END-TURN-CHOICE-REPLAY`。新增生成池与前缀分别在对应源码定版后验证，合并时只重跑共享抽牌段相关既有合同并执行最终原始蟹战交互对照。初次测试编译的不存在GetHandCount调用及perf包装器返回码问题保留在报告，不计作通过；外部注册与CoverageCatalog分类未变，无全量门禁或可见Steam。

## 通用分配与重复工作优化（2026-09-14，未发布）

基线为上游 `b1674f8`，双方使用相同生成器完整预算/NoGC回退测试支持。固定输入、全部开局、政策、动作与路线分别对账；短搜探针数字不作为完整极高性能结论。完整样本、失败、源码阶段和限制见[本批报告](../performance/general-allocation-20260914.md)。

| 验证 | runId / 结果 |
| --- | --- |
| 九条RNG原生序列、完整状态、保留引用、父子/兄弟/多代及冷读取不物化 | `c665b52b8d2d4b55876c513477e19a2c` Passed |
| 通用抽牌后前缀发现、完整状态/历史/续用/RNG、DOP1/DOP2工作与动作、并发/取消/失败排空 | `65c8ad5d55414cbdaf4ef5696a81badc` Passed |
| 原 ToolsOfTheTrade 即时前缀及选牌回放合同 | `3d17b2c8b693488e8ca7e78255b7ceaf` Passed |
| 生成器固定/正常预算映射与既有建局合同 | `8ac328b1c2964837816806a03bb3a77c` Passed |
| 根牌/生成牌/Clone/多代Fork首次入场、父子隔离及污染增减 | `b5af79fada6a4797b22016c8d4d979c7` Passed，最终根共享实现 |
| 冻结跑局前缀身份/顺序、可变Power映射、卡牌变异和多代Fork | `bc8eb0fc6c1f43d9bcd98fbb3c259b6d` Passed，最终根共享实现 |
| 长期资源均匀/非均匀池旧实现对照、选中身份/顺序、共享祖先及全部排名恢复 | `152b0f1432a84433bf224c3afda896a7` Passed；保留原List遍历方式的最终候选 `50397d3aba5b4b9ea919f9a0f9bd7649` Passed |
| 最终候选十种完整VeryHigh开局，蟹战/静默女王追加交错复核 | 24次候选请求Passed；22次严格oracle相同，亡灵契约师女王两次总转移少1、动作/路线一致，排除严格同工作量提速；全部runId及差异见[结构化证据](../performance/general-allocation-20260914.json) |
| 最终正常Release、比较器单元测试、启动器语法与结构边界 | Release 0警告/0错误；比较器10项通过；Bash/PowerShell启动器语法通过；最终双端结构门禁通过，`search_files=90` |

保留两个夹具失败：`e0d1edede32e4300b67c9478cba1b1ff` 的敌人过早死亡，未覆盖前缀复用；改为敌HP999后覆盖。`94ba920946634fd0b3a1dafeeb37caa2` 缺少既有污染断言所需技能牌；加入DEFEND_IRONCLAD后覆盖。早期入场测试 `5527c75b597b43e59ba966c7e1f1c5a9` 通过，不能替代最终根共享合同。原120秒重场景内环未返回结果，作为超时保存；最终完整请求属于预先确定的独立测量层。

资源保路夹具先保留两次失败：`eea19a6790b349858d7d5294f99a2ba1` 暴露旧反射回放入口漏传两个新增可选参数，已同步真实签名；`cc7e15d2b9e942f2aabd9a3ad6498da0` 对零资源错误调用只接受正增量的领域方法，改为保留零初值后通过。两次均未执行到排名对照，不能算生产候选错误或通过。

复跑最小合同使用原生启动器、`--scenario-id` 对应 `LAZY-RNG-FORK`、`ADAPTIVE-END-TURN-CHOICE-REPLAY`、`END-TURN-CHOICE-REPLAY`、`POWER-AFFLICTION-ENTRY`、`FROZEN-ROOT-LISTENERS`、`LONG-TERM-RESOURCE-STAGING`。选牌/入场/资源合同用IRONCLAD、敌HP999；入场/冻结监听用清空战斗牌堆后加入手牌INFLAME与DEFEND_IRONCLAD。请求上限120秒，关闭NoGC，仅检查建局合同并停止；PowerShell使用对应PascalCase参数。生成器另用解析后的指定样本。Linux无头不证明可见FPS、Windows或完整自动部署。

## 在线监控：离线战绩身份（2026-09-14）

此服务记录已迁至独立私有仓库的 [历史卷](https://github.com/Torch1230/combatsolver-presence-service/blob/main/docs/archive/history-testing-202609.md)；原文与当时验证范围完整保留。

## 0.38.6 发布范围：格挡药路线直插与录像收录辅助 Mod 判定（2026-09-14）

- 格挡药路线直插：Release 隔离构建 0 警告、0 错误，Windows PowerShell 结构门禁通过（`search_files=91`），CoverageCatalog 取得 3035 项、0 未分析、0 待实现。新增 `BLOCK-POTION-ROUTE-INSERTION` 完整部署场景，断言 Smart 无药路线单回合战损达到 9 后直接插入格挡药、实际省血至少 9、T2 获胜且计划外重算为 0；本轮运行时因已有普通游戏进程占用宿主准入而未执行，不记为通过。

- `REPORT-V2-CONTRACT` / `7c9972982bc4425b929247e149e6c790` Passed，22.84 秒。新增合同证明录像浏览器、统计、QuickSL 等 `affects_gameplay=false` 辅助 Mod，以及统一登记的 Loadout/RNG 复现工具不会阻止收录；声明修改玩法的新角色和数值重制 Mod 仍被拒绝。既有问题包上传、取消和 TLS 合同同时通过。
- 最新实机日志确认 0.38.5 未上传的原因是旧逻辑把 23 个已加载 Mod 与两项 ID 白名单比较，在线统计实际开启，本地没有待上传包，服务端也没有收到请求。本轮不使用 Computer Use，不执行 Bash 门禁。

## 0.38.5 发布范围：Act 3 无伤 Boss 录像对局库（2026-09-14）

- CombatSolver 与私用录像 Mod 的 Release 构建通过，0 警告、0 错误；Windows PowerShell 结构门禁另记最终结果。按用户要求不执行 Bash 门禁，不启动可见 Steam。
- 日志后台 Python 3.12 隔离环境完整 64 项单元测试通过；测试进程退出后既有 `test_reports_v2` 临时 SQLite 句柄出现一次 Windows 清理告警，不影响测试退出码和断言结果。
- 服务端合同覆盖五文件白名单、文件摘要、客户端/路线结束回合一致性、从动作时间线重新计算结束回合及用药数、1/3/多回合、同根质量替换、每组前 100、只读鉴权、后台展示/下载/删除。
- 实机全职业/全原版第三幕 Boss 的精确导入与路线部署尚未运行；因此当前验证不宣称这些组合已逐项实机通过。

## 0.38.4 发布范围（2026-09-14）

- `HP-MODIFIER-COLLECTIONS` / `09f87bce86e74dc1b30f179d71e3dcd6` Passed：192 组 HP 修正集合合同保持；新增孤注一掷意图预测断言，非致命穿透伤害准确转为死亡并预测消耗一次蜥蜴尾巴，全额格挡保持安全，预测前后完整状态不变。
- `DEATH-SAVE-ORDERING-FINAL` / `015992ad11e34b3eacdd34d2f527cbbd` Passed：控制器生命周期与搜索合同通过；纯排序断言证明同为完整胜利时零复活路线压过血量和回合更优的复活路线，而复活胜利仍压过无复活的失败路线。最终战不再免除保命资源成本。
- `FAIRY-AUTOMATIC-RESCUE-DEATH-SAVE-FINAL` / `bb074141070441258c9f13191dabe520` Passed：1 HP 且只有瓶中精灵能存活的既有两回合场景仍自动复活并获胜，用药 1、计划外重算 0，证明新约束没有把万不得已的救命路线禁掉。三项均使用 Windows 隔离 headless；Release 构建 0 警告、0 错误，未启动可见 Steam。

## 0.38.3 发布范围（2026-09-14）

- 问题包弹窗正文回归：`UI-LOCALIZATION` / `dc7da1036cb0461698c9b75f073ffbd0` Passed；尺寸合同增加“滚动容器不参与自然高度时仍取得 320 px 默认高度”的断言，继续覆盖三种视口的总尺寸、拖动边界及 eng/zhs/zht 控件。Release 构建 0 警告、0 错误；未启动可见 Steam 做人工排版验收。
- 夸克打包版：直接调用统一发布脚本中的 `New-QuarkReleaseBundle`，以 `CombatSolver-0.38.2.zip` 为基础加入未解压的 `STS2 RitsuLib 0.5.20.zip`。临时外层包为 22,036,563 字节，严格超过 10 MiB；嵌套条目恰好一项，名称保持不变，条目原始长度 20,161,342 字节与前置文件一致。未执行上传、移动或发布。
- 失败窄搜移除：主搜索和 Smart 精确药水层直接使用原 profile，源码中不再存在 `NARROW_BEAM_RECOVERY`、`RecoverDeferredTurnFrontier` 或同回合落选前沿 fixture；请求级无胜利扩大搜索合同保留。Release 构建 0 警告、0 错误，PowerShell 结构门禁通过（`search_files=90`）；`NoVictoryRecoveryChecks` 最终通过 8 项请求合同及原策略断言，首次运行因检查工具仍引用已删除的旧 `Deep` profile 而未编译，改用当前 `Default` 后通过。按用户要求不执行 Bash 门禁。
- 药水批量预设：四种纯策略转换及设置序列化断言已进入控制器生命周期测试；Release 构建 0 警告、0 错误。完整控制器场景继续到既有 Smart 药水补查断言后失败，该失败不在本项批量预设路径，未记整场通过。
- `Ctrl+F9` 显隐：结构断言覆盖正确组合、错误功能键、键盘连发及隐藏后恢复原可见状态；输入节点独立于覆盖层。可见游戏未运行。
- 问题包弹窗：`UI-LOCALIZATION` / `8fcb860ea114408c8a476d5bdee69334` Passed；纯尺寸合同覆盖 1920×1080、1280×720、960×540，正文为纵向滚动容器且标题/按钮位于其外，中英/简繁控件合同通过；可见排版未运行。
- 原生选牌覆盖等待：`NATIVE-CHOICE-COVERED-WAIT-0383` / `fa7d612ef12e40e3a40f8e552d4f6a6a` Passed；纯状态合同覆盖预期页、其他覆盖层和真实缺失三态，10 秒真实缺失、60 秒遮挡、再 19.999 秒缺失不超时，累计真实缺失到 30 秒才超时；控制器生命周期与零损首回合搜索通过。工具箱下实际打开卡组未运行可见测试。
- 成长机会目标：`SEARCH-HP-TARGET-STOP` / `26431b92993144bd9ac1f7d1a0513ce1` Passed，23.49 秒；覆盖能力牌未打出实体、消耗牌未消耗实体、永久牌组实例、固定重放目标与逐次实际收益、黏糊强化、炼制药水不按空槽裁剪、致命来源竞争、动态重放和消耗回收。零损早停 1 节点、关闭后 4 节点，狩猎兑现后早停、强制/至少一瓶药水合同保持。前两次独立实例因默认实例持有独占租约而在主机准入阶段超时，未执行场景；随后按受管标记复用默认实例并通过。
- 第三方与额度：`GROWTH-POLICY-FREE-FIRST` / `c26a45b17e174cd7a6861188ed35d69f` Passed，21.48 秒；`GROWTH-POLICY-PAID` / `a9314d72f9cf43aaa162431515a90171` Passed，21.51 秒。覆盖旧登记不提供目标时保持完整搜索、可选计算器只读冻结快照、固定 Spiral 附魔重放、负次数与无效返回拒绝，以及零额度拒绝付血、足额额度取得成长、IgnoreLongTermRewards 清零。先行失败 `8d6dcdf7ca5e4c2ea3e01f76d9e2d3e8` 修正旧测试硬编码八个来源，`e203b483afae45918bea2a718a0c7b7a` 修正“成长存在即永不早停”的旧断言，`c8c16d90b32f4cd09258cf2ade57dc4f` 把额度排序与早停测试职责拆开；失败均未记通过。最终 Release 构建 0 警告、0 错误；可见 Steam 未运行。
