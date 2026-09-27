using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

// Request-local fitted values. Labels are witnessed future HP costs, never old
// heuristic scores or fabricated negative labels for unsearched branches.
internal sealed class SearchOutcomeValueModel
{
    private const int Schema = 3;
    private const int MaximumStates = 8192;
    private sealed class Observation(Dictionary<string, double> features)
    {
        internal readonly Dictionary<string, double> Features = features;
        internal double Target = double.PositiveInfinity;
    }
    private readonly Dictionary<StateFingerprint, Observation> _observations = [];
    private readonly Dictionary<StateFingerprint, double> _predictions = [];
    private string[] _featureNames = [];
    private bool _frozen;
    internal int Samples { get; private set; }
    internal bool IsFitted => _forest != null;

    internal void ObserveState(SearchNode node, Player player)
    {
        if (_frozen) return;
        if (_observations.Count < MaximumStates && !_observations.ContainsKey(node.StateKey))
            _observations.Add(node.StateKey, new(Features(node.Snapshot, player)));
    }

    internal void ObserveVictory(SearchNode node, Player player)
    {
        if (_frozen) return;
        if (!node.Snapshot.AllEnemiesDead || node.Snapshot.PlayerDead || node.HasPredictionRisk
            || node.BoundaryReason != SearchBoundaryReason.None) return;
        if (node.Snapshot.HasSimulator) ObserveState(node, player);
        double total = Cost(node.Snapshot);
        for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
        {
            if (!_observations.TryGetValue(cursor.StateKey, out var observation)) continue;
            double target = total - Cost(cursor.Snapshot);
            if (target >= observation.Target) continue;
            if (!double.IsFinite(observation.Target)) Samples++;
            observation.Target = target;
        }
    }

    internal sealed record Tree(int Feature, double Threshold, double Mean, Tree? Left = null, Tree? Right = null)
    {
        internal double Predict(IReadOnlyDictionary<string, double> x, string[] names)
        {
            Tree node = this;
            while (node.Feature >= 0) node = x.GetValueOrDefault(names[node.Feature]) <= node.Threshold ? node.Left! : node.Right!;
            return node.Mean;
        }
    }
    private Tree[]? _forest;

