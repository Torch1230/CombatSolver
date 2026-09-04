using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal static class CardDynamicVarWarmup
{
    public static void EnsureMaterialized(CombatState state)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("Card dynamic variables must be materialized on the main thread.");

        foreach (CardModel card in state.Players
                     .Where(player => player.PlayerCombatState != null)
                     .SelectMany(player => player.PlayerCombatState!.AllCards))
        {
            _ = card.DynamicVars;
        }
    }
}
