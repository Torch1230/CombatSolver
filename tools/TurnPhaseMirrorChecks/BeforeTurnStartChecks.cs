using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;


static class BeforeTurnStartChecks
{
    public static void Run(string[] args)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        void Throws<T>(Action action, string message) where T : Exception
        {
            try { action(); } catch (T) { checks++; return; }
            throw new Exception(message);
        }
        bool Run(CombatPredictionSimulator simulator, CombatSide side = CombatSide.Player, params Creature[] participants)
            => HookMirrors.BeforeSideTurnStart(simulator, side, participants);

        if (args.Contains("--seal"))
        {
            BeforeSideTurnStartMirrors.Seal();
            Throws<InvalidOperationException>(() => BeforeSideTurnStartMirrors.Register<StartRelic>((_, _) => { }),
                "Root seal allowed registration before first dispatch.");
            Throws<InvalidOperationException>(() => BeforeSideTurnStartMirrors.Register(typeof(StartRelic), (_, _) => { }),
                "Root seal allowed runtime-type registration before first dispatch.");
            Console.WriteLine($"TURN_START_SEAL_OK checks={checks}");
            return;
        }

        Throws<ArgumentNullException>(() => BeforeSideTurnStartMirrors.Register<StartRelic>(null!), "Null handler accepted.");
        Throws<ArgumentException>(() => BeforeSideTurnStartMirrors.Register<AbstractStartRelic>((_, _) => { }), "Abstract receiver accepted.");
        Throws<InvalidOperationException>(() => BeforeSideTurnStartMirrors.Register<RelicModel>((_, _) => { }), "Non-override accepted.");
        BeforeSideTurnStartMirrors.Register<StartRelic>((relic, context) =>
        {
            context.Simulator.Events.Add(relic.Label);
            relic.Callback?.Invoke(context);
        });
        BeforeSideTurnStartMirrors.Register<StartModifier>((_, context) =>
            context.Simulator.Events.Add($"modifier:{context.Side}:{context.Participants.Count}"));
        BeforeSideTurnStartMirrors.Register<StartCard>((card, context) => context.Simulator.Events.Add(card.Label));
        BeforeSideTurnStartMirrors.Register<StartPower>((_, context) => context.Simulator.Events.Add("power"));
        Throws<ArgumentException>(() => BeforeSideTurnStartMirrors.Register<StartRelic>((_, _) => { }), "Duplicate accepted.");

        // Runtime-type entry point: same contract as the generic one, for adapters that only have a Type.
        Throws<ArgumentNullException>(() => BeforeSideTurnStartMirrors.Register((Type)null!, (_, _) => { }), "Null runtime type accepted.");
        Throws<ArgumentException>(() => BeforeSideTurnStartMirrors.Register(typeof(AbstractStartRelic), (_, _) => { }), "Abstract runtime type accepted.");
        Throws<InvalidOperationException>(() => BeforeSideTurnStartMirrors.Register(typeof(RelicModel), (_, _) => { }), "Non-override runtime type accepted.");
        BeforeSideTurnStartMirrors.Register(typeof(TypeStartRelic), (model, context) =>
            context.Simulator.Events.Add($"typed:{((TypeStartRelic)model).Label}"));
        Throws<ArgumentException>(() => BeforeSideTurnStartMirrors.Register(typeof(TypeStartRelic), (_, _) => { }), "Duplicate runtime type accepted.");

