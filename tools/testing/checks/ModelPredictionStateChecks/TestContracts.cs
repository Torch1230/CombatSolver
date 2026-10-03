// Only the game identities and simulator shell are substituted. The registry, field writer,
// state store and fingerprint below test production source, without initializing Godot.
namespace MegaCrit.Sts2.Core.Models
{
    internal abstract class AbstractModel;
    internal class RelicModel : AbstractModel;
    internal class ModifierModel : AbstractModel;
    internal class CardModel(string id = "SAME_CARD") : AbstractModel
    {
        public string Id = id;
        public int Upgrade;
    }
}

namespace MegaCrit.Sts2.Core.Entities.Players
{
    internal sealed class Player(ulong netId)
    {
        public ulong NetId => netId;
        public List<Models.RelicModel> Relics { get; } = [];
        public PlayerCombatState PlayerCombatState { get; } = new();
    }
    internal sealed class PlayerCombatState
    {
        public Pile Hand { get; } = new();
        public Pile DrawPile { get; } = new();
        public Pile DiscardPile { get; } = new();
        public Pile ExhaustPile { get; } = new();
        public Pile PlayPile { get; } = new();
    }
    internal sealed class Pile { public List<Models.CardModel> Cards { get; } = []; }
}

namespace MegaCrit.Sts2.Core.Combat
{
    internal interface ICombatState
    {
        IReadOnlyList<Entities.Players.Player> Players { get; }
        IReadOnlyList<Models.ModifierModel> Modifiers { get; }
    }
}

namespace CombatSolver
{
    internal sealed class SimulatedCombatState(
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.Players.Player> players,
        IReadOnlyList<MegaCrit.Sts2.Core.Models.ModifierModel> modifiers) : MegaCrit.Sts2.Core.Combat.ICombatState
    {
        public IReadOnlyList<MegaCrit.Sts2.Core.Entities.Players.Player> Players => players;
        public IReadOnlyList<MegaCrit.Sts2.Core.Models.ModifierModel> Modifiers => modifiers;
        public IReadOnlyList<MegaCrit.Sts2.Core.Models.RelicModel> RelicsOf(MegaCrit.Sts2.Core.Entities.Players.Player player)
            => player.Relics;
    }
}

namespace CombatSolver.Engine.InCombat.Simulation
{
    internal sealed class CombatPredictionSimulator(Common.PredictionStateStore? store = null)
    {
        public Common.PredictionStateStore StateStore { get; } = store ?? new();
        public CombatPredictionState State { get; } = new();
    }
    internal sealed class CombatPredictionState
    {
        public readonly Dictionary<MegaCrit.Sts2.Core.Entities.Players.Player, SimPlayerCombatState> Players = [];
        public SimPlayerCombatState GetPlayerCombatState(MegaCrit.Sts2.Core.Entities.Players.Player player) => Players[player];
    }
    internal sealed class SimPlayerCombatState
    {
        public List<Common.SimCardPile> AllPiles { get; } = [new(), new(), new(), new(), new()];
        public IEnumerable<Common.PredictedCard> AllCards => AllPiles.SelectMany(pile => pile.Cards);
    }
}

namespace CombatSolver.Engine.Common
{
    internal sealed class PredictedCard(MegaCrit.Sts2.Core.Models.CardModel original,
        MegaCrit.Sts2.Core.Models.CardModel? preview = null)
    {
        public MegaCrit.Sts2.Core.Models.CardModel Original => original;
        public MegaCrit.Sts2.Core.Models.CardModel Preview => preview ?? original;
        public bool References(object source) => ReferenceEquals(source, Original) || ReferenceEquals(source, Preview);
    }
    internal sealed class SimCardPile { public List<PredictedCard> Cards { get; } = []; }
    internal interface IPredictionStateForkable
    {
        object Fork(PredictionForkContext context);
    }

    internal interface IPredictionForkBoundary
    {
        void AssertForkable();
    }

    internal sealed class PredictionForkContext
    {
        private readonly Dictionary<object, object> _objects = new(ReferenceEqualityComparer.Instance);

        public void Register<T>(T source, T fork) where T : class
        {
            if (ReferenceEquals(source, fork))
                return;
            if (_objects.TryGetValue(source, out object? existing) && !ReferenceEquals(existing, fork))
                throw new InvalidOperationException("Object was forked twice.");
            _objects[source] = fork;
        }

        public bool TryRemap<T>(T source, out T? fork) where T : class
        {
            bool found = _objects.TryGetValue(source, out object? value);
            fork = found ? (T)value! : null;
            return found;
        }

        public T RemapOrSelf<T>(T source) where T : class
            => TryRemap(source, out T? fork) ? fork! : source;

        public T RequireRemap<T>(T source) where T : class
            => TryRemap(source, out T? fork) ? fork! : throw new InvalidOperationException("Required mapping is absent.");
    }
}
