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
