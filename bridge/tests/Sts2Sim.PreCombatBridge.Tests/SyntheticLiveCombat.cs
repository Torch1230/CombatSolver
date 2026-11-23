using CombatSolver.Beam;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Rngs;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Models.Orbs;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;

namespace Sts2Sim.PreCombatBridge.Tests;

// Builds wire inputs for an unmodified opening or a single Strike followed by EndTurn.
// Private wire values describe those scripted actions independently of Core's storage.
internal static class SyntheticLiveCombat
{
    internal static async Task<(string SaveJson, InCombatLiveStateDto LiveDto, CombatRoom Room)> BuildTurn1CombatAsync(
        string encounterId,
        bool withHappyFlower = false)
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(characterId: "CHARACTER.IRONCLAD", ascension: 0);
        Player player = run.Players[0];
        if (withHappyFlower)
        {
            RelicModel happyFlower = (RelicModel)ModelDb.Relic<HappyFlower>().MutableClone();
            happyFlower.AssignOwner(player);
            player.AddRelicInternal(happyFlower);
        }

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        string saveJson = TestSaveBuilder.SerializeNormalizedSave(run);
        Assert.True(EncounterCatalog.TryGet(encounterId, out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(
                entry.Definition,
                entry.RoomType,
                target.coord.row + 1,
                target.coord.col,
                target.PointType,
                false),
            new ForecastOpeningRunner(CancellationToken.None),
            encounterMonsterSeedOverride: null);

        return (saveJson, CreateSnapshot(entered.Room, encounterId), entered.Room);
    }

    internal static async Task<(string SaveJson, InCombatLiveStateDto LiveDto, CombatRoom Room)> BuildTurn2CombatAsync(
        string encounterId)
    {
        (string saveJson, InCombatLiveStateDto opening, CombatRoom room) =
            await BuildTurn1CombatAsync(encounterId, withHappyFlower: true);
        Player player = room.Engine.State.Players[0];
        CardModel strike = player.PlayerCombatState!.Hand.Cards.First(c => c.CanPlay(out _) && c.TargetType == TargetType.AnyEnemy);
        int cost = strike.EnergyCost;
        Assert.NotNull(await room.Engine.PlayCardWithResultAsync(player, strike, room.Engine.State.HittableEnemies[0]));
        await room.Engine.EndPlayerTurnAsync();

        InCombatLiveStateDto turn2 = CreateSnapshot(room, encounterId);
        var firstTurn = new InCombatTurnKeyDto(1, "Player", [1]);
        return (saveJson, turn2 with
        {
            SemanticHistory = turn2.SemanticHistory with
            {
                CardDrawn = [.. opening.SemanticHistory.CardDrawn, .. turn2.SemanticHistory.CardDrawn],
                EnergySpent = [new(firstTurn, 0, cost)],
            },
        }, room);
    }

