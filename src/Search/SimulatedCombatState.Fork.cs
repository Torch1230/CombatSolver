using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    ICombatState ICombatPredictionForkableState.Fork(PredictionForkContext context)
    {
        AssertForkable();
        SimulatedCombatState fork = new(
            this,
            _allies.Fork(),
            _enemies.Fork(),
            _knownEnemies.Fork(),
            _escapedCreatures.Fork())
        {
            _drawNextTurn = _drawNextTurn?.Fork(),
            _skipNextDurationTick = _skipNextDurationTick?.Fork(),
            _skipNextMove = _skipNextMove?.Fork(),
            _pressureGunBonus = _pressureGunBonus?.Fork(),
            _steamEruptionDamage = _steamEruptionDamage?.Fork(),
            _steamEruptionPhases = _steamEruptionPhases?.Fork(),
            _aeonglassAdditionalStrength = _aeonglassAdditionalStrength?.Fork(),
            _aeonglassWitherUpgradeCount = _aeonglassWitherUpgradeCount?.Fork(),
            _nemesisShouldApplyIntangible = _nemesisShouldApplyIntangible?.Fork(),
            _tenderCardsPlayed = _tenderCardsPlayed?.Fork(),
            _attacksPlayedThisTurn = _attacksPlayedThisTurn?.Fork(),
            _shivsPlayedThisTurn = _shivsPlayedThisTurn?.Fork(),
            _blockCardsPlayedThisTurn = _blockCardsPlayedThisTurn?.Fork(),
            _skillCardsPlayedThisTurn = _skillCardsPlayedThisTurn?.Fork(),
            _cardsExhaustedThisTurn = _cardsExhaustedThisTurn?.Fork(),
            _doomAppliersThisTurn = _doomAppliersThisTurn?.Fork(),
            _unblockedDamageThisTurn = _unblockedDamageThisTurn?.Fork(),
            _cumulativeHpLost = _cumulativeHpLost?.Fork(),
            _poweredAttackHitsThisTurn = _poweredAttackHitsThisTurn?.Fork(),
            _cardsDiscardedThisTurn = _cardsDiscardedThisTurn?.Fork(),
            _creatureAttacksThisTurn = _creatureAttacksThisTurn?.Fork(),
            _energySpentThisTurn = _energySpentThisTurn?.Fork(),
            _starsGainedThisTurn = _starsGainedThisTurn?.Fork(),
            _nonHandDrawsThisTurn = _nonHandDrawsThisTurn?.Fork(),
            _statusCardsDrawnThisTurn = _statusCardsDrawnThisTurn?.Fork(),
            _cardPlaysStartedThisTurn = _cardPlaysStartedThisTurn?.Fork(),
            _zeroCostAttackStartsThisTurn = _zeroCostAttackStartsThisTurn?.Fork(),
            _enemiesIntendingAttack = _enemiesIntendingAttack?.Fork(),
            _hasPredictedEnemyIntents = _hasPredictedEnemyIntents,
            _playerTurnNumbers = _playerTurnNumbers?.Fork(),
            _knowledgeDemonCurseCounters = _knowledgeDemonCurseCounters?.Fork(),
            _monsterAiStates = _monsterAiStates?.Fork(),
            _monsterIntStates = _monsterIntStates?.Fork(),
            _dampenCasters = _dampenCasters?.Fork(),
            _deathPhases = _deathPhases?.Fork(),
            _stolenStrength = _stolenStrength?.Fork(),
            _stolenDexterity = _stolenDexterity?.Fork(),
            _nextCreatureId = _nextCreatureId,
            _roundNumber = _roundNumber,
            _currentSide = _currentSide,
            _battlewornDummyTimedOut = _battlewornDummyTimedOut,
            _rootMaterialized = _rootMaterialized,
            _statefulRelicStates = _statefulRelicStates?.Fork(),
            _simulatedOsties = _simulatedOsties?.Fork(),
            _simulatedOstyMaxHp = _simulatedOstyMaxHp?.Fork(),
            _cardsPlayedThisTurn = _cardsPlayedThisTurn?.Fork(),
            _manualCardsPlayedThisTurn = _manualCardsPlayedThisTurn?.Fork(),
            _fetchCardsPlayedThisTurn = _fetchCardsPlayedThisTurn?.Fork(),
            _simulatedPlayerGold = _simulatedPlayerGold?.Fork(),
            _liveCardsAtSnapshot = _liveCardsAtSnapshot?.Fork(),
            _swordSageCardsInitialized = _swordSageCardsInitialized,
            _skillsPlayedThisTurn = _skillsPlayedThisTurn?.Fork(),
            _potionSlots = _potionSlots?.Fork(),
            _potionUses = _potionUses?.Fork(),
            _outstandingStolenGold = _outstandingStolenGold,
            _outstandingStolenCards = _outstandingStolenCards,
            _longTermResourceValue = _longTermResourceValue,
            _angerCopiesGenerated = _angerCopiesGenerated,
        };

        if (_addedPowerInstances is not null)
        {
            List<PowerModel> addedPowerInstances = new(_addedPowerInstances.Count);
            foreach (PowerModel power in _addedPowerInstances)
                addedPowerInstances.Add(ForkPower(power, context));
            fork._addedPowerInstances = addedPowerInstances;
        }
        if (_rootMultiInstancePowerClones is not null)
        {
            fork._rootMultiInstancePowerClones = new(
                _rootMultiInstancePowerClones.Count,
                ReferenceEqualityComparer.Instance);
            foreach ((PowerModel source, PowerModel clone) in _rootMultiInstancePowerClones)
                fork._rootMultiInstancePowerClones.Add(source, context.RemapOrSelf(clone));
        }
        if (_powers is not null)
        {
            fork._powers = new Dictionary<(MegaCrit.Sts2.Core.Entities.Creatures.Creature Owner, Type Type), PowerModel>(
                _powers.Count,
                _powers.Comparer);
            foreach (((MegaCrit.Sts2.Core.Entities.Creatures.Creature owner, Type type), PowerModel power) in _powers)
                fork._powers.Add((owner, type), ForkPower(power, context));
        }
        if (_powerListenerOrder is not null)
            fork._powerListenerOrder = new List<(MegaCrit.Sts2.Core.Entities.Creatures.Creature Owner, Type Type)>(_powerListenerOrder);

        if (_nightmareSelections is not null)
        {
            fork._nightmareSelections = new(_nightmareSelections.Count, _nightmareSelections.Comparer);
            foreach ((var power, PredictedCard card) in _nightmareSelections)
                fork._nightmareSelections.Add(context.RemapOrSelf(power), ForkCard(card, context));
        }
        fork._returnToHandNextTurn = ForkCardSet(_returnToHandNextTurn, context);
        fork._swordSageReplayBonuses = ForkCardDictionary(_swordSageReplayBonuses, context);
        fork._powerAfflictionKnownCards = ForkCardSet(_powerAfflictionKnownCards, context);
        fork._dampenOriginalUpgrades = ForkDampenCards(context);
        fork._lastAttackThisTurn = ForkHistoryCourseCards(_lastAttackThisTurn, context);
        fork._lastAttackPreviousTurn = ForkHistoryCourseCards(_lastAttackPreviousTurn, context);
        if (_registeredCombatCards is not null)
        {
            // 观察者只往卡上写一个回调，不读也不写任何被 ForkCard 改动的状态，
            // 所以可以和分叉同一趟走完：卡的顺序、ForkCard 的调用序列都不变。
            List<PredictedCard> registeredCombatCards = new(_registeredCombatCards.Count);
            foreach (PredictedCard card in _registeredCombatCards)
            {
                PredictedCard forkedCard = ForkCard(card, context);
                registeredCombatCards.Add(forkedCard);
                fork.ObserveCardMutations(forkedCard);
            }
            fork._registeredCombatCards = registeredCombatCards;
        }
        fork._generatedCombatCards = ForkCardList(_generatedCombatCards, context);

        if (_orbitEnergyRemainders is not null)
        {
            fork._orbitEnergyRemainders = new(_orbitEnergyRemainders.Count, _orbitEnergyRemainders.Comparer);
            foreach ((var power, int amount) in _orbitEnergyRemainders)
                fork._orbitEnergyRemainders.Add(context.RemapOrSelf(power), amount);
        }
        if (_paleBlueDotActivated is not null)
        {
            fork._paleBlueDotActivated = new(_paleBlueDotActivated.Count, _paleBlueDotActivated.Comparer);
            foreach ((var power, bool active) in _paleBlueDotActivated)
                fork._paleBlueDotActivated.Add(context.RemapOrSelf(power), active);
        }

        RestoreHookListenerCaches(fork, context);
        context.Register(this, fork);
        return fork;
    }

    private void RestoreHookListenerCaches(
        SimulatedCombatState fork,
        PredictionForkContext context)
    {
        // CardModifier membership cannot change which Power models are active. Preserve this
        // independently invalidated projection even when listener arrays must stay conservative.
        if (_effectivePowers is not null)
            fork._effectivePowers = RemapCachedPowers(_effectivePowers, context);

        // BaseLib CardModifier membership lives in an opaque side table and can change
        // without invalidating these caches. Those roots always rebuild listeners on
        // enumeration, so remapping the cached arrays here only allocates short-lived
        // fork-local copies that can never be consumed.
        if (!CanReuseHookListenerCache)
            return;

        if (_baseHookListeners is not null)
            fork._baseHookListeners = RemapCachedModels(_baseHookListeners, context);

        if (_effectiveHookListeners is not null)
        {
            fork._effectiveHookListeners = ReferenceEquals(_effectiveHookListeners, _baseHookListeners)
                ? fork._baseHookListeners
                : RemapCachedModels(_effectiveHookListeners, context);
        }

        if (_effectiveRunHookListeners is not null)
        {
            if (ReferenceEquals(_effectiveRunHookListeners, _effectiveHookListeners))
            {
                fork._effectiveRunHookListeners = fork._effectiveHookListeners;
            }
            else if (_effectiveRunHookListeners is ConcatenatedListenerView view
                && ReferenceEquals(view.Suffix, _effectiveHookListeners)
                && fork._effectiveHookListeners is { } forkedSuffix)
            {
                // 逐元素重映射对拼接是可分配的：remap(前缀 ++ 后缀) == remap(前缀) ++ remap(后缀)。
                // 后缀就是刚刚重映射好的战斗监听表，前缀是根牌组快照（只含 CardModel/Enchantment，
                // 从不作为 Fork 源登记），两段都没变时连视图对象一起复用。
                IReadOnlyList<AbstractModel> forkedPrefix = RemapCachedModels(view.Prefix, context);
                fork._effectiveRunHookListeners =
                    ReferenceEquals(forkedPrefix, view.Prefix)
                        && ReferenceEquals(forkedSuffix, view.Suffix)
                        ? _effectiveRunHookListeners
                        : new ConcatenatedListenerView(forkedPrefix, forkedSuffix);
            }
            else
            {
                fork._effectiveRunHookListeners = RemapCachedModels(_effectiveRunHookListeners, context);
            }
        }

    }

    private static IReadOnlyList<AbstractModel> RemapCachedModels(
        IReadOnlyList<AbstractModel> source,
        PredictionForkContext context)
    {
        AbstractModel[]? remapped = null;
        for (int index = 0; index < source.Count; index++)
        {
            AbstractModel mapped = context.RemapOrSelf(source[index]);
            if (ReferenceEquals(mapped, source[index]))
                continue;
            remapped ??= source.ToArray();
            remapped[index] = mapped;
        }
        return remapped ?? source;
    }

    private static IReadOnlyList<PowerModel> RemapCachedPowers(
        IReadOnlyList<PowerModel> source,
        PredictionForkContext context)
    {
        PowerModel[]? remapped = null;
        for (int index = 0; index < source.Count; index++)
        {
            PowerModel mapped = context.RemapOrSelf(source[index]);
            if (ReferenceEquals(mapped, source[index]))
                continue;
            remapped ??= source.ToArray();
            remapped[index] = mapped;
        }
        return remapped ?? source;
    }

    public void AssertForkable()
    {
        if (_activeActionChoices is not null)
            throw new InvalidOperationException("Cannot fork during action choice resolution.");
        if (_cardExecutionScopeDepth != 0 || _activeCardExecutionDeaths is not null)
            throw new InvalidOperationException("Cannot fork during card execution.");
        if (_playerTurnEndRequested)
            throw new InvalidOperationException("Cannot fork before a requested player turn end is resolved.");
        if (_powerCardSources is { Count: > 0 })
            throw new InvalidOperationException("Cannot fork during card Power application.");
        if (PendingTurnStartChoice is not null)
            throw new InvalidOperationException($"Cannot fork with pending turn-start choice: {PendingTurnStartChoice.SourceId}.");
        if (PendingKnowledgeDemonChoice is not null)
            throw new InvalidOperationException(
                $"Cannot fork with pending Knowledge Demon choice: {PendingKnowledgeDemonChoice.SourceId}.");
        if (_pendingPowerAmountChanges is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"Cannot fork with pending Power amount changes: {DescribePendingPowerAmountChanges()}.");
        }
        if (_unsettlingLampTriggeringCards is { Count: > 0 }
            || _unsettlingLampInternalPowerTypes is { Count: > 0 })
        {
            throw new InvalidOperationException("Cannot fork during Unsettling Lamp resolution.");
        }
    }

    private static PowerModel ForkPower(PowerModel source, PredictionForkContext context)
    {
        if (context.TryRemap(source, out PowerModel? existing))
            return existing!;
        PowerModel fork = PredictionUtils.CloneModelForSimulation(source);
        fork._owner = source.Owner;
        fork._applier = source.Applier;
        fork._target = source.Target;
        fork._amount = source.Amount;
        context.Register(source, fork);
        return fork;
    }

    private static HashSet<PredictedCard>? ForkCardSet(
        HashSet<PredictedCard>? source,
        PredictionForkContext context)
    {
        if (source is null)
            return null;
        HashSet<PredictedCard> fork = new(source.Count, source.Comparer);
        foreach (PredictedCard card in source)
            fork.Add(ForkCard(card, context));
        return fork;
    }

    private static List<PredictedCard>? ForkCardList(
        List<PredictedCard>? source,
        PredictionForkContext context)
    {
        if (source is null)
            return null;
        List<PredictedCard> fork = new(source.Count);
        foreach (PredictedCard card in source)
            fork.Add(ForkCard(card, context));
        return fork;
    }

    private static Dictionary<PredictedCard, int>? ForkCardDictionary(
        Dictionary<PredictedCard, int>? source,
        PredictionForkContext context)
    {
        if (source is null)
            return null;
        Dictionary<PredictedCard, int> fork = new(source.Count, source.Comparer);
        foreach ((PredictedCard card, int value) in source)
            fork.Add(ForkCard(card, context), value);
        return fork;
    }

    private static PredictedCard ForkCard(PredictedCard source, PredictionForkContext context)
    {
        return context.TryRemap(source, out PredictedCard? existing)
            ? existing!
            : source.Fork(context);
    }
}
