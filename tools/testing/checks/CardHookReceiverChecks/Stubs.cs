namespace MegaCrit.Sts2.Core.Entities.Players { internal sealed class Player; }
namespace MegaCrit.Sts2.Core.Entities.Cards
{
    internal enum PileType { Hand, Discard }
    internal sealed class CardPile
    {
        public PileType Type { get; init; }
        public List<MegaCrit.Sts2.Core.Models.CardModel> Cards { get; } = [];
    }
}
namespace MegaCrit.Sts2.Core.Models
{
    internal class AbstractModel;
    internal sealed class CardModel : AbstractModel, IComparable<CardModel>
    {
        public bool Bound;
        public int CardsInHand;
        public int CompareTo(CardModel? other) => 0;
        public CardModel Copy() => (CardModel)MemberwiseClone();
    }
}
namespace CombatSolver.Engine.Common
{
    using MegaCrit.Sts2.Core.Models;
    using MegaCrit.Sts2.Core.Entities.Players;
    internal sealed class PredictionForkContext
    {
        public void Register(object source, object target) { }
    }
    internal static class PredictionUtils
    {
        public static CardModel CloneCardStateForSimulation(CardModel card) => card.Copy();
        public static CardModel CreateCard(CardModel card, Player player) => card.Copy();
    }
    internal static class PredictionModModelSupport
    {
        public static void RegisterBaseLibCardModifierOwner(CardModel card) { }
    }
}
