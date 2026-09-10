namespace CombatSolver.Engine.InCombat.Simulation.Compact;

/// <summary>
/// Experimental closed-domain executor. Only its value workspace is authoritative while running.
/// The admitting adapter must prove that draw, discard and play have no unrepresented effects.
/// It is deliberately not selected by production search.
/// </summary>
internal sealed class ResumableDiscardProgram
{
    internal readonly record struct Card(int Cost, CardEffectProgram Effects, bool Sly = false);
    internal enum Pile { Hand, Draw, Discard, Play, Exhaust }
    internal enum EventKind { Pay, Start, Draw, Select, SelectedCard, Discard, Block, Finish, Shuffle, ShuffleCard, Retrieve, Damage, DamageBlocked, DamageOverkill, AttackFinish, Death, PowerChange }
    internal readonly record struct Event(EventKind Kind, int Card, int Value, bool Automatic, int Target = -1, int Flags = 0);
    private const int EnergySlot = 0, BlockSlot = 1, DepthSlot = 2, EventCountSlot = 3;
    private const int RngSlot = 4, ShuffleCountSlot = 9;
    private const int FrameWidth = 19, MaxFrames = 8;
    private const int CardOffset = 0, IpOffset = 1, AutoOffset = 2, BeforeBlockOffset = 3;
    private const int SelectedCountOffset = 4, NextAutoOffset = 5, SelectedOffset = 6;
    private const int DrawIndexOffset = 16, TargetOffset = 17, EffectIndexOffset = 18;
    private readonly Card[] _cards;
    private readonly CreatureAttackLayout? _combat;
    private readonly BasicPowerLayout? _powers;
    private readonly int _discardBlock;
    private readonly int _stratagem;
    private readonly int _shuffleBlock;
    private readonly bool _shuffleBlockFirst;
    private readonly CardComparer? _cardComparer;
    private readonly int _pileStart = 10;
    private readonly int _frameStart;
    private readonly int _eventStart;
    internal ReversibleValueState State { get; }
    internal int Energy => Read(EnergySlot);
    internal int Block => _combat?.Read(State, 0).Block ?? Read(BlockSlot);
    internal int PowerCount => _powers?.Count ?? 0;
    internal BasicPowerValues Power(int index) => _powers!.Read(State, index);
    internal BasicPowerDefinition PowerDefinition(int index) => _powers!.Definition(index);
    internal int CreatureCount => _combat?.Count ?? 0;
    internal CreatureVitals Creature(int index) => _combat!.Read(State, index);
    internal bool CreaturePresent(int index) => _combat!.Present(State, index);
    internal bool CreatureDeathCompleted(int index) => _combat!.DeathCompleted(State, index);
    internal bool Ending => _combat?.IsEnding(State) ?? false;
    internal bool Terminal => _combat?.Terminal(State) ?? false;
    internal bool CheckWinCondition() => Complete ? _combat?.CheckWinCondition(State) ?? false
        : throw new InvalidOperationException("Terminal check requires a completed command.");
    internal bool Complete => Read(DepthSlot) == 0;
    internal bool NeedsChoice => !Complete && Read(Frame + IpOffset) is 2 or 6;
    internal Pile ChoicePile => NeedsChoice && Read(Frame + IpOffset) == 6 ? Pile.Draw : Pile.Hand;
    internal int ChoiceCard => NeedsChoice ? Read(Frame + CardOffset) : throw new InvalidOperationException("No pending choice.");
    internal int ChoiceCount => NeedsChoice
        ? Math.Min(ChoicePile == Pile.Draw ? _stratagem : CurrentInstruction.Amount, Count(ChoicePile))
        : throw new InvalidOperationException("No pending choice.");
    internal int ShuffleCount => Read(ShuffleCountSlot);
    internal ValueShuffleRng ShuffleRng => new(Read(RngSlot), unchecked((ulong)State[RngSlot + 1]),
        unchecked((ulong)State[RngSlot + 2]), unchecked((ulong)State[RngSlot + 3]), unchecked((ulong)State[RngSlot + 4]));
    internal int EventCount => Read(EventCountSlot);
    internal long EventsExecuted { get; private set; }
    private int Frame => _frameStart + (Read(DepthSlot) - 1) * FrameWidth;
    private CardInstruction CurrentInstruction => _cards[Read(Frame + CardOffset)].Effects[Read(Frame + EffectIndexOffset)];

