using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.MonsterMoves;

namespace CombatSolver.HotKernel;

internal static partial class HotCombatEngine
{
    /// <summary>
    /// Executes the current compiled move node for <paramref name="enemySlot"/>:
    /// multi-hit powered attacks against Player followed by compiled <see cref="HotMonsterMoveEffect"/>s
    /// (block, buffs, debuffs, status cards). Matches <c>MonsterMoveSemantics.ApplyForecastMove</c>.
    /// </summary>
    internal static void ExecuteMonsterMove(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte enemySlot)
    {
        if (enemySlot >= s.EnemyCount)
            return;

        ushort monsterDefId = s.Enemies[enemySlot].MonsterDefId;
        short moveNodeId = s.Enemies[enemySlot].CurrentMoveNode;
        if (monsterDefId == 0 || moveNodeId < 0)
            return;

        HotMonsterMachine? machine = rules.GetMonsterMachine(monsterDefId);
        if (machine == null || moveNodeId >= machine.Nodes.Length)
            return;

        HotMonsterNode node = machine.Nodes[moveNodeId];
        byte dealerSlot = (byte)(3 + enemySlot);

        // 1. Attack hits (matches MonsterMoveSemantics.ApplyForecastMove attack loop)
        if (node.Repeats > 0)
        {
            int hits = node.Repeats;
            if (rules.Monsters[monsterDefId].StateKind == HotMonsterStateKind.TestSubject
                && string.Equals(node.MoveId, "MULTI_CLAW_MOVE", StringComparison.Ordinal))
            {
                hits = Math.Max(1, rules.Monsters[monsterDefId].TestSubjectBaseMultiClawCount + s.Enemies[enemySlot].AiRegs[1]);
            }

            scratch.AttackActive = true;
            for (int h = 0; h < hits; h++)
            {
                if ((s.Player.Flags & HotCreatureFlags.Alive) == 0
                    || (s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                {
                    break;
                }

                DealDamage(
                    ref s,
                    ref cold,
                    rules,
                    scratch,
                    dealerSlot: dealerSlot,
                    targetSlot: 0,
                    baseDamage: node.BaseDamage,
                    isPoweredAttack: true);
            }
            s.Enemies[enemySlot].CreatureAttacksThisTurn++;
            scratch.AttackActive = false;

            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
                return;
        }

        // 2. Non-attack / post-attack move effects (block, powers, status cards)
        HotMonsterMoveEffect[] effects = node.MoveEffects;
        for (int i = 0; i < effects.Length; i++)
        {
            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
                break;

            HotMonsterMoveEffect effect = effects[i];
            switch (effect.Op)
            {
                case HotCardOpKind.GainBlock:
                {
                    // Vanilla monster move block uses ValueProp.Move (affected by Dexterity/Frail).
                    bool isPoweredBlock = ((HotCardOpFlags)effect.Flags & HotCardOpFlags.Unpowered) == 0;
                    GainBlock(ref s, rules, targetSlot: dealerSlot, baseAmount: effect.Amount, isCardBlock: isPoweredBlock);
                    break;
                }

                case HotCardOpKind.ApplyPowerSelf:
                {
                    ApplyPower(
                        ref s,
                        rules,
                        ownerSlot: dealerSlot,
                        applierSlot: dealerSlot,
                        powerDefId: effect.TargetDefId,
                        amount: effect.Amount);
                    break;
                }

                case HotCardOpKind.ApplyPowerTarget:
                {
                    ApplyPower(
                        ref s,
                        rules,
                        ownerSlot: 0,
                        applierSlot: dealerSlot,
                        powerDefId: effect.TargetDefId,
                        amount: effect.Amount);
                    break;
                }

                case HotCardOpKind.Heal:
                {
                    HealCreature(ref s, targetSlot: dealerSlot, amount: effect.Amount);
                    break;
                }

                case HotCardOpKind.LoseHp:
                {
                    DealDamage(
                        ref s,
                        ref cold,
                        rules,
                        scratch,
                        dealerSlot: dealerSlot,
                        targetSlot: 0,
                        baseDamage: effect.Amount,
                        isPoweredAttack: false,
                        isUnblockable: true);
                    break;
                }

                case HotCardOpKind.AddFixedCard:
                {
                    for (int c = 0; c < effect.Amount; c++)
                        AddGeneratedFixedCard(ref s, rules, effect.TargetDefId, HotPileKind.Discard, s.DiscardCount);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Advances a living enemy's compiled move state machine after its turn, matching
    /// <c>SimulatedCombatState.PrepareMonsterMoveForNextRound</c> &amp; <c>BranchMonsterAi.Advance</c>.
    /// </summary>
    internal static void PrepareMonsterMoveForNextRound(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        byte enemySlot,
        short performedMoveNode)
    {
        if (!IsValidEnemyTarget(in s, enemySlot))
            return;

        ushort monsterDefId = s.Enemies[enemySlot].MonsterDefId;
        if (monsterDefId == 0)
            return;

        HotMonsterMachine? machine = rules.GetMonsterMachine(monsterDefId);
        if (machine == null)
            return;

        if ((s.Enemies[enemySlot].Flags & HotEnemyFlags.NeedsInitialRoll) != 0)
        {
            RollInitialMonsterAi(ref s, ref cold, rules, enemySlot, machine);
            return;
        }

        // Newly summoned creatures retain their rolled first move until they act.
        if (performedMoveNode < 0)
            return;

        short currentNodeId = s.Enemies[enemySlot].CurrentMoveNode;
        if (currentNodeId >= 0 && currentNodeId < machine.Nodes.Length)
        {
            HotMonsterNode current = machine.Nodes[currentNodeId];
            if (string.Equals(current.MoveId, "STUNNED", StringComparison.Ordinal)
                && (s.Enemies[enemySlot].Flags & HotEnemyFlags.SkipNextMove) != 0)
            {
                return;
            }
            if (current.MustPerformOnceBeforeRepeat && performedMoveNode != currentNodeId)
                return;
        }

        AdvanceMonsterAi(ref s, ref cold, rules, enemySlot, machine);
    }

    internal static void AdvanceMonsterAi(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        byte enemySlot,
        HotMonsterMachine machine)
    {
        short currentNodeId = s.Enemies[enemySlot].CurrentMoveNode;
        if (currentNodeId < 0 || currentNodeId >= machine.Nodes.Length)
            throw new InvalidOperationException($"Enemy slot {enemySlot} ({machine.MonsterEntry}) has invalid CurrentMoveNode {currentNodeId}.");

        short nextNodeId = machine.Nodes[currentNodeId].NextNode;
        ResolveMonsterTransition(ref s, ref cold, rules, enemySlot, machine, nextNodeId);
    }

    internal static void RollInitialMonsterAi(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        byte enemySlot,
        HotMonsterMachine machine)
    {
        ResolveMonsterTransition(ref s, ref cold, rules, enemySlot, machine, machine.InitialNode);
        s.Enemies[enemySlot].Flags &= ~HotEnemyFlags.NeedsInitialRoll;
    }

    private static void ResolveMonsterTransition(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        byte enemySlot,
        HotMonsterMachine machine,
        short startNodeId)
    {
        short nextNodeId = startNodeId;
        for (int guard = 0; guard < 32; guard++)
        {
            if (nextNodeId < 0 || nextNodeId >= machine.Nodes.Length)
            {
                throw new InvalidOperationException(
                    $"Monster {machine.MonsterEntry} transitioned to invalid node {nextNodeId}.");
            }

            HotMonsterNode node = machine.Nodes[nextNodeId];
            switch (node.NodeKind)
            {
                case HotMonsterNodeKind.Move:
                    s.Enemies[enemySlot].CurrentMoveNode = nextNodeId;
                    AppendMonsterMoveLog(ref s, ref cold, enemySlot, nextNodeId);
                    return;

                case HotMonsterNodeKind.RandomBranch:
                    nextNodeId = PickRandomBranch(
                        in s,
                        cold,
                        machine,
                        node,
                        enemySlot,
                        ref s.Rng[(int)HotRngStream.MonsterAi]);
                    break;

                case HotMonsterNodeKind.ConditionalBranch:
                    nextNodeId = ResolveConditionalBranch(in s, rules, machine, node, enemySlot);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported monster node kind {node.NodeKind} for {machine.MonsterEntry}.");
            }
        }

        throw new InvalidOperationException(
            $"Monster {machine.MonsterEntry} state machine did not reach a Move node within 32 steps.");
    }

    /// <summary>
    /// Exact zero-allocation match of <c>MonsterRandomBranchResolver.Pick</c>.
    /// </summary>
    private static short PickRandomBranch(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotMonsterMachine machine,
        HotMonsterNode branch,
        byte enemySlot,
        ref HotRngState rng)
    {
        int count = branch.BranchTargets.Length;
        if (count == 0)
            throw new InvalidOperationException($"RandomBranch {branch.MoveId} for {machine.MonsterEntry} has no targets.");

        Span<float> weights = count <= HotLimits.MaxStackAllocElements
            ? stackalloc float[count]
            : new float[count];

        float total = 0f;
        for (int b = 0; b < count; b++)
        {
            short targetNode = branch.BranchTargets[b];
            float repeatWeight = ComputeRepeatWeight(
                in s,
                cold,
                machine,
                enemySlot,
                targetNode,
                (MoveRepeatType)branch.BranchRepeatTypes[b],
                branch.BranchMaxTimes[b],
                branch.BranchCooldowns[b]);
            float w = repeatWeight * branch.BranchWeights[b];
            weights[b] = w;
            total += w;
        }

        // Vanilla always invokes NextFloat(total) even when total == 0f (roll == 0 -> selects index 0).
        float roll = HotRng.NextFloat(ref rng, total);
        for (int b = 0; b < count; b++)
        {
            roll -= weights[b];
            if (roll <= 0f)
                return branch.BranchTargets[b];
        }

        for (int b = count - 1; b >= 0; b--)
        {
            if (weights[b] > 0f)
                return branch.BranchTargets[b];
        }

        return branch.BranchTargets[0];
    }

    private static float ComputeRepeatWeight(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotMonsterMachine machine,
        byte enemySlot,
        short targetNodeId,
        MoveRepeatType repeatType,
        byte maxTimes,
        byte cooldown)
    {
        ref readonly HotEnemy enemy = ref s.Enemies[enemySlot];
        int logLen = GetMonsterLogLength(in enemy, enemySlot, cold, out ushort[]? overflowLog);

        if (repeatType == MoveRepeatType.UseOnlyOnce)
        {
            if (targetNodeId >= 0 && targetNodeId < 64)
            {
                if ((enemy.EverUsedMovesMask & (1UL << targetNodeId)) != 0)
                    return 0f;
            }
            else
            {
                for (int i = 0; i < logLen; i++)
                {
                    if (GetMonsterLogNodeAt(in enemy, overflowLog, i) == targetNodeId)
                        return 0f;
                }
            }
        }

        if (repeatType == MoveRepeatType.CannotRepeat
            && logLen > 0
            && GetMonsterLogNodeAt(in enemy, overflowLog, logLen - 1) == targetNodeId)
        {
            return 0f;
        }

        if (repeatType == MoveRepeatType.CanRepeatXTimes)
        {
            int consecutive = 0;
            for (int i = logLen - 1; i >= 0; i--)
            {
                if (GetMonsterLogNodeAt(in enemy, overflowLog, i) != targetNodeId)
                    break;
                consecutive++;
            }
            if (consecutive >= maxTimes)
                return 0f;
        }

        if (cooldown > 0)
        {
            int movesSeen = 0;
            for (int i = logLen - 1; i >= 0 && movesSeen < cooldown; i--)
            {
                short loggedId = GetMonsterLogNodeAt(in enemy, overflowLog, i);
                if (loggedId >= 0 && loggedId < machine.Nodes.Length && machine.Nodes[loggedId].IsMove)
                {
                    if (loggedId == targetNodeId)
                        return 0f;
                    movesSeen++;
                }
            }
        }

        return 1f;
    }

    private static short ResolveConditionalBranch(
        in CombatHotState s,
        HotRules rules,
        HotMonsterMachine machine,
        HotMonsterNode branch,
        byte enemySlot)
    {
        ref readonly HotEnemy enemy = ref s.Enemies[enemySlot];
        switch (branch.ConditionalKind)
        {
            case HotMonsterConditionalKind.BowlbugRockPostHeadbutt:
            {
                short id = machine.FindNode("HEADBUTT_MOVE");
                return id >= 0 ? id : branch.NextNode;
            }

            case HotMonsterConditionalKind.FrogKnightHalfHealth:
            {
                bool charged = (enemy.Flags & HotEnemyFlags.BeetleCharged) != 0;
                string moveId = charged || enemy.Vital.CurrentHp >= enemy.Vital.MaxHp / 2
                    ? "TONGUE_LASH"
                    : "BEETLE_CHARGE";
                short id = machine.FindNode(moveId);
                return id >= 0 ? id : branch.NextNode;
            }

            case HotMonsterConditionalKind.LivingShieldSlam:
            {
                bool hasAliveAlly = false;
                for (int r = 0; r < s.RosterCount; r++)
                {
                    byte other = s.ActiveEnemyRoster[r];
                    if (other != enemySlot && (s.Enemies[other].Vital.Flags & HotCreatureFlags.Alive) != 0)
                    {
                        hasAliveAlly = true;
                        break;
                    }
                }
                short id = machine.FindNode(hasAliveAlly ? "SHIELD_SLAM_MOVE" : "SMASH_MOVE");
                return id >= 0 ? id : branch.NextNode;
            }

            case HotMonsterConditionalKind.QueenAmalgam:
            {
                bool amalgamDied = (enemy.Flags & HotEnemyFlags.AmalgamDied) != 0;
                short id = machine.FindNode(amalgamDied ? "OFF_WITH_YOUR_HEAD_MOVE" : "BURN_BRIGHT_FOR_ME_MOVE");
                return id >= 0 ? id : branch.NextNode;
            }

            case HotMonsterConditionalKind.TestSubjectRevive:
            {
                short id = machine.FindNode(enemy.AiRegs[0] < 2 ? "MULTI_CLAW_MOVE" : "PHASE3_LACERATE_MOVE");
                return id >= 0 ? id : branch.NextNode;
            }

            default:
                return branch.NextNode;
        }
    }

    /// <summary>
    /// Appends <paramref name="moveNodeId"/> to the monster's state log:
    /// zero-allocation inline append in <c>HotEnemy.RecentMoves[8]</c> for the first 8 moves,
    /// transparently promoting to COW <c>HotColdSidecar.MonsterLogs</c> when log exceeds 8 entries
    /// so <see cref="HotContinuationStamp"/> matches <c>MoveStateMachine.StateLog</c> verbatim on any turn.
    /// </summary>
    private static void AppendMonsterMoveLog(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        byte enemySlot,
        short moveNodeId)
    {
        ref HotEnemy enemy = ref s.Enemies[enemySlot];
        if (moveNodeId >= 0 && moveNodeId < 64)
            enemy.EverUsedMovesMask |= 1UL << moveNodeId;

        // Check if this enemy already has an overflow log in HotColdSidecar.
        if (cold?.MonsterLogs is { Length: > 0 } existingLogs)
        {
            for (int i = 0; i < existingLogs.Length; i++)
            {
                if (existingLogs[i].EnemySlot == enemySlot)
                {
                    ushort[] oldIds = existingLogs[i].MoveNodeIds;
                    ushort[] nextIds = new ushort[oldIds.Length + 1];
                    oldIds.CopyTo(nextIds, 0);
                    nextIds[oldIds.Length] = (ushort)moveNodeId;

                    HotMonsterStateLogOverflow[] replaced = (HotMonsterStateLogOverflow[])existingLogs.Clone();
                    replaced[i] = new HotMonsterStateLogOverflow(enemySlot, nextIds);
                    cold = cold with { MonsterLogs = replaced };

                    ShiftRecentMovesAppend(ref enemy, (byte)moveNodeId);
                    return;
                }
            }
        }

        // Inline path (zero heap allocation while <= 8 moves).
        if (enemy.RecentMoveCount < HotLimits.MaxMonsterRecentMoves)
        {
            enemy.RecentMoves[enemy.RecentMoveCount] = (byte)moveNodeId;
            enemy.RecentMoveCount++;
            return;
        }

        // Spill from inline 8 -> COW overflow log of length 9.
        ushort[] promoted = new ushort[HotLimits.MaxMonsterRecentMoves + 1];
        for (int i = 0; i < HotLimits.MaxMonsterRecentMoves; i++)
            promoted[i] = enemy.RecentMoves[i];
        promoted[HotLimits.MaxMonsterRecentMoves] = (ushort)moveNodeId;

        ShiftRecentMovesAppend(ref enemy, (byte)moveNodeId);

        HotMonsterStateLogOverflow entry = new(enemySlot, promoted);
        if (cold == null)
        {
            cold = new HotColdSidecar(MonsterLogs: [entry]);
        }
        else if (cold.MonsterLogs is not { Length: > 0 } prev)
        {
            cold = cold with { MonsterLogs = [entry] };
        }
        else
        {
            HotMonsterStateLogOverflow[] appended = new HotMonsterStateLogOverflow[prev.Length + 1];
            prev.CopyTo(appended, 0);
            appended[prev.Length] = entry;
            cold = cold with { MonsterLogs = appended };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ShiftRecentMovesAppend(ref HotEnemy enemy, byte nodeId)
    {
        for (int i = 0; i < HotLimits.MaxMonsterRecentMoves - 1; i++)
            enemy.RecentMoves[i] = enemy.RecentMoves[i + 1];
        enemy.RecentMoves[HotLimits.MaxMonsterRecentMoves - 1] = nodeId;
        enemy.RecentMoveCount = HotLimits.MaxMonsterRecentMoves;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetMonsterLogLength(
        in HotEnemy enemy,
        byte enemySlot,
        HotColdSidecar? cold,
        out ushort[]? overflowLog)
    {
        if (cold?.MonsterLogs is { Length: > 0 } logs)
        {
            for (int i = 0; i < logs.Length; i++)
            {
                if (logs[i].EnemySlot == enemySlot)
                {
                    overflowLog = logs[i].MoveNodeIds;
                    return overflowLog.Length;
                }
            }
        }
        overflowLog = null;
        return Math.Min((int)enemy.RecentMoveCount, HotLimits.MaxMonsterRecentMoves);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short GetMonsterLogNodeAt(in HotEnemy enemy, ushort[]? overflowLog, int index)
        => overflowLog != null ? (short)overflowLog[index] : (short)enemy.RecentMoves[index];
}
