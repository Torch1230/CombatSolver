# 重复卡牌与有序手牌

记录日期：2026-11-15。基线为 `3677d4ca`，客户端版本为 0.51.5。

## 问题与修复

相同状态的卡牌位于不同手牌位置时，打出后的手牌顺序可能不同。旧枚举只比较单张牌的状态，提前删除了后一个出牌位置。

最小手牌为 `[打击 A, 防御, 打击 B, 坚毅]`：

| 打出的牌 | 剩余手牌 | 随后坚毅随机消耗第 1 张牌 |
| --- | --- | --- |
| 打击 A | 防御、打击 B、坚毅 | 防御 |
| 打击 B | 打击 A、防御、坚毅 | 打击 |

未升级坚毅使用原生 `CombatCardSelection.NextItem(hand.Cards)`。预测镜像与原生结算均保留手牌顺序，错误发生在 Search 候选准入层。

修复同时覆盖 3 个入口：

- 普通候选枚举保留非等价的移除位置。
- 开局能力牌枚举采用同一判断，检查被过滤掉的间隔牌。
- 循环重放把不同残余牌序视为替代动作，交回普通搜索。

判定沿用现有逐牌语义指纹。两个端点的指纹相同时，只有中间各牌也具有该指纹，移除结果才可合并。连续同状态牌仍去重；不同位置的卡牌实例身份不因此获得额外等价保证。

普通枚举不再保存固定 10 槽的全手牌去重表。该修改不改变状态键、随机流、模拟结算或配置预算。

## 原生最小合同

环境：Windows，游戏 0.113.1，RitsuLib 0.6.13，.NET SDK 9.0.321。

本机辅助 Mod 栈包含：

- DamageMeter 1.14.7。
- ModConfig 0.2.3。
- RemoveMultiplayerPlayerLimit 0.0.5。
- Rewind 0.26.19。
- RouteSuggest 1.9.0。

场景清空牌堆、遗物和 Power，再建立最小手牌。捕获根之前推进原生选择随机流，直到下一次二选一选择第 1 项。预测与原生随后消费相同的完整随机状态。

| 场景 | 修改前 | 修改后 |
| --- | --- | --- |
| `ORDERED-HAND-DUPLICATES-FIRST` | 原生及完整差分通过，候选序号仅 `[0]`，枚举断言失败 | 通过，27.65 秒；候选序号 `[0,1]` |
| `ORDERED-HAND-DUPLICATES-SECOND` | 原生及完整差分通过，候选序号仅 `[0]`，枚举断言失败 | 通过，27.71 秒；候选序号 `[0,1]` |

两种原生请求的完整根相同。修改前后的完整根及两条预测续用也分别相同。证据与 runId 见[结构化记录](ordered-hand-duplicates.json)。

合同在每次打击和坚毅后比较完整原生状态及续用。父分支和兄弟 Fork 在预测及实机推进后均保持。相邻重复牌与中间夹伤口的场景另检查普通候选、循环替代动作和开局能力牌。

```text
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId ORDERED-HAND-DUPLICATES-FIRST -EnemyCurrentHp 100 -TimeoutSeconds 120 -CleanupInstanceOnExit
./tools/testing/run-unattended-test.sh --scenario-id ORDERED-HAND-DUPLICATES-FIRST --enemy-current-hp 100 --timeout-seconds 120 --cleanup-instance-on-exit
```

将 ScenarioId 改为 `ORDERED-HAND-DUPLICATES-SECOND` 可执行另一位置。

## 固定搜索对照

对照使用当前版本的原生开局行动，不覆盖为历史行动 `INHALE`。

| 用途 | 输入 |
| --- | --- |
| 目标 | `coverage/fixtures/search/ordered-hand-duplicates.json` |
| 相邻重复过牌与洗牌哨兵 | `coverage/fixtures/search/ordered-hand-adjacent-loop.json` |

离线宿主采用单次 `Evaluate` 搜索。固定 Low、12 宽度、128 节点、10000 毫秒、并行度 1，关闭药水、组合和 No-GC。按基线、候选、候选、基线顺序启动独立进程，各测 2 次。

| 场景 | 展开 / 转移，基线 → 候选 | 搜索耗时，基线 → 候选 | 路线质量 |
| --- | --- | --- | --- |
| 目标 | 39 / 110 → 45 / 128 | 909.81 / 965.56 → 917.24 / 780.00 毫秒 | 均第 2 回合获胜，4 战损、0 药水 |
| 哨兵 | 9 / 21 → 9 / 21 | 1019.20 / 1045.43 → 956.04 / 951.78 毫秒 | 均第 1 回合获胜，6 战损、0 药水 |

两组的完整根、政策、选中动作、全部续用及最终快照相同。哨兵剪枝计数也相同；目标增加 6 个展开节点和 18 次转移。

目标搜索分配从 5453696 / 5453672 字节增至 5925512 / 5908224 字节。哨兵从 3179712 / 3179072 字节变为 3177912 / 3177272 字节。新增合法分支有实际成本，不按分配减少或整搜提速汇报。

本组未观察到超出基线波动的耗时增加。每版仅 2 次计时，不能据此推断稳定提速或高预算成本。所有样本均未触及时间边界。

复跑命令如下。通过 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 分别选择基线和候选 DLL；哨兵替换请求路径。

```text
dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --request coverage/fixtures/search/ordered-hand-duplicates.json --profile Low --nodes 128 --dop 1 --budget-ms 10000 --potion-policy Disabled --search-mode Evaluate --label C1 --out .local/ordered-hand-target
```

## 验证边界

目标和哨兵另经原生整场部署验收，均开启增量回放核对。两项均使用 `Instant / 0 秒`，计划外重算均为 0。

| 场景 | 原生结果 | 请求总耗时 |
| --- | --- | --- |
| 目标 | 第 2 回合获胜 | 28.20 秒 |
| 哨兵 | 第 1 回合获胜 | 27.32 秒 |

原生入口记录的有效配置为 Custom、45 宽度、128 节点、60000 毫秒。该配置与前述离线对照分开记录，原生请求耗时不用于性能比较。配置原文、runId 和验收项保存在配套 JSON 中。

原生请求使用 PR #247 的启动器，通过游戏补丁版本的依赖预检查。该启动器修改不包含在本次提交中，隔离实例均按请求清理。

Linux 运行、并行度大于 1、宽组合、高预算和可见 Steam 帧时间尚未验证。上述辅助 Mod 栈下的原生结果不能替代仅安装 CombatSolver 与 RitsuLib 的环境结论。
