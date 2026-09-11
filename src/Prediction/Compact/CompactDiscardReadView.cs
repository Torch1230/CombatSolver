using CombatSolver.Engine.Common;
using System.Collections;
using MegaCrit.Sts2.Core.Entities.Cards;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver;

/// <summary>Lane-owned reader for the exact admission contract of CompactDiscardProjection.</summary>
internal sealed class CompactDiscardReadView : CompletedStateReadView
{
    private readonly CompactDiscardProjection _adapter;
    private readonly CombatPredictionSimulator _context;
    private readonly CompactCardMetadataReadBinding _cards;
    private readonly Player _player;
    private readonly CardHistoryReadValues _baseline;
    private readonly ulong[] _cardGapMasks;
    private readonly Dictionary<ulong, IReadOnlyList<PredictionGap>> _gapCombinations = [];
    private readonly IReadOnlyList<PredictionGap> _rootGaps;
    private readonly PredictionGap[] _distinctGaps;
    private ResumableDiscardProgram _program;
    private CardHistoryReadValues _history;
    private IReadOnlyList<PredictionGap> _gaps;
    private int _entries;
    private readonly int _rootPlayerHpLost;
    private int _playerHpLost;
    private readonly PileView _hand, _draw, _discard, _exhaust;
    private readonly RosterView _enemies;
    private readonly CombatHistoryReadValues _combatBaseline, _combatHistory = new();
    private readonly int[] _baseHits, _baseEnemyHits, _baseCreatureAttacks, _basePetHits, _baseHitsOnPet;
    private readonly SimulatedCombatState.CompletedOstyReadBinding? _ostyBinding;
    private readonly SimulatedCombatState.CompletedPowerReadBinding? _powerBinding;
    private CompletedPowerReadValues[] _powerValues;
    private readonly CompactMonsterAiReadBinding? _monsterAiBinding;
    private readonly SimulatedCombatState.CompletedRoundReadBinding? _roundBinding;
    internal int RiskSourceCount => _distinctGaps.Length;
    internal int LastImpureHistoryIndex { get; private set; }

