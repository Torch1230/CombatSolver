# 社区任务资料

从[社区任务入口](https://github.com/Torch1230/CombatSolver/issues/171)了解参与方式，在[任务队列](https://github.com/Torch1230/CombatSolver/issues/202)查看批次、认领者和完成状态。协作规则见[贡献指南](../../CONTRIBUTING.md)，具体实验入口见[夹具与开发脚手架](testing-guide.md)。

**建议优先修复 Bug 批次，再处理更优世界线优化批次。** 优化任务先确认模拟与部署正确，再在同根、同政策、同预算下比较路线质量。

## 已发布材料与历史素材

任务材料来自玩家提交的 CombatSolver 报告，具体版本见各批次说明。下面的统计记录 2026-10-02 发布时的选包范围与历史快照；之后新增批次按各自版本和资料说明记录。

2026-10-02 的发布选取 **CombatSolver 0.47.x** 未修复报告。历史快照中该版本段有 1138 份报告、1065 个战斗 session；当轮未选更老版本，固定报告 ID 后再做静态分析。

2026-10-02 16:16（北京时间）补删已发布及重复主题的 465 条后台记录后，该快照的 0.47.x 未修复报告与可下载包均为 **700**，覆盖 667 个 session。其中 150 包带明确故障信号；计入状态不一致、部署漂移、缺续用和路线耗尽的重算线索，共 300 包。排除明确第三方内容、外部补丁、测试模型、取消和药水政策拒绝后，暂留 183 包供分诊，其中纯异常故障候选 37 包。保护暂停、更优路线和手操信号不等于已确认 Bug。

此前静态抽看 25 个故障代表包，剩余材料的待修根因工作量粗估为 **10～20 类线索**，多数与当时已发布的 20 个机制主题重叠；这不是统计置信区间或已证实根因数。根捕获时集合生命周期与球数值 Hook 枚举器合同随后纳入 B016。续用不一致包含 Power、遗物计数、牌堆状态和敌人 HP 等不同差异，尚未定位到首个错误动作；也未验证这些历史问题在当前版本是否仍存在。

完整历史快照曾包含 0.44.0 起的 2553 份报告，以及 session 去重后的 312 个优化候选，作为历史元数据保留。当前优化代表均为 0.47.x；Q002 五个代表的预测战损差为 45～76 HP，Q003～Q007 新增 25 个代表的预测差为 7～19 HP，实际收益未验证。

截至 2026-10-02 的发布记录为 **11 个批次、55 个主题、63 个代表包**。故障 B012～B016 共 25 个机制主题；优化 Q002～Q007 共 30 个具体遭遇主题。**每批五主题，每主题一至两包，一个 issue，一次认领整批。** 卡牌名和报告 ID 是样本信息，验收按主题组织。

Q003～Q007 使用 2026-10-02 16:59（北京时间）的优化快照：103 份 0.47.x 更优世界线报告，98 份满足初筛条件，覆盖 34 个尚未发布遭遇。按每遭遇最高有效预测差取 25 个主题各一包，再按差值降序组为五批。骇鳗最高差样本含 Ascension100 实际修改器和进阶 18，改取原版进阶代表；该内容修改样本保留在后台，本轮不分发。

已静态核对比较日志、旧/新搜索结果条目、开战材料、录制动作、政策与原版模型。旧包的 `MANUAL_ROUTE_IMPROVED` 只表示手操改变状态后重新预测的原始战损下降，部分比较跨回合、增加用药或缺少中途检查点；具体边界记录在各主题的 `static-evidence.json`。新包按每多用一瓶药扣9 HP记录折算优化量，分类工具优先按该量排序，并排除折算为负的候选。旧包缺少该字段时标记资源未核验，不能把标签当作已确认的算法漏解；玩家禁用药水后手动使用的路线尤其需要核对政策。算法首因尚未定位，认领者从 `combat_start` 核对模拟与部署，再做同根、同政策、同预算质量对照。

B016 从 2026-10-02 16:37（北京时间）的新快照继续整理，固定 703 条 0.47.x 报告；新增根捕获集合、球数值 Hook、生成附魔来源、永久牌组克隆身份及回合结束 Power 顺序五个主题，各取一个代表包。已静态核对调用链、状态字段和源码，但首因及当前版本行为仍待实际验证。与 T006 共用末端对账入口的样本，按明确的上游生命周期限定范围；卡名和单个 HP 差异不能用于拆主题或直接认定重复。

社区任务面向原版游戏内容，主项目不主动适配修改游戏内容的第三方 Mod。角色筛选之外，还检查最早异常栈。原版牌进入 RebalancedSpire 内容补丁的错误已移出；外部补丁归属不清和证据不足的样本也可以跳过。

## 去重口径与验证状态

- 报告/session 去重后，继续读取最早异常、第一处状态分叉和源码，按所有权、执行阶段及共同调用链归并主题。
- 卡名、回合号、牌堆位置和包装异常不能作为拆主题依据。选牌回放、路线材料化、原生选择和回合准备等共享入口先按机制调查，不逐卡派发。
- 同报错行不证明同根因。证据分为静态因果已定位、共同机制首因待证、证据不足；最后一类直接跳过。不同已证实首因可在主题内补充子问题。
- 发布前完成静态读取与资料整理，恢复、搜索和部署均未执行；旧问题在当前代码是否仍存在尚未验证。
- 默认一个主题一包；第二包只补充不同调用链或关键边界。重复样本舍弃，允许漏掉，不追求全量分发。
- 后续分发检索到已有主题，直接删除该报告服务器 ZIP 和后台报告记录并跳过，不补材料、不重新发布。发布、丢弃重复与修复验收分别记账。

分类工具为 [classify-community-reports.py](../../tools/community/classify-community-reports.py)，接受维护者导出的 JSON 快照，输出诊断组、优化排名和每份报告归属：

```sh
python tools/community/classify-community-reports.py --reports .local/community-tasks/reports.private.json --output .local/community-tasks/classification.private.json
```

基础分类工具仅生成症状桶，不证明共同根因。发布选择用 [select-community-diagnostics.py](../../tools/community/select-community-diagnostics.py)，默认过滤 0.47.x，并读取[主题登记表](theme-registry.json)。已有主题输出固定报告 ID、删除原因及 GitHub 代表材料回执；其余候选继续静态分析。泛型异常匹配需结合调用方证据，避免把所有空引用或集合错误视为同一原因。

## 下载和公开副本

资料存放在独立 [community-tasks-2026-10-02 Release](https://github.com/Torch1230/CombatSolver/releases/tag/community-tasks-2026-10-02)，Release 正文提供各批次议题与整批下载：

- [B012：T001～T005](https://github.com/Torch1230/CombatSolver/issues/149)，9 个代表包。
- [B013：T006～T010](https://github.com/Torch1230/CombatSolver/issues/172)，8 个代表包。
- [B014：T011～T015](https://github.com/Torch1230/CombatSolver/issues/173)，6 个代表包。
- [B015：T016～T020](https://github.com/Torch1230/CombatSolver/issues/174)，5 个代表包。
- [B016：T021～T025](https://github.com/Torch1230/CombatSolver/issues/182)，5 个代表包。
- [Q002：O001～O005](https://github.com/Torch1230/CombatSolver/issues/150)，5 个代表包。
- [Q003：O006～O010](https://github.com/Torch1230/CombatSolver/issues/183)，5 个代表包。
- [Q004：O011～O015](https://github.com/Torch1230/CombatSolver/issues/184)，5 个代表包。
- [Q005：O016～O020](https://github.com/Torch1230/CombatSolver/issues/185)，5 个代表包。
- [Q006：O021～O025](https://github.com/Torch1230/CombatSolver/issues/186)，5 个代表包。
- [Q007：O026～O030](https://github.com/Torch1230/CombatSolver/issues/187)，5 个代表包。

批次 ZIP 按主题编号分目录，包含 `theme.json`、`static-evidence.json` 和 `reports/*.zip`。把代表报告 ZIP 交给回放入口。旧十项批次已经迁移，关闭只表示归并；旧 ZIP 和排名 CSV 保留历史用途，当前认领以五主题批次为准。

`community-task-index.json` 保存当前 0.47.x 报告归属、主题、代表和批次，也保留历史条目及迁移状态。`theme-registry.json` 是稳定机制、匹配规则、证据等级和已发布资料去向的维护入口。认领单位仍为整批，PR 按主题编号记录进展。

[export-community-bundle.py](../../tools/community/export-community-bundle.py) 生成公开副本，清理 `report.json` 和 `diagnostics/` 中的昵称、联系方式、玩家统计字段及个人路径。**`replay/*` 保留原字节**，用于保存牌序、RNG、模型身份、原生状态与录制事件；发布材料静态检查中的单人玩家 `net_id` 均为游戏测试身份 1。公开副本的 ZIP 字节与原包不同。

```sh
python tools/community/export-community-bundle.py .local/raw/REPORT.zip .local/public/REPORT.zip
```

导出前拒绝路径穿越、链接、加密和游戏程序集；静态核对回放条目与原包一致。导出检查只证明资料处理范围，实际可恢复性由认领者运行工具判断。

## 处理记录

每批由一名 Assignee 负责全部五个主题，建议以批次为粒度集中提交一个 PR，五个主题通过 commit 和验收记录区分。可以先建立 Draft PR，持续追加进度与实现；全批完成后转为 Ready for review，五主题全部验收后关闭批次。首因、最小夹具和实际验证放 issue/PR。交接时整理已完成/未解决主题及证据，维护者调整 Assignee。

认领者自行定位、实现、验证并提交 PR，跨模块、镜像登记和模拟生命周期等方案在 PR 中审阅。最终搜索/模拟路径改动以目标修复证据、固定哨兵的质量无退化与搜索耗时无明显增加验收，详见[指南](testing-guide.md#最终-pr-哨兵验收)。范围讨论采用异步记录，已明确主题持续推进。

任务队列 #202 的“认领者”列以各批次 Issue 的 Assignee 为准：空缺显示“未认领”，已指派显示 `[用户名](https://github.com/用户名)`。在批次回复 `认领` 或 `认领 B012 整批` 自动认领本人，回复 `取消认领` 自动释放该批次；同一人可以同时认领多个批次，已有负责人时保持当前指派。具体规则见[贡献指南](../../CONTRIBUTING.md)。

[community-claims.yml](../../.github/workflows/community-claims.yml) 监听新回复、指派、标签及批次状态变更，调用 [sync-community-claims.py](../../tools/community/sync-community-claims.py)。新增批次账本推送也触发同步。队列位置由发布账本的 `queueIssueNumber` 与 `queueIssueUrl` 记录。工作流只更新队列表格中的认领者与完成状态；主入口 #171 只保存隐藏的 `combatsolver-community-claims` 回复进度标记，用户设置的标题和手写正文保留。

批次状态标签以实际 Assignee 为准：有负责人为“已认领”，无负责人为“待认领”，移除旧“待定位”及相反的认领状态标签，保留其他标签。完成状态按批次 Issue 记录：以 completed 原因关闭为“已完成”，开放为“未完成”，取消或迁移等关闭原因为“已关闭（未完成）”。每次处理尚未消费的回复，更新队列成功后再保存入口进度，队列合并或重试时保留认领顺序。

维护者可以用 `python tools/community/sync-community-claims.py --dry-run` 预览，或手动运行 GitHub Actions 的 **Community batch claims** 工作流恢复同步。脚本使用已登录的 `gh` 或 Actions 的 `GITHUB_TOKEN`，权限为读取仓库与写入 Issue；不需要日志后台凭据。

发布流程固化在 [combatsolver-community-tasks skill](../../.agents/skills/combatsolver-community-tasks/SKILL.md)。清理有两个时点：GitHub 发布成功后清理实际代表包，或者分发检索确认已有主题时直接丢弃重复包。删除固定 ID 对应磁盘/COS ZIP 和后台报告记录，原因与 GitHub 去向保存在发布账本。此前仅清理 ZIP 的 465 条报告已全部补删后台记录，其中含 342 条 0.47.x 重复主题报告。

B016 发布后已删除五个代表及 17 条已有主题重复报告，共 22 条后台记录和 22 个服务器 ZIP；累计已清理 487 条后台记录。发布及重复清理不表示已修复，主题验收证据仍由 issue/PR 保存。

Q003～Q007 发布后已删除 25 个代表报告及 59 份同主题重复报告，共 84 条后台记录和 84 个服务器 ZIP；累计已清理 571 条后台记录及服务器 ZIP。本轮本地暂存的 65 个 ZIP 与私人快照、临时发布文件也已删除。公开资料保留在 GitHub，清理回执已写入索引与账本。

[publication-ledger.json](publication-ledger.json) 保存当前主题批次、历史迁移和清理回执。清理工具 [retire-community-archives.py](../../tools/community/retire-community-archives.py) 在日志服务容器读取固定清单，支持 dry-run；`published` 表示已发布代表清理，`duplicate_theme` 表示重复主题删除跳过，后者还校验真实报告版本属于 0.47.x。
