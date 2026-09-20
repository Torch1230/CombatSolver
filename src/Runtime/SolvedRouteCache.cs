using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace CombatSolver;

/// <summary>Persists value-only plans independently of a live combat's lifetime.</summary>
internal sealed class SolvedRouteCache(string path)
{
    private const int MaximumEntries = 64;
    public string Path { get; } = path;

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
        byte[] identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Solver = typeof(SolvedRouteCache).Module.ModuleVersionId,
            Game = typeof(CombatState).Module.ModuleVersionId,
            Mods = ModManager.Mods.Select(mod => new
            {
                Id = mod.manifest?.id,
                Version = mod.version?.ToString(),
                mod.state,
                Assemblies = mod.assemblies.Select(assembly => assembly.ManifestModule.ModuleVersionId).ToArray(),
            }).ToArray(),
            Seed = state.RunState.Rng.StringSeed,
            state.RunState.CurrentActIndex,
            state.RunState.TotalFloor,
            state.RunState.AscensionLevel,
            Encounter = state.Encounter?.Id.Entry,
            Modifiers = state.Modifiers.Select(modifier => modifier.Id.Entry).ToArray(),
            Native = writer.Buffer.AsSpan(0, writer.BytePosition).ToArray(),
            root.ContinuationStamp.StateText,
            damage,
            policy.Profile,
            policy.PotionPolicy,
            policy.PotionStrategy.Directives,
            policy.GrowthBudgets,
            policy.RelicTargets,
            policy.Act3BossStrategy,
            policy.BrightestFlameMaxHpLossLimit,
            policy.GrowthOpportunityTargets,
            policy.IgnoreLongTermRewards,
            policy.IncludeTurnSetup,
            policy.TheftPolicy,
            policy.ActTransitionBossHpStrategy,
            policy.FinalBossHpStrategy,
            policy.AcceptableBattleHpLoss,
            policy.StopAtAcceptableBattleHpLoss,
            policy.UseBeamWidthPortfolio,
            policy.UseNoveltyPortfolio,
            policy.PredictPotionReward,
            policy.NoveltyBudget,
            policy.BeamWidthPortfolioWidths,
            policy.BeamWidthPortfolioPlainBaselineMember,
            policy.PileOrderInvariantMask,
            policy.StateKeySalt,
            policy.TranspositionPruningDisabledMask,
            policy.MemoryNoProgressRecoveryLimit,
            PortfolioSelector = policy.PortfolioExperiment?.Model?.ModelId,
            policy.FixedBudget,
            policy.BudgetOverrideMilliseconds,
        });
        string key = Convert.ToHexString(SHA256.HashData(identity));
        return new SolvedRouteCache(System.IO.Path.Combine(
            ProjectSettings.GlobalizePath("user://combat-solver-routes"), key + ".json"));
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
        => JsonSerializer.SerializeToUtf8Bytes(result, Options(result.Forecast));

    internal static SolverResult DeserializeRoute(ReadOnlySpan<byte> bytes, IntentForecast currentForecast)
        => JsonSerializer.Deserialize<SolverResult>(bytes, Options(currentForecast))
           ?? throw new InvalidDataException("录像包中的预计算路线为空。");

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
