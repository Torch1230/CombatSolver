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

    public bool TryGetCommitmentFamily(
        string cardId,
        out PowerCommitmentFamily family)
    {
        if (_modelsByCardId.TryGetValue(cardId, out IPowerCardValuationModel? model)
            && model.Pool == PowerCardPool.Silent)
        {
            family = SilentFamily(cardId);
            return family != PowerCommitmentFamily.None;
        }
        family = PowerCommitmentFamily.None;
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

    private static PowerCommitmentFamily SilentFamily(string cardId)
        => cardId switch
        {
            "ABRASIVE" or "AFTERIMAGE" or "FOOTWORK" or "WRAITH_FORM"
                => PowerCommitmentFamily.DefenseEfficiency,
            "ACCURACY" or "FAN_OF_KNIVES" or "INFINITE_BLADES" or "PHANTOM_BLADES"
                => PowerCommitmentFamily.ShivEngine,
            "ACCELERANT" or "ENVENOM" or "NOXIOUS_FUMES"
                => PowerCommitmentFamily.PoisonEngine,
            "MASTER_PLANNER" or "SPEEDSTER" or "TOOLS_OF_THE_TRADE" or "WELL_LAID_PLANS"
                => PowerCommitmentFamily.HandEngine,
            "SERPENT_FORM" or "TRACKING"
                => PowerCommitmentFamily.DamageEngine,
            _ => PowerCommitmentFamily.None,
        };
}
