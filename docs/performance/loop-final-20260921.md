# 循环优化收尾：请求额度、前缀续搜与历史依赖

本轮基线为 `a8a90e74`（包含首批循环优化及审计遥测修正），不是原始 `3f4002bd`。原始版本的收益见[首批报告](loop-optimization-20260921.md)。本轮逐次指标、预算原因、路线摘要和原生 runId 见[结构化证据](loop-final-20260921-evidence.json)，固定输入见 `coverage/unattended/loop-final-20260921/suite.json`。不提升版本、不发包、不启动可见 Steam。

## 设计与质量边界

- **历史依赖在根冻结。** 原来一个读者就使六类累计值全部入键；现在按实际读者保留必要项。BansheesCry / PullFromBelow 只需 EtherealPlays，GoldAxe 只需 FinishedPlays，另外四类读者分别对应自己的计数。编码仍保留原六槽位，仅把无依赖项写零，All 保持原编码。根扫描全部战斗牌（含消耗堆），不在热路径扫描牌堆或历史。
- **未来读者不能漏。** 随机生成、变牌、间接生成药水、Nightmare 保存副本，以及第三方模型、订阅者、BaseLib 修饰器和 AdaptedOnPlay 快照保守使用 All。原版来源表是显式维护边界；新增开放生成入口必须补表，原生合同枚举 CardGenerationCardMirrors 的公开来源防止遗漏。固定衍生牌和普通复制不引入新读者类型。第三方六项之外的历史语义仍需专门适配。
- **额外回放按请求限额。** `SearchRequestWorkTotals` 原子消费最多 4096 动作；Coordinator 的主搜、药水审计、Beam 组合共享同一实例，独立 Evaluate 创建私有额度。Expanded 仍只计普通父节点展开，Transitions 包含真实回放；外部同时提供两者。`CycleReplayActions` 属于所选 solver，`TotalCycleReplayActions` 属于请求。
- **已验证前缀可续搜。** 触顶时把无损、合法前缀加入普通 frontier，不丢弃数千步模拟。每步仍走真实 ReplayAction，保留完整动作父链并附加普通循环调度证据；普通候选与之前的 EndTurn 出口继续参与搜索。中间 simulator 逐步释放。非法、漂移、跨回合、受伤或风险仍终止；时间/取消/内存检查保留。不外推伤害，不按保守轮数估算提前拒绝，所以 4095 实际动作、4101 估算动作的边界仍可完成。
- **第一次执行前不烧掉 region。** 有替代可执行牌或目标时未启动真实回放，后续可以再试；一旦执行第一步，该回合/形状区域只尝试一次。始终存在分支的八牌循环仍走普通搜索，不能宣称已经普遍加速。

共享有限额度意味着后续 solver 不一定还有加速额度；它们保留普通搜索。有限 Beam / 墙钟搜索没有任意根上质量单调的数学保证，本轮用固定根、原生合同和反例检验，不把“加法候选”当成全局不退化证明。

## 对照结果

28 组场景/策略配置（26 个不同根）中，23 根完整路线与质量指标相同；5 根改善，其中 2 根是旧候选在节点预算内未击杀、新候选完成胜利。没有观察到候选胜负、战损或用药退步。首次消耗堆夹具误写了本版本不存在的 EXHUME，两侧都建局失败；已记录并改正，失败不计入 28 组结论。首轮候选之后仅追加 Nightmare 保存副本和原版 OnPlay 适配的保守回退；最终 ABBA 和生成来源原生合同来自最终构建。

| 场景 | 旧 → 新 Expanded / Transitions | 旧 → 新质量 |
|---|---|---|
| 7200 HP，Evaluate | 4675 / 13471 → 441 / 4978 | 4 HP / T2 → 0 HP / T1，均无药 |
| 历史读者在手牌 | 380 / 684 → 74 / 135 | 同路线，1 HP / T2 |
| 历史读者稍后抽到 | 540 / 1005 → 140 / 252 | 同路线，1 HP / T2 |
| 6835 HP，4095 动作边界 | 6 / 4107 → 6 / 4107 | 同路线，0 HP / T1 |
| Coordinator Smart | 4677 / 13475 → 441 / 4978 | 4 HP / T2 → 0 HP / T1，无药 |
| Coordinator RequireAtLeastOne | 12000 / 33069 → 892 / 6322 | 剩敌 775 HP → 0 HP / T1，均用 1 药 |
| Coordinator Beam 组合 | 9657 / 27573 → 441 / 4978 | 4 HP / T2 → 0 HP / T1，无药 |
| 9000 HP，多 solver 组合，6000 节点配置 | 12000 / 34792 → 6000 / 16179 | 剩敌 810 HP → 4 HP / T2，无药 |

最后一根新候选所选 solver 只有 1917 展开 / 7955 转移，请求总值为 6000 / 16179，证明实际运行了其他成员；请求回放总数仍为 4096。旧组合的请求节点数本就可能叠加，这里不把 6000 配置说成旧版请求总展开硬上限。DOP2 的 cap / margin 与 DOP1 的候选完整路线及质量一致；不外推所有分支根的普遍 DOP 等价。

工具保留 `Different`、`Failed` 与非零退出码：3 根胜利改善为 Different，2 根旧版未击杀为 Failed；没有把改善自动改成 Equivalent。人工结论来自胜负、战损、用药、回合、根和完整路线逐项对照。

