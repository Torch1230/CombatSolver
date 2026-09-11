namespace CombatSolver.Engine.InCombat.Simulation.Compact;

/// <summary>
/// Closed-domain executor. Only its value workspace is authoritative while running.
/// The admitting adapter must prove that draw, discard and play have no unrepresented effects.
/// Production selects it only after complete root admission.
/// </summary>
internal sealed partial class ResumableDiscardProgram
{
    internal readonly record struct Card(int Cost, CardEffectProgram Effects, bool Sly = false,
        Pile ResultPile = Pile.Discard, bool CostsX = false, int CapturedX = 0, CardCategory Category = CardCategory.Other,
        bool Ethereal = false, RandomDrawCost? DrawCost = null, int? HandEndDamage = null, bool Unplayable = false, bool Retain = false, bool SingleTurnSly = false,
        bool EnchantmentInitiallyDisabled = false);
    internal enum Pile { Hand, Draw, Discard, Play, Exhaust, Removed, Unplaced }
    internal enum EventKind { Pay, Start, Draw, Select, SelectedCard, Discard, Block, Finish, Shuffle, ShuffleCard, Retrieve, Damage, DamageBlocked, DamageOverkill, AttackFinish, Death, PowerChange, ResultMoved, Generated, CostChanged, HandEndMoved, HandEndStart, HandEndFinish, BeginSide, ResetEnergy, CleanupCards, CommitPlayerTurnHistory, GainEnergy, DoomApplied, Kill, SummonPet, KeywordAdded, EnchantmentStart, EnchantmentFinish, PanacheStart, PanacheFinish, CardHooksFinished, PlayerPhaseChanged }
    internal enum PlayerPhase { End, None, Start, Play }
    internal readonly record struct Event(EventKind Kind, int Card, int Value, bool Automatic, int Target = -1, int Flags = 0, int Dealer = -1)
    {
        internal long Data => (long)(uint)Card | (long)(uint)Value << 32;
        internal long Metadata => (uint)Kind <= byte.MaxValue && (uint)Flags <= byte.MaxValue && Dealer is >= -1 and < 254
            ? (long)(byte)Kind | (long)(byte)Flags << 8 | (long)(uint)Target << 16 | (Automatic ? 1L << 48 : 0) | (long)(Dealer + 1) << 49
            : throw new ArgumentOutOfRangeException(nameof(Kind), "Compact event tags exceed their encoding.");
        internal static Event Decode(long data, long metadata) => new((EventKind)(metadata & 255), unchecked((int)data),
            (int)(data >> 32), (metadata & (1L << 48)) != 0, unchecked((int)(metadata >> 16)), (int)((metadata >> 8) & 255), (int)((metadata >> 49) & 255) - 1);
    }
    // The low three bits remain the native damage-result flags; the upper bits describe
    // command provenance without retaining a model or attributing indirect damage to a card.
    [Flags]
    internal enum DamageTraits { Unpowered = 8, Unblockable = 16, NoDealer = 32, NoCard = 64, Poison = 128 }
    private const int EnergySlot = 0, BlockSlot = 1, DepthSlot = 2, AttackCardStartsSlot = 3;
    private const int RngSlot = 4, ShuffleCountSlot = 9;
    private const int FrameWidth = 22, MaxFrames = 8, PileCount = 7;
    private const int CardOffset = 0, IpOffset = 1, AutoOffset = 2, BeforeBlockOffset = 3;
    private const int SelectedCountOffset = 4, NextAutoOffset = 5, SelectedOffset = 6;
    private const int DrawIndexOffset = 16, TargetOffset = 17, EffectIndexOffset = 18, EnergyValueOffset = 19;
    private const int FirstDrawnOffset = 20;
    private const int DrawResumeIpOffset = 21;
    private readonly Card[] _definitions;
    private readonly int _rootCardCount;
    private readonly CreatureAttackLayout? _combat;
    private readonly BasicPowerLayout? _powers;
    private readonly PanachePowerLayout? _panache;
    private readonly int _discardBlock;
    private readonly int _stratagem;
    private readonly int _shuffleBlock;
    private readonly bool _shuffleBlockFirst;
    private readonly bool _handEndAdmitted;
    private readonly bool _powerPhasesAdmitted;
    private readonly MonsterEffectProgram[]? _monsterMoves;
    private readonly DeterministicMonsterAiLayout? _monsterAi;
    private readonly CompactRoundLayout? _round;
    private readonly CardComparer? _cardComparer;
    private readonly InstanceComparer? _instanceComparer;
    private const int FrameStart = 10;
    private readonly ReversibleValueBuffer[] _piles;
    private readonly ReversibleValueBuffer _cardInstances;
    private readonly ReversibleValueBuffer _events;
    private readonly RandomDrawCostLayout? _drawCosts;
    internal ReversibleValueState State { get; }
    internal int Energy => Read(EnergySlot);
    internal int AttackCardStarts => Read(AttackCardStartsSlot);
    internal int CardCount => _cardInstances.Count(State);
    // Existing instances cannot change definition in this admitted program. Only their
    // captured X, added keywords and one-shot status are mutable; generated identities share the buffer.
    internal int DefinitionIndex(int card) => (uint)card < (uint)_rootCardCount ? card : (int)(_cardInstances.Read(State, card) & int.MaxValue);
    internal Card Definition(int card) => _definitions[DefinitionIndex(card)];
    internal int CapturedX(int card) => CardInstance(card).CapturedX;
    internal bool EnchantmentDisabled(int card) => CardInstance(card).EnchantmentDisabled;
    internal int LocalEnergyCost(int card) => Definition(card).DrawCost == null ? Definition(card).Cost
        : _drawCosts!.Current(State, card, Definition(card).Cost);
    internal bool HasGlobalEnergyCosts => _powers?.HasGlobalEnergyCosts ?? false;
    internal int EnergyCost(int card)
    {
        var definition = Definition(card);
        int local = LocalEnergyCost(card);
        if (definition.Cost < 0 || definition.CostsX) return definition.Cost;
        if (!HasGlobalEnergyCosts || Ending) return Math.Max(0, local);
        int borrowed = _powers?.FindOrDefault(0, BasicPowerKind.BorrowedTime) ?? -1;
        long cost = (long)local + (borrowed < 0 ? 0 : Power(borrowed).Amount);
        int veil = _powers?.FindOrDefault(0, BasicPowerKind.Veilpiercer) ?? -1;
        // The late free-cost hook runs after the early additive pass, and only in
        // these two piles. X and negative base costs never enter either pass.
        if (veil >= 0 && Power(veil).Amount > 0 && IsEthereal(card)
            && (Contains(Pile.Hand, card) || Contains(Pile.Play, card))) return 0;
        return Math.Max(0, checked((int)cost));
    }
    internal int CostModifierCount(int card) => Definition(card).DrawCost == null ? 0 : _drawCosts!.Count(State, card);
    internal int CostModifierAt(int card, int index) => _drawCosts!.At(State, card, index);
    internal ValueRng? EnergyCostRng => _drawCosts?.Rng(State);
    internal Pile ResultPile(int card) => Definition(card).ResultPile;
    internal bool CardRemoved(int card) => Contains(Pile.Removed, card);
    internal bool CardUnplaced(int card) => Contains(Pile.Unplaced, card);
    internal bool CardValuesInvariant => _definitions.All(card => card.ResultPile == Pile.Discard && !card.Ethereal && !card.CostsX && !card.SingleTurnSly
        && !card.Effects.GeneratesCards && !card.Effects.ExhaustsCards && !card.Effects.ChangesKeywords && !card.Effects.HasOneShotEnchantment && card.DrawCost == null)
        && _monsterMoves?.All(move => !move.GeneratesCards) != false;
    internal int Block => _combat?.Read(State, 0).Block ?? Read(BlockSlot);
    internal int PowerCount => _powers?.Count ?? 0;
    internal BasicPowerValues Power(int index) => _powers!.Read(State, index);
    internal BasicPowerDefinition PowerDefinition(int index) => _powers!.Definition(index);
    internal int CreatureCount => _combat?.Count ?? 0;
    internal int EnemyEnd => _combat?.EnemyEnd ?? 0;
    internal int PetIndex => _combat?.Pet ?? -1;
    internal bool PetSummoned => _combat?.PetSummoned(State) ?? false;
    internal CreatureVitals Creature(int index) => _combat!.Read(State, index);
    internal bool CreaturePresent(int index) => _combat!.Present(State, index);
    internal bool CreatureDeathCompleted(int index) => _combat!.DeathCompleted(State, index);
    internal bool Ending => _combat?.IsEnding(State) ?? false;
    internal bool Terminal => _combat?.Terminal(State) ?? false;
    internal bool DefeatTerminal => _combat?.DefeatTerminal(State) ?? false;
    internal bool CheckWinCondition()
    {
        if (!Complete) throw new InvalidOperationException("Terminal check requires a completed command.");
        bool terminal = _combat?.CheckWinCondition(State) ?? false;
        if (terminal) _round?.LockTerminal(State);
        return terminal;
    }
    internal bool Complete => Read(DepthSlot) == 0;
    internal bool NeedsChoice => !Complete && Read(Frame + IpOffset) is 2 or 6 or 8 or 9 or 10;
    internal bool ChoiceRetrieves => NeedsChoice && Read(Frame + IpOffset) == 6;
    internal bool ChoiceExhausts => NeedsChoice && Read(Frame + IpOffset) == 8;
    internal bool ChoiceReturnsFromDiscard => NeedsChoice && Read(Frame + IpOffset) == 9;
    internal Pile ChoicePile => ChoiceReturnsFromDiscard ? Pile.Discard : ChoiceRetrieves || ChoiceExhausts ? Pile.Draw : Pile.Hand;
    internal int ChoiceCard => NeedsChoice ? Read(Frame + CardOffset) : throw new InvalidOperationException("No pending choice.");
    internal bool ChoiceAutomatic => NeedsChoice && Read(Frame + AutoOffset) != 0;
    internal int ChoiceRequestedCount => NeedsChoice
        ? ChoiceRetrieves ? _stratagem : CurrentInstruction.Amount
        : throw new InvalidOperationException("No pending choice.");
    internal int ChoiceCount => NeedsChoice
        ? Math.Min(ChoiceRequestedCount, ChoiceKeyword == CardKeywordFlags.None ? Count(ChoicePile) : KeywordChoiceCount())
        : throw new InvalidOperationException("No pending choice.");
    internal int ShuffleCount => Read(ShuffleCountSlot);
    internal ValueRng ShuffleRng => new(Read(RngSlot), unchecked((ulong)State[RngSlot + 1]),
        unchecked((ulong)State[RngSlot + 2]), unchecked((ulong)State[RngSlot + 3]), unchecked((ulong)State[RngSlot + 4]));
    internal int EventCount => _events.Count(State) / 2;
    internal long EventsExecuted { get; private set; }
    private int Frame => FrameStart + (Read(DepthSlot) - 1) * FrameWidth;
    private CardInstruction CurrentInstruction => Read(Frame + CardOffset) < 0 ? RoundInstruction
        : Definition(Read(Frame + CardOffset)).Effects[Read(Frame + EffectIndexOffset)];

