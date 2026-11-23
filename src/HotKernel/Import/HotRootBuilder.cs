using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using STS2RitsuLib;

namespace CombatSolver.HotKernel;

internal sealed record HotRootBundle(
    HotRules Rules,
    CombatHotState RootState,
    HotColdSidecar? RootCold);

/// <summary>
/// Captures either a live vanilla STS2 <see cref="CombatState"/> (main thread) or a detached
/// <see cref="CombatRootSnapshot"/> (worker-thread safe) into an immutable <see cref="HotRules"/>,
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

    private static readonly ConcurrentDictionary<string, CanonicalCardTemplate> CanonicalCardCache =
        new(StringComparer.Ordinal);

    private static readonly Lazy<CanonicalPowerTemplate?[]> CanonicalPowerTemplates =
        new(BuildCanonicalPowerTemplates);

    private sealed class CanonicalPowerTemplate
    {
        public required string[] OrderedVarNames { get; init; }
        public required decimal[] DefaultVarValues { get; init; }
        public string?[]? OrderedVarStrings { get; init; }
        public required bool IsInstanced { get; init; }
    }

    private static CanonicalPowerTemplate?[] BuildCanonicalPowerTemplates()
    {
        CanonicalPowerTemplate?[] table = new CanonicalPowerTemplate?[(int)HotPowerId.Count];
        Assembly vanillaAssembly = typeof(AbstractModel).Assembly;
        foreach (PowerModel power in ModelDb.AllPowers)
        {
            if (power.GetType().Assembly != vanillaAssembly)
                continue;
            HotPowerId id = HotPowerCatalog.FindByEntry(power.Id.Entry);
            if (id == HotPowerId.None || table[(int)id] != null)
                continue;
            ExtractSemanticVars(power, out string[] varNames, out decimal[] varValues, out string?[]? varStrings);
            table[(int)id] = new CanonicalPowerTemplate
            {
                OrderedVarNames = varNames,
                DefaultVarValues = varValues,
                OrderedVarStrings = varStrings,
                IsInstanced = power.InstanceType != PowerInstanceType.None,
            };
        }
        return table;
    }

    private sealed class CanonicalCardTemplate
    {
        public required CardType Type { get; init; }
        public required CardRarity Rarity { get; init; }
        public required TargetType Target { get; init; }
        public required sbyte BaseCost { get; init; }
        public required sbyte UpgradedCost { get; init; }
        public required sbyte BaseStarCost { get; init; }
        public required byte BaseKeywords { get; init; }
        public required short BaseDamage { get; init; }
        public required short UpgradedDamage { get; init; }
        public required short BaseBlock { get; init; }
        public required short UpgradedBlock { get; init; }
        public required short BaseMagic { get; init; }
        public required short UpgradedMagic { get; init; }
        public required byte BaseHits { get; init; }
        public required byte UpgradedHits { get; init; }
        public required string[] OrderedVarNames { get; init; }
        public required decimal[] BaseVarValues { get; init; }
        public required decimal[] UpgradedVarValues { get; init; }
        public string?[]? OrderedVarStrings { get; init; }
        public required byte Var0Index { get; init; }
        public required byte Var1Index { get; init; }
        public required bool IsAllowedInBlitz { get; init; }
        public required bool GainsBlock { get; init; }
        public required bool CanBeGeneratedInCombat { get; init; }
        public required int MaxUpgradeLevel { get; init; }
        public required HotCardPrivateKind PrivateKind { get; init; }
        public required bool IsStrikeTag { get; init; }
        public required bool IsDefendTag { get; init; }
        public required bool IsShootTag { get; init; }
        public required bool IsOstyAttackTag { get; init; }
        public required bool IsShivTag { get; init; }
        public required bool IsMinionTag { get; init; }
        public required byte MelodyTagMask { get; init; }
    }

    // =========================================================================
    // Entry point 1: Main-thread capture from live CombatState (Gate A/B)
    // =========================================================================
    public static HotRootBundle BuildFromLive(CombatState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        VanillaRootGate.ValidateVanillaOnly(state);

        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        PlayerCombatState pcs = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有战斗状态。");

        BuilderContext ctx = new();
        ctx.PreRegisterCatalogPowersAndOrbs();

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

        int baseMaxEnergy = player.MaxEnergy;
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

        int wuppyPetIdx = FindWuppyPetIndex(nonOstyPets);
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
            throw new HotCapacityExceededException($"Enemy count {enemies.Count} exceeds HotLimits.MaxEnemies ({HotLimits.MaxEnemies}).");

        RoomType? roomType = state.Encounter?.RoomType;
        bool isBoss = roomType == RoomType.Boss;
        bool isElite = roomType == RoomType.Elite;

        hot.EnemyCount = (byte)enemies.Count;
        hot.RosterCount = (byte)enemies.Count;
        string[] enemySlotNames = new string[enemies.Count];
        string[] enemyMonsterEntries = new string[enemies.Count];

        for (int i = 0; i < enemies.Count; i++)
        {
            Creature enemy = enemies[i];
            creatureToOwnerSlot[enemy] = (byte)(3 + i);
            hot.ActiveEnemyRoster[i] = (byte)i;
            enemySlotNames[i] = enemy.SlotName ?? "-";
            enemyMonsterEntries[i] = enemy.Monster?.Id.Entry ?? "null";

            hot.Enemies[i] = BuildLiveEnemy(ctx, state, enemy, enemySlot: (byte)i, isBoss, isElite);
        }

        // Pre-compile spawnable monster closure via BFS
        ctx.PreRegisterSpawnableMonsters(isBoss, isElite);

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

        hot.HandCount = (ushort)ImportLivePile(ctx, pcs.Hand, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, cardToUid);
        hot.DrawCount = (ushort)ImportLivePile(ctx, pcs.DrawPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, cardToUid);
        hot.DiscardCount = (ushort)ImportLivePile(ctx, pcs.DiscardPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, cardToUid);
        hot.ExhaustCount = (ushort)ImportLivePile(ctx, pcs.ExhaustPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, cardToUid);
        hot.PlayCount = (ushort)ImportLivePile(ctx, pcs.PlayPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, cardToUid);
        hot.NextCardUid = nextCardUid;

        // Orbs
        IReadOnlyList<OrbModel> orbs = pcs.OrbQueue.Orbs;
        if (orbs.Count > HotLimits.MaxOrbs)
            throw new HotCapacityExceededException($"Orb count {orbs.Count} exceeds HotLimits.MaxOrbs ({HotLimits.MaxOrbs}).");
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

        // Active player relics & stateful/tracked relic state slots
        bool hasRenewablePotionShapedRock = player.Relics.Any(static r => !r.IsMelted && r is PetrifiedToad);
        CapturePostCombatRelicHeal(player.Relics, out int postUncondHeal, out int postWoundHeal, out int postWoundPct);

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
                    throw new HotCapacityExceededException($"Stateful/tracked relic count exceeds HotLimits.MaxRelicStates ({HotLimits.MaxRelicStates}).");
                stateSlot = relicStateCount++;
                hot.RelicStates[stateSlot] = CaptureLiveRelicState(relic, def.SpecialKind);
                relicDefIdToStateSlot.TryAdd(relicDefId, stateSlot);
            }

            activeRelics.Add(new HotActiveRelic(relicDefId, stateSlot));
            activeRelicDefIds.Add(relicDefId);
        }
        hot.RelicStateCount = relicStateCount;

        // Potions
        int potionSlots = player.PotionSlots.Count;
        if (potionSlots > HotLimits.MaxPotions)
            throw new HotCapacityExceededException($"Potion slot count {potionSlots} exceeds HotLimits.MaxPotions ({HotLimits.MaxPotions}).");
        hot.PotionSlotCount = (byte)potionSlots;
        for (int slot = 0; slot < potionSlots; slot++)
        {
            PotionModel? potion = player.GetPotionAtSlotIndex(slot);
            hot.Potions[slot] = potion == null ? (ushort)0 : ctx.EnsurePotionDef(potion, hasRenewablePotionShapedRock);
        }

        // Fool arrows
        IReadOnlyList<ArrowSlot> arrows = pcs.Arrows;
        if (arrows.Count > HotLimits.MaxArrows)
            throw new HotCapacityExceededException($"Arrow count {arrows.Count} exceeds HotLimits.MaxArrows ({HotLimits.MaxArrows}).");
        hot.ArrowCount = (byte)arrows.Count;
        for (int i = 0; i < arrows.Count; i++)
        {
            ArrowSlot arrow = arrows[i];
            hot.Fool.Arrows[i] = (ushort)(((int)arrow.Status & 0xF) | (arrow.DestroyAfterImpaled ? 0x10 : 0));
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
                    throw new HotCapacityExceededException($"Power count exceeds HotLimits.MaxPowers ({HotLimits.MaxPowers}).");

                hot.Powers[powerCursor++] = BuildLivePower(
                    ctx, power, ownerSlot, creatureToOwnerSlot, cardToUid, arrows, ref hot, ref nextCardUid, ref rootCold);
            }
        }
        hot.PowerCount = (byte)powerCursor;
        hot.NextCardUid = nextCardUid;

        // Combat history counters & melodies
        CaptureLiveHistoryAndMelodies(state, player, ref hot);

        // All 10 RNG streams
        hot.Rng[(int)HotRngStream.Shuffle] = HotRng.FromNative(state.RunState.Rng.Shuffle.CaptureState());
        hot.Rng[(int)HotRngStream.CombatCardGeneration] = HotRng.FromNative(state.RunState.Rng.CombatCardGeneration.CaptureState());
        hot.Rng[(int)HotRngStream.CombatPotionGeneration] = HotRng.FromNative(state.RunState.Rng.CombatPotionGeneration.CaptureState());
        hot.Rng[(int)HotRngStream.CombatCardSelection] = HotRng.FromNative(state.RunState.Rng.CombatCardSelection.CaptureState());
        hot.Rng[(int)HotRngStream.CombatEnergyCosts] = HotRng.FromNative(state.RunState.Rng.CombatEnergyCosts.CaptureState());
        hot.Rng[(int)HotRngStream.CombatTargets] = HotRng.FromNative(state.RunState.Rng.CombatTargets.CaptureState());
        hot.Rng[(int)HotRngStream.CombatOrbGeneration] = HotRng.FromNative(state.RunState.Rng.CombatOrbGeneration.CaptureState());
        hot.Rng[(int)HotRngStream.MonsterAi] = HotRng.FromNative(state.RunState.Rng.MonsterAi.CaptureState());
        hot.Rng[(int)HotRngStream.Niche] = HotRng.FromNative(state.RunState.Rng.Niche.CaptureState());
        hot.Rng[(int)HotRngStream.NonCombatGeneration] = HotRng.FromNative(player.PlayerRng.NonCombatGeneration.CaptureState());

        CombatRulesSnapshot rulesSnapshot = CombatRulesSnapshot.Capture(state);
        CardMultiplayerConstraint mpConstraint = state.RunState.CardMultiplayerConstraint;
        List<CardPoolModel> otherCharPools = player.UnlockState.CharacterCardPools.ToList();
        if (otherCharPools.Count > 1)
            otherCharPools.Remove(player.Character.CardPool);

        FrozenGenerationPools pools = ctx.PreRegisterPoolsAndTokens(
            unlockedCharCards: player.Character.CardPool.GetUnlockedCards(player.UnlockState, mpConstraint),
            unlockedColorlessCards: ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(player.UnlockState, mpConstraint),
            unlockedCurseCards: ModelDb.CardPool<CurseCardPool>().GetUnlockedCards(player.UnlockState, mpConstraint),
            otherCharCards: otherCharPools.SelectMany(p => p.GetUnlockedCards(player.UnlockState, mpConstraint)),
            tarotCards: TarotCards.All.Where(card => card.IsAllowedInRun(state.RunState)),
            mpConstraint: mpConstraint,
            rulesSnapshot: rulesSnapshot,
            hasRenewablePotionShapedRock: hasRenewablePotionShapedRock);

        HotRules rules = FinalizeRules(
            ctx,
            ref hot,
            activeRelics,
            activeRelicDefIds,
            relicDefIdToStateSlot,
            rulesSnapshot,
            encounterGoldForfeited,
            baseMaxEnergy,
            enemySlotNames,
            enemyMonsterEntries,
            rootPets,
            rootAllyOwnerSlots,
            pools,
            hasRenewablePotionShapedRock,
            isBoss,
            isElite,
            TheftEncounterStrategy.IsApplicable(state),
            player.Character.Id.Entry,
            state.Encounter?.Id.Entry ?? string.Empty,
            state.Encounter?.Slots.ToArray() ?? [],
            state.RunState.CurrentActIndex,
            state.RunState.TotalFloor,
            postUncondHeal,
            postWoundHeal,
            postWoundPct);

        rootCold = ctx.MergeColdSidecar(rootCold);
        return new HotRootBundle(rules, hot, rootCold);
    }

    // =========================================================================
    // Entry point 2: Worker-thread-safe capture from CombatRootSnapshot (Phase 3)
    // =========================================================================
    public static HotRootBundle BuildFromRootSnapshot(CombatRootSnapshot root)
    {
        ArgumentNullException.ThrowIfNull(root);

        CombatPredictionSimulator sim = root.ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)sim.State.CombatState;
        Player player = root.PlayerIdentity;
        SimPlayerCombatState pcs = sim.State.GetPlayerCombatState(player);
        SimCreatureState simPlayer = sim.State.GetCreature(player.Creature);

        VanillaRootGate.ValidateVanillaOnly(sim, combat, player, root.PotionSlotCount);

        BuilderContext ctx = new();
        ctx.PreRegisterCatalogPowersAndOrbs();

        CombatHotState hot = default;
        uint maxCombatId = player.Creature.CombatId ?? 0;
        foreach (Creature c in combat.Allies)
            if (c.CombatId is uint cid && cid > maxCombatId) maxCombatId = cid;
        foreach (Creature c in combat.Enemies)
            if (c.CombatId is uint cid && cid > maxCombatId) maxCombatId = cid;

        hot.NextCreatureId = maxCombatId + 1;
        hot.RoundNumber = combat.RoundNumber;
        hot.PlayerTurnNumber = root.StartTurnNumber;
        hot.Energy = pcs.Energy;
        hot.Stars = pcs.Stars;
        hot.Gold = combat.GetPlayerGold(player);
        hot.CurrentSide = combat.CurrentSide == CombatSide.Enemy ? HotCombatSide.Enemy : HotCombatSide.Player;
        hot.TerminalOutcome = HotCombatOutcome.Ongoing;

        int maxHandSize = sim.GetMaxHandSize(player);
        hot.MaxHandSize = (byte)Math.Clamp(maxHandSize, 0, byte.MaxValue);

        int baseMaxEnergy = combat.GetBaseMaxEnergy(player);
        hot.Counters[(int)HotCounterSlot.BaseMaxEnergy] = baseMaxEnergy;
        hot.Counters[(int)HotCounterSlot.BaseHandDraw] = 5;

        HotCombatFlags combatFlags = HotCombatFlags.IsInProgress | HotCombatFlags.PlayerHooksActive;
        bool encounterGoldForfeited = TheftEncounterStrategy.ForfeitsEncounterGold(combat);
        if (encounterGoldForfeited)
            combatFlags |= HotCombatFlags.EncounterGoldForfeited;

        // Player creature (slot 0)
        hot.Player = BuildSimCreature(player.Creature, simPlayer, slotIdx: 0, HotCreatureFlags.IsPlayer);

        // Osty (slot 1)
        Creature? osty = combat.GetOsty(player);
        if (osty != null)
        {
            SimCreatureState simOsty = sim.State.GetCreature(osty);
            int ostyMaxHp = combat.GetOstyMaxHp(sim, player);
            combatFlags |= HotCombatFlags.HasOsty;
            hot.Osty = new HotCreature
            {
                CombatId = osty.CombatId ?? uint.MaxValue,
                CurrentHp = simOsty.CurrentHp,
                MaxHp = ostyMaxHp,
                Block = simOsty.Block,
                Flags = (simOsty.CurrentHp > 0 && simOsty.IsAlive ? HotCreatureFlags.Alive : HotCreatureFlags.None)
                    | HotCreatureFlags.ActiveInRoster | HotCreatureFlags.IsOsty | HotCreatureFlags.IsPet,
                SlotIdx = 1,
                Pad = 0,
            };
        }

        // Non-Osty pets
        List<HotRootPetSnapshot> rootPets = [];
        Dictionary<Creature, byte> creatureToOwnerSlot = new(ReferenceEqualityComparer.Instance)
        {
            [player.Creature] = 0,
        };
        if (osty != null)
            creatureToOwnerSlot[osty] = 1;

        List<Creature> nonOstyPets = [];
        foreach (Creature ally in combat.Allies)
        {
            if (ally.PetOwner == player && !ReferenceEquals(ally, osty))
                nonOstyPets.Add(ally);
        }

        int wuppyPetIdx = FindWuppyPetIndex(nonOstyPets);
        for (int i = 0; i < nonOstyPets.Count; i++)
        {
            Creature pet = nonOstyPets[i];
            SimCreatureState simPet = sim.State.GetCreature(pet);
            bool isWuppySlot = i == wuppyPetIdx;
            byte petOwnerSlot = isWuppySlot ? (byte)2 : (byte)(19 + i);
            creatureToOwnerSlot[pet] = petOwnerSlot;

            if (isWuppySlot)
            {
                combatFlags |= HotCombatFlags.HasWuppy;
                hot.Wuppy = BuildSimCreature(pet, simPet, slotIdx: 2, HotCreatureFlags.IsPet);
            }

            rootPets.Add(new HotRootPetSnapshot(
                CombatId: pet.CombatId ?? uint.MaxValue,
                MonsterEntry: pet.Monster!.Id.Entry,
                CurrentHp: simPet.CurrentHp,
                MaxHp: simPet.MaxHp,
                Block: simPet.Block,
                IsWuppySlot: isWuppySlot));
        }

        hot.CombatFlags = combatFlags;

        // Parse predicted monster AI logs & monster state/deathPhase cleanly from detached combat
        StringBuilder aiText = new(256);
        combat.AppendPredictedMonsterAiContinuation(aiText);
        string[] aiLogs = ParseIndexedContinuationFields(aiText.ToString(), "AI", combat.Enemies.Count);

        StringBuilder msText = new(128);
        combat.AppendPredictedMonsterStateContinuation(msText);
        string[] msFields = ParseIndexedContinuationFields(msText.ToString(), "MS", combat.Enemies.Count);

        IReadOnlyList<Creature> enemies = combat.Enemies;
        if (enemies.Count > HotLimits.MaxEnemies)
            throw new HotCapacityExceededException($"Enemy count {enemies.Count} exceeds HotLimits.MaxEnemies ({HotLimits.MaxEnemies}).");

        bool isBoss = root.EncounterRoomType == RoomType.Boss;
        bool isElite = root.EncounterRoomType == RoomType.Elite;

        hot.EnemyCount = (byte)enemies.Count;
        hot.RosterCount = (byte)enemies.Count;
        string[] enemySlotNames = new string[enemies.Count];
        string[] enemyMonsterEntries = new string[enemies.Count];

        for (int i = 0; i < enemies.Count; i++)
        {
            Creature enemy = enemies[i];
            SimCreatureState simEnemy = sim.State.GetCreature(enemy);
            creatureToOwnerSlot[enemy] = (byte)(3 + i);
            hot.ActiveEnemyRoster[i] = (byte)i;
            enemySlotNames[i] = enemy.SlotName ?? "-";
            enemyMonsterEntries[i] = enemy.Monster?.Id.Entry ?? "null";

            string moveId = combat.TryGetForcedMoveId(enemy, out string forced)
                ? forced
                : combat.GetPredictedMoveId(enemy);

            hot.Enemies[i] = BuildSimEnemy(
                ctx, enemy, simEnemy, enemySlot: (byte)i, moveId, aiLogs[i], msFields[i], isBoss, isElite);
        }

        ctx.PreRegisterSpawnableMonsters(isBoss, isElite);

        List<byte> rootAllyOwnerSlots = new(combat.Allies.Count);
        foreach (Creature ally in combat.Allies)
        {
            if (creatureToOwnerSlot.TryGetValue(ally, out byte slot))
                rootAllyOwnerSlots.Add(slot);
        }

        // Capture cards across all 5 piles from detached SimPlayerCombatState
        HotColdSidecar? rootCold = null;
        int cardCursor = 0;
        ushort nextCardUid = 1;
        List<ushort> allCardUids = new(pcs.Hand.Cards.Count + pcs.DrawPile.Cards.Count + pcs.DiscardPile.Cards.Count + pcs.ExhaustPile.Cards.Count + pcs.PlayPile.Cards.Count);

        hot.HandCount = (ushort)ImportSimPile(ctx, pcs.Hand, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, allCardUids);
        hot.DrawCount = (ushort)ImportSimPile(ctx, pcs.DrawPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, allCardUids);
        hot.DiscardCount = (ushort)ImportSimPile(ctx, pcs.DiscardPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, allCardUids);
        hot.ExhaustCount = (ushort)ImportSimPile(ctx, pcs.ExhaustPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, allCardUids);
        hot.PlayCount = (ushort)ImportSimPile(ctx, pcs.PlayPile, ref hot, ref rootCold, ref cardCursor, ref nextCardUid, allCardUids);
        hot.NextCardUid = nextCardUid;

        // Orbs
        IReadOnlyList<OrbModel> orbs = pcs.OrbQueue.Orbs;
        if (orbs.Count > HotLimits.MaxOrbs)
            throw new HotCapacityExceededException($"Orb count {orbs.Count} exceeds HotLimits.MaxOrbs ({HotLimits.MaxOrbs}).");
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
                PassiveVal = (short)OrbMirrors.GetPassiveValue(sim, orb),
                EvokeVal = (int)OrbMirrors.GetEvokeValue(sim, orb),
            };
        }

        // Relics & captured stateful/tracked relic states from detached simulator
        IReadOnlyList<RelicModel> simRelics = combat.RelicsOf(player);
        StringBuilder statefulRelicsText = new(128);
        combat.AppendPredictedStatefulRelics(statefulRelicsText, player);
        Dictionary<string, Queue<(int Curr, int Prev)>> statefulMap = ParseStatefulRelics(statefulRelicsText.ToString());

        StringBuilder mirrorRelicsText = new(128);
        RelicPredictionStateSupport.AppendPredictedContinuation(mirrorRelicsText, sim, simRelics);
        Dictionary<string, Queue<string>> mirrorMap = ParseMirrorRelics(mirrorRelicsText.ToString());

        List<HotActiveRelic> activeRelics = [];
        List<ushort> activeRelicDefIds = [];
        Dictionary<ushort, byte> relicDefIdToStateSlot = [];
        byte relicStateCount = 0;

        foreach (RelicModel relic in simRelics)
        {
            if (relic.IsMelted)
                continue;
            ushort relicDefId = ctx.EnsureRelicDef(relic);
            HotRelicDef def = ctx.Relics[relicDefId];
            byte stateSlot = 0xFF;

            if (def.IsStateful || def.IsTracked)
            {
                if (relicStateCount >= HotLimits.MaxRelicStates)
                    throw new HotCapacityExceededException($"Stateful/tracked relic count exceeds HotLimits.MaxRelicStates ({HotLimits.MaxRelicStates}).");
                stateSlot = relicStateCount++;
                hot.RelicStates[stateSlot] = DecodeSimRelicState(def.Entry, def.SpecialKind, statefulMap, mirrorMap);
                relicDefIdToStateSlot.TryAdd(relicDefId, stateSlot);
            }

            activeRelics.Add(new HotActiveRelic(relicDefId, stateSlot));
            activeRelicDefIds.Add(relicDefId);
        }
        hot.RelicStateCount = relicStateCount;

        // Potions
        int potionSlots = root.PotionSlotCount;
        if (potionSlots > HotLimits.MaxPotions)
            throw new HotCapacityExceededException($"Potion slot count {potionSlots} exceeds HotLimits.MaxPotions ({HotLimits.MaxPotions}).");
        hot.PotionSlotCount = (byte)potionSlots;
        for (int slot = 0; slot < potionSlots; slot++)
        {
            PotionModel? potion = combat.GetPotionAtSlot(player, slot);
            hot.Potions[slot] = potion == null ? (ushort)0 : ctx.EnsurePotionDef(potion, root.HasRenewablePotionShapedRock);
        }

        // Fool arrows
        IReadOnlyList<ArrowSlot> arrows = pcs.Arrows;
        if (arrows.Count > HotLimits.MaxArrows)
            throw new HotCapacityExceededException($"Arrow count {arrows.Count} exceeds HotLimits.MaxArrows ({HotLimits.MaxArrows}).");
        hot.ArrowCount = (byte)arrows.Count;
        for (int i = 0; i < arrows.Count; i++)
        {
            ArrowSlot arrow = arrows[i];
            hot.Fool.Arrows[i] = (ushort)(((int)arrow.Status & 0xF) | (arrow.DestroyAfterImpaled ? 0x10 : 0));
        }

        // Parse ShowOff card indices from detached combat
        StringBuilder showOffText = new(64);
        combat.AppendShowOffState(showOffText, player, sim);
        Queue<int> showOffIndices = ParseShowOffIndices(showOffText.ToString());

        // Powers across all effective powers
        int powerCursor = 0;
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (power.Amount == 0)
                continue;
            if (!creatureToOwnerSlot.TryGetValue(power.Owner, out byte ownerSlot))
                continue;
            if (powerCursor >= HotLimits.MaxPowers)
                throw new HotCapacityExceededException($"Power count exceeds HotLimits.MaxPowers ({HotLimits.MaxPowers}).");

            hot.Powers[powerCursor++] = BuildSimPower(
                ctx, sim, combat, power, ownerSlot, creatureToOwnerSlot, allCardUids, showOffIndices, arrows, ref hot, ref nextCardUid, ref rootCold);
        }
        hot.PowerCount = (byte)powerCursor;
        hot.NextCardUid = nextCardUid;

        // Turn history counters & melodies from detached combat
        hot.Counters[(int)HotCounterSlot.StatusCardsDrawnThisTurn] = combat.GetStatusCardsDrawnThisTurn(player);
        hot.Counters[(int)HotCounterSlot.ZeroCostAttackStartsThisTurn] = combat.GetZeroCostAttackStartsThisTurn(player.Creature);
        hot.Counters[(int)HotCounterSlot.CardPlayStartsThisTurn] = combat.GetCardPlayStartsThisTurn(player.Creature);
        hot.Counters[(int)HotCounterSlot.AttackSkillStartsThisTurn] = combat.GetAttackSkillStartsThisTurn(player.Creature);
        hot.Counters[(int)HotCounterSlot.AttackPlayStartsThisTurn] = combat.GetAttackPlayStartsThisTurn(player.Creature);
        hot.Counters[(int)HotCounterSlot.SkillPlayStartsThisTurn] = combat.GetSkillCardsPlayedThisTurn(player.Creature);
        hot.Counters[(int)HotCounterSlot.CardsExhaustedThisTurn] = combat.WasCardExhaustedThisTurn(player.Creature) ? 1 : 0;
        hot.Counters[(int)HotCounterSlot.CardsDiscardedThisTurn] = combat.GetCardsDiscardedThisTurn(player.Creature);
        hot.Counters[(int)HotCounterSlot.EnergySpentThisTurn] = combat.GetEnergySpentThisTurn(player);
        hot.Counters[(int)HotCounterSlot.StarsGainedThisTurn] = combat.GetStarsGainedThisTurn(player);
        hot.Counters[(int)HotCounterSlot.NonHandDrawsThisTurn] = combat.GetNonHandDrawsThisTurn(player);
        hot.Counters[(int)HotCounterSlot.HasLostHpThisTurn] = combat.HasLostHpThisTurn(player.Creature) ? 1 : 0;
        hot.Counters[(int)HotCounterSlot.WasDoomAppliedThisTurn] = combat.WasDoomAppliedThisTurn(player.Creature) ? 1 : 0;
        hot.Counters[(int)HotCounterSlot.AttacksPlayedThisTurn] = combat.GetAttacksPlayedThisTurn(player.Creature);
        hot.Counters[(int)HotCounterSlot.SkillsPlayedThisTurn] = combat.GetSkillCardsPlayedThisTurn(player.Creature);
        hot.Counters[(int)HotCounterSlot.CardsPlayedThisTurn] = combat.GetCardsPlayedThisTurn(player.Creature);
        if (osty != null)
            hot.Counters[(int)HotCounterSlot.OstyAttacksThisTurn] = combat.GetCreatureAttacksThisTurn(osty);
        hot.Counters[(int)HotCounterSlot.SadnessGeneratedBefore] = combat.GetSadnessGeneratedBeforePrediction(player);

        hot.BrightestFlameMaxHpSpent = combat.BrightestFlameMaxHpSpent;
        hot.HistoryFinishedPlays = combat.GetFinishedPlaysBeforePrediction();
        hot.HistoryEtherealPlays = combat.GetEtherealPlaysBeforePrediction(player);
        hot.HistoryLightningChannels = combat.GetLightningChannelsBeforePrediction(player);
        hot.HistoryUnblockedHitsReceived = combat.GetUnblockedHitsBeforePrediction(player.Creature);
        hot.HistoryCardsDrawn = combat.GetCardsDrawnBeforePrediction(player);
        hot.HistoryCardsGenerated = combat.GetCardsGeneratedBeforePrediction(player);

        StringBuilder melodyText = new(32);
        combat.AppendMelodyState(melodyText, player);
        DecodeMelodyState(melodyText.ToString(), ref hot);

        // All 10 RNG streams from detached simulator
        hot.Rng[(int)HotRngStream.Shuffle] = HotRng.FromNative(sim.Rng.ShuffleState);
        hot.Rng[(int)HotRngStream.CombatCardGeneration] = HotRng.FromNative(sim.Rng.CombatCardGenerationState);
        hot.Rng[(int)HotRngStream.CombatPotionGeneration] = HotRng.FromNative(sim.Rng.CombatPotionGenerationState);
        hot.Rng[(int)HotRngStream.CombatCardSelection] = HotRng.FromNative(sim.Rng.CombatCardSelectionState);
        hot.Rng[(int)HotRngStream.CombatEnergyCosts] = HotRng.FromNative(sim.Rng.CombatEnergyCostsState);
        hot.Rng[(int)HotRngStream.CombatTargets] = HotRng.FromNative(sim.Rng.CombatTargetsState);
        hot.Rng[(int)HotRngStream.CombatOrbGeneration] = HotRng.FromNative(sim.Rng.CombatOrbGenerationState);
        hot.Rng[(int)HotRngStream.MonsterAi] = HotRng.FromNative(sim.Rng.MonsterAiState);
        hot.Rng[(int)HotRngStream.Niche] = HotRng.FromNative(sim.Rng.NicheState);
        hot.Rng[(int)HotRngStream.NonCombatGeneration] = HotRng.FromNative(sim.Rng.NonCombatGenerationState(player));

        CombatRulesSnapshot rulesSnapshot = combat.Rules;
        CardMultiplayerConstraint mpConstraint = combat.CardMultiplayerConstraint;
        FrozenGenerationPools pools = ctx.PreRegisterPoolsAndTokens(
            unlockedCharCards: player.Character.CardPool.GetUnlockedCards(player.UnlockState, mpConstraint),
            unlockedColorlessCards: ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(player.UnlockState, mpConstraint),
            unlockedCurseCards: ModelDb.CardPool<CurseCardPool>().GetUnlockedCards(player.UnlockState, mpConstraint),
            otherCharCards: combat.OtherCharacterCards(player),
            tarotCards: combat.TarotPool,
            mpConstraint: mpConstraint,
            rulesSnapshot: rulesSnapshot,
            hasRenewablePotionShapedRock: root.HasRenewablePotionShapedRock);

        bool isTheft = combat.Encounter?.Id.Entry is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK"
            || combat.Enemies.Any(static e => e.Monster is MegaCrit.Sts2.Core.Models.Monsters.GremlinMerc
                or MegaCrit.Sts2.Core.Models.Monsters.ThievingHopper
                or MegaCrit.Sts2.Core.Models.Monsters.FatGremlin);

        HotRules rules = FinalizeRules(
            ctx,
            ref hot,
            activeRelics,
            activeRelicDefIds,
            relicDefIdToStateSlot,
            rulesSnapshot,
            encounterGoldForfeited,
            baseMaxEnergy,
            enemySlotNames,
            enemyMonsterEntries,
            rootPets,
            rootAllyOwnerSlots,
            pools,
            root.HasRenewablePotionShapedRock,
            isBoss,
            isElite,
            isTheft,
            player.Character.Id.Entry,
            combat.Encounter?.Id.Entry ?? string.Empty,
            combat.Encounter?.Slots.ToArray() ?? [],
            combat.CurrentActIndex,
            root.TotalFloor,
            root.PostCombatRelicHeal.UnconditionalHeal,
            root.PostCombatRelicHeal.WoundedHeal,
            root.PostCombatRelicHeal.WoundedHpPercent);

        rootCold = ctx.MergeColdSidecar(rootCold);
        return new HotRootBundle(rules, hot, rootCold);
    }

    // =========================================================================
    // Finalization: build immutable HotRules + O(1) lookup tables + coverage
    // =========================================================================
    private static HotRules FinalizeRules(
        BuilderContext ctx,
        ref CombatHotState hot,
        List<HotActiveRelic> activeRelics,
        List<ushort> activeRelicDefIds,
        Dictionary<ushort, byte> relicDefIdToStateSlot,
        CombatRulesSnapshot rulesSnapshot,
        bool encounterGoldForfeited,
        int baseMaxEnergy,
        string[] enemySlotNames,
        string[] enemyMonsterEntries,
        List<HotRootPetSnapshot> rootPets,
        List<byte> rootAllyOwnerSlots,
        FrozenGenerationPools pools,
        bool hasRenewablePotionShapedRock,
        bool isBoss,
        bool isElite,
        bool isTheft,
        string characterEntry,
        string encounterEntry,
        string[] encounterSlots,
        int currentActIndex,
        int totalFloor,
        int postUncondHeal,
        int postWoundHeal,
        int postWoundPct)
    {
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

        // Compute ActiveHookMask = static relics | active powers | combat cards
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

        // Build dense O(1) enum-ID -> DefId tables
        ushort[] cardDefIdByCardId = new ushort[(int)HotCustomCardId.Count];
        for (int i = 1; i < ctx.Cards.Count; i++)
        {
            HotCardDef cd = ctx.Cards[i];
            if (cd.CardId != HotCustomCardId.None && cd.DefId == cd.CanonicalDefId)
                cardDefIdByCardId[(int)cd.CardId] = cd.DefId;
        }

        ushort[] powerDefIdByPowerId = new ushort[(int)HotPowerId.Count];
        for (int i = 1; i < ctx.Powers.Count; i++)
        {
            HotPowerDef pd = ctx.Powers[i];
            if (pd.PowerId != HotPowerId.None && powerDefIdByPowerId[(int)pd.PowerId] == 0)
                powerDefIdByPowerId[(int)pd.PowerId] = pd.DefId;
        }

        ushort[] relicDefIdByRelicId = new ushort[(int)HotRelicId.Count];
        byte[] relicStateSlotByRelicId = new byte[(int)HotRelicId.Count];
        Array.Fill(relicStateSlotByRelicId, (byte)0xFF);
        bool[] hasActiveRelicByRelicId = new bool[(int)HotRelicId.Count];
        for (int i = 0; i < activeRelics.Count; i++)
        {
            HotActiveRelic ar = activeRelics[i];
            HotRelicId rid = ctx.Relics[ar.RelicDefId].RelicId;
            if (rid != HotRelicId.None)
            {
                relicDefIdByRelicId[(int)rid] = ar.RelicDefId;
                hasActiveRelicByRelicId[(int)rid] = true;
                if (ar.StateSlot != 0xFF && relicStateSlotByRelicId[(int)rid] == 0xFF)
                    relicStateSlotByRelicId[(int)rid] = ar.StateSlot;
            }
        }

        ushort[] potionDefIdByPotionId = new ushort[(int)HotCustomPotionId.Count];
        for (int i = 1; i < ctx.Potions.Count; i++)
        {
            HotPotionDef pt = ctx.Potions[i];
            if (pt.PotionId != HotCustomPotionId.None && potionDefIdByPotionId[(int)pt.PotionId] == 0)
                potionDefIdByPotionId[(int)pt.PotionId] = pt.DefId;
        }

        ushort[] monsterDefIdByMonsterId = new ushort[(int)HotMonsterId.Count];
        for (int i = 1; i < ctx.Monsters.Count; i++)
        {
            HotMonsterDef md = ctx.Monsters[i];
            if (md.MonsterId != HotMonsterId.None && monsterDefIdByMonsterId[(int)md.MonsterId] == 0)
                monsterDefIdByMonsterId[(int)md.MonsterId] = md.DefId;
        }

        byte[] orbDefIdByOrbId = new byte[6];
        for (int i = 1; i < ctx.Orbs.Count; i++)
        {
            HotOrbId oid = ctx.Orbs[i].OrbId;
            if (oid != HotOrbId.None)
                orbDefIdByOrbId[(int)oid] = ctx.Orbs[i].DefId;
        }

        byte[] enchDefIdByEnchId = new byte[Enum.GetValues<HotEnchantmentId>().Length + 1];
        for (int i = 1; i < ctx.Enchantments.Count; i++)
        {
            int idx = (int)ctx.Enchantments[i].EnchantmentId;
            if ((uint)idx < (uint)enchDefIdByEnchId.Length)
                enchDefIdByEnchId[idx] = ctx.Enchantments[i].DefId;
        }

        byte[] afflDefIdByAfflId = new byte[Enum.GetValues<HotAfflictionId>().Length + 1];
        for (int i = 1; i < ctx.Afflictions.Count; i++)
        {
            int idx = (int)ctx.Afflictions[i].AfflictionId;
            if ((uint)idx < (uint)afflDefIdByAfflId.Length)
                afflDefIdByAfflId[idx] = ctx.Afflictions[i].DefId;
        }

        // Compute conservative 2-bit certified HP incumbent flag across active combat entities
        bool hasAnyHealOrUnknown = false;
        for (int i = 0; i < totalCards && !hasAnyHealOrUnknown; i++)
        {
            HotCardDef cd = ctx.Cards[hot.Cards[i].CardDefId];
            if (cd.CanHealPlayer || cd.CanGenerateUnknown)
                hasAnyHealOrUnknown = true;
        }
        for (int i = 0; i < hot.PowerCount && !hasAnyHealOrUnknown; i++)
        {
            HotPowerDef pd = ctx.Powers[hot.Powers[i].PowerDefId];
            if (pd.CanHealPlayer)
                hasAnyHealOrUnknown = true;
        }
        for (int i = 0; i < activeRelicDefIds.Count && !hasAnyHealOrUnknown; i++)
        {
            HotRelicDef rd = ctx.Relics[activeRelicDefIds[i]];
            if (rd.CanHealPlayer || rd.CanGenerateUnknown)
                hasAnyHealOrUnknown = true;
        }
        for (int s = 0; s < hot.PotionSlotCount && !hasAnyHealOrUnknown; s++)
        {
            ushort pId = hot.Potions[s];
            if (pId != 0)
            {
                HotPotionDef pt = ctx.Potions[pId];
                if (pt.CanHealPlayer || pt.CanGenerateUnknown)
                    hasAnyHealOrUnknown = true;
            }
        }

        HotCoverageManifest coverage = BuildCoverageManifest(ctx);

        ushort burningBloodId = relicDefIdByRelicId[(int)HotRelicId.BurningBlood];
        ushort oddMushroomId = ctx.RelicDefIdByEntry.GetValueOrDefault("ODD_MUSHROOM");
        ushort paperPhrogId = relicDefIdByRelicId[(int)HotRelicId.PaperPhrog];

        return new HotRules
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
            PotionEffectOps = ctx.PotionEffectOps.ToArray(),
            MonsterMachines = ctx.MonsterMachines.ToArray(),
            CardDefIdByCardId = cardDefIdByCardId,
            PowerDefIdByPowerId = powerDefIdByPowerId,
            RelicDefIdByRelicId = relicDefIdByRelicId,
            RelicStateSlotByRelicId = relicStateSlotByRelicId,
            HasActiveRelicByRelicId = hasActiveRelicByRelicId,
            PotionDefIdByPotionId = potionDefIdByPotionId,
            MonsterDefIdByMonsterId = monsterDefIdByMonsterId,
            OrbDefIdByOrbId = orbDefIdByOrbId,
            EnchDefIdByEnchId = enchDefIdByEnchId,
            AfflDefIdByAfflId = afflDefIdByAfflId,
            UnlockedCharacterCardPool = pools.UnlockedCharacter,
            UnlockedColorlessCardPool = pools.UnlockedColorless,
            UnlockedCurseCardPool = pools.UnlockedCurse,
            OtherCharacterCardPool = pools.OtherCharacter,
            TarotCardPool = pools.Tarot,
            CombatCharacterAttackPool = pools.CombatCharAttacks,
            CombatCharacterSkillPool = pools.CombatCharSkills,
            CombatCharacterPowerPool = pools.CombatCharPowers,
            CombatColorlessPool = pools.CombatColorless,
            UnlockedPotionPool = pools.UnlockedPotions,
            Dominion = HotMonsterCatalog.FindDominionByEntry(rulesSnapshot.DominionId),
            HasAnyHealOrUnknownGeneration = hasAnyHealOrUnknown,
            HasRenewablePotionShapedRock = hasRenewablePotionShapedRock,
            IsBossEncounter = isBoss,
            IsEliteEncounter = isElite,
            IsTheftEncounter = isTheft,
            CharacterEntry = characterEntry,
            EncounterEntry = encounterEntry,
            EncounterSlots = encounterSlots,
            CurrentActIndex = currentActIndex,
            TotalFloor = totalFloor,
            PostCombatRelicUnconditionalHeal = postUncondHeal,
            PostCombatRelicWoundedHeal = postWoundHeal,
            PostCombatRelicWoundedHpPercent = postWoundPct,
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
            VulnerablePowerDefId = powerDefIdByPowerId[(int)HotPowerId.VulnerablePower],
            WeakPowerDefId = powerDefIdByPowerId[(int)HotPowerId.WeakPower],
            FrailPowerDefId = powerDefIdByPowerId[(int)HotPowerId.FrailPower],
            StrengthPowerDefId = powerDefIdByPowerId[(int)HotPowerId.StrengthPower],
            DexterityPowerDefId = powerDefIdByPowerId[(int)HotPowerId.DexterityPower],
            MetallicizePowerDefId = ctx.PowerDefIdByEntry.GetValueOrDefault("METALLICIZE_POWER"),
            PlatingPowerDefId = powerDefIdByPowerId[(int)HotPowerId.PlatingPower],
            BarricadePowerDefId = powerDefIdByPowerId[(int)HotPowerId.BarricadePower],
            ArtifactPowerDefId = powerDefIdByPowerId[(int)HotPowerId.ArtifactPower],
            ThornsPowerDefId = powerDefIdByPowerId[(int)HotPowerId.ThornsPower],
            RitualPowerDefId = powerDefIdByPowerId[(int)HotPowerId.RitualPower],
            BurningBloodRelicDefId = burningBloodId,
            OddMushroomRelicDefId = oddMushroomId,
            PaperFrogRelicDefId = paperPhrogId,
            HasBurningBlood = hasActiveRelicByRelicId[(int)HotRelicId.BurningBlood],
            HasOddMushroom = oddMushroomId != 0 && activeRelicDefIds.Contains(oddMushroomId),
            HasPaperFrog = hasActiveRelicByRelicId[(int)HotRelicId.PaperPhrog],
        };
    }

    // =========================================================================
    // Creature / Enemy / Pile / Card / Power / Relic Helpers
    // =========================================================================
    private static int FindWuppyPetIndex(List<Creature> nonOstyPets)
    {
        for (int i = 0; i < nonOstyPets.Count; i++)
        {
            if (nonOstyPets[i].Monster is MegaCrit.Sts2.Core.Models.Monsters.Wuppy)
                return i;
        }
        return nonOstyPets.Count > 0 ? 0 : -1;
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

    private static HotCreature BuildSimCreature(Creature creature, SimCreatureState simState, byte slotIdx, HotCreatureFlags roleFlags)
    {
        HotCreatureFlags flags = roleFlags | HotCreatureFlags.ActiveInRoster;
        if (simState.CurrentHp > 0 && simState.IsAlive)
            flags |= HotCreatureFlags.Alive;

        return new HotCreature
        {
            CombatId = creature.CombatId ?? uint.MaxValue,
            CurrentHp = simState.CurrentHp,
            MaxHp = simState.MaxHp,
            Block = simState.Block,
            Flags = flags,
            SlotIdx = slotIdx,
            Pad = 0,
        };
    }

    private static HotEnemy BuildLiveEnemy(
        BuilderContext ctx,
        CombatState combat,
        Creature creature,
        byte enemySlot,
        bool isBoss,
        bool isElite)
    {
        HotEnemy enemy = default;
        enemy.Vital = BuildCreature(creature, slotIdx: (byte)(3 + enemySlot), HotCreatureFlags.None);
        enemy.DeathPhase = ComputeLiveDeathPhase(creature, combat);
        enemy.CurrentMoveNode = -1;

        MonsterModel? monster = creature.Monster;
        if (monster == null)
            return enemy;

        ushort monsterDefId = ctx.RegisterEnemyMonster(monster, isBoss, isElite, extraMoveIds: null, out HotMonsterMachine? machine);
        enemy.MonsterDefId = monsterDefId;

        if (machine != null)
        {
            if (monster.NextMove is { } nextMove)
                enemy.CurrentMoveNode = machine.FindNode(nextMove.Id);

            if (monster.MoveStateMachine is { } liveMachine)
            {
                IReadOnlyList<MonsterState> stateLog = liveMachine.StateLog;
                string[] logIds = new string[stateLog.Count];
                for (int i = 0; i < stateLog.Count; i++)
                    logIds[i] = stateLog[i].Id;
                PopulateEnemyStateLog(ctx, ref enemy, enemySlot, machine, logIds);
            }
        }

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
                enemy.AiRegs[0] = EncodeFabricatorSpawn(spawnedId);
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
            case "Aeonglass":
                enemy.AiRegs[0] = MonsterValueReader.ReadInt(monster, "_additionalStrength");
                enemy.AiRegs[1] = MonsterValueReader.ReadInt(monster, "_witherUpgradeCount");
                break;
            case "WaterfallGiant":
                enemy.AiRegs[1] = MonsterValueReader.ReadInt(monster, "_steamEruptionDamage");
                enemy.SteamPhase = monster.NextMove?.Id switch
                {
                    "ABOUT_TO_BLOW_MOVE" => 1,
                    "EXPLODE_MOVE" => 2,
                    _ => 0,
                };
                break;
        }

        enemy.Flags = eFlags;
        return enemy;
    }

    private static HotEnemy BuildSimEnemy(
        BuilderContext ctx,
        Creature creature,
        SimCreatureState simEnemy,
        byte enemySlot,
        string moveId,
        string aiLogCsv,
        string msField,
        bool isBoss,
        bool isElite)
    {
        HotEnemy enemy = default;
        enemy.Vital = BuildSimCreature(creature, simEnemy, slotIdx: (byte)(3 + enemySlot), HotCreatureFlags.None);
        enemy.CurrentMoveNode = -1;

        string[] logIds = string.IsNullOrEmpty(aiLogCsv)
            ? []
            : aiLogCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);

        string[] extraIds = logIds.Length == 0
            ? (moveId != "null" ? [moveId] : [])
            : (moveId != "null" ? [.. logIds, moveId] : logIds);

        MonsterModel? monster = creature.Monster;
        if (monster != null)
        {
            ushort monsterDefId = ctx.RegisterEnemyMonster(monster, isBoss, isElite, extraIds, out HotMonsterMachine? machine);
            enemy.MonsterDefId = monsterDefId;

            if (machine != null)
            {
                if (moveId != "null")
                    enemy.CurrentMoveNode = machine.FindNode(moveId);
                PopulateEnemyStateLog(ctx, ref enemy, enemySlot, machine, logIds);
            }

            // Decode msField: "{stateText}/{deathPhase}"
            int slash = msField.LastIndexOf('/');
            string statePart = slash >= 0 ? msField[..slash] : msField;
            if (slash >= 0 && int.TryParse(msField.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dp))
                enemy.DeathPhase = (HotDeathPhase)dp;

            HotEnemyFlags eFlags = HotEnemyFlags.None;
            switch (ctx.Monsters[monsterDefId].StateKind)
            {
                case HotMonsterStateKind.FrogKnight:
                    if (statePart == "1") eFlags |= HotEnemyFlags.BeetleCharged;
                    break;
                case HotMonsterStateKind.TwoTailedRat:
                case HotMonsterStateKind.TestSubject:
                {
                    int comma = statePart.IndexOf(',');
                    if (comma >= 0)
                    {
                        _ = int.TryParse(statePart.AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r0);
                        _ = int.TryParse(statePart.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r1);
                        enemy.AiRegs[0] = r0;
                        enemy.AiRegs[1] = r1;
                    }
                    break;
                }
                case HotMonsterStateKind.Fabricator:
                    enemy.AiRegs[0] = EncodeFabricatorSpawn(statePart);
                    break;
                case HotMonsterStateKind.ToughEgg:
                    if (statePart == "1") eFlags |= HotEnemyFlags.Hatched;
                    break;
                case HotMonsterStateKind.Tunneler:
                    if (statePart == "1") eFlags |= HotEnemyFlags.Stunned;
                    break;
                case HotMonsterStateKind.AwakeCheck:
                    if (statePart == "1") eFlags |= HotEnemyFlags.Awake;
                    break;
                case HotMonsterStateKind.Queen:
                    if (statePart == "1") eFlags |= HotEnemyFlags.AmalgamDied;
                    break;
            }
            switch (ctx.Monsters[monsterDefId].TypeName)
            {
                case "KnowledgeDemon":
                    enemy.AiRegs[0] = MonsterValueReader.ReadInt(monster, "_curseOfKnowledgeCounter");
                    break;
                case "TheObscura":
                    if (MonsterValueReader.ReadBool(monster, "_hasSummoned"))
                        eFlags |= HotEnemyFlags.HasSummoned;
                    break;
                case "Aeonglass":
                    enemy.AiRegs[0] = MonsterValueReader.ReadInt(monster, "_additionalStrength");
                    enemy.AiRegs[1] = MonsterValueReader.ReadInt(monster, "_witherUpgradeCount");
                    break;
                case "WaterfallGiant":
                    enemy.AiRegs[1] = MonsterValueReader.ReadInt(monster, "_steamEruptionDamage");
                    enemy.SteamPhase = moveId switch
                    {
                        "ABOUT_TO_BLOW_MOVE" => 1,
                        "EXPLODE_MOVE" => 2,
                        _ => 0,
                    };
                    break;
            }
            enemy.Flags = eFlags;
        }

        return enemy;
    }

    private static void PopulateEnemyStateLog(
        BuilderContext ctx,
        ref HotEnemy enemy,
        byte enemySlot,
        HotMonsterMachine machine,
        string[] logIds)
    {
        if (logIds.Length == 0)
            return;

        ushort[] fullIds = new ushort[logIds.Length];
        ulong everUsed = 0;
        for (int i = 0; i < logIds.Length; i++)
        {
            short nodeId = machine.FindNode(logIds[i]);
            ushort uNode = nodeId >= 0 ? (ushort)nodeId : (ushort)0;
            fullIds[i] = uNode;
            if (uNode < 64)
                everUsed |= 1UL << uNode;
        }

        enemy.EverUsedMovesMask = everUsed;
        if (logIds.Length <= HotLimits.MaxMonsterRecentMoves)
        {
            enemy.RecentMoveCount = (byte)logIds.Length;
            for (int i = 0; i < logIds.Length; i++)
                enemy.RecentMoves[i] = (byte)fullIds[i];
        }
        else
        {
            enemy.RecentMoveCount = HotLimits.MaxMonsterRecentMoves;
            int start = logIds.Length - HotLimits.MaxMonsterRecentMoves;
            for (int i = 0; i < HotLimits.MaxMonsterRecentMoves; i++)
                enemy.RecentMoves[i] = (byte)fullIds[start + i];
            ctx.AddMonsterLogOverflow(enemySlot, fullIds);
        }
    }

    private static int EncodeFabricatorSpawn(string entry)
        => entry switch
        {
            "GUARDBOT" => 1,
            "NOISEBOT" => 2,
            "ZAPBOT"   => 3,
            "STABBOT"  => 4,
            _          => 0,
        };

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

    private static int ImportLivePile(
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
            HotCard hotCard = BuildCard(ctx, liveCard, dupeSource: null, uid, ref nextCardUid, ref rootCold);
            hot.Cards[cursor++] = hotCard;
            cardToUid[liveCard] = uid;
        }
        return cards.Count;
    }

    private static int ImportSimPile(
        BuilderContext ctx,
        SimCardPile pile,
        ref CombatHotState hot,
        ref HotColdSidecar? rootCold,
        ref int cursor,
        ref ushort nextCardUid,
        List<ushort> allCardUids)
    {
        IReadOnlyList<PredictedCard> cards = pile.Cards;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cursor >= HotLimits.MaxCards)
                throw new HotCapacityExceededException($"Total combat card count exceeds HotLimits.MaxCards ({HotLimits.MaxCards}).");

            PredictedCard predicted = cards[i];
            ushort uid = nextCardUid++;
            HotCard hotCard = BuildCard(ctx, predicted.Preview, predicted.DupeSource?.Preview, uid, ref nextCardUid, ref rootCold);
            hot.Cards[cursor++] = hotCard;
            allCardUids.Add(uid);
        }
        return cards.Count;
    }

    private static HotCard BuildCard(
        BuilderContext ctx,
        CardModel card,
        CardModel? dupeSource,
        ushort cardUid,
        ref ushort nextCardUid,
        ref HotColdSidecar? rootCold)
    {
        ushort cardDefId = ctx.EnsureCardDefForLiveInstance(card, dupeSource, out short var0Delta, out short var1Delta);
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

    private static HotPower BuildLivePower(
        BuilderContext ctx,
        PowerModel power,
        byte ownerSlot,
        Dictionary<Creature, byte> creatureToOwnerSlot,
        Dictionary<CardModel, ushort> cardToUid,
        IReadOnlyList<ArrowSlot> arrows,
        ref CombatHotState hot,
        ref ushort nextCardUid,
        ref HotColdSidecar? rootCold)
    {
        ushort powerDefId = ctx.EnsurePowerDef(power);
        byte applierSlot = power.Applier != null && creatureToOwnerSlot.TryGetValue(power.Applier, out byte aSlot) ? aSlot : (byte)0xFF;
        byte targetSlot = power.Target != null && creatureToOwnerSlot.TryGetValue(power.Target, out byte tSlot) ? tSlot : (byte)0xFF;

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
                HotCard detached = BuildCard(ctx, selected, dupeSource: null, cardUid: 0, ref nextCardUid, ref rootCold);
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
                PackImpaledPower(impaled, ownerSlot, arrows, ref hot, out aux0, out aux1);
                break;

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

        return new HotPower
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

    private static HotPower BuildSimPower(
        BuilderContext ctx,
        CombatPredictionSimulator sim,
        SimulatedCombatState combat,
        PowerModel power,
        byte ownerSlot,
        Dictionary<Creature, byte> creatureToOwnerSlot,
        List<ushort> allCardUids,
        Queue<int> showOffIndices,
        IReadOnlyList<ArrowSlot> arrows,
        ref CombatHotState hot,
        ref ushort nextCardUid,
        ref HotColdSidecar? rootCold)
    {
        ushort powerDefId = ctx.EnsurePowerDef(power);
        byte applierSlot = power.Applier != null && creatureToOwnerSlot.TryGetValue(power.Applier, out byte aSlot) ? aSlot : (byte)0xFF;
        byte targetSlot = power.Target != null && creatureToOwnerSlot.TryGetValue(power.Target, out byte tSlot) ? tSlot : (byte)0xFF;

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
                aux0 = (int)PowerPredictionStateSupport.SurroundedFacing(sim, surrounded);
                break;

            case NightmarePower nightmare:
            {
                PredictedCard selected = combat.GetNightmareSelection(nightmare);
                HotCard detached = BuildCard(ctx, selected.Preview, selected.DupeSource?.Preview, cardUid: 0, ref nextCardUid, ref rootCold);
                aux0 = ctx.AddDetachedCard(detached);
                break;
            }

            case OrbitPower orbit:
                aux0 = combat.GetOrbitEnergyRemainder(orbit);
                break;

            case ShowOffPower:
                if (ownerSlot == 0 && showOffIndices.Count > 0)
                {
                    int idx = showOffIndices.Dequeue();
                    if ((uint)idx < (uint)allCardUids.Count)
                        auxCardUid = allCardUids[idx];
                }
                break;

            case ImpaledPower impaled:
                PackImpaledPower(impaled, ownerSlot, arrows, ref hot, out aux0, out aux1);
                break;

            case AimPower aim:
            {
                string desc = AimPowerMirrors.Describe(aim, sim);
                if (desc != "ready")
                {
                    pFlags |= HotPowerFlags.BoolAux;
                    if (desc.StartsWith("latched:", StringComparison.Ordinal)
                        && int.TryParse(desc.AsSpan("latched:".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cardIdx)
                        && (uint)cardIdx < (uint)allCardUids.Count)
                    {
                        auxCardUid = allCardUids[cardIdx];
                    }
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

        return new HotPower
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

    private static void PackImpaledPower(
        ImpaledPower impaled,
        byte ownerSlot,
        IReadOnlyList<ArrowSlot> arrows,
        ref CombatHotState hot,
        out int aux0,
        out int aux1)
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

    private static void CapturePostCombatRelicHeal(
        IEnumerable<RelicModel> relics,
        out int unconditionalHeal,
        out int woundedHeal,
        out int woundedHpPercent)
    {
        unconditionalHeal = 0;
        woundedHeal = 0;
        woundedHpPercent = 0;
        foreach (RelicModel relic in relics)
        {
            if (relic.IsMelted)
                continue;
            switch (relic)
            {
                case BurningBlood or BlackBlood:
                    unconditionalHeal += relic.DynamicVars.Heal.IntValue;
                    break;
                case MeatOnTheBone:
                    woundedHeal += relic.DynamicVars.Heal.IntValue;
                    woundedHpPercent = Math.Max(woundedHpPercent, relic.DynamicVars["HpThreshold"].IntValue);
                    break;
            }
        }
    }

    private static void CaptureLiveHistoryAndMelodies(CombatState combatState, Player player, ref CombatHotState hot)
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
                        if (!drawn.FromHandDraw) nonHandDraws++;
                        if (drawn.Card.Type == CardType.Status) statusCardsDrawn++;
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
                            if (started.CardPlay.Card.Tags.Contains(melodyTags[m]))
                                melodyStarted[m]++;
                    }
                    break;

                case CardPlayFinishedEntry finished:
                    finishedPlays++;
                    if (ReferenceEquals(finished.CardPlay.Player, player))
                    {
                        if (finished.WasEthereal) etherealPlays++;
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
                            if (anyMelody) melodyCardsFinished++;
                        }
                    }
                    break;

                case OrbChanneledEntry orb when ReferenceEquals(orb.Actor.Player, player):
                    if (orb.Orb is LightningOrb) lightningChannels++;
                    break;

                case DamageReceivedEntry dmg when ReferenceEquals(dmg.Receiver, player.Creature):
                    if (dmg.Result.UnblockedDamage > 0)
                    {
                        unblockedHits++;
                        if (thisTurn) hasLostHpThisTurn = 1;
                    }
                    break;

                case CardGeneratedEntry gen when ReferenceEquals(gen.Creator, player):
                    cardsGenerated++;
                    if (gen.Card is Sadness) sadnessGenerated++;
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

    // ---- Parsers for detached SimulatedCombatState continuation snippets ----
    private static string[] ParseIndexedContinuationFields(string text, string prefix, int count)
    {
        string[] result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = string.Empty;

        foreach (string seg in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = seg.IndexOf('=');
            if (eq <= prefix.Length || !seg.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (int.TryParse(seg.AsSpan(prefix.Length, eq - prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                && (uint)idx < (uint)count)
            {
                result[idx] = seg[(eq + 1)..];
            }
        }
        return result;
    }

    private static Dictionary<string, Queue<(int Curr, int Prev)>> ParseStatefulRelics(string text)
    {
        Dictionary<string, Queue<(int, int)>> map = new(StringComparer.Ordinal);
        int eq = text.IndexOf('=');
        if (eq < 0 || eq + 1 >= text.Length)
            return map;
        foreach (string item in text[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = item.Split('/');
            if (parts.Length < 3) continue;
            _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int c);
            _ = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p);
            if (!map.TryGetValue(parts[0], out Queue<(int, int)>? q))
                map[parts[0]] = q = new Queue<(int, int)>();
            q.Enqueue((c, p));
        }
        return map;
    }

    private static Dictionary<string, Queue<string>> ParseMirrorRelics(string text)
    {
        Dictionary<string, Queue<string>> map = new(StringComparer.Ordinal);
        int eq = text.IndexOf('=');
        if (eq < 0 || eq + 1 >= text.Length)
            return map;
        foreach (string item in text[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int slash = item.IndexOf('/');
            if (slash <= 0) continue;
            string entry = item[..slash];
            string val = item[(slash + 1)..];
            if (!map.TryGetValue(entry, out Queue<string>? q))
                map[entry] = q = new Queue<string>();
            q.Enqueue(val);
        }
        return map;
    }

    private static HotRelicState DecodeSimRelicState(
        string entry,
        HotRelicSpecialKind kind,
        Dictionary<string, Queue<(int Curr, int Prev)>> statefulMap,
        Dictionary<string, Queue<string>> mirrorMap)
    {
        if (kind == HotRelicSpecialKind.RainbowRing)
        {
            _ = statefulMap.TryGetValue(entry, out Queue<(int, int)>? sq) && sq.TryDequeue(out _);
            if (mirrorMap.TryGetValue(entry, out Queue<string>? mq) && mq.TryDequeue(out string? val))
            {
                string[] parts = val.Split(':');
                int atk = parts.Length > 0 && int.TryParse(parts[0], out int a) ? a : 0;
                int skl = parts.Length > 1 && int.TryParse(parts[1], out int s) ? s : 0;
                int pwr = parts.Length > 2 && int.TryParse(parts[2], out int p) ? p : 0;
                int act = parts.Length > 3 && int.TryParse(parts[3], out int ac) ? ac : 0;
                return new HotRelicState
                {
                    Current = (atk & 0xFFFF) | ((skl & 0xFFFF) << 16),
                    PreviousOrAux = (pwr & 0xFFFF) | ((act & 0xFFFF) << 16),
                };
            }
            return default;
        }

        if (statefulMap.TryGetValue(entry, out Queue<(int Curr, int Prev)>? stQ) && stQ.TryDequeue(out (int Curr, int Prev) st))
            return new HotRelicState { Current = st.Curr, PreviousOrAux = st.Prev };

        if (mirrorMap.TryGetValue(entry, out Queue<string>? mirQ) && mirQ.TryDequeue(out string? raw))
        {
            switch (kind)
            {
                case HotRelicSpecialKind.BeatingRemnant:
                    _ = decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal dmg);
                    return new HotRelicState { Current = (int)dmg };

                case HotRelicSpecialKind.JossPaper:
                {
                    int ethIdx = raw.IndexOf(":ethereal=", StringComparison.Ordinal);
                    if (ethIdx >= 0)
                    {
                        _ = int.TryParse(raw.AsSpan(0, ethIdx), out int ex);
                        _ = int.TryParse(raw.AsSpan(ethIdx + ":ethereal=".Length), out int eth);
                        return new HotRelicState { Current = ex, PreviousOrAux = eth };
                    }
                    _ = int.TryParse(raw, out int exhausted);
                    return new HotRelicState { Current = exhausted };
                }

                case HotRelicSpecialKind.PaelsLegion:
                {
                    string[] parts = raw.Split(':');
                    int cd = parts.Length > 0 && int.TryParse(parts[0], out int c) ? c : 0;
                    int tb = parts.Length > 1 && parts[1] == "1" ? 1 : 0;
                    int acp = parts.Length > 2 && parts[2] == "1" ? 2 : 0;
                    return new HotRelicState { Current = cd, PreviousOrAux = tb | acp };
                }

                default:
                    _ = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v);
                    return new HotRelicState { Current = v };
            }
        }

        return default;
    }

    private static Queue<int> ParseShowOffIndices(string text)
    {
        Queue<int> indices = new();
        int eq = text.IndexOf('=');
        if (eq < 0 || eq + 1 >= text.Length)
            return indices;
        foreach (string item in text[(eq + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (item == "null")
            {
                indices.Enqueue(-1);
                continue;
            }
            int colon = item.IndexOf(':');
            if (colon > 0 && int.TryParse(item.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                indices.Enqueue(idx);
            else
                indices.Enqueue(-1);
        }
        return indices;
    }

    private static void DecodeMelodyState(string text, ref CombatHotState hot)
    {
        int eq = text.IndexOf('=');
        if (eq < 0 || eq + 1 >= text.Length)
            return;
        string[] parts = text[(eq + 1)..].Split('/');
        if (parts.Length < 3)
            return;
        string[] started = parts[0].Split(',');
        string[] finished = parts[1].Split(',');
        for (int i = 0; i < 3; i++)
        {
            if (i < started.Length && int.TryParse(started[i], out int sVal))
                hot.Fool.MelodyStarted[i] = (byte)Math.Clamp(sVal, 0, byte.MaxValue);
            if (i < finished.Length && int.TryParse(finished[i], out int fVal))
                hot.Fool.MelodyFinished[i] = (byte)Math.Clamp(fVal, 0, byte.MaxValue);
        }
        if (int.TryParse(parts[2], out int cardsFin))
            hot.Fool.MelodyCardsFinished = (byte)Math.Clamp(cardsFin, 0, byte.MaxValue);
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
                int bit = BitOperations.TrailingZeroCount(mask);
                (buckets[bit] ??= []).Add((byte)i);
                mask &= mask - 1;
            }
        }

        for (int b = 0; b < 64; b++)
            table[b] = buckets[b]?.ToArray() ?? [];

        return table;
    }

    private static HotCoverageManifest BuildCoverageManifest(BuilderContext ctx)
    {
        bool[] supportedCards = new bool[ctx.Cards.Count];
        for (int i = 1; i < ctx.Cards.Count; i++)
            supportedCards[i] = ctx.Cards[i].CardId != HotCustomCardId.None;

        bool[] supportedPowers = new bool[ctx.Powers.Count];
        for (int i = 1; i < ctx.Powers.Count; i++)
            supportedPowers[i] = ctx.Powers[i].PowerId != HotPowerId.None;

        bool[] supportedRelics = new bool[ctx.Relics.Count];
        for (int i = 1; i < ctx.Relics.Count; i++)
            supportedRelics[i] = ctx.Relics[i].RelicId != HotRelicId.None;

        bool[] supportedMonsters = new bool[ctx.Monsters.Count];
        for (int i = 1; i < ctx.Monsters.Count; i++)
            supportedMonsters[i] = ctx.Monsters[i].MonsterId != HotMonsterId.None;

        return new HotCoverageManifest
        {
            SupportedCardDefs = supportedCards,
            SupportedPowerDefs = supportedPowers,
            SupportedRelicDefs = supportedRelics,
            SupportedMonsterDefs = supportedMonsters,
            IsRootFullySupported = true,
            UnsupportedReasons = [],
        };
    }

    private sealed record FrozenGenerationPools(
        ushort[] UnlockedCharacter,
        ushort[] UnlockedColorless,
        ushort[] UnlockedCurse,
        ushort[] OtherCharacter,
        ushort[] Tarot,
        ushort[] CombatCharAttacks,
        ushort[] CombatCharSkills,
        ushort[] CombatCharPowers,
        ushort[] CombatColorless,
        ushort[] UnlockedPotions);

    // =========================================================================
    // Per-root BuilderContext
    // =========================================================================
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
        public readonly List<HotCardEffectOp> PotionEffectOps = [];
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
            Orbs.Add(new HotOrbDef { DefId = 0, OrbId = HotOrbId.None, Entry = string.Empty, BasePassiveVal = 0, BaseEvokeVal = 0 });
            Enchantments.Add(new HotEnchantmentDef { DefId = 0, EnchantmentId = HotEnchantmentId.None, Entry = string.Empty, HasMomentumExtraDamage = false });
            Afflictions.Add(new HotAfflictionDef { DefId = 0, AfflictionId = HotAfflictionId.None, Entry = string.Empty });
            Monsters.Add(new HotMonsterDef
            {
                DefId = 0, MonsterId = HotMonsterId.None, Entry = string.Empty, TypeName = string.Empty,
                StateKind = HotMonsterStateKind.None, MachineIndex = -1,
            });
        }

        public void PreRegisterCatalogPowersAndOrbs()
        {
            // Pre-register ALL 304 vanilla powers at exact (ushort)HotPowerId indices (1..Count-1)
            // with canonical semantic DynamicVars so newly applied powers serialize with exact ContinuationStamp parity.
            CanonicalPowerTemplate?[] powerTemplates = CanonicalPowerTemplates.Value;
            HotPowerCatalogEntry[] powerEntries = HotPowerCatalog.ById;
            for (int i = 1; i < powerEntries.Length; i++)
            {
                ref readonly HotPowerCatalogEntry pe = ref powerEntries[i];
                CanonicalPowerTemplate? tpl = powerTemplates[i];
                ushort defId = (ushort)Powers.Count;

                Powers.Add(new HotPowerDef
                {
                    DefId = defId,
                    PowerId = pe.Id,
                    Domain = pe.Domain,
                    Entry = pe.Entry,
                    Type = pe.Type,
                    StackType = pe.StackType,
                    HookMask = pe.HookMask,
                    CanHealPlayer = pe.CanHealPlayer,
                    HasRelevantTurnStartAmount = pe.HasRelevantTurnStartAmount,
                    HasRelevantSkipDurationTick = pe.HasRelevantSkipDurationTick,
                    SetupWeight = pe.SetupWeight,
                    OrderedVarNames = tpl?.OrderedVarNames ?? [],
                    DefaultVarValues = tpl?.DefaultVarValues ?? [],
                    OrderedVarStrings = tpl?.OrderedVarStrings,
                    IsInstanced = tpl?.IsInstanced ?? false,
                    SpecialKind = ClassifyPowerSpecialById(pe.Id),
                });
                PowerDefIdByEntry[pe.Entry] = defId;
            }

            // Pre-register all 5 vanilla Orbs at exact HotOrbId indices (1..5)
            EnsureOrbDef(ModelDb.Orb<LightningOrb>());
            EnsureOrbDef(ModelDb.Orb<FrostOrb>());
            EnsureOrbDef(ModelDb.Orb<DarkOrb>());
            EnsureOrbDef(ModelDb.Orb<PlasmaOrb>());
            EnsureOrbDef(ModelDb.Orb<GlassOrb>());
        }

        public ushort EnsurePowerDefById(HotPowerId powerId)
        {
            int idx = (int)powerId;
            return (uint)idx < (uint)Powers.Count ? (ushort)idx : (ushort)0;
        }

        public ushort EnsurePowerDef(PowerModel power)
        {
            string entry = power.Id.Entry;
            ExtractSemanticVars(power, out string[] varNames, out decimal[] varValues, out string?[]? varStrings);
            HotPowerId catalogId = HotPowerCatalog.FindByEntry(entry);
            ref readonly HotPowerCatalogEntry ce = ref HotPowerCatalog.Get(catalogId);

            if (PowerDefIdByEntry.TryGetValue(entry, out ushort existingId))
            {
                HotPowerDef existing = Powers[existingId];
                if (existing.SpecialKind == HotPowerSpecialKind.Impaled
                    && existing.OrderedVarNames.Length == 1 && varNames.Length == 1
                    && existing.OrderedVarNames[0] == "HitCounter" && varNames[0] == "HitCounter"
                    && existing.Type == power.Type)
                {
                    return existingId;
                }
                if (existing.OrderedVarNames.Length == 0 && varNames.Length == 0 && existing.Type == power.Type
                    && existing.StackType == power.StackType && existing.IsInstanced == (power.InstanceType != PowerInstanceType.None))
                {
                    return existingId;
                }
                if (VarsMatchExact(existing.OrderedVarNames, existing.DefaultVarValues, existing.OrderedVarStrings,
                        varNames, varValues, varStrings)
                    && existing.Type == power.Type)
                {
                    return existingId;
                }
            }

            ushort defId = (ushort)Powers.Count;
            HotPowerDef def = new()
            {
                DefId = defId,
                PowerId = catalogId,
                Domain = ce.Domain,
                Entry = entry,
                Type = power.Type,
                HookMask = ce.HookMask,
                CanHealPlayer = ce.CanHealPlayer,
                HasRelevantTurnStartAmount = power is DrawCardsNextTurnPower or SummonNextTurnPower or HelloWorldPower or GodByrdPower,
                HasRelevantSkipDurationTick = PowerLifecycleSupport.UsesNativeDurationSkip(power.GetType()),
                SetupWeight = ce.SetupWeight,
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

        public ushort EnsureCardDefForLiveInstance(CardModel liveCard, CardModel? explicitDupeSource, out short var0Delta, out short var1Delta)
        {
            ushort canonicalId = EnsureCanonicalCardDef(liveCard);
            HotCardDef canonical = Cards[canonicalId];

            ExtractSemanticVars(liveCard, out string[] liveNames, out decimal[] liveValues, out string?[]? liveStrings);

            string? dupeKey = liveCard.IsDupe
                ? CardChoiceSupport.ChoiceCardKey(explicitDupeSource ?? liveCard.DupeOf
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

            var0Delta = 0;
            var1Delta = 0;
            ushort variantId = (ushort)Cards.Count;
            ExtractCardScalars(liveNames, liveValues, out short dmg, out short blk, out short magic, out byte hits, out byte v0, out byte v1);

            HotCardDef variant = new()
            {
                DefId = variantId,
                CanonicalDefId = canonicalId,
                CardId = canonical.CardId,
                CustomCardId = canonical.CustomCardId,
                Domain = canonical.Domain,
                CalcVar = canonical.CalcVar,
                Entry = canonical.Entry,
                Type = liveCard.Type,
                Rarity = liveCard.Rarity,
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
                GainsBlock = canonical.GainsBlock,
                CanBeGeneratedInCombat = canonical.CanBeGeneratedInCombat,
                MaxUpgradeLevel = canonical.MaxUpgradeLevel,
                PrivateKind = canonical.PrivateKind,
                IsStrikeTag = canonical.IsStrikeTag,
                IsDefendTag = canonical.IsDefendTag,
                IsShootTag = canonical.IsShootTag,
                IsOstyAttackTag = canonical.IsOstyAttackTag,
                IsShivTag = canonical.IsShivTag,
                IsMinionTag = canonical.IsMinionTag,
                MelodyTagMask = canonical.MelodyTagMask,
                RootDupeSourceKey = dupeKey,
            };
            Cards.Add(variant);
            return variantId;
        }

        public ushort EnsureCanonicalCardDef(CardModel sample)
        {
            string entry = sample.Id.Entry;
            if (CardDefIdByEntry.TryGetValue(entry, out ushort existing))
                return existing;

            ushort defId = (ushort)Cards.Count;
            CardDefIdByEntry[entry] = defId;

            CanonicalCardTemplate tpl = CanonicalCardCache.GetOrAdd(entry, _ => BuildCanonicalCardTemplate(sample));
            HotCustomCardId cardId = HotCardCatalog.FindByEntry(entry);
            ref readonly HotCardCatalogEntry ce = ref HotCardCatalog.Get(cardId);

            HotCardCatalog.CompileCardBytecode(
                cardId,
                defId,
                EnsurePowerDefById,
                EffectOps,
                out int opStart,
                out byte opCount,
                out HotCustomCardId customCardId);

            HotCardDef def = new()
            {
                DefId = defId,
                CanonicalDefId = defId,
                CardId = cardId,
                CustomCardId = customCardId,
                Domain = ce.Domain,
                CalcVar = ce.CalcVar,
                Entry = entry,
                Type = tpl.Type,
                Rarity = tpl.Rarity,
                Target = tpl.Target,
                BaseCost = tpl.BaseCost,
                UpgradedCost = tpl.UpgradedCost,
                BaseStarCost = tpl.BaseStarCost,
                BaseKeywords = tpl.BaseKeywords,
                BaseDamage = tpl.BaseDamage,
                UpgradedDamage = tpl.UpgradedDamage,
                BaseBlock = tpl.BaseBlock,
                UpgradedBlock = tpl.UpgradedBlock,
                BaseMagic = tpl.BaseMagic,
                UpgradedMagic = tpl.UpgradedMagic,
                BaseHits = tpl.BaseHits,
                UpgradedHits = tpl.UpgradedHits,
                HookMask = ce.HookMask,
                CanHealPlayer = ce.CanHealPlayer,
                CanGenerateUnknown = ce.CanGenerateUnknown,
                SetupWeight = ce.SetupWeight,
                OpStart = opStart,
                OpCount = opCount,
                CalcVarKind = (byte)ce.CalcVar,
                CustomHandlerId = (ushort)customCardId,
                OrderedVarNames = tpl.OrderedVarNames,
                BaseVarValues = tpl.BaseVarValues,
                UpgradedVarValues = tpl.UpgradedVarValues,
                OrderedVarStrings = tpl.OrderedVarStrings,
                Var0Index = tpl.Var0Index,
                Var1Index = tpl.Var1Index,
                IsAllowedInBlitz = tpl.IsAllowedInBlitz,
                GainsBlock = tpl.GainsBlock,
                CanBeGeneratedInCombat = tpl.CanBeGeneratedInCombat,
                MaxUpgradeLevel = tpl.MaxUpgradeLevel,
                PrivateKind = tpl.PrivateKind,
                IsStrikeTag = tpl.IsStrikeTag,
                IsDefendTag = tpl.IsDefendTag,
                IsShootTag = tpl.IsShootTag,
                IsOstyAttackTag = tpl.IsOstyAttackTag,
                IsShivTag = tpl.IsShivTag,
                IsMinionTag = tpl.IsMinionTag,
                MelodyTagMask = tpl.MelodyTagMask,
            };

            Cards.Add(def);
            return defId;
        }

        private static CanonicalCardTemplate BuildCanonicalCardTemplate(CardModel sample)
        {
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

            byte melodyMask = 0;
            bool isStrike = false, isDefend = false, isShoot = false, isOstyAtk = false, isShiv = false, isMinion = false;
            foreach (CardTag tag in baseModel.Tags)
            {
                switch (tag)
                {
                    case CardTag.Strike: isStrike = true; break;
                    case CardTag.Defend: isDefend = true; break;
                    case CardTag.Shoot: isShoot = true; break;
                    case CardTag.OstyAttack: isOstyAtk = true; break;
                    case CardTag.Shiv: isShiv = true; break;
                    case CardTag.Minion: isMinion = true; break;
                    case CardTag.Wah: melodyMask |= 1; break;
                    case CardTag.Cho: melodyMask |= 2; break;
                    case CardTag.Ki: melodyMask |= 4; break;
                }
            }

            return new CanonicalCardTemplate
            {
                Type = baseModel.Type,
                Rarity = baseModel.Rarity,
                Target = baseModel.TargetType,
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
                OrderedVarNames = baseNames,
                BaseVarValues = baseValues,
                UpgradedVarValues = upgValues,
                OrderedVarStrings = baseStrings,
                Var0Index = v0Idx,
                Var1Index = v1Idx,
                IsAllowedInBlitz = baseModel.IsAllowedInBlitz,
                GainsBlock = baseModel.GainsBlock,
                CanBeGeneratedInCombat = baseModel.CanBeGeneratedInCombat,
                MaxUpgradeLevel = baseModel.MaxUpgradeLevel,
                PrivateKind = ClassifyCardPrivate(baseModel),
                IsStrikeTag = isStrike,
                IsDefendTag = isDefend,
                IsShootTag = isShoot,
                IsOstyAttackTag = isOstyAtk,
                IsShivTag = isShiv,
                IsMinionTag = isMinion,
                MelodyTagMask = melodyMask,
            };
        }

        public ushort EnsureRelicDef(RelicModel relic)
        {
            string entry = relic.Id.Entry;
            if (RelicDefIdByEntry.TryGetValue(entry, out ushort existing))
                return existing;

            ushort defId = (ushort)Relics.Count;
            RelicDefIdByEntry[entry] = defId;

            HotRelicId relicId = HotRelicCatalog.FindByEntry(entry);
            ref readonly HotRelicCatalogEntry ce = ref HotRelicCatalog.Get(relicId);
            HotRelicSpecialKind special = ClassifyRelicSpecial(relic, out bool isStateful, out bool isTracked);
            short primaryVal = 0;
            short secondaryVal = 0;
            foreach (KeyValuePair<string, DynamicVar> kv in relic.DynamicVars)
            {
                if (primaryVal == 0)
                    primaryVal = (short)Math.Clamp(kv.Value.IntValue, short.MinValue, short.MaxValue);
                else if (secondaryVal == 0)
                {
                    secondaryVal = (short)Math.Clamp(kv.Value.IntValue, short.MinValue, short.MaxValue);
                    break;
                }
            }

            Relics.Add(new HotRelicDef
            {
                DefId = defId,
                RelicId = relicId,
                Entry = entry,
                HookMask = ce.HookMask,
                IsStateful = isStateful,
                IsTracked = isTracked,
                CanHealPlayer = ce.CanHealPlayer,
                CanGenerateUnknown = ce.CanGenerateUnknown,
                SpecialKind = special,
                PrimaryValue = primaryVal,
                SecondaryValue = secondaryVal,
            });
            return defId;
        }

        public ushort EnsurePotionDef(PotionModel potion, bool hasRenewablePotionShapedRock)
        {
            string entry = potion.Id.Entry;
            if (PotionDefIdByEntry.TryGetValue(entry, out ushort existing))
                return existing;

            ushort defId = (ushort)Potions.Count;
            PotionDefIdByEntry[entry] = defId;

            HotCustomPotionId potionId = HotPotionCatalog.FindByEntry(entry);
            ref readonly HotPotionCatalogEntry pe = ref HotPotionCatalog.Get(potionId);

            HotPotionCatalog.CompilePotionBytecode(
                potionId,
                EnsurePowerDefById,
                PotionEffectOps,
                out int opStart,
                out byte opCount,
                out HotCustomPotionId customPotionId);

            short primaryVal = 0;
            short secondaryVal = 0;
            foreach (KeyValuePair<string, DynamicVar> kv in potion.DynamicVars)
            {
                if (kv.Value is StringVar or CalculatedVar)
                    continue;
                if (primaryVal == 0)
                    primaryVal = (short)Math.Clamp(kv.Value.IntValue, short.MinValue, short.MaxValue);
                else if (secondaryVal == 0)
                {
                    secondaryVal = (short)Math.Clamp(kv.Value.IntValue, short.MinValue, short.MaxValue);
                    break;
                }
            }

            short strategicCost = potion.Rarity == PotionRarity.Token
                || (hasRenewablePotionShapedRock && potion is PotionShapedRock)
                ? (short)0
                : pe.BaseStrategicHpCost;

            Potions.Add(new HotPotionDef
            {
                DefId = defId,
                PotionId = potionId,
                CustomPotionId = customPotionId,
                Entry = entry,
                Rarity = potion.Rarity,
                Target = potion.TargetType,
                CanHealPlayer = pe.CanHealPlayer,
                CanGenerateUnknown = pe.CanGenerateUnknown,
                PrimaryValue = primaryVal,
                SecondaryValue = secondaryVal,
                OpStart = opStart,
                OpCount = opCount,
                StrategicHpCost = strategicCost,
                RequiresOpeningUse = pe.RequiresOpeningUse,
                GeneratesPotionChain = pe.GeneratesPotionChain,
                IsCombatUsable = pe.IsCombatUsable,
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
            HotOrbId orbId = HotMonsterCatalog.FindOrbByEntry(entry);
            (short basePassive, int baseEvoke) = orbId switch
            {
                HotOrbId.Lightning => ((short)3, 8),
                HotOrbId.Frost => ((short)2, 5),
                HotOrbId.Dark => ((short)6, 6),
                HotOrbId.Plasma => ((short)1, 2),
                HotOrbId.Glass => ((short)4, 8),
                _ => orb.IsCanonical ? ((short)0, 0) : ((short)orb.PassiveVal, (int)orb.EvokeVal),
            };
            Orbs.Add(new HotOrbDef
            {
                DefId = defId,
                OrbId = orbId,
                Entry = entry,
                BasePassiveVal = basePassive,
                BaseEvokeVal = baseEvoke,
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
                EnchantmentId = HotMonsterCatalog.FindEnchantmentByEntry(entry),
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
                AfflictionId = HotMonsterCatalog.FindAfflictionByEntry(entry),
                Entry = entry,
            });
            return defId;
        }

        public ushort RegisterEnemyMonster(
            MonsterModel monster,
            bool isBoss,
            bool isElite,
            string[]? extraMoveIds,
            out HotMonsterMachine? compiledMachine)
        {
            ushort defId = (ushort)Monsters.Count;
            string entry = monster.Id.Entry;
            string typeName = monster.GetType().Name;
            HotMonsterId monsterId = HotMonsterCatalog.FindByEntry(entry);
            if (monsterId == HotMonsterId.None)
                monsterId = HotMonsterCatalog.FindByTypeName(typeName);

            IReadOnlyDictionary<string, int> staticInts = MonsterMoveEffects.CaptureStaticIntValues(monster);
            int testSubjectBaseMultiClaw = typeName == "TestSubject"
                ? MonsterValueReader.ReadInt(monster, "BaseMultiClawCount")
                : 0;

            int machineIndex = -1;
            compiledMachine = null;
            if (monster.MoveStateMachine is { } liveMachine)
            {
                compiledMachine = CompileMonsterMachine(defId, monsterId, monster, liveMachine, staticInts, extraMoveIds);
                machineIndex = MonsterMachines.Count;
                MonsterMachines.Add(compiledMachine);
            }

            HotMonsterDef def = new()
            {
                DefId = defId,
                MonsterId = monsterId,
                Entry = entry,
                TypeName = typeName,
                StateKind = ClassifyMonsterState(typeName),
                MachineIndex = machineIndex,
                StaticInts = staticInts,
                TestSubjectBaseMultiClawCount = testSubjectBaseMultiClaw,
                MinInitialHp = monster.MinInitialHp,
                MaxInitialHp = monster.MaxInitialHp,
                IsBoss = isBoss,
                IsElite = isElite,
            };
            Monsters.Add(def);
            MonsterDefIdByEntry.TryAdd(entry, defId);
            return defId;
        }

        public void PreRegisterSpawnableMonsters(bool isBoss, bool isElite)
        {
            Queue<HotMonsterId> queue = new();
            HashSet<HotMonsterId> seen = [];
            for (int i = 1; i < Monsters.Count; i++)
            {
                HotMonsterId mid = Monsters[i].MonsterId;
                if (mid != HotMonsterId.None && seen.Add(mid))
                    queue.Enqueue(mid);
            }

            while (queue.Count > 0)
            {
                HotMonsterId cur = queue.Dequeue();
                ReadOnlySpan<HotMonsterId> children = HotMonsterCatalog.GetSpawnClosure(cur);
                for (int i = 0; i < children.Length; i++)
                {
                    HotMonsterId child = children[i];
                    if (!seen.Add(child))
                        continue;
                    queue.Enqueue(child);
                    if (HotMonsterCatalog.TryGetCanonicalMonsterModel(child) is { } canonical)
                    {
                        MonsterModel mutable = canonical.IsCanonical ? canonical.ToMutable() : canonical;
                        if (mutable.MoveStateMachine == null)
                            mutable.SetUpForCombat();
                        RegisterEnemyMonster(mutable, isBoss, isElite, extraMoveIds: null, out _);
                    }
                }
            }
        }

        public FrozenGenerationPools PreRegisterPoolsAndTokens(
            IEnumerable<CardModel> unlockedCharCards,
            IEnumerable<CardModel> unlockedColorlessCards,
            IEnumerable<CardModel> unlockedCurseCards,
            IEnumerable<CardModel> otherCharCards,
            IEnumerable<CardModel> tarotCards,
            CardMultiplayerConstraint mpConstraint,
            CombatRulesSnapshot rulesSnapshot,
            bool hasRenewablePotionShapedRock)
        {
            // Pre-register all Status and Token cards so fixed-card generation (Shiv, Dazed, Wound, Burn, Soul, SovereignBlade, etc.) is always O(1).
            foreach (CardModel card in ModelDb.CardPool<StatusCardPool>().AllCards)
                EnsureCanonicalCardDef(card);
            foreach (CardModel card in ModelDb.CardPool<TokenCardPool>().AllCards)
                EnsureCanonicalCardDef(card);

            ushort[] RegisterPool(IEnumerable<CardModel> src)
            {
                List<ushort> ids = [];
                HashSet<ushort> dedupe = [];
                foreach (CardModel c in src)
                {
                    if (!rulesSnapshot.Allows(c))
                        continue;
                    ushort id = EnsureCanonicalCardDef(c);
                    if (dedupe.Add(id))
                        ids.Add(id);
                }
                return ids.ToArray();
            }

            // Match exact STS2 CardFactory.FilterForCombat + multiplayer constraint + rulesSnapshot.Allows
            // so TakeRandom/NextItem RNG indices are bit-identical to Legacy RootCombatCardGenerationPoolSnapshot.
            ushort[] charPool = RegisterPool(unlockedCharCards.FilterForCombatAndPlayerCount(mpConstraint));
            ushort[] colorlessPool = RegisterPool(unlockedColorlessCards);
            ushort[] combatColorless = RegisterPool(unlockedColorlessCards.FilterForCombatAndPlayerCount(mpConstraint));
            ushort[] cursePool = RegisterPool(unlockedCurseCards);
            ushort[] otherCharPool = RegisterPool(otherCharCards.FilterForCombatAndPlayerCount(mpConstraint));
            ushort[] tarotPool = RegisterPool(tarotCards);

            List<ushort> charAttacks = [];
            List<ushort> charSkills = [];
            List<ushort> charPowers = [];
            for (int i = 0; i < charPool.Length; i++)
            {
                HotCardDef def = Cards[charPool[i]];
                switch (def.Type)
                {
                    case CardType.Attack: charAttacks.Add(def.DefId); break;
                    case CardType.Skill: charSkills.Add(def.DefId); break;
                    case CardType.Power: charPowers.Add(def.DefId); break;
                }
            }

            List<ushort> potionPool = [];
            foreach (PotionModel potion in ModelDb.AllPotions)
            {
                if (potion.GetType().Assembly != typeof(AbstractModel).Assembly)
                    continue;
                HotCustomPotionId pid = HotPotionCatalog.FindByEntry(potion.Id.Entry);
                if (pid == HotCustomPotionId.None)
                    continue;
                ushort pDefId = EnsurePotionDef(potion, hasRenewablePotionShapedRock);
                if (Potions[pDefId].IsCombatUsable && potion.Rarity != PotionRarity.Token)
                    potionPool.Add(pDefId);
            }

            return new FrozenGenerationPools(
                UnlockedCharacter: charPool,
                UnlockedColorless: colorlessPool,
                UnlockedCurse: cursePool,
                OtherCharacter: otherCharPool,
                Tarot: tarotPool,
                CombatCharAttacks: charAttacks.ToArray(),
                CombatCharSkills: charSkills.ToArray(),
                CombatCharPowers: charPowers.ToArray(),
                CombatColorless: combatColorless,
                UnlockedPotions: potionPool.ToArray());
        }

        private HotMonsterMachine CompileMonsterMachine(
            ushort monsterDefId,
            HotMonsterId monsterId,
            MonsterModel monster,
            MonsterMoveStateMachine liveMachine,
            IReadOnlyDictionary<string, int> staticInts,
            string[]? extraMoveIds)
        {
            string typeName = monster.GetType().Name;

            List<string> orderedIds = new(liveMachine.States.Count + 4);
            Dictionary<string, short> idToNode = new(StringComparer.Ordinal);

            void AddId(string id)
            {
                if (!string.IsNullOrEmpty(id) && !idToNode.ContainsKey(id))
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
            if (extraMoveIds != null)
            {
                for (int i = 0; i < extraMoveIds.Length; i++)
                    AddId(extraMoveIds[i]);
            }

            HotMonsterNode[] nodes = new HotMonsterNode[orderedIds.Count];
            for (int i = 0; i < orderedIds.Count; i++)
            {
                string id = orderedIds[i];
                liveMachine.States.TryGetValue(id, out MonsterState? mState);
                if (mState == null && monster.NextMove?.Id == id)
                    mState = monster.NextMove;

                if (mState is MoveState move)
                {
                    List<HotMonsterAttack> attacks = [];
                    foreach (AbstractIntent intent in move.Intents)
                    {
                        if (intent is AttackIntent attack)
                        {
                            int bd = IntentForecaster.CaptureBaseDamage(attack, monster, move.Id);
                            int rep = Math.Max(1, attack.Repeats);
                            attacks.Add(new HotMonsterAttack((short)bd, (byte)Math.Clamp(rep, 1, byte.MaxValue)));
                        }
                    }

                    int baseDamage = attacks.Count > 0 ? attacks[0].BaseDamage : 0;
                    int repeats = attacks.Count > 0 ? attacks[0].Repeats : 0;

                    string? followUpId = move.FollowUpState?.Id ?? move.FollowUpStateId;
                    short nextNode = followUpId != null && idToNode.TryGetValue(followUpId, out short fId)
                        ? fId
                        : (short)-1;

                    HotMonsterMoveEffect[] effects = HotMonsterCatalog.CompileStandardMoveEffects(
                        monsterId, move.Id, staticInts, EnsurePowerDefById);

                    nodes[i] = new HotMonsterNode
                    {
                        NodeId = (short)i,
                        MoveId = id,
                        NodeKind = HotMonsterNodeKind.Move,
                        BaseDamage = baseDamage,
                        Repeats = repeats,
                        Attacks = attacks.Count > 0 ? attacks.ToArray() : [],
                        RemovesOwnerOnMove = HotMonsterCatalog.RemovesOwnerOnMove(monsterId, move.Id),
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
                    if (monster._creature != null)
                    {
                        try
                        {
                            string resolved = conditional.GetNextState(monster._creature, new Rng(0));
                            if (idToNode.TryGetValue(resolved, out short rId))
                                fallbackNode = rId;
                        }
                        catch (InvalidOperationException)
                        {
                            // Conditional branch unreachable at root; engine resolves dynamically if reached.
                        }
                        catch (NullReferenceException)
                        {
                            // Spawnable prototype without live CombatState; engine resolves dynamically if reached.
                        }
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

    // =========================================================================
    // Semantic DynamicVar extraction & entity classifiers
    // =========================================================================
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
            if (name is "Damage" or "OstyDamage")
            {
                damage = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
                var0Index = (byte)i;
            }
            else if (name == "Block")
            {
                block = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
                var1Index = (byte)i;
            }
            else if (name is "HitCount" or "Repeat")
            {
                hits = (byte)Math.Clamp((int)val, 1, byte.MaxValue);
            }
            else if (!magicFound && name is "Vulnerable" or "Weak" or "Frail" or "Cards" or "StrengthPower"
                or "PlatingPower" or "MetallicizePower" or "Magic" or "Energy" or "Stars" or "Heal"
                or "HpLoss" or "Forge" or "Summon" or "Poison" or "Doom" or "DexterityPower" or "FocusPower")
            {
                magic = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
                magicFound = true;
            }
        }

        if (!magicFound)
        {
            for (int i = 0; i < varNames.Length; i++)
            {
                if (i != var0Index && i != var1Index && varNames[i] is not ("HitCount" or "Repeat"))
                {
                    magic = (short)Math.Clamp(varValues[i], short.MinValue, short.MaxValue);
                    break;
                }
            }
        }

        if (var0Index == 0xFF && varNames.Length > 0)
        {
            for (int i = 0; i < varNames.Length; i++)
                if (i != var1Index) { var0Index = (byte)i; break; }
        }
        if (var1Index == 0xFF && varNames.Length > 1)
        {
            for (int i = 0; i < varNames.Length; i++)
                if (i != var0Index) { var1Index = (byte)i; break; }
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

    private static HotPowerSpecialKind ClassifyPowerSpecialById(HotPowerId id)
        => id switch
        {
            HotPowerId.SurroundedPower => HotPowerSpecialKind.Surrounded,
            HotPowerId.NightmarePower  => HotPowerSpecialKind.Nightmare,
            HotPowerId.OrbitPower      => HotPowerSpecialKind.Orbit,
            HotPowerId.ShowOffPower    => HotPowerSpecialKind.ShowOff,
            HotPowerId.ImpaledPower    => HotPowerSpecialKind.Impaled,
            HotPowerId.AimPower        => HotPowerSpecialKind.Aim,
            HotPowerId.EncorePower     => HotPowerSpecialKind.Encore,
            HotPowerId.YahPower        => HotPowerSpecialKind.Yah,
            HotPowerId.LullabyPower    => HotPowerSpecialKind.Lullaby,
            _                          => HotPowerSpecialKind.None,
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
