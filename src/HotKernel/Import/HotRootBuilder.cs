using System.Globalization;
using System.Reflection;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;
using STS2RitsuLib;

namespace CombatSolver.HotKernel;

internal sealed record HotRootBundle(
    HotRules Rules,
    CombatHotState RootState,
    HotColdSidecar? RootCold);

/// <summary>
/// Captures a live vanilla STS2 <see cref="CombatState"/> into an immutable <see cref="HotRules"/>,
/// contiguous <see cref="CombatHotState"/>, and optional <see cref="HotColdSidecar"/>.
/// </summary>
internal static class HotRootBuilder
{
    private static readonly FieldInfo InitialStateField =
        typeof(MonsterMoveStateMachine).GetField("_initialState", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(MonsterMoveStateMachine).FullName, "_initialState");

    private static readonly FieldInfo MomentumExtraDamageField =
        typeof(Momentum).GetField("_extraDamage", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(Momentum).FullName, "_extraDamage");

    private static readonly FieldInfo ArtOfWarLastTurnField =
        typeof(ArtOfWar).GetField("_anyAttacksPlayedLastTurn", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(ArtOfWar).FullName, "_anyAttacksPlayedLastTurn");

    private static readonly FieldInfo ArtOfWarThisTurnField =
        typeof(ArtOfWar).GetField("_anyAttacksPlayedThisTurn", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(ArtOfWar).FullName, "_anyAttacksPlayedThisTurn");

    private static readonly FieldInfo BeltBuckleDexterityAppliedField =
        typeof(BeltBuckle).GetField("_dexterityApplied", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(BeltBuckle).FullName, "_dexterityApplied");

    private static readonly FieldInfo RuinedHelmetUsedField =
        typeof(RuinedHelmet).GetField("_usedThisCombat", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(RuinedHelmet).FullName, "_usedThisCombat");

    private static readonly FieldInfo UnsettlingLampFinishedField =
        typeof(UnsettlingLamp).GetField("_isFinishedTriggering", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(UnsettlingLamp).FullName, "_isFinishedTriggering");