    internal ResumableDiscardProgram(Card[] cards, IReadOnlyList<int>[] piles, int energy, int block, int discardBlock,
        ValueRng shuffleRng = default, int[]? comparisons = null, int stratagem = 0, int shuffleBlock = 0,
        bool shuffleBlockFirst = false, CreatureVitals[]? creatures = null, BasicPowerDefinition[]? powers = null, Card[]? generatedCards = null,
        ValueRng? energyCostRng = null, bool handEndAdmitted = false, MonsterEffectProgram[]? monsterMoves = null, bool powerPhasesAdmitted = false, DeterministicMonsterAi? monsterAi = null, CompactRoundRoot? round = null, int pet = -1, int attackCardStarts = 0, PanachePowerValues[]? panache = null)
    {
        if (attackCardStarts < 0) throw new ArgumentOutOfRangeException(nameof(attackCardStarts));
        Card[] definitions = [.. cards, .. generatedCards ?? []];
        if (panache is { Length: > 0 } && (powers == null || creatures == null || panache.Any(value => value.Amount is <= 0 or > 999_999_999
            || value.Applier < -1 || value.Applier >= creatures.Length || value.Order <= 0 || value.CardsLeft is < 0 or > 5)))
            throw new NotSupportedException("Panache roots require admitted positive player-owned instances and captured counters.");
        if (cards.Length == 0 || piles.Length != 5 || cards.Count(c => c.Sly || c.SingleTurnSly) >= MaxFrames
            || generatedCards?.Any(card => card.Sly || card.SingleTurnSly || card.DrawCost != null) == true)
            throw new NotSupportedException("Compact prototype capacity exceeded.");
        if (definitions.Any(c => c.Cost < 0 && !(c.Unplayable && c.Cost == -1) || c.CapturedX is < 0 or > 999_999_999 || c.Effects == null || !Enum.IsDefined(c.Category)
                || c.HandEndDamage is < 0 or > 999_999_999 || c.HandEndDamage != null && creatures == null
                || c.Unplayable && (c.CostsX || c.Sly || c.SingleTurnSly || c.Effects.Count != 0 || c.DrawCost != null)
                || c.DrawCost != null && c.CostsX || c.Effects.RequiresPowers && (powers == null || creatures == null) || c.Effects.RequiresEnergyX && !c.CostsX
                || c.ResultPile is not (Pile.Discard or Pile.Exhaust or Pile.Removed)
                || c.Effects.RequiresPet && pet < 0 || c.Effects.RequiresTarget && creatures == null || (c.Sly || c.SingleTurnSly) && (c.Effects.Count == 0 || c.Effects.RequiresTarget))
            || energy is < 0 or > 999_999_999 || block < 0 || discardBlock < 0 || stratagem is < 0 or > 10 || shuffleBlock < 0)
            throw new ArgumentException("Invalid compact root.");
        int[] identities = piles.SelectMany(p => p).ToArray();
        if (identities.Length != cards.Length || !identities.Order().SequenceEqual(Enumerable.Range(0, cards.Length))
            || piles[(int)Pile.Play].Count != 0 || piles[(int)Pile.Hand].Count > 10)
            throw new ArgumentException("Compact root requires unique instances and an idle play pile.");
        // A card can be drawn/played at most once without reshuffling. Reject before execution,
        // so an unsupported random operation can never leave a partially accepted candidate.
        if (comparisons == null && definitions.Sum(c => c.Effects.TotalDraw) > piles[(int)Pile.Draw].Count)
            throw new NotSupportedException("Compact shuffle requires captured ordering and random state.");
        if (comparisons != null && (comparisons.Length != definitions.Length * definitions.Length || cards.Count(c => c.Sly || c.SingleTurnSly) > 1))
            throw new NotSupportedException("Compact shuffle admits at most one Sly instance and a full comparison matrix.");
        foreach (var definition in definitions)
        for (int instruction = 0; instruction < definition.Effects.Count; instruction++)
        {
            var effect = definition.Effects[instruction];
            if (effect.Kind == CardInstructionKind.GenerateCards && (effect.CardTemplate < cards.Length || effect.CardTemplate >= definitions.Length))
                throw new NotSupportedException("Generation references a template outside the captured closure.");
            if (effect.Placement == CardGenerationPlacement.RandomDraw && comparisons == null)
                throw new NotSupportedException("Random generation requires captured ordering and random state.");
        }
        if (monsterMoves != null)
        {
            if (creatures == null || powers == null || monsterMoves.Length == 0 || monsterMoves.Any(move => move == null))
                throw new ArgumentException("Monster commands require admitted creature and Power layouts.");
            foreach (var move in monsterMoves)
            for (int index = 0; index < move.Count; index++)
                if (move[index].Kind == MonsterInstructionKind.GenerateCards
                    && (move[index].CardTemplate < cards.Length || move[index].CardTemplate >= definitions.Length))
                    throw new NotSupportedException("Monster generation references a template outside the captured closure.");
        }
        if (powerPhasesAdmitted && (creatures == null || powers == null))
            throw new ArgumentException("Power phases require creature and Power layouts.");
        if (monsterAi != null && (monsterMoves == null || monsterAi.MoveCount != monsterMoves.Length
            || creatures == null || monsterAi.Owner >= creatures.Length))
            throw new ArgumentException("Monster AI requires matching captured commands and owner.");
        if (round != null && (!handEndAdmitted || !powerPhasesAdmitted || monsterAi == null || monsterAi.Owner != 1
            || (creatures?.Length != (pet < 0 ? 2 : 3) || pet >= 0 && pet != 2) || comparisons == null
            || round.Value.TurnStartSummon > 0 && pet < 0
            || powers!.Any(power => power.Owner == 0 && power.Kind == BasicPowerKind.Poison && power.Amount != 0)))
            throw new NotSupportedException("Round closure requires one enemy, all phases, ordering and no player Poison.");
        ValidateBlockReturns(definitions, powers);
        _definitions = definitions;
        _rootCardCount = cards.Length;
        _discardBlock = discardBlock;
        _stratagem = stratagem;
        _shuffleBlock = shuffleBlock;
        _shuffleBlockFirst = shuffleBlockFirst;
        _handEndAdmitted = handEndAdmitted;
        _powerPhasesAdmitted = powerPhasesAdmitted;
        _monsterMoves = monsterMoves == null ? null : (MonsterEffectProgram[])monsterMoves.Clone();
        State = new ReversibleValueState(FrameStart + MaxFrames * FrameWidth);
        _piles = Enumerable.Range(0, PileCount).Select(_ => new ReversibleValueBuffer(State)).ToArray();
        _cardInstances = new(State);
        _cardComparer = comparisons == null ? null : new((int[])comparisons.Clone(), definitions.Length);
        _instanceComparer = _cardComparer == null ? null : new(this, _cardComparer);
        _combat = creatures == null ? null : new(State, creatures, pet);
        _powers = powers == null ? null : new(State, powers, panache?.Select(value => value.Order).DefaultIfEmpty().Max() ?? 0);
        _panache = panache is { Length: > 0 } || definitions.Any(card => card.Effects.CreatesPanache) ? new(State, panache ?? []) : null;
        _events = new(State);
        _monsterAi = monsterAi == null ? null : new(State, monsterAi);
        _round = round is { } roundRoot ? new(State, roundRoot) : null;
        _drawCosts = cards.Any(card => card.DrawCost != null) ? new(State, cards.Select(card => card.DrawCost).ToArray(),
            energyCostRng ?? throw new NotSupportedException("Random draw costs require a captured RNG stream.")) : null;
        State.Write(EnergySlot, energy);
        State.Write(AttackCardStartsSlot, attackCardStarts);
        if (_combat == null) State.Write(BlockSlot, block);
        else if (Creature(0).Block != block) throw new ArgumentException("Player block disagrees with creature values.");
        WriteRng(shuffleRng);
        for (int card = 0; card < cards.Length; card++) _cardInstances.Append(State,
            [new CardInstanceValue(card, cards[card].CapturedX, EnchantmentDisabled: cards[card].EnchantmentInitiallyDisabled).Data]);
        for (int p = 0; p < piles.Length; p++)
            foreach (int card in piles[p]) _piles[p].Append(State, [card]);
    }

