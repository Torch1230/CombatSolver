using System.Globalization;
using Godot;

namespace CombatSolver;

internal sealed partial class SolverSettingsPanel
{
    private OptionButton _performancePreset = null!;
    private CheckButton _noGcRegionEnabled = null!;
    private LineEdit _noGcRegionBudget = null!;
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
                    EnableNoGcRegion = false,
                    NoGcRegionBudgetGigabytes = 8d,
                });
            bool migrationApplied = migrated.PerformanceMigrationVersion
                    == SolverSettings.CurrentPerformanceMigrationVersion
                && SolverSettings.ResolvePerformancePreset(migrated) == SolverPerformancePreset.Medium
                && !migrated.EnableNoGcRegion
                && migrated.NoGcRegionBudgetGigabytes == SolverSettings.DefaultNoGcRegionBudgetGigabytes;
            string legacyJson =
                "{\"performanceMigrationVersion\":" +
                SolverSettings.CurrentPerformanceMigrationVersion +
                ",\"noGcRegionBudgetGigabytes\":32}";
            SolverSettingsData legacy = SolverSettings.DeserializeForTesting(legacyJson);
            bool legacyDefaultApplied = legacy.EnableNoGcRegion
                                        && legacy.NoGcRegionBudgetGigabytes == 32d;
            SolverSettingsData preset = SolverSettings.ApplyPerformancePreset(
                original with
                {
                    EnableNoGcRegion = false,
                    NoGcRegionBudgetGigabytes = 64d,
                },
                SolverPerformancePreset.High);
            SolverSettingsData roundTripped = SolverSettings.RoundTripForTesting(preset);
            SolverSettings.ApplyForTesting(preset);
            Reload();
            return migrationApplied
                   && legacyDefaultApplied
                   && preset.NoGcRegionBudgetGigabytes == 64d
                   && !roundTripped.EnableNoGcRegion
                   && roundTripped.NoGcRegionBudgetGigabytes == 64d
                   && CommitPending()
                   && SolverSettings.ResolvePerformancePreset(SolverSettings.Current)
                   == SolverPerformancePreset.High
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
        AddBasicRow(
            budgetGrid,
            SolverText.Get("搜索并行度"),
            CreateSearchParallelismInput(),
            SolverText.Get("关闭时使用单线程搜索；2–16 是并行上限，实际并发还会受可独立分支数和内存安全准入限制，因此 CPU 不一定满载。提高可能加快大型搜索，也会增加 CPU、峰值内存和帧率压力；超过物理核心数通常只有小幅收益。默认按可用逻辑处理器选择：16 个及以上用 8 线程，4–15 个用 4 线程，2–3 个用 2 线程，其余用单线程；遇到疑似并行问题时请先上传问题包，再切换为关闭。"));
        _noGcRegionEnabled = CreateToggle();
        AddSettingsSection(content, SolverText.Get("搜索预算"),
            SolverText.Get("选择性能预设与并行度；详细参数可在下方展开。"), budgetGrid);
        GridContainer memoryGrid = CreateSettingsGrid();
        _noGcRegionEnabled.Toggled += OnNoGcRegionEnabledToggled;
        AddBasicRow(
            memoryGrid,
            SolverText.Get("启用 NoGC 区域"),
            _noGcRegionEnabled,
            SolverText.Get("开启时按下方预算建立战斗级 NoGC 区域，在安全分配检查点整理内存后继续；最终搜索完成后保留区域，战斗结束后延时清理。关闭时搜索期间使用 CLR 常规分代 GC。切换在下次搜索生效。"));
        _noGcRegionBudget = CreateRequiredDoubleInput(
            data => data.NoGcRegionBudgetGigabytes
                ?? SolverSettings.DefaultNoGcRegionBudgetGigabytes,
            (data, value) => data with { NoGcRegionBudgetGigabytes = value },
            1d,
            SolverSettings.MaximumNoGcRegionBudgetGigabytes);
        AddBasicRow(
            memoryGrid,
            SolverText.Get("搜索内存预算（GB）"),
            _noGcRegionBudget,
            SolverText.Get("这是独立于性能预设的战斗级 NoGC 区域请求上限，不是进程总内存上限，也不等于实际驻留内存。求解器会按系统当前安全余量自动下调实际区域；提高后可容纳更多并行分支并减少长搜索中的整理次数，但会增加内存占用与系统换页风险。搜索接近分配额度或系统内存安全线时，会保留活动 Beam、整理后继续；最终搜索完成后保留区域，战斗结束后延时清理。"));
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
        AddSettingsSection(content, SolverText.Get("内存管理"),
            SolverText.Get("设置搜索内存预算；手动释放入口位于主界面内存条右侧。"), memoryGrid);

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
            100_000,
            SolverText.Get("单次搜索最多展开的状态数量。提高后搜索范围更大，也会增加耗时和内存占用。"));
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
           && _noGcRegionEnabled.ButtonPressed == SolverSettings.Current.EnableNoGcRegion
           && _noGcRegionBudget.Text == SolverSettings.FormatSeconds(
               SolverSettings.Current.NoGcRegionBudgetGigabytes
               ?? SolverSettings.DefaultNoGcRegionBudgetGigabytes)
           && _noGcRegionBudget.Editable == SolverSettings.Current.EnableNoGcRegion;

    private void ReloadPerformancePage(SolverSettingsData data)
    {
        SolverPerformancePreset preset = SolverSettings.ResolvePerformancePreset(data);
        _performancePreset.Selected = _performancePreset.GetItemIndex((int)preset);
        _noGcRegionEnabled.ButtonPressed = data.EnableNoGcRegion;
        _noGcRegionBudget.Editable = data.EnableNoGcRegion;
        SetAdvancedParametersExpanded(preset == SolverPerformancePreset.Custom);
    }

    private void OnNoGcRegionEnabledToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverSettings.Update(SolverSettings.Current with { EnableNoGcRegion = enabled });
        _noGcRegionBudget.Editable = enabled;
        SetStatus(
            enabled ? SolverText.Get("NoGC 已启用，下次搜索生效") : SolverText.Get("NoGC 已关闭，下次搜索使用常规 GC"),
            SolverUiTokens.Palette.Success);
    }

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
        int maximum,
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
        int maximum)
    {
        LineEdit input = CreateInput(string.Empty);
        _reloadInputs.Add(data => input.Text = getter(data).ToString(CultureInfo.InvariantCulture));
        bool Commit()
        {
            string text = input.Text.Trim();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value < minimum || value > maximum)
            {
                ShowInvalid(input, SolverText.Format($"请输入 {minimum}–{maximum} 的整数"));
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
