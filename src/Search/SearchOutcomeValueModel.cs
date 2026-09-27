using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

// Search-state ranking learned only from compared, completed continuation witnesses.
// Unlabelled/pruned states stay unknown. All collection and fitting is opt-in offline.
internal sealed partial class SearchOutcomeValueModel
{
    internal const int Schema = 10, LegacySchema = 9, FeatureSchema = 8;
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
    private int _noveltyGroups;
    private readonly List<CorrectionQuery> _correctionQueries = [];
    private readonly HashSet<int> _correctionDepths = [];
    // Retention can compare an expanded parent's rank after releasing its simulator.
    // Record copies can share a snapshot after the original node is expanded.
    // Keep only scalars, keyed by the exact state/policy identity, for that weak
    // snapshot lifetime. Never retain its simulator or a node/parent chain.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SimulationSnapshot,
        Dictionary<ObservationKey, double>> _snapshotPriorities = new();
    private bool _correcting;
    private bool _frozen;
    private string[] _featureNames = [];
    private Tree[]? _forest;
    private double[] _linearWeights = [];
    private Dictionary<string, int> _columns = new(StringComparer.Ordinal);
    private HashSet<string> _prefixes = [];
    private double[] _featureScratch = [];
    private float[] _predictionValues = [];
    private long _predictionCalls, _cacheHits, _featureTicks, _forestTicks;
    internal bool MeasurePerformance { get; set; }
    private const double LearningRate = 0.1;
    internal bool IsFitted => _forest != null;
    internal int FittedPairs { get; private set; }
    internal int FittedRows { get; private set; }
    internal int FittedRoots { get; private set; }
    internal int[] FittedPairKinds { get; private set; } = new int[3];
    internal int EligibleFeatures { get; private set; }
    internal int Samples => _observations.Values.Count(o => o.Outcome != null);
    private static ObservationKey Key(SearchNode node) => new(node.StateKey,
        node.Snapshot.CumulativePlayerHpLost - node.Snapshot.RecoveredPlayerHp - node.Snapshot.StrategicHpCredit,
        node.PotionStrategicCost);

    internal void ObserveState(SearchNode node, Player player)
    {
        if (_frozen || _correcting || !node.Snapshot.HasSimulator || node.HasPredictionRisk
            || node.IsTerminal || node.BoundaryReason != SearchBoundaryReason.None) return;
        var key = Key(node);
        if (_observations.Count < MaximumStates && !_observations.ContainsKey(key))
            _observations.Add(key, new(Features(node, player)));
    }

