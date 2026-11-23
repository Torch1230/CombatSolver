using System.Buffers;
using System.Runtime.CompilerServices;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;

namespace CombatSolver.Beam;

internal static class ForecastRetentionKey
{
    private sealed class DigestComparer : IComparer<CombatStateDescriptionDigest>
    {
        public static readonly DigestComparer Instance = new();
        public int Compare(CombatStateDescriptionDigest x, CombatStateDescriptionDigest y) => CompareDigest(x, y);
    }

    public static CombatStateDescriptionDigest ComputePlayableCardKey(CombatState state, CardModel card) =>
        ComputePlayableCardKey(state, card, DeckVersionAccessAnalyzer.RequiresDeckVersionIdentity(state, card));

    public static CombatStateDescriptionDigest ComputePlayableCardKey(
        CombatState state,
        CardModel card,
        bool requiresDeckIdentity)
    {
        var builder = new CombatStateDescriptionBuilder();
        builder.Append("forecast-playable-card-v1");
        builder.Append(card.LocalEnergyCost);
        builder.Append(card.CanPlay(out _));
        builder.Append(requiresDeckIdentity);
        if (requiresDeckIdentity && card.DeckVersion is { } deckVersion)
        {
            int deckIndex = IndexOfCardReference(card.Owner.Deck.Cards, deckVersion);
            builder.Append(deckIndex);
            if (deckIndex < 0)
            {
                builder.Append(RuntimeHelpers.GetHashCode(deckVersion));
            }
        }
        CombatStateDescription.AppendCardStateWithDeckVersion(ref builder, card, state);
        return builder.Build();
    }

