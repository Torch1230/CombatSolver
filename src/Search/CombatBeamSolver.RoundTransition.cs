using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private SearchBoundaryReason AdvanceRoundPlayerStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        SimPlayerCombatState playerState,
        SimCreatureState simulatedPlayer,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        ref int shufflesCrossed,
        TurnStartChoiceCursor roundChoices,
        bool takingExtraTurn,
        RoundReplayCheckpointCapture? capture = null)
    {
        simulatedCombat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
        using SearchMeasurementScope _ = _run.Performance.Measure(SearchMetricPhase.RoundPlayerStart);
        simulatedCombat.CurrentSide = CombatSide.Player;
        if (!takingExtraTurn)
            simulatedCombat.RoundNumber++;
        simulatedCombat.AdvancePlayerTurn(_player);
        simulatedCombat.BeginSideTurn(_player.Creature);
        simulatedCombat.SnapshotPowerAmountsAtTurnStart([_player.Creature]);

        if (!TurnStartRelicSupport.TriggerBeforeSideTurnStart(
                simulator,
                simulatedCombat,
                [_player.Creature]))
        {
            return SearchBoundaryReason.PendingChoice;
        }
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
        if (!CorePowerSupport.TriggerAfterBlockCleared(
                simulator,
                simulatedCombat,
                _player.Creature))
        {
            return SearchBoundaryReason.PendingChoice;
        }

        if (PersistentRelicSupport.ShouldPlayerResetEnergy(simulatedCombat, _player))
            playerState.LoseEnergy(playerState.Energy);
        playerState.GainEnergy(PersistentPowerSupport.GetModifiedMaxEnergy(simulatedCombat, _player)
            + simulatedCombat.ConsumeEnergyNextTurn(_player));
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
        int beforeHandDrawShuffleEvents = simulator.ShuffleEventCount;
        bool sideTurnStartTriggeredEarly = false;
        using (roundChoices.BeforeNextTake(() =>
               {
                   sideTurnStartTriggeredEarly = true;
                   return simulatedCombat.TriggerSideTurnStart(
                       simulator,
                       CombatSide.Player,
                       [_player.Creature],
                       decrementPlating: simulatedCombat.GetPlayerTurnNumber(_player) != 1,
                       takingExtraTurn);
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
            capture?.CaptureBeforeHandDraw(this, simulator, simulatedCombat, roundChoices,
                processedEnemyDeaths, shufflesCrossed, takingExtraTurn,
                sideTurnStartTriggeredEarly, drawCount, willShuffle);
            if (!DrawPreparedPlayerHand(simulator, simulatedCombat, drawCount,
                    willShuffle, ref shufflesCrossed))
                return SearchBoundaryReason.PendingChoice;
        }
        capture?.Capture(this, simulator, simulatedCombat, roundChoices, processedEnemyDeaths,
            shufflesCrossed, takingExtraTurn, sideTurnStartTriggeredEarly);
        return CompleteRoundPlayerStart(simulator, simulatedCombat, roundIndex,
            processedEnemyDeaths, roundChoices, takingExtraTurn, sideTurnStartTriggeredEarly);
    }

    private bool DrawPreparedPlayerHand(
        CombatPredictionSimulator simulator, SimulatedCombatState combat,
        int drawCount, bool willShuffle, ref int shufflesCrossed)
    {
        int historyEntryStart = simulator.History.Entries.Count;
        using (_run.Performance.Measure(SearchMetricPhase.RoundDraw))
            simulator.Draw(_player, drawCount, fromHandDraw: true);
        if (willShuffle)
            shufflesCrossed++;
        if (combat.HasPendingChoice)
            return false;
        TriggeredPowerSupport.CompensateHistorySince(simulator, combat, historyEntryStart);
        return !combat.HasPendingChoice;
    }

    private SearchBoundaryReason CompleteRoundPlayerStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState simulatedCombat,
        int roundIndex,
        ISet<uint> processedEnemyDeaths,
        TurnStartChoiceCursor roundChoices,
        bool takingExtraTurn,
        bool sideTurnStartTriggeredEarly)
    {
        using (sideTurnStartTriggeredEarly ? null : roundChoices.BeforeNextTake(() =>
               {
                   sideTurnStartTriggeredEarly = true;
                   return simulatedCombat.TriggerSideTurnStart(
                       simulator, CombatSide.Player, [_player.Creature],
                       decrementPlating: simulatedCombat.GetPlayerTurnNumber(_player) != 1,
                       takingExtraTurn);
               }))
        {
            if (simulatedCombat.TriggerAfterPlayerTurnStart(
                    simulator,
                    _player.Creature,
                    roundChoices))
                return SearchBoundaryReason.PendingChoice;
            if (!sideTurnStartTriggeredEarly)
            {
                if (!simulatedCombat.TriggerSideTurnStart(
                        simulator,
                        CombatSide.Player,
                        [_player.Creature],
                        decrementPlating: simulatedCombat.GetPlayerTurnNumber(_player) != 1,
                        takingExtraTurn))
                {
                    return SearchBoundaryReason.PendingChoice;
                }
            }
        }
        if (!CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                simulatedCombat,
                simulatedCombat.KnownEnemies,
                processedEnemyDeaths))
        {
            return SearchBoundaryReason.PendingChoice;
        }
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
        simulator.CheckWinCondition(simulatedCombat.GetPlayerTurnNumber(_player));
        return SearchBoundaryReason.None;
    }
    private RoundReplayCheckpoint? _roundReplayCheckpoint;

    internal int VerifyRoundReplayCheckpointForTesting(
        bool learnFromProbe = false, bool handDrawShuffle = false)
    {
        SimulationSnapshot rootSnapshot = Replay([]);
        SearchNode parent = new(null, 0, rootSnapshot.PotionUseCount,
            rootSnapshot.PotionStrategicCost, rootSnapshot.Turn, SearchRouteTraits.None,
            0, rootSnapshot.Score, rootSnapshot.StateKey, rootSnapshot.HasRisk,
            rootSnapshot.BoundaryReason, false, null, rootSnapshot,
            CombatProgressState.Capture(rootSnapshot));
        ContinuationStamp parentBefore = CaptureDiagnosticContinuation(rootSnapshot);
        using RoundReplayCheckpointCapture capture = new(parent);
        SimulationSnapshot? probe = null;
        try
        {
            PlanAction endTurn = new(PlanActionKind.EndTurn, parent.Turn);
            if (learnFromProbe || handDrawShuffle)
            {
                using RoundReplayCheckpointCapture discovery = new(parent);
                SimulationSnapshot first = ReplayAction(parent, endTurn, roundCheckpointCapture: discovery);
                try
                {
                    bool reachedExpectedPoint = handDrawShuffle
                        ? discovery.ReachedHandDrawShuffle && !discovery.ReachedStablePrefix
                        : discovery.ReachedStablePrefix;
                    if (discovery.HasCheckpoint || !reachedExpectedPoint
                        || first.BoundaryReason != SearchBoundaryReason.PendingChoice)
                        throw new InvalidOperationException("Adaptive prefix fixture did not learn from the expected uncached choice.");
                    discovery.ObservePendingChoice(this,
                        ((SimulatedCombatState)first.Simulator.State.CombatState).PendingTurnStartChoice?.SourceId);
                }
                finally { first.ReleaseSimulator(); }
            }
            if (handDrawShuffle)
            {
                var withoutSource = (CombatPredictionSimulator)rootSnapshot.Simulator.Fork();
                var withoutSourceCombat = (SimulatedCombatState)withoutSource.State.CombatState;
                withoutSourceCombat.SetAmount<StratagemPower>(_player.Creature, 0);
                using RoundReplayCheckpointCapture inactive = new(parent);
                inactive.CaptureBeforeHandDraw(this, withoutSource, withoutSourceCombat,
                    new TurnStartChoiceCursor(null), new ForkableSet<uint>(), 0,
                    takingExtraTurn: false, sideTurnStartTriggeredEarly: false,
                    drawCount: 5, willShuffle: true);
                if (inactive.HasCheckpoint)
                    throw new InvalidOperationException("Inactive shuffle-choice source displaced the post-draw prefix.");
            }
            probe = ReplayAction(parent, endTurn, roundCheckpointCapture: capture);
            using RoundReplayCheckpoint checkpoint = capture.Take()
                ?? throw new InvalidOperationException("Round prefix fixture did not capture a checkpoint.");
            if (checkpoint.HandDrawCount.HasValue != handDrawShuffle)
                throw new InvalidOperationException("Round prefix fixture captured the wrong continuation point.");
            SimulatedCombatState combat = (SimulatedCombatState)probe.Simulator.State.CombatState;
            var request = combat.PendingTurnStartChoice
                ?? throw new InvalidOperationException("Round prefix fixture did not reach a turn-start choice.");
            CardChoiceSpec spec = TurnStartChoiceSupport.BuildPendingSpec(
                (CombatPredictionSimulator)probe.Simulator, combat, _player);
            var choices = CardChoiceSupport.BuildChoices(spec, displayNames,
                _profile.MaxPileChoiceBranchesPerAction, _profile.MaxHandChoiceBranchesPerAction);
            int checkedBranches = 0;
            // Revisit the first sibling after all other writes to expose shared mutable state.
            foreach (var choice in choices.Concat(choices.Take(1)))
            {
                PlanAction action = endTurn with { TurnStartChoices = [choice with
                {
                    SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing,
                }] };
                SimulationSnapshot full = ReplayAction(parent, action);
                SimulationSnapshot? resumed = null;
                try
                {
                    _parallelActionReplayForkGate = new object();
                    _roundReplayCheckpoint = checkpoint;
                    resumed = ReplayAction(parent, action);
                    AssertIncrementalEquivalent(action, [action], resumed, full);
                    if (resumed.ShufflesCrossed != full.ShufflesCrossed
                        || resumed.Simulator.History.Entries.Count != full.Simulator.History.Entries.Count)
                        throw new InvalidOperationException("Round prefix history/shuffle accounting differs.");
                    checkedBranches++;
                }
                finally
                {
                    _parallelActionReplayForkGate = null;
                    _roundReplayCheckpoint = null;
                    resumed?.ReleaseSimulator();
                    full.ReleaseSimulator();
                }
            }
            if (checkedBranches < 3 || _run.RoundReplayPrefixReuses != checkedBranches
                || CaptureDiagnosticContinuation(rootSnapshot) != parentBefore)
                throw new InvalidOperationException("Round prefix sibling isolation/reuse was not proved.");
            return checkedBranches;
        }
        finally
        {
            probe?.ReleaseSimulator();
            rootSnapshot.ReleaseSimulator();
        }
    }

    // A checkpoint belongs to one admitted parent's EndTurn frontier. It is released only
    // after all replay producers and the ordered continuation have drained.
    private sealed class RoundReplayCheckpoint(
        SearchNode parent,
        CombatPredictionSimulator simulator,
        ForkableSet<uint> deaths,
        int shufflesCrossed,
        bool takingExtraTurn,
        bool sideTurnStartTriggeredEarly,
        int? handDrawCount = null) : IDisposable
    {
        private CombatPredictionSimulator? _simulator = simulator;
        private ForkableSet<uint>? _deaths = deaths;
        public int ShufflesCrossed { get; } = shufflesCrossed;
        public int? HandDrawCount { get; } = handDrawCount;
        public bool TakingExtraTurn { get; } = takingExtraTurn;
        public bool SideTurnStartTriggeredEarly { get; } = sideTurnStartTriggeredEarly;
        public bool Matches(SearchNode candidate, PlanAction action)
            => ReferenceEquals(parent, candidate)
                && action.Kind == PlanActionKind.EndTurn
                && action.Turn == parent.Turn
                && action.TurnStartChoices is { Count: > 0 } choices
                && choices.All(choice => choice.Timing == PlanChoiceTiming.PlayerTurnStart
                    && choice.Effect != PlanChoiceEffect.ApplyKnowledgeCurse);

        public ReplayForkSeed Fork(CombatBeamSolver owner, object gate, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner._run.ForkCount++;
                owner._run.RoundReplayPrefixReuses++;
                using var measure = owner._run.Performance.Measure(SearchMetricPhase.Fork);
                return new ReplayForkSeed(
                    (_simulator ?? throw new InvalidOperationException("Round checkpoint was released.")).Fork(),
                    (_deaths ?? throw new InvalidOperationException("Round checkpoint was released.")).Fork());
            }
        }

        public void Dispose()
        {
            _simulator = null;
            _deaths = null;
        }
    }

    private sealed class RoundReplayCheckpointCapture(SearchNode parent) : IDisposable
    {
        private RoundReplayCheckpoint? _checkpoint;
        public bool ReachedStablePrefix { get; private set; }
        public bool ReachedHandDrawShuffle { get; private set; }
        public bool HasCheckpoint => _checkpoint is not null;
        public void ObservePendingChoice(CombatBeamSolver owner, string? sourceId = null)
        {
            if (ReachedStablePrefix)
                owner._run.HasObservedPostDrawRoundChoice = true;
            else if (ReachedHandDrawShuffle && sourceId != null)
                (owner._run.ObservedHandDrawShuffleChoiceSources ??= new(StringComparer.Ordinal)).Add(sourceId);
        }
        public void CaptureBeforeHandDraw(CombatBeamSolver owner, CombatPredictionSimulator simulator,
            SimulatedCombatState combat, TurnStartChoiceCursor cursor,
            ISet<uint> deaths, int shufflesCrossed, bool takingExtraTurn,
            bool sideTurnStartTriggeredEarly, int drawCount, bool willShuffle)
        {
            if (!willShuffle || !simulator.IsInProgress || combat.PlayerTurnEndRequested
                || combat.HasPendingChoice)
                return;
            ReachedHandDrawShuffle = true;
            if (owner._run.ObservedHandDrawShuffleChoiceSources is not { Count: > 0 } sources)
                return;
            // Hints contain source identifiers only. Keep ordinary branches on the later
            // post-draw prefix when their observed choice-producing power is absent.
            IReadOnlyList<PowerModel> powers = combat.EffectivePowers();
            for (int index = 0; index < powers.Count; index++)
            {
                PowerModel power = powers[index];
                if (power.Amount > 0 && ReferenceEquals(power.Owner, owner._player.Creature)
                    && sources.Contains(power.Id.Entry))
                {
                    CaptureCore(owner, simulator, combat, cursor, deaths, shufflesCrossed,
                        takingExtraTurn, sideTurnStartTriggeredEarly, drawCount);
                    return;
                }
            }
        }
        public void Capture(CombatBeamSolver owner, CombatPredictionSimulator simulator,
            SimulatedCombatState combat, TurnStartChoiceCursor cursor,
            ISet<uint> deaths, int shufflesCrossed, bool takingExtraTurn, bool sideTurnStartTriggeredEarly)
        {
            if (!simulator.IsInProgress || combat.PlayerTurnEndRequested)
                return;
            ReachedStablePrefix = true;
            // A pre-draw prefix already covers this parent's earlier choice point.
            // Other duplicate captures still fail in CaptureCore.
            if (_checkpoint is { HandDrawCount: not null })
                return;
            // Keep the existing immediate reservation, and learn other sources from a
            // completed probe. Outside the existing reservation, a lane pays for no
            // copies until it has observed a post-draw choice.
            // Every copy still belongs to this parent; the hint never shares state.
            if (!owner._run.HasObservedPostDrawRoundChoice
                && combat.GetAmount<ToolsOfTheTradePower>(owner._player.Creature) <= 0)
                return;
            CaptureCore(owner, simulator, combat, cursor, deaths, shufflesCrossed,
                takingExtraTurn, sideTurnStartTriggeredEarly, handDrawCount: null);
        }
        private void CaptureCore(CombatBeamSolver owner, CombatPredictionSimulator simulator,
            SimulatedCombatState combat, TurnStartChoiceCursor cursor,
            ISet<uint> deaths, int shufflesCrossed, bool takingExtraTurn,
            bool sideTurnStartTriggeredEarly, int? handDrawCount)
        {
            if (_checkpoint != null)
                throw new InvalidOperationException("Round prefix captured twice.");
            PlanChoiceTiming timing = combat.ActiveActionChoiceTiming;
            // This initial probe has no planned choices. All prefix commands have returned;
            // the original Fork assertions still check every engine and model transaction.
            combat.EndActionChoices();
            try
            {
                owner._run.ForkCount++;
                owner._run.RoundReplayPrefixCaptures++;
                using var measure = owner._run.Performance.Measure(SearchMetricPhase.Fork);
                var fork = simulator.Fork();
                _checkpoint = new(parent, fork, ((ForkableSet<uint>)deaths).Fork(),
                    shufflesCrossed, takingExtraTurn, sideTurnStartTriggeredEarly, handDrawCount);
            }
            finally
            {
                combat.BeginActionChoices(cursor);
                combat.SetActionChoiceTiming(timing);
            }
        }
        public RoundReplayCheckpoint? Take()
        {
            var result = _checkpoint;
            _checkpoint = null;
            return result;
        }
        public void Dispose()
        {
            _checkpoint?.Dispose();
            _checkpoint = null;
        }
    }

    private SearchBoundaryReason ResumeRoundPlayerStart(
        CombatPredictionSimulator simulator, SimulatedCombatState combat, int roundIndex,
        ISet<uint> deaths, ref int shufflesCrossed,
        IReadOnlyList<PlanCardChoice>? choices, RoundReplayCheckpoint checkpoint)
    {
        TurnStartChoiceCursor cursor = new(choices);
        combat.BeginActionChoices(cursor);
        combat.SetActionChoiceTiming(PlanChoiceTiming.PlayerTurnStart);
        try
        {
            using var measure = _run.Performance.Measure(SearchMetricPhase.RoundPlayerStart);
            bool sideTurnStartTriggeredEarly = checkpoint.SideTurnStartTriggeredEarly;
            if (checkpoint.HandDrawCount is int drawCount)
            {
                using (sideTurnStartTriggeredEarly ? null : cursor.BeforeNextTake(() =>
                       {
                           sideTurnStartTriggeredEarly = true;
                           return combat.TriggerSideTurnStart(simulator, CombatSide.Player,
                               [_player.Creature],
                               decrementPlating: combat.GetPlayerTurnNumber(_player) != 1,
                               checkpoint.TakingExtraTurn);
                       }))
                {
                    if (!DrawPreparedPlayerHand(simulator, combat, drawCount,
                            willShuffle: true, ref shufflesCrossed))
                        return SearchBoundaryReason.PendingChoice;
                }
            }
            return CompleteRoundPlayerStart(simulator, combat, roundIndex, deaths, cursor,
                checkpoint.TakingExtraTurn, sideTurnStartTriggeredEarly);
        }
        finally
        {
            combat.EndActionChoices();
        }
    }
}
