using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;


internal sealed partial class CombatBeamSolver
{
    private ActionCandidate BuildCandidate(
        SimulationSnapshot before,
        SimulationSnapshot after,
        SearchNode node,
        CardType cardType,
        uint? targetCombatId)
    {
        int energy = Math.Max(0, before.Energy - after.Energy);
        int stars = Math.Max(0, before.Stars - after.Stars);
        int damage = Math.Max(0, before.EnemyHp - after.EnemyHp);
        int block = Math.Max(0, after.PlayerBlock - before.PlayerBlock);
        double resource = Math.Max(0.5d, energy + stars * 0.5d);
        double normalized = (damage + block * 0.8d) / resource;
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)after.Simulator;
        bool pure = true;
        foreach (CombatPredictionHistoryEntry entry in
                 simulator.History.EntriesFrom(before.HistoryEntryCount))
        {
            if (IsPureHistoryEntry(entry))
                continue;
            pure = false;
            break;
        }
        SimulatedCombatState beforeCombat = (SimulatedCombatState)
            ((CombatPredictionSimulator)before.Simulator).State.CombatState;
        bool declinedExtraTurn = beforeCombat.RelicsOf(_player)
            .OfType<PaelsEye>()
            .Any(relic => !relic.IsMelted && beforeCombat.IsPaelsEyeUnused(relic));
        SearchRouteTraits traits = ClassifyCardTraits(
            node.Traits,
            before,
            after,
            pure,
            declinedExtraTurn);
        ActionOptionFamily optionFamilies = ClassifyActionOptionFamilies(
            cardType,
            targetCombatId,
            before,
            after,
            damage,
            block,
            pure);
        return new ActionCandidate(
            node with { Traits = traits },
            cardType,
            targetCombatId,
            energy,
            stars,
            damage,
            block,
            after.PlayerHp,
            after.PlayerMaxHp,
            after.CumulativePlayerHpLost,
            after.LongTermResourceValue,
            after.AngerCopiesGenerated,
            optionFamilies,
            pure,
            normalized);
    }

    private List<ActionCandidate> SelectActionCandidates(
        SearchNode parent,
        List<ActionCandidate> candidates)
    {
        candidates.Sort(static (left, right) =>
        {
            int byScore = right.Node.Score.CompareTo(left.Node.Score);
            return byScore != 0
                ? byScore
                : right.NormalizedValue.CompareTo(left.NormalizedValue);
        });
        int limit = Math.Min(_profile.MaxCardBranchesPerNode, candidates.Count);
        List<ActionCandidate> selected = new(limit + 2);

        void Add(ActionCandidate candidate, bool allowOverflow = false)
        {
            if (!allowOverflow && selected.Count >= limit)
                return;
            // A capturing predicate allocated once per admission attempt, including
            // duplicate representatives. Keep the original reference-identity test.
            for (int index = 0; index < selected.Count; index++)
                if (ReferenceEquals(selected[index].Node, candidate.Node))
                    return;
            selected.Add(candidate);
        }

        // A stolen-resource carrier can have no immediate attack threat while it is
        // preparing to flee. Preserve at least one actionable branch against it before
        // the ordinary action-family quota fills the node.
        if (_theftPolicy == SolverTheftPolicy.PreserveResources)
        {
            foreach (ActionCandidate candidate in candidates)
                if (IsStolenResourceRecoveryTarget(candidate))
                    Add(candidate);
        }

        // A resolved routing choice and a revival window are semantic branch boundaries. Preserve
        // the previous overflow behavior for them; the ordinary family portfolio remains inside the
        // configured per-node card branch budget.
        foreach (ActionCandidate candidate in candidates)
        {
            if (CurrentTurnRoutingChoice(candidate.Node) != null)
                Add(candidate, allowOverflow: true);
        }
        ActionCandidate? revivalWindowCandidate = null;
        foreach (ActionCandidate candidate in candidates)
        {
            SimulationSnapshot snapshot = candidate.Node.Snapshot;
            if (snapshot.RevivingEnemyCount <= parent.Snapshot.RevivingEnemyCount)
                continue;
            if (revivalWindowCandidate is { } current)
            {
                SimulationSnapshot best = current.Node.Snapshot;
                int comparison = best.RevivingEnemyCount.CompareTo(snapshot.RevivingEnemyCount);
                if (comparison == 0)
                    comparison = snapshot.RawEnemyHp.CompareTo(best.RawEnemyHp);
                if (comparison == 0)
                    comparison = snapshot.MaxCurrentEnemyHp.CompareTo(best.MaxCurrentEnemyHp);
                if (comparison == 0)
                    comparison = best.ProjectedPlayerHp.CompareTo(snapshot.ProjectedPlayerHp);
                // Stable first minimum, matching OrderBy/ThenBy/FirstOrDefault.
                if (comparison >= 0)
                    continue;
            }
            revivalWindowCandidate = candidate;
        }
        if (revivalWindowCandidate is { } revivalCandidate)
            Add(revivalCandidate, allowOverflow: true);

        ReadOnlySpan<ActionOptionFamily> families =
        [
            ActionOptionFamily.ImmediateDefense,
            ActionOptionFamily.ImmediateOffense,
            ActionOptionFamily.ResourceAndCycle,
            ActionOptionFamily.PersistentSetup,
            ActionOptionFamily.Control,
            ActionOptionFamily.TargetRemoval,
            ActionOptionFamily.HpInvestment,
        ];
        foreach (ActionOptionFamily family in families)
        {
            foreach (ActionCandidate candidate in candidates)
            {
                if (!candidate.OptionFamilies.HasFlag(family))
                    continue;
                Add(candidate);
                break;
            }
        }

        foreach (IGrouping<uint, ActionCandidate> targetGroup in candidates
                     .Where(candidate => candidate.TargetCombatId.HasValue && candidate.Damage > 0)
                     .GroupBy(candidate => candidate.TargetCombatId!.Value))
        {
            Add(targetGroup.First());
        }

        foreach (ActionCandidate candidate in candidates)
            Add(candidate);

        int baselineCount = Math.Min(limit, candidates.Count);
        HashSet<SearchNode> baseline = new(ReferenceEqualityComparer.Instance);
        foreach (ActionCandidate candidate in candidates.Take(baselineCount))
            baseline.Add(candidate.Node);
        _run.ActionAdmissionRepresentativesProtected += selected.Count(candidate =>
            !baseline.Contains(candidate.Node));
        if (_detailedDiagnostics && parent.ActionCount <= 1)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Debug] ACTION_ADMISSION prefix=" +
                $"{string.Join('>', parent.Actions.Select(PolicyActionToken))} " +
                $"candidates={candidates.Count} " +
                $"limit={_profile.MaxCardBranchesPerNode} selected={selected.Count} " +
                $"protected={selected.Count(candidate => !baseline.Contains(candidate.Node))} " +
                $"portfolio={string.Join(',', selected.Select(candidate =>
                    $"{candidate.Node.Action?.CardId ?? "-"}:{candidate.OptionFamilies}:" +
                    $"{candidate.Node.Score:F0}/{candidate.NormalizedValue:F1}"))} " +
                $"admissible={string.Join(',', candidates.Select(candidate =>
                    $"{candidate.Node.Action?.CardId ?? "-"}:{candidate.OptionFamilies}:" +
                    $"{candidate.Node.Score:F0}/{candidate.NormalizedValue:F1}"))}");
        }
        return selected;
    }

    private static bool IsStolenResourceRecoveryTarget(ActionCandidate candidate)
    {
        if (candidate.TargetCombatId is not uint targetCombatId)
            return false;

        SearchNode parent = candidate.Node.Parent
            ?? throw new InvalidOperationException("资源追回动作缺少父节点。");
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)parent.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        Creature? target = combat.Enemies.FirstOrDefault(enemy => enemy.CombatId == targetCombatId);
        return target != null
            && combat.ContainsCreature(target)
            && simulator.State.GetCreature(target).IsAlive
            && combat.EffectivePowers().Any(power =>
                power.Owner == target
                && (power is HeistPower { Amount: > 0 }
                    || power is SwipePower { StolenCard: not null }));
    }

    private static ActionOptionFamily ClassifyActionOptionFamilies(
        CardType cardType,
        uint? targetCombatId,
        SimulationSnapshot before,
        SimulationSnapshot after,
        int damage,
        int block,
        bool pure)
    {
        ActionOptionFamily families = ActionOptionFamily.None;
        if (block > 0
            || after.ProjectedPlayerHp > before.ProjectedPlayerHp
            || after.PlayerHp > before.PlayerHp
            || after.StrategicEffects.PreventionPotential
                > before.StrategicEffects.PreventionPotential)
        {
            families |= ActionOptionFamily.ImmediateDefense;
        }
        if (damage > 0
            || after.StrategicEffects.DamagePotential > before.StrategicEffects.DamagePotential)
            families |= ActionOptionFamily.ImmediateOffense;
        if (after.Energy > before.Energy
            || after.Stars > before.Stars
            || after.HandCount >= before.HandCount
            || after.ReachableHandValue > before.ReachableHandValue
            || after.ZeroCostPlayableCount > before.ZeroCostPlayableCount
            || after.FutureResourceValue > before.FutureResourceValue
            || after.StrategicEffects.ResourcePotential > before.StrategicEffects.ResourcePotential
            || after.StrategicEffects.CardAccessPotential > before.StrategicEffects.CardAccessPotential
            || after.LiveDeckClutter < before.LiveDeckClutter)
        {
            families |= ActionOptionFamily.ResourceAndCycle;
        }
        if (cardType == CardType.Power
            || after.PersistentBuffValue > before.PersistentBuffValue
            || after.DelayedDamageValue > before.DelayedDamageValue
            || after.ReplayPotentialValue > before.ReplayPotentialValue
            || after.ReactiveDamageValue > before.ReactiveDamageValue
            || after.StrategicEffects.RetentionValue > before.StrategicEffects.RetentionValue
            || after.LongTermResourceValue > before.LongTermResourceValue)
        {
            families |= ActionOptionFamily.PersistentSetup;
        }
        if (after.SandpitRemaining > before.SandpitRemaining
            || after.EnemyStrengthSuppression > before.EnemyStrengthSuppression
            || after.EnemyWeakTurns > before.EnemyWeakTurns
            || after.EnemyVulnerableTurns > before.EnemyVulnerableTurns
            || after.LiveDeckClutter < before.LiveDeckClutter
            || !pure && damage == 0 && block == 0)
        {
            families |= ActionOptionFamily.Control;
        }
        if (targetCombatId.HasValue
            && (after.AliveEnemyCount < before.AliveEnemyCount
                || after.FocusTargetCurrentThreat < before.FocusTargetCurrentThreat))
        {
            families |= ActionOptionFamily.TargetRemoval;
        }
        if (after.PlayerHp < before.PlayerHp
            || after.PlayerMaxHp < before.PlayerMaxHp
            || after.CumulativePlayerHpLost > before.CumulativePlayerHpLost)
        {
            families |= ActionOptionFamily.HpInvestment;
        }
        return families;
    }

    private static SearchRouteTraits ClassifyCardTraits(
        SearchRouteTraits current,
        SimulationSnapshot before,
        SimulationSnapshot after,
        bool pure,
        bool declinedExtraTurn)
    {
        SearchRouteTraits traits = current;
        if (declinedExtraTurn)
            traits |= SearchRouteTraits.DeclinedExtraTurn;
        if (after.PersistentBuffValue > before.PersistentBuffValue
            || after.DelayedDamageValue > before.DelayedDamageValue
            || after.ReplayPotentialValue > before.ReplayPotentialValue
            || after.LongTermResourceValue > before.LongTermResourceValue)
        {
            traits |= SearchRouteTraits.Scaling;
        }
        if (after.LongTermResourceValue > before.LongTermResourceValue)
            traits |= SearchRouteTraits.LongTermResource;
        if (after.PlayerHp < before.PlayerHp
            || after.PlayerMaxHp < before.PlayerMaxHp
            || after.CumulativePlayerHpLost > before.CumulativePlayerHpLost)
        {
            traits |= SearchRouteTraits.HpInvestment;
        }
        if (after.ReactiveDamageValue > before.ReactiveDamageValue)
            traits |= SearchRouteTraits.ReactiveDamage;
        if (after.Energy > before.Energy
            || after.Stars > before.Stars
            || after.HandCount >= before.HandCount
            || after.ReachableHandValue > before.ReachableHandValue
            || after.ZeroCostPlayableCount > before.ZeroCostPlayableCount
            || after.FutureResourceValue > before.FutureResourceValue)
        {
            traits |= SearchRouteTraits.Resource;
        }
        if (after.SandpitRemaining > before.SandpitRemaining
            || after.EnemyStrengthSuppression > before.EnemyStrengthSuppression
            || after.EnemyWeakTurns > before.EnemyWeakTurns
            || after.EnemyVulnerableTurns > before.EnemyVulnerableTurns
            || after.LiveDeckClutter < before.LiveDeckClutter
            || after.DelayedDamageValue > before.DelayedDamageValue
            || !pure)
        {
            traits |= SearchRouteTraits.Control;
        }
        if (OpensRevivalWindow(before, after))
        {
            traits |= SearchRouteTraits.RevivalWindow;
        }
        return traits;
    }

    private static SearchRouteTraits ClassifyPotionTraits(
        SearchRouteTraits current,
        SimulationSnapshot before,
        SimulationSnapshot after)
    {
        SearchRouteTraits traits = current;
        if (after.PersistentBuffValue > before.PersistentBuffValue
            || after.DelayedDamageValue > before.DelayedDamageValue)
        {
            traits |= SearchRouteTraits.Scaling;
        }
        if (after.Energy > before.Energy
            || after.Stars > before.Stars
            || after.HandCount > before.HandCount
            || after.FutureResourceValue > before.FutureResourceValue)
        {
            traits |= SearchRouteTraits.Resource;
        }
        if (after.SandpitRemaining > before.SandpitRemaining
            || after.EnemyStrengthSuppression > before.EnemyStrengthSuppression
            || after.EnemyWeakTurns > before.EnemyWeakTurns
            || after.LiveDeckClutter < before.LiveDeckClutter
            || after.DelayedDamageValue > before.DelayedDamageValue
            || after.EnemyHp == before.EnemyHp && after.PlayerBlock == before.PlayerBlock)
        {
            traits |= SearchRouteTraits.Control;
        }
        if (before.Energy == 0
            && after.HandCount == 0
            && before.LiveDeckSize - after.LiveDeckSize >= 6
            && before.PocketwatchCardThreshold >= 0
            && before.PocketwatchCardsPlayedThisTurn == before.PocketwatchCardThreshold)
        {
            traits |= SearchRouteTraits.EndTurnDeckCompression;
        }
        if (OpensRevivalWindow(before, after))
        {
            traits |= SearchRouteTraits.RevivalWindow;
        }
        return traits;
    }

    private static SearchRouteTraits ClassifyRoundTransitionTraits(
        SearchRouteTraits current,
        SimulationSnapshot before,
        SimulationSnapshot after)
    {
        if (OpensRevivalWindow(before, after))
            return current | SearchRouteTraits.RevivalWindow;
        return current;
    }

    private static bool OpensRevivalWindow(
        SimulationSnapshot before,
        SimulationSnapshot after)
        => after.RevivingEnemyCount > before.RevivingEnemyCount
            || after.RawEnemyHp < before.RawEnemyHp && after.EnemyHp >= before.EnemyHp;

    private static bool IsPureHistoryEntry(CombatPredictionHistoryEntry entry)
    {
        return entry is CombatPredictionCardPlayStartedEntry
            or CombatPredictionCardPlayFinishedEntry
            or CombatPredictionCreatureAttackedEntry
            or CombatPredictionDamageReceivedEntry;
    }

    private bool Dominates(ActionCandidate left, ActionCandidate right)
    {
        bool leftHasCycleEvidence = left.Node.CycleProbeLease != null || left.Node.Cycle != null;
        bool rightHasCycleEvidence = right.Node.CycleProbeLease != null || right.Node.Cycle != null;
        bool leftHasCycleExitProbe = left.Node.CycleExitProbe != null
            || HasValidPendingCycleExitObservation(left.Node);
        bool rightHasCycleExitProbe = right.Node.CycleExitProbe != null
            || HasValidPendingCycleExitObservation(right.Node);
        if (ReferenceEquals(left.Node, right.Node)
            || !left.IsPure
            || !right.IsPure
            || leftHasCycleEvidence != rightHasCycleEvidence
            || leftHasCycleEvidence
                && BuildCycleProbeFamilyKey(left.Node) != BuildCycleProbeFamilyKey(right.Node)
            || leftHasCycleExitProbe != rightHasCycleExitProbe
            || leftHasCycleExitProbe
                && BuildCycleExitAdmissionFamilyKey(left.Node)
                    != BuildCycleExitAdmissionFamilyKey(right.Node)
            || (leftHasCycleEvidence || leftHasCycleExitProbe)
                && CycleStartupHealthRiskBucket(left.Node)
                    != CycleStartupHealthRiskBucket(right.Node)
            || left.CardType != right.CardType
            || left.TargetCombatId != right.TargetCombatId
            || left.OptionFamilies != right.OptionFamilies
            || left.EnergySpent != right.EnergySpent
            || left.StarsSpent != right.StarsSpent)
        {
            return false;
        }

        bool noWorse = left.Damage >= right.Damage
            && left.Block >= right.Block
            && left.Hp >= right.Hp
            && left.MaxHp >= right.MaxHp
            && left.CumulativeHpLost <= right.CumulativeHpLost
            && left.LongTermResourceValue >= right.LongTermResourceValue
            && left.Node.Snapshot.StrategicHpCredit >= right.Node.Snapshot.StrategicHpCredit
            && (left.Node.Snapshot.RelicCounters.SatisfiedMask & right.Node.Snapshot.RelicCounters.SatisfiedMask)
                == right.Node.Snapshot.RelicCounters.SatisfiedMask
            && left.Node.Snapshot.StrategyGoalCount >= right.Node.Snapshot.StrategyGoalCount
            && left.AngerCopiesGenerated <= right.AngerCopiesGenerated;
        bool strictlyBetter = left.Damage > right.Damage
            || left.Block > right.Block
            || left.Hp > right.Hp
            || left.MaxHp > right.MaxHp
            || left.CumulativeHpLost < right.CumulativeHpLost
            || left.LongTermResourceValue > right.LongTermResourceValue
            || left.AngerCopiesGenerated < right.AngerCopiesGenerated;
        return noWorse && strictlyBetter;
    }

    private void AddNonDominatedCandidate(
        List<ActionCandidate> candidates,
        ActionCandidate candidate)
    {
        for (int index = candidates.Count - 1; index >= 0; index--)
        {
            ActionCandidate current = candidates[index];
            if (Dominates(current, candidate))
            {
                _run.DominatedActionsPruned++;
                candidate.Node.Snapshot.ReleaseSimulator();
                return;
            }
            if (!Dominates(candidate, current))
                continue;
            candidates.RemoveAt(index);
            _run.DominatedActionsPruned++;
            current.Node.Snapshot.ReleaseSimulator();
        }
        candidates.Add(candidate);
    }

    private static bool HasValidCycleProbeLease(SearchNode candidate)
    {
        if (candidate.CycleProbeLease is not { } lease
            || lease.Tracker == null
            || lease.Tracker.PeriodActions <= 0
            || lease.NextActionIndex < 0
            || lease.NextActionIndex >= lease.Tracker.PeriodActions
            || lease.CompletedRepetitions < 0)
        {
            return false;
        }
        return true;
    }

    private static bool HasValidCycleExitProbe(
        SearchNode candidate,
        bool requireIssuedTicket)
    {
        if (candidate.CycleExitProbe is not { } probe
            || probe.OriginTracker == null
            || probe.OriginGeneration <= 0
            || probe.OriginPeriodActions <= 0
            || probe.OriginPeriodActions != probe.OriginTracker.PeriodActions
            || probe.OriginShapeKey != probe.OriginTracker.ShapeKey
            || probe.OriginSequenceKey != probe.OriginTracker.SequenceKey
            || probe.OriginPhaseIndex < 0
            || probe.OriginPhaseIndex >= probe.OriginPeriodActions
            || probe.RemainingActions <= 0
            || probe.RemainingActions > MaximumCycleExitProbeActions
            || probe.RemainingEpochActions <= 0
            || probe.RemainingEpochActions > probe.RemainingActions
            || probe.RemainingTurnTransitions < 0
            || probe.RemainingTurnTransitions > MaximumCycleExitProbeTurnTransitions)
        {
            return false;
        }
        if (probe.LeaseIssued)
        {
            // Issued siblings own independent bounded continuations. Another sibling may
            // settle the shared tracker generation without revoking this embedded ticket.
            return true;
        }
        return !requireIssuedTicket
            && probe.OriginTracker.HasPendingExitProbe(
                probe.OriginPhaseIndex,
                probe.ExitActionKey,
                probe.OriginGeneration);
    }

    private static bool HasCycleAdmissionTranspositionLease(SearchNode candidate)
        => HasValidCycleProbeLease(candidate)
            || HasValidCycleExitProbe(candidate, requireIssuedTicket: false)
            || HasValidPendingCycleExitObservation(candidate);

    private static bool HasCycleExpansionTranspositionLease(SearchNode candidate)
        => HasValidCycleProbeLease(candidate)
            || HasValidCycleExitProbe(candidate, requireIssuedTicket: true);

    private static CycleExitProbeFamilyKey BuildCycleExitAdmissionFamilyKey(
        SearchNode candidate)
    {
        if (candidate.CycleExitProbe != null)
            return BuildCycleExitProbeFamilyKey(candidate);
        PendingCycleExitObservation pending = candidate.PendingCycleExitObservation
            ?? throw new InvalidOperationException("循环出口 admission 候选缺少临时证据。");
        return new CycleExitProbeFamilyKey(
            pending.OriginTracker.ShapeKey,
            pending.OriginTracker.SequenceKey,
            pending.OriginTracker.PeriodActions,
            pending.OriginPhaseIndex,
            pending.OriginTracker,
            OriginGeneration: 0,
            ExitActionKey: pending.ExitActionKey);
    }

    private static bool ShouldDeferCycleTranspositionUntilActionAdmission(
        SearchNode candidate)
        => !HasCycleAdmissionTranspositionLease(candidate)
            && RequiresBoundedCyclePlanning(candidate);

    private static bool TryIssueSingleDeferredCycleProbeLease(
        IReadOnlyList<ActionCandidate> admitted,
        IReadOnlyList<ActionCandidate> deferred,
        ActionCandidate preferred)
    {
        foreach (ActionCandidate candidate in admitted)
        {
            if (HasValidCycleProbeLease(candidate.Node))
                return false;
        }
        bool containsPreferred = false;
        foreach (ActionCandidate candidate in deferred)
        {
            containsPreferred |= ReferenceEquals(candidate.Node, preferred.Node);
            if (HasValidCycleProbeLease(candidate.Node))
                return false;
        }
        if (!containsPreferred
            || preferred.Node.CycleProbeLease != null
            || preferred.Node.CycleExitProbe != null
            || !RequiresBoundedCyclePlanning(preferred.Node))
        {
            return false;
        }
        StartCycleProbeLease(preferred.Node);
        return HasValidCycleProbeLease(preferred.Node);
    }

    private void CommitDeferredCycleCandidates(
        List<ActionCandidate> nonDominated,
        IReadOnlyList<ActionCandidate>? deferred,
        ExpansionBatch? batch)
    {
        if (deferred == null || deferred.Count == 0)
            return;
        int bestMaxHp = deferred[0].Node.Snapshot.PlayerMaxHp;
        foreach (ActionCandidate candidate in nonDominated)
            bestMaxHp = Math.Max(bestMaxHp, candidate.Node.Snapshot.PlayerMaxHp);
        foreach (ActionCandidate candidate in deferred)
            bestMaxHp = Math.Max(bestMaxHp, candidate.Node.Snapshot.PlayerMaxHp);

        ActionCandidate? leaseCandidate = nonDominated.Any(candidate =>
                HasValidCycleProbeLease(candidate.Node))
            ? null
            : SelectPreferredCycleAdmissionCandidate(
                deferred.Where(candidate => candidate.Node.CycleProbeLease == null
                    && candidate.Node.CycleExitProbe == null
                    && RequiresBoundedCyclePlanning(candidate.Node)),
                bestMaxHp);
        if (leaseCandidate is { } preferred)
        {
            if (!TryIssueSingleDeferredCycleProbeLease(
                    nonDominated,
                    deferred,
                    preferred))
            {
                throw new InvalidOperationException(
                    "动作 admission 选中的循环候选未取得有效探测租约。");
            }
            _run.CycleCandidatesProtected++;
        }

        // Only the single preferred recurrence owns a lease before the global table. Every
        // sibling first proves it is independently non-dominated in the exact-state frontier.
        ActionCandidate? protectedCandidate = null;
        foreach (ActionCandidate candidate in deferred)
        {
            if (!TryAcceptTransposition(candidate.Node))
            {
                if (batch == null)
                    candidate.Node.Snapshot.ReleaseSimulator();
                else
                    batch.Release(candidate.Node.Snapshot);
                continue;
            }
            if (leaseCandidate is { } leased
                && ReferenceEquals(candidate.Node, leased.Node))
            {
                protectedCandidate = candidate;
                continue;
            }
            if (batch == null)
                AddNonDominatedCandidate(nonDominated, candidate);
            else
                AddNonDominatedParallelCandidate(nonDominated, candidate, batch);
        }
        // This is the one explicit cycle lane. It neither removes ordinary candidates nor
        // participates in their pairwise dominance pruning; final action admission decides
        // whether it also wins a normal slot and otherwise appends the issued lease once.
        if (protectedCandidate is { } protectedCycle)
            nonDominated.Add(protectedCycle);
    }

    internal static void VerifyCycleTranspositionLeasePolicyForTesting()
    {
        SimulationSnapshot snapshot = (SimulationSnapshot)System.Runtime.CompilerServices
            .RuntimeHelpers.GetUninitializedObject(typeof(SimulationSnapshot));
        StateFingerprint shapeKey = new(1, 2);
        StateFingerprint sequenceKey = new(3, 4);
        StateFingerprint actionKey = new(5, 6);
        CycleSearchState coarseCycle = new(
            shapeKey,
            sequenceKey,
            PeriodActions: 1,
            Repetitions: 1,
            LastDelta: default,
            HasConsistentDelta: false);
        SearchNode candidate = new(
            Action: null,
            ActionCount: 2,
            PotionCount: 0,
            PotionStrategicCost: 0,
            Turn: 1,
            Traits: SearchRouteTraits.None,
            FutureSoldHp: 0,
            Score: 9,
            StateKey: new StateFingerprint(7, 8),
            HasPredictionRisk: false,
            BoundaryReason: SearchBoundaryReason.None,
            IsTerminal: false,
            Parent: null,
            Snapshot: null!,
            CombatProgress: null!,
            Cycle: coarseCycle);
        TranspositionLabel dominating = new(0, 0, 0, 0, 1, 10);
        TranspositionLabel dominated = new(0, 0, 0, 0, 2, 9);

        if (!ShouldDeferCycleTranspositionUntilActionAdmission(candidate)
            || HasCycleAdmissionTranspositionLease(candidate)
            || new TranspositionFrontier(dominating).TryAccept(dominated))
        {
            throw new InvalidOperationException(
                "没有租约的循环元数据未重新受到转置支配约束。");
        }

        SearchNode testRoot = new(
            Action: null,
            ActionCount: 0,
            PotionCount: 0,
            PotionStrategicCost: 0,
            Turn: 1,
            Traits: SearchRouteTraits.None,
            FutureSoldHp: 0,
            Score: 0,
            StateKey: default,
            HasPredictionRisk: false,
            BoundaryReason: SearchBoundaryReason.None,
            IsTerminal: false,
            Parent: null,
            Snapshot: snapshot,
            CombatProgress: null!);
        SearchNode firstRecurrence = new(
            Action: new PlanAction(PlanActionKind.PlayCard, 1),
            ActionCount: 1,
            PotionCount: 0,
            PotionStrategicCost: 0,
            Turn: 1,
            Traits: SearchRouteTraits.None,
            FutureSoldHp: 0,
            Score: 0,
            StateKey: new StateFingerprint(9, 10),
            HasPredictionRisk: false,
            BoundaryReason: SearchBoundaryReason.None,
            IsTerminal: false,
            Parent: testRoot,
            Snapshot: snapshot,
            CombatProgress: null!,
            Cycle: coarseCycle);
        SearchNode secondRecurrence = firstRecurrence with
        {
            StateKey = new StateFingerprint(11, 12),
        };
        ActionCandidate firstAction = new(
            Node: firstRecurrence,
            CardType: CardType.Attack,
            TargetCombatId: null,
            EnergySpent: 0,
            StarsSpent: 0,
            Damage: 0,
            Block: 0,
            Hp: 0,
            MaxHp: 0,
            CumulativeHpLost: 0,
            LongTermResourceValue: 0,
            AngerCopiesGenerated: 0,
            OptionFamilies: ActionOptionFamily.ResourceAndCycle,
            IsPure: true,
            NormalizedValue: 0);
        ActionCandidate secondAction = firstAction with { Node = secondRecurrence };
        ActionCandidate[] multipleRecurrences = [firstAction, secondAction];
        List<ActionCandidate> ordinarySelected = [secondAction];
        if (!TryIssueSingleDeferredCycleProbeLease(
                Array.Empty<ActionCandidate>(),
                multipleRecurrences,
                firstAction)
            || TryIssueSingleDeferredCycleProbeLease(
                Array.Empty<ActionCandidate>(),
                multipleRecurrences,
                secondAction)
            || !AdmitExistingCycleProbeLease(
                multipleRecurrences,
                ordinarySelected,
                bestMaxHp: 0)
            || ordinarySelected.Count != 2
            || !ReferenceEquals(ordinarySelected[1].Node, firstRecurrence)
            || !HasValidCycleProbeLease(firstRecurrence)
            || HasValidCycleProbeLease(secondRecurrence)
            || !HasCycleAdmissionTranspositionLease(firstRecurrence)
            || HasCycleAdmissionTranspositionLease(secondRecurrence)
            || multipleRecurrences.Count(item => HasValidCycleProbeLease(item.Node)) != 1)
        {
            throw new InvalidOperationException(
                "同一父节点的循环 admission 没有保持并复用唯一探测租约。");
        }

        SearchNode deferredAfterInheritedLease = secondRecurrence with
        {
            StateKey = new StateFingerprint(13, 14),
        };
        ActionCandidate deferredAfterInheritedAction = secondAction with
        {
            Node = deferredAfterInheritedLease,
        };
        if (TryIssueSingleDeferredCycleProbeLease(
                [firstAction],
                [deferredAfterInheritedAction],
                deferredAfterInheritedAction)
            || !HasValidCycleProbeLease(firstRecurrence)
            || HasValidCycleProbeLease(deferredAfterInheritedLease))
        {
            throw new InvalidOperationException(
                "父节点已有继承循环租约时仍给 deferred recurrence 签发了第二租约。");
        }

        CycleProbeTracker tracker = new(
            shapeKey,
            sequenceKey,
            [actionKey],
            default);
        candidate.CycleProbeLease = new CycleProbeLease(
            tracker,
            NextActionIndex: 0,
            CompletedRepetitions: 0,
            ImprovedSinceWrap: false,
            LastCompletedRepetitionImproved: false,
            ObservedExitQualityEpoch: 0);
        if (!HasCycleAdmissionTranspositionLease(candidate)
            || !HasCycleExpansionTranspositionLease(candidate))
        {
            throw new InvalidOperationException("有效循环探测租约没有绕过转置约束。");
        }

        candidate.CycleProbeLease = null;
        if (HasCycleAdmissionTranspositionLease(candidate)
            || new TranspositionFrontier(dominating).TryAccept(dominated))
        {
            throw new InvalidOperationException("被剥离的循环探测租约仍然绕过转置约束。");
        }

        long generation = tracker.ObserveExit(0, actionKey, default, out _);
        candidate.CycleExitProbe = new CycleExitProbeState(
            OriginTracker: tracker,
            OriginNode: candidate,
            OriginPhaseIndex: 0,
            OriginShapeKey: shapeKey,
            OriginSequenceKey: sequenceKey,
            OriginPeriodActions: 1,
            ExitActionKey: actionKey,
            OriginGeneration: generation,
            RemainingActions: MaximumCycleExitProbeActions,
            RemainingEpochActions: BaseCycleExitProbeActions,
            RemainingTurnTransitions: MaximumCycleExitProbeTurnTransitions);
        if (!HasCycleAdmissionTranspositionLease(candidate)
            || HasCycleExpansionTranspositionLease(candidate))
        {
            throw new InvalidOperationException(
                "待签发的循环出口票据没有被限制在 admission 阶段。");
        }
        if (!tracker.TryMarkExitProbeIssued(0, actionKey, generation))
            throw new InvalidOperationException("循环出口测试票据无法签发。");
        candidate.CycleExitProbe = candidate.CycleExitProbe with { LeaseIssued = true };
        if (!HasCycleExpansionTranspositionLease(candidate))
            throw new InvalidOperationException("已签发的循环出口票据无法继续推进。");
    }

    /// <summary>两张转置表共用条目预算；已有状态的支配标签继续更新。</summary>
    private bool AtTranspositionEntryLimit()
        => policy.TranspositionEntryLimit > 0
            && _run.Transpositions.Count + _run.ExpandedTranspositions.Count
                >= policy.TranspositionEntryLimit;

    private void ObserveTranspositionEntries()
        => _run.TranspositionDiagnostics.ObserveEntries(
            _run.Transpositions.Count + _run.ExpandedTranspositions.Count,
            policy.TranspositionEntryLimit, _run.Expanded);

    private bool TryAcceptTransposition(SearchNode candidate)
    {
        // Scheduling obligations are deliberately bounded elsewhere. A normal route at the
        // same simulator state cannot inherit their exact pattern/envelope history, so it must
        // not erase the probe before the obligation reaches the frontier.
        if (HasCycleAdmissionTranspositionLease(candidate)
            || CanRetainOrderedMutationLease(_run, candidate))
        {
            ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "bypass_cycle_or_ordered_lease");
            return true;
        }
        if ((policy.TranspositionPruningDisabledMask & 1) != 0)
        {
            ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "disabled_for_measurement");
            return true;
        }
        TranspositionLabel next = new(
            candidate.PotionCount,
            candidate.PotionStrategicCost,
            candidate.FutureSoldHp,
            candidate.Snapshot.CumulativePlayerHpLost,
            candidate.ActionCount,
            candidate.Score);
        if (!_run.Transpositions.TryGetValue(candidate.StateKey, out TranspositionFrontier? frontier))
        {
            if (AtTranspositionEntryLimit())
            {
                _run.TranspositionLimitBypasses++;
                ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "entry_limit");
                return true;
            }
            _run.Transpositions.Add(candidate.StateKey, new TranspositionFrontier(next));
            ObserveTranspositionEntries();
            ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "accepted_new_state");
            return true;
        }
        if (frontier.TryAccept(next))
        {
            ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "accepted_label");
            return true;
        }
        _run.TranspositionBranchesPruned++;
        ObserveSearchPath(candidate, SearchPathObservationStage.AdmissionTransposition, "rejected_dominated");
        if (_detailedDiagnostics && candidate.ActionCount <= 2)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Debug] TRANSPOSITION_REJECT route=" +
                $"{string.Join('>', candidate.Actions.Select(PolicyActionToken))} " +
                $"score={candidate.Score:F0} hp={candidate.Snapshot.ProjectedPlayerHp} " +
                $"enemy={candidate.Snapshot.EnemyHp} hand=" +
                $"{candidate.Snapshot.HandCount}/{candidate.Snapshot.ReachableHandValue}/" +
                $"{candidate.Snapshot.ZeroCostPlayableCount}");
        }
        return false;
    }

    private bool TryMarkExpandedState(SearchNode node)
    {
        if (node.PendingCycleExitObservation != null)
        {
            SearchReplayEvidence.PublishCandidateFailure(policy.Diagnostics, node, "expansion_admission_frontier");
            throw new InvalidOperationException(
                "临时循环出口 observation 越过了 action admission frontier。");
        }
        if (HasCycleExpansionTranspositionLease(node)
            || CanRetainOrderedMutationLease(_run, node))
        {
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "bypass_cycle_or_ordered_lease");
            return true;
        }
        if ((policy.TranspositionPruningDisabledMask & 2) != 0)
        {
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "disabled_for_measurement");
            return true;
        }
        TranspositionLabel next = new(
            node.PotionCount,
            node.PotionStrategicCost,
            node.FutureSoldHp,
            node.Snapshot.CumulativePlayerHpLost,
            node.ActionCount,
            node.Score);
        if (!_run.ExpandedTranspositions.TryGetValue(node.StateKey, out TranspositionFrontier? frontier))
        {
            if (AtTranspositionEntryLimit())
            {
                _run.TranspositionLimitBypasses++;
                ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "entry_limit");
                return true;
            }
            _run.ExpandedTranspositions.Add(node.StateKey, new TranspositionFrontier(next));
            ObserveTranspositionEntries();
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "accepted_new_state");
            return true;
        }
        if (frontier.TryAccept(next))
        {
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "accepted_label");
            return true;
        }
        _run.TranspositionBranchesPruned++;
        ObserveSearchPath(node, SearchPathObservationStage.ExpansionTransposition, "rejected_dominated");
        return false;
    }

    private IEnumerable<(int Index, Creature? Target)> TargetsFor(
        PredictedCard card,
        CombatPredictionSimulator simulator)
    {
        if (simulator.GetTargetType(card) == TargetType.AnyEnemy)
        {
            IReadOnlyList<Creature> enemies = simulator.State.Enemies;
            for (int i = 0; i < enemies.Count; i++)
            {
                Creature target = enemies[i];
                if (simulator.State.IsHittable(target))
                    yield return (i, target);
            }
            yield break;
        }

        yield return (-1, null);
    }

    private IEnumerable<(int Index, Creature? Target)> TargetsForPotion(
        PotionModel potion,
        CombatPredictionSimulator simulator)
    {
        if (potion.TargetType == TargetType.AnyEnemy)
        {
            IReadOnlyList<Creature> enemies = simulator.State.Enemies;
            for (int index = 0; index < enemies.Count; index++)
            {
                Creature enemy = enemies[index];
                if (simulator.State.IsHittable(enemy))
                    yield return (index, enemy);
            }
            yield break;
        }

        if (potion.TargetType is TargetType.AnyPlayer or TargetType.Self)
        {
            if (simulator.State.GetCreature(_player.Creature).IsAlive)
                yield return (-1, null);
            yield break;
        }

        if (potion.TargetType is TargetType.AllEnemies or TargetType.TargetedNoCreature)
            yield return (-1, null);
    }

    private static IReadOnlyList<PlanCardChoice>? ActionChoicesForReplay(PlanAction action)
    {
        List<PlanCardChoice> choices = [.. action.GetActionChoicesInExecutionOrder()];
        if (action.Kind == PlanActionKind.PlayCard && action.TurnStartChoices is { Count: > 0 })
        {
            // Knowledge Demon curses are never taken through a cursor. They are read straight off the raw plan
            // list by KnowledgeDemonChoiceSupport.Resolve during the enemy turn, which for a card that forces the
            // turn to end runs in AdvanceRound - after EndActionChoices has already asserted this cursor. Leaving
            // them here makes AssertConsumed report a choice that was never this cursor's to take.
            choices.AddRange(action.TurnStartChoices
                .Where(choice => choice.Effect != PlanChoiceEffect.ApplyKnowledgeCurse));
        }
        return choices.Count == 0 ? null : choices;
    }

}
