using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task ClearOrderedEffectFixtureAsync(CombatState combat, Player player)
    {
        await ClearPlayerPilesAsync(player);
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        SetEnergy(player, 10);
    }

    private async Task AssertSeekerOrderedOptionsAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await InjectCardAsync(combat, player, new() { CardId = "SEEKER_STRIKE", Pile = "Hand" });
        foreach (string id in new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD" })
            await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Draw" });
        var root = CombatRootSnapshot.Capture(combat);
        var probe = root.ForkSimulator();
        var spec = CardChoiceSupport.GetSpec(probe, FindSimulatedHandCard(probe, player, "SEEKER_STRIKE", 0))
            ?? throw new InvalidOperationException("Seeker fixture did not generate candidates.");
        if (!spec.Options.SequenceEqual(probe.State.GetPlayerCombatState(player).DrawPile.Cards))
            throw new InvalidOperationException("Seeker candidates differ from the native filtered draw-pile order.");
        var choice = CardChoiceSupport.BuildRequestedChoice(spec, ["STRIKE_IRONCLAD"]);
        var action = new PlanAction(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: "SEEKER_STRIKE",
            TargetCombatId: combat.Enemies[0].CombatId, Choice: choice);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var predicted = InvokeForcedTerminalReplay(driver, [action], null, 0, null);
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            using var session = NativeChoiceRuntime.Begin(combat, player, "test:seeker-ordered-options");
            session.SetPlanAndStartDriving(NGame.Instance!, [choice], deadline.Token);
            var card = FindActualHandCard(player, "SEEKER_STRIKE", 0);
            GameAction queued = await SolverController.EnqueueAndCaptureActionAsync(
                candidate => candidate is PlayCardAction play && ReferenceEquals(play.NetCombatCard.ToCardModelOrNull(), card),
                () => { if (!card.TryManualPlay(combat.Enemies[0])) throw new InvalidOperationException("Native Seeker was refused."); }, deadline.Token);
            await session.AwaitProducerAndCompleteAsync(queued.CompletionTask).WaitAsync(deadline.Token);
            var expected = CaptureSimulated(predicted.Simulator, (SimulatedCombatState)predicted.Simulator.State.CombatState, player, combat.Enemies[0]);
            AssertSnapshotEqual(expected, CaptureActual(combat, player, combat.Enemies[0]), "SeekerStrike", "NativeOrderedInstance");
            var fork = predicted.Simulator.Fork();
            AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, combat.Enemies[0]), "SeekerStrike", "Fork");
        }
        finally { predicted.ReleaseSimulator(); }
        _completedChecks.Add("SeekerStrike:RandomSample:FilteredPileOrder:DuplicateInstances:Native:Fork");
    }

    private async Task AssertEvilEyeExhaustHistoryAsync(CombatState combat, Player player)
    {
        foreach (bool exhausted in new[] { false, true })
        {
            await ClearOrderedEffectFixtureAsync(combat, player);
            await InjectPowerAsync(combat, player, new() { PowerId = "DEXTERITY_POWER", Target = "Player", Amount = 1 });
            await InjectPowerAsync(combat, player, new() { PowerId = "UNMOVABLE_POWER", Target = "Player", Amount = 2 });
            await InjectCardAsync(combat, player, new() { CardId = "EVIL_EYE", Pile = "Hand" });
            if (exhausted)
            {
                await InjectCardAsync(combat, player, new() { CardId = "WOUND", Pile = "Hand" });
                await CardCmd.Exhaust(new BlockingPlayerChoiceContext(), FindActualHandCard(player, "WOUND", 0));
            }
            var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            await PlayHistorySensitiveFixtureCardAsync(simulator, (SimulatedCombatState)simulator.State.CombatState,
                combat, player, combat.Enemies[0], FindActualHandCard(player, "EVIL_EYE", 0), "EvilEye");
        }
        _completedChecks.Add("EvilEye:ExhaustedHistory:SingleAndDoubleBlock:Native");
    }

    private async Task AssertRitualTemporaryStrengthOrderAsync(CombatState combat, Player player)
    {
        foreach (bool ritualFirst in new[] { false, true })
        {
            await ClearOrderedEffectFixtureAsync(combat, player);
            foreach (string id in ritualFirst ? new[] { "RITUAL_POWER", "REPTILE_TRINKET_POWER" }
                : new[] { "REPTILE_TRINKET_POWER", "RITUAL_POWER" })
                await InjectPowerAsync(combat, player, new() { PowerId = id, Target = "Player", Amount = id == "RITUAL_POWER" ? 1 : 3 });
            var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            var shadow = (SimulatedCombatState)simulator.State.CombatState;
            if (!CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects(simulator, shadow, [player.Creature]))
                throw new InvalidOperationException("Ritual fixture suspended.");
            await Hook.AfterSideTurnEnd(combat, CombatSide.Player, [player.Creature]);
            var expected = CaptureSimulated(simulator, shadow, player, combat.Enemies[0]);
            AssertSnapshotEqual(expected, CaptureActual(combat, player, combat.Enemies[0]), "Ritual", $"TemporaryStrengthOrder:{ritualFirst}");
            var fork = simulator.Fork();
            AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, combat.Enemies[0]), "Ritual", "Fork");
        }
        _completedChecks.Add("Ritual:TemporaryStrength:BothListenerOrders:Native:Fork");
    }

    private async Task AssertNoxiousRampartOrderAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await InjectPowerAsync(combat, player, new() { PowerId = "NOXIOUS_FUMES_POWER", Target = "Player", Amount = 3 });
        await InjectPowerAsync(combat, player, new() { PowerId = "SLEIGHT_OF_FLESH_POWER", Target = "Player", Amount = 9 });
        var shield = combat.Enemies.Single(creature => creature.Monster!.Id.Entry == "LIVING_SHIELD");
        await PowerCmd.Apply<RampartPower>(new BlockingPlayerChoiceContext(), shield, 25, shield, null);
        foreach (var enemy in combat.Enemies) await CreatureCmd.LoseBlock(new BlockingPlayerChoiceContext(), enemy, enemy.Block, null);
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        if (!PersistentPowerSupport.TriggerAfterSideTurnStart(simulator, shadow, CombatSide.Player, [player.Creature]))
            throw new InvalidOperationException("Noxious fixture suspended.");
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
        await Hook.AfterSideTurnStart(combat, CombatSide.Player, [player.Creature]);
        var turret = combat.Enemies.Single(creature => creature.Monster!.Id.Entry == "TURRET_OPERATOR");
        var expected = CaptureSimulated(simulator, shadow, player, turret);
        AssertSnapshotEqual(expected, CaptureActual(combat, player, turret), "NoxiousFumes", "SleightOfFleshBeforeRampart");
        var fork = simulator.Fork();
        AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, turret), "NoxiousFumes", "Fork");
        _completedChecks.Add("NoxiousFumes:SleightOfFlesh:PerTargetDamage:BeforeRampart:Native:Fork");
    }

    private async Task AssertBlockSpecCardPlayIdentityAsync(CombatState combat, Player player)
    {
        foreach (string id in new[] { "DEATHS_DOOR", "GLITTERSTREAM" })
        {
            await ClearOrderedEffectFixtureAsync(combat, player);
            int previousBlocks = CombatManager.Instance.History.Entries.OfType<BlockGainedEntry>()
                .Count(entry => entry.HappenedThisTurn(combat) && entry.CardPlay != null
                    && entry.CardPlay.Player == player && entry.Props.IsCardOrMonsterMove());
            await InjectPowerAsync(combat, player, new() { PowerId = "UNMOVABLE_POWER", Target = "Player", Amount = previousBlocks + 1 });
            if (id == "DEATHS_DOOR")
                await PowerCmd.Apply<DoomPower>(new BlockingPlayerChoiceContext(), combat.Enemies[0], 1, player.Creature, null);
            await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Hand" });
            var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            var shadow = (SimulatedCombatState)simulator.State.CombatState;
            await PlayHistorySensitiveFixtureCardAsync(simulator, shadow, combat, player, combat.Enemies[0],
                FindActualHandCard(player, id, 0), "BlockSpecIdentity");
            var fork = simulator.Fork();
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, combat.Enemies[0]),
                CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, combat.Enemies[0]), id, "Fork");
        }
        _completedChecks.Add("BlockSpecIdentity:DeathsDoor:Glitterstream:Unmovable:Native:Fork");
    }
}
