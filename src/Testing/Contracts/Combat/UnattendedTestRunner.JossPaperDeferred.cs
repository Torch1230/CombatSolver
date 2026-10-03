using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertJossPaperDeferredFork(CombatState combat, Player player)
    {
        JossPaper live = player.Relics.OfType<JossPaper>().Single();
        if (live.CardsExhausted != 1 || live._etherealCount != 2)
            throw new InvalidOperationException("Joss Paper fixture requires one counted and two deferred exhausts.");
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        CombatPredictionSimulator parent = root.ForkSimulator();
        SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
        JossPaper predicted = parentCombat.RelicsOf(player).OfType<JossPaper>().Single();
        MoveStateSnapshot before = CaptureSimulated(parent, parentCombat, player, combat.Enemies[0]);
        live._etherealCount = 7;
        try
        {
            if (RelicPredictionStateSupport.GetJossPaperEtherealCount(parent, predicted) != 2)
                throw new InvalidOperationException("Joss Paper prediction changed after its live root advanced.");
            CombatPredictionSimulator child = parent.Fork();
            CombatPredictionSimulator sibling = parent.Fork();
            SimulatedCombatState childCombat = (SimulatedCombatState)child.State.CombatState;
            SimulatedCombatState siblingCombat = (SimulatedCombatState)sibling.State.CombatState;
            JossPaper childRelic = childCombat.RelicsOf(player).OfType<JossPaper>().Single();
            JossPaper siblingRelic = siblingCombat.RelicsOf(player).OfType<JossPaper>().Single();
            RelicPredictionStateSupport.SetJossPaperEtherealCount(child, childRelic, 3);
            StateFingerprintBuilder parentKey = new();
            StateFingerprintBuilder childKey = new();
            parentCombat.AppendFingerprint(ref parentKey, parent);
            childCombat.AppendFingerprint(ref childKey, child);
            if (parentKey.Finish() == childKey.Finish())
                throw new InvalidOperationException("Deferred Joss Paper exhausts did not distinguish branch fingerprints.");
            AssertSnapshotEqual(before, CaptureSimulated(sibling, siblingCombat, player, combat.Enemies[0]),
                "JossPaper", "SiblingBeforeConsumption");
            if (!PlayerTurnEndLifecycle.RunPhaseTwo(child, childCombat, [player.Creature])
                || RelicPredictionStateSupport.GetJossPaperCardsExhausted(child, childRelic) != 4
                || RelicPredictionStateSupport.GetJossPaperEtherealCount(child, childRelic) != 0)
                throw new InvalidOperationException("Joss Paper did not consume its branch's deferred exhausts.");
            AssertSnapshotEqual(before, CaptureSimulated(parent, parentCombat, player, combat.Enemies[0]),
                "JossPaper", "ParentAfterChildConsumption");
            if (!PlayerTurnEndLifecycle.RunPhaseTwo(sibling, siblingCombat, [player.Creature])
                || RelicPredictionStateSupport.GetJossPaperCardsExhausted(sibling, siblingRelic) != 3
                || RelicPredictionStateSupport.GetJossPaperEtherealCount(sibling, siblingRelic) != 0
                || live._etherealCount != 7 || live.CardsExhausted != 1)
                throw new InvalidOperationException("Joss Paper consumption changed another branch or the live relic.");
            _completedChecks.Add("JossPaper:RootDeferredCount:LiveAdvance:Fingerprint:Continuation:SiblingFork:ConsumeOnce");
        }
        finally { live._etherealCount = 2; }
    }
}
