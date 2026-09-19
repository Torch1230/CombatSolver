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
    private IReadOnlyList<(IReadOnlyList<PlanCardChoice> Choices, SimulationSnapshot Snapshot)>
        BuildTurnSetupRoots()
    {
        List<(IReadOnlyList<PlanCardChoice>, SimulationSnapshot)> roots = [];
        SimulationSnapshot? initialSnapshot = null;
        bool completed = false;
        try
        {
            initialSnapshot = ReplayTurnSetup([]);
            PendingChoiceBudgetSeed? initialSeed =
                BuildCurrentPendingChoiceBudgetSeed(initialSnapshot);
            if (initialSnapshot.BoundaryReason == SearchBoundaryReason.PendingChoice
                && initialSeed?.TurnSetupLayer == null)
            {
                throw new InvalidOperationException(
                    "回合准备阶段产生了未登记的选择类型。");
            }
            WholeActionChoiceBudget wholeActionBudget = CreateWholeActionChoiceBudget(
                primaryChoiceSpec: null,
                minimumSemanticFinalQuota: 1,
                currentPendingChoice: initialSeed);
            ChoiceOccurrenceCollector<IReadOnlyList<PlanCardChoice>> occurrenceCollector = new(
                wholeActionBudget.OccurrenceFinalReserve,
                wholeActionBudget.OccurrenceReplayAttemptQuota);
            SimulationSnapshot ownedInitialSnapshot = initialSnapshot;
            initialSnapshot = null;
            ResolveTurnSetupChoices(
                [],
                ownedInitialSnapshot,
                roots,
                wholeActionBudget.SemanticSearchBudget,
                occurrenceCollector,
                initialSeed?.TurnSetupLayer);

            IReadOnlyList<IReadOnlyList<PlanCardChoice>> occurrencePrefixes =
                occurrenceCollector.Seal();
            ChoiceSearchBudget occurrenceBudget = new(
                semanticFinalQuota: 0,
                materializedOccurrenceFinalQuota: occurrenceCollector.FinalReserve,
                replayAttemptQuota: occurrenceCollector.ReplayAttemptQuota);
            for (int index = 0; index < occurrencePrefixes.Count; index++)
            {
                ChoiceSearchBudget? branchBudget = CreateChoiceBranchBudgetCore(
                    occurrenceBudget,
                    occurrencePrefixes.Count - index - 1);
                if (branchBudget == null)
                {
                    RecordChoiceBranchesDroppedByBudget(
                        occurrencePrefixes.Count - index);
                    break;
                }
                if (!TrySpendChoiceReplayAttempt(branchBudget))
                    break;

                IReadOnlyList<PlanCardChoice> prefix = occurrencePrefixes[index];
                SimulationSnapshot resolved;
                try
                {
                    resolved = ReplayTurnSetup(prefix);
                }
                catch (InvalidPlannedChoiceBranchException ex)
                {
                    if (_detailedDiagnostics)
                    {
                        policy.Diagnostics.Debug(
                            $"[CombatSolver/Test] INITIAL_OCCURRENCE_CHOICE_REPLAY_PRUNED " +
                            $"reason={ex.Message}");
                    }
                    continue;
                }
                ResolveTurnSetupChoices(
                    prefix,
                    resolved,
                    roots,
                    branchBudget,
                    occurrenceCollector);
            }
            completed = true;
            return roots;
        }
        finally
        {
            initialSnapshot?.ReleaseSimulator();
            if (!completed)
            {
                foreach ((_, SimulationSnapshot snapshot) in roots)
                    snapshot.ReleaseSimulator();
            }
        }
    }

    private void ResolveTurnSetupChoices(
        IReadOnlyList<PlanCardChoice> choices,
        SimulationSnapshot snapshot,
        List<(IReadOnlyList<PlanCardChoice>, SimulationSnapshot)> roots,
        ChoiceSearchBudget searchBudget,
        ChoiceOccurrenceCollector<IReadOnlyList<PlanCardChoice>> occurrenceCollector,
        TurnSetupChoiceLayer? preparedLayer = null)
    {
        if (!GrowthCostPolicy.AllowsBrightestFlame(policy.BrightestFlameMaxHpLossLimit,
                root.InitialBrightestFlameMaxHpSpent, snapshot.BrightestFlameMaxHpSpent))
        {
            snapshot.ReleaseSimulator();
            return;
        }
        if (snapshot.BoundaryReason == SearchBoundaryReason.None)
        {
            if (searchBudget.TryConsumeFinal())
                roots.Add((choices, snapshot));
            else
            {
                RecordChoiceBranchesDroppedByBudget(1);
                snapshot.ReleaseSimulator();
            }
            return;
        }
        if (snapshot.BoundaryReason != SearchBoundaryReason.PendingChoice)
        {
            snapshot.ReleaseSimulator();
            return;
        }
        if (!searchBudget.HasWork)
        {
            _run.ChoiceReplayBudgetExhaustions++;
            snapshot.ReleaseSimulator();
            return;
        }

        using ExecutionChoiceReplayCheckpoint? checkpoint = TakeExecutionChoiceCheckpoint(null, null, snapshot, choices);
        TurnStartChoiceRequest request;
        IReadOnlyList<IReadOnlyList<PlanCardChoice>> prefixes;
        int semanticBranchCount;
        try
        {
            TurnSetupChoiceLayer layer = preparedLayer
                ?? BuildCurrentPendingChoiceBudgetSeed(snapshot)?.TurnSetupLayer
                ?? throw new InvalidOperationException(
                    "回合准备阶段产生了未登记的选择类型。");
            request = layer.Request;
            CardChoiceSpec spec = layer.Spec;
            IReadOnlyList<PlanCardChoice> branches = layer.Branches;
            bool identityChangingLayer =
                CardChoiceSupport.IsIdentityChangingPersistentChoiceEffect(spec.Effect);
            branches = CardChoiceSupport.TakeChoicesWithIdentityOccurrenceReserve(
                branches,
                spec.Effect,
                Math.Max(1, searchBudget.ActiveFinalQuota),
                identityChangingLayer
                    ? occurrenceCollector.RemainingFinalReserve
                    : 0);
            semanticBranchCount = identityChangingLayer
                ? CardChoiceSupport.CountSemanticChoices(branches)
                : branches.Count;

            List<IReadOnlyList<PlanCardChoice>> resolvedPrefixes =
                new(branches.Count);
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
                resolvedPrefixes.Add(next);
            }
            prefixes = resolvedPrefixes;

            if (identityChangingLayer)
            {
                for (int index = semanticBranchCount; index < prefixes.Count; index++)
                    occurrenceCollector.Collect(prefixes[index]);
            }
        }
        catch
        {
            snapshot.ReleaseSimulator();
            throw;
        }
        snapshot.ReleaseSimulator();

        for (int index = 0; index < semanticBranchCount; index++)
        {
            ChoiceSearchBudget? branchBudget = CreateChoiceBranchBudgetCore(
                searchBudget,
                semanticBranchCount - index - 1);
            if (branchBudget == null)
            {
                RecordChoiceBranchesDroppedByBudget(semanticBranchCount - index);
                break;
            }
            if (!TrySpendChoiceReplayAttempt(branchBudget))
                break;

            IReadOnlyList<PlanCardChoice> prefix = prefixes[index];
            SimulationSnapshot resolved;
            try
            {
                resolved = checkpoint?.MatchesPrefix(prefix) == true
                    ? ResumeExecutionChoice(checkpoint, null, null, prefix) : ReplayTurnSetup(prefix);
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
            ResolveTurnSetupChoices(
                prefix,
                resolved,
                roots,
                branchBudget,
                occurrenceCollector);
        }
    }

    private SimulationSnapshot ReplayTurnSetup(IReadOnlyList<PlanCardChoice> choices, bool allowExecutionCapture = true)
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

        bool capturingExecution = allowExecutionCapture && !_disableExecutionChoiceContinuationsForTesting
            && simulator.BeginExecutionContinuationCapture();
        TurnStartChoiceCursor cursor = new(choices);
        simulatedCombat.BeginActionChoices(cursor);
        simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
        SearchBoundaryReason boundary;
        try
        {
            using var executionDispatch = simulator.BeginExecutionDispatch();
            boundary = PreparePlayerPlayPhase(
                simulator,
                simulatedCombat,
                cursor,
                processedEnemyDeaths);
        }
        finally
        {
            try
            {
                simulatedCombat.EndActionChoices();
                if (capturingExecution && simulator.HasCapturedExecutionContinuation)
                    simulator.AppendExecutionContinuation(new ExecutionReplayTailFrame(processedEnemyDeaths,
                        _startTurnNumber, 0, simulator.ShuffleEventCount, simulator.ShuffleEventCount,
                        simulatedCombat.LastActionChoicesConsumed, simulator.CapturedExecutionFrame<PlayerStartFrame>()?.Progress,
                        RootSetup: true));
            }
            finally { if (capturingExecution) simulator.EndExecutionContinuationCapture(); }
        }
        if (boundary == SearchBoundaryReason.None
            && !SettleReplayActionBoundary(simulator, simulatedCombat))
        {
            boundary = SearchBoundaryReason.PendingChoice;
        }
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
        // The setup root is already inside this turn. Preserve events that occurred before energy reset.
        if (PersistentRelicSupport.ShouldPlayerResetEnergy(simulatedCombat, _player))
            playerState.LoseEnergy(playerState.Energy);
        playerState.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(simulatedCombat, _player));
        if (simulatedCombat.HasPendingChoice
            || !PersistentPowerSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, _player))
        {
            return SearchBoundaryReason.PendingChoice;
        }
        TurnStartRelicSupport.TriggerAfterEnergyReset(simulator, simulatedCombat, _player);
        if (simulatedCombat.HasPendingChoice)
            return SearchBoundaryReason.PendingChoice;
        TurnStartRelicSupport.TriggerAfterEnergyResetLate(simulator, simulatedCombat, _player);
        if (simulatedCombat.HasPendingChoice)
            return SearchBoundaryReason.PendingChoice;
        var progress = new PlayerStartProgress(_player, _startTurnNumber, rootSetup: true,
            takingExtraTurn: false, processedEnemyDeaths, 0, simulator.ShuffleEventCount);
        return ContinuePlayerStart(simulator, simulatedCombat, progress, PlayerStartStage.BeforeHand);
    }

    private SimulationSnapshot Replay(
        IReadOnlyList<PlanAction> actions,
        SimulationSnapshot? parentSnapshot = null,
        int startingTurn = 0,
        int priorActionCount = 0,
        ActionRelicTriggerRecorder? triggerRecorder = null,
        ReplayForkSeed? replayForkSeed = null,
        SearchReplayEvidence? replayEvidence = null,
        RoundReplayCheckpoint? roundCheckpoint = null,
        RoundReplayCheckpointCapture? roundCheckpointCapture = null,
        CardChoiceReplayCapture? cardChoiceCapture = null,
        ManualCardChoiceFrame? cardChoiceFrame = null,
        PotionChoiceFrame? potionChoiceFrame = null,
        bool countTransition = true,
        bool allowExecutionCapture = true)
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
            if (countTransition) _run.TransitionCount += actions.Count;
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
            shufflesCrossed = roundCheckpoint?.ShufflesCrossed ?? parentSnapshot.ShufflesCrossed;
        }
        if (triggerRecorder != null)
            simulator.ActionRelicTriggers = triggerRecorder;

        bool capturingExecution = allowExecutionCapture && parentSnapshot != null && actions.Count == 1
            && cardChoiceFrame is null && potionChoiceFrame is null && triggerRecorder is null
            && ShouldCaptureExecution(simulator, actions[0]) && simulator.BeginExecutionContinuationCapture();
        int actionShuffleEventsBefore = simulator.ShuffleEventCount;
        try
        {
        SearchMeasurement actionMeasurement = _run.Performance.Begin();
        try
        {
        for (int actionOffset = 0; actionOffset < actions.Count; actionOffset++)
        {
            if (!simulator.IsInProgress)
                throw new InvalidOperationException("回放包含已锁定战斗终局之后的动作。");
            PlanAction action = actions[actionOffset];
            triggerRecorder?.BeginAction(priorActionCount + actionOffset);
            cancellationToken.ThrowIfCancellationRequested();
            if (action.Kind == PlanActionKind.EndTurn)
            {
                SearchMeasurement roundMeasurement = _run.Performance.Begin();
                try
                {
                    using var executionDispatch = simulator.BeginExecutionDispatch();
                    boundary = roundCheckpoint != null
                        ? ResumeRoundPlayerStart(simulator, simulatedCombat, turn - _startTurnNumber,
                            processedEnemyDeaths, ref shufflesCrossed, action.TurnStartChoices, roundCheckpoint)
                        : AdvanceRound(simulator, simulatedCombat, turn - _startTurnNumber,
                            processedEnemyDeaths, ref shufflesCrossed, action.TurnStartChoices,
                            roundCheckpointCapture);
                }
                finally
                {
                    _run.Performance.End(SearchMetricPhase.RoundAdvance, roundMeasurement);
                }
                _ = simulatedCombat.ConsumePlayerTurnEndRequest();
                if (boundary == SearchBoundaryReason.None
                    && !SettleReplayActionBoundary(simulator, simulatedCombat))
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                }
                turn = simulatedCombat.GetPlayerTurnNumber(_player);
                LogAnnotatedReplayState(simulator, action, priorActionCount + actionOffset, turn, replayEvidence);
                continue;
            }

            if (action.Kind == PlanActionKind.UsePotion)
            {
                PotionModel potion = potionChoiceFrame?.Potion ?? simulatedCombat.GetPotionAtSlot(_player, action.PotionSlot)
                    ?? throw new InvalidOperationException($"回放时药水槽位 {action.PotionSlot} 为空。");
                if (!string.Equals(potion.Id.Entry, action.PotionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"回放时药水槽位 {action.PotionSlot} 为 {potion.Id.Entry}，预期 {action.PotionId}。");
                }
                if (potionChoiceFrame == null && !simulatedCombat.IsPotionAvailable(_player, action.PotionSlot))
                    throw new InvalidOperationException($"回放时药水 {action.PotionId} 已被消耗。");
                Creature? potionTarget = simulatedCombat.GetCreature(action.TargetCombatId);
                int potionShuffleEvents = potionChoiceFrame?.ShuffleEventsBefore ?? simulator.ShuffleEventCount;
                int potionHistoryEntryStart = potionChoiceFrame?.HistoryStart ?? simulator.History.Entries.Count;
                SearchMeasurement potionMeasurement = _run.Performance.Begin();
                simulatedCombat.BeginActionChoices(action.NestedChoices);
                try
                {
                    if (potionChoiceFrame == null && !PotionExecutionSupport.Prepare(
                            simulator, simulatedCombat, potion, action.PotionSlot, potionTarget))
                    {
                        boundary = SearchBoundaryReason.PendingChoice;
                        break;
                    }
                    if (!PotionExecutionSupport.Complete(simulator, simulatedCombat, potion, potionTarget,
                            action.Choice, potionHistoryEntryStart, processedEnemyDeaths))
                    {
                        boundary = SearchBoundaryReason.PendingChoice;
                        break;
                    }
                    if (!SettleReplayActionBoundary(simulator, simulatedCombat))
                    {
                        boundary = SearchBoundaryReason.PendingChoice;
                        break;
                    }
                }
                finally
                {
                    _run.Performance.End(SearchMetricPhase.PotionExecution, potionMeasurement);
                    simulatedCombat.EndActionChoices();
                }
                if (simulator.ShuffleEventCount != potionShuffleEvents)
                {
                    shufflesCrossed++;
                }
                simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player));
                boundary = ResolveRequestedPlayerTurnEnd(
                    simulator, simulatedCombat, action, processedEnemyDeaths, ref turn, ref shufflesCrossed);
                LogAnnotatedReplayState(simulator, action, priorActionCount + actionOffset, turn, replayEvidence);
                continue;
            }

            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
            PredictedCard? card = cardChoiceFrame?.Card ?? FindCardForReplay(playerState.Hand.Cards, action);
            if (card is null)
            {
                string hand = string.Join(',', playerState.Hand.Cards.Select(candidate =>
                    $"{candidate.Preview.Id.Entry}#{candidate.Preview.CurrentUpgradeLevel}:{CardChoiceSupport.ChoiceCardKey(candidate)}"));
                throw new InvalidOperationException(
                    $"回放时找不到手牌 {action.CardId}#{action.CardOccurrence}：turn={turn} " +
                    $"action_index={priorActionCount + actionOffset} " +
                    $"state_occurrence={action.CardStateOccurrence} state_key={action.CardStateKey} hand={hand}。");
            }
            Creature? target = simulatedCombat.GetCreature(action.TargetCombatId);
            if (cardChoiceFrame is null && !simulatedCombat.CanPlayCard(simulator, card))
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
            int shuffleEvents = cardChoiceFrame?.ShuffleEventsBefore ?? simulator.ShuffleEventCount;
            bool capturingChoice = cardChoiceCapture != null && simulator.BeginManualCardChoiceCapture(card);
            SearchMeasurement cardExecutionMeasurement = _run.Performance.Begin();
            simulatedCombat.BeginActionChoices(ActionChoicesForReplay(action));
            using IDisposable cardExecutionScope =
                simulatedCombat.BeginCardExecutionScope(processedEnemyDeaths);
            bool cardPlayCompleted;
            try
            {
                cardPlayCompleted = cardChoiceFrame is null
                    ? simulator.ManualPlay(card, target, out _)
                    : simulator.ResumeManualCardChoice(cardChoiceFrame);
            }
            finally
            {
                _run.Performance.End(SearchMetricPhase.CardExecution, cardExecutionMeasurement);
                if (capturingChoice) simulator.EndManualCardChoiceCapture();
            }
            SearchMeasurement cardPostMeasurement = _run.Performance.Begin();
            try
            {
                if (!cardPlayCompleted)
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                    break;
                }
                bool deathsCompleted;
                using (simulator.BeginExecutionDispatch())
                    deathsCompleted = CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator, simulatedCombat, simulatedCombat.KnownEnemies, processedEnemyDeaths);
                if (!deathsCompleted)
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                    break;
                }
                if (!SettleReplayActionBoundary(simulator, simulatedCombat))
                {
                    boundary = SearchBoundaryReason.PendingChoice;
                    break;
                }
            }
            finally
            {
                _run.Performance.End(SearchMetricPhase.CardPostProcessing, cardPostMeasurement);
                simulatedCombat.EndActionChoices();
            }
            if (simulator.ShuffleEventCount != shuffleEvents)
            {
                shufflesCrossed++;
            }
            // The native action executor checks after the complete card effect, before
            // servicing a requested turn end. A lethal forced-end card never starts a round.
            simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player));
            boundary = ResolveRequestedPlayerTurnEnd(
                simulator, simulatedCombat, action, processedEnemyDeaths, ref turn, ref shufflesCrossed);
            LogAnnotatedReplayState(simulator, action, priorActionCount + actionOffset, turn, replayEvidence);
        }
        }
        finally { _run.Performance.End(SearchMetricPhase.Action, actionMeasurement); }
        if (capturingExecution && simulator.HasCapturedExecutionContinuation)
            simulator.AppendExecutionContinuation(new ExecutionReplayTailFrame(processedEnemyDeaths, turn,
                priorActionCount + actions.Count, shufflesCrossed, actionShuffleEventsBefore,
                simulatedCombat.LastActionChoicesConsumed,
                simulator.CapturedExecutionFrame<PlayerStartFrame>()?.Progress));
        }
        finally { if (capturingExecution) simulator.EndExecutionContinuationCapture(); }
        if ((cardChoiceFrame != null || potionChoiceFrame != null) && boundary == SearchBoundaryReason.PendingChoice)
        {
            // This is the same logical choice attempt. The extra physical fork is observable,
            // but cannot spend a second transition or branch-budget lease. All child scopes have
            // unwound before replaying from the retained parent with the complete original action.
            if (cardChoiceFrame != null) _run.CardChoicePrefixFallbacks++;
            else _run.PotionChoicePrefixFallbacks++;
            using ReplayForkSeed? fallbackSeed = _parallelActionReplayForkGate is null ? null
                : PrepareReplayForkSeed(parentSnapshot!, _parallelActionReplayForkGate);
            return Replay(actions, parentSnapshot, startingTurn, priorActionCount,
                triggerRecorder, replayForkSeed: fallbackSeed, replayEvidence: replayEvidence, countTransition: false);
        }

        SearchMeasurement snapshotMeasurement = _run.Performance.Begin();
        SimulationSnapshot snapshot = Snapshot(
            simulator,
            turn,
            priorActionCount + actions.Count,
            shufflesCrossed,
            boundary,
            processedEnemyDeaths);
        _run.Performance.End(SearchMetricPhase.Snapshot, snapshotMeasurement);
        try { cardChoiceCapture?.Receive(this, simulator, processedEnemyDeaths); }
        catch { snapshot.ReleaseSimulator(); throw; }
        return snapshot;
    }

    private SearchBoundaryReason ResolveRequestedPlayerTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PlanAction action,
        ForkableSet<uint> processedEnemyDeaths,
        ref int turn,
        ref int shufflesCrossed)
    {
        bool requested = combat.ConsumePlayerTurnEndRequest();
        if (!requested || !simulator.IsInProgress)
            return SearchBoundaryReason.None;
        SearchBoundaryReason boundary = AdvanceRound(
            simulator, combat, turn - _startTurnNumber, processedEnemyDeaths,
            ref shufflesCrossed, action.TurnStartChoices);
        if (boundary == SearchBoundaryReason.None && !SettleReplayActionBoundary(simulator, combat))
            boundary = SearchBoundaryReason.PendingChoice;
        _ = combat.ConsumePlayerTurnEndRequest();
        turn = combat.GetPlayerTurnNumber(_player);
        return boundary;
    }

    internal static bool SettleReplayActionBoundary(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
    {
        using var executionDispatch = simulator.BeginExecutionDispatch();
        simulator.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, combat);
        if (simulator.HasPendingChoice)
            return false;
        combat.NormalizeAeonglassWithers(simulator);
        if (simulator.HasPendingChoice)
            return false;
        combat.NormalizeCardAfflictions(simulator);
        return !simulator.HasPendingChoice;
    }

    private void LogAnnotatedReplayState(
        CombatPredictionSimulator simulator,
        PlanAction action,
        int actionIndex,
        int turn,
        SearchReplayEvidence? replayEvidence = null)
    {
        if (replayEvidence != null && replayEvidence.Observe(simulator, _player, action, actionIndex))
            replayEvidence.FirstActualState = ContinuationStamp.CapturePredicted(
                _player, simulator, turn, _forecast, _startTurnNumber).StateText;
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
        ReplayForkSeed? replayForkSeed = null,
        RoundReplayCheckpointCapture? roundCheckpointCapture = null,
        CardChoiceReplayCapture? cardChoiceCapture = null)
    {
        if (replayForkSeed != null && policy.VerifyIncrementalSearch)
            throw new InvalidOperationException("严格增量回放不能消费并行 Fork seed。");
        ExecutionChoiceReplayCheckpoint? executionCheckpoint = _executionChoiceReplayCheckpoint?.Matches(parent, action) == true
            ? _executionChoiceReplayCheckpoint : null;
        ReplayForkSeed? gatedSeed = null;
        ManualCardChoiceFrame? cardChoiceFrame = null;
        PotionChoiceFrame? potionChoiceFrame = null;
        PotionChoiceReplayCheckpoint? potionCheckpoint = _potionChoiceReplayCheckpoint?.Matches(parent, action) == true
            ? _potionChoiceReplayCheckpoint : null;
        CardChoiceReplayCheckpoint? cardCheckpoint = _cardChoiceReplayCheckpoint?.Matches(parent, action) == true
            ? _cardChoiceReplayCheckpoint : null;
        RoundReplayCheckpoint? roundCheckpoint = !policy.VerifyIncrementalSearch
            && _roundReplayCheckpoint?.Matches(parent, action) == true ? _roundReplayCheckpoint : null;
        try
        {
            if (executionCheckpoint != null)
            {
                if (replayForkSeed != null || cardChoiceCapture != null)
                    throw new InvalidOperationException("Execution continuation cannot consume another replay seed or capture.");
                roundCheckpoint = null;
            }
            else if (cardCheckpoint != null)
            {
                if (replayForkSeed != null || roundCheckpoint != null || cardChoiceCapture != null)
                    throw new InvalidOperationException("Card continuation cannot consume another replay seed or capture.");
                gatedSeed = cardCheckpoint.Fork(this, out cardChoiceFrame);
                replayForkSeed = gatedSeed;
            }
            else if (potionCheckpoint != null)
            {
                if (replayForkSeed != null || roundCheckpoint != null || cardChoiceCapture != null)
                    throw new InvalidOperationException("Potion continuation cannot consume another replay seed or capture.");
                gatedSeed = potionCheckpoint.Fork(this, out potionChoiceFrame);
                replayForkSeed = gatedSeed;
            }
            else if (roundCheckpoint != null)
            {
                if (replayForkSeed != null || _parallelActionReplayForkGate == null)
                    throw new InvalidOperationException("Round checkpoint requires its owning replay gate.");
                gatedSeed = roundCheckpoint.Fork(this, _parallelActionReplayForkGate, cancellationToken);
                replayForkSeed = gatedSeed;
            }
            else if (replayForkSeed == null && _parallelActionReplayForkGate != null)
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
                SimulationSnapshot incremental;
                try
                {
                    incremental = executionCheckpoint != null
                    ? ResumeExecutionChoice(executionCheckpoint, parent, action)
                    : Replay(
                    [action],
                    parent.Snapshot,
                    parent.Turn,
                    parent.ActionCount,
                    replayForkSeed: replayForkSeed,
                    roundCheckpoint: roundCheckpoint,
                    roundCheckpointCapture: roundCheckpointCapture,
                    cardChoiceCapture: cardChoiceCapture,
                    cardChoiceFrame: cardChoiceFrame,
                    potionChoiceFrame: potionChoiceFrame);
                }
                catch (InvalidPlannedChoiceBranchException error) when (_verifyChoiceContinuationStepsForTesting
                    && (executionCheckpoint != null || cardCheckpoint != null || potionCheckpoint != null))
                {
                    VerifyRejectedChoiceContinuationStepForTesting(parent, action, error);
                    throw;
                }
                if (_verifyChoiceContinuationStepsForTesting
                    && (executionCheckpoint != null || cardCheckpoint != null || potionCheckpoint != null))
                    VerifyChoiceContinuationStepForTesting(parent, action, incremental);
                if (!policy.VerifyIncrementalSearch)
                    return incremental;

                List<PlanAction> fullActions = new(parent.ActionCount + 1);
                fullActions.AddRange(parent.Actions);
                fullActions.Add(action);
                SimulationSnapshot? fullReplayRoot = _includeTurnSetup
                    ? ReplayTurnSetup(parent.GetTurnSetupChoices(), allowExecutionCapture: false)
                    : null;
                SimulationSnapshot replayed;
                try
                {
                    replayed = Replay(
                        fullActions,
                        fullReplayRoot,
                        _startTurnNumber,
                        priorActionCount: 0, allowExecutionCapture: false);
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
        catch (SearchTransitionException error)
        {
            SearchReplayEvidence.PublishCandidateFailure(policy.Diagnostics, parent,
                "action_replay:" + error.Message, action);
            throw;
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
            && incremental.TerminalStamp == replayed.TerminalStamp
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
            $"incremental_terminal={incremental.TerminalStamp} replayed_terminal={replayed.TerminalStamp} " +
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
        IReadOnlyList<PlanCardChoice>? turnStartChoices,
        RoundReplayCheckpointCapture? roundCheckpointCapture = null)
    {
        if (!simulator.IsInProgress)
            return SearchBoundaryReason.None;
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
        if (!simulatedCombat.TryPrepareExtraPlayerTurn(
                simulator,
                _player,
                out bool takingExtraTurn,
                out bool hasActiveEmotionChip))
        {
            return SearchBoundaryReason.PendingChoice;
        }
        int etherealExhaustCount = simulatedCombat.CountEtherealCardsInHand(simulator, _player);
        {
            using SearchMeasurementScope _ = _run.Performance.Measure(SearchMetricPhase.RoundPlayerEnd);
            bool playerTurnEndCompleted;
            using (_run.Performance.Measure(SearchMetricPhase.RoundEndSimulation))
                playerTurnEndCompleted = PlayerTurnEndLifecycle.RunPhaseOne(
                    simulator,
                    simulatedCombat,
                    _player,
                    [_player.Creature]);
            if (!playerTurnEndCompleted)
                return SearchBoundaryReason.PendingChoice;
            simulatedCombat.CommitHistoryCourseTurn(_player);
            simulatedCombat.NormalizeAeonglassWithers(simulator);
            simulatedCombat.NormalizeCardAfflictions(simulator);
            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    simulatedCombat,
                    simulatedCombat.KnownEnemies,
                    processedEnemyDeaths))
            {
                return SearchBoundaryReason.PendingChoice;
            }
            // Finish the already-started phase-one compensation above, but do not
            // flush the hand or enter phase two after either native phase-one check ended combat.
            if (!simulator.IsInProgress)
                return SearchBoundaryReason.None;
            using (_run.Performance.Measure(SearchMetricPhase.RoundFlush))
                CorePowerSupport.FlushPlayerHandAtTurnEnd(simulator, simulatedCombat, _player);
            int turnEndShuffleEvents = simulator.ShuffleEventCount;
            bool playerTurnEndCompletedPhaseTwo;
            using (_run.Performance.Measure(SearchMetricPhase.RoundPlayerEndPowers))
            {
                playerTurnEndCompletedPhaseTwo = PlayerTurnEndLifecycle.RunPhaseTwo(
                    simulator,
                    simulatedCombat,
                    [_player.Creature],
                    etherealExhaustCount);
            }
            shufflesCrossed += simulator.ShuffleEventCount - turnEndShuffleEvents;
            if (!playerTurnEndCompletedPhaseTwo)
                return SearchBoundaryReason.PendingChoice;
            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                    simulator,
                    simulatedCombat,
                    simulatedCombat.KnownEnemies,
                    processedEnemyDeaths))
            {
                return SearchBoundaryReason.PendingChoice;
            }
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
                foreach (Creature enemy in simulatedCombat.Enemies)
                    simulatedCombat.BeginSideTurn(enemy);
                simulatedCombat.SnapshotPowerAmountsAtTurnStart(simulatedCombat.Enemies);
                // 怪物方开始回合时，上一怪物回合留下的格挡先清除。
                if (!TurnStartRelicSupport.TriggerBeforeSideTurnStart(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.Enemies))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
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
                    if (!CorePowerSupport.TriggerAfterBlockCleared(
                            simulator,
                            simulatedCombat,
                            enemy))
                    {
                        return SearchBoundaryReason.PendingChoice;
                    }
                }
                bool decrementEnemyPlating = simulatedCombat.RoundNumber > 1;
                if (!simulatedCombat.TriggerSideTurnStart(
                        simulator,
                        CombatSide.Enemy,
                        simulatedCombat.Enemies,
                        decrementEnemyPlating))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                int enemyPoisonHistoryStart = simulator.History.Entries.Count;
                if (!CorePowerSupport.TriggerPoison(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.Enemies.ToArray()))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                TriggeredPowerSupport.CompensateHistorySince(
                    simulator,
                    simulatedCombat,
                    enemyPoisonHistoryStart);
                if (simulatedCombat.HasPendingChoice)
                    return SearchBoundaryReason.PendingChoice;
                if (!CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.KnownEnemies,
                        processedEnemyDeaths))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
            }
            // Vanilla checks after the entire enemy-side start, not between listeners.
            if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player)))
                return SearchBoundaryReason.None;
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
                        if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player)))
                            return SearchBoundaryReason.None;
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
                            if (simulatedCombat.HasPendingChoice)
                                return SearchBoundaryReason.PendingChoice;
                            using (simulator.PushDamageSource(
                                CombatDamageSource.For(
                                    CombatDamageSourceKind.MonsterMove,
                                    move.Owner.Monster?.Id.Entry)))
                            {
                                simulator.Kill(move.Owner, force: true);
                            }
                            if (simulatedCombat.HasPendingChoice)
                                return SearchBoundaryReason.PendingChoice;
                            if (!CorePowerSupport.ApplyEnemyDeathPowers(
                                    simulator,
                                    simulatedCombat,
                                    simulatedCombat.KnownEnemies,
                                    processedEnemyDeaths))
                            {
                                return SearchBoundaryReason.PendingChoice;
                            }
                        }
                        if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player)))
                            return SearchBoundaryReason.None;
                        continue;
                    }
                    bool playerDied = MonsterMoveSemantics.ApplyForecastMove(
                            simulator,
                            simulatedCombat,
                            move,
                            _player.Creature,
                            processedEnemyDeaths,
                            turnStartChoices);
                    performedMoves[actingEnemy] = move.Move;
                    if (move.Owner.CombatId is uint revivedCombatId
                        && simulator.State.GetCreature(move.Owner).IsAlive)
                    {
                        processedEnemyDeaths.Remove(revivedCombatId);
                    }
                    if (simulatedCombat.HasPendingChoice)
                        return SearchBoundaryReason.PendingChoice;
                    // ApplyForecastMove has completed its attack finally and command tails.
                    if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player))
                        || playerDied)
                        return SearchBoundaryReason.None;
                }
            }

            using (_run.Performance.Measure(SearchMetricPhase.RoundEnemyEndPowers))
            {
                if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.Enemies.ToArray()))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                if (simulatedCombat.BattlewornDummyTimedOut)
                    return SearchBoundaryReason.EventDefeat;
                if (!CorePowerSupport.ApplyEnemyDeathPowers(
                        simulator,
                        simulatedCombat,
                        simulatedCombat.KnownEnemies,
                        processedEnemyDeaths))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                int playerPoisonHistoryStart = simulator.History.Entries.Count;
                if (!CorePowerSupport.TriggerPoison(
                        simulator,
                        simulatedCombat,
                        [_player.Creature]))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
                TriggeredPowerSupport.CompensateHistorySince(
                    simulator,
                    simulatedCombat,
                    playerPoisonHistoryStart);
                if (simulatedCombat.HasPendingChoice)
                    return SearchBoundaryReason.PendingChoice;
                simulatedCombat.ClearNoDraw(_player.Creature);
                simulatedCombat.RecordRelicRoundDamage(simulator, _player, roundHistoryEntryStart);
            }
            if (simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player)))
                return SearchBoundaryReason.None;
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

        return AdvanceRoundPlayerStart(simulator, simulatedCombat, playerState, simulatedPlayer,
            roundIndex, processedEnemyDeaths, ref shufflesCrossed, roundChoices, takingExtraTurn,
            turnStartChoices is not { Count: > 0 } ? roundCheckpointCapture : null);
        }
        finally
        {
            simulatedCombat.EndActionChoices();
        }
    }

}
