# Q015 路线质量

[Issue #222](https://github.com/Torch1230/CombatSolver/issues/222)，O066～O070 同一批次。贡献分支从主线 `9a4489d8`（manifest 0.50.0）开始；PR #213 已被主线合入。本记录保存本地验收证据，不自动关闭 Issue。原始任务预测见 [发布材料](../archive/community/2026-10-05-worldlines/Q015.md)。

## 状态（2026-10-06）

| 条目 | 当前证据 | 状态 |
| --- | --- | --- |
| O068 海洋混混／储君 | 同开战根主线7／0；最终自主原生3／0／T4、零重算 | 折算追平；详细比较限制见下文 |
| O066 异蛙寄生虫／储君 | 同开战根主线36／0／T12；最终自主原生0／1异鱼之油／T6，实际41/75HP、零重算 | 折算9优于已原生验证的人工12；原T4根兼容验证通过 |
| O067 感染棱柱／静默猎手 | 同开战根主线12／1；最终自主原生7／1／T7、零重算 | 折算追平；详细比较限制见下文 |
| O069 灵魂枢纽／亡灵契约师 | 同开战根主线62／0；最终自主原生40／0／T13、零重算 | 折算少1；详细比较限制见下文 |
| O070 永世沙漏／故障机器人 | 同开战根主线36／0；最终自主原生20／0／T11、零重算 | 折算追平；详细比较限制见下文 |

## 机制与比较口径

- O068：既有药水数量分组名额内保留刚减少敌方HP＋格挡、仍有合法攻击的续路；合法攻击数沿既有手牌遍历冻结。
- O067：仅在Discard／DiscardAndDraw上下文保留预计安全且直接／延迟进展较好的续路；Control沿原8个结束回合探测名额保留一个延迟伤害可能收尾的代表，由实际回放决定胜负。
- O066：冻结AliveEnemyMask中旧敌退出、新敌入场的变化进入既有RevivalWindow通道。O069与O070验证相邻资源、选牌及长战斗路径。
- 以上均为中途保留启发式，不改战斗语义、状态等价、终局排序或预算，不增加Beam／通道／探测名额，不按报告、卡牌或遭遇特化。通用Testing观察步数参数同时维护PowerShell和Bash入口。

| 条目 | 同开战根主线战损／药／回合 | 人工完整参照战损／药／回合 | 最终折算成本（每药9HP） |
| --- | --- | --- | --- |
| O066 | 36／0／T12 | 3／1／T9 | 9，较人工12少3 |
| O067 | 12／1／T7 | 7／1／T7 | 16，与人工一致 |
| O068 | 7／0／T4 | 3／0／T4 | 3，与人工一致 |
| O069 | 62／0／T11 | 32／1／T7 | 40，较人工41少1；裸战损多8、晚6回合 |
| O070 | 36／0／T12 | 20／0／T10 | 20，与人工一致；晚1回合 |

原Issue数字为旧版本且部分来自不同中途比较根，不能当成本轮同开战根基线。开战恢复核对完整ContinuationStamp、原生二进制状态、牌序与RNG；保留原药水／成长／遗物／奖励／组合政策，仅profile固定10000ms。原有组合成员可使请求总搜索超过10秒；请求总超时120秒。普通性能样本未开启路径观察器或逐转移回放。分阶段原生人工参照、增量／完整等价、政策字段核对和失败试验保留在[排查历史](../archive/strategy/q015-investigation-20261006.md)。

## 固定独立哨兵

固定输入[BYRDONIS_ELITE／SILENT](../../coverage/fixtures/search/damaging-continuation-sentinel.json)来自已正确执行的Q002生成根，覆盖相邻攻击、弃牌、药水路径。种子、牌组、RNG、药水与成长政策固定；独立进程、Custom／Beam60／120000节点／固定30000ms／DOP2／Smart／普通GC／无详细日志。两端原生开局已捕获并对账，验证范围是首个完整预测，不是整场原生执行。

| 版本／runId | 战损／药／回合 | 展开／转移 | 总搜索ms／分配B |
| --- | --- | --- | --- |
| 主线9a4489d8／`61f63b7118a04bb79db8074288fefc61` | 5／1／T4 | 17480／68036 | 8819.4325／2425680760 |
| 最终Q015／`f1ecad1682dd49b397489a5f4214fad2` | 5／1／T4 | 17589／67841 | 7475.2308／2435604896 |

两端Passed、预测质量未退化，最终耗时未明显增加；分配增加约0.41%。每版仅一个独立正常样本，未建立统计波动范围，不能证明普遍性能不退化或提速；详细中间样本与GC记录见排查历史。没有Linux运行、原档位长搜、完整哨兵原生部署或可见Steam帧时间结论。

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

## PR前自检与主线合并（2026-10-06）

已合入main `a0b7cf0f`（PR #226），合并提交`d30f95c4`。开发记录的唯一文本冲突保留双方内容；Q015生产搜索与`057bb3c7`一致，新增主线UI／Runtime初始化来源独立标明。原五个主题证据继续复用；合并版最短O068原包开战整场补验`c885cebc3ca74a2eb761320f9ee188f0` Passed：3／0／T4，实际57/75HP，零计划外重算，831.787ms／174686272B，请求19.35秒。

固定哨兵另补一对正常独立进程原生整场验证。维护输入仅将mode从Search改为Deploy，牌组、种子、RNG、预算及政策字段均不变；双方完整rootContinuationStamp、原生开局JSON、executedPolicy及runtime字段逐项相等。CLR9.0.7、ServerGC开启、NoGC关闭、相同DOP2／无详细日志／冷进程；Instant／0秒部署。

| 版本／runId | 预测与实际战损／药／回合 | 实际HP／重算 | 展开／转移 | 搜索ms／分配B |
| --- | --- | --- | --- | --- |
| 9a4489d8生产基线／`d3a448e499ba47c2905aca0f610f5fbe` | 5／1／T4 | 51/70／0 | 17480／68036 | 8250.8126／2445527152 |
| 合并版d30f95c4／`abe046108c234fad8d8d6bff20aef6c0` | 5／1／T4 | 51/70／0 | 17589／67841 | 7998.459／2437694640 |

两端Passed，质量无退化；本对搜索耗时8.25→8.00秒、分配约减少0.32%，工作量相近。每版仅一个正常原生部署样本，不外推统计上的普遍提速／性能不退化。请求分别28.83／28.65秒，实例均清理。此补验完成该哨兵整场可执行性验证，不追溯修改前文首结果验证的历史范围。

合并版DLL SHA256 `7EC25A979A6F0AD26D4A13115C5B2D41EC011185F80EF169899A17D5BA0DB1C6`，最终五文件部署核对均一致。Release构建0编译错误、4条NU1900警告（NuGet漏洞数据服务无法连接；漏洞数据获取未验证）。结构、工具、文档、coverage及提交差异检查通过；失败记录保留：一次构建未完成时私有快照冻结拒绝启动，未进搜索；一次哨兵原生请求被夹具内Search模式覆盖，`70be332dd1ea45b4973f1d0d1aaeaa25`在搜索完成后因T4原生预期Failed，未执行整场。构建完成／使用明确Deploy模式后取得上方成功证据，没有绕过门禁。

验收范围仍为固定短搜；原档位120／180秒、Linux与可见Steam帧时间未验证。PR使用Refs #222，请维护者审阅范围，尚未推送或创建PR。

## PR #227提交时的主线更新与旧报告兼容

创建[PR #227](https://github.com/Torch1230/CombatSolver/pull/227)时main已推进至`b2d23a05`，新增PR #224的手牌上限续用字段／指纹；已以`6f702e43`合入并保留该修复，开发记录文本冲突保留双方内容。Q015自有Search保路改动保持，不能把本次上游状态键变化混称为此前同源构建。

新增字段使原始O068包首次恢复`7c3af4f4151e435aaedf168e098fc1d3` Failed：expected24／actual25，唯一新增字段max_hand_size=10，尚未进入搜索。补充Testing兼容提交`952778fd`：仅完整原生checkpoint已核验、旧期望缺字段且当前末尾唯一默认10时迁移缺失字段，所有旧字段严格比较；原始ZIP不改，Runtime／Search的现行手牌上限校验不改。无原生核验、非默认13／0、显式冲突、重复／错位及其他牌或RNG差异均拒绝。旧未记录非默认上限不作为已验证。

同源DLL SHA256 `E24F038A630A2D37A038C4511DF618F869193AFE257309F021830C09CB8973F4`完成以下必要补验，实例全部清理：

| 范围／runId | 结果 | 展开／转移 | 正常搜索ms／分配B |
| --- | --- | --- | --- |
| REPLAY-BOUNDARY-CONTRACT／`584dceb12637437fb6d5b39e813e4913` | Passed，默认字段须显式核验门禁；非默认、显式冲突、重复、位置、牌／RNG漂移拒绝 | 合同，不作搜索质量样本 | 请求18.68秒 |
| 原始O068开战／`4dddc0ccdab04f9497d28baa4509442c` | Passed，3／0／T4、57/75HP、零重算；continuation／native-state及legacyDefaultHandLimitVerified均true | 1858／4935 | 876.9917／175051304 |
| 独立哨兵整场／`319191f08dce419ca14845719a2d421f` | Passed，5／1／T4、51/70HP、零重算 | 17447／67690 | 7336.4416／2427558280 |

后两项完整开战续用文本仅移除末尾新增默认字段后，与上一阶段逐字相等，执行政策完整相等；新字段仍参与现行模拟／原生续用对账。其余Q015主题保留合入PR #224之前的同源整场证据，本阶段只复跑受影响旧包路径、严格负合同和独立哨兵；不声明五份全部在新手牌上限版本复测。最新哨兵7.34秒为单个正常样本，上文8.25→8.00秒成对数据仍属原阶段，不冒充最新main成对性能测试。Release构建0错误、3条NuGet漏洞数据联网警告，原档位长搜／Linux／可见Steam性能与旧非默认手牌上限仍未验证。

旧报告负合同复跑：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId REPLAY-BOUNDARY-CONTRACT -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit`；补充本机游戏／Ritsu路径。原包与哨兵整场命令沿用下方入口。

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

补验哨兵整场：在`.local/validation/q015/`复制维护输入并仅改mode，沿用上方哨兵的全部参数，移除首结果停止参数，增加零重算与最终HP断言。控制组另传`-CombatSolverBuildDir <9a4489d8独立构建目录>`，原始基线二进制不提交。

```powershell
New-Item -ItemType Directory -Force .local/validation/q015 | Out-Null
$q015Sentinel = Get-Content -Raw coverage/fixtures/search/damaging-continuation-sentinel.json | ConvertFrom-Json
$q015Sentinel.mode = 'Deploy'
$q015Sentinel | ConvertTo-Json -Depth 20 | Set-Content .local/validation/q015/sentinel-deploy.json -Encoding utf8
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId Q015-SENTINEL-DEPLOY -GeneratedScenarioPath .local/validation/q015/sentinel-deploy.json -PerformancePresetForTest Custom -SearchBeamWidthForTest 60 -SearchMaxExpandedNodesForTest 120000 -SearchBudgetOverrideMilliseconds 30000 -FixedSearchBudget -SearchMaxDegreeOfParallelismForTest 2 -EnableNoGcRegionForTest 0 -EnableDetailedDiagnosticLogsForTest 0 -PotionPolicyForTest Smart -RuntimeProfile default -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -ExpectedInitialProjectedBattleHpLostAtMost 5 -ExpectedInitialPotionCount 1 -ExpectedInitialFinalEnemyHpAtMost 0 -ExpectedUnexpectedReplansAtMost 0 -ExpectedFinishedTurnAtMost 4 -ExpectedFinishedPlayerHpAtLeast 51 -EvidenceDirectory .local/validation/q015/sentinel-deploy -TimeoutSeconds 120 -CleanupInstanceOnExit
```
