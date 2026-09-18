using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed class PowerCardValuationRegistry
{
    private readonly Dictionary<Type, IPowerCardValuationModel> _models;
    private readonly Dictionary<string, IPowerCardValuationModel> _modelsByCardId;

    public PowerCardValuationRegistry(IEnumerable<IPowerCardValuationModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        _models = [];
        _modelsByCardId = new(StringComparer.Ordinal);
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
            string cardId = CardIdFor(model.CardType);
            if (!_modelsByCardId.TryAdd(cardId, model))
                throw new InvalidOperationException($"能力牌 ID 重复登记：{cardId}。");
        }
    }

    public int Count => _models.Count;

    public bool ContainsCardId(string cardId)
        => _modelsByCardId.ContainsKey(cardId);

    public bool TryGetPool(string cardId, out PowerCardPool pool)
    {
        if (_modelsByCardId.TryGetValue(cardId, out IPowerCardValuationModel? model))
        {
            pool = model.Pool;
            return true;
        }
        pool = default;
        return false;
    }

    public bool TryGetCommitmentDescriptor(
        string cardId,
        out PowerCommitmentDescriptor descriptor)
    {
        if (_modelsByCardId.TryGetValue(cardId, out IPowerCardValuationModel? model)
            && model.CommitmentFamily != PowerCommitmentFamily.None
            && !model.AdmissionPolicy.NoInCombatCommitment)
        {
            descriptor = new PowerCommitmentDescriptor(
                model.Pool,
                cardId,
                model.CommitmentFamily,
                model.AdmissionPolicy);
            return true;
        }
        descriptor = default;
        return false;
    }

    public static string CardIdFor(Type cardType)
    {
        ArgumentNullException.ThrowIfNull(cardType);
        string name = cardType.Name;
        System.Text.StringBuilder result = new(name.Length + 8);
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (index > 0 && char.IsUpper(current)
                && (char.IsLower(name[index - 1])
                    || index + 1 < name.Length && char.IsLower(name[index + 1])))
            {
                result.Append('_');
            }
            result.Append(char.ToUpperInvariant(current));
        }
        return result.ToString();
    }

    public PowerCardValuationRequirements RequirementsFor(Type cardType)
    {
        ArgumentNullException.ThrowIfNull(cardType);
        return _models.TryGetValue(cardType, out IPowerCardValuationModel? model)
            ? model.Requirements
            : PowerCardValuationRequirements.None;
    }

    /// <summary>
    /// 按精确卡牌类型求逐卡估值。当前只有合同检查与文档使用；生产搜索尚未构造
    /// <see cref="PowerCardValuationContext" />，因此返回值不影响准入、保路、专搜或终局排序。
    /// </summary>
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

    public IReadOnlyList<string> RegisteredCardIds(PowerCardPool pool)
        => _modelsByCardId
            .Where(pair => pair.Value.Pool == pool)
            .Select(pair => pair.Key)
            .OrderBy(cardId => cardId, StringComparer.Ordinal)
            .ToArray();
}
