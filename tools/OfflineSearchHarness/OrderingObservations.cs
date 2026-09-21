using System.Security.Cryptography;
using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

/// <summary>
/// Bounded, opt-in copies of real retention decisions. Never retains a node or simulator.
/// Observations are training diagnostics, not performance samples or optimality labels.
/// </summary>
internal sealed class OrderingObservations : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(UnattendedTestFiles.JsonOptions) { WriteIndented = false };
    private readonly StreamWriter _writer;
    private readonly int _limit;
    private int _written;
    private readonly HashSet<StateFingerprint> _watched;
    private readonly object _writeLock = new();
    public SearchPathObserver Observer { get; }

    public OrderingObservations(string directory, int limit, string? watchedStatesPath = null)
    {
        _limit = limit;
        _watched = watchedStatesPath == null ? []
            : new(JsonSerializer.Deserialize<StateFingerprint[]>(File.ReadAllText(watchedStatesPath), Json)
                ?? throw new InvalidDataException("Missing watched ordering states."));
        _writer = new StreamWriter(new FileStream(Path.Combine(directory, "ordering-observations.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        Observer = new SearchPathObserver(
            state => Volatile.Read(ref _written) < _limit && _watched.Contains(state), Observe,
            _ => Volatile.Read(ref _written) < _limit);
    }

    private void Observe(SearchPathObservation observation)
    {
        if (observation.Stage is not (SearchPathObservationStage.GlobalRetention
                or SearchPathObservationStage.RetentionPoolFinal) && !_watched.Contains(observation.StateKey))
            return;
        // Global retention is serial, but explicitly watched transition events can arrive
        // from workers. Serialize only the diagnostic writer, never search or simulation.
        lock (_writeLock)
        {
            if (_written >= _limit)
                return;
            using IncrementalHash prefix = NewPrefix(observation.RootTurnSetupChoices);
            foreach (PlanAction action in observation.Actions) Append(prefix, action);
            _writer.WriteLine(JsonSerializer.Serialize(new
            {
                prefix = Convert.ToHexString(prefix.GetCurrentHash()), observation,
            }, Json));
            _written++;
        }
    }

    public void WriteSelectedPath(string directory, SolverResult result)
    {
        // Match executable prefixes, including all card state/choice/target fields and
        // root choices. Presentation-only relic annotations can be added by final replay.
        // Matching is scoped to the same root; these are witnesses, never optimal labels.
        using IncrementalHash prefix = NewPrefix(result.TurnSetupChoices);
        List<string> prefixes = [Convert.ToHexString(prefix.GetCurrentHash())];
        foreach (PlanAction action in result.BestNode.Actions)
        {
            Append(prefix, action);
            prefixes.Add(Convert.ToHexString(prefix.GetCurrentHash()));
        }
        using FileStream stream = new(Path.Combine(directory, "ordering-selected-prefixes.json"),
            FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, prefixes, Json);
    }

    private static IncrementalHash NewPrefix(IReadOnlyList<PlanCardChoice> choices)
    {
        IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(choices, Json));
        return hash;
    }

    private static void Append(IncrementalHash hash, PlanAction action)
        => hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(action with
        { RelicEffects = null, CardTitle = "", TargetName = "", PotionTitle = "" }, Json));

    public void Dispose() => _writer.Dispose();
}
