using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Orbs;

namespace CombatSolver.Beam;

internal static class ForecastRetentionKey
{
    public static CombatStateDescriptionDigest ComputePlayableCardKey(CombatState state, CardModel card)
    {
        var builder = new CombatStateDescriptionBuilder();
        builder.Append("forecast-playable-card-v1");
        builder.Append(card.LocalEnergyCost);
        builder.Append(card.CanPlay(out _));
        bool requiresDeckIdentity = DeckVersionAccessAnalyzer.RequiresDeckVersionIdentity(state, card);
        builder.Append(requiresDeckIdentity);
        if (requiresDeckIdentity && card.DeckVersion is { } deckVersion)
        {
            int deckIndex = IndexOfCardReference(card.Owner.Deck.Cards, deckVersion);
            builder.Append(deckIndex);
            if (deckIndex < 0)
            {
                builder.Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(deckVersion));
            }
        }
        AppendForecastCard(ref builder, card, state);
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

    public static CombatStateDescriptionDigest Compute(SearchNode node)
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

        builder.Append(state.Players.Count);
        foreach (Player player in state.Players)
        {
            CombatStateDescription.AppendPlayerHeaderAndState(
                ref builder,
                state,
                player,
                includeDominanceMeasuredResources: true);

            // Deck 顺序不受打牌permutation影响，完整保留有序摘要。
            builder.Append((int)player.Deck.Type);
            builder.Append(player.Deck.Cards.Count);
            foreach (CardModel card in player.Deck.Cards)
            {
                CombatStateDescription.AppendCard(ref builder, card, state);
            }

            foreach (CardPile pile in player.PlayerCombatState!.AllPiles)
            {
                if (pile.Type == PileType.Discard || pile.Type == PileType.Exhaust)
                {
                    AppendUnorderedPile(ref builder, pile, state);
                }
                else
                {
                    AppendOrderedForecastPile(ref builder, pile, state);
                }
            }

            builder.Append(player.PotionSlots.Count);
            foreach (PotionModel? potion in player.PotionSlots)
            {
                builder.Append(potion is not null);
                if (potion is not null)
                {
                    builder.Append(potion.Id);
                }
            }

            builder.Append(player.Relics.Count);
            foreach (RelicModel relic in player.Relics)
            {
                builder.Append(relic.Id);
                builder.Append((int)relic.Status);
                builder.Append(relic.StackCount);
                CombatStateDescription.AppendModel(ref builder, state, relic);
            }
        }

        builder.Append(state.Allies.Count);
        foreach (Creature ally in state.Allies)
        {
            AppendCreature(ref builder, state, ally);
        }

        builder.Append(state.Enemies.Count);
        foreach (Creature enemy in state.Enemies)
        {
            AppendCreature(ref builder, state, enemy);
        }

        CombatStateDescription.AppendRemovedCreatures(ref builder, state);
        AppendOrderInvariantDamageHistory(ref builder, state);
        AppendOrderInvariantSemanticHistory(ref builder, state);

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

    private static void AppendForecastCard(
        ref CombatStateDescriptionBuilder builder,
        CardModel card,
        CombatState state)
    {
        CombatStateDescription.AppendCardState(ref builder, card, state);
        builder.Append(card.DeckVersion is not null);
        if (card.DeckVersion is { } deckVersion)
        {
            // 省略 Deck 中的下标，避免同一初始卡的不同副本被误判为不同状态。
            CombatStateDescription.AppendCardState(ref builder, deckVersion, state);
        }
    }

    private static void AppendOrderedForecastPile(
        ref CombatStateDescriptionBuilder builder,
        CardPile pile,
        CombatState state)
    {
        builder.Append((int)pile.Type);
        builder.Append(pile.Cards.Count);
        foreach (CardModel card in pile.Cards)
        {
            AppendForecastCard(ref builder, card, state);
        }
    }

    private static void AppendUnorderedPile(
        ref CombatStateDescriptionBuilder builder,
        CardPile pile,
        CombatState state)
    {
        builder.Append((int)pile.Type);
        IReadOnlyList<CardModel> cards = pile.Cards;
        builder.Append(cards.Count);
        if (cards.Count == 0)
        {
            return;
        }

        var digests = new CombatStateDescriptionDigest[cards.Count];
        for (int i = 0; i < cards.Count; i++)
        {
            var cardBuilder = new CombatStateDescriptionBuilder();
            AppendForecastCard(ref cardBuilder, cards[i], state);
            digests[i] = cardBuilder.Build();
        }

        Array.Sort(digests, CompareDigest);
        foreach (CombatStateDescriptionDigest digest in digests)
        {
            builder.Append(digest.First);
            builder.Append(digest.Second);
        }
    }

    private static void AppendOrderInvariantDamageHistory(
        ref CombatStateDescriptionBuilder builder,
        CombatState state)
    {
        IReadOnlyList<CombatDamageHistoryEntry> entries = state.DamageHistory.Entries;
        builder.Append(entries.Count);
        if (entries.Count == 0)
        {
            return;
        }

        var digests = new CombatStateDescriptionDigest[entries.Count];
        for (int i = 0; i < entries.Count; i++)
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
            foreach (Player player in state.Players)
            {
                entryBuilder.Append(entry.HappenedLastPlayerTurn(player));
            }

            digests[i] = entryBuilder.Build();
        }

        Array.Sort(digests, CompareDigest);
        foreach (CombatStateDescriptionDigest digest in digests)
        {
            builder.Append(digest.First);
            builder.Append(digest.Second);
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

    private static void AppendOrderInvariantSemanticHistory(
        ref CombatStateDescriptionBuilder builder,
        CombatState state)
    {
        CombatSemanticHistory history = state.SemanticHistory;
        builder.Append(history.CardsExhaustedThisCombat);
        foreach (Player player in state.Players)
        {
            builder.Append(history.CountCardsExhaustedThisTurn(state, player));
            // 不记录 BlockGainEntry.PlayOrdinal，仅保留当回合有效得甲次数。
            builder.Append(history.CountOtherQualifyingBlockGainsThisTurn(state, player.Creature, currentPlay: null));
            builder.Append(history.SumEnergySpentThisTurn(state, player));
            builder.Append(history.CountCardsDrawnOutsideHandDrawThisTurn(state, player));
            builder.Append(history.CountStatusCardsDrawnThisTurn(state, player));

            foreach (CombatSemanticHistory.ActorEvent kind in Enum.GetValues<CombatSemanticHistory.ActorEvent>())
            {
                builder.Append((int)kind);
                builder.Append(history.CountThisTurn(state, kind, player));
                builder.Append(history.CountThisCombat(state, kind, player));
            }

            builder.Append(history.CountOrbsChanneledThisCombat<LightningOrb>(state, player));
            builder.Append(history.CountOrbsChanneledThisCombat<FrostOrb>(state, player));
            builder.Append(history.CountOrbsChanneledThisCombat<DarkOrb>(state, player));
            builder.Append(history.CountOrbsChanneledThisCombat<PlasmaOrb>(state, player));
            builder.Append(history.CountOrbsChanneledThisCombat<GlassOrb>(state, player));
        }
    }

    internal static int CompareDigest(CombatStateDescriptionDigest left, CombatStateDescriptionDigest right)
    {
        int first = left.First.CompareTo(right.First);
        return first != 0 ? first : left.Second.CompareTo(right.Second);
    }
}
