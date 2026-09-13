using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertImplicitChoiceOrderAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        SetEnergy(player, 3);
        foreach (string id in new[] { "HIDDEN_DAGGERS", "STRIKE_SILENT", "DEFEND_SILENT" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var names = SolverDisplayNames.Capture(combat);
        string[]? selectedIds = null;
        var cursor = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
        {
            var spec = TurnStartChoiceSupport.BuildSpec(simulator, player, request);
            var choices = CardChoiceSupport.BuildChoices(spec, names, 18, 24);
            var choice = choices.Single();
            var automatic = CardChoiceSupport.BuildAutomaticPolicyChoice(spec);
            if (!automatic.Cards.Select(c => c.CardId).SequenceEqual(choice.Cards.Select(c => c.CardId)))
                throw new InvalidOperationException("Nested automatic policy changed an implicit selection order.");
            selectedIds = choice.Cards.Select(c => c.CardId).ToArray();
            return choice;
        });
        var nativeCard = FindActualHandCard(player, "HIDDEN_DAGGERS", 0);
        shadow.BeginActionChoices(cursor);
        using (shadow.BeginCardExecutionScope(new HashSet<uint>()))
        {
            try { simulator.ManualPlay(simulator.State.FindCard(nativeCard)!, null, out _); }
            finally { shadow.EndActionChoices(); }
        }
        if (shadow.HasPendingChoice || selectedIds == null)
            throw new InvalidOperationException("Implicit hand choice was not resolved by the normal choice builder.");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
            () => { if (!nativeCard.TryManualPlay(null)) throw new InvalidOperationException("Native Hidden Daggers was refused."); }, deadline.Token);
        await action.CompletionTask.WaitAsync(deadline.Token);
        var enemy = combat.Enemies.Single();
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "ImplicitDiscardOrder");
        _completedChecks.Add("ImplicitAllSelection:NativeHandOrder:CompleteDiscardAndGeneratedPiles:Rng:" + string.Join(',', selectedIds));
    }
}
