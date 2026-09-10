using CombatSolver.Engine.Common;
using System.Collections;
using MegaCrit.Sts2.Core.Entities.Cards;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver;

/// <summary>Test-only reader for the exact admission contract of CompactDiscardProjection.</summary>
internal sealed class CompactDiscardReadView : CompletedStateReadView
{
    private readonly CompactDiscardProjection _adapter;
    private readonly CombatPredictionSimulator _root;
    private readonly PredictedCard[] _cards;
    private readonly Player _player;
    private readonly CardHistoryReadValues _baseline;
    private readonly int[] _cardGapMasks;
    private readonly IReadOnlyList<PredictionGap>[] _gapCombinations;
    private ResumableDiscardProgram _program;
    private CardHistoryReadValues _history;
    private IReadOnlyList<PredictionGap> _gaps;
    private int _entries;
    private readonly PileView _hand, _draw, _discard, _exhaust;
    private readonly RosterView _enemies;
    private readonly CombatHistoryReadValues _combatBaseline, _combatHistory = new();
    private readonly int[] _baseHits;

    internal CompactDiscardReadView(CompactDiscardProjection adapter, CombatPredictionSimulator root, Player player,
        bool[] inferred)
    {
        _adapter = adapter;
        _root = root;
        _player = player;
        _program = adapter.Program;
        _cards = Enumerable.Range(0, adapter.CardCount).Select(id => root.State.FindCard(adapter.Original(id))
            ?? throw new InvalidOperationException("Read view lost a root card.")).ToArray();
        // Getters materialize history maps. Only this disposable setup fork may do that;
        // untouched maps in the immutable root must retain their original absence/zero shape.
        var metadata = (SimulatedCombatState)root.Fork().State.CombatState;
        _baseline = new(player, metadata.GetBlockCardsPlayedThisTurn(player.Creature),
            metadata.GetSkillCardsPlayedThisTurn(player.Creature), metadata.GetCardsDiscardedThisTurn(player.Creature),
            metadata.GetEnergySpentThisTurn(player), metadata.GetNonHandDrawsThisTurn(player),
            metadata.GetCardPlaySeriesStartedThisTurn(player.Creature), metadata.GetCardPlayStartsThisTurn(player.Creature),
            metadata.GetCardsPlayedThisTurn(player.Creature), metadata.GetManualCardsPlayedThisTurn(player.Creature),
            metadata.GetAttacksPlayedThisTurn(player.Creature), metadata.GetCreatureAttacksThisTurn(player.Creature),
            metadata.GetZeroCostAttackStartsThisTurn(player.Creature));
        _combatBaseline = ((SimulatedCombatState)root.State.CombatState).CaptureCombatHistoryReadValues();
        _baseHits = Enumerable.Range(0, _program.CreatureCount)
            .Select(id => metadata.GetPoweredAttackHitsThisTurn(player.Creature, adapter.Creature(id))).ToArray();
        _enemies = new(this);
        IReadOnlyList<PredictionGap> rootGaps = PredictionCoverage.Collect(root);
        PredictionGap?[] cardGaps = _cards.Select((card, id) => inferred[id]
            ? PredictionCoverage.FromSource(card.Preview, "OnPlay", PredictionRiskReason.MethodMirrorIncomplete) : null).ToArray();
        PredictionGap[] distinct = cardGaps.OfType<PredictionGap>().Distinct().ToArray();
        // The admitting adapter limits executable card types; no generic source cache escapes it.
        if (distinct.Length > 4) throw new NotSupportedException("Closed read view has more than four risk sources.");
        _cardGapMasks = cardGaps.Select(gap => gap is null ? 0 : 1 << Array.IndexOf(distinct, gap)).ToArray();
        _gapCombinations = Enumerable.Range(0, 1 << distinct.Length)
            .Select(mask => PredictionCoverage.Normalize(rootGaps.Concat(distinct.Where((_, i) => (mask & (1 << i)) != 0))))
            .ToArray();
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
        _program = program;
        int attacks = 0, creatureAttacks = 0, zeroCostAttacks = 0;
        _combatHistory.ResetFrom(_combatBaseline);
        int block = 0, skill = 0, discarded = 0, energy = 0, draw = 0, starts = 0, plays = 0, manual = 0;
        _entries = _root.History.Entries.Count;
        int gapMask = 0;
        for (int i = 0; i < program.EventCount; i++)
        {
            var item = program.EventAt(i);
            switch (item.Kind)
            {
                case ResumableDiscardProgram.EventKind.Pay: energy += item.Value; break;
                case ResumableDiscardProgram.EventKind.Start:
                    starts++;
                    if (_cards[item.Card].Preview.Type == CardType.Attack && item.Value == 0) zeroCostAttacks++;
                    if (!item.Automatic) manual++;
                    _entries++;
                    int cardMask = _cardGapMasks[item.Card];
                    if (cardMask != 0) { _entries++; gapMask |= cardMask; }
                    break;
                case ResumableDiscardProgram.EventKind.Draw: draw++; _entries += 2; break;
                case ResumableDiscardProgram.EventKind.Discard: discarded++; break;
                case ResumableDiscardProgram.EventKind.Finish:
                    plays++; _entries++;
                    if (_cards[item.Card].Preview.Type == CardType.Skill) skill++;
                    if (_cards[item.Card].Preview.Type == CardType.Attack)
                    { attacks++; _combatHistory.LastAttacks[_player] = _cards[item.Card]; }
                    if (item.Value != 0) block++;
                    break;
                case ResumableDiscardProgram.EventKind.Damage:
                    Creature receiver = _adapter.Creature(item.Target);
                    if (item.Value > 0) _combatHistory.LostHp.Add(receiver);
                    var hitKey = (_player.Creature, receiver);
                    _combatHistory.PoweredHits[hitKey] = _combatHistory.PoweredHits.GetValueOrDefault(hitKey, _baseHits[item.Target]) + 1;
                    _entries++;
                    break;
                case ResumableDiscardProgram.EventKind.AttackFinish: creatureAttacks++; _entries++; break;
                case ResumableDiscardProgram.EventKind.Death:
                    _combatHistory.DeathPhases[_adapter.Creature(item.Target)] = PredictedDeathPhase.PermanentlyDead;
                    break;
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
        static int? Add(int? value, int delta) => delta == 0 ? null : checked(value!.Value + delta);
        _history = new(_player, Add(_baseline.BlockPlays, block), Add(_baseline.SkillPlays, skill),
            Add(_baseline.Discards, discarded), Add(_baseline.EnergySpent, energy), Add(_baseline.Draws, draw),
            Add(_baseline.Series, starts), Add(_baseline.Starts, starts), Add(_baseline.Plays, plays), Add(_baseline.ManualPlays, manual), Add(_baseline.AttackPlays, attacks),
            Add(_baseline.CreatureAttacks, creatureAttacks), Add(_baseline.ZeroCostAttackStarts, zeroCostAttacks));
        _gaps = _gapCombinations[gapMask];
    }

    internal override CombatPredictionSimulator Root => _root;
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
        CreatureReadValues values = CreatureReadValues.Capture(_root, creature);
        return ReferenceEquals(creature, _player.Creature) ? values with { Block = Block } : values;
    }
    internal override CombatTerminalStamp? TerminalStamp => _program.Terminal
        ? new(_adapter.PlayerTurn, CombatTerminalOutcome.Victory) : _root.TerminalStamp;
    internal override IReadOnlyList<Creature> EnemyRoster => _program.CreatureCount == 0
        ? ((SimulatedCombatState)_root.State.CombatState).Enemies : _enemies;
    internal override bool EnemyValuesInvariant => _program.CreatureCount == 0;
    internal override int HistoryEntries => _entries;
    internal override IReadOnlyList<PredictedCard> Hand => _hand;
    internal override IReadOnlyList<PredictedCard> Draw => _draw;
    internal override IReadOnlyList<PredictedCard> Discard => _discard;
    internal override IReadOnlyList<PredictedCard> Exhaust => _exhaust;
    internal override IReadOnlyList<PredictionGap> PredictionGaps => _gaps;
    internal override CardHistoryReadValues CardHistory => _history;
    internal override CombatHistoryReadValues? CombatHistory => _program.CreatureCount == 0 ? null : _combatHistory;
    internal override PredictionRngState ShuffleRng
    {
        get
        {
            ValueShuffleRng rng = _program.ShuffleRng;
            return new(rng.Counter, rng.State0, rng.State1, rng.State2, rng.State3);
        }
    }

    private sealed class RosterView(CompactDiscardReadView owner) : IReadOnlyList<Creature>
    {
        public int Count
        {
            get { int count = 0; for (int i = 1; i < owner._program.CreatureCount; i++) if (owner._program.CreaturePresent(i)) count++; return count; }
        }
        public Creature this[int index]
        {
            get
            {
                for (int i = 1; i < owner._program.CreatureCount; i++)
                    if (owner._program.CreaturePresent(i) && index-- == 0) return owner._adapter.Creature(i);
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
        public IEnumerator<Creature> GetEnumerator()
        {
            for (int i = 1; i < owner._program.CreatureCount; i++)
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
