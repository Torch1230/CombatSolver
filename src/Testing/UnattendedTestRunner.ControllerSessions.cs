using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertControllerSessionLifecycleAsync(CombatState combat)
    {
        CombatBeamSolver.VerifyCycleTranspositionLeasePolicyForTesting();
        NGame host = NGame.Instance
            ?? throw new InvalidOperationException("控制器会话测试找不到 NGame。");
        if (SolverController.SolverDisabled)
            throw new InvalidOperationException("控制器会话测试要求求解器初始启用。");
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("药水策略 UI 测试找不到本地玩家。");
        (int Slot, PotionModel Potion)? strategyPotion = Enumerable.Range(0, player.PotionSlots.Count)
            .Select(slot => (Slot: slot, Potion: player.GetPotionAtSlotIndex(slot)))
            .Where(item => item.Potion != null && PotionOnUseSupport.CanSearch(item.Potion))
            .Select(item => (item.Slot, item.Potion!))
            .Cast<(int Slot, PotionModel Potion)?>()
            .FirstOrDefault();
        if (strategyPotion is { } forcedPotion)
        {
            SolverSettingsData settingsBeforeStaleDirectiveCheck = SolverSettings.Current;
            try
            {
                PersistedPotionDirective staleDirective = new(
                    player.PotionSlots.Count,
                    forcedPotion.Potion.Id.Entry,
                    SolverPotionDirective.Disabled);
                SolverSettings.ApplyForTesting(settingsBeforeStaleDirectiveCheck with
                {
                    PotionDirectives = [staleDirective],
                });
                PotionStrategySnapshot staleStrategy = SolverController.CapturePotionStrategy(
                    combat,
                    SolverPotionPolicy.Smart);
                if (staleStrategy.Directives.Count != 0)
                    throw new InvalidOperationException("超出当前药水栏的持久化策略没有被忽略。");
            }
            finally
            {
                SolverSettings.ApplyForTesting(settingsBeforeStaleDirectiveCheck);
            }

            SolverController.SetPotionDirectiveForTesting(
                combat,
                forcedPotion.Slot,
                forcedPotion.Potion.Id.Entry,
                SolverPotionDirective.Force);
            try
            {
                SolverSettingsSnapshot settings = SolverSettings.Capture();
                SearchInteractionState interaction = new();
                SearchPolicySnapshot forcedPolicy = SolverController.CaptureSearchPolicy(
                    settings,
                    combat,
                    includeTurnSetup: false,
                    theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
                {
                    ShortProfile = settings.ShortProfile with
                    {
                        MaxExpandedNodes = Math.Min(500, settings.ShortProfile.MaxExpandedNodes),
                        SoftTimeBudgetMilliseconds = 5_000,
                    },
                    ForceShortOnly = true,
                    MaxDegreeOfParallelism = 4,
                    Interaction = interaction,
                };
                CombatRootSnapshot forcedRoot = CombatRootSnapshot.Capture(combat);
                SolverDisplayNames forcedDisplayNames = SolverDisplayNames.Capture(combat);
                BattleDamageSnapshot forcedBattleDamage = BattleDamageTracker.Observe(combat);
                new CombatBeamSolver(
                    forcedRoot,
                    forcedDisplayNames,
                    forcedBattleDamage,
                    forcedPolicy,
                    searchProfile: forcedPolicy.ShortProfile with { BeamWidth = 1 })
                    .VerifyFinalPolicyQualificationRetentionForTesting(
                        forcedPotion.Potion.Id.Entry,
                        forcedPotion.Slot);
                bool forcedAdoptionRequested = false;
                SolverResult forcedResult = await Task.Run(() => CombatSearchCoordinator.Solve(
                    forcedRoot,
                    forcedDisplayNames,
                    forcedBattleDamage,
                    forcedPolicy,
                    CancellationToken.None,
                    progress =>
                    {
                        if (progress.CurrentBestResult != null
                            && !forcedAdoptionRequested)
                        {
                            forcedAdoptionRequested = true;
                            interaction.RequestApplyCurrentTurn();
                        }
                    }));
                if (!forcedAdoptionRequested
                    || !forcedResult.BestNode.Actions.Any(action =>
                        action.Kind == PlanActionKind.UsePotion
                        && action.PotionSlot == forcedPotion.Slot
                        && string.Equals(
                            action.PotionId,
                            forcedPotion.Potion.Id.Entry,
                            StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("强制用药的可采用路线没有使用指定槽位的指定药水。");
                }
            }
            finally
            {
                SolverController.SetPotionDirectiveForTesting(
                    combat,
                    forcedPotion.Slot,
                    forcedPotion.Potion.Id.Entry,
                    SolverPotionDirective.Smart);
            }
            await AssertBoundedSmartPotionAuditAsync(combat);
        }

        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        if (!SolverController.IsSearching)
            throw new InvalidOperationException("控制器没有建立搜索会话。");
        int progressTurn = player.PlayerCombatState!.TurnNumber;
        SolverSpeculativeRoutePreview progressPreview = new(
            CandidateVersion: 1,
            StartTurnNumber: progressTurn,
            ProjectedBattlePotionCount: 0,
            ProjectedBattleHpLost: 9,
            CombatEnded: false,
            OnlyDeathRoutesFound: false,
            HasRisk: false,
            Turns:
            [
                new SolverFrontierTurn(
                    progressTurn,
                    Actions: [],
                    HpLost: 9,
                    EnemyHpLost: 1,
                    EnergyLeft: 0,
                    CombatEnded: false),
            ]);
        SolverOverlay.ShowProgress(
            new SolverProgress(
                progressTurn,
                progressTurn,
                CompletedTurnLayers: 0,
                PlayDepth: 0,
                ExpandedNodes: 7,
                ReviewedWorldlines: 37,
                MaxNodes: 100,
                FrontierNodes: 0,
                EndedNodes: 0,
                ElapsedMilliseconds: 500,
                Phase: "test"),
            deployWhenReady: false,
            reviewedWorldlinesBeforeSearch: 5);
        if (SolverOverlay.SearchSummaryTextForTesting != "已查阅 42 条世界线")
            throw new InvalidOperationException("搜索进度区没有独立显示累计查阅世界线数量。");
        double progressRatio = SolverOverlay.SearchProgressRatioForTesting;
        SolverOverlay.ShowProgress(
            new SolverProgress(
                progressTurn,
                progressTurn,
                CompletedTurnLayers: 0,
                PlayDepth: 0,
                ExpandedNodes: 1,
                ReviewedWorldlines: 40,
                MaxNodes: 100,
                FrontierNodes: 0,
                EndedNodes: 0,
                ElapsedMilliseconds: 600,
                Phase: "正在搜索无药路线",
                CurrentBestResult: new SolverInterimResult(
                    Won: false,
                    OutstandingStolenResource: 0,
                    ProjectedBattleHpLost: 9,
                    StrategicHpDeficit: 9,
                    PotionStrategicCost: 0,
                    ProjectedBattlePotionCount: 0,
                    CombatEndedTurn: null,
                    EnemyHp: 1,
                    Score: 0d),
                SpeculativeRoutePreview: progressPreview),
            deployWhenReady: false,
            reviewedWorldlinesBeforeSearch: 5,
            bestSnapshot: SolverOverlaySnapshot.CaptureSpeculativeRoute(progressPreview));
        if (SolverOverlay.SearchProgressRatioForTesting < progressRatio
            || SolverOverlay.ReviewSummaryTextForTesting?.Contains(
                "正在搜索无药路线",
                StringComparison.Ordinal) != true
            || SolverOverlay.SearchSummaryTextForTesting?.Contains(
                "求解器当前考虑",
                StringComparison.Ordinal) != true
            || SolverOverlay.HpOutcomeTextForTesting?.Contains(
                "预计战损 未知",
                StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException("药水补查开始后搜索进度条倒退。");
        }
        if (SolverController.IsSearching
            && (SolverOverlay.StopSearchButtonTextForTesting != "停止计算"
                || SolverOverlay.StopSearchButtonDisabledForTesting))
        {
            throw new InvalidOperationException("搜索期间独立停止计算按钮不可用。");
        }
        if (!SolverOverlay.MessageWrappingEnabledForTesting)
            throw new InvalidOperationException("求解器消息区域没有启用自动换行。");
        if (!SolverOverlay.UploadProgressConfiguredForTesting)
            throw new InvalidOperationException("在线问题包上传没有配置可视化进度条和单实例按钮初始状态。");
        if (!SolverOverlay.SearchCompletionNotificationSettingsConfiguredForTesting)
            throw new InvalidOperationException("搜索结束通知三态选项没有按持久化设置加载。");
        if (!SolverOverlay.VisualSettingsConfiguredForTesting
            || SolverOverlay.ActiveThemeForTesting != SolverSettings.Current.OverlayTheme)
        {
            throw new InvalidOperationException("界面主题或覆盖层透明度没有按持久化设置加载。");
        }
        if (!SolverOverlay.BossHpStrategySettingsConfiguredForTesting
            || !SolverOverlay.ExerciseBossHpStrategySettingsForTesting())
        {
            throw new InvalidOperationException("第一、二幕与最终 Boss 的血量策略没有独立持久化。");
        }
        if (!SolverOverlay.ExerciseBossHpStrategyHintForTesting())
            throw new InvalidOperationException("幕末 Boss 血量策略提示没有按战斗类型独立显示和关闭。");
        bool resizeUiConfigured = SolverOverlay.ResizeUiConfiguredForTesting;
        bool resizePersistencePassed = await SolverOverlay.ExerciseOverlayResizePersistenceForTestingAsync();
        if (!resizeUiConfigured || !resizePersistencePassed)
        {
            throw new InvalidOperationException(
                $"覆盖层拖拽缩放、尺寸持久化或展开恢复没有正确建立：" +
                $"configured={resizeUiConfigured}, persistence={resizePersistencePassed}。");
        }
        if (!SolverOverlay.SettingsTabsConfiguredForTesting
            || !SolverOverlay.ExerciseSettingsTabSwitchingForTesting())
        {
            throw new InvalidOperationException("设置页没有按常规、性能、反馈三页独立切换。");
        }
        if (!SolverOverlay.ManualSystemMemoryReleaseButtonConfiguredForTesting)
            throw new InvalidOperationException("强制释放内存按钮没有位于主界面内存条右侧。");
        if (!SolverOverlay.NoGcControlsConfiguredForTesting)
            throw new InvalidOperationException("NoGC 开关或预算输入没有归属性能设置页。");
        bool memoryUsageBarConfigured = SolverOverlay.MemoryUsageBarConfiguredForTesting;
        bool memoryUsageBarFormatting = SolverOverlay.ExerciseMemoryUsageBarForTesting();
        if (!memoryUsageBarConfigured || !memoryUsageBarFormatting)
        {
            throw new InvalidOperationException(
                $"主界面内存占用条没有按搜索 GC 回收边界建立：" +
                $"configured={memoryUsageBarConfigured} formatting={memoryUsageBarFormatting}。");
        }
        if (!SolverOverlay.ExercisePerformanceHintForTesting())
            throw new InvalidOperationException("战损结果没有可用的性能预设重试胶囊提示。");
        if (!SolverOverlay.ExerciseSearchLimitHintForTesting())
            throw new InvalidOperationException("时间或节点上限停止后没有显示不可关闭的顶部提示。");
        if (strategyPotion is { } potionEntry)
        {
            if (!SolverOverlay.PotionStrategyUiConfiguredForTesting
                || !SolverOverlay.ExercisePotionStrategyUiForTesting())
            {
                throw new InvalidOperationException("主界面药水策略没有按右侧图标卡片网格建立。");
            }
            PotionStrategySnapshot initialStrategy = SolverController.CapturePotionStrategy(
                combat,
                SolverPotionPolicy.Smart);
            if (initialStrategy.Resolve(potionEntry.Slot, potionEntry.Potion.Id.Entry)
                != SolverPotionDirective.Smart)
            {
                throw new InvalidOperationException("新获得药水没有默认使用智能策略。");
            }
            SolverController.SetPotionDirectiveForTesting(
                combat,
                potionEntry.Slot,
                potionEntry.Potion.Id.Entry,
                SolverPotionDirective.Force);
            SolverSettings.ApplyForTesting(SolverSettings.RoundTripForTesting(SolverSettings.Current));
            PotionStrategySnapshot forcedStrategy = SolverController.CapturePotionStrategy(
                combat,
                SolverPotionPolicy.Smart);
            PlanAction forcedUse = new(
                PlanActionKind.UsePotion,
                player.PlayerCombatState!.TurnNumber,
                PotionSlot: potionEntry.Slot,
                PotionId: potionEntry.Potion.Id.Entry);
            if (!forcedStrategy.HasForcedDirectives
                || !forcedStrategy.EvaluateForcedUses([forcedUse], renewablePotionShapedRock: false)
                    .AllForcedUsesSatisfied
                || forcedStrategy.EvaluateForcedUses([], renewablePotionShapedRock: false)
                    .AllForcedUsesSatisfied)
            {
                throw new InvalidOperationException("指定药水没有形成精确槽位和药水身份约束。");
            }
            if (SolverSettings.ResolvePotionDirective(
                    potionEntry.Slot,
                    potionEntry.Potion.Id.Entry + "_REPLACEMENT") != SolverPotionDirective.Smart)
            {
                throw new InvalidOperationException("同槽位的新药错误继承了旧药的持久化策略。");
            }
            SolverController.SetPotionDirectiveForTesting(
                combat,
                potionEntry.Slot,
                potionEntry.Potion.Id.Entry,
                SolverPotionDirective.Disabled);
            PotionStrategySnapshot disabledStrategy = SolverController.CapturePotionStrategy(
                combat,
                SolverPotionPolicy.Smart);
            if (disabledStrategy.AllowsExplicitUse(
                    potionEntry.Slot,
                    potionEntry.Potion.Id.Entry,
                    SolverPotionPolicy.Smart,
                    forceAllDisabled: false))
            {
                throw new InvalidOperationException("保护药水仍进入主动用药候选。");
            }
            SolverController.SetPotionDirectiveForTesting(
                combat,
                potionEntry.Slot,
                potionEntry.Potion.Id.Entry,
                SolverPotionDirective.Smart);
            if (SolverSettings.Current.PotionDirectives.Any(item => item.Slot == potionEntry.Slot))
                throw new InvalidOperationException("恢复智能后仍保留了逐瓶策略覆盖项。");
        }
        if (!SolverOverlay.ExerciseSearchCompletionNotificationPolicyForTesting())
            throw new InvalidOperationException("搜索结束通知三态选项不能无损回读旧设置字段。");
        if (!SolverOverlay.ExerciseVisualSettingsForTesting())
            throw new InvalidOperationException("浅色主题或覆盖层透明度不能无损回读设置。");
        SolverSettingsData originalVisualSettings = SolverSettings.Current;
        SolverOverlayTheme alternateTheme = originalVisualSettings.OverlayTheme == SolverOverlayTheme.Dark
            ? SolverOverlayTheme.Light
            : SolverOverlayTheme.Dark;
        try
        {
            SolverSettings.ApplyForTesting(originalVisualSettings with
            {
                OverlayTheme = alternateTheme,
                OverlayOpacity = 0.65f,
            });
            SolverOverlay.ApplyConfiguredTheme();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (SolverOverlay.ActiveThemeForTesting != alternateTheme
                || Math.Abs(SolverOverlay.OverlayOpacityForTesting - 0.65f) > 0.001f
                || !SolverOverlay.VisualSettingsConfiguredForTesting)
            {
                throw new InvalidOperationException("界面主题切换没有重建覆盖层并恢复透明度设置。");
            }
        }
        finally
        {
            SolverSettings.ApplyForTesting(originalVisualSettings);
            SolverOverlay.ApplyConfiguredTheme();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        SolverSettingsData notificationDefaults = new();
        if (SolverSettings.ResolvePerformancePreset(notificationDefaults)
                != SolverPerformancePreset.Medium
            || !notificationDefaults.EnableNoGcRegion
            || notificationDefaults.NoGcRegionBudgetGigabytes
                != SolverSettings.DefaultNoGcRegionBudgetGigabytes)
        {
            throw new InvalidOperationException("新安装和恢复默认没有使用中性能档与启用的 16 GB 独立内存预算。");
        }
        if (!notificationDefaults.SearchCompletionNotificationsEnabled
            || notificationDefaults.SearchCompletionNotificationMode
            != SolverSearchCompletionNotificationMode.OnlyWhenGameInBackground
            || SearchCompletionNotifier.ShouldNotifyForTesting(
                enabled: false,
                mode: SolverSearchCompletionNotificationMode.Always,
                gameForeground: false)
            || SearchCompletionNotifier.ShouldNotifyForTesting(
                enabled: true,
                mode: SolverSearchCompletionNotificationMode.OnlyWhenGameInBackground,
                gameForeground: true)
            || !SearchCompletionNotifier.ShouldNotifyForTesting(
                enabled: true,
                mode: SolverSearchCompletionNotificationMode.OnlyWhenGameInBackground,
                gameForeground: false)
            || !SearchCompletionNotifier.ShouldNotifyForTesting(
                enabled: true,
                mode: SolverSearchCompletionNotificationMode.Always,
                gameForeground: true))
        {
            throw new InvalidOperationException("搜索结束通知的默认值或前台判断不正确。");
        }
        if (notificationDefaults.OverlayTheme != SolverOverlayTheme.Dark
            || Math.Abs(notificationDefaults.OverlayOpacity - 0.65f) > 0.001f
            || notificationDefaults.OverlayWidth != 1200f
            || notificationDefaults.OverlayHeight != 700f)
        {
            throw new InvalidOperationException("界面默认值不是深色主题、65% 透明度和 1200×700 尺寸。");
        }
        SolverSettingsData originalNotificationSettings = SolverSettings.Current;
        try
        {
            SolverSettings.ApplyForTesting(originalNotificationSettings with
            {
                SearchCompletionNotificationsEnabled = true,
                SearchCompletionNotificationMode = SolverSearchCompletionNotificationMode.Always,
            });
            int requestsBefore = SearchCompletionNotifier.RequestCountForTesting;
            int nativeBefore = SearchCompletionNotifier.NativeNotificationCountForTesting;
            SearchCompletionNotifier.Notify(SearchCompletionNotificationKind.Succeeded);
            if (SearchCompletionNotifier.RequestCountForTesting != requestsBefore + 1
                || SearchCompletionNotifier.NativeNotificationCountForTesting != nativeBefore)
            {
                throw new InvalidOperationException("Headless 搜索结束通知没有停在原生平台调用之前。");
            }
        }
        finally
        {
            SolverSettings.ApplyForTesting(originalNotificationSettings);
        }
        if (!SolverOverlay.ExerciseUploadCompletionTransitionForTesting())
            throw new InvalidOperationException("上传任务结束前按钮状态提前切回空闲，可能重新打开确认弹窗。");
        if (!SolverOverlay.ExercisePerformancePresetPersistenceForTesting())
            throw new InvalidOperationException("0.24.3 性能迁移或预设/内存独立持久化失败。");
        if (SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(1) != 1
            || SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(2) != 2
            || SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(3) != 2
            || SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(4) != 4
            || SolverWeights.ResolveDefaultSearchMaxDegreeOfParallelism(32) != 4)
        {
            throw new InvalidOperationException("默认搜索并行度没有按逻辑处理器数量解析为 1/2/4。");
        }
        string parallelFailure = SolverController.FormatSearchFailureForTesting(
            new InvalidOperationException("parallel failure"),
            parallelSearchWasEnabled: true);
        string serialFailure = SolverController.FormatSearchFailureForTesting(
            new InvalidOperationException("serial failure"),
            parallelSearchWasEnabled: false);
        if (!parallelFailure.Contains("上传问题包", StringComparison.Ordinal)
            || !parallelFailure.Contains("关闭（单线程）", StringComparison.Ordinal)
            || !serialFailure.Contains("上传问题包", StringComparison.Ordinal)
            || serialFailure.Contains("关闭（单线程）", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("搜索失败提示没有按本次请求的并行状态提供恢复建议。");
        }

        // The UI checks above cross several frames. A one-HP fixture can finish its first search
        // during those awaits, so establish a fresh active session immediately before exercising
        // the synchronous stop transition.
        if (!SolverController.IsSearching)
        {
            SolverController.RequestSearch(host, combat, SearchReason.Manual);
            if (!SolverController.IsSearching)
                throw new InvalidOperationException("停止断言前无法重新建立活动搜索会话。");
        }
        int stopNotificationRequestsBefore = SearchCompletionNotifier.RequestCountForTesting;
        int stopNativeNotificationsBefore = SearchCompletionNotifier.NativeNotificationCountForTesting;
        SolverController.StopSearchByUser(host);
        if (SolverController.IsSearching
            || SolverController.IsDeploying
            || SolverController.FullAutoEnabled
            || !SolverController.AutomaticSearchPaused
            || SolverController.CurrentResultForBugReport != null)
        {
            throw new InvalidOperationException("用户停止搜索后仍残留活动会话、路线或自动计算状态。");
        }
        if (SearchCompletionNotifier.RequestCountForTesting != stopNotificationRequestsBefore + 1
            || SearchCompletionNotifier.NativeNotificationCountForTesting
            != stopNativeNotificationsBefore)
        {
            throw new InvalidOperationException("用户停止搜索后没有产生一次受 headless 保护的结束通知。");
        }

        SolverController.RequestSearch(host, combat, SearchReason.AutoTurnStart);
        if (SolverController.IsSearching || !SolverController.AutomaticSearchPaused)
            throw new InvalidOperationException("用户停止后，自动回合入口重新启动了搜索。");

        SolverController.RecordManualProjectionComparisonForTesting(7, 3);
        SolverOverlay.RefreshControls();
        if (!SolverController.ManualRouteImprovementDetected
            || SolverController.LastManualProjectionComparisonForTesting?.Difference != -4
            || !SolverOverlay.ManualRouteImprovementVisibleForTesting)
        {
            throw new InvalidOperationException("手操降低预计战损后没有记录比较结果并显示绿色反馈提示。");
        }
        string liveDescription = SolverController.BuildBugReportDescription("玩家现场描述");
        if (!liveDescription.Contains("玩家现场描述", StringComparison.Ordinal)
            || !liveDescription.Contains("找到更优世界线", StringComparison.Ordinal)
            || !liveDescription.Contains("预计战损 7 → 3", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("在线问题描述没有读取本场手操改线信号。");
        }

        AssertBugReportAutomaticClassification();
        await AssertBugReportUploadBoundariesAsync();

        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        if (!SolverController.IsSearching || SolverController.AutomaticSearchPaused)
            throw new InvalidOperationException("重新计算没有恢复当前及后续回合搜索。");
        SolverController.CancelSearchForTesting();

        await NextFrameAsync();
        await NextFrameAsync();
        if (SolverController.IsSearching
            || SolverController.CurrentResultForBugReport != null
            || SolverController.LastSearchFailureForTesting != null)
        {
            throw new InvalidOperationException("已取消搜索的回调重新写入了控制器状态。");
        }

        long priorReleaseDeadline = System.Environment.TickCount64 + 30_000;
        while (SolverController.PendingSearchReferenceReleaseCountForTesting != 0)
        {
            if (System.Environment.TickCount64 >= priorReleaseDeadline)
                throw new TimeoutException("前一项取消搜索在 30 秒内没有释放 worker+callback 引用。");
            await NextFrameAsync();
        }

        int releasesScheduledBefore =
            SolverController.SearchReferenceReleaseScheduledCountForTesting;
        int releasesCompletedBefore =
            SolverController.SearchReferenceReleaseCompletedCountForTesting;
        int cancellationsDisposedBefore =
            SolverController.SearchCtsDisposeCountForTesting;
        TaskCompletionSource deferredVisualSetupCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool staleDeferredOperationRan = false;
        _ = SolverController.StartCombatDeferredOperation(async token =>
        {
            await deferredVisualSetupCompletion.Task;
            token.ThrowIfCancellationRequested();
            staleDeferredOperationRan = true;
        });
        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        if (!SolverController.IsSearching)
            throw new InvalidOperationException("搜索 A 没有建立控制器会话。");
        SolverController.RequestSearch(host, combat, SearchReason.Manual);
        if (!SolverController.IsSearching)
            throw new InvalidOperationException("搜索 B 没有替换搜索 A。");
        int staleTurnSetupGeneration = SolverController.CombatLifecycleGeneration;
        SolverController.Reset("unattended_search_replacement_release");
        SolverController.ReleaseUnattendedResultReferencesForTesting();
        if (SolverController.LastCompletedResultForTesting != null
            || SolverController.LastTurnSetupResultForTesting != null
            || SolverController.LastSearchFailureForTesting != null)
        {
            throw new InvalidOperationException(
                "最终断言后仍由无人测试观察字段保留上一场 SolverResult 图。");
        }
        if (SolverController.RecordTurnSetupFailure(
                combat,
                staleTurnSetupGeneration,
                new InvalidOperationException("stale turn setup failure"))
            || SolverController.RecordTurnSetupStateMismatch(
                combat,
                staleTurnSetupGeneration,
                "stale turn setup mismatch"))
        {
            throw new InvalidOperationException(
                "Reset 后的旧回合准备完成仍写入了新控制器生命周期。");
        }
        Task referenceRelease = SolverController.LastCombatReferenceReleaseForTesting;
        long releaseDeadline = System.Environment.TickCount64 + 30_000;
        while (SolverController.SearchReferenceReleaseCompletedCountForTesting
               - releasesCompletedBefore < 2)
        {
            if (System.Environment.TickCount64 >= releaseDeadline)
                throw new TimeoutException("搜索 A/B 在 30 秒内没有释放 worker+callback 引用。");
            await NextFrameAsync();
        }
        if (referenceRelease.IsCompleted)
        {
            throw new InvalidOperationException(
                "Reset 引用屏障没有等待回合开始 visual-setup 延迟任务。");
        }
        deferredVisualSetupCompletion.TrySetResult();
        while (!referenceRelease.IsCompleted)
        {
            if (System.Environment.TickCount64 >= releaseDeadline)
            {
                throw new TimeoutException(
                    "搜索 A/B 在 Reset 后 30 秒内没有越过 worker+callback 引用释放屏障。");
            }
            await NextFrameAsync();
        }
        await referenceRelease;
        if (staleDeferredOperationRan)
            throw new InvalidOperationException("已取消的旧战斗延迟任务仍在 Reset 后执行。");
        int releasesScheduled =
            SolverController.SearchReferenceReleaseScheduledCountForTesting
            - releasesScheduledBefore;
        int releasesCompleted =
            SolverController.SearchReferenceReleaseCompletedCountForTesting
            - releasesCompletedBefore;
        int cancellationsDisposed =
            SolverController.SearchCtsDisposeCountForTesting
            - cancellationsDisposedBefore;
        if (releasesScheduled != 2
            || releasesCompleted != 2
            || cancellationsDisposed != 2)
        {
            throw new InvalidOperationException(
                $"搜索 A/B 的 Reset 引用释放不完整：scheduled={releasesScheduled} " +
                $"completed={releasesCompleted} cts_disposed={cancellationsDisposed}。");
        }

        SolverSettingsData settingsBeforeDelayCancellation = SolverSettings.Current;
        try
        {
            const double fullDelaySeconds = 3d;
            SolverSettings.ApplyForTesting(settingsBeforeDelayCancellation with
            {
                DeploymentInterActionDelaySeconds = fullDelaySeconds,
            });
            Task delayOperation = SolverController.StartCombatDeferredOperation(
                token => SolverController.WaitForTurnStartDeploymentDelayAsync(
                    host,
                    turn: -1,
                    token: token));
            if (delayOperation.IsCompleted)
            {
                throw new InvalidOperationException(
                    "3 秒回合开始延迟没有进入真实 SceneTreeTimer 等待。");
            }

            long cancellationStartedAt = Stopwatch.GetTimestamp();
            SolverController.Reset("unattended_deployment_delay_cancel");
            Task delayReferenceRelease = SolverController.LastCombatReferenceReleaseForTesting;
            try
            {
                await delayReferenceRelease.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    "取消回合开始/动作间隔计时器后，战斗引用屏障仍等待完整 3 秒延迟。",
                    ex);
            }
            double cancellationElapsedMilliseconds =
                Stopwatch.GetElapsedTime(cancellationStartedAt).TotalMilliseconds;
            if (!delayOperation.IsCompletedSuccessfully
                || cancellationElapsedMilliseconds >= 1_000d)
            {
                throw new InvalidOperationException(
                    $"取消部署延迟后引用释放不够快：" +
                    $"operation_completed={delayOperation.IsCompletedSuccessfully} " +
                    $"elapsed_ms={cancellationElapsedMilliseconds:F1}。");
            }
        }
        finally
        {
            SolverSettings.ApplyForTesting(settingsBeforeDelayCancellation);
        }

        await SearchGcPolicy.CaptureRootSnapshotBarrier().WaitAsync(TimeSpan.FromSeconds(30));
        SearchGcPolicy.DetachCombatLifecyclePressure(
            "unattended_disabled_gc_reference_barrier_setup");
        SolverSettingsData settingsBeforeDisabledGcReset = SolverSettings.Current;
        TaskCompletionSource disabledReferenceReleaseGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task disabledDeferredOperation = Task.CompletedTask;
        try
        {
            SolverSettings.ApplyForTesting(settingsBeforeDisabledGcReset with
            {
                EnableNoGcRegion = false,
            });
            disabledDeferredOperation = SolverController.StartCombatDeferredOperation(async token =>
            {
                await disabledReferenceReleaseGate.Task;
                token.ThrowIfCancellationRequested();
            });
            SolverController.Reset("unattended_disabled_gc_reference_barrier");
            Task disabledReferenceRelease = SolverController.LastCombatReferenceReleaseForTesting;
            if (disabledReferenceRelease.IsCompleted)
            {
                throw new InvalidOperationException(
                    "关闭 NoGC 的 Reset 夹具没有建立故意延迟的旧图释放任务。");
            }
            Task disabledRootCaptureBarrier = SearchGcPolicy.CaptureRootSnapshotBarrier();
            if (!disabledRootCaptureBarrier.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException(
                    "全程关闭 NoGC 的战斗仍阻塞下一场根快照，未保持 CLR 常规回收语义。");
            }
            disabledReferenceReleaseGate.TrySetResult();
            await disabledReferenceRelease.WaitAsync(TimeSpan.FromSeconds(1));
            await disabledDeferredOperation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            disabledReferenceReleaseGate.TrySetResult();
            await disabledDeferredOperation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            SolverSettings.ApplyForTesting(settingsBeforeDisabledGcReset);
        }

        var deploymentLifecycle =
            await SolverController.ExerciseDeploymentSessionLifecycleForTestingAsync();
        if (!deploymentLifecycle.StaleCompletionPreservedCurrentSession
            || !deploymentLifecycle.BarrierWaitedForBothOperations
            || deploymentLifecycle.ReleasesScheduled != 2
            || deploymentLifecycle.ReleasesCompleted != 2
            || deploymentLifecycle.CancellationsDisposed != 2)
        {
            throw new InvalidOperationException(
                $"部署 A/B 的 Reset 引用释放不完整：" +
                $"stale_preserved={deploymentLifecycle.StaleCompletionPreservedCurrentSession} " +
                $"barrier_waited={deploymentLifecycle.BarrierWaitedForBothOperations} " +
                $"scheduled={deploymentLifecycle.ReleasesScheduled} " +
                $"completed={deploymentLifecycle.ReleasesCompleted} " +
                $"cts_disposed={deploymentLifecycle.CancellationsDisposed}。");
        }
    }

    private static async Task AssertBoundedSmartPotionAuditAsync(CombatState combat)
    {
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchInteractionState interaction = new();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings,
            combat,
            includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
        {
            ShortProfile = settings.ShortProfile with
            {
                MaxExpandedNodes = 100_000,
                SoftTimeBudgetMilliseconds = 1_200,
            },
            ForceShortOnly = true,
            MaxDegreeOfParallelism = 1,
            Interaction = interaction,
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames displayNames = SolverDisplayNames.Capture(combat);
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("药水阶段文案测试找不到本地玩家。");
        (int Slot, PotionModel Potion) potion = Enumerable.Range(0, player.PotionSlots.Count)
            .Select(slot => (Slot: slot, Potion: player.GetPotionAtSlotIndex(slot)))
            .Where(item => item.Potion != null && PotionOnUseSupport.CanSearch(item.Potion))
            .Select(item => (item.Slot, item.Potion!))
            .First();
        (int Slot, PotionModel Potion)[] searchablePotions = Enumerable.Range(0, player.PotionSlots.Count)
            .Select(slot => (Slot: slot, Potion: player.GetPotionAtSlotIndex(slot)))
            .Where(item => item.Potion != null && PotionOnUseSupport.CanSearch(item.Potion))
            .Select(item => (item.Slot, item.Potion!))
            .ToArray();
        PlanAction potionAction = new(
            PlanActionKind.UsePotion,
            player.PlayerCombatState!.TurnNumber,
            PotionSlot: potion.Slot,
            PotionId: potion.Potion.Id.Entry);
        string potionName = displayNames.Potion(potion.Potion.Id.Entry);
        int expectedMinimumPotionCost = PotionUsePolicy.StrategicHpCost(
            potion.Potion,
            root.HasRenewablePotionShapedRock);
        int expectedMinimumPotionHpSaved = PotionUsePolicy.SmartRequiredHpSaved(
            expectedMinimumPotionCost,
            root.BossHpRelief);
        if (root.MinimumSearchablePotionStrategicCost != expectedMinimumPotionCost
            || !CombatSearchCoordinator.CanAnySmartPotionQualify(
                root,
                policy,
                potionFreeWon: true,
                potionFreeHpDeficit: Math.Max(0, expectedMinimumPotionHpSaved - 1))
            || !CombatSearchCoordinator.CanAnySmartPotionQualify(
                root,
                policy,
                potionFreeWon: true,
                potionFreeHpDeficit: expectedMinimumPotionHpSaved))
        {
            throw new InvalidOperationException(
                "Smart 药水补查错误地用当前血量空间排除了可能缩短战斗的路线。");
        }
        if (CombatSearchCoordinator.HasReachedProvablePrimaryQualityLowerBound(
                completeVictory: false,
                strategicHpDeficit: 0,
                combatEndedTurn: null,
                earliestPossibleCombatEndedTurn: 1)
            || CombatSearchCoordinator.HasReachedProvablePrimaryQualityLowerBound(
                completeVictory: true,
                strategicHpDeficit: 0,
                combatEndedTurn: 2,
                earliestPossibleCombatEndedTurn: 1)
            || CombatSearchCoordinator.HasReachedProvablePrimaryQualityLowerBound(
                completeVictory: true,
                strategicHpDeficit: 0,
                combatEndedTurn: 1,
                earliestPossibleCombatEndedTurn: null)
            || !CombatSearchCoordinator.HasReachedProvablePrimaryQualityLowerBound(
                completeVictory: true,
                strategicHpDeficit: 0,
                combatEndedTurn: 1,
                earliestPossibleCombatEndedTurn: 1))
        {
            throw new InvalidOperationException(
                "开局能力补查错误地把未胜、较慢的零战损结果或未知回合下界当成可停止条件。");
        }
        PrimarySearchIncumbent incumbent = new(
            StrategicHpDeficit: 5,
            CombatEndedTurn: 3);
        if (!CombatBeamSolver.ShouldPruneByPrimaryIncumbent(
                cumulativePlayerHpLost: 6,
                turn: 2,
                incumbent: incumbent)
            || !CombatBeamSolver.ShouldPruneByPrimaryIncumbent(
                cumulativePlayerHpLost: 5,
                turn: 4,
                incumbent: incumbent)
            || CombatBeamSolver.ShouldPruneByPrimaryIncumbent(
                cumulativePlayerHpLost: 5,
                turn: 3,
                incumbent: incumbent)
            // Even far past the incumbent turn, a branch below the incumbent's loss
            // remains eligible. Its current max-HP deficit is deliberately not an input:
            // later effects may recover max HP before combat ends.
            || CombatBeamSolver.ShouldPruneByPrimaryIncumbent(
                cumulativePlayerHpLost: 4,
                turn: 99,
                incumbent: incumbent))
        {
            throw new InvalidOperationException(
                "主结果下界剪枝没有严格限制为不可逆累计战损与回合字典序。");
        }
        PotionFreePolicyBaseline auditedPotionFreeBaseline = new(
            Won: true,
            HpDeficit: 5,
            PlayerHp: 75,
            CombatEndedTurn: 3);
        PrimarySearchIncumbent? dynamicIncumbent = null;
        if (CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline: null,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 5,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 0,
                maximumPotionUses: 0,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 0,
                candidateStrategicHpDeficit: 4,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 2,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 5,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: false,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 4,
                candidateCombatEndedTurn: null,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: false,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 4,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 0,
                candidateStrategicHpDeficit: 4,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 5,
                candidateCombatEndedTurn: 4,
                incumbent: ref dynamicIncumbent)
            || !CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 5,
                candidateCombatEndedTurn: 2,
                incumbent: ref dynamicIncumbent)
            || dynamicIncumbent != new PrimarySearchIncumbent(5, 2)
            || !CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 4,
                candidateCombatEndedTurn: 9,
                incumbent: ref dynamicIncumbent)
            || dynamicIncumbent != new PrimarySearchIncumbent(4, 9)
            || CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                auditedPotionFreeBaseline,
                minimumPotionUses: 1,
                maximumPotionUses: 1,
                candidateCompleteVictory: true,
                candidateSatisfiesHardRules: true,
                candidateExplicitPotionUses: 1,
                candidateStrategicHpDeficit: 5,
                candidateCombatEndedTurn: 1,
                incumbent: ref dynamicIncumbent))
        {
            throw new InvalidOperationException(
                "动态主结果下界没有限制为精确用药层中满足硬规则且严格优于无药审计的完整胜利。");
        }
        if (PotionUsePolicy.SmartRequiredHpSaved(
                SolverWeights.PotionMinimumHpSaved,
                BossHpRelief.ActClearHeal) != 45
            || CombatBeamSolver.ResolveSoldHpThreshold(
                initialPlayerMaxHp: 80,
                RoomType.Boss,
                BossHpRelief.ActClearHeal,
                theftPolicy: null) != 75)
        {
            throw new InvalidOperationException("跨幕回复没有按 80% 同步缩放药水与卖血阈值。");
        }
        if (ActEndingBossPolicy.ResolveStrategicHpRelief(
                BossHpRelief.ActClearHeal,
                BossHpStrategy.ProgressionFirst,
                BossHpStrategy.MinimizeHpLoss) != BossHpRelief.ActClearHeal
            || ActEndingBossPolicy.ResolveStrategicHpRelief(
                BossHpRelief.ActClearHeal,
                BossHpStrategy.MinimizeHpLoss,
                BossHpStrategy.ProgressionFirst) != BossHpRelief.None
            || ActEndingBossPolicy.ResolveStrategicHpRelief(
                BossHpRelief.RunEnding,
                BossHpStrategy.MinimizeHpLoss,
                BossHpStrategy.ProgressionFirst) != BossHpRelief.RunEnding
            || ActEndingBossPolicy.ResolveStrategicHpRelief(
                BossHpRelief.RunEnding,
                BossHpStrategy.ProgressionFirst,
                BossHpStrategy.MinimizeHpLoss) != BossHpRelief.None
            || PotionUsePolicy.SmartRequiredHpSaved(
                SolverWeights.PotionMinimumHpSaved,
                BossHpRelief.None) != SolverWeights.PotionMinimumHpSaved
            || CombatBeamSolver.ResolveSoldHpThreshold(
                initialPlayerMaxHp: 80,
                RoomType.Boss,
                BossHpRelief.None,
                theftPolicy: null) != SolverWeights.BossSoldHpThreshold)
        {
            throw new InvalidOperationException("两类幕末 Boss 的最低战损策略没有独立恢复正常血量权重。");
        }
        SearchablePotionSlotSnapshot[] allowedPotions = root.SearchablePotions
            .Where(potion => policy.PotionStrategy.AllowsExplicitUse(
                potion.Slot,
                potion.PotionId,
                SolverPotionPolicy.Smart,
                forceAllDisabled: false))
            .ToArray();
        BossHpRelief strategicBossHpRelief = ActEndingBossPolicy.ResolveStrategicHpRelief(
            root.BossHpRelief,
            policy.ActTransitionBossHpStrategy,
            policy.FinalBossHpStrategy);
        int paidPotionHpRequired = PotionUsePolicy.SmartRequiredHpSaved(
            SolverWeights.PotionMinimumHpSaved,
            strategicBossHpRelief);
        int freePotionCount = allowedPotions.Count(potion => potion.StrategicHpCost == 0);
        if (new[]
            {
                0,
                Math.Max(0, paidPotionHpRequired - 1),
                paidPotionHpRequired,
                paidPotionHpRequired >= int.MaxValue / 8
                    ? paidPotionHpRequired
                    : paidPotionHpRequired * 2,
            }.Any(hpDeficit => CombatSearchCoordinator.MaximumSmartPotionUses(
                root,
                policy,
                potionFreeWon: true,
                potionFreeHpDeficit: hpDeficit) != (policy.TheftPolicy == SolverTheftPolicy.PreserveResources
                    ? allowedPotions.Length
                    : Math.Min(
                        allowedPotions.Length,
                        freePotionCount + (paidPotionHpRequired >= int.MaxValue / 4
                            ? 0
                            : hpDeficit / paidPotionHpRequired)))))
        {
            throw new InvalidOperationException(
                "Smart 药水梯度没有按每瓶战略 HP 成本限制搜索层数。");
        }
        if (searchablePotions.Length >= 2)
        {
            (int Slot, PotionModel Potion) disabled = searchablePotions[0];
            SolverController.SetPotionDirectiveForTesting(
                combat,
                disabled.Slot,
                disabled.Potion.Id.Entry,
                SolverPotionDirective.Disabled);
            try
            {
                SolverSettingsSnapshot restrictedSettings = SolverSettings.Capture();
                SearchPolicySnapshot restrictedPolicy = SolverController.CaptureSearchPolicy(
                    restrictedSettings,
                    combat,
                    includeTurnSetup: false,
                    theftPolicy: SolverController.ResolveTheftPolicy(combat));
                int maximum = CombatSearchCoordinator.MaximumSmartPotionUses(
                    root,
                    restrictedPolicy,
                    potionFreeWon: false,
                    potionFreeHpDeficit: 0);
                if (maximum != searchablePotions.Length - 1)
                {
                    throw new InvalidOperationException(
                        $"禁用一瓶药后 Smart 仍会搜索过多药水层：maximum={maximum} " +
                        $"searchable={searchablePotions.Length}。");
                }
            }
            finally
            {
                SolverController.SetPotionDirectiveForTesting(
                    combat,
                    disabled.Slot,
                    disabled.Potion.Id.Entry,
                    SolverPotionDirective.Smart);
            }
        }
        if (SolverInterimResultOrdering.IsResourceTradeImprovement(
                candidateHpDeficit: 2,
                candidatePotionCost: 9,
                currentHpDeficit: 10,
                currentPotionCost: 0)
            || !SolverInterimResultOrdering.IsResourceTradeImprovement(
                candidateHpDeficit: 1,
                candidatePotionCost: 9,
                currentHpDeficit: 10,
                currentPotionCost: 0)
            || SolverInterimResultOrdering.IsResourceTradeImprovement(
                candidateHpDeficit: 10,
                candidatePotionCost: 0,
                currentHpDeficit: 10,
                currentPotionCost: 0))
        {
            throw new InvalidOperationException("搜索中间路线没有按每瓶 9 HP 成本保持严格递增优。");
        }
        SolverInterimResult noPotionTrade = new(
            Won: true,
            OutstandingStolenResource: 0,
            ProjectedBattleHpLost: 10,
            StrategicHpDeficit: 10,
            PotionStrategicCost: 0,
            ProjectedBattlePotionCount: 0,
            EnemyHp: 0,
            Score: 0d,
            CombatEndedTurn: 3);
        SolverInterimResult underpaidPotionTrade = noPotionTrade with
        {
            ProjectedBattleHpLost = 2,
            StrategicHpDeficit = 2,
            PotionStrategicCost = 9,
            ProjectedBattlePotionCount = 1,
            CombatEndedTurn = 1,
        };
        SolverInterimResult qualifiedPotionTrade = underpaidPotionTrade with
        {
            ProjectedBattleHpLost = 1,
            StrategicHpDeficit = 1,
        };
        if (SolverInterimResultOrdering.IsBetter(underpaidPotionTrade, noPotionTrade)
            || !SolverInterimResultOrdering.IsBetter(qualifiedPotionTrade, noPotionTrade)
            || CombatSearchCoordinator.IsSmartPotionGradientCandidateAcceptable(
                potionFreeWon: true,
                candidateWon: true,
                hpSaved: 8,
                hpRequired: 9,
                protectsLoot: false)
            || !CombatSearchCoordinator.IsSmartPotionGradientCandidateAcceptable(
                potionFreeWon: true,
                candidateWon: true,
                hpSaved: 9,
                hpRequired: 9,
                protectsLoot: false))
        {
            throw new InvalidOperationException("Smart 药水梯度或动态展示绕过了每瓶 9 HP 门槛。");
        }
        SolverInterimResult displayedVictory = new(
            Won: true,
            OutstandingStolenResource: 0,
            ProjectedBattleHpLost: 10,
            StrategicHpDeficit: 10,
            PotionStrategicCost: 0,
            ProjectedBattlePotionCount: 0,
            EnemyHp: 0,
            Score: 0d);
        SolverInterimResult higherDamageVictory = displayedVictory with
        {
            ProjectedBattleHpLost = 11,
            StrategicHpDeficit = 8,
        };
        SolverInterimResult lowerDamageVictory = displayedVictory with
        {
            ProjectedBattleHpLost = 9,
            StrategicHpDeficit = 9,
        };
        if (SolverInterimResultOrdering.CanPromoteDisplayedResult(
                higherDamageVictory,
                displayedVictory)
            || !SolverInterimResultOrdering.CanPromoteDisplayedResult(
                lowerDamageVictory,
                displayedVictory))
        {
            throw new InvalidOperationException("完整路线的动态预计战损没有保持单调递减。");
        }
        if (SolverInterimResultOrdering.IsCompleteVictory(
                actionCount: 1,
                allEnemiesDead: false,
                playerDead: false,
                projectedPlayerHp: 80)
            || !SolverInterimResultOrdering.IsCompleteVictory(
                actionCount: 1,
                allEnemiesDead: true,
                playerDead: false,
                projectedPlayerHp: 69))
        {
            throw new InvalidOperationException("未结束战斗的回合边界被错误发布为可采用路线。");
        }
        if (CombatBeamSolver.DescribePotionProgressPhase(
                displayNames,
                SolverPotionPolicy.Disabled,
                maximumPotionUses: 0,
                minimumPotionUses: 0,
                fixedPrefixActions: null) != "正在搜索无药路线"
            || CombatBeamSolver.DescribePotionProgressPhase(
                displayNames,
                SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 1,
                minimumPotionUses: 1,
                fixedPrefixActions: [potionAction]) != $"正在搜索使用 {potionName} 路线"
            || CombatBeamSolver.DescribePotionProgressPhase(
                displayNames,
                SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 2,
                minimumPotionUses: 2,
                fixedPrefixActions: [potionAction, potionAction])
                != $"正在搜索使用 {potionName} 和 {potionName} 路线"
            || CombatBeamSolver.DescribePotionProgressPhase(
                displayNames,
                SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 3,
                minimumPotionUses: 3,
                fixedPrefixActions: [potionAction, potionAction, potionAction])
                != $"正在搜索使用 {potionName}、{potionName} 和 {potionName} 路线"
            || CombatBeamSolver.DescribePotionProgressPhase(
                displayNames,
                SolverPotionPolicy.RequireAtLeastOne,
                maximumPotionUses: 2,
                minimumPotionUses: 2,
                fixedPrefixActions: null) != "正在搜索恰好 2 瓶药路线")
        {
            throw new InvalidOperationException("药水补查没有生成无药与任意多药阶段文案。");
        }
        BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(combat);
        List<long> elapsedSamples = [];
        bool adoptionRequested = false;
        int? displayedPotionCount = null;
        int? displayedHpLost = null;
        Stopwatch stopwatch = Stopwatch.StartNew();
        SolverResult adopted = await Task.Run(() => CombatSearchCoordinator.Solve(
            root,
            displayNames,
            battleDamage,
            policy,
            CancellationToken.None,
            progress =>
            {
                elapsedSamples.Add(progress.ElapsedMilliseconds);
                if (adoptionRequested
                    || progress.CurrentBestResult is not { } result)
                {
                    return;
                }
                displayedPotionCount = result.ProjectedBattlePotionCount;
                displayedHpLost = result.ProjectedBattleHpLost;
                adoptionRequested = interaction.RequestApplyCurrentTurn();
            }));
        stopwatch.Stop();
        if (stopwatch.ElapsedMilliseconds > 4_000)
        {
            throw new InvalidOperationException(
                $"Smart 药水补查超过单次请求预算：{stopwatch.ElapsedMilliseconds} ms。");
        }
        if (elapsedSamples.Zip(elapsedSamples.Skip(1), (left, right) => right >= left).Any(valid => !valid))
            throw new InvalidOperationException("药水补查的累计耗时发生倒退。");
        if (!adoptionRequested
            || displayedPotionCount != adopted.ProjectedBattlePotionCount
            || displayedHpLost != adopted.ProjectedBattleHpLost)
        {
            throw new InvalidOperationException("搜索中间结果没有显示用药、战损并在玩家采纳后成为最终路线。");
        }
    }

    private static void AssertBugReportAutomaticClassification()
    {
        CombatBugReportIssueLedger issues = new();
        foreach (CombatBugReportIssueKind kind in Enum.GetValues<CombatBugReportIssueKind>())
            issues.Record(kind, "分类测试");
        CombatBugReportClassificationSnapshot snapshot = new(
            StateMismatchReplans: 1,
            DeploymentDriftReplans: 2,
            ContinuationMissingReplans: 3,
            PlanExhaustedReplans: 4,
            ManualDivergenceReplans: 5,
            issues.Snapshot());
        string description = CombatBugReportDescription.AppendAutomaticClassification(
            "玩家填写的问题描述",
            snapshot);
        string[] expectedClassifications =
        [
            "玩家填写的问题描述",
            "【CombatSolver 自动分类】",
            $"CombatSolver 版本：{CombatBugReportDescription.CurrentModVersion}",
            "计划外重算：3 次（状态不一致 1，执行漂移 2）",
            "续接路线缺失后重算：3 次",
            "本回合路线耗尽后重算：4 次",
            "手操偏离原路线后重算：5 次",
            "找到更优世界线",
            "手操后预计战损上升",
            "重算后预计战损上升",
            "搜索初始化失败",
            "第三方 Mod 不兼容",
            "计算失败",
            "搜索动作回放失败",
            "搜索内存或容量错误",
            "药水策略未满足",
            "计算期间状态变化，过期结果已丢弃",
            "自动执行中止",
            "回合准备选牌失败",
            "回合准备计划与实机状态不一致",
            "未计划的选牌",
            "选牌页面执行失败",
            "遗物标注回放与选中状态不一致",
            "等待游戏状态超时",
            "存在尚未支持的战斗语义",
            "全自动因重算后战损上升而暂停",
            "全自动因预计本回合死亡而暂停",
            "全自动因结束回合实机复核将死亡而暂停",
            "全自动因结束回合实机复核战损上升而暂停",
        ];
        foreach (string expected in expectedClassifications)
        {
            if (!description.Contains(expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"在线问题描述缺少自动分类：{expected}。");
        }
    }
}
