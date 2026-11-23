using System.Globalization;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver.HotKernel;

/// <summary>
/// Serializes a <see cref="CombatHotState"/> + <see cref="HotColdSidecar"/> + <see cref="HotRules"/>
/// into the exact <see cref="ContinuationStamp"/> <c>StateText</c> grammar for Gate A/B 0-diff verification.
/// </summary>
internal static class HotContinuationStamp
{
    public static string CaptureFromHot(
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules)
    {
        StringBuilder text = new(1024);

        // 1. Header scalars
        text.Append("turn=").Append(state.PlayerTurnNumber)
            .Append(";hp=").Append(state.Player.CurrentHp)
            .Append(";max_hp=").Append(state.Player.MaxHp)
            .Append(";block=").Append(state.Player.Block)
            .Append(";energy=").Append(state.Energy)
            .Append(";stars=").Append(state.Stars)
            .Append(";gold=").Append(state.Gold);

        // 2. Osty
        if ((state.CombatFlags & HotCombatFlags.HasOsty) != 0)
        {
            text.Append(";osty=")
                .Append(state.Osty.CombatId)
                .Append('/').Append(state.Osty.CurrentHp)
                .Append('/').Append(state.Osty.MaxHp);
        }
        else
        {
            text.Append(";osty=").Append(uint.MaxValue).Append("/0/0");
        }

        // 3. Pets (non-Osty)
        text.Append(";pets=");
        bool hasWuppy = (state.CombatFlags & HotCombatFlags.HasWuppy) != 0;
        bool emittedWuppy = false;
        for (int i = 0; i < rules.RootPets.Length; i++)
        {
            HotRootPetSnapshot pet = rules.RootPets[i];
            bool useHotWuppy = pet.IsWuppySlot && hasWuppy;
            if (pet.IsWuppySlot)
                emittedWuppy = true;
            uint petCid = useHotWuppy && state.Wuppy.CombatId != 0 && state.Wuppy.CombatId != uint.MaxValue
                ? state.Wuppy.CombatId
                : pet.CombatId;
            int hp = useHotWuppy ? state.Wuppy.CurrentHp : pet.CurrentHp;
            int maxHp = useHotWuppy ? state.Wuppy.MaxHp : pet.MaxHp;
            int blk = useHotWuppy ? state.Wuppy.Block : pet.Block;
            text.Append(petCid).Append(':').Append(pet.MonsterEntry).Append(':')
                .Append(hp).Append('/').Append(maxHp).Append('/').Append(blk).Append(',');
        }
        if (hasWuppy && !emittedWuppy)
        {
            text.Append(state.Wuppy.CombatId).Append(":WUPPY:")
                .Append(state.Wuppy.CurrentHp).Append('/')
                .Append(state.Wuppy.MaxHp).Append('/')
                .Append(state.Wuppy.Block).Append(',');
        }

        // Resolve active enemy roster ordering & slot names (matching live CombatState.SortEnemiesBySlotName)
        int rosterCount = Math.Min((int)state.RosterCount, HotLimits.MaxEnemies);
        Span<byte> orderedRoster = stackalloc byte[HotLimits.MaxEnemies];
        string[]? resolvedSlotNames = ResolveActiveEnemyRosterAndSlotNames(
            in state,
            rules,
            orderedRoster[..rosterCount]);

        // 4. Enemies (;E{index}=...)
        for (int idx = 0; idx < rosterCount; idx++)
        {
            byte enemySlot = orderedRoster[idx];
            ref readonly HotEnemy enemy = ref state.Enemies[enemySlot];
            string monsterEntry = enemy.MonsterDefId > 0 && enemy.MonsterDefId < rules.Monsters.Length
                ? rules.Monsters[enemy.MonsterDefId].Entry
                : (enemySlot < rules.EnemyMonsterEntries.Length
                    ? rules.EnemyMonsterEntries[enemySlot]
                    : "null");
            string slotName = resolvedSlotNames != null
                ? resolvedSlotNames[enemySlot]
                : (enemySlot < rules.EnemySlotNames.Length ? rules.EnemySlotNames[enemySlot] : "-");
            string moveId = ResolveMoveId(rules, enemy.MonsterDefId, enemy.CurrentMoveNode);

            text.Append(";E").Append(idx).Append('=')
                .Append(enemy.Vital.CombatId).Append('/')
                .Append(monsterEntry).Append('/')
                .Append(slotName).Append('/')
                .Append(enemy.Vital.CurrentHp).Append('/')
                .Append(enemy.Vital.MaxHp).Append('/')
                .Append(enemy.Vital.Block).Append('/')
                .Append(moveId);
        }

        // 5. Monster AI logs (;AI{index}=...)
        for (int idx = 0; idx < rosterCount; idx++)
        {
            byte enemySlot = orderedRoster[idx];
            ref readonly HotEnemy enemy = ref state.Enemies[enemySlot];
            text.Append(";AI").Append(idx).Append('=');
            AppendMonsterAiLog(text, in enemy, enemySlot, cold, rules);
        }

        // 6. Monster state & death phase (;MS{index}=...)
        for (int idx = 0; idx < rosterCount; idx++)
        {
            byte enemySlot = orderedRoster[idx];
            ref readonly HotEnemy enemy = ref state.Enemies[enemySlot];
            text.Append(";MS").Append(idx).Append('=');
            AppendMonsterState(text, in enemy, rules);
            text.Append('/').Append((int)enemy.DeathPhase);
        }

        // 7. Card piles (;H=, ;D=, ;C=, ;X=) — Play pile is intentionally excluded matching ContinuationStamp
        AppendPile(text, in state, cold, rules, 'H', 0, state.HandCount);
        AppendPile(text, in state, cold, rules, 'D', state.DrawStart, state.DrawCount);
        AppendPile(text, in state, cold, rules, 'C', state.DiscardStart, state.DiscardCount);
        AppendPile(text, in state, cold, rules, 'X', state.ExhaustStart, state.ExhaustCount);

        // 8. Turn card history (;Y=, ;FlameHp=, ;AttackStarts=)
        text.Append(";Y=")
            .Append(state.Counters[(int)HotCounterSlot.StatusCardsDrawnThisTurn]).Append('/')
            .Append(state.Counters[(int)HotCounterSlot.ZeroCostAttackStartsThisTurn]).Append('/')
            .Append(state.Counters[(int)HotCounterSlot.CardPlayStartsThisTurn]).Append('/')
            .Append(state.Counters[(int)HotCounterSlot.AttackSkillStartsThisTurn]);
        text.Append(";FlameHp=").Append(state.BrightestFlameMaxHpSpent);
        text.Append(";AttackStarts=").Append(state.Counters[(int)HotCounterSlot.AttackPlayStartsThisTurn]);

        // 9. Orbs (;O=) — compute live Focus-adjusted PassiveVal/EvokeVal matching 0.113.1 OrbModel properties
        text.Append(";O=").Append(state.OrbCapacity).Append(':');
        for (int i = 0; i < state.OrbCount; i++)
        {
            HotOrb orb = state.Orbs[i];
            text.Append(rules.Orbs[orb.OrbTypeId].Entry).Append('[')
                .Append(HotCombatEngine.GetOrbPassiveValue(in state, rules, in orb)).Append('/')
                .Append(HotCombatEngine.GetOrbEvokeValue(in state, rules, in orb)).Append("],");
        }

        // 10. Potions (;potions=)
        text.Append(";potions=");
        for (int slot = 0; slot < state.PotionSlotCount; slot++)
        {
            if (slot > 0)
                text.Append(',');
            ushort potionDefId = state.Potions[slot];
            text.Append(potionDefId == 0 ? "-" : rules.Potions[potionDefId].Entry);
        }

        // 11. Stateful relics (;relicCounters=)
        text.Append(";relicCounters=");
        for (int i = 0; i < rules.StatefulRelicOrder.Length; i++)
        {
            if (i > 0)
                text.Append(',');
            HotActiveRelic ar = rules.ActiveRelics[rules.StatefulRelicOrder[i]];
            HotRelicDef def = rules.Relics[ar.RelicDefId];
            HotRelicState rs = state.RelicStates[ar.StateSlot];
            text.Append(def.Entry).Append('/');
            if (def.SpecialKind == HotRelicSpecialKind.RainbowRing)
            {
                int activations = (rs.PreviousOrAux >> 16) & 0xFFFF;
                text.Append(activations).Append("/0");
            }
            else
            {
                text.Append(rs.Current).Append('/').Append(rs.PreviousOrAux);
            }
        }

        // 12. Tracked mirror relics (;mirrorRelics=)
        text.Append(";mirrorRelics=");
        for (int i = 0; i < rules.TrackedRelicOrder.Length; i++)
        {
            if (i > 0)
                text.Append(',');
            HotActiveRelic ar = rules.ActiveRelics[rules.TrackedRelicOrder[i]];
            HotRelicDef def = rules.Relics[ar.RelicDefId];
            HotRelicState rs = state.RelicStates[ar.StateSlot];
            text.Append(def.Entry).Append('/');
            AppendTrackedRelicValue(text, def.SpecialKind, rs);
        }

        // 13. Powers (;P=) grouped in exact CombatState.Creatures order (Allies then active Enemies)
        Span<byte> allyOwnerSlots = stackalloc byte[HotLimits.MaxEnemies + 4];
        int allyOwnerCount = BuildActiveAllyOwnerSlots(in state, rules, allyOwnerSlots);
        text.Append(";P=");
        for (int i = 0; i < allyOwnerCount; i++)
        {
            byte allySlot = allyOwnerSlots[i];
            AppendPowersForOwner(text, in state, cold, rules, allySlot, ResolveAllyCombatId(in state, rules, allySlot));
        }
        for (int idx = 0; idx < rosterCount; idx++)
        {
            byte enemySlot = orderedRoster[idx];
            uint enemyCid = state.Enemies[enemySlot].Vital.CombatId;
            AppendPowersForOwner(text, in state, cold, rules, ownerSlot: (byte)(3 + enemySlot), ownerCombatId: enemyCid);
        }

        // 14. Melodies (;melodies=)
        text.Append(";melodies=")
            .Append(state.Fool.MelodyStarted[0]).Append(',')
            .Append(state.Fool.MelodyStarted[1]).Append(',')
            .Append(state.Fool.MelodyStarted[2]).Append('/')
            .Append(state.Fool.MelodyFinished[0]).Append(',')
            .Append(state.Fool.MelodyFinished[1]).Append(',')
            .Append(state.Fool.MelodyFinished[2]).Append('/')
            .Append(state.Fool.MelodyCardsFinished);

        // 15. ShowOff (;showOff=)
        text.Append(";showOff=");
        for (int i = 0; i < state.PowerCount; i++)
        {
            ref readonly HotPower p = ref state.Powers[i];
            if (p.OwnerSlot != 0 || p.Amount == 0 || rules.Powers[p.PowerDefId].SpecialKind != HotPowerSpecialKind.ShowOff)
                continue;
            if (p.AuxCardUid == 0)
            {
                text.Append("null/");
            }
            else
            {
                int cardIdx = FindCardIndexByUid(in state, p.AuxCardUid);
                text.Append(cardIdx).Append(':');
                if (cardIdx >= 0)
                    AppendChoiceCardKey(text, in state.Cards[cardIdx], cold, rules);
                text.Append('/');
            }
        }

        // 16. Fool arrows, impaled, fool_powers
        text.Append(";arrows=");
        for (int i = 0; i < state.ArrowCount; i++)
        {
            ushort packed = state.Fool.Arrows[i];
            int status = packed & 0xF;
            int destroy = (packed & 0x10) != 0 ? 1 : 0;
            text.Append(status).Append(':').Append(destroy).Append(',');
        }

        text.Append(";impaled=");
        AppendImpaledPowers(text, in state, rules, allyOwnerSlots[..allyOwnerCount], orderedRoster[..rosterCount]);

        text.Append(";fool_powers=");
        for (int i = 0; i < state.PowerCount; i++)
        {
            ref readonly HotPower p = ref state.Powers[i];
            if (p.OwnerSlot != 0 || p.Amount <= 0)
                continue;
            switch (rules.Powers[p.PowerDefId].SpecialKind)
            {
                case HotPowerSpecialKind.Aim:
                    text.Append("Aim:");
                    if ((p.Flags & HotPowerFlags.BoolAux) == 0)
                        text.Append("ready/");
                    else if (p.AuxCardUid == 0)
                        text.Append("latched:null/");
                    else
                        text.Append("latched:").Append(FindCardIndexByUid(in state, p.AuxCardUid)).Append('/');
                    break;
                case HotPowerSpecialKind.Encore:
                    text.Append("Encore:").Append(p.Aux0).Append('/');
                    break;
                case HotPowerSpecialKind.Yah:
                    text.Append("Yah:").Append((p.Flags & HotPowerFlags.BoolAux) != 0 ? 1 : 0).Append('/');
                    break;
                case HotPowerSpecialKind.Lullaby:
                    text.Append("Lullaby:").Append((p.Flags & HotPowerFlags.BoolAux) != 0 ? 1 : 0).Append('/');
                    break;
            }
        }

        // 17. RNG (;R= and ;non_combat_generation=)
        text.Append(";R=");
        for (int r = 0; r < 9; r++)
        {
            if (r > 0)
                text.Append('/');
            AppendRngState(text, in state.Rng[r]);
        }
        text.Append(";non_combat_generation=");
        AppendRngState(text, in state.Rng[(int)HotRngStream.NonCombatGeneration]);

        // 18. Max hand size, combat rules, gold forfeit
        text.Append(";max_hand_size=").Append(state.MaxHandSize);
        rules.RulesSnapshot.Append(text);
        if ((state.CombatFlags & HotCombatFlags.EncounterGoldForfeited) != 0)
            text.Append(";encounter_gold_forfeited=1");

        return text.ToString();
    }

