using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.FoolArrows;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Rngs;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;
using Sts2Sim.Core.ValueProps;

namespace Sts2Sim.PreCombatBridge;

internal static class LiveCombatTransplantMapper
{
    public static LiveCombatSnapshotSpec Map(InCombatLiveStateDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (string.IsNullOrWhiteSpace(dto.EncounterId))
            throw new BridgeRequestFailedException("liveState.encounterId", "EncounterId cannot be empty.");
        RoomType roomType = ParseEnum<RoomType>(dto.RoomType, "liveState.roomType");
        CombatSide currentSide = ParseEnum<CombatSide>(dto.CurrentSide, "liveState.currentSide");
        if (currentSide != CombatSide.Player)
        {
            throw new ForecastCapabilityMissException(
                "live_combat_precondition",
                "liveState.currentSide",
                $"Live combat bridge requires CurrentSide=Player, got {currentSide}.");
        }
        if (dto.RoundNumber < 1)
            throw new BridgeRequestFailedException("liveState.roundNumber", "RoundNumber must be >= 1.");
        if (string.IsNullOrWhiteSpace(dto.RunRngSeed))
            throw new BridgeRequestFailedException("liveState.runRngSeed", "RunRngSeed cannot be empty.");
        if (dto.Player is null)
            throw new BridgeRequestFailedException("liveState.player", "Player state cannot be null.");
        if (dto.Cards is null || dto.Relics is null || dto.Creatures is null || dto.Verification is null
            || dto.SemanticHistory is null || dto.DamageHistory is null)
        {
            throw new BridgeRequestFailedException("liveState", "LiveState contains null collections.");
        }

        SerializableRunRngSet runRng = ParseRunRngSet(dto.RunRngSeed, dto.RunRngs);
        SerializablePlayerRngSet playerRng = ParsePlayerRngSet(dto.PlayerRngSeed, dto.PlayerRngs);

        LivePlayerSnapshotSpec playerSpec = MapPlayer(dto.Player);
        var cardSpecs = new List<LiveCardSnapshotSpec>(dto.Cards.Count);
        foreach (InCombatCardDto c in dto.Cards)
            cardSpecs.Add(MapCard(c));

        var relicSpecs = new List<LiveRelicSnapshotSpec>(dto.Relics.Count);
        for (int i = 0; i < dto.Relics.Count; i++)
            relicSpecs.Add(MapRelic(dto.Relics[i], $"liveState.relics[{i}]"));

        var creatureSpecs = new List<LiveCreatureSnapshotSpec>(dto.Creatures.Count);
        for (int i = 0; i < dto.Creatures.Count; i++)
            creatureSpecs.Add(MapCreature(dto.Creatures[i], $"liveState.creatures[{i}]"));

        var damageEntries = new List<LiveDamageHistoryEntrySpec>(dto.DamageHistory.Count);
        for (int i = 0; i < dto.DamageHistory.Count; i++)
            damageEntries.Add(MapDamageEntry(dto.DamageHistory[i], $"liveState.damageHistory[{i}]"));

        LiveSemanticHistorySpec semanticSpec = MapSemanticHistory(dto.SemanticHistory);
        LiveCombatVerificationSpec verificationSpec = MapVerification(dto.Verification);

        return new LiveCombatSnapshotSpec(
            dto.EncounterId,
            roomType,
            dto.EncounterSlots ?? [],
            currentSide,
            dto.RoundNumber,
            dto.IsPlayerExtraTurn,
            dto.GoldWasStolen,
            dto.NextCreatureId,
            runRng,
            playerRng,
            playerSpec,
            cardSpecs,
            relicSpecs,
            creatureSpecs,
            dto.AllyHandles ?? [],
            dto.EnemyHandles ?? [],
            dto.EscapedHandles ?? [],
            dto.RemovedHandles ?? [],
            dto.SpawnedEnemyHandles ?? [],
            dto.PetHandles ?? [],
            damageEntries,
            semanticSpec,
            verificationSpec);
    }

