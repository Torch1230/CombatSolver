using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertSmartPotionAuditBudgetAsync(CombatState live, Player player)
    {
        await ClearOrderedEffectFixtureAsync(live, player);
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 1);
        await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.StrengthPower>(
            new ThrowingPlayerChoiceContext(), live.Enemies.Single(), 50, live.Enemies.Single(), null);
        await InjectCardAsync(live, player, new()
        {
            CardId = "BLOODLETTING", Pile = "Hand", DynamicVars = new() { ["HpLoss"] = 21 },
        });
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        SetEnergy(player, 0);
        foreach (var potion in player.Potions.ToArray()) potion.Discard();
        InjectPotionForTest(player, "FIRE_POTION");
        InjectPotionForTest(player, "STRENGTH_POTION");
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        SolverDisplayNames names = SolverDisplayNames.Capture(live);
        BattleDamageSnapshot damage = new(0, 0, 0, []);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), live, false, null) with
        {
            FixedBudget = true, BudgetOverrideMilliseconds = null,
            MaxDegreeOfParallelism = 1, VerifyIncrementalSearch = true,
            DetailedDiagnostics = false, MeasurePhasePerformance = false,
            UseBeamWidthPortfolio = false, UseNoveltyPortfolio = false,
            StopAtAcceptableBattleHpLoss = false, IgnoreLongTermRewards = true,
            RelicTargets = [], PredictPotionReward = false,
            PotionPolicy = SolverPotionPolicy.Smart, PotionStrategy = new(SolverPotionPolicy.Smart, []),
            Profile = SolverSearchProfile.Default with
            {
                BeamWidth = 8, MaxExpandedNodes = 256, SoftTimeBudgetMilliseconds = 5_000,
                StopPortfolioAtHpTarget = false,
            },
        };
        string before = ContinuationStamp.CaptureLive(live).StateText;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        SolverResult baseline = await Task.Run(() => new CombatBeamSolver(root, names, damage,
            policy, deadline.Token, potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
        if (!baseline.Snapshot.AllEnemiesDead || baseline.ExplicitPotionCount != 0
            || baseline.ProjectedBattleHpLost != 21)
            throw new InvalidOperationException($"Budget fixture requires a 21-loss potion-free victory: "
                + $"won={baseline.Snapshot.AllEnemiesDead} potions={baseline.ExplicitPotionCount} "
                + $"loss={baseline.ProjectedBattleHpLost}.");

        async Task<(SolverResult Result, int Members, string[] Logs)> Audit(SearchPolicySnapshot input)
        {
            Stopwatch clock = Stopwatch.StartNew();
            SolverSearchProfile expiredProfile = input.Profile with { SoftTimeBudgetMilliseconds = 1 };
            input = input with { Profile = expiredProfile };
            SearchBudgetLedger ledger = new(clock, input);
            List<string> logs = [];
            input = input with { RequestWorkTotals = ledger.WorkTotals, Diagnostics = new(logs.Add, _ => { }) };
            SearchPassContext context = new(root, names, damage, input, expiredProfile, clock,
                ledger, deadline.Token, null, null);
            await Task.Delay(5, deadline.Token);
            SolverResult result = await Task.Run(() =>
                CombatSearchCoordinator.RunSupplementalAuditsForTesting(context, baseline));
            return (result, ledger.WorkTotals.Snapshot().RecordedSolverCount, logs.ToArray());
        }

        var audited = await Audit(policy);
        if (!audited.Result.Snapshot.AllEnemiesDead || audited.Result.ProjectedBattleHpLost != 0
            || audited.Result.ExplicitPotionCount != 1 || audited.Members != 1
            || !audited.Logs.Any(line => line.Contains("exhausted=true", StringComparison.Ordinal))
            || audited.Logs.Any(line => line.Contains("SMART_POTION_GRADIENT layer=2 ", StringComparison.Ordinal)))
            throw new InvalidOperationException("Exhausted Smart audit must retain its one-layer zero-loss victory.");
        foreach (var guarded in new[]
        {
            policy with { PotionPolicy = SolverPotionPolicy.Disabled, PotionStrategy = new(SolverPotionPolicy.Disabled, []) },
            policy with { PotionPolicy = SolverPotionPolicy.RequireAtLeastOne, PotionStrategy = new(SolverPotionPolicy.RequireAtLeastOne, []) },
            policy with { IncludeTurnSetup = true },
            policy with { PotionStrategy = new(SolverPotionPolicy.Smart,
                [new(0, "FIRE_POTION", SolverPotionDirective.Force)]) },
        })
        {
            var skipped = await Audit(guarded);
            if (!ReferenceEquals(skipped.Result, baseline) || skipped.Members != 0)
                throw new InvalidOperationException("Dedicated potion audit must preserve its policy eligibility.");
        }
        if (ContinuationStamp.CaptureLive(live).StateText != before || root.ContinuationStamp.StateText != before)
            throw new InvalidOperationException("Potion audit changed live or root state.");
        _completedChecks.Add("SmartPotionAuditBudget:ExhaustedLedger:Loss21To0:OnePotion:OneMember:PolicyGuards:FullReplay:LiveIsolation");
    }
}

internal static partial class CombatSearchCoordinator
{
    internal static SolverResult RunSupplementalAuditsForTesting(SearchPassContext context, SolverResult primary)
        => RunSupplementalAudits(context, primary, new SmartLayerMemoryForecast());
}
