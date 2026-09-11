# 原始机甲完整路线的紧凑执行对照

原始 30 张牌、附魔与蛇之戒保持不变，紧凑程序已通过整场组合语义验证：44 个原生动作在第 8 回合胜利，最终 59/65 HP；沿所选路线的 424 个已准入替代分支逐一对账。生产搜索仍使用旧后端生成路线，本项没有性能结论。

## 实现与发现

`Testing/CompactPlanReplay` 使用正式 `PlanAction` 协议，根据卡牌状态键及出现序号定位手牌，根据选牌 token 的状态和选项序号恢复选择。每个读取器只创建一次私有元数据上下文；执行中的帧、选择、牌堆、RNG、Power 和回合值仍属于紧凑工作区。未表示的药水、重复出牌和强制结束回合协议显式拒绝。

完整组合发现原有阶段测试遗漏了最后攻击牌的跨回合记账：先执行 `BLADE_OF_INK` 与 `SUPPRESS`，再结束回合，旧引擎将本回合最后一张攻击牌提交为上回合攻击牌，紧凑路径没有提交。完整状态与续用文本相等，但原状态键不同，足以影响搜索去重。

新增 `CommitPlayerTurnHistory` 值事件，位于手牌末尾阶段之后、首个终局检查之前，与旧 `CommitHistoryCourseTurn` 的位置一致。投影复用原记账方法，完成读取器分别提供本回合及上回合的攻击牌映射，原键继续使用原有编码。没有新增另一套评分或去重规则。

## 验证范围

最终 `COMPACT-FULL-ROUTE-NATIVE`，runId `c42c12e1eb8b42ddbdf6cbe6b717bc80`，Passed，45.55 秒。请求使用原始 `VH_PERF_MECHA` seed、300 敌人 HP、65/65 玩家 HP、完整 30 牌及 31 个运行级监听器；VeryHigh、DOP8、Smart，保持原搜索预算。请求与逐动作摘要见[结构化证据](simulation-full-route-20260911.json)。

- 旧正式 coordinator 生成完整路线；在每个所选前缀调用原 `Expand`，核对它返回的全部已准入替代分支，共 424 个。该数目不代表整棵搜索树的所有状态。
- 所选路线共 44 个动作，包含 8 次结束回合、普通和附魔小刀生成、X 费用、Power 移除、弃抽、主选择和起手选择、蛇形费用变化、两次洗牌、灼伤及敌方中毒终局。
- 每个被核对的分支比较完整状态、Power 全字段、来源历史、九条 RNG、原状态键及所有估值属性；直接读取器的缓存开关与旧事件投影均对账。
- 替代候选逆序恢复；两个独立工作区重放完整路线，逐前缀核对冻结值与完整估值。原生通过公开入口执行所选路线，每步核对完整状态、逐实例有序牌堆及仍在战斗中的卡牌指纹；终局在原生清理前捕获。
- 原生整场结束后再次从冻结根重放，终局完整估值与父根均保持一致。

v1 Release 因测试文件缺少 `PredictedCard` 命名空间失败；v2/v3 原生请求在同一个回合历史状态键问题上失败，v3 增加字段诊断确认原因。v4 修正后通过；没有将失败样本计为通过。最终 Release 10.97 秒、零警告／错误，纯值工具既有全部合同通过。Linux 结构门禁通过；PowerShell 规则同步，未执行。原生实例已停止。

复现时从 [`performance-veryhigh-mecha-native.json`](../../coverage/unattended/performance-veryhigh-mecha-native.json) 提取 `runCards`，使用报告 JSON 中的参数；不要用 `cards` 代替运行牌组。测试自己调用 coordinator，再通过原生入口部署，它没有经过 Runtime 的 GC 生命周期或全自动续用编排；请求中 NoGC16GB 为配置断言，不是已开启无 GC 区的性能证据，也不是最终零计划外重算验收。

```bash
python3 - <<'PY'
import json
from pathlib import Path
Path('.local/compact-route').mkdir(parents=True, exist_ok=True)
source = json.load(open('coverage/unattended/performance-veryhigh-mecha-native.json'))
Path('.local/compact-route/run-cards.json').write_text(json.dumps(source['runCards']))
PY
./tools/run-unattended-test.sh --scenario-id COMPACT-FULL-ROUTE-NATIVE \
  --character-id SILENT --encounter-id MECHA_KNIGHT_ELITE --seed VH_PERF_MECHA \
  --enemy-current-hp 300 --initial-player-hp 65 --initial-player-max-hp 65 \
  --clear-run-deck --run-cards-path .local/compact-route/run-cards.json --cards-json '[]' \
  --combat-solver-build-dir <release-artifact> --headless-instance compact-full-route \
  --evidence-directory .local/compact-route/native --headless-fast-mode-for-test Instant \
  --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 \
  --performance-preset-for-test VeryHigh --search-max-degree-of-parallelism-for-test 8 \
  --potion-policy-for-test Smart --enable-no-gc-region-for-test 1 \
  --no-gc-region-budget-gigabytes-for-test 16 --timeout-seconds 120
```

亡灵完整输入及动态生成闭包、正式后端选择、搜索政策账本／排序、DOP1／2 与取消／失败排空，以及最终两场完整 A/B 和部署仍需继续完成。本项不改变这些目标。

后续职责迁移：六个准入／读取／计划适配文件移至 `Prediction/Compact`，方法体与调用顺序不变，Testing 继续持有差分和计量。Release 10.46 秒、零警告／错误；代表完整路线请求 `ec8fa69959f742e4b8c1390566b2fc0a` Passed，45.68 秒，44 原生动作及 424 替代分支再次通过，完整路线取证对象与迁移前逐字段相同。Linux 门禁通过，生产后端尚未启用。
