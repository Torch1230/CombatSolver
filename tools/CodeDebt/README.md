# CodeDebt 静态审计

只读审计主源码与夹具；不加载游戏、不运行工具入口、不修改设置或部署 Mod。原始产物放仓库 `.local/debt/`，报告里的计数必须注明来源提交。

```bash
export DOTNET_ROOT=/opt/homebrew/Cellar/dotnet/9.0.8/libexec DOTNET_ROLL_FORWARD=Major
mkdir -p .local/debt
nohup bash tools/CodeDebt/run_analysis.sh > .local/debt/analyzer-launch.log 2>&1 &
# 等待 analyzer.exit 出现；0 才是成功。运行中不得编辑生产源码或启动离线宿主。
dotnet run --project tools/CodeDebt/CodeMetrics -c Release -- "$PWD" "$PWD/.local/debt/before"
python3 tools/CodeDebt/sarif.py --input .local/debt/analyzer.sarif --out .local/debt/before
python3 tools/CodeDebt/clones.py --tokens .local/debt/before/tokens.json --out .local/debt/before/clones.json
python3 tools/CodeDebt/survey.py --data .local/debt/before
python3 tools/CodeDebt/check_tools.py --out .local/debt/tool-builds
python3 tools/CodeDebt/self_test.py
```

`run_analysis.sh` 临时安装专用 `.editorconfig`，退出时移除；已有配置时拒绝覆盖。全量 `latest-all`、重点规则 warning、SARIF 中被抑制诊断均保留，构建固定 `CopyModOnBuild=false`。运行时不要另开主工程构建；离线宿主活动时脚本拒绝构建。

## 指标与限制

- CodeMetrics 引用当前 .NET SDK 随带的 `Microsoft.CodeAnalysis.CSharp`，不下载新包，不需要游戏程序集。保留源码行号、类型、方法、访问器和局部函数。圈复杂度从1开始，计 if/循环/catch/条件表达式/case/短路与合并运算及 switch expression 非默认分支数；匿名函数体不计入外层，局部函数单列。嵌套深度统计控制结构；局部变量计声明和模式绑定，不是活跃变量峰值。类型成员为源码声明，非编译器生成成员。
- `references.json` 只收已绑定的源码符号；`parse.json` 单列未绑定名字。依赖边是源码 NamedType 引用，目录和命名空间不是同一种边界。没有游戏元数据时不能把未绑定引用当成“无引用”。`coupling.json` 只计同一 partial 类型跨声明文件的 private 字段，不把不同类型的私有字段混入；编译器为主构造参数合成的捕获字段不在源码字段计数中。
- clones.py 使用 Roslyn 词法 token 的代码行序列，排除仅括号/分号的行，保留字面量、关键字、运算符与原行号；标识符统一占位后做 SHA-256 窗口哈希，再逐 token 核对及扩展最大匹配，最低12个代码行。同文件重叠对不计。不是 AST 语义等价证明，跨行格式不同可漏报。literal 是忽略空白/注释后的逐 token 相同；identifier-only 要求名称双射且没有成员访问/构造类型改名；structural-review 包含成员或类型改名，必须按语义另审。相似度是对应原始 token 相同的比例。所有候选都需要人工确认作用域与求值顺序。
- survey.py 联结引用、设置、场景、夹具、文档、工具目录与本地化。场景字符串没有 Executor 特判不等于无处理：通用执行器通过请求字段运行。请求字段未出现在JSON不等于无测试，另列工具与源码赋值。UI/文档/测试引用仅表示静态提及，不是分支执行覆盖证明。
- 文档检查本地 inline Markdown 链接与常用标题锚点，不访问网络；复杂HTML、自定义渲染器与 reference-style 链接需人工补查。缺失锚点列为待复核，不能自动删除历史链接。
- 本地化同时收普通字符串及插值还原模板；无字面调用者仍须检查转发函数与动态构造。分析器 IDE0058 只说返回值没用，不表示调用无副作用；CA1508 对闭包、反射、原生边界可能误判，不能直接删分支。catch 扫描只做风险分类，不改异常语义。

## 安全清理提案

```bash
dotnet run --project tools/CodeDebt/CodeMetrics -c Release -- "$PWD" "$PWD/.local/debt" --propose-safe
```

只输出 `safe-proposal.json` 的原文、候选改文与裁决，不写源码。限定分析器命中的 private 方法，排除 Engine/Prediction/Testing、Patch、特性入口、字符串/nameof/方法组/限定接收者。删除实参仅接受已绑定的局部变量、参数或字面量；未绑定、ref、命名或省略参数调用交人工处理。修改前还应检查 `tools/` 的字符串与反射引用，使用补丁工具应用并验证；提案不是自动安全证明。

每个清理提交必须普通 Release 0警告/0错误及结构门禁；纯移动必须保存原成员文本对账，中风险批次和最终离线验证按任务约定执行。不得在离线批次期间重建 DLL。
