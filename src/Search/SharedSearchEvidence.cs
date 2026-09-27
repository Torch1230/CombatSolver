namespace CombatSolver;

// One frozen root, potion policy and automatic pass. Only value evidence survives a
// member: no frontier, admission decision, SearchNode or simulator is retained here.
internal sealed class SharedSearchEvidence
{
    internal const int Capacity = 4096;
    internal const int MaximumPathLength = 256;
    internal const int MaximumOutcomeEvents = 2048;
    private readonly Entry[] _entries = new Entry[Capacity];
    private int _outcomeEvents;
    public long ProbeHits { get; private set; }
    public long ProbeStores { get; private set; }
    public long OutcomeBackups { get; private set; }
    public long RankedCandidates { get; private set; }
    public long ReorderedCandidates { get; private set; }
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

    // Reject cold states before hashing their full history. The complete identity
    // must still match in TryProbe; this filter can never authorize reuse.
    internal bool MayHaveProbe(StateFingerprint state)
    {
        ref Entry entry = ref _entries[Slot(state)];
        return entry.Occupied && entry.Key.State == state && entry.Probe.HasValue;
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
            || terminal.ActionCount > MaximumPathLength || !outcome.Won || !outcome.Survives
            || _outcomeEvents >= MaximumOutcomeEvents || terminal.Parent == null)
            return;
        _outcomeEvents++;
        // Learn each observed branch, including those worse than the global incumbent.
        // An existing better witness at its immediate parent already covered this path.
        outcome = outcome with { Score = 0 };
        if (OutcomeFor(terminal.Parent) is { } previous && !Better(outcome, previous)) return;
        Span<SearchEvidenceKey> keys = stackalloc SearchEvidenceKey[MaximumPathLength + 1];
        SearchEvidenceKey.CaptureAncestors(terminal.Parent, keys);
        int index = 0;
        for (SearchNode? node = terminal.Parent; node != null; node = node.Parent)
        {
            if (IsUsable(node)) StoreOutcome(keys[index], outcome);
            index++;
        }
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

    // A completed continuation is a feasible witness, not an estimate of all
    // continuations. Use it only to break existing full Beam ties; promoting it
    // across unequal ranks can suppress a branch with a better unseen outcome.
    // Unknown and terminal candidates retain their original positions.
    internal void RankTies(List<(SearchNode Node, double Score)> ranked)
    {
        if (OutcomeBackups == 0 || ranked.Count < 2) return;
        for (int start = 0; start < ranked.Count;)
        {
            int end = start + 1;
            var first = ranked[start];
            while (end < ranked.Count && CombatBeamSolver.CompareBeamRankOrder(
                first.Score, first.Node.Snapshot.OffensiveProgressValue, first.Node.ActionCount,
                ranked[end].Score, ranked[end].Node.Snapshot.OffensiveProgressValue,
                ranked[end].Node.ActionCount) == 0)
                end++;
            if (end - start > 1) RankTie(ranked, start, end);
            start = end;
        }
    }

    private void RankTie(List<(SearchNode Node, double Score)> ranked, int start, int end)
    {
        List<(SearchNode Node, SolverInterimResult Quality, int Rank)>? known = null;
        for (int i = start; i < end; i++)
            if (OutcomeFor(ranked[i].Node) is { } quality)
                (known ??= []).Add((ranked[i].Node, quality, i));
        if (known == null) return;
        RankedCandidates += known.Count;
        if (known.Count < 2) return;
        int[] positions = known.Select(k => k.Rank).ToArray();
        known.Sort(static (a, b) => Better(a.Quality, b.Quality) ? -1
            : Better(b.Quality, a.Quality) ? 1 : a.Rank.CompareTo(b.Rank));
        for (int i = 0; i < known.Count; i++)
        {
            int position = positions[i];
            if (!ReferenceEquals(ranked[position].Node, known[i].Node)) ReorderedCandidates++;
            ranked[position] = (known[i].Node, ranked[position].Score);
        }
    }

    internal string Describe()
        => $"entries={Count} capacity={Capacity} probe_hits={ProbeHits} probe_stores={ProbeStores} "
            + $"outcome_backups={OutcomeBackups} ranked_candidates={RankedCandidates} "
            + $"reordered_candidates={ReorderedCandidates} evictions={Evictions}";
}

// Stronger than a transposition key: identical state AND full action/choice history,
// including root setup, physical cards, RNG-bearing states and policy accumulators.
// Hashing uses the same 128-bit identity mechanism as the existing state table.
internal readonly record struct SearchEvidenceKey(StateFingerprint State, StateFingerprint Path)
{
    internal static SearchEvidenceKey Capture(SearchNode node)
    {
        Span<SearchEvidenceKey> keys = stackalloc SearchEvidenceKey[SharedSearchEvidence.MaximumPathLength + 1];
        CaptureAncestors(node, keys);
        return keys[0];
    }

    // Build every ancestor identity in one pass, instead of rescanning the whole
    // history once for each backed-up node. The buffer holds only fixed-size values.
    internal static void CaptureAncestors(SearchNode node, Span<SearchEvidenceKey> keys)
    {
        int count = 0;
        for (SearchNode? cursor = node; cursor != null; cursor = cursor.Parent)
        {
            if (count == keys.Length)
                throw new InvalidOperationException("Evidence path exceeds its bounded capture buffer.");
            StateFingerprintBuilder key = new();
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
            keys[count++] = new(cursor.StateKey, key.Finish());
        }
        StateFingerprint prefix = default;
        for (int i = count - 1; i >= 0; i--)
        {
            StateFingerprintBuilder key = new();
            key.Add(prefix.First);
            key.Add(prefix.Second);
            key.Add(keys[i].Path.First);
            key.Add(keys[i].Path.Second);
            prefix = key.Finish();
            keys[i] = keys[i] with { Path = prefix };
        }
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
