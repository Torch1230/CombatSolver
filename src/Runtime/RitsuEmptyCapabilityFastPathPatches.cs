using System.Collections.Concurrent;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Models.Capabilities;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal static class RitsuEmptyCapabilityFastPath
{
    private readonly record struct DefaultCapabilitySourceCacheEntry(
        int Generation,
        bool HasSource);

    private const string CardCapabilityHostTypeName =
        "STS2RitsuLib.Models.Capabilities.CardModelCapabilityHost";
    private const string CapabilityDefaultsTypeName =
        "STS2RitsuLib.Models.Capabilities.ModelCapabilityDefaults";
    private static readonly Func<AbstractModel, bool> HasDefaultCapabilitySource =
        CreateHasDefaultCapabilitySource();
    private static readonly ConcurrentDictionary<Type, DefaultCapabilitySourceCacheEntry>
        DefaultCapabilitySources = [];
    private static int _defaultCapabilitySourceGeneration;

    internal static ModPatchTarget CardHostTarget(string methodName, params Type[] parameters)
    {
        Type host = typeof(ModelCapabilities).Assembly.GetType(CardCapabilityHostTypeName)
            ?? throw new TypeLoadException(CardCapabilityHostTypeName);
        return new ModPatchTarget(host, methodName, parameters);
    }

    internal static bool CanSkip(AbstractModel model)
    {
        if (!SimulationNotificationIsolation.IsActive)
            return false;
        if (ModelCapabilities.TryGet(model, out ModelCapabilitySet? capabilities))
            return capabilities.Count == 0;
        Type modelType = model.GetType();
        while (true)
        {
            int generation = Volatile.Read(ref _defaultCapabilitySourceGeneration);
            if (DefaultCapabilitySources.TryGetValue(modelType, out var cached)
                && cached.Generation == generation)
            {
                return !cached.HasSource;
            }

            bool hasDefaultCapabilitySource = HasDefaultCapabilitySource(model);
            if (generation != Volatile.Read(ref _defaultCapabilitySourceGeneration))
                continue;
            DefaultCapabilitySources[modelType] = new(generation, hasDefaultCapabilitySource);
            if (generation == Volatile.Read(ref _defaultCapabilitySourceGeneration))
                return !hasDefaultCapabilitySource;
        }
    }

    internal static ModPatchTarget DefaultCapabilityRegistrationTarget() => new(
        CapabilityDefaultsType(),
        "Modify",
        [
            typeof(string),
            typeof(string),
            typeof(Type),
            typeof(Action<AbstractModel, ModelCapabilityList>),
            typeof(int),
        ]);

    internal static void InvalidateDefaultCapabilitySources()
    {
        Interlocked.Increment(ref _defaultCapabilitySourceGeneration);
        DefaultCapabilitySources.Clear();
    }

    internal static int DefaultCapabilitySourceGenerationForTesting
        => Volatile.Read(ref _defaultCapabilitySourceGeneration);

    internal static bool HasCachedDefaultCapabilitySourceGenerationForTesting(
        Type modelType,
        int generation)
        => DefaultCapabilitySources.TryGetValue(modelType, out var cached)
            && cached.Generation == generation;

    private static Func<AbstractModel, bool> CreateHasDefaultCapabilitySource()
    {
        Type defaults = CapabilityDefaultsType();
        MethodInfo method = defaults.GetMethod(
            "HasDefaultCapabilitySource",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(AbstractModel)],
            modifiers: null)
            ?? throw new MissingMethodException(defaults.FullName, "HasDefaultCapabilitySource(AbstractModel)");
        return method.CreateDelegate<Func<AbstractModel, bool>>();
    }

    private static Type CapabilityDefaultsType()
        => typeof(ModelCapabilities).Assembly.GetType(CapabilityDefaultsTypeName)
            ?? throw new TypeLoadException(CapabilityDefaultsTypeName);
}

internal sealed class RitsuDefaultCapabilityRegistrationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_default_capability_cache_invalidation";
    public static string Description => "Ritsu 默认能力注册变化时清理求解类型缓存";

    public static ModPatchTarget[] GetTargets() =>
    [
        RitsuEmptyCapabilityFastPath.DefaultCapabilityRegistrationTarget(),
    ];

    public static void Postfix()
        => RitsuEmptyCapabilityFastPath.InvalidateDefaultCapabilitySources();
}

internal sealed class RitsuEmptyCardTypeFastPathPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_empty_card_type_fast_path";
    public static string Description => "求解模拟跳过空 Ritsu capability 的卡牌类型贡献管线";

    public static ModPatchTarget[] GetTargets() =>
    [
        RitsuEmptyCapabilityFastPath.CardHostTarget(
            "ApplyCardType",
            typeof(CardModel),
            typeof(CardType)),
    ];

    public static bool Prefix(CardModel card, CardType current, ref CardType __result)
    {
        if (!RitsuEmptyCapabilityFastPath.CanSkip(card))
            return true;
        __result = current;
        return false;
    }
}

