using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.MonsterMoves;

namespace CombatSolver.HotKernel;

/// <summary>
/// Zero-allocation STS2 0.113.1 monster move execution, state-machine AI transitions,
/// dynamic spawning, and enemy death-lifecycle kernel.
/// Covers all 99 explicit monsters / 179 explicit moves, all spawn closures, and all
/// revival/death powers with exact RNG and continuation-stamp parity.
/// </summary>
internal static class HotMonsters
{
    private const int InfiniteHpSentinel = 999_999_999;
    private const short SyntheticReviveNodeSentinel = -2;

    // -------------------------------------------------------------------------
    // Public Entrypoint 1: ExecuteMonsterMove
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executes the current move for <paramref name="enemySlot"/>, matching
    /// <c>MonsterMoveSemantics.ApplyForecastMove</c>, <c>MonsterMoveEffects.ApplyBeforeAttack</c>,
    /// and <c>MonsterMoveEffects.Apply</c>.
    /// </summary>
    public static void ExecuteMonsterMove(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int enemySlot)
    {
        if ((uint)enemySlot >= (uint)s.EnemyCount)
            return;

        byte slot = (byte)enemySlot;
        byte dealerSlot = (byte)(3 + slot);

        bool isAlive = (s.Enemies[slot].Vital.Flags & HotCreatureFlags.Alive) != 0;
        bool isReviving = s.Enemies[slot].DeathPhase == HotDeathPhase.Reviving;
        if (!isAlive && !isReviving)
            return;

        ushort monsterDefId = s.Enemies[slot].MonsterDefId;
        if (monsterDefId == 0 || monsterDefId >= rules.Monsters.Length)
            return;

        HotMonsterDef monsterDef = rules.Monsters[monsterDefId];
        HotMonsterId monsterId = ResolveMonsterId(monsterDef);
        HotMonsterMachine? machine = rules.GetMonsterMachine(monsterDefId);

        // Synthetic IllusionPower REVIVE_MOVE when machine has no precompiled REVIVE_MOVE node.
        if (s.Enemies[slot].CurrentMoveNode == SyntheticReviveNodeSentinel)
        {
            if (isReviving)
            {
                s.Enemies[slot].Vital.CurrentHp = s.Enemies[slot].Vital.MaxHp;
                s.Enemies[slot].Vital.Flags |= HotCreatureFlags.Alive;
                s.Enemies[slot].DeathPhase = HotDeathPhase.None;
            }
            return;
        }

        // Handle WaterfallGiant SteamEruption forced phase moves even if CurrentMoveNode lagged.
        if (monsterId == HotMonsterId.WaterfallGiant && s.Enemies[slot].SteamPhase != 0)
        {
            if (s.Enemies[slot].SteamPhase == 1)
            {
                PrepareSteamEruption(ref s, rules, slot);
                return;
            }

            if (s.Enemies[slot].SteamPhase == 2)
            {
                int steamDmg = s.Enemies[slot].AiRegs[1];
                s.Enemies[slot].SteamPhase = 0;
                ExecuteWaterfallExplode(ref s, ref cold, rules, scratch, slot, dealerSlot, steamDmg);
                return;
            }
        }

        short moveNodeId = s.Enemies[slot].CurrentMoveNode;
        if (machine == null || moveNodeId < 0 || moveNodeId >= machine.Nodes.Length)
            return;

        HotMonsterNode node = machine.Nodes[moveNodeId];
        string moveId = node.MoveId;

        // 1. Pre-attack effects (LivingFog BLOAT_MOVE spawns GasBomb; ThievingHopper THIEVERY_MOVE steals card).
        ApplyBeforeAttack(ref s, ref cold, rules, scratch, slot, dealerSlot, monsterDef, monsterId, moveId);
        if (scratch.NeedsChoice || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return;

        // 2. Multi-hit attack loop (matches MonsterMoveSemantics.ApplyForecastMove).
        bool hasAttack = HasAttackHits(node, monsterId, moveId);
        bool fullyBlockedAnyHit = false;
        int playerUnblockedHits = 0;
        int suckTriggeredHits = 0;

        if (hasAttack && (s.Enemies[slot].Vital.Flags & HotCreatureFlags.Alive) != 0)
        {
            HotHookArgs atkArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = 0,
                IsPoweredAttack = true,
                CardIdx = -1,
            };
            HotCombatEngine.DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.BeforeAttack, ref atkArgs);
            if (scratch.NeedsChoice)
                return;

            scratch.AttackActive = true;
            ExecuteAttackHits(
                ref s,
                ref cold,
                rules,
                scratch,
                slot,
                dealerSlot,
                monsterDef,
                monsterId,
                node,
                ref fullyBlockedAnyHit,
                ref playerUnblockedHits,
                ref suckTriggeredHits);
            s.Enemies[slot].CreatureAttacksThisTurn++;
            scratch.AttackActive = false;

            if (scratch.NeedsChoice)
                return;

            HotHookArgs afterAtkArgs = new()
            {
                ActorSlot = dealerSlot,
                TargetSlot = 0,
                IsPoweredAttack = true,
                CardIdx = -1,
            };
            HotCombatEngine.DispatchHooks(ref s, ref cold, rules, scratch, HotHookMask.AfterAttack, ref afterAtkArgs);
            if (scratch.NeedsChoice)
                return;

            // PainfulStabsPower: add Wound to Discard for each unblocked hit received by player.
            if (playerUnblockedHits > 0 && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
            {
                int painfulStabs = HotCombatEngine.GetPowerAmountById(in s, rules, dealerSlot, HotPowerId.PainfulStabsPower);
                if (painfulStabs > 0)
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Wound, HotPileKind.Discard, painfulStabs * playerUnblockedHits, randomPos: false);
            }

            // SuckPower: gain Strength for each hit that dealt unblocked damage to player or pet.
            if (suckTriggeredHits > 0 && (s.Enemies[slot].Vital.Flags & HotCreatureFlags.Alive) != 0)
            {
                int suck = HotCombatEngine.GetPowerAmountById(in s, rules, dealerSlot, HotPowerId.SuckPower);
                if (suck > 0)
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, dealerSlot, dealerSlot, HotPowerId.StrengthPower, suck * suckTriggeredHits);
            }

            if (scratch.NeedsChoice || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
                return;
        }

        // ImbalancedPower stun when attack was fully blocked.
        if (fullyBlockedAnyHit
            && (s.Enemies[slot].Vital.Flags & HotCreatureFlags.Alive) != 0
            && HotCombatEngine.GetPowerAmountById(in s, rules, dealerSlot, HotPowerId.ImbalancedPower) > 0)
        {
            if (monsterId == HotMonsterId.BowlbugRock)
                ForceStunnedMove(ref s, rules, slot, "HEADBUTT_MOVE");
            else
                ForceStunnedMove(ref s, rules, slot, null);
            s.Enemies[slot].Flags |= HotEnemyFlags.SkipNextMove;
        }

        // 3. Post-attack move effects (all 99 explicit monsters / 179 explicit moves + compiled MoveEffects fallback).
        bool handled = ApplyPostAttackMoveEffects(
            ref s,
            ref cold,
            rules,
            scratch,
            slot,
            dealerSlot,
            monsterDef,
            monsterId,
            moveId);

        if (scratch.NeedsChoice)
            return;

        if (!handled && node.MoveEffects.Length > 0)
        {
            ExecuteCompiledMoveEffects(ref s, ref cold, rules, scratch, dealerSlot, node.MoveEffects);
            if (scratch.NeedsChoice)
                return;
        }

        // Keep Wither upgrades and card afflictions synchronized after monster actions.
        if (monsterId == HotMonsterId.Aeonglass)
            NormalizeAeonglassWithers(ref s, rules);

