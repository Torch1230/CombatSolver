using System.Text.Json;

namespace CombatSolver;

// Plain immutable transport/storage data. No game objects or search dependencies.
internal sealed record RunStatisticsRecord(
    string RunId, string ProfileId, long StartedAt, long? EndedAt, string CharacterId,
    int Ascension, string Version, string Participation, string Outcome,
    bool ObservedFromStart, bool EverEnabled, bool DisabledInCombat,
    string[] Battles, string[] SolvedBattles, string[] ExecutedBattles, string[] AutoBattles);
internal sealed record HistoricalCharacterStatistics(string CharacterId, int Wins, int Losses, long CurrentStreak, long BestStreak);
internal sealed record HistoricalStatistics(string ProfileId, long CapturedAt, HistoricalCharacterStatistics[] Characters);
internal sealed record RunStatisticsSummary(int Wins, int Losses, int Abandoned, int CurrentStreak, int BestStreak,
    int CompletedRuns, double? WinRate);
internal sealed record RunStatisticsSnapshot(int SchemaVersion, string ProfileId, long CapturedAt,
    RunStatisticsSummary Solver, HistoricalStatistics? Historical);

internal static class RunStatisticsAggregation
{
    internal static RunStatisticsSummary Summarize(IEnumerable<RunStatisticsRecord> records)
    {
        int wins = 0, losses = 0, abandoned = 0, streak = 0, best = 0;
        var ordered = records.OrderBy(r => r.StartedAt).ThenBy(r => r.RunId, StringComparer.Ordinal).ToArray();
        for (int index = 0; index < ordered.Length; index++)
        {
            var run = ordered[index];
            if (run.Outcome == "pending" && index == ordered.Length - 1) continue;
            if (run.Participation != "full") { streak = 0; continue; }
            if (run.Outcome == "pending") { streak = 0; continue; }
            if (run.Outcome == "win") { wins++; streak++; best = Math.Max(best, streak); }
            else { losses++; streak = 0; if (run.Outcome == "abandoned") abandoned++; }
        }
        return new(wins, losses, abandoned, streak, best, wins + losses,
            wins + losses == 0 ? null : (double)wins / (wins + losses));
    }
}

// Used exclusively by the statistics worker. Each run is a small independent atomic file.
internal sealed class RunStatisticsStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly Dictionary<string, RunStatisticsRecord> _runs = new();
    private readonly Dictionary<string, HistoricalStatistics> _history = new();
    private readonly HashSet<string> _pending = new();
    internal RunStatisticsStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        foreach (string path in Directory.EnumerateFiles(directory, "*.run.json"))
        {
            var record = JsonSerializer.Deserialize<RunStatisticsRecord>(File.ReadAllText(path), Json)!;
            _runs.Add(record.RunId, record);
            if (!File.Exists(path + ".sent")) _pending.Add(record.RunId);
        }
        foreach (string path in Directory.EnumerateFiles(directory, "*.history.json"))
        {
            var history = JsonSerializer.Deserialize<HistoricalStatistics>(File.ReadAllText(path), Json)!;
            _history.Add(history.ProfileId, history);
        }
    }
    internal RunStatisticsRecord? Find(string id) => _runs.GetValueOrDefault(id);
    internal bool HasHistory(string profile) => _history.ContainsKey(profile);
    internal void Reconcile(string profile, string nativeDirectory)
    {
        foreach (var run in _runs.Values.Where(r => r.ProfileId == profile && r.Outcome == "pending").ToArray())
        {
            string path = Path.Combine(nativeDirectory, (run.StartedAt / 1000) + ".run");
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("start_time").GetInt64() * 1000 != run.StartedAt) throw new InvalidDataException("Native history identity mismatch.");
            // Only reconcile our own tracked run, never assign solver participation to historical runs.
            Save(run with { Outcome = root.GetProperty("was_abandoned").GetBoolean() ? "abandoned" : root.GetProperty("win").GetBoolean() ? "win" : "loss",
                EndedAt = run.StartedAt + (long)(root.GetProperty("run_time").GetDouble() * 1000) });
        }
    }
    internal void Import(HistoricalStatistics history)
    {
        if (HasHistory(history.ProfileId)) return;
        Write(history.ProfileId + ".history.json", history);
        _history.Add(history.ProfileId, history);
    }
    internal void Save(RunStatisticsRecord record)
    {
        // Invalidate receipt before replacing the event, so an interrupted save is retried.
        File.Delete(Path.Combine(_directory, record.RunId + ".run.json.sent"));
        Write(record.RunId + ".run.json", record);
        _runs[record.RunId] = record;
        _pending.Add(record.RunId);
    }
    private void Write<T>(string name, T value)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".tmp", path, true);
    }
    internal RunStatisticsRecord[] Pending(int count) => _pending.Take(count).Select(id => _runs[id]).ToArray();
    internal void Acknowledge(RunStatisticsRecord record)
    {
        File.WriteAllText(Path.Combine(_directory, record.RunId + ".run.json.sent"), "1");
        _pending.Remove(record.RunId);
    }
    internal RunStatisticsSnapshot Snapshot(string profile) => new(1, profile, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        RunStatisticsAggregation.Summarize(_runs.Values.Where(r => r.ProfileId == profile)),
        _history.GetValueOrDefault(profile));
}
