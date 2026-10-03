using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCrabRageDeathTimingAsync(CombatState combat, Player player)
    {
        var dead = combat.Enemies.Single(enemy => enemy.Monster is Rocket);
        var survivor = combat.Enemies.Single(enemy => enemy.Monster is Crusher);
        await CreatureCmd.SetCurrentHp(dead, 1);
        await CreatureCmd.SetCurrentHp(survivor, 100);
        await SetBlockAsync(dead, 0);
        await SetBlockAsync(survivor, 0);
        CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        simulator.Damage(dead, 1, ValueProp.Unpowered, player.Creature);
        simulator.Damage(survivor, 20, ValueProp.Unpowered, player.Creature);
        if (!CorePowerSupport.ApplyEnemyDeathPowers(simulator, shadow, shadow.KnownEnemies, new HashSet<uint>()))
            throw new InvalidOperationException("Death fixture unexpectedly requested a choice.");
        await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), dead, 1, ValueProp.Unpowered, player.Creature);
        await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), survivor, 20, ValueProp.Unpowered, player.Creature);
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, survivor), CaptureActual(combat, player, survivor),
            _request.ScenarioId, "DamageAfterCompanionDeath");
        _completedChecks.Add("CrabRage:DeathBeforeNextHit:FullStateAndRng");
    }
}
