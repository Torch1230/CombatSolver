using System.Runtime.CompilerServices;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static HotOrbId GetOrbId(HotRules rules, byte orbTypeId)
    {
        if (orbTypeId == 0)
            return HotOrbId.None;
        if (orbTypeId < rules.Orbs.Length)
        {
            HotOrbId id = rules.Orbs[orbTypeId].OrbId;
            if (id != HotOrbId.None)
                return id;
        }
        return orbTypeId <= (byte)HotOrbId.Glass ? (HotOrbId)orbTypeId : HotOrbId.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte ResolveOrbTypeId(HotRules rules, HotOrbId orbId)
    {
        if (orbId == HotOrbId.None)
            return 0;
        byte defId = rules.GetOrbDefId(orbId);
        if (defId != 0)
            return defId;
        for (int i = 1; i < rules.Orbs.Length; i++)
        {
            if (rules.Orbs[i].OrbId == orbId)
                return (byte)i;
        }
        return (byte)orbId;
    }

    internal static void AddOrbSlots(ref CombatHotState s, int amount)
    {
        if (amount <= 0)
            return;
        int nextCap = Math.Min(HotLimits.MaxOrbs, s.OrbCapacity + amount);
        s.OrbCapacity = (byte)nextCap;
    }

    internal static void RemoveOrbSlots(ref CombatHotState s, int amount)
    {
        if (amount <= 0)
            return;
        int nextCap = Math.Max(0, s.OrbCapacity - amount);
        s.OrbCapacity = (byte)nextCap;
        while (s.OrbCount > nextCap)
        {
            s.OrbCount--;
            s.Orbs[s.OrbCount] = default;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ModifyOrbValue(in CombatHotState s, HotRules rules, HotOrbId orbId, int baseValue)
    {
        if (orbId == HotOrbId.Plasma)
            return Math.Max(0, baseValue);

        ushort focusDefId = rules.GetPowerDefId(HotPowerId.FocusPower);
        int focus = focusDefId != 0 ? GetPowerAmount(in s, ownerSlot: 0, focusDefId) : 0;
        // 0.113.1 CombatState.IterateHookListeners: player Powers (FocusPower clamps >= 0) run before player Relics (InfusedCore +1 on LightningOrb).
        int value = Math.Max(0, baseValue + focus);
        if (orbId == HotOrbId.Lightning && rules.HasRelic(HotRelicId.InfusedCore))
            value += 1;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetOrbPassiveValue(in CombatHotState s, HotRules rules, in HotOrb orb)
    {
        HotOrbId orbId = GetOrbId(rules, orb.OrbTypeId);
        return orbId switch
        {
            HotOrbId.Lightning => ModifyOrbValue(in s, rules, orbId, 3),
            HotOrbId.Frost => ModifyOrbValue(in s, rules, orbId, 2),
            HotOrbId.Dark => ModifyOrbValue(in s, rules, orbId, 6),
            HotOrbId.Plasma => orb.PassiveVal > 0 ? orb.PassiveVal : 1,
            HotOrbId.Glass => ModifyOrbValue(in s, rules, orbId, Math.Max(0, (int)orb.PassiveVal)),
            _ => 0,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetOrbEvokeValue(in CombatHotState s, HotRules rules, in HotOrb orb)
    {
        HotOrbId orbId = GetOrbId(rules, orb.OrbTypeId);
        return orbId switch
        {
            HotOrbId.Lightning => ModifyOrbValue(in s, rules, orbId, 8),
            HotOrbId.Frost => ModifyOrbValue(in s, rules, orbId, 5),
            HotOrbId.Dark => Math.Max(0, orb.EvokeVal),
            HotOrbId.Plasma => orb.EvokeVal > 0 ? orb.EvokeVal : 2,
            HotOrbId.Glass => ModifyOrbValue(in s, rules, orbId, Math.Max(0, (int)orb.PassiveVal)) * 2,
            _ => 0,
        };
    }

    internal static void SyncOrbsOnFocusChanged(ref CombatHotState s, HotRules rules, int deltaFocus)
    {
        if (deltaFocus == 0 || s.OrbCount == 0)
            return;

        int count = s.OrbCount;
        for (int i = 0; i < count; i++)
        {
            HotOrbId orbId = GetOrbId(rules, s.Orbs[i].OrbTypeId);
            switch (orbId)
            {
                case HotOrbId.Lightning:
                    s.Orbs[i].PassiveVal = (short)ModifyOrbValue(in s, rules, orbId, 3);
                    s.Orbs[i].EvokeVal = ModifyOrbValue(in s, rules, orbId, 8);
                    break;

                case HotOrbId.Frost:
                    s.Orbs[i].PassiveVal = (short)ModifyOrbValue(in s, rules, orbId, 2);
                    s.Orbs[i].EvokeVal = ModifyOrbValue(in s, rules, orbId, 5);
                    break;

                case HotOrbId.Dark:
                    s.Orbs[i].PassiveVal = (short)ModifyOrbValue(in s, rules, orbId, 6);
                    break;

                case HotOrbId.Glass:
                    // GlassOrb.cs:15,21,23: PassiveVal stores unmodified base _passiveVal; Focus applies on read.
                    s.Orbs[i].EvokeVal = ModifyOrbValue(in s, rules, orbId, Math.Max(0, (int)s.Orbs[i].PassiveVal)) * 2;
                    break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CountDistinctOrbTypes(in CombatHotState s)
    {
        uint seenMask = 0;
        int distinct = 0;
        int count = s.OrbCount;
        for (int i = 0; i < count; i++)
        {
            byte typeId = s.Orbs[i].OrbTypeId;
            if (typeId == 0)
                continue;
            uint bit = 1u << (typeId & 31);
            if ((seenMask & bit) == 0)
            {
                seenMask |= bit;
                distinct++;
            }
        }
        return distinct;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CountOrbsOfKind(in CombatHotState s, HotRules rules, HotOrbId targetKind)
    {
        int found = 0;
        int count = s.OrbCount;
        for (int i = 0; i < count; i++)
        {
            if (GetOrbId(rules, s.Orbs[i].OrbTypeId) == targetKind)
                found++;
        }
        return found;
    }

    internal static bool ChannelOrb(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotOrbId orbId)
    {
        byte orbTypeId = ResolveOrbTypeId(rules, orbId);
        return ChannelOrb(ref s, ref cold, rules, scratch, orbTypeId);
    }

    internal static bool ChannelOrb(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte orbTypeId)
    {
        if (IsOverOrEnding(in s) || scratch.NeedsChoice || orbTypeId == 0)
            return false;

        // Vanilla OrbCmd.Channel (OrbCmd.cs:74-77) grants 1 slot only when both BaseOrbSlotCount == 0
        // and current capacity == 0; Defect (BaseOrbSlotCount == 3) reduced to 0 slots drops channeled orbs.
        if (s.OrbCapacity == 0 && rules.BaseOrbSlotCount == 0)
            s.OrbCapacity = 1;

        if (s.OrbCapacity == 0)
            return false;

        if (s.OrbCount >= s.OrbCapacity)
        {
            EvokeOrb(ref s, ref cold, rules, scratch, orbIdx: 0, repeat: 1, dequeue: true);
            if (scratch.NeedsChoice || IsOverOrEnding(in s))
                return false;
            if (s.OrbCount >= s.OrbCapacity)
                return false;
        }

        if (s.OrbCount >= HotLimits.MaxOrbs)
            return false;

        HotOrbId orbId = GetOrbId(rules, orbTypeId);
        short basePassive;
        int baseEvoke;
        if (orbTypeId < rules.Orbs.Length && (rules.Orbs[orbTypeId].BasePassiveVal > 0 || rules.Orbs[orbTypeId].BaseEvokeVal > 0))
        {
            basePassive = rules.Orbs[orbTypeId].BasePassiveVal;
            baseEvoke = rules.Orbs[orbTypeId].BaseEvokeVal;
        }
        else
        {
            (basePassive, baseEvoke) = orbId switch
            {
                HotOrbId.Lightning => ((short)3, 8),
                HotOrbId.Frost => ((short)2, 5),
                HotOrbId.Dark => ((short)6, 6),
                HotOrbId.Plasma => ((short)1, 2),
                HotOrbId.Glass => ((short)4, 8),
                _ => ((short)0, 0),
            };
        }

        short effPassive;
        int effEvoke;
        switch (orbId)
        {
            case HotOrbId.Lightning:
                effPassive = (short)ModifyOrbValue(in s, rules, orbId, basePassive);
                effEvoke = ModifyOrbValue(in s, rules, orbId, baseEvoke);
                break;
            case HotOrbId.Frost:
                effPassive = (short)ModifyOrbValue(in s, rules, orbId, basePassive);
                effEvoke = ModifyOrbValue(in s, rules, orbId, baseEvoke);
                break;
            case HotOrbId.Dark:
                effPassive = (short)ModifyOrbValue(in s, rules, orbId, basePassive);
                effEvoke = baseEvoke;
                break;
            case HotOrbId.Glass:
                // GlassOrb.cs:15,21,23: store unmodified base _passiveVal (4); Focus applies on read.
                effPassive = basePassive;
                effEvoke = ModifyOrbValue(in s, rules, orbId, Math.Max(0, (int)basePassive)) * 2;
                break;
            default:
                effPassive = basePassive;
                effEvoke = baseEvoke;
                break;
        }

        s.Orbs[s.OrbCount++] = new HotOrb
        {
            OrbTypeId = orbTypeId,
            Flags = 0,
            PassiveVal = effPassive,
            EvokeVal = effEvoke,
        };

        if (orbId == HotOrbId.Lightning)
            s.HistoryLightningChannels++;

        if ((s.ActiveHookMask & (ulong)HotHookMask.AfterOrbChanneled) != 0)
        {
            HotHookArgs args = new()
            {
                ActorSlot = 0,
                OrbTypeId = orbTypeId,
            };
            DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterOrbChanneled, ref args);
        }

        return !scratch.NeedsChoice;
    }

    internal static void ChannelRandomOrb(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            if (IsOverOrEnding(in s) || scratch.NeedsChoice)
                return;
            // Matches OrbModel._validOrbs order: Lightning, Frost, Dark, Plasma, Glass.
            int pick = HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatOrbGeneration], 5);
            HotOrbId orbId = (HotOrbId)(pick + 1);
            ChannelOrb(ref s, ref cold, rules, scratch, orbId);
            if (IsOverOrEnding(in s) || scratch.NeedsChoice)
                return;
        }
    }

    internal static void EvokeOrb(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int orbIdx = 0,
        int repeat = 1,
        bool dequeue = true)
    {
        if (IsOverOrEnding(in s) || scratch.NeedsChoice || repeat <= 0)
            return;
        if (s.OrbCount == 0 || (uint)orbIdx >= (uint)s.OrbCount)
            return;

        HotOrb orbSnapshot = s.Orbs[orbIdx];

        for (int r = 0; r < repeat; r++)
        {
            if (IsOverOrEnding(in s) || scratch.NeedsChoice)
                return;

            bool shouldDequeueNow = dequeue && r == repeat - 1;
            if (shouldDequeueNow && (uint)orbIdx < (uint)s.OrbCount)
            {
                int last = s.OrbCount - 1;
                for (int k = orbIdx; k < last; k++)
                    s.Orbs[k] = s.Orbs[k + 1];
                s.Orbs[last] = default;
                s.OrbCount = (byte)last;
            }

            HotOrbId orbId = GetOrbId(rules, orbSnapshot.OrbTypeId);
            byte evokedTargetSlot = 0xFF;
            int evokeVal = GetOrbEvokeValue(in s, rules, in orbSnapshot);

            switch (orbId)
            {
                case HotOrbId.Lightning:
                {
                    int enemySlot = PickRandomLivingEnemySlot(ref s);
                    if (enemySlot >= 0)
                    {
                        evokedTargetSlot = (byte)(3 + enemySlot);
                        DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: evokedTargetSlot, baseDamage: evokeVal, isPoweredAttack: false);
                    }
                    break;
                }

                case HotOrbId.Frost:
                {
                    evokedTargetSlot = 0;
                    GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: evokeVal, isCardBlock: false);
                    break;
                }

                case HotOrbId.Dark:
                {
                    int enemySlot = PickLowestHpLivingEnemySlot(in s);
                    if (enemySlot >= 0)
                    {
                        evokedTargetSlot = (byte)(3 + enemySlot);
                        DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: evokedTargetSlot, baseDamage: evokeVal, isPoweredAttack: false);
                    }
                    break;
                }

                case HotOrbId.Plasma:
                {
                    evokedTargetSlot = 0;
                    GainEnergy(ref s, rules, evokeVal);
                    break;
                }

                case HotOrbId.Glass:
                {
                    if (evokeVal > 0)
                        DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: evokeVal, isPoweredAttack: false);
                    break;
                }
            }

            if (scratch.NeedsChoice)
                return;

            if ((s.ActiveHookMask & (ulong)HotHookMask.AfterOrbEvoked) != 0)
            {
                HotHookArgs args = new()
                {
                    ActorSlot = 0,
                    TargetSlot = evokedTargetSlot,
                    OrbTypeId = orbSnapshot.OrbTypeId,
                    OrbValue = evokeVal,
                };
                DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterOrbEvoked, ref args);
                if (scratch.NeedsChoice)
                    return;
            }
        }
    }

    internal static void TriggerOrbPassive(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int orbIdx,
        int forcedTargetEnemySlot = -1,
        bool applyTriggerCountMods = true)
    {
        if (IsOverOrEnding(in s) || scratch.NeedsChoice)
            return;
        if ((uint)orbIdx >= (uint)s.OrbCount)
            return;

        int triggerCount = 1;
        if (applyTriggerCountMods && orbIdx == 0 && rules.HasRelic(HotRelicId.GoldPlatedCables))
            triggerCount += 1;

        for (int t = 0; t < triggerCount; t++)
        {
            if (IsOverOrEnding(in s) || scratch.NeedsChoice || (uint)orbIdx >= (uint)s.OrbCount)
                return;

            HotOrbId orbId = GetOrbId(rules, s.Orbs[orbIdx].OrbTypeId);
            switch (orbId)
            {
                case HotOrbId.Lightning:
                {
                    // LightningOrb.cs:52, TeslaCoil.cs:35, CreatureCmd.cs:305: explicit target does not retarget
                    // or consume CombatTargets RNG if dead/unhittable (deals 0).
                    int enemySlot = forcedTargetEnemySlot >= 0
                        ? (IsValidEnemyTarget(in s, forcedTargetEnemySlot) ? forcedTargetEnemySlot : -1)
                        : PickRandomLivingEnemySlot(ref s);
                    if (enemySlot >= 0)
                    {
                        int dmg = ModifyOrbValue(in s, rules, orbId, 3);
                        DealDamage(ref s, ref cold, rules, scratch, dealerSlot: 0, targetSlot: (byte)(3 + enemySlot), baseDamage: dmg, isPoweredAttack: false);
                    }
                    break;
                }

                case HotOrbId.Frost:
                {
                    int blk = ModifyOrbValue(in s, rules, orbId, 2);
                    GainBlock(ref s, ref cold, rules, scratch, targetSlot: 0, baseAmount: blk, isCardBlock: false);
                    break;
                }

                case HotOrbId.Dark:
                {
                    int add = ModifyOrbValue(in s, rules, orbId, 6);
                    s.Orbs[orbIdx].EvokeVal += add;
                    break;
                }

                case HotOrbId.Plasma:
                {
                    int energy = s.Orbs[orbIdx].PassiveVal > 0 ? s.Orbs[orbIdx].PassiveVal : 1;
                    GainEnergy(ref s, rules, energy);
                    break;
                }

                case HotOrbId.Glass:
                {
                    // GlassOrb.cs:15,21,23,30-40: evaluate Focus-adjusted PassiveVal first; if > 0,
                    // decrement unmodified base _passiveVal (clamped >= 0) before dealing damage.
                    int basePassive = Math.Max(0, (int)s.Orbs[orbIdx].PassiveVal);
                    int effectivePassive = ModifyOrbValue(in s, rules, orbId, basePassive);
                    if (effectivePassive <= 0)
                        break;
                    short nextBasePassive = (short)Math.Max(0, basePassive - 1);
                    s.Orbs[orbIdx].PassiveVal = nextBasePassive;
                    s.Orbs[orbIdx].EvokeVal = ModifyOrbValue(in s, rules, orbId, nextBasePassive) * 2;
                    DamageAllEnemies(ref s, ref cold, rules, scratch, dealerSlot: 0, baseDamage: effectivePassive, isPoweredAttack: false);
                    break;
                }
            }
        }
    }

    internal static void TriggerOrbsBeforeTurnEnd(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        int count = s.OrbCount;
        for (int i = 0; i < count; i++)
        {
            if (IsOverOrEnding(in s) || scratch.NeedsChoice || i >= s.OrbCount)
                return;

            HotOrbId orbId = GetOrbId(rules, s.Orbs[i].OrbTypeId);
            if (orbId is HotOrbId.Lightning or HotOrbId.Frost or HotOrbId.Dark or HotOrbId.Glass)
                TriggerOrbPassive(ref s, ref cold, rules, scratch, i, forcedTargetEnemySlot: -1, applyTriggerCountMods: true);
        }
    }

    internal static void TriggerOrbsAfterTurnStart(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch)
    {
        int count = s.OrbCount;
        for (int i = 0; i < count; i++)
        {
            if (IsOverOrEnding(in s) || scratch.NeedsChoice || i >= s.OrbCount)
                return;

            HotOrbId orbId = GetOrbId(rules, s.Orbs[i].OrbTypeId);
            if (orbId == HotOrbId.Plasma)
                TriggerOrbPassive(ref s, ref cold, rules, scratch, i, forcedTargetEnemySlot: -1, applyTriggerCountMods: true);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PickLowestHpLivingEnemySlot(in CombatHotState s)
    {
        int rosterCount = s.RosterCount;
        int bestSlot = -1;
        int bestHp = int.MaxValue;
        for (int r = 0; r < rosterCount; r++)
        {
            byte enemySlot = s.ActiveEnemyRoster[r];
            ref readonly HotCreature vital = ref s.Enemies[enemySlot].Vital;
            if ((vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;
            if (vital.CurrentHp < bestHp)
            {
                bestHp = vital.CurrentHp;
                bestSlot = enemySlot;
            }
        }
        return bestSlot;
    }
}
