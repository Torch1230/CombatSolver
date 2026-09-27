using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

// Observations, not utilities. No card/relic bonus or exchange rate is encoded.
// Named sparse columns avoid merging unrelated identities into small hash buckets.
internal static class SearchOutcomeContext
{
    internal static Dictionary<string, double> Capture(CombatPredictionSimulator simulator, Player player)
    {
        Dictionary<string, double> values = new(StringComparer.Ordinal);
        Capture(new FeatureWriter(values), simulator, player);
        return values;
    }

    internal static void CaptureSelected(CombatPredictionSimulator simulator, Player player,
        IReadOnlyDictionary<string, int> columns, double[] values)
    {
        Array.Clear(values);
        Capture(new FeatureWriter(columns, values), simulator, player);
    }

    private static void Capture(FeatureWriter x, CombatPredictionSimulator simulator, Player player)
    {
        var state = simulator.State.GetPlayerCombatState(player);
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        var body = simulator.State.GetCreature(player.Creature);
        void Add(string name, double value) => x.Add(name, value);
        x["character/" + player.Character.Id.Entry] = 1;
        x["player/hp"] = body.CurrentHp;
        x["player/max-hp"] = body.MaxHp;
        x["player/block"] = body.Block;
        x["player/energy"] = state.Energy;
        x["player/stars"] = state.Stars;
        foreach (var relic in combat.RelicsOf(player))
        {
            string name = "relic/" + relic.Id.Entry;
            x[name + "/present"] = 1;
            x[name + "/melted"] = relic.IsMelted ? 1 : 0;
            var key = combat.CaptureRelicObservation(simulator, relic);
            x[$"{name}/state/{key.First:X16}{key.Second:X16}"] = 1;
            if (RelicCounterCatalog.Identify(relic) != null)
                x[name + "/counter"] = combat.ReadRelicCounter(simulator, relic);
        }
        if (combat.TryGetPocketwatchState(player, out int current, out int previous, out int threshold))
        {
            x["relic/POCKETWATCH/current"] = current;
            x["relic/POCKETWATCH/previous"] = previous;
            x["relic/POCKETWATCH/threshold"] = threshold;
        }
        var piles = new[] { state.Hand.Cards, state.DrawPile.Cards, state.DiscardPile.Cards, state.ExhaustPile.Cards };
        string[] zones = ["hand", "draw", "discard", "exhaust"];
        for (int zone = 0; zone < piles.Length; zone++)
        {
            string pile = "pile/" + zones[zone];
            x[pile + "/count"] = piles[zone].Count;
            for (int index = 0; index < piles[zone].Count; index++)
            {
                var card = piles[zone][index];
                var preview = card.Preview;
                string id = pile + "/card/" + preview.Id.Entry;
                string type = pile + "/type/" + preview.Type;
                Add(id + "/count", 1);
                Add(id + "/upgrades", preview.CurrentUpgradeLevel);
                Add(type + "/count", 1);
                // Hand costs include branch-owned global and local cost modifiers.
                if (zone == 0)
                {
                    int energy = card.GetEnergyCostWithModifiers(simulator, state);
                    int stars = card.GetStarCostWithModifiers(simulator, state);
                    Add(id + "/energy", energy); Add(id + "/stars", stars);
                    Add(type + "/energy", energy); Add(type + "/stars", stars);
                }
                Add(type + "/x-cost", preview.EnergyCost.CostsX ? 1 : 0);
                Add(type + "/exhaust-next", preview.ExhaustOnNextPlay ? 1 : 0);
                Add(type + "/retain", preview.ShouldRetainThisTurn ? 1 : 0);
                foreach (var keyword in preview.Keywords) Add(type + "/keyword/" + keyword, 1);
                foreach (var (name, value) in preview.DynamicVars)
                    if (SemanticStateFieldPolicy.IsSemantic(preview, name, value))
                        Add(type + "/var/" + name, (double)value.BaseValue);
                if (preview.Enchantment is { } enchantment)
                    Add(id + "/enchantment/" + enchantment.Id.Entry, enchantment.Amount);
                if (preview.Affliction is { } affliction)
                    Add(id + "/affliction/" + affliction.Id.Entry, affliction.Amount);
                // Draw order is semantically meaningful; preserve the next hand's prefix.
                if (zone == 1 && index < 10) Add($"{pile}/position/{index}/{preview.Id.Entry}", 1);
            }
        }
        foreach (var power in combat.EffectivePowers())
        {
            string owner = ReferenceEquals(power.Owner, player.Creature) ? "player" : "enemy/" + power.Owner?.CombatId;
            Add("power/" + owner + "/" + power.Id.Entry, power.Amount);
        }
        for (int index = 0; index < combat.KnownEnemies.Count; index++)
        {
            var enemy = combat.KnownEnemies[index];
            var predicted = simulator.State.GetCreature(enemy);
            string name = "enemy/" + index;
            x[name + "/hp"] = combat.EffectiveEnemyHp(enemy, predicted);
            x[name + "/block"] = predicted.Block;
            x[name + "/present"] = combat.ContainsCreature(enemy) ? 1 : 0;
        }
        x["orb/capacity"] = state.OrbQueue.Capacity;
        for (int index = 0; index < state.OrbQueue.Orbs.Count; index++)
        {
            var orb = state.OrbQueue.Orbs[index];
            string name = $"orb/{index}/{orb.Id.Entry}";
            x[name + "/present"] = 1;
            x[name + "/passive"] = (double)OrbMirrors.GetPassiveValue(simulator, orb);
            x[name + "/evoke"] = (double)OrbMirrors.GetEvokeValue(simulator, orb);
        }
    }
    private readonly struct FeatureWriter
    {
        private readonly Dictionary<string, double>? _sparse;
        private readonly IReadOnlyDictionary<string, int>? _columns;
        private readonly double[]? _values;
        internal FeatureWriter(Dictionary<string, double> sparse) => _sparse = sparse;
        internal FeatureWriter(IReadOnlyDictionary<string, int> columns, double[] values)
        { _columns = columns; _values = values; }
        internal double this[string name]
        {
            set
            {
                if (_sparse != null) _sparse[name] = value;
                else if (_columns!.TryGetValue(name, out int index)) _values![index] = value;
            }
        }
        internal void Add(string name, double value)
        {
            if (_sparse != null) _sparse[name] = _sparse.GetValueOrDefault(name) + value;
            else if (_columns!.TryGetValue(name, out int index)) _values![index] += value;
        }
    }

}
