using System.Text.Json;
using CombatSolver.Replay;

namespace CombatSolver;

internal sealed record CombatLogEntry(long Time, string Level, string Message);
internal sealed record CombatLogSummary(string SessionId, string Encounter, string Seed,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? EndReason, long Messages,
    long Errors, string? LastError, string? RecordingError);
internal sealed record CombatLogArchive(CombatLogSummary? Current, CombatLogSummary[] History,
    EventLogSnapshot Events, EventLogSnapshot Process);

// Producers enqueue immutable text. Serialization, file I/O, snapshots and retirement run
// off the game/search thread. No search node, model or simulator is owned by this journal.
internal sealed class CombatDiagnosticJournal : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private sealed class Session(string id, string encounter, string seed, string path)
    {
        public readonly AppendOnlyEventLog<CombatLogEntry> Log = new(Serialize, outputPath: path);
        public readonly string Path = path;
        public readonly DateTimeOffset Started = DateTimeOffset.Now;
        public DateTimeOffset? Ended;
        public string? Reason, LastError;
        public long Messages, Errors;
        public bool Retired;
        public CombatLogSummary Summary() => new(id, encounter, seed, Started, Ended, Reason,
            Messages, Errors, LastError, Log.Error);
        public string Seed => seed;
    }
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly AppendOnlyEventLog<CombatLogEntry> _process;
    private readonly Queue<CombatLogSummary> _history = new();
    private Session? _session;
    private bool _disposed;

    public CombatDiagnosticJournal(string directory)
    {
        _directory = Path.Combine(directory, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        _process = new(Serialize, maximumFileBytes: 4 * 1024 * 1024,
            outputPath: Path.Combine(_directory, "process.jsonl"));
    }
    private static byte[] Serialize(CombatLogEntry entry) => JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);

    public void BeginCombat(string id, string encounter, string seed)
    {
        lock (_gate)
        {
            if (_session is { } previous)
            {
                if (previous.Seed != seed) _history.Clear();
                else
                {
                    _history.Enqueue(previous.Summary());
                    while (_history.Count > 100) _history.Dequeue();
                }
                previous.Retired = true;
                previous.Log.Dispose();
                _ = Task.Run(() => RemoveRetiredAsync(previous));
            }
            _session = new(id, encounter, seed, Path.Combine(_directory, $"combat-{id}.jsonl"));
            WriteProcessEvent($"COMBAT_LOG_BEGIN id={id} encounter={encounter}");
        }
    }
    private async Task RemoveRetiredAsync(Session session)
    {
        await session.Log.Completion.ConfigureAwait(false);
        try { File.Delete(session.Path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { WriteOwned(null, "warning", $"log_cleanup_failed:{error.GetType().Name}"); }
    }
    public void EndCombat(string reason)
    {
        lock (_gate)
        {
            if (_session == null) return;
            _session.Ended = DateTimeOffset.Now;
            _session.Reason = reason;
            WriteProcessEvent($"COMBAT_LOG_END reason={reason}");
        }
    }
    public Action<string> Bind(string level)
    {
        lock (_gate)
        {
            Session? owner = _session;
            return message => WriteOwned(owner, level, message);
        }
    }
    public void Write(string level, string message)
    {
        lock (_gate) WriteCore(_session, level, message);
    }
    private void WriteOwned(Session? owner, string level, string message)
    {
        lock (_gate) WriteCore(owner, level, message);
    }
    private void WriteCore(Session? owner, string level, string message)
    {
        if (_disposed || owner?.Retired == true) return;
        if (owner != null)
        {
            owner.Messages++;
            if (level == "error")
            {
                owner.Errors++;
                owner.LastError = message.Length > 2000 ? message[..2000] : message;
            }
        }
        CombatLogEntry entry = new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), level, message);
        int estimatedBytes = checked(message.Length * 2);
        (owner?.Log ?? _process).TryAppend(entry, estimatedBytes);
        // Keep compact process-wide evidence when the next combat retires its detailed log.
        // This excludes per-node diagnostics and the frequently refreshed memory UI sample.
        if (owner != null && IsProcessPerformanceEvent(message))
            _process.TryAppend(entry, estimatedBytes);
    }
    private void WriteProcessEvent(string message)
        => _process.TryAppend(new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "info", message), message.Length * 2);

    internal static bool IsProcessPerformanceEvent(string message)
        => message.StartsWith("[CombatSolver/Test] HEAP_RECLAIM ", StringComparison.Ordinal)
            || message.StartsWith("[CombatSolver/Test] GC_SEARCH_ALLOCATION_LIMIT ", StringComparison.Ordinal)
            || message.StartsWith("[CombatSolver/Test] GC_ALLOCATION_CAPACITY ", StringComparison.Ordinal)
            || message.StartsWith("[CombatSolver/Test] GC_FRAGMENTATION_COMPACTION ", StringComparison.Ordinal)
            || message.StartsWith("[CombatSolver/Test] POTION_GRADIENT_MEMORY_DECISION ", StringComparison.Ordinal)
            || message.StartsWith("[CombatSolver/Test] MAIN_THREAD_FRAMES ", StringComparison.Ordinal)
            || message.StartsWith("[CombatSolver/Test] SEARCH_GC_LIFECYCLE ", StringComparison.Ordinal);
    public Task<CombatLogArchive> CaptureAsync()
    {
        lock (_gate)
        {
            CombatLogSummary? current = _session?.Summary();
            CombatLogSummary[] history = _history.ToArray();
            Task<EventLogSnapshot>? events = _session?.Log.CaptureAsync();
            return Finish(current, history, events, _process.CaptureAsync());
        }
    }
    private static async Task<CombatLogArchive> Finish(CombatLogSummary? current, CombatLogSummary[] history,
        Task<EventLogSnapshot>? events, Task<EventLogSnapshot> process)
        => new(current, history, events == null ? new([], 0, null, 0, 0) : await events.ConfigureAwait(false),
            await process.ConfigureAwait(false));
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _session?.Log.Dispose();
            _process.Dispose();
        }
    }
}
