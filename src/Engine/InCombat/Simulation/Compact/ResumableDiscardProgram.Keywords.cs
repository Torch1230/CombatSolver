namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal sealed partial class ResumableDiscardProgram
{
    private CardInstanceValue CardInstance(int card) => CardInstanceValue.Decode(_cardInstances.Read(State, card));
    internal bool KeywordsCanChange => _definitions.Any(card => card.Effects.ChangesKeywords);
    internal bool IsEthereal(int card) => Definition(card).Ethereal || (CardInstance(card).AddedKeywords & CardKeywordFlags.Ethereal) != 0;
    internal bool IsRetained(int card) => Definition(card).Retain || (CardInstance(card).AddedKeywords & CardKeywordFlags.Retain) != 0;
    internal CardKeywordFlags ChoiceKeyword => NeedsChoice && Read(Frame + IpOffset) == 10
        ? CurrentInstruction.Keyword : CardKeywordFlags.None;

    internal bool IsChoiceOption(int card) => Contains(ChoicePile, card) && (ChoiceKeyword switch
    {
        CardKeywordFlags.None => true,
        CardKeywordFlags.Ethereal => !IsEthereal(card),
        CardKeywordFlags.Retain => !IsRetained(card),
        _ => throw new InvalidOperationException("Unknown keyword choice.")
    });

    internal int[] ChoiceOptions()
    {
        var cards = Cards(ChoicePile);
        return ChoiceKeyword == CardKeywordFlags.None ? cards : cards.Where(IsChoiceOption).ToArray();
    }

    private int KeywordChoiceCount()
    {
        int count = 0;
        for (int index = 0; index < Count(Pile.Hand); index++)
            if (IsChoiceOption(CardAt(Pile.Hand, index))) count++;
        return count;
    }

    private void AddKeyword(int card, CardKeywordFlags keyword)
    {
        var before = CardInstance(card);
        _cardInstances.Write(State, card, (before with { AddedKeywords = before.AddedKeywords | keyword }).Data);
        Emit(EventKind.KeywordAdded, card, flags: (int)keyword);
    }
}
