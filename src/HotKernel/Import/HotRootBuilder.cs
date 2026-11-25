using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
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
using MegaCrit.Sts2.Core.Factories;
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
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.ValueProps;
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
internal static partial class HotRootBuilder
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

    private static readonly FieldInfo HappyFlowerGrantEnergyField =
        typeof(HappyFlower).GetField("_grantEnergyThisTurn", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(HappyFlower).FullName, "_grantEnergyThisTurn");

    private static readonly FieldInfo FakeHappyFlowerGrantEnergyField =
        typeof(FakeHappyFlower).GetField("_grantEnergyThisTurn", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(FakeHappyFlower).FullName, "_grantEnergyThisTurn");

    private static readonly ConcurrentDictionary<string, CanonicalCardTemplate> CanonicalCardCache =
        new(StringComparer.Ordinal);

    private static readonly Lazy<CanonicalPowerTemplate?[]> CanonicalPowerTemplates =
        new(BuildCanonicalPowerTemplates);

    private sealed class CanonicalPowerTemplate
    {
        public required string[] OrderedVarNames { get; init; }
        public required decimal[] DefaultVarValues { get; init; }
        public string?[]? OrderedVarStrings { get; init; }
        public required PowerInstanceType InstanceType { get; init; }
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
                InstanceType = power.InstanceType,
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
        public required bool IsVisualColorless { get; init; }
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
        hot.PlayerPhase = (byte)pcs.Phase;
        hot.TerminalOutcome = HotCombatOutcome.Ongoing;

        int maxHandSize = RitsuLibFramework.GetMaxHandSize(player);
        hot.MaxHandSize = (byte)Math.Clamp(maxHandSize, 0, byte.MaxValue);

        int baseMaxEnergy = player.MaxEnergy;
        hot.Counters[(int)HotCounterSlot.BaseMaxEnergy] = baseMaxEnergy;
        hot.Counters[(int)HotCounterSlot.BaseHandDraw] = 5;

        HotCombatFlags combatFlags = HotCombatFlags.IsInProgress | HotCombatFlags.PlayerHooksActive;
        if (pcs.Phase is PlayerTurnPhase.Start or PlayerTurnPhase.AutoPrePlay)
            combatFlags |= HotCombatFlags.PendingTurnSetup;
        bool encounterGoldForfeited = HotImportSupport.ForfeitsEncounterGold(state);
        if (encounterGoldForfeited)
            combatFlags |= HotCombatFlags.EncounterGoldForfeited;
        if (state.Encounter is MegaCrit.Sts2.Core.Models.Encounters.BattlewornDummyEventEncounter dummyEnc
            && dummyEnc.RanOutOfTime)
        {
            combatFlags |= HotCombatFlags.BattlewornDummyTimedOut;
        }

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
        bool isWeak = state.Encounter?.IsWeak ?? false;
        int initialEncounterMonsterCount = state.Encounter is { IsMutable: true, HaveMonstersBeenGenerated: true } enc
            ? enc.MonstersWithSlots.Count
            : enemies.Count;

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

        // Pre-compile spawnable monster closure via Encounter.AllPossibleMonsters, enemy death-spawn powers, and BFS.
        ctx.PreRegisterSpawnableMonsters(state.Encounter, enemies, isBoss, isElite);

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
            // 0.113.1 GlassOrb.cs:15,21,23,38: store unmodified base _passiveVal in PassiveVal
            // so Focus is applied on read and Passive() decrements base _passiveVal rather than Focus.
            short passiveVal = orb is GlassOrb glassOrb
                ? (short)Math.Clamp((int)glassOrb._passiveVal, 0, short.MaxValue)
                : (short)orb.PassiveVal;
            hot.Orbs[i] = new HotOrb
            {
                OrbTypeId = orbDefId,
                Flags = 0,
                PassiveVal = passiveVal,
                EvokeVal = (int)orb.EvokeVal,
            };
        }

        // Active player relics & stateful/tracked relic state slots
        bool hasRenewablePotionShapedRock = player.Relics.Any(static r => !r.IsMelted && r is PetrifiedToad);
        bool isFurCoatActiveInCurrentRoom = false;
        if (state.RunState.CurrentMapPoint != null)
        {
            foreach (RelicModel relic in player.Relics)
            {
                if (!relic.IsMelted && relic is FurCoat furCoat
                    && furCoat.GetMarkedCoords() is { } markedCoords
                    && markedCoords.Contains(state.RunState.CurrentMapPoint.coord))
                {
                    isFurCoatActiveInCurrentRoom = true;
                    break;
                }
            }
        }
        CapturePostCombatRelicHeal(player.Relics, out int postUncondHeal, out int postWoundHeal, out int postWoundPct);
        int madScienceUpgradeCapacity = MadScienceGrowth.CaptureRemainingCapacity(state);

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
            hot.Fool.Arrows[i] = (ushort)((int)arrow.Status & HotFoolState.ArrowStatusMask);
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

                int powerIdx = powerCursor;
                hot.Powers[powerCursor++] = BuildLivePower(
                    ctx, state, power, powerIdx, ownerSlot, creatureToOwnerSlot, cardToUid, arrows, ref hot, ref nextCardUid, ref rootCold);
            }
        }
        hot.PowerCount = (byte)powerCursor;
        hot.NextCardUid = nextCardUid;

        // Combat history counters & melodies
        CaptureLiveHistoryAndMelodies(state, player, ref hot);
        if (hot.Counters[(int)HotCounterSlot.ShivPlaysFinishedThisTurn] > 0)
        {
            for (int i = 0; i < hot.PowerCount; i++)
            {
                if (hot.Powers[i].OwnerSlot == 0
                    && hot.Powers[i].PowerDefId < ctx.Powers.Count
                    && ctx.Powers[hot.Powers[i].PowerDefId].PowerId == HotPowerId.PhantomBladesPower)
                {
                    hot.Powers[i].Aux0 = 1;
                }
            }
        }

        // HistoryCourse: capture last non-dupe Attack from last turn (during TurnSetup) or this turn (during Play).
        if (player.Relics.Any(static r => !r.IsMelted && r is HistoryCourse)
            && CombatManager.Instance?.History is { } hcHistory)
        {
            bool isPrePlay = pcs.Phase is PlayerTurnPhase.Start or PlayerTurnPhase.AutoPrePlay;
            CardModel? hcCard = null;
            foreach (CardPlayFinishedEntry entry in hcHistory.CardPlaysFinished)
            {
                if (!ReferenceEquals(entry.CardPlay.Player, player))
                    continue;
                if (entry.CardPlay.Card.Type != CardType.Attack || entry.CardPlay.Card.IsDupe)
                    continue;
                bool turnMatch = isPrePlay
                    ? entry.HappenedLastPlayerTurn(player)
                    : entry.HappenedThisTurn(state);
                if (turnMatch)
                    hcCard = entry.CardPlay.Card;
            }
            if (hcCard != null)
            {
                ushort existingUid = cardToUid.GetValueOrDefault(hcCard);
                bool foundExisting = false;
                if (existingUid != 0)
                {
                    int totalCards = hot.TotalCardCount;
                    for (int c = 0; c < totalCards; c++)
                    {
                        if (hot.Cards[c].CardUid == existingUid)
                        {
                            hot.HistoryCourseCard = hot.Cards[c];
                            foundExisting = true;
                            break;
                        }
                    }
                }
                if (!foundExisting)
                    hot.HistoryCourseCard = BuildCard(ctx, hcCard, dupeSource: null, cardUid: existingUid, ref nextCardUid, ref rootCold);
                hot.HistoryCourseUid = existingUid;
                hot.CombatFlags |= HotCombatFlags.HasHistoryCourseCard;
                hot.NextCardUid = nextCardUid;
            }
        }

        // All 10 RNG streams
        hot.Rng[(int)HotRngStream.Shuffle] = HotImportSupport.CaptureRng(state.RunState.Rng.Shuffle);
        hot.Rng[(int)HotRngStream.CombatCardGeneration] = HotImportSupport.CaptureRng(state.RunState.Rng.CombatCardGeneration);
        hot.Rng[(int)HotRngStream.CombatPotionGeneration] = HotImportSupport.CaptureRng(state.RunState.Rng.CombatPotionGeneration);
        hot.Rng[(int)HotRngStream.CombatCardSelection] = HotImportSupport.CaptureRng(state.RunState.Rng.CombatCardSelection);
        hot.Rng[(int)HotRngStream.CombatEnergyCosts] = HotImportSupport.CaptureRng(state.RunState.Rng.CombatEnergyCosts);
        hot.Rng[(int)HotRngStream.CombatTargets] = HotImportSupport.CaptureRng(state.RunState.Rng.CombatTargets);
        hot.Rng[(int)HotRngStream.CombatOrbGeneration] = HotImportSupport.CaptureRng(state.RunState.Rng.CombatOrbGeneration);
        hot.Rng[(int)HotRngStream.MonsterAi] = HotImportSupport.CaptureRng(state.RunState.Rng.MonsterAi);
        hot.Rng[(int)HotRngStream.Niche] = HotImportSupport.CaptureRng(state.RunState.Rng.Niche);
        hot.Rng[(int)HotRngStream.NonCombatGeneration] = HotImportSupport.CaptureRng(player.PlayerRng.NonCombatGeneration);

        HotCombatRulesSnapshot rulesSnapshot = HotCombatRulesSnapshot.Capture(state);
        CardMultiplayerConstraint mpConstraint = state.RunState.CardMultiplayerConstraint;
        List<CardPoolModel> otherCharPools = player.UnlockState.CharacterCardPools.ToList();
        if (otherCharPools.Count > 1)
            otherCharPools.Remove(player.Character.CardPool);

        HotCardDomain playerDomain = MapCardPoolToDomain(player.Character.CardPool);
        IEnumerable<CardModel>?[] perDomainCharCards = new IEnumerable<CardModel>?[7];
        foreach (CardPoolModel charPoolModel in player.UnlockState.CharacterCardPools)
        {
            HotCardDomain domain = MapCardPoolToDomain(charPoolModel);
            if (domain != HotCardDomain.None && domain != playerDomain)
                perDomainCharCards[(int)domain] = charPoolModel.GetUnlockedCards(player.UnlockState, mpConstraint);
        }
        EnsureMissingVanillaCharacterDomainPool(perDomainCharCards, playerDomain, HotCardDomain.Ironclad, ModelDb.CardPool<IroncladCardPool>(), player.UnlockState, mpConstraint);
        EnsureMissingVanillaCharacterDomainPool(perDomainCharCards, playerDomain, HotCardDomain.Silent, ModelDb.CardPool<SilentCardPool>(), player.UnlockState, mpConstraint);
        EnsureMissingVanillaCharacterDomainPool(perDomainCharCards, playerDomain, HotCardDomain.Defect, ModelDb.CardPool<DefectCardPool>(), player.UnlockState, mpConstraint);
        EnsureMissingVanillaCharacterDomainPool(perDomainCharCards, playerDomain, HotCardDomain.Regent, ModelDb.CardPool<RegentCardPool>(), player.UnlockState, mpConstraint);
        EnsureMissingVanillaCharacterDomainPool(perDomainCharCards, playerDomain, HotCardDomain.Necrobinder, ModelDb.CardPool<NecrobinderCardPool>(), player.UnlockState, mpConstraint);
        EnsureMissingVanillaCharacterDomainPool(perDomainCharCards, playerDomain, HotCardDomain.Fool, ModelDb.CardPool<FoolCardPool>(), player.UnlockState, mpConstraint);

        FrozenGenerationPools pools = ctx.PreRegisterPoolsAndTokens(
            unlockedCharCards: player.Character.CardPool.GetUnlockedCards(player.UnlockState, mpConstraint),
            unlockedColorlessCards: ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(player.UnlockState, mpConstraint),
            unlockedCurseCards: ModelDb.CardPool<CurseCardPool>().GetUnlockedCards(player.UnlockState, mpConstraint),
            unlockedStatusCards: ModelDb.CardPool<StatusCardPool>().GetUnlockedCards(player.UnlockState, mpConstraint),
            unlockedTokenCards: ModelDb.CardPool<TokenCardPool>().GetUnlockedCards(player.UnlockState, mpConstraint),
            otherCharCards: otherCharPools.SelectMany(p => p.GetUnlockedCards(player.UnlockState, mpConstraint)),
            tarotCards: TarotCards.All.Where(card => card.IsAllowedInRun(state.RunState)),
            unlockedPotions: PotionFactory.GetPotionOptions(player),
            playerDomain: playerDomain,
            perDomainCharCards: perDomainCharCards,
            mpConstraint: mpConstraint,
            rulesSnapshot: rulesSnapshot,
            hasRenewablePotionShapedRock: hasRenewablePotionShapedRock);

        byte baseOrbSlotCount = (byte)Math.Clamp(player.Character.BaseOrbSlotCount, 0, byte.MaxValue);

        HotRules rules = FinalizeRules(
            ctx,
            ref hot,
            activeRelics,
            activeRelicDefIds,
            relicDefIdToStateSlot,
            rulesSnapshot,
            encounterGoldForfeited,
            baseMaxEnergy,
            baseOrbSlotCount,
            enemySlotNames,
            enemyMonsterEntries,
            rootPets,
            rootAllyOwnerSlots,
            pools,
            hasRenewablePotionShapedRock,
            isFurCoatActiveInCurrentRoom,
            isBoss,
            isElite,
            isWeak,
            HotImportSupport.IsTheftEncounter(state),
            player.Character.Id.Entry,
            state.Encounter?.Id.Entry ?? string.Empty,
            state.Encounter?.Slots.ToArray() ?? [],
            initialEncounterMonsterCount,
            state.RunState.CurrentActIndex,
            state.RunState.TotalFloor,
            postUncondHeal,
            postWoundHeal,
            postWoundPct,
            madScienceUpgradeCapacity);

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
        HotCombatRulesSnapshot rulesSnapshot,
        bool encounterGoldForfeited,
        int baseMaxEnergy,
        byte baseOrbSlotCount,
        string[] enemySlotNames,
        string[] enemyMonsterEntries,
        List<HotRootPetSnapshot> rootPets,
        List<byte> rootAllyOwnerSlots,
        FrozenGenerationPools pools,
        bool hasRenewablePotionShapedRock,
        bool isFurCoatActiveInCurrentRoom,
        bool isBoss,
        bool isElite,
        bool isWeak,
        bool isTheft,
        string characterEntry,
        string encounterEntry,
        string[] encounterSlots,
        int initialEncounterMonsterCount,
        int currentActIndex,
        int totalFloor,
        int postUncondHeal,
        int postWoundHeal,
        int postWoundPct,
        int madScienceUpgradeCapacity)
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
            if (hot.Powers[i].OwnerSlot != 0 || hot.Powers[i].Amount == 0)
                continue;
            HotPowerDef pd = ctx.Powers[hot.Powers[i].PowerDefId];
            if (pd.CanHealPlayer || PowerCanGenerateUnknownInCombat(pd.PowerId))
                hasAnyHealOrUnknown = true;
        }
        bool turn1SetupPending = hot.PlayerTurnNumber == 0
            || (hot.PlayerTurnNumber == 1 && (hot.CombatFlags & HotCombatFlags.PendingTurnSetup) != 0);
        for (int i = 0; i < activeRelics.Count && !hasAnyHealOrUnknown; i++)
        {
            HotActiveRelic ar = activeRelics[i];
            HotRelicDef rd = ctx.Relics[ar.RelicDefId];
            if ((rd.CanHealPlayer || rd.CanGenerateUnknown)
                && RelicCanHealOrGenerateRemainingInCombat(in hot, in ar, rd, turn1SetupPending))
            {
                hasAnyHealOrUnknown = true;
            }
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
            UnlockedStatusCardPool = pools.UnlockedStatus,
            UnlockedTokenCardPool = pools.UnlockedToken,
            OtherCharacterCardPool = pools.OtherCharacter,
            TarotCardPool = pools.Tarot,
            CombatCharacterAttackPool = pools.CombatCharAttacks,
            CombatCharacterSkillPool = pools.CombatCharSkills,
            CombatCharacterPowerPool = pools.CombatCharPowers,
            CombatColorlessPool = pools.CombatColorless,
            UnlockedPotionPool = pools.UnlockedPotions,
            UnlockedCharacterPoolsByDomain = pools.UnlockedCharPoolsByDomain,
            Dominion = HotMonsterCatalog.FindDominionByEntry(rulesSnapshot.DominionId),
            HasAnyHealOrUnknownGeneration = hasAnyHealOrUnknown,
            HasRenewablePotionShapedRock = hasRenewablePotionShapedRock,
            IsFurCoatActiveInCurrentRoom = isFurCoatActiveInCurrentRoom,
            IsBossEncounter = isBoss,
            IsEliteEncounter = isElite,
            IsWeakEncounter = isWeak,
            IsTheftEncounter = isTheft,
            CharacterEntry = characterEntry,
            EncounterEntry = encounterEntry,
            EncounterSlots = encounterSlots,
            InitialEncounterMonsterCount = initialEncounterMonsterCount,
            CurrentActIndex = currentActIndex,
            TotalFloor = totalFloor,
            PostCombatRelicUnconditionalHeal = postUncondHeal,
            PostCombatRelicWoundedHeal = postWoundHeal,
            PostCombatRelicWoundedHpPercent = postWoundPct,
            MadScienceUpgradeCapacity = madScienceUpgradeCapacity,
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
            BaseOrbSlotCount = baseOrbSlotCount,
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
        return -1;
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
                if (HotImportSupport.ReadMonsterBool(monster, "_hasBeetleCharged"))
                    eFlags |= HotEnemyFlags.BeetleCharged;
                break;
            case "TwoTailedRat":
                enemy.AiRegs[0] = HotImportSupport.ReadMonsterInt(monster, "_turnsUntilSummonable");
                enemy.AiRegs[1] = HotImportSupport.ReadMonsterInt(monster, "_callForBackupCount");
                break;
            case "Fabricator":
                string spawnedId = (HotImportSupport.ReadMonsterObject(monster, "_lastSpawned") as MonsterModel)?.Id.Entry ?? string.Empty;
                enemy.AiRegs[0] = EncodeFabricatorSpawn(spawnedId);
                break;
            case "ToughEgg":
                if (HotImportSupport.ReadMonsterBool(monster, "_isHatched"))
                    eFlags |= HotEnemyFlags.Hatched;
                break;
            case "TestSubject":
                enemy.AiRegs[0] = HotImportSupport.ReadMonsterInt(monster, "_respawns");
                enemy.AiRegs[1] = HotImportSupport.ReadMonsterInt(monster, "_extraMultiClawCount");
                break;
            case "Tunneler":
                if (HotImportSupport.ReadMonsterBool(monster, "_isStunned"))
                    eFlags |= HotEnemyFlags.Stunned;
                break;
            case "LagavulinMatriarch":
            case "SlumberingBeetle":
                if (HotImportSupport.ReadMonsterBool(monster, "_isAwake"))
                    eFlags |= HotEnemyFlags.Awake;
                break;
            case "Queen":
                if (HotImportSupport.ReadMonsterBool(monster, "_hasAmalgamDied"))
                    eFlags |= HotEnemyFlags.AmalgamDied;
                break;
            case "KnowledgeDemon":
                enemy.AiRegs[0] = HotImportSupport.ReadMonsterInt(monster, "_curseOfKnowledgeCounter");
                break;
            case "TheObscura":
                if (HotImportSupport.ReadMonsterBool(monster, "_hasSummoned"))
                    eFlags |= HotEnemyFlags.HasSummoned;
                break;
            case "Aeonglass":
                enemy.AiRegs[0] = HotImportSupport.ReadMonsterInt(monster, "_additionalStrength");
                enemy.AiRegs[1] = HotImportSupport.ReadMonsterInt(monster, "_witherUpgradeCount");
                break;
            case "WaterfallGiant":
                enemy.AiRegs[1] = HotImportSupport.ReadMonsterInt(monster, "_steamEruptionDamage");
                enemy.SteamPhase = monster.NextMove?.Id switch
                {
                    "ABOUT_TO_BLOW_MOVE" => 1,
                    "EXPLODE_MOVE" => 2,
                    _ => 0,
                };
                break;
            case "Wriggler":
                if (creature.SlotName is { Length: > 8 } wSlot
                    && wSlot.StartsWith("wriggler", StringComparison.Ordinal)
                    && int.TryParse(wSlot.AsSpan(8), out int wIdx)
                    && wIdx > 0)
                {
                    enemy.AiRegs[0] = wIdx;
                }
                break;
            case "Axebot":
            {
                // 0.113.1 Axebot.cs:54,116: encode RespawnCount (2 - StockAmount) as (respawnCount + 1)
                // so 0 = unset fallback to StaticInts["RespawnCount"], 1 = 0, 2 = 1, 3 = 2.
                int respawnCount = Math.Max(0, HotImportSupport.ReadMonsterInt(monster, "RespawnCount"));
                enemy.AiRegs[0] = (byte)Math.Clamp(respawnCount + 1, 1, byte.MaxValue);
                break;
            }
        }

        enemy.Flags = eFlags;
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

        if (creature.GetPower<ReattachPower>() is { } reattach)
        {
            bool allOtherReattachDead = combat.GetTeammatesOf(creature)
                .Where(other => !ReferenceEquals(other, creature) && other.HasPower<ReattachPower>())
                .All(static other => other.IsDead);
            if (allOtherReattachDead)
                return HotDeathPhase.PermanentlyDead;
            if (!reattach.ShouldAllowHitting(creature))
                return HotDeathPhase.Reviving;
        }

        if (creature.GetPower<AdaptablePower>() is { } adaptable && !adaptable.ShouldAllowHitting(creature))
            return HotDeathPhase.Reviving;

        if (creature.GetPower<IllusionPower>() is { } illusion
            && !illusion.ShouldAllowHitting(creature)
            && combat.Enemies.Any(e => !e.HasPower<MinionPower>()
                && (e.CurrentHp > 0
                    || (e.GetPower<AdaptablePower>() is { } ap && !ap.ShouldAllowHitting(e))
                    || (e.GetPower<ReattachPower>() is { } rp && !rp.ShouldAllowHitting(e)))))
        {
            return HotDeathPhase.Reviving;
        }

        string move = creature.Monster?.NextMove?.Id ?? string.Empty;
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

    private static HotCard BuildCard(
        BuilderContext ctx,
        CardModel card,
        CardModel? dupeSource,
        ushort cardUid,
        ref ushort nextCardUid,
        ref HotColdSidecar? rootCold)
    {
        ushort cardDefId = ctx.EnsureCardDefForLiveInstance(
            card,
            dupeSource,
            cardUid,
            out short var0Delta,
            out short var1Delta,
            out decimal var0DecimalDelta);
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
        if (card is Fetch && WasFetchPlayedThisTurn(card))
            flags |= HotCardFlags.FetchPlayedThisTurn;

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

        if (var0DecimalDelta != 0m && cardUid != 0)
            HotColdSidecar.SetDecimalVarDelta(ref rootCold, cardUid, var0DecimalDelta);

        return hotCard;
    }

    private static HotPower BuildLivePower(
        BuilderContext ctx,
        CombatState state,
        PowerModel power,
        int powerIdx,
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
        if (HotImportSupport.SemanticallyRelevantSkipNextDurationTick(power))
            pFlags |= HotPowerFlags.SkipNextDurationTick;
        if (power.InstanceType != PowerInstanceType.None)
            pFlags |= HotPowerFlags.Instanced;

        int amountOnTurnStart = HotImportSupport.SemanticallyRelevantAmountOnTurnStart(power);
        ushort auxCardUid = 0;
        int aux0 = 0;
        int aux1 = 0;

        switch (power)
        {
            case SurroundedPower surrounded:
                aux0 = (int)surrounded.Facing;
                break;

            case SkittishPower skittish:
                if (skittish.HasGainedBlockThisTurn)
                    pFlags |= HotPowerFlags.BoolAux;
                break;

            case NightmarePower nightmare:
            {
                CardModel? selected = nightmare.GetInternalData<NightmarePower.Data>().selectedCard;
                if (selected != null)
                {
                    HotCard detached = BuildCard(ctx, selected, dupeSource: null, cardUid: 0, ref nextCardUid, ref rootCold);
                    aux0 = ctx.AddDetachedCard(detached);
                }
                else
                {
                    aux0 = -1;
                }
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

            case TenderPower tender:
                aux0 = tender.DisplayAmount;
                break;

            case DampenPower dampen:
            {
                // 0.113.1 DampenPower.cs:13-76: preserve active casters bitmask (Aux0) and
                // downgradedCardsToOldUpgradeLevels (DampenOrigUpg + HotColdSidecar.DampenEntries).
                DampenPower.Data data = dampen.GetInternalData<DampenPower.Data>();
                foreach (Creature caster in data.casters)
                {
                    if (creatureToOwnerSlot.TryGetValue(caster, out byte cSlot) && cSlot >= 3)
                    {
                        int eSlot = cSlot - 3;
                        if (eSlot < HotLimits.MaxEnemies)
                            aux0 |= 1 << eSlot;
                    }
                }

                if (data.downgradedCardsToOldUpgradeLevels.Count > 0)
                {
                    List<HotDampenEntry> added = new(data.downgradedCardsToOldUpgradeLevels.Count);
                    int totalCards = hot.TotalCardCount;
                    foreach (KeyValuePair<CardModel, int> kvp in data.downgradedCardsToOldUpgradeLevels)
                    {
                        if (kvp.Value <= 0 || !cardToUid.TryGetValue(kvp.Key, out ushort uid))
                            continue;
                        byte origUpg = (byte)Math.Clamp(kvp.Value, 0, byte.MaxValue);
                        for (int c = 0; c < totalCards; c++)
                        {
                            if (hot.Cards[c].CardUid == uid)
                            {
                                hot.Cards[c].DampenOrigUpg = origUpg;
                                break;
                            }
                        }
                        added.Add(new HotDampenEntry(uid, origUpg, powerIdx));
                    }

                    if (added.Count > 0)
                    {
                        added.Sort(static (a, b) => a.CardUid.CompareTo(b.CardUid));
                        HotDampenEntry[]? prev = rootCold?.DampenEntries;
                        int prevLen = prev?.Length ?? 0;
                        HotDampenEntry[] merged = new HotDampenEntry[prevLen + added.Count];
                        if (prevLen > 0)
                            prev!.CopyTo(merged, 0);
                        added.CopyTo(merged, prevLen);
                        rootCold = rootCold == null
                            ? new HotColdSidecar(DampenEntries: merged)
                            : rootCold with { DampenEntries = merged };
                    }
                }
                break;
            }

            case NemesisPower nemesis:
                // 0.113.1 NemesisPower.cs:14,24-28: BoolAux mirrors _shouldApplyIntangible toggle.
                if (nemesis._shouldApplyIntangible)
                    pFlags |= HotPowerFlags.BoolAux;
                break;

            case ChainsOfBindingPower chains:
            {
                // 0.113.1 ChainsOfBindingPower.cs:20-74: Aux0 = boundCardPlayed, Aux1 = this-turn Bound affliction count.
                aux0 = chains.GetInternalData<ChainsOfBindingPower.Data>().boundCardPlayed ? 1 : 0;
                if (CombatManager.Instance?.History is { } chainsHistory)
                {
                    int boundCount = 0;
                    foreach (CombatHistoryEntry entry in chainsHistory.Entries)
                    {
                        if (entry is CardAfflictedEntry affl
                            && affl.HappenedThisTurn(state)
                            && ReferenceEquals(affl.Actor, chains.Owner)
                            && affl.Affliction is MegaCrit.Sts2.Core.Models.Afflictions.Bound)
                        {
                            boundCount++;
                        }
                    }
                    aux1 = boundCount;
                }
                break;
            }

            case JugglingPower juggling:
            {
                // 0.113.1 JugglingPower.cs:16-54: preserve mid-turn attack count (Aux0) and pending clone card.
                JugglingPower.Data data = juggling.GetInternalData<JugglingPower.Data>();
                aux0 = data.attacksPlayedThisTurn;
                if (data.cardToClone is { } cloneCard && cardToUid.TryGetValue(cloneCard, out ushort cloneUid))
                    auxCardUid = cloneUid;
                break;
            }

            case FeralPower feral:
                // 0.113.1 FeralPower.cs:15-35: preserve mid-turn zero-cost attacks played count (Aux0).
                aux0 = feral.GetInternalData<FeralPower.Data>().zeroCostAttacksPlayed;
                break;

            case WitheringPresencePower withering:
                // 0.113.1 WitheringPresencePower.cs:24,28,50: preserve CardsLeft (Aux0) across pre-capture Imbued auto-plays.
                aux0 = withering.DynamicVars["CardsLeft"].IntValue;
                break;

            case PossessStrengthPower possessStrength:
                // 0.113.1 PossessStrengthPower.cs:15,53,65: stolenStrength accumulates negative Strength deltas per player creature;
                // HotMonsters.RefundPossessedStats checks Aux0 < 0 and refunds -Aux0 on death.
                if (possessStrength.GetInternalData<PossessStrengthPower.Data>() is { stolenStrength: { } stolenStrength })
                {
                    decimal totalStolen = 0m;
                    foreach (KeyValuePair<Creature, decimal> kvp in stolenStrength)
                    {
                        if (creatureToOwnerSlot.TryGetValue(kvp.Key, out byte cSlot) && cSlot == 0)
                            totalStolen += kvp.Value;
                    }
                    aux0 = (int)totalStolen;
                }
                break;

            case PossessSpeedPower possessSpeed:
                // 0.113.1 PossessSpeedPower.cs:15,53,65: stolenDexterity accumulates negative Dexterity deltas per player creature;
                // HotMonsters.RefundPossessedStats checks Aux0 < 0 and refunds -Aux0 on death.
                if (possessSpeed.GetInternalData<PossessSpeedPower.Data>() is { stolenDexterity: { } stolenDexterity })
                {
                    decimal totalStolen = 0m;
                    foreach (KeyValuePair<Creature, decimal> kvp in stolenDexterity)
                    {
                        if (creatureToOwnerSlot.TryGetValue(kvp.Key, out byte cSlot) && cSlot == 0)
                            totalStolen += kvp.Value;
                    }
                    aux0 = (int)totalStolen;
                }
                break;

            case SlowPower slow:
                // 0.113.1 SlowPower.cs:24-51: preserve mid-turn SlowAmount count (Aux0).
                aux0 = slow.DynamicVars["SlowAmount"].IntValue;
                break;

            case HardenedShellPower hardenedShell:
                // 0.113.1 HardenedShellPower.cs:14-87: preserve turn-accumulated damageReceivedThisTurn (Aux0).
                aux0 = (int)hardenedShell.GetInternalData<HardenedShellPower.Data>().damageReceivedThisTurn;
                break;

            case TheBombPower theBomb:
                // 0.113.1 TheBombPower.cs:25-56: preserve per-instance explosion Damage (Aux0, e.g. 40 or 50).
                aux0 = theBomb.DynamicVars.Damage.IntValue;
                break;

            case PanachePower panache:
                // 0.113.1 PanachePower.cs:17-64: preserve mid-turn CardsLeft (Aux0) and !alreadyApplied guard (BoolAux).
                aux0 = panache.DynamicVars["CardsLeft"].IntValue;
                if (!panache.GetInternalData<PanachePower.Data>().alreadyApplied)
                    pFlags |= HotPowerFlags.BoolAux;
                break;

            case InfernoPower inferno:
                // 0.113.1 InfernoPower.cs:24-55: preserve accumulated SelfDamage (Aux0).
                aux0 = inferno.DynamicVars["SelfDamage"].IntValue;
                break;

            case CrimsonMantlePower crimsonMantle:
                // 0.113.1 CrimsonMantlePower.cs:23-40: preserve accumulated SelfDamage (Aux0).
                aux0 = crimsonMantle.DynamicVars["SelfDamage"].IntValue;
                break;

            case EnnuiPower ennui:
                // 0.113.1 EnnuiPower.cs:20-49: preserve accumulated Sadness Cards count (Aux0).
                aux0 = ennui.DynamicVars.Cards.IntValue;
                break;

            case MonologuePower monologue:
                // 0.113.1 MonologuePower.cs:47-88: preserve Strength per card (Aux0) and StrengthApplied (Aux1).
                aux0 = monologue.DynamicVars.Strength.IntValue;
                aux1 = monologue.DynamicVars["StrengthApplied"].IntValue;
                break;

            case InsatiableDominionPower insatiable:
                // 0.113.1 InsatiableDominionPower.cs:22-55: preserve current Damage (2 + TurnNumber) in Aux0.
                aux0 = insatiable.DynamicVars.Damage.IntValue;
                break;

            case CacophonyPower cacophony:
                // 0.113.1 CacophonyPower.cs:18-39: preserve remaining draw counter Cards (Aux0).
                aux0 = cacophony.DynamicVars.Cards.IntValue;
                break;

            case ToricToughnessPower toricToughness:
            {
                // 0.113.1 ToricToughnessPower.cs:22-43: preserve Block floor (Aux0) and hundredths (Aux1).
                decimal toricBlock = toricToughness.DynamicVars.Block.BaseValue;
                aux0 = (int)Math.Clamp(toricBlock, 0m, int.MaxValue);
                aux1 = (int)Math.Clamp(Math.Round(toricBlock * 100m, MidpointRounding.AwayFromZero), 0m, int.MaxValue);
                break;
            }

            case AutomationPower automation:
                // 0.113.1 AutomationPower.cs:12-51: preserve remaining cardsLeft counter (Aux0).
                aux0 = automation.GetInternalData<AutomationPower.Data>().cardsLeft;
                break;

            case VoidFormPower voidForm:
                // 0.113.1 VoidFormPower.cs:16-161: preserve mid-turn cardsPlayedThisTurn (Aux0).
                aux0 = voidForm.GetInternalData<VoidFormPower.Data>().cardsPlayedThisTurn;
                break;

            case SlothPower sloth:
                // 0.113.1 SlothPower.cs:14-51: preserve mid-turn _cardsPlayedThisTurn via DisplayAmount (Aux0).
                aux0 = sloth.DisplayAmount;
                break;

            case RollingBoulderPower rollingBoulder:
                // 0.113.1 RollingBoulderPower.cs:26-53: preserve per-turn Damage increment (Aux0).
                aux0 = rollingBoulder.DynamicVars.Damage.IntValue;
                break;

            case PaleBlueDotPower paleBlueDot:
                // 0.113.1 PaleBlueDotPower.cs:18-70: preserve alreadyActivatedThisTurn (BoolAux).
                if (paleBlueDot.GetInternalData<PaleBlueDotPower.Data>().alreadyActivatedThisTurn)
                    pFlags |= HotPowerFlags.BoolAux;
                break;

            case StretchPower stretch:
            {
                // 0.114.0 StretchPower.cs:15-111: preserve latched CardPlay + amountWhenCardStarted (Aux0)
                // and CardUid/gaveBonusBlock (Aux1, BoolAux) only while cardPlay != null (AfterCardPlayed clears
                // cardPlay without resetting amountWhenCardStarted, whereas HotFoolTarot zeroes Aux0/Aux1).
                StretchPower.Data data = stretch.GetInternalData<StretchPower.Data>();
                if (data.cardPlay != null)
                {
                    pFlags |= HotPowerFlags.BoolAux;
                    aux0 = data.amountWhenCardStarted;
                    ushort latchedUid = data.cardPlay.Card != null && cardToUid.TryGetValue(data.cardPlay.Card, out ushort stretchUid)
                        ? stretchUid
                        : (ushort)0;
                    aux1 = latchedUid | (data.gaveBonusBlock ? unchecked((int)0x8000_0000) : 0);
                }
                break;
            }
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
        int packedCount = 0;
        int packedArrows = 0;
        int enemyIndex = ownerSlot >= 3 && ownerSlot < 3 + HotLimits.MaxEnemies ? ownerSlot - 3 : -1;
        for (int a = 0; a < impaledArrows.Count; a++)
        {
            int arrowIdx = HotImportSupport.ArrowSlotIndex(arrows, impaledArrows[a]);
            if (arrowIdx < 0)
                continue;
            if (packedCount < 5)
            {
                packedArrows |= (arrowIdx & 0x1F) << (packedCount * 5);
                packedCount++;
            }
            if (enemyIndex >= 0 && arrowIdx < HotLimits.MaxArrows)
            {
                ushort existing = hot.Fool.Arrows[arrowIdx];
                hot.Fool.Arrows[arrowIdx] = (ushort)((existing & HotFoolState.ArrowStatusMask) | (((enemyIndex + 1) & HotFoolState.ArrowEnemyMask) << HotFoolState.ArrowEnemyShift));
            }
        }
        aux1 = packedArrows | (packedCount << 25);
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
            ShowOff showOff => ((short)showOff.CurrentAim & 0xFFFF) | (((short)showOff.IncreasedAim & 0xFFFF) << 16),
            Bounce or ThrummingHatchet or Bolas => CaptureReturnNextTurnLastPlayTurn(card),
            _ => 0,
        };

    private static int CaptureReturnNextTurnLastPlayTurn(CardModel card)
    {
        Player? owner = card.Owner;
        PlayerCombatState? pcs = owner?.PlayerCombatState;
        ICombatState? combatState = card.CombatState;
        CombatHistory? history = CombatManager.Instance?.History;
        if (owner == null || pcs == null || combatState == null || history == null)
            return 0;

        int turn = pcs.TurnNumber;
        bool playedThisTurn = false;
        bool playedLastTurn = false;
        foreach (CardPlayFinishedEntry entry in history.CardPlaysFinished)
        {
            if (!ReferenceEquals(entry.CardPlay.Card, card))
                continue;
            if (entry.HappenedThisTurn(combatState))
                playedThisTurn = true;
            else if (entry.HappenedLastPlayerTurn(owner))
                playedLastTurn = true;
        }

        if (playedThisTurn)
            return turn;
        if (playedLastTurn)
            return Math.Max(0, turn - 1);
        return 0;
    }

    private static bool WasFetchPlayedThisTurn(CardModel card)
    {
        // 0.113.1 Fetch.cs:30: HasBeenPlayedThisTurn checks CombatManager.Instance.History.CardPlaysFinished
        // for ReferenceEquals(e.CardPlay.Card, this) && e.HappenedThisTurn(CombatState).
        ICombatState? combatState = card.CombatState;
        CombatHistory? history = CombatManager.Instance?.History;
        if (combatState == null || history == null)
            return false;

        foreach (CardPlayFinishedEntry entry in history.CardPlaysFinished)
        {
            if (ReferenceEquals(entry.CardPlay.Card, card) && entry.HappenedThisTurn(combatState))
                return true;
        }

        return false;
    }

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

    private static bool PowerCanGenerateUnknownInCombat(HotPowerId powerId)
        => powerId is HotPowerId.CalamityPower
            or HotPowerId.CallOfTheVoidPower
            or HotPowerId.CreativeAiPower
            or HotPowerId.EntropyPower
            or HotPowerId.HelloWorldPower
            or HotPowerId.MythicFormPower
            or HotPowerId.SpectrumShiftPower;

    private static bool RelicCanHealOrGenerateRemainingInCombat(
        in CombatHotState hot,
        in HotActiveRelic ar,
        HotRelicDef rd,
        bool turn1SetupPending)
    {
        switch (rd.RelicId)
        {
            // Post-combat heals: BurningBlood/BlackBlood are modeled monotonically by PostCombatRelicHeal,
            // and MeatOnTheBone is intentionally excluded from MonotoneHealFor ranking.
            case HotRelicId.BurningBlood:
            case HotRelicId.BlackBlood:
            case HotRelicId.MeatOnTheBone:
                return false;

            // Pre-Turn-1 BeforeCombatStart / Turn <= 1 BeforeSideTurnStart relics:
            // already resolved before Turn 1 Setup/Play begins.
            case HotRelicId.Pantograph:
            case HotRelicId.BathRobes:
                return hot.PlayerTurnNumber == 0;

            // Turn-1-only setup relics (BeforeHandDraw / AfterPlayerTurnStart / AfterSideTurnStart):
            // cannot fire again once Turn 1 Play begins or on Turn >= 2.
            case HotRelicId.BathTowel:
            case HotRelicId.BloodVial:
            case HotRelicId.FakeBloodVial:
            case HotRelicId.BigHat:
            case HotRelicId.ChoicesParadox:
            case HotRelicId.Ocarina:
            case HotRelicId.OrangeDough:
            case HotRelicId.Toolbox:
            case HotRelicId.VexingPuzzlebox:
                return turn1SetupPending;

            // Death-save heal only triggers while unused (Current == 0).
            case HotRelicId.LizardTail:
                return ar.StateSlot >= hot.RelicStateCount || hot.RelicStates[ar.StateSlot].Current == 0;

            default:
                return true;
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
        HashSet<CardPlay>? cardBlockGainPlays = null;
        int shivPlaysFinished = 0;
        int firstInSeriesCardPlayStarts = 0;
        int historyCardsExhausted = 0;

        int[] melodyStarted = new int[3];
        int[] melodyFinished = new int[3];
        int melodyCardsFinished = 0;
        CardTag[] melodyTags = HotImportSupport.MelodyTags;

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
                        if (started.CardPlay.IsFirstInSeries)
                            firstInSeriesCardPlayStarts++;
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
                            if (finished.CardPlay.Card.Tags.Contains(CardTag.Shiv))
                                shivPlaysFinished++;
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

                case CardExhaustedEntry exh:
                    historyCardsExhausted++;
                    if (thisTurn && ReferenceEquals(exh.Actor, player.Creature))
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

                case BlockGainedEntry blk when thisTurn
                    && blk.CardPlay != null
                    && ReferenceEquals(blk.CardPlay.Player, player)
                    && blk.Props.IsCardOrMonsterMove():
                    (cardBlockGainPlays ??= new HashSet<CardPlay>(ReferenceEqualityComparer.Instance)).Add(blk.CardPlay);
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
        hot.Counters[(int)HotCounterSlot.CardBlockGainsThisTurn] = cardBlockGainPlays?.Count ?? 0;
        hot.Counters[(int)HotCounterSlot.ShivPlaysFinishedThisTurn] = shivPlaysFinished;
        hot.Counters[(int)HotCounterSlot.FirstInSeriesCardPlayStartsThisTurn] = firstInSeriesCardPlayStarts;
        hot.Counters[(int)HotCounterSlot.HistoryCardsExhausted] = historyCardsExhausted;

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
            FakeHappyFlower flower => new HotRelicState
            {
                Current = flower.TurnsSeen,
                PreviousOrAux = (bool)FakeHappyFlowerGrantEnergyField.GetValue(flower)! ? 1 : 0,
            },
            FakeVenerableTeaSet tea => new HotRelicState { Current = tea.GainEnergyInNextCombat ? 1 : 0 },
            HappyFlower flower => new HotRelicState
            {
                Current = flower.TurnsSeen,
                PreviousOrAux = (bool)HappyFlowerGrantEnergyField.GetValue(flower)! ? 1 : 0,
            },
            MiniRegent regent => new HotRelicState { Current = regent._usedThisTurn ? 1 : 0 },
            GalacticDust dust => new HotRelicState { Current = dust.StarsSpent },
            PaelsEye eye => CapturePaelsEyeState(eye),
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

    private static HotRelicState CapturePaelsEyeState(PaelsEye eye)
    {
        bool used = eye._usedThisCombat;
        bool wasPart = eye._wasOwnerPartOfLastPlayerTurn;
        bool manualPlayed = false;
        if (!used && eye.Owner?.Creature is { } ownerCreature)
        {
            CombatHistory? history = CombatManager.Instance?.History;
            if (history != null)
            {
                foreach (CardPlayFinishedEntry entry in history.CardPlaysFinished)
                {
                    if (ReferenceEquals(entry.Actor, ownerCreature)
                        && entry.HappenedThisTurn(ownerCreature.CombatState)
                        && !entry.CardPlay.IsAutoPlay)
                    {
                        manualPlayed = true;
                        break;
                    }
                }
            }
        }
        return new HotRelicState
        {
            Current = used ? 1 : 0,
            PreviousOrAux = (wasPart ? 1 : 0) | (manualPlayed ? 2 : 0),
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
        ushort[] UnlockedStatus,
        ushort[] UnlockedToken,
        ushort[] OtherCharacter,
        ushort[] Tarot,
        ushort[] CombatCharAttacks,
        ushort[] CombatCharSkills,
        ushort[] CombatCharPowers,
        ushort[] CombatColorless,
        ushort[] UnlockedPotions,
        ushort[][] UnlockedCharPoolsByDomain);

    private static HotCardDomain MapCardPoolToDomain(CardPoolModel pool)
        => pool switch
        {
            IroncladCardPool => HotCardDomain.Ironclad,
            SilentCardPool => HotCardDomain.Silent,
            DefectCardPool => HotCardDomain.Defect,
            RegentCardPool => HotCardDomain.Regent,
            NecrobinderCardPool => HotCardDomain.Necrobinder,
            FoolCardPool => HotCardDomain.Fool,
            _ => HotCardDomain.None,
        };

    private static void EnsureMissingVanillaCharacterDomainPool(
        IEnumerable<CardModel>?[] perDomainCharCards,
        HotCardDomain playerDomain,
        HotCardDomain domain,
        CardPoolModel pool,
        UnlockState unlockState,
        CardMultiplayerConstraint mpConstraint)
    {
        if (domain != playerDomain && perDomainCharCards[(int)domain] == null)
            perDomainCharCards[(int)domain] = pool.GetUnlockedCards(unlockState, mpConstraint);
    }

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
                    InstanceType = tpl?.InstanceType ?? pe.InstanceType,
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
                    && existing.StackType == power.StackType && existing.InstanceType == power.InstanceType)
                {
                    return existingId;
                }
                if (VarsMatchExact(existing.OrderedVarNames, existing.DefaultVarValues, existing.OrderedVarStrings,
                        varNames, varValues, varStrings)
                    && existing.Type == power.Type
                    && existing.StackType == power.StackType
                    && existing.InstanceType == power.InstanceType)
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
                HasRelevantSkipDurationTick = HotImportSupport.UsesNativeDurationSkip(power.GetType()),
                SetupWeight = ce.SetupWeight,
                OrderedVarNames = varNames,
                DefaultVarValues = varValues,
                OrderedVarStrings = varStrings,
                SpecialKind = ClassifyPowerSpecial(power),
                InstanceType = power.InstanceType,
                StackType = power.StackType,
            };
            Powers.Add(def);
            PowerDefIdByEntry.TryAdd(entry, defId);
            return defId;
        }

        public ushort EnsureCardDefForLiveInstance(
            CardModel liveCard,
            CardModel? explicitDupeSource,
            ushort cardUid,
            out short var0Delta,
            out short var1Delta,
            out decimal var0DecimalDelta)
        {
            ushort canonicalId = EnsureCanonicalCardDef(liveCard);
            HotCardDef canonical = Cards[canonicalId];

            ExtractSemanticVars(liveCard, out string[] liveNames, out decimal[] liveValues, out string?[]? liveStrings);

            string? dupeKey = liveCard.IsDupe
                ? HotImportSupport.FormatLiveChoiceCardKey(explicitDupeSource ?? liveCard.DupeOf
                    ?? throw new InvalidOperationException("Dupe has no source card."))
                : null;

            bool canReuseCanonical = dupeKey == null
                && liveCard.CurrentUpgradeLevel <= 1
                && liveCard.Type == canonical.Type
                && liveCard.TargetType == canonical.Target
                && liveCard.GainsBlock == canonical.GainsBlock
                && canonical.OrderedVarNames.Length == liveNames.Length
                && StringsEqual(canonical.OrderedVarStrings, liveStrings);

            short d0 = 0;
            short d1 = 0;
            decimal d0Frac = 0m;

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

                    if (i == canonical.Var0Index)
                    {
                        decimal diff = actual - expected;
                        decimal whole = Math.Truncate(diff);
                        decimal frac = diff - whole;
                        if (whole >= short.MinValue && whole <= short.MaxValue
                            && (frac != 0m
                                ? (cardUid != 0 && DecimalScaleMatches(expected + (short)whole + frac, actual))
                                : DecimalScaleMatches(expected, actual)))
                        {
                            d0 = (short)whole;
                            d0Frac = frac;
                            continue;
                        }
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
                var0DecimalDelta = d0Frac;
                return canonicalId;
            }

            var0Delta = 0;
            var1Delta = 0;
            var0DecimalDelta = 0m;
            ushort variantId = (ushort)Cards.Count;
            ExtractCardScalars(liveNames, liveValues, out short dmg, out short blk, out short magic, out byte hits, out byte v0, out byte v1);
            decimal[] variantVarValues = liveValues;
            if (cardUid != 0 && v0 != 0xFF && v0 < liveValues.Length)
            {
                decimal whole0 = Math.Truncate(liveValues[v0]);
                decimal frac0 = liveValues[v0] - whole0;
                if (frac0 != 0m)
                {
                    variantVarValues = (decimal[])liveValues.Clone();
                    variantVarValues[v0] = whole0;
                    var0DecimalDelta = frac0;
                }
            }

            decimal[] baseVarValues = variantVarValues;
            decimal[] upgradedVarValues = variantVarValues;
            short baseDmg = dmg, upgDmg = dmg;
            short baseBlk = blk, upgBlk = blk;
            short baseMagic = magic, upgMagic = magic;
            byte baseHits = hits, upgHits = hits;

            bool canDeriveOppositeLevel = liveCard.CurrentUpgradeLevel is 0 or 1
                && liveNames.Length > 0
                && canonical.OrderedVarNames.Length == liveNames.Length
                && canonical.BaseVarValues.Length == liveNames.Length
                && canonical.UpgradedVarValues.Length == liveNames.Length;
            if (canDeriveOppositeLevel)
            {
                for (int i = 0; i < liveNames.Length; i++)
                {
                    if (!string.Equals(canonical.OrderedVarNames[i], liveNames[i], StringComparison.Ordinal))
                    {
                        canDeriveOppositeLevel = false;
                        break;
                    }
                }
            }

            if (canDeriveOppositeLevel)
            {
                if (liveCard.CurrentUpgradeLevel == 0)
                {
                    upgradedVarValues = new decimal[liveNames.Length];
                    for (int i = 0; i < liveNames.Length; i++)
                    {
                        decimal delta = canonical.UpgradedVarValues[i] - canonical.BaseVarValues[i];
                        upgradedVarValues[i] = variantVarValues[i] + delta;
                    }
                }
                else
                {
                    baseVarValues = new decimal[liveNames.Length];
                    for (int i = 0; i < liveNames.Length; i++)
                    {
                        decimal delta = canonical.UpgradedVarValues[i] - canonical.BaseVarValues[i];
                        baseVarValues[i] = variantVarValues[i] - delta;
                    }
                }

                ExtractCardScalars(liveNames, baseVarValues, out baseDmg, out baseBlk, out baseMagic, out baseHits, out _, out _);
                ExtractCardScalars(liveNames, upgradedVarValues, out upgDmg, out upgBlk, out upgMagic, out upgHits, out _, out _);
            }

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
                BaseDamage = baseDmg,
                UpgradedDamage = upgDmg,
                BaseBlock = baseBlk,
                UpgradedBlock = upgBlk,
                BaseMagic = baseMagic,
                UpgradedMagic = upgMagic,
                BaseHits = baseHits,
                UpgradedHits = upgHits,
                HookMask = canonical.HookMask,
                CanHealPlayer = canonical.CanHealPlayer,
                CanGenerateUnknown = canonical.CanGenerateUnknown,
                SetupWeight = canonical.SetupWeight,
                OpStart = canonical.OpStart,
                OpCount = canonical.OpCount,
                CalcVarKind = canonical.CalcVarKind,
                CustomHandlerId = canonical.CustomHandlerId,
                OrderedVarNames = liveNames,
                BaseVarValues = baseVarValues,
                UpgradedVarValues = upgradedVarValues,
                OrderedVarStrings = liveStrings,
                Var0Index = v0,
                Var1Index = v1,
                IsAllowedInBlitz = liveCard.IsAllowedInBlitz,
                GainsBlock = liveCard.GainsBlock,
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
                IsVisualColorless = canonical.IsVisualColorless,
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
                IsVisualColorless = tpl.IsVisualColorless,
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
                IsVisualColorless = baseModel.VisualCardPool.IsColorless,
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

            if (relic is PumpkinCandle pumpkinCandle)
            {
                primaryVal = (short)Math.Clamp(pumpkinCandle.KindleCount, 0, short.MaxValue);
                secondaryVal = 0;
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
                CanBeGeneratedInCombat = potion.CanBeGeneratedInCombat,
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

            IReadOnlyDictionary<string, int> staticInts = HotImportSupport.CaptureStaticMonsterIntValues(monster);
            int testSubjectBaseMultiClaw = typeName == "TestSubject"
                ? HotImportSupport.ReadMonsterInt(monster, "BaseMultiClawCount")
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

        public void PreRegisterSpawnableMonsters(
            EncounterModel? encounter,
            IReadOnlyList<Creature> liveEnemies,
            bool isBoss,
            bool isElite)
        {
            Queue<HotMonsterId> queue = new();
            HashSet<HotMonsterId> seen = [];
            for (int i = 1; i < Monsters.Count; i++)
            {
                HotMonsterId mid = Monsters[i].MonsterId;
                if (mid != HotMonsterId.None && seen.Add(mid))
                    queue.Enqueue(mid);
            }

            void RegisterPrototypeIfMissing(MonsterModel? canonical)
            {
                if (canonical == null || MonsterDefIdByEntry.ContainsKey(canonical.Id.Entry))
                    return;

                MonsterModel mutable = canonical.IsCanonical ? canonical.ToMutable() : canonical;
                if (mutable.MoveStateMachine == null)
                {
                    try
                    {
                        mutable.SetUpForCombat();
                    }
                    catch
                    {
                        // Non-spawned encounter helper may expect live CombatState; register metadata safely.
                    }
                }

                ushort defId = RegisterEnemyMonster(mutable, isBoss, isElite, extraMoveIds: null, out _);
                HotMonsterId mid = Monsters[defId].MonsterId;
                if (mid != HotMonsterId.None && seen.Add(mid))
                    queue.Enqueue(mid);
            }

            void SeedSpawnId(HotMonsterId child)
            {
                if (!seen.Add(child))
                    return;
                queue.Enqueue(child);
                RegisterPrototypeIfMissing(HotMonsterCatalog.TryGetCanonicalMonsterModel(child));
            }

            if (encounter != null)
            {
                foreach (MonsterModel possible in encounter.AllPossibleMonsters)
                    RegisterPrototypeIfMissing(possible);
            }

            for (int i = 0; i < liveEnemies.Count; i++)
            {
                foreach (PowerModel power in liveEnemies[i].Powers)
                {
                    switch (power)
                    {
                        case InfestedPower:
                            SeedSpawnId(HotMonsterId.Wriggler);
                            break;
                        case StockPower:
                            SeedSpawnId(HotMonsterId.Axebot);
                            break;
                        case SurprisePower:
                            SeedSpawnId(HotMonsterId.FatGremlin);
                            SeedSpawnId(HotMonsterId.SneakyGremlin);
                            break;
                    }
                }
            }

            while (queue.Count > 0)
            {
                HotMonsterId cur = queue.Dequeue();
                ReadOnlySpan<HotMonsterId> children = HotMonsterCatalog.GetSpawnClosure(cur);
                for (int i = 0; i < children.Length; i++)
                    SeedSpawnId(children[i]);
            }
        }

        public FrozenGenerationPools PreRegisterPoolsAndTokens(
            IEnumerable<CardModel> unlockedCharCards,
            IEnumerable<CardModel> unlockedColorlessCards,
            IEnumerable<CardModel> unlockedCurseCards,
            IEnumerable<CardModel> unlockedStatusCards,
            IEnumerable<CardModel> unlockedTokenCards,
            IEnumerable<CardModel> otherCharCards,
            IEnumerable<CardModel> tarotCards,
            IEnumerable<PotionModel> unlockedPotions,
            HotCardDomain playerDomain,
            IEnumerable<CardModel>?[] perDomainCharCards,
            CardMultiplayerConstraint mpConstraint,
            HotCombatRulesSnapshot rulesSnapshot,
            bool hasRenewablePotionShapedRock)
        {
            // Pre-register all Status and Token cards so fixed-card generation (Shiv, Dazed, Wound, Burn, Soul, SovereignBlade, etc.) is always O(1).
            foreach (CardModel card in ModelDb.CardPool<StatusCardPool>().AllCards)
                EnsureCanonicalCardDef(card);
            foreach (CardModel card in ModelDb.CardPool<TokenCardPool>().AllCards)
                EnsureCanonicalCardDef(card);
            EnsureCanonicalCardDef(ModelDb.Card<Disintegration>());
            EnsureCanonicalCardDef(ModelDb.Card<MindRot>());
            EnsureCanonicalCardDef(ModelDb.Card<Sloth>());
            EnsureCanonicalCardDef(ModelDb.Card<WasteAway>());
            EnsureCanonicalCardDef(ModelDb.Card<Amnesia>());

            // Pre-register all 7 canonical STS2 0.113.1 afflictions and mid-combat-applied enchantments (Inky)
            // so mid-combat GetAfflictionDefId / GetEnchantmentDefId lookups never silently return 0.
            EnsureAfflictionDef(ModelDb.Affliction<MegaCrit.Sts2.Core.Models.Afflictions.Bound>());
            EnsureAfflictionDef(ModelDb.Affliction<MegaCrit.Sts2.Core.Models.Afflictions.Entangled>());
            EnsureAfflictionDef(ModelDb.Affliction<MegaCrit.Sts2.Core.Models.Afflictions.Galvanized>());
            EnsureAfflictionDef(ModelDb.Affliction<MegaCrit.Sts2.Core.Models.Afflictions.Hexed>());
            EnsureAfflictionDef(ModelDb.Affliction<MegaCrit.Sts2.Core.Models.Afflictions.Ringing>());
            EnsureAfflictionDef(ModelDb.Affliction<MegaCrit.Sts2.Core.Models.Afflictions.Smog>());
            EnsureAfflictionDef(ModelDb.Affliction<MegaCrit.Sts2.Core.Models.Afflictions.Tainted>());
            EnsureEnchantmentDef(ModelDb.Enchantment<MegaCrit.Sts2.Core.Models.Enchantments.Inky>());

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
            ushort[] charPool = RegisterPool(HotImportSupport.FilterForCombatAndPlayerCount(unlockedCharCards, mpConstraint));
            ushort[] colorlessPool = RegisterPool(unlockedColorlessCards);
            ushort[] combatColorless = RegisterPool(HotImportSupport.FilterForCombatAndPlayerCount(unlockedColorlessCards, mpConstraint));
            ushort[] cursePool = RegisterPool(unlockedCurseCards);
            ushort[] statusPool = RegisterPool(unlockedStatusCards);
            ushort[] tokenPool = RegisterPool(unlockedTokenCards);
            ushort[] otherCharPool = RegisterPool(HotImportSupport.FilterForCombatAndPlayerCount(otherCharCards, mpConstraint));
            ushort[] tarotPool = RegisterPool(tarotCards);

            ushort[][] unlockedCharPoolsByDomain = new ushort[7][];
            for (int d = 0; d < unlockedCharPoolsByDomain.Length; d++)
                unlockedCharPoolsByDomain[d] = [];
            if (playerDomain != HotCardDomain.None && (int)playerDomain < unlockedCharPoolsByDomain.Length)
                unlockedCharPoolsByDomain[(int)playerDomain] = charPool;
            for (int d = 1; d < unlockedCharPoolsByDomain.Length; d++)
            {
                if (d == (int)playerDomain)
                    continue;
                if (d < perDomainCharCards.Length && perDomainCharCards[d] is { } domainCards)
                    unlockedCharPoolsByDomain[d] = RegisterPool(HotImportSupport.FilterForCombatAndPlayerCount(domainCards, mpConstraint));
            }

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

            // Always register Token PotionShapedRock so PetrifiedToad can procure it even if belt starts empty.
            EnsurePotionDef(ModelDb.Potion<PotionShapedRock>(), hasRenewablePotionShapedRock);

            // Exact STS2 0.113.1 PotionFactory.GetPotionOptions(player): character unlocked + SharedPotionPool unlocked.
            List<ushort> potionPool = [];
            foreach (PotionModel potion in unlockedPotions)
            {
                if (potion.GetType().Assembly != typeof(AbstractModel).Assembly)
                    continue;
                HotCustomPotionId pid = HotPotionCatalog.FindByEntry(potion.Id.Entry);
                if (pid == HotCustomPotionId.None)
                    continue;
                ushort pDefId = EnsurePotionDef(potion, hasRenewablePotionShapedRock);
                potionPool.Add(pDefId);
            }

            return new FrozenGenerationPools(
                UnlockedCharacter: charPool,
                UnlockedColorless: colorlessPool,
                UnlockedCurse: cursePool,
                UnlockedStatus: statusPool,
                UnlockedToken: tokenPool,
                OtherCharacter: otherCharPool,
                Tarot: tarotPool,
                CombatCharAttacks: charAttacks.ToArray(),
                CombatCharSkills: charSkills.ToArray(),
                CombatCharPowers: charPowers.ToArray(),
                CombatColorless: combatColorless,
                UnlockedPotions: potionPool.ToArray(),
                UnlockedCharPoolsByDomain: unlockedCharPoolsByDomain);
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

            // Synthesize dynamic STS2 0.113.1 states not declared in GenerateMoveStateMachine():
            // - STUNNED (CreatureCmd.Stun / Ravenous / Flutter / Whistle / Burrowed / Asleep / Slumber / Shriek / Imbalanced)
            // - REVIVE_MOVE (IllusionPower.AfterDeath on Parafright / EyeWithTeeth)
            AddId("STUNNED");
            if (monsterId is HotMonsterId.Parafright or HotMonsterId.EyeWithTeeth)
                AddId("REVIVE_MOVE");

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
                            int bd = HotImportSupport.CaptureAttackBaseDamage(attack, monster, move.Id);
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
                    bool mustPerformOnce = string.Equals(id, "STUNNED", StringComparison.Ordinal)
                        || string.Equals(id, "REVIVE_MOVE", StringComparison.Ordinal);
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
                        MustPerformOnceBeforeRepeat = mustPerformOnce,
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
            if (!HotImportSupport.IsSemanticVar(model, kv.Key, kv.Value))
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
