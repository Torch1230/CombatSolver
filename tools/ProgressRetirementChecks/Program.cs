using System.Runtime.CompilerServices;
using CombatSolver;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"{name} failed");
    checks++;
}

SolverResult result = new(SolverResultScope.SearchCompletion);
LiveCombatStamp stamp = new("state-7");
SearchInteractionState interaction = new();
var payload = new object();
SolverRouteAdoptionSeed seed = new(1, [new PlanAction(1, payload)], () => result);
interaction.PublishProgress(new SolverProgress(1, seed));
Check(ReferenceEquals(interaction.Progress?.RouteAdoptionSeed, seed), "worker progress published");
Check(interaction.TryCreateDisplayProgress(Environment.TickCount64 + 2, out SolverProgress rendered), "progress rendered");
Check(ReferenceEquals(rendered, interaction.RenderedProgress), "rendered progress retained");
interaction.RenderedRouteAdoptionSeed = seed;
Check(interaction.RequestAdoptRoute(seed, stopAfterResult: true), "stop takeover accepted");
Check(interaction.StopRequested, "stop request visible");
SearchTakeoverRequest? completed = interaction.CompleteTakeover();
Check(completed?.StopAfterResult == true, "complete returns stop request");
Check(completed?.RouteAdoptionSeed == seed, "complete returns original seed");
Check(interaction.CurrentTakeoverRequest == null, "request retired");
Check(!interaction.CanAcceptTakeover, "takeover gate closed");
Check(interaction.Progress == null, "worker progress retired");
Check(interaction.RenderedProgress == null, "rendered progress retired");
Check(interaction.RenderedRouteAdoptionSeed == null, "rendered seed retired");
Check(interaction.ProgressDisplay.RenderedProgress == null, "display cache retired");

interaction = new();
var seedForStop = new SolverRouteAdoptionSeed(2, [], () => result);
interaction.RenderedRouteAdoptionSeed = seedForStop;
Check(interaction.RequestAdoptRoute(seedForStop, stopAfterResult: true), "second stop accepted");
interaction.PreserveStoppedResult(result, stamp);
Check(ReferenceEquals(interaction.StoppedResult, result), "stopped result retained");
Check(interaction.StoppedStamp == stamp, "stopped stamp retained");
Check(interaction.CurrentTakeoverRequest == null, "preserve retires request");
Check(interaction.RenderedRouteAdoptionSeed == null, "preserve retires seed");
Check(ReferenceEquals(interaction.TakeStoppedResult(stamp), result), "matching stopped result retrieved");
Check(interaction.StoppedResult == null && interaction.StoppedStamp == null, "retrieval consumes stopped result");
Check(interaction.TakeStoppedResult(new LiveCombatStamp("state-8")) == null, "expired result rejected");

interaction = new();
interaction.PreserveStoppedResult(result, stamp);
Check(interaction.TakeStoppedResult(new LiveCombatStamp("state-8")) == null, "fresh mismatched stamp rejected");
Check(interaction.StoppedResult == null && interaction.StoppedStamp == null, "mismatched result is retired");

interaction.ResetForSearch();
Check(interaction.CanAcceptTakeover, "reset reopens takeover");
Check(interaction.RequestApplyCurrentTurn(), "reset allows new request");
Check(interaction.IsApplyingCurrentTurn, "new request kind preserved");
Check(!interaction.RequestApplyCurrentTurn(), "duplicate request rejected");
SearchTakeoverRequest? normal = interaction.CompleteTakeover();
Check(normal?.Kind == SearchTakeoverKind.ApplyCurrentTurn, "normal complete returns request");
Check(interaction.CompleteTakeover() == null, "no-request complete returns null");

int materializeCount = 0;
SolverRouteAdoptionSeed once = new(3, [], () => { materializeCount++; return result; });
Check(ReferenceEquals(once.Materialize(), result), "seed materializes result");
Check(ReferenceEquals(once.Materialize(), result) && materializeCount == 1, "seed materializes once");
int failures = 0;
SolverRouteAdoptionSeed throws = new(4, [], () => { failures++; throw new InvalidOperationException("contract"); });
try { throws.Materialize(); } catch (InvalidOperationException) { }
try { throws.Materialize(); } catch (InvalidOperationException) { }
Check(failures == 1, "failed materialization is cached");

