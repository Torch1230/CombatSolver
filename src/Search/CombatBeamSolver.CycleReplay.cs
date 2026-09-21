using System.Diagnostics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // An additional terminal probe, not a replacement for any ordinary continuation or exit.
    // Disabled when the caller requests exhaustive quality comparison instead of HP-target stopping.
    private SearchNode? TryReplayCycleVictory(SearchNode seed, Stopwatch clock)
    {
        if (!policy.CanStopAtHpTarget || seed.Cycle is not { Repetitions: >= 2 } cycle
            || seed.IsTerminal || seed.BoundaryReason != SearchBoundaryReason.None
            || cycle.PriorCycleEndpoint is not { } prior
            || !policy.GrowthTargetSatisfied(seed.Snapshot.GrowthRewards)
            || !policy.RelicTargetsSatisfied(seed.Snapshot.RelicCounters)
            || !TheftEncounterStrategy.RecoverySatisfied(_theftPolicy, seed.Snapshot.OutstandingStolenResource)
            || battleDamage.HpLostSoFar + seed.Snapshot.CumulativePlayerHpLost > _acceptableBattleHpLoss
            || seed.Snapshot.CumulativePlayerHpLost != 0
            || seed.FutureSoldHp != 0
            || seed.Snapshot.CumulativePlayerHpLost != prior.Snapshot.CumulativePlayerHpLost
            || seed.Snapshot.PlayerMaxHp < prior.Snapshot.PlayerMaxHp
            || seed.Snapshot.Energy < prior.Snapshot.Energy || seed.Snapshot.Stars < prior.Snapshot.Stars
            || _run.CycleReplayActions >= 4096)
            return null;

        long damage = EnemyDurabilityProgress.PositiveReduction(
            prior.Snapshot.EnemyDurabilityByCombatId, seed.Snapshot.EnemyDurabilityByCombatId);
        if (damage <= 0)
            return null;
        CycleRegionKey region = new(seed.Turn, cycle.ShapeKey);
        if (_run.CycleReplayRegions.Contains(region)) return null;
        PlanAction[] sequence = new PlanAction[cycle.PeriodActions];
        SearchNode cursor = seed;
        for (int i = sequence.Length - 1; i >= 0; i--, cursor = cursor.Parent!)
        {
            if (cursor.Action is not { Kind: PlanActionKind.PlayCard, EndsPlayerTurn: false } action
                || action.Choice != null || action.NestedChoices is { Count: > 0 }
                || action.TurnStartChoices is { Count: > 0 })
                return null;
            sequence[i] = action;
        }
        _run.CycleReplayRegions.Add(region);
        _run.CycleReplayAttempts++;
        long remaining = Math.Max(1L, (long)seed.Snapshot.EnemyHp + seed.Snapshot.EnemyBlock);
        int limit = (int)Math.Min(4096 - _run.CycleReplayActions,
            Math.Max(sequence.Length, (remaining / damage + 2) * sequence.Length));
        SearchNode current = seed;
        bool published = false;
        try
        {
            for (int index = 0; index < limit; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds >= _profile.SoftTimeBudgetMilliseconds
                    || policy.MemoryPressureSignal.IsLimitReached())
                    break;
                PlanAction template = sequence[index % sequence.Length];
                CombatPredictionSimulator simulator = current.Snapshot.Simulator;
                SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
                IReadOnlyList<PredictedCard> hand = simulator.State.GetPlayerCombatState(_player).Hand.Cards;
                PredictedCard? card = FindCardOccurrence(hand, template.CardId, template.CardOccurrence);
                if (card == null || !combat.CanPlayCard(simulator, card))
                    break;
                // Never jump over a genuine card/target decision. Equivalent duplicates use
                // the ordinary expansion identity; EndTurn remains in the normal frontier.
                StateFingerprint playableKey = BuildPlayableCardKey(card);
                bool alternative = false;
                foreach (PredictedCard other in hand)
                {
                    if (!ReferenceEquals(other, card) && combat.CanPlayCard(simulator, other)
                        && BuildPlayableCardKey(other) != playableKey)
                    {
                        alternative = true;
                        break;
                    }
                }
                if (alternative) break;
                int targets = 0;
                bool matchesTarget = false;
                foreach (var target in TargetsFor(card, simulator))
                {
                    targets++;
                    matchesTarget |= target.Item2?.CombatId == template.TargetCombatId;
                }
                if (targets != 1 || !matchesTarget) break;
                string stateKey = CardChoiceSupport.ChoiceCardKey(card);
                int stateOccurrence = 0;
                foreach (PredictedCard preceding in hand)
                {
                    if (ReferenceEquals(preceding, card)) break;
                    if (CardChoiceSupport.ChoiceCardKey(preceding) == stateKey) stateOccurrence++;
                }
                PlanAction action = template with
                {
                    CardStateKey = stateKey,
                    CardStateOccurrence = stateOccurrence,
                    CardUpgradeLevel = card.Preview.CurrentUpgradeLevel,
                    CardEnchantmentId = card.Preview.Enchantment?.Id.Entry ?? "",
                    ReplayCount = Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                };
                SimulationSnapshot snapshot = ReplayAction(current, action);
                _run.CycleReplayActions++;
                if (snapshot.BoundaryReason != SearchBoundaryReason.None || snapshot.Turn != seed.Turn
                    || snapshot.PlayerDead || snapshot.HasRisk
                    || snapshot.CumulativePlayerHpLost != seed.Snapshot.CumulativePlayerHpLost
                    || snapshot.PlayerMaxHp < seed.Snapshot.PlayerMaxHp)
                {
                    snapshot.ReleaseSimulator();
                    break;
                }
                SearchNode next = new(action, current.ActionCount + 1,
                    snapshot.PotionUseCount, snapshot.PotionStrategicCost, snapshot.Turn,
                    current.Traits, current.FutureSoldHp,
                    ApplySoldHpPenalty(snapshot.Score, current.FutureSoldHp), snapshot.StateKey,
                    snapshot.HasRisk, snapshot.BoundaryReason, snapshot.AllEnemiesDead,
                    current, snapshot, current.CombatProgress)
                {
                    CumulativeEnemyHpLost = AccumulateEnemyHpLost(current, snapshot),
                };
                if (!ReferenceEquals(current, seed)) current.Snapshot.ReleaseSimulator();
                current = next;
                if (snapshot.AllEnemiesDead)
                {
                    _run.CycleReplayVictories++;
                    published = true;
                    return current;
                }
                if ((index + 1) % sequence.Length == 0
                    && (snapshot.CycleShapeKey != seed.Snapshot.CycleShapeKey
                        || snapshot.Energy < seed.Snapshot.Energy || snapshot.Stars < seed.Snapshot.Stars))
                    break;
            }
            return null;
        }
        finally
        {
            if (!published && !ReferenceEquals(current, seed)) current.Snapshot.ReleaseSimulator();
        }
    }
}
