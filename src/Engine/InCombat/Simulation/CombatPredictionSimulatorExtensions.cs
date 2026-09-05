using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.Engine.InCombat.Simulation;

internal static class CombatPredictionSimulatorExtensions
{
    // Convenience extension method to simulate an attack command.
    public static void Simulate(this AttackCommand attackCommand, CombatPredictionSimulator simulator)
    {
        simulator.ExecuteAttack(attackCommand);
    }

    // Exhausts the player's hand.
    // Used by Glowwater Potion and Pael's Eye to mirror their effects.
    public static void ExhaustHand(this CombatPredictionSimulator simulator, Player player)
    {
        var cards = simulator.State.GetPlayerCombatState(player).Hand.Cards.ToArray();
        foreach (var card in cards)
        {
            simulator.Exhaust(card);
            if (simulator.HasPendingChoice)
                return;
        }
    }

    // Moves all cards in the player's hand to the draw pile.
    // Used by Bottled Potential and Reboot to mirror their effects.
    public static void MoveHandToDrawPile(this CombatPredictionSimulator simulator, Player player)
    {
        var cards = simulator.State.GetPlayerCombatState(player).Hand.Cards.ToArray();
        simulator.AddToPile(cards, PileType.Draw);
    }
}
