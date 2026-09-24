using System.Globalization;
using Godot;

namespace CombatSolver;

internal sealed partial class SolverSettingsPanel
{
    private OptionButton _performancePreset = null!;
    private CheckButton _beamWidthPortfolioEnabled = null!;
    private CheckButton _noveltyPortfolioEnabled = null!;
    private CheckButton _noGcRegionEnabled = null!;
    private LineEdit _noGcRegionBudget = null!;
    private Label _gcStartupStatus = null!;
    private Control _advancedParameters = null!;
    private Button _advancedParametersToggle = null!;
    private bool _advancedParametersExpanded;

    internal bool ExercisePerformancePresetPersistenceForTesting()
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            SolverSettingsData migrated = SolverSettings.ApplyCurrentPerformanceMigrationForTesting(
                original with
                {
                    PerformanceMigrationVersion = 0,
                    PerformancePreset = SolverPerformancePreset.VeryHigh,
                    UseBeamWidthPortfolio = true,
                    UseNoveltyPortfolio = true,
                    ShowNoveltyPortfolioHint = false,
                    EnableNoGcRegion = false,
                    NoGcRegionBudgetGigabytes = 8d,
                });
            bool migrationApplied = migrated.PerformanceMigrationVersion
                    == SolverSettings.CurrentPerformanceMigrationVersion
                && SolverSettings.ResolvePerformancePreset(migrated) == SolverPerformancePreset.Medium
                && migrated.UseBeamWidthPortfolio
                && migrated.UseNoveltyPortfolio
                && !migrated.ShowNoveltyPortfolioHint
                && !migrated.EnableNoGcRegion
                && migrated.NoGcRegionBudgetGigabytes == SolverSettings.DefaultNoGcRegionBudgetGigabytes;
            SolverSettingsData refinementMigrated = SolverSettings.ApplyCurrentPerformanceMigrationForTesting(
                original with
                {
                    PerformanceMigrationVersion = SolverSettings.CurrentPerformanceMigrationVersion - 1,
                    PerformancePreset = SolverPerformancePreset.Custom,
                    SearchMaxExpandedNodes = 1_000_001,
                    UseBeamWidthPortfolio = false,
                    UseNoveltyPortfolio = false,
                    ShowNoveltyPortfolioHint = true,
                    EnableNoGcRegion = false,
                    NoGcRegionBudgetGigabytes = 64d,
                });
            SolverSettings.ApplyForTesting(refinementMigrated);
            bool refinementMigrationApplied = refinementMigrated.PerformanceMigrationVersion
                    == SolverSettings.CurrentPerformanceMigrationVersion
                && SolverSettings.ResolvePerformancePreset(refinementMigrated) == SolverPerformancePreset.Custom
                && SolverSettings.ResolvePerformanceValues(refinementMigrated).Profile.MaxExpandedNodes == 1_000_001
                && !refinementMigrated.UseBeamWidthPortfolio
                && !refinementMigrated.UseNoveltyPortfolio
                && refinementMigrated.ShowNoveltyPortfolioHint
                && !refinementMigrated.EnableNoGcRegion
                && refinementMigrated.NoGcRegionBudgetGigabytes == 64d;
            SolverSettingsData currentPreferences = SolverSettings.ApplyCurrentPerformanceMigrationForTesting(
                refinementMigrated with
                {
                    UseBeamWidthPortfolio = false,
                    UseNoveltyPortfolio = true,
                    ShowNoveltyPortfolioHint = false,
                });
            bool postMigrationPreferencePreserved = !currentPreferences.UseBeamWidthPortfolio
                && currentPreferences.UseNoveltyPortfolio
                && !currentPreferences.ShowNoveltyPortfolioHint;
            string legacyJson =
                "{\"performanceMigrationVersion\":" +
                SolverSettings.CurrentPerformanceMigrationVersion +
                ",\"noGcRegionBudgetGigabytes\":32}";
            SolverSettingsData legacy = SolverSettings.DeserializeForTesting(legacyJson);
            bool legacyDefaultApplied = legacy.EnableNoGcRegion
                                        && legacy.NoGcRegionBudgetGigabytes == 32d
                                        && !legacy.UseNoveltyPortfolio
                                        && legacy.ShowNoveltyPortfolioHint
                                        && legacy.ShowSpeedXWarning;
            SolverSettingsData preset = SolverSettings.ApplyPerformancePreset(
                original with
                {
                    UseBeamWidthPortfolio = true,
                    UseNoveltyPortfolio = true,
                    EnableNoGcRegion = false,
                    NoGcRegionBudgetGigabytes = 64d,
                },
                SolverPerformancePreset.High);
            SolverSettingsData roundTripped = SolverSettings.RoundTripForTesting(preset);
            SolverSettings.ApplyForTesting(preset);
            Reload();
            return migrationApplied
                   && refinementMigrationApplied
                   && postMigrationPreferencePreserved
                   && legacyDefaultApplied
                   && preset.NoGcRegionBudgetGigabytes == 64d
                   && roundTripped.UseBeamWidthPortfolio
                   && roundTripped.UseNoveltyPortfolio
                   && !roundTripped.EnableNoGcRegion
                   && roundTripped.NoGcRegionBudgetGigabytes == 64d
                   && CommitPending()
                   && SolverSettings.ResolvePerformancePreset(SolverSettings.Current)
                   == SolverPerformancePreset.High
                   && SolverSettings.Current.UseBeamWidthPortfolio
                   && _beamWidthPortfolioEnabled.ButtonPressed
                   && SolverSettings.Current.UseNoveltyPortfolio
                   && _noveltyPortfolioEnabled.ButtonPressed
                   && !SolverSettings.Current.EnableNoGcRegion
                   && SolverSettings.Current.NoGcRegionBudgetGigabytes == 64d
                   && !_noGcRegionBudget.Editable;
        }
        finally
        {
            SolverSettings.ApplyForTesting(original);
            Reload();
        }
    }

    private Control CreatePerformancePage()
    {
        VBoxContainer content = CreatePageContent("PerformanceSettingsPage");
        GridContainer budgetGrid = CreateSettingsGrid();
        _performancePreset = CreatePerformancePresetInput();
        AddBasicRow(budgetGrid, SolverText.Get("性能预设"), _performancePreset);
        _beamWidthPortfolioEnabled = CreateToggle();
        _reloadInputs.Add(data =>
            _beamWidthPortfolioEnabled.ButtonPressed = data.UseBeamWidthPortfolio);
        _beamWidthPortfolioEnabled.Toggled += enabled =>
        {
            if (_loading)
                return;
            SolverSettings.Update(SolverSettings.Current with { UseBeamWidthPortfolio = enabled });
            SetStatus(
                enabled
                    ? SolverText.Get("多宽度路线精炼已启用，下次搜索生效")
                    : SolverText.Get("多宽度路线精炼已关闭"),
                SolverUiTokens.Palette.Success);
        };
        AddBasicRow(
            budgetGrid,
            SolverText.Get("多宽度路线精炼（实验）"),
            _beamWidthPortfolioEnabled,
            SolverText.Get("先按当前性能预设正常搜索。首轮较快完成、路线仍有改善空间且剩余时间、节点和内存充足时，再尝试几种不同的搜索方式并选择更优路线。可能提高路线质量，也会增加耗时和内存占用；不会突破当前设置的时间和节点上限。"));
        _noveltyPortfolioEnabled = CreateToggle();
        _reloadInputs.Add(data => _noveltyPortfolioEnabled.ButtonPressed = data.UseNoveltyPortfolio);
        _noveltyPortfolioEnabled.Toggled += enabled =>
        {
            if (_loading) return;
            SolverSettings.Update(SolverSettings.Current with
            {
                UseNoveltyPortfolio = enabled,
                ShowNoveltyPortfolioHint = enabled
                    ? false
                    : SolverSettings.Current.ShowNoveltyPortfolioHint,
            });
            SolverOverlay.RefreshGuidanceHints();
            SetStatus(SolverText.Get(enabled
                ? "多策略路线搜索已启用，下次搜索生效"
                : "多策略路线搜索已关闭"), SolverUiTokens.Palette.Success);
        };
        AddBasicRow(budgetGrid, SolverText.Get("多策略路线搜索（实验）"), _noveltyPortfolioEnabled,
            SolverText.Get("先用部分预算尝试不同路线，再用剩余预算进行常规搜索，并按当前战损、成长和药水规则选优。可能更快找到好路线，也可能因预算分配而改变结果。与常规搜索共用时间和节点上限；下次搜索生效。"));
        AddBasicRow(
            budgetGrid,
            SolverText.Get("搜索并行度"),
            CreateSearchParallelismInput(),
            SolverText.Get("关闭时使用单线程搜索；2–16 是并行上限，实际并发还会受可独立分支数和内存安全准入限制，因此 CPU 不一定满载。提高可能加快大型搜索，也会增加 CPU、峰值内存和帧率压力；超过物理核心数通常只有小幅收益。默认按可用逻辑处理器选择：16 个及以上用 8 线程，4–15 个用 4 线程，2–3 个用 2 线程，其余用单线程；遇到疑似并行问题时请先上传问题包，再切换为关闭。"));
        _noGcRegionEnabled = CreateToggle();
        _noGcRegionEnabled.Disabled = !SearchGcPolicy.NoGcRegionSupported
            || RuntimeGcProfile.Current.IsActive;
        AddSettingsSection(content, SolverText.Get("搜索预算"),
            SolverText.Get("选择性能预设与并行度；详细参数可在下方展开。"), budgetGrid);
        GridContainer memoryGrid = CreateSettingsGrid();
        CheckButton automaticGc = CreateToggle();
        _reloadInputs.Add(data => automaticGc.ButtonPressed = data.AutoConfigureServerGc);
        _reloadInputs.Add(_ => _gcStartupStatus.Text = DescribeGcStartup());
        automaticGc.Toggled += enabled =>
        {
            if (_loading) return;
            SolverSettings.Update(SolverSettings.Current with { AutoConfigureServerGc = enabled });
            RuntimeGcStartup.Prepare(enabled);
            _gcStartupStatus.Text = DescribeGcStartup();
            SetStatus(DescribeGcStartup(), RuntimeGcStartup.Status == "Failed"
                ? SolverUiTokens.Palette.TextMuted : SolverUiTokens.Palette.Success);
        };
        AddBasicRow(memoryGrid, SolverText.Get("多核内存回收（推荐，重启生效）"), automaticGc,
            SolverText.Get("【推荐开启】开启后，整个游戏将改用多核并发垃圾回收（Server GC）。\n· 优势：内存占用大幅降低约 70%，并显著加快复杂战斗的搜索速度，有效避免高内存占用与换页卡顿。\n· 注意：因回收模式由游戏启动时决定，开启或关闭均需重启游戏生效；直接卸载 Mod 不会自动还原配置，建议在卸载前先在此关闭。\n· 生效期间，后台会自动多核回收，下方旧版的“搜索时暂缓回收”将自动停用。"));
        _gcStartupStatus = SolverUiTokens.CreateLabel(DescribeGcStartup(),
            SolverUiTokens.Type.Caption, SolverUiTokens.Palette.TextMuted);
        _gcStartupStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        memoryGrid.AddChild(_gcStartupStatus);
        memoryGrid.AddChild(new Control());
        _noGcRegionEnabled.Toggled += OnNoGcRegionEnabledToggled;
        AddBasicRow(
            memoryGrid,
            SolverText.Get("搜索时暂缓内存回收（备用）"),
            _noGcRegionEnabled,
            SolverText.Get("【备用方案】仅在未启用“多核内存回收”时生效。\n开启后，求解器会在战斗搜索期间向系统申请大内存并暂缓垃圾回收，以减少单核回收引起的搜索微卡顿，但这会占用大量内存（甚至数 GB 到十几 GB）。达到额度上限时仍会自动整理。修改从下次搜索生效。"));
        if (RuntimeGcProfile.Current.IsActive)
            _noGcRegionEnabled.TooltipText = DescribeRuntimeGcProfile();
        else if (!SearchGcPolicy.NoGcRegionSupported)
            _noGcRegionEnabled.TooltipText = SolverText.Get("当前设备不支持暂缓内存回收；已保存的选择保留，搜索照常回收。");
        _noGcRegionBudget = CreateRequiredDoubleInput(
            data => data.NoGcRegionBudgetGigabytes
                ?? SolverSettings.DefaultNoGcRegionBudgetGigabytes,
            (data, value) => data with { NoGcRegionBudgetGigabytes = value },
            1d,
            SolverSettings.MaximumNoGcRegionBudgetGigabytes);
        AddBasicRow(
            memoryGrid,
            SolverText.Get("暂缓回收的额度上限（GB）"),
            _noGcRegionBudget,
            SolverText.Get("仅在“搜索时暂缓内存回收”启用时生效。指定求解器在搜索期间暂缓回收的内存配额上限（默认 16 GB），并非游戏的常驻内存或整机内存上限。系统可用内存不足时会自动下调。调高可减少长时间搜索中的整理停顿，但会大幅增加内存占用；多核内存回收生效时此项不生效。"));
        GridContainer stopGrid = CreateSettingsGrid();
        _acceptableBattleHpLoss = CreateAcceptableBattleHpLossInput();
        CheckButton stopAtHpTarget = CreateToggle();
        _reloadInputs.Add(data => stopAtHpTarget.ButtonPressed = data.StopAtAcceptableBattleHpLoss);
        stopAtHpTarget.Toggled += enabled =>
        {
            if (_loading) return;
            SolverSettings.Update(SolverSettings.Current with { StopAtAcceptableBattleHpLoss = enabled });
            SetStatus(SolverText.Get("已保存，下次搜索生效"), SolverUiTokens.Palette.Success);
        };
        AddBasicRow(stopGrid, SolverText.Get("达到战损目标后停止搜索"), stopAtHpTarget,
            SolverText.Get("默认开启。完整胜利达到战损阈值且没有多用药水时停止；0 表示零损。成长收益尚未满足时继续搜索，击杀成长牌兑现收益后可停止。下次搜索生效。"));
        AddBasicRow(stopGrid, SolverText.Get("提前结束搜索的战损阈值（HP）"), _acceptableBattleHpLoss,
            SolverText.Get("默认 0，即零损。启用上方开关后，找到预计整场扣血不超过此值的完整胜利路线就停止搜索；仅保存成长额度而本场没有对应卡牌时仍可早停。"));
        AddSettingsSection(content, SolverText.Get("搜索停止条件"),
            SolverText.Get("战损阈值按整场累计扣血计算，下次搜索生效。"), stopGrid);
        string memoryDescription = SolverText.Get("推荐使用多核内存回收（默认开启）：大幅降低内存占用并加速复杂搜索。若未生效，则备用下方的暂缓回收策略。主界面内存条右侧可随时手动释放内存。");
        AddSettingsSection(content, SolverText.Get("内存管理"), memoryDescription, memoryGrid);

        _advancedParametersToggle = SolverUiTokens.CreateButton(
            SolverText.Get("展开自定义参数"),
            SolverButtonStyle.Secondary);
        _advancedParametersToggle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _advancedParametersToggle.Pressed += ToggleAdvancedParameters;
        content.AddChild(_advancedParametersToggle);

        VBoxContainer advanced = CreatePageContent("AdvancedSearchParameters");
        advanced.AddChild(CreateSectionHeading(SolverText.Get("自定义搜索参数")));
        GridContainer searchGrid = new()
        {
            Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass,
        };
        searchGrid.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Md);
        searchGrid.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Sm);
        AddGridHeader(searchGrid, SolverText.Get("配置项"));
        AddGridHeader(searchGrid, SolverText.Get("搜索预算"));
        AddDoubleRow(
            searchGrid,
            SolverText.Get("时间上限（秒）"),
            data => SolverSettings.ResolvePerformanceValues(data).Profile.SoftTimeBudgetMilliseconds / 1000d,
            (data, value) => AsCustomPerformance(data with { SearchTimeLimitSeconds = value }),
            0.1d,
            600d,
            SolverText.Get("搜索使用一套时间预算，期间持续更新当前最好路线；达到停止条件时提前结束。"));
        AddIntRow(
            searchGrid,
            SolverText.Get("Beam 宽度"),
            data => SolverSettings.ResolvePerformanceValues(data).Profile.BeamWidth,
            (data, value) => AsCustomPerformance(data with { SearchBeamWidth = value }),
            1,
            512,
            SolverText.Get("每层保留的候选路线数量。提高后更不容易过早淘汰好路线，但会明显增加计算量和内存占用。"));
        AddIntRow(
            searchGrid,
            SolverText.Get("节点上限"),
            data => SolverSettings.ResolvePerformanceValues(data).Profile.MaxExpandedNodes,
            (data, value) => AsCustomPerformance(data with { SearchMaxExpandedNodes = value }),
            100,
            null,
            SolverText.Get("单次搜索最多展开的状态数量。自定义数值不设额外上限；提高后搜索范围更大，也会增加耗时和内存占用。"));
        AddIntRow(
            searchGrid,
            SolverText.Get("单节点出牌分支"),
            data => SolverSettings.ResolvePerformanceValues(data).Profile.MaxCardBranchesPerNode,
            (data, value) => AsCustomPerformance(data with { SearchMaxCardBranchesPerNode = value }),
            1,
            100,
            SolverText.Get("每个状态最多继续尝试的出牌动作数量。提高后能覆盖更多出牌顺序，但会放大后续搜索量。"));
        advanced.AddChild(searchGrid);
        Label hint = SolverUiTokens.CreateLabel(
            SolverText.Get("修改任一数值后，性能预设会切换为自定义。"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextMuted);
        advanced.AddChild(hint);
        _advancedParameters = advanced;
        content.AddChild(_advancedParameters);
        return CreatePageScroll(content);
    }

    internal bool NoGcControlsConfiguredForTesting
        => _performancePage.IsAncestorOf(_noGcRegionEnabled)
           && _performancePage.IsAncestorOf(_noGcRegionBudget)
           && _noGcRegionEnabled.ButtonPressed == RuntimeGcProfile.Current.ResolveEnableNoGcRegion(
               SolverSettings.Current.EnableNoGcRegion)
           && _noGcRegionBudget.Text == SolverSettings.FormatSeconds(
               SolverSettings.Current.NoGcRegionBudgetGigabytes
               ?? SolverSettings.DefaultNoGcRegionBudgetGigabytes)
           && _noGcRegionEnabled.Disabled ==
               (!SearchGcPolicy.NoGcRegionSupported || RuntimeGcProfile.Current.IsActive)
           && _noGcRegionBudget.Editable ==
               (RuntimeGcProfile.Current.ResolveEnableNoGcRegion(SolverSettings.Current.EnableNoGcRegion)
                && SearchGcPolicy.NoGcRegionSupported);

    internal bool BeamWidthPortfolioControlConfiguredForTesting
        => _performancePage.IsAncestorOf(_beamWidthPortfolioEnabled)
           && _beamWidthPortfolioEnabled.ButtonPressed == SolverSettings.Current.UseBeamWidthPortfolio
           && _performancePage.IsAncestorOf(_noveltyPortfolioEnabled)
           && _noveltyPortfolioEnabled.ButtonPressed == SolverSettings.Current.UseNoveltyPortfolio;

    private void ReloadPerformancePage(SolverSettingsData data)
    {
        SolverPerformancePreset preset = SolverSettings.ResolvePerformancePreset(data);
        _performancePreset.Selected = _performancePreset.GetItemIndex((int)preset);
        _beamWidthPortfolioEnabled.ButtonPressed = data.UseBeamWidthPortfolio;
        _noveltyPortfolioEnabled.ButtonPressed = data.UseNoveltyPortfolio;
        bool effectiveNoGc = RuntimeGcProfile.Current.ResolveEnableNoGcRegion(data.EnableNoGcRegion);
        _noGcRegionEnabled.ButtonPressed = effectiveNoGc;
        _noGcRegionBudget.Editable = effectiveNoGc && SearchGcPolicy.NoGcRegionSupported;
        SetAdvancedParametersExpanded(preset == SolverPerformancePreset.Custom);
    }

    private void OnNoGcRegionEnabledToggled(bool enabled)
    {
        if (_loading || RuntimeGcProfile.Current.IsActive)
            return;
        SolverSettings.Update(SolverSettings.Current with { EnableNoGcRegion = enabled });
        _noGcRegionBudget.Editable = enabled && SearchGcPolicy.NoGcRegionSupported;
        SetStatus(
            enabled ? SolverText.Get("已启用暂缓内存回收，下次搜索生效") : SolverText.Get("已关闭暂缓内存回收，下次搜索生效"),
            SolverUiTokens.Palette.Success);
    }

    private static string DescribeGcStartup()
    {
        if (RuntimeGcProfile.Current.IsActive)
        {
            return RuntimeGcStartup.Status switch
            {
                "AlreadyConfigured" or "Prepared" => SolverText.Get("当前状态：多核回收生效中（下次启动保持生效）。"),
                "Restored" => SolverText.Get("已恢复原有启动配置；本次游戏仍维持多核回收，重启游戏后生效。"),
                "Skipped" => SolverText.Get("当前由专用启动方式或测试环境管理，未修改游戏启动配置。"),
                _ => SolverText.Get("未能修改游戏启动配置，详情见日志；本次继续维持多核回收。"),
            };
        }

        return RuntimeGcStartup.Status switch
        {
            "Prepared" => SolverText.Get("配置已就绪：本次游戏尚未生效，请重启游戏以启用多核回收。"),
            "AlreadyConfigured" => SolverText.Get("已配置多核回收，但本次启动未生效；重启游戏后生效。"),
            "Restored" => SolverText.Get("已恢复原有启动配置，重启游戏后生效。本次使用备用回收。"),
            "Unchanged" => SolverText.Get("未启用多核回收；当前使用备用的暂缓回收策略。"),
            "Skipped" => SolverText.Get("当前由专用启动方式或测试环境管理，未修改游戏启动配置。"),
            _ => SolverText.Get("未能修改游戏启动配置，详情见日志；本次继续使用备用回收。"),
        };
    }

    private static string DescribeRuntimeGcProfile()
        => RuntimeGcProfile.Current.Status switch
        {
            RuntimeGcProfileStatus.Active => SolverText.Get(
                "当前正在使用更优的“多核内存回收”，后台会自动并发清理内存，因此无需且已停用“暂缓回收”。如需改用此功能，请关闭上方开关并重启游戏。"),
            RuntimeGcProfileStatus.ServerGcUnavailable => SolverText.Get(
                "运行库未能启用多核内存回收，搜索按已保存的暂缓回收设置运行。"),
            RuntimeGcProfileStatus.UnknownProfile => SolverText.Get(
                "未知的内存回收配置，搜索按已保存的暂缓回收设置运行。"),
            _ => SolverText.Get("本次未启用多核内存回收，搜索按下方暂缓回收设置运行。"),
        };

    private OptionButton CreatePerformancePresetInput()
    {
        OptionButton input = CreateOptionInput(260);
        input.AddItem(SolverText.Get("低档（60 秒）"), (int)SolverPerformancePreset.Low);
        input.AddItem(SolverText.Get("中档（默认，120 秒）"), (int)SolverPerformancePreset.Medium);
        input.AddItem(SolverText.Get("高档（180 秒）"), (int)SolverPerformancePreset.High);
        input.AddItem(SolverText.Get("极高（300 秒）"), (int)SolverPerformancePreset.VeryHigh);
        input.AddItem(SolverText.Get("自定义"), (int)SolverPerformancePreset.Custom);
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            SolverPerformancePreset preset = (SolverPerformancePreset)input.GetItemId((int)index);
            SolverSettings.Update(SolverSettings.ApplyPerformancePreset(SolverSettings.Current, preset));
            Reload();
            SetStatus(SolverText.Get("性能预设已保存，下次搜索生效"), SolverUiTokens.Palette.Success);
        };
        return input;
    }

    private OptionButton CreateSearchParallelismInput()
    {
        OptionButton input = CreateOptionInput();
        input.AddItem(SolverText.Get("关闭（单线程）"), 1);
        for (int degree = 2; degree <= SolverWeights.MaximumSearchMaxDegreeOfParallelism; degree++)
            input.AddItem(degree.ToString(CultureInfo.InvariantCulture), degree);
        _reloadInputs.Add(data =>
        {
            int degree = data.SearchMaxDegreeOfParallelism
                ?? SolverWeights.DefaultSearchMaxDegreeOfParallelism;
            input.Selected = input.GetItemIndex(degree);
        });
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            int degree = input.GetItemId((int)index);
            SolverSettings.Update(SolverSettings.Current with
            {
                SearchMaxDegreeOfParallelism = degree,
            });
            SetStatus(
                degree == 1
                    ? SolverText.Get("并行搜索已关闭，下次搜索使用单线程")
                    : SolverText.Format($"搜索并行度已设为 {degree}，下次搜索生效"),
                SolverUiTokens.Palette.Success);
        };
        return input;
    }

    private void AddIntRow(
        GridContainer grid,
        string label,
        Func<SolverSettingsData, int> getDeep,
        Func<SolverSettingsData, int, SolverSettingsData> setDeep,
        int minimum,
        int? maximum,
        string tooltip)
    {
        Label rowLabel = CreateRowLabel(label);
        LineEdit deepInput = CreateRequiredIntInput(getDeep, setDeep, minimum, maximum);
        ApplyTooltip(rowLabel, tooltip);
        ApplyTooltip(deepInput, tooltip);
        grid.AddChild(rowLabel);
        grid.AddChild(deepInput);
    }

    private void AddDoubleRow(
        GridContainer grid,
        string label,
        Func<SolverSettingsData, double> getDeep,
        Func<SolverSettingsData, double, SolverSettingsData> setDeep,
        double minimum,
        double maximum,
        string tooltip)
    {
        Label rowLabel = CreateRowLabel(label);
        LineEdit deepInput = CreateRequiredDoubleInput(getDeep, setDeep, minimum, maximum);
        ApplyTooltip(rowLabel, tooltip);
        ApplyTooltip(deepInput, tooltip);
        grid.AddChild(rowLabel);
        grid.AddChild(deepInput);
    }

    private LineEdit CreateRequiredIntInput(
        Func<SolverSettingsData, int> getter,
        Func<SolverSettingsData, int, SolverSettingsData> setter,
        int minimum,
        int? maximum)
    {
        LineEdit input = CreateInput(string.Empty);
        _reloadInputs.Add(data => input.Text = getter(data).ToString(CultureInfo.InvariantCulture));
        bool Commit()
        {
            string text = input.Text.Trim();
            bool parsed = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value);
            bool aboveMaximum = maximum is { } configuredMaximum && value > configuredMaximum;
            if (!parsed || value < minimum || aboveMaximum)
            {
                ShowInvalid(input, maximum.HasValue
                    ? SolverText.Format($"请输入 {minimum}–{maximum.Value} 的整数")
                    : SolverText.Format($"请输入不小于 {minimum} 的整数"));
                return false;
            }
            if (getter(SolverSettings.Current) == value)
                return KeepUnchanged(input);
            return SavePerformanceInput(input, setter(SolverSettings.Current, value));
        }
        input.FocusExited += () => Commit();
        input.TextSubmitted += _ => Commit();
        _commitInputs.Add(Commit);
        return input;
    }

    private LineEdit CreateRequiredDoubleInput(
        Func<SolverSettingsData, double> getter,
        Func<SolverSettingsData, double, SolverSettingsData> setter,
        double minimum,
        double maximum)
    {
        LineEdit input = CreateInput(string.Empty);
        _reloadInputs.Add(data => input.Text = SolverSettings.FormatSeconds(getter(data)));
        bool Commit()
        {
            string text = input.Text.Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || value < minimum || value > maximum)
            {
                ShowInvalid(input, SolverText.Format($"请输入 {minimum:0.###}–{maximum:0.###} 的数字"));
                return false;
            }
            if (getter(SolverSettings.Current).Equals(value))
                return KeepUnchanged(input);
            return SavePerformanceInput(input, setter(SolverSettings.Current, value));
        }
        input.FocusExited += () => Commit();
        input.TextSubmitted += _ => Commit();
        _commitInputs.Add(Commit);
        return input;
    }

    private bool SavePerformanceInput(LineEdit input, SolverSettingsData data)
    {
        if (_loading)
            return true;
        if (data != SolverSettings.Current)
            SolverSettings.Update(data);
        SolverPerformancePreset preset = SolverSettings.ResolvePerformancePreset(data);
        _performancePreset.Selected = _performancePreset.GetItemIndex((int)preset);
        SetAdvancedParametersExpanded(preset == SolverPerformancePreset.Custom);
        input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        SetStatus(SolverText.Get("已保存，下次搜索生效"), SolverUiTokens.Palette.Success);
        return true;
    }

    private void ToggleAdvancedParameters()
        => SetAdvancedParametersExpanded(!_advancedParametersExpanded);

    private void SetAdvancedParametersExpanded(bool expanded)
    {
        _advancedParametersExpanded = expanded;
        _advancedParameters.Visible = expanded;
        _advancedParametersToggle.Text = expanded ? SolverText.Get("收起自定义参数") : SolverText.Get("展开自定义参数");
    }

    private static SolverSettingsData AsCustomPerformance(SolverSettingsData data)
        => data with { PerformancePreset = SolverPerformancePreset.Custom };
}
