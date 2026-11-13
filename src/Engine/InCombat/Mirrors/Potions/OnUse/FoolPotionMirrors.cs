using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver.Engine.InCombat.Mirrors.Potions.OnUse;

internal static class FoolPotionMirrors
{
    internal static void Precision(PrecisionPotion potion, PotionOnUseMirrorContext c)
        => ((SimulatedCombatState)c.CombatState).ApplyPowerFromSource(typeof(AimPower), c.TargetPlayer.Creature,
            potion.DynamicVars["AimPower"].IntValue, potion.Owner.Creature, null);

    internal static void CrystallineEuphoria(CrystallineEuphoria potion, PotionOnUseMirrorContext c)
    {
        var player = c.TargetPlayer;
        c.Simulator.CreateAndAddGeneratedCardsToCombat<Joy>(player, PileType.Hand,
            potion.DynamicVars.Cards.IntValue, potion.Owner);
        if (c.Simulator.HasPendingChoice) return;
        int remaining = CardPile.MaxCardsInHand - c.State.GetPlayerCombatState(player).Hand.Cards.Count;
        c.Simulator.CreateAndAddGeneratedCardsToCombat<Sadness>(player, PileType.Hand, remaining, potion.Owner);
    }

    internal static void BottledSong(BottledSong potion, PotionOnUseMirrorContext c)
    {
        var combat = (SimulatedCombatState)c.CombatState;
        var player = c.TargetPlayer;
        List<PredictedCard> cards = [];
        foreach (CardTag tag in new[] { CardTag.Wah, CardTag.Cho, CardTag.Ki })
        {
            var generated = combat.UnlockedCharacterCards(player).Where(card => card.Tags.Contains(tag))
                .GetDistinctForCombat(player, 1, c.Rng.CombatCardGeneration, combat.CardMultiplayerConstraint, combat.Rules)
                .FirstOrDefault();
            if (generated is null) continue;
            generated.SetToFreeThisTurn();
            cards.Add(generated);
        }
        c.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, potion.Owner,
            resultKind: CardGenerationResultKind.Random);
    }
}