    internal CompactDiscardReadView(CompactDiscardProjection adapter, CombatPredictionSimulator root, Player player,
        PredictionRiskReason?[] risks)
    {
        _adapter = adapter;
        _context = root;
        _player = player;
        _program = adapter.Program;
        var cards = Enumerable.Range(0, adapter.CardCount).Select(id => root.State.FindCard(adapter.Original(id))
            ?? throw new InvalidOperationException("Read view lost a root card.")).ToArray();
        _cards = new(adapter, cards);
        if (_program.HasGlobalEnergyCosts) _context.CompletedEnergyCosts = _cards;
        // Getters materialize history maps. Only this disposable setup fork may do that;
        // untouched maps in the immutable root must retain their original absence/zero shape.
        var metadata = (SimulatedCombatState)root.Fork().State.CombatState;
        _baseline = new(player, metadata.GetBlockCardsPlayedThisTurn(player.Creature),
            metadata.GetSkillCardsPlayedThisTurn(player.Creature), metadata.GetCardsDiscardedThisTurn(player.Creature),
            metadata.GetEnergySpentThisTurn(player), metadata.GetNonHandDrawsThisTurn(player),
            metadata.GetCardPlaySeriesStartedThisTurn(player.Creature), metadata.GetCardPlayStartsThisTurn(player.Creature),
            metadata.GetCardsPlayedThisTurn(player.Creature), metadata.GetManualCardsPlayedThisTurn(player.Creature),
            metadata.GetAttacksPlayedThisTurn(player.Creature), metadata.GetCreatureAttacksThisTurn(player.Creature),
            metadata.GetZeroCostAttackStartsThisTurn(player.Creature), metadata.GetCardsExhaustedThisTurn(player.Creature), metadata.GetShivsPlayedThisTurn(player.Creature), metadata.GetStatusCardsDrawnThisTurn(player));
        _combatBaseline = ((SimulatedCombatState)root.State.CombatState).CaptureCombatHistoryReadValues();
        _rootPlayerHpLost = metadata.GetCumulativeHpLost(player.Creature);
        _baseHits = Enumerable.Range(0, _program.CreatureCount)
            .Select(id => metadata.GetPoweredAttackHitsThisTurn(player.Creature, adapter.Creature(id))).ToArray();
        _baseEnemyHits = !adapter.HasMonsterMoves ? [] : Enumerable.Range(0, _program.CreatureCount)
            .Select(id => metadata.GetPoweredAttackHitsThisTurn(adapter.Creature(id), player.Creature)).ToArray();
        _baseCreatureAttacks = !adapter.HasMonsterMoves && _program.PetIndex < 0 ? [] : Enumerable.Range(0, _program.CreatureCount)
            .Select(id => metadata.GetCreatureAttacksThisTurn(adapter.Creature(id))).ToArray();
        _basePetHits = _program.PetIndex < 0 ? [] : Enumerable.Range(0, _program.CreatureCount)
            .Select(id => metadata.GetPoweredAttackHitsThisTurn(adapter.Creature(_program.PetIndex), adapter.Creature(id))).ToArray();
        _baseHitsOnPet = _program.PetIndex < 0 ? [] : Enumerable.Range(0, _program.CreatureCount)
            .Select(id => metadata.GetPoweredAttackHitsThisTurn(adapter.Creature(id), adapter.Creature(_program.PetIndex))).ToArray();
        _ostyBinding = adapter.CreateOstyReadBinding(_context);
        _enemies = new(this);
        _monsterAiBinding = adapter.CreateMonsterAiReadBinding(_context);
        _roundBinding = adapter.CreateRoundReadBinding(_context);
        _powerValues = new CompletedPowerReadValues[_program.PowerCount + _program.PanacheCount];
        _powerBinding = _program.PowerCount == 0 ? null : adapter.CreatePowerReadBinding(_context);
        _rootGaps = PredictionCoverage.Collect(root);
        PredictionGap?[] cardGaps = adapter.DefinitionModels.Select((card, id) => risks[id] is { } reason
            ? PredictionCoverage.FromSource(card, "OnPlay", reason) : null).ToArray();
        _distinctGaps = cardGaps.OfType<PredictionGap>().Distinct().ToArray();
        // Gaps are keyed by immutable definitions, not the growing instance count. Cache
        // only combinations actually read by this lane, never all possible subsets.
        if (_distinctGaps.Length > 64) throw new NotSupportedException("Compact risk mask capacity exceeded.");
        _cardGapMasks = cardGaps.Select(gap => gap is null ? 0UL : 1UL << Array.IndexOf(_distinctGaps, gap)).ToArray();
        _gapCombinations.Add(0, PredictionCoverage.Normalize(_rootGaps));
        _gaps = _gapCombinations[0];
        _hand = new(this, ResumableDiscardProgram.Pile.Hand);
        _draw = new(this, ResumableDiscardProgram.Pile.Draw);
        _discard = new(this, ResumableDiscardProgram.Pile.Discard);
        _exhaust = new(this, ResumableDiscardProgram.Pile.Exhaust);
    }

    internal void Read(ResumableDiscardProgram program)
    {
        if (!_adapter.Program.State.HasSameRoot(program.State) || !program.Complete)
            throw new InvalidOperationException("Read view requires a completed candidate from its own root.");
        ReadCore(program, null);
    }

    internal void ReadPending(ResumableDiscardProgram program, TurnStartChoiceRequest request)
    {
        if (!_adapter.Program.State.HasSameRoot(program.State) || !program.NeedsChoice)
            throw new InvalidOperationException("Pending read requires an explicit suspended selector from its own root.");
        ReadCore(program, request);
    }

