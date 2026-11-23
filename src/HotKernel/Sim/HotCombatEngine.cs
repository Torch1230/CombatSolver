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

        int energyCost = GetPayableEnergyCost(in s, in card, cold);
        int starCost = GetPayableStarCost(in s, in card, cold);
        if (energyCost > s.Energy || starCost > s.Stars)
            return false;

        HotCardDef def = rules.Cards[card.CardDefId];
        if (def.Target == TargetType.AnyEnemy && !HasAnyLivingEnemy(in s))
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

    public static int EnumerateLegalActions(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        Span<HotAction> dst)
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

        bool executed;
        switch (action.Kind)
        {
            case HotActionKind.PlayCard:
                executed = ExecutePlayCard(ref s, ref cold, rules, scratch, action.SlotOrHandIdx, action.TargetEnemySlot);
                break;

            case HotActionKind.EndTurn:
                executed = ExecuteEndTurn(ref s, ref cold, rules, scratch);
                break;

            case HotActionKind.UsePotion:
                executed = false;
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
    /// Computes post-combat victory healing from relics such as Burning Blood (STS2 triggers
    /// <c>BurningBlood.AfterCombatVictory</c> after combat teardown, so in-combat <c>Player.CurrentHp</c>
    /// stays exact for lockstep <c>ContinuationStamp</c> parity while search evaluation accounts for it).
    /// </summary>
    public static int ComputePostCombatRelicHeal(in CombatHotState s, HotRules rules)
    {
        if (s.TerminalOutcome != HotCombatOutcome.Victory || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return 0;

        int missingHp = Math.Max(0, s.Player.MaxHp - s.Player.CurrentHp);
        if (missingHp == 0)
            return 0;

        int heal = 0;
        if (rules.HasBurningBlood && rules.BurningBloodRelicDefId != 0)
        {
            short bbHeal = rules.Relics[rules.BurningBloodRelicDefId].PrimaryValue;
            heal += bbHeal > 0 ? bbHeal : 6;
        }

        return Math.Min(missingHp, heal);
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
    internal static bool IsInProgress(in CombatHotState s)
        => (s.CombatFlags & HotCombatFlags.IsInProgress) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsCombatEnding(in CombatHotState s)
        => IsInProgress(in s)
            && ((s.CombatFlags & HotCombatFlags.IsAboutToLose) != 0
                || s.TerminalOutcome != HotCombatOutcome.Ongoing
                || !HasAnyLivingEnemy(in s));

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

        if (s.TerminalOutcome == HotCombatOutcome.Victory || !HasAnyLivingEnemy(in s))
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
