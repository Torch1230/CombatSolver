using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Potions.OnUse;

internal static class DrawPotionMirrors
{
    public static void BottledPotentialOnUse(BottledPotential potion, PotionOnUseMirrorContext context)
    {
        var player = context.TargetPlayer;
        context.Simulator.MoveHandToDrawPile(player);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Simulator.Shuffle(player);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Simulator.Draw(player, potion.DynamicVars.Cards.BaseValue);
    }

    public static void ClarityOnUse(Clarity potion, PotionOnUseMirrorContext context)
    {
        var player = context.TargetPlayer;
        context.Simulator.Draw(player, potion.DynamicVars.Cards.BaseValue);
        if (context.Simulator.HasPendingChoice)
            return;
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("清醒药水结算缺少可写的预测状态。");
        effects.ApplyPower(
            typeof(ClarityPower),
            player.Creature,
            potion.DynamicVars["ClarityPower"].IntValue,
            potion.Owner.Creature);
    }

    public static void CureAllOnUse(CureAll potion, PotionOnUseMirrorContext context)
    {
        var player = context.TargetPlayer;
        context.Simulator.GainEnergy(player, potion.DynamicVars.Energy.BaseValue);
        context.Simulator.Draw(player, potion.DynamicVars.Cards.BaseValue);
    }

    public static void GlowwaterPotionOnUse(GlowwaterPotion potion, PotionOnUseMirrorContext context)
    {
        var player = context.TargetPlayer;
        context.Simulator.ExhaustHand(player);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Simulator.Draw(player, potion.DynamicVars.Cards.BaseValue);
    }

    public static void SneckoOilOnUse(SneckoOil potion, PotionOnUseMirrorContext context)
    {
        var player = context.TargetPlayer;
        var hand = context.State.GetPlayerCombatState(player).Hand;

        context.Simulator.Draw(player, potion.DynamicVars.Cards.BaseValue);
        if (context.Simulator.HasPendingChoice)
            return;

        foreach (var card in hand)
        {
            if (card.Preview.EnergyCost.CostsX ||
                card.Preview.EnergyCost.GetWithModifiers(CostModifiers.None) < 0)
            {
                continue;
            }

            card.MutablePreview.EnergyCost.SetThisTurnOrUntilPlayed(context.Rng.CombatEnergyCosts.NextInt(4));
        }

        context.History.CardCostsRandomized(hand.Cards);
    }

    public static void SwiftPotionOnUse(SwiftPotion potion, PotionOnUseMirrorContext context)
    {
        context.Simulator.Draw(context.TargetPlayer, potion.DynamicVars.Cards.BaseValue);
    }
}
