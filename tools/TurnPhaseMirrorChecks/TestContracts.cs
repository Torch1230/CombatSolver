// Minimal engine contracts for the linked production registry and phase facade.
// These do not replace native lifecycle, listener-filter or damage-command validation.
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace MegaCrit.Sts2.Core.Combat { enum CombatSide { Player, Enemy } interface ICombatState; }
namespace MegaCrit.Sts2.Core.Entities.Creatures
{
    sealed class Creature { public bool IsAlive = true; }
}
namespace MegaCrit.Sts2.Core.GameActions.Multiplayer { class PlayerChoiceContext; }
namespace MegaCrit.Sts2.Core.Entities.Players { class Player; }
namespace MegaCrit.Sts2.Core.ValueProps { enum ValueProp { Unpowered } }
namespace MegaCrit.Sts2.Core.Models
{
    class AbstractModel
    {
        public virtual Task AfterPlayerTurnStartEarly(GameActions.Multiplayer.PlayerChoiceContext choice, Entities.Players.Player player) => Task.CompletedTask;
        public virtual Task AfterPlayerTurnStart(GameActions.Multiplayer.PlayerChoiceContext choice, Entities.Players.Player player) => Task.CompletedTask;
        public virtual Task AfterPlayerTurnStartLate(GameActions.Multiplayer.PlayerChoiceContext choice, Entities.Players.Player player) => Task.CompletedTask;
        public virtual Task BeforeSideTurnStart(
            GameActions.Multiplayer.PlayerChoiceContext choice, CombatSide side,
            IReadOnlyList<Creature> participants, ICombatState combatState) => Task.CompletedTask;
        public virtual Task AfterSideTurnEndLate(
            GameActions.Multiplayer.PlayerChoiceContext choice, CombatSide side, IEnumerable<Creature> participants)
            => Task.CompletedTask;
    }
    class CardModel : AbstractModel { public object Owner = new(); }
    class RelicModel : AbstractModel;
    class PowerModel : AbstractModel;
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
    [Flags] enum MirroredHookMask { AfterSideTurnEndLate=1, BeforeSideTurnStart=2,
        AfterPlayerTurnStartEarly=4, AfterPlayerTurnStart=8, AfterPlayerTurnStartLate=16 }
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
        public bool ContinuationRejected;
        public void RejectExecutionContinuation() => ContinuationRejected = true;
        public IDisposable PushDamageSource(CombatDamageSource source) => new PredictionTrace.TraceScope();
        public void Damage(Creature owner, int amount, MegaCrit.Sts2.Core.ValueProps.ValueProp props, Creature source)
        { DamageCalls++; DamageTotal += amount; }
    }
    sealed class FakeState
    {
        public ICombatState? CombatState { get; set; }
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

namespace CombatSolver
{
    sealed class TurnStartChoiceCursor;
    sealed class SimulatedCombatState : ICombatState
    {
        public bool TriggerAfterPlayerTurnStartVanilla(CombatPredictionSimulator simulator,
            MegaCrit.Sts2.Core.Entities.Players.Player player, TurnStartChoiceCursor choices)
        { simulator.Events.Add("vanilla"); return false; }
    }
    static class TurnStartRelicSupport
    {
        public static bool TriggerBeforeSideTurnStart(CombatPredictionSimulator s, SimulatedCombatState c, IReadOnlyList<Creature> p) => true;
    }
    static class TurnStartPowerSupport
    {
        public static bool TriggerBeforeSideTurnStart(CombatPredictionSimulator s, SimulatedCombatState c, IReadOnlyList<Creature> p) => false;
    }
}
namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart
{
    internal static partial class AfterPlayerTurnStartMirrors
    {
        private static partial void RegisterVanilla(MethodMirrorRegistry<AbstractModel, AfterPlayerTurnStartMirrorContext> registry, string hook)
        {
            if (hook == nameof(AbstractModel.AfterPlayerTurnStart))
                registry.Register<NativeNormalGenerator>((_, context) =>
                {
                    context.Simulator.Events.Add("native normal");
                    context.Simulator.Listeners.Add(new GeneratedLateListener());
                });
        }
    }
    class NativeNormalGenerator : RelicModel
    {
        public override Task AfterPlayerTurnStart(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext choice,
            MegaCrit.Sts2.Core.Entities.Players.Player player) => throw new Exception("Native hook invoked.");
    }
    class GeneratedLateListener : RelicModel
    {
        public override Task AfterPlayerTurnStartLate(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext choice,
            MegaCrit.Sts2.Core.Entities.Players.Player player) => throw new Exception("Native hook invoked.");
    }
    internal static partial class BeforeSideTurnStartMirrors
    {
        private static partial void RegisterVanilla(MethodMirrorRegistry<AbstractModel, BeforeSideTurnStartMirrorContext> registry) { }
    }
}
