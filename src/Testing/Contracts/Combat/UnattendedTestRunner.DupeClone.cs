using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertDupeCloneContractAsync(CombatState combat, Player player)
    {
        CardModel nativeSource = player.PlayerCombatState!.Hand.Cards.First().CreateClone();
        nativeSource.EnergyCost.SetThisCombat(2);
        CardModel nativeDupe = nativeSource.CreateDupe(player);
        nativeDupe.EnergyCost.SetThisCombat(0);
        await CardPileCmd.AddGeneratedCardToCombat(nativeDupe, PileType.Discard, player);
        var parent = CombatRootSnapshot.Capture(combat).ForkSimulator();
        PredictedCard dupe = parent.State.FindCard(nativeDupe)!;
        PredictedCard source = dupe.GameplayCloneSource;
        if (source.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) != 2)
            throw new InvalidOperationException("Root did not capture the off-pile dupe source.");

        var child = parent.Fork();
        PredictedCard childDupe = child.State.FindCard(nativeDupe)!;
        childDupe.GameplayCloneSource.MutablePreview.EnergyCost.SetThisCombat(3);
        nativeSource.EnergyCost.SetThisCombat(3);
        PredictedCard clone = childDupe.CreateClone();
        CardModel actual = nativeDupe.CreateClone();
        if (clone.Preview.IsDupe || CardChoiceSupport.ChoiceCardKey(clone) != CardChoiceSupport.ChoiceCardKey(actual))
            throw new InvalidOperationException("Clone from dupe differs from native source clone.");
        if (dupe.CreateClone().Preview.EnergyCost.GetWithModifiers(CostModifiers.Local) != 2)
            throw new InvalidOperationException("Dupe source mutation escaped its child branch.");
        if (CardChoiceSupport.ChoiceCardKey(dupe) == CardChoiceSupport.ChoiceCardKey(childDupe)
            || CombatBeamSolver.CaptureCardStateFingerprintForTesting(dupe)
                == CombatBeamSolver.CaptureCardStateFingerprintForTesting(childDupe))
            throw new InvalidOperationException("Dupe source state is absent from card equivalence.");
        using var context = new PredictionForkContext();
        PredictedCard firstDupe = dupe.Fork(context);
        PredictedCard thenSource = source.Fork(context);
        if (!ReferenceEquals(firstDupe.DupeSource, thenSource))
            throw new InvalidOperationException("Dupe source fork created two wrappers for one source.");
        _completedChecks.Add("DupeClone:Native:OffPileRoot:LiveAdvance:ForkRemap:ParentIsolation:StateKeys");
    }
}
