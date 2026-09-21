using System.Collections.Frozen;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

[Flags]
internal enum CombatHistoryDependencies
{
    None = 0, FinishedPlays = 1, EtherealPlays = 2, LightningChannels = 4,
    UnblockedHitsReceived = 8, CardsDrawn = 16, CardsGenerated = 32,
    All = 63,
}

/// <summary>Immutable root-wide dependencies, including readers which can enter combat later.</summary>
internal static class CombatHistoryCounterKey
{
    // Random generation is transitive: a generated power can generate another card or potion.
    // Keep all counters for these sources, rather than guessing the first generated result.
    // Fixed token creation and copying existing cards do not introduce a new reader dependency.
    private static readonly FrozenSet<string> OpenGenerationSources = new[]
    {
        "ABUNDANCE", "BUNDLE_OF_JOY", "DISTRACTION", "DISCOVERY", "INFERNAL_BLADE",
        "JACK_OF_ALL_TRADES", "JACKPOT", "LARGESSE", "MAD_SCIENCE", "TINKER_TIME",
        "MANIFEST_AUTHORITY", "METAMORPHOSIS", "QUASAR", "SPLASH", "STOKE", "WHITE_NOISE",
        "ALCHEMIZE", "CALAMITY", "CALL_OF_THE_VOID", "CREATIVE_AI", "HELLO_WORLD",
        "SPECTRUM_SHIFT", "ENTROPY", "CALAMITY_POWER", "CALL_OF_THE_VOID_POWER",
        "CREATIVE_AI_POWER", "HELLO_WORLD_POWER", "SPECTRUM_SHIFT_POWER", "ENTROPY_POWER",
        // A stored copy can outlive the original card or its identity (e.g. transformation).
        "NIGHTMARE_POWER",
        "TOOLBOX", "CHOICES_PARADOX", "VEXING_PUZZLEBOX", "BIG_HAT", "CROSSBOW",
        "ORANGE_DOUGH", "PETRIFIED_TOAD",
        "ATTACK_POTION", "SKILL_POTION", "POWER_POTION", "COLORLESS_POTION",
        "COSMIC_CONCOCTION", "OROBIC_ACID", "ENTROPIC_BREW",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static CombatHistoryDependencies ForCard(string id) => id switch
    {
        "GOLD_AXE" => CombatHistoryDependencies.FinishedPlays,
        "PULL_FROM_BELOW" or "BANSHEES_CRY" => CombatHistoryDependencies.EtherealPlays,
        "VOLTAIC" => CombatHistoryDependencies.LightningChannels,
        "TEAR_ASUNDER" => CombatHistoryDependencies.UnblockedHitsReceived,
        "MURDER" => CombatHistoryDependencies.CardsDrawn,
        "SUPERMASSIVE" => CombatHistoryDependencies.CardsGenerated,
        _ => CombatHistoryDependencies.None,
    };

    public static bool AppliesTo(IReadOnlySet<string> playerCardIds)
        => playerCardIds.Any(id => ForCard(id) != CombatHistoryDependencies.None);

    internal static CombatHistoryDependencies Capture(IEnumerable<AbstractModel> sources, bool hasModHooks)
    {
        if (hasModHooks) return CombatHistoryDependencies.All;
        CombatHistoryDependencies dependencies = CombatHistoryDependencies.None;
        foreach (AbstractModel source in sources)
        {
            if (source.GetType().Assembly != typeof(CardModel).Assembly
                || OpenGenerationSources.Contains(source.Id.Entry))
                return CombatHistoryDependencies.All;
            if (source is CardModel) dependencies |= ForCard(source.Id.Entry);
        }
        return dependencies;
    }

    public static void Append(ref StateFingerprintBuilder key, CombatPredictionSimulator simulator, Player owner,
        CombatHistoryDependencies dependencies = CombatHistoryDependencies.All)
        => AppendCounters(ref key, simulator.History.GetCounters(owner), dependencies);

    internal static void AppendCounters(ref StateFingerprintBuilder key, CombatHistoryCounters counters)
        => AppendCounters(ref key, counters, CombatHistoryDependencies.All);

    internal static void AppendCounters(ref StateFingerprintBuilder key, CombatHistoryCounters counters,
        CombatHistoryDependencies dependencies)
    {
        if (dependencies == CombatHistoryDependencies.None) return;
        // Retain the existing encoding for All; an immutable root mask needs no per-node tag.
        key.Add('h');
        key.Add((dependencies & CombatHistoryDependencies.FinishedPlays) != 0 ? counters.FinishedPlays : 0);
        key.Add((dependencies & CombatHistoryDependencies.EtherealPlays) != 0 ? counters.EtherealPlays : 0);
        key.Add((dependencies & CombatHistoryDependencies.LightningChannels) != 0 ? counters.LightningChannels : 0);
        key.Add((dependencies & CombatHistoryDependencies.UnblockedHitsReceived) != 0 ? counters.UnblockedHitsReceived : 0);
        key.Add((dependencies & CombatHistoryDependencies.CardsDrawn) != 0 ? counters.CardsDrawn : 0);
        key.Add((dependencies & CombatHistoryDependencies.CardsGenerated) != 0 ? counters.CardsGenerated : 0);
    }
}
