using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal interface IPowerCardValuationModel
{
    Type CardType { get; }
    PowerCardPool Pool { get; }

    /// <summary>该卡归属的机制族；<see cref="PowerCommitmentFamily.None" /> 表示不创建承诺。</summary>
    PowerCommitmentFamily CommitmentFamily { get; }

    /// <summary>该卡的路线准入政策；未登记承诺的卡可以保留默认值。</summary>
    PowerRouteAdmissionPolicy AdmissionPolicy { get; }

    PowerCardValuationRequirements Requirements { get; }

    PowerCardValuationResult Evaluate(
        CardModel card,
        in PowerCardValuationContext context);
}

internal abstract class PowerCardValuationModel<TCard> : IPowerCardValuationModel
    where TCard : CardModel
{
    public Type CardType => typeof(TCard);
    public abstract PowerCardPool Pool { get; }
    public virtual PowerCommitmentFamily CommitmentFamily => PowerCommitmentFamily.None;
    public virtual PowerRouteAdmissionPolicy AdmissionPolicy => default;
    public abstract PowerCardValuationRequirements Requirements { get; }

    PowerCardValuationResult IPowerCardValuationModel.Evaluate(
        CardModel card,
        in PowerCardValuationContext context)
    {
        if (card is not TCard typedCard)
        {
            throw new InvalidOperationException(
                $"能力牌估值模型 {GetType().Name} 不能处理 {card.GetType().FullName}。");
        }
        return Evaluate(typedCard, in context);
    }

    protected abstract PowerCardValuationResult Evaluate(
        TCard card,
        in PowerCardValuationContext context);
}
