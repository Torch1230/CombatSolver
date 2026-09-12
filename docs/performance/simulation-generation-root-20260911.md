# 完整角色生成池冻结（2026-09-11）

CallOfTheVoid 原来在后台每次生成时读取角色解锁池。现在主线程搜索根捕获完整原生规范候选，Fork 共享只读数据；调用者从该池执行原有逐层完整洗牌和独立卡牌创建。攻击池由相同的有序候选投影。此批不扩大紧凑准入，当前仍为 53 种卡牌类型。

## 所有权与兼容

- `RootCombatCardGenerationPoolSnapshot` 保存完整角色池及攻击子集。角色、牌池、卡牌必须是原生规范且不可变的模型；读取时继续核验角色／牌池／AllCards 身份与人数约束。候选顺序与规范卡身份不变。
- `ICombatPredictionCardGenerationPoolSnapshot` 只提供根数据读取。引擎扩展只尝试读取缓存，CallOfTheVoid 的 Prediction 调用点保留原来的自定义池备用链。未知池不会因本次缓存进入紧凑执行。
- 原版资格条件已排除 Basic／Ancient／Event，因此完整原生池可以服务 CallOfTheVoid；攻击过滤对这些规范纯元数据保持原序。不能对自定义牌池重复执行可能有状态的资格查询，也不能假设 ID 相同等于规范模型。
- RNG、可变生成牌、虚无和后续入堆仍由当前分支拥有。该缓存不缓存随机结果，不共享可变牌，不改变逐次洗乱整个候选池的次数。

## 验证

直接结果见[结构化证据](simulation-generation-root-20260911.json)。Release v3 零警告／错误，Linux 结构门禁通过 94 个 Search 文件；PowerShell 等价规则已同步，未执行 Windows。

`COMPACT-GENERATION-CLOSURE-AUDIT` 保留原始 38 牌、19 件遗物注入、原生初始遗物和两瓶药。两个完整池仍分别为 78／50 个候选，24 次原生／旧链／根缓存抽样比较全部五个 RNG 字段和完整卡牌指纹；检查 Fork 共享、错误人数／可变／自定义／其他牌池拒绝、分支突变隔离，并执行原攻击池和无色池缓存合同。真实完整快照不变，紧凑根仍显式拒绝未迁移药水。

`CALL-OF-THE-VOID-GENERATION-ROOT` 在相同完整输入注入 4 层 CallOfTheVoid，连续三次执行原生 `BeforeHandDraw`。12 张生成牌保留虚无，覆盖手牌及满手溢出；每次完整快照与续用、Fork、实机完成生成后的旧根首批重放都与模型引擎一致。这个测试直接穿过新的生产缓存调用点以及原生生成／入堆回调；没有将完整随机池迁入紧凑后端。

命令沿用[完整生成池审计参数](simulation-generation-audit-20260911.md)，场景分别为上述两个 ID，产物 `.local/character-generation-root-20260911/artifact`，最终证据为 `audit-v3`、`native-v3`。v2 两项先通过，随后审查发现自定义池备用链会重复过滤，修正后使用 v3 验证；没有把 v2 当作修正后证据。

此批未改变 registry 覆盖分类，未重复 CoverageCatalog；未运行整场搜索、完整部署或 NoGC 性能基准。生成池名单及剩余 106 个直接类型缺口见[前一批审计](simulation-generation-audit-20260911.md)，后续还需随机生成值执行、可达生成链、完整遗物／药水和 AEONGLASS AI。
