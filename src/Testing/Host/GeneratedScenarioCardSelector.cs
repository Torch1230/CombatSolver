using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace CombatSolver;

// Owned only by ScenarioBuilder's setup scope. Never installed during solver deployment.
internal sealed class GeneratedScenarioCardSelector(string[][]? requested) : ICardSelector
{
    internal List<string[]> Choices { get; } = [];

    private List<CardModel> Select(IEnumerable<CardModel> options, int minimum, int maximum)
    {
        List<CardModel> remaining = options.ToList();
        if (minimum < 0 || maximum < minimum || minimum > remaining.Count)
            throw new InvalidDataException("生成场景的原生选牌边界无效。");
        List<CardModel> selected = [];
        if (requested == null)
            selected.AddRange(remaining.Take(minimum));
        else
        {
            if (Choices.Count >= requested.Length || requested[Choices.Count] == null)
                throw new InvalidDataException("生成场景遇到配置之外的额外开局/获取选牌。");
            foreach (string id in requested[Choices.Count])
            {
                CardModel card = remaining.FirstOrDefault(c => string.Equals(c.Id.Entry, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"开局选牌候选中找不到{id}。");
                selected.Add(card);
                remaining.Remove(card);
            }
        }
        if (selected.Count < minimum || selected.Count > maximum)
            throw new InvalidDataException("指定开局选牌数量不满足原生限制。");
        Choices.Add(selected.Select(c => c.Id.Entry).ToArray());
        return selected;
    }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        => Task.FromResult<IEnumerable<CardModel>>(Select(options, minSelect, maxSelect));

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        // Card rewards can be skipped. Explicit [] records that choice; automatic setup takes the first card.
        List<CardModel> selected = Select(options.Select(x => x.Card), requested == null && options.Count > 0 ? 1 : 0, 1);
        return selected.Count == 0 ? default : new CardRewardSelection { card = selected[0] };
    }

    internal void AssertConsumed()
    {
        if (requested != null && requested.Length != Choices.Count)
            throw new InvalidDataException("生成场景没有消费完显式开局/获取选牌。");
    }
}
