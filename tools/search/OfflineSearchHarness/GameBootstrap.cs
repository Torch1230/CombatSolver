using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace OfflineSearchHarness;

/// <summary>
/// 游戏核心静态状态的离线初始化，以及所有「会打到 Godot 原生层」的绕过。全部绕过只在本类里出现，
/// 结果 JSON 的 <c>bypasses</c> 字段逐条列出实际装上的那些。
///
/// <code>
/// 目标                                        为什么必须绕                                   绕过后返回                对搜索结果的影响
/// Logger.GetIsRunningFromGodotEditor          静态构造读 OS.GetCmdlineArgs/OS.HasFeature      false                     无：只决定日志去向
/// ConsoleLogPrinter.Print                     走 GD.Print/GD.PrintErr                        写 System.Console         无：只决定日志去向
/// LocString.GetRawText / GetFormattedText     LocManager.Initialize 用 Godot.FileAccess      本地化键名                无：搜索不读文案（见“已知限制”）
/// LocString.Exists(table,key)                 同上                                           true                      无：同上
/// PreloadManager.Load{Run,Act,RoomCombat}Assets  全是 Godot 资源加载                         Task.CompletedTask        无：战斗建立不用立绘/节点
/// NCombatRulesFtue.Create                     基础教学界面是 Godot 节点                       null                      无：与游戏内无人测试同语义
/// MigrationRegistry.RegisterAllMigrations     迁移类字段初始化要造 Godot.StringName（原生）    跳过注册                  无：离线不读任何存档
/// NGame.GetGameVersion                        版本号读 Godot 资源（ReleaseInfoManager）       固定串                    无：只进联机握手信息
/// PlatformUtil.GetPlatformBranch              平台原生查询                                   None                      无：同上
/// PlatformUtil.GetPlayerNameRaw               策略构造读命令行与 Godot 文件系统               与无人测试同一份映射      无：只是显示名
/// NHoverTipSet.Clear                          跳过节点静态构造后 _activeHoverTips 为 null     跳过                     无：战斗胜利收尾防 NRE
/// SoulNexus.AfterDeath                        原版缺 NCombatRoom.Instance?. 判空              无房间节点时跳过          无：仅清 Spine 死亡动画
/// Crusher/Rocket.BeforeDeath                  原版缺 NAudioManager.Instance?. 判空            无音频单例时 Task.CompletedTask 无：仅播死亡音效/背景动画
/// FakeMerchantMonster.ShowDialogueForMove     调 LocManager.GetTable("monsters")              Task.CompletedTask       无：仅播对话气泡（用 Chaotic RNG）
/// GameAction.Cancel                           原版用 Callable.From(...).CallDeferred()        同步 TrySetCanceled       无：离线无 Godot 延迟队列
/// RelicModel..cctor                           3 个 UI 着色器 StringName 静态字段              跳过静态构造              无：战斗不调 UpdateTexture
/// Godot.Time/OS/Engine/DisplayServer/         静态构造/原生绑定会触发地址 0 段错误            纯托管桩返回值            无：离线无 Godot 原生运行时
///   ProjectSettings/DirAccess/FileAccess/
///   ResourceLoader/GD/GodotObject/Input/Server
/// ProgressSaveManager.SaveProgress            离线内存存档未初始化 ProfileId                  跳过                     无：离线不落盘进度存档
/// RunManager.WriteReplay                      离线不写 .mcr 战斗回放文件                       跳过                     无：离线不落盘回放
/// SaveManager._currentProfileId               内存存档实例默认未设 ProfileId                   设为 1（不触盘）          无：仅防 CurrentProfileId 抛错
/// Godot.Node 及其 304 个子类的静态构造         字段初始化造 StringName/NodePath（原生）        跳过静态构造              无：离线不建节点树
/// </code>
///
/// 还有一处不是 Harmony 补丁：<c>SolverController.DisplayServerNameProvider</c> 被设成固定返回
/// <c>"headless"</c>（见 <see cref="ApplyGodotBypasses"/>），与游戏内 <c>--headless</c> 取到的值一致，
/// 帧压力恢复照样关闭。
/// </summary>
internal static class GameBootstrap
{
    public const string HarmonyId = "combatsolver.offline.harness";
    private static Harmony? _harmony;
    private static readonly List<string> _bypasses = [];
    private static bool _nodeCctorsSkipped;
    private static bool _staticStateInitialized;
    private static MethodInfo? _gameActionStateSetter;
    private static FieldInfo? _gameActionBeforeCancelledField;
    private static MethodInfo? _gameActionCancelActionMethod;
    private static FieldInfo? _gameActionCompletionSourceField;

    public static IReadOnlyList<string> Bypasses => _bypasses;
    public static Harmony Harmony => _harmony ?? throw new InvalidOperationException("Harmony 未初始化。");

