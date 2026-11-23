using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Powers;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    private const decimal SettlementCeilingDecimal = 999_999_999m;
    private const long SettlementCeilingLong = 999_999_999L;

    /// <summary>
    /// Exact STS2 0.113.1 block gain matching <c>CombatPredictionSimulator.GainBlock</c>,
    /// <c>DexterityPower</c>, <c>FrailPower</c>, and <c>SimCreatureState.GainBlock</c>.
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
            // Additive pass (Dexterity)
            if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyBlockAdditive) != 0 && rules.DexterityPowerDefId != 0)
            {
                int dex = GetPowerAmount(in s, targetSlot, rules.DexterityPowerDefId);
                block += dex;
            }

            // Multiplicative pass (Frail)
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
    /// Exact STS2 0.113.1 single-target damage resolution matching <c>CombatPredictionSimulator.Damage</c>,
    /// <c>HookMirrors.ModifyDamage</c>, <c>StrengthPower</c>, <c>WeakPower</c>, <c>VulnerablePower</c>,
    /// <c>ThornsPower</c>, <c>PaperPhrog</c>/<c>PaperKrane</c>/<c>OddMushroom</c>, Block/Wuppy/Osty interception,
    /// and death/roster compaction.
    /// </summary>
    public static int DealDamage(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        byte targetSlot,
        int baseDamage,
        bool isPoweredAttack)
        => DealDamage(ref s, ref cold, rules, scratch, dealerSlot, targetSlot, baseDamage, isPoweredAttack, isUnblockable: false);

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

        decimal modifiedAmount = ComputeModifiedDamage(in s, rules, dealerSlot, targetSlot, baseDamage, isPoweredAttack);

        // BeforeDamageReceived hook: ThornsPower deals Unpowered damage back to attacker on powered attacks.
        if (isPoweredAttack
            && dealerSlot != 0xFF
            && rules.ThornsPowerDefId != 0
            && (s.ActiveHookMask & (ulong)(HotHookMask.BeforeDamageReceived | HotHookMask.AfterDamageReceived)) != 0)
        {
            int thorns = GetPowerAmount(in s, targetSlot, rules.ThornsPowerDefId);
            if (thorns > 0)
            {
                DealDamage(
                    ref s,
                    ref cold,
                    rules,
                    scratch,
                    dealerSlot: targetSlot,
                    targetSlot: dealerSlot,
                    baseDamage: thorns,
                    isPoweredAttack: false,
                    isUnblockable: false);
            }
        }

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
        decimal blockedDamage = isUnblockable
            ? 0m
            : Math.Min((decimal)targetRef.Block, damageRemaining);
        targetRef.Block -= (int)blockedDamage;
        damageRemaining = Math.Max(damageRemaining - blockedDamage, 0m);

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

        // Target HP loss (matches SimCreatureState.LoseHp truncation).
        ref HotCreature target = ref GetCreatureRef(ref s, targetSlot);
        int hpPrev = target.CurrentHp;
        bool wasTargetKilled = hpPrev > 0 && damageRemaining >= hpPrev;
        int hpLoss = (int)Math.Clamp(damageRemaining, 0m, SettlementCeilingDecimal);
        target.CurrentHp = Math.Max(0, hpPrev - hpLoss);
        int unblockedDamage = hpPrev - target.CurrentHp;

        if (unblockedDamage > 0)
        {
            if (targetSlot == 0)
            {
                s.HistoryUnblockedHitsReceived++;
                s.Counters[(int)HotCounterSlot.HasLostHpThisTurn] = 1;
                s.Counters[(int)HotCounterSlot.PlayerCumulativeHpLost] += unblockedDamage;
            }
            scratch.UnblockedDamageThisPlay = true;
        }

        if (isPoweredAttack && dealerSlot == 0 && targetSlot >= 3)
            s.Enemies[targetSlot - 3].PoweredHitsFromPlayer++;

        if (wasTargetKilled || target.CurrentHp <= 0)
            HandleCreatureDeath(ref s, rules, targetSlot);

        return unblockedDamage;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static decimal ComputeModifiedDamage(
        in CombatHotState s,
        HotRules rules,
        byte dealerSlot,
        byte targetSlot,
        int baseDamage,
        bool isPoweredAttack)
    {
        decimal damage = baseDamage;
        if (!isPoweredAttack)
            return Math.Max(0m, damage);

        // Additive pass (Strength)
        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyDamageAdditive) != 0
            && dealerSlot != 0xFF
            && rules.StrengthPowerDefId != 0)
        {
            damage += GetPowerAmount(in s, dealerSlot, rules.StrengthPowerDefId);
        }

        // Multiplicative pass (Weak, Vulnerable, PaperPhrog / PaperFrog / PaperKrane / OddMushroom)
        if ((s.ActiveHookMask & (ulong)HotHookMask.ModifyDamageMultiplicative) != 0)
        {
            if (dealerSlot != 0xFF
                && rules.WeakPowerDefId != 0
                && GetPowerAmount(in s, dealerSlot, rules.WeakPowerDefId) > 0)
            {
                decimal weakMult = 0.75m;
                if (targetSlot == 0 && HasActiveRelic(rules, "PAPER_KRANE"))
                    weakMult -= 0.15m;
                damage *= weakMult;
            }

            if (rules.VulnerablePowerDefId != 0
                && GetPowerAmount(in s, targetSlot, rules.VulnerablePowerDefId) > 0)
            {
                decimal vulnMult = 1.5m;
                if (dealerSlot == 0 && targetSlot != 0)
                {
                    if (rules.HasPaperFrog || HasActiveRelic(rules, "PAPER_PHROG"))
                        vulnMult += 0.25m;
                }
                else if (targetSlot == 0 && rules.HasOddMushroom)
                {
                    vulnMult -= 0.25m;
                }
                damage *= vulnMult;
            }
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

    /// <summary>
    /// Exact STS2 0.113.1 power application matching <c>SimulatedCombatState.Apply</c>:
    /// Artifact negation for debuffs, stacking/removal at 0, <c>SkipNextDurationTick</c> on newly
    /// enemy-applied Weak/Vulnerable/Frail to player, <c>WasJustAppliedByEnemy</c> on Ritual,
    /// and <c>ActiveHookMask</c> synchronization.
    /// </summary>
    public static int ApplyPower(
        ref CombatHotState s,
        HotRules rules,
        byte ownerSlot,
        byte applierSlot,
        ushort powerDefId,
        int amount)
    {
        if (amount == 0 || powerDefId == 0 || powerDefId >= rules.Powers.Length)
            return 0;
        if (!IsCreatureSlotValid(in s, ownerSlot))
            return 0;
        if ((GetCreatureRef(ref s, ownerSlot).Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        HotPowerDef def = rules.Powers[powerDefId];
        bool allowNegative = powerDefId == rules.StrengthPowerDefId || powerDefId == rules.DexterityPowerDefId;
        bool isDebuff = (def.Type == PowerType.Debuff && amount > 0)
            || (def.Type == PowerType.Buff && allowNegative && amount < 0);

        // Artifact negation for incoming debuffs.
        if (isDebuff && rules.ArtifactPowerDefId != 0)
        {
            int artifactIdx = FindPowerIndex(in s, ownerSlot, rules.ArtifactPowerDefId);
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

        if (newAmount == 0)
        {
            if (existingIdx >= 0)
            {
                RemovePowerAt(ref s, existingIdx);
                s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            }
            return -prevAmount;
        }

        if (existingIdx >= 0)
        {
            // Stacking on an existing power never renews SkipNextDurationTick or Ritual delay.
            s.Powers[existingIdx].Amount = newAmount;
            return newAmount - prevAmount;
        }

        if (s.PowerCount >= HotLimits.MaxPowers)
            throw new HotCapacityExceededException($"Power count exceeded HotLimits.MaxPowers ({HotLimits.MaxPowers}).");

        HotPowerFlags flags = HotPowerFlags.None;
        if (def.IsInstanced)
            flags |= HotPowerFlags.Instanced;

        // Newly enemy-applied player duration debuffs (Weak/Vulnerable/Frail) skip their first tick
        // at the end of the same enemy turn.
        bool isPlayerSide = ownerSlot <= 2;
        if (isPlayerSide && s.CurrentSide == HotCombatSide.Enemy && def.HasRelevantSkipDurationTick)
            flags |= HotPowerFlags.SkipNextDurationTick;

        // Enemy-applied Ritual skips its first Strength gain at the end of the same enemy turn.
        if (powerDefId == rules.RitualPowerDefId
            && newAmount > 0
            && ownerSlot >= 3
            && s.CurrentSide == HotCombatSide.Enemy)
        {
            flags |= HotPowerFlags.WasJustAppliedByEnemy;
        }

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
            Aux0 = 0,
            Aux1 = 0,
        };
        s.ActiveHookMask |= (ulong)def.HookMask;
        return newAmount;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ShouldClearBlock(in CombatHotState s, HotRules rules, byte ownerSlot)
    {
        if ((s.ActiveHookMask & (ulong)HotHookMask.ShouldClearBlock) == 0)
            return true;
        if (rules.BarricadePowerDefId != 0 && GetPowerAmount(in s, ownerSlot, rules.BarricadePowerDefId) > 0)
            return false;
        return true;
    }

    private static void HandleCreatureDeath(ref CombatHotState s, HotRules rules, byte targetSlot)
    {
        if (targetSlot == 0)
        {
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
            s.Osty.Flags &= ~HotCreatureFlags.Alive;
            RemoveAllPowersForOwner(ref s, 1);
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            return;
        }

        if (targetSlot == 2)
        {
            s.Wuppy.Flags &= ~HotCreatureFlags.Alive;
            RemoveAllPowersForOwner(ref s, 2);
            s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);
            return;
        }

        byte enemySlot = (byte)(targetSlot - 3);
        if (enemySlot >= s.EnemyCount)
            return;

        ref HotEnemy enemy = ref s.Enemies[enemySlot];
        enemy.Vital.Flags &= ~(HotCreatureFlags.Alive | HotCreatureFlags.ActiveInRoster);
        enemy.DeathPhase = HotDeathPhase.PermanentlyDead;

        // Compact ActiveEnemyRoster while preserving exact remaining enemy order.
        int rosterCount = s.RosterCount;
        int write = 0;
        for (int read = 0; read < rosterCount; read++)
        {
            byte slot = s.ActiveEnemyRoster[read];
            if (slot != enemySlot)
                s.ActiveEnemyRoster[write++] = slot;
        }
        for (int i = write; i < rosterCount; i++)
            s.ActiveEnemyRoster[i] = 0;
        s.RosterCount = (byte)write;

        RemoveAllPowersForOwner(ref s, targetSlot);
        s.ActiveHookMask = RecomputeActiveHookMask(in s, rules);

        if (!HasAnyLivingEnemy(in s) && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
        {
            s.TerminalOutcome = HotCombatOutcome.Victory;
            s.TerminalTurn = (byte)Math.Clamp(s.PlayerTurnNumber, 0, byte.MaxValue);
        }
    }
}
