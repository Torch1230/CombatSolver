# 2026-09-07 逻辑问题批次交接

## 停止点与发布范围

维护者已要求暂停继续排查，先将现有批次及 PR #57、#58 合并 main，发布 0.33.0。此文用于下一次明确恢复修复时接手，计划项均未自动完成。

本批从 0.32.0 开始，截至 `3af25e4` 已提交 21 项逻辑修复，另有 WheelchairSpire 不兼容拒绝及分诊/测试补充。逐项根因、提交和证据见 [分诊台账](report-logic-bugs-20260907.md) 与 [测试矩阵](../../TEST_MATRIX.md)。117 份报告按共享根因归并，不等于 117 项独立缺陷，也未全部恢复验证。

## 材料位置

- 原始三个包位于本机 `D:\Download\Edgedownload`，文件名分别为 `combatsolver-reports-20260907-135612.zip`（9 份）、`135521.zip`（45 份）、`135438.zip`（63 份；后两项沿用相同文件名前缀）。
- 本机解包：`.local/issue-bundles/reports-20260907/raw/<外层包名>/logs/`。分组清单：`.local/issue-bundles/reports-20260907/triage/classified.json`，含完整 ID、archive、triage_group、signals、audit、first_events。下表用 ID 前缀查找唯一条目。
- 内层检查点位于 `combat-solver/forensics/current/replay-state/`，日志通常为 `combat-solver/forensics/current/logs/godot.log`，部分只有 `logs/00-godot.log`。
- 原版只读参考：`.local/decompiled/sts2-v0.111.0/`。本机材料不提交 Git；其他机器接手需要对应问题包。
- `4f52cb972f4447c5b87dcd4b1b3a9f2e` 精确恢复被 `environment_mismatch:mods` 拒绝。`95e19fb8` 只通过 Preflight。两者都不能视作已恢复，继续沿用原环境一致性门。

## 待处理入口

| 报告/组 | 已知事实及下一步 |
|---|---|
| `90d0e56c` 尸蛞蝓 | 暂停时正在读原版：预测 GOOP_MOVE，实际 WHIP_SLAP_MOVE，并缺少实际 Frail 2。Ravenous 在同阵营死亡时眩晕，恢复行动依赖 StateLog。下一步从两只存活、尚未击杀的根开始，击杀一只后走两回合完整差分。重复死亡修复可能相关，尚未证明覆盖原包；尚未写此 fixture。 |
| `979c5109` 蜈蚣 | 预测 DEAD_MOVE，实际 REATTACH_MOVE。相邻重新接合/再次死亡测试已通过，原时间线仍待恢复。 |
| `44a62cbc` 实验体 | 落选分支恢复时指纹不同，另有终局后动作错误。根含 Nemesis、Doom 11、Curious 1、Shroud 8、SentryMode 1；缺完整失败路线前缀。已有阶段根捕获，不能直接归因为 RNG。 |
| `c3f8cf86` 忧郁 | T3 动作 11 预计费用 2，实际手牌同身份费用 3。奥斯提死亡四牌堆及 Fork 最小原生检查已通过，原路线未验证。 |
| `84cca2ef` 步法 | T2 动作 8，计划牌完全不在手牌，与忧郁费用问题不同。旧日志无 PLAN_REPLAY_STATE，需恢复更早动作找首个偏差。 |
| `663dfe38` 抽牌递归 | T7、动作 30，Pagestorm AfterCardDrawn 链达到 100 层。原版也在 Add 后触发 AfterDraw，不能用跳过失败 Add 或提高深度当修复。根因未证实；不扩展调查外部 Mod。 |
| `5ffe912c`、`cc86335f` 循环出口 | 父租约撤销后的子观测准入边界已修，原包完整触发链尚未回放。 |
| `1aec4ced`、`7899d83b`、`bebb1bed` | TOASTY_MITTENS。基础 SILENT 加 BAG_OF_PREPARATION 的 9 张开局测试通过，原包完整组合未验证。 |
| `06b250ca` | 5 选项 Grid，预期 Accuracy、实际 SerpentForm，来源为空且无接管/选择轨迹；可能为手动选择，尚未确认缺陷。 |
| `1965602a`、`0da8cd51`、`773f2520` | GAMBLING_CHIP 弃牌/手牌顺序；首份日志被外部错误覆盖，后两份保留调查。 |
| 27 份蟹皇恢复评分 | 同指纹异评分与遗漏包围朝向相符；共享/独立评分缓存隔离测试通过，未逐份完整恢复。 |
| `387a2e1c` 女王 | T8 实际随从已离场，预测仍在，实际女王少 9 HP。炼狱击杀随从而女王存活的直接原生差分通过，原包提前死亡原因未知。 |
| 其余 HP、弃牌顺序、路线标注组 | 保留分诊台账中的状态，按首个错误字段继续选代表，不将局部测试通过扩称原报告解决。 |

## 明确排除

维护者要求 WheelchairSpire 直接判不兼容，已按清单 ID 与程序集拒绝；`bc23d7c8` 属于此边界，不适配其数值。`4dcb76b4` 的 MYCHARACTERMOD-INSPIRED_PURSUIT_RECORD 动态变量规格缺失属于外部适配缺口，本批排除。

## 继续工作的最短路径

先读项目 AGENTS、issue-bundle-triage 与对应语义 skill，然后从上表选择一个未解决入口。以完整 ID 定位材料，明确首个错误状态，构造不超过 120 秒的最小原生/预测差分；新增跨回合状态时检查 Fork 和续用。保持完整状态核对，不以聚合 HP 相同替代通过。

最新已通过证据：双尾鼠新召唤行动 `370dae413cb94195b2f5143a17132ad9`；相邻蜈蚣重新接合 `d74dcb906e9543d8b1e336456de303dc`。命令与其他 21 项证据均在测试矩阵，输入与行为源码未变时不重复。

PR #57 保留叠加污染，但多生命火花的新入场施加顺序仍有既有近似。PR #58 增加隐藏状态指纹与根捕获登记，尚未补充对应续用核对入口，第三方角色整场适配仍需独立验收。
