using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct CardPlayPowerSuppression(
    Creature Owner,
    int? PhantomBlades,
    int? Lethality,
    int? Unmovable);

internal sealed partial class SimulatedCombatState
{
    private sealed class HistorySensitiveCardModifierScope(
        SimulatedCombatState owner,
        CardPlayPowerSuppression suppression) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner.RestoreHistorySensitiveCardModifiers(suppression);
        }
    }

    IDisposable ICombatPredictionCardExecutionSink.BeginHistorySensitiveCardModifierScope(
        PredictedCard card)
        => new HistorySensitiveCardModifierScope(
            this,
            SuppressHistorySensitiveCardModifiers(card));

    public CardPlayPowerSuppression SuppressHistorySensitiveCardModifiers(PredictedCard card)
    {
        Creature owner = card.Preview.Owner.Creature;
        int? phantomBlades = null;
        int? lethality = null;
        int? unmovable = null;

        int phantomAmount = GetAmount<PhantomBladesPower>(owner);
        if (phantomAmount > 0
            && card.Preview.Tags.Contains(CardTag.Shiv)
            && GetShivsPlayedThisTurn(owner) > 0)
        {
            phantomBlades = phantomAmount;
            SetAmount<PhantomBladesPower>(owner, 0);
        }

        int lethalityAmount = GetAmount<LethalityPower>(owner);
        if (lethalityAmount > 0
            && card.Preview.Type == CardType.Attack
            && GetAttacksPlayedThisTurn(owner) > 0)
        {
            lethality = lethalityAmount;
            SetAmount<LethalityPower>(owner, 0);
        }

        int unmovableAmount = GetAmount<UnmovablePower>(owner);
        if (unmovableAmount > 0 && GetBlockCardsPlayedThisTurn(owner) >= unmovableAmount)
        {
            unmovable = unmovableAmount;
            SetAmount<UnmovablePower>(owner, 0);
        }

        return new CardPlayPowerSuppression(owner, phantomBlades, lethality, unmovable);
    }

    public void RestoreHistorySensitiveCardModifiers(CardPlayPowerSuppression suppression)
    {
        if (suppression.PhantomBlades is { } phantomBlades)
            SetAmount<PhantomBladesPower>(suppression.Owner, phantomBlades);
        if (suppression.Lethality is { } lethality)
            SetAmount<LethalityPower>(suppression.Owner, lethality);
        if (suppression.Unmovable is { } unmovable)
            SetAmount<UnmovablePower>(suppression.Owner, unmovable);
    }

    public void RecordCardPlayed(PredictedCard card, bool gainedBlock)
    {
        RecordHistoryCourseAttack(card);
        Creature owner = card.Preview.Owner.Creature;
        if (card.Preview.Type == CardType.Attack)
        {
            (_attacksPlayedThisTurn ??= [])[owner] = GetAttacksPlayedThisTurn(owner) + 1;
            if (card.Preview.Tags.Contains(CardTag.Shiv))
                (_shivsPlayedThisTurn ??= [])[owner] = GetShivsPlayedThisTurn(owner) + 1;
        }
        if (gainedBlock)
            (_blockCardsPlayedThisTurn ??= [])[owner] = GetBlockCardsPlayedThisTurn(owner) + 1;

        foreach (Creature creature in Creatures)
        {
            SlowPower? slow = GetPower<SlowPower>(creature);
            if (slow == null || slow.Amount <= 0)
                continue;
            SlowPower mutable = (SlowPower)GetOrCreatePower(
                creature,
                CanonicalModels.Power<SlowPower>(),
                slow.Applier);
            mutable.DynamicVars["SlowAmount"].BaseValue++;
            mutable.DynamicVars["DisplayAmount"].BaseValue =
                mutable.DynamicVars["SlowAmount"].BaseValue * 10;
        }
    }

    public void InitializeFeralAfterApplied(
        CombatPredictionSimulator simulator,
        Creature owner)
    {
        FeralPower power = GetPower<FeralPower>(owner)
            ?? throw new InvalidOperationException("野性状态施加后未找到对应 Power。");
        simulator.StateStore
            .Get(power, () => new FeralPredictionState(power))
            .ZeroCostAttacksPlayed = GetZeroCostAttackStartsThisTurn(owner);
    }

    public void InitializeJugglingAfterApplied(
        CombatPredictionSimulator simulator,
        Creature owner)
    {
        JugglingPower power = GetPower<JugglingPower>(owner)
            ?? throw new InvalidOperationException("杂耍状态施加后未找到对应 Power。");
        simulator.StateStore
            .Get(power, () => new JugglingPredictionState(power))
            .AttacksPlayedThisTurn = GetAttacksPlayedThisTurn(owner);
    }

    public int GetAttacksPlayedThisTurn(Creature owner)
    {
        if (_attacksPlayedThisTurn?.TryGetValue(owner, out int value) == true)
            return value;
        value = _rootHistory.CardPlaysStarted.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.CardPlay.Card.Type == CardType.Attack
            && entry.CardPlay.Player.Creature == owner);
        (_attacksPlayedThisTurn ??= [])[owner] = value;
        return value;
    }

    public int GetShivsPlayedThisTurn(Creature owner)
    {
        if (_shivsPlayedThisTurn?.TryGetValue(owner, out int value) == true)
            return value;
        value = _rootHistory.CardPlaysFinished.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.CardPlay.Card.Tags.Contains(CardTag.Shiv)
            && entry.CardPlay.Player.Creature == owner);
        (_shivsPlayedThisTurn ??= [])[owner] = value;
        return value;
    }

    private int GetBlockCardsPlayedThisTurn(Creature owner)
    {
        if (_blockCardsPlayedThisTurn?.TryGetValue(owner, out int value) == true)
            return value;
        value = _rootHistory.BlockGained.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.CardPlay?.Player.Creature == owner
            && entry.Props.IsCardOrMonsterMove());
        (_blockCardsPlayedThisTurn ??= [])[owner] = value;
        return value;
    }
}