    public static HotRootBundle BuildFromLive(CombatState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        VanillaRootGate.ValidateVanillaOnly(state);

        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        PlayerCombatState pcs = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有战斗状态。");

        BuilderContext ctx = new();

        // Pre-register core Phase 1 powers so card bytecodes & monster effects resolve deterministic DefIds.
        ctx.PreRegisterPhase1Powers();

        CombatHotState hot = default;
        hot.NextCreatureId = state._nextCreatureId;
        hot.RoundNumber = state.RoundNumber;
        hot.PlayerTurnNumber = pcs.TurnNumber;
        hot.Energy = pcs.Energy;
        hot.Stars = pcs.Stars;
        hot.Gold = player.Gold;
        hot.CurrentSide = state.CurrentSide == CombatSide.Enemy ? HotCombatSide.Enemy : HotCombatSide.Player;
        hot.TerminalOutcome = HotCombatOutcome.Ongoing;

        int maxHandSize = RitsuLibFramework.GetMaxHandSize(player);
        hot.MaxHandSize = (byte)Math.Clamp(maxHandSize, 0, byte.MaxValue);

        int baseMaxEnergy = pcs.MaxEnergy;
        hot.Counters[(int)HotCounterSlot.BaseMaxEnergy] = baseMaxEnergy;
        hot.Counters[(int)HotCounterSlot.BaseHandDraw] = 5;

        HotCombatFlags combatFlags = HotCombatFlags.IsInProgress | HotCombatFlags.PlayerHooksActive;
        bool encounterGoldForfeited = TheftEncounterStrategy.ForfeitsEncounterGold(state);
        if (encounterGoldForfeited)
            combatFlags |= HotCombatFlags.EncounterGoldForfeited;

        // Player creature (slot 0)
        hot.Player = BuildCreature(player.Creature, slotIdx: 0, HotCreatureFlags.IsPlayer);

        // Osty (slot 1)
        Creature? osty = player.Osty;
        if (osty != null)
        {
            combatFlags |= HotCombatFlags.HasOsty;
            hot.Osty = BuildCreature(osty, slotIdx: 1, HotCreatureFlags.IsOsty | HotCreatureFlags.IsPet);
        }

        // Non-Osty player pets in exact state.Allies order
        List<HotRootPetSnapshot> rootPets = [];
        Dictionary<Creature, byte> creatureToOwnerSlot = new(ReferenceEqualityComparer.Instance)
        {
            [player.Creature] = 0,
        };
        if (osty != null)
            creatureToOwnerSlot[osty] = 1;

        List<Creature> nonOstyPets = [];
        foreach (Creature ally in state.Allies)
        {
            if (ally.PetOwner == player && !ReferenceEquals(ally, osty))
                nonOstyPets.Add(ally);
        }

        int wuppyPetIdx = -1;
        for (int i = 0; i < nonOstyPets.Count; i++)
        {
            if (nonOstyPets[i].Monster is MegaCrit.Sts2.Core.Models.Monsters.Wuppy)
            {
                wuppyPetIdx = i;
                break;
            }
        }
        if (wuppyPetIdx < 0 && nonOstyPets.Count > 0)
            wuppyPetIdx = 0;

        for (int i = 0; i < nonOstyPets.Count; i++)
        {
            Creature pet = nonOstyPets[i];
            bool isWuppySlot = i == wuppyPetIdx;
            byte petOwnerSlot = isWuppySlot ? (byte)2 : (byte)(19 + i);
            creatureToOwnerSlot[pet] = petOwnerSlot;

            if (isWuppySlot)
            {
                combatFlags |= HotCombatFlags.HasWuppy;
                hot.Wuppy = BuildCreature(pet, slotIdx: 2, HotCreatureFlags.IsPet);
            }

            rootPets.Add(new HotRootPetSnapshot(
                CombatId: pet.CombatId ?? uint.MaxValue,
                MonsterEntry: pet.Monster!.Id.Entry,
                CurrentHp: pet.CurrentHp,
                MaxHp: pet.MaxHp,
                Block: pet.Block,
                IsWuppySlot: isWuppySlot));
        }

        hot.CombatFlags = combatFlags;

        // Enemies (slots 3..18 -> Enemies[0..15])
        IReadOnlyList<Creature> enemies = state.Enemies;
        if (enemies.Count > HotLimits.MaxEnemies)
            throw new InvalidOperationException($"Enemy count {enemies.Count} exceeds HotLimits.MaxEnemies ({HotLimits.MaxEnemies}).");

        hot.EnemyCount = (byte)enemies.Count;
        hot.RosterCount = (byte)enemies.Count;
        string[] enemySlotNames = new string[enemies.Count];
        string[] enemyMonsterEntries = new string[enemies.Count];

        for (int i = 0; i < enemies.Count; i++)
        {
            Creature enemy = enemies[i];
            byte ownerSlot = (byte)(3 + i);
            creatureToOwnerSlot[enemy] = ownerSlot;
            hot.ActiveEnemyRoster[i] = (byte)i;
            enemySlotNames[i] = enemy.SlotName ?? "-";
            enemyMonsterEntries[i] = enemy.Monster?.Id.Entry ?? "null";

            hot.Enemies[i] = BuildEnemy(ctx, state, enemy, enemySlot: (byte)i);
        }

        // Capture ally ordering for exact ContinuationStamp power grouping
        List<byte> rootAllyOwnerSlots = new(state.Allies.Count);
        foreach (Creature ally in state.Allies)
        {
            if (creatureToOwnerSlot.TryGetValue(ally, out byte slot))
                rootAllyOwnerSlots.Add(slot);
        }

        // Capture cards across all 5 piles in exact order: [Hand | Draw | Discard | Exhaust | Play]
        HotColdSidecar? rootCold = null;
        Dictionary<CardModel, ushort> cardToUid = new(ReferenceEqualityComparer.Instance);
        int cardCursor = 0;
        ushort nextCardUid = 1;

        hot.HandCount = (ushort)ImportPile(ctx, pcs.Hand, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, cardToUid);
        hot.DrawCount = (ushort)ImportPile(ctx, pcs.DrawPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, cardToUid);
        hot.DiscardCount = (ushort)ImportPile(ctx, pcs.DiscardPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, cardToUid);
        hot.ExhaustCount = (ushort)ImportPile(ctx, pcs.ExhaustPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, cardToUid);
        hot.PlayCount = (ushort)ImportPile(ctx, pcs.PlayPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, cardToUid);
        hot.NextCardUid = nextCardUid;

        // Orbs
        IReadOnlyList<OrbModel> orbs = pcs.OrbQueue.Orbs;
        if (orbs.Count > HotLimits.MaxOrbs)
            throw new InvalidOperationException($"Orb count {orbs.Count} exceeds HotLimits.MaxOrbs ({HotLimits.MaxOrbs}).");
        hot.OrbCapacity = (byte)Math.Clamp(pcs.OrbQueue.Capacity, 0, byte.MaxValue);
        hot.OrbCount = (byte)orbs.Count;
        for (int i = 0; i < orbs.Count; i++)
        {
            OrbModel orb = orbs[i];
            byte orbDefId = ctx.EnsureOrbDef(orb);
            hot.Orbs[i] = new HotOrb
            {
                OrbTypeId = orbDefId,
                Flags = 0,
                PassiveVal = (short)orb.PassiveVal,
                EvokeVal = (int)orb.EvokeVal,
            };
        }

        // Potions
        int potionSlots = player.PotionSlots.Count;
        if (potionSlots > HotLimits.MaxPotions)
            throw new InvalidOperationException($"Potion slot count {potionSlots} exceeds HotLimits.MaxPotions ({HotLimits.MaxPotions}).");
        hot.PotionSlotCount = (byte)potionSlots;
        for (int slot = 0; slot < potionSlots; slot++)
        {
            PotionModel? potion = player.GetPotionAtSlotIndex(slot);
            hot.Potions[slot] = potion == null ? (ushort)0 : ctx.EnsurePotionDef(potion);
        }

        // Active player relics & stateful/tracked relic state slots
        List<HotActiveRelic> activeRelics = [];
        List<ushort> activeRelicDefIds = [];
        Dictionary<ushort, byte> relicDefIdToStateSlot = [];
        byte relicStateCount = 0;

        foreach (RelicModel relic in player.Relics)
        {
            if (relic.IsMelted)
                continue;
            ushort relicDefId = ctx.EnsureRelicDef(relic);
            HotRelicDef def = ctx.Relics[relicDefId];
            byte stateSlot = 0xFF;

            if (def.IsStateful || def.IsTracked)
            {
                if (relicStateCount >= HotLimits.MaxRelicStates)
                    throw new InvalidOperationException($"Stateful/tracked relic count exceeds HotLimits.MaxRelicStates ({HotLimits.MaxRelicStates}).");
                stateSlot = relicStateCount++;
                hot.RelicStates[stateSlot] = CaptureLiveRelicState(relic, def.SpecialKind);
                relicDefIdToStateSlot.TryAdd(relicDefId, stateSlot);
            }

            activeRelics.Add(new HotActiveRelic(relicDefId, stateSlot));
            activeRelicDefIds.Add(relicDefId);
        }
        hot.RelicStateCount = relicStateCount;

        byte[] statefulRelicOrder = activeRelics
            .Select((r, idx) => (Relic: r, Index: (byte)idx, Def: ctx.Relics[r.RelicDefId]))
            .Where(static x => x.Def.IsStateful)
            .OrderBy(static x => x.Def.Entry, StringComparer.Ordinal)
            .Select(static x => x.Index)
            .ToArray();

        byte[] trackedRelicOrder = activeRelics
            .Select((r, idx) => (Relic: r, Index: (byte)idx, Def: ctx.Relics[r.RelicDefId]))
            .Where(static x => x.Def.IsTracked)
            .OrderBy(static x => x.Def.Entry, StringComparer.Ordinal)
            .Select(static x => x.Index)
            .ToArray();

        byte[][] relicHooksByBit = BuildRelicHooksByBit(ctx.Relics, activeRelicDefIds, out ulong staticRelicHookMask);

        // Fool arrows
        IReadOnlyList<ArrowSlot> arrows = pcs.Arrows;
        if (arrows.Count > HotLimits.MaxArrows)
            throw new InvalidOperationException($"Arrow count {arrows.Count} exceeds HotLimits.MaxArrows ({HotLimits.MaxArrows}).");
        hot.ArrowCount = (byte)arrows.Count;
        for (int i = 0; i < arrows.Count; i++)
        {
            ArrowSlot arrow = arrows[i];
            ushort packed = (ushort)(((int)arrow.Status & 0xF) | (arrow.DestroyAfterImpaled ? 0x10 : 0));
            hot.Fool.Arrows[i] = packed;
        }

        // Powers across all creatures in exact CombatState.Creatures order
        int powerCursor = 0;
        foreach (Creature creature in state.Creatures)
        {
            if (!creatureToOwnerSlot.TryGetValue(creature, out byte ownerSlot))
                continue;

            foreach (PowerModel power in creature.Powers)
            {
                if (power.Amount == 0)
                    continue;
                if (powerCursor >= HotLimits.MaxPowers)
                    throw new InvalidOperationException($"Power count exceeds HotLimits.MaxPowers ({HotLimits.MaxPowers}).");

                ushort powerDefId = ctx.EnsurePowerDef(power);
                HotPowerDef powerDef = ctx.Powers[powerDefId];

                byte applierSlot = power.Applier != null && creatureToOwnerSlot.TryGetValue(power.Applier, out byte aSlot)
                    ? aSlot
                    : (byte)0xFF;
                byte targetSlot = power.Target != null && creatureToOwnerSlot.TryGetValue(power.Target, out byte tSlot)
                    ? tSlot
                    : (byte)0xFF;

                HotPowerFlags pFlags = HotPowerFlags.None;
                if (PowerLifecycleSupport.SemanticallyRelevantSkipNextDurationTick(power))
                    pFlags |= HotPowerFlags.SkipNextDurationTick;
                if (power.InstanceType != PowerInstanceType.None)
                    pFlags |= HotPowerFlags.Instanced;

                int amountOnTurnStart = PowerLifecycleSupport.SemanticallyRelevantAmountOnTurnStart(power);
                ushort auxCardUid = 0;
                int aux0 = 0;
                int aux1 = 0;

                switch (power)
                {
                    case SurroundedPower surrounded:
                        aux0 = (int)surrounded.Facing;
                        break;

                    case NightmarePower nightmare:
                    {
                        CardModel selected = nightmare.GetInternalData<NightmarePower.Data>().selectedCard
                            ?? throw new InvalidOperationException("Native Nightmare has no selected card at root capture.");
                        HotCard detached = BuildCard(ctx, selected, cardUid: 0, ref nextCardUid, ref rootCold);
                        aux0 = ctx.AddDetachedCard(detached);
                        break;
                    }

                    case OrbitPower orbit:
                        aux0 = (4 - orbit.DisplayAmount) % 4;
                        break;

                    case ShowOffPower showOff:
                        if (showOff.ShowOffInstance is { } showOffCard && cardToUid.TryGetValue(showOffCard, out ushort showOffUid))
                            auxCardUid = showOffUid;
                        break;

                    case ImpaledPower impaled:
                    {
                        aux0 = impaled.HitCounter;
                        IReadOnlyList<ArrowSlot> impaledArrows = impaled.Arrows;
                        int packedCount = Math.Min(impaledArrows.Count, 5);
                        int packedArrows = packedCount << 25;
                        int enemyIndex = ownerSlot >= 3 && ownerSlot < 3 + HotLimits.MaxEnemies ? ownerSlot - 3 : -1;
                        for (int a = 0; a < impaledArrows.Count; a++)
                        {
                            int arrowIdx = FoolStateSupport.SlotIndex(arrows, impaledArrows[a]);
                            if (a < 5)
                                packedArrows |= (arrowIdx & 0x1F) << (a * 5);
                            if (enemyIndex >= 0 && arrowIdx >= 0 && arrowIdx < HotLimits.MaxArrows)
                            {
                                ushort existing = hot.Fool.Arrows[arrowIdx];
                                hot.Fool.Arrows[arrowIdx] = (ushort)((existing & 0x1F) | (((enemyIndex + 1) & 0x1F) << 5));
                            }
                        }
                        aux1 = packedArrows;
                        break;
                    }

                    case AimPower aim:
                    {
                        var cmd = aim.GetInternalData<AimPower.Data>().commandToModify;
                        if (cmd != null)
                        {
                            pFlags |= HotPowerFlags.BoolAux;
                            if (cmd.ModelSource is CardModel srcCard && cardToUid.TryGetValue(srcCard, out ushort srcUid))
                                auxCardUid = srcUid;
                        }
                        break;
                    }

                    case EncorePower encore:
                        aux0 = encore.GetInternalData<EncorePower.Data>().numberOfCardsPlayedSinceApplied;
                        break;

                    case YahPower yah:
                        if (yah.GetInternalData<YahPower.Data>().triggeredThisTurn)
                            pFlags |= HotPowerFlags.BoolAux;
                        break;

                    case LullabyPower lullaby:
                        if (lullaby._proccdThisTurn)
                            pFlags |= HotPowerFlags.BoolAux;
                        break;
                }

                hot.Powers[powerCursor++] = new HotPower
                {
                    PowerDefId = powerDefId,
                    OwnerSlot = ownerSlot,
                    ApplierSlot = applierSlot,
                    TargetSlot = targetSlot,
                    Flags = pFlags,
                    AuxCardUid = auxCardUid,
                    Amount = power.Amount,
                    AmountOnTurnStart = amountOnTurnStart,
                    Aux0 = aux0,
                    Aux1 = aux1,
                };
            }
        }
        hot.PowerCount = (byte)powerCursor;
        hot.NextCardUid = nextCardUid;

        // Combat history counters & melodies
        CaptureHistoryAndMelodies(state, player, ref hot);

        // All 10 RNG streams (9 combat + 1 non_combat_generation)
        hot.Rng[(int)HotRngStream.Shuffle] = FromRng(state.RunState.Rng.Shuffle);
        hot.Rng[(int)HotRngStream.CombatCardGeneration] = FromRng(state.RunState.Rng.CombatCardGeneration);
        hot.Rng[(int)HotRngStream.CombatPotionGeneration] = FromRng(state.RunState.Rng.CombatPotionGeneration);
        hot.Rng[(int)HotRngStream.CombatCardSelection] = FromRng(state.RunState.Rng.CombatCardSelection);
        hot.Rng[(int)HotRngStream.CombatEnergyCosts] = FromRng(state.RunState.Rng.CombatEnergyCosts);
        hot.Rng[(int)HotRngStream.CombatTargets] = FromRng(state.RunState.Rng.CombatTargets);
        hot.Rng[(int)HotRngStream.CombatOrbGeneration] = FromRng(state.RunState.Rng.CombatOrbGeneration);
        hot.Rng[(int)HotRngStream.MonsterAi] = FromRng(state.RunState.Rng.MonsterAi);
        hot.Rng[(int)HotRngStream.Niche] = FromRng(state.RunState.Rng.Niche);
        hot.Rng[(int)HotRngStream.NonCombatGeneration] = FromRng(player.PlayerRng.NonCombatGeneration);

        // ActiveHookMask = static relics | active powers | combat cards
        ulong activeMask = staticRelicHookMask;
        for (int i = 0; i < hot.PowerCount; i++)
        {
            if (hot.Powers[i].Amount != 0)
                activeMask |= (ulong)ctx.Powers[hot.Powers[i].PowerDefId].HookMask;
        }
        int totalCards = hot.TotalCardCount;
        for (int i = 0; i < totalCards; i++)
            activeMask |= (ulong)ctx.Cards[hot.Cards[i].CardDefId].HookMask;
        hot.ActiveHookMask = activeMask;

        CombatRulesSnapshot rulesSnapshot = CombatRulesSnapshot.Capture(state);
        HotCoverageManifest coverage = BuildCoverageManifest(ctx, state, player, in hot, rulesSnapshot, rootPets);

        ushort vulnerableId = ctx.PowerDefIdByEntry.GetValueOrDefault("VULNERABLE_POWER");
        ushort weakId = ctx.PowerDefIdByEntry.GetValueOrDefault("WEAK_POWER");
        ushort frailId = ctx.PowerDefIdByEntry.GetValueOrDefault("FRAIL_POWER");
        ushort strengthId = ctx.PowerDefIdByEntry.GetValueOrDefault("STRENGTH_POWER");
        ushort dexterityId = ctx.PowerDefIdByEntry.GetValueOrDefault("DEXTERITY_POWER");
        ushort metallicizeId = ctx.PowerDefIdByEntry.GetValueOrDefault("METALLICIZE_POWER");
        ushort platingId = ctx.PowerDefIdByEntry.GetValueOrDefault("PLATING_POWER");
        ushort barricadeId = ctx.PowerDefIdByEntry.GetValueOrDefault("BARRICADE_POWER");
        ushort artifactId = ctx.PowerDefIdByEntry.GetValueOrDefault("ARTIFACT_POWER");
        ushort thornsId = ctx.PowerDefIdByEntry.GetValueOrDefault("THORNS_POWER");
        ushort ritualId = ctx.PowerDefIdByEntry.GetValueOrDefault("RITUAL_POWER");

        ushort burningBloodId = ctx.RelicDefIdByEntry.GetValueOrDefault("BURNING_BLOOD");
        ushort oddMushroomId = ctx.RelicDefIdByEntry.GetValueOrDefault("ODD_MUSHROOM");
        ushort paperFrogId = ctx.RelicDefIdByEntry.GetValueOrDefault("PAPER_FROG");

        HotRules rules = new()
        {
            Cards = ctx.Cards.ToArray(),
            Powers = ctx.Powers.ToArray(),
            Relics = ctx.Relics.ToArray(),
            Potions = ctx.Potions.ToArray(),
            Orbs = ctx.Orbs.ToArray(),
            Enchantments = ctx.Enchantments.ToArray(),
            Afflictions = ctx.Afflictions.ToArray(),
            Monsters = ctx.Monsters.ToArray(),
            EffectOps = ctx.EffectOps.ToArray(),
            MonsterMachines = ctx.MonsterMachines.ToArray(),
            ActiveRelicDefIds = activeRelicDefIds.ToArray(),
            ActiveRelics = activeRelics.ToArray(),
            StatefulRelicOrder = statefulRelicOrder,
            TrackedRelicOrder = trackedRelicOrder,
            RelicHooksByBit = relicHooksByBit,
            RelicDefIdToStateSlot = relicDefIdToStateSlot,
            StaticRelicHookMask = staticRelicHookMask,
            RulesSnapshot = rulesSnapshot,
            MaxHandSize = hot.MaxHandSize,
            EncounterGoldForfeited = encounterGoldForfeited,
            BaseMaxEnergy = baseMaxEnergy,
            BaseHandDraw = 5,
            EnemySlotNames = enemySlotNames,
            EnemyMonsterEntries = enemyMonsterEntries,
            RootPets = rootPets.ToArray(),
            RootAllyOwnerSlots = rootAllyOwnerSlots.ToArray(),
            CoverageManifest = coverage,
            CardDefIdByEntry = ctx.CardDefIdByEntry,
            PowerDefIdByEntry = ctx.PowerDefIdByEntry,
            RelicDefIdByEntry = ctx.RelicDefIdByEntry,
            MonsterDefIdByEntry = ctx.MonsterDefIdByEntry,
            VulnerablePowerDefId = vulnerableId,
            WeakPowerDefId = weakId,
            FrailPowerDefId = frailId,
            StrengthPowerDefId = strengthId,
            DexterityPowerDefId = dexterityId,
            MetallicizePowerDefId = metallicizeId,
            PlatingPowerDefId = platingId,
            BarricadePowerDefId = barricadeId,
            ArtifactPowerDefId = artifactId,
            ThornsPowerDefId = thornsId,
            RitualPowerDefId = ritualId,
            BurningBloodRelicDefId = burningBloodId,
            OddMushroomRelicDefId = oddMushroomId,
            PaperFrogRelicDefId = paperFrogId,
            HasBurningBlood = burningBloodId != 0 && activeRelicDefIds.Contains(burningBloodId),
            HasOddMushroom = oddMushroomId != 0 && activeRelicDefIds.Contains(oddMushroomId),
            HasPaperFrog = paperFrogId != 0 && activeRelicDefIds.Contains(paperFrogId),
        };

        rootCold = ctx.MergeColdSidecar(rootCold);
        return new HotRootBundle(rules, hot, rootCold);
    }

    private static HotCreature BuildCreature(Creature creature, byte slotIdx, HotCreatureFlags roleFlags)
    {
        HotCreatureFlags flags = roleFlags | HotCreatureFlags.ActiveInRoster;
        if (creature.CurrentHp > 0 && creature.IsAlive)
            flags |= HotCreatureFlags.Alive;

        return new HotCreature
        {
            CombatId = creature.CombatId ?? uint.MaxValue,
            CurrentHp = creature.CurrentHp,
            MaxHp = creature.MaxHp,
            Block = creature.Block,
            Flags = flags,
            SlotIdx = slotIdx,
            Pad = 0,
        };
    }

