using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors;

internal static partial class HookMirrors
{
    public static bool BeforeHandDraw(CombatPredictionSimulator simulator, Player player, TurnStartChoiceCursor choices)
    {
        BeforeHandDrawMirrors.Seal();
        if (simulator.HasPendingChoice) return false;
        List<CardHookReceiver> receivers = [];
        foreach (AbstractModel listener in IterateCombatHookListeners(simulator, MirroredHookMask.AdditionalCallbacks))
            receivers.Add(new CardHookReceiver(listener, listener is CardModel card
                ? simulator.State.GetPlayerCombatState(card.Owner).FindCard(card) : null));
        return ContinueBeforeHandDraw(simulator, player, choices, receivers, 0);
    }

    private static bool ContinueBeforeHandDraw(CombatPredictionSimulator simulator, Player player,
        TurnStartChoiceCursor choices, IReadOnlyList<CardHookReceiver> receivers, int start)
    {
        var context = new BeforeHandDrawMirrorContext { Simulator = simulator, Player = player, Choices = choices };
        for (int index = start; index < receivers.Count; index++)
        {
            BeforeHandDrawMirrors.Invoke(receivers[index].Current, context);
            if (simulator.HasPendingChoice)
            {
                simulator.AppendExecutionContinuation(new BeforeHandDrawListenersFrame(player, receivers, index + 1));
                return false;
            }
        }
        return true;
    }

    private sealed record BeforeHandDrawListenersFrame(Player Player, IReadOnlyList<CardHookReceiver> Receivers,
        int NextIndex) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            foreach (CardHookReceiver receiver in Receivers) receiver.PrepareFork(context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Receivers = Receivers.Select(receiver => receiver.Fork(context)).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
            => ContinueBeforeHandDraw(simulator, Player, ((SimulatedCombatState)simulator.State.CombatState).ActiveExecutionChoices, Receivers, NextIndex);
    }
}