    private void ReadCore(ResumableDiscardProgram program, TurnStartChoiceRequest? pending)
    {
        var combat = (SimulatedCombatState)_context.State.CombatState;
        combat.ClearPendingTurnStartChoice();
        _program = program;
        _cards.Read(program);
        if (_ostyBinding != null)
        {
            var pet = program.Creature(program.PetIndex);
            _ostyBinding.Read(pet.CurrentHp, pet.MaxHp, pet.Block, program.PetSummoned);
        }
        _monsterAiBinding?.Read(program);
        _roundBinding?.Read(program.RoundNumber, program.PlayerTurn, program.EnemySide, program.BeganEnemyTurn, program.BeganPlayerTurn);
        bool playerReset = false, enemyReset = false;
        int attacks = 0, creatureAttacks = 0, zeroCostAttacks = 0, shivs = 0, statusDraws = 0;
        _combatHistory.ResetFrom(_combatBaseline);
        int block = 0, skill = 0, discarded = 0, exhausted = 0, energy = 0, draw = 0, starts = 0, plays = 0, manual = 0;
        _entries = _context.History.Entries.Count;
        // Search compares only suffixes beginning at or after this captured root.
        LastImpureHistoryIndex = _entries - 1;
        _playerHpLost = _rootPlayerHpLost;
        ulong gapMask = 0;
        for (int i = 0; i < program.EventCount; i++)
        {
            var item = program.EventAt(i);
            switch (item.Kind)
            {
                case ResumableDiscardProgram.EventKind.CommitPlayerTurnHistory:
                    if (_combatHistory.LastAttacks.Remove(_player, out var lastAttack))
                        _combatHistory.PreviousTurnAttacks[_player] = lastAttack;
                    else _combatHistory.PreviousTurnAttacks.Remove(_player);
                    break;
                case ResumableDiscardProgram.EventKind.BeginSide:
                    bool enemy = item.Card == -2;
                    Creature phaseOwner = _adapter.Creature(enemy ? 1 : 0);
                    _combatHistory.LostHp.Clear();
                    _combatHistory.DoomAppliers.Clear();
                    _combatHistory.PoweredHits.Clear();
                    _combatHistory.CreatureAttacks.Clear();
                    _combatHistory.CreatureAttacks[phaseOwner] = 0;
                    if (!enemy && program.PetIndex >= 0) _combatHistory.CreatureAttacks[_adapter.Creature(program.PetIndex)] = 0;
                    playerReset = enemyReset = true;
                    attacks = creatureAttacks = zeroCostAttacks = shivs = statusDraws = 0;
                    block = skill = discarded = exhausted = energy = draw = starts = plays = manual = 0;
                    break;
                case ResumableDiscardProgram.EventKind.Pay: energy += item.Value; break;
                case ResumableDiscardProgram.EventKind.Start:
                    starts++;
                    if (_cards[item.Card].Preview.Type == CardType.Attack && item.Value == 0) zeroCostAttacks++;
                    if (!item.Automatic) manual++;
                    _entries++;
                    ulong cardMask = _cardGapMasks[program.DefinitionIndex(item.Card)];
                    if (cardMask != 0) { LastImpureHistoryIndex = _entries++; gapMask |= cardMask; }
                    break;
                case ResumableDiscardProgram.EventKind.Generated:
                    _entries += 2; LastImpureHistoryIndex = _entries - 1; break;
                case ResumableDiscardProgram.EventKind.Draw:
                    if (item.Value == 0) draw++;
                    _entries += 2;
                    LastImpureHistoryIndex = _entries - 1;
                    if (_cards[item.Card].Preview.Type == CardType.Status) statusDraws++;
                    break;
                case ResumableDiscardProgram.EventKind.Discard: discarded++; break;
                case ResumableDiscardProgram.EventKind.ResultMoved:
                    if ((ResumableDiscardProgram.Pile)item.Value == ResumableDiscardProgram.Pile.Exhaust) exhausted++;
                    break;
                case ResumableDiscardProgram.EventKind.Finish:
                    plays++; _entries++;
                    if (_cards[item.Card].Preview.Type == CardType.Skill) skill++;
                    if (_cards[item.Card].Preview.Type == CardType.Attack)
                    {
                        attacks++; _combatHistory.LastAttacks[_player] = _cards[item.Card];
                        if (_cards[item.Card].Preview.Tags.Contains(CardTag.Shiv)) shivs++;
                    }
                    if (item.Value != 0) block++;
                    break;
                case ResumableDiscardProgram.EventKind.Damage:
                    Creature receiver = _adapter.Creature(item.Target);
                    if (item.Target == 0) _playerHpLost += item.Value;
                    if (item.Value > 0) _combatHistory.LostHp.Add(receiver);
                    if ((item.Flags & (int)(ResumableDiscardProgram.DamageTraits.Unpowered | ResumableDiscardProgram.DamageTraits.NoDealer)) == 0)
                    {
                        int dealer = item.Dealer;
                        var hitKey = (_adapter.Creature(dealer), receiver);
                        int baseline = playerReset ? 0 : dealer == 0 ? _baseHits[item.Target]
                            : dealer == program.PetIndex ? _basePetHits[item.Target]
                            : item.Target == program.PetIndex ? _baseHitsOnPet[dealer] : _baseEnemyHits[dealer];
                        _combatHistory.PoweredHits[hitKey] = _combatHistory.PoweredHits.GetValueOrDefault(hitKey, baseline) + 1;
                    }
                    _entries++;
                    break;
                case ResumableDiscardProgram.EventKind.AttackFinish:
                    if (item.Dealer == 0) creatureAttacks++;
                    else
                    {
                        int owner = item.Dealer;
                        Creature actor = _adapter.Creature(owner);
                        _combatHistory.CreatureAttacks[actor] = _combatHistory.CreatureAttacks.GetValueOrDefault(actor, enemyReset ? 0 : _baseCreatureAttacks[owner]) + 1;
                    }
                    _entries++;
                    break;
                case ResumableDiscardProgram.EventKind.Death:
                    if (item.Target != program.PetIndex)
                        _combatHistory.DeathPhases[_adapter.Creature(item.Target)] = PredictedDeathPhase.PermanentlyDead;
                    break;
                case ResumableDiscardProgram.EventKind.DoomApplied:
                    _combatHistory.DoomAppliers.Add(_adapter.Creature(item.Card));
                    break;
                case ResumableDiscardProgram.EventKind.SummonPet:
                case ResumableDiscardProgram.EventKind.Kill:
                case ResumableDiscardProgram.EventKind.GainEnergy:
                case ResumableDiscardProgram.EventKind.ResetEnergy:
                case ResumableDiscardProgram.EventKind.CleanupCards:
                case ResumableDiscardProgram.EventKind.PowerChange:
                case ResumableDiscardProgram.EventKind.HandEndMoved:
                case ResumableDiscardProgram.EventKind.HandEndStart:
                case ResumableDiscardProgram.EventKind.HandEndFinish:
                case ResumableDiscardProgram.EventKind.CostChanged:
                case ResumableDiscardProgram.EventKind.KeywordAdded:
                case ResumableDiscardProgram.EventKind.EnchantmentStart:
                case ResumableDiscardProgram.EventKind.EnchantmentFinish:
                case ResumableDiscardProgram.EventKind.PanacheStart:
                case ResumableDiscardProgram.EventKind.PanacheFinish:
                case ResumableDiscardProgram.EventKind.CardHooksFinished:
                case ResumableDiscardProgram.EventKind.DamageBlocked:
                case ResumableDiscardProgram.EventKind.DamageOverkill:
                case ResumableDiscardProgram.EventKind.Block:
                case ResumableDiscardProgram.EventKind.Shuffle:
                case ResumableDiscardProgram.EventKind.ShuffleCard:
                case ResumableDiscardProgram.EventKind.Retrieve:
                case ResumableDiscardProgram.EventKind.Select:
                case ResumableDiscardProgram.EventKind.SelectedCard: break;
                default: throw new InvalidOperationException("Read view encountered an unknown committed event.");
            }
        }
        int? Add(int? value, int delta) => delta == 0 ? null : checked((playerReset ? 0 : value!.Value) + delta);
        _history = new(_player, Add(_baseline.BlockPlays, block), Add(_baseline.SkillPlays, skill),
            Add(_baseline.Discards, discarded), Add(_baseline.EnergySpent, energy), Add(_baseline.Draws, draw),
            Add(_baseline.Series, starts), Add(_baseline.Starts, starts), Add(_baseline.Plays, plays), Add(_baseline.ManualPlays, manual), Add(_baseline.AttackPlays, attacks),
            Add(_baseline.CreatureAttacks, creatureAttacks), Add(_baseline.ZeroCostAttackStarts, zeroCostAttacks), Add(_baseline.Exhausts, exhausted), Add(_baseline.ShivPlays, shivs), Add(_baseline.StatusDraws, statusDraws));
        if (!_gapCombinations.TryGetValue(gapMask, out var gaps))
        {
            gaps = PredictionCoverage.Normalize(_rootGaps.Concat(_distinctGaps.Where((_, i) => (gapMask & (1UL << i)) != 0)));
            _gapCombinations.Add(gapMask, gaps);
        }
        _gaps = gaps;
        combat.ImportCompletedDoomAppliers(_combatHistory.DoomAppliers);
        if (_powerBinding != null)
        {
            int count = program.PowerCount + program.PanacheCount;
            if (_powerValues.Length < count) Array.Resize(ref _powerValues, count);
            var values = _powerValues.AsSpan(0, count);
            _adapter.CopyPowerReadValues(program, values);
            _powerBinding.Read(values, _enemies);
        }
        if (pending != null) combat.SetPendingTurnStartChoice(pending);
    }

