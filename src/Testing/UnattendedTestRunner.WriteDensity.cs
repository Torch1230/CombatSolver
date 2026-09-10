using CombatSolver.Engine.Common;
using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Observation only: net changes at stable boundaries are not write counts, byte density,
    // transient journal traffic, or a compact representation benchmark.
    private readonly List<object> _writeDensitySamples = [];

    private JsonElement? CaptureWriteDensity(CombatPredictionSimulator simulator, Player player, Creature enemy)
    {
        if (Environment.GetEnvironmentVariable("COMBATSOLVER_WRITE_DENSITY") != "1") return null;
        var rng = simulator.Rng;
        return JsonSerializer.SerializeToElement(new
        {
            state = CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState, player, enemy),
            exactRng = new { shuffle = rng.Shuffle.CaptureState(), cardGeneration = rng.CombatCardGeneration.CaptureState(),
                potionGeneration = rng.CombatPotionGeneration.CaptureState(), cardSelection = rng.CombatCardSelection.CaptureState(),
                energyCosts = rng.CombatEnergyCosts.CaptureState(), targets = rng.CombatTargets.CaptureState(),
                orbs = rng.CombatOrbGeneration.CaptureState(), monsterAi = rng.MonsterAi.CaptureState(), niche = rng.Niche.CaptureState() }
        });
    }

    private void RecordWriteDensity(string phase, JsonElement? before,
        CombatPredictionSimulator simulator, Player player, Creature enemy)
    {
        if (before is null) return;
        JsonElement after = CaptureWriteDensity(simulator, player, enemy)!.Value;
        static Dictionary<string, JsonElement> Flatten(JsonElement root)
        {
            Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
            void Walk(JsonElement item, string path)
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in item.EnumerateObject()) Walk(property.Value, path + "/" + property.Name);
                }
                else if (item.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var child in item.EnumerateArray()) Walk(child, path + "/" + i++);
                }
                else result.Add(path, item);
            }
            Walk(root, "");
            return result;
        }
        var left = Flatten(before.Value);
        var right = Flatten(after);
        string[] paths = left.Keys.Union(right.Keys).Order(StringComparer.Ordinal).ToArray();
        var changed = paths.Where(path => !left.TryGetValue(path, out var a)
            || !right.TryGetValue(path, out var b) || a.GetRawText() != b.GetRawText())
            .Select(path => new { path, before = left.TryGetValue(path, out var a) ? (JsonElement?)a : null,
                after = right.TryGetValue(path, out var b) ? (JsonElement?)b : null }).ToArray();
        _writeDensitySamples.Add(new { phase, beforeLeaves = left.Count, afterLeaves = right.Count,
            unionLeaves = paths.Length, changedLeaves = changed.Length, changed });
        if (string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
            throw new InvalidOperationException("Write density diagnostics require an evidence directory.");
        Directory.CreateDirectory(_request.EvidenceDirectory);
        File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "semantic-net-changes.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, scenario = _request.ScenarioId,
                scope = "native-verified legacy simulation; net observable field changes, not mutation counts or compact slot density",
                encoding = "JSON scalar leaves; packed state strings count as one field; exact nine RNG states are expanded separately",
                samples = _writeDensitySamples }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
