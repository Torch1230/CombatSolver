using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    private enum CardExecutionStage { Start, AfterOnPlay, OwnChoice, AfterChoice }

    internal static void PrepareExecutionCardPlay(PredictedCard card, CardPlay play, PredictionForkContext context)
    {
        PredictedCard copy = context.TryRemap(card, out PredictedCard? found) ? found! : card.Fork(context);
        context.Register(play.Card, copy.MutablePreview);
        ForkExecutionCardPlay(play, context);
    }

    private bool ContinueCardPlayExecution(PredictedCard card, Creature? target, bool isAutoPlay, ResourceInfo resources,
        CardLocation result, int playCount, string? choiceSource, string? choiceContext, int nextIndex,
        CardExecutionStage stage = CardExecutionStage.Start, CardPlay? activePlay = null,
        int ownerBlockBefore = 0, int playHistoryStart = 0)
    {
        var preview = card.MutablePreview;
        var owner = preview.Owner;
        var ownerCreature = State.GetCreature(owner.Creature);
        if (stage == CardExecutionStage.Start && ownerCreature.IsDead) return !HasPendingChoice;
        for (int index = nextIndex; index < playCount; index++)
        {
            if (stage == CardExecutionStage.Start)
            {
                if (IsOverOrEnding) break;
                preview.CurrentPlayIndex = index;
                ownerBlockBefore = ownerCreature.Block;
                playHistoryStart = History.Entries.Count;
                activePlay = new CardPlay { Card = preview, Player = owner, Target = target,
                    ResultPile = result.pileType, Resources = resources, IsAutoPlay = isAutoPlay,
                    PlayIndex = index, PlayCount = playCount };
                HookMirrors.BeforeCardPlayed(this, card, activePlay);
                if (HasPendingChoice)
                {
                    RejectExecutionContinuation();
                    HookMirrors.AbortCardPlayed(this, activePlay);
                    return false;
                }
                SynchronizePowerAmountPredictionStates();
                History.CardPlayStarted(card, activePlay);
                if (State.CombatState is ICombatPredictionCardExecutionSink startedSink)
                    startedSink.RecordCardPlayStarted(card, activePlay);
                var effectSink = State.CombatState as ICombatPredictionCardExecutionSink;
                stage = CardExecutionStage.AfterOnPlay;
                using (effectSink?.BeginCardPowerApplication(card))
                {
                    CardOnPlayMirrors.Invoke(this, card, activePlay);
                    if (!HasPendingChoice)
                    {
                        using (BeginExecutionDispatch())
                            effectSink?.ApplyCardPlayEffects(this, card, activePlay, target, ownerBlockBefore,
                                GetBlockGained(activePlay), playHistoryStart);
                        stage = CardExecutionStage.OwnChoice;
                    }
                }
                if (HasPendingChoice)
                {
                    Suspend();
                    return false;
                }
            }
            CardPlay play = activePlay ?? throw new InvalidOperationException("Card execution lost its active play.");
            if (stage == CardExecutionStage.AfterOnPlay)
            {
                if (State.CombatState is ICombatPredictionCardExecutionSink effectSink)
                {
                    using (effectSink.BeginCardPowerApplication(card))
                    using (BeginExecutionDispatch())
                        effectSink.ApplyCardPlayEffects(this, card, play, target, ownerBlockBefore,
                            GetBlockGained(play), playHistoryStart);
                }
                stage = CardExecutionStage.OwnChoice;
                if (HasPendingChoice)
                {
                    Suspend();
                    return false;
                }
            }
            if (stage == CardExecutionStage.OwnChoice)
            {
                bool selected = isAutoPlay
                    ? choiceSource is null || ResolveNestedAutoPlayChoice(card, choiceSource, choiceContext)
                    : State.CombatState is not ICombatPredictionManualCardChoiceSink choiceSink
                        || choiceSink.ResolveManualCardChoice(this, card);
                stage = CardExecutionStage.AfterChoice;
                if (!selected)
                {
                    if (!isAutoPlay && TryCaptureManualCardChoice(card, target, play, result, ownerBlockBefore))
                    {
                        _pendingExecutionSteps = null;
                        return false;
                    }
                    if (HasCapturedExecutionContinuation)
                        Suspend();
                    else
                        HookMirrors.AbortCardPlayed(this, play);
                    return false;
                }
            }
            if (!CompleteManualCardPlayTail(card, target, play, ownerBlockBefore))
            {
                if (HasPendingChoice)
                    AppendExecutionContinuation(new CardLoopExecutionFrame(card, target, isAutoPlay, resources,
                        result, playCount, choiceSource, choiceContext, index + 1, CardExecutionStage.Start, null, 0, 0));
                return false;
            }
            stage = CardExecutionStage.Start;
            activePlay = null;
            continue;

            void Suspend()
            {
                if (HasCapturedExecutionContinuation)
                    AppendExecutionContinuation(new CardLoopExecutionFrame(card, target, isAutoPlay, resources,
                        result, playCount, choiceSource, choiceContext, index, stage, activePlay, ownerBlockBefore, playHistoryStart));
                else
                    HookMirrors.AbortCardPlayed(this, activePlay!);
            }
        }
        CompleteManualCardResultTail(card, owner, result);
        return !HasPendingChoice;
    }

    private sealed record CardLoopExecutionFrame(PredictedCard Card, Creature? Target, bool IsAutoPlay,
        ResourceInfo Resources, CardLocation Result, int PlayCount, string? ChoiceSource, string? ChoiceContext,
        int NextIndex, CardExecutionStage Stage, CardPlay? Play, int OwnerBlockBefore, int PlayHistoryStart)
        : ICombatPredictionExecutionFrame
    {
        public IEnumerable<CardPlay> ActiveCardPlays => Play is null ? [] : [Play];
        public void PrepareFork(PredictionForkContext context)
        {
            if (Play != null) PrepareExecutionCardPlay(Card, Play, context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Card = context.RequireRemap(Card), Play = Play is null ? null : context.RequireRemap(Play) };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            simulator.ContinueCardPlayExecution(Card, Target, IsAutoPlay, Resources, Result, PlayCount,
                ChoiceSource, ChoiceContext, NextIndex, Stage, Play, OwnerBlockBefore, PlayHistoryStart);
            return !simulator.HasPendingChoice;
        }
    }

    private sealed record FinishCardExecutionFrame : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context) => this;
        public bool Resume(CombatPredictionSimulator simulator)
        {
            using (simulator.BeginExecutionDispatch())
                ((ICombatPredictionCardExecutionSink)simulator.State.CombatState).CompleteCardExecution(simulator);
            return !simulator.HasPendingChoice;
        }
    }
}
