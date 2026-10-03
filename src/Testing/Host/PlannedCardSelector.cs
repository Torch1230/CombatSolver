using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.TestSupport;

namespace CombatSolver;

// Headless differentials have no native selection surface. Production deployment uses NativeChoiceRuntime.
internal sealed class PlannedCardSelector : ICardSelector
{
    private static long _nextTransactionId;
    private readonly IReadOnlyList<PlanCardChoice> _choices;
    private readonly long _transactionId = Interlocked.Increment(ref _nextTransactionId);
    private Dictionary<(string Id, int Upgrade), int>? _beforeCardCounts;
    private int _index;

    public bool IsConsumed => _index == _choices.Count;

    public PlannedCardSelector(PlanCardChoice choice)
        : this([choice])
    {
    }

    public PlannedCardSelector(IReadOnlyList<PlanCardChoice> choices)
    {
        _choices = choices;
    }

    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options,
        int minSelect,
        int maxSelect)
    {
        List<CardModel> available = options.ToList();
        if (_index >= _choices.Count)
            throw new InvalidOperationException("游戏请求了计划外的额外选牌。");
        PlanCardChoice choice = _choices[_index++];
        List<CardModel> selected = [];
        foreach (PlanCardToken token in choice.Cards)
        {
            CardModel card = available.Where(item => CardChoiceSupport.MatchesToken(item, token))
                .Skip(token.OptionOccurrence)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"自动选牌时找不到 {token.CardId}+{token.UpgradeLevel}#{token.OptionOccurrence}。");
            selected.Add(card);
        }

        if (selected.Count < minSelect || selected.Count > maxSelect)
            throw new InvalidOperationException(
                $"计划选择 {selected.Count} 张牌，但界面要求 {minSelect}..{maxSelect} 张。");
        Entry.Logger.Info(
            $"[CombatSolver/Test] DEPLOY_CHOICE transaction={_transactionId} " +
            $"index={_index - 1}/{_choices.Count} source={choice.SourceId} effect={choice.Effect} " +
            $"cards={string.Join(',', selected.Select(card => card.Id.Entry))}");
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    public void CaptureBefore(Player player)
    {
        _beforeCardCounts = AllCombatCards(player)
            .GroupBy(card => (card.Id.Entry, card.CurrentUpgradeLevel))
            .ToDictionary(group => group.Key, group => group.Count());
    }

    public void AssertConsumed()
    {
        if (_index != _choices.Count)
        {
            throw new InvalidOperationException(
                $"选择事务 {_transactionId} 仍有 {_choices.Count - _index} 个计划选牌没有被游戏请求。");
        }
    }

    public void ReconcileImplicitChoices(Player player)
    {
        while (_index < _choices.Count && WasImplicitChoiceApplied(_choices[_index], player))
        {
            PlanCardChoice choice = _choices[_index++];
            Entry.Logger.Info(
                $"[CombatSolver/Test] DEPLOY_CHOICE_IMPLICIT transaction={_transactionId} " +
                $"index={_index - 1}/{_choices.Count} source={choice.SourceId} " +
                $"effect={choice.Effect} cards={string.Join(',', choice.Cards.Select(card => card.CardId))}");
        }
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        if (_index >= _choices.Count)
            throw new InvalidOperationException("游戏请求了计划外的卡牌奖励选择。");

        PlanCardChoice choice = _choices[_index++];
        if (choice.Cards.Count == 0)
            return default;
        PlanCardToken token = choice.Cards[0];
        CardModel selected = options.Select(option => option.Card)
            .Where(card => CardChoiceSupport.MatchesToken(card, token))
            .Skip(token.OptionOccurrence)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"自动卡牌奖励选择找不到 {token.CardId}+{token.UpgradeLevel}#{token.OptionOccurrence}。");
        Entry.Logger.Info(
            $"[CombatSolver/Test] DEPLOY_CARD_REWARD_CHOICE source={choice.SourceId} card={selected.Id.Entry}");
        return new CardRewardSelection { card = selected };
    }

    private bool WasImplicitChoiceApplied(PlanCardChoice choice, Player player)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("核销隐式选牌时玩家没有战斗牌堆。");
        IReadOnlyList<CardModel> allCards = AllCombatCards(player);
        return choice.Effect switch
        {
            PlanChoiceEffect.MoveToHand => ContainsTokens(state.Hand.Cards, choice.Cards),
            PlanChoiceEffect.MoveToDrawTop => ContainsTokens(
                state.DrawPile.Cards.Take(choice.Cards.Count).ToArray(),
                choice.Cards),
            PlanChoiceEffect.Discard => ContainsTokens(state.DiscardPile.Cards, choice.Cards),
            PlanChoiceEffect.Exhaust => ContainsTokens(state.ExhaustPile.Cards, choice.Cards),
            PlanChoiceEffect.ApplySly => TokensMatchCards(
                choice.Cards,
                allCards.Where(card => card.IsSlyThisTurn)),
            PlanChoiceEffect.ApplyEthereal => TokensMatchCards(
                choice.Cards,
                allCards.Where(card => card.GetKeywordsWithSources(KeywordSources.Local)
                    .Contains(CardKeyword.Ethereal))),
            PlanChoiceEffect.ApplyRetain => TokensMatchCards(
                choice.Cards,
                allCards.Where(card => card.Keywords.Contains(CardKeyword.Retain))),
            PlanChoiceEffect.Upgrade => choice.Cards.All(token => allCards.Any(card =>
                card.Id.Entry == token.CardId
                && card.CurrentUpgradeLevel > token.UpgradeLevel)),
            PlanChoiceEffect.Transform => WasImplicitTransformApplied(choice, player),
            _ => false,
        };
    }

    private bool WasImplicitTransformApplied(PlanCardChoice choice, Player player)
    {
        if (_beforeCardCounts == null || choice.Cards.Count == 0)
            return false;
        Dictionary<(string Id, int Upgrade), int> after = AllCombatCards(player)
            .GroupBy(card => (card.Id.Entry, card.CurrentUpgradeLevel))
            .ToDictionary(group => group.Key, group => group.Count());
        return choice.Cards
            .GroupBy(token => (token.CardId, token.UpgradeLevel))
            .All(group => _beforeCardCounts.GetValueOrDefault(group.Key)
                - after.GetValueOrDefault(group.Key) >= group.Count());
    }

    private static IReadOnlyList<CardModel> AllCombatCards(Player player)
    {
        PlayerCombatState state = player.PlayerCombatState
            ?? throw new InvalidOperationException("读取计划选牌前后状态时玩家没有战斗牌堆。");
        return state.Hand.Cards
            .Concat(state.DrawPile.Cards)
            .Concat(state.DiscardPile.Cards)
            .Concat(state.ExhaustPile.Cards)
            .Concat(state.PlayPile.Cards)
            .ToArray();
    }

    private static bool ContainsTokens(
        IReadOnlyList<CardModel> cards,
        IReadOnlyList<PlanCardToken> tokens)
        => TokensMatchCards(tokens, cards);

    private static bool TokensMatchCards(
        IReadOnlyList<PlanCardToken> tokens,
        IEnumerable<CardModel> cards)
    {
        Dictionary<(string Id, int Upgrade), int> available = cards
            .GroupBy(card => (card.Id.Entry, card.CurrentUpgradeLevel))
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (IGrouping<(string CardId, int UpgradeLevel), PlanCardToken> group in tokens.GroupBy(token =>
                     (token.CardId, token.UpgradeLevel)))
        {
            if (available.GetValueOrDefault(group.Key) < group.Count())
                return false;
        }
        return true;
    }
}
