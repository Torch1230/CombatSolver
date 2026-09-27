namespace CombatSolver;

// One frozen root, potion policy and automatic pass. Only value evidence survives a
// member: no frontier, admission decision, SearchNode or simulator is retained here.
internal sealed class SharedSearchEvidence
{
    internal const int Capacity = 4096;
    internal const int MaximumPathLength = 256;
    private readonly Entry[] _entries = new Entry[Capacity];
    private SolverInterimResult? _bestObserved;
    public long ProbeHits { get; private set; }
    public long ProbeStores { get; private set; }
    public long OutcomeBackups { get; private set; }
    public long RankedCandidates { get; private set; }
    public long Evictions { get; private set; }
    public int Count { get; private set; }

    private struct Entry
    {
        public bool Occupied;
        public SearchEvidenceKey Key;
        public CombatBeamSolver.StandPatEvaluation? Probe;
        public SolverInterimResult? Outcome;
    }

    // A direct-mapped, fixed-capacity table. A collision discards an optimization,
    // never a search candidate. No growing dictionary or simulator graph is kept.
    private static int Slot(StateFingerprint state)
        => (int)(StateFingerprintBuilder.MixFirst(state.First ^ state.Second) & (Capacity - 1));

    private ref Entry GetOrReplace(SearchEvidenceKey key)
    {
        ref Entry entry = ref _entries[Slot(key.State)];
        if (!entry.Occupied || entry.Key != key)
        {
            if (entry.Occupied) Evictions++;
            else Count++;
            entry = new Entry { Occupied = true, Key = key };
        }
        return ref entry;
    }

    internal bool TryProbe(SearchEvidenceKey key, out CombatBeamSolver.StandPatEvaluation value)
    {
        ref Entry entry = ref _entries[Slot(key.State)];
        if (entry.Occupied && entry.Key == key && entry.Probe is { } probe)
        {
            ProbeHits++;
            value = probe;
            return true;
        }
        value = default;
        return false;
    }

    internal void StoreProbe(SearchEvidenceKey key, CombatBeamSolver.StandPatEvaluation value)
    {
        if (!value.Reusable) return;
        GetOrReplace(key).Probe = value;
        ProbeStores++;
    }

    internal void StoreOutcome(SearchEvidenceKey key, SolverInterimResult outcome)
    {
        if (!outcome.Won || !outcome.Survives) return;
        ref Entry entry = ref GetOrReplace(key);
        if (entry.Outcome == null || Better(outcome, entry.Outcome))
        {
            entry.Outcome = outcome.Score == 0 ? outcome : outcome with { Score = 0 };
            OutcomeBackups++;
        }
    }

    internal bool TryOutcome(SearchEvidenceKey key, out SolverInterimResult? outcome)
    {
        ref Entry entry = ref _entries[Slot(key.State)];
        outcome = entry.Occupied && entry.Key == key ? entry.Outcome : null;
        return outcome != null;
    }

    internal void ObserveVictory(SearchNode terminal, SolverInterimResult outcome)
    {
        if (terminal.HasPredictionRisk || terminal.BoundaryReason != SearchBoundaryReason.None
            || terminal.ActionCount > MaximumPathLength || !outcome.Won || !outcome.Survives)
            return;
        // Only improving completed witnesses need another parent walk. Observations
        // are feasible continuations, not bounds or samples of an expected return.
        outcome = outcome with { Score = 0 };
        if (_bestObserved != null && !Better(outcome, _bestObserved)) return;
        _bestObserved = outcome;
        for (SearchNode? node = terminal.Parent; node != null; node = node.Parent)
            if (IsUsable(node)) StoreOutcome(SearchEvidenceKey.Capture(node), outcome);
    }

    internal SolverInterimResult? OutcomeFor(SearchNode node)
    {
        if (!IsUsable(node)) return null;
        // Most nodes have no witness. Avoid walking a path on the cold lookup.
        ref Entry entry = ref _entries[Slot(node.StateKey)];
        if (!entry.Occupied || entry.Key.State != node.StateKey || entry.Outcome == null)
            return null;
        return TryOutcome(SearchEvidenceKey.Capture(node), out var outcome) ? outcome : null;
    }

    internal static bool IsUsable(SearchNode node)
        => !node.IsTerminal && !node.HasPredictionRisk
            && node.BoundaryReason == SearchBoundaryReason.None
            && node.ActionCount <= MaximumPathLength;

    internal static bool Better(SolverInterimResult left, SolverInterimResult right)
        => CombatSearchCoordinator.IsBetterPotionPolicyResult(left.TheftPolicy,
            left.Score == 0 ? left : left with { Score = 0 },
            right.Score == 0 ? right : right with { Score = 0 });

