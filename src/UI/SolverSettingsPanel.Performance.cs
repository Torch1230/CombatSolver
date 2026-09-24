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
        automaticGc.Toggled += enabled =>
        {
            if (_loading) return;
            SolverSettings.Update(SolverSettings.Current with { AutoConfigureServerGc = enabled });
            RuntimeGcStartup.Prepare(enabled);
            _gcStartupStatus.Text = DescribeGcStartup();
            SetStatus(DescribeGcStartup(), RuntimeGcStartup.Status == "Failed"
                ? SolverUiTokens.Palette.TextMuted : SolverUiTokens.Palette.Success);
        };
        AddBasicRow(memoryGrid, SolverText.Get("使用多核内存回收（重启生效）"), automaticGc,
            SolverText.Get("默认开启。首次启用时只准备游戏设置；退出并再次从 Steam 启动后，整个游戏才会改用多核内存回收。它可能降低搜索内存占用，也可能增加处理器负担、让部分搜索变慢或改变路线，并会影响其他 Mod。关闭后恢复修改前的启动设置，重启生效。卸载前请先在此关闭；直接卸载不会恢复。"));
        _gcStartupStatus = SolverUiTokens.CreateLabel(DescribeGcStartup(),
            SolverUiTokens.Type.Caption, SolverUiTokens.Palette.TextMuted);
        _gcStartupStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        memoryGrid.AddChild(_gcStartupStatus);
        memoryGrid.AddChild(new Control());
        _noGcRegionEnabled.Toggled += OnNoGcRegionEnabledToggled;
        AddBasicRow(
            memoryGrid,
            SolverText.Get("搜索时暂缓内存回收"),
            _noGcRegionEnabled,
            SolverText.Get("本次启动未启用上方方式时，此项才可使用。开启后，求解器会在战斗中暂缓自动内存回收，尽量减少搜索中的停顿，但可能占用更多内存；接近下方额度时仍会在安全时机整理。关闭后搜索照常回收。修改从下次搜索生效。"));
        if (RuntimeGcProfile.Current.IsActive)
            _noGcRegionEnabled.TooltipText = DescribeRuntimeGcProfile();
        else if (!SearchGcPolicy.NoGcRegionSupported)
            _noGcRegionEnabled.TooltipText = SolverText.Get("这台设备无法暂缓内存回收；已保存的选择保留，搜索照常回收。");
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
            SolverText.Get("只用于“搜索时暂缓内存回收”。这是求解器在整理前尝试使用的分配额度，不是游戏内存上限，也不是已占用内存。系统内存紧张时会自动下调；调高可能减少搜索中的整理次数，也可能增加内存占用。"));
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
        string memoryDescription = SolverText.Get("多核回收生效时，搜索不会再暂缓回收；额度只供暂缓回收使用。手动释放入口在主界面内存条右侧。");
        string runtimeProfileDescription = DescribeRuntimeGcProfile();
        if (runtimeProfileDescription.Length > 0)
            memoryDescription += "\n" + runtimeProfileDescription;
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
            enabled ? SolverText.Get("下次搜索将尝试暂缓内存回收") : SolverText.Get("下次搜索将照常回收内存"),
            SolverUiTokens.Palette.Success);
    }

    private static string DescribeGcStartup()
        => RuntimeGcStartup.Status switch
        {
            "Prepared" => SolverText.Get("已为下次启动准备好设置；本次游戏不会切换回收方式。"),
            "AlreadyConfigured" => SolverText.Get("启动设置已就绪；本次是否生效见下方状态。"),
            "Restored" => SolverText.Get("已恢复修改前的启动设置；重启后生效，本次不会切换。"),
            "Unchanged" => SolverText.Get("未改动游戏的启动设置。"),
            "Skipped" => SolverText.Get("本次由专用启动方式或测试环境管理，未改动游戏的启动设置。"),
            _ => SolverText.Get("未能修改游戏的启动设置；本次继续原方式，详情见日志。"),
        };

    private static string DescribeRuntimeGcProfile()
        => RuntimeGcProfile.Current.Status switch
        {
            RuntimeGcProfileStatus.Active => SolverText.Get(
                "本次游戏正在使用多核内存回收；搜索时暂缓回收已停用，原选择仍保留。关闭上方设置并重启后可恢复。"),
            RuntimeGcProfileStatus.ServerGcUnavailable => SolverText.Get(
                "本次启动未能使用多核内存回收；搜索按已保存的暂缓回收设置运行。"),
            RuntimeGcProfileStatus.UnknownProfile => SolverText.Get(
                "无法识别本次启动的回收方式；搜索按已保存的暂缓回收设置运行。"),
            _ => SolverText.Get("本次未启用上方新方式；搜索按下方设置运行。"),
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
