# 搜索结束后释放进度引用（2026-09-22）

## 结果与口径

本轮选择内存方向达成用户后来允许的 20% 目标。在事先选定的 **17 个根、全部纳入**的
离线对照中，停止/完成并清理进度后，全进程存活托管内存样本均值从 **25.207 MB 降至
19.918 MB，下降 20.98%**，平均每次少保留 5.289 MB。12 个停止样本贡献主要收益；
5 个正常完成样本几乎没有可释放空间。

这是强制完整 GC 后的 `GC.GetTotalMemory`，适用于已结束搜索的引用保留问题。
活动搜索峰值、进程 RSS、可见帧时间、整段游戏会话的时间加权内存没有 20% 改善结论。
探针刻意触发旧预览被请求、新预览随后发布的有效顺序，尚未测量真实玩家操作中该顺序的频率。

上游 `6922828d` 已拉取，任务分支 `perf/bounded-hotpath-20260922`。对照源码为
`91e12e88`；保存的基线 DLL 来自 `510dd703`，两者之间只有调研文档/补丁归档差异。
前一轮怪物指纹优化在两侧都存在，本报告不把它的收益重复计入。

## 调研与实现

先进行了[通用热点筛查](general-optimization-screen-20260922.md)，随后补齐全部17根基线调查，
并采集真实存活堆。分配热点分散，StandPat 跨成员复用的收益上限不足；没有启用新的缓存、
改变搜索预算或按角色/牌名做特殊处理。调查中两根约60秒的请求受截止时间影响，不能列为
固定工作量性能证据。

实际问题在 Runtime 生命周期：停止结果被保存在 `SearchInteractionState` 时，旧的
`RenderedProgress`、显示缓存或尚未选中的 `SolverRouteAdoptionSeed` 仍可能持有惰性
materializer。该闭包能引用已完成的求解器及其搜索上下文。选中的 A 已经 materialize，
并不保证随后发布的 B 也释放了闭包。

生产改动仅位于两份 Runtime 文件：

- `CompleteTakeover` 在既有 worker 排空边界清空工作进度、渲染进度、路线 seed 和显示缓存。
- `PreserveStoppedResult` 先保留结果及状态戳，再调用同一清理入口；普通准备阶段完成也复用它。
- `CompleteTakeover` 仍返回原请求，停止标志在清理前被调用方保存；重算通过原 `ResetForSearch` 重开。

清理是固定数量的引用赋空，没有生产强制 GC、对象池、新预算或搜索评分变化。
选中路线、结果和恢复状态戳仍由原有字段拥有，没有以决策质量或搜索工作量换内存。

## 实验设计与数据

同一 Linux / .NET SDK 9.0.120，独立进程串行运行，按根交替 A/B 与 B/A。
High、beam 90、每成员节点配置6000、DOP1、Coordinator、原有 portfolio、60000 ms
请求预算、120秒进程上限、正常 GC。真实预览更新受墙钟节流，停止阈值4800只是测试驱动，
不进入生产代码；实际停止点保存在 JSON，不能把两侧不同停止点的路线作质量优劣比较。

17根覆盖五角色的普通/精英/首领，以及弃牌、药水哨兵。每次 worker 返回后先测一次存活堆，
再调用生产清理并测一次；两次测量都强制 GC。旧版作为负对照：旧 seed 在17/17样本中仍存活；
新版在17/17中可被回收。12个停止样本两侧都保持原结果对象，并以匹配状态戳恢复。

下表 MB 为十进制；A/B降幅取双方清理后的值，负数表示候选略高。

| 场景 | 路径 | A 退休后 MB | B 退休后 MB | A/B 降幅 |
|---|---|---:|---:|---:|
| dev-00-ironclad-elite | 停止 | 27.251 | 20.124 | +26.15% |
| dev-01-silent-elite | 停止 | 26.949 | 19.665 | +27.03% |
| dev-02-defect-elite | 停止 | 24.622 | 20.100 | +18.37% |
| dev-03-regent-elite | 停止 | 32.270 | 24.026 | +25.55% |
| dev-04-necrobinder-elite | 停止 | 32.423 | 20.227 | +37.61% |
| dev-05-ironclad-boss | 正常完成 | 19.390 | 19.352 | +0.20% |
| dev-06-silent-boss | 停止 | 26.296 | 19.043 | +27.58% |
| dev-07-defect-boss | 停止 | 26.580 | 20.510 | +22.84% |
| dev-08-regent-boss | 停止 | 30.531 | 21.525 | +29.50% |
| dev-09-necrobinder-boss | 停止 | 27.620 | 20.001 | +27.59% |
| holdout-00-ironclad | 正常完成 | 18.472 | 18.521 | -0.27% |
| holdout-01-silent | 正常完成 | 19.449 | 19.467 | -0.09% |
| holdout-02-defect | 停止 | 24.429 | 19.699 | +19.36% |
| holdout-03-regent | 停止 | 26.814 | 19.180 | +28.47% |
| holdout-04-necrobinder | 停止 | 29.041 | 20.813 | +28.33% |
| sentinel-discard | 正常完成 | 18.281 | 18.280 | +0.00% |
| sentinel-potions | 正常完成 | 18.106 | 18.071 | +0.20% |

