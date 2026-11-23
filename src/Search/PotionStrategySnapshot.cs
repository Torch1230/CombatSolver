namespace CombatSolver;

internal enum SolverPotionDirective
{
    Smart,
    Force,
    Disabled,
}

internal enum PotionStrategyPreset
{
    AllSmart,
    AllProtected,
    AllForced,
    OnlyForced,
}

internal readonly struct PotionHpThresholds : IEquatable<PotionHpThresholds>
{
    public const int DefaultStandard = 9;
    public const int DefaultElevated = 14;
    public const int DefaultHigh = 18;
    public const int MinValue = 0;
    public const int MaxValue = 100;
    public const decimal AmbergrisMaxHpFraction = 0.40m;

    private readonly KeyValuePair<string, int>[]? _overrides;

    private PotionHpThresholds(KeyValuePair<string, int>[]? overrides) => _overrides = overrides;

    public static PotionHpThresholds Default => default;

    public bool IsEmpty => _overrides is null || _overrides.Length == 0;

    public int Count => _overrides?.Length ?? 0;

    public IReadOnlyList<KeyValuePair<string, int>> Overrides
        => (IReadOnlyList<KeyValuePair<string, int>>?)_overrides ?? Array.Empty<KeyValuePair<string, int>>();

    public bool TryGetOverride(string? potionId, out int thresholdHp)
    {
        if (string.IsNullOrEmpty(potionId) || _overrides is null)
        {
            thresholdHp = 0;
            return false;
        }
        int index = IndexOf(potionId);
        if (index < 0)
        {
            thresholdHp = 0;
            return false;
        }
        thresholdHp = _overrides[index].Value;
        return true;
    }

    public PotionHpThresholds WithOverride(string potionId, int thresholdHp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(potionId);
        int clamped = Math.Clamp(thresholdHp, MinValue, MaxValue);
        int index = IndexOf(potionId);
        if (index >= 0)
        {
            if (_overrides![index].Value == clamped)
                return this;
            KeyValuePair<string, int>[] replaced = (KeyValuePair<string, int>[])_overrides.Clone();
            replaced[index] = new KeyValuePair<string, int>(potionId, clamped);
            return new PotionHpThresholds(replaced);
        }

        int insertAt = ~index;
        KeyValuePair<string, int>[] grown = new KeyValuePair<string, int>[(_overrides?.Length ?? 0) + 1];
        if (_overrides is not null)
        {
            Array.Copy(_overrides, grown, insertAt);
            Array.Copy(_overrides, insertAt, grown, insertAt + 1, _overrides.Length - insertAt);
        }
        grown[insertAt] = new KeyValuePair<string, int>(potionId, clamped);
        return new PotionHpThresholds(grown);
    }

    public PotionHpThresholds WithoutOverride(string potionId)
    {
        if (string.IsNullOrEmpty(potionId) || _overrides is null)
            return this;
        int index = IndexOf(potionId);
        if (index < 0)
            return this;
        if (_overrides.Length == 1)
            return default;
        KeyValuePair<string, int>[] shrunk = new KeyValuePair<string, int>[_overrides.Length - 1];
        Array.Copy(_overrides, shrunk, index);
        Array.Copy(_overrides, index + 1, shrunk, index, shrunk.Length - index);
        return new PotionHpThresholds(shrunk);
    }

    public Dictionary<string, int>? ToDictionary()
    {
        if (_overrides is null || _overrides.Length == 0)
            return null;
        Dictionary<string, int> dict = new(_overrides.Length, StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> entry in _overrides)
            dict[entry.Key] = entry.Value;
        return dict;
    }

    public static PotionHpThresholds FromDictionary(IReadOnlyDictionary<string, int>? source)
    {
        if (source is null || source.Count == 0)
            return default;
        List<KeyValuePair<string, int>> entries = new(source.Count);
        foreach (KeyValuePair<string, int> entry in source)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                continue;
            entries.Add(new KeyValuePair<string, int>(
                entry.Key,
                Math.Clamp(entry.Value, MinValue, MaxValue)));
        }
        if (entries.Count == 0)
            return default;
        entries.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return new PotionHpThresholds([.. entries]);
    }

    public static void ValidateDictionary(IReadOnlyDictionary<string, int>? source)
    {
        if (source is null)
            return;
        foreach (KeyValuePair<string, int> entry in source)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                throw new InvalidDataException("Potion HP threshold override key must not be empty.");
            if (entry.Value is < MinValue or > MaxValue)
            {
                throw new InvalidDataException(
                    $"Potion HP threshold override {entry.Key} must be between {MinValue} and {MaxValue}.");
            }
        }
    }

    public static int DefaultAmbergrisCost(int maximumHp)
    {
        int fracCost = maximumHp > 0
            ? (int)Math.Ceiling(maximumHp * AmbergrisMaxHpFraction)
            : 0;
        return Math.Max(DefaultHigh, fracCost);
    }

    private int IndexOf(string potionId)
    {
        if (_overrides is null)
            return -1;
        int low = 0;
        int high = _overrides.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int cmp = string.CompareOrdinal(_overrides[middle].Key, potionId);
            if (cmp == 0)
                return middle;
            if (cmp < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return ~low;
    }

    public bool Equals(PotionHpThresholds other)
    {
        if (_overrides is null || other._overrides is null)
            return IsEmpty && other.IsEmpty;
        if (ReferenceEquals(_overrides, other._overrides))
            return true;
        if (_overrides.Length != other._overrides.Length)
            return false;
        for (int i = 0; i < _overrides.Length; i++)
        {
            if (!string.Equals(_overrides[i].Key, other._overrides[i].Key, StringComparison.Ordinal)
                || _overrides[i].Value != other._overrides[i].Value)
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is PotionHpThresholds other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        if (_overrides is not null)
        {
            foreach (KeyValuePair<string, int> entry in _overrides)
            {
                hash.Add(entry.Key, StringComparer.Ordinal);
                hash.Add(entry.Value);
            }
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(PotionHpThresholds left, PotionHpThresholds right) => left.Equals(right);
    public static bool operator !=(PotionHpThresholds left, PotionHpThresholds right) => !left.Equals(right);
}

internal readonly record struct PotionSlotDirective(
    int Slot,
    string PotionId,
    SolverPotionDirective Directive);

internal sealed class PotionStrategySnapshot
{
    private readonly Dictionary<(int Slot, string PotionId), SolverPotionDirective> _directives;

    public PotionStrategySnapshot(
        SolverPotionPolicy defaultPolicy,
        IEnumerable<PotionSlotDirective> directives)
    {
        DefaultPolicy = defaultPolicy;
        _directives = directives.ToDictionary(
            directive => (directive.Slot, directive.PotionId),
            directive => directive.Directive);
        Directives = _directives
            .Select(item => new PotionSlotDirective(item.Key.Slot, item.Key.PotionId, item.Value))
            .OrderBy(item => item.Slot)
            .ToArray();
    }

    public SolverPotionPolicy DefaultPolicy { get; }
    public IReadOnlyList<PotionSlotDirective> Directives { get; }
    public PotionHpThresholds Thresholds { get; init; } = PotionHpThresholds.Default;

    public bool HasForcedDirectives
        => Directives.Any(directive => directive.Directive == SolverPotionDirective.Force);

    public int ForcedDirectiveCount
        => Directives.Count(directive => directive.Directive == SolverPotionDirective.Force);

    public SolverPotionDirective Resolve(int slot, string potionId)
        => _directives.GetValueOrDefault(
            (slot, potionId),
            DefaultPolicy == SolverPotionPolicy.Disabled
                ? SolverPotionDirective.Disabled
                : SolverPotionDirective.Smart);

    public string DescribeForcedUses()
        => string.Join(", ", Directives
            .Where(directive => directive.Directive == SolverPotionDirective.Force)
            .Select(directive => $"{directive.PotionId}@{directive.Slot}"));
}
