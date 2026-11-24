using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    private const decimal SettlementCeilingDecimal = 999_999_999m;
    private const long SettlementCeilingLong = 999_999_999L;

    [ThreadStatic]
    internal static decimal t_lastGainBlockDecimal;

    [ThreadStatic]
    internal static int t_lastAttackUnblockedDamage;

    [ThreadStatic]
    internal static int t_lastAttackTotalPlusOverkill;

    [ThreadStatic]
    private static int t_lastHitTotalPlusOverkill;

    /// <summary>
    /// Exact STS2 0.113.1 block gain (Phase 1 compatibility overload without sidecar/scratch).
    /// </summary>
    public static int GainBlock(
        ref CombatHotState s,
        HotRules rules,
        byte targetSlot,
        int baseAmount,
        bool isCardBlock)
    {
        if (IsOverOrEnding(in s) || !IsCreatureSlotValid(in s, targetSlot))
            return 0;

        ref HotCreature target = ref GetCreatureRef(ref s, targetSlot);
        if ((target.Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        decimal block = baseAmount;
        if (isCardBlock)
        {
            if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyBlockAdditive) != 0 && rules.DexterityPowerDefId != 0)
                block += GetPowerAmount(in s, targetSlot, rules.DexterityPowerDefId);

            if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyBlockMultiplicative) != 0
                && rules.FrailPowerDefId != 0
                && GetPowerAmount(in s, targetSlot, rules.FrailPowerDefId) > 0)
            {
                block *= 0.75m;
            }
        }

        if (block <= 0m)
            return 0;

        decimal floored = Math.Floor(block);
        if (floored <= 0m)
            return 0;

        int gained = (int)Math.Min(floored, SettlementCeilingDecimal);
        target.Block = (int)Math.Min((long)target.Block + gained, SettlementCeilingLong);
        return gained;
    }

    /// <summary>
    /// Full hook-dispatching block gain matching STS2 0.113.1 (<c>ModifyBlockAdditive</c>,
    /// <c>ModifyBlockMultiplicative</c>, <c>AfterBlockGained</c> including Juggernaut/BeaconOfHope/relics).
    /// </summary>
    internal static int GainBlock(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot,
        int baseAmount,
        bool isCardBlock)
        => GainBlockInternal(ref s, ref cold, rules, scratch, targetSlot, baseAmount, isCardBlock, cardIdx: -1, cardDefId: 0);

    /// <summary>
    /// Computes modified block amount via STS2 0.113.1 <c>Hook.ModifyBlock</c> (enchantments +
    /// <c>ModifyBlockAdditive</c> + <c>ModifyBlockMultiplicative</c>) without mutating creature
    /// block, counters, or firing <c>AfterBlockGained</c> (used e.g. by <c>Glitterstream</c>'s
    /// <c>BlockNextTurn</c>).
    /// </summary>
    internal static int ComputeModifiedBlockAmount(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot,
        int baseAmount,
        bool isCardBlock,
        int cardIdx,
        ushort cardDefId,
        bool isUnpowered = false)
    {
        decimal block = ComputeModifiedBlockDecimal(
            ref s, ref cold, rules, scratch, targetSlot, baseAmount, isCardBlock, cardIdx, cardDefId, isUnpowered);
        if (block <= 0m)
            return 0;
        decimal floored = Math.Floor(block);
        return floored <= 0m ? 0 : (int)Math.Min(floored, SettlementCeilingDecimal);
    }

    private static decimal ComputeModifiedBlockDecimal(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot,
        int baseAmount,
        bool isCardBlock,
        int cardIdx,
        ushort cardDefId,
        bool isUnpowered)
    {
        if (IsOverOrEnding(in s) || !IsCreatureSlotValid(in s, targetSlot))
            return 0m;
        if ((GetCreatureRef(ref s, targetSlot).Flags & HotCreatureFlags.Alive) == 0)
            return 0m;

        decimal block = baseAmount;

        // Card enchantment additive block (Nimble / Goopy — applies on card block including unpowered e.g. Entrench)
        if (isCardBlock && cardIdx >= 0 && cardIdx < s.TotalCardCount && s.Cards[cardIdx].EnchId != 0)
        {
            byte eId = s.Cards[cardIdx].EnchId;
            if (eId < rules.Enchantments.Length)
            {
                HotEnchantmentId kind = rules.Enchantments[eId].EnchantmentId;
                if (kind == HotEnchantmentId.Nimble)
                    block += s.Cards[cardIdx].EnchAmt;
                else if (kind == HotEnchantmentId.Goopy && s.Cards[cardIdx].EnchAmt > 1)
                    block += s.Cards[cardIdx].EnchAmt - 1;
            }
        }

        // Additive pass (Dexterity, Fasten, Stretch, Relics — powered card/move block only)
        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyBlockAdditive) != 0)
        {
            HotHookArgs addArgs = new()
            {
                TargetSlot = targetSlot,
                CardIdx = cardIdx,
                CardDefId = cardDefId,
                IsCardBlock = isCardBlock && !isUnpowered,
                DecimalValue = 0m,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyBlockAdditive, ref addArgs);
            block += addArgs.DecimalValue;
        }

        // Multiplicative pass (Frail, NoBlock, Shadowmeld, Unmovable, Vambrace, PaelsLegion)
        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyBlockMultiplicative) != 0)
        {
            HotHookArgs mulArgs = new()
            {
                TargetSlot = targetSlot,
                CardIdx = cardIdx,
                CardDefId = cardDefId,
                IsCardBlock = isCardBlock,
                BoolFlag = isUnpowered,
                DecimalValue = block,
                DecimalMultiplier = 1m,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyBlockMultiplicative, ref mulArgs);
            if (mulArgs.DecimalMultiplier >= decimal.MaxValue && block > 0m)
                block = SettlementCeilingDecimal;
            else
                block *= mulArgs.DecimalMultiplier;
        }

        return Math.Max(0m, block);
    }

    internal static int GainBlockInternal(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot,
        int baseAmount,
        bool isCardBlock,
        int cardIdx,
        ushort cardDefId,
        bool isUnpowered = false)
    {
        t_lastGainBlockDecimal = 0m;
        if (IsOverOrEnding(in s) || !IsCreatureSlotValid(in s, targetSlot))
            return 0;
        if ((GetCreatureRef(ref s, targetSlot).Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        decimal decimalResult = ComputeModifiedBlockDecimal(
            ref s, ref cold, rules, scratch, targetSlot, baseAmount, isCardBlock, cardIdx, cardDefId, isUnpowered);
        t_lastGainBlockDecimal = decimalResult;
        if (decimalResult <= 0m)
            return 0;

        decimal floored = Math.Floor(decimalResult);
        if (floored <= 0m)
            return 0;

        int gained = (int)Math.Min(floored, SettlementCeilingDecimal);
        ref HotCreature target = ref GetCreatureRef(ref s, targetSlot);
        target.Block = (int)Math.Min((long)target.Block + gained, SettlementCeilingLong);

        if (isCardBlock && targetSlot == 0 && gained > 0)
        {
            s.Counters[(int)HotCounterSlot.CardBlockGainsThisTurn]++;
            scratch.CardBlockGainsInCurrentPlay++;
        }

        if (gained > 0 && (s.ActiveHookMask & (ulong)HotHookMask.AfterBlockGained) != 0)
        {
            HotHookArgs afterArgs = new()
            {
                ActorSlot = targetSlot,
                TargetSlot = targetSlot,
                BlockGained = gained,
                IsCardBlock = isCardBlock,
                CardIdx = cardIdx,
                CardDefId = cardDefId,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterBlockGained, ref afterArgs);
        }

        t_lastGainBlockDecimal = decimalResult;
        return gained;
    }

    public static int DealDamage(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        byte targetSlot,
        int baseDamage,
        bool isPoweredAttack)
        => DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot, targetSlot, baseDamage, isPoweredAttack, isUnblockable: false, cardIdx: -1, cardDefId: 0);

    internal static int DealDamage(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        byte targetSlot,
        int baseDamage,
        bool isPoweredAttack,
        bool isUnblockable)
        => DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot, targetSlot, baseDamage, isPoweredAttack, isUnblockable, cardIdx: -1, cardDefId: 0);

    internal static int DealDamageInternal(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        byte targetSlot,
        int baseDamage,
        bool isPoweredAttack,
        bool isUnblockable,
        int cardIdx,
        ushort cardDefId,
        bool deferKill = false,
        bool capOnlyModifiers = false)
    {
        t_lastHitTotalPlusOverkill = 0;

        if (dealerSlot != 0xFF)
        {
            if (!IsCreatureSlotValid(in s, dealerSlot))
                return 0;
            ref readonly HotCreature dealerRef = ref GetCreatureRef(ref s, dealerSlot);
            if ((dealerRef.Flags & HotCreatureFlags.Alive) == 0 || dealerRef.CurrentHp <= 0)
                return 0;
        }

        if (!IsCreatureSlotValid(in s, targetSlot))
            return 0;
        ref readonly HotCreature initialTargetRef = ref GetCreatureRef(ref s, targetSlot);
        if ((initialTargetRef.Flags & HotCreatureFlags.Alive) == 0 || initialTargetRef.CurrentHp <= 0)
            return 0;

        ushort cardUid = (uint)cardIdx < (uint)s.TotalCardCount ? s.Cards[cardIdx].CardUid : (ushort)0;

        decimal modifiedAmount = ComputeModifiedDamage(ref s, ref cold, rules, scratch, dealerSlot, targetSlot, baseDamage, isPoweredAttack, cardIdx, cardDefId, capOnlyModifiers);

        // Pre-block damage cap (Intangible, HardToKill, HardenedShell)
        if (modifiedAmount > 0m && (s.ActiveHookMask & (ulong)HotHookMask.ModifyDamageCap) != 0)
        {
            HotHookArgs capPre = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
                IsPoweredAttack = isPoweredAttack,
                IsUnblockable = isUnblockable,
                BoolFlag = true, // pre-block phase
                DecimalValue = modifiedAmount,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyDamageCap, ref capPre);
            modifiedAmount = Math.Max(0m, capPre.DecimalValue);
        }

        // BeforeDamageReceived hook (Thorns, etc.)
        if ((s.ActiveHookMask & (ulong)HotHookMask.BeforeDamageReceived) != 0)
        {
            cardIdx = RefreshCardIdxByUid(in s, cardIdx, cardUid);
            HotHookArgs beforeArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
                CardIdx = cardIdx,
                CardUid = cardUid,
                CardDefId = cardDefId,
                IsPoweredAttack = isPoweredAttack,
                IsUnblockable = isUnblockable,
                Amount = (int)Math.Clamp(modifiedAmount, 0m, SettlementCeilingDecimal),
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.BeforeDamageReceived, ref beforeArgs);
            if (scratch.NeedsChoice)
                return 0;
        }

        ref readonly HotCreature postBeforeTarget = ref GetCreatureRef(ref s, targetSlot);
        if ((postBeforeTarget.Flags & HotCreatureFlags.Alive) == 0 || postBeforeTarget.CurrentHp <= 0)
            return 0;

        decimal damageRemaining = modifiedAmount;

        // Pre-block Wuppy interception when player is targeted on powered attacks (WuppysLoyaltyPower.cs:28-43).
        // STS2 0.113.1 CreatureCmd.cs:317-356, 455-491: apply HP loss to ALL receivers (Wuppy, Osty, originalTarget)
        // FIRST, then dispatch post-damage hooks (AfterBlockBroken, AfterCurrentHpChanged, AfterDamageGiven,
        // AfterDamageReceived) and Kill so e.g. NecroMasteryPower sees Player.IsDead when overkill kills Player.
        int wuppyLoss = 0;
        if (targetSlot == 0
            && isPoweredAttack
            && damageRemaining > 0m
            && (s.CombatFlags & HotCombatFlags.HasWuppy) != 0
            && (s.Wuppy.Flags & HotCreatureFlags.Alive) != 0
            && (rules.HasRelic(HotRelicId.Wuppy) || GetPowerAmountById(in s, rules, 2, HotPowerId.WuppysLoyaltyPower) > 0))
        {
            int wuppyDmg = (int)Math.Clamp(damageRemaining, 0m, SettlementCeilingDecimal);
            int wuppyPrev = s.Wuppy.CurrentHp;
            bool wuppyKilled = wuppyPrev > 0 && damageRemaining >= wuppyPrev;
            s.Wuppy.CurrentHp = Math.Max(0, wuppyPrev - wuppyDmg);
            wuppyLoss = wuppyPrev - s.Wuppy.CurrentHp;
            int wuppyOverkill = wuppyKilled ? Math.Max(wuppyDmg - wuppyPrev, 0) : 0;
            if (wuppyKilled || s.Wuppy.CurrentHp <= 0)
                s.Wuppy.Flags &= ~HotCreatureFlags.Alive;
            damageRemaining = wuppyOverkill;
        }

        // Block absorption (matches STS2 0.113.1 CreatureCmd.cs:313,326,338:
        // pets share their owner's Block via originalTarget.PetOwner?.Creature ?? originalTarget).
        byte blockSlot = (targetSlot == 1 || targetSlot == 2) ? (byte)0 : targetSlot;
        ref HotCreature blockTarget = ref GetCreatureRef(ref s, blockSlot);
        int blockBefore = blockTarget.Block;
        decimal blockedDamage = isUnblockable
            ? 0m
            : Math.Min((decimal)blockBefore, damageRemaining);
        int blockedInt = (int)blockedDamage;
        blockTarget.Block -= blockedInt;
        damageRemaining = Math.Max(damageRemaining - blockedDamage, 0m);
        ref readonly HotCreature origTargetBlock = ref GetCreatureRef(ref s, targetSlot);
        bool wasBlockBroken = origTargetBlock.Block <= 0 && blockedDamage > 0m;

        // Post-block Osty interception when player is targeted on powered attacks (DieForYouPower.cs:15-30).
        int ostyLoss = 0;
        bool ostyDiedNow = false;
        if (targetSlot == 0
            && isPoweredAttack
            && damageRemaining > 0m
            && (s.CombatFlags & HotCombatFlags.HasOsty) != 0
            && (s.Osty.Flags & HotCreatureFlags.Alive) != 0)
        {
            int ostyDmg = (int)Math.Clamp(damageRemaining, 0m, SettlementCeilingDecimal);
            int ostyPrev = s.Osty.CurrentHp;
            bool ostyKilled = ostyPrev > 0 && damageRemaining >= ostyPrev;
            s.Osty.CurrentHp = Math.Max(0, ostyPrev - ostyDmg);
            ostyLoss = ostyPrev - s.Osty.CurrentHp;
            int ostyOverkill = ostyKilled ? Math.Max(ostyDmg - ostyPrev, 0) : 0;
            if (ostyKilled || s.Osty.CurrentHp <= 0)
            {
                s.Osty.Flags &= ~HotCreatureFlags.Alive;
                ostyDiedNow = true;
            }
            damageRemaining = ostyOverkill;
        }

        // Post-block / post-Osty HP loss caps matching STS2 0.113.1 Hook.ModifyHpLost (Hook.cs:1805-1825):
        // Pass 1 (Amount = 1): ModifyHpLostAfterOsty (IntangiblePower, SlipperyPower, HardenedShellPower, BeatingRemnant, TungstenRod)
        // Pass 2 (Amount = 2): ModifyHpLostAfterOstyLate (BufferPower, TheBoot) — only when damage remains after Pass 1.
        // Keep exact decimal damageRemaining (truncation happens in LoseHpInternal and BufferPower's Truncate check).
        if (damageRemaining > 0m && (s.ActiveHookMask & (ulong)HotHookMask.ModifyDamageCap) != 0)
        {
            HotHookArgs capPost = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
                CardDefId = cardDefId,
                IsPoweredAttack = isPoweredAttack,
                IsUnblockable = isUnblockable,
                BoolFlag = false, // post-block phase
                Amount = 1,       // ModifyHpLostAfterOsty
                DecimalValue = damageRemaining,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyDamageCap, ref capPost);
            damageRemaining = Math.Max(0m, capPost.DecimalValue);

            if (damageRemaining > 0m && (s.ActiveHookMask & (ulong)HotHookMask.ModifyDamageCap) != 0)
            {
                HotHookArgs capPostLate = new()
                {
                    ActorSlot = dealerSlot,
                    TargetSlot = targetSlot,
                    CardDefId = cardDefId,
                    IsPoweredAttack = isPoweredAttack,
                    IsUnblockable = isUnblockable,
                    BoolFlag = false, // post-block phase
                    Amount = 2,       // ModifyHpLostAfterOstyLate
                    DecimalValue = damageRemaining,
                };
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyDamageCap, ref capPostLate);
                damageRemaining = Math.Max(0m, capPostLate.DecimalValue);
            }
        }

        // Target HP loss (matches SimCreatureState.LoseHp truncation).
        ref HotCreature target = ref GetCreatureRef(ref s, targetSlot);
        int hpPrev = target.CurrentHp;
        bool wasTargetKilled = hpPrev > 0 && damageRemaining >= hpPrev;
        int hpLoss = (int)Math.Clamp(damageRemaining, 0m, SettlementCeilingDecimal);
        target.CurrentHp = Math.Max(0, hpPrev - hpLoss);
        int unblockedDamage = hpPrev - target.CurrentHp;
        int overkillDamage = wasTargetKilled ? Math.Max(hpLoss - hpPrev, 0) : 0;
        int hitTotalPlusOverkill = blockedInt + unblockedDamage + overkillDamage;
        bool wasFullyBlocked = !isUnblockable && (blockedDamage > 0m || target.Block > 0) && unblockedDamage == 0 && ostyLoss == 0;

        // STS2 0.113.1 CreatureCmd.cs:339,364: History.DamageReceived records every hit unconditionally
        // before AfterDamageReceived/Kill (including fatal hits revived by LizardTail/FairyInABottle,
        // 0-damage unblocked hits when Block == 0, unblockable hits, and LoseMaxHp overflow).
        HotRelics.RecordEmotionChipDamageIfEligible(ref s, rules, targetSlot, wasFullyBlocked);

        // Post-HP-loss hooks for Wuppy / Osty results (CreatureCmd.cs:455-465) — run AFTER target HP loss.
        if (wuppyLoss > 0 && (s.ActiveHookMask & (ulong)HotHookMask.AfterHpLost) != 0)
        {
            HotHookArgs wuppyHpArgs = new()
            {
                TargetSlot = 2,
                Amount = wuppyLoss,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterHpLost, ref wuppyHpArgs);
            if (scratch.NeedsChoice)
            {
                t_lastHitTotalPlusOverkill = hitTotalPlusOverkill;
                return unblockedDamage;
            }
        }

        if (ostyLoss > 0 && (s.ActiveHookMask & (ulong)HotHookMask.AfterHpLost) != 0)
        {
            HotHookArgs ostyHpArgs = new()
            {
                TargetSlot = 1,
                Amount = ostyLoss,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterHpLost, ref ostyHpArgs);
            if (scratch.NeedsChoice)
            {
                t_lastHitTotalPlusOverkill = hitTotalPlusOverkill;
                return unblockedDamage;
            }
        }

        if (wasBlockBroken && (s.ActiveHookMask & (ulong)HotHookMask.AfterBlockBroken) != 0)
        {
            HotHookArgs brokenArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterBlockBroken, ref brokenArgs);
            if (scratch.NeedsChoice)
            {
                t_lastHitTotalPlusOverkill = hitTotalPlusOverkill;
                return unblockedDamage;
            }
        }

        if (unblockedDamage > 0)
        {
            if (targetSlot == 0)
            {
                s.HistoryUnblockedHitsReceived++;
                s.Counters[(int)HotCounterSlot.HasLostHpThisTurn] = 1;
                s.Counters[(int)HotCounterSlot.PlayerCumulativeHpLost] += unblockedDamage;
            }
            else if (targetSlot >= 3 && scratch.AttackActive)
            {
                int enemyIdx = targetSlot - 3;
                if ((uint)enemyIdx < 32u)
                    scratch.AttackUnblockedEnemyMask |= 1u << enemyIdx;
            }
            scratch.UnblockedDamageThisPlay = true;

            if ((s.ActiveHookMask & (ulong)HotHookMask.AfterHpLost) != 0)
            {
                HotHookArgs hpArgs = new()
                {
                    TargetSlot = targetSlot,
                    Amount = unblockedDamage,
                };
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterHpLost, ref hpArgs);
            }
        }

        if (isPoweredAttack && dealerSlot == 0 && targetSlot >= 3)
            s.Enemies[targetSlot - 3].PoweredHitsFromPlayer++;

        // AfterDamageGiven hook (STS2 0.113.1 CreatureCmd.cs:470-473: runs BEFORE AfterDamageReceived)
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterDamageGiven) != 0)
        {
            cardIdx = RefreshCardIdxByUid(in s, cardIdx, cardUid);
            HotHookArgs givenArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
                CardIdx = cardIdx,
                CardUid = cardUid,
                CardDefId = cardDefId,
                IsPoweredAttack = isPoweredAttack,
                IsUnblockable = isUnblockable,
                BlockedDamage = blockedInt,
                UnblockedDamage = unblockedDamage,
                WasBlockBroken = wasBlockBroken,
                WasFullyBlocked = wasFullyBlocked,
                WasTargetKilled = wasTargetKilled,
                Amount = blockedInt + unblockedDamage,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterDamageGiven, ref givenArgs);
            if (scratch.NeedsChoice)
            {
                t_lastHitTotalPlusOverkill = hitTotalPlusOverkill;
                return unblockedDamage;
            }
        }

        // AfterDamageReceived hook (STS2 0.113.1 CreatureCmd.cs:474-477: skipped when WasTargetKilled && originalTarget.IsDead
        // so fatal hits that trigger death-preventing revives like LizardTail in Kill() do not fire FlameBarrier/Reflect/etc.)
        ref readonly HotCreature postGivenTarget = ref GetCreatureRef(ref s, targetSlot);
        bool targetIsDead = postGivenTarget.CurrentHp <= 0 || (postGivenTarget.Flags & HotCreatureFlags.Alive) == 0;
        if ((!wasTargetKilled || !targetIsDead) && (s.ActiveHookMask & (ulong)HotHookMask.AfterDamageReceived) != 0)
        {
            cardIdx = RefreshCardIdxByUid(in s, cardIdx, cardUid);
            HotHookArgs recvArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
                CardIdx = cardIdx,
                CardUid = cardUid,
                CardDefId = cardDefId,
                IsPoweredAttack = isPoweredAttack,
                IsUnblockable = isUnblockable,
                BlockedDamage = blockedInt,
                UnblockedDamage = unblockedDamage,
                WasBlockBroken = wasBlockBroken,
                WasFullyBlocked = wasFullyBlocked,
                WasTargetKilled = wasTargetKilled,
                Amount = blockedInt + unblockedDamage,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterDamageReceived, ref recvArgs);
            if (scratch.NeedsChoice)
            {
                t_lastHitTotalPlusOverkill = hitTotalPlusOverkill;
                return unblockedDamage;
            }
        }

        if (ostyDiedNow)
            HotRegentNecro.OnAnyCreatureDeath(ref s, ref cold, rules);

        if (!deferKill && (wasTargetKilled || GetCreatureRef(ref s, targetSlot).CurrentHp <= 0))
            KillCreature(ref s, ref cold, rules, scratch, targetSlot, force: false, sourceCardDefId: cardDefId, sourceCardUid: cardUid);

        t_lastHitTotalPlusOverkill = hitTotalPlusOverkill;
        return unblockedDamage;
    }

    internal static int DamageAllEnemies(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        int baseDamage,
        bool isPoweredAttack,
        bool isUnblockable = false)
    {
        Span<byte> targets = stackalloc byte[HotLimits.MaxEnemies];
        int count = SnapshotLivingEnemies(in s, targets);
        int totalUnblocked = 0;
        for (int i = 0; i < count; i++)
        {
            if (scratch.NeedsChoice)
                break;
            byte enemySlot = targets[i];
            if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;
            totalUnblocked += DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot, (byte)(3 + enemySlot), baseDamage, isPoweredAttack, isUnblockable, -1, 0, deferKill: true);
        }
        for (int i = 0; i < count; i++)
        {
            if (scratch.NeedsChoice)
                break;
            byte enemySlot = targets[i];
            if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0
                && s.Enemies[enemySlot].Vital.CurrentHp <= 0)
            {
                KillCreature(ref s, ref cold, rules, scratch, (byte)(3 + enemySlot), force: false);
            }
        }
        return totalUnblocked;
    }

    internal static int DamageRandomEnemy(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        int baseDamage,
        bool isPoweredAttack,
        bool isUnblockable = false)
    {
        int enemySlot = PickRandomLivingEnemySlot(ref s);
        if (enemySlot < 0)
            return 0;
        return DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot, (byte)(3 + enemySlot), baseDamage, isPoweredAttack, isUnblockable, -1, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RefreshCardIdxByUid(in CombatHotState s, int cardIdx, ushort cardUid)
    {
        if (cardUid == 0)
            return cardIdx;
        if ((uint)cardIdx < (uint)s.TotalCardCount && s.Cards[cardIdx].CardUid == cardUid)
            return cardIdx;
        int liveIdx = FindCardIndexByUid(in s, HotPileKind.Play, cardUid);
        return liveIdx >= 0 ? liveIdx : FindCardIndexByUidAnyPile(in s, cardUid);
    }

    internal static void ExecuteDealerSingleTargetAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        int cardIdx,
        ushort cardDefId,
        int targetEnemySlot,
        int baseDamage,
        int hits,
        bool powered)
    {
        t_lastAttackUnblockedDamage = 0;
        t_lastAttackTotalPlusOverkill = 0;
        if (IsOverOrEnding(in s) || (uint)targetEnemySlot >= (uint)s.EnemyCount)
            return;
        if (dealerSlot == 0 && (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return;
        if (dealerSlot == 1 && ((s.CombatFlags & HotCombatFlags.HasOsty) == 0 || (s.Osty.Flags & HotCreatureFlags.Alive) == 0))
            return;
        if (cardDefId == 0 && cardIdx >= 0 && cardIdx < s.TotalCardCount)
            cardDefId = s.Cards[cardIdx].CardDefId;
        ushort cardUid = (uint)cardIdx < (uint)s.TotalCardCount ? s.Cards[cardIdx].CardUid : (ushort)0;

        if (hits < 0)
            hits = 0;
        hits = ApplyModifyAttackHitCount(rules, cardDefId, hits);
        byte targetCreatureSlot = (byte)(3 + targetEnemySlot);

        BeginAttackHooks(ref s, ref cold, rules, scratch, dealerSlot, cardIdx, cardDefId, powered);
        scratch.AttackActive = true;
        int totalUnblocked = 0;
        int totalPlusOverkill = 0;
        for (int h = 0; h < hits; h++)
        {
            if (IsOverOrEnding(in s)
                || !IsValidEnemyTarget(in s, targetEnemySlot)
                || scratch.NeedsChoice)
            {
                break;
            }
            if (dealerSlot == 0 && (s.Player.Flags & HotCreatureFlags.Alive) == 0)
                break;
            if (dealerSlot == 1 && (s.Osty.Flags & HotCreatureFlags.Alive) == 0)
                break;
            cardIdx = RefreshCardIdxByUid(in s, cardIdx, cardUid);
            totalUnblocked += DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot, targetCreatureSlot, baseDamage, isPoweredAttack: powered, isUnblockable: false, cardIdx: cardIdx, cardDefId: cardDefId);
            totalPlusOverkill += t_lastHitTotalPlusOverkill;
        }
        scratch.AttackActive = false;
        t_lastAttackUnblockedDamage = totalUnblocked;
        t_lastAttackTotalPlusOverkill = totalPlusOverkill;
        cardIdx = RefreshCardIdxByUid(in s, cardIdx, cardUid);
        EndAttackHooks(ref s, ref cold, rules, scratch, dealerSlot, cardIdx, cardDefId, powered);
        t_lastAttackUnblockedDamage = totalUnblocked;
        t_lastAttackTotalPlusOverkill = totalPlusOverkill;
    }

    internal static void ExecuteDealerAllEnemiesAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        int cardIdx,
        ushort cardDefId,
        int baseDamage,
        int hits,
        bool powered)
    {
        if (IsOverOrEnding(in s))
            return;
        if (dealerSlot == 1 && ((s.CombatFlags & HotCombatFlags.HasOsty) == 0 || (s.Osty.Flags & HotCreatureFlags.Alive) == 0))
            return;
        if (cardDefId == 0 && cardIdx >= 0 && cardIdx < s.TotalCardCount)
            cardDefId = s.Cards[cardIdx].CardDefId;
        ushort cardUid = (uint)cardIdx < (uint)s.TotalCardCount ? s.Cards[cardIdx].CardUid : (ushort)0;

        if (hits < 0)
            hits = 0;
        hits = ApplyModifyAttackHitCount(rules, cardDefId, hits);
        Span<byte> targets = stackalloc byte[HotLimits.MaxEnemies];

        BeginAttackHooks(ref s, ref cold, rules, scratch, dealerSlot, cardIdx, cardDefId, powered);
        scratch.AttackActive = true;
        for (int h = 0; h < hits; h++)
        {
            if (IsOverOrEnding(in s) || scratch.NeedsChoice)
                break;
            if (dealerSlot == 0 && (s.Player.Flags & HotCreatureFlags.Alive) == 0)
                break;
            if (dealerSlot == 1 && (s.Osty.Flags & HotCreatureFlags.Alive) == 0)
                break;

            int targetCount = SnapshotLivingEnemies(in s, targets);
            if (targetCount == 0)
                break;

            for (int t = 0; t < targetCount; t++)
            {
                if (scratch.NeedsChoice)
                    break;
                if (dealerSlot == 0 && (s.Player.Flags & HotCreatureFlags.Alive) == 0)
                    break;
                if (dealerSlot == 1 && (s.Osty.Flags & HotCreatureFlags.Alive) == 0)
                    break;
                byte enemySlot = targets[t];
                if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                    continue;
                cardIdx = RefreshCardIdxByUid(in s, cardIdx, cardUid);
                DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot, (byte)(3 + enemySlot), baseDamage, isPoweredAttack: powered, isUnblockable: false, cardIdx: cardIdx, cardDefId: cardDefId, deferKill: true);
            }
            for (int t = 0; t < targetCount; t++)
            {
                if (scratch.NeedsChoice)
                    break;
                byte enemySlot = targets[t];
                if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0
                    && s.Enemies[enemySlot].Vital.CurrentHp <= 0)
                {
                    KillCreature(ref s, ref cold, rules, scratch, (byte)(3 + enemySlot), force: false, sourceCardDefId: cardDefId, sourceCardUid: cardUid);
                }
            }
        }
        scratch.AttackActive = false;
        cardIdx = RefreshCardIdxByUid(in s, cardIdx, cardUid);
        EndAttackHooks(ref s, ref cold, rules, scratch, dealerSlot, cardIdx, cardDefId, powered);
    }

    internal static void ExecuteDealerRandomEnemyAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        int cardIdx,
        ushort cardDefId,
        int baseDamage,
        int hits,
        bool powered)
    {
        if (IsOverOrEnding(in s))
            return;
        if (dealerSlot == 1 && ((s.CombatFlags & HotCombatFlags.HasOsty) == 0 || (s.Osty.Flags & HotCreatureFlags.Alive) == 0))
            return;
        if (cardDefId == 0 && cardIdx >= 0 && cardIdx < s.TotalCardCount)
            cardDefId = s.Cards[cardIdx].CardDefId;
        ushort cardUid = (uint)cardIdx < (uint)s.TotalCardCount ? s.Cards[cardIdx].CardUid : (ushort)0;

        if (hits < 0)
            hits = 0;
        hits = ApplyModifyAttackHitCount(rules, cardDefId, hits);

        BeginAttackHooks(ref s, ref cold, rules, scratch, dealerSlot, cardIdx, cardDefId, powered);
        scratch.AttackActive = true;
        for (int h = 0; h < hits; h++)
        {
            if (IsOverOrEnding(in s) || scratch.NeedsChoice)
                break;
            if (dealerSlot == 0 && (s.Player.Flags & HotCreatureFlags.Alive) == 0)
                break;
            if (dealerSlot == 1 && (s.Osty.Flags & HotCreatureFlags.Alive) == 0)
                break;
            int enemySlot = PickRandomLivingEnemySlot(ref s);
            if (enemySlot < 0)
                break;
            cardIdx = RefreshCardIdxByUid(in s, cardIdx, cardUid);
            DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot, (byte)(3 + enemySlot), baseDamage, isPoweredAttack: powered, isUnblockable: false, cardIdx: cardIdx, cardDefId: cardDefId);
        }
        scratch.AttackActive = false;
        cardIdx = RefreshCardIdxByUid(in s, cardIdx, cardUid);
        EndAttackHooks(ref s, ref cold, rules, scratch, dealerSlot, cardIdx, cardDefId, powered);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PickRandomLivingEnemySlot(ref CombatHotState s)
    {
        Span<byte> targets = stackalloc byte[HotLimits.MaxEnemies];
        int count = SnapshotLivingEnemies(in s, targets);
        if (count == 0)
            return -1;
        int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatTargets], count);
        return targets[pick];
    }

    private static decimal ComputeModifiedDamage(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        byte targetSlot,
        int baseDamage,
        bool isPoweredAttack,
        int cardIdx,
        ushort cardDefId,
        bool capOnlyModifiers = false)
    {
        decimal damage = baseDamage;
        if (capOnlyModifiers || !isPoweredAttack)
            return Math.Max(0m, damage);

        if (cold != null && cardIdx >= 0 && cardIdx < s.TotalCardCount && cardDefId > 0 && cardDefId < rules.Cards.Length)
        {
            HotCardDef def = rules.Cards[cardDefId];
            if (def.Var0Index < def.OrderedVarNames.Length
                && string.Equals(def.OrderedVarNames[def.Var0Index], "Damage", StringComparison.Ordinal))
            {
                damage += HotColdSidecar.GetDecimalVarDelta(cold, s.Cards[cardIdx].CardUid);
            }
        }

        // Card enchantment additive & multiplicative damage (Hook.ModifyDamage lines 1541-1551:
        // EnchantDamageAdditive then EnchantDamageMultiplicative run BEFORE Strength/Vigor/Weak/Vulnerable).
        if (cardIdx >= 0 && cardIdx < s.TotalCardCount && s.Cards[cardIdx].EnchId != 0)
        {
            byte eId = s.Cards[cardIdx].EnchId;
            if (eId < rules.Enchantments.Length)
            {
                HotEnchantmentId kind = rules.Enchantments[eId].EnchantmentId;
                if (kind == HotEnchantmentId.Sharp)
                    damage += s.Cards[cardIdx].EnchAmt;
                else if (kind == HotEnchantmentId.Momentum)
                    damage += s.Cards[cardIdx].EnchPrivate;
                else if (kind == HotEnchantmentId.Vigorous && s.Cards[cardIdx].EnchStatus == 0)
                    damage += s.Cards[cardIdx].EnchAmt;
                else if (kind == HotEnchantmentId.TezcatarasEmber)
                    damage += 3m;
                else if (kind == HotEnchantmentId.Instinct)
                    damage *= 2m;
                else if (kind == HotEnchantmentId.Corrupted)
                    damage *= 1.5m;
            }
        }

        // Additive pass (Strength, Vigor, Accuracy, PhantomBlades, Aim, StrikeDummy, etc.)
        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyDamageAdditive) != 0)
        {
            HotHookArgs addArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
                CardIdx = cardIdx,
                CardDefId = cardDefId,
                IsPoweredAttack = true,
                DecimalValue = 0m,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyDamageAdditive, ref addArgs);
            damage += addArgs.DecimalValue;
        }

        // Multiplicative pass (Weak, Vulnerable, DoubleDamage, Gigantification, Colossus, Slow, Flutter, PenNib, etc.)
        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyDamageMultiplicative) != 0)
        {
            HotHookArgs mulArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
                CardIdx = cardIdx,
                CardDefId = cardDefId,
                IsPoweredAttack = true,
                DecimalMultiplier = 1m,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyDamageMultiplicative, ref mulArgs);
            damage *= mulArgs.DecimalMultiplier;
        }

        // Card-level ModifyDamageMultiplicative (WahHammer: 2x vs Impaled target, after additive pass).
        if (cardDefId != 0 && cardDefId < rules.Cards.Length
            && rules.Cards[cardDefId].CardId == HotCustomCardId.WahHammer
            && targetSlot >= 3
            && HotFoolTarot.IsEnemyImpaledAny(in s, rules, targetSlot - 3))
        {
            damage *= 2m;
        }

        return Math.Max(0m, damage);
    }

    public static int HealCreature(ref CombatHotState s, byte targetSlot, int amount)
    {
        if (amount <= 0 || !IsCreatureSlotValid(in s, targetSlot))
            return 0;

        ref HotCreature creature = ref GetCreatureRef(ref s, targetSlot);
        if ((creature.Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        int prev = creature.CurrentHp;
        int next = Math.Min(creature.MaxHp, prev + amount);
        int healed = next - prev;
        creature.CurrentHp = next;
        if (targetSlot == 0 && healed > 0)
            s.Counters[(int)HotCounterSlot.PlayerRecoveredHp] += healed;
        return healed;
    }

    internal static int HealCreature(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot,
        int amount)
    {
        if (amount <= 0 || !IsCreatureSlotValid(in s, targetSlot))
            return 0;

        int modified = amount;
        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyHealAmount) != 0)
        {
            HotHookArgs modArgs = new()
            {
                TargetSlot = targetSlot,
                HealAmount = modified,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyHealAmount, ref modArgs);
            modified = Math.Max(0, modArgs.HealAmount);
        }

        int healed = HealCreature(ref s, targetSlot, modified);
        if (healed > 0 && (s.ActiveHookMask & (ulong)HotHookMask.AfterHeal) != 0)
        {
            HotHookArgs afterArgs = new()
            {
                TargetSlot = targetSlot,
                HealAmount = healed,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterHeal, ref afterArgs);
        }
        return healed;
    }

    internal static void GainMaxHp(ref CombatHotState s, byte targetSlot, int amount)
    {
        if (amount <= 0 || !IsCreatureSlotValid(in s, targetSlot))
            return;
        ref HotCreature c = ref GetCreatureRef(ref s, targetSlot);
        if ((c.Flags & HotCreatureFlags.Alive) == 0)
            return;
        int oldMax = c.MaxHp;
        int nextMax = (int)Math.Min((long)oldMax + amount, SettlementCeilingLong);
        int delta = nextMax - oldMax;
        c.MaxHp = nextMax;
        if (delta > 0)
            HealCreature(ref s, targetSlot, delta);
    }

    internal static void GainMaxHp(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot,
        int amount)
    {
        if (amount <= 0 || !IsCreatureSlotValid(in s, targetSlot))
            return;
        ref HotCreature c = ref GetCreatureRef(ref s, targetSlot);
        if ((c.Flags & HotCreatureFlags.Alive) == 0)
            return;
        int oldMax = c.MaxHp;
        int nextMax = (int)Math.Min((long)oldMax + amount, SettlementCeilingLong);
        int delta = nextMax - oldMax;
        c.MaxHp = nextMax;
        if (delta > 0)
            HealCreature(ref s, ref cold, rules, scratch, targetSlot, delta);
    }

    internal static void LoseMaxHp(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot,
        int amount,
        ushort cardDefId = 0)
    {
        if (amount <= 0 || !IsCreatureSlotValid(in s, targetSlot))
            return;
        ref HotCreature c = ref GetCreatureRef(ref s, targetSlot);
        if ((c.Flags & HotCreatureFlags.Alive) == 0)
            return;
        int newMax = Math.Max(1, c.MaxHp - amount);
        if (c.CurrentHp > newMax)
        {
            DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot: 0xFF, targetSlot: targetSlot, baseDamage: c.CurrentHp - newMax, isPoweredAttack: false, isUnblockable: true, -1, cardDefId);
            if (scratch.NeedsChoice)
                return;
        }
        ref HotCreature target = ref GetCreatureRef(ref s, targetSlot);
        target.MaxHp = newMax;
        if (target.CurrentHp > newMax)
            target.CurrentHp = newMax;
    }

    internal static void GainEnergy(ref CombatHotState s, HotRules rules, int amount)
    {
        if (amount <= 0)
            return;
        if (GetPowerAmountById(in s, rules, 0, HotPowerId.NoEnergyGainPower) > 0)
            return;
        s.Energy += amount;
    }

    internal static void LoseEnergy(ref CombatHotState s, int amount)
    {
        if (amount <= 0)
            return;
        s.Energy = Math.Max(0, s.Energy - amount);
    }

    internal static void GainStars(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int amount)
    {
        if (amount <= 0)
            return;
        s.Stars += amount;
        s.Counters[(int)HotCounterSlot.StarsGainedThisTurn] += amount;

        // 0.113.1 BlackHolePower.AfterStarsGained (sole combat listener of AfterStarsGained):
        // deals Unpowered AOE damage to all living enemies whenever player gains > 0 stars.
        int blackHoleAmt = GetPowerAmountById(in s, rules, 0, HotPowerId.BlackHolePower);
        if (blackHoleAmt > 0 && !IsOverOrEnding(in s) && !scratch.NeedsChoice)
            DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: blackHoleAmt, isPoweredAttack: false);
    }

    internal static void Forge(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int amount)
        => HotRegentNecro.Forge(ref s, ref cold, rules, scratch, amount);

    internal static void SummonOsty(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int amount)
        => HotRegentNecro.SummonOsty(ref s, ref cold, rules, scratch, amount);

    /// <summary>
    /// Exact STS2 0.113.1 power application (Phase 1 compatibility overload).
    /// </summary>
    public static int ApplyPower(
        ref CombatHotState s,
        HotRules rules,
        byte ownerSlot,
        byte applierSlot,
        ushort powerDefId,
        int amount)
    {
        HotColdSidecar? dummyCold = null;
        return ApplyPowerCore(ref s, ref dummyCold, rules, scratch: null, ownerSlot, applierSlot, powerDefId, amount, isCardSource: false);
    }

    internal static int ApplyPower(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte ownerSlot,
        byte applierSlot,
        ushort powerDefId,
        int amount,
        bool isCardSource = false)
        => ApplyPowerCore(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, powerDefId, amount, isCardSource);

    internal static int ApplyPowerById(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte ownerSlot,
        byte applierSlot,
        HotPowerId powerId,
        int amount,
        bool isCardSource = false)
    {
        ushort defId = rules.GetPowerDefId(powerId);
        return defId != 0
            ? ApplyPowerCore(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, defId, amount, isCardSource)
            : 0;
    }

    internal static void ApplyPowerToAllLivingEnemies(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotPowerId powerId,
        int amount,
        bool isCardSource = false)
    {
        ushort defId = rules.GetPowerDefId(powerId);
        if (defId == 0 || amount == 0)
            return;
        Span<byte> targets = stackalloc byte[HotLimits.MaxEnemies];
        int count = SnapshotLivingEnemies(in s, targets);
        for (int i = 0; i < count; i++)
        {
            byte eSlot = targets[i];
            if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;
            ApplyPowerCore(ref s, ref cold, rules, scratch, (byte)(3 + eSlot), 0, defId, amount, isCardSource);
            if (scratch.NeedsChoice)
                return;
        }
    }

    internal static void ApplyTemporaryStat(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte ownerSlot,
        byte applierSlot,
        HotPowerId baseStatPowerId,
        HotPowerId tempPowerId,
        int signedDelta,
        bool isCardSource = false)
    {
        if (signedDelta == 0)
            return;
        ushort tempDefId = rules.GetPowerDefId(tempPowerId);
        if (tempDefId != 0
            && TryGetTemporaryStatInfo(tempPowerId, out HotPowerId mappedBaseStatId, out int mappedRetireSign)
            && mappedBaseStatId == baseStatPowerId
            && Math.Sign(signedDelta) == -mappedRetireSign)
        {
            ApplyPowerCore(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, tempDefId, Math.Abs(signedDelta), isCardSource);
            return;
        }
        ushort baseDefId = rules.GetPowerDefId(baseStatPowerId);
        int applied = baseDefId != 0
            ? ApplyPowerCore(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, baseDefId, signedDelta, isCardSource)
            : 0;
        if (applied != 0 && tempDefId != 0)
            ApplyPowerRaw(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, tempDefId, Math.Abs(signedDelta), isCardSource: false);
    }

    private static int ApplyPowerCore(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch? scratch,
        byte ownerSlot,
        byte applierSlot,
        ushort powerDefId,
        int amount,
        bool isCardSource)
    {
        if (amount == 0 || powerDefId == 0 || powerDefId >= rules.Powers.Length)
            return 0;

        HotPowerDef def = rules.Powers[powerDefId];
        HotPowerId pid = def.PowerId;

        // Automatic STS2 temporary-stat handling matching PowerCmd.cs:124-161,
        // UnsettlingLamp.cs:72-134,169-172, RuinedHelmet.cs:32-53, TemporaryStrengthPower.cs:134-137:
        // 1. Run pre-hooks (UnsettlingLamp) + Artifact negation on the temporary power FIRST -> tempAmt.
        //    If blocked (0), return 0 without touching the base stat power.
        // 2. Apply base stat with -retireSign * tempAmt and isCardSource: false (prevents UnsettlingLamp
        //    from double-dipping, while still letting RuinedHelmet double positive StrengthPower > 0).
        // 3. Apply/stack the temporary power with tempAmt skipping pre-hooks & Artifact (already resolved in step 1).
        if (amount > 0 && TryGetTemporaryStatInfo(pid, out HotPowerId baseStatId, out int retireSign))
        {
            if (!IsCreatureSlotValid(in s, ownerSlot))
                return 0;
            if ((GetCreatureRef(ref s, ownerSlot).Flags & HotCreatureFlags.Alive) == 0)
                return 0;

            int tempAmt = TryResolvePowerPreHooksAndArtifact(
                ref s, ref cold, rules, scratch, ownerSlot, applierSlot, powerDefId, def, pid, amount, isCardSource);
            if (tempAmt == 0)
                return 0;

            ushort baseDefId = rules.GetPowerDefId(baseStatId);
            if (baseDefId != 0)
                ApplyPowerRaw(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, baseDefId, -retireSign * tempAmt, isCardSource: false);

            return ApplyPowerRaw(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, powerDefId, tempAmt, isCardSource: false, skipPreHooksAndArtifact: true);
        }

        return ApplyPowerRaw(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, powerDefId, amount, isCardSource);
    }

    private static int TryResolvePowerPreHooksAndArtifact(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch? scratch,
        byte ownerSlot,
        byte applierSlot,
        ushort powerDefId,
        HotPowerDef def,
        HotPowerId pid,
        int amount,
        bool isCardSource)
    {
        // BeforePowerAmountChanged hook (SneckoSkull +1 Poison, UnsettlingLamp, RuinedHelmet, etc.)
        if (scratch != null && (s.ActiveHookMask & (ulong)HotHookMask.BeforePowerAmountChanged) != 0)
        {
            HotHookArgs preArgs = new()
            {
                ActorSlot = applierSlot,
                TargetSlot = ownerSlot,
                PowerDefId = powerDefId,
                Amount = amount,
                BoolFlag = true,
                IsCardBlock = isCardSource,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.BeforePowerAmountChanged, ref preArgs);
            if (!preArgs.BoolFlag || preArgs.Amount == 0)
                return 0;
            amount = preArgs.Amount;
        }

        bool allowNegative = pid is HotPowerId.StrengthPower or HotPowerId.DexterityPower or HotPowerId.FocusPower or HotPowerId.ShrinkPower
            || powerDefId == rules.StrengthPowerDefId
            || powerDefId == rules.DexterityPowerDefId;
        bool isDebuff = (def.Type == PowerType.Debuff && (amount > 0 || pid == HotPowerId.ShrinkPower))
            || (def.Type == PowerType.Buff && allowNegative && amount < 0);

        // Artifact negation for incoming debuffs.
        ushort artDefId = rules.ArtifactPowerDefId != 0 ? rules.ArtifactPowerDefId : rules.GetPowerDefId(HotPowerId.ArtifactPower);
        if (isDebuff && artDefId != 0)
        {
            int artifactIdx = FindPowerIndex(in s, ownerSlot, artDefId);
            if (artifactIdx >= 0 && s.Powers[artifactIdx].Amount > 0)
            {
                s.Powers[artifactIdx].Amount--;
                if (s.Powers[artifactIdx].Amount == 0)
                {
                    RemovePowerAt(ref s, artifactIdx);
                    s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
                }
                return 0;
            }
        }

        return amount;
    }

    private static int ApplyPowerRaw(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch? scratch,
        byte ownerSlot,
        byte applierSlot,
        ushort powerDefId,
        int amount,
        bool isCardSource,
        bool skipPreHooksAndArtifact = false)
    {
        if (amount == 0 || powerDefId == 0 || powerDefId >= rules.Powers.Length)
            return 0;
        if (!IsCreatureSlotValid(in s, ownerSlot))
            return 0;
        if ((GetCreatureRef(ref s, ownerSlot).Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        HotPowerDef def = rules.Powers[powerDefId];
        HotPowerId pid = def.PowerId;

        if (!skipPreHooksAndArtifact)
        {
            amount = TryResolvePowerPreHooksAndArtifact(
                ref s, ref cold, rules, scratch, ownerSlot, applierSlot, powerDefId, def, pid, amount, isCardSource);
            if (amount == 0)
                return 0;
        }

        bool allowNegative = pid is HotPowerId.StrengthPower or HotPowerId.DexterityPower or HotPowerId.FocusPower or HotPowerId.ShrinkPower
            || powerDefId == rules.StrengthPowerDefId
            || powerDefId == rules.DexterityPowerDefId;

        int existingIdx = def.InstanceType switch
        {
            PowerInstanceType.Instanced => -1,
            PowerInstanceType.InstancedPerApplier => FindPowerIndexByApplier(in s, ownerSlot, powerDefId, applierSlot),
            _ => FindPowerIndex(in s, ownerSlot, powerDefId),
        };
        int prevAmount = existingIdx >= 0 ? s.Powers[existingIdx].Amount : 0;
        if (pid == HotPowerId.MinionPower && existingIdx >= 0 && prevAmount > 0 && amount > 0)
            return 0;
        long sum = (long)prevAmount + amount;
        int newAmount = (int)Math.Clamp(sum, allowNegative ? -SettlementCeilingLong : 0L, SettlementCeilingLong);
        int delta = newAmount - prevAmount;

        if (newAmount == 0)
        {
            if (existingIdx >= 0)
            {
                RemovePowerAt(ref s, existingIdx);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            }
        }
        else if (existingIdx >= 0)
        {
            s.Powers[existingIdx].Amount = newAmount;
            if (amount > 0 && pid == HotPowerId.CrimsonMantlePower)
                s.Powers[existingIdx].Aux0++;
        }
        else
        {
            if (s.PowerCount >= HotLimits.MaxPowers)
                throw new HotCapacityExceededException($"Power count exceeded HotLimits.MaxPowers ({HotLimits.MaxPowers}).");

            HotPowerFlags flags = HotPowerFlags.None;
            if (def.InstanceType != PowerInstanceType.None)
                flags |= HotPowerFlags.Instanced;

            bool isPlayerSide = ownerSlot <= 2;
            if (isPlayerSide && s.CurrentSide == HotCombatSide.Enemy && def.HasRelevantSkipDurationTick)
                flags |= HotPowerFlags.SkipNextDurationTick;

            if ((powerDefId == rules.RitualPowerDefId || pid == HotPowerId.RitualPower)
                && newAmount > 0
                && ownerSlot >= 3
                && s.CurrentSide == HotCombatSide.Enemy)
            {
                flags |= HotPowerFlags.WasJustAppliedByEnemy;
            }

            // PanachePower.InitInternalData starts with alreadyApplied = false so the Panache card's
            // own AfterCardPlayed does not decrement CardsLeft from 5 -> 4 (PanachePower.cs:22,51-63).
            if (pid == HotPowerId.PanachePower)
                flags |= HotPowerFlags.BoolAux;

            // LullabyPower.AfterApplied (LullabyPower.cs:44-51): if Cho, Wah, and Ki were all already
            // played this turn when Lullaby is applied, ProccdThisTurn starts true.
            if (pid == HotPowerId.LullabyPower && ownerSlot == 0 && newAmount > 0 && HotFoolTarot.CountMelodies(in s) >= 3)
                flags |= HotPowerFlags.BoolAux;

            // Note: InfernoPower and EnnuiPower start at CanonicalVar 0 (SelfDamage=0 / Cards=0) and
            // are incremented by Inferno/Ennui OnPlay in HotCombatEngine.Bytecode.cs:197-202.
            int initAux0 = pid switch
            {
                HotPowerId.PanachePower => 5,
                HotPowerId.AutomationPower => 10,
                HotPowerId.CacophonyPower => 3,
                HotPowerId.CrimsonMantlePower => 1,
                HotPowerId.InsatiableDominionPower => 2 + s.PlayerTurnNumber,
                HotPowerId.WitheringPresencePower => 6,
                HotPowerId.JugglingPower => s.Counters[(int)HotCounterSlot.AttackPlayStartsThisTurn],
                HotPowerId.FeralPower => s.Counters[(int)HotCounterSlot.ZeroCostAttackStartsThisTurn],
                HotPowerId.PhantomBladesPower when ownerSlot == 0 && s.Counters[(int)HotCounterSlot.ShivPlaysFinishedThisTurn] > 0 => 1,
                _ => 0,
            };

            s.Powers[s.PowerCount++] = new HotPower
            {
                PowerDefId = powerDefId,
                OwnerSlot = ownerSlot,
                ApplierSlot = applierSlot,
                TargetSlot = ownerSlot,
                Flags = flags,
                AuxCardUid = 0,
                Amount = newAmount,
                AmountOnTurnStart = 0,
                Aux0 = initAux0,
                Aux1 = 0,
            };
            s.ActiveHookMask |= (ulong)def.HookMask;
        }

        if (pid == HotPowerId.DoomPower && delta > 0)
        {
            s.Counters[(int)HotCounterSlot.WasDoomAppliedThisTurn] = 1;
        }
        else if (pid == HotPowerId.FocusPower && ownerSlot == 0 && delta != 0)
        {
            SyncOrbsOnFocusChanged(ref s, rules, delta);
        }
        else if (ownerSlot == 0 && newAmount > 0 && pid is HotPowerId.HexPower or HotPowerId.TangledPower or HotPowerId.RingingPower)
        {
            ApplyPlayerPowerCardAffliction(ref s, rules, pid, newAmount);
        }
        else if (pid == HotPowerId.IllusionPower && newAmount > 0 && ownerSlot >= 3
            && GetPowerAmountById(in s, rules, ownerSlot, HotPowerId.MinionPower) <= 0)
        {
            // 0.113.1 IllusionPower.AfterApplied: automatically applies MinionPower(1) if not already present.
            ushort minionDefId = rules.GetPowerDefId(HotPowerId.MinionPower);
            if (minionDefId != 0)
                ApplyPowerRaw(ref s, ref cold, rules, scratch, ownerSlot, 0xFF, minionDefId, 1, isCardSource: false);
        }

        if (delta != 0 && scratch != null && (s.ActiveHookMask & (ulong)HotHookMask.AfterPowerAmountChanged) != 0)
        {
            HotHookArgs afterArgs = new()
            {
                ActorSlot = applierSlot,
                TargetSlot = ownerSlot,
                PowerDefId = powerDefId,
                Amount = delta,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterPowerAmountChanged, ref afterArgs);
        }

        return delta;
    }

    internal static int AddPowerInstance(
        ref CombatHotState s,
        HotRules rules,
        byte ownerSlot,
        byte applierSlot,
        ushort powerDefId,
        int amount,
        int aux0 = 0,
        int aux1 = 0,
        ushort auxCardUid = 0)
    {
        if (amount == 0 || powerDefId == 0 || powerDefId >= rules.Powers.Length)
            return -1;
        if (s.PowerCount >= HotLimits.MaxPowers)
            throw new HotCapacityExceededException($"Power count exceeded HotLimits.MaxPowers ({HotLimits.MaxPowers}).");

        HotPowerDef def = rules.Powers[powerDefId];
        int idx = s.PowerCount++;
        s.Powers[idx] = new HotPower
        {
            PowerDefId = powerDefId,
            OwnerSlot = ownerSlot,
            ApplierSlot = applierSlot,
            TargetSlot = ownerSlot,
            Flags = HotPowerFlags.Instanced,
            AuxCardUid = auxCardUid,
            Amount = amount,
            AmountOnTurnStart = 0,
            Aux0 = aux0,
            Aux1 = aux1,
        };
        s.ActiveHookMask |= (ulong)def.HookMask;
        return idx;
    }

    internal static void SetPowerAmount(
        ref CombatHotState s,
        HotRules rules,
        byte ownerSlot,
        ushort powerDefId,
        int newAmount)
    {
        if (powerDefId == 0 || powerDefId >= rules.Powers.Length)
            return;
        int idx = FindPowerIndex(in s, ownerSlot, powerDefId);
        if (idx < 0)
        {
            if (newAmount != 0)
                ApplyPower(ref s, rules, ownerSlot, ownerSlot, powerDefId, newAmount);
            return;
        }

        int prevAmount = s.Powers[idx].Amount;
        if (newAmount == 0)
        {
            RemovePowerAt(ref s, idx);
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
        }
        else
        {
            s.Powers[idx].Amount = newAmount;
        }

        if (ownerSlot == 0 && newAmount != prevAmount && rules.Powers[powerDefId].PowerId == HotPowerId.FocusPower)
            SyncOrbsOnFocusChanged(ref s, rules, newAmount - prevAmount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetPowerAmount(in CombatHotState s, byte ownerSlot, ushort powerDefId)
    {
        if (powerDefId == 0)
            return 0;
        int count = s.PowerCount;
        for (int i = 0; i < count; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot == ownerSlot && p.PowerDefId == powerDefId)
                return p.Amount;
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetPowerAmountById(in CombatHotState s, HotRules rules, byte ownerSlot, HotPowerId powerId)
    {
        ushort defId = rules.GetPowerDefId(powerId);
        return defId != 0 ? GetPowerAmount(in s, ownerSlot, defId) : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FindPowerIndex(in CombatHotState s, byte ownerSlot, ushort powerDefId)
    {
        if (powerDefId == 0)
            return -1;
        int count = s.PowerCount;
        for (int i = 0; i < count; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot == ownerSlot && p.PowerDefId == powerDefId)
                return i;
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FindPowerIndexByApplier(in CombatHotState s, byte ownerSlot, ushort powerDefId, byte applierSlot)
    {
        if (powerDefId == 0)
            return -1;
        int count = s.PowerCount;
        for (int i = 0; i < count; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot == ownerSlot && p.PowerDefId == powerDefId && p.ApplierSlot == applierSlot)
                return i;
        }
        return -1;
    }

    internal static void RemovePowerAt(ref CombatHotState s, int index)
    {
        int last = s.PowerCount - 1;
        for (int i = index; i < last; i++)
            s.Powers[i] = s.Powers[i + 1];
        s.Powers[last] = default;
        s.PowerCount = (byte)last;
    }

    internal static void RemoveAllPowersForOwner(ref CombatHotState s, byte ownerSlot)
    {
        int write = 0;
        int count = s.PowerCount;
        for (int read = 0; read < count; read++)
        {
            if (s.Powers[read].OwnerSlot != ownerSlot)
            {
                if (write != read)
                    s.Powers[write] = s.Powers[read];
                write++;
            }
        }
        for (int i = write; i < count; i++)
            s.Powers[i] = default;
        s.PowerCount = (byte)write;
    }

    internal static bool ShouldClearBlock(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte ownerSlot,
        out int maxBlockLossCap)
    {
        maxBlockLossCap = int.MaxValue;
        if ((s.ActiveHookMask & (ulong)HotHookMask.ShouldClearBlock) == 0)
            return true;

        HotHookArgs args = new()
        {
            TargetSlot = ownerSlot,
            BoolFlag = true,
            IntValue = int.MaxValue,
        };
        DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ShouldClearBlock, ref args);
        maxBlockLossCap = args.IntValue;
        return args.BoolFlag;
    }

    internal static void KillCreature(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot,
        bool force = false,
        ushort sourceCardDefId = 0,
        ushort sourceCardUid = 0)
    {
        if (!IsCreatureSlotValid(in s, targetSlot))
            return;
        if ((GetCreatureRef(ref s, targetSlot).Flags & HotCreatureFlags.Alive) == 0)
            return;

        // STS2 0.113.1 CreatureCmd.cs:605-610 (KillWithoutCheckingWinCondition):
        // unconditionally drop player HP to 0 and fire AfterCurrentHpChanged (AfterHpLost)
        // BEFORE Hook.ShouldDie / death-prevention revives (FairyInABottle, LizardTail).
        if (targetSlot == 0 && s.Player.CurrentHp > 0)
        {
            int preKillLoss = s.Player.CurrentHp;
            s.Player.CurrentHp = 0;
            if ((s.ActiveHookMask & (ulong)HotHookMask.AfterHpLost) != 0)
            {
                HotHookArgs hpArgs = new()
                {
                    TargetSlot = 0,
                    Amount = preKillLoss,
                };
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterHpLost, ref hpArgs);
            }
        }

        // Death prevention: Fairy in a Bottle (ShouldDie) before LizardTail (ShouldDieLate),
        // matching STS2 0.113.1 Hook.cs:2335-2355 and CreatureCmd.cs:613,675-679.
        if (!force && (targetSlot != 0 || s.Player.MaxHp > 0))
        {
            if (targetSlot == 0 && HotPotions.TryTriggerAutoFairy(ref s, ref cold, rules, scratch))
                return;

            if ((s.ActiveHookMask & (ulong)HotHookMask.ShouldDie) != 0)
            {
                HotHookArgs dieArgs = new()
                {
                    TargetSlot = targetSlot,
                    BoolFlag = true,
                };
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ShouldDie, ref dieArgs);
                if (!dieArgs.BoolFlag && (targetSlot != 0 || s.Player.CurrentHp > 0))
                    return;
            }
        }

        if (targetSlot == 0)
        {
            s.Player.CurrentHp = 0;
            s.Player.Flags &= ~HotCreatureFlags.Alive;
            RemoveAllPowersForOwner(ref s, 0);
            s.OrbCount = 0;
            if ((s.CombatFlags & HotCombatFlags.HasOsty) != 0 && (s.Osty.Flags & HotCreatureFlags.Alive) != 0)
            {
                s.Osty.CurrentHp = 0;
                s.Osty.Flags &= ~HotCreatureFlags.Alive;
                RemoveAllPowersForOwner(ref s, 1);
            }
            s.CombatFlags &= ~HotCombatFlags.PlayerHooksActive;
            s.CombatFlags |= HotCombatFlags.IsAboutToLose;
            s.TerminalOutcome = HotCombatOutcome.Defeat;
            s.TerminalTurn = (byte)Math.Clamp(s.PlayerTurnNumber, 0, byte.MaxValue);
            s.CombatFlags &= ~HotCombatFlags.IsInProgress;
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            return;
        }

        if (targetSlot == 1)
        {
            int ostyLoss = s.Osty.CurrentHp;
            s.Osty.CurrentHp = 0;
            s.Osty.Flags &= ~HotCreatureFlags.Alive;
            if (ostyLoss > 0 && (s.ActiveHookMask & (ulong)HotHookMask.AfterHpLost) != 0)
            {
                HotHookArgs ostyHpArgs = new()
                {
                    TargetSlot = 1,
                    Amount = ostyLoss,
                };
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterHpLost, ref ostyHpArgs);
            }
            RemoveDeadPetPowers(ref s, rules, ownerSlot: 1, keepPowerId: HotPowerId.DieForYouPower);
            HotRegentNecro.OnAnyCreatureDeath(ref s, ref cold, rules);
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            return;
        }

        if (targetSlot == 2)
        {
            int wuppyLoss = s.Wuppy.CurrentHp;
            s.Wuppy.CurrentHp = 0;
            s.Wuppy.Flags &= ~HotCreatureFlags.Alive;
            if (wuppyLoss > 0 && (s.ActiveHookMask & (ulong)HotHookMask.AfterHpLost) != 0)
            {
                HotHookArgs wuppyHpArgs = new()
                {
                    TargetSlot = 2,
                    Amount = wuppyLoss,
                };
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterHpLost, ref wuppyHpArgs);
            }
            RemoveDeadPetPowers(ref s, rules, ownerSlot: 2, keepPowerId: HotPowerId.WuppysLoyaltyPower);
            HotRegentNecro.OnAnyCreatureDeath(ref s, ref cold, rules);
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            return;
        }

        byte enemySlot = (byte)(targetSlot - 3);
        if (enemySlot >= s.EnemyCount)
            return;

        s.Enemies[enemySlot].Vital.CurrentHp = 0;
        s.Enemies[enemySlot].Vital.Flags &= ~HotCreatureFlags.Alive;

        // Dispatch enemy-specific death effects (Reattach, Adaptable, Infested, Surprise, Stock,
        // SteamEruption, Explosive, Minion, Queen/Amalgam, Dampen cleanup, roster update).
        HotMonsters.HandleEnemyDeathEffect(ref s, ref cold, rules, scratch, enemySlot);

        // WaterfallGiant SteamEruptionPower revives the enemy into AboutToBlow (still Alive,
        // preventing removal and combat end) — Vanilla CreatureCmd.cs:627-645 still fires
        // Hook.AfterDeath (Melancholy, ImpaledPower, ShowOffPower, CompostPower, GremlinHorn, etc.)
        // BEFORE RemoveAllPowersAfterDeath() for both SteamEruption and reviving (Reattach/Adaptable) enemies.
        bool deathPrevented = (s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0;

        // Card-level AfterDeath (Melancholy: -1 cost on any creature death where !wasRemovalPrevented).
        if (!scratch.NeedsChoice)
            HotRegentNecro.OnAnyCreatureDeath(ref s, ref cold, rules);

        // Dispatch AfterDeath hooks for player relics (e.g. GremlinHorn, BookRepairKnife) and powers
        // while dead/reviving/SteamEruption enemy powers (e.g. ImpaledPower) are still readable.
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterDeath) != 0 && !scratch.NeedsChoice)
        {
            HotHookArgs deathArgs = new()
            {
                TargetSlot = targetSlot,
                ActorSlot = 0,
                CardIdx = -1,
                CardDefId = sourceCardDefId,
                CardUid = sourceCardUid,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterDeath, ref deathArgs);
        }

        if (deathPrevented)
        {
            HotMonsters.RemovePowersAfterDeath(ref s, rules, targetSlot, removeAllBecauseDeparted: false);
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            return;
        }

        // Remove dead/reviving enemy's removable powers AFTER AfterDeath finishes (CreatureCmd.cs:641-645).
        // Per 0.113.1 AfterRemoved semantics: only clear Tainted if VitalSparkPower was actually removed from this dead enemy.
        // GalvanicPower has NO AfterRemoved (Galvanized persists), and SmoggyPower clears Smog only at player AfterSideTurnEnd.
        if (s.Enemies[enemySlot].DeathPhase == HotDeathPhase.Reviving)
        {
            HotMonsters.RemovePowersAfterDeath(ref s, rules, targetSlot, removeAllBecauseDeparted: false);
        }
        else
        {
            bool removedVitalSpark = RemoveDeadEnemyPowers(ref s, rules, enemySlot, targetSlot);
            if (removedVitalSpark)
                HotMonsters.ClearCardAffliction(ref s, rules, HotAfflictionId.Tainted);
        }

        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);

        if (!HasAnyPrimaryEnemyAliveOrReviving(in s, rules)
            && (s.Player.Flags & HotCreatureFlags.Alive) != 0
            && (s.CombatFlags & HotCombatFlags.BattlewornDummyTimedOut) == 0)
        {
            s.TerminalOutcome = HotCombatOutcome.Victory;
            s.TerminalTurn = (byte)Math.Clamp(s.PlayerTurnNumber, 0, byte.MaxValue);
        }
    }

    private static bool HasAnyPrimaryEnemyAliveOrReviving(in CombatHotState s, HotRules rules)
    {
        ushort minionDefId = rules.GetPowerDefId(HotPowerId.MinionPower);
        int enemyCount = s.EnemyCount;
        for (int e = 0; e < enemyCount; e++)
        {
            HotCreatureFlags flags = s.Enemies[e].Vital.Flags;
            bool aliveInRoster = (flags & (HotCreatureFlags.Alive | HotCreatureFlags.ActiveInRoster))
                == (HotCreatureFlags.Alive | HotCreatureFlags.ActiveInRoster);
            bool reviving = s.Enemies[e].DeathPhase == HotDeathPhase.Reviving;
            if (!aliveInRoster && !reviving)
                continue;
            if (minionDefId == 0 || GetPowerAmount(in s, (byte)(3 + e), minionDefId) <= 0)
                return true;
        }
        return false;
    }

    private static void RemoveDeadPetPowers(ref CombatHotState s, HotRules rules, byte ownerSlot, HotPowerId keepPowerId)
    {
        int i = 0;
        while (i < s.PowerCount)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot != ownerSlot)
            {
                i++;
                continue;
            }
            if (p.PowerDefId < rules.Powers.Length && rules.Powers[p.PowerDefId].PowerId == keepPowerId)
                i++;
            else
                RemovePowerAt(ref s, i);
        }
    }

    private static bool RemoveDeadEnemyPowers(ref CombatHotState s, HotRules rules, byte enemySlot, byte ownerSlot)
    {
        bool removedVitalSpark = false;
        bool departed = (s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.ActiveInRoster) == 0;
        if (departed)
        {
            for (int k = 0; k < s.PowerCount; k++)
            {
                ref readonly HotPower p = ref s.Powers[k];
                if (p.OwnerSlot == ownerSlot
                    && p.PowerDefId < rules.Powers.Length
                    && rules.Powers[p.PowerDefId].PowerId == HotPowerId.VitalSparkPower)
                {
                    removedVitalSpark = true;
                    break;
                }
            }
            RemoveAllPowersForOwner(ref s, ownerSlot);
            return removedVitalSpark;
        }

        int i = 0;
        while (i < s.PowerCount)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot != ownerSlot)
            {
                i++;
                continue;
            }

            HotPowerId pid = p.PowerDefId < rules.Powers.Length ? rules.Powers[p.PowerDefId].PowerId : HotPowerId.None;
            bool keep = pid is
                HotPowerId.AdaptablePower
                or HotPowerId.DieForYouPower
                or HotPowerId.IllusionPower
                or HotPowerId.MinionPower
                or HotPowerId.PainfulStabsPower
                or HotPowerId.ReattachPower
                or HotPowerId.SteamEruptionPower;
            if (keep)
            {
                i++;
            }
            else
            {
                if (pid == HotPowerId.VitalSparkPower)
                    removedVitalSpark = true;
                RemovePowerAt(ref s, i);
            }
        }
        return removedVitalSpark;
    }

    internal static void ApplyPlayerPowerCardAffliction(ref CombatHotState s, HotRules rules, HotPowerId powerId, int amount)
    {
        if (amount <= 0)
            return;

        if (powerId == HotPowerId.HexPower)
        {
            byte hexId = rules.GetAfflictionDefId(HotAfflictionId.Hexed);
            if (hexId == 0)
                return;
            short clamped = (short)Math.Clamp(amount, 0, short.MaxValue);
            int total = s.TotalCardCount;
            for (int c = 0; c < total; c++)
            {
                ref HotCard card = ref s.Cards[c];
                if (card.AfflId == 0)
                {
                    card.AfflId = hexId;
                    card.AfflAmt = clamped;
                }
            }
        }
        else if (powerId == HotPowerId.TangledPower)
        {
            byte entId = rules.GetAfflictionDefId(HotAfflictionId.Entangled);
            if (entId == 0)
                return;
            int total = s.TotalCardCount;
            for (int c = 0; c < total; c++)
            {
                ref HotCard card = ref s.Cards[c];
                if (card.AfflId == 0 && card.CardDefId < rules.Cards.Length && rules.Cards[card.CardDefId].Type == CardType.Attack)
                {
                    card.AfflId = entId;
                    card.AfflAmt = 1;
                }
            }
        }
        else if (powerId == HotPowerId.RingingPower)
        {
            byte ringId = rules.GetAfflictionDefId(HotAfflictionId.Ringing);
            if (ringId == 0)
                return;
            int total = s.TotalCardCount;
            for (int c = 0; c < total; c++)
            {
                ref HotCard card = ref s.Cards[c];
                if (card.AfflId == 0)
                {
                    card.AfflId = ringId;
                    card.AfflAmt = 1;
                }
            }
        }
    }

    /// <summary>
    /// Exact STS2 0.113.1 global+local Ethereal check (<c>CardModel.Keywords.Contains(Ethereal)</c>):
    /// true when the card has local <c>Ethereal</c> OR carries <c>Hexed</c> while player has <c>HexPower</c>
    /// (<c>HexPower.TryModifyKeywordsInCombat</c>, never mutating <c>LocalKeywordsMask</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsCardEthereal(in CombatHotState s, in HotCard card, HotRules rules)
    {
        if ((card.LocalKeywordsMask & HotCardKeywordMask.Ethereal) != 0)
            return true;
        if (card.AfflId != 0
            && card.AfflId < rules.Afflictions.Length
            && rules.Afflictions[card.AfflId].AfflictionId == HotAfflictionId.Hexed)
        {
            return GetPowerAmountById(in s, rules, 0, HotPowerId.HexPower) > 0;
        }
        return false;
    }

    /// <summary>
    /// Exact STS2 0.113.1 global+local Retain check (<c>CardModel.Keywords.Contains(Retain)</c>):
    /// true when the card has local <c>Retain</c> OR carries <c>CardTag.Shiv</c> while player has
    /// <c>PhantomBladesPower</c> (<c>PhantomBladesPower.TryModifyKeywordsInCombat</c>, never mutating <c>LocalKeywordsMask</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasEffectiveRetainKeyword(in CombatHotState s, in HotCard card, HotRules rules)
    {
        if ((card.LocalKeywordsMask & HotCardKeywordMask.Retain) != 0)
            return true;
        if (card.CardDefId > 0
            && card.CardDefId < rules.Cards.Length
            && rules.Cards[card.CardDefId].IsShivTag)
        {
            return GetPowerAmountById(in s, rules, 0, HotPowerId.PhantomBladesPower) > 0;
        }
        return false;
    }

    internal static void ClearHexedAffliction(ref CombatHotState s, HotRules rules)
    {
        int total = s.TotalCardCount;
        for (int c = 0; c < total; c++)
        {
            ref HotCard card = ref s.Cards[c];
            if (card.AfflId == 0 || card.AfflId >= rules.Afflictions.Length)
                continue;
            if (rules.Afflictions[card.AfflId].AfflictionId != HotAfflictionId.Hexed)
                continue;

            card.AfflId = 0;
            card.AfflAmt = 0;
        }
    }
}
