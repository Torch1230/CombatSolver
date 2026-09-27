using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

// Observations, not utilities. No card/relic bonus or exchange rate is encoded.
// Named sparse columns avoid merging unrelated identities into small hash buckets.
internal static class SearchOutcomeContext
{
    internal const string EnemyPowerTotalPrefix = "power/enemies/";

    // A lossless addition to schema-6 observations: keep every original column
    // and expose the same power across roster positions to the learner.
    internal static void AddLegacyEnemyPowerTotals(Dictionary<string, double> values)
    {
        Dictionary<string, double> totals = new(StringComparer.Ordinal);
        foreach (var (name, amount) in values)
        {
            if (name.StartsWith(EnemyPowerTotalPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("Legacy observations already contain enemy power totals.");
            const string prefix = "power/enemy/";
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int separator = name.IndexOf('/', prefix.Length);
            if (separator < 0 || separator == name.Length - 1
                || !int.TryParse(name.AsSpan(prefix.Length, separator - prefix.Length),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int slot)
                || slot < 0)
                throw new InvalidDataException("Invalid legacy enemy power column.");
            string total = EnemyPowerTotalPrefix + name[(separator + 1)..];
            totals[total] = totals.GetValueOrDefault(total) + amount;
        }
        foreach (var (name, amount) in totals) values.Add(name, amount);
    }

    internal static Dictionary<string, double> Capture(CombatPredictionSimulator simulator, Player player)
    {
        Dictionary<string, double> values = new(StringComparer.Ordinal);
        Capture(new FeatureWriter(values), simulator, player);
        return values;
    }

    internal static void CaptureSelected(CombatPredictionSimulator simulator, Player player,
        IReadOnlyDictionary<string, int> columns, double[] values, IReadOnlySet<string>? prefixes = null)
    {
        Array.Clear(values);
        Capture(new FeatureWriter(columns, values, prefixes), simulator, player);
    }

    internal static HashSet<string> RequiredPrefixes(IEnumerable<string> names)
    {
        HashSet<string> prefixes = new(StringComparer.Ordinal);
        foreach (string name in names)
            for (int i = 0; i < name.Length; i++)
                if (name[i] == '/') prefixes.Add(name[..(i + 1)]);
        return prefixes;
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
        var osty = combat.GetOsty(player);
        if (x.WantsPrefix("osty/"))
        {
            x["osty/present"] = osty != null ? 1 : 0;
            x["osty/hittable"] = combat.IsOstyHittable(simulator, player) ? 1 : 0;
            x["osty/max-hp"] = combat.GetOstyMaxHp(simulator, player);
            if (osty != null)
            {
                var pet = simulator.State.GetCreature(osty);
                x["osty/hp"] = pet.CurrentHp;
                x["osty/block"] = pet.Block;
            }
        }
        foreach (var relic in combat.RelicsOf(player))
        {
            string name = "relic/" + relic.Id.Entry;
            if (!x.WantsPrefix(name + "/")) continue;
            x[name + "/present"] = 1;
            x[name + "/melted"] = relic.IsMelted ? 1 : 0;
            if (x.WantsPrefix(name + "/state/"))
            {
                var key = combat.CaptureRelicObservation(simulator, relic);
                x[$"{name}/state/{key.First:X16}{key.Second:X16}"] = 1;
            }
            if (x.Wants(name + "/counter") && RelicCounterCatalog.Identify(relic) != null)
                x[name + "/counter"] = combat.ReadRelicCounter(simulator, relic);
        }
        if (x.WantsPrefix("relic/POCKETWATCH/")
            && combat.TryGetPocketwatchState(player, out int current, out int previous, out int threshold))
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
            if (!x.WantsPrefix(pile + "/")) continue;
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
                    if (x.Wants(id + "/energy") || x.Wants(type + "/energy"))
                    {
                        int energy = card.GetEnergyCostWithModifiers(simulator, state);
                        Add(id + "/energy", energy); Add(type + "/energy", energy);
                    }
                    if (x.Wants(id + "/stars") || x.Wants(type + "/stars"))
                    {
                        int stars = card.GetStarCostWithModifiers(simulator, state);
                        Add(id + "/stars", stars); Add(type + "/stars", stars);
                    }
                }
                Add(type + "/x-cost", preview.EnergyCost.CostsX ? 1 : 0);
                Add(type + "/exhaust-next", preview.ExhaustOnNextPlay ? 1 : 0);
                Add(type + "/retain", preview.ShouldRetainThisTurn ? 1 : 0);
                if (x.WantsPrefix(type + "/keyword/"))
                    foreach (var keyword in preview.Keywords) Add(type + "/keyword/" + keyword, 1);
                if (x.WantsPrefix(type + "/var/") || x.WantsPrefix(id + "/var/"))
                    foreach (var (name, value) in preview.DynamicVars)
                        if (SemanticStateFieldPolicy.IsSemantic(preview, name, value))
                        {
                            Add(type + "/var/" + name, (double)value.BaseValue);
                            Add(id + "/var/" + name, (double)value.BaseValue);
                        }
                if (preview.Enchantment is { } enchantment)
                    Add(id + "/enchantment/" + enchantment.Id.Entry, enchantment.Amount);
                if (preview.Affliction is { } affliction)
                    Add(id + "/affliction/" + affliction.Id.Entry, affliction.Amount);
                // Draw order is semantically meaningful; preserve the next hand's prefix.
                if (zone == 1 && index < 10) Add($"{pile}/position/{index}/{preview.Id.Entry}", 1);
            }
        }
        if (x.WantsPrefix("power/")) foreach (var power in combat.EffectivePowers())
        {
            // Align enemy powers with the same roster index as enemy bodies;
            // a pet's powers are not enemy resources or arbitrary combat IDs.
            string owner = ReferenceEquals(power.Owner, player.Creature) ? "player"
                : osty != null && ReferenceEquals(power.Owner, osty) ? "osty"
                : "other/" + power.Owner?.CombatId;
            for (int index = 0; index < combat.KnownEnemies.Count; index++)
                if (ReferenceEquals(power.Owner, combat.KnownEnemies[index]))
                {
                    owner = "enemy/" + index;
                    if (x.WantsPrefix(EnemyPowerTotalPrefix))
                        Add(EnemyPowerTotalPrefix + power.Id.Entry, power.Amount);
                    break;
                }
            Add("power/" + owner + "/" + power.Id.Entry, power.Amount);
        }
        for (int index = 0; index < combat.KnownEnemies.Count; index++)
        {
            string name = "enemy/" + index;
            if (!x.WantsPrefix(name + "/")) continue;
            var enemy = combat.KnownEnemies[index];
            var predicted = simulator.State.GetCreature(enemy);
            x[name + "/hp"] = combat.EffectiveEnemyHp(enemy, predicted);
            x[name + "/block"] = predicted.Block;
            x[name + "/present"] = combat.ContainsCreature(enemy) ? 1 : 0;
            x[name + "/max-hp"] = predicted.MaxHp;
            if (enemy.Monster != null) x[name + "/identity/" + enemy.Monster.Id.Entry] = 1;
            if (!combat.ContainsCreature(enemy) || !predicted.IsAlive) continue;
            x[name + "/skip-next"] = combat.WillSkipNextMove(enemy) ? 1 : 0;
            if (enemy.Monster != null && (x.WantsPrefix(name + "/move/")
                || x.Wants(name + "/attack-hits") || x.Wants(name + "/attack-damage")))
            {
                ForecastMove move = combat.CurrentMonsterMove(enemy);
                x[name + "/move/" + move.Move.Id] = 1;
                Add(name + "/attack-hits", move.AttackHits.Count);
                foreach (ForecastAttackHit hit in move.AttackHits)
                    Add(name + "/attack-damage", combat.AdjustMonsterMoveDamage(enemy, move.Move.Id, hit.BaseDamage));
            }
        }
        x["orb/capacity"] = state.OrbQueue.Capacity;
        for (int index = 0; index < state.OrbQueue.Orbs.Count; index++)
        {
            var orb = state.OrbQueue.Orbs[index];
            string name = $"orb/{index}/{orb.Id.Entry}";
            if (!x.WantsPrefix(name + "/")) continue;
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
        private readonly IReadOnlySet<string>? _prefixes;
        internal FeatureWriter(Dictionary<string, double> sparse) => _sparse = sparse;
        internal FeatureWriter(IReadOnlyDictionary<string, int> columns, double[] values, IReadOnlySet<string>? prefixes)
        { _columns = columns; _values = values; _prefixes = prefixes; }
        internal bool Wants(string name) => _columns == null || _columns.ContainsKey(name);
        internal bool WantsPrefix(string prefix) => _prefixes == null || _prefixes.Contains(prefix);
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
