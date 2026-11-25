using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal static partial class InCombatLiveStateCapturer
{
    private static RustKernelFieldValueDto IntVal(int value) => new("Int", IntValue: value);
    private static RustKernelFieldValueDto NullableIntVal(int? value) => new("NullableInt", IntValue: value);
    private static RustKernelFieldValueDto DecimalVal(decimal value) => new("Decimal", DecimalValue: value);
    private static RustKernelFieldValueDto BoolVal(bool value) => new("Bool", BoolValue: value);
    private static RustKernelFieldValueDto StringVal(string? value) => new("String", StringValue: value);
    private static RustKernelFieldValueDto CardHandleVal(int? handle) => new("CardHandle", IntValue: handle);
    private static RustKernelFieldValueDto CreatureHandleVal(int? handle) => new("CreatureHandle", IntValue: handle);
    private static RustKernelFieldValueDto IntArrayVal(IReadOnlyList<int> items) => new("IntArray", IntArrayValue: items);
    private static RustKernelFieldValueDto CardHandleArrayVal(IReadOnlyList<int> items) => new("CardHandleArray", IntArrayValue: items);
    private static RustKernelFieldValueDto CreatureHandleArrayVal(IReadOnlyList<int> items) => new("CreatureHandleArray", IntArrayValue: items);

    private static void RegisterPrivateReferenceTargets(
        CombatState combatState,
        Player player,
        Dictionary<Creature, int> creatureHandles,
        CardPlayFinishedEntry[] finishedEntries,
        Func<Creature, int> registerCreature,
        Func<CardModel, int> registerCard)
    {
        foreach (RelicModel relic in player.Relics)
        {
            if (relic is HistoryCourse)
            {
                (CardModel? lastAttack, _, CardModel? prevAttack, _) = FindHistoryCourseAttacks(
                    combatState, player, finishedEntries);
                if (lastAttack is not null) registerCard(lastAttack);
                if (prevAttack is not null) registerCard(prevAttack);
            }
            else if (relic is MusicBox mb && mb._cardBeingPlayed is { } mbCard)
            {
                registerCard(mbCard);
            }
            else if (relic is UnsettlingLamp ul && ul._triggeringCard is { } ulCard)
            {
                registerCard(ulCard);
            }
            else if (relic is Vambrace vam && vam._triggeringCard is { } vamCard)
            {
                registerCard(vamCard);
            }
        }

        var scanned = new HashSet<Creature>(ReferenceEqualityComparer.Instance);
        while (true)
        {
            Creature[] pending = creatureHandles.Keys
                .Where(c => scanned.Add(c))
                .ToArray();
            if (pending.Length == 0)
                break;

            foreach (Creature creature in pending)
            {
                if (creature.Monster is Queen queen && queen._amalgam is { } amalgam)
                    registerCreature(amalgam);

                foreach (PowerModel power in creature.Powers)
                {
                    if (power.Applier is not null)
                        registerCreature(power.Applier);
                    switch (power)
                    {
                        case CurlUpPower curl when curl.GetInternalData<CurlUpPower.Data>().playedCard is { } curlCard:
                            registerCard(curlCard);
                            break;
                        case DampenPower dampen:
                            foreach (Creature caster in dampen.GetInternalData<DampenPower.Data>().casters)
                                registerCreature(caster);
                            foreach (CardModel c in dampen.GetInternalData<DampenPower.Data>().downgradedCardsToOldUpgradeLevels.Keys)
                                registerCard(c);
                            break;
                        case HeistPower heist when heist.Target is { } hTarget:
                            registerCreature(hTarget);
                            break;
                        case ImitationLearningPower:
                            break;
                        case JugglingPower jugg when jugg.GetInternalData<JugglingPower.Data>().cardToClone is { } jCard:
                            registerCard(jCard);
                            break;
                        case NightmarePower nm when nm.GetInternalData<NightmarePower.Data>().selectedCard is { } nmCard:
                            registerCard(nmCard);
                            break;
                        case OblivionPower ob:
                            foreach (CardModel c in ob.GetInternalData<OblivionPower.Data>().amountsForPlayedCards.Keys)
                                registerCard(c);
                            break;
                        case PossessSpeedPower ps:
                            foreach (Creature target in ps.GetInternalData<PossessSpeedPower.Data>().stolenDexterity.Keys)
                                registerCreature(target);
                            break;
                        case PossessStrengthPower pstr:
                            foreach (Creature target in pstr.GetInternalData<PossessStrengthPower.Data>().stolenStrength.Keys)
                                registerCreature(target);
                            break;
                        case RupturePower rup:
                            foreach (CardModel c in rup.GetInternalData<RupturePower.Data>().playedCards.Keys)
                                registerCard(c);
                            break;
                        case SandpitPower sp when sp.Target is { } sTarget:
                            registerCreature(sTarget);
                            break;
                        case ShowOffPower so when so.ShowOffInstance is { } soCard:
                            registerCard(soCard);
                            break;
                        case SwipePower sw when sw.StolenCard is { } swCard:
                            registerCard(swCard);
                            break;
                        case SwordSagePower:
                            foreach (CardModel card in player.PlayerCombatState?.AllCards ?? [])
                            {
                                if (card is SovereignBlade)
                                    registerCard(card);
                            }
                            break;
                        case ThieveryPower th when th.Target is { } thTarget:
                            registerCreature(thTarget);
                            break;
                        case WitheringPresencePower wp when wp.Target is { } wpTarget:
                            registerCreature(wpTarget);
                            break;
                    }
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, RustKernelFieldValueDto> CaptureEnchantmentPrivateFields(
        EnchantmentModel enchantment)
    {
        var fields = new Dictionary<string, RustKernelFieldValueDto>(StringComparer.Ordinal);
        if (enchantment is Momentum)
        {
            fields["Momentum._extraDamage"] = IntVal(
                (int)(typeof(Momentum).GetField("_extraDamage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(enchantment)
                ?? throw new MissingFieldException(typeof(Momentum).FullName, "_extraDamage")));
        }
        return fields;
    }

    private static IReadOnlyDictionary<CardModel, int> ComputeTheHuntPendingRewards(
        Player player,
        CombatHistoryEntry[] allHistory)
    {
        var rewardsByCard = new Dictionary<CardModel, int>(ReferenceEqualityComparer.Instance);
        var activePlays = new List<CardPlay>();
        foreach (CombatHistoryEntry entry in allHistory)
        {
            switch (entry)
            {
                case CardPlayStartedEntry started:
                    activePlays.Add(started.CardPlay);
                    break;

                case CardPlayFinishedEntry finished:
                {
                    int removeIdx = -1;
                    for (int i = activePlays.Count - 1; i >= 0; i--)
                    {
                        if (ReferenceEquals(activePlays[i], finished.CardPlay))
                        {
                            removeIdx = i;
                            break;
                        }
                    }
                    if (removeIdx < 0)
                    {
                        throw new InCombatLiveCaptureUnsupportedException(
                            "live_private_state_unmapped",
                            "cards.TheHunt._pendingCardRewards",
                            "Mismatched CardPlayFinishedEntry while attributing TheHuntPower.");
                    }
                    activePlays.RemoveAt(removeIdx);
                    break;
                }

                case PowerReceivedEntry received when received.Power is TheHuntPower
                    && ReferenceEquals(received.Actor, player.Creature):
                {
                    if (received.Amount <= 0m || received.Amount != decimal.Truncate(received.Amount))
                    {
                        throw new InCombatLiveCaptureUnsupportedException(
                            "live_private_state_unmapped",
                            "cards.TheHunt._pendingCardRewards",
                            $"Non-integral TheHuntPower delta {received.Amount}.");
                    }
                    var distinctHunts = new List<CardModel>(1);
                    foreach (CardPlay play in activePlays)
                    {
                        if (ReferenceEquals(play.Player, player)
                            && play.Card is TheHunt huntCard
                            && !distinctHunts.Any(existing => ReferenceEquals(existing, huntCard)))
                        {
                            distinctHunts.Add(huntCard);
                        }
                    }
                    if (distinctHunts.Count != 1)
                    {
                        throw new InCombatLiveCaptureUnsupportedException(
                            "live_private_state_unmapped",
                            "cards.TheHunt._pendingCardRewards",
                            $"Ambiguous active TheHunt card attribution count ({distinctHunts.Count}).");
                    }
                    CardModel sourceHunt = distinctHunts[0];
                    rewardsByCard[sourceHunt] = rewardsByCard.GetValueOrDefault(sourceHunt) + (int)received.Amount;
                    break;
                }
            }
        }

        int totalTheHuntPower = player.Creature.GetPower<TheHuntPower>()?.Amount ?? 0;
        int attributedSum = rewardsByCard.Values.Sum();
        if (attributedSum != totalTheHuntPower)
        {
            throw new InCombatLiveCaptureUnsupportedException(
                "live_private_state_unmapped",
                "cards.TheHunt._pendingCardRewards",
                $"Attributed TheHunt rewards ({attributedSum}) disagree with live TheHuntPower ({totalTheHuntPower}).");
        }
        return rewardsByCard;
    }

    internal static IReadOnlyDictionary<string, RustKernelFieldValueDto> CaptureCardPrivateFields(
        CardModel card,
        CombatState state,
        IReadOnlyDictionary<CardModel, int> cardHandles,
        CardPlayFinishedEntry[] finishedEntries,
        IReadOnlyDictionary<CardModel, int> theHuntPendingRewards)
    {
        var fields = new Dictionary<string, RustKernelFieldValueDto>(StringComparer.Ordinal);
        switch (card)
        {
            case Abundance:
                fields["Abundance._generatedCandidates"] = CardHandleArrayVal([]);
                break;
            case Bolas:
                fields["Bolas._playedOnTurnNumber"] = NullableIntVal(FindLastFinishedTurnNumber(card, finishedEntries));
                break;
            case Bombardment bomb:
                bool hasReplayed = bomb.Pile?.Type == PileType.Exhaust
                    && finishedEntries.Any(e => ReferenceEquals(e.CardPlay.Card, bomb) && e.CardPlay.IsAutoPlay);
                fields["Bombardment._hasReplayed"] = BoolVal(hasReplayed);
                break;
            case Bounce:
                fields["Bounce._lastFinishedPlayerTurn"] = IntVal(FindLastFinishedTurnNumber(card, finishedEntries) ?? -1);
                break;
            case Claw claw:
                decimal clawIncrease = claw.DynamicVars["Increase"].BaseValue;
                fields["Claw._damage"] = DecimalVal(claw.DynamicVars.Damage.BaseValue);
                fields["Claw._increase"] = DecimalVal(clawIncrease);
                break;
            case Disintegration:
                fields["Disintegration.<PowerAmount>k__BackingField"] = IntVal(card.DynamicVars["DisintegrationPower"].IntValue);
                break;
            case Fetch:
                CardPlayFinishedEntry? fetchEntry = finishedEntries.LastOrDefault(e =>
                    ReferenceEquals(e.CardPlay.Card, card) && e.HappenedThisTurn(state));
                if (fetchEntry is not null)
                {
                    fields["Fetch._finishedRound"] = IntVal(fetchEntry.RoundNumber);
                    fields["Fetch._finishedSide"] = IntVal((int)fetchEntry.CurrentSide);
                    int[] turnNums = state.RunState.Players
                        .Select(p => fetchEntry._playerTurnNumbers.TryGetValue(p.NetId, out int t) ? t : -1)
                        .ToArray();
                    fields["Fetch._finishedTurnNumbers"] = IntArrayVal(turnNums);
                }
                else
                {
                    fields["Fetch._finishedRound"] = IntVal(-1);
                    fields["Fetch._finishedSide"] = IntVal(0);
                    fields["Fetch._finishedTurnNumbers"] = IntArrayVal([]);
                }
                break;
            case GeneticAlgorithm ga:
                fields["GeneticAlgorithm._currentBlock"] = IntVal(ga.CurrentBlock);
                fields["GeneticAlgorithm._increasedBlock"] = IntVal(ga.IncreasedBlock);
                fields["GeneticAlgorithm._increase"] = IntVal(ga.DynamicVars["Increase"].IntValue);
                break;
            case KinglyPunch kp:
                fields["KinglyPunch._damage"] = DecimalVal(kp.DynamicVars.Damage.BaseValue);
                break;
            case MadScience ms:
                int mappedRider = ms.TinkerTimeRider switch
                {
                    TinkerTime.RiderEffect.None => 0,
                    TinkerTime.RiderEffect.Sapping => 1,
                    TinkerTime.RiderEffect.Violence => 2,
                    TinkerTime.RiderEffect.Choking => 3,
                    TinkerTime.RiderEffect.Energized => 4,
                    TinkerTime.RiderEffect.Wisdom => 5,
                    TinkerTime.RiderEffect.Chaos => 6,
                    TinkerTime.RiderEffect.Expertise => 7,
                    TinkerTime.RiderEffect.Curious => 8,
                    TinkerTime.RiderEffect.Improvement => 9,
                    _ => throw new InCombatLiveCaptureUnsupportedException(
                        "live_private_state_unmapped",
                        "cards.MadScience.Rider",
                        $"Unsupported MadScience TinkerTimeRider '{ms.TinkerTimeRider}'."),
                };
                fields["MadScience._type"] = IntVal((int)ms.TinkerTimeType);
                fields["MadScience.<Rider>k__BackingField"] = IntVal(mappedRider);
                break;
            case Maul maul:
                decimal maulIncrease = maul.DynamicVars["Increase"].BaseValue;
                fields["Maul._damage"] = DecimalVal(maul.DynamicVars.Damage.BaseValue);
                fields["Maul._increase"] = DecimalVal(maulIncrease);
                fields["Maul._extraDamageFromMaulPlays"] = DecimalVal(maul._extraDamageFromMaulPlays);
                break;
            case Rampage rampage:
                fields["Rampage._damage"] = DecimalVal(rampage.DynamicVars.Damage.BaseValue);
                break;
            case ShowOff showOff:
                fields["ShowOff._currentAim"] = IntVal(showOff.CurrentAim);
                fields["ShowOff._increasedAim"] = IntVal(showOff.IncreasedAim);
                break;
            case SovereignBlade sb:
                fields["SovereignBlade._damage"] = DecimalVal(sb.DynamicVars.Damage.BaseValue);
                break;
            case TheBall ball:
                fields["TheBall._damage"] = DecimalVal(ball.DynamicVars.Damage.BaseValue);
                break;
            case TheHunt hunt:
                fields["TheHunt._pendingCardRewards"] = IntVal(theHuntPendingRewards.GetValueOrDefault(hunt, 0));
                break;
            case TheScythe scythe:
                fields["TheScythe._increasedDamage"] = IntVal(scythe.IncreasedDamage);
                break;
            case Thrash thrash:
                fields["Thrash._damage"] = DecimalVal(thrash.DynamicVars.Damage.BaseValue);
                break;
            case ThrummingHatchet:
                fields["ThrummingHatchet._playedOnTurnNumber"] = NullableIntVal(FindLastFinishedTurnNumber(card, finishedEntries));
                break;
            case Whistle whistle:
                fields["Whistle._damage"] = DecimalVal(whistle.DynamicVars.Damage.BaseValue);
                break;
            case Wither wither:
                fields["Wither._fakeUpgradeLevel"] = IntVal(wither._fakeUpgradeLevel);
                break;
        }
        return fields;
    }

    private static int? FindLastFinishedTurnNumber(CardModel card, CardPlayFinishedEntry[] finishedEntries)
    {
        CardPlayFinishedEntry? last = finishedEntries.LastOrDefault(e => ReferenceEquals(e.CardPlay.Card, card));
        if (last is null || card.Owner is null)
            return null;
        return last._playerTurnNumbers.TryGetValue(card.Owner.NetId, out int turnNumber) ? turnNumber : null;
    }

    private static (CardModel? LastAttack, int LastTurn, CardModel? PreviousAttack, int PrevTurn)
        FindHistoryCourseAttacks(
            CombatState state,
            Player player,
            CardPlayFinishedEntry[] finishedEntries)
    {
        int currentTurn = player.PlayerCombatState?.TurnNumber ?? 1;
        CardModel? lastAttack = null;
        int lastAttackTurn = 0;
        CardModel? previousAttack = null;
        int previousAttackTurn = 0;
        foreach (CardPlayFinishedEntry e in finishedEntries)
        {
            if (!ReferenceEquals(e.CardPlay.Player, player) || e.CardPlay.Card.IsDupe || e.CardPlay.Card.Type != CardType.Attack)
                continue;
            if (!e._playerTurnNumbers.TryGetValue(player.NetId, out int playTurn) || playTurn <= 0 || playTurn > currentTurn)
            {
                throw new InCombatLiveCaptureUnsupportedException(
                    "live_private_state_unmapped",
                    "relics.HistoryCourse",
                    $"HistoryCourse attack entry '{e.CardPlay.Card.Id.Entry}' is missing a valid player turn number.");
            }
            if (playTurn < lastAttackTurn)
            {
                throw new InCombatLiveCaptureUnsupportedException(
                    "live_private_state_unmapped",
                    "relics.HistoryCourse",
                    $"HistoryCourse observed non-monotonic attack turn {playTurn} after {lastAttackTurn}.");
            }
            if (lastAttackTurn != playTurn)
            {
                previousAttack = lastAttack;
                previousAttackTurn = lastAttackTurn;
            }
            lastAttack = e.CardPlay.Card;
            lastAttackTurn = playTurn;
        }
        return (lastAttack, lastAttackTurn, previousAttack, previousAttackTurn);
    }

    private static bool ProveToolboxUsedThisCombat(
        Toolbox toolbox,
        Player player,
        CombatHistoryEntry[] allHistory)
    {
        if (toolbox.IsMelted || player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play })
            return false;

        foreach (CombatHistoryEntry entry in allHistory)
        {
            if (!entry._playerTurnNumbers.TryGetValue(player.NetId, out int entryTurn) || entryTurn != 1)
                continue;
            if (entry is CardDrawnEntry drawn
                && drawn.FromHandDraw
                && ReferenceEquals(drawn.Actor, player.Creature))
            {
                return true;
            }
        }
        return false;
    }

    private static RustKernelRelicDto CaptureRelic(
        RelicModel relic,
        CombatState state,
        Player player,
        IReadOnlyDictionary<CardModel, int> cardHandles,
        CardPlayStartedEntry[] startedEntries,
        CardPlayFinishedEntry[] finishedEntries,
        CombatHistoryEntry[] allHistory)
    {
        var fields = new Dictionary<string, RustKernelFieldValueDto>(StringComparer.Ordinal);
        switch (relic)
        {
            case ArtOfWar aow:
                bool anyLastTurn = (bool)(typeof(ArtOfWar).GetField("_anyAttacksPlayedLastTurn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(aow) ?? false);
                bool anyThisTurn = (bool)(typeof(ArtOfWar).GetField("_anyAttacksPlayedThisTurn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(aow) ?? false);
                fields["ArtOfWar._attackPlayedThisTurn"] = BoolVal(anyThisTurn);
                fields["ArtOfWar._previousTurnHadNoAttack"] = BoolVal(!anyLastTurn);
                break;
            case BathRobes br:
                fields["BathRobes._wasUsed"] = BoolVal(br.WasUsed);
                break;
            case BeatingRemnant remnant:
                fields["BeatingRemnant._hpLostThisTurn"] = IntVal((int)remnant._damageReceivedThisTurn);
                break;
            case BeltBuckle buckle:
                bool dexApplied = (bool)(typeof(BeltBuckle).GetField("_dexterityApplied", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(buckle) ?? false);
                fields["BeltBuckle._dexterityApplied"] = BoolVal(dexApplied);
                break;
            case BrilliantScarf bs:
                fields["BrilliantScarf._cardsPlayed"] = IntVal(bs._cardsPlayedThisTurn);
                break;
            case BurningSticks sticks:
                fields["BurningSticks._wasUsedThisCombat"] = BoolVal(sticks.WasUsedThisCombat);
                break;
            case MegaCrit.Sts2.Core.Models.Relics.Byrdpip byrd:
                fields["Byrdpip._swoopsPlayed"] = IntVal(byrd.SwoopsPlayed);
                break;
            case CentennialPuzzle cp:
                fields["CentennialPuzzle._triggeredThisCombat"] = BoolVal(cp.UsedThisCombat);
                break;
            case ConductorsBaton baton:
                fields["ConductorsBaton._triggeredThisTurn"] = BoolVal(baton._triggeredThisTurn);
                break;
            case DemonTongue dt:
                fields["DemonTongue._triggeredThisTurn"] = BoolVal(dt._triggeredThisTurn);
                break;
            case FakeHappyFlower fhf:
                fields["FakeHappyFlower._turnsSeen"] = IntVal(fhf.TurnsSeen);
                fields["FakeHappyFlower._grantEnergyThisTurn"] = BoolVal(
                    (bool)(typeof(FakeHappyFlower)
                        .GetField("_grantEnergyThisTurn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        ?.GetValue(fhf) ?? throw new MissingFieldException(typeof(FakeHappyFlower).FullName, "_grantEnergyThisTurn")));
                break;
            case FakeOrichalcum fo:
                fields["FakeOrichalcum._shouldTrigger"] = BoolVal(fo._shouldTrigger);
                break;
            case FakeVenerableTeaSet fvts:
                fields["FakeVenerableTeaSet._isArmed"] = BoolVal(fvts.GainEnergyInNextCombat);
                break;
            case GalacticDust gd:
                fields["GalacticDust._starsSpent"] = IntVal(gd.StarsSpent);
                break;
            case HappyFlower hf:
                fields["HappyFlower._turnCounter"] = IntVal(hf.TurnsSeen);
                fields["HappyFlower._grantEnergyThisTurn"] = BoolVal(
                    (bool)(typeof(HappyFlower)
                        .GetField("_grantEnergyThisTurn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        ?.GetValue(hf) ?? throw new MissingFieldException(typeof(HappyFlower).FullName, "_grantEnergyThisTurn")));
                break;
            case HistoryCourse:
                (CardModel? lastAtk, int lastTurn, CardModel? prevAtk, int prevTurn) = FindHistoryCourseAttacks(
                    state, player, finishedEntries);
                fields["HistoryCourse._lastAttack"] = CardHandleVal(lastAtk is not null ? cardHandles[lastAtk] : null);
                fields["HistoryCourse._lastAttackTurn"] = IntVal(lastTurn);
                fields["HistoryCourse._previousAttack"] = CardHandleVal(prevAtk is not null ? cardHandles[prevAtk] : null);
                fields["HistoryCourse._previousAttackTurn"] = IntVal(prevTurn);
                break;
            case IronClub ic:
                fields["IronClub._cardsPlayed"] = IntVal(ic.CardsPlayed);
                break;
            case JossPaper jp:
                fields["JossPaper._cardsExhausted"] = IntVal(jp.CardsExhausted);
                fields["JossPaper._etherealCount"] = IntVal(jp._etherealCount);
                break;
            case Kunai kunai:
                fields["Kunai._attacksThisTurn"] = IntVal(kunai._attacksPlayedThisTurn);
                break;
            case Kusarigama kusa:
                fields["Kusarigama._attacksThisTurn"] = IntVal(kusa._attacksPlayedThisTurn);
                break;
            case LavaLamp lamp:
                fields["LavaLamp._tookDamageThisCombat"] = BoolVal(lamp.TookDamageThisCombat);
                break;
            case LetterOpener lo:
                fields["LetterOpener._skillsThisTurn"] = IntVal(lo._skillsPlayedThisTurn);
                break;
            case LizardTail lt:
                fields["LizardTail._wasUsed"] = BoolVal(lt.WasUsed);
                break;
            case MawBank mb:
                fields["MawBank._hasItemBeenBought"] = BoolVal(mb.HasItemBeenBought);
                break;
            case Metronome met:
                fields["Metronome._orbsChanneled"] = IntVal(met._orbsChanneled);
                break;
            case MiniRegent mr:
                fields["MiniRegent._triggeredThisTurn"] = BoolVal(mr._usedThisTurn);
                break;
            case MusicBox mbx:
                if (mbx._cardBeingPlayed is not null)
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_private_state_unmapped",
                        "relics.MusicBox._cardBeingPlayed",
                        "MusicBox has an active in-flight _cardBeingPlayed reference.");
                }
                fields["MusicBox._wasUsedThisTurn"] = BoolVal(mbx._wasUsedThisTurn);
                fields["MusicBox._cardBeingPlayed"] = CardHandleVal(null);
                break;
            case Nunchaku nun:
                fields["Nunchaku._attacksPlayed"] = IntVal(nun.AttacksPlayed);
                break;
            case Orichalcum ori:
                fields["Orichalcum._shouldTrigger"] = BoolVal(ori._shouldTrigger);
                break;
            case OrnamentalFan fan:
                fields["OrnamentalFan._attacksThisTurn"] = IntVal(fan._attacksPlayedThisTurn);
                break;
            case PaelsEye eye:
                fields["PaelsEye._usedThisCombat"] = BoolVal(eye._usedThisCombat);
                fields["PaelsEye._wasOwnerPartOfLastPlayerTurn"] = BoolVal(eye._wasOwnerPartOfLastPlayerTurn);
                fields["PaelsEye._eligibleForExtraTurn"] = BoolVal(!eye._usedThisCombat && eye._wasOwnerPartOfLastPlayerTurn);
                break;
            case MegaCrit.Sts2.Core.Models.Relics.PaelsLegion leg:
                bool activeInFlight = leg._affectedCardPlay is not null
                    && !finishedEntries.Any(e => ReferenceEquals(e.CardPlay, leg._affectedCardPlay));
                if (activeInFlight)
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_private_state_unmapped",
                        "relics.PaelsLegion._affectedCardPlay",
                        "PaelsLegion has an active in-flight _affectedCardPlay reference.");
                }
                fields["PaelsLegion._cooldown"] = IntVal(leg._cooldown);
                fields["PaelsLegion._affectedCardPlay"] = BoolVal(false);
                break;
            case PaelsTears tears:
                fields["PaelsTears._gainNextTurn"] = BoolVal(tears._hadLeftoverEnergy);
                break;
            case Pendulum pend:
                fields["Pendulum._turnCounter"] = IntVal(pend.TurnsSeen);
                break;
            case PenNib nib:
                fields["PenNib._attacksPlayed"] = IntVal(nib.AttacksPlayed);
                break;
            case Permafrost pf:
                fields["Permafrost._triggeredThisCombat"] = BoolVal(pf._activatedThisCombat);
                break;
            case Pocketwatch pw:
                fields["Pocketwatch._cardsPlayedThisTurn"] = IntVal(pw._cardsPlayedThisTurn);
                fields["Pocketwatch._cardsPlayedLastTurn"] = IntVal(pw._cardsPlayedLastTurn);
                break;
            case PollinousCore pc:
                fields["PollinousCore._handsSeen"] = IntVal(pc.TurnsSeen);
                break;
            case RainbowRing rr:
                fields["RainbowRing._playedAttack"] = BoolVal(rr._attacksPlayedThisTurn > 0);
                fields["RainbowRing._playedSkill"] = BoolVal(rr._skillsPlayedThisTurn > 0);
                fields["RainbowRing._playedPower"] = BoolVal(rr._powersPlayedThisTurn > 0);
                fields["RainbowRing._triggeredThisTurn"] = BoolVal(rr._activationCountThisTurn > 0);
                break;
            case RedSkull rs:
                fields["RedSkull._strengthApplied"] = BoolVal(rs._strengthApplied);
                break;
            case Regalite reg:
                fields["Regalite._usedThisTurn"] = BoolVal(reg._usedThisTurn);
                break;
            case RippleBasin:
                bool attackPlayed = finishedEntries.Any(e =>
                    e.HappenedThisTurn(state) && e.CardPlay.Card.Type == CardType.Attack && ReferenceEquals(e.CardPlay.Player, player));
                fields["RippleBasin._attackPlayedThisTurn"] = BoolVal(attackPlayed);
                break;
            case RuinedHelmet:
                bool ruinedUsed = (bool)(typeof(RuinedHelmet).GetField("_usedThisCombat", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(relic)
                    ?? false);
                fields["RuinedHelmet._usedThisCombat"] = BoolVal(ruinedUsed);
                break;
            case Shuriken shu:
                fields["Shuriken._attacksThisTurn"] = IntVal(shu._attacksPlayedThisTurn);
                break;
            case SilkenTress st:
                fields["SilkenTress._isUsed"] = BoolVal(st.IsUsedUp);
                break;
            case StoneCalendar sc:
                int damageTurn = sc.DynamicVars["DamageTurn"].IntValue;
                fields["StoneCalendar._shouldTriggerThisTurn"] = BoolVal((player.PlayerCombatState?.TurnNumber ?? 1) == damageTurn);
                break;
            case SwordOfStone sos:
                fields["SwordOfStone._eliteVictories"] = IntVal(sos.ElitesDefeated);
                break;
            case ThrowingAxe ta:
                fields["ThrowingAxe._used"] = BoolVal(ta._usedThisCombat);
                break;
            case Toolbox toolbox:
                if (toolbox.IsMelted)
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_private_state_unmapped",
                        "relics.Toolbox._wasUsedThisCombat",
                        "Melted Toolbox cannot prove live trigger state.");
                }
                if (!ProveToolboxUsedThisCombat(toolbox, player, allHistory))
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_private_state_unmapped",
                        "relics.Toolbox._wasUsedThisCombat",
                        "Cannot prove whether Toolbox.BeforeHandDraw already triggered on turn 1.");
                }
                fields["Toolbox._wasUsedThisCombat"] = BoolVal(true);
                break;
            case TuningFork tf:
                fields["TuningFork._skillsPlayed"] = IntVal(tf.SkillsPlayed);
                break;
            case UnsettlingLamp ul:
                if (ul._triggeringCard is not null && !ul._isFinishedTriggering)
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_private_state_unmapped",
                        "relics.UnsettlingLamp._triggeringCard",
                        "UnsettlingLamp has an active triggering card reference.");
                }
                string[] doubledTemporaryPowers = ul._doubledPowers?.OfType<ITemporaryPower>()
                    .Select(power => power.InternallyAppliedPower.Id.ToString())
                    .Distinct(StringComparer.Ordinal).Take(2).ToArray() ?? [];
                if (doubledTemporaryPowers.Length > 1)
                {
                    throw new InCombatLiveCaptureUnsupportedException("live_private_state_unmapped",
                        "relics.UnsettlingLamp._doubledTemporaryInternalPowerType",
                        "Multiple temporary power types cannot be represented by one simulator marker.");
                }
                fields["UnsettlingLamp._doubledTemporaryInternalPowerType"] = StringVal(doubledTemporaryPowers.FirstOrDefault());
                fields["UnsettlingLamp._finished"] = BoolVal(ul._isFinishedTriggering);
                fields["UnsettlingLamp._triggeringCard"] = CardHandleVal(
                    ul._triggeringCard is not null ? cardHandles[ul._triggeringCard] : null);
                break;
            case Vambrace vam:
                if (vam._triggeringCard is not null && !vam._blockGainedThisCombat)
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_private_state_unmapped",
                        "relics.Vambrace._triggeringCard",
                        "Vambrace has an active triggering card reference.");
                }
                fields["Vambrace._blockGainedThisCombat"] = BoolVal(vam._blockGainedThisCombat);
                fields["Vambrace._triggeringCard"] = CardHandleVal(
                    vam._triggeringCard is not null ? cardHandles[vam._triggeringCard] : null);
                break;
            case VenerableTeaSet vts:
                fields["VenerableTeaSet._isArmed"] = BoolVal(vts.GainEnergyInNextCombat);
                break;
            case MegaCrit.Sts2.Core.Models.Relics.Wuppy wuppy:
                fields["Wuppy._currentHp"] = IntVal(wuppy.CurrentHp);
                fields["Wuppy._maxHp"] = IntVal(wuppy.MaxHp);
                fields["Wuppy._currentHpSet"] = BoolVal(wuppy.CurrentHp > 0 || wuppy.MaxHp > 0);
                break;
        }

        return new RustKernelRelicDto(
            relic.Id.ToString(),
            relic.IsWax,
            relic.IsMelted,
            relic.StackCount,
            relic.FloorAddedToDeck,
            fields);
    }

    internal static IReadOnlyDictionary<string, RustKernelFieldValueDto> CapturePowerPrivateFields(
        PowerModel power,
        IReadOnlyDictionary<Creature, int> creatureHandles,
        IReadOnlyDictionary<CardModel, int> cardHandles,
        CardPlayStartedEntry[] startedEntries)
    {
        var fields = new Dictionary<string, RustKernelFieldValueDto>(StringComparer.Ordinal);
        switch (power)
        {
            case AdaptablePower adapt:
                fields["AdaptablePower._isReviving"] = BoolVal(adapt.GetInternalData<AdaptablePower.Data>().isReviving);
                break;
            case AutomationPower auto:
                fields["AutomationPower._cardsLeft"] = IntVal(auto.DisplayAmount);
                break;
            case BattlewornDummyTimeLimitPower dummy:
                fields["BattlewornDummyTimeLimitPower._shouldEscape"] = BoolVal(dummy.Amount <= 1 && !dummy.Owner.IsAlive);
                break;
            case CacophonyPower cacophony:
                fields["CacophonyPower._cardsLeft"] = IntVal(cacophony.DynamicVars.Cards.IntValue);
                break;
            case ChainsOfBindingPower chains:
                int afflictedThisTurn = CombatManager.Instance.History.Entries
                    .OfType<CardAfflictedEntry>()
                    .Count(e => e.HappenedThisTurn(chains.CombatState) && ReferenceEquals(e.Actor, chains.Owner));
                fields["ChainsOfBindingPower._cardsAfflictedThisTurn"] = IntVal(afflictedThisTurn);
                fields["ChainsOfBindingPower._boundCardPlayedThisTurn"] = BoolVal(
                    chains.GetInternalData<ChainsOfBindingPower.Data>().boundCardPlayed);
                break;
            case CrimsonMantlePower cm:
                fields["CrimsonMantlePower._selfDamage"] = DecimalVal(cm.DynamicVars["SelfDamage"].BaseValue);
                break;
            case CurlUpPower curl:
                CardModel? curlCard = curl.GetInternalData<CurlUpPower.Data>().playedCard;
                fields["CurlUpPower._triggeringCard"] = CardHandleVal(curlCard is not null ? cardHandles[curlCard] : null);
                break;
            case DampenPower dampen:
                DampenPower.Data dampenData = dampen.GetInternalData<DampenPower.Data>();
                fields["DampenPower._casters"] = CreatureHandleArrayVal(
                    dampenData.casters.Select(c => creatureHandles[c]).ToArray());
                fields["DampenPower._downgraded"] = new RustKernelFieldValueDto(
                    "CardIntMap",
                    CardIntMapValue: dampenData.downgradedCardsToOldUpgradeLevels
                        .Select(kv => new RustKernelCardIntEntryDto(cardHandles[kv.Key], kv.Value))
                        .ToArray());
                break;
            case DarkEmbracePower de:
                fields["DarkEmbracePower._etherealExhausts"] = IntVal(de.GetInternalData<DarkEmbracePower.Data>().etherealCount);
                break;
            case EncorePower enc:
                EncorePower.Data encData = enc.GetInternalData<EncorePower.Data>();
                if (encData.cardsBeingPlayed.Count > 0)
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_private_state_unmapped",
                        "powers.EncorePower._cardsBeingPlayed",
                        "EncorePower has active in-flight _cardsBeingPlayed.");
                }
                fields["EncorePower._cardsBeingPlayed"] = CardHandleArrayVal([]);
                fields["EncorePower._numberOfCardsPlayedSinceApplied"] = IntVal(encData.numberOfCardsPlayedSinceApplied);
                break;
            case EnnuiPower ennui:
                fields["EnnuiPower._sadnessAmount"] = IntVal(ennui.DynamicVars.Cards.IntValue);
                break;
            case FeralPower feral:
                fields["FeralPower._zeroCostAttacksPlayed"] = IntVal(feral.GetInternalData<FeralPower.Data>().zeroCostAttacksPlayed);
                break;
            case HardenedShellPower hs:
                fields["HardenedShellPower._damageReceivedThisTurn"] = DecimalVal(
                    hs.GetInternalData<HardenedShellPower.Data>().damageReceivedThisTurn);
                break;
            case HeistPower heist:
                fields["HeistPower.<Target>k__BackingField"] = CreatureHandleVal(
                    heist.Target is not null ? creatureHandles[heist.Target] : null);
                break;
            case HellraiserPower hr:
                fields["HellraiserPower._infiniteAutoPlaysThisTurn"] = IntVal(
                    hr.GetInternalData<HellraiserPower.Data>().infiniteAutoPlaysThisTurn);
                break;
            case IllusionPower illusion:
                fields["IllusionPower._followUpStateId"] = StringVal(illusion.FollowUpStateId);
                fields["IllusionPower._isReviving"] = BoolVal(illusion.IsReviving);
                break;
            case ImitationLearningPower il:
                fields["ImitationLearningPower._playerTarget"] = CreatureHandleVal(
                    il.Target is { } ilTarget ? creatureHandles[ilTarget] : null);
                fields["ImitationLearningPower._pending"] = new RustKernelFieldValueDto("CardPairArray", CardPairArrayValue: []);
                break;
            case ImpaledPower impaled:
                Player? impalePlayer = impaled.Applier?.Player ?? impaled.CombatState?.Players.FirstOrDefault();
                IReadOnlyList<MegaCrit.Sts2.Core.Entities.FoolArrows.ArrowSlot> playerArrows =
                    impalePlayer?.PlayerCombatState?.Arrows ?? [];
                var arrowIndices = impaled.Arrows.Select(a =>
                {
                    for (int idx = 0; idx < playerArrows.Count; idx++)
                    {
                        if (ReferenceEquals(playerArrows[idx], a))
                            return idx;
                    }
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_private_state_unmapped",
                        "powers.ImpaledPower._arrowSources",
                        "ImpaledPower references an arrow missing from player arrows.");
                }).ToArray();
                fields["ImpaledPower._hitCounter"] = IntVal(impaled.HitCounter);
                fields["ImpaledPower._arrowSources"] = IntArrayVal(arrowIndices);
                break;
            case InfernoPower inf:
                fields["InfernoPower._selfDamage"] = DecimalVal(inf.DynamicVars["SelfDamage"].BaseValue);
                break;
            case JugglingPower jugg:
                JugglingPower.Data juggData = jugg.GetInternalData<JugglingPower.Data>();
                fields["JugglingPower._attacksPlayedThisTurn"] = IntVal(juggData.attacksPlayedThisTurn);
                fields["JugglingPower._cardToClone"] = CardHandleVal(
                    juggData.cardToClone is not null ? cardHandles[juggData.cardToClone] : null);
                break;
            case LullabyPower lullaby:
                fields["LullabyPower._proccdThisTurn"] = BoolVal(lullaby._proccdThisTurn);
                break;
            case MonologuePower mono:
                fields["MonologuePower._strengthApplied"] = DecimalVal(mono.DynamicVars["StrengthApplied"].BaseValue);
                fields["MonologuePower._strength"] = DecimalVal(mono.DynamicVars.Strength.BaseValue);
                break;
            case NemesisPower nemesis:
                bool shouldApplyIntangible = (bool)(typeof(NemesisPower)
                    .GetField("_shouldApplyIntangible", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(nemesis) ?? false);
                fields["NemesisPower._shouldApplyIntangible"] = BoolVal(shouldApplyIntangible);
                break;
            case NightmarePower nightmare:
                CardModel? selectedNm = nightmare.GetInternalData<NightmarePower.Data>().selectedCard;
                fields["NightmarePower._selectedCard"] = CardHandleVal(
                    selectedNm is not null ? cardHandles[selectedNm] : null);
                break;
            case OblivionPower oblivion:
                OblivionPower.Data obData = oblivion.GetInternalData<OblivionPower.Data>();
                fields["OblivionPower._amountsForPlayedCards"] = new RustKernelFieldValueDto(
                    "CardIntMap",
                    CardIntMapValue: obData.amountsForPlayedCards
                        .Select(kv => new RustKernelCardIntEntryDto(cardHandles[kv.Key], kv.Value))
                        .ToArray());
                break;
            case OrbitPower orbit:
                fields["OrbitPower._energySpent"] = IntVal(orbit.GetInternalData<OrbitPower.Data>().energySpent % 4);
                break;
            case PaleBlueDotPower pbd:
                fields["PaleBlueDotPower._alreadyActivatedThisTurn"] = BoolVal(
                    pbd.GetInternalData<PaleBlueDotPower.Data>().alreadyActivatedThisTurn);
                break;
            case PanachePower panache:
                fields["PanachePower._alreadyApplied"] = BoolVal(panache.GetInternalData<PanachePower.Data>().alreadyApplied);
                fields["PanachePower._cardsLeft"] = IntVal(panache.DynamicVars["CardsLeft"].IntValue);
                break;
            case PossessSpeedPower pSpeed:
                fields["PossessSpeedPower._stolen"] = new RustKernelFieldValueDto(
                    "CreatureDecimalMap",
                    CreatureDecimalMapValue: pSpeed.GetInternalData<PossessSpeedPower.Data>().stolenDexterity
                        .Select(kv => new RustKernelCreatureDecimalEntryDto(creatureHandles[kv.Key], kv.Value))
                        .ToArray());
                break;
            case PossessStrengthPower pStrength:
                fields["PossessStrengthPower._stolen"] = new RustKernelFieldValueDto(
                    "CreatureDecimalMap",
                    CreatureDecimalMapValue: pStrength.GetInternalData<PossessStrengthPower.Data>().stolenStrength
                        .Select(kv => new RustKernelCreatureDecimalEntryDto(creatureHandles[kv.Key], kv.Value))
                        .ToArray());
                break;
            case ReattachPower reattach:
                fields["ReattachPower._isReviving"] = BoolVal(reattach.GetInternalData<ReattachPower.Data>().isReviving);
                break;
            case RitualPower ritual:
                bool skipFirst = (bool)(typeof(RitualPower)
                    .GetField("_wasJustAppliedByEnemy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(ritual) ?? false);
                fields["RitualPower._skipFirstEnemyTurnEnd"] = BoolVal(skipFirst);
                break;
            case RupturePower rupture:
                fields["RupturePower._pendingStrength"] = new RustKernelFieldValueDto(
                    "CardIntMap",
                    CardIntMapValue: rupture.GetInternalData<RupturePower.Data>().playedCards
                        .Select(kv => new RustKernelCardIntEntryDto(cardHandles[kv.Key], kv.Value))
                        .ToArray());
                break;
            case SandpitPower sandpit:
                fields["SandpitPower.<Target>k__BackingField"] = CreatureHandleVal(
                    sandpit.Target is not null ? creatureHandles[sandpit.Target] : null);
                break;
            case ShowOffPower showOff:
                fields["ShowOffPower._showOffInstance"] = CardHandleVal(
                    showOff.ShowOffInstance is not null ? cardHandles[showOff.ShowOffInstance] : null);
                break;
            case SkittishPower skittish:
                fields["SkittishPower._hasGainedBlockThisTurn"] = BoolVal(skittish.HasGainedBlockThisTurn);
                break;
            case SlothPower sloth:
                int slothPlayed = (int)(typeof(SlothPower)
                    .GetField("_cardsPlayedThisTurn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(sloth) ?? sloth.DisplayAmount);
                fields["SlothPower._cardsPlayed"] = IntVal(slothPlayed);
                break;
            case SlowPower slow:
                fields["SlowPower._cardsPlayedSinceOwnerTurnStart"] = IntVal(slow.DynamicVars["SlowAmount"].IntValue);
                break;
            case SmoggyPower smoggy:
                bool skillStarted = startedEntries.Any(e =>
                    e.HappenedThisTurn(smoggy.CombatState)
                    && ReferenceEquals(e.CardPlay.Player, smoggy.Owner.Player)
                    && e.CardPlay.Card.Type == CardType.Skill);
                fields["SmoggyPower._skillCardPlayStartedThisTurn"] = BoolVal(skillStarted);
                break;
            case StretchPower stretch:
                if (stretch.GetInternalData<StretchPower.Data>().cardPlay is not null)
                {
                    throw new InCombatLiveCaptureUnsupportedException(
                        "live_private_state_unmapped",
                        "powers.StretchPower._cardPlay",
                        "StretchPower has an active in-flight cardPlay reference.");
                }
                break;
            case SurroundedPower surrounded:
                fields["SurroundedPower.<Facing>k__BackingField"] = IntVal((int)surrounded.Facing);
                break;
            case SwipePower swipe:
                fields["SwipePower.<StolenCard>k__BackingField"] = CardHandleVal(
                    swipe.StolenCard is not null ? cardHandles[swipe.StolenCard] : null);
                break;
            case SwordSagePower swordSage:
                var sovEntries = (swordSage.Owner.Player?.PlayerCombatState?.AllCards ?? [])
                    .Where(static c => c is SovereignBlade)
                    .Select(c => new RustKernelCardIntEntryDto(cardHandles[c], swordSage.Amount))
                    .ToArray();
                fields["SwordSagePower._grantedReplays"] = new RustKernelFieldValueDto("CardIntMap", CardIntMapValue: sovEntries);
                break;
            case TenderPower tender:
                int tenderPlayed = (int)(typeof(TenderPower)
                    .GetField("_cardsPlayedThisTurn", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(tender) ?? tender.DisplayAmount);
                fields["TenderPower._cardsPlayedThisTurn"] = IntVal(tenderPlayed);
                break;
            case TheBombPower bomb:
                fields["TheBombPower._damage"] = DecimalVal(bomb.DynamicVars.Damage.BaseValue);
                break;
            case ThieveryPower thievery:
                fields["ThieveryPower._goldStolen"] = IntVal(thievery.DynamicVars.Gold.IntValue);
                fields["ThieveryPower.<Target>k__BackingField"] = CreatureHandleVal(
                    thievery.Target is not null ? creatureHandles[thievery.Target] : null);
                break;
            case ToricToughnessPower toric:
                fields["ToricToughnessPower.<StoredBlock>k__BackingField"] = IntVal(toric.DynamicVars.Block.IntValue);
                break;
            case VoidFormPower vf:
                fields["VoidFormPower._cardsPlayedThisTurn"] = IntVal(vf.GetInternalData<VoidFormPower.Data>().cardsPlayedThisTurn);
                break;
            case WitheringPresencePower wp:
                Creature wpTarget = wp.Target ?? wp.Owner;
                int cardsLeft = wp.DynamicVars["CardsLeft"].IntValue;
                fields["WitheringPresencePower._cardsPlayed"] = IntVal((6 - cardsLeft) % 6);
                fields["WitheringPresencePower.<Target>k__BackingField"] = CreatureHandleVal(creatureHandles[wpTarget]);
                break;
            case YahPower yah:
                fields["YahPower._triggeredThisTurn"] = BoolVal(yah.GetInternalData<YahPower.Data>().triggeredThisTurn);
                break;
        }
        return fields;
    }

    private static (IReadOnlyDictionary<string, RustKernelFieldValueDto> SetupFields,
        IReadOnlyDictionary<string, RustKernelFieldValueDto> PostFields)
        CaptureMonsterPrivateFields(
            MonsterModel monster,
            IReadOnlyDictionary<Creature, int> creatureHandles)
    {
        var setup = new Dictionary<string, RustKernelFieldValueDto>(StringComparer.Ordinal);
        var post = new Dictionary<string, RustKernelFieldValueDto>(StringComparer.Ordinal);

        switch (monster)
        {
            case Nibbit nib:
                setup["Nibbit._isFront"] = BoolVal(nib.IsFront);
                setup["Nibbit._isAlone"] = BoolVal(nib.IsAlone);
                break;
            case CorpseSlug slug:
                setup["CorpseSlug._starterMoveIdx"] = IntVal(slug.StarterMoveIdx);
                post["CorpseSlug._isRavenous"] = BoolVal(slug.IsRavenous);
                break;
            case TwoTailedRat rat:
                setup["TwoTailedRat._starterMoveIndex"] = IntVal(rat.StarterMoveIndex);
                post["TwoTailedRat._turnsUntilSummonable"] = IntVal(HotImportSupport.ReadMonsterInt(rat, "_turnsUntilSummonable"));
                post["TwoTailedRat._callForBackupCount"] = IntVal(rat.CallForBackupCount);
                break;
            case ScrollOfBiting scroll:
                setup["ScrollOfBiting._starterMoveIdx"] = IntVal(scroll.StarterMoveIdx);
                break;
            case Chomper chomper:
                setup["Chomper._screamFirst"] = BoolVal(chomper.ScreamFirst);
                break;
            case Inklet inklet:
                setup["Inklet._middleInklet"] = BoolVal(inklet.MiddleInklet);
                break;
            case KinFollower kin:
                setup["KinFollower._startsWithDance"] = BoolVal(kin.Index == 0);
                break;
            case Wriggler wriggler:
                setup["Wriggler._startStunned"] = BoolVal(wriggler.StartStunned);
                break;
            case PunchConstruct punch:
                setup["PunchConstruct._startsWithFastPunch"] = BoolVal(punch.StartsWithFastPunch);
                break;
            case DecimillipedeSegment seg:
                setup["DecimillipedeSegment._starterMoveIdx"] = IntVal(seg.StarterMoveIdx);
                break;
            case LouseProgenitor louse:
                setup["LouseProgenitor._curled"] = BoolVal(louse.Curled);
                break;
            case Aeonglass ag:
                post["Aeonglass._additionalStrength"] = IntVal(HotImportSupport.ReadMonsterInt(ag, "_additionalStrength"));
                post["Aeonglass._witherUpgradeCount"] = IntVal(ag.WitherUpgradeCount);
                break;
            case Axebot ab:
                object? stockRaw = HotImportSupport.ReadMonsterObject(ab, "_stockOverrideAmount");
                post["Axebot._stockAmount"] = NullableIntVal(stockRaw as int?);
                break;
            case BowlbugRock br:
                post["BowlbugRock._isOffBalance"] = BoolVal(br.IsOffBalance);
                break;
            case CeremonialBeast cb:
                post["CeremonialBeast._beastCryState"] = StringVal(cb.BeastCryState?.Id);
                break;
            case Fabricator fab:
                string? botId = (HotImportSupport.ReadMonsterObject(fab, "_lastSpawned") as MonsterModel)?.Id.Entry;
                post["Fabricator._lastSpawned"] = StringVal(botId);
                break;
            case FrogKnight fk:
                post["FrogKnight._charged"] = BoolVal(HotImportSupport.ReadMonsterBool(fk, "_hasBeetleCharged"));
                break;
            case GasBomb gb:
                post["GasBomb._hasExploded"] = BoolVal(HotImportSupport.ReadMonsterBool(gb, "_hasExploded"));
                break;
            case KnowledgeDemon kd:
                post["KnowledgeDemon._curseCounter"] = IntVal(HotImportSupport.ReadMonsterInt(kd, "_curseOfKnowledgeCounter"));
                break;
            case LagavulinMatriarch lm:
                post["LagavulinMatriarch._isAwake"] = BoolVal(lm.IsAwake);
                post["LagavulinMatriarch._isShellAwake"] = BoolVal(lm.IsShellAwake);
                break;
            case LivingFog lf:
                post["LivingFog._bloatAmount"] = IntVal(HotImportSupport.ReadMonsterInt(lf, "_bloatAmount"));
                break;
            case PhantasmalGardener pg:
                post["PhantasmalGardener._enlargeTriggers"] = IntVal(pg.EnlargeTriggers);
                break;
            case Queen queen:
                post["Queen._hasAmalgamDied"] = BoolVal(HotImportSupport.ReadMonsterBool(queen, "_hasAmalgamDied"));
                post["Queen._amalgam"] = CreatureHandleVal(queen._amalgam is { } amalgam ? creatureHandles[amalgam] : null);
                break;
            case SoulFysh sf:
                post["SoulFysh._isInvisible"] = BoolVal(sf.IsInvisible);
                break;
            case TestSubject ts:
                post["TestSubject._respawns"] = IntVal(HotImportSupport.ReadMonsterInt(ts, "_respawns"));
                post["TestSubject._extraMultiClawCount"] = IntVal(HotImportSupport.ReadMonsterInt(ts, "_extraMultiClawCount"));
                break;
            case Toadpole toad:
                setup["Toadpole._isFront"] = BoolVal(toad.IsFront);
                post["Toadpole._isFront"] = BoolVal(toad.IsFront);
                break;
            case ToughEgg egg:
                post["ToughEgg._isHatched"] = BoolVal(egg.IsHatched);
                post["ToughEgg._afterHatchedState"] = StringVal(egg.AfterHatchedState?.Id);
                break;
            case WaterfallGiant wg:
                post["WaterfallGiant._currentPressureGunDamage"] = IntVal(HotImportSupport.ReadMonsterInt(wg, "_currentPressureGunDamage"));
                post["WaterfallGiant._steamEruptionDamage"] = IntVal(HotImportSupport.ReadMonsterInt(wg, "_steamEruptionDamage"));
                break;
        }

        return (setup, post);
    }
}