    private static SerializableRunRngSet ParseRunRngSet(
        string seed,
        IReadOnlyDictionary<string, InCombatRngStreamDto>? streams)
    {
        if (streams is null)
            throw new BridgeRequestFailedException("liveState.runRngs", "Missing runRngs.");
        var dict = new Dictionary<RunRngType, SerializableRng>();
        foreach ((string key, InCombatRngStreamDto value) in streams)
        {
            RunRngType streamType = ParseEnum<RunRngType>(key, $"liveState.runRngs.{key}");
            if (!dict.TryAdd(streamType, ToSerializableRng(value, $"liveState.runRngs.{key}")))
                throw new BridgeRequestFailedException($"liveState.runRngs.{key}", $"Duplicate RunRng '{key}'.");
        }
        foreach (RunRngType req in Enum.GetValues<RunRngType>())
        {
            if (!dict.ContainsKey(req))
                throw new BridgeRequestFailedException($"liveState.runRngs.{req}", $"Missing required RunRng stream '{req}'.");
        }
        return new SerializableRunRngSet { Seed = seed, Rngs = dict };
    }

    private static SerializablePlayerRngSet ParsePlayerRngSet(
        ulong seed,
        IReadOnlyDictionary<string, InCombatRngStreamDto>? streams)
    {
        if (streams is null)
            throw new BridgeRequestFailedException("liveState.playerRngs", "Missing playerRngs.");
        var dict = new Dictionary<PlayerRngType, SerializableRng>();
        foreach ((string key, InCombatRngStreamDto value) in streams)
        {
            PlayerRngType streamType = ParseEnum<PlayerRngType>(key, $"liveState.playerRngs.{key}");
            if (!dict.TryAdd(streamType, ToSerializableRng(value, $"liveState.playerRngs.{key}")))
                throw new BridgeRequestFailedException($"liveState.playerRngs.{key}", $"Duplicate PlayerRng '{key}'.");
        }
        foreach (PlayerRngType req in Enum.GetValues<PlayerRngType>())
        {
            if (!dict.ContainsKey(req))
                throw new BridgeRequestFailedException($"liveState.playerRngs.{req}", $"Missing required PlayerRng stream '{req}'.");
        }
        return new SerializablePlayerRngSet { Seed = seed, Rngs = dict };
    }

    private static SerializableRng ToSerializableRng(InCombatRngStreamDto? dto, string path)
    {
        if (dto is null)
            throw new BridgeRequestFailedException(path, "RNG stream cannot be null.");
        if (dto.Counter < 0)
            throw new BridgeRequestFailedException($"{path}.counter", "RNG counter cannot be negative.");
        if ((dto.S0 | dto.S1 | dto.S2 | dto.S3) == 0UL)
            throw new BridgeRequestFailedException(path, "RNG state words (s0..s3) cannot all be zero.");
        return new SerializableRng
        {
            counter = dto.Counter,
            state0 = dto.S0,
            state1 = dto.S1,
            state2 = dto.S2,
            state3 = dto.S3,
        };
    }

