using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.HotKernel;

internal enum HotActionKind : byte
{
    PlayCard = 0,
    UsePotion = 1,
    EndTurn = 2,
}

internal readonly record struct HotAction(
    HotActionKind Kind,
    byte SlotOrHandIdx,
    sbyte TargetEnemySlot)
{
    public static HotAction PlayCard(int handIdx, int targetEnemySlot = -1)
        => new(HotActionKind.PlayCard, (byte)handIdx, (sbyte)targetEnemySlot);

    public static HotAction UsePotion(int slot, int targetEnemySlot = -1)
        => new(HotActionKind.UsePotion, (byte)slot, (sbyte)targetEnemySlot);

    public static HotAction EndTurn()
        => new(HotActionKind.EndTurn, 0, -1);
}

internal readonly record struct HotStepResult(
    bool Executed,
    bool NeedsChoice,
    HotCombatOutcome Outcome);

/// <summary>
/// Zero-allocation contiguous-memory combat simulation kernel targeting STS2 0.113.1.
/// </summary>
internal static partial class HotCombatEngine
{
    public static ulong RecomputeActiveHookMask(in CombatHotState s, HotRules rules)
    {
        ulong mask = rules.StaticRelicHookMask;

        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.Amount != 0 && p.PowerDefId < rules.Powers.Length)
                mask |= (ulong)rules.Powers[p.PowerDefId].HookMask;
        }

        int totalCards = s.TotalCardCount;
        for (int i = 0; i < totalCards; i++)
        {
            ushort cardDefId = s.Cards[i].CardDefId;
            if (cardDefId < rules.Cards.Length)
                mask |= (ulong)rules.Cards[cardDefId].HookMask;
        }

        return mask;
    }

    public static bool CanPlayCard(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        int handIdx)
    {
        if (!IsInProgress(in s) || s.TerminalOutcome != HotCombatOutcome.Ongoing)
            return false;
        if (s.CurrentSide != HotCombatSide.Player)
            return false;
        if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return false;
        if ((uint)handIdx >= (uint)s.HandCount)
            return false;

        ref readonly HotCard card = ref s.Cards[handIdx];
        if ((card.LocalKeywordsMask & HotCardKeywordMask.Unplayable) != 0)
            return false;

        int energyCost = GetEffectiveEnergyCost(in s, in card, cold, rules, isInPlayablePile: true);
        int starCost = GetEffectiveStarCost(in s, in card, cold, rules, isInPlayablePile: true);
        if (energyCost > s.Energy || starCost > s.Stars)
            return false;

        HotCardDef def = rules.Cards[card.CardDefId];
        if (def.Target == TargetType.AnyEnemy && !HasAnyLivingEnemy(in s))
            return false;

        if (!CheckShouldPlayRestrictions(in s, in card, def, rules))
            return false;

        if (!CheckCardIsPlayable(in s, def, rules))
            return false;

        return true;
    }

    public static bool CanPlayCard(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        int handIdx,
        int targetEnemySlot)
    {
        if (!CanPlayCard(in s, cold, rules, handIdx))
            return false;

        HotCardDef def = rules.Cards[s.Cards[handIdx].CardDefId];
        if (def.Target == TargetType.AnyEnemy)
            return IsValidEnemyTarget(in s, targetEnemySlot);

        return true;
    }

    public static bool CanUsePotion(in CombatHotState s, HotRules rules, int slot)
    {
        if (!IsInProgress(in s) || s.TerminalOutcome != HotCombatOutcome.Ongoing)
            return false;
        if (s.CurrentSide != HotCombatSide.Player)
            return false;
        if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return false;
        if ((uint)slot >= (uint)s.PotionSlotCount || (uint)slot >= HotLimits.MaxPotions)
            return false;

        ushort potionDefId = s.Potions[slot];
        if (potionDefId == 0 || potionDefId >= rules.Potions.Length)
            return false;

        HotPotionDef def = rules.Potions[potionDefId];
        if (!def.IsCombatUsable)
            return false;
        if (def.Target == TargetType.AnyEnemy && !HasAnyLivingEnemy(in s))
            return false;

        return true;
    }

    public static bool CanUsePotion(in CombatHotState s, HotRules rules, int slot, int targetEnemySlot)
    {
        if (!CanUsePotion(in s, rules, slot))
            return false;

        HotPotionDef def = rules.Potions[s.Potions[slot]];
        if (def.Target == TargetType.AnyEnemy)
            return IsValidEnemyTarget(in s, targetEnemySlot);

        return true;
    }

    public static int EnumerateLegalActions(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        Span<HotAction> dst,
        bool includePotions = false)
    {
        if (!IsInProgress(in s) || s.TerminalOutcome != HotCombatOutcome.Ongoing || dst.IsEmpty)
            return 0;
        if (s.CurrentSide != HotCombatSide.Player || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        int written = 0;
        int handCount = s.HandCount;
        int rosterCount = s.RosterCount;

        for (int handIdx = 0; handIdx < handCount; handIdx++)
        {
            if (!CanPlayCard(in s, cold, rules, handIdx))
                continue;

            HotCardDef def = rules.Cards[s.Cards[handIdx].CardDefId];
            if (def.Target == TargetType.AnyEnemy)
            {
                for (int r = 0; r < rosterCount; r++)
                {
                    byte enemySlot = s.ActiveEnemyRoster[r];
                    if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                        continue;
                    if (written >= dst.Length)
                        return written;
                    dst[written++] = HotAction.PlayCard(handIdx, enemySlot);
                }
            }
            else
            {
                if (written >= dst.Length)
                    return written;
                dst[written++] = HotAction.PlayCard(handIdx, -1);
            }
        }

        if (includePotions)
        {
            int potionSlots = Math.Min((int)s.PotionSlotCount, HotLimits.MaxPotions);
            for (int slot = 0; slot < potionSlots; slot++)
            {
                if (!CanUsePotion(in s, rules, slot))
                    continue;

                HotPotionDef pDef = rules.Potions[s.Potions[slot]];
                if (pDef.Target == TargetType.AnyEnemy)
                {
                    for (int r = 0; r < rosterCount; r++)
                    {
                        byte enemySlot = s.ActiveEnemyRoster[r];
                        if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                            continue;
                        if (written >= dst.Length)
                            return written;
                        dst[written++] = HotAction.UsePotion(slot, enemySlot);
                    }
                }
                else
                {
                    if (written >= dst.Length)
                        return written;
                    dst[written++] = HotAction.UsePotion(slot, -1);
                }
            }
        }

        if (written < dst.Length)
            dst[written++] = HotAction.EndTurn();

        return written;
    }

    public static HotStepResult Step(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotAction action,
        ReadOnlySpan<HotChoiceSelection> choices = default)
    {
        if (!IsInProgress(in s) || s.TerminalOutcome != HotCombatOutcome.Ongoing)
            return new HotStepResult(Executed: false, NeedsChoice: false, Outcome: s.TerminalOutcome);

        scratch.BeginAction(choices);
        t_requestEndPlayerTurn = false;

        bool executed;
        switch (action.Kind)
        {
            case HotActionKind.PlayCard:
                executed = ExecutePlayCard(ref s, ref cold, rules, scratch, action.SlotOrHandIdx, action.TargetEnemySlot);
                break;

            case HotActionKind.UsePotion:
            {
                if (!CanUsePotion(in s, rules, action.SlotOrHandIdx, action.TargetEnemySlot))
                {
                    executed = false;
                }
                else
                {
                    HotStepResult potionResult = HotPotions.UsePotion(
                        ref s,
                        ref cold,
                        rules,
                        scratch,
                        action.SlotOrHandIdx,
                        action.TargetEnemySlot);
                    executed = potionResult.Executed;
                }
                break;
            }

            case HotActionKind.EndTurn:
                executed = ExecuteEndTurn(ref s, ref cold, rules, scratch);
                break;

            default:
                executed = false;
                break;
        }

        if (executed && !scratch.NeedsChoice)
            CheckWinCondition(ref s, rules);

        return new HotStepResult(executed, scratch.NeedsChoice, s.TerminalOutcome);
    }

    /// <summary>
    /// Computes post-combat victory healing from relics such as Burning Blood, Black Blood, and
    /// Meat on the Bone (STS2 triggers post-combat relic healing after combat teardown, so in-combat
    /// <c>Player.CurrentHp</c> stays exact for lockstep <c>ContinuationStamp</c> parity while search
    /// evaluation accounts for post-combat recovery).
    /// </summary>
    public static int ComputePostCombatRelicHeal(in CombatHotState s, HotRules rules)
    {
        if (s.TerminalOutcome != HotCombatOutcome.Victory || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        int missingHp = Math.Max(0, s.Player.MaxHp - s.Player.CurrentHp);
        if (missingHp == 0)
            return 0;

        int heal = rules.PostCombatRelicUnconditionalHeal;
        if (heal == 0 && rules.HasBurningBlood && rules.BurningBloodRelicDefId != 0)
        {
            short bbHeal = rules.Relics[rules.BurningBloodRelicDefId].PrimaryValue;
            heal += bbHeal > 0 ? bbHeal : 6;
        }

        if (rules.PostCombatRelicWoundedHeal > 0
            && rules.PostCombatRelicWoundedHpPercent > 0
            && s.Player.MaxHp > 0
            && s.Player.CurrentHp * 100 <= s.Player.MaxHp * rules.PostCombatRelicWoundedHpPercent)
        {
            heal += rules.PostCombatRelicWoundedHeal;
        }

        return Math.Min(missingHp, Math.Max(0, heal));
    }

    /// <summary>
    /// Zero-allocation read-only calculation of <c>CardModel.GetEnergyCostWithModifiers</c>
    /// (local cost modifiers + early pass + late pass matching STS2 0.113.1).
    /// </summary>
    internal static int GetEffectiveEnergyCost(
        in CombatHotState s,
        in HotCard card,
        HotColdSidecar? cold,
        HotRules rules,
        bool isInPlayablePile = true)
    {
        if ((card.Flags & HotCardFlags.CostsX) != 0)
            return s.Energy;
        if (card.BaseEnergyCost < 0)
            return card.BaseEnergyCost;

        int cost = Math.Max(0, HotCostModifiers.GetLocalEnergyCost(in card, cold));
        HotCardDef def = rules.Cards[card.CardDefId];

        // 1. Early card-self modifiers
        if (def.CardId == HotCustomCardId.Coda)
        {
            int melodies = CountFinishedMelodies(in s);
            if (melodies > 0)
                cost = Math.Max(0, cost - 2 * melodies);
        }
        else if (def.CardId == HotCustomCardId.ChoLimbs)
        {
            if (s.Fool.MelodyFinished[0] > 0 || s.Fool.MelodyFinished[2] > 0)
                cost = Math.Max(0, cost - 2);
        }

        // 2. Early relic modifiers
        if (def.Type == CardType.Power && rules.HasRelic(HotRelicId.SpikedGauntlets))
            cost += 1;

        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyEnergyCost) == 0)
            return Math.Max(0, cost);

        HotAfflictionId cardAffl = card.AfflId != 0 && card.AfflId < rules.Afflictions.Length
            ? rules.Afflictions[card.AfflId].AfflictionId
            : HotAfflictionId.None;

        // 3. Early power pass (BorrowedTime, Tangled, Curious)
        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot != 0 || p.Amount <= 0 || p.PowerDefId >= rules.Powers.Length)
                continue;

            switch (rules.Powers[p.PowerDefId].PowerId)
            {
                case HotPowerId.BorrowedTimePower:
                    cost += p.Amount;
                    break;

                case HotPowerId.TangledPower:
                    if (cardAffl == HotAfflictionId.Entangled)
                        cost += p.Amount;
                    break;

                case HotPowerId.CuriousPower:
                    if (def.Type == CardType.Power && cost > 0)
                        cost = Math.Max(0, cost - p.Amount);
                    break;
            }
        }

        // 4. Late power & relic pass (Corruption, FreeAttack/Skill/Power/Card, Veilpiercer, VoidForm, BrilliantScarf)
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot != 0 || p.Amount <= 0 || p.PowerDefId >= rules.Powers.Length)
                continue;

            switch (rules.Powers[p.PowerDefId].PowerId)
            {
                case HotPowerId.CorruptionPower:
                    if (def.Type == CardType.Skill)
                        cost = 0;
                    break;

                case HotPowerId.FreeAttackPower:
                    if (isInPlayablePile && def.Type == CardType.Attack)
                        cost = 0;
                    break;

                case HotPowerId.FreeSkillPower:
                    if (isInPlayablePile && def.Type == CardType.Skill)
                        cost = 0;
                    break;

                case HotPowerId.FreePowerPower:
                    if (isInPlayablePile && def.Type == CardType.Power)
                        cost = 0;
                    break;

                case HotPowerId.FreeCardPower:
                    if (isInPlayablePile)
                        cost = 0;
                    break;

                case HotPowerId.VeilpiercerPower:
                    if (isInPlayablePile && (card.LocalKeywordsMask & HotCardKeywordMask.Ethereal) != 0)
                        cost = 0;
                    break;

                case HotPowerId.VoidFormPower:
                    if (isInPlayablePile && p.Aux0 < p.Amount)
                        cost = 0;
                    break;
            }
        }

        if (isInPlayablePile && cost > 0 && IsBrilliantScarfFreePlayReady(in s, rules))
            cost = 0;

        return Math.Max(0, cost);
    }

    /// <summary>
    /// Zero-allocation read-only calculation of <c>CardModel.GetStarCostWithModifiers</c>
    /// matching STS2 0.113.1 (<c>VoidFormPower</c>, <c>BrilliantScarf</c>).
    /// </summary>
    internal static int GetEffectiveStarCost(
        in CombatHotState s,
        in HotCard card,
        HotColdSidecar? cold,
        HotRules rules,
        bool isInPlayablePile = true)
    {
        if ((card.Flags & HotCardFlags.HasStarCostX) != 0)
            return s.Stars;

        int cost = Math.Max(0, HotCostModifiers.GetEffectiveStarCost(in card, cold));
        if (cost <= 0 || !isInPlayablePile)
            return Math.Max(0, cost);

        if ((s.ActiveHookMask & (ulong)(HotHookMask.ModifyStarCost | HotHookMask.ModifyEnergyCost)) != 0)
        {
            int powerCount = s.PowerCount;
            for (int i = 0; i < powerCount; i++)
            {
                ref readonly HotPower p = ref s.Powers[i];
                if (p.OwnerSlot == 0
                    && p.Amount > 0
                    && p.PowerDefId < rules.Powers.Length
                    && rules.Powers[p.PowerDefId].PowerId == HotPowerId.VoidFormPower
                    && p.Aux0 < p.Amount)
                {
                    return 0;
                }
            }

            if (IsBrilliantScarfFreePlayReady(in s, rules))
                return 0;
        }

        return cost;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBrilliantScarfFreePlayReady(in CombatHotState s, HotRules rules)
    {
        if (!rules.HasRelic(HotRelicId.BrilliantScarf))
            return false;
        byte slot = rules.GetRelicStateSlot(HotRelicId.BrilliantScarf);
        if (slot == 0xFF || slot >= HotLimits.MaxRelicStates)
            return false;
        ushort defId = rules.GetRelicDefId(HotRelicId.BrilliantScarf);
        int threshold = defId != 0 && rules.Relics[defId].PrimaryValue > 0
            ? rules.Relics[defId].PrimaryValue
            : 5;
        return s.RelicStates[slot].Current == threshold - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountFinishedMelodies(in CombatHotState s)
    {
        int count = 0;
        if (s.Fool.MelodyFinished[0] > 0) count++;
        if (s.Fool.MelodyFinished[1] > 0) count++;
        if (s.Fool.MelodyFinished[2] > 0) count++;
        return count;
    }

    private static bool CheckShouldPlayRestrictions(
        in CombatHotState s,
        in HotCard card,
        HotCardDef def,
        HotRules rules)
    {
        int playStarts = s.Counters[(int)HotCounterSlot.CardPlayStartsThisTurn];

        // Velvet Choker relic (default 6 cards per turn)
        if (rules.HasRelic(HotRelicId.VelvetChoker))
        {
            ushort vcDefId = rules.GetRelicDefId(HotRelicId.VelvetChoker);
            int limit = vcDefId != 0 && rules.Relics[vcDefId].PrimaryValue > 0
                ? rules.Relics[vcDefId].PrimaryValue
                : 6;
            if (playStarts >= limit)
                return false;
        }

        // In-hand restriction curses (Enthralled, Normality)
        int handCount = s.HandCount;
        for (int i = 0; i < handCount; i++)
        {
            ushort handCardDefId = s.Cards[i].CardDefId;
            if (handCardDefId >= rules.Cards.Length)
                continue;
            HotCustomCardId handCardId = rules.Cards[handCardDefId].CardId;
            if (handCardId == HotCustomCardId.Enthralled && def.CardId != HotCustomCardId.Enthralled)
                return false;
            if (handCardId == HotCustomCardId.Normality && playStarts >= 3)
                return false;
        }

        // Player power restrictions (Smoggy, ChainsOfBinding, Ringing, Sloth, Encore)
        HotAfflictionId cardAffl = card.AfflId != 0 && card.AfflId < rules.Afflictions.Length
            ? rules.Afflictions[card.AfflId].AfflictionId
            : HotAfflictionId.None;

        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot != 0 || p.Amount <= 0 || p.PowerDefId >= rules.Powers.Length)
                continue;

            switch (rules.Powers[p.PowerDefId].PowerId)
            {
                case HotPowerId.SmoggyPower:
                    if (cardAffl == HotAfflictionId.Smog)
                        return false;
                    break;

                case HotPowerId.ChainsOfBindingPower:
                    if (cardAffl == HotAfflictionId.Bound && p.Aux0 != 0)
                        return false;
                    break;

                case HotPowerId.RingingPower:
                    if (cardAffl == HotAfflictionId.Ringing && playStarts > 0)
                        return false;
                    break;

                case HotPowerId.SlothPower:
                case HotPowerId.EncorePower:
                    if (p.Aux0 >= p.Amount)
                        return false;
                    break;
            }
        }

        return true;
    }

    private static bool CheckCardIsPlayable(in CombatHotState s, HotCardDef def, HotRules rules)
    {
        switch (def.CardId)
        {
            case HotCustomCardId.Clash:
            {
                int handCount = s.HandCount;
                for (int i = 0; i < handCount; i++)
                {
                    if (rules.Cards[s.Cards[i].CardDefId].Type != CardType.Attack)
                        return false;
                }
                return true;
            }

            case HotCustomCardId.GrandFinale:
                return s.DrawCount == 0;

            case HotCustomCardId.HighFive:
                return (s.CombatFlags & HotCombatFlags.HasOsty) != 0
                    && (s.Osty.Flags & HotCreatureFlags.Alive) != 0;

            default:
                return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetPayableEnergyCost(in CombatHotState s, in HotCard card, HotColdSidecar? cold)
    {
        if ((card.Flags & HotCardFlags.CostsX) != 0)
            return s.Energy;
        return Math.Max(0, HotCostModifiers.GetLocalEnergyCost(in card, cold));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetPayableStarCost(in CombatHotState s, in HotCard card, HotColdSidecar? cold)
    {
        if ((card.Flags & HotCardFlags.HasStarCostX) != 0)
            return s.Stars;
        return Math.Max(0, HotCostModifiers.GetEffectiveStarCost(in card, cold));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddEnergyModPreservingInline(
        ref HotCard card,
        ref HotColdSidecar? cold,
        short amount,
        bool isAbsolute,
        int expiration,
        bool isReduceOnly)
    {
        if (!isAbsolute && amount == 0)
            return;
        if (isAbsolute && amount == 0 && card.BaseEnergyCost < 0)
            return;

        HotCostModifiers.AddEnergyModifierExact(ref card, ref cold, amount, isAbsolute, expiration, isReduceOnly);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CostAddThisTurn(ref HotCard card, ref HotColdSidecar? cold, int delta, bool reduceOnly = false)
        => AddEnergyModPreservingInline(ref card, ref cold, (short)delta, isAbsolute: false, expiration: (int)LocalCostModifierExpiration.EndOfTurn, isReduceOnly: reduceOnly);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CostAddThisCombat(ref HotCard card, ref HotColdSidecar? cold, int delta, bool reduceOnly = false)
        => AddEnergyModPreservingInline(ref card, ref cold, (short)delta, isAbsolute: false, expiration: (int)LocalCostModifierExpiration.EndOfCombat, isReduceOnly: reduceOnly);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CostAddUntilPlayed(ref HotCard card, ref HotColdSidecar? cold, int delta, bool reduceOnly = false)
        => AddEnergyModPreservingInline(ref card, ref cold, (short)delta, isAbsolute: false, expiration: (int)LocalCostModifierExpiration.WhenPlayed, isReduceOnly: reduceOnly);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CostSetThisTurn(ref HotCard card, ref HotColdSidecar? cold, int cost, bool reduceOnly = false)
        => AddEnergyModPreservingInline(ref card, ref cold, (short)cost, isAbsolute: true, expiration: (int)LocalCostModifierExpiration.EndOfTurn, isReduceOnly: reduceOnly);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CostSetThisCombat(ref HotCard card, ref HotColdSidecar? cold, int cost, bool reduceOnly = false)
        => AddEnergyModPreservingInline(ref card, ref cold, (short)cost, isAbsolute: true, expiration: (int)LocalCostModifierExpiration.EndOfCombat, isReduceOnly: reduceOnly);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CostSetThisTurnOrUntilPlayed(ref HotCard card, ref HotColdSidecar? cold, int cost, bool reduceOnly = false)
        => AddEnergyModPreservingInline(ref card, ref cold, (short)cost, isAbsolute: true, expiration: (int)(LocalCostModifierExpiration.EndOfTurn | LocalCostModifierExpiration.WhenPlayed), isReduceOnly: reduceOnly);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetCardToFreeThisTurn(ref HotCard card, ref HotColdSidecar? cold)
    {
        CostSetThisTurnOrUntilPlayed(ref card, ref cold, 0);
        HotCostModifiers.AddTempStarCost(ref card, ref cold, 0, clearsWhenTurnEnds: true, clearsWhenCardIsPlayed: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsInProgress(in CombatHotState s)
        => (s.CombatFlags & HotCombatFlags.IsInProgress) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsCombatEnding(in CombatHotState s)
        => IsInProgress(in s)
            && ((s.CombatFlags & HotCombatFlags.IsAboutToLose) != 0
                || s.TerminalOutcome != HotCombatOutcome.Ongoing
                || (!HasAnyLivingEnemy(in s) && !HasAnyRevivingEnemy(in s)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsOverOrEnding(in CombatHotState s)
        => !IsInProgress(in s) || IsCombatEnding(in s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasAnyLivingEnemy(in CombatHotState s)
    {
        int rosterCount = s.RosterCount;
        for (int i = 0; i < rosterCount; i++)
        {
            byte enemySlot = s.ActiveEnemyRoster[i];
            if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasAnyRevivingEnemy(in CombatHotState s)
    {
        int enemyCount = s.EnemyCount;
        for (int e = 0; e < enemyCount; e++)
        {
            if (s.Enemies[e].DeathPhase == HotDeathPhase.Reviving)
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsValidEnemyTarget(in CombatHotState s, int enemySlot)
    {
        if ((uint)enemySlot >= (uint)s.EnemyCount)
            return false;
        const HotCreatureFlags required = HotCreatureFlags.Alive | HotCreatureFlags.ActiveInRoster;
        return (s.Enemies[enemySlot].Vital.Flags & required) == required;
    }

    internal static bool CheckWinCondition(ref CombatHotState s, HotRules rules)
    {
        if (!IsInProgress(in s))
            return s.TerminalOutcome != HotCombatOutcome.Ongoing;

        if ((s.CombatFlags & HotCombatFlags.IsAboutToLose) != 0 || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
        {
            s.TerminalOutcome = HotCombatOutcome.Defeat;
            s.TerminalTurn = (byte)Math.Clamp(s.PlayerTurnNumber, 0, byte.MaxValue);
            s.CombatFlags &= ~(HotCombatFlags.IsAboutToLose | HotCombatFlags.IsInProgress);
            return true;
        }

        if (s.TerminalOutcome == HotCombatOutcome.Victory || (!HasAnyLivingEnemy(in s) && !HasAnyRevivingEnemy(in s)))
        {
            s.TerminalOutcome = HotCombatOutcome.Victory;
            s.TerminalTurn = (byte)Math.Clamp(s.PlayerTurnNumber, 0, byte.MaxValue);
            s.CombatFlags &= ~HotCombatFlags.IsInProgress;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsCreatureSlotValid(in CombatHotState s, byte slot)
    {
        if (slot == 0)
            return true;
        if (slot == 1)
            return (s.CombatFlags & HotCombatFlags.HasOsty) != 0;
        if (slot == 2)
            return (s.CombatFlags & HotCombatFlags.HasWuppy) != 0;
        int enemySlot = slot - 3;
        return enemySlot >= 0 && enemySlot < s.EnemyCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref HotCreature GetCreatureRef(ref CombatHotState s, byte slot)
    {
        if (slot == 0)
            return ref s.Player;
        if (slot == 1)
            return ref s.Osty;
        if (slot == 2)
            return ref s.Wuppy;
        return ref s.Enemies[slot - 3].Vital;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasActiveRelic(HotRules rules, string entry)
    {
        ushort defId = rules.FindRelicDefId(entry);
        if (defId == 0)
            return false;
        ushort[] active = rules.ActiveRelicDefIds;
        for (int i = 0; i < active.Length; i++)
        {
            if (active[i] == defId)
                return true;
        }
        return false;
    }
}
