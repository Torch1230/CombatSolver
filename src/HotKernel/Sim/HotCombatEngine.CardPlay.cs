using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    private const byte ResultPileRemoveFromCombat = 0xFF;
    private const int MaximumNestedDrawDepth = 100;

    /// <summary>
    /// Full manual card-play lifecycle matching <c>CombatPredictionSimulator.ManualPlay</c>,
    /// <c>OnPlayWrapper</c>, <c>ContinueCardPlayExecution</c>, and <c>ContinueCardResultExecution</c>.
    /// Zero managed heap allocations on the normal path.
    /// </summary>
    internal static bool ExecutePlayCard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int handIdx,
        int targetEnemySlot)
    {
        if (!CanPlayCard(in s, cold, rules, handIdx, targetEnemySlot))
            return false;

        // 1. SpendResources (mirrors CardModel.SpendResources)
        ref HotCard handCard = ref s.Cards[handIdx];
        bool costsX = (handCard.Flags & HotCardFlags.CostsX) != 0;
        int energyValue = GetPayableEnergyCost(in s, in handCard, cold);
        int starValue = GetPayableStarCost(in s, in handCard, cold);

        if (costsX)
            handCard.CapturedEnergy = (short)energyValue;
        scratch.LastStarsSpent = starValue;

        if (energyValue > 0)
        {
            s.Counters[(int)HotCounterSlot.EnergySpentThisTurn] += energyValue;
            s.Energy -= energyValue;
        }
        if (starValue > 0)
            s.Stars -= starValue;

        // 2. Move Hand -> Play (mirrors AddDuringManualCardPlay)
        int playIdx = HotPiles.MoveCard(ref s, handIdx, HotPileKind.Play, s.PlayCount);
        int playOffsetInPlay = playIdx - s.PlayStart;
        scratch.CurrentPlayTargetSlot = targetEnemySlot;

        // 3. Determine result pile (mirrors CardResultLocationMirrors.GetBaseResultLocation)
        ref HotCard playCard = ref s.Cards[playIdx];
        HotCardDef cardDef = rules.Cards[playCard.CardDefId];
        byte resultPile;

        if ((playCard.Flags & HotCardFlags.IsDupe) != 0 || cardDef.Type == CardType.Power)
        {
            resultPile = ResultPileRemoveFromCombat;
        }
        else if ((playCard.Flags & HotCardFlags.ExhaustOnNextPlay) != 0
            || (playCard.LocalKeywordsMask & HotCardKeywordMask.Exhaust) != 0)
        {
            if ((playCard.Flags & HotCardFlags.ExhaustOnNextPlay) != 0)
                playCard.Flags &= ~HotCardFlags.ExhaustOnNextPlay;
            resultPile = (byte)HotPileKind.Exhaust;
        }
        else
        {
            resultPile = (byte)HotPileKind.Discard;
        }

        int playCount = 1 + playCard.BaseReplayCount + playCard.ExtraReplayThisTurn;
        if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return true;

        // 4. Play execution loop (mirrors ContinueCardPlayExecution)
        for (int playIndex = 0; playIndex < playCount; playIndex++)
        {
            if (IsOverOrEnding(in s))
                break;

            scratch.CurrentPlayIndex = playIndex;

            // RecordCardPlayStarted (mirrors SimulatedCombatState.RecordCardPlayStarted)
            s.Counters[(int)HotCounterSlot.CardPlayStartsThisTurn]++;
            if (cardDef.Type == CardType.Attack)
            {
                s.Counters[(int)HotCounterSlot.AttackPlayStartsThisTurn]++;
                s.Counters[(int)HotCounterSlot.AttackSkillStartsThisTurn]++;
                if (energyValue == 0)
                    s.Counters[(int)HotCounterSlot.ZeroCostAttackStartsThisTurn]++;
            }
            else if (cardDef.Type == CardType.Skill)
            {
                s.Counters[(int)HotCounterSlot.SkillPlayStartsThisTurn]++;
                s.Counters[(int)HotCounterSlot.AttackSkillStartsThisTurn]++;
            }
            else if (cardDef.Type == CardType.Power)
            {
                s.Counters[(int)HotCounterSlot.PowerPlayStartsThisTurn]++;
            }

            if (cardDef.MelodyTagMask != 0)
            {
                for (int m = 0; m < 3; m++)
                {
                    if ((cardDef.MelodyTagMask & (1 << m)) != 0)
                        s.Fool.MelodyStarted[m]++;
                }
            }

            // Execute compiled bytecode ops or custom handler
            ExecuteCardEffects(ref s, ref cold, rules, scratch, playOffsetInPlay, targetEnemySlot);
            if (scratch.NeedsChoice)
                return true;

            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
                break;

            // RecordCardPlayFinished (mirrors ContinueCardPlayTailExecution.Finish)
            s.HistoryFinishedPlays++;
            int curPlayIdx = s.PlayStart + playOffsetInPlay;
            if ((s.Cards[curPlayIdx].LocalKeywordsMask & HotCardKeywordMask.Ethereal) != 0)
                s.HistoryEtherealPlays++;

            if (cardDef.MelodyTagMask != 0)
            {
                for (int m = 0; m < 3; m++)
                {
                    if ((cardDef.MelodyTagMask & (1 << m)) != 0)
                        s.Fool.MelodyFinished[m]++;
                }
                s.Fool.MelodyCardsFinished++;
            }

            s.Counters[(int)HotCounterSlot.CardsPlayedThisTurn]++;
            if (cardDef.Type == CardType.Attack)
                s.Counters[(int)HotCounterSlot.AttacksPlayedThisTurn]++;
            else if (cardDef.Type == CardType.Skill)
                s.Counters[(int)HotCounterSlot.SkillsPlayedThisTurn]++;
        }

        // 5. Move from Play -> Result pile & expire WhenPlayed cost modifiers
        if ((s.Player.Flags & HotCreatureFlags.Alive) != 0 && playOffsetInPlay < s.PlayCount)
        {
            int finalPlayIdx = s.PlayStart + playOffsetInPlay;

            if (resultPile == ResultPileRemoveFromCombat)
            {
                HotPiles.RemoveCard(ref s, finalPlayIdx);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            }
            else if (resultPile == (byte)HotPileKind.Exhaust)
            {
                if (!IsOverOrEnding(in s))
                {
                    int dstIdx = HotPiles.MoveCard(ref s, finalPlayIdx, HotPileKind.Exhaust, s.ExhaustCount);
                    s.Counters[(int)HotCounterSlot.CardsExhaustedThisTurn]++;
                    HotCostModifiers.ExpireAfterPlay(ref s.Cards[dstIdx], ref cold);
                }
                else
                {
                    HotCostModifiers.ExpireAfterPlay(ref s.Cards[finalPlayIdx], ref cold);
                }
            }
            else
            {
                // Matches CardPileCmd.Add (if (IsEnding) return without moving out of Play pile).
                if (!IsCombatEnding(in s))
                {
                    int dstIdx = HotPiles.MoveCard(ref s, finalPlayIdx, HotPileKind.Discard, s.DiscardCount);
                    HotCostModifiers.ExpireAfterPlay(ref s.Cards[dstIdx], ref cold);
                }
                else
                {
                    HotCostModifiers.ExpireAfterPlay(ref s.Cards[finalPlayIdx], ref cold);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Exact STS2 0.113.1 draw loop matching <c>CombatPredictionSimulator.Draw</c> &amp;
    /// <c>ShuffleIfNecessary</c>: shuffles discard into draw when draw empties, stops at
    /// <c>MaxHandSize</c> or combat end, and updates draw history counters.
    /// </summary>
    internal static int DrawCards(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int drawCount,
        bool fromHandDraw)
    {
        if (IsOverOrEnding(in s) || drawCount <= 0)
            return 0;

        if (scratch.ActiveDrawDepth >= MaximumNestedDrawDepth)
            throw new InvalidOperationException($"Hot draw recursion exceeded depth {MaximumNestedDrawDepth}.");

        scratch.ActiveDrawDepth++;
        try
        {
            int drawn = 0;
            int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;

            for (int i = 0; i < drawCount; i++)
            {
                if (IsOverOrEnding(in s) || s.HandCount >= maxHand)
                    break;

                if (s.DrawCount == 0 && s.DiscardCount > 0)
                    HotPiles.ShuffleDiscardIntoDraw(ref s, rules, scratch, cold);

                if (s.DrawCount == 0 || s.HandCount >= maxHand)
                    break;

                if (!HotPiles.DrawTopToHand(ref s))
                    break;

                int drawnIdx = s.HandCount - 1;
                s.HistoryCardsDrawn++;
                if (!fromHandDraw)
                    s.Counters[(int)HotCounterSlot.NonHandDrawsThisTurn]++;
                if (rules.Cards[s.Cards[drawnIdx].CardDefId].Type == CardType.Status)
                    s.Counters[(int)HotCounterSlot.StatusCardsDrawnThisTurn]++;

                drawn++;
            }

            return drawn;
        }
        finally
        {
            scratch.ActiveDrawDepth--;
        }
    }

    /// <summary>
    /// Clones the currently playing card (e.g., Anger) into the bottom of Discard matching
    /// <c>CombatPredictedCardExtensions.CreateClone</c> &amp; <c>AddGeneratedCardToCombat</c>.
    /// </summary>
    internal static void ClonePlayedCardToDiscard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        int srcPlayIdx)
    {
        if (IsOverOrEnding(in s))
            return;

        HotCard clone = s.Cards[srcPlayIdx];
        ushort srcUid = clone.CardUid;
        ushort cloneUid = s.NextCardUid++;
        clone.CardUid = cloneUid;
        clone.Flags &= ~(HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay);
        clone.Flags |= HotCardFlags.IsClone;

        if ((clone.Flags & HotCardFlags.HasCostOverflow) != 0 && cold?.CostOverflow is { Length: > 0 } overflows)
        {
            for (int i = 0; i < overflows.Length; i++)
            {
                if (overflows[i].CardUid == srcUid)
                {
                    HotCardCostOverflow dup = new(
                        cloneUid,
                        (HotEnergyCostMod[])overflows[i].EnergyMods.Clone(),
                        (HotTempStarCost[])overflows[i].TempStarCosts.Clone());
                    HotCardCostOverflow[] next = new HotCardCostOverflow[overflows.Length + 1];
                    overflows.CopyTo(next, 0);
                    next[overflows.Length] = dup;
                    cold = cold with { CostOverflow = next };
                    break;
                }
            }
        }

        HotPiles.AddCard(ref s, in clone, HotPileKind.Discard, s.DiscardCount);
        s.HistoryCardsGenerated++;
        if (string.Equals(rules.Cards[clone.CardDefId].Entry, "ANGER", StringComparison.Ordinal))
            s.AngerCopiesGenerated++;
    }

    internal static void AddGeneratedFixedCard(
        ref CombatHotState s,
        HotRules rules,
        ushort cardDefId,
        HotPileKind dstPile,
        int dstOffset)
    {
        if (IsOverOrEnding(in s) || cardDefId == 0 || cardDefId >= rules.Cards.Length)
            return;

        HotCardDef def = rules.Cards[cardDefId];
        HotCard created = new()
        {
            CardDefId = cardDefId,
            CardUid = s.NextCardUid++,
            UpgradeLevel = 0,
            BaseEnergyCost = def.BaseCost,
            BaseStarCost = def.BaseStarCost,
            BaseReplayCount = 0,
            ExtraReplayThisTurn = 0,
            LocalKeywordsMask = (HotCardKeywordMask)def.BaseKeywords,
            KeywordsAddedOutsideMask = HotCardKeywordMask.None,
            KeywordsRemovedOutsideMask = HotCardKeywordMask.None,
            Flags = HotCardFlags.None,
        };

        HotPileKind actualPile = dstPile == HotPileKind.Hand && s.MaxHandSize > 0 && s.HandCount >= s.MaxHandSize
            ? HotPileKind.Discard
            : dstPile;
        int actualOffset = actualPile != dstPile ? HotPiles.ZoneCount(in s, actualPile) : dstOffset;

        HotPiles.AddCard(ref s, in created, actualPile, actualOffset);
        s.HistoryCardsGenerated++;
        if (def.HookMask != HotHookMask.None)
            s.ActiveHookMask |= (ulong)def.HookMask;
    }
}
