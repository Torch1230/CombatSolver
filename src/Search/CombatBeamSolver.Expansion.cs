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
using MegaCrit.Sts2.Core.Models.Potions;
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
    internal IReadOnlyList<PlanAction> BuildOpeningPowerActions()
        => BuildPowerActionsAfterPrefix([]);

    internal IReadOnlyList<PlanAction> BuildPowerActionsAfterPrefix(IReadOnlyList<PlanAction> prefix)
    {
        SimulationSnapshot prefixSnapshot = Replay(prefix);
        try
        {
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)prefixSnapshot.Simulator;
            SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
            IReadOnlyList<PredictedCard> hand = playerState.Hand.Cards;
            List<PlanAction> actions = [];
            HashSet<string> seenCardStates = [];
            SearchNode seed = new(
                null,
                0,
                prefixSnapshot.PotionUseCount,
                prefixSnapshot.PotionStrategicCost,
                prefixSnapshot.Turn,
                SearchRouteTraits.None,
                0,
                prefixSnapshot.Score,
                prefixSnapshot.StateKey,
                prefixSnapshot.HasRisk,
                prefixSnapshot.BoundaryReason,
                false,
                null,
                prefixSnapshot,
                CombatProgressState.Capture(prefixSnapshot));

            for (int handIndex = 0; handIndex < hand.Count; handIndex++)
            {
                PredictedCard card = hand[handIndex];
                if (card.Preview.Type != CardType.Power || !combat.CanPlayCard(simulator, card))
                    continue;

                string cardStateKey = CardChoiceSupport.ChoiceCardKey(card);
                if (!seenCardStates.Add(cardStateKey))
                    continue;
                int occurrence = hand.Take(handIndex).Count(candidate =>
                    string.Equals(candidate.Preview.Id.Entry, card.Preview.Id.Entry, StringComparison.Ordinal));
                int cardStateOccurrence = hand.Take(handIndex).Count(candidate =>
                    string.Equals(
                        CardChoiceSupport.ChoiceCardKey(candidate),
                        cardStateKey,
                        StringComparison.Ordinal));
                foreach ((int targetIndex, Creature? target) in TargetsFor(card, simulator))
                {
                    if (!card.Original.CanPlayTargeting(target))
                        continue;
                    PlanAction action = new(
                        PlanActionKind.PlayCard,
                        prefixSnapshot.Turn,
                        card.Preview.Id.Entry,
                        occurrence,
                        targetIndex,
                        target?.CombatId,
                        displayNames.Card(card.Preview),
                        displayNames.Creature(target),
                        ReplayCount: Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                        CardStateKey: cardStateKey,
                        CardStateOccurrence: cardStateOccurrence);
                    SimulationSnapshot probe = ReplayAction(seed, action);
                    try
                    {
                        if (probe.BoundaryReason == SearchBoundaryReason.None
                            && probe.Turn == prefixSnapshot.Turn
                            && CardChoiceSupport.GetSpec(
                                (CombatPredictionSimulator)probe.Simulator,
                                card) == null)
                        {
                            actions.Add(action);
                        }
                    }
                    finally
                    {
                        probe.ReleaseSimulator();
                    }
                }
            }
            return actions;
        }
        finally
        {
            prefixSnapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildOpeningPotionActions()
        => BuildPotionActionsAfterPrefix([]);

    internal IReadOnlyList<PlanAction> BuildPotionActionsAfterPrefix(IReadOnlyList<PlanAction> prefix)
    {
        SimulationSnapshot rootSnapshot = Replay(prefix);
        List<SearchNode> children = [];
        try
        {
            SearchNode seed = new(
                null,
                0,
                rootSnapshot.PotionUseCount,
                rootSnapshot.PotionStrategicCost,
                rootSnapshot.Turn,
                SearchRouteTraits.None,
                0,
                rootSnapshot.Score,
                rootSnapshot.StateKey,
                rootSnapshot.HasRisk,
                rootSnapshot.BoundaryReason,
                false,
                null,
                rootSnapshot,
                CombatProgressState.Capture(rootSnapshot));
            children.AddRange(Expand(seed));
            return children
                .Where(node => node.Action?.Kind == PlanActionKind.UsePotion)
                .OrderByDescending(node => node.Score)
                .Select(node => node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            rootSnapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildPreferredOpeningPotionActions()
        => BuildPreferredPotionActionsAfterPrefix([]);

    internal IReadOnlyList<PlanAction> BuildOpeningResourceActions()
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        List<SearchNode> children = [];
        try
        {
            IReadOnlyList<PredictedCard> openingHand = ((CombatPredictionSimulator)rootSnapshot.Simulator)
                .State.GetPlayerCombatState(_player).Hand.Cards;
            IReadOnlyDictionary<string, CardType> cardTypes = openingHand
                .GroupBy(card => card.Preview.Id.Entry)
                .ToDictionary(group => group.Key, group => group.First().Preview.Type);
            SearchNode seed = new(
                null,
                0,
                rootSnapshot.PotionUseCount,
                rootSnapshot.PotionStrategicCost,
                rootSnapshot.Turn,
                SearchRouteTraits.None,
                0,
                rootSnapshot.Score,
                rootSnapshot.StateKey,
                rootSnapshot.HasRisk,
                rootSnapshot.BoundaryReason,
                false,
                null,
                rootSnapshot,
                CombatProgressState.Capture(rootSnapshot));
            children.AddRange(Expand(seed).Where(node =>
                node.Action is { Kind: PlanActionKind.PlayCard, Turn: var turn }
                && turn == rootSnapshot.Turn));
            return children
                .Where(node => node.Snapshot.Energy > rootSnapshot.Energy
                    || node.Snapshot.Stars > rootSnapshot.Stars
                    || node.Snapshot.HandCount > rootSnapshot.HandCount
                    || node.Snapshot.ReachableHandValue > rootSnapshot.ReachableHandValue
                    || node.Snapshot.ZeroCostPlayableCount > rootSnapshot.ZeroCostPlayableCount
                    || (node.Traits & SearchRouteTraits.Resource) != 0)
                .Select(node => (
                    Node: node,
                    Value: (node.Snapshot.Energy - rootSnapshot.Energy) * 64
                        + (node.Snapshot.Stars - rootSnapshot.Stars) * 48
                        + (node.Snapshot.HandCount - rootSnapshot.HandCount) * 16
                        + node.Snapshot.ReachableHandValue - rootSnapshot.ReachableHandValue
                        + (node.Snapshot.ZeroCostPlayableCount - rootSnapshot.ZeroCostPlayableCount) * 8))
                .GroupBy(candidate => cardTypes[candidate.Node.Action!.CardId])
                .Select(group => group
                    .OrderByDescending(candidate => candidate.Value)
                    .ThenByDescending(candidate => candidate.Node.Score)
                    .First())
                .OrderByDescending(candidate => candidate.Value)
                .ThenByDescending(candidate => candidate.Node.Score)
                .Take(3)
                .Select(candidate => candidate.Node.Action!)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode child in children)
                child.Snapshot.ReleaseSimulator();
            rootSnapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildPreferredPotionActionsAfterPrefix(
        IReadOnlyList<PlanAction> prefix)
    {
        HashSet<uint> setupTargetIds = (_forecast.Rounds.FirstOrDefault() ?? [])
            .Where(move => move.AttackHits.Count == 0 && move.Owner.CombatId.HasValue)
            .Select(move => move.Owner.CombatId!.Value)
            .ToHashSet();
        List<PlanAction> selected = [];
        foreach (IGrouping<int, PlanAction> slotActions in BuildPotionActionsAfterPrefix(prefix)
                     .GroupBy(action => action.PotionSlot))
        {
            selected.Add(slotActions.First());
            PlanAction? setupTargetAction = slotActions.FirstOrDefault(action =>
                action.TargetCombatId is uint targetId && setupTargetIds.Contains(targetId));
            if (setupTargetAction != null && !selected.Contains(setupTargetAction))
                selected.Add(setupTargetAction);
        }
        return selected;
    }

    internal IReadOnlyList<PlanAction> SelectGeneratedResourcePotionActions(
        IReadOnlyList<PlanAction> actions)
        => actions
            .Where(action => action.Choice is
            {
                Effect: PlanChoiceEffect.GenerateToHand,
                Cards.Count: 1,
            })
            .Select(action => (Action: action, Value: GeneratedCardResourceValue(action)))
            .Where(candidate => candidate.Value > 0)
            .GroupBy(candidate => candidate.Action.PotionSlot)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Value)
                .ThenBy(candidate => candidate.Action.Choice!.Cards[0].CardId, StringComparer.Ordinal)
                .First().Action)
            .ToArray();

    private int GeneratedCardResourceValue(PlanAction action)
    {
        SimulationSnapshot snapshot = Replay([action]);
        try
        {
            PlanCardToken token = action.Choice!.Cards[0];
            PredictedCard? card = ((CombatPredictionSimulator)snapshot.Simulator).State
                .GetPlayerCombatState(_player)
                .Hand.Cards
                .LastOrDefault(candidate => CardChoiceSupport.MatchesToken(candidate, token));
            if (card == null)
                return 0;

            int draw = Math.Max(0, (int)CardChoiceSupport.DynamicVarBaseValue(card.Preview.DynamicVars, "Cards"));
            int energy = Math.Max(0, (int)CardChoiceSupport.DynamicVarBaseValue(card.Preview.DynamicVars, "Energy"));
            int stars = Math.Max(0, (int)CardChoiceSupport.DynamicVarBaseValue(card.Preview.DynamicVars, "Stars"));
            return draw * 16 + energy * 16 + stars * 8;
        }
        finally
        {
            snapshot.ReleaseSimulator();
        }
    }

    internal PlanAction? BuildOpeningPowerOffensiveFollowUp(PlanAction openingPower)
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        SimulationSnapshot? powerSnapshot = null;
        List<SearchNode> followUps = [];
        try
        {
            SearchNode seed = new(
                null,
                0,
                rootSnapshot.PotionUseCount,
                rootSnapshot.PotionStrategicCost,
                _startTurnNumber,
                SearchRouteTraits.None,
                0,
                rootSnapshot.Score,
                rootSnapshot.StateKey,
                rootSnapshot.HasRisk,
                rootSnapshot.BoundaryReason,
                false,
                null,
                rootSnapshot,
                CombatProgressState.Capture(rootSnapshot));
            powerSnapshot = ReplayAction(seed, openingPower);
            SearchNode powerNode = new(
                openingPower,
                1,
                powerSnapshot.PotionUseCount,
                powerSnapshot.PotionStrategicCost,
                _startTurnNumber,
                SearchRouteTraits.Scaling,
                0,
                powerSnapshot.Score,
                powerSnapshot.StateKey,
                powerSnapshot.HasRisk,
                powerSnapshot.BoundaryReason,
                false,
                seed,
                powerSnapshot,
                CombatProgressState.Capture(powerSnapshot))
            {
                CumulativeEnemyHpLost = AccumulateEnemyHpLost(seed, powerSnapshot),
            };
            followUps.AddRange(Expand(powerNode).Where(node =>
                node.Action is { Kind: PlanActionKind.PlayCard, Turn: var turn }
                && turn == _startTurnNumber));
            SearchNode? best = followUps
                .Where(node => node.Snapshot.EnemyHp < powerSnapshot.EnemyHp)
                .OrderBy(node => node.Snapshot.EnemyHp)
                .ThenByDescending(node => node.Score)
                .FirstOrDefault();
            return best?.Action;
        }
        finally
        {
            foreach (SearchNode followUp in followUps)
                followUp.Snapshot.ReleaseSimulator();
            powerSnapshot?.ReleaseSimulator();
            rootSnapshot.ReleaseSimulator();
        }
    }

    internal IReadOnlyList<PlanAction> BuildOpeningOffensiveFollowUps(
        IReadOnlyList<PlanAction> prefix)
    {
        SimulationSnapshot prefixSnapshot = Replay(prefix);
        List<SearchNode> followUps = [];
        try
        {
            SearchNode seed = new(
                null,
                0,
                prefixSnapshot.PotionUseCount,
                prefixSnapshot.PotionStrategicCost,
                prefixSnapshot.Turn,
                SearchRouteTraits.None,
                0,
                prefixSnapshot.Score,
                prefixSnapshot.StateKey,
                prefixSnapshot.HasRisk,
                prefixSnapshot.BoundaryReason,
                false,
                null,
                prefixSnapshot,
                CombatProgressState.Capture(prefixSnapshot));
            followUps.AddRange(Expand(seed).Where(node =>
                node.Action is
                {
                    Kind: PlanActionKind.PlayCard,
                    Turn: var turn,
                    TargetCombatId: not null,
                }
                && turn == prefixSnapshot.Turn
                && node.Snapshot.EnemyHp < prefixSnapshot.EnemyHp));
            return followUps
                .GroupBy(node => node.Action!.TargetCombatId!.Value)
                .Select(group => group
                    .OrderBy(node => node.Snapshot.AliveEnemyCount)
                    .ThenBy(node => node.Snapshot.EnemyHp)
                    .ThenByDescending(node => node.Snapshot.FocusTargetPressure)
                    .ThenByDescending(node => node.Score)
                    .First().Action!)
                .OrderBy(action => action.TargetCombatId)
                .Take(3)
                .ToArray();
        }
        finally
        {
            foreach (SearchNode followUp in followUps)
                followUp.Snapshot.ReleaseSimulator();
            prefixSnapshot.ReleaseSimulator();
        }
    }

    internal PlanAction? BuildOpeningDefensiveFollowUp(IReadOnlyList<PlanAction> prefix)
    {
        SimulationSnapshot prefixSnapshot = Replay(prefix);
        List<SearchNode> followUps = [];
        try
        {
            SearchNode seed = new(
                null,
                0,
                prefixSnapshot.PotionUseCount,
                prefixSnapshot.PotionStrategicCost,
                prefixSnapshot.Turn,
                SearchRouteTraits.Scaling,
                0,
                prefixSnapshot.Score,
                prefixSnapshot.StateKey,
                prefixSnapshot.HasRisk,
                prefixSnapshot.BoundaryReason,
                false,
                null,
                prefixSnapshot,
                CombatProgressState.Capture(prefixSnapshot));
            followUps.AddRange(Expand(seed).Where(node =>
                node.Action is { Kind: PlanActionKind.PlayCard, Turn: var turn }
                && turn == prefixSnapshot.Turn));
            SearchNode? best = followUps
                .Where(node => node.Snapshot.PlayerBlock > prefixSnapshot.PlayerBlock)
                .OrderByDescending(node => node.Snapshot.PlayerBlock)
                .ThenByDescending(node => node.Score)
                .FirstOrDefault();
            return best?.Action;
        }
        finally
        {
            foreach (SearchNode followUp in followUps)
                followUp.Snapshot.ReleaseSimulator();
            prefixSnapshot.ReleaseSimulator();
        }
    }

    internal PlanAction? BuildOpeningSetupFollowUp(IReadOnlyList<PlanAction> prefix)
    {
        SimulationSnapshot prefixSnapshot = Replay(prefix);
        List<SearchNode> followUps = [];
        try
        {
            SearchNode seed = new(
                null,
                0,
                prefixSnapshot.PotionUseCount,
                prefixSnapshot.PotionStrategicCost,
                prefixSnapshot.Turn,
                SearchRouteTraits.Scaling,
                0,
                prefixSnapshot.Score,
                prefixSnapshot.StateKey,
                prefixSnapshot.HasRisk,
                prefixSnapshot.BoundaryReason,
                false,
                null,
                prefixSnapshot,
                CombatProgressState.Capture(prefixSnapshot));
            followUps.AddRange(Expand(seed).Where(node =>
                node.Action is { Kind: PlanActionKind.PlayCard, Turn: var turn }
                && turn == prefixSnapshot.Turn));
            SearchNode? best = followUps
                .Where(node => node.Snapshot.PersistentBuffValue > prefixSnapshot.PersistentBuffValue
                    || node.Snapshot.DelayedDamageValue > prefixSnapshot.DelayedDamageValue
                    || node.Snapshot.ReplayPotentialValue > prefixSnapshot.ReplayPotentialValue
                    || node.Snapshot.ReactiveDamageValue > prefixSnapshot.ReactiveDamageValue
                    || node.Snapshot.StrategicEffects.RetentionValue
                        > prefixSnapshot.StrategicEffects.RetentionValue
                    || node.Snapshot.LongTermResourceValue > prefixSnapshot.LongTermResourceValue)
                .OrderByDescending(node => node.Score)
                .FirstOrDefault();
            return best?.Action;
        }
        finally
        {
            foreach (SearchNode followUp in followUps)
                followUp.Snapshot.ReleaseSimulator();
            prefixSnapshot.ReleaseSimulator();
        }
    }

    internal PlanAction? BuildOpeningFullRedrawPotionAction(PlanAction selectedPotionAction)
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        SimulationSnapshot? probeSnapshot = null;
        try
        {
            CombatPredictionSimulator simulator = (CombatPredictionSimulator)rootSnapshot.Simulator;
            SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
            PotionModel? potion = combat.GetPotionAtSlot(_player, selectedPotionAction.PotionSlot);
            if (potion is not GamblersBrew
                || !string.Equals(
                    potion.Id.Entry,
                    selectedPotionAction.PotionId,
                    StringComparison.Ordinal))
            {
                return null;
            }

            SearchNode seed = new(
                null,
                0,
                rootSnapshot.PotionUseCount,
                rootSnapshot.PotionStrategicCost,
                _startTurnNumber,
                SearchRouteTraits.None,
                0,
                rootSnapshot.Score,
                rootSnapshot.StateKey,
                rootSnapshot.HasRisk,
                rootSnapshot.BoundaryReason,
                false,
                null,
                rootSnapshot,
                CombatProgressState.Capture(rootSnapshot));
            PlanAction baseAction = selectedPotionAction with
            {
                Turn = _startTurnNumber,
                Choice = null,
                NestedChoices = null,
                NestedChoicesBeforePrimary = 0,
                TurnStartChoices = null,
                RelicEffects = null,
                EndsPlayerTurn = false,
            };
            probeSnapshot = ReplayAction(seed, baseAction);
            CardChoiceSpec spec = PotionChoiceSupport.GetSpec(
                (CombatPredictionSimulator)probeSnapshot.Simulator,
                potion);
            PlanCardChoice? fullRedraw = CardChoiceSupport.BuildChoices(
                    spec,
                    displayNames,
                    _profile.MaxPileChoiceBranchesPerAction,
                    _profile.MaxHandChoiceBranchesPerAction)
                .OrderByDescending(choice => choice.Cards.Count)
                .FirstOrDefault();
            return fullRedraw == null
                ? null
                : baseAction with
                {
                    Choice = fullRedraw with { SourceId = potion.Id.Entry },
                };
        }
        finally
        {
            probeSnapshot?.ReleaseSimulator();
            rootSnapshot.ReleaseSimulator();
        }
    }

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
        using ExpansionBatch? cycleExitBatch = node.CycleProbeLease == null
            && node.CycleExitProbe == null
            ? null
            : new ExpansionBatch();
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
                    CardStateOccurrence: cardStateOccurrence);
                SimulationSnapshot probeSnapshot = ReplayAction(node, action);

                CardChoiceSpec? choiceSpec = BuildPrimaryCardChoiceSpec(probeSnapshot);
                if (choiceSpec == null && CardChoiceSupport.RequiresUnsupportedExistingChoice(card.Preview))
                {
                    probeSnapshot.ReleaseSimulator();
                    continue;
                }
                PlanCardChoice? requiredEmptyChoice = CardChoiceSupport.BuildRequiredEmptyChoice(card.Preview);
                CardChoiceSpec? primaryChoiceSpec = choiceSpec
                    ?? BuildRequiredEmptyChoiceSpec(requiredEmptyChoice);
                int actionChoiceBranchLimit = ResolveWholeActionChoiceBranchLimit(
                    action,
                    primaryChoiceSpec);
                IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> resolvedBranches =
                    HasChoiceBeforePrimary(probeSnapshot, primaryChoiceSpec)
                        ? ResolveRoundChoiceBranches(
                            node,
                            action,
                            probeSnapshot,
                            BuildPrimaryChoiceMatch(primaryChoiceSpec),
                            actionChoiceBranchLimit)
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
                    CommitCycleExitObservation(child);
                    if (ShouldPruneCrossTurnNoProgress(child))
                    {
                        _run.RepeatableNoProgressBranchesPruned++;
                        finalSnapshot.ReleaseSimulator();
                        continue;
                    }
                    bool deferTransposition = ShouldDeferCycleTranspositionUntilActionAdmission(
                        child);
                    if (deferTransposition)
                    {
                        deferredCycleCandidates ??= [];
                        deferredCycleCandidates.Add(BuildCandidate(
                            snapshot,
                            finalSnapshot,
                            child,
                            card.Preview.Type,
                            target?.CombatId));
                    }
                    else if (TryAcceptTransposition(child))
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

        if (cycleExitBatch != null)
        {
            PruneCommittedCrossTurnCandidates(cycleExitBatch.Potions, cycleExitBatch);
            PruneCommittedCrossTurnCandidates(cycleExitBatch.EndTurns, cycleExitBatch);
        }
        CommitDeferredCycleCandidates(
            nonDominated,
            deferredCycleCandidates,
            batch: null);
        if (node.CycleProbeLease != null)
        {
            SearchNode[] directChildren = nonDominated.Select(candidate => candidate.Node)
                .Concat(cycleExitBatch?.Potions ?? [])
                .Concat(cycleExitBatch?.EndTurns ?? [])
                .ToArray();
            foreach (SearchNode directChild in directChildren)
                CommitCycleExitObservation(directChild);
            AnnotateCycleExitProgress(node, directChildren);
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
                CardChoiceSpec? choiceSpec = null;
                if (PotionChoiceSupport.RequiresChoice(potion))
                {
                    CombatPredictionSimulator choiceSimulator = simulator;
                    if (PotionChoiceSupport.GeneratesCardChoice(potion))
                    {
                        probeSnapshot = ReplayAction(node, baseAction);
                        choiceSimulator = (CombatPredictionSimulator)probeSnapshot.Simulator;
                    }
                    choiceSpec = PotionChoiceSupport.GetSpec(choiceSimulator, potion);
                    choices = CardChoiceSupport.BuildChoices(
                            choiceSpec,
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
                int maxFinalBranches = ResolveWholeActionChoiceBranchLimit(
                    baseAction,
                    choiceSpec);
                ChoiceReplayBudget replayBudget = CreateChoiceReplayBudget(maxFinalBranches);
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
                        if (!TrySpendChoiceReplayBudget(replayBudget))
                            break;
                        SimulationSnapshot? replayedChoice = ReplayPlannedChoiceBranch(node, action);
                        if (replayedChoice == null)
                            continue;
                        childSnapshot = replayedChoice;
                    }

                    foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                             ResolveRoundChoiceBranches(
                                 node,
                                 action,
                                 childSnapshot,
                                 maxFinalBranches: maxFinalBranches,
                                 replayBudget: replayBudget))
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
        if (node.CycleExitProbe is { RemainingActions: > 0, RemainingTurnTransitions: >= 0 })
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
        List<CrossTurnStandPatBaseline>? directStandPatBaselines =
            ReferenceEquals(FindTurnStart(node), node)
            ? []
            : null;
        foreach ((PlanAction endAction, SimulationSnapshot endSnapshot) in BuildEndTurnBranches(node, []))
        {
            bool combatEnded = endSnapshot.PlayerDead || endSnapshot.AllEnemiesDead;
            SearchNode endNode = new(
                endAction,
                node.ActionCount + 1,
                endSnapshot.PotionUseCount,
                endSnapshot.PotionStrategicCost,
                node.Turn + 1,
                ClassifyRoundTransitionTraits(node.Traits, node.Snapshot, endSnapshot),
                node.FutureSoldHp,
                ApplySoldHpPenalty(endSnapshot.Score, node.FutureSoldHp),
                endSnapshot.StateKey,
                endSnapshot.HasRisk,
                endSnapshot.BoundaryReason,
                combatEnded || endSnapshot.BoundaryReason != SearchBoundaryReason.None,
                node,
                endSnapshot,
                node.CombatProgress.Advance(endSnapshot))
            {
                CumulativeEnemyHpLost = AccumulateEnemyHpLost(node, endSnapshot),
            };
            endNode = AttachCycleSchedulingEvidence(endNode);
            if (directStandPatBaselines != null
                && IsComparableCrossTurnOutcome(endSnapshot.BoundaryReason))
            {
                directStandPatBaselines.Add(new CrossTurnStandPatBaseline(
                    endNode.StateKey,
                    MeasureCycleExitQuality(node, endNode)));
            }
            CommitCycleExitObservation(endNode);
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
        if (directStandPatBaselines != null)
            PublishCrossTurnStandPatBaselines(node, directStandPatBaselines);
    }

    private readonly record struct PrimaryChoiceMatch(
        string ContextId,
        PlanChoiceEffect Effect,
        PileType SourcePile,
        int MinCount);

    private readonly record struct PendingChoiceReplayBranch(
        PlanAction Action,
        bool PruneInvalidBranch);

    private sealed record PrimaryCardChoiceLayer(
        IReadOnlyList<PlanCardChoice?> Choices,
        bool UnregisteredPendingChoice,
        int WholeActionChoiceBranchQuota,
        int DownstreamChoiceBranchQuota);

    private sealed record PendingChoiceReplayLayer(
        IReadOnlyList<PendingChoiceReplayBranch> Branches);

    /// <summary>
    /// Bounds actual replay work for one parent/action independently from the number of final
    /// leaves retained by the beam. Invalid prefixes and prefixes that open another choice layer
    /// consume the same budget as successful leaves; otherwise a nested choice tree can spend
    /// unbounded time and allocation while the expanded-node counter remains unchanged.
    /// </summary>
    private sealed class ChoiceReplayBudget(int attemptLimit)
    {
        private int _remainingAttempts = attemptLimit > 0
            ? attemptLimit
            : throw new ArgumentOutOfRangeException(nameof(attemptLimit));
        private bool _exhaustionRecorded;

        public int AttemptLimit { get; } = attemptLimit;

        public bool TrySpend()
        {
            if (_remainingAttempts <= 0)
                return false;
            _remainingAttempts--;
            return true;
        }

        public bool TryRecordExhaustion()
        {
            if (_exhaustionRecorded)
                return false;
            _exhaustionRecorded = true;
            return true;
        }
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolvePrimaryCardChoiceBranches(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot probeSnapshot,
        CardChoiceSpec? choiceSpec,
        PlanCardChoice? requiredEmptyChoice)
    {
        PrimaryCardChoiceLayer layer = BuildPrimaryCardChoiceLayer(
            action,
            probeSnapshot,
            choiceSpec,
            requiredEmptyChoice);
        ChoiceReplayBudget replayBudget = CreateChoiceReplayBudget(
            layer.WholeActionChoiceBranchQuota);
        foreach ((PlanAction resolvedAction, SimulationSnapshot resolvedSnapshot) in
                 ResolvePrimaryCardChoiceLayer(
                     node,
                     action,
                     probeSnapshot,
                     layer,
                     replayBudget))
        {
            yield return (resolvedAction, resolvedSnapshot);
        }
    }

    private PrimaryCardChoiceLayer BuildPrimaryCardChoiceLayer(
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
        int wholeActionBranchLimit = ResolveWholeActionChoiceBranchLimit(action, choiceSpec);
        if (action.ReplayCount > 0
            && wholeActionBranchLimit != int.MaxValue
            && choices.Count > 1)
        {
            int choiceEvents = checked(action.ReplayCount + 1);
            int initialChoiceLimit = Math.Max(
                1,
                (int)Math.Ceiling(Math.Pow(wholeActionBranchLimit, 1d / choiceEvents)));
            choices = choices.Take(initialChoiceLimit).ToList();
        }
        if (choices.Count == 0)
            choices = [null];
        else if (choiceSpec != null)
            _run.ChoiceBranchesEvaluated += choices.Count;

        SimulatedCombatState probeCombat =
            (SimulatedCombatState)probeSnapshot.Simulator.State.CombatState;
        bool unregisteredPendingChoice = probeSnapshot.BoundaryReason == SearchBoundaryReason.PendingChoice
            && probeCombat.PendingTurnStartChoice == null
            && probeCombat.PendingKnowledgeDemonChoice == null;
        int downstreamChoiceBranchQuota = wholeActionBranchLimit == int.MaxValue
            ? int.MaxValue
            : Math.Max(1, wholeActionBranchLimit / Math.Max(1, choices.Count));

        return new PrimaryCardChoiceLayer(
            choices,
            unregisteredPendingChoice,
            wholeActionBranchLimit,
            downstreamChoiceBranchQuota);
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolvePrimaryCardChoiceLayer(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot probeSnapshot,
        PrimaryCardChoiceLayer layer,
        ChoiceReplayBudget? replayBudget = null)
    {
        replayBudget ??= CreateChoiceReplayBudget(layer.WholeActionChoiceBranchQuota);
        bool retainsProbeSnapshot = layer.Choices.Contains(null);
        if (!retainsProbeSnapshot)
            probeSnapshot.ReleaseSimulator();
        if (layer.UnregisteredPendingChoice)
        {
            if (retainsProbeSnapshot)
                probeSnapshot.ReleaseSimulator();
            throw new InvalidOperationException(
                $"卡牌 {action.CardId} 产生了未登记的分支选择，不能静默回退到原生重扫。");
        }
        foreach (PlanCardChoice? choice in layer.Choices)
        {
            PlanAction resolvedAction = action with { Choice = choice };
            SimulationSnapshot childSnapshot;
            if (choice == null)
            {
                childSnapshot = probeSnapshot;
            }
            else
            {
                if (!TrySpendChoiceReplayBudget(replayBudget))
                    yield break;
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
                         maxFinalBranches: layer.DownstreamChoiceBranchQuota,
                         replayBudget: replayBudget))
            {
                yield return (finalAction, finalSnapshot);
            }
        }
    }

    private int ResolveWholeActionChoiceBranchLimit(
        PlanAction action,
        CardChoiceSpec? primaryChoiceSpec)
    {
        _ = action;
        return primaryChoiceSpec?.SourcePile == PileType.Hand
            ? _profile.MaxHandChoiceBranchesPerAction
            : primaryChoiceSpec == null
                ? Math.Max(
                    _profile.MaxPileChoiceBranchesPerAction,
                    _profile.MaxHandChoiceBranchesPerAction)
                : _profile.MaxPileChoiceBranchesPerAction;
    }

    private ChoiceReplayBudget CreateChoiceReplayBudget(int maxFinalBranches)
        => new(ResolveChoiceReplayAttemptLimit(maxFinalBranches));

    private int ResolveChoiceReplayAttemptLimit(int maxFinalBranches)
    {
        int finiteFinalBranches = maxFinalBranches == int.MaxValue
            ? Math.Max(
                _profile.MaxPileChoiceBranchesPerAction,
                _profile.MaxHandChoiceBranchesPerAction)
            : Math.Max(1, maxFinalBranches);
        // Four replay attempts per retained leaf covers ordinary primary + nested chains while
        // keeping a hard ceiling for recursive generators. Custom profiles remain bounded too.
        return (int)Math.Min(512L, Math.Max(1L, finiteFinalBranches * 4L));
    }

    private bool TrySpendChoiceReplayBudget(ChoiceReplayBudget replayBudget)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _run.WorkPacer.YieldIfNeeded();
        if (replayBudget.TrySpend())
        {
            _run.ChoiceReplayAttempts++;
            return true;
        }
        if (replayBudget.TryRecordExhaustion())
            _run.ChoiceReplayBudgetExhaustions++;
        return false;
    }

    private CardChoiceSpec? BuildPrimaryCardChoiceSpec(SimulationSnapshot probeSnapshot)
    {
        CombatPredictionSimulator probeSimulator =
            (CombatPredictionSimulator)probeSnapshot.Simulator;
        SimulatedCombatState probeCombat =
            (SimulatedCombatState)probeSnapshot.Simulator.State.CombatState;
        return probeCombat.PendingTurnStartChoice is { } pendingChoice
            && string.IsNullOrEmpty(pendingChoice.SourceId)
                ? TurnStartChoiceSupport.BuildPendingSpec(
                    probeSimulator,
                    probeCombat,
                    _player)
                : null;
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
        => MatchesPrimaryChoice(request, BuildPrimaryChoiceMatch(primaryChoiceSpec));

    private static PrimaryChoiceMatch? BuildPrimaryChoiceMatch(
        CardChoiceSpec? primaryChoiceSpec)
        => primaryChoiceSpec == null
            ? null
            : new PrimaryChoiceMatch(
                primaryChoiceSpec.ContextId,
                primaryChoiceSpec.Effect,
                primaryChoiceSpec.SourcePile,
                primaryChoiceSpec.MinCount);

    private static bool MatchesPrimaryChoice(
        TurnStartChoiceRequest request,
        PrimaryChoiceMatch? primaryChoice)
        => primaryChoice is { } match
            && string.IsNullOrEmpty(request.SourceId)
            && string.Equals(request.ContextId, match.ContextId, StringComparison.Ordinal)
            && request.Effect == match.Effect
            && request.SourcePile == match.SourcePile
            && request.Count == match.MinCount;

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> ResolveRoundChoiceBranches(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot snapshot,
        PrimaryChoiceMatch? unresolvedPrimaryChoice = null,
        int maxFinalBranches = int.MaxValue,
        ChoiceReplayBudget? replayBudget = null)
    {
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
        {
            yield return (action, snapshot);
            yield break;
        }

        if (maxFinalBranches == int.MaxValue)
        {
            maxFinalBranches = Math.Max(
                _profile.MaxPileChoiceBranchesPerAction,
                _profile.MaxHandChoiceBranchesPerAction);
        }
        replayBudget ??= CreateChoiceReplayBudget(maxFinalBranches);

        PendingChoiceReplayLayer layer;
        try
        {
            layer = BuildPendingChoiceReplayLayer(
                node,
                action,
                snapshot,
                unresolvedPrimaryChoice,
                maxFinalBranches);
        }
        catch
        {
            snapshot.ReleaseSimulator();
            throw;
        }
        snapshot.ReleaseSimulator();
        foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                 ResolvePendingChoiceReplayLayer(
                     node,
                     layer,
                     unresolvedPrimaryChoice,
                     maxFinalBranches,
                     replayBudget))
        {
            yield return (finalAction, finalSnapshot);
        }
    }

    private PendingChoiceReplayLayer BuildPendingChoiceReplayLayer(
        SearchNode node,
        PlanAction action,
        SimulationSnapshot snapshot,
        PrimaryChoiceMatch? unresolvedPrimaryChoice,
        int maxFinalBranches)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (combat.PendingKnowledgeDemonChoice is { } knowledgeRequest)
        {
            IReadOnlyList<PlanCardChoice> branches = KnowledgeDemonChoiceSupport.BuildChoices(
                knowledgeRequest,
                displayNames);
            _run.ChoiceBranchesEvaluated += Math.Min(branches.Count, maxFinalBranches);
            List<PendingChoiceReplayBranch> resolvedBranches = new(branches.Count);
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
                resolvedBranches.Add(new PendingChoiceReplayBranch(
                    resolvedAction,
                    PruneInvalidBranch: true));
            }
            return new PendingChoiceReplayLayer(resolvedBranches);
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
            bool turnResolution = action.Kind == PlanActionKind.EndTurn
                || snapshot.Turn > node.Turn;
            bool primaryChoice = !turnResolution
                && action.Choice == null
                && string.IsNullOrEmpty(request.SourceId)
                && (unresolvedPrimaryChoice == null
                    || MatchesPrimaryChoice(request, unresolvedPrimaryChoice));
            IReadOnlyList<PlanCardChoice> existing = turnResolution
                ? action.TurnStartChoices ?? []
                : action.NestedChoices ?? [];
            List<PendingChoiceReplayBranch> resolvedBranches = new(branches.Count);
            foreach (PlanCardChoice branch in branches)
            {
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
                resolvedBranches.Add(new PendingChoiceReplayBranch(
                    resolvedAction,
                    PruneInvalidBranch: true));
            }
            return new PendingChoiceReplayLayer(resolvedBranches);
        }
        throw new InvalidOperationException(
            $"动作 {PolicyActionToken(action)} 产生了未登记的分支选择，不能留下等待原生结算的搜索边界。");
    }

    private IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>
        ResolvePendingChoiceReplayLayer(
            SearchNode node,
            PendingChoiceReplayLayer layer,
            PrimaryChoiceMatch? unresolvedPrimaryChoice,
            int maxFinalBranches,
            ChoiceReplayBudget replayBudget)
    {
        int yielded = 0;
        foreach (PendingChoiceReplayBranch branch in layer.Branches)
        {
            if (!TrySpendChoiceReplayBudget(replayBudget))
                yield break;
            SimulationSnapshot? resolvedSnapshot = ReplayPendingChoiceBranch(node, branch);
            if (resolvedSnapshot == null)
                continue;
            foreach ((PlanAction finalAction, SimulationSnapshot finalSnapshot) in
                     ResolveRoundChoiceBranches(
                         node,
                         branch.Action,
                         resolvedSnapshot,
                         unresolvedPrimaryChoice,
                         maxFinalBranches - yielded,
                         replayBudget))
            {
                yield return (finalAction, finalSnapshot);
                if (++yielded >= maxFinalBranches)
                    yield break;
            }
        }
    }

    private SimulationSnapshot? ReplayPendingChoiceBranch(
        SearchNode node,
        PendingChoiceReplayBranch branch,
        ReplayForkSeed? replayForkSeed = null)
        => branch.PruneInvalidBranch
            ? ReplayPlannedChoiceBranch(node, branch.Action, replayForkSeed)
            : ReplayAction(node, branch.Action, replayForkSeed);

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
        ActionRelicTriggerRecorder? triggerRecorder = null,
        ReplayForkSeed? replayForkSeed = null)
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
            if (replayForkSeed != null)
                throw new InvalidOperationException("根回放不能消费父节点 Fork seed。");
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
            _run.TransitionCount += actions.Count;
            if (replayForkSeed == null)
            {
                _run.ForkCount++;
                SearchMeasurement forkMeasurement = _run.Performance.Begin();
                try
                {
                    simulator = ((CombatPredictionSimulator)parentSnapshot.Simulator).Fork();
                }
                finally
                {
                    _run.Performance.End(SearchMetricPhase.Fork, forkMeasurement);
                }
                processedEnemyDeaths =
                    ((ForkableSet<uint>)parentSnapshot.ProcessedEnemyDeaths).Fork();
            }
            else
            {
                (simulator, processedEnemyDeaths) = replayForkSeed.Take();
            }
            simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
            turn = startingTurn;
            shufflesCrossed = parentSnapshot.ShufflesCrossed;
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

    private ReplayForkSeed PrepareReplayForkSeed(
        SimulationSnapshot parentSnapshot,
        object? forkGate = null)
    {
        if (parentSnapshot.BoundaryReason != SearchBoundaryReason.None)
            throw new InvalidOperationException("不能从已抵达搜索边界的模拟状态准备 Fork seed。");
        cancellationToken.ThrowIfCancellationRequested();
        if (forkGate != null)
        {
            lock (forkGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return PrepareReplayForkSeedCore(parentSnapshot);
            }
        }
        return PrepareReplayForkSeedCore(parentSnapshot);
    }

    private ReplayForkSeed PrepareReplayForkSeedCore(SimulationSnapshot parentSnapshot)
    {
        _run.ForkCount++;
        SearchMeasurement forkMeasurement = _run.Performance.Begin();
        try
        {
            CombatPredictionSimulator simulator =
                ((CombatPredictionSimulator)parentSnapshot.Simulator).Fork();
            ForkableSet<uint> processedEnemyDeaths =
                ((ForkableSet<uint>)parentSnapshot.ProcessedEnemyDeaths).Fork();
            return new ReplayForkSeed(simulator, processedEnemyDeaths);
        }
        finally
        {
            _run.Performance.End(SearchMetricPhase.Fork, forkMeasurement);
        }
    }

    private SimulationSnapshot ReplayAction(
        SearchNode parent,
        PlanAction action,
        ReplayForkSeed? replayForkSeed = null)
    {
        if (replayForkSeed != null && policy.VerifyIncrementalSearch)
            throw new InvalidOperationException("严格增量回放不能消费并行 Fork seed。");
        ReplayForkSeed? gatedSeed = null;
        try
        {
            if (replayForkSeed == null && _parallelActionReplayForkGate != null)
            {
                gatedSeed = PrepareReplayForkSeed(
                    parent.Snapshot,
                    _parallelActionReplayForkGate);
                replayForkSeed = gatedSeed;
            }
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
                    parent.ActionCount,
                    replayForkSeed: replayForkSeed);
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
        finally
        {
            gatedSeed?.Dispose();
        }
    }

    private SimulationSnapshot? ReplayPlannedChoiceBranch(
        SearchNode parent,
        PlanAction action,
        ReplayForkSeed? replayForkSeed = null)
    {
        try
        {
            return ReplayAction(parent, action, replayForkSeed);
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
            Creature[] actingEnemies = simulatedCombat.Enemies.ToArray();
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
            damage,
            block,
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
            if ((!allowOverflow && selected.Count >= limit)
                || selected.Any(current => ReferenceEquals(current.Node, candidate.Node)))
            {
                return;
            }
            selected.Add(candidate);
        }

        // A resolved routing choice and a revival window are semantic branch boundaries. Preserve
        // the previous overflow behavior for them; the ordinary family portfolio remains inside the
        // configured per-node card branch budget.
        foreach (ActionCandidate candidate in candidates)
        {
            if (CurrentTurnRoutingChoice(candidate.Node) != null)
                Add(candidate, allowOverflow: true);
        }
        ActionCandidate? revivalWindowCandidate = candidates
            .Where(candidate => candidate.Node.Snapshot.RevivingEnemyCount
                > parent.Snapshot.RevivingEnemyCount)
            .OrderByDescending(candidate => candidate.Node.Snapshot.RevivingEnemyCount)
            .ThenBy(candidate => candidate.Node.Snapshot.RawEnemyHp)
            .ThenBy(candidate => candidate.Node.Snapshot.MaxCurrentEnemyHp)
            .ThenByDescending(candidate => candidate.Node.Snapshot.ProjectedPlayerHp)
            .Select(candidate => (ActionCandidate?)candidate)
            .FirstOrDefault();
        if (revivalWindowCandidate is { } revivalCandidate)
            Add(revivalCandidate, allowOverflow: true);

        foreach (ActionOptionFamily family in new[]
                 {
                     ActionOptionFamily.ImmediateDefense,
                     ActionOptionFamily.ImmediateOffense,
                     ActionOptionFamily.ResourceAndCycle,
                     ActionOptionFamily.PersistentSetup,
                     ActionOptionFamily.Control,
                     ActionOptionFamily.TargetRemoval,
                     ActionOptionFamily.HpInvestment,
                 })
        {
            ActionCandidate? representative = candidates
                .Where(candidate => candidate.OptionFamilies.HasFlag(family))
                .Select(candidate => (ActionCandidate?)candidate)
                .FirstOrDefault();
            if (representative is { } candidate)
                Add(candidate);
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
        bool leftHasCycleEvidence = left.Node.CycleProbeLease != null || left.Node.Cycle != null;
        bool rightHasCycleEvidence = right.Node.CycleProbeLease != null || right.Node.Cycle != null;
        bool leftHasCycleExitProbe = left.Node.CycleExitProbe != null;
        bool rightHasCycleExitProbe = right.Node.CycleExitProbe != null;
        if (ReferenceEquals(left.Node, right.Node)
            || !left.IsPure
            || !right.IsPure
            || leftHasCycleEvidence != rightHasCycleEvidence
            || leftHasCycleEvidence
                && BuildCycleProbeFamilyKey(left.Node) != BuildCycleProbeFamilyKey(right.Node)
            || leftHasCycleExitProbe != rightHasCycleExitProbe
            || leftHasCycleExitProbe
                && BuildCycleExitProbeFamilyKey(left.Node)
                    != BuildCycleExitProbeFamilyKey(right.Node)
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
            || HasValidCycleExitProbe(candidate, requireIssuedTicket: false);

    private static bool HasCycleExpansionTranspositionLease(SearchNode candidate)
        => HasValidCycleProbeLease(candidate)
            || HasValidCycleExitProbe(candidate, requireIssuedTicket: true);

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
            Snapshot: null!,
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
            Snapshot: null!,
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

        CycleProbeTracker tracker = new(shapeKey, sequenceKey, [actionKey]);
        candidate.CycleProbeLease = new CycleProbeLease(
            tracker,
            NextActionIndex: 0,
            CompletedRepetitions: 0,
            ImprovedSinceWrap: false,
            LastCompletedRepetitionImproved: false);
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

        long generation = tracker.ObserveExit(0, actionKey, default);
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

    private bool TryAcceptTransposition(SearchNode candidate)
    {
        // Scheduling obligations are deliberately bounded elsewhere. A normal route at the
        // same simulator state cannot inherit their exact pattern/envelope history, so it must
        // not erase the probe before the obligation reaches the frontier.
        if (HasCycleAdmissionTranspositionLease(candidate))
            return true;
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
        if (HasCycleExpansionTranspositionLease(node))
            return true;
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
