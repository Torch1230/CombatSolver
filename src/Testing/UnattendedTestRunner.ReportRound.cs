using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertHistoryCourseEmptyTurnAsync(CombatState combat, Player player)
    {
        int turn = player.PlayerCombatState!.TurnNumber;
        if (!FindActualHandCard(player, "STRIKE_SILENT", 0).TryManualPlay(combat.Enemies.Single()))
            throw new InvalidOperationException("History Course setup strike failed.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CombatManager.Instance.OnEndedTurnLocally();
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
        {
            EnsureWithinDeadline();
            await NextFrameAsync();
        }
        await AssertReportRoundAsync(combat, player);
    }

    private async Task AssertReportRoundAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies.Single();
        if (_request.ScenarioId == "REPORT-ROUND-DOOM-THRESHOLD-CARD")
        {
            if (enemy.CurrentHp != 134
                || enemy.GetPower<MegaCrit.Sts2.Core.Models.Powers.DoomPower>()?.Amount != 34)
                throw new InvalidOperationException("Report Doom threshold requires exactly 134 HP and 34 Doom.");
        }
        if (_request.ScenarioId is "REPORT-ROUND-ROOT-DEAD" or "REPORT-ROUND-SECOND-FORM-CARD")
        {
            await CreatureCmd.Kill(enemy);
            if (_request.ScenarioId == "REPORT-ROUND-SECOND-FORM-CARD")
            {
                int firstTurn = player.PlayerCombatState!.TurnNumber;
                var doomCard = player.PlayerCombatState.Hand.Cards.Single();
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, firstTurn));
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= firstTurn)
                {
                    EnsureWithinDeadline();
                    await NextFrameAsync();
                }
                await CreatureCmd.SetCurrentHp(enemy, 30);
                foreach (var drawn in player.PlayerCombatState.Hand.Cards.ToArray())
                    await CardPileCmd.Add(drawn, PileType.Discard);
                await CardPileCmd.Add(doomCard, PileType.Hand);
            }
        }
        int turn = player.PlayerCombatState!.TurnNumber;
        var root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        bool anyCardFirst = _request.ScenarioId.EndsWith("-CARD", StringComparison.Ordinal);
        bool strikeFirst = anyCardFirst || _request.ScenarioId.EndsWith("-STRIKE", StringComparison.Ordinal);
        var strikeCard = strikeFirst ? player.PlayerCombatState.Hand.Cards.First(card => anyCardFirst || card.Type == CardType.Attack) : null;
        var cardTarget = strikeCard?.TargetType == TargetType.AnyEnemy ? enemy : null;
        PlanAction end = new(PlanActionKind.EndTurn, turn);
        PlanAction[] actions = strikeFirst
            ? [new PlanAction(PlanActionKind.PlayCard, turn, CardId: strikeCard!.Id.Entry, TargetCombatId: cardTarget?.CombatId), end]
            : [end];
        var next = InvokeForcedTerminalReplay(driver, actions, null, 0, null);
        try
        {
            var expected = CaptureSimulated(next.Simulator, (SimulatedCombatState)next.Simulator.State.CombatState, player, enemy);
            if (strikeFirst)
            {
                var parent = InvokeForcedTerminalReplay(driver, [actions[0]], null, 0, null);
                try
                {
                    var incremental = InvokeForcedTerminalReplay(driver, [end], parent, turn, null);
                    try
                    {
                        AssertSnapshotEqual(expected, CaptureSimulated(incremental.Simulator,
                            (SimulatedCombatState)incremental.Simulator.State.CombatState, player, enemy),
                            _request.ScenarioId, "IncrementalAfterDeath");
                    }
                    finally { incremental.ReleaseSimulator(); }
                }
                finally { parent.ReleaseSimulator(); }
            }
            var fork = next.Simulator.Fork();
            AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy), _request.ScenarioId, "Fork");
            if (strikeFirst)
            {
                if (!strikeCard!.TryManualPlay(cardTarget))
                    throw new InvalidOperationException("Report round native strike failed.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
            {
                EnsureWithinDeadline();
                if (!CombatManager.Instance.IsInProgress)
                    throw new InvalidOperationException("Report round fixture ended combat.");
                await NextFrameAsync();
            }
            AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), _request.ScenarioId, "NextTurn");
            if (next.Simulator.State.GetPlayerCombatState(player).Phase != player.PlayerCombatState.Phase)
                throw new InvalidOperationException("Predicted and native player phases differ.");
            if (_request.ScenarioId == "REPORT-ROUND-NOSTALGIA-STRIKE")
            {
                var nextCard = player.PlayerCombatState.Hand.Cards.First(card => card.Type == CardType.Attack);
                await PlayHistorySensitiveFixtureCardAsync(next.Simulator,
                    (SimulatedCombatState)next.Simulator.State.CombatState, combat, player, enemy, nextCard,
                    "NostalgiaNextTurn");
                string liveStamp = ContinuationStamp.CaptureLive(combat).StateText;
                RestoreReplayTurnCardHistory(combat, player, liveStamp);
                string[] stampFields = liveStamp.Split(';');
                int historyIndex = Array.FindIndex(stampFields, field => field.StartsWith("Y=", StringComparison.Ordinal));
                string[] history = stampFields[historyIndex].Split('/');
                history[^1] = (int.Parse(history[^1]) + 1).ToString();
                stampFields[historyIndex] = string.Join('/', history);
                bool rejected = false;
                try { RestoreReplayTurnCardHistory(combat, player, string.Join(';', stampFields)); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected)
                    throw new InvalidOperationException("Replay accepted a different attack/skill history count.");
            }
        }
        finally
        {
            next.ReleaseSimulator();
        }
    }
}
