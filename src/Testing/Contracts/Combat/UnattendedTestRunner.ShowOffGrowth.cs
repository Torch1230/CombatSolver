using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertShowOffGrowthAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        var enemy = combat.Enemies[0];
        if (combat.Enemies.Count != 2)
            throw new InvalidOperationException("Show Off contract requires two enemies.");
        await CreatureCmd.SetMaxHp(enemy, 15);
        await CreatureCmd.SetCurrentHp(enemy, 15);
        SetEnergy(player, 20);
        await InjectCardAsync(combat, player, new()
            { CardId = "SHOW_OFF", Pile = "Hand", Count = 2, TreatAsDeckCard = true });
        await InjectCardAsync(combat, player, new() { CardId = "FOOLISH_SHOT", Pile = "Hand" });
        var sources = player.PlayerCombatState!.Hand.Cards.OfType<ShowOff>().ToArray();
        GrowthValues budget = new(ShowOff: 4);
        if (GrowthOpportunityPolicy.Capture(combat).RequiredRewards.ShowOff != 2
            || SolverSettings.RoundTripForTesting(SolverSettings.Current with { GrowthBudgets = budget })
                .GrowthBudgets != budget)
            throw new InvalidOperationException("Show Off growth target or budget persistence changed.");
        using (SolverGrowthStrategyPanel panel = new())
            if (panel.FindChild(nameof(GrowthSource.ShowOff), recursive: true, owned: false) is not Godot.SpinBox)
                throw new InvalidOperationException("Show Off growth row is missing.");
        foreach (var card in sources) await PlayNativeCardAsync(card, null);
        if (GrowthOpportunityPolicy.Capture(combat).RequiredRewards.ShowOff != 2)
            throw new InvalidOperationException("Active Show Off powers lost their growth opportunities.");
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var initial = CaptureActual(combat, player, enemy);
        var shot = FindActualHandCard(player, "FOOLISH_SHOT", 0);
        var shadow = (SimulatedCombatState)child.State.CombatState;
        PlaySimulatedCard(child, shadow, child.State.FindCard(shot)!, enemy, combat.Enemies);
        if (shadow.GrowthRewards.ShowOff != 2 || budget.Credit(shadow.GrowthRewards) != 8
            || sources.Any(card => card.IncreasedAim != 0 || ((ShowOff)card.DeckVersion!).IncreasedAim != 0))
            throw new InvalidOperationException("Show Off branch growth changed live deck state or event credit.");
        _ = child.Fork();
        await PlayNativeCardAsync(shot, enemy);
        AssertSnapshotEqual(CaptureSimulated(child, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "NativePermanentGrowth");
        if (sources.Any(card => card.IncreasedAim != 2 || ((ShowOff)card.DeckVersion!).IncreasedAim != 2))
            throw new InvalidOperationException("Native Show Off did not grow each permanent source.");
        foreach (var branch in new[] { parent, sibling })
        {
            var state = (SimulatedCombatState)branch.State.CombatState;
            AssertSnapshotEqual(initial, CaptureSimulated(branch, state, player, enemy),
                _request.ScenarioId, "ShowOffForkIsolation");
            if (state.GrowthRewards.ShowOff != 0)
                throw new InvalidOperationException("Show Off growth leaked into a sibling.");
        }
        _completedChecks.Add("ShowOffGrowth:PermanentSources:PowerReferenceFork:LiveAdvance:Budgets:ActiveTargets:UI:NativeReplay");
    }
}
