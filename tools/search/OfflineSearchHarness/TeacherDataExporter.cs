using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CombatSolver;
using CombatSolver.HotKernel;
using CombatSolver.HotSearch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Dread;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace OfflineSearchHarness;

/// <summary>
/// Zero-allocation, domain-agnostic 35-feature <c>int32</c> state extractor over <see cref="CombatHotState"/>
/// and <see cref="HotRules"/> (ZERO card IDs, ZERO character-ID hardcoding).
/// Shared between offline Stage 8F Teacher-Student data export and calibrated Student evaluation.
/// </summary>
internal static class HotCalibrationFeatures
{
    public const int FeatureCount = 35;

    private const ulong RecurringBuffHookBits =
        (ulong)(HotHookMask.AfterSideTurnStart
        | HotHookMask.BeforeSideTurnStart
        | HotHookMask.BeforeTurnEnd
        | HotHookMask.AfterTurnEnd
        | HotHookMask.BeforeHandDraw
        | HotHookMask.AfterCardPlayed
        | HotHookMask.BeforeCardPlayed
        | HotHookMask.AfterCardDrawn
        | HotHookMask.AfterCardExhausted
        | HotHookMask.AfterCardDiscarded
        | HotHookMask.AfterOrbChanneled
        | HotHookMask.AfterOrbEvoked
        | HotHookMask.AfterBlockGained
        | HotHookMask.ModifyDamageAdditive
        | HotHookMask.ModifyDamageMultiplicative
        | HotHookMask.ModifyBlockAdditive
        | HotHookMask.ModifyBlockMultiplicative
        | HotHookMask.ModifyMaxEnergy
        | HotHookMask.ModifyHandDraw
        | HotHookMask.ModifyCardPlayCount
        | HotHookMask.ShouldClearBlock);

    public static readonly string[] FeatureNames =
    [
        "player_hp",              // 0
        "player_max_hp",          // 1
        "player_block",           // 2
        "unblocked_incoming",     // 3
        "lethal_incoming_excess", // 4
        "potion_cost_hp",         // 5
        "enemy_hp_sum",           // 6
        "enemy_block_sum",        // 7
        "alive_enemy_count",      // 8
        "reviving_enemy_count",   // 9
        "focus_fire_gap",         // 10
        "est_rem_turns",          // 11
        "poison_expected_dmg",    // 12
        "player_accelerant",      // 13
        "doom_quadratic_val",     // 14
        "doom_lethal_count",      // 15
        "orb_passive_dmg",        // 16
        "orb_passive_block",      // 17
        "max_dark_evoke",         // 18
        "orb_count",              // 19
        "orb_capacity",           // 20
        "recurring_buff_stacks",  // 21
        "player_core_stats",      // 22
        "player_debuff_stacks",   // 23
        "enemy_debuff_stacks",    // 24
        "enemy_buff_threat",      // 25
        "osty_hp",                // 26
        "playable_hand_count",    // 27
        "energy",                 // 28
        "stars",                  // 29
        "next_turn_carryover",    // 30
        "live_deck_size",         // 31
        "deck_clutter",           // 32
        "scaling_x_rem_turns",    // 33
        "focus_x_orb_count",      // 34
    ];

    /// <summary>
    /// Extracts the 35 domain-agnostic <c>int32</c> features directly from <paramref name="s"/> and
    /// <paramref name="rules"/> into <paramref name="dst"/> with zero managed heap allocations.
    /// </summary>
    public static void Extract(
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules,
        in HotEvalLookup lookup,
        int netPotionCostCenti,
        Span<int> dst)
    {
        if (dst.Length < FeatureCount)
            throw new ArgumentException($"Feature destination span must have length >= {FeatureCount}.", nameof(dst));

        int playerHp = Math.Clamp(s.Player.CurrentHp, 0, 999);
        int playerMaxHp = Math.Clamp(s.Player.MaxHp, 1, 999);
        int playerBlock = Math.Clamp(s.Player.Block, 0, 500);

        // 1. Single pass over active powers (player buffs/debuffs + per-enemy status arrays)
        int playerStrength = 0;
        int playerDexterity = 0;
        int playerFocus = 0;
        int playerVuln = 0;
        int playerIntangible = 0;
        int playerBuffer = 0;
        int playerArtifact = 0;
        int playerAccelerant = 0;
        int playerEndTurnBlock = 0;
        int nextTurnBlock = 0;
        int nextTurnEnergy = 0;
        int nextTurnDraw = 0;
        int nextTurnStars = 0;
        int nextTurnSummon = 0;
        bool preservesExcessBlock = false;
        int recurringBuffStacks = 0;
        int playerDebuffStacks = 0;
        int enemyDebuffStacks = 0;
        int enemyBuffThreat = 0;

        Span<int> enemyStr = stackalloc int[HotLimits.MaxEnemies];
        Span<byte> enemyWeak = stackalloc byte[HotLimits.MaxEnemies];
        Span<int> enemyPoison = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyDoom = stackalloc int[HotLimits.MaxEnemies];
        Span<int> enemyReattachHp = stackalloc int[HotLimits.MaxEnemies];
        Span<byte> enemyHasDamageCap = stackalloc byte[HotLimits.MaxEnemies];

        int powerCount = s.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref s.Powers[i];
            if (p.Amount == 0)
                continue;

            ushort defId = p.PowerDefId;
            if (defId == 0 || defId >= rules.Powers.Length)
                continue;

            HotPowerDef pDef = rules.Powers[defId];

            if (p.OwnerSlot == 0)
            {
                bool isOneTurnCarryover = false;
                if (defId == lookup.StrengthDefId) playerStrength += p.Amount;
                else if (defId == lookup.DexterityDefId) playerDexterity += p.Amount;
                else if (defId == lookup.FocusDefId) playerFocus += p.Amount;
                else if (defId == lookup.VulnerableDefId) playerVuln += p.Amount;
                else if (defId == lookup.IntangibleDefId) playerIntangible += p.Amount;
                else if (defId == lookup.BufferDefId) playerBuffer += p.Amount;
                else if (defId == lookup.ArtifactDefId) playerArtifact += p.Amount;
                else if (defId == lookup.AccelerantDefId) playerAccelerant += p.Amount;
                else if (defId == lookup.BarricadeDefId || defId == lookup.BlurDefId) preservesExcessBlock = true;
                else if (defId == lookup.MetallicizeDefId || defId == lookup.PlatingDefId) playerEndTurnBlock += p.Amount;
                else if (defId == lookup.BlockNextTurnDefId || defId == lookup.SelfFormingClayDefId || defId == lookup.ToricToughnessDefId)
                {
                    nextTurnBlock += p.Amount;
                    isOneTurnCarryover = true;
                }
                else if (defId == lookup.EnergyNextTurnDefId) { nextTurnEnergy += p.Amount; isOneTurnCarryover = true; }
                else if (defId == lookup.DrawCardsNextTurnDefId) { nextTurnDraw += p.Amount; isOneTurnCarryover = true; }
                else if (defId == lookup.StarNextTurnDefId) { nextTurnStars += p.Amount; isOneTurnCarryover = true; }
                else if (defId == lookup.SummonNextTurnDefId) { nextTurnSummon += p.Amount; isOneTurnCarryover = true; }

                if (pDef.Type == PowerType.Debuff && p.Amount > 0)
                {
                    playerDebuffStacks += Math.Min(p.Amount, 10);
                }
                else if (!isOneTurnCarryover
                    && pDef.Type == PowerType.Buff
                    && p.Amount > 0
                    && ((ulong)pDef.HookMask & RecurringBuffHookBits) != 0)
                {
                    // Distinct persistent recurring power presence + bounded stacks (zero power-ID switch).
                    recurringBuffStacks += 2 + Math.Min(p.Amount, 30);
                }
            }
            else if (p.OwnerSlot >= 3)
            {
                int eSlot = p.OwnerSlot - 3;
                if ((uint)eSlot >= HotLimits.MaxEnemies)
                    continue;

                if (defId == lookup.ReattachDefId && p.Amount > 0)
                {
                    enemyReattachHp[eSlot] = p.Amount;
                    continue;
                }

                if ((s.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Alive) == 0)
                    continue;

                if (defId == lookup.StrengthDefId)
                {
                    enemyStr[eSlot] += p.Amount;
                    if (p.Amount > 0)
                        enemyBuffThreat += Math.Min(p.Amount, 25) * 2;
                    else
                        enemyDebuffStacks += Math.Min(-p.Amount, 15);
                }
                else if (defId == lookup.WeakDefId && p.Amount > 0)
                {
                    enemyWeak[eSlot] = 1;
                    enemyDebuffStacks += Math.Min(p.Amount, 10);
                }
                else if (defId == lookup.PoisonDefId && p.Amount > 0)
                {
                    enemyPoison[eSlot] += p.Amount;
                }
                else if (defId == lookup.DoomDefId && p.Amount > 0)
                {
                    enemyDoom[eSlot] += p.Amount;
                }
                else if (defId == lookup.RitualDefId && p.Amount > 0)
                {
                    enemyBuffThreat += Math.Min(p.Amount, 12) * 4;
                }
                else if (p.Amount > 0
                    && (defId == lookup.IntangibleDefId
                        || defId == lookup.BufferDefId
                        || defId == lookup.SlipperyDefId
                        || defId == lookup.HardToKillDefId
                        || defId == lookup.HardenedShellDefId))
                {
                    enemyHasDamageCap[eSlot] = 1;
                    enemyBuffThreat += 3;
                }
                else if (pDef.Type == PowerType.Debuff && p.Amount > 0)
                {
                    enemyDebuffStacks += Math.Min(p.Amount, 15);
                }
                else if (pDef.Type == PowerType.Buff && p.Amount > 0)
                {
                    enemyBuffThreat += 2;
                }
            }
        }

        // 2. Orbs: PassiveVal and EvokeVal already bake playerFocus via HotCombatEngine.Orbs.
        int orbPassiveDmg = 0;
        int orbPassiveBlock = 0;
        int maxDarkEvoke = 0;
        int orbCount = Math.Min((int)s.OrbCount, HotLimits.MaxOrbs);
        int orbCapacity = Math.Min((int)s.OrbCapacity, HotLimits.MaxOrbs);

        for (int o = 0; o < orbCount; o++)
        {
            HotOrb orb = s.Orbs[o];
            HotOrbId orbId = HotCombatEngine.GetOrbId(rules, orb.OrbTypeId);
            if (orbId == HotOrbId.None)
                continue;

            int passiveVal = Math.Max(0, (int)orb.PassiveVal);
            int evokeVal = Math.Max(0, orb.EvokeVal);
            int triggerCount = (o == 0 && lookup.HasGoldPlatedCables) ? 2 : 1;

            switch (orbId)
            {
                case HotOrbId.Lightning:
                case HotOrbId.Glass:
                    orbPassiveDmg += passiveVal * triggerCount;
                    break;
                case HotOrbId.Dark:
                    orbPassiveDmg += passiveVal * triggerCount;
                    if (evokeVal > maxDarkEvoke)
                        maxDarkEvoke = evokeVal;
                    break;
                case HotOrbId.Frost:
                    orbPassiveBlock += passiveVal * triggerCount;
                    break;
                case HotOrbId.Plasma:
                    orbPassiveDmg += passiveVal * triggerCount * 8;
                    break;
            }
        }

        // 3. First roster pass: enemy HP, block, counts, Poison next-tick lethality, Doom^2/HP, incoming threat.
        int enemyHpSum = 0;
        int enemyBlockSum = 0;
        int aliveEnemyCount = 0;
        int revivingEnemyCount = 0;
        int revivingHpPenalty = 0;
        int reattachAliveCount = 0;
        int incomingDamage = 0;
        int lowestEffectiveEnemyHp = int.MaxValue;
        int doomQuadraticVal = 0;
        int doomLethalCount = 0;
        int nextTurnPoisonTickSum = 0;

        int rosterCount = Math.Min((int)s.RosterCount, HotLimits.MaxEnemies);
        int poisonTicksPerTurn = 1 + Math.Max(0, playerAccelerant);

