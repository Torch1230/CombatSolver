using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using CombatSolver.Api;

namespace CombatSolver;

internal static class CombatShowcaseRuntime
{
    private const long MaxExpandedBundleBytes = 32L * 1024 * 1024;
    private static readonly Action TerminalRewardsOverride = OnTerminalRewardsProceed;
    private static int _importing;
    private static int _showcaseRunActive;
    private static int _returningToMainMenu;
    internal static bool ImportInProgress => Volatile.Read(ref _importing) != 0;

    internal static CombatShowcaseCompatibility GetCompatibility()
        => new(
            CombatShowcaseCollector.ProtocolVersion,
            typeof(CombatState).Assembly.GetName().Version?.ToString()
                ?? throw new InvalidDataException("游戏程序集缺少版本号。"),
            typeof(Entry).Assembly.GetName().Version?.ToString(3)
                ?? throw new InvalidDataException("CombatSolver 程序集缺少版本号。"));

    internal static async Task<CombatShowcaseEnterResult> EnterAsync(string bundlePath)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("录像对局必须从游戏主线程进入。");
        if (Interlocked.CompareExchange(ref _importing, 1, 0) != 0)
            throw new InvalidOperationException("已有录像对局正在导入。");
        ValidatedBundle? bundle = null;
        try
        {
            if (RunManager.Instance.IsInProgress || CombatManager.Instance.IsInProgress)
                throw new InvalidOperationException("只能从主菜单进入录像对局。");
            bundle = ValidateAndExtract(bundlePath);
            using JsonDocument metadataDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(bundle.MetadataPath));
            JsonElement metadata = metadataDocument.RootElement;
            CombatShowcaseCompatibility compatibility = GetCompatibility();
            int protocol = metadata.GetProperty("routeProtocolVersion").GetInt32();
            string gameVersion = RequiredString(metadata, "gameVersion");
            if (protocol != compatibility.RouteProtocolVersion || gameVersion != compatibility.GameVersion)
                throw new InvalidDataException($"录像包与当前游戏不兼容：协议 {protocol}/{compatibility.RouteProtocolVersion}，游戏 {gameVersion}/{compatibility.GameVersion}。");

            SerializableRun save = JsonSerializer.Deserialize(
                await File.ReadAllBytesAsync(bundle.RunStatePath),
                JsonSerializationUtility.GetTypeInfo<SerializableRun>())
                ?? throw new InvalidDataException("录像包的跑局快照为空。");
            RunState run = RunState.FromSerializable(save);
            NGame host = NGame.Instance ?? throw new InvalidOperationException("游戏主节点不存在。");
            await RunManager.Instance.SetUpSavedSingleplayer(run, save);
            RunManager.Instance.ShouldSave = false;
            RunManager.Instance.CombatReplayWriter.IsEnabled = false;
            NAudioManager.Instance?.StopMusic();
            SfxCmd.Play(run.Players[0].Character.CharacterTransitionSfx);
            await host.Transition.FadeOut(
                0.8f,
                run.Players[0].Character.CharacterSelectTransitionPath);
            host.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            await PreloadManager.LoadRunAssets(run.Players.Select(static player => player.Character));
            await PreloadManager.LoadActAssets(run.Act);
            RunManager.Instance.Launch();
            host.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();

            string encounterId = RequiredString(metadata, "encounterId");
            EncounterModel encounter = ModelDb.All.OfType<EncounterModel>()
                .Single(candidate => string.Equals(candidate.Id.Entry, encounterId, StringComparison.Ordinal));
            await RunManager.Instance.EnterRoomDebug(RoomType.Boss, MapPointType.Boss, encounter.ToMutable());
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            CombatState state = CombatManager.Instance.DebugOnlyGetState()
                ?? throw new InvalidOperationException("录像对局没有创建战斗状态。");
            long playDeadline = System.Environment.TickCount64 + 30_000;
            while (LocalContext.GetMe(state)?.PlayerCombatState?.Phase.ToString() != "Play")
            {
                if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding)
                    throw new InvalidOperationException("录像对局未进入可出牌状态。");
                if (System.Environment.TickCount64 >= playDeadline)
                    throw new TimeoutException("录像对局 30 秒内没有进入可出牌状态。");
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            Player player = LocalContext.GetMe(state)
                ?? throw new InvalidOperationException("录像对局没有本地玩家。");
            await UnattendedTestRunner.ApplyReplayStateAsync(
                state,
                player,
                bundle.ReplayStatePath,
                bundle.RunStatePath,
                nativeStatePath: null);
            bool nativeStateVerified = CombatShowcaseNativeState.AssertMatches(
                state,
                bundle.NativeStatePath);
            if (!nativeStateVerified)
            {
                Entry.Logger.Warn(
                    "[CombatSolver/Showcase] LEGACY_NATIVE_STATE comparison=exact_replay_state " +
                    "reason=patched_packet_format");
            }
            CombatBugReportExporter.ResetOutcomeAtRestoredRoot(state);

