# 零训练的动作条件估值实验

以真实模拟后续的胜负、战损、资源代价评价当前动作；不生成全局卡牌分数，也不拟合模型。
本工具是离线研究入口，正常游戏不加载它。结论与限制见
[实验报告](../../docs/strategy/outcome-valuation-20260927.md)。

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

`outcome-probes.json` 保存所有条件结果、实际节点/转移、选择的探针、动作覆盖与软时间/节点超额标记。
输出的 `Total*` 工作量包含全部 solver；其他单 solver 指标仍属于所选结果。
原节点/软时间帽没有增加，但实际消耗通常增加；一次不可分割工作也可能越过软预算。
首动作不能是药水、EndTurn 或结束回合的牌，带根准备选择的局面不做额外探针。这些缺口有显式记录。

`run.py --seconds` 限制单个批次的全部采集、进程启动与比较，最多 1800 秒；单进程最多 120 秒。
超时、错误和缺对保留记录，连续两次失败即停止。`--resume` 只重用同命令的成功结果，核对程序集、
配置和输入摘要；此前时间继续扣账，异常中断还会保守计入中断后的墙钟间隔。
多次独立批次仍需自行合计时间；本次两批上限事先分配为 1000+400 秒。
修改程序集后请使用新输出目录。完整日志和请求保存在 `.local/`，不提交。

本轮 17 根中 3 根减少战损、1 根同战损提前结束、13 根实质相同；无胜负翻转。
合计搜索墙钟约 68.0→108.3 秒，实际转移约 23.6→49.2 万；不能据小样本称为普遍改善。
续搜仍依赖原启发式，当前原型没有消除全部手写评分，训练时间为零。
