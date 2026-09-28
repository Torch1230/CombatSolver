using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
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

    public static void OutrageOnPlay(Outrage card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;
        foreach (var member in Combat(context).Players)
        {
            if (!context.State.GetCreature(member.Creature).IsAlive)
                continue;
            PredictedCard clone = context.Card.CreateCloneForPlayer(member);
            context.Simulator.AddGeneratedCardToCombat(clone, PileType.Discard, card.Owner,
                resultKind: CardGenerationResultKind.Fixed);
            if (context.Simulator.HasPendingChoice)
                return;
        }
    }

    public static void GlimpseBeyondOnPlay(GlimpseBeyond card, CardOnPlayMirrorContext context)
    {
        foreach (var member in Combat(context).Players)
        {
            if (!context.State.GetCreature(member.Creature).IsAlive)
                continue;
            List<PredictedCard> souls = new(card.DynamicVars.Cards.IntValue);
            for (int index = 0; index < card.DynamicVars.Cards.IntValue; index++)
                souls.Add(PredictedCard.Create(CanonicalModels.Card<Soul>(), member));
            context.Simulator.AddGeneratedCardsToCombat(souls, PileType.Draw, card.Owner,
                CardPilePosition.Random, CardGenerationResultKind.Fixed);
            if (context.Simulator.HasPendingChoice)
                return;
        }
    }

    public static void TheBallOnPlay(TheBall card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;
        TheBall mutable = (TheBall)context.Card.MutablePreview;
        decimal increase = mutable.DynamicVars["Increase"].BaseValue;
        mutable.DynamicVars.Damage.BaseValue += increase;
        mutable._extraDamageFromPlays += increase;
    }

    public static void BeaconOfHopeOnPlay(BeaconOfHope card, CardOnPlayMirrorContext context)
        => Combat(context).Apply<BeaconOfHopePower>(card.Owner.Creature, 1, card.Owner.Creature);

    public static void CacophonyOnPlay(Cacophony card, CardOnPlayMirrorContext context)
        => Combat(context).Apply<CacophonyPower>(card.Owner.Creature,
            card.DynamicVars.Damage.IntValue, card.Owner.Creature);

    public static void ConcoctOnPlay(Concoct card, CardOnPlayMirrorContext context)
        => Combat(context).Apply<ConcoctPower>(context.Target,
            card.DynamicVars["ConcoctPower"].IntValue, card.Owner.Creature);

    public static void FlankingOnPlay(Flanking card, CardOnPlayMirrorContext context)
        => Combat(context).Apply<FlankingPower>(context.Target, 2, card.Owner.Creature);

    public static void HammerTimeOnPlay(HammerTime card, CardOnPlayMirrorContext context)
        => Combat(context).Apply<HammerTimePower>(card.Owner.Creature, 1, card.Owner.Creature);

    public static void HibernateOnPlay(Hibernate card, CardOnPlayMirrorContext context)
    {
        Combat(context).Apply<HibernatePower>(card.Owner.Creature, 1, card.Owner.Creature);
        if (context.Simulator.HasPendingChoice)
            return;
        for (int index = 0; index < card.DynamicVars.Repeat.IntValue; index++)
        {
            context.Simulator.OrbChannel<FrostOrb>(card.Owner);
            if (context.Simulator.HasPendingChoice)
                return;
        }
    }

    public static void InterceptOnPlay(Intercept card, CardOnPlayMirrorContext context)
    {
        context.GainBlock(card.Owner.Creature);
        if (context.Simulator.HasPendingChoice)
            return;
        Combat(context).Apply<CoveredPower>(context.Target, 1, card.Owner.Creature);
    }

    public static void SneakyOnPlay(Sneaky card, CardOnPlayMirrorContext context)
        => Combat(context).Apply<SneakyPower>(card.Owner.Creature,
            card.DynamicVars["SneakyPower"].IntValue, card.Owner.Creature);

    public static void SoulboundOnPlay(Soulbound card, CardOnPlayMirrorContext context)
        => Combat(context).Apply<SoulboundPower>(context.Target, 1, card.Owner.Creature);

    public static void TagTeamOnPlay(TagTeam card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
    }

    public static void TankOnPlay(Tank card, CardOnPlayMirrorContext context)
        => Combat(context).Apply<TankPower>(card.Owner.Creature, 1, card.Owner.Creature);

    public static void UnderworldOnPlay(Underworld card, CardOnPlayMirrorContext context)
        => Combat(context).Apply<UnderworldPower>(card.Owner.Creature, 1, card.Owner.Creature);

    private static SimulatedCombatState Combat(CardOnPlayMirrorContext context)
        => context.State.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("Multiplayer card requires SimulatedCombatState.");
}
