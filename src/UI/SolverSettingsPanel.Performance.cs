using System.Globalization;
using Godot;

namespace CombatSolver;

internal sealed partial class SolverSettingsPanel
{
    private SpinBox _maxCombatSolveSeconds = null!;
    private OptionButton _searchParallelism = null!;
    private CheckButton _useRustCombatKernel = null!;

    internal bool ExercisePerformancePresetPersistenceForTesting()
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            const string legacyJson =
                """
                {
                  "performanceMigrationVersion": 246,
                  "performancePreset": "VeryHigh",
                  "searchStrengthPreset": "VeryHigh",
                  "beamWidth": 320,
                  "maxExpandedNodes": 800000,
                  "softTimeBudgetSeconds": 300,
                  "showMemoryMonitor": true,
                  "showBattleDamagePerformanceHint": true,
                  "searchTimeLimitSeconds": 300,
                  "searchBeamWidth": 135,
                  "searchMaxExpandedNodes": 500000,
                  "acceptableBattleHpLoss": 11,
                  "overlayTheme": "Light"
                }
                """;
            SolverSettingsData legacyLoaded = SolverSettings.DeserializeForTesting(legacyJson);
            SolverSettingsData migrated = SolverSettings.ApplyCurrentPerformanceMigrationForTesting(legacyLoaded);
            SolverSettings.ApplyForTesting(migrated);
            bool migrationApplied = migrated.PerformanceMigrationVersion
                    == SolverSettings.CurrentPerformanceMigrationVersion
                && migrated.MaxCombatSolveSeconds == SolverSettings.DefaultMaxCombatSolveSeconds
                && migrated.PerformancePreset == null
                && migrated.SearchBeamWidth == null
                && migrated.SearchMaxExpandedNodes == null
                && migrated.SearchTimeLimitSeconds == null
                && !migrated.ShowBattleDamagePerformanceHint
                && migrated.AcceptableBattleHpLoss == 11
                && migrated.OverlayTheme == SolverOverlayTheme.Light
                && migrated.ShowSpeedXWarning;

            SolverSettingsData customSolve = migrated with
            {
                MaxCombatSolveSeconds = 9,
                SearchMaxDegreeOfParallelism = null,
                UseRustCombatKernel = false,
            };
            SolverSettingsData roundTripped = SolverSettings.RoundTripForTesting(customSolve);
            SolverSettings.ApplyForTesting(customSolve);
            Reload();
            bool controlsSynced = (int)Math.Round(_maxCombatSolveSeconds.Value) == 9
                && _searchParallelism.GetItemId(_searchParallelism.Selected) == 0
                && !roundTripped.UseRustCombatKernel
                && !_useRustCombatKernel.ButtonPressed
                && roundTripped.MaxCombatSolveSeconds == 9
                && roundTripped.SearchMaxDegreeOfParallelism == null
                && CommitPending()
                && !SolverSettings.Current.UseRustCombatKernel
                && !SolverSettings.Capture().UseRustCombatKernel
                && SolverSettings.Current.MaxCombatSolveSeconds == 9
                && SolverSettings.Capture().MaxCombatSolveSeconds == 9;

