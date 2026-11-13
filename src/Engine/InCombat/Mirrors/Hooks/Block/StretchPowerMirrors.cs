using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Block;

internal static class StretchPowerMirrors
{
    internal static void Before(StretchPower power, BeforeCardPlayedMirrorContext context)
    {
        if (context.CardPlay.Player.Creature != power.Owner || !context.PreviewCard.GainsBlock) return;
        var state = context.StateStore.Get<State>(power);
        if (state.Play is not null) return;
        state.Play = context.CardPlay;
        state.StartingAmount = power.Amount;
    }

    internal static decimal Modify(StretchPower power, ModifyBlockMultiplicativeMirrorContext context)
    {
        if (context.CardSource?.Preview.Owner != power.Owner.Player || !context.Props.IsPoweredCardOrMonsterMoveBlock()) return 0;
        var state = context.StateStore.Get<State>(power);
        if (state.Play is not null && !ReferenceEquals(state.Play, context.CardPlay)) return 0;
        if (context.CardPlay is null) return power.Amount;
        if (state.Play is null || state.Play.Player != context.CardSource!.Preview.Owner) return 0;
        state.GaveBlock = true;
        return power.Amount;
    }

    internal static void After(StretchPower power, AfterCardPlayedMirrorContext context)
        => Complete(context.Simulator, power, context.StateStore.Get<State>(power), context.CardPlay, true);

    internal static void Cleanup(CombatPredictionSimulator simulator, CardPlay play, bool completed)
    {
        foreach ((AbstractModel model, State state) in simulator.StateStore.ReadEntries<State>())
            Complete(simulator, (StretchPower)model, state, play, completed);
    }

    private static void Complete(CombatPredictionSimulator simulator, StretchPower power, State state, CardPlay play, bool completed)
    {
        if (!ReferenceEquals(state.Play, play)) return;
        state.Play = null;
        if (completed && state.GaveBlock)
            ((SimulatedCombatState)simulator.State.CombatState).SetPowerAmount(power, power.Amount - state.StartingAmount);
        state.GaveBlock = false;
        state.StartingAmount = 0;
    }

    private sealed class State : IPredictionStateForkable, IPredictionForkBoundary
    {
        public CardPlay? Play;
        public int StartingAmount;
        public bool GaveBlock;
        public object Fork(PredictionForkContext context) { AssertForkable(); return MemberwiseClone(); }
        public void AssertForkable()
        {
            if (Play is not null) throw new InvalidOperationException("Stretch is resolving a card at the Fork boundary.");
        }
    }
}
