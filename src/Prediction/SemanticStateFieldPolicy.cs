using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal enum SemanticStateFieldRole
{
    Behavior,
    Derived,
    PresentationOnly,
}

internal static class SemanticStateFieldPolicy
{
    private static readonly HashSet<(Type ModelType, string FieldName)> PresentationOnlyFields =
    [
        (typeof(KnockdownPower), "Applier"),
        (typeof(ImpaledPower), "Applier"),
        (typeof(GuardedPower), "Applier"),
        (typeof(InterceptPower), "Covering"),
        (typeof(VitalSparkPower), "AfflictionTitle"),
        (typeof(GalvanicPower), "AfflictionTitle"),
        (typeof(NightmarePower), "Card"),
        (typeof(FlankingPower), "Applier"),
        (typeof(ShrinkPower), "ApplierName"),
        (typeof(ImitationLearningPower), "TargetPlayer"),
        (typeof(CoveredPower), "Applier"),
        (typeof(BarricadePower), "ApplierName"),
    ];

    public static SemanticStateFieldRole Classify(
        AbstractModel model,
        string fieldName,
        DynamicVar value)
    {
        if (value is CalculatedVar)
            return SemanticStateFieldRole.Derived;
        if (value is not StringVar)
            return SemanticStateFieldRole.Behavior;
        return ClassifyString(model.GetType(), fieldName);
    }

    public static SemanticStateFieldRole ClassifyString(Type modelType, string fieldName)
    {
        if (PresentationOnlyFields.Contains((modelType, fieldName)))
            return SemanticStateFieldRole.PresentationOnly;

        throw new InvalidOperationException(
            $"未分类的字符串状态字段：{modelType.FullName}.{fieldName}。");
    }

    public static bool IsSemantic(AbstractModel model, string fieldName, DynamicVar value)
        => Classify(model, fieldName, value) == SemanticStateFieldRole.Behavior;
}
