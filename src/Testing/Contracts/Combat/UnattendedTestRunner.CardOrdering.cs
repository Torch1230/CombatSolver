using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertNativeCardOrderingAsync(CombatState combat, Player player)
    {
        foreach (string id in new[] { "ABUNDANCE", "DEFEND_IRONCLAD", "FINESSE", "OFFERING" })
            await AssertNativeCardOrderingCaseAsync(combat, player, id);
    }

    private async Task AssertNativeCardOrderingCaseAsync(CombatState combat, Player player, string cardId)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await InjectCardAsync(combat, player, new() { CardId = cardId, Pile = "Hand", Count = 8 });
        var cards = player.PlayerCombatState!.Hand.Cards.ToArray();
        for (int i = 0; i < cards.Length; i++)
        {
            cards[i].EnergyCost.SetThisCombat(3 - i % 3);
            cards[i].BaseReplayCount = i % 2;
            foreach (var variable in cards[i].DynamicVars)
                variable.Value.BaseValue += i % 4;
        }
        var parent = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var rngBefore = parent.Rng.ShuffleState;
        var expectedRng = parent.Rng.Shuffle.Clone();
        var expected = cards.ToList().StableShuffle(expectedRng);
        await InjectPowerAsync(combat, player, new() { PowerId = "CORRUPTION_POWER", Target = "Player", Amount = 1 });
        foreach (var branch in new[] { child, sibling })
        {
            var actual = branch.State.GetPlayerCombatState(player).Hand.Cards.ToList();
            actual.StableShuffle(branch.Rng.Shuffle, branch);
            if (!actual.Select((card, index) => card.References(expected[index])).All(equal => equal)
                || branch.Rng.ShuffleState != expectedRng.CaptureState())
                throw new InvalidOperationException("Branch shuffle order or RNG differs from frozen native ordering.");
        }
        if (parent.Rng.ShuffleState != rngBefore)
            throw new InvalidOperationException("Child shuffle mutated parent RNG.");
        var original = parent.State.FindCard(cards[0])!;
        var modified = child.State.FindCard(cards[0])!;
        modified.MutablePreview.EnergyCost.SetThisCombat(0);
        if (original.Preview.EnergyCost.GetWithModifiers(MegaCrit.Sts2.Core.Entities.Cards.CostModifiers.Local) != 3)
            throw new InvalidOperationException("Card sorting fixture mutated its parent card.");
        var modifiedCards = child.State.GetPlayerCombatState(player).Hand.Cards.ToArray();
        foreach (var variable in modified.MutablePreview.DynamicVars)
            variable.Value.BaseValue += 5;
        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        var repeatedActual = modifiedCards.ToList();
        repeatedActual.Add(modifiedCards[0]);
        var expectedWithDuplicate = modifiedCards.Select(card => card.Preview).ToList();
        expectedWithDuplicate.Add(modifiedCards[0].Preview);
        var duplicateExpectedRng = child.Rng.Shuffle.Clone();
        expectedWithDuplicate.StableShuffle(duplicateExpectedRng);
        repeatedActual.StableShuffle(child.Rng.Shuffle, child);
        if (!repeatedActual.Select((card, index) => ReferenceEquals(card.Preview, expectedWithDuplicate[index])).All(equal => equal)
            || child.Rng.ShuffleState != duplicateExpectedRng.CaptureState()
            || parent.Rng.ShuffleState != rngBefore)
            throw new InvalidOperationException("A later shuffle reused stale values or changed duplicate identity/RNG.");
        _completedChecks.Add($"CardOrdering:{cardId}:vars={cards[0].DynamicVars.Count}:Native:CostReplayDynamicValues:FullRng:LiveAdvance:ParentSiblingForks:RepeatedSort:DuplicateReference");
    }
}
