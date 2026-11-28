using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed class InCombatLiveCaptureUnsupportedException(
    string failureCode,
    string? fieldPath,
    string message) : Exception(message)
{
    public string FailureCode { get; } = failureCode;
    public string? FieldPath { get; } = fieldPath;
}

internal static partial class InCombatLiveStateCapturer
{
    public static InCombatRustHostMetadata CaptureHostMetadata(
        CombatState state,
        int? initialEncounterMonsterCount = null)
    {
        if (state.RunState is not RunState concreteRun || concreteRun.Players.Count != 1)
        {
            throw new InCombatLiveCaptureUnsupportedException(
                "live_combat_precondition",
                "players",
                "Rust combat kernel requires a single-player RunState.");
        }
        if (state.Encounter is null)
        {
            throw new InCombatLiveCaptureUnsupportedException(
                "encounter_unmapped",
                "encounterId",
                "Active combat has no EncounterModel.");
        }

        Player player = LocalContext.GetMe(state) ?? concreteRun.Players[0];
        bool hasMurderous = state.Modifiers.Any(static m => m is Murderous)
            || concreteRun.Modifiers.Any(static m => m is Murderous);
        int frozenEncounterMonsterCount = initialEncounterMonsterCount
            ?? (state.Encounter is { IsMutable: true, HaveMonstersBeenGenerated: true } enc
                ? enc.MonstersWithSlots.Count
                : state.Enemies.Count);
        return new InCombatRustHostMetadata(
            CharacterId: player.Character.Id.ToString(),
            Ascension: (byte)Math.Clamp(concreteRun.AscensionLevel, 0, 255),
            GameMode: concreteRun.GameMode.ToString(),
            DominionId: state.Dominion?.Id.ToString(),
            RunModelIds: concreteRun.RunModels.Select(static m => m.Id.ToString()).ToArray(),
            EncounterIsWeak: state.Encounter.IsWeak,
            InitialEncounterMonsterCount: frozenEncounterMonsterCount,
            TotalFloor: concreteRun.TotalFloor,
            ActIndex: concreteRun.CurrentActIndex,
            MapCol: concreteRun.CurrentMapCoord?.col ?? 0,
            MapRow: concreteRun.CurrentMapCoord?.row ?? 0,
            HasMurderousModifier: hasMurderous);
    }

