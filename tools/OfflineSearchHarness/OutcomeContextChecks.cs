using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using MegaCrit.Sts2.Core.Models.Relics;

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
