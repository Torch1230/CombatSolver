using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    /// <summary>
    /// Full vanilla STS2 0.113.1 turn transition matching <c>CombatBeamSolver.AdvanceRound</c>,
    /// <c>CombatBeamSolver.AdvanceRoundPlayerStart</c>, <c>LiveEndTurnRiskEvaluator</c>, and
    /// <c>CombatPredictionSimulator.EndTurn</c>:
    /// Player Turn End -> Hand Flush &amp; Cleanup -> Enemy Turn (Clear Block, Turn Start, Moves,
    /// Turn End, Duration Ticks, AI Advance) -> Next Player Turn Start (Clear Block, Reset Energy,
    /// Draw Hand, Turn Start Powers).
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

        // 1. Player Turn End (Phase One -> Hand Flush -> Phase Two)
        if (!RunPlayerTurnEnd(ref s, ref cold, rules))
            return true;

        // 2. Enemy Turn (Start -> Moves -> End Powers -> Duration Ticks -> AI Advance)
        if (!RunEnemyTurn(ref s, ref cold, rules, scratch))
            return true;

        // 3. Next Player Turn Start (Counters -> Block Clear -> Energy Reset -> Draw -> Side Start)
        RunNextPlayerTurnStart(ref s, ref cold, rules, scratch);
        return true;
    }

    private static bool RunPlayerTurnEnd(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules)
    {
        s.PlayerPhase = (byte)PlayerTurnPhase.End;

        // BeforeSideTurnEndEarly / BeforeSideTurnEnd: PlatingPower & MetallicizePower gain unpowered block.
        if ((s.ActiveHookMask & (ulong)HotHookMask.BeforeTurnEnd) != 0)
        {
            if (rules.PlatingPowerDefId != 0)
            {
                int plating = GetPowerAmount(in s, ownerSlot: 0, rules.PlatingPowerDefId);
                if (plating > 0)
                    GainBlock(ref s, rules, targetSlot: 0, baseAmount: plating, isCardBlock: false);
            }
            if (rules.MetallicizePowerDefId != 0)
            {
                int metallicize = GetPowerAmount(in s, ownerSlot: 0, rules.MetallicizePowerDefId);
                if (metallicize > 0)
                    GainBlock(ref s, rules, targetSlot: 0, baseAmount: metallicize, isCardBlock: false);
            }
        }

        if (CheckWinCondition(ref s, rules))
            return false;

        // Ethereal exhaust (forward hand order, matches CombatPredictionSimulator.DoTurnEnd).
        int handCursor = 0;
        while (handCursor < s.HandCount)
        {
            if ((s.Cards[handCursor].LocalKeywordsMask & HotCardKeywordMask.Ethereal) != 0)
            {
                HotPiles.MoveCard(ref s, handCursor, HotPileKind.Exhaust, s.ExhaustCount);
                s.Counters[(int)HotCounterSlot.CardsExhaustedThisTurn]++;
            }
            else
            {
                handCursor++;
            }
        }

        if (CheckWinCondition(ref s, rules))
            return false;

        // Hand flush: non-retained hand cards move to bottom of Discard (does NOT count as Discard effect).
        handCursor = 0;
        while (handCursor < s.HandCount)
        {
            ref readonly HotCard c = ref s.Cards[handCursor];
            bool retain = (c.Flags & HotCardFlags.SingleTurnRetain) != 0
                || (c.LocalKeywordsMask & HotCardKeywordMask.Retain) != 0;
            if (!retain)
            {
                HotPiles.MoveCard(ref s, handCursor, HotPileKind.Discard, s.DiscardCount);
            }
            else
            {
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

        // Player Phase Two (AfterSideTurnEnd regular powers, e.g. Player RitualPower).
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterTurnEnd) != 0 && rules.RitualPowerDefId != 0)
        {
            int ritual = GetPowerAmount(in s, ownerSlot: 0, rules.RitualPowerDefId);
            if (ritual > 0 && rules.StrengthPowerDefId != 0)
                ApplyPower(ref s, rules, ownerSlot: 0, applierSlot: 0, rules.StrengthPowerDefId, ritual);
        }

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

        // Snapshot living roster order before enemy turn starts (matches actingEnemies in AdvanceRound).
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

        // Clear enemy block (unless Barricade or similar preventer).
        for (int i = 0; i < s.RosterCount; i++)
        {
            byte enemySlot = s.ActiveEnemyRoster[i];
            ref HotEnemy enemy = ref s.Enemies[enemySlot];
            if (enemy.Vital.Block > 0 && ShouldClearBlock(in s, rules, (byte)(3 + enemySlot)))
                enemy.Vital.Block = 0;
        }

        // Enemy side turn start: PlatingPower decrements by 1 when RoundNumber > 1.
        if (s.RoundNumber > 1 && rules.PlatingPowerDefId != 0)
        {
            for (int i = 0; i < s.RosterCount; i++)
            {
                byte ownerSlot = (byte)(3 + s.ActiveEnemyRoster[i]);
                DecrementPlating(ref s, rules, ownerSlot);
            }
        }

        if (CheckWinCondition(ref s, rules))
            return false;

        // Execute living enemy moves in roster order.
        for (int a = 0; a < actingCount; a++)
        {
            byte enemySlot = actingEnemies[a];
            if (!IsValidEnemyTarget(in s, enemySlot))
                continue;

            performedMoveBySlot[enemySlot] = s.Enemies[enemySlot].CurrentMoveNode;

            if ((s.Enemies[enemySlot].Flags & HotEnemyFlags.SkipNextMove) != 0)
            {
                s.Enemies[enemySlot].Flags &= ~HotEnemyFlags.SkipNextMove;
                if (CheckWinCondition(ref s, rules))
                    return false;
                continue;
            }

            ExecuteMonsterMove(ref s, ref cold, rules, scratch, enemySlot);

            if (CheckWinCondition(ref s, rules) || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
                return false;
        }

        // Enemy Turn End powers: Plating / Metallicize block gain, Ritual strength gain, then Duration ticks.
        if ((s.ActiveHookMask & (ulong)HotHookMask.BeforeTurnEnd) != 0)
        {
            for (int i = 0; i < s.RosterCount; i++)
            {
                byte enemySlot = s.ActiveEnemyRoster[i];
                if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                    continue;
                byte ownerSlot = (byte)(3 + enemySlot);

                if (rules.PlatingPowerDefId != 0)
                {
                    int plating = GetPowerAmount(in s, ownerSlot, rules.PlatingPowerDefId);
                    if (plating > 0)
                        GainBlock(ref s, rules, ownerSlot, plating, isCardBlock: false);
                }
                if (rules.MetallicizePowerDefId != 0)
                {
                    int metallicize = GetPowerAmount(in s, ownerSlot, rules.MetallicizePowerDefId);
                    if (metallicize > 0)
                        GainBlock(ref s, rules, ownerSlot, metallicize, isCardBlock: false);
                }
            }
        }

        // Enemy RitualPower (respecting WasJustAppliedByEnemy first-turn skip).
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterTurnEnd) != 0 && rules.RitualPowerDefId != 0)
        {
            for (int i = 0; i < s.RosterCount; i++)
            {
                byte enemySlot = s.ActiveEnemyRoster[i];
                if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                    continue;
                byte ownerSlot = (byte)(3 + enemySlot);
                int ritualIdx = FindPowerIndex(in s, ownerSlot, rules.RitualPowerDefId);
                if (ritualIdx < 0 || s.Powers[ritualIdx].Amount <= 0)
                    continue;

                if ((s.Powers[ritualIdx].Flags & HotPowerFlags.WasJustAppliedByEnemy) != 0)
                {
                    s.Powers[ritualIdx].Flags &= ~HotPowerFlags.WasJustAppliedByEnemy;
                }
                else if (rules.StrengthPowerDefId != 0)
                {
                    ApplyPower(ref s, rules, ownerSlot, ownerSlot, rules.StrengthPowerDefId, s.Powers[ritualIdx].Amount);
                }
            }
        }

        // TickDurations for ALL creatures (Player and Enemies) at end of Enemy Turn.
        // Matches WeakPower/VulnerablePower/FrailPower.AfterSideTurnEnd(CombatSide.Enemy) & CorePowerSupport.TickDurations.
        TickAllDurationPowers(ref s, rules);

        if (CheckWinCondition(ref s, rules))
            return false;

        // Prepare monster moves for next round (advance state machine + MonsterAi RNG).
        int remainingRoster = s.RosterCount;
        for (int i = 0; i < remainingRoster; i++)
        {
            byte enemySlot = s.ActiveEnemyRoster[i];
            PrepareMonsterMoveForNextRound(ref s, ref cold, rules, enemySlot, performedMoveBySlot[enemySlot]);
        }

        return true;
    }

    private static void RunNextPlayerTurnStart(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        s.CurrentSide = HotCombatSide.Player;
        s.RoundNumber++;
        s.PlayerTurnNumber++;

        ResetPlayerTurnCounters(ref s);
        SnapshotTurnStartPowerAmounts(ref s, rules, enemySide: false);

        // Clear Player Block unless Barricade (or equivalent preventer).
        if (s.Player.Block > 0 && ShouldClearBlock(in s, rules, ownerSlot: 0))
            s.Player.Block = 0;

        // Reset Player Energy to base max (3 + energy relics, captured from PlayerCombatState.MaxEnergy).
        int maxEnergy = rules.BaseMaxEnergy > 0
            ? rules.BaseMaxEnergy
            : s.Counters[(int)HotCounterSlot.BaseMaxEnergy];
        s.Energy = Math.Max(0, maxEnergy);

        // Draw opening hand (base 5 + DrawNextTurn, shuffling discard into draw if needed).
        s.PlayerPhase = (byte)PlayerTurnPhase.Start;
        int baseDraw = rules.BaseHandDraw > 0
            ? rules.BaseHandDraw
            : (s.Counters[(int)HotCounterSlot.BaseHandDraw] > 0 ? s.Counters[(int)HotCounterSlot.BaseHandDraw] : 5);
        int drawCount = baseDraw + s.DrawNextTurn;
        s.DrawNextTurn = 0;
        DrawCards(ref s, ref cold, rules, scratch, drawCount, fromHandDraw: true);

        // Player side turn start: PlatingPower decrements by 1 when PlayerTurnNumber != 1.
        if (s.PlayerTurnNumber != 1 && rules.PlatingPowerDefId != 0)
            DecrementPlating(ref s, rules, ownerSlot: 0);

        s.PlayerPhase = (byte)PlayerTurnPhase.None;
        CheckWinCondition(ref s, rules);
    }

    private static void TickAllDurationPowers(ref CombatHotState s, HotRules rules)
    {
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterTurnEnd) == 0)
            return;

        bool hookMaskDirty = false;
        int i = 0;
        while (i < s.PowerCount)
        {
            ref HotPower p = ref s.Powers[i];
            if (p.PowerDefId < rules.Powers.Length && rules.Powers[p.PowerDefId].HasRelevantSkipDurationTick)
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
            }
            else
            {
                i++;
            }
        }

        if (hookMaskDirty)
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
    }

    private static void DecrementPlating(ref CombatHotState s, HotRules rules, byte ownerSlot)
    {
        int idx = FindPowerIndex(in s, ownerSlot, rules.PlatingPowerDefId);
        if (idx < 0 || s.Powers[idx].Amount <= 0)
            return;

        if (s.Powers[idx].Amount > 1)
        {
            s.Powers[idx].Amount--;
        }
        else
        {
            RemovePowerAt(ref s, idx);
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
        }
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

    private static void ResetPlayerTurnCounters(ref CombatHotState s)
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
        }
        s.Fool.MelodyCardsFinished = 0;

        for (int e = 0; e < s.EnemyCount; e++)
            s.Enemies[e].PoweredHitsFromPlayer = 0;
    }
}
