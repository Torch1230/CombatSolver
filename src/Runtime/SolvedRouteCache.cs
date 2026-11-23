using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CombatSolver.HotKernel;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

/// <summary>Persists value-only plans independently of a live combat's lifetime.</summary>
internal sealed class SolvedRouteCache
{
    private const int MaximumEntries = 64;
    private readonly Lazy<string> _path;

    internal SolvedRouteCache(string path)
    {
        _path = new Lazy<string>(path);
    }

    private SolvedRouteCache(Func<string> pathFactory)
    {
        _path = new Lazy<string>(pathFactory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Path => _path.Value;

    private sealed record ModIdentity(
        string? Id,
        string? Version,
        ModLoadState state,
        Guid[] Assemblies);

    // Property names, casing, and declaration order must match the original anonymous
    // cache-identity object exactly so persisted route cache filenames remain compatible.
    private sealed record CacheIdentityPayload(
        int Schema,
        Guid Solver,
        Guid Game,
        ModIdentity[] Mods,
        string Seed,
        int CurrentActIndex,
        int TotalFloor,
        int AscensionLevel,
        HotCombatRulesSnapshot Rules,
        string? Encounter,
        string[] Modifiers,
        byte[] Native,
        string StateText,
        BattleDamageSnapshot damage,
        SolverSearchProfile Profile,
        SolverPotionPolicy PotionPolicy,
        IReadOnlyList<PotionSlotDirective> Directives,
        IReadOnlyList<KeyValuePair<string, int>> PotionThresholdOverrides,
        GrowthValues GrowthBudgets,
        IReadOnlyList<RelicCounterTarget> RelicTargets,
        bool Act3BossStrategy,
        int? BrightestFlameMaxHpLossLimit,
        GrowthOpportunityTargets GrowthOpportunityTargets,
        bool IgnoreLongTermRewards,
        bool IncludeTurnSetup,
        SolverTheftPolicy? TheftPolicy,
        BossHpStrategy ActTransitionBossHpStrategy,
        BossHpStrategy FinalBossHpStrategy,
        int AcceptableBattleHpLoss,
        bool StopAtAcceptableBattleHpLoss,
        bool PredictPotionReward,
        int TranspositionPruningDisabledMask,
        bool FixedBudget,
        int? BudgetOverrideMilliseconds);

    internal static void WarmUpSerializer()
    {
        CacheIdentityPayload dummy = new(
            Schema: 1,
            Solver: typeof(SolvedRouteCache).Module.ModuleVersionId,
            Game: typeof(CombatState).Module.ModuleVersionId,
            Mods: Array.Empty<ModIdentity>(),
            Seed: string.Empty,
            CurrentActIndex: 0,
            TotalFloor: 0,
            AscensionLevel: 0,
            Rules: new HotCombatRulesSnapshot(default, 0, 0, null),
            Encounter: null,
            Modifiers: Array.Empty<string>(),
            Native: Array.Empty<byte>(),
            StateText: string.Empty,
            damage: new BattleDamageSnapshot(0, 0, 0, Array.Empty<string>(), 0),
            Profile: SolverSearchProfile.Default,
            PotionPolicy: SolverPotionPolicy.Smart,
            Directives: Array.Empty<PotionSlotDirective>(),
            PotionThresholdOverrides: Array.Empty<KeyValuePair<string, int>>(),
            GrowthBudgets: default,
            RelicTargets: Array.Empty<RelicCounterTarget>(),
            Act3BossStrategy: false,
            BrightestFlameMaxHpLossLimit: null,
            GrowthOpportunityTargets: GrowthOpportunityTargets.Empty,
            IgnoreLongTermRewards: false,
            IncludeTurnSetup: false,
            TheftPolicy: null,
            ActTransitionBossHpStrategy: BossHpStrategy.ProgressionFirst,
            FinalBossHpStrategy: BossHpStrategy.ProgressionFirst,
            AcceptableBattleHpLoss: 0,
            StopAtAcceptableBattleHpLoss: true,
            PredictPotionReward: false,
            TranspositionPruningDisabledMask: 0,
            FixedBudget: false,
            BudgetOverrideMilliseconds: null);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(dummy);
        _ = SHA256.HashData(bytes);
    }

    public static SolvedRouteCache Capture(
        CombatState state,
        CombatRootSnapshot root,
        SearchPolicySnapshot policy,
        BattleDamageSnapshot damage)
    {
        PacketWriter writer = new() { WarnOnGrow = false };
        NetFullCombatState native = NetFullCombatState.FromRun(state.RunState, justFinishedAction: null);
        // These network sequencing counters change on reload without changing combat.
        native.nextChoiceIds.Clear();
        native.nextRewardIds.Clear();
        native.Serialize(writer);
        foreach (var player in state.Players)
            NetFullCombatState.CombatPileState.From(player.Deck).Serialize(writer);
        writer.ZeroByteRemainder();

        // Freeze all live-state and Godot-API fields on the main thread, then defer
        // JSON serialization + SHA256 hashing until Path is first read on the worker thread.
        string cacheDirectory = ProjectSettings.GlobalizePath("user://combat-solver-routes");
        CacheIdentityPayload payload = new(
            Schema: 1,
            Solver: typeof(SolvedRouteCache).Module.ModuleVersionId,
            Game: typeof(CombatState).Module.ModuleVersionId,
            Mods: ModManager.Mods.Select(mod => new ModIdentity(
                mod.manifest?.id,
                mod.version?.ToString(),
                mod.state,
                mod.assemblies.Select(assembly => assembly.ManifestModule.ModuleVersionId).ToArray())).ToArray(),
            Seed: state.RunState.Rng.StringSeed,
            CurrentActIndex: state.RunState.CurrentActIndex,
            TotalFloor: state.RunState.TotalFloor,
            AscensionLevel: state.RunState.AscensionLevel,
            Rules: root.Rules,
            Encounter: state.Encounter?.Id.Entry,
            Modifiers: state.Modifiers.Select(modifier => modifier.Id.Entry).ToArray(),
            Native: writer.Buffer.AsSpan(0, writer.BytePosition).ToArray(),
            StateText: root.ContinuationStamp.StateText,
            damage: damage,
            Profile: policy.Profile,
            PotionPolicy: policy.PotionPolicy,
            Directives: policy.PotionStrategy.Directives,
            PotionThresholdOverrides: policy.PotionStrategy.Thresholds.Overrides,
            GrowthBudgets: policy.GrowthBudgets,
            RelicTargets: policy.RelicTargets,
            Act3BossStrategy: policy.Act3BossStrategy,
            BrightestFlameMaxHpLossLimit: policy.BrightestFlameMaxHpLossLimit,
            GrowthOpportunityTargets: policy.GrowthOpportunityTargets,
            IgnoreLongTermRewards: policy.IgnoreLongTermRewards,
            IncludeTurnSetup: policy.IncludeTurnSetup,
            TheftPolicy: policy.TheftPolicy,
            ActTransitionBossHpStrategy: policy.ActTransitionBossHpStrategy,
            FinalBossHpStrategy: policy.FinalBossHpStrategy,
            AcceptableBattleHpLoss: policy.AcceptableBattleHpLoss,
            StopAtAcceptableBattleHpLoss: policy.StopAtAcceptableBattleHpLoss,
            PredictPotionReward: policy.PredictPotionReward,
            TranspositionPruningDisabledMask: policy.TranspositionPruningDisabledMask,
            FixedBudget: policy.FixedBudget,
            BudgetOverrideMilliseconds: policy.BudgetOverrideMilliseconds);

        return new SolvedRouteCache(() =>
        {
            byte[] identity = JsonSerializer.SerializeToUtf8Bytes(payload);
            string key = Convert.ToHexString(SHA256.HashData(identity));
            return System.IO.Path.Combine(cacheDirectory, key + ".json");
        });
    }

    // 缓存是可选功能：读不出来按未命中处理，坏文件改名留作诊断，搜索照常进行。
    public SolverResult? Read(IntentForecast currentForecast)
        => AncillaryWork.Try("READ_FAILED", () => ReadCore(currentForecast), Log);

    private SolverResult? ReadCore(IntentForecast currentForecast)
    {
        if (!File.Exists(Path))
            return null;
        try
        {
            using FileStream stream = File.OpenRead(Path);
            SolverResult result = JsonSerializer.Deserialize<SolverResult>(stream, Options(currentForecast))
                ?? throw new InvalidDataException($"Empty solved route: {Path}");
            if (!result.TryValidateTurnOutcomes(out string? failure))
            {
                Log($"INCOMPLETE_TURN_OUTCOMES path={Path} reason={failure}");
                return null;
            }
            result.WasRestoredFromCache = true;
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            // 只隔离内容损坏的文件；被占用、无权限等 IO 失败下次可能恢复，不动原文件。
            AncillaryWork.Run("QUARANTINE_FAILED", () => File.Move(Path, Path + QuarantineSuffix, overwrite: true), Log);
            throw;
        }
    }

    internal static byte[] SerializeRoute(SolverResult result)
    {
        result.AssertCompleteTurnOutcomes();
        return JsonSerializer.SerializeToUtf8Bytes(result, Options(result.Forecast));
    }

    internal static SolverResult DeserializeRoute(ReadOnlySpan<byte> bytes, IntentForecast currentForecast)
    {
        SolverResult result = JsonSerializer.Deserialize<SolverResult>(bytes, Options(currentForecast))
            ?? throw new InvalidDataException("录像包中的预计算路线为空。");
        if (!result.TryValidateTurnOutcomes(out string? failure))
            throw new InvalidDataException($"录像路线回合统计不完整：{failure}。");
        return result;
    }

    // 写入或清理失败只损失这条缓存，已经算出的路线照常交付。
    public void StoreFirst(SolverResult result)
        => AncillaryWork.Run("STORE_FAILED", () => StoreFirstCore(result), Log);

    private void StoreFirstCore(SolverResult result)
    {
        if (result.WasRestoredFromCache
            || result.ResultScope == SolverResultScope.CurrentTurnAdoption
            || File.Exists(Path))
            return;
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        byte[] bytes = SerializeRoute(result);
        string temporary = Path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, Path);
        }
        finally
        {
            AncillaryWork.Run("TEMP_CLEANUP_FAILED", () => File.Delete(temporary), Log);
        }
        DirectoryInfo info = new(directory);
        foreach (FileInfo obsolete in info.GetFiles("*.json")
                     .OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaximumEntries)
                     .Concat(info.GetFiles("*" + QuarantineSuffix)
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaximumQuarantinedEntries)))
            AncillaryWork.Run("CLEANUP_FAILED", obsolete.Delete, Log);
    }

    private const string QuarantineSuffix = ".bad";
    private const int MaximumQuarantinedEntries = 8;

    private static void Log(string message)
        => Entry.Logger.Warn($"[CombatSolver/RouteCache] {message}");

    private static JsonSerializerOptions Options(IntentForecast forecast)
    {
        DefaultJsonTypeInfoResolver resolver = new();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(SolverResult))
                return;
            foreach (JsonPropertyInfo property in info.Properties)
            {
                PropertyInfo member = typeof(SolverResult).GetProperty(property.Name)!;
                if (member.GetSetMethod(nonPublic: true) is { } setter)
                    property.Set = (instance, value) => setter.Invoke(instance, [value]);
            }
        });
        JsonSerializerOptions options = new() { TypeInfoResolver = resolver };
        options.Converters.Add(new ForecastReferenceConverter(forecast));
        return options;
    }

    // Forecast contains live Creature/MoveState references. The exact root supplies a
    // newly captured forecast on reload; those objects never enter the cache file.
    private sealed class ForecastReferenceConverter(IntentForecast forecast) : JsonConverter<IntentForecast>
    {
        public override IntentForecast Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.True)
                throw new JsonException("Expected captured-root forecast reference.");
            return forecast;
        }

        public override void Write(Utf8JsonWriter writer, IntentForecast value, JsonSerializerOptions options)
            => writer.WriteBooleanValue(true);
    }
}
