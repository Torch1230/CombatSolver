using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

// Search-state ranking learned only from compared, completed continuation witnesses.
// Unlabelled/pruned states stay unknown. All collection and fitting is opt-in offline.
internal sealed class SearchOutcomeValueModel
{
    private const int Schema = 4;
    private const int MaximumStates = 8192, MaximumGroups = 256, MaximumGroupMembers = 32;
    private readonly record struct ObservationKey(StateFingerprint State, int HpCost, int PotionCost);
    private sealed class Observation(Dictionary<string, double> features)
    {
        internal readonly Dictionary<string, double> Features = features;
        internal readonly List<int> Groups = [];
        internal SolverInterimResult? Outcome;
        internal int RemainingActions;
    }
    private readonly Dictionary<ObservationKey, Observation> _observations = [];
    private readonly Dictionary<ObservationKey, double> _predictions = [];
    private int _groups;
    private bool _frozen;
    private string[] _featureNames = [];
    private Tree[]? _forest;
    private Dictionary<string, int> _columns = new(StringComparer.Ordinal);
    private double[] _featureScratch = [];
    private float[] _predictionValues = [];
    private const double LearningRate = 0.1;
    internal bool IsFitted => _forest != null;
    internal int FittedPairs { get; private set; }
    internal int Samples => _observations.Values.Count(o => o.Outcome != null);
    private static ObservationKey Key(SearchNode node) => new(node.StateKey,
        node.Snapshot.CumulativePlayerHpLost - node.Snapshot.RecoveredPlayerHp - node.Snapshot.StrategicHpCredit,
        node.PotionStrategicCost);

    internal void ObserveState(SearchNode node, Player player)
    {
        if (_frozen || !node.Snapshot.HasSimulator || node.HasPredictionRisk
            || node.IsTerminal || node.BoundaryReason != SearchBoundaryReason.None) return;
        var key = Key(node);
        if (_observations.Count < MaximumStates && !_observations.ContainsKey(key))
            _observations.Add(key, new(Features(node, player)));
    }

    internal void ObservePool(IReadOnlyList<SearchNode> nodes, Player player)
    {
        if (_frozen || _groups >= MaximumGroups || nodes.Count < 2) return;
        int group = _groups++;
        int count = Math.Min(MaximumGroupMembers, nodes.Count);
        for (int i = 0; i < count; i++)
        {
            SearchNode node = nodes[i * nodes.Count / count];
            ObserveState(node, player);
            if (_observations.TryGetValue(Key(node), out var observation)
                && observation.Groups.Count < 8 && !observation.Groups.Contains(group))
                observation.Groups.Add(group);
        }
    }

    internal void ObserveVictory(SearchNode node, SolverInterimResult quality)
    {
        if (_frozen || !quality.Won || !quality.Survives || node.HasPredictionRisk
            || node.BoundaryReason != SearchBoundaryReason.None) return;
        // The existing final-policy result includes post-combat healing, max-HP,
        // boss relief, death saves and explicit user goals. The old Score is omitted.
        quality = quality with { Score = 0 };
        for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
        {
            if (!_observations.TryGetValue(Key(cursor), out var observation)) continue;
            int actions = node.ActionCount - cursor.ActionCount;
            if (observation.Outcome is { } old
                && (SolverInterimResultOrdering.IsBetter(old, quality)
                    || !SolverInterimResultOrdering.IsBetter(quality, old)
                        && observation.RemainingActions <= actions)) continue;
            observation.Outcome = quality;
            observation.RemainingActions = actions;
        }
    }

    internal sealed record TrainingRow(Dictionary<string, double> Features,
        SolverInterimResult Outcome, int RemainingActions, int[] Groups);
    internal sealed record Tree(int Feature, double Threshold, double Mean, Tree? Left = null, Tree? Right = null)
    {
        internal double Predict(float[] values)
        {
            Tree node = this;
            while (node.Feature >= 0) node = values[node.Feature] <= node.Threshold ? node.Left! : node.Right!;
            return node.Mean;
        }
    }
    internal sealed record Document(int Schema, string[] FeatureNames, Guid GameMvid, Tree[] Forest);
    internal TrainingRow[] ExportRows() => _observations.Values.Where(o => o.Outcome != null && o.Groups.Count != 0)
        .Select(o => new TrainingRow(o.Features, o.Outcome!, o.RemainingActions, o.Groups.ToArray())).ToArray();
    internal Document ExportModel() => new(Schema, _featureNames, typeof(Player).Assembly.ManifestModule.ModuleVersionId,
        _forest ?? throw new InvalidOperationException("No fitted ranker."));
    internal static SearchOutcomeValueModel Load(Document document)
    {
        if (document.Schema != Schema || document.FeatureNames == null
            || document.FeatureNames.Any(string.IsNullOrWhiteSpace)
            || document.FeatureNames.Distinct(StringComparer.Ordinal).Count() != document.FeatureNames.Length
            || document.GameMvid != typeof(Player).Assembly.ManifestModule.ModuleVersionId
            || document.Forest is not { Length: > 0 and <= 64 })
            throw new InvalidDataException("Incompatible outcome ranking model.");
        foreach (var tree in document.Forest) Validate(tree, 0);
        SearchOutcomeValueModel model = new() { _frozen = true };
        model.Compile(document.FeatureNames, document.Forest);
        return model;
        void Validate(Tree? tree, int depth)
        {
            if (tree == null || depth > 8 || !double.IsFinite(tree.Mean) || !double.IsFinite(tree.Threshold)
                || tree.Feature < -1 || tree.Feature >= document.FeatureNames.Length)
                throw new InvalidDataException("Invalid ranking tree.");
            if (tree.Feature >= 0) { Validate(tree.Left, depth + 1); Validate(tree.Right, depth + 1); }
            else if (tree.Left != null || tree.Right != null) throw new InvalidDataException("Invalid ranking leaf.");
        }
    }

