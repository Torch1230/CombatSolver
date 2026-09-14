// Minimal engine contracts for the linked production registry and phase facade.
// These do not replace native lifecycle, listener-filter or damage-command validation.
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace MegaCrit.Sts2.Core.Combat { enum CombatSide { Player, Enemy } }
namespace MegaCrit.Sts2.Core.Entities.Creatures
{
    sealed class Creature { public bool IsAlive = true; }
}
namespace MegaCrit.Sts2.Core.GameActions.Multiplayer { class PlayerChoiceContext; }
namespace MegaCrit.Sts2.Core.ValueProps { enum ValueProp { Unpowered } }
namespace MegaCrit.Sts2.Core.Models
{
    class AbstractModel
    {
        public virtual Task AfterSideTurnEndLate(
            GameActions.Multiplayer.PlayerChoiceContext choice, CombatSide side, IEnumerable<Creature> participants)
            => Task.CompletedTask;
    }
    class CardModel : AbstractModel { public object Owner = new(); }
    class RelicModel : AbstractModel;
    class ModifierModel : AbstractModel;
}
namespace MegaCrit.Sts2.Core.Models.Powers
{
    sealed class DisintegrationPower : AbstractModel
    {
        public required Creature Owner;
        public int Amount;
        public override Task AfterSideTurnEndLate(
            GameActions.Multiplayer.PlayerChoiceContext choice, CombatSide side, IEnumerable<Creature> participants)
            => throw new Exception("Native hook must never run in prediction.");
    }
}
namespace MegaCrit.Sts2.Core.Modding
{
    sealed class Manifest { public bool affectsGameplay = true; public string id = "test"; }
    sealed class Mod { public Manifest? manifest = new(); }
    static class AssemblyInfo
    {
        public static Mod? ModForType(Type type, out bool isBaseGame) { isBaseGame = false; return new(); }
    }
}
namespace CombatSolver
{
    static class Entry { public static Log Logger = new(); }
    sealed class Log { public void Info(string message) { } }
}
namespace CombatSolver.Engine.Common
{
    enum MirroredHookMask { AfterSideTurnEndLate }
    sealed class PredictedCard { public required CardModel Preview; }
    sealed class PredictionTrace
    {
        public struct TraceScope : IDisposable { public void Dispose() { } }
    }
}
namespace CombatSolver.Engine.InCombat.Simulation
{
    enum CombatDamageSourceKind { Power }
    sealed record CombatDamageSource(string Name)
    {
        public static CombatDamageSource For(CombatDamageSourceKind kind, string name) => new(name);
    }
    sealed class CombatPredictionSimulator
    {
        public bool HasPendingChoice;
        public bool IsOverOrEnding;
        public FakeState State = new();
        public List<AbstractModel> Listeners = [];
        public List<string> Events = [];
        public int Risks;
        public int DamageCalls;
        public int DamageTotal;
        public IDisposable PushDamageSource(CombatDamageSource source) => new PredictionTrace.TraceScope();
        public void Damage(Creature owner, int amount, MegaCrit.Sts2.Core.ValueProps.ValueProp props, Creature source)
        { DamageCalls++; DamageTotal += amount; }
    }
    sealed class FakeState
    {
        public FakePlayerState Player = new();
        public Creature GetCreature(Creature creature) => creature;
        public FakePlayerState GetPlayerCombatState(object owner) => Player;
    }
    sealed class FakePlayerState
    {
        public Dictionary<CardModel, PredictedCard> Cards = [];
        public PredictedCard? FindCard(CardModel card) => Cards.GetValueOrDefault(card);
    }
}
namespace CombatSolver.Engine.InCombat.Mirrors
{
    abstract class CombatMirrorContext : IMethodMirrorContext<AbstractModel>
    {
        public required CombatPredictionSimulator Simulator { get; init; }
        public FakeState State => Simulator.State;
        public PredictionTrace.TraceScope PushDispatchSource(AbstractModel model, MirrorMethodSpec spec) => new();
        public void RecordMethodNotMirroredRisk() => Simulator.Risks++;
        public void RecordMethodMirrorIncompleteRisk() => Simulator.Risks++;
    }
    static partial class HookMirrors
    {
        // The real helper also uses the root-frozen hook mask. This contract supplies membership only.
        private static IReadOnlyList<AbstractModel> IterateCombatHookListeners(
            CombatPredictionSimulator simulator, MirroredHookMask mask)
            => simulator.IsOverOrEnding ? [] : simulator.Listeners;
    }
}
