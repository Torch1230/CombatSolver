using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

namespace CombatSolver;

internal readonly record struct WrapperRegistrySnapshot(int GodotObjects, int OtherWrappers);

// Local, opt-in diagnostics. The writer accepts scalar snapshots only; it never owns game models.
internal sealed class PerformanceSession : IDisposable
{
    private readonly Channel<object> _events = Channel.CreateBounded<object>(8192);
    private readonly Task _writer;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Func<WrapperRegistrySnapshot>? _wrapperRegistry;
    private long _dropped;
    private long _heartbeat = Stopwatch.GetTimestamp();
    private int _stopping;
    private string? _failure;
    internal string? Failure => Volatile.Read(ref _failure);
    internal long Dropped => Interlocked.Read(ref _dropped);
    internal static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal PerformanceSession(string directory, Func<WrapperRegistrySnapshot>? wrapperRegistry = null)
    {
        _wrapperRegistry = wrapperRegistry;
        Directory.CreateDirectory(directory);
        _writer = Task.Factory.StartNew(() => Run(directory), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal void Heartbeat() => Interlocked.Exchange(ref _heartbeat, Stopwatch.GetTimestamp());
    internal void Write(object snapshot)
    {
        if (!_events.Writer.TryWrite(snapshot)) Interlocked.Increment(ref _dropped);
    }

    private void Run(string directory)
    {
        try
        {
            using StreamWriter output = new(Path.Combine(directory, "timeline.jsonl"), false);
            long lastSample = 0;
            int samples = 0;
            while (Volatile.Read(ref _stopping) == 0 || _events.Reader.TryPeek(out _))
            {
                int drained = 0;
                while (drained++ < 8192 && _events.Reader.TryRead(out object? item))
                    output.WriteLine(JsonSerializer.Serialize(item, item.GetType()));
                long now = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(lastSample, now).TotalSeconds >= 1)
                {
                    long workStart = Stopwatch.GetTimestamp();
                    _process.Refresh();
                    GCMemoryInfo gc = GC.GetGCMemoryInfo();
                    object[] generations = gc.GenerationInfo.ToArray().Select((g, i) => (object)new
                    {
                        generation = i, g.SizeBeforeBytes, g.SizeAfterBytes,
                        g.FragmentationBeforeBytes, g.FragmentationAfterBytes
                    }).ToArray();
                    NativeMemory memory = ReadNativeMemory(_process.Handle);
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        kind = "sample", utcMs = Now, monotonic = now,
                        heartbeatAgeMs = Stopwatch.GetElapsedTime(Interlocked.Read(ref _heartbeat), now).TotalMilliseconds,
                        cpuMs = _process.TotalProcessorTime.TotalMilliseconds,
                        userCpuMs = _process.UserProcessorTime.TotalMilliseconds,
                        kernelCpuMs = _process.PrivilegedProcessorTime.TotalMilliseconds,
                        workingSet = _process.WorkingSet64, privateBytes = _process.PrivateMemorySize64,
                        peakWorkingSet = _process.PeakWorkingSet64, handles = _process.HandleCount,
                        threads = _process.Threads.Count, allocated = GC.GetTotalAllocatedBytes(false),
                        managedEstimate = GC.GetTotalMemory(false), gen0 = GC.CollectionCount(0),
                        gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                        gc.Index, gc.Generation, gc.Concurrent, gc.Compacted, gc.HeapSizeBytes,
                        gc.FragmentedBytes, gc.TotalCommittedBytes, gc.MemoryLoadBytes,
                        gc.HighMemoryLoadThresholdBytes, gc.TotalAvailableMemoryBytes,
                        gc.PinnedObjectsCount, gc.FinalizationPendingCount,
                        pauseTotalMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                        lastGcPausesMs = gc.PauseDurations.ToArray().Select(p => p.TotalMilliseconds),
                        latency = System.Runtime.GCSettings.LatencyMode.ToString(),
                        serverGc = System.Runtime.GCSettings.IsServerGC, generations, memory,
                        poolThreads = ThreadPool.ThreadCount, poolPending = ThreadPool.PendingWorkItemCount,
                        poolCompleted = ThreadPool.CompletedWorkItemCount, timers = Timer.ActiveCount,
                        wrapperRegistry = _wrapperRegistry?.Invoke(),
                        dropped = Dropped, samplerMs = Stopwatch.GetElapsedTime(workStart).TotalMilliseconds
                    }));
                    if (samples++ % 5 == 0) WriteThreads(output);
                    output.Flush();
                    lastSample = now;
                }
                if (Volatile.Read(ref _stopping) == 0) Thread.Sleep(100);
            }
            output.WriteLine(JsonSerializer.Serialize(new { kind = "writer_end", utcMs = Now, dropped = Dropped }));
        }
        catch (Exception error)
        {
            // Diagnostics must explicitly fail without bringing down or modifying the player's run.
            Volatile.Write(ref _failure, error.ToString());
            try { File.WriteAllText(Path.Combine(directory, "RECORDER_FAILED.txt"), error.ToString()); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void WriteThreads(StreamWriter output)
    {
        foreach (ProcessThread thread in _process.Threads)
        {
            using (thread)
            {
                try
                {
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        kind = "thread", utcMs = Now, id = thread.Id,
                        cpuMs = thread.TotalProcessorTime.TotalMilliseconds,
                        userCpuMs = thread.UserProcessorTime.TotalMilliseconds,
                        kernelCpuMs = thread.PrivilegedProcessorTime.TotalMilliseconds,
                        state = thread.ThreadState.ToString(),
                        wait = thread.ThreadState == System.Diagnostics.ThreadState.Wait ? thread.WaitReason.ToString() : null
                    }));
                }
                catch (System.ComponentModel.Win32Exception) { /* The thread exited during enumeration. */ }
                catch (InvalidOperationException) { /* The thread exited during enumeration. */ }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        _events.Writer.TryComplete();
        if (_writer.Wait(TimeSpan.FromSeconds(3))) _process.Dispose();
    }

    internal sealed record NativeMemory(bool Available, uint PageFaults, ulong AvailablePhysical,
        ulong TotalPhysical, ulong AvailablePageFile, ulong ReadBytes, ulong WriteBytes, ulong OtherBytes);
    private static NativeMemory ReadNativeMemory(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows()) return new(false, 0, 0, 0, 0, 0, 0, 0);
        MemoryCounters counters = new() { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GetProcessMemoryInfo(handle, ref counters, counters.Size)
            || !GlobalMemoryStatusEx(ref status) || !GetProcessIoCounters(handle, out IoCounters io))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new(true, counters.PageFaultCount, status.AvailablePhysical, status.TotalPhysical,
            status.AvailablePageFile, io.ReadBytes, io.WriteBytes, io.OtherBytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPaged, QuotaPaged, QuotaPeakNonPaged,
            QuotaNonPaged, PageFileUsage, PeakPageFileUsage, PrivateUsage;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