            SolverSettingsData harnessCompat = SolverSettings.ApplyPerformancePreset(
                original,
                SolverPerformancePreset.High);
            bool harnessOk = SolverSettings.ResolvePerformanceValues(harnessCompat).Profile.BeamWidth > 0;
            return migrationApplied && controlsSynced && harnessOk;
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
        GridContainer performanceGrid = CreateSettingsGrid();
        _maxCombatSolveSeconds = CreateMaxCombatSolveSecondsInput();
        AddBasicRow(
            performanceGrid,
            SolverText.Get("战斗最大求解时长（秒）"),
            _maxCombatSolveSeconds,
            SolverText.Get("默认 30 秒（范围 1–600 秒）。简单战斗通常瞬间完成；复杂战斗自动逐级深化，最长不超过此设定。下次搜索生效。"));
        _searchParallelism = CreateSearchParallelismInput();
        AddBasicRow(
            performanceGrid,
            SolverText.Get("最大并发线程数"),
            _searchParallelism,
            SolverText.Get("默认选择自动，按可用逻辑处理器决定并发上限（16 核及以上 8 线程、4–15 核 4 线程、2–3 核 2 线程，其余单线程）。关闭时使用单线程；2–16 为并行上限。遇到疑似并行问题时请先上传问题包，再切换为关闭。"));
        _useRustCombatKernel = CreateToggle();
        _useRustCombatKernel.Toggled += enabled =>
        {
            if (_loading)
                return;
            SolverSettings.Update(SolverSettings.Current with { UseRustCombatKernel = enabled });
            SolverOverlay.RefreshControls();
            SetStatus(
                enabled
                    ? SolverText.Get("战斗内优先使用 Rust 内核已启用，下次搜索生效")
                    : SolverText.Get("已关闭战斗内 Rust 内核，将使用 HotKernel 引擎"),
                SolverUiTokens.Palette.Success);
        };
        AddBasicRow(
            performanceGrid,
            SolverText.Get("战斗内优先使用 Rust 内核"),
            _useRustCombatKernel,
            SolverText.Get("开启后，单人原版战斗（含标准、恐惧与极速模式）会优先使用原生 Rust 内核并行求解，并在采纳前由 HotKernel 逐动作回放验证；遇到不支持的 Mod、策略或异常时自动回退至 HotKernel。"));
        AddSettingsSection(
            content,
            SolverText.Get("搜索性能"),
            SolverText.Get("简单战斗通常瞬间完成；遇到复杂或高战损战斗时自动逐级深化，最长不超过设定求解时长。"),
            performanceGrid);
        return CreatePageScroll(content);
    }

    private void ReloadPerformancePage(SolverSettingsData data)
    {
        _maxCombatSolveSeconds.SetValueNoSignal(data.MaxCombatSolveSeconds);
        int parallelismId = data.SearchMaxDegreeOfParallelism ?? 0;
        _searchParallelism.Selected = _searchParallelism.GetItemIndex(parallelismId);
        _useRustCombatKernel.ButtonPressed = data.UseRustCombatKernel;
    }

    private SpinBox CreateMaxCombatSolveSecondsInput()
    {
        SpinBox input = new()
        {
            Name = "MaxCombatSolveSecondsInput",
            MinValue = SolverSettings.MinimumMaxCombatSolveSeconds,
            MaxValue = SolverSettings.MaximumMaxCombatSolveSeconds,
            Step = 1d,
            Rounded = true,
            CustomMinimumSize = new Vector2(140, 34),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Suffix = " s",
            UpdateOnTextChanged = false,
        };
        StyleSpinBox(input);
        LineEdit lineEdit = input.GetLineEdit();
        lineEdit.FocusExited += input.Apply;
        lineEdit.TextSubmitted += _ => input.Apply();
        input.ValueChanged += value =>
        {
            if (_loading)
                return;
            int seconds = Math.Clamp(
                (int)Math.Round(value),
                SolverSettings.MinimumMaxCombatSolveSeconds,
                SolverSettings.MaximumMaxCombatSolveSeconds);
            if (SolverSettings.Current.MaxCombatSolveSeconds == seconds)
                return;
            SolverSettings.Update(SolverSettings.Current with
            {
                MaxCombatSolveSeconds = seconds,
            });
            SetStatus(
                SolverText.Format($"战斗最大求解时长已设为 {seconds} 秒，下次搜索生效"),
                SolverUiTokens.Palette.Success);
        };
        _commitInputs.Add(() =>
        {
            input.Apply();
            return true;
        });
        return input;
    }

    private OptionButton CreateSearchParallelismInput()
    {
        OptionButton input = CreateOptionInput(220);
        int defaultDop = SolverWeights.DefaultSearchMaxDegreeOfParallelism;
        string autoLabel = defaultDop == 1
            ? SolverText.Get("自动（单线程）")
            : SolverText.Format($"自动（{defaultDop} 线程）");
        input.AddItem(autoLabel, 0);
        input.AddItem(SolverText.Get("关闭（单线程）"), 1);
        for (int degree = 2; degree <= SolverWeights.MaximumSearchMaxDegreeOfParallelism; degree++)
            input.AddItem(degree.ToString(CultureInfo.InvariantCulture), degree);
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            int selected = input.GetItemId((int)index);
            int? configured = selected == 0 ? null : selected;
            SolverSettings.Update(SolverSettings.Current with
            {
                SearchMaxDegreeOfParallelism = configured,
            });
            SetStatus(
                selected == 0
                    ? SolverText.Get("最大并发线程数已设为自动，下次搜索生效")
                    : selected == 1
                        ? SolverText.Get("并行搜索已关闭，下次搜索使用单线程")
                        : SolverText.Format($"最大并发线程数已设为 {selected}，下次搜索生效"),
                SolverUiTokens.Palette.Success);
        };
        return input;
    }
}
