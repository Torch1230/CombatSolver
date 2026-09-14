using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal readonly record struct GrowthOpportunityTarget
{
    private GrowthOpportunityTarget(int? maximumRewards, string? unboundedReason)
    {
        MaximumRewards = maximumRewards;
        UnboundedReason = unboundedReason;
    }

    public int? MaximumRewards { get; }
    public string? UnboundedReason { get; }
    public bool IsBounded => MaximumRewards.HasValue;

    public static GrowthOpportunityTarget Bounded(int maximumRewards)
        => maximumRewards >= 0
            ? new GrowthOpportunityTarget(maximumRewards, null)
            : throw new ArgumentOutOfRangeException(nameof(maximumRewards));

    public static GrowthOpportunityTarget Unbounded(string reason)
        => !string.IsNullOrWhiteSpace(reason)
            ? new GrowthOpportunityTarget(null, reason)
            : throw new ArgumentException("成长机会的不可证明原因不能为空。", nameof(reason));
}

internal sealed record GrowthOpportunityCardSnapshot(
    string CardId,
    string RuntimeType,
    bool HasPermanentDeckVersion,
    int FixedReplayCount);

internal sealed record GrowthOpportunityContext(
    IReadOnlyList<GrowthOpportunityCardSnapshot> MatchingCards,
    int EnemyCount);

internal sealed record GrowthOpportunityUnboundedSource(
    string SourceId,
    string Reason);

internal sealed record GrowthOpportunityTargets(
    GrowthValues RequiredRewards,
    IReadOnlyList<GrowthOpportunityUnboundedSource> UnboundedSources)
{
    public static GrowthOpportunityTargets Empty { get; } = new(
        default,
        Array.Empty<GrowthOpportunityUnboundedSource>());
    public bool HasTargets => RequiredRewards.Total > 0 || UnboundedSources.Count > 0;
    public bool IsBounded => UnboundedSources.Count == 0;
    public bool IsSatisfiedBy(GrowthValues rewards)
        => IsBounded && rewards.Satisfies(RequiredRewards);

    internal static GrowthOpportunityTargets UnboundedForTesting(string reason)
        => !string.IsNullOrWhiteSpace(reason)
            ? new GrowthOpportunityTargets(
                default,
                Array.AsReadOnly(new[] { new GrowthOpportunityUnboundedSource("test", reason) }))
            : throw new ArgumentException("成长机会的不可证明原因不能为空。", nameof(reason));
}

internal static class GrowthOpportunityPolicy
{
    public static GrowthOpportunityTargets Capture(CombatState state)
    {
        CardModel[] availableCards = state.Players
            .SelectMany(player => player.PlayerCombatState!.AllCards)
            .Where(IsAvailable)
            .ToArray();
        bool hasAnyTargets = availableCards.Any(GrowthValues.HasTarget);
        string? dynamicRisk = null;
        if (hasAnyTargets)
            HasDynamicCardCountRisk(state, availableCards, out dynamicRisk);
        return CaptureTargets(availableCards, state.Enemies.Count, dynamicRisk);
    }

    private static GrowthOpportunityTargets CaptureTargets(
        IReadOnlyList<CardModel> availableCards,
        int enemyCount,
        string? dynamicRisk)
    {
        GrowthValues required = default;
        List<GrowthOpportunityUnboundedSource> unbounded = [];

        if (dynamicRisk != null)
        {
            foreach (GrowthSource source in Enum.GetValues<GrowthSource>()
                         .Where(source => availableCards.Any(card => MatchesSource(card, source))))
            {
                unbounded.Add(new GrowthOpportunityUnboundedSource(source.ToString(), dynamicRisk));
            }
        }
        else
            CaptureBuiltInTargets(availableCards, enemyCount, ref required, unbounded);

        foreach (GrowthSourceMirrors.Entry entry in GrowthSourceMirrors.All)
        {
            CardModel[] matching = availableCards.Where(entry.HasTarget).ToArray();
            if (matching.Length == 0)
                continue;
            if (dynamicRisk != null)
            {
                unbounded.Add(new GrowthOpportunityUnboundedSource(entry.Id, dynamicRisk));
                continue;
            }
            if (entry.OpportunityTarget == null)
            {
                unbounded.Add(new GrowthOpportunityUnboundedSource(entry.Id, "target_not_registered"));
                continue;
            }
            GrowthOpportunityCardSnapshot[] frozenCards = matching
                .Select(FreezeCard)
                .ToArray();
            GrowthOpportunityTarget target = entry.OpportunityTarget(
                new GrowthOpportunityContext(Array.AsReadOnly(frozenCards), enemyCount));
            if (target.IsBounded)
            {
                required = required.With(
                    new GrowthSourceHandle(entry.Id),
                    target.MaximumRewards!.Value);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(target.UnboundedReason))
                    throw new InvalidDataException(
                        $"第三方成长来源 {entry.Id} 返回了无效的目标结果。");
                unbounded.Add(new GrowthOpportunityUnboundedSource(entry.Id, target.UnboundedReason));
            }
        }

