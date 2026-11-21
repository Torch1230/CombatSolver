// Contract checks for the regular AfterSideTurnEnd registration entry.
// Mirrors the late-pass suite: argument validation, exact-type matching, freeze semantics and the
// explicit-failure rule the maintainer asked for. The dispatch order assertions run the same loop shape
// EndTurnPowerSupport.TriggerRegular uses, at the mirror boundary only.
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

internal static class SideTurnEndChecks
{
    internal static void Run(string[] args)
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

        // Returns true when the listener was accounted for here, exactly like the call site: registered and ignored
        // listeners are handled, a third-party override without a declaration throws, and everything else falls
        // through to the audited vanilla switch.
        bool DispatchOne(CombatPredictionSimulator simulator, AbstractModel listener)
        {
            if (!AfterSideTurnEndMirrors.IsThirdParty(listener))
            {
                simulator.Events.Add($"switch:{listener.GetType().Name}");
                return false;
            }

            if (AfterSideTurnEndMirrors.TryDispatch(simulator, listener, CombatSide.Player, []))
            {
                return true;
            }

            simulator.Events.Add($"switch:{listener.GetType().Name}");
            return false;
        }

        // --- argument validation, before anything is sealed ---
        Throws<ArgumentNullException>(
            () => AfterSideTurnEndMirrors.Register<TestSideEndPower>(null!), "Null handler accepted.");
        Throws<ArgumentException>(
            () => AfterSideTurnEndMirrors.Register<AbstractSideEndPower>((_, _) => { }), "Abstract receiver accepted.");
        Throws<InvalidOperationException>(
            () => AfterSideTurnEndMirrors.Register<RelicModel>((_, _) => { }), "Non-override accepted.");
        Throws<ArgumentNullException>(
            () => AfterSideTurnEndMirrors.Register(null!, (_, _) => { }), "Null runtime type accepted.");
        Throws<InvalidOperationException>(
            () => AfterSideTurnEndMirrors.Register(typeof(RelicModel), (_, _) => { }), "Runtime non-override accepted.");
        Throws<ArgumentException>(
            () => AfterSideTurnEndMirrors.RegisterIgnored(typeof(AbstractSideEndPower)), "Abstract ignore accepted.");

        // --- registrations: generic handler, runtime-addressed handler, ignored declaration ---
        AfterSideTurnEndMirrors.Register<TestSideEndPower>((power, context) =>
        {
            context.Simulator.Events.Add(power.Label);
            power.Callback?.Invoke(context);
        });
        Throws<ArgumentException>(
            () => AfterSideTurnEndMirrors.Register<TestSideEndPower>((_, _) => { }), "Duplicate accepted.");
        AfterSideTurnEndMirrors.Register(
            typeof(RuntimeSideEndPower), (model, context) => context.Simulator.Events.Add(((RuntimeSideEndPower)model).Label));
        AfterSideTurnEndMirrors.RegisterIgnored(typeof(PresentationOnlySideEndPower));
        Throws<ArgumentException>(
            () => AfterSideTurnEndMirrors.RegisterIgnored(typeof(UnrelatedSideEndReceiver)), "Unrelated runtime type accepted.");

        // --- dispatch order stays the native listener order, in place of the type switch ---
        CombatPredictionSimulator simulator = new()
        {
            Listeners =
            [
                new VanillaEndTurnPower(),
                new TestSideEndPower("first") { Callback = static context => context.Simulator.Events.Add("callback") },
                new PresentationOnlySideEndPower(),
                new TestSideEndPower("last"),
            ],
        };
        foreach (AbstractModel listener in simulator.Listeners)
        {
            DispatchOne(simulator, listener);
        }
        Check(
            simulator.Events.SequenceEqual(["switch:VanillaEndTurnPower", "first", "callback", "last"]),
            "Dispatch order, ignored handling or vanilla fallback changed.");

        // A type that inherits a registered override is not covered by that registration: it must fail loudly
        // rather than reuse the base handler.
        Check(AfterSideTurnEndMirrors.IsThirdParty(new DerivedSideEndPower()), "Derived type classified as base game.");

        // --- explicit failure for an unregistered third-party override, like the late pass ---
        simulator = new() { Listeners = [new UnknownSideEndPower(), new TestSideEndPower("must not run")] };
        Throws<NotSupportedException>(() => DispatchOne(simulator, simulator.Listeners[0]),
            "Unregistered third-party override was silently skipped.");
        Check(simulator.Events.Count == 0, "Unknown override continued dispatch.");
        Throws<NotSupportedException>(() => DispatchOne(new CombatPredictionSimulator(), new DerivedSideEndPower()),
            "Inherited registration was applied to a derived type.");

        // --- freeze: the first dispatch seals the table, and the root capture path seals it too ---
        Throws<InvalidOperationException>(
            () => AfterSideTurnEndMirrors.Register<LateSideEndPower>((_, _) => { }), "Registration accepted after dispatch.");
        Throws<InvalidOperationException>(
            () => AfterSideTurnEndMirrors.Register(typeof(LateSideEndPower), (_, _) => { }), "Runtime handler bypassed the seal.");
        Throws<InvalidOperationException>(
            () => AfterSideTurnEndMirrors.RegisterIgnored(typeof(LateSideEndPower)), "Runtime ignore bypassed the seal.");

        Console.WriteLine($"TURN_PHASE_SIDE_END_OK checks={checks}");
    }
}

abstract class AbstractSideEndPower : PowerModel
{
    public override Task AfterSideTurnEnd(
        PlayerChoiceContext choice, CombatSide side, IEnumerable<Creature> participants)
        => throw new Exception("Native hook invoked.");
}

class TestSideEndPower(string label = "base") : PowerModel
{
    public string Label = label;
    public Action<AfterSideTurnEndMirrorContext>? Callback;

    public override Task AfterSideTurnEnd(
        PlayerChoiceContext choice, CombatSide side, IEnumerable<Creature> participants)
        => throw new Exception("Native hook invoked.");
}

class RuntimeSideEndPower(string label = "runtime") : TestSideEndPower(label);
class PresentationOnlySideEndPower : TestSideEndPower;
class UnknownSideEndPower : TestSideEndPower;
class DerivedSideEndPower : TestSideEndPower;
class LateSideEndPower : TestSideEndPower;

class UnrelatedSideEndReceiver
{
    public Task AfterSideTurnEnd(PlayerChoiceContext choice, CombatSide side, IEnumerable<Creature> participants)
        => Task.CompletedTask;
}
