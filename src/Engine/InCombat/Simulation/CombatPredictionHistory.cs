using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

/// <summary>
/// Stores prediction-only combat events in simulation order without touching live combat history.
/// Deferred events use separate original and resolved entries; the resolved entry carries the final snapshot and
/// risk boundary while the original entry determines semantic order.
/// </summary>
internal sealed class CombatPredictionHistory(PredictionTrace trace)
    : IReadOnlyList<CombatPredictionHistoryEntry>
{
    public readonly struct HistoryEntryRange
    {
        private readonly CombatPredictionHistory _history;
        private readonly int _startIndex;
        private readonly List<CombatPredictionHistoryEntry>? _tail;
        private readonly int _tailOffset;

        internal HistoryEntryRange(
            CombatPredictionHistory history,
            int startIndex,
            int count,
            List<CombatPredictionHistoryEntry>? tail,
            int tailOffset)
        {
            _history = history;
            _startIndex = startIndex;
            Count = count;
            _tail = tail;
            _tailOffset = tailOffset;
        }

        public int Count { get; }

        public CombatPredictionHistoryEntry this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return _tail is null
                    ? _history[_startIndex + index]
                    : _tail[_tailOffset + index];
            }
        }

        public Enumerator GetEnumerator() => new(this);

        public struct Enumerator(HistoryEntryRange range)
        {
            private int _index = -1;

            public CombatPredictionHistoryEntry Current => range[_index];

            public bool MoveNext() => ++_index < range.Count;
        }
    }

    private HistorySegment? _prefix;
    // 前缀段是从叶到根的链表，每次枚举都要先把它翻过来。段链只在 SealTail 时变长，
    // 所以把翻好的顺序缓存下来；SealTail 换新数组而不是就地改，正在跑的迭代器仍看到
    // 它启动时抓到的那一份，与原来"枚举开始时建一个 Stack"的可见性完全一致。
    private HistorySegment[]? _orderedSegments;
    private List<CombatPredictionHistoryEntry>? _tail;
    // 上一段封存时的条目数。同一条搜索里相邻两段长度接近，用它给可变尾表预留容量，
    // 省掉从 0 开始的多轮扩容。容量只影响分配，不影响任何内容或顺序。
    private int _tailCapacityHint;
    private Dictionary<CombatPredictionHistoryEntry, CombatPredictionHistoryEntry>? _tailCompletions;
    private int _pendingDeferredEntries;
    private ulong _riskSignatureFirst;
    private ulong _riskSignatureSecond;
    private int _riskEntryCount;
    private int _cardDrawnEntryCount;
    private int _orbChanneledEntryCount;

    private sealed class HistorySegment(
        HistorySegment? parent,
        CombatPredictionHistoryEntry[] entries,
        Dictionary<CombatPredictionHistoryEntry, CombatPredictionHistoryEntry>? completions)
    {
        public HistorySegment? Parent { get; } = parent;
        public CombatPredictionHistoryEntry[] Entries { get; } = entries;
        public Dictionary<CombatPredictionHistoryEntry, CombatPredictionHistoryEntry>? Completions { get; } = completions;
        public int Count { get; } = (parent?.Count ?? 0) + entries.Length;
    }

    private CombatPredictionHistory(
        PredictionTrace trace,
        HistorySegment? prefix,
        ulong riskSignatureFirst,
        ulong riskSignatureSecond,
        int riskEntryCount,
        int cardDrawnEntryCount,
        int orbChanneledEntryCount,
        int tailCapacityHint)
        : this(trace)
    {
        _prefix = prefix;
        _tailCapacityHint = tailCapacityHint;
        _riskSignatureFirst = riskSignatureFirst;
        _riskSignatureSecond = riskSignatureSecond;
        _riskEntryCount = riskEntryCount;
        _cardDrawnEntryCount = cardDrawnEntryCount;
        _orbChanneledEntryCount = orbChanneledEntryCount;
    }

    public IReadOnlyList<CombatPredictionHistoryEntry> Entries => this;

    private int EntryCount => (_prefix?.Count ?? 0) + (_tail?.Count ?? 0);

    /// <summary>
    /// Captures the current suffix beginning at <paramref name="startIndex"/>.
    /// Ranges wholly inside the mutable tail enumerate without walking the persistent prefix.
    /// </summary>
    public HistoryEntryRange EntriesFrom(int startIndex)
        => EntriesBetween(startIndex, EntryCount);

    /// <summary>
    /// Captures the current half-open history range [<paramref name="startIndex"/>,
    /// <paramref name="endExclusive"/>).
    /// </summary>
    public HistoryEntryRange EntriesBetween(int startIndex, int endExclusive)
    {
        int entryCount = EntryCount;
        if ((uint)startIndex > (uint)entryCount)
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (endExclusive < startIndex || endExclusive > entryCount)
            throw new ArgumentOutOfRangeException(nameof(endExclusive));

        int prefixCount = _prefix?.Count ?? 0;
        bool whollyInTail = startIndex >= prefixCount && _tail is not null;
        return new HistoryEntryRange(
            this,
            startIndex,
            endExclusive - startIndex,
            whollyInTail ? _tail : null,
            whollyInTail ? startIndex - prefixCount : 0);
    }

    int IReadOnlyCollection<CombatPredictionHistoryEntry>.Count => EntryCount;

    public CombatPredictionHistoryEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)EntryCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            int prefixCount = _prefix?.Count ?? 0;
            if (index >= prefixCount)
                return _tail![index - prefixCount];

            HistorySegment segment = _prefix!;
            while (segment.Parent is { } parent && index < parent.Count)
                segment = parent;
            int segmentStart = segment.Parent?.Count ?? 0;
            return segment.Entries[index - segmentStart];
        }
    }

    public IEnumerable<TEntry> OfType<TEntry>()
        where TEntry : CombatPredictionHistoryEntry
        => Enumerable.OfType<TEntry>(this);

    public int Count<TEntry>()
        where TEntry : CombatPredictionHistoryEntry
    {
        Type type = typeof(TEntry);
        if (type == typeof(CombatPredictionRiskEntry))
            return _riskEntryCount;
        if (type == typeof(CombatPredictionCardDrawnEntry))
            return _cardDrawnEntryCount;
        if (type == typeof(CombatPredictionOrbChanneledEntry))
            return _orbChanneledEntryCount;
        throw new NotSupportedException($"Prediction history has no fixed counter for {type.FullName}.");
    }

    /// <summary>
    /// Aggregates risk through the latest supplied relevant entry.
    /// </summary>
    /// <remarks>Every supplied entry is expected to belong to this history; an empty sequence yields no risk.</remarks>
    public PredictionRisk GetRisk(IEnumerable<CombatPredictionHistoryEntry> entries)
    {
        CombatPredictionHistoryEntry? lastEntry = null;
        foreach (var entry in entries)
        {
            ValidateOwnership(entry);
            if (lastEntry is null || entry.Index > lastEntry.Index)
            {
                lastEntry = entry;
            }
        }

        return lastEntry is null
            ? PredictionRisk.None
            : GetRiskThrough(lastEntry.Index);
    }

    /// <summary>
    /// Aggregates risk through the current end of the timeline.
    /// </summary>
    public PredictionRisk GetCurrentRisk()
    {
        return EntryCount == 0 ? PredictionRisk.None : GetRiskThrough(EntryCount - 1);
    }

    public bool HasRisk => _riskEntryCount > 0;

    internal PredictionRiskSignature RiskSignature
        => new(_riskSignatureFirst, _riskSignatureSecond, _riskEntryCount);

    public void RecordRisk(PredictionRiskReason reason)
    {
        Record(new CombatPredictionRiskEntry { Reason = reason });
        AppendRiskSignature(reason);
    }

    public void CardAfflicted(PredictedCard card, AfflictionModel affliction)
    {
        Record(new CombatPredictionCardAfflictedEntry
        {
            Card = CombatPredictionCardSnapshot.Capture(card),
            Affliction = affliction
        });
    }

    public CombatPredictionCardDrawnEntry CardDrawn(PredictedCard card, bool fromHandDraw)
    {
        _pendingDeferredEntries++;
        return Record(new CombatPredictionCardDrawnEntry
        {
            Card = CombatPredictionCardSnapshot.Capture(card),
            FromHandDraw = fromHandDraw
        });
    }

    public void CardDrawResolved(CombatPredictionCardDrawnEntry originalEntry, PredictedCard card)
    {
        Complete(originalEntry, new CombatPredictionCardDrawResolvedEntry
        {
            OriginalEntry = originalEntry,
            Card = CombatPredictionCardSnapshot.Capture(card)
        });
    }

    public void CardCostsRandomized(IReadOnlyList<PredictedCard> cards)
    {
        Record(new CombatPredictionCardCostsRandomizedEntry { Cards = SnapshotCards(cards) });
    }

    public void CardsSelected(IReadOnlyList<PredictedCard> cards)
    {
        Record(new CombatPredictionCardsSelectedEntry { Cards = SnapshotCards(cards) });
    }

    public void CardPlayStarted(PredictedCard card, CardPlay cardPlay)
    {
        Record(new CombatPredictionCardPlayStartedEntry
        {
            Card = card,
            CardPlay = cardPlay
        });
    }

    public void CardPlayFinished(PredictedCard card, CardPlay cardPlay, bool wasEthereal)
    {
        Record(new CombatPredictionCardPlayFinishedEntry
        {
            Card = card,
            CardPlay = cardPlay,
            WasEthereal = wasEthereal
        });
    }

    public CombatPredictionCardGeneratedEntry CardGenerated(
        PredictedCard card,
        Player? creator,
        CardGenerationResultKind resultKind)
    {
        _pendingDeferredEntries++;
        return Record(new CombatPredictionCardGeneratedEntry
        {
            Card = CombatPredictionCardSnapshot.Capture(card),
            Creator = creator,
            ResultKind = resultKind
        });
    }

    public void CardGenerationResolved(CombatPredictionCardGeneratedEntry originalEntry, PredictedCard card)
    {
        Complete(originalEntry, new CombatPredictionCardGenerationResolvedEntry
        {
            OriginalEntry = originalEntry,
            Card = CombatPredictionCardSnapshot.Capture(card)
        });
    }

    public void CardGenerationOptions(IReadOnlyList<PredictedCard> cards)
    {
        Record(new CombatPredictionCardGenerationOptionsEntry
        {
            Cards = SnapshotCards(cards),
            // Choice generators hand ownership of these prediction-only cards to history.
            // The entry is immutable after publication; a selected option is cloned only
            // when it is materialized into a branch's combat state.
            Options = cards.ToArray(),
        });
    }

    public void AutoPlayFromDrawPile(PredictedCard card)
    {
        Record(new CombatPredictionAutoPlayFromDrawPileEntry
        {
            Card = CombatPredictionCardSnapshot.Capture(card)
        });
    }

    public void PotionGenerated(PotionModel potion)
    {
        Record(new CombatPredictionPotionGeneratedEntry { Potion = potion });
    }

    public void CreatureAttacked(
        Creature attacker,
        IReadOnlyList<DamageResult> hitResults)
    {
        Record(new CombatPredictionCreatureAttackedEntry
        {
            Attacker = attacker,
            HitResults = hitResults
        });
    }

    public void DamageReceived(Creature receiver, Creature? dealer, DamageResult result, PredictedCard? cardSource)
    {
        Record(new CombatPredictionDamageReceivedEntry
        {
            Receiver = receiver,
            Result = result,
            Dealer = dealer,
            CardSource = cardSource
        });
    }

    public void OrbChanneled(OrbModel orb)
    {
        Record(new CombatPredictionOrbChanneledEntry { Orb = orb });
    }

    /// <summary>
    /// Returns the resolution paired with one exact deferred started-entry instance.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the entry is unresolved, belongs to another history, or has a different resolution type.
    /// </exception>
    public TResolved GetResolvedEntry<TResolved>(CombatPredictionHistoryEntry originalEntry)
        where TResolved : CombatPredictionHistoryEntry
    {
        if (!TryGetCompletion(originalEntry, out CombatPredictionHistoryEntry? resolvedEntry))
            throw new InvalidOperationException("The deferred history entry has not been resolved.");

        return resolvedEntry as TResolved
            ?? throw new InvalidOperationException("The deferred history entry has an invalid resolution type.");
    }

    private TEntry Record<TEntry>(TEntry entry)
        where TEntry : CombatPredictionHistoryEntry
    {
        entry.Index = EntryCount;
        entry.Trace = trace.Current;
        (_tail ??= new List<CombatPredictionHistoryEntry>(_tailCapacityHint)).Add(entry);
        if (entry is CombatPredictionCardDrawnEntry)
            _cardDrawnEntryCount++;
        else if (entry is CombatPredictionOrbChanneledEntry)
            _orbChanneledEntryCount++;
        return entry;
    }

    private static IReadOnlyList<CombatPredictionCardSnapshot> SnapshotCards(IEnumerable<PredictedCard> cards)
    {
        return [.. cards.Select(CombatPredictionCardSnapshot.Capture)];
    }

    private void Complete(CombatPredictionHistoryEntry originalEntry, CombatPredictionHistoryEntry resolvedEntry)
    {
        ValidateOwnership(originalEntry);
        if (TryGetCompletion(originalEntry, out _))
        {
            throw new InvalidOperationException("The deferred history entry has already been resolved.");
        }

        Record(resolvedEntry);
        (_tailCompletions ??= new(ReferenceEqualityComparer.Instance)).Add(originalEntry, resolvedEntry);
        _pendingDeferredEntries--;
    }

    private void ValidateOwnership(CombatPredictionHistoryEntry entry)
    {
        var index = entry.Index;
        if (index < 0 || index >= EntryCount || !ReferenceEquals(this[index], entry))
        {
            throw new InvalidOperationException("The history entry does not belong to this history.");
        }
    }

    private CombatPredictionRisk GetRiskThrough(int boundaryIndex)
    {
        return new CombatPredictionRisk([.. this
            .Take(boundaryIndex + 1)
            .OfType<CombatPredictionRiskEntry>()]);
    }

    internal CombatPredictionHistory Fork(PredictionTrace forkTrace)
    {
        AssertForkable();
        SealTail();
        return new CombatPredictionHistory(
            forkTrace,
            _prefix,
            _riskSignatureFirst,
            _riskSignatureSecond,
            _riskEntryCount,
            _cardDrawnEntryCount,
            _orbChanneledEntryCount,
            _tailCapacityHint);
    }

    internal void AssertForkable()
    {
        if (_pendingDeferredEntries != 0)
            throw new InvalidOperationException("Cannot fork prediction history with unresolved deferred entries.");
    }

    public IEnumerator<CombatPredictionHistoryEntry> GetEnumerator()
    {
        if (_prefix is not null)
        {
            HistorySegment[] ordered = GetOrderedSegments();
            for (int segmentIndex = 0; segmentIndex < ordered.Length; segmentIndex++)
            {
                foreach (CombatPredictionHistoryEntry entry in ordered[segmentIndex].Entries)
                    yield return entry;
            }
        }
        if (_tail is not null)
        {
            foreach (CombatPredictionHistoryEntry entry in _tail)
                yield return entry;
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    // 原来每次枚举都新建一个 Stack（对象 + 逐轮扩容的数组）来把段链翻成从根到叶。
    // 段链只在 SealTail 变长，翻好的顺序缓存起来即可，元素与顺序完全一致。
    private HistorySegment[] GetOrderedSegments()
    {
        if (_orderedSegments is { } cached)
            return cached;
        int count = 0;
        for (HistorySegment? segment = _prefix; segment is not null; segment = segment.Parent)
            count++;
        HistorySegment[] ordered = new HistorySegment[count];
        int index = count;
        for (HistorySegment? segment = _prefix; segment is not null; segment = segment.Parent)
            ordered[--index] = segment;
        _orderedSegments = ordered;
        return ordered;
    }

    private bool TryGetCompletion(
        CombatPredictionHistoryEntry originalEntry,
        out CombatPredictionHistoryEntry? resolvedEntry)
    {
        resolvedEntry = _tailCompletions?.GetValueOrDefault(originalEntry);
        for (HistorySegment? segment = _prefix; resolvedEntry is null && segment is not null; segment = segment.Parent)
            resolvedEntry = segment.Completions?.GetValueOrDefault(originalEntry);
        return resolvedEntry is not null;
    }

    private void SealTail()
    {
        if (_tail is not { Count: > 0 })
            return;

        CombatPredictionHistoryEntry[] entries = _tail.ToArray();
        _prefix = new HistorySegment(
            _prefix,
            entries,
            _tailCompletions);
        _orderedSegments = null;
        _tailCapacityHint = entries.Length;
        _tail = null;
        _tailCompletions = null;
    }

    private void AppendRiskSignature(PredictionRiskReason reason)
    {
        AbstractModel? source = trace.Current?.Source;
        string sourceId = source?.Id.Entry ?? source?.GetType().FullName ?? "UNKNOWN";
        string method = trace.Current?.Invocation.Method?.Name
            ?? trace.Current?.Invocation.Action?.ToString()
            ?? "Unknown";
        ulong first = 1469598103934665603UL;
        ulong second = 1099511628211UL;
        HashText(sourceId, ref first, ref second);
        HashText(method, ref first, ref second);
        first = (first ^ (uint)reason) * 1099511628211UL;
        second = (second + (uint)reason + 0x9e3779b97f4a7c15UL) * 0xbf58476d1ce4e5b9UL;
        _riskSignatureFirst += Mix(first);
        _riskSignatureSecond += Mix(second);
        _riskEntryCount++;
    }

    private static void HashText(string text, ref ulong first, ref ulong second)
    {
        foreach (char value in text)
        {
            first = (first ^ value) * 1099511628211UL;
            second = (second + value + 0x9e3779b97f4a7c15UL) * 0xbf58476d1ce4e5b9UL;
        }
        first ^= 0xffUL;
        second ^= 0x94d049bb133111ebUL;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
}

internal readonly record struct PredictionRiskSignature(ulong First, ulong Second, int EntryCount);