    internal void ObservePool(IReadOnlyList<SearchNode> nodes, Player player, bool novelty = false)
    {
        if (_frozen || _correcting || _groups >= MaximumGroups || nodes.Count < 2
            || novelty && _noveltyGroups >= 64) return;
        if (novelty) _noveltyGroups++;
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

    // Detached first-turn prefixes only: the fixed-prefix replay contract does
    // not accept EndTurn or a root's initial choice transaction. No state graphs
    // survive this synchronous pruning callback.
    internal sealed record CorrectionQuery(PlanAction[] Prefix, StateFingerprint State, int HpCost, int PotionCost);
    internal void ObserveCorrectionBoundary(IReadOnlyList<SearchNode> pool, IReadOnlyList<SearchNode> selected,
        Player player, SearchOutcomeValueModel predictor)
    {
        if (_frozen || _correcting || _correctionDepths.Count >= 3 || _groups >= MaximumGroups) return;
        var retained = selected.LastOrDefault(Eligible);
        if (retained == null || _correctionDepths.Contains(retained.ActionCount)) return;
        var selectedKeys = selected.Select(Key).ToHashSet();
        var dropped = pool.Where(n => Eligible(n) && n.ActionCount == retained.ActionCount
                && !selectedKeys.Contains(Key(n)))
            .OrderByDescending(n => predictor.PredictPriority(n, player)).FirstOrDefault();
        if (dropped == null) return;
        ObserveState(retained, player); ObserveState(dropped, player);
        if (!_observations.TryGetValue(Key(retained), out var a) || a.Groups.Count >= 8
            || !_observations.TryGetValue(Key(dropped), out var b) || b.Groups.Count >= 8) return;
        int group = _groups++;
        a.Groups.Add(group); b.Groups.Add(group);
        _correctionDepths.Add(retained.ActionCount);
        foreach (var node in new[] { retained, dropped })
        {
            List<PlanAction> actions = [];
            for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
                if (cursor.Action is { } action) actions.Add(CombatBeamSolver.CopyObservedAction(action));
            actions.Reverse();
            var key = Key(node);
            _correctionQueries.Add(new(actions.ToArray(), key.State, key.HpCost, key.PotionCost));
        }

        static bool Eligible(SearchNode node)
        {
            if (node.ActionCount is < 1 or > 96 || node.IsTerminal || node.HasPredictionRisk
                || node.BoundaryReason != SearchBoundaryReason.None || !node.Snapshot.HasSimulator) return false;
            for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
                if (cursor.TurnSetupChoices is { Count: > 0 }
                    || cursor.Action is { } action && (action.EndsPlayerTurn || action.Turn != node.Turn)) return false;
            return true;
        }
    }

    internal CorrectionQuery[] BeginCorrections()
    {
        _correcting = true; // Teacher adds witnesses, never more collection pools.
        return _correctionQueries.ToArray();
    }
    internal SolverInterimResult? CorrectionWitness(CorrectionQuery query)
        => _observations[new(query.State, query.HpCost, query.PotionCost)].Outcome;

    internal void ObserveCompleted(SearchNode node, SolverInterimResult quality)
    {
        bool victory = quality.Won && quality.Survives;
        bool defeat = !quality.Won && !quality.Survives && node.Snapshot.PlayerDead
            && node.Snapshot.TerminalStamp is { Outcome: CombatTerminalOutcome.Defeat };
        if (_frozen || (!victory && !defeat) || node.HasPredictionRisk
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
        SolverInterimResult Outcome, int RemainingActions, int[] Groups, bool CompletedDefeat = false,
        int FeatureSchema = 0);
    internal sealed record Tree(int Feature, double Threshold, double Mean, Tree? Left = null, Tree? Right = null)
    {
        internal double Predict(float[] values)
        {
            Tree node = this;
            while (node.Feature >= 0) node = values[node.Feature] <= node.Threshold ? node.Left! : node.Right!;
            return node.Mean;
        }
    }
    internal sealed record Document(int Schema, string[] FeatureNames, Guid GameMvid, Tree[] Forest,
        double[]? LinearWeights = null, double[][]? FactorWeights = null);
    internal TrainingRow[] ExportRows() => _observations.Values.Where(o => o.Outcome != null && o.Groups.Count != 0)
        .Select(o => new TrainingRow(o.Features, o.Outcome!, o.RemainingActions, o.Groups.ToArray(), !o.Outcome!.Won, FeatureSchema)).ToArray();
    internal Document ExportModel() => new(Schema, _featureNames, typeof(Player).Assembly.ManifestModule.ModuleVersionId,
        _forest ?? throw new InvalidOperationException("No fitted ranker."), _linearWeights,
        _factorWeights?.Select(row => row.ToArray()).ToArray());
    internal Document ExportLinearModel() => Load(ExportModel() with
        { Forest = [new(-1, 0, 0)], FactorWeights = null }).ExportModel();
    internal object DescribeCollection() => new
    {
        featureSchema = FeatureSchema, states = _observations.Count, pools = _groups, noveltyPools = _noveltyGroups,
        labelled = Samples, exported = ExportRows().Length,
    };
    internal static SearchOutcomeValueModel Load(Document document)
    {
        if (document.Schema != Schema && document.Schema != LegacySchema
            || document.Schema == LegacySchema && document.FactorWeights != null || document.FeatureNames == null
            || document.FeatureNames.Any(string.IsNullOrWhiteSpace)
            || document.FeatureNames.Distinct(StringComparer.Ordinal).Count() != document.FeatureNames.Length
            || document.GameMvid != typeof(Player).Assembly.ManifestModule.ModuleVersionId
            || document.Forest is not { Length: > 0 and <= 64 }
            || document.LinearWeights is { } weights && (weights.Length != document.FeatureNames.Length
                || weights.Any(w => !double.IsFinite(w))))
            throw new InvalidDataException("Incompatible outcome ranking model.");
        foreach (var tree in document.Forest) Validate(tree, 0);
        ValidateFactors(document.FactorWeights, document.FeatureNames.Length);
        SearchOutcomeValueModel model = new() { _frozen = true };
        model.Compile(document.FeatureNames, document.Forest,
            document.LinearWeights ?? new double[document.FeatureNames.Length], document.FactorWeights);
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

    internal readonly record struct Pair(int Preferred, int Other, double Weight);
    internal sealed record PreparedTraining(List<TrainingRow> Rows, List<Pair> Pairs,
        string[] FeatureNames, int ParticipatingRoots, int[] PairKinds);
    internal static void ValidateTrainingRows(IEnumerable<TrainingRow> rows)
    {
        if (rows.Any(r => r == null || r.FeatureSchema != FeatureSchema || r.Features == null || r.Outcome == null || r.Groups == null || r.Features.Count == 0
            || !(r.Outcome.Won && r.Outcome.Survives && !r.CompletedDefeat
                || !r.Outcome.Won && !r.Outcome.Survives && r.CompletedDefeat)
            || r.Outcome.Score != 0 || r.RemainingActions < 0 || r.Groups.Length == 0
            || r.Features.Any(x => string.IsNullOrWhiteSpace(x.Key) || !double.IsFinite(x.Value))))
            throw new InvalidDataException("Invalid witnessed ranking row.");
    }

    // The built-in and optional offline fitters consume the same authoritative
    // comparisons, support threshold, ordering, root weights and sampled rows.
    internal static PreparedTraining PrepareTraining(IReadOnlyList<TrainingRow[]> roots,
        bool highestPolicyTierOnly = false, bool balanceTrainingTurns = false)
    {
        List<TrainingRow> rows = [];
        List<Pair> pairs = [];
        Dictionary<string, int> featureRoots = new(StringComparer.Ordinal);
        int participatingRoots = 0;
        int[] pairKinds = new int[3];
        Random random = new(0);
        foreach (var root in roots)
        {
            int offset = rows.Count;
            ValidateTrainingRows(root);
            int[]? turns = balanceTrainingTurns ? root.Select(TrainingTurn).ToArray() : null;
            rows.AddRange(root);
            List<(int Preferred, int Other, int Kind)> rootPairs = [];
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
                        int order = CompareWitnesses(root[ia], root[ib], out int kind);
                        if (order == 0) continue;
                        var pair = order < 0 ? (ia, ib) : (ib, ia);
                        if (seen.Add(pair)) rootPairs.Add((pair.Item1, pair.Item2, kind));
                    }
            }
            // Equal total weight per root; repeated states/pools cannot multiply a pair.
            // Optional offline ablation: use each root's highest available
            // policy tier. No hand-assigned numeric tradeoff between tiers.
            int firstKind = rootPairs.Count == 0 ? 0 : rootPairs.Min(p => p.Kind);
            var sampled = rootPairs.Where(p => !highestPolicyTierOnly || p.Kind == firstKind).ToArray();
            random.Shuffle(sampled);
            int count = Math.Min(4096, sampled.Length);
            if (count > 0)
            {
                participatingRoots++;
                foreach (string name in root.SelectMany(r => r.Features.Keys).Distinct(StringComparer.Ordinal))
                    featureRoots[name] = featureRoots.GetValueOrDefault(name) + 1;
            }
            // Optional offline stratification changes only the weights of the
            // already sampled edges. Each witnessed turn receives equal mass
            // within its root; rare cross-turn edges belong to the later turn.
            int[]? pairTurns = turns == null ? null : sampled.Take(count)
                .Select(p => Math.Max(turns[p.Preferred], turns[p.Other])).ToArray();
            var turnCounts = pairTurns?.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            for (int i = 0; i < count; i++)
            {
                var pair = sampled[i];
                double weight = turnCounts == null ? 1d / count
                    : 1d / (turnCounts.Count * (double)turnCounts[pairTurns![i]]);
                pairs.Add(new(offset + pair.Preferred, offset + pair.Other, weight));
                pairKinds[pair.Kind]++;
            }
        }
        if (pairs.Count < 2) return new([], pairs, [], participatingRoots, pairKinds);
        // Validate every input above, but allocate feature histograms only for
        // witnesses referenced by a sampled preference. Unknown comparisons and
        // all-defeat pools supply no gradient, even if their raw row count is high.
        // Preserve row/pair order exactly so compacting cannot change the model.
        int[] observed = pairs.SelectMany(p => new[] { p.Preferred, p.Other }).Distinct().Order().ToArray();
        int[] remap = new int[rows.Count];
        for (int i = 0; i < observed.Length; i++) remap[observed[i]] = i;
        rows = observed.Select(i => rows[i]).ToList();
        pairs = pairs.Select(p => new Pair(remap[p.Preferred], remap[p.Other], p.Weight)).ToList();
        // Correlated rows from one battle are not independent support for an
        // identity-specific coefficient or split. Count actual witnessed roots.
        int minimumRoots = Math.Min(3, participatingRoots);
        string[] names = featureRoots.Where(p => p.Value >= minimumRoots)
            .Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        return new(rows, pairs, names, participatingRoots, pairKinds);
    }

    internal static int TrainingTurn(TrainingRow row)
    {
        if (!row.Features.TryGetValue("battle/turn", out double value) || !double.IsFinite(value)
            || value < 1 || value > int.MaxValue || value != Math.Truncate(value))
            throw new InvalidDataException("Turn-balanced ranking requires an observed positive integer battle turn.");
        return (int)value;
    }

    internal static (Document Model, double[] Scores) FitLinearFoundation(PreparedTraining prepared)
    {
        if (prepared.Pairs.Count < 2) throw new InvalidDataException("Insufficient ranking pairs.");
        var (weights, scores) = FitLinearTerms(prepared.Rows, prepared.Pairs, prepared.FeatureNames);
        return (new(Schema, prepared.FeatureNames, typeof(Player).Assembly.ManifestModule.ModuleVersionId,
            [new(-1, 0, 0)], weights), scores);
    }

    internal bool Fit(IReadOnlyList<TrainingRow[]> roots, int maximumTrainingParallelism = 1,
        bool highestPolicyTierOnly = false, bool balanceTrainingTurns = false)
    {
        if (maximumTrainingParallelism is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(maximumTrainingParallelism));
        var prepared = PrepareTraining(roots, highestPolicyTierOnly, balanceTrainingTurns);
        FittedPairs = prepared.Pairs.Count;
        FittedRoots = prepared.ParticipatingRoots;
        FittedRows = prepared.Rows.Count;
        FittedPairKinds = prepared.PairKinds;
        if (FittedPairs < 2) return false;
        var rows = prepared.Rows;
        var pairs = prepared.Pairs;
        _featureNames = prepared.FeatureNames;
        EligibleFeatures = _featureNames.Length;
        var (foundation, scores) = FitLinearFoundation(prepared);
        double[] linearWeights = foundation.LinearWeights!;
        int[] indices = Enumerable.Range(0, rows.Count).ToArray();
        // Learn splits from all observed columns. Randomly trying a few sparse
        // identities left most known preferences tied even on the fitting roots.
        List<(int Feature, double[] Cuts, byte[] Bins)> columns = [];
        for (int feature = 0; feature < _featureNames.Length; feature++)
        {
            float[] values = rows.Select(r => (float)r.Features.GetValueOrDefault(_featureNames[feature])).ToArray();
            float[] distinct = indices.Select(i => values[i]).Distinct().Order().ToArray();
            if (distinct.Length < 2) continue;
            int count = Math.Min(31, distinct.Length - 1);
            double[] cuts = Enumerable.Range(0, count)
                .Select(i => (double)distinct[(i + 1) * distinct.Length / (count + 1) - 1]).ToArray();
            byte[] bins = new byte[rows.Count];
            foreach (int i in indices)
            {
                int bin = Array.BinarySearch(cuts, (double)values[i]);
                bins[i] = checked((byte)(bin < 0 ? ~bin : bin));
            }
            columns.Add((feature, cuts, bins));
        }
        double[] gradient = new double[rows.Count], hessian = new double[rows.Count];
        // Offline fitting only: workers own disjoint column slots. Their sums
        // retain the original row order; the winner is reduced in column/bin
        // order after every worker joins, preserving serial tie semantics.
        double[] splitGains = new double[columns.Count * 32];
        int[] splitBalances = new int[splitGains.Length];
        ParallelOptions fittingWorkers = new()
        {
            MaxDegreeOfParallelism = maximumTrainingParallelism,
        };
        List<Tree> trees = [];
        for (int round = 0; round < 64; round++)
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
        }
        Compile(_featureNames, trees.ToArray(), linearWeights);
        return true;

        Tree Build(int[] selected, int depth)
        {
            double g = selected.Sum(i => gradient[i]), h = selected.Sum(i => hessian[i]);
            const double regularization = 0.001;
            double mean = g / (h + regularization);
            Tree Leaf()
            {
                foreach (int i in selected) scores[i] += LearningRate * mean;
                return new(-1, 0, mean);
            }
            if (depth >= 6 || selected.Length < 12 || columns.Count == 0) return Leaf();
            double bestGain = g * g / (h + regularization);
            int feature = -1, splitBin = -1, bestBalance = -1;
            double threshold = 0;
            byte[]? bestBins = null;
            void EvaluateColumn(int columnIndex)
            {
                var column = columns[columnIndex];
                int offset = columnIndex * 32;
                Span<double> binGradient = stackalloc double[32], binHessian = stackalloc double[32];
                Span<int> binCount = stackalloc int[32];
                binGradient.Clear(); binHessian.Clear(); binCount.Clear();
                foreach (int i in selected)
                {
                    int bin = column.Bins[i];
                    binGradient[bin] += gradient[i];
                    binHessian[bin] += hessian[i];
                    binCount[bin]++;
                }
                double lg = 0, lh = 0;
                int leftCount = 0;
                for (int bin = 0; bin < column.Cuts.Length; bin++)
                {
                    lg += binGradient[bin]; lh += binHessian[bin]; leftCount += binCount[bin];
                    splitGains[offset + bin] = leftCount < 4 || selected.Length - leftCount < 4
                        ? double.NegativeInfinity
                        : lg * lg / (lh + regularization) + (g - lg) * (g - lg) / (h - lh + regularization);
                    splitBalances[offset + bin] = Math.Min(leftCount, selected.Length - leftCount);
                }
            }
            if (fittingWorkers.MaxDegreeOfParallelism > 1 && selected.Length >= 1024 && columns.Count >= 64)
                Parallel.For(0, columns.Count, fittingWorkers, EvaluateColumn);
            else
                for (int i = 0; i < columns.Count; i++) EvaluateColumn(i);
            for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var column = columns[columnIndex];
                for (int bin = 0; bin < column.Cuts.Length; bin++)
                {
                    double gain = splitGains[columnIndex * 32 + bin];
                    int balance = splitBalances[columnIndex * 32 + bin];
                    // A constant-per-root context has zero marginal pairwise gain:
                    // each root's gradients sum to zero. A neutral split must be
                    // allowed to expose a conditional reversal in its children.
                    bool neutralContext = Math.Abs(bestGain) < 1e-12 && Math.Abs(gain) < 1e-12
                        && balance > bestBalance;
                    if (gain > bestGain + 1e-12 || neutralContext)
                    {
                        bestGain = gain; feature = column.Feature; threshold = column.Cuts[bin];
                        bestBins = column.Bins; splitBin = bin; bestBalance = balance;
                    }
                }
            }
            if (feature < 0) return Leaf();
            return new(feature, threshold, mean,
                Build(selected.Where(i => bestBins![i] <= splitBin).ToArray(), depth + 1),
                Build(selected.Where(i => bestBins![i] > splitBin).ToArray(), depth + 1));
        }
    }

    // Lower means preferred. Effort only breaks an exact final-policy tie; it is
    // observed suffix length, not a handmade conversion between damage and cards.
    internal static int CompareWitnesses(TrainingRow a, TrainingRow b)
        => CompareWitnesses(a, b, out _);

    // Kind is meaningful only for a nonzero preference: victory, policy, effort.
    internal static int CompareWitnesses(TrainingRow a, TrainingRow b, out int kind)
    {
        kind = a.Outcome.Won != b.Outcome.Won ? 0 : 1;
        // Neither defeated continuation achieves the primary goal. Do not teach
        // the model to die faster via the effort tie-breaker.
        if (!a.Outcome.Won && !b.Outcome.Won) return 0;
        if (SolverInterimResultOrdering.IsBetter(a.Outcome, b.Outcome)) return -1;
        if (SolverInterimResultOrdering.IsBetter(b.Outcome, a.Outcome)) return 1;
        kind = 2;
        return a.RemainingActions.CompareTo(b.RemainingActions);
    }

    // A compact numeric program references only columns used by a learned term.
    // Scratch is request-local and used only by the enforced DOP1 research path.
    private void Compile(string[] names, Tree[] trees, double[] linearWeights, double[][]? factors = null)
    {
        Tree Trim(Tree tree)
        {
            if (tree.Feature < 0) return tree;
            Tree left = Trim(tree.Left!), right = Trim(tree.Right!);
            return left.Feature < 0 && right.Feature < 0 && left.Mean == right.Mean
                ? left : tree with { Left = left, Right = right };
        }
        trees = trees.Select(Trim).ToArray();
        HashSet<int> used = [];
        for (int i = 0; i < linearWeights.Length; i++)
            if (linearWeights[i] != 0) used.Add(i);
        bool hasFactors = factors != null && factors.Any(row => row.Any(value => value != 0));
        if (hasFactors)
            for (int i = 0; i < factors!.Length; i++)
                if (factors[i].Any(value => value != 0)) used.Add(i);
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
        _prefixes = SearchOutcomeContext.RequiredPrefixes(_featureNames);
        _forest = trees.Select(Rewrite).ToArray();
        _linearWeights = original.Select(i => linearWeights[i]).ToArray();
        _factorWeights = hasFactors ? original.Select(i => factors![i].ToArray()).ToArray() : null;
        _featureScratch = new double[_featureNames.Length];
        _predictionValues = new float[_featureNames.Length];
    }

    internal double PredictPriority(SearchNode node, Player player)
    {
        if (_forest == null) throw new InvalidOperationException("Ranker has not been fitted.");
        _predictionCalls++;
        var key = Key(node);
        if (_frozen && _snapshotPriorities.TryGetValue(node.Snapshot, out var priorities)
            && priorities.TryGetValue(key, out double prior))
        {
            _cacheHits++;
            return prior;
        }
        if (_frozen && _predictions.TryGetValue(key, out double cached))
        {
            _cacheHits++;
            _snapshotPriorities.GetOrCreateValue(node.Snapshot)[key] = cached;
            return cached;
        }
        long started = MeasurePerformance ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        SearchOutcomeContext.CaptureSelected((CombatPredictionSimulator)node.Snapshot.Simulator,
            player, _columns, _featureScratch, _prefixes);
        AddPolicyFeatures(node, Set);
        for (int i = 0; i < _predictionValues.Length; i++) _predictionValues[i] = (float)_featureScratch[i];
        long featuresDone = MeasurePerformance ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        double sum = 0;
        foreach (Tree tree in _forest) sum += tree.Predict(_predictionValues);
        double predicted = AddInteractions(_predictionValues, LearningRate * sum + LinearPrediction(_predictionValues));
        if (MeasurePerformance)
        {
            _featureTicks += featuresDone - started;
            _forestTicks += System.Diagnostics.Stopwatch.GetTimestamp() - featuresDone;
        }
        if (_frozen)
        {
            if (_predictions.Count < 4096) _predictions.TryAdd(key, predicted);
            _snapshotPriorities.GetOrCreateValue(node.Snapshot)[key] = predicted;
        }
        return predicted;
        void Set(string name, double value)
        {
            if (_columns.TryGetValue(name, out int index)) _featureScratch[index] = value;
        }
    }

    internal object DescribePerformance() => new
    {
        schema = Schema, predictionCalls = _predictionCalls, cacheHits = _cacheHits,
        usedFeatures = _featureNames.Length, trees = _forest?.Length ?? 0, factorRank = _factorWeights?[0].Length ?? 0,
        featureMilliseconds = _featureTicks * 1000d / System.Diagnostics.Stopwatch.Frequency,
        forestMilliseconds = _forestTicks * 1000d / System.Diagnostics.Stopwatch.Frequency,
        measured = MeasurePerformance,
    };

    internal double PredictFeaturesForTesting(IReadOnlyDictionary<string, double> features)
    {
        if (_forest == null) throw new InvalidOperationException("Ranker has not been fitted.");
        float[] values = _featureNames.Select(name => (float)features.GetValueOrDefault(name)).ToArray();
        return AddInteractions(values, LearningRate * _forest.Sum(tree => tree.Predict(values)) + LinearPrediction(values));
    }

    private double LinearPrediction(float[] values)
    {
        double sum = 0;
        for (int i = 0; i < _linearWeights.Length; i++) sum += _linearWeights[i] * values[i];
        return sum;
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
