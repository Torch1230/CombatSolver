using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

// Observations, not utilities. No card/relic bonus or exchange rate is encoded.
// Named sparse columns avoid merging unrelated identities into small hash buckets.
internal static partial class SearchOutcomeContext
{
    internal const string EnemyPowerTotalPrefix = "power/enemies/";

    internal static Dictionary<string, double> Capture(CombatPredictionSimulator simulator, Player player)
    {
        Dictionary<string, double> values = new(StringComparer.Ordinal);
        Capture(new FeatureWriter(values), simulator, player);
        return values;
    }

    internal static void CaptureSelected(CombatPredictionSimulator simulator, Player player,
        IReadOnlyDictionary<string, int> columns, double[] values)
        => CaptureSelected(simulator, player, new Selection(columns), values);

    internal static void CaptureSelected(CombatPredictionSimulator simulator, Player player,
        Selection selection, double[] values)
    {
        if (values.Length < selection.RequiredLength) throw new ArgumentException("Feature buffer is too small.");
        Array.Clear(values);
        Capture(new FeatureWriter(selection, values), simulator, player);
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
        var character = x.Scope("character");
        character[player.Character.Id.Entry] = 1;
        x["player/hp"] = body.CurrentHp;
        x["player/max-hp"] = body.MaxHp;
        x["player/block"] = body.Block;
        x["player/energy"] = state.Energy;
        x["player/stars"] = state.Stars;
        // Derived rule queries at the current branch/turn, not guaranteed future
        // income. Never consume delayed resources or advance the combat to observe.
        if (x.Wants("resource/current-max-energy"))
            x["resource/current-max-energy"] = PersistentPowerSupport.GetModifiedMaxEnergy(combat, player);
        if (x.Wants("resource/current-hand-draw"))
            x["resource/current-hand-draw"] = PersistentPowerSupport.GetModifiedHandDraw(
                combat, player, CombatManager.baseHandDrawCount);
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
        var relics = x.Scope("relic");
        if (relics.Needed) foreach (var relic in combat.RelicsOf(player))
        {
            var observed = relics.Scope(relic.Id.Entry);
            if (!observed.Needed) continue;
            observed["present"] = 1;
            observed["melted"] = relic.IsMelted ? 1 : 0;
            var states = observed.Scope("state");
            if (states.Needed)
            {
                var key = combat.CaptureRelicObservation(simulator, relic);
                states[$"{key.First:X16}{key.Second:X16}"] = 1;
            }
            if (observed.Wants("counter") && RelicCounterCatalog.Identify(relic) != null)
                observed["counter"] = combat.ReadRelicCounter(simulator, relic);
        }
        var pocketwatch = relics.Scope("POCKETWATCH");
        if (pocketwatch.Needed
            && combat.TryGetPocketwatchState(player, out int current, out int previous, out int threshold))
        {
            pocketwatch["current"] = current;
            pocketwatch["previous"] = previous;
            pocketwatch["threshold"] = threshold;
        }
        for (int zone = 0; zone < 4; zone++)
        {
            var pile = x.Scope(zone switch { 0 => "pile/hand", 1 => "pile/draw",
                2 => "pile/discard", _ => "pile/exhaust" });
            if (!pile.Needed) continue;
            var cards = zone switch { 0 => state.Hand.Cards, 1 => state.DrawPile.Cards,
                2 => state.DiscardPile.Cards, _ => state.ExhaustPile.Cards };
            pile["count"] = cards.Count;
            var identities = pile.Scope("card");
            var types = pile.Scope("type");
            var positions = pile.Scope("position");
            for (int index = 0; index < cards.Count; index++)
            {
                var card = cards[index];
                var preview = card.Preview;
                var id = identities.Scope(preview.Id.Entry);
                var type = types.Scope(Enum.GetName(preview.Type) ?? preview.Type.ToString());
                id.Add("count", 1);
                id.Add("upgrades", preview.CurrentUpgradeLevel);
                type.Add("count", 1);
                // Hand costs include branch-owned global and local cost modifiers.
                if (zone == 0)
                {
                    if (id.Wants("energy") || type.Wants("energy"))
                    {
                        int energy = card.GetEnergyCostWithModifiers(simulator, state);
                        id.Add("energy", energy); type.Add("energy", energy);
                    }
                    if (id.Wants("stars") || type.Wants("stars"))
                    {
                        int stars = card.GetStarCostWithModifiers(simulator, state);
                        id.Add("stars", stars); type.Add("stars", stars);
                    }
                }
                type.Add("x-cost", preview.EnergyCost.CostsX ? 1 : 0);
                type.Add("exhaust-next", preview.ExhaustOnNextPlay ? 1 : 0);
                type.Add("retain", preview.ShouldRetainThisTurn ? 1 : 0);
                var keywords = type.Scope("keyword");
                if (keywords.Needed)
                    foreach (var keyword in preview.Keywords)
                        keywords.Add(Enum.GetName(keyword) ?? keyword.ToString(), 1);
                var typeVars = type.Scope("var");
                var idVars = id.Scope("var");
                if (typeVars.Needed || idVars.Needed)
                    foreach (var (name, value) in preview.DynamicVars)
                        if (SemanticStateFieldPolicy.IsSemantic(preview, name, value))
                        {
                            typeVars.Add(name, (double)value.BaseValue);
                            idVars.Add(name, (double)value.BaseValue);
                        }
                if (preview.Enchantment is { } enchantment)
                    id.Scope("enchantment").Add(enchantment.Id.Entry, enchantment.Amount);
                if (preview.Affliction is { } affliction)
                    id.Scope("affliction").Add(affliction.Id.Entry, affliction.Amount);
                // Draw order is semantically meaningful; preserve the next hand's prefix.
                if (zone == 1 && index < 10 && positions.Needed)
                    positions.Scope(index.ToString()).Add(preview.Id.Entry, 1);
            }
        }
        var powers = x.Scope("power");
        var enemyPowers = powers.Scope("enemy");
        var enemyPowerTotals = powers.Scope("enemies");
        if (powers.Needed) foreach (var power in combat.EffectivePowers())
        {
            // Align enemy powers with the same roster index as enemy bodies;
            // a pet's powers are not enemy resources or arbitrary combat IDs.
            int enemyOwner = -1;
            for (int index = 0; index < combat.KnownEnemies.Count; index++)
                if (ReferenceEquals(power.Owner, combat.KnownEnemies[index]))
                {
                    enemyOwner = index;
                    enemyPowerTotals.Add(power.Id.Entry, power.Amount);
                    break;
                }
            var owner = enemyOwner >= 0 ? enemyPowers.Scope(enemyOwner.ToString())
                : ReferenceEquals(power.Owner, player.Creature) ? powers.Scope("player")
                : osty != null && ReferenceEquals(power.Owner, osty) ? powers.Scope("osty")
                : powers.Scope("other").Scope((power.Owner?.CombatId).ToString() ?? string.Empty);
            owner.Add(power.Id.Entry, power.Amount);
        }
        var enemies = x.Scope("enemy");
        if (enemies.Needed) for (int index = 0; index < combat.KnownEnemies.Count; index++)
        {
            var observed = enemies.Scope(index.ToString());
            if (!observed.Needed) continue;
            var enemy = combat.KnownEnemies[index];
            var predicted = simulator.State.GetCreature(enemy);
            observed["hp"] = combat.EffectiveEnemyHp(enemy, predicted);
            observed["block"] = predicted.Block;
            observed["present"] = combat.ContainsCreature(enemy) ? 1 : 0;
            observed["max-hp"] = predicted.MaxHp;
            var identities = observed.Scope("identity");
            if (enemy.Monster != null) identities[enemy.Monster.Id.Entry] = 1;
            if (!combat.ContainsCreature(enemy) || !predicted.IsAlive) continue;
            observed["skip-next"] = combat.WillSkipNextMove(enemy) ? 1 : 0;
            var moves = observed.Scope("move");
            if (enemy.Monster != null && (moves.Needed
                || observed.Wants("attack-hits") || observed.Wants("attack-damage")))
            {
                ForecastMove move = combat.CurrentMonsterMove(enemy);
                moves[move.Move.Id] = 1;
                observed.Add("attack-hits", move.AttackHits.Count);
                if (observed.Wants("attack-damage"))
                    foreach (ForecastAttackHit hit in move.AttackHits)
                        observed.Add("attack-damage", combat.AdjustMonsterMoveDamage(enemy, move.Move.Id, hit.BaseDamage));
            }
        }
        var orbs = x.Scope("orb");
        orbs["capacity"] = state.OrbQueue.Capacity;
        if (orbs.Needed) for (int index = 0; index < state.OrbQueue.Orbs.Count; index++)
        {
            var orb = state.OrbQueue.Orbs[index];
            var observed = orbs.Scope(index.ToString()).Scope(orb.Id.Entry);
            if (!observed.Needed) continue;
            observed["present"] = 1;
            if (observed.Wants("passive"))
                observed["passive"] = (double)OrbMirrors.GetPassiveValue(simulator, orb);
            if (observed.Wants("evoke"))
                observed["evoke"] = (double)OrbMirrors.GetEvokeValue(simulator, orb);
        }
    }
    private readonly struct FeatureWriter
    {
        private readonly Dictionary<string, double>? _sparse;
        private readonly IReadOnlyDictionary<string, int>? _columns;
        private readonly double[]? _values;
        private readonly IReadOnlySet<string>? _prefixes;
        private readonly Selection? _selection;
        internal FeatureWriter(Dictionary<string, double> sparse) => _sparse = sparse;
        internal FeatureWriter(Selection selection, double[] values)
        { _selection = selection; _columns = selection.Columns; _values = values; _prefixes = selection.Prefixes; }
        internal FeatureScope Scope(string path) => _sparse != null
            ? new(_sparse, path + "/") : new(_selection!.Root.Find(path), _values!);
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
    }

}
