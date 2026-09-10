namespace CombatSolver.Engine.InCombat.Simulation.Compact;

/// <summary>
/// Experimental closed-domain executor. Only its value workspace is authoritative while running.
/// The admitting adapter must prove that draw, discard and play have no unrepresented effects.
/// It is deliberately not selected by production search.
/// </summary>
internal sealed class ResumableDiscardProgram
{
    internal readonly record struct Card(int Cost, int Draw, int Discard, bool Sly);
    internal enum Pile { Hand, Draw, Discard, Play, Exhaust }
    internal enum EventKind { Pay, Start, Draw, Select, SelectedCard, Discard, Block, Finish }
    internal readonly record struct Event(EventKind Kind, int Card, int Value, bool Automatic);
    private const int EnergySlot = 0, BlockSlot = 1, DepthSlot = 2, EventCountSlot = 3;
    private const int FrameWidth = 17, MaxFrames = 8;
    private const int CardOffset = 0, IpOffset = 1, AutoOffset = 2, BeforeBlockOffset = 3;
    private const int SelectedCountOffset = 4, NextAutoOffset = 5, SelectedOffset = 6;
    private readonly Card[] _cards;
    private readonly int _discardBlock;
    private readonly int _pileStart = 4;
    private readonly int _frameStart;
    private readonly int _eventStart;
    private readonly int _maxEvents;
    internal ReversibleValueState State { get; }
    internal int Energy => Read(EnergySlot);
    internal int Block => Read(BlockSlot);
    internal bool Complete => Read(DepthSlot) == 0;
    internal bool NeedsChoice => !Complete && Read(Frame + IpOffset) == 2;
    internal int ChoiceCard => NeedsChoice ? Read(Frame + CardOffset) : throw new InvalidOperationException("No pending choice.");
    internal int ChoiceCount => Math.Min(_cards[ChoiceCard].Discard, Count(Pile.Hand));
    internal int EventCount => Read(EventCountSlot);
    internal long EventsExecuted { get; private set; }
    private int Frame => _frameStart + (Read(DepthSlot) - 1) * FrameWidth;

    internal ResumableDiscardProgram(Card[] cards, IReadOnlyList<int>[] piles, int energy, int block, int discardBlock)
    {
        if (cards.Length == 0 || cards.Length > 64 || piles.Length != 5 || cards.Count(c => c.Sly) >= MaxFrames)
            throw new NotSupportedException("Compact prototype capacity exceeded.");
        if (cards.Any(c => c.Cost < 0 || c.Draw < 0 || c.Discard < 0 || c.Discard > 10 || c.Sly && c.Draw == 0)
            || energy < 0 || block < 0 || discardBlock < 0)
            throw new ArgumentException("Invalid compact root.");
        int[] identities = piles.SelectMany(p => p).ToArray();
        if (identities.Length != cards.Length || !identities.Order().SequenceEqual(Enumerable.Range(0, cards.Length))
            || piles[(int)Pile.Play].Count != 0 || piles[(int)Pile.Hand].Count > 10)
            throw new ArgumentException("Compact root requires unique instances and an idle play pile.");
        // A card can be drawn/played at most once without reshuffling. Reject before execution,
        // so an unsupported random operation can never leave a partially accepted candidate.
        if (cards.Sum(c => c.Draw) > piles[(int)Pile.Draw].Count)
            throw new NotSupportedException("Compact prototype does not admit roots that may shuffle.");
        _cards = (Card[])cards.Clone();
        _discardBlock = discardBlock;
        _frameStart = _pileStart + 5 * (cards.Length + 1);
        _eventStart = _frameStart + MaxFrames * FrameWidth;
        _maxEvents = cards.Length * 9 + 8;
        State = new ReversibleValueState(_eventStart + _maxEvents);
        State.Write(EnergySlot, energy);
        State.Write(BlockSlot, block);
        for (int p = 0; p < piles.Length; p++)
        {
            State.Write(PileBase((Pile)p), piles[p].Count);
            for (int i = 0; i < piles[p].Count; i++)
                State.Write(PileBase((Pile)p) + 1 + i, piles[p][i]);
        }
    }

