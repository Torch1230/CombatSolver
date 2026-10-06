# Q014 路线质量

[Issue #221](https://github.com/Torch1230/CombatSolver/issues/221)，O061–O065 同一批次。贡献分支从主线 `b2d23a05`（manifest 0.50.0）开始；原始任务见[发布材料](../archive/community/2026-10-05-worldlines/Q014.md)。本记录不自动关闭 Issue。

## 当前状态

| 主题 | 同开战根基线 | 当前证据 | 状态 |
| --- | --- | --- | --- |
| O061 储君／永世沙漏 | 45／2药／T9，原生零重算 | 人工27／0／T9严格回放通过 | 未追平，保留下一轮入口 |
| O062 铁甲战士／感染棱柱 | 13／1药／T5，原生零重算 | 参照7／1／T3原生通过；药水生成选项在PruneInput后丢失 | 未追平，保留下一轮入口 |
| O063 静默猎手／幻影园丁 | 首根恢复Failed | 缺Loadout空标记；模型编号表不同，原生二进制不可比较 | 保持严格失败；原环境材料缺口 |
| O064 静默猎手／骇鳗 | 29／0药／T11，原生零重算 | 最终自主原生14／1铁之心／T10，零重算；独立哨兵通过 | 短预算折算追平 |
| O065 故障机器人／瀑布巨人 | 5／0／T7，原生零重算 | 旧28动作当前预测和原生均8／0／T7，历史3不成立 | 当前环境无已证实的5→3质量缺口 |

## 恢复与比较范围

- 原包未修改，不主动适配内容 Mod。程序集列表不同只作诊断，结论以实际原生状态、完整续用戳、牌序与 RNG 对账为准。
- 主线新增 `max_hand_size` 后，O064 原包首个恢复失败：旧戳24字段，当前25字段，唯一新增为默认10。复用 Q015 的限定兼容：完整原生状态已严格相同时，才允许旧默认10补齐；拒绝非默认上限、重复字段及其他差异。
- 比较从明确的 `combat_start` / cursor 0 开始。保留各包的药水、成长、遗物、奖励、组合和并行度政策；profile 固定10000ms用于快速迭代。原包120／180／300秒政策未验证，不能声称原长预算验收完成。
- 每个 unattended 请求最多120秒；普通性能搜索不启用逐转移回放或路径观察器。完整原生部署使用Instant／0秒，断言计划外重算为0。药水折算每瓶9HP。

## O064 机制和证据

铁之心提供跨回合效果，却未列入既有开局药水类型。主搜索精炼耗尽余量后，开局药水后验只剩约半秒。把它加入登记，并按已经完成的基线实测成本，为现有普通药水层及开局后验保留余量；节点、时间、Beam和候选容量不增加。

另一个独立错误在审计截止路径：开局药水后验已找到并选中完整14HP胜利，后续候选截止时抛出取消，外层仍返回旧30HP结果。审计现在保存最新已完成且满足成本政策的结果，只处理本审计截止；用户取消继续传播，不接受未完成候选。

| 证据 | runId | 实际结果／用途 |
| --- | --- | --- |
| 主线首根恢复失败 | `6609671d820e47e6adbc63a9335d8a29` | 仅默认手牌上限字段差异；未进入搜索 |
| 限定兼容后的主线基线 | `eb65082a262b4bf0b3202fbfe9ccfd7d` | 自主原生29／0／T11，零重算，正常搜索9529ms、25269展开／90780转移 |
| 历史人工前缀＋合法后缀 | `895b66e84daa49c681742767de523eb8` | 原生14／1／T9；34动作增量／完整状态一致；只证明可行性 |
| 使用药水后的路径诊断 | `061d7355f1964e49993d042e7b9aad34` | 普通搜索能找到14HP；路径观察结果不作性能证据 |
| 留余量但未修截止 | `88f5a215a3d94f52a93f64878cce85bb` | 后验日志已选14／1，最终却返回30／0，直接失败证据 |
| 修正截止后的原生部署 | `1d46394ebf594a1aa8a05f2d095eec58` | 54→40/70HP，14／1／T10，零重算；9928ms、29203展开／106117转移 |
| 最终源码原生部署 | `ecb0584456aa439baca830d8922eeeda` | 54→40/70HP，14／1／T10，零重算；9935.7477ms、28620展开／104345转移、3768627424分配字节；最终DLL `BC2D1F223F42CA760D469BCE6ABA42683B69E609C89E46BC6905FAA390AD63A0` |

仅补登记、提前检查的两次试验仍返回29／0，未保留无效的提前增加成员实现。首次短政策含null `brightestFlameMaxHpLossLimit`，在政策解析处失败；显式文件删除缺值后通过。一次启动受沙箱对测试宿主锁访问限制，未进游戏；使用已授权的隔离测试权限后继续。构建NU1900为漏洞数据源不可达，构建本身零错误。

折算基线29，当前23，改善6；人工也是23，但T9胜利，当前T10。固定时间预算接近耗尽，不能凭总耗时接近宣称普遍提速；首次目标正常搜索分配3278889288→3828744768字节，目标质量改善但工作量、分配增加。

独立固定哨兵为 `coverage/fixtures/search/q014-persistent-potion-sentinel.json`：SILENT／BYRDONIS_ELITE，持力量药、格挡药；Beam60／120000节点／请求时间覆盖30000ms／DOP2／Smart／NoGC关闭。原Custom profile时间120000ms，实际请求覆盖为30000ms。基线 `35d355fedef443198fcc038cfc150a03` 与候选 `3115e70c137a42e6be5987fee8cea24e` 均原生5战损／1格挡药／T4，零重算；完整根、政策及最终动作序列相同。正常搜索7918.323→8125.1884ms（+2.61%），展开19858、转移75893均相同，分配2715568224→2734561176字节（+0.70%）。只有一对样本，不作普遍提速或可见FPS结论；后续仅改Testing诊断入口，没有改变这对哨兵覆盖的生产搜索行为。

## 其他主题直接证据

- O061：主线基线 `620cdc147b35431797fc09b589727b9d`，原生52→7/87HP、45损／2药／T9、零重算。人工前缀和25动作后缀 `b75b9dd47aae487db3eb0cf1153fda8e` 严格增量／完整等价、实际52→25/87HP、27损／0药／T9、零重算；只能证明可行性，未从开战自主找到。
- O062：主线基线 `ea63b671b11e43dbb8f15ec81ad7f967`，原生13损／1药／T5，零重算；正常搜索7879.623ms。参照 `7a6fcd4923a64958b12942cdbc3def2c`，原生7／1／T3，零重算，7后缀动作逐步增量／完整等价。按实际第一回合录制重新排序原始身份后，与该后缀组成13动作派生诊断，`1a653df50f8745bf8a5c64935adb9f30` 严格回放通过；原包未修改。352个只读路径事件无丢失：第1–6步进入保留并展开，第7步无色药生成VICIOUS选项已准入并进入PruneInput，未见PruneFinal／Expanded，后续步骤无观察。下一轮从生成药水选择的保路边界开始，不能硬编码此顺序。
- O065：主线基线 `f039543f267c49c2be45dbc4ff7c18a8`，原生5损／0药／T7，零重算；正常搜索5193.5343ms。
- O063：`add07521e4e546919d8fed95af98a556` Failed。首个表面差异是字段错位：原包在Powers前多 `loadout_summon_powers=empty`，惊惧7层等普通字段实际一致。原生模型编号表hash 4186358368→1568834832，`AssertNativeCheckpoint` 在不同编码时返回不可比较，不能误称原生字节相等。试验仅在原生字节已验证后允许既有空标记等价，`7aa7510ad4424f50948ec0ef863c7a9c`同样Failed；严格门槛生效，兼容试验已经撤回。没有加载Loadout、研究内容Mod或略过状态检查。本机安装环境未找到Loadout.dll；原环境重建及后续质量未验证。


O061：T5后缀路径诊断 fabf39122ccb4f0fa9ed85fab797c08e 自主搜索27／0，不能当成开战自主发现。把旧预测前22动作与原生参照后25动作拼接的派生诊断 df257a3c284a4ffd91a105f38ee921fe 在T5第23动作严格失败（君王之剑Damage32与25不同），夹具未通过；实际T3／T4原生录制顺序与旧预测不同，不能以旧预测替代原生前缀，也未去掉身份键。
O065：请求已不在归档检查点表中的:7时在Preflight失败，报告仍有该编号的预测。开战原始28动作在 `987853e022e84c1ea232b9451edd0580` 逐步严格回放合法，但当前预测8／0／T7与旧3／0／T7不符，普通模式保持Failed。显式当前结果诊断 `bcc36e64859142f786148642e55450db` 将未改写的28动作原生部署，62→54/75HP，8／0／T7，额外搜索和重算均0；当前模拟与原生一致。`recordedPredictionOutcomeMatched=false` 明确保留旧数字失败，诊断Passed不是旧3HP可行性的证明。当前自主5HP优于这条当前8HP参照，不计人为优化收益。

攻击续段保路单因素试验已撤回：O061 `7a52dcf94c224fc3a5a462e39f2fa848` 仍45／2，O062 `fcdc38d1e04f4abe8a8453535b1cfc47` 为22／0，折算与13／1相同。没有将无收益机制并入最终代码。补诊断日志的首次编译因插值引号错误失败，修正后Release零错误、3个NU1900警告。

## 重跑入口与未验证项

原生材料下载、预检和还原见[检查点指南](../CHECKPOINT_REPLAY.md)。五份 `coverage/fixtures/search/q014-o061-policy.json` 至 `q014-o065-policy.json` 保存本轮显式短政策。原包不提交，游戏及Ritsu路径由本机参数提供。目标示例：

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId Q014-O064-final-native -CheckpointArchivePath $o064OriginalZip -CheckpointSelector start -ReplayMode DeploySolver -ReplayPolicyOverridePath coverage/fixtures/search/q014-o064-policy.json -EnableNoGcRegionForTest 0 -EnableDetailedDiagnosticLogsForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId SPECIFIED-COMBAT-001 -GeneratedScenarioPath coverage/fixtures/search/q014-persistent-potion-sentinel.json -PerformancePresetForTest Custom -SearchBeamWidthForTest 60 -SearchMaxExpandedNodesForTest 120000 -SearchBudgetOverrideMilliseconds 30000 -FixedSearchBudget -SearchMaxDegreeOfParallelismForTest 2 -PotionPolicyForTest Smart -EnableNoGcRegionForTest 0 -EnableDetailedDiagnosticLogsForTest 0 -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -ExpectedUnexpectedReplansAtMost 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
```

基线是仅包含限定旧手牌上限兼容的 `ff3e275e`，主线行为源为 `b2d23a05`；原生材料及政策保持相同。最终源码只保留O064共享机制、限定旧报告兼容与通用预测差异诊断。本批不是五项全部修复：O061、O062未追平，O063环境恢复阻塞，O065历史目标无效；原120／180／300秒政策和正常可见Steam性能均未验证。O061从15:39开始、O062从15:41开始累计计时，单包40分钟未追平则结束本轮并留下一轮入口。未完成项保留同一批次入口，不能据此关闭Issue。

Release构建零错误、3个NU1900漏洞源不可达警告；结构门禁通过（252 Search文件），文档检查通过（481文件、1946链接），工具检查通过（293文件、37项目）。最终五个本地Mod文件已精确部署；没有提升版本、打包、推送或创建PR。

