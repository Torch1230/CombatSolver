using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
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

    public static void ValidateVanillaOnly(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        int potionSlotCount)
    {
        ArgumentNullException.ThrowIfNull(simulator);
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(player);

        CheckModel(combat.Encounter, "encounter");
        CheckModel(player.Character, "character");

        foreach (ModifierModel modifier in combat.Modifiers)
            CheckModel(modifier, "combat_modifier");

        foreach (RelicModel relic in combat.RelicsOf(player))
            CheckModel(relic, "relic");

        for (int slot = 0; slot < potionSlotCount; slot++)
            CheckModel(combat.GetPotionAtSlot(player, slot), "potion");

        SimPlayerCombatState pcs = simulator.State.GetPlayerCombatState(player);
        foreach (PredictedCard card in pcs.AllCards)
            CheckCard(card.Preview);

        foreach (OrbModel orb in pcs.OrbQueue.Orbs)
            CheckModel(orb, "orb");

        foreach (Creature enemy in combat.Enemies)
            CheckModel(enemy.Monster, "monster");

        foreach (Creature ally in combat.Allies)
            CheckModel(ally.Monster, "ally");

        foreach (PowerModel power in combat.EffectivePowers())
        {
            CheckModel(power, "power");
            if (power is NightmarePower nightmare)
                CheckCard(combat.GetNightmareSelection(nightmare).Preview);
        }
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
