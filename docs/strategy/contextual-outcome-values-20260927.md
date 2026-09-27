# 角色与遗物上下文的结果估值研究

**未通过替代验收。生产手写评分尚未移除。** 本轮实现了可观测的分支上下文、非神经网络结果森林和可复现的采集/验证工具；没有启用玩家模式、增加玩家开关或加载实验模型。基线 `660dc802` 已合并上游 `8915a7c0`。结构化结果见[证据](contextual-outcome-values-20260927-evidence.json)。

## 为什么不能给每张牌一个固定分数

价值属于“在当前状态执行这个动作后的结果”。角色、费用、抽牌顺序、能力、敌方状态和遗物触发进度都会改变后续动作。模拟器原先已经结算支持的遗物效果，但旧学习原型仅有资源量、64桶卡牌/Power计数，缺少显式角色、遗物及其触发状态；不能因为模拟器正确就声称估值器已处理这些组合。

本轮 `SearchOutcomeContext` 消费捕获后的 `SimulatedCombatState`、分支牌堆和 `PredictionStateStore`：

- 角色身份；遗物持有/熔化、既有 stateful/mirror 状态的类别键、11类已有计数读取器和怀表当前/上回合计数。
- 各牌堆的卡牌身份、升级，手牌包含全局/局部修正的实际费用，关键词、附魔/负面附魔及类型资源量；抽牌堆前10个位置。
- 分目标 Power、敌人血量/格挡/存续，球队列顺序与被动/激发数值；快照的敌方总血量、预计玩家血量、回合和存活敌数。

这些是原始观测，不给遗物或卡牌指定效用加分。具名稀疏列代替小哈希桶，训练时临时展开为 float 列以加快拟合。既有遗物指纹读取器被复用，没有另写遗物结算规则。尚未穷举所有私有/第三方适配器状态、完整卡牌逐实例状态和敌方行动历史；**此表示不是用于证明状态等价的完整状态键**。

## 学习与搜索边界

`SearchOutcomeValueModel` 用已观察到的、完整且无风险胜利给祖先状态标记后续 HP 成本，取已见最小值。未搜索或被裁剪的状态不标记成失败。32棵随机回归树、最大深度8；特征 schema 3 和游戏 MVID 必须匹配。模型只在离线拟合，搜索期间冻结；最多8192个观测状态、4096项预测缓存，只保存字段与标量，不保存模拟器。条目界不是固定字节上限。

结果值只在实验路径排序中间节点，最终路线仍使用现有完整政策。实验跳过普通卡牌候选总分排序和显式选牌效用表，使用同一请求剩余预算逐步加宽，预测缓存可跨宽度使用。旧随机 rollout、原始多目标轮换和在线拟合原型已撤掉。未验证的实验严格限制为离线 Coordinator、DOP1、Disabled 药水，不能叠加玩家自动组合。

**这仍不是完整的无手写评分替代。** 现有部分搜索机制仍计算/消费启发式元数据；胜利条件标签有采样偏差，无法把未完成状态当作已知好坏；后续 HP 标签也窄于包含战后回血、首领折算和最大生命政策的最终目标。森林不自动解决这些问题。

## 训练成本

本机 Ryzen 7 7840H，16逻辑线程，系统内存约58.6 GiB；采集/拟合串行，无GPU、无神经网络或外部机器学习包。用户的半小时限制按每轮采集加拟合执行，不限制传统算法研究。

| 版本 | 根局面 | 拟合行数 | 采集加拟合 | 拟合含读入 | 模型 |
|---|---:|---:|---:|---:|---:|
| v18，小哈希桶 | 35 | 17,076 | 约292秒 | 3.12秒 | 336 KiB |
| v20，具名上下文 | 43 | 21,999 | 365.77秒 | 12.15秒 | 367 KiB |

