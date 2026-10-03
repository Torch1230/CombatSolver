// These contracts isolate the production store from game initialization. The model is an
// identity only, and the fork context exercises the same explicit reference-remapping API.
// This harness does not replace the game's simulator/Fork boundary tests.
namespace MegaCrit.Sts2.Core.Models
{
    internal class AbstractModel(string name)
    {
        public string Name { get; } = name;
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
            if (_objects.TryGetValue(source, out object? existing))
            {
                if (!ReferenceEquals(existing, fork))
                    throw new InvalidOperationException("Object was forked twice.");
                return;
            }
            _objects.Add(source, fork);
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
            => TryRemap(source, out T? fork)
                ? fork!
                : throw new InvalidOperationException("Required mapping is absent.");

        public void Clear() => _objects.Clear();
    }
}
