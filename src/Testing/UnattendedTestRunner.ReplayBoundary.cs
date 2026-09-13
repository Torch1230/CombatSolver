using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertReplayBoundaryContractAsync(Player player)
    {
        HashSet<uint> completed = [6];
        if (!IsRecordedActionWindow(6, 6, true, completed)
            || !IsRecordedActionWindow(null, 6, false, completed)
            || IsRecordedActionWindow(7, 6, true, completed)
            || IsRecordedActionWindow(null, 6, true, completed)
            || IsRecordedActionWindow(null, 7, false, completed))
            throw new InvalidOperationException("Replay must submit a queued input in its recorded parent window or after that parent completed with an idle executor.");
        foreach (string recorded in new[] { "H=A;Y=2/1;R=9", "H=A;Y=2/1/3;R=9" })
            if (!ReplayContinuationMatches(recorded, "H=A;Y=2/1/3/2;R=9"))
                throw new InvalidOperationException("Legacy history schema was rejected.");
        string[] legacyStarts = ["H=A;Y=0/0/0;R=9", "H=A;Y=0/0/0/0;R=9"];
        const string currentStart = "H=A;Y=0/0/0/0;FlameHp=0;R=9";
        foreach (string legacyStart in legacyStarts)
            if (!ReplayContinuationMatches(legacyStart, currentStart, allowLegacyZeroCounter: true)
                || ReplayContinuationMatches(legacyStart, currentStart))
                throw new InvalidOperationException("Zero legacy FlameHp is limited to an explicit native combat-start boundary.");
        foreach (string invalid in new[] {
            "H=A;Y=0/0/0/0;FlameHp=1;R=9", "H=A;FlameHp=0;Y=0/0/0/0;R=9",
            "H=A;Y=0/0/0/0;FlameHp=0;FlameHp=0;R=9", "H=B;Y=0/0/0/0;FlameHp=0;R=9",
            "H=A;Y=0/1/0/0;FlameHp=0;R=9", "H=A;Y=0/0/0/0;FlameHp=0;R=10",
        })
            if (legacyStarts.Any(legacyStart =>
                    ReplayContinuationMatches(legacyStart, invalid, allowLegacyZeroCounter: true)))
                throw new InvalidOperationException("Legacy combat-start migration accepted a real state difference.");
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
