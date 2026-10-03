# 循环质量、确定性回放与折叠展示（2026-09-21）

对应[原始调研](../research/loop-quality-performance-display-20260921.md)。本批不提升版本、不发包；收尾后提交中文 PR。基线 `3f4002bd`，A/B 两侧共享固定夹具入口；基线 Search 未改。逐次数据和完整路线比较结论见[结构化证据](loop-optimization-20260921-evidence.json)。

## 实现及质量边界

1. **有效格挡统一计价。** 威胁投影复用逐击已经算出的实际吸收格挡；会清空的溢出不再为循环出口、区域进展、ordered-mutation 进展和防守代表赚取正向收益。保留格挡走 `ShouldClearBlock` 和既有清空阻止者的规则；有固定保留上限的来源共用 `PersistentRelicSupport.BlockAfterPreventingClear` 的纯计算，跨回合执行也调用同一实现。未来保留价值仍以最大 HP 有界，格挡转伤害继续沿已有 offensive progress 计价。候选原始 Block、合法性、状态键及最终战损排序不变。
2. **额外的确定性击杀回放。** 新 `CombatBeamSolver.CycleReplay.cs` 在整个当前动作层完成、worker 全部排空后按原序探测。只在允许达标早停、成长/遗物/偷窃目标已满足、种子没有累计掉血或卖血、周期有真实敌方耐久下降且能量/星能不下降时启动。每一步重新检查当前牌实例、费用、目标和附加重放次数，使用原 `ReplayAction` 真正执行，保持完整父链和平坦 PlanAction。不同于直接复制结算数值，没有任何伤害、历史、RNG 外推。
   - 每一步必须只有一种可执行卡牌身份和一种目标；同状态重复牌复用普通展开的等价规则。出现不同可执行牌（包括终结牌）、多个目标、选牌、非法动作、形状漂移、跨回合、自伤或不确定结算，立即结束额外探测。普通路径和结束回合候选一直保留，只发布实际完成的胜利。手里仍有其他可执行杂牌的八牌循环通常不能进入这条加速路径。
   - 每个回合/形状区域在开始执行后至多一次；第一步前的替代出牌不登记 region，可以在更深层重试。开始回放后的非法、漂移或跨回合仍终止该区域的探测。
   - 4096 是**请求级**额外真实动作上限；Coordinator 的主搜、药水审计与组合共享 `SearchRequestWorkTotals`，独立 Evaluate 也最多 4096。回放计入 Transitions、不计入 Expanded；对外必须成对汇报。每步检查取消、时间和内存。估算只决定探测长度，不按保守估算提前拒绝；到达动作额度时，已验证的无损前缀加入普通候选队列，保留之前的出牌和 EndTurn 出口，并由普通搜索收尾。关闭达标早停不回放；仍有真正出牌分支、卖血、选择或证据不足时继续普通搜索。追加设计与数据见[请求预算和历史依赖收尾](loop-final-20260921.md)。
   - 不在父节点提交中触发，避免串行早停与并行已预约父节点造成额外工作量差异。早期实现确实观察到 98/101 展开差异，已撤回该触发位置；最终 DOP 数据见下表。
