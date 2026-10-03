# CombatSolver 测试入口历史卷 07

## 通用搜索、语义与 Headless 验证记录（开发中）

以下逐项记录保留对应 vN 阶段的证据与当时判定；当前交付口径以上方摘要为准。v66 同回合落选续搜已通过 Custom 首结果与实际部署，v67 已补边界合同。仅现有失败窄搜开启；前缀根/动作工作计入原节点预算，不能把单出边回放当成新的完整节点展开。

| 验证 | 当前证据 | 边界 |
| --- | --- | --- |
| 无保留路由普通同分排序 | v77策略合同 `373de14258414197b22e534152302b47`；Soul `45cd4d4d81ee42498d9efd83e9e8cf9f` 正常1损/97HP/T4/零药 | 原20秒/DOP1/GC、2112/13480/6996，旧质量审计通过；同Turn/full6D各自原位置排序，带旧保留路由的组内位置固定，不改必保/席数/预算。Release与两端66门禁通过，非本轮部署证明 |
| v77 Exoskeletons非退化哨兵 | `00d1a845cfa74866a4f7e12910db2e18` 零损T10、13746/208100/147857，当前v66基线审计通过 | 原VeryHigh/DOP8/NoGC16/Smart；旧T5质量审计仍失败，不把当前非退化当作全部目标达成。headless共享进程数据不作最终性能结论 |
| v77 Soul DOP1/2正常一致性 | DOP2 `feefa42bb2d54ea99b1eb8ac08e4e54d`，同1损/97HP/T4/零药和2112/13480/6996；实际最大并发2 | 80项非时序/非调度RESULT、26条日志动作、4条回合结果及政策路线一致；DOP2 NoGC4保持/rollover0。原断言、GC和冷暖配置不同，非性能A/B或完整PlanAction字节等价 |
| v77 Custom恢复非退化 | `9bc0a475157246e4a351a149e5ca012b` 原配置短质量通过，3损/1HP/T1/1药/7洗牌/19动作 | 获胜solver仍10742/39529/3802，请求28726/97786/10486略变；不宣称全部工作量等价，未重复v66原生整场部署 |
| 实验有序派生键清理 | v77 `02354eecb8c145368efdf5ca083c3f08`，26基础前缀及五变体第11/12步完整/增量、根/live通过 | 生产只去掉实验派生哈希及其冗余核验，完整StateKey/Continuation不变；Testing对每个已见变体逐一比较四牌堆token数组，不反向代替通用状态键。不启动Solve，未测量独立性能收益 |
| 新鲜生成粗族单席补位（否决实验） | v75 `7610a80fe425460c8917dfa14689a2ca` 合同通过；正常Exo `65db7899b0424deba05e9eca2f0058ad` 损1/T4 Failed | Release零警告错误、两端66门禁通过；4803/99681/75856，原VeryHigh/DOP8/NoGC16/Smart。提前结束不能代替零损目标；完成拒绝版诊断后已撤回生产因素及专属合同/门禁 |
| v75拒绝版第4步保留及第5步断点 | `5196a01b963a4e558a1808160c6bb00b` 6407事件NoDrops，完整四敌/根/live证明通过 | 4 raw2000→selected90→Final→Expanded，旧required/routing/容量不变；5生成/准入后Prune11丢失，该次不含5整池。诊断工作量同正常4803/99681/75856、仍损1/T4；不作质量或性能通过 |
| v76拒绝版第5步完整候选池 | `46ec3b33c3e34e889448b1b557f429b9` 3997事件NoDrops，四敌24前缀/根/live通过 | 目标raw257无必保/路由/选中；135全局选中均在153最终集合。33节点来源族有11个最终存活，完整生成签名未变；同父攻击敌3/2存活，敌1/4落选，战术前三键相同但防守投影不同。仍损1/T4，不支持延长生成保护；生产v75已撤回 |
| 普通同分截线同政策战术排序合同（已撤回实验） | v74 `f82e142ca2e64d52933ceeda728c1491` SearchPolicySnapshot通过，Release零警告错误、两端66门禁通过 | 真实9/7目标换入、完整6D+Turn逐维交错隔离、稳定词典序、必保原位及全部既有旁路；邻接质量回退后，仅撤回本因素及专属合同/门禁 |
| v74 Soul目标通过但外骨骼虫回退（已撤回） | Soul `72efaeef12f64c2b85b0a83b08120b64` 1损/T4通过；Exo `de76b95d766e4398b444010ed98421b0` 0损/T11，质量审计失败 | 原v9输入/预算/Smart。Soul2112/13477/6994；Exo14485/204622/142069，相对当前v66的0损T10回退且旧T5未达。无观察器，只验首结果，不是实际部署或Steam性能结论；未跑DOP2/全矩阵 |
| Soul实际换序代表与第18步整池 | v73 `8591487e906b426383ed306696ba6ecb` Passed，1406事件/1StrictAliasAnchor | 真实18前缀+原样末8步逐步完整/增量、根/live通过，1损97HP/T4；18 raw35无routing，普通同分块9选7遗漏目标。只证明战斗后缀及实际剪枝，不证明调度等价；原质量仍19损T7。Release零警告错误、两端66门禁通过 |
| 外骨骼虫第4步真实整池 | v72 `2bef44a967284a4c9bf39e9404b4295d` Passed，6400事件NoDrops | 真Exo solver第10边界：3109排序、91必保/135限额、96路由/54配额；目标raw2000无routing/required/selected/Final。完整保留池连续索引通过。复用进程日志须按Sample/solver分开；原质量0损T10、13746/208100/147857未变，非性能证据 |
| 五生成上下文完整后缀与联合路径观察 | v72 `adb95fc0a6f046afbe3a33ec1278b19f` Passed；五条26步各1损/97HP/T4/零药，626事件NoDrops | 仅第8步重绑定，后续全部冻结；完整/增量及根/live通过。按完整动作与实测政策分桶，防御变体12真实routing10/quota13、selected47并展开，准确展开到14；15为TT拒绝且有同状态别名，不作全路径丢失结论。正式搜索仍19损T7；Release零警告错误/两端66门禁通过，无生产保路变更或性能结论 |
| v71全部选牌输出细分普通席（已撤回） | 正常 `bf3b1d42f48344ec9e27c0c2d9873635` 损43/T11；诊断 `48fd2f875573474d8ca7739a8f900a1f` 第11步整池断言Failed | 正常主6926/45680/22819，请求8480/54547/25635原20秒；诊断110事件，两solver准确1–7 Expanded，8准入后Prune丢失，11未到达。仅通过Release/两端66门禁，未跑新静态合同/Exo哨兵；全部357行新普通席因素已撤回 |
| 外骨骼虫已知早胜路线原版严格对照 | v70 `9c8e6cf093bd40aa8149e9d225d66c44` 通过，实际97/103HP、0损、0药、T5 | `KNOWN-EXOSKELETONS-ROUTE-NATIVE-V0111`：24完整预测先冻结；24原版动作、6Primary/4Nested/4EndTurn逐敌StateDiff/Continuation、阵容/死亡/行动及累计伤害/药水/洗牌事件一致。真实清理前四敌取证并等待CombatEnded；不Solve、不代表生产UI部署。最终Release零警告错误、两端66文件门禁通过 |
| 外骨骼虫已知早胜路线首次丢路 | v70 `f538b44ac3d045f8a07a01f563abe7cc` 27事件NoDrops；准确第4步首次Prune丢失 | 原策略DOP1/NoGC16；1–3真正Expanded，4横祸完整Nested已Generated/TT接受/动作准入但无PruneFinal。实际仍0损T10、13746/208100/147857，与v66工作量相同；诊断非质量或性能通过，下一次整池锚点设4 |
| 生成上下文普通席合同（已撤回实验） | v69 `e4981f5eb9494a26bcd65cc438849e78` SearchPolicySnapshot通过，Release零警告错误、两端66文件门禁通过 | 原路由/必留/额度未变；替身合同验证无碰撞旁路、完整标签隔离、必留槽位、细上下文去重、公平与确定性。因真实质量未达标，筛选及专属合同已撤回 |
| v69普通席Soul质量与拒绝版诊断 | 正常 `872c23e436754eb9af4a1ff2c3272513` 损17/T11；诊断 `4c30e68dcffd4b4fbd5a857a952584a3` 首次丢失准确第12步 | 正常主7399/69026/43629，请求7964/72340/44590耗满原20秒；诊断333事件NoDrops，11从普通席selected38真正Expanded，12生成/TT/动作准入后Prune丢失。无12整池原因证明。诊断损24/T7受时限影响，不当作正常质量或性能证据；未跑Exo哨兵 |
| 新有序牌堆键真实上下文合同 | v69 `ab2936c3280941148ff2b9d19d4f8a92` 通过 | `KNOWN-SOUL-GENERATION-CONTEXT-V0111`在第11/12步各得到五个不同有序键、相同无序键；26已知前缀及五变体完整/增量、根/live不变。无Solve或原版动作 |
| 外骨骼虫早胜约束完整回放 | v69 `9739c9b9d20f4924986e8ba0357935cf` 全24步通过，模拟97HP/0损/T5/0药 | v9的24步约束加同导入根v31真实生成候选的第4步4Nested；6Primary/4EndTurn，逐敌完整/增量、阵容与死亡账本、root/live不变。不是v9 PlanAction字节恢复，不是原版或搜索发现证明 |
| Soul生成上下文具体牌序 | v68 `8fc546a16f184e5f95c08a3b1d42e6bc` 通过，26已知前缀及五变体11/12步完整/增量、根不变 | `KNOWN-SOUL-GENERATION-CONTEXT-V0111`；差异仅Hand语义token顺序，其他三堆相同；同一冻结过牌动作后仍如此。不Solve，不把差异本身视为胜负或调度证明 |
| 外骨骼虫旧零损早胜约束重建 | v68 `34f71157c08f4711a02e4d842e86ab0c` 前3/24步通过，第4步横祸因未知Nested明确失败 | `KNOWN-EXOSKELETONS-ROUTE-REPLAY-V0111`，逐敌完整/增量及根不变；Source=CATASTROPHE/Hand/AutoPlayRepeated/必选1/四候选，旧ACTION无记录，不能默认选牌。旧0损T5在当前引擎尚未证明合法；无Solve/原版动作 |
| 同回合落选恢复边界合同 | v67 `589c10309b54482397fdd66162811c91` 通过；Release零警告错误、两端66文件门禁通过 | `KNOWN-CUSTOM-DEFERRED-FRONTIER-V0111`不Solve；19个严格前缀及18步恢复+末步，三次选择/一药。九种预算、停止、取消、异常及成功路径均完成；默认关/仅窄搜开合同通过。合成政策元数据不证明实际调度资格，TT不检查私有标签内容 |
| 置顶有序谱系Soul首结果 | v67 `5e037748973c4057b9118fc738855857` 损2/96HP/T7/零药，严格损1目标失败；因素已撤回 | 原20秒/VeryHigh/DOP1/GC，无观察器；5109/33202/16639，比原损19改善但尚未达损1/T4；有序保留用满2048。不改Smart/可接受战损停止规则 |
| 置顶有序谱系Exoskeletons哨兵 | v67 `b365cdf18acf4287a49ccb4a44c4fe4d` 0损T11，晚于当前v66 T10，因素撤回 | 13727/155781/97093，协议Passed只代表零损断言；不以此覆盖结束回合回退。只撤回置顶builder因素，保留v66恢复与新边界合同 |
| v67拒绝版Soul路径诊断 | `69e7e9443f4e4bf190145257b634fd7d`，419事件无丢弃，26前缀及根不变通过 | 准确第11步raw59/parent47/routing13/quota13，leader仍未进入最终Prune；未实际展开后缀。诊断结果损2/T7、5109/33202/16639与无观察器相同，非质量通过 |
| Custom同回合落选续搜 | v66 `f5685edaae3c416ab1ccd09c1746002c` 原配置首结果通过，T1/1HP/损3/1药/19动作 | 未注入已知路线；用药恢复77叶、77根+570前缀动作计入10742/12000节点额度。全部76快照值恢复核对；请求28736展开工作/97719转移/10447选择，包含无药失败恢复成本，非性能收益声明 |
| Custom新路线实际部署 | v66 `6d25ca7a3e6a4252adcc0d5fe902bf3b` 原生T1结束战斗、火焰药水使用、UnexpectedReplans=0 | 同配置自行搜索后正常部署19动作与三次燃烧契约选择；Instant/0秒与速度恢复检查通过。不是显式已知路线，也不是每前缀全状态差分证明 |
| Exoskeletons当前基线哨兵 | v66 `d698c918a51f462eb93a6dec1adb7753` 0损T10、13746/208100/147857，与post0300/v35同聚合质量和工作量 | 未启用窄搜/落选恢复。旧T5目标未完成；较早T9不是当前合并后基线，v62 T10不能据此再算当前回归 |
| 有界剪枝恢复合同（v54 原型） | `5b7cd7d6882b40eda490bf166bed78e2` 三组入口全部通过；Release 0警告错误，两端门禁66 | `BEAM-CUT-RECOVERY-V0111`：值存储/分页/公平/祖先/容量；协调器替身累计预算/零工作/取消；真实根上的合成节点保护与最终别名计数。没有正式Solve或原版动作，不构成Custom质量通过；同进程后续目标搜索结果另记 |
| Custom 剪枝恢复 v54 | 质量失败 `718a49fbfc5647aba0a656e9b17ad3ce`，HP0/敌347/4洗牌，未部署 | 原配置/首结果停止；总29994展开/112117转移/15259选择，6.614秒、4,189,131,112 B、GC253.254毫秒；共享合同进程峰值3,855,784 KiB。恢复两层分别花满12000展开，用药层2256候选无普通席。原型未达标，不以新增展开证明改善 |
| v55同药量quota替换 | 合同 `96268fdb5d184077a50ccb74fb7cb222` 通过；Custom `350a83e241b545faba1c06670c9abc17` 质量仍失败，原型撤回 | HP0/敌347/4洗牌；29994展开/112192转移/15259选择、6.372秒、4,193,335,816 B、GC6.964毫秒；共享峰值5,026,748 KiB。用药层no_seats=0，却只服务cut17→14第一页，97准入/64实际展开；没有后续哨兵或部署。代码和夹具不再位于生产/Testing入口 |
| 当前引擎Soul已知路线约束重建 | v56 `3cd33a96dd544421b8585c6a6566e420` 全26前缀通过，预计97HP/损1/T4胜利 | `KNOWN-SOUL-ROUTE-REPLAY-V0111`，原导入根，五次真实主选择绑定，完整/增量StateDiff与root/live不变，风险门通过；无额外选择。非旧PlanAction字节复原，未Solve/原版动作/性能，metrics为空；原生对照见v58 |
| 灵魂枢纽已知路线原版严格对照 | v57末击失败；v58 `3a85ce2f16654b6daf976fff1307b688` 全26前缀通过，实际T4/97HP/损1/零药水 | `KNOWN-SOUL-ROUTE-NATIVE-V0111`；先冻结26个完整预测，再执行26个原版动作、5次严格实例选牌、3次原版EndTurn；末击清理前取证并等待CombatEnded。不是搜索发现、生产UI自动部署或性能通过 |
| 灵魂枢纽已知路线纯值追踪 | v59 `4ad27f7d9345427097003a0130d6bb67`，131事件无丢弃；搜索仍损19/T7 | `KNOWN-SOUL-PATH-TRACE-V0111`；原20秒/VeryHigh/DOP1/Smart/GC，前10步生成并展开，第11步类星体选择深谋远虑在外层Prune首次丢失。按完整动作/选择及政策标签区分同状态历史，不向Solve传入已知前缀；仅诊断完整性通过，非质量或性能通过 |
| 灵魂枢纽首次裁剪整池 | v60 `a444d99408ed4145ac060f8fd2c27d36`，98输入/98真实排名/54最终保留、381完整事件；仍损19/T7 | 同入口及预算，仅新增指定外层池观察。目标零基raw67、routing60/quota13，非leader；低于普通截线且不在路由配额，required15/54未满。五个不同前序放回选择具有相同无序生成上下文；不当作策略已修复 |
| 生成选项有序分组 v61 | 灵魂枢纽DOP1/2损1/T4通过，但Exoskeletons损1/T4退化，方案撤回 | Soul `7fbba3fcc7aa4cf09e83b689ef4f63ba` / `a505c55d01b64ce39f3fa58830f5bfc6`：27日志动作、80项非时序/非调度字段相同，实际并发2；日志未序列化全部PlanAction字段。Exoskeletons `3605b08cd8214adb87b58323b40bea74` 零损断言失败，不用Soul单项成功覆盖哨兵回归，不保留旧leader被替换的分组方案 |
| 同分生成上下文伙伴 v62 | Soul损13/T7，Exoskeletons零损T10，方案及有序键已撤回 | Soul `9206ac3b8b0149f1b544c5b82848628b` 的第11步存活、第12步裁剪；Exoskeletons `7f3de2d204ad4fb0b0b056a32638312c` 协议Passed但旧零损T5质量审计退出1，27.528秒/12.178GB分配。v66已纠正当前合并后基线也是T10，不再按更早T9声称其回合数回退。只保留测试共享helper，不保留伙伴保路策略 |
| 自定义战斗已知路线纯值追踪 v63 | `bbb40f801677463ba44047a4b04b3a38` 诊断通过、128完整事件；实际搜索仍死亡/敌347/4洗牌 | `KNOWN-CUSTOM-PATH-TRACE-V0111`，原政策/DOP1；19个冻结前缀回放、shadow/live根不变，同一用药solver准确生成并展开首步。135宽前9个目标状态存活，第10步外层Prune丢失；60宽第6步丢失。第3步准确路线及第5步其他排列的TT拒绝均有同状态同6维标签代表继续展开，不当作故障；第3步Traits已不同，尚未证明别名完整后缀或调度历史等价 |
| 自定义第10步别名后缀及整池 v64 | `49a3fb9418d644d3bb4f158b8e0911ec` 诊断通过；实际搜索质量仍失败 | 1个真实Generated别名原根回放及9步原样后缀逐步全状态/增量等价，T1/HP1/损3/1药胜利；非调度历史等价。1024无丢弃事件，355输入/排名、135全局/177最终保留，目标raw172未进route96/quota69，required90；同上下文raw17以更多即时伤害先入。原搜索9412/35298/4699未改变；构建及两端65项门禁通过 |
| 持久选择上下文SetupFirst v65 | Custom `127761331d114c96b870d61786e6991c` 质量失败，实验撤回 | 原配置/DOP8/NoGC16/无观察器/首结果停止；仍死亡/敌347/4洗牌，9353展开/35073转移/4755选择。只改同context候选顺序且保持集合/评分/预算，不足以恢复完整解；未跑哨兵或部署 |
| 遗物属性在末击后的命令边界 | v58 `b9edcf8d2c3a416baec2caa6505ea610` 两个最小边界、三遗物均通过 | `relic-stat-terminal-v0111`；苦无/手里剑/彩虹戒指在非致死动作正常加属性，致死动作仍递增计数但不施加属性；原版与全根/增量完整状态一致。每个根只打一张牌，不运行Solve |
| Windows helper 预约自测 | `tools/testing/test-headless-runtime.ps1` 通过：双 parallel、exclusive、资源不足、未知游戏、归属、stale、warm | Linux 上的 PowerShell 替身测试；非 Windows 游戏进程或快照实测 |
| Windows资料复制边界 | `-ProfileOnly` 通过：私有拷贝、源资料不变、重解析点拒绝 | Linux上PowerShell文件系统验证，不是Windows游戏验证 |
| 两端结构门禁 | Bash / PowerShell 均 `REFACTOR_BOUNDARIES_OK search_files=64` | 只证明结构边界 |
| Linux helper 原生子进程生命周期 | 11项通过：并发、同实例拒绝、排队、独占、warm、取消/超时、pending/孤儿、stale、PID出生及未知进程 | 真实辅助层 + 私有原生sleep；枚举限定测试域，非游戏协议 |
| Linux 快照隔离 | `--snapshots` 4项通过：A/B不同DLL内容、A更新不改B/源树、旧快照保留、活进程拒绝替换 | 文本DLL替身，不证明实际程序集加载 |
| Linux 快照故障注入 | `--snapshot-failures` 9项通过：find、中间SHA、rm/mkdir/cp、retired mktemp/mv、publish mv、ID mv | 错误显式传播，不越界移动、不形成新game/旧ID错误缓存；旧树仍可恢复 |
| 真实双 headless | PID3841962 / PID3842057 的请求区间重叠约23.7秒；Fork通过 `3ca7afc55dc44476bf13f0ebf2ab6a7b`，Start旧DLL按预期失败 `bd5e0bbd45cd4698aa090070c67fa333`，各自退出 | Linux私有游戏/Mod/协议；不是单场性能对比，也不声称两个语义fixture都通过 |
| 真实静稳复用 | v43差分Passed→Ready→同PID3847699最小根检查Passed并ExitOnComplete；后者 `7ebee077e8e544cc8d0f1e713ea6816e` | 未真实测试Held或Windows；取消/故障互不误杀的细分证据来自原生替身 |
| 矩阵实例传递与清理 | 两端 `test-headless-matrix-runtime` 通过实例/参数传递、暖实例尾部stop、外来身份拒绝、同实例及取消隔离 | mock场景入口；PowerShell取消为适配器测试，不等于Windows原生Ctrl+C |
| stop-only真实入口 | Linux原生替身通过只停本实例、peer保留、stale/absent幂等、未知/PID复用/无marker/已有producer拒绝；PowerShell入口通过stale/absent/noPID/foreign-pwsh边界 | 缺DLL/依赖仍不创建request/profile/snapshot或启动游戏；不是Windows游戏生命周期实测 |
| 真实暖游戏stop-only | v49合同请求Passed→Ready后，精确停止PID4049888；故意指定不存在的构建/源游戏/依赖路径仍成功 | 旧结果 `be1553a145b64023b7d44ceab1ff1456` 保持，PID和marker消失，未创建指定目录；不再发布游戏请求，不代表Windows实机通过 |
| 回手 Start 根修正 | v43 `bf4fa60a07764a28ab452b93d203f9b3` 7项严格状态对照通过 | 包含三次PreDrawStartRoot与原版/连续/Fork/中途根，不是整场搜索 |
| 水银沙漏/千足虫死亡状态投影 | v44失败 `65d54627f8f3435da583ef159763e24a` 仅全灭后的MS0/1误分类；v45修后 `f654b4ad4a1c429f88e4739ff82a8b1a` 8项通过 | 一段1HP、两段原生复活资格；REATTACH→0/22/22不赢，DEAD→全灭；直接/Fork/重捕获根严格状态对照，非搜索排序或错误胜利复现 |
| 跨回合计划终局回合数 | v45基线 `0ae11f4b7f584d819d60d33cb928d276` 错报T1；v46 `8d53449e35b4407fa0a4d4c03164a955` 正确T2；闪电球末尾对照 `bf20264541ab457f975151c5846f4973` 仍T1 | 正式短搜+增量回放；标注、排序共用原版安全点锁定的玩家回合号，不统一给EndTurn加一 |
| 千足虫终局标记与严格状态 | v46 `15b7787fa6ba4dd6bc041fc79cbab81a` 两阶段8项通过 | 一段1HP、两段原生复活资格；额外断言终局Fork与首次锁定不覆盖；非整场性能/搜索质量结论 |
| 敌方开局中毒终局对照 | v45 `2ac6c15441594cb199551ead687fd534` 与 v46 `fd2a5d8612344f18a5a8304de814ae31` 都返回T1 | 修后正式短搜+增量回放，没有因EndTurn误加一；与沙漏/闪电球共享短请求进程，非独立性能A/B |
| 普通打牌强制结束后的终局 | v47 `2414e7137a554f54b8037df5b8adb89c` 6项通过 | 唯一出牌动作原版/根回放/增量回放严格对照，T动作触发T+1沙漏击杀；正式增量断言比较终局标记，Fork保持，释放快照后正式标注仍T+1；无搜索展开/性能结论 |
| Soul 语义修后搜索基线 | v47 `1f43a8bab8fd402baedcf600d4c70397` 质量失败：掉血19、79 HP、T7 | 原20秒/DOP1/NoGC关闭/VeryHigh；总8598展开/64393转移/31687选择；未再出现旧牌堆标注差分，但未达到历史掉血1目标，后续策略实验以此当前基线单因素对照 |
| Custom 语义修后搜索基线 | v47 `d0b4125e662f40ccabed12604aac9ebf` 仍只有死亡路线、敌剩347 HP、洗牌4次；未部署 | 原VeryHigh/DOP8/NoGC16，9412展开/35298转移/4699选择；搜索4.641秒、分配1,403,332,960 B、GC132.458毫秒、VmHWM2,285,236 KiB；未达到历史T1获胜目标 |
| 新鲜选牌父名次同分实验 | v48局部节点合同通过，但Soul质量退化至掉血47/T9（`70f18bd55eda4f7a8f4e6662ef3241c3`）；方案撤回 | 只作失败实验记录，不作为现行策略或通过证据 |
| 拥挤策略与普通候选仲裁实验 | v49合同通过 `be1553a145b64023b7d44ceab1ff1456`；Custom仍死亡/敌347/4洗牌（`c16bbcbbc2564e3eb3b21365d70607fe`），未解决目标，方案撤回 | 9142展开/34420转移/4689选择；4.069秒、分配1,355,445,536 B、GC146.625毫秒、VmHWM2,339,800 KiB；不将少量加速当修复，不执行后续Soul哨兵 |
| 当前引擎重建Custom已知解 | v50 `d519b3ca52f947c89d0ace26dbe9ede6` 通过19个前缀严格增量/全根回放，预计1 HP/损3/T1/敌灭/7洗牌/1火焰药 | 依据v29动作与实际三次单选记录，在当前原归一化根绑定完整卡牌/选择身份；不是旧PlanAction逐字反序列化。原模拟根及实战完整状态保持不变，未Solve、未原生部署、metrics为空；证明当前模拟器能表达该解，不证明搜索已找回它 |
| Custom已知解预测风险门 | v51 `fc247b55b2dc4a04990e1deda3cb773d` 全19前缀及终局通过，额外要求 `HasRisk=false` 且无未补偿PredictionGap | 补强模拟可行性证据；仍未Solve或原生部署，不计作搜索质量成功或性能数据 |
| Custom已知解原版严格对照 | v52 `83d25eef53f847df81d98f0cf18aea4d` 全19个原版动作/冻结预测前缀通过，实际T1胜利、1 HP、损3 | `KNOWN-CUSTOM-ROUTE-NATIVE-V0111`；同一归一化根，原版ManualPlay/EnqueueManualUse，三次选择核对完整实例状态与两个游标，末击原版清理前取证；头部无Solve指标，不当作搜索已找到路线或自动部署通过 |
| 后台Gen2检查点生命周期 | v52 `6c1ef5e88b67423b9e6a30406e089b50` 8组合同通过，复用PID4085610后退出 | `GC-CHECKPOINT-BACKGROUND-V0111`；正常确认/重建、同步上下文、取消与晚manual/引用释放、注入超时排空、旧早manual/开始前失败/epoch捕获前后；确认窗口可暂停但不控制CLR mark。普通完成实测background，超时兜底blocking；非长线性能A/B |
| Custom固定工作量回收A/B（1 GB） | v51同步 `841a2372b6bc4436b34b14d455fc0e02` / v52后台 `e9c77ba6a6394a6bbf604ebae20d018d`；两侧原胜利断言仍失败 | 同根/VeryHigh/DOP8/Smart；52项非时序字段、9412展开/35298转移/4699选择及13条日志动作一致（日志未序列化全部PlanAction字段）。22检查点均重建成功，新版22次background；计账5.481→5.301秒，GC累计/最长1601.244/104.588→46.818/4.977毫秒；峰值1,631,036→1,774,676 KiB（+140.3 MiB）。不当作已找到合法胜利或Windows长线性能通过 |

