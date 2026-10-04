using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    // Gold's after-gain hook uses RunState.IterateHookListeners(null), whereas
    // its modifier passes use the run prefix followed by combat listeners.
    // Only immutable root membership is shared; resources come from this branch.
    private sealed record GoldRunHookSnapshot(
        Player[] ActivePlayers, AbstractModel[] Globals)
    {
        public static GoldRunHookSnapshot Capture(RunState run,
            IReadOnlyDictionary<AbstractModel, AbstractModel> clones,
            IReadOnlyList<AbstractModel> runSubscribers)
        {
            AbstractModel[] globals = run.IterateHookListeners(null)
                .Where(model => model is not CardModel and not EnchantmentModel
                    and not RelicModel and not PotionModel
                    && !runSubscribers.Contains(model))
                .Select(model => clones.TryGetValue(model, out AbstractModel? clone)
                    ? clone : throw new PredictionUnsupportedException(
                        $"Run-only gold listener {model.GetType().FullName} was not captured."))
                .ToArray();
            return new(run.Players.Where(player => player.IsActiveForHooks).ToArray(), globals);
        }
    }

    internal IEnumerable<AbstractModel> GoldAfterGainHookListeners(CombatPredictionSimulator simulator)
    {
        foreach (AbstractModel listener in _rootRunHookListeners)
            if (GoldHookOwnerIsActive(simulator, listener))
                yield return listener;
        foreach (Player player in _goldRunHookSnapshot.ActivePlayers)
        {
            if (!simulator.State.GetPlayerCombatState(player).HooksActive)
                continue;
            foreach (RelicModel relic in RelicsOf(player))
                if (!relic.IsMelted)
                    yield return relic;
            for (int slot = 0; slot < PotionSlotCount(player); slot++)
                if (GetPotionAtSlot(player, slot) is { } potion)
                    yield return potion;
        }
        foreach (AbstractModel listener in _goldRunHookSnapshot.Globals)
            yield return listener;
        foreach (AbstractModel listener in _modHookSubscribers.RunSubscribers)
            yield return listener;
    }

    internal IEnumerable<AbstractModel> GoldModifierHookListeners(CombatPredictionSimulator simulator)
    {
        foreach (AbstractModel listener in ((ICombatPredictionHookListenerSource)this).RunHookListeners)
            if (GoldHookOwnerIsActive(simulator, listener))
                yield return listener;
    }

    private static bool GoldHookOwnerIsActive(CombatPredictionSimulator simulator, AbstractModel listener)
    {
        Player? owner = listener switch
        {
            CardModel card => card.Owner,
            EnchantmentModel enchantment when enchantment.HasCard => enchantment.Card.Owner,
            RelicModel relic => relic.Owner,
            PotionModel potion => potion.Owner,
            PowerModel power => power.Owner.Player,
            _ => null,
        };
        return owner is null || simulator.State.GetPlayerCombatState(owner).HooksActive;
    }
}
