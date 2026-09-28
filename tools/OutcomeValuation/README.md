# 结果估值与小规模训练工具

离线输入的`exportRowRoots: true`将为压缩后的训练观察生成显式根编号附属文件，供`root_provenance.read_assignments`严格核对每根权重和来源。251项C#断言、两组六项Python合同和真实292根原图不变核对通过。只读零边界检查由3→40项通过分区约束，已接入显式研究入口`root_context_fit.py`并通过六项新增合成拟合合同，真实候选尚未训练。见[状态与边界](../../docs/strategy/training-root-provenance-20260928.md)。

`root_support.py`提供尚未接入拟合器的纯数值剪枝原型：按根Hessian参与度约束树叶，弱支持分支用真实梯度/Hessian重算父叶，并要求后续轮使用修改后的预测。另六项合成合同通过，包括低曲率条件反转；不代表实际遗物收益或新模型验收。

`context_product_audit.py export-directory fitted-directory report.json --seconds 180`只读重放已保存交互候选的16轮Hessian，报告被叶节点门槛必然排除的乘积及遗物存在列。未被排除不等于有有效增益；六项合成合同已通过，真实数据诊断必须与受控性能评测串行。详见[诊断边界](../../docs/strategy/compiled-relic-interactions-20260928.md#逐轮叶节点门槛诊断)。

`context_gate_fit.py export-directory model-directory --seconds 1000`是显式离线交互原型：48棵普通树加16棵深度4树，后者可选择训练梯度筛出的二值遗物存在×原始数值乘积。导出编译回64棵原始字段树、最大深度8，生产不添加乘积观察或依赖。所有头共用时限，仍需计入导出及模型祖先训练的1,800秒总账；默认训练器不变。6项合成合同、2,880条C#合成输入与1,280条真实训练观察数值核对通过。完整训练294秒，但真实树未采用候选乘积；100场400次核心12改善/72一致/16退化、慢7.33%，未采用；不能用合成反转成功宣称掌握遗物交互。见[原型边界](../../docs/strategy/compiled-relic-interactions-20260928.md)。

联合训练实验 `crossTurnOutcomes: true` 依赖 `balanceCorrectionSources: true`，用同根、不同回合且不同原候选池的已完成结果补充有界偏好。它复用原行，不比较模仿标签或以更短动作后缀制造跨回合价值；每根新增边至多1,024，总边仍至多4,096，非空来源等权且根总权重1。只改变C#导出图，Python不重建标签；完整训练仍受1,800秒上限和独立场景审计约束。见[完整协议](../../docs/strategy/cross-turn-context-calibration-20260928.md)。

宿主 `--audit-outcome-context-ranking <结果训练输入> <模型> <输出>` 对已有同根完成见证作有界跨池/跨回合诊断，只使用完整终局政策，不用动作后缀长短制造政策偏好。`--check-outcome-context-ranking` 验证其合同；结果不能当作独立测试准确率。见[292训练根审计](../../docs/strategy/training-context-ranking-audit-20260928.md)。

`collect_continuations.py <冻结协议.json> <输出目录>` 用于有限的跨回合训练补采，不拟合模型；训练/开发/最终集合先做家族与实际牌组隔离，再核对每次原生装备和双根戳。冻结二进制、模型、输入摘要，保留失败，可续跑已记录任务；Linux入口使用指定CPU亲和性，单进程超时由协议给出。教师代价和采集RSS单列，不作为正常推理性能。协议与试采证据见[跨回合续局监督](../../docs/strategy/cross-turn-witnesses-20260928.md)。

`neural_fit.py` 是可选的CPU单隐层tanh残差训练器，依赖现有 `requirements-ranking.txt`，游戏内只运行C#数值核。示例：`python tools/OutcomeValuation/neural_fit.py export-directory model-directory --seconds 1000 --units 16 --penalty .1 --iterations 128`。沿用权威偏好图和学习得到的线性基础项；活动列和RMS只从训练侧计算，权重折回原始观察单位，全部角色共用一个时限，传入时间仍须扣除完整导出成本。输出schema11，至多32个隐单元；旧9/10明确拒绝携带神经项，11必须携带神经项且不混入因子。线性导出清除全部非线性项，旧模型序列化不会增加空字段。`--self-test`检查梯度、重复数据的根权重、条件作用和原单位导出；128次迭代耗尽记录为未收敛，不能当作最优解或更强决策。仍须通过C#实样本核对及独立实战，不自动启用模型。

训练输入可显式指定 `"pairWeighting": "turns"`，在每个根内按已采样偏好对的较晚端点回合分层，非空回合等权、回合内各对等权，每根总权重仍为1。默认 `pairs` 保留逐对等权和既有产物；标签、配对端点、随机抽样、最多4096对及特征支持度不变。原始观察必须包含有限正整数 `battle/turn`，先校验全部原始行再抽样，排除此列的消融不能同时使用。未知值拒绝，内置和外部训练共用同一C#权重入口，无新增玩家模式或模型格式。审计始终按原始全量对计算根均值，额外 `turns` 分项仅描述回合覆盖和损失；缺少回合的合成观察分项为null。这是待实战验证的实验参数。

`sparse_fit.py` 是可选离线联合线性拟合器，使用同一C#导出图，依赖现有 `requirements-ranking.txt`。示例：`python tools/OutcomeValuation/sparse_fit.py export-directory model-directory --l1 .001 --l2 .001 --seconds 1000 --stop STOP`。正L1产生精确零系数，非负L2约束系数；两者均须有限并按隔离的完整家族选型。偏好差值RMS只使用训练侧，根内不变列保持零；输出沿用基础文档的schema与游戏身份、现有 `LinearWeights` 和零残差树，不新增运行模型或依赖。五角色合用256次/头的固定迭代协议和一个总时限，显式记录收敛状态与KKT残差；`--seconds` 仍须扣除C#导出成本。数学合同不证明战斗优势，实际候选需核对C#数值和独立开发质量。`test_sparse_fit.py` 覆盖解析梯度、已知最优解、精确零、重复对的根权重、原单位导出及失败不发布。

`factor_fit.py` 是另一种离线训练后端：固定学习得到的线性项，加秩8二阶因子交互；只需 `requirements-ranking.txt` 的 NumPy/SciPy，不依赖 XGBoost。使用下方同一个 C# 导出目录，运行 `python tools/OutcomeValuation/factor_fit.py export-directory model-directory --seconds 1000 --stop STOP`。总预算仍包括导出和全部角色，`--seconds` 要扣除已用导出时间；遇到停止/超时/坏输入不发布完整模型。固定 L-BFGS-B 128 次迭代、均值偏好损失，默认 L2=.001，迭代上限并不意味着收敛。离线可显式指定非负有限 `--regularization`；应使用隔离的场景家族选型，线性基础、特征支持度和归一化也只能由内部训练侧拟合。首次完整家族留出试验见[正则化选型](../../docs/strategy/factor-regularization-selection-20260928.md)，默认值与玩家入口不变。

因子只增加模型格式（schema10），原始观察保持8；宿主明确兼容无交互项的9，旧宿主拒绝10。各外部后端共用 `ranking_data.py` 读入原 C# 偏好图，不在 Python 重建标签。用相同预测命令核对 C#，然后做独立开发战斗；首个完整交互模型仍有退化，未启用，见[实际结果](../../docs/strategy/factor-interactions-20260928.md)。`test_factor_fit.py` 覆盖解析梯度、归一化/原单位导出、情境反转、未观察列、停止及损坏输入。

可选 CPU 树训练器位于 `xgboost_fit.py`，只用于离线研究。Linux 私有 Python 3.12 环境安装 `requirements-xgboost.txt` 中的固定依赖；Mod 没有新增 Python/XGBoost 依赖。流程如下，完整导出加五角色拟合的外层总超时应不超过 1,200 秒，传给 Python 的 `--seconds` 必须扣除导出耗时：

```sh
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --export-outcome-ranking inputs.json export-directory
python tools/OutcomeValuation/xgboost_fit.py export-directory model-directory --seconds 1000
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --predict-outcome-features model-directory/model.json model-directory/parity-inputs.json actual.json
```

输入继续遵守下面的场景隔离规则。导出与原训练共用 C# 配对、根权重、特征支持度及线性基础项，采用显式零的稠密 float32 矩阵。Python 只学习 64 棵深度 6 的残差树，使用耦合配对损失的对角 Hessian 上界；它不是独立行标签模型。导出目录必须为空，拟合输出目录必须尚不存在；出错产物不能用于搜索。核对 `actual.json` 与 `parity-expected.json`，再做独立开发战斗；训练损失降低不等于战斗更强。`test_xgboost_fit.py` 覆盖梯度/上界、边界导入和数值一致性。此试验不增加玩家模式，不自动替换夜间或游戏默认算法。


用户明确授权长时间运行时，可用 Linux `overnight.py prepare` 冻结程序与数据，再用生成目录内的 `scripts/overnight.py run <目录>` 启动可恢复监督进程。它不更改下面短训练入口的 1800 秒规则；整夜累计采集/拟合成本单独记录。用 `--until <带时区的截止时间>` 明确终点，`--validation` 与 `--sealed-test` 分别指向开发和封存测试，`--baselines` 是各开发根的已有基线目录和程序集/搜索配置身份，`--screen-id` 重复指定预先选定的筛选子集；`--prior-job <已停止目录>` 可以继承完整数据、失败记录与成本，原训练行摘要和隔离会重新核对；其余必需参数见 `prepare --help`。

最多四路采集，按五角色、非空章节/遭遇类型桶轮换。每批 45 根，默认拟合最多 128 个有偏好根、单次最多 600 秒；线性和完整模型均用独立开发场景筛选，最终测试只做结构排除。状态见生成目录的 `STATUS.md` / `status.json`，创建 `STOP` 文件会终止并回收本任务的子进程；恢复前删除这个停止标记并运行同一冻结脚本。中断记录保留，独占锁拒绝重复启动。历史基线耗时不能证明本轮提速，未核实结局不计可靠胜局，模型不会自动上线。详见[本轮报告](../../docs/strategy/shared-scheduler-20260927.md)。

`prepare --fit-roots <20..1024> --fit-rows-per-root <64..8192> --fit-seconds <1..1800>` 可显式调整独立根数、每根行数和单次拟合时限，写入冻结计划。默认保持旧输入格式；非默认行数需要支持 schemaVersion=1 的新拟合宿主。减少每根行数会改变训练样本，必须另做开发验证。宿主也接受 `{ "schemaVersion": 1, "maximumRowsPerRoot": 512, "roots": [...] }` 输入；旧数组仍等价于 2048 行/根。读取时共享训练局部的特征名字符串，各行数值独立，全部原始标签在抽样前校验。

实验输入可另指定 `"sampling": "pools"`，按已观察到的比较组而非单行抽样，不按标签优劣选择组；默认 `rows` 保持原行为。过大比较组或未知采样值明确拒绝，512 行/根仍是硬上限。先看[同根单因素实验](../../docs/strategy/pool-sampling-20260927.md)，不能把偏好对增加当作模型已经更强。

训练输入可显式指定 `"partition": "character"`，先统一抽样再按观察到的原生角色拟合；输出仍是 `model.json` / `model.linear.json`，各自内嵌所有角色头。宿主在搜索前按实际根角色选择，所有头校验格式和游戏 MVID，未知角色拒绝；共享文档格式与默认 `partition=shared` 保持支持，每个角色模型仍须满足各自的9/10/11格式合同。夜间 `prepare --fit-partition character` 将选择写入冻结计划，必须使用支持条件容器的新宿主。单次拟合时限覆盖全部角色训练，不能给每个头重新开一份预算。见[角色条件估值](../../docs/strategy/character-conditioned-ranking-20260927.md)。

敌方 Power 汇总及当时旧观察升级的历史边界见[本轮报告](../../docs/strategy/shared-enemy-powers-20260928.md)。夜间 r5 继续使用其冻结的旧程序/模型/观察格式；不能直接给冻结服务换 DLL。

可选 `"pairSelection": "highest-policy-tier"` 是离线训练消融：每根若有胜负对照就只学胜负，否则学胜局政策差异，再否则学同政策的剩余动作；默认 `all` 保持原配对。两种方式都先验证全部原始行、每根总权重相同、最多 4096 对。未知值拒绝，输出 `pairKinds` 按胜负/胜局政策/动作顺序计数；这不是玩家搜索模式，也不是已验证的默认改进。

训练输入可用 `"excludedFeaturePrefixes": ["pile/hand/card/"]` 做显式列消融，默认不排除。前缀按 Ordinal 精确匹配；全量原始标签先验证，随后仅从已固定的抽样观察中移除对应列，原始文件、标签、组和行顺序不变。空白/重复前缀、非字符串数组或移除全部列明确失败。输出记录排除列表。它可能丢失必要信息，不能当无损简化；推理只读取模型实际引用的原有列，不增加玩家选项。

外部训练可显式指定 `--minimum-leaf-hessian 3`，默认 0 保持此前实验。每个根的边总权重为 1，在当前对角上界中总 Hessian 不超过 1；阈值 3 使分裂后的叶子至少需要三个根的数值贡献，同一根的数百行不能填满该阈值。这是保守证据量约束，不是精确根计数，也不能保证每个根都贡献非零梯度。未知/负数/非有限输入拒绝；不会重采样或更改标签。此项仍是研究参数，[固定数据试验](../../docs/strategy/leaf-evidence-regularization-20260928.md)没有通过开发质量门槛。

`--audit-outcome-ranking <输入清单> <模型文件> <输出>` 输出每根及 `kinds` 三类排序诊断（胜负、胜局政策、剩余动作），始终审计全部原始偏好，不因训练筛选而删掉困难对照；`rootIndex` 是输入清单中的从 0 开始索引，`root` 仅为目录显示名，不能作为唯一身份。同名 `search` 目录不得合并。数据是否参与拟合由调用方的冻结分组决定，排序损失不代表完整搜索决策质量；见[分组留出选型试验](../../docs/strategy/model-selection-20260928.md)。

当前训练入口 `train.py` 采集同池完整胜利和引擎确认的终局死亡见证，拟合模型schema10的线性基础项与成对残差树；采集加拟合由 `--seconds` 限制，最大1800秒。传统搜索探索与验证不受此前误解的“所有工作共30分钟”限制。

拟合宿主最多使用四路列统计；分裂选择保持原顺序，输出记录 `participatingRoots` / `participatingRows` / `trainingParallelism`。原始行全量校验，未被偏好对引用的行不进入训练数组；两条死亡续局即使敌人剩余血量不同也没有偏好。夜间 `prepare --fit-harness <DLL> --fit-mod <DLL>` 可单独冻结新拟合器，采集和验证继续使用 `--harness/--mod` 指定的原搜索引擎；两项必须同时提供，加载时继续验证模型格式与游戏 MVID。冻结基线同时保存实际原生装备，支持后续任务再次核对并继承；升级只改训练，不借用不兼容的基线身份。详见[吞吐与有效监督](../../docs/strategy/overnight-training-throughput-20260927.md)。

- `prepare_training.py --catalog <原生目录> --harness <宿主> --mod <Mod> --exclude-manifest <验证> --exclude-manifest <最终测试> --out <新目录> --seed <预先固定种子>`：按目录固定4普通/3精英/1首领，每个角色各8根，只做原生建局；保留实际装备和准备时间。
- `refit.py --prior-training <完整训练目录> --manifest <同训练清单> --evaluation-manifest <验证清单> --evaluation-manifest <封存测试清单> --harness <宿主> --mod <Mod> --out <新目录>`：复用观察，核对底层请求/装备与原采集一致，继承全部历史成本并在余下1800秒总额内拟合；不重开免费训练预算。输出完整模型和纯线性消融产物。
- `train.py --preparation-budget <准备账本>` 将上述建局成本纳入1800秒。采集不在首个零损胜局提前停止；新颖性观察最多64池，Beam与新颖性总共仍最多256池。正常验证保持零损早停。
- 线性与残差树只使用至少三个有偏好训练根中出现的特征；按场景统计支持度，不把一场的重复状态当成独立证据。
- 模型 schema10 与训练观察 schema8 分别校验；新增当前分支规则下的能量上限、抽牌量，复用引擎查询而非手写效用。这不是保证下回合到账的资源。兼容无交互项的 schema9 模型，schema8 及更旧模型和观察8以前的行明确拒绝；旧行缺失新量，必须重新采集；任何 `featureUpgrade` 配置都拒绝，不填零伪装新观察。旧聚合投影可由原冻结程序复现。Python 历史标签普查可识别 6/7/8，但不会把旧行转换成可供当前 C# 拟合的行。每次搜索仍核对实际完整装备。
- `dataset.py --train <训练清单> --evaluation <验证清单> --evaluation <测试清单> --out <审计JSON>` 对三组两两检查模板、实际遭遇家族和牌组隔离；随机根需 `loadout` 指向原生 `generated-scenario.loadout.json`。种子/血量/牌序/升级/遗物计数变体不算新场景。同角色牌组多重集 Jaccard ≥0.85 拒绝；该门槛不是统计独立性的证明。
- `train.py` / `refit.py` 必须重复提供 `--evaluation-manifest <验证清单> --evaluation-manifest <测试清单>`，只读取清单及建局装备来检查隔离，不读取验证或测试搜索结果，不把这些轨迹加入拟合。同根全部轨迹属于同一组，禁止随机按状态行拆分。
- `prepare_holdout.py --train <训练清单> --training-results <采集目录> --catalog <原生目录JSON> --harness <宿主DLL> --model <模型> --out <新目录> --seed <冻结种子>` 按目录顺序选五个未见遭遇，原生建局后审计并封存，不运行搜索、不按结果筛选。
- `python3 tools/OutcomeValuation/test_dataset.py` 验证防重叠门禁；宿主 `--check-outcome-ranking` 验证成对标签与模型合同。

生产尚未移除手写评分；五个独立最终测试根保持封存。当前结果见[连续估值与场景支持度](../../docs/strategy/linear-outcome-ranking-20260927.md)。新增入口：

- `train.py --roll-in-model <已训练模型> --prior-training <其训练目录>`：在同一组根上采集模型轨迹，并进行有界旧Beam纠正；继承全部采集文件和历史训练成本，总成本最多1800秒。必须核对原清单、模型及每根的两个完整状态戳。
- `prepare_holdout.py --kind Elite --split validation --exclude-manifest <封存测试清单>`：准备独立验证组，排除最终测试遭遇；仍只按目录选择，不看搜索成绩。
- `evaluate.py --train <训练清单> --manifest <验证/测试清单> --model <模型> --mod <Mod DLL> --harness <宿主 DLL> --out <新目录>`：独立进程交错对照，核对两臂根戳，记录旧Score清零的实质比较。评测最终测试时还必须提供 `--validation-manifest <开发验证清单>`，防止验证集与测试集相互泄漏；每次只运行一组。首次打开test锁定模型、程序、搜索配置与三组数据摘要；改变这些内容后该组只算开发数据，须另设最终测试。

学习分已通过 `ObjectiveRankScore` 接入共用保路与自动协调器；旧实验专用宽度循环已删除，新颖性队列及回退也使用学习分。状态去重、选牌/循环调度和完整结果下界仍由原职责负责。模型在准入节点/固定前缀释放前计算标量，以弱引用快照身份及完整状态/政策键保存，使保路可以比较已释放模拟器的父节点及record副本；另一个按状态去重的缓存仍上限4096项。弱表随仍存活的快照增长，不是固定4096项内存上限，不保存模拟器或特征向量。辅助路线仍使用旧结构特征，不能称全部手写启发式已消除。v31新增十个按目录预选的普通/首领开发根；后续训练和最终评测应使用包含全部15根的开发清单，不能只核对原五根。

采集、拟合和比较进程通过环境变量显式加载 `--mod` 指定的程序集；完整日志不提交。下文为早期零训练方案的历史复现。

# 零训练的动作条件估值实验

以真实模拟后续的胜负、战损、资源代价评价当前动作；不生成全局卡牌分数，也不拟合模型。
本工具是离线研究入口，正常游戏不加载它。结论与限制见
[实验报告](../../docs/strategy/outcome-valuation-20260927.md)。

以下第一版复现命令与后面的第二版命令组择一使用，全部批次需共用半小时总账。

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release
python3 tools/OutcomeValuation/run.py --out .local/outcome/screen --seconds 1000
python3 tools/OutcomeValuation/run.py --out .local/outcome/heldout --suite heldout --seconds 400
python3 tools/OutcomeValuation/test_run.py
```

Python 仅使用标准库。第一批 12 根（10 个定向机制、2 个随机构筑），第二批 5 根（五角色随机精英战）。
第二批没有参数调优。默认每臂 `Evaluate / DOP1 / beam24 / nodes12000 / 30s / Disabled`，
零战损目标早停开启；每根两臂的执行顺序交替。比较调用生产终局比较器，另报告去掉旧 Score
末位的实质差异。它不代表 Coordinator 或原生部署验收。

实验臂通过 `--outcome-probes 8` 启用：

1. 半数节点、半数软时间给普通搜索，并保留它找到的结果。
2. 复制首个相关 Prune 输入池中的首动作，含物理牌身份、目标和选牌信息；去重后最多 128 项。
   按动作序列化键排序，均匀抽取至多 8 项，不用卡牌评分挑选。这里仅覆盖已经生成的动作，
   不会找回更早被分支枚举或转置准入排除的动作。
3. 重用冻结根和现有固定前缀入口，为每个首动作运行 beam≤8 的后续搜索。余下节点按实际已用量扣除，
   时间按整个实验的墙钟扣除，剩余份额在剩余探针间均分。所有 solver 共用一个请求账本。
4. 只有无预测风险、正常边界的完整胜利能替换原结果。用原终局政策比较，比较时清零旧 Score 尾键。
   没搜完、未获胜或未知分支保留未知含义，不能作为动作差或无解的证明。

第二版可加 `--selective --probes 4`（单请求 CLI 为 `--selective-outcome-probes --outcome-probes 4`）：

- 先让原搜索使用完整配置额度，并保留返回结果。随后最多再消耗其实际展开量，且总量不超过原节点上限；整个请求共用原软时间上限。
- 在已有搜索中旁听完整胜利，把结果按精确首动作保存到当前根的有界缓存。最多 2048 次回调、128 个动作；不持有模拟器、节点或跨根状态。
- 按已见终局质量安排候选，与未知候选交替；先覆盖不同牌/目标组，再覆盖组内选择。已有好见证的动作仍能再探测，没有好见证也不意味着差。它只复用估值信息，不缓存可直接执行的整条路线。
- 达到用户明确启用的战损目标时，沿用生产组合的早停条件，省掉后续探测。基线耗尽预算时不追加。
- 节点预算停止搜索，但选中快照已是正常、无风险、存活的完整胜利时，允许用它参与比较。未完成、预测边界、内存截断和部分接管结果仍不接纳；这不构成最优性证明。

加 `--coordinator` 会将两臂都切到 `Coordinator --use-portfolio`，实验臂保留完整协调器结果后追加探测。
此入口要求 selective 且禁用药水，防止绕过药水反事实审计。原协调器可能因专用成员额度超过节点或软时间配置；
这时实验原样保留基线、不追加，并单独记录 `baselineNodeAllowanceExceeded`，不能宣称整请求严格符合该节点/时间帽。
实验仍只在离线宿主显式开启，游戏不会自动启用。
`--coordinator` 本身不打开游戏设置里的多策略探索；额外加 `--novelty` 才开启该生产路径（宿主参数 `--novelty-portfolio`），
它与后置探索实验 `--adaptive-novelty` 分开，不能同时使用。双开对照和合并建议见第二版报告的补充章节。

```bash
python3 tools/OutcomeValuation/run.py --out .local/outcome/v2-screen --seconds 400 --selective --probes 4
python3 tools/OutcomeValuation/run.py --out .local/outcome/v2-confirmation --seconds 300 --selective --probes 4 --suite heldout --seed-tag=-v2-confirmation
python3 tools/OutcomeValuation/run.py --out .local/outcome/v2-coordinator --seconds 180 --selective --probes 4 --coordinator --case focus_investment --case attack_or_block --case random-regent
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --check-outcome-cache
```

`--case` 可重复以固定少量目标/哨兵；`--seed-tag` 用于事先冻结新 RNG 确认组，不应在看结果后调参并继续称其留出。
第二版与第一版的结果及完整累计用时见[第二版报告](../../docs/strategy/outcome-valuation-v2-20260927.md)。

`outcome-probes.json` 保存所有条件结果、实际节点/转移、选择的探针、动作覆盖与软时间/节点超额标记。
输出的 `Total*` 工作量包含全部 solver；其他单 solver 指标仍属于所选结果。
原节点/软时间帽没有增加，但实际消耗通常增加；一次不可分割工作也可能越过软预算。
额外探测的首动作不能是药水、EndTurn 或结束回合的牌，带根准备选择的局面不做额外探针。这些缺口有显式记录。

`run.py --seconds` 限制单个批次的全部采集、进程启动与比较，最多 1800 秒；单进程最多 120 秒。
超时、错误和缺对保留记录，连续两次失败即停止。`--resume` 只重用同命令的成功结果，核对程序集、
配置和输入摘要；此前时间继续扣账，异常中断还会保守计入中断后的墙钟间隔。
多次独立批次仍需自行合计时间；本次两批上限事先分配为 1000+400 秒。
修改程序集后请使用新输出目录。完整日志和请求保存在 `.local/`，不提交。

本轮 17 根中 3 根减少战损、1 根同战损提前结束、13 根实质相同；无胜负翻转。
合计搜索墙钟约 68.0→108.3 秒，实际转移约 23.6→49.2 万；不能据小样本称为普遍改善。
续搜仍依赖原启发式，当前原型没有消除全部手写评分，训练时间为零。

跨回合查询的联合导出可使用 `correctionInputs` 和 `balanceCorrectionSources: true`。它仍输出同一数值格式供现有CPU拟合器消费；逐根原生身份、六行上限、独立池、有限边预算与单次根权重由C#校验，不能在Python重新生成偏好。完成采集后才冻结新训练输入，单次完整导出/拟合及必要祖先训练共用1800秒期限；采集成本单列。详见[纠正来源平衡](../../docs/strategy/source-balanced-corrections-20260928.md)。

固定配对报告同时保留完整终局分类与 `coreClassification`（C#原比较器仅排除结束回合及旧Score）。旧比较缺少 `coreComparison` 时核心分类为未验证，不猜测。总表、角色/战斗类型、共同胜/败、质量不下降及稳定获胜子集的时间/内存分别输出；重复搜索不是新的独立场景。两种描述性区间均按遭遇家族重抽样，不能解释为全部真实牌组的总体保证。
