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
            policy.HasGrowthTargets,
            policy.IgnoreLongTermRewards,
            policy.IncludeTurnSetup,
            policy.TheftPolicy,
            policy.ActTransitionBossHpStrategy,
            policy.FinalBossHpStrategy,
            policy.AcceptableBattleHpLoss,
            policy.StopAtAcceptableBattleHpLoss,
            policy.FatalGrowthTarget,
            policy.FixedBudget,
            policy.BudgetOverrideMilliseconds,
        });
        string key = Convert.ToHexString(SHA256.HashData(identity));
        return new SolvedRouteCache(System.IO.Path.Combine(
            ProjectSettings.GlobalizePath("user://combat-solver-routes"), key + ".json"));
    }

    public SolverResult? Read(IntentForecast currentForecast)
    {
        if (!File.Exists(Path))
            return null;
        using FileStream stream = File.OpenRead(Path);
        SolverResult result = JsonSerializer.Deserialize<SolverResult>(stream, Options(currentForecast))
            ?? throw new InvalidDataException($"Empty solved route: {Path}");
        result.WasRestoredFromCache = true;
        return result;
    }

    public void StoreFirst(SolverResult result)
    {
        if (result.WasRestoredFromCache
            || result.ResultScope == SolverResultScope.CurrentTurnAdoption
            || File.Exists(Path))
            return;
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result, Options(result.Forecast));
        string temporary = Path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, Path);
        foreach (FileInfo obsolete in new DirectoryInfo(directory).GetFiles("*.json")
                     .OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaximumEntries))
            obsolete.Delete();
    }

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