    /// <summary>
    /// Strips mod-only stamp sections (<c>model_state</c>, <c>onplay_configuration</c>,
    /// <c>hook_projection_patches</c>, <c>loadout_summon_powers</c>) from a live or predicted
    /// <see cref="ContinuationStamp.StateText"/> so Gate A/B compares pure vanilla state parity.
    /// </summary>
    public static string StripModStampSections(string stateText)
    {
        if (string.IsNullOrEmpty(stateText))
            return stateText;

        // Fast check: if none of the 4 mod markers exist, return verbatim without allocating.
        if (!stateText.Contains(";model_state=", StringComparison.Ordinal)
            && !stateText.Contains(";onplay_configuration=", StringComparison.Ordinal)
            && !stateText.Contains(";hook_projection_patches=", StringComparison.Ordinal)
            && !stateText.Contains(";loadout_summon_powers=", StringComparison.Ordinal))
        {
            return stateText;
        }

        StringBuilder result = new(stateText.Length);
        int start = 0;
        int braceDepth = 0;
        int bracketDepth = 0;

        for (int i = 0; i <= stateText.Length; i++)
        {
            bool atEnd = i == stateText.Length;
            char ch = atEnd ? ';' : stateText[i];

            if (!atEnd)
            {
                if (ch == '{') { braceDepth++; continue; }
                if (ch == '}') { if (braceDepth > 0) braceDepth--; continue; }
                if (ch == '[') { bracketDepth++; continue; }
                if (ch == ']') { if (bracketDepth > 0) bracketDepth--; continue; }
                if (ch != ';' || braceDepth != 0 || bracketDepth != 0)
                    continue;
            }

            ReadOnlySpan<char> segment = stateText.AsSpan(start, i - start);
            if (!IsModSection(segment))
            {
                if (result.Length > 0)
                    result.Append(';');
                result.Append(segment);
            }

            start = i + 1;
        }

        return result.ToString();
    }

