using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
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

    public static IReadOnlyList<string> Bypasses => _bypasses;
    public static Harmony Harmony => _harmony ?? throw new InvalidOperationException("Harmony 未初始化。");

    /// <summary>第一步：只装绕过补丁，必须在任何游戏代码跑起来之前。</summary>
    public static void ApplyGodotBypasses()
    {
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

        // 8) CaptureSearchPolicy 用显示服务器的名字决定要不要开帧压力恢复。离线没有显示服务器，
        //    走模组自己留的注入口给固定值：与游戏内 --headless 取到的一致，恢复逻辑照样关闭。
        CombatSolver.SolverController.DisplayServerNameProvider = static () => "headless";
        _bypasses.Add("SolverController.DisplayServerNameProvider -> \"headless\"（不启动显示服务器；与游戏内 --headless 同值）");
    }

    /// <summary>第二步：游戏静态状态。顺序照 OneTimeInitialization.ExecuteEssential。</summary>
    private static void Trace(string stage) => HarnessLog.Trace(stage);

    public static string InitializeStaticState()
    {
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

    private static void Patch(
        Type type,
        string method,
        string prefix,
        string note,
        Type[]? args = null)
    {
        MethodInfo target = args == null
            ? AccessTools.Method(type, method)
                ?? throw new MissingMethodException(type.FullName, method)
            : AccessTools.Method(type, method, args)
                ?? throw new MissingMethodException(type.FullName, method);
        MethodInfo replacement = typeof(GameBootstrap).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)!;
        Harmony.Patch(target, prefix: new HarmonyMethod(replacement));
        _bypasses.Add(note);
    }

    private static bool SkipPrefix() => false;

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
