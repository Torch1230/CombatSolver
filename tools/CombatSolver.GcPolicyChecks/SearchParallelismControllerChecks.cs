namespace CombatSolver;

internal static class SearchParallelismControllerChecks
{
    public static void Run()
    {
        PolicyCheck.Run("adaptive parallelism is opt-in and DOP1 is inert", () =>
        {
            SearchParallelismController disabled = new(4);
            SearchParallelismController serial = new(1, enabled: true);
            for (int index = 0; index < 8; index++)
            {
                disabled.Observe(Sample(4, 1_000, gcDuty: 0.8));
                serial.Observe(Sample(1, 1_000, gcDuty: 0.8));
            }
            PolicyCheck.Require(!disabled.IsEnabled && disabled.Capacity == 4 && !serial.IsEnabled
                && serial.Capacity == 1, "Default and serial configurations must never probe capacity.");
        });
        PolicyCheck.Run("persistent GC pressure probes fewer lanes and keeps a throughput win", () =>
        {
            SearchParallelismController controller = LowerProbe();
            SearchParallelismDecision first = controller.Observe(Sample(2, 1_300, gcDuty: 0.05));
            PolicyCheck.Require(first.Kind == SearchParallelismDecisionKind.None && controller.IsProbing,
                "One fast window is insufficient to accept the reduction.");
            SearchParallelismDecision second = controller.Observe(Sample(2, 1_300, gcDuty: 0.05));
            PolicyCheck.Require(second.Kind == SearchParallelismDecisionKind.ProbeAccepted
                && second.ThroughputRatio > 1.2 && controller.Capacity == 2 && !controller.IsProbing,
                "Comparable work with higher throughput should retain the smaller capacity.");
        });
        PolicyCheck.Run("a harmful lower-DOP probe restores the preceding capacity", () =>
        {
            SearchParallelismController controller = LowerProbe();
            controller.Observe(Sample(2, 600, gcDuty: 0.02));
            SearchParallelismDecision decision = controller.Observe(Sample(2, 600, gcDuty: 0.02));
            PolicyCheck.Require(decision.Kind == SearchParallelismDecisionKind.ProbeRejected
                && decision.ThroughputRatio < 0.7 && controller.Capacity == 4,
                "Less GC alone must not excuse a large throughput regression.");
            for (int index = 0; index < 4; index++)
                controller.Observe(Sample(4, 1_000, gcDuty: 0.4));
            PolicyCheck.Require(controller.Capacity == 4 && !controller.IsProbing,
                "A rejected reduction needs a cooldown before it can repeat.");
        });
        PolicyCheck.Run("stable throughput and substantial GC relief can retain fewer lanes", () =>
        {
            SearchParallelismController controller = LowerProbe();
            controller.Observe(Sample(2, 990, gcDuty: 0.10));
            SearchParallelismDecision decision = controller.Observe(Sample(2, 990, gcDuty: 0.10));
            PolicyCheck.Require(decision.Kind == SearchParallelismDecisionKind.ProbeAccepted
                && controller.Capacity == 2, "Noise-sized throughput difference plus real GC relief can be useful.");
        });
        PolicyCheck.Run("continued pressure can probe 4 to 2 to 1", () =>
        {
            SearchParallelismController controller = LowerProbe();
            controller.Observe(Sample(2, 1_300, gcDuty: 0.3));
            controller.Observe(Sample(2, 1_300, gcDuty: 0.3));
            controller.Observe(Sample(2, 1_300, gcDuty: 0.3));
            SearchParallelismDecision decision = controller.Observe(Sample(2, 1_300, gcDuty: 0.3));
            PolicyCheck.Require(decision.Kind == SearchParallelismDecisionKind.ProbeLower
                && controller.Capacity == 1 && controller.IsProbing,
                "A retained reduction must not prevent a later single-lane probe under continued pressure.");
        });
        PolicyCheck.Run("low pressure permits recovery only after a measured upper-DOP win", () =>
        {
            SearchParallelismController controller = KeptLowerCapacity();
            for (int index = 0; index < 5; index++)
                controller.Observe(Sample(2, 1_300, gcDuty: 0.02));
            PolicyCheck.Require(controller.Capacity == 2, "Recovery must respect its cooldown.");
            SearchParallelismDecision started = controller.Observe(Sample(2, 1_300, gcDuty: 0.02));
            PolicyCheck.Require(started.Kind == SearchParallelismDecisionKind.ProbeHigher
                && controller.Capacity == 4, "A relaxed stable window should permit a bounded upper probe.");
            controller.Observe(Sample(4, 1_600, gcDuty: 0.05));
            SearchParallelismDecision kept = controller.Observe(Sample(4, 1_600, gcDuty: 0.05));
            PolicyCheck.Require(kept.Kind == SearchParallelismDecisionKind.ProbeAccepted
                && controller.Capacity == 4 && !controller.IsProbing,
                "Higher capacity must demonstrate throughput improvement with controlled GC pressure.");
        });
        PolicyCheck.Run("an upper-DOP probe with no throughput gain is reverted and backed off", () =>
        {
            SearchParallelismController controller = KeptLowerCapacity();
            for (int index = 0; index < 6; index++)
                controller.Observe(Sample(2, 1_300, gcDuty: 0.02));
            controller.Observe(Sample(4, 1_300, gcDuty: 0.02));
            SearchParallelismDecision rejected = controller.Observe(Sample(4, 1_300, gcDuty: 0.02));
            PolicyCheck.Require(rejected.Kind == SearchParallelismDecisionKind.ProbeRejected
                && controller.Capacity == 2, "Using more lanes without more throughput is not a successful probe.");
            for (int index = 0; index < 6; index++)
                controller.Observe(Sample(2, 1_300, gcDuty: 0.02));
            PolicyCheck.Require(controller.Capacity == 2, "Failed upper probes need a longer retry interval.");
        });
        PolicyCheck.Run("an upper-DOP throughput win with excessive GC is rejected", () =>
        {
            SearchParallelismController controller = KeptLowerCapacity();
            for (int index = 0; index < 6; index++)
                controller.Observe(Sample(2, 1_300, gcDuty: 0.02));
            controller.Observe(Sample(4, 1_800, gcDuty: 0.35));
            SearchParallelismDecision decision = controller.Observe(Sample(4, 1_800, gcDuty: 0.35));
            PolicyCheck.Require(decision.Kind == SearchParallelismDecisionKind.ProbeRejected
                && controller.Capacity == 2, "Recovery must not accept high GC duty merely for throughput.");
        });
        PolicyCheck.Run("allocation-complexity change cannot masquerade as a DOP benefit", () =>
        {
            SearchParallelismController controller = LowerProbe();
            SearchParallelismDecision decision = controller.Observe(
                Sample(2, 2_000, gcDuty: 0.01, bytesPerTransition: 100));
            PolicyCheck.Require(decision.Kind == SearchParallelismDecisionKind.WorkloadChanged
                && controller.Capacity == 4 && !controller.IsProbing,
                "A much cheaper workload does not prove that the lower DOP caused the speedup.");
        });
        PolicyCheck.Run("isolated pressure spikes do not oscillate capacity", () =>
        {
            SearchParallelismController controller = new(4, enabled: true);
            for (int index = 0; index < 12; index++)
                controller.Observe(Sample(4, 1_000, gcDuty: index % 2 == 0 ? 0.26 : 0.24));
            PolicyCheck.Require(controller.Capacity == 4 && !controller.IsProbing,
                "Pressure must persist across a window streak before starting a lower probe.");
        });
        PolicyCheck.Run("narrow frontiers do not train a fully occupied capacity", () =>
        {
            SearchParallelismController controller = new(4, enabled: true);
            for (int index = 0; index < 8; index++)
                controller.Observe(Sample(1, 200, gcDuty: 0.7));
            PolicyCheck.Require(controller.Capacity == 4 && !controller.IsProbing,
                "Actual DOP1 waves must not be used as evidence about DOP4 throughput.");
        });
        PolicyCheck.Run("short and zero-work samples cannot trigger an unsupported decision", () =>
        {
            SearchParallelismController controller = new(4, enabled: true);
            controller.Observe(Sample(4, 0, gcDuty: 0.8));
            controller.Observe(Sample(4, 1_000, gcDuty: 0.8) with { Elapsed = TimeSpan.Zero });
            for (int index = 0; index < 4; index++)
                controller.Observe(Sample(4, 10, gcDuty: 0.8) with { Elapsed = TimeSpan.FromMilliseconds(5) });
            PolicyCheck.Require(controller.Capacity == 4 && !controller.IsProbing,
                "Insufficient duration and work must remain buffered or ignored.");
        });
        PolicyCheck.Run("critical memory can retain a safety cap without claiming a throughput win", () =>
        {
            SearchParallelismController controller = LowerProbe();
            controller.Observe(Sample(2, 600, gcDuty: 0.01, memoryPressure: 0.99));
            SearchParallelismDecision decision = controller.Observe(
                Sample(2, 600, gcDuty: 0.01, memoryPressure: 0.99));
            PolicyCheck.Require(decision.Kind == SearchParallelismDecisionKind.MemoryPressureLimited
                && controller.Capacity == 2 && decision.ThroughputRatio < 0.7,
                "A pressure safety cap must be distinguishable from a measured performance improvement.");
        });
        PolicyCheck.Run("canceling a probe prevents further capacity adaptation", () =>
        {
            SearchParallelismController controller = LowerProbe();
            SearchParallelismDecision canceled = controller.Observe(Sample(2, 0, 0) with { Cancelled = true });
            int capacity = controller.Capacity;
            for (int index = 0; index < 8; index++)
                controller.Observe(Sample(capacity, 2_000, gcDuty: 0));
            PolicyCheck.Require(canceled.Kind == SearchParallelismDecisionKind.Cancelled
                && !controller.IsEnabled && !controller.IsProbing && controller.Capacity == capacity,
                "A canceled request cannot keep learning from late worker completions.");
        });
        PolicyCheck.Run("capacity and sample validation enforce the configured ceiling", () =>
        {
            PolicyCheck.Throws<ArgumentOutOfRangeException>(() => new SearchParallelismController(0));
            SearchParallelismController controller = new(3, enabled: true);
            PolicyCheck.Throws<ArgumentOutOfRangeException>(() => controller.Observe(Sample(4, 1_000, 0)));
            PolicyCheck.Throws<ArgumentOutOfRangeException>(() => controller.Observe(
                Sample(3, 1_000, 0) with { AllocatedBytes = -1 }));
            PolicyCheck.Throws<ArgumentOutOfRangeException>(() => controller.Observe(
                Sample(3, 1_000, 0) with { MemoryPressureRatio = double.NaN }));
        });
    }

