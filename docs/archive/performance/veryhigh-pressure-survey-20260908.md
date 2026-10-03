# 极高配置的其他战斗压力筛查（2026-09-08）

[性能索引](../../performance/README.md) · [完整指标、runId 与复跑参数](veryhigh-pressure-survey-20260908.json)

在 `56165ed` 上测试10组不同遭遇/牌组组合，另对其中两组测试允许深搜的正常配置，共12个有效请求。只用 VeryHigh、DOP8、NoGC16GB、headless；按用户要求跳过可见测试。本轮没有修改生产代码，也没有做优化前后 A/B。药水场景沿用夹具的`RequireAtLeastOne`（至少用一瓶药）政策，其余为Smart；“正常配置”指允许按原预设进入深搜，不表示所有选项均为玩家默认值。

## 最值得关注的压力

1. **死灵契约师的药水组合**：正常极高配置搜索58.53秒，累计分配35.81GB，独立进程峰值RSS16.85GB；636,428次转移中有431,140个选牌分支。发生3次NoGC区域重启，观测到单次GC暂停1,745.901ms，累计暂停1,813.060ms。它比只看搜索耗时更值得关注，下一步可先定位选牌候选/分支快照分配及回收暂停。该请求正常结束，但只有死亡路线，不能把Passed写成战斗获胜。
2. **2305张牌的极端牌堆**：50.21秒只展开277个节点、3,390次转移，分配12.38GB，约**3.65MB/转移**；普通药水组合约52KB/转移。压力更像单次转移的对象复制/快照成本，需要后续采样确认具体函数。20秒是软搜索预算：本次首条时间预算日志直到44.52秒才出现，最终50.21秒返回TimeLimit；不能把20秒设置误解成硬停止期限。整个请求94.68秒，仍在120秒以内。
3. **女王的生成/选牌组合**：短搜18.95秒，分配18.14GB、124,013个选牌分支，约114KB/转移。不过最终路线带`engine_risk=True`，只能作压力探针；应先核对未覆盖的生成牌语义，再将其用于质量或优化验收。

这是对这些固定根的判断，不是按怪物名字得出的通用难度排序。同一副30张牌的花园幽灵鳗仅需0.15秒；复杂度明显取决于牌组机制、选牌组合和当前状态。

## 短搜层统一筛查

只限制为极高预设原有的短搜层，不覆盖其20,000ms / 10,000节点、Beam54和45个动作分支参数；没有使用历史夹具的Low或2秒配置。请求总超时120秒，首结果即停止。表中工作量均为请求累计展开/转移/选择，可能包含多个求解器，不能只看选中solver的计数。

| 遭遇与输入 | 搜索秒 | 分配GB | 展开 / 转移 / 选择 | 搜索结果边界 |
|---|---:|---:|---:|---|
| 三敌骑士，63张合成随机攻击/防御 | 2.454 | 0.628 | 3,448 / 8,796 / 0 | 仅死亡路线 |
| 女王，64张合成生成/选牌 | 18.950 | 18.136 | 8,766 / 159,680 / 124,013 | 仅死亡路线；预测有风险 |
| 实验体 #C71，64张合成随机能力 | 2.000 | 2.663 | 6,419 / 28,977 / 0 | 预测战损30、T13 |
| 永世沙漏，63张合成随机生成 | 1.236 | 1.374 | 5,179 / 23,146 / 9,223 | 仅死亡路线；预测有风险 |
| 永世沙漏，38张战前牌/20遗物/2药 | 9.330 | 7.513 | 10,000 / 144,368 / 101,808 | NodeLimit，未完成胜利 |
| 机甲骑士，2305张极端牌堆 | 50.210 | 12.381 | 277 / 3,390 / 2,111 | TimeLimit，未完成胜利 |
| 感染棱柱，同一30张牌组 | 3.586 | 4.611 | 9,755 / 84,952 / 50,136 | 预测战损15、T8 |
| 花园幽灵鳗，同一30张牌组 | 0.152 | 0.228 | 791 / 5,002 / 683 | 预测零损、T2 |
| 灵魂枢纽，同一30张牌组 | 3.200 | 3.856 | 10,000 / 79,970 / 46,737 | NodeLimit；已有预测战损10/T12路线 |
| 外骨骼虫，同一30张牌组 | 1.470 | 2.034 | 3,337 / 34,556 / 17,903 | 预测零损、T7 |

最后四组是A10、第三幕的原生开局，使用上一轮机甲骑士夹具的同一30张静默猎手牌组，战前HP65；`preserveNativeCombatStateForTest=true` 保留原生敌人生命、行动、起手与开局效果。其余是明确注入的合成根。药水组合由旧白名单投影中的牌、遗物和药水重建，**不是原存档恢复**；既有起始遗物保留，另19件遗物按保存顺序加入且不重放获得效果。

