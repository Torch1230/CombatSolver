# 怪物状态指纹的小集合快速路径（2026-09-22）

## 范围与调研

基线为本轮 fetch 得到的上游 `6922828d`（已包含 PR #123–125），任务分支
`perf/bounded-hotpath-20260922`。工作放在独立 worktree，原工作区未提交内容保留。

先核对[上一轮热路径报告](hot-path-fast-lanes-20260922.md)、当前架构、搜索性能技能及
`AppendMonsterStateFingerprint` 的调用链。上一轮已优化死亡指纹与监听位图；本轮不重新开启
跨 Fork COW、撤销执行器、排序政策或预算实验。Luna 并行复核了候选和验证入口。

唯一改动：怪物整数状态表在物化之后为空时直接返回，单项时使用原字典的值类型枚举器
追加原来的 `m / CombatId / name / value` 四项。两项以上仍用原
`OrderBy(CombatId).ThenBy(Name, Ordinal)`。没有新增缓存、长期字段或搜索配置。

单项序列天然有序，既有 null CombatId 的输出仍为 `uint.MaxValue`。字典空不意味着
KnownEnemies 无需物化，所以物化循环保留在快速路径之前。返回字符串仅为触发物化的
额外格式化也保留，以免扩大本轮审计面。

## 验证设计

- 直接调用生产私有方法，与原稳定排序和相同指纹写入比较；覆盖空表、单项、重复排序键、
  null ID、多项，以及父子字典写时复制。
- 串行独立进程 ABBA：Queen（目标单项状态）、双尾鼠（多项回退）、普通敌人（无怪物整数状态）。
  同一上游基线、游戏、种子、输入、Low / CLI beam 30 / 3000 节点 / DOP1 / Coordinator / Smart，
  搜索预算60秒、进程上限120秒，不开启额外组合。它是受控短搜，不代表完整生产预设。
- 比较所有非时序求解指标、完整路线 JSON、根与续用状态、剪枝计数、政策和实际工作量；
  时间边界或错误样本不列入等价或收益结论。
- 原生无头短搜使用严格增量回放，单独验收，不把该运行的耗时列为性能样本。

## 本轮结果

原始样本、冻结输入、命令与比较汇总见[结构化证据](monster-state-singleton-20260922.json)。
完整日志留在本工作区 `.local/monster-fingerprint/`，不纳入源码。SDK 9.0.120，Linux，
每次独立进程；实际机器信息记录在 JSON。没有修改 GC 或搜索预算。

### 正确性与工作量

- 基线与候选各通过 77 项[生产方法合同](../../tools/MonsterStateFingerprintChecks/README.md)。
  原排序作 oracle，比较两个64位指纹；多项重复排序键保持插入顺序，Fork 后修改与清空仍隔离。
- 三场各4次，共12次：全部 `Passed`、无时间截断。每场以 A1 分别对 A2/B1/B2，
  共9对768个字段全部一致；另对完整 `route.json`（包含嵌套选择）、全部剪枝计数和政策做精确比较，均相等。
- Queen：276展开 / 977转移，预测第2回合获胜、26 HP战损；双尾鼠：3000 / 10933，
  预测第7回合获胜、35 HP战损；普通敌人：151 / 383，预测第2回合获胜、4 HP战损。
  上述数值各次完全相同，未改善或牺牲决策质量。实际 Queen 成员按既有 Boss 政策使用 beam 45，
  其余为30；两侧完全一致，不将CLI宽度误作所有实际成员宽度。
- 原生短搜 `MONSTER-FINGERPRINT-QUEEN-NATIVE`：
  `runId=4d75ecf0b26c4f668f081472e3a0ff6e`，300节点、DOP1、
  `verifyIncrementalSearch=true`、首个结果停止，Passed。实例由启动器清理，日志记录
  `UNATTENDED_INSTANCE_REMOVED`。这是逐步完整回放对账，不是完整自动部署。
- Release 构建0警告/0错误；Bash结构门禁 `search_files=208`。两次只读审查未发现生产语义问题。

### 完整短搜成本

表中时间为请求级 `totalElapsedMilliseconds`；分配为 `totalWorkerAllocatedBytes`。
A、B分别为基线、候选，单场固定执行顺序 A1/B1/B2/A2。

