# 局面排序实验（开发中）

用户目标：不依赖玩家问题包，自行构造多种局面，改善通用排序、误剪与高压搜索成本；能力牌只是其中一类。
分支 `feat/contextual-search-ordering-20260922`，基线 `2a4b1a45`。当前默认生产排序不变；可选实验模型只由离线宿主显式注入，没有模型上线。

## 验收边界

- 不把加宽或提高预算当作优化。对照固定同根、相同预算和政策，展开/转移成对报告。
- 未完成结果、墙钟切层、模拟失败不能伪造成有效的“更优”标签。
- 模型预测不是可采纳下界，不得用于声称安全的硬剪枝。
- 药水政策与必要目标不得绕过。按玩家已开启的战损目标结束追加组合属于有损取舍，必须单独量化，不把正则、范围检查或版本回退当作质量证明。
- 最终要覆盖独立生成战斗、定向反例、真实 Coordinator 与原生执行。无头不证明可见帧率。
- 不训练或调参于 test。压力变体保持同一划分；当前是按种子分组的留存，额外随机构筑用于检查跨牌组泛化，不宣称已按机制族完全隔离。

## 当前工具

`generate.py --out <new-directory>` 生成 105 根：10 种定向机制 × 3 个种子划分 × 2 种压力，以及 5 角色 × 3 遭遇类型 × 3 划分的独立随机构筑。没有标注“最优路线”；牌、资源、敌人力量都走原生建局注入。产物包含精确输入，目录必须新建。

`--seed-namespace <frozen-name>` 可建立新 RNG 批次；默认值保持原语料的种子。新种子仍属相同机制族，不能宣称机制独立；manifest 记录命名空间，划分和具体请求一起冻结。

`generate.py --suite target-stop-boundaries --out <new-directory>` 单独构造7个回血反例边界，交叉手牌/待抽回血牌和再生效果，并检查立即斩杀与延后回血、必要用药的取舍；用于检查达到零战损后可能放弃的回血，不混入泛化质量分母。

`run.py --manifest <manifest.json> --variants <variants.json> --out <new-directory>` 串行跑训练集。每个进程 120 秒上限，墙钟切层保留为 TimeLimited，错误不被跳过或纳入成功均值。`--split validation/test` 显式选择留存集。每个 variant 指定 `name`、`dll`，可附加 `beam`、`nodes`、`arguments`；同根所有变体相邻运行。

离线宿主新增 `--ordering baseline|base|band`，只复用原有成员排序。非基线仅限 Evaluate，实际配置写入 search-policy.json。`--observe-ordering N` 用原有 SearchPathObserver 有界导出真实剪枝池及完整动作身份；不得用这类采集运行作性能样本。被剪分支没有后续标签，不能自动当负样本。

`OfflineSearchHarness --compare-quality-batch <pairs.json> <output.json>` 从 `quality.json` 调用**实际生产协调器**比较器，避免 Python 简化口径漏掉成长、药水、偷取或 Score 末位。pairs 为 `{id,candidate,baseline}` 数组，路径指向各自保存的 quality.json。时间、根身份、预算可比性由上层批次检查，不由这个纯值比较器替代。

## 进度与失败记录

- 首轮 v1 建局试跑 20 根：16 成功、4 失败。两张旧作卡名 CATALYST/CLEAVE 不存在；enemyCurrentHp 被原生最大生命截断，定向根偏易。修正为当前有效卡、同时注入最大/当前生命和真实敌方力量，生成独立 v2 输入，v1 结果完整保留，不充当质量证据。
- v2 已完成 35 个训练根、105 次观察，101 Comparable / 4 TimeLimited；baseline/base/band 固定 20000 节点、beam24、60秒墙钟窗口、DOP1，正常零损达标早停启用。所有实验附加回放仍遵守请求级工作量口径。
- 已完成首次误剪/别名诊断、Evaluate独立留存和Coordinator完整test；尚待有明确收益的实现、更充分的候选见证及获准候选的原生验收。当前工具完成不等于用户目标完成。用户允许少数局面退化换取整体收益；仍须分别报告频率、损失幅度、胜负翻转和计算代价。

## 拟合与比较

