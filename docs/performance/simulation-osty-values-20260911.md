# 奥斯蒂值执行与搜索

本阶段把已捕获奥斯蒂的承伤、复活、最大生命增长、攻击和两个完整回合迁入可撤销值状态。精确 `BODYGUARD`／`UNLEASH` 普通及升级卡牌进入编译闭包，共 33 种精确类型；模型与紧凑后端的完整状态、原键、估值、续用和固定节点搜索一致。首次创建宠物、初始遗物及原始亡灵完整输入仍在后续范围中。

## 状态与职责

`CreatureAttackLayout` 将宠物放在主要敌人区间之后，死亡保留身份，不让宠物阻止胜利或被敌方群体效果命中。代伤先扣玩家格挡，然后记录宠物损失和溢出玩家损失；完全格挡时也保留两个结果。两个伤害事件均先于死亡处理，宠物普通能力退休，代伤能力保留；玩家死亡随后直接杀死仍存活的宠物。毁灭的直接死亡保留格挡，不能伪造成伤害历史。

召唤区分存活增长和死亡复活，状态页记录死亡位与是否曾召唤。事件既有两个 64 位字保持宽度，空闲元数据位保存实际施伤者；卡牌和目标身份仍保留完整 32 位，不能截为单字节。读取器保留宠物的攻击／命中历史、双方代伤结果及根计数；出攻击牌的玩家与实际攻击的宠物分别记账。

`CompletedOstyReadBinding` 为每个 lane 独占，只导入当前生命、最大生命、格挡及原最大生命映射的缺席／已有值形状。它不执行召唤、伤害或 Power 命令，不新增每叶 Fork，不进入冻结候选。`ContinuationStamp` 从读视图取得宠物 HP，其余合法性和原键公式继续使用同一派生上下文。普通回合开始明确包括保留的宠物，额外玩家回合仅包含玩家。

整根准入仅允许精确奥斯蒂及其一层代伤能力、可选力量；死亡根只保留代伤能力。首次创建在执行前明确拒绝，`ModifySummonAmount`、`AfterOstyRevived` 和 `AfterSummon` 的未知观察者继续拒绝。没有为第三方开放紧凑效果登记表。

## 原版对照发现的旧行为偏差

1. 旧怪物伤害通过临时将代伤能力清零再恢复来避开 live 存活判断，会退休并重建能力，丢失回合初始量。失败 `5941956399b641d4806f9bcbed99d367` 的完整聚合快照已相同，但 `AmountOnTurnStart` 变为 0。现在 `ModifyUnblockedDamageTargetMirrors` 直接读取分支 HP，按原版链式返回目标，包括结束状态下的分发；未知覆盖显式抛出不支持，旧临时移除逻辑删除。
2. `GainMaxHp` 原版按实际封顶增量治疗。失败 `96aae02946f44a6a9985e9dbf1d13887` 在接近上限时原生 HP 为 999999992，旧／紧凑为 999999995；已到上限时原生 HP 为 999999990，旧／紧凑仍多治疗 5。旧映射还写入超过 999999999 的最大生命。两条执行路径现在按封顶后的净增量治疗，并记录实际最大生命。
3. 原版普通玩家回合包括所有 Allies，连死亡但保留的宠物也会更新能力初始量和清理格挡。失败 `9d045fd2920942f6a9e10da11d23a7ba` 在第一个完整回合看到原生代伤初始量 1，而旧／紧凑仍为 37。现在先冻结完整参与者，快照全部能力，清完全部格挡再逐个执行 AfterBlockCleared；额外玩家回合继续只取玩家。原生场景显式赋宠物 2 格挡，避免仅靠能力字段验证。

上述属于语义修正，不能描述成与旧错误实现逐位不变的性能优化。另一个早期失败只是 fixture 把宠物死亡套用敌人清扫阶段；修正断言后保留完整状态对照，没有放宽原键或评分相等。

## 本轮直接证据

| 场景 | 本轮结果 | 验证范围 |
|---|---|---|
| `COMPACT-OSTY-NATIVE` | Passed，26.08 秒 | 三个根、十五个原生步骤、两个完整回合；不同力量、已有宠物历史、部分／完全格挡、死亡空攻击、复活／增长、群体效果排除宠物；完整快照、能力元数据、历史、九 RNG、原键／估值／续用、缓存开关、逆序／撤销、八工作区及实机后根隔离 |
| `COMPACT-OSTY-CAP` | Passed，3.50 秒 | 接近／已到最大生命上限的两个原生召唤，三十组完整身份／施伤者／自动标记／flags 编码边界 |
| `COMPACT-OSTY-DEFEAT` | Passed，3.44 秒 | 毁灭阈值与错误相位、直接杀死玩家并连带宠物、保留格挡／代伤能力，无伤害历史；全部键、估值、八工作区及实机后冻结恢复 |
| `COMPACT-ROUND-NATIVE` | Passed，8.07 秒 | 三模式、二十分支、七原生动作、八个省略选择边界，完整状态／能力／历史／键／续用及恢复回归 |
| `COMPACT-OSTY-SEARCH` | Passed，25.10 秒 | 共享宠物建局加一层 Tools；固定 250 节点旧／新 DOP1 及紧凑 DOP2 完整政策相等，实际并发 2、挂起候选非零；取消／异常排空、根复用、未迁移药水拒绝及实机不变 |

时长是请求总时间（包括启动和建局），不是搜索性能。全部 runId、版本、失败原因、封顶原始标量和构建结果保存在[结构化证据](simulation-osty-values-20260911.json)。v8 的行为源码为最终实现；v9 只提取同一测试建局和增加搜索测试，没有重跑已通过的 v8 原生场景。v5 构建的注册表诊断接口／命名空间错误已在 v6 修复，保留失败日志；最终 v9 Release 构建 10.78 秒、零警告／错误。

## 重跑方式与限制

构建隔离产物并复制根 manifest；复用实例前须退出加载旧 DLL 的进程：

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false -o .local/compact-osty-values/artifact
cp CombatSolver.json .local/compact-osty-values/artifact/
./tools/run-unattended-test.sh --headless-instance compact-osty-values --combat-solver-build-dir .local/compact-osty-values/artifact --scenario-id COMPACT-OSTY-NATIVE --character-id SILENT --encounter-id MECHA_KNIGHT_ELITE --cards-json '[]' --enemy-current-hp 300 --timeout-seconds 120 --headless-fast-mode-for-test Instant --evidence-directory .local/compact-osty-values/native
```

替换 ScenarioId 可执行表中其余测试，Windows 使用对应 `.ps1` 和 PascalCase 参数，没有新增协议字段。测试内部注入宠物以隔离未迁移的角色初始遗物；这不是原始亡灵全输入验收。

本阶段未测新增性能，尚不据此声称加速。原始亡灵完整输入／动态闭包及双方原输入预热交错正常 NoGC 验收仍待完成；此前性能以[运行时对照](simulation-runtime-backend-20260911.md)为准。没有启动 Steam、安装、打包、发布或推送远端。

Linux 结构门禁通过（93 个 Search 文件），PowerShell 等价维护但未执行。CoverageCatalog `--verify-effective` 通过，生成元数据仅将代伤从旧补偿登记改为精确引擎镜像，没有本轮原生 IL 指纹变更；未重新验证无关场景。