    private static LivePlayerSnapshotSpec MapPlayer(InCombatPlayerDto p)
    {
        if (p.TurnNumber < 1)
            throw new BridgeRequestFailedException("liveState.player.turnNumber", "Player turnNumber must be >= 1.");
        if (p.Counters is null)
            throw new BridgeRequestFailedException("liveState.player.counters", "Missing player counters.");

        PlayerTurnPhase phase = ParseEnum<PlayerTurnPhase>(p.Phase, "liveState.player.phase");
        if (phase != PlayerTurnPhase.Play)
        {
            throw new ForecastCapabilityMissException(
                "live_combat_precondition",
                "liveState.player.phase",
                $"Live combat bridge requires player Phase=Play, got {phase}.");
        }
        var counters = new LivePlayerCombatCountersSpec(
            p.Counters.StarsGainedThisTurn,
            p.Counters.SkillCardsPlayedThisTurn,
            p.Counters.AttackCardsPlayedThisTurn,
            p.Counters.ShivsPlayedThisTurn,
            p.Counters.CardsDiscardedThisTurn,
            p.Counters.CardsPlayedThisCombat,
            p.Counters.CardsGeneratedThisCombat,
            p.Counters.SadnessGeneratedThisCombat,
            p.Counters.CardsDrawnThisCombat,
            p.Counters.CardsPlayedThisTurn,
            p.Counters.ManualCardsPlayedThisTurn,
            p.Counters.CardPlaysStartedThisTurn,
            p.Counters.AttackOrSkillCardPlaysStartedThisTurn,
            p.Counters.AttackCardsStartedThisTurn,
            p.Counters.ZeroCostAttacksStartedThisTurn,
            p.Counters.FirstInSeriesCardPlaysStartedThisTurn,
            p.Counters.WahStartedThisTurn,
            p.Counters.ChoStartedThisTurn,
            p.Counters.KiStartedThisTurn,
            p.Counters.FirstWahPlayOrdinalThisTurn,
            p.Counters.FirstChoPlayOrdinalThisTurn,
            p.Counters.FirstKiPlayOrdinalThisTurn,
            p.Counters.WahFinishedThisTurn,
            p.Counters.ChoFinishedThisTurn,
            p.Counters.KiFinishedThisTurn,
            p.Counters.MelodyCardsFinishedThisTurn);

        var orbs = new List<LiveOrbSnapshotSpec>(p.Orbs?.Count ?? 0);
        if (p.Orbs is not null)
        {
            foreach (InCombatOrbDto orb in p.Orbs)
                orbs.Add(new LiveOrbSnapshotSpec(orb.OrbId, orb.RawEvokeValue, orb.RawPassiveValue));
        }

        var arrows = new List<LiveArrowSlotSnapshotSpec>(p.Arrows?.Count ?? 0);
        if (p.Arrows is not null)
        {
            for (int i = 0; i < p.Arrows.Count; i++)
            {
                InCombatArrowSlotDto a = p.Arrows[i];
                arrows.Add(new LiveArrowSlotSnapshotSpec(
                    ParseEnum<ArrowStatus>(a.Status, $"liveState.player.arrows[{i}].status"),
                    a.DestroyAfterImpaled));
            }
        }

        return new LivePlayerSnapshotSpec(
            p.CurrentHp,
            p.MaxHp,
            p.Block,
            p.CumulativeHpLost,
            p.Gold,
            p.MaxEnergy,
            p.BaseOrbSlotCount,
            p.IsActiveForHooks,
            p.CanUseOrRemovePotions,
            p.Energy,
            p.Stars,
            phase,
            p.TurnNumber,
            counters,
            p.HandHandles ?? [],
            p.DrawHandles ?? [],
            p.DiscardHandles ?? [],
            p.ExhaustHandles ?? [],
            p.PlayHandles ?? [],
            p.DeckHandles ?? [],
            p.PotionSlots ?? [],
            p.OrbCapacity,
            orbs,
            arrows);
    }