3. **显示层折叠。** snapshot 携带既有循环动作键，并比较全部显示值、ReplayCount 和本地化身份；只折叠显示完全相同的重复序列。主线程把最多 32 动作的周期显示一次，与“循环 ×N”标题一起包在一个外层圆角胶囊内，内部按可用宽度换行（玩家截图反馈后的追加修正，见测试矩阵）。当前回合、实时预览和未来路线统一经过同一渲染入口。真实动作数组和执行数不变，通过原区间与周期余数映射部署高亮；末击、遗物效果或选择变化不会被隐藏。胶囊及重复标签都参与语言刷新。16 行之外明确显示尚有 N 回合。
4. **状态与遥测。** 历史读者按实际依赖计数入键；根包含消耗堆，存在开放生成/变牌或第三方来源时保守保留六项，覆盖未来读者。循环停止细分为无收益、重复预算、family 深度、出口预算，加局部回合额度切层及回放尝试/动作/胜利计数，随 SolverResult 和无人指标传播。`TurnLayerBudgetStops` 仅计 `TURN_LAYER_BUDGET` 分支（局部时间份额或节点份额耗尽），追加 `TurnLayerTimeBudgetStops` / `TurnLayerNodeBudgetStops` 拆分原因，二者之和等于总数；同时耗尽时按原日志归 time。不计全局 `SEARCH_TIME_BUDGET`、用户接管、内存边界或其他节点上限退出，不能作为所有截断的总数。累计掉血原本就在转置支配标签，最终候选也不会在政策比较前按状态键压成一个；本批没有把累计掉血/回血直接加入战斗指纹，避免把策略历史当新游戏状态。此处不声称已证明所有中间保路的历史等价性。
5. **固定夹具测量入口。** 离线宿主现在接受没有 generatedScenarioPath 的普通固定夹具。共享 ScenarioBuilder 的初始注入方法，不抄一套模拟结算；快照恢复、追加怪物和自定义规则明确拒绝。仍只产测量数据，不自动执行 JSON 中的 expected 断言。搜索预设、预算、药水政策由宿主 CLI 指定，不能把 fixture 中的 `potionPolicyForTest` 当成已应用。

## 性能与不可退化对照

Linux .NET 9、同一固定根、Low、6000 节点、20 秒软预算、DOP1、默认 GC。2000 HP 隐藏相位循环启用 `--stop-at-zero-loss`。预先安排 A1/B1/B2/A2，所有样本完整 1200 个动作逐字段一致，战损 0、无药、T1 击杀；不是降低节点预算或减少实际出牌换来的收益。

| 指标 | A1 / A2 | B1 / B2 | 均值变化 |
|---|---:|---:|---:|
| 整个离线求解调用，秒 | 1.4672 / 1.4591 | 0.8468 / 0.8381 | -42.4% |
| Search 总计，毫秒 | 1258.9 / 1251.1 | 629.4 / 632.3 | -49.7% |
| 展开 | 1200 / 1200 | 6 / 6 | -99.5% |
| 真实转移 | 2400 / 2400 | 1206 / 1206 | -49.8% |
| Search 分配，字节 | 119147240 / 119133144 | 41940016 / 41944560 | -64.8% |
| 采样托管活对象峰值，字节 | 44343736 / 46752696 | 29564016 / 29651688 | -35.0% |
| 采样托管堆峰值，字节 | 41816352 / 41828224 | 25414880 / 25422904 | -39.2% |
| 工作集峰值，字节 | 250327040 / 250142720 | 229474304 / 229855232 | -8.2% |

内存采样间隔 100 ms，峰值是进程级采样值；Search 分配来自请求指标，不混用总进程启动分配。未运行可见 Steam，不外推 FPS、帧时间或可见性能收益。格挡保留上限的后续共用函数提取不改变以上无玩家格挡夹具的路径。

| 不可退化／确定性检查 | 结果 |
|---|---|
| 停滞格挡循环 A/B | 都是 10 展开、20 转移，战损 0、T1；完整路线相同 |
| 无战后回血的卖血哨兵 A/B | 都是 675 展开、1820 转移，3 HP、T2；完整路线相同；1.0162/1.0159 秒；分配 61995960/62011352 字节（+0.025%，15.4 KB） |
| 50 万 HP 成长循环 A/B，Low 60000 节点，RequireAtLeastOne | 都是 10555 展开、22248 转移，战损 0、T1；完整路线相同；5.3929/5.2784 秒；分配 756518888/753542344 字节。这是一对哨兵样本，不宣称小幅提速已建立 |
| 最终长循环 DOP1/DOP2 | 都是 6 展开、1206 转移、1194 个额外回放动作；完整路线、根续用戳及所有政策/剪枝计数相同，DOP2 最大父节点并发 2 |
| 分支根、关闭早停，DOP1/DOP2 | 都是 101 展开、256 转移，完整路线相同；DOP2 父节点及动作实际最大并发均 2 |

