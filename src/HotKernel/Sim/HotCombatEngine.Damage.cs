using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Powers;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    private const decimal SettlementCeilingDecimal = 999_999_999m;
    private const long SettlementCeilingLong = 999_999_999L;

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

    internal static int GainBlockInternal(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot,
        int baseAmount,
        bool isCardBlock,
        int cardIdx,
        ushort cardDefId)
    {
        if (IsOverOrEnding(in s) || !IsCreatureSlotValid(in s, targetSlot))
            return 0;
        if ((GetCreatureRef(ref s, targetSlot).Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        decimal block = baseAmount;

        // Card enchantment additive block (Nimble / Steady)
        if (isCardBlock && cardIdx >= 0 && cardIdx < s.TotalCardCount && s.Cards[cardIdx].EnchId != 0)
        {
            byte eId = s.Cards[cardIdx].EnchId;
            if (eId < rules.Enchantments.Length)
            {
                HotEnchantmentId kind = rules.Enchantments[eId].EnchantmentId;
                if (kind == HotEnchantmentId.Nimble)
                    block += s.Cards[cardIdx].EnchAmt;
            }
        }

        // Additive pass (Dexterity, Fasten, Stretch, Relics)
        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyBlockAdditive) != 0)
        {
            HotHookArgs addArgs = new()
            {
                TargetSlot = targetSlot,
                CardIdx = cardIdx,
                CardDefId = cardDefId,
                IsCardBlock = isCardBlock,
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
                DecimalMultiplier = 1m,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyBlockMultiplicative, ref mulArgs);
            if (mulArgs.DecimalMultiplier >= decimal.MaxValue && block > 0m)
                block = SettlementCeilingDecimal;
            else
                block *= mulArgs.DecimalMultiplier;
        }

        if (block <= 0m)
            return 0;

        decimal floored = Math.Floor(block);
        if (floored <= 0m)
            return 0;

        int gained = (int)Math.Min(floored, SettlementCeilingDecimal);
        ref HotCreature target = ref GetCreatureRef(ref s, targetSlot);
        target.Block = (int)Math.Min((long)target.Block + gained, SettlementCeilingLong);

        if (isCardBlock && targetSlot == 0)
        {
            ushort unmovId = rules.GetPowerDefId(HotPowerId.UnmovablePower);
            if (unmovId != 0)
            {
                int uIdx = FindPowerIndex(in s, 0, unmovId);
                if (uIdx >= 0)
                    s.Powers[uIdx].Aux0++;
            }
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
        bool deferKill = false)
    {
        if (dealerSlot != 0xFF)
        {
            if (!IsCreatureSlotValid(in s, dealerSlot) || (GetCreatureRef(ref s, dealerSlot).Flags & HotCreatureFlags.Alive) == 0)
                return 0;
        }

        if (!IsCreatureSlotValid(in s, targetSlot))
            return 0;
        if ((GetCreatureRef(ref s, targetSlot).Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        decimal modifiedAmount = ComputeModifiedDamage(ref s, ref cold, rules, scratch, dealerSlot, targetSlot, baseDamage, isPoweredAttack, cardIdx, cardDefId);

        // Pre-block damage cap (Intangible, Slippery, HardenedShell)
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
            HotHookArgs beforeArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
                CardIdx = cardIdx,
                CardDefId = cardDefId,
                IsPoweredAttack = isPoweredAttack,
                IsUnblockable = isUnblockable,
                Amount = (int)Math.Clamp(modifiedAmount, 0m, SettlementCeilingDecimal),
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.BeforeDamageReceived, ref beforeArgs);
            if (scratch.NeedsChoice)
                return 0;
        }

        if ((GetCreatureRef(ref s, targetSlot).Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        decimal damageRemaining = modifiedAmount;

        // Pre-block Wuppy interception when player is targeted.
        if (targetSlot == 0
            && damageRemaining > 0m
            && (s.CombatFlags & HotCombatFlags.HasWuppy) != 0
            && (s.Wuppy.Flags & HotCreatureFlags.Alive) != 0)
        {
            int wuppyDmg = (int)Math.Clamp(damageRemaining, 0m, SettlementCeilingDecimal);
            int wuppyPrev = s.Wuppy.CurrentHp;
            bool wuppyKilled = wuppyPrev > 0 && damageRemaining >= wuppyPrev;
            s.Wuppy.CurrentHp = Math.Max(0, wuppyPrev - wuppyDmg);
            int wuppyOverkill = wuppyKilled ? Math.Max(wuppyDmg - wuppyPrev, 0) : 0;
            if (wuppyKilled || s.Wuppy.CurrentHp <= 0)
                s.Wuppy.Flags &= ~HotCreatureFlags.Alive;
            damageRemaining = wuppyOverkill;
        }

        // Block absorption (matches SimCreatureState.DamageBlock exact decimal subtraction).
        ref HotCreature targetRef = ref GetCreatureRef(ref s, targetSlot);
        int blockBefore = targetRef.Block;
        decimal blockedDamage = isUnblockable
            ? 0m
            : Math.Min((decimal)blockBefore, damageRemaining);
        int blockedInt = (int)blockedDamage;
        targetRef.Block -= blockedInt;
        damageRemaining = Math.Max(damageRemaining - blockedDamage, 0m);
        bool wasBlockBroken = blockBefore > 0 && targetRef.Block == 0 && !isUnblockable && blockedInt > 0;

        if (wasBlockBroken && (s.ActiveHookMask & (ulong)HotHookMask.AfterBlockBroken) != 0)
        {
            HotHookArgs brokenArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterBlockBroken, ref brokenArgs);
            if (scratch.NeedsChoice)
                return 0;
        }

        // Post-block Osty interception when player is targeted on powered attacks.
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
            int ostyOverkill = ostyKilled ? Math.Max(ostyDmg - ostyPrev, 0) : 0;
            if (ostyKilled || s.Osty.CurrentHp <= 0)
                s.Osty.Flags &= ~HotCreatureFlags.Alive;
            damageRemaining = ostyOverkill;
        }

        // Post-block / post-Osty HP loss cap (Buffer, BeatingRemnant, TungstenRod)
        if (damageRemaining > 0m && (s.ActiveHookMask & (ulong)HotHookMask.ModifyDamageCap) != 0)
        {
            HotHookArgs capPost = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
                IsPoweredAttack = isPoweredAttack,
                IsUnblockable = isUnblockable,
                BoolFlag = false, // post-block phase
                DecimalValue = damageRemaining,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ModifyDamageCap, ref capPost);
            damageRemaining = Math.Max(0m, capPost.DecimalValue);
        }

        // Target HP loss (matches SimCreatureState.LoseHp truncation).
        ref HotCreature target = ref GetCreatureRef(ref s, targetSlot);
        int hpPrev = target.CurrentHp;
        bool wasTargetKilled = hpPrev > 0 && damageRemaining >= hpPrev;
        int hpLoss = (int)Math.Clamp(damageRemaining, 0m, SettlementCeilingDecimal);
        target.CurrentHp = Math.Max(0, hpPrev - hpLoss);
        int unblockedDamage = hpPrev - target.CurrentHp;
        bool wasFullyBlocked = unblockedDamage == 0 && blockedInt > 0;

        if (unblockedDamage > 0)
        {
            if (targetSlot == 0)
            {
                s.HistoryUnblockedHitsReceived++;
                s.Counters[(int)HotCounterSlot.HasLostHpThisTurn] = 1;
                s.Counters[(int)HotCounterSlot.PlayerCumulativeHpLost] += unblockedDamage;
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

        // AfterDamageReceived hook
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterDamageReceived) != 0)
        {
            HotHookArgs recvArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
                CardIdx = cardIdx,
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
                return unblockedDamage;
        }

        // AfterDamageGiven hook
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterDamageGiven) != 0)
        {
            HotHookArgs givenArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = targetSlot,
                CardIdx = cardIdx,
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
                return unblockedDamage;
        }

        if (!deferKill && (wasTargetKilled || GetCreatureRef(ref s, targetSlot).CurrentHp <= 0))
            KillCreature(ref s, ref cold, rules, scratch, targetSlot, force: false);

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
        ushort cardDefId)
    {
        decimal damage = baseDamage;
        if (!isPoweredAttack)
            return Math.Max(0m, damage);

        // Card enchantment additive damage (Sharp / Momentum / Vigorous)
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
        c.MaxHp += amount;
        c.CurrentHp = Math.Min(c.MaxHp, c.CurrentHp + amount);
    }

    internal static void LoseMaxHp(
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
        int newMax = Math.Max(1, c.MaxHp - amount);
        if (c.CurrentHp > newMax)
        {
            DealDamageInternal(ref s, ref cold, rules, scratch, dealerSlot: 0xFF, targetSlot: targetSlot, baseDamage: c.CurrentHp - newMax, isPoweredAttack: false, isUnblockable: true, -1, 0);
            if (scratch.NeedsChoice)
                return;
        }
        GetCreatureRef(ref s, targetSlot).MaxHp = newMax;
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
    }

    internal static void Forge(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int amount)
    {
        if (IsOverOrEnding(in s) || scratch.NeedsChoice)
            return;

        ushort bladeDefId = rules.GetCardDefId(HotCustomCardId.SovereignBlade);
        if (bladeDefId == 0)
            return;

        bool hasUnexhaustedBlade = false;
        int total = s.TotalCardCount;
        int exhStart = s.ExhaustStart;
        int exhEnd = exhStart + s.ExhaustCount;
        for (int i = 0; i < total; i++)
        {
            if (i >= exhStart && i < exhEnd)
                continue;
            ref readonly HotCard c = ref s.Cards[i];
            if (c.CardDefId == bladeDefId && (c.Flags & HotCardFlags.IsDupe) == 0)
            {
                hasUnexhaustedBlade = true;
                break;
            }
        }

        if (!hasUnexhaustedBlade)
        {
            int idx = AddGeneratedFixedCard(ref s, ref cold, rules, scratch, bladeDefId, HotPileKind.Hand, -1, upgraded: false, createdByPlayer: true);
            if (idx >= 0)
                s.Cards[idx].Flags |= HotCardFlags.SovereignBladeCreatedThroughForge;
            if (scratch.NeedsChoice)
                return;
        }

        total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            ref HotCard c = ref s.Cards[i];
            if (c.CardDefId == bladeDefId && (c.Flags & HotCardFlags.IsDupe) == 0)
            {
                c.Var0Delta = (short)(c.Var0Delta + amount);
                c.CardPrivate += amount;
            }
        }
    }

    internal static void SummonOsty(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int amount)
    {
        if (amount <= 0 || IsOverOrEnding(in s))
            return;
        s.CombatFlags |= HotCombatFlags.HasOsty;
        if ((s.Osty.Flags & HotCreatureFlags.Alive) == 0)
        {
            s.Osty.Flags = HotCreatureFlags.Alive | HotCreatureFlags.IsOsty | HotCreatureFlags.IsPet;
            s.Osty.SlotIdx = 1;
            s.Osty.MaxHp = amount;
            s.Osty.CurrentHp = amount;
            s.Osty.Block = 0;
        }
        else
        {
            s.Osty.MaxHp += amount;
            s.Osty.CurrentHp = Math.Min(s.Osty.MaxHp, s.Osty.CurrentHp + amount);
        }
    }

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
        int signedDelta)
    {
        if (signedDelta == 0)
            return;
        ushort baseDefId = rules.GetPowerDefId(baseStatPowerId);
        ushort tempDefId = rules.GetPowerDefId(tempPowerId);
        int applied = baseDefId != 0
            ? ApplyPowerCore(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, baseDefId, signedDelta, isCardSource: false)
            : 0;
        if (applied != 0 && tempDefId != 0)
            ApplyPowerRaw(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, tempDefId, Math.Abs(applied), isCardSource: false);
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

        HotPowerId pid = rules.Powers[powerDefId].PowerId;

        // Automatic STS2 temporary-stat handling (e.g. FlexPotionPower -> +Strength + FlexPotionPower,
        // DarkShacklesPower/PiercingWailPower/ShacklingPotionPower -> -Strength + temp recovery power).
        if (amount > 0 && TryGetTemporaryStatInfo(pid, out HotPowerId baseStatId, out int retireSign))
        {
            int immediateDelta = -retireSign * amount;
            ushort baseDefId = rules.GetPowerDefId(baseStatId);
            int appliedStat = baseDefId != 0
                ? ApplyPowerRaw(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, baseDefId, immediateDelta, isCardSource)
                : 0;
            if (appliedStat == 0)
                return 0; // Blocked by Artifact
            return ApplyPowerRaw(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, powerDefId, Math.Abs(appliedStat), isCardSource: false);
        }

        return ApplyPowerRaw(ref s, ref cold, rules, scratch, ownerSlot, applierSlot, powerDefId, amount, isCardSource);
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
        bool isCardSource)
    {
        if (amount == 0 || powerDefId == 0 || powerDefId >= rules.Powers.Length)
            return 0;
        if (!IsCreatureSlotValid(in s, ownerSlot))
            return 0;
        if ((GetCreatureRef(ref s, ownerSlot).Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        HotPowerDef def = rules.Powers[powerDefId];
        HotPowerId pid = def.PowerId;

        // BeforePowerAmountChanged hook (SneckoSkull +1 Poison, UnsettlingLamp, etc.)
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

        bool allowNegative = pid is HotPowerId.StrengthPower or HotPowerId.DexterityPower or HotPowerId.FocusPower
            || powerDefId == rules.StrengthPowerDefId
            || powerDefId == rules.DexterityPowerDefId;
        bool isDebuff = (def.Type == PowerType.Debuff && amount > 0)
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

        int existingIdx = def.IsInstanced ? -1 : FindPowerIndex(in s, ownerSlot, powerDefId);
        int prevAmount = existingIdx >= 0 ? s.Powers[existingIdx].Amount : 0;
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
        }
        else
        {
            if (s.PowerCount >= HotLimits.MaxPowers)
                throw new HotCapacityExceededException($"Power count exceeded HotLimits.MaxPowers ({HotLimits.MaxPowers}).");

            HotPowerFlags flags = HotPowerFlags.None;
            if (def.IsInstanced)
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

            int initAux0 = pid switch
            {
                HotPowerId.PanachePower => 5,
                HotPowerId.AutomationPower => 10,
                HotPowerId.CacophonyPower => 3,
                HotPowerId.InfernoPower => 1,
                HotPowerId.CrimsonMantlePower => 1,
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
            s.Counters[(int)HotCounterSlot.WasDoomAppliedThisTurn] = 1;
        else if (pid == HotPowerId.FocusPower && ownerSlot == 0 && delta != 0)
            SyncOrbsOnFocusChanged(ref s, rules, delta);

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

        if (newAmount == 0)
        {
            RemovePowerAt(ref s, idx);
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
        }
        else
        {
            s.Powers[idx].Amount = newAmount;
        }
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
        bool force = false)
    {
        if (!IsCreatureSlotValid(in s, targetSlot))
            return;
        if ((GetCreatureRef(ref s, targetSlot).Flags & HotCreatureFlags.Alive) == 0)
            return;

        // Death prevention (LizardTail / ShouldDie hooks + Fairy in a Bottle auto-revive)
        if (!force)
        {
            if ((s.ActiveHookMask & (ulong)HotHookMask.ShouldDie) != 0)
            {
                HotHookArgs dieArgs = new()
                {
                    TargetSlot = targetSlot,
                    BoolFlag = true,
                };
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.ShouldDie, ref dieArgs);
                if (!dieArgs.BoolFlag)
                    return;
            }

            if (targetSlot == 0 && HotPotions.TryTriggerAutoFairy(ref s, rules))
                return;
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
            s.Osty.CurrentHp = 0;
            s.Osty.Flags &= ~HotCreatureFlags.Alive;
            RemoveAllPowersForOwner(ref s, 1);
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            return;
        }

        if (targetSlot == 2)
        {
            s.Wuppy.CurrentHp = 0;
            s.Wuppy.Flags &= ~HotCreatureFlags.Alive;
            RemoveAllPowersForOwner(ref s, 2);
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

        // If SteamEruption revived the enemy (still Alive), do not fire AfterDeath hooks or strip powers.
        if ((s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) != 0)
        {
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            return;
        }

        // Dispatch AfterDeath hooks for player relics (e.g. GremlinHorn, BookRepairKnife) and powers
        // while dead enemy powers (e.g. ImpaledPower) are still readable.
        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterDeath) != 0 && !scratch.NeedsChoice)
        {
            HotHookArgs deathArgs = new()
            {
                TargetSlot = targetSlot,
                ActorSlot = 0,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterDeath, ref deathArgs);
        }

        // Remove permanently dead enemy's removable powers (HotMonsters leaves them intact until AfterDeath finishes;
        // if enemy stays in ActiveEnemyRoster e.g. ReattachPower/AdaptablePower/PainfulStabs, preserve non-removable death powers).
        if (s.Enemies[enemySlot].DeathPhase != HotDeathPhase.Reviving)
            RemoveDeadEnemyPowers(ref s, rules, enemySlot, targetSlot);

        // Clear card afflictions whose source enemy/power was removed on death (VitalSpark -> Tainted,
        // Galvanic -> Galvanized, Hex/Tangled/Ringing via NormalizeCardAfflictions).
        HotMonsters.NormalizeCardAfflictions(ref s, rules);
        ClearOrphanedEnemyCardAfflictions(ref s, rules);

        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);

        if (!HasAnyLivingEnemy(in s) && !HasAnyRevivingEnemy(in s) && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
        {
            s.TerminalOutcome = HotCombatOutcome.Victory;
            s.TerminalTurn = (byte)Math.Clamp(s.PlayerTurnNumber, 0, byte.MaxValue);
        }
    }

    private static void RemoveDeadEnemyPowers(ref CombatHotState s, HotRules rules, byte enemySlot, byte ownerSlot)
    {
        bool departed = (s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.ActiveInRoster) == 0;
        if (departed)
        {
            RemoveAllPowersForOwner(ref s, ownerSlot);
            return;
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

            bool keep = p.PowerDefId < rules.Powers.Length
                && rules.Powers[p.PowerDefId].PowerId is
                    HotPowerId.AdaptablePower
                    or HotPowerId.DieForYouPower
                    or HotPowerId.IllusionPower
                    or HotPowerId.MinionPower
                    or HotPowerId.PainfulStabsPower
                    or HotPowerId.ReattachPower
                    or HotPowerId.SteamEruptionPower;
            if (keep)
                i++;
            else
                RemovePowerAt(ref s, i);
        }
    }

    private static void ClearOrphanedEnemyCardAfflictions(ref CombatHotState s, HotRules rules)
    {
        bool hasVitalSpark = false;
        bool hasGalvanic = false;
        bool hasSmoggy = false;
        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.Amount <= 0 || p.PowerDefId >= rules.Powers.Length)
                continue;
            if (!IsCreatureSlotValid(in s, p.OwnerSlot)
                || (GetCreatureRef(ref s, p.OwnerSlot).Flags & HotCreatureFlags.Alive) == 0)
            {
                continue;
            }
            HotPowerId pid = rules.Powers[p.PowerDefId].PowerId;
            if (pid == HotPowerId.VitalSparkPower) hasVitalSpark = true;
            else if (pid == HotPowerId.GalvanicPower) hasGalvanic = true;
            else if (pid == HotPowerId.SmoggyPower) hasSmoggy = true;
        }

        if (hasVitalSpark && hasGalvanic && hasSmoggy)
            return;

        int totalCards = s.TotalCardCount;
        for (int c = 0; c < totalCards; c++)
        {
            byte afflDefId = s.Cards[c].AfflId;
            if (afflDefId == 0 || afflDefId >= rules.Afflictions.Length)
                continue;
            HotAfflictionId affl = rules.Afflictions[afflDefId].AfflictionId;
            if ((!hasVitalSpark && affl == HotAfflictionId.Tainted)
                || (!hasGalvanic && affl == HotAfflictionId.Galvanized)
                || (!hasSmoggy && affl == HotAfflictionId.Smog))
            {
                s.Cards[c].AfflId = 0;
                s.Cards[c].AfflAmt = 0;
            }
        }
    }
}