    private static LiveCardSnapshotSpec MapCard(InCombatCardDto c)
    {
        string path = $"liveState.cards[handle={c.Handle}]";
        var mods = new List<LiveLocalEnergyModifierSpec>(c.LocalEnergyModifiers?.Count ?? 0);
        if (c.LocalEnergyModifiers is not null)
        {
            for (int i = 0; i < c.LocalEnergyModifiers.Count; i++)
            {
                InCombatLocalEnergyModifierDto m = c.LocalEnergyModifiers[i];
                mods.Add(new LiveLocalEnergyModifierSpec(
                    ParseEnum<CardModel.LocalEnergyModifierKind>(m.Kind, $"{path}.localEnergyModifiers[{i}].kind"),
                    m.Amount,
                    m.IsReduceOnly));
            }
        }

        LiveEnchantmentSpec? enchSpec = c.Enchantment is null
            ? null
            : new LiveEnchantmentSpec(
                c.Enchantment.EnchantmentId,
                c.Enchantment.Amount,
                ParseEnum<EnchantmentStatus>(c.Enchantment.Status, $"{path}.enchantment.status"),
                MapFields(c.Enchantment.PrivateFields, $"{path}.enchantment.privateFields"));

        LiveAfflictionSpec? affSpec = c.Affliction is null
            ? null
            : new LiveAfflictionSpec(c.Affliction.AfflictionId, c.Affliction.Amount);

        return new LiveCardSnapshotSpec(
            c.Handle,
            c.CardId,
            c.UpgradeLevel,
            c.DeckCardHandle,
            c.CloneOfHandle,
            c.IsDupe,
            c.ExhaustOnNextPlay,
            c.BaseReplayCount,
            c.CapturedEnergyXValue,
            c.FloorAddedToDeck,
            c.CanonicalBaseEnergyCost,
            c.BaseEnergyCost,
            c.CostsXEnergy,
            mods,
            c.TemporaryStarCostOverrideThisTurn,
            c.TemporaryFreeThisTurn,
            c.TemporaryFreeUntilPlayed,
            c.TemporaryFreeThisCombat,
            c.TemporaryCostOverrideThisTurn,
            c.TemporaryCostOverrideThisTurnOrUntilPlayed,
            c.TemporaryCostOverrideThisCombat,
            c.TemporaryRetainThisTurn,
            c.TemporarySlyThisTurn,
            MapKeywords(c.LocalKeywords, $"{path}.localKeywords"),
            MapKeywords(c.KeywordsAddedFromOutside, $"{path}.keywordsAddedFromOutside"),
            MapKeywords(c.KeywordsRemovedFromOutside, $"{path}.keywordsRemovedFromOutside"),
            enchSpec,
            affSpec,
            MapFields(c.PrivateFields, $"{path}.privateFields"));
    }

    private static CardKeyword[] MapKeywords(IReadOnlyList<string>? names, string path)
    {
        if (names is null || names.Count == 0)
            return [];
        var result = new CardKeyword[names.Count];
        for (int i = 0; i < names.Count; i++)
            result[i] = ParseEnum<CardKeyword>(names[i], $"{path}[{i}]");
        return result;
    }

    private static LiveRelicSnapshotSpec MapRelic(InCombatRelicDto r, string path) =>
        new(
            r.RelicId,
            r.IsWax,
            r.IsMelted,
            r.StackCount,
            r.FloorAddedToDeck,
            MapFields(r.PrivateFields, $"{path}.privateFields"));

    private static LiveCreatureSnapshotSpec MapCreature(InCombatCreatureDto cr, string path)
    {
        CombatSide side = ParseEnum<CombatSide>(cr.Side, $"{path}.side");
        LiveMonsterSnapshotSpec? monster = null;
        if (cr.Monster is { } m)
        {
            if (!cr.IsPlayer && !cr.IsDetached && cr.CurrentHp > 0 && m.IsPerformingMove)
            {
                throw new ForecastCapabilityMissException(
                    "live_combat_precondition",
                    $"{path}.monster.isPerformingMove",
                    $"Live combat bridge requires a quiescent player turn, but alive monster '{cr.MonsterId}' is performing a move.");
            }
            monster = new LiveMonsterSnapshotSpec(
                ToSerializableRng(m.Rng, $"{path}.monster.rng"),
                m.IsPerformingMove,
                m.SpawnedThisTurn,
                m.CurrentStateId,
                m.NextMoveId,
                m.NextMoveCanTransitionAway,
                m.PerformedFirstMove,
                m.StateLogIds ?? [],
                m.TransientMoveFollowUpStateId,
                MapFields(m.SetupFields, $"{path}.monster.setupFields"),
                MapFields(m.PrivateFields, $"{path}.monster.privateFields"));
        }

        var powers = new List<LivePowerSnapshotSpec>(cr.Powers?.Count ?? 0);
        if (cr.Powers is not null)
        {
            for (int i = 0; i < cr.Powers.Count; i++)
            {
                InCombatPowerDto p = cr.Powers[i];
                powers.Add(new LivePowerSnapshotSpec(
                    p.PowerId,
                    p.Amount,
                    p.AmountOnTurnStart,
                    p.SkipNextDurationTick,
                    p.ApplierCreatureHandle,
                    MapFields(p.PrivateFields, $"{path}.powers[{i}].privateFields")));
            }
        }

        return new LiveCreatureSnapshotSpec(
            cr.Handle,
            cr.CombatId,
            side,
            cr.SlotName,
            cr.IsPlayer,
            cr.PetOwnerPlayerIndex,
            cr.MonsterId,
            cr.CurrentHp,
            cr.MaxHp,
            cr.Block,
            cr.CumulativeHpLost,
            cr.IsDetached,
            monster,
            powers);
    }