        if (monsterId is HotMonsterId.SpectralKnight or HotMonsterId.VineShambler or HotMonsterId.CeremonialBeast)
            NormalizeCardAfflictions(ref s, rules);
    }

    // -------------------------------------------------------------------------
    // Public Entrypoint 2: RollNextMove & PrepareMoveForNextRound
    // -------------------------------------------------------------------------

    /// <summary>
    /// Advances a living or reviving enemy's compiled move state machine after its turn,
    /// matching <c>SimulatedCombatState.PrepareMonsterMoveForNextRound</c>.
    /// </summary>
    internal static void PrepareMoveForNextRound(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        int enemySlot,
        short performedMoveNode)
    {
        if ((uint)enemySlot >= (uint)s.EnemyCount)
            return;

        byte slot = (byte)enemySlot;
        if (!CanPerformMonsterMove(in s, slot))
            return;

        ushort monsterDefId = s.Enemies[slot].MonsterDefId;
        if (monsterDefId == 0 || monsterDefId >= rules.Monsters.Length)
            return;

        HotMonsterMachine? machine = rules.GetMonsterMachine(monsterDefId);
        if (machine == null)
            return;

        if ((s.Enemies[slot].Flags & HotEnemyFlags.NeedsInitialRoll) != 0)
        {
            RollInitialMove(ref s, ref cold, rules, slot, machine);
            return;
        }

        // Newly summoned creatures retain their already-rolled first move until they act.
        if (performedMoveNode < 0)
            return;

        HotMonsterId monsterId = ResolveMonsterId(rules.Monsters[monsterDefId]);

        // WaterfallGiant SteamEruption overrides normal state machine progression.
        if (monsterId == HotMonsterId.WaterfallGiant && s.Enemies[slot].SteamPhase != 0)
        {
            if (s.Enemies[slot].SteamPhase == 1)
            {
                ForceMonsterMove(ref s, rules, slot, "ABOUT_TO_BLOW_MOVE");
            }
            else if (s.Enemies[slot].SteamPhase == 2)
            {
                short explodeNode = machine.FindNode("EXPLODE_MOVE");
                if (explodeNode >= 0)
                {
                    s.Enemies[slot].CurrentMoveNode = explodeNode;
                    s.Enemies[slot].Flags &= ~HotEnemyFlags.NeedsInitialRoll;
                    AppendMonsterMoveLog(ref s, ref cold, slot, explodeNode);
                }
                else
                {
                    ForceMonsterMove(ref s, rules, slot, "EXPLODE_MOVE");
                }
            }
            return;
        }

        short currentNodeId = s.Enemies[slot].CurrentMoveNode;
        if (currentNodeId >= 0 && currentNodeId < machine.Nodes.Length)
        {
            HotMonsterNode current = machine.Nodes[currentNodeId];
            if (string.Equals(current.MoveId, "STUNNED", StringComparison.Ordinal)
                && (s.Enemies[slot].Flags & HotEnemyFlags.SkipNextMove) != 0)
            {
                return;
            }
            if (current.MustPerformOnceBeforeRepeat && performedMoveNode != currentNodeId)
                return;
        }

        RollNextMove(ref s, ref cold, rules, enemySlot);
    }

    /// <summary>
    /// Rolls or advances the next move for <paramref name="enemySlot"/>, matching
    /// <c>BranchMonsterAi.Advance</c>, <c>BranchMonsterAi.RollInitial</c>,
    /// <c>ResolveConditional</c>, and <c>MonsterRandomBranchResolver.Pick</c>.
    /// </summary>
    public static void RollNextMove(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        int enemySlot)
    {
        if ((uint)enemySlot >= (uint)s.EnemyCount)
            return;

        byte slot = (byte)enemySlot;
        ushort monsterDefId = s.Enemies[slot].MonsterDefId;
        if (monsterDefId == 0 || monsterDefId >= rules.Monsters.Length)
            return;

        HotMonsterMachine? machine = rules.GetMonsterMachine(monsterDefId);
        if (machine == null)
            return;

        if ((s.Enemies[slot].Flags & HotEnemyFlags.NeedsInitialRoll) != 0)
        {
            RollInitialMove(ref s, ref cold, rules, slot, machine);
            return;
        }

        HotMonsterId monsterId = ResolveMonsterId(rules.Monsters[monsterDefId]);

        // WaterfallGiant forced SteamEruption transitions.
        if (monsterId == HotMonsterId.WaterfallGiant && s.Enemies[slot].SteamPhase != 0)
        {
            if (s.Enemies[slot].SteamPhase == 1)
            {
                ForceMonsterMove(ref s, rules, slot, "ABOUT_TO_BLOW_MOVE");
            }
            else if (s.Enemies[slot].SteamPhase == 2)
            {
                short explodeNode = machine.FindNode("EXPLODE_MOVE");
                if (explodeNode >= 0)
                {
                    s.Enemies[slot].CurrentMoveNode = explodeNode;
                    s.Enemies[slot].Flags &= ~HotEnemyFlags.NeedsInitialRoll;
                    AppendMonsterMoveLog(ref s, ref cold, slot, explodeNode);
                }
                else
                {
                    ForceMonsterMove(ref s, rules, slot, "EXPLODE_MOVE");
                }
            }
            return;
        }

        // If SkipNextMove is still pending at end of enemy turn (e.g. ImbalancedPower stun during own attack
        // or RavenousPower stun triggered later in the same enemy turn), preserve the stunned state for next round.
        if ((s.Enemies[slot].Flags & HotEnemyFlags.SkipNextMove) != 0)
            return;

        short currentNodeId = s.Enemies[slot].CurrentMoveNode;

        // Synthetic IllusionPower REVIVE_MOVE recovery via AiRegs[3].
        if (currentNodeId == SyntheticReviveNodeSentinel)
        {
            short followUp = s.Enemies[slot].AiRegs[3] > 0
                ? (short)(s.Enemies[slot].AiRegs[3] - 1)
                : machine.InitialNode;
            s.Enemies[slot].AiRegs[3] = 0;
            if (followUp >= 0)
                ResolveTransition(ref s, ref cold, rules, slot, machine, monsterId, followUp);
            return;
        }

        if (currentNodeId < 0 || currentNodeId >= machine.Nodes.Length)
            return;

        HotMonsterNode currentNode = machine.Nodes[currentNodeId];

        // KnowledgeDemon: PONDER_MOVE branches on _curseOfKnowledgeCounter (< 3 -> CURSE_OF_KNOWLEDGE_MOVE else SLAP_MOVE).
        if (monsterId == HotMonsterId.KnowledgeDemon
            && string.Equals(currentNode.MoveId, "PONDER_MOVE", StringComparison.Ordinal))
        {
            string nextId = s.Enemies[slot].AiRegs[0] < 3 ? "CURSE_OF_KNOWLEDGE_MOVE" : "SLAP_MOVE";
            short target = machine.FindNode(nextId);
            if (target >= 0)
            {
                s.Enemies[slot].CurrentMoveNode = target;
                AppendMonsterMoveLog(ref s, ref cold, slot, target);
                return;
            }
        }

        short nextNodeId = currentNode.NextNode;
        if ((nextNodeId < 0
                || string.Equals(currentNode.MoveId, "STUNNED", StringComparison.Ordinal)
                || string.Equals(currentNode.MoveId, "REVIVE_MOVE", StringComparison.Ordinal)
                || (s.Enemies[slot].Flags & HotEnemyFlags.Stunned) != 0)
            && s.Enemies[slot].AiRegs[3] > 0)
        {
            nextNodeId = (short)(s.Enemies[slot].AiRegs[3] - 1);
            s.Enemies[slot].AiRegs[3] = 0;
            s.Enemies[slot].Flags &= ~HotEnemyFlags.Stunned;
        }

        if (nextNodeId < 0)
            return;

        ResolveTransition(ref s, ref cold, rules, slot, machine, monsterId, nextNodeId);
    }

    internal static void RollInitialMove(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        byte slot,
        HotMonsterMachine machine)
    {
        HotMonsterId monsterId = ResolveMonsterId(rules.Monsters[s.Enemies[slot].MonsterDefId]);
        short startNode = machine.InitialNode;

        // Replacement Axebot / Wriggler spawned moves stored in AiRegs[3] (1-based) if pending.
        if (s.Enemies[slot].AiRegs[3] > 0)
        {
            startNode = (short)(s.Enemies[slot].AiRegs[3] - 1);
            s.Enemies[slot].AiRegs[3] = 0;
        }
        else if (monsterId == HotMonsterId.TwoTailedRat)
        {
            // Per 0.113.1 TwoTailedRat.cs:134-137, mid-combat summoned TwoTailedRat has StarterMoveIndex == -1
            // and starts at randomBranchState ("RAND") rather than the initial encounter rat's starter move.
            short randNode = machine.FindNode("RAND");
            if (randNode >= 0)
                startNode = randNode;
        }

        if (startNode >= 0)
            ResolveTransition(ref s, ref cold, rules, slot, machine, monsterId, startNode);
        s.Enemies[slot].Flags &= ~HotEnemyFlags.NeedsInitialRoll;
    }

    // -------------------------------------------------------------------------
    // Public Entrypoint 3: HandleEnemyDeathEffect
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executes the full STS2 0.113.1 enemy death lifecycle for <paramref name="enemySlot"/>,
    /// matching <c>DeathPowerSupport.Trigger</c>, <c>AfterDeathMirrors</c>,
    /// <c>CorePowerSupport.ApplyEnemyDeathPowers</c>, and <c>SimulatedCombatState.DeathLifecycle</c>.
    /// </summary>
    public static void HandleEnemyDeathEffect(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        int enemySlot)
    {
        if ((uint)enemySlot >= (uint)s.EnemyCount)
            return;

        byte slot = (byte)enemySlot;
        byte deadSlot = (byte)(3 + slot);

        // Guard against re-entrant or duplicate death processing.
        if (s.Enemies[slot].DeathPhase != HotDeathPhase.None)
            return;

        s.Enemies[slot].Vital.CurrentHp = 0;
        s.Enemies[slot].Vital.Flags &= ~HotCreatureFlags.Alive;

        ushort monsterDefId = s.Enemies[slot].MonsterDefId;
        HotMonsterId monsterId = monsterDefId > 0 && monsterDefId < rules.Monsters.Length
            ? ResolveMonsterId(rules.Monsters[monsterDefId])
            : HotMonsterId.None;

        // 1. WaterfallGiant SteamEruptionPower survival (becomes 999,999,999 HP AboutToBlow).
        // Powers remain intact here so KillCreature dispatches HotHookMask.AfterDeath (e.g. ImpaledPower)
        // before calling RemovePowersAfterDeath(removeAllBecauseDeparted: false).
        if (monsterId == HotMonsterId.WaterfallGiant
            && s.Enemies[slot].SteamPhase == 0
            && HotCombatEngine.GetPowerAmountById(in s, rules, deadSlot, HotPowerId.SteamEruptionPower) > 0)
        {
            s.Enemies[slot].Vital.MaxHp = InfiniteHpSentinel;
            s.Enemies[slot].Vital.CurrentHp = InfiniteHpSentinel;
            s.Enemies[slot].Vital.Flags |= HotCreatureFlags.Alive;
            s.Enemies[slot].SteamPhase = 1; // AboutToBlow
            s.Enemies[slot].DeathPhase = HotDeathPhase.None;
            ForceMonsterMove(ref s, rules, slot, "ABOUT_TO_BLOW_MOVE");
            s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);
            return;
        }

        // Snapshot death predicates BEFORE powers are mutated/removed.
        bool isPrimaryEnemy = HotCombatEngine.GetPowerAmountById(in s, rules, deadSlot, HotPowerId.MinionPower) <= 0;
        bool hasReattach = HotCombatEngine.GetPowerAmountById(in s, rules, deadSlot, HotPowerId.ReattachPower) > 0;
        bool allOtherReattachDead = !hasReattach || AreAllOtherReattachSegmentsDead(in s, rules, slot);
        bool shouldRemoveFromCombat = ShouldEnemyBeRemovedFromCombatAfterDeath(in s, rules, deadSlot);

        int explosiveAmt = HotCombatEngine.GetPowerAmountById(in s, rules, deadSlot, HotPowerId.ExplosivePower);
        int stockAmt = HotCombatEngine.GetPowerAmountById(in s, rules, deadSlot, HotPowerId.StockPower);
        bool hasAdaptable = HotCombatEngine.GetPowerAmountById(in s, rules, deadSlot, HotPowerId.AdaptablePower) > 0;
        bool hasIllusion = HotCombatEngine.GetPowerAmountById(in s, rules, deadSlot, HotPowerId.IllusionPower) > 0;
        bool hasInfested = HotCombatEngine.GetPowerAmountById(in s, rules, deadSlot, HotPowerId.InfestedPower) > 0;
        bool hasSurprise = HotCombatEngine.GetPowerAmountById(in s, rules, deadSlot, HotPowerId.SurprisePower) > 0;

        // 2. ExplosivePower (WaterfallGiantDominion / Explosive enemy death blast against Player).
        if (explosiveAmt > 0 && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
        {
            HotCombatEngine.DealDamage(
                ref s,
                ref cold,
                rules,
                scratch,
                dealerSlot: 0xFF,
                targetSlot: 0,
                baseDamage: explosiveAmt,
                isPoweredAttack: false);
            if (scratch.NeedsChoice)
                return;
        }

        // 3. DampenPower cleanup (restore downgraded cards if this enemy was the last caster).
        RemoveDampenCaster(ref s, ref cold, rules, slot);

        // 4. CrabRagePower on surviving enemy teammates (Strength +6, Unpowered Block +99, remove CrabRage).
        TriggerCrabRageOnTeammates(ref s, ref cold, rules, scratch, slot);

        // 5. Queen reacts to TorchHeadAmalgam death.
        if (monsterId == HotMonsterId.TorchHeadAmalgam)
            NotifyQueenOfAmalgamDeath(ref s, rules);

        // 6. StockPower (Axebot replacement).
        if (stockAmt > 0)
            SpawnStockReplacementAxebot(ref s, ref cold, rules, scratch, deadSlot, stockAmt);

        // 7. SurroundedPower facing update on Player.
        UpdateSurroundedFacingAfterEnemyDeath(ref s, rules, slot);

        // 8. Owner-specific death powers.
        if (hasAdaptable)
        {
            s.Enemies[slot].DeathPhase = HotDeathPhase.Reviving;
            ForceMonsterMove(ref s, rules, slot, "RESPAWN_MOVE");
        }

        if (hasIllusion && HasAnyLivingOrRevivingPrimaryEnemy(in s, rules))
            BeginIllusionRevive(ref s, cold, rules, slot);

        if (hasInfested)
        {
            for (int w = 1; w <= 4; w++)
            {
                SpawnMonster(
                    ref s,
                    ref cold,
                    rules,
                    scratch,
                    HotMonsterId.Wriggler,
                    sourceSlot: deadSlot,
                    minion: false,
                    wrigglerSlotIndex: w);
            }
        }

        if (hasReattach)
        {
            if (allOtherReattachDead)
            {
                MarkAllReattachSegmentsPermanentlyDead(ref s, rules);
            }
            else
            {
                s.Enemies[slot].DeathPhase = HotDeathPhase.Reviving;
                ForceMonsterMove(ref s, rules, slot, "DEAD_MOVE");
            }
        }

        if (hasSurprise)
            TriggerSurpriseSpawn(ref s, ref cold, rules, scratch, deadSlot);

        bool isNowReviving = s.Enemies[slot].DeathPhase == HotDeathPhase.Reviving;

        // 9. RavenousPower on surviving enemy teammates (RavenousPower.cs:24-35):
        // stuns surviving teammates and grants them Strength equal to RavenousPower amount.
        TriggerRavenousOnTeammates(ref s, ref cold, rules, scratch, slot, applyStrength: true);

        // 10. Refund PossessStrength / PossessSpeed stolen stats back to Player.
        RefundPossessedStats(ref s, ref cold, rules, scratch, deadSlot);

        // 11. Recover stolen gold (HeistPower) and stolen cards (SwipePower).
        RecoverStolenResources(ref s, rules, deadSlot);

        // 12. Clean up applier-linked debuffs (Constrict, Hex, Shrink, MagicBomb).
        CleanupApplierLinkedDebuffs(ref s, rules, deadSlot);

        // 13-14. Update roster and DeathPhase while leaving deadSlot's powers intact until
        // HotCombatEngine.KillCreature dispatches OnAnyCreatureDeath + HotHookMask.AfterDeath
        // (e.g. Melancholy, ShowOffPower, ImpaledPower, CompostPower) and removes dead powers.
        bool removeFromRoster = shouldRemoveFromCombat && !isNowReviving;

        if (removeFromRoster)
        {
            s.Enemies[slot].Vital.Flags &= ~(HotCreatureFlags.Alive | HotCreatureFlags.ActiveInRoster);
            RemoveFromActiveEnemyRoster(ref s, slot);
        }

        if (!isNowReviving)
            s.Enemies[slot].DeathPhase = HotDeathPhase.PermanentlyDead;

        s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);

        // 15. MinionPower cascade kill: if a primary enemy died and only living minions remain (no reviving primary).
        if (isPrimaryEnemy && !HasAnyLivingOrRevivingPrimaryEnemy(in s, rules))
        {
            // Clear reviving phase on already-dead secondary enemies (e.g. IllusionPower EyeWithTeeth/Parafright)
            // once no primary enemies remain, so CombatHotState/KillCreature/IsCombatEnding agree on Victory.
            for (int e = 0; e < s.EnemyCount; e++)
            {
                if (e != slot && s.Enemies[e].DeathPhase == HotDeathPhase.Reviving)
                    s.Enemies[e].DeathPhase = HotDeathPhase.PermanentlyDead;
            }

            Span<byte> minionsToKill = stackalloc byte[HotLimits.MaxEnemies];
            int minionCount = 0;
            for (int r = 0; r < s.RosterCount; r++)
            {
                byte other = s.ActiveEnemyRoster[r];
                if (other == slot)
                    continue;
                if ((s.Enemies[other].Vital.Flags & HotCreatureFlags.Alive) != 0)
                    minionsToKill[minionCount++] = other;
            }

            for (int m = 0; m < minionCount; m++)
            {
                byte mSlot = minionsToKill[m];
                if ((s.Enemies[mSlot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                {
                    HotCombatEngine.KillCreature(ref s, ref cold, rules, scratch, (byte)(3 + mSlot), force: false);
                    if (scratch.NeedsChoice)
                        return;
                }
            }
        }

        // 16. Victory check (respecting reviving primary enemies & Stock/Infested/Surprise spawns already created).
        if ((s.Player.Flags & HotCreatureFlags.Alive) != 0
            && (s.CombatFlags & HotCombatFlags.BattlewornDummyTimedOut) == 0
            && !HasAnyLivingOrRevivingPrimaryEnemy(in s, rules))
        {
            s.TerminalOutcome = HotCombatOutcome.Victory;
            s.TerminalTurn = (byte)Math.Clamp(s.PlayerTurnNumber, 0, byte.MaxValue);
        }
    }

    // -------------------------------------------------------------------------
    // Pre-Attack Effects
    // -------------------------------------------------------------------------

    private static void ApplyBeforeAttack(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte slot,
        byte dealerSlot,
        HotMonsterDef monsterDef,
        HotMonsterId monsterId,
        string moveId)
    {
        if (monsterId == HotMonsterId.LivingFog && string.Equals(moveId, "BLOAT_MOVE", StringComparison.Ordinal))
        {
            int count = GetStaticInt(monsterDef, "BloatAmount", 1);
            for (int i = 0; i < count; i++)
            {
                if (!HasFreeEncounterSlot(in s, rules))
                    break;
                SpawnMonster(ref s, ref cold, rules, scratch, HotMonsterId.GasBomb, sourceSlot: dealerSlot, minion: false);
            }
            return;
        }

        if (monsterId == HotMonsterId.ThievingHopper && string.Equals(moveId, "THIEVERY_MOVE", StringComparison.Ordinal))
            ApplyThievingHopperStealCard(ref s, rules, dealerSlot);
    }

    private static void ApplyThievingHopperStealCard(
        ref CombatHotState s,
        HotRules rules,
        byte dealerSlot)
    {
        // Candidates: DrawPile then DiscardPile cards with HasDeckVersion && Rarity != Quest.
        Span<int> allCandidates = stackalloc int[HotLimits.MaxCards];
        int candCount = 0;

        int drawStart = s.DrawStart;
        int drawEnd = drawStart + s.DrawCount;
        for (int i = drawStart; i < drawEnd; i++)
        {
            ref readonly HotCard c = ref s.Cards[i];
            if ((c.Flags & HotCardFlags.HasDeckVersion) != 0
                && c.CardDefId < rules.Cards.Length
                && rules.Cards[c.CardDefId].Rarity != CardRarity.Quest)
            {
                allCandidates[candCount++] = i;
            }
        }

        int discStart = s.DiscardStart;
        int discEnd = discStart + s.DiscardCount;
        for (int i = discStart; i < discEnd; i++)
        {
            ref readonly HotCard c = ref s.Cards[i];
            if ((c.Flags & HotCardFlags.HasDeckVersion) != 0
                && c.CardDefId < rules.Cards.Length
                && rules.Cards[c.CardDefId].Rarity != CardRarity.Quest)
            {
                allCandidates[candCount++] = i;
            }
        }

        if (candCount == 0)
            return;

        Span<int> preferred = stackalloc int[HotLimits.MaxCards];
        int prefCount = 0;

        for (int tier = 0; tier < 4; tier++)
        {
            prefCount = 0;
            for (int k = 0; k < candCount; k++)
            {
                int idx = allCandidates[k];
                ref readonly HotCard c = ref s.Cards[idx];
                CardRarity rarity = rules.Cards[c.CardDefId].Rarity;
                bool isImbued = c.EnchId != 0
                    && c.EnchId < rules.Enchantments.Length
                    && rules.Enchantments[c.EnchId].EnchantmentId == HotEnchantmentId.Imbued;

                bool matches = tier switch
                {
                    0 => !isImbued && rarity == CardRarity.Uncommon,
                    1 => !isImbued && rarity is CardRarity.Common or CardRarity.Rare or CardRarity.Event,
                    2 => !isImbued && rarity is CardRarity.Basic or CardRarity.Quest,
                    _ => rarity == CardRarity.Ancient || isImbued,
                };

                if (matches)
                    preferred[prefCount++] = idx;
            }
            if (prefCount > 0)
                break;
        }

        ReadOnlySpan<int> pool = prefCount > 0 ? preferred[..prefCount] : allCandidates[..candCount];
        int chosenCardIdx = pool[HotRng.NextInt(ref s.Rng[(int)HotRngStream.CombatCardSelection], pool.Length)];
        HotCard stolen = s.Cards[chosenCardIdx];

        HotPiles.RemoveCard(ref s, chosenCardIdx);
        s.OutstandingStolenCards++;

        ushort swipeDefId = rules.GetPowerDefId(HotPowerId.SwipePower);
        if (swipeDefId != 0)
        {
            int prevPowerCount = s.PowerCount;
            HotCombatEngine.AddPowerInstance(
                ref s,
                rules,
                ownerSlot: dealerSlot,
                applierSlot: dealerSlot,
                powerDefId: swipeDefId,
                amount: 1,
                aux0: stolen.CardDefId,
                aux1: 0,
                auxCardUid: stolen.CardUid);
            if (s.PowerCount > prevPowerCount)
                s.Powers[s.PowerCount - 1].TargetSlot = 0;
        }

        s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);
    }

    // -------------------------------------------------------------------------
    // Attack Execution
    // -------------------------------------------------------------------------

    private static bool HasAttackHits(HotMonsterNode node, HotMonsterId monsterId, string moveId)
    {
        if (node.Attacks.Length > 0 || node.Repeats > 0)
            return true;
        return monsterId == HotMonsterId.WaterfallGiant
            && string.Equals(moveId, "EXPLODE_MOVE", StringComparison.Ordinal);
    }

    private static void ExecuteAttackHits(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte slot,
        byte dealerSlot,
        HotMonsterDef monsterDef,
        HotMonsterId monsterId,
        HotMonsterNode node,
        ref bool fullyBlockedAnyHit,
        ref int playerUnblockedHits,
        ref int suckTriggeredHits)
    {
        if (node.Attacks.Length > 0)
        {
            HotMonsterAttack[] attacks = node.Attacks;
            for (int a = 0; a < attacks.Length; a++)
            {
                int baseDmg = AdjustMonsterMoveDamage(in s, slot, monsterId, node.MoveId, attacks[a].BaseDamage);
                int repeats = ResolveAttackRepeats(in s, slot, monsterDef, monsterId, node.MoveId, attacks[a].Repeats);

                for (int h = 0; h < repeats; h++)
                {
                    if (!ExecuteSingleMonsterHit(
                            ref s, ref cold, rules, scratch, slot, dealerSlot, baseDmg,
                            ref fullyBlockedAnyHit, ref playerUnblockedHits, ref suckTriggeredHits))
                    {
                        return;
                    }
                }
            }
            return;
        }

        int fallbackHits = node.Repeats > 0
            ? ResolveAttackRepeats(in s, slot, monsterDef, monsterId, node.MoveId, node.Repeats)
            : 1;
        int fallbackDmg = AdjustMonsterMoveDamage(in s, slot, monsterId, node.MoveId, node.BaseDamage);

        for (int h = 0; h < fallbackHits; h++)
        {
            if (!ExecuteSingleMonsterHit(
                    ref s, ref cold, rules, scratch, slot, dealerSlot, fallbackDmg,
                    ref fullyBlockedAnyHit, ref playerUnblockedHits, ref suckTriggeredHits))
            {
                return;
            }
        }
    }

    private static bool ExecuteSingleMonsterHit(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte slot,
        byte dealerSlot,
        int baseDamage,
        ref bool fullyBlockedAnyHit,
        ref int playerUnblockedHits,
        ref int suckTriggeredHits)
    {
        if ((s.Player.Flags & HotCreatureFlags.Alive) == 0
            || (s.Enemies[slot].Vital.Flags & HotCreatureFlags.Alive) == 0)
        {
            return false;
        }

        int playerBlockBefore = s.Player.Block;
        int ostyHpBefore = s.Osty.CurrentHp;
        int wuppyHpBefore = s.Wuppy.CurrentHp;

        // Capture exact pre-kill unblocked damage directly from DealDamage (HotCombatEngine.Damage.cs:411,555)
        // so death-preventing revives (e.g. FairyInABottle / LizardTail) healing above pre-hit HP
        // do not zero out playerUnblocked and miss PainfulStabsPower / SuckPower or trigger ImbalancedPower.
        int playerUnblocked = HotCombatEngine.DealDamage(
            ref s,
            ref cold,
            rules,
            scratch,
            dealerSlot: dealerSlot,
            targetSlot: 0,
            baseDamage: baseDamage,
            isPoweredAttack: true);

        if (scratch.NeedsChoice)
            return false;

        int ostyUnblocked = Math.Max(0, ostyHpBefore - s.Osty.CurrentHp);
        int wuppyUnblocked = Math.Max(0, wuppyHpBefore - s.Wuppy.CurrentHp);
        int blocked = Math.Max(0, playerBlockBefore - s.Player.Block);

        if (playerUnblocked == 0 && ostyUnblocked == 0 && wuppyUnblocked == 0 && blocked > 0)
            fullyBlockedAnyHit = true;

        if (playerUnblocked > 0)
            playerUnblockedHits++;

        if (playerUnblocked > 0 || ostyUnblocked > 0 || wuppyUnblocked > 0)
            suckTriggeredHits++;

        return (s.Player.Flags & HotCreatureFlags.Alive) != 0
            && (s.Enemies[slot].Vital.Flags & HotCreatureFlags.Alive) != 0;
    }

    private static void ExecuteWaterfallExplode(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte slot,
        byte dealerSlot,
        int steamDamage)
    {
        if ((s.Enemies[slot].Vital.Flags & HotCreatureFlags.Alive) != 0
            && (s.Player.Flags & HotCreatureFlags.Alive) != 0)
        {
            scratch.AttackActive = true;
            HotCombatEngine.DealDamage(
                ref s,
                ref cold,
                rules,
                scratch,
                dealerSlot: dealerSlot,
                targetSlot: 0,
                baseDamage: steamDamage,
                isPoweredAttack: true);
            s.Enemies[slot].CreatureAttacksThisTurn++;
            scratch.AttackActive = false;

            if (scratch.NeedsChoice || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
                return;
        }

        HotCombatEngine.KillCreature(ref s, ref cold, rules, scratch, dealerSlot, force: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AdjustMonsterMoveDamage(
        in CombatHotState s,
        byte slot,
        HotMonsterId monsterId,
        string moveId,
        int baseDamage)
    {
        if (monsterId == HotMonsterId.TheForgotten && string.Equals(moveId, "DREAD", StringComparison.Ordinal))
            return baseDamage + s.Enemies[slot].AiRegs[0];
        if (string.Equals(moveId, "PRESSURE_GUN_MOVE", StringComparison.Ordinal))
            return baseDamage + s.Enemies[slot].AiRegs[0];
        if (monsterId == HotMonsterId.WaterfallGiant && string.Equals(moveId, "EXPLODE_MOVE", StringComparison.Ordinal))
            return s.Enemies[slot].AiRegs[1] > 0 ? s.Enemies[slot].AiRegs[1] : baseDamage;
        return baseDamage;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolveAttackRepeats(
        in CombatHotState s,
        byte slot,
        HotMonsterDef monsterDef,
        HotMonsterId monsterId,
        string moveId,
        int defaultRepeats)
    {
        if (monsterId == HotMonsterId.TestSubject && string.Equals(moveId, "MULTI_CLAW_MOVE", StringComparison.Ordinal))
        {
            int baseCount = monsterDef.TestSubjectBaseMultiClawCount > 0 ? monsterDef.TestSubjectBaseMultiClawCount : 3;
            return Math.Max(1, baseCount + s.Enemies[slot].AiRegs[1]);
        }
        return Math.Max(1, defaultRepeats);
    }

    // -------------------------------------------------------------------------
    // Post-Attack Explicit Move Effects (ALL 99 Monsters / 179 Moves)
    // -------------------------------------------------------------------------

    private static bool ApplyPostAttackMoveEffects(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte slot,
        byte dealerSlot,
        HotMonsterDef monsterDef,
        HotMonsterId monsterId,
        string moveId)
    {
        // Revive moves (TestSubject RESPAWN_MOVE, Decimillipede REATTACH_MOVE, Illusion REVIVE_MOVE).
        ResolveReviveMove(ref s, ref cold, rules, scratch, slot, dealerSlot, monsterDef, monsterId, moveId);

        // STUNNED wake/unstun cleanup.
        if (string.Equals(moveId, "STUNNED", StringComparison.Ordinal))
        {
            if (monsterId == HotMonsterId.SlumberingBeetle
                && (s.Enemies[slot].Flags & HotEnemyFlags.Awake) == 0
                && HotCombatEngine.GetPowerAmountById(in s, rules, dealerSlot, HotPowerId.SlumberPower) <= 0)
            {
                // 0.113.1 SlumberPower.cs:25-30 & SlumberingBeetle.cs:95-107:
                // SlumberPower stuns into WakeUpMove, which sets IsAwake = true and removes PlatingPower.
                s.Enemies[slot].Flags |= HotEnemyFlags.Awake;
                RemovePowerById(ref s, rules, dealerSlot, HotPowerId.PlatingPower);
            }
            else if (monsterId == HotMonsterId.LagavulinMatriarch
                && (s.Enemies[slot].Flags & HotEnemyFlags.Awake) == 0
                && HotCombatEngine.GetPowerAmountById(in s, rules, dealerSlot, HotPowerId.AsleepPower) <= 0)
            {
                s.Enemies[slot].Flags |= HotEnemyFlags.Awake;
                RemovePowerById(ref s, rules, dealerSlot, HotPowerId.PlatingPower);
            }
            else if (monsterId == HotMonsterId.Tunneler)
            {
                s.Enemies[slot].Flags &= ~HotEnemyFlags.Stunned;
            }
        }

        // FrogKnight BeetleCharge latch.
        if (monsterId == HotMonsterId.FrogKnight && string.Equals(moveId, "BEETLE_CHARGE", StringComparison.Ordinal))
            s.Enemies[slot].Flags |= HotEnemyFlags.BeetleCharged;

        // TwoTailedRat summon cooldown decrement on non-backup moves.
        if (monsterId == HotMonsterId.TwoTailedRat
            && (string.Equals(moveId, "SCRATCH_MOVE", StringComparison.Ordinal)
                || string.Equals(moveId, "DISEASE_BITE_MOVE", StringComparison.Ordinal)
                || string.Equals(moveId, "SCREECH_MOVE", StringComparison.Ordinal)))
        {
            s.Enemies[slot].AiRegs[0]--;
        }

        bool isDread = rules.RulesSnapshot.IsDread;
        bool isBlitz = rules.RulesSnapshot.IsBlitz;

        switch (monsterId)
        {
            case HotMonsterId.Fabricator:
                if (moveId == "FABRICATE_MOVE")
                {
                    SpawnFabricatorBot(ref s, ref cold, rules, scratch, slot, dealerSlot, defensive: true);
                    SpawnFabricatorBot(ref s, ref cold, rules, scratch, slot, dealerSlot, defensive: false);
                    return true;
                }
                if (moveId == "FABRICATING_STRIKE_MOVE")
                {
                    SpawnFabricatorBot(ref s, ref cold, rules, scratch, slot, dealerSlot, defensive: false);
                    return true;
                }
                return false;

            case HotMonsterId.Fogmog:
                if (moveId == "ILLUSION_MOVE")
                {
                    SpawnMonster(ref s, ref cold, rules, scratch, HotMonsterId.EyeWithTeeth, sourceSlot: dealerSlot, minion: false);
                    return true;
                }
                if (moveId is "SWIPE_MOVE" or "SWIPE_RANDOM_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.LivingFog:
                if (moveId == "BLOAT_MOVE")
                    return true;
                if (moveId == "ADVANCED_GAS_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.SmoggyPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.Ovicopter:
                if (moveId == "LAY_EGGS_MOVE")
                {
                    for (int i = 0; i < 3; i++)
                    {
                        if (!HasFreeEncounterSlot(in s, rules))
                            break;
                        SpawnMonster(ref s, ref cold, rules, scratch, HotMonsterId.ToughEgg, sourceSlot: dealerSlot, minion: true);
                    }
                    return true;
                }
                if (moveId == "TENDERIZER_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VulnerablePower, 2);
                    return true;
                }
                if (moveId == "NUTRITIONAL_PASTE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "NutritionalPasteStrengthAmount", IsDeadly(rules) ? 4 : 3));
                    return true;
                }
                return false;

            case HotMonsterId.TheObscura:
                if (moveId == "ILLUSION_MOVE")
                {
                    SpawnMonster(ref s, ref cold, rules, scratch, HotMonsterId.Parafright, sourceSlot: dealerSlot, minion: false);
                    s.Enemies[slot].Flags |= HotEnemyFlags.HasSummoned;
                    return true;
                }
                if (moveId == "HARDENING_STRIKE_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, GetStaticInt(monsterDef, "HardeningStrikeBlock", 6));
                    return true;
                }
                if (moveId == "SAIL_MOVE")
                {
                    for (int r = 0; r < s.RosterCount; r++)
                    {
                        byte other = s.ActiveEnemyRoster[r];
                        if ((s.Enemies[other].Vital.Flags & HotCreatureFlags.Alive) != 0)
                            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + other), dealerSlot, HotPowerId.StrengthPower, 3);
                    }
                    return true;
                }
                return false;

            case HotMonsterId.TwoTailedRat:
                if (moveId == "CALL_FOR_BACKUP_MOVE")
                {
                    if (HasFreeEncounterSlot(in s, rules))
                        SpawnMonster(ref s, ref cold, rules, scratch, HotMonsterId.TwoTailedRat, sourceSlot: dealerSlot, minion: false);

                    int nextCount = 0;
                    for (int r = 0; r < s.RosterCount; r++)
                    {
                        byte other = s.ActiveEnemyRoster[r];
                        if (ResolveMonsterId(rules.Monsters[s.Enemies[other].MonsterDefId]) == HotMonsterId.TwoTailedRat)
                        {
                            int candidate = s.Enemies[other].AiRegs[1] + 1;
                            if (candidate > nextCount)
                                nextCount = candidate;
                        }
                    }
                    for (int r = 0; r < s.RosterCount; r++)
                    {
                        byte other = s.ActiveEnemyRoster[r];
                        if (ResolveMonsterId(rules.Monsters[s.Enemies[other].MonsterDefId]) == HotMonsterId.TwoTailedRat)
                            s.Enemies[other].AiRegs[1] = nextCount;
                    }
                    return true;
                }
                if (moveId == "SCREECH_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.ToughEgg:
                if (moveId == "HATCH_MOVE")
                {
                    s.Enemies[slot].Flags |= HotEnemyFlags.Hatched;
                    RemoveAllPowersExceptMinion(ref s, rules, dealerSlot);
                    int minHp = GetStaticInt(monsterDef, "HatchlingMinHp", IsTough(rules) ? 20 : 19);
                    int maxHp = GetStaticInt(monsterDef, "HatchlingMaxHp", IsTough(rules) ? 23 : 22);
                    int hp = HotRng.NextInt(ref s.Rng[(int)HotRngStream.Niche], minHp, maxHp + 1);
                    s.Enemies[slot].Vital.MaxHp = hp;
                    s.Enemies[slot].Vital.CurrentHp = hp;
                    return true;
                }
                return false;

            case HotMonsterId.FatGremlin:
                if (moveId == "FLEE_MOVE")
                {
                    CreatureEscaped(ref s, rules, slot);
                    return true;
                }
                return false;

            case HotMonsterId.ThievingHopper:
                if (moveId == "ESCAPE_MOVE")
                {
                    CreatureEscaped(ref s, rules, slot);
                    return true;
                }
                if (moveId == "FLUTTER_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FlutterPower, 5);
                    return true;
                }
                if (moveId == "THIEVERY_MOVE")
                    return true;
                return false;

            case HotMonsterId.MagiKnight:
                if (moveId == "DAMPEN_MOVE")
                {
                    ApplyDampen(ref s, ref cold, rules, scratch, slot, dealerSlot);
                    return true;
                }
                if (moveId is "POWER_SHIELD_MOVE" or "PREP_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot,
                        GetStaticInt(monsterDef, "PowerShieldBlock", IsTough(rules) ? 9 : 5));
                    return true;
                }
                return false;

            case HotMonsterId.KnowledgeDemon:
                if (moveId == "CURSE_OF_KNOWLEDGE_MOVE")
                {
                    ResolveKnowledgeDemonCurse(ref s, ref cold, rules, scratch, slot);
                    return true;
                }
                if (moveId == "PONDER_MOVE")
                {
                    HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, dealerSlot, 30);
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "PonderStrength", IsDeadly(rules) ? 3 : 2));
                    return true;
                }
                return false;

            case HotMonsterId.TestSubject:
                switch (moveId)
                {
                    case "RESPAWN_MOVE":
                    case "PHASE3_LACERATE_MOVE":
                    case "BIG_POUNCE":
                        return true;
                    case "SKULL_BASH_MOVE":
                        ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VulnerablePower, 1);
                        return true;
                    case "MULTI_CLAW_MOVE":
                        s.Enemies[slot].AiRegs[1]++;
                        return true;
                    case "BURNING_GROWL_MOVE":
                        AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Burn, HotPileKind.Discard,
                            GetStaticInt(monsterDef, "BurningGrowlBurnCount", IsDeadly(rules) ? 5 : 3), randomPos: false);
                        ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                            GetStaticInt(monsterDef, "BurningGrowlStrengthGain", IsDeadly(rules) ? 3 : 2));
                        return true;
                    default:
                        return false;
                }

            case HotMonsterId.SludgeSpinner:
                if (moveId == "OIL_SPRAY_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 1);
                    return true;
                }
                if (moveId == "RAGE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 3);
                    return true;
                }
                return false;

            case HotMonsterId.Flyconid:
                if (moveId == "VULNERABLE_SPORES_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VulnerablePower, 2);
                    return true;
                }
                if (moveId == "FRAIL_SPORES_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.FrogKnight:
                if (moveId == "FOR_THE_QUEEN")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 5);
                    return true;
                }
                if (moveId == "TONGUE_LASH")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 2);
                    return true;
                }
                if (moveId == "BEETLE_CHARGE")
                    return true;
                return false;

            case HotMonsterId.GasBomb:
                if (moveId == "EXPLODE_MOVE")
                {
                    if ((s.Enemies[slot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                        HotCombatEngine.KillCreature(ref s, ref cold, rules, scratch, dealerSlot, force: true);
                    return true;
                }
                return false;

            case HotMonsterId.GlobeHead:
                if (moveId == "SHOCKING_SLAP")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 2);
                    return true;
                }
                if (moveId == "GALVANIC_BURST")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.BowlbugSilk:
                if (moveId == "TOXIC_SPIT_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.BowlbugEgg:
                if (moveId == "BITE_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, GetStaticInt(monsterDef, "ProtectBlock", 8));
                    return true;
                }
                return false;

            case HotMonsterId.HauntedShip:
                if (moveId == "HAUNT_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 3);
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Dazed, HotPileKind.Discard, 5, randomPos: false);
                    return true;
                }
                return false;

            case HotMonsterId.HunterKiller:
                if (moveId == "TENDERIZING_GOOP_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.TenderPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.Guardbot:
                if (moveId == "GUARD_MOVE")
                {
                    for (int r = 0; r < s.RosterCount; r++)
                    {
                        byte other = s.ActiveEnemyRoster[r];
                        if ((s.Enemies[other].Vital.Flags & HotCreatureFlags.Alive) != 0
                            && ResolveMonsterId(rules.Monsters[s.Enemies[other].MonsterDefId]) == HotMonsterId.Fabricator)
                        {
                            HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, (byte)(3 + other), 15, isCardBlock: false);
                        }
                    }
                    return true;
                }
                return false;

            case HotMonsterId.InfestedPrism:
                if (moveId == "PULSATE_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VulnerablePower, 1);
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VitalSparkPower,
                        GetStaticInt(monsterDef, "VitalSparkAmount", IsDeadly(rules) ? 2 : 1));
                    return true;
                }
                return false;

            case HotMonsterId.KinFollower:
                if (moveId == "POWER_DANCE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "DanceStrength", 2));
                    return true;
                }
                return false;

            case HotMonsterId.KinPriest:
                if (moveId == "ORB_OF_FRAILTY_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 1);
                    return true;
                }
                if (moveId == "ORB_OF_WEAKNESS_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 1);
                    return true;
                }
                if (moveId == "RITUAL_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "RitualStrength", 2));
                    return true;
                }
                return false;

            case HotMonsterId.LagavulinMatriarch:
                if (moveId == "SLASH2_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, GetStaticInt(monsterDef, "Slash2Block", 8));
                    return true;
                }
                if (moveId == "SOUL_SIPHON_MOVE")
                {
                    int siphonAmt = GetStaticInt(monsterDef, "DebuffAmount", -1);
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, dealerSlot, HotPowerId.StrengthPower, siphonAmt);
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, dealerSlot, HotPowerId.DexterityPower, siphonAmt);
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.LeafSlimeM:
                if (moveId == "STICKY_SHOT")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Slimed, HotPileKind.Discard, 2, randomPos: false);
                    return true;
                }
                return false;

            case HotMonsterId.LeafSlimeS:
                if (moveId == "GOOP_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Slimed, HotPileKind.Discard, 1, randomPos: false);
                    return true;
                }
                return false;

            case HotMonsterId.LivingShield:
                if (moveId == "SMASH_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 3);
                    return true;
                }
                return false;

            case HotMonsterId.Mawler:
                if (moveId == "ROAR_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VulnerablePower, 3);
                    return true;
                }
                return false;

            case HotMonsterId.Myte:
                if (moveId == "TOXIC_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Toxic, HotPileKind.Hand, 2, randomPos: false);
                    return true;
                }
                if (moveId == "SUCK_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "SuckStrength", 2));
                    return true;
                }
                return false;

            case HotMonsterId.Nibbit:
                if (moveId == "SLICE_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, GetStaticInt(monsterDef, "SliceBlock", 5));
                    return true;
                }
                if (moveId == "HISS_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "HissStrengthGain", 2));
                    return true;
                }
                return false;

            case HotMonsterId.Chomper:
                if (moveId == "SCREECH_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Dazed, HotPileKind.Discard, 3, randomPos: false);
                    return true;
                }
                return false;

            case HotMonsterId.MechaKnight:
                if (moveId == "FLAMETHROWER_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Burn, HotPileKind.Hand, 4, randomPos: false);
                    return true;
                }
                if (moveId == "WINDUP_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, 15);
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 5);
                    return true;
                }
                return false;

            case HotMonsterId.PunchConstruct:
                if (moveId == "READY_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, 10);
                    return true;
                }
                if (moveId == "FAST_PUNCH_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.Seapunk:
                if (moveId == "BUBBLE_BURP_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, GetStaticInt(monsterDef, "BubbleBlock", 8));
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "BubbleStr", 2));
                    return true;
                }
                return false;

            case HotMonsterId.SewerClam:
                if (moveId == "PRESSURIZE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 4);
                    return true;
                }
                return false;

            case HotMonsterId.Wriggler:
                if (moveId == "WRIGGLE_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Infection, HotPileKind.Discard, 1, randomPos: false);
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.FlailKnight:
            case HotMonsterId.MysteriousKnight:
                if (moveId == "WAR_CHANT")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 3);
                    return true;
                }
                return false;

            case HotMonsterId.Exoskeleton:
                if (moveId == "ENRAGE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.BowlbugNectar:
                if (moveId == "BUFF_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "BuffStrengthGain", 15));
                    return true;
                }
                return false;

            case HotMonsterId.CorpseSlug:
                if (moveId == "GOOP_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower,
                        GetStaticInt(monsterDef, "GoopFrailAmt", 2));
                    return true;
                }
                return false;

            case HotMonsterId.SoulFysh:
                if (moveId == "SCREAM_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VulnerablePower,
                        GetStaticInt(monsterDef, "ScreamMoveAmount", 2));
                    return true;
                }
                if (moveId == "BECKON_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Beckon, HotPileKind.Draw, 1, randomPos: true);
                    if (isDread)
                        AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Beckon, HotPileKind.Hand, 1, randomPos: false);
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Beckon, HotPileKind.Discard, 1, randomPos: false);
                    return true;
                }
                if (moveId == "GAZE_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Beckon, HotPileKind.Discard,
                        GetStaticInt(monsterDef, "GazeMoveAmount", 1), randomPos: false);
                    return true;
                }
                if (moveId == "FADE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.IntangiblePower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.TheLost:
                if (moveId == "DEBILITATING_SMOG")
                {
                    int strSteal = GetStaticInt(monsterDef, "DebilitatingSmogStrengthStealAmount", 2);
                    int before = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.StrengthPower);
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, dealerSlot, HotPowerId.StrengthPower, -strSteal);
                    int applied = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.StrengthPower) - before;
                    if (applied < 0)
                        RecordPossessedStatDelta(ref s, rules, dealerSlot, HotPowerId.PossessStrengthPower, applied);
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, strSteal);
                    return true;
                }
                return false;

            case HotMonsterId.CalcifiedCultist:
            case HotMonsterId.DampCultist:
                if (moveId == "INCANTATION_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.RitualPower,
                        GetStaticInt(monsterDef, "IncantationAmount", 2));
                    return true;
                }
                return false;

            case HotMonsterId.CeremonialBeast:
                if (moveId == "PLOW_MOVE")
                {
                    if (HotCombatEngine.GetPowerAmountById(in s, rules, dealerSlot, HotPowerId.PlowPower) > 0)
                    {
                        ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                            GetStaticInt(monsterDef, "PlowStrength", 2));
                    }
                    return true;
                }
                if (moveId == "CRUSH_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "CrushStrength", 3));
                    return true;
                }
                if (moveId == "STAMP_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.PlowPower,
                        GetStaticInt(monsterDef, "PlowAmount", 150));
                    return true;
                }
                if (moveId == "BEAST_CRY_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.RingingPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.EyeWithTeeth:
                if (moveId == "DISTRACT_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Dazed, HotPileKind.Discard, 3, randomPos: false);
                    return true;
                }
                return false;

            case HotMonsterId.SpinyToad:
                if (moveId == "PROTRUDING_SPIKES_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.ThornsPower, 5);
                    return true;
                }
                if (moveId == "SPIKE_EXPLOSION_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.ThornsPower, -5);
                    return true;
                }
                return false;

            case HotMonsterId.Stabbot:
                if (moveId == "STAB_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.ScrollOfBiting:
                if (moveId == "MORE_TEETH")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.ShrinkerBeetle:
                if (moveId == "SHRINKER_MOVE")
                {
                    ApplyInfiniteShrinkDebuff(ref s, ref cold, rules, scratch, dealerSlot);
                    return true;
                }
                return false;

            case HotMonsterId.VineShambler:
                if (moveId == "GRASPING_VINES_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.TangledPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.SlitheringStrangler:
                if (moveId == "THWACK")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, 5);
                    return true;
                }
                if (moveId == "CONSTRICT")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.ConstrictPower, 3);
                    return true;
                }
                return false;

            case HotMonsterId.SpectralKnight:
                if (moveId == "HEX")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.HexPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.SnappingJaxfruit:
                if (moveId == "ENERGY_ORB_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.SoulNexus:
                if (moveId == "DRAIN_LIFE_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VulnerablePower, 2);
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.SkulkingColony:
                if (moveId == "INERTIA_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "InertiaStrengthGain", 3));
                    return true;
                }
                return false;

            case HotMonsterId.SlimedBerserker:
                if (moveId == "VOMIT_ICHOR_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Slimed, HotPileKind.Discard, 10, randomPos: false);
                    return true;
                }
                if (moveId == "LEECHING_HUG_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 3);
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 3);
                    return true;
                }
                return false;

            case HotMonsterId.TerrorEel:
                if (moveId == "TERROR_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VulnerablePower, 99);
                    return true;
                }
                if (moveId == "THRASH_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VigorPower, 6);
                    return true;
                }
                return false;

            case HotMonsterId.TwigSlimeM:
                if (moveId == "STICKY_SHOT_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Slimed, HotPileKind.Discard, 1, randomPos: false);
                    return true;
                }
                return false;

            case HotMonsterId.TurretOperator:
                if (moveId == "RELOAD_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.TheAdversaryMkOne:
                if (moveId == "BARRAGE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.TheAdversaryMkTwo:
                if (moveId == "BARRAGE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 3);
                    return true;
                }
                return false;

            case HotMonsterId.TheAdversaryMkThree:
                if (moveId == "BARRAGE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 4);
                    return true;
                }
                return false;

            case HotMonsterId.Toadpole:
                if (moveId == "SPIKEN_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.ThornsPower, 2);
                    return true;
                }
                if (moveId == "SPIKE_SPIT_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.ThornsPower, -2);
                    return true;
                }
                return false;

            case HotMonsterId.PhrogParasite:
                if (moveId == "INFECT_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Infection, HotPileKind.Discard, 3, randomPos: false);
                    return true;
                }
                return false;

            case HotMonsterId.Vantom:
                if (moveId == "DISMEMBER_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Wound, HotPileKind.Discard, 3, randomPos: false);
                    return true;
                }
                if (moveId == "PREPARE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "PrepareStrength", 2));
                    return true;
                }
                return false;

            case HotMonsterId.TheInsatiable:
                if (moveId == "SALIVATE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "SalivateStrength", 3));
                    return true;
                }
                if (moveId == "LIQUIFY_GROUND_MOVE")
                {
                    int sandpitAmt = GetStaticInt(monsterDef, "SandpitAmount", 6);
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, dealerSlot, dealerSlot, HotPowerId.SandpitPower, sandpitAmt);
                    ushort sandpitDefId = rules.GetPowerDefId(HotPowerId.SandpitPower);
                    if (sandpitDefId != 0)
                    {
                        int spIdx = HotCombatEngine.FindPowerIndex(in s, dealerSlot, sandpitDefId);
                        if (spIdx >= 0)
                            s.Powers[spIdx].TargetSlot = 0;
                    }
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.FranticEscape, HotPileKind.Draw, 3, randomPos: true);
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.FranticEscape, HotPileKind.Discard, 3, randomPos: true);
                    return true;
                }
                return false;

            case HotMonsterId.TheForgotten:
                if (moveId == "MIASMA")
                {
                    int dexSteal = GetStaticInt(monsterDef, "DebilitatingSmogDexStealAmount", 2);
                    int beforePlayerDex = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.DexterityPower);
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, dealerSlot, HotPowerId.DexterityPower, -dexSteal);
                    int appliedPlayerDex = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.DexterityPower) - beforePlayerDex;
                    if (appliedPlayerDex < 0)
                        RecordPossessedStatDelta(ref s, rules, dealerSlot, HotPowerId.PossessSpeedPower, appliedPlayerDex);

                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, 8);

                    int beforeSelfDex = HotCombatEngine.GetPowerAmountById(in s, rules, dealerSlot, HotPowerId.DexterityPower);
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.DexterityPower, dexSteal);
                    int appliedSelfDex = HotCombatEngine.GetPowerAmountById(in s, rules, dealerSlot, HotPowerId.DexterityPower) - beforeSelfDex;
                    s.Enemies[slot].AiRegs[0] += appliedSelfDex;
                    return true;
                }
                return false;

            case HotMonsterId.OwlMagistrate:
                if (moveId == "JUDICIAL_FLIGHT")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.SoarPower, 1);
                    return true;
                }
                if (moveId == "VERDICT")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VulnerablePower, 4);
                    RemovePowerById(ref s, rules, dealerSlot, HotPowerId.SoarPower);
                    return true;
                }
                return false;

            case HotMonsterId.Queen:
                if (moveId == "PUPPET_STRINGS_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.ChainsOfBindingPower, 3);
                    return true;
                }
                if (moveId is "DREAD_INITIAL_MOVE" or "YOU_ARE_MINE_MOVE")
                {
                    if (moveId == "DREAD_INITIAL_MOVE")
                        ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.ChainsOfBindingPower, 3);
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 99);
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 99);
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.VulnerablePower, 99);
                    return true;
                }
                if (moveId == "BURN_BRIGHT_FOR_ME_MOVE")
                {
                    int allyStr = isDread ? 2 : 1;
                    for (int r = 0; r < s.RosterCount; r++)
                    {
                        byte other = s.ActiveEnemyRoster[r];
                        if (other != slot && (s.Enemies[other].Vital.Flags & HotCreatureFlags.Alive) != 0)
                            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, (byte)(3 + other), dealerSlot, HotPowerId.StrengthPower, allyStr);
                    }
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, 20);
                    return true;
                }
                if (moveId == "ENRAGE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.LouseProgenitor:
                if (moveId == "WEB_CANNON_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 2);
                    return true;
                }
                if (moveId == "CURL_AND_GROW_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, GetStaticInt(monsterDef, "CurlBlock", 14));
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "GrowStrength", 5));
                    return true;
                }
                return false;

            case HotMonsterId.Tunneler:
                if (moveId == "BURROW_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.BurrowedPower, 1);
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, GetStaticInt(monsterDef, "BlockGain", 30));
                    return true;
                }
                return false;

            case HotMonsterId.CubexConstruct:
                if (moveId is "CHARGE_UP_MOVE" or "REPEATER_BLAST_MOVE" or "REPEATER_BLAST_MOVE_2")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.SlumberingBeetle:
                if (moveId == "ROLL_OUT_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.Crusher:
                if (moveId == "BUG_STING_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 2);
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 2);
                    return true;
                }
                if (moveId == "ADAPT_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "AdaptStrengthGain", 2));
                    return true;
                }
                if (moveId == "GUARDED_STRIKE_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, 18);
                    return true;
                }
                return false;

            case HotMonsterId.Rocket:
                if (moveId == "CHARGE_UP_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "ChargeUpStrengthGain", 2));
                    return true;
                }
                return false;

            case HotMonsterId.TrackerRubyRaider:
                if (moveId == "TRACK_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.Noisebot:
                if (moveId == "NOISE_MOVE")
                {
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Dazed, HotPileKind.Discard, 1, randomPos: false);
                    AddMonsterStatusCards(ref s, ref cold, rules, scratch, HotCustomCardId.Dazed, HotPileKind.Draw, 1, randomPos: true);
                    return true;
                }
                return false;

            case HotMonsterId.PhantasmalGardener:
                if (moveId == "ENLARGE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower,
                        GetStaticInt(monsterDef, "EnlargeStr", 3));
                    return true;
                }
                return false;

            case HotMonsterId.FuzzyWurmCrawler:
                if (moveId == "INHALE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 7);
                    return true;
                }
                return false;

            case HotMonsterId.Axebot:
                if (moveId == "BOOT_UP_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot,
                        GetStaticInt(monsterDef, "BootUpBlock", IsDeadly(rules) ? 15 : 10));
                    // 0.113.1 Axebot.cs:54,116: AiRegs[0] encodes (RespawnCount + 1) so 0 = unset fallback.
                    int respawnCount = s.Enemies[slot].AiRegs[0] > 0
                        ? s.Enemies[slot].AiRegs[0] - 1
                        : GetStaticInt(monsterDef, "RespawnCount", 0);
                    int strGain = GetStaticInt(monsterDef, "BootUpStrGain", IsDeadly(rules) ? 4 : 3) * respawnCount;
                    if (strGain != 0)
                        ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, strGain);
                    return true;
                }
                if (moveId == "HAMMER_UPPERCUT_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 2);
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.Aeonglass:
                if (moveId == "EBB_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, GetStaticInt(monsterDef, "EbbBlock", 33));
                    return true;
                }
                if (moveId == "INCREASING_INTENSITY_MOVE")
                {
                    FakeUpgradeAllWitherCards(ref s, rules);
                    s.Enemies[slot].AiRegs[1]++; // WitherUpgradeCount++
                    AddMonsterStatusCards(
                        ref s,
                        ref cold,
                        rules,
                        scratch,
                        HotCustomCardId.Wither,
                        isDread ? HotPileKind.Draw : HotPileKind.Discard,
                        GetStaticInt(monsterDef, "WitherAmount", IsDeadly(rules) ? 3 : 2),
                        randomPos: true);
                    int addStr = s.Enemies[slot].AiRegs[0]++; // AdditionalStrength++
                    int baseStr = GetStaticInt(monsterDef, "IncreasingIntensityBaseStrength", IsDeadly(rules) ? 4 : 3);
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, baseStr + addStr);
                    return true;
                }
                return false;

            case HotMonsterId.AxeRubyRaider:
                if (moveId is "SWING_1" or "SWING_2")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, GetStaticInt(monsterDef, "SwingBlock", 5));
                    return true;
                }
                return false;

            case HotMonsterId.BruteRubyRaider:
                if (moveId == "ROAR_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 3);
                    return true;
                }
                return false;

            case HotMonsterId.BygoneEffigy:
                if (moveId == "WAKE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 10);
                    return true;
                }
                return false;

            case HotMonsterId.CrossbowRubyRaider:
                if (moveId == "RELOAD_MOVE")
                {
                    GainMoveBlock(ref s, ref cold, rules, scratch, dealerSlot, 3);
                    return true;
                }
                return false;

            case HotMonsterId.DevotedSculptor:
                if (moveId == "FORBIDDEN_INCANTATION_MOVE")
                {
                    HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, dealerSlot, 0xFF, HotPowerId.RitualPower, 9);
                    return true;
                }
                return false;

            case HotMonsterId.Entomancer:
                if (moveId == "PHEROMONE_SPIT_MOVE")
                {
                    int hive = HotCombatEngine.GetPowerAmountById(in s, rules, dealerSlot, HotPowerId.PersonalHivePower);
                    if (hive <= 0 || hive >= 3)
                    {
                        ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    }
                    else
                    {
                        ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.PersonalHivePower, 1);
                        ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 1);
                    }
                    return true;
                }
                return false;

            case HotMonsterId.FakeMerchantMonster:
                if (moveId == "ENRAGE_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                if (moveId == "THROW_RELIC_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.FossilStalker:
                if (moveId == "TACKLE_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.FrailPower, 1);
                    return true;
                }
                return false;

            case HotMonsterId.WaterfallGiant:
                switch (moveId)
                {
                    case "PRESSURIZE_MOVE":
                        ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.SteamEruptionPower,
                            GetStaticInt(monsterDef, "PressurizeAmount", IsDeadly(rules) ? 20 : 15));
                        return true;
                    case "STOMP_MOVE":
                        ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 1);
                        ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.SteamEruptionPower,
                            GetStaticInt(monsterDef, "SteamBoostAmount", 3));
                        return true;
                    case "RAM_MOVE":
                    case "PRESSURE_UP_MOVE":
                        ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.SteamEruptionPower,
                            GetStaticInt(monsterDef, "SteamBoostAmount", 3));
                        return true;
                    case "SIPHON_MOVE":
                        HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, dealerSlot,
                            GetStaticInt(monsterDef, "SiphonHeal", IsTough(rules) ? 15 : 10));
                        ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.SteamEruptionPower,
                            GetStaticInt(monsterDef, "SteamBoostAmount", 3));
                        return true;
                    case "PRESSURE_GUN_MOVE":
                        s.Enemies[slot].AiRegs[0] += GetStaticInt(monsterDef, "PressureGunIncrease", 5);
                        ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.SteamEruptionPower,
                            GetStaticInt(monsterDef, "SteamBoostAmount", 3));
                        return true;
                    case "ABOUT_TO_BLOW_MOVE":
                        PrepareSteamEruption(ref s, rules, slot);
                        return true;
                    case "EXPLODE_MOVE":
                        s.Enemies[slot].SteamPhase = 0;
                        HotCombatEngine.KillCreature(ref s, ref cold, rules, scratch, dealerSlot, force: true);
                        return true;
                    default:
                        return false;
                }

            case HotMonsterId.DecimillipedeSegmentBack:
            case HotMonsterId.DecimillipedeSegmentFront:
            case HotMonsterId.DecimillipedeSegmentMiddle:
                if (moveId == "BULK_MOVE")
                {
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                if (moveId == "CONSTRICT_MOVE")
                {
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 1);
                    return true;
                }
                if (moveId is "DEAD_MOVE" or "REATTACH_MOVE")
                    return true;
                return false;

            case HotMonsterId.GremlinMerc:
                if (moveId == "DOUBLE_SMASH_MOVE")
                {
                    if (!isBlitz) RecordThievery(ref s, rules, dealerSlot);
                    ApplyPlayerDebuff(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.WeakPower, 2);
                    return true;
                }
                if (moveId == "GIMME_MOVE")
                {
                    if (!isBlitz) RecordThievery(ref s, rules, dealerSlot);
                    return true;
                }
                if (moveId == "HEHE_MOVE")
                {
                    if (!isBlitz) RecordThievery(ref s, rules, dealerSlot);
                    ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.StrengthPower, 2);
                    return true;
                }
                return false;

            case HotMonsterId.Parafright:
                if (moveId == "REVIVE_MOVE")
                    return true;
                return false;

            default:
                return false;
        }
    }

    private static void ExecuteCompiledMoveEffects(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        HotMonsterMoveEffect[] effects)
    {
        for (int i = 0; i < effects.Length; i++)
        {
            if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
                break;

            HotMonsterMoveEffect effect = effects[i];
            switch (effect.Op)
            {
                case HotCardOpKind.GainBlock:
                {
                    bool isPoweredBlock = ((HotCardOpFlags)effect.Flags & HotCardOpFlags.Unpowered) == 0;
                    HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot: dealerSlot, baseAmount: effect.Amount, isCardBlock: isPoweredBlock);
                    break;
                }
                case HotCardOpKind.ApplyPowerSelf:
                    HotCombatEngine.ApplyPower(ref s, ref cold, rules, scratch, ownerSlot: dealerSlot, applierSlot: dealerSlot, powerDefId: effect.TargetDefId, amount: effect.Amount);
                    break;
                case HotCardOpKind.ApplyPowerTarget:
                    HotCombatEngine.ApplyPower(ref s, ref cold, rules, scratch, ownerSlot: 0, applierSlot: dealerSlot, powerDefId: effect.TargetDefId, amount: effect.Amount);
                    break;
                case HotCardOpKind.Heal:
                    HotCombatEngine.HealCreature(ref s, ref cold, rules, scratch, targetSlot: dealerSlot, amount: effect.Amount);
                    break;
                case HotCardOpKind.LoseHp:
                    HotCombatEngine.DealDamage(ref s, ref cold, rules, scratch, dealerSlot: dealerSlot, targetSlot: 0, baseDamage: effect.Amount, isPoweredAttack: false, isUnblockable: true);
                    break;
                case HotCardOpKind.AddFixedCard:
                    for (int c = 0; c < effect.Amount; c++)
                        HotCombatEngine.AddGeneratedFixedCard(ref s, ref cold, rules, scratch, effect.TargetDefId, HotPileKind.Discard, dstOffset: -1, upgraded: false, createdByPlayer: false);
                    break;
            }
            if (scratch.NeedsChoice)
                return;
        }
    }

    // -------------------------------------------------------------------------
    // Revival, KnowledgeDemon, Dampen, Thievery, Aeonglass & Affliction Helpers
    // -------------------------------------------------------------------------

    private static void ResolveReviveMove(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte slot,
        byte dealerSlot,
        HotMonsterDef monsterDef,
        HotMonsterId monsterId,
        string moveId)
    {
        if (monsterId == HotMonsterId.TestSubject && string.Equals(moveId, "RESPAWN_MOVE", StringComparison.Ordinal))
        {
            int respawns = ++s.Enemies[slot].AiRegs[0];
            int hp = respawns == 1
                ? GetStaticInt(monsterDef, "SecondFormHp", IsTough(rules) ? 212 : 200)
                : GetStaticInt(monsterDef, "ThirdFormHp", IsTough(rules) ? 313 : 300);

            s.Enemies[slot].Vital.MaxHp = hp;
            s.Enemies[slot].Vital.CurrentHp = hp;
            s.Enemies[slot].Vital.Flags |= HotCreatureFlags.Alive;
            s.Enemies[slot].DeathPhase = HotDeathPhase.None;

            if (respawns == 1)
            {
                ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.PainfulStabsPower, 1);
            }
            else
            {
                ApplySelf(ref s, ref cold, rules, scratch, dealerSlot, HotPowerId.NemesisPower, 1);
                RemovePowerById(ref s, rules, dealerSlot, HotPowerId.AdaptablePower);
                RemovePowerById(ref s, rules, dealerSlot, HotPowerId.PainfulStabsPower);
            }
            return;
        }

        if (IsDecimillipedeSegment(monsterId) && string.Equals(moveId, "REATTACH_MOVE", StringComparison.Ordinal))
        {
            bool allOthersDead = true;
            for (int r = 0; r < s.RosterCount; r++)
            {
                byte other = s.ActiveEnemyRoster[r];
                if (other == slot)
                    continue;
                if (HotCombatEngine.GetPowerAmountById(in s, rules, (byte)(3 + other), HotPowerId.ReattachPower) > 0
                    && CanPerformMonsterMove(in s, other))
                {
                    allOthersDead = false;
                    break;
                }
            }

            if (!allOthersDead)
            {
                int healAmt = HotCombatEngine.GetPowerAmountById(in s, rules, dealerSlot, HotPowerId.ReattachPower);
                if (healAmt > 0)
                {
                    s.Enemies[slot].Vital.Flags |= HotCreatureFlags.Alive;
                    s.Enemies[slot].Vital.CurrentHp = Math.Min(s.Enemies[slot].Vital.MaxHp, s.Enemies[slot].Vital.CurrentHp + healAmt);
                }
                s.Enemies[slot].DeathPhase = HotDeathPhase.None;
            }
            return;
        }

        if (string.Equals(moveId, "REVIVE_MOVE", StringComparison.Ordinal))
        {
            s.Enemies[slot].Vital.CurrentHp = s.Enemies[slot].Vital.MaxHp;
            s.Enemies[slot].Vital.Flags |= HotCreatureFlags.Alive;
            s.Enemies[slot].DeathPhase = HotDeathPhase.None;
        }
    }

    private static void ResolveKnowledgeDemonCurse(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte slot)
    {
        int counter = s.Enemies[slot].AiRegs[0];
        if ((uint)counter >= 3u)
            return;

        HotCustomCardId opt0 = HotCustomCardId.Disintegration;
        HotCustomCardId opt1 = counter switch
        {
            0 => HotCustomCardId.MindRot,
            1 => HotCustomCardId.Sloth,
            _ => HotCustomCardId.WasteAway,
        };

        Span<ushort> options = stackalloc ushort[2];
        options[0] = rules.GetCardDefId(opt0);
        options[1] = rules.GetCardDefId(opt1);
        HotCombatEngine.SetPendingGeneratedChoiceOptions(options);

        if (!HotCombatEngine.RequestChoice(ref s, scratch, HotChoiceKind.GeneratedCard, 0, 1, 1, out HotChoiceSelection sel))
            return;

        HotCustomCardId chosenId;
        if (sel.CardDefId > 0 && sel.CardDefId < rules.Cards.Length && rules.Cards[sel.CardDefId].CardId != HotCustomCardId.None)
            chosenId = rules.Cards[sel.CardDefId].CardId;
        else
            chosenId = sel.CardUidOrIndex == 0 ? opt0 : opt1;

        int disintDmg = (rules.RulesSnapshot.IsDread ? 8 : 6) + counter;
        switch (chosenId)
        {
            case HotCustomCardId.Disintegration:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.DisintegrationPower, disintDmg);
                break;
            case HotCustomCardId.MindRot:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.MindRotPower, 1);
                break;
            case HotCustomCardId.Sloth:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.SlothPower, 3);
                break;
            case HotCustomCardId.WasteAway:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.WasteAwayPower, 1);
                break;
            case HotCustomCardId.Amnesia:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0, HotPowerId.AmnesiaPower, 1);
                break;
        }

        s.Enemies[slot].AiRegs[0] = counter + 1;
    }

    private static void ApplyDampen(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte enemySlot,
        byte casterSlot)
    {
        ushort dampenDefId = rules.GetPowerDefId(HotPowerId.DampenPower);
        if (dampenDefId == 0)
            return;

        int casterBit = 1 << enemySlot;
        int existingIdx = HotCombatEngine.FindPowerIndex(in s, ownerSlot: 0, dampenDefId);
        if (existingIdx >= 0 && s.Powers[existingIdx].Amount > 0)
        {
            if ((s.Powers[existingIdx].Aux0 & casterBit) != 0)
                return;
            s.Powers[existingIdx].Aux0 |= casterBit;
            return;
        }

        HotCombatEngine.ApplyPower(ref s, ref cold, rules, scratch, ownerSlot: 0, applierSlot: casterSlot, powerDefId: dampenDefId, amount: 1);
        int dampenIdx = HotCombatEngine.FindPowerIndex(in s, ownerSlot: 0, dampenDefId);
        if (dampenIdx < 0)
            return;
        s.Powers[dampenIdx].Aux0 |= casterBit;

        // Downgrade all currently upgraded player cards and record original upgrade levels in HotColdSidecar.DampenEntries.
        int totalCards = s.TotalCardCount;
        int upgradedCount = 0;
        for (int i = 0; i < totalCards; i++)
        {
            if (s.Cards[i].UpgradeLevel > 0)
                upgradedCount++;
        }

        if (upgradedCount == 0)
            return;

        HotDampenEntry[]? prev = cold?.DampenEntries;
        int prevLen = prev?.Length ?? 0;
        HotDampenEntry[] next = new HotDampenEntry[prevLen + upgradedCount];
        if (prevLen > 0)
            prev!.CopyTo(next, 0);

        int cursor = prevLen;
        for (int i = 0; i < totalCards; i++)
        {
            ref HotCard card = ref s.Cards[i];
            if (card.UpgradeLevel == 0)
                continue;

            if (card.CardUid == 0)
                card.CardUid = s.NextCardUid++;

            byte origUpg = card.UpgradeLevel;
            card.DampenOrigUpg = origUpg;
            card.UpgradeLevel = 0;
            if (card.CardDefId < rules.Cards.Length)
            {
                // Match 0.113.1 CardModel.DowngradeInternal (CardModel.cs:2336-2358).
                HotCardDef def = rules.Cards[card.CardDefId];
                card.BaseEnergyCost = def.BaseCost;
                card.BaseStarCost = def.BaseStarCost;
                card.LocalKeywordsMask = ((HotCardKeywordMask)def.BaseKeywords | card.KeywordsAddedOutsideMask)
                    & ~card.KeywordsRemovedOutsideMask;
                ReapplyEnchantmentOnDowngrade(ref card, rules);
            }

            next[cursor++] = new HotDampenEntry(card.CardUid, origUpg, dampenIdx);
        }

        cold = cold == null
            ? new HotColdSidecar(DampenEntries: next)
            : cold with { DampenEntries = next };
    }

    private static void ReapplyEnchantmentOnDowngrade(ref HotCard card, HotRules rules)
    {
        if (card.EnchId == 0 || card.EnchId >= rules.Enchantments.Length)
            return;

        switch (rules.Enchantments[card.EnchId].EnchantmentId)
        {
            case HotEnchantmentId.Goopy:
                card.LocalKeywordsMask |= HotCardKeywordMask.Exhaust;
                break;

            case HotEnchantmentId.RoyallyApproved:
                card.LocalKeywordsMask |= HotCardKeywordMask.Innate | HotCardKeywordMask.Retain;
                break;

            case HotEnchantmentId.SoulsPower:
                card.LocalKeywordsMask &= ~HotCardKeywordMask.Exhaust;
                break;

            case HotEnchantmentId.Steady:
                card.LocalKeywordsMask |= HotCardKeywordMask.Retain;
                break;

            case HotEnchantmentId.TezcatarasEmber:
                if (card.BaseEnergyCost > 0 && (card.Flags & HotCardFlags.CostsX) == 0)
                    card.BaseEnergyCost = 0;
                card.LocalKeywordsMask |= HotCardKeywordMask.Eternal;
                break;
        }
    }

    internal static void RemoveDampenCaster(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        byte enemySlot)
    {
        ushort dampenDefId = rules.GetPowerDefId(HotPowerId.DampenPower);
        if (dampenDefId == 0)
            return;

        int dampenIdx = HotCombatEngine.FindPowerIndex(in s, ownerSlot: 0, dampenDefId);
        if (dampenIdx < 0)
            return;

        byte casterSlot = (byte)(3 + enemySlot);
        int casterBit = 1 << enemySlot;
        int mask = s.Powers[dampenIdx].Aux0;

        if (mask != 0)
        {
            if ((mask & casterBit) == 0)
                return;
            mask &= ~casterBit;
            s.Powers[dampenIdx].Aux0 = mask;
            if (mask != 0)
                return;
        }
        else if (s.Powers[dampenIdx].ApplierSlot != casterSlot)
        {
            return;
        }

        // Last caster died: remove DampenPower and restore all dampened cards (DampenPower.cs:58-71).
        HotCombatEngine.RemovePowerAt(ref s, dampenIdx);
        s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);

        if (cold?.DampenEntries is { Length: > 0 } entries)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                HotDampenEntry entry = entries[i];
                int cardIdx = HotCombatEngine.FindCardIndexByUidAnyPile(in s, entry.CardUid);
                if (cardIdx < 0)
                    continue;
                for (int u = 0; u < entry.OriginalUpgrade; u++)
                    HotCombatEngine.UpgradeCard(ref s, rules, cardIdx);
                s.Cards[cardIdx].DampenOrigUpg = 0;
            }

            HotColdSidecar updated = cold with { DampenEntries = null };
            cold = updated.IsEmpty ? null : updated;
        }

        // Fallback sweep: restore any dampened card whose CardUid wasn't matched via DampenEntries.
        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            byte origUpg = s.Cards[i].DampenOrigUpg;
            if (origUpg == 0)
                continue;
            for (int u = 0; u < origUpg; u++)
                HotCombatEngine.UpgradeCard(ref s, rules, i);
            s.Cards[i].DampenOrigUpg = 0;
        }
    }

    private static void RecordThievery(ref CombatHotState s, HotRules rules, byte dealerSlot)
    {
        if ((s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return;

        ushort thieveryDefId = rules.GetPowerDefId(HotPowerId.ThieveryPower);
        if (thieveryDefId == 0)
            return;

        int idx = HotCombatEngine.FindPowerIndex(in s, dealerSlot, thieveryDefId);
        if (idx < 0 || s.Powers[idx].Amount <= 0)
            return;

        int stolen = Math.Min(s.Powers[idx].Amount, s.Gold);
        if (stolen <= 0)
            return;

        s.OutstandingStolenGold += stolen;
        s.Powers[idx].Aux0 += stolen;
        s.Gold -= stolen;
    }

    private static void PrepareSteamEruption(ref CombatHotState s, HotRules rules, byte slot)
    {
        byte ownerSlot = (byte)(3 + slot);
        int steam = Math.Max(0, HotCombatEngine.GetPowerAmountById(in s, rules, ownerSlot, HotPowerId.SteamEruptionPower));
        s.Enemies[slot].AiRegs[1] = steam;
        RemovePowerById(ref s, rules, ownerSlot, HotPowerId.SteamEruptionPower);
        s.Enemies[slot].SteamPhase = 2; // Explode next (resolved & logged in PrepareNextMove / RollNextMove)
    }

    private static void FakeUpgradeAllWitherCards(ref CombatHotState s, HotRules rules)
    {
        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            ushort defId = s.Cards[i].CardDefId;
            if (defId < rules.Cards.Length && rules.Cards[defId].CardId == HotCustomCardId.Wither)
            {
                s.Cards[i].CardPrivate++;
                s.Cards[i].Var0Delta += 3;
            }
        }
    }

    private static void NormalizeAeonglassWithers(ref CombatHotState s, HotRules rules)
    {
        int expectedUpgrade = 0;
        for (int r = 0; r < s.RosterCount; r++)
        {
            byte eSlot = s.ActiveEnemyRoster[r];
            ushort mDefId = s.Enemies[eSlot].MonsterDefId;
            if (mDefId > 0 && mDefId < rules.Monsters.Length && ResolveMonsterId(rules.Monsters[mDefId]) == HotMonsterId.Aeonglass)
                expectedUpgrade += s.Enemies[eSlot].AiRegs[1];
        }
        if (expectedUpgrade <= 0)
            return;

        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            ushort defId = s.Cards[i].CardDefId;
            if (defId >= rules.Cards.Length || rules.Cards[defId].CardId != HotCustomCardId.Wither)
                continue;
            int current = s.Cards[i].CardPrivate;
            if (current < expectedUpgrade)
            {
                int diff = expectedUpgrade - current;
                s.Cards[i].CardPrivate = expectedUpgrade;
                s.Cards[i].Var0Delta += (short)(diff * 3);
            }
        }
    }

    internal static void NormalizeCardAfflictions(ref CombatHotState s, HotRules rules)
    {
        int hex = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.HexPower);
        int tangled = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.TangledPower);
        int ringing = HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.RingingPower);
        if (hex <= 0 && tangled <= 0 && ringing <= 0)
            return;

        byte hexedDef = rules.GetAfflictionDefId(HotAfflictionId.Hexed);
        byte entangledDef = rules.GetAfflictionDefId(HotAfflictionId.Entangled);
        byte ringingDef = rules.GetAfflictionDefId(HotAfflictionId.Ringing);

        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            ref HotCard card = ref s.Cards[i];
            if (card.AfflId != 0)
                continue;

            if (hex > 0 && hexedDef != 0)
            {
                card.AfflId = hexedDef;
                card.AfflAmt = (short)hex;
            }
            else if (tangled > 0 && entangledDef != 0
                && card.CardDefId < rules.Cards.Length
                && rules.Cards[card.CardDefId].Type == CardType.Attack)
            {
                card.AfflId = entangledDef;
                card.AfflAmt = 1;
            }
            else if (ringing > 0 && ringingDef != 0)
            {
                card.AfflId = ringingDef;
                card.AfflAmt = 1;
            }
        }
    }

    /// <summary>
    /// Clears a specific affliction from all combat cards (matching STS2 0.113.1
    /// <c>HexPower/TangledPower/RingingPower.AfterRemoved</c> without stripping unrelated afflictions).
    /// </summary>
    internal static void ClearCardAffliction(ref CombatHotState s, HotRules rules, HotAfflictionId targetAffliction)
    {
        int total = s.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            ref HotCard card = ref s.Cards[i];
            if (card.AfflId != 0
                && card.AfflId < rules.Afflictions.Length
                && rules.Afflictions[card.AfflId].AfflictionId == targetAffliction)
            {
                card.AfflId = 0;
                card.AfflAmt = 0;
            }
        }
    }

    private static void ApplyInfiniteShrinkDebuff(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot)
    {
        ushort shrinkDefId = rules.GetPowerDefId(HotPowerId.ShrinkPower);
        if (shrinkDefId == 0 || (s.Player.Flags & HotCreatureFlags.Alive) == 0)
            return;

        // Check Artifact negation first if player has Artifact.
        ushort artifactDefId = rules.ArtifactPowerDefId != 0
            ? rules.ArtifactPowerDefId
            : rules.GetPowerDefId(HotPowerId.ArtifactPower);
        if (artifactDefId != 0)
        {
            int artIdx = HotCombatEngine.FindPowerIndex(in s, ownerSlot: 0, artifactDefId);
            if (artIdx >= 0 && s.Powers[artIdx].Amount > 0)
            {
                s.Powers[artIdx].Amount--;
                if (s.Powers[artIdx].Amount == 0)
                {
                    HotCombatEngine.RemovePowerAt(ref s, artIdx);
                    s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);
                }
                return;
            }
        }

        int existingIdx = HotCombatEngine.FindPowerIndex(in s, ownerSlot: 0, shrinkDefId);
        if (existingIdx >= 0)
        {
            s.Powers[existingIdx].Amount = -1;
            s.Powers[existingIdx].ApplierSlot = dealerSlot;
            return;
        }

        HotCombatEngine.AddPowerInstance(ref s, rules, ownerSlot: 0, applierSlot: dealerSlot, powerDefId: shrinkDefId, amount: -1);
        s.ActiveHookMask |= (ulong)rules.Powers[shrinkDefId].HookMask;
    }

    private static void RecordPossessedStatDelta(
        ref CombatHotState s,
        HotRules rules,
        byte possessorSlot,
        HotPowerId possessPowerId,
        int negativeDelta)
    {
        ushort pDefId = rules.GetPowerDefId(possessPowerId);
        if (pDefId == 0)
            return;
        int idx = HotCombatEngine.FindPowerIndex(in s, possessorSlot, pDefId);
        if (idx >= 0 && s.Powers[idx].Amount > 0)
            s.Powers[idx].Aux0 += negativeDelta;
    }

    // -------------------------------------------------------------------------
    // Monster AI State Machine Transition, Random Branch & Conditional Branch
    // -------------------------------------------------------------------------

    private static void ResolveTransition(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        byte enemySlot,
        HotMonsterMachine machine,
        HotMonsterId monsterId,
        short startNodeId)
    {
        short nextNodeId = startNodeId;
        for (int guard = 0; guard < 32; guard++)
        {
            if (nextNodeId < 0 || nextNodeId >= machine.Nodes.Length)
                return;

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
                        rules,
                        machine,
                        monsterId,
                        node,
                        enemySlot,
                        ref s.Rng[(int)HotRngStream.MonsterAi]);
                    break;

                case HotMonsterNodeKind.ConditionalBranch:
                    nextNodeId = ResolveConditionalBranch(in s, rules, machine, monsterId, node, enemySlot);
                    break;

                default:
                    return;
            }
        }
    }

    private static short PickRandomBranch(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        HotMonsterMachine machine,
        HotMonsterId monsterId,
        HotMonsterNode branch,
        byte enemySlot,
        ref HotRngState rng)
    {
        int count = branch.BranchTargets.Length;
        if (count == 0)
            return -1;

        Span<float> weights = count <= HotLimits.MaxStackAllocElements
            ? stackalloc float[count]
            : new float[count];

        bool isRat = monsterId == HotMonsterId.TwoTailedRat;
        bool ratCanSummon = isRat && CanTwoTailedRatSummon(in s, rules, enemySlot);

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

            float baseWeight = branch.BranchWeights[b];
            if (isRat && targetNode >= 0 && targetNode < machine.Nodes.Length)
            {
                string targetMoveId = machine.Nodes[targetNode].MoveId;
                if (string.Equals(targetMoveId, "CALL_FOR_BACKUP_MOVE", StringComparison.Ordinal))
                    baseWeight = ratCanSummon ? 0.75f : 0f;
                else if (string.Equals(targetMoveId, "SCREECH_MOVE", StringComparison.Ordinal)
                    || string.Equals(targetMoveId, "SCRATCH_MOVE", StringComparison.Ordinal)
                    || string.Equals(targetMoveId, "DISEASE_BITE_MOVE", StringComparison.Ordinal))
                {
                    baseWeight = ratCanSummon ? (1f / 12f) : 1f;
                }
            }

            float w = repeatWeight * baseWeight;
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

    private static bool CanTwoTailedRatSummon(in CombatHotState s, HotRules rules, byte enemySlot)
    {
        if (s.Enemies[enemySlot].AiRegs[0] > 0
            || s.Enemies[enemySlot].AiRegs[1] >= 3
            || !HasFreeEncounterSlot(in s, rules))
        {
            return false;
        }

        for (int r = 0; r < s.RosterCount; r++)
        {
            byte other = s.ActiveEnemyRoster[r];
            if (other == enemySlot)
                continue;
            ushort mDefId = s.Enemies[other].MonsterDefId;
            if (mDefId == 0 || mDefId >= rules.Monsters.Length)
                continue;
            if (ResolveMonsterId(rules.Monsters[mDefId]) != HotMonsterId.TwoTailedRat)
                continue;
            if (string.Equals(GetEnemyMoveId(in s, rules, other), "CALL_FOR_BACKUP_MOVE", StringComparison.Ordinal))
                return false;
        }

        return true;
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
        HotMonsterId monsterId,
        HotMonsterNode branch,
        byte enemySlot)
    {
        ref readonly HotEnemy enemy = ref s.Enemies[enemySlot];
        byte ownerSlot = (byte)(3 + enemySlot);

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

            case HotMonsterConditionalKind.FabricatorBranch:
            {
                int aliveTeammates = CountLivingEnemiesInRoster(in s);
                short id = machine.FindNode(aliveTeammates < 4 ? "RAND" : "DISINTEGRATE_MOVE");
                return id >= 0 ? id : branch.NextNode;
            }

            case HotMonsterConditionalKind.TestSubjectRevive:
            {
                short id = machine.FindNode(enemy.AiRegs[0] < 2 ? "MULTI_CLAW_MOVE" : "PHASE3_LACERATE_MOVE");
                return id >= 0 ? id : branch.NextNode;
            }

            case HotMonsterConditionalKind.OvicopterSummon:
            {
                int aliveTeammates = CountLivingEnemiesInRoster(in s);
                short id = machine.FindNode(aliveTeammates <= 3 ? "LAY_EGGS_MOVE" : "NUTRITIONAL_PASTE_MOVE");
                return id >= 0 ? id : branch.NextNode;
            }

            case HotMonsterConditionalKind.LagavulinSleep:
            {
                bool asleep = HotCombatEngine.GetPowerAmountById(in s, rules, ownerSlot, HotPowerId.AsleepPower) > 0;
                short id = machine.FindNode(asleep ? "SLEEP_MOVE" : "SLASH_MOVE");
                return id >= 0 ? id : branch.NextNode;
            }

            case HotMonsterConditionalKind.SlumberingBeetleSnore:
            {
                bool slumbering = HotCombatEngine.GetPowerAmountById(in s, rules, ownerSlot, HotPowerId.SlumberPower) > 0;
                short id = machine.FindNode(slumbering ? "SNORE_MOVE" : "ROLL_OUT_MOVE");
                return id >= 0 ? id : branch.NextNode;
            }

            case HotMonsterConditionalKind.QueenAmalgam:
            {
                bool amalgamDied = (enemy.Flags & HotEnemyFlags.AmalgamDied) != 0;
                short id = machine.FindNode(amalgamDied ? "OFF_WITH_YOUR_HEAD_MOVE" : "BURN_BRIGHT_FOR_ME_MOVE");
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

            default:
            {
                if (monsterId == HotMonsterId.KnowledgeDemon
                    && string.Equals(branch.MoveId, "CurseOfKnowledgeBranch", StringComparison.Ordinal))
                {
                    short id = machine.FindNode(enemy.AiRegs[0] < 3 ? "CURSE_OF_KNOWLEDGE_MOVE" : "SLAP_MOVE");
                    return id >= 0 ? id : branch.NextNode;
                }
                if (monsterId == HotMonsterId.Wriggler
                    && string.Equals(branch.MoveId, "INIT_MOVE", StringComparison.Ordinal)
                    && enemy.AiRegs[0] > 0)
                {
                    short id = machine.FindNode((enemy.AiRegs[0] & 1) == 1 ? "NASTY_BITE_MOVE" : "WRIGGLE_MOVE");
                    return id >= 0 ? id : branch.NextNode;
                }
                return branch.NextNode;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Monster Spawning (MonsterSpawnSupport Parity)
    // -------------------------------------------------------------------------

    private static void SpawnFabricatorBot(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte fabricatorSlot,
        byte sourceSlot,
        bool defensive)
    {
        int previous = s.Enemies[fabricatorSlot].AiRegs[0];
        Span<int> candidates = stackalloc int[2];
        int candCount = 0;
        int opt0 = defensive ? 1 : 3;
        int opt1 = defensive ? 2 : 4;
        if (opt0 != previous) candidates[candCount++] = opt0;
        if (opt1 != previous) candidates[candCount++] = opt1;
        if (candCount == 0)
            return;

        int selected = candidates[HotRng.NextInt(ref s.Rng[(int)HotRngStream.MonsterAi], candCount)];
        s.Enemies[fabricatorSlot].AiRegs[0] = selected;

        if (!HasFreeEncounterSlot(in s, rules))
            return;

        HotMonsterId botId = selected switch
        {
            1 => HotMonsterId.Guardbot,
            2 => HotMonsterId.Noisebot,
            3 => HotMonsterId.Zapbot,
            _ => HotMonsterId.Stabbot,
        };
        SpawnMonster(ref s, ref cold, rules, scratch, botId, sourceSlot: sourceSlot, minion: true);
    }

    private static void SpawnStockReplacementAxebot(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte deadSlot,
        int oldStockAmount)
    {
        int newStock = oldStockAmount - 1;
        int respawnCount = Math.Max(0, 2 - newStock);
        int hpBonus = respawnCount * 10;
        bool tough = IsTough(rules);
        int hp = (tough ? 80 : 70) + hpBonus;

        SpawnMonster(
            ref s,
            ref cold,
            rules,
            scratch,
            HotMonsterId.Axebot,
            sourceSlot: deadSlot,
            minion: false,
            customMinHp: hp,
            customMaxHp: hp,
            stockAmount: newStock,
            axebotRespawnCount: respawnCount);
    }

    private static void TriggerSurpriseSpawn(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte deadSlot)
    {
        // Match STS2 0.113.1 SurprisePower.cs:24-35 exact creation & Niche RNG order:
        // 1) CreateCreature(FatGremlin, "fat") -> rolls FatGremlin HP & claims FatGremlin CombatId FIRST
        // 2) CreatureCmd.Add<SneakyGremlin>("sneaky") -> rolls SneakyGremlin HP, claims next CombatId, adds to combat
        // 3) CreatureCmd.Add(fatGremlin) with HeistPower copied from dead GremlinMerc's ThieveryPower(s).
        ushort fatDefId = FindMonsterDefId(rules, HotMonsterId.FatGremlin);
        if (fatDefId == 0)
            throw new InvalidOperationException("HotMonsters.TriggerSurpriseSpawn: FatGremlin was not pre-registered in HotRules.Monsters.");
        int fatHp = RollSpawnHp(in s, ref s.Rng[(int)HotRngStream.Niche], rules, HotMonsterId.FatGremlin, fatDefId, 0, 0);
        uint fatCombatId = s.NextCreatureId++;

        SpawnMonster(ref s, ref cold, rules, scratch, HotMonsterId.SneakyGremlin, sourceSlot: deadSlot, minion: false);

        AddPreparedSpawn(
            ref s,
            ref cold,
            rules,
            scratch,
            HotMonsterId.FatGremlin,
            fatDefId,
            fatHp,
            sourceSlot: deadSlot,
            minion: false,
            stockAmount: -1,
            axebotRespawnCount: 0,
            wrigglerSlotIndex: 0,
            overrideCombatId: fatCombatId,
            applySurpriseHeistFromSource: true);
    }

    private static void ApplySurpriseHeistPowers(
        ref CombatHotState s,
        HotRules rules,
        byte deadSlot,
        byte fatOwnerSlot)
    {
        ushort thieveryDefId = rules.GetPowerDefId(HotPowerId.ThieveryPower);
        ushort heistDefId = rules.GetPowerDefId(HotPowerId.HeistPower);
        if (thieveryDefId == 0 || heistDefId == 0)
            return;

        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            if (s.Powers[i].OwnerSlot == deadSlot
                && s.Powers[i].PowerDefId == thieveryDefId
                && s.Powers[i].Amount > 0)
            {
                int stolenGold = s.Powers[i].Aux0;
                int addedIdx = HotCombatEngine.AddPowerInstance(
                    ref s,
                    rules,
                    ownerSlot: fatOwnerSlot,
                    applierSlot: deadSlot,
                    powerDefId: heistDefId,
                    amount: stolenGold);
                if (addedIdx >= 0)
                    s.Powers[addedIdx].TargetSlot = 0;
            }
        }
    }

    internal static int SpawnMonster(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotMonsterId monsterId,
        byte sourceSlot,
        bool minion,
        int customMinHp = 0,
        int customMaxHp = 0,
        int stockAmount = -1,
        int axebotRespawnCount = 0,
        int wrigglerSlotIndex = 0)
    {
        if (s.EnemyCount >= HotLimits.MaxEnemies || s.RosterCount >= HotLimits.MaxEnemies)
            return -1;

        ushort monsterDefId = FindMonsterDefId(rules, monsterId);
        if (monsterDefId == 0)
            throw new InvalidOperationException($"HotMonsters.SpawnMonster: monster '{monsterId}' was not pre-registered in HotRules.Monsters.");

        int baseHp = RollSpawnHp(
            in s,
            ref s.Rng[(int)HotRngStream.Niche],
            rules,
            monsterId,
            monsterDefId,
            customMinHp,
            customMaxHp);

        return AddPreparedSpawn(
            ref s,
            ref cold,
            rules,
            scratch,
            monsterId,
            monsterDefId,
            baseHp,
            sourceSlot,
            minion,
            stockAmount,
            axebotRespawnCount,
            wrigglerSlotIndex);
    }

    private static int AddPreparedSpawn(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotMonsterId monsterId,
        ushort monsterDefId,
        int baseHp,
        byte sourceSlot,
        bool minion,
        int stockAmount,
        int axebotRespawnCount,
        int wrigglerSlotIndex,
        uint overrideCombatId = 0,
        bool applySurpriseHeistFromSource = false)
    {
        if (s.EnemyCount >= HotLimits.MaxEnemies || s.RosterCount >= HotLimits.MaxEnemies)
            return -1;

        byte newSlot = s.EnemyCount++;
        byte newOwnerSlot = (byte)(3 + newSlot);

        ref HotEnemy enemy = ref s.Enemies[newSlot];
        enemy = default;
        enemy.Vital = new HotCreature
        {
            CombatId = overrideCombatId != 0 ? overrideCombatId : s.NextCreatureId++,
            CurrentHp = baseHp,
            MaxHp = baseHp,
            Block = 0,
            Flags = HotCreatureFlags.Alive | HotCreatureFlags.ActiveInRoster,
            SlotIdx = newOwnerSlot,
            Pad = 0,
        };
        enemy.MonsterDefId = monsterDefId;
        enemy.CurrentMoveNode = -1;
        enemy.DeathPhase = HotDeathPhase.None;
        enemy.Flags = HotEnemyFlags.NeedsInitialRoll;

        // Monster-specific initial private registers.
        if (monsterId == HotMonsterId.TwoTailedRat)
            enemy.AiRegs[0] = 2; // _turnsUntilSummonable starts at 2
        else if (monsterId == HotMonsterId.Axebot)
            enemy.AiRegs[0] = Math.Max(0, axebotRespawnCount) + 1;
        else if (monsterId == HotMonsterId.Wriggler && wrigglerSlotIndex > 0)
            enemy.AiRegs[0] = wrigglerSlotIndex;

        // Assign exact STS2 0.113.1 encounter slot into AiRegs[2] (1-based) and keep ActiveEnemyRoster
        // sorted in EncounterSlots order to match live CombatState.SortEnemiesBySlotName().
        if (rules.EncounterSlots.Length > 0)
        {
            int chosenEncounterSlotIdx = ChooseSpawnEncounterSlot(in s, rules, monsterId, sourceSlot, wrigglerSlotIndex);
            if (chosenEncounterSlotIdx >= 0)
                s.Enemies[newSlot].AiRegs[2] = chosenEncounterSlotIdx + 1;
        }

        s.ActiveEnemyRoster[s.RosterCount++] = newSlot;
        if (rules.EncounterSlots.Length > 0 && s.RosterCount > 1)
            SortActiveEnemyRosterByEncounterSlots(ref s, rules);

        // Native entrance powers (MonsterSpawnSupport.ApplyNativeEntrancePowers).
        switch (monsterId)
        {
            case HotMonsterId.GasBomb:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, newOwnerSlot, newOwnerSlot, HotPowerId.MinionPower, 1);
                break;
            case HotMonsterId.EyeWithTeeth:
            case HotMonsterId.Parafright:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, newOwnerSlot, newOwnerSlot, HotPowerId.IllusionPower, 1);
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, newOwnerSlot, 0xFF, HotPowerId.MinionPower, 1);
                break;
            case HotMonsterId.ToughEgg:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, newOwnerSlot, newOwnerSlot, HotPowerId.HatchPower,
                    s.CurrentSide == HotCombatSide.Enemy ? 2 : 1);
                break;
            case HotMonsterId.Zapbot:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, newOwnerSlot, newOwnerSlot, HotPowerId.HighVoltagePower, 2);
                break;
            case HotMonsterId.Axebot when stockAmount > 0:
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, newOwnerSlot, 0xFF, HotPowerId.StockPower, stockAmount);
                break;
        }

        // Prepare initial move state machine.
        HotMonsterMachine? machine = rules.GetMonsterMachine(monsterDefId);
        if (machine != null)
        {
            if (monsterId == HotMonsterId.Wriggler && wrigglerSlotIndex > 0)
            {
                short spawnedNode = machine.FindNode("SPAWNED_MOVE");
                if (spawnedNode >= 0)
                {
                    if (s.CurrentSide == HotCombatSide.Player)
                    {
                        s.Enemies[newSlot].CurrentMoveNode = spawnedNode;
                        s.Enemies[newSlot].Flags &= ~HotEnemyFlags.NeedsInitialRoll;
                        AppendMonsterMoveLog(ref s, ref cold, newSlot, spawnedNode);
                    }
                    else
                    {
                        s.Enemies[newSlot].AiRegs[3] = spawnedNode + 1;
                    }
                }
                else if (s.CurrentSide == HotCombatSide.Player)
                {
                    RollInitialMove(ref s, ref cold, rules, newSlot, machine);
                }
            }
            else if (monsterId == HotMonsterId.Axebot && stockAmount >= 0)
            {
                short bootUpNode = machine.FindNode("BOOT_UP_MOVE");
                if (bootUpNode >= 0)
                {
                    if (s.CurrentSide == HotCombatSide.Player)
                    {
                        s.Enemies[newSlot].CurrentMoveNode = bootUpNode;
                        s.Enemies[newSlot].Flags &= ~HotEnemyFlags.NeedsInitialRoll;
                        AppendMonsterMoveLog(ref s, ref cold, newSlot, bootUpNode);
                    }
                    else
                    {
                        s.Enemies[newSlot].AiRegs[3] = bootUpNode + 1;
                    }
                }
                else if (s.CurrentSide == HotCombatSide.Player)
                {
                    RollInitialMove(ref s, ref cold, rules, newSlot, machine);
                }
            }
            else if (s.CurrentSide == HotCombatSide.Player)
            {
                RollInitialMove(ref s, ref cold, rules, newSlot, machine);
            }
        }

        // 0.113.1 SurprisePower.cs:26-35 applies HeistPower to FatGremlin BEFORE CreatureCmd.Add(fatGremlin)
        // triggers Hook.AfterCreatureAddedToCombat (Dominions / PhilosophersStone), so HeistPower precedes
        // Dominion-applied powers (e.g. ExplosivePower, RitualPower) in FatGremlin's power list.
        if (applySurpriseHeistFromSource)
            ApplySurpriseHeistPowers(ref s, rules, sourceSlot, newOwnerSlot);

        // AfterCreatureAddedToCombat hooks (Dominions & PhilosophersStone).
        ApplyAfterCreatureAddedHooks(ref s, ref cold, rules, scratch, newSlot, newOwnerSlot);

        // Summoning move applies MinionPower AFTER CreatureCmd.Add and entrance hooks finish.
        if (minion && HotCombatEngine.GetPowerAmountById(in s, rules, newOwnerSlot, HotPowerId.MinionPower) <= 0)
            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, newOwnerSlot, sourceSlot, HotPowerId.MinionPower, 1);

        s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);
        return newSlot;
    }

    private static void ApplyAfterCreatureAddedHooks(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte newEnemySlot,
        byte newOwnerSlot)
    {
        if (rules.Dominion != HotDominionId.None)
            HotFoolTarot.ApplyDominionOnEnemyAdded(ref s, ref cold, rules, scratch, newEnemySlot);

        if (rules.HasRelic(HotRelicId.PhilosophersStone))
            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, newOwnerSlot, 0, HotPowerId.StrengthPower, 1);

        if (rules.IsFurCoatActiveInCurrentRoom && rules.HasRelic(HotRelicId.FurCoat) && s.Enemies[newEnemySlot].Vital.CurrentHp > 1)
            s.Enemies[newEnemySlot].Vital.CurrentHp = 1;
    }

    private static int GetEnemyEncounterSlotOrder(in CombatHotState s, HotRules rules, byte enemySlot)
    {
        string[] encSlots = rules.EncounterSlots;
        if (encSlots.Length == 0)
            return -1;
        if (enemySlot < rules.EnemySlotNames.Length)
        {
            string name = rules.EnemySlotNames[enemySlot];
            for (int i = 0; i < encSlots.Length; i++)
            {
                if (string.Equals(encSlots[i], name, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }
        int encIdx1 = s.Enemies[enemySlot].AiRegs[2];
        return encIdx1 > 0 && encIdx1 <= encSlots.Length ? encIdx1 - 1 : -1;
    }

    private static int ChooseSpawnEncounterSlot(
        in CombatHotState s,
        HotRules rules,
        HotMonsterId monsterId,
        byte sourceSlot,
        int wrigglerSlotIndex)
    {
        string[] encSlots = rules.EncounterSlots;
        if (encSlots.Length == 0)
            return -1;

        static int FindNamedSlot(string[] slots, string name)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (string.Equals(slots[i], name, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        switch (monsterId)
        {
            case HotMonsterId.Wriggler:
                if (wrigglerSlotIndex > 0)
                {
                    int wIdx = FindNamedSlot(encSlots, $"wriggler{wrigglerSlotIndex}");
                    if (wIdx >= 0)
                        return wIdx;
                }
                return FindFirstUnoccupiedEncounterSlot(in s, rules);

            case HotMonsterId.EyeWithTeeth:
            case HotMonsterId.Parafright:
            {
                int illIdx = FindNamedSlot(encSlots, "illusion");
                return illIdx >= 0 ? illIdx : FindFirstUnoccupiedEncounterSlot(in s, rules);
            }

            case HotMonsterId.SneakyGremlin:
            {
                int sneakyIdx = FindNamedSlot(encSlots, "sneaky");
                return sneakyIdx >= 0 ? sneakyIdx : FindFirstUnoccupiedEncounterSlot(in s, rules);
            }

            case HotMonsterId.FatGremlin:
            {
                int fatIdx = FindNamedSlot(encSlots, "fat");
                return fatIdx >= 0 ? fatIdx : FindFirstUnoccupiedEncounterSlot(in s, rules);
            }

            case HotMonsterId.Axebot:
                if (sourceSlot >= 3)
                {
                    int srcIdx = GetEnemyEncounterSlotOrder(in s, rules, (byte)(sourceSlot - 3));
                    if (srcIdx >= 0)
                        return srcIdx;
                }
                return FindFirstUnoccupiedEncounterSlot(in s, rules);

            case HotMonsterId.ToughEgg:
            case HotMonsterId.TwoTailedRat:
                return FindLastUnoccupiedEncounterSlot(in s, rules);

            default:
                return FindFirstUnoccupiedEncounterSlot(in s, rules);
        }
    }

    private static int FindFirstUnoccupiedEncounterSlot(in CombatHotState s, HotRules rules)
    {
        string[] encSlots = rules.EncounterSlots;
        for (int idx = 0; idx < encSlots.Length; idx++)
        {
            bool occupied = false;
            for (int r = 0; r < s.RosterCount; r++)
            {
                byte activeSlot = s.ActiveEnemyRoster[r];
                if (GetEnemyEncounterSlotOrder(in s, rules, activeSlot) == idx)
                {
                    occupied = true;
                    break;
                }
            }
            if (!occupied)
                return idx;
        }
        return -1;
    }

    private static int FindLastUnoccupiedEncounterSlot(in CombatHotState s, HotRules rules)
    {
        string[] encSlots = rules.EncounterSlots;
        for (int idx = encSlots.Length - 1; idx >= 0; idx--)
        {
            bool occupied = false;
            for (int r = 0; r < s.RosterCount; r++)
            {
                byte activeSlot = s.ActiveEnemyRoster[r];
                if (GetEnemyEncounterSlotOrder(in s, rules, activeSlot) == idx)
                {
                    occupied = true;
                    break;
                }
            }
            if (!occupied)
                return idx;
        }
        return -1;
    }

    private static void SortActiveEnemyRosterByEncounterSlots(ref CombatHotState s, HotRules rules)
    {
        int count = s.RosterCount;
        for (int i = 1; i < count; i++)
        {
            byte key = s.ActiveEnemyRoster[i];
            int keyOrder = GetEnemyEncounterSlotOrder(in s, rules, key);
            int j = i - 1;
            while (j >= 0 && GetEnemyEncounterSlotOrder(in s, rules, s.ActiveEnemyRoster[j]) > keyOrder)
            {
                s.ActiveEnemyRoster[j + 1] = s.ActiveEnemyRoster[j];
                j--;
            }
            s.ActiveEnemyRoster[j + 1] = key;
        }
    }

    private static int RollSpawnHp(
        in CombatHotState s,
        ref HotRngState nicheRng,
        HotRules rules,
        HotMonsterId monsterId,
        ushort monsterDefId,
        int customMinHp,
        int customMaxHp)
    {
        HotMonsterDef def = rules.Monsters[monsterDefId];
        int minHp = customMinHp > 0 ? customMinHp : (def.MinInitialHp > 0 ? def.MinInitialHp : FallbackMinHp(monsterId, rules));
        int maxHp = customMaxHp > 0 ? customMaxHp : (def.MaxInitialHp > 0 ? def.MaxInitialHp : FallbackMaxHp(monsterId, rules));
        if (maxHp < minHp)
            maxHp = minHp;

        int range = maxHp - minHp + 1;
        if (range <= 1)
        {
            // Single value: still check if unique vs existing max HP; NextInt(min, min+1) consumes 1 Niche draw if deduplicated!
            // Vanilla builds available = {minHp} \ existingMaxHps; whether empty or size 1, NextInt/NextItem consumes 1 RNG draw!
            _ = HotRng.NextInt(ref nicheRng, 1);
            return minHp;
        }

        if (range <= 64)
        {
            Span<int> available = stackalloc int[64];
            int availCount = 0;
            for (int hp = minHp; hp <= maxHp; hp++)
            {
                bool taken = false;
                for (int r = 0; r < s.RosterCount; r++)
                {
                    if (s.Enemies[s.ActiveEnemyRoster[r]].Vital.MaxHp == hp)
                    {
                        taken = true;
                        break;
                    }
                }
                if (!taken)
                    available[availCount++] = hp;
            }

            if (availCount == 0)
                return HotRng.NextInt(ref nicheRng, minHp, maxHp + 1);
            return available[HotRng.NextInt(ref nicheRng, availCount)];
        }

        return HotRng.NextInt(ref nicheRng, minHp, maxHp + 1);
    }

    private static int FallbackMinHp(HotMonsterId monsterId, HotRules rules)
    {
        bool tough = IsTough(rules);
        return monsterId switch
        {
            HotMonsterId.TwoTailedRat => tough ? 18 : 17,
            HotMonsterId.GasBomb => tough ? 8 : 7,
            HotMonsterId.EyeWithTeeth => 6,
            HotMonsterId.Parafright => 21,
            HotMonsterId.Guardbot => tough ? 17 : 16,
            HotMonsterId.Noisebot => tough ? 19 : 18,
            HotMonsterId.Zapbot => tough ? 19 : 18,
            HotMonsterId.Stabbot => tough ? 19 : 18,
            HotMonsterId.FatGremlin => tough ? 14 : 13,
            HotMonsterId.SneakyGremlin => tough ? 11 : 10,
            HotMonsterId.Wriggler => tough ? 18 : 17,
            HotMonsterId.ToughEgg => tough ? 15 : 14,
            HotMonsterId.Axebot => tough ? 80 : 70,
            _ => 20,
        };
    }

    private static int FallbackMaxHp(HotMonsterId monsterId, HotRules rules)
    {
        bool tough = IsTough(rules);
        return monsterId switch
        {
            HotMonsterId.TwoTailedRat => tough ? 22 : 21,
            HotMonsterId.GasBomb => tough ? 8 : 7,
            HotMonsterId.EyeWithTeeth => 6,
            HotMonsterId.Parafright => 21,
            HotMonsterId.Guardbot => tough ? 21 : 20,
            HotMonsterId.Noisebot => tough ? 24 : 23,
            HotMonsterId.Zapbot => tough ? 24 : 23,
            HotMonsterId.Stabbot => tough ? 24 : 23,
            HotMonsterId.FatGremlin => tough ? 18 : 17,
            HotMonsterId.SneakyGremlin => tough ? 15 : 14,
            HotMonsterId.Wriggler => tough ? 22 : 21,
            HotMonsterId.ToughEgg => tough ? 19 : 18,
            HotMonsterId.Axebot => tough ? 80 : 70,
            _ => 20,
        };
    }

    // -------------------------------------------------------------------------
    // Death Lifecycle Helpers
    // -------------------------------------------------------------------------

    private static void TriggerCrabRageOnTeammates(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte deadEnemySlot)
    {
        ushort crabDefId = rules.GetPowerDefId(HotPowerId.CrabRagePower);
        if (crabDefId == 0)
            return;

        for (int r = 0; r < s.RosterCount; r++)
        {
            byte other = s.ActiveEnemyRoster[r];
            if (other == deadEnemySlot || (s.Enemies[other].Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;

            byte otherOwnerSlot = (byte)(3 + other);
            int idx = HotCombatEngine.FindPowerIndex(in s, otherOwnerSlot, crabDefId);
            if (idx < 0 || s.Powers[idx].Amount <= 0)
                continue;

            HotCombatEngine.RemovePowerAt(ref s, idx);
            s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);

            HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, otherOwnerSlot, otherOwnerSlot, HotPowerId.StrengthPower, 6);
            HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, otherOwnerSlot, 99, isCardBlock: false);
        }
    }

    private static void TriggerRavenousOnTeammates(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte deadEnemySlot,
        bool applyStrength)
    {
        ushort ravenousDefId = rules.GetPowerDefId(HotPowerId.RavenousPower);
        if (ravenousDefId == 0)
            return;

        for (int r = 0; r < s.RosterCount; r++)
        {
            byte other = s.ActiveEnemyRoster[r];
            if (other == deadEnemySlot || (s.Enemies[other].Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;

            byte otherOwnerSlot = (byte)(3 + other);
            int ravAmt = HotCombatEngine.GetPowerAmount(in s, otherOwnerSlot, ravenousDefId);
            if (ravAmt <= 0)
                continue;

            ForceStunnedMove(ref s, rules, other, null);
            if (applyStrength)
            {
                s.Enemies[other].Flags |= HotEnemyFlags.SkipNextMove | HotEnemyFlags.Stunned;
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, otherOwnerSlot, otherOwnerSlot, HotPowerId.StrengthPower, ravAmt);
            }
        }
    }

    private static void NotifyQueenOfAmalgamDeath(ref CombatHotState s, HotRules rules)
    {
        for (int r = 0; r < s.RosterCount; r++)
        {
            byte eSlot = s.ActiveEnemyRoster[r];
            if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;
            ushort mDefId = s.Enemies[eSlot].MonsterDefId;
            if (mDefId == 0 || mDefId >= rules.Monsters.Length)
                continue;
            if (ResolveMonsterId(rules.Monsters[mDefId]) != HotMonsterId.Queen)
                continue;

            s.Enemies[eSlot].Flags |= HotEnemyFlags.AmalgamDied;
            if (string.Equals(GetEnemyMoveId(in s, rules, eSlot), "BURN_BRIGHT_FOR_ME_MOVE", StringComparison.Ordinal))
                ForceMonsterMove(ref s, rules, eSlot, "ENRAGE_MOVE");
        }
    }

    private static void UpdateSurroundedFacingAfterEnemyDeath(ref CombatHotState s, HotRules rules, byte deadEnemySlot)
    {
        ushort surroundedDefId = rules.GetPowerDefId(HotPowerId.SurroundedPower);
        if (surroundedDefId == 0)
            return;

        int surIdx = HotCombatEngine.FindPowerIndex(in s, ownerSlot: 0, surroundedDefId);
        if (surIdx < 0 || s.Powers[surIdx].Amount <= 0)
            return;

        ushort leftDefId = rules.GetPowerDefId(HotPowerId.BackAttackLeftPower);
        ushort rightDefId = rules.GetPowerDefId(HotPowerId.BackAttackRightPower);
        if (leftDefId == 0 && rightDefId == 0)
            return;

        int remainingCount = 0;
        bool allLeft = true;
        bool allRight = true;
        byte firstRemainingOwner = 0;

        for (int r = 0; r < s.RosterCount; r++)
        {
            byte other = s.ActiveEnemyRoster[r];
            if (other == deadEnemySlot || (s.Enemies[other].Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;

            byte ownerSlot = (byte)(3 + other);
            if (remainingCount == 0)
                firstRemainingOwner = ownerSlot;
            remainingCount++;

            if (leftDefId == 0 || HotCombatEngine.GetPowerAmount(in s, ownerSlot, leftDefId) <= 0)
                allLeft = false;
            if (rightDefId == 0 || HotCombatEngine.GetPowerAmount(in s, ownerSlot, rightDefId) <= 0)
                allRight = false;
        }

        if (remainingCount > 0 && (allLeft || allRight))
        {
            if (leftDefId != 0 && HotCombatEngine.GetPowerAmount(in s, firstRemainingOwner, leftDefId) > 0)
                s.Powers[surIdx].Aux0 = 1; // SurroundedPower.Direction.Left
            else if (rightDefId != 0 && HotCombatEngine.GetPowerAmount(in s, firstRemainingOwner, rightDefId) > 0)
                s.Powers[surIdx].Aux0 = 2; // SurroundedPower.Direction.Right
        }
    }

    private static void BeginIllusionRevive(
        ref CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        byte slot)
    {
        if (s.Enemies[slot].DeathPhase == HotDeathPhase.Reviving)
            return;

        ushort mDefId = s.Enemies[slot].MonsterDefId;
        HotMonsterMachine? machine = rules.GetMonsterMachine(mDefId);

        short followUp = -1;
        if (machine != null)
        {
            int logLen = GetMonsterLogLength(in s.Enemies[slot], slot, cold, out ushort[]? overflowLog);
            for (int i = logLen - 1; i >= 0; i--)
            {
                short loggedId = GetMonsterLogNodeAt(in s.Enemies[slot], overflowLog, i);
                if (loggedId >= 0 && loggedId < machine.Nodes.Length
                    && !string.Equals(machine.Nodes[loggedId].MoveId, "REVIVE_MOVE", StringComparison.Ordinal))
                {
                    followUp = loggedId;
                    break;
                }
            }
            if (followUp < 0)
                followUp = s.Enemies[slot].CurrentMoveNode;
        }

        s.Enemies[slot].DeathPhase = HotDeathPhase.Reviving;
        s.Enemies[slot].Flags &= ~HotEnemyFlags.NeedsInitialRoll;
        s.Enemies[slot].AiRegs[3] = followUp >= 0 ? followUp + 1 : 0;

        short reviveNode = machine?.FindNode("REVIVE_MOVE") ?? -1;
        s.Enemies[slot].CurrentMoveNode = reviveNode >= 0 ? reviveNode : SyntheticReviveNodeSentinel;
    }

    private static bool AreAllOtherReattachSegmentsDead(in CombatHotState s, HotRules rules, byte selfSlot)
    {
        ushort reattachDefId = rules.GetPowerDefId(HotPowerId.ReattachPower);
        if (reattachDefId == 0)
            return true;

        for (int r = 0; r < s.RosterCount; r++)
        {
            byte other = s.ActiveEnemyRoster[r];
            if (other == selfSlot)
                continue;
            if (HotCombatEngine.GetPowerAmount(in s, (byte)(3 + other), reattachDefId) > 0
                && (s.Enemies[other].Vital.Flags & HotCreatureFlags.Alive) != 0
                && s.Enemies[other].Vital.CurrentHp > 0)
            {
                return false;
            }
        }
        return true;
    }

    private static void MarkAllReattachSegmentsPermanentlyDead(ref CombatHotState s, HotRules rules)
    {
        ushort reattachDefId = rules.GetPowerDefId(HotPowerId.ReattachPower);
        if (reattachDefId == 0)
            return;

        for (int r = 0; r < s.RosterCount; r++)
        {
            byte eSlot = s.ActiveEnemyRoster[r];
            if (HotCombatEngine.GetPowerAmount(in s, (byte)(3 + eSlot), reattachDefId) > 0)
                s.Enemies[eSlot].DeathPhase = HotDeathPhase.PermanentlyDead;
        }
    }

    private static void RefundPossessedStats(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte deadSlot)
    {
        ushort posStrDef = rules.GetPowerDefId(HotPowerId.PossessStrengthPower);
        if (posStrDef != 0)
        {
            int idx = HotCombatEngine.FindPowerIndex(in s, deadSlot, posStrDef);
            if (idx >= 0 && s.Powers[idx].Aux0 < 0)
            {
                int refund = -s.Powers[idx].Aux0;
                s.Powers[idx].Aux0 = 0;
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0xFF, HotPowerId.StrengthPower, refund);
            }
        }

        ushort posSpdDef = rules.GetPowerDefId(HotPowerId.PossessSpeedPower);
        if (posSpdDef != 0)
        {
            int idx = HotCombatEngine.FindPowerIndex(in s, deadSlot, posSpdDef);
            if (idx >= 0 && s.Powers[idx].Aux0 < 0)
            {
                int refund = -s.Powers[idx].Aux0;
                s.Powers[idx].Aux0 = 0;
                HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, 0xFF, HotPowerId.DexterityPower, refund);
            }
        }
    }

    private static void RecoverStolenResources(ref CombatHotState s, HotRules rules, byte deadSlot)
    {
        ushort swipeDefId = rules.GetPowerDefId(HotPowerId.SwipePower);
        ushort heistDefId = rules.GetPowerDefId(HotPowerId.HeistPower);
        if (swipeDefId == 0 && heistDefId == 0)
            return;

        int count = s.PowerCount;
        for (int i = 0; i < count; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot != deadSlot)
                continue;
            if (p.PowerDefId == swipeDefId && (p.AuxCardUid != 0 || p.Amount > 0))
                s.OutstandingStolenCards = Math.Max(0, s.OutstandingStolenCards - 1);
            else if (p.PowerDefId == heistDefId && p.Amount > 0)
                s.OutstandingStolenGold = Math.Max(0, s.OutstandingStolenGold - p.Amount);
        }
    }

    private static void CleanupApplierLinkedDebuffs(ref CombatHotState s, HotRules rules, byte deadSlot)
    {
        ushort constrictDef = rules.GetPowerDefId(HotPowerId.ConstrictPower);
        ushort hexDef = rules.GetPowerDefId(HotPowerId.HexPower);
        ushort shrinkDef = rules.GetPowerDefId(HotPowerId.ShrinkPower);
        ushort magicBombDef = rules.GetPowerDefId(HotPowerId.MagicBombPower);

        bool removedHex = false;
        int i = 0;
        while (i < s.PowerCount)
        {
            ref readonly HotPower p = ref s.Powers[i];
            bool shouldRemove = false;

            if (p.ApplierSlot == deadSlot)
            {
                if (p.OwnerSlot == 0 && (p.PowerDefId == constrictDef || p.PowerDefId == shrinkDef))
                {
                    shouldRemove = true;
                }
                else if (p.OwnerSlot == 0 && p.PowerDefId == hexDef)
                {
                    shouldRemove = true;
                    removedHex = true;
                }
                else if (p.PowerDefId == magicBombDef)
                {
                    shouldRemove = true;
                }
            }

            if (shouldRemove)
                HotCombatEngine.RemovePowerAt(ref s, i);
            else
                i++;
        }

        if (removedHex && HotCombatEngine.GetPowerAmountById(in s, rules, 0, HotPowerId.HexPower) <= 0)
            ClearCardAffliction(ref s, rules, HotAfflictionId.Hexed);
    }

    private static bool ShouldEnemyBeRemovedFromCombatAfterDeath(in CombatHotState s, HotRules rules, byte ownerSlot)
    {
        int count = s.PowerCount;
        for (int i = 0; i < count; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot != ownerSlot || p.Amount <= 0 || p.PowerDefId >= rules.Powers.Length)
                continue;

            HotPowerId pid = rules.Powers[p.PowerDefId].PowerId;
            if (pid is HotPowerId.AdaptablePower
                or HotPowerId.IllusionPower
                or HotPowerId.ReattachPower
                or HotPowerId.SteamEruptionPower
                or HotPowerId.PainfulStabsPower
                or HotPowerId.DieForYouPower)
            {
                return false;
            }
        }
        return true;
    }

    internal static void CleanupPowersAfterEnemyDeath(
        ref CombatHotState s,
        HotRules rules,
        byte enemySlot)
    {
        if (enemySlot >= s.EnemyCount)
            return;
        byte ownerSlot = (byte)(3 + enemySlot);
        bool removedFromRoster = (s.Enemies[enemySlot].Vital.Flags & HotCreatureFlags.ActiveInRoster) == 0;
        RemovePowersAfterDeath(ref s, rules, ownerSlot, removeAllBecauseDeparted: removedFromRoster);
    }

    internal static void RemovePowersAfterDeath(
        ref CombatHotState s,
        HotRules rules,
        byte ownerSlot,
        bool removeAllBecauseDeparted)
    {
        bool hasIllusion = !removeAllBecauseDeparted
            && HotCombatEngine.GetPowerAmountById(in s, rules, ownerSlot, HotPowerId.IllusionPower) > 0;

        int i = 0;
        while (i < s.PowerCount)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.OwnerSlot != ownerSlot)
            {
                i++;
                continue;
            }

            bool keep = false;
            if (!removeAllBecauseDeparted && p.PowerDefId < rules.Powers.Length)
            {
                HotPowerDef def = rules.Powers[p.PowerDefId];
                if (hasIllusion)
                    keep = def.Type != PowerType.Debuff || IsTemporaryPower(def.PowerId);
                else
                    keep = !ShouldPowerBeRemovedAfterOwnerDeath(def.PowerId);
            }

            if (keep)
                i++;
            else
                HotCombatEngine.RemovePowerAt(ref s, i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldPowerBeRemovedAfterOwnerDeath(HotPowerId powerId)
        => powerId is not (
            HotPowerId.AdaptablePower
            or HotPowerId.DieForYouPower
            or HotPowerId.IllusionPower
            or HotPowerId.MinionPower
            or HotPowerId.PainfulStabsPower
            or HotPowerId.ReattachPower
            or HotPowerId.SteamEruptionPower);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsTemporaryPower(HotPowerId powerId)
        => powerId is
            HotPowerId.FlexPotionPower
            or HotPowerId.SetupStrikePower
            or HotPowerId.FeedingFrenzyPower
            or HotPowerId.ReptileTrinketPower
            or HotPowerId.CoordinatePower
            or HotPowerId.ShacklingPotionPower
            or HotPowerId.PiercingWailPower
            or HotPowerId.DarkShacklesPower
            or HotPowerId.EnfeeblingTouchPower
            or HotPowerId.CrushUnderPower
            or HotPowerId.DyingStarPower
            or HotPowerId.ManglePower
            or HotPowerId.MonarchsGazeStrengthDownPower
            or HotPowerId.SpeedPotionPower
            or HotPowerId.AnticipatePower
            or HotPowerId.HelicalDartPower
            or HotPowerId.FocusedStrikePower
            or HotPowerId.HotfixPower
            or HotPowerId.SynchronizePower
            or HotPowerId.HyperbeamFocusDownPower
            or HotPowerId.FadePower
            or HotPowerId.ChoBarbsPower;

    internal static void CreatureEscaped(ref CombatHotState s, HotRules rules, byte enemySlot)
    {
        byte ownerSlot = (byte)(3 + enemySlot);
        ushort mDefId = s.Enemies[enemySlot].MonsterDefId;
        if (mDefId > 0 && mDefId < rules.Monsters.Length
            && ResolveMonsterId(rules.Monsters[mDefId]) == HotMonsterId.FatGremlin
            && !rules.RulesSnapshot.IsBlitz
            && string.Equals(rules.EncounterEntry, "GREMLIN_MERC_NORMAL", StringComparison.Ordinal))
        {
            // 0.113.1 GremlinMercNormal.cs:58-68 & HotImportSupport.ForfeitsEncounterGold:
            // FatGremlin escaping in non-Blitz GremlinMercNormal forfeits encounter gold.
            s.CombatFlags |= HotCombatFlags.EncounterGoldForfeited;
        }
        RemovePowersAfterDeath(ref s, rules, ownerSlot, removeAllBecauseDeparted: true);
        s.Enemies[enemySlot].Vital.Flags &= ~(HotCreatureFlags.Alive | HotCreatureFlags.ActiveInRoster);
        s.Enemies[enemySlot].Vital.Flags |= HotCreatureFlags.Escaped;
        RemoveFromActiveEnemyRoster(ref s, enemySlot);
        s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);
    }

    // -------------------------------------------------------------------------
    // Move Forcing, State Log & General Helpers
    // -------------------------------------------------------------------------

    internal static void ForceMonsterMove(ref CombatHotState s, HotRules rules, byte slot, string moveId)
    {
        HotMonsterMachine? machine = rules.GetMonsterMachine(s.Enemies[slot].MonsterDefId);
        if (machine == null)
            return;
        short nodeId = machine.FindNode(moveId);
        if (nodeId >= 0)
        {
            s.Enemies[slot].CurrentMoveNode = nodeId;
            s.Enemies[slot].Flags &= ~HotEnemyFlags.NeedsInitialRoll;
        }
    }

    internal static void ForceStunnedMove(ref CombatHotState s, HotRules rules, byte slot, string? nextMoveId)
    {
        ushort mDefId = s.Enemies[slot].MonsterDefId;
        bool isTunnelerStun = mDefId > 0 && mDefId < rules.Monsters.Length
            && ResolveMonsterId(rules.Monsters[mDefId]) == HotMonsterId.Tunneler
            && string.Equals(nextMoveId, "BITE_MOVE", StringComparison.Ordinal);

        HotMonsterMachine? machine = rules.GetMonsterMachine(mDefId);
        if (machine == null)
        {
            s.Enemies[slot].Flags |= HotEnemyFlags.Stunned | HotEnemyFlags.SkipNextMove;
            return;
        }

        short stunnedNode = machine.FindNode("STUNNED");
        short followUp = nextMoveId != null
            ? machine.FindNode(nextMoveId)
            : (s.Enemies[slot].CurrentMoveNode != stunnedNode
                ? s.Enemies[slot].CurrentMoveNode
                : (s.Enemies[slot].AiRegs[3] > 0 ? (short)(s.Enemies[slot].AiRegs[3] - 1) : (short)-1));

        if (stunnedNode >= 0)
        {
            s.Enemies[slot].CurrentMoveNode = stunnedNode;
            s.Enemies[slot].Flags &= ~HotEnemyFlags.NeedsInitialRoll;
            // 0.113.1 BurrowedPower.AfterBlockBroken calls Tunneler.GetStunned() -> IsStunned = true
            // (cleared when STUNNED / StillDizzyMove executes at line 975).
            if (isTunnelerStun)
                s.Enemies[slot].Flags |= HotEnemyFlags.Stunned;
            if (followUp >= 0)
                s.Enemies[slot].AiRegs[3] = followUp + 1;
        }
        else
        {
            s.Enemies[slot].Flags |= HotEnemyFlags.Stunned | HotEnemyFlags.SkipNextMove;
            if (followUp >= 0)
                s.Enemies[slot].AiRegs[3] = followUp + 1;
        }
    }

    /// <summary>
    /// Stuns <paramref name="slot"/> with follow-up resolved to the current state's next state
    /// (matching <c>FlutterPower.AfterDamageReceived</c>: <c>StateLog.Last().GetNextState(...)</c>).
    /// </summary>
    internal static void ForceStunnedWithNextMove(ref CombatHotState s, HotRules rules, byte slot)
    {
        HotMonsterMachine? machine = rules.GetMonsterMachine(s.Enemies[slot].MonsterDefId);
        if (machine == null)
        {
            ForceStunnedMove(ref s, rules, slot, null);
            return;
        }

        short cur = s.Enemies[slot].CurrentMoveNode;
        short stunnedNode = machine.FindNode("STUNNED");
        short followUp = -1;
        if (cur >= 0 && cur < machine.Nodes.Length && cur != stunnedNode)
        {
            short next = machine.Nodes[cur].NextNode;
            followUp = next >= 0 ? next : cur;
        }
        else if (s.Enemies[slot].AiRegs[3] > 0)
        {
            followUp = (short)(s.Enemies[slot].AiRegs[3] - 1);
        }

        if (stunnedNode >= 0)
        {
            s.Enemies[slot].CurrentMoveNode = stunnedNode;
            s.Enemies[slot].Flags &= ~HotEnemyFlags.NeedsInitialRoll;
            if (followUp >= 0)
                s.Enemies[slot].AiRegs[3] = followUp + 1;
        }
        else
        {
            s.Enemies[slot].Flags |= HotEnemyFlags.Stunned | HotEnemyFlags.SkipNextMove;
            if (followUp >= 0)
                s.Enemies[slot].AiRegs[3] = followUp + 1;
        }
    }

    private static void AppendMonsterMoveLog(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        byte enemySlot,
        short moveNodeId)
    {
        ref HotEnemy enemy = ref s.Enemies[enemySlot];
        if (moveNodeId >= 0 && moveNodeId < 64)
            enemy.EverUsedMovesMask |= 1UL << moveNodeId;

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

        if (enemy.RecentMoveCount < HotLimits.MaxMonsterRecentMoves)
        {
            enemy.RecentMoves[enemy.RecentMoveCount] = (byte)moveNodeId;
            enemy.RecentMoveCount++;
            return;
        }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool CanPerformMonsterMove(in CombatHotState s, byte slot)
        => (s.Enemies[slot].Vital.Flags & HotCreatureFlags.ActiveInRoster) != 0
            && ((s.Enemies[slot].Vital.Flags & HotCreatureFlags.Alive) != 0
                || s.Enemies[slot].DeathPhase == HotDeathPhase.Reviving);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HotMonsterId ResolveMonsterId(HotMonsterDef def)
    {
        if (def.MonsterId != HotMonsterId.None)
            return def.MonsterId;
        HotMonsterId byType = HotMonsterCatalog.FindByTypeName(def.TypeName);
        return byType != HotMonsterId.None ? byType : HotMonsterCatalog.FindByEntry(def.Entry);
    }

    private static ushort FindMonsterDefId(HotRules rules, HotMonsterId monsterId)
    {
        ushort fast = rules.GetMonsterDefId(monsterId);
        if (fast != 0)
            return fast;

        int idx = (int)monsterId;
        if ((uint)idx < (uint)HotMonsterCatalog.ById.Length)
        {
            string entry = HotMonsterCatalog.ById[idx].Entry;
            if (!string.IsNullOrEmpty(entry) && rules.MonsterDefIdByEntry.TryGetValue(entry, out ushort byEntry))
                return byEntry;
        }
        return 0;
    }

    private static string GetEnemyMoveId(in CombatHotState s, HotRules rules, byte slot)
    {
        short nodeId = s.Enemies[slot].CurrentMoveNode;
        if (nodeId < 0)
            return string.Empty;
        HotMonsterMachine? machine = rules.GetMonsterMachine(s.Enemies[slot].MonsterDefId);
        return machine != null && nodeId < machine.Nodes.Length ? machine.Nodes[nodeId].MoveId : string.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetStaticInt(HotMonsterDef def, string key, int fallback)
        => def.StaticInts != null && def.StaticInts.TryGetValue(key, out int val) ? val : fallback;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsTough(HotRules rules)
        => (rules.AscensionsMask & (1UL << (int)AscensionLevel.ToughEnemies)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDeadly(HotRules rules)
        => (rules.AscensionsMask & (1UL << (int)AscensionLevel.DeadlyEnemies)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDecimillipedeSegment(HotMonsterId monsterId)
        => monsterId is HotMonsterId.DecimillipedeSegmentFront
            or HotMonsterId.DecimillipedeSegmentMiddle
            or HotMonsterId.DecimillipedeSegmentBack;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasFreeEncounterSlot(in CombatHotState s, HotRules rules)
    {
        if (s.EnemyCount >= HotLimits.MaxEnemies || s.RosterCount >= HotLimits.MaxEnemies)
            return false;
        int limit = rules.EncounterSlots.Length > 0 ? rules.EncounterSlots.Length : 5;
        return s.RosterCount < limit;
    }

    private static int CountLivingEnemiesInRoster(in CombatHotState s)
    {
        int alive = 0;
        for (int r = 0; r < s.RosterCount; r++)
        {
            if ((s.Enemies[s.ActiveEnemyRoster[r]].Vital.Flags & HotCreatureFlags.Alive) != 0)
                alive++;
        }
        return alive;
    }

    private static bool HasAnyLivingOrRevivingPrimaryEnemy(in CombatHotState s, HotRules rules)
    {
        ushort minionDefId = rules.GetPowerDefId(HotPowerId.MinionPower);
        for (int r = 0; r < s.RosterCount; r++)
        {
            byte eSlot = s.ActiveEnemyRoster[r];
            bool aliveOrReviving = (s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) != 0
                || s.Enemies[eSlot].DeathPhase == HotDeathPhase.Reviving;
            if (!aliveOrReviving)
                continue;
            if (minionDefId == 0 || HotCombatEngine.GetPowerAmount(in s, (byte)(3 + eSlot), minionDefId) <= 0)
                return true;
        }
        return false;
    }

    private static void RemoveFromActiveEnemyRoster(ref CombatHotState s, byte enemySlot)
    {
        int rosterCount = s.RosterCount;
        int write = 0;
        for (int read = 0; read < rosterCount; read++)
        {
            byte candidate = s.ActiveEnemyRoster[read];
            if (candidate != enemySlot)
                s.ActiveEnemyRoster[write++] = candidate;
        }
        for (int i = write; i < rosterCount; i++)
            s.ActiveEnemyRoster[i] = 0;
        s.RosterCount = (byte)write;
    }

    private static void RemoveAllPowersExceptMinion(ref CombatHotState s, HotRules rules, byte ownerSlot)
    {
        ushort minionDefId = rules.GetPowerDefId(HotPowerId.MinionPower);
        int i = 0;
        while (i < s.PowerCount)
        {
            if (s.Powers[i].OwnerSlot == ownerSlot && s.Powers[i].PowerDefId != minionDefId)
                HotCombatEngine.RemovePowerAt(ref s, i);
            else
                i++;
        }
        s.ActiveHookMask = HotCombatEngine.RecomputeActiveHookMask(in s, rules);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RemovePowerById(ref CombatHotState s, HotRules rules, byte ownerSlot, HotPowerId powerId)
    {
        ushort defId = rules.GetPowerDefId(powerId);
        if (defId != 0)
            HotCombatEngine.SetPowerAmount(ref s, rules, ownerSlot, defId, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplySelf(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        HotPowerId powerId,
        int amount)
        => HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, dealerSlot, dealerSlot, powerId, amount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyPlayerDebuff(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte dealerSlot,
        HotPowerId powerId,
        int amount)
        => HotCombatEngine.ApplyPowerById(ref s, ref cold, rules, scratch, 0, dealerSlot, powerId, amount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GainMoveBlock(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        byte targetSlot,
        int amount)
        => HotCombatEngine.GainBlock(ref s, ref cold, rules, scratch, targetSlot, amount, isCardBlock: true);

    private static void AddMonsterStatusCards(
        ref CombatHotState s,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotCustomCardId cardId,
        HotPileKind pile,
        int count,
        bool randomPos)
    {
        if (count <= 0)
            return;
        ushort cardDefId = rules.GetCardDefId(cardId);
        if (cardDefId == 0)
            return;

        int dstOffset = randomPos ? -2 : -1;
        for (int i = 0; i < count; i++)
        {
            HotCombatEngine.AddGeneratedFixedCard(
                ref s,
                ref cold,
                rules,
                scratch,
                cardDefId,
                pile,
                dstOffset: dstOffset,
                upgraded: false,
                createdByPlayer: false);
            if (scratch.NeedsChoice)
                return;
        }
    }
}
