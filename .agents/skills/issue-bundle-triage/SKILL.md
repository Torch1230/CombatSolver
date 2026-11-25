---
name: issue-bundle-triage
description: Triage CombatSolver player issue ZIPs, combat bug reports, replay bundles, or minimal fuzz/soak repro files using logs and OfflineSearchHarness.
---

# Issue Bundle & Bug Report Triage

收到玩家上传的问题包 ZIP、战斗记录或 `fuzz`/`soak` 最小复现 JSON 时使用本指南，用最短路径定位根因。

## 1. 安全解包与快速初筛

1. **隔离解压**：把玩家 ZIP 解压到仓库忽略目录 `.local/tmp/<bundle-name>/`（排查完清理），不要污染工作区根目录。
2. **先看关键摘要与时间线**：
   - `diagnostics/logs/index.json`（错误分类、版本、加载 Mod 列表、事件路径）
   - `diagnostics/logs/events.jsonl`（报错前后的根捕获、搜索请求、部署动作与重算触发）
   - `replay/` 或检查点文件（初始手牌/遗物/药水/敌人、用户动作序列与预测状态）
3. **初步归因分流**：
   - 含第三方未知机制或明确不支持异常 → 核对是否属于兼容范围或缺漏导入（`.local/decompiled/` 与第三方适配）。
   - 提示部署动作匹配失败 / 触发计划外重算 → 排查 `HotCombatEngine` 语义差分或 `NativeChoiceRuntime` 实例匹配。
   - 搜索卡顿、超时或明显劣质路线 → 排查 `HotSearch` 剪枝/评分/预算或 `HotPotionPolicy`。

## 2. 用 `OfflineSearchHarness` 秒级复现（首选）

绝大多数战斗语义与路线问题**不需要启动 Godot 实机**，直接用纯 .NET 离线宿主复现即可：

```powershell
# 1) 玩家 ZIP 问题包锁定步进 + 部署差分检查
OfflineSearchHarness.exe --replay-bundle .local/tmp/bundle.zip --lockstep-actions --deploy --dump-diff .local/tmp/diff.json

# 2) Fuzz / Soak 产生的最小复现 JSON
OfflineSearchHarness.exe --replay-fuzz .local/tmp/repro.json
```

- 也可直接在 `--replay-bundle` 后附加 `--profile` / `--beam` / `--nodes` / `--potion-policy` / `--no-rollout` 验证特定搜索配置下的表现差异。

## 3. 按需启用无头实机验证

仅当离线复现无法覆盖问题（例如 Godot 场景生命周期、界面显示异常、实机出牌动画时序、特定第三方 Mod 仅在实机加载生效）时，再调用无头测试入口：

```powershell
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ReplayBundlePath .local/tmp/bundle.zip -CleanupInstanceOnExit -TimeoutSeconds 120
```

排查结束后简洁汇报：**已确认根因**（标注问题归属层与具体代码位置）、**复现结果**（离线还是实机、关键差分指标）、以及**最小修复方案**（或明确标明因缺少原版/第三方环境而未能复现的范围）。
