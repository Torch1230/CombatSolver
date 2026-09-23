using CombatSolver;

namespace OfflineSearchHarness;

internal static class SearchTimeBoundaryObserverChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition)
                throw new InvalidOperationException($"time-boundary observer check failed: {name}");
        }

        SearchTimeBoundaryObserver observer = new();
        observer.ObserveMessage("TURN_LAYER_BUDGET reason=time completed_turns=0");
        Check(observer.TimeBoundaryObserved, "local_time_layer_stop");

        observer = new SearchTimeBoundaryObserver();
        observer.ObserveMessage("SMART_POTION_GRADIENT result stop=deadline maximum=2 selected_potions=0");
        Check(observer.TimeBoundaryObserved, "potion_gradient_deadline");
        observer = new SearchTimeBoundaryObserver();
        observer.ObserveMessage("SUPPLEMENTAL_AUDIT_BUDGET exhausted=true elapsed_ms=100 budget_ms=100");
        Check(observer.TimeBoundaryObserved, "supplemental_deadline");
        observer = new SearchTimeBoundaryObserver();
        observer.ObserveMessage("SEARCH_TIME_BUDGET completed_turns=0");
        Check(observer.TimeBoundaryObserved, "solver_time_budget");
        observer = new SearchTimeBoundaryObserver();
        observer.ObserveMessage("NOVELTY_SEARCH_STOP reason=time_limit expanded=100");
        Check(observer.TimeBoundaryObserved, "novelty_time_limit");
        observer = new SearchTimeBoundaryObserver();
        observer.ObserveSelectedResult(SearchBoundaryReason.TimeLimit);
        Check(observer.TimeBoundaryObserved, "selected_result_time_limit");

        observer = new SearchTimeBoundaryObserver();
        observer.ObserveMessage("SMART_POTION_GRADIENT result stop=threshold_met maximum=2 selected_potions=1");
        observer.ObserveMessage("SMART_POTION_GRADIENT result stop=complete maximum=2 selected_potions=0");
        observer.ObserveMessage("SUPPLEMENTAL_AUDIT_BUDGET exhausted=false");
        observer.ObserveMessage("TURN_LAYER_BUDGET reason=nodes completed_turns=0");
        observer.ObserveMessage("NOVELTY_SEARCH_STOP reason=node_limit expanded=100");
        observer.ObserveSelectedResult(SearchBoundaryReason.NodeLimit);
        Check(!observer.TimeBoundaryObserved, "completed_threshold_and_node_limits_are_not_time");

        observer.ObserveMessage("SUPPLEMENTAL_AUDIT_BUDGET exhausted=true elapsed_ms=101 budget_ms=100");
        observer.ObserveMessage("search finished normally");
        Check(observer.TimeBoundaryObserved, "observation_is_monotonic_across_later_logs");

        List<string> info = [];
        int debugCalls = 0;
        SearchPathObserver pathObserver = new(_ => false, _ => { });
        SearchDiagnosticsSink wrapped = new SearchTimeBoundaryObserver().Wrap(
            new SearchDiagnosticsSink(info.Add, _ => debugCalls++, pathObserver));
        wrapped.Info("SEARCH_TIME_BUDGET");
        wrapped.Info("ordinary diagnostic");
        wrapped.Debug("debug diagnostic");
        Check(info.Count == 2 && debugCalls == 1
            && ReferenceEquals(wrapped.PathObserver, pathObserver), "sink_forwarding");

        SearchTimeBoundaryObserver firstRequest = new();
        SearchTimeBoundaryObserver secondRequest = new();
        firstRequest.ObserveMessage("SEARCH_TIME_BUDGET");
        Check(firstRequest.TimeBoundaryObserved && !secondRequest.TimeBoundaryObserved,
            "requests_have_independent_state");

        Console.WriteLine($"TIME_BOUNDARY_OBSERVER_CHECKS Passed checks={checks}");
        return checks;
    }
}