    private static void ValidateBlockReturns(Card[] cards, BasicPowerDefinition[]? powers)
    {
        if (powers == null || !powers.Any(power => power.Owner == 0 && power.Kind == BasicPowerKind.Frail && power.Amount != 0)) return;
        var returns = cards.SelectMany(card => Enumerable.Range(0, card.Effects.Count).Select(index => card.Effects[index]))
            .Where(instruction => instruction.Kind == CardInstructionKind.GainBlockAndApplyPower).ToArray();
        if (returns.Length == 0) return;
        long minimum = powers.Single(power => power.Owner == 0 && power.Kind == BasicPowerKind.Dexterity).Amount;
        long maximum = minimum;
        foreach (var card in cards)
        for (int index = 0; index < card.Effects.Count; index++)
        {
            var instruction = card.Effects[index];
            if (instruction.Kind != CardInstructionKind.ApplyBasicPower || instruction.Power != BasicPowerKind.Dexterity
                || instruction.Target != CardInstructionTarget.Owner) continue;
            if (instruction.EnergyXMultiplier != 0) { minimum = -999_999_999; maximum = 999_999_999; }
            else if (instruction.Amount > 0)
                maximum = card.ResultPile == Pile.Removed ? Math.Min(999_999_999, maximum + instruction.Amount) : 999_999_999;
            else if (instruction.Amount < 0)
                minimum = card.ResultPile == Pile.Removed ? Math.Max(-999_999_999, minimum + instruction.Amount) : -999_999_999;
        }
        // A 0.75 return creates a native zero-amount Power instance. This domain uses zero
        // as absence, so reject the entire reachable interval before any candidate executes.
        if (returns.Any(instruction => 1L - instruction.Amount >= minimum && 1L - instruction.Amount <= maximum))
            throw new NotSupportedException("Compact block return can create an unrepresented zero-amount Power instance.");
    }

