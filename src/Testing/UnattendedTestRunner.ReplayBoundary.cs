using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertReplayBoundaryContractAsync(Player player)
    {
        foreach (string recorded in new[] { "H=A;Y=2/1;R=9", "H=A;Y=2/1/3;R=9" })
            if (!ReplayContinuationMatches(recorded, "H=A;Y=2/1/3/2;R=9"))
                throw new InvalidOperationException("Legacy history schema was rejected.");
        foreach (string recorded in new[]
                 {
                     "H=A;Y=2/0;R=9", "H=A;Y=2/1/4;R=9",
                     "H=A;Y=2/1/3/1;R=9", "H=B;Y=2/1/3;R=9",
                 })
            if (ReplayContinuationMatches(recorded, "H=A;Y=2/1/3/2;R=9"))
                throw new InvalidOperationException("A recorded state mismatch was accepted.");

        using NativeReplayDriver driver = new(this, [], 0, player);
        InvalidDataException failure = new("replay_boundary_original_failure");
        driver.ObserveBoundary(() => throw failure);
        try
        {
            await driver.AdvanceAsync(Task.CompletedTask);
        }
        catch (InvalidDataException error) when (ReferenceEquals(error, failure))
        {
            return;
        }
        throw new InvalidOperationException("Replay lost the original boundary failure.");
    }
}
