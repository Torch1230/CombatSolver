# CombatSolver 覆盖材料

这里保存当前分类登记、测试证据、可复用输入和固定语料。目录生成证明源码登记与历史证据能够对账；运行证据仍以取得时的版本、场景和边界为准。

| 目录 | 内容与维护方式 |
| --- | --- |
| [catalog](catalog/) | `classifications.json` 手工分类；`generated/` 是 CoverageCatalog 替换生成的十份现行快照 |
| [evidence](evidence/) | `test-evidence.json` 保存证据等级、步骤、期望、实际结果与材料路径 |
| [fixtures](fixtures/) | 原生差分、生命周期、搜索、Runtime、UI 和已固定问题的最小输入 |
| [corpora](corpora/) | 当前策略代表根、排序和 GC 压力语料 |
| [archive](archive/README.md) | 已结束的等价对照报告，保留原始结论、失败与未验证项 |

fixtures 按卡牌、Power、遗物、药水、怪物、球、附魔、搜索、Runtime、UI、复现专题和第三方合同分组。已有主题先扩展同组输入，相关 cards、powers、relics 和快照一起维护。文件名描述机制或复现目标；新证据引用完整的仓库相对文件路径。历史通配符保留批次范围，CoverageCatalog 只消费明确文件引用，不把通配符扩展为新增通过证据。

## 检查与生成

```powershell
python -B tools/inspection/verify-coverage.py
dotnet run --project tools/inspection/CoverageCatalog -c Release -- . --verify-state-fields --verify-branch-state-reads
```

第二条命令读取分类与历史证据，替换 `catalog/generated/` 和 [覆盖报告](../docs/COMBAT_HOOK_COVERAGE.md)，不会重新执行游戏。主动效果运行证据等专项门禁按任务选用；当前报告仍保留未完成项。

CoverageCatalog 的 `--generate-simple-card-fixture`、`--generate-exact-card-fixture`、`--generate-simple-monster-move-fixture` 和 `--generate-state-mutation-fixtures` 将候选输入写到被忽略的 `.local/coverage-fixtures/`。完成验证后只将必要的可复用输入纳入 fixtures，并登记对应证据。

## 收纳规则

- 新增前核对现有 fixture、语料和证据，保留能覆盖独立机制或边界的最小材料。同一主题集中维护。
- 单次探针、待验证生成器输出、原始玩家包、整场日志、trace 和批次运行结果写入 `.local/`，任务结束清理。
- coverage 保存数据；运行器、比较器和检查工具由 tools 的职责目录维护。退役批次脚本从当前树删除，历史源码由 Git 保留。
- 移动材料同步证据、请求 JSON、脚本、skills 和文档引用。分类数、验证等级、通过状态和历史数字按证据维护；目录归并不提升验证等级。
- 历史摘要归档后冻结。仍被当前工作流使用的原始输入放 corpora；历史哈希描述当时文件，路径归并后的文件不冒充原始字节。
- 长期输入保留当前消费者或独立机制回归价值；覆盖目录仍读取的明确证据输入及其配套配置一起保留。批次已结束且失去消费者的输入、可重新生成的重复分片从当前树删除，历史报告用固定提交恢复原材料，不向 archive 复制一份库存。

玩家报告复现与验收方法见 [无人测试](../docs/HEADLESS_TESTING.md) 和 [适配验证](../docs/ADAPTATION_VERIFICATION.md)。
