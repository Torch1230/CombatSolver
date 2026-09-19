using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.CardPiles;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

/// <summary>
/// The vanilla CardModel.Pile getter builds Player.Piles (two Concat iterators), a predicate
/// closure and interface enumerators for every query. Prediction cards ask for this property in
/// several upstream/Ritsu hooks, so that otherwise tiny lookup becomes a major allocation source.
///
/// A registered mod pile can extend both PlayerCombatState.AllPiles and Player.Piles. Keep those
/// semantics completely on the patched upstream path. When no mod pile exists, the two direct
/// loops below are exactly the vanilla order: combat piles first, then the run deck.
/// </summary>
internal static class SimulationCardPileLookupFastPath
{
    private static int _eligibleAfterRegistrationFreeze = -1;

    internal static bool CanUse()
    {
        int cached = Volatile.Read(ref _eligibleAfterRegistrationFreeze);
        if (cached >= 0)
            return cached != 0;
        if (!ModCardPileRegistry.IsFrozen)
            return false;

        bool eligible = ModCardPileRegistry.GetDefinitionsSnapshot().Length == 0;
        Interlocked.CompareExchange(
            ref _eligibleAfterRegistrationFreeze,
            eligible ? 1 : 0,
            -1);
        return Volatile.Read(ref _eligibleAfterRegistrationFreeze) != 0;
    }

    internal static CardPile? Find(CardModel card)
    {
        Player? owner = card._owner;
        IReadOnlyList<CardPile>? combatPiles = owner?.PlayerCombatState?.AllPiles;
        if (combatPiles != null)
        {
            for (int index = 0; index < combatPiles.Count; index++)
            {
                CardPile pile = combatPiles[index];
                if (pile.Cards.Contains(card))
                    return pile;
            }
        }

        CardPile? deck = owner?.Deck;
        return deck != null && deck.Cards.Contains(card)
            ? deck
            : null;
    }
}

internal sealed class SimulationCardPileLookupPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_simulation_card_pile_lookup_fast_path";
    public static string Description => "求解隔离域使用无分配的原版牌堆查询";

    public static ModPatchTarget[] GetTargets() =>
    [
        new ModPatchTarget(typeof(CardModel), "get_Pile", Type.EmptyTypes),
    ];

    public static bool Prefix(CardModel __instance, ref CardPile? __result)
    {
        if (!SimulationNotificationIsolation.IsActive
            || !SimulationCardPileLookupFastPath.CanUse())
        {
            return true;
        }

        __result = SimulationCardPileLookupFastPath.Find(__instance);
        return false;
    }
}
