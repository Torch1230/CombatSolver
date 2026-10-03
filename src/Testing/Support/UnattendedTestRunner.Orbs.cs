using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task RunOrbDifferentialAsync(
        CombatState combatState,
        Player player,
        UnattendedOrbCheck check)
    {
        Creature enemy = ResolveEnemyByIndex(combatState, check.TargetIndex);
        player.PlayerCombatState!.OrbQueue.Clear();
        OrbModel canonical = ResolveOrbForTest(check.OrbId);
        await OrbCmd.Channel(new BlockingPlayerChoiceContext(), canonical.ToMutable(), player);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        OrbModel actualOrb = player.PlayerCombatState.OrbQueue.Orbs.Single();
        MoveStateSnapshot beforePassive = CaptureActual(combatState, player, enemy);
        SimulatedCombatState simulatedCombat = new(combatState);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        OrbModel simulatedOrb = simulator.State.GetPlayerCombatState(player).OrbQueue.Orbs.Single();

        simulator.OrbPassive(simulatedOrb);
        MoveStateSnapshot predictedPassive = CaptureSimulated(simulator, simulatedCombat, player, enemy);
        await OrbCmd.Passive(new BlockingPlayerChoiceContext(), actualOrb, target: null);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        MoveStateSnapshot actualPassive = CaptureActual(combatState, player, enemy);
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] ORB_DIFF run_id={_request.RunId} orb={actualOrb.Id.Entry} hook=Passive " +
            $"before={Serialize(beforePassive)} predicted={Serialize(predictedPassive)} actual={Serialize(actualPassive)}");
        AssertSnapshotEqual(predictedPassive, actualPassive, "Orb", $"{actualOrb.Id.Entry}.Passive");

        simulator.OrbEvokeNext(player);
        MoveStateSnapshot predictedEvoke = CaptureSimulated(simulator, simulatedCombat, player, enemy);
        await OrbCmd.EvokeNext(new BlockingPlayerChoiceContext(), player);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        MoveStateSnapshot actualEvoke = CaptureActual(combatState, player, enemy);
        Entry.Logger.Info(
            $"[CombatSolver/Unattended] ORB_DIFF run_id={_request.RunId} orb={actualOrb.Id.Entry} hook=Evoke " +
            $"before={Serialize(actualPassive)} predicted={Serialize(predictedEvoke)} actual={Serialize(actualEvoke)}");
        AssertSnapshotEqual(predictedEvoke, actualEvoke, "Orb", $"{actualOrb.Id.Entry}.Evoke");
    }

    private static OrbModel ResolveOrbForTest(string input)
    {
        (Type Type, ModelId Id)[] matches = typeof(OrbModel).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(OrbModel).IsAssignableFrom(type))
            .Select(type => (Type: type, Id: ModelDb.GetId(type)))
            .Where(pair => pair.Type.Name.Equals(input, StringComparison.OrdinalIgnoreCase)
                || pair.Type.FullName?.Equals(input, StringComparison.OrdinalIgnoreCase) == true
                || pair.Id.Entry.Equals(input, StringComparison.OrdinalIgnoreCase)
                || pair.Id.ToString().Equals(input, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException($"找不到充能球 {input}。");
        if (matches.Length > 1)
            throw new InvalidOperationException($"充能球 {input} 不唯一，请使用完整模型 ID。");
        return ModelDb.GetByIdOrNull<OrbModel>(matches[0].Id)
            ?? throw new InvalidOperationException($"游戏模型库未返回测试充能球 {matches[0].Id} 的规范实例。");
    }
}