    private ResumableDiscardProgram(Card[] cards, int discardBlock, ReversibleValueState state)
    {
        _cards = cards;
        _discardBlock = discardBlock;
        _frameStart = _pileStart + 5 * (cards.Length + 1);
        _eventStart = _frameStart + MaxFrames * FrameWidth;
        _maxEvents = cards.Length * 9 + 8;
        State = state;
    }

    internal sealed class Candidate
    {
        private readonly Card[] _cards;
        private readonly int _discardBlock;
        private readonly ReversibleValueState.FrozenValues _values;
        internal Candidate(ResumableDiscardProgram source)
        {
            _values = source.State.Freeze();
            _cards = source._cards;
            _discardBlock = source._discardBlock;
        }
        internal int PayloadBytes => _values.PayloadBytes;
        internal ResumableDiscardProgram Open() => new(_cards, _discardBlock, _values.CreateWorkspace());
        internal void RestoreInto(ResumableDiscardProgram workspace) => workspace.State.Restore(_values);
    }

    internal Candidate Freeze() => new(this);
    internal int Count(Pile pile) => Read(PileBase(pile));
    internal int CardAt(Pile pile, int index)
        => (uint)index < (uint)Count(pile) ? Read(PileBase(pile) + 1 + index) : throw new ArgumentOutOfRangeException(nameof(index));
    internal int[] Cards(Pile pile) => Enumerable.Range(0, Count(pile)).Select(i => CardAt(pile, i)).ToArray();
    internal Event EventAt(int index)
    {
        if ((uint)index >= (uint)EventCount) throw new ArgumentOutOfRangeException(nameof(index));
        long packed = State[_eventStart + index];
        return new((EventKind)(packed & 15), (int)((packed >> 4) & 255),
            (int)((packed >> 12) & uint.MaxValue), (packed & (1L << 44)) != 0);
    }

    internal void Begin(int card)
    {
        if (!Complete || !Contains(Pile.Hand, card)
            || _cards[card].Draw == 0 || Energy < _cards[card].Cost)
            throw new InvalidOperationException("Card cannot begin this compact action.");
        State.Write(EnergySlot, Energy - _cards[card].Cost);
        Emit(EventKind.Pay, card, _cards[card].Cost);
        Push(card, false);
    }

    internal void SupplyChoice(ReadOnlySpan<int> selected)
    {
        if (!NeedsChoice || selected.Length != ChoiceCount)
            throw new InvalidOperationException("Choice does not match the suspended instruction.");
        for (int i = 0; i < selected.Length; i++)
        {
            if (!Contains(Pile.Hand, selected[i]) || selected[..i].Contains(selected[i]))
                throw new InvalidOperationException("Choice contains an absent or repeated instance.");
        }
        State.Write(Frame + SelectedCountOffset, selected.Length);
        State.Write(Frame + NextAutoOffset, 0);
        for (int i = 0; i < selected.Length; i++) State.Write(Frame + SelectedOffset + i, selected[i]);
        State.Write(Frame + IpOffset, 3);
    }

