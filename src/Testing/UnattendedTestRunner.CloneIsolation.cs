using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertCloneEventIsolation(Player player)
    {
        var source = FindActualHandCard(player, "STRIKE_IRONCLAD", 0);
        int events = 0;
        void OnChanged() => events++;
        source.EnchantmentChanged += OnChanged;
        try
        {
            var clone = PredictionUtils.CloneCardStateForSimulation(source);
            if (events != 0)
                throw new InvalidOperationException($"Prediction clone invoked {events} live enchantment callbacks.");
            if (clone.Enchantment is null || ReferenceEquals(source.Enchantment, clone.Enchantment)
                || !ReferenceEquals(clone.Enchantment.Card, clone))
                throw new InvalidOperationException("Prediction enchantment clone ownership differs.");
        }
        finally
        {
            source.EnchantmentChanged -= OnChanged;
        }
    }
}
