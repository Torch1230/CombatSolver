using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private readonly record struct ExpansionPlan(SearchNode Parent, bool CardNameFirst);

    private bool TryAdmitExpansionParent(SearchNode node, SimulationSnapshot snapshot, bool parallel)
    {
        if (node.IsTerminal
            || snapshot.PlayerDead
            || snapshot.AllEnemiesDead
            || snapshot.BoundaryReason != SearchBoundaryReason.None)
        {
            throw new InvalidOperationException(parallel
                ? "终结搜索节点不应进入并行展开阶段。"
                : "终结搜索节点不应进入展开阶段。");
        }
        _run.ReusedNodeSnapshots++;
        if (!TryMarkExpandedState(node))
            return false;
        if (!TryConsumeCycleExitProbeExpansionBudget(node))
        {
            _run.CycleContinuationsStopped++;
            _run.CycleStoppedExitBudget++;
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionBlocked, "cycle_exit_budget");
            return false;
        }
        _run.Expanded++;
        ObserveSearchPath(node, SearchPathObservationStage.Expanded,
            parallel ? "parallel_parent" : "serial_parent");
        return true;
    }

    private IEnumerable<PreparedCardAction> EnumeratePlannedCardActions(
        ExpansionPlan plan)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SearchNode node = plan.Parent;
        SimulationSnapshot snapshot = node.Snapshot;
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead)
            yield break;

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        IReadOnlyList<PredictedCard> hand = playerState.Hand.Cards;
        HandFingerprintBuffer seenCards = default;
        int seenCardCount = 0;
        for (int handIndex = 0; handIndex < hand.Count; handIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PredictedCard card = hand[handIndex];
            if (card.Preview is Tutor)
                throw new NotSupportedException(
                    "Tutor 需要队友原生选牌；搜索尚不能评价该未知结果。");
            string cardId = card.Preview.Id.Entry;
            int occurrence = 0;
            for (int priorIndex = 0; priorIndex < handIndex; priorIndex++)
            {
                if (string.Equals(hand[priorIndex].Preview.Id.Entry, cardId, StringComparison.Ordinal))
                    occurrence++;
            }
            if (!simulatedCombat.CanPlayCard(simulator, card))
                continue;
            StateFingerprint playableKey = BuildPlayableCardKey(card);
            bool duplicate = false;
            for (int seenIndex = 0; seenIndex < seenCardCount; seenIndex++)
            {
                if (seenCards[seenIndex] == playableKey)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate)
            {
                _run.DuplicateCardBranchesPruned++;
                continue;
            }
            seenCards[seenCardCount++] = playableKey;
            string cardStateKey = CardChoiceSupport.ChoiceCardKey(card);
            bool requiresUnsupportedExistingChoice =
                CardChoiceSupport.RequiresUnsupportedExistingChoice(card.Preview);
            PlanCardChoice? requiredEmptyChoice =
                CardChoiceSupport.BuildRequiredEmptyChoice(card.Preview);
            int cardStateOccurrence = 0;
            for (int priorIndex = 0; priorIndex < handIndex; priorIndex++)
            {
                if (string.Equals(
                        CardChoiceSupport.ChoiceCardKey(hand[priorIndex]),
                        cardStateKey,
                        StringComparison.Ordinal))
                {
                    cardStateOccurrence++;
                }
            }
            foreach ((int targetIndex, Creature? target) in TargetsFor(card, simulator))
            {
                if (IsPureTeammateSupport(card, target))
                    continue;
                if (node.ActionCount == 0 && !card.Original.CanPlayTargeting(target))
                    continue;
                string cardTitle;
                string targetName;
                if (plan.CardNameFirst)
                {
                    cardTitle = displayNames.Card(card.Preview);
                    targetName = displayNames.Creature(target, simulatedCombat.KnownEnemies);
                }
                else
                {
                    targetName = displayNames.Creature(target, simulatedCombat.KnownEnemies);
                    cardTitle = displayNames.Card(card.Preview);
                }
                PlanAction action = new(
                    PlanActionKind.PlayCard,
                    node.Turn,
                    card.Preview.Id.Entry,
                    occurrence,
                    targetIndex,
                    target?.CombatId,
                    cardTitle,
                    targetName,
                    ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                    CardStateKey: cardStateKey,
                    CardStateOccurrence: cardStateOccurrence,
                    CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? "",
                    CardUpgradeLevel: card.Preview.CurrentUpgradeLevel);
                yield return new PreparedCardAction(
                    action,
                    card.Preview.Type,
                    target?.CombatId,
                    requiresUnsupportedExistingChoice,
                    requiredEmptyChoice);
            }
        }
    }

    private IEnumerable<PreparedPotionAction> EnumeratePlannedPotionActions(ExpansionPlan plan)
    {
        SearchNode node = plan.Parent;
        SimulationSnapshot snapshot = node.Snapshot;
        if (snapshot.PlayerDead || snapshot.AllEnemiesDead
            || _earliestPotionTurn is { } earliestTurn && node.Turn < earliestTurn
            || _maximumPotionUses != null
                && ExplicitPotionUseCount(node) >= _maximumPotionUses.Value)
        {
            yield break;
        }

        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        for (int potionSlot = 0; potionSlot < root.PotionSlotCount; potionSlot++)
        {
            PotionModel? potion = simulatedCombat.GetPotionAtSlot(_player, potionSlot);
            if (potion == null
                || !simulatedCombat.IsPotionAvailable(_player, potionSlot)
                || !PotionOnUseSupport.CanSearch(potion)
                || !AllowsPotionUse(potionSlot, potion.Id.Entry))
            {
                continue;
            }

            foreach ((int targetIndex, Creature? target) in TargetsForPotion(potion, simulator))
            {
                PlanAction baseAction = new(
                    PlanActionKind.UsePotion,
                    node.Turn,
                    TargetIndex: targetIndex,
                    TargetCombatId: target?.CombatId,
                    TargetName: displayNames.Creature(target,
                        ((SimulatedCombatState)simulator.State.CombatState).KnownEnemies),
                    PotionSlot: potionSlot,
                    PotionId: potion.Id.Entry,
                    PotionTitle: displayNames.Potion(potion));
                yield return new PreparedPotionAction(baseAction, potion);
            }
        }
    }

    private SearchNode CreatePlannedCardChild(
        SearchNode node,
        PlanAction finalAction,
        SimulationSnapshot finalSnapshot)
    {
        bool forcedTurnEnd = finalSnapshot.Turn > node.Turn;
        PlanAction nodeAction = finalAction with { EndsPlayerTurn = forcedTurnEnd };
        bool terminal = finalSnapshot.PlayerDead
            || finalSnapshot.AllEnemiesDead
            || finalSnapshot.BoundaryReason != SearchBoundaryReason.None;
        double score = ApplySoldHpPenalty(finalSnapshot.Score, node.FutureSoldHp);
        SearchNode child = new(
            nodeAction,
            node.ActionCount + 1,
            finalSnapshot.PotionUseCount,
            finalSnapshot.PotionStrategicCost,
            forcedTurnEnd ? node.Turn + 1 : node.Turn,
            node.Traits,
            node.FutureSoldHp,
            score,
            finalSnapshot.StateKey,
            finalSnapshot.HasRisk,
            finalSnapshot.BoundaryReason,
            terminal,
            node,
            finalSnapshot,
            forcedTurnEnd
                ? node.CombatProgress.Advance(finalSnapshot)
                : node.CombatProgress)
        {
            CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, finalSnapshot),
        };
        return AttachCycleSchedulingEvidence(child);
    }

    private bool TryResolvePlannedCardChoices(
        SearchNode node,
        PreparedCardAction action,
        SimulationSnapshot probeSnapshot,
        out IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> resolvedBranches)
    {
        CardChoiceSpec? choiceSpec = BuildPrimaryCardChoiceSpec(probeSnapshot);
        if (choiceSpec == null && action.RequiresUnsupportedExistingChoice)
        {
            probeSnapshot.ReleaseSimulator();
            resolvedBranches = [];
            return false;
        }
        PlanCardChoice? requiredEmptyChoice = action.RequiredEmptyChoice;
        CardChoiceSpec? primaryChoiceSpec = choiceSpec
            ?? BuildRequiredEmptyChoiceSpec(requiredEmptyChoice);
        resolvedBranches = HasChoiceBeforePrimary(probeSnapshot, primaryChoiceSpec)
            ? ResolveRoundChoiceBranches(
                node,
                action.Action,
                probeSnapshot,
                BuildPrimaryChoiceMatch(primaryChoiceSpec),
                budgetPrimaryChoiceSpec: primaryChoiceSpec)
            : ResolvePrimaryCardChoiceBranches(
                node,
                action.Action,
                probeSnapshot,
                choiceSpec,
                requiredEmptyChoice);
        return true;
    }

    private SearchNode CreatePlannedPotionChild(
        SearchNode node,
        PlanAction finalAction,
        SimulationSnapshot finalSnapshot)
    {
        bool terminal = finalSnapshot.PlayerDead
            || finalSnapshot.AllEnemiesDead
            || finalSnapshot.BoundaryReason != SearchBoundaryReason.None;
        SearchNode child = new(
            finalAction,
            node.ActionCount + 1,
            finalSnapshot.PotionUseCount,
            finalSnapshot.PotionStrategicCost,
            node.Turn,
            ClassifyPotionTraits(node.Traits, node.Snapshot, finalSnapshot),
            node.FutureSoldHp,
            ApplySoldHpPenalty(finalSnapshot.Score, node.FutureSoldHp),
            finalSnapshot.StateKey,
            finalSnapshot.HasRisk,
            finalSnapshot.BoundaryReason,
            terminal,
            node,
            finalSnapshot,
            node.CombatProgress)
        {
            CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, finalSnapshot),
        };
        return AttachCycleSchedulingEvidence(child);
    }

    private bool TryAdmitPlannedPotionChild(SearchNode child, out bool rejectedByCycle)
    {
        EnsureBoundedCycleProbeLease(child);
        rejectedByCycle = ShouldRejectCycleCandidate(child);
        return !rejectedByCycle && TryAcceptTransposition(child);
    }

    private IEnumerable<SearchNode> AdmitPlannedEndTurnChildren(ExpansionBatch batch)
    {
        foreach (SearchNode child in batch.EndTurns)
        {
            if (!TryAcceptTransposition(child))
            {
                batch.Release(child.Snapshot);
                continue;
            }
            batch.Transfer(child.Snapshot);
            yield return child;
        }
    }

    private void ProcessExpandedCardCandidate(
        SearchNode parent,
        RawCardCandidate raw,
        List<ActionCandidate> nonDominated,
        ref List<ActionCandidate>? deferredCycleCandidates,
        ExpansionBatch? batch)
    {
        SearchNode child = raw.Node;
        PromoteOrderedMutationProgressTail(child);
        CommitCycleExitObservation(child);
        if (ShouldPruneCrossTurnNoProgress(child))
        {
            _run.RepeatableNoProgressBranchesPruned++;
            ReleasePlannedCandidate(child, batch);
            return;
        }
        ActionCandidate candidate = BuildCandidate(
            parent.Snapshot,
            child.Snapshot,
            child,
            raw.CardType,
            raw.TargetCombatId);
        if (CanRetainOrderedMutationLease(_run, child))
        {
            nonDominated.Add(candidate);
            return;
        }
        if (ShouldDeferCycleTranspositionUntilActionAdmission(child))
        {
            deferredCycleCandidates ??= [];
            deferredCycleCandidates.Add(candidate);
            return;
        }
        if (TryAcceptTransposition(child))
            AddNonDominatedCandidate(nonDominated, candidate, batch);
        else
            ReleasePlannedCandidate(child, batch);
    }

    private static void ReleasePlannedCandidate(SearchNode child, ExpansionBatch? batch)
    {
        if (batch == null)
            child.Snapshot.ReleaseSimulator();
        else
            batch.Release(child.Snapshot);
    }
}
