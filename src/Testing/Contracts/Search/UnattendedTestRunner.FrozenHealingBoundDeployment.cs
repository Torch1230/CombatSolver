using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Align the offline corpus fixture values, then require the entire native root
    // (including card state and RNG) to equal the supplied frozen continuation.
    private async Task PrepareFrozenHealingBoundDeploymentAsync(CombatState live, Player player)
    {
        if (_request.InitialPlayerHp is not { } hp || _request.InitialEnemyMoveIds.Length != 3
            || _request.EvidenceDirectory is not { } area)
            throw new InvalidOperationException("冻结治疗界限部署需要明确生命、三段行动及证据目录。");
        CombatReplayRecording.Pending?.MarkIncomplete("test_fixture_state_injection");
        await CreatureCmd.SetCurrentHp(player.Creature, hp);
        ForceInitialEnemyMoves(live);
        ForceInitialEnemyStateLogs(live);
        string expected = File.ReadAllText(Path.Combine(area, "frozen-root-stamp.txt")).TrimEnd('\r', '\n');
        string actual = ContinuationStamp.CaptureLive(live).StateText;
        _writer.WriteGeneratedArtifact("frozen-root-verification.json", new
        {
            matches = actual == expected, expected, actual,
        });
        if (actual != expected)
            throw new InvalidOperationException("原生完整根与冻结根不同；不得继续部署或作为性能证据。");
        CombatBugReportExporter.ResetOutcomeAtRestoredRoot(live);
        BattleDamageTracker.Begin(live);
        _completedChecks.Add("FrozenHealingBound:ExactNativeRootIncludingRng:OutcomeResetAtFrozenRoot");
    }

}