    /// <summary>幂等初始化离线宿主（Harmony 绕过 + Godot 节点静态构造跳过 + ModelDb/存档静态状态）。</summary>
    public static void EnsureBootstrapped(MainLoopContext loop, HarnessOptions? modOptions = null)
    {
        SynchronizationContext.SetSynchronizationContext(loop);
        ApplyGodotBypasses();
        _ = SkipGodotNodeStaticConstructors();
        _ = InitializeStaticState();
        if (modOptions != null && ModRuntime.Session == null)
            _ = ModRuntime.Initialize(modOptions);
    }

    /// <summary>第一步：只装绕过补丁，必须在任何游戏代码跑起来之前。</summary>
    public static void ApplyGodotBypasses()
    {
        if (_harmony != null)
            return;
        _harmony = new Harmony(HarmonyId);

        // 1) Logger 的静态构造读 OS.GetCmdlineArgs()/OS.HasFeature，ConsoleLogPrinter.Print 走 GD.Print，
        //    两者都是 Godot 原生调用。让编辑器判定直接返回 false，并把打印改到 System.Console。
        Patch(typeof(Logger), "GetIsRunningFromGodotEditor", prefix: nameof(NotGodotEditorPrefix),
            note: "Logger.GetIsRunningFromGodotEditor -> false（原为 OS.GetCmdlineArgs/OS.HasFeature 原生调用）");
        Patch(typeof(ConsoleLogPrinter), nameof(ConsoleLogPrinter.Print), prefix: nameof(ConsolePrintPrefix),
            note: "ConsoleLogPrinter.Print -> System.Console（原为 GD.Print/GD.PrintErr）");

        // 2) 本地化：LocManager.Initialize 用 Godot.FileAccess 读 res://localization，离线不可用。
        //    先走简报的 (b) 方案：文案一律返回键名，搜索本身不读文案。
        Patch(typeof(LocString), nameof(LocString.GetRawText), prefix: nameof(LocRawTextPrefix),
            note: "LocString.GetRawText -> 键名（LocManager.Instance 为 null）");
        Patch(typeof(LocString), nameof(LocString.GetFormattedText), prefix: nameof(LocFormattedTextPrefix),
            note: "LocString.GetFormattedText -> 键名（同上）");
        Patch(typeof(LocString), nameof(LocString.Exists), prefix: nameof(LocExistsPrefix),
            note: "LocString.Exists(table,key) -> true（同上）",
            args: [typeof(string), typeof(string)]);

        // 3) 资源预载全是 Godot 资源加载；战斗建立本身不需要（怪物节点、立绘只在有 NCombatRoom 时才用）。
        foreach (string loader in new[] { "LoadRunAssets", "LoadActAssets", "LoadRoomCombatAssets" })
        {
            Patch(typeof(MegaCrit.Sts2.Core.Assets.PreloadManager), loader, prefix: nameof(CompletedTaskPrefix),
                note: $"PreloadManager.{loader} -> Task.CompletedTask（Godot 资源加载）");
        }

        // 4) 战斗基础教学界面是 Godot 节点。游戏内无人测试用同名补丁把它挡掉（UnattendedHeadlessFtuePatch），
        //    这里照同一语义来，保证与参考跑法走同一条分支。
        Patch(typeof(MegaCrit.Sts2.Core.Nodes.Ftue.NCombatRulesFtue),
            nameof(MegaCrit.Sts2.Core.Nodes.Ftue.NCombatRulesFtue.Create), prefix: nameof(NullResultPrefix),
            note: "NCombatRulesFtue.Create -> null（与游戏内 UnattendedHeadlessFtuePatch 同语义）",
            args: Type.EmptyTypes);

        // 5) 存档迁移表：注册时会实例化每个迁移类，其中控制器按键迁移的字段初始化要造 Godot.StringName，
        //    那是原生调用（跳转到地址 0 直接段错误）。离线不读任何存档，所以整张迁移表不注册。
        Patch(typeof(MegaCrit.Sts2.Core.Saves.Migrations.MigrationRegistry),
            nameof(MegaCrit.Sts2.Core.Saves.Migrations.MigrationRegistry.RegisterAllMigrations),
            prefix: nameof(SkipPrefix),
            note: "MigrationRegistry.RegisterAllMigrations -> 跳过（迁移类字段初始化会造 Godot.StringName）");

        // 6) 联机握手用的本机版本信息：版本号读 Godot 资源（ReleaseInfoManager），分支读 OS.HasFeature。
        //    离线是单机，这两项只进 PeerVersionInfo，不影响战斗。
        Patch(typeof(NGame), nameof(NGame.GetGameVersion), prefix: nameof(GameVersionPrefix),
            note: "NGame.GetGameVersion -> 固定串（原为 ReleaseInfoManager/Git 读 Godot 资源）");
        Patch(typeof(MegaCrit.Sts2.Core.Platform.PlatformUtil),
            nameof(MegaCrit.Sts2.Core.Platform.PlatformUtil.GetPlatformBranch),
            prefix: nameof(PlatformBranchPrefix),
            note: "PlatformUtil.GetPlatformBranch -> None（原为平台原生查询）");

        // 7) 玩家名：PlatformUtil 的 None 策略在构造时读命令行（OS.GetCmdlineArgs）并用 GodotFileIo 找
        //    mp_names.json，两处都是原生调用。直接给出该策略在无 mp_names.json 时的同一份映射，
        //    这与参考跑法（--force-steam=off ⇒ PlatformType.None）返回的名字一致。
        Patch(typeof(MegaCrit.Sts2.Core.Platform.PlatformUtil),
            nameof(MegaCrit.Sts2.Core.Platform.PlatformUtil.GetPlayerNameRaw),
            prefix: nameof(PlayerNameRawPrefix),
            note: "PlatformUtil.GetPlayerNameRaw -> NullPlatformUtilStrategy 的同一映射（原策略构造读命令行与 Godot 文件系统）");

        // 8) 完整纯 .NET 实机战斗收尾/怪物死亡所需的绕过：
        //    - NHoverTipSet.Clear（CombatManager.EndCombatInternal 胜利收尾）：跳过节点静态构造后 _activeHoverTips 为 null，不绕过会抛 NRE。
        //    - SoulNexus.AfterDeath：原版缺少 NCombatRoom.Instance?. 判空，无房间节点死亡时会抛 NRE。
        //    - Crusher.BeforeDeath / Rocket.BeforeDeath：原版缺少 NAudioManager.Instance?. 判空，无音频单例死亡时会抛 NRE。
        //    - RelicModel..cctor：静态构造初始化 3 个仅用于 UpdateTexture 的 Godot.StringName 字段，会触发原生段错误。
        //    - GameAction.Cancel：原版用 Callable.From(() => _completionSource.TrySetCanceled()).CallDeferred()，会触发原生段错误。
        Patch(typeof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet),
            nameof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet.Clear),
            prefix: nameof(SkipPrefix),
            note: "NHoverTipSet.Clear -> 跳过（节点静态构造跳过后 _activeHoverTips 为 null，战斗胜利防 NRE）",
            args: Type.EmptyTypes);
        Patch(typeof(MegaCrit.Sts2.Core.Models.Monsters.SoulNexus),
            "AfterDeath",
            prefix: nameof(SoulNexusAfterDeathPrefix),
            note: "SoulNexus.AfterDeath -> 无 NCombatRoom 时跳过（原版缺 ?. 判空，防死亡 NRE）",
            args: [typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)]);
        Patch(typeof(MegaCrit.Sts2.Core.Models.Monsters.Crusher),
            nameof(MegaCrit.Sts2.Core.Models.Monsters.Crusher.BeforeDeath),
            prefix: nameof(SkipAudioBeforeDeathPrefix),
            note: "Crusher.BeforeDeath -> 无 NAudioManager 时返回 CompletedTask（原版缺 ?. 判空，仅播音效/背景动画）",
            args: [typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)]);
        Patch(typeof(MegaCrit.Sts2.Core.Models.Monsters.Rocket),
            nameof(MegaCrit.Sts2.Core.Models.Monsters.Rocket.BeforeDeath),
            prefix: nameof(SkipAudioBeforeDeathPrefix),
            note: "Rocket.BeforeDeath -> 无 NAudioManager 时返回 CompletedTask（原版缺 ?. 判空，仅播音效/背景动画）",
            args: [typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)]);
        Patch(typeof(MegaCrit.Sts2.Core.Models.Monsters.FakeMerchantMonster),
            "ShowDialogueForMove",
            prefix: nameof(CompletedTaskPrefix),
            note: "FakeMerchantMonster.ShowDialogueForMove -> Task.CompletedTask（离线 LocManager 无 monsters 表，仅播对话气泡）",
            args: [typeof(string)]);
        SkipStaticCctor(typeof(RelicModel), "RelicModel..cctor -> 跳过（3 个 UI 着色器 StringName 静态字段）");

        _gameActionStateSetter = AccessTools.PropertySetter(typeof(GameAction), nameof(GameAction.State))
            ?? throw new MissingMethodException(typeof(GameAction).FullName, "set_State");
        _gameActionBeforeCancelledField = AccessTools.Field(typeof(GameAction), "BeforeCancelled")
            ?? throw new MissingFieldException(typeof(GameAction).FullName, "BeforeCancelled");
        _gameActionCancelActionMethod = AccessTools.Method(typeof(GameAction), "CancelAction", Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(GameAction).FullName, "CancelAction");
        _gameActionCompletionSourceField = AccessTools.Field(typeof(GameAction), "_completionSource")
            ?? throw new MissingFieldException(typeof(GameAction).FullName, "_completionSource");
        Patch(typeof(GameAction),
            nameof(GameAction.Cancel),
            prefix: nameof(GameActionCancelPrefix),
            note: "GameAction.Cancel -> 同步 TrySetCanceled（替代 Callable.From(...).CallDeferred() 防原生段错误）",
            args: Type.EmptyTypes);

        // 8b) Godot 原生静态单例与工具类统一绕过：
        //     这些类的 .cctor 会造 Godot.StringName / MethodBind* 原生句柄，静态方法直接 P/Invoke 到 Godot 原生层。
        //     CardModel.OnPlayWrapper 无条件调 Godot.Time.GetTicksMsec()，故必须完整提供纯托管桩。
        SkipStaticCctor(typeof(Godot.ResourceLoader), "Godot.ResourceLoader..cctor -> 跳过（防原生 StringName/ClassDB 段错误）");
        SkipStaticCctor(typeof(Godot.Time), "Godot.Time..cctor -> 跳过（防原生 StringName/MethodBind 段错误）");
        SkipStaticCctor(typeof(Godot.OS), "Godot.OS..cctor -> 跳过（防原生 StringName/MethodBind 段错误）");
        SkipStaticCctor(typeof(Godot.Engine), "Godot.Engine..cctor -> 跳过（防原生 StringName/MethodBind 段错误）");
        SkipStaticCctor(typeof(Godot.DisplayServer), "Godot.DisplayServer..cctor -> 跳过（防原生 StringName/MethodBind 段错误）");
        SkipStaticCctor(typeof(Godot.ProjectSettings), "Godot.ProjectSettings..cctor -> 跳过（防原生 StringName/MethodBind 段错误）");
        SkipStaticCctor(typeof(Godot.DirAccess), "Godot.DirAccess..cctor -> 跳过（防原生 StringName/MethodBind 段错误）");
        SkipStaticCctor(typeof(Godot.FileAccess), "Godot.FileAccess..cctor -> 跳过（防原生 StringName/MethodBind 段错误）");
        SkipStaticCctor(typeof(Godot.Input), "Godot.Input..cctor -> 跳过（防原生 StringName/MethodBind 段错误）");
        SkipStaticCctor(typeof(Godot.AudioServer), "Godot.AudioServer..cctor -> 跳过（防原生 StringName/MethodBind 段错误）");
        SkipStaticCctor(typeof(Godot.RenderingServer), "Godot.RenderingServer..cctor -> 跳过（防原生 StringName/MethodBind 段错误）");
        SkipStaticCctor(typeof(Godot.TranslationServer), "Godot.TranslationServer..cctor -> 跳过（防原生 StringName/MethodBind 段错误）");

        Patch(typeof(Godot.Time), nameof(Godot.Time.GetTicksMsec), prefix: nameof(TicksMsecPrefix),
            note: "Godot.Time.GetTicksMsec -> Environment.TickCount64（CardModel.OnPlayWrapper 必经路径）",
            args: Type.EmptyTypes);
        Patch(typeof(Godot.Time), nameof(Godot.Time.GetTicksUsec), prefix: nameof(TicksUsecPrefix),
            note: "Godot.Time.GetTicksUsec -> Environment.TickCount64 * 1000",
            args: Type.EmptyTypes);
        Patch(typeof(Godot.Time), nameof(Godot.Time.GetUnixTimeFromSystem), prefix: nameof(UnixTimePrefix),
            note: "Godot.Time.GetUnixTimeFromSystem -> DateTimeOffset.UtcNow",
            args: Type.EmptyTypes);

        Patch(typeof(Godot.OS), nameof(Godot.OS.HasFeature), prefix: nameof(FalseResultPrefix),
            note: "Godot.OS.HasFeature -> false",
            args: [typeof(string)]);
        Patch(typeof(Godot.OS), nameof(Godot.OS.GetName), prefix: nameof(OsNamePrefix),
            note: "Godot.OS.GetName -> \"Windows\"",
            args: Type.EmptyTypes);
        Patch(typeof(Godot.OS), nameof(Godot.OS.GetCmdlineArgs), prefix: nameof(EmptyStringArrayPrefix),
            note: "Godot.OS.GetCmdlineArgs -> empty",
            args: Type.EmptyTypes);

        Patch(typeof(Godot.Engine), nameof(Godot.Engine.GetMainLoop), prefix: nameof(NullMainLoopPrefix),
            note: "Godot.Engine.GetMainLoop -> null",
            args: Type.EmptyTypes);
        Patch(typeof(Godot.Engine), nameof(Godot.Engine.IsEditorHint), prefix: nameof(FalseResultPrefix),
            note: "Godot.Engine.IsEditorHint -> false",
            args: Type.EmptyTypes);

        Patch(typeof(Godot.DisplayServer), nameof(Godot.DisplayServer.GetName), prefix: nameof(HeadlessNamePrefix),
            note: "Godot.DisplayServer.GetName -> \"headless\"",
            args: Type.EmptyTypes);
        Patch(typeof(Godot.DisplayServer), nameof(Godot.DisplayServer.GetScreenCount), prefix: nameof(ZeroIntPrefix),
            note: "Godot.DisplayServer.GetScreenCount -> 0",
            args: Type.EmptyTypes);

        Patch(typeof(Godot.ProjectSettings), nameof(Godot.ProjectSettings.GlobalizePath), prefix: nameof(IdentityStringPrefix),
            note: "Godot.ProjectSettings.GlobalizePath -> 原串",
            args: [typeof(string)]);

        Patch(typeof(Godot.DirAccess), nameof(Godot.DirAccess.Open), prefix: nameof(NullDirAccessPrefix),
            note: "Godot.DirAccess.Open -> null",
            args: [typeof(string)]);
        Patch(typeof(Godot.DirAccess), nameof(Godot.DirAccess.DirExistsAbsolute), prefix: nameof(FalseResultPrefix),
            note: "Godot.DirAccess.DirExistsAbsolute -> false",
            args: [typeof(string)]);
        Patch(typeof(Godot.DirAccess), nameof(Godot.DirAccess.GetDirectoriesAt), prefix: nameof(EmptyStringArrayPrefix),
            note: "Godot.DirAccess.GetDirectoriesAt -> empty",
            args: [typeof(string)]);
        Patch(typeof(Godot.DirAccess), nameof(Godot.DirAccess.GetFilesAt), prefix: nameof(EmptyStringArrayPrefix),
            note: "Godot.DirAccess.GetFilesAt -> empty",
            args: [typeof(string)]);

        Patch(typeof(Godot.FileAccess), nameof(Godot.FileAccess.Open), prefix: nameof(NullFileAccessPrefix),
            note: "Godot.FileAccess.Open -> null",
            args: [typeof(string), typeof(Godot.FileAccess.ModeFlags)]);
        Patch(typeof(Godot.FileAccess), nameof(Godot.FileAccess.FileExists), prefix: nameof(FalseResultPrefix),
            note: "Godot.FileAccess.FileExists -> false",
            args: [typeof(string)]);

        Patch(typeof(Godot.ResourceLoader),
            nameof(Godot.ResourceLoader.Exists),
            prefix: nameof(FalseResultPrefix),
            note: "Godot.ResourceLoader.Exists -> false（离线无 Godot 资源层）",
            args: [typeof(string), typeof(string)]);
        MethodInfo rlLoad = typeof(Godot.ResourceLoader)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(static m => m.Name == nameof(Godot.ResourceLoader.Load)
                && !m.IsGenericMethodDefinition
                && m.GetParameters().Length == 3);
        Harmony.Patch(rlLoad, prefix: new HarmonyMethod(
            typeof(GameBootstrap).GetMethod(nameof(NullResourcePrefix), BindingFlags.Static | BindingFlags.NonPublic)!));
        _bypasses.Add("Godot.ResourceLoader.Load -> null（离线不加载贴图/场景）");

        Patch(typeof(Godot.GD), nameof(Godot.GD.Load), prefix: nameof(NullResourcePrefix),
            note: "Godot.GD.Load -> null",
            args: [typeof(string)]);
        HashSet<string> gdSkipNames =
            ["Print", "PrintRich", "PrintErr", "PrintRaw", "PrintS", "PrintT", "PushError", "PushWarning"];
        MethodInfo skipMethod = typeof(GameBootstrap).GetMethod(nameof(SkipPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
        int gdPatched = 0;
        foreach (MethodInfo method in typeof(Godot.GD).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!method.IsGenericMethodDefinition && gdSkipNames.Contains(method.Name))
            {
                Harmony.Patch(method, prefix: new HarmonyMethod(skipMethod));
                gdPatched++;
            }
        }
        _bypasses.Add($"Godot.GD 打印/告警静态方法 ({gdPatched} 个重载) -> 跳过（防原生调用）");

        Patch(typeof(Godot.GodotObject), nameof(Godot.GodotObject.IsInstanceValid), prefix: nameof(IsInstanceValidPrefix),
            note: "Godot.GodotObject.IsInstanceValid -> instance != null",
            args: [typeof(Godot.GodotObject)]);

        // 8c) 离线不落盘玩家进度与战斗回放，跳过 ProgressSaveManager.SaveProgress 与 RunManager.WriteReplay，
        //     避免战斗胜利/收尾触发 SaveManager.CurrentProfileId 或写磁盘。
        Patch(typeof(MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager),
            nameof(MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager.SaveProgress),
            prefix: nameof(SkipPrefix),
            note: "ProgressSaveManager.SaveProgress -> 跳过（离线不写 progress.save）",
            args: Type.EmptyTypes);
        Patch(typeof(MegaCrit.Sts2.Core.Runs.RunManager),
            nameof(MegaCrit.Sts2.Core.Runs.RunManager.WriteReplay),
            prefix: nameof(SkipPrefix),
            note: "RunManager.WriteReplay -> 跳过（离线不写 .mcr 战斗回放，防胜利时 InitProfileId 抛错）",
            args: [typeof(bool)]);

        // 9) CaptureSearchPolicy 用显示服务器的名字决定要不要开帧压力恢复。离线没有显示服务器，
        //    走模组自己留的注入口给固定值：与游戏内 --headless 取到的一致，恢复逻辑照样关闭。
        CombatSolver.SolverController.DisplayServerNameProvider = static () => "headless";
        _bypasses.Add("SolverController.DisplayServerNameProvider -> \"headless\"（不启动显示服务器；与游戏内 --headless 同值）");
    }

    /// <summary>第二步：游戏静态状态。顺序照 OneTimeInitialization.ExecuteEssential。</summary>
    private static void Trace(string stage) => HarnessLog.Trace(stage);

    public static string InitializeStaticState()
    {
        if (_staticStateInitialized)
            return "already_initialized";
        Trace("begin");
        // ModManager 没有初始化，ReflectionHelper.ModTypes 会抛。离线没有被游戏加载的模组，
        // 直接把「模组类型集合」定成空数组，并把 ModManager 状态推过 None。
        AccessTools.Field(typeof(ReflectionHelper), "_modTypes").SetValue(null, Array.Empty<Type>());
        AccessTools.PropertySetter(typeof(ModManager), nameof(ModManager.State))
            ?.Invoke(null, [ModManagerState.Initialized]);
        Trace("mod_manager_state");

        // 游戏自己的单元测试模式：ActionExecutor 走同步 NonInteractiveMode 分支（不等 Godot 帧），
        // FadeIn/FadeOut/ClearScreens 变成空操作，ChecksumTracker 与 CombatReplayWriter 关闭。
        TestMode.IsOn = true;
        Trace("test_mode");

        // 提前把游戏日志器跑起来：它的静态构造是第一个会打到 Godot 原生层的地方。
        Log.Info("[offline-harness] logger probe");
        Trace("logger");

        // 存档层：真实实现走 Godot 文件系统。用游戏自带的内存 Mock。
        Trace("save_store");
        MemorySaveStore store = new();
        if (Environment.GetEnvironmentVariable("OFFLINE_HARNESS_PROBE_MIGRATIONS") == "1")
        {
            for (int index = 0; index < MegaCrit.Sts2.Core.Saves.Migrations.IMigrationSubtypes.Count; index++)
            {
                Type migration = MegaCrit.Sts2.Core.Saves.Migrations.IMigrationSubtypes.Get(index);
                Trace($"migration[{index}] {migration.FullName}");
                _ = Activator.CreateInstance(migration);
            }
        }
        _ = new MegaCrit.Sts2.Core.Saves.Migrations.MigrationManager(store);
        Trace("migration_manager");
        SaveManager saveManager = new(store, forceSynchronous: true);
        SaveManager.MockInstanceForTesting(saveManager);
        Trace("save_manager");
        saveManager.InitSettingsDataForTest();
        saveManager.InitPrefsDataForTest();
        // 离线内存存档不调 InitProfileId（会清理/创建真实磁盘目录），直接写入 _currentProfileId = 1 防任何路径读取 CurrentProfileId 抛错。
        AccessTools.Field(typeof(SaveManager), "_currentProfileId")?.SetValue(saveManager, 1);
        _bypasses.Add("SaveManager._currentProfileId -> 1（纯内存赋值，不触磁盘）");
        Trace("save_data");

        Trace("localization " + OfflineLocalization.Install(HarnessLog.Language));
        _bypasses.Add($"LocManager 单例改为空表实例（language={HarnessLog.Language}），不跑其构造函数与 res://localization 加载");

        AssemblyInfo.Init();
        Trace("assembly_info");
        ModelDb.Init(AbstractModelSubtypes.All.ToArray());
        Trace("model_db_init");
        ModelIdSerializationCache.Init();
        Trace("model_id_cache");
        ModelDb.InitIds();
        Trace("model_db_init_ids");
        MessageTypes.Initialize();
        Trace("message_types");
        ActionTypes.Initialize();
        Trace("action_types");
        // ModelDb.Preload 只是把贴图/图标路径这类缓存提前算好，里面 AllPortraitPaths、IconPath 走
        // Godot 资源层。战斗与搜索都不读这些路径，所以整个预热跳过（纯缓存，不改语义）。
        _ = ModelDb.AllCards;
        _ = ModelDb.AllRelics;
        _ = ModelDb.AllPotions;
        _ = ModelDb.AllEncounters;
        Trace("model_db_warm");

        // 模组的根捕获与模拟状态构造都要求主线程；NGame 未构造时 _mainThreadId 是 0。
        AccessTools.Field(typeof(NGame), "_mainThreadId").SetValue(null, Environment.CurrentManagedThreadId);
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("NGame.IsMainThread() 仍为 false。");

        _staticStateInitialized = true;
        return $"models={ModelDb.All.Count()} cards={ModelDb.AllCards.Count()} "
            + $"encounters={ModelDb.All.OfType<EncounterModel>().Count()} "
            + $"characters={ModelDb.AllCharacters.Count()} main_thread={NGame.IsMainThread()}";
    }

    /// <summary>
    /// 逐个跑 sts2 里名字匹配的类型的静态构造。段错误（跳地址 0）常常发生在某个
    /// 静态字段初始化要造 Godot 原生对象时；最后一行 trace 就是罪魁。
    /// </summary>
    public static int ProbeStaticConstructors(string filter)
    {
        Type[] types = typeof(AbstractModel).Assembly.GetTypes()
            .Where(type => type.FullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        foreach (Type type in types)
        {
            if (type.ContainsGenericParameters)
                continue;
            Trace($"static_ctor {type.FullName}");
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            catch (Exception error)
            {
                Trace($"static_ctor_failed {type.FullName}: {error.GetType().Name}: {error.Message}");
            }
        }
        Trace($"static_ctor_done filter={filter} types={types.Length}");
        return types.Length;
    }

    /// <summary>
    /// Godot 源生成的节点类型，其静态构造会造 <c>Godot.StringName</c>（MethodName/PropertyName 表），
    /// 离线没有 Godot 原生层，一造就跳地址 0。战斗逻辑里凡是取 <c>NXxx.Instance</c> 的地方都会
    /// 触发这件事，而离线本来就没有场景树、这些单例本来就该是 null。所以整类跳过它们的静态构造：
    /// 静态字段保持默认值（引用型即 null），与「没有场景树」完全一致。
    /// </summary>
    public static int SkipGodotNodeStaticConstructors()
    {
        if (_nodeCctorsSkipped)
            return 0;
        _nodeCctorsSkipped = true;
        int patched = 0;
        Type godotObject = typeof(Godot.GodotObject);
        bool trace = Environment.GetEnvironmentVariable("OFFLINE_HARNESS_TRACE_NODE_CCTOR") == "1";
        MethodInfo prefix = typeof(GameBootstrap).GetMethod(
            trace ? nameof(TraceNodeStaticConstructorPrefix) : nameof(SkipNodeStaticConstructorPrefix),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (Type type in typeof(AbstractModel).Assembly.GetTypes())
        {
            if (!godotObject.IsAssignableFrom(type) || type.ContainsGenericParameters)
                continue;
            ConstructorInfo? cctor = type.TypeInitializer;
            if (cctor == null)
                continue;
            try
            {
                Harmony.Patch(cctor, prefix: new HarmonyMethod(prefix));
                patched++;
            }
            catch (Exception error)
            {
                Trace($"skip_node_cctor_failed {type.FullName}: {error.GetType().Name}");
            }
        }
        _bypasses.Add($"跳过 {patched} 个 Godot 节点类型的静态构造（MethodName/PropertyName 的 StringName 表要 Godot 原生层）");
        return patched;
    }

    private static bool SkipNodeStaticConstructorPrefix() => false;

    private static bool TraceNodeStaticConstructorPrefix(MethodBase __originalMethod)
    {
        Trace($"node_cctor {__originalMethod.DeclaringType?.FullName}");
        return true;
    }

    private static void SkipStaticCctor(Type type, string note)
    {
        if (type.TypeInitializer is not { } cctor)
            return;
        MethodInfo skipCctor = typeof(GameBootstrap).GetMethod(
            nameof(SkipNodeStaticConstructorPrefix),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            Harmony.Patch(cctor, prefix: new HarmonyMethod(skipCctor));
            _bypasses.Add(note);
        }
        catch (Exception error)
        {
            Trace($"skip_cctor_failed {type.FullName}: {error.GetType().Name}: {error.Message}");
        }
    }

    private static MethodInfo ResolveNonGenericMethod(Type type, string method, Type[]? args)
    {
        if (args == null)
        {
            return AccessTools.Method(type, method)
                ?? throw new MissingMethodException(type.FullName, method);
        }

        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            foreach (MethodInfo candidate in current.GetMethods(Flags))
            {
                if (candidate.Name != method || candidate.IsGenericMethodDefinition)
                    continue;
                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length != args.Length)
                    continue;
                bool match = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType != args[i])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return candidate;
            }
        }

        return AccessTools.Method(type, method, args)
            ?? throw new MissingMethodException(type.FullName, method);
    }

    private static void Patch(
        Type type,
        string method,
        string prefix,
        string note,
        Type[]? args = null)
    {
        MethodInfo target = ResolveNonGenericMethod(type, method, args);
        MethodInfo replacement = typeof(GameBootstrap).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)!;
        Harmony.Patch(target, prefix: new HarmonyMethod(replacement));
        _bypasses.Add(note);
    }

    private static bool SkipPrefix() => false;

    private static bool FalseResultPrefix(ref bool __result)
    {
        __result = false;
        return false;
    }

    private static bool NullResourcePrefix(ref Godot.Resource? __result)
    {
        __result = null;
        return false;
    }

    private static bool NullMainLoopPrefix(ref Godot.MainLoop? __result)
    {
        __result = null;
        return false;
    }

    private static bool NullDirAccessPrefix(ref Godot.DirAccess? __result)
    {
        __result = null;
        return false;
    }

    private static bool NullFileAccessPrefix(ref Godot.FileAccess? __result)
    {
        __result = null;
        return false;
    }

    private static bool TicksMsecPrefix(ref ulong __result)
    {
        __result = (ulong)Environment.TickCount64;
        return false;
    }

    private static bool TicksUsecPrefix(ref ulong __result)
    {
        __result = (ulong)Environment.TickCount64 * 1000UL;
        return false;
    }

    private static bool UnixTimePrefix(ref double __result)
    {
        __result = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        return false;
    }

    private static bool OsNamePrefix(ref string __result)
    {
        __result = "Windows";
        return false;
    }

    private static bool HeadlessNamePrefix(ref string __result)
    {
        __result = "headless";
        return false;
    }

    private static bool EmptyStringArrayPrefix(ref string[] __result)
    {
        __result = Array.Empty<string>();
        return false;
    }

    private static bool ZeroIntPrefix(ref int __result)
    {
        __result = 0;
        return false;
    }

    private static bool IdentityStringPrefix(string __0, ref string __result)
    {
        __result = __0;
        return false;
    }

    private static bool IsInstanceValidPrefix(Godot.GodotObject? __0, ref bool __result)
    {
        __result = __0 != null;
        return false;
    }

    private static bool SoulNexusAfterDeathPrefix()
        => MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance != null;

    private static bool SkipAudioBeforeDeathPrefix(ref Task __result)
    {
        if (MegaCrit.Sts2.Core.Nodes.Audio.NAudioManager.Instance != null)
            return true;
        __result = Task.CompletedTask;
        return false;
    }

    private static bool GameActionCancelPrefix(GameAction __instance)
    {
        _gameActionStateSetter?.Invoke(__instance, [GameActionState.Canceled]);
        if (_gameActionBeforeCancelledField?.GetValue(__instance) is Action<GameAction> beforeCancelled)
            beforeCancelled.Invoke(__instance);
        _gameActionCancelActionMethod?.Invoke(__instance, null);
        if (_gameActionCompletionSourceField?.GetValue(__instance) is TaskCompletionSource tcs)
            tcs.TrySetCanceled();
        return false;
    }

    private static bool PlayerNameRawPrefix(ulong playerId, ref string __result)
    {
        __result = playerId switch
        {
            1uL => "Test Host",
            1000uL => "Test Client 1",
            2000uL => "Test Client 2",
            3000uL => "Test Client 3",
            _ => playerId.ToString(),
        };
        return false;
    }

    private static bool GameVersionPrefix(ref string __result)
    {
        __result = "offline-harness";
        return false;
    }

    private static bool PlatformBranchPrefix(ref MegaCrit.Sts2.Core.Platform.PlatformBranch __result)
    {
        __result = MegaCrit.Sts2.Core.Platform.PlatformBranch.None;
        return false;
    }

    private static bool NotGodotEditorPrefix(ref bool __result)
    {
        __result = false;
        return false;
    }

    private static bool ConsolePrintPrefix(LogLevel logLevel, string text, int skipFrames)
    {
        if (logLevel >= LogLevel.Warn)
            Console.Error.WriteLine($"[{logLevel.ToString().ToUpperInvariant()}] {text}");
        else if (HarnessLog.VerboseGameLog)
            Console.WriteLine($"[{logLevel.ToString().ToUpperInvariant()}] {text}");
        return false;
    }

    /// <summary>
    /// 诊断用：给所有文案加一个后缀。文案本不该进搜索状态，如果加了后缀结果就变，
    /// 说明某处把显示名喂进了状态指纹或并列判据。
    /// </summary>
    private static readonly string TitleSalt =
        Environment.GetEnvironmentVariable("OFFLINE_HARNESS_TITLE_SALT") ?? string.Empty;

    private static bool LocRawTextPrefix(LocString __instance, ref string __result)
    {
        __result = (__instance.LocEntryKey ?? string.Empty) + TitleSalt;
        return false;
    }

    private static bool LocFormattedTextPrefix(LocString __instance, ref string __result)
    {
        __result = __instance.LocEntryKey ?? string.Empty;
        return false;
    }

    private static bool LocExistsPrefix(ref bool __result)
    {
        __result = true;
        return false;
    }

    private static bool CompletedTaskPrefix(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }

    private static bool NullResultPrefix(ref object? __result)
    {
        __result = null;
        return false;
    }

}

internal static class HarnessLog
{
    public static bool VerboseGameLog { get; set; }
    public static bool TraceInit { get; set; } = true;
    public static string Language { get; set; } = "eng";

    /// <summary>
    /// 段错误（跳转到地址 0 的 Godot 原生入口）杀进程时托管栈就没了，所以每一步都打一行带 flush 的
    /// 标记：最后一行标记就是崩在哪一步。
    /// </summary>
    public static void Trace(string stage)
    {
        if (!TraceInit)
            return;
        Console.WriteLine($"[trace] {stage}");
        Console.Out.Flush();
    }
}