    private ResumableDiscardProgram(Card[] cards, int rootCardCount, int discardBlock, int stratagem, int shuffleBlock,
        bool shuffleBlockFirst, CardComparer? cardComparer, CreatureAttackLayout? combat, BasicPowerLayout? powers,
        ReversibleValueBuffer[] piles, ReversibleValueBuffer cardInstances, ReversibleValueBuffer events, RandomDrawCostLayout? drawCosts,
        bool handEndAdmitted, MonsterEffectProgram[]? monsterMoves, bool powerPhasesAdmitted, DeterministicMonsterAiLayout? monsterAi, CompactRoundLayout? round, PanachePowerLayout? panache, ReversibleValueState state)
    {
        _definitions = cards;
        _rootCardCount = rootCardCount;
        _discardBlock = discardBlock;
        _stratagem = stratagem;
        _shuffleBlock = shuffleBlock;
        _shuffleBlockFirst = shuffleBlockFirst;
        _handEndAdmitted = handEndAdmitted;
        _powerPhasesAdmitted = powerPhasesAdmitted;
        _monsterMoves = monsterMoves;
        _monsterAi = monsterAi;
        _round = round;
        _cardComparer = cardComparer;
        _instanceComparer = cardComparer == null ? null : new(this, cardComparer);
        _piles = piles;
        _cardInstances = cardInstances;
        _events = events;
        _drawCosts = drawCosts;
        _combat = combat;
        _powers = powers;
        _panache = panache;
        State = state;
    }

    internal sealed class Candidate
    {
        private readonly Card[] _definitions;
        private readonly int _rootCardCount;
        private readonly int _discardBlock;
        private readonly int _stratagem, _shuffleBlock;
        private readonly bool _shuffleBlockFirst;
        private readonly bool _handEndAdmitted;
        private readonly bool _powerPhasesAdmitted;
        private readonly MonsterEffectProgram[]? _monsterMoves;
        private readonly DeterministicMonsterAiLayout? _monsterAi;
        private readonly CompactRoundLayout? _round;
        private readonly CardComparer? _cardComparer;
        private readonly ReversibleValueState.FrozenValues _values;
        private readonly CreatureAttackLayout? _combat;
        private readonly BasicPowerLayout? _powers;
        private readonly PanachePowerLayout? _panache;
        private readonly ReversibleValueBuffer[] _piles;
        private readonly ReversibleValueBuffer _cardInstances, _events;
        private readonly RandomDrawCostLayout? _drawCosts;
        internal Candidate(ResumableDiscardProgram source)
        {
            _values = source.State.Freeze();
            _definitions = source._definitions;
            _rootCardCount = source._rootCardCount;
            _piles = source._piles;
            _cardInstances = source._cardInstances;
            _discardBlock = source._discardBlock;
            _stratagem = source._stratagem;
            _shuffleBlock = source._shuffleBlock;
            _shuffleBlockFirst = source._shuffleBlockFirst;
            _handEndAdmitted = source._handEndAdmitted;
            _powerPhasesAdmitted = source._powerPhasesAdmitted;
            _monsterMoves = source._monsterMoves;
            _monsterAi = source._monsterAi;
            _round = source._round;
            _cardComparer = source._cardComparer;
            _combat = source._combat;
            _powers = source._powers;
            _panache = source._panache;
            _events = source._events;
            _drawCosts = source._drawCosts;
        }
        internal int PayloadBytes => _values.PayloadBytes;
        internal ResumableDiscardProgram Open() => new(_definitions, _rootCardCount, _discardBlock, _stratagem, _shuffleBlock,
            _shuffleBlockFirst, _cardComparer, _combat, _powers, _piles, _cardInstances, _events, _drawCosts, _handEndAdmitted, _monsterMoves, _powerPhasesAdmitted, _monsterAi, _round, _panache, _values.CreateWorkspace());
        internal void RestoreInto(ResumableDiscardProgram workspace) => workspace.State.Restore(_values);
    }

    internal Candidate Freeze() => new(this);
    internal int Count(Pile pile) => _piles[(int)pile].Count(State);
    internal int CardAt(Pile pile, int index)
        => (uint)index < (uint)Count(pile) ? checked((int)_piles[(int)pile].Read(State, index)) : throw new ArgumentOutOfRangeException(nameof(index));
    internal int[] Cards(Pile pile) => Enumerable.Range(0, Count(pile)).Select(i => CardAt(pile, i)).ToArray();
    internal Event EventAt(int index)
    {
        if ((uint)index >= (uint)EventCount) throw new ArgumentOutOfRangeException(nameof(index));
        long data = _events.Read(State, index * 2), metadata = _events.Read(State, index * 2 + 1);
        return Event.Decode(data, metadata);
    }

