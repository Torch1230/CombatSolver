namespace CombatSolver;

/// <summary>
/// An immutable, ordered listener snapshot. Wide snapshots share unchanged pages across
/// preview replacements and Fork remapping. An owned rebuild list is paged only when a
/// changed branch needs it; small snapshots retain flat storage.
/// </summary>
internal sealed class ImmutableListenerList<T> : IReadOnlyList<T> where T : class
{
    private const int PageShift = 5;
    private const int PageSize = 1 << PageShift;
    private const int PageMask = PageSize - 1;
    // A privately owned List<T>, or immutable pages. Promotion changes only the representation;
    // every published storage object keeps the same ordered identities forever.
    private object _storage;

    private ImmutableListenerList(object storage, int count)
    {
        _storage = storage;
        Count = count;
    }

    public int Count { get; }

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            object storage = Volatile.Read(ref _storage);
            return storage is List<T> owned
                ? owned[index]
                : ((T[][])storage)[index >> PageShift][index & PageMask];
        }
    }

    public static IReadOnlyList<T> Capture(List<T> source)
    {
        if (source.Count <= PageSize)
            return source.ToArray();
        return new ImmutableListenerList<T>(CreatePages(source), source.Count);
    }

    /// <summary>
    /// Publishes a private rebuild buffer without copying it. The caller transfers ownership
    /// and must never mutate the list again. This does not change Capture's copying contract.
    /// </summary>
    public static IReadOnlyList<T> TakeOwnership(List<T> source)
        => source.Count <= PageSize
            ? source
            : new ImmutableListenerList<T>(source, source.Count);

    private static T[][] CreatePages(List<T> source)
    {
        T[][] pages = new T[(source.Count + PageMask) >> PageShift][];
        for (int page = 0; page < pages.Length; page++)
        {
            int start = page << PageShift;
            T[] entries = new T[Math.Min(PageSize, source.Count - start)];
            source.CopyTo(start, entries, 0, entries.Length);
            pages[page] = entries;
        }
        return pages;
    }

    private T[][] GetOrCreatePages()
    {
        object storage = Volatile.Read(ref _storage);
        if (storage is T[][] pages)
            return pages;
        T[][] promoted = CreatePages((List<T>)storage);
        object previous = Interlocked.CompareExchange(ref _storage, promoted, storage);
        // Sibling forks can race to promote, but publish only one equivalent page table. The
        // winning replacement also releases this snapshot's reference to the old rebuild List.
        return ReferenceEquals(previous, storage) ? promoted : (T[][])previous;
    }

    public static IReadOnlyList<T> Replace(
        IReadOnlyList<T> source,
        int start,
        ReadOnlySpan<T> replacements)
    {
        if (start < 0 || start > source.Count - replacements.Length)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (source is not ImmutableListenerList<T> paged)
        {
            T[]? changed = null;
            for (int index = 0; index < replacements.Length; index++)
            {
                if (ReferenceEquals(source[start + index], replacements[index]))
                    continue;
                changed ??= source.ToArray();
                changed[start + index] = replacements[index];
            }
            return changed ?? source;
        }

        T[][]? originalPages = null;
        T[][]? pages = null;
        for (int index = 0; index < replacements.Length; index++)
        {
            int position = start + index;
            T replacement = replacements[index];
            if (ReferenceEquals(source[position], replacement))
                continue;
            originalPages ??= paged.GetOrCreatePages();
            pages ??= (T[][])originalPages.Clone();
            int page = position >> PageShift;
            if (ReferenceEquals(pages[page], originalPages[page]))
                pages[page] = (T[])pages[page].Clone();
            pages[page][position & PageMask] = replacement;
        }
        return pages is null ? source : new ImmutableListenerList<T>(pages, source.Count);
    }

    public static IReadOnlyList<T> Remap<TContext>(
        IReadOnlyList<T> source,
        TContext context,
        Func<TContext, T, T> remap)
    {
        if (source is not ImmutableListenerList<T> paged)
        {
            T[]? changed = null;
            for (int index = 0; index < source.Count; index++)
            {
                T value = source[index];
                T mapped = remap(context, value);
                if (ReferenceEquals(value, mapped))
                    continue;
                changed ??= source.ToArray();
                changed[index] = mapped;
            }
            return changed ?? source;
        }

        object storage = Volatile.Read(ref paged._storage);
        if (storage is List<T> owned)
            return RemapOwnedList(paged, owned, context, remap);

        T[][] originalPages = (T[][])storage;
        T[][]? pages = null;
        for (int page = 0; page < originalPages.Length; page++)
        {
            T[] entries = originalPages[page];
            for (int offset = 0; offset < entries.Length; offset++)
            {
                T value = entries[offset];
                T mapped = remap(context, value);
                if (ReferenceEquals(value, mapped))
                    continue;
                pages ??= (T[][])originalPages.Clone();
                if (ReferenceEquals(pages[page], entries))
                    pages[page] = (T[])entries.Clone();
                pages[page][offset] = mapped;
            }
        }
        return pages is null ? source : new ImmutableListenerList<T>(pages, source.Count);
    }

    private static IReadOnlyList<T> RemapOwnedList<TContext>(
        ImmutableListenerList<T> source,
        List<T> owned,
        TContext context,
        Func<TContext, T, T> remap)
    {
        T[][]? originalPages = null;
        T[][]? pages = null;
        for (int index = 0; index < owned.Count; index++)
        {
            T value = owned[index];
            T mapped = remap(context, value);
            if (ReferenceEquals(value, mapped))
                continue;
            originalPages ??= source.GetOrCreatePages();
            pages ??= (T[][])originalPages.Clone();
            int page = index >> PageShift;
            if (ReferenceEquals(pages[page], originalPages[page]))
                pages[page] = (T[])pages[page].Clone();
            pages[page][index & PageMask] = mapped;
        }
        return pages is null ? source : new ImmutableListenerList<T>(pages, source.Count);
    }

    public IEnumerator<T> GetEnumerator()
    {
        // Keep the captured storage alive for this enumeration even if a sibling branch
        // promotes the source snapshot while the caller is paused between MoveNext calls.
        object storage = Volatile.Read(ref _storage);
        if (storage is List<T> owned)
        {
            foreach (T listener in owned)
                yield return listener;
            yield break;
        }
        foreach (T[] page in (T[][])storage)
        {
            foreach (T listener in page)
                yield return listener;
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
