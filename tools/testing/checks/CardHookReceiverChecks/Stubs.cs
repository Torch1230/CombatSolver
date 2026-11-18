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
        public bool IsDupe { get; init; }
        public CardModel? DupeOf { get; init; }
        public AbstractModel? Affliction { get; init; }
        public AbstractModel? Enchantment { get; init; }
        public int CompareTo(CardModel? other) => 0;
        public CardModel Copy() => (CardModel)MemberwiseClone();
    }
}
namespace CombatSolver.Engine.Common
{
    using MegaCrit.Sts2.Core.Models;
    using MegaCrit.Sts2.Core.Entities.Players;
    internal sealed class MirroredHookListenerFilter
    {
        // The independent receiver check uses the complete pile path.
        public bool CanProjectReceivers => false;

        public bool HasMirroredCallbacks(AbstractModel receiver)
            => throw new NotSupportedException("Receiver projection requires native hook metadata.");
    }

    internal sealed class PredictionForkContext
    {
        // Identity mapping only; native graph capture is outside this check's scope.
        private readonly Dictionary<object, object> _forks = new(ReferenceEqualityComparer.Instance);
        public void Register<T>(T source, T target) where T : class
        {
            if (ReferenceEquals(source, target)) return;
            if (_forks.TryGetValue(source, out object? existing) && !ReferenceEquals(existing, target))
                throw new InvalidOperationException("Object forked twice.");
            _forks[source] = target;
        }
        public bool TryRemap<T>(T source, out T? target) where T : class
        {
            target = _forks.TryGetValue(source, out object? value) ? (T)value : null;
            return target is not null;
        }
        public T RemapOrSelf<T>(T source) where T : class
            => TryRemap(source, out T? target) ? target! : source;
        public T RequireRemap<T>(T source) where T : class
            => TryRemap(source, out T? target) ? target! : throw new InvalidOperationException("Missing fork mapping.");
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
