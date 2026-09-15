using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertFrozenRootRunListeners(CombatState live, Player player)
    {
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        SimulatedCombatState parent = new(live);
        CombatPredictionSimulator parentSimulator = new(parent);
        parent.SetAmount<StrengthPower>(player.Creature, 2);
        _ = parent.DrainPowerAmountChanges();
        FieldInfo rootField = typeof(SimulatedCombatState).GetField("_rootRunHookListeners",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var frozen = (AbstractModel[])rootField.GetValue(parent)!;
        if (frozen.Length == 0 || frozen.Any(m => m is not CardModel and not EnchantmentModel))
            throw new InvalidOperationException("Frozen root listener fixture lacks the captured deck prefix.");
        var parentView = ((ICombatPredictionHookListenerSource)parent).RunHookListeners;
        AbstractModel[] originalOrder = parentView.ToArray();
        if (parentView is not ISegmentedModelList parentSegments
            || !ReferenceEquals(parentSegments.Prefix, frozen))
            throw new InvalidOperationException("Run listener cache did not use the exact frozen deck prefix.");

        CombatPredictionSimulator childSimulator = parentSimulator.Fork();
        SimulatedCombatState child = (SimulatedCombatState)childSimulator.State.CombatState;
        var childView = ((ICombatPredictionHookListenerSource)child).RunHookListeners;
        if (!ReferenceEquals(rootField.GetValue(child), frozen)
            || childView is not ISegmentedModelList childSegments
            || !ReferenceEquals(childSegments.Prefix, frozen))
            throw new InvalidOperationException("Child copied or replaced immutable root deck listeners.");
        StrengthPower parentPower = parent.GetPower<StrengthPower>(player.Creature)!;
        StrengthPower childPower = child.GetPower<StrengthPower>(player.Creature)!;
        for (int index = 0; index < parentView.Count; index++)
        {
            if (parentView[index] is PowerModel)
                continue;
            if (!ReferenceEquals(parentView[index], childView[index]))
                throw new InvalidOperationException("Fork changed a non-power listener's ordered identity.");
        }
        if (ReferenceEquals(parentPower, childPower) || !childView.Contains(childPower) || childView.Contains(parentPower))
            throw new InvalidOperationException("Frozen prefix reuse bypassed mutable Power remapping.");

        PredictedCard card = childSimulator.State.GetPlayerCombatState(player).AllCards.First();
        card.MutablePreview.BaseReplayCount++;
        child.SetAmount<StrengthPower>(player.Creature, 3);
        _ = child.DrainPowerAmountChanges();
        _ = ((ICombatPredictionHookListenerSource)child).RunHookListeners;
        CombatPredictionSimulator grandchildSimulator = childSimulator.Fork();
        SimulatedCombatState grandchild = (SimulatedCombatState)grandchildSimulator.State.CombatState;
        var grandchildView = ((ICombatPredictionHookListenerSource)grandchild).RunHookListeners;
        if (grandchildView is not ISegmentedModelList grandchildSegments
            || !ReferenceEquals(grandchildSegments.Prefix, frozen)
            || !parentView.SequenceEqual(originalOrder, ReferenceEqualityComparer.Instance)
            || parentPower.Amount != 2
            || !grandchildView.Contains(grandchild.GetPower<StrengthPower>(player.Creature)!)
            || grandchildView.Contains(childPower)
            || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
            throw new InvalidOperationException("Card/Power mutation or a second Fork escaped the branch boundary.");
    }
}