    private static HotEnemy BuildEnemy(BuilderContext ctx, CombatState combat, Creature creature, byte enemySlot)
    {
        HotEnemy enemy = default;
        enemy.Vital = BuildCreature(creature, slotIdx: (byte)(3 + enemySlot), HotCreatureFlags.None);
        enemy.DeathPhase = ComputeLiveDeathPhase(creature, combat);
        enemy.CurrentMoveNode = -1;

        MonsterModel? monster = creature.Monster;
        if (monster == null)
            return enemy;

        ushort monsterDefId = ctx.RegisterEnemyMonster(monster, out HotMonsterMachine? machine);
        enemy.MonsterDefId = monsterDefId;

        if (machine != null)
        {
            if (monster.NextMove is { } nextMove)
                enemy.CurrentMoveNode = machine.FindNode(nextMove.Id);

            if (monster.MoveStateMachine is { } liveMachine)
            {
                IReadOnlyList<MonsterState> stateLog = liveMachine.StateLog;
                ushort[] fullIds = new ushort[stateLog.Count];
                ulong everUsed = 0;

                for (int i = 0; i < stateLog.Count; i++)
                {
                    short nodeId = machine.FindNode(stateLog[i].Id);
                    ushort uNode = nodeId >= 0 ? (ushort)nodeId : (ushort)0;
                    fullIds[i] = uNode;
                    if (uNode < 64)
                        everUsed |= 1UL << uNode;
                }

                enemy.EverUsedMovesMask = everUsed;

                if (stateLog.Count <= HotLimits.MaxMonsterRecentMoves)
                {
                    enemy.RecentMoveCount = (byte)stateLog.Count;
                    for (int i = 0; i < stateLog.Count; i++)
                        enemy.RecentMoves[i] = (byte)fullIds[i];
                }
                else
                {
                    enemy.RecentMoveCount = HotLimits.MaxMonsterRecentMoves;
                    int start = stateLog.Count - HotLimits.MaxMonsterRecentMoves;
                    for (int i = 0; i < HotLimits.MaxMonsterRecentMoves; i++)
                        enemy.RecentMoves[i] = (byte)fullIds[start + i];
                    ctx.AddMonsterLogOverflow(enemySlot, fullIds);
                }
            }
        }

        // Capture monster-specific private state matching SimulatedCombatState.MonsterState & BranchMonsterAi
        string typeName = monster.GetType().Name;
        HotEnemyFlags eFlags = HotEnemyFlags.None;
        switch (typeName)
        {
            case "FrogKnight":
                if (MonsterValueReader.ReadBool(monster, "_hasBeetleCharged"))
                    eFlags |= HotEnemyFlags.BeetleCharged;
                break;
            case "TwoTailedRat":
                enemy.AiRegs[0] = MonsterValueReader.ReadInt(monster, "_turnsUntilSummonable");
                enemy.AiRegs[1] = MonsterValueReader.ReadInt(monster, "_callForBackupCount");
                break;
            case "Fabricator":
                string spawnedId = (MonsterValueReader.ReadObject(monster, "_lastSpawned") as MonsterModel)?.Id.Entry ?? string.Empty;
                enemy.AiRegs[0] = spawnedId switch
                {
                    "GUARDBOT" => 1,
                    "NOISEBOT" => 2,
                    "ZAPBOT"   => 3,
                    "STABBOT"  => 4,
                    _          => 0,
                };
                break;
            case "ToughEgg":
                if (MonsterValueReader.ReadBool(monster, "_isHatched"))
                    eFlags |= HotEnemyFlags.Hatched;
                break;
            case "TestSubject":
                enemy.AiRegs[0] = MonsterValueReader.ReadInt(monster, "_respawns");
                enemy.AiRegs[1] = MonsterValueReader.ReadInt(monster, "_extraMultiClawCount");
                break;
            case "Tunneler":
                if (MonsterValueReader.ReadBool(monster, "_isStunned"))
                    eFlags |= HotEnemyFlags.Stunned;
                break;
            case "LagavulinMatriarch":
            case "SlumberingBeetle":
                if (MonsterValueReader.ReadBool(monster, "_isAwake"))
                    eFlags |= HotEnemyFlags.Awake;
                break;
            case "Queen":
                if (MonsterValueReader.ReadBool(monster, "_hasAmalgamDied"))
                    eFlags |= HotEnemyFlags.AmalgamDied;
                break;
            case "KnowledgeDemon":
                enemy.AiRegs[0] = MonsterValueReader.ReadInt(monster, "_curseOfKnowledgeCounter");
                break;
            case "TheObscura":
                if (MonsterValueReader.ReadBool(monster, "_hasSummoned"))
                    eFlags |= HotEnemyFlags.HasSummoned;
                break;
        }

        enemy.Flags = eFlags;
        return enemy;
    }

    private static HotDeathPhase ComputeLiveDeathPhase(Creature creature, CombatState combat)
    {
        if (creature.CurrentHp > 0)
            return HotDeathPhase.None;

        string move = creature.Monster?.NextMove?.Id ?? string.Empty;
        if (move is "DEAD_MOVE" or "REATTACH_MOVE"
            && creature.HasPower<ReattachPower>()
            && combat.GetTeammatesOf(creature)
                .Where(other => !ReferenceEquals(other, creature) && other.HasPower<ReattachPower>())
                .All(static other => other.IsDead))
        {
            return HotDeathPhase.PermanentlyDead;
        }

        return move is "RESPAWN_MOVE" or "REVIVE_MOVE" or "DEAD_MOVE" or "REATTACH_MOVE"
            ? HotDeathPhase.Reviving
            : HotDeathPhase.PermanentlyDead;
    }

    private static int ImportPile(
        BuilderContext ctx,
        CardPile pile,
        ref CombatHotState hot,
        ref HotColdSidecar? rootCold,
        ref int cursor,
        ref ushort nextCardUid,
        Dictionary<CardModel, ushort> cardToUid)
    {
        IReadOnlyList<CardModel> cards = pile.Cards;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cursor >= HotLimits.MaxCards)
                throw new HotCapacityExceededException($"Total combat card count exceeds HotLimits.MaxCards ({HotLimits.MaxCards}).");

