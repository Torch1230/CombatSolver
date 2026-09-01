using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace CombatSolver;

internal sealed partial class SolverSettingsPanel
{
    private CheckButton _solverEnabled = null!;
    private CheckButton _automaticCalculation = null!;
    private CheckButton _stopOnCombatEnd = null!;
    private CheckButton _stopOnDeathTurn = null!;
    private CheckButton _stopOnWorseRecalculation = null!;
    private OptionButton _actTransitionBossHpStrategy = null!;
    private OptionButton _finalBossHpStrategy = null!;
    private LineEdit _acceptableBattleHpLoss = null!;
    private OptionButton _searchCompletionNotificationPolicy = null!;
    private CheckButton _pursueFatalKillBonus = null!;
    private CheckButton _pursuePersistentGrowth = null!;
    private OptionButton _overlayTheme = null!;
    private HSlider _overlayOpacity = null!;
    private Label _overlayOpacityValue = null!;

    internal bool SearchCompletionNotificationSettingsConfiguredForTesting
        => _searchCompletionNotificationPolicy.GetItemId(
               _searchCompletionNotificationPolicy.Selected)
           == (int)ResolveSearchCompletionNotificationPolicy(SolverSettings.Current);

    internal bool VisualSettingsConfiguredForTesting
        => _overlayTheme.GetItemId(_overlayTheme.Selected) == (int)SolverSettings.Current.OverlayTheme
           && Math.Abs(_overlayOpacity.Value - SolverSettings.Current.OverlayOpacity) < 0.001d;

    internal bool BossHpStrategySettingsConfiguredForTesting
        => _actTransitionBossHpStrategy.GetItemId(_actTransitionBossHpStrategy.Selected)
               == (int)SolverSettings.Current.ActTransitionBossHpStrategy
           && _finalBossHpStrategy.GetItemId(_finalBossHpStrategy.Selected)
               == (int)SolverSettings.Current.FinalBossHpStrategy;

    internal bool AcceptableBattleHpLossSettingsConfiguredForTesting
        => _acceptableBattleHpLoss.Text
           == SolverSettings.Current.AcceptableBattleHpLoss.ToString(CultureInfo.InvariantCulture);

