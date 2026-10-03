using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertDampenDeathTimingAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        SetEnergy(player, 3);
        string cardId = _request.ScenarioId.EndsWith("-SCYTHE", StringComparison.Ordinal) ? "THE_SCYTHE" : "CLAW";
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = cardId, Pile = "Hand", UpgradeLevels = 1 });
        var caster = combat.Enemies.Single(e => e.Monster!.Id.Entry == "MAGI_KNIGHT");
        await CreatureCmd.SetCurrentHp(caster, 1);
        var dampen = await PowerCmd.Apply<DampenPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, caster, null)
            ?? throw new InvalidOperationException("Native Dampen was not applied.");
        dampen.AddCaster(caster);
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var card = FindActualHandCard(player, cardId, 0);
        PlaySimulatedCard(simulator, shadow, simulator.State.FindCard(card)!, caster, combat.Enemies.ToArray());
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), card),
            () => { if (!card.TryManualPlay(caster)) throw new InvalidOperationException("Native Dampen fixture card was refused."); }, deadline.Token);
        await action.CompletionTask.WaitAsync(deadline.Token);
        var other = combat.Enemies.First(e => !ReferenceEquals(e, caster));
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, other), CaptureActual(combat, player, other),
            _request.ScenarioId, "CasterDeathBeforeCardGrowth");
        _completedChecks.Add($"Dampen:NativeCasterDeath:UpgradeBeforeGrowth:{cardId}:FullStateAndRng");
    }
}