    private static LiveDamageHistoryEntrySpec MapDamageEntry(InCombatDamageHistoryEntryDto d, string path) =>
        new(
            d.ReceiverCreatureHandle,
            d.DealerCreatureHandle,
            d.OriginalTargetCreatureHandle,
            (ValueProp)d.Props,
            d.IntendedHpLoss,
            d.BlockedDamage,
            d.UnblockedDamage,
            d.OverkillDamage,
            d.WasTargetKilled,
            d.WasBlockBroken,
            d.WasFullyBlocked,
            d.CardSourceHandle,
            d.RoundNumber,
            ParseEnum<CombatSide>(d.CurrentSide, $"{path}.currentSide"),
            d.PlayerTurnNumbers ?? []);

    private static LiveSemanticHistorySpec MapSemanticHistory(InCombatSemanticHistoryDto s) =>
        new(
            (s.Exhausts ?? []).Select((x, i) => new LiveExhaustHistorySpec(
                MapTurnKey(x.Turn, $"liveState.semanticHistory.exhausts[{i}].turn"),
                x.ActorIndex)).ToArray(),
            (s.CardBlockGains ?? []).Select((x, i) => new LiveBlockGainHistorySpec(
                MapTurnKey(x.Turn, $"liveState.semanticHistory.cardBlockGains[{i}].turn"),
                x.ActorIndex,
                x.PlayOrdinal)).ToArray(),
            (s.OrbChannels ?? []).Select((x, i) => new LiveOrbChannelHistorySpec(
                MapTurnKey(x.Turn, $"liveState.semanticHistory.orbChannels[{i}].turn"),
                x.ActorIndex,
                x.OrbId)).ToArray(),
            (s.EnergySpent ?? []).Select((x, i) => new LiveEnergySpentHistorySpec(
                MapTurnKey(x.Turn, $"liveState.semanticHistory.energySpent[{i}].turn"),
                x.ActorIndex,
                x.Amount)).ToArray(),
            (s.CardDrawn ?? []).Select((x, i) => new LiveCardDrawnHistorySpec(
                MapTurnKey(x.Turn, $"liveState.semanticHistory.cardDrawn[{i}].turn"),
                x.ActorIndex,
                ParseEnum<CardType>(x.CardType, $"liveState.semanticHistory.cardDrawn[{i}].cardType"),
                x.FromHandDraw)).ToArray(),
            (s.ActorEvents ?? []).Select((x, i) => new LiveActorEventHistorySpec(
                ParseEnum<CombatSemanticHistory.ActorEvent>(x.Kind, $"liveState.semanticHistory.actorEvents[{i}].kind"),
                MapTurnKey(x.Turn, $"liveState.semanticHistory.actorEvents[{i}].turn"),
                x.ActorIndex)).ToArray());

