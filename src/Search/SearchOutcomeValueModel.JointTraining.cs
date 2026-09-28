namespace CombatSolver;

internal sealed partial class SearchOutcomeValueModel
{
    // The two targets keep their own observations and pool namespaces. Matching
    // feature vectors cannot certify that two underlying search states are equal.
    internal sealed record JointObservation(RankingObservation Source, int[] Groups, bool IsCorrection = false) : RankingObservation
    {
        public Dictionary<string, double> Features => Source.Features;
        public int FeatureSchema => Source.FeatureSchema;
    }

    internal static JointObservation[] JoinTrainingRoot(TrainingRow[] outcomes, ImitationRow[] imitation,
        TrainingRow[]? corrections = null)
    {
        ValidateTrainingRows(outcomes);
        ValidateImitationRows(imitation);
        if (corrections is { Length: > 6 })
            throw new InvalidDataException("A root has at most six detached correction queries.");
        if (corrections != null) ValidateTrainingRows(corrections);
        List<JointObservation> rows = [];
        Dictionary<(int Target, int Group), int> groups = [];
        void Add(RankingObservation row, int target)
        {
            int Remap(int group)
            {
                var key = (target, group);
                if (!groups.TryGetValue(key, out int value)) groups.Add(key, value = groups.Count);
                return value;
            }
            rows.Add(new(row, row.Groups.Select(Remap).ToArray(), IsCorrection: target == 2));
        }
        foreach (var row in outcomes) Add(row, 0);
        foreach (var row in imitation) Add(row, 1);
        if (corrections != null)
            foreach (var row in corrections) Add(row, 2);
        return rows.ToArray();
    }

    internal static PreparedTraining PrepareJointTraining(IReadOnlyList<JointObservation[]> roots,
        bool balanceCorrections = false)
        => PrepareRanking(roots, ValidateJointRows, CompareJoint, kindCount: 4,
            correctionSource: balanceCorrections ? row => row.IsCorrection : null);

    private static void ValidateJointRows(IEnumerable<JointObservation> rows)
    {
        var array = rows.ToArray();
        if (array.Any(r => r == null || r.Groups == null || r.Groups.Length == 0
            || r.Source is not (TrainingRow or ImitationRow)
            || r.IsCorrection && r.Source is not TrainingRow)
            || array.Count(r => r.IsCorrection) > 6)
            throw new InvalidDataException("Invalid joint ranking observation.");
        ValidateTrainingRows(array.Select(r => r.Source).OfType<TrainingRow>());
        ValidateImitationRows(array.Select(r => r.Source).OfType<ImitationRow>());
    }

    private static int CompareJoint(JointObservation left, JointObservation right, out int kind)
    {
        if (left.IsCorrection != right.IsCorrection)
            throw new InvalidDataException("Independent correction collections must not share preference pools.");
        if (left.Source is TrainingRow a && right.Source is TrainingRow b)
            return CompareWitnesses(a, b, out kind);
        if (left.Source is ImitationRow c && right.Source is ImitationRow d)
        {
            int order = CompareImitation(c, d, out _);
            kind = 3; // Separate from all three completed-outcome preference kinds.
            return order;
        }
        throw new InvalidDataException("Independent target pools must not create cross-target preferences.");
    }
}
