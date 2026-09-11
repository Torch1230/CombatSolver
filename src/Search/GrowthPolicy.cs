using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Models;
using Cards = MegaCrit.Sts2.Core.Models.Cards;
using Enchantments = MegaCrit.Sts2.Core.Models.Enchantments;

namespace CombatSolver;

internal enum GrowthSource
{
    HandOfGreed, TheHunt, Feed, Royalties, Alchemize, GeneticAlgorithm, TheScythe, Goopy, ForbiddenGrimoire,
}

/// <summary>
/// 成长向量里第三方来源那一半，按 <see cref="GrowthSourceMirrors"/> 登记的 id 存。
/// </summary>
/// <remarks>
/// <para>
/// 原版九个来源是 <see cref="GrowthValues"/> 上的九个 int 字段，走热路径；第三方来源数量不定，
/// 只能另开一处。这里用一个「按 id 序数升序、不存 0 值」的数组：为空时是 <c>null</c>，
/// 于是没有任何 mod 登记时，整个成长向量与开这个口子之前逐位相同。
/// </para>
/// <para>
/// 键用 id 而不是登记序号，是为了让<b>没登记的 id 也能原样留着</b>。额度存在设置文件里，
/// 玩家可能临时停用某个 mod；如果按序号存，停用期间写回设置就会把那份额度冲掉，重新启用后
/// 玩家得再填一遍。按 id 存的话，读进来认不出的条目照样带着走、照样写回去，只是这一局不生效。
/// </para>
/// </remarks>
internal readonly struct GrowthExtras : IEquatable<GrowthExtras>
{
    /// <summary>额度上限，与原版九个字段同一口径。</summary>
    private const int MaximumValue = 1000;

    private readonly KeyValuePair<string, int>[]? _entries;

    private GrowthExtras(KeyValuePair<string, int>[]? entries) => _entries = entries;

    /// <summary>一条都没有。空表时下游可以整段跳过。</summary>
    public bool IsEmpty => _entries is null;

    /// <summary>按 id 序数升序列出所有非 0 条目。</summary>
    public ReadOnlySpan<KeyValuePair<string, int>> Entries => _entries;

    public int Total
    {
        get
        {
            int total = 0;
            if (_entries is not null)
            {
                foreach (KeyValuePair<string, int> entry in _entries)
                    total = checked(total + entry.Value);
            }
            return total;
        }
    }

    public int Get(string id)
    {
        int index = IndexOf(id);
        return index < 0 ? 0 : _entries![index].Value;
    }

    public GrowthExtras With(string id, int value)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        int index = IndexOf(id);
        if (index >= 0)
        {
            if (value == _entries![index].Value)
                return this;
            if (value == 0)
            {
                if (_entries.Length == 1)
                    return default;
                KeyValuePair<string, int>[] shrunk = new KeyValuePair<string, int>[_entries.Length - 1];
                Array.Copy(_entries, shrunk, index);
                Array.Copy(_entries, index + 1, shrunk, index, shrunk.Length - index);
                return new GrowthExtras(shrunk);
            }
            KeyValuePair<string, int>[] replaced = (KeyValuePair<string, int>[])_entries.Clone();
            replaced[index] = new KeyValuePair<string, int>(id, value);
            return new GrowthExtras(replaced);
        }
        if (value == 0)
            return this;
        int insertAt = ~index;
        KeyValuePair<string, int>[] grown = new KeyValuePair<string, int>[(_entries?.Length ?? 0) + 1];
        if (_entries is not null)
        {
            Array.Copy(_entries, grown, insertAt);
            Array.Copy(_entries, insertAt, grown, insertAt + 1, _entries.Length - insertAt);
        }
        grown[insertAt] = new KeyValuePair<string, int>(id, value);
        return new GrowthExtras(grown);
    }

    /// <summary>
    /// 把这份（从设置文件读来的）额度里<b>还没登记</b>的条目并进 <paramref name="edited"/>。
    /// 侧栏只列得出已登记的来源，重新发布额度时靠这一步把停用中的 mod 那份额度原样带回去，
    /// 免得写回设置文件时把它冲掉。
    /// </summary>
    public GrowthExtras MergeUnregistered(GrowthExtras edited)
    {
        if (_entries is null)
            return edited;
        GrowthExtras merged = edited;
        foreach (KeyValuePair<string, int> entry in _entries)
        {
            if (!GrowthSourceMirrors.IsRegistered(entry.Key))
                merged = merged.With(entry.Key, entry.Value);
        }
        return merged;
    }

    /// <summary>把这份额度乘到 <paramref name="rewards"/> 那份计数上。</summary>
    public int Credit(GrowthExtras rewards)
    {
        if (_entries is null || rewards._entries is null)
            return 0;
        int credit = 0;
        foreach (KeyValuePair<string, int> reward in rewards._entries)
            credit = checked(credit + Get(reward.Key) * reward.Value);
        return credit;
    }

    public void ValidateBudgets()
    {
        if (_entries is null)
            return;
        foreach (KeyValuePair<string, int> entry in _entries)
        {
            if (string.IsNullOrEmpty(entry.Key))
                throw new InvalidDataException("Third-party growth source ID must not be empty.");
            if (entry.Value is < 0 or > MaximumValue)
                throw new InvalidDataException($"Growth budget {entry.Key} must be in 0..{MaximumValue} HP.");
        }
    }

    public void AppendFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        if (_entries is null)
            return;
        fingerprint.Add('E');
        foreach (KeyValuePair<string, int> entry in _entries)
        {
            fingerprint.Add(entry.Key);
            fingerprint.Add(entry.Value);
        }
    }

    public Dictionary<string, int>? ToDictionary()
    {
        if (_entries is null)
            return null;
        Dictionary<string, int> result = new(_entries.Length, StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> entry in _entries)
            result.Add(entry.Key, entry.Value);
        return result;
    }

    /// <summary>从持久化过来的字典重建。0 值和空 id 直接丢掉，认不出的 id 原样留着。</summary>
    public static GrowthExtras FromDictionary(IReadOnlyDictionary<string, int>? source)
    {
        if (source is null || source.Count == 0)
            return default;
        List<KeyValuePair<string, int>> entries = new(source.Count);
        foreach (KeyValuePair<string, int> entry in source)
        {
            if (entry.Value != 0 && !string.IsNullOrEmpty(entry.Key))
                entries.Add(entry);
        }
        if (entries.Count == 0)
            return default;
        entries.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return new GrowthExtras([.. entries]);
    }

    private int IndexOf(string id)
    {
        if (_entries is null)
            return -1;
        int low = 0;
        int high = _entries.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = string.CompareOrdinal(_entries[middle].Key, id);
            if (comparison == 0)
                return middle;
            if (comparison < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return ~low;
    }

    public bool Equals(GrowthExtras other)
    {
        if (_entries is null || other._entries is null)
            return _entries is null && other._entries is null;
        if (ReferenceEquals(_entries, other._entries))
            return true;
        if (_entries.Length != other._entries.Length)
            return false;
        for (int index = 0; index < _entries.Length; index++)
        {
            if (!string.Equals(_entries[index].Key, other._entries[index].Key, StringComparison.Ordinal)
                || _entries[index].Value != other._entries[index].Value)
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is GrowthExtras other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        if (_entries is not null)
        {
            foreach (KeyValuePair<string, int> entry in _entries)
            {
                hash.Add(entry.Key, StringComparer.Ordinal);
                hash.Add(entry.Value);
            }
        }
        return hash.ToHashCode();
    }

    public override string ToString()
        => _entries is null
            ? "[]"
            : "[" + string.Join(", ", _entries.Select(entry => entry.Key + " = " + entry.Value)) + "]";

    public static bool operator ==(GrowthExtras left, GrowthExtras right) => left.Equals(right);

    public static bool operator !=(GrowthExtras left, GrowthExtras right) => !left.Equals(right);
}

// Immutable vectors are used for both per-event HP budgets and realized event counts.
internal readonly record struct GrowthValues(
    int HandOfGreed = 0, int TheHunt = 0, int Feed = 0, int Royalties = 0,
    int Alchemize = 0, int GeneticAlgorithm = 0, int TheScythe = 0, int Goopy = 0, int ForbiddenGrimoire = 0)
{
    private readonly GrowthExtras _extras;

    /// <summary>第三方来源那一半，见 <see cref="GrowthSourceMirrors"/>。</summary>
    [JsonIgnore]
    public GrowthExtras Extras
    {
        get => _extras;
        init => _extras = value;
    }

    /// <summary>
    /// 持久化用的第三方部分。没有第三方条目时是 <c>null</c>，设置文件与问题包里不会多出这个字段，
    /// 旧文件读进来也就还原成空。
    /// </summary>
    [JsonPropertyName("thirdParty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? ThirdParty
    {
        get => _extras.ToDictionary();
        init => _extras = GrowthExtras.FromDictionary(value);
    }

    [JsonIgnore]
    public bool IsEnabled => this != default;
    [JsonIgnore]
    public int Total => checked(HandOfGreed + TheHunt + Feed + Royalties + Alchemize + GeneticAlgorithm + TheScythe + Goopy
        + ForbiddenGrimoire + _extras.Total);

    public static bool HasTarget(CardModel card)
        => card is Cards.HandOfGreed or Cards.TheHunt or Cards.Feed or Cards.Royalties or Cards.Alchemize or Cards.ForbiddenGrimoire
            || card.DeckVersion != null && (card is Cards.GeneticAlgorithm or Cards.TheScythe || card.Enchantment is Enchantments.Goopy)
            || GrowthSourceMirrors.HasTarget(card);

    // Early stopping asks each currently available fatal card to realize its reward once,
    // bounded by the enemies present. This is a search goal, not a theoretical farming bound.
    // Non-fatal and mixed sources retain the existing growth search policy.
    public static FatalGrowthSearchTarget? CaptureFatalTarget(IEnumerable<CardModel> cards, int enemyCount)
    {
        GrowthSource? source = null;
        int availableCards = 0;
        foreach (CardModel card in cards.Where(HasTarget))
        {
            GrowthSource? candidate = card switch
            {
                Cards.TheHunt => GrowthSource.TheHunt,
                Cards.Feed => GrowthSource.Feed,
                Cards.HandOfGreed => GrowthSource.HandOfGreed,
                _ => null,
            };
            if (candidate == null || card.Enchantment is Enchantments.Goopy
                || GrowthSourceMirrors.HasTarget(card) || (source.HasValue && source != candidate))
                return null;
            source = candidate;
            if (card.Pile?.Type != MegaCrit.Sts2.Core.Entities.Cards.PileType.Exhaust)
                availableCards++;
        }
        return source is { } selected && enemyCount > 0
            ? new FatalGrowthSearchTarget(selected,
                selected == GrowthSource.HandOfGreed && availableCards > 0 ? enemyCount : Math.Min(availableCards, enemyCount))
            : null;
    }
    public int Get(GrowthSource source) => source switch
    {
        GrowthSource.HandOfGreed => HandOfGreed,
        GrowthSource.TheHunt => TheHunt,
        GrowthSource.Feed => Feed,
        GrowthSource.Royalties => Royalties,
        GrowthSource.Alchemize => Alchemize,
        GrowthSource.GeneticAlgorithm => GeneticAlgorithm,
        GrowthSource.TheScythe => TheScythe,
        GrowthSource.Goopy => Goopy,
        GrowthSource.ForbiddenGrimoire => ForbiddenGrimoire,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>取一个第三方来源的值。没登记过的 id 一律是 0。</summary>
    public int Get(GrowthSourceHandle source) => _extras.Get(RequireValid(source));

    public GrowthValues With(GrowthSource source, int value) => source switch
    {
        GrowthSource.HandOfGreed => this with { HandOfGreed = value },
        GrowthSource.TheHunt => this with { TheHunt = value },
        GrowthSource.Feed => this with { Feed = value },
        GrowthSource.Royalties => this with { Royalties = value },
        GrowthSource.Alchemize => this with { Alchemize = value },
        GrowthSource.GeneticAlgorithm => this with { GeneticAlgorithm = value },
        GrowthSource.TheScythe => this with { TheScythe = value },
        GrowthSource.Goopy => this with { Goopy = value },
        GrowthSource.ForbiddenGrimoire => this with { ForbiddenGrimoire = value },
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public GrowthValues With(GrowthSourceHandle source, int value)
        => this with { Extras = _extras.With(RequireValid(source), value) };

    public int Credit(GrowthValues rewards) => checked(
        HandOfGreed * rewards.HandOfGreed + TheHunt * rewards.TheHunt
        + Feed * rewards.Feed + Royalties * rewards.Royalties
        + Alchemize * rewards.Alchemize + GeneticAlgorithm * rewards.GeneticAlgorithm
        + TheScythe * rewards.TheScythe + Goopy * rewards.Goopy
        + ForbiddenGrimoire * rewards.ForbiddenGrimoire + _extras.Credit(rewards._extras));

    public void ValidateBudgets()
    {
        foreach (GrowthSource source in Enum.GetValues<GrowthSource>())
        {
            if (Get(source) is < 0 or > 1000)
                throw new InvalidDataException($"Growth budget {source} must be in 0..1000 HP.");
        }
        _extras.ValidateBudgets();
    }

    /// <summary>把这份计数写进状态指纹。第三方部分为空时与开这个口子之前逐位相同。</summary>
    public void AppendFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        fingerprint.Add(HandOfGreed);
        fingerprint.Add(TheHunt);
        fingerprint.Add(Feed);
        fingerprint.Add(Royalties);
        fingerprint.Add(Alchemize);
        fingerprint.Add(GeneticAlgorithm);
        fingerprint.Add(TheScythe);
        fingerprint.Add(Goopy);
        fingerprint.Add(ForbiddenGrimoire);
        _extras.AppendFingerprint(ref fingerprint);
    }

    public override string ToString()
    {
        string vanilla = $"{nameof(GrowthValues)} {{ {nameof(HandOfGreed)} = {HandOfGreed}, "
            + $"{nameof(TheHunt)} = {TheHunt}, {nameof(Feed)} = {Feed}, {nameof(Royalties)} = {Royalties}, "
            + $"{nameof(Alchemize)} = {Alchemize}, {nameof(GeneticAlgorithm)} = {GeneticAlgorithm}, "
            + $"{nameof(TheScythe)} = {TheScythe}, {nameof(Goopy)} = {Goopy}, {nameof(ForbiddenGrimoire)} = {ForbiddenGrimoire}";
        return _extras.IsEmpty ? vanilla + " }" : vanilla + $", {nameof(ThirdParty)} = {_extras} }}";
    }

    private static string RequireValid(GrowthSourceHandle source)
        => source.IsValid
            ? source.Id
            : throw new ArgumentException(
                "第三方成长来源句柄没有初始化，必须用 GrowthSourceMirrors.Register 取得。", nameof(source));
}
