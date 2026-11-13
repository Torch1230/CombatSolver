using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task<bool> PlayNativeCardAsync(CardModel card, Creature? target)
    {
        EnsureWithinDeadline();
        if (!card.CanPlayTargeting(target)) return false;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(
            _request.TimeoutSeconds - _stopwatch.Elapsed.TotalSeconds));
        GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction play && ReferenceEquals(play.NetCombatCard.ToCardModelOrNull(), card),
            () =>
            {
                if (!card.TryManualPlay(target))
                    throw new InvalidOperationException($"Native card eligibility changed while enqueuing {card.Id.Entry}.");
            }, deadline.Token);
        await action.CompletionTask.WaitAsync(deadline.Token);
        return true;
    }
}