    internal static InCombatLiveStateDto CreateSnapshot(CombatRoom room, string encounterId)
    {
        CombatState state = room.Engine.State;
        Player player = state.Players[0];
        PlayerCombatState pcs = player.PlayerCombatState!;

        var creatureHandles = new Dictionary<Creature, int>(ReferenceEqualityComparer.Instance);
        var cardHandles = new Dictionary<CardModel, int>(ReferenceEqualityComparer.Instance);

        int RegisterCreature(Creature c)
        {
            if (!creatureHandles.TryGetValue(c, out int handle))
            {
                handle = creatureHandles.Count + 1;
                creatureHandles.Add(c, handle);
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
                if (card.CloneOf is { } co)
                    RegisterCard(co);
            }
            return handle;
        }

        RegisterCreature(player.Creature);
        foreach (Creature c in state.Allies) RegisterCreature(c);
        foreach (Creature c in state.Enemies) RegisterCreature(c);
        foreach (Creature c in state.EscapedCreatures) RegisterCreature(c);
        foreach (Creature c in state.SpawnedEnemies) RegisterCreature(c);
        foreach (Creature c in pcs.Pets) RegisterCreature(c);

        foreach (CardModel c in player.Deck.Cards) RegisterCard(c);
        foreach (CardPile pile in pcs.AllPiles)
        {
            foreach (CardModel c in pile.Cards) RegisterCard(c);
        }
        foreach (CombatDamageHistoryEntry dmg in state.DamageHistory.Entries)
        {
            RegisterCreature(dmg.Receiver);
            RegisterCreature(dmg.Result.OriginalTarget);
            if (dmg.Dealer is not null) RegisterCreature(dmg.Dealer);
            if (dmg.CardSource is not null) RegisterCard(dmg.CardSource);
        }

        var cardsDto = cardHandles
            .OrderBy(kv => kv.Value)
            .Select(kv => ExportCard(kv.Key, kv.Value, cardHandles))
            .ToArray();

        var relicsDto = player.Relics
            .Select(r => ExportRelic(r, pcs.TurnNumber))
            .ToArray();

        var creaturesDto = creatureHandles
            .OrderBy(kv => kv.Value)
            .Select(kv => ExportCreature(kv.Key, kv.Value, state, creatureHandles))
            .ToArray();

        // The fixture has a contiguous opening roster and has never removed a creature.
        uint nextCreatureId = checked(creatureHandles.Keys.Max(c => c.CombatId!.Value) + 1);

        SerializableRunRngSet runRng = state.RunState.Rng.ToSerializable();
        var runRngDtos = runRng.Rngs.ToDictionary(
            kv => kv.Key.ToString(),
            kv => ToRngDto(kv.Value),
            StringComparer.Ordinal);
        SerializablePlayerRngSet playerRng = player.PlayerRng.ToSerializable();
        var playerRngDtos = playerRng.Rngs.ToDictionary(
            kv => kv.Key.ToString(),
            kv => ToRngDto(kv.Value),
            StringComparer.Ordinal);

        var countersDto = new InCombatPlayerCountersDto(
            pcs.StarsGainedThisTurn,
            pcs.SkillCardsPlayedThisTurn,
            pcs.AttackCardsPlayedThisTurn,
            pcs.ShivsPlayedThisTurn,
            pcs.CardsDiscardedThisTurn,
            pcs.CardsPlayedThisCombat,
            pcs.CardsGeneratedThisCombat,
            pcs.SadnessGeneratedThisCombat,
            pcs.CardsDrawnThisCombat,
            pcs.CardsPlayedThisTurn,
            pcs.ManualCardsPlayedThisTurn,
            pcs.CardPlaysStartedThisTurn,
            pcs.AttackOrSkillCardPlaysStartedThisTurn,
            pcs.AttackCardsStartedThisTurn,
            pcs.ZeroCostAttacksStartedThisTurn,
            pcs.FirstInSeriesCardPlaysStartedThisTurn,
            pcs.WahStartedThisTurn,
            pcs.ChoStartedThisTurn,
            pcs.KiStartedThisTurn,
            pcs.FirstWahPlayOrdinalThisTurn,
            pcs.FirstChoPlayOrdinalThisTurn,
            pcs.FirstKiPlayOrdinalThisTurn,
            pcs.WahFinishedThisTurn,
            pcs.ChoFinishedThisTurn,
            pcs.KiFinishedThisTurn,
            pcs.MelodyCardsFinishedThisTurn);

        var orbsDto = pcs.OrbQueue.Orbs
            .Select(o => new InCombatOrbDto(
                o.Id.ToString(),
                o is DarkOrb dark ? dark.EvokeVal : null,
                o is GlassOrb ? 4m : null))
            .ToArray();

        var arrowsDto = pcs.Arrows
            .Select(a => new InCombatArrowSlotDto(a.Status.ToString(), a.DestroyAfterImpaled))
            .ToArray();

        var playerDto = new InCombatPlayerDto(
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.Creature.Block,
            player.Creature.CumulativeHpLost,
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
            player.PotionSlots.Select(p => p?.Id.ToString()).ToArray(),
            pcs.OrbQueue.Capacity,
            orbsDto,
            arrowsDto);

        var damageDtos = state.DamageHistory.Entries
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
                [e.RoundNumber]))
            .ToArray();

        var turnKey = new InCombatTurnKeyDto(state.RoundNumber, "Player", [pcs.TurnNumber]);
        var semanticDto = new InCombatSemanticHistoryDto([], [],
            orbsDto.Select(o => new InCombatOrbChannelHistoryDto(turnKey, 0, o.OrbId)).ToArray(),
            [], pcs.Hand.Cards.Take(5).Select(c => new InCombatCardDrawnHistoryDto(
                turnKey, 0, c.Type.ToString(), FromHandDraw: true)).ToArray(), []);

        var verificationDto = new InCombatVerificationDto(
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.Creature.Block,
            pcs.Energy,
            pcs.Stars,
            player.Gold,
            pcs.OrbQueue.Capacity,
            pcs.OrbQueue.Orbs.Select(o => o.Id.ToString()).ToArray(),
            pcs.Arrows.Select(a => a.Status.ToString()).ToArray(),
            player.PotionSlots.Select(p => p?.Id.ToString()).ToArray(),
            pcs.Hand.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.DrawPile.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.DiscardPile.Cards.Select(ToVerifiedCard).ToArray(),
            pcs.ExhaustPile.Cards.Select(ToVerifiedCard).ToArray(),
            state.Enemies.Select(e => new InCombatVerifiedCreatureDto(
                e.CombatId,
                e.Monster?.Id.ToString(),
                e.CurrentHp,
                e.MaxHp,
                e.Block,
                e.Monster?.NextMove?.Id,
                e.Powers.Select(pw => new InCombatVerifiedPowerDto(pw.Id.ToString(), pw.Amount)).ToArray())).ToArray(),
            player.Creature.Powers.Select(pw => new InCombatVerifiedPowerDto(pw.Id.ToString(), pw.Amount)).ToArray());

        return new InCombatLiveStateDto(
            encounterId,
            room.RoomType.ToString(),
            room.Encounter?.Slots ?? [],
            state.CurrentSide.ToString(),
            state.RoundNumber,
            state.IsPlayerExtraTurn,
            state.GoldWasStolen,
            nextCreatureId,
            runRng.Seed,
            runRngDtos,
            playerRng.Seed,
            playerRngDtos,
            playerDto,
            cardsDto,
            relicsDto,
            creaturesDto,
            state.Allies.Select(c => creatureHandles[c]).ToArray(),
            state.Enemies.Select(c => creatureHandles[c]).ToArray(),
            state.EscapedCreatures.Select(c => creatureHandles[c]).ToArray(),
            [],
            state.SpawnedEnemies.Select(c => creatureHandles[c]).ToArray(),
            pcs.Pets.Select(c => creatureHandles[c]).ToArray(),
            damageDtos,
            semanticDto,
            verificationDto);

        static InCombatVerifiedCardDto ToVerifiedCard(CardModel c) =>
            new(
                c.Id.ToString(),
                c.CurrentUpgradeLevel,
                c.CostsXEnergy || c.CanonicalEnergyCostValue < 0 ? 0 : Math.Max(0, c.EnergyCost),
                c.GetKeywordsWithSources(KeywordSources.Local).Select(k => k.ToString()).ToArray());
    }

    private static InCombatCardDto ExportCard(
        CardModel c,
        int handle,
        IReadOnlyDictionary<CardModel, int> cardHandles)
    {
        EnchantmentModel? ench = c.Enchantments.SingleOrDefault();
        InCombatEnchantmentDto? enchDto = ench is null
            ? null
            : new InCombatEnchantmentDto(
                ench.Id.ToString(),
                (int)ench.Magnitude,
                ench.Status.ToString(),
                ench is Sts2Sim.Core.Models.Enchantments.Momentum
                    ? new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal)
                    {
                        ["Momentum._extraDamage"] = new("Int", IntValue: 0),
                    }
                    : new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal));

        InCombatAfflictionDto? affDto = c.Affliction is { } aff
            ? new InCombatAfflictionDto(aff.Id.ToString(), aff.Amount)
            : null;

        var priv = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal);
        if (c is GeneticAlgorithm ga)
        {
            priv["GeneticAlgorithm._currentBlock"] = new("Int", IntValue: ga.CurrentBlock);
            priv["GeneticAlgorithm._increasedBlock"] = new("Int", IntValue: ga.IncreasedBlock);
            priv["GeneticAlgorithm._increase"] = new("Int", IntValue: ga.IsUpgraded ? 4 : 3);
        }
        else if (c is TheScythe scythe)
        {
            priv["TheScythe._increasedDamage"] = new("Int", IntValue: scythe.IncreasedDamage);
        }

        return new InCombatCardDto(
            handle,
            c.Id.ToString(),
            c.CurrentUpgradeLevel,
            c.DeckVersion is not null ? cardHandles[c.DeckVersion] : null,
            c.CloneOf is not null ? cardHandles[c.CloneOf] : null,
            c.IsDupe,
            c.ExhaustOnNextPlay,
            c.BaseReplayCount,
            0,
            c.FloorAddedToDeck,
            c.CanonicalEnergyCostValue,
            c.CanonicalEnergyCostValue,
            c.CostsXEnergy,
            [],
            c.TemporaryStarCostOverrideThisTurn,
            c.TemporaryFreeThisTurn,
            c.TemporaryFreeUntilPlayed,
            c.TemporaryFreeThisCombat,
            c.TemporaryCostOverrideThisTurn,
            c.TemporaryCostOverrideThisTurnOrUntilPlayed,
            c.TemporaryCostOverrideThisCombat,
            c.TemporaryRetainThisTurn,
            c.TemporarySlyThisTurn,
            c.GetKeywordsWithSources(KeywordSources.Local).Select(k => k.ToString()).ToArray(),
            [],
            [],
            enchDto,
            affDto,
            priv);
    }

    private static InCombatRelicDto ExportRelic(RelicModel r, int turnNumber)
    {
        var priv = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal);
        if (r is HappyFlower)
        {
            priv["HappyFlower._turnCounter"] = new("Int", IntValue: turnNumber);
        }
        return new InCombatRelicDto(
            r.Id.ToString(),
            r.IsWax,
            r.IsMelted,
            r.StackCount,
            r.FloorAddedToDeck,
            priv);
    }

    private static InCombatCreatureDto ExportCreature(
        Creature c,
        int handle,
        CombatState state,
        IReadOnlyDictionary<Creature, int> creatureHandles)
    {
        int? petOwnerIdx = null;
        if (c.PetOwner is not null)
        {
            for (int i = 0; i < state.Players.Count; i++)
            {
                if (ReferenceEquals(state.Players[i], c.PetOwner))
                {
                    petOwnerIdx = i;
                    break;
                }
            }
        }

        InCombatMonsterDto? monsterDto = null;
        if (c.Monster is { } m)
        {
            MonsterMoveStateMachine sm = m.MoveStateMachine!;
            var setupFields = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal);
            if (m is Nibbit nib)
            {
                setupFields["Nibbit._isFront"] = new("Bool", BoolValue: nib.IsFront);
                setupFields["Nibbit._isAlone"] = new("Bool", BoolValue: nib.IsAlone);
            }
            var privFields = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal);

            SerializableRng serializedMonsterRng = m.Rng.ToSerializable();

            monsterDto = new InCombatMonsterDto(
                ToRngDto(serializedMonsterRng),
                m.IsPerformingMove,
                m.SpawnedThisTurn,
                m.NextMove?.Id ?? sm.States.Keys.Single(),
                m.NextMove?.Id,
                m.NextMove?.CanTransitionAway ?? true,
                state.RoundNumber == 2,
                sm.StateLog.Select(s => s.Id).ToArray(),
                null,
                setupFields,
                privFields);
        }

        var powers = c.Powers.Select(p =>
        {
            var pPriv = new Dictionary<string, InCombatFieldValueDto>(StringComparer.Ordinal);
            return new InCombatPowerDto(
                p.Id.ToString(),
                p.Amount,
                p.AmountOnTurnStart,
                p.SkipNextDurationTick,
                p.Applier is not null ? creatureHandles[p.Applier] : null,
                pPriv);
        }).ToArray();

        return new InCombatCreatureDto(
            handle,
            c.CombatId,
            c.Side.ToString(),
            c.SlotName,
            c.IsPlayer,
            petOwnerIdx,
            c.Monster?.Id.ToString(),
            c.CurrentHp,
            c.MaxHp,
            c.Block,
            c.CumulativeHpLost,
            IsDetached: c.CombatState is null,
            monsterDto,
            powers);
    }

    private static InCombatRngStreamDto ToRngDto(SerializableRng r) =>
        new(r.counter, r.state0, r.state1, r.state2, r.state3);
}