    private static bool IsModSection(ReadOnlySpan<char> segment)
        => segment.StartsWith("model_state=", StringComparison.Ordinal)
            || segment.StartsWith("onplay_configuration=", StringComparison.Ordinal)
            || segment.StartsWith("hook_projection_patches=", StringComparison.Ordinal)
            || segment.StartsWith("loadout_summon_powers=", StringComparison.Ordinal);

    private static string ResolveMoveId(HotRules rules, ushort monsterDefId, short moveNode)
    {
        if (moveNode < 0 || monsterDefId == 0)
            return "null";
        HotMonsterMachine? machine = rules.GetMonsterMachine(monsterDefId);
        if (machine == null || moveNode >= machine.Nodes.Length)
            return "null";
        return machine.Nodes[moveNode].MoveId;
    }

    private static void AppendMonsterAiLog(
        StringBuilder text,
        in HotEnemy enemy,
        byte enemySlot,
        HotColdSidecar? cold,
        HotRules rules)
    {
        HotMonsterMachine? machine = rules.GetMonsterMachine(enemy.MonsterDefId);
        if (machine == null)
            return;

        if (cold?.MonsterLogs is { Length: > 0 } logs)
        {
            for (int i = 0; i < logs.Length; i++)
            {
                if (logs[i].EnemySlot == enemySlot)
                {
                    ushort[] ids = logs[i].MoveNodeIds;
                    for (int k = 0; k < ids.Length; k++)
                    {
                        if (k > 0) text.Append(',');
                        text.Append(machine.Nodes[ids[k]].MoveId);
                    }
                    return;
                }
            }
        }

        int count = Math.Min((int)enemy.RecentMoveCount, HotLimits.MaxMonsterRecentMoves);
        for (int k = 0; k < count; k++)
        {
            if (k > 0) text.Append(',');
            byte nodeIdx = enemy.RecentMoves[k];
            text.Append(nodeIdx < machine.Nodes.Length ? machine.Nodes[nodeIdx].MoveId : "null");
        }
    }

    private static void AppendMonsterState(StringBuilder text, in HotEnemy enemy, HotRules rules)
    {
        HotMonsterStateKind kind = enemy.MonsterDefId > 0
            ? rules.Monsters[enemy.MonsterDefId].StateKind
            : HotMonsterStateKind.None;

        switch (kind)
        {
            case HotMonsterStateKind.FrogKnight:
                text.Append((enemy.Flags & HotEnemyFlags.BeetleCharged) != 0 ? '1' : '0');
                break;
            case HotMonsterStateKind.TwoTailedRat:
            case HotMonsterStateKind.TestSubject:
                text.Append(enemy.AiRegs[0]).Append(',').Append(enemy.AiRegs[1]);
                break;
            case HotMonsterStateKind.Fabricator:
                text.Append(enemy.AiRegs[0] switch
                {
                    1 => "GUARDBOT",
                    2 => "NOISEBOT",
                    3 => "ZAPBOT",
                    4 => "STABBOT",
                    _ => "-",
                });
                break;
            case HotMonsterStateKind.ToughEgg:
                text.Append((enemy.Flags & HotEnemyFlags.Hatched) != 0 ? '1' : '0');
                break;
            case HotMonsterStateKind.Tunneler:
                text.Append((enemy.Flags & HotEnemyFlags.Stunned) != 0 ? '1' : '0');
                break;
            case HotMonsterStateKind.AwakeCheck:
                text.Append((enemy.Flags & HotEnemyFlags.Awake) != 0 ? '1' : '0');
                break;
            case HotMonsterStateKind.Queen:
                text.Append((enemy.Flags & HotEnemyFlags.AmalgamDied) != 0 ? '1' : '0');
                break;
            default:
                text.Append('-');
                break;
        }
    }

    private static void AppendPile(
        StringBuilder text,
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules,
        char marker,
        int start,
        int count)
    {
        text.Append(';').Append(marker).Append('=');
        for (int i = 0; i < count; i++)
            AppendCard(text, in state.Cards[start + i], cold, rules);
    }

