using System.Text;
using CombatSolver.Api;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Monsters;
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
    public static (string NormalizedSaveJson, InCombatLiveStateDto LiveState, int PriorBattleHpLost) Capture(
        CombatState state,
        BattleDamageSnapshot battleDamage)
    {
        if (state.RunState is not RunState concreteRun || concreteRun.Players.Count != 1)
        {
            throw new InCombatLiveCaptureUnsupportedException(
                "live_combat_precondition",
                "players",
                "In-combat Sts2Sim requires a single-player RunState.");
        }
        if (state.Encounter is null)
        {
            throw new InCombatLiveCaptureUnsupportedException(
                "encounter_unmapped",
                "encounterId",
                "Active combat has no EncounterModel.");
        }

        SerializableRun serializableRun = RunManager.Instance.ToSave(null);
        byte[] normalizedBytes = PreCombatRunSerialization.SerializeNormalized(serializableRun);
        string normalizedSaveJson = Encoding.UTF8.GetString(normalizedBytes);

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

        foreach (DamageReceivedEntry dmg in damageEntries)
        {
            if (dmg.Receiver is not null) RegisterCreature(dmg.Receiver);
            if (dmg.Dealer is not null) RegisterCreature(dmg.Dealer);
            if (dmg.Result.OriginalTarget is not null) RegisterCreature(dmg.Result.OriginalTarget);
            if (dmg.CardSource is not null) RegisterCard(dmg.CardSource);
        }

        foreach (CardPlayFinishedEntry finished in finishedEntries)
        {
            if (finished.CardPlay.Card is { } playedCard)
                RegisterCard(playedCard);
        }

        // 提前遍历场上 Creature/Power/Relic 所引用的生物和卡牌句柄，确保外键句柄闭合。
        RegisterPrivateReferenceTargets(player, creatureHandles, RegisterCreature, RegisterCard);

        var cardsDto = cardHandles
            .OrderBy(static kv => kv.Value)
            .Select(kv => CaptureCard(kv.Key, kv.Value, state, cardHandles, finishedEntries))
            .ToArray();

        var relicsDto = player.Relics
            .Select(relic => CaptureRelic(relic, state, player, cardHandles, startedEntries, finishedEntries))
            .ToArray();

        var creaturesDto = creatureHandles
            .OrderBy(static kv => kv.Value)
            .Select(kv => CaptureCreature(
                kv.Key,
                kv.Value,
                state,
                concreteRun,
                creatureHandles,
                cardHandles,
                startedEntries))
            .ToArray();

        // 计算不在 Allies/Enemies/Escaped 中的脱离生物，填入 RemovedHandles。
        var activeAndEscaped = new HashSet<Creature>(
            state.Allies.Concat(state.Enemies).Concat(state.EscapedCreatures),
            ReferenceEqualityComparer.Instance);
        int[] removedHandles = creatureHandles
            .Where(kv => !activeAndEscaped.Contains(kv.Key))
            .OrderBy(static kv => kv.Value)
            .Select(static kv => kv.Value)
            .ToArray();

        SerializableRunRngSet runRng = state.RunState.Rng.ToSerializable();
        Dictionary<string, InCombatRngStreamDto> runRngDtos = runRng.Rngs.ToDictionary(
            static kv => kv.Key.ToString(),
            static kv => ToRngDto(kv.Value),
            StringComparer.Ordinal);
        SerializablePlayerRngSet playerRng = player.PlayerRng.ToSerializable();
        Dictionary<string, InCombatRngStreamDto> playerRngDtos = playerRng.Rngs.ToDictionary(
            static kv => kv.Key.ToString(),
            static kv => ToRngDto(kv.Value),
            StringComparer.Ordinal);

        int playerCumulativeHpLost = damageEntries
            .Where(e => ReferenceEquals(e.Receiver, player.Creature))
            .Sum(e => Math.Max(0, e.Result.UnblockedDamage));

        var countersDto = CapturePlayerCounters(state, player, startedEntries, finishedEntries, allHistory);

        var orbsDto = pcs.OrbQueue.Orbs
            .Select(orb => new InCombatOrbDto(
                orb.Id.ToString(),
                orb is DarkOrb dark ? dark._evokeVal : null,
                orb is GlassOrb glass ? glass._passiveVal : null))
            .ToArray();

        var arrowsDto = pcs.Arrows
            .Select(arrow => new InCombatArrowSlotDto(arrow.Status.ToString(), arrow.DestroyAfterImpaled))
            .ToArray();

        var playerDto = new InCombatPlayerDto(
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

        InCombatDamageHistoryEntryDto[] damageDtos = damageEntries
            .Where(e => e.Receiver is not null && e.Result.OriginalTarget is not null)
            .Select(e => new InCombatDamageHistoryEntryDto(
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

        InCombatSemanticHistoryDto semanticDto = CaptureSemanticHistory(concreteRun, allHistory, startedEntries);

        var verificationDto = new InCombatVerificationDto(
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.Creature.Block,
            pcs.Energy,
            pcs.Stars,
            player.Gold,
            pcs.OrbQueue.Capacity,
            pcs.OrbQueue.Orbs.Select(static o => o.Id.ToString()).ToArray(),
            pcs.Arrows.Select(static a => a.Status.ToString()).ToArray(),
            player.PotionSlots.Select(static p => p?.Id.ToString()).ToArray(),
            pcs.Hand.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.DrawPile.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.DiscardPile.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.ExhaustPile.Cards.Select(ToVerifiedCard).ToArray(),
            state.Enemies.Select(static e => new InCombatVerifiedCreatureDto(
                e.CombatId,
                e.Monster?.Id.ToString(),
                e.CurrentHp,
                e.MaxHp,
                e.Block,
                CaptureNextMoveId(e.Monster),
                e.Powers.Select(static pw => new InCombatVerifiedPowerDto(pw.Id.ToString(), pw.Amount)).ToArray())).ToArray(),
            player.Creature.Powers.Select(static pw => new InCombatVerifiedPowerDto(pw.Id.ToString(), pw.Amount)).ToArray());

        bool goldWasStolen = state.Encounter is GremlinMercNormal gremlinMerc && gremlinMerc._goldWasStolen;
        bool isExtraTurn = CombatManager.Instance.PlayersTakingExtraTurn.Contains(player);
        int[] spawnedEnemyHandles = CaptureSpawnedEnemyHandles(state, creatureHandles);

        var liveState = new InCombatLiveStateDto(
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
            Verification: verificationDto);

        return (normalizedSaveJson, liveState, battleDamage.HpLostSoFar);

        static InCombatVerifiedCardDto ToVerifiedCard(CardModel c) =>
            new(
                c.Id.ToString(),
                c.CurrentUpgradeLevel,
                c.EnergyCost.GetResolved(),
                c.GetKeywordsWithSources(KeywordSources.Local).Select(static k => k.ToString()).ToArray());
    }

    private static int[] CaptureSpawnedEnemyHandles(
        CombatState state,
        IReadOnlyDictionary<Creature, int> creatureHandles)
    {
        var handles = new List<int>(state.Enemies.Count + state.EscapedCreatures.Count);
        var seen = new HashSet<int>();
        foreach (Creature enemy in state.Enemies.Concat(state.EscapedCreatures))
        {
            if (creatureHandles.TryGetValue(enemy, out int h) && seen.Add(h))
                handles.Add(h);
        }
        return handles.ToArray();
    }

    private static InCombatCardDto CaptureCard(
        CardModel c,
        int handle,
        CombatState state,
        IReadOnlyDictionary<CardModel, int> cardHandles,
        CardPlayFinishedEntry[] finishedEntries)
    {
        CardEnergyCost energyCost = c.EnergyCost;
        var localModifiers = new List<InCombatLocalEnergyModifierDto>(energyCost._localModifiers.Count);
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
            localModifiers.Add(new InCombatLocalEnergyModifierDto(kind, m.Amount, m.IsReduceOnly));
        }

        int? tempStarCost = null;
        if (c._temporaryStarCosts.Count > 1)
        {
            throw new InCombatLiveCaptureUnsupportedException(
                "live_card_cost_unsupported",
                $"cards[{c.Id.Entry}].starCost",
                $"Multiple stacked TemporaryCardCost layers ({c._temporaryStarCosts.Count}) on card '{c.Id.Entry}'.");
        }
        if (c._temporaryStarCosts.Count == 1)
        {
            TemporaryCardCost starMod = c._temporaryStarCosts[0];
            if (!starMod.ClearsWhenTurnEnds || !starMod.ClearsWhenCardIsPlayed)
            {
                throw new InCombatLiveCaptureUnsupportedException(
                    "live_card_cost_unsupported",
                    $"cards[{c.Id.Entry}].starCost",
                    $"Unsupported star cost lifetime on card '{c.Id.Entry}'.");
            }
            tempStarCost = starMod.Cost;
        }

        InCombatEnchantmentDto? enchantment = c.Enchantment is null
            ? null
            : new InCombatEnchantmentDto(
                c.Enchantment.Id.ToString(),
                c.Enchantment.Amount,
                c.Enchantment.Status.ToString(),
                CaptureEnchantmentPrivateFields(c.Enchantment));

        InCombatAfflictionDto? affliction = c.Affliction is null
            ? null
            : new InCombatAfflictionDto(
                c.Affliction.Id.ToString(),
                c.Affliction.Amount);

        IReadOnlyDictionary<string, InCombatFieldValueDto> priv = CaptureCardPrivateFields(
            c, state, cardHandles, finishedEntries);

        CardModel? cloneOrigin = c._cloneOf ?? c.DupeOf;
        return new InCombatCardDto(
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
            TemporaryFreeUntilPlayed: false,
            TemporaryFreeThisCombat: false,
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

    private static InCombatCreatureDto CaptureCreature(
        Creature creature,
        int handle,
        CombatState state,
        RunState run,
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

        InCombatMonsterDto? monsterDto = null;
        if (creature.Monster is { } m)
        {
            MonsterMoveStateMachine sm = m.MoveStateMachine
                ?? throw new InCombatLiveCaptureUnsupportedException(
                    "live_monster_state_unmapped",
                    $"creatures[{creature.Monster?.Id.Entry ?? "unknown"}]",
                    "Monster is missing MoveStateMachine.");
            string currentStateId = sm._currentState.Id;
            string? transientFollowUp = sm.States.ContainsKey(currentStateId)
                ? null
                : (sm._currentState as MoveState)?.FollowUpState?.Id
                  ?? (sm._currentState as MoveState)?.FollowUpStateId;

            (IReadOnlyDictionary<string, InCombatFieldValueDto> setupFields,
                IReadOnlyDictionary<string, InCombatFieldValueDto> postFields) =
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
            monsterDto = new InCombatMonsterDto(
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

        var powersDto = creature.Powers.Select(power => new InCombatPowerDto(
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

        return new InCombatCreatureDto(
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
            IsDetached: creature.CombatState is null,
            Monster: monsterDto,
            Powers: powersDto);
    }

    private static InCombatPlayerCountersDto CapturePlayerCounters(
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

        return new InCombatPlayerCountersDto(
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

    private static InCombatSemanticHistoryDto CaptureSemanticHistory(
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

        InCombatTurnKeyDto MakeTurnKey(CombatHistoryEntry e) =>
            new(
                e.RoundNumber,
                e.CurrentSide.ToString(),
                run.Players
                    .Select(p => e._playerTurnNumbers.TryGetValue(p.NetId, out int t) ? (int?)t : null)
                    .ToArray());

        var exhausts = allHistory.OfType<CardExhaustedEntry>()
            .Select(e => (Idx: PlayerIndex(e.Actor?.Player), Entry: e))
            .Where(static x => x.Idx >= 0)
            .Select(x => new InCombatExhaustHistoryDto(MakeTurnKey(x.Entry), x.Idx))
            .ToArray();

        var blockGains = allHistory.OfType<BlockGainedEntry>()
            .Where(static e => e.CardPlay is not null && e.Props.HasFlag(ValueProp.Move))
            .Select(e => (Idx: PlayerIndex(e.CardPlay!.Player), Entry: e))
            .Where(static x => x.Idx >= 0)
            .Select(x => new InCombatBlockGainHistoryDto(
                MakeTurnKey(x.Entry),
                x.Idx,
                playOrdinalByCardPlay.TryGetValue(x.Entry.CardPlay!, out int ord) ? ord : 0))
            .ToArray();

        var orbChannels = allHistory.OfType<OrbChanneledEntry>()
            .Select(e => (Idx: PlayerIndex(e.Orb.Owner), Entry: e))
            .Where(static x => x.Idx >= 0)
            .Select(x => new InCombatOrbChannelHistoryDto(MakeTurnKey(x.Entry), x.Idx, x.Entry.Orb.Id.ToString()))
            .ToArray();

        var energySpent = allHistory.OfType<EnergySpentEntry>()
            .Where(static e => e.Amount > 0)
            .Select(e => (Idx: PlayerIndex(e.Actor?.Player), Entry: e))
            .Where(static x => x.Idx >= 0)
            .Select(x => new InCombatEnergySpentHistoryDto(MakeTurnKey(x.Entry), x.Idx, x.Entry.Amount))
            .ToArray();

        var cardDrawn = allHistory.OfType<CardDrawnEntry>()
            .Select(e => (Idx: PlayerIndex(e.Card.Owner), Entry: e))
            .Where(static x => x.Idx >= 0)
            .Select(x => new InCombatCardDrawnHistoryDto(
                MakeTurnKey(x.Entry),
                x.Idx,
                x.Entry.Card.Type.ToString(),
                x.Entry.FromHandDraw))
            .ToArray();

        var actorEvents = new List<InCombatActorEventHistoryDto>();
        foreach (CombatHistoryEntry entry in allHistory)
        {
            switch (entry)
            {
                case CreatureAttackedEntry attacked when attacked.Actor?.Monster is Osty
                    && PlayerIndex(attacked.Actor.PetOwner) is int ostyOwner && ostyOwner >= 0:
                    actorEvents.Add(new InCombatActorEventHistoryDto("OstyAttack", MakeTurnKey(entry), ostyOwner));
                    break;
                case PowerReceivedEntry received when received.Power is DoomPower
                    && PlayerIndex(received.Applier?.Player) is int doomActor && doomActor >= 0:
                    actorEvents.Add(new InCombatActorEventHistoryDto("DoomApplied", MakeTurnKey(entry), doomActor));
                    break;
                case CardPlayFinishedEntry finished when finished.WasEthereal
                    && PlayerIndex(finished.CardPlay.Player) is int ethActor && ethActor >= 0:
                    actorEvents.Add(new InCombatActorEventHistoryDto("EtherealPlayFinished", MakeTurnKey(entry), ethActor));
                    break;
            }
        }

        return new InCombatSemanticHistoryDto(
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

    private static InCombatRngStreamDto ToRngDto(SerializableRng r) =>
        new(r.counter, r.state0, r.state1, r.state2, r.state3);
}