    public static (RustKernelLiveStateDto LiveState, int PriorBattleHpLost) Capture(
        CombatState state,
        BattleDamageSnapshot battleDamage)
    {
        if (state.RunState is not RunState concreteRun || concreteRun.Players.Count != 1)
        {
            throw new InCombatLiveCaptureUnsupportedException(
                "live_combat_precondition",
                "players",
                "Rust combat kernel requires a single-player RunState.");
        }
        if (state.Encounter is null)
        {
            throw new InCombatLiveCaptureUnsupportedException(
                "encounter_unmapped",
                "encounterId",
                "Active combat has no EncounterModel.");
        }

        Player player = LocalContext.GetMe(state) ?? concreteRun.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState
            ?? throw new InCombatLiveCaptureUnsupportedException(
                "live_combat_precondition",
                "player",
                "Player has no active PlayerCombatState.");

        var creatureHandles = new Dictionary<Creature, int>(ReferenceEqualityComparer.Instance);
        var cardHandles = new Dictionary<CardModel, int>(ReferenceEqualityComparer.Instance);

        int RegisterCreature(Creature creature)
        {
            if (!creatureHandles.TryGetValue(creature, out int handle))
            {
                handle = creatureHandles.Count + 1;
                creatureHandles.Add(creature, handle);
            }
            return handle;
        }

        int RegisterCard(CardModel card)
        {
            if (!cardHandles.TryGetValue(card, out int handle))
            {
                handle = cardHandles.Count + 1;
                cardHandles.Add(card, handle);
                if (card.DeckVersion is { } dv)
                    RegisterCard(dv);
                if ((card._cloneOf ?? card.DupeOf) is { } origin)
                    RegisterCard(origin);
            }
            return handle;
        }

        RegisterCreature(player.Creature);
        foreach (Creature c in state.Allies) RegisterCreature(c);
        foreach (Creature c in state.Enemies) RegisterCreature(c);
        foreach (Creature c in state.EscapedCreatures) RegisterCreature(c);
        foreach (Creature c in pcs.Pets) RegisterCreature(c);

        foreach (CardModel c in player.Deck.Cards) RegisterCard(c);
        foreach (CardPile pile in pcs.AllPiles)
        {
            foreach (CardModel c in pile.Cards) RegisterCard(c);
        }

        CombatHistoryEntry[] allHistory = CombatManager.Instance.History.Entries.ToArray();
        DamageReceivedEntry[] damageEntries = allHistory.OfType<DamageReceivedEntry>().ToArray();
        CardPlayStartedEntry[] startedEntries = allHistory.OfType<CardPlayStartedEntry>().ToArray();
        CardPlayFinishedEntry[] finishedEntries = allHistory.OfType<CardPlayFinishedEntry>().ToArray();

        foreach (CombatHistoryEntry entry in allHistory)
        {
            if (entry.Actor is not null)
                RegisterCreature(entry.Actor);
            switch (entry)
            {
                case DamageReceivedEntry dmg:
                    if (dmg.Dealer is not null) RegisterCreature(dmg.Dealer);
                    if (dmg.Result.OriginalTarget is not null) RegisterCreature(dmg.Result.OriginalTarget);
                    if (dmg.CardSource is not null) RegisterCard(dmg.CardSource);
                    break;
                case CreatureAttackedEntry attacked:
                    foreach (DamageResult dr in attacked.DamageResults)
                    {
                        if (dr.Receiver is not null) RegisterCreature(dr.Receiver);
                        if (dr.OriginalTarget is not null) RegisterCreature(dr.OriginalTarget);
                    }
                    break;
                case CardPlayStartedEntry started:
                    if (started.CardPlay.Target is not null) RegisterCreature(started.CardPlay.Target);
                    if (started.CardPlay.Card is { } startedCard) RegisterCard(startedCard);
                    break;
                case CardPlayFinishedEntry finished:
                    if (finished.CardPlay.Target is not null) RegisterCreature(finished.CardPlay.Target);
                    if (finished.CardPlay.Card is { } playedCard) RegisterCard(playedCard);
                    break;
                case PowerReceivedEntry powerReceived:
                    if (powerReceived.Applier is not null) RegisterCreature(powerReceived.Applier);
                    break;
                case PotionUsedEntry potionUsed:
                    if (potionUsed.Target is not null) RegisterCreature(potionUsed.Target);
                    break;
                case MonsterPerformedMoveEntry moveEntry when moveEntry.Targets is not null:
                    foreach (Creature target in moveEntry.Targets)
                        RegisterCreature(target);
                    break;
            }
        }

        RegisterPrivateReferenceTargets(state, player, creatureHandles, finishedEntries, RegisterCreature, RegisterCard);

        IReadOnlyDictionary<CardModel, int> theHuntPendingRewards = ComputeTheHuntPendingRewards(player, allHistory);

        var cardsDto = cardHandles
            .OrderBy(static kv => kv.Value)
            .Select(kv => CaptureCard(kv.Key, kv.Value, state, cardHandles, finishedEntries, theHuntPendingRewards))
            .ToArray();

        var relicsDto = player.Relics
            .Select(relic => CaptureRelic(relic, state, player, cardHandles, startedEntries, finishedEntries, allHistory))
            .ToArray();

        var activeAndEscaped = new HashSet<Creature>(
            state.Allies.Concat(state.Enemies).Concat(state.EscapedCreatures),
            ReferenceEqualityComparer.Instance);

        var creaturesDto = creatureHandles
            .OrderBy(static kv => kv.Value)
            .Select(kv => CaptureCreature(
                kv.Key,
                kv.Value,
                state,
                concreteRun,
                !activeAndEscaped.Contains(kv.Key),
                creatureHandles,
                cardHandles,
                startedEntries))
            .ToArray();

        int[] removedHandles = creatureHandles
            .Where(kv => !activeAndEscaped.Contains(kv.Key))
            .OrderBy(static kv => kv.Value)
            .Select(static kv => kv.Value)
            .ToArray();

        SerializableRunRngSet runRng = state.RunState.Rng.ToSerializable();
        Dictionary<string, RustKernelRngStreamDto> runRngDtos = runRng.Rngs.ToDictionary(
            static kv => kv.Key.ToString(),
            static kv => ToRngDto(kv.Value),
            StringComparer.Ordinal);
        SerializablePlayerRngSet playerRng = player.PlayerRng.ToSerializable();
        Dictionary<string, RustKernelRngStreamDto> playerRngDtos = playerRng.Rngs.ToDictionary(
            static kv => kv.Key.ToString(),
            static kv => ToRngDto(kv.Value),
            StringComparer.Ordinal);

        int playerCumulativeHpLost = damageEntries
            .Where(e => ReferenceEquals(e.Receiver, player.Creature))
            .Sum(e => Math.Max(0, e.Result.UnblockedDamage));

        var countersDto = CapturePlayerCounters(state, player, startedEntries, finishedEntries, allHistory);

        var orbsDto = pcs.OrbQueue.Orbs
            .Select(orb => new RustKernelOrbDto(
                orb.Id.ToString(),
                orb is DarkOrb dark ? dark._evokeVal : null,
                orb is GlassOrb glass ? glass._passiveVal : null))
            .ToArray();

        var arrowsDto = pcs.Arrows
            .Select(arrow => new RustKernelArrowSlotDto(arrow.Status.ToString()))
            .ToArray();

        var playerDto = new RustKernelPlayerDto(
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.Creature.Block,
            playerCumulativeHpLost,
            player.Gold,
            player.MaxEnergy,
            player.BaseOrbSlotCount,
            player.IsActiveForHooks,
            player.CanUseOrRemovePotions,
            pcs.Energy,
            pcs.Stars,
            pcs.Phase.ToString(),
            pcs.TurnNumber,
            countersDto,
            pcs.Hand.Cards.Select(c => cardHandles[c]).ToArray(),
            pcs.DrawPile.Cards.Select(c => cardHandles[c]).ToArray(),
            pcs.DiscardPile.Cards.Select(c => cardHandles[c]).ToArray(),
            pcs.ExhaustPile.Cards.Select(c => cardHandles[c]).ToArray(),
            pcs.PlayPile.Cards.Select(c => cardHandles[c]).ToArray(),
            player.Deck.Cards.Select(c => cardHandles[c]).ToArray(),
            player.PotionSlots.Select(static p => p?.Id.ToString()).ToArray(),
            pcs.OrbQueue.Capacity,
            orbsDto,
            arrowsDto);

        RustKernelDamageHistoryEntryDto[] damageDtos = damageEntries
            .Where(e => e.Receiver is not null && e.Result.OriginalTarget is not null)
            .Select(e => new RustKernelDamageHistoryEntryDto(
                creatureHandles[e.Receiver],
                e.Dealer is not null ? creatureHandles[e.Dealer] : null,
                creatureHandles[e.Result.OriginalTarget],
                (int)e.Result.Props,
                e.Result.IntendedHpLoss,
                e.Result.BlockedDamage,
                e.Result.UnblockedDamage,
                e.Result.OverkillDamage,
                e.Result.WasTargetKilled,
                e.Result.WasBlockBroken,
                e.Result.WasFullyBlocked,
                e.CardSource is not null ? cardHandles[e.CardSource] : null,
                e.RoundNumber,
                e.CurrentSide.ToString(),
                concreteRun.Players
                    .Select(p => e._playerTurnNumbers.TryGetValue(p.NetId, out int t) ? t : p.PlayerCombatState?.TurnNumber ?? 1)
                    .ToArray()))
            .ToArray();

        RustKernelSemanticHistoryDto semanticDto = CaptureSemanticHistory(concreteRun, allHistory, startedEntries);

        bool goldWasStolen = state.Encounter is GremlinMercNormal gremlinMerc && gremlinMerc._goldWasStolen;
        bool isExtraTurn = CombatManager.Instance.PlayersTakingExtraTurn.Contains(player);
        int[] spawnedEnemyHandles = CaptureSpawnedEnemyHandles(state, creatureHandles);

        bool isBlitz = concreteRun.GameMode == GameMode.Blitz;
        CardMultiplayerConstraint mpConstraint = state.RunState.CardMultiplayerConstraint;
        string[] CaptureCardPoolEntries(IEnumerable<CardModel> cards)
        {
            var entries = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CardModel card in cards)
            {
                if (card.GetType().Assembly != typeof(AbstractModel).Assembly)
                    continue;
                if (isBlitz && !card.IsAllowedInBlitz)
                    continue;
                if (mpConstraint == CardMultiplayerConstraint.SingleplayerOnly
                    && card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
                    continue;
                if (mpConstraint == CardMultiplayerConstraint.MultiplayerOnly
                    && card.MultiplayerConstraint == CardMultiplayerConstraint.SingleplayerOnly)
                    continue;
                string entry = card.Id.Entry;
                if (seen.Add(entry))
                    entries.Add(entry);
            }
            return entries.ToArray();
        }

        List<CardPoolModel> otherCharPools = player.UnlockState.CharacterCardPools.ToList();
        if (otherCharPools.Count > 1)
            otherCharPools.Remove(player.Character.CardPool);

        string[] unlockedCharacterCards = CaptureCardPoolEntries(
            player.Character.CardPool.GetUnlockedCards(player.UnlockState, mpConstraint));
        string[] unlockedColorlessCards = CaptureCardPoolEntries(
            ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(player.UnlockState, mpConstraint));
        string[] unlockedCurseCards = CaptureCardPoolEntries(
            ModelDb.CardPool<CurseCardPool>().GetUnlockedCards(player.UnlockState, mpConstraint));
        string[] otherCharacterCards = CaptureCardPoolEntries(
            otherCharPools.SelectMany(p => p.GetUnlockedCards(player.UnlockState, mpConstraint)));
        string[] tarotCards = CaptureCardPoolEntries(
            TarotCards.All.Where(card => card.IsAllowedInRun(state.RunState)));

        var unlockedPotionList = new List<string>();
        var seenPotions = new HashSet<string>(StringComparer.Ordinal);
        foreach (PotionModel potion in PotionFactory.GetPotionOptions(player))
        {
            if (potion.GetType().Assembly != typeof(AbstractModel).Assembly)
                continue;
            string entry = potion.Id.Entry;
            if (seenPotions.Add(entry))
                unlockedPotionList.Add(entry);
        }
        string[] unlockedPotions = unlockedPotionList.ToArray();

        bool battlewornDummyTimedOut =
            state.Encounter is MegaCrit.Sts2.Core.Models.Encounters.BattlewornDummyEventEncounter dummyEnc
            && dummyEnc.RanOutOfTime;

        var liveState = new RustKernelLiveStateDto(
            EncounterId: state.Encounter.Id.Entry,
            RoomType: state.Encounter.RoomType.ToString(),
            EncounterSlots: state.Encounter.Slots.ToArray(),
            CurrentSide: state.CurrentSide.ToString(),
            RoundNumber: state.RoundNumber,
            IsPlayerExtraTurn: isExtraTurn,
            GoldWasStolen: goldWasStolen,
            NextCreatureId: state._nextCreatureId,
            RunRngSeed: runRng.Seed ?? string.Empty,
            RunRngs: runRngDtos,
            PlayerRngSeed: playerRng.Seed,
            PlayerRngs: playerRngDtos,
            Player: playerDto,
            Cards: cardsDto,
            Relics: relicsDto,
            Creatures: creaturesDto,
            AllyHandles: state.Allies.Select(c => creatureHandles[c]).ToArray(),
            EnemyHandles: state.Enemies.Select(c => creatureHandles[c]).ToArray(),
            EscapedHandles: state.EscapedCreatures.Select(c => creatureHandles[c]).ToArray(),
            RemovedHandles: removedHandles,
            SpawnedEnemyHandles: spawnedEnemyHandles,
            PetHandles: pcs.Pets.Select(c => creatureHandles[c]).ToArray(),
            DamageHistory: damageDtos,
            SemanticHistory: semanticDto,
            BattlewornDummyTimedOut: battlewornDummyTimedOut,
            UnlockedCharacterCards: unlockedCharacterCards,
            UnlockedColorlessCards: unlockedColorlessCards,
            UnlockedCurseCards: unlockedCurseCards,
            OtherCharacterCards: otherCharacterCards,
            TarotCards: tarotCards,
            UnlockedPotions: unlockedPotions);

        return (liveState, battleDamage.HpLostSoFar);
    }