    private static int IndexOfCardReference(IReadOnlyList<CardModel> cards, CardModel target)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (ReferenceEquals(cards[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    public static CombatStateDescriptionDigest Compute(SearchNode node) =>
        node.GetOrComputeRetentionKey();

    internal static CombatStateDescriptionDigest ComputeUncached(SearchNode node)
    {
        CombatState state = node.State;
        var builder = new CombatStateDescriptionBuilder();
        builder.Append("forecast-retention-key-v1");
        builder.Append((int)state.CurrentSide);
        builder.Append(state.RoundNumber);
        builder.Append(state.IsPlayerExtraTurn);
        builder.Append(state.Engine?.IsInProgress ?? false);
        builder.Append(state.Engine?.Won ?? false);
        builder.Append(state.RunState.Rng.Seed);
        CombatStateDescription.AppendRunRng(ref builder, state.RunState.Rng);

        IReadOnlyList<Player> players = state.Players;
        builder.Append(players.Count);
        for (int pi = 0; pi < players.Count; pi++)
        {
            Player player = players[pi];
            CombatStateDescription.AppendPlayerHeaderAndState(
                ref builder,
                state,
                player,
                includeDominanceMeasuredResources: true);

            // Deck 顺序不受打牌permutation影响，完整保留有序摘要。
            CombatStateDescription.AppendOrderedCardPile(ref builder, player.Deck, state);

            PlayerCombatState pcs = player.PlayerCombatState!;
            for (int p = 0; p < PlayerCombatState.PileCount; p++)
            {
                CardPile pile = pcs.GetPileAt(p);
                if (pile.Type == PileType.Discard || pile.Type == PileType.Exhaust)
                {
                    AppendUnorderedPile(ref builder, pile, state);
                }
                else
                {
                    CombatStateDescription.AppendOrderedCardStatesWithDeckVersions(ref builder, pile, state);
                }
            }

            IReadOnlyList<PotionModel?> potionSlots = player.PotionSlots;
            builder.Append(potionSlots.Count);
            for (int s = 0; s < potionSlots.Count; s++)
            {
                PotionModel? potion = potionSlots[s];
                builder.Append(potion is not null);
                if (potion is not null)
                {
                    builder.Append(potion.Id);
                }
            }

            IReadOnlyList<RelicModel> relics = player.Relics;
            builder.Append(relics.Count);
            for (int r = 0; r < relics.Count; r++)
            {
                RelicModel relic = relics[r];
                builder.Append(relic.Id);
                builder.Append((int)relic.Status);
                builder.Append(relic.StackCount);
                CombatStateDescription.AppendModel(ref builder, state, relic);
            }
        }

        IReadOnlyList<Creature> allies = state.Allies;
        builder.Append(allies.Count);
        for (int i = 0; i < allies.Count; i++)
        {
            AppendCreature(ref builder, state, allies[i]);
        }

        IReadOnlyList<Creature> enemies = state.Enemies;
        builder.Append(enemies.Count);
        for (int i = 0; i < enemies.Count; i++)
        {
            AppendCreature(ref builder, state, enemies[i]);
        }

        CombatStateDescription.AppendRemovedCreatures(ref builder, state);
        AppendOrderInvariantDamageHistory(ref builder, state);
        state.SemanticHistory.AppendOrderInvariantStateDescription(ref builder, state);

        builder.Append(node.PotionUses);
        builder.Append(node.ForcedPotionUses);
        builder.Append(node.BrightestFlameMaxHpLost);
        builder.Append(node.DeathSaveHpRestored);
        builder.Append(node.DeathSaveUseCount);
        builder.Append(node.EscapedTheft);
        builder.Append(node.Growth.HandOfGreed);
        builder.Append(node.Growth.TheHunt);
        builder.Append(node.Growth.Feed);
        builder.Append(node.Growth.Royalties);
        builder.Append(node.Growth.Alchemize);
        builder.Append(node.Growth.GeneticAlgorithm);
        builder.Append(node.Growth.TheScythe);
        builder.Append(node.Growth.Goopy);
        builder.Append(node.Growth.ForbiddenGrimoire);
        builder.Append(node.Growth.MadScience);

        return builder.Build();
    }

    private static void AppendCreature(
        ref CombatStateDescriptionBuilder builder,
        CombatState state,
        Creature creature)
    {
        CombatStateDescription.AppendCreatureIdentity(ref builder, creature);
        builder.Append(creature.CurrentHp);
        builder.Append(creature.MaxHp);
        builder.Append(creature.Block);
        builder.Append(creature.CumulativeHpLost);
        CombatStateDescription.AppendCreatureState(ref builder, state, creature);
    }

    private static void AppendUnorderedPile(
        ref CombatStateDescriptionBuilder builder,
        CardPile pile,
        CombatState state)
    {
        builder.Append((int)pile.Type);
        IReadOnlyList<CardModel> cards = pile.Cards;
        int count = cards.Count;
        builder.Append(count);
        if (count == 0)
        {
            return;
        }

        if (count == 1)
        {
            var cardBuilder = new CombatStateDescriptionBuilder();
            CombatStateDescription.AppendCardStateWithDeckVersion(ref cardBuilder, cards[0], state);
            CombatStateDescriptionDigest digest = cardBuilder.Build();
            builder.Append(digest.First);
            builder.Append(digest.Second);
            return;
        }

        if (count <= 32)
        {
            Span<CombatStateDescriptionDigest> digests = stackalloc CombatStateDescriptionDigest[count];
            BuildAndAppendSortedPileDigests(ref builder, pile, state, digests);
            return;
        }

        CombatStateDescriptionDigest[] rented = ArrayPool<CombatStateDescriptionDigest>.Shared.Rent(count);
        try
        {
            BuildAndAppendSortedPileDigests(ref builder, pile, state, rented.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<CombatStateDescriptionDigest>.Shared.Return(rented);
        }

        static void BuildAndAppendSortedPileDigests(
            ref CombatStateDescriptionBuilder builder,
            CardPile pile,
            CombatState state,
            Span<CombatStateDescriptionDigest> digests)
        {
            CombatStateDescription.DescribeCardsWithDeckVersions(digests, pile, state);
            digests.Sort(DigestComparer.Instance);
            for (int i = 0; i < digests.Length; i++)
            {
                CombatStateDescriptionDigest digest = digests[i];
                builder.Append(digest.First);
                builder.Append(digest.Second);
            }
        }
    }

    private static void AppendOrderInvariantDamageHistory(
        ref CombatStateDescriptionBuilder builder,
        CombatState state)
    {
        IReadOnlyList<CombatDamageHistoryEntry> entries = state.DamageHistory.Entries;
        int count = entries.Count;
        builder.Append(count);
        if (count == 0)
        {
            return;
        }

        if (count <= 32)
        {
            Span<CombatStateDescriptionDigest> digests = stackalloc CombatStateDescriptionDigest[count];
            BuildAndAppendSortedDamageDigests(ref builder, entries, state, digests);
            return;
        }

        CombatStateDescriptionDigest[] rented = ArrayPool<CombatStateDescriptionDigest>.Shared.Rent(count);
        try
        {
            BuildAndAppendSortedDamageDigests(ref builder, entries, state, rented.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<CombatStateDescriptionDigest>.Shared.Return(rented);
        }

        static void BuildAndAppendSortedDamageDigests(
            ref CombatStateDescriptionBuilder builder,
            IReadOnlyList<CombatDamageHistoryEntry> entries,
            CombatState state,
            Span<CombatStateDescriptionDigest> digests)
        {
            IReadOnlyList<Player> players = state.Players;
            for (int i = 0; i < digests.Length; i++)
            {
                CombatDamageHistoryEntry entry = entries[i];
                var entryBuilder = new CombatStateDescriptionBuilder();
                AppendCreatureReference(ref entryBuilder, state, entry.Receiver);
                AppendCreatureReference(ref entryBuilder, state, entry.Dealer);
                entryBuilder.Append((int)entry.Result.Props);
                entryBuilder.Append(entry.Result.BlockedDamage);
                entryBuilder.Append(entry.Result.UnblockedDamage);
                entryBuilder.Append(entry.Result.OverkillDamage);
                entryBuilder.Append(entry.Result.WasFullyBlocked);
                entryBuilder.Append(entry.Result.WasTargetKilled);
                entryBuilder.Append(entry.CardSource is not null);
                if (entry.CardSource is { } cardSource)
                {
                    // 仅保留来源卡 ModelId，不记录牌堆下标，避免牌堆序号区分等价换序分支。
                    entryBuilder.Append(cardSource.Id);
                }

                entryBuilder.Append(entry.RoundNumber);
                entryBuilder.Append((int)entry.CurrentSide);
                entryBuilder.Append(entry.HappenedThisTurn(state));
                for (int p = 0; p < players.Count; p++)
                {
                    entryBuilder.Append(entry.HappenedLastPlayerTurn(players[p]));
                }

                digests[i] = entryBuilder.Build();
            }

            digests.Sort(DigestComparer.Instance);
            for (int i = 0; i < digests.Length; i++)
            {
                CombatStateDescriptionDigest digest = digests[i];
                builder.Append(digest.First);
                builder.Append(digest.Second);
            }
        }
    }

    private static void AppendCreatureReference(
        ref CombatStateDescriptionBuilder builder,
        CombatState state,
        Creature? creature)
    {
        if (creature is null)
        {
            builder.Append(-1);
            return;
        }

        int allyIndex = IndexOfReference(state.Allies, creature);
        if (allyIndex >= 0)
        {
            builder.Append(0);
            builder.Append(allyIndex);
            return;
        }

        int escapedIndex = IndexOfReference(state.EscapedCreatures, creature);
        if (escapedIndex >= 0)
        {
            builder.Append(2);
            builder.Append(escapedIndex);
            return;
        }

        int enemyIndex = IndexOfReference(state.Enemies, creature);
        if (enemyIndex >= 0)
        {
            builder.Append(1);
            builder.Append(enemyIndex);
            return;
        }

        // 已移除生物通过 CombatId 与固有标识区分，避免依赖内部 RemovedCreatures 列表。
        builder.Append(3);
        CombatStateDescription.AppendCreatureIdentity(ref builder, creature);
        builder.Append(creature.IsAlive);
    }

    private static int IndexOfReference(IReadOnlyList<Creature> list, Creature target)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    internal static int CompareDigest(CombatStateDescriptionDigest left, CombatStateDescriptionDigest right)
    {
        int first = left.First.CompareTo(right.First);
        return first != 0 ? first : left.Second.CompareTo(right.Second);
    }
}
