using System.Diagnostics;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using BufferCard = MegaCrit.Sts2.Core.Models.Cards.Buffer;

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
        _run.Expanded++;
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        List<ActionCandidate> nonDominated = new(16);
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
                    CardStateOccurrence: cardStateOccurrence);
                SimulationSnapshot probeSnapshot = ReplayAction(node, action);

                CombatPredictionSimulator probeSimulator = (CombatPredictionSimulator)probeSnapshot.Simulator;
                CardChoiceSpec? choiceSpec = CardChoiceSupport.GetSpec(probeSimulator, card);
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
                        ? ResolveRoundChoiceBranches(node, action, probeSnapshot, primaryChoiceSpec)
                        : ResolvePrimaryCardChoiceBranches(
                            node,
                            action,
                            probeSnapshot,
                            choiceSpec,
                            requiredEmptyChoice);
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
                    bool repeatableNoProgress = IsRepeatableNoProgressStep(
                        snapshot,
                        finalSnapshot,
                        card);
                    string? repeatableCardId = repeatableNoProgress
                        ? card.Preview.Id.Entry
                        : null;
                    int repeatableCount = repeatableNoProgress
                        ? string.Equals(
                            node.RepeatableNoProgressCardId,
                            repeatableCardId,
                            StringComparison.Ordinal)
                            ? node.RepeatableNoProgressCount + 1
                            : 1
                        : 0;
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
                            : node.CombatProgress,
                        RepeatableNoProgressCardId: repeatableCardId,
                        RepeatableNoProgressCount: repeatableCount);
                    if (ShouldPruneRepeatableNoProgress(child)
                        || ShouldPruneCrossTurnNoProgress(child))
                    {
                        _run.RepeatableNoProgressBranchesPruned++;
                        finalSnapshot.ReleaseSimulator();
                        continue;
                    }
                    if (TryAcceptTransposition(child))
                    {
                        AddNonDominatedCandidate(nonDominated, BuildCandidate(
                            snapshot,
                            finalSnapshot,
                            child,
                            card.Preview.Type,
                            target?.CombatId));
                    }
                    else
                    {
                        finalSnapshot.ReleaseSimulator();
                    }
                }
            }
        }

        nonDominated.Sort(static (left, right) =>
        {
            int byScore = right.Node.Score.CompareTo(left.Node.Score);
            return byScore != 0
                ? byScore
                : right.NormalizedValue.CompareTo(left.NormalizedValue);
        });
        int regularQueuedCount = Math.Min(_profile.MaxCardBranchesPerNode, nonDominated.Count);
        List<ActionCandidate> queuedCandidates = nonDominated.Take(regularQueuedCount).ToList();
        ActionCandidate? revivalWindowCandidate = nonDominated
            .Where(candidate => candidate.Node.Snapshot.RevivingEnemyCount > snapshot.RevivingEnemyCount)
            .OrderByDescending(candidate => candidate.Node.Snapshot.RevivingEnemyCount)
            .ThenBy(candidate => candidate.Node.Snapshot.RawEnemyHp)
            .ThenBy(candidate => candidate.Node.Snapshot.MaxCurrentEnemyHp)
            .ThenByDescending(candidate => candidate.Node.Snapshot.ProjectedPlayerHp)
            .Select(candidate => (ActionCandidate?)candidate)
            .FirstOrDefault();
        if (revivalWindowCandidate is { } revivalCandidate
            && !queuedCandidates.Any(candidate => ReferenceEquals(candidate.Node, revivalCandidate.Node)))
        {
            queuedCandidates.Add(revivalCandidate);
        }
        foreach (ActionCandidate candidate in nonDominated.Skip(regularQueuedCount))
        {
            if (CurrentTurnRoutingChoice(candidate.Node) != null)
                queuedCandidates.Add(candidate);
        }
        _run.TopQueueActionsDropped += nonDominated.Count - queuedCandidates.Count;
        foreach (ActionCandidate candidate in nonDominated)
        {
            if (!queuedCandidates.Any(retained => ReferenceEquals(retained.Node, candidate.Node)))
                candidate.Node.Snapshot.ReleaseSimulator();
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
        if (_potionPolicy != SolverPotionPolicy.Disabled
            && (_maximumPotionUses == null || node.PotionCount < _maximumPotionUses.Value))
        for (int potionSlot = 0; potionSlot < root.PotionSlotCount; potionSlot++)
        {
            PotionModel? potion = simulatedCombat.GetPotionAtSlot(_player, potionSlot);
            if (potion == null
                || _bannedPotionSlots.Contains(potionSlot)
                || !simulatedCombat.IsPotionAvailable(_player, potionSlot)
                || !PotionOnUseSupport.CanSearch(potion)
                || PotionUsePolicy.RequiresOpeningUse(potion)
                    && node.Actions.Any(action => action.Kind != PlanActionKind.UsePotion))
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
                SimulationSnapshot? probeSnapshot = null;
                IReadOnlyList<PlanCardChoice?> choices;
                if (PotionChoiceSupport.RequiresChoice(potion))
                {
                    CombatPredictionSimulator choiceSimulator = simulator;
                    if (PotionChoiceSupport.GeneratesCardChoice(potion))
                    {
                        probeSnapshot = ReplayAction(node, baseAction);
                        choiceSimulator = (CombatPredictionSimulator)probeSnapshot.Simulator;
                    }
                    CardChoiceSpec spec = PotionChoiceSupport.GetSpec(choiceSimulator, potion);
                    choices = CardChoiceSupport.BuildChoices(
                            spec,
                            displayNames,
                            _profile.MaxPileChoiceBranchesPerAction,
                            _profile.MaxHandChoiceBranchesPerAction)
                        .Select(choice => choice with { SourceId = potion.Id.Entry })
                        .Cast<PlanCardChoice?>()
                        .ToList();
                    _run.ChoiceBranchesEvaluated += choices.Count;
                    probeSnapshot?.ReleaseSimulator();
                }
                else
                {
                    probeSnapshot = ReplayAction(node, baseAction);
                    choices = [null];
                }
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
                foreach (PlanCardChoice? choice in choices)
                {
                    PlanAction action = baseAction with { Choice = choice };
                    SimulationSnapshot childSnapshot;
                    if (choice == null)
                    {
                        childSnapshot = probeSnapshot
                            ?? throw new InvalidOperationException("无选牌药水缺少动作快照。");
                    }
                    else
                    {
                        SimulationSnapshot? replayedChoice = ReplayPlannedChoiceBranch(node, action);
                        if (replayedChoice == null)
                            continue;
                        childSnapshot = replayedChoice;
                    }

                    foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                             ResolveRoundChoiceBranches(node, action, childSnapshot))
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
                            node.CombatProgress);
                        bool accepted = TryAcceptTransposition(child);
                        if (_detailedDiagnostics && node.ActionCount == 0)
                        {
                            policy.Diagnostics.Info(
                                $"[CombatSolver/Debug] ROOT_POTION_BRANCH potion={potion.Id.Entry} " +
                                $"choice={(choice == null ? "-" : string.Join(',', choice.Cards.Select(card => card.CardId)))} " +
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
        }

        foreach ((PlanAction endAction, SimulationSnapshot endSnapshot) in BuildEndTurnBranches(node, []))
        {
            int nextTurn = node.Turn + 1;
            bool combatEnded = endSnapshot.PlayerDead || endSnapshot.AllEnemiesDead;
            bool endTerminal = combatEnded || endSnapshot.BoundaryReason != SearchBoundaryReason.None;
            double endScore = ApplySoldHpPenalty(endSnapshot.Score, node.FutureSoldHp);
            SearchNode endNode = new(
                endAction,
                node.ActionCount + 1,
                endSnapshot.PotionUseCount,
                endSnapshot.PotionStrategicCost,
                nextTurn,
                ClassifyRoundTransitionTraits(node.Traits, snapshot, endSnapshot),
                node.FutureSoldHp,
                endScore,
                endSnapshot.StateKey,
                endSnapshot.HasRisk,
                endSnapshot.BoundaryReason,
                endTerminal,
                node,
                endSnapshot,
                node.CombatProgress.Advance(endSnapshot));
            if (ShouldPruneCrossTurnNoProgress(endNode))
            {
                _run.RepeatableNoProgressBranchesPruned++;
                endSnapshot.ReleaseSimulator();
            }
            else if (TryAcceptTransposition(endNode))
                yield return endNode;
            else
                endSnapshot.ReleaseSimulator();
        }
    }

    private bool IsRepeatableNoProgressStep(
        SimulationSnapshot before,
        SimulationSnapshot after,
        PredictedCard playedCard)
    {
        if (after.BoundaryReason != SearchBoundaryReason.None
            || after.PlayerDead
            || after.AllEnemiesDead
            || after.Turn != before.Turn
            || after.EnemyHp != before.EnemyHp
            || after.AliveEnemyCount != before.AliveEnemyCount
            || after.PlayerHp != before.PlayerHp
            || after.Energy != before.Energy
            || after.Stars != before.Stars
            || after.HandCount != before.HandCount)
        {
            return false;
        }

        CombatPredictionSimulator simulator = (CombatPredictionSimulator)after.Simulator;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        PredictedCard? returned = playerState.Hand.Cards.FirstOrDefault(card =>
            card.References(playedCard.Original));
        return returned != null
            && returned.GetEnergyCostWithModifiers(simulator, playerState) == 0
            && returned.GetStarCostWithModifiers(simulator, playerState) == 0;
    }

    private bool ShouldPruneRepeatableNoProgress(SearchNode node)
    {
        if (node.RepeatableNoProgressCount <= 0)
            return false;

        int limit = SolverWeights.MaxRepeatableNoProgressPlays;
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)node.Snapshot.Simulator;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        bool hasLinearKillPayoff = playerState.Hand.Cards.Any(card =>
            card.Preview is BodySlam or LunarBlast or GoldAxe);
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        bool canScaleAttackWithSlow = playerState.Hand.Cards.Any(card => card.Preview.Type == CardType.Attack)
            && combat.EffectivePowers().Any(power =>
                power is SlowPower
                && power.Amount > 0
                && simulator.State.GetCreature(power.Owner).IsAlive);
        if (hasLinearKillPayoff || canScaleAttackWithSlow)
            limit = Math.Max(limit, node.Snapshot.EnemyHp + 1);
        return node.RepeatableNoProgressCount > limit;
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
        return node.CombatProgress.TurnsWithoutProgress >= noProgressLimit;
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

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolvePrimaryCardChoiceBranches(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot probeSnapshot,
        CardChoiceSpec? choiceSpec,
        PlanCardChoice? requiredEmptyChoice)
    {
        IReadOnlyList<PlanCardChoice?> choices = choiceSpec == null
            ? requiredEmptyChoice == null ? [null] : [requiredEmptyChoice]
            : CardChoiceSupport.BuildChoices(
                choiceSpec,
                displayNames,
                _profile.MaxPileChoiceBranchesPerAction,
                _profile.MaxHandChoiceBranchesPerAction).Cast<PlanCardChoice?>().ToList();
        if (choices.Count == 0)
            choices = [null];
        else if (choiceSpec != null)
            _run.ChoiceBranchesEvaluated += choices.Count;

        SimulatedCombatState probeCombat =
            (SimulatedCombatState)probeSnapshot.Simulator.State.CombatState;
        bool unregisteredPendingChoice = probeSnapshot.BoundaryReason == SearchBoundaryReason.PendingChoice
            && probeCombat.PendingTurnStartChoice == null
            && probeCombat.PendingKnowledgeDemonChoice == null;
        if (!choices.Contains(null))
            probeSnapshot.ReleaseSimulator();

        int repeatedAutoPlayBranchQuota = choiceSpec?.Effect == PlanChoiceEffect.AutoPlayRepeated
            ? Math.Max(1, _profile.MaxHandChoiceBranchesPerAction / Math.Max(1, choices.Count))
            : int.MaxValue;

        foreach (PlanCardChoice? choice in choices)
        {
            if (unregisteredPendingChoice)
            {
                throw new InvalidOperationException(
                    $"卡牌 {action.CardId} 产生了未登记的分支选择，不能静默回退到原生重扫。");
            }
            PlanAction resolvedAction = action with { Choice = choice };
            SimulationSnapshot childSnapshot;
            if (choice == null)
            {
                childSnapshot = probeSnapshot;
            }
            else
            {
                SimulationSnapshot? replayedChoice = ReplayPlannedChoiceBranch(node, resolvedAction);
                if (replayedChoice == null)
                    continue;
                childSnapshot = replayedChoice;
            }
            foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                     ResolveRoundChoiceBranches(
                         node,
                         resolvedAction,
                         childSnapshot,
                         maxFinalBranches: repeatedAutoPlayBranchQuota))
            {
                yield return (finalAction, finalSnapshot);
            }
        }
    }

    private static CardChoiceSpec? BuildRequiredEmptyChoiceSpec(PlanCardChoice? requiredEmptyChoice)
        => requiredEmptyChoice == null
            ? null
            : new CardChoiceSpec(
                requiredEmptyChoice.Effect,
                requiredEmptyChoice.SourcePile,
                0,
                0,
                [],
                [],
                ReplacementValue: 0d);

    private static bool HasChoiceBeforePrimary(
        SimulationSnapshot snapshot,
        CardChoiceSpec? primaryChoiceSpec)
    {
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
            return false;
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (combat.PendingTurnStartChoice is not { } request)
            return combat.PendingKnowledgeDemonChoice != null;
        return !MatchesPrimaryChoice(request, primaryChoiceSpec);
    }

    private static bool MatchesPrimaryChoice(
        TurnStartChoiceRequest request,
        CardChoiceSpec? primaryChoiceSpec)
        => primaryChoiceSpec != null
            && string.IsNullOrEmpty(request.SourceId)
            && string.IsNullOrEmpty(request.ContextId)
            && request.Effect == primaryChoiceSpec.Effect
            && request.SourcePile == primaryChoiceSpec.SourcePile
            && request.Count == primaryChoiceSpec.MinCount;

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolveRoundChoiceBranches(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot snapshot,
        CardChoiceSpec? unresolvedPrimaryChoice = null,
        int maxFinalBranches = int.MaxValue)
    {
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
        {
            yield return (action, snapshot);
            yield break;
        }

        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (combat.PendingKnowledgeDemonChoice is { } knowledgeRequest)
        {
            IReadOnlyList<PlanCardChoice> branches = KnowledgeDemonChoiceSupport.BuildChoices(
                knowledgeRequest,
                displayNames);
            _run.ChoiceBranchesEvaluated += Math.Min(branches.Count, maxFinalBranches);
            snapshot.ReleaseSimulator();
            int yielded = 0;
            foreach (PlanCardChoice branch in branches)
            {
                IReadOnlyList<PlanCardChoice> existing = action.TurnStartChoices ?? [];
                List<PlanCardChoice> next = new(existing.Count + 1);
                next.AddRange(existing);
                next.Add(branch);
                PlanAction resolvedAction = action with
                {
                    TurnStartChoices = next,
                };
                SimulationSnapshot resolvedSnapshot = ReplayAction(node, resolvedAction);
                foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                         ResolveRoundChoiceBranches(
                             node,
                             resolvedAction,
                             resolvedSnapshot,
                             unresolvedPrimaryChoice,
                             maxFinalBranches - yielded))
                {
                    yield return (finalAction, finalSnapshot);
                    if (++yielded >= maxFinalBranches)
                        yield break;
                }
            }
            yield break;
        }

        if (combat.PendingTurnStartChoice is { } request)
        {
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
            CardChoiceSpec spec = TurnStartChoiceSupport.BuildPendingSpec(simulator, combat, _player);
            IReadOnlyList<PlanCardChoice> branches = CardChoiceSupport.BuildChoices(
                spec,
                displayNames,
                _profile.MaxPileChoiceBranchesPerAction,
                _profile.MaxHandChoiceBranchesPerAction);
            _run.ChoiceBranchesEvaluated += Math.Min(branches.Count, maxFinalBranches);
            snapshot.ReleaseSimulator();
            int yielded = 0;
            foreach (PlanCardChoice branch in branches)
            {
                bool turnResolution = action.Kind == PlanActionKind.EndTurn
                    || snapshot.Turn > node.Turn;
                bool primaryChoice = !turnResolution
                    && action.Choice == null
                    && MatchesPrimaryChoice(request, unresolvedPrimaryChoice);
                IReadOnlyList<PlanCardChoice> existing = turnResolution
                    ? action.TurnStartChoices ?? []
                    : action.NestedChoices ?? [];
                List<PlanCardChoice> next = new(existing.Count + 1);
                next.AddRange(existing);
                PlanCardChoice resolvedBranch = branch with
                {
                    SourceId = request.SourceId,
                    ContextId = request.ContextId,
                    Timing = request.Timing,
                };
                if (!primaryChoice)
                    next.Add(resolvedBranch);
                PlanAction resolvedAction = turnResolution
                    ? action with { TurnStartChoices = next }
                    : primaryChoice
                        ? action with { Choice = resolvedBranch }
                        : action with
                        {
                            NestedChoices = next,
                            NestedChoicesBeforePrimary = action.Choice == null
                                ? action.NestedChoicesBeforePrimary + 1
                                : action.NestedChoicesBeforePrimary,
                        };
                SimulationSnapshot resolvedSnapshot;
                try
                {
                    resolvedSnapshot = ReplayAction(node, resolvedAction);
                }
                catch (InvalidPlannedChoiceBranchException ex)
                {
                    if (_detailedDiagnostics)
                    {
                        policy.Diagnostics.Debug(
                            $"[CombatSolver/Test] CHOICE_REPLAY_PRUNED action={PolicyActionToken(resolvedAction)} " +
                            $"reason={ex.Message}");
                    }
                    continue;
                }
                foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                         ResolveRoundChoiceBranches(
                             node,
                             resolvedAction,
                             resolvedSnapshot,
                             unresolvedPrimaryChoice,
                             maxFinalBranches - yielded))
                {
                    yield return (finalAction, finalSnapshot);
                    if (++yielded >= maxFinalBranches)
                        yield break;
                }
            }
            yield break;
        }
        throw new InvalidOperationException(
            $"动作 {PolicyActionToken(action)} 产生了未登记的分支选择，不能留下等待原生结算的搜索边界。");
    }

    private IReadOnlyList<(IReadOnlyList<PlanCardChoice> Choices, SimulationSnapshot Snapshot)>
        BuildTurnSetupRoots()
    {
        List<(IReadOnlyList<PlanCardChoice>, SimulationSnapshot)> roots = [];
        ResolveTurnSetupChoices([], ReplayTurnSetup([]), roots);
        return roots;
    }

    private void ResolveTurnSetupChoices(
        IReadOnlyList<PlanCardChoice> choices,
        SimulationSnapshot snapshot,
        List<(IReadOnlyList<PlanCardChoice>, SimulationSnapshot)> roots)
    {
        if (snapshot.BoundaryReason == SearchBoundaryReason.None)
        {
            roots.Add((choices, snapshot));
            return;
        }
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
        {
            snapshot.ReleaseSimulator();
            return;
        }

        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        TurnStartChoiceRequest request = combat.PendingTurnStartChoice
            ?? throw new InvalidOperationException("回合准备阶段产生了未登记的选择类型。");
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)snapshot.Simulator;
        CardChoiceSpec spec = TurnStartChoiceSupport.BuildPendingSpec(simulator, combat, _player);
        IReadOnlyList<PlanCardChoice> branches = CardChoiceSupport.BuildChoices(
            spec,
            displayNames,
            _profile.MaxPileChoiceBranchesPerAction,
            _profile.MaxHandChoiceBranchesPerAction);
        _run.ChoiceBranchesEvaluated += branches.Count;
        snapshot.ReleaseSimulator();

        foreach (PlanCardChoice branch in branches)
        {
            List<PlanCardChoice> next = new(choices.Count + 1);
            next.AddRange(choices);
            next.Add(branch with
            {
                SourceId = request.SourceId,
                ContextId = request.ContextId,
                Timing = request.Timing,
            });
            SimulationSnapshot resolved;
            try
            {
                resolved = ReplayTurnSetup(next);
            }
            catch (InvalidPlannedChoiceBranchException ex)
            {
                if (_detailedDiagnostics)
                {
                    policy.Diagnostics.Debug(
                        $"[CombatSolver/Test] INITIAL_CHOICE_REPLAY_PRUNED " +
                        $"source={request.SourceId} reason={ex.Message}");
                }
                continue;
            }
            ResolveTurnSetupChoices(next, resolved, roots);
        }
    }

    private SimulationSnapshot ReplayTurnSetup(IReadOnlyList<PlanCardChoice> choices)
    {
        _run.WorkPacer.YieldIfNeeded();
        _run.ReplayCount++;
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        ForkableSet<uint> processedEnemyDeaths = [];
        foreach (Creature enemy in root.Enemies)
        {
            if (enemy.CombatId is uint combatId && simulator.State.GetCreature(enemy).IsDead)
                processedEnemyDeaths.Add(combatId);
        }

        TurnStartChoiceCursor cursor = new(choices);
        simulatedCombat.BeginActionChoices(cursor);
        simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
        SearchBoundaryReason boundary;
        try
        {
            boundary = PreparePlayerPlayPhase(
                simulator,
                simulatedCombat,
                cursor,
                processedEnemyDeaths);
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
        if (boundary == SearchBoundaryReason.None)
            SettleReplayActionBoundary(simulator, simulatedCombat);
        return Snapshot(
            simulator,
            _startTurnNumber,
            actionCount: 0,
            shufflesCrossed: simulator.ShuffleEventCount,
            boundary,
            processedEnemyDeaths);
    }

    private SearchBoundaryReason PreparePlayerPlayPhase(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        TurnStartChoiceCursor choices,
        ISet<uint> processedEnemyDeaths)
    {
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        if (PersistentRelicSupport.ShouldPlayerResetEnergy(simulatedCombat, _player))
            playerState.LoseEnergy(playerState.Energy);
        playerState.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(simulatedCombat, _player));
        TurnStartRelicSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, _player);
        PersistentPowerSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, _player);
        TurnStartRelicSupport.TriggerAfterEnergyResetLate(simulator, simulatedCombat, _player);
        simulatedCombat.ClearPendingTurnStartChoice();
        bool sideTurnStartTriggeredEarly = false;
        using (choices.BeforeNextTake(() =>
               {
                   simulatedCombat.TriggerSideTurnStart(
                       simulator,
                       CombatSide.Player,
                       [_player.Creature],
                       decrementPlating: _startTurnNumber != 1);
                   sideTurnStartTriggeredEarly = true;
               }))
            {
                if (simulatedCombat.PrepareBeforeHandDraw(simulator, _player, choices))
                    return SearchBoundaryReason.PendingChoice;

                int drawCount = PersistentPowerSupport.ConsumeModifiedHandDraw(
                    simulatedCombat,
                    _player,
                    CombatManager.baseHandDrawCount);
                if (_startTurnNumber == 1)
                {
                    SimCardPile drawPile = playerState.DrawPile;
                    PredictedCard[] bottomCards = drawPile.Cards
                        .Where(card => card.Preview.Enchantment?.ShouldStartAtBottomOfDrawPile ?? false)
                        .ToArray();
                    foreach (PredictedCard card in bottomCards)
                    {
                        drawPile.Remove(card);
                        drawPile.Add(card);
                    }
                    PredictedCard[] innateCards = drawPile.Cards
                        .Where(card => card.Preview.Keywords.Contains(CardKeyword.Innate))
                        .Except(bottomCards)
                        .ToArray();
                    foreach (PredictedCard card in innateCards)
                    {
                        drawPile.Remove(card);
                        drawPile.Insert(0, card);
                    }
                    drawCount = Math.Max(drawCount, innateCards.Length);
                    drawCount = Math.Min(drawCount, simulatedCombat.GetMaxHandSize(_player));
                }

                int historyEntryStart = simulator.History.Entries.Count;
                simulator.Draw(_player, drawCount, fromHandDraw: true);
                if (simulatedCombat.HasPendingChoice)
                    return SearchBoundaryReason.PendingChoice;
                TriggeredPowerSupport.CompensateHistorySince(
                    simulator,
                    simulatedCombat,
                    historyEntryStart);
                if (simulatedCombat.TriggerAfterPlayerTurnStart(
                        simulator,
                        _player.Creature,
                        choices))
                    return SearchBoundaryReason.PendingChoice;
                if (!sideTurnStartTriggeredEarly)
                {
                    simulatedCombat.TriggerSideTurnStart(
                        simulator,
                        CombatSide.Player,
                        [_player.Creature],
                        decrementPlating: _startTurnNumber != 1);
                }
            }
        CorePowerSupport.ApplyEnemyDeathPowers(
            simulator,
            simulatedCombat,
            simulatedCombat.KnownEnemies,
            processedEnemyDeaths);
        if (simulatedCombat.HasPendingChoice)
            return SearchBoundaryReason.PendingChoice;
        EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(simulator, _player);
        if (simulatedCombat.TriggerAutoPrePlayEarly(
                simulator,
                _player,
                _startTurnNumber,
                choices,
                processedEnemyDeaths))
        {
            return SearchBoundaryReason.PendingChoice;
        }
        choices.AssertConsumed();
        simulatedCombat.NormalizeAeonglassWithers(simulator);
        simulatedCombat.NormalizeCardAfflictions(simulator);
        IReadOnlyList<ForecastMove> moves = simulatedCombat.CurrentMonsterMoves();
        simulatedCombat.SetPredictedEnemyIntents(
            moves.Where(move => move.AttackHits.Count > 0).Select(move => move.Owner));
        return SearchBoundaryReason.None;
    }

    private SimulationSnapshot Replay(
        IReadOnlyList<PlanAction> actions,
        SimulationSnapshot? parentSnapshot = null,
        int startingTurn = 0,
        int priorActionCount = 0,
        ActionRelicTriggerRecorder? triggerRecorder = null)
    {
        _run.WorkPacer.YieldIfNeeded();
        CombatPredictionSimulator simulator;
        SimulatedCombatState simulatedCombat;
        int turn;
        int shufflesCrossed;
        SearchBoundaryReason boundary = SearchBoundaryReason.None;
        ForkableSet<uint> processedEnemyDeaths;
        if (parentSnapshot is null)
        {
            _run.ReplayCount++;
            simulator = root.ForkSimulator();
            simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
            turn = _startTurnNumber;
            shufflesCrossed = 0;
            processedEnemyDeaths = [];
            foreach (Creature enemy in root.Enemies)
            {
                if (enemy.CombatId is uint combatId && simulator.State.GetCreature(enemy).IsDead)
                    processedEnemyDeaths.Add(combatId);
            }
        }
        else
        {
            if (parentSnapshot.BoundaryReason != SearchBoundaryReason.None)
                throw new InvalidOperationException("不能从已抵达搜索边界的模拟状态继续分叉。");
            _run.ForkCount++;
            _run.TransitionCount += actions.Count;
            SearchMeasurement forkMeasurement = _run.Performance.Begin();
            simulator = ((CombatPredictionSimulator)parentSnapshot.Simulator).Fork();
            _run.Performance.End(SearchMetricPhase.Fork, forkMeasurement);
            simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
            turn = startingTurn;
            shufflesCrossed = parentSnapshot.ShufflesCrossed;
            processedEnemyDeaths = ((ForkableSet<uint>)parentSnapshot.ProcessedEnemyDeaths).Fork();
        }
        if (triggerRecorder != null)
            simulator.ActionRelicTriggers = triggerRecorder;

        SearchMeasurement actionMeasurement = _run.Performance.Begin();
        for (int actionOffset = 0; actionOffset < actions.Count; actionOffset++)
        {
            PlanAction action = actions[actionOffset];
            triggerRecorder?.BeginAction(priorActionCount + actionOffset);
            cancellationToken.ThrowIfCancellationRequested();
            if (action.Kind == PlanActionKind.EndTurn)
            {
                SearchMeasurement roundMeasurement = _run.Performance.Begin();
                try
                {
                    boundary = AdvanceRound(
                        simulator,
                        simulatedCombat,
                        turn - _startTurnNumber,
                        processedEnemyDeaths,
                        ref shufflesCrossed,
                        action.TurnStartChoices);
                }
                finally
                {
                    _run.Performance.End(SearchMetricPhase.RoundAdvance, roundMeasurement);
                }
                _ = simulatedCombat.ConsumePlayerTurnEndRequest();
                if (boundary == SearchBoundaryReason.None)
                    SettleReplayActionBoundary(simulator, simulatedCombat);
                turn++;
                LogAnnotatedReplayState(simulator, action, priorActionCount + actionOffset, turn);
                continue;
            }

            if (action.Kind == PlanActionKind.UsePotion)
            {
                PotionModel potion = simulatedCombat.GetPotionAtSlot(_player, action.PotionSlot)
                    ?? throw new InvalidOperationException($"回放时药水槽位 {action.PotionSlot} 为空。");
                if (!string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"回放时药水槽位 {action.PotionSlot} 为 {potion.Id.Entry}，预期 {action.PotionId}。");
                }
                if (!simulatedCombat.IsPotionAvailable(_player, action.PotionSlot))
                    throw new InvalidOperationException($"回放时药水 {action.PotionId} 已被消耗。");
                Creature? potionTarget = simulatedCombat.GetCreature(action.TargetCombatId);
                int potionShuffleEvents = simulator.ShuffleEventCount;
                int potionHistoryEntryStart = simulator.History.Entries.Count;
                SearchMeasurement potionMeasurement = _run.Performance.Begin();
                simulatedCombat.BeginActionChoices(action.NestedChoices);
                try
                {
                    simulatedCombat.ConsumePotion(_player, action.PotionSlot);
                    simulatedCombat.BeforePotionUsed(simulator, potion, potionTarget);
                    PotionOnUseSupport.Use(simulator, simulatedCombat, potion, potionTarget);
                    if (action.Choice != null)
                        PotionChoiceSupport.Apply(simulator, potion, action.Choice);
                    if (simulator.State.GetCreature(potion.Owner.Creature).IsAlive)
                        simulatedCombat.AfterPotionUsed(simulator, potion, potionTarget);
                    simulator.SynchronizePowerAmountPredictionStates();
                    PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, simulatedCombat);
                    TriggeredPowerSupport.CompensateHistorySince(
                        simulator,
                        simulatedCombat,
                        potionHistoryEntryStart);
                    CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator, simulatedCombat, simulatedCombat.KnownEnemies, processedEnemyDeaths);
                    if (simulatedCombat.HasPendingChoice)
                    {
                        boundary = SearchBoundaryReason.PendingChoice;
                        break;
                    }
                    SettleReplayActionBoundary(simulator, simulatedCombat);
                }
                finally
                {
                    simulatedCombat.EndActionChoices();
                    _run.Performance.End(SearchMetricPhase.PotionExecution, potionMeasurement);
                }
                if (simulator.ShuffleEventCount != potionShuffleEvents)
                {
                    shufflesCrossed++;
                }
                LogAnnotatedReplayState(simulator, action, priorActionCount + actionOffset, turn);
                continue;
            }

            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
            PredictedCard? card = FindCardForReplay(playerState.Hand.Cards, action)
                ?? throw new InvalidOperationException($"回放时找不到手牌 {action.CardId}#{action.CardOccurrence}。");
            Creature? target = simulatedCombat.GetCreature(action.TargetCombatId);
            if (!simulatedCombat.CanPlayCard(simulator, card))
            {
                int energyCost = card.GetEnergyCostWithModifiers(simulator, playerState);
                int starCost = card.GetStarCostWithModifiers(simulator, playerState);
                string hand = string.Join(',', playerState.Hand.Cards.Select(candidate =>
                    $"{candidate.Preview.Id.Entry}#{candidate.Preview.CurrentUpgradeLevel}"));
                string choiceCards = action.Choice == null
                    ? "-"
                    : string.Join(',', action.Choice.Cards.Select(token =>
                        $"{token.CardId}#{token.UpgradeLevel}@{token.SourceOccurrence}"));
                throw new InvalidOperationException(
                    $"回放时 {action.CardId} 已不可打出：turn={turn} " +
                    $"action_index={priorActionCount + actionOffset} occurrence={action.CardOccurrence} " +
                    $"energy={playerState.Energy} cost={energyCost} stars={playerState.Stars} star_cost={starCost} " +
                    $"choice={action.Choice?.Effect.ToString() ?? "-"}:{choiceCards} " +
                    $"hand={hand}。");
            }
            int shuffleEvents = simulator.ShuffleEventCount;
            CardPlayPowerSuppression suppression =
                simulatedCombat.SuppressHistorySensitiveCardModifiers(card);
            SearchMeasurement cardExecutionMeasurement = _run.Performance.Begin();
            simulatedCombat.BeginActionChoices(ActionChoicesForReplay(action));
            using IDisposable cardExecutionScope =
                simulatedCombat.BeginCardExecutionScope(processedEnemyDeaths);
            try
            {
                simulator.ManualPlay(card, target, out _);
            }
            finally
            {
                simulatedCombat.RestoreHistorySensitiveCardModifiers(suppression);
                _run.Performance.End(SearchMetricPhase.CardExecution, cardExecutionMeasurement);
            }
            SearchMeasurement cardPostMeasurement = _run.Performance.Begin();
            try
            {
                CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator, simulatedCombat, simulatedCombat.KnownEnemies, processedEnemyDeaths);
                if (simulatedCombat.HasPendingChoice)
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                    break;
                }
                SettleReplayActionBoundary(simulator, simulatedCombat);
            }
            finally
            {
                simulatedCombat.EndActionChoices();
                _run.Performance.End(SearchMetricPhase.CardPostProcessing, cardPostMeasurement);
            }
            if (simulator.ShuffleEventCount != shuffleEvents)
            {
                shufflesCrossed++;
            }
            bool forcedTurnEnd = simulatedCombat.ConsumePlayerTurnEndRequest();
            if (forcedTurnEnd)
            {
                boundary = AdvanceRound(
                    simulator,
                    simulatedCombat,
                    turn - _startTurnNumber,
                    processedEnemyDeaths,
                    ref shufflesCrossed,
                    action.TurnStartChoices);
                if (boundary == SearchBoundaryReason.None)
                    SettleReplayActionBoundary(simulator, simulatedCombat);
                _ = simulatedCombat.ConsumePlayerTurnEndRequest();
                turn++;
            }
            LogAnnotatedReplayState(simulator, action, priorActionCount + actionOffset, turn);
        }
        _run.Performance.End(SearchMetricPhase.Action, actionMeasurement);

        SearchMeasurement snapshotMeasurement = _run.Performance.Begin();
        SimulationSnapshot snapshot = Snapshot(
            simulator,
            turn,
            priorActionCount + actions.Count,
            shufflesCrossed,
            boundary,
            processedEnemyDeaths);
        _run.Performance.End(SearchMetricPhase.Snapshot, snapshotMeasurement);
        return snapshot;
    }

    private static void SettleReplayActionBoundary(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
    {
        simulator.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        combat.NormalizeAeonglassWithers(simulator);
        combat.NormalizeCardAfflictions(simulator);
    }

    private void LogAnnotatedReplayState(
        CombatPredictionSimulator simulator,
        PlanAction action,
        int actionIndex,
        int turn)
    {
        if (!_detailedDiagnostics || simulator.ActionRelicTriggers == null)
            return;

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        string actionToken = action.Kind switch
        {
            PlanActionKind.PlayCard => action.CardId,
            PlanActionKind.UsePotion => action.PotionId,
            _ => action.Kind.ToString(),
        };
        policy.Diagnostics.Info(
            $"[CombatSolver/Debug] PLAN_REPLAY_STATE turn={turn} action_index={actionIndex} " +
            $"action={actionToken} " +
            $"energy={playerState.Energy} hand={string.Join(',', playerState.Hand.Cards.Select(card => card.Preview.Id.Entry))} " +
            $"draw={string.Join(',', playerState.DrawPile.Cards.Select(card => card.Preview.Id.Entry))} " +
            $"discard={string.Join(',', playerState.DiscardPile.Cards.Select(card => card.Preview.Id.Entry))} " +
            $"exhaust={string.Join(',', playerState.ExhaustPile.Cards.Select(card => card.Preview.Id.Entry))} " +
            $"enemies={string.Join(',', root.Enemies.Select(enemy =>
                $"{enemy.Monster?.Id.Entry ?? "null"}:{simulator.State.GetCreature(enemy).CurrentHp}/{simulator.State.GetCreature(enemy).Block}"))}");
    }

    private SimulationSnapshot ReplayAction(SearchNode parent, PlanAction action)
    {
        return SearchTransitionGuard.Execute(
            action,
            parent.StateKey,
            parent.ActionCount,
            () =>
        {
            SimulationSnapshot incremental = Replay(
                [action],
                parent.Snapshot,
                parent.Turn,
                parent.ActionCount);
            if (!policy.VerifyIncrementalSearch)
                return incremental;

            List<PlanAction> fullActions = new(parent.ActionCount + 1);
            fullActions.AddRange(parent.Actions);
            fullActions.Add(action);
            SimulationSnapshot? fullReplayRoot = _includeTurnSetup
                ? ReplayTurnSetup(parent.GetTurnSetupChoices())
                : null;
            SimulationSnapshot replayed;
            try
            {
                replayed = Replay(
                    fullActions,
                    fullReplayRoot,
                    _startTurnNumber,
                    priorActionCount: 0);
            }
            finally
            {
                fullReplayRoot?.ReleaseSimulator();
            }
            try
            {
                AssertIncrementalEquivalent(action, fullActions, incremental, replayed);
            }
            finally
            {
                replayed.ReleaseSimulator();
            }
            return incremental;
        });
    }

    private SimulationSnapshot? ReplayPlannedChoiceBranch(SearchNode parent, PlanAction action)
    {
        try
        {
            return ReplayAction(parent, action);
        }
        catch (InvalidPlannedChoiceBranchException ex)
        {
            if (_detailedDiagnostics)
            {
                policy.Diagnostics.Debug(
                    $"[CombatSolver/Test] CHOICE_REPLAY_PRUNED action={PolicyActionToken(action)} " +
                    $"reason={ex.Message}");
            }
            return null;
        }
    }

    private void AssertIncrementalEquivalent(
        PlanAction action,
        IReadOnlyList<PlanAction> fullActions,
        SimulationSnapshot incremental,
        SimulationSnapshot replayed)
    {
        ContinuationStamp incrementalStamp = ContinuationStamp.CapturePredicted(
            _player,
            incremental.Simulator,
            incremental.Turn,
            _forecast,
            _startTurnNumber);
        ContinuationStamp replayedStamp = ContinuationStamp.CapturePredicted(
            _player,
            replayed.Simulator,
            replayed.Turn,
            _forecast,
            _startTurnNumber);
        bool equal = incremental.StateKey == replayed.StateKey
            && incremental.Turn == replayed.Turn
            && incremental.Score.Equals(replayed.Score)
            && incremental.BoundaryReason == replayed.BoundaryReason
            && incremental.HasRisk == replayed.HasRisk
            && incremental.PlayerDead == replayed.PlayerDead
            && incremental.AllEnemiesDead == replayed.AllEnemiesDead
            && incrementalStamp == replayedStamp
            && incremental.ProcessedEnemyDeaths.SetEquals(replayed.ProcessedEnemyDeaths)
            && incremental.PredictionGaps.SequenceEqual(replayed.PredictionGaps);
        if (equal)
            return;

        string stateDifferences = string.Join(" || ",
            incrementalStamp.DescribeDifferences(replayedStamp).Take(12));
        throw new InvalidOperationException(
            $"增量分叉与完整回放不一致：action={PolicyActionToken(action)} " +
            $"prefix={string.Join('|', fullActions.Select(PolicyActionToken))} " +
            $"state_diffs={stateDifferences} " +
            $"incremental_boundary={incremental.BoundaryReason} replayed_boundary={replayed.BoundaryReason} " +
            $"incremental_key={incremental.StateKey} replayed_key={replayed.StateKey}");
    }

    internal static PredictedCard? FindCardForReplay(
        IReadOnlyList<PredictedCard> cards,
        PlanAction action)
    {
        if (!string.IsNullOrEmpty(action.CardStateKey))
        {
            int occurrence = action.CardStateOccurrence;
            foreach (PredictedCard card in cards)
            {
                if (!string.Equals(
                        CardChoiceSupport.ChoiceCardKey(card),
                        action.CardStateKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (occurrence-- == 0)
                    return card;
            }
            return null;
        }
        return FindCardOccurrence(cards, action.CardId, action.CardOccurrence);
    }

    private static PredictedCard? FindCardOccurrence(
        IReadOnlyList<PredictedCard> cards,
        string cardId,
        int occurrence)
    {
        foreach (PredictedCard card in cards)
        {
            if (!string.Equals(card.Preview.Id.Entry, cardId, StringComparison.Ordinal))
                continue;
            if (occurrence-- == 0)
                return card;
        }
        return null;
    }

    private SearchBoundaryReason AdvanceRound(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        ref int shufflesCrossed,
        IReadOnlyList<PlanCardChoice>? turnStartChoices)
    {
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        IReadOnlyList<PlanCardChoice>? roundChoicePlans = turnStartChoices?
            .Where(choice => choice.Effect != PlanChoiceEffect.ApplyKnowledgeCurse)
            .ToArray();
        TurnStartChoiceCursor roundChoices = new(roundChoicePlans);
        simulatedCombat.BeginActionChoices(roundChoices);
        simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnEnd);
        try
        {
        int roundHistoryEntryStart = simulator.History.Entries.Count;
        bool takingExtraTurn = simulatedCombat.PrepareExtraPlayerTurn(
            simulator,
            _player,
            out bool hasActiveEmotionChip);
        int etherealExhaustCount = simulatedCombat.CountEtherealCardsInHand(simulator, _player);
        {
            using SearchMeasurementScope _ = _run.Performance.Measure(SearchMetricPhase.RoundPlayerEnd);
            using (_run.Performance.Measure(SearchMetricPhase.RoundEndSimulation))
                PlayerTurnEndLifecycle.RunPhaseOne(
                    simulator,
                    simulatedCombat,
                    _player,
                    [_player.Creature]);
            simulatedCombat.CommitHistoryCourseTurn(_player);
            simulatedCombat.NormalizeAeonglassWithers(simulator);
            simulatedCombat.NormalizeCardAfflictions(simulator);
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator, simulatedCombat, simulatedCombat.KnownEnemies, processedEnemyDeaths);
            if (simulatedCombat.HasPendingChoice)
                return SearchBoundaryReason.PendingChoice;
            using (_run.Performance.Measure(SearchMetricPhase.RoundFlush))
                CorePowerSupport.FlushPlayerHandAtTurnEnd(simulator, simulatedCombat, _player);
            int turnEndShuffleEvents = simulator.ShuffleEventCount;
            TurnStartRelicSupport.TriggerAfterSideTurnEnd(
                simulator,
                simulatedCombat,
                [_player.Creature],
                etherealExhaustCount);
            shufflesCrossed += simulator.ShuffleEventCount - turnEndShuffleEvents;
            using (_run.Performance.Measure(SearchMetricPhase.RoundPlayerEndPowers))
            {
                CorePowerSupport.TriggerPlayerSideTurnEndEffects(
                    simulator,
                    simulatedCombat,
                    [_player.Creature],
                    etherealExhaustCount);
            }
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator, simulatedCombat, simulatedCombat.KnownEnemies, processedEnemyDeaths);
            if (simulatedCombat.HasPendingChoice)
                return SearchBoundaryReason.PendingChoice;
        }

        SimCreatureState simulatedPlayer = simulator.State.GetCreature(_player.Creature);
        if (!takingExtraTurn)
        {
            simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.EnemyTurn);
            using SearchMeasurementScope _ = _run.Performance.Measure(SearchMetricPhase.RoundEnemyTurn);
            {
                using SearchMeasurementScope enemyStart = _run.Performance.Measure(SearchMetricPhase.RoundEnemyStart);
                simulatedCombat.CurrentSide = CombatSide.Enemy;
                simulatedCombat.SnapshotPowerAmountsAtTurnStart(simulatedCombat.Enemies);
                // 怪物方开始回合时，上一怪物回合留下的格挡先清除。
                TurnStartRelicSupport.TriggerBeforeSideTurnStart(simulator, simulatedCombat, simulatedCombat.Enemies);
                if (TurnStartPowerSupport.TriggerBeforeSideTurnStart(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.Enemies))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                foreach (Creature enemy in simulatedCombat.Enemies)
                {
                    SimCreatureState simulatedEnemy = simulator.State.GetCreature(enemy);
                    if (simulatedEnemy.Block > 0)
                    {
                        if (simulatedCombat.ShouldClearBlock(enemy, out AbstractModel? preventer))
                            simulatedEnemy.DamageBlock(simulatedEnemy.Block, ValueProp.Move);
                        else
                            PersistentRelicSupport.TriggerAfterPreventingBlockClear(simulator, preventer, enemy);
                    }
                    CorePowerSupport.TriggerAfterBlockCleared(simulator, simulatedCombat, enemy);
                }
                bool decrementEnemyPlating = simulatedCombat.RoundNumber > 1;
                simulatedCombat.TriggerSideTurnStart(
                    simulator,
                    CombatSide.Enemy,
                    simulatedCombat.Enemies,
                    decrementEnemyPlating);
                int enemyPoisonHistoryStart = simulator.History.Entries.Count;
                CorePowerSupport.TriggerPoison(
                    simulator,
                    simulatedCombat,
                    simulatedCombat.Enemies.ToArray());
                TriggeredPowerSupport.CompensateHistorySince(
                    simulator,
                    simulatedCombat,
                    enemyPoisonHistoryStart);
                CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator, simulatedCombat, simulatedCombat.KnownEnemies, processedEnemyDeaths);
                if (simulatedCombat.HasPendingChoice)
                    return SearchBoundaryReason.PendingChoice;
            }
            Dictionary<Creature, MoveState> performedMoves;
            {
                using SearchMeasurementScope enemyMoves = _run.Performance.Measure(SearchMetricPhase.RoundEnemyMoves);
                Creature[] actingEnemies = simulatedCombat.Enemies.ToArray();
                performedMoves = new Dictionary<Creature, MoveState>(actingEnemies.Length);
                foreach (Creature actingEnemy in actingEnemies)
                {
                    if (!simulatedCombat.CanPerformMonsterMove(simulator, actingEnemy))
                        continue;
                    ForecastMove move = simulatedCombat.CurrentMonsterMove(actingEnemy);
                    if (simulatedCombat.ConsumeStunNextMove(actingEnemy))
                    {
                        performedMoves[actingEnemy] = move.Move;
                        continue;
                    }
                    if (simulatedCombat.TryConsumeForcedMonsterMove(actingEnemy, out string forcedMove, out int forcedDamage))
                    {
                        performedMoves[actingEnemy] = move.Move;
                        if (forcedMove == "EXPLODE_MOVE")
                        {
                            MonsterMoveSemantics.DamagePlayer(
                                simulator,
                                simulatedCombat,
                                move.Owner,
                                _player.Creature,
                                forcedDamage);
                            simulator.Kill(move.Owner, force: true);
                            CorePowerSupport.ApplyEnemyDeathPowers(
                                simulator, simulatedCombat, simulatedCombat.KnownEnemies, processedEnemyDeaths);
                            if (simulatedCombat.HasPendingChoice)
                                return SearchBoundaryReason.PendingChoice;
                            if (simulatedPlayer.IsDead)
                                return SearchBoundaryReason.None;
                        }
                        continue;
                    }
                    if (MonsterMoveSemantics.ApplyForecastMove(
                            simulator,
                            simulatedCombat,
                            move,
                            _player.Creature,
                            processedEnemyDeaths,
                            turnStartChoices))
                    {
                        return SearchBoundaryReason.None;
                    }
                    performedMoves[actingEnemy] = move.Move;
                    if (move.Owner.CombatId is uint revivedCombatId
                        && simulator.State.GetCreature(move.Owner).IsAlive)
                    {
                        processedEnemyDeaths.Remove(revivedCombatId);
                    }
                    if (simulatedCombat.HasPendingChoice)
                        return SearchBoundaryReason.PendingChoice;
                }
            }

            using (_run.Performance.Measure(SearchMetricPhase.RoundEnemyEndPowers))
            {
                CorePowerSupport.TriggerEnemySideTurnEndEffects(
                    simulator,
                    simulatedCombat,
                    simulatedCombat.Enemies.ToArray());
                if (simulatedCombat.BattlewornDummyTimedOut)
                    return SearchBoundaryReason.EventDefeat;
                CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator, simulatedCombat, simulatedCombat.KnownEnemies, processedEnemyDeaths);
                if (simulatedCombat.HasPendingChoice)
                    return SearchBoundaryReason.PendingChoice;
                int playerPoisonHistoryStart = simulator.History.Entries.Count;
                CorePowerSupport.TriggerPoison(simulator, simulatedCombat, [_player.Creature]);
                TriggeredPowerSupport.CompensateHistorySince(
                    simulator,
                    simulatedCombat,
                    playerPoisonHistoryStart);
                simulatedCombat.ClearNoDraw(_player.Creature);
                simulatedCombat.RecordRelicRoundDamage(simulator, _player, roundHistoryEntryStart);
            }
            simulatedCombat.PrepareMonsterMovesForNextRound(simulator, performedMoves);
        }
        else
        {
            // An extra turn advances the player's turn number too, so damage from the
            // just-finished turn becomes Emotion Chip's "previous turn" window.
            if (hasActiveEmotionChip)
                simulatedCombat.RecordRelicRoundDamage(simulator, _player, roundHistoryEntryStart);
            simulatedCombat.ConsumeExtraTurnSources(_player);
        }

        {
            simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
            using SearchMeasurementScope _ = _run.Performance.Measure(SearchMetricPhase.RoundPlayerStart);
            simulatedCombat.CurrentSide = CombatSide.Player;
            if (!takingExtraTurn)
                simulatedCombat.RoundNumber++;
            simulatedCombat.AdvancePlayerTurn(_player);
            simulatedCombat.SnapshotPowerAmountsAtTurnStart([_player.Creature]);

            TurnStartRelicSupport.TriggerBeforeSideTurnStart(simulator, simulatedCombat, [_player.Creature]);
            if (TurnStartPowerSupport.TriggerBeforeSideTurnStart(
                    simulator,
                    simulatedCombat,
                    [_player.Creature]))
            {
                return SearchBoundaryReason.PendingChoice;
            }

            if (simulatedPlayer.Block > 0)
            {
                if (simulatedCombat.ShouldClearBlock(_player.Creature, out AbstractModel? preventer))
                    simulatedPlayer.DamageBlock(simulatedPlayer.Block, ValueProp.Move);
                else
                    PersistentRelicSupport.TriggerAfterPreventingBlockClear(
                        simulator,
                        preventer,
                        _player.Creature);
            }
            CorePowerSupport.TriggerAfterBlockCleared(
                simulator,
                simulatedCombat,
                _player.Creature);

            if (PersistentRelicSupport.ShouldPlayerResetEnergy(simulatedCombat, _player))
                playerState.LoseEnergy(playerState.Energy);
            playerState.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(simulatedCombat, _player)
                + simulatedCombat.ConsumeEnergyNextTurn(_player));
            TurnStartRelicSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, _player);
            PersistentPowerSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, _player);
            TurnStartRelicSupport.TriggerAfterEnergyResetLate(simulator, simulatedCombat, _player);
            simulatedCombat.ClearPendingTurnStartChoice();
            int beforeHandDrawShuffleEvents = simulator.ShuffleEventCount;
            bool sideTurnStartTriggeredEarly = false;
            using (roundChoices.BeforeNextTake(() =>
                   {
                       simulatedCombat.TriggerSideTurnStart(
                           simulator,
                           CombatSide.Player,
                           [_player.Creature],
                           decrementPlating: simulatedCombat.GetPlayerTurnNumber(_player) != 1,
                           takingExtraTurn);
                       sideTurnStartTriggeredEarly = true;
                   }))
            {
                if (simulatedCombat.PrepareBeforeHandDraw(simulator, _player, roundChoices))
                    return SearchBoundaryReason.PendingChoice;
                shufflesCrossed += simulator.ShuffleEventCount - beforeHandDrawShuffleEvents;
                int drawCount = PersistentPowerSupport.ConsumeModifiedHandDraw(
                    simulatedCombat,
                    _player,
                    CombatManager.baseHandDrawCount);
                int effectiveDraw = Math.Min(
                    drawCount,
                    simulatedCombat.GetMaxHandSize(_player) - playerState.Hand.Cards.Count);
                bool willShuffle = effectiveDraw > playerState.DrawPile.Cards.Count
                    && !playerState.DiscardPile.IsEmpty;
                int historyEntryStart = simulator.History.Entries.Count;
                using (_run.Performance.Measure(SearchMetricPhase.RoundDraw))
                    simulator.Draw(_player, drawCount, fromHandDraw: true);
                if (willShuffle)
                    shufflesCrossed++;
                if (simulatedCombat.HasPendingChoice)
                    return SearchBoundaryReason.PendingChoice;
                TriggeredPowerSupport.CompensateHistorySince(simulator, simulatedCombat, historyEntryStart);
                if (simulatedCombat.TriggerAfterPlayerTurnStart(
                        simulator,
                        _player.Creature,
                        roundChoices))
                    return SearchBoundaryReason.PendingChoice;
                if (!sideTurnStartTriggeredEarly)
                {
                    simulatedCombat.TriggerSideTurnStart(
                        simulator,
                        CombatSide.Player,
                        [_player.Creature],
                        decrementPlating: simulatedCombat.GetPlayerTurnNumber(_player) != 1,
                        takingExtraTurn);
                }
            }
            CorePowerSupport.ApplyEnemyDeathPowers(
                simulator, simulatedCombat, simulatedCombat.KnownEnemies, processedEnemyDeaths);
            if (simulatedCombat.HasPendingChoice)
                return SearchBoundaryReason.PendingChoice;
            EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(simulator, _player);
            if (simulatedCombat.TriggerAutoPrePlayEarly(
                    simulator,
                    _player,
                    _startTurnNumber + roundIndex + 1,
                    roundChoices,
                    processedEnemyDeaths))
            {
                return SearchBoundaryReason.PendingChoice;
            }
            roundChoices.AssertConsumed();
            simulatedCombat.NormalizeAeonglassWithers(simulator);
            simulatedCombat.NormalizeCardAfflictions(simulator);
            IReadOnlyList<ForecastMove> nextMoves = simulatedCombat.CurrentMonsterMoves();
            simulatedCombat.SetPredictedEnemyIntents(
                nextMoves.Where(move => move.AttackHits.Count > 0).Select(move => move.Owner));
            return SearchBoundaryReason.None;
        }
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

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
        bool pure = simulator.History.Entries
            .Skip(before.HistoryEntryCount)
            .All(IsPureHistoryEntry);
        SimulatedCombatState beforeCombat = (SimulatedCombatState)
            ((CombatPredictionSimulator)before.Simulator).State.CombatState;
        bool declinedExtraTurn = beforeCombat.RelicsOf(_player)
            .OfType<PaelsEye>()
            .Any(relic => !relic.IsMelted && beforeCombat.IsPaelsEyeUnused(relic));
        SearchRouteTraits traits = ClassifyCardTraits(
            node.Traits,
            before,
            after,
            damage,
            block,
            pure,
            declinedExtraTurn);
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
            pure,
            normalized);
    }

    private SearchRouteTraits ClassifyCardTraits(
        SearchRouteTraits current,
        SimulationSnapshot before,
        SimulationSnapshot after,
        int damage,
        int block,
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
        if (after.ReactiveDamageValue > before.ReactiveDamageValue)
            traits |= SearchRouteTraits.ReactiveDamage;
        if (after.Energy > before.Energy
            || after.Stars > before.Stars
            || after.HandCount >= before.HandCount
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
            || !pure && (_profile.Phase == SolverSearchPhase.Deep || damage == 0 && block == 0))
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

    private static bool Dominates(ActionCandidate left, ActionCandidate right)
    {
        if (ReferenceEquals(left.Node, right.Node)
            || !left.IsPure
            || !right.IsPure
            || left.CardType != right.CardType
            || left.TargetCombatId != right.TargetCombatId
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

    private void AddNonDominatedCandidate(List<ActionCandidate> candidates, ActionCandidate candidate)
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

    private bool TryAcceptTransposition(SearchNode candidate)
    {
        TranspositionLabel next = new(
            candidate.PotionCount,
            candidate.PotionStrategicCost,
            candidate.FutureSoldHp,
            candidate.Snapshot.CumulativePlayerHpLost,
            candidate.ActionCount,
            candidate.Score);
        if (!_run.Transpositions.TryGetValue(candidate.StateKey, out TranspositionFrontier? frontier))
        {
            _run.Transpositions.Add(candidate.StateKey, new TranspositionFrontier(next));
            return true;
        }
        if (frontier.TryAccept(next))
            return true;
        _run.TranspositionBranchesPruned++;
        return false;
    }

    private bool TryMarkExpandedState(SearchNode node)
    {
        TranspositionLabel next = new(
            node.PotionCount,
            node.PotionStrategicCost,
            node.FutureSoldHp,
            node.Snapshot.CumulativePlayerHpLost,
            node.ActionCount,
            node.Score);
        if (!_run.ExpandedTranspositions.TryGetValue(node.StateKey, out TranspositionFrontier? frontier))
        {
            _run.ExpandedTranspositions.Add(node.StateKey, new TranspositionFrontier(next));
            return true;
        }
        if (frontier.TryAccept(next))
            return true;
        _run.TranspositionBranchesPruned++;
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
            choices.AddRange(action.TurnStartChoices);
        return choices.Count == 0 ? null : choices;
    }

}