| 场景 | A耗时 ms（两次） | B耗时 ms（两次） | 平均耗时变化 | 平均分配变化 |
|---|---|---|---:|---:|
| Queen 单项目标 | 704.83 / 710.18 | 689.13 / 693.04 | −2.32% | −1.31% |
| 双尾鼠多项回退 | 3468.44 / 3465.22 | 3397.20 / 3471.57 | −0.94% | +0.09% |
| 普通敌人无状态 | 461.24 / 482.80 | 467.35 / 485.19 | +0.90% | −0.08% |

Queen 搜索分配均值42.216 MB → 41.663 MB，减少0.554 MB；这是分配流量，不能当作驻留内存减少。
Queen 两个候选样本均更快，但每臂只有2个样本，速度结论仍限于这次短搜。
两个哨兵时间范围重叠：普通敌人同臂首尾漂移约4%，因此0.90%的差异不能建立真实退化。

| 场景 | A托管堆峰值 MB | B托管堆峰值 MB | 平均变化 | 工作集峰值均值变化 |
|---|---|---|---:|---:|
| Queen | 25.205 / 28.018 | 24.232 / 23.155 | −10.96% | +0.07% |
| 双尾鼠 | 40.038 / 41.452 | 43.067 / 41.738 | +4.07% | +0.85% |
| 普通敌人 | 23.718 / 23.255 | 22.501 / 23.736 | −1.57% | +0.12% |

峰值含初始化、受GC和采样时点影响。双尾鼠托管峰值均值增加约1.66 MB，不能隐藏；
其分配基本相同、工作集波动较小，没有据此认定长期保留集增大，也**不宣称总体峰值内存下降**。
所有样本与GC计数保留在JSON。

### 局部成本与取舍

隔离微基准用动态委托直接调用生产方法，空 `KnownEnemies`、500000次、先暖50000次，
每臂2次独立进程ABBA；仅此微基准设置 `DOTNET_TieredCompilation=0`。

| 字典条数 | A分配 B/调用 | B分配 B/调用 | A平均 ns/调用 | B平均 ns/调用 |
|---:|---:|---:|---:|---:|
| 0（非null空字典） | 312 | 40 | 70.32 | 37.02 |
| 1 | 632 | 40 | 166.12 | 39.89 |
| 4 | 760 | 760 | 320.17 | 332.23 |

完整方法仍有40 B剩余分配，包含保留的空 `KnownEnemies` 接口枚举成本；不把候选称为整个方法零分配。
单项每调用少592 B、局部耗时−75.99%；四项多约12.06 ns（+3.77%）、分配不变。
**接受的有限取舍是多项路径在该微基准中的局部耗时增加**，换取空/单项路径明确的分配和时间收益；
新增计数分支及JIT代码布局可能共同影响该差异，没有单独归因。
实际多项哨兵没有测出总体耗时退化，Queen 同时改善时间与分配，且所有测试路线一致，
因此保留这处14行改动；它不是普遍性能保证。

没有测量 Windows、可见Steam、完整生产预设或整场自动部署，不外推帧率、卡顿和全局决策质量。
不扩展到额外排序算法、格式化去除或新缓存。

## 复现

先分别保存上游与候选 Release DLL（`CopyModOnBuild=false`），构建离线宿主和合同工具。
从结构化证据还原3个输入：

```python
import json
from pathlib import Path
source = json.loads(Path("docs/performance/monster-state-singleton-20260922.json").read_text())
folder = Path(".local/monster-fingerprint/inputs")
folder.mkdir(parents=True, exist_ok=True)
for name, request in source["inputs"].items():
    (folder / f"{name}.json").write_text(json.dumps(request, indent=2))
```

每个场景按ABBA独立执行，替换DLL、请求名、label与输出目录；单进程上限120秒：

```bash
OFFLINE_HARNESS_COMBATSOLVER_DLL=/absolute/path/to/CombatSolver.dll timeout 120 \
  dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request .local/monster-fingerprint/inputs/queen.json --label A1-queen \
  --out .local/monster-fingerprint/runs/A1-queen --profile Low --nodes 3000 \
  --beam 30 --dop 1 --budget-ms 60000 --search-mode Coordinator --potion-policy Smart
```

用现有 `compare_results.py` 比较 A1 与 A2/B1/B2 前缀；额外精确比较路线JSON、
`pruneCounters` 和 `search-policy.json`。合同与微基准命令见工具README。
原生入口用 `run-unattended-test.sh`，对应JSON已保存关键请求参数；增加
`--fixed-search-budget --force-short-search-only --short-search-budget-override-milliseconds 10000
--enable-no-gc-region-for-test 0 --timeout-seconds 120 --cleanup-instance-on-exit`，
`--combat-solver-build-dir` 指向含候选DLL与manifest的独立目录。
