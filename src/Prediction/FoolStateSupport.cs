using System.Text;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class FoolStateSupport
{
    internal static int SlotIndex(IReadOnlyList<ArrowSlot> arrows, ArrowSlot arrow)
    {
        for (int index = 0; index < arrows.Count; index++)
            if (ReferenceEquals(arrows[index], arrow)) return index;
        throw new InvalidOperationException("Impaled arrow has no owning player slot.");
    }

    internal static void AppendState(StringBuilder text, Player player, IReadOnlyList<ArrowSlot> arrows,
        IEnumerable<PowerModel> powers, CombatPredictionSimulator? simulator = null)
    {
        text.Append(";arrows=");
        foreach (ArrowSlot arrow in arrows)
            text.Append((int)arrow.Status).Append(':').Append(arrow.DestroyAfterImpaled ? 1 : 0).Append(',');
        text.Append(";impaled=");
        foreach (ImpaledPower power in powers.OfType<ImpaledPower>().Where(power => power.Amount > 0 && power.Applier == player.Creature))
        {
            text.Append(power.Owner.CombatId).Append(':').Append(power.HitCounter).Append(':');
            foreach (ArrowSlot arrow in power.Arrows) text.Append(SlotIndex(arrows, arrow)).Append(',');
            text.Append('/');
        }
        text.Append(";fool_powers=");
        foreach (PowerModel power in powers.Where(power => power.Amount > 0 && power.Owner == player.Creature))
        {
            if (power is AimPower aim) text.Append("Aim:").Append(AimPowerMirrors.Describe(aim, simulator)).Append('/');
            if (power is EncorePower encore)
                text.Append("Encore:").Append(encore.GetInternalData<EncorePower.Data>().numberOfCardsPlayedSinceApplied).Append('/');
            if (power is YahPower yah)
                text.Append("Yah:").Append(yah.GetInternalData<YahPower.Data>().triggeredThisTurn ? 1 : 0).Append('/');
            if (power is LullabyPower lullaby)
                text.Append("Lullaby:").Append(lullaby._proccdThisTurn ? 1 : 0).Append('/');
        }
    }

    internal static void AddFingerprint(ref StateFingerprintBuilder key, Player player, IReadOnlyList<ArrowSlot> arrows,
        IEnumerable<PowerModel> powers, CombatPredictionSimulator simulator)
    {
        key.Add(arrows.Count);
        foreach (ArrowSlot arrow in arrows)
        {
            key.Add((int)arrow.Status);
            key.Add(arrow.DestroyAfterImpaled);
        }
        foreach (PowerModel model in powers)
        {
            if (model is AimPower aim && aim.Owner == player.Creature && aim.Amount > 0)
                key.Add(AimPowerMirrors.Describe(aim, simulator));
            if (model is EncorePower encore && encore.Owner == player.Creature && encore.Amount > 0)
                key.Add(encore.GetInternalData<EncorePower.Data>().numberOfCardsPlayedSinceApplied);
            if (model is ImpaledPower power && power.Amount > 0 && power.Applier == player.Creature)
            {
                key.Add(power.Owner.CombatId!.Value);
                key.Add(power.HitCounter);
                key.Add(power.Arrows.Count);
                foreach (ArrowSlot arrow in power.Arrows) key.Add(SlotIndex(arrows, arrow));
            }
            if (model is YahPower yah && yah.Owner == player.Creature && yah.Amount > 0)
                key.Add(yah.GetInternalData<YahPower.Data>().triggeredThisTurn);
            if (model is LullabyPower lullaby && lullaby.Owner == player.Creature && lullaby.Amount > 0)
                key.Add(lullaby._proccdThisTurn);
        }
    }
}
