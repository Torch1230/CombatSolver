using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertSharedSearchEvidenceAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH", "INFLAME" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 12);
        SetEnergy(player, 3);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = new(0, 0, 0, []);
        SharedSearchEvidence evidence = new();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, false, null) with
        {
            SharedEvidence = evidence, UseAutomaticSearch = false,
            PotionPolicy = SolverPotionPolicy.Disabled, MaxDegreeOfParallelism = 1,
            VerifyIncrementalSearch = true, FixedBudget = true,
            StopAtAcceptableBattleHpLoss = false,
            Profile = SolverSearchProfile.Default with
                { BeamWidth = 8, MaxExpandedNodes = 128, SoftTimeBudgetMilliseconds = 1500 },
        };
        var first = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
            searchProfile: policy.Profile).ProbeSharedRootForTesting());
        if (!first.Reusable || evidence.ProbeStores != 1 || evidence.ProbeHits != 0)
            throw new InvalidOperationException("Shared evidence: first root probe did not populate the cache.");
        var second = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
            searchProfile: policy.Profile with { BeamWidth = 16 }).ProbeSharedRootForTesting());
        if (first != second || evidence.ProbeHits != 1)
            throw new InvalidOperationException("Shared evidence: cross-width probe differs from full replay.");
        SolverResult serial = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
            searchProfile: policy.Profile).Solve());
        if (!serial.Snapshot.AllEnemiesDead || serial.Snapshot.PlayerDead || evidence.OutcomeBackups == 0)
            throw new InvalidOperationException("Shared evidence: completed native route was not backed up.");
        SolverResult parallel = await Task.Run(() => new CombatBeamSolver(root, names, damage,
            policy with { VerifyIncrementalSearch = false, MaxDegreeOfParallelism = 2 },
            searchProfile: policy.Profile).Solve());
        if (!parallel.Snapshot.AllEnemiesDead || parallel.Snapshot.PlayerDead
            || parallel.ProjectedBattleHpLost != serial.ProjectedBattleHpLost)
            throw new InvalidOperationException("Shared evidence: DOP2 route quality changed.");
        using CancellationTokenSource stopped = new();
        stopped.Cancel();
        bool cancelled = false;
        try
        {
            await Task.Run(() => new CombatBeamSolver(root, names, damage, policy, stopped.Token,
                searchProfile: policy.Profile).ProbeSharedRootForTesting());
        }
        catch (OperationCanceledException) { cancelled = true; }
        if (!cancelled) throw new InvalidOperationException("Shared evidence: cache masked cancellation.");
        _completedChecks.Add($"SharedEvidence:CrossWidth:FullReplay:DOP2:Cancellation:{evidence.Describe()}");
    }
}
