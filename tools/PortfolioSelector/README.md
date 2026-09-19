# Beam 组合成员选择器（实验，默认关闭）

一个只在 CPU 上运行的小决策树，用来**跳过预计不会改善路线的追加搜索成员**。它不改搜索算法、
不改评分、不改终局排序；基线成员和最终路线比较都不经过它。没有模型、模型版本不匹配、输入超出
训练范围或策略不覆盖时，一律回退成今天的“全部成员都跑”。

目标是用户提出的口径：**决策质量不下降的前提下减少耗时**。因此工具链只在验证集上选“零遗漏改善”
的阈值；找不到这样的阈值就输出 `no_candidate`，不产生可用的模型。

## 组成

| 文件 | 作用 |
|---|---|
| `collect.py` | 生成场景与请求，用离线宿主批量采集“追加成员 + 真实政策标签” |
| `resplit.py` | 只按有可用标签的战斗重新划分 train/validation/test，并在每种遭遇类型内轮转，不看标签值 |
| `train.py` | 固定深度小树；只用 train 拟合，只用 validation 选阈值；从不打开 test |
| `evaluate.py` | 独立进程里 A/B：A 为现在的“成员全跑”，B 为选择器；按生产排序比较路线质量并统计耗时 |
| `test_train.py` | 训练工具的单元测试（划分、元数据一致性、保守回退、导出等价） |

生产侧只有两处：`src/Search/BeamPortfolioSelector.cs`（纯值模型与判定）和
`src/Search/CombatSearchCoordinator.cs` 里基线成员完成后的成员准入处；模型由
`src/Runtime/PortfolioSelectorRuntime.cs` 从环境变量 `COMBATSOLVER_PORTFOLIO_SELECTOR` 指定的 JSON 加载。
离线宿主用 `--observe-portfolio` 采集、用 `--portfolio-model <path>` 应用。

## 流水线

```bash
# 1. 采集（每条根一个独立进程，日志与产物留在工作区）
python3 tools/PortfolioSelector/collect.py --out <data> --repo . \
    --harness tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
    --seeds-per-combo 20 --workers 6 --nodes 120000 --budget-ms 60000 --timeout 420

# 2. 只按有可用标签的战斗分层划分
python3 tools/PortfolioSelector/resplit.py --manifest <data>/manifest.json \
    --runs <data>/runs --out <data>/manifest-stratified.json

# 3. 拟合小树并选阈值（status 必须是 selected 才有可用模型）
python3 tools/PortfolioSelector/train.py --manifest <data>/manifest-stratified.json \
    --runs <data>/runs --model <model>.json --report <report>.json

# 4. 独立场景上的端到端对照
python3 tools/PortfolioSelector/evaluate.py --manifest <data>/manifest-stratified.json \
    --requests <data>/requests --model <model>.json --out <compare> --repo . \
    --harness tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --split test
```

## 合同与安全规则

- **标签就是生产比较器**：`improved` 等于 `IsBetterPotionPolicyResult` 在“成员结果 vs 当前最佳”上的取值，
  直接沿用胜负、生存、复活消耗、偷取、战略战损、成长额度与回合的既有顺序，没有另立一套口径。
- **只用可比结果**：撞节点上限又没到终局的成员不参与质量比较，也不更新“当前最佳”，否则会污染后续标签。
- **跳过只能发生在原门控已经放行之后**；选择器不能放宽基线未耗尽、时间不足、节点不足或内存不足这四条。
- **保守回退**：`VersionMismatch`（求解器或游戏程序集 MVID 不一致）、`FeatureMismatch`、`InvalidFeature`、
  `OutsideTrainingRange` 任一命中即运行成员。计时与剩余预算字段不做范围约束，它们随负载漂移。
- **稀疏覆盖**：模型只见过采集用的角色、幕、遭遇类型与档位；这些之外的战斗不是拒绝条件，靠 `evaluate.py`
  在独立场景上量化。选择器默认关闭，且只在显式给出模型路径时才生效。

## 已知限制

- 采集只在无头离线宿主里进行；它证明不了可见 Steam 上的帧时间或卡顿，也不是正确性验收。
- 成员耗时是各自搜索的墙钟；跳过省下的是这部分时间，不等于端到端请求时间的等量下降。
- 训练与对照的数字只属于本文记录的那批场景、游戏版本与 RitsuLib 版本。
