using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct SimulatedPowerAmountChange(
    PowerModel Power,
    int Delta,
    Creature? Applier);

internal sealed partial class SimulatedCombatState
{
    private static readonly FieldInfo PowerInternalDataField =
        typeof(PowerModel).GetField("_internalData", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(PowerModel).FullName, "_internalData");

    private List<SimulatedPowerAmountChange>? _pendingPowerAmountChanges;
    private Dictionary<OrbitPower, int>? _orbitEnergyRemainders;
    private Dictionary<PaleBlueDotPower, bool>? _paleBlueDotActivated;
    private Dictionary<PredictedCard, int>? _swordSageReplayBonuses;
    private ForkableSet<CardModel>? _liveCardsAtSnapshot;
    private HashSet<PredictedCard>? _powerAfflictionKnownCards;
    private bool _swordSageCardsInitialized;
    private ForkableSet<Creature>? _skillsPlayedThisTurn;

    public void RecordPowerAmountChange(PowerModel power, int delta, Creature? applier)
    {
        if (delta != 0)
            (_pendingPowerAmountChanges ??= []).Add(new(power, delta, applier));
    }

    public SimulatedPowerAmountChange[] DrainPowerAmountChanges()
    {
        if (_pendingPowerAmountChanges is not { Count: > 0 })
            return [];
        SimulatedPowerAmountChange[] changes = _pendingPowerAmountChanges.ToArray();
        _pendingPowerAmountChanges.Clear();
        return changes;
    }

    private string DescribePendingPowerAmountChanges()
        => _pendingPowerAmountChanges is not { Count: > 0 }
            ? "none"
            : string.Join(',', _pendingPowerAmountChanges.Select(change =>
                $"{change.Power.Id.Entry}:{change.Delta}"));

    public int AdvanceOrbitEnergy(OrbitPower power, int energySpent)
    {
        int remainder;
        if (_orbitEnergyRemainders?.TryGetValue(power, out remainder) != true)
            remainder = (4 - power.DisplayAmount) % 4;
        int total = remainder + energySpent;
        (_orbitEnergyRemainders ??= [])[power] = total % 4;
        return total / 4;
    }

    public void InitializePaleBlueDot(PaleBlueDotPower power, bool activated)
        => (_paleBlueDotActivated ??= [])[power] = activated;

    public bool IsPaleBlueDotActivated(PaleBlueDotPower power)
    {
        if (_paleBlueDotActivated?.TryGetValue(power, out bool activated) == true)
            return activated;
        object data = PowerInternalDataField.GetValue(power)
            ?? throw new InvalidOperationException("苍蓝星球没有内部回合状态。");
        FieldInfo field = data.GetType().GetField(
            "alreadyActivatedThisTurn",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(data.GetType().FullName, "alreadyActivatedThisTurn");
        activated = (bool)field.GetValue(data)!;
        (_paleBlueDotActivated ??= [])[power] = activated;
        return activated;
    }

    public void SetPaleBlueDotActivated(PaleBlueDotPower power, bool activated)
        => (_paleBlueDotActivated ??= [])[power] = activated;

    public void RecordSkillPlayed(Creature owner)
    {
        (_skillsPlayedThisTurn ??= []).Add(owner);
        (_skillCardsPlayedThisTurn ??= [])[owner] = GetSkillCardsPlayedThisTurn(owner) + 1;
    }

    public int GetSkillCardsPlayedThisTurn(Creature owner)
    {
        if (_skillCardsPlayedThisTurn?.TryGetValue(owner, out int value) == true)
            return value;
        value = _rootHistory.CardPlaysStarted.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.CardPlay.Player.Creature == owner
            && entry.CardPlay.Card.Type == CardType.Skill);
        (_skillCardsPlayedThisTurn ??= [])[owner] = value;
        return value;
    }

    public bool HasPlayedSkillThisTurn(Creature owner)
        => _skillsPlayedThisTurn?.Contains(owner) == true;

    public void ResetPowerLifecycleTurn(Creature owner)
        => _skillsPlayedThisTurn?.Remove(owner);

    public int CountEtherealCardsInHand(CombatPredictionSimulator simulator, Player player)
        => simulator.State.GetPlayerCombatState(player).Hand.Cards.Count(card =>
            card.HasKeyword(simulator.State, CardKeyword.Ethereal));

    public void NormalizePowerCardState(CombatPredictionSimulator simulator)
    {
        NormalizePowerAfflictions(simulator);
        NormalizeSwordSageReplays(simulator);
    }

    private void CapturePowerAfflictionRootCards(CombatPredictionSimulator simulator)
    {
        if (_liveCardsAtSnapshot != null)
            throw new InvalidOperationException("Power affliction root cards were captured more than once.");
        _liveCardsAtSnapshot = new ForkableSet<CardModel>(Players
            .SelectMany(player => simulator.State.GetPlayerCombatState(player).AllCards)
            .Select(card => card.Original));
    }

    public void ClearSmogAfflictions(CombatPredictionSimulator simulator, Creature owner)
    {
        if (owner.Player is not { } player)
            return;
        foreach (PredictedCard card in simulator.State.GetPlayerCombatState(player).AllCards)
        {
            if (card.Preview.Affliction is Smog)
                card.ClearAffliction();
        }
    }