    private static void AppendCard(
        StringBuilder text,
        in HotCard card,
        HotColdSidecar? cold,
        HotRules rules)
    {
        HotCardDef def = rules.Cards[card.CardDefId];
        bool costsX = (card.Flags & HotCardFlags.CostsX) != 0;
        bool hasStarCostX = (card.Flags & HotCardFlags.HasStarCostX) != 0;
        int localEnergyCost = HotRules.GetCardLocalEnergyCost(in card, cold);
        int currentStarCost = HotRules.GetCardCurrentStarCost(in card, cold);

        text.Append(def.Entry).Append('+').Append(card.UpgradeLevel)
            .Append('/').Append(costsX).Append(':').Append(localEnergyCost)
            .Append('/').Append(hasStarCostX).Append(':').Append(currentStarCost)
            .Append('/').Append(card.BaseReplayCount).Append(':').Append(card.ExtraReplayThisTurn)
            .Append('/').Append((card.Flags & HotCardFlags.ExhaustOnNextPlay) != 0)
            .Append('/').Append((card.Flags & HotCardFlags.IsSlyThisTurn) != 0)
            .Append('/').Append((card.Flags & HotCardFlags.SingleTurnRetain) != 0)
            .Append('/').Append((card.Flags & HotCardFlags.HasDeckVersion) != 0)
            .Append('/').Append((card.Flags & HotCardFlags.RemovedFromState) != 0)
            .Append('/');

        AppendEnchantment(text, in card, rules);

        text.Append('/')
            .Append(card.AfflId == 0 ? "-" : rules.Afflictions[card.AfflId].Entry)
            .Append(':').Append(card.AfflId == 0 ? 0 : card.AfflAmt)
            .Append('[');

        for (int i = 0; i < def.OrderedVarNames.Length; i++)
        {
            text.Append(def.OrderedVarNames[i]).Append('=').Append(HotRules.GetCardVarValue(in card, def, i));
            if (def.OrderedVarStrings != null && def.OrderedVarStrings[i] is { } strVal)
                text.Append(':').Append(strVal);
            text.Append(',');
        }

        text.Append("]/private=");
        switch (def.PrivateKind)
        {
            case HotCardPrivateKind.Claw:
            case HotCardPrivateKind.GeneticAlgorithm:
            case HotCardPrivateKind.Maul:
            case HotCardPrivateKind.Rampage:
            case HotCardPrivateKind.TheScythe:
                text.Append(card.CardPrivate);
                break;
            case HotCardPrivateKind.MadScience:
                CardType ttType = (CardType)(card.CardPrivate & 0xFFFF);
                TinkerTime.RiderEffect ttRider = (TinkerTime.RiderEffect)((card.CardPrivate >> 16) & 0xFFFF);
                text.Append(ttType).Append('/').Append(ttRider);
                break;
            default:
                text.Append('-');
                break;
        }

        AppendCardCostState(text, in card, cold);
        AppendCardInstanceState(text, in card, def, cold, rules);

        text.Append("/keywords=[");
        AppendKeywordMask(text, card.LocalKeywordsMask);
        text.Append("]/baselib=-,");
    }

    private static void AppendEnchantment(StringBuilder text, in HotCard card, HotRules rules)
    {
        if (card.EnchId == 0)
        {
            text.Append("-:0:0");
            return;
        }

        HotEnchantmentDef def = rules.Enchantments[card.EnchId];
        text.Append(def.Entry).Append(':').Append(card.EnchAmt).Append(':').Append(card.EnchStatus);
        if (def.HasMomentumExtraDamage)
            text.Append(':').Append(card.EnchPrivate);
    }

    private static void AppendCardCostState(StringBuilder text, in HotCard card, HotColdSidecar? cold)
    {
        int energyCount = HotCostModifiers.GetEnergyModifierCount(in card, cold);
        int starCount = HotCostModifiers.GetTempStarCostCount(in card, cold);
        if (energyCount == 0 && starCount == 0)
            return;

        text.Append("|cost-state=").Append(card.BaseEnergyCost)
            .Append(':').Append(energyCount).Append('[');
        for (int m = 0; m < energyCount; m++)
            AppendEnergyCostMod(text, HotCostModifiers.GetEnergyModifierAt(in card, cold, m));

        text.Append("]/stars=").Append(card.BaseStarCost)
            .Append(':').Append(starCount).Append('[');
        for (int s = 0; s < starCount; s++)
        {
            HotTempStarCost star = HotCostModifiers.GetTempStarCostAt(in card, cold, s);
            text.Append(star.Cost).Append(':')
                .Append(star.ClearsWhenTurnEnds).Append(':')
                .Append(star.ClearsWhenCardIsPlayed).Append(',');
        }
        text.Append(']');
    }

    private static void AppendEnergyCostMod(StringBuilder text, HotEnergyCostMod mod)
    {
        int type = mod.IsAbsolute ? (int)LocalCostType.Absolute : (int)LocalCostType.Relative;
        text.Append(mod.Amount).Append(':').Append(type).Append(':').Append(mod.Expiration)
            .Append(':').Append(mod.IsReduceOnly).Append(',');
    }

    private static void AppendCardInstanceState(
        StringBuilder text,
        in HotCard card,
        HotCardDef def,
        HotColdSidecar? cold,
        HotRules rules)
    {
        text.Append("|keywordsAdded=");
        AppendKeywordMask(text, card.KeywordsAddedOutsideMask);
        text.Append("|keywordsRemoved=");
        AppendKeywordMask(text, card.KeywordsRemovedOutsideMask);

        if (def.PrivateKind == HotCardPrivateKind.TheShot)
            text.Append("|florescence=").Append(card.CardPrivate);
        else if (def.PrivateKind == HotCardPrivateKind.ShowOff)
        {
            short currentAim = (short)(card.CardPrivate & 0xFFFF);
            short increasedAim = (short)((card.CardPrivate >> 16) & 0xFFFF);
            text.Append("|showOff=").Append(currentAim).Append(':').Append(increasedAim);
        }

        bool isDupe = (card.Flags & HotCardFlags.IsDupe) != 0;
        text.Append("|dupe=").Append(isDupe);
        if (isDupe)
        {
            text.Append("|dupeSource={");
            if (!string.IsNullOrEmpty(def.RootDupeSourceKey))
            {
                text.Append(def.RootDupeSourceKey);
            }
            else
            {
                HotCard reconstructedSource = ReconstructDupeSourceCard(in card, def);
                AppendChoiceCardKey(text, in reconstructedSource, cold, rules);
            }
            text.Append('}');
        }
    }

    private static HotCard ReconstructDupeSourceCard(in HotCard dupeCard, HotCardDef def)
    {
        HotCard source = dupeCard;
        source.Flags &= ~(HotCardFlags.IsDupe | HotCardFlags.IsClone);
        HotCardKeywordMask baseKeywords = (HotCardKeywordMask)def.BaseKeywords;
        if ((baseKeywords & HotCardKeywordMask.Exhaust) != 0
            && (source.LocalKeywordsMask & HotCardKeywordMask.Exhaust) == 0
            && (source.KeywordsRemovedOutsideMask & HotCardKeywordMask.Exhaust) != 0)
        {
            source.LocalKeywordsMask |= HotCardKeywordMask.Exhaust;
            source.KeywordsRemovedOutsideMask &= ~HotCardKeywordMask.Exhaust;
        }
        return source;
    }

