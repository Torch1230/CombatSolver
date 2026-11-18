# 原生生命以及资源入口扫描

读取指定 DLL 的 IL 元数据；游戏输入列出生命值命令、内部写入口、直接字段写入和字段取址，同时保存静态方法/委托引用、泛型模型参数、异步方法映射、回复相关回调、全部原版模型虚方法、Hook 调用入口和模型定义。也可为依赖 DLL 保存引用图；没有游戏入口不代表依赖安全。不会加载或执行模型，不会修改 DLL。

schema3 另外列出药水获取/消费/丢弃/槽位容量入口、槽位字段全部访问、Player 与 PotionModel 事件订阅，以及药水相关模型 Hook 定义。字段读取同样保留，因为 List 可在 `ldfld` 之后改变，只读接口也可能返回原列表别名。事件、生成和间接获得遗物的调用仍然需要人工复核，不能从零回复认证推导库存不变。当前调查见[库存与后续计划研究](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/potion-inventory-pruning-research-20261006.md)。

schema4 追加牌堆命令、完整牌堆/消耗堆读取、返回精确牌堆类型的方法以及原生战斗监听枚举入口，保留 CardPile 自身字段，以及精确 CardPile 类型或其数组/泛型包装字段的全部 IL 访问；不将名字包含 CardPile 的其他结果类型算作牌堆根。返回值扫描也覆盖 CardModel.Pile 和各牌堆 getter 的间接别名。它帮助定位有序列表别名、根字段和间接读写，不把只读接口、已消耗或没有直接取牌调用视为无交互证明。方法/委托图、原版模型虚方法和异步映射继续复用原格式；反射、扩展、递归生成和完整未来等价仍然必须单独证明。

schema5 追加原生 Hook 实际引用的 AbstractModel 入口以及模型虚方法槽。按输入模块内的继承链、参数、返回类型和显式 override 定位槽根，保留 IsNewSlot/IsFinal、解析链和失败原因；外部程序集、多个显式槽、泛型/协变或歧义不猜测。Hook 入口包含非虚方法和战斗外回调，不能据此认定全部都是战斗虚回调。另外列出 AbstractModel、CardModel、附着效果基类、CardPile、CardEnergyCost 和 PlayerCombatState 的事件以及静态订阅/退订入口。尤其默认 Hook 之后也可能 InvokeExecutionFinished；没有声明回调不等于没有事件副作用。调用槽只定位实际继承关系，不证明方法体、当前监听者、补丁或生成闭包安全。[消耗堆闭包研究](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-reader-closure-20261007.md)记录适用边界。

schema6 保留同文本签名的全部定义以及 token、泛型参数数量、实例/静态和调用约定，不再因 FullName 字典键重复丢失整份清单。当前 Ritsu Runtime 存在同文本的普通/泛型方法；文本签名不能作为唯一身份。只按文本的虚槽/显式 override 解析遇到这种碰撞时保守拒绝，重复类型的声明或基类也明确报告。静态方法引用和异步映射增加 CallerDefinitionToken 与 OperandMetadataToken；与输入 MVID 一起解释，operand 可能是 MemberRef/MethodSpec，不能冒充已解析目标 MethodDef。字段访问等记录目前没有这两个 token；非游戏程序集的附着容器事件、字段别名和完整写入闭包仍然需要另外审计。14 项托管元数据边界以及实际依赖调查见[扩展写入审计](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-extension-writer-audit-20261007.md)。

schema7 另外列出 `cardStateEventAccessorCandidates`：在同一七个明确类型上保留所有 `add_`/`remove_` 方法引用，包含依赖 DLL 对原生事件的引用；原有事件定义/引用列表仍然只使用输入自身定义。候选记录操作码、调用 token、声明类型范围以及程序集身份、参数/返回类型身份、实例标志和调用约定。名称可能对应普通方法，全部 `DefinitionResolution=not-resolved`，不作为事件解析或安全认证；不同程序集/版本的同名类型不能合并。工具不增加外部程序集加载/自动解析。21 项元数据检查以及 Ritsu 新增 10 处候选见[跨程序集事件审计](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/exhaust-event-reference-audit-20261007.md)。

schema8 另外列出 `nativeHookModelBodies`：对输入中被原生 Hook 直接引用的模型方法定义，保留 MVID/MethodDef token、原始 IL 摘要以及完整指令、异常区域/局部变量数量和方法体形状。同文本定义仍然各自保留；读取 PE 与 Cecil 模块身份不一致时失败。只识别空 void、类型/作用域一致的返回参数、整数常量、引用 null 以及无参数静态调用形状。分支、字段、输出参数、泛型未解析或其他模式保留未分类；调用仍然是未解析依赖，操作数 token 不冒充目标定义。方法体分类不认证整个 Hook、默认返回值的中性含义、活动补丁、事件或未来接收者。[默认回调方法体审计](https://github.com/ltlly/CombatSolver/blob/1018fd7020aaaff1b2d24e9950f4b37446eea441/docs/performance/native-hook-body-proof-20261008.md)记录 180 个入口、26 项新增元数据边界以及剩余证明义务。

构建时传入已安装 ILSpy 的 `Mono.Cecil.dll`，不把该依赖或游戏二进制提交到仓库：

```bash
dotnet build tools/inspection/HealingSourceAudit/HealingSourceAudit.csproj -c Release \
  -p:CecilAssemblyPath=/path/to/Mono.Cecil.dll
dotnet .local/tool-build/HealingSourceAudit/bin/Release/net9.0/HealingSourceAudit.dll \
  /path/to/sts2.dll .local/healing-audit/native-references.json
```

输出包括输入 SHA-256、MVID 和方法数量。保留大型扫描产物于 `.local/`，提交的审计只保留复核所需元数据、来源哈希及人工语义判断。

调用列表只是调查入口。它不能证明目标是玩家、调用实际可达、回复次数有限或生成集合封闭；设置生命还可能是损血、敌人初始化、读档或同步。静态图也不能解决虚调用、反射与外部 Mod 的回调。必须结合当前版本的完整原生实现、生成过滤及原生/模拟差分，未知来源继续拒绝认证。