        return required.Total == 0 && unbounded.Count == 0
            ? GrowthOpportunityTargets.Empty
            : new GrowthOpportunityTargets(required, Array.AsReadOnly(unbounded.ToArray()));
    }

    internal static GrowthOpportunityTargets CaptureBuiltInForTesting(
        IReadOnlyList<CardModel> availableCards,
        int enemyCount)
    {
        GrowthValues required = default;
        List<GrowthOpportunityUnboundedSource> unbounded = [];
        CaptureBuiltInTargets(availableCards, enemyCount, ref required, unbounded);
        return required.Total == 0 && unbounded.Count == 0
            ? GrowthOpportunityTargets.Empty
            : new GrowthOpportunityTargets(required, Array.AsReadOnly(unbounded.ToArray()));
    }

    internal static GrowthOpportunityTargets CaptureAvailableForTesting(
        IReadOnlyList<CardModel> availableCards,
        int enemyCount)
        => CaptureTargets(availableCards, enemyCount, dynamicRisk: null);

    private static void CaptureBuiltInTargets(
        IReadOnlyList<CardModel> cards,
        int enemyCount,
        ref GrowthValues required,
        List<GrowthOpportunityUnboundedSource> unbounded)
    {
        GrowthSource[] fatalSources = cards
            .Select(FatalSource)
            .Where(source => source.HasValue)
            .Select(source => source!.Value)
            .Distinct()
            .OrderBy(source => source)
            .ToArray();
        if (fatalSources.Length > 1)
        {
            foreach (GrowthSource source in fatalSources)
            {
                unbounded.Add(new GrowthOpportunityUnboundedSource(
                    source.ToString(),
                    "fatal_source_competition"));
            }
        }
        else if (fatalSources.Length == 1)
        {
            GrowthSource source = fatalSources[0];
            int count = source == GrowthSource.HandOfGreed
                ? enemyCount
                : Math.Min(enemyCount, cards.Count(card => FatalSource(card) == source));
            required = required.With(source, count);
        }

        required = required
            .With(GrowthSource.Royalties, CountFixedPlays(cards, card => card is Royalties))
            .With(GrowthSource.Alchemize, CountFixedPlays(cards, card => card is Alchemize))
            .With(GrowthSource.GeneticAlgorithm, CountFixedPlays(cards,
                card => card is GeneticAlgorithm && card.DeckVersion != null))
            .With(GrowthSource.TheScythe, CountFixedPlays(cards,
                card => card is TheScythe && card.DeckVersion != null))
            .With(GrowthSource.Goopy, CountFixedPlays(cards,
                card => card.DeckVersion != null && card.Enchantment is Goopy))
            .With(GrowthSource.ForbiddenGrimoire, CountFixedPlays(cards,
                card => card is ForbiddenGrimoire));
    }

    private static int CountFixedPlays(
        IEnumerable<CardModel> cards,
        Func<CardModel, bool> predicate)
    {
        int count = 0;
        foreach (CardModel card in cards.Where(predicate))
        {
            int fixedReplayCount = card.GetEnchantedReplayCount();
            if (fixedReplayCount < 0)
                throw new InvalidDataException(
                    $"成长牌 {card.Id.Entry} 的固定重放次数不能为负数：{fixedReplayCount}。");
            count = checked(count + 1 + fixedReplayCount);
        }
        return count;
    }

    private static GrowthOpportunityCardSnapshot FreezeCard(CardModel card)
    {
        int fixedReplayCount = card.GetEnchantedReplayCount();
        if (fixedReplayCount < 0)
            throw new InvalidDataException(
                $"成长牌 {card.Id.Entry} 的固定重放次数不能为负数：{fixedReplayCount}。");
        return new GrowthOpportunityCardSnapshot(
            card.Id.Entry,
            card.GetType().FullName ?? card.GetType().Name,
            card.DeckVersion != null,
            fixedReplayCount);
    }

    private static GrowthSource? FatalSource(CardModel card)
        => card switch
        {
            HandOfGreed => GrowthSource.HandOfGreed,
            TheHunt => GrowthSource.TheHunt,
            Feed => GrowthSource.Feed,
            _ => null,
        };

    private static bool MatchesSource(CardModel card, GrowthSource source)
        => source switch
        {
            GrowthSource.HandOfGreed => card is HandOfGreed,
            GrowthSource.TheHunt => card is TheHunt,
            GrowthSource.Feed => card is Feed,
            GrowthSource.Royalties => card is Royalties,
            GrowthSource.Alchemize => card is Alchemize,
            GrowthSource.GeneticAlgorithm => card is GeneticAlgorithm && card.DeckVersion != null,
            GrowthSource.TheScythe => card is TheScythe && card.DeckVersion != null,
            GrowthSource.Goopy => card.DeckVersion != null && card.Enchantment is Goopy,
            GrowthSource.ForbiddenGrimoire => card is ForbiddenGrimoire,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

    private static bool IsAvailable(CardModel card)
        => !card.HasBeenRemovedFromState
            && card.Pile is { Type: not PileType.Exhaust };

    private static bool HasDynamicCardCountRisk(
        CombatState state,
        IReadOnlyList<CardModel> cards,
        out string? reason)
    {
        if (cards.Any(card => card is Burst or EchoForm or OneTwoPunch or SignalBoost or TagTeam
                or Nightmare or DualWield or HeirloomHammer or Juggling or ImitationLearning
                or HowlFromBeyond))
        {
            reason = "built_in:reachable_replay_or_copy_card";
            return true;
        }
        if (state.Players.SelectMany(player => player.Creature.Powers).Any(power => power.Amount > 0m
                && power is BurstPower or DuplicationPower or EchoFormPower or OneTwoPunchPower
                    or SignalBoostPower or TagTeamPower or JugglingPower or ImitationLearningPower
                    or NightmarePower))
        {
            reason = "built_in:active_replay_or_copy_power";
            return true;
        }
        if (state.Players.SelectMany(player => player.Relics).Any(relic => !relic.IsMelted
                && relic is ThrowingAxe or BurningSticks))
        {
            reason = "built_in:active_replay_or_copy_relic";
            return true;
        }
        if (state.Players.SelectMany(player => player.PotionSlots).Any(potion => potion?.Id.Entry == "DUPLICATOR"))
        {
            reason = "built_in:available_duplication_potion";
            return true;
        }
        reason = null;
        return false;
    }
}
