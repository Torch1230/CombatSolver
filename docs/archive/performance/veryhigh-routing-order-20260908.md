# 极高配置：路由上下文去重优化（2026-09-08）

[返回性能目录](../../performance/README.md) · [结构化证据](veryhigh-routing-order-20260908.json)

本轮只改 `BeamRetentionPolicy.RankBest` 的路由列表构建。没有调整 Beam、时间、节点、动作配额、并行度、GC 策略或战损排序。普通开发提交，不升版本、不发包。

## 原因与等价性

压力场景的 25 秒采样中，`AddRoutingContext` 内线性 `Any` 查重占旧reader保留的EventPipe线程样本约7.33%（当时误称CPU样本；[后续口径纠正](backend-cpu-hotspots-20260908.md)，不是实际CPU占比）。每次加入都扫描此前全部上下文，形成平方级工作，并为查重分配闭包/枚举对象。

上游 `nodesByRoutingChoice` 已按完整路由签名去重。后续 family 分组和 option 分块排序都是对这些唯一项的划分及排列；各 family 的 Effect 一致。唯一重复来源是：先发出持久效果的前 8 个上下文，再从所有 family 的第 0 项开始轮询。

现在第一遍直接追加持久前缀，第二遍只跳过持久 family 已输出的前 8 项，并按字典项数预分配结果列表。首次出现顺序、成员和所有后续配额完全不变，没有引入集合排序、额外保路或跨分支缓存。

采样也出现监听器重建、Fork 与快照开销。本轮没有修改这些所有权边界；快照拼接已经使用底层 List 的批量复制，未根据采样归因再添加重复优化。

## 目标场景

复用[压力筛查](veryhigh-pressure-survey-20260908.md)的死灵法师药水投影输入、种子和完整极高配置：DOP8、配置 NoGC 16,000,000,000 字节、120 秒请求上限、首搜索结果停止、详细诊断关闭。此输入是显式注入的组合，并非原存档精确回放。

| 指标 | 基线 | 候选 |
|---|---:|---:|
| 搜索耗时 | 58.531 秒 | 51.219 秒 |
| 累计 worker 分配 | 35.810 GB | 35.623 GB |
| 独立进程采样峰值 RSS | 16.845 GB | 18.228 GB |
| 展开 / 转移 / 选择分支 | 46,239 / 636,428 / 431,140 | 相同 |
| 最终阶段 / 战损投影 / 仅死亡路线 | Deep / 9 / 是 | 相同 |

耗时观测减少 12.49%，分配减少 0.52%；RSS 增加约 8.2%。这里的 GB 均为十进制。两次均是新 headless 进程，但基线复用上一轮证据，候选请求期间有短暂离线 trace 解析/编译活动，且 GC 准入自适应预算不同（基线 16.000 GB、候选约 15.405 GB）。这是单样本观察，不能作为严格可重复的倍率或内存增幅保证。

总 GC 暂停观测从 1,813.06 ms 变为 85.49 ms，最长观测从 1,745.901 ms 变为 30.094 ms；本轮没有改 GC 策略，不能据此宣称长暂停已解决。调度相关的 replay wave/deferred layer 计数会变化；不把相同总转移数写成全部调度指标相同。

`Passed` 表示请求及其断言完成；目标场景两版仍只有死亡路线，不是战斗胜利证明。高分配仍主要存在，本轮没有实现两倍收益或消除 GC 压力。

## 正确性及完成范围

- 机甲骑士整场 headless：`f4007c4a2da645e3b3cbbb22c4687eca`，战损8、57HP、第7回合结束、无药、零计划外重算。27条实际部署出牌及目标/选择摘要与上一轮基线一致，整场胜利通过。
- `ForkBoundaries` 合同合集包含既有路由 portfolio、8项分块、公平配额和重复/遗漏检查；最终通过（`7ae66da70a944340b8b1abcc3ad1cc9c`）。首次输入缺 `treatAsDeckCard`，在无牌组身份的测试前置条件处失败；补齐后暴露旧合同仍要求移除后复用 Power 实例，原始基线同样失败。依照现有重新获得规则修正测试，断言新实例唯一注册、旧实例退出当前视图及父子分支隔离；没有改生产逻辑绕过。随后还补入合集要求的未受污染技能牌（防御）；该次只缺测试输入。最终产物仅追加这个测试修正，生产搜索代码与性能/整场证据相同，未重复跑性能。
- Release 构建零警告/错误，Bash 与 PowerShell 结构门禁通过。无职责迁移、协议变化或第三方登记变化。
- 遵从用户暂不测可见会话的要求；没有运行可见性能、Windows 游戏、逐转移增量或完整发布门禁。临时安装的 DLL/manifest 已恢复。

合同复跑使用 `--character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --clear-player-piles --cards-json '[{"cardId":"STRIKE_IRONCLAD","pile":"Hand","count":1,"treatAsDeckCard":true},{"cardId":"DEFEND_IRONCLAD","pile":"Hand","count":1,"treatAsDeckCard":true}]' --verify-fork-boundaries --stop-after-combat-root-snapshot-assertion --performance-preset-for-test VeryHigh --timeout-seconds 120`。性能输入沿用压力报告的可移植牌组、遗物和药水 JSON；每次请求固定 `--combat-solver-build-dir`，不得让复用进程切回默认产物。