    internal sealed record TrainingRow(Dictionary<string, double> Features, double Target);
    internal sealed record Document(int Schema, string[] FeatureNames, Guid GameMvid, Tree[] Forest);
    internal TrainingRow[] ExportRows() => _observations.Values.Where(o => double.IsFinite(o.Target))
        .Select(o => new TrainingRow(o.Features, o.Target)).ToArray();
    internal Document ExportModel() => new(Schema, _featureNames, typeof(Player).Assembly.ManifestModule.ModuleVersionId,
        _forest ?? throw new InvalidOperationException("No fitted model."));
    internal static SearchOutcomeValueModel Load(Document document)
    {
        if (document.Schema != Schema || document.FeatureNames.Length == 0
            || document.FeatureNames.Distinct(StringComparer.Ordinal).Count() != document.FeatureNames.Length
            || document.GameMvid != typeof(Player).Assembly.ManifestModule.ModuleVersionId)
            throw new InvalidDataException("Incompatible outcome value model.");
        if (document.Forest.Length is < 1 or > 64) throw new InvalidDataException("Invalid forest size.");
        foreach (var tree in document.Forest) Validate(tree, 0);
        return new() { _forest = document.Forest, _featureNames = document.FeatureNames, _frozen = true };

        void Validate(Tree? tree, int depth)
        {
            if (tree == null || depth > 16 || !double.IsFinite(tree.Mean) || !double.IsFinite(tree.Threshold)
                || tree.Feature < -1 || tree.Feature >= document.FeatureNames.Length)
                throw new InvalidDataException("Invalid value tree.");
            if (tree.Feature >= 0) { Validate(tree.Left, depth + 1); Validate(tree.Right, depth + 1); }
            else if (tree.Left != null || tree.Right != null) throw new InvalidDataException("Invalid value leaf.");
        }
    }
    internal bool Fit() => Fit(ExportRows());
    internal bool Fit(IReadOnlyList<TrainingRow> rows)
    {
        if (rows.Any(r => r.Features.Count == 0 || !double.IsFinite(r.Target)
            || r.Features.Any(x => string.IsNullOrWhiteSpace(x.Key) || !double.IsFinite(x.Value))))
            throw new InvalidDataException("Invalid outcome training row.");
        _featureNames = rows.SelectMany(r => r.Features.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // Temporary dense columns make fitting fast; inference and collection remain sparse.
        var samples = rows.Select(r => (Features: _featureNames.Select(n => (float)r.Features.GetValueOrDefault(n)).ToArray(), r.Target)).ToArray();
        if (samples.Length < 2) return false;
        int[] active = Enumerable.Range(0, _featureNames.Length).Where(f =>
            samples.Any(o => o.Features[f] != samples[0].Features[f])).ToArray();
        Random random = new(0);
        int[] indices = Enumerable.Range(0, samples.Length).ToArray();
        _forest = Enumerable.Range(0, 32).Select(_ => Build(indices, 0)).ToArray();
        return true;

        Tree Build(int[] rows, int depth)
        {
            double sum = rows.Sum(i => samples[i].Target);
            double sumSquare = rows.Sum(i => samples[i].Target * samples[i].Target);
            double mean = sum / rows.Length;
            double bestLoss = sumSquare - sum * mean;
            if (depth >= 8 || rows.Length < 16 || bestLoss <= 1e-9 || active.Length == 0)
                return new(-1, 0, mean);
            int feature = -1; double threshold = 0;
            for (int trial = 0; trial < Math.Max(8, (int)Math.Sqrt(active.Length)); trial++)
            {
                int f = active[random.Next(active.Length)];
                double min = rows.Min(i => samples[i].Features[f]);
                double max = rows.Max(i => samples[i].Features[f]);
                if (min == max) continue;
                double split = min + random.NextDouble() * (max - min);
                double leftSum = 0, leftSquare = 0; int leftCount = 0;
                foreach (int i in rows)
                {
                    if (samples[i].Features[f] > split) continue;
                    leftCount++; leftSum += samples[i].Target;
                    leftSquare += samples[i].Target * samples[i].Target;
                }
                int rightCount = rows.Length - leftCount;
                if (leftCount < 4 || rightCount < 4) continue;
                double rightSum = sum - leftSum;
                double loss = leftSquare - leftSum * leftSum / leftCount
                    + sumSquare - leftSquare - rightSum * rightSum / rightCount;
                if (loss < bestLoss) { bestLoss = loss; feature = f; threshold = split; }
            }
            if (feature < 0) return new(-1, 0, mean);
            return new(feature, threshold, mean,
                Build(rows.Where(i => samples[i].Features[feature] <= threshold).ToArray(), depth + 1),
                Build(rows.Where(i => samples[i].Features[feature] > threshold).ToArray(), depth + 1));
        }
    }

    internal double PredictTotalCost(SearchNode node, Player player)
    {
        if (_forest == null) throw new InvalidOperationException("Value model has not been fitted.");
        double value = Cost(node.Snapshot);
        if (node.IsTerminal) return value;
        if (_frozen && _predictions.TryGetValue(node.StateKey, out double cached)) return value + cached;
        Dictionary<string, double> features = _observations.TryGetValue(node.StateKey, out var observation)
            ? observation.Features : Features(node.Snapshot, player);
        double predicted = _forest.Average(tree => tree.Predict(features, _featureNames));
        if (_frozen && _predictions.Count < 4096) _predictions.TryAdd(node.StateKey, predicted);
        return value + predicted;
    }

    private static double Cost(SimulationSnapshot s)
        => s.CumulativePlayerHpLost - s.RecoveredPlayerHp - s.StrategicHpCredit;

    private static Dictionary<string, double> Features(SimulationSnapshot s, Player player)
    {
        var x = SearchOutcomeContext.Capture((CombatPredictionSimulator)s.Simulator, player);
        x["battle/enemy-hp"] = s.EnemyHp;
        x["battle/projected-hp"] = s.ProjectedPlayerHp;
        x["battle/enemies-alive"] = s.AliveEnemyCount;
        x["battle/turn"] = s.Turn;
        return x;
    }
}