    internal override CombatPredictionSimulator EvaluationContext => _context;
    internal override int Energy => _program.Energy;
    internal override int Block => _program.Block;
    internal override CreatureReadValues ReadCreature(Creature creature)
    {
        int index = _adapter.CreatureIndex(creature);
        if (index >= 0)
        {
            CreatureVitals current = _program.Creature(index);
            return new(current.CurrentHp, current.MaxHp, current.Block, _program.CreaturePresent(index));
        }
        CreatureReadValues values = CreatureReadValues.Capture(_context, creature);
        return ReferenceEquals(creature, _player.Creature) ? values with { Block = Block } : values;
    }
    internal override CombatTerminalStamp? TerminalStamp => _program.Terminal
        ? new(_program.HasRounds ? _program.TerminalPlayerTurn : _adapter.PlayerTurn, _program.DefeatTerminal ? CombatTerminalOutcome.Defeat : CombatTerminalOutcome.Victory) : _context.TerminalStamp;
    internal override IReadOnlyList<Creature> EnemyRoster => _program.CreatureCount == 0
        ? ((SimulatedCombatState)_context.State.CombatState).Enemies : _enemies;
    internal override bool EnemyValuesInvariant => _program.CreatureCount == 0;
    internal override bool CardValuesInvariant => _adapter.CardValuesInvariant
        && _program.Count(ResumableDiscardProgram.Pile.Play) == 0;
    internal override int HistoryEntries => _entries;
    internal override IReadOnlyList<PredictedCard> Hand => _hand;
    internal override IReadOnlyList<PredictedCard> Draw => _draw;
    internal override IReadOnlyList<PredictedCard> Discard => _discard;
    internal override IReadOnlyList<PredictedCard> Exhaust => _exhaust;
    internal override IReadOnlyList<PredictionGap> PredictionGaps => _gaps;
    internal override CardHistoryReadValues CardHistory => _history;
    internal override CombatHistoryReadValues? CombatHistory => _program.CreatureCount == 0 ? null : _combatHistory;
    internal override int? CumulativePlayerHpLost => _playerHpLost;
    internal override PredictionRngState ShuffleRng
    {
        get
        {
            ValueRng rng = _program.ShuffleRng;
            return new(rng.Counter, rng.State0, rng.State1, rng.State2, rng.State3);
        }
    }

    internal override PredictionRngState? EnergyCostRng => _program.EnergyCostRng is { } rng
        ? new(rng.Counter, rng.State0, rng.State1, rng.State2, rng.State3) : null;

    private sealed class RosterView(CompactDiscardReadView owner) : IReadOnlyList<Creature>
    {
        public int Count
        {
            get { int count = 0; for (int i = 1; i < owner._program.EnemyEnd; i++) if (owner._program.CreaturePresent(i)) count++; return count; }
        }
        public Creature this[int index]
        {
            get
            {
                for (int i = 1; i < owner._program.EnemyEnd; i++)
                    if (owner._program.CreaturePresent(i) && index-- == 0) return owner._adapter.Creature(i);
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
        public IEnumerator<Creature> GetEnumerator()
        {
            for (int i = 1; i < owner._program.EnemyEnd; i++)
                if (owner._program.CreaturePresent(i)) yield return owner._adapter.Creature(i);
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class PileView(CompactDiscardReadView owner, ResumableDiscardProgram.Pile pile)
        : IReadOnlyList<PredictedCard>
    {
        public int Count => owner._program.Count(pile);
        public PredictedCard this[int index] => owner._cards[owner._program.CardAt(pile, index)];
        public IEnumerator<PredictedCard> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