聚合公式为 `1 - sum(B_after_bytes) / sum(A_after_bytes)`，即相同样本数下的平均字节降幅。
A合计428,525,008 B，B合计338,604,536 B，下降20.9837%。逐场百分比的算术均值是18.7304%，
两个口径不同，不能互相替代。候选同一进程清理前后汇总下降21.0626%，与跨版本对照接近。
普通完成的两根候选分别多约49 KB、18 KB；幅度不足0.3%，其进程内清理仍释放了引用，
不据独立进程的这一波动声称正常完成有显著内存改善。

额外DOP4验证铁甲战士与储君精英，共4次，全部旧seed存活/新seed回收，结果原样恢复；
候选进程内下降30.45% / 29.35%。这两根没有混入17根的主汇总。

初版普通完成探针的测量辅助栈保留了临时强引用，使弱引用目标在GC时仍存活。将准备过程移入
禁止内联的辅助方法后，五根普通完成两侧全部重跑，生产DLL未变；旧10份观察保留在
JSON的 `supersededCompletionSamples`，不混入最终均值。初版 `completed-setup-search`
标签也改为 `completed-search`，因为离线 Coordinator 调用不证明原生准备页面。

## 速度、决策与生命周期验证

- 五个正常完成根，初版及修正后的探针共10对/20次完整搜索：`route.json`、`quality.json`、
  根状态、搜索结果/工作量、全部剪枝计数及政策逐项完全相等。停止样本只断言清理前后结果
  身份和恢复能力，不把墙钟导致的停止点差异算作质量改善。
- 上述完整搜索的 worker 总耗时合计91,763.807 → 91,566.231 ms（−0.215%），
  分配19,899,258,584 → 19,891,310,576 B（−0.040%）。每根每臂只有2次且预览受墙钟节流，
  这些数值只支持本批未观察到整体成本退化，不宣称显著提速。worker指标不包含探针强制GC。
- [40项生命周期合同](../../tools/ProgressRetirementChecks/README.md)通过：匹配/过期状态戳、
  一次物化、异常清理、无请求完成、重算、闭包回收且结果保留。旧源码负对照精确失败在
  `worker progress retired failed`，编译错误不会被当作预期失败。该合同不替代原生验证。
- 主项目与离线宿主 Release 构建均0警告/错误；Bash结构门禁通过，`search_files=208`。

原生无头验证使用候选DLL及表中注明的基线DLL，单请求上限120秒；全部实例由启动器确认删除。

| 测试 | 结果 | runId |
|---|---|---|
| controller-stop | Failed | c7b8e71e11a84f31b048293f2cdb75fc |
| controller-baseline | Failed | 434769f32ddc4626a53923a78fe9d18a |
| setup-stop-adopt | Passed | 997fde49b7684f27b0619668fed6c274 |
| setup-stop-restart | Passed | c1c8e3d227d747dda9b6b9cae5ae943f |
| setup-refresh-takeover | Passed | c06975bd938a4e038b5376179ec34ea4 |

三个通过项分别覆盖停止后采用候选并释放忙碌状态、停止/重算/再次停止后在部分手选状态下恢复、
重算期间排队接管并使用新计划驱动原生选择。它们验证控件/所有权/选择流程，不代表完整自动战斗。

通用控制器测试在新旧DLL上都失败于相同的覆盖层尺寸持久化断言
`configured=True, persistence=False`，发生在该fixture的停止断言之前。
本轮没有修改这项无关UI逻辑，也不把主控制器的原生停止链记为已通过。纯合同与真实搜索探针
覆盖共享交互状态，原生准备阶段覆盖停止/恢复；可见Steam、Windows、主控制器该原生链仍未验证。

## 复现与证据

[结构化证据](progress-retirement-20260922.json)保存17根输入配置、所有主样本、DOP4、
被替代的探针样本、pilot、原生命令/检查/失败，以及汇总公式。完整日志在工作区
`.local/progress-retirement/`；调查日志在 `.local/general-perf/`。

先按[离线宿主文档](../OFFLINE_SEARCH_HARNESS.md)构建主项目与宿主，保存两份DLL。
为每个 `coverage/novelty-search/<name>.json` 创建包装请求，包含 `scenarioId`、`characterId`
及其绝对 `generatedScenarioPath`，使用证据中的统一命令；切换
`OFFLINE_HARNESS_COMBATSOLVER_DLL` 并设置 `OFFLINE_HARNESS_RETIRE_PROGRESS_AFTER_NODES=4800`。
单看 `harness-result.json` 的 wallSeconds 会包含探针的强制GC，不应用作生产提速数字。
