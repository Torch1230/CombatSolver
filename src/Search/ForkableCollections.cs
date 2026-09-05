using System.Collections;

namespace CombatSolver;

internal sealed class ForkableList<T> : IReadOnlyList<T>
{
    private sealed class Storage(List<T> values)
    {
        public List<T> Values { get; } = values;
        public volatile bool Shared;
    }

    private Storage _storage;

    public ForkableList()
        : this(new List<T>())
    {
    }

    public ForkableList(IEnumerable<T> values)
        : this(new List<T>(values))
    {
    }

    private ForkableList(List<T> values)
        => _storage = new Storage(values);

    private ForkableList(Storage storage)
        => _storage = storage;

    public int Count => _storage.Values.Count;
    public T this[int index]
    {
        get => _storage.Values[index];
        set
        {
            EnsureWritable();
            _storage.Values[index] = value;
        }
    }

    public ForkableList<T> Fork()
    {
        _storage.Shared = true;
        return new ForkableList<T>(_storage);
    }

    public void Add(T value)
    {
        EnsureWritable();
        _storage.Values.Add(value);
    }

    public void Insert(int index, T value)
    {
        EnsureWritable();
        _storage.Values.Insert(index, value);
    }

    public bool Remove(T value)
    {
        // 未命中的 Remove 不得触发写时复制，所以只有共享存储才需要先探测一次。
        // 独占存储直接删，省掉 Contains + Remove 的双次线性查找。
        if (_storage.Shared)
        {
            if (!_storage.Values.Contains(value))
                return false;
            EnsureWritable();
        }
        return _storage.Values.Remove(value);
    }

    public void RemoveAt(int index)
    {
        EnsureWritable();
        _storage.Values.RemoveAt(index);
    }

    public int IndexOf(T value) => _storage.Values.IndexOf(value);
    public bool Contains(T value) => _storage.Values.Contains(value);
    public List<T>.Enumerator GetEnumerator() => _storage.Values.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void EnsureWritable()
    {
        if (!_storage.Shared)
            return;
        _storage = new Storage(new List<T>(_storage.Values));
    }
}

internal sealed class ForkableDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    private sealed class Storage(Dictionary<TKey, TValue> values)
    {
        public Dictionary<TKey, TValue> Values { get; } = values;
        public volatile bool Shared;
    }

    private Storage _storage;

    public ForkableDictionary()
        : this(new Dictionary<TKey, TValue>())
    {
    }

    public ForkableDictionary(IEqualityComparer<TKey> comparer)
        : this(new Dictionary<TKey, TValue>(comparer))
    {
    }

    private ForkableDictionary(Dictionary<TKey, TValue> values)
    {
        _storage = new Storage(values);
    }

    private ForkableDictionary(Storage storage)
    {
        _storage = storage;
    }

    public int Count => _storage.Values.Count;
    public IEnumerable<TKey> Keys => _storage.Values.Keys;
    public IEnumerable<TValue> Values => _storage.Values.Values;
    public IEqualityComparer<TKey> Comparer => _storage.Values.Comparer;

    public TValue this[TKey key]
    {
        get => _storage.Values[key];
        set
        {
            EnsureWritable();
            _storage.Values[key] = value;
        }
    }

    public ForkableDictionary<TKey, TValue> Fork()
    {
        _storage.Shared = true;
        return new ForkableDictionary<TKey, TValue>(_storage);
    }

    public void Add(TKey key, TValue value)
    {
        EnsureWritable();
        _storage.Values.Add(key, value);
    }

    public bool Remove(TKey key)
    {
        // 未命中的 Remove 不得触发写时复制，所以只有共享存储才需要先探测一次。
        if (_storage.Shared)
        {
            if (!_storage.Values.ContainsKey(key))
                return false;
            EnsureWritable();
        }
        return _storage.Values.Remove(key);
    }

    public void Clear()
    {
        if (_storage.Values.Count == 0)
            return;
        EnsureWritable();
        _storage.Values.Clear();
    }

    public bool ContainsKey(TKey key) => _storage.Values.ContainsKey(key);

    public bool TryGetValue(TKey key, out TValue value) => _storage.Values.TryGetValue(key, out value!);

    public TValue GetValueOrDefault(TKey key) => _storage.Values.GetValueOrDefault(key)!;

    public TValue GetValueOrDefault(TKey key, TValue defaultValue)
        => _storage.Values.GetValueOrDefault(key, defaultValue);

    public Dictionary<TKey, TValue>.Enumerator GetEnumerator() => _storage.Values.GetEnumerator();

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
        => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void EnsureWritable()
    {
        if (!_storage.Shared)
            return;
        _storage = new Storage(new Dictionary<TKey, TValue>(_storage.Values, _storage.Values.Comparer));
    }
}