维护时默认使用分层快速回归：普通语义改动跑单效果严格差分；Fork、跨回合历史和续用改动补一个最小两回合或最早复用边界；搜索/部署改动的最终候选才运行必要的完整自动场。快速 unattended 请求总超时不超过 `120` 秒，超时后缩小 fixture 或记为未验证，不在同一轮延长等待。下方完整矩阵是发布门禁和专项审计入口，不是每次修复都要执行的默认清单。

## 未发布：GC 独立研究（2026-09-05）

固定上游 `5c4b69d`，版本不变；设置与隔离方式见 [研究起点](../performance/gc-issue36-research.md)，最终实现、候选取舍和 A/B 口径见 [实施报告](../performance/gc-issue36-implementation.md)。首轮 250 节点记录仍只是单次 pilot；下列最终 A/B 使用独立冷进程、固定节点预算和三次中位数。最终长搜及 Smart 样本的完整 ACTION/TURN、工作量和非时序剪枝比较均通过；NodeLimit 结果不代表完成整场。

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `GC36-ROUND2-SNAPSHOT-LIST` | 通过（实际 helper / 7项） | 嵌套、异常填充、单槽/reset、容量上限、旧/复制lease、owner隔离、弱引用释放。命令见 tools/testing/checks/SnapshotListBufferChecks/README.md。 | 2026-09-05 |
| `GC36-ROUND2-AB` | 通过（三次交替，固定工作量等价） | Silent 分配中位 −3.39%，Necrobinder −1.34%；完整 ACTION/TURN、评分、工作量和非时序剪枝一致，耗时无稳定收益。逐轮runId见 [第二轮证据](../performance/gc-issue36-round2-results.json)。 | 2026-09-05 |
| `GC36-ROUND2-BOUNDARIES` | 通过（新 parallel 入口） | SearchPolicySnapshot / ForkBoundaries，含DOP1/DOP2等价、实际并发2及历史/根边界；runId `5171caca9cf84baaa3f48884644f3b07`。该并行样本不用于性能。 | 2026-09-05 |
| `GC36-ROUND2-TRACE` | 产物严格解析通过，runner停止等待超时 | 搜索18,572 allocation ticks，Fork权重43.93%；0缺栈/全未解析/报告丢失，19条含部分未解析帧。清理SIGTERM完成采集，完整经过及失败尝试见 [报告](../performance/gc-issue36-round2.md)。 | 2026-09-05 |
| `GC36-ROUND2-SMART-SOFT` | 通过等价检查，未采用生产接线 | 关闭/512/192MiB各1个exclusive冷进程，回收0/1/2次、loss0；降低峰值但增加暂停与耗时，不作为默认阈值排名。 | 2026-09-05 |
| `HEADLESS-INSTANCE-HELPERS` | 通过（Linux替身） | 租约13组、快照隔离4组、失败注入9组；含暖进程剩余预约与token/出生身份变化。无真实游戏语义结论。 | 2026-09-05 |
| `GC36-PARALLEL-A/B` | 通过（两个真实独立进程） | 建局/退出均Passed，请求区间重叠23.47秒；runId `3f8b0b3f6bcc47ca82528f8528ccb486` / `a97addbd1bf34400afe13f33342013bf`。未请求额外root断言，不使用耗时做性能结论。 | 2026-09-05 |
| `GC36-RELEASE-BUILD` | 通过（Linux） | Release 构建显式设置 `CopyModOnBuild=false`，0 警告、0 错误；输出仅复制到本任务的隔离 mods。 | 2026-09-05 |
| `GC36-SILENT-250-GC` | 通过（headless pilot） | DOP1 / 普通 GC / 每 solver 250 节点；selected 展开/转移 250/1426，请求累计 500/2510，265,265,168 B worker 分配；runId `2f721baf127c41aaa0646dc50e46e754`。NodeLimit，未完成整场。 | 2026-09-05 |
| `GC36-NECRO-250-SMART-NOGC4` | 通过（headless pilot） | DOP1 / Smart / NoGC 4 GB / 每 solver 250 节点；请求累计 750/7540 展开/转移，432,608,568 B worker 分配；两次层间回收暂停约 102.4 ms。runId `129de2c8d3ea47649c61c5b6eb865566`。NodeLimit，未完成整场。 | 2026-09-05 |
| `GC36-FINAL-BOUNDARIES` | 通过（headless，最终候选5） | Fork/历史/根快照、取消工作量只记一次，以及 DOP1/DOP2 的路线、评分、展开/转移和非时序剪枝等价。runId `9c4b36665ce240f185e4c722c024ff23`。listener slot 已撤回。 | 2026-09-05 |
| `GC36-AEONGLASS-PREVIEW-OWNERSHIP` | 通过（两步 native 严格差分） | 先只读判型、仅 Wither 写入；第一次生成凋零总伤害6/力量3，第二次升级并生成后总伤害18/力量7。非 Wither preview 身份不变，未执行兄弟的 preview 身份与凋零伤害不变。runId `825d477edaa0456b91934583498388ba`。 | 2026-09-05 |
| `GC36-FINAL-LONG-SILENT-GC0-DOP4` | 通过（三次 A/B，路线/工作量/剪枝等价） | 每 solver 2,500 节点，请求5,000展开/19,065转移。基线→最终中位：分配2.805→1.829 GB（−34.8%）、时间13,672.4→10,484.7 ms（−23.3%）、暂停3,007.0→1,280.3 ms（−57.4%）、VmHWM2.117→1.684 GB（−20.4%）。最终 runId `d45e986c9fe0490f8a1f03afcb0fecdd` / `658c7729a5a54188bc76bca1a0c55f6c` / `04e508c3b433429ca84b31a69d788576`。 | 2026-09-05 |
| `GC36-FINAL-SMART-NECRO-NOGC4-DOP4` | 通过（三次 A/B，有峰值代价） | 每 solver 576 节点，请求1,728展开/22,541转移，路线/工作量/剪枝等价。基线→最终中位：分配1.302→1.296 GB、时间5,113.8→5,074.9 ms、暂停153.0→0 ms；VmHWM1.999→2.824 GB（约+0.825 GB）。最终 runId `356f302ce2fb400e9b67834e3989167a` / `c3810131b74546949076723dd3e6769b` / `4bc38f581c224830a51b1548d385744a`。 | 2026-09-05 |
| `GC36-LISTENER-SLOTS-EAGER-LAZY` | 已拒绝并撤回生产 | helper/游戏 Fork 顺序检查通过，但 lazy 版2,500节点长搜分配增加20.81%，主要反增位于敌方动作中的密集 preview 更新。仅撤回 listener 的候选4控制样本恢复2,801,108,024 B，路线/工作量/剪枝相同。代码归档于 [ExperimentalListenerSlots](https://github.com/Torch1230/CombatSolver/blob/556e72994303e45ca2b2833aa09ba793d1b096cb/tools/ExperimentalListenerSlots/README.md)。 | 2026-09-05 |
| `GC36-FINAL-PRESSURE-1GB-DOP8` | 通过（合法低预算，单次） | 请求1,728展开/22,541转移，完整路线/工作量/剪枝等价；5,236.773 ms、1,279,327,296 B、暂停160.357 ms、VmHWM1,963,995,136 B。两个 Smart 层因 forecast_exceeds_remaining 回收，forced/start/end/restart/loss=`2/3/2/2/0`。runId `511d89a5ce1c4b378d20fc7bfc90c256`。 | 2026-09-05 |
| `GC36-FINAL-PRESSURE-0.6GB` | 设置校验拒绝，未执行搜索 | 低于1 GB最小设置，runId `a68a6be47d404e7195e45d3fa47af7d8`；不计为搜索失败或性能数据。 | 2026-09-05 |
| `GC36-AFTER-PRUNE-PRESSURE` | 代码审查，未有命中实测 | 保留有下一次 parent 准入时才在剪枝后回收的 guard。admitted_parents 同时受过滤和自然 frontier 影响，不将每个缩小的 wave 都归为内存压力事件。 | 2026-09-05 |
| `GC36-ADAPTIVE-WAVE-EXPERIMENT` | 已撤回生产接线 | 15项合成决策检查通过，真实单轮实验暂无收益依据；控制器和补丁归档于 [ExperimentalAdaptiveGc](https://github.com/Torch1230/CombatSolver/tree/556e72994303e45ca2b2833aa09ba793d1b096cb/tools/ExperimentalAdaptiveGc)，检查仍可运行。 | 2026-09-05 |
| `GC36-FINAL-ADAPTIVE-SILENT` | 通过等价检查，候选未采用 | 每 solver 2,500节点，55个完整窗口、无probe；请求5,000展开/19,065转移，完整路线/工作量/剪枝等价。耗时11,114.4844 ms，相对最终长搜三次中位增加6.01%；单轮不作稳定退化或提升结论。runId `7ced6eed76f745968f0134743d308296`。 | 2026-09-05 |
| `GC36-FINAL-ADAPTIVE-NECRO` | 通过等价检查，候选未采用 | 每 solver 576节点，35个完整窗口；最后层probeLower=1/rejected=1，4→2核吞吐比0.638、GC duty 0.258→0.146，拒绝后恢复4核且无pending。请求1,728展开/22,541转移，完整路线/工作量/剪枝等价。耗时5,994.7884 ms，相对同代码单轮对照增加1.72%；单轮不作稳定退化或提升结论。runId `20a59a45c3154712981125be355787ae`。 | 2026-09-05 |

最终源码保留 StateStore/空 dirty 查询、历史引用解除、有界 batch storage 复用、原序前缀释放、GC scope 指标、按余量准入、Smart 预测回收及只读判型修复；listener、普通 GC 自适应并发、通用 StateStore COW/typed buckets 和 compact/undo/page COW 内核均不进入生产。VmHWM 为包含启动/建局的进程峰值，GB 使用十进制单位；本轮未完成 Windows、可见 Steam 或完整自动战斗验收。

## 0.30.0：Checkpoint 日志与回放入口重做

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `CHECKPOINT-BATCH-REUSE-0300` | 通过 | 正常/环境错误/正常三请求同 PID 22388，run 内耗时约17.1s/19.9ms/1.7s；后续 Resume 三项全部复用。证据 `.local/replay-validation/batch3/reuse-fixed/`。 | 2026-09-06 |
| `CHECKPOINT-BATCH-DIFFERENCE-0300` | 通过 | 故意修改 ChooseCard 候选状态，在 eventCursor=1 返回 recorded_action_mismatch，difference.json 保存三事件窗口及候选；下包重启到另一 PID 并通过。证据 `.local/replay-validation/batch3/difference-results/`。 | 2026-09-06 |
| `CHECKPOINT-LEGACY-PREFLIGHT-0300` | 通过（材料） | 200 ZIP 和2已解压旧包全部识别开战四材料；约5秒完成批量索引和汇总，没有逐包运行恢复。 | 2026-09-06 |
| `NATIVE-SPOOL-NESTED-0300` | 通过（录制前缀） | 工具箱与低语耳环、同名生存者与杂技建局；导出5623740223b9434abfa60ef462099260，回放d3a52fce86f34cc29aeb22a45bb6c10d。顺序文件及选择候选对账通过，记录3个外部/Hook事件，其余自动出牌由原版重建。 | 2026-09-06 |
| `NATIVE-OPENING-CHOICE-HANDOFF-0300` | 通过 | ed4e8d241b8c433e966f2e8a8e334a8a，开战原生状态一致后释放录制选择器，由求解器选择开局路线并完成 SearchOnly，固定1500ms短搜。此前等待用户启动造成的超时已修为测试器明确提交接管。 | 2026-09-06 |
| `VISIBLE-LOGGING-ROUNDTRIP-0300` | 通过 | 可见Steam六回合导出bf92cb1c0c284f23ab9d1fc58d9c4b4d；同包22事件回放1a633135013a4aa4bc2bdead00d79e75，开战求解器部署3f43a41babd54993a7aaa680c38e4ac5，均实际6HP/0药，严格原生状态与续用状态一致，部署零计划外重算。 | 2026-09-06 |
| `VISIBLE-LOGGING-COLLECTION-0300` | 已测量 | 最终事件累计0.8444ms/最大0.4235ms，峰值积压416B；14次检查点调用累计102.2237ms/最大26.7889ms，峰值积压2，保留6快照，ZIP326162B。未证明相较初次测量显著加速，不作“无卡顿”结论。 | 2026-09-06 |
| `CHECKPOINT-NEGATIVE-CONTRACT-0300` | 通过 | 工具29项断言，覆盖材料缺失、重复、目录穿越、配对错用、诊断包/录制缺项、顺序文件前缀及容量、JSONL断尾恢复、技术失败优先、实际相对人工+3、混合/未完战/不同药水政策拒绝比较。冷启动10秒超时实测正确写timeout并清理进程。 | 2026-09-06 |
| `NATIVE-REPLAY-COMBAT-0300` | 通过 | 原生四回合16事件重放，runId `55b8fd0e302f4151be6878c8c24273a5`，完整续用状态一致；进入/回放阶段约961ms。战后回血与清牌之前的相同结束边界对账。 | 2026-09-06 |
| `NATIVE-REPLAY-SETUP-CHOICE-0300` | 通过 | 工具箱开局生成及选择3个原生事件，runId `f97cf39891864fabb517e95b3def281f`；选择器限定在录制回放作用域，跳过求解器的页面接管，不启动搜索。 | 2026-09-06 |
| `NATIVE-REPLAY-MIXED-0300` | 通过 | 刀刃之舞生成牌及力量药水，runId `e866e6c7c24f4223a5c3b20e856c4740`，10事件，原生二进制及续用状态一致，实际0HP/1药，零重算。 | 2026-09-06 |
| `NATIVE-DEPLOY-FROM-OPENING-0300` | 通过 | runId `e716aea669114d199433daeb1bb8430f`，从原生开战恢复并通过二进制状态对账，求解器T1至T4实际5HP/0药、计划外重算0；记录实际政策及预测指标。 | 2026-09-06 |
| `LEGACY-OPENING-RANK3-0300` | 通过（开战检查点） | 旧实验体ZIP直接选择start，runId `651be9ae9c4a4e998cac5d9f9c28e774`，17.0秒，通过完整续用状态及原生二进制对账。原生加载跑局保留遗物池和存档属性；校验时点与原始开战导出一致。 | 2026-09-06 |
| `ARCHIVE-CONTRACT-0300` | 通过 | `dotnet run --project tools/replay/CheckpointTool/CheckpointTool.csproj -c Release -- self-test`，12 项检查覆盖同名文件分离、稳定默认入口、战后选择、旧包无索引、会话错配、重复及不安全路径。 | 2026-09-06 |
| `ARCHIVE-V2-EXPORT-0300` / `ARCHIVE-V2-IMPORT-0300` | 通过（检查点） | 导出 runId `7ccbe05b91c441d3a7ff5ebea12ee660`；相同 ZIP 直接导入 runId `30f462a75df5456286fddae1144736aa`，`CheckpointContinuationMatched`，材料准备约29ms。没有运行搜索或整场部署。证据 `.local/replay-validation/batch1/`。 | 2026-09-06 |
| `CHECKPOINT-INDEX-0300` | 通过 | 导出 runId `b309d78548cd46708a8dcf008af8bd40` 生成唯一 `combat-solver/checkpoint.json`；索引指向的 metadata、replay-state、native-state、run-state 均存在。再以同一 ZIP 直接导入，runId `15fa0e74fee74af787116be3c2bf2dae` 通过开战根状态断言。 | 2026-09-05 |

> 上述日志批次未逐项实测全部怪物召唤/复活、能力内部引用和所有嵌套选择；缺乏完整记录的旧中途包可能返回具体恢复差异。Linux 脚本完成 Bash 语法及同源边界门禁，未在原生 Linux 游戏进程中运行。本轮不修改世界线策略清单，不以日志测试声明策略提升。

## 下一版本（开发中）：循环、顺序选择与回收边界

当前回手和终局时点定向修复已验证，优先继续搜索质量实验；完整进展以本文顶部最新证据为准。以下按历史阶段记录失败定位过程。v36/v37 临时诊断均已从源码撤除；触发来源 `PR-V37-SOUL-ROUTING-TRACE` 最终标注回放严格消耗堆差分失败、metrics为空，其较早剪枝日志只用于定位，不作为整场质量或性能通过。

| 回手边界 | 证据 | 验证范围 |
| --- | --- | --- |
| `PR-V38-RETURN-ORDER-BASELINE` → `PR-V39-RETURN-ORDER-FORK` | 失败 `0f65140fc6904b699556205a9d6d225d` → 通过 `1d704ce83b7f4208b01a9aed448759c6` | 第二次连续回手此前与当前弃牌堆次序相反；修后通过 `ForkBoundaries`、`CombatRootSnapshot`。独立影子根、资格回调、两次回手、跨牌堆顺序和移除后引用检查，不启动搜索。 |
| `RETURN-TO-HAND-ORDER-V0111` → `PR-V40-RETURN-ORDER-ACTUAL-RETRY` | 中途根失败 `e82c6aa15cbf4ab488411446bf1c0173` → 通过 `7726954cb8e34c69a3269ea938921e7c` | 原版手动出牌与抽牌前 Hook 严格对照连续、逐步 Fork、中途根、已消费历史根。正序、反序、不重新打出不得再次回手及总完成标记均通过，finishedTurn4、metrics为空。独立边界推进，无敌方行动/常规抽牌/搜索/部署；Start-phase 根尚未覆盖。 |
| `PR-V41-RETURN-MEMBERSHIP-BASELINE` | 失败 `6379c0eb127448ed9ee1ff04dc82bda6`，修后待验证 | 同根两张同名同升级牌仅第二张重放次数不同。保持两分支完整有序牌堆和历史不变，仅交换回手资格指向，实际得到不同后续牌堆但战斗指纹相同。包含无资格及同资格 Fork 控制，不冒充原版历史回放。 |

Start-phase 扩展测试在同一专用夹具中，于回合推进后、抽牌前分别捕获瞬时根，覆盖正序、反序和未重打负例，不重复推进新根回合。v42早先三次启动因其他任务游戏被拒绝；完成隔离后已取得v42失败基线，v43修后7项检查全部通过（含三项 `PreDrawStartRoot`，见顶部）。这不证明任意抽牌中途的Start根捕获。

2026-09-05 已同步上游 `0.30.0` / `e49aa18`。下述 v30–v33 为合并前本地实验编号；拉取完成后已建立以下新基线，仍有质量失败，不能沿用旧通过项宣称当前版本通过。三项请求总截止均为120秒，没有为失败延长超时。

| 合并后基线 | 质量结论 | 请求总工作量与成本 |
| --- | --- | --- |
| `PR-POST0300-EXOSKELETONS` | 协议通过、质量失败；零损/97 HP/T10/敌灭，目标零损T≤5。runId `38b24975ee1043c4ac15949156cb46e2` | 13,746展开/208,100转移/147,857选择；29.011秒、分配12,549,318,408 B；GC总/最长969.852毫秒，VmHWM 11,210,724 KiB。 |
| `PR-POST0300-CUSTOM` | 失败；死亡HP0/敌347，未完成部署。runId `45e637d4ed964734b94d482f425dd23a`。首条错误为洗牌4<7，但实质是未找到胜利，不放宽断言掩盖死亡。 | 9,135/34,553/4,610；3.517秒、分配1,365,577,712 B；GC总/最长100.685毫秒，VmHWM 2,276,360 KiB。 |
| `PR-POST0300-SOUL-DOP1-GC` | 失败；损13/85 HP/T9/敌灭，目标损≤1且同损T≤4。runId `b2b6e0fa2f8440e6ba2888967f81efa7`。启动前去掉精确T4，改以获胜和独立战损/回合审计允许真正更早获胜。 | 7,886/59,699/31,255；19.991秒、分配3,359,506,264 B；GC总2,753.657/最长54.098毫秒，VmHWM 1,592,312 KiB。 |
| `PR-V34-EXOSKELETONS` | 实验否决且已撤回；损1/96 HP/T4/敌灭，违反零损目标。runId `64ec100b55ca420d8e512eba93e09ca4`。完整组合身份/家族公平前缀不记为有效修复，新增单元合同尚未运行。 | 4,271/78,678/57,325；12.780秒、分配4,704,011,536 B；GC0，VmHWM 6,223,180 KiB。不能以更快更省内存掩盖战损退化。 |

以上均为独立 headless 进程；总分配不是峰值内存，不作为 Windows 可见游戏的提速结论。初始牌堆及玩家/敌人摘要与旧通过根一致；Exoskeletons 新 Power 导入写入0项，Custom/Soul 的新增赋值与旧严格续用根已有值相同。旧 Exoskeletons ACTION 缺嵌套选择，仍不能称为已在现引擎完整重放旧24步路线。

以下是本分支已执行的定向证据。只复用其后输入和所覆盖行为未改变的结果；最终宽预设和请求级搜索回归仍单列待验证。fixture 中的具体卡牌仅构造测试输入，生产调度依据通用状态与收益。

最新源码为 v30 无主动用药胜利 incumbent 候选，构建0警告/0错误、64项结构门禁及策略/8项最小搜索回归通过。下列 v29 真实样本结果仅作先前源码阶段证据。v30 Exoskeletons 原搜索配置复测协议Passed但零损/T9劣于旧零损/T5，质量FAIL，PR暂停；20秒短诊断与EventPipe仍不作为正常配置性能或同条件A/B成绩。

v32 fresh-option 战术同分实验也未改善零损T9，质量审计失败后仅撤该实验恢复v30。当前没有通过验收的组合启动保路修复；以下诊断或单元合同不充当完整旧路线复现与质量成功。

v33 标准窄预设诊断 runId `adb8c4f778104bb5a0264a4726be8217` 已结束并因损1/96 HP/T4违反零损断言而失败；不同预设不能与原配置比较性能，不作为已恢复质量的证据。

旧来源请求的协议断言不等于最终质量门槛；保持已启动输入不变，再人工核对非死亡、敌人全灭、战损优先及同损不增加回合。Phantasmal 须零损且T≤3，Exoskeletons 须零损且T≤5（旧请求仅限损≤1），Infested 须损≤4且损4时T≤5；长线对照须损≤9且损9时T≤9。Kaiser 按最早T2续用、复用预测零损及零计划外重算的部署边界验收，不由 Passed 推断整场完成。Aeonglass 与长线对照是不同输入根，不混用质量基线。

Smart 验收沿用当前上游政策：按无药基线和药水价值门槛逐层搜索，首个可接受获胜层或设置的战损阈值可以提前结束；记录实际层数和总工作量，不要求固定三层全搜索。最新抽牌修复移除分支历史累计 `100` 次的截断，`100` 仅限同步递归深度并在无法继续时明确失败；新增语义断言已由 v21 Fork 验证，长循环的 v30 定向搜索已通过，长线 GC 对照仍待完成。

v30 SearchPolicy 与下列8项最小搜索夹具在同一 headless 进程复用暖缓存，批次退出码0，监测整程最大 `VmHWM=1,928,528 KiB`。各项耗时/分配仅描述该次搜索，不是独立冷启动；不把共享进程峰值分配给单项，也不作为可见游戏性能结论。结果中的预计终局与完整自动部署分开表述。

| 场景 | 当前证据 | 验证内容 |
| --- | --- | --- |
| `PR-V21-FORK` | 通过，可复用语义证据 | `ForkBoundaries`、`CombatRootSnapshot`；runId `7314b9b4cc164984a706b7d39438f155`。覆盖 DrawLifetime 的超过100次合法抽牌、兄弟 Fork 独立历史及同步递归失败/释放深度，同时覆盖 AfterAttack 和既有待处理选择边界；其后仅有 Search 修改。 |
| `PR-V22-STRICT-KAISER-NESTED` | 通过，严格差分 | 嵌套自动出牌比较完整预测/实际状态，包括有序牌堆和 RNG；runId `1fff3b29c5854d8ca5a8779ab08a4a49`，完成检查 `FuzzyWurmCrawler:INHALE`。输入见 `mayhem-decisions-metamorphosis-nested-order-v0111.json`。 |
| `GENERIC-LOOP-STAGNANT-V30-DOP1/2` | v30 通过 | runId `3cfd5703aa0d49bcbf0cc81b27d053de` / `f0510219758f483fa0addbdcdb496e4b`：均 `10/20/0` 展开/转移/选择，有界探测后停止，最终0动作、不采用空转；搜索 `5.686/4.160 ms`、分配 `1,236,200/1,023,120 B`。两者 NoGC 配置不同，不作纯 DOP 性能比较。 |
| `GENERIC-LOOP-RAMPAGE-GROWTH-V30` | v30 通过 | runId `5d177f8527a4418a8d5577eb244fcf3d`：`64/130/2`、32动作/15洗牌，预计T1零损/80 HP/敌人全灭，`boundary=None`；`17.113 ms`、分配 `7,000,504 B`。正常抽牌没有历史累计次数上限。 |
| `GENERIC-LOOP-LONG-HIDDEN-PHASE` | v30 通过 | runId `30cc729ff61944ba907636d04b8a0820`：`1,200/2,400/0`，1,200动作及洗牌，预计T1零损/80 HP/敌人全灭，`boundary=None`；`0.612 s`、分配 `192,420,096 B`。 |
| `GENERIC-LOOP-LONG-GROWING-DAMAGE-V0111` | v30 通过 | 50万HP、超过256周期，runId `5d70d82fbc8a4198b0fdf1f18120921e`：`1,784/3,570/2`、892动作/445洗牌，预计T1零损/80 HP/敌人全灭，`boundary=None`；`0.766 s`、分配 `226,789,904 B`。动作/形状/伤害相位重复但伤害值可变，逐步刷新耐久低点，没有外推伤害。 |
| `GENERIC-CROSS-TURN-POSITIVE/CONTROL-V30` | v30 通过 | 正例 runId `87b23d759fdc46238e807b2722340b75`：`513/770/0`、预计T17零损/999 HP/敌人全灭，`74.548 ms`、分配 `36,748,288 B`。停滞对照 runId `9231490c20c14630a2885d2c0fa32160`：`78/117/0`、最终0动作且不采用纯防御空转，敌人仍57 HP，不是获胜；`15.009 ms`、分配 `6,142,760 B`。 |
| `GENERIC-LOOP-ZERO-LOSS-QUALITY-V30` | v30 通过 | runId `12fc55312bfd47738ae11cf4f0a02eaf`：`20/57/0`、5动作/4洗牌，预计T1零损/80 HP/敌人全灭；不选不必要的卖血动作。`8.076 ms`、分配 `3,020,680 B`。 |
| `SEARCH-POLICY-V30` | v30 通过 | runId `f79be2a0a3764d44ab869e2915c407a8`，完成 `SearchPolicySnapshot`：新无主动用药incumbent资格、旧exact药层门槛及严格下界断言已执行；保留普通截线、共享服务、付费/弱引用隔离、实际NoGC回退、生命周期与固定250节点DOP等价检查。不是实测弱引用GC节约。 |
| `PERSISTENT-CHOICE-ORDER-LETHAL-V29` | v29 DOP1/2 通过，完整路线等价 | runId `91f47065d7ef4016956c66193be37f53` / `3dbdca220d084e20ac73eca942d38fc1`：均 `4,439/17,886/4,190`、39动作/9洗牌、零损/6 HP/T1/敌人全灭、无边界；39条完整动作与80项非计时/非内存/非调度结果相同，含45项工作/剪枝计数。DOP1/2 分别 `4.966/4.233 s`、分配 `713,359,480/727,272,152 B`、GC累计 `522.803/0 ms`（DOP1最长 `13.574 ms`）、进程 `VmHWM=1,561,112/2,152,880 KiB`。NoGC 为关闭/4 GB，不能归因纯多核收益。 |
| `CUSTOM-231301-NORMALIZED-V29` | v29 搜索与自动部署通过 | runId `59472b049645446e8fdd272022b0b6b3`，VeryHigh/DOP8/NoGC16：19动作/7洗牌/1瓶 `FIRE_POTION`，战损3/1 HP/T1/敌人全灭；完整自动部署、零计划外重算、Instant 设置恢复。选中层 `1,820/7,200/901`、总计 `9,385/34,969/4,628`；`3.761 s`、分配 `1,362,432,464 B`、GC累计/最长 `100.747/100.747 ms`、`VmHWM=2,319,024 KiB`。归一化原版根不等于完整第三方 Mod 兼容。 |
| `SOUL-NEXUS-V29-DOP1-GC` | v29 质量回归通过 | runId `498a4092aebe4812b7ae9790e19377a6`：战损1/97 HP/T4/敌人全灭、1次洗牌，达到旧 v9 的战损/回合质量。`7,795/55,393/27,266` 展开/转移/选择、`20.071 s`、分配 `3,228,250,528 B`、GC累计/最长 `2,537.668/14.543 ms`、`VmHWM=1,673,108 KiB`。与上行 Custom 属于同一 v29 普通截线轮转方案，不拼接不同候选。 |
| `SOUL-NEXUS-V29-DOP2-NOGC4` | v29 质量回归通过 | runId `7320b172df194b90889bb986d285e5ae`：战损1/97 HP/T4；`8,035/56,372/27,464`、`13.455 s`、分配 `3,307,080,992 B`、GC累计/最长 `385.912/385.912 ms`、`VmHWM=3,856,408 KiB`。DOP与GC设置同时改变且工作量不同，不将与DOP1的差值解释为纯并行收益。 |
| `TIE-RANK-V28-AB` | 被否决的失败对照 | 直接撤第三排序键：Soul runId `0e4d810a675c42ad9ba2ce0ae516e487` 获胜，但 Custom runId `37cbc9160ee740b1bbedfd97d454cb78` 为 `OnlyDeath=true`、HP0/敌人372。该实验只用于根因定位，不属于当前方案或最终通过证据。 |
| `PHANTASMAL-V29` | 质量断言失败；Smart政策冲突与性能增长分开记录 | runId `3a8ba38edb9e4a59b0b203f32c0954e0`：旧v9无药死亡/损10后搜索1药层，节省10≥门槛9，得零损/10 HP/T3；v29无药胜利损5/5 HP/T8，Smart按 `5/9=0` 跳过药水层，旧胜利本轮未生成，不认定为已知剪枝丢解。无药工作量旧 `5,693/62,011/42,575`、`7.490 s`/约3.073 GB分配；本轮 `45,590/692,219/441,528`、`85.106 s`、分配 `55,959,575,112 B`、GC累计/最长 `6,781.069/1,547.848 ms`、`VmHWM=11,682,412 KiB`。原质量失败保留，模式选择待用户决定，不删更好无药解绕过门槛。 |
| `EXOSKELETONS-V29` | 120秒超时失败 | runId `c272a6a285de40b3a9dd67d5a9a4eae6`，进程 `VmHWM=12,654,004 KiB`；没有可用最终搜索指标，不填写推测的节点、战损或路线质量。补测批次已退出，广泛矩阵暂停。 |
| `EXOSKELETONS-V30` | 协议Passed，人工质量FAIL | 原搜索配置、不采样、请求截止120秒，runId `26c4826d1973456ca8a6d7087e8732a0`，退出码0；零损/97 HP/T9/敌人全灭，劣于旧v9零损/T5。整条计划42动作/3洗牌/0药，首回合0动作；`12,509/136,516/84,648`、`19.067 s`、分配 `7,859,213,448 B`、GC0、`VmHWM=9,242,068 KiB`、`boundary=None`。不能因协议通过、零损或无边界就宣称质量通过。 |
| `EXOSKELETONS-V31-RETENTION-DIAG` | 诊断证据，不是修复通过 | runId `22bc13a42f5147aaa1b79748d7349bf8`，工作量和零损T9结果同v30。pass9/展开555/去重池3,109，相关启动候选排名2,002、非必保、后续保留均false；同父40分支仅保留2个。日志pass13/展开1,154截断，pass1–4和6–12完整；旧日志缺NestedChoices，不能声称精确复现旧路线。 |
| `EXOSKELETONS-V32-FRESH-OPTION` | 实验否决，质量审计退出1 | runId `c0dbc1b82f1d4b909b234ee73e6a302d`：协议Passed，仍零损/97 HP/T9/敌人全灭，劣于旧零损/T5；`11,930/163,767/114,124`、`21.023 s`、分配 `9,134,969,760 B`、GC0、`VmHWM=10,515,728 KiB`。同父最高Beam同分战术代表未改善质量，不保留为有效修复。 |
| `EXOSKELETONS-V30-BOUNDED-PROFILE` | 短预算诊断，零损质量失败 | runId `3f39e454273f466f89b4fdd7e994822e`：损1/96 HP/T4、`TimeLimit`，`8,373/129,782/92,009`、`20.874 s`、分配 `7,337,646,088 B`。实际 `completed_turns=7`，选中T4不是探索深度；20秒deep预算改变配额并触发T2/T3强制结束候选，不与旧300秒配置作同条件A/B。EventPipe为67,011样本/1,015类型、加权 `7,299,859,352 B`，EventsLost0且未观测GC开始事件，非存活/峰值内存；线程Wait样本不是CPU利用率。该次峰值缺失，不用working set补位。 |
| `KAISER / INFESTED` | v29未运行 | 位于 Exoskeletons 超时之后，尚无本轮结果。后续须按上述质量门槛人工复核，Kaiser 单列T2续用与零计划外重算，不以较弱协议 Passed 或固定旧节点数代替质量。 |
| `AEONGLASS / LONGLINE-NOGC4/OFF` | v29未运行 | 位于 Exoskeletons 超时之后，须在后续验证实际获胜并比较 GC/NoGC 峰值、检查点和同根质量。旧 Aeonglass runId `030812eb278643389d002f23eef0f256` 虽协议为 Passed，实际只有死亡路线，不算获胜证据；headless 结果不作为可见 Steam 卡顿结论。 |

## 0.29.1（历史）：2026-09-04 19 点后问题包硬逻辑修复

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ISSUE-20260904-AFTER-1900-TRIAGE` | 已整理 | 5 批下载包共 173 条记录，单列计算失败、回合准备选牌、部署中止、计划外重算，并重点标出 26 条带玩家备注的原始证据。 | 2026-09-04 |
| `TURN-START-ORDER-0291` | 通过 | Power/遗物回合准备顺序、能量重置顺序和 Sly 嵌套选择保持原版时序；`FIX-141700-TURN-START-CHOICE-FIXED` runId `b366e89a3e1b4f6eb11744430aa66348`，Toasty 回归 runId `4364f489957f49bca864d69ffd708201`。 | 2026-09-04 |
| `KNOWLEDGE-DEMON-NATIVE-CHOICE-0291` | 通过 | 结束回合后原生知识恶魔选牌仍由部署会话驱动，观测 `MIND_ROT_POWER`；runId `8ed16a06900c48ddb048fa89d2fe6fe6`。该夹具最终只有死亡路线，不作为整战质量证据。 | 2026-09-04 |
| `CARD-DERIVED-STATE-IDENTITY-0291` | 已修复，待专用复跑 | 动作身份键与状态指纹排除派生 `CalculatedVar`，针对 `NO_ESCAPE`、`UNLEASH`、`COMET` 的问题包空手牌异常已完成根因修复。 | 2026-09-04 |
| `SPITE-REPEAT-DYNAMIC-VAR-0291` | 已修复，待专用复跑 | `Spite` 缺失 `Repeat` 时按原版固定公式和升级等级恢复重复攻击次数，针对 `Repeat` KeyNotFound 问题包完成根因修复。 | 2026-09-04 |
| `ROOT-HOOK-NULL-0291` | 已修复，待专用复跑 | 根监听器快照过滤空项，针对 Queen `BeforeAttack` 钩子中的 NullReferenceException 完成根因修复。 | 2026-09-04 |
| `POTION-SLOT-DRIFT-0291` | 已修复，待专用漂移夹具 | 部署前药水槽位为空或内容不符时转入 `DeploymentDrift` 重算；未使用宽泛异常吞掉真实执行错误。 | 2026-09-04 |

## 0.29.5（本地扩展）：主进程 Mod 版本钉住

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PRECOMBAT-MOD-PIN-015` | 通过 | 十 Mod 组合加载 Combat Solver `0.29.5`、Seed Oracle `0.1.18`、HowlFromBeyondBgm `1.1.5` 等主进程版本；工坊式原子替换后，启动期硬链接快照仍保留旧内容。两次确定预测和一次假设样本复用同一 worker，计数 `starts=1 / reuses=3`；战损 `5`、内存可见、隔离音频静音、2/10/30 分钟与一直维持、自动关闭及 live 状态不变均通过。runId `8382cbd40ce74a44ac2cac7e444f6403`。 | 2026-09-05 |

## 0.29.4（本地扩展）：作者 0.29.1 基线与可配置 worker 期限

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PRECOMBAT-API-V5-UPSTREAM-0291-013` | 通过 | 作者 `v0.29.1`、RitsuLib、Random Foreseer、Combat Solver `0.29.4` 与 Seed Oracle `0.1.16` 组合加载。停止状态接受 2/10 分钟和一直维持；预热后显示 PID/内存/静音并切换为 30 分钟；两次确定预测战损均为 `5`，一个假设样本复用同一 worker，计数 `starts=1 / reuses=3`，样本结束自动关闭，live 状态令牌不变。runId `e2236a4ec85041dc95c0b3417ed0b688`。 | 2026-09-04 |
| `SEEDORACLE-PRECOMBAT-V5-LOCALIZATION-014` | 通过 | Seed Oracle UI 自检验证 5 个 worker 策略、内存/关闭/预热控件、11 个样本次数、6 类对象、完整模拟风险说明，以及当前幕所有遭遇标题均由游戏本地化解析，不再显示 `LocString … .title`。runId `e15b744f36e84d85aa8771cf02328121`。 | 2026-09-04 |

## 0.29.3（本地扩展）：worker 资源控制与假设战斗样本

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PRECOMBAT-API-V4-WORKER-SIMULATION-010` | 通过 | RitsuLib、Combat Solver `0.29.3`、Random Foreseer 与 Seed Oracle `0.1.15` 四 Mod 同时加载。显式关闭后状态为停止；预热返回 PID、工作集、私有内存与静音标记；两次相同确定预测战损均为 `5`。同一 worker 计数 `starts=1 / reuses=3`；样本种子 `104372539623684` 在内层恢复校验后、开战前应用并回传匹配标记；样本结束自动关闭，主进程 live 状态令牌不变。runId `61f19e8db7ae4f9a8a217424e602f075`。 | 2026-09-04 |

## 0.29.2（本地扩展）：可复用静音战前 worker

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PRECOMBAT-API-MANUAL-CACHE-REUSE-009` | 通过 | RitsuLib、Combat Solver `0.29.2`、Random Foreseer 与 Seed Oracle `0.1.14` 四 Mod 同时加载；同一快照两次强制预测返回相同战损 `5`，使用同一 worker PID，计数为 `starts=1 / reuses=1`。隔离设置四类音量均为 `0`，live 状态令牌不变；首请求 game startup `12.57 s`，第二次 `0.6 ms`。runId `08fc0ae91d39417b9c234bbc5d2da6ea`。 | 2026-09-04 |

## 0.29.1（本地扩展）：基于 0.29.0 的战前预测 API 与硬逻辑修复

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ISSUE-20260903-MANGLE-COW-FIX` | 通过 | MANGLE + SLITHER 问题包在 `VerifyIncrementalSearch` 下完整回放通过；增量/完整回放的费用 RNG、牌面状态和搜索结果一致。runId `f25dee83d9ba4b039318a4cdef20fbe3`。 | 2026-09-04 |
| `ISSUE-20260903-MANGLE-CARD-DIFFERENTIAL-FIX` | 通过 | MANGLE 单卡严格差分，验证抽牌后带 SLITHER 的攻击牌仍可正确打出并产生预期效果。runId `b7e5d32dff794ef695c93dbc3123b689`。 | 2026-09-04 |
| `ISSUE-20260903-QUEEN-HP-MISMATCH-CURRENT` | 通过 | 复用 `0.28.2` Queen 遗物标注问题包的精确根和 RNG；当前源码短搜完成最终路线物化，没有再次出现 HP 标注回放差异。runId `22f3789304284eb49662d378361940e2`。 | 2026-09-04 |
| `ISSUE-20260903-QUEEN-HAZE-MISMATCH-CURRENT` | 通过 | 复用 `0.28.2` Queen/Haze 问题包的精确根和 RNG；当前源码完成短搜并返回候选，没有再次出现卡牌状态标注差异。runId `5bfc15213c444b449161c1b216ab256d`。 | 2026-09-04 |
| `ISSUE-20260903-SPITE-REPEAT-CURRENT` | 通过 | 当前原版卡牌严格差分覆盖失血后 Spite 的重复攻击语义，13 个动作检查全部通过。runId `cd97d0160dec44d3b35cae9b05ca328f`。 | 2026-09-04 |
| `ISSUE-20260903-DEPLOYMENT-DRIFT-RECOVERY` | 已修复，待实时漂移夹具 | 部署时普通计划手牌缺失现在归类为 `DeploymentDrift` 并重新捕获当前根；保留真实执行失败的显式错误。现有 Fork/部署身份边界通过，尚缺在可见游戏中先改动手牌再部署的专用夹具。 | 2026-09-04 |
| `ISSUE-20260903-OBSCURA-SPAWN-CURRENT` | 通过 | 原生 `THE_OBSCURA_NORMAL` 生成路径短搜覆盖 8 回合和 3 次洗牌；生成新怪物前固定敌人列表快照，没有再次出现 `Collection was modified`。runId `faeec097dee647af8453f9aa00f2c6ab`。 | 2026-09-04 |
| `ISSUE-20260903-KNOWLEDGE-CURSOR-FIX` | 代码修复，场景未通过 | 结束回合部署不再把 `ApplyKnowledgeCurse` 放入原生选牌游标；当前默认知识恶魔建局只有死亡路线，等待战斗结束超时，因此不记录为行为通过。已有 `PR29-KNOWLEDGE-CURSOR` 结构回归覆盖相同过滤边界。 | 2026-09-04 |
| `ISSUE-20260903-NATIVE-CHOICE-DRIFT-RECOVERY` | 已修复，待可见漂移夹具 | 原生选牌候选/页面生命周期不一致现在关闭当前页面并请求 `DeploymentDrift` 重捕获；确认按钮等待布局完成后再提交。尚未有专用可见页面先漂移再重捕获的 unattended 证据。 | 2026-09-04 |
| `ISSUE-20260903-NATIVE-CHOICE-PLAN-SEQUENCE` | 已修复，部分通过 | 原生选牌驱动器在收到计划外请求、计划提前结束或页面要求数量变化时报告选择计划漂移，并由部署层关闭页面后请求 `DeploymentDrift` 重捕获；重复计划仍显式失败。`SCULPTING-STRIKE-CHOICE-151` 严格增量回放和第 2 回合复用通过，runId `a35708eb3bae4aa49ef7769230d59bfa`。 | 2026-09-04 |
| `ISSUE-20260903-DEPLOYMENT-TURN-DRIFT` | 已修复，待专用时序夹具 | 部署动作检测到玩家回合已结束时现在清理旧路线并按 `DeploymentDrift` 重捕获，不再记为自动执行失败；其他部署异常仍显式失败。 | 2026-09-04 |
| `ISSUE-20260903-PENDING-CHOICE-HOOK-BOUNDARY` | 已修复，待双监听器夹具 | 洗牌 Hook 在已有待处理选择时停止继续调用监听器，分支消费后再继续；避免同一模拟事件创建冲突选择。 | 2026-09-04 |
| `ISSUE-20260903-NATIVE-CHOICE-SURFACE-TIMEOUT` | 已修复，待页面消失夹具 | 原生选牌页面或确认按钮等待超时现在按页面漂移关闭并请求 `DeploymentDrift` 重捕获；非原生等待超时仍走原有失败路径。 | 2026-09-04 |
| `PRECOMBAT-API-FINAL-004` | 通过 | 外层真实 headless 跑局调用 public API v1；内层独立进程精确恢复完整规范化存档后进入毛绒伏地虫战斗并返回战损 `5`、药水 `0`、边界 `None`，外层调用前后完整状态令牌一致。runId `7da852343994495e923dec58bf624b28`。 | 2026-09-04 |
| `PRECOMBAT-API-SEED-STACK-005` | 通过 | RitsuLib、Combat Solver、Random Foreseer 与 Seed Oracle 共四个 Mod 同时加载；Seed Oracle 探测到 API v1/隔离 worker，worker 对精确相同 Mod 集合完成直接恢复和战前搜索，无递归请求，状态令牌不变。runId `1961ef8c7d484da691e07cec99074215`。 | 2026-09-04 |
| `PRECOMBAT-POTION-METRICS-006` | 通过 | 强制至少使用一瓶药水的短搜索把非空动作写入公共结果协议：`FIRE_POTION` / “火焰药水”、第 `3` 回合、槽位 `0`；战斗第 `3` 回合结束。runId `5e281451a3534051b605577cd51c9038`。 | 2026-09-04 |
| `PRECOMBAT-REMOTE-CAMPFIRE-007` | 通过 | 从含历史事件选择的完整跑局精确恢复；按目标路线补记第 9 层篝火与第 10 层宝箱，用目标列坐标进入第 11 层 `SLIMES_NORMAL`，在开战 Hook 前覆盖为休息后的 `66 HP`。3 秒 DOP1 短搜返回战损 `12`、最终 `54 HP`；完成项包含 `DirectRunSnapshot:ExactStateRestored`、`PreCombatInterveningMapPoints:2`、`PreCombatPlayerHp:66`。runId `fe93b060ada64e78864fca8d825aeb85`。 | 2026-09-04 |
| `PRECOMBAT-API-MANUAL-V2-008` | 通过 | public API v2 双进程往返使用目标坐标、独占可取消 worker、强制重算和入战 HP 覆盖；空事件历史变量被规范化、非空变量保留，返回 `EntryHp=79`、战损 `1`、药水 `0`、边界 `None`，调用前后 live 状态令牌一致。runId `f8db7c03ea9444cc89483495c985f221`。 | 2026-09-04 |
| `PRECOMBAT-API-SEED-STACK-0.29.1-FINAL` | 通过 | API v2 改动重放到作者 `0.29.0` 后的四 Mod 组合回归；Combat Solver 以 `0.29.1.0` 加载，外层 Seed Oracle 记录 `precombat_public_api_v2=true`，公开 API 返回 `EntryHp=79`、战损 `5`、药水 `0`、边界 `None`，调用前后 live 状态令牌一致。runId `2e038fa1be6b45a19a7a5096c9be0f16`。 | 2026-09-04 |

性能指标口径：`selected_*` 只描述最终选中的单个 solver；请求级 `total_expanded_nodes / total_transitions / total_choice_branches`、`total_solver_ms`、分配与 GC 累计对正常、失败和取消的每个 solver 工作区间精确记录一次，包括取消前已发生的部分工作。Smart 有限药水层之间由 coordinator 主动执行的内存整理也计入时间、分配与 GC，但不增加 solver 数；建立开局、层间比较等其他编排工作仍不在这些总值中。因此端到端耗时以请求/阶段外层墙钟为准，峰值内存以进程 `VmHWM` 为准。Smart 多层的取消时点可能令请求总工作量小幅波动，语义验收优先比较胜负、战损、回合和动作路线。峰值工作集是瞬时进程峰值，不能跨阶段相加；`16 GB` NoGC 是运行时请求预算，不等于实际占用或硬上限；NoGC 活跃时 `GC.GetTotalMemory(false)` 不是严格 live-set 测量。

## 下一版本（开发中）：战后回血遗物计入战损

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POST-COMBAT-RELIC-HEAL-ASSERTIONS` | 通过（无人测试静态断言） | 无条件回血按剩余生命上限裁剪：`6` 点回血在 `70/80` 得 `6`、`78/80` 得 `2`、`80/80` 得 `0`。带阈值的一件按原版截断判定：`75` 点最大生命下停在 `37` 得 `18`、停在 `38` 只得 `6`，`70/75` 受上限裁剪得 `5`。排序口径 `MonotoneHealFor` 在同一输入下只给 `6`，阈值部分不进排序。未获胜或阵亡的路线一律得 `0`。 | 2026-09-04 |
| `POST-COMBAT-RELIC-HEAL-VANILLA-GROUND-TRUTH` | 通过（反编译核对） | 全量反编译 `sts2.dll` 后交叉筛选，胜利后回血的遗物恰好是燃烧之血、黑暗之血、带骨肉三件；其余用 `AfterCombatVictory` 的遗物不回血，其余会回血的遗物挂在进房间、回合开始等别的时点。带骨肉走 `AfterCombatVictoryEarly`，先于两件血遗物结算，阈值判定读的是未回血前的终局生命。 | 2026-09-04 |
| `POST-COMBAT-RELIC-HEAL-LIVE-RUN` | 未验证 | 实机整局观察尚未进行，headless fixture 仍因缺少非 Steam `default` 存档配置无法建立。 | 2026-09-04 |

> 带骨肉只显示不排序是本批的已知取舍，不是遗漏。要让它进排序，需要先把 `MultiObjectiveDominates` 和 `TranspositionLabel.Dominates` 改成比较战后终局生命而不是原始生命与原始掉血，并为阈值附近的支配关系补专门夹具。

## 下一版本（开发中）：路线回血计入战损

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `RECOVERED-HP-STRATEGIC-VALUE-ASSERTIONS` | 通过（无人测试静态断言） | `PersistentValueOfRecoveredHp` 在普通战斗、第一、二幕末 Boss 和最终 Boss 分别为 `10/2/0`；`StrategicHpDeficit(12,3,10,None)=5`、`(0,0,9,None)=-9`、`(0,0,9,RunEnding)=0`；可回血时 `0` 不再被当成已证明的最优下界。 | 2026-09-04 |
| `RECOVERED-HP-IRONCLAD-NOT-YET-LIVE-RUN` | 通过（实机单局观察，未建 headless fixture） | 战士带「时候未到」整局 23 次打出，回血量 `0/2/3/5/7/9`，均不超过牌面 `10`。多条选中路线在挨了伤害后回满：`战损 9 / 回血 9 / 结束 87`（T1 与 T3）、`战损 7 / 回血 7 / 结束 87`、`战损 3 / 回血 9 / 结束 87`。`战损 7 / 回血 2 / 结束 82` 证明溢出裁剪按回血发生时的空间结算，不按回合结束时的空间。整局决策未见退化。 | 2026-09-04 |

> headless 启动器需要一个非 Steam 的 `default` 存档配置来初始化隔离数据目录。当前验证机器只通过 Steam 启动过游戏，没有该配置，因此本批改动没有对应的 unattended fixture，行为证据来自实机单局观察。

## 0.28.3（已发布）：战损停止与路线信息

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ACCEPTABLE-BATTLE-HP-LOSS-THRESHOLD` | 通过（headless 控制器/UI/搜索生命周期，DOP1） | 设置页阈值 JSON 往返、默认 `0`、完整胜利且预计本局战损 `<=` 阈值才触发早停的边界断言通过；独立短搜在允许范围上限下返回满足阈值的完整胜利路线。runId `aca83616becd422e99558a0d07b970d0`。 | 2026-09-03 |
| `KILL-SOURCE-ANNOTATION` | 待实机确认 | 最终路线回放记录卡牌、药水、毒、荆棘、能力、遗物和球等击杀来源；直接移除仍标记为未知效果，召唤/重建敌人可保留目标名称。Release 构建和 headless 生命周期通过。 | 2026-09-03 |
| `GREMLIN-MERC-PRESERVE-RESOURCE-TARGET` | 待实机确认 | 保钱策略在胖地精携带被盗资源且暂无攻击威胁时保留追回资源的动作分支，避免资源携带者逃跑。Release 构建和 headless 生命周期通过。 | 2026-09-03 |

## 0.28.2（已发布）：搜索热路径与 NoGC 回退

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR37-HOT-PATH-ALLOCATION-A/B` | 贡献者 A/B 通过 | 固定机甲根在 Windows 与 macOS 上保持工作量、路线、评分和战损不变；Windows worker 分配 `7.40 GB → 6.20 GB`，macOS 分配 `10.40 GB → 9.44 GB`，两端搜索时间均下降。 | 2026-09-03 |
| `PR38-NOGC-FALLBACK-PARALLELISM-A/B` | 贡献者 A/B 通过 | macOS 不支持 NoGC 时保持固定工作量与结果，实际并发 `2 → 8`、耗时 `37.2 s → 27.2 s`；Windows 正常 NoGC 路径无可测差异。合并态策略断言覆盖系统余量回退保守并发与普通平台/尺寸回退完整并发。 | 2026-09-03 |

## 0.28.1（已发布）：Smart 药水门槛与手动深度释放系统内存

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `MANUAL-SYSTEM-MEMORY-RELEASE` | 待实机确认 | 主界面内存条右侧提供固定入口；等待搜索退出后压缩托管堆并修剪游戏进程工作集，UAC 辅助程序清空系统工作集与待机列表，不清空修改页列表。 | 2026-09-03 |
| `GENERIC-SMART-POTION-SAME-LOSS-CONSERVE-V0111` | 通过 | 无药零损获胜时，付费药即使更早结束也不绕过每瓶 `9 HP` 门槛；结果为 `24/52` 展开/转移、`0` 药、`0` 战损、T3，且未打出卖血牌。runId `c91f29fcaa9a40129e6f67adfe06a8b9`。 | 2026-09-03 |

## 0.28.0（已发布）：通用周期与跨回合收益

> 这里记录基于上游 `0.27.2` 的最终定向与性能证据。生产算法只使用控制形状、精确动作相位、分支相对 stand-pat 状态和通用收益向量；fixture 中的卡牌、药水或遗物名称只是输入，不是生产特判。周期识别窗口最多 `32` 个动作；跨回合基础观察期为 `max(16, 两个完整牌堆周期所需回合)`，语义变化探针最多 `64` 次回合转移，最近一回合确有通用改善的探针最多 `128` 次。命中节点上限的场景只证明预算内找到路线，不称穷尽或数学全局最优。

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `AFTER-STARS-GAINED-ENGINE-MIRROR-0111` | 通过 | 通用 `GainStars` 在状态变更后分发 `AfterStarsGained`；5 层黑洞配合发光严格对比原版与预测完整状态，并显式断言获得 `1` 星、敌人生命变化 `-5`。最终 runId `b4c281152e5b4468b59f10c665d68d`。 | 2026-09-03 |
| `GENERIC-LOOP-LETTER-OPENER-HIDDEN-PHASE-DOP1/2` | 通过 | 两次表面回到同一牌堆形状后，隐藏相位在后续重复中兑现；DOP1/DOP2 都为 `6/12` 展开/转移、`6` 次洗牌、T1。runId `1ae74fcaf14648a19a773e9be602fa34` / `28d3a383cb3c4fd4944ffcc923f76e7a`。 | 2026-09-03 |
| `GENERIC-CROSS-TURN-HIDDEN-BUFFER-DOP1/2` | 通过 | 表面进展长期停滞但精确状态仍跨回合推进；DOP1/DOP2 都为 `513/770` 展开/转移、`16` 次洗牌、T17；DOP2 最大并发为 `2`。runId `e1aac2f411fa4d58b91466022f5edde7` / `d38be6387ddd4aadb13a1adcdc3864cf`。 | 2026-09-03 |
| `GENERIC-CROSS-TURN-STAGNANT-CONTROL` | 通过（上游合并后定向证据） | 无伤害手段的停滞场在 `78/117` 展开/转移后有界停止，最终路线不采用纯防御空转；与隐藏缓冲场共同约束“不能早停、也不能无限续期”。runId `fbba8ab4c2724a3d8fa2585b448dd713`。 | 2026-09-02 |
| `GENERIC-CROSS-TURN-PURITY-PILLAGE` | 通过（定向证据） | 先净化牌库、下一回合兑现，`123/340` 展开/转移、T2。runId `c4e37ee3249d4a819c27a99e0d15fb8a`。 | 2026-09-02 |
| `GENERIC-LOOP-RAMPAGE-DYNAMIC-GROWTH-DOP1/2` | 通过 | 动态成长值不写入循环特判；DOP1/DOP2 都为 `2400/5354/344` 展开/转移/选牌、`32` 动作、T1，动作和全部非时序工作量一致；DOP2 搜索与动作重放最大并发均为 `2`。runId `2d33c3a4eaee44d5b06004758e8cefb4` / `5100ca05213f455ba6f1dd57f0916fef`。 | 2026-09-03 |
| `GENERIC-SMART-POTION-SAME-LOSS-FASTER` | 通过 | 同为零战损时，Smart 选择 T1 的一药路线；请求累计 `31/74`、选中层 `7/22` 展开/转移。runId `02c81917a3284c269a90e2fec5b65d4e`。 | 2026-09-03 |
| `GENERIC-SMART-POTION-THREE-LAYER-PROGRESS-REBASE` | 通过 | 完成无药、恰好一药、恰好两药三层，两次层间整理后仍选择零战损 T1 的一药路线；请求累计 `52/140`、选中层 `7/19`，Gen0/1/2 均 `4` 次。runId `8e01068bacc049b89acacd66e218f73f`。 | 2026-09-03 |
| `GENERIC-SEARCH-POLICY-BRANCHING-REBASE` | 通过 | 控制器生命周期、三层聚合、NoGC 生命周期、DOP 等价、快照释放及同父节点唯一循环租约/转置边界断言通过。runId `328dbe4322a54815a83f3946563d73b6`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-PILLAGE-SINGLE` | 通过 | 单张掠夺触发自动转移内连锁；`1/2` 展开/转移、T1、零损、一个显式动作。该机制不是循环规划器证明出的数学无限。runId `aed2647646d645abb18e1ef94bf53283`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-POMMEL-FINITE` | 通过 | 有限链控制场为 `6/7`、两次洗牌、T2、战损 `11`，首动剑柄打击；没有被误判为 T1 无限。runId `eb6084633d3744399a3e3422e13e2e8a`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-BLOODLETTING-QUALITY` | 通过 | `186/464`、四次洗牌、T2、战损 `3`；最终排序选择少卖血的 T2，而非战损 `6` 的 T1。runId `b57b545ed9c7415cb6644af2df7e89cc`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-SILENT-DISCARD` | 通过 | 准备/战术大师抽弃链为 `301/837/261` 展开/转移/选牌、16 个动作、T1、零损。runId `1991d55b153f44c8b722546214e57f0a`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-DEFECT-RETRIEVAL` | 节点上限内找到解 | 万物一心/全息影像取回链在 `2400/6584/1714` 后命中 NodeLimit，找到 29 动作、T1、零损路线；不称全量穷尽。runId `a7a3fda4781e4f329a00b39aecdf079b`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-REGENT-PARTICLE-WALL` | 节点上限内找到解 | 粒子墙/照我说的做链在 `2400/6037/2` 后命中 NodeLimit，找到 38 动作、T1、零损路线，实际/路线最大格挡 `243/1125`；不称全量穷尽。runId `5859c64bb16d4a0c8bf1134f45d23861`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-REGENT-SEALED-BLACK-HOLE` | 通过 | 封印王座/黑洞资源链为 `10/20`、10 个动作、90 格挡、T1、零损。runId `3bb92f0684d7414d9d0660f83edb992b`。 | 2026-09-03 |
| `INFESTED-PRISMS-GENERIC-QUALITY-INTERMEDIATE-BASELINE` | 已被最终候选取代 | 历史中间候选为 `80,009/537,025/213,213`、约 `57.10 s`、约 `11.29 GiB`、52 HP/战损 8/T6；它暴露了质量保路导致的工作量膨胀，仅保留作优化过程证据。runId `f234bd8a4c0f4f2a87cb6a27458515e4`。 | 2026-09-02 |
| `INFESTED-PRISMS-GENERIC-QUALITY-FINAL` | 通过（配置搜索完整结束） | 同根 VeryHigh/Smart/DOP8/NoGC 16 GB：累计 `13,516/80,477/33,664`，选中层 `4,437/23,810/8,337`；搜索 `10,548.215 ms`、累计分配 `4,920,306,312 B`、`VmHWM=3,715,840 kB`（约 `3.54 GiB`）。结果 43 HP/战损 17/T5/两药，优于旧 42 HP/战损 18/T7；两次层间 NoGC 回收重建成功，Gen0/1/2 均 `4`，GC 暂停累计/最大 `640.203/404.340 ms`。runId `1e9c735a5c9e42889ab46c6389a66b16`。 | 2026-09-03 |
| `AEONGLASS-LONGLINE-NOGC4` | 通过 | 长线同根累计 `27,905/173,477/74,998`、选中层 `8,687/59,910/26,619`，战损 9/56 HP/T9/两药；`72,151.169 ms`、`VmHWM=4,396,804 kB`（约 `4.19 GiB`），Gen0/1/2 均 `30`，GC 暂停 `4,822.405 ms`。13 个压力检查点和 2 次层间整理全部重建 NoGC，无回退。runId `5f75b1cd2b604f34874be9e6243590db`。 | 2026-09-03 |
| `AEONGLASS-LONGLINE-NOGC-OFF` | 通过（A/B） | 与 NoGC4 节点、路线、战损和回合完全一致；`97,927.457 ms`、`VmHWM=2,745,000 kB`（约 `2.62 GiB`），Gen0/1/2=`3522/1695/88`，GC 暂停 `29,712.935 ms`。关闭 NoGC 省约 `1.57 GiB` 峰值内存，但慢约 `35.7%`（反向口径：NoGC 快约 `26.3%`）。runId `6c187605b7a8451bbd163a800d9f25c4`。 | 2026-09-03 |

### 新增 fixture 清单

| Fixture | 主要边界 |
| --- | --- |
| `coverage/fixtures/powers/after-stars-gained-black-hole-glow-0111.json` | 通用星能增加 Hook 分发、黑洞单次伤害与完整状态严格差分 |
| `coverage/fixtures/search/generic-loop-letter-opener-hidden-phase-v0111.json` | 同牌堆形状的隐藏相位收益、DOP 等价 |
| `coverage/fixtures/search/generic-cross-turn-hidden-buffer-positive-v0111.json` | 晚于基础观察期兑现的精确隐藏状态、DOP 等价 |
| `coverage/fixtures/search/generic-cross-turn-stagnant-control-v0111.json` | 真正无收益跨回合路线有界停止 |
| `coverage/fixtures/search/generic-loop-cross-turn-purity-pillage-positive-v0111.json` | 先净化牌库、下一回合兑现的跨回合收益 |
| `coverage/fixtures/search/generic-loop-rampage-dynamic-growth-positive-v0111.json` | 动态成长循环、32 动作路线与 DOP 等价 |
| `coverage/fixtures/search/generic-final-quality-zero-loss-over-faster-blood-sale-v0111.json` | 低战损优先；同战损才比较结束回合 |
| `coverage/fixtures/search/generic-smart-potion-same-loss-faster-v0111.json` | Smart 付费药未省足战略 HP 时保留无药路线 |
| `coverage/fixtures/search/generic-smart-potion-three-layer-progress-v0111.json` | 零损终局不启动无收益的付费药梯度 |
| `coverage/fixtures/search/generic-loop-speedster-discard-draw-positive-v0111.json` | 多动作抽弃循环、洗牌与零损击杀 |
| `coverage/fixtures/search/generic-loop-speedster-startup-positive-v0111.json` | 先建立能力再进入循环 |
| `coverage/fixtures/search/generic-loop-hellraiser-pillage-bloodletting-positive-v0111.json` | 卖血/能量启动后兑现 |
| `coverage/fixtures/search/generic-loop-hellraiser-startup-positive-v0111.json` | 先打能力牌再启动 |
| `coverage/fixtures/search/generic-loop-hellraiser-pillage-defend-breaker-v0111.json` | 循环被非攻击抽牌打断并跨回合求解 |
| `coverage/fixtures/search/generic-loop-pale-blue-dot-threshold-cross-turn-v0111.json` | 阈值状态、药水入口与跨回合/出口收益 |
| `coverage/fixtures/search/generic-loop-regent-star-energy-positive-v0111.json` | 星能与能量循环 |
| `coverage/fixtures/search/generic-loop-regent-black-hole-startup-positive-v0111.json` | 储君能力启动与循环 |
| `coverage/fixtures/search/generic-loop-hellraiser-pillage-single-current-v0111.json` | 当前规则单张掠夺的自动转移内连锁；不是数学无限 |
| `coverage/fixtures/search/generic-loop-hellraiser-pommel-finite-current-v0111.json` | 当前规则有限链负例；不得误判为 T1 无限 |
| `coverage/fixtures/search/generic-loop-bloodletting-double-pommel-quality-v0111.json` | 卖血启动质量排序；低战损优先于少回合 |
| `coverage/fixtures/search/generic-loop-silent-prepared-tactician-current-v0111.json` | 当前规则抽弃重复链 |
| `coverage/fixtures/search/generic-loop-defect-all-for-one-hologram-current-v0111.json` | 当前规则取回重复链；NodeLimit 内有解 |
| `coverage/fixtures/search/generic-loop-regent-particle-wall-make-it-so-current-v0111.json` | 当前规则技能/格挡重复链；NodeLimit 内有解 |
| `coverage/fixtures/search/generic-loop-regent-sealed-throne-black-hole-current-v0111.json` | 当前规则双资源重复链 |

`coverage/fixtures/search/generic-loop-hellraiser-pillage-bloodletting-cards.json` 只是可复用牌堆输入，不是独立场景。未在上表列出 runId 的 fixture 仍须复测，不能因文件存在就宣称当前工作树通过。

## 0.27.2（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `DYNAMIC-ROUTE-HP-LOSS-MONOTONIC` | 待玩家实测（Release 编译） | 未结束战斗的动态路线显示“预计战损 未知”；完整胜利路线才显示数值，并拒绝用更高战损候选覆盖当前展示。逐回合掉血不受影响。按要求不运行 UI 测试。 | 2026-09-02 |

## 0.27.1（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `UI-MEMORY-GC-WALL-IDLE` | 待玩家实测（系统压力修复已部署） | 实机基线在条显示 `20.1%` 时 NoGC 意外退出：本轮分配 `2.15 GB`，但预测系统压力已约 `96%`，随后累计 GC 暂停增至 `19.8 s`。配置预算现作为上限，实际区域按系统安全余量缩小；搜索检查点同时检查分配额度与系统压力。Smart 梯度之间主动清理，最终梯度正常结束后保留战斗级区域，战斗结束再延时清理。本轮按要求不运行 UI 测试，等待玩家实测。 | 2026-09-02 |
| `UI-MEMORY-SYSTEM-PROCESS-SEGMENTS` | 待玩家实测（Release 编译） | 内存条按实时物理内存分为灰色系统占用、彩色游戏进程占用和剩余空间；文字显示“当前内存占用 X GB / 搜索总可用 Y GB”，搜索总可用为 CLR 安全总量减去系统占用。本轮按要求不运行 UI 测试。 | 2026-09-02 |
| `UI-SEARCH-LIMIT-WARNING` | 待玩家实测（此前结构验证通过） | `TimeLimit` 与 `NodeLimit` 结果始终显示不可关闭的顶部警告，以“计算尚未彻底穷尽”解释时间/节点上限；正常结束不显示。此前结构 runId `0482e473ee9b4271ba314c25fa9285a7`；本轮按要求不运行 UI 测试。 | 2026-09-02 |

## 0.27.0（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR31-CONTROLLER-UI-LIFECYCLE` | 通过（headless 控制器/UI/药水生命周期，DOP4） | 自动计算持久化、独立停止/采用/执行控件、候选路线与逐回合对敌伤害、窄药水浮层、三向缩放、内容最小尺寸、折叠恢复及位置/尺寸 JSON 往返均通过。runId `75070ce99c1b48ef9c9608205ac57e19`。 | 2026-09-02 |
| `PR31-INITIAL-TOASTY-CONTROLS` | 通过（headless 烘焙手套开局搜索，DOP4） | 首次回合准备搜索可采用已展示候选，返回第 1 回合 `25` 个动作；采用、执行和后续重算仍由回合准备事务接管。runId `e6cb54b4de4f4110a996c2563c7f96ea`。 | 2026-09-02 |
| `OVERLAY-RESIZE-PERSISTENCE-NEXT` | 通过（headless 真实重排） | 宽/高成对持久化、右/下/右下三向缩放、三斜线抓手、`16 ms` 拖动节流、内容最小尺寸、紧凑收起和展开恢复通过；独立药水浮层不改变主面板持久宽度。可见观感未检查。 | 2026-09-02 |
| `BOSS-HP-STRATEGY-SETTINGS-NEXT` | 通过（headless 设置/UI/搜索策略，DOP4） | 第一、二幕与最终 Boss 两项策略独立 JSON 往返；通关优先分别保留 `45 HP/瓶`、`75 HP` 卖血阈值和最终 Boss 存活边界，最低战损独立恢复 `9 HP/瓶` 与普通 Boss 卖血阈值；两类提示文案和关闭状态互不串联。runId `e52f2ec763ac4361a9a09992ab8ae7d5`。 | 2026-09-02 |
| `PR32-DYNAMIC-PREVIEW-EARLY-FINISH` | 结构验证（Release 编译） | 动态演化预览约每 `100 ms` 更新，当前回合预览与可采用推演路线分离；等价获胜路线在敌方状态之后、总评分之前比较结束回合。零警告；按用户要求未运行行为回归。 | 2026-09-02 |
| `LIVING-FOG-GAS-BOMB-TERMINAL-MOVE` | 通过（headless 最小生命周期） | 毒气弹执行 `EXPLODE_MOVE` 后离开活动阵容；回合收尾保留其 AI 快照但不再解析不存在的后继行动。runId `0450d8534bce46e0b329a22f562d95a5`。 | 2026-09-02 |
| `PR33-DAMAGE-AND-POTION-PREVIEW` | 结构验证（Release 编译） | 逐回合对敌伤害累计实际失血；药水补查只在全局路线真正改善时同步更新预览和采用种子。零警告；按用户要求未追加行为回归。 | 2026-09-02 |

## 0.26.0（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR21-BOSS-HP-RELIEF-0254` | 通过（第一/二幕 Boss、第三幕第二 Boss，DOP4） | 第一、二幕分类为 `ActClearHeal`，普通药按 `45 HP/瓶` 开梯度，Boss 卖血阈值为 `75`；第三幕第二 Boss 分类为 `RunEnding`，血量只保留存活边界。runId `444c43b31b3745ef9d94758b2ed79d96`、`4cb4e5936f9b45a09b1ea4ba3091913d`、`da035809ed9243cc850b97085415b3af`。 | 2026-09-02 |
| `PR30-VOID-FORM-SCARCITY` | 通过（合并态 Fork 边界、DOP4） | 两张同成本牌下，虚空形态一个剩余免费格只计一张的机会价值，两个免费格精确计为两倍；PR #27 的奥斯蒂未来价值同时保留。具体玩家实战选牌未稳定复现。runId `aaaa1a2b47924f8b8774a1b6df0b017c`。 | 2026-09-02 |
| `PR29-KNOWLEDGE-CURSOR` | 通过（Fork 边界、DOP4） | 强制结束回合的出牌回放中，知识恶魔诅咒不进入卡牌选择游标；普通动作选择与普通回合选择仍保留并接受消费校验。runId `bb84ece61ac7453e8befa2bb37220f86`。 | 2026-09-02 |
| `PR27-MERGE-FORK-PARALLEL` | 通过（合并态 Fork 边界、DOP4） | PR #27 的低分配状态、roster、缓存与分支所有权断言通过，同时保留 `0.25.3` 的选牌、复活、自动出牌历史和球死亡召唤断言；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=59`。runId `a1747125352741efa72ec01a2ae64c4a`。 | 2026-09-02 |
| `INFESTED-PRISMS-V0251-FULL-SMART-DOP8-A/B` | 通过（最终低分配候选、最新上游同根完整搜索） | 上游/最终阶段墙钟 `112798.755 → 9846.963 ms`，加速 `11.46×`；结束采样工作集 `11,204,886,528 B`。请求累计 `24109/157893/69006` 展开/转移/选牌分支，选中 solver 为 `5861/31244/12125`；保持 `42 HP`、预计战损 `18`、第 `7` 回合和同一动作路线。runId `80921c75b7224f4b887e096f07505739`。 | 2026-09-02 |
| `LONG-LINE-V0251-FULL-SMART-DOP8-A/B` | 通过（四个公开合成长线根） | Silent 396、Necrobinder、Mecha、Queen 的上游→候选阶段墙钟为 `120464.516→55984.737`、`120399.236→29898.891`、`23853.522→8034.665`、`67789.142→29062.616 ms`，加速 `2.15×/4.03×/2.97×/2.33×`；胜负、战损、回合与动作语义不退化。 | 2026-09-02 |
| `SILENT-396-NOGC-BUDGET-BALANCE` | 通过（同根 16/4/2 GB） | `4 GB` 为 `53440.887 ms`、峰值工作集 `4.60 GB`，同 16 GB 路线和工作量且相对上游加速 `2.25×`；`2 GB` 为 `54006.365 ms`、峰值约 `3.4 GB`，同路线且加速 `2.23×`。证明预算是玩家可见的速度/内存权衡，不被预设改写或静默钳制。runId `86b10633dd78400fb9176877855074d3` / `fb93590ed2c04cc7b602fe16ea33f824`。 | 2026-09-02 |
| `PERF-NOGC-TOGGLE-DOP-LIFECYCLE-V0251` | 通过（headless GC/DOP 时序门） | 实际覆盖 NoGC→常规 GC→NoGC、切换中手动回收、关闭模式活动计数、搜索检查点吸收手动回收、引用释放后生命周期补账、`1→2 GB` 重建、取消工作量精确一次、节点快照释放及 DOP1/DOP2 全字段等价和真实并发。runId `df0ab7f8f52c41a2b856aea39c411f49`。 | 2026-09-02 |
| `SEARCH-GC-CLR-UPSTREAM-SHORT-FINAL` | 通过（关闭态端到端） | 配置保留 `false / 17,000,000,000 B`，实际为区域未激活、预算 `0 B`、latency `Interactive`；CLR 可自主回收，不把 GC 次数或 pause 错断言为零。runId `6473c714239b4f63a8735b9291d47629`。 | 2026-09-02 |
| `NOGC-SETTINGS-CONTROLLER-LIFECYCLE-FINAL` | 通过（设置页与 Reset 生命周期） | 新装默认开关与 16 GB、旧 JSON、关闭后预算保留、UI 控件归属均通过；全程关闭的 Reset 不建立自动 GC 根屏障，已启用模式的旧义务仍安全结清。runId `50b51af7a92f42948862686001b1b2cb`。 | 2026-09-02 |
| `PARALLEL-WAVE-ROUND-CHOICE-FINAL` | 通过（安全准入、玩家根与并行指标） | 每个并发 parent 按全局高水位 `1.5×` 预约，never-fit 纯串行，仅 multi-parent 成功 wave 扩宽；自然 singleton action replay、round-choice 唯一所有权及原序合并均实际命中。EXOSKELETONS DOP8 为 `5,686 / 199,522 / 175,150`、`44.608 s`、T4/掉 1；`max parent/action/round = 8/8/5`，runId `6ae1570a41054d369669d65895d285db`。最终 DOP1/DOP2 全政策字段等价、Fork/根快照边界通过，runId `66ca91f7b0934ea6aefd69d4ff563826`。 | 2026-09-02 |
| `PERF-PLAYER-ROOTS-LOW-ALLOCATION-FINAL` | 通过（最终低分配候选的 3 个性能根） | `16 GB` 下 INFESTED `24,109/157,893/69,006`、`9.847 s / 5.730 GB`、42 HP/T7，runId `80921c75b7224f4b887e096f07505739`；PHANTASMAL `24,526/477,315/353,923`、`74.328 s / 42.860 GB`、4 HP/T7，runId `6be970228d0b477d8da0fa2748523819`；EXOSKELETONS `5,686/199,522/175,150`、`42.150 s / 22.180 GB`、96 HP/T4，runId `a97129a4cc514665ba7d222169ac1aef`。三者胜负、战损、回合和动作路线不退化。AEONGLASS 已转独立质量分支。 | 2026-09-02 |
| `PERF-EXOSKELETONS-NOGC16-32-FINAL` | 通过（同 DLL、同工作量的 CPU/内存权衡） | NoGC `16 → 32 GB` 保持 `5,686/199,522/175,150`、评分和 96 HP/T4 路线，墙钟 `42.150 → 28.242 s`；结束工作集 `7.66 → 12.83 GB`、private `18.60 → 35.64 GB`。runId `a97129a4cc514665ba7d222169ac1aef` / `efd4eb77f20848cbbdd147c4a9a12c5f`。PHANTASMAL 同设置为 `74.328/75.273 s`，32 GB 没有收益且结束工作集升至约 `21.90 GB`，所以不作通用默认。 | 2026-09-02 |
| `PERF-ALLOCATION-ENUMERATOR-FORK-BOUNDARY` | 通过（低分配枚举与 Fork 所有权） | StateStore static factory、牌堆/AllCards/Forkable concrete enumerator、roster sink 与直接 COW Fork 构造已编译；Fork 边界完成 parent/child 隔离、阵容移除和状态存储验证，runId `5004871b37f94cbeaf6986556fd53533`。结构门禁 `REFACTOR_BOUNDARIES_OK search_files=59`。 | 2026-09-02 |
| `POWER-LISTENER-CACHE-FINAL` | 通过（Fork 隔离与三个性能根） | Fork 夹具通过 `1→2` 缓存身份、`2→0→1` 结构失效、父子缓存 Power 身份隔离及新增 Power 唯一/顺序，runId `962946a034004fd88cf7bec055c5a04f`。INFESTED 保持 `24,109/157,893/69,006`、42 HP/T7，`9.945 s / 5.474 GB`；PHANTASMAL 保持 `24,526/477,315/353,923`、4 HP/T7，`75.122 s / 40.015 GB`；EXOSKELETONS 保持 `5,686/199,522/175,150`、96 HP/T4，`42.257 s / 20.261 GB`。相对上一最终低分配根累计分配约降 `4.5%/6.6%/8.7%`，耗时中性。runId `5371edccb4c74f9dab68d85a51daa641`、`71606a471f5a44d9b1315af133c5987c`、`9b811499b12d4d0188f0d8db12e0ee4d`。 | 2026-09-02 |
| `PERF-EXOSKELETONS-DOP-SWEEP` | 通过（最终安全 admission、同结果并行扩展） | DOP4/8/12/16 均返回同一 `5,686 / 199,522 / 175,150`、96 HP/T4 路线，墙钟为 `44.896 / 44.608 / 43.157 / 43.082 s`；runId `53c14038d4984e9cac9c0113c6861991`、`6ae1570a41054d369669d65895d285db`、`d2029212067941978790f232ff126680`、`e53770540a464f4e94dec64d830e17d0`。DOP4→16 只快 `4.0%`，12→16 仅 `0.2%`，因此开放 16 但仍默认 DOP4。 | 2026-09-02 |
| `PERF-NOGC-LONG-ROOT-CHECKPOINTS` | 通过（安全点与观察内存） | 最终 `16 GB` INFESTED/PHANTASMAL/EXOSKELETONS 分别跨越 `0/4/6` 个 `SEARCH_MEMORY_CHECKPOINT/RESUMED` 成对边界；需要回收的两场均退出区域、回收并继续。结果与检查点日志观察到的最大工作集约 `11.25/12.22/7.28 GB`；这是离散观察值，不冒充连续采样的精确峰值。 | 2026-09-02 |
| `PERF-V0251-VISIBLE-STEAM` | 未验证（Steam 客户端阻断） | 可见门已尝试三次，最近一次仍未在 `60 s` 内启动游戏；没有留下游戏进程，协议文件已恢复。当前数据来自隔离 Linux headless，不替代完整 Mod 组合下的主线程 p95/p99/max 和可见搜索吞吐。 | 2026-09-02 |

## 0.25.3（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ENEMY-TURN-START-SPAWN-ACTION-BOUNDARY` | 通过（斧兵毒杀复生与千足虫、DOP4） | 敌方回合开始后才出生的怪物不参与本回合行动、不提前推进初始行动；斧兵与千足虫均在第 2 回合精确复用，计划外重算 `0`。runId `e33244c904394b19a5e85d78eba5dccf`、`eee444019fd440a887f22d4fe7b35de7`。 | 2026-09-02 |
| `BOMBARDMENT-EARLY-BEFORE-MAYHEM` | 通过（虔诚雕刻师实包第 2 回合根、DOP4） | 爆破在乱战抽牌前检查既有消耗区，不再把乱战本轮刚耗尽的爆破重复打出；第 3 回合精确复用，计划外重算 `0`。runId `e4f7b25918a74f819d0f3ef5705dcaa9`。 | 2026-09-02 |
| `HEXED-JOSS-PAPER-TURN-END` | 通过（三骑士实包第 4 回合根、DOP4） | 纸钱按 Power 动态赋予的虚无统计回合末消耗牌，阈值抽牌和后续手牌保持一致；第 5 回合精确复用，计划外重算 `0`。runId `6a26ff90d1de4c7e8238b82d6b335285`。 | 2026-09-02 |
| `PAELS-LEGION-CARDPLAY-COOLDOWN` | 通过（斧兵实包第 1 回合根、DOP4） | 补偿层产生的卡牌格挡保留 CardPlay 身份，佩尔士兵在出牌完成后启动冷却；第 2 回合精确复用，计划外重算 `0`。runId `0a6b24215996448f9204b84c3fc193da`。 | 2026-09-02 |
| `UNSETTLING-LAMP-CARD-POWER-SCOPE` | 通过（女王实包第 4 回合根、DOP4） | 卡牌 OnPlay 的通用 Power 效果与专项补偿共用同一卡牌作用域；躁动之灯由鞭打的 Doom 正确消耗，不再错误翻倍后续弱化之触。第 5 回合精确复用，计划外重算 `0`。runId `018bc519e6204a42be24ed6e92788eda`。 | 2026-09-02 |
| `ORB-SLOT-CAP-10` | 通过（Fork 边界、DOP4） | 增加轨道槽位统一遵守原版容量上限：`9 + 2 = 10`，满槽后继续增加仍为 `10`。永劫之镜问题包的完整回放曾卡在等待玩家回合，未声称整包复现。runId `2eeb4d75036a4f1a8245275efbbcf31f`。 | 2026-09-02 |
| `AUTOPLAY-UNMOVABLE-PRIOR-BLOCK` | 通过（Fork 边界、DOP4） | 自动打出的格挡牌读取本回合此前完整的卡牌格挡历史；坚不可摧生效前已有卡牌格挡时不再重复翻倍。runId `a2e032db9bf641b89d6d295fa1106f2d`。 | 2026-09-02 |
| `ORB-DEATH-SPAWN-BETWEEN-PASSIVES` | 通过（Fork 边界、DOP4） | 闪电球击杀感染目标后先完成四只扭动虫召唤，再结算后续玻璃球；四只新生怪均承受 `4` 点伤害。runId `a2e032db9bf641b89d6d295fa1106f2d`。 | 2026-09-02 |