## 性能与实际代价

以下是串行 ABBA 的 Search 总耗时和累计分配，不是可见帧率或完整进程启动时间。峰值为离线宿主采样。28 组常规配置为 Low / DOP1 / 20 秒 / 每根显式节点数；cap 的 20 秒 ABBA 中 B2 出现 time=1、T2，整组标为 Inconclusive，**不计算该组收益**。随后另设两侧相同 60 秒窗口、仍为 12000 节点的实验，排除 5 秒回合份额的干扰；四份均无时间切层，旧侧依旧 4675 展开、新侧依旧 441，生产预算未修改。

| ABBA 场景 | 搜索耗时 ms，旧 → 新 | 累计分配 MB，旧 → 新 | 采样活对象峰值变化 / RSS 变化 |
|---|---|---|---|
| cap，60 秒隔离窗口 | 5086 → 2244（−55.9%） | 593.23 → 178.10（−70.0%） | −54.1% / −29.1% |
| 历史牌在手 | 530 → 391（−26.1%） | 26.28 → 8.33（−68.3%） | −9.0% / −2.3% |
| 历史牌待抽 | 609 → 423（−30.6%） | 36.91 → 12.19（−67.0%） | −13.6% / −1.7% |
| 4095 动作成功边界 | 1705 → 1772（**+3.9%**） | 135.60 → 137.09（**+1.1%**） | +4.4% / 约 0% |
| 3 展开短根 | 362 → 358（−1.2%） | 3.29 → 3.35（+2.0%） | 见逐次数据 |
| 敌人带格挡短根 | 375 → 370（−1.3%） | 4.37 → 4.38（+0.3%） | 见逐次数据 |

4095 成功边界的小幅成本是本轮明确保留的取舍：逐节点维护普通调度证据，使触顶前缀能够安全交回搜索；它不再只服务最终击杀。短根单次曾观察到时间 +125%、另一根分配 +8%，完整 ABBA 没有复现该量级，原单次数据仍保留。不能承诺每次运行都有表中的精确比例。开放生成与第三方来源为正确性保守扩键，可能增加搜索状态；本轮生成场景未观察到大幅退化，但不代替完整生成语料验收。

## 原生合同与剩余范围

- `LOOP-HISTORY-DEPENDENCIES` / `f64d28e612a44bfb9a9527c952fe3ff0`：三种牌堆、无关历史合并/相关历史分离、Fork、未来生成 GoldAxe 读取先前历史、所有公开生成卡来源、独立保存副本、第三方回退、8 个并发消费者精确共享 4096。Passed。
- `LOOP-REPLAY-REQUEST-BUDGET` / `b1fad46ba0d34b9b9486ecf83c6c3fde`：预先消费 4000，再运行同根两个真实 solver；分别 96/0 回放、首个产生一条前缀续搜，总额 4096；两者零损 T1、严格增量校验、live 根不变。Passed。
- `LOOP-DEFENSE-THORNS-RESERVE` / `ccc3903662d7423da94932417818050a`：回合末投影不计出牌反伤，两张防御吸收两次 4 点反伤，4 动作完整原生部署，0 HP / T1、0 计划外重算。Passed。
- `LOOP-DEFENSE-REPLAYED-THORNS-RESERVE` / `caeaba92711e4eb79415b86d33b0fabc`：下一攻击重复，必须先累计两张防御，再打唯一攻击吸收两次反伤；3 动作完整原生部署，严格增量、0 HP / T1、0 计划外重算。Passed。这是投影范围外伤害的必要格挡哨兵，不声称修复所有怪物动态伤害低估。
- Release 构建 0 warning / 0 error；Python 分类合同 10 项通过；Linux / PowerShell 两套结构门禁通过。所有原生实例已清理。

EQ10 / FULL40 历史根在该工作树不可得，未执行；本轮 28 组不冒充历史语料。未启动可见 Steam，未验收 FPS、布局和真实主线程峰值。UI 行复用与本地化没有本轮源码变化，沿用首批记录。长期复杂多分支循环的普遍加速、第三方新历史种类和新的清空格挡保留上限不扩入本分支。

复跑离线对照（DLL 两侧均需支持固定夹具入口）：

```bash
python3 tools/OfflineSearchHarness/run_loop_boundaries.py \
  --suite coverage/unattended/loop-final-20260921/suite.json \
  --baseline-dll <a8a90e74-DLL> --candidate-dll <candidate-DLL> --out <new-directory>
# 针对最终四根 ABBA：追加 --cases letter-replay-cap estimate-margin history-banshee-hand history-banshee-drawn --abba
# 60 秒隔离实验：仅在 suite 副本改 budgetMilliseconds=60000，节点数不改。
```

原生历史合同使用 `--scenario-id LOOP-HISTORY-DEPENDENCIES --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --seed LOOPHISTORYDEPENDENCIES --clear-player-piles --clear-all-powers --cards-json '[]' --timeout-seconds 120 --cleanup-instance-on-exit`。预算合同使用 `LOOP-REPLAY-REQUEST-BUDGET`、敌方 max/current HP 均 200、能量 0、Hand/Discard 各一张 IMPATIENCE、LETTER_OPENER；测试内部固定两个 solver 的共享额度及严格增量开关。防守两根参数完整保存在对应 JSON；原生启动器按字段映射传入，并保留严格增量、完整部署与清理开关。
