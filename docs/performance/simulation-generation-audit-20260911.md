# 完整随机生成池审计（2026-09-11）

原始亡灵输入的迁移范围远大于牌组里剩余的一种卡牌。只读原生建局审计导出两个完整直接生成池；[有序类型清单、样本和结果](simulation-generation-audit-20260911.json)保留全部候选，没有按当前能力缩池。

| 来源 | 解锁源数量 | 战斗可生成候选 | 当前精确类型准入 | 尚缺类型 | 每次完整洗牌 RNG 次数 |
| --- | ---: | ---: | ---: | ---: | ---: |
| CallOfTheVoid | 80 | 78 | 19 | 59 | 77 |
| 无色药水 | 53 | 50 | 3 | 47 | 49 |

这里的准入数字只表示编译器类型集合成员资格，不证明任意状态、Hook 和后续生成闭包都支持。106 个未迁移类型也不包含进一步生成链。完整输入仍由紧凑根明确拒绝，当前首先报告未迁移药水。本批没有修改生产执行或准入。

## 原生顺序与所有权

- CallOfTheVoid 在 `BeforeHandDraw` 从主人角色解锁池排除 Basic／Ancient。原生工厂再按玩家数量过滤，排除不能战斗生成及 Basic／Ancient／Event 的牌，并保持顺序去重。
- `GetDistinctForCombat(..., 1)` 仍对整个池执行 `TakeRandom`／`UnstableShuffle`，不能替换为一次随机索引。每层独立调用，所以不同层之间允许重复；无色药水一次取三个不同候选供选择。
- CallOfTheVoid 先创建整批牌并全部附虚无，再进入生成入堆命令。后者逐张记生成历史、执行入堆、派发 `AfterCardGeneratedForCombat`。满手转入弃牌；终局生成历史与实际入堆有不同门禁。生成不是抽牌，不应触发书页风暴。
- 当前根缓存只冻结无色池和角色攻击池，完整角色生成池仍需接入根捕获。上述原生时序来自本机游戏 `0.111.0 / 41cef1ea` 定向反编译；本批运行只验证池与 RNG，不验证原生卡牌创建、入堆和回调行为。

## 直接验证

`13339699a6c142a2b456a493fab6e6c2` Passed，23.99 秒，含游戏启动。Release v5 零警告／错误。两池各连续 12 个样本；调用原生筛选／`TakeRandom`，与旧引擎生成选项逐 ID 和完整五字段 RNG 对照。前后完整真实快照一致，主线程根仍拒绝完整未迁移域。本次不启动搜索，命令保留原请求性能字段，但没有进入 NoGC，不作速度结论。

原清单是 38 张牌、19 件遗物注入、两瓶药；框架保留初始遗物，实际为 20 件，战斗准备还生成灵魂等牌，本次实际捕获 41 个战斗实例。必须保留这套自然建局结果。v2 首次运行因审计错误断言总遗物必须为 19 而失败；修正为确认 19 个请求注入全部存在，并记录实际完整列表。v4 把请求数组误写为 Count 属性，编译失败、没有部署；v5 修正为 Length 后通过。这些是夹具问题，没有裁剪生产输入。

```bash
dotnet build CombatSolver.csproj -c Release
./tools/run-unattended-test.sh --scenario-id COMPACT-GENERATION-CLOSURE-AUDIT \
  --character-id NECROBINDER --seed SEARCH_PERF_NECROBINDER_POTION \
  --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2 \
  --enemy-current-hp 526 --initial-player-hp 41 --initial-player-max-hp 76 \
  --clear-run-deck \
  --run-cards-path coverage/unattended/search-performance-necrobinder-projected-run-cards.json \
  --relics-path coverage/unattended/search-performance-necrobinder-projected-relics.json \
  --potions-path coverage/unattended/search-performance-necrobinder-projected-potions.json \
  --cards-json '[]' --potion-policy-for-test RequireAtLeastOne \
  --performance-preset-for-test VeryHigh --search-max-degree-of-parallelism-for-test 8 \
  --enable-no-gc-region-for-test 1 --no-gc-region-budget-gigabytes-for-test 16 \
  --enable-detailed-diagnostic-logs-for-test 0 --timeout-seconds 120 \
  --headless-instance compact-full-route-20260911 \
  --combat-solver-build-dir <successful-release-artifact> --evidence-directory <evidence>
```

重编译后先用 `--headless-instance compact-full-route-20260911 --stop-instance` 停止本任务旧实例，再传入新产物。Windows 对应既有 PascalCase 参数；没有新增协议字段。本批未运行 Windows、整场部署或性能基准。

下一步冻结完整角色生成池，保留解锁、原始顺序和严格规范模型条件；然后迁移随机生成值执行、可达类型、遗物、药水和敌人 AI，继续按完整输入验收。不能通过只允许上述 22 种已覆盖牌完成 CallOfTheVoid 或无色药水。