DOP 对照的并行调度计数以及 worker 局部 ThreatProjectionCache 条目数按设计不同，不写成“全部非时序数字相同”。以上是首批测量范围；后续扩展曾发现额外回放耗尽但未交付的退化反例（最新修复见追加报告），见[19 个边界场景及审计复核](loop-boundaries-20260921.md)。因此撤回覆盖所有循环的“未见明显退化”表述。展开减少率不能当作总工作量或提速比例，对外应成对列出 Expanded / Transitions，并同时给出实际时间和内存。

## 原生与 UI 验证

- `LOOP-DEFENSIVE-VALUE`：100→200 格挡在来袭已覆盖时不增加防守收益；Barricade 按原生 hook 保留远期价值；追加固定保留上限检查。最终 `50cfafd5acc642508e18840ab459f69a` Passed。
- `UI-LOCALIZATION`：`275d2dcafd214c62b675f5832a11993d` Passed，eng/zhs/zht；41 个真实动作显示为 4 个控件，保留额外重放、最后一次击杀、全区间高亮、复用和双语标签。
- `ROUTE-ROW-REUSE`：`5ec0d3f0b90c450b8c43a87b0cdaee25` Passed，完整显示身份、失败重试、语言往返、订阅释放、部署索引和原控件复用均通过。首次运行缺少 evidence-directory，行为合同已通过但证据写入失败；补齐测试输出目录后复跑通过，不是吞掉异常。
- `LOOP-REPLAY-DEPLOY`：`d4867a3cb9154340a85fe848d1139a56` Passed；40 HP、24 动作，原生完整部署、严格逐动作增量/全前缀续用戳核对、0 非预期重算；这份时间/分配不用于性能表。
- 独立 `tools/LoopDisplayChecks`：122505 个断言，随机序列逐项还原和执行索引覆盖；8 动作 ×200 + 非循环后缀保持映射。
- 所有原生测试都使用仓库内隔离实例、120 秒请求上限和 cleanup，启动器已报告实例删除。可见排版和动态意图投影与原生面板的一致性未验证。

## 对原调研证据的修正

- 2000 HP 夹具本次基线离线已能完成；旧的启动器 120 秒超时不足以证明 Search 撞上限。
- 原铁甲战士卖血夹具期望 3 HP/T2，但燃烧之血会补回 6 HP。当前基线与候选都选择 6 HP/T1，最终战略生命缺口 0，符合现有战后回血政策。原生也复现此旧断言失败；新增 `generic-loop-bloodletting-no-postcombat-heal-quality.json` 改用无战后回血的角色，保留同一牌组与可等待出口，离线 A/B 和原生都验证 3 HP/T2。未更改终局政策来凑旧断言。
- 成长循环在人为缩成 6000 节点时，两侧都先撞回合预算而失败；固定为 Low 的 60000 节点、明确 RequireAtLeastOne 后两侧都完成，不把这次预算修正写成候选收益。

## 重跑入口

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request coverage/unattended/generic-loop-long-damage-hidden-phase-v0111.json \
  --label loop --out .local/loop-check --profile Low --nodes 6000 \
  --budget-ms 20000 --dop 1 --stop-at-zero-loss
dotnet run --project tools/LoopDisplayChecks -c Release
./tools/run-unattended-test.sh --scenario-id LOOP-DEFENSIVE-VALUE \
  --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --cleanup-instance-on-exit
./tools/run-unattended-test.sh --scenario-id UI-LOCALIZATION --cleanup-instance-on-exit
```

`OFFLINE_HARNESS_COMBATSOLVER_DLL` 指向预先保存的基线 DLL 可做 A/B。`--verify-incremental` 仅用于小根严格回放，不能用于性能表；历史计数修复和格挡保路会改变有相关内容的搜索，不宣称任意未测战斗都逐位等价。