    internal ResumableDiscardProgram(Card[] cards, IReadOnlyList<int>[] piles, int energy, int block, int discardBlock,
        ValueShuffleRng shuffleRng = default, int[]? comparisons = null, int stratagem = 0, int shuffleBlock = 0,
        bool shuffleBlockFirst = false, CreatureVitals[]? creatures = null, BasicPowerDefinition[]? powers = null)
    {
        if (cards.Length == 0 || cards.Length > 64 || piles.Length != 5 || cards.Count(c => c.Sly) >= MaxFrames)
            throw new NotSupportedException("Compact prototype capacity exceeded.");
        if (cards.Any(c => c.Cost < 0 || c.Effects == null || c.Effects.RequiresPowers && powers == null
                || c.Effects.RequiresTarget && creatures == null || c.Sly && (c.Effects.Count == 0 || c.Effects.RequiresTarget))
            || energy < 0 || block < 0 || discardBlock < 0 || stratagem is < 0 or > 10 || shuffleBlock < 0)
            throw new ArgumentException("Invalid compact root.");
        int[] identities = piles.SelectMany(p => p).ToArray();
        if (identities.Length != cards.Length || !identities.Order().SequenceEqual(Enumerable.Range(0, cards.Length))
            || piles[(int)Pile.Play].Count != 0 || piles[(int)Pile.Hand].Count > 10)
            throw new ArgumentException("Compact root requires unique instances and an idle play pile.");
        // A card can be drawn/played at most once without reshuffling. Reject before execution,
        // so an unsupported random operation can never leave a partially accepted candidate.
        if (comparisons == null && cards.Sum(c => c.Effects.TotalDraw) > piles[(int)Pile.Draw].Count)
            throw new NotSupportedException("Compact shuffle requires captured ordering and random state.");
        if (comparisons != null && (comparisons.Length != cards.Length * cards.Length || cards.Count(c => c.Sly) > 1))
            throw new NotSupportedException("Compact shuffle admits at most one Sly instance and a full comparison matrix.");
        _cards = (Card[])cards.Clone();
        _discardBlock = discardBlock;
        _stratagem = stratagem;
        _shuffleBlock = shuffleBlock;
        _shuffleBlockFirst = shuffleBlockFirst;
        _cardComparer = comparisons == null ? null : new((int[])comparisons.Clone(), cards.Length);
        _frameStart = _pileStart + 5 * (cards.Length + 1);
        State = new ReversibleValueState(_frameStart + MaxFrames * FrameWidth);
        _combat = creatures == null ? null : new(State, creatures);
        _powers = powers == null ? null : new(State, powers);
        _eventStart = State.Count;
        State.Write(EnergySlot, energy);
        if (_combat == null) State.Write(BlockSlot, block);
        else if (Creature(0).Block != block) throw new ArgumentException("Player block disagrees with creature values.");
        WriteRng(shuffleRng);
        for (int p = 0; p < piles.Length; p++)
        {
            State.Write(PileBase((Pile)p), piles[p].Count);
            for (int i = 0; i < piles[p].Count; i++)
                State.Write(PileBase((Pile)p) + 1 + i, piles[p][i]);
        }
    }

    private ResumableDiscardProgram(Card[] cards, int discardBlock, int stratagem, int shuffleBlock,
        bool shuffleBlockFirst, CardComparer? cardComparer, CreatureAttackLayout? combat, BasicPowerLayout? powers, int eventStart, ReversibleValueState state)
    {
        _cards = cards;
        _discardBlock = discardBlock;
        _stratagem = stratagem;
        _shuffleBlock = shuffleBlock;
        _shuffleBlockFirst = shuffleBlockFirst;
        _cardComparer = cardComparer;
        _frameStart = _pileStart + 5 * (cards.Length + 1);
        _eventStart = eventStart;
        _combat = combat;
        _powers = powers;
        State = state;
    }

    internal sealed class Candidate
    {
        private readonly Card[] _cards;
        private readonly int _discardBlock;
        private readonly int _stratagem, _shuffleBlock;
        private readonly bool _shuffleBlockFirst;
        private readonly CardComparer? _cardComparer;
        private readonly ReversibleValueState.FrozenValues _values;
        private readonly CreatureAttackLayout? _combat;
        private readonly BasicPowerLayout? _powers;
        private readonly int _eventStart;
        internal Candidate(ResumableDiscardProgram source)
        {
            _values = source.State.Freeze();
            _cards = source._cards;
            _discardBlock = source._discardBlock;
            _stratagem = source._stratagem;
            _shuffleBlock = source._shuffleBlock;
            _shuffleBlockFirst = source._shuffleBlockFirst;
            _cardComparer = source._cardComparer;
            _combat = source._combat;
            _powers = source._powers;
            _eventStart = source._eventStart;
        }
        internal int PayloadBytes => _values.PayloadBytes;
        internal ResumableDiscardProgram Open() => new(_cards, _discardBlock, _stratagem, _shuffleBlock,
            _shuffleBlockFirst, _cardComparer, _combat, _powers, _eventStart, _values.CreateWorkspace());
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
        return new((EventKind)(packed & 31), (int)((packed >> 5) & 255),
            (int)((packed >> 13) & uint.MaxValue), (packed & (1L << 45)) != 0, (int)((packed >> 46) & 255) - 1, (int)((packed >> 54) & 255));
    }