var seedRetirement = TestSeedRetirement(result, stamp);
Check(seedRetirement.SelectedMaterializations == 1, "selected seed materialized once");
Check(seedRetirement.UnselectedMaterializations == 0, "unselected seed not materialized");
ForceCollection(seedRetirement.UnselectedWeak);
Check(!seedRetirement.UnselectedWeak.IsAlive, "unselected seed closure payload collected");
Check(ReferenceEquals(seedRetirement.State.StoppedResult, result), "selected result survives seed retirement");

interaction = new();
var throwingSeed = new SolverRouteAdoptionSeed(7, [], () => throw new InvalidOperationException("expected"));
interaction.PublishProgress(new SolverProgress(12, throwingSeed));
interaction.TryCreateDisplayProgress(Environment.TickCount64 + 2, out _);
interaction.RenderedRouteAdoptionSeed = throwingSeed;
Check(interaction.RequestAdoptRoute(throwingSeed), "throwing seed request accepted");
try { interaction.FinalizeWorkerResult(result); throw new InvalidOperationException("expected materialization did not throw"); }
catch (InvalidOperationException error) when (error.Message == "expected") { }
Check(interaction.CompleteTakeover()?.RouteAdoptionSeed == throwingSeed, "exception takeover returns request");
Check(interaction.Progress == null && interaction.RenderedProgress == null
    && interaction.RenderedRouteAdoptionSeed == null
    && interaction.ProgressDisplay.RenderedProgress == null,
    "exception completion clears all progress");

var retired = CaptureRetiredPayload(result, stamp);
ForceCollection(retired.WeakPayload);
Check(!retired.WeakPayload.IsAlive && ReferenceEquals(retired.State.StoppedResult, result), "unselected payload can retire while result remains");
Console.WriteLine($"PROGRESS_RETIREMENT_OK {checks} assertions; result preservation, takeover retirement, reset, lazy seed, expiry and GC");

[MethodImpl(MethodImplOptions.NoInlining)]
static (WeakReference WeakPayload, SearchInteractionState State) CaptureRetiredPayload(SolverResult result, LiveCombatStamp stamp)
{
    SearchInteractionState state = new();
    object payload = new();
    WeakReference weak = new(payload);
    SolverRouteAdoptionSeed oldSeed = new(9, [new PlanAction(9, payload)], () => result);
    state.PublishProgress(new SolverProgress(9, oldSeed));
    state.TryCreateDisplayProgress(Environment.TickCount64 + 2, out _);
    state.RenderedRouteAdoptionSeed = oldSeed;
    state.PreserveStoppedResult(result, stamp);
    if (!ReferenceEquals(state.StoppedResult, result))
        throw new InvalidOperationException("GC result still retained failed");
    oldSeed = null!;
    payload = null!;
    return (weak, state);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static (int SelectedMaterializations, int UnselectedMaterializations, WeakReference UnselectedWeak, SearchInteractionState State)
    TestSeedRetirement(SolverResult result, LiveCombatStamp stamp)
{
    SearchInteractionState state = new();
    int selectedMaterializations = 0;
    int unselectedMaterializations = 0;
    PayloadHolder selectedHolder = new();
    PayloadHolder unselectedHolder = new();
    WeakReference unselectedWeak = new(unselectedHolder);
    SolverRouteAdoptionSeed seedA = new(5, [], () =>
    {
        _ = selectedHolder.Data.Length;
        selectedMaterializations++;
        return result;
    });
    SolverRouteAdoptionSeed seedB = new(6, [], () =>
    {
        _ = unselectedHolder.Data.Length;
        unselectedMaterializations++;
        return result;
    });
    state.PublishProgress(new SolverProgress(11, seedB));
    state.TryCreateDisplayProgress(Environment.TickCount64 + 2, out _);
    state.RenderedRouteAdoptionSeed = seedB;
    if (!state.RequestAdoptRoute(seedA)
        || !ReferenceEquals(state.FinalizeWorkerResult(result), result)
        || selectedMaterializations != 1
        || unselectedMaterializations != 0)
    {
        throw new InvalidOperationException("A/B seed retirement setup failed");
    }
    state.PreserveStoppedResult(result, stamp);
    return (selectedMaterializations, unselectedMaterializations, unselectedWeak, state);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void ForceCollection(WeakReference weak)
{
    for (int i = 0; i < 8 && weak.IsAlive; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

sealed class PayloadHolder
{
    public byte[] Data { get; } = new byte[4096];
}
