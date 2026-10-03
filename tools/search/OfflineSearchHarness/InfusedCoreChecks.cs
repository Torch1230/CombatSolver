using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Relics;

namespace OfflineSearchHarness;

internal static class InfusedCoreChecks
{
    internal static void Run(CombatState combat, string output)
    {
        var owner = combat.Players.Single();
        foreach (var relic in owner.Relics.ToArray())
            owner.RemoveRelicInternal(relic, silent: true);
        owner.AddRelicInternal(ModelDb.Relic<InfusedCore>().ToMutable());

        var root = CombatRootSnapshot.Capture(combat);
        var liveBefore = ContinuationStamp.CaptureLive(combat);
        var parent = root.ForkSimulator();
        var parentCombat = (SimulatedCombatState)parent.State.CombatState;
        var parentQueue = parent.State.GetPlayerCombatState(owner).OrbQueue;
        parentQueue.Clear();
        parentQueue.AddCapacity(3);
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException($"InfusedCore: {name}");
            checks++;
        }
        bool Trigger(CombatPredictionSimulator simulator, bool participating = true)
            => ((SimulatedCombatState)simulator.State.CombatState).TriggerRelicsAfterSideTurnStart(
                simulator, participating ? CombatSide.Player : CombatSide.Enemy,
                participating ? [owner.Creature] : combat.Enemies);

        Check(parentCombat.GetPlayerTurnNumber(owner) == 1, "fixture requires turn one");
        var child = parent.Fork();
        var sibling = parent.Fork();
        Check(Trigger(child), "first-turn hook completes");
        var queue = child.State.GetPlayerCombatState(owner).OrbQueue;
        Check(queue.Orbs.Count == 3, $"first turn must channel three orbs; actual={queue.Orbs.Count}");
        Check(queue.Orbs.All(orb => orb is LightningOrb && ReferenceEquals(orb.Owner, owner)),
            "all three orbs belong to the player");
        Check(queue.Orbs.All(orb => OrbMirrors.GetPassiveValue(child, orb) == 4
                && OrbMirrors.GetEvokeValue(child, orb) == 9), "passive and evoke values remain 4/9");
        Check(child.History.GetCounters(owner).LightningChannels
            == parent.History.GetCounters(owner).LightningChannels + 3, "three channel history entries");
        Check(parentQueue.Orbs.Count == 0 && sibling.State.GetPlayerCombatState(owner).OrbQueue.Orbs.Count == 0,
            "parent and sibling remain empty");

        var nextTurn = parent.Fork();
        ((SimulatedCombatState)nextTurn.State.CombatState).AdvancePlayerTurn(owner);
        Check(Trigger(nextTurn) && nextTurn.State.GetPlayerCombatState(owner).OrbQueue.Orbs.Count == 0,
            "empty queue on turn two does not trigger");
        var channels = child.History.GetCounters(owner).LightningChannels;
        ((SimulatedCombatState)child.State.CombatState).AdvancePlayerTurn(owner);
        Check(Trigger(child) && child.History.GetCounters(owner).LightningChannels == channels,
            "later turn preserves existing orbs without channeling again");
        Check(Trigger(sibling, participating: false)
            && sibling.State.GetPlayerCombatState(owner).OrbQueue.Orbs.Count == 0,
            "non-participating owner does not trigger");

        var grandchild = child.Fork();
        var copiedQueue = grandchild.State.GetPlayerCombatState(owner).OrbQueue;
        Check(copiedQueue.Orbs.Count == 3 && copiedQueue.Orbs.Zip(queue.Orbs)
            .All(pair => !ReferenceEquals(pair.First, pair.Second)), "fork owns distinct orb models");
        copiedQueue.Remove(copiedQueue.Orbs[0]);
        Check(queue.Orbs.Count == 3 && copiedQueue.Orbs.Count == 2, "fork queue mutation is isolated");
        Check(ContinuationStamp.CaptureLive(combat) == liveBefore, "prediction leaves live state unchanged");
        Check(root.ForkSimulator().State.GetPlayerCombatState(owner).OrbQueue.Orbs.Count
            == owner.PlayerCombatState!.OrbQueue.Orbs.Count, "root remains unchanged");

        File.WriteAllText(Path.Combine(output, "infused-core-checks.json"), JsonSerializer.Serialize(new
        {
            status = "Passed",
            scope = "offline_production_hook_diagnostic_not_native_acceptance",
            checks,
            firstTurnOrbs = 3,
            passive = 4,
            evoke = 9,
        }));
        Console.WriteLine($"INFUSED_CORE_CHECKS Passed checks={checks} scope=offline_diagnostic");
    }
}