    public static string FormatChoiceCardKey(
        in HotCard card,
        HotColdSidecar? cold,
        HotRules rules)
    {
        StringBuilder text = new(160);
        AppendChoiceCardKey(text, in card, cold, rules);
        return text.ToString();
    }

    private static void AppendChoiceCardKey(
        StringBuilder text,
        in HotCard card,
        HotColdSidecar? cold,
        HotRules rules)
    {
        HotCardDef def = rules.Cards[card.CardDefId];
        bool costsX = (card.Flags & HotCardFlags.CostsX) != 0;
        bool hasStarCostX = (card.Flags & HotCardFlags.HasStarCostX) != 0;
        int localEnergyCost = HotRules.GetCardLocalEnergyCost(in card, cold);
        int currentStarCost = HotRules.GetCardCurrentStarCost(in card, cold);

        text.Append(def.Entry).Append('+').Append(card.UpgradeLevel)
            .Append("|energy=").Append(costsX).Append(':').Append(localEnergyCost)
            .Append("|stars=").Append(hasStarCostX).Append(':').Append(currentStarCost)
            .Append("|replay=").Append(card.BaseReplayCount).Append(':').Append(card.ExtraReplayThisTurn)
            .Append("|exhaust=").Append((card.Flags & HotCardFlags.ExhaustOnNextPlay) != 0)
            .Append("|sly=").Append((card.Flags & HotCardFlags.IsSlyThisTurn) != 0)
            .Append("|retain=").Append((card.Flags & HotCardFlags.SingleTurnRetain) != 0)
            .Append("|deck=").Append((card.Flags & HotCardFlags.HasDeckVersion) != 0)
            .Append("|keywords=");
        AppendKeywordMask(text, card.LocalKeywordsMask);
        text.Append("|vars=");
        for (int i = 0; i < def.OrderedVarNames.Length; i++)
        {
            if (i > 0) text.Append(';');
            text.Append(def.OrderedVarNames[i]).Append('=').Append(HotRules.GetCardVarValue(in card, def, i));
        }
        text.Append('|');
        if (card.EnchId == 0)
            text.Append('-');
        else
            AppendEnchantment(text, in card, rules);
        text.Append('|')
            .Append(card.AfflId == 0 ? string.Empty : rules.Afflictions[card.AfflId].Entry)
            .Append(':').Append(card.AfflId == 0 ? 0 : card.AfflAmt)
            .Append("|baselib=-");
        AppendCardCostState(text, in card, cold);
        AppendCardInstanceState(text, in card, def, cold, rules);
    }

    private static void AppendKeywordMask(StringBuilder text, HotCardKeywordMask mask)
    {
        if (mask == HotCardKeywordMask.None)
            return;

        bool first = true;
        // Bit order 0..6 matches CardKeyword enum values 1..7 (ascending order).
        if ((mask & HotCardKeywordMask.Exhaust) != 0) { AppendKw(text, ref first, nameof(CardKeyword.Exhaust)); }
        if ((mask & HotCardKeywordMask.Ethereal) != 0) { AppendKw(text, ref first, nameof(CardKeyword.Ethereal)); }
        if ((mask & HotCardKeywordMask.Innate) != 0) { AppendKw(text, ref first, nameof(CardKeyword.Innate)); }
        if ((mask & HotCardKeywordMask.Unplayable) != 0) { AppendKw(text, ref first, nameof(CardKeyword.Unplayable)); }
        if ((mask & HotCardKeywordMask.Retain) != 0) { AppendKw(text, ref first, nameof(CardKeyword.Retain)); }
        if ((mask & HotCardKeywordMask.Sly) != 0) { AppendKw(text, ref first, nameof(CardKeyword.Sly)); }
        if ((mask & HotCardKeywordMask.Eternal) != 0) { AppendKw(text, ref first, nameof(CardKeyword.Eternal)); }
    }

    private static void AppendKw(StringBuilder text, ref bool first, string name)
    {
        if (!first)
            text.Append(',');
        first = false;
        text.Append(name);
    }

    private static void AppendTrackedRelicValue(StringBuilder text, HotRelicSpecialKind kind, HotRelicState rs)
    {
        switch (kind)
        {
            case HotRelicSpecialKind.BeatingRemnant:
                text.Append(((decimal)rs.Current).ToString(CultureInfo.InvariantCulture));
                break;
            case HotRelicSpecialKind.JossPaper:
                if (rs.PreviousOrAux == 0)
                    text.Append(rs.Current);
                else
                    text.Append(rs.Current).Append(":ethereal=").Append(rs.PreviousOrAux);
                break;
            case HotRelicSpecialKind.PaelsLegion:
                text.Append(rs.Current)
                    .Append(':').Append((rs.PreviousOrAux & 1) != 0 ? '1' : '0')
                    .Append(':').Append((rs.PreviousOrAux & 2) != 0 ? '1' : '0');
                break;
            case HotRelicSpecialKind.RainbowRing:
                int atk = rs.Current & 0xFFFF;
                int skl = (rs.Current >> 16) & 0xFFFF;
                int pwr = rs.PreviousOrAux & 0xFFFF;
                int act = (rs.PreviousOrAux >> 16) & 0xFFFF;
                text.Append(atk).Append(':').Append(skl).Append(':').Append(pwr).Append(':').Append(act);
                break;
            default:
                text.Append(rs.Current);
                break;
        }
    }