    internal void Begin(int card, int target = -1)
    {
        if (!Complete || Terminal || Ending || !Contains(Pile.Hand, card)
            || Definition(card).Unplayable || Definition(card).Effects.Count == 0 || !Definition(card).CostsX && Energy < EnergyCost(card)
            || (Definition(card).Effects.RequiresTarget ? target <= 0 || target >= EnemyEnd || !CreaturePresent(target) || Creature(target).CurrentHp <= 0 : target != -1))
            throw new InvalidOperationException($"Card cannot begin this compact action: card={card}, target={target}, "
                + $"energy={Energy}, cost={EnergyCost(card)}, complete={Complete}, ending={Ending}, terminal={Terminal}, "
                + $"inHand={Contains(Pile.Hand, card)}, requiresTarget={Definition(card).Effects.RequiresTarget}.");
        int energy = Definition(card).CostsX ? Energy : EnergyCost(card);
        State.Write(EnergySlot, Energy - energy);
        Emit(EventKind.Pay, card, energy);
        Push(card, false, target, energy);
    }

    internal void SupplyChoice(ReadOnlySpan<int> selected)
    {
        if (!NeedsChoice || selected.Length != ChoiceCount)
            throw new InvalidOperationException("Choice does not match the suspended instruction.");
        for (int i = 0; i < selected.Length; i++)
        {
            if (!IsChoiceOption(selected[i]) || selected[..i].Contains(selected[i]))
                throw new InvalidOperationException("Choice contains an absent or repeated instance.");
        }
        if (ChoiceKeyword != CardKeywordFlags.None)
        {
            Emit(EventKind.Select, ChoiceCard, selected.Length);
            foreach (int card in selected)
            {
                Emit(EventKind.SelectedCard, card);
                AddKeyword(card, ChoiceKeyword);
            }
            AdvanceInstruction();
            return;
        }
        if (ChoiceExhausts || ChoiceReturnsFromDiscard)
        {
            bool exhaust = ChoiceExhausts;
            Emit(EventKind.Select, ChoiceCard, selected.Length);
            foreach (int card in selected)
            {
                Emit(EventKind.SelectedCard, card);
                if (Ending) continue;
                Move(card, exhaust ? Pile.Exhaust : Count(Pile.Hand) < 10 ? Pile.Hand : Pile.Discard);
                if (exhaust) Emit(EventKind.ResultMoved, card, (int)Pile.Exhaust);
                else Emit(EventKind.Retrieve, card);
            }
            AdvanceInstruction();
            return;
        }
        if (ChoiceRetrieves)
        {
            foreach (int card in selected)
            {
                Move(card, Count(Pile.Hand) < 10 ? Pile.Hand : Pile.Discard);
                Emit(EventKind.Retrieve, card);
            }
            if (!_shuffleBlockFirst) GainBlock(ChoiceCard, _shuffleBlock);
            State.Write(Frame + IpOffset, Read(Frame + DrawResumeIpOffset));
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
                    BeforeCardPlayed(card);
                    Emit(EventKind.Start, card, Read(frame + EnergyValueOffset), Read(frame + AutoOffset) != 0, Read(frame + TargetOffset));
                    if (Definition(card).Category == CardCategory.Attack)
                        State.Write(AttackCardStartsSlot, checked(AttackCardStarts + 1));
                    State.Write(frame + IpOffset, 1);
                    break;
                case 1:
                    if (Read(frame + EffectIndexOffset) == InstructionCount(card))
                        State.Write(frame + IpOffset, 5);
                    else if (!ExecuteInstruction(card)) return;
                    break;
                case 2:
                case 6:
                case 8:
                case 9:
                case 10:
                    return;
                case 11:
                    if (!DrawCards(card, CurrentInstruction.Amount, 11)) return;
                    Emit(EventKind.EnchantmentFinish, card);
                    AdvanceInstruction();
                    break;
                case 3:
                    // Native batch discard moves every selected card and runs its hooks before
                    // starting any Sly card. The selected instance list survives nested choices.
                    if (CurrentInstruction.Kind == CardInstructionKind.Discard)
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
                    State.Write(frame + IpOffset, CurrentInstruction.Kind == CardInstructionKind.DiscardHandAndDraw ? 7 : 4);
                    break;
                case 7:
                    if (!DrawCards(card, Read(frame + SelectedCountOffset), 7)) return;
                    State.Write(frame + IpOffset, 4);
                    break;
                case 4:
                    int next = Read(frame + NextAutoOffset);
                    if (next < Read(frame + SelectedCountOffset))
                    {
                        State.Write(frame + NextAutoOffset, next + 1);
                        int discarded = Read(frame + SelectedOffset + next);
                        if (IsSly(discarded)) Push(discarded, true);
                    }
                    else AdvanceInstruction();
                    break;
                case 5:
                    if (card < 0) { CompletePlayerSideStart(); Emit(EventKind.PlayerPhaseChanged, -1, (int)PlayerPhase.Play); State.Write(DepthSlot, 0); break; }
                    Emit(EventKind.Finish, card, Block > Read(frame + BeforeBlockOffset) ? 1 : 0,
                        Read(frame + AutoOffset) != 0, flags: (IsEthereal(card) ? 1 : 0) | (PanacheCount > 0 ? 2 : 0));
                    if (PanacheCount > 0)
                    {
                        AfterCardPanache(card);
                        Emit(EventKind.CardHooksFinished, card);
                    }
                    if (ResultPile(card) == Pile.Removed || !Ending)
                    {
                        Move(card, ResultPile(card));
                        Emit(EventKind.ResultMoved, card, (int)ResultPile(card));
                    }
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
            case CardInstructionKind.AddPanachePower:
                if (!Ending && instruction.Amount > 0)
                    _panache!.Add(State, new(instruction.Amount, 0, _powers!.NextOrder(State)));
                break;
            case CardInstructionKind.GainEnergy:
                if (!Ending && instruction.Amount > 0)
                {
                    int before = Energy;
                    State.Write(EnergySlot, Math.Min(999_999_999L, (long)Energy + instruction.Amount));
                    Emit(EventKind.GainEnergy, card, Energy - before);
                }
                break;
            case CardInstructionKind.GenerateCards:
                GenerateCards(instruction.CardTemplate, checked(instruction.Amount + instruction.EnergyXMultiplier * Read(Frame + EnergyValueOffset)),
                    creator: 0, instruction.Placement);
                break;
            case CardInstructionKind.SummonPet:
                int repeats = instruction.RepeatForEnergyX ? Read(Frame + EnergyValueOffset) : 1;
                for (int index = 0; index < repeats && !Ending; index++) SummonPet(card, instruction.Amount);
                break;
            case CardInstructionKind.PetAttackTarget:
                AttackCreature(card, PetIndex, Read(Frame + TargetOffset),
                    instruction.Amount + (decimal)instruction.Multiplier * Creature(PetIndex).CurrentHp);
                break;
            case CardInstructionKind.AttackTarget:
                AttackCreature(card, 0, Read(Frame + TargetOffset), instruction.Amount, instruction.AttackMultiplierPower);
                break;
            case CardInstructionKind.LoseEnemyHp:
                int receiver = Read(Frame + TargetOffset);
                if (!Ending && CreaturePresent(receiver) && Creature(receiver).CurrentHp > 0)
                    RecordDamage(card, receiver, _combat!.Damage(State, receiver, instruction.Amount, unblockable: true),
                        DamageTraits.Unpowered | DamageTraits.Unblockable);
                break;
            case CardInstructionKind.GainBlock:
                GainBlock(card, _powers?.ModifyBlock(State, 0, instruction.Amount) ?? instruction.Amount);
                break;
            case CardInstructionKind.GainBlockFromPowerSum:
                int sum = 0;
                for (int target = 1; target < EnemyEnd; target++)
                    if (CreaturePresent(target) && Creature(target).CurrentHp > 0)
                        sum = checked(sum + _powers!.Amount(State, target, instruction.Power));
                decimal block = instruction.Amount + (decimal)instruction.Multiplier * sum;
                GainBlock(card, _powers!.ModifyBlock(State, 0, block));
                break;
            case CardInstructionKind.GainBlockAndApplyPower:
                decimal returned = _powers!.ModifyBlock(State, 0, instruction.Amount);
                GainBlock(card, returned);
                ApplyPower(card, 0, instruction.Power, (int)returned);
                break;
            case CardInstructionKind.ApplyBasicPower:
                int amount = checked(instruction.Amount + instruction.EnergyXMultiplier * CapturedX(card));
                if (instruction.Target == CardInstructionTarget.AllEnemies)
                {
                    // One command visits the captured roster in order. The next instruction
                    // starts only after all its targets, as in native bulk PowerCmd.Apply.
                    for (int target = 1; target < EnemyEnd; target++) ApplyPower(card, target, instruction.Power, amount);
                }
                else ApplyPower(card, instruction.Target == CardInstructionTarget.Owner ? 0 : Read(Frame + TargetOffset), instruction.Power, amount);
                break;
            case CardInstructionKind.ApplyPowerAtLeastCurrent:
                int growthTarget = Read(Frame + TargetOffset);
                int currentAmount = _powers!.Amount(State, growthTarget, instruction.Power);
                // Within this closed domain, Artifact is the only incoming modifier and
                // consumes even a native zero-offset stack at the cap. Clamp on commit so
                // that this modifier still runs; no admitted observer reads the request.
                int growth = Math.Max(instruction.Amount, currentAmount);
                ApplyPower(card, growthTarget, instruction.Power, growth);
                break;
            case CardInstructionKind.ApplyTemporaryStrengthLoss:
                if (instruction.Target == CardInstructionTarget.AllEnemies)
                {
                    for (int target = 1; target < EnemyEnd; target++) ApplyTemporaryStrengthLoss(card, target, instruction.Amount);
                }
                else ApplyTemporaryStrengthLoss(card, Read(Frame + TargetOffset), instruction.Amount);
                break;
            case CardInstructionKind.TriggerBasicPower:
                if (instruction.Target == CardInstructionTarget.AllEnemies)
                {
                    for (int target = 1; target < EnemyEnd; target++) TriggerPoison(card, target);
                }
                else TriggerPoison(card, Read(Frame + TargetOffset));
                break;
            case CardInstructionKind.SkipIfDrawnCardNotType:
                int first = Read(Frame + FirstDrawnOffset);
                if (first < 0 || Definition(first).Category != instruction.RequiredCategory)
                    State.Write(Frame + EffectIndexOffset, Read(Frame + EffectIndexOffset) + instruction.Amount);
                break;
            case CardInstructionKind.SkipIfTargetLacksPower:
                if (_powers!.Amount(State, Read(Frame + TargetOffset), instruction.Power) == 0)
                    State.Write(Frame + EffectIndexOffset, Read(Frame + EffectIndexOffset) + instruction.Amount);
                break;
            case CardInstructionKind.Draw:
                if (!DrawCards(card, instruction.Amount, 1)) return false;
                break;
            case CardInstructionKind.DrawOnce:
                if (EnchantmentDisabled(card)) break;
                _cardInstances.Write(State, card, (CardInstance(card) with { EnchantmentDisabled = true }).Data);
                Emit(EventKind.EnchantmentStart, card);
                // Resume inside the already-started enchantment after shuffle selection.
                State.Write(Frame + IpOffset, 11);
                return true;
            case CardInstructionKind.DiscardHandAndDraw:
                if (Ending) break;
                int count = Count(Pile.Hand);
                State.Write(Frame + SelectedCountOffset, count);
                State.Write(Frame + NextAutoOffset, 0);
                for (int index = 0; index < count; index++) State.Write(Frame + SelectedOffset + index, CardAt(Pile.Hand, index));
                State.Write(Frame + IpOffset, 3);
                return true;
            case CardInstructionKind.Discard:
                State.Write(Frame + IpOffset, 2);
                if (ChoiceCount != 0) { CompletePlayerSideStart(); return false; }
                SupplyChoice([]);
                return true;
            case CardInstructionKind.ApplyKeywordFromHand:
                if (Ending) break;
                State.Write(Frame + IpOffset, 10);
                if (ChoiceCount != 0) return false;
                AdvanceInstruction();
                return true;
            case CardInstructionKind.ExhaustFromDraw:
                // An empty native pile has no selector or selected-card command.
                if (Count(Pile.Draw) == 0 || instruction.Amount == 0) break;
                State.Write(Frame + IpOffset, 8);
                return false;
            case CardInstructionKind.RetrieveFromDiscard:
                if (Ending || Count(Pile.Discard) == 0 || instruction.Amount == 0) break;
                State.Write(Frame + IpOffset, 9);
                return false;
            default:
                throw new InvalidOperationException("Unknown admitted compact instruction.");
        }
        AdvanceInstruction();
        return true;
    }

