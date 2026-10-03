using System.Text.Json;

namespace CombatSolver;

internal sealed partial class RunStatistics
{
    internal static async Task AssertHealthySaturationIsolationAsync(string evidenceDirectory)
    {
        string directory = Path.Combine(evidenceDirectory, "saturated-statistics");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var instance = new RunStatistics();
        var run = new RunStatisticsRecord("saturation-run", "test-profile", 1000, null, "IRONCLAD", 0, "test",
            "full", "pending", true, true, false, [], [], [], []);
        instance._run = run;
        instance._worker = Task.Run(async () => { await gate.Task; await instance.ProcessAsync(directory); });
        try
        {
            for (int i = 0; i < 256; i++) instance.Enqueue(new("execute", run, "battle-" + i, true, true, true));
            if (instance._worker.IsCompleted || instance._signals.Reader.Count != 256)
                throw new InvalidOperationException("Healthy saturation fixture did not fill its paused consumer.");
            instance.Enqueue(new("sync"));
            if (instance._recordingStopped) throw new InvalidOperationException("Coalesced sync stopped business recording.");
            instance.Enqueue(new("execute", run, "rejected-battle", true, true, true));
            if (!instance._recordingStopped || instance._run != null)
                throw new InvalidOperationException("Rejected business event did not explicitly stop recording.");
            instance.SetUploading(true);
            if (instance._uploadEnabled) throw new InvalidOperationException("Stopped statistics re-enabled upload.");
        }
        finally
        {
            instance._signals.Writer.TryComplete();
            gate.SetResult();
            await instance._worker.WaitAsync(TimeSpan.FromSeconds(15));
        }
        instance._Process(0);
        var store = new RunStatisticsStore(directory);
        var saved = store.Find(run.RunId) ?? throw new InvalidOperationException("Accepted events were not persisted.");
        if (saved.ExecutedBattles.Length != 256 || saved.ExecutedBattles.Contains("rejected-battle")
            || saved.ObservedFromStart || saved.Participation != "partial" || instance._snapshot != null)
            throw new InvalidOperationException("Saturation lost accepted events or advertised complete statistics.");
        if (instance._failure is not { Reason: "queue_capacity", RunId: "saturation-run", RejectedSignal: "execute",
                QueuedSignals: 256, IncompleteMarkerSaved: true })
            throw new InvalidOperationException("Saturation did not preserve its explicit failure and persistence status.");
        await AssertDrainFaultObservedAsync(evidenceDirectory, run);
        string native = Path.Combine(directory, "native");
        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, "1.run"), "{\"start_time\":1,\"was_abandoned\":false,\"win\":true,\"run_time\":10}");
        store.Reconcile(run.ProfileId, native);
        if (store.Snapshot(run.ProfileId).Solver.Wins != 0)
            throw new InvalidOperationException("Native recovery promoted incomplete tracking to a full win.");
        Entry.Logger.Info("[CombatSolver/Unattended] RUN_STATISTICS_SATURATION_OK healthy_256 sync_coalesced rejected_explicit accepted_persisted partial_restart upload_disabled");
    }

    private static async Task AssertDrainFaultObservedAsync(string evidenceDirectory, RunStatisticsRecord run)
    {
        string blockedDirectory = Path.Combine(evidenceDirectory, "blocked-statistics-directory");
        File.WriteAllText(blockedDirectory, "not a directory");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var instance = new RunStatistics { _run = run };
        instance._worker = Task.Run(async () => { await gate.Task; await instance.ProcessAsync(blockedDirectory); });
        for (int i = 0; i < 257; i++) instance.Enqueue(new("execute", run, "battle-" + i, true, true, true));
        gate.SetResult();
        try
        {
            await instance._worker.WaitAsync(TimeSpan.FromSeconds(15));
            throw new InvalidOperationException("Blocked store directory unexpectedly loaded.");
        }
        catch (IOException) { }
        instance._Process(0);
        if (instance._failure is not { Reason: "queue_capacity", IncompleteMarkerSaved: false } failure
            || !failure.Error.Contains("Drain failed:") || !failure.Error.Contains(nameof(IOException))
            || instance._signals.Reader.Count != 0)
            throw new InvalidOperationException("Stopped recording hid its subsequent store failure.");
    }

    // Exercise the actual Node producer/consumer boundary without enabling telemetry in a test run.
    internal static async Task AssertWorkerFailureIsolationAsync(string evidenceDirectory)
    {
        string directory = Path.Combine(evidenceDirectory, "corrupt-statistics");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "damaged.run.json");
        byte[] damaged = [0, 0, 0, 0];
        await File.WriteAllBytesAsync(path, damaged);
        using var instance = new RunStatistics();
        instance._run = new("test-run", "test-profile", 1, null, "IRONCLAD", 0, "test",
            "full", "pending", true, true, false, [], [], [], []);
        instance._snapshot = new(1, "test-profile", 1, new(1, 0, 0, 1, 1, 1, 1), null);
        instance._worker = Task.Run(() => instance.ProcessAsync(directory));
        try
        {
            await instance._worker;
            throw new InvalidOperationException("Corrupt statistics unexpectedly loaded.");
        }
        catch (JsonException exception) when (exception.BytePositionInLine == 0)
        {
            // The fixture must hit the historical store-constructor error, not a substitute fault.
        }
        // Producer must notice completion even before the next Godot process callback.
        instance.Enqueue(new("execute", instance._run, "first-post", true, true, true));
        instance._Process(0);
        if (instance._failure is not { Reason: "consumer_completed", IncompleteMarkerSaved: false } failure
            || !failure.Error.Contains(nameof(JsonException)))
            throw new InvalidOperationException("Consumer failure lost its original JSON error.");
        for (int i = 0; i < 300; i++)
            instance.Enqueue(new("execute", instance._run, "battle-" + i, true, true, true));
        if (instance._run != null || instance._snapshot != null || instance._signals.Reader.Count != 0)
            throw new InvalidOperationException("Failed statistics retained a valid snapshot or queued gameplay events.");
        if (!File.ReadAllBytes(path).SequenceEqual(damaged))
            throw new InvalidOperationException("Statistics failure changed the corrupt source evidence.");
        Entry.Logger.Info("[CombatSolver/Unattended] RUN_STATISTICS_WORKER_FAILURE_OK original_json_fault 300_posts snapshot_invalidated evidence_preserved");
    }
}