    private static void AppendPowersForOwner(
        StringBuilder text,
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules,
        byte ownerSlot,
        uint ownerCombatId)
    {
        for (int i = 0; i < state.PowerCount; i++)
        {
            ref readonly HotPower p = ref state.Powers[i];
            if (p.OwnerSlot != ownerSlot || p.Amount == 0)
                continue;

            HotPowerDef def = rules.Powers[p.PowerDefId];
            int turnStartAmt = def.HasRelevantTurnStartAmount ? p.AmountOnTurnStart : 0;

            text.Append(ownerCombatId).Append(':').Append(def.Entry).Append('=')
                .Append(p.Amount).Append('/').Append(turnStartAmt);
            if (def.HasRelevantSkipDurationTick && (p.Flags & HotPowerFlags.SkipNextDurationTick) != 0)
                text.Append("/skip");

            text.Append('[');
            for (int v = 0; v < def.OrderedVarNames.Length; v++)
            {
                string varName = def.OrderedVarNames[v];
                decimal val = ResolvePowerVarValue(in p, def, v, varName);
                text.Append(varName).Append('=').Append(val);
                if (def.OrderedVarStrings != null && def.OrderedVarStrings[v] is { } strVal)
                    text.Append(':').Append(strVal);
                text.Append(',');
            }

            switch (def.SpecialKind)
            {
                case HotPowerSpecialKind.Surrounded:
                {
                    // 0.113.1 SurroundedPower.Direction: Right = 0, Left = 1.
                    // Normalize legacy Aux0 == 2 (Right) from HotCombatEngine.Powers so Facing never prints "2".
                    SurroundedPower.Direction facing = p.Aux0 == (int)SurroundedPower.Direction.Left
                        ? SurroundedPower.Direction.Left
                        : SurroundedPower.Direction.Right;
                    text.Append("Facing=").Append(facing).Append(',');
                    break;
                }
                case HotPowerSpecialKind.Nightmare:
                    text.Append("Selected=");
                    if (p.AuxCardUid == 0
                        && cold?.DetachedCards is { Length: > 0 } detached
                        && p.Aux0 >= 0
                        && p.Aux0 < detached.Length)
                    {
                        AppendCard(text, in detached[p.Aux0], cold, rules);
                    }
                    else if (p.AuxCardUid != 0)
                    {
                        int srcIdx = FindCardIndexByUid(in state, p.AuxCardUid);
                        if (srcIdx >= 0)
                        {
                            HotCard nmCard = PrepareNightmareSelectedCard(in state.Cards[srcIdx], cold, out HotColdSidecar? nmCold);
                            AppendCard(text, in nmCard, nmCold, rules);
                        }
                        else if (p.Aux0 > 0 && p.Aux0 < rules.Cards.Length)
                        {
                            HotCard nmCard = BuildCanonicalCard((ushort)p.Aux0, rules);
                            AppendCard(text, in nmCard, cold: null, rules);
                        }
                    }
                    else if (p.Aux0 > 0 && p.Aux0 < rules.Cards.Length)
                    {
                        HotCard nmCard = BuildCanonicalCard((ushort)p.Aux0, rules);
                        AppendCard(text, in nmCard, cold: null, rules);
                    }
                    break;
                case HotPowerSpecialKind.Orbit:
                    text.Append("EnergyRemainder=").Append(p.Aux0).Append(',');
                    break;
            }

            text.Append("],");
        }
    }

    private static decimal ResolvePowerVarValue(in HotPower p, HotPowerDef def, int varIndex, string varName)
    {
        if (def.SpecialKind == HotPowerSpecialKind.Impaled && string.Equals(varName, "HitCounter", StringComparison.Ordinal))
            return p.Aux0;

        switch (def.PowerId)
        {
            case HotPowerId.TheBombPower:
                if (string.Equals(varName, "Damage", StringComparison.Ordinal) && p.Aux0 > 0)
                    return p.Aux0;
                break;

            case HotPowerId.ThieveryPower:
                if (string.Equals(varName, "Gold", StringComparison.Ordinal) && p.Aux0 > 0)
                    return def.DefaultVarValues[varIndex] + p.Aux0;
                break;

            case HotPowerId.PanachePower:
                if (string.Equals(varName, "CardsLeft", StringComparison.Ordinal) && p.Aux0 > 0)
                    return p.Aux0;
                break;

            case HotPowerId.SlowPower:
                if (string.Equals(varName, "SlowAmount", StringComparison.Ordinal) && p.Aux0 > 0)
                    return p.Aux0;
                if (string.Equals(varName, "DisplayAmount", StringComparison.Ordinal) && p.Aux0 > 0)
                    return p.Aux0 * 10m;
                break;

            case HotPowerId.InfernoPower:
                if (string.Equals(varName, "SelfDamage", StringComparison.Ordinal) && p.Aux0 > 0)
                    return p.Aux0;
                break;

            case HotPowerId.EnnuiPower:
                if (string.Equals(varName, "Cards", StringComparison.Ordinal) && p.Aux0 > 0)
                    return p.Aux0;
                break;

            case HotPowerId.CrimsonMantlePower:
                if (string.Equals(varName, "SelfDamage", StringComparison.Ordinal) && p.Aux0 > 0)
                    return p.Aux0;
                break;

            case HotPowerId.WitheringPresencePower:
                if (string.Equals(varName, "CardsLeft", StringComparison.Ordinal) && p.Aux0 > 0)
                    return p.Aux0;
                break;

            case HotPowerId.MonologuePower:
                if (string.Equals(varName, "StrengthApplied", StringComparison.Ordinal) && p.Aux1 > 0)
                    return p.Aux1;
                break;

            case HotPowerId.MythicFormPower:
                if (string.Equals(varName, "Upgraded", StringComparison.Ordinal) && p.Aux0 > 0)
                    return p.Aux0;
                break;

            case HotPowerId.EncorePower:
                if (string.Equals(varName, "PlaysRemaining", StringComparison.Ordinal))
                    return Math.Max(0, p.Amount - p.Aux0);
                break;

            case HotPowerId.InsatiableDominionPower:
                if (string.Equals(varName, "Damage", StringComparison.Ordinal) && p.Aux0 > 0)
                    return p.Aux0;
                break;

            case HotPowerId.CacophonyPower:
                if (string.Equals(varName, "Cards", StringComparison.Ordinal) && p.Aux0 > 0)
                    return p.Aux0;
                break;

            case HotPowerId.ToricToughnessPower:
                if (string.Equals(varName, "Block", StringComparison.Ordinal))
                {
                    if (p.Aux1 > 0)
                        return (decimal)p.Aux1 / 100m;
                    if (p.Aux0 > 0)
                        return p.Aux0;
                }
                break;
        }

        return def.DefaultVarValues[varIndex];
    }

    private static HotCard PrepareNightmareSelectedCard(
        in HotCard source,
        HotColdSidecar? cold,
        out HotColdSidecar? adjustedCold)
    {
        // Match NightmarePower.SetSelectedCard: CreateClone() + ClearAffliction() + EndOfTurnCleanup().
        HotCard copy = source;
        copy.Flags &= ~(HotCardFlags.HasDeckVersion
            | HotCardFlags.RemovedFromState
            | HotCardFlags.ExhaustOnNextPlay
            | HotCardFlags.SingleTurnRetain
            | HotCardFlags.IsSlyThisTurn
            | HotCardFlags.IsDupe);
        copy.Flags |= HotCardFlags.IsClone;
        copy.AfflId = 0;
        copy.AfflAmt = 0;
        copy.ExtraReplayThisTurn = 0;

        if ((copy.Flags & HotCardFlags.HasCostOverflow) == 0)
        {
            int write = 0;
            int count = Math.Min((int)copy.CostModCount, HotLimits.MaxEnergyCostMods);
            for (int m = 0; m < count; m++)
            {
                HotEnergyCostMod mod = copy.EnergyCostMods[m];
                if (!mod.ClearsOnTurnEnd)
                    copy.EnergyCostMods[write++] = mod;
            }
            for (int m = write; m < HotLimits.MaxEnergyCostMods; m++)
                copy.EnergyCostMods[m] = default;
            copy.CostModCount = (byte)write;

            if ((copy.TempStarCostPacked & 0x80) != 0 && (copy.TempStarCostPacked & 0x40) != 0)
                copy.TempStarCostPacked = 0;

            adjustedCold = cold;
            return copy;
        }

        if (cold?.CostOverflow is { Length: > 0 } overflows)
        {
            for (int i = 0; i < overflows.Length; i++)
            {
                if (overflows[i].CardUid != copy.CardUid)
                    continue;
                HotEnergyCostMod[] keptEnergy = Array.FindAll(overflows[i].EnergyMods, static m => !m.ClearsOnTurnEnd);
                HotTempStarCost[] keptStars = Array.FindAll(overflows[i].TempStarCosts, static t => !t.ClearsWhenTurnEnds);
                adjustedCold = new HotColdSidecar(CostOverflow: [new HotCardCostOverflow(copy.CardUid, keptEnergy, keptStars)]);
                return copy;
            }
        }

        adjustedCold = cold;
        return copy;
    }

