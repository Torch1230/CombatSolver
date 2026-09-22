using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

// Diagnostic only: reproduce a UI request for preview A while the worker publishes B.
// Both previews come from the real solver; no synthetic search payload is allocated.
internal sealed class ProgressRetirementProbe(int stopAfterNodes)
{
    public SearchInteractionState Interaction { get; } = new();
    private SolverRouteAdoptionSeed? _previous;
    private WeakReference? _unselected;
    private long _displayTick = Environment.TickCount64;
    private int _requestedVersion;
    private int _unselectedVersion;
    private int _expandedAtRequest;

    public void Observe(SolverProgress progress)
    {
        if (_unselected != null || progress.RouteAdoptionSeed is not { } seed)
            return;
        if (progress.ExpandedNodes >= stopAfterNodes && _previous is { } selected
            && !ReferenceEquals(selected, seed))
        {
            if (!Interaction.RequestAdoptRoute(selected, stopAfterResult: true))
                throw new InvalidOperationException("Probe takeover was rejected.");
            _requestedVersion = selected.CandidateVersion;
            _unselectedVersion = seed.CandidateVersion;
            _expandedAtRequest = progress.ExpandedNodes;
            _unselected = new WeakReference(seed);
            _previous = null;
        }
        else
        {
            _previous = seed;
        }
        Interaction.PublishProgress(progress);
        _displayTick += SolverWeights.ProgressUiIntervalMilliseconds;
        if (!Interaction.TryCreateDisplayProgress(_displayTick, out SolverProgress display))
            throw new InvalidOperationException("Probe display update was rejected.");
        Interaction.RenderedRouteAdoptionSeed = display.RouteAdoptionSeed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool PrepareCompletionPreview()
    {
        bool stopped = _unselected != null;
        if (!stopped)
        {
            // A short search may finish before the stop threshold. Setup search
            // also retires previews on ordinary completion while keeping its result.
            _unselected = _previous == null ? null : new WeakReference(_previous);
            _previous = null;
        }
        if (_unselected == null)
            throw new InvalidOperationException("Search ended without a route preview.");
        return stopped;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SolverResult Finish(SolverResult workerResult, string outputDirectory)
    {
        // Keep the temporary strong reference used to build WeakReference off
        // the measuring stack, including during tier-0 JIT compilation.
        bool stopped = PrepareCompletionPreview();
        SolverResult result = Interaction.FinalizeWorkerResult(workerResult);
        MemorySample before = CaptureMemory();
        bool previewAliveBefore = _unselected!.IsAlive;
        LiveCombatStamp stamp = new("progress-retirement-probe");
        if (stopped)
            Interaction.PreserveStoppedResult(result, stamp);
        else
            Interaction.CompleteTakeover();
        MemorySample after = CaptureMemory();
        bool previewAliveAfter = _unselected.IsAlive;
        bool? resultPreserved = stopped ? ReferenceEquals(result, Interaction.StoppedResult) : null;
        bool? resultResumed = stopped ? ReferenceEquals(result, Interaction.TakeStoppedResult(stamp)) : null;
        File.WriteAllText(Path.Combine(outputDirectory, "progress-retirement.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            mode = stopped ? "stopped-search" : "completed-search",
            metric = "whole-process live managed bytes after progress retirement (forced GC, not peak/RSS)",
            stopAfterNodes,
            expandedAtRequest = _expandedAtRequest,
            requestedVersion = _requestedVersion,
            unselectedVersion = _unselectedVersion,
            previewAliveBefore,
            previewAliveAfter,
            resultPreserved,
            resultResumed,
            result.ResultScope,
            result.TotalExpandedNodes,
            result.TotalTransitionCount,
            before,
            after,
            managedReductionFraction = 1d - after.ManagedBytes / (double)before.ManagedBytes,
        }, new JsonSerializerOptions { WriteIndented = true }));
        GC.KeepAlive(result);
        GC.KeepAlive(Interaction);
        return result;
    }

    private sealed record MemorySample(long ManagedBytes, long WorkingSetBytes, long PrivateBytes);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MemorySample CaptureMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        long managed = GC.GetTotalMemory(forceFullCollection: true);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new(managed, process.WorkingSet64, process.PrivateMemorySize64);
    }
}