        for (int r = 0; r < rosterCount; r++)
        {
            byte eSlot = s.ActiveEnemyRoster[r];
            ref readonly HotEnemy enemy = ref s.Enemies[eSlot];
            if ((enemy.Vital.Flags & HotCreatureFlags.Alive) == 0)
            {
                if (enemy.DeathPhase == HotDeathPhase.Reviving)
                {
                    revivingEnemyCount++;
                    revivingHpPenalty += enemyReattachHp[eSlot] > 0
                        ? enemyReattachHp[eSlot]
                        : Math.Max(15, enemy.Vital.MaxHp / 2);
                }
                continue;
            }

            aliveEnemyCount++;
            int curHp = Math.Max(0, enemy.Vital.CurrentHp);
            enemyHpSum += curHp;
            enemyBlockSum += Math.Max(0, enemy.Vital.Block);

            bool hasReattach = enemyReattachHp[eSlot] > 0;
            if (hasReattach)
                reattachAliveCount++;

            int doom = enemyDoom[eSlot];
            if (doom > 0 && curHp > 0 && !hasReattach)
            {
                int cappedDoom = Math.Min(doom, curHp);
                doomQuadraticVal += (int)(((long)cappedDoom * cappedDoom) / curHp);
                if (doom >= curHp)
                    doomLethalCount++;
            }

            int poison = enemyPoison[eSlot];
            long poisonOneTurnDmg = 0;
            if (poison > 0)
            {
                int ticks = Math.Min(poison, poisonTicksPerTurn);
                poisonOneTurnDmg = ((long)ticks * (2L * poison - ticks + 1L)) / 2L;
                nextTurnPoisonTickSum += (int)Math.Min((long)curHp, poisonOneTurnDmg);
            }

            bool poisonLethalBeforeAttack = curHp > 0
                && poisonOneTurnDmg >= curHp
                && !hasReattach
                && enemyHasDamageCap[eSlot] == 0;

            int effHp = Math.Max(0, curHp - (int)Math.Min((long)curHp, poisonOneTurnDmg) - doom);
            if (effHp < lowestEffectiveEnemyHp)
                lowestEffectiveEnemyHp = effHp;

            if ((enemy.Flags & HotEnemyFlags.SkipNextMove) == 0 && !poisonLethalBeforeAttack)
            {
                incomingDamage += EstimateEnemyIncomingDamage(
                    in enemy,
                    rules,
                    enemyStr[eSlot],
                    enemyWeak[eSlot] != 0,
                    playerVuln > 0,
                    playerIntangible > 0,
                    in lookup);
            }
        }

        int ostyHp = ((s.CombatFlags & HotCombatFlags.HasOsty) != 0 && (s.Osty.Flags & HotCreatureFlags.Alive) != 0)
            ? Math.Clamp(s.Osty.CurrentHp, 0, 500)
            : 0;
        int wuppyHp = ((s.CombatFlags & HotCombatFlags.HasWuppy) != 0 && (s.Wuppy.Flags & HotCreatureFlags.Alive) != 0)
            ? Math.Max(0, s.Wuppy.CurrentHp)
            : 0;

        int endTurnBlock = playerBlock + playerEndTurnBlock + orbPassiveBlock;
        int effectiveDefense = endTurnBlock + ostyHp + wuppyHp;
        int unblockedIncoming = Math.Clamp(Math.Max(0, incomingDamage - effectiveDefense), 0, 500);
        int lethalIncomingExcess = unblockedIncoming >= playerHp
            ? Math.Clamp(unblockedIncoming - playerHp + 10, 0, 500)
            : 0;
        int preservedExcessBlock = preservesExcessBlock
            ? Math.Clamp(Math.Max(0, endTurnBlock - incomingDamage), 0, 200)
            : 0;

        // 4. Horizon estimate: estRemTurns = clamp(EffectiveEnemyHp / max(15, ExpectedDpt), 1, 8)
        int expectedDpt = Math.Clamp(
            15 + Math.Max(0, playerStrength) * 3 + orbPassiveDmg + nextTurnPoisonTickSum + ostyHp / 2,
            15,
            250);
        int remainingHpApprox = Math.Max(0, enemyHpSum + revivingHpPenalty - doomQuadraticVal - nextTurnPoisonTickSum);
        int estRemTurns = remainingHpApprox <= 0
            ? 1
            : Math.Clamp((remainingHpApprox + expectedDpt - 1) / expectedDpt, 1, 8);