internal sealed class ForkableSet<T> : ISet<T>, IReadOnlySet<T>
{
    private sealed class Storage(HashSet<T> values)
    {
        public HashSet<T> Values { get; } = values;
        public volatile bool Shared;
    }

    private Storage _storage;

    public ForkableSet()
        : this(new HashSet<T>())
    {
    }

    public ForkableSet(IEqualityComparer<T> comparer)
        : this(new HashSet<T>(comparer))
    {
    }

    public ForkableSet(IEnumerable<T> values)
        : this(new HashSet<T>(values))
    {
    }

    private ForkableSet(HashSet<T> values)
    {
        _storage = new Storage(values);
    }

    private ForkableSet(Storage storage)
    {
        _storage = storage;
    }

    public int Count => _storage.Values.Count;
    public bool IsReadOnly => false;
    public IEqualityComparer<T> Comparer => _storage.Values.Comparer;

    public ForkableSet<T> Fork()
    {
        _storage.Shared = true;
        return new ForkableSet<T>(_storage);
    }

    public bool Add(T value)
    {
        // 重复的 Add 不得触发写时复制，所以只有共享存储才需要先探测一次。
        // 独占存储直接加，省掉 Contains + Add 的双次哈希查找。
        if (_storage.Shared)
        {
            if (_storage.Values.Contains(value))
                return false;
            EnsureWritable();
        }
        return _storage.Values.Add(value);
    }

    void ICollection<T>.Add(T item) => Add(item);

    public bool Remove(T value)
    {
        // 未命中的 Remove 不得触发写时复制，所以只有共享存储才需要先探测一次。
        if (_storage.Shared)
        {
            if (!_storage.Values.Contains(value))
                return false;
            EnsureWritable();
        }
        return _storage.Values.Remove(value);
    }

    public void Clear()
    {
        if (_storage.Values.Count == 0)
            return;
        EnsureWritable();
        _storage.Values.Clear();
    }

    public bool Contains(T item) => _storage.Values.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _storage.Values.CopyTo(array, arrayIndex);
    public void ExceptWith(IEnumerable<T> other)
    {
        EnsureWritable();
        _storage.Values.ExceptWith(other);
    }
    public void IntersectWith(IEnumerable<T> other)
    {
        EnsureWritable();
        _storage.Values.IntersectWith(other);
    }
    public bool IsProperSubsetOf(IEnumerable<T> other) => _storage.Values.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<T> other) => _storage.Values.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<T> other) => _storage.Values.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<T> other) => _storage.Values.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<T> other) => _storage.Values.Overlaps(other);
    public bool SetEquals(IEnumerable<T> other) => _storage.Values.SetEquals(other);
    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        EnsureWritable();
        _storage.Values.SymmetricExceptWith(other);
    }
    public void UnionWith(IEnumerable<T> other)
    {
        EnsureWritable();
        _storage.Values.UnionWith(other);
    }
    public HashSet<T>.Enumerator GetEnumerator() => _storage.Values.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void EnsureWritable()
    {
        if (!_storage.Shared)
            return;
        _storage = new Storage(new HashSet<T>(_storage.Values, _storage.Values.Comparer));
    }
}
