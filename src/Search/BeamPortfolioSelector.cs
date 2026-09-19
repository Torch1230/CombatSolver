using System.Text.Json;

namespace CombatSolver;

internal sealed record BeamPortfolioSelectorNode(
    int Feature, double Threshold, int Left, int Right,
    double ImprovementRate, int Samples, int Battles);

internal sealed record BeamPortfolioSelectorDocument(
    int SchemaVersion, string SolverAssemblyId, string GameAssemblyId,
    string[] FeatureNames, double[] Minimum, double[] Maximum,
    double SkipThreshold, int MinimumBattles, BeamPortfolioSelectorNode[] Nodes);

internal sealed class BeamPortfolioSelector
{
    public const int SchemaVersion = 1;
    private static readonly string[] FeatureNamesStorage =
    [
        "initial_hp", "maximum_hp", "card_count", "power_count", "enemy_count", "turn",
        "searchable_potions", "boss", "beam", "node_budget", "card_branches", "pile_branches",
        "hand_branches", "baseline_won", "baseline_loss", "baseline_actions", "baseline_turns",
        "baseline_expanded", "baseline_transitions", "baseline_milliseconds",
        "incumbent_loss", "incumbent_potions", "remaining_node_fraction", "remaining_time_fraction",
        "member_width_ratio", "member_second_band", "member_base_score", "dop",
        "growth_targets", "relic_target_count", "theft_policy", "forced_potion_directives",
    ];

    /// <summary>只读视图：调用方改写它不能改变解析时使用的特征顺序。</summary>
    public static IReadOnlyList<string> FeatureNames { get; } = Array.AsReadOnly(FeatureNamesStorage);

    private readonly BeamPortfolioSelectorDocument _model;
    public string ModelId { get; }
    public string SolverAssemblyId => _model.SolverAssemblyId;
    public string GameAssemblyId => _model.GameAssemblyId;

    private BeamPortfolioSelector(BeamPortfolioSelectorDocument model, string id)
    {
        _model = model;
        ModelId = id;
    }

    public static BeamPortfolioSelector Parse(string json)
    {
        BeamPortfolioSelectorDocument model = JsonSerializer.Deserialize<BeamPortfolioSelectorDocument>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty portfolio selector model.");
        if (model.SchemaVersion != SchemaVersion
            || model.FeatureNames == null || !model.FeatureNames.SequenceEqual(FeatureNamesStorage)
            || model.Minimum?.Length != FeatureNamesStorage.Length || model.Maximum?.Length != FeatureNamesStorage.Length
            || model.Nodes == null || model.Nodes.Length is < 1 or > 255
            || !Guid.TryParse(model.SolverAssemblyId, out _) || !Guid.TryParse(model.GameAssemblyId, out _)
            || !double.IsFinite(model.SkipThreshold) || model.SkipThreshold is < 0 or > 1
            || model.MinimumBattles < 2)
            throw new InvalidDataException("Invalid portfolio selector schema or metadata.");
        for (int index = 0; index < FeatureNamesStorage.Length; index++)
            if (!double.IsFinite(model.Minimum[index]) || !double.IsFinite(model.Maximum[index])
                || model.Minimum[index] > model.Maximum[index])
                throw new InvalidDataException("Invalid portfolio selector feature bounds.");
        HashSet<int> visited = [];
        ValidateNode(0);
        if (visited.Count != model.Nodes.Length)
            throw new InvalidDataException("Unreachable portfolio selector node.");
        string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return new BeamPortfolioSelector(model, id);

        void ValidateNode(int index)
        {
            if (index < 0 || index >= model.Nodes.Length || !visited.Add(index))
                throw new InvalidDataException("Invalid portfolio selector tree links.");
            BeamPortfolioSelectorNode node = model.Nodes[index]
                ?? throw new InvalidDataException("Null portfolio selector node.");
            if (!double.IsFinite(node.ImprovementRate) || node.ImprovementRate is < 0 or > 1
                || node.Samples < 1 || node.Battles < 1 || node.Battles > node.Samples)
                throw new InvalidDataException("Invalid portfolio selector leaf evidence.");
            if (node.Feature == -1)
            {
                if (node.Left != -1 || node.Right != -1)
                    throw new InvalidDataException("Portfolio selector leaf has children.");
                return;
            }
            if (node.Feature < 0 || node.Feature >= FeatureNamesStorage.Length || !double.IsFinite(node.Threshold))
                throw new InvalidDataException("Invalid portfolio selector split.");
            ValidateNode(node.Left);
            ValidateNode(node.Right);
        }
    }

    public string Decide(ReadOnlySpan<double> values, string solverAssemblyId, string gameAssemblyId)
    {
        if (solverAssemblyId != _model.SolverAssemblyId || gameAssemblyId != _model.GameAssemblyId)
            return "VersionMismatch";
        if (values.Length != FeatureNamesStorage.Length)
            return "FeatureMismatch";
        for (int index = 0; index < values.Length; index++)
        {
            if (!double.IsFinite(values[index]))
                return "InvalidFeature";
            // Timing and remaining budgets vary with load; only state and configuration define support.
            if ((index <= 12 || index >= 24) &&
                (values[index] < _model.Minimum[index] || values[index] > _model.Maximum[index]))
                return "OutsideTrainingRange";
        }
        int cursor = 0;
        while (_model.Nodes[cursor].Feature >= 0)
        {
            BeamPortfolioSelectorNode node = _model.Nodes[cursor];
            // Training uses float32 feature matrices, including at threshold boundaries.
            cursor = (float)values[node.Feature] <= node.Threshold ? node.Left : node.Right;
        }
        BeamPortfolioSelectorNode leaf = _model.Nodes[cursor];
        return leaf.Battles >= _model.MinimumBattles && leaf.ImprovementRate <= _model.SkipThreshold
            ? "LearnedNoImprovement" : "Run";
    }
}

internal sealed record BeamPortfolioObservation(
    double[] Features, string Decision, bool Ran, bool? Improved, bool? LabelUsable,
    long ElapsedMilliseconds, string? Termination);

internal sealed record BeamPortfolioExperiment(
    BeamPortfolioSelector? Model,
    Action<BeamPortfolioObservation>? Observe);
