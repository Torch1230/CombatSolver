# BeamOrderingAudit

读取已保存的搜索结果，分析中途排序与终局质量之间的偏差。工具输出观测和候选对照，实际搜索与战斗语义由对应宿主负责。

```bash
python tools/search/BeamOrderingAudit/test_monotonicity.py
python tools/search/BeamOrderingAudit/monotonicity.py --runs <runs目录> --out <报告.json>
```

`portfolio_ablation_ab.py` 用于已有结果的组合成员 A/B 分析，参数以 `--help` 为准。历史实验、失败样本和候选键论证见 [归档报告](../../../docs/archive/strategy/beam-ordering-audit-20261003.md)。
