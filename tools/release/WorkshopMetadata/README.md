# 创意工坊标题与介绍维护

修复或维护一种语言的工坊元数据时，读取 [metadata.json](../../../docs/workshop/metadata.json) 与该语言的完整介绍，成对提交标题和介绍。语言字段先设置，所有 API 的返回值与最终提交结果均检查；失败直接退出。工具只调用标题、介绍与语言接口，不设置二进制、更新日志、图片、标签、依赖或可见性。

依赖 .NET 9、已登录的 Steam 客户端、当前游戏安装内配套的 `Steamworks.NET.dll` 与 Steam API 原生库。游戏位置沿用仓库 `local.props`，也可通过 MSBuild 的 `Sts2DataDir` 显式指定。构建产物写入 `.local/tool-build/WorkshopMetadata/`。

在仓库根目录运行：

```powershell
dotnet build tools/release/WorkshopMetadata/WorkshopMetadata.csproj -c Release
dotnet .local/tool-build/WorkshopMetadata/bin/Release/net9.0/WorkshopMetadata.dll . schinese --validate-only
dotnet .local/tool-build/WorkshopMetadata/bin/Release/net9.0/WorkshopMetadata.dll . schinese --apply
```

英文使用 `english`。Linux 使用相同的 `dotnet` 命令，构建复制游戏内的 `libsteam_api.so`。`--validate-only` 检查所选语言的标题与介绍长度，输出条目、语言、标题和介绍字节数，不连接 Steam。`--apply` 完成元数据提交，成功证据为 `SubmitItemUpdate=k_EResultOK`、匹配的条目 ID 与退出码 0；成功后不重复上传或打开网页验证。60 秒超时表示提交结果未知，重试前读取 Steam 工坊日志。
