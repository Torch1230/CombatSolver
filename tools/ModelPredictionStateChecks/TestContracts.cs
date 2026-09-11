// Only the game identities and simulator shell are substituted. The registry, field writer,
// state store and fingerprint below test production source, without initializing Godot.
namespace MegaCrit.Sts2.Core.Models
{
    internal abstract class AbstractModel;
    internal class RelicModel : AbstractModel;
    internal class ModifierModel : AbstractModel;
}

namespace MegaCrit.Sts2.Core.Entities.Players
{
    internal sealed class Player(ulong netId)
    {
        public ulong NetId => netId;
        public List<Models.RelicModel> Relics { get; } = [];
    }
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
    }
}

namespace CombatSolver.Engine.Common
{
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
