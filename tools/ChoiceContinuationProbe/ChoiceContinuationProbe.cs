// Diagnostic-only source. build.py injects this into a disposable source checkout.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class ChoiceContinuationProbe
{
    internal readonly record struct GroupKey(long ParentId, StateFingerprint Action);
    private sealed class Counts
    {
        public long ManualCalls, Completed, Pending, OwnVisits, DiscardVisits, FirstDiscardVisits;
        public long EligibleVisits, PrefixAllocated, PrefixTicks, ManualAllocated, ManualTicks;
        public long EligiblePrefixAllocated, EligiblePrefixTicks;
        public Dictionary<string, long> Shapes = [];
    }
    private sealed class Group
    {
        public string Card = "";
        public long Visits, PrefixAllocated, PrefixTicks;
        public long EligibleVisits, EligiblePrefixAllocated, EligiblePrefixTicks;
    }
    private sealed class Lane
    {
        public Dictionary<string, Counts> Cards = [];
        public Dictionary<GroupKey, Group> Groups = [];
        public GroupKey? ReplayGroup;
        public Manual? Current;
    }
    private sealed record ParentIdentity(long Id);
    private static readonly ConditionalWeakTable<object, ParentIdentity> ParentIds = new();
    private static long _nextParentId;
    private static readonly ThreadLocal<Lane> Lanes = new(() => new(), trackAllValues: true);
    internal sealed class ReplayScope(GroupKey? group) : IDisposable
    {
        private readonly Lane _lane = Lanes.Value!;
        private readonly GroupKey? _previous = Lanes.Value!.ReplayGroup;
        public void Start() => _lane.ReplayGroup = group;
        public void Dispose() => _lane.ReplayGroup = _previous;
    }
    public static ReplayScope EnterReplay(object parent, StateFingerprint action)
    {
        var result = new ReplayScope(new GroupKey(ParentIds.GetValue(parent, static _ => new(Interlocked.Increment(ref _nextParentId))).Id, action));
        result.Start();
        return result;
    }
    internal sealed class Manual : IDisposable
    {
        private readonly Lane _lane;
        private readonly Manual? _previous;
        private readonly CombatPredictionSimulator _simulator;
        private readonly PredictedCard _card;
        private readonly Counts _counts;
        private readonly long _startTicks, _startBytes;
        private long _overheadTicks, _overheadBytes;
        private bool _visited;
        internal int PlayCount;
        internal Manual(CombatPredictionSimulator simulator, PredictedCard card)
        {
            _lane = Lanes.Value!;
            _previous = _lane.Current;
            _lane.Current = this;
            _simulator = simulator;
            _card = card;
            string id = card.Preview.Id.Entry;
            if (!_lane.Cards.TryGetValue(id, out Counts? counts))
                _lane.Cards.Add(id, counts = new());
            _counts = counts;
            _counts.ManualCalls++;
            _startBytes = GC.GetAllocatedBytesForCurrentThread();
            _startTicks = Stopwatch.GetTimestamp();
        }
        public void AtChoice(CardChoiceSpec? spec, int consumedChoices, int executionDepth,
            int pendingPowerChanges, bool turnEndRequested)
        {
            long timestamp = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            _counts.OwnVisits++;
            if (spec is { Effect: PlanChoiceEffect.Discard, SourcePile: PileType.Hand })
            {
                _counts.DiscardVisits++;
                if (!_visited)
                {
                    _counts.FirstDiscardVisits++;
                    long prefixTicks = timestamp - _startTicks - _overheadTicks;
                    long prefixBytes = allocated - _startBytes - _overheadBytes;
                    _counts.PrefixTicks += prefixTicks;
                    _counts.PrefixAllocated += prefixBytes;
                    var blockers = _simulator.ContinuationProbeBlockers();
                    if (consumedChoices != 0) blockers.Add("EarlierChoiceConsumed");
                    if (executionDepth != 1) blockers.Add("NestedExecutionScope");
                    if (pendingPowerChanges != 0) blockers.Add("PendingPowerChanges");
                    if (turnEndRequested) blockers.Add("TurnEndRequested");
                    if (PlayCount != 1) blockers.Add("RepeatedPlayCount");
                    if (_card.Preview.CurrentPlayIndex != 0) blockers.Add("RepeatedPlayIndex");
                    if (_card.Preview.Enchantment != null) blockers.Add("Enchantment");
                    if (_card.Preview.Affliction != null) blockers.Add("Affliction");
                    if (_simulator.CurrentFrame?.Parent != null) blockers.Add("NestedTrace");
                    bool eligible = blockers.Count == 0;
                    if (eligible)
                    {
                        _counts.EligibleVisits++;
                        _counts.EligiblePrefixTicks += prefixTicks;
                        _counts.EligiblePrefixAllocated += prefixBytes;
                    }
                    foreach (string blocker in blockers)
                        _counts.Shapes[blocker] = _counts.Shapes.GetValueOrDefault(blocker) + 1;
                    foreach (string detail in new[] {
                        "EnchantmentType:" + (_card.Preview.Enchantment?.GetType().Name ?? "None"),
                        "AfflictionType:" + (_card.Preview.Affliction?.GetType().Name ?? "None"),
                        "BlockerSet:" + string.Join("+", blockers.Order()) })
                        _counts.Shapes[detail] = _counts.Shapes.GetValueOrDefault(detail) + 1;
                    string selectionShape = $"ChoiceCount:{spec.MinCount}..{spec.MaxCount}/Options:{spec.Options.Count}";
                    _counts.Shapes[selectionShape] = _counts.Shapes.GetValueOrDefault(selectionShape) + 1;
                    if (_lane.ReplayGroup is { } key)
                    {
                        if (!_lane.Groups.TryGetValue(key, out Group? group))
                            _lane.Groups.Add(key, group = new() { Card = _card.Preview.Id.Entry });
                        group.Visits++;
                        group.PrefixTicks += prefixTicks;
                        group.PrefixAllocated += prefixBytes;
                        if (eligible)
                        {
                            group.EligibleVisits++;
                            group.EligiblePrefixTicks += prefixTicks;
                            group.EligiblePrefixAllocated += prefixBytes;
                        }
                    }
                    _visited = true;
                }
            }
            _overheadBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
            _overheadTicks += Stopwatch.GetTimestamp() - timestamp;
        }
        public void Dispose()
        {
            _counts.ManualTicks += Stopwatch.GetTimestamp() - _startTicks - _overheadTicks;
            _counts.ManualAllocated += GC.GetAllocatedBytesForCurrentThread() - _startBytes - _overheadBytes;
            if (_simulator.HasPendingChoice) _counts.Pending++; else _counts.Completed++;
            _lane.Current = _previous;
        }
    }
    public static Manual? EnterManual(CombatPredictionSimulator simulator, PredictedCard card)
        => Lanes.Value!.ReplayGroup == null ? null : new(simulator, card);
    public static void SetPlayCount(int count)
    {
        if (Lanes.Value!.Current is { } current) current.PlayCount = count;
    }
    public static void AtChoice(CardChoiceSpec? spec, int consumedChoices, int executionDepth,
        int pendingPowerChanges, bool turnEndRequested)
        => Lanes.Value!.Current?.AtChoice(spec, consumedChoices, executionDepth,
            pendingPowerChanges, turnEndRequested);

    public static string Export()
    {
        var cards = new Dictionary<string, Counts>();
        var groups = new Dictionary<GroupKey, Group>();
        foreach (var lane in Lanes.Values)
        {
            foreach (var (id, source) in lane.Cards)
            {
                if (!cards.TryGetValue(id, out var target)) cards.Add(id, target = new());
                foreach (var field in typeof(Counts).GetFields())
                    if (field.FieldType == typeof(long))
                        field.SetValue(target, (long)field.GetValue(target)! + (long)field.GetValue(source)!);
                foreach (var (shape, count) in source.Shapes)
                    target.Shapes[shape] = target.Shapes.GetValueOrDefault(shape) + count;
            }
            foreach (var (key, source) in lane.Groups)
            {
                if (!groups.TryGetValue(key, out var target)) groups.Add(key, target = new() { Card = source.Card });
                target.Visits += source.Visits;
                target.PrefixTicks += source.PrefixTicks;
                target.PrefixAllocated += source.PrefixAllocated;
                target.EligibleVisits += source.EligibleVisits;
                target.EligiblePrefixTicks += source.EligiblePrefixTicks;
                target.EligiblePrefixAllocated += source.EligiblePrefixAllocated;
            }
        }
        return JsonSerializer.Serialize(new
        {
            StopwatchFrequency = Stopwatch.Frequency,
            Scope = "Instrumented manual-play prefix census; excludes Fork/snapshot. Not wall-time speedup. Eligibility is only an initial screen, not proof of safe suspension.",
            Cards = cards,
            Families = groups.Values.GroupBy(g => g.Card).ToDictionary(g => g.Key, g => new
            {
                Groups = g.Count(), Visits = g.Sum(x => x.Visits),
                RepeatedVisits = g.Sum(x => Math.Max(0, x.Visits - 1)),
                EligibleGroups = g.Count(x => x.EligibleVisits > 0),
                EligibleVisits = g.Sum(x => x.EligibleVisits),
                RepeatedEligibleVisits = g.Sum(x => Math.Max(0, x.EligibleVisits - 1)),
                MaxVisits = g.Max(x => x.Visits),
            }),
        }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true });
    }
}
