using System.Text;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.CardPools;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private static readonly FieldInfo ArtOfWarLastTurnField =
        typeof(ArtOfWar).GetField("_anyAttacksPlayedLastTurn", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(ArtOfWar).FullName, "_anyAttacksPlayedLastTurn");
    private static readonly FieldInfo ArtOfWarThisTurnField =
        typeof(ArtOfWar).GetField("_anyAttacksPlayedThisTurn", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(ArtOfWar).FullName, "_anyAttacksPlayedThisTurn");
    private static readonly FieldInfo BeltBuckleDexterityAppliedField =
        typeof(BeltBuckle).GetField("_dexterityApplied", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(BeltBuckle).FullName, "_dexterityApplied");

    private readonly record struct StatefulRelicState(int Current, int Previous);

    private ForkableDictionary<RelicModel, StatefulRelicState>? _statefulRelicStates;

    public bool PrepareRelicsBeforeHandDraw(
        CombatPredictionSimulator simulator,
        Player player,
        TurnStartChoiceCursor choices)
    {
        int turn = GetPlayerTurnNumber(player);
        foreach (RelicModel relic in RelicsOf(player).Where(static relic => !relic.IsMelted))
        {
            StatefulRelicState state;
            switch (relic)
            {
                case Pendulum:
                    state = GetStatefulRelicState(relic);
                    SetStatefulRelicState(
                        relic,
                        state with
                        {
                            Current = (state.Current + 1) % relic.DynamicVars["Turns"].IntValue,
                        });
                    break;
                case PollinousCore:
                    state = GetStatefulRelicState(relic);
                    SetStatefulRelicState(relic, state with { Current = state.Current + 1 });
                    break;
                case BlessedAntler when turn <= 1:
                    simulator.CreateAndAddGeneratedCardsToCombat<Dazed>(
                        player,
                        PileType.Draw,
                        relic.DynamicVars.Cards.IntValue,
                        player,
                        CardPilePosition.Random);
                    break;
                case JeweledMask when turn <= 1:
                {
                    SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
                    List<PredictedCard> powerCards = playerState.DrawPile.Cards
                        .Where(card => card.Preview.Type == CardType.Power)
                        .ToList();
                    if (powerCards.Count == 0)
                        break;
                    List<PredictedCard> nonInnate = powerCards
                        .Where(card => !card.Preview.Keywords.Contains(CardKeyword.Innate))
                        .ToList();
                    if (nonInnate.Count > 0)
                        powerCards = nonInnate;
                    PredictedCard selected = simulator.Rng.CombatCardSelection.NextItem(powerCards)
                        ?? throw new InvalidOperationException("宝石面具存在能力牌候选但随机选择返回空。");
                    selected.SetToFreeThisTurn();
                    simulator.AddToPile(selected, PileType.Hand);
                    break;
                }
                case Toolbox when turn <= 1:
                {
                    IReadOnlyList<PredictedCard> options = ModelDb.CardPool<ColorlessCardPool>()
                        .GetUnlockedCards(player.UnlockState, _cardMultiplayerConstraint)
                        .GetDistinctForCombat(
                            player,
                            relic.DynamicVars.Cards.IntValue,
                            simulator.Rng.CombatCardGeneration,
                            _cardMultiplayerConstraint)
                        .ToArray();
                    if (!TurnStartChoiceSupport.ResolveGeneratedToHand(
                            simulator,
                            this,
                            player,
                            choices,
                            relic.Id.Entry,
                            options,
                            "BEFORE_HAND_DRAW"))
                    {
                        return true;
                    }
                    break;
                }
            }
            if (simulator.HasPendingChoice)
                return true;
        }
        return false;
    }

    public void CompleteRelicHandDraw(Player player)
    {
        foreach (PollinousCore relic in RelicsOf(player)
                     .OfType<PollinousCore>()
                     .Where(static relic => !relic.IsMelted))
        {
            StatefulRelicState state = GetStatefulRelicState(relic);
            if (state.Current >= relic.DynamicVars["Turns"].IntValue)
                SetStatefulRelicState(relic, state with { Current = 0 });
        }
    }

    public void RecordRelicCardPlayed(
        CombatPredictionSimulator simulator,
        Player player,
        CardModel card)
    {
        foreach (RelicModel relic in RelicsOf(player).Where(static relic => !relic.IsMelted))
        {
            switch (relic)
            {
                case Pocketwatch:
                    {
                        StatefulRelicState state = GetStatefulRelicState(relic);
                        SetStatefulRelicState(relic, state with { Current = state.Current + 1 });
                        break;
                    }
                case ArtOfWar when card.Type == CardType.Attack:
                    {
                        StatefulRelicState state = GetStatefulRelicState(relic);
                        SetStatefulRelicState(relic, state with { Current = 1 });
                        break;
                    }
                case RainbowRing value:
                    {
                        StatefulRelicState state = GetStatefulRelicState(value);
                        if (state.Current == 0
                            && RelicPredictionStateSupport.GetRainbowActivationCount(simulator, value) > 0)
                        {
                            // Power application belongs to the exact AfterCardPlayed mirror.
                            // Keep this existing lifecycle projection synchronized with its counter.
                            SetStatefulRelicState(value, state with { Current = 1 });
                        }
                        break;
                    }
            }
        }
    }

    public decimal GetStatefulRelicHandDrawContribution(
        RelicModel relic,
        Player player,
        int turn)
        => GetStatefulRelicHandDrawContribution(relic, player, turn, GetStatefulRelicState(relic));

    public bool TryGetPocketwatchState(
        Player player,
        out int cardsPlayedThisTurn,
        out int cardsPlayedLastTurn,
        out int cardThreshold)
    {
        Pocketwatch? pocketwatch = RelicsOf(player)
            .OfType<Pocketwatch>()
            .FirstOrDefault(static relic => !relic.IsMelted);
        if (pocketwatch == null)
        {
            cardsPlayedThisTurn = 0;
            cardsPlayedLastTurn = 0;
            cardThreshold = -1;
            return false;
        }

        StatefulRelicState state = GetStatefulRelicState(pocketwatch);
        cardsPlayedThisTurn = state.Current;
        cardsPlayedLastTurn = state.Previous;
        cardThreshold = pocketwatch.DynamicVars["CardThreshold"].IntValue;
        return true;
    }

    public static decimal GetLiveStatefulRelicHandDrawContribution(
        RelicModel relic,
        Player player,
        int turn)
        => GetStatefulRelicHandDrawContribution(relic, player, turn, CaptureLiveState(relic));

    private static decimal GetStatefulRelicHandDrawContribution(
        RelicModel relic,
        Player player,
        int turn,
        StatefulRelicState state)
    {
        if (!ReferenceEquals(relic.Owner, player))
            return 0m;
        return relic switch
        {
            Pendulum when state.Current == 0 => relic.DynamicVars.Cards.BaseValue,
            Pocketwatch when turn != 1
                && state.Previous <= relic.DynamicVars["CardThreshold"].IntValue
                => relic.DynamicVars.Cards.BaseValue,
            PollinousCore when state.Current >= relic.DynamicVars["Turns"].IntValue
                => relic.DynamicVars.Cards.BaseValue,
            _ => 0m,
        };
    }

    public static void AppendLiveStatefulRelics(StringBuilder text, Player player)
        => AppendStatefulRelics(text, player.Relics, CaptureLiveState);

    public void AppendPredictedStatefulRelics(StringBuilder text, Player player)
        => AppendStatefulRelics(text, RelicsOf(player), GetStatefulRelicState);

    private static void AppendStatefulRelics(
        StringBuilder text,
        IEnumerable<RelicModel> relics,
        Func<RelicModel, StatefulRelicState> getState)
    {
        text.Append(";relicCounters=");
        bool first = true;
        foreach (RelicModel relic in relics
                     .Where(static relic => !relic.IsMelted && IsStatefulRelic(relic))
                     .OrderBy(static relic => relic.Id.Entry, StringComparer.Ordinal))
        {
            if (!first)
                text.Append(',');
            first = false;
            StatefulRelicState state = getState(relic);
            text.Append(relic.Id.Entry)
                .Append('/').Append(state.Current)
                .Append('/').Append(state.Previous);
        }
    }

    private void AppendStatefulRelicFingerprint(
        ref StateFingerprintBuilder fingerprint,
        CombatPredictionSimulator simulator)
    {
        fingerprint.Add('L');
        foreach (Player player in Players)
        {
            foreach (RelicModel relic in RelicsOf(player))
            {
                if (relic.IsMelted || !IsStatefulRelic(relic))
                    continue;
                StatefulRelicState state = PeekStatefulRelicState(relic);
                fingerprint.Add(player.NetId);
                fingerprint.Add(relic.Id.Entry);
                fingerprint.Add(state.Current);
                fingerprint.Add(state.Previous);
            }
            foreach (RelicModel relic in RelicsOf(player))
            {
                if (relic.IsMelted || !RelicPredictionStateSupport.IsTracked(relic))
                    continue;
                fingerprint.Add(player.NetId);
                fingerprint.Add(relic.Id.Entry);
                RelicPredictionStateSupport.AppendFingerprint(ref fingerprint, simulator, relic);
            }
        }
    }

    private StatefulRelicState GetStatefulRelicState(RelicModel relic)
    {
        if (_statefulRelicStates?.TryGetValue(relic, out StatefulRelicState state) == true)
            return state;
        if (_rootMaterialized && _rootRelics.Values.Any(relics => relics.Contains(relic)))
            throw new InvalidOperationException($"Root relic state was not captured for {relic.Id.Entry}.");
        state = CaptureLiveState(_rootRelicSources?.GetValueOrDefault(relic, relic) ?? relic);
        (_statefulRelicStates ??= [])[relic] = state;
        return state;
    }

    private StatefulRelicState PeekStatefulRelicState(RelicModel relic)
        => GetStatefulRelicState(relic);

    private void SetStatefulRelicState(RelicModel relic, StatefulRelicState state)
        => (_statefulRelicStates ??= [])[relic] = state;

    public bool GetRedSkullStrengthApplied(RedSkull relic)
        => GetStatefulRelicState(relic).Current != 0;

    public void SetRedSkullStrengthApplied(RedSkull relic, bool value)
    {
        StatefulRelicState state = GetStatefulRelicState(relic);
        SetStatefulRelicState(relic, state with { Current = value ? 1 : 0 });
    }

    private static StatefulRelicState CaptureLiveState(RelicModel relic)
        => relic switch
        {
            ArtOfWar art => new StatefulRelicState(
                (bool)ArtOfWarThisTurnField.GetValue(art)! ? 1 : 0,
                (bool)ArtOfWarLastTurnField.GetValue(art)! ? 1 : 0),
            BeltBuckle buckle => new StatefulRelicState(
                (bool)BeltBuckleDexterityAppliedField.GetValue(buckle)! ? 1 : 0,
                0),
            BoneTea tea => new StatefulRelicState(tea.CombatsLeft, 0),
            EmotionChip chip => new StatefulRelicState(
                CombatManager.Instance.History.Entries
                    .OfType<DamageReceivedEntry>()
                    .Any(entry => ReferenceEquals(entry.Receiver, chip.Owner.Creature)
                                  && !entry.Result.WasFullyBlocked
                                  && entry.HappenedLastPlayerTurn(chip.Owner)) ? 1 : 0,
                CombatManager.Instance.History.Entries
                    .OfType<DamageReceivedEntry>()
                    .Any(entry => ReferenceEquals(entry.Receiver, chip.Owner.Creature)
                                  && !entry.Result.WasFullyBlocked
                                  && entry.HappenedThisTurn(chip.Owner.Creature.CombatState)) ? 1 : 0),
            FakeHappyFlower flower => new StatefulRelicState(flower.TurnsSeen, 0),
            FakeVenerableTeaSet tea => new StatefulRelicState(tea.GainEnergyInNextCombat ? 1 : 0, 0),
            HappyFlower flower => new StatefulRelicState(flower.TurnsSeen, 0),
            MiniRegent regent => new StatefulRelicState(regent._usedThisTurn ? 1 : 0, 0),
            GalacticDust dust => new StatefulRelicState(dust.StarsSpent, 0),
            PaelsEye eye => new StatefulRelicState(eye._usedThisCombat ? 1 : 0, eye._wasOwnerPartOfLastPlayerTurn ? 1 : 0),
            PaelsTears tears => new StatefulRelicState(tears._hadLeftoverEnergy ? 1 : 0, 0),
            RainbowRing ring => new StatefulRelicState(ring._activationCountThisTurn, 0),
            RedSkull skull => new StatefulRelicState(skull._strengthApplied ? 1 : 0, 0),
            Pendulum pendulum => new StatefulRelicState(pendulum.TurnsSeen, 0),
            Pocketwatch pocketwatch => new StatefulRelicState(
                pocketwatch._cardsPlayedThisTurn,
                pocketwatch._cardsPlayedLastTurn),
            PollinousCore core => new StatefulRelicState(core.TurnsSeen, 0),
            RuinedHelmet helmet => new StatefulRelicState(CaptureRuinedHelmetUsed(helmet) ? 1 : 0, 0),
            UnsettlingLamp lamp => new StatefulRelicState(CaptureUnsettlingLampFinished(lamp) ? 1 : 0, 0),
            VenerableTeaSet tea => new StatefulRelicState(tea.GainEnergyInNextCombat ? 1 : 0, 0),
            _ => default,
        };

    private static bool IsStatefulRelic(RelicModel relic)
        => relic is ArtOfWar
            or BeltBuckle
            or BoneTea
            or EmotionChip
            or FakeHappyFlower
            or FakeVenerableTeaSet
            or HappyFlower
            or MiniRegent
            or GalacticDust
            or PaelsEye
            or PaelsTears
            or RainbowRing
            or RedSkull
            or Pendulum
            or Pocketwatch
            or PollinousCore
            or RuinedHelmet
            or UnsettlingLamp
            or VenerableTeaSet;

    public bool CanTriggerArtOfWarNextTurn(Player player)
        => RelicsOf(player)
            .OfType<ArtOfWar>()
            .Any(relic => !relic.IsMelted && GetStatefulRelicState(relic).Current == 0);
}
