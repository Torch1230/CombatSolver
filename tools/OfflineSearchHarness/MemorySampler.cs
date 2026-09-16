using System.Diagnostics;

namespace OfflineSearchHarness;

/// <summary>
/// 后台采样峰值内存。macOS 上 <c>Process.PeakWorkingSet64</c> 恒为 0，
/// <c>GC.GetGCMemoryInfo().HeapSizeBytes</c> 又只反映最后一次 GC，所以自己按固定间隔取最大值。
/// Dispose 幂等：调用方可以先显式停采样再让 using 收尾。
/// </summary>
internal sealed class MemorySampler : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private bool _disposed;

    public long PeakManagedHeapBytes { get; private set; }
    public long PeakManagedLiveBytes { get; private set; }
    public long PeakWorkingSetBytes { get; private set; }
    public long Samples { get; private set; }

    public MemorySampler(TimeSpan interval)
    {
        Sample();
        _loop = Task.Factory.StartNew(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await Task.Delay(interval, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                Sample();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private void Sample()
    {
        lock (_gate)
        {
            PeakManagedHeapBytes = Math.Max(PeakManagedHeapBytes, GC.GetGCMemoryInfo().HeapSizeBytes);
            PeakManagedLiveBytes = Math.Max(PeakManagedLiveBytes, GC.GetTotalMemory(forceFullCollection: false));
            using Process process = Process.GetCurrentProcess();
            PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, process.WorkingSet64);
            Samples++;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        Sample();
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _stop.Dispose();
    }
}
