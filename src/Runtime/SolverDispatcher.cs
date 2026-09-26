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
        // 隔离 worker 是无头进程：它只跑模拟，不承载玩家可见的战斗 UI。
        // 缺这道守卫时，worker 里也会走到 MonitorCombatPresence → ShowRetainedOrManualReady
        // → SolverOverlay.EnsureCreated → Create → new SolverGrowthStrategyPanel()；
        // 在无头环境里构造 UI 面板会让进程以 exit code 1 退出，父进程随即在
        // "exited with code 1 before becoming reusable" 上失败，并留下被锁住的镜像 ——
        // 该父进程此后再也无法做任何预战预报，只能重启游戏。
        if (!Entry.IsPreCombatWorker)
        {
            SolverController.MonitorCombatPresence();
            SolverController.RefreshSearchProgress();
        }
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