        // 5. Multi-turn triangular Poison damage over estRemTurns * (1 + Accelerant), capped per enemy at curHp.
        int poisonExpectedDmg = 0;
        for (int r = 0; r < rosterCount; r++)
        {
            byte eSlot = s.ActiveEnemyRoster[r];
            ref readonly HotEnemy enemy = ref s.Enemies[eSlot];
            if ((enemy.Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;
            int poison = enemyPoison[eSlot];
            if (poison <= 0)
                continue;

            int curHp = Math.Max(0, enemy.Vital.CurrentHp);
            int totalTicks = Math.Min(poison, estRemTurns * poisonTicksPerTurn);
            long totalDmg = ((long)totalTicks * (2L * poison - totalTicks + 1L)) / 2L;
            poisonExpectedDmg += (int)Math.Min((long)curHp, totalDmg);
        }

        // 6. Focus-fire gap (multi-enemy non-Reattach)
        bool allReattach = reattachAliveCount > 0
            && (reattachAliveCount + revivingEnemyCount) >= aliveEnemyCount;
        int focusFireGap = (!allReattach && aliveEnemyCount > 1 && lowestEffectiveEnemyHp < int.MaxValue)
            ? Math.Clamp((enemyHpSum / aliveEnemyCount) - lowestEffectiveEnemyHp, 0, 200)
            : 0;

        // 7. Hand playable count, retained cards, and deck size / clutter (ZERO card IDs!)
        int handCount = s.HandCount;
        int playableHandCount = 0;
        int retainedCount = 0;
        int handClutter = 0;
        int energy = Math.Clamp(s.Energy, 0, 30);
        int stars = Math.Clamp(s.Stars, 0, 30);

        for (int h = 0; h < handCount; h++)
        {
            ref readonly HotCard card = ref s.Cards[h];
            if (card.CardDefId >= rules.Cards.Length)
                continue;

            HotCardDef cDef = rules.Cards[card.CardDefId];
            if (cDef.Type is CardType.Status or CardType.Curse)
            {
                handClutter++;
                continue;
            }

            if ((card.LocalKeywordsMask & HotCardKeywordMask.Retain) != 0
                || (card.Flags & HotCardFlags.SingleTurnRetain) != 0)
            {
                retainedCount++;
            }

            if ((card.LocalKeywordsMask & HotCardKeywordMask.Unplayable) != 0)
                continue;

            int eCost = (card.Flags & HotCardFlags.CostsX) != 0
                ? s.Energy
                : HotCombatEngine.GetEffectiveEnergyCost(in s, in card, cold, rules, isInPlayablePile: true);
            int sCost = (card.Flags & HotCardFlags.HasStarCostX) != 0
                ? s.Stars
                : HotCombatEngine.GetEffectiveStarCost(in s, in card, cold, rules, isInPlayablePile: true);
            if (eCost <= s.Energy && sCost <= s.Stars)
                playableHandCount++;
        }

        int pileClutter = 0;
        int livePileEnd = Math.Min(s.DiscardStart + s.DiscardCount, s.TotalCardCount);
        for (int idx = s.DrawStart; idx < livePileEnd; idx++)
        {
            ushort cDefId = s.Cards[idx].CardDefId;
            if (cDefId < rules.Cards.Length && rules.Cards[cDefId].Type is CardType.Status or CardType.Curse)
                pileClutter++;
        }

        int liveDeckSize = Math.Clamp(s.HandCount + s.DrawCount + s.DiscardCount, 0, 256);
        int deckClutter = Math.Clamp(handClutter * 2 + pileClutter, 0, 200);

        int playerCoreStats = Math.Clamp(
            playerStrength
            + playerDexterity
            + Math.Min(playerIntangible, 6) * 8
            + Math.Min(playerBuffer, 6) * 4
            + Math.Min(playerArtifact, 6) * 2
            + Math.Min(playerEndTurnBlock, 60) / 2
            + preservedExcessBlock / 4,
            -50,
            300);

        int nextTurnCarryover = Math.Clamp(
            Math.Min(nextTurnBlock, 80) / 2
            + Math.Min(nextTurnEnergy, 8) * 6
            + Math.Min(nextTurnDraw + Math.Max(0, s.DrawNextTurn), 8) * 4
            + Math.Min(nextTurnStars, 10) * 3
            + Math.Min(nextTurnSummon, 30) / 2
            + Math.Min(retainedCount, 8) * 4
            + Math.Min((int)s.Fool.MelodyCardsFinished, 12) * 4,
            0,
            400);

        int clampedRecurring = Math.Clamp(recurringBuffStacks, 0, 500);
        int scalingBase = clampedRecurring + Math.Max(0, playerStrength + playerDexterity) * 2;
        int scalingXRemTurns = Math.Clamp(scalingBase * Math.Max(0, estRemTurns - 1), 0, 4000);
        int focusXOrbCount = Math.Clamp(playerFocus * orbCount, -200, 500);

        dst[0] = playerHp;
        dst[1] = playerMaxHp;
        dst[2] = playerBlock;
        dst[3] = unblockedIncoming;
        dst[4] = lethalIncomingExcess;
        dst[5] = Math.Clamp(netPotionCostCenti / HotSearchConstants.HpFixedScale, 0, 200);
        dst[6] = Math.Clamp(enemyHpSum, 0, 5000);
        dst[7] = Math.Clamp(enemyBlockSum, 0, 1000);
        dst[8] = aliveEnemyCount;
        dst[9] = revivingEnemyCount;
        dst[10] = focusFireGap;
        dst[11] = estRemTurns;
        dst[12] = Math.Clamp(poisonExpectedDmg, 0, 3000);
        dst[13] = Math.Clamp(playerAccelerant, 0, 20);
        dst[14] = Math.Clamp(doomQuadraticVal, 0, 3000);
        dst[15] = doomLethalCount;
        dst[16] = Math.Clamp(orbPassiveDmg, 0, 500);
        dst[17] = Math.Clamp(orbPassiveBlock, 0, 500);
        dst[18] = Math.Clamp(maxDarkEvoke, 0, 1000);
        dst[19] = orbCount;
        dst[20] = orbCapacity;
        dst[21] = clampedRecurring;
        dst[22] = playerCoreStats;
        dst[23] = Math.Clamp(playerDebuffStacks, 0, 100);
        dst[24] = Math.Clamp(enemyDebuffStacks, 0, 200);
        dst[25] = Math.Clamp(enemyBuffThreat, 0, 200);
        dst[26] = ostyHp;
        dst[27] = Math.Clamp(playableHandCount, 0, 20);
        dst[28] = energy;
        dst[29] = stars;
        dst[30] = nextTurnCarryover;
        dst[31] = liveDeckSize;
        dst[32] = deckClutter;
        dst[33] = scalingXRemTurns;
        dst[34] = focusXOrbCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EstimateEnemyIncomingDamage(
        in HotEnemy enemy,
        HotRules rules,
        int enemyStrength,
        bool enemyWeak,
        bool playerVulnerable,
        bool playerIntangible,
        in HotEvalLookup lookup)
    {
        if (enemy.MonsterDefId == 0 || enemy.CurrentMoveNode < 0)
            return 0;

        HotMonsterMachine? machine = rules.GetMonsterMachine(enemy.MonsterDefId);
        if (machine == null || enemy.CurrentMoveNode >= machine.Nodes.Length)
            return 0;

        HotMonsterNode node = machine.Nodes[enemy.CurrentMoveNode];
        int total = 0;

        if (node.Attacks.Length > 0)
        {
            for (int i = 0; i < node.Attacks.Length; i++)
            {
                HotMonsterAttack atk = node.Attacks[i];
                if (atk.Repeats == 0)
                    continue;
                int hit = ScaleEnemyHit(atk.BaseDamage, enemyStrength, enemyWeak, playerVulnerable, playerIntangible, in lookup);
                total += hit * atk.Repeats;
            }
        }
        else if (node.Repeats > 0)
        {
            int hits = node.Repeats;
            if (rules.Monsters[enemy.MonsterDefId].StateKind == HotMonsterStateKind.TestSubject
                && string.Equals(node.MoveId, "MULTI_CLAW_MOVE", StringComparison.Ordinal))
            {
                hits = Math.Max(1, rules.Monsters[enemy.MonsterDefId].TestSubjectBaseMultiClawCount + enemy.AiRegs[1]);
            }
            int hit = ScaleEnemyHit(node.BaseDamage, enemyStrength, enemyWeak, playerVulnerable, playerIntangible, in lookup);
            total += hit * hits;
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScaleEnemyHit(
        int baseDamage,
        int enemyStrength,
        bool enemyWeak,
        bool playerVulnerable,
        bool playerIntangible,
        in HotEvalLookup lookup)
    {
        int dmg = baseDamage + enemyStrength;
        if (dmg <= 0)
            return 0;
        if (enemyWeak)
            dmg = lookup.HasPaperKrane ? (dmg * 60) / 100 : (dmg * 75) / 100;
        if (playerVulnerable)
            dmg = lookup.HasOddMushroom ? (dmg * 125) / 100 : (dmg * 150) / 100;
        if (playerIntangible && dmg > 1)
            dmg = 1;
        return Math.Max(0, dmg);
    }
}

/// <summary>
/// Stage 8F (P4) Offline Teacher-Student Auto-Calibration Data Exporter (<c>--export-teacher-pairs</c>):
/// generates unbiased fights across all 6 characters × Normal/Elite/Boss × Standard/Dread/Blitz,
/// solves each fight with a wide offline Teacher (<c>W=400</c>, <c>1.2M</c> nodes, <c>FixedBudget=true</c>, <c>DOP=8</c>),
/// replays the Teacher's winning route across Turns 1..4 inside <see cref="HotCombatEngine"/>, and extracts
/// clean same-turn / turn-boundary <c>(winner_state_t, loser_sibling_t)</c> preference pairs using
/// <see cref="HotCalibrationFeatures"/>'s 35-feature domain-agnostic <c>int32</c> vector to
/// <c>.local/bench/teacher-pairs-{train,val}.jsonl</c>.
/// </summary>
internal static class TeacherDataExporter
{
    private static readonly string[] AllCharacters =
        ["IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER", "FOOL"];

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record ExportScenario(
        string Split,
        int Index,
        string FightId,
        string RoomTypeName,
        CombatSetupSpec Setup);

    private sealed record TeacherPairRecord
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("split")]
        public required string Split { get; init; }

        [JsonPropertyName("fight_id")]
        public required string FightId { get; init; }

        [JsonPropertyName("character_id")]
        public required string CharacterId { get; init; }

        [JsonPropertyName("encounter_id")]
        public required string EncounterId { get; init; }

        [JsonPropertyName("room_type")]
        public required string RoomType { get; init; }

        [JsonPropertyName("game_mode")]
        public required string GameMode { get; init; }

        [JsonPropertyName("turn")]
        public int Turn { get; init; }

        [JsonPropertyName("step")]
        public int Step { get; init; }

        [JsonPropertyName("pair_kind")]
        public required string PairKind { get; init; }

        [JsonPropertyName("teacher_v_centi")]
        public long TeacherVCenti { get; init; }

        [JsonPropertyName("teacher_hp_loss")]
        public int TeacherHpLoss { get; init; }

        [JsonPropertyName("teacher_turns")]
        public int TeacherTurns { get; init; }

        [JsonPropertyName("teacher_potions")]
        public int TeacherPotions { get; init; }

        [JsonPropertyName("winner_eval_score")]
        public long WinnerEvalScore { get; init; }

        [JsonPropertyName("loser_eval_score")]
        public long LoserEvalScore { get; init; }

        [JsonPropertyName("winner")]
        public required int[] Winner { get; init; }

        [JsonPropertyName("loser")]
        public required int[] Loser { get; init; }
    }

    private struct PotionTracker
    {
        public ushort RootOccupiedMask;
        public ushort RootConsumedMask;
        public int OptionalPaidRawHpCost;
        public int OptionalPaidCount;

        public readonly int ComputeNetPotionCostCenti(in CombatHotState state, in HotSearchPolicy policy)
        {
            int emptySlots = 0;
            int slotCount = Math.Min((int)state.PotionSlotCount, HotLimits.MaxPotions);
            for (int i = 0; i < slotCount; i++)
            {
                if (state.Potions[i] == 0)
                    emptySlots++;
            }
            int alchemizeCount = state.Growth[4];
            return HotPotionPolicy.ComputeNetPotionOpportunityCostCenti(
                OptionalPaidRawHpCost,
                OptionalPaidCount,
                alchemizeCount,
                emptySlots,
                in policy);
        }

        public void RecordPotionAction(
            in CombatHotState stateBefore,
            int slot,
            HotRules rules,
            short[] rootSlotCosts,
            in HotSearchPolicy policy)
        {
            if ((uint)slot >= HotLimits.MaxPotions || (uint)slot >= (uint)stateBefore.PotionSlotCount)
                return;
            ushort pDefId = stateBefore.Potions[slot];
            if (pDefId == 0 || pDefId >= rules.Potions.Length)
                return;

            ushort slotBit = (ushort)(1 << slot);
            if ((stateBefore.CombatGeneratedPotionMask & slotBit) != 0)
                return;
            if ((RootOccupiedMask & ~RootConsumedMask & slotBit) == 0)
                return;

            RootConsumedMask |= slotBit;
            if ((policy.ForcedPotionSlotMask & slotBit) != 0)
                return;

            short cost = (uint)slot < (uint)rootSlotCosts.Length && rootSlotCosts[slot] >= 0
                ? rootSlotCosts[slot]
                : HotPotionPolicy.ComputePotionDefCost(
                    rules.Potions[pDefId],
                    policy.HasRenewablePotionShapedRock,
                    policy.InitialPlayerMaxHp,
                    policy.PotionThresholds);
            if (cost > 0)
            {
                OptionalPaidRawHpCost += cost;
                OptionalPaidCount++;
            }
        }
    }

    private sealed class DivergenceSeed(
        CombatHotState state,
        HotColdSidecar? cold,
        PotionTracker potions,
        int stepIdx,
        bool skipGreedyCompletion)
    {
        public CombatHotState State = state;
        public readonly HotColdSidecar? Cold = cold;
        public readonly PotionTracker Potions = potions;
        public readonly int StepIdx = stepIdx;
        public readonly bool SkipGreedyCompletion = skipGreedyCompletion;
    }

    private sealed record CandidateLoser(
        long EvalScore,
        HotHash128 StateHash,
        int[] Features);

    private sealed class SplitStats
    {
        public int TotalFights;
        public int TeacherWins;
        public int TeacherDefeats;
        public int HardErrors;
        public int ReplaySkipped;
        public int PairsExported;
        public long TotalTeacherExpandedNodes;
        public double TotalTeacherSolveMs;
        public readonly Dictionary<string, int> PairsByKind = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> PairsByCharacter = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> PairsByRoomType = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> PairsByTurn = new(StringComparer.Ordinal);

        public void RecordPair(TeacherPairRecord rec)
        {
            PairsExported++;
            Increment(PairsByKind, rec.PairKind);
            Increment(PairsByCharacter, rec.CharacterId);
            Increment(PairsByRoomType, rec.RoomType);
            Increment(PairsByTurn, $"T{rec.Turn}");
        }

        private static void Increment(Dictionary<string, int> map, string key)
            => map[key] = map.TryGetValue(key, out int cur) ? cur + 1 : 1;
    }

    internal static int Run(string[] args)
    {
        int totalFights = 800;
        int? trainFightsOverride = null;
        int? valFightsOverride = null;
        int seed = 202604;
        int teacherBeam = 400;
        int teacherNodes = 1_200_000;
        int dop = 8;
        int budgetMs = 60_000;
        int maxTurns = 4;
        int maxPairsPerStep = 4;
        int maxPairsPerFight = 32;
        string potionPolicyName = "Smart";
        string sourceMode = "realistic";
        string repoRoot = ResolveRepoRoot();
        string outDir = Path.Combine(repoRoot, ".local", "bench");
        string? trainOutOverride = null;
        string? valOutOverride = null;

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string NextVal() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"--export-teacher-pairs 选项 {key} 缺少参数。");
            switch (key)
            {
                case "--fights":
                    totalFights = Math.Max(2, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--train-fights":
                    trainFightsOverride = Math.Max(0, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--val-fights":
                    valFightsOverride = Math.Max(0, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--seed":
                    seed = int.Parse(NextVal(), CultureInfo.InvariantCulture);
                    break;
                case "--beam":
                    teacherBeam = Math.Clamp(int.Parse(NextVal(), CultureInfo.InvariantCulture), 16, HotSearchConstants.MaxBeamWidth);
                    break;
                case "--nodes":
                    teacherNodes = Math.Max(10_000, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--dop":
                    dop = Math.Clamp(int.Parse(NextVal(), CultureInfo.InvariantCulture), 1, HotSearchConstants.MaximumSearchMaxDegreeOfParallelism);
                    break;
                case "--budget-ms":
                    budgetMs = Math.Max(1_000, int.Parse(NextVal(), CultureInfo.InvariantCulture));
                    break;
                case "--max-turns":
                    maxTurns = Math.Clamp(int.Parse(NextVal(), CultureInfo.InvariantCulture), 1, 10);
                    break;
                case "--max-pairs-per-step":
                    maxPairsPerStep = Math.Clamp(int.Parse(NextVal(), CultureInfo.InvariantCulture), 1, 16);
                    break;
                case "--max-pairs-per-fight":
                    maxPairsPerFight = Math.Clamp(int.Parse(NextVal(), CultureInfo.InvariantCulture), 1, 128);
                    break;
                case "--potion-policy":
                    potionPolicyName = NextVal();
                    break;
                case "--source":
                    sourceMode = NextVal().ToLowerInvariant();
                    break;
                case "--out-dir":
                    outDir = Path.GetFullPath(NextVal());
                    break;
                case "--train-out":
                    trainOutOverride = Path.GetFullPath(NextVal());
                    break;
                case "--val-out":
                    valOutOverride = Path.GetFullPath(NextVal());
                    break;
                default:
                    throw new ArgumentException($"--export-teacher-pairs 未知选项：{key}");
            }
        }

        if (sourceMode is not ("realistic" or "fuzz" or "mixed"))
            throw new ArgumentException("--source 只支持 realistic|fuzz|mixed。");

        SolverPotionPolicy parsedPotionPolicy = Enum.Parse<SolverPotionPolicy>(potionPolicyName, ignoreCase: true);

        int trainFights = trainFightsOverride ?? ((totalFights * 3) / 4);
        int valFights = valFightsOverride ?? Math.Max(0, totalFights - trainFights);
        if (trainFights + valFights == 0)
            throw new ArgumentException("train 与 val 场次之和必须大于 0。");

        Directory.CreateDirectory(outDir);
        string trainPath = trainOutOverride ?? Path.Combine(outDir, "teacher-pairs-train.jsonl");
        string valPath = valOutOverride ?? Path.Combine(outDir, "teacher-pairs-val.jsonl");
        string schemaPath = Path.Combine(Path.GetDirectoryName(trainPath) ?? outDir, "teacher-pairs-schema.json");
        string summaryPath = Path.Combine(outDir, "teacher-export-summary.json");

        if (Path.GetDirectoryName(trainPath) is { Length: > 0 } trainDir)
            Directory.CreateDirectory(trainDir);
        if (Path.GetDirectoryName(valPath) is { Length: > 0 } valDir)
            Directory.CreateDirectory(valDir);

        Stopwatch totalSw = Stopwatch.StartNew();
        Console.WriteLine(
            $"=== --export-teacher-pairs (train={trainFights}, val={valFights}, seed={seed}, source={sourceMode}, " +
            $"teacher=W{teacherBeam}/{teacherNodes}nodes/DOP{dop}/budget{budgetMs}ms/FixedBudget=true, " +
            $"potion={parsedPotionPolicy}, turns=1..{maxTurns}) ===");

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        HarnessOptions modOptions = new()
        {
            Profile = "Custom",
            Beam = teacherBeam,
            Nodes = teacherNodes,
            MaxDegreeOfParallelism = dop,
            BudgetMilliseconds = budgetMs,
            PotionPolicy = parsedPotionPolicy.ToString(),
            SearchMode = "Coordinator",
            StopAtZeroLoss = false,
            WorkspaceDirectory = Path.Combine(repoRoot, ".local", "offline-harness"),
            OutputDirectory = Path.Combine(repoRoot, ".local", "offline-harness", "teacher-export-logs"),
        };
        GameBootstrap.EnsureBootstrapped(loop, modOptions);

        int trainSeed = seed;
        int valSeed = seed ^ 0x5A3C_19E7;
        List<ExportScenario> trainScenarios = BuildScenarios("train", trainFights, trainSeed, sourceMode);
        List<ExportScenario> valScenarios = BuildScenarios("val", valFights, valSeed, sourceMode);

        File.WriteAllText(
            schemaPath,
            JsonSerializer.Serialize(new
            {
                schema_version = 1,
                feature_count = HotCalibrationFeatures.FeatureCount,
                feature_names = HotCalibrationFeatures.FeatureNames,
                train_path = trainPath,
                val_path = valPath,
                train_seed = trainSeed,
                val_seed = valSeed,
                teacher = new
                {
                    beam_width = teacherBeam,
                    max_expanded_nodes = teacherNodes,
                    dop,
                    budget_ms = budgetMs,
                    fixed_budget = true,
                    potion_policy = parsedPotionPolicy.ToString(),
                },
            }, PrettyJsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        SplitStats trainStats = new();
        SplitStats valStats = new();
        HotExecScratch replayScratch = new();
        byte[] packBuf = new byte[HotLimits.MaxPackedStateBytes];
        HotAction[] actionBuf = new HotAction[256];
        HotChoiceSelection[] choiceOptBuf = new HotChoiceSelection[128];

        int gen2Before = GC.CollectionCount(2);
        using MemorySampler memory = new(TimeSpan.FromMilliseconds(250));

        try
        {
            ProcessSplit(
                loop, trainScenarios, trainPath, trainStats,
                teacherBeam, teacherNodes, dop, budgetMs, parsedPotionPolicy,
                maxTurns, maxPairsPerStep, maxPairsPerFight,
                replayScratch, packBuf, actionBuf, choiceOptBuf, totalSw);

            ProcessSplit(
                loop, valScenarios, valPath, valStats,
                teacherBeam, teacherNodes, dop, budgetMs, parsedPotionPolicy,
                maxTurns, maxPairsPerStep, maxPairsPerFight,
                replayScratch, packBuf, actionBuf, choiceOptBuf, totalSw);
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            BattleDamageTracker.Reset();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }

        memory.Dispose();
        totalSw.Stop();
        int gcGen2 = GC.CollectionCount(2) - gen2Before;
        double peakRssMb = Math.Round(
            Math.Max(memory.PeakWorkingSetBytes, memory.PeakProcessWorkingSetBytes) / (1024.0 * 1024.0), 2);

        object summaryReport = new
        {
            schema_version = 1,
            feature_count = HotCalibrationFeatures.FeatureCount,
            source = sourceMode,
            seed,
            train_seed = trainSeed,
            val_seed = valSeed,
            teacher_beam = teacherBeam,
            teacher_nodes = teacherNodes,
            dop,
            budget_ms = budgetMs,
            fixed_budget = true,
            potion_policy = parsedPotionPolicy.ToString(),
            max_turns = maxTurns,
            elapsed_seconds = Math.Round(totalSw.Elapsed.TotalSeconds, 2),
            peak_rss_mb = peakRssMb,
            gc_gen2 = gcGen2,
            train_path = trainPath,
            val_path = valPath,
            schema_path = schemaPath,
            train = SummarizeSplit(trainStats),
            val = SummarizeSplit(valStats),
        };

        File.WriteAllText(
            summaryPath,
            JsonSerializer.Serialize(summaryReport, PrettyJsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.WriteLine();
        Console.WriteLine("=== --export-teacher-pairs SUMMARY ===");
        Console.WriteLine(
            $"train: fights={trainStats.TotalFights} wins={trainStats.TeacherWins} defeats={trainStats.TeacherDefeats} " +
            $"errors={trainStats.HardErrors} pairs={trainStats.PairsExported} -> {trainPath}");
        Console.WriteLine(
            $"val:   fights={valStats.TotalFights} wins={valStats.TeacherWins} defeats={valStats.TeacherDefeats} " +
            $"errors={valStats.HardErrors} pairs={valStats.PairsExported} -> {valPath}");
        Console.WriteLine(
            $"elapsed={totalSw.Elapsed.TotalSeconds:F1}s peak_rss_mb={peakRssMb:F1} gc_gen2={gcGen2} summary={summaryPath}");

        int totalExported = trainStats.PairsExported + valStats.PairsExported;
        return totalExported > 0 ? 0 : 1;
    }

    private static void ProcessSplit(
        MainLoopContext loop,
        List<ExportScenario> scenarios,
        string outPath,
        SplitStats stats,
        int teacherBeam,
        int teacherNodes,
        int dop,
        int budgetMs,
        SolverPotionPolicy potionPolicy,
        int maxTurns,
        int maxPairsPerStep,
        int maxPairsPerFight,
        HotExecScratch replayScratch,
        byte[] packBuf,
        HotAction[] actionBuf,
        HotChoiceSelection[] choiceOptBuf,
        Stopwatch totalSw)
    {
        using StreamWriter writer = new(
            outPath,
            append: false,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 64 * 1024);

        for (int i = 0; i < scenarios.Count; i++)
        {
            ExportScenario scenario = scenarios[i];
            stats.TotalFights++;

            try
            {
                ExportSingleFight(
                    loop,
                    scenario,
                    writer,
                    stats,
                    teacherBeam,
                    teacherNodes,
                    dop,
                    budgetMs,
                    potionPolicy,
                    maxTurns,
                    maxPairsPerStep,
                    maxPairsPerFight,
                    replayScratch,
                    packBuf,
                    actionBuf,
                    choiceOptBuf);
            }
            catch (Exception ex)
            {
                stats.HardErrors++;
                Exception rootEx = Unwrap(ex);
                Console.Error.WriteLine(
                    $"[teacher:{scenario.Split}] fight#{scenario.Index} ({scenario.FightId}) ERROR: " +
                    $"{rootEx.GetType().Name}: {rootEx.Message}");
            }
            finally
            {
                RealCombatDriver.CleanupActiveRun();
                BattleDamageTracker.Reset();
            }

            if ((i + 1) % 20 == 0 || i + 1 == scenarios.Count)
            {
                writer.Flush();
                Console.WriteLine(
                    $"[teacher:{scenario.Split}] {i + 1}/{scenarios.Count} | wins={stats.TeacherWins} " +
                    $"defeats={stats.TeacherDefeats} errors={stats.HardErrors} pairs={stats.PairsExported} | " +
                    $"{totalSw.Elapsed.TotalSeconds:F1}s");
            }
        }

        writer.Flush();
    }

    private static void ExportSingleFight(
        MainLoopContext loop,
        ExportScenario scenario,
        StreamWriter writer,
        SplitStats stats,
        int teacherBeam,
        int teacherNodes,
        int dop,
        int budgetMs,
        SolverPotionPolicy potionPolicy,
        int maxTurns,
        int maxPairsPerStep,
        int maxPairsPerFight,
        HotExecScratch replayScratch,
        byte[] packBuf,
        HotAction[] actionBuf,
        HotChoiceSelection[] choiceOptBuf)
    {
        BattleDamageTracker.Reset();
        CombatState combat = RealCombatDriver.StartCombat(loop, scenario.Setup);

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SolverTheftPolicy? theftPolicy = SolverController.ResolveTheftPolicy(combat);
        SearchPolicySnapshot basePolicy = SolverController.CaptureSearchPolicy(
            settings, combat, includeTurnSetup: false, theftPolicy: theftPolicy);

        SolverSearchProfile teacherProfile = basePolicy.Profile with
        {
            BeamWidth = teacherBeam,
            MaxExpandedNodes = teacherNodes,
            MaxCardBranchesPerNode = Math.Max(basePolicy.Profile.MaxCardBranchesPerNode, 64),
            MaxPileChoiceBranchesPerAction = Math.Max(basePolicy.Profile.MaxPileChoiceBranchesPerAction, 24),
            MaxHandChoiceBranchesPerAction = Math.Max(basePolicy.Profile.MaxHandChoiceBranchesPerAction, 24),
            SoftTimeBudgetMilliseconds = budgetMs,
        };

        SearchPolicySnapshot teacherPolicy = basePolicy with
        {
            Profile = teacherProfile,
            PotionPolicy = potionPolicy,
            FixedBudget = true,
            BudgetOverrideMilliseconds = budgetMs,
            MaxDegreeOfParallelism = dop,
            AdaptiveEscalationEnabled = false,
            StopAtAcceptableBattleHpLoss = false,
            TranspositionEntryLimit = Math.Max(SearchPolicySnapshot.DefaultTranspositionEntryLimit, 1_500_000),
        };

        Stopwatch solveSw = Stopwatch.StartNew();
        SolverResult teacherResult = HotSearchCoordinator.Solve(
            root, names, damage, teacherPolicy, CancellationToken.None, null);
        solveSw.Stop();

        stats.TotalTeacherSolveMs += solveSw.Elapsed.TotalMilliseconds;
        stats.TotalTeacherExpandedNodes += teacherResult.TotalExpandedNodes > 0
            ? teacherResult.TotalExpandedNodes
            : teacherResult.ExpandedNodes;

        FightOutcomeRecord outcome = BenchmarkOutcomeHelper.FromSolverResult(
            scenario.FightId,
            scenario.Split,
            $"{scenario.Setup.CharacterId.ToLowerInvariant()}_{scenario.RoomTypeName.ToLowerInvariant()}_{scenario.Setup.GameMode.ToString().ToLowerInvariant()}",
            scenario.Setup.CharacterId,
            scenario.Setup.EncounterId,
            root,
            teacherPolicy,
            teacherResult,
            solveSw.Elapsed.TotalMilliseconds);

        if (!outcome.Won || !outcome.Survives)
        {
            stats.TeacherDefeats++;
            return;
        }

        stats.TeacherWins++;

        IReadOnlyList<PlanAction> actions = teacherResult.BestNode.Actions;
        if (actions.Count == 0)
            return;

        HotRootBundle rootBundle = root.HotRoot;
        HotRules rules = rootBundle.Rules;
        CombatHotState curState = rootBundle.RootState;
        HotColdSidecar? curCold = rootBundle.RootCold;

        short[] rootSlotCosts = HotPotionPolicy.BuildRootSlotCosts(
            root, in curState, rules, teacherPolicy.PotionStrategy.Thresholds);
        HotSearchPolicy hotPolicy = new(root, damage, teacherPolicy, rules, in curState, rootSlotCosts);
        HotEvalLookup evalLookup = new(rules);

        PotionTracker curPotions = CreateRootPotionTracker(in curState);

        // Handle optional root TurnSetup edge before Turn 1 Play actions.
        if (HotCombatEngine.HasPendingTurnSetup(in curState))
        {
            HotStatePacker.CanonicalizeForPack(ref curState, ref curCold);
            HotParallelExpander.AssignDeterministicCardUids(ref curState);
            if (!TryReplayActionWithPlannedChoices(
                ref curState, ref curCold, rules, replayScratch, HotAction.TurnSetup(),
                teacherResult.TurnSetupChoices, choiceOptBuf))
            {
                stats.ReplaySkipped++;
                return;
            }
        }

        int emittedForFight = 0;
        int actionCursor = 0;
        int startTurn = curState.PlayerTurnNumber;
        int maxAllowedTurn = startTurn + maxTurns - 1;
        int globalStepIndex = 0;

        while (actionCursor < actions.Count
            && emittedForFight < maxPairsPerFight
            && curState.TerminalOutcome == HotCombatOutcome.Ongoing
            && curState.PlayerTurnNumber <= maxAllowedTurn)
        {
            int turn = curState.PlayerTurnNumber;

            // Collect all Teacher actions belonging to this Turn t.
            int turnStartCursor = actionCursor;
            int turnEndCursor = actionCursor;
            while (turnEndCursor < actions.Count && actions[turnEndCursor].Turn == turn)
                turnEndCursor++;

            if (turnEndCursor == turnStartCursor)
                break;

            // Track Teacher's full Turn-t trajectory AND non-Teacher divergence states to compare at Turn-t boundary.
            List<DivergenceSeed> divergenceSeeds = [];
            CombatHotState teacherPreEndState = default;
            HotColdSidecar? teacherPreEndCold = null;
            PotionTracker teacherPreEndPotions = default;
            bool hasTeacherPreEnd = false;

            CombatHotState teacherPostEndState = default;
            HotColdSidecar? teacherPostEndCold = null;
            PotionTracker teacherPostEndPotions = default;
            bool hasTeacherPostEnd = false;

            bool replayOk = true;

            for (int idx = turnStartCursor; idx < turnEndCursor; idx++)
            {
                if (curState.TerminalOutcome != HotCombatOutcome.Ongoing || curState.PlayerTurnNumber != turn)
                    break;

                HotStatePacker.CanonicalizeForPack(ref curState, ref curCold);
                HotParallelExpander.AssignDeterministicCardUids(ref curState);

                PlanAction planAction = actions[idx];
                if (!TryResolvePlanActionToHot(in curState, curCold, rules, planAction, out HotAction winnerAction))
                {
                    replayOk = false;
                    break;
                }

                // Collect remaining cards Teacher plays in this Turn t to filter commutative permutations.
                HashSet<(ushort CardDefId, int TargetSlot)> remainingTeacherCards = [];
                for (int future = idx; future < turnEndCursor; future++)
                {
                    PlanAction fa = actions[future];
                    if (fa.Kind != PlanActionKind.PlayCard)
                        continue;
                    ushort defId = rules.FindCardDefId(fa.CardId);
                    int targetSlot = ResolveTargetEnemySlot(in curState, fa.TargetCombatId, fa.TargetIndex);
                    if (defId != 0)
                        remainingTeacherCards.Add((defId, targetSlot));
                }

                // Execute Teacher's chosen action on a copy first so parent state stays intact for sibling enumeration.
                CombatHotState nextState = curState;
                HotColdSidecar? nextCold = curCold;
                PotionTracker nextPotions = curPotions;
                if (winnerAction.Kind == HotActionKind.UsePotion)
                    nextPotions.RecordPotionAction(in curState, winnerAction.SlotOrHandIdx, rules, rootSlotCosts, in hotPolicy);

                List<PlanCardChoice> plannedChoices = CollectPlannedChoices(planAction);
                if (!TryReplayActionWithPlannedChoices(
                    ref nextState, ref nextCold, rules, replayScratch, winnerAction, plannedChoices, choiceOptBuf))
                {
                    replayOk = false;
                    break;
                }

                // Snapshot pre-EndTurn & post-EndTurn states along Teacher's route.
                if (winnerAction.Kind == HotActionKind.EndTurn)
                {
                    teacherPreEndState = curState;
                    teacherPreEndCold = curCold;
                    teacherPreEndPotions = curPotions;
                    hasTeacherPreEnd = true;

                    if (nextState.TerminalOutcome == HotCombatOutcome.Ongoing && nextState.PlayerTurnNumber == turn + 1)
                    {
                        teacherPostEndState = nextState;
                        teacherPostEndCold = nextCold;
                        teacherPostEndPotions = nextPotions;
                        hasTeacherPostEnd = true;
                    }
                }

                // Enumerate genuine non-Teacher sibling actions at this decision point.
                bool includePotions = potionPolicy != SolverPotionPolicy.Disabled;
                int legalCount = HotCombatEngine.EnumerateLegalActions(
                    in curState, curCold, rules, actionBuf, includePotions: includePotions);

                bool winnerStayedInTurn = winnerAction.Kind != HotActionKind.EndTurn
                    && nextState.TerminalOutcome == HotCombatOutcome.Ongoing
                    && nextState.PlayerTurnNumber == turn;

                HotHash128 winnerHash = winnerStayedInTurn
                    ? ComputeStateHash(in nextState, nextCold, packBuf)
                    : default;
                int[]? winnerStepFeatures = null;
                long winnerStepEval = 0;

                if (winnerStayedInTurn)
                {
                    int wPotCenti = nextPotions.ComputeNetPotionCostCenti(in nextState, in hotPolicy);
                    winnerStepFeatures = ExtractFeatureArray(in nextState, nextCold, rules, in evalLookup, wPotCenti);
                    winnerStepEval = EvaluateHotScore(in nextState, nextCold, rules, in hotPolicy, in evalLookup, wPotCenti, globalStepIndex + 1);
                }

                List<CandidateLoser> stepLosers = [];
                HashSet<(HotActionKind Kind, ushort EntityDefId, byte Upgrade, sbyte Target)> seenSiblings = [];
                HashSet<HotHash128> seenStepHashes = [];
                if (winnerStayedInTurn)
                    seenStepHashes.Add(winnerHash);

                for (int a = 0; a < legalCount; a++)
                {
                    HotAction sibAction = actionBuf[a];
                    if (sibAction == winnerAction)
                        continue;

                    // Deduplicate equivalent sibling cards in hand & skip commutative Teacher cards.
                    if (sibAction.Kind == HotActionKind.PlayCard)
                    {
                        ref readonly HotCard sibCard = ref curState.Cards[sibAction.SlotOrHandIdx];
                        if (!seenSiblings.Add((HotActionKind.PlayCard, sibCard.CardDefId, sibCard.UpgradeLevel, sibAction.TargetEnemySlot)))
                            continue;
                        if (remainingTeacherCards.Contains((sibCard.CardDefId, sibAction.TargetEnemySlot)))
                            continue;
                    }
                    else if (sibAction.Kind == HotActionKind.UsePotion)
                    {
                        ushort pDefId = curState.Potions[sibAction.SlotOrHandIdx];
                        if (pDefId == 0 || pDefId >= rules.Potions.Length)
                            continue;
                        if (!seenSiblings.Add((HotActionKind.UsePotion, pDefId, 0, sibAction.TargetEnemySlot)))
                            continue;
                        if (HotPotionPolicy.IsProvableNoOpPotionUse(in curState, rules.Potions[pDefId], isForced: false, in hotPolicy))
                            continue;
                    }
                    else if (sibAction.Kind == HotActionKind.EndTurn)
                    {
                        // Premature EndTurn diverges from continuing Turn t: keep as-is (skip greedy completion) for turn-boundary comparison.
                        if (winnerAction.Kind != HotActionKind.EndTurn && divergenceSeeds.Count < maxPairsPerStep * 3)
                            divergenceSeeds.Add(new DivergenceSeed(curState, curCold, curPotions, globalStepIndex, skipGreedyCompletion: true));
                        continue;
                    }
                    else
                    {
                        continue;
                    }

                    CombatHotState sibState = curState;
                    HotColdSidecar? sibCold = curCold;
                    PotionTracker sibPotions = curPotions;
                    if (sibAction.Kind == HotActionKind.UsePotion)
                        sibPotions.RecordPotionAction(in curState, sibAction.SlotOrHandIdx, rules, rootSlotCosts, in hotPolicy);

                    if (!TryExecuteActionGreedyChoices(
                        ref sibState, ref sibCold, rules, in hotPolicy, in evalLookup,
                        sibPotions.ComputeNetPotionCostCenti(in sibState, in hotPolicy),
                        globalStepIndex + 1, replayScratch, sibAction, choiceOptBuf))
                    {
                        continue;
                    }

                    // Never treat an immediate Victory sibling as a loser.
                    if (sibState.TerminalOutcome != HotCombatOutcome.Ongoing)
                        continue;

                    // Same-turn mid-turn sibling pair (only when both Teacher and sibling stayed in Turn t).
                    if (winnerStayedInTurn && sibState.PlayerTurnNumber == turn && winnerStepFeatures != null)
                    {
                        HotHash128 sibHash = ComputeStateHash(in sibState, sibCold, packBuf);
                        if (seenStepHashes.Add(sibHash))
                        {
                            int sPotCenti = sibPotions.ComputeNetPotionCostCenti(in sibState, in hotPolicy);
                            int[] sibFeatures = ExtractFeatureArray(in sibState, sibCold, rules, in evalLookup, sPotCenti);
                            if (!AreFeaturesIdentical(winnerStepFeatures, sibFeatures))
                            {
                                long sibEval = EvaluateHotScore(
                                    in sibState, sibCold, rules, in hotPolicy, in evalLookup, sPotCenti, globalStepIndex + 1);
                                stepLosers.Add(new CandidateLoser(sibEval, sibHash, sibFeatures));
                            }
                        }
                    }

                    // Seed turn-boundary rollout comparison (covers e.g. playing SovereignBlade vs Teacher EndTurn hold,
                    // or playing Strike+Defend vs Teacher DemonForm/SerpentForm/Dark-cooking).
                    if (sibState.PlayerTurnNumber == turn && divergenceSeeds.Count < maxPairsPerStep * 3)
                        divergenceSeeds.Add(new DivergenceSeed(sibState, sibCold, sibPotions, globalStepIndex, skipGreedyCompletion: false));
                }

                // Hard-negative mining: keep top-K losers that current HotEvaluator ranks highest!
                if (winnerStayedInTurn && winnerStepFeatures != null && stepLosers.Count > 0 && emittedForFight < maxPairsPerFight)
                {
                    stepLosers.Sort(static (x, y) => y.EvalScore.CompareTo(x.EvalScore));
                    int emitCap = Math.Min(maxPairsPerStep, Math.Min(stepLosers.Count, maxPairsPerFight - emittedForFight));
                    for (int k = 0; k < emitCap; k++)
                    {
                        CandidateLoser loser = stepLosers[k];
                        TeacherPairRecord rec = new()
                        {
                            Split = scenario.Split,
                            FightId = scenario.FightId,
                            CharacterId = scenario.Setup.CharacterId,
                            EncounterId = scenario.Setup.EncounterId,
                            RoomType = scenario.RoomTypeName,
                            GameMode = scenario.Setup.GameMode.ToString(),
                            Turn = turn,
                            Step = globalStepIndex,
                            PairKind = "step_mid_turn",
                            TeacherVCenti = outcome.EconomicValueCenti,
                            TeacherHpLoss = outcome.HpLoss,
                            TeacherTurns = outcome.Turns,
                            TeacherPotions = outcome.PotionsUsed,
                            WinnerEvalScore = winnerStepEval,
                            LoserEvalScore = loser.EvalScore,
                            Winner = winnerStepFeatures,
                            Loser = loser.Features,
                        };
                        writer.WriteLine(JsonSerializer.Serialize(rec, CompactJsonOptions));
                        stats.RecordPair(rec);
                        emittedForFight++;
                    }
                }

                curState = nextState;
                curCold = nextCold;
                curPotions = nextPotions;
                globalStepIndex++;
            }

            if (!replayOk)
            {
                stats.ReplaySkipped++;
                break;
            }

            // Emit Turn-t pre-EndTurn & post-EndTurn boundary preference pairs (exact HotBeamRetention frontier!).
            if (hasTeacherPreEnd && divergenceSeeds.Count > 0 && emittedForFight < maxPairsPerFight)
            {
                EmitTurnBoundaryPairs(
                    scenario,
                    outcome,
                    turn,
                    in teacherPreEndState,
                    teacherPreEndCold,
                    teacherPreEndPotions,
                    hasTeacherPostEnd,
                    in teacherPostEndState,
                    teacherPostEndCold,
                    teacherPostEndPotions,
                    divergenceSeeds,
                    rules,
                    in hotPolicy,
                    in evalLookup,
                    replayScratch,
                    packBuf,
                    actionBuf,
                    choiceOptBuf,
                    maxPairsPerStep,
                    maxPairsPerFight,
                    ref emittedForFight,
                    writer,
                    stats);
            }

            actionCursor = turnEndCursor;
        }
    }

    private static void EmitTurnBoundaryPairs(
        ExportScenario scenario,
        FightOutcomeRecord outcome,
        int turn,
        in CombatHotState teacherPreEndState,
        HotColdSidecar? teacherPreEndCold,
        PotionTracker teacherPreEndPotions,
        bool hasTeacherPostEnd,
        in CombatHotState teacherPostEndState,
        HotColdSidecar? teacherPostEndCold,
        PotionTracker teacherPostEndPotions,
        List<DivergenceSeed> divergenceSeeds,
        HotRules rules,
        in HotSearchPolicy hotPolicy,
        in HotEvalLookup evalLookup,
        HotExecScratch scratch,
        byte[] packBuf,
        HotAction[] actionBuf,
        HotChoiceSelection[] choiceOptBuf,
        int maxPairsPerStep,
        int maxPairsPerFight,
        ref int emittedForFight,
        StreamWriter writer,
        SplitStats stats)
    {
        int wPrePot = teacherPreEndPotions.ComputeNetPotionCostCenti(in teacherPreEndState, in hotPolicy);
        HotHash128 wPreHash = ComputeStateHash(in teacherPreEndState, teacherPreEndCold, packBuf);
        int[] wPreFeat = ExtractFeatureArray(in teacherPreEndState, teacherPreEndCold, rules, in evalLookup, wPrePot);
        long wPreEval = EvaluateHotScore(in teacherPreEndState, teacherPreEndCold, rules, in hotPolicy, in evalLookup, wPrePot, 10);

        HotHash128 wPostHash = default;
        int[]? wPostFeat = null;
        long wPostEval = 0;
        if (hasTeacherPostEnd)
        {
            int wPostPot = teacherPostEndPotions.ComputeNetPotionCostCenti(in teacherPostEndState, in hotPolicy);
            wPostHash = ComputeStateHash(in teacherPostEndState, teacherPostEndCold, packBuf);
            wPostFeat = ExtractFeatureArray(in teacherPostEndState, teacherPostEndCold, rules, in evalLookup, wPostPot);
            wPostEval = EvaluateHotScore(in teacherPostEndState, teacherPostEndCold, rules, in hotPolicy, in evalLookup, wPostPot, 11);
        }

        List<CandidateLoser> preLosers = [];
        List<CandidateLoser> postLosers = [];
        HashSet<HotHash128> seenPreHashes = [wPreHash];
        HashSet<HotHash128> seenPostHashes = [];
        if (hasTeacherPostEnd)
            seenPostHashes.Add(wPostHash);

        for (int i = 0; i < divergenceSeeds.Count; i++)
        {
            DivergenceSeed seed = divergenceSeeds[i];
            CombatHotState preState = seed.State;
            HotColdSidecar? preCold = seed.Cold;
            PotionTracker seedPotions = seed.Potions;
            int seedStep = seed.StepIdx;

            if (!seed.SkipGreedyCompletion)
            {
                // Greedily complete the alternative sibling's remaining Turn-t actions using current HotEvaluator.
                CompleteTurnGreedily(
                    ref preState, ref preCold, turn, rules, in hotPolicy, in evalLookup,
                    seedPotions.ComputeNetPotionCostCenti(in preState, in hotPolicy),
                    scratch, actionBuf, choiceOptBuf);
            }

            if (preState.TerminalOutcome != HotCombatOutcome.Ongoing || preState.PlayerTurnNumber != turn)
                continue;

            int sPrePot = seedPotions.ComputeNetPotionCostCenti(in preState, in hotPolicy);
            HotHash128 sPreHash = ComputeStateHash(in preState, preCold, packBuf);
            if (seenPreHashes.Add(sPreHash))
            {
                int[] sPreFeat = ExtractFeatureArray(in preState, preCold, rules, in evalLookup, sPrePot);
                if (!AreFeaturesIdentical(wPreFeat, sPreFeat))
                {
                    long sPreEval = EvaluateHotScore(in preState, preCold, rules, in hotPolicy, in evalLookup, sPrePot, seedStep + 1);
                    preLosers.Add(new CandidateLoser(sPreEval, sPreHash, sPreFeat));
                }
            }

            if (!hasTeacherPostEnd || wPostFeat == null)
                continue;

            CombatHotState postState = preState;
            HotColdSidecar? postCold = preCold;
            HotStatePacker.CanonicalizeForPack(ref postState, ref postCold);
            HotParallelExpander.AssignDeterministicCardUids(ref postState);

            if (!TryExecuteActionGreedyChoices(
                ref postState, ref postCold, rules, in hotPolicy, in evalLookup,
                sPrePot, seedStep + 2, scratch, HotAction.EndTurn(), choiceOptBuf))
            {
                continue;
            }

            if (postState.TerminalOutcome != HotCombatOutcome.Ongoing || postState.PlayerTurnNumber != turn + 1)
                continue;

            int sPostPot = seedPotions.ComputeNetPotionCostCenti(in postState, in hotPolicy);
            HotHash128 sPostHash = ComputeStateHash(in postState, postCold, packBuf);
            if (seenPostHashes.Add(sPostHash))
            {
                int[] sPostFeat = ExtractFeatureArray(in postState, postCold, rules, in evalLookup, sPostPot);
                if (!AreFeaturesIdentical(wPostFeat, sPostFeat))
                {
                    long sPostEval = EvaluateHotScore(in postState, postCold, rules, in hotPolicy, in evalLookup, sPostPot, seedStep + 2);
                    postLosers.Add(new CandidateLoser(sPostEval, sPostHash, sPostFeat));
                }
            }
        }

        // Emit top hard-negative turn_pre_end pairs
        if (preLosers.Count > 0 && emittedForFight < maxPairsPerFight)
        {
            preLosers.Sort(static (x, y) => y.EvalScore.CompareTo(x.EvalScore));
            int cap = Math.Min(maxPairsPerStep, Math.Min(preLosers.Count, maxPairsPerFight - emittedForFight));
            for (int k = 0; k < cap; k++)
            {
                CandidateLoser loser = preLosers[k];
                TeacherPairRecord rec = new()
                {
                    Split = scenario.Split,
                    FightId = scenario.FightId,
                    CharacterId = scenario.Setup.CharacterId,
                    EncounterId = scenario.Setup.EncounterId,
                    RoomType = scenario.RoomTypeName,
                    GameMode = scenario.Setup.GameMode.ToString(),
                    Turn = turn,
                    Step = -1,
                    PairKind = "turn_pre_end",
                    TeacherVCenti = outcome.EconomicValueCenti,
                    TeacherHpLoss = outcome.HpLoss,
                    TeacherTurns = outcome.Turns,
                    TeacherPotions = outcome.PotionsUsed,
                    WinnerEvalScore = wPreEval,
                    LoserEvalScore = loser.EvalScore,
                    Winner = wPreFeat,
                    Loser = loser.Features,
                };
                writer.WriteLine(JsonSerializer.Serialize(rec, CompactJsonOptions));
                stats.RecordPair(rec);
                emittedForFight++;
            }
        }

        // Emit top hard-negative turn_post_end pairs (start of Turn t+1)
        if (hasTeacherPostEnd && wPostFeat != null && postLosers.Count > 0 && emittedForFight < maxPairsPerFight)
        {
            postLosers.Sort(static (x, y) => y.EvalScore.CompareTo(x.EvalScore));
            int cap = Math.Min(maxPairsPerStep, Math.Min(postLosers.Count, maxPairsPerFight - emittedForFight));
            for (int k = 0; k < cap; k++)
            {
                CandidateLoser loser = postLosers[k];
                TeacherPairRecord rec = new()
                {
                    Split = scenario.Split,
                    FightId = scenario.FightId,
                    CharacterId = scenario.Setup.CharacterId,
                    EncounterId = scenario.Setup.EncounterId,
                    RoomType = scenario.RoomTypeName,
                    GameMode = scenario.Setup.GameMode.ToString(),
                    Turn = turn,
                    Step = -2,
                    PairKind = "turn_post_end",
                    TeacherVCenti = outcome.EconomicValueCenti,
                    TeacherHpLoss = outcome.HpLoss,
                    TeacherTurns = outcome.Turns,
                    TeacherPotions = outcome.PotionsUsed,
                    WinnerEvalScore = wPostEval,
                    LoserEvalScore = loser.EvalScore,
                    Winner = wPostFeat,
                    Loser = loser.Features,
                };
                writer.WriteLine(JsonSerializer.Serialize(rec, CompactJsonOptions));
                stats.RecordPair(rec);
                emittedForFight++;
            }
        }
    }

    private static void CompleteTurnGreedily(
        ref CombatHotState state,
        ref HotColdSidecar? cold,
        int targetTurn,
        HotRules rules,
        in HotSearchPolicy hotPolicy,
        in HotEvalLookup evalLookup,
        int netPotionCostCenti,
        HotExecScratch scratch,
        HotAction[] actionBuf,
        HotChoiceSelection[] choiceOptBuf)
    {
        const int MaxGreedySteps = 12;
        const int MaxCardActionsPerStep = 16;

        for (int step = 0; step < MaxGreedySteps; step++)
        {
            if (state.TerminalOutcome != HotCombatOutcome.Ongoing || state.PlayerTurnNumber != targetTurn)
                return;

            HotStatePacker.CanonicalizeForPack(ref state, ref cold);
            HotParallelExpander.AssignDeterministicCardUids(ref state);

            long currentScore = EvaluateHotScore(in state, cold, rules, in hotPolicy, in evalLookup, netPotionCostCenti, step);
            int legalCount = HotCombatEngine.EnumerateLegalActions(in state, cold, rules, actionBuf, includePotions: false);

            long bestScore = currentScore;
            bool foundImprovement = false;
            CombatHotState bestNext = default;
            HotColdSidecar? bestCold = null;
            int evaluated = 0;
            HashSet<(ushort CardDefId, byte Upgrade, sbyte Target)> dedupe = [];

            for (int i = 0; i < legalCount && evaluated < MaxCardActionsPerStep; i++)
            {
                HotAction act = actionBuf[i];
                if (act.Kind != HotActionKind.PlayCard)
                    continue;

                ref readonly HotCard card = ref state.Cards[act.SlotOrHandIdx];
                if (!dedupe.Add((card.CardDefId, card.UpgradeLevel, act.TargetEnemySlot)))
                    continue;
                evaluated++;

                CombatHotState cand = state;
                HotColdSidecar? candCold = cold;
                if (!TryExecuteActionGreedyChoices(
                    ref cand, ref candCold, rules, in hotPolicy, in evalLookup,
                    netPotionCostCenti, step + 1, scratch, act, choiceOptBuf))
                {
                    continue;
                }

                if (cand.TerminalOutcome == HotCombatOutcome.Defeat || cand.TerminalOutcome == HotCombatOutcome.EventDefeat)
                    continue;

                long candScore = EvaluateHotScore(in cand, candCold, rules, in hotPolicy, in evalLookup, netPotionCostCenti, step + 1);
                if (candScore > bestScore)
                {
                    bestScore = candScore;
                    bestNext = cand;
                    bestCold = candCold;
                    foundImprovement = true;
                }
            }

            if (!foundImprovement)
                return;

            state = bestNext;
            cold = bestCold;
        }
    }

    private static bool TryExecuteActionGreedyChoices(
        ref CombatHotState state,
        ref HotColdSidecar? cold,
        HotRules rules,
        in HotSearchPolicy hotPolicy,
        in HotEvalLookup evalLookup,
        int netPotionCostCenti,
        int actionCount,
        HotExecScratch scratch,
        HotAction action,
        HotChoiceSelection[] choiceOptBuf)
    {
        Span<HotChoiceSelection> chosen = stackalloc HotChoiceSelection[HotLimits.MaxChoicesPerAction];
        int chosenCount = 0;

        for (int depth = 0; depth <= HotLimits.MaxChoicesPerAction; depth++)
        {
            CombatHotState probe = state;
            HotColdSidecar? probeCold = cold;
            HotStepResult res = HotCombatEngine.Step(ref probe, ref probeCold, rules, scratch, action, chosen[..chosenCount]);
            if (!res.Executed && !res.NeedsChoice)
                return false;

            if (!res.NeedsChoice)
            {
                state = probe;
                cold = probeCold;
                return true;
            }

            if (chosenCount >= HotLimits.MaxChoicesPerAction)
                return false;

            int optCount = HotCombatEngine.EnumerateChoiceOptions(in probe, probeCold, rules, scratch, choiceOptBuf);
            if (optCount <= 0)
                return false;

            // Evaluate up to 4 candidate choice options and pick the highest-scoring one deterministically.
            int probeLimit = Math.Min(optCount, 4);
            int bestIdx = 0;
            long bestScore = long.MinValue;
            for (int o = 0; o < probeLimit; o++)
            {
                chosen[chosenCount] = choiceOptBuf[o];
                CombatHotState optState = state;
                HotColdSidecar? optCold = cold;
                HotStepResult optRes = HotCombatEngine.Step(
                    ref optState, ref optCold, rules, scratch, action, chosen[..(chosenCount + 1)]);
                if (!optRes.Executed && !optRes.NeedsChoice)
                    continue;
                long optScore = EvaluateHotScore(
                    in optState, optCold, rules, in hotPolicy, in evalLookup, netPotionCostCenti, actionCount);
                if (optScore > bestScore)
                {
                    bestScore = optScore;
                    bestIdx = o;
                }
            }

            chosen[chosenCount++] = choiceOptBuf[bestIdx];
        }

        return false;
    }

    private static bool TryReplayActionWithPlannedChoices(
        ref CombatHotState state,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotAction action,
        IReadOnlyList<PlanCardChoice> plannedChoices,
        HotChoiceSelection[] choiceOptBuf)
    {
        Span<HotChoiceSelection> chosen = stackalloc HotChoiceSelection[HotLimits.MaxChoicesPerAction];
        int chosenCount = 0;
        int promptCursor = 0;
        int tokenCursor = 0;
        ushort activePromptSourceDefId = 0;
        HotChoiceKind activePromptKind = HotChoiceKind.None;

        for (int depth = 0; depth <= HotLimits.MaxChoicesPerAction; depth++)
        {
            CombatHotState probe = state;
            HotColdSidecar? probeCold = cold;
            HotStepResult res = HotCombatEngine.Step(ref probe, ref probeCold, rules, scratch, action, chosen[..chosenCount]);
            if (!res.Executed && !res.NeedsChoice)
                return false;

            if (!res.NeedsChoice)
            {
                state = probe;
                cold = probeCold;
                return true;
            }

            if (chosenCount >= HotLimits.MaxChoicesPerAction)
                return false;

            int optCount = HotCombatEngine.EnumerateChoiceOptions(in probe, probeCold, rules, scratch, choiceOptBuf);
            if (optCount <= 0)
                return false;

            HotChoiceSelection selected = default;
            bool matched = false;

            // Check if we just finished all tokens of an optional multi-select prompt and need to emit None terminator.
            if (promptCursor < plannedChoices.Count
                && tokenCursor > 0
                && tokenCursor >= plannedChoices[promptCursor].Cards.Count)
            {
                if (scratch.PendingChoiceMin == 0
                    && scratch.PendingChoiceKind == activePromptKind
                    && scratch.PendingChoiceSourceCardDefId == activePromptSourceDefId
                    && TryFindNoneChoice(choiceOptBuf, optCount, out selected))
                {
                    matched = true;
                    promptCursor++;
                    tokenCursor = 0;
                    activePromptSourceDefId = 0;
                    activePromptKind = HotChoiceKind.None;
                }
                else
                {
                    promptCursor++;
                    tokenCursor = 0;
                    activePromptSourceDefId = 0;
                    activePromptKind = HotChoiceKind.None;
                }
            }

            if (!matched)
            {
                if (promptCursor < plannedChoices.Count)
                {
                    PlanCardChoice prompt = plannedChoices[promptCursor];
                    if (prompt.Cards.Count == 0)
                    {
                        if (!TryFindNoneChoice(choiceOptBuf, optCount, out selected))
                            return false;
                        matched = true;
                        promptCursor++;
                        tokenCursor = 0;
                    }
                    else
                    {
                        PlanCardToken token = prompt.Cards[tokenCursor];
                        if (TryMatchChoiceToken(in probe, probeCold, rules, scratch.PendingChoiceKind, choiceOptBuf, optCount, token, out selected))
                        {
                            matched = true;
                            activePromptKind = scratch.PendingChoiceKind;
                            activePromptSourceDefId = scratch.PendingChoiceSourceCardDefId;
                            tokenCursor++;
                        }
                        else if (optCount == 1)
                        {
                            // Implicit single-option selection (e.g. vanilla auto-pick without separate PlanCardChoice).
                            selected = choiceOptBuf[0];
                            matched = true;
                        }
                        else
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    if (scratch.PendingChoiceMin == 0 && TryFindNoneChoice(choiceOptBuf, optCount, out selected))
                        matched = true;
                    else if (optCount == 1)
                    {
                        selected = choiceOptBuf[0];
                        matched = true;
                    }
                    else
                        return false;
                }
            }

            if (!matched)
                return false;

            chosen[chosenCount++] = selected;
        }

        return false;
    }

    private static bool TryFindNoneChoice(
        HotChoiceSelection[] options,
        int optCount,
        out HotChoiceSelection noneChoice)
    {
        for (int i = 0; i < optCount; i++)
        {
            if (options[i].Kind == HotChoiceKind.None)
            {
                noneChoice = options[i];
                return true;
            }
        }
        noneChoice = default;
        return false;
    }

    private static bool TryMatchChoiceToken(
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules,
        HotChoiceKind pendingKind,
        HotChoiceSelection[] options,
        int optCount,
        PlanCardToken token,
        out HotChoiceSelection matched)
    {
        if (pendingKind == HotChoiceKind.GeneratedCard)
        {
            for (int i = 0; i < optCount; i++)
            {
                HotChoiceSelection opt = options[i];
                if (opt.Kind != HotChoiceKind.GeneratedCard || opt.CardDefId >= rules.Cards.Length)
                    continue;
                if (string.Equals(rules.Cards[opt.CardDefId].Entry, token.CardId, StringComparison.Ordinal))
                {
                    matched = opt;
                    return true;
                }
            }
            matched = default;
            return false;
        }

        HotPileKind pile = pendingKind switch
        {
            HotChoiceKind.HandCard => HotPileKind.Hand,
            HotChoiceKind.DrawPile => HotPileKind.Draw,
            HotChoiceKind.DiscardPile => HotPileKind.Discard,
            HotChoiceKind.ExhaustPile => HotPileKind.Exhaust,
            _ => HotPileKind.Hand,
        };

        // Pass 1: exact StateKey match
        if (!string.IsNullOrEmpty(token.StateKey))
        {
            for (int i = 0; i < optCount; i++)
            {
                HotChoiceSelection opt = options[i];
                if (opt.Kind == HotChoiceKind.None)
                    continue;
                int cardIdx = HotCombatEngine.FindCardIndexByUid(in state, pile, opt.CardUidOrIndex);
                if (cardIdx < 0)
                    cardIdx = HotCombatEngine.FindCardIndexByUidAnyPile(in state, opt.CardUidOrIndex);
                if (cardIdx < 0)
                    continue;
                string key = HotContinuationStamp.FormatChoiceCardKey(in state.Cards[cardIdx], cold, rules);
                if (string.Equals(key, token.StateKey, StringComparison.Ordinal))
                {
                    matched = opt;
                    return true;
                }
            }
        }

        // Pass 2: CardId + UpgradeLevel match
        for (int i = 0; i < optCount; i++)
        {
            HotChoiceSelection opt = options[i];
            if (opt.Kind == HotChoiceKind.None)
                continue;
            int cardIdx = HotCombatEngine.FindCardIndexByUid(in state, pile, opt.CardUidOrIndex);
            if (cardIdx < 0)
                cardIdx = HotCombatEngine.FindCardIndexByUidAnyPile(in state, opt.CardUidOrIndex);
            if (cardIdx < 0)
                continue;
            ref readonly HotCard c = ref state.Cards[cardIdx];
            if (c.CardDefId < rules.Cards.Length
                && string.Equals(rules.Cards[c.CardDefId].Entry, token.CardId, StringComparison.Ordinal)
                && c.UpgradeLevel == token.UpgradeLevel)
            {
                matched = opt;
                return true;
            }
        }

        matched = default;
        return false;
    }

    private static bool TryResolvePlanActionToHot(
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules,
        PlanAction planAction,
        out HotAction hotAction)
    {
        switch (planAction.Kind)
        {
            case PlanActionKind.EndTurn:
                hotAction = HotAction.EndTurn();
                return true;

            case PlanActionKind.UsePotion:
            {
                int targetSlot = ResolveTargetEnemySlot(in state, planAction.TargetCombatId, planAction.TargetIndex);
                if (!HotCombatEngine.CanUsePotion(in state, rules, planAction.PotionSlot, targetSlot))
                {
                    hotAction = default;
                    return false;
                }
                hotAction = HotAction.UsePotion(planAction.PotionSlot, targetSlot);
                return true;
            }

            case PlanActionKind.PlayCard:
            {
                int handIdx = FindHandCardIndex(in state, cold, rules, planAction);
                if (handIdx < 0)
                {
                    hotAction = default;
                    return false;
                }
                int targetSlot = ResolveTargetEnemySlot(in state, planAction.TargetCombatId, planAction.TargetIndex);
                if (!HotCombatEngine.CanPlayCard(in state, cold, rules, handIdx, targetSlot))
                {
                    hotAction = default;
                    return false;
                }
                hotAction = HotAction.PlayCard(handIdx, targetSlot);
                return true;
            }

            default:
                hotAction = default;
                return false;
        }
    }

    private static int FindHandCardIndex(
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules,
        PlanAction planAction)
    {
        int handCount = state.HandCount;

        if (!string.IsNullOrEmpty(planAction.CardStateKey))
        {
            int stateOcc = 0;
            for (int i = 0; i < handCount; i++)
            {
                string key = HotContinuationStamp.FormatChoiceCardKey(in state.Cards[i], cold, rules);
                if (string.Equals(key, planAction.CardStateKey, StringComparison.Ordinal))
                {
                    if (stateOcc == planAction.CardStateOccurrence)
                        return i;
                    stateOcc++;
                }
            }
        }

        int cardOcc = 0;
        for (int i = 0; i < handCount; i++)
        {
            ushort defId = state.Cards[i].CardDefId;
            if (defId < rules.Cards.Length
                && string.Equals(rules.Cards[defId].Entry, planAction.CardId, StringComparison.Ordinal))
            {
                if (cardOcc == planAction.CardOccurrence)
                    return i;
                cardOcc++;
            }
        }

        return -1;
    }

    private static int ResolveTargetEnemySlot(
        in CombatHotState state,
        uint? targetCombatId,
        int targetIndex)
    {
        if (targetCombatId is uint cid)
        {
            int rosterCount = Math.Min((int)state.RosterCount, HotLimits.MaxEnemies);
            for (int r = 0; r < rosterCount; r++)
            {
                byte slot = state.ActiveEnemyRoster[r];
                if (state.Enemies[slot].Vital.CombatId == cid)
                    return slot;
            }
            int enemyCount = Math.Min((int)state.EnemyCount, HotLimits.MaxEnemies);
            for (int e = 0; e < enemyCount; e++)
            {
                if (state.Enemies[e].Vital.CombatId == cid)
                    return e;
            }
        }

        if (targetIndex >= 0 && targetIndex < state.RosterCount)
            return state.ActiveEnemyRoster[targetIndex];

        return -1;
    }

    private static List<PlanCardChoice> CollectPlannedChoices(PlanAction action)
    {
        List<PlanCardChoice> list = [.. action.GetActionChoicesInExecutionOrder()];
        if (action.TurnStartChoices is { Count: > 0 } turnStart)
            list.AddRange(turnStart);
        return list;
    }

    private static PotionTracker CreateRootPotionTracker(in CombatHotState rootState)
    {
        ushort occupied = 0;
        int slotCount = Math.Min((int)rootState.PotionSlotCount, HotLimits.MaxPotions);
        for (int s = 0; s < slotCount; s++)
        {
            if (rootState.Potions[s] != 0)
                occupied |= (ushort)(1 << s);
        }
        return new PotionTracker { RootOccupiedMask = occupied };
    }

    private static int[] ExtractFeatureArray(
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules,
        in HotEvalLookup lookup,
        int netPotionCostCenti)
    {
        int[] features = new int[HotCalibrationFeatures.FeatureCount];
        HotCalibrationFeatures.Extract(in state, cold, rules, in lookup, netPotionCostCenti, features);
        return features;
    }

    private static long EvaluateHotScore(
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules,
        in HotSearchPolicy hotPolicy,
        in HotEvalLookup lookup,
        int netPotionCostCenti,
        int actionCount)
    {
        HotFastSummary summary = HotEvaluator.ExtractStage1(in state, in hotPolicy);
        HotEvaluator.EvaluateState(
            in state, cold, rules, in hotPolicy, in lookup, in summary,
            netPotionCostCenti, actionCount, HotEvalMode.Balanced, out HotEvalFeatures eval);
        return eval.Score;
    }

    private static HotHash128 ComputeStateHash(
        in CombatHotState state,
        HotColdSidecar? cold,
        byte[] packBuf)
    {
        CombatHotState copy = state;
        HotColdSidecar? coldCopy = cold;
        int len = HotStatePacker.Pack(ref copy, ref coldCopy, packBuf, canonicalize: true);
        return HotHash.ComputeHash(packBuf.AsSpan(0, len), coldCopy);
    }

    private static bool AreFeaturesIdentical(int[] a, int[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    private static List<ExportScenario> BuildScenarios(string split, int fights, int seed, string sourceMode)
    {
        if (fights <= 0)
            return [];

        if (sourceMode == "fuzz")
            return BuildFromFuzzGenerator(split, fights, seed);

        if (sourceMode == "mixed")
        {
            int fuzzCount = Math.Max(1, fights / 6);
            int realisticCount = Math.Max(0, fights - fuzzCount);
            List<ExportScenario> combined = BuildRealisticStratifiedScenarios(split, realisticCount, seed);
            combined.AddRange(BuildFromFuzzGenerator(split, fuzzCount, seed ^ 0x2468_ACE1, startIndex: realisticCount));
            return combined;
        }

        return BuildRealisticStratifiedScenarios(split, fights, seed);
    }

    private static List<ExportScenario> BuildFromFuzzGenerator(
        string split,
        int fights,
        int seed,
        int startIndex = 0)
    {
        List<FuzzScenarioSpec> specs = FuzzScenarioGenerator.Generate(fights, seed, phase: "sweep", maxTurns: 8);
        Dictionary<string, RoomType> roomTypeByEncounter = [];
        foreach (EncounterModel e in ModelDb.AllEncounters)
            roomTypeByEncounter[e.Id.Entry] = e.RoomType;

        List<ExportScenario> result = new(specs.Count);
        for (int i = 0; i < specs.Count; i++)
        {
            FuzzScenarioSpec spec = specs[i];
            RoomType rt = roomTypeByEncounter.TryGetValue(spec.Setup.EncounterId, out RoomType found)
                ? found
                : RoomType.Monster;
            string roomName = rt switch
            {
                RoomType.Boss => "Boss",
                RoomType.Elite => "Elite",
                _ => "Normal",
            };
            result.Add(new ExportScenario(
                Split: split,
                Index: startIndex + i,
                FightId: $"{split}_fuzz_{startIndex + i:D4}_{spec.Setup.CharacterId.ToLowerInvariant()}_{roomName.ToLowerInvariant()}_{spec.Setup.GameMode.ToString().ToLowerInvariant()}",
                RoomTypeName: roomName,
                Setup: spec.Setup));
        }
        return result;
    }

    /// <summary>
    /// Unbiased stratified realistic combat generator covering all 6 characters × Normal/Elite/Boss × Standard/Dread/Blitz
    /// using character-native card/relic/potion pools (mirroring <see cref="DeploymentSoakRunner"/> and extending to Bosses).
    /// </summary>
    private static List<ExportScenario> BuildRealisticStratifiedScenarios(string split, int fights, int seed)
    {
        Random rng = new(seed);
        string seedPrefix = split == "train" ? "TRN" : "VAL";

        Dictionary<string, CharacterModel> charMap = ModelDb.AllCharacters
            .ToDictionary(static c => c.Id.Entry, StringComparer.Ordinal);

        CardModel[] colorlessPool = ModelDb.CardPool<ColorlessCardPool>().AllCards
            .Where(c => !HotCoverageAuditor.IsMockOrDeprecated(c)
                && GeneratedCombatScenario.IsSingleplayerCard(c)
                && c.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare)
            .DistinctBy(static c => c.Id)
            .OrderBy(static c => c.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        RelicModel[] sharedRelics = ModelDb.RelicPool<SharedRelicPool>().AllRelics
            .Where(r => !HotCoverageAuditor.IsMockOrDeprecated(r)
                && GeneratedCombatScenario.IsSingleplayerRelic(r)
                && r.Rarity is RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare or RelicRarity.Shop)
            .DistinctBy(static r => r.Id)
            .OrderBy(static r => r.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        PotionModel[] sharedPotions = ModelDb.PotionPool<SharedPotionPool>().AllPotions
            .Where(p => !HotCoverageAuditor.IsMockOrDeprecated(p))
            .DistinctBy(static p => p.Id)
            .OrderBy(static p => p.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        List<(int ActIndex, EncounterModel Encounter)> normalEncounters = [];
        List<(int ActIndex, EncounterModel Encounter)> eliteEncounters = [];
        List<(int ActIndex, EncounterModel Encounter)> bossEncounters = [];

        ActModel[] acts = ModelDb.Acts.ToArray();
        for (int a = 0; a < acts.Length; a++)
        {
            int actIdx = Math.Min(a, 2);
            foreach (EncounterModel e in acts[a].AllEncounters)
            {
                if (HotCoverageAuditor.IsMockOrDeprecated(e))
                    continue;
                if (e.RoomType == RoomType.Monster)
                    normalEncounters.Add((actIdx, e));
                else if (e.RoomType == RoomType.Elite)
                    eliteEncounters.Add((actIdx, e));
                else if (e.RoomType == RoomType.Boss)
                    bossEncounters.Add((actIdx, e));
            }
        }

        normalEncounters = normalEncounters
            .DistinctBy(static x => x.Encounter.Id)
            .OrderBy(static x => x.Encounter.Id.Entry, StringComparer.Ordinal)
            .ToList();
        eliteEncounters = eliteEncounters
            .DistinctBy(static x => x.Encounter.Id)
            .OrderBy(static x => x.Encounter.Id.Entry, StringComparer.Ordinal)
            .ToList();
        bossEncounters = bossEncounters
            .DistinctBy(static x => x.Encounter.Id)
            .OrderBy(static x => x.Encounter.Id.Entry, StringComparer.Ordinal)
            .ToList();

        DominionModel[] dominions = ModelDb.All.OfType<DominionModel>()
            .Where(d => !HotCoverageAuditor.IsMockOrDeprecated(d))
            .OrderBy(static d => d.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        EnchantmentModel[] enchantments = ModelDb.DebugEnchantments
            .Where(e => !HotCoverageAuditor.IsMockOrDeprecated(e)
                && e.Id.Entry is "ADROIT" or "SHARP" or "NIMBLE" or "SWIFT" or "VIGOROUS" or "STEADY" or "SPIRAL" or "INSTINCT" or "TEZCATARAS_EMBER" or "PERFECT_FIT")
            .OrderBy(static e => e.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        List<ExportScenario> result = new(fights);
        for (int i = 0; i < fights; i++)
        {
            string charId = AllCharacters[i % AllCharacters.Length];
            CharacterModel ch = charMap[charId];

            // Stratify room type: ~50% Normal, ~30% Elite, ~20% Boss across every character.
            int cycleBucket = (i / AllCharacters.Length) % 10;
            (int actIdx, EncounterModel enc, string roomName) = cycleBucket switch
            {
                >= 8 when bossEncounters.Count > 0 => PickEncounter(bossEncounters, rng, "Boss"),
                >= 5 when eliteEncounters.Count > 0 => PickEncounter(eliteEncounters, rng, "Elite"),
                _ => PickEncounter(normalEncounters, rng, "Normal"),
            };

            // Stratify mode across Standard (50%), Dread (33%), Blitz (17%).
            GameMode mode = ((i + cycleBucket) % 6) switch
            {
                0 or 1 or 2 => GameMode.Standard,
                3 or 4 => GameMode.Dread,
                _ => GameMode.Blitz,
            };
            string? dominionId = mode == GameMode.Dread && dominions.Length > 0
                ? dominions[(i / 2) % dominions.Length].Id.Entry
                : null;

            CardModel[] charCards = ch.CardPool.AllCards
                .Where(c => !HotCoverageAuditor.IsMockOrDeprecated(c)
                    && GeneratedCombatScenario.IsSingleplayerCard(c)
                    && c.Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare
                    && (mode != GameMode.Blitz || c.IsAllowedInBlitz))
                .DistinctBy(static c => c.Id)
                .OrderBy(static c => c.Id.Entry, StringComparer.Ordinal)
                .ToArray();

            int baseCardCount = roomName switch
            {
                "Boss" => 9 + actIdx * 2,
                "Elite" => 7 + actIdx * 2,
                _ => 5 + actIdx * 2,
            };
            int addCardCount = baseCardCount + rng.Next(4);
            List<CardInjectionSpec> addedCards = new(addCardCount + 1);
            for (int c = 0; c < addCardCount && charCards.Length > 0; c++)
            {
                CardModel pick = charCards[rng.Next(charCards.Length)];
                int upg = rng.Next(3) == 0 && pick.MaxUpgradeLevel > 0 ? 1 : 0;
                string? enchId = null;
                if (c == 0 && enchantments.Length > 0 && rng.Next(2) == 0)
                {
                    EnchantmentModel cand = enchantments[rng.Next(enchantments.Length)];
                    if (RealCombatDriver.CanSafelyEnchant(cand, pick))
                        enchId = cand.Id.Entry;
                }
                addedCards.Add(new CardInjectionSpec(pick.Id.Entry, upg, enchId, 2));
            }
            if (colorlessPool.Length > 0 && rng.Next(2) == 0)
            {
                CardModel cl = colorlessPool[rng.Next(colorlessPool.Length)];
                if (mode != GameMode.Blitz || cl.IsAllowedInBlitz)
                    addedCards.Add(new CardInjectionSpec(cl.Id.Entry, rng.Next(2)));
            }

            RelicModel[] relicPool = ch.RelicPool.AllRelics
                .Concat(sharedRelics)
                .Where(r => !HotCoverageAuditor.IsMockOrDeprecated(r)
                    && GeneratedCombatScenario.IsSingleplayerRelic(r)
                    && (mode != GameMode.Blitz || !RelicLists.notAllowedInBlitz.Contains(r.Id)))
                .DistinctBy(static r => r.Id)
                .OrderBy(static r => r.Id.Entry, StringComparer.Ordinal)
                .ToArray();

            int relicBase = roomName == "Boss" ? 4 : roomName == "Elite" ? 3 : 2;
            int relicCount = relicBase + actIdx + rng.Next(3);
            HashSet<string> chosenRelics = new(StringComparer.Ordinal);
            for (int r = 0; r < relicCount * 2 && chosenRelics.Count < relicCount && relicPool.Length > 0; r++)
                chosenRelics.Add(relicPool[rng.Next(relicPool.Length)].Id.Entry);

            PotionModel[] potionPool = ch.PotionPool.AllPotions
                .Concat(sharedPotions)
                .Where(p => !HotCoverageAuditor.IsMockOrDeprecated(p))
                .DistinctBy(static p => p.Id)
                .OrderBy(static p => p.Id.Entry, StringComparer.Ordinal)
                .ToArray();

            int potionCount = 1 + rng.Next(3);
            List<string> chosenPotions = new(potionCount);
            for (int p = 0; p < potionCount && potionPool.Length > 0; p++)
                chosenPotions.Add(potionPool[rng.Next(potionPool.Length)].Id.Entry);

            int ascension = mode == GameMode.Blitz ? (i % 6) : ((i % 5 == 0) ? 10 : 0);
            int maxHp = (roomName == "Boss" ? 90 : roomName == "Elite" ? 85 : 80) + actIdx * 10;
            int curHp = maxHp - rng.Next(15);

            string fightId = $"{split}_{i:D4}_{charId.ToLowerInvariant()}_{roomName.ToLowerInvariant()}_{mode.ToString().ToLowerInvariant()}_{enc.Id.Entry.ToLowerInvariant()}";
            result.Add(new ExportScenario(
                Split: split,
                Index: i,
                FightId: fightId,
                RoomTypeName: roomName,
                Setup: new CombatSetupSpec
                {
                    CharacterId = charId,
                    EncounterId = enc.Id.Entry,
                    Seed = $"{seedPrefix}{seed:X4}_{i:D4}",
                    GameMode = mode,
                    DominionId = dominionId,
                    Ascension = ascension,
                    ActIndex = actIdx,
                    PlayerMaxHp = maxHp,
                    PlayerHp = curHp,
                    Cards = addedCards.ToArray(),
                    Relics = chosenRelics.ToArray(),
                    PotionSlotCount = 3,
                    Potions = chosenPotions.ToArray(),
                    ExtraEnergy = roomName == "Boss" && actIdx >= 2 ? 1 : 0,
                    ExtraStars = charId == "REGENT" && roomName == "Boss" ? 2 : 0,
                }));
        }

        return result;
    }

    private static (int ActIndex, EncounterModel Encounter, string RoomName) PickEncounter(
        List<(int ActIndex, EncounterModel Encounter)> pool,
        Random rng,
        string roomName)
    {
        (int actIdx, EncounterModel enc) = pool[rng.Next(pool.Count)];
        return (actIdx, enc, roomName);
    }

    private static object SummarizeSplit(SplitStats s) => new
    {
        total_fights = s.TotalFights,
        teacher_wins = s.TeacherWins,
        teacher_defeats = s.TeacherDefeats,
        hard_errors = s.HardErrors,
        replay_skipped = s.ReplaySkipped,
        pairs_exported = s.PairsExported,
        avg_teacher_solve_ms = s.TotalFights > 0 ? Math.Round(s.TotalTeacherSolveMs / s.TotalFights, 1) : 0.0,
        avg_teacher_expanded_nodes = s.TotalFights > 0 ? Math.Round((double)s.TotalTeacherExpandedNodes / s.TotalFights, 1) : 0.0,
        pairs_by_kind = s.PairsByKind,
        pairs_by_character = s.PairsByCharacter,
        pairs_by_room_type = s.PairsByRoomType,
        pairs_by_turn = s.PairsByTurn,
    };

    private static Exception Unwrap(Exception ex)
    {
        while (ex is System.Reflection.TargetInvocationException or AggregateException && ex.InnerException != null)
            ex = ex.InnerException;
        return ex;
    }

    private static string ResolveRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
}