短搜批次复用了进程、包含冷暖差异，不是逐场重复取中位数的严谨排名。筛查RSS包含前一场驻留页，例如实验体只分配2.66GB但进程峰值仍有12.45GB，不能据此认定该场独占12.45GB。所有原始采样峰值仍保留在结构化记录中。

## 正常极高配置，独立进程

各启动一个新headless进程，取消`forceShortSearchOnly`；保持极高原预算20,000/300,000ms、节点10,000/50,000、Beam54/135、分支45/72，以及每请求120秒上限。两项都在原期限内完成。

| 场景 | 搜索秒 | 分配GB | 峰值RSS GB | 实际阶段及结果 |
|---|---:|---:|---:|---|
| 死灵药水组合 | 58.531 | 35.810 | **16.845** | 触发Deep；46,239展开/636,428转移/431,140选择；仅死亡路线 |
| 灵魂枢纽 / 30张牌 | 10.948 | 7.856 | **9.473** | 返回Short，未触发Deep；20,938/165,030/86,268；预测战损6/T9/无药 |

runId分别为 `0f0f6eaee52b4967a39a771bc8026671`、`3635c085260f4d97b0254646ae93a284`。药水组合的阶段日志为Short32.219秒、Deep26.312秒；灵魂枢纽没有实际进入Deep，不能仅因允许深搜就声称它测试了Deep阶段。两场最终`engine_risk=False`、`modeled_damage_exact=True`。

RSS按100ms采样，可能漏掉更短尖峰；累计分配不是同时占用。GC暂停来自请求指标的观测值，不是独立EventPipe trace的全局最大暂停，也不是可见帧时间。单次冷进程样本不用于宣称相对短搜或上一轮优化的准确速度倍率。

## 输入修正与验证范围

- 旧 `search-performance-necrobinder-potion-heavy-run-snapshot.json` 仅含玩家白名单字段，缺少原生反序列化需要的角色身份。`screen-necro-potions` 在建局前以`ArgumentNullException`失败，未进入搜索，不计性能结果。本轮新增三个明确注入的文件：[牌](../../../coverage/unattended/search-performance-necrobinder-projected-run-cards.json)、[遗物](../../../coverage/unattended/search-performance-necrobinder-projected-relics.json)、[药水](../../../coverage/unattended/search-performance-necrobinder-projected-potions.json)。它们保留38张牌的升级/附魔、19件额外遗物顺序与两瓶药顺序。
- 四个遭遇的初次调用遗漏敌人HP参数，误用了runner默认1HP；这四条`screen-*`记录全部作废。有效数据只使用修正后的`native-*`请求，原生HP由`preserveNativeCombatStateForTest`保留。
- 12个有效请求均返回Passed，但这只表示协议与请求断言完成。未找到胜利或有预测风险的行已单独标出；其余战损也是搜索预测，未运行各场完整部署/原生逐动作差分，未证明优化前后质量等价。
- 未改生产代码，因此复用上一轮已构建的候选DLL，没有重复构建或全量门禁。测试数据的JSON、字段、数量、投影来源和文档链接另行验证。未使用增量/详细诊断模式的时间作为性能数字；测试进程已停止。

## 复跑示例

以下从仓库根执行。`<固定构建目录>`必须含同一版本的DLL和manifest，每次请求都显式指定。移除`--force-short-search-only`即可重跑正常配置；仍保持120秒总期限。

```bash
./tools/run-unattended-test.sh \
  --scenario-id VH-PRESSURE-NECRO-PROJECTED \
  --character-id NECROBINDER --seed SEARCH_PERF_NECROBINDER_POTION \
  --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2 \
  --enemy-current-hp 526 --initial-player-hp 41 --initial-player-max-hp 76 \
  --clear-run-deck \
  --run-cards-path coverage/unattended/search-performance-necrobinder-projected-run-cards.json \
  --relics-path coverage/unattended/search-performance-necrobinder-projected-relics.json \
  --potions-path coverage/unattended/search-performance-necrobinder-projected-potions.json \
  --cards-json '[]' --potion-policy-for-test RequireAtLeastOne \
  --performance-preset-for-test VeryHigh --search-max-degree-of-parallelism-for-test 8 \
  --enable-no-gc-region-for-test 1 --no-gc-region-budget-gigabytes-for-test 16 \
  --enable-detailed-diagnostic-logs-for-test 0 --force-short-search-only \
  --stop-after-initial-solver-result-assertion --timeout-seconds 120 \
  --combat-solver-build-dir '<固定构建目录>' --headless-instance pressure-survey --keep-game-open
```

30张牌组从 `coverage/unattended/performance-veryhigh-mecha-native.json` 的`runCards`字段提取为临时JSON数组，再传给`--run-cards-path`。四遭遇必须加`--preserve-native-combat-state-for-test --pre-combat-player-current-hp-override 65`，不能依赖默认敌人生命。全部12项展开后的参数见结构化记录；Bash/PowerShell入口分别使用现有GNU长参数/PascalCase等价项，Windows游戏本轮未运行。