            using JsonDocument routeDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(bundle.RoutePath));
            JsonElement route = routeDocument.RootElement;
            byte[] serializedResult = Convert.FromBase64String(RequiredString(route, "solverResult"));
            IntentForecast forecast = CombatRootSnapshot.Capture(state).Forecast;
            SolverResult result = SolvedRouteCache.DeserializeRoute(serializedResult, forecast);
            if (result.StartTurnNumber != route.GetProperty("startTurnNumber").GetInt32()
                || result.CombatEndedTurn != route.GetProperty("combatEndedTurn").GetInt32()
                || !result.Snapshot.AllEnemiesDead)
                throw new InvalidDataException("录像包路线摘要与预计算结果不一致。");
            int before = SolverController.SearchesStartedForShowcase;
            SolverController.AcceptShowcaseRoute(host, state, result);
            int localSearchStarts = SolverController.SearchesStartedForShowcase - before;
            if (localSearchStarts != 0)
                throw new InvalidOperationException("导入录像路线时意外启动了本地搜索。");
            BeginShowcaseRun();
            await host.Transition.FadeIn();
            return new CombatShowcaseEnterResult(
                RequiredString(metadata, "bundleId"),
                RequiredString(metadata, "characterId"),
                encounterId,
                route.GetProperty("combatEndedTurn").GetInt32(),
                route.GetProperty("turnCount").GetInt32(),
                localSearchStarts);
        }
        catch (Exception importError)
        {
            if (RunManager.Instance.IsInProgress && NGame.Instance is { } host)
            {
                try
                {
                    await host.ReturnToMainMenu();
                }
                catch (Exception cleanupError)
                {
                    Entry.Logger.Error(
                        $"[CombatSolver/Showcase] IMPORT_FAILED error={importError} cleanup_error={cleanupError}");
                    throw new InvalidOperationException(
                        $"录像对局导入失败：{importError.Message}；返回主菜单清理失败：{cleanupError.Message}",
                        new AggregateException(importError, cleanupError));
                }
            }
            throw;
        }
        finally
        {
            if (bundle is not null)
                DeleteImportDirectory(bundle.Directory);
            Volatile.Write(ref _importing, 0);
        }
    }

    private static ValidatedBundle ValidateAndExtract(string bundlePath)
    {
        FileInfo source = new(bundlePath);
        if (!source.Exists || source.Length is <= 0 or > 8L * 1024 * 1024)
            throw new InvalidDataException("录像包不存在或超过 8 MiB 上限。");
        using ZipArchive archive = ZipFile.OpenRead(source.FullName);
        string[] expected = ["showcase.json", "run-state.save", "replay-state.json", "native-state.bin", "route.json"];
        string[] actual = archive.Entries.Select(static entry => entry.FullName).OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected.OrderBy(static name => name, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("录像包必须且只能包含五个协议文件。");
        long expandedBytes = archive.Entries.Sum(static entry => entry.Length);
        if (expandedBytes is <= 0 or > MaxExpandedBundleBytes)
            throw new InvalidDataException("录像包解压后为空或超过 32 MiB 上限。");
        ZipArchiveEntry metadataEntry = archive.GetEntry("showcase.json")!;
        using JsonDocument document = JsonDocument.Parse(metadataEntry.Open());
        JsonElement fileManifest = document.RootElement.GetProperty("files");
        string directory = Path.Combine(GetImportRoot(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string name in expected)
            {
                ZipArchiveEntry entry = archive.GetEntry(name)!;
                string destination = Path.Combine(directory, name);
                using (Stream input = entry.Open())
                using (FileStream output = File.Create(destination))
                    input.CopyTo(output);
                if (name == "showcase.json")
                    continue;
                JsonElement descriptor = fileManifest.GetProperty(name);
                byte[] bytes = File.ReadAllBytes(destination);
                if (bytes.LongLength != descriptor.GetProperty("sizeBytes").GetInt64()
                    || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(
                        RequiredString(descriptor, "sha256"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"录像包文件校验失败：{name}。");
            }
            return new ValidatedBundle(
                directory,
                Path.Combine(directory, "showcase.json"),
                Path.Combine(directory, "run-state.save"),
                Path.Combine(directory, "replay-state.json"),
                Path.Combine(directory, "native-state.bin"),
                Path.Combine(directory, "route.json"));
        }
        catch
        {
            DeleteImportDirectory(directory);
            throw;
        }
    }

    private static string GetImportRoot()
        => Path.GetFullPath(ProjectSettings.GlobalizePath("user://combat-solver-showcases/import"));

    private static void DeleteImportDirectory(string directory)
    {
        string root = GetImportRoot().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝清理录像导入根目录之外的路径。");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static string RequiredString(JsonElement element, string name)
        => element.GetProperty(name).GetString()
           ?? throw new InvalidDataException($"录像包字段 {name} 为空。");

    private static void BeginShowcaseRun()
    {
        if (RunManager.Instance.debugAfterCombatRewardsOverride is { } existing
            && !ReferenceEquals(existing, TerminalRewardsOverride))
        {
            throw new InvalidOperationException("原生终端奖励流程已有其他覆盖入口。");
        }
        RunManager.Instance.debugAfterCombatRewardsOverride = TerminalRewardsOverride;
        Volatile.Write(ref _showcaseRunActive, 1);
        Volatile.Write(ref _returningToMainMenu, 0);
    }

    private static void OnTerminalRewardsProceed()
    {
        if (Volatile.Read(ref _showcaseRunActive) == 0)
            throw new InvalidOperationException("录像对局终端返回入口在非录像跑局中被调用。");
        if (Interlocked.CompareExchange(ref _returningToMainMenu, 1, 0) != 0)
            return;
        NGame host = NGame.Instance ?? throw new InvalidOperationException("游戏主节点不存在。");
        Entry.Logger.Info("[CombatSolver/Showcase] TERMINAL_PROCEED destination=main_menu transition=native");
        TaskHelper.RunSafely(host.ReturnToMainMenuAfterRun());
    }

    internal static void EndShowcaseRun()
    {
        if (ReferenceEquals(RunManager.Instance.debugAfterCombatRewardsOverride, TerminalRewardsOverride))
            RunManager.Instance.debugAfterCombatRewardsOverride = null;
        Volatile.Write(ref _showcaseRunActive, 0);
        Volatile.Write(ref _returningToMainMenu, 0);
    }

    private sealed record ValidatedBundle(
        string Directory,
        string MetadataPath,
        string RunStatePath,
        string ReplayStatePath,
        string NativeStatePath,
        string RoutePath);
}

internal sealed class CombatShowcaseSaveIsolationPatch : STS2RitsuLib.Patching.Models.IPatchMethod
{
    public static string PatchId => "combat_solver_showcase_save_isolation";
    public static string Description => "录像临时跑局不递增正式存档的重载计数";
    public static STS2RitsuLib.Patching.Models.ModPatchTarget[] GetTargets()
        => [new(typeof(SaveManager), nameof(SaveManager.IncrementNumReloads),
            [typeof(SerializableRun), typeof(MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType), typeof(bool)])];

    public static bool Prefix(SerializableRun save, ref Task __result)
    {
        if (!CombatShowcaseRuntime.ImportInProgress)
            return true;
        save.NumReloads++;
        __result = Task.CompletedTask;
        return false;
    }
}

internal sealed class CombatShowcaseCleanupPatch : STS2RitsuLib.Patching.Models.IPatchMethod
{
    public static string PatchId => "combat_solver_showcase_cleanup";
    public static string Description => "录像临时跑局清理终端返回入口";
    public static STS2RitsuLib.Patching.Models.ModPatchTarget[] GetTargets()
        => [new(typeof(RunManager), nameof(RunManager.CleanUp), [typeof(bool)])];

    public static void Postfix()
        => CombatShowcaseRuntime.EndShowcaseRun();
}