    private static HotCard BuildCanonicalCard(ushort cardDefId, HotRules rules)
    {
        HotCardDef def = rules.Cards[cardDefId];
        return new HotCard
        {
            CardDefId = cardDefId,
            BaseEnergyCost = def.BaseCost,
            BaseStarCost = def.BaseStarCost,
            LocalKeywordsMask = (HotCardKeywordMask)def.BaseKeywords,
        };
    }

    private static string[]? ResolveActiveEnemyRosterAndSlotNames(
        in CombatHotState state,
        HotRules rules,
        Span<byte> orderedRoster)
    {
        int rosterCount = orderedRoster.Length;
        bool hasSummoned = false;
        for (int i = 0; i < rosterCount; i++)
        {
            byte eSlot = state.ActiveEnemyRoster[i];
            orderedRoster[i] = eSlot;
            if (eSlot >= rules.EnemySlotNames.Length)
                hasSummoned = true;
        }

        if (!hasSummoned)
            return null;

        int tableLen = Math.Max((int)state.EnemyCount, rules.EnemySlotNames.Length);
        string[] slotNames = new string[tableLen];
        string[] encounterSlots = rules.EncounterSlots;
        ulong occupiedMask = 0UL;

        // Pass 1: root enemies keep their exact captured SlotName.
        for (int i = 0; i < rosterCount; i++)
        {
            byte eSlot = orderedRoster[i];
            if (eSlot < rules.EnemySlotNames.Length)
            {
                string name = rules.EnemySlotNames[eSlot];
                slotNames[eSlot] = name;
                MarkEncounterSlotOccupied(encounterSlots, name, ref occupiedMask);
            }
        }

        // Pass 2: summoned enemies with an explicit encounter slot index recorded in AiRegs[2] (1-based).
        for (int i = 0; i < rosterCount; i++)
        {
            byte eSlot = orderedRoster[i];
            if (eSlot < rules.EnemySlotNames.Length || slotNames[eSlot] != null)
                continue;
            int encodedSlot = state.Enemies[eSlot].AiRegs[2];
            if (encodedSlot > 0 && encodedSlot <= encounterSlots.Length)
            {
                int encIdx = encodedSlot - 1;
                slotNames[eSlot] = encounterSlots[encIdx];
                if (encIdx < 64)
                    occupiedMask |= 1UL << encIdx;
            }
        }

        // Pass 3: deterministic STS2 0.113.1 fallback in ascending enemySlot (spawn) order.
        Span<byte> unresolved = stackalloc byte[HotLimits.MaxEnemies];
        int unresolvedCount = 0;
        for (int i = 0; i < rosterCount; i++)
        {
            byte eSlot = orderedRoster[i];
            if (slotNames[eSlot] == null)
                unresolved[unresolvedCount++] = eSlot;
        }
        // Sort unresolved by ascending enemySlot (exact spawn order) before claiming free encounter slots.
        for (int i = 1; i < unresolvedCount; i++)
        {
            byte key = unresolved[i];
            int j = i - 1;
            while (j >= 0 && unresolved[j] > key)
            {
                unresolved[j + 1] = unresolved[j];
                j--;
            }
            unresolved[j + 1] = key;
        }

        for (int i = 0; i < unresolvedCount; i++)
        {
            byte eSlot = unresolved[i];
            ref readonly HotEnemy enemy = ref state.Enemies[eSlot];
            string derived = DeriveSummonedEnemySlotName(in enemy, rules, encounterSlots, ref occupiedMask);
            slotNames[eSlot] = derived;
            MarkEncounterSlotOccupied(encounterSlots, derived, ref occupiedMask);
        }

        // Match CombatManager.AddCreature -> CombatState.SortEnemiesBySlotName() when encounter defines Slots.
        if (encounterSlots.Length > 0 && rosterCount > 1)
        {
            for (int i = 1; i < rosterCount; i++)
            {
                byte keySlot = orderedRoster[i];
                int keyOrder = FindEncounterSlotIndex(encounterSlots, slotNames[keySlot]);
                int j = i - 1;
                while (j >= 0 && FindEncounterSlotIndex(encounterSlots, slotNames[orderedRoster[j]]) > keyOrder)
                {
                    orderedRoster[j + 1] = orderedRoster[j];
                    j--;
                }
                orderedRoster[j + 1] = keySlot;
            }
        }

        return slotNames;
    }

    private static string DeriveSummonedEnemySlotName(
        in HotEnemy enemy,
        HotRules rules,
        string[] encounterSlots,
        ref ulong occupiedMask)
    {
        HotMonsterId monsterId = enemy.MonsterDefId > 0 && enemy.MonsterDefId < rules.Monsters.Length
            ? rules.Monsters[enemy.MonsterDefId].MonsterId
            : HotMonsterId.None;

        switch (monsterId)
        {
            case HotMonsterId.EyeWithTeeth:
            case HotMonsterId.Parafright:
                return "illusion";

            case HotMonsterId.SneakyGremlin:
                return "sneaky";

            case HotMonsterId.FatGremlin:
                return "fat";

            case HotMonsterId.Wriggler:
                return enemy.AiRegs[0] > 0
                    ? $"wriggler{enemy.AiRegs[0]}"
                    : ClaimFirstFreeEncounterSlot(encounterSlots, ref occupiedMask);

            case HotMonsterId.Axebot:
                for (int r = 0; r < rules.EnemySlotNames.Length && r < rules.EnemyMonsterEntries.Length; r++)
                {
                    if (string.Equals(rules.EnemyMonsterEntries[r], "AXEBOT", StringComparison.Ordinal))
                        return rules.EnemySlotNames[r];
                }
                return encounterSlots.Length == 1 ? encounterSlots[0] : "front";

            case HotMonsterId.ToughEgg:
            case HotMonsterId.TwoTailedRat:
                return ClaimLastFreeEncounterSlot(encounterSlots, ref occupiedMask);

            case HotMonsterId.GasBomb:
            case HotMonsterId.Guardbot:
            case HotMonsterId.Noisebot:
            case HotMonsterId.Zapbot:
            case HotMonsterId.Stabbot:
                return ClaimFirstFreeEncounterSlot(encounterSlots, ref occupiedMask);

            default:
                return ClaimFirstFreeEncounterSlot(encounterSlots, ref occupiedMask);
        }
    }