    private readonly record struct Pair(int Preferred, int Other, double Weight);
    internal bool Fit(IReadOnlyList<TrainingRow[]> roots)
    {
        List<TrainingRow> rows = [];
        List<Pair> pairs = [];
        Random random = new(0);
        foreach (var root in roots)
        {
            int offset = rows.Count;
            if (root.Any(r => r.Features.Count == 0 || !r.Outcome.Won || !r.Outcome.Survives
                || r.Outcome.Score != 0 || r.RemainingActions < 0 || r.Groups.Length == 0
                || r.Features.Any(x => string.IsNullOrWhiteSpace(x.Key) || !double.IsFinite(x.Value))))
                throw new InvalidDataException("Invalid witnessed ranking row.");
            rows.AddRange(root);
            List<(int Preferred, int Other)> rootPairs = [];
            HashSet<(int, int)> seen = [];
            var groups = Enumerable.Range(0, root.Length).SelectMany(i => root[i].Groups.Select(g => (Group: g, Row: i)))
                .GroupBy(x => x.Group);
            foreach (var group in groups)
            {
                int[] members = group.Select(x => x.Row).Distinct().ToArray();
                for (int a = 0; a < members.Length; a++)
                    for (int b = a + 1; b < members.Length; b++)
                    {
                        int ia = members[a], ib = members[b];
                        int order = CompareWitnesses(root[ia], root[ib]);
                        if (order == 0) continue;
                        var pair = order < 0 ? (ia, ib) : (ib, ia);
                        if (seen.Add(pair)) rootPairs.Add(pair);
                    }
            }
            // Equal total weight per root; repeated states/pools cannot multiply a pair.
            var sampled = rootPairs.ToArray(); random.Shuffle(sampled);
            int count = Math.Min(4096, sampled.Length);
            foreach (var pair in sampled.Take(count))
                pairs.Add(new(offset + pair.Preferred, offset + pair.Other, 1d / count));
        }
        FittedPairs = pairs.Count;
        if (pairs.Count < 2) return false;
        _featureNames = rows.SelectMany(r => r.Features.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        float[][] x = rows.Select(r => _featureNames.Select(n => (float)r.Features.GetValueOrDefault(n)).ToArray()).ToArray();
        int[] active = Enumerable.Range(0, _featureNames.Length).Where(f => x.Any(r => r[f] != x[0][f])).ToArray();
        int[] indices = pairs.SelectMany(p => new[] { p.Preferred, p.Other }).Distinct().Order().ToArray();
        double[] scores = new double[rows.Count], gradient = new double[rows.Count], hessian = new double[rows.Count];
        List<Tree> trees = [];
        for (int round = 0; round < 48; round++)
        {
            Array.Clear(gradient); Array.Clear(hessian);
            foreach (var pair in pairs)
            {
                double p = 1 / (1 + Math.Exp(Math.Clamp(scores[pair.Preferred] - scores[pair.Other], -40, 40)));
                double g = pair.Weight * p, h = pair.Weight * p * (1 - p);
                gradient[pair.Preferred] += g; gradient[pair.Other] -= g;
                hessian[pair.Preferred] += h; hessian[pair.Other] += h;
            }
            Tree tree = Build(indices, 0);
            trees.Add(tree);
            foreach (int i in indices) scores[i] += LearningRate * tree.Predict(x[i]);
        }
        Compile(_featureNames, trees.ToArray());
        return true;

        Tree Build(int[] selected, int depth)
        {
            double g = selected.Sum(i => gradient[i]), h = selected.Sum(i => hessian[i]);
            const double regularization = 0.001;
            double mean = g / (h + regularization);
            if (depth >= 4 || selected.Length < 12 || active.Length == 0) return new(-1, 0, mean);
            double bestGain = g * g / (h + regularization); int feature = -1; double threshold = 0;
            for (int trial = 0; trial < Math.Max(16, (int)Math.Sqrt(active.Length)); trial++)
            {
                int f = active[random.Next(active.Length)];
                double min = selected.Min(i => x[i][f]), max = selected.Max(i => x[i][f]);
                if (min == max) continue;
                double split = min + random.NextDouble() * (max - min);
                double lg = 0, lh = 0; int leftCount = 0;
                foreach (int i in selected)
                {
                    if (x[i][f] > split) continue;
                    leftCount++; lg += gradient[i]; lh += hessian[i];
                }
                if (leftCount < 4 || selected.Length - leftCount < 4) continue;
                double gain = lg * lg / (lh + regularization) + (g - lg) * (g - lg) / (h - lh + regularization);
                if (gain > bestGain + 1e-12) { bestGain = gain; feature = f; threshold = split; }
            }
            if (feature < 0) return new(-1, 0, mean);
            return new(feature, threshold, mean,
                Build(selected.Where(i => x[i][feature] <= threshold).ToArray(), depth + 1),
                Build(selected.Where(i => x[i][feature] > threshold).ToArray(), depth + 1));
        }
    }

    // Lower means preferred. Effort only breaks an exact final-policy tie; it is
    // observed suffix length, not a handmade conversion between damage and cards.
    internal static int CompareWitnesses(TrainingRow a, TrainingRow b)
    {
        if (SolverInterimResultOrdering.IsBetter(a.Outcome, b.Outcome)) return -1;
        if (SolverInterimResultOrdering.IsBetter(b.Outcome, a.Outcome)) return 1;
        return a.RemainingActions.CompareTo(b.RemainingActions);
    }

    // A compact numeric program references only columns actually used by a split.
    // Scratch is request-local and used only by the enforced DOP1 research path.
    private void Compile(string[] names, Tree[] trees)
    {
        HashSet<int> used = [];
        void Visit(Tree tree)
        {
            if (tree.Feature < 0) return;
            used.Add(tree.Feature); Visit(tree.Left!); Visit(tree.Right!);
        }
        foreach (Tree tree in trees) Visit(tree);
        int[] original = used.Order().ToArray();
        var remap = original.Select((feature, index) => (feature, index)).ToDictionary(p => p.feature, p => p.index);
        Tree Rewrite(Tree tree) => tree.Feature < 0 ? tree : tree with
        { Feature = remap[tree.Feature], Left = Rewrite(tree.Left!), Right = Rewrite(tree.Right!) };
        _featureNames = original.Select(i => names[i]).ToArray();
        _columns = _featureNames.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
        _forest = trees.Select(Rewrite).ToArray();
        _featureScratch = new double[_featureNames.Length];
        _predictionValues = new float[_featureNames.Length];
    }

    internal double PredictPriority(SearchNode node, Player player)
    {
        if (_forest == null) throw new InvalidOperationException("Ranker has not been fitted.");
        var key = Key(node);
        if (_frozen && _predictions.TryGetValue(key, out double cached)) return cached;
        SearchOutcomeContext.CaptureSelected((CombatPredictionSimulator)node.Snapshot.Simulator,
            player, _columns, _featureScratch);
        AddPolicyFeatures(node, Set);
        for (int i = 0; i < _predictionValues.Length; i++) _predictionValues[i] = (float)_featureScratch[i];
        double predicted = LearningRate * _forest.Sum(tree => tree.Predict(_predictionValues));
        if (_frozen && _predictions.Count < 4096) _predictions.TryAdd(key, predicted);
        return predicted;
        void Set(string name, double value)
        {
            if (_columns.TryGetValue(name, out int index)) _featureScratch[index] = value;
        }
    }

    internal double PredictFeaturesForTesting(IReadOnlyDictionary<string, double> features)
    {
        if (_forest == null) throw new InvalidOperationException("Ranker has not been fitted.");
        float[] values = _featureNames.Select(name => (float)features.GetValueOrDefault(name)).ToArray();
        return LearningRate * _forest.Sum(tree => tree.Predict(values));
    }

    private static Dictionary<string, double> Features(SearchNode node, Player player)
    {
        var x = SearchOutcomeContext.Capture((CombatPredictionSimulator)node.Snapshot.Simulator, player);
        AddPolicyFeatures(node, (name, value) => x[name] = value);
        return x;
    }
    private static void AddPolicyFeatures(SearchNode node, Action<string, double> set)
    {
        var s = node.Snapshot;
        set("battle/enemy-hp", s.EnemyHp);
        set("battle/projected-hp", s.ProjectedPlayerHp);
        set("battle/enemies-alive", s.AliveEnemyCount);
        set("battle/turn", s.Turn);
        set("policy/hp-cost", Key(node).HpCost);
        set("policy/potion-cost", node.PotionStrategicCost);
        set("policy/death-saves", s.ProjectedDeathSaveUseCount);
        set("policy/strategic-credit", s.StrategicHpCredit);
    }
}
