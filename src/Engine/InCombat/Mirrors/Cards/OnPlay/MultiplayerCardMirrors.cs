using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class MultiplayerCardMirrors
{
    public static void BelieveInYouOnPlay(BelieveInYou card, CardOnPlayMirrorContext context)
        => context.Simulator.GainEnergy(context.TargetPlayer, card.DynamicVars.Energy.IntValue);

    public static void BlazeOnPlay(Blaze card, CardOnPlayMirrorContext context)
        => Combat(context).Apply<StrengthPower>(
            context.Target, card.DynamicVars.Strength.IntValue, card.Owner.Creature);

    public static void CoordinateOnPlay(Coordinate card, CardOnPlayMirrorContext context)
        => Combat(context).ApplyTemporaryStrengthGain<CoordinatePower>(
            context.Target, card.DynamicVars.Strength.IntValue, card.Owner.Creature);

    public static void FadeOnPlay(Fade card, CardOnPlayMirrorContext context)
        => Combat(context).ApplyTemporaryDexterity<FadePower>(
            context.Target, card.DynamicVars.Dexterity.IntValue, card.Owner.Creature);

    public static void EnergySurgeOnPlay(EnergySurge card, CardOnPlayMirrorContext context)
    {
        foreach (var member in Combat(context).Players)
            if (context.State.GetCreature(member.Creature).IsAlive)
                context.Simulator.GainEnergy(member, card.DynamicVars.Energy.IntValue);
    }

    public static void PlotOnPlay(Plot card, CardOnPlayMirrorContext context)
    {
        SimulatedCombatState combat = Combat(context);
        foreach (var member in combat.Players)
            if (context.State.GetCreature(member.Creature).IsAlive)
                combat.Apply<DrawCardsNextTurnPower>(
                    member.Creature, card.DynamicVars.Cards.IntValue, card.Owner.Creature);
    }

    public static void OneForAllOnPlay(OneForAll card, CardOnPlayMirrorContext context)
    {
        SimulatedCombatState combat = Combat(context);
        foreach (var member in combat.Players)
            combat.Apply<OneForAllPower>(
                member.Creature, card.DynamicVars["OneForAllPower"].IntValue, card.Owner.Creature);
    }

    public static void BladeSymphonyOnPlay(BladeSymphony card, CardOnPlayMirrorContext context)
    {
        foreach (var member in Combat(context).Players)
            if (context.State.GetCreature(member.Creature).IsAlive)
                CardPileOnPlaySupport.GenerateShivs(
                    context.Simulator, member, card.DynamicVars.Cards.IntValue, upgraded: false);
    }

    public static void MimicOnPlay(Mimic card, CardOnPlayMirrorContext context)
        => context.GainBlock(card.Owner.Creature,
            context.Calculate(card.DynamicVars.CalculatedBlock),
            card.DynamicVars.CalculatedBlock.Props);

    public static void DemonicShieldOnPlay(DemonicShield card, CardOnPlayMirrorContext context)
    {
        context.Simulator.Damage([card.Owner.Creature], card.DynamicVars.HpLoss.BaseValue,
            ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move,
            card.Owner.Creature, context.Card, context.CardPlay);
        if (context.Simulator.HasPendingChoice)
            return;
        context.GainBlock(context.Target,
            context.Calculate(card.DynamicVars.CalculatedBlock),
            card.DynamicVars.CalculatedBlock.Props);
    }

    private static SimulatedCombatState Combat(CardOnPlayMirrorContext context)
        => context.State.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("Multiplayer card requires SimulatedCombatState.");
}
