using System.Text.Json;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// Optional, immutable correction to intermediate Beam ranking. This is an estimate,
/// never an admissible bound; final policy and exact dominance do not consume it.
/// Loading/validation belongs to the caller before Solve, not expansion workers.
/// </summary>
internal sealed class ContextualRankingModel
{
    internal const int FeatureCount = 28;
    internal static IReadOnlyList<string> FeatureNames { get; } = Array.AsReadOnly(new[]
    {
        "energy", "hp_pressure", "incoming_fraction", "enemy_hp", "hand", "free_plays",
        "reachable_hand", "persistent", "setup", "retained_attack", "replay", "future_resource",
        "delayed_damage", "clutter_fraction", "strength_suppression", "weak_turns", "vulnerable_turns",
        "deck_size", "turn", "energy_x_pressure", "energy_x_incoming", "hand_x_energy",
        "persistent_x_pressure", "setup_x_pressure", "future_x_pressure",
        "delayed_x_enemy_hp", "reachable_x_incoming", "free_x_incoming",
    });

    internal sealed record Document(int SchemaVersion, string SolverAssemblyId, string GameAssemblyId,
        string[] FeatureNames, double[] Weights, double[] Minimum, double[] Maximum,
        double MaximumAdjustmentHp, string ModelId);

    private readonly Document _document;
    public string ModelId => _document.ModelId;

    private ContextualRankingModel(Document document) => _document = document;

    internal static ContextualRankingModel Parse(string json)
    {
        Document document = JsonSerializer.Deserialize<Document>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty contextual ranking model.");
        if (document.SchemaVersion != 1 || document.FeatureNames == null
            || !document.FeatureNames.SequenceEqual(FeatureNames)
            || document.Weights?.Length != FeatureCount || document.Minimum?.Length != FeatureCount
            || document.Maximum?.Length != FeatureCount || string.IsNullOrWhiteSpace(document.ModelId)
            || !double.IsFinite(document.MaximumAdjustmentHp) || document.MaximumAdjustmentHp is <= 0 or > 8)
            throw new InvalidDataException("Invalid contextual ranking model schema.");
        if (document.SolverAssemblyId != typeof(ContextualRankingModel).Assembly.ManifestModule.ModuleVersionId.ToString()
            || document.GameAssemblyId != typeof(CardModel).Assembly.ManifestModule.ModuleVersionId.ToString())
            throw new InvalidDataException("Contextual ranking model assembly mismatch.");
        for (int i = 0; i < FeatureCount; i++)
            if (!double.IsFinite(document.Weights[i]) || Math.Abs(document.Weights[i]) > 100
                || !double.IsFinite(document.Minimum[i]) || !double.IsFinite(document.Maximum[i])
                || document.Minimum[i] > document.Maximum[i])
                throw new InvalidDataException("Invalid contextual ranking coefficient or range.");
        return new ContextualRankingModel(document);
    }

    internal double Adjustment(SearchNode node)
    {
        if (node.IsTerminal || node.Snapshot.ProjectedPlayerHp <= 0)
            return 0;
        Span<double> features = stackalloc double[FeatureCount];
        Capture(node, features);
        double value = 0;
        for (int i = 0; i < features.Length; i++)
        {
            double feature = features[i];
            if (feature < _document.Minimum[i] || feature > _document.Maximum[i])
                return 0; // Unsupported context keeps the original heuristic.
            value += feature * _document.Weights[i];
        }
        return Math.Clamp(value, -_document.MaximumAdjustmentHp, _document.MaximumAdjustmentHp) * SolverWeights.Hp;
    }

    internal static void Capture(SearchNode node, Span<double> values)
    {
        if (values.Length != FeatureCount)
            throw new ArgumentException("Wrong contextual ranking feature buffer size.", nameof(values));
        SimulationSnapshot s = node.Snapshot;
        static double Cap(double value, double scale) => Math.Clamp(value / scale, 0, 4);
        double energy = Cap(s.Energy, 6);
        double pressure = 1 - Math.Clamp((double)s.PlayerHp / Math.Max(1, s.PlayerMaxHp), 0, 1);
        double incoming = Math.Clamp((double)(s.PlayerHp - s.ProjectedPlayerHp) / Math.Max(1, s.PlayerHp), 0, 1);
        double enemyHp = Cap(s.EnemyHp, 200);
        double hand = Cap(s.HandCount, 10);
        double free = Cap(s.ZeroCostPlayableCount, 10);
        double reachable = Cap(s.ReachableHandValue, 50);
        double persistent = Cap(s.PersistentBuffValue, 128);
        double setup = Cap(s.LatentSetupValue, 64);
        double future = Cap(s.FutureResourceValue, 64);
        double delayed = Cap(s.DelayedDamageValue, 100);
        values[0] = energy;
        values[1] = pressure;
        values[2] = incoming;
        values[3] = enemyHp;
        values[4] = hand;
        values[5] = free;
        values[6] = reachable;
        values[7] = persistent;
        values[8] = setup;
        values[9] = Cap(s.RetainedAttackValue, 128);
        values[10] = Cap(s.ReplayPotentialValue, 64);
        values[11] = future;
        values[12] = delayed;
        values[13] = Math.Clamp((double)s.LiveDeckClutter / Math.Max(1, s.LiveDeckSize), 0, 1);
        values[14] = Cap(s.EnemyStrengthSuppression, 16);
        values[15] = Cap(s.EnemyWeakTurns, 16);
        values[16] = Cap(s.EnemyVulnerableTurns, 16);
        values[17] = Cap(s.LiveDeckSize, 30);
        values[18] = Cap(node.Turn, 10);
        values[19] = energy * pressure;
        values[20] = energy * incoming;
        values[21] = hand * energy;
        values[22] = persistent * pressure;
        values[23] = setup * pressure;
        values[24] = future * pressure;
        values[25] = delayed * enemyHp;
        values[26] = reachable * incoming;
        values[27] = free * incoming;
    }
}
