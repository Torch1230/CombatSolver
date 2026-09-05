using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private List<PowerModel>? _addedPowerInstances;
    private Dictionary<NightmarePower, PredictedCard>? _nightmareSelections;
    private ForkableDictionary<Player, Creature>? _simulatedOsties;
    private ForkableDictionary<Creature, int>? _simulatedOstyMaxHp;
    private HashSet<PredictedCard>? _returnToHandNextTurn;
    private ForkableDictionary<Creature, int>? _cardsPlayedThisTurn;
    private ForkableDictionary<Creature, int>? _manualCardsPlayedThisTurn;
    private ForkableSet<CardModel>? _fetchCardsPlayedThisTurn;
    private ISet<uint>? _activeCardExecutionDeaths;
    private int _cardExecutionScopeDepth;
    private bool _playerTurnEndRequested;

    private sealed class CardExecutionScope(SimulatedCombatState owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner.EndCardExecutionScope();
        }
    }

    public T AddPowerInstance<T>(Creature owner, int amount, Creature? applier = null)
        where T : PowerModel
    {
        T power = (T)CanonicalModels.Power<T>().ToMutable();
        power._owner = owner;
        power._applier = applier;
        power._target = owner;
        power._amount = amount;
        (_addedPowerInstances ??= []).Add(power);
        InvalidateHookListeners();
        return power;
    }

    public void SetNightmareSelection(NightmarePower power, PredictedCard selected)
        => (_nightmareSelections ??= [])[power] = selected;

    public void SummonOsty(CombatPredictionSimulator simulator, Player player, int amount)
    {
        if (amount <= 0)
            return;
        Creature? existingOsty = GetOsty(player);
        bool created = existingOsty == null;
        Creature osty;
        if (created)
        {
            Osty model = (Osty)CanonicalModels.Monster<Osty>().ToMutable();
            osty = CreatePredictedMonster(simulator, model, player.Creature.Side, slot: null);
            osty.PetOwner = player;
            AddPredictedMonster(osty);
            (_simulatedOsties ??= [])[player] = osty;
            Apply<DieForYouPower>(osty, 1);
        }
        else
        {
            osty = existingOsty!;
        }
        SimCreatureState state = simulator.State.GetCreature(osty);
        int currentMax = _simulatedOstyMaxHp?.GetValueOrDefault(osty) ?? state.MaxHp;
        if (!created && state.IsAlive)
        {
            currentMax += amount;
            state.SetMaxHp(currentMax);
            state.CurrentHp = Math.Min(currentMax, state.CurrentHp + amount);
        }
        else
        {
            currentMax = amount;
            state.SetMaxHp(currentMax);
            state.CurrentHp = amount;
        }
        (_simulatedOstyMaxHp ??= [])[osty] = currentMax;
    }

    public void HealOsty(CombatPredictionSimulator simulator, Player player, int amount)
    {
        Creature osty = GetOsty(player)
            ?? throw new InvalidOperationException("治疗奥斯蒂时，玩家没有可供模拟的奥斯蒂实例。");
        SimCreatureState state = simulator.State.GetCreature(osty);
        int maxHp = _simulatedOstyMaxHp?.GetValueOrDefault(osty) ?? state.MaxHp;
        state.CurrentHp = Math.Min(maxHp, state.CurrentHp + Math.Max(0, amount));
    }

    public int GetOstyMaxHp(CombatPredictionSimulator simulator, Player player)
    {
        Creature? osty = GetOsty(player);
        if (osty == null)
            return 0;
        return _simulatedOstyMaxHp?.GetValueOrDefault(osty)
            ?? simulator.State.GetCreature(osty).MaxHp;
    }

    public bool IsOstyHittable(CombatPredictionSimulator simulator, Player player)
    {
        Creature? osty = GetOsty(player);
        if (osty == null)
            return false;
        SimCreatureState state = simulator.State.GetCreature(osty);
        return state.IsAlive && (_rootDeadCreatures.Contains(osty) || simulator.State.IsHittable(osty));
    }

    public Creature? GetOsty(Player player)
        => _simulatedOsties?.GetValueOrDefault(player) ?? player.Osty;

    public void RecordCardLifecycle(CombatPredictionSimulator simulator, PredictedCard card)
    {
        Creature owner = card.Preview.Owner.Creature;
        (_cardsPlayedThisTurn ??= [])[owner] = GetCardsPlayedThisTurn(owner) + 1;
        if (card.Preview is Fetch)
            GetFetchCardsPlayedThisTurn().Add(card.Original);
        RecordRelicCardPlayed(simulator, card.Preview.Owner, card.Preview);
        if (card.Preview.Type == CardType.Skill)
            RecordSkillPlayed(owner);
        if (ReturnsToHandAfterPlaying(card.Preview))
            (_returnToHandNextTurn ??= []).Add(card);
    }

    private static bool ReturnsToHandAfterPlaying(CardModel card)
        => card is Bolas or ThrummingHatchet;

    private void CaptureReturningCardEligibility(CombatPredictionSimulator simulator)
    {
        // MaterializeRoot is a synchronous main-thread capture. A supported Start root
        // is captured before SetupPlayerTurn, so last turn's returns are still due now.
        // Play roots must not rearm those already-consumed returns; current-turn plays
        // remain eligible for the next turn in either entry path.
        foreach (var entry in _rootHistory.CardPlaysFinished)
        {
            Player owner = entry.CardPlay.Player;
            bool awaitingTurnSetup = owner.PlayerCombatState?.Phase == PlayerTurnPhase.Start;
            if ((entry.HappenedThisTurn(this)
                    || awaitingTurnSetup && entry.HappenedLastPlayerTurn(owner))
                && ReturnsToHandAfterPlaying(entry.CardPlay.Card)
                && simulator.State.FindCard(entry.CardPlay.Card) is { } card)
            {
                (_returnToHandNextTurn ??= []).Add(card);
            }
        }
    }

    public IDisposable BeginCardExecutionScope(ISet<uint>? processedEnemyDeaths = null)
    {
        if (_cardExecutionScopeDepth == 0)
        {
            _activeCardExecutionDeaths = processedEnemyDeaths ?? new HashSet<uint>();
        }
        else if (processedEnemyDeaths != null
                 && !ReferenceEquals(_activeCardExecutionDeaths, processedEnemyDeaths))
        {
            throw new InvalidOperationException("嵌套出牌尝试替换正在使用的死亡处理集合。");
        }
        _cardExecutionScopeDepth++;
        return new CardExecutionScope(this);
    }

    IDisposable ICombatPredictionCardExecutionSink.BeginCardExecutionScope()
        => BeginCardExecutionScope();

    void ICombatPredictionCardExecutionSink.RecordCardPlayStarted(PredictedCard card, CardPlay cardPlay)
    {
        if (!cardPlay.IsFirstInSeries)
            return;
        Creature owner = card.Preview.Owner.Creature;
        (_cardPlaysStartedThisTurn ??= [])[owner] = GetCardPlaysStartedThisTurn(owner) + 1;
        if (card.Preview.Type == CardType.Attack && cardPlay.Resources.EnergyValue == 0)
        {
            (_zeroCostAttackStartsThisTurn ??= [])[owner] =
                GetZeroCostAttackStartsThisTurn(owner) + 1;
        }
        if (!cardPlay.IsAutoPlay)
        {
            (_manualCardsPlayedThisTurn ??= [])[owner] = GetManualCardsPlayedThisTurn(owner) + 1;
        }
    }

    public int GetZeroCostAttackStartsThisTurn(Creature owner)
    {
        if (_zeroCostAttackStartsThisTurn?.TryGetValue(owner, out int value) == true)
            return value;
        value = _rootHistory.CardPlaysStarted.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.CardPlay.Player.Creature == owner
            && entry.CardPlay.Card.Type == CardType.Attack
            && entry.CardPlay.Resources.EnergyValue == 0);
        (_zeroCostAttackStartsThisTurn ??= [])[owner] = value;
        return value;
    }

    void ICombatPredictionCardExecutionSink.ApplyCardPlayEffects(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        CardPlay cardPlay,
        Creature? target,
        int ownerBlockBefore,
        decimal cardBlockGained,
        int historyEntryStart)
    {
        ISet<uint> processedEnemyDeaths = _activeCardExecutionDeaths ?? new HashSet<uint>();
        if (!CorePowerSupport.ApplyCardPowers(
                simulator,
                this,
                card,
                cardPlay,
                target,
                ownerBlockBefore,
                cardBlockGained,
                historyEntryStart,
                processedEnemyDeaths))
        {
            return;
        }
        _ = CorePowerSupport.ApplyEnemyDeathPowers(
            simulator,
            this,
            KnownEnemies,
            processedEnemyDeaths);
    }

    void ICombatPredictionCardExecutionSink.CompleteCardPlayEffects(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        int ownerBlockBefore,
        int historyEntryStart)
    {
        TriggeredPowerSupport.CompensateHistorySince(simulator, this, historyEntryStart);
        if (HasPendingChoice)
            return;
        simulator.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, this);
        if (HasPendingChoice)
            return;
        int ownerBlockAfter = simulator.State.GetCreature(card.Preview.Owner.Creature).Block;
        RecordCardPlayed(card, ownerBlockAfter > ownerBlockBefore);
        RecordCardLifecycle(simulator, card);
    }

    void ICombatPredictionCardExecutionSink.CompleteCardExecution(
        CombatPredictionSimulator simulator)
    {
        ISet<uint> processedEnemyDeaths = _activeCardExecutionDeaths ?? new HashSet<uint>();
        if (!CorePowerSupport.ApplyEnemyDeathPowers(
                simulator,
                this,
                KnownEnemies,
                processedEnemyDeaths))
        {
            return;
        }
        simulator.SynchronizePowerAmountPredictionStates();
        PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, this);
    }

    void ICombatPredictionEnemyDeathSink.ResolvePendingEnemyDeaths(
        CombatPredictionSimulator simulator,
        ISet<uint> processedEnemyDeaths)
    {
        _ = CorePowerSupport.ApplyEnemyDeathPowers(
            simulator,
            this,
            KnownEnemies,
            _activeCardExecutionDeaths ?? processedEnemyDeaths);
    }

    private void EndCardExecutionScope()
    {
        if (_cardExecutionScopeDepth <= 0)
            throw new InvalidOperationException("出牌作用域计数失衡。");
        _cardExecutionScopeDepth--;
        if (_cardExecutionScopeDepth == 0)
            _activeCardExecutionDeaths = null;
    }

    public bool PlayerTurnEndRequested => _playerTurnEndRequested;

    public void RequestPlayerTurnEnd()
        => _playerTurnEndRequested = true;

    public bool ConsumePlayerTurnEndRequest()
    {
        bool requested = _playerTurnEndRequested;
        _playerTurnEndRequested = false;
        return requested;
    }

    public MonologuePower[] CapturePendingMonologues(Creature owner)
    {
        return EffectivePowers()
            .OfType<MonologuePower>()
            .Where(power => power.Amount > 0 && ReferenceEquals(power.Owner, owner))
            .ToArray();
    }

    public void ResolveMonologues(Creature owner, IReadOnlyList<MonologuePower> powers)
    {
        foreach (MonologuePower power in powers)
        {
            int strength = power.DynamicVars.Strength.IntValue;
            Apply<StrengthPower>(owner, strength, owner);
            MonologuePower mutable = (MonologuePower)GetMutablePowerInstance(power);
            mutable.DynamicVars[MonologuePower.strengthAppliedKey].BaseValue += strength;
        }
    }

    public void ResetHellraiserTurn(CombatPredictionSimulator simulator, HellraiserPower power)
        => simulator.StateStore
            .Get(power, () => new HellraiserPredictionState(power))
            .InfiniteAutoPlaysThisTurn = 0;

    public void ResetPanacheTurn(CombatPredictionSimulator simulator, PanachePower power)
    {
        PanachePower mutable = (PanachePower)GetMutablePowerInstance(power);
        simulator.StateStore.RemapModel(power, mutable);
        mutable.DynamicVars["CardsLeft"].BaseValue = 5;
        simulator.StateStore.Get(power, () => new PanachePredictionState(power)).CardsLeft = 5;
    }

    public void SynchronizePanacheState(CombatPredictionSimulator simulator, Creature owner)
    {
        foreach (PanachePower power in EffectivePowers()
                     .OfType<PanachePower>()
                     .Where(candidate => candidate.Amount > 0 && ReferenceEquals(candidate.Owner, owner))
                     .ToArray())
        {
            int cardsLeft = simulator.StateStore
                .GetReadOnly(power, () => new PanachePredictionState(power))
                .CardsLeft;
            PanachePower mutable = (PanachePower)GetMutablePowerInstance(power);
            simulator.StateStore.RemapModel(power, mutable);
            mutable.DynamicVars["CardsLeft"].BaseValue = cardsLeft;
        }
    }

    public void ResetSkittishTurn(CombatPredictionSimulator simulator, SkittishPower power)
        => simulator.StateStore
            .Get(power, () => new SkittishPredictionState(power))
            .HasGainedBlockThisTurn = false;

    public bool IsCardPlayPrevented(CombatPredictionSimulator simulator, PredictedCard card)
    {
        SimPlayerCombatState player = simulator.State.GetPlayerCombatState(card.Preview.Owner);
        if (player.Hand.Cards.Any(candidate => candidate.Preview is Enthralled)
            && card.Preview is not Enthralled)
        {
            return true;
        }
        return player.Hand.Cards.Any(candidate => candidate.Preview is Normality)
            && GetCardsPlayedThisTurn(card.Preview.Owner.Creature) >= 3;
    }

    public bool PrepareBeforeHandDraw(
        CombatPredictionSimulator simulator,
        Player player,
        TurnStartChoiceCursor choices)
    {
        // BeforeHandDraw snapshots its listeners from the current ordered combat piles
        // before invoking powers or relics. The set records eligibility only: its free-slot
        // history changes on Fork and must never determine the order of moves to Hand.
        PredictedCard[] returningCards = _returnToHandNextTurn is { Count: > 0 }
            ? simulator.State.GetPlayerCombatState(player).AllCards
                .Where(card => _returnToHandNextTurn.Contains(card))
                .ToArray()
            : [];
        if (TurnStartPowerSupport.TriggerBeforeHandDraw(simulator, this, player, choices))
            return true;
        if (PrepareRelicsBeforeHandDraw(simulator, player, choices))
            return true;

        if (_nightmareSelections != null)
        {
            foreach ((NightmarePower power, PredictedCard selected) in _nightmareSelections.ToArray())
            {
                if (power.Amount <= 0 || !ReferenceEquals(power.Owner.Player, player))
                    continue;
                List<PredictedCard> copies = new(power.Amount);
                for (int index = 0; index < power.Amount; index++)
                {
                    PredictedCard copy = selected.CreateClone();
                    copy.ClearAffliction();
                    copies.Add(copy);
                }
                simulator.AddGeneratedCardsToCombat(
                    copies,
                    PileType.Hand,
                    player,
                    CardPilePosition.Bottom,
                    CardGenerationResultKind.Fixed);
                if (HasPendingChoice)
                    return true;
                SetPowerAmount(power, 0);
            }
        }

        foreach (PowerModel power in EffectivePowers().ToArray())
        {
            if (power.Amount <= 0 || !ReferenceEquals(power.Owner.Player, player))
                continue;
            CardModel? canonical = power switch
            {
                InfiniteBladesPower => CanonicalModels.Card<Shiv>(),
                SentryModePower => CanonicalModels.Card<SweepingGaze>(),
                _ => null,
            };
            if (canonical == null)
                continue;
            List<PredictedCard> generated = new(power.Amount);
            for (int index = 0; index < power.Amount; index++)
                generated.Add(PredictedCard.Create(canonical, player));
            simulator.AddGeneratedCardsToCombat(
                generated,
                PileType.Hand,
                player,
                CardPilePosition.Bottom,
                CardGenerationResultKind.Fixed);
            if (HasPendingChoice)
                return true;
        }

        if (_returnToHandNextTurn != null)
        {
            foreach (PredictedCard card in returningCards)
            {
                SimCardPile? pile = card.GetPile(simulator.State);
                if (card.Preview.HasBeenRemovedFromState)
                {
                    _returnToHandNextTurn.Remove(card);
                    continue;
                }
                if (pile?.Type != PileType.Hand)
                {
                    simulator.AddToPile(card, PileType.Hand);
                    if (HasPendingChoice)
                        return true;
                }
                _returnToHandNextTurn.Remove(card);
            }
        }
        return false;
    }

    public bool PrepareBeforeHandDraw(CombatPredictionSimulator simulator, Player player)
        => PrepareBeforeHandDraw(simulator, player, new TurnStartChoiceCursor(null));

    public bool TriggerAutoPrePlayEarly(
        CombatPredictionSimulator simulator,
        Player player,
        int turnNumber,
        TurnStartChoiceCursor choices,
        ISet<uint> processedEnemyDeaths)
    {
        PredictedCard[] bombardments = simulator.State.GetPlayerCombatState(player)
            .ExhaustPile.Cards
            .Where(card => card.Preview is Bombardment)
            .ToArray();
        for (int index = 0; index < bombardments.Length; index++)
        {
            PredictedCard card = bombardments[index];
            if (!AutoPlayWithChoice(
                    simulator,
                    card,
                    card.Preview.Id.Entry,
                    $"{card.Preview.Id.Entry}+{card.Preview.CurrentUpgradeLevel}#{index}",
                    choices,
                    processedEnemyDeaths))
            {
                return true;
            }
        }
        if (TriggerScheduledAutoPlays(
                simulator,
                player,
                turnNumber,
                choices,
                processedEnemyDeaths))
            return true;
        if (EnchantmentLifecycleSupport.TriggerAutoPrePlay(
                simulator,
                this,
                player,
                turnNumber,
                choices,
                processedEnemyDeaths))
        {
            return true;
        }
        return !TriggerWhisperingEarring(
            simulator,
            player,
            turnNumber,
            processedEnemyDeaths);
    }

    public bool TriggerAutoPrePlayEarly(
        CombatPredictionSimulator simulator,
        Player player,
        int turnNumber,
        ISet<uint> processedEnemyDeaths)
        => TriggerAutoPrePlayEarly(
            simulator,
            player,
            turnNumber,
            new TurnStartChoiceCursor(null),
            processedEnemyDeaths);

    public int GetCardsPlayedThisTurn(Creature owner)
    {
        if (_cardsPlayedThisTurn?.TryGetValue(owner, out int value) == true)
            return value;
        value = _rootHistory.CardPlaysStarted.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.CardPlay.IsFirstInSeries
            && entry.CardPlay.Player.Creature == owner);
        (_cardsPlayedThisTurn ??= [])[owner] = value;
        return value;
    }

    public int GetManualCardsPlayedThisTurn(Creature owner)
    {
        if (_manualCardsPlayedThisTurn?.TryGetValue(owner, out int value) == true)
            return value;
        value = _rootHistory.CardPlaysStarted.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.CardPlay.IsFirstInSeries
            && !entry.CardPlay.IsAutoPlay
            && entry.CardPlay.Player.Creature == owner);
        (_manualCardsPlayedThisTurn ??= [])[owner] = value;
        return value;
    }

    public int GetCardPlaysStartedThisTurn(Creature owner)
    {
        if (_cardPlaysStartedThisTurn?.TryGetValue(owner, out int value) == true)
            return value;
        value = _rootHistory.CardPlaysStarted.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.CardPlay.Player.Creature == owner);
        (_cardPlaysStartedThisTurn ??= [])[owner] = value;
        return value;
    }

    public bool WasFetchPlayedThisTurn(PredictedCard card)
    {
        if (card.Preview is not Fetch)
            throw new ArgumentException($"Card {card.Preview.Id.Entry} is not Fetch.", nameof(card));
        return GetFetchCardsPlayedThisTurn().Contains(card.Original);
    }

    private ForkableSet<CardModel> GetFetchCardsPlayedThisTurn()
        => _fetchCardsPlayedThisTurn ??= new ForkableSet<CardModel>(
            _rootHistory.CardPlaysFinished
                .Where(entry => entry.HappenedThisTurn(this) && entry.CardPlay.Card is Fetch)
                .Select(entry => entry.CardPlay.Card));

    private void ResetCardLifecycleTurn(Creature owner)
    {
        (_cardsPlayedThisTurn ??= [])[owner] = 0;
        (_manualCardsPlayedThisTurn ??= [])[owner] = 0;
        _fetchCardsPlayedThisTurn?.Clear();
        ResetPowerLifecycleTurn(owner);
    }

    private void AppendCardLifecycleFingerprint(
        ref StateFingerprintBuilder fingerprint,
        CombatPredictionSimulator simulator)
    {
        AddCreatureIntMap(ref fingerprint, 'c', _cardsPlayedThisTurn);
        AddCreatureIntMap(ref fingerprint, 'm', _manualCardsPlayedThisTurn);
        AddCreatureIntMap(ref fingerprint, 'o', _simulatedOstyMaxHp);

        ulong first = 0;
        ulong second = 0;
        int count = 0;
        if (_returnToHandNextTurn != null)
        {
            foreach (PredictedCard card in _returnToHandNextTurn)
            {
                StateFingerprintBuilder item = new();
                item.Add(card.Preview.Owner.NetId);
                item.Add(card.Preview.Id.Entry);
                item.Add(card.Preview.CurrentUpgradeLevel);
                SimCardPile? pile = card.GetPile(simulator.State);
                item.Add(pile?.Type.ToString());
                // Membership belongs to an instance in the ordered state, not merely
                // to a card name. Two equal-name cards can have different replay/cost
                // state, or sit on opposite sides of another returning listener.
                int position = -1;
                if (pile != null)
                {
                    for (int index = 0; index < pile.Cards.Count; index++)
                    {
                        if (!ReferenceEquals(pile.Cards[index], card))
                            continue;
                        position = index;
                        break;
                    }
                }
                else
                {
                    position = _registeredCombatCards?.IndexOf(card) ?? -1;
                }
                if (position < 0)
                    throw new InvalidOperationException("Returning card eligibility has no branch-owned card position.");
                item.Add(position);
                AddUnorderedItem(item.Finish(), ref first, ref second);
                count++;
            }
        }
        AddUnordered(ref fingerprint, 'r', count, first, second);

        first = 0;
        second = 0;
        count = 0;
        if (_fetchCardsPlayedThisTurn != null && _registeredCombatCards != null)
        {
            for (int index = 0; index < _registeredCombatCards.Count; index++)
            {
                PredictedCard card = _registeredCombatCards[index];
                if (!_fetchCardsPlayedThisTurn.Contains(card.Original))
                    continue;
                StateFingerprintBuilder item = new();
                item.Add(index);
                item.Add(card.Preview.Id.Entry);
                AddUnorderedItem(item.Finish(), ref first, ref second);
                count++;
            }
        }
        AddUnordered(ref fingerprint, 'f', count, first, second);

        first = 0;
        second = 0;
        count = 0;
        if (_nightmareSelections != null)
        {
            foreach ((NightmarePower power, PredictedCard selected) in _nightmareSelections)
            {
                if (power.Amount <= 0)
                    continue;
                CardModel card = selected.Preview;
                StateFingerprintBuilder item = new();
                item.Add(power.Owner.CombatId ?? uint.MaxValue);
                item.Add(power.Amount);
                item.Add(card.Id.Entry);
                item.Add(card.CurrentUpgradeLevel);
                item.Add(card.EnergyCost.GetWithModifiers(CostModifiers.Local));
                item.Add(card.CurrentStarCost);
                item.Add(card.BaseReplayCount);
                EnchantmentStateSupport.Append(ref item, card.Enchantment);
                item.Add(card.Affliction?.Id.Entry);
                item.Add(card.Affliction?.Amount ?? 0);
                AddUnorderedItem(item.Finish(), ref first, ref second);
                count++;
            }
        }
        AddUnordered(ref fingerprint, 'n', count, first, second);
    }
}