    // Alternate empirical and heuristic priorities. Unknown paths keep their original
    // relative order and exploration opportunities; final routes keep their positions.
    // This only changes intermediate ordering, never exact dominance or final policy.
    internal void Rank(List<SearchNode> ranked)
    {
        if (OutcomeBackups == 0 || ranked.Count < 2) return;
        List<(SearchNode Node, SolverInterimResult Quality, int Rank)>? known = null;
        for (int i = 0; i < ranked.Count; i++)
            if (OutcomeFor(ranked[i]) is { } quality)
                (known ??= []).Add((ranked[i], quality, i));
        if (known == null) return;
        RankedCandidates += known.Count;
        known.Sort(static (a, b) => Better(a.Quality, b.Quality) ? -1
            : Better(b.Quality, a.Quality) ? 1 : a.Rank.CompareTo(b.Rank));
        HashSet<SearchNode> observed = new(known.Select(k => k.Node), ReferenceEqualityComparer.Instance);
        Queue<SearchNode> unknown = new(ranked.Where(n => !n.IsTerminal && !observed.Contains(n)));
        int witness = 0;
        bool preferWitness = true;
        for (int i = 0; i < ranked.Count; i++)
        {
            if (ranked[i].IsTerminal) continue;
            if (witness < known.Count && (preferWitness || unknown.Count == 0))
                ranked[i] = known[witness++].Node;
            else ranked[i] = unknown.Dequeue();
            preferWitness = !preferWitness;
        }
    }

    internal string Describe()
        => $"entries={Count} capacity={Capacity} probe_hits={ProbeHits} probe_stores={ProbeStores} "
            + $"outcome_backups={OutcomeBackups} ranked_candidates={RankedCandidates} evictions={Evictions}";
}

// Stronger than a transposition key: identical state AND full action/choice history,
// including root setup, physical cards, RNG-bearing states and policy accumulators.
// Hashing uses the same 128-bit identity mechanism as the existing state table.
internal readonly record struct SearchEvidenceKey(StateFingerprint State, StateFingerprint Path)
{
    internal static SearchEvidenceKey Capture(SearchNode node)
    {
        StateFingerprintBuilder key = new();
        for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
        {
            key.Add(cursor.StateKey.First);
            key.Add(cursor.StateKey.Second);
            key.Add(cursor.ActionCount);
            key.Add(cursor.Turn);
            key.Add(cursor.PotionCount);
            key.Add(cursor.PotionStrategicCost);
            key.Add(cursor.FutureSoldHp);
            key.Add(cursor.Snapshot.CumulativePlayerHpLost);
            key.Add(cursor.Snapshot.ShufflesCrossed);
            AppendAction(ref key, cursor.Action);
            AppendChoices(ref key, cursor.TurnSetupChoices);
        }
        return new(node.StateKey, key.Finish());
    }

    internal static void AppendAction(ref StateFingerprintBuilder key, PlanAction? action)
    {
        key.Add(action != null);
        if (action == null) return;
        key.Add((int)action.Kind);
        key.Add(action.Turn);
        key.Add(action.CardId);
        key.Add(action.CardOccurrence);
        key.Add(action.CardStateKey);
        key.Add(action.CardStateOccurrence);
        key.Add(action.CardUpgradeLevel);
        key.Add(action.CardEnchantmentId);
        key.Add(action.TargetIndex);
        key.Add(action.TargetCombatId.HasValue);
        key.Add(action.TargetCombatId.GetValueOrDefault());
        key.Add(action.PotionId);
        key.Add(action.PotionSlot);
        key.Add(action.ReplayCount);
        key.Add(action.EndsPlayerTurn);
        key.Add(action.NestedChoicesBeforePrimary);
        AppendChoice(ref key, action.Choice);
        AppendChoices(ref key, action.NestedChoices);
        AppendChoices(ref key, action.TurnStartChoices);
    }

    private static void AppendChoices(ref StateFingerprintBuilder key, IReadOnlyList<PlanCardChoice>? choices)
    {
        key.Add(choices?.Count ?? 0);
        if (choices != null)
            foreach (var choice in choices) AppendChoice(ref key, choice);
    }

    private static void AppendChoice(ref StateFingerprintBuilder key, PlanCardChoice? choice)
    {
        key.Add(choice != null);
        if (choice == null) return;
        key.Add((int)choice.Effect);
        key.Add((int)choice.SourcePile);
        key.Add(choice.SourceId);
        key.Add(choice.ContextId);
        key.Add((int)choice.Timing);
        key.Add(choice.Cards.Count);
        foreach (var card in choice.Cards)
        {
            key.Add(card.CardId);
            key.Add(card.UpgradeLevel);
            key.Add(card.StateKey);
            key.Add(card.SourceOccurrence);
            key.Add(card.OptionOccurrence);
        }
    }
}
