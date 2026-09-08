using CombatSolver.Engine.InCombat.Simulation;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertSearchObjectiveEdgeContracts(CombatState combat)
    {
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            Objective = new(SearchObjective.Balanced), IgnoreLongTermRewards = true,
            FinalBossHpStrategy = BossHpStrategy.ProgressionFirst,
        };
        if (policy.EffectiveHasGrowthTargets || policy.EffectiveGrowthBudgets != default
            || policy.ResolveStrategicHpRelief(BossHpRelief.RunEnding) != BossHpRelief.RunEnding)
            throw new InvalidOperationException("Ignore-rewards changed the legacy boss HP strategy.");
        var capped = new SearchObjectiveOutcome(new(SearchObjective.PermanentGrowth, 10, 1, 3),
            new(0, 0, 3), 0, 0, 0, 0, 0, 0, 0, 50);
        SolverInterimResult first = new(true, 0, 0, 0, 10, 1, 0, 0, 2) { Objective = capped };
        SolverInterimResult improved = first with { PotionStrategicCost = 0, ProjectedBattlePotionCount = 0 };
        if (!SolverInterimResultOrdering.IsBetter(improved, first)
            || SolverInterimResultOrdering.IsBetter(first, improved))
            throw new InvalidOperationException("Reached growth target hides a cheaper tied route.");
        _completedChecks.Add("SearchObjectives:LegacyIgnoreBossPolicy:ReachedTargetPotionTie");
    }

    private async Task AssertSearchObjectiveUiAsync(CombatState combat)
    {
        SolverSettingsData original = SolverSettings.Current;
        string language = LocManager.Instance.Language;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var toggle = typeof(SolverOverlay).GetMethod("ToggleGrowthStrategy", flags)!;
        try
        {
            SolverSettings.ApplyForTesting(original with { AutomaticCalculationEnabled = false,
                IgnoreLongTermRewards = false, Objective = new(SearchObjective.PermanentGrowth, 20, 1, 3) });
            var root = CombatRootSnapshot.Capture(combat);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
            {
                ForceShortOnly = true, ShortBudgetOverrideMilliseconds = 2000,
                MaxDegreeOfParallelism = 1, PotionPolicy = SolverPotionPolicy.Disabled,
            };
            if ((policy with { Objective = new(SearchObjective.Survival) }).ResolveStrategicHpRelief(BossHpRelief.RunEnding)
                != BossHpRelief.None) throw new InvalidOperationException("Survival objective must retain HP value at bosses.");
            var names = SolverDisplayNames.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null));
            SolverOverlay.ShowResult(_host, SolverOverlaySnapshot.Capture(result, false));
            var panel = (SolverGrowthStrategyPanel)typeof(SolverOverlay).GetField("_growthStrategyPanel", flags)!.GetValue(null)!;
            if (!panel.Visible) toggle.Invoke(null, null);
            await NextFrameAsync();
            var mode = (OptionButton)panel.FindChild("SearchObjectiveMode", true, false);
            foreach (SearchObjective selected in Enum.GetValues<SearchObjective>())
            {
                mode.Select((int)selected);
                mode.EmitSignal(OptionButton.SignalName.ItemSelected, (long)selected);
                if (SolverSettings.Current.Objective.Mode != selected)
                    throw new InvalidOperationException("Objective dropdown did not persist selection.");
            }
            mode.Select((int)SearchObjective.PermanentGrowth);
            mode.EmitSignal(OptionButton.SignalName.ItemSelected, (long)SearchObjective.PermanentGrowth);
            ((SpinBox)panel.FindChild("ObjectiveMaximumHpLoss", true, false)).Value = 15;
            ((SpinBox)panel.FindChild("ObjectiveMinimumHp", true, false)).Value = 5;
            ((SpinBox)panel.FindChild("ObjectiveGrowthTarget", true, false)).Value = 4;
            if (SolverSettings.Current.Objective != new SearchObjectivePolicy(SearchObjective.PermanentGrowth, 15, 5, 4))
                throw new InvalidOperationException("Objective limit inputs did not persist.");
            foreach (string locale in new[] { "eng", "zhs", "zht" })
            {
                LocManager.Instance.SetLanguage(locale);
                await NextFrameAsync();
                await NextFrameAsync();
                if (mode.GetItemText((int)SearchObjective.PermanentGrowth) != SolverText.Get("永久培养优先"))
                    throw new InvalidOperationException("Objective selector did not refresh its language.");
                if (SolverOverlay.SearchSummaryTextForTesting?.Contains(SearchObjectiveText.Summary(result.Snapshot.Objective)) != true)
                    throw new InvalidOperationException("Objective result summary did not refresh its language.");
                Rect2 bounds = panel.GetGlobalRect();
                Vector2 viewport = _host.GetViewport().GetVisibleRect().Size;
                if (bounds.Position.X < 0 || bounds.Position.Y < 0 || bounds.End.X > viewport.X || bounds.End.Y > viewport.Y)
                    throw new InvalidOperationException($"Objective panel outside viewport: {bounds} / {viewport}");
                if (DisplayServer.GetName() != "headless" && locale != "zht")
                {
                    await Task.Delay(1100);
                    await NextFrameAsync();
                    _host.GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(OS.GetUserDataDir(), $"objectives-{locale}.png"));
                }
            }
            _completedChecks.Add("SearchObjectives:NativeControls:Persistence:LiveLocales:Viewport:BossHpPolicy");
        }
        finally
        {
            LocManager.Instance.SetLanguage(language);
            SolverSettings.ApplyForTesting(original);
            var panel = (SolverGrowthStrategyPanel?)typeof(SolverOverlay).GetField("_growthStrategyPanel", flags)!.GetValue(null);
            if (panel?.Visible == true) toggle.Invoke(null, null);
        }
    }

    private async Task AssertSearchObjectivesAsync(CombatState combat)
    {
        static void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("Search objectives: " + message);
        }
        bool resources = _request.ScenarioId == "SEARCH-OBJECTIVES-RESOURCES";
        var mode = resources ? SearchObjective.NetResources : SearchObjective.PermanentGrowth;
        SearchObjectivePolicy objective = new(mode, 100, 1, 0);
        SearchObjectiveOutcome gain = new(objective, new(0, 0, 5), 20, 0, 0, 0, 0, 0, 5, 50);
        SearchObjectiveOutcome plain = gain with { Growth = default, GoldGain = 0, BattleHpLoss = 0 };
        Check(gain.CompareTo(plain) < 0, "gain wins within limits");
        Check((gain with { BattleHpLoss = 101 }).CompareTo(plain) > 0, "HP loss bound outranks reward");
        Check((gain with { EndingHp = 0 }).CompareTo(plain) > 0, "ending HP bound outranks reward");
        Check(SolverInterimResultOrdering.ComparePrimaryQuality(false, -100, 1, true, 0, 1,
            candidateObjective: gain, currentObjective: plain) > 0, "victory outranks farming");
        var capped = gain with { Policy = new(SearchObjective.PermanentGrowth, 100, 1, 3) };
        Check(capped.CompareTo(capped with { Growth = new(0, 0, 10) }) == 0, "growth cap removes extra reward");
        Check((gain with { PotionValueChange = -100 }).NetResourceScore == -80, "net subtracts consumed inventory");
        var original = SolverSettings.Current;
        string language = LocManager.Instance.Language;
        try
        {
            foreach (SearchObjective selected in Enum.GetValues<SearchObjective>())
            {
                var configured = original with { Objective = objective with { Mode = selected }, IgnoreLongTermRewards = false };
                Check(SolverSettings.RoundTripForTesting(configured).Objective == configured.Objective, "settings round trip");
                SolverSettings.ApplyForTesting(configured);
                Check(SolverSettings.Capture().Objective == configured.Objective, "root policy capture");
                using SolverGrowthStrategyPanel panel = new();
                Check(panel.SettingsConfiguredForTesting, "growth sidebar honors active objective");
            }
            foreach (string locale in new[] { "eng", "zhs", "zht" })
            {
                LocManager.Instance.SetLanguage(locale);
                _ = SearchObjectiveText.Summary(gain);
                _ = SearchObjectiveText.Details(gain);
                _ = SearchObjectiveText.Details(gain with { EndingHp = 0 });
                _ = SearchObjectiveText.Details(capped);
            }
        }
        finally
        {
            LocManager.Instance.SetLanguage(language);
            SolverSettings.ApplyForTesting(original);
        }
        _completedChecks.Add("SearchObjectives:Limits:Victory:Cap:NetValue:Settings:Sidebar:Locales");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            Objective = objective,
            GrowthBudgets = default, IgnoreLongTermRewards = false,
            PotionPolicy = SolverPotionPolicy.Disabled,
            ForceShortOnly = true, ShortBudgetOverrideMilliseconds = 2000,
            MaxDegreeOfParallelism = 1, VerifyIncrementalSearch = true,
        };
        Check(policy.EffectiveHasGrowthTargets, "reward objectives disable HP early stops");
        var driver = new CombatBeamSolver(root, names, damage, policy);
        SimulationSnapshot initial = InvokeForcedTerminalReplay(driver, [], null, 0, null);
        try
        {
            CombatPredictionSimulator fork = initial.Simulator.Fork();
            var child = (SimulatedCombatState)fork.State.CombatState;
            child.RecordPermanentGrowth(block: 3);
            Check(((SimulatedCombatState)initial.Simulator.State.CombatState).PermanentGrowth == default,
                "permanent growth fork isolation");
            SimulationSnapshot observed = (SimulationSnapshot)InvokeForcedTerminalMethod(driver, "Snapshot",
                [fork, initial.Turn, 0, initial.ShufflesCrossed, SearchBoundaryReason.None, initial.ProcessedEnemyDeaths])!;
            Check(observed.Objective.Growth.CardBlock == 3 && observed.StateKey != initial.StateKey,
                "growth amount propagates to snapshot and state identity");
            var pile = fork.State.GetPlayerCombatState(root.PlayerIdentity).Hand;
            Check(pile.TryGetCachedUnorderedFingerprint(out _, out _), "unordered pile cache populated");
            foreach (var statePile in new[] { fork.State.GetPlayerCombatState(root.PlayerIdentity).Hand,
                fork.State.GetPlayerCombatState(root.PlayerIdentity).DrawPile,
                fork.State.GetPlayerCombatState(root.PlayerIdentity).DiscardPile,
                fork.State.GetPlayerCombatState(root.PlayerIdentity).ExhaustPile })
                statePile.InvalidateFingerprint();
            SimulationSnapshot recomputed = (SimulationSnapshot)InvokeForcedTerminalMethod(driver, "Snapshot",
                [fork, initial.Turn, 0, initial.ShufflesCrossed, SearchBoundaryReason.None, initial.ProcessedEnemyDeaths])!;
            Check(observed.UnorderedPileKey == recomputed.UnorderedPileKey, "cached and recomputed fingerprints agree");
            pile.DisableFingerprintCache();
            pile.SetCachedUnorderedFingerprint(1, 2);
            Check(!pile.TryGetCachedUnorderedFingerprint(out _, out _), "disabled cache stays disabled");
            recomputed.ReleaseSimulator();
            observed.ReleaseSimulator();
        }
        finally { initial.ReleaseSimulator(); }
        _completedChecks.Add("SearchObjectives:Fork:AmountFingerprint:UnorderedCache");

        async Task<SolverResult> Solve(SearchObjectivePolicy selection)
            => await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage,
                policy with { Objective = selection }, CancellationToken.None, null));
        SolverResult survival = await Solve(objective with { Mode = SearchObjective.Survival });
        SolverResult balanced = await Solve(objective with { Mode = SearchObjective.Balanced });
        SolverResult reward = await Solve(objective);
        Check(survival.Snapshot.AllEnemiesDead && balanced.Snapshot.AllEnemiesDead && reward.Snapshot.AllEnemiesDead,
            "all modes find complete victory");
        Check(reward.Snapshot.Objective.MeetsLimits, "reward route meets configured limits");
        if (resources)
            Check(reward.Snapshot.Objective.GoldGain > survival.Snapshot.Objective.GoldGain, "net objective earns extra gold");
        else
            Check(reward.Snapshot.Objective.Growth.CardBlock > survival.Snapshot.Objective.Growth.CardBlock,
                "growth objective earns actual permanent block");
        SolverResult constrained = await Solve(objective with { MaximumBattleHpLoss = 0 });
        Check(constrained.Snapshot.AllEnemiesDead && constrained.ProjectedBattleHpLost == 0,
            "zero-loss constraint chooses safe victory");
        Check(constrained.Snapshot.Objective.TargetValue < reward.Snapshot.Objective.TargetValue,
            "constraint rejects paid farming");
        _completedChecks.Add("SearchObjectives:NativeRoot:Balanced:Survival:Reward:Constrained:IncrementalReplay");
        Entry.Logger.Info($"[CombatSolver/Test] OBJECTIVES_OK mode={mode} survival_loss={survival.ProjectedBattleHpLost} " +
            $"reward_loss={reward.ProjectedBattleHpLost} growth={reward.Snapshot.Objective.Growth.Total} " +
            $"gold={reward.Snapshot.Objective.GoldGain} constrained_loss={constrained.ProjectedBattleHpLost}");
    }
}
