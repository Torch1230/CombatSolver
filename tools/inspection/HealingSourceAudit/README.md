# 原生回复入口扫描

读取指定游戏 DLL 的 IL 元数据，列出生命值命令、内部写入口、直接字段写入和字段取址；同时保存静态方法/委托引用、泛型模型参数、异步方法映射、回复相关回调、全部原版模型虚方法、Hook 调用入口和模型定义。不会加载或执行游戏模型，不会修改 DLL。

构建时传入已安装 ILSpy 的 `Mono.Cecil.dll`，不把该依赖或游戏二进制提交到仓库：

```bash
dotnet build tools/inspection/HealingSourceAudit/HealingSourceAudit.csproj -c Release \
  -p:CecilAssemblyPath=/path/to/Mono.Cecil.dll
dotnet .local/tool-build/HealingSourceAudit/bin/Release/net9.0/HealingSourceAudit.dll \
  /path/to/sts2.dll .local/healing-audit/native-references.json
```

输出包括输入 SHA-256、MVID 和方法数量。保留大型扫描产物于 `.local/`，提交的审计只保留复核所需元数据、来源哈希及人工语义判断。

调用列表只是调查入口。它不能证明目标是玩家、调用实际可达、回复次数有限或生成集合封闭；设置生命还可能是损血、敌人初始化、读档或同步。静态图也不能解决虚调用、反射与外部 Mod 的回调。必须结合当前版本的完整原生实现、生成过滤及原生/模拟差分，未知来源继续拒绝认证。
