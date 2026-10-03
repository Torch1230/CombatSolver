# 重复牌剪枝收益探索（2026-09-29）

基线为本地 `upstream/main@72363308`。普通出牌已经对全部可打出牌使用卡牌指纹去重，普通选牌也已有组合去重；升级、消耗、变形、复制等持久选择补留至多两个实体代表。本轮没有扩大这套等价关系，也没有打开无序牌堆实验。

保留两项小改动：无重复选项时省去必然失败的实体代表构造；补齐费用修改的有效期与有序层次，避免将当前同费、未来不同费的卡牌合并。指标、失败/弃用测量和比较结果见[结构化证据](duplicate-choice-pruning-20260929.json)。

## 1. 没有采用的扩大剪枝

离线宿主通过 `OFFLINE_HARNESS_DUPLICATE_CHOICES=measure/skip` 在实体补留入口采样。`skip` 故意跳过复制和梦魇的实体补留，只用于估计收益空间，**没有安全性证明，不是生产开关**。没有更改生产分类、保路租约或预算。

| 场景 | 转移：原实现 → 跳过补留 | 转移减少 | worker 分配减少 |
|---|---:|---:|---:|
| 普通复制手牌 | 1124 → 1096 | 2.49% | 1.99% |
| 梦魇手牌 | 1122 → 1113 | 0.80% | 0.53% |
| 三张复制牌的密集手牌 | 2340 → 2147 | 8.25% | 5.17% |

三个短搜样本均找到零损胜利，但这不证明等价性或任意局面的质量。原件引用 `CloneOf`、附着模型、实例位置和有序变异保路仍需要额外证明。当前收益不足以承担扩大语义面的成本，生产未采用。

## 2. 保留的选牌优化

`BuildChoices` 原本已经在 `previousEqualIndex` 中计算全部候选的重复关系。新增局部布尔量复用这个结果；没有重复语义键时，跳过 `ReserveIdentityOccurrenceRepresentatives` 与后续实体代表重排。

安全性理由：候选语义键各不相同时，每个无重复索引的组合也有唯一的有序语义键序列。替换为同键实体的尾部代表必然就是原组合，因此补留结果必为空，重排也只能返回原语义代表顺序。既有可选张数、分支额度、分数排序、目标、实体令牌及有重复牌时的处理均保留。没有增加搜索状态、跨调用缓存或模型引用。

目标为十张不同语义卡牌的多选变形构造；固定 JIT 微基准与真实搜索分开计量。首次默认分层 JIT 微基准受前一阶段预热与晋级影响，重复牌对照出现不合理的分配差异，列入证据但不用于收益结论。后续固定 `DOTNET_TieredCompilation=0`，独立进程 ABBA，重复牌对照的分配一致。最终使用已入库的强类型探针，2000次构造均产出108000条选择，分配305.200 MB → 158.064 MB（减少48.21%），耗时285.91/285.52ms → 128.98/125.99ms（均值减少55.38%）。重复候选对照均产出98000条选择、分配100.624 MB；计时103.14–106.99ms，未见显著变化。

短搜目标使用 `duplicate-choice-pruning-purity-20260929.json`：DOP1、300 节点、Beam24、15秒软上限，ABBA 两对均为 3179 转移、1721 选择。worker 分配约 101.87 MB → 100.28 MB（减少 1.57%）。基线 1262.98/1264.82ms，候选 1243.52/1178.94ms；时间样本少且幅度不稳，不宣称稳定整搜提速。两对均比较 73 个非时序指标、完整路线（含选牌）、根续用文本与全部剪枝计数，全部一致。

该优化只有少量局部条件分支，无新增语义假设；因此即使整搜收益小，也值得保留。局部构造收益不能外推为整场或可见帧时间收益。

## 3. 费用身份修复

原版 `SetToFreeThisTurn` 与 `SetToFreeThisCombat` 对两张打击产生相同的当前费用，但原版回合末清理后分别恢复为1费和保持0费。旧出牌指纹与选牌键把它们判为相同。旧续用卡牌文本也只记录当前费用。

`Prediction/CardCostStateSupport` 统一向三处追加能量基础费用、有序能量修改列表（数值、绝对/相对类型、失效条件、仅降费标志），以及星能基础费用与有序临时覆盖列表。无任何修改列表时不追加数据，保留普通牌原来的键。继续使用分支卡牌现有状态和原版克隆/清理流程，不增加第二份费用状态，也不改变费用结算。

文本列表使用方括号中的逗号，不引入续用顶层分号，保留牌堆/卡牌定位诊断。此修复会让原本误合并的费用状态分别搜索，不以减少节点为目标；旧带费用修改的精确令牌/续用文本不应当跨构建复用。

## 4. 验证与复现

- 原生 `CARD-COST-IDENTITY-CONTRACT`：11组比较覆盖费用有效期、原版出牌/回合末清理、星能、被遮盖的基础费和永久层、修改顺序、仅降费、Fork隔离、真实选牌分支及live/predicted续用卡牌文本。新增断言核对差异仍定位至 `H[0]`；最终runId为 `269ec96177c641d0af43469241ed397d`。
- 原生 `CHOICE-COMBINATION-CONTRACT`：`1cc7c04da24d427b9c9cff7bb0f0080c`，1200组/5069条选择与独立旧枚举器逐项相同；该合同的附带旧微基准还包含历史优化，不作为本轮独立收益。
- 费用修复的普通与密集手牌控制：无相关费用修改时，根、完整路线、73个非时序指标和剪枝计数一致。它们用于正确性/开销检查，不宣称费用修复提速。
- 最终优化的密集重复牌哨兵：2340转移/461选择，完整路线、根文本、73个非时序指标与剪枝计数均与主线基线相同。
- Bash与PowerShell结构门禁通过（search_files=238）；Mod、离线宿主、Windows MemoryCleaner的Release编译通过；依赖审计源不可达产生NU1900警告。没有运行可见Steam、整场自动部署或Windows游戏；所有无头实例使用工作树目录并已清理。最终同源码DLL及manifest、Windows MemoryCleaner、两份许可文件已精确部署到已确认的本地游戏Mod目录；未提版、发包或启动游戏。

原生最小合同（Linux；另一项只替换scenario-id）：

```bash
./tools/testing/run-unattended-test.sh --scenario-id CARD-COST-IDENTITY-CONTRACT \
  --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK \
  --enemy-current-hp 999 --initial-player-hp 80 --initial-player-max-hp 80 \
  --relics-json '[]' --stop-after-combat-root-snapshot-assertion \
  --timeout-seconds 120 --cleanup-instance-on-exit
```

离线研究夹具位于 `coverage/fixtures/scenarios/choices/duplicate-choice-pruning-*-20260929.json`。主线构建、费用修复构建、最终构建分别保存后，用 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 选择同一对照的DLL，不把两项改动混成一个性能因素。

```bash
OFFLINE_HARNESS_DUPLICATE_CHOICES=measure \
dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request coverage/fixtures/scenarios/choices/duplicate-choice-pruning-dual-20260929.json \
  --out .local/duplicate-pruning/reproduce --label duplicate-choice \
  --nodes 300 --beam 24 --dop 1 --budget-ms 15000

DOTNET_TieredCompilation=0 OFFLINE_HARNESS_DUPLICATE_CHOICES=builders \
dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --out .local/duplicate-pruning/builders --label duplicate-builders --milestone M1
```

第二个入口只构造新牌并计量2000次选牌生成，不运行搜索，不更改live牌堆。输出 `duplicate-builder-probe.json`；分别切换费用修复基线与最终DLL进行ABBA，结果只代表选牌构造。