            CardModel liveCard = cards[i];
            ushort uid = nextCardUid++;
            HotCard hotCard = BuildCard(ctx, liveCard, uid, ref nextCardUid, ref rootCold);
            hot.Cards[cursor++] = hotCard;
            cardToUid[liveCard] = uid;
        }
        return cards.Count;
    }

    private static HotCard BuildCard(
        BuilderContext ctx,
        CardModel card,
        ushort cardUid,
        ref ushort nextCardUid,
        ref HotColdSidecar? rootCold)
    {
        ushort cardDefId = ctx.EnsureCardDefForLiveInstance(card, out short var0Delta, out short var1Delta);
        byte enchId = 0;
        byte enchStatus = 0;
        short enchAmt = 0;
        short enchPrivate = 0;

        if (card.Enchantment is { } enchantment)
        {
            enchId = ctx.EnsureEnchantmentDef(enchantment);
            enchStatus = (byte)enchantment.Status;
            enchAmt = (short)enchantment.Amount;
            if (enchantment is Momentum momentum)
                enchPrivate = (short)(int)MomentumExtraDamageField.GetValue(momentum)!;
        }

        byte afflId = 0;
        short afflAmt = 0;
        if (card.Affliction is { } affliction)
        {
            afflId = ctx.EnsureAfflictionDef(affliction);
            afflAmt = (short)affliction.Amount;
        }

        HotCardFlags flags = HotCardFlags.None;
        if (card.EnergyCost.CostsX) flags |= HotCardFlags.CostsX;
        if (card.HasStarCostX) flags |= HotCardFlags.HasStarCostX;
        if (card.ExhaustOnNextPlay) flags |= HotCardFlags.ExhaustOnNextPlay;
        if (card.IsSlyThisTurn) flags |= HotCardFlags.IsSlyThisTurn;
        if (card._hasSingleTurnRetain) flags |= HotCardFlags.SingleTurnRetain;
        if (card.DeckVersion != null) flags |= HotCardFlags.HasDeckVersion;
        if (card.HasBeenRemovedFromState) flags |= HotCardFlags.RemovedFromState;
        if (card.IsDupe) flags |= HotCardFlags.IsDupe;
        if (card.IsClone) flags |= HotCardFlags.IsClone;
        if (card is SovereignBlade blade && blade.CreatedThroughForge)
            flags |= HotCardFlags.SovereignBladeCreatedThroughForge;

        HotCard hotCard = new()
        {
            CardDefId = cardDefId,
            CardUid = cardUid,
            UpgradeLevel = (byte)Math.Clamp(card.CurrentUpgradeLevel, 0, byte.MaxValue),
            BaseEnergyCost = (sbyte)Math.Clamp(card.EnergyCost._base, sbyte.MinValue, sbyte.MaxValue),
            BaseStarCost = (sbyte)Math.Clamp(card.BaseStarCost, sbyte.MinValue, sbyte.MaxValue),
            BaseReplayCount = (byte)Math.Clamp(card.BaseReplayCount, 0, byte.MaxValue),
            ExtraReplayThisTurn = (byte)Math.Clamp(card.ExtraReplayThisTurn, 0, byte.MaxValue),
            LocalKeywordsMask = HotKeywords.FromEnumerable(card.GetKeywordsWithSources(KeywordSources.Local)),
            KeywordsAddedOutsideMask = HotKeywords.FromEnumerable(card._keywordsAddedFromOutside),
            KeywordsRemovedOutsideMask = HotKeywords.FromEnumerable(card._keywordsRemovedFromOutside),
            Flags = flags,
            EnchId = enchId,
            EnchStatus = enchStatus,
            EnchAmt = enchAmt,
            AfflId = afflId,
            AfflAmt = afflAmt,
            Var0Delta = var0Delta,
            Var1Delta = var1Delta,
            EnchPrivate = enchPrivate,
            CapturedEnergy = card.EnergyCost.CostsX ? (short)card.EnergyCost.CapturedXValue : (short)0,
            CardPrivate = CaptureCardPrivate(card),
        };

        // Energy cost local modifiers & temporary star costs via shared HotCostModifiers
        List<LocalCostModifier> energyMods = card.EnergyCost._localModifiers;
        for (int m = 0; m < energyMods.Count; m++)
        {
            LocalCostModifier mod = energyMods[m];
            HotCostModifiers.AddEnergyModifierExact(
                ref hotCard,
                ref rootCold,
                (short)mod.Amount,
                mod.Type == LocalCostType.Absolute,
                (int)mod.Expiration,
                mod.IsReduceOnly);
        }

        List<TemporaryCardCost> starMods = card._temporaryStarCosts;
        for (int s = 0; s < starMods.Count; s++)
        {
            TemporaryCardCost star = starMods[s];
            HotCostModifiers.AddTempStarCost(
                ref hotCard,
                ref rootCold,
                star.Cost,
                star.ClearsWhenTurnEnds,
                star.ClearsWhenCardIsPlayed);
        }

        return hotCard;
    }

    private static int CaptureCardPrivate(CardModel card)
        => card switch
        {
            Claw claw => (int)claw.ExtraDamageFromClawPlays,
            GeneticAlgorithm ga => ga.IncreasedBlock,
            Maul maul => (int)maul._extraDamageFromMaulPlays,
            MadScience ms => ((int)ms.TinkerTimeType & 0xFFFF) | (((int)ms.TinkerTimeRider & 0xFFFF) << 16),
            Rampage rampage => (int)rampage.ExtraDamageFromPlays,
            TheScythe scythe => scythe.IncreasedDamage,
            TheShot shot => (int)shot._cardsExhaustedByFlorescence,
            ShowOff showOff => ((short)showOff.CurrentAim & 0xFFFF) | (((short)showOff.IncreasedAim & 0xFFFF) << 16),
            _ => 0,
        };

    private static HotRngState FromRng(Rng rng)
        => HotRng.FromNative(rng.CaptureState());

    private static void CaptureHistoryAndMelodies(CombatState combatState, Player player, ref CombatHotState hot)
    {
        CombatHistory? history = CombatManager.Instance?.History;
        if (history == null)
            return;

        int statusCardsDrawn = 0;
        int nonHandDraws = 0;
        int totalCardsDrawn = 0;
        int zeroCostAttackStarts = 0;
        int cardPlayStarts = 0;
        int attackSkillStarts = 0;
        int attackPlayStarts = 0;
        int skillPlayStarts = 0;
        int powerPlayStarts = 0;
        int brightestFlameMaxHp = 0;
        int finishedPlays = 0;
        int etherealPlays = 0;
        int lightningChannels = 0;
        int unblockedHits = 0;
        int cardsGenerated = 0;
        int sadnessGenerated = 0;
        int cardsExhausted = 0;
        int cardsDiscarded = 0;
        int energySpent = 0;
        int starsGained = 0;
        int hasLostHpThisTurn = 0;
        int wasDoomAppliedThisTurn = 0;
        int ostyAttacks = 0;

        int[] melodyStarted = new int[3];
        int[] melodyFinished = new int[3];
        int melodyCardsFinished = 0;
        CardTag[] melodyTags = SimulatedCombatState.MelodyTags;

        foreach (CombatHistoryEntry entry in history.Entries)
        {
            bool thisTurn = entry.HappenedThisTurn(combatState);
            switch (entry)
            {
                case CardDrawnEntry drawn when ReferenceEquals(drawn.Actor.Player, player):
                    totalCardsDrawn++;
                    if (thisTurn)
                    {
                        if (!drawn.FromHandDraw)
                            nonHandDraws++;
                        if (drawn.Card.Type == CardType.Status)
                            statusCardsDrawn++;
                    }
                    break;

                case CardPlayStartedEntry started:
                    if (started.CardPlay.Card is BrightestFlame)
                        brightestFlameMaxHp += started.CardPlay.Card.DynamicVars.MaxHp.IntValue;
                    if (thisTurn && ReferenceEquals(started.CardPlay.Player, player))
                    {
                        cardPlayStarts++;
                        CardType cType = started.CardPlay.Card.Type;
                        if (cType == CardType.Attack)
                        {
                            attackPlayStarts++;
                            attackSkillStarts++;
                            if (started.CardPlay.Resources.EnergyValue == 0)
                                zeroCostAttackStarts++;
                        }
                        else if (cType == CardType.Skill)
                        {
                            skillPlayStarts++;
                            attackSkillStarts++;
                        }
                        else if (cType == CardType.Power)
                        {
                            powerPlayStarts++;
                        }

                        for (int m = 0; m < 3; m++)
                        {
                            if (started.CardPlay.Card.Tags.Contains(melodyTags[m]))
                                melodyStarted[m]++;
                        }
                    }
                    break;

                case CardPlayFinishedEntry finished:
                    finishedPlays++;
                    if (ReferenceEquals(finished.CardPlay.Player, player))
                    {
                        if (finished.WasEthereal)
                            etherealPlays++;
                        if (thisTurn)
                        {
                            bool anyMelody = false;
                            for (int m = 0; m < 3; m++)
                            {
                                if (finished.CardPlay.Card.Tags.Contains(melodyTags[m]))
                                {
                                    melodyFinished[m]++;
                                    anyMelody = true;
                                }
                            }
                            if (anyMelody)
                                melodyCardsFinished++;
                        }
                    }
                    break;

                case OrbChanneledEntry orb when ReferenceEquals(orb.Actor.Player, player):
                    if (orb.Orb is MegaCrit.Sts2.Core.Models.Orbs.LightningOrb)
                        lightningChannels++;
                    break;

                case DamageReceivedEntry dmg when ReferenceEquals(dmg.Receiver, player.Creature):
                    if (dmg.Result.UnblockedDamage > 0)
                    {
                        unblockedHits++;
                        if (thisTurn)
                            hasLostHpThisTurn = 1;
                    }
                    break;

                case CardGeneratedEntry gen when ReferenceEquals(gen.Creator, player):
                    cardsGenerated++;
                    if (gen.Card is MegaCrit.Sts2.Core.Models.Cards.Sadness)
                        sadnessGenerated++;
                    break;

                case CardExhaustedEntry exh when thisTurn && ReferenceEquals(exh.Actor, player.Creature):
                    cardsExhausted++;
                    break;

                case CardDiscardedEntry disc when thisTurn && ReferenceEquals(disc.Actor, player.Creature):
                    cardsDiscarded++;
                    break;

                case EnergySpentEntry energy when thisTurn && ReferenceEquals(energy.Actor.Player, player):
                    energySpent += energy.Amount;
                    break;

                case StarsModifiedEntry stars when thisTurn && ReferenceEquals(stars.Actor.Player, player) && stars.Amount > 0:
                    starsGained += stars.Amount;
                    break;

                case PowerReceivedEntry pwr when thisTurn && pwr.Power is DoomPower && ReferenceEquals(pwr.Applier, player.Creature):
                    wasDoomAppliedThisTurn = 1;
                    break;

                case CreatureAttackedEntry atk when thisTurn && player.Osty != null && ReferenceEquals(atk.Actor, player.Osty):
                    ostyAttacks++;
                    break;
            }
        }

        hot.Counters[(int)HotCounterSlot.StatusCardsDrawnThisTurn] = statusCardsDrawn;
        hot.Counters[(int)HotCounterSlot.ZeroCostAttackStartsThisTurn] = zeroCostAttackStarts;
        hot.Counters[(int)HotCounterSlot.CardPlayStartsThisTurn] = cardPlayStarts;
        hot.Counters[(int)HotCounterSlot.AttackSkillStartsThisTurn] = attackSkillStarts;
        hot.Counters[(int)HotCounterSlot.AttackPlayStartsThisTurn] = attackPlayStarts;
        hot.Counters[(int)HotCounterSlot.SkillPlayStartsThisTurn] = skillPlayStarts;
        hot.Counters[(int)HotCounterSlot.PowerPlayStartsThisTurn] = powerPlayStarts;
        hot.Counters[(int)HotCounterSlot.CardsExhaustedThisTurn] = cardsExhausted;
        hot.Counters[(int)HotCounterSlot.CardsDiscardedThisTurn] = cardsDiscarded;
        hot.Counters[(int)HotCounterSlot.EnergySpentThisTurn] = energySpent;
        hot.Counters[(int)HotCounterSlot.StarsGainedThisTurn] = starsGained;
        hot.Counters[(int)HotCounterSlot.NonHandDrawsThisTurn] = nonHandDraws;
        hot.Counters[(int)HotCounterSlot.HasLostHpThisTurn] = hasLostHpThisTurn;
        hot.Counters[(int)HotCounterSlot.WasDoomAppliedThisTurn] = wasDoomAppliedThisTurn;
        hot.Counters[(int)HotCounterSlot.AttacksPlayedThisTurn] = attackPlayStarts;
        hot.Counters[(int)HotCounterSlot.SkillsPlayedThisTurn] = skillPlayStarts;
        hot.Counters[(int)HotCounterSlot.CardsPlayedThisTurn] = cardPlayStarts;
        hot.Counters[(int)HotCounterSlot.OstyAttacksThisTurn] = ostyAttacks;
        hot.Counters[(int)HotCounterSlot.SadnessGeneratedBefore] = sadnessGenerated;

        hot.BrightestFlameMaxHpSpent = brightestFlameMaxHp;
        hot.HistoryFinishedPlays = finishedPlays;
        hot.HistoryEtherealPlays = etherealPlays;
        hot.HistoryLightningChannels = lightningChannels;
        hot.HistoryUnblockedHitsReceived = unblockedHits;
        hot.HistoryCardsDrawn = totalCardsDrawn;
        hot.HistoryCardsGenerated = cardsGenerated;

        for (int m = 0; m < 3; m++)
        {
            hot.Fool.MelodyStarted[m] = (byte)Math.Clamp(melodyStarted[m], 0, byte.MaxValue);
            hot.Fool.MelodyFinished[m] = (byte)Math.Clamp(melodyFinished[m], 0, byte.MaxValue);
        }
        hot.Fool.MelodyCardsFinished = (byte)Math.Clamp(melodyCardsFinished, 0, byte.MaxValue);
    }

    private static HotRelicState CaptureLiveRelicState(RelicModel relic, HotRelicSpecialKind kind)
        => relic switch
        {
            ConductorsBaton baton => new HotRelicState { Current = baton._triggeredThisTurn ? 1 : 0 },
            MegaCrit.Sts2.Core.Models.Relics.Wuppy wuppy => new HotRelicState { Current = wuppy.CurrentHp, PreviousOrAux = wuppy.MaxHp },
            Byrdpip byrd => new HotRelicState { Current = byrd.SwoopsPlayed },
            ArtOfWar art => new HotRelicState
            {
                Current = (bool)ArtOfWarThisTurnField.GetValue(art)! ? 1 : 0,
                PreviousOrAux = (bool)ArtOfWarLastTurnField.GetValue(art)! ? 1 : 0,
            },
            BeltBuckle buckle => new HotRelicState { Current = (bool)BeltBuckleDexterityAppliedField.GetValue(buckle)! ? 1 : 0 },
            BoneTea tea => new HotRelicState { Current = tea.CombatsLeft },
            EmotionChip chip => CaptureEmotionChipState(chip),
            FakeHappyFlower flower => new HotRelicState { Current = flower.TurnsSeen },
            FakeVenerableTeaSet tea => new HotRelicState { Current = tea.GainEnergyInNextCombat ? 1 : 0 },
            HappyFlower flower => new HotRelicState { Current = flower.TurnsSeen },
            MiniRegent regent => new HotRelicState { Current = regent._usedThisTurn ? 1 : 0 },
            GalacticDust dust => new HotRelicState { Current = dust.StarsSpent },
            PaelsEye eye => new HotRelicState
            {
                Current = eye._usedThisCombat ? 1 : 0,
                PreviousOrAux = eye._wasOwnerPartOfLastPlayerTurn ? 1 : 0,
            },
            PaelsTears tears => new HotRelicState { Current = tears._hadLeftoverEnergy ? 1 : 0 },
            RainbowRing ring => new HotRelicState
            {
                Current = (ring._attacksPlayedThisTurn & 0xFFFF) | ((ring._skillsPlayedThisTurn & 0xFFFF) << 16),
                PreviousOrAux = (ring._powersPlayedThisTurn & 0xFFFF) | ((ring._activationCountThisTurn & 0xFFFF) << 16),
            },
            RedSkull skull => new HotRelicState { Current = skull._strengthApplied ? 1 : 0 },
            Pendulum pendulum => new HotRelicState { Current = pendulum.TurnsSeen },
            Pocketwatch pocketwatch => new HotRelicState
            {
                Current = pocketwatch._cardsPlayedThisTurn,
                PreviousOrAux = pocketwatch._cardsPlayedLastTurn,
            },
            PollinousCore core => new HotRelicState { Current = core.TurnsSeen },
            RuinedHelmet helmet => new HotRelicState { Current = (bool)RuinedHelmetUsedField.GetValue(helmet)! ? 1 : 0 },
            UnsettlingLamp lamp => new HotRelicState { Current = (bool)UnsettlingLampFinishedField.GetValue(lamp)! ? 1 : 0 },
            VenerableTeaSet tea => new HotRelicState { Current = tea.GainEnergyInNextCombat ? 1 : 0 },
            BeatingRemnant remnant => new HotRelicState { Current = (int)remnant._damageReceivedThisTurn },
            BrilliantScarf scarf => new HotRelicState { Current = scarf._cardsPlayedThisTurn },
            BurningSticks sticks => new HotRelicState { Current = sticks.WasUsedThisCombat ? 1 : 0 },
            CentennialPuzzle puzzle => new HotRelicState { Current = puzzle.UsedThisCombat ? 1 : 0 },
            DemonTongue tongue => new HotRelicState { Current = tongue._triggeredThisTurn ? 1 : 0 },
            IronClub club => new HotRelicState { Current = club.CardsPlayed },
            JossPaper paper => new HotRelicState { Current = paper.CardsExhausted, PreviousOrAux = paper._etherealCount },
            Kunai kunai => new HotRelicState { Current = kunai._attacksPlayedThisTurn },
            Kusarigama kusa => new HotRelicState { Current = kusa._attacksPlayedThisTurn },
            LetterOpener opener => new HotRelicState { Current = opener._skillsPlayedThisTurn },
            LizardTail tail => new HotRelicState { Current = tail.WasUsed ? 1 : 0 },
            Metronome metro => new HotRelicState { Current = metro._orbsChanneled },
            MusicBox box => new HotRelicState { Current = box._wasUsedThisTurn ? 1 : 0 },
            Nunchaku nunchaku => new HotRelicState { Current = nunchaku.AttacksPlayed },
            OrnamentalFan fan => new HotRelicState { Current = fan._attacksPlayedThisTurn },
            PaelsLegion legion => new HotRelicState
            {
                Current = legion._cooldown,
                PreviousOrAux = (legion._triggeredBlockLastTurn ? 1 : 0) | (legion._affectedCardPlay != null ? 2 : 0),
            },
            PenNib penNib => new HotRelicState { Current = penNib.AttacksPlayed },
            Permafrost frost => new HotRelicState { Current = frost._activatedThisCombat ? 1 : 0 },
            Regalite regalite => new HotRelicState { Current = regalite._usedThisTurn ? 1 : 0 },
            Shuriken shuriken => new HotRelicState { Current = shuriken._attacksPlayedThisTurn },
            ThrowingAxe axe => new HotRelicState { Current = axe._usedThisCombat ? 1 : 0 },
            TuningFork fork => new HotRelicState { Current = fork.SkillsPlayed },
            Vambrace vambrace => new HotRelicState { Current = vambrace._blockGainedThisCombat ? 1 : 0 },
            _ => default,
        };

    private static HotRelicState CaptureEmotionChipState(EmotionChip chip)
    {
        CombatHistory? history = CombatManager.Instance?.History;
        if (history == null)
            return default;

        Creature receiver = chip.Owner.Creature;
        bool lastPlayerTurn = false;
        bool thisTurn = false;
        foreach (CombatHistoryEntry entry in history.Entries)
        {
            if (entry is not DamageReceivedEntry damage
                || !ReferenceEquals(damage.Receiver, receiver)
                || damage.Result.WasFullyBlocked)
            {
                continue;
            }
            if (!lastPlayerTurn && damage.HappenedLastPlayerTurn(chip.Owner))
                lastPlayerTurn = true;
            if (!thisTurn && damage.HappenedThisTurn(receiver.CombatState))
                thisTurn = true;
            if (lastPlayerTurn && thisTurn)
                break;
        }
        return new HotRelicState
        {
            Current = lastPlayerTurn ? 1 : 0,
            PreviousOrAux = thisTurn ? 1 : 0,
        };
    }

    private static byte[][] BuildRelicHooksByBit(
        List<HotRelicDef> relics,
        List<ushort> activeRelicDefIds,
        out ulong staticRelicHookMask)
    {
        staticRelicHookMask = 0;
        byte[][] table = new byte[64][];
        List<byte>[] buckets = new List<byte>[64];

        for (int i = 0; i < activeRelicDefIds.Count; i++)
        {
            ulong mask = (ulong)relics[activeRelicDefIds[i]].HookMask;
            staticRelicHookMask |= mask;
            while (mask != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(mask);
                (buckets[bit] ??= []).Add((byte)i);
                mask &= mask - 1;
            }
        }

        for (int b = 0; b < 64; b++)
            table[b] = buckets[b]?.ToArray() ?? [];

        return table;
    }

    private static HotCoverageManifest BuildCoverageManifest(
        BuilderContext ctx,
        CombatState combat,
        Player player,
        in CombatHotState hot,
        CombatRulesSnapshot rulesSnapshot,
        List<HotRootPetSnapshot> rootPets)
    {
        bool[] supportedCards = new bool[ctx.Cards.Count];
        for (int i = 1; i < ctx.Cards.Count; i++)
            supportedCards[i] = HotRules.IsPhase1SupportedCard(ctx.Cards[i].Entry);

        bool[] supportedPowers = new bool[ctx.Powers.Count];
        for (int i = 1; i < ctx.Powers.Count; i++)
            supportedPowers[i] = HotRules.IsPhase1SupportedPower(ctx.Powers[i].Entry);

        bool[] supportedRelics = new bool[ctx.Relics.Count];
        for (int i = 1; i < ctx.Relics.Count; i++)
            supportedRelics[i] = HotRules.IsPhase1SupportedRelic(ctx.Relics[i].Entry);

        bool[] supportedMonsters = new bool[ctx.Monsters.Count];
        for (int i = 1; i < ctx.Monsters.Count; i++)
            supportedMonsters[i] = HotRules.IsPhase1SupportedMonsterType(ctx.Monsters[i].TypeName);

        List<string> reasons = [];

        if (rulesSnapshot.DominionId != null)
            reasons.Add($"dominion:{rulesSnapshot.DominionId}");
        if (hot.OrbCount > 0 || hot.OrbCapacity > 0)
            reasons.Add("orbs");
        if (hot.ArrowCount > 0)
            reasons.Add("arrows");
        if ((hot.CombatFlags & (HotCombatFlags.HasOsty | HotCombatFlags.HasWuppy)) != 0 || rootPets.Count > 0)
            reasons.Add("pets");

        for (int s = 0; s < hot.PotionSlotCount; s++)
        {
            if (hot.Potions[s] != 0)
                reasons.Add($"potion:{ctx.Potions[hot.Potions[s]].Entry}");
        }

        int totalCards = hot.TotalCardCount;
        HashSet<string> reported = new(StringComparer.Ordinal);
        for (int i = 0; i < totalCards; i++)
        {
            HotCard c = hot.Cards[i];
            HotCardDef def = ctx.Cards[c.CardDefId];
            if (!supportedCards[c.CardDefId] && reported.Add("card:" + def.Entry))
                reasons.Add("card:" + def.Entry);
            if (c.EnchId != 0 && reported.Add("ench:" + ctx.Enchantments[c.EnchId].Entry))
                reasons.Add("ench:" + ctx.Enchantments[c.EnchId].Entry);
            if (c.AfflId != 0 && reported.Add("affl:" + ctx.Afflictions[c.AfflId].Entry))
                reasons.Add("affl:" + ctx.Afflictions[c.AfflId].Entry);
        }

        foreach (RelicModel relic in player.Relics)
        {
            if (relic.IsMelted)
                continue;
            if (!HotRules.IsPhase1SupportedRelic(relic.Id.Entry) && reported.Add("relic:" + relic.Id.Entry))
                reasons.Add("relic:" + relic.Id.Entry);
        }

        for (int i = 0; i < hot.PowerCount; i++)
        {
            HotPowerDef pDef = ctx.Powers[hot.Powers[i].PowerDefId];
            if (!supportedPowers[hot.Powers[i].PowerDefId] && reported.Add("power:" + pDef.Entry))
                reasons.Add("power:" + pDef.Entry);
        }

        for (int i = 0; i < hot.EnemyCount; i++)
        {
            ushort mId = hot.Enemies[i].MonsterDefId;
            if (mId == 0 || !supportedMonsters[mId])
            {
                string label = mId == 0 ? "null" : ctx.Monsters[mId].Entry;
                if (reported.Add("monster:" + label))
                    reasons.Add("monster:" + label);
            }
        }

        return new HotCoverageManifest
        {
            SupportedCardDefs = supportedCards,
            SupportedPowerDefs = supportedPowers,
            SupportedRelicDefs = supportedRelics,
            SupportedMonsterDefs = supportedMonsters,
            IsRootFullySupported = reasons.Count == 0,
            UnsupportedReasons = reasons.ToArray(),
        };
    }

    private sealed class BuilderContext
    {
        public readonly List<HotCardDef> Cards = [];
        public readonly List<HotPowerDef> Powers = [];
        public readonly List<HotRelicDef> Relics = [];
        public readonly List<HotPotionDef> Potions = [];
        public readonly List<HotOrbDef> Orbs = [];
        public readonly List<HotEnchantmentDef> Enchantments = [];
        public readonly List<HotAfflictionDef> Afflictions = [];
        public readonly List<HotMonsterDef> Monsters = [];
        public readonly List<HotCardEffectOp> EffectOps = [];
        public readonly List<HotMonsterMachine> MonsterMachines = [];

        public readonly Dictionary<string, ushort> CardDefIdByEntry = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ushort> PowerDefIdByEntry = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ushort> RelicDefIdByEntry = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ushort> PotionDefIdByEntry = new(StringComparer.Ordinal);
        public readonly Dictionary<string, byte> OrbDefIdByEntry = new(StringComparer.Ordinal);
        public readonly Dictionary<string, byte> EnchDefIdByEntry = new(StringComparer.Ordinal);
        public readonly Dictionary<string, byte> AfflDefIdByEntry = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ushort> MonsterDefIdByEntry = new(StringComparer.Ordinal);

        private List<HotCard>? _detachedCards;
        private List<HotMonsterStateLogOverflow>? _monsterLogs;

        public BuilderContext()
        {
            // Reserve index 0 as sentinel "None" across every definition table.
            Cards.Add(new HotCardDef
            {
                DefId = 0, CanonicalDefId = 0, Entry = string.Empty,
                Type = CardType.None, Target = TargetType.None,
                BaseCost = 0, UpgradedCost = 0, BaseStarCost = -1, BaseKeywords = 0,
                BaseDamage = 0, UpgradedDamage = 0, BaseBlock = 0, UpgradedBlock = 0,
                BaseMagic = 0, UpgradedMagic = 0, BaseHits = 1, UpgradedHits = 1,
                HookMask = HotHookMask.None, CanHealPlayer = false, CanGenerateUnknown = false,
                SetupWeight = 0, OpStart = 0, OpCount = 0, CalcVarKind = 0, CustomHandlerId = 0,
                OrderedVarNames = [], BaseVarValues = [], UpgradedVarValues = [], IsAllowedInBlitz = true,
            });
            Powers.Add(new HotPowerDef
            {
                DefId = 0, Entry = string.Empty, Type = PowerType.None, HookMask = HotHookMask.None,
                CanHealPlayer = false, HasRelevantTurnStartAmount = false, HasRelevantSkipDurationTick = false,
                SetupWeight = 0, OrderedVarNames = [], DefaultVarValues = [],
            });
            Relics.Add(new HotRelicDef
            {
                DefId = 0, Entry = string.Empty, HookMask = HotHookMask.None,
                IsStateful = false, IsTracked = false, CanHealPlayer = false, CanGenerateUnknown = false,
            });
            Potions.Add(new HotPotionDef
            {
                DefId = 0, Entry = string.Empty, Target = TargetType.None,
                CanHealPlayer = false, CanGenerateUnknown = false,
            });
            Orbs.Add(new HotOrbDef { DefId = 0, Entry = string.Empty, BasePassiveVal = 0, BaseEvokeVal = 0 });
            Enchantments.Add(new HotEnchantmentDef { DefId = 0, Entry = string.Empty, HasMomentumExtraDamage = false });
            Afflictions.Add(new HotAfflictionDef { DefId = 0, Entry = string.Empty });
            Monsters.Add(new HotMonsterDef
            {
                DefId = 0, Entry = string.Empty, TypeName = string.Empty,
                StateKind = HotMonsterStateKind.None, MachineIndex = -1,
            });
        }

        public void PreRegisterPhase1Powers()
        {
            EnsurePowerDef(ModelDb.Power<VulnerablePower>());
            EnsurePowerDef(ModelDb.Power<WeakPower>());
            EnsurePowerDef(ModelDb.Power<FrailPower>());
            EnsurePowerDef(ModelDb.Power<StrengthPower>());
            EnsurePowerDef(ModelDb.Power<DexterityPower>());
            EnsurePowerDef(ModelDb.Power<PlatingPower>());
            EnsurePowerDef(ModelDb.Power<BarricadePower>());
            EnsurePowerDef(ModelDb.Power<ArtifactPower>());
            EnsurePowerDef(ModelDb.Power<ThornsPower>());
            EnsurePowerDef(ModelDb.Power<RitualPower>());
        }

        public ushort EnsurePowerDefByEntry(string entry)
        {
            if (PowerDefIdByEntry.TryGetValue(entry, out ushort existing))
                return existing;

            ushort defId = (ushort)Powers.Count;
            PowerDefIdByEntry[entry] = defId;
            Powers.Add(new HotPowerDef
            {
                DefId = defId,
                Entry = entry,
                Type = entry is "VULNERABLE_POWER" or "WEAK_POWER" or "FRAIL_POWER" ? PowerType.Debuff : PowerType.Buff,
                HookMask = HotRules.GetPowerHookMask(entry),
                CanHealPlayer = false,
                HasRelevantTurnStartAmount = false,
                HasRelevantSkipDurationTick = entry is "VULNERABLE_POWER" or "WEAK_POWER" or "FRAIL_POWER",
                SetupWeight = 0,
                OrderedVarNames = [],
                DefaultVarValues = [],
            });
            return defId;
        }

        public ushort EnsurePowerDef(PowerModel power)
        {
            string entry = power.Id.Entry;
            ExtractSemanticVars(power, out string[] varNames, out decimal[] varValues, out string?[]? varStrings);

            if (PowerDefIdByEntry.TryGetValue(entry, out ushort existingId))
            {
                HotPowerDef existing = Powers[existingId];
                if (VarsMatchExact(existing.OrderedVarNames, existing.DefaultVarValues, existing.OrderedVarStrings,
                        varNames, varValues, varStrings))
                {
                    return existingId;
                }
            }

            ushort defId = (ushort)Powers.Count;
            HotPowerDef def = new()
            {
                DefId = defId,
                Entry = entry,
                Type = power.Type,
                HookMask = HotRules.GetPowerHookMask(entry),
                CanHealPlayer = false,
                HasRelevantTurnStartAmount = power is DrawCardsNextTurnPower or SummonNextTurnPower or HelloWorldPower or GodByrdPower,
                HasRelevantSkipDurationTick = PowerLifecycleSupport.UsesNativeDurationSkip(power.GetType()),
                SetupWeight = 0,
                OrderedVarNames = varNames,
                DefaultVarValues = varValues,
                OrderedVarStrings = varStrings,
                SpecialKind = ClassifyPowerSpecial(power),
                IsInstanced = power.InstanceType != PowerInstanceType.None,
                StackType = power.StackType,
            };
            Powers.Add(def);
            PowerDefIdByEntry.TryAdd(entry, defId);
            return defId;
        }

        public ushort EnsureCardDefForLiveInstance(CardModel liveCard, out short var0Delta, out short var1Delta)
        {
            ushort canonicalId = EnsureCanonicalCardDef(liveCard);
            HotCardDef canonical = Cards[canonicalId];

            ExtractSemanticVars(liveCard, out string[] liveNames, out decimal[] liveValues, out string?[]? liveStrings);

            string? dupeKey = liveCard.IsDupe
                ? CardChoiceSupport.ChoiceCardKey(liveCard.DupeOf
                    ?? throw new InvalidOperationException("Dupe has no source card."))
                : null;

            bool canReuseCanonical = dupeKey == null
                && liveCard.CurrentUpgradeLevel <= 1
                && canonical.OrderedVarNames.Length == liveNames.Length
                && StringsEqual(canonical.OrderedVarStrings, liveStrings);

            short d0 = 0;
            short d1 = 0;

            if (canReuseCanonical)
            {
                decimal[] refValues = liveCard.CurrentUpgradeLevel > 0
                    ? canonical.UpgradedVarValues
                    : canonical.BaseVarValues;

                for (int i = 0; i < liveNames.Length; i++)
                {
                    if (!string.Equals(canonical.OrderedVarNames[i], liveNames[i], StringComparison.Ordinal))
                    {
                        canReuseCanonical = false;
                        break;
                    }

                    decimal expected = refValues[i];
                    decimal actual = liveValues[i];
                    if (actual == expected && DecimalScaleMatches(actual, expected))
                        continue;

                    if (i == canonical.Var0Index && TryComputeShortDelta(expected, actual, out short delta0))
                    {
                        d0 = delta0;
                        continue;
                    }
                    if (i == canonical.Var1Index && TryComputeShortDelta(expected, actual, out short delta1))
                    {
                        d1 = delta1;
                        continue;
                    }

                    canReuseCanonical = false;
                    break;
                }
            }

            if (canReuseCanonical)
            {
                var0Delta = d0;
                var1Delta = d1;
                return canonicalId;
            }

            // Mint a root-specialized variant def preserving exact live DynamicVars / DupeSource with zero cold sidecar.
            var0Delta = 0;
            var1Delta = 0;
            ushort variantId = (ushort)Cards.Count;
            ExtractCardScalars(liveNames, liveValues, out short dmg, out short blk, out short magic, out byte hits, out byte v0, out byte v1);

            HotCardDef variant = new()
            {
                DefId = variantId,
                CanonicalDefId = canonicalId,
                Entry = canonical.Entry,
                Type = liveCard.Type,
                Target = liveCard.TargetType,
                BaseCost = canonical.BaseCost,
                UpgradedCost = canonical.UpgradedCost,
                BaseStarCost = canonical.BaseStarCost,
                BaseKeywords = canonical.BaseKeywords,
                BaseDamage = dmg,
                UpgradedDamage = dmg,
                BaseBlock = blk,
                UpgradedBlock = blk,
                BaseMagic = magic,
                UpgradedMagic = magic,
                BaseHits = hits,
                UpgradedHits = hits,
                HookMask = canonical.HookMask,
                CanHealPlayer = canonical.CanHealPlayer,
                CanGenerateUnknown = canonical.CanGenerateUnknown,
                SetupWeight = canonical.SetupWeight,
                OpStart = canonical.OpStart,
                OpCount = canonical.OpCount,
                CalcVarKind = canonical.CalcVarKind,
                CustomHandlerId = canonical.CustomHandlerId,
                OrderedVarNames = liveNames,
                BaseVarValues = liveValues,
                UpgradedVarValues = liveValues,
                OrderedVarStrings = liveStrings,
                Var0Index = v0,
                Var1Index = v1,
                IsAllowedInBlitz = liveCard.IsAllowedInBlitz,
                PrivateKind = canonical.PrivateKind,
                IsStrikeTag = canonical.IsStrikeTag,
                MelodyTagMask = canonical.MelodyTagMask,
                RootDupeSourceKey = dupeKey,
            };
            Cards.Add(variant);
            return variantId;
        }

        private ushort EnsureCanonicalCardDef(CardModel sample)
        {
            string entry = sample.Id.Entry;
            if (CardDefIdByEntry.TryGetValue(entry, out ushort existing))
                return existing;

            ushort defId = (ushort)Cards.Count;
            CardDefIdByEntry[entry] = defId;

            CardModel canonicalModel = sample.IsCanonical ? sample : ModelDb.GetById<CardModel>(sample.Id);
            CardModel baseModel = canonicalModel.IsCanonical ? canonicalModel.ToMutable() : sample;

            ExtractSemanticVars(baseModel, out string[] baseNames, out decimal[] baseValues, out string?[]? baseStrings);
            ExtractCardScalars(baseNames, baseValues, out short baseDmg, out short baseBlk, out short baseMagic, out byte baseHits, out byte v0Idx, out byte v1Idx);

            sbyte baseCost = (sbyte)Math.Clamp(baseModel.EnergyCost._base, sbyte.MinValue, sbyte.MaxValue);
            sbyte upgCost = baseCost;
            decimal[] upgValues = baseValues;
            short upgDmg = baseDmg;
            short upgBlk = baseBlk;
            short upgMagic = baseMagic;
            byte upgHits = baseHits;

            if (canonicalModel.IsCanonical && canonicalModel.MaxUpgradeLevel > 0)
            {
                CardModel upgModel = canonicalModel.ToMutable();
                upgModel.UpgradeInternal();
                ExtractSemanticVars(upgModel, out string[] upgNames, out decimal[] upgVals, out _);
                upgCost = (sbyte)Math.Clamp(upgModel.EnergyCost._base, sbyte.MinValue, sbyte.MaxValue);
                if (upgNames.Length == baseNames.Length)
                {
                    upgValues = upgVals;
                    ExtractCardScalars(upgNames, upgVals, out upgDmg, out upgBlk, out upgMagic, out upgHits, out _, out _);
                }
            }

            HotRules.CompilePhase1CardBytecode(
                entry,
                defId,
                EnsurePowerDefByEntry,
                EffectOps,
                out int opStart,
                out byte opCount,
                out ushort customHandlerId);

            byte melodyMask = 0;
            bool isStrike = false;
            foreach (CardTag tag in sample.Tags)
            {
                if (tag == CardTag.Strike) isStrike = true;
                else if (tag == CardTag.Wah) melodyMask |= 1;
                else if (tag == CardTag.Cho) melodyMask |= 2;
                else if (tag == CardTag.Ki) melodyMask |= 4;
            }

            HotCardDef def = new()
            {
                DefId = defId,
                CanonicalDefId = defId,
                Entry = entry,
                Type = sample.Type,
                Target = sample.TargetType,
                BaseCost = baseCost,
                UpgradedCost = upgCost,
                BaseStarCost = (sbyte)Math.Clamp(baseModel.BaseStarCost, sbyte.MinValue, sbyte.MaxValue),
                BaseKeywords = (byte)HotKeywords.FromEnumerable(baseModel.GetKeywordsWithSources(KeywordSources.Local)),
                BaseDamage = baseDmg,
                UpgradedDamage = upgDmg,
                BaseBlock = baseBlk,
                UpgradedBlock = upgBlk,
                BaseMagic = baseMagic,
                UpgradedMagic = upgMagic,
                BaseHits = baseHits,
                UpgradedHits = upgHits,
                HookMask = HotHookMask.None,
                CanHealPlayer = false,
                CanGenerateUnknown = false,
                SetupWeight = 0,
                OpStart = opStart,
                OpCount = opCount,
                CalcVarKind = 0,
                CustomHandlerId = customHandlerId,
                OrderedVarNames = baseNames,
                BaseVarValues = baseValues,
                UpgradedVarValues = upgValues,
                OrderedVarStrings = baseStrings,
                Var0Index = v0Idx,
                Var1Index = v1Idx,
                IsAllowedInBlitz = sample.IsAllowedInBlitz,
                PrivateKind = ClassifyCardPrivate(sample),
                IsStrikeTag = isStrike,
                MelodyTagMask = melodyMask,
            };

            Cards.Add(def);
            return defId;
        }

        public ushort EnsureRelicDef(RelicModel relic)
        {
            string entry = relic.Id.Entry;
            if (RelicDefIdByEntry.TryGetValue(entry, out ushort existing))
                return existing;

            ushort defId = (ushort)Relics.Count;
            RelicDefIdByEntry[entry] = defId;

            HotRelicSpecialKind special = ClassifyRelicSpecial(relic, out bool isStateful, out bool isTracked);
            short primaryVal = 0;
            if (relic is BurningBlood bb && bb.DynamicVars.TryGetValue("Heal", out DynamicVar? healVar))
                primaryVal = (short)healVar.IntValue;

            Relics.Add(new HotRelicDef
            {
                DefId = defId,
                Entry = entry,
                HookMask = HotRules.GetRelicHookMask(entry),
                IsStateful = isStateful,
                IsTracked = isTracked,
                CanHealPlayer = relic is BurningBlood or BlackBlood or MeatOnTheBone or BloodVial,
                CanGenerateUnknown = false,
                SpecialKind = special,
                PrimaryValue = primaryVal,
            });
            return defId;
        }

        public ushort EnsurePotionDef(PotionModel potion)
        {
            string entry = potion.Id.Entry;
            if (PotionDefIdByEntry.TryGetValue(entry, out ushort existing))
                return existing;

            ushort defId = (ushort)Potions.Count;
            PotionDefIdByEntry[entry] = defId;
            Potions.Add(new HotPotionDef
            {
                DefId = defId,
                Entry = entry,
                Target = potion.TargetType,
                CanHealPlayer = false,
                CanGenerateUnknown = false,
            });
            return defId;
        }

        public byte EnsureOrbDef(OrbModel orb)
        {
            string entry = orb.Id.Entry;
            if (OrbDefIdByEntry.TryGetValue(entry, out byte existing))
                return existing;

            byte defId = (byte)Orbs.Count;
            OrbDefIdByEntry[entry] = defId;
            Orbs.Add(new HotOrbDef
            {
                DefId = defId,
                Entry = entry,
                BasePassiveVal = (short)orb.PassiveVal,
                BaseEvokeVal = (int)orb.EvokeVal,
            });
            return defId;
        }

        public byte EnsureEnchantmentDef(EnchantmentModel enchantment)
        {
            string entry = enchantment.Id.Entry;
            if (EnchDefIdByEntry.TryGetValue(entry, out byte existing))
                return existing;

            byte defId = (byte)Enchantments.Count;
            EnchDefIdByEntry[entry] = defId;
            Enchantments.Add(new HotEnchantmentDef
            {
                DefId = defId,
                Entry = entry,
                HasMomentumExtraDamage = enchantment is Momentum,
            });
            return defId;
        }

        public byte EnsureAfflictionDef(AfflictionModel affliction)
        {
            string entry = affliction.Id.Entry;
            if (AfflDefIdByEntry.TryGetValue(entry, out byte existing))
                return existing;

            byte defId = (byte)Afflictions.Count;
            AfflDefIdByEntry[entry] = defId;
            Afflictions.Add(new HotAfflictionDef
            {
                DefId = defId,
                Entry = entry,
            });
            return defId;
        }

        public ushort RegisterEnemyMonster(MonsterModel monster, out HotMonsterMachine? compiledMachine)
        {
            ushort defId = (ushort)Monsters.Count;
            string entry = monster.Id.Entry;
            string typeName = monster.GetType().Name;
            IReadOnlyDictionary<string, int> staticInts = MonsterMoveEffects.CaptureStaticIntValues(monster);
            int testSubjectBaseMultiClaw = typeName == "TestSubject"
                ? MonsterValueReader.ReadInt(monster, "BaseMultiClawCount")
                : 0;

            int machineIndex = -1;
            compiledMachine = null;
            if (monster.MoveStateMachine is { } liveMachine)
            {
                compiledMachine = CompileMonsterMachine(defId, monster, liveMachine, staticInts);
                machineIndex = MonsterMachines.Count;
                MonsterMachines.Add(compiledMachine);
            }

            HotMonsterDef def = new()
            {
                DefId = defId,
                Entry = entry,
                TypeName = typeName,
                StateKind = ClassifyMonsterState(typeName),
                MachineIndex = machineIndex,
                StaticInts = staticInts,
                TestSubjectBaseMultiClawCount = testSubjectBaseMultiClaw,
            };
            Monsters.Add(def);
            MonsterDefIdByEntry.TryAdd(entry, defId);
            return defId;
        }

        private HotMonsterMachine CompileMonsterMachine(
            ushort monsterDefId,
            MonsterModel monster,
            MonsterMoveStateMachine liveMachine,
            IReadOnlyDictionary<string, int> staticInts)
        {
            string typeName = monster.GetType().Name;

            // Collect all known state IDs in deterministic order (machine states + NextMove + StateLog extras).
            List<string> orderedIds = new(liveMachine.States.Count + 2);
            Dictionary<string, short> idToNode = new(StringComparer.Ordinal);

            void AddId(string id)
            {
                if (!idToNode.ContainsKey(id))
                {
                    idToNode[id] = (short)orderedIds.Count;
                    orderedIds.Add(id);
                }
            }

            foreach (string id in liveMachine.States.Keys.OrderBy(static k => k, StringComparer.Ordinal))
                AddId(id);
            if (monster.NextMove is { } nm)
                AddId(nm.Id);
            foreach (MonsterState logged in liveMachine.StateLog)
                AddId(logged.Id);

            HotMonsterNode[] nodes = new HotMonsterNode[orderedIds.Count];
            for (int i = 0; i < orderedIds.Count; i++)
            {
                string id = orderedIds[i];
                liveMachine.States.TryGetValue(id, out MonsterState? mState);
                if (mState == null && monster.NextMove?.Id == id)
                    mState = monster.NextMove;

                if (mState is MoveState move)
                {
                    int baseDamage = 0;
                    int repeats = 0;
                    foreach (AbstractIntent intent in move.Intents)
                    {
                        if (intent is AttackIntent attack)
                        {
                            baseDamage = IntentForecaster.CaptureBaseDamage(attack, monster, move.Id);
                            repeats = Math.Max(1, attack.Repeats);
                            break;
                        }
                    }

                    string? followUpId = move.FollowUpState?.Id ?? move.FollowUpStateId;
                    short nextNode = followUpId != null && idToNode.TryGetValue(followUpId, out short fId)
                        ? fId
                        : (short)-1;

                    HotMonsterMoveEffect[] effects = CompilePhase1MonsterMoveEffects(typeName, move.Id, staticInts);

                    nodes[i] = new HotMonsterNode
                    {
                        NodeId = (short)i,
                        MoveId = id,
                        NodeKind = HotMonsterNodeKind.Move,
                        BaseDamage = baseDamage,
                        Repeats = repeats,
                        NextNode = nextNode,
                        BranchTargets = [],
                        BranchWeights = [],
                        BranchRepeatTypes = [],
                        BranchMaxTimes = [],
                        BranchCooldowns = [],
                        Cooldown = 0,
                        MustPerformOnceBeforeRepeat = move.MustPerformOnceBeforeTransitioning,
                        CannotRepeat = false,
                        ConditionalKind = HotMonsterConditionalKind.None,
                        MoveEffects = effects,
                    };
                }
                else if (mState is RandomBranchState random)
                {
                    int count = random.States.Count;
                    short[] targets = new short[count];
                    float[] weights = new float[count];
                    byte[] repeatTypes = new byte[count];
                    byte[] maxTimes = new byte[count];
                    byte[] cooldowns = new byte[count];
                    int maxCooldown = 0;
                    bool anyCannotRepeat = false;

                    for (int b = 0; b < count; b++)
                    {
                        RandomBranchState.StateWeight sw = random.States[b];
                        targets[b] = idToNode.TryGetValue(sw.stateId, out short tId) ? tId : (short)-1;
                        weights[b] = sw.GetWeight();
                        repeatTypes[b] = (byte)sw.repeatType;
                        maxTimes[b] = (byte)Math.Clamp(sw.maxTimes, 0, byte.MaxValue);
                        cooldowns[b] = (byte)Math.Clamp(sw.cooldown, 0, byte.MaxValue);
                        if (sw.cooldown > maxCooldown) maxCooldown = sw.cooldown;
                        if (sw.repeatType == MoveRepeatType.CannotRepeat) anyCannotRepeat = true;
                    }

                    nodes[i] = new HotMonsterNode
                    {
                        NodeId = (short)i,
                        MoveId = id,
                        NodeKind = HotMonsterNodeKind.RandomBranch,
                        BaseDamage = 0,
                        Repeats = 0,
                        NextNode = -1,
                        BranchTargets = targets,
                        BranchWeights = weights,
                        BranchRepeatTypes = repeatTypes,
                        BranchMaxTimes = maxTimes,
                        BranchCooldowns = cooldowns,
                        Cooldown = maxCooldown,
                        MustPerformOnceBeforeRepeat = false,
                        CannotRepeat = anyCannotRepeat,
                        ConditionalKind = HotMonsterConditionalKind.None,
                        MoveEffects = [],
                    };
                }
                else if (mState is ConditionalBranchState conditional)
                {
                    HotMonsterConditionalKind condKind = ClassifyConditional(typeName, conditional.Id);
                    short fallbackNode = -1;
                    try
                    {
                        string resolved = conditional.GetNextState(monster.Creature, new Rng(0));
                        if (idToNode.TryGetValue(resolved, out short rId))
                            fallbackNode = rId;
                    }
                    catch (InvalidOperationException)
                    {
                        // Conditional branch unreachable at root; engine resolves dynamically if reached.
                    }

                    nodes[i] = new HotMonsterNode
                    {
                        NodeId = (short)i,
                        MoveId = id,
                        NodeKind = HotMonsterNodeKind.ConditionalBranch,
                        BaseDamage = 0,
                        Repeats = 0,
                        NextNode = fallbackNode,
                        BranchTargets = [],
                        BranchWeights = [],
                        BranchRepeatTypes = [],
                        BranchMaxTimes = [],
                        BranchCooldowns = [],
                        Cooldown = 0,
                        MustPerformOnceBeforeRepeat = false,
                        CannotRepeat = false,
                        ConditionalKind = condKind,
                        MoveEffects = [],
                    };
                }
                else
                {
                    // Synthetic / historical-only state ID (e.g. STUNNED)
                    nodes[i] = new HotMonsterNode
                    {
                        NodeId = (short)i,
                        MoveId = id,
                        NodeKind = HotMonsterNodeKind.Move,
                        BaseDamage = 0,
                        Repeats = 0,
                        NextNode = -1,
                        BranchTargets = [],
                        BranchWeights = [],
                        BranchRepeatTypes = [],
                        BranchMaxTimes = [],
                        BranchCooldowns = [],
                        Cooldown = 0,
                        MustPerformOnceBeforeRepeat = false,
                        CannotRepeat = false,
                        ConditionalKind = HotMonsterConditionalKind.None,
                        MoveEffects = [],
                    };
                }
            }

            short initialNode = -1;
            if (InitialStateField.GetValue(liveMachine) is MonsterState initState
                && idToNode.TryGetValue(initState.Id, out short initId))
            {
                initialNode = initId;
            }

            return new HotMonsterMachine
            {
                MonsterDefId = monsterDefId,
                MonsterEntry = monster.Id.Entry,
                InitialNode = initialNode,
                Nodes = nodes,
                NodeById = idToNode,
            };
        }

        private HotMonsterMoveEffect[] CompilePhase1MonsterMoveEffects(
            string typeName,
            string moveId,
            IReadOnlyDictionary<string, int> staticInts)
        {
            int StaticInt(string key, int fallback = 0)
                => staticInts.TryGetValue(key, out int v) ? v : fallback;

            return (typeName, moveId) switch
            {
                ("FuzzyWurmCrawler", "INHALE") =>
                [
                    new HotMonsterMoveEffect(HotCardOpKind.ApplyPowerSelf, EnsurePowerDefByEntry("STRENGTH_POWER"), 7),
                ],
                ("Nibbit", "SLICE_MOVE") =>
                [
                    new HotMonsterMoveEffect(HotCardOpKind.GainBlock, 0, (short)StaticInt("SliceBlock")),
                ],
                ("Nibbit", "HISS_MOVE") =>
                [
                    new HotMonsterMoveEffect(HotCardOpKind.ApplyPowerSelf, EnsurePowerDefByEntry("STRENGTH_POWER"), (short)StaticInt("HissStrengthGain")),
                ],
                ("BowlbugEgg", "BITE_MOVE") =>
                [
                    new HotMonsterMoveEffect(HotCardOpKind.GainBlock, 0, (short)StaticInt("ProtectBlock")),
                ],
                ("CalcifiedCultist", "INCANTATION_MOVE") =>
                [
                    new HotMonsterMoveEffect(HotCardOpKind.ApplyPowerSelf, EnsurePowerDefByEntry("RITUAL_POWER"), (short)StaticInt("IncantationAmount")),
                ],
                ("SludgeSpinner", "OIL_SPRAY_MOVE") =>
                [
                    new HotMonsterMoveEffect(HotCardOpKind.ApplyPowerTarget, EnsurePowerDefByEntry("WEAK_POWER"), 1),
                ],
                ("SludgeSpinner", "RAGE_MOVE") =>
                [
                    new HotMonsterMoveEffect(HotCardOpKind.ApplyPowerSelf, EnsurePowerDefByEntry("STRENGTH_POWER"), 3),
                ],
                ("Flyconid", "VULNERABLE_SPORES_MOVE") =>
                [
                    new HotMonsterMoveEffect(HotCardOpKind.ApplyPowerTarget, EnsurePowerDefByEntry("VULNERABLE_POWER"), 2),
                ],
                ("Flyconid", "FRAIL_SPORES_MOVE") =>
                [
                    new HotMonsterMoveEffect(HotCardOpKind.ApplyPowerTarget, EnsurePowerDefByEntry("FRAIL_POWER"), 2),
                ],
                ("CorpseSlug", "GOOP_MOVE") =>
                [
                    new HotMonsterMoveEffect(HotCardOpKind.ApplyPowerTarget, EnsurePowerDefByEntry("FRAIL_POWER"), (short)StaticInt("GoopFrailAmt")),
                ],
                _ => [],
            };
        }

        public int AddDetachedCard(in HotCard card)
        {
            _detachedCards ??= [];
            int index = _detachedCards.Count;
            _detachedCards.Add(card);
            return index;
        }

        public void AddMonsterLogOverflow(byte enemySlot, ushort[] moveNodeIds)
        {
            _monsterLogs ??= [];
            _monsterLogs.Add(new HotMonsterStateLogOverflow(enemySlot, moveNodeIds));
        }

        public HotColdSidecar? MergeColdSidecar(HotColdSidecar? existing)
        {
            if (_detachedCards == null && _monsterLogs == null)
                return existing is { IsEmpty: false } ? existing : null;

            HotCard[]? detached = _detachedCards?.ToArray();
            HotMonsterStateLogOverflow[]? logs = _monsterLogs?.ToArray();
            if (existing == null)
            {
                return new HotColdSidecar(
                    DetachedCards: detached,
                    MonsterLogs: logs);
            }

            return existing with
            {
                DetachedCards = detached ?? existing.DetachedCards,
                MonsterLogs = logs ?? existing.MonsterLogs,
            };
        }
    }

    private static void ExtractSemanticVars(
        AbstractModel model,
        out string[] names,
        out decimal[] values,
        out string?[]? strings)
    {
        DynamicVarSet? vars = model switch
        {
            CardModel c => c.DynamicVars,
            PowerModel p => p.DynamicVars,
            _ => null,
        };

        if (vars == null || vars.Count == 0)
        {
            names = [];
            values = [];
            strings = null;
            return;
        }

        List<(string Key, decimal Value, string? Str)> list = new(vars.Count);
        bool anyString = false;
        foreach (KeyValuePair<string, DynamicVar> kv in vars)
        {
            if (!SemanticStateFieldPolicy.IsSemantic(model, kv.Key, kv.Value))
                continue;
            string? str = kv.Value is StringVar sv ? sv.StringValue : null;
            if (str != null)
                anyString = true;
            list.Add((kv.Key, kv.Value.BaseValue, str));
        }

        if (list.Count == 0)
        {
            names = [];
            values = [];
            strings = null;
            return;
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        names = new string[list.Count];
        values = new decimal[list.Count];
        strings = anyString ? new string?[list.Count] : null;
        for (int i = 0; i < list.Count; i++)
        {
            names[i] = list[i].Key;
            values[i] = list[i].Value;
            if (strings != null)
                strings[i] = list[i].Str;
        }
    }

    private static void ExtractCardScalars(
        string[] varNames,
        decimal[] varValues,
        out short damage,
        out short block,
        out short magic,
        out byte hits,
        out byte var0Index,
        out byte var1Index)
    {
        damage = 0;
        block = 0;
        magic = 0;
        hits = 1;
        var0Index = 0xFF;
        var1Index = 0xFF;
        bool magicFound = false;

        for (int i = 0; i < varNames.Length; i++)
        {
            string name = varNames[i];
            decimal val = varValues[i];
            if (name == "Damage")
            {
                damage = (short)val;
                var0Index = (byte)i;
            }
            else if (name == "Block")
            {
                block = (short)val;
                var1Index = (byte)i;
            }
            else if (name is "HitCount" or "Repeat")
            {
                hits = (byte)Math.Clamp((int)val, 1, byte.MaxValue);
            }
            else if (!magicFound && name is "Vulnerable" or "Weak" or "Frail" or "Cards" or "StrengthPower"
                or "PlatingPower" or "MetallicizePower" or "Magic" or "Energy" or "Stars" or "Heal" or "HpLoss" or "Forge")
            {
                magic = (short)val;
                magicFound = true;
            }
        }

        if (!magicFound)
        {
            for (int i = 0; i < varNames.Length; i++)
            {
                if (i != var0Index && i != var1Index && varNames[i] is not ("HitCount" or "Repeat"))
                {
                    magic = (short)varValues[i];
                    break;
                }
            }
        }

        // If Damage or Block wasn't present, assign unused Var0/Var1 slots to first remaining vars.
        if (var0Index == 0xFF && varNames.Length > 0)
        {
            for (int i = 0; i < varNames.Length; i++)
            {
                if (i != var1Index)
                {
                    var0Index = (byte)i;
                    break;
                }
            }
        }
        if (var1Index == 0xFF && varNames.Length > 1)
        {
            for (int i = 0; i < varNames.Length; i++)
            {
                if (i != var0Index)
                {
                    var1Index = (byte)i;
                    break;
                }
            }
        }
    }

    private static bool DecimalScaleMatches(decimal a, decimal b)
    {
        Span<int> bitsA = stackalloc int[4];
        Span<int> bitsB = stackalloc int[4];
        decimal.GetBits(a, bitsA);
        decimal.GetBits(b, bitsB);
        return bitsA[0] == bitsB[0] && bitsA[1] == bitsB[1] && bitsA[2] == bitsB[2] && bitsA[3] == bitsB[3];
    }

    private static bool TryComputeShortDelta(decimal expected, decimal actual, out short delta)
    {
        decimal diff = actual - expected;
        if (diff >= short.MinValue && diff <= short.MaxValue && decimal.Truncate(diff) == diff)
        {
            short candidate = (short)diff;
            if (DecimalScaleMatches(expected + candidate, actual))
            {
                delta = candidate;
                return true;
            }
        }
        delta = 0;
        return false;
    }

    private static bool VarsMatchExact(
        string[] namesA, decimal[] valsA, string?[]? strsA,
        string[] namesB, decimal[] valsB, string?[]? strsB)
    {
        if (namesA.Length != namesB.Length || !StringsEqual(strsA, strsB))
            return false;
        for (int i = 0; i < namesA.Length; i++)
        {
            if (!string.Equals(namesA[i], namesB[i], StringComparison.Ordinal))
                return false;
            if (!DecimalScaleMatches(valsA[i], valsB[i]))
                return false;
        }
        return true;
    }

    private static bool StringsEqual(string?[]? a, string?[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    private static HotCardPrivateKind ClassifyCardPrivate(CardModel card)
        => card switch
        {
            Claw => HotCardPrivateKind.Claw,
            GeneticAlgorithm => HotCardPrivateKind.GeneticAlgorithm,
            Maul => HotCardPrivateKind.Maul,
            MadScience => HotCardPrivateKind.MadScience,
            Rampage => HotCardPrivateKind.Rampage,
            TheScythe => HotCardPrivateKind.TheScythe,
            TheShot => HotCardPrivateKind.TheShot,
            ShowOff => HotCardPrivateKind.ShowOff,
            SovereignBlade => HotCardPrivateKind.SovereignBlade,
            _ => HotCardPrivateKind.None,
        };

    private static HotPowerSpecialKind ClassifyPowerSpecial(PowerModel power)
        => power switch
        {
            SurroundedPower => HotPowerSpecialKind.Surrounded,
            NightmarePower  => HotPowerSpecialKind.Nightmare,
            OrbitPower      => HotPowerSpecialKind.Orbit,
            ShowOffPower    => HotPowerSpecialKind.ShowOff,
            ImpaledPower    => HotPowerSpecialKind.Impaled,
            AimPower        => HotPowerSpecialKind.Aim,
            EncorePower     => HotPowerSpecialKind.Encore,
            YahPower        => HotPowerSpecialKind.Yah,
            LullabyPower    => HotPowerSpecialKind.Lullaby,
            _               => HotPowerSpecialKind.None,
        };

    private static HotRelicSpecialKind ClassifyRelicSpecial(RelicModel relic, out bool isStateful, out bool isTracked)
    {
        isStateful = false;
        isTracked = false;
        switch (relic)
        {
            case ArtOfWar: isStateful = true; return HotRelicSpecialKind.ArtOfWar;
            case ConductorsBaton: isStateful = true; return HotRelicSpecialKind.ConductorsBaton;
            case MegaCrit.Sts2.Core.Models.Relics.Wuppy: isStateful = true; return HotRelicSpecialKind.Wuppy;
            case Byrdpip: isStateful = true; return HotRelicSpecialKind.Byrdpip;
            case BeltBuckle: isStateful = true; return HotRelicSpecialKind.BeltBuckle;
            case BoneTea: isStateful = true; return HotRelicSpecialKind.BoneTea;
            case EmotionChip: isStateful = true; return HotRelicSpecialKind.EmotionChip;
            case FakeHappyFlower: isStateful = true; return HotRelicSpecialKind.FakeHappyFlower;
            case FakeVenerableTeaSet: isStateful = true; return HotRelicSpecialKind.FakeVenerableTeaSet;
            case HappyFlower: isStateful = true; return HotRelicSpecialKind.HappyFlower;
            case MiniRegent: isStateful = true; return HotRelicSpecialKind.MiniRegent;
            case GalacticDust: isStateful = true; return HotRelicSpecialKind.GalacticDust;
            case PaelsEye: isStateful = true; return HotRelicSpecialKind.PaelsEye;
            case PaelsTears: isStateful = true; return HotRelicSpecialKind.PaelsTears;
            case RainbowRing: isStateful = true; isTracked = true; return HotRelicSpecialKind.RainbowRing;
            case RedSkull: isStateful = true; return HotRelicSpecialKind.RedSkull;
            case Pendulum: isStateful = true; return HotRelicSpecialKind.Pendulum;
            case Pocketwatch: isStateful = true; return HotRelicSpecialKind.Pocketwatch;
            case PollinousCore: isStateful = true; return HotRelicSpecialKind.PollinousCore;
            case RuinedHelmet: isStateful = true; return HotRelicSpecialKind.RuinedHelmet;
            case UnsettlingLamp: isStateful = true; return HotRelicSpecialKind.UnsettlingLamp;
            case VenerableTeaSet: isStateful = true; return HotRelicSpecialKind.VenerableTeaSet;
            case BeatingRemnant: isTracked = true; return HotRelicSpecialKind.BeatingRemnant;
            case BrilliantScarf: isTracked = true; return HotRelicSpecialKind.BrilliantScarf;
            case BurningSticks: isTracked = true; return HotRelicSpecialKind.BurningSticks;
            case CentennialPuzzle: isTracked = true; return HotRelicSpecialKind.CentennialPuzzle;
            case DemonTongue: isTracked = true; return HotRelicSpecialKind.DemonTongue;
            case IronClub: isTracked = true; return HotRelicSpecialKind.IronClub;
            case JossPaper: isTracked = true; return HotRelicSpecialKind.JossPaper;
            case Kunai: isTracked = true; return HotRelicSpecialKind.Kunai;
            case Kusarigama: isTracked = true; return HotRelicSpecialKind.Kusarigama;
            case LetterOpener: isTracked = true; return HotRelicSpecialKind.LetterOpener;
            case LizardTail: isTracked = true; return HotRelicSpecialKind.LizardTail;
            case Metronome: isTracked = true; return HotRelicSpecialKind.Metronome;
            case MusicBox: isTracked = true; return HotRelicSpecialKind.MusicBox;
            case Nunchaku: isTracked = true; return HotRelicSpecialKind.Nunchaku;
            case OrnamentalFan: isTracked = true; return HotRelicSpecialKind.OrnamentalFan;
            case PaelsLegion: isTracked = true; return HotRelicSpecialKind.PaelsLegion;
            case PenNib: isTracked = true; return HotRelicSpecialKind.PenNib;
            case Permafrost: isTracked = true; return HotRelicSpecialKind.Permafrost;
            case Regalite: isTracked = true; return HotRelicSpecialKind.Regalite;
            case Shuriken: isTracked = true; return HotRelicSpecialKind.Shuriken;
            case ThrowingAxe: isTracked = true; return HotRelicSpecialKind.ThrowingAxe;
            case TuningFork: isTracked = true; return HotRelicSpecialKind.TuningFork;
            case Vambrace: isTracked = true; return HotRelicSpecialKind.Vambrace;
            default: return HotRelicSpecialKind.None;
        }
    }

    private static HotMonsterStateKind ClassifyMonsterState(string typeName)
        => typeName switch
        {
            "FrogKnight" => HotMonsterStateKind.FrogKnight,
            "TwoTailedRat" => HotMonsterStateKind.TwoTailedRat,
            "Fabricator" => HotMonsterStateKind.Fabricator,
            "ToughEgg" => HotMonsterStateKind.ToughEgg,
            "TestSubject" => HotMonsterStateKind.TestSubject,
            "Tunneler" => HotMonsterStateKind.Tunneler,
            "LagavulinMatriarch" or "SlumberingBeetle" => HotMonsterStateKind.AwakeCheck,
            "Queen" => HotMonsterStateKind.Queen,
            _ => HotMonsterStateKind.None,
        };

    private static HotMonsterConditionalKind ClassifyConditional(string typeName, string branchId)
        => (typeName, branchId) switch
        {
            ("FrogKnight", "HALF_HEALTH") => HotMonsterConditionalKind.FrogKnightHalfHealth,
            ("Fabricator", "fabricateBranch") => HotMonsterConditionalKind.FabricatorBranch,
            ("TestSubject", "REVIVE_BRANCH") => HotMonsterConditionalKind.TestSubjectRevive,
            ("Ovicopter", "SUMMON_BRANCH_STATE") => HotMonsterConditionalKind.OvicopterSummon,
            ("LagavulinMatriarch", "SLEEP_BRANCH") => HotMonsterConditionalKind.LagavulinSleep,
            ("SlumberingBeetle", "SNORE_NEXT") => HotMonsterConditionalKind.SlumberingBeetleSnore,
            ("Queen", "YOURE_MINE_NOW_BRANCH" or "BURN_BRIGHT_FOR_ME_BRANCH") => HotMonsterConditionalKind.QueenAmalgam,
            ("LivingShield", "SHIELD_SLAM_BRANCH") => HotMonsterConditionalKind.LivingShieldSlam,
            ("BowlbugRock", "POST_HEADBUTT") => HotMonsterConditionalKind.BowlbugRockPostHeadbutt,
            _ => HotMonsterConditionalKind.StaticFallback,
        };
}
