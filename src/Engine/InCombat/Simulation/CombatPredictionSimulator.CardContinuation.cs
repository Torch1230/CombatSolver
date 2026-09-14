using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver.Engine.InCombat.Simulation;

// One explicit program counter: the manual card's own selector, before completion hooks.
// Root model references are stable identities; branch-mutable play/card/history is remapped.
internal sealed record ManualCardChoiceFrame(PredictedCard Card, Creature? Target, CardPlay Play,
    CardLocation Result, int OwnerBlockBefore, PredictionTraceFrame Trace, int HistoryStart,
    int ShuffleEventsBefore);

internal sealed partial class CombatPredictionSimulator
{
    private bool _captureManualCardChoice;
    private ManualCardChoiceFrame? _capturedManualCardChoice;
    private ManualCardChoiceFrame? _ownedManualCardChoice;
    private int _manualChoiceHistoryStart;
    private int _manualChoiceShuffleEvents;

    internal static bool SupportsManualCardChoiceContinuation(CardModel card)
        => card is DaggerThrow or Acrobatics or Prepared
            && card.Enchantment is null && card.Affliction is null;

    private void GuardOrdinaryCardContinuationFork()
    {
        if (_ownedManualCardChoice != null || _capturedManualCardChoice != null)
            throw new InvalidOperationException("Suspended card requires its owning continuation fork.");
    }

    internal bool BeginManualCardChoiceCapture(PredictedCard card)
    {
        if (!SupportsManualCardChoiceContinuation(card.Preview)
            || !StateStore.SupportsManualCardChoiceContinuation) return false;
        GuardOrdinaryCardContinuationFork();
        AssertForkable();
        _manualChoiceHistoryStart = History.PrepareManualCardChoice();
        _manualChoiceShuffleEvents = ShuffleEventCount;
        _captureManualCardChoice = true;
        return true;
    }

    internal void EndManualCardChoiceCapture() => _captureManualCardChoice = false;

    // Called only after every CLR action/card/trace scope has unwound. The pending request
    // stays available to the existing choice builder until that discovery snapshot is released.
    internal ManualCardChoiceFrame? TakeManualCardChoiceFrame()
    {
        if (_captureManualCardChoice || CurrentFrame != null || _ownedManualCardChoice != null)
            throw new InvalidOperationException("Manual card capture has not reached its ownership boundary.");
        ManualCardChoiceFrame? frame = _capturedManualCardChoice;
        _capturedManualCardChoice = null;
        _ownedManualCardChoice = frame;
        return frame;
    }

    private bool TryCaptureManualCardChoice(PredictedCard card, Creature? target, CardPlay play,
        CardLocation result, int ownerBlockBefore)
    {
        // In particular, ordinary replay performs no diagnostic work or history materialization.
        if (!_captureManualCardChoice) return false;
        if (_capturedManualCardChoice != null || !SupportsManualCardChoiceContinuation(card.Preview)
            || play.IsAutoPlay || play.PlayCount != 1 || play.PlayIndex != 0
            || CurrentFrame is not { Parent: null } trace
            || _damageSource != null || _activeDrawDepth != 0 || ActionRelicTriggers != null
            || _blockGainedByCardPlay.Count != 0 || !StateStore.SupportsManualCardChoiceContinuation
            || State.CombatState is not ICombatPredictionCardContinuationState { CanCaptureManualCardChoice: true }
            || !History.SupportsManualCardChoice(_manualChoiceHistoryStart, trace, play))
            return false;
        _capturedManualCardChoice = new(card, target, play, result, ownerBlockBefore,
            trace, _manualChoiceHistoryStart, _manualChoiceShuffleEvents);
        return true;
    }

    internal CombatPredictionSimulator ForkManualCardChoice(ManualCardChoiceFrame source,
        out ManualCardChoiceFrame frame)
    {
        if (!ReferenceEquals(source, _ownedManualCardChoice))
            throw new InvalidOperationException("Manual card fork does not own this suspended frame.");
        // The checkpoint owns the request as well as the frame. Only this private fork removes
        // it temporarily; ordinary Fork still rejects the seed, and all transaction assertions run.
        using var pending = ((ICombatPredictionCardContinuationState)State.CombatState)
            .DetachPendingManualCardChoice();
        AssertForkable();
        using PredictionForkContext context = new();
        PredictionTrace trace = new();
        CombatPredictionState state = State.Fork(context);
        PredictedCard card = context.RequireRemap(source.Card);
        CardPlay play = new()
        {
            Card = card.MutablePreview, Player = source.Play.Player, Target = source.Play.Target,
            ResultPile = source.Play.ResultPile, Resources = source.Play.Resources,
            IsAutoPlay = source.Play.IsAutoPlay, PlayIndex = source.Play.PlayIndex, PlayCount = source.Play.PlayCount,
        };
        context.Register(source.Play, play);
        PredictionTraceFrame action = new() { Source = source.Trace.Source,
            Invocation = source.Trace.Invocation, Parent = null };
        context.Register(source.Trace, action);
        frame = source with { Card = card, Play = play, Trace = action };
        PredictionStateStore store = StateStore.Fork(context);
        CombatPredictionHistory history = History.ForkManualCardChoice(trace, context, source.HistoryStart);
        return new CombatPredictionSimulator(trace, state, Rng.Fork(), store, history,
            IsInProgress, IsAboutToLose, TerminalStamp, ShuffleEventCount, null);
    }

    internal bool ResumeManualCardChoice(ManualCardChoiceFrame frame)
    {
        using (_trace.ResumeManualCardChoice(frame.Trace))
        {
            if (!((ICombatPredictionManualCardChoiceSink)State.CombatState).ResolveManualCardChoice(this, frame.Card))
                return false;
            if (CompleteManualCardPlayTail(frame.Card, frame.Target, frame.Play, frame.OwnerBlockBefore))
                CompleteManualCardResultTail(frame.Card, frame.Play.Player, frame.Result);
        }
        if (History.HasCardPlayStartedSince(frame.HistoryStart, frame.Trace) && !HasPendingChoice)
            ((ICombatPredictionCardExecutionSink)State.CombatState).CompleteCardExecution(this);
        return !HasPendingChoice;
    }
}
