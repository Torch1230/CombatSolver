using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private Dictionary<Player, PredictedCard>? _lastAttackThisTurn;
    private Dictionary<Player, PredictedCard>? _lastAttackPreviousTurn;

    private void RecordHistoryCourseAttack(PredictedCard card)
    {
        if (card.Preview.Type != CardType.Attack || card.Preview.IsDupe)
            return;
        (_lastAttackThisTurn ??= [])[card.Preview.Owner] = card;
    }

    public void CommitHistoryCourseTurn(Player player)
    {
        _lastAttackPreviousTurn ??= [];
        if (_lastAttackThisTurn?.Remove(player, out PredictedCard? card) == true)
            _lastAttackPreviousTurn[player] = card;
        else
            _lastAttackPreviousTurn.Remove(player);
    }

    public bool TriggerScheduledAutoPlays(
        CombatPredictionSimulator simulator,
        Player player,
        int turnNumber,
        TurnStartChoiceCursor choices,
        ISet<uint> processedEnemyDeaths)
    {
        int mayhem = GetAmount<MegaCrit.Sts2.Core.Models.Powers.MayhemPower>(player.Creature);
        IReadOnlyList<PredictedCard> mayhemCards = simulator.MoveCardsForAutoPlay(
            player,
            mayhem,
            CardPilePosition.Top);
        if (HasPendingChoice)
        {
            simulator.AppendExecutionContinuation(new ScheduledAutoPlayFrame(player, turnNumber,
                processedEnemyDeaths, mayhemCards, 0));
            return true;
        }
        return ContinueScheduledAutoPlays(simulator, player, turnNumber, processedEnemyDeaths, mayhemCards, 0);
    }

    public bool TriggerWhisperingEarring(
        CombatPredictionSimulator simulator,
        Player player,
        int turnNumber,
        ISet<uint> processedEnemyDeaths)
    {
        if (turnNumber > 1)
            return true;

        foreach (WhisperingEarring relic in RelicsOf(player)
                     .OfType<WhisperingEarring>()
                     .Where(static relic => !relic.IsMelted))
        {
            TurnStartChoiceCursor vakuuChoices = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
                request.Spec == null ? null : CardChoiceSupport.BuildVakuuChoice(request.Spec));
            TurnStartChoiceCursor previous = OverrideActionChoices(vakuuChoices);
            try
            {
                for (int cardsPlayed = 0; cardsPlayed < WhisperingEarring.maxCardsToPlay; cardsPlayed++)
                {
                    if (simulator.IsOverOrEnding)
                        break;
                    SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
                    PredictedCard? card = playerState.Hand.Cards
                        .FirstOrDefault(candidate => CanPlayCard(simulator, candidate));
                    if (card == null)
                        break;
                    Creature? target = simulator.GetTargetType(card) switch
                    {
                        TargetType.AnyEnemy => HittableEnemies.FirstOrDefault(),
                        TargetType.AnyAlly => simulator.Rng.CombatTargets.NextItem(
                            Allies.Where(candidate => candidate.IsPlayer
                                && !ReferenceEquals(candidate, player.Creature)
                                && simulator.State.GetCreature(candidate).IsAlive)),
                        TargetType.AnyPlayer => player.Creature,
                        _ => null,
                    };
                    // The overridden cursor answers every request with the fixed Vakuu policy, so the
                    // card's own selection resolves inside the auto-play like any other nested choice.
                    bool played = CardExecutionSupport.AutoPlay(
                            simulator,
                            this,
                            card,
                            target,
                            processedEnemyDeaths,
                            payResources: true,
                            nestedChoiceSourceId: card.Preview.Id.Entry);
                    if (HasPendingChoice)
                        return false;
                    if (!played)
                    {
                        break;
                    }

                    if (!CorePowerSupport.ApplyEnemyDeathPowers(
                            simulator,
                            this,
                            KnownEnemies,
                            processedEnemyDeaths))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                RestoreActionChoices(vakuuChoices, previous);
            }
        }
        return true;
    }

    public bool AutoPlayWithChoice(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        string sourceId,
        string contextId,
        TurnStartChoiceCursor choices,
        ISet<uint> processedEnemyDeaths)
    {
        _ = choices;
        _ = CardExecutionSupport.AutoPlay(
            simulator,
            this,
            card,
            target: null,
            processedEnemyDeaths,
            nestedChoiceSourceId: sourceId,
            nestedChoiceContextId: contextId);
        return !HasPendingChoice;
    }

    private PredictedCard? GetPreviousTurnAttack(CombatPredictionSimulator simulator, Player player)
    {
        if (_rootMaterialized)
            return _lastAttackPreviousTurn?.GetValueOrDefault(player);
        if (_lastAttackPreviousTurn?.TryGetValue(player, out PredictedCard? predicted) == true)
            return predicted;
        CardPlayFinishedEntry? live = _rootHistory.CardPlaysFinished.LastOrDefault(entry =>
            entry.CardPlay.Player == player
            && entry.HappenedLastPlayerTurn(player)
            && entry.CardPlay.Card.Type == CardType.Attack
            && !entry.CardPlay.Card.IsDupe);
        if (live == null)
            return null;
        predicted = simulator.State.FindCard(live.CardPlay.Card)
            ?? PredictedCard.FromGenerated(PredictionUtils.CloneCardStateForSimulation(live.CardPlay.Card));
        (_lastAttackPreviousTurn ??= [])[player] = predicted;
        return predicted;
    }

    private static Dictionary<Player, PredictedCard>? ForkHistoryCourseCards(
        Dictionary<Player, PredictedCard>? source,
        PredictionForkContext context)
    {
        if (source == null)
            return null;
        Dictionary<Player, PredictedCard> result = new(source.Count);
        foreach ((Player player, PredictedCard card) in source)
            result.Add(player, ForkCard(card, context));
        return result;
    }

    private void AppendAutoPlayFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        AppendTrackedAttack(ref fingerprint, 't', _lastAttackThisTurn);
        AppendTrackedAttack(ref fingerprint, 'p', _lastAttackPreviousTurn);
    }

    private static void AppendTrackedAttack(
        ref StateFingerprintBuilder fingerprint,
        char marker,
        Dictionary<Player, PredictedCard>? cards)
    {
        if (cards == null || cards.Count == 0)
            return;
        // Every state key visits this. Single-player combat tracks at most one player, and a
        // single entry is already in NetId order, so only larger tables need the stable sort.
        if (cards.Count == 1)
        {
            foreach ((Player player, PredictedCard card) in cards)
                AppendTrackedAttackEntry(ref fingerprint, marker, player, card);
            return;
        }
        foreach ((Player player, PredictedCard card) in cards.OrderBy(entry => entry.Key.NetId))
            AppendTrackedAttackEntry(ref fingerprint, marker, player, card);
    }

    private static void AppendTrackedAttackEntry(
        ref StateFingerprintBuilder fingerprint,
        char marker,
        Player player,
        PredictedCard card)
    {
        fingerprint.Add(marker);
        fingerprint.Add((long)player.NetId);
        fingerprint.Add(card.Preview.Id.Entry);
        fingerprint.Add(card.Preview.CurrentUpgradeLevel);
    }
}