    private bool DrawCards(int card, int count, int resumeIp)
    {
        if (Read(Frame + DrawIndexOffset) == 0) State.Write(Frame + FirstDrawnOffset, -1);
        while (Read(Frame + DrawIndexOffset) < count && Count(Pile.Hand) < 10 && !Ending)
        {
            if (Count(Pile.Draw) == 0 && Count(Pile.Discard) != 0)
            {
                State.Write(Frame + DrawResumeIpOffset, resumeIp);
                Shuffle(card);
                if (NeedsChoice) return false;
            }
            if (Count(Pile.Draw) == 0 || Count(Pile.Hand) >= 10) break;
            int drawn = CardAt(Pile.Draw, 0);
            Move(drawn, Pile.Hand);
            Emit(EventKind.Draw, drawn, card < 0 ? 1 : 0);
            if (Definition(drawn).DrawCost != null) Emit(EventKind.CostChanged, drawn, _drawCosts!.Draw(State, drawn));
            if (Read(Frame + DrawIndexOffset) == 0) State.Write(Frame + FirstDrawnOffset, drawn);
            State.Write(Frame + DrawIndexOffset, Read(Frame + DrawIndexOffset) + 1);
        }
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
        order.Sort(_instanceComparer);
        ValueRng rng = ShuffleRng;
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
        {
            State.Write(Frame + IpOffset, 6);
            CompletePlayerSideStart();
        }
        else if (!_shuffleBlockFirst) GainBlock(sourceCard, _shuffleBlock);
    }

