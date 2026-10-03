# 极高配置：状态与缓存实验（2026-09-08）

[返回性能目录](../../performance/README.md) · [结构化证据](veryhigh-state-experiments-20260908.json)

本轮继续以 `b168b68` 为基线寻找大幅加速，没有得到值得保留的生产改动。四项实现实验全部撤回，另一个回放重复率探针未进入生产。**用户要求的大幅优化尚未达成。** 上一轮12.58%的单组样本属于上一轮，不算本轮收益。

## 固定工作量筛查

同一死灵法师投影输入：38张牌、20件遗物、两瓶药水，要求至少用一瓶。每个版本独立headless进程，先跑一次冷启动短搜，再跑一次正式短搜；极高、DOP8、NoGC配置16GB，禁用详细诊断、阶段计时和增量验证。每请求120秒上限，首结果停止；编译与基准不并发。GB为十进制，峰值RSS每100ms采样整个进程，含预热残留。

| 因素 | 正式耗时（秒） | 相对基线 | 累计worker分配（GB） | 进程峰值RSS（GB） |
|---|---:|---:|---:|---:|
| 基线 b168b68 | 5.9322 | +0.00% | 7.4121 | 9.3088 |
| 按类型序列复用监听布局 | 6.1887 | +4.32% | 7.3866 | 9.2530 |
| 复用空 Power 显示变量集合 | 5.9039 | -0.48% | 7.1903 | 9.0799 |
| 卡牌评分缓存 | 5.8503 | -1.38% | 7.4130 | 9.3031 |
| 复用同 owner 的 Power 插入位置 | 5.8670 | -1.10% | 7.4142 | 9.3138 |

五个正式请求均Passed，同为10,000展开、144,368转移、101,808选择分支，Short/NodeLimit、投影战损3、不是仅死亡路线。只核对这些筛查指标，没有据此声称完整状态、路线或战斗语义等价。每因素只有一个正式样本，约1%的差异不能证明稳定收益，因此没有扩大语义验收面或保留复杂度。

## 为什么撤回

- 监听布局共享：根内按完整有序类型序列核对后复用，只保存类型和位图，设1024项/65536条目上限。实际分配仅少约25.5MB，时间变慢。采样中的未限定`Entry[]`包含其他类型，不能把它全部当作监听布局。
- 空Power变量集合：仅在原版默认深克隆、空变量且相关基础方法无补丁时尝试共享空容器，仍克隆Power及初始化内部数据。分配约少3%，时间没有明显变化；额外克隆语义审计成本不值得。
- 卡牌评分缓存：沿已有预览存储版本失效，第三方/附着模型隔离旁路。没有改估值公式，正式耗时仅少1.38%，不足以证明大幅优化。
- Power插入位置：连续同owner新增时复用已经确认的插入位置，初次遇到anchor时保守重扫。正式耗时仅少1.10%，撤回；没有将未经完整顺序合同验证的实验合入。

## 回放复用与采样

临时诊断按“父状态指纹、确定性动作指纹、父动作数”记录一个完整coordinator请求。635,701次回放尝试中618,785次父边界为None，603,222个不同键，15,563次重复，占可缓存尝试约2.52%。仅保留最近1024/8192/32768条时，命中分别为6,224/8,519/8,526。该结果限定于这个键与输入，不能证明其他设计的理论上限；但没有支持扩大这类缓存来达到翻倍的证据。探针自己持有键并影响耗时，不能当性能样本。

另做25秒EventPipe线程/分配采样（[后续复核](backend-cpu-hotspots-20260908.md)确认线程样本不应称为实际CPU样本），覆盖基线完整搜索的一部分。采样显示成本分散在Fork、动作执行和快照，并出现监听数组、牌包装、字典及动态变量分配。部分等待栈和内联归因不干净，不将inclusive百分比称为墙钟占比，也不把采样字节当整场精确归因。全量状态COW或动作撤销可能减少复制，但必须解决Power/卡牌别名、模型重映射、回调写屏障及多分支生命周期；本轮没有实现或验证这些方案，不能承诺它们会翻倍。

## 复跑和交付范围

下面是基线固定工作量入口。每版本先以明确的Release artifact启动新headless实例预热一次，再在同一实例执行正式请求；用`--combat-solver-build-dir`指定构建目录，`--headless-instance`指定独立实例。不要将首次冷运行与本表预热后数据混比。实验源码已撤回，表中实验不是当前生产实现，不能用HEAD重跑来代表它们。

```bash
./tools/run-unattended-test.sh --scenario-id VH-STATE-FIXED-WORK --character-id NECROBINDER --seed SEARCH_PERF_NECROBINDER_POTION --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2 --enemy-current-hp 526 --initial-player-hp 41 --initial-player-max-hp 76 --clear-run-deck --run-cards-path coverage/unattended/search-performance-necrobinder-projected-run-cards.json --relics-path coverage/unattended/search-performance-necrobinder-projected-relics.json --potions-path coverage/unattended/search-performance-necrobinder-projected-potions.json --cards-json '[]' --potion-policy-for-test RequireAtLeastOne --performance-preset-for-test VeryHigh --search-max-degree-of-parallelism-for-test 8 --enable-no-gc-region-for-test 1 --no-gc-region-budget-gigabytes-for-test 16 --enable-detailed-diagnostic-logs-for-test 0 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --keep-game-open --force-short-search-only
```

所有有效请求runId、完整solver指标、采样内存和诊断条件见JSON。Power插入实验初次复制产物使用了错误输出路径，已停止该次生产者/实例并排除数据；后续v2从成功构建报告的`.godot/mono/temp/bin/Release`复制，独立进程完成预热与正式请求。

最终只提交研究记录。所有实验生产源码恢复到基线，独立测试实例已停止，未修改原生安装或用户设置。没有新增生产状态、职责或测试能力，未追加整场战斗、DOP合同、可见Steam、Windows游戏、版本提升、发包或推送。实验Release构建成功只证明可编译，不代表语义验收；最终文档执行JSON、链接、固定工作量及差异检查。