    internal void Begin(int card, int target = -1)
    {
        if (!Complete || Terminal || Ending || !Contains(Pile.Hand, card)
            || _cards[card].Effects.Count == 0 || Energy < _cards[card].Cost
            || (_cards[card].Effects.RequiresTarget ? target <= 0 || target >= CreatureCount || !CreaturePresent(target) || Creature(target).CurrentHp <= 0 : target != -1))
            throw new InvalidOperationException("Card cannot begin this compact action.");
        State.Write(EnergySlot, Energy - _cards[card].Cost);
        Emit(EventKind.Pay, card, _cards[card].Cost);
        Push(card, false, target);
    }

    internal void SupplyChoice(ReadOnlySpan<int> selected)
    {
        if (!NeedsChoice || selected.Length != ChoiceCount)
            throw new InvalidOperationException("Choice does not match the suspended instruction.");
        for (int i = 0; i < selected.Length; i++)
        {
            if (!Contains(ChoicePile, selected[i]) || selected[..i].Contains(selected[i]))
                throw new InvalidOperationException("Choice contains an absent or repeated instance.");
        }
        if (ChoicePile == Pile.Draw)
        {
            foreach (int card in selected)
            {
                Move(card, Count(Pile.Hand) < 10 ? Pile.Hand : Pile.Discard);
                Emit(EventKind.Retrieve, card);
            }
            if (!_shuffleBlockFirst) GainBlock(ChoiceCard, _shuffleBlock);
            State.Write(Frame + IpOffset, 1);
            return;
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
                    Emit(EventKind.Start, card, _cards[card].Cost, Read(frame + AutoOffset) != 0, Read(frame + TargetOffset));
                    State.Write(frame + IpOffset, 1);
                    break;
                case 1:
                    if (Read(frame + EffectIndexOffset) == _cards[card].Effects.Count)
                        State.Write(frame + IpOffset, 5);
                    else if (!ExecuteInstruction(card)) return;
                    break;
                case 2:
                case 6:
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
                        GainBlock(discarded, _discardBlock);
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
                    else AdvanceInstruction();
                    break;
                case 5:
                    Emit(EventKind.Finish, card, Block > Read(frame + BeforeBlockOffset) ? 1 : 0,
                        Read(frame + AutoOffset) != 0);
                    if (!Ending) Move(card, Pile.Discard);
                    State.Write(DepthSlot, Read(DepthSlot) - 1);
                    break;
                default:
                    throw new InvalidOperationException("Unknown compact instruction.");
            }
        }
    }

    private bool ExecuteInstruction(int card)
    {
        CardInstruction instruction = CurrentInstruction;
        switch (instruction.Kind)
        {
            case CardInstructionKind.AttackTarget:
                Attack(card, Read(Frame + TargetOffset), instruction.Amount);
                break;
            case CardInstructionKind.GainBlock:
                GainBlock(card, _powers?.ModifyBlock(State, 0, instruction.Amount) ?? instruction.Amount);
                break;
            case CardInstructionKind.ApplyWeakToTarget:
                ApplyWeak(card, Read(Frame + TargetOffset), instruction.Amount);
                break;
            case CardInstructionKind.Draw:
                while (Read(Frame + DrawIndexOffset) < instruction.Amount && Count(Pile.Hand) < 10 && !Ending)
                {
                    if (Count(Pile.Draw) == 0 && Count(Pile.Discard) != 0)
                    {
                        Shuffle(card);
                        if (NeedsChoice) return false;
                    }
                    if (Count(Pile.Draw) == 0 || Count(Pile.Hand) >= 10) break;
                    int drawn = CardAt(Pile.Draw, 0);
                    Move(drawn, Pile.Hand);
                    Emit(EventKind.Draw, drawn);
                    State.Write(Frame + DrawIndexOffset, Read(Frame + DrawIndexOffset) + 1);
                }
                break;
            case CardInstructionKind.Discard:
                State.Write(Frame + IpOffset, 2);
                if (ChoiceCount != 0) return false;
                SupplyChoice([]);
                return true;
            default:
                throw new InvalidOperationException("Unknown admitted compact instruction.");
        }
        AdvanceInstruction();
        return true;
    }

    private void AdvanceInstruction()
    {
        State.Write(Frame + EffectIndexOffset, Read(Frame + EffectIndexOffset) + 1);
        State.Write(Frame + DrawIndexOffset, 0);
        State.Write(Frame + IpOffset, 1);
    }

    private void Shuffle(int sourceCard)
    {
        if (_cardComparer == null) throw new InvalidOperationException("Shuffle ordering was not admitted.");
        int count = Count(Pile.Discard);
        Span<int> order = stackalloc int[count];
        for (int i = 0; i < count; i++) order[i] = CardAt(Pile.Discard, i);
        order.Sort(_cardComparer);
        ValueShuffleRng rng = ShuffleRng;
        for (int i = count - 1; i > 0; i--)
        {
            rng = rng.NextInt(i + 1, out int index);
            (order[index], order[i]) = (order[i], order[index]);
        }
        WriteRng(rng);
        State.Write(ShuffleCountSlot, ShuffleCount + 1);
        Emit(EventKind.Shuffle, sourceCard, count);
        foreach (int card in order) { Move(card, Pile.Draw); Emit(EventKind.ShuffleCard, card); }
        if (_shuffleBlockFirst) GainBlock(sourceCard, _shuffleBlock);
        if (_stratagem > 0)
            State.Write(Frame + IpOffset, 6);
        else if (!_shuffleBlockFirst) GainBlock(sourceCard, _shuffleBlock);
    }

    private void WriteRng(ValueShuffleRng rng)
    {
        State.Write(RngSlot, rng.Counter);
        State.Write(RngSlot + 1, unchecked((long)rng.State0));
        State.Write(RngSlot + 2, unchecked((long)rng.State1));
        State.Write(RngSlot + 3, unchecked((long)rng.State2));
        State.Write(RngSlot + 4, unchecked((long)rng.State3));
    }

    private void GainBlock(int card, decimal amount)
    {
        if (amount <= 0 || Ending) return;
        int previous = Block;
        if (_combat == null) State.Write(BlockSlot, (int)Math.Min(999_999_999m, Block + amount));
        else
        {
            CreatureVitals values = Creature(0);
            values.GainBlock(amount);
            _combat.Write(State, 0, values);
        }
        Emit(EventKind.Block, card, Block - previous);
    }

    private void Attack(int card, int target, int amount)
    {
        if (Ending || !CreaturePresent(target) || Creature(target).CurrentHp <= 0) return;
        DamageValues result = _combat!.Damage(State, target, _powers?.ModifyAttack(State, 0, target, amount) ?? amount);
        Emit(EventKind.Damage, card, result.Unblocked, target: target, flags: result.Flags);
        Emit(EventKind.DamageBlocked, card, result.Blocked, target: target);
        Emit(EventKind.DamageOverkill, card, result.Overkill, target: target);
        if (result.Killed)
        {
            _combat.CompleteDeath(State, target);
            _powers?.RemoveOwner(State, target);
            Emit(EventKind.Death, card, target: target);
        }
        Emit(EventKind.AttackFinish, card, target: target);
    }

    private void ApplyWeak(int card, int target, int amount)
    {
        if (amount > 0 && !Ending && CreaturePresent(target) && Creature(target).CurrentHp > 0)
        {
            int index = _powers!.Find(target, BasicPowerKind.Weak);
            int before = _powers.Read(State, index).Amount;
            _powers.Apply(State, index, amount, 0);
            Emit(EventKind.PowerChange, card, _powers.Read(State, index).Amount - before, target: target, flags: (int)BasicPowerKind.Weak);
        }
    }

    private sealed class CardComparer(int[] comparisons, int count) : IComparer<int>
    {
        public int Compare(int left, int right) => comparisons[left * count + right];
    }

    private void Push(int card, bool automatic, int target = -1)
    {
        if (Read(DepthSlot) >= MaxFrames) throw new InvalidOperationException("Compact frame capacity exceeded.");
        State.Write(DepthSlot, Read(DepthSlot) + 1);
        for (int i = 0; i < FrameWidth; i++) State.Write(Frame + i, 0);
        State.Write(Frame + CardOffset, card);
        State.Write(Frame + AutoOffset, automatic ? 1 : 0);
        State.Write(Frame + BeforeBlockOffset, Block);
        State.Write(Frame + TargetOffset, target);
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

    private void Emit(EventKind kind, int card, int value = 0, bool automatic = false, int target = -1, int flags = 0)
    {
        int count = EventCount;
        if (State.Count != checked(_eventStart + count))
            throw new InvalidOperationException("Compact event storage lost its append position.");
        State.Append((long)(byte)kind | (long)(byte)card << 5 | (long)(uint)value << 13
            | (automatic ? 1L << 45 : 0) | (long)(byte)(target + 1) << 46 | (long)(byte)flags << 54);
        State.Write(EventCountSlot, checked(count + 1));
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