    private static SearchParallelismController LowerProbe()
    {
        SearchParallelismController controller = new(4, enabled: true);
        controller.Observe(Sample(4, 1_000, gcDuty: 0.4));
        SearchParallelismDecision decision = controller.Observe(Sample(4, 1_000, gcDuty: 0.4));
        PolicyCheck.Require(decision.Kind == SearchParallelismDecisionKind.ProbeLower
            && controller.Capacity == 2, "Sustained pressure should begin a two-lane probe.");
        return controller;
    }

    private static SearchParallelismController KeptLowerCapacity()
    {
        SearchParallelismController controller = LowerProbe();
        controller.Observe(Sample(2, 1_300, gcDuty: 0.02));
        controller.Observe(Sample(2, 1_300, gcDuty: 0.02));
        PolicyCheck.Require(controller.Capacity == 2 && !controller.IsProbing, "Expected an accepted lower capacity.");
        return controller;
    }

    private static SearchParallelismSample Sample(
        int dop,
        long transitions,
        double gcDuty,
        long bytesPerTransition = 1_024,
        double memoryPressure = 0.5)
        => new(TimeSpan.FromMilliseconds(200), transitions, transitions * bytesPerTransition,
            TimeSpan.FromMilliseconds(200 * gcDuty), dop, memoryPressure);
}
