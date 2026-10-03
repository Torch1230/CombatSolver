using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Runs;
using System.Reflection;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertMurderRootHistoryAsync(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator parent = root.ForkSimulator();
        var card = parent.State.GetPlayerCombatState(player).Hand.Cards.Single(c => c.Preview.Id.Entry == "MURDER");
        decimal initial = MurderValue(parent, card);
        CombatPredictionSimulator fork = parent.Fork();
        var forkCard = fork.State.GetPlayerCombatState(player).Hand.Cards.Single(c => c.Preview.Id.Entry == "MURDER");
        fork.Draw(player, 1);
        decimal afterDraw = MurderValue(fork, forkCard);
        if (afterDraw != initial + 1 || MurderValue(parent, card) != initial)
            throw new InvalidOperationException("MURDER 的分支抽牌没有保持父分支隔离。");
        var enemy = combat.Enemies.Single();
        var expected = CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy);
        await CardPileCmd.Draw(new BlockingPlayerChoiceContext(), 1, player, fromHandDraw: false);
        AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), "MurderRootHistory", "NativeDraw");
        decimal parentAfterLive = MurderValue(parent, card);
        decimal forkAfterLive = MurderValue(fork, forkCard);
        if (parentAfterLive != initial || forkAfterLive != afterDraw)
            throw new InvalidOperationException($"MURDER root history changed after live draw: parent={initial}->{parentAfterLive}, fork={afterDraw}->{forkAfterLive}.");
        var shadow = (SimulatedCombatState)fork.State.CombatState;
        await PlayHistorySensitiveFixtureCardAsync(fork, shadow, combat, player, enemy,
            FindActualHandCard(player, "MURDER", 0), "MurderAfterDraw");
        int turn = player.PlayerCombatState!.TurnNumber;
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var advanceRound = typeof(CombatBeamSolver).GetMethod("AdvanceRound", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(CombatBeamSolver), "AdvanceRound");
        var boundary = (SearchBoundaryReason)advanceRound.Invoke(driver,
            [fork, shadow, 0, new HashSet<uint>(), 0, null])!;
        if (boundary != SearchBoundaryReason.None || shadow.HasPendingChoice)
            throw new InvalidOperationException($"MURDER 跨回合出现边界 {boundary}。");
        var nextExpected = CaptureSimulated(fork, shadow, player, enemy);
        decimal nextValue = MurderValue(fork, forkCard);
        CombatManager.Instance.OnEndedTurnLocally();
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
        {
            EnsureWithinDeadline();
            if (!CombatManager.Instance.IsInProgress)
                throw new InvalidOperationException("MURDER 夹具意外结束战斗。");
            await NextFrameAsync();
        }
        AssertSnapshotEqual(nextExpected, CaptureActual(combat, player, enemy), "MurderRootHistory", "NextTurn");
        if (MurderValue(fork, forkCard) != nextValue || MurderValue(parent, card) != initial)
            throw new InvalidOperationException("实机跨回合抽牌改变了已冻结的 MURDER 分支历史。");
    }

    private static decimal MurderValue(CombatPredictionSimulator simulator, PredictedCard card)
    {
        if (!CalculatedVarSpecRegistry.TryCalculate((CalculatedVar)card.Preview.DynamicVars.CalculatedDamage,
                simulator, card, null, out decimal value))
            throw new InvalidOperationException("MURDER 夹具没有命中计算变量语义。");
        return value;
    }
}
