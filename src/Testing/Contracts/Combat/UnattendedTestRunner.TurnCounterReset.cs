using System.Reflection;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertTurnCounterResetFork(CombatRootSnapshot root)
    {
        CombatPredictionSimulator parent = root.ForkSimulator();
        SimulatedCombatState parentState = (SimulatedCombatState)parent.State.CombatState;
        var player = parentState.Players[0];
        AssertRootColorlessGenerationPoolCache(parent, player);
        parentState.BeginSideTurn(player.Creature);
        FieldInfo counterField = typeof(SimulatedCombatState).GetField(
            "_cardsPlayedThisTurn", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var parentCounters = (ForkableDictionary<MegaCrit.Sts2.Core.Entities.Creatures.Creature, int>)
            counterField.GetValue(parentState)!;
        parentCounters[player.Creature] = 7;
        CombatPredictionSimulator child = parent.Fork();
        SimulatedCombatState childState = (SimulatedCombatState)child.State.CombatState;
        childState.BeginSideTurn(player.Creature);
        var childCounters = (ForkableDictionary<MegaCrit.Sts2.Core.Entities.Creatures.Creature, int>)
            counterField.GetValue(childState)!;
        if (parentCounters[player.Creature] != 7 || childCounters[player.Creature] != 0)
            throw new InvalidOperationException("回合计数重置未保持父子隔离或显式零。");
        CombatPredictionSimulator grandchild = child.Fork();
        SimulatedCombatState grandchildState = (SimulatedCombatState)grandchild.State.CombatState;
        var grandchildCounters = (ForkableDictionary<MegaCrit.Sts2.Core.Entities.Creatures.Creature, int>)
            counterField.GetValue(grandchildState)!;
        FieldInfo storage = childCounters.GetType().GetField(
            "_storage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        StateFingerprintBuilder before = new();
        grandchildState.AppendFingerprint(ref before, grandchild);
        grandchildState.BeginSideTurn(player.Creature);
        StateFingerprintBuilder after = new();
        grandchildState.AppendFingerprint(ref after, grandchild);
        if (before.Finish() != after.Finish()
            || !ReferenceEquals(storage.GetValue(childCounters), storage.GetValue(grandchildCounters)))
            throw new InvalidOperationException("已有零计数的回合重置改变状态或复制共享存储。");
        grandchildCounters[player.Creature] = 3;
        if (childCounters[player.Creature] != 0 || parentCounters[player.Creature] != 7)
            throw new InvalidOperationException("省略零写入后后续计数写入破坏了 Fork 隔离。");
    }
}
