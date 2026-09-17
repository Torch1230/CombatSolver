using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal interface IPowerCardValuationModel
{
    Type CardType { get; }
    PowerCardPool Pool { get; }
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
