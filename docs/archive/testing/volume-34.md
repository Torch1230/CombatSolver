# 测试历史卷 34

## 1.0.1 策略通路恢复与高风险重算清零

基线：基于 `1.0.1` 最终源码与 Release 构建（Windows / STS2 `v0.113.1` / .NET 9），全部验证严格串行执行并覆盖最终源码。

### 构建与离线内核门禁（`OfflineSearchHarness`）

- `dotnet build CombatSolver.csproj -c Release` 与 `dotnet build tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release` 均为 **0 警告 0 错误**（发布准备前后续只改版本号、文档及最小 ZIP 的正式发布构建亦通过 `Assert-ReleaseConnectionMetadata`）。
- `--check-hot-layout`：**7/7 PASS**（35 ms，`FixedCore=1128 B`，`Total=17256 B`，`CombatHotState` `0 B` 增量）。
- `--audit-hot-coverage`：**0 缺口**，**183/183** 反射校验通过（1,698 ms；覆盖 Cards 673、Powers 290、Relics 314、Potions 67、Monsters+Moves 121、Enchantments 22、Afflictions 7、Orbs 5、Dominions 12）。
- `--verify-hot-parity --dop 8`：**0 failures**——Gate A 19/19 PASS、Gate B1 6 角色 246 步自洽 0 失败、Gate B2 4/4 `DOP=1 == DOP=8`、Gate C `0.61 us/step`（`1,633,923 trans/s`，`0.0 B/step`）。

### 策略合同与定向差分回归

- `--verify-strategy-policies`：**4/4 套件、25/25 项全通过**（3,754 ms）——Suite A（全部 11 个局外成长源 11/11）、Suite B（`HotSearch` 长线收益保留/零额度/不考虑局外收益 6/6）、Suite C（卡遗物对齐/推迟击杀/`MeatOnTheBone` 净正回血与打平界线 6/6）、Suite D（`BrightestFlameMaxHpLossLimit` 2/2）。
- `--replay-fuzz .local/tool-tasks/v101-wc/fuzz-hr-500/EndTurn__END_TURN__H__None.json`（`ROCKET_PUNCH`）：22 步完整回放 **0 diffs / 0 crashes**（2,696 ms）。

### 千场差分模糊与规模化部署压测（`OfflineSearchHarness`）

- `--fuzz-combat --fights 500 --seed 4026 --phase high-risk --max-turns 20`（62.64 s，13,091 步）：**`pass_fights = 500 / 500`**（较基线 `455/500` 收敛至全过），**`diff_buckets = 0`**、**`crash_buckets = 0`**；高风险池覆盖 Cards 647 (played 618)、Powers 270、Relics 178、Potions 67 (used 66)、Monsters 100、Enchantments 20、Afflictions 7、Orbs 5、Dominions 12。
- `--fuzz-combat --fights 500 --seed 3026 --phase all --max-turns 20`（64.59 s，11,879 步）：**`pass_fights = 500 / 500`**、**`diff_buckets = 0`**、**`crash_buckets = 0`**；全单人实体覆盖 Cards 673 (played 637)、Powers 268、Relics 192、Potions 67 (used 66)、Monsters 106、Enchantments 22、Afflictions 7、Orbs 5、Dominions 12。
- `--soak-deploy --fights 300 --seed 4026 --pool high-risk --profile Medium --dop 8`（382.08 s）：**`wins = 286 / 300`（95.3%）**、`defeats = 14`、**`hard_error_count = 0`**、**`choice_mismatch_count = 0`**、**`unplanned_replan_count = 0 / 1,454 turns`（0.00%，`reused = 1,154`、`planned_boundary = 0`）**（较基线 28 次计划外重算与 1 次硬错误/错配全部清零），`custom_beam135_stage2_ok = True`，`avg_hp_loss = 15.67`、`avg_potions_used = 0.45`、`avg_economic_value_v = -15.19`、`p50_solve_ms = 678.9 ms`、`p95_solve_ms = 3,835.5 ms`、`peak_rss_mb = 1,218.1 MB`、`gc_gen2 = 33`。
- `--soak-deploy --fights 120 --seed 9001 --pool all --profile Medium --dop 8`（83.92 s）：**`wins = 116 / 120`（96.7%）**、`defeats = 4`、**`hard_error_count = 0`**、**`choice_mismatch_count = 0`**、**`unplanned_replan_count = 0 / 583 turns`（0.00%，`reused = 463`、`planned_boundary = 0`）**，`custom_beam135_stage2_ok = True`，`avg_hp_loss = 13.14`、`avg_potions_used = 0.53`、`avg_economic_value_v = -15.65`、`p50_solve_ms = 370.1 ms`、`p95_solve_ms = 2,085.0 ms`、`peak_rss_mb = 543.7 MB`、`gc_gen2 = 8`。

### 未验证范围

- 本批次所有核心合同、差分与部署压测均在离线 `.NET` 宿主（`OfflineSearchHarness`）与本地 Mod 同步中完成；真实可见 Steam 会话内的交互界面排版、动画手感、实机帧率以及从最终发布 ZIP 的干净安装冒烟验收尚未执行。