    internal bool ExerciseAcceptableBattleHpLossSettingsForTesting()
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            SolverSettings.ApplyForTesting(SolverSettings.RoundTripForTesting(
                original with { AcceptableBattleHpLoss = 17 }));
            Reload();
            return AcceptableBattleHpLossSettingsConfiguredForTesting;
        }
        finally
        {
            SolverSettings.ApplyForTesting(original);
            Reload();
        }
    }

    internal bool ExerciseBossHpStrategySettingsForTesting()
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            SolverSettings.ApplyForTesting(SolverSettings.RoundTripForTesting(original with
            {
                ActTransitionBossHpStrategy = BossHpStrategy.MinimizeHpLoss,
                FinalBossHpStrategy = BossHpStrategy.ProgressionFirst,
            }));
            Reload();
            bool actTransitionIndependent = BossHpStrategySettingsConfiguredForTesting;

            SolverSettings.ApplyForTesting(SolverSettings.RoundTripForTesting(original with
            {
                ActTransitionBossHpStrategy = BossHpStrategy.ProgressionFirst,
                FinalBossHpStrategy = BossHpStrategy.MinimizeHpLoss,
            }));
            Reload();
            return actTransitionIndependent && BossHpStrategySettingsConfiguredForTesting;
        }
        finally
        {
            SolverSettings.ApplyForTesting(original);
            Reload();
        }
    }

    internal bool ExerciseVisualSettingsForTesting()
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            SolverSettings.ApplyForTesting(original with
            {
                OverlayTheme = SolverOverlayTheme.Light,
                OverlayOpacity = 0.55f,
            });
            Reload();
            SolverOverlay.ApplyOverlayOpacity();
            return VisualSettingsConfiguredForTesting
                   && _overlayTheme.GetItemId(_overlayTheme.Selected) == (int)SolverOverlayTheme.Light
                   && _overlayOpacityValue.Text == "55%"
                   && Math.Abs(SolverOverlay.OverlayOpacityForTesting - 0.55f) < 0.001f;
        }
        finally
        {
            SolverSettings.ApplyForTesting(original);
            Reload();
            SolverOverlay.ApplyOverlayOpacity();
        }
    }

    internal bool ExerciseSearchCompletionNotificationPolicyForTesting()
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            SolverSettings.ApplyForTesting(original with
            {
                SearchCompletionNotificationsEnabled = false,
                SearchCompletionNotificationMode = SolverSearchCompletionNotificationMode.Always,
            });
            Reload();
            bool disabledLoaded = SelectedSearchCompletionNotificationPolicy()
                                  == SearchCompletionNotificationPolicy.Disabled;

            SolverSettings.ApplyForTesting(original with
            {
                SearchCompletionNotificationsEnabled = true,
                SearchCompletionNotificationMode =
                    SolverSearchCompletionNotificationMode.OnlyWhenGameInBackground,
            });
            Reload();
            bool backgroundLoaded = SelectedSearchCompletionNotificationPolicy()
                                    == SearchCompletionNotificationPolicy.BackgroundOnly;

            SolverSettings.ApplyForTesting(original with
            {
                SearchCompletionNotificationsEnabled = true,
                SearchCompletionNotificationMode = SolverSearchCompletionNotificationMode.Always,
            });
            Reload();
            bool alwaysLoaded = SelectedSearchCompletionNotificationPolicy()
                                == SearchCompletionNotificationPolicy.Always;
            return disabledLoaded && backgroundLoaded && alwaysLoaded;
        }
        finally
        {
            SolverSettings.ApplyForTesting(original);
            Reload();
        }
    }

    private Control CreateGeneralPage()
    {
        VBoxContainer content = CreatePageContent("GeneralSettingsPage");
        content.AddChild(CreateSectionHeading("求解器"));
        GridContainer solverGrid = CreateSettingsGrid();
        _solverEnabled = CreateToggle();
        _solverEnabled.Toggled += OnSolverEnabledToggled;
        AddBasicRow(solverGrid, "启用求解器", _solverEnabled);
        _automaticCalculation = CreateToggle();
        _automaticCalculation.Toggled += OnAutomaticCalculationToggled;
        AddBasicRow(
            solverGrid,
            "自动计算",
            _automaticCalculation,
            "开启后会在进入战斗局面和每个玩家回合自动开始后台计算；关闭后由主面板手动开始计算。");
        _searchCompletionNotificationPolicy = CreateSearchCompletionNotificationPolicyInput();
        AddBasicRow(
            solverGrid,
            "搜索结束通知",
            _searchCompletionNotificationPolicy,
            "搜索成功、失败、停止或结果过期时发送 Windows 系统通知和提示音。可关闭、仅在游戏不处于前台时通知，或始终通知；其他平台不会调用 Windows 接口。");
        _acceptableBattleHpLoss = CreateAcceptableBattleHpLossInput();
        AddBasicRow(
            solverGrid,
            "可接受战损上限（HP）",
            _acceptableBattleHpLoss,
            "完整胜利路线的预计本局战损小于等于此值时停止继续搜索；默认 0，只在零战损路线出现后停止。死亡或未完成路线不会触发。重新计算后生效。");
        content.AddChild(solverGrid);

        content.AddChild(CreateSectionHeading("幕末 Boss"));
        GridContainer bossStrategyGrid = CreateSettingsGrid();
        _actTransitionBossHpStrategy = CreateBossHpStrategyInput(
            data => data.ActTransitionBossHpStrategy,
            (data, strategy) => data with { ActTransitionBossHpStrategy = strategy });
        AddBasicRow(
            bossStrategyGrid,
            "第一、二幕血量取舍",
            _actTransitionBossHpStrategy,
            "通关优先会按战后回复 80% 折算血量价值并尽量保留药水；最低战损会按普通战斗完整比较掉血。重新计算后生效。");
        _finalBossHpStrategy = CreateBossHpStrategyInput(
            data => data.FinalBossHpStrategy,
            (data, strategy) => data with { FinalBossHpStrategy = strategy });
        AddBasicRow(
            bossStrategyGrid,
            "最终 Boss 血量取舍",
            _finalBossHpStrategy,
            "通关优先只要求路线存活并优先保留资源；最低战损会继续比较剩余血量。重新计算后生效。");
        content.AddChild(bossStrategyGrid);
        content.AddChild(CreateSectionHeading("跨战斗收益"));
        GridContainer longTermGrid = CreateSettingsGrid();
        _pursueFatalKillBonus = CreateToggle();
        _pursueFatalKillBonus.Toggled += OnPursueFatalKillBonusToggled;
        AddBasicRow(
            longTermGrid,
            "强制用斩杀牌收尾",
            _pursueFatalKillBonus,
            "贪婪之手、狩猎、狂宴用致命一击击杀时有额外收益。开启后，求解器会强制选择用它们收尾的路线，"
            + "并报出为此多付的生命和药水。狩猎和狂宴是消耗牌，没斩杀就打出等于永久丢掉这张牌，"
            + "所以它们要么用来斩杀，要么留在手里；贪婪之手不消耗，提前打出不算浪费。"
            + "仍然不会为此放弃胜利或送死；只有留着牌会输掉或送死时才退回。");
        _pursuePersistentGrowth = CreateToggle();
        _pursuePersistentGrowth.Toggled += OnPursuePersistentGrowthToggled;
        AddBasicRow(
            longTermGrid,
            "强制打出成长牌",
            _pursuePersistentGrowth,
            "遗传算法、巨镰、王国资产每次打出都会带来跨战斗的永久成长。开启后，求解器会强制选择打出这些牌的路线，"
            + "并在路线面板报出为此多付的生命和药水。只统计牌组里的本体，战斗中生成的复制品不算。");
        content.AddChild(longTermGrid);

        content.AddChild(CreateSectionHeading("自动执行"));
        GridContainer executionGrid = CreateSettingsGrid();
        _stopOnCombatEnd = CreateToggle();
        _stopOnCombatEnd.Toggled += OnStopOnCombatEndToggled;
        AddBasicRow(executionGrid, "预计结束战斗时暂停", _stopOnCombatEnd);
        _stopOnDeathTurn = CreateToggle();
        _stopOnDeathTurn.Toggled += OnStopOnDeathTurnToggled;
        AddBasicRow(executionGrid, "死亡回合时暂停", _stopOnDeathTurn);
        _stopOnWorseRecalculation = CreateToggle();
        _stopOnWorseRecalculation.Toggled += OnStopOnWorseRecalculationToggled;
        AddBasicRow(executionGrid, "重算后战损增加时暂停", _stopOnWorseRecalculation);
        AddBasicRow(executionGrid, "自动出牌速度", CreateDeploymentFastModeInput());
        AddBasicRow(executionGrid, "牌间额外停顿（秒）", CreateOptionalDoubleInput(
            0d,
            data => data.DeploymentInterActionDelaySeconds,
            (data, value) => data with { DeploymentInterActionDelaySeconds = value },
            0d,
            3d));
        content.AddChild(executionGrid);

        content.AddChild(CreateSectionHeading("界面"));
        GridContainer interfaceGrid = CreateSettingsGrid();
        _overlayTheme = CreateOverlayThemeInput();
        AddBasicRow(
            interfaceGrid,
            "界面主题",
            _overlayTheme,
            "深色为默认主题；切换后会重建当前覆盖层，并保留最近的路线与设置页面。");
        AddBasicRow(
            interfaceGrid,
            "覆盖层透明度",
            CreateOverlayOpacityInput(),
            "调整整个求解器覆盖层的透明度，范围为 25%–100%，立即生效。");
        content.AddChild(interfaceGrid);
        return CreatePageScroll(content);
    }

    private void ReloadGeneralPage(SolverSettingsData data)
    {
        _solverEnabled.ButtonPressed = !data.SolverDisabled;
        _automaticCalculation.ButtonPressed = data.AutomaticCalculationEnabled;
        _pursueFatalKillBonus.ButtonPressed = data.PursueFatalKillBonus;
        _pursuePersistentGrowth.ButtonPressed = data.PursuePersistentGrowth;
        _stopOnCombatEnd.ButtonPressed = data.StopFullAutoOnCombatEnd;
        _stopOnDeathTurn.ButtonPressed = data.StopFullAutoOnDeathTurn;
        _stopOnWorseRecalculation.ButtonPressed = data.StopFullAutoOnWorseRecalculation;
    }

    private OptionButton CreateSearchCompletionNotificationPolicyInput()
    {
        OptionButton input = CreateOptionInput(260);
        input.AddItem("关闭", (int)SearchCompletionNotificationPolicy.Disabled);
        input.AddItem("仅游戏不在前台（默认）", (int)SearchCompletionNotificationPolicy.BackgroundOnly);
        input.AddItem("始终通知", (int)SearchCompletionNotificationPolicy.Always);
        _reloadInputs.Add(data => input.Selected = input.GetItemIndex(
            (int)ResolveSearchCompletionNotificationPolicy(data)));
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            SearchCompletionNotificationPolicy policy =
                (SearchCompletionNotificationPolicy)input.GetItemId((int)index);
            SolverSettings.Update(SolverSettings.Current with
            {
                SearchCompletionNotificationsEnabled = policy != SearchCompletionNotificationPolicy.Disabled,
                SearchCompletionNotificationMode = policy == SearchCompletionNotificationPolicy.Always
                    ? SolverSearchCompletionNotificationMode.Always
                    : SolverSearchCompletionNotificationMode.OnlyWhenGameInBackground,
            });
            SetStatus("已保存并立即生效", SolverUiTokens.Palette.Success);
        };
        return input;
    }

    private OptionButton CreateBossHpStrategyInput(
        Func<SolverSettingsData, BossHpStrategy> read,
        Func<SolverSettingsData, BossHpStrategy, SolverSettingsData> write)
    {
        OptionButton input = CreateOptionInput(260);
        input.AddItem("通关优先（默认）", (int)BossHpStrategy.ProgressionFirst);
        input.AddItem("最低战损", (int)BossHpStrategy.MinimizeHpLoss);
        _reloadInputs.Add(data => input.Selected = input.GetItemIndex((int)read(data)));
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            BossHpStrategy strategy = (BossHpStrategy)input.GetItemId((int)index);
            SolverSettings.Update(write(SolverSettings.Current, strategy));
            SolverOverlay.RefreshBossHpStrategyHint();
            SetStatus("已保存，重新计算后生效", SolverUiTokens.Palette.Success);
        };
        return input;
    }
    private static void OnPursueFatalKillBonusToggled(bool enabled)
        => SolverSettings.Update(SolverSettings.Current with { PursueFatalKillBonus = enabled });

    private static void OnPursuePersistentGrowthToggled(bool enabled)
        => SolverSettings.Update(SolverSettings.Current with { PursuePersistentGrowth = enabled });

    private LineEdit CreateAcceptableBattleHpLossInput()
    {
        LineEdit input = CreateInput("0");
        _reloadInputs.Add(data => input.Text = data.AcceptableBattleHpLoss
            .ToString(CultureInfo.InvariantCulture));
        bool Commit()
        {
            string text = input.Text.Trim();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value < 0
                || value > SolverSettings.MaximumAcceptableBattleHpLoss)
            {
                ShowInvalid(input, $"请输入 0–{SolverSettings.MaximumAcceptableBattleHpLoss} 的整数");
                return false;
            }
            if (SolverSettings.Current.AcceptableBattleHpLoss == value)
                return KeepUnchanged(input);
            return SaveSetting(
                input,
                SolverSettings.Current with { AcceptableBattleHpLoss = value },
                "已保存，下次搜索生效");
        }
        input.FocusExited += () => Commit();
        input.TextSubmitted += _ => Commit();
        _commitInputs.Add(Commit);
        return input;
    }

    private OptionButton CreateDeploymentFastModeInput()
    {
        OptionButton input = CreateOptionInput();
        input.AddItem("跟随游戏（默认）", (int)SolverDeploymentFastMode.FollowGame);
        input.AddItem("正常", (int)SolverDeploymentFastMode.Normal);
        input.AddItem("快速", (int)SolverDeploymentFastMode.Fast);
        input.AddItem("瞬间", (int)SolverDeploymentFastMode.Instant);
        _reloadInputs.Add(data => input.Selected = input.GetItemIndex((int)data.DeploymentFastMode));
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            SolverDeploymentFastMode mode = (SolverDeploymentFastMode)input.GetItemId((int)index);
            SolverSettings.Update(SolverSettings.Current with { DeploymentFastMode = mode });
            SetStatus("已保存，下次执行生效", SolverUiTokens.Palette.Success);
        };
        return input;
    }

    private OptionButton CreateOverlayThemeInput()
    {
        OptionButton input = CreateOptionInput();
        input.AddItem("深色（默认）", (int)SolverOverlayTheme.Dark);
        input.AddItem("浅色", (int)SolverOverlayTheme.Light);
        _reloadInputs.Add(data => input.Selected = input.GetItemIndex((int)data.OverlayTheme));
        input.ItemSelected += index =>
        {
            if (_loading)
                return;
            SolverOverlayTheme theme = (SolverOverlayTheme)input.GetItemId((int)index);
            SolverSettings.Update(SolverSettings.Current with { OverlayTheme = theme });
            SetStatus("界面主题已保存并应用", SolverUiTokens.Palette.Success);
            SolverOverlay.ApplyConfiguredTheme();
        };
        return input;
    }

    private Control CreateOverlayOpacityInput()
    {
        HBoxContainer row = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _overlayOpacity = new HSlider
        {
            MinValue = 0.25,
            MaxValue = 1d,
            Step = 0.05,
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(220, 24),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        StyleSlider(_overlayOpacity);
        _overlayOpacityValue = SolverUiTokens.CreateLabel(
            "100%",
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary,
            FontType.Bold);
        _overlayOpacityValue.HorizontalAlignment = HorizontalAlignment.Right;
        _overlayOpacityValue.CustomMinimumSize = new Vector2(48, 24);
        _reloadInputs.Add(data =>
        {
            _overlayOpacity.SetValueNoSignal(data.OverlayOpacity);
            _overlayOpacityValue.Text = $"{Math.Round(data.OverlayOpacity * 100d)}%";
        });
        _overlayOpacity.ValueChanged += value =>
        {
            _overlayOpacityValue.Text = $"{Math.Round(value * 100d)}%";
            if (_loading)
                return;
            SolverSettings.Update(SolverSettings.Current with { OverlayOpacity = (float)value });
            SolverOverlay.ApplyOverlayOpacity();
            SetStatus("透明度已保存并立即生效", SolverUiTokens.Palette.Success);
        };
        row.AddChild(_overlayOpacity);
        row.AddChild(_overlayOpacityValue);
        return row;
    }

    private void OnSolverEnabledToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverController.SetSolverDisabled(!enabled);
        SetStatus(enabled ? "求解器已启用" : "求解器已暂停", SolverUiTokens.Palette.Success);
    }

    private void OnAutomaticCalculationToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverController.SetAutomaticCalculationEnabled(enabled);
        SetStatus(
            enabled ? "自动计算已开启" : "自动计算已关闭",
            SolverUiTokens.Palette.Success);
    }

    private void OnStopOnCombatEndToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverController.SetStopFullAutoOnCombatEnd(enabled);
        SetStatus("已保存并立即生效", SolverUiTokens.Palette.Success);
    }

    private void OnStopOnDeathTurnToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverController.SetStopFullAutoOnDeathTurn(enabled);
        SetStatus("已保存并立即生效", SolverUiTokens.Palette.Success);
    }

    private void OnStopOnWorseRecalculationToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverController.SetStopFullAutoOnWorseRecalculation(enabled);
        SetStatus("已保存并立即生效", SolverUiTokens.Palette.Success);
    }

    private static SearchCompletionNotificationPolicy ResolveSearchCompletionNotificationPolicy(
        SolverSettingsData data)
    {
        if (!data.SearchCompletionNotificationsEnabled)
            return SearchCompletionNotificationPolicy.Disabled;
        return data.SearchCompletionNotificationMode == SolverSearchCompletionNotificationMode.Always
            ? SearchCompletionNotificationPolicy.Always
            : SearchCompletionNotificationPolicy.BackgroundOnly;
    }

    private SearchCompletionNotificationPolicy SelectedSearchCompletionNotificationPolicy()
        => (SearchCompletionNotificationPolicy)_searchCompletionNotificationPolicy.GetItemId(
            _searchCompletionNotificationPolicy.Selected);

    private enum SearchCompletionNotificationPolicy
    {
        Disabled,
        BackgroundOnly,
        Always,
    }
}
