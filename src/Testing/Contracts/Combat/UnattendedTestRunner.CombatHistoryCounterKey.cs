using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertCombatHistoryCounterKey(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        if (!root.PlayerCardIds.Contains("GOLD_AXE"))
            throw new InvalidOperationException("The fixture needs Gold Axe in the captured root piles.");

        CombatPredictionSimulator parent = root.ForkSimulator();
        CombatPredictionSimulator child = parent.Fork();
        PredictedCard card = child.State.GetPlayerCombatState(player).Hand.Cards
            .Single(value => value.Preview.Id.Entry == "DEFEND_IRONCLAD");
        PredictedCard parentAxe = parent.State.GetPlayerCombatState(player).Hand.Cards
            .Single(value => value.Preview.Id.Entry == "GOLD_AXE");
        PredictedCard childAxe = child.State.GetPlayerCombatState(player).Hand.Cards
            .Single(value => value.Preview.Id.Entry == "GOLD_AXE");

        decimal before = GoldAxeValue(parent, parentAxe);
        using (child.PushActionSource(card.Original, PredictionActionKind.CardPlay))
        {
            var play = CreateHistoryProbePlay(card, player);
            child.History.CardPlayStarted(card, play);
            child.History.CardPlayFinished(card, play, wasEthereal: false);
        }
        if (GoldAxeValue(child, childAxe) != before + 1 || GoldAxeValue(parent, parentAxe) != before)
            throw new InvalidOperationException("Gold Axe did not observe one additional finished play on the child branch.");

        using (SimulationNotificationIsolation.Enter())
        {
            var evaluator = new SurgicalEvaluationDriver(root, SolverDisplayNames.Capture(combat),
                BattleDamageTracker.Observe(combat),
                SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
            StateFingerprint parentKey = ReleaseSurgicalSnapshot(evaluator.Evaluate(parent)).StateKey;
            StateFingerprint childKey = ReleaseSurgicalSnapshot(evaluator.Evaluate(child)).StateKey;
            StateFingerprint grandchildKey = ReleaseSurgicalSnapshot(evaluator.Evaluate(child.Fork())).StateKey;
            if (parentKey == childKey || childKey != grandchildKey
                || parentKey != ReleaseSurgicalSnapshot(evaluator.Evaluate(parent)).StateKey)
                throw new InvalidOperationException("The state key merged different Gold Axe values or changed across Fork.");
        }
        _completedChecks.Add("CombatHistoryCounterKey:GoldAxe:DifferentFinishedPlaysDistinctKeys:ForkStable:ParentUnchanged");
    }

    private static decimal GoldAxeValue(CombatPredictionSimulator simulator, PredictedCard card)
    {
        if (!CalculatedVarSpecRegistry.TryCalculate((CalculatedVar)card.Preview.DynamicVars.CalculatedDamage,
                simulator, card, null, out decimal value))
            throw new InvalidOperationException("Gold Axe calculated damage was not available.");
        return value;
    }
}
