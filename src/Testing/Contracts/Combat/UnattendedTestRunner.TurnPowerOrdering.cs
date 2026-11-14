using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertTurnPowerOrderingAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies[0];
        foreach (bool reversed in new[] { false, true })
        {
            await ClearOrderedEffectFixtureAsync(combat, player);
            foreach (string id in reversed
                         ? new[] { "PREP_TIME_POWER", "HIDDEN_ROOTS_POWER" }
                         : new[] { "HIDDEN_ROOTS_POWER", "PREP_TIME_POWER" })
                await InjectPowerAsync(combat, player, new() { PowerId = id, Amount = 2, Target = "Player" });
            var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            var shadow = (SimulatedCombatState)simulator.State.CombatState;
            if (!TurnStartPowerSupport.TriggerAfterSideTurnStart(simulator, shadow, CombatSide.Player, [player.Creature]))
                throw new InvalidOperationException("Turn-start power order suspended.");
            await Hook.AfterSideTurnStart(combat, CombatSide.Player, [player.Creature]);
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy),
                CaptureActual(combat, player, enemy), _request.ScenarioId, $"PowerOrder:{reversed}");
        }
        _completedChecks.Add("TurnPowerOrder:PrepTime:HiddenRoots:BothOrders:ExactNativeState");

        await ClearOrderedEffectFixtureAsync(combat, player);
        foreach (string id in new[] { "SADNESS", "DEFEND_IRONCLAD", "SADNESS", "STRIKE_IRONCLAD", "SADNESS" })
            await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Hand" });
        await InjectPowerAsync(combat, player, new() { PowerId = "ECSTASY_POWER", Amount = 2, Target = "Player" });
        var ecstasy = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var ecstasyShadow = (SimulatedCombatState)ecstasy.State.CombatState;
        if (TurnStartPowerSupport.TriggerAfterPlayerTurnStart(ecstasy, ecstasyShadow, player, new TurnStartChoiceCursor(null)))
            throw new InvalidOperationException("Ecstasy suspended.");
        await player.Creature.GetPower<EcstasyPower>()!.AfterPlayerTurnStart(new BlockingPlayerChoiceContext(), player);
        AssertSnapshotEqual(CaptureSimulated(ecstasy, ecstasyShadow, player, enemy),
            CaptureActual(combat, player, enemy), _request.ScenarioId, "EcstasyBatchTransform");
        _completedChecks.Add("Ecstasy:BatchTransform:OrderedHand:FullRng:ExactNativeState");

        await ClearOrderedEffectFixtureAsync(combat, player);
        await CreatureCmd.SetCurrentHp(enemy, 5);
        foreach (string id in new[] { "PLATING_POWER", "DOOM_POWER", "REGEN_POWER" })
            await InjectPowerAsync(combat, player, new() { PowerId = id, Amount = id == "DOOM_POWER" ? 5 : 2, Target = "Enemy" });
        var ending = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var endingShadow = (SimulatedCombatState)ending.State.CombatState;
        if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(ending, endingShadow, [enemy]))
            throw new InvalidOperationException("Enemy early effects suspended.");
        await Hook.BeforeSideTurnEnd(combat, CombatSide.Enemy, [enemy]);
        await Hook.AfterSideTurnEnd(combat, CombatSide.Enemy, [enemy]);
        AssertSnapshotEqual(CaptureSimulated(ending, endingShadow, player, enemy),
            CaptureActual(combat, player, enemy), _request.ScenarioId, "RegenBeforeDoom");
        if (enemy.CurrentHp != 7 || enemy.Block != 2)
            throw new InvalidOperationException("Early block/heal fixture failed to exercise the Doom threshold.");
        _completedChecks.Add("EnemyTurnEnd:Plating:RegenBeforeDoom:ExactNativeState");
    }

    private async Task AssertBeatDownDynamicTargetAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await InjectPowerAsync(combat, player, new() { PowerId = "SEEKING_EDGE_POWER", Amount = 1, Target = "Player" });
        await InjectCardAsync(combat, player, new() { CardId = "SOVEREIGN_BLADE", Pile = "Discard" });
        await InjectCardAsync(combat, player, new() { CardId = "BEAT_DOWN", Pile = "Hand" });
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        await PlayHistorySensitiveFixtureCardAsync(simulator, (SimulatedCombatState)simulator.State.CombatState,
            combat, player, combat.Enemies[0], FindActualHandCard(player, "BEAT_DOWN", 0), "BeatDownSeekingEdge");
        foreach (var enemy in combat.Enemies)
            AssertSnapshotEqual(CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState, player, enemy),
                CaptureActual(combat, player, enemy), _request.ScenarioId, "AllTargetsAndRng");
        _completedChecks.Add("BeatDown:SeekingEdge:DynamicTarget:AllEnemies:FullRng:ExactNativeState");
    }

    private async Task AssertDoomRetainedBlockAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies[0];
        await CreatureCmd.SetCurrentHp(enemy, 5);
        await SetBlockAsync(enemy, 0);
        if (enemy.GetPower<ReattachPower>() == null)
            throw new InvalidOperationException("Retained block fixture requires a Decimillipede segment.");
        await PowerCmd.Apply<PlatingPower>(new BlockingPlayerChoiceContext(), enemy, 2, enemy, null);
        await PowerCmd.Apply<DoomPower>(new BlockingPlayerChoiceContext(), enemy, 5, player.Creature, null);
        int plating = enemy.GetPower<PlatingPower>()!.Amount;
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        if (!CorePowerSupport.TriggerEnemySideTurnEndEffects(simulator, shadow, [enemy]))
            throw new InvalidOperationException("Retained block fixture suspended.");
        if (!CorePowerSupport.ApplyEnemyDeathPowers(simulator, shadow, shadow.KnownEnemies, new HashSet<uint>()))
            throw new InvalidOperationException("Retained block death cleanup suspended.");
        await Hook.BeforeSideTurnEnd(combat, CombatSide.Enemy, [enemy]);
        await Hook.AfterSideTurnEnd(combat, CombatSide.Enemy, [enemy]);
        foreach (var creature in combat.Enemies)
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, creature),
                CaptureActual(combat, player, creature), _request.ScenarioId, "DoomRetainedBlock");
        if (!enemy.IsDead || enemy.Block != plating)
            throw new InvalidOperationException("Dead retained segment did not keep its early Plating block.");
        _completedChecks.Add("Doom:RetainedSegment:EarlyPlatingBlock:ExactNativeState");
    }

    private async Task AssertTerminalPowerPrefixAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 1);
        await InjectCardAsync(combat, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(combat, player, new() { CardId = "INFLAME", Pile = "Hand" });
        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        if (driver.BuildPowerActionsAfterPrefix([]).Count != 1)
            throw new InvalidOperationException("Power probe fixture needs an ordinary playable power.");
        PlanAction[] prefix = [new(PlanActionKind.PlayCard, root.StartTurnNumber,
            CardId: "STRIKE_IRONCLAD", TargetCombatId: combat.Enemies.Single().CombatId),
            new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: "INFLAME"),
            new(PlanActionKind.EndTurn, root.StartTurnNumber)];
        if (driver.BuildPowerActionsAfterPrefix(prefix).Count != 0)
            throw new InvalidOperationException("Power probe continued past terminal victory.");
        _completedChecks.Add("PowerPrefix:OrdinaryPowerAvailable:EarlyVictoryStopsSuffix");
    }
}
