using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;

namespace OfflineSearchHarness;

internal static class OutcomeContextChecks
{
    // Explicit harness-only contract: never called by the player search or trainer.
    internal static void Run(CombatRootSnapshot root, string output)
    {
        int checks = 0;
        var player = root.PlayerIdentity;
        var parent = root.ForkSimulator();
        var before = SearchOutcomeContext.Capture(parent, player);
        var liveCombat = player.Creature.CombatState!;
        Check(before["resource/current-max-energy"] == Math.Max(0, (int)Hook.ModifyMaxEnergy(liveCombat, player, player.MaxEnergy))
            && before["resource/current-hand-draw"] == Math.Max(0, (int)Hook.ModifyHandDraw(liveCombat, player,
                CombatManager.baseHandDrawCount, out _)), "root resource observations match native rule queries");
        var resources = parent.Fork();
        var resourceCombat = (SimulatedCombatState)resources.State.CombatState;
        resourceCombat.Apply<DemesnePower>(player.Creature, 2);
        resourceCombat.AddEnergyNextTurn(player, 3);
        resourceCombat.AddDrawNextTurn(player, 4);
        var resourceStamp = ContinuationStamp.CapturePredicted(player, resources, root.StartTurnNumber, root.Forecast, root.StartTurnNumber);
        var capacity = SearchOutcomeContext.Capture(resources, player);
        Check(capacity["resource/current-max-energy"] == before["resource/current-max-energy"] + 2
            && capacity["resource/current-hand-draw"] == before["resource/current-hand-draw"] + 2,
            "branch power effects are exposed as modified rule quantities");
        Check(resourceStamp == ContinuationStamp.CapturePredicted(player, resources, root.StartTurnNumber, root.Forecast, root.StartTurnNumber),
            "resource observation preserves full branch continuation state");
        Check(resourceCombat.GetAmount<EnergyNextTurnPower>(player.Creature) == 3
            && resourceCombat.ConsumeDrawNextTurn(player) == 4, "observing does not consume delayed resources");
        Check(Equal(before, SearchOutcomeContext.Capture(parent, player)), "resource modifiers stay in the child branch");
        Dictionary<string, int> resourceColumns = new() { ["resource/current-max-energy"] = 0, ["resource/current-hand-draw"] = 1 };
        double[] resourceValues = [double.NaN, double.NaN];
        SearchOutcomeContext.CaptureSelected(resources, player, resourceColumns, resourceValues);
        Check(resourceColumns.All(p => resourceValues[p.Value] == capacity[p.Key]),
            "resource-only selected projection matches full observations");
        var fork = parent.Fork();
        Check(Equal(before, SearchOutcomeContext.Capture(fork, player)), "fork starts equal");
        PenNib live = player.Relics.OfType<PenNib>().Single();
        PropertyInfo counter = typeof(PenNib).GetProperty(nameof(PenNib.AttacksPlayed))!;
        int saved = live.AttacksPlayed;
        try
        {
            counter.SetValue(live, (saved + 1) % 10);
            Check(Equal(before, SearchOutcomeContext.Capture(parent, player)), "later live changes are isolated");
        }
        finally { counter.SetValue(live, saved); }
        var branch = (SimulatedCombatState)fork.State.CombatState;
        var relic = branch.RelicsOf(player).OfType<PenNib>().Single();
        fork.StateStore.Get(relic, static r => new PenNibPredictionState(r)).AttacksPlayed = (saved + 1) % 10;
        var changed = SearchOutcomeContext.Capture(fork, player);
        Check(changed["relic/PEN_NIB/counter"] == (saved + 1) % 10, "child counter is observable");
        Check(!Equal(before, changed), "different relic progress is distinguishable");
        Check(Equal(before, SearchOutcomeContext.Capture(parent, player)), "child changes are isolated");
        var card = fork.State.GetPlayerCombatState(player).Hand.Cards[0];
        card.SetToFreeThisTurn();
        var reduced = SearchOutcomeContext.Capture(fork, player);
        string cost = "pile/hand/card/" + card.Preview.Id.Entry + "/energy";
        Check(reduced[cost] < changed[cost], "branch cost changes are observable");
        Check(Equal(before, SearchOutcomeContext.Capture(parent, player)), "child card cost is isolated");
        var columns = before.Keys.Append("unknown/absent").Select((name, index) => (name, index))
            .ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
        double[] numeric = Enumerable.Repeat(double.NaN, columns.Count).ToArray();
        SearchOutcomeContext.CaptureSelected(fork, player, columns, numeric);
        Check(columns.All(p => numeric[p.Value] == reduced.GetValueOrDefault(p.Key)), "selected features match sparse projection");
        Array.Fill(numeric, double.NaN);
        SearchOutcomeContext.CaptureSelected(parent, player, columns, numeric);
        Check(columns.All(p => numeric[p.Value] == before.GetValueOrDefault(p.Key)), "reused feature scratch is cleared");
        branch.StunNextMove(branch.KnownEnemies[0]);
        var stunned = SearchOutcomeContext.Capture(fork, player);
        Check(stunned["enemy/0/skip-next"] == 1, "branch intent suppression is observable");
        Check(Equal(before, SearchOutcomeContext.Capture(parent, player)), "branch intent observation stays isolated");
        if (player.Character.Id.Entry == "NECROBINDER")
        {
            var petFork = parent.Fork();
            var petCombat = (SimulatedCombatState)petFork.State.CombatState;
            petCombat.SummonOsty(petFork, player, 7);
            var pet = petCombat.GetOsty(player)!;
            var petBody = petFork.State.GetCreature(pet);
            petBody.GainBlock(3);
            petCombat.Apply<StrengthPower>(pet, 3);
            petCombat.Apply<StrengthPower>(petCombat.KnownEnemies[0], 4);
            var summoned = SearchOutcomeContext.Capture(petFork, player);
            Check(summoned["osty/hp"] > before.GetValueOrDefault("osty/hp"), "pet summon changes observed HP");
            Check(summoned["osty/max-hp"] > before.GetValueOrDefault("osty/max-hp"), "pet branch maximum is observed");
            Check(summoned["osty/block"] == 3 && summoned["osty/hittable"] == 1,
                "pet block and hittability are observed");
            string strength = petCombat.EffectivePowers().First(p => p is StrengthPower).Id.Entry;
            Check(summoned["power/osty/" + strength] == 3, "pet powers use pet ownership");
            Check(summoned["power/enemy/0/" + strength] == 4, "enemy powers align with body roster index");
            Check(summoned[SearchOutcomeContext.EnemyPowerTotalPrefix + strength] == 4,
                "enemy totals exclude the pet's identical power");
            string totalName = SearchOutcomeContext.EnemyPowerTotalPrefix + strength;
            Dictionary<string, int> totalColumn = new() { [totalName] = 0 };
            double[] totalValue = [double.NaN];
            SearchOutcomeContext.CaptureSelected(petFork, player, totalColumn, totalValue);
            Check(totalValue[0] == 4, "aggregate-only inference does not require a positional power column");
            Check(Equal(before, SearchOutcomeContext.Capture(parent, player)), "pet mutations do not change parent");
            var names = summoned.Keys.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
            int column = 0;
            foreach (string name in names.Keys.ToArray()) names[name] = column++;
            double[] values = new double[column];
            SearchOutcomeContext.CaptureSelected(petFork, player, names, values);
            Check(names.All(p => values[p.Value] == summoned[p.Key]), "pet selected features match sparse capture");
            petBody.CurrentHp = 0;
            var deadPet = SearchOutcomeContext.Capture(petFork, player);
            Check(deadPet["osty/hittable"] == 0 && deadPet["osty/hp"] == 0
                && deadPet["osty/max-hp"] == summoned["osty/max-hp"], "dead pet preserves maximum but cannot absorb hits");
        }
        string[] allNames = before.Keys.Union(stunned.Keys).Append("unknown/absent").ToArray();
        for (int offset = 0; offset < 3; offset++)
        {
            var selected = allNames.Where((_, i) => i % 3 == offset).Select((name, index) => (name, index))
                .ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
            double[] projected = new double[selected.Count];
            var plan = new SearchOutcomeContext.Selection(selected);
            // The plan owns names/indices only. Reuse across a changed child and
            // its parent, with dirty scratch, to catch state or value retention.
            SearchOutcomeContext.CaptureSelected(fork, player, plan, projected);
            Check(selected.All(p => projected[p.Value] == stunned.GetValueOrDefault(p.Key)),
                "pruned feature program matches full context partition " + offset);
            Array.Fill(projected, double.NaN);
            SearchOutcomeContext.CaptureSelected(parent, player, plan, projected);
            Check(selected.All(p => projected[p.Value] == before.GetValueOrDefault(p.Key)),
                "compiled projection reuse preserves parent and clears absent child values " + offset);
        }
        // Literal slashes are legal in external identities/variable names;
        // path scopes must resolve them without interning or collisions.
        var paths = new SearchOutcomeContext.Selection(new Dictionary<string, int>
        {
            ["pile/draw/card/MOD/CARD/var/custom/Δ"] = 0,
            ["pile/draw/card/MOD/CARD/count"] = 1,
            ["pile/draw/card/MOD/CARDINAL/count"] = 2,
        });
        var cardPaths = paths.Root.Find("pile/draw/card")!.Find("MOD/CARD")!;
        Check(cardPaths.Find("var")!.Find("custom/Δ")!.Index == 0
            && cardPaths.Find("count")!.Index == 1
            && cardPaths.Find("missing") == null
            && paths.Root.Find("pile/draw/card/MOD/CARDINAL/count")!.Index == 2,
            "compiled feature paths preserve exact identity and slash-bearing names");
        File.WriteAllText(Path.Combine(output, "outcome-context-checks.json"),
            JsonSerializer.Serialize(new { passed = checks, character = player.Character.Id.Entry, counter = saved }));

        static bool Equal(Dictionary<string, double> left, Dictionary<string, double> right)
            => left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out double value) && p.Value == value);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Outcome context: " + message);
            checks++;
        }
    }
}
