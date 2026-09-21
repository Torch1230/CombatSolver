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
    private IEnumerable<SearchNode> Expand(SearchNode node)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SimulationSnapshot snapshot = node.Snapshot;
        if (node.IsTerminal
            || snapshot.PlayerDead
            || snapshot.AllEnemiesDead
            || snapshot.BoundaryReason != SearchBoundaryReason.None)
        {
            throw new InvalidOperationException("终结搜索节点不应进入展开阶段。");
        }
        _run.ReusedNodeSnapshots++;
        if (!TryMarkExpandedState(node))
            yield break;
        if (!TryConsumeCycleExitProbeExpansionBudget(node))
        {
            _run.CycleContinuationsStopped++;
            _run.CycleStoppedExitBudget++;
            ObserveSearchPath(node, SearchPathObservationStage.ExpansionBlocked, "cycle_exit_budget");
            yield break;
        }
        _run.Expanded++;
        ObserveSearchPath(node, SearchPathObservationStage.Expanded, "serial_parent");
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        using ExpansionBatch? cycleExitBatch = node.CycleProbeLease == null
            && node.CycleExitProbe == null
            ? null
            : RentExpansionBatch();
        if (cycleExitBatch != null)
        {
            GenerateRawPotionCandidates(node, cycleExitBatch);
            GenerateRawEndTurnCandidates(node, cycleExitBatch);
        }

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        List<ActionCandidate> nonDominated = new(16);
        List<ActionCandidate>? deferredCycleCandidates = null;
        IReadOnlyList<PredictedCard> hand = playerState.Hand.Cards;
        HandFingerprintBuffer seenCards = default;
        int seenCardCount = 0;
        for (int handIndex = 0; handIndex < hand.Count; handIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PredictedCard card = hand[handIndex];
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
                // The first action after a partial-route restart still observes the live target gate.
                if (node.ActionCount == 0 && !card.Original.CanPlayTargeting(target))
                    continue;
                string targetName = displayNames.Creature(target);
                PlanAction action = new(
                    PlanActionKind.PlayCard,
                    node.Turn,
                    card.Preview.Id.Entry,
                    occurrence,
                    targetIndex,
                    target?.CombatId,
                    displayNames.Card(card.Preview),
                    targetName,
                    ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                    CardStateKey: cardStateKey,
                    CardStateOccurrence: cardStateOccurrence,
                        CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? "", CardUpgradeLevel: card.Preview.CurrentUpgradeLevel);
                using CardChoiceReplayCapture? cardCapture = PrepareCardChoiceCapture(node, action);
                SimulationSnapshot probeSnapshot = ReplayAction(node, action, cardChoiceCapture: cardCapture);

                CardChoiceSpec? choiceSpec = BuildPrimaryCardChoiceSpec(probeSnapshot);
                if (choiceSpec == null && CardChoiceSupport.RequiresUnsupportedExistingChoice(card.Preview))
                {
                    probeSnapshot.ReleaseSimulator();
                    continue;
                }
                PlanCardChoice? requiredEmptyChoice = CardChoiceSupport.BuildRequiredEmptyChoice(card.Preview);
                CardChoiceSpec? primaryChoiceSpec = choiceSpec
                    ?? BuildRequiredEmptyChoiceSpec(requiredEmptyChoice);
                IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> resolvedBranches =
                    HasChoiceBeforePrimary(probeSnapshot, primaryChoiceSpec)
                        ? ResolveRoundChoiceBranches(
                            node,
                            action,
                            probeSnapshot,
                            BuildPrimaryChoiceMatch(primaryChoiceSpec),
                            budgetPrimaryChoiceSpec: primaryChoiceSpec)
                        : ResolvePrimaryCardChoiceBranches(
                            node,
                            action,
                            probeSnapshot,
                            choiceSpec,
                            requiredEmptyChoice);
                resolvedBranches = WithCardChoiceCheckpoint(cardCapture?.Take(), resolvedBranches);
                foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in resolvedBranches)
                {
                    bool forcedTurnEnd = finalSnapshot.Turn > node.Turn;
                    PlanAction nodeAction = finalAction with { EndsPlayerTurn = forcedTurnEnd };
                    bool terminal = finalSnapshot.PlayerDead
                        || finalSnapshot.AllEnemiesDead
                        || finalSnapshot.BoundaryReason != SearchBoundaryReason.None;
                    double score = ApplySoldHpPenalty(
                        finalSnapshot.Score,
                        node.FutureSoldHp);
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
                    child = AttachCycleSchedulingEvidence(child);
                    PromoteOrderedMutationProgressTail(child);
                    CommitCycleExitObservation(child);
                    if (ShouldPruneCrossTurnNoProgress(child))
                    {
                        _run.RepeatableNoProgressBranchesPruned++;
                        finalSnapshot.ReleaseSimulator();
                        continue;
                    }
                    ActionCandidate actionCandidate = BuildCandidate(
                        snapshot,
                        finalSnapshot,
                        child,
                        card.Preview.Type,
                        target?.CombatId);
                    if (CanRetainOrderedMutationLease(_run, child))
                    {
                        // An admitted ordered-state lease has a bounded coordinator budget of
                        // its own. Let its direct semantic options reach action admission before
                        // ordinary transposition/dominance can erase the delayed-payoff edge.
                        nonDominated.Add(actionCandidate);
                    }
                    else if (ShouldDeferCycleTranspositionUntilActionAdmission(child))
                    {
                        deferredCycleCandidates ??= [];
                        deferredCycleCandidates.Add(actionCandidate);
                    }
                    else if (TryAcceptTransposition(child))
                    {
                        AddNonDominatedCandidate(nonDominated, actionCandidate);
                    }
                    else
                    {
                        finalSnapshot.ReleaseSimulator();
                    }
                }
            }
        }

        if (cycleExitBatch != null)
        {
            PruneCommittedCrossTurnCandidates(cycleExitBatch.Potions, cycleExitBatch);
            PruneCommittedCrossTurnCandidates(cycleExitBatch.EndTurns, cycleExitBatch);
        }
        CommitDeferredCycleCandidates(
            nonDominated,
            deferredCycleCandidates,
            batch: null);
        if (NeedsCycleExitAdmission(node, nonDominated, cycleExitBatch?.Potions, cycleExitBatch?.EndTurns))
        {
            SearchNode[] directChildren = nonDominated.Select(candidate => candidate.Node)
                .Concat(cycleExitBatch?.Potions ?? [])
                .Concat(cycleExitBatch?.EndTurns ?? [])
                .ToArray();
            foreach (SearchNode directChild in directChildren)
                CommitCycleExitObservation(directChild);
            AnnotateCycleExitProgress(node, directChildren);
            _ = MaterializeAdmittedCycleExitObservation(
                directChildren,
                _run.CycleFamilyLedger);
        }
        List<ActionCandidate> queuedCandidates = SelectActionCandidates(node, nonDominated);
        AdmitCycleProbeCandidate(nonDominated, queuedCandidates);
        AdmitCycleExitProbeCandidate(nonDominated, queuedCandidates);
        for (int index = queuedCandidates.Count - 1; index >= 0; index--)
        {
            ActionCandidate candidate = queuedCandidates[index];
            if (!ShouldRejectCycleCandidate(candidate.Node))
                continue;
            queuedCandidates.RemoveAt(index);
            nonDominated.RemoveAll(item => ReferenceEquals(item.Node, candidate.Node));
            candidate.Node.Snapshot.ReleaseSimulator();
        }
        _run.TopQueueActionsDropped += nonDominated.Count - queuedCandidates.Count;
        foreach (ActionCandidate candidate in nonDominated)
        {
            if (!queuedCandidates.Any(retained => ReferenceEquals(retained.Node, candidate.Node)))
            {
                candidate.Node.Snapshot.ReleaseSimulator();
            }
        }
        int yieldedCandidateCount = 0;
        try
        {
            while (yieldedCandidateCount < queuedCandidates.Count)
            {
                SearchNode candidate = queuedCandidates[yieldedCandidateCount].Node;
                yieldedCandidateCount++;
                yield return candidate;
            }
        }
        finally
        {
            for (; yieldedCandidateCount < queuedCandidates.Count; yieldedCandidateCount++)
                queuedCandidates[yieldedCandidateCount].Node.Snapshot.ReleaseSimulator();
        }

        if (cycleExitBatch != null)
        {
            foreach (SearchNode child in cycleExitBatch.Potions)
            {
                EnsureBoundedCycleProbeLease(child);
                if (ShouldRejectCycleCandidate(child)
                    || !TryAcceptTransposition(child))
                {
                    cycleExitBatch.Release(child.Snapshot);
                    continue;
                }
                cycleExitBatch.Transfer(child.Snapshot);
                yield return child;
            }
            foreach (SearchNode child in cycleExitBatch.EndTurns)
            {
                if (!TryAcceptTransposition(child))
                {
                    cycleExitBatch.Release(child.Snapshot);
                    continue;
                }
                cycleExitBatch.Transfer(child.Snapshot);
                yield return child;
            }
            yield break;
        }

        if (_detailedDiagnostics && node.ActionCount == 0)
        {
            policy.Diagnostics.Info(
                $"[CombatSolver/Debug] ROOT_POTION_SLOTS count={root.PotionSlotCount} " +
                $"potions={string.Join(',', Enumerable.Range(0, root.PotionSlotCount).Select(slot =>
                {
                    PotionModel? item = simulatedCombat.GetPotionAtSlot(_player, slot);
                    return $"{slot}:{item?.Id.Entry ?? "-"}:{(item != null && PotionOnUseSupport.CanSearch(item))}";
                }))}");
        }
        if (_maximumPotionUses == null || ExplicitPotionUseCount(node) < _maximumPotionUses.Value)
        for (int potionSlot = 0; potionSlot < root.PotionSlotCount; potionSlot++)
        {
            PotionModel? potion = simulatedCombat.GetPotionAtSlot(_player, potionSlot);
            if (potion == null
                || !simulatedCombat.IsPotionAvailable(_player, potionSlot)
                || !PotionOnUseSupport.CanSearch(potion)
                || !AllowsPotionUse(potionSlot, potion.Id.Entry)
                || PotionUsePolicy.RequiresOpeningUse(potion)
                    && node.HasNonPotionAction)
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
                    TargetName: displayNames.Creature(target),
                    PotionSlot: potionSlot,
                    PotionId: potion.Id.Entry,
                    PotionTitle: displayNames.Potion(potion));
                using PotionChoiceReplayCheckpoint? checkpoint = PreparePotionChoiceOptions(
                    node, baseAction, potion, out SimulationSnapshot? probeSnapshot,
                    out IReadOnlyList<PlanCardChoice?> choices, out CardChoiceSpec? choiceSpec);
                if (_detailedDiagnostics && node.ActionCount == 0)
                {
                    policy.Diagnostics.Info(
                        $"[CombatSolver/Debug] ROOT_POTION_OPTIONS potion={potion.Id.Entry} " +
                        $"choices={string.Join(';', choices.Select(choice => choice == null
                            ? "-"
                            : choice.Cards.Count == 0
                                ? "skip"
                                : string.Join(',', choice.Cards.Select(card => card.CardId))))}");
                }
                foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                         WithPotionChoiceCheckpoint(checkpoint, ResolveExplicitCardChoiceBranches(
                             node, baseAction, probeSnapshot, choices, choiceSpec)))
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
                        ClassifyPotionTraits(node.Traits, snapshot, finalSnapshot),
                        node.FutureSoldHp,
                        ApplySoldHpPenalty(
                            finalSnapshot.Score,
                            node.FutureSoldHp),
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
                    child = AttachCycleSchedulingEvidence(child);
                    PromoteOrderedMutationProgressTail(child);
                    CommitCycleExitObservation(child);
                    EnsureBoundedCycleProbeLease(child);
                    if (ShouldRejectCycleCandidate(child))
                    {
                        finalSnapshot.ReleaseSimulator();
                        continue;
                    }
                    bool accepted = TryAcceptTransposition(child);
                    if (_detailedDiagnostics && node.ActionCount == 0)
                    {
                        PlanCardChoice? resolvedChoice = finalAction.Choice;
                        policy.Diagnostics.Info(
                            $"[CombatSolver/Debug] ROOT_POTION_BRANCH potion={potion.Id.Entry} " +
                            $"choice={(resolvedChoice == null ? "-" : string.Join(',', resolvedChoice.Cards.Select(card => card.CardId)))} " +
                            $"accepted={accepted} hp={finalSnapshot.PlayerHp} " +
                            $"projected_hp={finalSnapshot.ProjectedPlayerHp} " +
                            $"enemy_hp={finalSnapshot.EnemyHp} hand={finalSnapshot.HandCount} " +
                            $"score={child.Score:0}");
                    }
                    if (accepted)
                        yield return child;
                    else
                        finalSnapshot.ReleaseSimulator();
                }
            }
        }

        foreach (SearchNode endNode in BuildAcceptedEndTurnNodes(node))
            yield return endNode;
    }

    private bool ShouldPruneCrossTurnNoProgress(SearchNode node)
    {
        if (node.Parent == null
            || node.Turn <= node.Parent.Turn
            || node.IsTerminal
            || node.BoundaryReason != SearchBoundaryReason.None)
        {
            return false;
        }
        if (node.CycleExitProbe is { RemainingActions: > 0, RemainingTurnTransitions: >= 0 }
            || HasValidPendingCycleExitObservation(node))
            return false;
        // A forced-turn action can be materialized before the direct EndTurn branches from
        // its turn start. Until turn-outcome annotation has the complete branch-aware baseline,
        // absence of scalar progress is not sufficient evidence for pruning.
        if (!node.CrossTurnSemanticEvidenceAttached)
            return false;

        CombatPredictionSimulator simulator = (CombatPredictionSimulator)node.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        int drawPerTurn = Math.Max(
            1,
            PersistentPowerSupport.GetModifiedHandDraw(
                combat,
                _player,
                CombatManager.baseHandDrawCount));
        int deckCycleTurns = Math.Max(
            1,
            (node.Snapshot.LiveDeckSize + drawPerTurn - 1) / drawPerTurn);
        int noProgressLimit = Math.Max(
            SolverWeights.SetupValueHorizonTurns,
            deckCycleTurns * 2);
        if (node.CrossTurnProbe is { } probe)
        {
            int boundedProbeTurns = Math.Clamp(
                checked(noProgressLimit + 1),
                SolverWeights.SetupValueHorizonTurns + 1,
                SolverWeights.SetupValueHorizonTurns * 4);
            if (probe.LastTurnImproved)
                boundedProbeTurns = checked(boundedProbeTurns * 2);
            if (probe.LastTurnChangedSemanticState)
            {
                // Repeated divergence from the exact stand-pat outcome is generic evidence
                // of hidden state movement (mutable counters, powers, RNG, etc.). Give that
                // one tiny portfolio lane a longer, still-hard-bounded horizon.
                boundedProbeTurns = SolverWeights.SetupValueHorizonTurns * 4;
            }
            if (probe.CompletedTurnTransitions > boundedProbeTurns)
            {
                _run.CrossTurnContinuationsStopped++;
                return true;
            }
        }
        if (node.CombatProgress.TurnsWithoutProgress < noProgressLimit)
            return false;
        if (node.CrossTurnProbe != null)
            return false;
        return true;
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> BuildEndTurnBranches(
        SearchNode node,
        IReadOnlyList<PlanCardChoice> choices)
    {
        PlanAction action = new(
            PlanActionKind.EndTurn,
            node.Turn,
            TurnStartChoices: choices.Count == 0 ? null : choices);
        SimulationSnapshot snapshot = ReplayAction(node, action);
        foreach ((PlanAction resolvedAction, SimulationSnapshot resolvedSnapshot) in
                 ResolveRoundChoiceBranches(node, action, snapshot))
        {
            yield return (resolvedAction, resolvedSnapshot);
        }
    }

    private IEnumerable<SearchNode> BuildAcceptedEndTurnNodes(SearchNode node)
    {
        using ExpansionBatch batch = RentExpansionBatch();
        GenerateRawEndTurnCandidates(node, batch);
        PruneCommittedCrossTurnCandidates(batch.EndTurns, batch);
        if (NeedsCycleExitAdmission(node, [], null, batch.EndTurns))
        {
            AnnotateCycleExitProgress(node, batch.EndTurns);
            _ = MaterializeAdmittedCycleExitObservation(
                batch.EndTurns,
                _run.CycleFamilyLedger);
        }
        foreach (SearchNode endNode in batch.EndTurns)
        {
            if (!TryAcceptTransposition(endNode))
            {
                batch.Release(endNode.Snapshot);
                continue;
            }
            batch.Transfer(endNode.Snapshot);
            yield return endNode;
        }
    }

}
