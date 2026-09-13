# 通用战斗场景生成

无需玩家测试包即可建立独立战斗。随机项可使用固定种子，或逐项指定；两者可以混合。用于生成性能样本、复现组合和后续回归，不修改正式求解算法。

默认配置：随机原版角色、A10、保留初始牌组和初始遗物、恰好一张进阶之灾，追加8张角色奖励池牌、2张无色奖励池牌、5件不重复随机遗物、2瓶药水，随机普通精英或Boss遭遇。遭遇与所属幕一起选择，按原生Elite/Boss房间进入，保留原生敌方生命、开局抽牌、Power和RNG。

## 运行

先用本分支编译的DLL和manifest。Linux原生入口：

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
./tools/run-unattended-test.sh --generated-scenario-path tools/GeneratedCombatScenarios/random.json --evidence-directory .local/generated-example --scenario-id GENERATED-EXAMPLE --headless-instance generated-example --timeout-seconds 120 --exit-on-complete
```

Windows PowerShell 7入口：

```powershell
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
./tools/run-unattended-test.ps1 -GeneratedScenarioPath tools/GeneratedCombatScenarios/random.json -EvidenceDirectory .local/generated-example -ScenarioId GENERATED-EXAMPLE -HeadlessInstance generated-example -TimeoutSeconds 120 -ExitOnComplete
```

游戏、RitsuLib和DLL路径继续使用现有 `Sts2GameRoot` / `CombatSolverBuildDir` 等参数。生成模式必须提供证据目录。普通启动器的单进程/复用/清理规则保持原样；请使用本任务专属实例。

可选Python 3批量入口，两端均调用各自原生启动器，默认串行复用同一专属进程、结束时停止：

```bash
python3 tools/GeneratedCombatScenarios/run.py --count 10 --seed BENCH-A10 --output .local/generated-suite
```

Windows使用 `python`。每个样本种子为 `BENCH-A10-0000`、`BENCH-A10-0001`……；单个样本不添加序号。`--config` 指定JSON，`--mode Setup|Search|Deploy` 覆盖运行方式，`--build` 选择冻结DLL目录。`--continue-on-failure` 在保存失败后以新进程继续，默认首个失败即停止，已计划但未运行的样本仍在suite.json中。`--write-inputs-only` 只生成输入文件，不启动游戏、不解析实际模型。

本地路径/搜索设置可通过尾部 `--` 传给原生启动器，例如Linux：

```bash
python3 tools/GeneratedCombatScenarios/run.py --count 3 --output .local/generated-short -- --search-max-degree-of-parallelism-for-test 2 --search-budget-override-milliseconds 1000
```

Windows尾部使用对应的 `-SearchMaxDegreeOfParallelismForTest 2 -SearchBudgetOverrideMilliseconds 1000`。实例、场景ID、证据目录、超时和进程生命周期由批量入口独占，不接受尾部重复覆盖。

## 配置

[随机示例](../tools/GeneratedCombatScenarios/random.json)与[部分指定示例](../tools/GeneratedCombatScenarios/specified.json)均可直接复制修改。

```json
{
  "schemaVersion": 1,
  "seed": "MY-CASE-001",
  "characterId": "SILENT",
  "ascension": 10,
  "actIndex": 2,
  "encounterId": "QUEEN_BOSS",
  "includeStartingDeck": true,
  "includeStartingRelics": true,
  "includeAscendersBane": true,
  "characterCards": { "count": 6, "ids": ["ACROBATICS", "DAGGER_THROW"], "upgradeLevels": 1 },
  "colorlessCards": { "count": 2 },
  "relics": { "count": 5, "ids": ["VAJRA", null, "ANCHOR"] },
  "potions": { "ids": ["FIRE_POTION", "BLOCK_POTION"] },
  "mode": "Search"
}
```

| 配置 | 含义 |
| --- | --- |
| `seed` | 建局种子，也是各类别生成随机流的输入；不要在A/B之间改变 |
| `characterId` | 省略或null则随机；指定时使用角色模型ID |
| `ascension` | 0–10，默认10；游戏自身施加A等级效果 |
| `actIndex` | 0/1/2，省略则从允许遭遇及所属幕一起随机选择 |
| `encounterId` | 指定遭遇；须属于所选幕的原版普通战斗目录。省略则按encounterKind选择 |
| `encounterKind` | `EliteOrBoss`（默认）/`Elite`/`Boss`/`Monster`；显式encounterId时由实际模型决定房间类型 |
| `includeStartingDeck` / `includeStartingRelics` | 是否保留角色原生初始装备，默认true |
| `includeAscendersBane` | 默认true，确保恰好一张；A10原生已有时不再追加。false移除原生该牌；A0且true则明确构造带该牌的测试局 |
| `characterCards` / `colorlessCards` | 追加卡牌选择，支持count、ids、upgradeLevels |
| `relics` / `potions` | 额外遗物/药水选择，支持count、ids |
| `applyRelicObtainEffects` | 默认false，仅添加持有实例；true走原生RelicCmd.Obtain及获取效果 |
| `setupChoices` | 省略则确定性选择；指定二维ID数组则按顺序消费建局期间所有选牌，额外或未消费项显式失败 |
| `playerCurrentHp` | 可选，获取遗物完成后、进入战斗前设为指定生命，不得超过实际最大生命 |
| `potionSlotCount` | 可选，显式覆盖槽数；省略则保留A等级及原生获取效果后的槽数 |
| `mode` | `Setup`只建局检查；`Search`只取首个求解结果；`Deploy`完整自动战斗 |

选择对象的规则：`count`是总数量，`ids`中的非null条目固定占据对应位置，null及剩余位置随机补齐；省略count时等于ids长度。`{"count":0}`或`{"ids":[]}`表示不追加。count不得小于ids长度，每类最多100项。卡牌的upgradeLevels支持0/1，使用现有原生注入器尝试升级至该级；实际升级状态写入loadout证据。

卡牌、药水允许重复；遗物无放回且排除已保留的初始遗物，显式重复会失败。随机卡牌来自当前角色/无色池的普通、罕见、稀有奖励牌，显式卡牌ID可以是该池的其他牌。随机遗物来自当前角色及共享池的Common/Uncommon/Rare/Shop，显式遗物支持原版其他稀有度与其他角色遗物。药水来自当前角色与共享药水池。随机池忽略账号解锁进度，仅纳入原版程序集模型；角色与无色牌池遵循原版单人限制，排除MultiplayerOnly牌，显式指定这类牌同样拒绝；多人专用遗物MassiveScroll也拒绝指定，原生获取效果结束后再次检查实际跑局仅有一名玩家且装备没有多人专用内容；不因求解器是否支持而筛掉内容，未支持机制应成为可见失败。

A10默认两药水槽。请求超过实际槽数时会失败，不沿用旧注入器“覆盖第0槽”的行为。需要更多药水时，显式增加槽数，或开启获取效果并指定能扩槽的遗物。

“持有遗物”与“从奖励获得遗物”不同。默认不重演获取时的加生命、升级、奖励和选牌；遗物加入后发生的正常战斗钩子仍执行，随后添加牌组时的原生命令也仍可触发遗物效果。`applyRelicObtainEffects:true`用于测试获取效果，但不保证所有遗物的非卡牌奖励/事件页面都支持无人处理；失败和超时保留原始证据，不自动换一件遗物。

建局卡牌选择默认取原生候选顺序中的最少合法张数，可跳过则跳过；卡牌奖励默认取首张。实际选择保存到解析后配置，避免赌博筹码/工具箱的开局依赖限时搜索。选择器仅在建局作用域存活，进入正式搜索/部署前释放。需要研究求解器如何优化原生开局选牌时，应使用已有专用选牌夹具，不把本工具的固定准备选择当成该能力的验收。

## 可复现证据

每个样本保存：

- `generated-scenario.resolved.json`：全部角色、幕、遭遇、卡牌、遗物、药水ID及已消费的建局选择，可直接作为下次生成输入。
- `generated-scenario.catalog.json`：当前角色使用的实际模型目录，帮助查找合法ID。
- `generated-scenario.loadout.json`：进入战斗前的实际牌组顺序/升级、遗物顺序、药水槽、生命和A等级。
- `generated-scenario.opening.json`：实际开局选牌和完整ContinuationStamp，包括有序牌堆、逐牌状态及九条战斗RNG等。
- `generated-scenario.request.json`：解析后的实际无人测试请求；标准result.json提供runId、失败阶段、异常、求解指标与生成模式的实际结果；实际战损/敌方剩余生命不取自首个搜索预测。
- 批量入口另写 `suite.json`、每例input.json/command.json/launcher.log及results.jsonl。失败不删样本，不把未运行样本记成通过。

随机类别使用独立的固定算法，改变药水数量不会重抽牌组或遭遇；显式遗物先占位，前面的随机槽不能抢占后面的指定项。相同种子要求相同游戏/Mod语义与目录；游戏更新可能改变池和开局规则。目录指纹用于识别来源变化，不是战斗等价性判定。严谨回归应固定DLL/Mod环境，使用解析后的配置并比较实际开局，而不只看种子字符串。

重跑单例：

```bash
./tools/run-unattended-test.sh --generated-scenario-path .local/generated-example/generated-scenario.resolved.json --evidence-directory .local/generated-replay --scenario-id GENERATED-REPLAY --headless-instance generated-replay --timeout-seconds 120 --exit-on-complete
```

## 搜索与验收口径

默认Search使用Medium、Smart、固定预算模式和1000毫秒测试搜索软时间预算；可以用现有无人测试参数覆盖预算、预设、DOP、NoGC等。Deploy默认Instant/0秒，并要求计划外重算为0（可用标准断言参数显式覆盖）。生成配置负责建局，不与问题包/快照恢复或另一套遗物、牌组、药水注入混用。原场景夹具行为不受生成模式影响。

Setup通过只证明生成和建局检查；Search通过只证明取得首个结果，不代表找到胜利；Deploy是实际执行，随机牌组可能弱、不可胜或触发未支持语义。完整请求仍遵守120秒上限，不为了把失败改成通过而延长超时。

随机样本适合找新问题，性能A/B应固定同一套解析后样本、搜索预算、DOP和Mod环境，并单列预热/GC/失败。Linux headless数据不代表Windows可见FPS；本批没有运行可见Steam。
