using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertExecutionChoiceSearchAsync(CombatState combat, Player player, IReadOnlyList<string> fixtures, bool nativeRound = false)
    {
        foreach (string fixture in fixtures)
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
            await ClearPlayerPilesAsync(player);
            bool setup = fixture != "Round" && fixture != "Cascade";
            if (fixture == "Cascade") await InjectCardAsync(combat, player, new() { CardId = "CASCADE", Pile = "Hand" });
            if (fixture == "Sources")
            {
                foreach (string id in new[] { "INFINITE_BLADES_POWER", "FOREGONE_CONCLUSION_POWER", "STRATAGEM_POWER", "ENTROPY_POWER", "TOOLS_OF_THE_TRADE_POWER", "TYRANNY_POWER" })
                    await InjectPowerAsync(combat, player, new() { PowerId = id, Amount = 1, Target = "Player" });
                foreach (string id in new[] { "TOOLBOX", "CHOICES_PARADOX", "GAMBLING_CHIP", "TOASTY_MITTENS" })
                    await InjectRelicAsync(player, new() { RelicId = id });
            }
            else if (fixture == "Round")
                foreach (string id in new[] { "STRATAGEM_POWER", "ENTROPY_POWER", "TOOLS_OF_THE_TRADE_POWER", "TYRANNY_POWER" })
                    await InjectPowerAsync(combat, player, new() { PowerId = id, Amount = 1, Target = "Player" });
            else if (fixture == "Mayhem")
                await InjectPowerAsync(combat, player, new() { PowerId = "MAYHEM_POWER", Amount = 2, Target = "Player" });
            string[] basic = ["STRIKE_SILENT", "DEFEND_SILENT", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_DEFECT", "DEFEND_DEFECT", "STRIKE_REGENT", "DEFEND_REGENT"];
            string[] draw = fixture switch
            {
                "Mayhem" => [.. basic.Take(5), "PREPARED", "PREPARED", .. basic.Skip(5)],
                "Cascade" => ["PREPARED", "ACROBATICS", "PREPARED", .. basic],
                _ => basic,
            };
            foreach (string id in draw) await InjectCardAsync(combat, player,
                new() { CardId = id, Pile = fixture is "Sources" or "Round" ? "Discard" : "Draw" });
            SetEnergy(player, 3); SetStars(player, 0);
            string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
            var root = CombatRootSnapshot.Capture(combat);
            var names = SolverDisplayNames.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, includeTurnSetup: setup,
                theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
            { FixedBudget = true, VerifyIncrementalSearch = false, MaxDegreeOfParallelism = 1, DetailedDiagnostics = false,
                MeasurePhasePerformance = false, BudgetOverrideMilliseconds = null };
            var evidence = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy, CancellationToken.None,
                searchProfile: policy.Profile with { BeamWidth = 12, MaxExpandedNodes = 80 },
                potionPolicyOverride: SolverPotionPolicy.Disabled).VerifyExecutionChoiceContinuationForTesting(setup, fixture == "Cascade" ? "CASCADE" : null));
            if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
                throw new InvalidOperationException("Search execution continuation changed live combat.");
            if (nativeRound)
            {
                var originalSettings = SolverSettings.Current;
                // The fixture owns this native choice session; disable automatic setup
                // takeover so the production coordinator does not acquire a second owner.
                SolverSettings.ApplyForTesting(originalSettings with { AutomaticCalculationEnabled = false });
                try
                {
                    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
                    using var session = NativeChoiceRuntime.Begin(combat, player, "test:round-execution-continuation");
                    session.SetPlanAndStartDriving(NGame.Instance!, evidence.Action.TurnStartChoices!, deadline.Token);
                    async Task AdvanceNative()
                    {
                        int turn = player.PlayerCombatState!.TurnNumber;
                        CombatManager.Instance.OnEndedTurnLocally();
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                        await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(deadline.Token);
                        while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } state || state.TurnNumber <= turn)
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            await NextFrameAsync();
                        }
                    }
                    try
                    {
                        await session.AwaitProducerAndCompleteAsync(AdvanceNative()).WaitAsync(deadline.Token);
                    }
                    catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
                    {
                        throw new InvalidOperationException($"Native round stalled at turn={player.PlayerCombatState?.TurnNumber} phase={player.PlayerCombatState?.Phase}; setup={PlayerTurnSetupCoordinator.DescribeControlsForTesting()}; choices={string.Join(";", NativeChoiceRuntime.TraceSnapshotForTesting)}", error);
                    }
                    string actual = ContinuationStamp.CaptureLive(combat).StateText;
                    if (actual != evidence.StateText)
                        throw new InvalidOperationException($"Native round continuation differs:\nEXPECTED\n{evidence.StateText}\nACTUAL\n{actual}");
                }
                finally { SolverSettings.ApplyForTesting(originalSettings); }
            }
            _completedChecks.Add($"ExecutionChoiceSearch:{fixture}:branches={evidence.Branches}:recapture:history:shuffle:budget:parent-live-isolation:native={nativeRound}");
        }
    }
}
