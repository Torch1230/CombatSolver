using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.InCombat.Simulation.Compact;

string output = args.Length > 0 ? args[0] : throw new ArgumentException("An output JSON path is required.");
string label = args.Length > 1 ? args[1] : "candidate";
List<object> samples = [];
List<object> shapes = [];
List<object> retainedStorage = [];
object? sink = null;
long checksum = 0;
int contracts = CandidateContracts.Run();

void Measure(string name, Action action, int iterations)
{
    action();
    for (int sample = 0; sample < 4; sample++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long stamp = Stopwatch.GetTimestamp();
        double cpu = CompactThreadCpu.Milliseconds();
        for (int i = 0; i < iterations; i++) action();
        double cpuMs = CompactThreadCpu.Milliseconds() - cpu;
        double wallMs = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        samples.Add(new { name, sample, iterations, cpuMs, wallMs, bytes });
        GC.KeepAlive(sink);
    }
}

var cards = new ResumableDiscardProgram.Card[30];
Array.Fill(cards, new(0, CardEffectProgram.Empty));
cards[0] = new(1, new([new(CardInstructionKind.Draw, 3), new(CardInstructionKind.Discard, 1)]));
cards[1] = new(0, new([new(CardInstructionKind.Draw, 2), new(CardInstructionKind.Discard, 2)]), true);
IReadOnlyList<int>[] piles = [Enumerable.Range(0, 5).ToArray(), Enumerable.Range(5, 25).ToArray(), [], [], []];
var program = new ResumableDiscardProgram(cards, piles, 6, 0, 3);
List<ResumableDiscardProgram.Candidate> retained = new(34);
void Walk(Action<ResumableDiscardProgram> leaf)
{
    if (program.Complete) { leaf(program); return; }
    int[] hand = program.Cards(ResumableDiscardProgram.Pile.Hand);
    foreach (int[] selection in Combinations(hand, program.ChoiceCount))
    {
        var mark = program.State.Mark();
        try { program.SupplyChoice(selection); program.Run(); Walk(leaf); }
        finally { program.State.Rollback(mark); }
    }
}
void Expand(Action<ResumableDiscardProgram> leaf)
{
    var mark = program.State.Mark();
    try { program.Begin(0); program.Run(); Walk(leaf); }
    finally { program.State.Rollback(mark); }
}
Expand(p =>
{
    var frozen = p.State.Freeze();
    int nonzero = Enumerable.Range(0, frozen.Count).Count(i => frozen[i] != 0);
    int prefix = frozen.Count;
    while (prefix > 0 && frozen[prefix - 1] == 0) prefix--;
    shapes.Add(new { slots = frozen.Count, nonzero, prefix, events = p.EventCount,
        indexedPayload = nonzero * 12, densePayload = frozen.Count * 8 });
    retained.Add(p.Freeze());
});
if (retained.Count != 34) throw new InvalidOperationException("The closed fixture must have 34 physical leaves.");
Measure("closed_expand_freeze", () => { retained.Clear(); Expand(p => retained.Add(p.Freeze())); sink = retained; }, 512);
Measure("closed_open_retained", () => { foreach (var candidate in retained) { var p = candidate.Open(); checksum ^= p.Energy; sink = p; } }, 512);

// Reflection only selects the optional new API, outside the measured loop. The baseline
// source has no reusable restore; no imitation of it is charged as a production operation.
var restoreMethod = typeof(ReversibleValueState).GetMethod("Restore");
var leafValues = retained.Select(c => c.Open().State.Freeze()).ToArray();
if (restoreMethod != null)
{
    var reuse = leafValues[0].CreateWorkspace();
    var restore = restoreMethod.CreateDelegate<Action<ReversibleValueState.FrozenValues>>(reuse);
    Measure("closed_restore_reused", () => { foreach (var frozen in leafValues) { restore(frozen); checksum ^= reuse[0]; } }, 4096);
}

// Structural workloads: all slots are exact integers, not implementations of game
// Power/RNG/death semantics. They bound storage behavior as write density/history grows.
foreach (int history in new[] { 0, 1024, 8192 })
{
    const int branches = 128;
    var state = new ReversibleValueState(1024 + history + 64);
    for (int i = 0; i < 1024 + history; i++) state.Write(i, i + 1);
    var root = state.Freeze();
    var baselineValues = Enumerable.Range(0, state.Count).Select(i => state[i]).ToArray();
    foreach (int writes in new[] { 8, 512, 1024 })
    {
        var held = new ReversibleValueState.FrozenValues[branches];
        void Batch()
        {
            for (int branch = 0; branch < branches; branch++)
            {
                var mark = state.Mark();
                try
                {
                    for (int i = 0; i < writes; i++) state.Write(i, -(branch + i + 1));
                    state.Write(1024 + history, branch + 1);
                    held[branch] = state.Freeze();
                }
                finally { state.Rollback(mark); }
            }
            sink = held;
        }
        Measure($"history_{history}_writes_{writes}_freeze128", Batch, 16);
        for (int i = 0; i < state.Count; i++)
            if (state[i] != baselineValues[i] || root[i] != baselineValues[i])
                throw new InvalidOperationException("A retained branch changed its root.");
        for (int branch = 0; branch < branches; branch++)
        {
            var child = held[branch].CreateWorkspace();
            for (int i = 0; i < child.Count; i++)
            {
                long expected = i < writes ? -(branch + i + 1) : i == 1024 + history ? branch + 1 : baselineValues[i];
                if (child[i] != expected) throw new InvalidOperationException("A retained branch failed full-slot restoration.");
            }
        }
        var storageMethod = typeof(ReversibleValueState.FrozenValues).GetMethod("RetainedStorage", BindingFlags.Static | BindingFlags.NonPublic);
        if (storageMethod != null)
        {
            var storage = ((int Pages, long PayloadBytes, long DirectoryBytes))storageMethod.Invoke(null, [held])!;
            retainedStorage.Add(new { history, writes, branches, storage.Pages, storage.PayloadBytes,
                storage.DirectoryBytes, densePayloadBytes = (long)branches * state.Count * 8 });
        }
        GC.KeepAlive(held);
    }
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(new { label,
    scope = "linked compact storage and closed executor; synthetic histories/density; no production Beam or game effects",
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    tieredCompilation = false, leaves = retained.Count, shapes, samples, retainedStorage, checksum,
    contracts, checks = "all retained synthetic slots, root isolation, 34 closed leaves" }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"COMPACT_CANDIDATE_CHECKS_OK label={label} samples={samples.Count} leaves={retained.Count} contracts={contracts}");

static IEnumerable<int[]> Combinations(int[] values, int count)
{
    if (count == 0) { yield return []; yield break; }
    for (int i = 0; i <= values.Length - count; i++)
        foreach (int[] rest in Combinations(values[(i + 1)..], count - 1))
            yield return [values[i], ..rest];
}
