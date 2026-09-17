using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed class PowerCardValuationRegistry
{
    private readonly Dictionary<Type, IPowerCardValuationModel> _models;

    public PowerCardValuationRegistry(IEnumerable<IPowerCardValuationModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        _models = [];
        foreach (IPowerCardValuationModel model in models)
        {
            ArgumentNullException.ThrowIfNull(model);
            if (!typeof(CardModel).IsAssignableFrom(model.CardType)
                || model.CardType.IsAbstract)
            {
                throw new InvalidOperationException(
                    $"能力牌估值登记要求具体 CardModel 类型：{model.CardType.FullName}。");
            }
            if (!_models.TryAdd(model.CardType, model))
            {
                throw new InvalidOperationException(
                    $"能力牌估值重复登记：{model.CardType.FullName}。");
            }
        }
    }

    public int Count => _models.Count;

    public PowerCardValuationRequirements RequirementsFor(Type cardType)
    {
        ArgumentNullException.ThrowIfNull(cardType);
        return _models.TryGetValue(cardType, out IPowerCardValuationModel? model)
            ? model.Requirements
            : PowerCardValuationRequirements.None;
    }

    public bool TryEvaluate(
        CardModel card,
        in PowerCardValuationContext context,
        out PowerCardValuationResult result)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (_models.TryGetValue(card.GetType(), out IPowerCardValuationModel? model))
        {
            result = model.Evaluate(card, in context);
            return true;
        }
        result = default;
        return false;
    }

    public IReadOnlyList<Type> RegisteredCardTypes(PowerCardPool pool)
        => _models.Values
            .Where(model => model.Pool == pool)
            .Select(model => model.CardType)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
}
