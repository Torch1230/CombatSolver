using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertDampenReactiveCardAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        SetEnergy(player, 3);
        bool generation = _request.ScenarioId.EndsWith("ROCKET-PUNCH", StringComparison.Ordinal);
        string cardId = generation ? "ROCKET_PUNCH" : "MELANCHOLY";
        await InjectCardAsync(combat, player, new() { CardId = cardId, Pile = "Hand", UpgradeLevels = 1 });
        if (!generation) await InjectCardAsync(combat, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        var caster = combat.Enemies.Single(e => e.Monster!.Id.Entry == "MAGI_KNIGHT");
        await CreatureCmd.SetCurrentHp(caster, 1);
        var dampen = await PowerCmd.Apply<DampenPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, caster, null)
            ?? throw new InvalidOperationException("Native Dampen was not applied.");
        dampen.AddCaster(caster);
        if (generation) await PowerCmd.Apply<SmokestackPower>(new BlockingPlayerChoiceContext(), player.Creature, 3, player.Creature, null);
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        if (generation)
        {
            simulator.CreateAndAddGeneratedCardsToCombat<Wound>(player, PileType.Hand, 1, player);
            await CardPileCmd.AddGeneratedCardToCombat(combat.CreateCard<Wound>(player), PileType.Hand, player);
        }
        else
        {
            PlaySimulatedCard(simulator, shadow, FindSimulatedHandCard(simulator, player, "STRIKE_IRONCLAD", 0), caster, combat.Enemies.ToArray());
            if (!FindActualHandCard(player, "STRIKE_IRONCLAD", 0).TryManualPlay(caster))
                throw new InvalidOperationException("Native reactive-card strike failed.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        var other = combat.Enemies.First(e => !ReferenceEquals(e, caster));
        var expected = CaptureSimulated(simulator, shadow, player, other);
        AssertSnapshotEqual(expected, CaptureActual(combat, player, other), cardId, "CasterDeathReactiveHook");
        var fork = simulator.Fork();
        AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, other), cardId, "Fork");
        _completedChecks.Add($"Dampen:{cardId}:NativeNestedHook:OrderedCostLayers:Fork");
    }

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