    private static LiveTurnKeySpec MapTurnKey(InCombatTurnKeyDto? tk, string path)
    {
        if (tk is null)
            throw new BridgeRequestFailedException(path, "Missing TurnKey.");
        return new LiveTurnKeySpec(
            tk.RoundNumber,
            ParseEnum<CombatSide>(tk.Side, $"{path}.side"),
            tk.PlayerTurnNumbers ?? []);
    }

    private static LiveCombatVerificationSpec MapVerification(InCombatVerificationDto v)
    {
        var arrows = new ArrowStatus[v.ArrowStatuses?.Count ?? 0];
        if (v.ArrowStatuses is not null)
        {
            for (int i = 0; i < v.ArrowStatuses.Count; i++)
                arrows[i] = ParseEnum<ArrowStatus>(v.ArrowStatuses[i], $"liveState.verification.arrowStatuses[{i}]");
        }

        return new LiveCombatVerificationSpec(
            v.PlayerHp,
            v.PlayerMaxHp,
            v.PlayerBlock,
            v.PlayerEnergy,
            v.PlayerStars,
            v.PlayerGold,
            v.OrbCapacity,
            v.OrbIds ?? [],
            arrows,
            v.PotionIds ?? [],
            MapVerifiedCards(v.Hand, "hand"),
            MapVerifiedCards(v.DrawPile, "drawPile"),
            MapVerifiedCards(v.DiscardPile, "discardPile"),
            MapVerifiedCards(v.ExhaustPile, "exhaustPile"),
            (v.Enemies ?? []).Select(e => new LiveVerifiedCreatureSpec(
                e.CombatId,
                e.MonsterId,
                e.CurrentHp,
                e.MaxHp,
                e.Block,
                e.NextMoveId,
                (e.Powers ?? []).Select(p => (p.PowerId, p.Amount)).ToArray())).ToArray(),
            (v.PlayerPowers ?? []).Select(p => (p.PowerId, p.Amount)).ToArray());

        LiveVerifiedCardSpec[] MapVerifiedCards(IReadOnlyList<InCombatVerifiedCardDto>? list, string name) =>
            (list ?? []).Select((c, i) => new LiveVerifiedCardSpec(
                c.CardId,
                c.UpgradeLevel,
                c.ResolvedEnergyCost,
                MapKeywords(c.LocalKeywords, $"liveState.verification.{name}[{i}].localKeywords"))).ToArray();
    }

    private static Dictionary<string, LiveFieldValue> MapFields(
        IReadOnlyDictionary<string, InCombatFieldValueDto>? raw,
        string path)
    {
        var result = new Dictionary<string, LiveFieldValue>(StringComparer.Ordinal);
        if (raw is null || raw.Count == 0)
            return result;

        foreach ((string key, InCombatFieldValueDto val) in raw)
        {
            string entryPath = $"{path}.{key}";
            LiveFieldKind kind = ParseEnum<LiveFieldKind>(val.Kind, $"{entryPath}.kind");
            result[key] = new LiveFieldValue(
                kind,
                val.IntValue,
                val.DecimalValue,
                val.BoolValue,
                val.StringValue,
                val.IntArrayValue,
                val.CardIntMapValue?.Select(e => new LiveCardIntEntry(e.CardHandle, e.Value)).ToArray(),
                val.CreatureDecimalMapValue?.Select(e => new LiveCreatureDecimalEntry(e.CreatureHandle, e.Value)).ToArray(),
                val.CardPairArrayValue?.Select(e => new LiveCardPairEntry(e.OriginalCardHandle, e.CloneCardHandle)).ToArray());
        }

        return result;
    }

    private static TEnum ParseEnum<TEnum>(string? raw, string path) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw) || !Enum.TryParse(raw, ignoreCase: false, out TEnum parsed) || !Enum.IsDefined(parsed))
            throw new BridgeRequestFailedException(path, $"Unknown {typeof(TEnum).Name} value '{raw}'.");
        return parsed;
    }
}
