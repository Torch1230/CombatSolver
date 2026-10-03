using System.Diagnostics;
using System.Text;
using CombatSolver;
using CombatSolver.Replay;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
CheckManualProjection();
if (args.Contains("--manual-projection-only")) return;
string root = Path.Combine(Path.GetTempPath(), "CombatSolver-journal-tests-" + Guid.NewGuid().ToString("N"));
using CombatDiagnosticJournal journal = new(root);
journal.Write("info", "process boot");
journal.BeginCombat("first", "遭遇一", "seed");
Action<string> oldWorker = journal.Bind("info");
journal.Write("info", "before upload");
Task<CombatLogArchive> frozen = journal.CaptureAsync();
journal.Write("error", "after upload");
journal.EndCombat("victory");
journal.BeginCombat("second", "遭遇二", "seed");
oldWorker("old callback must not enter the new battle");
journal.Write("info", "second battle");
CombatLogArchive first = await frozen;
CombatLogArchive second = await journal.CaptureAsync();
string firstText = Encoding.UTF8.GetString(first.Events.JsonLines);
Check(firstText.Contains("before upload") && !firstText.Contains("after upload"), "upload prefix was not frozen");
Check(second.History.Length == 1 && second.History[0].Errors == 1 && second.History[0].LastError == "after upload", "previous fight summary missing");
string secondText = Encoding.UTF8.GetString(second.Events.JsonLines);
Check(secondText.Contains("second battle") && !secondText.Contains("before upload") && !secondText.Contains("old callback"), "combat isolation failed");
Check(second.Events.Error == null && second.Process.EventCount == 1, "journal write failed");
Console.WriteLine("PASS frozen upload, historical summary, worker ownership and process isolation");

journal.BeginCombat("third", "new run", "other-seed");
Check((await journal.CaptureAsync()).History.Length == 0, "run histories mixed");
long allocated = GC.GetAllocatedBytesForCurrentThread();
Stopwatch watch = Stopwatch.StartNew();
for (int i = 0; i < 10000; i++) journal.Write("info", "fixed diagnostic event");
watch.Stop();
allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
CombatLogArchive throughput = await journal.CaptureAsync();
Check(throughput.Events.EventCount == 10000 && throughput.Events.Error == null, "normal diagnostic events lost");
Check(throughput.Events.PeakPendingBytes <= 8 * 1024 * 1024, "pending queue exceeded byte bound");
Console.WriteLine($"PASS 10000 producer calls: {watch.Elapsed.TotalMilliseconds:F2} ms, {allocated / 10000d:F1} allocated bytes/call; pending peak {throughput.Events.PeakPendingBytes}");

using AppendOnlyEventLog<string> limited = new(Encoding.UTF8.GetBytes, maximumPendingBytes: 10);
Check(!limited.TryAppend("over limit", 20), "queue must reject without waiting");
EventLogSnapshot limit = await limited.CaptureAsync();
Check(limit.Error == "event_pending_memory_limit", "missing explicit incomplete marker");
Console.WriteLine("PASS overload is visible and nonblocking");

static void CheckManualProjection()
{
foreach (var test in new[]
{
    (Before: 17, After: 9, OldPotions: 0, NewPotions: 1, Adjusted: -1, Improved: false),
    (Before: 17, After: 8, OldPotions: 0, NewPotions: 1, Adjusted: 0, Improved: true),
    (Before: 17, After: 7, OldPotions: 0, NewPotions: 1, Adjusted: 1, Improved: true),
    (Before: 30, After: 13, OldPotions: 1, NewPotions: 3, Adjusted: -1, Improved: false),
    (Before: 30, After: 12, OldPotions: 1, NewPotions: 3, Adjusted: 0, Improved: true),
    (Before: 7, After: 3, OldPotions: 1, NewPotions: 1, Adjusted: 4, Improved: true),
    (Before: 7, After: 7, OldPotions: 0, NewPotions: 0, Adjusted: 0, Improved: false),
    (Before: 7, After: 8, OldPotions: 0, NewPotions: 1, Adjusted: -10, Improved: false),
    (Before: 7, After: 3, OldPotions: 2, NewPotions: 1, Adjusted: 4, Improved: true),
})
{
    ManualProjectionComparison comparison = new(1, 2, test.Before, test.After,
        test.OldPotions, test.NewPotions, "test");
    Check(comparison.PotionAdjustedHpReduction == test.Adjusted
        && comparison.IsImprovement == test.Improved, $"potion comparison {test} failed");
    string serialized = System.Text.Json.JsonSerializer.Serialize(comparison,
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
    using var json = System.Text.Json.JsonDocument.Parse(serialized);
    Check(json.RootElement.GetProperty("additionalPotionCount").GetInt32() == Math.Max(0, test.NewPotions - test.OldPotions)
        && json.RootElement.GetProperty("potionAdjustedHpReduction").GetInt32() == test.Adjusted,
        "potion comparison metadata lost resource costs");
}
Console.WriteLine("PASS 9 manual projection boundaries and metadata serialization");
}