    private void NormalizePowerAfflictions(CombatPredictionSimulator simulator)
    {
        ForkableSet<CardModel> liveCardsAtSnapshot = _liveCardsAtSnapshot
            ?? throw new InvalidOperationException("Power affliction root cards were not captured.");
        IReadOnlyList<PowerModel> powers = EffectivePowers();
        int vitalSparkAmount = 0;
        for (int index = 0; index < powers.Count; index++)
        {
            if (powers[index] is VitalSparkPower { Amount: > 0 } vitalSpark)
                vitalSparkAmount = checked(vitalSparkAmount + vitalSpark.Amount);
        }
        bool hasVitalSpark = vitalSparkAmount > 0;
        IReadOnlyList<Player> players = Players;
        for (int playerIndex = 0; playerIndex < players.Count; playerIndex++)
        {
            Player player = players[playerIndex];
            Creature owner = player.Creature;
            // Vital Spark is owned by Infested Prism but its vanilla hooks afflict every player
            // Skill, not creatures on the Power owner's side.
            foreach (PredictedCard card in simulator.State.GetPlayerCombatState(player).AllCards)
            {
                // Root cards are already represented by _liveCardsAtSnapshot, so recording every
                // one here only makes each search fork clone a deck-sized HashSet. Track generated
                // cards sparsely; they still need identity-based first-entry detection across forks.
                bool enteredCombat = false;
                if (!liveCardsAtSnapshot.Contains(card.Original))
                    enteredCombat = (_powerAfflictionKnownCards ??= []).Add(card);
                if (card.Preview.Affliction is Tainted tainted)
                {
                    if (!hasVitalSpark)
                        card.ClearAffliction();
                    else if (tainted.Amount != vitalSparkAmount)
                        card.MutablePreview.Affliction!.Amount = vitalSparkAmount;
                    continue;
                }
                if (card.Preview.Affliction != null || !enteredCombat)
                    continue;

                for (int index = 0; index < powers.Count; index++)
                {
                    PowerModel power = powers[index];
                    if (power.Amount <= 0)
                        continue;
                    if (power is GalvanicPower && card.Preview.Type == CardType.Power)
                    {
                        if (power.Owner.Side != owner.Side)
                            continue;
                        simulator.Afflict<Galvanized>(card, power.Amount);
                        break;
                    }
                    if (power is VitalSparkPower && card.Preview.Type == CardType.Skill)
                    {
                        simulator.Afflict<Tainted>(card, vitalSparkAmount);
                        break;
                    }
                    if (power is SmoggyPower
                        && power.Owner.Side == owner.Side
                        && ReferenceEquals(power.Owner, owner)
                        && card.Preview.Type == CardType.Skill
                        && HasPlayedSkillThisTurn(owner))
                    {
                        simulator.Afflict<Smog>(card, 1);
                        break;
                    }
                }
            }
        }
    }

    private void NormalizeSwordSageReplays(CombatPredictionSimulator simulator)
    {
        _swordSageReplayBonuses ??= [];
        IReadOnlyList<Player> players = Players;
        for (int playerIndex = 0; playerIndex < players.Count; playerIndex++)
        {
            Player player = players[playerIndex];
            int desired = GetAmount<SwordSagePower>(player.Creature);
            int liveAmount = player.Creature.GetPower<SwordSagePower>()?.Amount ?? 0;
            foreach (PredictedCard card in simulator.State.GetPlayerCombatState(player).AllCards)
            {
                if (card.Preview is not SovereignBlade || card.Preview.IsClone)
                    continue;
                if (!_swordSageReplayBonuses.TryGetValue(card, out int applied))
                {
                    applied = _swordSageCardsInitialized ? 0 : liveAmount;
                    _swordSageReplayBonuses.Add(card, applied);
                }
                int delta = desired - applied;
                if (delta == 0)
                    continue;
                card.MutablePreview.BaseReplayCount += delta;
                _swordSageReplayBonuses[card] = desired;
            }
        }
        _swordSageCardsInitialized = true;
    }

    private void AppendPowerLifecycleFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        ulong first = 0;
        ulong second = 0;
        int count = 0;
        if (_orbitEnergyRemainders != null)
        {
            foreach ((OrbitPower power, int remainder) in _orbitEnergyRemainders)
            {
                StateFingerprintBuilder item = new();
                item.Add(power.Owner.CombatId ?? uint.MaxValue);
                item.Add(remainder);
                AddUnorderedItem(item.Finish(), ref first, ref second);
                count++;
            }
        }
        AddUnordered(ref fingerprint, 'O', count, first, second);

        first = 0;
        second = 0;
        count = 0;
        if (_paleBlueDotActivated != null)
        {
            foreach ((PaleBlueDotPower power, bool activated) in _paleBlueDotActivated)
            {
                StateFingerprintBuilder item = new();
                item.Add(power.Owner.CombatId ?? uint.MaxValue);
                item.Add(activated);
                AddUnorderedItem(item.Finish(), ref first, ref second);
                count++;
            }
        }
        AddUnordered(ref fingerprint, 'P', count, first, second);

        first = 0;
        second = 0;
        count = 0;
        if (_skillsPlayedThisTurn != null)
        {
            foreach (Creature owner in _skillsPlayedThisTurn)
            {
                StateFingerprintBuilder item = new();
                item.Add(owner.CombatId ?? uint.MaxValue);
                AddUnorderedItem(item.Finish(), ref first, ref second);
                count++;
            }
        }
        AddUnordered(ref fingerprint, 'K', count, first, second);
    }
}
