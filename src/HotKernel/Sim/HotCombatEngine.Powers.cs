using System.Numerics;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    private const byte ResultPileDrawTop = 0xFE;
    internal const byte ResultPileHandTop = 0xFD;

    /// <summary>
    /// Bitmask-gated zero-allocation central hook dispatcher covering active Relics,
    /// active Powers (Core / Ironclad / Silent / Defect / Enemy / Regent / Necrobinder / Fool),
    /// and active Dread Dominion.
    /// </summary>
    internal static void DispatchHooks(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotHookMask hook,
        ref HotHookArgs args)
        => DispatchHooksCore(ref s, ref cold, rules, scratch, hook, ref args, skipPlayerTurnStartListeners: false);

    internal static void DispatchAfterSideTurnStartRemaining(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ref HotHookArgs args)
        => DispatchHooksCore(ref s, ref cold, rules, scratch, HotHookMask.AfterSideTurnStart, ref args, skipPlayerTurnStartListeners: true);

    /// <summary>
    /// Exact STS2 0.113.1 <c>Hook.AfterPlayerTurnStart</c> dispatch (runs inside <c>SetupPlayerTurn</c>
    /// after opening hand draw and BEFORE <c>Hook.AfterSideTurnStart</c> such as Plating decrement,
    /// Poison tick, and BiasedCognition Focus loss).
    /// Order matches <c>CombatState.IterateHookListeners</c>: Player Powers first, then Player Relics
    /// in <see cref="HotRules.ActiveRelics"/> order (including <c>EmotionChip</c>).
    /// </summary>
    internal static void DispatchPlayerTurnStartHooks(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        bool takingExtraTurn)
    {
        if (!IsInProgress(in s) || (s.Player.Flags & HotCreatureFlags.Alive) == 0 || scratch.NeedsChoice)
            return;

        HotRules? prevRules = HotPiles.PushActiveRules(rules);
        try
        {
            HotHookArgs args = new()
            {
                Side = HotCombatSide.Player,
                ActorSlot = 0,
                BoolFlag = takingExtraTurn,
            };

            // 0. Early pass: Hook.AfterPlayerTurnStartEarly (STS2 0.114.0 Hook.cs:947-953: HappyFlower, FakeHappyFlower)
            if ((s.ActiveHookMask & (ulong)HotHookMask.AfterPlayerTurnStartEarly) != 0)
            {
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterPlayerTurnStartEarly, ref args);
                if (scratch.NeedsChoice || IsOverOrEnding(in s))
                    return;
            }

            // 1. Player Powers overriding AfterPlayerTurnStart
            if (s.PowerCount > 0 && (s.ActiveHookMask & (ulong)HotHookMask.AfterSideTurnStart) != 0)
            {
                Span<ushort> snapDefIds = stackalloc ushort[HotLimits.MaxPowers];
                Span<byte> snapOcc = stackalloc byte[HotLimits.MaxPowers];
                int snapCount = 0;
                int powerCount = s.PowerCount;
                for (int i = 0; i < powerCount; i++)
                {
                    ref readonly HotPower p = ref s.Powers[i];
                    if (p.OwnerSlot != 0 || p.Amount == 0 || p.PowerDefId >= rules.Powers.Length)
                        continue;

                    HotPowerDef def = rules.Powers[p.PowerDefId];
                    if ((def.HookMask & HotHookMask.AfterSideTurnStart) == 0 || !IsAfterPlayerTurnStartPower(def.PowerId))
                        continue;

                    byte occ = 0;
                    for (int k = 0; k < i; k++)
                    {
                        if (s.Powers[k].OwnerSlot == 0 && s.Powers[k].PowerDefId == p.PowerDefId)
                            occ++;
                    }
                    snapDefIds[snapCount] = p.PowerDefId;
                    snapOcc[snapCount] = occ;
                    snapCount++;
                }

                for (int n = 0; n < snapCount; n++)
                {
                    ushort defId = snapDefIds[n];
                    byte occ = snapOcc[n];
                    int liveIdx = FindPowerOccurrenceIndex(in s, 0, defId, occ);
                    if (liveIdx < 0 || s.Powers[liveIdx].Amount == 0)
                        continue;

                    int beforeOccCount = CountPowerOccurrences(in s, 0, defId);
                    HotPowerDef def = rules.Powers[defId];
                    DispatchSinglePowerHook(ref s, ref cold, rules, scratch, HotHookMask.AfterSideTurnStart, liveIdx, def, ref args);
                    int afterOccCount = CountPowerOccurrences(in s, 0, defId);
                    if (afterOccCount < beforeOccCount)
                    {
                        int removed = beforeOccCount - afterOccCount;
                        for (int m = n + 1; m < snapCount; m++)
                        {
                            if (snapDefIds[m] == defId && snapOcc[m] > occ)
                                snapOcc[m] = (byte)Math.Max(0, snapOcc[m] - removed);
                        }
                    }
                    if (scratch.NeedsChoice || IsOverOrEnding(in s))
                        return;
                }
            }

            // 2. Player Relics overriding AfterPlayerTurnStart (in exact ActiveRelics order)
            HotActiveRelic[] activeRelics = rules.ActiveRelics;
            bool emotionChipTriggered = false;
            for (int r = 0; r < activeRelics.Length; r++)
            {
                HotActiveRelic ar = activeRelics[r];
                if (ar.RelicDefId == 0 || ar.RelicDefId >= rules.Relics.Length)
                    continue;

                HotRelicDef rDef = rules.Relics[ar.RelicDefId];
                HotRelicId relicId = rDef.RelicId != HotRelicId.None
                    ? rDef.RelicId
                    : HotRelicCatalog.FindByEntry(rDef.Entry);

                if (relicId == HotRelicId.EmotionChip)
                {
                    if (!emotionChipTriggered)
                    {
                        emotionChipTriggered = true;
                        HotRelics.TriggerEmotionChipAfterPlayerTurnStart(ref s, ref cold, rules, scratch);
                    }
                }
                else if (IsAfterPlayerTurnStartRelic(relicId))
                {
                    HotRelics.DispatchRelicHook(ref s, ref cold, rules, scratch, HotHookMask.AfterSideTurnStart, (byte)r, ref args);
                }
                else
                {
                    continue;
                }

                if (scratch.NeedsChoice || IsOverOrEnding(in s))
                    return;
            }
        }
        finally
        {
            HotPiles.PopActiveRules(prevRules);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAfterPlayerTurnStartPower(HotPowerId pid)
        => pid is HotPowerId.AmnesiaPower
            or HotPowerId.CrimsonMantlePower
            or HotPowerId.EcstasyPower
            or HotPowerId.EntropyPower
            or HotPowerId.GodByrdPower
            or HotPowerId.HibernatePower
            or HotPowerId.InfernoPower
            or HotPowerId.LoopPower
            or HotPowerId.RollingBoulderPower
            or HotPowerId.SummonNextTurnPower
            or HotPowerId.ToolsOfTheTradePower
            or HotPowerId.TyrannyPower
            or HotPowerId.YouAreMinePower;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAfterPlayerTurnStartRelic(HotRelicId rid)
        => rid is HotRelicId.Bellows
            or HotRelicId.BoneTea
            or HotRelicId.ChoicesParadox
            or HotRelicId.EmotionChip
            or HotRelicId.FestivePopper
            or HotRelicId.GamblingChip
            or HotRelicId.MercuryHourglass
            or HotRelicId.MrStruggles
            or HotRelicId.RoyalPoison
            or HotRelicId.ToastyMittens
            or HotRelicId.VexingPuzzlebox;

    private static void DispatchHooksCore(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotHookMask hook,
        ref HotHookArgs args,
        bool skipPlayerTurnStartListeners)
    {
        ulong bitMask = (ulong)hook;
        if (bitMask == 0)
            return;

        bool hasMask = (s.ActiveHookMask & bitMask) != 0;
        if (!hasMask && rules.Dominion == HotDominionId.None)
            return;

        HotRules? prevRules = HotPiles.PushActiveRules(rules);
        try
        {
        int bit = BitOperations.TrailingZeroCount(bitMask);

        // Exact STS2 0.113.1 CombatState.IterateHookListeners order:
        // 1. Player Powers (ownerSlot == 0) -> 2. Player Relics -> 3. Non-player Powers (ownerSlot != 0) -> 4. Dread Dominion.
        if (hasMask)
        {
            if (IsReadOnlyModifierHook(hook))
            {
                if (s.PowerCount > 0)
                {
                    int powerCount = s.PowerCount;
                    for (int i = 0; i < powerCount; i++)
                    {
                        ref readonly HotPower p = ref s.Powers[i];
                        if (p.OwnerSlot != 0 || p.Amount == 0 || p.PowerDefId >= rules.Powers.Length)
                            continue;

                        HotPowerDef def = rules.Powers[p.PowerDefId];
                        if (((ulong)def.HookMask & bitMask) == 0)
                            continue;

                        DispatchSinglePowerHook(ref s, ref cold, rules, scratch, hook, i, def, ref args);
                        if (scratch.NeedsChoice)
                            return;
                    }
                }

                if ((uint)bit < (uint)rules.RelicHooksByBit.Length)
                {
                    byte[] relicIndices = rules.RelicHooksByBit[bit];
                    for (int r = 0; r < relicIndices.Length; r++)
                    {
                        HotRelics.DispatchRelicHook(ref s, ref cold, rules, scratch, hook, relicIndices[r], ref args);
                        if (scratch.NeedsChoice)
                            return;
                    }
                }

                if (s.PowerCount > 0)
                {
                    int powerCount = s.PowerCount;
                    for (int i = 0; i < powerCount; i++)
                    {
                        ref readonly HotPower p = ref s.Powers[i];
                        if (p.OwnerSlot == 0 || p.Amount == 0 || p.PowerDefId >= rules.Powers.Length)
                            continue;

                        HotPowerDef def = rules.Powers[p.PowerDefId];
                        if (((ulong)def.HookMask & bitMask) == 0)
                            continue;

                        DispatchSinglePowerHook(ref s, ref cold, rules, scratch, hook, i, def, ref args);
                        if (scratch.NeedsChoice)
                            return;
                    }
                }
            }
            else
            {
                // Snapshot all matching (OwnerSlot, PowerDefId, Occurrence, Amount) up front to match
                // CombatState.IterateHookListeners snapshot semantics so mid-hook additions/removals
                // never skip or double-fire listeners.
                Span<byte> snapOwners = stackalloc byte[HotLimits.MaxPowers];
                Span<ushort> snapDefIds = stackalloc ushort[HotLimits.MaxPowers];
                Span<byte> snapOcc = stackalloc byte[HotLimits.MaxPowers];
                Span<int> snapAmounts = stackalloc int[HotLimits.MaxPowers];
                int snapCount = 0;

                if (s.PowerCount > 0)
                {
                    int powerCount = s.PowerCount;
                    for (int i = 0; i < powerCount; i++)
                    {
                        ref readonly HotPower p = ref s.Powers[i];
                        if (p.Amount == 0 || p.PowerDefId >= rules.Powers.Length)
                            continue;

                        if (((ulong)rules.Powers[p.PowerDefId].HookMask & bitMask) == 0)
                            continue;
                        if (skipPlayerTurnStartListeners && p.OwnerSlot == 0
                            && IsAfterPlayerTurnStartPower(rules.Powers[p.PowerDefId].PowerId))
                            continue;

                        byte occ = 0;
                        for (int k = 0; k < i; k++)
                        {
                            if (s.Powers[k].OwnerSlot == p.OwnerSlot && s.Powers[k].PowerDefId == p.PowerDefId)
                                occ++;
                        }
                        snapOwners[snapCount] = p.OwnerSlot;
                        snapDefIds[snapCount] = p.PowerDefId;
                        snapOcc[snapCount] = occ;
                        snapAmounts[snapCount] = p.Amount;
                        snapCount++;
                    }
                }

                // 1. Player Powers (ownerSlot == 0)
                for (int n = 0; n < snapCount; n++)
                {
                    if (snapOwners[n] != 0)
                        continue;

                    byte owner = snapOwners[n];
                    ushort defId = snapDefIds[n];
                    byte occ = snapOcc[n];
                    int liveIdx = FindPowerOccurrenceIndex(in s, owner, defId, occ);
                    if (liveIdx < 0)
                    {
                        if (hook == HotHookMask.AfterTurnEnd)
                        {
                            TryRetireRemovedSnapshotTempStat(ref s, ref cold, rules, scratch, owner, defId, snapAmounts[n], ref args);
                            if (scratch.NeedsChoice)
                                return;
                        }
                        continue;
                    }
                    if (s.Powers[liveIdx].Amount == 0)
                        continue;

                    int beforeOccCount = CountPowerOccurrences(in s, owner, defId);
                    HotPowerDef def = rules.Powers[defId];
                    if (args.CardUid != 0)
                        RefreshHookCardIdx(in s, ref args);
                    DispatchSinglePowerHook(ref s, ref cold, rules, scratch, hook, liveIdx, def, ref args);
                    int afterOccCount = CountPowerOccurrences(in s, owner, defId);
                    if (afterOccCount < beforeOccCount)
                    {
                        int removed = beforeOccCount - afterOccCount;
                        for (int m = n + 1; m < snapCount; m++)
                        {
                            if (snapOwners[m] == owner && snapDefIds[m] == defId && snapOcc[m] > occ)
                                snapOcc[m] = (byte)Math.Max(0, snapOcc[m] - removed);
                        }
                    }
                    if (scratch.NeedsChoice)
                        return;
                }

                // 2. Player Relics
                if ((uint)bit < (uint)rules.RelicHooksByBit.Length)
                {
                    byte[] relicIndices = rules.RelicHooksByBit[bit];
                    for (int r = 0; r < relicIndices.Length; r++)
                    {
                        if (skipPlayerTurnStartListeners)
                        {
                            byte activeIdx = relicIndices[r];
                            if (activeIdx < rules.ActiveRelics.Length)
                            {
                                ushort rDefId = rules.ActiveRelics[activeIdx].RelicDefId;
                                if (rDefId < rules.Relics.Length)
                                {
                                    HotRelicDef rDef = rules.Relics[rDefId];
                                    HotRelicId rId = rDef.RelicId != HotRelicId.None
                                        ? rDef.RelicId
                                        : HotRelicCatalog.FindByEntry(rDef.Entry);
                                    if (IsAfterPlayerTurnStartRelic(rId))
                                        continue;
                                }
                            }
                        }
                        HotRelics.DispatchRelicHook(ref s, ref cold, rules, scratch, hook, relicIndices[r], ref args);
                        if (scratch.NeedsChoice)
                            return;
                    }
                }

                // 3. Non-player Powers (pets / enemies)
                for (int n = 0; n < snapCount; n++)
                {
                    if (snapOwners[n] == 0)
                        continue;

                    byte owner = snapOwners[n];
                    ushort defId = snapDefIds[n];
                    byte occ = snapOcc[n];
                    int liveIdx = FindPowerOccurrenceIndex(in s, owner, defId, occ);
                    if (liveIdx < 0)
                    {
                        if (hook == HotHookMask.AfterTurnEnd)
                        {
                            TryRetireRemovedSnapshotTempStat(ref s, ref cold, rules, scratch, owner, defId, snapAmounts[n], ref args);
                            if (scratch.NeedsChoice)
                                return;
                        }
                        continue;
                    }
                    if (s.Powers[liveIdx].Amount == 0)
                        continue;

                    int beforeOccCount = CountPowerOccurrences(in s, owner, defId);
                    HotPowerDef def = rules.Powers[defId];
                    if (args.CardUid != 0)
                        RefreshHookCardIdx(in s, ref args);
                    DispatchSinglePowerHook(ref s, ref cold, rules, scratch, hook, liveIdx, def, ref args);
                    int afterOccCount = CountPowerOccurrences(in s, owner, defId);
                    if (afterOccCount < beforeOccCount)
                    {
                        int removed = beforeOccCount - afterOccCount;
                        for (int m = n + 1; m < snapCount; m++)
                        {
                            if (snapOwners[m] == owner && snapDefIds[m] == defId && snapOcc[m] > occ)
                                snapOcc[m] = (byte)Math.Max(0, snapOcc[m] - removed);
                        }
                    }
                    if (scratch.NeedsChoice)
                        return;
                }
            }
        }

        // 4. Active Dread Dominion
        if (rules.Dominion != HotDominionId.None)
        {
            if (args.CardUid != 0)
                RefreshHookCardIdx(in s, ref args);
            HotFoolTarot.TryDispatchDominionHook(ref s, ref cold, rules, scratch, hook, ref args);
        }
        }
        finally
        {
            HotPiles.PopActiveRules(prevRules);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsReadOnlyModifierHook(HotHookMask hook)
        => hook is HotHookMask.ModifyDamageAdditive
            or HotHookMask.ModifyDamageMultiplicative
            or HotHookMask.ModifyBlockAdditive
            or HotHookMask.ModifyBlockMultiplicative
            or HotHookMask.ModifyEnergyCost
            or HotHookMask.ModifyStarCost
            or HotHookMask.ModifyHandDraw
            or HotHookMask.ModifyMaxEnergy
            or HotHookMask.ModifyHealAmount
            or HotHookMask.ShouldDraw
            or HotHookMask.ShouldClearBlock;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindPowerOccurrenceIndex(in CombatHotState s, byte ownerSlot, ushort powerDefId, byte occ)
    {
        int seen = 0;
        int count = s.PowerCount;
        for (int i = 0; i < count; i++)
        {
            if (s.Powers[i].OwnerSlot == ownerSlot && s.Powers[i].PowerDefId == powerDefId)
            {
                if (seen == occ)
                    return i;
                seen++;
            }
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountPowerOccurrences(in CombatHotState s, byte ownerSlot, ushort powerDefId)
    {
        int found = 0;
        int count = s.PowerCount;
        for (int i = 0; i < count; i++)
        {
            if (s.Powers[i].OwnerSlot == ownerSlot && s.Powers[i].PowerDefId == powerDefId)
                found++;
        }
        return found;
    }

    private static void DispatchSinglePowerHook(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotHookMask hook,
        int powerIdx,
        HotPowerDef def,
        ref HotHookArgs args)
    {
        if (def.Domain is HotPowerDomain.Regent or HotPowerDomain.Necrobinder)
        {
            if (HotRegentNecro.TryDispatchPowerHook(ref s, ref cold, rules, scratch, hook, powerIdx, ref args))
                return;
        }
        else if (def.Domain == HotPowerDomain.Fool
            || def.PowerId is HotPowerId.FlankingPower
                or HotPowerId.AeonglassDominionPower
                or HotPowerId.InsatiableDominionPower
                or HotPowerId.LagavulinMatriarchDominionPower)
        {
            if (HotFoolTarot.TryDispatchPowerHook(ref s, ref cold, rules, scratch, hook, powerIdx, ref args))
                return;
        }

        DispatchCorePowerHook(ref s, ref cold, rules, scratch, hook, powerIdx, def.PowerId, ref args);
    }

    private static void DispatchCorePowerHook(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotHookMask hook,
        int powerIdx,
        HotPowerId powerId,
        ref HotHookArgs args)
    {
        byte ownerSlot = s.Powers[powerIdx].OwnerSlot;
        int amount = s.Powers[powerIdx].Amount;

        switch (hook)
        {
            case HotHookMask.ModifyDamageAdditive:
                HandleModifyDamageAdditive(in s, rules, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.ModifyDamageMultiplicative:
                HandleModifyDamageMultiplicative(in s, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.ModifyDamageCap:
                HandleModifyDamageCap(ref s, rules, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.ModifyBlockAdditive:
                HandleModifyBlockAdditive(rules, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.ModifyBlockMultiplicative:
                HandleModifyBlockMultiplicative(in s, scratch, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.BeforeDamageReceived:
                if (powerId == HotPowerId.ThornsPower
                    && args.IsPoweredAttack
                    && args.TargetSlot == ownerSlot
                    && args.ActorSlot != 0xFF
                    && amount > 0)
                {
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: ownerSlot, targetSlot: args.ActorSlot, baseDamage: amount, isPoweredAttack: false);
                }
                break;

            case HotHookMask.AfterDamageReceived:
                HandleAfterDamageReceived(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterDamageGiven:
                HandleAfterDamageGiven(ref s, ref cold, rules, scratch, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterBlockGained:
                if (powerId == HotPowerId.JuggernautPower
                    && args.TargetSlot == ownerSlot
                    && args.BlockGained > 0
                    && amount > 0)
                {
                    DamageRandomEnemy(ref s, ref cold, rules, scratch, dealerSlot: ownerSlot, baseDamage: amount, isPoweredAttack: false);
                }
                break;

            case HotHookMask.AfterBlockBroken:
                if (powerId == HotPowerId.BurrowedPower && args.TargetSlot == ownerSlot && ownerSlot >= 3)
                {
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    s.Enemies[ownerSlot - 3].Vital.Block = 0;
                    HotMonsters.ForceStunnedMove(ref s, rules, (byte)(ownerSlot - 3), "BITE_MOVE");
                }
                break;

            case HotHookMask.BeforeHandDraw:
                HandleBeforeHandDrawPower(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount);
                break;

            case HotHookMask.BeforeAttack:
                HandleBeforeAttack(ref s, rules, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterAttack:
                HandleAfterAttack(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.BeforeCardPlayed:
                HandleBeforeCardPlayed(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterCardPlayed:
                HandleAfterCardPlayed(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.ModifyCardPlayCount:
                HandleModifyCardPlayCount(ref s, rules, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.ModifyCardPlayResultLocation:
                HandleModifyCardPlayResultLocation(ref s, rules, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.ModifyEnergyCost:
                HandleModifyEnergyCost(in s, rules, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.ShouldDraw:
                if (powerId == HotPowerId.NoDrawPower && ownerSlot == 0 && !args.FromHandDraw && amount > 0)
                    args.BoolFlag = false;
                break;

            case HotHookMask.ModifyHandDraw:
                HandleModifyHandDraw(powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.ModifyMaxEnergy:
                if (ownerSlot == 0 && amount > 0 && powerId is HotPowerId.PyrePower or HotPowerId.FriendshipPower)
                    args.MaxEnergy += amount;
                else if (ownerSlot == 0 && amount > 0 && powerId == HotPowerId.WasteAwayPower)
                    args.MaxEnergy -= amount;
                break;

            case HotHookMask.ShouldClearBlock:
                if (args.TargetSlot == ownerSlot && amount > 0
                    && powerId is HotPowerId.BarricadePower or HotPowerId.BlurPower or HotPowerId.BurrowedPower)
                {
                    args.BoolFlag = false;
                }
                break;

            case HotHookMask.AfterCardExhausted:
                HandleAfterCardExhausted(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterCardDrawn:
                HandleAfterCardDrawn(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterCardGenerated:
                HandleAfterCardGenerated(ref s, ref cold, rules, scratch, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterCardEnteredCombat:
                HandleAfterCardEnteredCombat(ref s, rules, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterShuffle:
                if (powerId == HotPowerId.StratagemPower && ownerSlot == 0 && amount > 0 && s.DrawCount > 0)
                {
                    int drawCount = s.DrawCount;
                    Span<ushort> pickedUids = stackalloc ushort[drawCount <= amount
                        ? Math.Min(drawCount, HotLimits.MaxCards)
                        : HotLimits.MaxChoicesPerAction];
                    int pickedCount = 0;
                    if (drawCount <= amount)
                    {
                        // 0.113.1 StratagemPower.cs:24 & CardSelectCmd.cs:310-313: !RequireManualConfirmation && drawCount <= amount bypasses selector in Draw pile order.
                        pickedCount = SnapshotPileCardUids(in s, HotPileKind.Draw, pickedUids);
                    }
                    else
                    {
                        int picks = Math.Min(amount, Math.Min(drawCount, HotLimits.MaxChoicesPerAction));
                        scratch.BeginChoiceGroup(HotChoiceGroupFlags.Unordered | HotChoiceGroupFlags.MultiPick);
                        for (int pick = 0; pick < picks && pickedCount < s.DrawCount; pick++)
                        {
                            if (!RequestChoiceExcluding(ref s, scratch, HotChoiceKind.DrawPile, 0, 1, 1, pickedUids[..pickedCount], out HotChoiceSelection sel))
                                return;
                            if (sel.CardUidOrIndex != 0)
                                pickedUids[pickedCount++] = sel.CardUidOrIndex;
                        }
                        scratch.EndChoiceGroup();
                        FlushDeferredPlayerSideTurnStartIfInSetup(ref s, ref cold, rules, scratch);
                        if (IsOverOrEnding(in s) || scratch.NeedsChoice)
                            return;
                    }
                    for (int i = 0; i < pickedCount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                    {
                        int idx = FindCardIndexByUid(in s, HotPileKind.Draw, pickedUids[i]);
                        if (idx >= 0)
                            MoveCardToHandOrDiscard(ref s, idx);
                    }
                }
                break;

            case HotHookMask.AfterPowerAmountChanged:
                HandleAfterPowerAmountChanged(ref s, ref cold, rules, scratch, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterOrbEvoked:
                if (powerId == HotPowerId.ThunderPower
                    && ownerSlot == 0
                    && amount > 0
                    && GetOrbId(rules, args.OrbTypeId) == HotOrbId.Lightning
                    && args.TargetSlot != 0xFF
                    && IsCreatureSlotValid(in s, args.TargetSlot)
                    && (GetCreatureRef(ref s, args.TargetSlot).Flags & HotCreatureFlags.Alive) != 0)
                {
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: args.TargetSlot, baseDamage: amount, isPoweredAttack: false);
                }
                break;

            case HotHookMask.AfterAutoPostPlay:
                HandleAfterAutoPostPlayPower(ref s, ref cold, rules, scratch, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.BeforeTurnEnd:
                HandleBeforeTurnEndPower(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterTurnEnd:
                HandleAfterTurnEndPower(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.BeforeSideTurnStart:
                HandleBeforeSideTurnStartPower(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterSideTurnStart:
                HandleAfterSideTurnStartPower(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterDeath:
                HandleAfterDeathPower(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.ShouldTakeExtraTurn:
                if (powerId == HotPowerId.ExtraTurnPower && ownerSlot == 0 && amount > 0)
                    args.BoolFlag = true;
                break;
        }
    }

    private static void HandleModifyDamageAdditive(
        in CombatHotState s,
        HotRules rules,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (!args.IsPoweredAttack)
            return;

        if (powerId == HotPowerId.TaintedPower)
        {
            if (args.TargetSlot == ownerSlot)
                args.DecimalValue += amount;
            return;
        }

        if (args.ActorSlot != ownerSlot)
            return;

        switch (powerId)
        {
            case HotPowerId.StrengthPower:
                args.DecimalValue += amount;
                break;

            case HotPowerId.VigorPower:
                if (amount > 0)
                    args.DecimalValue += amount;
                break;

            case HotPowerId.AccuracyPower:
                if (args.CardDefId > 0 && args.CardDefId < rules.Cards.Length && rules.Cards[args.CardDefId].IsShivTag)
                    args.DecimalValue += amount;
                break;

            case HotPowerId.PhantomBladesPower:
                if (amount > 0
                    && s.Powers[powerIdx].Aux0 == 0
                    && args.CardDefId > 0
                    && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].IsShivTag)
                {
                    args.DecimalValue += amount;
                }
                break;
        }
    }

    private static void HandleModifyDamageMultiplicative(
        in CombatHotState s,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (!args.IsPoweredAttack)
            return;

        switch (powerId)
        {
            case HotPowerId.WeakPower:
                if (args.ActorSlot == ownerSlot && amount > 0)
                {
                    decimal weakMult = 0.75m;
                    if (args.TargetSlot == 0 && rules.HasPaperKrane)
                        weakMult -= 0.15m;
                    if (GetPowerAmountById(in s, rules, ownerSlot, HotPowerId.DebilitatePower) > 0)
                        weakMult -= 1m - weakMult;
                    args.DecimalMultiplier *= weakMult;
                }
                break;

            case HotPowerId.VulnerablePower:
                if (args.TargetSlot == ownerSlot && amount > 0)
                {
                    decimal vulnMult = 1.5m;
                    if ((args.ActorSlot == 0 || args.ActorSlot == 1) && args.TargetSlot >= 3 && rules.HasPaperPhrog)
                        vulnMult += 0.25m;
                    else if (args.TargetSlot == 0 && rules.HasOddMushroom)
                        vulnMult -= 0.25m;

                    if (args.ActorSlot != 0xFF)
                    {
                        int cruelty = GetPowerAmountById(in s, rules, args.ActorSlot, HotPowerId.CrueltyPower);
                        if (cruelty == 0 && args.ActorSlot == 1)
                            cruelty = GetPowerAmountById(in s, rules, 0, HotPowerId.CrueltyPower);
                        if (cruelty != 0)
                            vulnMult += (decimal)cruelty / 100m;
                    }

                    if (GetPowerAmountById(in s, rules, ownerSlot, HotPowerId.DebilitatePower) > 0)
                        vulnMult += vulnMult - 1m;

                    args.DecimalMultiplier *= vulnMult;
                }
                break;

            case HotPowerId.DoubleDamagePower:
                if ((args.ActorSlot == ownerSlot || (ownerSlot == 0 && args.ActorSlot == 1)) && amount > 0 && args.CardIdx >= 0)
                    args.DecimalMultiplier *= 2m;
                break;

            case HotPowerId.GigantificationPower:
                if ((args.ActorSlot == ownerSlot || (ownerSlot == 0 && args.ActorSlot == 1)) && amount > 0 && args.CardIdx >= 0)
                    args.DecimalMultiplier *= 3m;
                break;

            case HotPowerId.ColossusPower:
                if (args.TargetSlot == ownerSlot && amount > 0 && args.ActorSlot != 0xFF
                    && GetPowerAmountById(in s, rules, args.ActorSlot, HotPowerId.VulnerablePower) > 0)
                {
                    args.DecimalMultiplier *= 0.5m;
                }
                break;

            case HotPowerId.FlutterPower:
                if (args.TargetSlot == ownerSlot && amount > 0)
                    args.DecimalMultiplier *= 0.5m;
                break;

            case HotPowerId.ShrinkPower:
                if (args.ActorSlot == ownerSlot && amount != 0)
                    args.DecimalMultiplier *= 0.7m;
                break;

            case HotPowerId.SlowPower:
                if (args.TargetSlot == ownerSlot)
                {
                    int slowCount = s.Powers[powerIdx].Aux0;
                    if (slowCount > 0)
                        args.DecimalMultiplier *= 1m + 0.1m * slowCount;
                }
                break;

            case HotPowerId.TrackingPower:
                if ((args.ActorSlot == ownerSlot || (ownerSlot == 0 && args.ActorSlot == 1))
                    && args.CardIdx >= 0
                    && amount > 0
                    && GetPowerAmountById(in s, rules, args.TargetSlot, HotPowerId.WeakPower) > 0)
                {
                    args.DecimalMultiplier *= 1m + (decimal)amount / 100m;
                }
                break;

            case HotPowerId.LethalityPower:
                if (args.ActorSlot == ownerSlot
                    && args.CardIdx >= 0
                    && amount > 0
                    && scratch.CurrentPlayIndex == 0
                    && s.Counters[(int)HotCounterSlot.AttackPlayStartsThisTurn] <= 1)
                {
                    args.DecimalMultiplier *= 1m + (decimal)amount / 100m;
                }
                break;

            case HotPowerId.ConductorsBatonPower:
                if (args.TargetSlot == ownerSlot && amount > 0)
                    args.DecimalMultiplier *= 0.75m;
                break;

            case HotPowerId.SoarPower:
                if (args.TargetSlot == ownerSlot && amount > 0)
                    args.DecimalMultiplier *= 0.5m;
                break;

            case HotPowerId.SurroundedPower:
                if (args.TargetSlot == ownerSlot && args.ActorSlot != 0xFF)
                {
                    // SurroundedPower.Direction: Right = 0, Left = 1 (SurroundedPower.cs:19-23,56-71).
                    int facing = s.Powers[powerIdx].Aux0;
                    if (((facing == 0 || facing == 2) && GetPowerAmountById(in s, rules, args.ActorSlot, HotPowerId.BackAttackLeftPower) > 0)
                        || (facing == 1 && GetPowerAmountById(in s, rules, args.ActorSlot, HotPowerId.BackAttackRightPower) > 0))
                    {
                        args.DecimalMultiplier *= 1.5m;
                    }
                }
                break;

            case HotPowerId.HangPower:
                if (args.TargetSlot == ownerSlot
                    && amount > 0
                    && args.CardDefId > 0
                    && args.CardDefId < rules.Cards.Length
                    && rules.Cards[args.CardDefId].CardId == HotCustomCardId.Hang)
                {
                    args.DecimalMultiplier *= amount;
                }
                break;
        }
    }

    private static void HandleModifyDamageCap(
        ref CombatHotState s,
        HotRules rules,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (args.TargetSlot != ownerSlot || amount <= 0 || args.DecimalValue <= 0m)
            return;

        switch (powerId)
        {
            case HotPowerId.IntangiblePower:
                // 0.113.1 IntangiblePower.cs:19-34,47-54: ModifyDamageCap (pre-block, BoolFlag=true)
                // and ModifyHpLostAfterOsty (Pass 1, !BoolFlag && Amount==1); never runs in Pass 2 (AfterOstyLate).
                if ((args.BoolFlag || args.Amount == 1) && args.DecimalValue > 1m)
                    args.DecimalValue = 1m;
                break;

            case HotPowerId.SlipperyPower:
                // 0.113.1 SlipperyPower.cs:19-30: ModifyHpLostAfterOsty (Pass 1).
                if (!args.BoolFlag && args.Amount == 1 && args.DecimalValue > 1m)
                    args.DecimalValue = 1m;
                break;

            case HotPowerId.HardToKillPower:
                // 0.114.0 HardToKillPower.cs:15-26: ModifyHpLostBeforeOstyLate (!BoolFlag && Amount == 0).
                if (!args.BoolFlag && args.Amount == 0 && args.DecimalValue > amount)
                    args.DecimalValue = amount;
                break;

            case HotPowerId.HardenedShellPower:
                // 0.114.0 HardenedShellPower.cs:32-43: ModifyHpLostBeforeOstyLate (!BoolFlag && Amount == 0).
                if (!args.BoolFlag && args.Amount == 0)
                {
                    int remainingCap = Math.Max(0, amount - s.Powers[powerIdx].Aux0);
                    if (args.DecimalValue > remainingCap)
                        args.DecimalValue = remainingCap;
                }
                break;

            case HotPowerId.BufferPower:
                // 0.113.1 BufferPower.cs:21-37 & Hook.cs:1816-1824: ModifyHpLostAfterOstyLate (Pass 2, Amount==2);
                // BrightestFlame bypasses Buffer, and AfterModifyingHpLostAfterOsty only decrements
                // when decimal.Truncate(before) != decimal.Truncate(0m), i.e. decimal.Truncate(DecimalValue) > 0m.
                if (!args.BoolFlag && args.Amount == 2 && decimal.Truncate(args.DecimalValue) > 0m)
                {
                    ushort brightestFlameId = rules.GetCardDefId(HotCustomCardId.BrightestFlame);
                    if (brightestFlameId == 0 || args.CardDefId != brightestFlameId)
                    {
                        args.DecimalValue = 0m;
                        DecrementPowerAt(ref s, rules, powerIdx);
                    }
                }
                break;
        }
    }

    private static void HandleModifyBlockAdditive(
        HotRules rules,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (!args.IsCardBlock || args.TargetSlot != ownerSlot)
            return;

        switch (powerId)
        {
            case HotPowerId.DexterityPower:
                args.DecimalValue += amount;
                break;

            case HotPowerId.FastenPower:
                if (args.CardDefId > 0 && args.CardDefId < rules.Cards.Length && rules.Cards[args.CardDefId].IsDefendTag)
                    args.DecimalValue += amount;
                break;
        }
    }

    private static void HandleModifyBlockMultiplicative(
        in CombatHotState s,
        HotExecScratch scratch,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (args.TargetSlot != ownerSlot || amount <= 0)
            return;

        switch (powerId)
        {
            case HotPowerId.FrailPower:
                if (args.IsCardBlock && !args.BoolFlag)
                    args.DecimalMultiplier *= 0.75m;
                break;

            case HotPowerId.NoBlockPower:
                if (args.IsCardBlock && !args.BoolFlag)
                    args.DecimalMultiplier = 0m;
                break;

            case HotPowerId.ShadowmeldPower:
                args.DecimalMultiplier *= amount >= 96
                    ? decimal.MaxValue
                    : (decimal)Math.Pow(2.0, amount);
                break;

            case HotPowerId.UnmovablePower:
                if (args.IsCardBlock && ownerSlot == 0
                    && (s.Counters[(int)HotCounterSlot.CardBlockGainsThisTurn] - scratch.CardBlockGainsInCurrentPlay) < amount)
                {
                    args.DecimalMultiplier *= 2m;
                }
                break;
        }
    }

    private static void HandleAfterDamageReceived(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (args.TargetSlot != ownerSlot)
            return;

        switch (powerId)
        {
            case HotPowerId.FlameBarrierPower:
                if (args.IsPoweredAttack && args.ActorSlot != 0xFF && amount > 0)
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: ownerSlot, targetSlot: args.ActorSlot, baseDamage: amount, isPoweredAttack: false);
                break;

            case HotPowerId.ReflectPower:
                if (args.IsPoweredAttack && args.BlockedDamage > 0 && args.ActorSlot != 0xFF)
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: ownerSlot, targetSlot: args.ActorSlot, baseDamage: args.BlockedDamage, isPoweredAttack: false);
                break;

            case HotPowerId.InfernoPower:
                if (args.UnblockedDamage > 0 && amount > 0 && IsSlotOnCurrentSide(s.CurrentSide, ownerSlot))
                    DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: ownerSlot, baseDamage: amount, isPoweredAttack: false);
                break;

            case HotPowerId.RupturePower:
                if (args.UnblockedDamage > 0 && amount > 0 && IsSlotOnCurrentSide(s.CurrentSide, ownerSlot))
                {
                    // 0.113.1 RupturePower.cs:45-58: defer Strength to AfterCardPlayed when damage came from an actively playing card.
                    if (!TryDeferRuptureStrength(in s, ref s.Powers[powerIdx], in args, amount))
                        ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.StrengthPower, amount);
                }
                break;

            case HotPowerId.CurlUpPower:
                // STS2 0.113.1 CurlUpPower.cs:34-54: latch the first attacking cardSource (CardUid) so nested plays do not pop it early.
                if (args.IsPoweredAttack && s.Powers[powerIdx].AuxCardUid == 0)
                {
                    ushort sourceUid = args.CardUid != 0
                        ? args.CardUid
                        : ((uint)args.CardIdx < (uint)s.TotalCardCount ? s.Cards[args.CardIdx].CardUid : (ushort)0);
                    if (sourceUid != 0)
                        s.Powers[powerIdx].AuxCardUid = sourceUid;
                }
                break;

            case HotPowerId.FlutterPower:
                if (args.IsPoweredAttack && args.UnblockedDamage > 0 && amount > 0)
                {
                    if (amount > 1)
                    {
                        s.Powers[powerIdx].Amount--;
                    }
                    else
                    {
                        RemovePowerAt(ref s, powerIdx);
                        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                        if (ownerSlot >= 3)
                            HotMonsters.ForceStunnedWithNextMove(ref s, rules, (byte)(ownerSlot - 3));
                    }
                }
                break;

            case HotPowerId.SlipperyPower:
                if (args.UnblockedDamage >= 1 && amount > 0)
                    DecrementPowerAt(ref s, rules, powerIdx);
                break;

            case HotPowerId.HardenedShellPower:
                if (!args.WasFullyBlocked && args.UnblockedDamage > 0)
                    s.Powers[powerIdx].Aux0 += args.UnblockedDamage;
                break;

            case HotPowerId.PersonalHivePower:
                if (args.IsPoweredAttack && (args.ActorSlot == 0 || args.ActorSlot == 1) && amount > 0)
                {
                    ushort dazedId = rules.GetCardDefId(HotCustomCardId.Dazed);
                    if (dazedId != 0)
                    {
                        for (int k = 0; k < amount; k++)
                            AddGeneratedFixedCard(ref s, ref cold, rules, scratch, dazedId, HotPileKind.Draw, dstOffset: -2, upgraded: false, createdByPlayer: false);
                    }
                }
                break;

            case HotPowerId.AsleepPower:
                if (args.UnblockedDamage > 0 && ownerSlot >= 3)
                {
                    ushort platingId = rules.PlatingPowerDefId != 0 ? rules.PlatingPowerDefId : rules.GetPowerDefId(HotPowerId.PlatingPower);
                    if (platingId != 0)
                        SetPowerAmount(ref s, rules, ownerSlot, platingId, 0);
                    s.Enemies[ownerSlot - 3].Flags |= HotEnemyFlags.Awake;
                    HotMonsters.ForceStunnedMove(ref s, rules, (byte)(ownerSlot - 3), "SLASH_MOVE");
                    int liveIdx = FindPowerIndex(in s, ownerSlot, rules.GetPowerDefId(HotPowerId.AsleepPower));
                    if (liveIdx >= 0)
                    {
                        RemovePowerAt(ref s, liveIdx);
                        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    }
                }
                break;

            case HotPowerId.SlumberPower:
                if (args.UnblockedDamage > 0 && ownerSlot >= 3)
                {
                    if (s.Powers[powerIdx].Amount > 1)
                    {
                        s.Powers[powerIdx].Amount--;
                    }
                    else
                    {
                        // 0.113.1 SlumberPower.cs:25-30: stun into WakeUpMove -> ROLL_OUT_MOVE; IsAwake becomes true when WakeUpMove executes.
                        RemovePowerAt(ref s, powerIdx);
                        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                        HotMonsters.ForceStunnedMove(ref s, rules, (byte)(ownerSlot - 3), "ROLL_OUT_MOVE");
                    }
                }
                break;

            case HotPowerId.BurrowedPower:
                if (args.WasBlockBroken && ownerSlot >= 3)
                {
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    s.Enemies[ownerSlot - 3].Vital.Block = 0;
                    HotMonsters.ForceStunnedMove(ref s, rules, (byte)(ownerSlot - 3), "BITE_MOVE");
                }
                break;

            case HotPowerId.TheGambitPower:
                if (args.IsPoweredAttack && args.UnblockedDamage > 0)
                {
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    KillCreature(ref s, ref cold, rules, scratch, ownerSlot, force: false);
                }
                break;

            case HotPowerId.ShriekPower:
                if (ownerSlot >= 3
                    && args.UnblockedDamage > 0
                    && GetCreatureRef(ref s, ownerSlot).CurrentHp <= amount)
                {
                    HotMonsters.ForceStunnedMove(ref s, rules, (byte)(ownerSlot - 3), "TERROR_MOVE");
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                }
                break;

            case HotPowerId.PlowPower:
                if (ownerSlot >= 3
                    && args.UnblockedDamage > 0
                    && GetCreatureRef(ref s, ownerSlot).CurrentHp <= amount)
                {
                    // 0.113.1 PlowPower.cs:37-51: remove all TemporaryStrengthPower instances & StrengthPower,
                    // stun owner (CeremonialBeast -> BEAST_CRY_MOVE), and remove PlowPower.
                    RemoveAllTemporaryStrengthPowers(ref s, rules, ownerSlot);
                    ushort strDefId = rules.StrengthPowerDefId != 0
                        ? rules.StrengthPowerDefId
                        : rules.GetPowerDefId(HotPowerId.StrengthPower);
                    if (strDefId != 0)
                        SetPowerAmount(ref s, rules, ownerSlot, strDefId, 0);
                    byte enemyIdx = (byte)(ownerSlot - 3);
                    bool isCeremonialBeast = s.Enemies[enemyIdx].MonsterDefId < rules.Monsters.Length
                        && rules.Monsters[s.Enemies[enemyIdx].MonsterDefId].MonsterId == HotMonsterId.CeremonialBeast;
                    if (isCeremonialBeast && rules.RulesSnapshot.IsDread)
                    {
                        // 0.113.1 CeremonialBeast.SetStunned(): applies RitualPower(2) in Dread mode.
                        ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.RitualPower, 2);
                    }
                    HotMonsters.ForceStunnedMove(ref s, rules, enemyIdx, isCeremonialBeast ? "BEAST_CRY_MOVE" : null);
                    ushort plowDefId = rules.GetPowerDefId(HotPowerId.PlowPower);
                    if (plowDefId != 0)
                        SetPowerAmount(ref s, rules, ownerSlot, plowDefId, 0);
                }
                break;
        }
    }

    private static void RemoveAllTemporaryStrengthPowers(ref CombatHotState s, HotRules rules, byte ownerSlot)
    {
        bool removed = false;
        for (int i = s.PowerCount - 1; i >= 0; i--)
        {
            if (s.Powers[i].OwnerSlot != ownerSlot || s.Powers[i].PowerDefId >= rules.Powers.Length)
                continue;

            HotPowerId pid = rules.Powers[s.Powers[i].PowerDefId].PowerId;
            if (pid is HotPowerId.CoordinatePower
                or HotPowerId.CrushUnderPower
                or HotPowerId.DarkShacklesPower
                or HotPowerId.DyingStarPower
                or HotPowerId.EnfeeblingTouchPower
                or HotPowerId.FeedingFrenzyPower
                or HotPowerId.FlexPotionPower
                or HotPowerId.ManglePower
                or HotPowerId.MonarchsGazeStrengthDownPower
                or HotPowerId.PiercingWailPower
                or HotPowerId.ReptileTrinketPower
                or HotPowerId.SetupStrikePower
                or HotPowerId.ShacklingPotionPower)
            {
                RemovePowerAt(ref s, i);
                removed = true;
            }
        }
        if (removed)
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
    }

    private static void HandleAfterDamageGiven(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (!args.IsPoweredAttack || args.ActorSlot != ownerSlot || amount <= 0)
            return;

        switch (powerId)
        {
            case HotPowerId.EnvenomPower:
            case HotPowerId.ConcoctPower:
                if (args.UnblockedDamage > 0)
                    ApplyPowerById(ref s, ref cold, rules, scratch, args.TargetSlot, ownerSlot, HotPowerId.PoisonPower, amount);
                break;

            case HotPowerId.PaperCutsPower:
                if (args.TargetSlot == 0 && args.UnblockedDamage > 0)
                    LoseMaxHp(ref s, ref cold, rules, scratch, targetSlot: 0, amount: amount);
                break;
        }
    }

    private static void HandleBeforeAttack(
        ref CombatHotState s,
        HotRules rules,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (!args.IsPoweredAttack || amount <= 0)
            return;

        if (powerId == HotPowerId.VigorPower)
        {
            if (args.ActorSlot == ownerSlot && s.Powers[powerIdx].Aux0 == 0)
                s.Powers[powerIdx].Aux0 = amount;
        }
        else if (powerId == HotPowerId.GigantificationPower
            && (args.ActorSlot == ownerSlot || (ownerSlot == 0 && args.ActorSlot == 1))
            && args.CardIdx >= 0
            && args.CardDefId > 0
            && args.CardDefId < rules.Cards.Length
            && rules.Cards[args.CardDefId].Type == CardType.Attack
            && s.Powers[powerIdx].Aux0 == 0)
        {
            s.Powers[powerIdx].Aux0 = 1;
        }
    }

    private static void HandleAfterAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (powerId == HotPowerId.SkittishPower)
        {
            if (ownerSlot >= 3
                && amount > 0
                && (s.Powers[powerIdx].Flags & HotPowerFlags.BoolAux) == 0
                && args.IsPoweredAttack
                && args.CardDefId != 0
                && (scratch.AttackUnblockedEnemyMask & (1u << (ownerSlot - 3))) != 0)
            {
                s.Powers[powerIdx].Flags |= HotPowerFlags.BoolAux;
                GainBlock(ref s, ref cold, rules, scratch, ownerSlot, amount, isCardBlock: false);
            }
            return;
        }

        if (powerId == HotPowerId.VigorPower)
        {
            if (args.ActorSlot == ownerSlot && s.Powers[powerIdx].Aux0 > 0)
            {
                int consumed = s.Powers[powerIdx].Aux0;
                s.Powers[powerIdx].Aux0 = 0;
                int remaining = Math.Max(0, s.Powers[powerIdx].Amount - consumed);
                if (remaining == 0)
                {
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                }
                else
                {
                    s.Powers[powerIdx].Amount = remaining;
                }
            }
        }
        else if (powerId == HotPowerId.GigantificationPower)
        {
            if ((args.ActorSlot == ownerSlot || (ownerSlot == 0 && args.ActorSlot == 1)) && s.Powers[powerIdx].Aux0 > 0)
            {
                s.Powers[powerIdx].Aux0 = 0;
                DecrementPowerAt(ref s, rules, powerIdx);
            }
        }
    }

    private static void HandleBeforeCardPlayed(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (args.CardDefId == 0 || args.CardDefId >= rules.Cards.Length)
            return;

        HotCardDef cardDef = rules.Cards[args.CardDefId];
        switch (powerId)
        {
            case HotPowerId.FreeAttackPower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Attack && amount > 0)
                    DecrementPowerAt(ref s, rules, powerIdx);
                break;

            case HotPowerId.FreeSkillPower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Skill && amount > 0)
                    DecrementPowerAt(ref s, rules, powerIdx);
                break;

            case HotPowerId.FreePowerPower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Power && amount > 0)
                    DecrementPowerAt(ref s, rules, powerIdx);
                break;

            case HotPowerId.VeilpiercerPower:
                if (ownerSlot == 0 && amount > 0 && args.CardIdx >= 0
                    && IsCardEthereal(in s, in s.Cards[args.CardIdx], rules))
                {
                    DecrementPowerAt(ref s, rules, powerIdx);
                }
                break;

            case HotPowerId.DanseMacabrePower:
                if (ownerSlot == 0 && args.EnergySpent >= 2 && amount > 0)
                    GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: amount, isCardBlock: false);
                break;

            case HotPowerId.SpiritOfAshPower:
                if (ownerSlot == 0 && amount > 0 && args.CardIdx >= 0
                    && IsCardEthereal(in s, in s.Cards[args.CardIdx], rules))
                {
                    GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: amount, isCardBlock: false);
                }
                break;

            case HotPowerId.SlothPower:
                if (ownerSlot == 0)
                    s.Powers[powerIdx].Aux0++;
                break;

            case HotPowerId.JugglingPower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Attack && args.CardIdx >= 0)
                {
                    s.Powers[powerIdx].Aux0++;
                    if (s.Powers[powerIdx].Aux0 == 3)
                        s.Powers[powerIdx].AuxCardUid = s.Cards[args.CardIdx].CardUid;
                }
                break;

            case HotPowerId.ChainsOfBindingPower:
                if (ownerSlot == 0 && args.CardIdx >= 0
                    && (s.Cards[args.CardIdx].Flags & HotCardFlags.IsDupe) == 0
                    && s.Cards[args.CardIdx].AfflId != 0
                    && rules.Afflictions[s.Cards[args.CardIdx].AfflId].AfflictionId == HotAfflictionId.Bound)
                {
                    s.Powers[powerIdx].Aux0 = 1;
                }
                break;

            case HotPowerId.AfterimagePower:
            case HotPowerId.GravityPower:
            case HotPowerId.SerpentFormPower:
                if (ownerSlot == 0 && amount > 0)
                    LatchCardPlayAmount(ref s.Powers[powerIdx], amount);
                break;

            case HotPowerId.StormPower:
            case HotPowerId.SubroutinePower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Power && amount > 0)
                    LatchCardPlayAmount(ref s.Powers[powerIdx], amount);
                break;

            case HotPowerId.CalamityPower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Attack && amount > 0)
                    LatchCardPlayAmount(ref s.Powers[powerIdx], amount);
                break;

            case HotPowerId.StranglePower:
                if (args.ActorSlot == 0 && ownerSlot >= 3 && amount > 0)
                    LatchCardPlayAmount(ref s.Powers[powerIdx], amount);
                break;

            case HotPowerId.MonologuePower:
                if (ownerSlot == 0 && s.Powers[powerIdx].AuxCardUid < ushort.MaxValue)
                    s.Powers[powerIdx].AuxCardUid++;
                break;

            case HotPowerId.SurroundedPower:
                if (ownerSlot == 0 && args.TargetSlot != 0xFF)
                    UpdateSurroundedFacingOnPower(ref s, rules, powerIdx, args.TargetSlot);
                break;

            case HotPowerId.RupturePower:
                if (ownerSlot == 0 && IsSlotOnCurrentSide(s.CurrentSide, ownerSlot))
                {
                    // 0.113.1 RupturePower.cs:31-43: track active player card plays so self-HP-loss defers Strength to AfterCardPlayed.
                    ushort uid = args.CardUid != 0
                        ? args.CardUid
                        : ((uint)args.CardIdx < (uint)s.TotalCardCount ? s.Cards[args.CardIdx].CardUid : (ushort)0);
                    if (uid != 0)
                        RegisterRupturePlayedCard(ref s.Powers[powerIdx], uid);
                }
                break;
        }
    }

    /// <summary>
    /// Updates player <c>SurroundedPower</c> facing toward <paramref name="targetCreatureSlot"/>
    /// (0.113.1 <c>SurroundedPower.BeforeCardPlayed</c> &amp; <c>BeforePotionUsed</c>, lines 75-88, 102-119).
    /// </summary>
    internal static void UpdateSurroundedFacingForTarget(
        ref CombatHotState s,
        HotRules rules,
        byte targetCreatureSlot)
    {
        if (targetCreatureSlot == 0xFF)
            return;
        ushort surroundedDefId = rules.GetPowerDefId(HotPowerId.SurroundedPower);
        if (surroundedDefId == 0)
            return;
        int powerIdx = FindPowerIndex(in s, ownerSlot: 0, surroundedDefId);
        if (powerIdx >= 0 && s.Powers[powerIdx].Amount > 0)
            UpdateSurroundedFacingOnPower(ref s, rules, powerIdx, targetCreatureSlot);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateSurroundedFacingOnPower(
        ref CombatHotState s,
        HotRules rules,
        int powerIdx,
        byte targetCreatureSlot)
    {
        // SurroundedPower.Direction: Right = 0, Left = 1 (SurroundedPower.cs:102-119).
        if (GetPowerAmountById(in s, rules, targetCreatureSlot, HotPowerId.BackAttackLeftPower) > 0)
            s.Powers[powerIdx].Aux0 = 1;
        else if (GetPowerAmountById(in s, rules, targetCreatureSlot, HotPowerId.BackAttackRightPower) > 0)
            s.Powers[powerIdx].Aux0 = 0;
    }

    private static void HandleAfterCardPlayed(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (args.CardDefId == 0 || args.CardDefId >= rules.Cards.Length)
            return;

        HotCardDef cardDef = rules.Cards[args.CardDefId];
        switch (powerId)
        {
            case HotPowerId.AfterimagePower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx]);
                if (ownerSlot == 0 && snapAmt > 0)
                    GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: snapAmt, isCardBlock: false);
                break;
            }

            case HotPowerId.RagePower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Attack && amount > 0)
                    GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: amount, isCardBlock: false);
                break;

            case HotPowerId.StormPower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx]);
                if (ownerSlot == 0 && cardDef.Type == CardType.Power && snapAmt > 0)
                {
                    for (int k = 0; k < snapAmt; k++)
                        ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Lightning);
                }
                break;
            }

            case HotPowerId.SubroutinePower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx]);
                if (ownerSlot == 0 && cardDef.Type == CardType.Power && snapAmt > 0)
                    GainEnergy(ref s, rules, snapAmt);
                break;
            }

            case HotPowerId.CalamityPower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx]);
                if (ownerSlot == 0 && cardDef.Type == CardType.Attack && snapAmt > 0)
                {
                    ushort[] atkPool = rules.CombatCharacterAttackPool;
                    if (atkPool.Length > 0)
                    {
                        for (int k = 0; k < snapAmt && !scratch.NeedsChoice && !IsOverOrEnding(in s); k++)
                        {
                            ushort picked = atkPool[HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardGeneration], atkPool.Length)];
                            AddGeneratedFixedCard(ref s, ref cold, rules, scratch, picked, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                        }
                    }
                }
                break;
            }

            case HotPowerId.EnragePower:
                if (cardDef.Type == CardType.Skill && amount > 0)
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.StrengthPower, amount);
                break;

            case HotPowerId.EnsemblePower:
                // STS2 0.114.0 EnsemblePower.cs:26-32: multiplayer ally trigger only, and only on IsLastInSeries.
                if (args.ActorSlot != ownerSlot && args.PlayIndex == args.PlayCount - 1 && amount > 0)
                    DecrementPowerAt(ref s, rules, powerIdx);
                break;

            case HotPowerId.CurlUpPower:
            {
                // STS2 0.113.1 CurlUpPower.cs:56-70: only trigger when cardPlay.Card matches the latched playedCard.
                ushort playedUid = args.CardUid != 0
                    ? args.CardUid
                    : ((uint)args.CardIdx < (uint)s.TotalCardCount ? s.Cards[args.CardIdx].CardUid : (ushort)0);
                if (playedUid != 0 && s.Powers[powerIdx].AuxCardUid == playedUid)
                {
                    s.Powers[powerIdx].AuxCardUid = 0;
                    if (amount > 0)
                        GainBlock(ref s, ref cold, rules, scratch, targetSlot: ownerSlot, baseAmount: amount, isCardBlock: false);
                    int liveIdx = FindPowerIndex(in s, ownerSlot, rules.GetPowerDefId(HotPowerId.CurlUpPower));
                    if (liveIdx >= 0)
                    {
                        RemovePowerAt(ref s, liveIdx);
                        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    }
                }
                break;
            }

            case HotPowerId.SlowPower:
                s.Powers[powerIdx].Aux0++;
                break;

            case HotPowerId.PanachePower:
                if (ownerSlot == 0 && amount > 0)
                {
                    // PanachePower.cs:50-63: ignore the Panache card's own AfterCardPlayed.
                    if ((s.Powers[powerIdx].Flags & HotPowerFlags.BoolAux) != 0)
                    {
                        s.Powers[powerIdx].Flags &= ~HotPowerFlags.BoolAux;
                        break;
                    }
                    int left = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : 5;
                    left--;
                    if (left <= 0)
                    {
                        s.Powers[powerIdx].Aux0 = 5;
                        DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: amount, isPoweredAttack: false);
                    }
                    else
                    {
                        s.Powers[powerIdx].Aux0 = left;
                    }
                }
                break;

            case HotPowerId.MasterPlannerPower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Skill && args.CardIdx >= 0 && args.CardIdx < s.TotalCardCount)
                {
                    s.Cards[args.CardIdx].LocalKeywordsMask |= HotCardKeywordMask.Sly;
                    s.Cards[args.CardIdx].KeywordsAddedOutsideMask |= HotCardKeywordMask.Sly;
                    s.Cards[args.CardIdx].Flags |= HotCardFlags.IsSlyThisTurn;
                }
                break;

            case HotPowerId.JugglingPower:
                if (ownerSlot == 0 && args.CardIdx >= 0 && args.CardIdx < s.TotalCardCount
                    && s.Powers[powerIdx].AuxCardUid == s.Cards[args.CardIdx].CardUid)
                {
                    s.Powers[powerIdx].AuxCardUid = 0;
                    HotCard srcCard = s.Cards[args.CardIdx];
                    for (int k = 0; k < amount; k++)
                    {
                        HotCard clone = srcCard;
                        clone.CardUid = s.NextCardUid++;
                        clone.Flags &= ~(HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay | HotCardFlags.IsDupe | HotCardFlags.FetchPlayedThisTurn);
                        clone.Flags |= HotCardFlags.IsClone;
                        ClearPerInstanceHistoryOnClone(ref clone, rules);
                        if ((clone.Flags & HotCardFlags.HasCostOverflow) != 0 && srcCard.CardUid != 0)
                            HotCostModifiers.CloneOverflow(ref cold, srcCard.CardUid, clone.CardUid);
                        AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in clone, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
                    }
                }
                break;

            case HotPowerId.GravityPower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx]);
                if (ownerSlot == 0 && snapAmt > 0)
                    DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: snapAmt, isPoweredAttack: false);
                break;
            }

            case HotPowerId.SerpentFormPower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx]);
                if (ownerSlot == 0 && snapAmt > 0)
                    DamageRandomEnemy(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: snapAmt, isPoweredAttack: false);
                break;
            }

            case HotPowerId.StranglePower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx]);
                if (args.ActorSlot == 0 && ownerSlot >= 3 && snapAmt > 0)
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: ownerSlot, baseDamage: snapAmt, isPoweredAttack: false, isUnblockable: true);
                break;
            }

            case HotPowerId.GalvanicPower:
                if (args.CardIdx >= 0 && args.CardIdx < s.TotalCardCount
                    && s.Cards[args.CardIdx].AfflId != 0
                    && rules.Afflictions[s.Cards[args.CardIdx].AfflId].AfflictionId == HotAfflictionId.Galvanized
                    && amount > 0)
                {
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0xFF, targetSlot: 0, baseDamage: amount, isPoweredAttack: false);
                }
                break;

            case HotPowerId.VitalSparkPower:
                if (args.CardIdx >= 0 && args.CardIdx < s.TotalCardCount
                    && s.Cards[args.CardIdx].AfflId != 0
                    && s.Cards[args.CardIdx].AfflId < rules.Afflictions.Length
                    && rules.Afflictions[s.Cards[args.CardIdx].AfflId].AfflictionId == HotAfflictionId.Tainted
                    && amount > 0)
                {
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot: 0, applierSlot: 0xFF, HotPowerId.TaintedPower, amount);
                }
                break;

            case HotPowerId.SmoggyPower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Skill)
                {
                    byte smogId = rules.GetAfflictionDefId(HotAfflictionId.Smog);
                    if (smogId != 0)
                    {
                        int total = s.TotalCardCount;
                        for (int c = 0; c < total; c++)
                        {
                            if (s.Cards[c].AfflId == 0 && rules.Cards[s.Cards[c].CardDefId].Type == CardType.Skill)
                            {
                                s.Cards[c].AfflId = smogId;
                                s.Cards[c].AfflAmt = 1;
                            }
                        }
                    }
                }
                break;

            case HotPowerId.WitheringPresencePower:
                if (ownerSlot >= 3 && s.Powers[powerIdx].TargetSlot == 0)
                {
                    int left = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : 6;
                    left--;
                    if (left <= 0)
                    {
                        s.Powers[powerIdx].Aux0 = 6;
                        ushort witherId = rules.GetCardDefId(HotCustomCardId.Wither);
                        if (witherId != 0)
                            AddGeneratedFixedCard(ref s, ref cold, rules, scratch, witherId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: false);
                    }
                    else
                    {
                        s.Powers[powerIdx].Aux0 = left;
                    }
                }
                break;

            case HotPowerId.PhantomBladesPower:
                if (ownerSlot == 0 && cardDef.IsShivTag)
                    s.Powers[powerIdx].Aux0 = 1;
                break;

            case HotPowerId.VoidFormPower:
                if (ownerSlot == 0)
                    s.Powers[powerIdx].Aux0++;
                break;

            case HotPowerId.TenderPower:
                if (args.ActorSlot == 0 && ownerSlot == 0)
                {
                    byte applier = s.Powers[powerIdx].ApplierSlot;
                    s.Powers[powerIdx].Aux0++;
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, applier, HotPowerId.StrengthPower, -1);
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, applier, HotPowerId.DexterityPower, -1);
                }
                break;

            case HotPowerId.MonologuePower:
                if (ownerSlot == 0 && s.Powers[powerIdx].AuxCardUid > 0)
                {
                    s.Powers[powerIdx].AuxCardUid--;
                    int strGain = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : 1;
                    s.Powers[powerIdx].Aux1 += strGain;
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, strGain);
                }
                break;

            case HotPowerId.PaleBlueDotPower:
                if (ownerSlot == 0
                    && (s.Powers[powerIdx].Flags & HotPowerFlags.BoolAux) == 0
                    && s.Counters[(int)HotCounterSlot.CardsPlayedThisTurn] + 1 >= 5)
                {
                    s.Powers[powerIdx].Flags |= HotPowerFlags.BoolAux;
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DrawCardsNextTurnPower, amount);
                }
                break;

            case HotPowerId.RupturePower:
                if (ownerSlot == 0)
                {
                    // 0.113.1 RupturePower.cs:60-66: apply any deferred Strength accumulated during this card play.
                    ushort uid = args.CardUid != 0
                        ? args.CardUid
                        : ((uint)args.CardIdx < (uint)s.TotalCardCount ? s.Cards[args.CardIdx].CardUid : (ushort)0);
                    if (uid != 0 && TryConsumeRuptureStrength(ref s.Powers[powerIdx], uid, out int pendingStrength) && pendingStrength > 0)
                        ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.StrengthPower, pendingStrength);
                }
                break;
        }
    }

    // RupturePower packs up to 2 nested active card plays in Aux0/Aux1: low 16 bits = CardUid, high 16 bits = pending Strength.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RegisterRupturePlayedCard(ref HotPower p, ushort cardUid)
    {
        if ((ushort)p.Aux0 == 0 || (ushort)p.Aux0 == cardUid)
            p.Aux0 = cardUid;
        else if ((ushort)p.Aux1 == 0 || (ushort)p.Aux1 == cardUid)
            p.Aux1 = cardUid;
        else if ((uint)p.Aux1 >> 16 == 0)
            p.Aux1 = cardUid;
    }

    private static bool TryDeferRuptureStrength(
        in CombatHotState s,
        ref HotPower p,
        in HotHookArgs args,
        int strengthGain)
    {
        ushort uid0 = (ushort)p.Aux0;
        ushort uid1 = (ushort)p.Aux1;
        if (uid0 == 0 && uid1 == 0)
            return false;

        ushort sourceUid = args.CardUid;
        if (sourceUid == 0 && args.CardIdx >= 0)
        {
            if ((uint)args.CardIdx < (uint)s.TotalCardCount
                && (args.CardDefId == 0 || s.Cards[args.CardIdx].CardDefId == args.CardDefId))
            {
                sourceUid = s.Cards[args.CardIdx].CardUid;
            }
            if ((sourceUid == 0 || (sourceUid != uid0 && sourceUid != uid1)) && args.CardDefId != 0)
            {
                int playStart = s.PlayStart;
                int playEnd = playStart + s.PlayCount;
                for (int i = playStart; i < playEnd; i++)
                {
                    if (s.Cards[i].CardDefId == args.CardDefId)
                    {
                        ushort playUid = s.Cards[i].CardUid;
                        if (playUid == uid1 || playUid == uid0)
                        {
                            sourceUid = playUid;
                            break;
                        }
                    }
                }
            }
        }

        if (sourceUid == 0)
            return false;

        if (uid1 == sourceUid)
        {
            int next = (int)((uint)p.Aux1 >> 16) + strengthGain;
            p.Aux1 = uid1 | (Math.Clamp(next, 0, ushort.MaxValue) << 16);
            return true;
        }
        if (uid0 == sourceUid)
        {
            int next = (int)((uint)p.Aux0 >> 16) + strengthGain;
            p.Aux0 = uid0 | (Math.Clamp(next, 0, ushort.MaxValue) << 16);
            return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryConsumeRuptureStrength(ref HotPower p, ushort cardUid, out int pendingStrength)
    {
        if ((ushort)p.Aux1 == cardUid)
        {
            pendingStrength = (int)((uint)p.Aux1 >> 16);
            p.Aux1 = 0;
            return true;
        }
        if ((ushort)p.Aux0 == cardUid)
        {
            pendingStrength = (int)((uint)p.Aux0 >> 16);
            p.Aux0 = 0;
            return true;
        }
        pendingStrength = 0;
        return false;
    }

    // Packs up to 5 nested LIFO BeforeCardPlayed -> AfterCardPlayed amount frames into
    // Aux0 (slots 0..1, 16-bit each), Aux1 (slots 2..3, 16-bit each), and AuxCardUid
    // (low 4 bits = depth 0..15, high 12 bits = slot 4). Zero struct size change, zero alloc,
    // and returns all three fields to 0 when the outermost card play finishes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void LatchCardPlayAmount(ref HotPower p, int amount)
    {
        int depth = p.AuxCardUid & 0x0F;
        if (depth >= 15)
            return;
        int clamped16 = Math.Clamp(amount, 0, ushort.MaxValue);
        switch (depth)
        {
            case 0:
                p.Aux0 = (p.Aux0 & unchecked((int)0xFFFF0000)) | clamped16;
                break;
            case 1:
                p.Aux0 = (p.Aux0 & 0x0000FFFF) | (clamped16 << 16);
                break;
            case 2:
                p.Aux1 = (p.Aux1 & unchecked((int)0xFFFF0000)) | clamped16;
                break;
            case 3:
                p.Aux1 = (p.Aux1 & 0x0000FFFF) | (clamped16 << 16);
                break;
            case 4:
                p.AuxCardUid = (ushort)((p.AuxCardUid & 0x000F) | (Math.Clamp(amount, 0, 0x0FFF) << 4));
                break;
        }
        p.AuxCardUid = (ushort)((p.AuxCardUid & 0xFFF0) | (depth + 1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ConsumeCardPlayAmount(ref HotPower p)
    {
        int depth = p.AuxCardUid & 0x0F;
        if (depth <= 0)
            return 0;
        int slot = depth - 1;
        p.AuxCardUid = (ushort)((p.AuxCardUid & 0xFFF0) | slot);
        switch (slot)
        {
            case 0:
            {
                int val = p.Aux0 & 0x0000FFFF;
                p.Aux0 &= unchecked((int)0xFFFF0000);
                return val;
            }
            case 1:
            {
                int val = (int)((uint)p.Aux0 >> 16);
                p.Aux0 &= 0x0000FFFF;
                return val;
            }
            case 2:
            {
                int val = p.Aux1 & 0x0000FFFF;
                p.Aux1 &= unchecked((int)0xFFFF0000);
                return val;
            }
            case 3:
            {
                int val = (int)((uint)p.Aux1 >> 16);
                p.Aux1 &= 0x0000FFFF;
                return val;
            }
            case 4:
            {
                int val = p.AuxCardUid >> 4;
                p.AuxCardUid &= 0x000F;
                return val;
            }
            default:
                return Math.Max(0, p.Amount);
        }
    }

    internal static int LastHitTotalPlusOverkill => t_lastHitTotalPlusOverkill;

    [ThreadStatic]
    private static int[]? t_attackContextPrevSeq;

    [ThreadStatic]
    private static int t_attackContextDepth;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void BeginAttackContext(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        int cardIdx,
        ushort cardDefId,
        bool powered)
    {
        if (scratch.NextAttackCommandSeq == 0)
            t_attackContextDepth = 0;
        int[] stack = t_attackContextPrevSeq ??= new int[16];
        if (t_attackContextDepth < stack.Length)
            stack[t_attackContextDepth] = scratch.ActiveAttackCommandSeq;
        t_attackContextDepth++;
        scratch.ActiveAttackCommandSeq = ++scratch.NextAttackCommandSeq;
        BeginAttackHooks(ref s, ref cold, rules, scratch, dealerSlot, cardIdx, cardDefId, powered);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EndAttackContext(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        int cardIdx,
        ushort cardDefId,
        bool powered)
    {
        try
        {
            EndAttackHooks(ref s, ref cold, rules, scratch, dealerSlot, cardIdx, cardDefId, powered);
        }
        finally
        {
            if (t_attackContextDepth > 0)
            {
                t_attackContextDepth--;
                int[]? stack = t_attackContextPrevSeq;
                scratch.ActiveAttackCommandSeq = stack != null && t_attackContextDepth < stack.Length
                    ? stack[t_attackContextDepth]
                    : 0;
            }
            else
            {
                scratch.ActiveAttackCommandSeq = 0;
            }
        }
    }

    private static void HandleModifyCardPlayCount(
        ref CombatHotState s,
        HotRules rules,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (amount <= 0 || args.CardDefId == 0 || args.CardDefId >= rules.Cards.Length)
            return;

        HotCardDef cardDef = rules.Cards[args.CardDefId];
        switch (powerId)
        {
            case HotPowerId.BurstPower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Skill)
                {
                    args.PlayCount++;
                    DecrementPowerAt(ref s, rules, powerIdx);
                }
                break;

            case HotPowerId.DuplicationPower:
                if (ownerSlot == 0)
                {
                    args.PlayCount++;
                    DecrementPowerAt(ref s, rules, powerIdx);
                }
                break;

            case HotPowerId.OneTwoPunchPower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Attack)
                {
                    args.PlayCount++;
                    DecrementPowerAt(ref s, rules, powerIdx);
                }
                break;

            case HotPowerId.SignalBoostPower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Power)
                {
                    args.PlayCount++;
                    DecrementPowerAt(ref s, rules, powerIdx);
                }
                break;

            case HotPowerId.EchoFormPower:
                if (ownerSlot == 0 && s.Counters[(int)HotCounterSlot.FirstInSeriesCardPlayStartsThisTurn] < amount)
                    args.PlayCount++;
                break;

            case HotPowerId.TagTeamPower:
                if (args.TargetSlot == ownerSlot && cardDef.Type == CardType.Attack)
                {
                    args.PlayCount++;
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                }
                break;
        }
    }

    private static void HandleModifyCardPlayResultLocation(
        ref CombatHotState s,
        HotRules rules,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (ownerSlot != 0 || amount <= 0 || args.CardDefId == 0 || args.CardDefId >= rules.Cards.Length)
            return;

        HotCardDef cardDef = rules.Cards[args.CardDefId];
        switch (powerId)
        {
            case HotPowerId.CorruptionPower:
                // STS2 0.113.1 CorruptionPower.cs:28-40 & CardModel.cs:2043-2044,2171-2182:
                // redirects all Skills (including ParticleWall -> Hand and IsDupe -> None) to Exhaust.
                if (cardDef.Type == CardType.Skill)
                    args.ResultPile = (byte)HotPileKind.Exhaust;
                break;

            case HotPowerId.ReboundPower:
                if (args.ResultPile == (byte)HotPileKind.Discard)
                {
                    args.ResultPile = ResultPileDrawTop;
                    DecrementPowerAt(ref s, rules, powerIdx);
                }
                break;

            case HotPowerId.FeralPower:
                if (cardDef.Type == CardType.Attack
                    && args.EnergySpent == 0
                    && args.ResultPile != ResultPileRemoveFromCombat
                    && s.Powers[powerIdx].Aux0 < amount)
                {
                    args.ResultPile = ResultPileHandTop;
                    s.Powers[powerIdx].Aux0++;
                }
                break;

            case HotPowerId.NostalgiaPower:
                if (cardDef.Type is CardType.Attack or CardType.Skill
                    && args.ResultPile == (byte)HotPileKind.Discard
                    && s.Counters[(int)HotCounterSlot.AttackSkillStartsThisTurn] < amount)
                {
                    args.ResultPile = ResultPileDrawTop;
                }
                break;
        }
    }

    private static void HandleModifyEnergyCost(
        in CombatHotState s,
        HotRules rules,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (amount <= 0 || args.CardDefId == 0 || args.CardDefId >= rules.Cards.Length)
            return;

        if (powerId == HotPowerId.EnsemblePower)
        {
            if (ownerSlot != 0)
                args.IntValue = 0;
            return;
        }

        if (ownerSlot != 0)
            return;

        HotCardDef cardDef = rules.Cards[args.CardDefId];
        switch (powerId)
        {
            case HotPowerId.CorruptionPower:
                if (cardDef.Type == CardType.Skill)
                    args.IntValue = 0;
                break;

            case HotPowerId.FreeAttackPower:
                if (cardDef.Type == CardType.Attack)
                    args.IntValue = 0;
                break;

            case HotPowerId.FreeSkillPower:
                if (cardDef.Type == CardType.Skill)
                    args.IntValue = 0;
                break;

            case HotPowerId.FreePowerPower:
                if (cardDef.Type == CardType.Power)
                    args.IntValue = 0;
                break;

            case HotPowerId.VeilpiercerPower:
                if (args.CardIdx >= 0 && IsCardEthereal(in s, in s.Cards[args.CardIdx], rules))
                    args.IntValue = 0;
                break;

            case HotPowerId.VoidFormPower:
                if (s.Powers[powerIdx].Aux0 < amount)
                    args.IntValue = 0;
                break;

            case HotPowerId.BorrowedTimePower:
                args.IntValue += amount;
                break;
        }
    }

    private static void HandleModifyHandDraw(HotPowerId powerId, byte ownerSlot, int amount, ref HotHookArgs args)
    {
        if (ownerSlot != 0 || amount <= 0)
            return;

        switch (powerId)
        {
            case HotPowerId.MachineLearningPower:
            case HotPowerId.DrawCardsNextTurnPower:
            case HotPowerId.DemesnePower:
            case HotPowerId.ToolsOfTheTradePower:
            case HotPowerId.TyrannyPower:
                args.HandDrawCount += amount;
                break;
            case HotPowerId.ClarityPower:
                // 0.113.1 ClarityPower.cs:22-29: always +1 card; Amount tracks turns remaining.
                args.HandDrawCount += 1;
                break;
            case HotPowerId.MindRotPower:
                args.HandDrawCount = Math.Max(0, args.HandDrawCount - amount);
                break;
            case HotPowerId.AmnesiaPower:
                args.HandDrawCount = Math.Max(0, args.HandDrawCount - 2);
                break;
        }
    }

    private static void HandleAfterCardExhausted(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (ownerSlot != 0 || amount <= 0)
            return;

        switch (powerId)
        {
            case HotPowerId.FeelNoPainPower:
                GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: amount, isCardBlock: false);
                break;

            case HotPowerId.DarkEmbracePower:
                if (args.CausedByEthereal)
                    s.Powers[powerIdx].Aux0++;
                else
                    DrawCards(ref s, ref cold, rules, scratch, amount, fromHandDraw: false);
                break;
        }
    }

    private static void HandleAfterCardDrawn(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (ownerSlot != 0 || args.CardIdx < 0 || args.CardIdx >= s.TotalCardCount)
            return;

        HotCardDef cardDef = rules.Cards[s.Cards[args.CardIdx].CardDefId];
        switch (powerId)
        {
            case HotPowerId.ConfusedPower:
                // STS2 0.113.1 ConfusedPower.cs:31-45: only skips Canonical < 0; CostsX cards have Canonical == 0,
                // so vanilla still rolls CombatEnergyCosts RNG and attaches an EndOfCombat modifier.
                if (cardDef.BaseCost >= 0 || (s.Cards[args.CardIdx].Flags & HotCardFlags.CostsX) != 0)
                {
                    int rolled = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatEnergyCosts], 4);
                    CostSetThisCombat(ref s.Cards[args.CardIdx], ref cold, rolled);
                }
                break;

            case HotPowerId.IterationPower:
                if (cardDef.Type == CardType.Status && s.Counters[(int)HotCounterSlot.StatusCardsDrawnThisTurn] <= 1 && amount > 0)
                    DrawCards(ref s, ref cold, rules, scratch, amount, fromHandDraw: false);
                break;

            case HotPowerId.PagestormPower:
                if (IsCardEthereal(in s, in s.Cards[args.CardIdx], rules) && amount > 0)
                    DrawCards(ref s, ref cold, rules, scratch, amount, fromHandDraw: false);
                break;

            case HotPowerId.SpeedsterPower:
                if (!args.FromHandDraw && s.CurrentSide == HotCombatSide.Player && amount > 0)
                    DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: amount, isPoweredAttack: false);
                break;

            case HotPowerId.CorrosiveWavePower:
                if (amount > 0)
                    ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.PoisonPower, amount);
                break;

            case HotPowerId.AutomationPower:
                if (amount > 0)
                {
                    int left = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : 10;
                    left--;
                    if (left <= 0)
                    {
                        s.Powers[powerIdx].Aux0 = 10;
                        GainEnergy(ref s, rules, amount);
                    }
                    else
                    {
                        s.Powers[powerIdx].Aux0 = left;
                    }
                }
                break;

            case HotPowerId.HellraiserPower:
                if (cardDef.IsStrikeTag && amount > 0)
                {
                    // STS2 0.113.1 HellraiserPower.cs:26,48-72: cap auto-plays to 9 per turn when
                    // HittableEnemies.All(c => c.HpDisplay.IsInfinite()) (vacuously true when 0
                    // hittable living enemies exist, e.g. reviving AdaptablePower/ReattachPower).
                    bool allHittableInfinite = true;
                    int rosterCount = s.RosterCount;
                    for (int r = 0; r < rosterCount; r++)
                    {
                        byte eSlot = s.ActiveEnemyRoster[r];
                        if (eSlot >= s.EnemyCount)
                            continue;
                        if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) == 0
                            || s.Enemies[eSlot].DeathPhase != HotDeathPhase.None)
                            continue;
                        if (s.Enemies[eSlot].Vital.MaxHp < 999_999_999)
                        {
                            allHittableInfinite = false;
                            break;
                        }
                    }

                    bool shouldAutoPlay = true;
                    if (allHittableInfinite)
                    {
                        int prevAutoPlays = s.Powers[powerIdx].Aux0;
                        if (prevAutoPlays >= 9)
                            shouldAutoPlay = false;
                        s.Powers[powerIdx].Aux0 = prevAutoPlays < short.MaxValue ? prevAutoPlays + 1 : short.MaxValue;
                    }
                    else
                    {
                        s.Powers[powerIdx].Aux0 = 0;
                    }

                    if (shouldAutoPlay)
                        AutoPlayCard(ref s, ref cold, rules, scratch, args.CardIdx);
                }
                break;

            case HotPowerId.CacophonyPower:
                if (amount > 0)
                {
                    int left = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : 3;
                    left--;
                    if (left <= 0)
                    {
                        s.Powers[powerIdx].Aux0 = 3;
                        DamageRandomEnemy(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: amount, isPoweredAttack: false);
                    }
                    else
                    {
                        s.Powers[powerIdx].Aux0 = left;
                    }
                }
                break;

            case HotPowerId.ChainsOfBindingPower:
                // 0.113.1 ChainsOfBindingPower.cs:41-51: afflict first `amount` unafflicted drawn cards
                // during player's turn with Bound(amount) (Bound.CanAfflictUnplayableCards == true).
                if (s.CurrentSide == HotCombatSide.Player
                    && amount > 0
                    && s.Cards[args.CardIdx].AfflId == 0
                    && s.Powers[powerIdx].Aux1 < amount)
                {
                    byte boundId = rules.GetAfflictionDefId(HotAfflictionId.Bound);
                    if (boundId != 0)
                    {
                        s.Cards[args.CardIdx].AfflId = boundId;
                        s.Cards[args.CardIdx].AfflAmt = (short)amount;
                        s.Powers[powerIdx].Aux1++;
                    }
                }
                break;
        }
    }

    private static void HandleAfterCardGenerated(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (ownerSlot != 0 || args.ActorSlot != 0 || amount <= 0)
            return;

        CardType genType = args.CardDefId < rules.Cards.Length ? rules.Cards[args.CardDefId].Type : CardType.None;
        switch (powerId)
        {
            case HotPowerId.ArsenalPower:
                ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.StrengthPower, amount);
                break;

            case HotPowerId.PillarOfCreationPower:
                GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: amount, isCardBlock: false);
                break;

            case HotPowerId.SmokestackPower:
                if (genType == CardType.Status)
                    DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: amount, isPoweredAttack: false);
                break;

            case HotPowerId.TrashToTreasurePower:
                if (genType == CardType.Status)
                    ChannelRandomOrb(ref s, ref cold, rules, scratch, amount);
                break;
        }
    }

    private static void HandleAfterCardEnteredCombat(
        ref CombatHotState s,
        HotRules rules,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (args.CardIdx < 0 || args.CardIdx >= s.TotalCardCount)
            return;
        ref HotCard card = ref s.Cards[args.CardIdx];
        if (card.AfflId != 0)
            return;

        CardType cardType = card.CardDefId < rules.Cards.Length ? rules.Cards[card.CardDefId].Type : CardType.None;
        switch (powerId)
        {
            case HotPowerId.VitalSparkPower:
                if (cardType == CardType.Skill && amount > 0)
                {
                    byte taintedId = rules.GetAfflictionDefId(HotAfflictionId.Tainted);
                    if (taintedId != 0)
                    {
                        card.AfflId = taintedId;
                        card.AfflAmt = (byte)Math.Clamp(amount, 0, byte.MaxValue);
                    }
                }
                break;

            case HotPowerId.GalvanicPower:
                if (cardType == CardType.Power && amount > 0)
                {
                    byte galvId = rules.GetAfflictionDefId(HotAfflictionId.Galvanized);
                    if (galvId != 0)
                    {
                        card.AfflId = galvId;
                        card.AfflAmt = (byte)Math.Clamp(amount, 0, byte.MaxValue);
                    }
                }
                break;

            case HotPowerId.TangledPower:
                if (ownerSlot == 0 && cardType == CardType.Attack && amount > 0)
                {
                    byte entId = rules.GetAfflictionDefId(HotAfflictionId.Entangled);
                    if (entId != 0)
                    {
                        card.AfflId = entId;
                        card.AfflAmt = 1;
                    }
                }
                break;

            case HotPowerId.SmoggyPower:
                if (ownerSlot == 0 && cardType == CardType.Skill && s.Counters[(int)HotCounterSlot.SkillPlayStartsThisTurn] > 0)
                {
                    byte smogId = rules.GetAfflictionDefId(HotAfflictionId.Smog);
                    if (smogId != 0)
                    {
                        card.AfflId = smogId;
                        card.AfflAmt = 1;
                    }
                }
                break;

            case HotPowerId.HexPower:
                if (ownerSlot == 0 && amount > 0)
                {
                    byte hexId = rules.GetAfflictionDefId(HotAfflictionId.Hexed);
                    if (hexId != 0)
                    {
                        card.AfflId = hexId;
                        card.AfflAmt = (short)Math.Clamp(amount, 0, short.MaxValue);
                    }
                }
                break;

            case HotPowerId.RingingPower:
                if (ownerSlot == 0 && amount > 0 && card.AfflId == 0)
                {
                    byte ringId = rules.GetAfflictionDefId(HotAfflictionId.Ringing);
                    if (ringId != 0)
                    {
                        card.AfflId = ringId;
                        card.AfflAmt = 1;
                    }
                }
                break;
        }
    }

    private static void HandleAfterPowerAmountChanged(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (args.PowerDefId == 0 || args.PowerDefId >= rules.Powers.Length)
            return;

        if (powerId == HotPowerId.VitalSparkPower
            && args.TargetSlot == ownerSlot
            && rules.Powers[args.PowerDefId].PowerId == HotPowerId.VitalSparkPower)
        {
            byte taintedId = rules.GetAfflictionDefId(HotAfflictionId.Tainted);
            if (taintedId != 0)
            {
                byte clamped = (byte)Math.Clamp(amount, 0, byte.MaxValue);
                int total = s.TotalCardCount;
                for (int c = 0; c < total; c++)
                {
                    if (s.Cards[c].AfflId == taintedId)
                        s.Cards[c].AfflAmt = clamped;
                }
            }
            return;
        }

        if (ownerSlot != 0 || args.ActorSlot != 0 || amount <= 0)
            return;

        HotPowerDef changedDef = rules.Powers[args.PowerDefId];
        switch (powerId)
        {
            case HotPowerId.ViciousPower:
                if (args.Amount > 0 && changedDef.PowerId == HotPowerId.VulnerablePower)
                    DrawCards(ref s, ref cold, rules, scratch, amount, fromHandDraw: false);
                break;

            case HotPowerId.SleightOfFleshPower:
                if (args.TargetSlot >= 3 && args.Amount != 0 && changedDef.Type == PowerType.Debuff)
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: args.TargetSlot, baseDamage: amount, isPoweredAttack: false);
                break;
        }
    }

    private static void HandleBeforeTurnEndPower(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (!IsSlotOnCurrentSide(args.Side, ownerSlot) || amount <= 0)
            return;
        if (!IsCreatureSlotValid(in s, ownerSlot) || (GetCreatureRef(ref s, ownerSlot).Flags & HotCreatureFlags.Alive) == 0)
            return;

        if (rules.MetallicizePowerDefId != 0 && s.Powers[powerIdx].PowerDefId == rules.MetallicizePowerDefId)
        {
            GainBlock(ref s, ref cold, rules, scratch, targetSlot: ownerSlot, baseAmount: amount, isCardBlock: false);
            return;
        }

        switch (powerId)
        {
            case HotPowerId.PlatingPower:
                GainBlock(ref s, ref cold, rules, scratch, targetSlot: ownerSlot, baseAmount: amount, isCardBlock: false);
                break;

            case HotPowerId.RegenPower:
                HealCreature(ref s, ref cold, rules, scratch, ownerSlot, amount);
                DecrementPowerAt(ref s, rules, powerIdx);
                break;

            case HotPowerId.HailstormPower:
                if (ownerSlot == 0 && CountOrbsOfKind(in s, rules, HotOrbId.Frost) >= 1)
                    DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: amount, isPoweredAttack: false);
                break;

            case HotPowerId.TheBombPower:
                if (ownerSlot == 0)
                {
                    if (amount > 1)
                    {
                        s.Powers[powerIdx].Amount--;
                    }
                    else
                    {
                        int dmg = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : 40;
                        RemovePowerAt(ref s, powerIdx);
                        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                        DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: dmg, isPoweredAttack: false);
                    }
                }
                break;

            case HotPowerId.ChainsOfBindingPower:
                if (ownerSlot == 0)
                {
                    int total = s.TotalCardCount;
                    for (int c = 0; c < total; c++)
                    {
                        if (s.Cards[c].AfflId != 0 && rules.Afflictions[s.Cards[c].AfflId].AfflictionId == HotAfflictionId.Bound)
                        {
                            s.Cards[c].AfflId = 0;
                            s.Cards[c].AfflAmt = 0;
                        }
                    }
                    s.Powers[powerIdx].Aux0 = 0;
                }
                break;
        }
    }

    private static void HandleAfterAutoPostPlayPower(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (args.Side != HotCombatSide.Player || ownerSlot != 0 || amount <= 0)
            return;

        if (powerId == HotPowerId.StampedePower)
        {
            Span<ushort> attackUids = stackalloc ushort[HotLimits.MaxStackAllocElements];
            for (int k = 0; k < amount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; k++)
            {
                int atkCount = 0;
                int handCount = s.HandCount;
                for (int h = 0; h < handCount && atkCount < attackUids.Length; h++)
                {
                    ref readonly HotCard hc = ref s.Cards[h];
                    if (rules.Cards[hc.CardDefId].Type == CardType.Attack
                        && (hc.LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0)
                    {
                        attackUids[atkCount++] = hc.CardUid;
                    }
                }
                if (atkCount == 0)
                    break;
                int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardSelection], atkCount);
                int liveIdx = FindCardIndexByUid(in s, HotPileKind.Hand, attackUids[pick]);
                if (liveIdx >= 0)
                    AutoPlayCard(ref s, ref cold, rules, scratch, liveIdx, targetEnemySlot: -1, forceExhaust: false);
            }
        }
    }

    private static void HandleAfterTurnEndPower(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (powerId is HotPowerId.IntangiblePower or HotPowerId.NoBlockPower or HotPowerId.ColossusPower)
        {
            if (args.Side == HotCombatSide.Enemy)
                DecrementPowerAt(ref s, rules, powerIdx);
            return;
        }

        bool ownerParticipates = IsSlotOnCurrentSide(args.Side, ownerSlot);
        if (ownerParticipates
            && args.Side == HotCombatSide.Enemy
            && ownerSlot >= 3
            && (args.EnemyParticipantMask & 0x8000_0000u) != 0)
        {
            ownerParticipates = (args.EnemyParticipantMask & (1u << (ownerSlot - 3))) != 0;
        }

        // Opponent-side turn-end expirations
        if (!ownerParticipates)
        {
            if (powerId == HotPowerId.FlameBarrierPower
                || powerId == HotPowerId.ConcoctPower
                || (powerId == HotPowerId.TaintedPower && args.Side == HotCombatSide.Enemy))
            {
                RemovePowerAt(ref s, powerIdx);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            }
            else if (powerId == HotPowerId.SkittishPower)
            {
                s.Powers[powerIdx].Flags &= ~HotPowerFlags.BoolAux;
            }
            return;
        }

        switch (powerId)
        {
            case HotPowerId.RitualPower:
                if ((s.Powers[powerIdx].Flags & HotPowerFlags.WasJustAppliedByEnemy) != 0)
                    s.Powers[powerIdx].Flags &= ~HotPowerFlags.WasJustAppliedByEnemy;
                else if (amount > 0)
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.StrengthPower, amount);
                break;

            case HotPowerId.HighVoltagePower:
            case HotPowerId.TerritorialPower:
                if (amount > 0)
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.StrengthPower, amount);
                break;

            case HotPowerId.DarkEmbracePower:
                if (ownerSlot == 0 && s.Powers[powerIdx].Aux0 > 0)
                {
                    int ethCount = s.Powers[powerIdx].Aux0;
                    s.Powers[powerIdx].Aux0 = 0;
                    DrawCards(ref s, ref cold, rules, scratch, amount * ethCount, fromHandDraw: false);
                }
                break;

            case HotPowerId.ConsumingShadowPower:
                if (ownerSlot == 0 && amount > 0)
                {
                    for (int k = 0; k < amount && s.OrbCount > 0; k++)
                        EvokeOrb(ref s, ref cold, rules, scratch, orbIdx: s.OrbCount - 1, repeat: 1, dequeue: true);
                }
                break;

            case HotPowerId.ConstrictPower:
                if (amount > 0)
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: ownerSlot, targetSlot: ownerSlot, baseDamage: amount, isPoweredAttack: false);
                break;

            case HotPowerId.DemisePower:
                if (amount > 0)
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0xFF, targetSlot: ownerSlot, baseDamage: amount, isPoweredAttack: false, isUnblockable: true);
                break;

            case HotPowerId.MagicBombPower:
                if (amount > 0)
                {
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: ownerSlot, targetSlot: ownerSlot, baseDamage: amount, isPoweredAttack: false);
                }
                break;

            case HotPowerId.MonologuePower:
            {
                int strApplied = s.Powers[powerIdx].Aux1;
                RemovePowerAt(ref s, powerIdx);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                if (strApplied != 0)
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.StrengthPower, -strApplied);
                break;
            }

            case HotPowerId.TenderPower:
            {
                int cardsPlayed = s.Powers[powerIdx].Aux0;
                byte applier = s.Powers[powerIdx].ApplierSlot;
                s.Powers[powerIdx].Aux0 = 0;
                if (cardsPlayed > 0)
                {
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, applier, HotPowerId.StrengthPower, cardsPlayed);
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, applier, HotPowerId.DexterityPower, cardsPlayed);
                }
                break;
            }

            case HotPowerId.SmoggyPower:
                if (ownerSlot == 0)
                {
                    byte smogId = rules.GetAfflictionDefId(HotAfflictionId.Smog);
                    if (smogId != 0)
                    {
                        int total = s.TotalCardCount;
                        for (int c = 0; c < total; c++)
                        {
                            if (s.Cards[c].AfflId == smogId)
                            {
                                s.Cards[c].AfflId = 0;
                                s.Cards[c].AfflAmt = 0;
                            }
                        }
                    }
                }
                break;

            case HotPowerId.AsleepPower:
                // STS2 0.113.1 AsleepPower.cs:38-57 & LagavulinMatriarch.cs:201-211:
                // PlatingPower is removed in BeforeSideTurnEndVeryEarly (Turn.cs) when Amount <= 1;
                // AfterSideTurnEnd decrements AsleepPower and sets Awake when Amount reaches 0.
                if (amount > 1)
                {
                    s.Powers[powerIdx].Amount--;
                }
                else
                {
                    RemovePowerAt(ref s, powerIdx);
                    if (ownerSlot >= 3)
                        s.Enemies[ownerSlot - 3].Flags |= HotEnemyFlags.Awake;
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                }
                break;

            case HotPowerId.SlumberPower:
                // STS2 0.113.1 SlumberPower.cs:40-51 & SlumberingBeetle.cs:95-107:
                // SlumberPower has no BeforeSideTurnEndVeryEarly; SlumberingBeetle.WakeUpMove removes PlatingPower here.
                if (amount > 1)
                {
                    s.Powers[powerIdx].Amount--;
                }
                else
                {
                    RemovePowerAt(ref s, powerIdx);
                    ushort platingId = rules.PlatingPowerDefId != 0 ? rules.PlatingPowerDefId : rules.GetPowerDefId(HotPowerId.PlatingPower);
                    if (platingId != 0)
                        SetPowerAmount(ref s, rules, ownerSlot, platingId, 0);
                    if (ownerSlot >= 3)
                        s.Enemies[ownerSlot - 3].Flags |= HotEnemyFlags.Awake;
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                }
                break;

            case HotPowerId.NemesisPower:
            {
                bool applyIntang = (s.Powers[powerIdx].Flags & HotPowerFlags.BoolAux) == 0;
                if (applyIntang)
                {
                    s.Powers[powerIdx].Flags |= HotPowerFlags.BoolAux;
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.IntangiblePower, 1);
                }
                else
                {
                    s.Powers[powerIdx].Flags &= ~HotPowerFlags.BoolAux;
                    ushort intangId = rules.GetPowerDefId(HotPowerId.IntangiblePower);
                    if (intangId != 0)
                        SetPowerAmount(ref s, rules, ownerSlot, intangId, 0);
                }
                break;
            }

            case HotPowerId.PanachePower:
                s.Powers[powerIdx].Aux0 = 5;
                break;

            case HotPowerId.JugglingPower:
                s.Powers[powerIdx].Aux0 = 0;
                s.Powers[powerIdx].AuxCardUid = 0;
                break;

            case HotPowerId.PaleBlueDotPower:
                s.Powers[powerIdx].Flags &= ~HotPowerFlags.BoolAux;
                break;

            case HotPowerId.PhantomBladesPower:
                s.Powers[powerIdx].Aux0 = 0;
                break;

            case HotPowerId.HellraiserPower:
                s.Powers[powerIdx].Aux0 = 0;
                break;

            case HotPowerId.TangledPower:
                RemovePowerAt(ref s, powerIdx);
                if (ownerSlot == 0)
                    HotMonsters.ClearCardAffliction(ref s, rules, HotAfflictionId.Entangled);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                break;

            case HotPowerId.RingingPower:
                RemovePowerAt(ref s, powerIdx);
                if (ownerSlot == 0)
                    HotMonsters.ClearCardAffliction(ref s, rules, HotAfflictionId.Ringing);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                break;

            // Transient 1-turn clears
            case HotPowerId.NoDrawPower:
            case HotPowerId.BorrowedTimePower:
            case HotPowerId.BurstPower:
            case HotPowerId.DuplicationPower:
            case HotPowerId.NoEnergyGainPower:
            case HotPowerId.OneTwoPunchPower:
            case HotPowerId.RagePower:
            case HotPowerId.ReboundPower:
            case HotPowerId.ShadowmeldPower:
            case HotPowerId.CorrosiveWavePower:
            case HotPowerId.GravityPower:
            case HotPowerId.StranglePower:
                RemovePowerAt(ref s, powerIdx);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                break;

            case HotPowerId.ShrinkPower:
                // ShrinkPower.cs:41,82: negative Amount is infinite (ShrinkerBeetle) and never ticks down.
                if (amount > 0)
                    DecrementPowerAt(ref s, rules, powerIdx);
                break;

            // Duration decrements
            case HotPowerId.DoubleDamagePower:
            case HotPowerId.RetainHandPower:
            case HotPowerId.DebilitatePower:
            case HotPowerId.HatchPower:
                DecrementPowerAt(ref s, rules, powerIdx);
                break;

            // Temporary stat retirements
            case HotPowerId.FlexPotionPower:
            case HotPowerId.SetupStrikePower:
            case HotPowerId.FeedingFrenzyPower:
            case HotPowerId.ReptileTrinketPower:
            case HotPowerId.CoordinatePower:
                RetireTempStat(ref s, ref cold, rules, scratch, powerIdx, ownerSlot, HotPowerId.StrengthPower, -amount);
                break;

            case HotPowerId.ShacklingPotionPower:
            case HotPowerId.PiercingWailPower:
            case HotPowerId.DarkShacklesPower:
            case HotPowerId.EnfeeblingTouchPower:
            case HotPowerId.CrushUnderPower:
            case HotPowerId.DyingStarPower:
            case HotPowerId.ManglePower:
            case HotPowerId.MonarchsGazeStrengthDownPower:
                RetireTempStat(ref s, ref cold, rules, scratch, powerIdx, ownerSlot, HotPowerId.StrengthPower, +amount);
                break;

            case HotPowerId.SpeedPotionPower:
            case HotPowerId.AnticipatePower:
            case HotPowerId.HelicalDartPower:
            case HotPowerId.FadePower:
                RetireTempStat(ref s, ref cold, rules, scratch, powerIdx, ownerSlot, HotPowerId.DexterityPower, -amount);
                break;

            case HotPowerId.FocusedStrikePower:
            case HotPowerId.HotfixPower:
            case HotPowerId.SynchronizePower:
                RetireTempStat(ref s, ref cold, rules, scratch, powerIdx, ownerSlot, HotPowerId.FocusPower, -amount);
                break;

            case HotPowerId.HyperbeamFocusDownPower:
                RetireTempStat(ref s, ref cold, rules, scratch, powerIdx, ownerSlot, HotPowerId.FocusPower, +amount);
                break;

            case HotPowerId.EscapeArtistPower:
                if (amount > 1)
                    s.Powers[powerIdx].Amount--;
                break;

            case HotPowerId.DisintegrationPower:
                // STS2 0.113.1 DisintegrationPower.AfterSideTurnEndLate (DisintegrationPower.cs:19):
                // dispatched in HotCombatEngine.Turn.cs's dedicated AfterSideTurnEndLate pass
                // AFTER all regular AfterTurnEnd powers & relics.
                break;

            case HotPowerId.AmnesiaPower:
                if (ownerSlot == 0 && args.Side == HotCombatSide.Player)
                {
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                }
                break;

            case HotPowerId.BattlewornDummyTimeLimitPower:
                if (amount > 1)
                {
                    s.Powers[powerIdx].Amount--;
                }
                else if (ownerSlot >= 3
                    && (s.Enemies[ownerSlot - 3].Vital.Flags & HotCreatureFlags.Alive) != 0)
                {
                    s.CombatFlags |= HotCombatFlags.BattlewornDummyTimedOut;
                    HotMonsters.CreatureEscaped(ref s, rules, (byte)(ownerSlot - 3));
                }
                break;
        }
    }

    private static void HandleBeforeSideTurnStartPower(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        // 0.113.1 PlatingPower.cs:41-56: enemy Plating grants unpowered Block on Player turn start when RoundNumber <= 1 (including Round 1 ExtraTurn).
        if (powerId == HotPowerId.PlatingPower)
        {
            if (args.Side == HotCombatSide.Player && ownerSlot >= 3 && s.RoundNumber <= 1 && amount > 0)
                GainBlock(ref s, ref cold, rules, scratch, targetSlot: ownerSlot, baseAmount: amount, isCardBlock: false);
            return;
        }

        // 0.113.1 HardenedShellPower.cs:76-87: resets damageReceivedThisTurn on every side turn start (both Player and Enemy).
        if (powerId == HotPowerId.HardenedShellPower)
        {
            s.Powers[powerIdx].Aux0 = 0;
            return;
        }

        if (!IsSlotOnCurrentSide(args.Side, ownerSlot))
            return;

        switch (powerId)
        {
            case HotPowerId.SlothPower:
            case HotPowerId.VoidFormPower:
                s.Powers[powerIdx].Aux0 = 0;
                break;

            case HotPowerId.AggressionPower:
                if (ownerSlot == 0 && amount > 0)
                {
                    Span<int> attacks = stackalloc int[HotLimits.MaxStackAllocElements];
                    int atkCount = 0;
                    int discStart = s.DiscardStart;
                    int discEnd = discStart + s.DiscardCount;
                    for (int i = discStart; i < discEnd && atkCount < attacks.Length; i++)
                    {
                        if (rules.Cards[s.Cards[i].CardDefId].Type == CardType.Attack)
                            attacks[atkCount++] = s.Cards[i].CardUid;
                    }
                    // 0.113.1 AggressionPower.cs:28 -> TakeRandom -> UnstableShuffle unconditionally consumes
                    // 1 NextULong on CombatCardSelection even when atkCount == 0.
                    HotRng.UnstableShuffle(attacks[..atkCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
                    int take = Math.Min(amount, atkCount);
                    for (int k = 0; k < take; k++)
                    {
                        int liveIdx = FindCardIndexByUid(in s, HotPileKind.Discard, (ushort)attacks[k]);
                        if (liveIdx >= 0)
                        {
                            int movedIdx = MoveCardToHandOrDiscard(ref s, liveIdx);
                            if (movedIdx >= 0)
                                UpgradeCard(ref s, rules, movedIdx, ref cold);
                        }
                    }
                }
                break;
        }
    }

    private static void HandleAfterSideTurnStartPower(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        // RampartPower (LivingShield): enemy-owned power that grants unpowered Block to TurretOperator on non-extra player turn start.
        if (powerId == HotPowerId.RampartPower)
        {
            if (args.Side == HotCombatSide.Player && !args.BoolFlag && amount > 0)
            {
                int rosterCount = s.RosterCount;
                for (int r = 0; r < rosterCount; r++)
                {
                    byte eSlot = s.ActiveEnemyRoster[r];
                    if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) != 0
                        && s.Enemies[eSlot].MonsterDefId < rules.Monsters.Length
                        && rules.Monsters[s.Enemies[eSlot].MonsterDefId].MonsterId == HotMonsterId.TurretOperator)
                    {
                        GainBlock(ref s, ref cold, rules, scratch, (byte)(3 + eSlot), amount, isCardBlock: false);
                    }
                }
            }
            return;
        }

        if (!IsSlotOnCurrentSide(args.Side, ownerSlot) || amount <= 0)
            return;

        switch (powerId)
        {
            case HotPowerId.BiasedCognitionPower:
                ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.FocusPower, -amount);
                break;

            case HotPowerId.CoolantPower:
                if (ownerSlot == 0)
                {
                    int distinct = CountDistinctOrbTypes(in s);
                    if (distinct > 0)
                        GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: distinct * amount, isCardBlock: false);
                }
                break;

            case HotPowerId.DemonFormPower:
                ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.StrengthPower, amount);
                break;

            case HotPowerId.FeralPower:
            case HotPowerId.SlowPower:
                s.Powers[powerIdx].Aux0 = 0;
                break;

            case HotPowerId.NoxiousFumesPower:
                if (ownerSlot == 0)
                    ApplyPowerToAllLivingEnemies(ref s, ref cold, rules, scratch, HotPowerId.PoisonPower, amount);
                break;

            case HotPowerId.WraithFormPower:
                ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.DexterityPower, -amount);
                break;

            case HotPowerId.ShadowStepPower:
                RemovePowerAt(ref s, powerIdx);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.DoubleDamagePower, amount);
                break;

            case HotPowerId.AmnesiaPower:
                if (ownerSlot == 0 && args.Side == HotCombatSide.Player)
                    LoseEnergy(ref s, 1);
                break;

            case HotPowerId.ReflectPower:
            case HotPowerId.ClarityPower:
            case HotPowerId.BlurPower:
            case HotPowerId.HibernatePower:
                DecrementPowerAt(ref s, rules, powerIdx);
                break;

            case HotPowerId.PrepTimePower:
                ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.VigorPower, amount);
                break;

            case HotPowerId.LoopPower:
                if (ownerSlot == 0 && s.OrbCount > 0)
                {
                    for (int k = 0; k < amount && s.OrbCount > 0; k++)
                    {
                        TriggerOrbPassive(ref s, ref cold, rules, scratch, orbIdx: 0, forcedTargetEnemySlot: -1, applyTriggerCountMods: false);
                        if (scratch.NeedsChoice || IsOverOrEnding(in s))
                            return;
                    }
                }
                break;

            case HotPowerId.CrimsonMantlePower:
                if (ownerSlot == 0)
                {
                    int selfDmg = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : 1;
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: 0, baseDamage: selfDmg, isPoweredAttack: false, isUnblockable: true);
                    if (!scratch.NeedsChoice && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
                        GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: amount, isCardBlock: false);
                }
                break;

            case HotPowerId.InfernoPower:
                if (ownerSlot == 0)
                {
                    int selfDmg = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : 1;
                    DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: 0, baseDamage: selfDmg, isPoweredAttack: false, isUnblockable: true);
                }
                break;

            case HotPowerId.RollingBoulderPower:
                if (ownerSlot == 0)
                {
                    ushort defId = s.Powers[powerIdx].PowerDefId;
                    int inc = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : 5;
                    byte occ = 0;
                    for (int k = 0; k < powerIdx; k++)
                    {
                        if (s.Powers[k].OwnerSlot == 0 && s.Powers[k].PowerDefId == defId)
                            occ++;
                    }
                    DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: amount, isPoweredAttack: false);
                    int liveIdx = FindPowerOccurrenceIndex(in s, 0, defId, occ);
                    if (liveIdx >= 0)
                        s.Powers[liveIdx].Amount += inc;
                }
                break;

            case HotPowerId.InfiniteBladesPower:
                if (ownerSlot == 0)
                {
                    ushort shivId = rules.GetCardDefId(HotCustomCardId.Shiv);
                    if (shivId != 0)
                    {
                        for (int k = 0; k < amount; k++)
                            AddGeneratedFixedCard(ref s, ref cold, rules, scratch, shivId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
                }
                break;

            case HotPowerId.NightmarePower:
                if (ownerSlot == 0)
                    TriggerNightmarePower(ref s, ref cold, rules, scratch, powerIdx, amount);
                break;

            case HotPowerId.EntropyPower:
                if (ownerSlot == 0 && s.HandCount > 0)
                {
                    // 0.113.1 EntropyPower.cs:27-32 & CardSelectCmd.cs:677-680: single simultaneous multi-select from Hand,
                    // bypassing selector in Hand pile order when !RequireManualConfirmation && handCount <= amount.
                    int handCount = s.HandCount;
                    Span<ushort> pickedUids = stackalloc ushort[handCount <= amount
                        ? Math.Min(handCount, HotLimits.MaxCards)
                        : HotLimits.MaxChoicesPerAction];
                    int pickedCount = 0;
                    if (handCount <= amount)
                    {
                        pickedCount = SnapshotPileCardUids(in s, HotPileKind.Hand, pickedUids);
                    }
                    else
                    {
                        int picks = Math.Min(amount, Math.Min(handCount, HotLimits.MaxChoicesPerAction));
                        scratch.BeginChoiceGroup(HotChoiceGroupFlags.MultiPick);
                        for (int k = 0; k < picks && pickedCount < s.HandCount && !IsOverOrEnding(in s); k++)
                        {
                            if (!RequestChoiceExcluding(ref s, scratch, HotChoiceKind.HandCard, 0, 1, 1, pickedUids[..pickedCount], out HotChoiceSelection sel))
                                return;
                            if (sel.CardUidOrIndex != 0)
                                pickedUids[pickedCount++] = sel.CardUidOrIndex;
                        }
                        scratch.EndChoiceGroup();
                        FlushDeferredPlayerSideTurnStartIfInSetup(ref s, ref cold, rules, scratch);
                        if (IsOverOrEnding(in s) || scratch.NeedsChoice)
                            return;
                    }
                    for (int i = 0; i < pickedCount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                    {
                        int idx = FindCardIndexByUid(in s, HotPileKind.Hand, pickedUids[i]);
                        if (idx >= 0)
                            TransformCardToRandomInCombat(ref s, ref cold, rules, scratch, idx);
                    }
                }
                break;

            case HotPowerId.YouAreMinePower:
                if (ownerSlot == 0 && s.PlayerTurnNumber >= 2)
                {
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0xFF, HotPowerId.WeakPower, amount);
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0xFF, HotPowerId.VulnerablePower, amount);
                    ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0xFF, HotPowerId.FrailPower, amount);
                }
                break;

            case HotPowerId.ToolsOfTheTradePower:
                if (ownerSlot == 0 && s.HandCount > 0)
                {
                    // 0.113.1 ToolsOfTheTradePower.cs:18-37: extra draw happens in ModifyHandDraw (fromHandDraw: true);
                    // AfterPlayerTurnStart only selects cards from Hand to discard.
                    int handCount = s.HandCount;
                    Span<ushort> pickedUids = stackalloc ushort[handCount <= amount
                        ? Math.Min(handCount, HotLimits.MaxCards)
                        : HotLimits.MaxChoicesPerAction];
                    int pickedCount = 0;
                    if (handCount <= amount)
                    {
                        // 0.113.1 ToolsOfTheTradePower.cs:31 & CardSelectCmd.cs:677-680: !RequireManualConfirmation && handCount <= amount bypasses selector in Hand pile order.
                        pickedCount = SnapshotPileCardUids(in s, HotPileKind.Hand, pickedUids);
                    }
                    else
                    {
                        int picks = Math.Min(amount, Math.Min(handCount, HotLimits.MaxChoicesPerAction));
                        scratch.BeginChoiceGroup(HotChoiceGroupFlags.Unordered | HotChoiceGroupFlags.MultiPick);
                        for (int k = 0; k < picks && pickedCount < s.HandCount; k++)
                        {
                            if (!RequestChoiceExcluding(ref s, scratch, HotChoiceKind.HandCard, 0, 1, 1, pickedUids[..pickedCount], out HotChoiceSelection sel))
                                return;
                            if (sel.CardUidOrIndex != 0)
                                pickedUids[pickedCount++] = sel.CardUidOrIndex;
                        }
                        scratch.EndChoiceGroup();
                        FlushDeferredPlayerSideTurnStartIfInSetup(ref s, ref cold, rules, scratch);
                        if (IsOverOrEnding(in s) || scratch.NeedsChoice)
                            return;
                    }
                    if (pickedCount > 0)
                        DiscardCardsAndDraw(ref s, ref cold, rules, scratch, pickedUids[..pickedCount], drawCount: 0);
                }
                break;

            case HotPowerId.TyrannyPower:
                if (ownerSlot == 0 && s.HandCount > 0)
                {
                    int handCount = s.HandCount;
                    Span<ushort> pickedUids = stackalloc ushort[handCount <= amount
                        ? Math.Min(handCount, HotLimits.MaxCards)
                        : HotLimits.MaxChoicesPerAction];
                    int pickedCount = 0;
                    if (handCount <= amount)
                    {
                        // 0.113.1 TyrannyPower.cs:36 & CardSelectCmd.cs:677-680: !RequireManualConfirmation && handCount <= amount bypasses selector in Hand pile order.
                        pickedCount = SnapshotPileCardUids(in s, HotPileKind.Hand, pickedUids);
                    }
                    else
                    {
                        int picks = Math.Min(amount, Math.Min(handCount, HotLimits.MaxChoicesPerAction));
                        scratch.BeginChoiceGroup(HotChoiceGroupFlags.Unordered | HotChoiceGroupFlags.MultiPick);
                        for (int k = 0; k < picks && pickedCount < s.HandCount; k++)
                        {
                            if (!RequestChoiceExcluding(ref s, scratch, HotChoiceKind.HandCard, 0, 1, 1, pickedUids[..pickedCount], out HotChoiceSelection sel))
                                return;
                            if (sel.CardUidOrIndex != 0)
                                pickedUids[pickedCount++] = sel.CardUidOrIndex;
                        }
                        scratch.EndChoiceGroup();
                        FlushDeferredPlayerSideTurnStartIfInSetup(ref s, ref cold, rules, scratch);
                        if (IsOverOrEnding(in s) || scratch.NeedsChoice)
                            return;
                    }
                    for (int i = 0; i < pickedCount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                    {
                        int idx = FindCardIndexByUid(in s, HotPileKind.Hand, pickedUids[i]);
                        if (idx >= 0)
                            ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                    }
                }
                break;

            case HotPowerId.ConductorsBatonPower:
                RemovePowerAt(ref s, powerIdx);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                break;

            case HotPowerId.GodByrdPower:
                if (ownerSlot == 0 && s.Powers[powerIdx].AmountOnTurnStart != 0)
                {
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: amount, isPoweredAttack: false);
                }
                break;
        }
    }

    private static void HandleBeforeHandDrawPower(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount)
    {
        if (ownerSlot != 0 || amount <= 0)
            return;

        switch (powerId)
        {
            case HotPowerId.InfiniteBladesPower:
            {
                ushort shivId = rules.GetCardDefId(HotCustomCardId.Shiv);
                if (shivId != 0)
                {
                    for (int k = 0; k < amount; k++)
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, shivId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                }
                break;
            }

            case HotPowerId.NightmarePower:
                TriggerNightmarePower(ref s, ref cold, rules, scratch, powerIdx, amount);
                break;

            case HotPowerId.ForegoneConclusionPower:
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
                if (s.DrawCount > 0)
                {
                    int drawCount = s.DrawCount;
                    Span<ushort> pickedUids = stackalloc ushort[drawCount <= amount
                        ? Math.Min(drawCount, HotLimits.MaxCards)
                        : HotLimits.MaxChoicesPerAction];
                    int pickedCount = 0;
                    if (drawCount <= amount)
                    {
                        // 0.113.1 ForegoneConclusionPower.cs:23 & CardSelectCmd.cs:310-313: !RequireManualConfirmation && drawCount <= amount bypasses selector in Draw pile order.
                        pickedCount = SnapshotPileCardUids(in s, HotPileKind.Draw, pickedUids);
                    }
                    else
                    {
                        int picks = Math.Min(amount, Math.Min(drawCount, HotLimits.MaxChoicesPerAction));
                        scratch.BeginChoiceGroup(HotChoiceGroupFlags.Unordered | HotChoiceGroupFlags.MultiPick);
                        for (int k = 0; k < picks && pickedCount < s.DrawCount; k++)
                        {
                            if (!RequestChoiceExcluding(ref s, scratch, HotChoiceKind.DrawPile, 0, 1, 1, pickedUids[..pickedCount], out HotChoiceSelection sel))
                                return;
                            if (sel.CardUidOrIndex != 0)
                                pickedUids[pickedCount++] = sel.CardUidOrIndex;
                        }
                        scratch.EndChoiceGroup();
                        FlushDeferredPlayerSideTurnStartIfInSetup(ref s, ref cold, rules, scratch);
                        if (IsOverOrEnding(in s) || scratch.NeedsChoice)
                            return;
                    }
                    for (int i = 0; i < pickedCount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; i++)
                    {
                        int idx = FindCardIndexByUid(in s, HotPileKind.Draw, pickedUids[i]);
                        if (idx >= 0)
                            MoveCardToHandOrDiscard(ref s, idx);
                    }
                }
                int liveIdx = FindPowerIndex(in s, 0, rules.GetPowerDefId(HotPowerId.ForegoneConclusionPower));
                if (liveIdx >= 0)
                {
                    RemovePowerAt(ref s, liveIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                }
                break;
            }

            case HotPowerId.CreativeAiPower:
                if (rules.CombatCharacterPowerPool.Length > 0)
                {
                    Span<ushort> picked = stackalloc ushort[1];
                    for (int k = 0; k < amount && !scratch.NeedsChoice && !IsOverOrEnding(in s); k++)
                    {
                        // 0.113.1 CreativeAiPower.cs:27: CardFactory.GetDistinctForCombat(..., 1, CombatCardGeneration)
                        // -> TakeRandom(1, rng) -> UnstableShuffle + Take(1).
                        if (HotPotions.TakeRandomDistinct(rules.CombatCharacterPowerPool, 1, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked) > 0)
                            AddGeneratedFixedCard(ref s, ref cold, rules, scratch, picked[0], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
                }
                break;

            case HotPowerId.HelloWorldPower:
                if (rules.UnlockedCharacterCardPool.Length > 0)
                {
                    // 0.113.1 HelloWorldPower.cs:25-28: GetDistinctForCombat(where Rarity == Common, AmountOnTurnStart, rng).
                    int genCount = s.Powers[powerIdx].AmountOnTurnStart > 0 ? s.Powers[powerIdx].AmountOnTurnStart : amount;
                    Span<ushort> commonPool = stackalloc ushort[Math.Min(rules.UnlockedCharacterCardPool.Length, 256)];
                    int commonCount = 0;
                    for (int i = 0; i < rules.UnlockedCharacterCardPool.Length && commonCount < commonPool.Length; i++)
                    {
                        ushort cid = rules.UnlockedCharacterCardPool[i];
                        if (cid > 0 && cid < rules.Cards.Length
                            && rules.Cards[cid].Rarity == CardRarity.Common
                            && IsCombatGeneratableCard(rules.Cards[cid]))
                        {
                            commonPool[commonCount++] = cid;
                        }
                    }
                    int pickCap = Math.Min(genCount, 16);
                    Span<ushort> picked = stackalloc ushort[pickCap];
                    int taken = HotPotions.TakeRandomDistinct(commonPool[..commonCount], pickCap, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked);
                    for (int k = 0; k < taken && !scratch.NeedsChoice && !IsOverOrEnding(in s); k++)
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, picked[k], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                }
                break;

            case HotPowerId.CallOfTheVoidPower:
                if (rules.UnlockedCharacterCardPool.Length > 0)
                {
                    // 0.113.1 CallOfTheVoidPower.cs:30-45: loop amount times calling GetDistinctForCombat(elig, 1, rng).
                    Span<ushort> elig = stackalloc ushort[Math.Min(rules.UnlockedCharacterCardPool.Length, 256)];
                    int eligCount = 0;
                    for (int i = 0; i < rules.UnlockedCharacterCardPool.Length && eligCount < elig.Length; i++)
                    {
                        ushort cid = rules.UnlockedCharacterCardPool[i];
                        if (cid > 0 && cid < rules.Cards.Length && IsCombatGeneratableCard(rules.Cards[cid]))
                            elig[eligCount++] = cid;
                    }
                    if (eligCount > 0)
                    {
                        Span<ushort> picked = stackalloc ushort[1];
                        for (int k = 0; k < amount && !scratch.NeedsChoice && !IsOverOrEnding(in s); k++)
                        {
                            if (HotPotions.TakeRandomDistinct(elig[..eligCount], 1, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked) > 0)
                            {
                                int addedIdx = AddGeneratedFixedCard(ref s, ref cold, rules, scratch, picked[0], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                                if (addedIdx >= 0)
                                    ApplyCardKeywordOutsideUpgrade(ref s.Cards[addedIdx], HotCardKeywordMask.Ethereal);
                            }
                        }
                    }
                }
                break;

            case HotPowerId.SpectrumShiftPower:
                if (rules.CombatColorlessPool.Length > 0)
                {
                    // 0.113.1 SpectrumShiftPower.cs:25: GetDistinctForCombat(colorless, amount, rng).
                    int pickCap = Math.Min(amount, 16);
                    Span<ushort> picked = stackalloc ushort[pickCap];
                    int taken = HotPotions.TakeRandomDistinct(rules.CombatColorlessPool, pickCap, ref s.Rng[(int)HotRngStream.CombatCardGeneration], picked);
                    for (int k = 0; k < taken && !scratch.NeedsChoice && !IsOverOrEnding(in s); k++)
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, picked[k], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                }
                break;

            case HotPowerId.SentryModePower:
            {
                ushort sweeperId = rules.GetCardDefId(HotCustomCardId.SweepingGaze);
                if (sweeperId != 0)
                {
                    for (int k = 0; k < amount; k++)
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, sweeperId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                }
                break;
            }
        }
    }

    private static int SnapshotPileCardUids(in CombatHotState s, HotPileKind pile, Span<ushort> dst)
    {
        int start = HotPiles.ZoneStart(in s, pile);
        int count = HotPiles.ZoneCount(in s, pile);
        int written = 0;
        for (int i = 0; i < count && written < dst.Length; i++)
        {
            ushort uid = s.Cards[start + i].CardUid;
            if (uid != 0)
                dst[written++] = uid;
        }
        return written;
    }

    /// <summary>
    /// Retires temporary stat powers whose <see cref="HotPowerDef.HookMask"/> does not include
    /// <see cref="HotHookMask.AfterTurnEnd"/> at the end of their owner's side turn.
    /// </summary>
    internal static void RetireSideTurnEndTemporaryStats(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotCombatSide side)
    {
        int i = 0;
        int guard = 0;
        int maxRetirements = HotLimits.MaxPowers * 2;
        while (i < s.PowerCount && !scratch.NeedsChoice && guard < maxRetirements)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.Amount == 0 || !IsSlotOnCurrentSide(side, p.OwnerSlot) || p.PowerDefId >= rules.Powers.Length)
            {
                i++;
                continue;
            }

            if ((rules.Powers[p.PowerDefId].HookMask & HotHookMask.AfterTurnEnd) != 0)
            {
                i++;
                continue;
            }

            HotPowerId pid = rules.Powers[p.PowerDefId].PowerId;
            if (TryGetTemporaryStatInfo(pid, out HotPowerId baseStat, out int retireSign))
            {
                int amt = p.Amount;
                byte owner = p.OwnerSlot;
                RetireTempStat(ref s, ref cold, rules, scratch, i, owner, baseStat, retireSign * amt);
                // Reset scan to 0 because RetireTempStat removes the temp power at i AND may also remove
                // its base stat power at an index < i (shifting subsequent powers left by 2).
                guard++;
                i = 0;
                continue;
            }
            i++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetTemporaryStatInfo(HotPowerId tempPowerId, out HotPowerId baseStatPowerId, out int retireSign)
    {
        switch (tempPowerId)
        {
            case HotPowerId.FlexPotionPower:
            case HotPowerId.SetupStrikePower:
            case HotPowerId.FeedingFrenzyPower:
            case HotPowerId.ReptileTrinketPower:
            case HotPowerId.CoordinatePower:
                baseStatPowerId = HotPowerId.StrengthPower;
                retireSign = -1; // applied +amt, retires -amt
                return true;

            case HotPowerId.ShacklingPotionPower:
            case HotPowerId.PiercingWailPower:
            case HotPowerId.DarkShacklesPower:
            case HotPowerId.EnfeeblingTouchPower:
            case HotPowerId.CrushUnderPower:
            case HotPowerId.DyingStarPower:
            case HotPowerId.ManglePower:
            case HotPowerId.MonarchsGazeStrengthDownPower:
                baseStatPowerId = HotPowerId.StrengthPower;
                retireSign = +1; // applied -amt, retires +amt
                return true;

            case HotPowerId.SpeedPotionPower:
            case HotPowerId.AnticipatePower:
            case HotPowerId.HelicalDartPower:
            case HotPowerId.FadePower:
                baseStatPowerId = HotPowerId.DexterityPower;
                retireSign = -1;
                return true;

            case HotPowerId.FocusedStrikePower:
            case HotPowerId.HotfixPower:
            case HotPowerId.SynchronizePower:
                baseStatPowerId = HotPowerId.FocusPower;
                retireSign = -1;
                return true;

            case HotPowerId.HyperbeamFocusDownPower:
                baseStatPowerId = HotPowerId.FocusPower;
                retireSign = +1;
                return true;

            default:
                baseStatPowerId = HotPowerId.None;
                retireSign = 0;
                return false;
        }
    }

    private static void HandleAfterDeathPower(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        // Enemy-side AfterDeath powers (Ravenous, CrabRage, Explosive on normal death, Stock, Adaptable,
        // Illusion, Infested, Reattach, Surprise, Dampen, Heist, Swipe, etc.) are executed inside
        // HotMonsters.HandleEnemyDeathEffect so they never double-trigger.
        switch (powerId)
        {
            case HotPowerId.ExplosivePower:
                // Normal enemy deaths execute ExplosivePower inside HotMonsters.HandleEnemyDeathEffect (L497-510).
                // WaterfallGiant SteamEruptionPower survival returns early at HotMonsters.cs:480 (setting Alive=true,
                // SteamPhase=1) before reaching L497; vanilla CreatureCmd.cs:627 still fires Hook.AfterDeath
                // (wasRemovalPrevented: false) before RemoveAllPowersAfterDeath, so deal the blast here only when
                // the dying enemy was kept alive by that survival branch (preventing double-triggers on normal deaths).
                if (ownerSlot == args.TargetSlot
                    && ownerSlot >= 3
                    && (uint)(ownerSlot - 3) < (uint)s.EnemyCount
                    && (s.Enemies[ownerSlot - 3].Vital.Flags & HotCreatureFlags.Alive) != 0
                    && amount > 0
                    && (s.Player.Flags & HotCreatureFlags.Alive) != 0
                    && s.Player.CurrentHp > 0)
                {
                    DealDamage(
                        ref s,
                        ref cold,
                        rules,
                        scratch,
                        dealerSlot: 0xFF,
                        targetSlot: 0,
                        baseDamage: amount,
                        isPoweredAttack: false,
                        isUnblockable: false);
                }
                break;

            case HotPowerId.HexPower:
                if (ownerSlot == 0 && args.TargetSlot == s.Powers[powerIdx].ApplierSlot)
                {
                    RemovePowerAt(ref s, powerIdx);
                    ClearHexedAffliction(ref s, rules);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                }
                break;

            case HotPowerId.ShrinkPower:
                if (args.TargetSlot == s.Powers[powerIdx].ApplierSlot && args.TargetSlot >= 3)
                {
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                }
                break;

            case HotPowerId.SurroundedPower:
                if (ownerSlot == 0 && args.TargetSlot >= 3)
                {
                    // SurroundedPower.cs:90-100: when an enemy dies, if all remaining living enemies are
                    // on one side, update Facing toward them (Right = 0, Left = 1).
                    bool anyLiving = false;
                    bool allLeft = true;
                    bool allRight = true;
                    int rosterCount = s.RosterCount;
                    for (int r = 0; r < rosterCount; r++)
                    {
                        byte eSlot = s.ActiveEnemyRoster[r];
                        if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                            continue;
                        anyLiving = true;
                        byte cSlot = (byte)(3 + eSlot);
                        if (GetPowerAmountById(in s, rules, cSlot, HotPowerId.BackAttackLeftPower) <= 0)
                            allLeft = false;
                        if (GetPowerAmountById(in s, rules, cSlot, HotPowerId.BackAttackRightPower) <= 0)
                            allRight = false;
                    }
                    if (anyLiving)
                    {
                        if (allLeft)
                            s.Powers[powerIdx].Aux0 = 1;
                        else if (allRight)
                            s.Powers[powerIdx].Aux0 = 0;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Exact STS2 0.113.1 Poison tick matching <c>CorePowerSupport.TriggerPoison</c>:
    /// checks opponent <c>AccelerantPower</c>, deals unblockable unpowered damage and decrements
    /// <c>1 + accelerant</c> times (stopping immediately on death).
    /// </summary>
    internal static void TriggerPoison(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot)
    {
        if (!IsCreatureSlotValid(in s, targetSlot) || (GetCreatureRef(ref s, targetSlot).Flags & HotCreatureFlags.Alive) == 0)
            return;

        ushort poisonDefId = rules.GetPowerDefId(HotPowerId.PoisonPower);
        if (poisonDefId == 0)
            return;

        int poison = GetPowerAmount(in s, targetSlot, poisonDefId);
        if (poison <= 0)
            return;

        int accelerant = 0;
        ushort accelDefId = rules.GetPowerDefId(HotPowerId.AccelerantPower);
        if (accelDefId != 0)
        {
            if (targetSlot >= 3 && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
            {
                accelerant = GetPowerAmount(in s, ownerSlot: 0, accelDefId);
            }
            else if (targetSlot <= 2)
            {
                for (int r = 0; r < s.RosterCount; r++)
                {
                    byte eSlot = s.ActiveEnemyRoster[r];
                    if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                        accelerant += GetPowerAmount(in s, (byte)(3 + eSlot), accelDefId);
                }
            }
        }

        int triggerCount = Math.Min(poison, 1 + Math.Max(0, accelerant));
        for (int t = 0; t < triggerCount; t++)
        {
            int cur = GetPowerAmount(in s, targetSlot, poisonDefId);
            if (cur <= 0)
                break;

            DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0xFF, targetSlot: targetSlot, baseDamage: cur, isPoweredAttack: false, isUnblockable: true);
            if (scratch.NeedsChoice)
                return;
            if ((GetCreatureRef(ref s, targetSlot).Flags & HotCreatureFlags.Alive) == 0)
                break;

            int postCur = GetPowerAmount(in s, targetSlot, poisonDefId);
            if (postCur <= 0)
                break;
            SetPowerAmount(ref s, rules, targetSlot, poisonDefId, postCur - 1);
        }
    }

    private static void TriggerNightmarePower(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int powerIdx,
        int amount)
    {
        ushort targetUid = s.Powers[powerIdx].AuxCardUid;
        int aux0 = s.Powers[powerIdx].Aux0;
        RemovePowerAt(ref s, powerIdx);
        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);

        if (targetUid == 0 && aux0 >= 0 && cold?.DetachedCards is { Length: > 0 } detached && aux0 < detached.Length)
        {
            HotCard template = detached[aux0];
            for (int k = 0; k < amount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; k++)
            {
                HotCard copy = template;
                copy.CardUid = s.NextCardUid++;
                copy.Flags &= ~(HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay | HotCardFlags.IsDupe | HotCardFlags.FetchPlayedThisTurn);
                copy.Flags |= HotCardFlags.IsClone;
                ClearPerInstanceHistoryOnClone(ref copy, rules);
                if ((copy.Flags & HotCardFlags.HasCostOverflow) != 0 && template.CardUid != 0)
                    HotCostModifiers.CloneOverflow(ref cold, template.CardUid, copy.CardUid);
                AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in copy, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
            }
            return;
        }

        if (targetUid != 0)
        {
            int srcIdx = FindCardIndexByUidAnyPile(in s, targetUid);
            if (srcIdx >= 0)
            {
                HotCard template = s.Cards[srcIdx];
                for (int k = 0; k < amount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; k++)
                {
                    HotCard copy = template;
                    copy.CardUid = s.NextCardUid++;
                    copy.Flags &= ~(HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay | HotCardFlags.IsDupe | HotCardFlags.FetchPlayedThisTurn);
                    copy.Flags |= HotCardFlags.IsClone;
                    ClearPerInstanceHistoryOnClone(ref copy, rules);
                    if ((copy.Flags & HotCardFlags.HasCostOverflow) != 0 && template.CardUid != 0)
                        HotCostModifiers.CloneOverflow(ref cold, template.CardUid, copy.CardUid);
                    AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in copy, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
                }
                return;
            }
        }

        if (aux0 > 0 && aux0 < rules.Cards.Length && targetUid != 0)
        {
            for (int k = 0; k < amount && !IsOverOrEnding(in s) && !scratch.NeedsChoice; k++)
                AddGeneratedFixedCard(ref s, ref cold, rules, scratch, (ushort)aux0, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
        }
    }

    private static void TransformCardToRandomInCombat(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int cardIdx)
    {
        if ((uint)cardIdx >= (uint)s.TotalCardCount)
            return;
        HotCard orig = s.Cards[cardIdx];
        if (orig.CardDefId == 0 || orig.CardDefId >= rules.Cards.Length)
            return;

        HotCardDef origDef = rules.Cards[orig.CardDefId];
        // 0.113.1 CardFactory.GetDefaultTransformationOptions (CardFactory.cs:171-182).
        ushort[] pool = rules.GetUnlockedTransformPool(origDef);

        if (pool.Length == 0)
            return;

        Span<ushort> candidates = pool.Length <= 512
            ? stackalloc ushort[pool.Length]
            : new ushort[pool.Length];
        int candCount = 0;
        bool isCurseOrSpecial = origDef.Rarity is CardRarity.Curse or CardRarity.Status;

        for (int i = 0; i < pool.Length; i++)
        {
            ushort id = pool[i];
            if (id == 0 || id >= rules.Cards.Length)
                continue;
            HotCardDef d = rules.Cards[id];
            if (d.CanonicalDefId == origDef.CanonicalDefId || !d.CanBeGeneratedInCombat)
                continue;
            if (!isCurseOrSpecial && d.Rarity is not (CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare))
                continue;
            candidates[candCount++] = id;
        }

        if (candCount == 0)
            return;

        ushort replacementId = candidates[HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardGeneration], candCount)];
        TransformCardInPlace(ref s, ref cold, rules, scratch, cardIdx, replacementId, upgraded: false);
    }

    internal static void RetireTempStat(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int tempPowerIdx,
        byte ownerSlot,
        HotPowerId statPowerId,
        int statDelta)
    {
        RemovePowerAt(ref s, tempPowerIdx);
        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
        if (statDelta != 0)
            ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, statPowerId, statDelta);
    }

    private static void TryRetireRemovedSnapshotTempStat(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte ownerSlot,
        ushort powerDefId,
        int snapAmount,
        ref HotHookArgs args)
    {
        if (snapAmount <= 0 || !IsSlotOnCurrentSide(args.Side, ownerSlot))
            return;
        if (IsOverOrEnding(in s) || IsCombatEnding(in s, rules))
            return;
        if (!IsCreatureSlotValid(in s, ownerSlot) || (GetCreatureRef(ref s, ownerSlot).Flags & HotCreatureFlags.Alive) == 0)
            return;
        if (!TryGetTemporaryStatInfo(rules.Powers[powerDefId].PowerId, out HotPowerId baseStatId, out int retireSign))
            return;

        // 0.113.1 Hook.AfterSideTurnEnd (Hook.cs:1329, CombatState.cs:429) snapshots creature.Powers up front,
        // and PowerModel.RemoveInternal (PowerModel.cs:580-585) leaves Owner & Amount intact.
        // If an earlier AfterSideTurnEnd listener (e.g. DemisePower -> PlowPower on CeremonialBeast) removed
        // a TemporaryStrength/Dexterity/Focus power while its owner remained alive, the snapshotted instance
        // still applies (-Sign * Amount) of its base stat without re-removing itself.
        ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, baseStatId, retireSign * snapAmount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DecrementPowerAt(ref CombatHotState s, HotRules rules, int powerIdx)
    {
        if ((uint)powerIdx >= (uint)s.PowerCount)
            return;
        if (s.Powers[powerIdx].Amount > 1)
        {
            s.Powers[powerIdx].Amount--;
        }
        else
        {
            RemovePowerAt(ref s, powerIdx);
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSlotOnCurrentSide(HotCombatSide side, byte ownerSlot)
        => side == HotCombatSide.Player ? ownerSlot <= 2 : ownerSlot >= 3;
}
