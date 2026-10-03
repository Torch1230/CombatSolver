using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;

namespace MegaCrit.Sts2.Core.Combat { }
namespace MegaCrit.Sts2.Core.Entities.Players { }
namespace MegaCrit.Sts2.Core.Hooks { }
namespace MegaCrit.Sts2.Core.Models { }
namespace CombatSolver.Engine.InCombat.Mirrors { }
namespace MegaCrit.Sts2.Core.ValueProps { internal enum ValueProp { Unpowered } }
namespace MegaCrit.Sts2.Core.Entities.Cards { internal sealed class CardPlay; }
namespace MegaCrit.Sts2.Core.Entities.Creatures
{
    internal sealed class Creature
    {
        public bool LiveDead;
        public bool RejectLiveRead;
        public int LiveReads;
        public bool IsDead
        {
            get
            {
                LiveReads++;
                if (RejectLiveRead) throw new InvalidOperationException("Read live dealer health");
                return LiveDead;
            }
        }
    }
    internal record DamageResult(Creature Receiver);
}
namespace MegaCrit.Sts2.Core.Localization.DynamicVars
{
    internal sealed class DamageVar
    {
        public decimal BaseValue = 2;
        public ValueProp Props;
    }
}
namespace CombatSolver.Engine.Common
{
    internal sealed class PredictedCard;
    internal record CombatDamageSource;
    internal sealed class SimCreatureState { public bool IsDead; }
    internal sealed class PredictionState
    {
        public readonly Dictionary<Creature, SimCreatureState> Creatures = [];
        public SimCreatureState GetCreature(Creature creature) => Creatures[creature];
    }
}
namespace CombatSolver.Engine.InCombat.Simulation
{
    internal sealed partial class CombatPredictionSimulator
    {
        public readonly PredictionState State = new();
        public readonly List<Creature> Targets = [];
        public int ProcessCalls;
        private CombatDamageSource ResolveDamageSource(PredictedCard? card) => new();
        private bool TryDamageTarget(Creature target, decimal amount, ValueProp props,
            Creature? dealer, PredictedCard? card, CardPlay? play, CombatDamageSource source,
            out IReadOnlyList<DamageResult> results)
        {
            Targets.Add(target);
            results = [new(target)];
            return true;
        }
        private bool ProcessDamageResults(IEnumerable<DamageResult> results, Creature? dealer,
            PredictedCard? card, CombatDamageSource source)
        {
            ProcessCalls++;
            return true;
        }
    }
}
