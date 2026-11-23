using System.Numerics;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    private const byte ResultPileDrawTop = 0xFE;

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
    {
        ulong bitMask = (ulong)hook;
        if (bitMask == 0)
            return;

        bool hasMask = (s.ActiveHookMask & bitMask) != 0;
        if (!hasMask && rules.Dominion == HotDominionId.None)
            return;

        int bit = BitOperations.TrailingZeroCount(bitMask);

        // 1. Active Relics participating in this hook bit (O(active matching relics))
        if (hasMask && (uint)bit < (uint)rules.RelicHooksByBit.Length)
        {
            byte[] relicIndices = rules.RelicHooksByBit[bit];
            for (int r = 0; r < relicIndices.Length; r++)
            {
                HotRelics.DispatchRelicHook(ref s, ref cold, rules, scratch, hook, relicIndices[r], ref args);
                if (scratch.NeedsChoice)
                    return;
            }
        }

        // 2. Active Powers participating in this hook bit
        if (hasMask && s.PowerCount > 0)
        {
            if (IsReadOnlyModifierHook(hook))
            {
                int powerCount = s.PowerCount;
                for (int i = 0; i < powerCount; i++)
                {
                    ref readonly HotPower p = ref s.Powers[i];
                    if (p.Amount == 0 || p.PowerDefId >= rules.Powers.Length)
                        continue;

                    HotPowerDef def = rules.Powers[p.PowerDefId];
                    if (((ulong)def.HookMask & bitMask) == 0)
                        continue;

                    DispatchSinglePowerHook(ref s, ref cold, rules, scratch, hook, i, def, ref args);
                    if (scratch.NeedsChoice)
                        return;
                }
            }
            else
            {
                // Snapshot only matching (OwnerSlot, PowerDefId, Occurrence) so mid-hook power
                // additions/removals never skip or double-fire listeners.
                Span<byte> snapOwners = stackalloc byte[HotLimits.MaxPowers];
                Span<ushort> snapDefIds = stackalloc ushort[HotLimits.MaxPowers];
                Span<byte> snapOcc = stackalloc byte[HotLimits.MaxPowers];
                int snapCount = 0;

                int powerCount = s.PowerCount;
                for (int i = 0; i < powerCount; i++)
                {
                    ref readonly HotPower p = ref s.Powers[i];
                    if (p.Amount == 0 || p.PowerDefId >= rules.Powers.Length)
                        continue;

                    if (((ulong)rules.Powers[p.PowerDefId].HookMask & bitMask) == 0)
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
                    snapCount++;
                }

                for (int n = 0; n < snapCount; n++)
                {
                    int liveIdx = FindPowerOccurrenceIndex(in s, snapOwners[n], snapDefIds[n], snapOcc[n]);
                    if (liveIdx < 0 || s.Powers[liveIdx].Amount == 0)
                        continue;

                    HotPowerDef def = rules.Powers[snapDefIds[n]];
                    DispatchSinglePowerHook(ref s, ref cold, rules, scratch, hook, liveIdx, def, ref args);
                    if (scratch.NeedsChoice)
                        return;
                }
            }
        }

        // 3. Active Dread Dominion
        if (rules.Dominion != HotDominionId.None)
            HotFoolTarot.TryDispatchDominionHook(ref s, ref cold, rules, scratch, hook, ref args);
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
            or HotHookMask.ShouldClearBlock
            or HotHookMask.ShouldTakeExtraTurn;

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
        else if (def.Domain == HotPowerDomain.Fool)
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
                HandleModifyBlockMultiplicative(in s, powerIdx, powerId, ownerSlot, amount, ref args);
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
                    s.Enemies[ownerSlot - 3].Flags |= HotEnemyFlags.SkipNextMove | HotEnemyFlags.Stunned;
                }
                break;

            case HotHookMask.BeforeHandDraw:
                HandleBeforeHandDrawPower(ref s, ref cold, rules, scratch, powerIdx, powerId, ownerSlot, amount);
                break;

            case HotHookMask.BeforeAttack:
                HandleBeforeAttack(ref s, powerIdx, powerId, ownerSlot, amount, ref args);
                break;

            case HotHookMask.AfterAttack:
                HandleAfterAttack(ref s, rules, powerIdx, powerId, ownerSlot, ref args);
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
                break;

            case HotHookMask.ShouldClearBlock:
                if (args.TargetSlot == ownerSlot && amount > 0
                    && powerId is HotPowerId.BarricadePower or HotPowerId.BlurPower)
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
                    for (int pick = 0; pick < amount && s.DrawCount > 0; pick++)
                    {
                        if (!RequestChoice(ref s, scratch, HotChoiceKind.DrawPile, 0, 1, 1, out HotChoiceSelection sel))
                            return;
                        int idx = FindCardIndexByUid(in s, HotPileKind.Draw, sel.CardUidOrIndex);
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
                if (args.ActorSlot == ownerSlot && amount > 0)
                    args.DecimalMultiplier *= 2m;
                break;

            case HotPowerId.GigantificationPower:
                if (args.ActorSlot == ownerSlot && amount > 0 && args.CardIdx >= 0)
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

            case HotPowerId.SlowPower:
                if (args.TargetSlot == ownerSlot)
                {
                    int slowCount = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : amount;
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
                    args.DecimalMultiplier *= 0.5m;
                break;

            case HotPowerId.SurroundedPower:
                if (args.TargetSlot == ownerSlot && args.ActorSlot != 0xFF)
                {
                    int facing = s.Powers[powerIdx].Aux0; // 1 = Left, 2 = Right
                    if ((facing == 2 && GetPowerAmountById(in s, rules, args.ActorSlot, HotPowerId.BackAttackLeftPower) > 0)
                        || (facing == 1 && GetPowerAmountById(in s, rules, args.ActorSlot, HotPowerId.BackAttackRightPower) > 0))
                    {
                        args.DecimalMultiplier *= 1.5m;
                    }
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
                if (args.DecimalValue > 1m)
                    args.DecimalValue = 1m;
                break;

            case HotPowerId.SlipperyPower:
                if (args.DecimalValue > 1m)
                    args.DecimalValue = 1m;
                break;

            case HotPowerId.HardenedShellPower:
            {
                int remainingCap = Math.Max(0, amount - s.Powers[powerIdx].Aux0);
                if (args.DecimalValue > remainingCap)
                    args.DecimalValue = remainingCap;
                break;
            }

            case HotPowerId.BufferPower:
                if (!args.BoolFlag) // Only post-block HP loss (BoolFlag == false means post-block phase)
                {
                    args.DecimalValue = 0m;
                    DecrementPowerAt(ref s, rules, powerIdx);
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
        int powerIdx,
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
                if (args.IsCardBlock)
                    args.DecimalMultiplier *= 0.75m;
                break;

            case HotPowerId.NoBlockPower:
                if (args.IsCardBlock)
                    args.DecimalMultiplier = 0m;
                break;

            case HotPowerId.ShadowmeldPower:
                args.DecimalMultiplier *= amount >= 96
                    ? decimal.MaxValue
                    : (decimal)Math.Pow(2.0, amount);
                break;

            case HotPowerId.UnmovablePower:
                if (args.IsCardBlock && ownerSlot == 0 && s.Powers[powerIdx].Aux0 < amount)
                    args.DecimalMultiplier *= 2m;
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
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, ownerSlot, HotPowerId.StrengthPower, amount);
                break;

            case HotPowerId.CurlUpPower:
                if (args.IsPoweredAttack && args.CardIdx >= 0 && s.Powers[powerIdx].Aux0 == 0)
                    s.Powers[powerIdx].Aux0 = 1;
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
                            s.Enemies[ownerSlot - 3].Flags |= HotEnemyFlags.SkipNextMove | HotEnemyFlags.Stunned;
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
                    s.Enemies[ownerSlot - 3].Flags |= HotEnemyFlags.Awake | HotEnemyFlags.SkipNextMove;
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
                        RemovePowerAt(ref s, powerIdx);
                        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                        s.Enemies[ownerSlot - 3].Flags |= HotEnemyFlags.Awake | HotEnemyFlags.SkipNextMove;
                    }
                }
                break;

            case HotPowerId.BurrowedPower:
                if (args.WasBlockBroken && ownerSlot >= 3)
                {
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    s.Enemies[ownerSlot - 3].Vital.Block = 0;
                    s.Enemies[ownerSlot - 3].Flags |= HotEnemyFlags.SkipNextMove | HotEnemyFlags.Stunned;
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
        }
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
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        int amount,
        ref HotHookArgs args)
    {
        if (!args.IsPoweredAttack || args.ActorSlot != ownerSlot || amount <= 0)
            return;

        if (powerId == HotPowerId.VigorPower && s.Powers[powerIdx].Aux0 == 0)
            s.Powers[powerIdx].Aux0 = amount;
        else if (powerId == HotPowerId.GigantificationPower && args.CardIdx >= 0 && s.Powers[powerIdx].Aux0 == 0)
            s.Powers[powerIdx].Aux0 = 1;
    }

    private static void HandleAfterAttack(
        ref CombatHotState s,
        HotRules rules,
        int powerIdx,
        HotPowerId powerId,
        byte ownerSlot,
        ref HotHookArgs args)
    {
        if (args.ActorSlot != ownerSlot)
            return;

        if (powerId == HotPowerId.VigorPower && s.Powers[powerIdx].Aux0 > 0)
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
        else if (powerId == HotPowerId.GigantificationPower && s.Powers[powerIdx].Aux0 > 0)
        {
            s.Powers[powerIdx].Aux0 = 0;
            DecrementPowerAt(ref s, rules, powerIdx);
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

            case HotPowerId.FreeCardPower:
                if (ownerSlot == 0 && amount > 0)
                    DecrementPowerAt(ref s, rules, powerIdx);
                break;

            case HotPowerId.VeilpiercerPower:
                if (ownerSlot == 0 && amount > 0 && args.CardIdx >= 0
                    && (s.Cards[args.CardIdx].LocalKeywordsMask & HotCardKeywordMask.Ethereal) != 0)
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
                    && (s.Cards[args.CardIdx].LocalKeywordsMask & HotCardKeywordMask.Ethereal) != 0)
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
                    LatchCardPlayAmount(ref s.Powers[powerIdx], s.PlayCount, amount);
                break;

            case HotPowerId.StormPower:
            case HotPowerId.SubroutinePower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Power && amount > 0)
                    LatchCardPlayAmount(ref s.Powers[powerIdx], s.PlayCount, amount);
                break;

            case HotPowerId.CalamityPower:
                if (ownerSlot == 0 && cardDef.Type == CardType.Attack && amount > 0)
                    LatchCardPlayAmount(ref s.Powers[powerIdx], s.PlayCount, amount);
                break;

            case HotPowerId.StranglePower:
                if (args.ActorSlot == 0 && ownerSlot >= 3 && amount > 0)
                    LatchCardPlayAmount(ref s.Powers[powerIdx], s.PlayCount, amount);
                break;

            case HotPowerId.MonologuePower:
                if (ownerSlot == 0)
                    s.Powers[powerIdx].Flags |= HotPowerFlags.BoolAux;
                break;

            case HotPowerId.SurroundedPower:
                if (ownerSlot == 0 && args.TargetSlot != 0xFF)
                {
                    if (GetPowerAmountById(in s, rules, args.TargetSlot, HotPowerId.BackAttackLeftPower) > 0)
                        s.Powers[powerIdx].Aux0 = 1;
                    else if (GetPowerAmountById(in s, rules, args.TargetSlot, HotPowerId.BackAttackRightPower) > 0)
                        s.Powers[powerIdx].Aux0 = 2;
                }
                break;
        }
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
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx], s.PlayCount);
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
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx], s.PlayCount);
                if (ownerSlot == 0 && cardDef.Type == CardType.Power && snapAmt > 0)
                {
                    for (int k = 0; k < snapAmt; k++)
                        ChannelOrb(ref s, ref cold, rules, scratch, HotOrbId.Lightning);
                }
                break;
            }

            case HotPowerId.SubroutinePower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx], s.PlayCount);
                if (ownerSlot == 0 && cardDef.Type == CardType.Power && snapAmt > 0)
                    GainEnergy(ref s, rules, snapAmt);
                break;
            }

            case HotPowerId.CalamityPower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx], s.PlayCount);
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

            case HotPowerId.CurlUpPower:
                if (s.Powers[powerIdx].Aux0 != 0 && amount > 0)
                {
                    s.Powers[powerIdx].Aux0 = 0;
                    GainBlock(ref s, ref cold, rules, scratch, targetSlot: ownerSlot, baseAmount: amount, isCardBlock: false);
                    int liveIdx = FindPowerIndex(in s, ownerSlot, rules.GetPowerDefId(HotPowerId.CurlUpPower));
                    if (liveIdx >= 0)
                    {
                        RemovePowerAt(ref s, liveIdx);
                        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    }
                }
                break;

            case HotPowerId.SlowPower:
                s.Powers[powerIdx].Aux0++;
                break;

            case HotPowerId.PanachePower:
                if (ownerSlot == 0 && amount > 0)
                {
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
                    s.Cards[args.CardIdx].LocalKeywordsMask |= HotCardKeywordMask.Sly;
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
                        clone.Flags &= ~(HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay);
                        clone.Flags |= HotCardFlags.IsClone;
                        AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in clone, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
                    }
                }
                break;

            case HotPowerId.GravityPower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx], s.PlayCount);
                if (ownerSlot == 0 && snapAmt > 0)
                    DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: snapAmt, isPoweredAttack: false);
                break;
            }

            case HotPowerId.SerpentFormPower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx], s.PlayCount);
                if (ownerSlot == 0 && snapAmt > 0)
                    DamageRandomEnemy(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: snapAmt, isPoweredAttack: false);
                break;
            }

            case HotPowerId.StranglePower:
            {
                int snapAmt = ConsumeCardPlayAmount(ref s.Powers[powerIdx], s.PlayCount);
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
                    int left = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : 3;
                    left--;
                    if (left <= 0)
                    {
                        s.Powers[powerIdx].Aux0 = 3;
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
                if (args.ActorSlot == 0 && ownerSlot >= 3)
                {
                    s.Powers[powerIdx].Aux0++;
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, 0, HotPowerId.StrengthPower, -1);
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, 0, HotPowerId.DexterityPower, -1);
                }
                break;

            case HotPowerId.MonologuePower:
                if (ownerSlot == 0 && (s.Powers[powerIdx].Flags & HotPowerFlags.BoolAux) != 0)
                {
                    s.Powers[powerIdx].Flags &= ~HotPowerFlags.BoolAux;
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
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LatchCardPlayAmount(ref HotPower p, int playCount, int amount)
    {
        if (playCount <= 1)
            p.Aux0 = amount;
        else
            p.Aux1 = amount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ConsumeCardPlayAmount(ref HotPower p, int playCount)
    {
        if (playCount <= 1)
        {
            int val = p.Aux0;
            p.Aux0 = 0;
            return val;
        }
        else
        {
            int val = p.Aux1;
            p.Aux1 = 0;
            return val;
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
                if (ownerSlot == 0 && s.Counters[(int)HotCounterSlot.CardPlayStartsThisTurn] < amount)
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
                if (cardDef.Type == CardType.Skill && args.ResultPile == (byte)HotPileKind.Discard)
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
                    args.ResultPile = (byte)HotPileKind.Hand;
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
        if (ownerSlot != 0 || amount <= 0 || args.CardDefId == 0 || args.CardDefId >= rules.Cards.Length)
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

            case HotPowerId.FreeCardPower:
                args.IntValue = 0;
                break;

            case HotPowerId.VeilpiercerPower:
                if (args.CardIdx >= 0 && (s.Cards[args.CardIdx].LocalKeywordsMask & HotCardKeywordMask.Ethereal) != 0)
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
            case HotPowerId.ClarityPower:
            case HotPowerId.DemesnePower:
                args.HandDrawCount += amount;
                break;
            case HotPowerId.WasteAwayPower:
                args.HandDrawCount = Math.Max(0, args.HandDrawCount - amount);
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
                if (s.Cards[args.CardIdx].BaseEnergyCost >= 0 && (s.Cards[args.CardIdx].Flags & HotCardFlags.CostsX) == 0)
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
                if ((s.Cards[args.CardIdx].LocalKeywordsMask & HotCardKeywordMask.Ethereal) != 0 && amount > 0)
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
                    AutoPlayCard(ref s, ref cold, rules, scratch, args.CardIdx);
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
                        card.AfflAmt = 1;
                    }
                    card.LocalKeywordsMask |= HotCardKeywordMask.Ethereal;
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
                if (args.Side == HotCombatSide.Enemy)
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
                            s.Cards[c].AfflId = 0;
                    }
                    s.Powers[powerIdx].Flags &= ~HotPowerFlags.BoolAux;
                }
                break;
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
        bool ownerParticipates = IsSlotOnCurrentSide(args.Side, ownerSlot);

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
                s.Powers[powerIdx].Aux0 = 0;
                if (cardsPlayed > 0)
                {
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, 0, HotPowerId.StrengthPower, cardsPlayed);
                    ApplyPowerById(ref s, ref cold, rules, scratch, ownerSlot, 0, HotPowerId.DexterityPower, cardsPlayed);
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
            case HotPowerId.SlumberPower:
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

            case HotPowerId.RegenPower:
                if (args.Side == HotCombatSide.Player)
                    DecrementPowerAt(ref s, rules, powerIdx);
                break;

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
            case HotPowerId.TangledPower:
            case HotPowerId.RingingPower:
                RemovePowerAt(ref s, powerIdx);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                break;

            // Duration decrements
            case HotPowerId.DoubleDamagePower:
            case HotPowerId.RetainHandPower:
            case HotPowerId.DebilitatePower:
            case HotPowerId.ShrinkPower:
            case HotPowerId.HatchPower:
                DecrementPowerAt(ref s, rules, powerIdx);
                break;

            case HotPowerId.ColossusPower:
                if (args.Side == HotCombatSide.Enemy)
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
        if (!IsSlotOnCurrentSide(args.Side, ownerSlot))
            return;

        switch (powerId)
        {
            case HotPowerId.HardenedShellPower:
                s.Powers[powerIdx].Aux0 = 0;
                break;

            case HotPowerId.SlothPower:
            case HotPowerId.VoidFormPower:
                s.Powers[powerIdx].Aux0 = 0;
                break;

            case HotPowerId.AggressionPower:
                if (ownerSlot == 0 && amount > 0 && s.DiscardCount > 0)
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
                    if (atkCount > 0)
                    {
                        HotRng.UnstableShuffle(attacks[..atkCount], ref s.Rng[(int)HotRngStream.CombatCardSelection]);
                        int take = Math.Min(amount, atkCount);
                        for (int k = 0; k < take; k++)
                        {
                            int liveIdx = FindCardIndexByUid(in s, HotPileKind.Discard, (ushort)attacks[k]);
                            if (liveIdx >= 0)
                            {
                                int movedIdx = MoveCardToHandOrDiscard(ref s, liveIdx);
                                if (movedIdx >= 0)
                                    UpgradeCard(ref s, rules, movedIdx);
                            }
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
                    DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: amount, isPoweredAttack: false);
                    int inc = s.Powers[powerIdx].Aux0 > 0 ? s.Powers[powerIdx].Aux0 : 5;
                    int liveIdx = FindPowerIndex(in s, 0, rules.GetPowerDefId(HotPowerId.RollingBoulderPower));
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
                {
                    ushort targetUid = s.Powers[powerIdx].AuxCardUid;
                    ushort targetDefId = (ushort)s.Powers[powerIdx].Aux0;
                    RemovePowerAt(ref s, powerIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                    int srcIdx = FindCardIndexByUidAnyPile(in s, targetUid);
                    if (srcIdx >= 0)
                    {
                        HotCard template = s.Cards[srcIdx];
                        for (int k = 0; k < amount; k++)
                        {
                            HotCard copy = template;
                            copy.CardUid = s.NextCardUid++;
                            copy.Flags &= ~(HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay);
                            copy.Flags |= HotCardFlags.IsClone;
                            AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in copy, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
                        }
                    }
                    else if (targetDefId != 0)
                    {
                        for (int k = 0; k < amount; k++)
                            AddGeneratedFixedCard(ref s, ref cold, rules, scratch, targetDefId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
                }
                break;

            case HotPowerId.ToolsOfTheTradePower:
                if (ownerSlot == 0)
                {
                    DrawCards(ref s, ref cold, rules, scratch, amount, fromHandDraw: false);
                    for (int k = 0; k < amount && s.HandCount > 0; k++)
                    {
                        if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, 0, 1, 1, out HotChoiceSelection sel))
                            return;
                        int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                        if (idx >= 0)
                            DiscardCard(ref s, ref cold, rules, scratch, idx);
                    }
                }
                break;

            case HotPowerId.TyrannyPower:
                if (ownerSlot == 0)
                {
                    for (int k = 0; k < amount && s.HandCount > 0; k++)
                    {
                        if (!RequestChoice(ref s, scratch, HotChoiceKind.HandCard, 0, 1, 1, out HotChoiceSelection sel))
                            return;
                        int idx = FindCardIndexByUid(in s, HotPileKind.Hand, sel.CardUidOrIndex);
                        if (idx >= 0)
                            ExhaustCard(ref s, ref cold, rules, scratch, idx, causedByEthereal: false);
                    }
                }
                break;

            case HotPowerId.CreativeAiPower:
                if (ownerSlot == 0 && rules.CombatCharacterPowerPool.Length > 0)
                {
                    for (int k = 0; k < amount; k++)
                    {
                        int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardGeneration], rules.CombatCharacterPowerPool.Length);
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, rules.CombatCharacterPowerPool[pick], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
                }
                break;

            case HotPowerId.HelloWorldPower:
                if (ownerSlot == 0 && rules.UnlockedCharacterCardPool.Length > 0)
                {
                    int genCount = s.Powers[powerIdx].AmountOnTurnStart > 0 ? s.Powers[powerIdx].AmountOnTurnStart : amount;
                    for (int k = 0; k < genCount; k++)
                    {
                        int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardGeneration], rules.UnlockedCharacterCardPool.Length);
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, rules.UnlockedCharacterCardPool[pick], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
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
            {
                ushort targetUid = s.Powers[powerIdx].AuxCardUid;
                ushort targetDefId = (ushort)s.Powers[powerIdx].Aux0;
                RemovePowerAt(ref s, powerIdx);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                int srcIdx = FindCardIndexByUidAnyPile(in s, targetUid);
                if (srcIdx >= 0)
                {
                    HotCard template = s.Cards[srcIdx];
                    for (int k = 0; k < amount; k++)
                    {
                        HotCard copy = template;
                        copy.CardUid = s.NextCardUid++;
                        copy.Flags &= ~(HotCardFlags.HasDeckVersion | HotCardFlags.RemovedFromState | HotCardFlags.ExhaustOnNextPlay);
                        copy.Flags |= HotCardFlags.IsClone;
                        AddGeneratedCardInstance(ref s, ref cold, rules, scratch, in copy, HotPileKind.Hand, dstOffset: -1, createdByPlayer: true);
                    }
                }
                else if (targetDefId != 0)
                {
                    for (int k = 0; k < amount; k++)
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, targetDefId, HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                }
                break;
            }

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
                for (int k = 0; k < amount && s.DrawCount > 0; k++)
                {
                    if (!RequestChoice(ref s, scratch, HotChoiceKind.DrawPile, 0, 1, 1, out HotChoiceSelection sel))
                        return;
                    int idx = FindCardIndexByUid(in s, HotPileKind.Draw, sel.CardUidOrIndex);
                    if (idx >= 0)
                        MoveCardToHandOrDiscard(ref s, idx);
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
                    for (int k = 0; k < amount; k++)
                    {
                        int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardGeneration], rules.CombatCharacterPowerPool.Length);
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, rules.CombatCharacterPowerPool[pick], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
                }
                break;

            case HotPowerId.HelloWorldPower:
                if (rules.UnlockedCharacterCardPool.Length > 0)
                {
                    int genCount = s.Powers[powerIdx].AmountOnTurnStart > 0 ? s.Powers[powerIdx].AmountOnTurnStart : amount;
                    for (int k = 0; k < genCount; k++)
                    {
                        int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardGeneration], rules.UnlockedCharacterCardPool.Length);
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, rules.UnlockedCharacterCardPool[pick], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
                }
                break;

            case HotPowerId.CallOfTheVoidPower:
                if (rules.UnlockedCharacterCardPool.Length > 0)
                {
                    for (int k = 0; k < amount; k++)
                    {
                        int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardGeneration], rules.UnlockedCharacterCardPool.Length);
                        int addedIdx = AddGeneratedFixedCard(ref s, ref cold, rules, scratch, rules.UnlockedCharacterCardPool[pick], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                        if (addedIdx >= 0)
                            s.Cards[addedIdx].LocalKeywordsMask |= HotCardKeywordMask.Ethereal;
                    }
                }
                break;

            case HotPowerId.SpectrumShiftPower:
                if (rules.CombatColorlessPool.Length > 0)
                {
                    for (int k = 0; k < amount; k++)
                    {
                        int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardGeneration], rules.CombatColorlessPool.Length);
                        AddGeneratedFixedCard(ref s, ref cold, rules, scratch, rules.CombatColorlessPool[pick], HotPileKind.Hand, dstOffset: -1, upgraded: false, createdByPlayer: true);
                    }
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

    /// <summary>
    /// Retires temporary stat powers whose <see cref="HotPowerDef.HookMask"/> is <see cref="HotHookMask.None"/>
    /// at the end of their owner's side turn.
    /// </summary>
    internal static void RetireSideTurnEndTemporaryStats(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotCombatSide side)
    {
        int i = 0;
        while (i < s.PowerCount)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.Amount == 0 || !IsSlotOnCurrentSide(side, p.OwnerSlot) || p.PowerDefId >= rules.Powers.Length)
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
                // Do not increment i because RemovePowerAt shifted subsequent powers left.
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
        // Enemy-side AfterDeath powers (Ravenous, CrabRage, Explosive, Stock, Adaptable, Illusion,
        // Infested, Reattach, Surprise, Dampen, Heist, Swipe, etc.) are executed inside
        // HotMonsters.HandleEnemyDeathEffect so they never double-trigger.
        _ = powerIdx;
        _ = powerId;
        _ = ownerSlot;
        _ = amount;
        _ = args;
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

            SetPowerAmount(ref s, rules, targetSlot, poisonDefId, cur - 1);
        }
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
