using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    public bool PrepareExtraPlayerTurn(
        CombatPredictionSimulator simulator,
        Player player,
        out bool hasActiveEmotionChip)
    {
        bool extraTurn = GetAmount<AmbergrisPower>(player.Creature) > 0;
        hasActiveEmotionChip = false;
        foreach (RelicModel relic in RelicsOf(player))
        {
            if (relic.IsMelted)
                continue;
            if (relic is EmotionChip)
                hasActiveEmotionChip = true;
            if (relic is not PaelsEye paelsEye || !ShouldTriggerPaelsEye(paelsEye))
                continue;
            TriggerPaelsEye(simulator, player, paelsEye);
            extraTurn = true;
        }
        return extraTurn;
    }

    public bool PrepareLiveExtraPlayerTurn(
        CombatPredictionSimulator simulator,
        Player player,
        bool paelsEyeTriggers)
    {
        bool extraTurn = GetAmount<AmbergrisPower>(player.Creature) > 0;
        if (!paelsEyeTriggers)
            return extraTurn;

        PaelsEye relic = RelicsOf(player)
            .OfType<PaelsEye>()
            .Single(static relic => !relic.IsMelted);
        TriggerPaelsEye(simulator, player, relic);
        return true;
    }

    public bool ShouldTriggerPaelsEye(PaelsEye relic)
    {
        Player player = relic.Owner;
        StatefulRelicState state = GetStatefulRelicState(relic);
        return state.Current == 0
            && state.Previous != 0
            && GetManualCardsPlayedThisTurn(player.Creature) == 0
            && !(GetPlayerTurnNumber(player) == 1
                && RelicsOf(player).Any(static candidate => !candidate.IsMelted && candidate is WhisperingEarring));
    }

    public bool IsPaelsEyeUnused(PaelsEye relic)
        => GetStatefulRelicState(relic).Current == 0;

    private static void TriggerPaelsEye(
        CombatPredictionSimulator simulator,
        Player player,
        PaelsEye relic)
    {
        foreach (PredictedCard card in simulator.State.GetPlayerCombatState(player).Hand.Cards.ToArray())
            simulator.Exhaust(card);
        if (simulator.IsRecordingActionRelicTriggers)
            simulator.RecordRelicTrigger(relic, "：额外回合");
    }

    public void ConsumeExtraTurnSources(Player player)
    {
        int ambergris = GetAmount<AmbergrisPower>(player.Creature);
        if (ambergris > 0)
            SetAmount<AmbergrisPower>(player.Creature, ambergris - 1);
        foreach (PaelsEye relic in RelicsOf(player).OfType<PaelsEye>().Where(static relic => !relic.IsMelted))
        {
            StatefulRelicState state = GetStatefulRelicState(relic);
            if (state.Current == 0
                && state.Previous != 0
                && GetManualCardsPlayedThisTurn(player.Creature) == 0)
            {
                SetStatefulRelicState(relic, state with { Current = 1 });
            }
        }
    }

    public void TriggerRelicsAfterPotionUsed(
        CombatPredictionSimulator simulator,
        PotionModel potion)
    {
        Player player = potion.Owner;
        foreach (RelicModel relic in RelicsOf(player).Where(static relic => !relic.IsMelted))
        {
            switch (relic)
            {
                case BeltBuckle when !HasAvailablePotion(player):
                {
                    StatefulRelicState state = GetStatefulRelicState(relic);
                    if (state.Current == 0)
                    {
                        Apply<DexterityPower>(
                            player.Creature,
                            relic.DynamicVars.Dexterity.IntValue,
                            player.Creature);
                        SetStatefulRelicState(relic, state with { Current = 1 });
                    }
                    break;
                }
                case ReptileTrinket value:
                    ApplyTemporaryStrengthGain<ReptileTrinketPower>(
                        player.Creature,
                        value.DynamicVars.Strength.IntValue,
                        player.Creature);
                    break;
            }
        }
    }

    private void TriggerRelicsAfterPotionProcured(Player player)
    {
        foreach (BeltBuckle relic in RelicsOf(player)
                     .OfType<BeltBuckle>()
                     .Where(static relic => !relic.IsMelted))
        {
            StatefulRelicState state = GetStatefulRelicState(relic);
            if (state.Current == 0)
                continue;
            Apply<DexterityPower>(
                player.Creature,
                -relic.DynamicVars.Dexterity.IntValue,
                player.Creature);
            SetStatefulRelicState(relic, state with { Current = 0 });
        }
    }

    public void TriggerRelicsAfterHandEmptied(
        CombatPredictionSimulator simulator,
        Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        if (!state.Hand.IsEmpty
            || !RelicsOf(player).Any(static relic => !relic.IsMelted && relic is UnceasingTop))
        {
            return;
        }
        if (state.DrawPile.IsEmpty)
        {
            if (state.DiscardPile.IsEmpty)
                return;
        }
        simulator.Draw(player, 1);
    }

    public void NormalizeGhostSeedCards(CombatPredictionSimulator simulator)
    {
        foreach (Player player in Players)
        {
            if (!RelicsOf(player).Any(static relic => !relic.IsMelted && relic is GhostSeed))
                continue;
            foreach (PredictedCard card in simulator.State.GetPlayerCombatState(player).AllCards)
            {
                CardModel preview = card.Preview;
                if (preview.Rarity == CardRarity.Basic
                    && (preview.Tags.Contains(CardTag.Strike) || preview.Tags.Contains(CardTag.Defend))
                    && !preview.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Ethereal))
                {
                    card.MutablePreview.AddKeyword(CardKeyword.Ethereal);
                }
            }
        }
    }

    public void TriggerBookmarkAfterFlush(
        CombatPredictionSimulator simulator,
        Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        foreach (Bookmark relic in RelicsOf(player).OfType<Bookmark>().Where(static relic => !relic.IsMelted))
        {
            List<PredictedCard> candidates = state.Hand.Cards
                .Where(static card => !card.Preview.EnergyCost.CostsX
                                      && card.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) > 0)
                .ToList();
            if (candidates.Count == 0)
                continue;
            PredictedCard selected = simulator.Rng.CombatCardSelection.NextItem(candidates)
                ?? throw new InvalidOperationException("书签的非零费用保留牌列表非空但没有返回候选。");
            selected.MutablePreview.EnergyCost.AddUntilPlayed(-1);
            simulator.History.CardsSelected([selected]);
        }
    }

    public void TriggerRelicsAfterBlockCleared(
        CombatPredictionSimulator simulator,
        Creature creature)
    {
        Player? player = creature.Player;
        if (player == null)
            return;
        int turn = GetPlayerTurnNumber(player);
        foreach (RelicModel relic in RelicsOf(player).Where(static relic => !relic.IsMelted))
        {
            switch (relic)
            {
                case CaptainsWheel when turn == 3:
                case HornCleat when turn == 2:
                    simulator.GainBlock(creature, relic.DynamicVars.Block.BaseValue, ValueProp.Unpowered);
                    break;
                case SparklingRouge when turn == 3:
                    Apply<StrengthPower>(creature, relic.DynamicVars.Strength.IntValue, creature);
                    Apply<DexterityPower>(creature, relic.DynamicVars.Dexterity.IntValue, creature);
                    break;
            }
        }
    }

    public void TriggerRelicsAfterSideTurnEnd(
        CombatPredictionSimulator simulator,
        IReadOnlyList<Creature> participants,
        int etherealExhaustCount)
    {
        foreach (RelicModel relic in Players
                     .SelectMany(RelicsOf)
                     .Where(relic => !relic.IsMelted && participants.Contains(relic.Owner.Creature)))
        {
            switch (relic)
            {
                case JossPaper value when etherealExhaustCount > 0:
                {
                    int threshold = value.DynamicVars["ExhaustAmount"].IntValue;
                    int exhausted = RelicPredictionStateSupport.GetJossPaperCardsExhausted(simulator, value)
                        + etherealExhaustCount;
                    int draws = exhausted / threshold;
                    RelicPredictionStateSupport.SetJossPaperCardsExhausted(
                        simulator,
                        value,
                        exhausted % threshold);
                    if (draws <= 0)
                        break;
                    simulator.Draw(value.Owner, draws);
                    break;
                }
                case LunarPastry value:
                    simulator.GainStars(value.Owner, value.DynamicVars.Stars.IntValue);
                    break;
                case ParryingShield value
                    when simulator.State.GetCreature(value.Owner.Creature).Block >= value.DynamicVars.Block.IntValue:
                {
                    Creature? target = simulator.Rng.CombatTargets.NextItem(
                        HittableEnemies.Where(simulator.State.IsHittable));
                    if (target != null)
                    {
                        using (simulator.PushDamageSource(
                            CombatDamageSource.For(CombatDamageSourceKind.Relic, value.Id.Entry)))
                        {
                            simulator.Damage(
                                target,
                                value.DynamicVars.Damage.BaseValue,
                                ValueProp.Unpowered,
                                value.Owner.Creature);
                        }
                    }
                    break;
                }
            }
            RelicPredictionStateSupport.ResetAfterSideTurnEnd(simulator, relic);
        }
    }

    public void RecordRelicRoundDamage(
        CombatPredictionSimulator simulator,
        Player player,
        int historyEntryStart)
    {
        foreach (CombatPredictionHistoryEntry entry in simulator.History.EntriesFrom(historyEntryStart))
            RecordRelicDamageEntry(entry);
        foreach (EmotionChip relic in RelicsOf(player).OfType<EmotionChip>().Where(static relic => !relic.IsMelted))
        {
            StatefulRelicState state = GetStatefulRelicState(relic);
            SetStatefulRelicState(relic, new StatefulRelicState(state.Previous, 0));
        }
    }

    public void RecordRelicDamageEntry(CombatPredictionHistoryEntry historyEntry)
    {
        if (historyEntry is not CombatPredictionDamageReceivedEntry entry
            || entry.Result.UnblockedDamage <= 0
            || entry.Receiver.Player is not { } player)
        {
            return;
        }
        foreach (EmotionChip relic in RelicsOf(player).OfType<EmotionChip>().Where(static relic => !relic.IsMelted))
        {
            StatefulRelicState state = GetStatefulRelicState(relic);
            SetStatefulRelicState(relic, state with { Previous = 1 });
        }
    }

    public void TriggerEmotionChip(
        CombatPredictionSimulator simulator,
        EmotionChip relic)
    {
        StatefulRelicState state = GetStatefulRelicState(relic);
        if (state.Current == 0)
            return;
        int historyEntryStart = simulator.History.Entries.Count;
        foreach (OrbModel orb in simulator.State.GetPlayerCombatState(relic.Owner).OrbQueue.Orbs.ToArray())
            simulator.TriggerOrbPassive(orb, null);
        TriggeredPowerSupport.CompensateHistorySince(simulator, this, historyEntryStart);
    }

    private bool HasAvailablePotion(Player player)
    {
        for (int slot = 0; slot < PotionSlotCount(player); slot++)
        {
            if (IsPotionAvailable(player, slot))
                return true;
        }
        return false;
    }
}
