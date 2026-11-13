using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace CombatSolver;

internal static partial class StrategicHpRecoveryBound
{
    internal static bool HasCertifiedNativeNonHealingGenerationPool(CardPoolModel pool)
    {
        if (typeof(CardModel).Module.ModuleVersionId != ComponentAuditMvid
            || pool.IsMutable || pool.AllCards is not CardModel[] cards)
            return false;
        foreach (CardModel card in cards)
        {
            if (card.IsMutable || card.GetType().Assembly != typeof(CardModel).Assembly)
                return false;
            // Validate a superset of every native unlock/player-count selection. This
            // reads canonical root metadata and neither selects a card nor advances RNG.
            if (card.CanBeGeneratedInCombat && card.Rarity != CardRarity.Ancient
                && card.Rarity != CardRarity.Event
                && !NativeNonHealingGeneratedCards.Contains(card.GetType()))
                return false;
        }
        return pool.GetType() == typeof(RegentCardPool) || pool.GetType() == typeof(ColorlessCardPool)
            || pool.GetType() == typeof(IroncladCardPool) || pool.GetType() == typeof(SilentCardPool)
            || pool.GetType() == typeof(NecrobinderCardPool) || pool.GetType() == typeof(DefectCardPool)
            || pool.GetType() == typeof(FoolCardPool)
            || pool.GetType() == typeof(StatusCardPool) || pool.GetType() == typeof(CurseCardPool);
    }

}