internal sealed class RitsuEmptyCardRarityFastPathPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_empty_card_rarity_fast_path";
    public static string Description => "求解模拟跳过空 Ritsu capability 的卡牌稀有度贡献管线";

    public static ModPatchTarget[] GetTargets() =>
    [
        RitsuEmptyCapabilityFastPath.CardHostTarget(
            "ApplyCardRarity",
            typeof(CardModel),
            typeof(CardRarity)),
    ];

    public static bool Prefix(CardModel card, CardRarity current, ref CardRarity __result)
    {
        if (!RitsuEmptyCapabilityFastPath.CanSkip(card))
            return true;
        __result = current;
        return false;
    }
}

internal sealed class RitsuEmptyCardTagsFastPathPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_empty_card_tags_fast_path";
    public static string Description => "求解模拟跳过空 Ritsu capability 的卡牌标签贡献管线";

    public static ModPatchTarget[] GetTargets() =>
    [
        RitsuEmptyCapabilityFastPath.CardHostTarget(
            "ApplyTags",
            typeof(CardModel),
            typeof(IEnumerable<CardTag>)),
    ];

    public static bool Prefix(CardModel card, IEnumerable<CardTag> current,
        ref IEnumerable<CardTag> __result)
    {
        if (!RitsuEmptyCapabilityFastPath.CanSkip(card))
            return true;
        __result = current;
        return false;
    }
}

internal sealed class RitsuEmptyEnergyContributorFastPathPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_empty_energy_contributor_fast_path";
    public static string Description => "求解模拟跳过空 Ritsu capability 的费用贡献者查询";

    public static ModPatchTarget[] GetTargets() =>
    [
        RitsuEmptyCapabilityFastPath.CardHostTarget(
            "HasEnergyCostContributors",
            typeof(CardModel)),
    ];

    public static bool Prefix(CardModel card, ref bool __result)
    {
        if (!RitsuEmptyCapabilityFastPath.CanSkip(card))
            return true;
        __result = false;
        return false;
    }
}

internal sealed class RitsuEmptyEnergyCostFastPathPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_empty_energy_cost_fast_path";
    public static string Description => "求解模拟跳过空 Ritsu capability 的费用修改管线";

    public static ModPatchTarget[] GetTargets() =>
    [
        RitsuEmptyCapabilityFastPath.CardHostTarget(
            "ApplyEnergyCost",
            typeof(CardModel),
            typeof(CostModifiers),
            typeof(int)),
    ];

    public static bool Prefix(CardModel card, int current, ref int __result)
    {
        if (!RitsuEmptyCapabilityFastPath.CanSkip(card))
            return true;
        __result = current;
        return false;
    }
}

internal sealed class RitsuEmptyStarContributorFastPathPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_empty_star_contributor_fast_path";
    public static string Description => "求解模拟跳过空 Ritsu capability 的星能费用贡献者查询";

    public static ModPatchTarget[] GetTargets() =>
    [
        RitsuEmptyCapabilityFastPath.CardHostTarget(
            "HasStarCostContributors",
            typeof(CardModel)),
    ];

    public static bool Prefix(CardModel card, ref bool __result)
    {
        if (!RitsuEmptyCapabilityFastPath.CanSkip(card))
            return true;
        __result = false;
        return false;
    }
}

internal sealed class RitsuEmptyStarCostFastPathPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_empty_star_cost_fast_path";
    public static string Description => "求解模拟跳过空 Ritsu capability 的星能费用修改管线";

    public static ModPatchTarget[] GetTargets() =>
    [
        RitsuEmptyCapabilityFastPath.CardHostTarget(
            "ApplyStarCost",
            typeof(CardModel),
            typeof(int)),
    ];

    public static bool Prefix(CardModel card, int current, ref int __result)
    {
        if (!RitsuEmptyCapabilityFastPath.CanSkip(card))
            return true;
        __result = current;
        return false;
    }
}

internal sealed class RitsuEmptyCanPlayFastPathPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_ritsu_empty_can_play_fast_path";
    public static string Description => "求解模拟跳过空 Ritsu capability 的卡牌可打出状态贡献管线";

    public static ModPatchTarget[] GetTargets() =>
    [
        RitsuEmptyCapabilityFastPath.CardHostTarget(
            "ApplyCanPlay",
            typeof(CardModel),
            typeof(bool)),
    ];

    public static bool Prefix(CardModel card, bool current, ref bool __result)
    {
        if (!RitsuEmptyCapabilityFastPath.CanSkip(card))
            return true;
        __result = current;
        return false;
    }
}
