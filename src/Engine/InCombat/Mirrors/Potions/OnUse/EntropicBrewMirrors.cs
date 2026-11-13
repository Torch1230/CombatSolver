using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Mirrors.Potions.OnUse;

internal static class EntropicBrewMirrors
{
    public static void OnUse(EntropicBrew _, PotionOnUseMirrorContext context)
    {
        var target = context.TargetPlayer;
        int potionSlotCount = context.CombatState is ICombatPredictionPlayerLimits limits
            ? limits.GetPotionSlotCount(target)
            : throw new InvalidOperationException("Entropic Brew prediction requires captured player limits.");
        var potions = Generate(target, potionSlotCount, context.Rng.NonCombatGeneration(target));

        foreach (var generatedPotion in potions)
        {
            // Potion-slot mutation and procurement hooks are outside the current simulator state domains.
            context.History.PotionGenerated(generatedPotion);
        }
    }

    /// <summary>Generates potion reward models without advancing real RNG or mutating potion slots.</summary>
    /// <param name="target">The player whose potion pool and belt capacity apply.</param>
    /// <param name="potionSlotCount">The potion-belt capacity captured at the prediction root.</param>
    /// <param name="rng">The target player's branch-owned non-combat-generation RNG.</param>
    public static IReadOnlyList<PotionModel> Generate(Player target, int potionSlotCount, Rng rng)
    {
        // The player may discard existing potions before drinking Entropic Brew, so preserve the existing
        // presentation policy of showing enough future results to fill the entire belt rather than only open slots.
        return PredictionUtils.PredictPotionRewards(target, potionSlotCount, rng);
    }
}
