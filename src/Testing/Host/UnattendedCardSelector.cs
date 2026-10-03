using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace CombatSolver;

internal sealed class UnattendedCardSelector(
    IReadOnlyList<string> cardIds,
    IReadOnlyList<string>? expectedExcludedCardIds = null) : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options,
        int minSelect,
        int maxSelect)
    {
        List<CardModel> remaining = options.ToList();
        foreach (string excludedCardId in expectedExcludedCardIds ?? [])
        {
            if (remaining.Any(card => card.Id.Entry.Equals(excludedCardId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"原生测试选牌候选不应包含 {excludedCardId}。");
            }
        }
        List<CardModel> selected = [];
        if (cardIds.Count == 1 && cardIds[0] == "__FIRST__" && remaining.Count > 0)
            selected.Add(remaining[0]);
        else
        {
            foreach (string cardId in cardIds)
            {
                CardModel card = remaining.FirstOrDefault(candidate =>
                        candidate.Id.Entry.Equals(cardId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"原生测试选牌候选中找不到 {cardId}。");
                selected.Add(card);
                remaining.Remove(card);
            }
        }
        if (selected.Count < minSelect || selected.Count > maxSelect)
        {
            throw new InvalidOperationException(
                $"原生测试选择 {selected.Count} 张牌，但界面要求 {minSelect}..{maxSelect} 张。");
        }
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        if (cardIds.Count == 0)
            return default;
        string cardId = cardIds[0];
        CardModel selected = cardId == "__FIRST__"
            ? options.FirstOrDefault()?.Card
                ?? throw new InvalidOperationException("原生测试卡牌奖励候选为空。")
            : options.Select(option => option.Card)
                .FirstOrDefault(card => card.Id.Entry.Equals(cardId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"原生测试卡牌奖励候选中找不到 {cardId}。");
        return new CardRewardSelection { card = selected };
    }
}
