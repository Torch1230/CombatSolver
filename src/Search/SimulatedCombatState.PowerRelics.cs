using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private sealed class CardPowerApplicationScope(
        SimulatedCombatState owner,
        CardModel? card) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner.CompleteCardPowerApplication(card);
        }
    }

    private static readonly FieldInfo RuinedHelmetUsedField =
        typeof(RuinedHelmet).GetField("_usedThisCombat", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(RuinedHelmet).FullName, "_usedThisCombat");
    private static readonly FieldInfo UnsettlingLampFinishedField =
        typeof(UnsettlingLamp).GetField("_isFinishedTriggering", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(UnsettlingLamp).FullName, "_isFinishedTriggering");

    private List<CardModel?>? _powerCardSources;
    private Dictionary<UnsettlingLamp, CardModel>? _unsettlingLampTriggeringCards;
    private Dictionary<UnsettlingLamp, HashSet<Type>>? _unsettlingLampInternalPowerTypes;

    private CardModel? CurrentPowerCardSource => _powerCardSources is { Count: > 0 }
        ? _powerCardSources[^1]
        : null;

    public void BeginCardPowerApplication(CardModel? card)
    {
        (_powerCardSources ??= []).Add(card);
    }

    IDisposable ICombatPredictionCardExecutionSink.BeginCardPowerApplication(PredictedCard card)
    {
        BeginCardPowerApplication(card.Preview);
        return new CardPowerApplicationScope(this, card.Preview);
    }

    public void CompleteCardPowerApplication(CardModel? card)
    {
        if (!ReferenceEquals(CurrentPowerCardSource, card))
            throw new InvalidOperationException($"结束了未开始的卡牌 Power 结算：{card?.Id.Entry}。");
        if (_unsettlingLampTriggeringCards != null)
        {
            UnsettlingLamp[] completedLamps = _unsettlingLampTriggeringCards
                .Where(pair => ReferenceEquals(pair.Value, card))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (UnsettlingLamp lamp in completedLamps)
            {
                SetStatefulRelicState(lamp, new StatefulRelicState(1, 0));
                _unsettlingLampTriggeringCards.Remove(lamp);
                _unsettlingLampInternalPowerTypes?.Remove(lamp);
            }
        }
        _powerCardSources!.RemoveAt(_powerCardSources.Count - 1);
    }

    public void ApplyPowerFromSource(Type powerType, Creature target, int amount, Creature? applier, CardModel? cardSource)
    {
        BeginCardPowerApplication(cardSource);
        using var scope = new CardPowerApplicationScope(this, cardSource);
        ApplyPower(powerType, target, amount, applier);
    }

    public int ModifyPowerAmountForRelics(
        PowerModel power,
        Creature target,
        int amount,
        Creature? applier)
    {
        ObserveUnsettlingLamp(power, target, amount, applier);
        if (applier != null)
        {
            amount = PersistentRelicSupport.ModifyPowerAmountGiven(
                this,
                power,
                applier,
                target,
                amount,
                CurrentPowerCardSource);
        }
        amount = ModifyPowerAmountForUnsettlingLamp(power, amount);
        return ModifyPowerAmountForRuinedHelmet(power, target, amount);
    }

    private void ObserveUnsettlingLamp(
        PowerModel power,
        Creature target,
        int amount,
        Creature? applier)
    {
        CardModel? cardSource = CurrentPowerCardSource;
        if (cardSource == null
            || applier == null
            || !power.IsVisible
            || power.GetTypeForAmount(amount) != PowerType.Debuff
            || GetAmount<ArtifactPower>(target) > 0)
        {
            return;
        }

        IEnumerable<UnsettlingLamp> lamps = applier.Player is { } player
            ? RelicsOf(player).OfType<UnsettlingLamp>().Where(static relic => !relic.IsMelted)
            : [];
        foreach (UnsettlingLamp lamp in lamps)
        {
            if (!ReferenceEquals(lamp.Owner.Creature, applier)
                || target.Side == lamp.Owner.Creature.Side
                || GetStatefulRelicState(lamp).Current != 0
                || _unsettlingLampTriggeringCards?.ContainsKey(lamp) == true)
            {
                continue;
            }

            (_unsettlingLampTriggeringCards ??= []).Add(lamp, cardSource);
            if (power is ITemporaryPower temporary)
            {
                Dictionary<UnsettlingLamp, HashSet<Type>> internalTypes =
                    _unsettlingLampInternalPowerTypes ??= [];
                if (!internalTypes.TryGetValue(lamp, out HashSet<Type>? types))
                    internalTypes.Add(lamp, types = []);
                types.Add(temporary.InternallyAppliedPower.GetType());
            }
        }
    }

    private int ModifyPowerAmountForUnsettlingLamp(PowerModel power, int amount)
    {
        CardModel? cardSource = CurrentPowerCardSource;
        if (cardSource == null
            || _unsettlingLampTriggeringCards is not { Count: > 0 }
            || power.GetTypeForAmount(amount) != PowerType.Debuff)
        {
            return amount;
        }
        foreach ((UnsettlingLamp lamp, CardModel triggeringCard) in _unsettlingLampTriggeringCards)
        {
            if (ReferenceEquals(triggeringCard, cardSource)
                && _unsettlingLampInternalPowerTypes?.GetValueOrDefault(lamp)?.Contains(power.GetType()) != true)
            {
                amount *= 2;
            }
        }
        return amount;
    }

    private int ModifyPowerAmountForRuinedHelmet(PowerModel power, Creature target, int amount)
    {
        if (power is not StrengthPower || amount <= 0 || target.Player is not { } player)
            return amount;
        foreach (RuinedHelmet helmet in RelicsOf(player)
                     .OfType<RuinedHelmet>()
                     .Where(static relic => !relic.IsMelted))
        {
            if (GetStatefulRelicState(helmet).Current != 0)
                continue;
            amount *= 2;
            SetStatefulRelicState(helmet, new StatefulRelicState(1, 0));
        }
        return amount;
    }

    private static bool CaptureRuinedHelmetUsed(RuinedHelmet helmet)
        => (bool)RuinedHelmetUsedField.GetValue(helmet)!;

    private static bool CaptureUnsettlingLampFinished(UnsettlingLamp lamp)
        => (bool)UnsettlingLampFinishedField.GetValue(lamp)!;
}
