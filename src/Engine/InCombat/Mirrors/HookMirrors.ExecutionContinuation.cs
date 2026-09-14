using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors;

internal static partial class HookMirrors
{
    private static IReadOnlyList<AbstractModel> CaptureUnfilteredExecutionHookListeners(CombatPredictionSimulator simulator)
    {
        List<AbstractModel> listeners = [];
        foreach (AbstractModel listener in simulator.State.IterateHookListeners()) listeners.Add(listener);
        return listeners;
    }

    private static bool ResumeAfterPlayExecution(CombatPredictionSimulator simulator, PredictedCard card, CardPlay play,
        int phase, IReadOnlyList<AbstractModel> listeners, int next)
    {
        var context = new AfterCardPlayedMirrorContext { Simulator = simulator, Card = card, CardPlay = play };
        while (phase < 2)
        {
            for (int index = next; index < listeners.Count; index++)
            {
                if (phase == 0) AfterCardPlayedMirrors.Invoke(listeners[index], context);
                else AfterCardPlayedMirrors.InvokeLate(listeners[index], context);
                if (!simulator.HasPendingChoice) continue;
                simulator.AppendExecutionContinuation(new AfterPlayExecutionFrame(card, play, phase, listeners, index + 1));
                return false;
            }
            phase++;
            next = 0;
            if (phase == 1) listeners = CaptureUnfilteredExecutionHookListeners(simulator);
        }
        BeforeCardPlayedMirrors.CompleteOrAbort(simulator, play);
        AfterCardPlayedMirrors.CompleteOrAbort(simulator, play, completed: true);
        return true;
    }

    private sealed record AfterPlayExecutionFrame(PredictedCard Card, CardPlay Play, int Phase,
        IReadOnlyList<AbstractModel> Listeners, int Next) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context) => CombatPredictionSimulator.PrepareExecutionCardPlay(Card, Play, context);
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Card = context.RequireRemap(Card), Play = context.RequireRemap(Play),
                Listeners = Listeners.Select(context.RemapOrSelf).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator) => ResumeAfterPlayExecution(simulator, Card, Play, Phase, Listeners, Next);
    }

    private enum CardEventExecutionKind { Exhaust, Discard, Generated }

    private static bool ResumeCardEventExecution(CombatPredictionSimulator simulator, PredictedCard card,
        CardEventExecutionKind kind, bool causedByEthereal, Player? creator,
        IReadOnlyList<AbstractModel> listeners, int next)
    {
        var exhaust = kind == CardEventExecutionKind.Exhaust
            ? new AfterCardExhaustedMirrorContext { Simulator = simulator, Card = card, CausedByEthereal = causedByEthereal } : null;
        var discard = kind == CardEventExecutionKind.Discard
            ? new AfterCardDiscardedMirrorContext { Simulator = simulator, Card = card } : null;
        var generated = kind == CardEventExecutionKind.Generated
            ? new AfterCardGeneratedForCombatMirrorContext { Simulator = simulator, Card = card, Creator = creator } : null;
        for (int index = next; index < listeners.Count; index++)
        {
            switch (kind)
            {
                case CardEventExecutionKind.Exhaust: AfterCardExhaustedMirrors.Invoke(listeners[index], exhaust!); break;
                case CardEventExecutionKind.Discard: AfterCardDiscardedMirrors.Invoke(listeners[index], discard!); break;
                case CardEventExecutionKind.Generated: AfterCardGeneratedForCombatMirrors.Invoke(listeners[index], generated!); break;
            }
            if (!simulator.HasPendingChoice) continue;
            simulator.AppendExecutionContinuation(new CardEventExecutionFrame(card, kind, causedByEthereal, creator, listeners, index + 1));
            return false;
        }
        return true;
    }

    private sealed record CardEventExecutionFrame(PredictedCard Card, CardEventExecutionKind Kind,
        bool CausedByEthereal, Player? Creator, IReadOnlyList<AbstractModel> Listeners, int Next) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Card = context.RequireRemap(Card), Listeners = Listeners.Select(context.RemapOrSelf).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
            => ResumeCardEventExecution(simulator, Card, Kind, CausedByEthereal, Creator, Listeners, Next);
    }

    private static IReadOnlyList<AbstractModel> CaptureExecutionHookListeners(
        CombatPredictionSimulator simulator, MirroredHookMask mask)
    {
        List<AbstractModel> listeners = [];
        foreach (AbstractModel listener in IterateCombatHookListeners(simulator, mask)) listeners.Add(listener);
        return listeners;
    }

    private static bool ResumeAfterDrawExecution(CombatPredictionSimulator simulator, PredictedCard card,
        CardModel initialCard, bool fromHandDraw, int phase, IReadOnlyList<AbstractModel> listeners,
        int next, bool intrinsicHandled)
    {
        var context = new AfterCardDrawnMirrorContext { Simulator = simulator, Card = card,
            InitialCard = initialCard, FromHandDraw = fromHandDraw, IntrinsicCardHandled = intrinsicHandled };
        while (phase < 2)
        {
            for (int index = next; index < listeners.Count; index++)
            {
                if (phase == 0) AfterCardDrawnMirrors.InvokeEarly(listeners[index], context);
                else AfterCardDrawnMirrors.Invoke(listeners[index], context);
                if (!simulator.HasPendingChoice) continue;
                simulator.AppendExecutionContinuation(new AfterDrawExecutionFrame(card, initialCard, fromHandDraw,
                    phase, listeners, index + 1, context.IntrinsicCardHandled));
                return false;
            }
            phase++;
            next = 0;
            if (phase == 1) listeners = CaptureExecutionHookListeners(simulator, MirroredHookMask.AfterCardDrawn);
        }
        AfterCardDrawnMirrors.Invoke(card.Preview, context);
        return !simulator.HasPendingChoice;
    }

    private sealed record AfterDrawExecutionFrame(PredictedCard Card, CardModel InitialCard, bool FromHandDraw,
        int Phase, IReadOnlyList<AbstractModel> Listeners, int Next, bool IntrinsicHandled) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Card = context.RequireRemap(Card), InitialCard = context.RemapOrSelf(InitialCard),
                Listeners = Listeners.Select(context.RemapOrSelf).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
            => ResumeAfterDrawExecution(simulator, Card, InitialCard, FromHandDraw, Phase, Listeners, Next, IntrinsicHandled);
    }

    private static bool ResumeAfterShuffleExecution(CombatPredictionSimulator simulator, Player player,
        IReadOnlyList<AbstractModel> listeners, int next)
    {
        var context = new AfterShuffleMirrorContext { Simulator = simulator, Player = player };
        for (int index = next; index < listeners.Count; index++)
        {
            AfterShuffleMirrors.Invoke(listeners[index], context);
            if (!simulator.HasPendingChoice) continue;
            simulator.AppendExecutionContinuation(new AfterShuffleExecutionFrame(player, listeners, index + 1));
            return false;
        }
        return true;
    }

    private sealed record AfterShuffleExecutionFrame(Player Player, IReadOnlyList<AbstractModel> Listeners, int Next)
        : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Listeners = Listeners.Select(context.RemapOrSelf).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
            => ResumeAfterShuffleExecution(simulator, Player, Listeners, Next);
    }
}
