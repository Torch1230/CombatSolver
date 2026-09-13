using MegaCrit.Sts2.Core.Combat;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;
using System.Reflection;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertGhostSeedKeywordLifecycleAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = "GHOST_SEED" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand", UpgradeLevels = 1 });
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var enemy = combat.Enemies.Single();
        shadow.ApplyDampen(simulator, player.Creature, enemy);
        var dampen = await PowerCmd.Apply<DampenPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, enemy, null)
            ?? throw new InvalidOperationException("Native Dampen was not applied.");
        dampen.AddCaster(enemy);
        shadow.NormalizeCardAfflictions(simulator);
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "DowngradeEtherealKeyword");
        var card = FindActualHandCard(player, "DEFEND_IRONCLAD", 0);
        var before = ContinuationStamp.CaptureLive(combat);
        CardCmd.ApplyKeyword(card, CardKeyword.Ethereal);
        if (ContinuationStamp.CaptureLive(combat) == before)
            throw new InvalidOperationException("Continuation ignored a local keyword change.");
        card.RemoveKeyword(CardKeyword.Ethereal);
        if (ContinuationStamp.CaptureLive(combat) != before)
            throw new InvalidOperationException("Restored keyword state did not restore continuation equality.");
        var generated = PredictedCard.Create(CanonicalModels.Card<DefendIronclad>(), player);
        simulator.AddGeneratedCardsToCombat([generated], PileType.Hand, player, resultKind: CardGenerationResultKind.Fixed);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        var clone = simulator.State.FindCard(card)!.CreateClone();
        simulator.AddGeneratedCardsToCombat([clone], PileType.Hand, player, resultKind: CardGenerationResultKind.Fixed);
        await CardPileCmd.AddGeneratedCardToCombat(card.CreateClone(), PileType.Hand, player);
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "NewCardAndCloneEntry");
        int turn = player.PlayerCombatState!.TurnNumber;
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var advance = typeof(CombatBeamSolver).GetMethod("AdvanceRound", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(CombatBeamSolver), "AdvanceRound");
        var boundary = (SearchBoundaryReason)advance.Invoke(driver,
            [simulator, shadow, 0, new HashSet<uint>(), 0, null])!;
        if (boundary != SearchBoundaryReason.None || shadow.HasPendingChoice)
            throw new InvalidOperationException($"Ghost seed fixture reached {boundary}.");
        CombatManager.Instance.OnEndedTurnLocally();
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } state || state.TurnNumber <= turn)
        {
            EnsureWithinDeadline();
            await NextFrameAsync();
        }
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "NextTurnKeywordPersistence");
        _completedChecks.Add("GhostSeed:NativeDowngrade:EntryAndClone:TwoTurns:LocalKeywordContinuation:CompleteStateAndRng");
    }
}