v20 有3959列，其中189列与遗物有关。35个原训练根含五角色、随机遗物和策划机制，额外8个干预根改变笔尖、双截棍、音叉、异蛇头骨和数据磁盘。验证/test 根不进入拟合。每根最多抽取1024条已见胜利标签，根之间并非严格等权。v20 的二进制、命令、模型、数据和计时保存在 `.local/objective-search/v20/`，不提交大体积产物。

## 实际结果

15个小型成对夹具在五角色中复用同一手牌和种子，分别无笔尖、计数8、计数9。10个有笔尖的夹具均首回合零损获胜，顺序为：

| 计数 | 执行顺序 |
|---|---|
| 8 | 普通打击 → 双重打击 |
| 9 | 双重打击 → 普通打击 |

另有每角色7项、共35项上下文检查：Fork初值相等、live后续修改隔离、子分支计数可见且与父分支隔离、临时费用可见且隔离。它们是离线宿主合同；不是本轮原生 actual/simulated 差分。

这证明实验搜索可以在这些局面中随遗物进度改变动作。**不证明森林独自理解笔尖**：当前森林未用笔尖计数分裂，这些结果也依赖准确模拟及搜索保留候选。五角色使用同一注入手牌，不是五套角色牌组的全面泛化验收。

v18 的独立15根验证中，旧算法14胜，候选10胜，出现4个胜利丢失。v20补充上下文后只跑已知的三项开发回归；此前已观察这些根，因此不称新的独立最终测试。相同配置为 Beam24、12,000节点、30秒软预算、DOP1、禁用主动药水；旧协调器审计可能超过配置节点，实际工作记录在证据中。

| 开发回归 | 原自动搜索 → 候选 | 耗时（秒） | 进程峰值RSS（MiB） |
|---|---|---:|---:|
| 攻防取舍 | 胜利、扣17 HP → 未获胜 | 5.16 → 6.00 | 252.8 → 316.8 |
| 集中投资 | 胜利、扣2 HP → 同质量 | 5.99 → 6.82 | 254.8 → 304.6 |
| 随机静默普通战 | 胜利、扣29 HP → 扣34 HP | 12.95 → 11.86 | 336.1 → 346.7 |

没有整体提速或节省内存的结论。模型文件很小也不代表推断便宜：上下文构造及不同搜索路径增加了分配/峰值。出现胜利丢失后停止扩展质量验收，不默认启用，不拿局部成功抵消整场退化。

## 复现

先构建 Release 主项目和离线宿主（`CopyModOnBuild=false`）。

```sh
python3 tools/ContextualOrdering/generate.py --out .local/context-corpus --seed-namespace outcome-values-20260927-a
python3 tools/OutcomeValuation/augment_context_roots.py --manifest .local/context-corpus/manifest.json --out .local/context-interventions
python3 tools/OutcomeValuation/train.py --manifest .local/context-interventions/manifest.json --harness tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --mod .godot/mono/temp/bin/Release/CombatSolver.dll --out .local/context-training --seconds 1800
python3 tools/OutcomeValuation/relic_pairs.py --out .local/context-pairs
```

普通采集使用 `--collect-outcome-values`；拟合入口为 `--fit-outcome-values <training-inputs.json> <model.json>`。候选请求使用 `--objective-search --outcome-value-model <model.json> --search-mode Coordinator --potion-policy Disabled --dop 1`，不得叠加旧组合开关。在计数8的成对夹具追加 `--verify-outcome-context` 检查所有权。完整实测命令在各 `.local/objective-search/v20/*/command.json`。初始输入与场景生成来源相同即可复现；本轮证据来自计时脚本，整理后的通用 `train.py` 尚未整轮重跑。

Release 主项目/宿主通过；Bash、PowerShell结构门禁通过（221个 Search 文件）。未跑本轮原生差分、可见Steam或完整部署战斗。已精确部署同源码 Mod（manifest、DLL、Windows MemoryCleaner及两份许可），没有启动游戏。未提升版本、发包或推送。下一步需要解决结果标签/未完成状态与剪枝分布失配，不能继续把“多加一些身份列”等同于完成评分替代。
