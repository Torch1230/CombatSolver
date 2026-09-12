# 书页风暴与可恢复嵌套抽牌

书页风暴 `Pagestorm` 进入紧凑闭包，精确卡牌类型增至 53 种；支持根已有能力、新施加／叠加、普通／升级牌，以及虚无牌抽到后继续抽牌。官方中文名已从本轮原生动作记录核对。此批以[上游 0.36.0 合并](simulation-upstream-merge-20260911.md)为基线。

## 实现与所有权

`ResumableDiscardProgram.Draw` 共用一个抽牌入口检查满手、结束、空牌堆和洗牌。涉及书页风暴的根额外持有可撤销抽牌栈：每层保存请求数、返回位置和未完成父卡；嵌套能力抽牌完成后，才执行父卡 Slither 随机费用和 DrawResolved。抽牌栈可增长，不占用原卡牌帧的八层容量。抽牌请求量很大时，空牌堆／满手仍立即结束，不按请求量空转。

回合起手使用原始 hand-draw 来源，内部能力抽牌使用非 hand-draw 来源；父返回值保持自己的首张牌，不能被子抽牌最后一张覆盖。洗牌／Stratagem 选择期间栈、RNG、父帧、方法来源及抽牌完成顺序一起冻结；恢复、撤销和八个独立工作区消费相同权威值。

Prediction 只编译精确卡牌和主人能力，复用普通四槽 Power 布局。兼容物化保存未完成抽牌历史及能力来源作用域；直接读取器计入 Draw／DrawResolved，两个方法来源标记不产生额外历史。没有相关能力或可施加卡的根继续走原简单循环，不分配抽牌栈。未知抽牌监听器仍在根准入时拒绝。

## 直接验证

| 检查 | 结果与范围 |
|---|---|
| Release v9 | Passed，零警告／错误 |
| 纯值 v5 | Passed；九层抽牌、满手检索、洗牌挂起、逆序费用／完成、父返回、终局、冻结／撤销／八工作区；此后内核未变 |
| COMPACT-PAGESTORM-NATIVE | `939b9de3d7e34db3afc3d5e4d030a426` Passed，35.96 秒；三根、24 原生动作／63 分支／32 挂起、3 次原生嵌套选择观察 |
| COMPACT-PAGESTORM-SEARCH | `c3c34264a1aa4531a298e221c0f34487` Passed，8.39 秒；250 节点，旧／新 DOP1 与紧凑 DOP2 全结果相同、并发 2、取消／失败排空与根复用 |
| COMPACT-DRAW-EXHAUST-NATIVE | `74c469c76e3e489abd8e334724cab46e` Passed，8.34 秒；无新能力根的共享抽牌／消耗／完整回合回归 |

原生测试保留全部快照、能力元数据、九 RNG、历史、原键／完整估值／续用、牌序／实例、随机费用、逆序读取、撤销和实机推进后冻结根。三根分别无初始能力、1 层、2 层；牌组包含虚无的玷污／致死性、迅速、Sly、雕琢打击新增虚无和两个完整回合。模式 0 验证回合起手中嵌套能力抽牌；模式 1／2 会按实际路线先消耗相关虚无牌，完整差分验证这个不同结果。

正式动作政策会丢弃此语义见证的准备动作。测试通过正式 PrepareCardActions／ReplayAction 与原选择解析器，在动作保留裁剪前取出完整合法路线；独立 SEARCH 测试继续使用全部正常搜索政策及预算。它不是搜索策略更改。

Linux 结构门禁通过，PowerShell 对应规则已同步但未执行。本批未改变旧 Hook registry 分类，未重复 CoverageCatalog；结构化证据见[JSON](simulation-pagestorm-20260911.json)。

## 失败与限制

先前 v2／v3 在路线准备阶段丢失 EscapePlan；v4 构建失败后误启动了旧 DLL，明确排除。v5 误用本身不带虚无的刺破帷幕，v6 的反射 helper 未识别 Nullable 参数，均为夹具问题。v7 暴露并修正了读视图遗漏的方法来源标记。v8 两个根已通过完整原生差分，但最终覆盖断言错误要求每个根下一回合都再次抽到虚无；当前明确由模式 0 证明该路径，其他根不改其实际结果。

本轮结果证明语义及固定节点等价，以上耗时含建局／验证，不能作为性能提速。原始亡灵整场仍需 CallOfTheVoid 的完整随机生成池、遗物、药水及 AEONGLASS；不能把 53 张静态卡牌计作整场闭包完成。

复跑使用既有双端无人协议，Linux 示例：

```bash
./tools/run-unattended-test.sh --scenario-id COMPACT-PAGESTORM-NATIVE \
  --character-id SILENT --encounter-id MECHA_KNIGHT_ELITE --enemy-current-hp 300 \
  --timeout-seconds 120 --headless-instance compact-full-route-20260911 \
  --combat-solver-build-dir <本批独立产物> --evidence-directory <证据目录>
```

SEARCH 与共享回归替换 ScenarioId。当前本机 v9 产物及请求证据在 `.local/compact-pagestorm-20260911/`，不会提交二进制或原生日志。
