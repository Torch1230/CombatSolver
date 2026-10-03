using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace CombatSolver;

internal static class GcManualMemoryReleaseChecks
{
    internal static void Run()
    {
        List<string> messages = [];
        Entry.Logger.InfoSink = messages.Add;
        try
        {
            byte[] live = new byte[64 * 1024 * 1024];
            Touch(live);
            WeakReference discarded = AllocateSearchGarbage();
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long privateBefore = process.PrivateMemorySize64;
            long workingBefore = process.WorkingSet64;
            SearchGcPolicy.ForceManualProcessMemoryRelease().WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
            process.Refresh();
            long committedAfter = GC.GetGCMemoryInfo().TotalCommittedBytes;
            long privateAfter = process.PrivateMemorySize64;
            long workingAfter = process.WorkingSet64;
            Touch(live);
            process.Refresh();
            long workingResumed = process.WorkingSet64;
            Console.WriteLine($"MANUAL_RELEASE server_gc={GCSettings.IsServerGC} committed_after={committedAfter} private={privateBefore}/{privateAfter} working={workingBefore}/{workingAfter}/{workingResumed}");
            PolicyCheck.Require(!discarded.IsAlive, "Discarded search buffers stayed reachable.");
            PolicyCheck.Require(!messages.Any(message => message.Contains("WORKING_SET_TRIM", StringComparison.Ordinal)),
                "Manual release evicted live pages which return when execution resumes.");
            PolicyCheck.Require(messages.Any(message => message.Contains("completion_kind=full_blocking_aggressive", StringComparison.Ordinal)),
                "Manual release did not request heap decommit.");
            PolicyCheck.Require(GCSettings.LatencyMode != GCLatencyMode.NoGCRegion,
                "Manual release kept the search allocation reservation.");
            PolicyCheck.Require(privateBefore - privateAfter >= 512L * 1024 * 1024,
                "Manual release kept the unused allocation reservation committed.");
            PolicyCheck.Require(workingResumed - workingAfter < live.Length / 2,
                "Resuming execution loaded the live buffer again.");
            GC.KeepAlive(live);
        }
        finally
        {
            Entry.Logger.InfoSink = null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateSearchGarbage()
    {
        SearchMemoryPressureSignal signal = new();
        using ISearchGcScope scope = SearchGcPolicy.EnterSearchScope(true, 1024L * 1024 * 1024, signal, CancellationToken.None);
        PolicyCheck.Require(signal.IsEnabled, "The fixture requires an actual NoGC allocation reservation.");
        byte[][] buffers = new byte[3200][];
        for (int index = 0; index < buffers.Length; index++)
        {
            buffers[index] = new byte[64 * 1024];
            Touch(buffers[index]);
        }
        WeakReference discarded = new(buffers);
        GC.KeepAlive(buffers);
        return discarded;
    }

    private static void Touch(byte[] buffer)
    {
        for (int index = 0; index < buffer.Length; index += 4096)
            buffer[index]++;
    }
}