    private static int[] CaptureSpawnedEnemyHandles(
        CombatState state,
        IReadOnlyDictionary<Creature, int> creatureHandles)
    {
        var observedCombatIds = new HashSet<uint>();
        var spawnedEnemies = new List<Creature>();
        foreach (Creature creature in creatureHandles.Keys)
        {
            if (creature.CombatId is not uint combatId)
            {
                throw new InCombatLiveCaptureUnsupportedException(
                    "live_private_state_unmapped",
                    "spawnedEnemyHandles",
                    $"Creature '{creature.ModelId.Entry}' is missing CombatId during live capture.");
            }
            observedCombatIds.Add(combatId);
            if (creature.Side == CombatSide.Enemy)
                spawnedEnemies.Add(creature);
        }

        for (uint expectedId = 0; expectedId < state._nextCreatureId; expectedId++)
        {
            if (!observedCombatIds.Contains(expectedId))
            {
                throw new InCombatLiveCaptureUnsupportedException(
                    "live_private_state_unmapped",
                    "spawnedEnemyHandles",
                    $"Spawned creature combatId={expectedId} (< nextCreatureId={state._nextCreatureId}) " +
                    "was removed without surviving references.");
            }
        }

        spawnedEnemies.Sort(static (a, b) => a.CombatId!.Value.CompareTo(b.CombatId!.Value));

        if (state.Encounter is { } encounter)
        {
            foreach (MonsterModel expectedMonster in encounter.SpawnedEnemies)
            {
                bool matched = spawnedEnemies.Any(e => e.Monster is { } m
                    && (ReferenceEquals(m.CanonicalInstance, expectedMonster) || m.Id == expectedMonster.Id));
                if (!matched)
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_private_state_unmapped",
                        "spawnedEnemyHandles",
                        $"Encounter.SpawnedEnemies monster '{expectedMonster.Id.Entry}' has no surviving creature handle.");
                }
            }
        }

        return spawnedEnemies.Select(c => creatureHandles[c]).ToArray();
    }

    private static RustKernelCardDto CaptureCard(
        CardModel c,
        int handle,
        CombatState state,
        IReadOnlyDictionary<CardModel, int> cardHandles,
        CardPlayFinishedEntry[] finishedEntries,
        IReadOnlyDictionary<CardModel, int> theHuntPendingRewards)
    {
        CardEnergyCost energyCost = c.EnergyCost;
        var localModifiers = new List<RustKernelLocalEnergyModifierDto>(energyCost._localModifiers.Count);
        foreach (LocalCostModifier m in energyCost._localModifiers)
        {
            string kind = (m.Type, m.Expiration) switch
            {
                (LocalCostType.Absolute, LocalCostModifierExpiration.EndOfCombat) => "SetThisCombat",
                (LocalCostType.Absolute, LocalCostModifierExpiration.EndOfTurn) => "SetThisTurn",
                (LocalCostType.Absolute, LocalCostModifierExpiration.WhenPlayed) => "SetUntilPlayed",
                (LocalCostType.Absolute, LocalCostModifierExpiration.EndOfTurn | LocalCostModifierExpiration.WhenPlayed) => "SetThisTurnOrUntilPlayed",
                (LocalCostType.Relative, LocalCostModifierExpiration.EndOfCombat) => "AddThisCombat",
                (LocalCostType.Relative, LocalCostModifierExpiration.EndOfTurn) => "AddThisTurn",
                (LocalCostType.Relative, LocalCostModifierExpiration.WhenPlayed) => "AddUntilPlayed",
                (LocalCostType.Relative, LocalCostModifierExpiration.EndOfTurn | LocalCostModifierExpiration.WhenPlayed) => "AddThisTurnOrUntilPlayed",
                _ => throw new InCombatLiveCaptureUnsupportedException(
                    "live_card_cost_unsupported",
                    $"cards[{c.Id.Entry}].energyCost",
                    $"Unsupported LocalCostModifier ({m.Type}, {m.Expiration}) on card '{c.Id.Entry}'."),
            };
            localModifiers.Add(new RustKernelLocalEnergyModifierDto(kind, m.Amount, m.IsReduceOnly));
        }

        int? tempStarCost = null;
        bool tempFreeUntilPlayed = false;
        bool tempFreeThisCombat = false;
        foreach (TemporaryCardCost starMod in c._temporaryStarCosts)
        {
            if (starMod is null)
                continue;
            if (!starMod.ClearsWhenTurnEnds && !starMod.ClearsWhenCardIsPlayed && starMod.Cost == 0)
            {
                tempFreeThisCombat = true;
            }
            else if (!starMod.ClearsWhenTurnEnds && starMod.ClearsWhenCardIsPlayed && starMod.Cost == 0)
            {
                tempFreeUntilPlayed = true;
            }
            else if (starMod.ClearsWhenTurnEnds && starMod.ClearsWhenCardIsPlayed)
            {
                if (tempStarCost is not null)
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_card_cost_unsupported",
                        $"cards[{c.Id.Entry}].starCost",
                        $"Multiple stacked Turn-End TemporaryCardCost layers ({c._temporaryStarCosts.Count}) on card '{c.Id.Entry}'.");
                }
                tempStarCost = starMod.Cost;
            }
            else
            {
                throw new InCombatLiveCaptureUnsupportedException(
                    "live_card_cost_unsupported",
                    $"cards[{c.Id.Entry}].starCost",
                    $"Unsupported star cost lifetime on card '{c.Id.Entry}'.");
            }
        }

        RustKernelEnchantmentDto? enchantment = c.Enchantment is null
            ? null
            : new RustKernelEnchantmentDto(
                c.Enchantment.Id.ToString(),
                c.Enchantment.Amount,
                c.Enchantment.Status.ToString(),
                CaptureEnchantmentPrivateFields(c.Enchantment));

        RustKernelAfflictionDto? affliction = c.Affliction is null
            ? null
            : new RustKernelAfflictionDto(
                c.Affliction.Id.ToString(),
                c.Affliction.Amount);

        IReadOnlyDictionary<string, RustKernelFieldValueDto> priv = CaptureCardPrivateFields(
            c, state, cardHandles, finishedEntries, theHuntPendingRewards);

        CardModel? cloneOrigin = c._cloneOf ?? c.DupeOf;
        return new RustKernelCardDto(
            Handle: handle,
            CardId: c.Id.ToString(),
            UpgradeLevel: c.CurrentUpgradeLevel,
            DeckCardHandle: c.DeckVersion is not null ? cardHandles[c.DeckVersion] : null,
            CloneOfHandle: cloneOrigin is not null ? cardHandles[cloneOrigin] : null,
            IsDupe: c.IsDupe,
            ExhaustOnNextPlay: c.ExhaustOnNextPlay,
            BaseReplayCount: c.BaseReplayCount,
            CapturedEnergyXValue: energyCost.CostsX ? energyCost.CapturedXValue : 0,
            FloorAddedToDeck: c.FloorAddedToDeck,
            CanonicalBaseEnergyCost: energyCost.Canonical,
            BaseEnergyCost: energyCost._base,
            CostsXEnergy: energyCost.CostsX,
            LocalEnergyModifiers: localModifiers,
            TemporaryStarCostOverrideThisTurn: tempStarCost,
            TemporaryFreeThisTurn: false,
            TemporaryFreeUntilPlayed: tempFreeUntilPlayed,
            TemporaryFreeThisCombat: tempFreeThisCombat,
            TemporaryCostOverrideThisTurn: null,
            TemporaryCostOverrideThisTurnOrUntilPlayed: null,
            TemporaryCostOverrideThisCombat: null,
            TemporaryRetainThisTurn: c._hasSingleTurnRetain,
            TemporarySlyThisTurn: c._hasSingleTurnSly,
            LocalKeywords: c.GetKeywordsWithSources(KeywordSources.Local).Select(static k => k.ToString()).ToArray(),
            KeywordsAddedFromOutside: c._keywordsAddedFromOutside.Select(static k => k.ToString()).ToArray(),
            KeywordsRemovedFromOutside: c._keywordsRemovedFromOutside.Select(static k => k.ToString()).ToArray(),
            Enchantment: enchantment,
            Affliction: affliction,
            PrivateFields: priv);
    }

    private static RustKernelCreatureDto CaptureCreature(
        Creature creature,
        int handle,
        CombatState state,
        RunState run,
        bool isRemovedFromCombat,
        IReadOnlyDictionary<Creature, int> creatureHandles,
        IReadOnlyDictionary<CardModel, int> cardHandles,
        CardPlayStartedEntry[] startedEntries)
    {
        int? petOwnerIdx = null;
        if (creature.PetOwner is not null)
        {
            for (int i = 0; i < run.Players.Count; i++)
            {
                if (ReferenceEquals(run.Players[i], creature.PetOwner))
                {
                    petOwnerIdx = i;
                    break;
                }
            }
        }

        RustKernelMonsterDto? monsterDto = null;
        if (creature.Monster is { } m)
        {
            MonsterMoveStateMachine? sm = m.MoveStateMachine;
            if (sm is null)
            {
                if (!isRemovedFromCombat || creature.IsAlive)
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_monster_state_unmapped",
                        $"creatures[{creature.Monster?.Id.Entry ?? "unknown"}]",
                        "Monster is missing MoveStateMachine.");
                }
            }
            else
            {
                string currentStateId = sm._currentState.Id;
                string? transientFollowUp = sm.States.ContainsKey(currentStateId)
                    ? null
                    : (sm._currentState as MoveState)?.FollowUpState?.Id
                      ?? (sm._currentState as MoveState)?.FollowUpStateId;

                (IReadOnlyDictionary<string, RustKernelFieldValueDto> setupFields,
                    IReadOnlyDictionary<string, RustKernelFieldValueDto> postFields) =
                    CaptureMonsterPrivateFields(m, creatureHandles);

                MegaCrit.Sts2.Core.Random.Rng? rawMonsterRng = m._rng ?? m.Rng;
                if (rawMonsterRng is null)
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_monster_state_unmapped",
                        $"creatures[{m.Id.Entry}].rng",
                        $"Monster '{m.Id.Entry}' is missing initialized Rng.");
                }
                SerializableRng monsterRng = rawMonsterRng.ToSerializable();
                monsterDto = new RustKernelMonsterDto(
                    ToRngDto(monsterRng),
                    m.IsPerformingMove,
                    m.SpawnedThisTurn,
                    currentStateId,
                    CaptureNextMoveId(m),
                    m.NextMove?.CanTransitionAway ?? true,
                    sm._performedFirstMove,
                    sm.StateLog.Select(static s => s.Id).ToArray(),
                    transientFollowUp,
                    setupFields,
                    postFields);
            }
        }

        var powersDto = creature.Powers.Select(power => new RustKernelPowerDto(
            power.Id.ToString(),
            power.Amount,
            power.AmountOnTurnStart,
            power.SkipNextDurationTick,
            power.Applier is not null ? creatureHandles[power.Applier] : null,
            CapturePowerPrivateFields(power, creatureHandles, cardHandles, startedEntries)))
            .ToArray();

        int cumulativeHpLost = CombatManager.Instance.History.Entries
            .OfType<DamageReceivedEntry>()
            .Where(e => ReferenceEquals(e.Receiver, creature))
            .Sum(e => Math.Max(0, e.Result.UnblockedDamage));

        return new RustKernelCreatureDto(
            Handle: handle,
            CombatId: creature.CombatId,
            Side: creature.Side.ToString(),
            SlotName: creature.SlotName,
            IsPlayer: creature.IsPlayer,
            PetOwnerPlayerIndex: petOwnerIdx,
            MonsterId: creature.Monster?.Id.ToString(),
            CurrentHp: creature.CurrentHp,
            MaxHp: creature.MaxHp,
            Block: creature.Block,
            CumulativeHpLost: cumulativeHpLost,
            IsDetached: creature.CombatState is null || isRemovedFromCombat,
            Monster: monsterDto,
            Powers: powersDto);
    }

    private static RustKernelPlayerCountersDto CapturePlayerCounters(
        CombatState state,
        Player player,
        CardPlayStartedEntry[] startedEntries,
        CardPlayFinishedEntry[] finishedEntries,
        CombatHistoryEntry[] allHistory)
    {
        var playOrdinalByCardPlay = new Dictionary<CardPlay, int>(ReferenceEqualityComparer.Instance);
        int nextOrdinal = 1;
        foreach (CardPlayStartedEntry started in startedEntries)
        {
            if (ReferenceEquals(started.CardPlay.Player, player) && !playOrdinalByCardPlay.ContainsKey(started.CardPlay))
                playOrdinalByCardPlay[started.CardPlay] = nextOrdinal++;
        }

        int FirstMelodyOrdinalThisTurn(CardTag tag)
        {
            foreach (CardPlayStartedEntry entry in startedEntries)
            {
                if (entry.HappenedThisTurn(state)
                    && ReferenceEquals(entry.CardPlay.Player, player)
                    && entry.CardPlay.Card.Tags.Contains(tag))
                {
                    return playOrdinalByCardPlay.TryGetValue(entry.CardPlay, out int ord) ? ord : 0;
                }
            }
            return 0;
        }

        return new RustKernelPlayerCountersDto(
            StarsGainedThisTurn: allHistory.OfType<StarsModifiedEntry>()
                .Where(e => e.HappenedThisTurn(state) && ReferenceEquals(e.Actor, player.Creature) && e.Amount > 0)
                .Sum(static e => (decimal)e.Amount),
            SkillCardsPlayedThisTurn: finishedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player) && e.CardPlay.Card.Type == CardType.Skill),
            AttackCardsPlayedThisTurn: finishedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player) && e.CardPlay.Card.Type == CardType.Attack),
            ShivsPlayedThisTurn: finishedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player) && e.CardPlay.Card.Tags.Contains(CardTag.Shiv)),
            CardsDiscardedThisTurn: allHistory.OfType<CardDiscardedEntry>()
                .Count(e => e.HappenedThisTurn(state) && ReferenceEquals(e.Actor, player.Creature)),
            CardsPlayedThisCombat: finishedEntries.Count(e => ReferenceEquals(e.CardPlay.Player, player)),
            CardsGeneratedThisCombat: allHistory.OfType<CardGeneratedEntry>()
                .Count(e => ReferenceEquals(e.Creator, player)),
            SadnessGeneratedThisCombat: allHistory.OfType<CardGeneratedEntry>()
                .Count(e => ReferenceEquals(e.Creator, player) && e.Card is MegaCrit.Sts2.Core.Models.Cards.Sadness),
            CardsDrawnThisCombat: allHistory.OfType<CardDrawnEntry>()
                .Count(e => ReferenceEquals(e.Actor, player.Creature)),
            CardsPlayedThisTurn: finishedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player)),
            ManualCardsPlayedThisTurn: finishedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player) && !e.CardPlay.IsAutoPlay),
            CardPlaysStartedThisTurn: startedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player)),
            AttackOrSkillCardPlaysStartedThisTurn: startedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player)
                && e.CardPlay.Card.Type is CardType.Attack or CardType.Skill),
            AttackCardsStartedThisTurn: startedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player) && e.CardPlay.Card.Type == CardType.Attack),
            ZeroCostAttacksStartedThisTurn: startedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player)
                && e.CardPlay.Card.Type == CardType.Attack && e.CardPlay.Resources.EnergyValue == 0),
            FirstInSeriesCardPlaysStartedThisTurn: startedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player)
                && e.CardPlay.IsFirstInSeries && ReferenceEquals(e.CardPlay.Card.Owner, player)),
            WahStartedThisTurn: startedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player) && e.CardPlay.Card.Tags.Contains(CardTag.Wah)),
            ChoStartedThisTurn: startedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player) && e.CardPlay.Card.Tags.Contains(CardTag.Cho)),
            KiStartedThisTurn: startedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player) && e.CardPlay.Card.Tags.Contains(CardTag.Ki)),
            FirstWahPlayOrdinalThisTurn: FirstMelodyOrdinalThisTurn(CardTag.Wah),
            FirstChoPlayOrdinalThisTurn: FirstMelodyOrdinalThisTurn(CardTag.Cho),
            FirstKiPlayOrdinalThisTurn: FirstMelodyOrdinalThisTurn(CardTag.Ki),
            WahFinishedThisTurn: finishedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player) && e.CardPlay.Card.Tags.Contains(CardTag.Wah)),
            ChoFinishedThisTurn: finishedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player) && e.CardPlay.Card.Tags.Contains(CardTag.Cho)),
            KiFinishedThisTurn: finishedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player) && e.CardPlay.Card.Tags.Contains(CardTag.Ki)),
            MelodyCardsFinishedThisTurn: finishedEntries.Count(e =>
                e.HappenedThisTurn(state) && ReferenceEquals(e.CardPlay.Player, player)
                && (e.CardPlay.Card.Tags.Contains(CardTag.Wah)
                    || e.CardPlay.Card.Tags.Contains(CardTag.Cho)
                    || e.CardPlay.Card.Tags.Contains(CardTag.Ki))));
    }

    private static RustKernelSemanticHistoryDto CaptureSemanticHistory(
        RunState run,
        CombatHistoryEntry[] allHistory,
        CardPlayStartedEntry[] startedEntries)
    {
        var playOrdinalByCardPlay = new Dictionary<CardPlay, int>(ReferenceEqualityComparer.Instance);
        int nextOrdinal = 1;
        foreach (CardPlayStartedEntry started in startedEntries)
        {
            if (!playOrdinalByCardPlay.ContainsKey(started.CardPlay))
                playOrdinalByCardPlay[started.CardPlay] = nextOrdinal++;
        }

        int PlayerIndex(Player? player)
        {
            if (player is null)
                return -1;
            for (int i = 0; i < run.Players.Count; i++)
            {
                if (ReferenceEquals(run.Players[i], player))
                    return i;
            }
            return -1;
        }

        RustKernelTurnKeyDto MakeTurnKey(CombatHistoryEntry e) =>
            new(
                e.RoundNumber,
                e.CurrentSide.ToString(),
                run.Players
                    .Select(p => e._playerTurnNumbers.TryGetValue(p.NetId, out int t) ? (int?)t : null)
                    .ToArray());

        var exhausts = allHistory.OfType<CardExhaustedEntry>()
            .Select(e => (Idx: PlayerIndex(e.Actor?.Player), Entry: e))
            .Where(static x => x.Idx >= 0)
            .Select(x => new RustKernelExhaustHistoryDto(MakeTurnKey(x.Entry), x.Idx))
            .ToArray();

        var blockGains = allHistory.OfType<BlockGainedEntry>()
            .Where(static e => e.CardPlay is not null && e.Props.HasFlag(ValueProp.Move))
            .Select(e => (Idx: PlayerIndex(e.CardPlay!.Player), Entry: e))
            .Where(static x => x.Idx >= 0)
            .Select(x => new RustKernelBlockGainHistoryDto(
                MakeTurnKey(x.Entry),
                x.Idx,
                playOrdinalByCardPlay.TryGetValue(x.Entry.CardPlay!, out int ord) ? ord : 0))
            .ToArray();

        var orbChannels = allHistory.OfType<OrbChanneledEntry>()
            .Select(e => (Idx: PlayerIndex(e.Orb.Owner), Entry: e))
            .Where(static x => x.Idx >= 0)
            .Select(x => new RustKernelOrbChannelHistoryDto(MakeTurnKey(x.Entry), x.Idx, x.Entry.Orb.Id.ToString()))
            .ToArray();

        var energySpent = allHistory.OfType<EnergySpentEntry>()
            .Where(static e => e.Amount > 0)
            .Select(e => (Idx: PlayerIndex(e.Actor?.Player), Entry: e))
            .Where(static x => x.Idx >= 0)
            .Select(x => new RustKernelEnergySpentHistoryDto(MakeTurnKey(x.Entry), x.Idx, x.Entry.Amount))
            .ToArray();

        var cardDrawn = allHistory.OfType<CardDrawnEntry>()
            .Select(e => (Idx: PlayerIndex(e.Card.Owner), Entry: e))
            .Where(static x => x.Idx >= 0)
            .Select(x => new RustKernelCardDrawnHistoryDto(
                MakeTurnKey(x.Entry),
                x.Idx,
                x.Entry.Card.Type.ToString(),
                x.Entry.FromHandDraw))
            .ToArray();

        var actorEvents = new List<RustKernelActorEventHistoryDto>();
        foreach (CombatHistoryEntry entry in allHistory)
        {
            switch (entry)
            {
                case CreatureAttackedEntry attacked when attacked.Actor?.Monster is MegaCrit.Sts2.Core.Models.Monsters.Osty
                    && PlayerIndex(attacked.Actor.PetOwner) is int ostyOwner && ostyOwner >= 0:
                    actorEvents.Add(new RustKernelActorEventHistoryDto("OstyAttack", MakeTurnKey(entry), ostyOwner));
                    break;
                case PowerReceivedEntry received when received.Power is DoomPower
                    && PlayerIndex(received.Applier?.Player) is int doomActor && doomActor >= 0:
                    actorEvents.Add(new RustKernelActorEventHistoryDto("DoomApplied", MakeTurnKey(entry), doomActor));
                    break;
                case CardPlayFinishedEntry finished when finished.WasEthereal
                    && PlayerIndex(finished.CardPlay.Player) is int ethActor && ethActor >= 0:
                    actorEvents.Add(new RustKernelActorEventHistoryDto("EtherealPlayFinished", MakeTurnKey(entry), ethActor));
                    break;
            }
        }

        return new RustKernelSemanticHistoryDto(
            exhausts,
            blockGains,
            orbChannels,
            energySpent,
            cardDrawn,
            actorEvents);
    }

    private static string? CaptureNextMoveId(MonsterModel? monster) =>
        monster?.NextMove?.Id is { Length: > 0 } id
        && !string.Equals(id, "UNSET_MOVE", StringComparison.Ordinal)
            ? id
            : null;

    private static RustKernelRngStreamDto ToRngDto(SerializableRng r) =>
        new(r.counter, r.state0, r.state1, r.state2, r.state3);
}
