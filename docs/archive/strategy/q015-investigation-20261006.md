# Q015 排查历史（2026-10-06）

冻结自提交 `057bb3c7`。以下为分阶段记录，失败与中途待办按当时状态保留；当前结论见[Q015 入口](../../issues/q015-route-quality.md)。

[Issue #222](https://github.com/Torch1230/CombatSolver/issues/222)，O066～O070 同一批次。贡献分支从主线 `9a4489d8`（manifest 0.50.0）开始；PR #213 已被主线合入。本记录保存本地验收证据，不自动关闭 Issue。原始任务预测见 [发布材料](../community/2026-10-05-worldlines/Q015.md)。

## 状态（2026-10-06）

| 条目 | 当前证据 | 状态 |
| --- | --- | --- |
| O068 海洋混混／储君 | 同开战根主线7／0；最终自主原生3／0／T4、零重算 | 折算追平；详细比较限制见下文 |
| O066 异蛙寄生虫／储君 | 同开战根主线36／0／T12；最终自主原生0／1异鱼之油／T6，实际41/75HP、零重算 | 折算9优于已原生验证的人工12；原T4根兼容验证通过 |
| O067 感染棱柱／静默猎手 | 同开战根主线12／1；最终自主原生7／1／T7、零重算 | 折算追平；详细比较限制见下文 |
| O069 灵魂枢纽／亡灵契约师 | 同开战根主线62／0；最终自主原生40／0／T13、零重算 | 折算少1；详细比较限制见下文 |
| O070 永世沙漏／故障机器人 | 同开战根主线36／0；最终自主原生20／0／T11、零重算 | 折算追平；详细比较限制见下文 |

## O068：候选产生，但破盾续路被淘汰

代表报告 `1b49315484e242cf8604815953f82a6d`，session `37c2b7a8552141f1aeae8735dc84fc36`，原版 0.111.0、A10、SEAPUNK_WEAK。原报告 8→3 的比较发生在 T2→T3，不直接算作本轮整场收益。本轮从开战 60/75 HP 恢复；开战和首次可操作点的完整 ContinuationStamp、原生二进制状态通过。辅助 Mod 清单存在差异，状态对账通过不代表所有 Mod 完全相同。

先从原包 `:7` 原生部署保存的获胜后缀：实际 3 战损、0 药、T4，搜索 0 次、重算 0 次。再将原记录前缀和获胜后缀合成 15 动作测试见证，逐步核对增量／完整回放。该派生包只存 `.local/`，原 ZIP 不改；它不是原包保存的完整开战预测，也不是自主发现证据。

只读整池诊断共 2791 事件、零丢失。窄成员宽度 36 的局部边界中有 80 个原始候选；倒数第二步原始排名 37，未进入最终保留集。必保代表置换后，原本较靠前的攻击候选也落选。防守估值偏好已经出防御的分支，而 T4 第一击只是消耗敌方格挡，敌人 HP 仍为 4，第二击才取胜。

O068 的独立改动只有一个因素：既有按药水数量分组的保路入口，在原名额内保留一个刚减少敌方 HP＋格挡、仍可合法出攻击的非终局候选。合法攻击数量从现有手牌合法性循环冻结，保路不读模拟器；它是启发式提示，不是可达击杀证明，不进入指纹或 ContinuationStamp。沿用既有进攻代表比较器，不改终局政策，不按卡名或遭遇特化。后续O067追加的两项独立因素及兼容性验证见下文。

## O068 验证与失败试验

所有正常样本同一原包 `start`、原 Beam 90／250000 节点上限／DOP4，固定 10000 ms，保留 Smart、成长、遗物、奖励预测及组合政策。没有使用原报告 180000 ms 的档位预算。请求上限 120 s，串行隔离运行并清理实例。

| 实验 | 正常预测：战损／药／回合 | 总展开／转移 | 正常总搜索 ms／分配 B | 结论 |
| --- | --- | --- | --- | --- |
| 主线 9a4489d8 基线 | 7／0／T4 | 7010／18362 | 2549.4631／620607688 | 当前独立基线 |
| 仅 HP 伤害＋剩余手牌价值 | 7／0／T4 | 7649／19887 | 2926.3534／663000744 | 失败，替换 |
| 保路时读取模拟器内合法攻击 | 7／0／T4 | 7007／18360 | 2670.1695／615626176 | 失败，撤去该读取 |
| 冻结攻击数，但只算 HP 进展 | 7／0／T4 | 7007／18360 | 2796.6852／611821056 | 失败，被格挡进展条件替换 |
| HP＋格挡进展及冻结合法攻击 | **3／0／T4** | 1858／4935 | 1025.6851／174835576 | 原生完整执行 57/75 HP，零重算 |

基线 runId `2130baa7463c49028dbfc6a3084a0877`；最终自主部署 `488511f0cd7348c5afdbdb3f60920c3b`。每项时间均为正常搜索，不含只读观察器或逐转移验证；本轮每版本一个独立进程样本，不能外推普遍提速或可见 Steam 帧时间。冻结基线 DLL 仅带通用 Testing 整池观察改动，其生产搜索与 9a4489d8 相同。

## 固定独立哨兵

沿用 Q002 的 SILENT／BYRDONIS_ELITE 生成根，冻结种子、牌组、升级、遗物及药水；输入见下方维护的 JSON。两次独立进程使用相同 Custom／Beam60／120000节点／固定30000 ms／DOP2／Smart／普通GC／无详细日志，原生开局均已捕获并对账。

| 版本 | 战损／药／结束回合 | 总展开／转移／选择分支 | 搜索 ms／分配 B | GC 总／最大暂停 ms |
| --- | --- | --- | --- | --- |
| 9a4489d8 生产基线 | 5／1／T4 | 17480／68036／12470 | 8819.4325／2425680760 | 206.968／25.729 |
| 攻击续路候选 | 5／1／T4 | 17587／67878／12297 | 8988.7631／2431347336 | 129.782／18.132 |

runId 分别为 `61f63b7118a04bb79db8074288fefc61`、`37331b22dcf24a90b00b33ed3d4fb7f8`，均 Passed。时间增加约 1.9%，分配增加约 0.23%，单对样本未见明显增加，但不能据此证明统计上的普遍性能不退化。两者在首个结果后停止，本轮未进行该哨兵整场原生部署；O068 的完整原生部署已通过。没有原档位长搜或可见 Steam 卡顿结论。

## O066：原比较根已在主线追平

报告 `488b952ad66f4c5dbed59264ae7361f2`，session `03b561868c6d4ceea599655def8da459`。原包 `:9`／cursor19／T4 的保存预测严格回放21动作并原生部署 Passed `3fc3be099c5d4db09cbb2de7a93a2896`：3战损、1异鱼之油、T9，原生38/75 HP，无额外搜索和重算。

同一 T4 根正常协调器对照，原 Medium profile60／120000节点／DOP4，仅固定为10000ms，Smart、原药水指令、零成长额度、奖励预测关闭均保持：

| 源码 | 预测战损／药／结束回合 | 展开／转移 | 搜索 ms／分配 B | 实际执行 |
| --- | --- | --- | --- | --- |
| 9a4489d8 主线 | 3／1／T9 | 6634／22903 | 4240.3057／1050745512 | 本次首个结果后停止；保存同根3损后缀已原生验证 |
| 攻击续路候选 | 3／1／T9 | 6610／22888 | 3911.7524／1049674136 | 38/75 HP，零计划外重算 |

正常主线 runId `fb08654c2348401ab21d83fcd8c27660`；候选自主部署 `feccc1a2cb774c3eb19e5038469a65ec`。本项按原报告的实际比较根已追平，不把该已有结果计为本次修复收益。

另从开战 `start` 比较固定10秒：主线36损／0药／T12（`42f90c670ce94611bf0393bdd7d32f53`，20591展开／79619转移／9905.6302ms／3697811576B）；候选17／0／T9（`de66e079e272430d867fd46a920391b5`，26249／96098／9921.7557ms／4394040680B）。候选因预期1药的断言 Failed，尚未原生部署，也未追平3损＋1药的折算12损目标。原日志还混有 startTurn1 的37动作旧计划与T4复用结果，不能拿旧17预测当成本轮开战短搜基线或混算区间；原档位120秒未验证，不扩大短搜预算继续调参。

## O067：弃牌续路与延迟伤害结束回合探测

报告 `cc243a922beb4965995c911f8ff9fb60`，session `3e88aff1627f423e8a26c4fb2fd9b652`。原包 `:5`／cursor36／T6 的6动作保存后缀已逐步核对增量／完整回放并原生执行（`42820d9838434ca9ab71b2d971cb2c71`）：整场7战损，原生49/77 HP，T7获胜。敏捷药水在录制的T1已经喝过，后缀0药不等于整场0药；整场共1瓶、零额外搜索和重算。

正式开战比较采用 `:3`／cursor0 的 Force 敏捷药指令，Swift 保持 Smart，使用[维护的政策输入](../../../coverage/fixtures/search/damaging-continuation-forced-dexterity-policy.json)；保留新颖性搜索开启、宽度组合开启、原成长额度和遗物目标，关闭奖励预测和提前探索，DOP4。14个非预算政策字段与原比较政策逐项一致。原 profile90／250000节点仅固定为10000ms；总搜索统计约26.47秒，包含既有能力组合成员，不能把 profile10000ms 宣称为整个请求10秒。

| 源码 | 正常预测：战损／整场药／回合 | 展开／转移 | 总搜索 ms／分配 B | 状态 |
| --- | --- | --- | --- | --- |
| 9a4489d8 主线 | 12／1／T7 | 68662／342674 | 26471.8835／15064986112 | SearchOnly Passed |
| 攻击续路候选 | 9／1／T7 | 70376／351837 | 26474.4649／15416907976 | 预期≤7断言 Failed；未原生部署 |

正常 runId `df5ba6553ad34f7ebe91c97000b26e39`、`aaf1ca3ec06d4a0fb8b8efb20ce338ff`。此前对新颖性开关不一致的判断经原包原文核对已撤销，没有因该误判重复正常搜索。

派生见证保留原始状态键、费用和选牌，只存 `.local/`：候选9损路线仅提前T2能力，严格结果仍9（30动作、T7、0展开，7损断言失败）；原保存12损路线仅把敏捷药从T4移至T1，严格结果8（29动作、T7、0展开，同样未达7）。按完整原生动作顺序及保存后缀重建的第三条见证严格回放31动作，7损、T7，增量／完整一致，303条观察事件无丢失（`37e46c7aae4c4c5ebc50e15e775e8823`）。派生见证不是自主发现或原生部署证据，不注入生产候选或评分。通用失败信息补充实际胜负、回合、战损、动作数和展开数，避免仅看到笼统的结局不匹配。

此前只读观察中，人工第17步（T4抽牌后防御）的等价状态进入候选和剪枝输入，未进入最终保留／展开；第18步未出现。此前部分具体动作被转置合并，后续等价状态仍存活，不能把首次具体动作丢失直接当作整条路线消失。

上一轮两次单因素尝试均未追平，已撤回：保留增加格挡、减少预计受击且仍有合法攻击的代表（`603a0f4283fc438aae5acc7fb504c3bc`，9／1／T7，65119展开／329536转移，29195.6575ms／14501949056B）；将合法攻击条件替换为现有可达手牌价值（`d00f854b7ea44cd1bf6322cc321c18fb`，9／1／T7，66524／333532，26367.6819ms／14653435712B）。两者均因≤7断言 Failed，未自主原生部署。上一轮约40分钟内未追平，先保留差2待处理项；本次在用户要求继续后取得下方的新证据。

### 2026-10-06 用户要求继续后的候选池定位

通用录制路线诊断增加显式观察步数入口，PowerShell/Bash 同步；默认仍是倒数第二步，不注入参照动作、不修改评分。原输入派生见证保持不变，只将观察点改为17。`57c7cc4d92c0453f92e8f5597864e819` Passed，1852观察事件、零丢失，完整31动作严格回放仍7损；诊断搜索不是正常性能或自主部署证据。

宽度135成员的边界25共有703原始候选。第17步等价状态原始排名47、父排名10，未获必保／选牌保留名额，最终落选。同一精确选牌上下文内，排名29的直接攻击／后续抽牌分支先获得选牌名额；它预计HP42，而目标防守续路预计HP49、实际HP49、能量2、可达手牌价值15。更宽的同一弃牌选项组混有刚完成选牌和已出一张牌的状态，不能仅以选项名认定续路已保留。

| 单因素替换实验 | 正常预测：战损／药／回合 | 展开／转移 | 正常总搜索 ms／分配 B | 结论 |
| --- | --- | --- | --- | --- |
| 每个选牌选项补一个防守首步代表 | 12／1／T7 | 68728／344139 | 20839.8557／15166874576 | `91138fcea2fd4f638d70b09cfbf52288`，≤7断言失败，替换 |
| 同一精确上下文内优先防守首步 | 12／1／T7 | 65821／329729 | 19337.0539／14669144096 | `b4d1f0927b624b4d8c2277d1d9509868`，≤7断言失败，替换 |
| 同一上下文内优先覆盖预计伤害并推进直接／延迟伤害 | 8／1／T7 | 67570／339454 | 20080.7921／15015636416 | `d6966fba364d4d2d9dfd13a70e5d1df6`，≤7断言仍失败；随后按明确的部分改进目标原生验收 |

第二个试验的第17步诊断 `6a8366c30be6401b8da1d345870273e4` Passed，1873事件、零丢失：目标排名47／711，选牌名额23、必保32、最终名额49，确实展开。随后第18步只进入剪枝输入；不能因第17步专用整池记录未出现第18步，断言其在全局排名前丢失。改用第18步观察后，`6f45daf0302d48d690e31a3efb3a4f5c` Passed，1415事件、零丢失：该延迟伤害状态在边界26排名189／482，同一上下文内没有获得保留名额。它预计HP49、能量0、延迟伤害17；先保留的直接攻击／后续抽牌分支预计HP同为49、延迟伤害10，但敌方HP更低且后续抽牌价值更高。第17步单独存活尚不足以兑现7损路线。两项试验均未原生部署，预算、名额、终局政策未扩大。

第三项只改变同一上下文内的代表优先级：已出牌、覆盖当前预计受击的非终局续路，依次按预计HP、已有直接／延迟进攻进展、可达手牌价值和既有进攻比较器排序；不增加选牌名额、不改变支配、状态键或终局政策。第18步诊断 `04f6bb6aeb254f02b52767889791192c` Passed，1500事件、零丢失：该状态排名215／465、选牌名额53、必保63、最终81，已展开，人工等价状态随后一直展开到第29步。按≤8、1药、完胜的独立部分改进目标原生验收 `9cfc5305351d4f10a8e5300fccbb4533` Passed：实际48/77HP、T7、零计划外重算，67609展开／339664转移／19932.9962ms／15033332960B。固定独立哨兵 `29b616e61d9647f7b8636950ce140079` Passed，保持5／1／T4，17589展开／67841转移／7019.9963ms／2431824120B；相对同根主线基线分配约增加0.25%，不同时间的单样本不证明普遍提速。这是当时的部分改进证据，不替代人工≤7目标；该宽选牌范围随后在O070失败，最终由下方限定弃牌范围版本取代。

第30步完整候选池诊断 `2adbee0a8fa643bd8c0562b0d9f7a7e3`：2478事件、零丢失；宽度135成员边界53共有954候选，目标排名221、父排名18、没有获得最终名额，也没有当回合选牌签名。目标实际HP49，但静态预计HP25、敌方HP35、延迟伤害32。相同用药数量的 Control 候选按静态预计HP取前8个，其预计HP均37；目标因此未进入既有结束回合试算。候选池另有实际HP49、敌方HP35、延迟伤害39的状态，同样未保留。冻结延迟伤害估值不是结束回合击杀证明，仍必须交给已有试算及实际终局验收。

在8损部分改进单独完成原生及哨兵验收后，再验证第二个独立因素：Control 结束回合探测的原8个名额中，保留一个冻结延迟伤害估值不低于敌方剩余HP的非终局候选，按已实现累计战损、当前HP、敌方HP及原分数选代表。候选只进入已有实际结束回合试算，不按估值直接宣告击杀；探测上限、Beam、profile、终局排序、状态等价及实际模拟语义不改。该因素不按报告、遭遇或卡名特化。

收窄选牌范围前的候选正常搜索及 Instant／0秒原生整场部署 `87eb7c9d4e2f4efcbf17572733c3356a` Passed：7／1敏捷药／T7，实际49/77HP、零计划外重算；68000展开／342086转移／20003.6511ms／15164090128B。本轮开始约05:12UTC，05:42UTC已取得O067原生达标结果；随后其他主题的复验暴露下方O070退步，不能据该单例及哨兵直接判定整批完成。

该候选固定独立哨兵 `5dfc8c3a36414a37880a83b1c208ec21` Passed，5／1／T4、17589展开／67841转移／7258.7316ms／2434927792B。主线正常基线8819.4325ms／2425680760B，分配约增加0.38%；8损中间版本7019.9963ms／2431824120B。不同时间单样本不证明普遍提速。

### O070 回归与弃牌范围纠正

完整批次复验暴露范围问题：宽选牌优先级＋延迟伤害探测在O070正常搜索 `0e3b221763d4460cb2d39cb8396f5947` 得到21／0／T10，88596展开／370660转移／22382.525ms／21928937128B，≤20断言 Failed，未原生部署。仅撤去探测因素后，宽选牌优先级 `3c528dd1ec2f468da013f462ab869732` 为44／0／T10，93480／375154／21003.3637ms／23050090392B，同样 Failed、未部署。两个失败版本均不作为最终兼容结果。

O070 的路线选择为 `MoveToHand`，属于弃牌堆取回手牌；O067 的定位依据为 `Discard`。将安全续路优先级限定为 `Discard`／`DiscardAndDraw`，取回及生成手牌继续沿用原候选代表顺序；这是选择效果边界，不按角色、卡名或遭遇特化。保留原8个名额内的延迟伤害探测因素。限定后的 O070 正常搜索和原生整场执行 `b6ab9489059a42209e2789f80b55b90d` Passed：20／0／T11，实际54/75HP、零重算，81957展开／333181转移／19018.5074ms／19719305456B。

最终限定范围的 O067 正常自主搜索和原生整场执行 `513d9628ffe644559a33ad0b695ca525` Passed：**7／1敏捷药／T7**、实际49/77HP、零重算，67961展开／340075转移／21162.3535ms／15119974416B。战损、用药及回合追平人工；较主线少5损，较仅攻击续路少2损。参照动作从未注入生产搜索。

最终固定独立哨兵 `416fcbad8199411eab0ea58894140ad6` Passed：5／1／T4，17589展开／67841转移／7119.7149ms／2444295768B；相对主线固定基线分配约增加0.77%，不同时间单样本未见明显耗时增加，不证明普遍提速。哨兵本轮仍为首结果验证，不宣称其完整原生部署或可见Steam帧时间通过。两个退步的宽范围版本只保留失败记录，最终生产采用限定弃牌范围的版本。

## O069：省药后的整场成本

报告 `c1185416b35446cfaa685e41f0876e50`，session `44abdd695aca454dab8065a93b82e0f0`。原包 `:13`／cursor33／T5 保存后缀严格回放13动作并原生部署 Passed（`45061c6bffb4483d954d8406098b72c1`）：整场32战损、1格挡药、T7，实际41/84 HP，零额外搜索和重算。与原无药预测46相比，省14血但多1药，净优势5。

本轮自主比较从 `start`，原High profile90／250000节点／DOP8，仅固定为10000ms；原Smart、零成长、奖励预测关闭、新颖性关闭、宽度组合开启均保持，非预算政策与原`:3`比较政策无差异。原比较`:3`发生在4条开局动作之后，不把原46预测当作本轮开战短搜基线。

| 源码 | 正常预测：战损／药／回合 | 展开／转移 | 总搜索 ms／分配 B | 状态 |
| --- | --- | --- | --- | --- |
| 9a4489d8 主线 | 62／0／T11 | 26770／123355 | 9917.1062／5398837040 | SearchOnly Passed |
| 攻击续路候选首次搜索 | 40／0／T13 | 25342／130359 | 9920.1305／5551688736 | 仅按≤32战损断言 Failed，未部署；按药水成本应比较41／0 |
| 攻击续路候选原生验收 | 40／0／T13 | 23052／118877 | 9956.2275／5096935872 | Passed；实际33/84HP、零重算 |

正常 runId `d9ae076c078c4bdcb6ed6f4ae9e03263`、`4a94a13cf8d14970aefcd0d62b292d01`；自主部署 `65e41f3c883847c0a2f245161b0c8534`。人工32＋1×9＝41，候选40＋0×9＝40，折算少1；不宣称裸战损追平32。原生验收以≤41战损、零药、完胜条件完成此前未执行的部署层，Instant／0秒、零计划外重算。无新增生产因素，仍是O068攻击续路改动。时间切片导致两次搜索工作量不同，不能宣称确定性工作量或普遍性能收益。原档位180秒与可见Steam帧时间未验证。

## O070：整场战损追平，晚一回合

报告 `acdd30c74a154131abb6c01e68340674`，session `745949047c544bb28d0edb785f42e0b5`。原包`:8`／cursor18／T3保存后缀严格回放63动作并原生执行 Passed（`6754c133daee4df6a2b701f90e27ffaa`）：整场20战损、零药、T10，实际54/75HP，零额外搜索和重算。战损含13自伤，不能仅比较怪物受击。

同开战根对照使用原Medium profile60／120000节点／DOP8，仅固定为10000ms；原Smart、零成长、新颖性关闭、宽度组合及首领HP政策保持。

| 源码 | 正常预测：战损／药／回合 | 展开／转移 | 总搜索 ms／分配 B | 状态 |
| --- | --- | --- | --- | --- |
| 9a4489d8 主线 | 36／0／T12 | 93538／378049 | 27870.08／22895372144 | SearchOnly Passed |
| 攻击续路候选 | 20／0／T11 | 80957／328267 | 26336.7511／19381488128 | 自主原生执行54/75HP，零重算 |

正常 runId `5fdc455e4b16491ebdddbc190d13fb23`、`7357a242a1f54baeb26b38f3aed590c7`。没有新增生产因素；O068攻击续路改动使本根少16战损，药水数量不变，整场战损追平人工20，但比人工T10晚一回合。总搜索包含既有组合成员，不能宣称请求总预算10秒；每版本单样本，不外推普遍性能收益。原档位120秒和可见Steam帧时间未验证。

## 限定弃牌范围版本的整批复验（追加窗口识别前）

下表五项均使用同一最终Release产物，正常自主搜索后以Instant／0秒原生执行完胜，计划外重算均为0。O066明确为原报告T4比较根，其余为开战根。旧宽选牌版本曾在O069取得31／0／T9，但未通过O070兼容验证，已经被取代；当前O069应报告40／0。

| 项目 | runId | 战损／药／回合 | 实际HP | 展开／转移 | 搜索ms／分配B |
| --- | --- | --- | --- | --- | --- |
| O066 T4 | `c1eed1afed7a491ab8d2efae5781aa4d` | 3／1异鱼之油／T9 | 38/75 | 6610／22888 | 3378.4922／1052992424 |
| O067 开战 | `513d9628ffe644559a33ad0b695ca525` | 7／1敏捷药／T7 | 49/77 | 67961／340075 | 21162.3535／15119974416 |
| O068 开战 | `a5fd0f12977347d9a891a9f55742d4cd` | 3／0／T4 | 57/75 | 1858／4935 | 788.5691／174702224 |
| O069 开战 | `5f4516610f0845a6b98aeea8cd0f0df6` | 40／0／T13 | 33/84 | 42606／208949 | 9941.2426／8910010832 |
| O070 开战 | `b6ab9489059a42209e2789f80b55b90d` | 20／0／T11 | 54/75 | 81957／333181 | 19018.5074／19719305456 |

O066首次复验在搜索开始前因启动进程不符合实例私有程序身份而失败（`4d8b8b85d6404a4faf68e425a7bf6d34`）；实例已清理，保留身份检查后换新实例取得上表结果。不是求解器质量失败，不推定未经证明的启动原因。最终哨兵见O067节；以上每版本单样本，固定时间切片内实际工作量不同，不宣称普遍性能收益。五个原比较点通过，不等于额外开战目标或原档位长搜已通过。

最终版本另测O066开战（`3449f77d7946432b87b231ce149176a9`）：7战损、1异鱼之油、T8，30623展开／109830转移／8434.6757ms／5048200976B。实际仅正常预测、未原生部署；单独的≤12裸战损断言Passed，但社区折算成本7＋9＝16，高于人工3＋9＝12，差4，应按质量目标Failed记录。开战与T4比较根均41/75HP，同持异鱼之油与复制药；原生记录到T4前未损血、未用药，不能用比较区间差异解释这4点缺口。

O066额外开战诊断从原生事件中核对前三回合15动作，加上T4实际君王之剑及已验证21动作后缀，生成37动作派生见证，只存`.local/`、原ZIP不改。君王之剑状态键取原预测中同状态实例，目标身份按原生T4记录修正；严格逐步增量／完整回放再次核对。`c4c9bfb18bbe4d899b5a0ef4377ad4c8` Passed：37动作、3损、1异鱼之油、T9、0展开，1044条只读观察无丢失，live/root未改；不是自主发现或这条完整派生路线的原生部署证据。

改用第17步观察，`75b7ed9f31d34bf782f083ebbb2a2e98` Passed，832条观察无丢失。第16步T4出剑的无药等价状态在宽度60成员保留并展开；第17步用药后的等价状态只在T1已用油的成员中出现，不应断言搜索实际枚举了人工T4用油顺序。该状态进入宽度60边界32的239个候选，排名137、父排名3，没有必保或最终名额。实际/预计HP41、敌75、能量1、持续增益26、可达手牌12，与排名136且获必保的代表相同，但完整状态键不同；目标累计敌方损血60、16动作，先保留代表50、15动作。人工具体动作顺序未注入搜索；此证据指向代表排序缺少历史进展区分，尚不证明任意改序都能改善完整请求。

仅替换同药水谱系、预计HP和敌HP相同后的同分顺序，优先累计敌方损血，再按原分数：正常请求`017bfa3aafea48149e2142f0ff066a17`仍7／1／T8，30746展开／110064转移／8691.7628ms／5042146472B，≤3断言Failed、未原生部署。该试验的第18步只读观察`b6725c37611a478a98483fab7f7ec8ab`Passed，768事件无丢失：第17步等价状态已保留／展开，第18步防御候选排名105却无必保／最终名额，后续未出现。局部保留改善未兑现整场质量，试验已撤回。

第18步同池同时有异蛙本体尚余1HP和本体已死、四个扭动虫合计75HP的状态。当前药水谱系代表在预计HP相同后先比较敌方剩余HP，因此优先尚未触发死亡增援的阶段；不能把这1HP当作整个遭遇只余1HP。现有模拟器正确结算死亡增援，冻结敌HP统计当前／复活中的敌人；未生成的主要敌人不计入当前HP。后续应定位跨增援阶段保路及弃置顺序的可兑现收益，不能将上述局部排序试验包装成语义修复，也不应为本包硬编码卡序或预计增援HP。

完整派生见证的原生部署层补验`e2995e4e2a79437f9d821da5c6f5b472`Passed：从41/75HP开战严格回放37动作、0展开，实际38/75HP、3战损、1异鱼之油在T4使用、T9完胜；0额外搜索、0计划外重算，未归因损血0。这证明人工整场12折算成本确实可执行，不是自主搜索达标证据。

此前补验误将场景名设为普通自主部署入口，`eb185e5063be4777ac7b9aca09e7f320`实际执行了当前自主7／1／T8路线，原生34/75HP、零重算；没有执行固定37动作，应按自主路线原生证据记录，折算成本仍16、未达人工12。派生包仅追加用于诊断的索引预测，原始开战状态、原生录制和政策输入不改；普通自主入口未读取或注入该参照路线。误设入口后，已使用明确的`CHECKPOINT-RECORDED-PLAN-DEPLOYMENT`完成上方独立人工验证，不将两者混算。

## 最终版本：死亡增援窗口与同源验收

O066在约06:04UTC开始额外开战排查，06:31UTC取得自主原生达标结果。失败的累计伤害同分试验已撤回；仅新增一个独立因素：冻结的AliveEnemyMask显示旧敌退出且新敌入场时，沿用既有RevivalWindow通道。原先仅判断复活数量或总HP变化，会漏掉击杀旧敌后增援使总HP上升的阶段。与此前7／1版本逐项核对，完整开战根ContinuationStamp及全部executedPolicy字段一致，政策差异为{}。识别不读模拟器，不新增通道名额，不预计未知增援HP，不改实际死亡／召唤语义、状态等价、预算或终局排序，不按报告、角色、卡名或遭遇特化。

同一最终Release程序集（SHA256 `7AB45F25ADCF5C5A02B4160A57E8EBCBB0E16252C3EDB033636ACBCFFF143CB7`）完成下表验收。五个主题均已从原始开战根自主搜索并原生执行完胜，零计划外重算；另保留O066原T4根兼容结果。固定哨兵仍为首结果验证，不宣称完整原生部署。

| 项目 | runId | 战损／药／回合 | 实际HP | 展开／转移 | 搜索ms／分配B |
| --- | --- | --- | --- | --- | --- |
| O066 开战 | `7e3d9c9ff0df468fada865f1aa33e5b4` | 0／1／T6 | 41/75 | 28791／103540 | 8387.9231／4767277264 |
| O066 T4 | `47029c7049664b59b8e81a50b6a945cb` | 3／1／T9 | 38/75 | 6610／22888 | 3591.7788／1054352448 |
| O067 开战 | `de73b24602e146858527361186b28789` | 7／1／T7 | 49/77 | 68008／342121 | 21164.1224／15145573792 |
| O068 开战 | `bda4c827af34449e94a680d16a4acc02` | 3／0／T4 | 57/75 | 1858／4935 | 869.8396／175002488 |
| O069 开战 | `08b7b882dcb64ed48640f4e5964cf4e5` | 40／0／T13 | 33/84 | 40584／201764 | 9942.3624／8587474376 |
| O070 开战 | `e8ebefd702364afabdf763bb7b3c5dd2` | 20／0／T11 | 54/75 | 81957／333181 | 19042.4303／19742803040 |
| 独立哨兵 | `f1ecad1682dd49b397489a5f4214fad2` | 5／1／T4 | 首结果；未完整部署 | 17589／67841 | 7475.2308／2435604896 |

O066开战自主路线28动作，T1使用1瓶异鱼之油，实际初始／最终均41/75HP，损血／回血／自伤／未归因损血均0。折算0＋9＝9，优于已原生验证的人工3＋9＝12，少3；旧7／1路线折算16的差距已解决。其他主题按药水机会成本比较；O070的晚一回合限制保留。全部使用原政策的固定短搜，没有原档位120／180秒或可见Steam帧时间结论。每版本单样本及时间切片工作量不构成普遍性能收益证明。

## 可重跑入口

### 命令

先从 [Q015 官方资料](https://github.com/Torch1230/CombatSolver/releases/download/community-tasks-2026-10-02/Q015.zip) 取得对应子包；`<O068.zip>` 等表示原始子包，不是临时派生见证。按本机情况补充游戏／Ritsu 路径。各命令对应下方已记录的实际验证范围。

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId Q015-O066-START-DEPLOY -CheckpointArchivePath <O066.zip> -CheckpointSelector start -ReplayMode DeploySolver -ReplayPolicyOverridePath coverage/fixtures/search/damaging-continuation-medium-replay-policy.json -ExpectedInitialProjectedBattleHpLostAtMost 3 -ExpectedInitialPotionCount 1 -ExpectedInitialFinalEnemyHpAtMost 0 -EvidenceDirectory .local/validation/q015/o066-start -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId Q015-O068-DEPLOY -CheckpointArchivePath <O068.zip> -CheckpointSelector start -ReplayMode DeploySolver -ReplayPolicyOverridePath coverage/fixtures/search/damaging-continuation-replay-policy.json -ExpectedInitialProjectedBattleHpLostAtMost 3 -ExpectedInitialPotionCount 0 -ExpectedInitialFinalEnemyHpAtMost 0 -EvidenceDirectory .local/validation/q015/o068 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId Q015-O066-T4-DEPLOY -CheckpointArchivePath <O066.zip> -CheckpointSelector 03b561868c6d4ceea599655def8da459:9 -ReplayMode DeploySolver -ReplayPolicyOverridePath coverage/fixtures/search/damaging-continuation-medium-replay-policy.json -ExpectedInitialProjectedBattleHpLostAtMost 3 -ExpectedInitialPotionCount 1 -ExpectedInitialFinalEnemyHpAtMost 0 -EvidenceDirectory .local/validation/q015/o066 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId Q015-O067-DEPLOY -CheckpointArchivePath <O067.zip> -CheckpointSelector start -ReplayMode DeploySolver -ReplayPolicyOverridePath coverage/fixtures/search/damaging-continuation-forced-dexterity-policy.json -ExpectedInitialProjectedBattleHpLostAtMost 7 -ExpectedInitialPotionCount 1 -ExpectedInitialFinalEnemyHpAtMost 0 -EvidenceDirectory .local/validation/q015/o067 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId Q015-O069-POTION-COST-DEPLOY -CheckpointArchivePath <O069.zip> -CheckpointSelector start -ReplayMode DeploySolver -ReplayPolicyOverridePath coverage/fixtures/search/damaging-continuation-replay-policy.json -ExpectedInitialProjectedBattleHpLostAtMost 41 -ExpectedInitialPotionCount 0 -ExpectedInitialFinalEnemyHpAtMost 0 -EvidenceDirectory .local/validation/q015/o069 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId Q015-O070-DEPLOY -CheckpointArchivePath <O070.zip> -CheckpointSelector start -ReplayMode DeploySolver -ReplayPolicyOverridePath coverage/fixtures/search/damaging-continuation-medium-replay-policy.json -ExpectedInitialProjectedBattleHpLostAtMost 20 -ExpectedInitialPotionCount 0 -ExpectedInitialFinalEnemyHpAtMost 0 -EvidenceDirectory .local/validation/q015/o070 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId Q015-DAMAGING-CONTINUATION-SENTINEL -GeneratedScenarioPath coverage/fixtures/search/damaging-continuation-sentinel.json -PerformancePresetForTest Custom -SearchBeamWidthForTest 60 -SearchMaxExpandedNodesForTest 120000 -SearchBudgetOverrideMilliseconds 30000 -FixedSearchBudget -SearchMaxDegreeOfParallelismForTest 2 -EnableNoGcRegionForTest 0 -EnableDetailedDiagnosticLogsForTest 0 -PotionPolicyForTest Smart -RuntimeProfile default -ExpectedInitialProjectedBattleHpLostAtMost 5 -ExpectedInitialPotionCount 1 -ExpectedInitialFinalEnemyHpAtMost 0 -StopAfterInitialSolverResultAssertion -EvidenceDirectory .local/validation/q015/sentinel -TimeoutSeconds 120 -CleanupInstanceOnExit
```

Linux 使用 `tools/testing/run-unattended-test.sh` 的对应 kebab-case 参数。维护的 JSON 输入不含个人路径、原始存档或玩家动作。
