using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

/// <summary>后台线程只入队；所有 Godot、战斗状态写入和 UI 更新都在这里回到主线程执行。</summary>
internal sealed partial class SolverDispatcher : Node
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static SolverDispatcher? _instance;
    private long _lastProcessTimestamp;

    public static void Ensure(NGame host)
    {
        if (_instance != null && GodotObject.IsInstanceValid(_instance))
            return;
        _instance = new SolverDispatcher { Name = "CombatSolverDispatcher" };
        host.AddChild(_instance);
        PerformanceRecording.Start(host);
        OnlinePresence.Start(host);
        _instance.SetProcess(true);
    }

    public static void Post(Action action)
    {
        Queue.Enqueue(action);
    }

    public override void _Process(double delta)
    {
        long now = Stopwatch.GetTimestamp();
        long allocated = PerformanceRecording.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
        if (_lastProcessTimestamp != 0)
            SolverController.ObserveMainThreadFrameGap(Stopwatch.GetElapsedTime(_lastProcessTimestamp, now));
        _lastProcessTimestamp = now;
        while (Queue.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Entry.Logger.Error($"[CombatSolver/Test] MAIN_THREAD_CALLBACK_FAILURE exception={ex}");
            }
        }
        SolverController.MonitorCombatPresence();
        SolverController.RefreshSearchProgress();
        if (PerformanceRecording.Enabled)
            PerformanceRecording.Dispatcher(Stopwatch.GetElapsedTime(now).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    public override void _ExitTree()
    {
        SolverController.Reset("host_exit");
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }
}