    private void WriteRng(ValueRng rng)
    {
        State.Write(RngSlot, rng.Counter);
        State.Write(RngSlot + 1, unchecked((long)rng.State0));
        State.Write(RngSlot + 2, unchecked((long)rng.State1));
        State.Write(RngSlot + 3, unchecked((long)rng.State2));
        State.Write(RngSlot + 4, unchecked((long)rng.State3));
    }

    private void GainBlock(int card, decimal amount) => GainCreatureBlock(card, 0, amount);

    private void GainCreatureBlock(int source, int owner, decimal amount)
    {
        if (amount <= 0 || Ending) return;
        int previous = _combat?.Read(State, owner).Block ?? Block;
        if (_combat == null) State.Write(BlockSlot, (int)Math.Min(999_999_999m, Block + amount));
        else
        {
            CreatureVitals values = Creature(owner);
            values.GainBlock(amount);
            _combat.Write(State, owner, values);
        }
        Emit(EventKind.Block, source, (_combat?.Read(State, owner).Block ?? Block) - previous, target: owner == 0 ? -1 : owner);
    }

    private void SummonPet(int source, int amount)
    {
        if (Ending || amount <= 0) return;
        _combat!.SummonPet(State, amount);
        Emit(EventKind.SummonPet, source, amount, target: PetIndex);
    }

    private void AttackCreature(int source, int dealer, int target, decimal amount, BasicPowerKind? cardMultiplier = null)
    {
        if (Ending || !CreaturePresent(target) || Creature(target).CurrentHp <= 0 || Creature(dealer).CurrentHp <= 0) return;
        bool redirect = target == 0 && PetIndex >= 0 && Creature(PetIndex).CurrentHp > 0;
        // Every admitted card attack is inside its first OnPlay, in the Play pile.
        // The source belongs to the player even when its actual dealer is the pet.
        DamageValues result = _combat!.Damage(State, target, _powers?.ModifyAttack(State, dealer, target, amount, cardMultiplier,
                firstCardAttack: source >= 0 && AttackCardStarts <= 1) ?? amount,
            out DamageValues? petResult, redirectToPet: redirect);
        DamageTraits traits = source < 0 ? DamageTraits.NoCard : 0;
        // Native records both receivers before processing either death. Even a fully
        // blocked hit produces the pet result and a separate zero-loss owner result.
        if (petResult is { } petDamage) EmitDamage(source, PetIndex, petDamage, traits, dealer);
        EmitDamage(source, target, result, traits, dealer);
        if (petResult is { Killed: true }) CompleteCreatureDeath(source, PetIndex);
        if (result.Killed) CompleteCreatureDeath(source, target);
        Emit(EventKind.AttackFinish, source, target: target, dealer: dealer);
    }

    private void TriggerPoison(int card, int target)
    {
        if (Ending || !CreaturePresent(target) || Creature(target).CurrentHp <= 0) return;
        int index = _powers!.Find(target, BasicPowerKind.Poison);
        int amount = _powers.Read(State, index).Amount;
        if (amount <= 0) return;
        // Accelerant and all unrepresented damage/death hooks are excluded by root admission.
        DamageValues result = _combat!.Damage(State, target, amount, unblockable: true);
        RecordDamage(card, target, result, DamageTraits.Unpowered | DamageTraits.Unblockable
            | DamageTraits.NoDealer | DamageTraits.NoCard | DamageTraits.Poison, dealer: -1);
        // Native Decrement bypasses application modifiers, including Artifact.
        if (Creature(target).CurrentHp > 0) CommitPower(card, target, BasicPowerKind.Poison, -1);
    }

    private void RecordDamage(int card, int target, DamageValues result, DamageTraits traits = 0, int dealer = 0)
    {
        EmitDamage(card, target, result, traits, dealer);
        if (result.Killed) CompleteCreatureDeath(card, target);
    }

    private void EmitDamage(int card, int target, DamageValues result, DamageTraits traits, int dealer)
    {
        Emit(EventKind.Damage, card, result.Unblocked, target: target, flags: result.Flags | (int)traits, dealer: dealer);
        Emit(EventKind.DamageBlocked, card, result.Blocked, target: target);
        Emit(EventKind.DamageOverkill, card, result.Overkill, target: target);
    }

    private void CompleteCreatureDeath(int source, int target)
    {
        _combat!.CompleteDeath(State, target);
        _powers?.RemoveOwner(State, target);
        if (target == 0) ClearPanache();
        Emit(EventKind.Death, source, target: target);
        if (target == 0 && PetIndex >= 0 && Creature(PetIndex).CurrentHp > 0)
            KillCreature(source, PetIndex);
    }

