// Isolated allocation probe only. These types do not implement game or Fork semantics.
namespace MegaCrit.Sts2.Core.Models
{
    public class AbstractModel { public int Value; }
}
namespace CombatSolver.Engine.Common
{
    internal interface IPredictionStateForkable { object Fork(PredictionForkContext context); }
    internal interface IPredictionForkBoundary { void AssertForkable(); }
    internal sealed class PredictionForkContext
    {
        public T RemapOrSelf<T>(T model) => throw new NotSupportedException();
        public bool TryRemap(object value, out object? result) => throw new NotSupportedException();
        public void Register(object source, object result) => throw new NotSupportedException();
    }
}
