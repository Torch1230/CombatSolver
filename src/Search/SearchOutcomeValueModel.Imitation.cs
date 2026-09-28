using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class SearchOutcomeValueModel
{
    // An expert's selected route is a separate imitation target. Absence from
    // this route says nothing about the unchosen state's eventual outcome.
    private SolverInterimResult? _imitationWitness;
    private int _imitationActions;
    private HashSet<ObservationKey>? _imitationPath;

    private void ObserveWinningRoute(SearchNode node, SolverInterimResult quality)
    {
        if (_imitationWitness is { } previous
            && (SolverInterimResultOrdering.IsBetter(previous, quality)
                || !SolverInterimResultOrdering.IsBetter(quality, previous)
                    && _imitationActions <= node.ActionCount)) return;
        _imitationWitness = quality;
        _imitationActions = node.ActionCount;
        _imitationPath ??= [];
        _imitationPath.Clear();
        int count = 0;
        for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
        {
            if (++count > MaximumStates)
            {
                // Explicitly unavailable; a truncated path would manufacture
                // negative labels for its omitted ancestors.
                _imitationPath = null;
                return;
            }
            _imitationPath.Add(Key(cursor));
        }
    }

    internal sealed record ImitationRow(Dictionary<string, double> Features, int[] Groups,
        bool? OnWinningRoute, int FeatureSchema = 0) : RankingObservation;
    internal sealed record ImitationDocument(int Schema, int FeatureSchema, Guid GameMvid,
        SolverInterimResult? Witness, int ActionCount, string? UnavailableReason, ImitationRow[] Rows);

    internal ImitationDocument ExportImitation() => new(1, FeatureSchema,
        typeof(Player).Assembly.ManifestModule.ModuleVersionId, _imitationWitness, _imitationActions,
        _imitationWitness == null ? "no-completed-winning-route" : _imitationPath == null ? "route-exceeds-bound" : null,
        _imitationPath == null ? [] : _observations.Where(p => p.Value.Groups.Count != 0)
            .Select(p => new ImitationRow(p.Value.Features, p.Value.Groups.ToArray(),
                _imitationPath.Contains(p.Key), FeatureSchema)).ToArray());

    internal static void ValidateImitationRows(IEnumerable<ImitationRow> rows)
    {
        if (rows.Any(r => r == null || r.FeatureSchema != FeatureSchema || r.Features == null
            || r.Features.Count == 0 || r.OnWinningRoute == null || r.Groups == null || r.Groups.Length == 0
            || r.Groups.Any(g => g < 0) || r.Features.Any(p => string.IsNullOrWhiteSpace(p.Key)
                || !float.IsFinite((float)p.Value))))
            throw new InvalidDataException("Invalid winning-route imitation observation.");
    }

    internal static PreparedTraining PrepareImitationTraining(IReadOnlyList<ImitationRow[]> roots)
        => PrepareRanking(roots, ValidateImitationRows, CompareImitation);

    private static int CompareImitation(ImitationRow left, ImitationRow right, out int kind)
    {
        kind = 0; // Imitation edge, never a victory-versus-defeat outcome label.
        return left.OnWinningRoute == right.OnWinningRoute ? 0 : left.OnWinningRoute == true ? -1 : 1;
    }
}