    private static string ClaimFirstFreeEncounterSlot(string[] encounterSlots, ref ulong occupiedMask)
    {
        for (int i = 0; i < encounterSlots.Length; i++)
        {
            if (i < 64 && (occupiedMask & (1UL << i)) != 0)
                continue;
            if (i < 64)
                occupiedMask |= 1UL << i;
            return encounterSlots[i];
        }
        return "-";
    }

    private static string ClaimLastFreeEncounterSlot(string[] encounterSlots, ref ulong occupiedMask)
    {
        for (int i = encounterSlots.Length - 1; i >= 0; i--)
        {
            if (i < 64 && (occupiedMask & (1UL << i)) != 0)
                continue;
            if (i < 64)
                occupiedMask |= 1UL << i;
            return encounterSlots[i];
        }
        return "-";
    }

    private static void MarkEncounterSlotOccupied(string[] encounterSlots, string slotName, ref ulong occupiedMask)
    {
        int idx = FindEncounterSlotIndex(encounterSlots, slotName);
        if (idx >= 0 && idx < 64)
            occupiedMask |= 1UL << idx;
    }

    private static int FindEncounterSlotIndex(string[] encounterSlots, string? slotName)
    {
        if (string.IsNullOrEmpty(slotName))
            return -1;
        for (int i = 0; i < encounterSlots.Length; i++)
        {
            if (string.Equals(encounterSlots[i], slotName, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static int BuildActiveAllyOwnerSlots(
        in CombatHotState state,
        HotRules rules,
        Span<byte> dst)
    {
        int count = 0;
        bool hasOstySlot = false;
        bool hasWuppySlot = false;
        for (int i = 0; i < rules.RootAllyOwnerSlots.Length && count < dst.Length; i++)
        {
            byte slot = rules.RootAllyOwnerSlots[i];
            dst[count++] = slot;
            if (slot == 1)
                hasOstySlot = true;
            else if (slot == 2)
                hasWuppySlot = true;
        }

        bool appendOsty = !hasOstySlot && (state.CombatFlags & HotCombatFlags.HasOsty) != 0;
        bool appendWuppy = !hasWuppySlot && (state.CombatFlags & HotCombatFlags.HasWuppy) != 0;

        if (appendOsty && appendWuppy)
        {
            if (state.Osty.CombatId <= state.Wuppy.CombatId)
            {
                if (count < dst.Length) dst[count++] = 1;
                if (count < dst.Length) dst[count++] = 2;
            }
            else
            {
                if (count < dst.Length) dst[count++] = 2;
                if (count < dst.Length) dst[count++] = 1;
            }
        }
        else if (appendOsty)
        {
            if (count < dst.Length) dst[count++] = 1;
        }
        else if (appendWuppy)
        {
            if (count < dst.Length) dst[count++] = 2;
        }

        return count;
    }

    private static void AppendImpaledPowers(
        StringBuilder text,
        in CombatHotState state,
        HotRules rules,
        ReadOnlySpan<byte> allyOwnerSlots,
        ReadOnlySpan<byte> orderedRoster)
    {
        // Emit in exact CombatState.Creatures order matching state.Creatures.SelectMany(c => c.Powers).OfType<ImpaledPower>()
        for (int i = 0; i < allyOwnerSlots.Length; i++)
        {
            byte allySlot = allyOwnerSlots[i];
            AppendImpaledForOwner(text, in state, rules, allySlot, ResolveAllyCombatId(in state, rules, allySlot));
        }
        for (int idx = 0; idx < orderedRoster.Length; idx++)
        {
            byte enemySlot = orderedRoster[idx];
            AppendImpaledForOwner(text, in state, rules, (byte)(3 + enemySlot), state.Enemies[enemySlot].Vital.CombatId);
        }
    }

    private static uint ResolveAllyCombatId(in CombatHotState state, HotRules rules, byte allySlot)
    {
        if (allySlot == 0)
            return state.Player.CombatId;
        if (allySlot == 1)
            return state.Osty.CombatId;
        if (allySlot == 2)
            return state.Wuppy.CombatId;
        int petIdx = allySlot - 19;
        return petIdx >= 0 && petIdx < rules.RootPets.Length ? rules.RootPets[petIdx].CombatId : uint.MaxValue;
    }

    private static void AppendImpaledForOwner(
        StringBuilder text,
        in CombatHotState state,
        HotRules rules,
        byte ownerSlot,
        uint ownerCombatId)
    {
        for (int i = 0; i < state.PowerCount; i++)
        {
            ref readonly HotPower p = ref state.Powers[i];
            if (p.OwnerSlot != ownerSlot || p.Amount <= 0 || p.ApplierSlot != 0)
                continue;
            if (rules.Powers[p.PowerDefId].SpecialKind != HotPowerSpecialKind.Impaled)
                continue;

            text.Append(ownerCombatId).Append(':').Append(p.Aux0).Append(':');
            int packedCount = (p.Aux1 >> 25) & 0x1F;
            if (packedCount > 0 && packedCount <= 5)
            {
                for (int a = 0; a < packedCount; a++)
                {
                    int arrowSlot = (p.Aux1 >> (a * 5)) & 0x1F;
                    text.Append(arrowSlot).Append(',');
                }
            }
            else if (ownerSlot >= 3)
            {
                int targetTag = (ownerSlot - 3) + 1;
                for (int a = 0; a < state.ArrowCount; a++)
                {
                    int impaledTag = (state.Fool.Arrows[a] >> 5) & 0x1F;
                    if (impaledTag == targetTag)
                        text.Append(a).Append(',');
                }
            }
            text.Append('/');
        }
    }

    private static int FindCardIndexByUid(in CombatHotState state, ushort cardUid)
    {
        int total = state.TotalCardCount;
        for (int i = 0; i < total; i++)
        {
            if (state.Cards[i].CardUid == cardUid)
                return i;
        }
        return -1;
    }

    private static void AppendRngState(StringBuilder text, in HotRngState rng)
        => text.Append(rng.Counter).Append(':')
            .Append(rng.S0).Append(':').Append(rng.S1).Append(':')
            .Append(rng.S2).Append(':').Append(rng.S3);
}
