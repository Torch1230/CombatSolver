using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    /// <summary>
    /// Full vanilla STS2 0.113.1 turn transition running Player Turn End -> Enemy Turn ->
    /// Next Player Turn Start on a single shared <see cref="HotExecScratch.ChoiceCursor"/>.
    /// </summary>
    internal static bool ExecuteEndTurn(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        if (!IsInProgress(in s) || s.TerminalOutcome != HotCombatOutcome.Ongoing)
            return false;
        if (s.CurrentSide != HotCombatSide.Player || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return false;

        if (!RunEndTurnBeforePlayerStart(ref s, ref cold, rules, scratch, out bool takingExtraTurn))
            return true;

        RunNextPlayerTurnStart(ref s, ref cold, rules, scratch, takingExtraTurn);
        return true;
    }

    /// <summary>
    /// Executes Player Turn End + Enemy Turn (including enemy moves, enemy turn-end powers,
    /// duration ticks, and AI advance for next round), pausing right before Next Player Turn Start.
    /// Enables <c>EndTurnPrefix</c> sharing across turn-start choices and fast end-turn risk evaluation.
    /// </summary>
    public static HotStepResult StepEndTurnBeforePlayerStart(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ReadOnlySpan<HotChoiceSelection> choices = default)
    {
        if (!IsInProgress(in s) || s.TerminalOutcome != HotCombatOutcome.Ongoing)
            return new HotStepResult(Executed: false, NeedsChoice: false, Outcome: s.TerminalOutcome);
        if (s.CurrentSide != HotCombatSide.Player || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return new HotStepResult(Executed: false, NeedsChoice: false, Outcome: s.TerminalOutcome);

        scratch.BeginAction(choices);
        RunEndTurnBeforePlayerStart(ref s, ref cold, rules, scratch, out _);
        if (!scratch.NeedsChoice)
            CheckWinCondition(ref s, rules);

        return new HotStepResult(Executed: true, NeedsChoice: scratch.NeedsChoice, Outcome: s.TerminalOutcome);
    }

    /// <summary>
    /// Executes Next Player Turn Start (round/turn increment, counter reset, block clear,
    /// energy reset, Plasma/turn-start powers, <c>BeforeHandDraw</c>, opening hand draw,
    /// and <c>AfterSideTurnStart</c> powers/relics).
    /// </summary>
    public static HotStepResult StepStartPlayerTurn(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ReadOnlySpan<HotChoiceSelection> choices = default)
    {
        if (!IsInProgress(in s) || s.TerminalOutcome != HotCombatOutcome.Ongoing)
            return new HotStepResult(Executed: false, NeedsChoice: false, Outcome: s.TerminalOutcome);
        if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return new HotStepResult(Executed: false, NeedsChoice: false, Outcome: s.TerminalOutcome);

        scratch.BeginAction(choices);
        bool takingExtraTurn = ConsumeExtraTurnFlag(ref s, rules);
        RunNextPlayerTurnStart(ref s, ref cold, rules, scratch, takingExtraTurn);
        if (!scratch.NeedsChoice)
            CheckWinCondition(ref s, rules);

        return new HotStepResult(Executed: true, NeedsChoice: scratch.NeedsChoice, Outcome: s.TerminalOutcome);
    }

    private static bool RunEndTurnBeforePlayerStart(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        out bool takingExtraTurn)
    {
        takingExtraTurn = false;

        // 1. Player Turn End (Phase One -> Orb Passives -> Ethereal/TurnEndInHand -> Hand Flush -> Phase Two)
        if (!RunPlayerTurnEnd(ref s, ref cold, rules, scratch))
            return false;
        if (scratch.NeedsChoice)
            return false;

        // Check Extra Turn (skips enemy turn if active)
        if ((s.ActiveHookMask & (ulong)HotHookMask.ShouldTakeExtraTurn) != 0)
        {
            HotHookArgs exArgs = new() { ActorSlot = 0, BoolFlag = false };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ShouldTakeExtraTurn, ref exArgs);
            if (exArgs.BoolFlag)
            {
                takingExtraTurn = true;
                return true;
            }
        }

        // 2. Enemy Turn (Start -> Poison -> Moves -> End Powers -> Duration Ticks -> AI Advance)
        if (!RunEnemyTurn(ref s, ref cold, rules, scratch))
            return false;

        return !scratch.NeedsChoice;
    }

    private static bool RunPlayerTurnEnd(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        s.PlayerPhase = (byte)PlayerTurnPhase.End;

        // BeforeSideTurnEnd (Plating, Metallicize, Regen, Orichalcum, CloakClasp, RippleBasin, Hailstorm, TheBomb, etc.)
        if ((s.ActiveHookMask & (ulong)HotHookMask.BeforeTurnEnd) != 0 || rules.Dominion != HotDominionId.None)
        {
            HotHookArgs preEndArgs = new()
            {
                Side = HotCombatSide.Player,
                ActorSlot = 0,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.BeforeTurnEnd, ref preEndArgs);
            if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
                return false;
        }

        // BeforeTurnEnd Orb Passives (Lightning, Frost, Dark, Glass)
        if (s.OrbCount > 0)
        {
            TriggerOrbsBeforeTurnEnd(ref s, ref cold, rules, scratch);
            if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
                return false;
        }

        // Ethereal exhaust & Turn-End-In-Hand cards (Burn, Decay, Regret, Doubt, Shame)
        int etherealExhaustCount = 0;
        Span<ushort> turnEndUids = stackalloc ushort[16];
        int turnEndCount = 0;
        int handCursor = 0;
        while (handCursor < s.HandCount)
        {
            HotCardDef hDef = rules.Cards[s.Cards[handCursor].CardDefId];
            if (HasTurnEndInHandEffect(hDef.CardId))
            {
                if (turnEndCount < turnEndUids.Length)
                    turnEndUids[turnEndCount++] = s.Cards[handCursor].CardUid;
                handCursor++;
            }
            else if ((s.Cards[handCursor].LocalKeywordsMask & HotCardKeywordMask.Ethereal) != 0)
            {
                ExhaustCard(ref s, ref cold, rules, scratch, handCursor, causedByEthereal: true);
                etherealExhaustCount++;
                if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
                    return false;
            }
            else
            {
                handCursor++;
            }
        }

        if (turnEndCount > 0)
        {
            int handCountSnapshot = s.HandCount;
            for (int t = 0; t < turnEndCount; t++)
            {
                int idx = FindCardIndexByUid(in s, HotPileKind.Hand, turnEndUids[t]);
                if (idx < 0)
                    continue;
                ExecuteTurnEndInHandCard(ref s, ref cold, rules, scratch, idx, handCountSnapshot);
                if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
                    return false;
            }
        }

        // Well-Laid Plans retain choice before hand flush
        int wlpAmt = GetPowerAmountById(in s, rules, ownerSlot: 0, HotPowerId.WellLaidPlansPower);
        if (wlpAmt > 0 && s.HandCount > 0 && !rules.HasRelic(HotRelicId.RunicPyramid)
            && GetPowerAmountById(in s, rules, 0, HotPowerId.RetainHandPower) == 0)
        {
            ushort wlpCardDefId = rules.GetCardDefId(HotCustomCardId.WellLaidPlans);
            for (int k = 0; k < wlpAmt; k++)
            {
                if (!HasEligibleHandCardForFilter(in s, rules, HotCustomCardId.WellLaidPlans))
                    break;
                if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, wlpCardDefId, 0, 1, out HotChoiceSelection sel))
                    return false;
                if (sel.Kind == HotChoiceKind.None)
                    break;
                int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                if (idx >= 0)
                    s.Cards[idx].Flags |= HotCardFlags.SingleTurnRetain;
            }
        }

        // Hand flush (skipped when RunicPyramid or RetainHandPower is active)
        bool retainWholeHand = rules.HasRelic(HotRelicId.RunicPyramid)
            || GetPowerAmountById(in s, rules, ownerSlot: 0, HotPowerId.RetainHandPower) > 0;
        if (!retainWholeHand)
        {
            handCursor = 0;
            while (handCursor < s.HandCount)
            {
                ref readonly HotCard c = ref s.Cards[handCursor];
                bool retain = (c.Flags & HotCardFlags.SingleTurnRetain) != 0
                    || (c.LocalKeywordsMask & HotCardKeywordMask.Retain) != 0;
                if (!retain)
                    HotPiles.MoveCard(ref s, handCursor, HotPileKind.Discard, s.DiscardCount);
                else
                    handCursor++;
            }
        }

        // CardModel.EndOfTurnCleanup across all combat cards.
        int totalCards = s.TotalCardCount;
        for (int i = 0; i < totalCards; i++)
        {
            ref HotCard c = ref s.Cards[i];
            c.Flags &= ~(HotCardFlags.SingleTurnRetain | HotCardFlags.IsSlyThisTurn);
            c.ExtraReplayThisTurn = 0;
        }
        HotCostModifiers.ExpireTurnEnd(ref s, ref cold);

        // Player Phase Two (AfterSideTurnEnd regular powers & relics)
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterTurnEnd) != 0 || rules.Dominion != HotDominionId.None)
        {
            HotHookArgs afterEndArgs = new()
            {
                Side = HotCombatSide.Player,
                ActorSlot = 0,
                EtherealExhaustCount = etherealExhaustCount,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterTurnEnd, ref afterEndArgs);
            if (scratch.NeedsChoice)
                return false;
        }

        RetireSideTurnEndTemporaryStats(ref s, ref cold, rules, scratch, HotCombatSide.Player);

        s.PlayerPhase = (byte)PlayerTurnPhase.None;
        return !CheckWinCondition(ref s, rules);
    }

    private static bool RunEnemyTurn(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        s.CurrentSide = HotCombatSide.Enemy;

        Span<byte> actingEnemies = stackalloc byte[HotLimits.MaxEnemies];
        Span<short> performedMoveBySlot = stackalloc short[HotLimits.MaxEnemies];
        performedMoveBySlot.Fill(-1);

        int actingCount = s.RosterCount;
        for (int i = 0; i < actingCount; i++)
        {
            byte enemySlot = s.ActiveEnemyRoster[i];
            actingEnemies[i] = enemySlot;
            s.Enemies[enemySlot].CreatureAttacksThisTurn = 0;
        }

        SnapshotTurnStartPowerAmounts(ref s, rules, enemySide: true);

        if ((s.ActiveHookMask & (ulong)HotHookMask.BeforeSideTurnStart) != 0 || rules.Dominion != HotDominionId.None)
        {
            HotHookArgs preStartArgs = new()
            {
                Side = HotCombatSide.Enemy,
                ActorSlot = 0xFF,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.BeforeSideTurnStart, ref preStartArgs);
            if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
                return false;
        }

        // Clear enemy block (unless Barricade / Blur / Burrowed)
        for (int i = 0; i < s.RosterCount; i++)
        {
            byte enemySlot = s.ActiveEnemyRoster[i];
            if (s.Enemies[enemySlot].Vital.Block > 0
                && ShouldClearBlock(ref s, ref cold, rules, scratch, (byte)(3 + enemySlot), out _))
            {
                s.Enemies[enemySlot].Vital.Block = 0;
            }
        }

        // Enemy side turn start: PlatingPower decrements by 1 when RoundNumber > 1.
        ushort platingDefId = rules.PlatingPowerDefId != 0 ? rules.PlatingPowerDefId : rules.GetPowerDefId(HotPowerId.PlatingPower);
        if (s.RoundNumber > 1 && platingDefId != 0)
        {
            for (int i = 0; i < s.RosterCount; i++)
            {
                byte ownerSlot = (byte)(3 + s.ActiveEnemyRoster[i]);
                DecrementPlating(ref s, rules, ownerSlot, platingDefId);
            }
        }

        // Enemy Poison ticks at start of enemy turn (with Accelerant!)
        for (int a = 0; a < actingCount; a++)
        {
            byte enemySlot = actingEnemies[a];
            if (!IsValidEnemyTarget(in s, enemySlot))
                continue;
            TriggerPoison(ref s, ref cold, rules, scratch, (byte)(3 + enemySlot));
            if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
                return false;
        }

        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterSideTurnStart) != 0 || rules.Dominion != HotDominionId.None)
        {
            HotHookArgs startArgs = new()
            {
                Side = HotCombatSide.Enemy,
                ActorSlot = 0xFF,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterSideTurnStart, ref startArgs);
            if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
                return false;
        }

        // Execute living and reviving enemy moves in roster order via HotMonsters.ExecuteMonsterMove
        for (int a = 0; a < actingCount; a++)
        {
            byte enemySlot = actingEnemies[a];
            if (!HotMonsters.CanPerformMonsterMove(in s, enemySlot))
                continue;

            performedMoveBySlot[enemySlot] = s.Enemies[enemySlot].CurrentMoveNode;

            if ((s.Enemies[enemySlot].Flags & HotEnemyFlags.SkipNextMove) != 0)
            {
                s.Enemies[enemySlot].Flags &= ~HotEnemyFlags.SkipNextMove;
                if (CheckWinCondition(ref s, rules))
                    return false;
                continue;
            }

            HotMonsters.ExecuteMonsterMove(ref s, ref cold, rules, scratch, enemySlot);

            if (scratch.NeedsChoice || CheckWinCondition(ref s, rules) || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
                return false;
        }

        // Enemy BeforeTurnEnd powers (Plating, Metallicize, Regen, Doom kill)
        if ((s.ActiveHookMask & (ulong)HotHookMask.BeforeTurnEnd) != 0 || rules.Dominion != HotDominionId.None)
        {
            HotHookArgs enemyPreEnd = new()
            {
                Side = HotCombatSide.Enemy,
                ActorSlot = 0xFF,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.BeforeTurnEnd, ref enemyPreEnd);
            if (scratch.NeedsChoice)
                return false;
        }

        // Enemy AfterTurnEnd powers (Ritual, Nemesis, Tender, Asleep, etc.)
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterTurnEnd) != 0 || rules.Dominion != HotDominionId.None)
        {
            HotHookArgs enemyAfterEnd = new()
            {
                Side = HotCombatSide.Enemy,
                ActorSlot = 0xFF,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterTurnEnd, ref enemyAfterEnd);
            if (scratch.NeedsChoice)
                return false;
        }

        RetireSideTurnEndTemporaryStats(ref s, ref cold, rules, scratch, HotCombatSide.Enemy);

        // TickDurations for ALL creatures (Player and Enemies) at end of Enemy Turn.
        TickAllDurationPowers(ref s, rules);

        if (CheckWinCondition(ref s, rules))
            return false;

        // Prepare monster moves for next round (handles reviving, WaterfallGiant SteamPhase, Stunned, MustPerformOnceBeforeRepeat).
        int remainingRoster = s.RosterCount;
        for (int i = 0; i < remainingRoster; i++)
        {
            byte enemySlot = s.ActiveEnemyRoster[i];
            HotMonsters.PrepareMoveForNextRound(ref s, ref cold, rules, enemySlot, performedMoveBySlot[enemySlot]);
        }

        return true;
    }

    private static void RunNextPlayerTurnStart(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        bool takingExtraTurn)
    {
        s.CurrentSide = HotCombatSide.Player;
        if (!takingExtraTurn)
            s.RoundNumber++;
        s.PlayerTurnNumber++;

        ResetPlayerTurnCounters(ref s, rules);
        SnapshotTurnStartPowerAmounts(ref s, rules, enemySide: false);

        // BeforeSideTurnStart (Aggression, Sloth/VoidForm reset, relic counters, CrackedCore, etc.)
        if ((s.ActiveHookMask & (ulong)HotHookMask.BeforeSideTurnStart) != 0 || rules.Dominion != HotDominionId.None)
        {
            HotHookArgs preStartArgs = new()
            {
                Side = HotCombatSide.Player,
                ActorSlot = 0,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.BeforeSideTurnStart, ref preStartArgs);
            if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
                return;
        }

        // Clear Player Block (always dispatch ShouldClearBlock so HornCleat / CaptainsWheel /
        // SparklingRouge / SturdyClamp fire even when Player starts the turn at 0 Block).
        if (ShouldClearBlock(ref s, ref cold, rules, scratch, ownerSlot: 0, out int lossCap) && s.Player.Block > 0)
            s.Player.Block = lossCap < s.Player.Block ? s.Player.Block - lossCap : 0;

        // AfterBlockCleared delayed block powers (BlockNextTurn, SelfFormingClay, ToricToughness)
        TriggerAfterBlockClearedPowers(ref s, ref cold, rules, scratch);
        if (scratch.NeedsChoice)
            return;

        // Reset Player Energy (unless IceCream) + ModifyMaxEnergy + EnergyNextTurnPower
        int baseMaxEnergy = rules.BaseMaxEnergy > 0
            ? rules.BaseMaxEnergy
            : s.Counters[(int)HotCounterSlot.BaseMaxEnergy];
        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyMaxEnergy) != 0)
        {
            HotHookArgs maxEArgs = new()
            {
                ActorSlot = 0,
                MaxEnergy = baseMaxEnergy,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyMaxEnergy, ref maxEArgs);
            baseMaxEnergy = Math.Max(0, maxEArgs.MaxEnergy);
        }

        int energyNextTurn = 0;
        ushort entDefId = rules.GetPowerDefId(HotPowerId.EnergyNextTurnPower);
        if (entDefId != 0)
        {
            energyNextTurn = GetPowerAmount(in s, 0, entDefId);
            if (energyNextTurn > 0)
                SetPowerAmount(ref s, rules, 0, entDefId, 0);
        }

        if (!rules.HasRelic(HotRelicId.IceCream))
            s.Energy = 0;
        s.Energy += Math.Max(0, baseMaxEnergy) + energyNextTurn;

        // AfterEnergyReset powers (Genesis, LightningRod, Radiance, Spinner, StarNextTurn)
        TriggerAfterEnergyResetPowers(ref s, ref cold, rules, scratch);
        if (scratch.NeedsChoice)
            return;

        // Plasma Orb turn-start passives
        if (s.OrbCount > 0)
        {
            TriggerOrbsAfterTurnStart(ref s, ref cold, rules, scratch);
            if (scratch.NeedsChoice)
                return;
        }

        // BeforeHandDraw (InfiniteBlades, Nightmare, ForegoneConclusion, CreativeAI, HelloWorld, CallOfTheVoid, SpectrumShift, SentryMode)
        s.PlayerPhase = (byte)PlayerTurnPhase.Start;
        if ((s.ActiveHookMask & (ulong)HotHookMask.BeforeHandDraw) != 0 || rules.Dominion != HotDominionId.None)
        {
            HotHookArgs bhdArgs = new()
            {
                ActorSlot = 0,
                Side = HotCombatSide.Player,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.BeforeHandDraw, ref bhdArgs);
            if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
                return;
        }

        // ModifyHandDraw + Opening Hand Draw
        int baseDraw = rules.BaseHandDraw > 0
            ? rules.BaseHandDraw
            : (s.Counters[(int)HotCounterSlot.BaseHandDraw] > 0 ? s.Counters[(int)HotCounterSlot.BaseHandDraw] : 5);
        int drawCount = baseDraw + s.DrawNextTurn;
        s.DrawNextTurn = 0;

        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyHandDraw) != 0)
        {
            HotHookArgs hdArgs = new()
            {
                ActorSlot = 0,
                HandDrawCount = drawCount,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyHandDraw, ref hdArgs);
            drawCount = Math.Max(0, hdArgs.HandDrawCount);
        }

        ushort drawNextTurnDefId = rules.GetPowerDefId(HotPowerId.DrawCardsNextTurnPower);
        if (drawNextTurnDefId != 0 && GetPowerAmount(in s, 0, drawNextTurnDefId) > 0)
            SetPowerAmount(ref s, rules, 0, drawNextTurnDefId, 0);

        DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: true);
        if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
            return;

        // Player side turn start: PlatingPower decrements by 1 when PlayerTurnNumber != 1.
        ushort platingDefId = rules.PlatingPowerDefId != 0 ? rules.PlatingPowerDefId : rules.GetPowerDefId(HotPowerId.PlatingPower);
        if (s.PlayerTurnNumber != 1 && platingDefId != 0)
            DecrementPlating(ref s, rules, ownerSlot: 0, platingDefId);

        // Player Poison tick (if poisoned)
        TriggerPoison(ref s, ref cold, rules, scratch, targetSlot: 0);
        if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
            return;

        // AfterSideTurnStart (BiasedCognition, Coolant, DemonForm, NoxiousFumes, WraithForm, Loop, ToolsOfTheTrade, Tyranny, relics)
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterSideTurnStart) != 0 || rules.Dominion != HotDominionId.None)
        {
            HotHookArgs afterStartArgs = new()
            {
                Side = HotCombatSide.Player,
                ActorSlot = 0,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterSideTurnStart, ref afterStartArgs);
            if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
                return;
        }

        // AfterAutoPrePlayPhaseEnteredEarly: auto-play exhausted Bombardment cards BEFORE MayhemPower.
        HotRegentNecro.TriggerExhaustedBombardments(ref s, ref cold, rules, scratch);
        if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
            return;

        // AfterAutoPrePlayPhaseEntered: MayhemPower auto-plays top card(s) of DrawPile.
        ushort mayhemDefId = rules.GetPowerDefId(HotPowerId.MayhemPower);
        if (mayhemDefId != 0)
        {
            int mayhemAmt = GetPowerAmount(in s, 0, mayhemDefId);
            if (mayhemAmt > 0)
            {
                AutoPlayFromDrawPile(ref s, ref cold, rules, scratch, mayhemAmt, forceExhaust: false);
                if (scratch.NeedsChoice || CheckWinCondition(ref s, rules))
                    return;
            }
        }

        s.PlayerPhase = (byte)PlayerTurnPhase.None;
        CheckWinCondition(ref s, rules);
    }

    private static bool HasTurnEndInHandEffect(HotCustomCardId cardId)
        => cardId is HotCustomCardId.Burn
            or HotCustomCardId.Decay
            or HotCustomCardId.Regret
            or HotCustomCardId.Doubt
            or HotCustomCardId.Shame;

    private static void ExecuteTurnEndInHandCard(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int handIdx,
        int handCountSnapshot)
    {
        HotCard c = s.Cards[handIdx];
        HotCardDef def = rules.Cards[c.CardDefId];

        switch (def.CardId)
        {
            case HotCustomCardId.Burn:
            {
                int dmg = c.UpgradeLevel > 0 ? 4 : 2;
                DealDamage(ref s, ref cold, rules, scratch, 0xFF, 0, dmg, isPoweredAttack: false);
                break;
            }
            case HotCustomCardId.Decay:
                DealDamage(ref s, ref cold, rules, scratch, 0xFF, 0, 2, isPoweredAttack: false);
                break;
            case HotCustomCardId.Regret:
                if (handCountSnapshot > 0)
                    DealDamage(ref s, ref cold, rules, scratch, 0xFF, 0, handCountSnapshot, isPoweredAttack: false, isUnblockable: true);
                break;
            case HotCustomCardId.Doubt:
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0xFF, HotPowerId.WeakPower, 1);
                break;
            case HotCustomCardId.Shame:
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0xFF, HotPowerId.FrailPower, 1);
                break;
        }

        int liveIdx = FindCardIndexByUid(in s, HotPileKind.Hand, c.CardUid);
        if (liveIdx >= 0 && !IsOverOrEnding(in s))
        {
            if ((s.Cards[liveIdx].LocalKeywordsMask & HotCardKeywordMask.Ethereal) != 0)
                ExhaustCard(ref s, ref cold, rules, scratch, liveIdx, causedByEthereal: true);
            else
                HotPiles.MoveCard(ref s, liveIdx, HotPileKind.Discard, s.DiscardCount);
        }
    }

    private static void TriggerAfterBlockClearedPowers(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        ushort bntId = rules.GetPowerDefId(HotPowerId.BlockNextTurnPower);
        if (bntId != 0)
        {
            int amt = GetPowerAmount(in s, 0, bntId);
            if (amt > 0)
            {
                SetPowerAmount(ref s, rules, 0, bntId, 0);
                GainBlock(ref s, ref cold, rules, scratch, 0, amt, isCardBlock: false);
                if (scratch.NeedsChoice)
                    return;
            }
        }

        ushort clayId = rules.GetPowerDefId(HotPowerId.SelfFormingClayPower);
        if (clayId != 0)
        {
            int amt = GetPowerAmount(in s, 0, clayId);
            if (amt > 0)
            {
                SetPowerAmount(ref s, rules, 0, clayId, 0);
                GainBlock(ref s, ref cold, rules, scratch, 0, amt, isCardBlock: false);
                if (scratch.NeedsChoice)
                    return;
            }
        }

        ushort toricId = rules.GetPowerDefId(HotPowerId.ToricToughnessPower);
        if (toricId != 0)
        {
            int i = 0;
            while (i < s.PowerCount)
            {
                if (s.Powers[i].OwnerSlot == 0 && s.Powers[i].PowerDefId == toricId && s.Powers[i].Amount > 0)
                {
                    int blk = s.Powers[i].Aux0 > 0 ? s.Powers[i].Aux0 : 5;
                    DecrementPowerAt(ref s, rules, i);
                    GainBlock(ref s, ref cold, rules, scratch, 0, blk, isCardBlock: false);
                    if (scratch.NeedsChoice)
                        return;
                    continue;
                }
                i++;
            }
        }
    }

    private static void TriggerAfterEnergyResetPowers(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        int i = 0;
        while (i < s.PowerCount)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot != 0 || p.Amount <= 0 || p.PowerDefId >= rules.Powers.Length)
            {
                i++;
                continue;
            }

            HotPowerId pid = rules.Powers[p.PowerDefId].PowerId;
            int amt = p.Amount;
            switch (pid)
            {
                case HotPowerId.GenesisPower:
                    GainStars(ref s, ref cold, rules, scratch, amt);
                    i++;
                    break;

                case HotPowerId.LightningRodPower:
                    ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Lightning);
                    if (scratch.NeedsChoice)
                        return;
                    DecrementPowerAt(ref s, rules, i);
                    break;

                case HotPowerId.RadiancePower:
                    GainEnergy(ref s, rules, 1);
                    DecrementPowerAt(ref s, rules, i);
                    break;

                case HotPowerId.SpinnerPower:
                    for (int k = 0; k < amt && !scratch.NeedsChoice; k++)
                        ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Glass);
                    i++;
                    break;

                case HotPowerId.StarNextTurnPower:
                    RemovePowerAt(ref s, i);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    GainStars(ref s, ref cold, rules, scratch, amt);
                    break;

                default:
                    i++;
                    break;
            }
        }
    }

    private static bool ConsumeExtraTurnFlag(ref CombatHotState s, HotRules rules)
    {
        ushort extraId = rules.GetPowerDefId(HotPowerId.ExtraTurnPower);
        if (extraId == 0)
            return false;
        int idx = FindPowerIndex(in s, 0, extraId);
        if (idx < 0 || s.Powers[idx].Amount <= 0)
            return false;
        DecrementPowerAt(ref s, rules, idx);
        return true;
    }

    private static void TickAllDurationPowers(ref CombatHotState s, HotRules rules)
    {
        bool hookMaskDirty = false;
        int i = 0;
        while (i < s.PowerCount)
        {
            ref HotPower p = ref s.Powers[i];
            if (p.PowerDefId < rules.Powers.Length)
            {
                HotPowerDef def = rules.Powers[p.PowerDefId];
                bool ticksHere = def.HasRelevantSkipDurationTick
                    || def.PowerId is HotPowerId.IntangiblePower or HotPowerId.NoBlockPower;
                if (ticksHere)
                {
                    if ((p.Flags & HotPowerFlags.SkipNextDurationTick) != 0)
                    {
                        p.Flags &= ~HotPowerFlags.SkipNextDurationTick;
                        i++;
                    }
                    else if (p.Amount > 1)
                    {
                        p.Amount--;
                        i++;
                    }
                    else if (p.Amount == 1)
                    {
                        RemovePowerAt(ref s, i);
                        hookMaskDirty = true;
                    }
                    else
                    {
                        i++;
                    }
                    continue;
                }
            }
            i++;
        }

        if (hookMaskDirty)
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
    }

    private static void DecrementPlating(ref CombatHotState s, HotRules rules, byte ownerSlot, ushort platingDefId)
    {
        int idx = FindPowerIndex(in s, ownerSlot, platingDefId);
        if (idx < 0 || s.Powers[idx].Amount <= 0)
            return;

        DecrementPowerAt(ref s, rules, idx);
    }

    private static void SnapshotTurnStartPowerAmounts(ref CombatHotState s, HotRules rules, bool enemySide)
    {
        int count = s.PowerCount;
        for (int i = 0; i < count; i++)
        {
            ref HotPower p = ref s.Powers[i];
            bool isEnemyPower = p.OwnerSlot >= 3;
            if (isEnemyPower != enemySide)
                continue;
            if (p.PowerDefId < rules.Powers.Length && rules.Powers[p.PowerDefId].HasRelevantTurnStartAmount)
                p.AmountOnTurnStart = p.Amount;
        }
    }

    private static void ResetPlayerTurnCounters(ref CombatHotState s, HotRules rules)
    {
        s.Counters[(int)HotCounterSlot.StatusCardsDrawnThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.ZeroCostAttackStartsThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.CardPlayStartsThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.AttackSkillStartsThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.AttackPlayStartsThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.SkillPlayStartsThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.PowerPlayStartsThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.CardsExhaustedThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.CardsDiscardedThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.EnergySpentThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.StarsGainedThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.NonHandDrawsThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.HasLostHpThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.WasDoomAppliedThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.AttacksPlayedThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.SkillsPlayedThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.CardsPlayedThisTurn] = 0;
        s.Counters[(int)HotCounterSlot.OstyAttacksThisTurn] = 0;

        for (int m = 0; m < 3; m++)
        {
            s.Fool.MelodyStarted[m] = 0;
            s.Fool.MelodyFinished[m] = 0;
            s.Fool.FirstPlayCardUid[m] = 0;
        }
        s.Fool.MelodyCardsFinished = 0;

        for (int e = 0; e < s.EnemyCount; e++)
            s.Enemies[e].PoweredHitsFromPlayer = 0;

        // Reset per-turn power auxiliary counters (Unmovable, Sloth, VoidForm, ChainsOfBinding)
        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            if (s.Powers[i].OwnerSlot != 0 || s.Powers[i].PowerDefId >= rules.Powers.Length)
                continue;

            switch (rules.Powers[s.Powers[i].PowerDefId].PowerId)
            {
                case HotPowerId.UnmovablePower:
                case HotPowerId.SlothPower:
                case HotPowerId.VoidFormPower:
                case HotPowerId.ChainsOfBindingPower:
                    s.Powers[i].Aux0 = 0;
                    break;
            }
        }
    }
}
