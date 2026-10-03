# ServerGC 启动配置

Mod 默认准备下一次 Steam 启动的 ServerGC 配置。首次加载只写入游戏程序集旁的 `runtimeconfig.json`；实际模式以当前进程的 CLR 与 `AppContext` 标记为准，下一次正常启动才生效。保存的 NoGC 设置保持，由实际运行模式决定搜索是否使用常规分代回收。

设置页关闭自动配置后，恢复本 Mod 记录的原 GC 值并移除自身标记，下次启动生效。退出或退订前先关闭该开关；已经退订时可恢复字段或验证游戏文件。未知标记、外部修改或并发变化拒绝覆盖，缺失配置或写入失败会记录原因。

该配置影响整个游戏进程；实际 CPU、速度和路线质量需要同根同政策数据。无头或离线数据不代表可见 Steam 帧时间。

开发者可运行 `dotnet run --project tools/RuntimeGcProfileChecks -c Release`；隔离原生启动入口为 `tools/test-runtime-gc-startup.ps1`。显式 profile 的启动命令与历史验证见 [启动配置记录](../archive/performance/server-gc-launch-profile-20260924.md)。
