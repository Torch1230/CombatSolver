using System.Reflection;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertCardChoiceSearchAsync(MegaCrit.Sts2.Core.Combat.CombatState combat,
        MegaCrit.Sts2.Core.Entities.Players.Player player, bool strictOnly = false, bool expanded = false)
    {
        foreach (var relic in player.Relics.ToArray()) await MegaCrit.Sts2.Core.Commands.RelicCmd.Remove(relic);
        await ClearPlayerPilesAsync(player);
        string[] hand = expanded
            ? ["PREPARED", "THINKING_AHEAD", "GLIMMER", "PHOTON_CUT", "SURVIVOR", "HOLOGRAM", "SEEKER_STRIKE", "ABUNDANCE"]
            : ["PREPARED", "ACROBATICS", "DAGGER_THROW", "SURVIVOR", "DEFEND_SILENT"];
        foreach (string id in hand)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        foreach (string id in new[] { "STRIKE_SILENT", "WOUND", "BACKFLIP", "DEADLY_POISON" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Draw" });
        if (expanded)
            foreach (string id in new[] { "DEFEND_SILENT", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD" })
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Discard" });
        SetEnergy(player, 3);
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        SearchPolicySnapshot capturedPolicy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
        {
            FixedBudget = true, VerifyIncrementalSearch = false, DetailedDiagnostics = false,
            MeasurePhasePerformance = false, BudgetOverrideMilliseconds = null,
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        // Add a nested discard selector to the isolated root to exercise full-replay fallback.
        CombatPredictionSimulator simulator = (CombatPredictionSimulator)typeof(CombatRootSnapshot)
            .GetField("_rootSimulator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
        simulator.State.GetPlayerCombatState(player).Hand.Cards.Single(c => c.Preview.Id.Entry == "SURVIVOR")
            .MutablePreview.GiveSingleTurnSly();
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSearchProfile profile = capturedPolicy.Profile with { BeamWidth = 12, MaxExpandedNodes = 80 };
        if (strictOnly)
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
            SolverResult strict = await Task.Run(() => new CombatBeamSolver(root, names, damage,
                capturedPolicy with { VerifyIncrementalSearch = true, MaxDegreeOfParallelism = 2 },
                deadline.Token, searchProfile: profile, potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
            if (strict.CardChoicePrefixCaptures <= 0 || strict.CardChoicePrefixReuses <= 0
                || strict.CardChoicePrefixFallbacks <= 0
                || ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
                throw new InvalidOperationException("Strict incremental search missed continuation/fallback or changed live state.");
            return;
        }
        await Task.Run(() => new CombatBeamSolver(root, names, damage, capturedPolicy,
            CancellationToken.None, searchProfile: profile,
            potionPolicyOverride: SolverPotionPolicy.Disabled).VerifyCardChoiceContinuationForTesting());
        SolverResult? parallelResult = null;
        foreach (int mode in new[] { 1, 2, 0 })
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
            using CountdownEvent bothReplays = new(2);
            object observationsGate = new();
            StateFingerprint? selectedParent = null;
            bool parentSelected = false;
            int claimed = 0, active = 0, overlaps = 0;
            InvalidOperationException injected = new("Card continuation replay failure probe");
            SearchRequestWorkTotals totals = new();
            SearchPathObserver observer = new(_ => true, observation =>
            {
                if (observation.Stage != SearchPathObservationStage.CardChoiceContinuationReplay)
                    return;
                int ordinal;
                lock (observationsGate)
                {
                    if (!parentSelected)
                    {
                        selectedParent = observation.StateKey;
                        parentSelected = true;
                    }
                    if (observation.StateKey != selectedParent || claimed >= 2) return;
                    ordinal = ++claimed;
                }
                Interlocked.Increment(ref active);
                try
                {
                    bothReplays.Signal();
                    if (!bothReplays.Wait(TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("同父卡牌续执行回放未在两个lane上重叠。");
                    if (ordinal != 2) return;
                    Interlocked.Increment(ref overlaps);
                    if (mode == 1) cancellation.Cancel();
                    if (mode == 2) throw injected;
                }
                finally { Interlocked.Decrement(ref active); }
            });
            SearchPolicySnapshot policy = capturedPolicy with
            {
                MaxDegreeOfParallelism = 2, RequestWorkTotals = totals,
                Diagnostics = new SearchDiagnosticsSink(capturedPolicy.Diagnostics.Info, capturedPolicy.Diagnostics.Debug, observer),
            };
            try
            {
                SolverResult result = await Task.Run(() => new CombatBeamSolver(root, names, damage,
                    policy, cancellation.Token, searchProfile: profile,
                    potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
                if (mode != 0) throw new InvalidOperationException($"卡牌续执行回放的在途失败未传播：mode={mode} claimed={claimed} "
                    + SolverDiagnostics.DescribeResult(result));
                parallelResult = result;
            }
            catch (OperationCanceledException error) when (mode == 1 && error.CancellationToken == cancellation.Token) { }
            catch (InvalidOperationException error) when (mode == 2 && ReferenceEquals(error, injected)) { }
            SearchRequestWorkSnapshot work = totals.Snapshot();
            if (overlaps != 1 || active != 0 || totals.RecordedSolverCountForTesting != 1
                || work.TransitionCount <= 0 || work.WorkerAllocatedBytes <= 0)
                throw new InvalidOperationException("卡牌续执行回放没有证明并发、完整排空及部分工作记录。");
        }
        SolverResult serial = await Task.Run(() => new CombatBeamSolver(root, names, damage,
            capturedPolicy with { MaxDegreeOfParallelism = 1 }, CancellationToken.None,
            searchProfile: profile, potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
        SolverResult legacy = await Task.Run(() => new CombatBeamSolver(root, names, damage,
            capturedPolicy with { MaxDegreeOfParallelism = 1 }, CancellationToken.None,
            searchProfile: profile, potionPolicyOverride: SolverPotionPolicy.Disabled)
            { DisableCardChoiceContinuationsForTesting = true }.Solve());
        AssertEquivalentSearchResults(legacy, serial, "card continuation legacy/serial");
        AssertEquivalentSearchResults(serial, parallelResult!, "card continuation DOP1/DOP2");
        if (parallelResult!.CardChoicePrefixCaptures <= 0 || parallelResult.CardChoicePrefixReuses <= 0
            || serial.CardChoicePrefixFallbacks <= 0 || legacy.CardChoicePrefixCaptures != 0)
            throw new InvalidOperationException("Card continuation fixture did not exercise reuse/fallback/legacy.");
        if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new InvalidOperationException("卡牌续执行回放合同改变了live战斗。");
    }
}
