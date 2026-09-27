# 结果估值与小规模训练工具

用户明确授权长时间运行时，可用 Linux `overnight.py prepare` 冻结程序与数据，再用生成目录内的 `scripts/overnight.py run <目录>` 启动可恢复监督进程。它不更改下面短训练入口的 1800 秒规则；整夜累计采集/拟合成本单独记录。用 `--until <带时区的截止时间>` 明确终点，`--validation` 与 `--sealed-test` 分别指向开发和封存测试，`--baselines` 是各开发根的已有基线目录和程序集/搜索配置身份，`--screen-id` 重复指定预先选定的筛选子集；`--prior-job <已停止目录>` 可以继承完整数据、失败记录与成本，原训练行摘要和隔离会重新核对；其余必需参数见 `prepare --help`。

最多四路采集，按五角色、非空章节/遭遇类型桶轮换。每批 45 根，默认拟合最多 128 个有偏好根、单次最多 600 秒；线性和完整模型均用独立开发场景筛选，最终测试只做结构排除。状态见生成目录的 `STATUS.md` / `status.json`，创建 `STOP` 文件会终止并回收本任务的子进程；恢复前删除这个停止标记并运行同一冻结脚本。中断记录保留，独占锁拒绝重复启动。历史基线耗时不能证明本轮提速，未核实结局不计可靠胜局，模型不会自动上线。详见[本轮报告](../../docs/strategy/shared-scheduler-20260927.md)。

`prepare --fit-roots <20..1024> --fit-rows-per-root <64..8192> --fit-seconds <1..1800>` 可显式调整独立根数、每根行数和单次拟合时限，写入冻结计划。默认保持旧输入格式；非默认行数需要支持 schemaVersion=1 的新拟合宿主。减少每根行数会改变训练样本，必须另做开发验证。宿主也接受 `{ "schemaVersion": 1, "maximumRowsPerRoot": 512, "roots": [...] }` 输入；旧数组仍等价于 2048 行/根。读取时共享训练局部的特征名字符串，各行数值独立，全部原始标签在抽样前校验。

当前训练入口 `train.py` 采集同池完整胜利和引擎确认的终局死亡见证，拟合模型schema7的线性基础项与成对残差树；采集加拟合由 `--seconds` 限制，最大1800秒。传统搜索探索与验证不受此前误解的“所有工作共30分钟”限制。

拟合宿主最多使用四路列统计；分裂选择保持原顺序，输出记录 `participatingRoots` / `participatingRows` / `trainingParallelism`。原始行全量校验，未被偏好对引用的行不进入训练数组；两条死亡续局即使敌人剩余血量不同也没有偏好。夜间 `prepare --fit-harness <DLL> --fit-mod <DLL>` 可单独冻结新拟合器，采集和验证继续使用 `--harness/--mod` 指定的原搜索引擎；两项必须同时提供，加载时继续验证模型格式与游戏 MVID。冻结基线同时保存实际原生装备，支持后续任务再次核对并继承；升级只改训练，不借用不兼容的基线身份。详见[吞吐与有效监督](../../docs/strategy/overnight-training-throughput-20260927.md)。

- `prepare_training.py --catalog <原生目录> --harness <宿主> --mod <Mod> --exclude-manifest <验证> --exclude-manifest <最终测试> --out <新目录> --seed <预先固定种子>`：按目录固定4普通/3精英/1首领，每个角色各8根，只做原生建局；保留实际装备和准备时间。
- `refit.py --prior-training <完整训练目录> --manifest <同训练清单> --evaluation-manifest <验证清单> --evaluation-manifest <封存测试清单> --harness <宿主> --mod <Mod> --out <新目录>`：复用观察，核对底层请求/装备与原采集一致，继承全部历史成本并在余下1800秒总额内拟合；不重开免费训练预算。输出完整模型和纯线性消融产物。
- `train.py --preparation-budget <准备账本>` 将上述建局成本纳入1800秒。采集不在首个零损胜局提前停止；新颖性观察最多64池，Beam与新颖性总共仍最多256池。正常验证保持零损早停。
- 线性与残差树只使用至少三个有偏好训练根中出现的特征；按场景统计支持度，不把一场的重复状态当成独立证据。
- 模型schema7与训练观察schema6分别校验；本轮观察语义未变，可复用schema6数据，旧模型不可由新加载器直接读取。训练和验证每次搜索还会核对实际完整装备与清单指向的冻结装备一致。
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
