using System.Text.Json;
using CombatSolver.Engine.InCombat.Mirrors.Cards;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertPlayerDeathPowersAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BURN", Pile = "Hand" });
        await CreatureCmd.SetCurrentHp(player.Creature, 1);
        await SetBlockAsync(player.Creature, 0);
        await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), player.Creature, 3, player.Creature, null);
        await PowerCmd.Apply<DexterityPower>(new BlockingPlayerChoiceContext(), player.Creature, 4, player.Creature, null);
        await PowerCmd.Apply<ArtifactPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var burn = player.PlayerCombatState!.Hand.Cards.Single();
        var enemy = combat.Enemies[0];
        var captured = CombatRootSnapshot.Capture(combat);
        var root = captured.ForkSimulator();
        var prediction = root.Fork();
        var shadow = (SimulatedCombatState)prediction.State.CombatState;
        MoveStateSnapshot expected;
        using (SimulationNotificationIsolation.Enter())
        {
            CardOnTurnEndInHandMirrors.Invoke(prediction, prediction.State.FindCard(burn)!);
            expected = CaptureSimulated(prediction, shadow, player, enemy);
            int entries = prediction.History.Entries.Count;
            if (prediction.Damage(enemy, 3m, ValueProp.Unpowered, player.Creature).Count != 0
                || prediction.Damage([enemy], 3m, ValueProp.Unpowered, player.Creature).Count != 0
                || prediction.History.Entries.Count != entries)
                throw new InvalidOperationException("A dead shadow dealer produced damage while its live identity was alive.");
            AssertSnapshotEqual(CaptureActual(combat, player, enemy),
                CaptureSimulated(root, (SimulatedCombatState)root.State.CombatState, player, enemy), "PlayerDeathPowers", "RootUnchanged");
        }
        await burn.OnTurnEndInHandWrapper(new BlockingPlayerChoiceContext());
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var actual = CaptureActual(combat, player, enemy);
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "player-death-powers.json"), JsonSerializer.Serialize(new
            {
                predicted = new { expected.PlayerHp, expected.PlayerPowers }, actual = new { actual.PlayerHp, actual.PlayerPowers },
                predictedPendingLoss = prediction.IsAboutToLose, nativePendingLoss = CombatManager.Instance.IsAboutToLose
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        AssertSnapshotEqual(expected, actual, "PlayerDeathPowers", "NativeBurnDeath");
        if (!prediction.IsAboutToLose || !CombatManager.Instance.IsAboutToLose || player.Creature.CurrentHp != 0)
            throw new InvalidOperationException("Player death did not remain pending until its safe terminal boundary.");
        using (SimulationNotificationIsolation.Enter())
        {
            var sibling = root.Fork();
            CardOnTurnEndInHandMirrors.Invoke(sibling, sibling.State.FindCard(burn)!);
            AssertSnapshotEqual(expected, CaptureSimulated(sibling, (SimulatedCombatState)sibling.State.CombatState, player, enemy),
                "PlayerDeathPowers", "ReplayAfterNativeDeath");
            var singleTarget = root.Fork();
            singleTarget.Damage(player.Creature, 2m, ValueProp.Unpowered | ValueProp.Move, player.Creature);
            AssertSnapshotEqual(expected, CaptureSimulated(singleTarget, (SimulatedCombatState)singleTarget.State.CombatState, player, enemy),
                "PlayerDeathPowers", "SingleTargetAfterNativeDeath");
            if (!prediction.CheckWinCondition(1) || prediction.TerminalStamp?.Outcome != CombatSolver.Engine.InCombat.Simulation.CombatTerminalOutcome.Defeat)
                throw new InvalidOperationException("Player death failed to lock defeat at its safe boundary.");
        }
        _completedChecks.Add("PlayerDeathPowers:BurnNativeDeath:ThreePowersRemoved:FullState:PendingLossThenDefeat:BothDamageOverloads:LiveAliveShadowDeadAndInverse:RootAndSiblingAfterNativeDeath");
    }
}
