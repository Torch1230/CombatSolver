using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class CardDrawCardMirrors
{
    public static void CompileDriverOnPlay(CompileDriver card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;
        var drawCount = context.OwnerState.OrbQueue.Orbs.Select(orb => orb.Id).Distinct().Count();
        context.Simulator.Draw(card.Owner, drawCount);
    }

    public static void CalculatedGambleOnPlay(CalculatedGamble card, CardOnPlayMirrorContext context)
    {
        var cards = context.OwnerState.Hand.Cards.ToArray();
        context.Simulator.DiscardAndDraw(cards, cards.Length);
    }

    public static void ConstellationOnPlay(Constellation card, CardOnPlayMirrorContext context)
    {
        var player = context.TargetPlayer;
        context.Simulator.Draw(player, card.DynamicVars.Cards.BaseValue);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Simulator.GainEnergy(player, card.DynamicVars.Energy.IntValue);
        if (context.Simulator.HasPendingChoice)
            return;
        context.GainBlock(player.Creature);
    }

    public static void EscapePlanOnPlay(EscapePlan card, CardOnPlayMirrorContext context)
    {
        var drawnCards = context.Simulator.Draw(card.Owner, 1);
        if (context.Simulator.HasPendingChoice)
            return;
        if (drawnCards is [{ Preview.Type: CardType.Skill }])
        {
            context.GainBlock(card.Owner.Creature);
        }
    }

    public static void ExpertiseOnPlay(Expertise card, CardOnPlayMirrorContext context)
    {
        var drawnCards = context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.IntValue);
        if (context.Simulator.HasPendingChoice)
            return;
        foreach (var drawnCard in drawnCards)
        {
            drawnCard.MutablePreview.GiveSingleTurnRetain();
        }
    }

    public static void FetchOnPlay(Fetch card, CardOnPlayMirrorContext context)
    {
        if (context.State.GetOsty(card.Owner) is not { } osty || context.State.GetCreature(osty).IsDead)
        {
            return;
        }

        DamageCmd.Attack(card.DynamicVars.OstyDamage.BaseValue)
            .FromOsty(osty, card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
        if (context.Simulator.HasPendingChoice)
            return;

        SimulatedCombatState combat = context.Simulator.State.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("Fetch requires simulated combat state.");
        if (!combat.WasFetchPlayedThisTurn(context.Card))
        {
            context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
        }
    }

    public static void FtlOnPlay(Ftl card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;

        SimulatedCombatState combat = context.Simulator.State.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("FTL requires simulated combat state.");
        if (combat.GetCardsPlayedThisTurn(card.Owner.Creature) < card.DynamicVars[Ftl._playMaxKey].IntValue)
        {
            context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
        }
    }

    public static void HuddleUpOnPlay(HuddleUp card, CardOnPlayMirrorContext context)
    {
        var allies = context.State.GetTeammatesOf(card.Owner.Creature)
            .Where(creature => creature.IsPlayer && context.State.GetCreature(creature).IsAlive);
        foreach (var ally in allies)
        {
            context.Simulator.Draw(ally.Player!, card.DynamicVars.Cards.BaseValue);
            if (context.Simulator.HasPendingChoice)
                return;
        }
    }

    public static void ImpatienceOnPlay(Impatience card, CardOnPlayMirrorContext context)
    {
        if (context.OwnerState.Hand.Cards.All(predicted => predicted.Preview.Type != CardType.Attack))
        {
            context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
        }
    }

    public static void PillageOnPlay(Pillage card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;

        while (true)
        {
            var drawnCards = context.Simulator.Draw(card.Owner, 1);
            if (context.Simulator.HasPendingChoice)
                return;
            if (drawnCards is not [{ Preview.Type: CardType.Attack }] ||
                context.OwnerState.Hand.Cards.Count >= context.Simulator.GetMaxHandSize(card.Owner))
            {
                break;
            }
        }
    }

    public static void RebootOnPlay(Reboot card, CardOnPlayMirrorContext context)
    {
        context.Simulator.MoveHandToDrawPile(card.Owner);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Simulator.Shuffle(card.Owner);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
    }

    public static void RestlessnessOnPlay(Restlessness card, CardOnPlayMirrorContext context)
    {
        if (context.OwnerState.Hand.IsEmpty)
        {
            context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.IntValue);
            if (context.Simulator.HasPendingChoice)
                return;
            context.Simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);
        }
    }

    public static void ScrapeOnPlay(Scrape card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;

        IReadOnlyList<PredictedCard> drawnCards = context.Simulator.Draw(
            card.Owner,
            card.DynamicVars.Cards.IntValue);
        if (context.Simulator.HasPendingChoice)
            return;
        var cardsToDiscard = drawnCards
            .Where(drawnCard =>
                drawnCard.Preview.EnergyCost.CostsX ||
                drawnCard.GetEnergyCostValueWithModifiers(context.Simulator) != 0)
            .ToList();
        context.Simulator.Discard(cardsToDiscard);
    }

    public static void ScrawlOnPlay(Scrawl card, CardOnPlayMirrorContext context)
    {
        int count = context.Simulator.GetMaxHandSize(card.Owner) - context.OwnerState.Hand.Cards.Count;
        context.Simulator.Draw(card.Owner, count);
    }

}
