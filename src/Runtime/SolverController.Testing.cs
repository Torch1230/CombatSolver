using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal static partial class SolverController
{
    // Unattended validation installs a frozen result at the normal deployment boundary.
    internal static void StartPredictedRouteForTesting(NGame host, CombatState state, SolverResult result, Action<CombatState> onCombatEnd)
    {
        AssertMainThread();
        BeginCombat(state);
        if (CombatReplayRecording.TestCombatEndObserver != null)
            throw new InvalidOperationException("Test deployment cannot replace an active replay observer.");
        CombatReplayRecording.TestCombatEndObserver = onCombatEnd;
        _combat.AutomaticSearchPaused = false;
        _combat.AutomaticSearchPausedTurn = null;
        _combat.LatestResult = result;
        _combat.LatestStamp = LiveCombatStamp.Capture(state);
        _combat.ContinuationSource = result;
        _combat.FullAutoEnabled = true;
        _combat.SearchesStarted = 1;
        _combat.ReplanCounts[ReplanCause.InitialSearch] = 1;
        LastCompletedResultForTesting = result;
        BattleDamageTracker.RegisterPlan(state, result);
        SolverOverlay.ShowResult(host, SolverOverlaySnapshot.Capture(result, unexpectedReplan: false));
        StartFullAutoDeployment(host, state, result);
    }
}
