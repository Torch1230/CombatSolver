using System.Diagnostics;
using System.Numerics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private NoveltySearchRun _novelty => _run.Novelty ??= new();
    private sealed class NoveltySearchRun
    {
        public long Generated;
        public int PeakOpen;
        public int HorizonLeaves;
        public int NonNovelPruned;
        public int FamiliarAdmitted;
        public int OpenDropped;
        public int Wins;
        public long? FirstVictoryMs;
        public int? FirstVictoryExpanded;
        public int? MinimumCumulativeLoss;
        public int? MinimumLossExpanded;
        public long? MinimumLossMs;
        public string Stop = "not_started";
        public int NoveltyEntries;
        public int NoveltyAtoms;
        public int NoveltyPartitions;
        public long Unary;
        public long Binary;
        public long Familiar;
        public List<NoveltySearchImprovement> Improvements = [];
        public List<BfwsFact> Facts = [];
        public Dictionary<(string Id, int Upgrade), int> ZoneCounts = [];
        public List<(string Id, int Upgrade)> ZoneOrder = [];
        public NoveltySearchTelemetry Describe() => new(Generated, PeakOpen, HorizonLeaves, NonNovelPruned, FamiliarAdmitted, OpenDropped, Wins, FirstVictoryMs,
            FirstVictoryExpanded, MinimumCumulativeLoss, MinimumLossExpanded, MinimumLossMs,
            Stop, NoveltyEntries, NoveltyAtoms, NoveltyPartitions,
            Unary, Binary, Familiar, Improvements.ToArray());
    }
    private void ObserveNoveltyCandidate(SearchNode node, Stopwatch clock)
    {
        if (policy.NoveltySearch == null) return;
        _novelty.Generated++;
        var s = node.Snapshot;
        if (!s.AllEnemiesDead || s.PlayerDead || s.HasRisk || s.BoundaryReason != SearchBoundaryReason.None) return;
        _novelty.Wins++;
        _novelty.FirstVictoryMs ??= clock.ElapsedMilliseconds;
        _novelty.FirstVictoryExpanded ??= _run.Expanded;
        if (_novelty.MinimumCumulativeLoss is { } previous && previous <= s.CumulativePlayerHpLost) return;
        _novelty.MinimumCumulativeLoss = s.CumulativePlayerHpLost;
        _novelty.MinimumLossExpanded = _run.Expanded;
        _novelty.MinimumLossMs = clock.ElapsedMilliseconds;
        _novelty.Improvements.Add(new(clock.ElapsedMilliseconds, _run.Expanded,
            s.CumulativePlayerHpLost, node.PotionCount, node.Turn));
    }

    private bool RunNoveltyOpen(List<SearchNode> initial, List<SearchNode> completed,
        Stopwatch clock, ref SearchNode fallback, Func<SearchNode, bool> hpTarget,
        Func<SearchNode, int, int, bool> beforeParent,
        Action<SearchNode, int, int> afterParent, Action<SearchNode> observeBoundary,
        out bool targetReached, out int turnLayers)
    {
        NoveltySearchOptions options = policy.NoveltySearch!;
        options.Validate();
        var open = new BfwsBoundedOpen<(SearchNode Node, BfwsEscapeBudget? Escape)>(options.MaxOpen);
        var novelty = new BfwsPackedNovelty(options.Width, options.MaxNoveltyEntries);
        long sequence = 0;
        List<SearchNode> noveltyDropped = [];
        int maximumTurn = _startTurnNumber;
        bool stopped = false;
        targetReached = false;
        _novelty.Stop = "open_exhausted";
        try
        {
            foreach (SearchNode seed in initial)
            {
                maximumTurn = Math.Max(maximumTurn, seed.Turn);
                if (!seed.IsTerminal)
                {
                    Enqueue(seed, initialSeed: true);
                    continue;
                }

                _ = NoveltyFor(seed);
                if (seed.Score > fallback.Score) fallback = seed;
                observeBoundary(seed);
                completed.Add(seed);
                targetReached |= hpTarget(seed);
            }
            initial.Clear();
            if (targetReached) _novelty.Stop = "hp_target";
            while (open.Count > 0 && !targetReached)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_run.Expanded >= _profile.MaxExpandedNodes) { _novelty.Stop = "node_limit"; break; }
                if (clock.ElapsedMilliseconds >= _profile.SoftTimeBudgetMilliseconds)
                { _novelty.Stop = "time_limit"; stopped = true; break; }
                var (parent, escape) = open.Dequeue();
                try
                {
                    if (parent.ActionCount >= options.MaxActions || parent.Turn >= _startTurnNumber + options.MaxTurns)
                    { _novelty.HorizonLeaves++; continue; }
                    if (!beforeParent(parent, open.Count, completed.Count))
                    { _novelty.Stop = "adoption"; stopped = true; break; }
                    BeginCyclePlanningLayer();
                    novelty.PrepareParent((parent.Snapshot.EnemyHp / 10, parent.PotionCount), CaptureNoveltyFacts(parent));
                    List<SearchNode> children = [];
                    try
                    {
                        foreach (SearchNode generated in Expand(parent)) children.Add(generated);
                        List<SearchNode> boundaries = children.Where(n => n.IsTerminal || n.Turn > parent.Turn).ToList();
                        List<SearchNode> annotated = AnnotateTurnOutcomes(boundaries);
                        ReleaseDroppedSnapshots(boundaries, annotated);
                        children = children.Where(n => !n.IsTerminal && n.Turn == parent.Turn).Concat(annotated).ToList();
                        foreach (SearchNode child in children)
                        {
                            ObserveNoveltyCandidate(child, clock);
                            maximumTurn = Math.Max(maximumTurn, child.Turn);
                            if (child.Score > fallback.Score) fallback = child;
                            if (child.IsTerminal || child.Turn > parent.Turn)
                                observeBoundary(child);
                            if (child.IsTerminal)
                            {
                                _ = NoveltyFor(child);
                                completed.Add(child);
                                targetReached |= hpTarget(child);
                            }
                            else Enqueue(child, parentEscape: escape);
                        }
                        if (noveltyDropped.Count > 0)
                        {
                            // A published sibling can share the same snapshot. Release by snapshot
                            // identity only after every successor has reached its final owner.
                            List<SearchNode> retained = open.Values.Select(item => item.Node)
                                .Concat(completed).Append(parent).ToList();
                            ReleaseDroppedSnapshots(noveltyDropped, retained);
                            noveltyDropped.Clear();
                        }
                        children.Clear(); // Ownership moved to OPEN or completed, or released above.
                    }
                    finally { foreach (SearchNode child in children) child.Snapshot.ReleaseSimulator(); }
                    if (completed.Count > 64)
                    {
                        List<SearchNode> ranked = Retention.RankFinal(completed);
                        ReleaseDroppedSnapshots(completed, ranked);
                        completed.Clear(); completed.AddRange(ranked);
                    }
                }
                finally { parent.Snapshot.ReleaseSimulator(); }
                afterParent(parent, open.Count, completed.Count);
                if (targetReached) { _novelty.Stop = "hp_target"; break; }
                if (novelty.LimitReached) { _novelty.Stop = "novelty_limit"; break; }
            }
            turnLayers = Math.Max(0, maximumTurn - _startTurnNumber);
            return stopped;
        }
        finally
        {
            foreach (SearchNode seed in initial) seed.Snapshot.ReleaseSimulator();
            initial.Clear();
            foreach (var pending in open.Values) pending.Node.Snapshot.ReleaseSimulator();
            open.Clear();
            foreach (SearchNode dropped in noveltyDropped) dropped.Snapshot.ReleaseSimulator();
            noveltyDropped.Clear();
            _novelty.NoveltyEntries = novelty.Entries;
            _novelty.NoveltyAtoms = novelty.AtomCount;
            _novelty.NoveltyPartitions = novelty.Partitions;
            _novelty.Unary = novelty.Unary;
            _novelty.Binary = novelty.Binary;
            _novelty.Familiar = novelty.Familiar;
        }
        int NoveltyFor(SearchNode node)
            => novelty.Evaluate((node.Snapshot.EnemyHp / 10, node.PotionCount), CaptureNoveltyFacts(node));
        void Enqueue(SearchNode node, bool initialSeed = false, BfwsEscapeBudget? parentEscape = null)
        {
            int w = NoveltyFor(node);
            if (!initialSeed && w > options.Width)
            {
                if (parentEscape?.TryAdmit() != true)
                {
                    _novelty.NonNovelPruned++;
                    noveltyDropped.Add(node);
                    return;
                }
                _novelty.FamiliarAdmitted++;
            }
            BfwsEscapeBudget? escape = options.FamiliarAllowance > 0
                ? initialSeed || w <= options.Width ? new(options.FamiliarAllowance) : parentEscape
                : null;
            var rank = ((double)w, -node.Score, sequence++);
            if (open.Enqueue((node, escape), rank, out var evicted))
            {
                _novelty.OpenDropped++;
                noveltyDropped.Add(evicted.Node);
            }
            _novelty.PeakOpen = Math.Max(_novelty.PeakOpen, open.Count);
        }
    }

    private IReadOnlyList<BfwsFact> CaptureNoveltyFacts(SearchNode node)
    {
        List<BfwsFact> facts = _novelty.Facts;
        facts.Clear();
        var s = node.Snapshot;
        ReadOnlySpan<int> values = [s.Energy, s.Stars, s.HandCount, s.ZeroCostPlayableCount,
            s.ReachableHandValue, s.PersistentBuffValue, s.LatentSetupValue, s.FutureResourceValue,
            s.RetainedAttackValue, s.ReplayPotentialValue, s.DelayedDamageValue, s.ReactiveDamageValue,
            s.StrategicEffects.DamagePotential, s.StrategicEffects.PreventionPotential,
            s.StrategicEffects.ResourcePotential, s.StrategicEffects.CardAccessPotential,
            s.StrategicEffects.ScalingPotential, s.EnemyStrengthSuppression, s.EnemyWeakTurns, s.EnemyVulnerableTurns];
        for (int i = 0; i < values.Length; i++)
            if (values[i] > 0)
                facts.Add(new(BfwsFactKind.Scalar, i, Value: i < 4 || i >= 18 ? Math.Min(7, values[i]) : Math.Min(7, 1 + BitOperations.Log2((uint)values[i]))));
        facts.Add(new(BfwsFactKind.Energy, Value: s.Energy));
        facts.Add(new(BfwsFactKind.Block, Value: s.PlayerBlock / 5));
        facts.Add(new(BfwsFactKind.Hp, Value: s.PlayerHp / 5));
        var simulator = (CombatPredictionSimulator)s.Simulator;
        var playerState = simulator.State.GetPlayerCombatState(_player);
        for (int zone = 0; zone < 4; zone++)
        {
            var cards = zone switch { 0 => playerState.Hand.Cards, 1 => playerState.DrawPile.Cards,
                2 => playerState.DiscardPile.Cards, _ => playerState.ExhaustPile.Cards };
            _novelty.ZoneCounts.Clear(); _novelty.ZoneOrder.Clear();
            foreach (var card in cards)
            {
                var key = (card.Preview.Id.Entry, card.Preview.CurrentUpgradeLevel);
                if (_novelty.ZoneCounts.TryGetValue(key, out int count)) _novelty.ZoneCounts[key] = count + 1;
                else { _novelty.ZoneCounts.Add(key, 1); _novelty.ZoneOrder.Add(key); }
            }
            foreach (var key in _novelty.ZoneOrder)
                facts.Add(new(BfwsFactKind.Zone, zone, key.Id, key.Upgrade, Math.Min(3, _novelty.ZoneCounts[key])));
        }
        for (int i = 0; i < Math.Min(3, playerState.DrawPile.Cards.Count); i++)
            facts.Add(new(BfwsFactKind.Draw, i, playerState.DrawPile.Cards[i].Preview.Id.Entry));
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        foreach (var power in combat.EffectivePowers())
            facts.Add(new(BfwsFactKind.Power, ReferenceEquals(power.Owner, _player.Creature) ? 0 : 1, power.Id.Entry, power.Amount));
        return facts;
    }

}
