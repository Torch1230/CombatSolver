using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver.HotKernel;

/// <summary>
/// Enforces that a combat root contains exclusively vanilla STS2 gameplay models
/// (defined in <c>sts2.dll</c>, i.e. <see cref="AbstractModel"/>'s assembly).
/// Throws <see cref="IncompatibleGameplayModException"/> on any foreign gameplay model.
/// </summary>
internal static class VanillaRootGate
{
    private static readonly Assembly VanillaAssembly = typeof(AbstractModel).Assembly;

    public static void ValidateVanillaOnly(CombatState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        CheckModel(state.Encounter, "encounter");
        CheckModel(state.Dominion, "dominion");

        foreach (ModifierModel modifier in state.Modifiers)
            CheckModel(modifier, "combat_modifier");

        if (state.RunState?.Modifiers is { } runModifiers)
        {
            foreach (ModifierModel modifier in runModifiers)
                CheckModel(modifier, "run_modifier");
        }

        foreach (Player player in state.Players)
        {
            CheckModel(player.Character, "character");

            foreach (RelicModel relic in player.Relics)
                CheckModel(relic, "relic");

            for (int slot = 0; slot < player.PotionSlots.Count; slot++)
                CheckModel(player.GetPotionAtSlotIndex(slot), "potion");

            if (player.PlayerCombatState is { } pcs)
            {
                foreach (CardModel card in pcs.AllCards)
                    CheckCard(card);

                foreach (OrbModel orb in pcs.OrbQueue.Orbs)
                    CheckModel(orb, "orb");
            }
        }

        foreach (Creature creature in state.Creatures)
            CheckCreature(creature);

        foreach (Creature escaped in state.EscapedCreatures)
            CheckCreature(escaped);
    }

    private static void CheckCreature(Creature creature)
    {
        CheckModel(creature.Monster, "monster");
        foreach (PowerModel power in creature.Powers)
        {
            CheckModel(power, "power");
            if (power is NightmarePower nightmare
                && nightmare.GetInternalData<NightmarePower.Data>().selectedCard is { } selected)
            {
                CheckCard(selected);
            }
        }
    }

    private static void CheckCard(CardModel card)
    {
        CheckModel(card, "card");
        CheckModel(card.Enchantment, "enchantment");
        CheckModel(card.Affliction, "affliction");
        if (card.IsDupe && card.DupeOf is { } dupeOf)
            CheckCard(dupeOf);
    }

    private static void CheckModel(AbstractModel? model, string scope)
    {
        if (model == null)
            return;
        Type type = model.GetType();
        if (type.Assembly == VanillaAssembly)
            return;

        string asmName = type.Assembly.GetName().Name ?? "unknown";
        throw new IncompatibleGameplayModException(
            modId: asmName,
            modName: asmName,
            subject: type.FullName ?? type.Name,
            scope: scope);
    }
}
