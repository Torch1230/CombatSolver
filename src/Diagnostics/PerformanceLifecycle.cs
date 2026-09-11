using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

// These spans measure elapsed time including awaits and other mods' patches, not CPU time.
// No arguments or returned game models are captured by continuations.
internal static class PerformanceLifecycle
{
    internal readonly record struct Span(long Id, long Started);
    private static long _nextId;
    internal static Harmony Start()
    {
        Harmony harmony = new("CombatSolver.PerformanceRecording");
        string[] names = ["SetUpNewSingleplayer", "SetUpSavedSingleplayer", "Launch", "GenerateMap",
            "EnterMapPointInternal", "LoadIntoLatestMapCoord", "ExitCurrentRooms", "EnterRoomInternal",
            "EnterNextAct", "EnterAct", "WinRun", "AbandonInternal", "CleanUp", "FadeIn", "FadeOut"];
        foreach (string name in names)
        {
            MethodInfo method = typeof(RunManager).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(RunManager).FullName, name);
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(PerformanceLifecycle), nameof(Begin)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(PerformanceLifecycle), typeof(Task).IsAssignableFrom(method.ReturnType) ? nameof(AsyncEnd) : nameof(SyncEnd)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(PerformanceLifecycle), nameof(Failed)) { priority = Priority.Last });
        }
        return harmony;
    }

    private static void Begin(MethodBase __originalMethod, out Span __state)
    {
        __state = new(Interlocked.Increment(ref _nextId), Stopwatch.GetTimestamp());
        PerformanceRecording.Log("span", $"begin id={__state.Id} method={__originalMethod.Name}");
    }
    private static void SyncEnd(MethodBase __originalMethod, Span __state) => End(__originalMethod.Name, __state, "complete");
    private static void AsyncEnd(MethodBase __originalMethod, Task __result, Span __state)
    {
        string method = __originalMethod.Name;
        if (__result == null) { End(method, __state, "null_task"); return; }
        _ = __result.ContinueWith(task => End(method, __state, task.Status.ToString()),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private static void Failed(MethodBase __originalMethod, Span __state, Exception? __exception)
    {
        if (__exception != null) End(__originalMethod.Name, __state, "threw:" + __exception.GetType().FullName);
    }
    private static void End(string method, Span span, string status) => PerformanceRecording.Log("span",
        FormattableString.Invariant($"end id={span.Id} method={method} elapsedMs={Stopwatch.GetElapsedTime(span.Started).TotalMilliseconds:F3} status={status}"));
}