        var descriptor = ((IMethodMirrorRegistryDescriptorProvider)typeof(BeforeSideTurnStartMirrors)
            .GetField("Registry", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).DescribeMirrorSupport();
        Check(descriptor.BaseMethod.Name == nameof(AbstractModel.BeforeSideTurnStart), "Wrong method metadata.");
        Check(descriptor.Registrations.Count == 5 && descriptor.Registrations.All(r => r.Kind == MethodMirrorRegistrationKind.Handled),
            "Coverage descriptor lost a handler.");

        CombatPredictionSimulator simulator = new() { Listeners = [new StartRelic("first"), new StartModifier(), new StartRelic("last")] };
        Check(Run(simulator, CombatSide.Enemy), "Empty participants should still dispatch.");
        Check(simulator.Events.SequenceEqual(["first", "modifier:Enemy:0", "last"]), "Order or side changed.");
        simulator = new() { Listeners = [new StartPower(), new StartRelic("relic"), new StartModifier()] };
        Check(Run(simulator) && simulator.Events.SequenceEqual(["power", "relic", "modifier:Player:0"]),
            "Power, relic and modifier receivers were regrouped.");
        simulator = new() { Listeners = [new TypeStartRelic("typed")] };
        Check(Run(simulator) && simulator.Events.SequenceEqual(["typed:typed"]),
            "Runtime-type handler did not run in listener order.");
        Throws<InvalidOperationException>(() => BeforeSideTurnStartMirrors.Register<StartCard>((_, _) => { }), "Late registration accepted.");

        simulator = new() { Listeners = [new AbstractModel()] };
        Check(Run(simulator) && simulator.Risks == 0, "Base no-op recorded risk.");
        simulator = new() { Listeners = [new UnknownStartRelic(), new StartRelic("must not run")] };
        Throws<NotSupportedException>(() => Run(simulator), "Unknown override silently skipped.");
        Check(simulator.Risks == 1 && simulator.Events.Count == 0, "Unknown override continued dispatch.");
        simulator = new() { Listeners = [new DerivedStartRelic()] };
        Throws<NotSupportedException>(() => Run(simulator), "Registration incorrectly inherited by derived model.");

        simulator = new() { HasPendingChoice = true, Listeners = [new StartRelic("must not run")] };
        Check(!Run(simulator) && simulator.Events.Count == 0, "Pending entry executed a callback.");
        simulator = new() { Listeners = [new StartRelic("pause", c => c.Simulator.HasPendingChoice = true), new StartRelic("later")] };
        Check(!Run(simulator) && simulator.Events.SequenceEqual(["pause"]), "Pending choice did not suspend later listeners.");
        simulator = new() { Listeners = [new StartRelic("throw", _ => throw new ArithmeticException()), new StartRelic("later")] };
        Throws<ArithmeticException>(() => Run(simulator), "Handler failure was swallowed.");
        Check(simulator.Events.SequenceEqual(["throw"]), "Failure continued dispatch.");

        simulator = new() { Listeners = [new StartRelic("mutate", c => c.Simulator.Listeners.Clear()), new StartRelic("captured")] };
        Check(Run(simulator) && simulator.Events.SequenceEqual(["mutate", "captured"]), "Listener membership was not frozen.");
        simulator = new() { Listeners = [new StartRelic("terminal", c => c.Simulator.IsOverOrEnding = true), new StartRelic("captured")] };
        Check(Run(simulator) && simulator.Events.SequenceEqual(["terminal", "captured"]), "Inserted a mid-phase terminal exit.");
        simulator = new() { IsOverOrEnding = true, Listeners = [new StartRelic("must not run")] };
        Check(Run(simulator) && simulator.Events.Count == 0, "Phase entry ignored terminal state.");

        StartCard oldCard = new() { Label = "old" }, newCard = new() { Label = "new" };
        PredictedCard predicted = new() { Preview = oldCard };
        simulator = new() { Listeners = [new StartRelic("cow", _ => predicted.Preview = newCard), oldCard] };
        simulator.State.Player.Cards[oldCard] = predicted;
        Check(Run(simulator) && simulator.Events.SequenceEqual(["cow", "new"]), "Card COW passed a stale receiver.");

        Console.WriteLine($"TURN_START_MIRRORS_OK checks={checks}");
    }
}
class StartRelic(string label = "base", Action<BeforeSideTurnStartMirrorContext>? callback = null) : RelicModel
{
    public string Label = label;
    public Action<BeforeSideTurnStartMirrorContext>? Callback = callback;
    public override Task BeforeSideTurnStart(PlayerChoiceContext choice, CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState)
        => throw new Exception("Native hook invoked.");
}
class DerivedStartRelic : StartRelic;
abstract class AbstractStartRelic : StartRelic;
class UnknownStartRelic : StartRelic;
class StartModifier : ModifierModel
{
    public override Task BeforeSideTurnStart(PlayerChoiceContext choice, CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState)
        => throw new Exception("Native hook invoked.");
}
class StartCard : CardModel
{
    public string Label = "";
    public override Task BeforeSideTurnStart(PlayerChoiceContext choice, CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState)
        => throw new Exception("Native hook invoked.");
}
class StartPower : PowerModel
{
    public override Task BeforeSideTurnStart(PlayerChoiceContext choice, CombatSide side,
        IReadOnlyList<Creature> participants, ICombatState combatState)
        => throw new Exception("Native hook invoked.");
}
class TypeStartRelic(string label = "base") : RelicModel
{
    public string Label = label;
    public override Task BeforeSideTurnStart(PlayerChoiceContext choice, CombatSide side,
        IReadOnlyList<Creature> participants, ICombatState combatState)
        => throw new Exception("Native hook invoked.");
}