    internal void Run(CancellationToken cancellationToken = default)
    {
        while (!Complete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frame = Frame;
            int card = Read(frame + CardOffset);
            switch (Read(frame + IpOffset))
            {
                case 0:
                    Move(card, Pile.Play);
                    Emit(EventKind.Start, card, _cards[card].Cost, Read(frame + AutoOffset) != 0);
                    State.Write(frame + IpOffset, 1);
                    break;
                case 1:
                    for (int i = 0; i < _cards[card].Draw && Count(Pile.Hand) < 10; i++)
                    {
                        if (Count(Pile.Draw) == 0) throw new InvalidOperationException("Admission failed to exclude shuffle.");
                        int drawn = CardAt(Pile.Draw, 0);
                        Move(drawn, Pile.Hand);
                        Emit(EventKind.Draw, drawn);
                    }
                    State.Write(frame + IpOffset, 2);
                    if (ChoiceCount != 0) return;
                    SupplyChoice([]);
                    break;
                case 2:
                    return;
                case 3:
                    // Native batch discard moves every selected card and runs its hooks before
                    // starting any Sly card. The selected instance list survives nested choices.
                    Emit(EventKind.Select, card, Read(frame + SelectedCountOffset));
                    for (int i = 0; i < Read(frame + SelectedCountOffset); i++)
                        Emit(EventKind.SelectedCard, Read(frame + SelectedOffset + i));
                    for (int i = 0; i < Read(frame + SelectedCountOffset); i++)
                    {
                        int discarded = Read(frame + SelectedOffset + i);
                        Move(discarded, Pile.Discard);
                        Emit(EventKind.Discard, discarded);
                        if (_discardBlock > 0)
                        {
                            int previous = Block;
                            State.Write(BlockSlot, Math.Min(999_999_999L, (long)Block + _discardBlock));
                            Emit(EventKind.Block, discarded, Block - previous);
                        }
                    }
                    State.Write(frame + IpOffset, 4);
                    break;
                case 4:
                    int next = Read(frame + NextAutoOffset);
                    if (next < Read(frame + SelectedCountOffset))
                    {
                        State.Write(frame + NextAutoOffset, next + 1);
                        int discarded = Read(frame + SelectedOffset + next);
                        if (_cards[discarded].Sly) Push(discarded, true);
                    }
                    else State.Write(frame + IpOffset, 5);
                    break;
                case 5:
                    Emit(EventKind.Finish, card, Block > Read(frame + BeforeBlockOffset) ? 1 : 0,
                        Read(frame + AutoOffset) != 0);
                    Move(card, Pile.Discard);
                    State.Write(DepthSlot, Read(DepthSlot) - 1);
                    break;
                default:
                    throw new InvalidOperationException("Unknown compact instruction.");
            }
        }
    }

    private void Push(int card, bool automatic)
    {
        if (Read(DepthSlot) >= MaxFrames) throw new InvalidOperationException("Compact frame capacity exceeded.");
        State.Write(DepthSlot, Read(DepthSlot) + 1);
        for (int i = 0; i < FrameWidth; i++) State.Write(Frame + i, 0);
        State.Write(Frame + CardOffset, card);
        State.Write(Frame + AutoOffset, automatic ? 1 : 0);
        State.Write(Frame + BeforeBlockOffset, Block);
    }

    private void Move(int card, Pile destination)
    {
        for (int p = 0; p < 5; p++)
        {
            Pile source = (Pile)p;
            for (int i = 0; i < Count(source); i++)
            {
                if (CardAt(source, i) != card) continue;
                int count = Count(source);
                for (int j = i; j + 1 < count; j++)
                    State.Write(PileBase(source) + 1 + j, CardAt(source, j + 1));
                State.Write(PileBase(source) + count, 0);
                State.Write(PileBase(source), count - 1);
                int targetCount = Count(destination);
                State.Write(PileBase(destination) + 1 + targetCount, card);
                State.Write(PileBase(destination), targetCount + 1);
                return;
            }
        }
        throw new InvalidOperationException("Compact card has no owning pile.");
    }

    private void Emit(EventKind kind, int card, int value = 0, bool automatic = false)
    {
        int count = EventCount;
        if (count >= _maxEvents) throw new InvalidOperationException("Compact event capacity exceeded.");
        State.Write(_eventStart + count, (long)(byte)kind | (long)(byte)card << 4 | (long)(uint)value << 12
            | (automatic ? 1L << 44 : 0));
        State.Write(EventCountSlot, count + 1);
        EventsExecuted++;
    }

    private bool Contains(Pile pile, int card)
    {
        for (int i = 0; i < Count(pile); i++) if (CardAt(pile, i) == card) return true;
        return false;
    }
    private int PileBase(Pile pile) => _pileStart + (int)pile * (_cards.Length + 1);
    private int Read(int slot) => checked((int)State[slot]);
}
