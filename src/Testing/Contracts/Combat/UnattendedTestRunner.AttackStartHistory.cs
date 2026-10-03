using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using System.Reflection;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertAttackStartHistoryAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        SetEnergy(player, 3);
        foreach (string id in new[] { "THRASH", "STRIKE_IRONCLAD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        await PowerCmd.Apply<LethalityPower>(new BlockingPlayerChoiceContext(), player.Creature, 75, player.Creature, null);
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var card = FindActualHandCard(player, "THRASH", 0);
        var enemy = combat.Enemies.Single();
        PlaySimulatedCard(simulator, shadow, simulator.State.FindCard(card)!, enemy, [enemy]);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), card),
            () => { if (!card.TryManualPlay(enemy)) throw new InvalidOperationException("Native Thrash was refused."); }, deadline.Token);
        await action.CompletionTask.WaitAsync(deadline.Token);
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "FirstAttackThenOtherCardDamageQuery");
        if (card.DynamicVars.Damage.BaseValue != 10)
            throw new InvalidOperationException("Thrash absorbed an already-consumed first-attack multiplier.");
        var parent = root.ForkSimulator();
        var recaptured = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var frozenAfterFirst = (SimulatedCombatState)recaptured.State.CombatState;
        if (shadow.GetAttackPlayStartsThisTurn(player.Creature) != 1
            || frozenAfterFirst.GetAttackPlayStartsThisTurn(player.Creature) != 1
            || ((SimulatedCombatState)parent.State.CombatState).GetAttackPlayStartsThisTurn(player.Creature) != 0)
            throw new InvalidOperationException("Attack-start root capture or Fork isolation failed.");
        simulator.AddToPile(simulator.State.FindCard(card)!, PileType.Hand);
        await CardPileCmd.Add(card, PileType.Hand);
        await PlayHistorySensitiveFixtureCardAsync(simulator, shadow, combat, player, enemy, card, "SecondAttackNoBonus");
        if (shadow.GetAttackPlayStartsThisTurn(player.Creature) != 2
            || frozenAfterFirst.GetAttackPlayStartsThisTurn(player.Creature) != 1)
            throw new InvalidOperationException("Second attack changed the captured parent counter.");
        int turn = player.PlayerCombatState!.TurnNumber;
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var advance = typeof(CombatBeamSolver).GetMethod("AdvanceRound", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(CombatBeamSolver), "AdvanceRound");
        var boundary = (SearchBoundaryReason)advance.Invoke(driver,
            [simulator, shadow, 0, new HashSet<uint>(), 0, null])!;
        if (boundary != SearchBoundaryReason.None || shadow.HasPendingChoice)
            throw new InvalidOperationException($"Attack-start fixture reached {boundary}.");
        CombatManager.Instance.OnEndedTurnLocally();
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } state || state.TurnNumber <= turn)
        {
            EnsureWithinDeadline();
            await NextFrameAsync();
        }
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "NextTurnReset");
        if (shadow.GetAttackPlayStartsThisTurn(player.Creature) != 0
            || frozenAfterFirst.GetAttackPlayStartsThisTurn(player.Creature) != 1)
            throw new InvalidOperationException("New turn did not reset only its own attack-start counter.");
        _completedChecks.Add("AttackStartedHistory:ThrashAbsorption:Lethality:Recapture:Fork:TwoTurns:FullStateAndRng");
    }
}
