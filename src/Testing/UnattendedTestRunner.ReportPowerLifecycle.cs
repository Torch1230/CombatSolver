using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertSummonedAllyPowerOrderAsync(CombatState combat, Player player)
    {
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
        { PowerId = "STRENGTH_POWER", Target = "Enemy", Amount = 2 });
        CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
        shadow.SummonOsty(simulator, player, 5);
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
        MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, combat.Enemies[0]);
        CombatPredictionSimulator fork = simulator.Fork();
        AssertSnapshotEqual(expected, CaptureSimulated(fork,
            (SimulatedCombatState)fork.State.CombatState, player, combat.Enemies[0]),
            _request.ScenarioId, "Fork");
        await OstyCmd.Summon(new BlockingPlayerChoiceContext(), player, 5, null);
        AssertSnapshotEqual(expected, CaptureActual(combat, player, combat.Enemies[0]),
            _request.ScenarioId, "Native");
    }

    private async Task AssertReportPowerLifecycleAsync(CombatState combat, Player player)
    {
        bool reapplication = _request.ScenarioId == "REMOVED-POWER-REAPPLICATION";
        if (!reapplication)
        {
            await AssertHistorySensitivePowerOrderAsync(combat, player);
            return;
        }

        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
        {
            PowerId = "DRAW_CARDS_NEXT_TURN_POWER",
            Target = "Player", Amount = 3
        });
        player.Creature.GetPower<DrawCardsNextTurnPower>()!.AmountOnTurnStart = 5;
        CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
        shadow.SetAmount<DrawCardsNextTurnPower>(player.Creature, 0);
        shadow.Apply<DrawCardsNextTurnPower>(player.Creature, 3, player.Creature);
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
        var enemy = combat.Enemies[0];
        MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, enemy);
        CombatPredictionSimulator initialFork = simulator.Fork();
        SimulatedCombatState initialForkState = (SimulatedCombatState)initialFork.State.CombatState;
        AssertSnapshotEqual(expected, CaptureSimulated(initialFork, initialForkState, player, enemy),
            _request.ScenarioId, "Fork");
        await PowerCmd.Remove(player.Creature.GetPower<DrawCardsNextTurnPower>()!);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
        { PowerId = "DRAW_CARDS_NEXT_TURN_POWER", Target = "Player", Amount = 3 });
        AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), _request.ScenarioId, "Native");
    }

    private async Task AssertHistorySensitivePowerOrderAsync(CombatState combat, Player player)
    {
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
        {
            PowerId = "ORBIT_POWER", Target = "Player", Amount = 3
        });
        CardModel[] cards =
        [
            (await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = "DEFEND_IRONCLAD", Pile = "Hand" })).Single(),
            (await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = "SHIV", Pile = "Hand" })).Single(),
            (await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = "DEFEND_IRONCLAD", Pile = "Hand" })).Single(),
            (await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = "SHIV", Pile = "Hand" })).Single(),
        ];
        SetEnergy(player, 10);

        CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
        Creature enemy = combat.Enemies[0];

        for (int index = 0; index < 2; index++)
            await PlayHistorySensitiveFixtureCardAsync(
                simulator, shadow, combat, player, enemy, cards[index], $"HistoryPrimer{index}");

        var acquisitionBefore = CaptureWriteDensity(simulator, player, enemy);
        shadow.Apply<PhantomBladesPower>(player.Creature, 9);
        shadow.Apply<StrengthPower>(player.Creature, 2);
        shadow.AddPowerInstance<OrbitPower>(player.Creature, 1);
        shadow.Apply<DexterityPower>(player.Creature, 1);
        shadow.Apply<LethalityPower>(player.Creature, 25);
        shadow.Apply<UnmovablePower>(player.Creature, 1);
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
        shadow.NormalizeCardAfflictions(simulator);
        foreach (var (id, amount) in new[] { ("PHANTOM_BLADES_POWER", 9), ("STRENGTH_POWER", 2),
                     ("ORBIT_POWER", 1), ("DEXTERITY_POWER", 1), ("LETHALITY_POWER", 25),
                     ("UNMOVABLE_POWER", 1) })
            await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = id, Target = "Player", Amount = amount });

        MoveStateSnapshot acquired = CaptureSimulated(simulator, shadow, player, enemy);
        AssertSnapshotEqual(acquired, CaptureActual(combat, player, enemy),
            _request.ScenarioId, "HistorySensitiveAcquisition");
        RecordWriteDensity("PowerAcquisition", acquisitionBefore, simulator, player, enemy);
        CombatPredictionSimulator initialFork = simulator.Fork();
        AssertSnapshotEqual(acquired, CaptureSimulated(initialFork,
                (SimulatedCombatState)initialFork.State.CombatState, player, enemy),
            _request.ScenarioId, "Fork");

        for (int index = 2; index < cards.Length; index++)
            await PlayHistorySensitiveFixtureCardAsync(
                simulator, shadow, combat, player, enemy, cards[index], $"HistorySensitiveCard{index}");

        shadow.Apply<WeakPower>(player.Creature, 1, enemy);
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = "WEAK_POWER", Target = "Player", Amount = 1 });
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy),
            CaptureActual(combat, player, enemy),
            _request.ScenarioId, "HistorySensitiveModifierOrder");

        MoveStateSnapshot historyExpected = CaptureSimulated(simulator, shadow, player, enemy);
        CombatPredictionSimulator reapplicationFork = simulator.Fork();
        SimulatedCombatState reapplicationState =
            (SimulatedCombatState)reapplicationFork.State.CombatState;
        var reapplicationBefore = CaptureWriteDensity(reapplicationFork, player, enemy);
        reapplicationState.SetAmount<StrengthPower>(player.Creature, 0);
        reapplicationState.Apply<StrengthPower>(player.Creature, 4);
        PowerLifecycleSupport.ResolvePowerAmountChanges(reapplicationFork, reapplicationState);
        MoveStateSnapshot reacquired = CaptureSimulated(
            reapplicationFork, reapplicationState, player, enemy);
        AssertSnapshotEqual(historyExpected, CaptureSimulated(simulator, shadow, player, enemy),
            _request.ScenarioId, "ParentIsolation");
        await PowerCmd.Remove(player.Creature.GetPower<StrengthPower>()!);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = "STRENGTH_POWER", Target = "Player", Amount = 4 });
        AssertSnapshotEqual(reacquired, CaptureActual(combat, player, enemy),
            _request.ScenarioId, "Reacquisition");
        RecordWriteDensity("PowerReacquisition", reapplicationBefore, reapplicationFork, player, enemy);
    }

    private async Task PlayHistorySensitiveFixtureCardAsync(
        CombatPredictionSimulator simulator,
        SimulatedCombatState shadow,
        CombatState combat,
        Player player,
        Creature enemy,
        CardModel liveCard,
        string step)
    {
        PredictedCard predictedCard = simulator.State.FindCard(liveCard)
            ?? throw new InvalidOperationException($"Missing predicted {liveCard.Id.Entry} card.");
        Creature? target = liveCard.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyEnemy
            ? enemy
            : null;
        var before = CaptureWriteDensity(simulator, player, enemy);
        PlaySimulatedCard(simulator, shadow, predictedCard, target, [enemy]);
        if (!liveCard.TryManualPlay(target))
            throw new InvalidOperationException($"Native fixture action was refused: {liveCard.Id.Entry}");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        AssertSnapshotEqual(
            CaptureSimulated(simulator, shadow, player, enemy),
            CaptureActual(combat, player, enemy),
            _request.ScenarioId,
            $"{step}:{liveCard.Id.Entry}");
        RecordWriteDensity($"{step}:{liveCard.Id.Entry}", before, simulator, player, enemy);
    }
}
