using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace MegaCrit.Sts2.Core.Entities.Cards
{
    internal enum TargetType { AnyEnemy, AllEnemies, Self }
}
namespace MegaCrit.Sts2.Core.Models
{
    internal class PowerModel;
    internal sealed class Creature
    {
        public readonly HashSet<Type> LivePowers = [];
    }
    internal sealed class Player
    {
        public Creature Creature { get; } = new();
    }
    internal class CardModel
    {
        public Player Owner { get; } = new();
        public int NativeTargetReads { get; private set; }
        public bool RejectNativeRead { get; set; }
        public virtual TargetType TargetType => ReadNativeTarget(null);
        protected TargetType ReadNativeTarget(Type? power)
        {
            NativeTargetReads++;
            if (RejectNativeRead)
                throw new InvalidOperationException("Read live target type inside prediction");
            return power == null ? TargetType.Self
                : Owner.Creature.LivePowers.Contains(power) ? TargetType.AllEnemies : TargetType.AnyEnemy;
        }
    }
}
namespace MegaCrit.Sts2.Core.Models.Powers
{
    internal sealed class SeekingEdgePower : PowerModel;
    internal sealed class FanOfKnivesPower : PowerModel;
}
namespace MegaCrit.Sts2.Core.Models.Cards
{
    internal sealed class SovereignBlade : CardModel
    {
        public override TargetType TargetType => ReadNativeTarget(typeof(Powers.SeekingEdgePower));
    }
    internal sealed class Shiv : CardModel
    {
        public override TargetType TargetType => ReadNativeTarget(typeof(Powers.FanOfKnivesPower));
    }
}
namespace CombatSolver.Engine.Common
{
    internal sealed class PredictedCard(CardModel preview)
    {
        public CardModel Preview => preview;
    }
}
namespace CombatSolver
{
    // Deterministic branch-state double. The test exercises the production target selector,
    // not SimulatedCombatState's capture/Fork implementation or the native damage pipeline.
    internal sealed class SimulatedCombatState
    {
        public readonly Dictionary<(Creature, Type), int> Powers = [];
        public int GetAmount<T>(Creature creature) where T : PowerModel
            => Powers.GetValueOrDefault((creature, typeof(T)));
    }
}
namespace CombatSolver.Engine.InCombat.Simulation
{
    internal sealed class PredictionState(object combat)
    {
        public object CombatState => combat;
    }
    internal sealed partial class CombatPredictionSimulator(object combat)
    {
        public PredictionState State { get; } = new(combat);
    }
}
