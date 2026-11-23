using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    private const byte ResultPileRemoveFromCombat = 0xFF;
    private const int MaximumNestedDrawDepth = 100;

    [ThreadStatic]
    private static bool t_requestEndPlayerTurn;

    internal static void RequestPlayerTurnEnd()
        => t_requestEndPlayerTurn = true;

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

        ref HotCard handCard = ref s.Cards[handIdx];
        bool costsX = (handCard.Flags & HotCardFlags.CostsX) != 0;
        bool starCostsX = (handCard.Flags & HotCardFlags.HasStarCostX) != 0;
        int energyValue = costsX ? s.Energy : GetEffectiveEnergyCost(in s, in handCard, cold, rules);
        int starValue = starCostsX ? s.Stars : GetEffectiveStarCost(in s, in handCard, cold, rules);

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

        int playIdx = HotPiles.MoveCard(ref s, handIdx, HotPileKind.Play, s.PlayCount);
        int playOffsetInPlay = playIdx - s.PlayStart;

        if (energyValue > 0 && (s.ActiveHookMask & (ulong)HotHookMask.AfterEnergySpent) != 0)
        {
            HotHookArgs eArgs = new()
            {
                ActorSlot = 0,
                CardIdx = s.PlayStart + playOffsetInPlay,
                CardDefId = s.Cards[s.PlayStart + playOffsetInPlay].CardDefId,
                EnergySpent = energyValue,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterEnergySpent, ref eArgs);
        }

        if (starValue > 0 && (s.ActiveHookMask & (ulong)HotHookMask.AfterStarsSpent) != 0)
        {
            HotHookArgs sArgs = new()
            {
                ActorSlot = 0,
                CardIdx = s.PlayStart + playOffsetInPlay,
                CardDefId = s.Cards[s.PlayStart + playOffsetInPlay].CardDefId,
                StarsSpent = starValue,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterStarsSpent, ref sArgs);
        }

        t_requestEndPlayerTurn = false;
        HotRegentNecro.ClearPendingEndTurnRequest();
        bool played = ResolveMovedCardInPlay(ref s, ref cold, rules, scratch, playOffsetInPlay, targetEnemySlot, energyValue, starValue, isAutoPlay: false, forceExhaust: false);
        bool endTurnRequested = t_requestEndPlayerTurn | HotRegentNecro.ConsumePendingEndTurnRequest();
        t_requestEndPlayerTurn = false;
        if (played && endTurnRequested && !scratch.NeedsChoice && !CheckWinCondition(ref s, rules) && !IsOverOrEnding(in s))
            ExecuteEndTurn(ref s, ref cold, rules, scratch);
        return played;
    }

    internal static bool AutoPlayCard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int cardIdx,
        int targetEnemySlot = -1,
        bool forceExhaust = false)
    {
        if (IsOverOrEnding(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0 || scratch.NeedsChoice)
            return false;
        if ((uint)cardIdx >= (uint)s.TotalCardCount)
            return false;

        ref readonly HotCard autoCardCheck = ref s.Cards[cardIdx];
        if (autoCardCheck.CardDefId == 0 || autoCardCheck.CardDefId >= rules.Cards.Length)
            return false;

        HotCardDef cardDef = rules.Cards[autoCardCheck.CardDefId];
        if ((autoCardCheck.LocalKeywordsMask & HotCardKeywordMask.Unplayable) != 0
            || !CheckShouldPlayRestrictions(in s, in autoCardCheck, cardDef, rules))
        {
            MoveCardToResultPileWithoutPlaying(ref s, ref cold, rules, scratch, cardIdx, forceExhaust);
            return false;
        }

        TargetType effectiveTarget = GetEffectiveCardTargetType(in s, in autoCardCheck, cardDef, rules);
        if (effectiveTarget == TargetType.AnyEnemy)
        {
            if (targetEnemySlot < 0 || !IsValidEnemyTarget(in s, targetEnemySlot))
            {
                targetEnemySlot = PickRandomLivingEnemySlot(ref s);
                if (targetEnemySlot < 0)
                {
                    MoveCardToResultPileWithoutPlaying(ref s, ref cold, rules, scratch, cardIdx, forceExhaust);
                    return false;
                }
            }
        }

        ref HotCard autoCard = ref s.Cards[cardIdx];
        bool costsX = (autoCard.Flags & HotCardFlags.CostsX) != 0;
        bool starCostsX = (autoCard.Flags & HotCardFlags.HasStarCostX) != 0;
        if (costsX)
            autoCard.CapturedEnergy = (short)s.Energy;

        int energyValue = costsX
            ? s.Energy
            : GetEffectiveEnergyCost(in s, in autoCard, cold, rules, isInPlayablePile: true);
        int starValue = starCostsX
            ? s.Stars
            : GetEffectiveStarCost(in s, in autoCard, cold, rules, isInPlayablePile: true);
        scratch.LastStarsSpent = starValue;

        bool alreadyInPlay = cardIdx >= s.PlayStart && cardIdx < s.PlayStart + s.PlayCount;
        int playIdx = alreadyInPlay ? cardIdx : HotPiles.MoveCard(ref s, cardIdx, HotPileKind.Play, s.PlayCount);
        int playOffsetInPlay = playIdx - s.PlayStart;
        return ResolveMovedCardInPlay(ref s, ref cold, rules, scratch, playOffsetInPlay, targetEnemySlot, energyValue: energyValue, starValue: starValue, isAutoPlay: true, forceExhaust: forceExhaust);
    }

    private static void MoveCardToResultPileWithoutPlaying(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int cardIdx,
        bool forceExhaust)
    {
        if ((uint)cardIdx >= (uint)s.TotalCardCount)
            return;

        bool alreadyInPlay = cardIdx >= s.PlayStart && cardIdx < s.PlayStart + s.PlayCount;
        int playIdx = alreadyInPlay ? cardIdx : HotPiles.MoveCard(ref s, cardIdx, HotPileKind.Play, s.PlayCount);
        ref HotCard card = ref s.Cards[playIdx];

        if ((card.Flags & HotCardFlags.IsDupe) != 0)
        {
            HotPiles.RemoveCard(ref s, playIdx);
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
        }
        else if (forceExhaust
            || (card.Flags & HotCardFlags.ExhaustOnNextPlay) != 0
            || (card.LocalKeywordsMask & HotCardKeywordMask.Exhaust) != 0)
        {
            if ((card.Flags & HotCardFlags.ExhaustOnNextPlay) != 0)
                card.Flags &= ~HotCardFlags.ExhaustOnNextPlay;
            ExhaustCard(ref s, ref cold, rules, scratch, playIdx, causedByEthereal: false);
        }
        else
        {
            HotPiles.MoveCard(ref s, playIdx, HotPileKind.Discard, s.DiscardCount);
        }
    }

    internal static bool AutoPlayTransientCard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        in HotCard card,
        int targetEnemySlot = -1,
        bool forceExhaust = false)
    {
        if (IsOverOrEnding(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0 || scratch.NeedsChoice)
            return false;
        if (card.CardDefId == 0 || card.CardDefId >= rules.Cards.Length)
            return false;

        HotCardDef cardDef = rules.Cards[card.CardDefId];
        if (GetEffectiveCardTargetType(in s, in card, cardDef, rules) == TargetType.AnyEnemy)
        {
            if (targetEnemySlot < 0 || !IsValidEnemyTarget(in s, targetEnemySlot))
            {
                targetEnemySlot = PickRandomLivingEnemySlot(ref s);
                if (targetEnemySlot < 0)
                    return false;
            }
        }

        HotCard copy = card;
        bool costsX = (copy.Flags & HotCardFlags.CostsX) != 0;
        if (costsX)
            copy.CapturedEnergy = (short)s.Energy;

        int energyValue = costsX
            ? s.Energy
            : GetEffectiveEnergyCost(in s, in copy, cold, rules, isInPlayablePile: true);
        int starValue = (copy.Flags & HotCardFlags.HasStarCostX) != 0
            ? s.Stars
            : GetEffectiveStarCost(in s, in copy, cold, rules, isInPlayablePile: true);
        scratch.LastStarsSpent = starValue;

        int playIdx = HotPiles.AddCard(ref s, in copy, HotPileKind.Play, s.PlayCount);
        int playOffsetInPlay = playIdx - s.PlayStart;
        return ResolveMovedCardInPlay(ref s, ref cold, rules, scratch, playOffsetInPlay, targetEnemySlot, energyValue: energyValue, starValue: starValue, isAutoPlay: true, forceExhaust: forceExhaust);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetEnchantedReplayCount(in HotCard card, HotRules rules)
    {
        int count = card.BaseReplayCount + card.ExtraReplayThisTurn;
        if (card.EnchId != 0 && card.EnchId < rules.Enchantments.Length)
        {
            HotEnchantmentId ench = rules.Enchantments[card.EnchId].EnchantmentId;
            if (ench == HotEnchantmentId.Spiral)
                count += card.EnchAmt > 0 ? card.EnchAmt : 1;
            else if (ench == HotEnchantmentId.Glam && card.EnchStatus == 0)
                count += 1;
        }
        return count;
    }

    internal static void AutoPlayFromDrawPile(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int count,
        bool forceExhaust = false)
    {
        if (IsOverOrEnding(in s) || count <= 0 || scratch.NeedsChoice)
            return;

        // STS2 0.113.1 CardPileCmd.AutoPlayFromDrawPile stages all pulled cards into PileType.Play
        // before auto-playing each one in order.
        int cap = Math.Min(count, HotLimits.MaxStackAllocElements);
        Span<ushort> stagedUids = stackalloc ushort[cap];
        int stagedCount = 0;

        for (int i = 0; i < cap && !IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
        {
            if (s.DrawCount == 0 && s.DiscardCount > 0)
            {
                HotPiles.ShuffleDiscardIntoDraw(ref s, rules, scratch, cold);
                if ((s.ActiveHookMask & (ulong)HotHookMask.AfterShuffle) != 0)
                {
                    HotHookArgs shArgs = new() { ActorSlot = 0 };
                    DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterShuffle, ref shArgs);
                    if (scratch.NeedsChoice)
                        return;
                }
            }
            if (s.DrawCount == 0)
                break;

            ushort uid = s.Cards[s.DrawStart].CardUid;
            HotPiles.MoveCard(ref s, s.DrawStart, HotPileKind.Play, s.PlayCount);
            stagedUids[stagedCount++] = uid;
        }

        for (int i = 0; i < stagedCount && !IsOverOrEnding(in s) && (s.Player.Flags & HotCreatureFlags.Alive) != 0 && !scratch.NeedsChoice; i++)
        {
            int playIdx = FindCardIndexByUid(in s, HotPileKind.Play, stagedUids[i]);
            if (playIdx < 0)
                continue;

            if (forceExhaust)
                s.Cards[playIdx].Flags |= HotCardFlags.ExhaustOnNextPlay;

            AutoPlayCard(ref s, ref cold, rules, scratch, playIdx, targetEnemySlot: -1, forceExhaust: forceExhaust);
        }
    }

    private static bool ResolveMovedCardInPlay(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int playOffsetInPlay,
        int targetEnemySlot,
        int energyValue,
        int starValue,
        bool isAutoPlay,
        bool forceExhaust)
    {
        int playIdx = s.PlayStart + playOffsetInPlay;
        scratch.CurrentPlayTargetSlot = targetEnemySlot;

        ref HotCard playCard = ref s.Cards[playIdx];
        ushort playCardUid = playCard.CardUid;
        ushort cardDefId = playCard.CardDefId;
        HotCardDef cardDef = rules.Cards[cardDefId];
        byte targetCreatureSlot = targetEnemySlot >= 0 && targetEnemySlot < s.EnemyCount
            ? (byte)(3 + targetEnemySlot)
            : (byte)0xFF;

        // 1. Determine base result pile (including CardModel.GetResultLocationForCardPlay overrides)
        byte resultPile;
        if ((playCard.Flags & HotCardFlags.IsDupe) != 0 || cardDef.Type == CardType.Power)
        {
            resultPile = ResultPileRemoveFromCombat;
        }
        else if (forceExhaust
            || (playCard.Flags & HotCardFlags.ExhaustOnNextPlay) != 0
            || (playCard.LocalKeywordsMask & HotCardKeywordMask.Exhaust) != 0)
        {
            if ((playCard.Flags & HotCardFlags.ExhaustOnNextPlay) != 0)
                playCard.Flags &= ~HotCardFlags.ExhaustOnNextPlay;
            resultPile = (byte)HotPileKind.Exhaust;
        }
        else
        {
            resultPile = cardDef.CardId switch
            {
                HotCustomCardId.ParticleWall or HotCustomCardId.MourningBlow => (byte)HotPileKind.Hand,
                HotCustomCardId.ShiningStrike => ResultPileDrawTop,
                HotCustomCardId.TrickShot when HotFoolTarot.CountAvailableArrows(in s) > 0 => (byte)HotPileKind.Hand,
                _ => (byte)HotPileKind.Discard,
            };
        }

        // ModifyCardPlayResultLocation hook (Corruption, Rebound, Feral, Nostalgia)
        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyCardPlayResultLocation) != 0)
        {
            HotHookArgs locArgs = new()
            {
                ActorSlot = 0,
                CardIdx = s.PlayStart + playOffsetInPlay,
                CardDefId = cardDefId,
                EnergySpent = energyValue,
                IsAutoPlay = isAutoPlay,
                ResultPile = resultPile,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyCardPlayResultLocation, ref locArgs);
            resultPile = locArgs.ResultPile;
        }

        // 2. Determine play count (Spiral enchantment + ModifyCardPlayCount: Burst, Duplication, EchoForm, OneTwoPunch, SignalBoost, TagTeam, ThrowingAxe)
        int liveIdxForCount = s.PlayStart + playOffsetInPlay;
        int playCount = 1 + GetEnchantedReplayCount(in s.Cards[liveIdxForCount], rules);
        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyCardPlayCount) != 0)
        {
            HotHookArgs cntArgs = new()
            {
                ActorSlot = 0,
                TargetSlot = targetCreatureSlot,
                CardIdx = liveIdxForCount,
                CardDefId = cardDefId,
                PlayCount = playCount,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyCardPlayCount, ref cntArgs);
            playCount = Math.Max(1, cntArgs.PlayCount);
        }

        if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return true;

        // 3. Play execution loop
        for (int playIndex = 0; playIndex < playCount; playIndex++)
        {
            if (IsOverOrEnding(in s))
                break;

            scratch.CurrentPlayIndex = playIndex;
            int curPlayIdx = s.PlayStart + playOffsetInPlay;

            // RecordCardPlayStarted
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

            if (cardDef.CardId == HotCustomCardId.BrightestFlame && curPlayIdx < s.TotalCardCount)
                s.BrightestFlameMaxHpSpent += GetNamedVarInt(in s.Cards[curPlayIdx], cardDef, "MaxHp", 2);

            if (cardDef.MelodyTagMask != 0)
            {
                for (int m = 0; m < 3; m++)
                {
                    if ((cardDef.MelodyTagMask & (1 << m)) != 0)
                    {
                        if (s.Fool.MelodyStarted[m] == 0)
                            s.Fool.FirstPlayCardUid[m] = s.Cards[curPlayIdx].CardUid;
                        s.Fool.MelodyStarted[m]++;
                    }
                }
            }

            // Card-level BeforeCardPlayed (Stomp cost reduction when Attack played)
            if (cardDef.Type == CardType.Attack && (s.ActiveHookMask & (ulong)HotHookMask.BeforeCardPlayed) != 0)
            {
                ushort stompId = rules.GetCardDefId(HotCustomCardId.Stomp);
                if (stompId != 0)
                {
                    int total = s.TotalCardCount;
                    for (int c = 0; c < total; c++)
                    {
                        if (s.Cards[c].CardDefId == stompId)
                            CostAddThisTurn(ref s.Cards[c], ref cold, -1);
                    }
                }
            }

            // BeforeCardPlayed hook (powers, relics, dominion)
            if ((s.ActiveHookMask & (ulong)HotHookMask.BeforeCardPlayed) != 0 || rules.Dominion != HotDominionId.None)
            {
                HotHookArgs beforeArgs = new()
                {
                    ActorSlot = 0,
                    TargetSlot = targetCreatureSlot,
                    CardIdx = s.PlayStart + playOffsetInPlay,
                    CardDefId = cardDefId,
                    EnergySpent = energyValue,
                    StarsSpent = starValue,
                    PlayIndex = playIndex,
                    PlayCount = playCount,
                    IsAutoPlay = isAutoPlay,
                };
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.BeforeCardPlayed, ref beforeArgs);
                if (scratch.NeedsChoice)
                    return true;
            }

            // Execute card effects (bytecode or custom handler)
            ExecuteCardEffects(ref s, ref cold, rules, scratch, playOffsetInPlay, targetEnemySlot, energyValue);
            if (scratch.NeedsChoice)
                return true;
            if (cardDef.CardId == HotCustomCardId.VoidForm)
                t_requestEndPlayerTurn = true;

            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
                break;

            // Execute Enchantment OnPlay (Adroit, Corrupted, Inky, Momentum, Sown, Swift)
            ExecuteEnchantmentOnPlay(ref s, ref cold, rules, scratch, playOffsetInPlay, targetEnemySlot);
            if (scratch.NeedsChoice)
                return true;

            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
                break;

            // RecordCardPlayFinished
            s.HistoryFinishedPlays++;
            curPlayIdx = s.PlayStart + playOffsetInPlay;
            if (cardDef.CardId == HotCustomCardId.Bounce && curPlayIdx < s.TotalCardCount)
                s.Cards[curPlayIdx].CardPrivate = (short)s.PlayerTurnNumber;
            bool playedWasEthereal = curPlayIdx < s.TotalCardCount
                && IsCardEthereal(in s, in s.Cards[curPlayIdx], rules);
            if (playedWasEthereal)
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
            {
                s.Counters[(int)HotCounterSlot.AttacksPlayedThisTurn]++;
                if (rules.HasRelic(HotRelicId.HistoryCourse)
                    && curPlayIdx < s.TotalCardCount
                    && (s.Cards[curPlayIdx].Flags & HotCardFlags.IsDupe) == 0)
                {
                    s.HistoryCourseCard = s.Cards[curPlayIdx];
                    s.HistoryCourseUid = s.Cards[curPlayIdx].CardUid;
                    s.CombatFlags |= HotCombatFlags.HasHistoryCourseCard;
                }
            }
            else if (cardDef.Type == CardType.Skill)
                s.Counters[(int)HotCounterSlot.SkillsPlayedThisTurn]++;

            // Card-level AfterCardPlayed (Pinpoint reduces cost on Skill played; BansheesCry on Ethereal)
            if (cardDef.Type == CardType.Skill)
            {
                ushort pinpointId = rules.GetCardDefId(HotCustomCardId.Pinpoint);
                if (pinpointId != 0)
                {
                    int total = s.TotalCardCount;
                    for (int c = 0; c < total; c++)
                    {
                        if (s.Cards[c].CardDefId == pinpointId)
                            CostAddThisTurn(ref s.Cards[c], ref cold, -1);
                    }
                }
            }
            if (playedWasEthereal)
            {
                ushort bansheeId = rules.GetCardDefId(HotCustomCardId.BansheesCry);
                if (bansheeId != 0)
                {
                    HotCardDef bansheeDef = rules.Cards[bansheeId];
                    int total = s.TotalCardCount;
                    for (int c = 0; c < total; c++)
                    {
                        if (s.Cards[c].CardDefId == bansheeId)
                        {
                            int delta = GetNamedVarInt(in s.Cards[c], bansheeDef, "Energy", 2);
                            if (delta > 0)
                                CostAddThisCombat(ref s.Cards[c], ref cold, -delta);
                        }
                    }
                }
            }

            // AfterCardPlayed hook (powers, relics, dominion)
            if ((s.ActiveHookMask & (ulong)HotHookMask.AfterCardPlayed) != 0 || rules.Dominion != HotDominionId.None)
            {
                HotHookArgs afterArgs = new()
                {
                    ActorSlot = 0,
                    TargetSlot = targetCreatureSlot,
                    CardIdx = s.PlayStart + playOffsetInPlay,
                    CardDefId = cardDefId,
                    EnergySpent = energyValue,
                    StarsSpent = starValue,
                    PlayIndex = playIndex,
                    PlayCount = playCount,
                    IsAutoPlay = isAutoPlay,
                };
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterCardPlayed, ref afterArgs);
                if (scratch.NeedsChoice)
                    return true;
            }

            // MakeItSo.AfterCardPlayedLate: move non-Hand MakeItSo cards to Hand every 3rd Skill played this turn.
            if (cardDef.Type == CardType.Skill && !scratch.NeedsChoice && !IsOverOrEnding(in s))
            {
                HotRegentNecro.OnPlayerSkillFinished(ref s, ref cold, rules, scratch);
                if (scratch.NeedsChoice)
                    return true;
            }

            // RightHandHand.AfterCardPlayedLate: return Discard RightHandHand copies to Hand when EnergyValue >= Energy var (2).
            if (energyValue > 0 && s.DiscardCount > 0 && !scratch.NeedsChoice && !IsOverOrEnding(in s))
                TriggerRightHandHandAfterCardPlayedLate(ref s, rules, energyValue);

            // Enchantment AfterCardPlayed (disable single-use Sown/Swift/Glam/Vigorous, scale Goopy)
            if (playOffsetInPlay < s.PlayCount && s.Cards[s.PlayStart + playOffsetInPlay].CardUid == playCardUid)
                ExecuteEnchantmentAfterPlay(ref s, rules, s.PlayStart + playOffsetInPlay);
        }

        // 4. Move from Play -> Result pile & expire WhenPlayed cost modifiers
        if ((s.Player.Flags & HotCreatureFlags.Alive) != 0)
        {
            int finalPlayIdx = FindCardIndexByUid(in s, HotPileKind.Play, playCardUid);
            if (finalPlayIdx >= 0)
            {
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
                        HotCostModifiers.ExpireAfterPlay(ref s.Cards[dstIdx], ref cold);
                        s.Counters[(int)HotCounterSlot.CardsExhaustedThisTurn]++;
                        FireAfterCardExhausted(ref s, ref cold, rules, scratch, dstIdx, causedByEthereal: false);
                    }
                    else
                    {
                        HotCostModifiers.ExpireAfterPlay(ref s.Cards[finalPlayIdx], ref cold);
                    }
                }
                else if (resultPile == ResultPileDrawTop)
                {
                    if (!IsCombatEnding(in s))
                    {
                        int dstIdx = HotPiles.MoveCard(ref s, finalPlayIdx, HotPileKind.Draw, 0);
                        HotCostModifiers.ExpireAfterPlay(ref s.Cards[dstIdx], ref cold);
                    }
                    else
                    {
                        HotCostModifiers.ExpireAfterPlay(ref s.Cards[finalPlayIdx], ref cold);
                    }
                }
                else if (resultPile == (byte)HotPileKind.Hand)
                {
                    if (!IsCombatEnding(in s))
                    {
                        int dstIdx = MoveCardToHandOrDiscard(ref s, finalPlayIdx);
                        if (dstIdx >= 0)
                            HotCostModifiers.ExpireAfterPlay(ref s.Cards[dstIdx], ref cold);
                    }
                    else
                    {
                        HotCostModifiers.ExpireAfterPlay(ref s.Cards[finalPlayIdx], ref cold);
                    }
                }
                else
                {
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
        }

        return true;
    }

    /// <summary>
    /// Exact STS2 0.113.1 draw loop matching <c>CombatPredictionSimulator.Draw</c>,
    /// <c>ShouldDraw</c>, <c>ShuffleIfNecessary</c> (<c>AfterShuffle</c>), and <c>AfterCardDrawn</c>.
    /// </summary>
    internal static int DrawCards(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int drawCount,
        bool fromHandDraw)
    {
        if (IsOverOrEnding(in s) || drawCount <= 0 || scratch.NeedsChoice)
            return 0;

        if (!fromHandDraw && (s.ActiveHookMask & (ulong)HotHookMask.ShouldDraw) != 0)
        {
            HotHookArgs sdArgs = new()
            {
                ActorSlot = 0,
                FromHandDraw = false,
                BoolFlag = true,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ShouldDraw, ref sdArgs);
            if (!sdArgs.BoolFlag)
                return 0;
        }

        if (scratch.ActiveDrawDepth >= MaximumNestedDrawDepth)
            throw new InvalidOperationException($"Hot draw recursion exceeded depth {MaximumNestedDrawDepth}.");

        scratch.ActiveDrawDepth++;
        try
        {
            int drawn = 0;
            int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;

            for (int i = 0; i < drawCount; i++)
            {
                if (IsOverOrEnding(in s) || s.HandCount >= maxHand || scratch.NeedsChoice)
                    break;

                if (s.DrawCount == 0 && s.DiscardCount > 0)
                {
                    HotPiles.ShuffleDiscardIntoDraw(ref s, rules, scratch, cold);
                    if ((s.ActiveHookMask & (ulong)HotHookMask.AfterShuffle) != 0)
                    {
                        HotHookArgs shArgs = new() { ActorSlot = 0 };
                        DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterShuffle, ref shArgs);
                        if (scratch.NeedsChoice)
                            break;
                    }
                }

                if (s.DrawCount == 0 || s.HandCount >= maxHand)
                    break;

                if (!HotPiles.DrawTopToHand(ref s))
                    break;

                int drawnIdx = s.HandCount - 1;
                ushort drawnDefId = s.Cards[drawnIdx].CardDefId;
                HotCardDef drawnDef = rules.Cards[drawnDefId];

                s.HistoryCardsDrawn++;
                if (!fromHandDraw)
                    s.Counters[(int)HotCounterSlot.NonHandDrawsThisTurn]++;
                if (drawnDef.Type == CardType.Status)
                    s.Counters[(int)HotCounterSlot.StatusCardsDrawnThisTurn]++;

                // Intrinsic card & enchantment OnDraw triggers
                if (drawnDef.CardId == HotCustomCardId.Void)
                {
                    LoseEnergy(ref s, 1);
                }
                else if (drawnDef.CardId == HotCustomCardId.KinglyKick)
                {
                    CostAddThisCombat(ref s.Cards[drawnIdx], ref cold, -1);
                }
                else if (drawnDef.CardId == HotCustomCardId.KinglyPunch)
                {
                    int inc = GetNamedVarInt(in s.Cards[drawnIdx], drawnDef, "Increase", 3);
                    s.Cards[drawnIdx].Var0Delta = (short)(s.Cards[drawnIdx].Var0Delta + inc);
                }

                if (s.Cards[drawnIdx].EnchId != 0
                    && s.Cards[drawnIdx].EnchId < rules.Enchantments.Length
                    && rules.Enchantments[s.Cards[drawnIdx].EnchId].EnchantmentId == HotEnchantmentId.Slither)
                {
                    int rolled = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatEnergyCosts], 4);
                    CostSetThisCombat(ref s.Cards[drawnIdx], ref cold, rolled);
                }

                if ((s.ActiveHookMask & (ulong)HotHookMask.AfterCardDrawn) != 0)
                {
                    HotHookArgs drawArgs = new()
                    {
                        ActorSlot = 0,
                        CardIdx = drawnIdx,
                        CardDefId = drawnDefId,
                        FromHandDraw = fromHandDraw,
                    };
                    DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterCardDrawn, ref drawArgs);
                    if (scratch.NeedsChoice)
                        break;
                }

                drawn++;
            }

            return drawn;
        }
        finally
        {
            scratch.ActiveDrawDepth--;
        }
    }

    internal static void DiscardCard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int cardIdx)
    {
        if (IsOverOrEnding(in s) || (uint)cardIdx >= (uint)s.TotalCardCount)
            return;

        bool isSly = (s.Cards[cardIdx].LocalKeywordsMask & HotCardKeywordMask.Sly) != 0
            || (s.Cards[cardIdx].Flags & HotCardFlags.IsSlyThisTurn) != 0;
        ushort cardUid = s.Cards[cardIdx].CardUid;
        ushort cardDefId = s.Cards[cardIdx].CardDefId;

        int dstIdx = HotPiles.MoveCard(ref s, cardIdx, HotPileKind.Discard, s.DiscardCount);
        s.Counters[(int)HotCounterSlot.CardsDiscardedThisTurn]++;

        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterCardDiscarded) != 0)
        {
            HotHookArgs discArgs = new()
            {
                ActorSlot = 0,
                CardIdx = dstIdx,
                CardDefId = cardDefId,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterCardDiscarded, ref discArgs);
            if (scratch.NeedsChoice)
                return;
        }

        // Sly keyword: automatically plays the discarded card for free (Reflex, Tactician, Sly skills)
        if (isSly && !IsOverOrEnding(in s))
        {
            int liveIdx = FindCardIndexByUid(in s, HotPileKind.Discard, cardUid);
            if (liveIdx >= 0)
                AutoPlayCard(ref s, ref cold, rules, scratch, liveIdx);
        }
    }

    /// <summary>
    /// Exact STS2 0.113.1 <c>CardCmd.DiscardAndDraw(cardsToDiscard, cardsToDraw)</c>:
    /// discards all cards in <paramref name="cardUids"/> first (firing <c>AfterCardDiscarded</c>
    /// without firing Sly yet), then draws <paramref name="drawCount"/> cards, and finally
    /// auto-plays all discarded Sly cards in discard order.
    /// </summary>
    internal static void DiscardCardsAndDraw(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ReadOnlySpan<ushort> cardUids,
        int drawCount)
    {
        if (IsOverOrEnding(in s) || scratch.NeedsChoice)
            return;

        Span<ushort> slyUids = stackalloc ushort[HotLimits.MaxStackAllocElements];
        int slyCount = 0;

        for (int i = 0; i < cardUids.Length; i++)
        {
            ushort uid = cardUids[i];
            if (uid == 0)
                continue;
            int idx = FindCardIndexByUid(in s, HotPileKind.Hand, uid);
            if (idx < 0)
                idx = FindCardIndexByUidAnyPile(in s, uid);
            if (idx < 0)
                continue;

            bool isFromHand = idx < s.HandCount;
            bool isSly = isFromHand
                && ((s.Cards[idx].LocalKeywordsMask & HotCardKeywordMask.Sly) != 0
                    || (s.Cards[idx].Flags & HotCardFlags.IsSlyThisTurn) != 0);
            if (isSly && slyCount < slyUids.Length)
                slyUids[slyCount++] = uid;

            ushort cardDefId = s.Cards[idx].CardDefId;
            int dstIdx = HotPiles.MoveCard(ref s, idx, HotPileKind.Discard, s.DiscardCount);
            s.Counters[(int)HotCounterSlot.CardsDiscardedThisTurn]++;

            if ((s.ActiveHookMask & (ulong)HotHookMask.AfterCardDiscarded) != 0)
            {
                HotHookArgs discArgs = new()
                {
                    ActorSlot = 0,
                    CardIdx = dstIdx,
                    CardDefId = cardDefId,
                };
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterCardDiscarded, ref discArgs);
                if (scratch.NeedsChoice)
                    return;
            }
        }

        if (drawCount > 0 && !IsOverOrEnding(in s) && !scratch.NeedsChoice)
        {
            DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: false);
            if (scratch.NeedsChoice)
                return;
        }

        for (int i = 0; i < slyCount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
        {
            int liveIdx = FindCardIndexByUid(in s, HotPileKind.Discard, slyUids[i]);
            if (liveIdx >= 0)
                AutoPlayCard(ref s, ref cold, rules, scratch, liveIdx);
        }
    }

    internal static void ExhaustCard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int cardIdx,
        bool causedByEthereal = false)
    {
        if (IsOverOrEnding(in s) || (uint)cardIdx >= (uint)s.TotalCardCount)
            return;

        int dstIdx = HotPiles.MoveCard(ref s, cardIdx, HotPileKind.Exhaust, s.ExhaustCount);
        s.Counters[(int)HotCounterSlot.CardsExhaustedThisTurn]++;
        FireAfterCardExhausted(ref s, ref cold, rules, scratch, dstIdx, causedByEthereal);
    }

    private static void FireAfterCardExhausted(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int exhaustedIdx,
        bool causedByEthereal)
    {
        ushort cardDefId = s.Cards[exhaustedIdx].CardDefId;
        HotCardDef def = rules.Cards[cardDefId];

        // Intrinsic card exhaust triggers
        if (def.CardId == HotCustomCardId.DrumOfBattle)
        {
            int eGain = GetNamedVarInt(in s.Cards[exhaustedIdx], def, "Energy", 2);
            GainEnergy(ref s, rules, eGain);
        }

        // Global Midnight cost reduction on any card exhaust
        ushort midnightId = rules.GetCardDefId(HotCustomCardId.Midnight);
        if (midnightId != 0)
        {
            int total = s.TotalCardCount;
            for (int c = 0; c < total; c++)
            {
                if (s.Cards[c].CardDefId == midnightId)
                    CostAddThisCombat(ref s.Cards[c], ref cold, -1);
            }
        }

        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterCardExhausted) != 0)
        {
            HotHookArgs exhArgs = new()
            {
                ActorSlot = 0,
                CardIdx = exhaustedIdx,
                CardDefId = cardDefId,
                CausedByEthereal = causedByEthereal,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterCardExhausted, ref exhArgs);
        }
    }

    internal static bool UpgradeCard(ref CombatHotState s, HotRules rules, int cardIdx)
    {
        if ((uint)cardIdx >= (uint)s.TotalCardCount)
            return false;
        ref HotCard card = ref s.Cards[cardIdx];
        if (card.CardDefId == 0 || card.CardDefId >= rules.Cards.Length)
            return false;
        HotCardDef def = rules.Cards[card.CardDefId];
        if (card.UpgradeLevel >= def.MaxUpgradeLevel)
            return false;

        card.UpgradeLevel++;
        if (card.UpgradeLevel == 1)
        {
            if (def.UpgradedCost != def.BaseCost && card.BaseEnergyCost == def.BaseCost)
                card.BaseEnergyCost = def.UpgradedCost;
            ApplyUpgradeKeywords(ref card, def.CardId);
        }
        return true;
    }

    /// <summary>
    /// Applies STS2 0.113.1 <c>CardModel.OnUpgrade()</c> keyword additions/removals
    /// (updates <c>LocalKeywordsMask</c> only, matching <c>CardModel.AddKeyword/RemoveKeyword</c>
    /// when <c>_isUpgrading == true</c>).
    /// </summary>
    internal static void ApplyUpgradeKeywords(ref HotCard card, HotCustomCardId cardId)
    {
        switch (cardId)
        {
            case HotCustomCardId.Afterimage:
            case HotCustomCardId.Aggression:
            case HotCustomCardId.Arsenal:
            case HotCustomCardId.BeaconOfHope:
            case HotCustomCardId.BigBang:
            case HotCustomCardId.CallOfTheVoid:
            case HotCustomCardId.Entropy:
            case HotCustomCardId.HelloWorld:
            case HotCustomCardId.InfiniteBlades:
            case HotCustomCardId.Juggling:
            case HotCustomCardId.Lullaby:
            case HotCustomCardId.MachineLearning:
            case HotCustomCardId.MadScience:
            case HotCustomCardId.Soulbound:
            case HotCustomCardId.Speedster:
            case HotCustomCardId.Tyranny:
                card.LocalKeywordsMask |= HotCardKeywordMask.Innate;
                break;

            case HotCustomCardId.Anointed:
            case HotCustomCardId.CalculatedGamble:
            case HotCustomCardId.Dredge:
            case HotCustomCardId.GoldAxe:
            case HotCustomCardId.Misery:
            case HotCustomCardId.Monologue:
            case HotCustomCardId.ReaperForm:
            case HotCustomCardId.RoyalGamble:
            case HotCustomCardId.Scrawl:
            case HotCustomCardId.TimesUp:
            case HotCustomCardId.Wish:
            case HotCustomCardId.Wisp:
                card.LocalKeywordsMask |= HotCardKeywordMask.Retain;
                break;

            case HotCustomCardId.Chill:
            case HotCustomCardId.DemonicShield:
            case HotCustomCardId.Discovery:
            case HotCustomCardId.Fusion:
            case HotCustomCardId.Graveblast:
            case HotCustomCardId.Hologram:
            case HotCustomCardId.Hotfix:
            case HotCustomCardId.Ignition:
            case HotCustomCardId.KnowThyPlace:
            case HotCustomCardId.Mimic:
            case HotCustomCardId.Mirage:
            case HotCustomCardId.Prolong:
            case HotCustomCardId.Rainbow:
            case HotCustomCardId.SecretTechnique:
            case HotCustomCardId.SecretWeapon:
            case HotCustomCardId.ThinkingAhead:
            case HotCustomCardId.Transfigure:
            case HotCustomCardId.Underworld:
            case HotCustomCardId.Voltaic:
                card.LocalKeywordsMask &= ~HotCardKeywordMask.Exhaust;
                break;

            case HotCustomCardId.Apparition:
            case HotCustomCardId.EchoForm:
            case HotCustomCardId.VoidForm:
                card.LocalKeywordsMask &= ~HotCardKeywordMask.Ethereal;
                break;
        }
    }

    private static void TriggerRightHandHandAfterCardPlayedLate(
        ref CombatHotState s,
        HotRules rules,
        int energyValue)
    {
        ushort rhhId = rules.GetCardDefId(HotCustomCardId.RightHandHand);
        if (rhhId == 0)
            return;

        HotCardDef rhhDef = rules.Cards[rhhId];
        Span<ushort> rhhUids = stackalloc ushort[HotLimits.MaxStackAllocElements];
        int rhhCount = 0;
        int discStart = s.DiscardStart;
        int discEnd = discStart + s.DiscardCount;
        for (int d = discStart; d < discEnd && rhhCount < rhhUids.Length; d++)
        {
            if (s.Cards[d].CardDefId == rhhId
                && energyValue >= GetNamedVarInt(in s.Cards[d], rhhDef, "Energy", 2))
            {
                rhhUids[rhhCount++] = s.Cards[d].CardUid;
            }
        }
        for (int r = 0; r < rhhCount; r++)
        {
            int dIdx = FindCardIndexByUid(in s, HotPileKind.Discard, rhhUids[r]);
            if (dIdx >= 0)
                MoveCardToHandOrDiscard(ref s, dIdx);
        }
    }

    /// <summary>
    /// Exact STS2 0.113.1 <c>CardModel.AddKeyword</c> outside <c>OnUpgrade()</c>:
    /// updates <c>LocalKeywordsMask</c>, <c>KeywordsAddedOutsideMask</c>, and clears
    /// <c>KeywordsRemovedOutsideMask</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ApplyCardKeywordOutsideUpgrade(ref HotCard card, HotCardKeywordMask keyword)
    {
        card.LocalKeywordsMask |= keyword;
        card.KeywordsAddedOutsideMask |= keyword;
        card.KeywordsRemovedOutsideMask &= ~keyword;
        if ((keyword & HotCardKeywordMask.Sly) != 0)
            card.Flags |= HotCardFlags.IsSlyThisTurn;
    }

    internal static int MoveCardToHandOrDiscard(ref CombatHotState s, int srcIdx)
    {
        if ((uint)srcIdx >= (uint)s.TotalCardCount)
            return -1;
        int maxHand = s.MaxHandSize > 0 ? s.MaxHandSize : 10;
        return s.HandCount < maxHand
            ? HotPiles.MoveCard(ref s, srcIdx, HotPileKind.Hand, s.HandCount)
            : HotPiles.MoveCard(ref s, srcIdx, HotPileKind.Discard, s.DiscardCount);
    }

    internal static void ClonePlayedCardToDiscard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
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

        int addedIdx = HotPiles.AddCard(ref s, in clone, HotPileKind.Discard, s.DiscardCount);
        s.HistoryCardsGenerated++;
        if (rules.Cards[clone.CardDefId].CardId == HotCustomCardId.Anger
            || string.Equals(rules.Cards[clone.CardDefId].Entry, "ANGER", StringComparison.Ordinal))
        {
            s.AngerCopiesGenerated++;
        }

        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterCardEnteredCombat) != 0)
        {
            HotHookArgs enterArgs = new()
            {
                ActorSlot = 0,
                CardIdx = addedIdx,
                CardDefId = clone.CardDefId,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterCardEnteredCombat, ref enterArgs);
        }

        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterCardGenerated) != 0)
        {
            HotHookArgs genArgs = new()
            {
                ActorSlot = 0,
                CardIdx = addedIdx,
                CardDefId = clone.CardDefId,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterCardGenerated, ref genArgs);
        }
    }

    /// <summary>
    /// Phase 1 compatibility 5-arg overload.
    /// </summary>
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
        HotCard created = CreateFreshCard(ref s, def, upgraded: false);
        HotPileKind actualPile = dstPile == HotPileKind.Hand && s.MaxHandSize > 0 && s.HandCount >= s.MaxHandSize
            ? HotPileKind.Discard
            : dstPile;
        int actualOffset = actualPile != dstPile || dstOffset < 0
            ? HotPiles.ZoneCount(in s, actualPile)
            : dstOffset;

        HotPiles.AddCard(ref s, in created, actualPile, actualOffset);
        if (def.HookMask != HotHookMask.None)
            s.ActiveHookMask |= (ulong)def.HookMask;
    }

    /// <summary>
    /// Full Phase 2 card generation helper supporting bottom (<c>-1</c>) and random (<c>-2</c>)
    /// insertion offsets, upgrades, and <c>AfterCardGenerated</c> hooks.
    /// </summary>
    internal static int AddGeneratedFixedCard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ushort cardDefId,
        HotPileKind dstPile,
        int dstOffset = -1,
        bool upgraded = false,
        bool createdByPlayer = true)
    {
        if (IsOverOrEnding(in s) || cardDefId == 0 || cardDefId >= rules.Cards.Length)
            return -1;

        HotCardDef def = rules.Cards[cardDefId];
        HotCard created = CreateFreshCard(ref s, def, upgraded);
        return AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in created, dstPile, dstOffset, createdByPlayer);
    }

    internal static int AddGeneratedCardInstance(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        in HotCard card,
        HotPileKind dstPile,
        int dstOffset = -1,
        bool createdByPlayer = true)
    {
        if (IsOverOrEnding(in s) || card.CardDefId == 0 || card.CardDefId >= rules.Cards.Length)
            return -1;

        HotCardDef def = rules.Cards[card.CardDefId];
        HotPileKind actualPile = dstPile == HotPileKind.Hand && s.MaxHandSize > 0 && s.HandCount >= s.MaxHandSize
            ? HotPileKind.Discard
            : dstPile;

        int zoneCount = HotPiles.ZoneCount(in s, actualPile);
        int actualOffset;
        if (actualPile != dstPile || dstOffset == -1)
            actualOffset = zoneCount;
        else if (dstOffset == -2)
            actualOffset = HotRng.NextInt(ref s.Rng[(int)HotRngStream.Shuffle], zoneCount + 1);
        else
            actualOffset = Math.Clamp(dstOffset, 0, zoneCount);

        int addedIdx = HotPiles.AddCard(ref s, in card, actualPile, actualOffset);
        if (createdByPlayer)
        {
            s.HistoryCardsGenerated++;
            if (def.CardId == HotCustomCardId.Sadness)
                s.Counters[(int)HotCounterSlot.SadnessGeneratedBefore]++;
        }
        // STS2 0.113.1 Aeonglass.AfterCardGeneratedForCombat (Aeonglass.cs:168-184):
        // newly generated Wither cards (e.g. from WitheringPresencePower) immediately match WitherUpgradeCount (AiRegs[1]).
        if (def.CardId == HotCustomCardId.Wither)
        {
            int expectedUpgrade = 0;
            for (int r = 0; r < s.RosterCount; r++)
            {
                byte eSlot = s.ActiveEnemyRoster[r];
                if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                    continue;
                ushort mDefId = s.Enemies[eSlot].MonsterDefId;
                if (mDefId > 0 && mDefId < rules.Monsters.Length && rules.Monsters[mDefId].MonsterId == HotMonsterId.Aeonglass)
                    expectedUpgrade += s.Enemies[eSlot].AiRegs[1];
            }
            if (expectedUpgrade > s.Cards[addedIdx].CardPrivate)
            {
                int diff = expectedUpgrade - s.Cards[addedIdx].CardPrivate;
                s.Cards[addedIdx].CardPrivate = expectedUpgrade;
                s.Cards[addedIdx].Var0Delta += (short)(diff * 3);
            }
        }
        if (def.HookMask != HotHookMask.None)
            s.ActiveHookMask |= (ulong)def.HookMask;

        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterCardEnteredCombat) != 0)
        {
            HotHookArgs enterArgs = new()
            {
                ActorSlot = createdByPlayer ? (byte)0 : (byte)0xFF,
                CardIdx = addedIdx,
                CardDefId = card.CardDefId,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterCardEnteredCombat, ref enterArgs);
        }

        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterCardGenerated) != 0)
        {
            HotHookArgs genArgs = new()
            {
                ActorSlot = createdByPlayer ? (byte)0 : (byte)0xFF,
                CardIdx = addedIdx,
                CardDefId = card.CardDefId,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterCardGenerated, ref genArgs);
        }

        // BansheesCry.AfterCardEnteredCombat: reduce cost by Energy var per Ethereal card already finished this combat (non-clones)
        if (def.CardId == HotCustomCardId.BansheesCry
            && (s.Cards[addedIdx].Flags & HotCardFlags.IsClone) == 0
            && s.HistoryEtherealPlays > 0)
        {
            int delta = GetNamedVarInt(in s.Cards[addedIdx], def, "Energy", 2);
            if (delta > 0)
                CostAddThisCombat(ref s.Cards[addedIdx], ref cold, -(s.HistoryEtherealPlays * delta));
        }

        // RocketPunch: any Status card generated by player reduces RocketPunch energy cost until played
        if (createdByPlayer && def.Type == CardType.Status)
        {
            ushort rocketId = rules.GetCardDefId(HotCustomCardId.RocketPunch);
            if (rocketId != 0)
            {
                int total = s.TotalCardCount;
                for (int c = 0; c < total; c++)
                {
                    if (s.Cards[c].CardDefId == rocketId)
                        CostAddUntilPlayed(ref s.Cards[c], ref cold, -1);
                }
            }
        }

        return addedIdx;
    }

    private static HotCard CreateFreshCard(ref CombatHotState s, HotCardDef def, bool upgraded)
    {
        bool doUpgrade = upgraded && def.MaxUpgradeLevel > 0;
        HotCard card = new()
        {
            CardDefId = def.DefId,
            CardUid = s.NextCardUid++,
            UpgradeLevel = doUpgrade ? (byte)1 : (byte)0,
            BaseEnergyCost = doUpgrade ? def.UpgradedCost : def.BaseCost,
            BaseStarCost = def.BaseStarCost,
            BaseReplayCount = 0,
            ExtraReplayThisTurn = 0,
            LocalKeywordsMask = (HotCardKeywordMask)def.BaseKeywords,
            KeywordsAddedOutsideMask = HotCardKeywordMask.None,
            KeywordsRemovedOutsideMask = HotCardKeywordMask.None,
            Flags = GetInitialCardFlags(def),
        };
        if (doUpgrade)
            ApplyUpgradeKeywords(ref card, def.CardId);
        return card;
    }
}
