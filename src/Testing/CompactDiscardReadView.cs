using CombatSolver.Engine.Common;
using System.Collections;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Entities.Players;

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
            metadata.GetCardsPlayedThisTurn(player.Creature), metadata.GetManualCardsPlayedThisTurn(player.Creature));
        IReadOnlyList<PredictionGap> rootGaps = PredictionCoverage.Collect(root);
        PredictionGap?[] cardGaps = _cards.Select((card, id) => inferred[id]
            ? PredictionCoverage.FromSource(card.Preview, "OnPlay", PredictionRiskReason.MethodMirrorIncomplete) : null).ToArray();
        PredictionGap[] distinct = cardGaps.OfType<PredictionGap>().Distinct().ToArray();
        // Only Acrobatics and Prepared can execute. Keep the specialization explicitly bounded.
        if (distinct.Length > 2) throw new NotSupportedException("Closed read view has more than two risk sources.");
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
                    if (!item.Automatic) manual++;
                    _entries++;
                    int cardMask = _cardGapMasks[item.Card];
                    if (cardMask != 0) { _entries++; gapMask |= cardMask; }
                    break;
                case ResumableDiscardProgram.EventKind.Draw: draw++; _entries += 2; break;
                case ResumableDiscardProgram.EventKind.Discard: discarded++; break;
                case ResumableDiscardProgram.EventKind.Finish:
                    plays++; skill++; _entries++;
                    if (item.Value != 0) block++;
                    break;
                case ResumableDiscardProgram.EventKind.Block:
                case ResumableDiscardProgram.EventKind.Select:
                case ResumableDiscardProgram.EventKind.SelectedCard: break;
                default: throw new InvalidOperationException("Read view encountered an unknown committed event.");
            }
        }
        static int? Add(int? value, int delta) => delta == 0 ? null : checked(value!.Value + delta);
        _history = new(_player, Add(_baseline.BlockPlays, block), Add(_baseline.SkillPlays, skill),
            Add(_baseline.Discards, discarded), Add(_baseline.EnergySpent, energy), Add(_baseline.Draws, draw),
            Add(_baseline.Series, starts), Add(_baseline.Starts, starts), Add(_baseline.Plays, plays), Add(_baseline.ManualPlays, manual));
        _gaps = _gapCombinations[gapMask];
    }

    internal override CombatPredictionSimulator Root => _root;
    internal override int Energy => _program.Energy;
    internal override int Block => _program.Block;
    internal override int HistoryEntries => _entries;
    internal override IReadOnlyList<PredictedCard> Hand => _hand;
    internal override IReadOnlyList<PredictedCard> Draw => _draw;
    internal override IReadOnlyList<PredictedCard> Discard => _discard;
    internal override IReadOnlyList<PredictedCard> Exhaust => _exhaust;
    internal override IReadOnlyList<PredictionGap> PredictionGaps => _gaps;
    internal override CardHistoryReadValues CardHistory => _history;

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
