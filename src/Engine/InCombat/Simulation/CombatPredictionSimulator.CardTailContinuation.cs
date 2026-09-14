using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Afflictions.OnPlay;
using CombatSolver.Engine.InCombat.Mirrors.Enchantments.OnPlay;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    private enum CardPlayTailStage { Enchantment, Affliction, Finish, AfterPlayed, Cleanup }
    private enum CardResultStage { Transfer, Move, HandEmpty, Cleanup }

    private bool ContinueCardPlayTailExecution(PredictedCard card, Creature? target, CardPlay play,
        int ownerBlockBefore, CardPlayTailStage stage, int completionHistoryStart)
    {
        var preview = card.MutablePreview;
        var owner = State.GetCreature(play.Player.Creature);
        if (stage <= CardPlayTailStage.Finish && owner.IsDead)
        {
            HookMirrors.AbortCardPlayed(this, play);
            return false;
        }
        if (stage == CardPlayTailStage.Enchantment)
        {
            if (preview.Enchantment is { } enchantment)
            {
                using (BeginExecutionDispatch()) EnchantmentOnPlayMirrors.Invoke(this, card, play, enchantment);
                if (HasPendingChoice) return Suspend(CardPlayTailStage.Affliction);
                if (owner.IsDead) { HookMirrors.AbortCardPlayed(this, play); return false; }
            }
            stage = CardPlayTailStage.Affliction;
        }
        if (stage == CardPlayTailStage.Affliction)
        {
            if (preview.Affliction is { } affliction)
            {
                using (BeginExecutionDispatch()) AfflictionOnPlayMirrors.Invoke(this, card, target, affliction);
                if (HasPendingChoice) return Suspend(CardPlayTailStage.Finish);
                if (owner.IsDead) { HookMirrors.AbortCardPlayed(this, play); return false; }
            }
            stage = CardPlayTailStage.Finish;
        }
        if (stage == CardPlayTailStage.Finish)
        {
            completionHistoryStart = History.Entries.Count;
            History.CardPlayFinished(card, play, card.HasKeyword(State, CardKeyword.Ethereal));
            HookMirrors.AfterCardPlayed(this, card, play);
            if (HasPendingChoice) return Suspend(CardPlayTailStage.AfterPlayed);
            stage = CardPlayTailStage.AfterPlayed;
        }
        if (stage == CardPlayTailStage.AfterPlayed && State.CombatState is ICombatPredictionCardExecutionSink sink)
        {
            using (BeginExecutionDispatch()) sink.CompleteCardPlayEffects(this, card, ownerBlockBefore, completionHistoryStart);
            if (HasPendingChoice) return Suspend(CardPlayTailStage.Cleanup);
        }
        _blockGainedByCardPlay.Remove(play);
        return !owner.IsDead;

        bool Suspend(CardPlayTailStage next)
        {
            if (HasCapturedExecutionContinuation)
                AppendExecutionContinuation(new CardPlayTailExecutionFrame(card, target, play, ownerBlockBefore, next, completionHistoryStart));
            else if (next <= CardPlayTailStage.Finish)
                HookMirrors.AbortCardPlayed(this, play);
            return false;
        }
    }

    private sealed record CardPlayTailExecutionFrame(PredictedCard Card, Creature? Target, CardPlay Play,
        int OwnerBlockBefore, CardPlayTailStage Stage, int CompletionHistoryStart) : ICombatPredictionExecutionFrame
    {
        public IEnumerable<CardPlay> ActiveCardPlays => Stage <= CardPlayTailStage.Finish ? [Play] : [];
        public void PrepareFork(PredictionForkContext context) => PrepareExecutionCardPlay(Card, Play, context);
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Card = context.RequireRemap(Card), Play = context.RequireRemap(Play) };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            simulator.ContinueCardPlayTailExecution(Card, Target, Play, OwnerBlockBefore, Stage, CompletionHistoryStart);
            return !simulator.HasPendingChoice;
        }
    }

    private bool ContinueCardResultExecution(PredictedCard card, Player originalOwner, CardLocation result, CardResultStage stage)
    {
        var preview = card.MutablePreview;
        if (stage == CardResultStage.Transfer)
        {
            if (originalOwner != result.player && result.pileType != PileType.None)
            {
                GiveToAnotherPlayer(card, originalOwner, result.player, result.pileType, result.position);
                if (HasPendingChoice) return Suspend(CardResultStage.Move);
            }
            stage = CardResultStage.Move;
        }
        if (stage == CardResultStage.Move)
        {
            if (card.GetPile(State)?.Type is PileType.Play)
            {
                switch (result.pileType)
                {
                    case PileType.None: RemoveFromCombat(card); break;
                    case PileType.Exhaust: Exhaust(card); break;
                    default: AddToPile(card, result.pileType, result.position); break;
                }
            }
            if (HasPendingChoice) return Suspend(CardResultStage.HandEmpty);
            stage = CardResultStage.HandEmpty;
        }
        if (stage == CardResultStage.HandEmpty && State.CombatState is ICombatPredictionCardEventSink handSink)
        {
            using (BeginExecutionDispatch()) handSink.AfterHandEmptied(this, originalOwner);
            if (HasPendingChoice) return Suspend(CardResultStage.Cleanup);
        }
        preview.EnergyCost.AfterCardPlayedCleanup();
        preview._temporaryStarCosts.RemoveAll(cost => cost.ClearsWhenCardIsPlayed);
        card.InvalidateCaches();
        preview.CurrentTarget = null;
        preview.CurrentPlayIndex = 0;
        SynchronizePowerAmountPredictionStates();
        return true;

        bool Suspend(CardResultStage next)
        {
            AppendExecutionContinuation(new CardResultExecutionFrame(card, originalOwner, result, next));
            return false;
        }
    }

    private sealed record CardResultExecutionFrame(PredictedCard Card, Player Owner, CardLocation Result, CardResultStage Stage)
        : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Card = context.RequireRemap(Card) };
        public bool Resume(CombatPredictionSimulator simulator) => simulator.ContinueCardResultExecution(Card, Owner, Result, Stage);
    }
}
