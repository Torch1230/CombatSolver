using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// 逐卡能力估值模型。当前状态：这是**合同与文档层**，不是生产搜索消费者。
/// 生产搜索使用 <see cref="PowerCommitmentDescriptor" />（登记/机制族/优先级/准入）、各卡池
/// 开局投影与触发证据，以及当前可打能力的固定前缀完整后验；本接口的 Reward/Penalty/Timing
/// 还没有接入候选准入、Beam 保路或终局排序。不要把它描述成“已经投入生产的量化”。
/// </summary>
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
