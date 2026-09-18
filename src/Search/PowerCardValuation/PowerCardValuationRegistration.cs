using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal delegate PowerCardValuationResult PowerCardEvaluation(
    CardModel card,
    PowerCardValuationContext context);
/// <summary>
/// 由逐卡数据构造的通用估值模型。角色目录只提供卡牌类型、机制族、准入政策、需求与公式，
/// 不重复实现估值接口样板。
/// </summary>
internal sealed class DelegatingPowerCardValuationModel<TCard>(
    PowerCardPool pool,
    PowerCommitmentFamily commitmentFamily,
    PowerRouteAdmissionPolicy admissionPolicy,
    PowerCardValuationRequirements requirements,
    PowerCardEvaluation evaluation) : PowerCardValuationModel<TCard>
    where TCard : CardModel
{
    public override PowerCardPool Pool => pool;
    public override PowerCommitmentFamily CommitmentFamily => commitmentFamily;
    public override PowerRouteAdmissionPolicy AdmissionPolicy => admissionPolicy;
    public override PowerCardValuationRequirements Requirements => requirements;

    protected override PowerCardValuationResult Evaluate(
        TCard card,
        in PowerCardValuationContext context)
        => evaluation(card, context);
}

internal static class PowerCardModelRegistration
{
    internal static IPowerCardValuationModel Register<TCard>(
        PowerCardPool pool,
        Func<string, PowerCommitmentFamily> familyFor,
        Func<string, PowerRouteAdmissionPolicy> admissionFor,
        PowerCardValuationRequirements requirements,
        PowerCardEvaluation evaluation)
        where TCard : CardModel
    {
        ArgumentNullException.ThrowIfNull(familyFor);
        ArgumentNullException.ThrowIfNull(admissionFor);
        ArgumentNullException.ThrowIfNull(evaluation);
        string cardId = PowerCardValuationRegistry.CardIdFor(typeof(TCard));
        return new DelegatingPowerCardValuationModel<TCard>(
            pool,
            familyFor(cardId),
            admissionFor(cardId),
            requirements,
            evaluation);
    }
}
