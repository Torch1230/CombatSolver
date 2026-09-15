using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private enum PlayerStartStage { BeforeHand, PrepareDraw, Draw, CompensateDraw, AfterPlayer, Side, Deaths, Orbs, AutoPlay, Finish }

    // One branch owns the progress object. All frames and the replay accounting refer to
    // the same forked copy; an early side-start callback is never stored in a frame.
    private sealed class PlayerStartProgress(Player player, int turnNumber, bool rootSetup,
        bool takingExtraTurn, ISet<uint> deaths, int shufflesCrossed, int beforeHandShuffles)
    {
        public Player Player { get; } = player;
        public int TurnNumber { get; } = turnNumber;
        public bool RootSetup { get; } = rootSetup;
        public bool TakingExtraTurn { get; } = takingExtraTurn;
        public ISet<uint> Deaths { get; private set; } = deaths;
        public int ShufflesCrossed = shufflesCrossed;
        public int BeforeHandShuffles = beforeHandShuffles;
        public bool SideStarted;
        public int DrawCount;
        public bool WillShuffle;
        public int DrawHistoryStart;

        public PlayerStartProgress Fork(PredictionForkContext context)
        {
            if (context.TryRemap(this, out PlayerStartProgress? found)) return found!;
            var result = (PlayerStartProgress)MemberwiseClone();
            result.Deaths = SimulatedCombatState.ForkExecutionDeaths(Deaths, context);
            context.Register(this, result);
            return result;
        }
    }

    private sealed record PlayerStartFrame(PlayerStartProgress Progress, PlayerStartStage Stage)
        : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context) => Progress.Fork(context);
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Progress = context.RequireRemap(Progress) };
        public bool Resume(CombatPredictionSimulator simulator)
            => ContinuePlayerStart(simulator, (SimulatedCombatState)simulator.State.CombatState,
                Progress, Stage) == SearchBoundaryReason.None;
    }

    private static SearchBoundaryReason ContinuePlayerStart(CombatPredictionSimulator simulator,
        SimulatedCombatState combat, PlayerStartProgress progress, PlayerStartStage stage,
        CombatBeamSolver? captureOwner = null, RoundReplayCheckpointCapture? capture = null,
        SearchPerformanceMetrics? metrics = null)
    {
        simulator.AcknowledgeExecutionDispatch();
        Player player = progress.Player;
        var playerState = simulator.State.GetPlayerCombatState(player);
        TurnStartChoiceCursor choices = combat.ActiveExecutionChoices;
        if (stage == PlayerStartStage.BeforeHand && simulator.IsCapturingExecutionContinuation)
        {
            using (combat.SuspendExecutionChoicesForStablePrefix()) simulator.TrySealStableExecutionPrefix();
        }
        bool StartSide()
        {
            progress.SideStarted = true;
            // Side-start sources with additional work must qualify their own continuation.
            using var dispatch = simulator.BeginExecutionDispatch();
            return combat.TriggerSideTurnStart(simulator, CombatSide.Player, [player.Creature],
                decrementPlating: combat.GetPlayerTurnNumber(player) != 1, progress.TakingExtraTurn);
        }
        bool Suspended(PlayerStartStage next)
        {
            if (!combat.HasPendingChoice) return false;
            simulator.AppendExecutionContinuation(new PlayerStartFrame(progress, next));
            return true;
        }
        using var beforeChoice = !progress.SideStarted && stage <= PlayerStartStage.AfterPlayer
            ? choices.BeforeNextTake(StartSide) : null;
        if (stage <= PlayerStartStage.BeforeHand)
        {
            combat.PrepareBeforeHandDraw(simulator, player, choices);
            if (Suspended(PlayerStartStage.PrepareDraw)) return SearchBoundaryReason.PendingChoice;
        }
        if (stage <= PlayerStartStage.PrepareDraw)
        {
            if (!progress.RootSetup)
                progress.ShufflesCrossed += simulator.ShuffleEventCount - progress.BeforeHandShuffles;
            progress.DrawCount = PersistentPowerSupport.ConsumeModifiedHandDraw(combat, player, CombatManager.baseHandDrawCount);
            if (progress.RootSetup && progress.TurnNumber == 1)
            {
                SimCardPile drawPile = playerState.DrawPile;
                PredictedCard[] bottom = drawPile.Cards.Where(card => card.Preview.Enchantment?.ShouldStartAtBottomOfDrawPile ?? false).ToArray();
                foreach (PredictedCard card in bottom) { drawPile.Remove(card); drawPile.Add(card); }
                PredictedCard[] innate = drawPile.Cards.Where(card => card.Preview.Keywords.Contains(CardKeyword.Innate)).Except(bottom).ToArray();
                foreach (PredictedCard card in innate) { drawPile.Remove(card); drawPile.Insert(0, card); }
                progress.DrawCount = Math.Min(Math.Max(progress.DrawCount, innate.Length), combat.GetMaxHandSize(player));
            }
            int effectiveDraw = Math.Min(progress.DrawCount, combat.GetMaxHandSize(player) - playerState.Hand.Cards.Count);
            progress.WillShuffle = effectiveDraw > playerState.DrawPile.Cards.Count && !playerState.DiscardPile.IsEmpty;
            capture?.CaptureBeforeHandDraw(captureOwner!, simulator, combat, choices, progress.Deaths,
                progress.ShufflesCrossed, progress.TakingExtraTurn, progress.SideStarted, progress.DrawCount, progress.WillShuffle);
        }
        if (stage <= PlayerStartStage.Draw)
        {
            progress.DrawHistoryStart = simulator.History.Entries.Count;
            using (metrics?.Measure(SearchMetricPhase.RoundDraw))
                simulator.Draw(player, progress.DrawCount, fromHandDraw: true);
            if (!progress.RootSetup && progress.WillShuffle) progress.ShufflesCrossed++;
            if (Suspended(PlayerStartStage.CompensateDraw)) return SearchBoundaryReason.PendingChoice;
        }
        if (stage <= PlayerStartStage.CompensateDraw)
        {
            using (simulator.BeginExecutionDispatch())
                TriggeredPowerSupport.CompensateHistorySince(simulator, combat, progress.DrawHistoryStart);
            if (Suspended(PlayerStartStage.AfterPlayer)) return SearchBoundaryReason.PendingChoice;
            capture?.Capture(captureOwner!, simulator, combat, choices, progress.Deaths,
                progress.ShufflesCrossed, progress.TakingExtraTurn, progress.SideStarted);
        }
        if (stage <= PlayerStartStage.AfterPlayer)
        {
            combat.TriggerAfterPlayerTurnStart(simulator, player.Creature, choices);
            if (Suspended(PlayerStartStage.Side)) return SearchBoundaryReason.PendingChoice;
        }
        if (stage <= PlayerStartStage.Side && !progress.SideStarted)
        {
            StartSide();
            if (Suspended(PlayerStartStage.Deaths)) return SearchBoundaryReason.PendingChoice;
        }
        beforeChoice?.Dispose();
        if (stage <= PlayerStartStage.Deaths)
        {
            using (simulator.BeginExecutionDispatch())
                CorePowerSupport.ApplyEnemyDeathPowers(simulator, combat, combat.KnownEnemies, progress.Deaths);
            if (Suspended(PlayerStartStage.Orbs)) return SearchBoundaryReason.PendingChoice;
        }
        if (stage <= PlayerStartStage.Orbs)
        {
            using (simulator.BeginExecutionDispatch())
                EnchantmentLifecycleSupport.TriggerAfterTurnStartOrbs(simulator, player);
            if (Suspended(PlayerStartStage.AutoPlay)) return SearchBoundaryReason.PendingChoice;
        }
        if (stage <= PlayerStartStage.AutoPlay)
        {
            using (simulator.BeginExecutionDispatch())
                combat.TriggerAutoPrePlayEarly(simulator, player, progress.TurnNumber, choices, progress.Deaths);
            if (Suspended(PlayerStartStage.Finish)) return SearchBoundaryReason.PendingChoice;
        }
        choices.AssertConsumed();
        combat.NormalizeAeonglassWithers(simulator);
        combat.NormalizeCardAfflictions(simulator);
        IReadOnlyList<ForecastMove> moves = combat.CurrentMonsterMoves();
        combat.SetPredictedEnemyIntents(moves.Where(move => move.AttackHits.Count > 0).Select(move => move.Owner));
        simulator.CheckWinCondition(combat.GetPlayerTurnNumber(player));
        return SearchBoundaryReason.None;
    }
}