    private void KillCreature(int source, int target)
    {
        CreatureVitals values = Creature(target);
        int hp = values.CurrentHp;
        values.LoseHp(hp);
        _combat!.Write(State, target, values);
        Emit(EventKind.Kill, source, hp, target: target);
        CompleteCreatureDeath(source, target);
    }

    private void ApplyTemporaryStrengthLoss(int card, int target, int amount)
    {
        // Native modifiers run before BeforeApplied. An Artifact blocks the entire temporary
        // effect, including its nested first Strength command, and is consumed exactly once.
        if (!PreparePower(card, target, BasicPowerKind.PiercingWail, amount)) return;
        // The first native Strength command precedes creation of the temporary counter.
        if (_powers!.Amount(State, target, BasicPowerKind.PiercingWail) == 0)
            ApplyPower(card, target, BasicPowerKind.Strength, -amount);
        CommitPower(card, target, BasicPowerKind.PiercingWail, amount);
        // Native callback compares the requested offset with the resulting counter,
        // including stacks whose counter is already at its cap.
        if (amount != _powers.Amount(State, target, BasicPowerKind.PiercingWail))
            ApplyPower(card, target, BasicPowerKind.Strength, -amount);
    }

    private void ApplyPower(int card, int target, BasicPowerKind kind, int amount)
    {
        if (PreparePower(card, target, kind, amount)) CommitPower(card, target, kind, amount);
    }

    private void BeforeCardPlayed(int card)
    {
        int order = 0;
        while ((_powers?.NextBeforeCardPower(State, order) ?? -1) is var index && index >= 0)
        {
            var power = Power(index);
            order = power.Order;
            switch (PowerDefinition(index).Kind)
            {
                case BasicPowerKind.Veilpiercer:
                    if (IsEthereal(card)) CommitPower(card, 0, BasicPowerKind.Veilpiercer, -1);
                    break;
                case BasicPowerKind.SpiritOfAsh:
                    if (IsEthereal(card)) GainBlock(card, power.Amount);
                    break;
                case BasicPowerKind.DanseMacabre:
                    int resolvedCost = Definition(card).CostsX ? CapturedX(card) : Math.Max(0, EnergyCost(card));
                    if (resolvedCost >= PowerDefinition(index).MinimumEnergyCost) GainBlock(card, power.Amount);
                    break;
                default: throw new InvalidOperationException("Unrepresented before-card Power.");
            }
        }
    }

    private bool PreparePower(int card, int target, BasicPowerKind kind, int amount)
    {
        if (amount == 0 || Ending || !CreaturePresent(target) || Creature(target).CurrentHp <= 0) return false;
        // All admitted debuffs are visible. Stat polarity depends on the requested amount,
        // while counter debuffs retain their type independently of the owner's current amount.
        bool debuff = BasicPowerLayout.IsDebuff(kind, amount);
        if (debuff && _powers!.HasArtifact)
        {
            int artifact = _powers.FindOrDefault(target, BasicPowerKind.Artifact);
            if (artifact >= 0 && _powers.Read(State, artifact) is { Amount: > 0 } current)
            {
                _powers.Apply(State, artifact, -1, current.Applier);
                Emit(EventKind.PowerChange, card, -1, target: target, flags: (int)BasicPowerKind.Artifact);
                return false;
            }
        }
        return true;
    }

    private void CommitPower(int card, int target, BasicPowerKind kind, int amount, int applier = 0)
    {
        int index = _powers!.Find(target, kind);
        int before = _powers.Read(State, index).Amount;
        _powers.Apply(State, index, amount, applier);
        int applied = _powers.Read(State, index).Amount - before;
        Emit(EventKind.PowerChange, card, applied, target: target, flags: (int)kind);
        if (kind == BasicPowerKind.Doom && applied > 0 && applier >= 0)
            Emit(EventKind.DoomApplied, applier, applied, target: target);
    }

    private sealed class InstanceComparer(ResumableDiscardProgram owner, CardComparer definitions) : IComparer<int>
    {
        public int Compare(int left, int right) => definitions.Compare(owner.DefinitionIndex(left), owner.DefinitionIndex(right));
    }

    private sealed class CardComparer(int[] comparisons, int count) : IComparer<int>
    {
        public int Compare(int left, int right) => comparisons[left * count + right];
    }

    private void Push(int card, bool automatic, int target = -1, int? energyValue = null)
    {
        if (Read(DepthSlot) >= MaxFrames) throw new InvalidOperationException("Compact frame capacity exceeded.");
        State.Write(DepthSlot, Read(DepthSlot) + 1);
        for (int i = 0; i < FrameWidth; i++) State.Write(Frame + i, 0);
        State.Write(Frame + CardOffset, card);
        State.Write(Frame + AutoOffset, automatic ? 1 : 0);
        State.Write(Frame + BeforeBlockOffset, Block);
        State.Write(Frame + TargetOffset, target);
        State.Write(Frame + FirstDrawnOffset, -1);
        int value = energyValue ?? (Definition(card).CostsX ? Energy : EnergyCost(card));
        State.Write(Frame + EnergyValueOffset, value);
        if (Definition(card).CostsX) _cardInstances.Write(State, card, (CardInstance(card) with { CapturedX = value }).Data);
    }

    private void Move(int card, Pile destination)
    {
        for (int p = 0; p < PileCount; p++)
        {
            Pile source = (Pile)p;
            for (int i = 0; i < Count(source); i++)
            {
                if (CardAt(source, i) != card) continue;
                int count = Count(source);
                for (int j = i; j + 1 < count; j++)
                    _piles[p].Write(State, j, CardAt(source, j + 1));
                _piles[p].Truncate(State, count - 1);
                _piles[(int)destination].Append(State, [card]);
                return;
            }
        }
        throw new InvalidOperationException("Compact card has no owning pile.");
    }

    private void Emit(EventKind kind, int card, int value = 0, bool automatic = false, int target = -1, int flags = 0, int dealer = -1)
    {
        // Card and target identities retain all 32 bits so generated instances cannot
        // alias an earlier event after the first 256 cards.
        var item = new Event(kind, card, value, automatic, target, flags, dealer);
        _events.Append(State, [item.Data, item.Metadata]);
        EventsExecuted++;
    }

    private bool Contains(Pile pile, int card)
    {
        for (int i = 0; i < Count(pile); i++) if (CardAt(pile, i) == card) return true;
        return false;
    }
    private int Read(int slot) => checked((int)State[slot]);
}