- `prepare_pairs.py --manifest <manifest> --runs <observed-runs> --out <new-dir> --harness <candidate-host>`：只从训练根、相同根戳/预算/政策和完整后续配对；调用实际终局比较器，排除旧 Score 尾键差异，未知保留在 coverage。
- `OfflineSearchHarness --ranking-schema <new-json>`：导出当前 Mod/Game MVID 与特征 schema。
- `fit.py --pairs <pairs.json> --schema <schema.json> --out <new-dir> [--feature hand]`：零残差起步、按根均衡的有界岭正则逻辑损失。只拟合已观察到的后续；不声称训练集或最终战斗最优。默认 cap=2 HP、ridge=.05。仅 `--ranking-model <model.json>` 显式启用，正常 Runtime 不加载模型。
- `screen_features.py --pairs <pairs.json> --out <new-json>`：训练内部 leave-family-out 的单特征代理筛选；**不是**最终搜索质量敏感度或留存集验收。
- `check_model.py --runs <observed-runs> --out <new-dir> --harness <candidate-host>`：真实/边界输入的 Python/C# 特征对账与运行时模型合同。
- `compare.py --baseline <runs> --candidate <runs> --candidate-variant <name> --out <new-dir> --harness <candidate-host>`：相同根、预算和非排序政策检查后调用生产终局比较器；完整政策与去末位 Score 的实质政策分开报告。time-limited、失败和缺对不进入可比样本。报告同时保留完整quality值，单列胜负翻转及双方都胜利时的总战损/政策折算战损变化，不能把死亡简化为多损失若干HP。`comparison=-1/0/1` 分别表示候选更好/相同/更差；这些聚合不自动决定上线。

variant 可指定自己的 `harness`。旧 DLL 需要兼容的旧宿主；引用新增 profile 成员的候选宿主不能直接假定兼容旧 DLL。模型与当前程序集绑定，修改行为代码并重编后须重新生成候选并验证，不能直接修改 MVID 冒充已验收模型。

第一轮模型已因 20 根中的 4 项退化（含胜转败）被拒绝。实际结果与限制见 [实验记录](../../docs/strategy/contextual-ordering-20260922.md)。训练没有消除未知后续和搜索分布漂移，正则/小幅修正并不保证不退化。

`first_loss.py --baseline <root/baseline> --witness <root/better> --out <new-json>` 对照完整动作前缀和外层最终保留池；报告同状态别名前缀及政策标签，忽略可能被采集上限截断的最后一个边界。前缀缺席不自动等于状态/最优解丢失。新宿主的采集同时记录 GlobalRetention 和 RetentionPoolFinal，总行数仍受 N 限制。

`--continuous-threat` 是另一个独立实验：只在 EndTurn 后的新回合起点、玩家实际存活且有可执行手牌时，用连续 HP 项替代中途排名里的投影死亡巨额罚分；默认关闭，不叠加模型或 base/band。终局和转置不使用该修正。20个定向训练根初筛无实质退化，但验证35根为3好/3差，最终test35根为2好/3差（含2次胜转败）。均为Evaluate口径；完整Coordinator test33可比根2好/1差/30同，总转移+5.4%；默认Medium/组合的3个代表主要质量全部相同。因实际配置收益不足仍不启用；不把单成员结果当作实际交付路线。

`--observe-ordering-states <json>` 需要 `--observe-ordering N`；JSON 是精确状态键数组，例如 `[{"first":123,"second":456}]`，通常从较好见证的观察记录提取。它用于追踪相同状态的不同前缀，区分“这条前缀被剪”与“状态没有被展开”。DOP并行回调只串行写诊断文件；采集不能用于性能比较。


原生复现可用 `--performance-preset-for-test Medium --search-beam-width-for-test 24 --search-max-expanded-nodes-for-test 20000 --search-budget-override-milliseconds 60000 --search-max-degree-of-parallelism-for-test 1 --fixed-search-budget` 对齐本套件预算；PowerShell使用对应PascalCase参数。两个新增预算参数只作用于无人请求并在收尾恢复。实验排序仍须明确启用；仅指定这些预算不启用候选。

`--stop-portfolio-at-hp-target` / `--disable-portfolio-hp-target-stop` 仅用于 Coordinator，显式覆盖组合达标早停；未指定时沿用生产profile（默认开启）。仍须启用玩家战损达标政策，宿主对应 `--stop-at-zero-loss`。当前最佳完整无风险路线满足战损、成长、遗物、追回资源和必要用药目标，且没有冻结的可见治疗来源或已选路线实际回血时，跳过剩余宽度/能力成员与能力开局前缀。它不改变中途分数和终局比较器，不保证回合数或旧Score最优，也不穷举未来生成的治疗机会。其他排序实验可以在该生产政策上对照；旧实验的精确复现须显式关闭此项或使用其冻结DLL/宿主。
