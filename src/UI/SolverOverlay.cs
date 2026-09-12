using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal enum SolverOverlayPresentation
{
    Ready,
    Deploying,
    ExecutedHistory,
    Searching,
}

internal static class SolverOverlay
{
    private enum ResizeEdge
    {
        Right,
        Bottom,
        BottomRight,
    }

    private const string LayerName = "CombatSolverOverlay";
    private const long ResizeLayoutIntervalMilliseconds = 16;
    private static Color Background => SolverUiTokens.Palette.Background;
    private static Color Surface => SolverUiTokens.Palette.Surface;
    private static Color Border => SolverUiTokens.Palette.Border;
    private static Color Accent => SolverUiTokens.Palette.Accent;
    private static Color TextPrimary => SolverUiTokens.Palette.TextPrimary;
    private static Color TextMuted => SolverUiTokens.Palette.TextMuted;
    private static Color Warning => SolverUiTokens.Palette.Warning;
    private static Color Danger => SolverUiTokens.Palette.Danger;
    private static Color Success => SolverUiTokens.Palette.Success;

    private static CanvasLayer? _layer;
    private static PanelContainer? _panel;
    private static Viewport? _viewport;
    private static ScrollContainer? _routeScroll;
    private static VBoxContainer? _body;
    private static VBoxContainer? _mainStack;
    private static ColorRect? _footerDivider;
    private static SolverSettingsPanel? _settingsPanel;
    private static PanelContainer? _summaryStatusBadge;
    private static Label? _summaryStateLabel;
    private static Label? _summaryContextLabel;
    private static PanelContainer? _summaryPanel;
    private static RichTextLabel? _summaryText;
    private static Label? _progressText;
    private static Label? _reviewText;
    private static ProgressBar? _searchProgressBar;
    private static HFlowContainer? _routeHeadingRow;
    private static PanelContainer? _routeOutcomePanel;
    private static Label? _routeHeadingLabel;
    private static readonly SolverRouteRow[] RouteRows = new SolverRouteRow[SolverWeights.UiTurnRows];
    private static PanelContainer? _detailsPanel;
    private static Label? _deathOutcomeLabel;
    private static Label? _potionOutcomeLabel;
    private static Label? _hpOutcomeLabel;
    private static Label? _hpRecoveredOutcomeLabel;
    private static RichTextLabel? _detailsText;
    private static SolverDetailsButton? _detailsButton;
    private static Button? _recalculateButton;
    private static Button? _stopSearchButton;
    private static Button? _adoptRouteButton;
    private static Button? _executeButton;
    private static Button? _fullAutoButton;
    private static SolverActionBar? _actionBar;
    private static Button? _solverEnabledButton;
    private static Label? _stolenResourceOutcomeLabel;
    private static HFlowContainer? _strategyOutcomeRow;
    private static CheckButton? _autoEnableFullAutoSwitch;
    private static Button? _collapseButton;
    private static Button? _settingsButton;
    private static Button? _potionStrategyButton;
    private static Button? _growthStrategyButton;
    private static SolverGrowthStrategyPanel? _growthStrategyPanel;
    private static bool _growthStrategyVisible;
    private static Button? _relicStrategyButton;
    private static SolverRelicStrategyPanel? _relicStrategyPanel;
    private static bool _relicStrategyVisible;
    private static PanelContainer? _searchLimitHint;
    private static Label? _searchLimitHintLabel;
    private static Button? _performanceHintButton;
    private static Button? _bossHpStrategyHintButton;
    private static SolverMemoryUsageBar? _memoryUsageBar;
    private static Button? _systemMemoryReleaseButton;
    private static SolverPotionStrategyPanel? _potionStrategyPanel;
    private static Control? _rightResizeHandle;
    private static Control? _bottomResizeHandle;
    private static Control? _cornerResizeHandle;
    private static PanelContainer? _feedbackBanner;
    private static Label? _feedbackBannerLabel;
    private static HBoxContainer? _theftPolicyControls;
    private static Button? _preserveResourcesButton;
    private static Button? _letEscapeButton;
    private static bool _collapsed;
    private static bool _settingsVisible;
    private static bool _potionStrategyVisible;
    private static bool _deployQueued;
    private static bool _detailsVisible;
    private static bool _dragging;
    private static bool _resizing;
    private static bool _layoutQueued;
    private static SolverButtonStyle? _renderedExecuteButtonStyle;
    private static SolverButtonStyle? _renderedAdoptRouteButtonStyle;
    private static SolverTheftPolicy? _renderedTheftPolicy;
    private static SolverOverlaySnapshot? _lastSnapshot;
    private static SolverOverlaySnapshot? _searchBestSnapshot;
    private static string? _lastMessageText;
    private static int _lastSearchingTurn;
    private static bool _lastSearchDeployWhenReady;
    private static long _lastReviewedWorldlinesBeforeSearch;
    private static double _lastSearchProgressRatio;
    private static SolverOverlayPresentation _presentation = SolverOverlayPresentation.Searching;
    private static int _lastDeploymentTurn;
    private static int _lastDeploymentActionCount;
    private static bool _lastDeploymentEndedTurn;
    private static bool _waitingForNextTurnPlan;
    private static bool _themeRefreshQueued;
    private static int _remainingLayoutPasses;
    private static long _lastResizeLayoutAt;
    private static BossHpRelief _activeBossHpRelief;
    private static Vector2 _dragOffset;
    private static ResizeEdge _activeResizeEdge = ResizeEdge.BottomRight;
    private static Vector2 _resizeStartMousePosition;
    private static Vector2 _resizeStartPrimarySize;
    private static Vector2? _customPanelSize;
    private static Vector2 _panelPosition = new(SolverUiTokens.Size.PanelMargin, SolverUiTokens.Size.PanelMargin);

    public static bool IsVisible
        => _layer != null && GodotObject.IsInstanceValid(_layer) && _layer.Visible;

    internal static bool TheftPolicyVisibleForTesting
        => _theftPolicyControls != null
            && GodotObject.IsInstanceValid(_theftPolicyControls)
            && _theftPolicyControls.Visible;
    internal static bool UnexpectedReplanWarningVisibleForTesting
        => _feedbackBanner?.Visible == true
            && _feedbackBannerLabel?.Text.Contains("计划外重算", StringComparison.Ordinal) == true
            && _feedbackBannerLabel.Text.Contains(
                SolverUiTokens.BugReportUploadInstruction,
                StringComparison.Ordinal);
    internal static bool ManualRouteImprovementVisibleForTesting
        => _feedbackBanner?.Visible == true
            && _feedbackBannerLabel?.Text.Contains("比求解器更好的世界线", StringComparison.Ordinal) == true
            && _feedbackBannerLabel.Text.Contains(
                SolverUiTokens.BugReportUploadInstruction,
                StringComparison.Ordinal);
    internal static string? ExecuteButtonTextForTesting => _executeButton?.Text;
    internal static bool ExecuteButtonDisabledForTesting => _executeButton?.Disabled ?? true;
    internal static string? StopSearchButtonTextForTesting => _stopSearchButton?.Text;
    internal static bool StopSearchButtonDisabledForTesting => _stopSearchButton?.Disabled ?? true;
    internal static string? AdoptRouteButtonTextForTesting => _adoptRouteButton?.Text;
    internal static bool AdoptRouteButtonDisabledForTesting => _adoptRouteButton?.Disabled ?? true;
    internal static SolverOverlayPresentation PresentationForTesting => _presentation;
    internal static string? RouteHeadingForTesting => _routeHeadingLabel?.Text;
    internal static string? HpOutcomeTextForTesting => _hpOutcomeLabel?.Text;
    internal static bool MessageWrappingEnabledForTesting
        => _summaryText is { FitContent: true, AutowrapMode: TextServer.AutowrapMode.WordSmart };
    internal static bool UploadProgressConfiguredForTesting
        => _settingsPanel?.UploadProgressConfiguredForTesting == true;
    internal static bool SearchCompletionNotificationSettingsConfiguredForTesting
        => _settingsPanel?.SearchCompletionNotificationSettingsConfiguredForTesting == true;
    internal static bool SettingsTabsConfiguredForTesting
        => _settingsPanel?.SettingsTabsConfiguredForTesting == true;
    internal static bool ManualSystemMemoryReleaseButtonConfiguredForTesting
        => _memoryUsageBar is { MouseFilter: Control.MouseFilterEnum.Pass } bar
            && GodotObject.IsInstanceValid(bar)
            && bar.IsInsideTree()
            && _systemMemoryReleaseButton is { } button
            && button.GetParent() == bar.GetParent()
            && button.GetIndex() > bar.GetIndex();
    internal static bool NoGcControlsConfiguredForTesting
        => _settingsPanel?.NoGcControlsConfiguredForTesting == true;
    internal static bool MemoryUsageBarConfiguredForTesting
        => _memoryUsageBar != null
            && GodotObject.IsInstanceValid(_memoryUsageBar)
            && _memoryUsageBar.IsInsideTree()
            && _memoryUsageBar.LayoutConfiguredForTesting;
    internal static bool ExerciseMemoryUsageBarForTesting()
        => SolverMemoryUsageBar.ExerciseFormattingForTesting();
    internal static bool VisualSettingsConfiguredForTesting
        => _settingsPanel?.VisualSettingsConfiguredForTesting == true;
    internal static bool BossHpStrategySettingsConfiguredForTesting
        => _settingsPanel?.BossHpStrategySettingsConfiguredForTesting == true;
    internal static bool AcceptableBattleHpLossSettingsConfiguredForTesting
        => _settingsPanel?.AcceptableBattleHpLossSettingsConfiguredForTesting == true;
    internal static bool ResizeUiConfiguredForTesting
        => _rightResizeHandle != null
            && _bottomResizeHandle != null
            && _cornerResizeHandle is TextureRect { Texture: not null }
            && _body?.SizeFlagsVertical == Control.SizeFlags.ExpandFill
            && _routeScroll?.SizeFlagsVertical == Control.SizeFlags.ExpandFill;
    internal static bool PotionStrategyUiConfiguredForTesting
        => _potionStrategyButton != null
            && _potionStrategyPanel is
            {
                RowCountForTesting: > 0,
                RowsUseIconAndTextForTesting: true,
                UsesGridCardsForTesting: true,
                IsSlimForTesting: true,
            };
    internal static bool PerformanceHintVisibleForTesting => _performanceHintButton?.Visible == true;
    internal static bool SearchLimitHintVisibleForTesting => _searchLimitHint?.Visible == true;
    internal static bool BossHpStrategyHintVisibleForTesting
        => _bossHpStrategyHintButton?.Visible == true;
    internal static string? BossHpStrategyHintTextForTesting => _bossHpStrategyHintButton?.Text;
    internal static string? ReviewSummaryTextForTesting => _reviewText?.Text;
    internal static string? SearchSummaryTextForTesting => _summaryText?.Text;
    internal static double SearchProgressRatioForTesting => _lastSearchProgressRatio;
    internal static bool ExercisePerformanceHintForTesting()
    {
        if (_performanceHintButton == null)
            return false;
        SolverSettingsData originalSettings = SolverSettings.Current;
        bool original = _performanceHintButton.Visible;
        try
        {
            SolverSettings.ApplyForTesting(originalSettings with
            {
                ShowBattleDamagePerformanceHint = true,
            });
            SetPerformanceHintVisible(true);
            bool visible = _performanceHintButton.Visible
                && _performanceHintButton.Text.Contains("本场战斗出现战损", StringComparison.Ordinal)
                && _performanceHintButton.Text.Contains("若对结果不满意", StringComparison.Ordinal)
                && _performanceHintButton.Text.Contains("设置 > 性能", StringComparison.Ordinal)
                && _performanceHintButton.Text.Contains("高或极高", StringComparison.Ordinal)
                && _performanceHintButton.Text.Contains("点击本消息之后不再提示", StringComparison.Ordinal)
                && !_performanceHintButton.Text.Contains("跳转", StringComparison.Ordinal);
            SolverSettings.ApplyForTesting(SolverSettings.RoundTripForTesting(
                originalSettings with { ShowBattleDamagePerformanceHint = false }));
            SetPerformanceHintVisible(false);
            SetPerformanceHintVisible(true);
            return visible && !_performanceHintButton.Visible;
        }
        finally
        {
            SolverSettings.ApplyForTesting(originalSettings);
            SetPerformanceHintVisible(original);
        }
    }

    internal static bool ExerciseSearchLimitHintForTesting()
    {
        if (_searchLimitHint == null || _searchLimitHintLabel == null)
            return false;
        string? original = _lastSnapshot?.SearchLimitWarningText;
        try
        {
            SetSearchLimitHint(SolverOverlaySnapshot.BuildSearchLimitWarning(SearchBoundaryReason.TimeLimit));
            bool timeLimit = SearchLimitHintVisibleForTesting
                && _searchLimitHintLabel.Text.Contains("计算尚未彻底穷尽", StringComparison.Ordinal)
                && _searchLimitHintLabel.Text.Contains("时间上限", StringComparison.Ordinal)
                && _searchLimitHintLabel.Text.Contains("设置 > 性能", StringComparison.Ordinal);
            SetSearchLimitHint(SolverOverlaySnapshot.BuildSearchLimitWarning(SearchBoundaryReason.NodeLimit));
            bool nodeLimit = SearchLimitHintVisibleForTesting
                && _searchLimitHintLabel.Text.Contains("节点上限", StringComparison.Ordinal);
            SetSearchLimitHint(SolverOverlaySnapshot.BuildSearchLimitWarning(SearchBoundaryReason.None));
            return timeLimit && nodeLimit && !SearchLimitHintVisibleForTesting;
        }
        finally
        {
            SetSearchLimitHint(original);
        }
    }

    internal static bool ExerciseBossHpStrategyHintForTesting()
    {
        if (_bossHpStrategyHintButton == null)
            return false;
        SolverSettingsData originalSettings = SolverSettings.Current;
        BossHpRelief originalRelief = _activeBossHpRelief;
        try
        {
            SolverSettings.ApplyForTesting(originalSettings with
            {
                ActTransitionBossHpStrategy = BossHpStrategy.ProgressionFirst,
                FinalBossHpStrategy = BossHpStrategy.MinimizeHpLoss,
                ShowActTransitionBossHpStrategyHint = true,
                ShowFinalBossHpStrategyHint = true,
            });
            _activeBossHpRelief = BossHpRelief.ActClearHeal;
            RefreshBossHpStrategyHint();
            bool actTransitionText = BossHpStrategyHintVisibleForTesting
                && _bossHpStrategyHintButton.Text.Contains("第一、二幕", StringComparison.Ordinal)
                && _bossHpStrategyHintButton.Text.Contains("通关优先", StringComparison.Ordinal)
                && _bossHpStrategyHintButton.Text.Contains("80%", StringComparison.Ordinal);

            SolverSettings.ApplyForTesting(SolverSettings.Current with
            {
                ShowActTransitionBossHpStrategyHint = false,
            });
            RefreshBossHpStrategyHint();
            bool actTransitionDismissed = !BossHpStrategyHintVisibleForTesting;

            _activeBossHpRelief = BossHpRelief.RunEnding;
            RefreshBossHpStrategyHint();
            bool finalIndependent = BossHpStrategyHintVisibleForTesting
                && _bossHpStrategyHintButton.Text.Contains("最终 Boss", StringComparison.Ordinal)
                && _bossHpStrategyHintButton.Text.Contains("最低战损", StringComparison.Ordinal);
            return actTransitionText && actTransitionDismissed && finalIndependent;
        }
        finally
        {
            SolverSettings.ApplyForTesting(originalSettings);
            _activeBossHpRelief = originalRelief;
            RefreshBossHpStrategyHint();
        }
    }
    internal static bool ExercisePotionStrategyUiForTesting()
    {
        if (_potionStrategyButton == null || _potionStrategyPanel == null)
            return false;
        bool original = _potionStrategyVisible;
        if (!original)
            TogglePotionStrategy();
        bool opened = _potionStrategyVisible
            && _potionStrategyPanel.Visible
            && ReferenceEquals(_potionStrategyPanel.GetParent(), _layer);
        if (!original)
            TogglePotionStrategy();
        return opened && _potionStrategyVisible == original;
    }
    internal static float OverlayOpacityForTesting => _panel?.Modulate.A ?? 1f;
    internal static int? CurrentSnapshotTurnForTesting => _lastSnapshot?.StartTurnNumber;
    internal static SolverOverlayTheme ActiveThemeForTesting => SolverUiTokens.IsLightTheme
        ? SolverOverlayTheme.Light
        : SolverOverlayTheme.Dark;
    internal static bool ExerciseUploadCompletionTransitionForTesting()
        => _settingsPanel?.ExerciseUploadCompletionTransitionForTesting() == true;
    internal static bool ExercisePerformancePresetPersistenceForTesting()
        => _settingsPanel?.ExercisePerformancePresetPersistenceForTesting() == true;
    internal static bool ExerciseSettingsTabSwitchingForTesting()
        => _settingsPanel?.ExerciseSettingsTabSwitchingForTesting() == true;
    internal static bool ExerciseSearchCompletionNotificationPolicyForTesting()
        => _settingsPanel?.ExerciseSearchCompletionNotificationPolicyForTesting() == true;
    internal static bool ExerciseVisualSettingsForTesting()
        => _settingsPanel?.ExerciseVisualSettingsForTesting() == true;
    internal static bool ExerciseBossHpStrategySettingsForTesting()
        => _settingsPanel?.ExerciseBossHpStrategySettingsForTesting() == true;
    internal static bool ExerciseAcceptableBattleHpLossSettingsForTesting()
    {
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            SolverSettings.ApplyForTesting(SolverSettings.RoundTripForTesting(original with
            {
                AcceptableBattleHpLoss = 17,
                GrowthBudgets = new GrowthValues(Feed: 3, GeneticAlgorithm: 2),
            }));
            _settingsPanel?.Reload();
            return AcceptableBattleHpLossSettingsConfiguredForTesting;
        }
        finally
        {
            SolverSettings.ApplyForTesting(original);
            _settingsPanel?.Reload();
        }
    }

    internal static async Task<bool> ExerciseOverlayResizePersistenceForTestingAsync()
    {
        if (_panel == null || _viewport == null)
            return false;
        SolverSettingsData originalSettings = SolverSettings.Current;
        Vector2 originalPosition = _panelPosition;
        Vector2? originalCustomSize = _customPanelSize;
        bool originalCollapsed = _collapsed;
        bool originalSettingsVisible = _settingsVisible;
        bool originalPotionStrategyVisible = _potionStrategyVisible;
        try
        {
            await WaitForResponsiveLayoutForTestingAsync();
            Vector2 viewportSize = _viewport.GetVisibleRect().Size;
            Vector2 testPosition = new(
                SolverUiTokens.Size.PanelMargin,
                SolverUiTokens.Size.PanelMargin);
            Vector2 contentMinimum = _panel.GetCombinedMinimumSize();
            Vector2 testSize = new(
                Math.Max(SolverSettings.MinimumOverlayWidth, contentMinimum.X) + 80f,
                Math.Max(SolverSettings.MinimumOverlayHeight, contentMinimum.Y) + 60f);
            if (viewportSize.X < testPosition.X + testSize.X + SolverUiTokens.Size.ResizeEdgeThickness
                || viewportSize.Y < testPosition.Y + testSize.Y + SolverUiTokens.Size.ResizeEdgeThickness)
            {
                return false;
            }

            SolverSettingsData persisted = SolverSettings.RoundTripForTesting(originalSettings with
            {
                OverlayPositionX = testPosition.X,
                OverlayPositionY = testPosition.Y,
                OverlayWidth = testSize.X,
                OverlayHeight = testSize.Y,
            });
            SolverSettings.ApplyForTesting(persisted);
            _panelPosition = SolverSettings.OverlayPosition
                ?? throw new InvalidOperationException("Round-tripped overlay position was not restored.");
            _customPanelSize = SolverSettings.OverlaySize
                ?? throw new InvalidOperationException("Round-tripped overlay size was not restored.");
            _settingsVisible = false;
            _potionStrategyVisible = false;
            SetCollapsed(false);
            await WaitForResponsiveLayoutForTestingAsync();
            bool expanded = IsNear(_panel.Size, testSize) && _cornerResizeHandle?.Visible == true;

            SetCollapsed(true);
            await WaitForResponsiveLayoutForTestingAsync();
            Vector2 collapsedMinimum = _panel.GetCombinedMinimumSize();
            bool collapsed = Math.Abs(
                    _panel.Size.X
                    - Math.Clamp(
                        SolverUiTokens.Size.CollapsedWidth,
                        Math.Min(collapsedMinimum.X, viewportSize.X),
                        viewportSize.X)) < 0.5f
                && Math.Abs(
                    _panel.Size.Y
                    - Math.Clamp(
                        SolverUiTokens.Size.CollapsedHeight,
                        Math.Min(collapsedMinimum.Y, viewportSize.Y),
                        viewportSize.Y)) < 0.5f
                && _cornerResizeHandle?.Visible == false;

            SetCollapsed(false);
            await WaitForResponsiveLayoutForTestingAsync();
            bool restored = IsNear(_panel.Size, testSize);
            Vector2 calculationLimit = testSize + new Vector2(200f, 200f);
            bool directionsCorrect = IsNear(
                    ResizePanelSize(
                        ResizeEdge.Right,
                        testSize,
                        new Vector2(40f, 30f),
                        contentMinimum,
                        calculationLimit),
                    testSize + new Vector2(40f, 0f))
                && IsNear(
                    ResizePanelSize(
                        ResizeEdge.Bottom,
                        testSize,
                        new Vector2(40f, 30f),
                        contentMinimum,
                        calculationLimit),
                    testSize + new Vector2(0f, 30f))
                && IsNear(
                    ResizePanelSize(
                        ResizeEdge.BottomRight,
                        testSize,
                        new Vector2(40f, 30f),
                        contentMinimum,
                        calculationLimit),
                    testSize + new Vector2(40f, 30f));
            _potionStrategyVisible = true;
            ApplyContentVisibility();
            ApplyResponsiveLayout();
            await WaitForResponsiveLayoutForTestingAsync();
            bool floatingPotionKeptPrimaryWidth = IsNear(_panel.Size, testSize)
                && _potionStrategyPanel?.Visible == true
                && ReferenceEquals(_potionStrategyPanel.GetParent(), _layer)
                && _customPanelSize == testSize
                && SolverSettings.OverlaySize == testSize;
            bool passed = expanded && collapsed && restored && directionsCorrect
                && floatingPotionKeptPrimaryWidth;
            if (!passed)
            {
                Entry.Logger.Info(
                    $"[CombatSolver/Test] UI_RESIZE_ASSERT expanded={expanded} collapsed={collapsed} " +
                    $"restored={restored} directions={directionsCorrect} " +
                    $"floating_potion={floatingPotionKeptPrimaryWidth} " +
                    $"panel_size={_panel.Size.X:F1}x{_panel.Size.Y:F1} " +
                    $"panel_min={_panel.GetCombinedMinimumSize().X:F1}x{_panel.GetCombinedMinimumSize().Y:F1} " +
                    $"body_min={_body?.GetCombinedMinimumSize().X:F1}x{_body?.GetCombinedMinimumSize().Y:F1} " +
                    $"route_min={_routeScroll?.GetCombinedMinimumSize().X:F1}x{_routeScroll?.GetCombinedMinimumSize().Y:F1} " +
                    $"custom_size={_customPanelSize?.X:F1}x{_customPanelSize?.Y:F1}");
            }
            return passed;
        }
        finally
        {
            SolverSettings.ApplyForTesting(originalSettings);
            _panelPosition = originalPosition;
            _customPanelSize = originalCustomSize;
            _settingsVisible = originalSettingsVisible;
            _potionStrategyVisible = originalPotionStrategyVisible;
            SetCollapsed(originalCollapsed);
            ApplyContentVisibility();
            ApplyResponsiveLayout();
            await WaitForResponsiveLayoutForTestingAsync();
        }
    }

    private static async Task WaitForResponsiveLayoutForTestingAsync()
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel))
            return;
        SceneTree tree = _panel.GetTree();
        await _panel.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        await _panel.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }

    private static bool IsNear(Vector2 left, Vector2 right)
        => Math.Abs(left.X - right.X) < 0.5f
            && Math.Abs(left.Y - right.Y) < 0.5f;

    public static void Show(Node host, string text)
    {
        _lastSnapshot = null;
        _lastMessageText = text;
        EnsureCreated(host);
        _deployQueued = false;
        SetStatus(SolverText.Get("求解器消息"), TextMuted);
        SetSearchLimitHint(null);
        SetPerformanceHintVisible(false);
        SetCurrentBossHpStrategyHint();
        SetReviewText(null);
        const string legacyTitle = "[b]战斗路线求解器[/b]\n";
        const string englishTitle = "[b]CombatSolver[/b]\n";
        SetMessageContent(text.StartsWith(legacyTitle, StringComparison.Ordinal) ? text[legacyTitle.Length..]
            : text.StartsWith(englishTitle, StringComparison.Ordinal) ? text[englishTitle.Length..] : text);
        ShowLayer();
        RefreshControls();
    }

    public static void ShowDisabled(Node host)
    {
        _lastSnapshot = null;
        _lastMessageText = null;
        EnsureCreated(host);
        _deployQueued = false;
        SetStatus(SolverText.Get("求解器已禁用"), TextMuted);
        SetSearchLimitHint(null);
        SetPerformanceHintVisible(false);
        SetBossHpStrategyHint(BossHpRelief.None);
        SetReviewText(null);
        SetMessageContent(SolverText.Format($"[color={SolverUiTokens.Palette.TextSecondaryHex}]自动搜索和路线执行已暂停。[/color]"));
        ShowLayer();
        RefreshControls();
    }

    public static void ShowSearchStopped(Node host)
    {
        _lastMessageText = null;
        EnsureCreated(host);
        _deployQueued = false;
        SetStatus(SolverText.Get("计算已停止"), Danger);
        SetSearchLimitHint(null);
        SetPerformanceHintVisible(false);
        SetCurrentBossHpStrategyHint();
        SetReviewText(null);
        if (_searchBestSnapshot == null && _lastSnapshot == null)
        {
            SetMessageContent(
                SolverText.Format($"[color={SolverUiTokens.Palette.DangerHex}]本回合计算已停止。可手动开始计算；进入下一回合后是否自动计算由设置决定。[/color]"));
        }
        else if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text =
                SolverText.Format($"[color={SolverUiTokens.Palette.WarningHex}]计算已停止；下方保留停止时已经得到的当前候选路线。[/color]");
        }
        ShowLayer();
        RefreshControls();
    }

    public static void ShowManualCalculationReady(Node host, bool hasPreviousCalculation)
    {
        _lastSnapshot = null;
        _searchBestSnapshot = null;
        _lastMessageText = null;
        EnsureCreated(host);
        _deployQueued = false;
        SetStatus(SolverText.Get("等待手动计算"), TextMuted);
        SetSearchLimitHint(null);
        SetPerformanceHintVisible(false);
        SetCurrentBossHpStrategyHint();
        SetReviewText(null);
        SetMessageContent(
            SolverText.Format($"[color={SolverUiTokens.Palette.TextSecondaryHex}]自动计算已关闭。点击“{(hasPreviousCalculation ? SolverText.Get("重新计算") : SolverText.Get("开始计算"))}”生成当前回合路线。[/color]"));
        ShowLayer();
        RefreshControls();
    }

    public static void ShowProgress(
        SolverProgress progress,
        bool deployWhenReady,
        long reviewedWorldlinesBeforeSearch,
        SolverOverlaySnapshot? bestSnapshot = null)
    {
        _presentation = SolverOverlayPresentation.Searching;
        _waitingForNextTurnPlan = false;
        if (_layer == null || !GodotObject.IsInstanceValid(_layer) || !_layer.Visible)
            return;
        _deployQueued = deployWhenReady;
        _lastSearchDeployWhenReady = deployWhenReady;
        _lastReviewedWorldlinesBeforeSearch = reviewedWorldlinesBeforeSearch;
        if (bestSnapshot != null)
        {
            _searchBestSnapshot = bestSnapshot;
            PopulateRoute(bestSnapshot, resetScroll: false);
        }
        string routeContext = _searchBestSnapshot is { Turns.Count: > 0 } routeSnapshot
            ? SolverText.Format($"已规划至第 {routeSnapshot.Turns[^1].Turn} 回合")
            : SolverText.Get("等待候选路线");
        bool reclaimingMemory = progress.Phase.EndsWith("正在整理内存", StringComparison.Ordinal);
        bool changingPotionGradient = progress.Phase.StartsWith(
            "切换用药路线",
            StringComparison.Ordinal);
        SetStatus(
            reclaimingMemory
                ? changingPotionGradient ? SolverText.Get("切换用药路线") : SolverText.Get("正在整理内存")
                : SolverText.Get("后台计算中"),
            reclaimingMemory ? Warning : Accent,
            deployWhenReady ? SolverText.Format($"{routeContext}    已排队执行") : routeContext);
        if (_routeHeadingLabel != null)
            _routeHeadingLabel.Text = SolverText.Get("求解器当前考虑（尚未验证）");
        if (_progressText != null)
        {
            _progressText.Visible = true;
            _progressText.Text = SolverText.Format($"已用 {progress.ElapsedMilliseconds / 1000d:F1} s");
        }
        string potionSearchPhase = progress.Phase.StartsWith("正在搜索", StringComparison.Ordinal)
            || reclaimingMemory
                ? progress.Phase
                : string.Empty;
        string reviewedWorldlinesText =
            SolverText.Format($"已查阅 {reviewedWorldlinesBeforeSearch + progress.ReviewedWorldlines:N0} 条世界线");
        SetReviewText(SolverText.IsEnglish && potionSearchPhase.Length > 0
            ? reclaimingMemory ? SolverText.Get("正在整理内存") : SolverText.Get("后台计算中")
            : potionSearchPhase);
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = _searchBestSnapshot is { } snapshot
                ? SolverUiTokens.AdaptRichTextToActiveTheme(snapshot.SummaryText) +
                  $"  │  {reviewedWorldlinesText}"
                : reviewedWorldlinesText;
        }
        if (_searchProgressBar != null)
        {
            _searchProgressBar.Visible = true;
            double currentRatio = Math.Clamp(
                progress.ExpandedNodes / (double)Math.Max(1, progress.MaxNodes),
                0d,
                1d);
            _lastSearchProgressRatio = Math.Max(_lastSearchProgressRatio, currentRatio);
            _searchProgressBar.MaxValue = 1d;
            _searchProgressBar.Value = _lastSearchProgressRatio;
        }
        RefreshControls();
    }

    public static void ShowSearching(
        Node host,
        int turn,
        bool deployWhenReady,
        long reviewedWorldlinesBeforeSearch)
    {
        _presentation = SolverOverlayPresentation.Searching;
        _waitingForNextTurnPlan = false;
        SolverController.InvalidateRenderedRouteAdoptionSeed();
        _lastSnapshot = null;
        _searchBestSnapshot = null;
        _lastMessageText = null;
        _lastSearchingTurn = turn;
        _lastSearchDeployWhenReady = deployWhenReady;
        _lastReviewedWorldlinesBeforeSearch = reviewedWorldlinesBeforeSearch;
        _lastSearchProgressRatio = 0d;
        EnsureCreated(host);
        SetSearchLimitHint(null);
        SetCurrentBossHpStrategyHint();
        _deployQueued = deployWhenReady;
        SetStatus(
            SolverText.Get("后台计算中"),
            Accent,
            deployWhenReady ? SolverText.Format($"第 {turn} 回合    已排队执行") : SolverText.Format($"第 {turn} 回合"));
        if (_routeHeadingLabel != null)
            _routeHeadingLabel.Text = SolverText.Get("求解器当前考虑（尚未验证）");
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverText.Format($"[color={SolverUiTokens.Palette.TextSecondaryHex}]正在计算当前回合，等待可存活候选…[/color]");
        }
        if (_progressText != null)
            _progressText.Visible = false;
        SetReviewText(null);
        SetPerformanceHintVisible(false);
        if (_searchProgressBar != null)
        {
            _searchProgressBar.Visible = true;
            _searchProgressBar.MaxValue = SolverSearchProfile.Default.MaxExpandedNodes;
            _searchProgressBar.Value = 0;
        }
        SetRouteVisibility(true);
        if (_potionOutcomeLabel != null)
            _potionOutcomeLabel.Visible = false;
        if (_hpOutcomeLabel != null)
            _hpOutcomeLabel.Visible = false;
        if (_stolenResourceOutcomeLabel != null)
            _stolenResourceOutcomeLabel.Visible = false;
        if (_hpRecoveredOutcomeLabel != null)
            _hpRecoveredOutcomeLabel.Visible = false;
        if (_deathOutcomeLabel != null)
            _deathOutcomeLabel.Visible = false;
        for (int index = 0; index < SolverWeights.UiTurnRows; index++)
        {
            SetRouteRowVisible(index, index == 0);
            if (index != 0)
                continue;
            RouteRows[index].TurnLabel.Text = SolverText.Format($"第 {turn} 回合");
            RouteRows[index].ShowStatus(SolverText.Get("等待当前回合候选…"));
            RouteRows[index].SetOutcome(string.Empty, TextMuted);
        }
        if (_routeScroll != null)
            _routeScroll.ScrollVertical = 0;
        if (_summaryPanel != null)
            _summaryPanel.Visible = true;
        if (_detailsPanel != null)
            _detailsPanel.Visible = false;
        if (_detailsButton != null)
            _detailsButton.Visible = false;
        SetDetailsVisible(false);
        ShowLayer();
        RefreshControls();
        Entry.Logger.Info($"[CombatSolver/Test] UI_STATE state=searching turn={turn} deploy_queued={deployWhenReady}");
    }

    public static void ShowResult(Node host, SolverOverlaySnapshot snapshot)
    {
        _presentation = SolverOverlayPresentation.Ready;
        _waitingForNextTurnPlan = false;
        _lastSnapshot = snapshot;
        _searchBestSnapshot = null;
        _lastMessageText = null;
        EnsureCreated(host);
        _deployQueued = false;
        SetSearchLimitHint(snapshot.SearchLimitWarningText);
        Color statusColor = snapshot.StatusTone switch
        {
            SolverOverlayTone.Danger => Danger,
            SolverOverlayTone.Success => Success,
            _ => Accent,
        };
        SetStatus(snapshot.StatusText, statusColor);
        if (_summaryPanel != null)
            _summaryPanel.Visible = true;
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverUiTokens.AdaptRichTextToActiveTheme(snapshot.SummaryText);
        }
        if (_progressText != null)
            _progressText.Visible = false;
        SetReviewText(snapshot.ReviewSummaryText);
        bool hasRouteDetails = !string.IsNullOrEmpty(snapshot.DetailsText);
        SetPerformanceHintVisible(
            hasRouteDetails
            && snapshot.ProjectedBattleHpLossKnown
            && snapshot.ProjectedBattleHpLost > 0);
        SetCurrentBossHpStrategyHint();
        if (_searchProgressBar != null)
            _searchProgressBar.Visible = false;

        if (_routeHeadingLabel != null)
            _routeHeadingLabel.Text = SolverText.Get("推荐路线");
        PopulateRoute(snapshot, resetScroll: true);
        if (_detailsButton != null)
            _detailsButton.Visible = hasRouteDetails;
        if (_detailsText != null)
            _detailsText.Text = SolverUiTokens.AdaptRichTextToActiveTheme(snapshot.DetailsText);
        SetDetailsVisible(false);
        ShowLayer();
        RefreshControls();
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_STATE state=ready turn={snapshot.StartTurnNumber} " +
            $"risk={snapshot.HasRisk} only_death_routes={snapshot.OnlyDeathRoutesFound}");
    }

    private static void PopulateRoute(SolverOverlaySnapshot snapshot, bool resetScroll)
    {
        SetRouteVisibility(true);
        if (_strategyOutcomeRow != null)
        {
            foreach (Node child in _strategyOutcomeRow.GetChildren())
            {
                _strategyOutcomeRow.RemoveChild(child);
                child.QueueFree();
            }
            foreach (var outcome in snapshot.StrategyOutcomes)
            {
                Label label = CreateTextLabel(outcome.Text, 16, outcome.Satisfied ? Success : Warning, FontType.Bold);
                label.TooltipText = outcome.Text;
                label.HorizontalAlignment = HorizontalAlignment.Right;
                _strategyOutcomeRow.AddChild(label);
            }
            _strategyOutcomeRow.Visible = snapshot.StrategyOutcomes.Count > 0;
        }
        if (_stolenResourceOutcomeLabel != null)
        {
            _stolenResourceOutcomeLabel.Text = snapshot.UnrecoveredLootText ?? string.Empty;
            _stolenResourceOutcomeLabel.Visible = snapshot.UnrecoveredLootText != null;
        }
        if (_potionOutcomeLabel != null)
        {
            _potionOutcomeLabel.Visible = snapshot.ProjectedBattlePotionCount > 0;
            _potionOutcomeLabel.Text = SolverText.Format($"预计用{snapshot.ProjectedBattlePotionCount}瓶药");
        }
        if (_hpOutcomeLabel != null)
            _hpOutcomeLabel.Visible = true;
        if (_hpRecoveredOutcomeLabel != null)
        {
            // Post-combat relic healing is reported next to route healing rather than folded into it:
            // it lands after the last action, so attributing it to a turn would misplace it.
            _hpRecoveredOutcomeLabel.Visible = snapshot.RouteHpRecovered > 0
                || snapshot.RoutePostCombatRelicHeal > 0;
            _hpRecoveredOutcomeLabel.Text =
                (snapshot.RouteHpRecovered, snapshot.RoutePostCombatRelicHeal) switch
                {
                    ( > 0, > 0) => SolverText.Format($"路线回血  {snapshot.RouteHpRecovered} HP") +
                        SolverText.Format($"　战后遗物  {snapshot.RoutePostCombatRelicHeal} HP"),
                    ( > 0, _) => SolverText.Format($"路线回血  {snapshot.RouteHpRecovered} HP"),
                    (_, > 0) => SolverText.Format($"战后遗物回血  {snapshot.RoutePostCombatRelicHeal} HP"),
                    _ => string.Empty,
                };
        }
        if (_deathOutcomeLabel != null)
            _deathOutcomeLabel.Visible = snapshot.OnlyDeathRoutesFound;
        for (int index = 0; index < SolverWeights.UiTurnRows; index++)
        {
            SetRouteRowVisible(index, index < snapshot.Turns.Count);
            if (index >= snapshot.Turns.Count)
                continue;
            SolverOverlayTurnSnapshot turn = snapshot.Turns[index];
            RouteRows[index].TurnLabel.Text = SolverText.Format($"第 {turn.Turn} 回合");
            RouteRows[index].Populate(turn);
            // Damage alone reads wrong on a turn that also heals: the player wants the number the
            // turn actually leaves them at, not the hits they took on the way there.
            int netHpChange = turn.HpRecovered - turn.HpLoss;
            string outcome = turn.CombatEnded
                ? SolverText.Get("战斗结束")
                : netHpChange != 0
                    ? $"{HpChangeText.Signed(netHpChange)} HP"
                    : "0 HP";
            RouteRows[index].SetOutcome(
                outcome,
                turn.CombatEnded ? Success : netHpChange < 0 ? Danger : netHpChange > 0 ? Success : TextMuted,
                energyText: SolverText.Format($"余 {turn.EnergyLeft} 费"),
                enemyDamageText: turn.EnemyHpDamageLost is { } damage
                    ? SolverText.Format($"对敌伤害 {damage}")
                    : string.Empty);
        }
        if (resetScroll && _routeScroll != null)
            _routeScroll.ScrollVertical = 0;

        if (_hpOutcomeLabel != null)
        {
            _hpOutcomeLabel.Text = snapshot.HpOutcomeText;
            _hpOutcomeLabel.AddThemeColorOverride(
                "font_color",
                !snapshot.ProjectedBattleHpLossKnown
                    ? TextMuted
                    : snapshot.ProjectedBattleHpLost > 0
                        ? Danger
                        : Success);
        }
    }

    public static void ShowDeploying(Node host, int turn, int actionCount)
    {
        _presentation = SolverOverlayPresentation.Deploying;
        _waitingForNextTurnPlan = false;
        _lastDeploymentTurn = turn;
        _lastDeploymentActionCount = actionCount;
        _lastDeploymentEndedTurn = false;
        EnsureCreated(host);
        _deployQueued = false;
        SetStatus(SolverText.Get("正在执行"), Warning, SolverText.Format($"第 {turn} 回合"));
        if (_routeHeadingLabel != null)
            _routeHeadingLabel.Text = SolverText.Get("正在执行的路线");
        if (_summaryPanel != null)
            _summaryPanel.Visible = true;
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverText.Format($"[color={SolverUiTokens.Palette.TextSecondaryHex}]按推荐顺序执行 [b]{actionCount}[/b] 张牌，完成后结束本回合。[/color]");
        }
        if (_progressText != null)
            _progressText.Visible = false;
        if (_searchProgressBar != null)
            _searchProgressBar.Visible = false;
        ShowDeploymentStep(0, actionCount, null);
        if (_routeScroll != null)
            _routeScroll.ScrollVertical = 0;
        ShowLayer();
        RefreshControls();
        Entry.Logger.Info($"[CombatSolver/Test] UI_STATE state=deploying turn={turn} card_count={actionCount}");
    }

    public static void ShowDeploymentStep(int completedActions, int actionCount, string? currentCardTitle)
    {
        if (RouteRows[0] == null)
            return;
        if (RouteRows[0].DeploymentActionCount != actionCount)
        {
            throw new InvalidOperationException(
                $"部署动作数为 {actionCount}，Overlay 动作胶囊数为 {RouteRows[0].DeploymentActionCount}。");
        }
        int? activeActionIndex = currentCardTitle != null && completedActions < actionCount
            ? completedActions
            : null;
        RouteRows[0].SetDeploymentProgress(completedActions, activeActionIndex);
        if (_summaryText != null && completedActions < actionCount && currentCardTitle != null)
        {
            _summaryText.Text =
                SolverText.Format($"[color={SolverUiTokens.Palette.TextSecondaryHex}]正在执行 [b]{completedActions + 1}/{actionCount}[/b]：[/color][color={SolverUiTokens.Palette.WarningHex}] {currentCardTitle}[/color]");
        }
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_DEPLOYMENT_STEP completed={completedActions} " +
            $"action_count={actionCount} active_action_index={activeActionIndex?.ToString() ?? "-"}");
    }

    public static void ShowEndTurnDeploymentStep()
    {
        if (RouteRows[0] == null)
            return;
        RouteRows[0].SetEndTurnDeploymentState(active: true, completed: false);
        if (_summaryText != null)
        {
            _summaryText.Text =
                SolverText.Format($"[color={SolverUiTokens.Palette.TextSecondaryHex}]正在执行：[/color]") +
                SolverText.Format($"[color={SolverUiTokens.Palette.WarningHex}] 结束回合[/color]");
        }
        Entry.Logger.Info("[CombatSolver/Test] UI_DEPLOYMENT_END_TURN state=active");
    }

    public static void ShowDeploymentComplete(Node host, int turn, int actionCount, bool endedTurn)
    {
        _presentation = SolverOverlayPresentation.ExecutedHistory;
        _waitingForNextTurnPlan = false;
        _lastDeploymentTurn = turn;
        _lastDeploymentActionCount = actionCount;
        _lastDeploymentEndedTurn = endedTurn;
        EnsureCreated(host);
        ShowDeploymentStep(actionCount, actionCount, null);
        RouteRows[0].SetEndTurnDeploymentState(active: false, completed: endedTurn);
        _deployQueued = false;
        SetStatus(SolverText.Get("执行完成"), Accent, SolverText.Format($"第 {turn} 回合"));
        if (_routeHeadingLabel != null)
            _routeHeadingLabel.Text = SolverText.Get("已执行路线（后续回合待校验）");
        if (_summaryPanel != null)
            _summaryPanel.Visible = true;
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = endedTurn
                ? SolverText.Format($"已按推荐路线打出 [b]{actionCount}[/b] 张牌，并提交结束回合动作。")
                : SolverText.Format($"已打出 [b]{actionCount}[/b] 张牌；战斗或当前回合已在执行期间结束。");
        }
        if (_progressText != null)
            _progressText.Visible = false;
        ShowLayer();
        RefreshControls();
        Entry.Logger.Info($"[CombatSolver/Test] UI_STATE state=deployment_complete turn={turn} card_count={actionCount} end_turn={endedTurn}");
    }

    public static void ShowWaitingForNextTurnPlan(Node host)
    {
        _presentation = SolverOverlayPresentation.ExecutedHistory;
        _waitingForNextTurnPlan = true;
        if (_lastSnapshot == null)
        {
            Show(host, SolverText.Get("[b]战斗路线求解器[/b]\n等待下一回合方案。"));
            return;
        }

        EnsureCreated(host);
        SetStatus(SolverText.Get("等待下一回合方案"), TextMuted, SolverText.Format($"第 {_lastDeploymentTurn} 回合已执行"));
        if (_routeHeadingLabel != null)
            _routeHeadingLabel.Text = SolverText.Get("已执行路线（后续回合待校验）");
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text =
                SolverText.Format($"[color={SolverUiTokens.Palette.TextSecondaryHex}]当前路线已经执行；等待下一回合方案就绪。[/color]");
        }
        ShowLayer();
        RefreshControls();
    }

    public static void ShowFullAutoStoppedAtCombatEnd(int turn)
    {
        SetStatus(SolverText.Get("方案就绪"), Success, SolverText.Format($"第 {turn} 回合    全自动已暂停"));
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverText.Format($"[color={SolverUiTokens.Palette.SuccessHex}]当前方案预计结束战斗，操作权已交还。[/color]");
        }
    }

    public static void ShowFullAutoStoppedAtDeathTurn(int turn)
    {
        SetStatus(SolverText.Get("已暂停执行"), Danger, SolverText.Format($"第 {turn} 回合    预计死亡"));
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverText.Format($"[color={SolverUiTokens.Palette.DangerHex}]当前方案预计本回合死亡，操作权已交还。[/color]");
        }
    }

    public static void ShowFullAutoStoppedAfterWorseRecalculation(
        int turn,
        int? previousProjectedBattleHpLost,
        int projectedBattleHpLost)
    {
        SetStatus(SolverText.Get("已暂停执行"), Danger, SolverText.Format($"第 {turn} 回合    重算后战损上升"));
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text =
                SolverText.Format($"[color={SolverUiTokens.Palette.DangerHex}]完整路线原预计 {previousProjectedBattleHpLost} HP，") +
                SolverText.Format($"重算后为 {projectedBattleHpLost} HP；全自动已暂停。[/color]\n") +
                SolverUiTokens.BugReportUploadInstructionRichText;
        }
    }

    public static void ShowFullAutoStoppedAtLiveRisk(
        int turn,
        int plannedHpLoss,
        int liveHpLoss,
        bool playerDead)
    {
        SetStatus(
            SolverText.Get("已暂停执行"),
            Danger,
            playerDead
                ? SolverText.Format($"第 {turn} 回合    实机复核将死亡")
                : SolverText.Format($"第 {turn} 回合    实机复核战损上升"));
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text =
                SolverText.Format($"[color={SolverUiTokens.Palette.DangerHex}]路线预计掉血 {plannedHpLoss} HP，") +
                SolverText.Format($"结束回合前实机复核为 {liveHpLoss} HP；全自动未提交结束回合。[/color]\n") +
                SolverUiTokens.BugReportUploadInstructionRichText;
        }
    }

    public static void RefreshControls()
    {
        if (_recalculateButton == null || _stopSearchButton == null || _adoptRouteButton == null
            || _executeButton == null || _fullAutoButton == null)
            return;

        RefreshFeedbackBanner();

        bool solverDisabled = SolverController.SolverDisabled;
        if (_solverEnabledButton != null)
            _solverEnabledButton.Text = SolverText.Get(solverDisabled ? "求解器：关" : "求解器：开");
        bool searching = SolverController.IsSearching;
        bool adoptingRoute = SolverController.IsAdoptingCurrentRoute;
        bool canAdoptRoute = SolverController.CanAdoptCurrentRoute;
        bool canApplyCurrentTurn = SolverController.CanApplyCurrentTurn;
        bool canExecuteCurrentTurn = SolverController.CanExecuteCurrentTurn;
        _recalculateButton.Text = !SolverController.AutomaticCalculationEnabled
            && !SolverController.HasCalculatedThisCombat
                ? SolverText.Get("开始计算")
                : SolverText.Get("重新计算");
        _recalculateButton.Disabled = solverDisabled || searching
            || SolverController.IsDeploying || adoptingRoute;
        _stopSearchButton.Disabled = solverDisabled || !searching || SolverController.IsStoppingSearch;
        _adoptRouteButton.Disabled = solverDisabled || !canAdoptRoute || adoptingRoute;
        _adoptRouteButton.Text = adoptingRoute ? SolverText.Get("正在采用…") : SolverText.Get("采用当前路线");
        SolverButtonStyle adoptRouteStyle = SolverButtonStyle.Secondary;
        if (_renderedAdoptRouteButtonStyle != adoptRouteStyle)
        {
            SolverUiTokens.ApplyButtonStyle(_adoptRouteButton, adoptRouteStyle);
            _renderedAdoptRouteButtonStyle = adoptRouteStyle;
        }
        _executeButton.Disabled = solverDisabled
            || SolverController.IsDeploying
            || SolverController.IsApplyingCurrentTurn
            || adoptingRoute
            || searching && !canApplyCurrentTurn
            || !searching && !canExecuteCurrentTurn;
        if (SolverController.IsDeploying)
            _executeButton.Text = SolverText.Get("执行中…");
        else if (SolverController.IsApplyingCurrentTurn)
            _executeButton.Text = SolverText.Get("正在应用…");
        else if (searching)
            _executeButton.Text = SolverText.Get("应用当前回合");
        else if (_deployQueued)
            _executeButton.Text = SolverText.Get("已排队执行");
        else if (_presentation == SolverOverlayPresentation.ExecutedHistory
                 && !canExecuteCurrentTurn)
            _executeButton.Text = SolverText.Get("等待下一回合");
        else
            _executeButton.Text = SolverText.Get("执行本回合");
        SolverButtonStyle executeStyle = SolverButtonStyle.Secondary;
        if (_renderedExecuteButtonStyle != executeStyle)
        {
            SolverUiTokens.ApplyButtonStyle(_executeButton, executeStyle);
            _renderedExecuteButtonStyle = executeStyle;
        }

        _fullAutoButton.Text = SolverText.Get(SolverController.FullAutoEnabled ? "全自动：开" : "全自动：关");
        SolverUiTokens.ApplyButtonStyle(_fullAutoButton, SolverController.FullAutoEnabled ? SolverButtonStyle.Positive : SolverButtonStyle.Secondary);
        _fullAutoButton.Disabled = solverDisabled || adoptingRoute;
        if (_autoEnableFullAutoSwitch != null)
            _autoEnableFullAutoSwitch.ButtonPressed = SolverSettings.Current.AutoEnableFullAuto;
        _actionBar?.Refresh(new SolverActionBarState(_collapsed, searching, canAdoptRoute || adoptingRoute));
        _executeButton.TooltipText = SolverText.Get(solverDisabled ? "求解器已关闭，请从标题栏开启。"
            : adoptingRoute ? "正在采用路线，请等待完成。"
            : SolverController.IsDeploying ? "正在执行当前回合。"
            : _executeButton.Disabled ? "当前没有可执行的回合，请等待计算或完成当前选牌。"
            : "执行当前回合的动作。");

        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        bool combatActive = combat != null && CombatManager.Instance.IsInProgress;
        if (_growthStrategyButton != null)
        {
            _growthStrategyButton.Disabled = !combatActive;
            _growthStrategyButton.AddThemeColorOverride("font_color", _growthStrategyVisible ? Accent : SolverUiTokens.Palette.TextSecondary);
        }
        _growthStrategyPanel?.Refresh(SolverController.IsDeploying);
        if (_relicStrategyButton != null)
        {
            _relicStrategyButton.Disabled = !combatActive;
            _relicStrategyButton.AddThemeColorOverride("font_color", _relicStrategyVisible ? Accent : SolverUiTokens.Palette.TextSecondary);
        }
        if (_relicStrategyVisible) _relicStrategyPanel?.Refresh(SolverController.IsDeploying);
        if (_potionStrategyButton != null)
        {
            _potionStrategyButton.Disabled = !combatActive;
            _potionStrategyButton.AddThemeColorOverride(
                "font_color",
                _potionStrategyVisible || SolverUiTokens.IsLightTheme
                    ? Accent
                    : SolverUiTokens.Palette.TextSecondary);
        }
        _potionStrategyPanel?.Refresh(
            combatActive ? combat : null,
            SolverController.IsDeploying);
        bool showTheftPolicy = combat != null
            && combatActive
            && TheftEncounterStrategy.IsApplicable(combat);
        if (_theftPolicyControls != null)
            _theftPolicyControls.Visible = showTheftPolicy;
        if (!showTheftPolicy || _preserveResourcesButton == null || _letEscapeButton == null)
        {
            _renderedTheftPolicy = null;
            return;
        }

        SolverTheftPolicy activePolicy = SolverController.TheftPolicy
            ?? SolverTheftPolicy.PreserveResources;
        _preserveResourcesButton.Disabled = solverDisabled || SolverController.IsDeploying;
        _letEscapeButton.Disabled = solverDisabled || SolverController.IsDeploying;
        if (_renderedTheftPolicy != activePolicy)
        {
            SolverUiTokens.ApplyButtonStyle(
                _preserveResourcesButton,
                activePolicy == SolverTheftPolicy.PreserveResources
                    ? SolverButtonStyle.Positive
                    : SolverButtonStyle.Secondary);
            SolverUiTokens.ApplyButtonStyle(
                _letEscapeButton,
                activePolicy == SolverTheftPolicy.LetEscape
                    ? SolverButtonStyle.Primary
                    : SolverButtonStyle.Secondary);
            _renderedTheftPolicy = activePolicy;
        }
    }

    public static void Hide()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            _layer.Visible = false;
    }

    public static void ApplyOverlayOpacity()
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel))
            return;
        float opacity = Math.Clamp(SolverSettings.Current.OverlayOpacity, 0.25f, 1f);
        Color modulate = new(1f, 1f, 1f, opacity);
        _panel.Modulate = modulate;
        if (_rightResizeHandle != null)
            _rightResizeHandle.Modulate = modulate;
        if (_bottomResizeHandle != null)
            _bottomResizeHandle.Modulate = modulate;
        if (_cornerResizeHandle != null)
            _cornerResizeHandle.Modulate = modulate;
        if (_potionStrategyPanel != null)
            _potionStrategyPanel.Modulate = Colors.White;
        if (_growthStrategyPanel != null)
            _growthStrategyPanel.Modulate = Colors.White;
        if (_relicStrategyPanel != null) _relicStrategyPanel.Modulate = Colors.White;
    }

    public static void ApplyConfiguredTheme()
    {
        if (_themeRefreshQueued)
            return;
        _themeRefreshQueued = true;
        Callable.From(RebuildConfiguredTheme).CallDeferred();
    }

    private static void RebuildConfiguredTheme()
    {
        _themeRefreshQueued = false;
        SolverUiTokens.ConfigureTheme(SolverSettings.Current.OverlayTheme);
        if (_layer == null || !GodotObject.IsInstanceValid(_layer))
            return;
        Node host = _layer.GetParent()
            ?? throw new InvalidOperationException("CombatSolver overlay has no host node.");
        bool wasVisible = _layer.Visible;
        bool wasSettingsVisible = _settingsVisible;
        bool wasPotionStrategyVisible = _potionStrategyVisible;
        bool wasGrowthStrategyVisible = _growthStrategyVisible;
        bool wasRelicStrategyVisible = _relicStrategyVisible;
        bool wasCollapsed = _collapsed;
        bool wereDetailsVisible = _detailsVisible;
        SolverOverlayPresentation presentation = _presentation;
        bool wasWaitingForNextTurnPlan = _waitingForNextTurnPlan;
        CanvasLayer oldLayer = _layer;
        oldLayer.Visible = false;
        oldLayer.QueueFree();
        if (_viewport != null && GodotObject.IsInstanceValid(_viewport))
            _viewport.SizeChanged -= ApplyResponsiveLayout;
        _layer = null;
        _panel = null;
        _viewport = null;
        _rightResizeHandle = null;
        _bottomResizeHandle = null;
        _cornerResizeHandle = null;
        _resizing = false;
        _layoutQueued = false;
        _remainingLayoutPasses = 0;
        _renderedExecuteButtonStyle = null;
        _renderedAdoptRouteButtonStyle = null;
        _renderedTheftPolicy = null;

        if (_lastSnapshot is { } snapshot)
        {
            ShowResult(host, snapshot);
            if (presentation == SolverOverlayPresentation.Deploying)
                ShowDeploying(host, _lastDeploymentTurn, _lastDeploymentActionCount);
            else if (presentation == SolverOverlayPresentation.ExecutedHistory)
            {
                ShowDeploymentComplete(
                    host,
                    _lastDeploymentTurn,
                    _lastDeploymentActionCount,
                    _lastDeploymentEndedTurn);
                if (wasWaitingForNextTurnPlan)
                    ShowWaitingForNextTurnPlan(host);
            }
            if (SolverController.AutomaticSearchPaused)
                ShowSearchStopped(host);
        }
        else if (SolverController.SolverDisabled)
        {
            ShowDisabled(host);
        }
        else if (SolverController.AutomaticSearchPaused)
        {
            ShowSearchStopped(host);
        }
        else if (SolverController.IsSearching)
        {
            ShowSearching(
                host,
                _lastSearchingTurn,
                _lastSearchDeployWhenReady,
                _lastReviewedWorldlinesBeforeSearch);
        }
        else if (!SolverController.AutomaticCalculationEnabled)
        {
            ShowManualCalculationReady(host, SolverController.HasCalculatedThisCombat);
        }
        else
        {
            Show(host, _lastMessageText ?? SolverText.Get("界面主题已应用。"));
        }

        _settingsVisible = wasSettingsVisible;
        _potionStrategyVisible = wasPotionStrategyVisible;
        _growthStrategyVisible = wasGrowthStrategyVisible;
        _relicStrategyVisible = wasRelicStrategyVisible;
        if (wasSettingsVisible)
            _settingsPanel?.Reload();
        SetCollapsed(wasSettingsVisible ? false : wasCollapsed);
        if (!wasSettingsVisible && !wasCollapsed && wereDetailsVisible && _lastSnapshot != null)
            SetDetailsVisible(true);
        ApplyContentVisibility();
        ApplyOverlayOpacity();
        if (!wasVisible)
            Hide();
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_THEME_APPLIED theme={SolverSettings.Current.OverlayTheme} " +
            $"opacity={SolverSettings.Current.OverlayOpacity:0.##}");
    }

    private static void EnsureCreated(Node host)
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            return;
        Create(host);
    }

    private static void Create(Node host)
    {
        CanvasLayer layer = new()
        {
            Name = LayerName,
            Layer = 120,
        };
        PanelContainer panel = new()
        {
            Name = "Panel",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        panel.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            Background,
            Border,
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Md,
            shadow: true));

        VBoxContainer root = new()
        {
            Name = "Layout",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        root.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        panel.AddChild(root);

        root.AddChild(CreateHeader());
        root.AddChild(CreateSearchLimitHint());
        root.AddChild(CreatePerformanceHint());
        root.AddChild(CreateBossHpStrategyHint());

        HBoxContainer contentColumns = new()
        {
            Name = "ContentColumns",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        contentColumns.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        root.AddChild(contentColumns);

        VBoxContainer primaryColumn = new()
        {
            Name = "PrimaryColumn",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        primaryColumn.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        contentColumns.AddChild(primaryColumn);
        primaryColumn.AddChild(CreateFeedbackBanner());
        primaryColumn.AddChild(CreateDivider());

        VBoxContainer lowerStack = new()
        {
            Name = "ContentAndActions",
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        lowerStack.AddThemeConstantOverride("separation", 0);
        primaryColumn.AddChild(lowerStack);
        _mainStack = lowerStack;

        _settingsPanel = new SolverSettingsPanel
        {
            Visible = false,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _settingsPanel.ResetPositionRequested += ResetOverlayPosition;
        primaryColumn.AddChild(_settingsPanel);

        _potionStrategyPanel = new SolverPotionStrategyPanel();
        _growthStrategyPanel = new SolverGrowthStrategyPanel();
        _relicStrategyPanel = new SolverRelicStrategyPanel();
        _relicStrategyPanel.PolicyChanged += OnRelicPolicyChanged;
        _growthStrategyPanel.PolicyChanged += OnGrowthPolicyChanged;
        _growthStrategyPanel.BrightestFlameLimitChanged += OnBrightestFlameLimitChanged;
        _growthStrategyPanel.IgnoreLongTermRewardsChanged += OnIgnoreLongTermRewardsChanged;
        _potionStrategyPanel.DirectiveChanged += OnPotionDirectiveChanged;

        _body = new VBoxContainer
        {
            Name = "Body",
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _body.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        lowerStack.AddChild(_body);

        _body.AddChild(CreateSummarySection());
        _routeOutcomePanel = CreateSectionPanel("RouteOutcomePanel");
        _routeHeadingRow = new HFlowContainer
        {
            Alignment = FlowContainer.AlignmentMode.End,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _routeHeadingRow.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Md);
        _routeHeadingRow.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        _routeHeadingLabel = CreateTextLabel(
            SolverText.Get("推荐路线"),
            SolverUiTokens.Type.Body,
            TextPrimary,
            FontType.Bold);
        _routeHeadingLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _routeHeadingLabel.Visible = false;
        _routeHeadingRow.AddChild(_routeHeadingLabel);
        _deathOutcomeLabel = CreateTextLabel(
            SolverText.Get("未找到生还路线"),
            SolverUiTokens.Type.Body,
            Danger,
            FontType.Bold);
        _deathOutcomeLabel.Visible = false;
        _deathOutcomeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _routeHeadingRow.AddChild(_deathOutcomeLabel);
        _potionOutcomeLabel = CreateTextLabel(SolverText.Get("预计用1瓶药"), SolverUiTokens.Type.Body, Warning, FontType.Bold);
        _potionOutcomeLabel.Visible = false;
        _potionOutcomeLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _potionOutcomeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _routeHeadingRow.AddChild(_potionOutcomeLabel);
        _stolenResourceOutcomeLabel = CreateTextLabel(string.Empty, SolverUiTokens.Type.Body, Danger, FontType.Bold);
        _stolenResourceOutcomeLabel.Visible = false;
        _routeHeadingRow.AddChild(_stolenResourceOutcomeLabel);
        _strategyOutcomeRow = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.End, Visible = false };
        _strategyOutcomeRow.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Md);
        _routeHeadingRow.AddChild(_strategyOutcomeRow);
        _hpOutcomeLabel = CreateTextLabel(SolverText.Get("本局扣血  0 HP"), SolverUiTokens.Type.Metric, Success, FontType.Bold);
        _hpOutcomeLabel.AddThemeFontSizeOverride("font_size", 16);
        _hpOutcomeLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _hpOutcomeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _routeHeadingRow.AddChild(_hpOutcomeLabel);
        // Healing keeps its own label so it stays green while the loss label turns red.
        _hpRecoveredOutcomeLabel = CreateTextLabel(
            SolverText.Get("路线回血  0 HP"),
            SolverUiTokens.Type.Body,
            Success,
            FontType.Bold);
        _hpRecoveredOutcomeLabel.Visible = false;
        _hpRecoveredOutcomeLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _hpRecoveredOutcomeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _routeHeadingRow.AddChild(_hpRecoveredOutcomeLabel);
        HBoxContainer routeSummary = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        routeSummary.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        routeSummary.AddChild(CreateTextLabel(SolverText.Get("当前路线摘要"), SolverUiTokens.Type.Body, TextPrimary, FontType.Bold));
        routeSummary.AddChild(_routeHeadingRow);
        _routeOutcomePanel.AddChild(routeSummary);
        _body.AddChild(_routeOutcomePanel);
        _body.MoveChild(_routeOutcomePanel, 0);
        VBoxContainer routes = new()
        {
            Name = "Routes",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        routes.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        for (int index = 0; index < SolverWeights.UiTurnRows; index++)
        {
            SolverRouteRow row = new(index);
            RouteRows[index] = row;
            routes.AddChild(row);
        }
        _routeScroll = new ScrollContainer
        {
            Name = "RouteScroll",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.RouteViewportHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _routeScroll.AddChild(routes);
        _body.AddChild(_routeScroll);

        _detailsPanel = CreateSectionPanel("DetailsPanel");
        _detailsText = CreateRichText(SolverUiTokens.Type.Caption);
        _detailsText.FitContent = true;
        _detailsText.CustomMinimumSize = new Vector2(0, 56);
        _detailsPanel.AddChild(_detailsText);
        _detailsPanel.Visible = false;
        _body.AddChild(_detailsPanel);

        _footerDivider = CreateDivider();
        lowerStack.AddChild(_footerDivider);
        MarginContainer footerArea = new()
        {
            Name = "FooterArea",
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        footerArea.AddThemeConstantOverride("margin_top", SolverUiTokens.Spacing.Sm);
        footerArea.AddChild(CreateFooter());
        lowerStack.AddChild(footerArea);

        layer.AddChild(panel);
        layer.AddChild(_potionStrategyPanel);
        layer.AddChild(_growthStrategyPanel);
        layer.AddChild(_relicStrategyPanel);
        _rightResizeHandle = CreateResizeHandle("RightResizeHandle", ResizeEdge.Right);
        _bottomResizeHandle = CreateResizeHandle("BottomResizeHandle", ResizeEdge.Bottom);
        _cornerResizeHandle = CreateResizeHandle("CornerResizeHandle", ResizeEdge.BottomRight);
        layer.AddChild(_rightResizeHandle);
        layer.AddChild(_bottomResizeHandle);
        layer.AddChild(_cornerResizeHandle);
        host.AddChild(layer);
        _layer = layer;
        _panel = panel;
        ApplyOverlayOpacity();
        if (_viewport != null && GodotObject.IsInstanceValid(_viewport))
            _viewport.SizeChanged -= ApplyResponsiveLayout;
        _viewport = host.GetViewport();
        _viewport.SizeChanged += ApplyResponsiveLayout;
        panel.MinimumSizeChanged += QueueResponsiveLayout;
        _panelPosition = SolverSettings.OverlayPosition
            ?? new Vector2(SolverUiTokens.Size.PanelMargin, SolverUiTokens.Size.PanelMargin);
        _customPanelSize = SolverSettings.OverlaySize;
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_POSITION_LOADED persisted={SolverSettings.OverlayPosition.HasValue} " +
            $"x={_panelPosition.X:F1} y={_panelPosition.Y:F1} " +
            $"size_persisted={_customPanelSize.HasValue} " +
            $"w={_customPanelSize?.X.ToString("F1") ?? "-"} " +
            $"h={_customPanelSize?.Y.ToString("F1") ?? "-"}");
        _dragging = false;
        _resizing = false;
        _settingsVisible = false;
        _potionStrategyVisible = false;
        _growthStrategyVisible = false;
        SetCollapsed(false);
        _relicStrategyVisible = false;
        Entry.Logger.Info("[CombatSolver/Test] UI_CREATE responsive=true content_fit_height=true minimum_size_reflow=true draggable=true drag_coordinates=viewport drag_relayout=release_only resizable=right+bottom+corner resize_grip=three_diagonal_lines size_persisted=true route_scroll_expand=true max_width=viewport max_height=viewport route_row_height=44 route_viewport_height=148 visible_unwrapped_route_rows=3 cached_route_rows=16 all_searched_turns=true route_scroll=true persistent_status_card=true compact_title=true compact_footer=true collapsed_action_buttons=true footer_pause_toggles=false settings_pause_toggles=true footer_top_margin=8 details_in_status_row=true battle_hp_in_route_heading=true sold_hp_summary=false three_column_routes=true semantic_action_pills=true full_target_names=true whole_pill_kill_highlight=true text_outline_px=2 wrapped_summary=true summary_bold_metric=true flat_collapse=true plain_details_button=true full_auto_positive_toggle=true no_middle_dot=true status_badge=true plain_action_buttons=true always_show_energy=true plain_route_heading=true settings_button=true settings_persisted=true settings_tabs=general+performance+feedback performance_advanced=collapsed notification_policy=three_state performance_presets=low+medium+high+very_high+custom kill_pill=green_with_target_names status_badge=content_width deployment_speed_settings=true search_status=fixed_columns_seconds only_death_marker=true relic_action_labels=true position_persisted=true theft_policy_buttons=contextual stop_search_button=true");
        Entry.Logger.Info("[CombatSolver/Test] UI_FEEDBACK_BANNER position=full_width manual_improvement=green unexpected_replan=red export_prompt=full_bug_report");
    }

    private static Control CreateHeader()
    {
        HBoxContainer header = new()
        {
            Name = "Header",
            CustomMinimumSize = new Vector2(0, 32),
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Move,
        };
        header.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        header.GuiInput += OnHeaderGuiInput;

        Control marker;
        if (SolverUiTokens.IsLightTheme)
        {
            PanelContainer icon = new()
            {
                Name = "AppIcon",
                CustomMinimumSize = new Vector2(16, 16),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            icon.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
                Accent,
                Accent,
                SolverUiTokens.Radius.Small,
                0,
                0,
                borderWidth: 0));
            marker = icon;
        }
        else
        {
            marker = new ColorRect
            {
                Color = Accent,
                CustomMinimumSize = new Vector2(4, 24),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
        }
        header.AddChild(marker);

        Label title = CreateTextLabel(SolverText.Get("战斗路线求解器"), SolverUiTokens.Type.Title, TextPrimary, FontType.Bold);
        title.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        header.AddChild(title);

        _solverEnabledButton = CreateHeaderButton(SolverText.Get("求解器：开"), 90);
        _solverEnabledButton.Pressed += () => SolverController.SetSolverDisabled(!SolverController.SolverDisabled);
        header.AddChild(_solverEnabledButton);

        Control spacer = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        header.AddChild(spacer);

        _potionStrategyButton = CreateHeaderButton(SolverText.Get("药水"), 48);
        _potionStrategyButton.Pressed += TogglePotionStrategy;
        header.AddChild(_potionStrategyButton);
        _growthStrategyButton = CreateHeaderButton(SolverText.Get("成长"), 48);
        _growthStrategyButton.Pressed += ToggleGrowthStrategy;
        header.AddChild(_growthStrategyButton);
        _relicStrategyButton = CreateHeaderButton(SolverText.Get("遗物"), 48);
        _relicStrategyButton.Pressed += ToggleRelicStrategy;
        header.AddChild(_relicStrategyButton);

        _settingsButton = CreateHeaderButton(SolverText.Get("设置"), 54);
        _settingsButton.Pressed += ToggleSettings;
        if (SolverUiTokens.IsLightTheme)
        {
            _settingsButton.AddThemeColorOverride("font_color", Accent);
            _settingsButton.AddThemeColorOverride("font_hover_color", Accent);
            _settingsButton.AddThemeColorOverride("font_pressed_color", Accent);
        }
        header.AddChild(_settingsButton);

        _collapseButton = CreateHeaderButton(SolverText.Get("−  收起"), 54);
        _collapseButton.Pressed += ToggleCollapsed;
        if (SolverUiTokens.IsLightTheme)
        {
            _collapseButton.AddThemeColorOverride("font_color", Danger);
            _collapseButton.AddThemeColorOverride("font_hover_color", Danger);
            _collapseButton.AddThemeColorOverride("font_pressed_color", Danger);
        }
        header.AddChild(_collapseButton);

        return header;
    }

    private static Control CreateResizeHandle(string name, ResizeEdge edge)
    {
        Control handle = edge == ResizeEdge.BottomRight
            ? new TextureRect
            {
                Texture = SolverUiTokens.CreateResizeGripTexture(
                    new Color(SolverUiTokens.Palette.TextMuted, 0.88f)),
                StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            }
            : new Control();
        handle.Name = name;
        handle.MouseFilter = Control.MouseFilterEnum.Stop;
        handle.ZIndex = 1;
        handle.MouseDefaultCursorShape = edge switch
        {
            ResizeEdge.Right => Control.CursorShape.Hsize,
            ResizeEdge.Bottom => Control.CursorShape.Vsize,
            _ => Control.CursorShape.Fdiagsize,
        };
        handle.GuiInput += inputEvent => OnResizeHandleGuiInput(inputEvent, edge);
        return handle;
    }

    private static Control CreateFeedbackBanner()
    {
        _feedbackBanner = new PanelContainer
        {
            Name = "FeedbackBanner",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _feedbackBannerLabel = CreateTextLabel(
            string.Empty,
            SolverUiTokens.Type.Body,
            Success,
            FontType.Bold);
        _feedbackBannerLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _feedbackBannerLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _feedbackBanner.AddChild(_feedbackBannerLabel);
        return _feedbackBanner;
    }

    private static Button CreateHeaderButton(string text, float minimumWidth)
    {
        Button button = SolverUiTokens.CreateButton(text, SolverButtonStyle.Secondary);
        button.CustomMinimumSize = new Vector2(minimumWidth, SolverUiTokens.Size.ButtonHeight);
        button.ApplyLocaleFontSubstitution(FontType.Bold, "font");
        return button;
    }

    private static Control CreatePerformanceHint()
    {
        _performanceHintButton = SolverUiTokens.CreateButton(
            SolverText.Get("本场战斗出现战损，若对结果不满意可以前往 设置 > 性能，将性能预设调为高或极高后重试。点击本消息之后不再提示"),
            SolverButtonStyle.Secondary);
        _performanceHintButton.Name = "PerformanceHint";
        _performanceHintButton.Visible = false;
        _performanceHintButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _performanceHintButton.CustomMinimumSize = new Vector2(0, 44);
        _performanceHintButton.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _performanceHintButton.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? Warning.Lightened(0.82f) : Warning.Darkened(0.78f),
            Warning,
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        _performanceHintButton.AddThemeStyleboxOverride("hover", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? Warning.Lightened(0.72f) : Warning.Darkened(0.68f),
            Warning.Lightened(0.12f),
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        _performanceHintButton.Pressed += DismissPerformanceHint;
        return _performanceHintButton;
    }

    private static Control CreateSearchLimitHint()
    {
        _searchLimitHint = new PanelContainer
        {
            Name = "SearchLimitHint",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 44),
        };
        _searchLimitHint.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? Warning.Lightened(0.82f) : Warning.Darkened(0.78f),
            Warning,
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        _searchLimitHintLabel = CreateTextLabel(
            string.Empty,
            SolverUiTokens.Type.Body,
            Warning,
            FontType.Bold);
        _searchLimitHintLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _searchLimitHintLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _searchLimitHint.AddChild(_searchLimitHintLabel);
        return _searchLimitHint;
    }

    private static Control CreateBossHpStrategyHint()
    {
        _bossHpStrategyHintButton = SolverUiTokens.CreateButton(
            string.Empty,
            SolverButtonStyle.Secondary);
        _bossHpStrategyHintButton.Name = "BossHpStrategyHint";
        _bossHpStrategyHintButton.Visible = false;
        _bossHpStrategyHintButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _bossHpStrategyHintButton.CustomMinimumSize = new Vector2(0, 44);
        _bossHpStrategyHintButton.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _bossHpStrategyHintButton.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? Accent.Lightened(0.84f) : Accent.Darkened(0.78f),
            Accent,
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        _bossHpStrategyHintButton.AddThemeStyleboxOverride("hover", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme ? Accent.Lightened(0.74f) : Accent.Darkened(0.68f),
            Accent.Lightened(0.12f),
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Md,
            SolverUiTokens.Spacing.Xxs));
        _bossHpStrategyHintButton.Pressed += DismissBossHpStrategyHint;
        return _bossHpStrategyHintButton;
    }

    private static Control CreateSummarySection()
    {
        const int summaryFontSize = 16;
        _summaryPanel = CreateSectionPanel("SummaryPanel");
        _summaryPanel.MouseFilter = Control.MouseFilterEnum.Pass;
        _summaryPanel.CustomMinimumSize = Vector2.Zero;
        VBoxContainer layout = new() { MouseFilter = Control.MouseFilterEnum.Pass };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        HBoxContainer statusRow = new()
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        statusRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        HFlowContainer statisticsRow = new()
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        statisticsRow.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Md);
        statisticsRow.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);
        _summaryStatusBadge = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(0, 26),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        _summaryStateLabel = CreateTextLabel(
            SolverText.Get("等待战斗状态"),
            summaryFontSize,
            TextMuted,
            FontType.Bold);
        _summaryStatusBadge.AddChild(_summaryStateLabel);
        statusRow.AddChild(_summaryStatusBadge);
        _summaryContextLabel = CreateTextLabel(
            string.Empty,
            summaryFontSize,
            SolverUiTokens.Palette.TextSecondary,
            FontType.Bold);
        _summaryContextLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        _summaryContextLabel.CustomMinimumSize = new Vector2(0, 24);
        _summaryContextLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        _summaryContextLabel.Visible = false;
        statisticsRow.AddChild(_summaryContextLabel);
        _summaryText = CreateRichText(summaryFontSize);
        _summaryText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _summaryText.FitContent = true;
        _summaryText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _summaryText.CustomMinimumSize = new Vector2(0, 24);
        _summaryText.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _summaryText.ApplyLocaleFontSubstitution(FontType.Bold, "normal_font");
        _progressText = CreateTextLabel(string.Empty, summaryFontSize, TextPrimary, FontType.Bold);
        _progressText.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        _progressText.CustomMinimumSize = new Vector2(0, 24);
        _progressText.AutowrapMode = TextServer.AutowrapMode.Off;
        _progressText.Visible = false;
        statisticsRow.AddChild(_progressText);
        _reviewText = CreateTextLabel(
            string.Empty,
            summaryFontSize,
            SolverUiTokens.Palette.TextSecondary,
            FontType.Bold);
        _reviewText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _reviewText.CustomMinimumSize = new Vector2(0, 24);
        _reviewText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _reviewText.Visible = false;
        _detailsButton = new SolverDetailsButton
        {
            Visible = false,
        };
        _detailsButton.Pressed += ToggleDetails;
        MarginContainer detailsSlot = new()
        {
            CustomMinimumSize = new Vector2(0, 24),
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
        };
        detailsSlot.AddChild(_detailsButton);
        detailsSlot.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        statusRow.AddChild(_summaryText);
        statusRow.AddChild(detailsSlot);
        layout.AddChild(statusRow);
        statisticsRow.AddChild(_reviewText);
        layout.AddChild(statisticsRow);
        _searchProgressBar = new ProgressBar
        {
            Name = "SearchProgress",
            MinValue = 0,
            MaxValue = SolverSearchProfile.Default.MaxExpandedNodes,
            Value = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 4),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _searchProgressBar.AddThemeStyleboxOverride("background",
            SolverUiTokens.CreateBox(
                SolverUiTokens.Palette.ProgressBackground,
                SolverUiTokens.IsLightTheme ? Colors.Transparent : SolverUiTokens.Palette.BorderSubtle,
                SolverUiTokens.Radius.Small,
                0,
                0,
                borderWidth: SolverUiTokens.IsLightTheme ? 0 : 1));
        _searchProgressBar.AddThemeStyleboxOverride("fill",
            SolverUiTokens.CreateBox(
                SolverUiTokens.Palette.ProgressFill,
                Accent,
                SolverUiTokens.Radius.Small,
                0,
                0));
        layout.AddChild(_searchProgressBar);
        _summaryPanel.AddChild(layout);
        return _summaryPanel;
    }

    private static Control CreateFooter()
    {
        _theftPolicyControls = new HBoxContainer
        {
            Name = "TheftPolicy",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _theftPolicyControls.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        _preserveResourcesButton = CreateButton(SolverText.Get("保牌/保钱"), false);
        _preserveResourcesButton.CustomMinimumSize = new Vector2(112, SolverUiTokens.Size.ButtonHeight);
        _preserveResourcesButton.Pressed += () => OnTheftPolicyPressed(SolverTheftPolicy.PreserveResources);
        _theftPolicyControls.AddChild(_preserveResourcesButton);
        _letEscapeButton = CreateButton(SolverText.Get("放走"), false);
        _letEscapeButton.CustomMinimumSize = new Vector2(72, SolverUiTokens.Size.ButtonHeight);
        _letEscapeButton.Pressed += () => OnTheftPolicyPressed(SolverTheftPolicy.LetEscape);
        _theftPolicyControls.AddChild(_letEscapeButton);
        _body!.AddChild(_theftPolicyControls);
        _body.MoveChild(_theftPolicyControls, 2);

        _recalculateButton = CreateButton(SolverText.Get("重新计算"), false);
        _recalculateButton.CustomMinimumSize = new Vector2(112, SolverUiTokens.Size.ButtonHeight);
        _recalculateButton.Pressed += OnRecalculatePressed;

        _stopSearchButton = CreateButton(SolverText.Get("停止计算"), false);
        SolverUiTokens.ApplyButtonStyle(_stopSearchButton, SolverButtonStyle.Danger);
        _stopSearchButton.CustomMinimumSize = new Vector2(112, SolverUiTokens.Size.ButtonHeight);
        _stopSearchButton.Pressed += OnStopSearchPressed;

        _adoptRouteButton = CreateButton(SolverText.Get("采用当前路线"), false);
        _adoptRouteButton.CustomMinimumSize = new Vector2(132, SolverUiTokens.Size.ButtonHeight);
        _adoptRouteButton.Pressed += OnAdoptRoutePressed;
        _adoptRouteButton.TooltipText = SolverText.Get("结束搜索并采用屏幕当前路线；已开启的全自动或排队执行仍按原设置继续。");

        _executeButton = CreateButton(SolverText.Get("执行本回合"), false);
        _renderedExecuteButtonStyle = SolverButtonStyle.Secondary;
        _executeButton.CustomMinimumSize = new Vector2(132, SolverUiTokens.Size.ButtonHeight);
        _executeButton.Pressed += OnExecutePressed;

        _fullAutoButton = SolverUiTokens.CreateButton(SolverText.Get("全自动：关"), SolverButtonStyle.Secondary);
        _fullAutoButton.CustomMinimumSize = new Vector2(144, SolverUiTokens.Size.ButtonHeight);
        _fullAutoButton.TooltipText = SolverText.Get("控制本场自动续打。关闭后，正在执行的动作按原流程完成。");
        _fullAutoButton.Pressed += OnFullAutoPressed;

        HBoxContainer autoStart = new()
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            TooltipText = SolverText.Get("每场战斗开始时自动开启全自动。本场手动停止后保持停止，下场战斗再次开启。"),
        };
        autoStart.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        autoStart.AddChild(CreateTextLabel(SolverText.Get("自动开启全自动"), SolverUiTokens.Type.Caption, TextPrimary));
        _autoEnableFullAutoSwitch = SolverSettingsPanel.CreateToggle();
        _autoEnableFullAutoSwitch.CustomMinimumSize = new Vector2(40, 24);
        _autoEnableFullAutoSwitch.TooltipText = autoStart.TooltipText;
        _autoEnableFullAutoSwitch.ButtonPressed = SolverSettings.Current.AutoEnableFullAuto;
        _autoEnableFullAutoSwitch.Toggled += enabled =>
        {
            if (SolverSettings.Current.AutoEnableFullAuto != enabled)
                SolverSettings.Update(SolverSettings.Current with { AutoEnableFullAuto = enabled });
        };
        autoStart.AddChild(_autoEnableFullAutoSwitch);

        _memoryUsageBar = new SolverMemoryUsageBar
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        _systemMemoryReleaseButton = SolverUiTokens.CreateButton(SolverText.Get("强制释放内存"), SolverButtonStyle.Secondary);
        _systemMemoryReleaseButton.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        _systemMemoryReleaseButton.TooltipText = SolverText.Get("等待搜索退出并回收求解器内存后，请求 Windows 管理员权限，")
            + SolverText.Get("清空系统工作集与待机列表。其他程序之后重新载入页面时可能短暂卡顿。");
        _systemMemoryReleaseButton.Pressed += OnSystemMemoryReleasePressed;

        _actionBar = new SolverActionBar(_executeButton, _recalculateButton, _stopSearchButton,
            _adoptRouteButton, _fullAutoButton, autoStart, _memoryUsageBar, _systemMemoryReleaseButton);
        return _actionBar;
    }

    private static async void OnSystemMemoryReleasePressed()
    {
        if (_systemMemoryReleaseButton is not { Disabled: false } button)
            return;

        button.Disabled = true;
        button.Text = SolverText.Get("正在释放内存…");
        Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=manual_system_memory_release");
        SetStatus(SolverText.Get("系统内存释放已安排，搜索退出后将请求管理员权限"), Success);
        try
        {
            await SystemMemoryReleaseService.ReleaseAsync();
            SetStatus(SolverText.Get("系统内存释放完成"), Success);
        }
        catch (OperationCanceledException)
        {
            SetStatus(SolverText.Get("已取消管理员授权"), TextMuted);
        }
        catch (Exception ex)
        {
            Entry.Logger.Error(
                $"[CombatSolver/Test] SYSTEM_MEMORY_RELEASE_FAILED exception={ex}");
            SetStatus(SolverText.Get("系统内存释放失败，详情已写入日志"), Danger);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(button))
            {
                button.Disabled = false;
                button.Text = SolverText.Get("强制释放内存");
            }
        }
    }

    private static PanelContainer CreateSectionPanel(string name)
    {
        PanelContainer panel = new()
        {
            Name = name,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        panel.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            Surface,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Sm));
        return panel;
    }

    private static Label CreateTextLabel(
        string text,
        int size,
        Color color,
        FontType fontType = FontType.Regular)
    {
        return SolverUiTokens.CreateLabel(text, size, color, fontType);
    }

    private static RichTextLabel CreateRichText(int size)
    {
        return SolverUiTokens.CreateRichText(size);
    }

    private static Button CreateButton(string text, bool primary)
    {
        return SolverUiTokens.CreateButton(text, primary ? SolverButtonStyle.Primary : SolverButtonStyle.Secondary);
    }

    private static ColorRect CreateDivider() => new()
    {
        Color = SolverUiTokens.Palette.BorderSubtle,
        CustomMinimumSize = new Vector2(0, 1),
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static void SetStatus(string text, Color color, string context = "")
    {
        if (_summaryStateLabel == null)
            return;
        _summaryStateLabel.Text = text;
        _summaryStateLabel.AddThemeColorOverride("font_color", color);
        if (_summaryStatusBadge != null)
        {
            _summaryStatusBadge.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
                SolverUiTokens.IsLightTheme ? color.Lightened(0.86f) : color.Darkened(0.76f),
                SolverUiTokens.IsLightTheme ? new Color(color, 0.5f) : color.Darkened(0.18f),
                SolverUiTokens.Radius.Pill,
                horizontalPadding: SolverUiTokens.Spacing.Sm,
                verticalPadding: 2));
        }
        if (_summaryContextLabel != null)
        {
            _summaryContextLabel.Text = context;
            _summaryContextLabel.Visible = !string.IsNullOrEmpty(context);
        }
    }

    private static void SetMessageContent(string text)
    {
        if (_summaryPanel != null)
            _summaryPanel.Visible = true;
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverUiTokens.AdaptRichTextToActiveTheme(text);
        }
        if (_progressText != null)
            _progressText.Visible = false;
        if (_searchProgressBar != null)
            _searchProgressBar.Visible = false;
        SetRouteVisibility(false);
        if (_detailsPanel != null)
            _detailsPanel.Visible = false;
        if (_detailsButton != null)
            _detailsButton.Visible = false;
        SetDetailsVisible(false);
    }

    private static void RefreshFeedbackBanner()
    {
        if (_feedbackBanner == null || _feedbackBannerLabel == null)
            return;

        string? text;
        Color tone;
        if (SolverController.ManualRouteImprovementDetected)
        {
            text = SolverText.Get("你打出了比求解器更好的世界线。") +
                   SolverUiTokens.BugReportUploadInstruction +
                   SolverText.Get("这可以更好地推动算法进步！");
            tone = Success;
        }
        else if (SolverController.UnexpectedReplanCount > 0)
        {
            text = SolverText.Get("出现计划外重算，可能是模拟或算法问题。") +
                   SolverUiTokens.BugReportUploadInstruction;
            tone = Danger;
        }
        else if (SolverController.BugReportUploadRecommended)
        {
            text = SolverText.Get("求解器记录到需要反馈的异常。") +
                   SolverUiTokens.BugReportUploadInstruction;
            tone = Danger;
        }
        else
        {
            text = null;
            tone = TextMuted;
        }

        if (LoadedModWarnings.SpeedXPresent)
        {
            string notice = SolverText.Get("检测到皮皮极速（SpeedX）：其悬浮显示可能持续产生大量临时内存，触发频繁回收，加重长时间游玩时的卡顿。\n需要战斗加速时，建议使用求解器自带的“瞬间”：设置 > 常规 > 自动执行，将“自动出牌速度”设为“瞬间”，“牌间额外停顿（秒）”设为 0。玩家和怪物回合均加速，局外保持原速。");
            text = text == null ? notice : notice + "\n" + text;
            if (tone != Danger)
                tone = Warning;
        }

        if (OnlinePresence.AvailableUpdateVersion is { } latestVersion)
        {
            string notice = SolverText.Format($"求解器新版本 {latestVersion} 已发布，请更新 Mod 后重启游戏。");
            text = text == null ? notice : notice + "\n" + text;
            if (tone != Danger)
                tone = Warning;
        }

        bool visibilityChanged = _feedbackBanner.Visible != (text != null);
        _feedbackBanner.Visible = text != null;
        if (text != null)
        {
            _feedbackBannerLabel.Text = text;
            _feedbackBannerLabel.AddThemeColorOverride("font_color", tone);
            _feedbackBanner.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
                SolverUiTokens.IsLightTheme ? tone.Lightened(0.86f) : tone.Darkened(0.78f),
                SolverUiTokens.IsLightTheme ? new Color(tone, 0.45f) : tone.Darkened(0.12f),
                SolverUiTokens.Radius.Medium,
                SolverUiTokens.Spacing.Md,
                SolverUiTokens.Spacing.Sm));
        }
        if (visibilityChanged)
            QueueResponsiveLayout();
    }

    private static void SetRouteVisibility(bool visible)
    {
        if (_routeHeadingRow != null)
            _routeHeadingRow.Visible = visible;
        if (_routeOutcomePanel != null)
            _routeOutcomePanel.Visible = visible;
        for (int index = 0; index < SolverWeights.UiTurnRows; index++)
            SetRouteRowVisible(index, visible);
        QueueResponsiveLayout();
    }

    private static void SetRouteRowVisible(int index, bool visible)
    {
        if (RouteRows[index] != null)
            RouteRows[index].Visible = visible;
    }

    private static void ShowLayer()
    {
        if (_layer != null)
            _layer.Visible = true;
    }

    private static void ToggleCollapsed()
    {
        SetCollapsed(!_collapsed);
        Entry.Logger.Info($"[CombatSolver/Test] UI_ACTION action=collapse collapsed={_collapsed}");
    }

    private static void ToggleSettings()
    {
        _relicStrategyVisible = false;
        if (_settingsVisible && _settingsPanel?.CommitPending() == false)
            return;
        if (_collapsed)
            SetCollapsed(false);
        _settingsVisible = !_settingsVisible;
        if (_settingsVisible)
            _potionStrategyVisible = false;
        if (_settingsVisible)
            _growthStrategyVisible = false;
        if (_settingsVisible)
            _settingsPanel?.Reload();
        ApplyContentVisibility();
        SetPerformanceHintVisible(
            !_settingsVisible
            && _lastSnapshot is
            {
                ProjectedBattleHpLossKnown: true,
                ProjectedBattleHpLost: > 0,
            });
        QueueResponsiveLayout();
        Entry.Logger.Info($"[CombatSolver/Test] UI_ACTION action=settings visible={_settingsVisible}");
    }

    private static void TogglePotionStrategy()
    {
        _relicStrategyVisible = false;
        if (_settingsVisible && _settingsPanel?.CommitPending() == false)
            return;
        if (_collapsed)
            SetCollapsed(false);
        _settingsVisible = false;
        _potionStrategyVisible = !_potionStrategyVisible;
        _growthStrategyVisible = false;
        _potionStrategyPanel?.Invalidate();
        RefreshControls();
        ApplyContentVisibility();
        SetPerformanceHintVisible(
            _lastSnapshot is
            {
                ProjectedBattleHpLossKnown: true,
                ProjectedBattleHpLost: > 0,
            });
        QueueResponsiveLayout();
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_ACTION action=potion_strategy visible={_potionStrategyVisible}");
    }

    private static void DismissPerformanceHint()
    {
        SolverSettings.Update(SolverSettings.Current with
        {
            ShowBattleDamagePerformanceHint = false,
        });
        SetPerformanceHintVisible(false);
        Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=performance_hint_dismissed");
    }

    private static void DismissBossHpStrategyHint()
    {
        SolverSettingsData data = SolverSettings.Current;
        SolverSettingsData updated = _activeBossHpRelief switch
        {
            BossHpRelief.ActClearHeal => data with
            {
                ShowActTransitionBossHpStrategyHint = false,
            },
            BossHpRelief.RunEnding => data with
            {
                ShowFinalBossHpStrategyHint = false,
            },
            _ => data,
        };
        if (!ReferenceEquals(updated, data))
            SolverSettings.Update(updated);
        RefreshBossHpStrategyHint();
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_ACTION action=boss_hp_strategy_hint_dismissed " +
            $"relief={_activeBossHpRelief}");
    }

    private static void SetCurrentBossHpStrategyHint()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        SetBossHpStrategyHint(
            state != null && CombatManager.Instance.IsInProgress
                ? ActEndingBossPolicy.ResolveHpRelief(state)
                : BossHpRelief.None);
    }

    private static void SetBossHpStrategyHint(BossHpRelief relief)
    {
        _activeBossHpRelief = relief;
        RefreshBossHpStrategyHint();
    }

    public static void RefreshBossHpStrategyHint()
    {
        if (_bossHpStrategyHintButton == null)
            return;
        SolverSettingsData settings = SolverSettings.Current;
        bool enabled = _activeBossHpRelief switch
        {
            BossHpRelief.ActClearHeal => settings.ShowActTransitionBossHpStrategyHint,
            BossHpRelief.RunEnding => settings.ShowFinalBossHpStrategyHint,
            _ => false,
        };
        bool visible = enabled && !_collapsed && !_settingsVisible;
        string text = _activeBossHpRelief switch
        {
            BossHpRelief.ActClearHeal when settings.ActTransitionBossHpStrategy
                == BossHpStrategy.MinimizeHpLoss
                => SolverText.Get("本场为第一、二幕的幕末 Boss 战。当前选择最低战损，不折算战后回复；可在 设置 > 常规 > 幕末 Boss 中切换，重新计算后生效。点击本消息后不再提示"),
            BossHpRelief.ActClearHeal
                => SolverText.Get("本场为第一、二幕的幕末 Boss 战。当前选择通关优先，按战后回复 80% 折算血量并优先保留药水；可在 设置 > 常规 > 幕末 Boss 中切换，重新计算后生效。点击本消息后不再提示"),
            BossHpRelief.RunEnding when settings.FinalBossHpStrategy
                == BossHpStrategy.MinimizeHpLoss
                => SolverText.Get("本场为最终 Boss 战。当前选择最低战损，会继续比较路线剩余血量；可在 设置 > 常规 > 幕末 Boss 中切换，重新计算后生效。点击本消息后不再提示"),
            BossHpRelief.RunEnding
                => SolverText.Get("本场为最终 Boss 战。当前选择通关优先，路线存活后优先保留资源；可在 设置 > 常规 > 幕末 Boss 中切换，重新计算后生效。点击本消息后不再提示"),
            _ => string.Empty,
        };
        bool changed = _bossHpStrategyHintButton.Visible != visible
            || !string.Equals(_bossHpStrategyHintButton.Text, text, StringComparison.Ordinal);
        _bossHpStrategyHintButton.Text = text;
        _bossHpStrategyHintButton.Visible = visible;
        if (changed)
            QueueResponsiveLayout();
    }

    private static void SetReviewText(string? text)
    {
        if (_reviewText == null)
            return;
        _reviewText.Visible = !string.IsNullOrEmpty(text);
        _reviewText.Text = text ?? string.Empty;
    }

    private static void SetPerformanceHintVisible(bool visible)
    {
        visible &= SolverSettings.Current.ShowBattleDamagePerformanceHint;
        if (_performanceHintButton == null || _performanceHintButton.Visible == visible)
            return;
        _performanceHintButton.Visible = visible;
        QueueResponsiveLayout();
    }

    private static void SetSearchLimitHint(string? text)
    {
        if (_searchLimitHint == null || _searchLimitHintLabel == null)
            return;
        bool visible = !string.IsNullOrEmpty(text);
        bool changed = _searchLimitHint.Visible != visible
            || !string.Equals(_searchLimitHintLabel.Text, text, StringComparison.Ordinal);
        _searchLimitHintLabel.Text = text ?? string.Empty;
        _searchLimitHint.Visible = visible;
        if (changed)
            QueueResponsiveLayout();
    }

    private static void ToggleDetails()
    {
        SetDetailsVisible(!_detailsVisible);
        Entry.Logger.Info($"[CombatSolver/Test] UI_ACTION action=calculation_details visible={_detailsVisible}");
    }

    private static void SetDetailsVisible(bool visible)
    {
        _detailsVisible = visible;
        if (_detailsPanel != null)
            _detailsPanel.Visible = visible;
        if (_detailsText != null)
            _detailsText.Visible = visible;
        if (_detailsButton != null)
            _detailsButton.SetExpanded(visible);
        if (_routeScroll != null)
        {
            _routeScroll.CustomMinimumSize = new Vector2(
                0,
                visible
                    ? SolverUiTokens.Size.RouteViewportHeightWithDetails
                    : SolverUiTokens.Size.RouteViewportHeight);
        }
        QueueResponsiveLayout();
    }

    private static void QueueResponsiveLayout()
    {
        _remainingLayoutPasses = 2;
        if (_layoutQueued)
            return;
        _layoutQueued = true;
        Callable.From(ApplyResponsiveLayoutDeferred).CallDeferred();
    }

    private static void ApplyResponsiveLayoutDeferred()
    {
        ApplyResponsiveLayout();
        if (--_remainingLayoutPasses > 0 && _panel != null && GodotObject.IsInstanceValid(_panel))
        {
            Callable.From(ApplyResponsiveLayoutDeferred).CallDeferred();
            return;
        }
        _layoutQueued = false;
        _remainingLayoutPasses = 0;
    }

    private static void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        if (_collapseButton != null)
            _collapseButton.Text = collapsed ? SolverText.Get("+  展开") : SolverText.Get("−  收起");
        ApplyContentVisibility();
        ApplyResponsiveLayout();
    }

    private static void ApplyContentVisibility()
    {
        _actionBar?.Refresh(new SolverActionBarState(_collapsed, SolverController.IsSearching,
            SolverController.CanAdoptCurrentRoute || SolverController.IsAdoptingCurrentRoute));
        if (_potionStrategyButton != null)
            _potionStrategyButton.Visible = !_collapsed;
        if (_growthStrategyButton != null)
            _growthStrategyButton.Visible = !_collapsed;
        if (_relicStrategyButton != null) _relicStrategyButton.Visible = !_collapsed;
        if (_settingsButton != null)
            _settingsButton.Visible = !_collapsed;
        // Keep the same outcome controls and presentation state in both layouts.
        if (_routeOutcomePanel != null && _mainStack != null && _body != null)
        {
            Node parent = _collapsed ? _mainStack : _body;
            if (_routeOutcomePanel.GetParent() != parent)
            {
                _routeOutcomePanel.Reparent(parent);
                parent.MoveChild(_routeOutcomePanel, 0);
            }
        }
        if (_mainStack != null)
            _mainStack.Visible = !_settingsVisible || _collapsed;
        if (_body != null)
            _body.Visible = !_collapsed && !_settingsVisible;
        if (_rightResizeHandle != null)
            _rightResizeHandle.Visible = !_collapsed;
        if (_bottomResizeHandle != null)
            _bottomResizeHandle.Visible = !_collapsed;
        if (_cornerResizeHandle != null)
            _cornerResizeHandle.Visible = !_collapsed;
        if (_footerDivider != null)
            _footerDivider.Visible = !_collapsed && !_settingsVisible;
        if (_settingsPanel != null)
            _settingsPanel.Visible = !_collapsed && _settingsVisible;
        if (_potionStrategyPanel != null)
            _potionStrategyPanel.Visible = !_collapsed && !_settingsVisible && _potionStrategyVisible;
        if (_growthStrategyPanel != null)
            _growthStrategyPanel.Visible = !_collapsed && !_settingsVisible && _growthStrategyVisible;
        if (_relicStrategyPanel != null)
            _relicStrategyPanel.Visible = !_collapsed && !_settingsVisible && _relicStrategyVisible;
        RefreshBossHpStrategyHint();
        if (_settingsButton != null)
        {
            _settingsButton.Text = _settingsVisible ? SolverText.Get("返回") : SolverText.Get("设置");
            _settingsButton.AddThemeColorOverride(
                "font_color",
                _settingsVisible || SolverUiTokens.IsLightTheme
                    ? Accent
                    : SolverUiTokens.Palette.TextSecondary);
        }
        if (_potionStrategyButton != null)
        {
            _potionStrategyButton.AddThemeColorOverride(
                "font_color",
                _potionStrategyVisible || SolverUiTokens.IsLightTheme
                    ? Accent
                    : SolverUiTokens.Palette.TextSecondary);
        }
    }

    private static void ApplyResponsiveLayout()
    {
        if (_panel == null || _viewport == null
            || !GodotObject.IsInstanceValid(_panel)
            || !GodotObject.IsInstanceValid(_viewport))
        {
            return;
        }

        Vector2 viewportSize = _viewport.GetVisibleRect().Size;
        float edge = SolverUiTokens.Size.ResizeEdgeThickness;
        float availableWidth = Math.Max(0f, viewportSize.X - edge * 2f);
        float availableHeight = Math.Max(0f, viewportSize.Y - edge * 2f);
        Vector2 contentMinimum = _panel.GetCombinedMinimumSize();
        float defaultPrimaryWidth = Math.Min(
            SolverUiTokens.Size.ExpandedMaxWidth,
            Math.Max(SolverUiTokens.Size.ExpandedMinWidth, viewportSize.X * 0.58f));
        float minimumWidth = Math.Min(
            Math.Max(SolverSettings.MinimumOverlayWidth, contentMinimum.X),
            availableWidth);
        float width = _collapsed
            ? Math.Clamp(
                SolverUiTokens.Size.CollapsedWidth,
                Math.Min(contentMinimum.X, availableWidth),
                availableWidth)
            : Math.Clamp(_customPanelSize?.X ?? defaultPrimaryWidth, minimumWidth, availableWidth);

        float height;
        if (_collapsed)
        {
            height = Math.Clamp(
                SolverUiTokens.Size.CollapsedHeight,
                Math.Min(contentMinimum.Y, availableHeight),
                availableHeight);
        }
        else if (_customPanelSize is { } customSize)
        {
            float minimumHeight = Math.Min(
                Math.Max(SolverSettings.MinimumOverlayHeight, contentMinimum.Y),
                availableHeight);
            height = Math.Clamp(customSize.Y, minimumHeight, availableHeight);
        }
        else
        {
            float maximumHeight = _settingsVisible
                ? 540f
                : SolverUiTokens.Size.ExpandedMaxHeight;
            height = Math.Min(
                _panel.GetCombinedMinimumSize().Y,
                Math.Min(maximumHeight, availableHeight));
        }

        ApplyPanelBounds(viewportSize, width, height);
    }

    private static void ApplyPanelBounds(Vector2 viewportSize, float width, float height)
    {
        if (_panel == null)
            return;
        float edge = SolverUiTokens.Size.ResizeEdgeThickness;
        width = Math.Min(width, Math.Max(0f, viewportSize.X - edge * 2f));
        height = Math.Min(height, Math.Max(0f, viewportSize.Y - edge * 2f));
        float maxX = Math.Max(edge, viewportSize.X - width - edge);
        float maxY = Math.Max(edge, viewportSize.Y - height - edge);
        _panelPosition = new Vector2(
            Math.Clamp(_panelPosition.X, edge, maxX),
            Math.Clamp(_panelPosition.Y, edge, maxY));
        _panel.OffsetLeft = _panelPosition.X;
        _panel.OffsetTop = _panelPosition.Y;
        _panel.OffsetRight = _panelPosition.X + width;
        _panel.OffsetBottom = _panelPosition.Y + height;
        ApplyResizeHandleBounds(width, height);
        ApplyPotionStrategyBounds(viewportSize, width, height);
        ApplyStrategyBounds(_growthStrategyPanel, SolverGrowthStrategyPanel.PreferredWidth, viewportSize, width, height);
        ApplyStrategyBounds(_relicStrategyPanel, SolverRelicStrategyPanel.PreferredWidth, viewportSize, width, height);
    }

    private static void ApplyResizeHandleBounds(float panelWidth, float panelHeight)
    {
        bool visible = !_collapsed;
        float edge = SolverUiTokens.Size.ResizeEdgeThickness;
        float grip = SolverUiTokens.Size.ResizeGripSize;
        float cornerSize = Math.Min(grip, Math.Min(panelWidth, panelHeight));
        SetResizeHandleBounds(
            _rightResizeHandle,
            _panelPosition.X + panelWidth - edge,
            _panelPosition.Y,
            edge,
            Math.Max(0f, panelHeight - grip),
            visible);
        SetResizeHandleBounds(
            _bottomResizeHandle,
            _panelPosition.X,
            _panelPosition.Y + panelHeight - edge,
            Math.Max(0f, panelWidth - grip),
            edge,
            visible);
        SetResizeHandleBounds(
            _cornerResizeHandle,
            _panelPosition.X + panelWidth - cornerSize,
            _panelPosition.Y + panelHeight - cornerSize,
            cornerSize,
            cornerSize,
            visible);
    }

    private static void SetResizeHandleBounds(
        Control? handle,
        float x,
        float y,
        float width,
        float height,
        bool visible)
    {
        if (handle == null)
            return;
        handle.Visible = visible;
        handle.OffsetLeft = x;
        handle.OffsetTop = y;
        handle.OffsetRight = x + width;
        handle.OffsetBottom = y + height;
    }

    private static void ApplyPotionStrategyBounds(Vector2 viewportSize, float panelWidth, float panelHeight)
        => ApplyStrategyBounds(_potionStrategyPanel, SolverPotionStrategyPanel.PreferredWidth, viewportSize, panelWidth, panelHeight);

    private static void ApplyStrategyBounds(Control? sidebar, float preferredWidth, Vector2 viewportSize, float panelWidth, float panelHeight)
    {
        if (sidebar == null || !GodotObject.IsInstanceValid(sidebar))
            return;
        const float edge = 8f;
        float width = Math.Min(
            preferredWidth,
            Math.Max(0f, viewportSize.X - edge * 2f));
        float height = Math.Min(panelHeight, Math.Max(0f, viewportSize.Y - edge * 2f));
        float maximumX = Math.Max(edge, viewportSize.X - width - edge);
        float rightOfPanelX = _panelPosition.X + panelWidth + SolverUiTokens.Spacing.Md;
        float x = rightOfPanelX <= maximumX
            ? rightOfPanelX
            : Math.Clamp(_panelPosition.X + panelWidth - width, edge, maximumX);
        float y = Math.Clamp(
            _panelPosition.Y,
            edge,
            Math.Max(edge, viewportSize.Y - height - edge));
        sidebar.OffsetLeft = x;
        sidebar.OffsetTop = y;
        sidebar.OffsetRight = x + width;
        sidebar.OffsetBottom = y + height;
    }

    private static void OnHeaderGuiInput(InputEvent inputEvent)
    {
        if (_panel == null)
            return;
        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed)
            {
                _dragging = true;
                _dragOffset = _viewport!.GetMousePosition() - _panelPosition;
            }
            else if (_dragging)
            {
                _dragging = false;
                ApplyResponsiveLayout();
                SolverSettings.SetOverlayPosition(_panelPosition);
                Entry.Logger.Info(
                    $"[CombatSolver/Test] UI_POSITION_SAVED x={_panelPosition.X:F1} y={_panelPosition.Y:F1}");
            }
            return;
        }
        if (!_dragging || inputEvent is not InputEventMouseMotion)
            return;
        _panelPosition = _viewport!.GetMousePosition() - _dragOffset;
        ApplyPanelBounds(_viewport.GetVisibleRect().Size, _panel.Size.X, _panel.Size.Y);
    }

    private static void OnResizeHandleGuiInput(InputEvent inputEvent, ResizeEdge edge)
    {
        if (_panel == null || _viewport == null || _collapsed)
            return;
        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed)
            {
                _resizing = true;
                _dragging = false;
                _activeResizeEdge = edge;
                _resizeStartMousePosition = _viewport.GetMousePosition();
                _resizeStartPrimarySize = new Vector2(
                    Math.Max(SolverSettings.MinimumOverlayWidth, _panel.Size.X),
                    Math.Max(SolverSettings.MinimumOverlayHeight, _panel.Size.Y));
                _customPanelSize = _resizeStartPrimarySize;
                _lastResizeLayoutAt = System.Environment.TickCount64 - ResizeLayoutIntervalMilliseconds;
            }
            else if (_resizing)
            {
                ResizeToMousePosition();
                _resizing = false;
                ApplyResponsiveLayout();
                Vector2 customSize = _customPanelSize
                    ?? throw new InvalidOperationException("Resize completed without a custom panel size.");
                SolverSettings.SetOverlayBounds(_panelPosition, customSize);
                Entry.Logger.Info(
                    $"[CombatSolver/Test] UI_SIZE_SAVED x={_panelPosition.X:F1} y={_panelPosition.Y:F1} " +
                    $"w={customSize.X:F1} h={customSize.Y:F1}");
            }
            return;
        }
        if (!_resizing || inputEvent is not InputEventMouseMotion)
            return;
        long now = System.Environment.TickCount64;
        if (now - _lastResizeLayoutAt < ResizeLayoutIntervalMilliseconds)
            return;
        _lastResizeLayoutAt = now;
        ResizeToMousePosition();
    }

    private static void ResizeToMousePosition()
    {
        if (_panel == null || _viewport == null)
            return;
        Vector2 viewportSize = _viewport.GetVisibleRect().Size;
        float edgeMargin = SolverUiTokens.Size.ResizeEdgeThickness;
        float maximumWidth = Math.Max(
            SolverSettings.MinimumOverlayWidth,
            viewportSize.X - _panelPosition.X - edgeMargin);
        float maximumHeight = Math.Max(
            SolverSettings.MinimumOverlayHeight,
            viewportSize.Y - _panelPosition.Y - edgeMargin);
        Vector2 contentMinimum = _panel.GetCombinedMinimumSize();
        Vector2 minimumSize = new(
            Math.Max(SolverSettings.MinimumOverlayWidth, contentMinimum.X),
            Math.Max(SolverSettings.MinimumOverlayHeight, contentMinimum.Y));
        Vector2 delta = _viewport.GetMousePosition() - _resizeStartMousePosition;
        _customPanelSize = ResizePanelSize(
            _activeResizeEdge,
            _resizeStartPrimarySize,
            delta,
            minimumSize,
            new Vector2(maximumWidth, maximumHeight));
        ApplyResponsiveLayout();
    }

    private static Vector2 ResizePanelSize(
        ResizeEdge edge,
        Vector2 startSize,
        Vector2 delta,
        Vector2 minimumSize,
        Vector2 maximumSize)
    {
        float width = edge is ResizeEdge.Right or ResizeEdge.BottomRight
            ? startSize.X + delta.X
            : startSize.X;
        float height = edge is ResizeEdge.Bottom or ResizeEdge.BottomRight
            ? startSize.Y + delta.Y
            : startSize.Y;
        return new Vector2(
            Math.Clamp(width, minimumSize.X, Math.Max(minimumSize.X, maximumSize.X)),
            Math.Clamp(height, minimumSize.Y, Math.Max(minimumSize.Y, maximumSize.Y)));
    }

    private static void ResetOverlayPosition()
    {
        _panelPosition = new Vector2(SolverUiTokens.Size.PanelMargin, SolverUiTokens.Size.PanelMargin);
        _customPanelSize = null;
        _resizing = false;
        ApplyResponsiveLayout();
    }

    private static void OnRecalculatePressed()
    {
        Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=recalculate");
        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (host == null || state == null || !CombatManager.Instance.IsInProgress)
        {
            if (host != null)
                Show(host, SolverText.Get("当前没有进行中的战斗。"));
            return;
        }
        SolverController.RequestSearch(host, state, SearchReason.Manual);
    }

    private static void OnStopSearchPressed()
    {
        Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=stop_search");
        if (NGame.Instance is { } host)
            SolverController.StopSearchByUser(host);
        RefreshControls();
    }

    private static void OnAdoptRoutePressed()
    {
        SolverController.AdoptCurrentRoute();
        RefreshControls();
    }

    private static void OnExecutePressed()
    {
        NGame? host = NGame.Instance;
        if (host != null && SolverController.IsSearching)
        {
            if (SolverController.CanApplyCurrentTurn)
                SolverController.ApplyCurrentTurn();
            return;
        }

        Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=deploy");
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (host == null || state == null || !CombatManager.Instance.IsInProgress)
        {
            if (host != null)
                Show(host, SolverText.Get("当前没有进行中的战斗。"));
            return;
        }
        SolverController.RequestDeploy(host, state);
        RefreshControls();
    }

    private static void OnFullAutoPressed()
    {
        Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=full_auto_toggle");
        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (host == null || state == null || !CombatManager.Instance.IsInProgress)
        {
            if (host != null)
                Show(host, SolverText.Get("当前没有进行中的战斗。"));
            return;
        }
        SolverController.SetFullAuto(host, state, !SolverController.FullAutoEnabled);
    }

    private static void OnTheftPolicyPressed(SolverTheftPolicy policy)
    {
        Entry.Logger.Info($"[CombatSolver/Test] UI_ACTION action=theft_policy policy={policy}");
        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (host == null || state == null || !CombatManager.Instance.IsInProgress)
            return;
        SolverController.SetTheftPolicy(host, state, policy);
        RefreshControls();
    }

    internal static async Task ExerciseCompactQolForTesting(CombatState combat)
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Compact UI: " + message);
        }
        NGame host = NGame.Instance ?? throw new InvalidOperationException("UI test requires a game host.");
        SolverSettingsData original = SolverSettings.Current;
        bool originalCollapsed = _collapsed;
        try
        {
            Check(!new SolverSettingsData().AutoEnableFullAuto, "opt-in default");
            SolverSettings.ApplyForTesting(original with { AutoEnableFullAuto = false, AutomaticCalculationEnabled = false });
            SolverController.BeginCombat(combat);
            EnsureCreated(host);
            RefreshControls();
            Check(!_autoEnableFullAutoSwitch!.ButtonPressed && !SolverController.FullAutoEnabled, "off at combat start");
            _autoEnableFullAutoSwitch.ButtonPressed = true;
            Check(SolverSettings.RoundTripForTesting(SolverSettings.Current).AutoEnableFullAuto, "switch persists");
            Check(!SolverController.FullAutoEnabled, "preference applies at next combat");
            SolverController.BeginCombat(combat);
            Check(SolverController.FullAutoEnabled && SolverController.PrepareAutomaticSearchForTurn(host, combat), "start with manual calculation preference");
            SolverController.SetFullAuto(host, combat, false);
            RefreshControls();
            Check(!SolverController.PrepareAutomaticSearchForTurn(host, combat) && !SolverController.FullAutoEnabled
                && _autoEnableFullAutoSwitch.ButtonPressed, "manual stop remains stopped");
            SolverController.BeginCombat(combat);
            Check(SolverController.FullAutoEnabled, "next combat re-arms");
            _autoEnableFullAutoSwitch.ButtonPressed = false;
            SolverController.BeginCombat(combat);
            Check(!SolverController.FullAutoEnabled, "disabled preference applies next combat");

            SolverOverlaySnapshot snapshot = new(1, "UI test", SolverOverlayTone.Success, "", "", 0, 7, true,
                SolverText.Format($"本局扣血  {7} HP"), 0, 0, false, [], "", false, null);
            ShowResult(host, snapshot);
            Label outcome = _hpOutcomeLabel!;
            SetCollapsed(true);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(outcome.IsVisibleInTree() && !_body!.Visible && _routeOutcomePanel!.GetParent() == _mainStack
                && outcome.Text == snapshot.HpOutcomeText && outcome.GetThemeColor("font_color") == Danger, "collapsed loss and color");
            ShowResult(host, snapshot with { HpOutcomeText = "0 HP", ProjectedBattleHpLost = 0 });
            Check(ReferenceEquals(outcome, _hpOutcomeLabel) && outcome.Text == "0 HP"
                && outcome.GetThemeColor("font_color") == Success, "live update uses same label");
            SetCollapsed(false);
            Check(_routeOutcomePanel!.GetParent() == _body && outcome.IsVisibleInTree(), "expanded placement restored");
            SetCollapsed(true);
            ShowSearching(host, 1, false, 0);
            Check(!outcome.Visible, "new search clears stale loss");
            ShowResult(host, snapshot with { ProjectedBattleHpLossKnown = false, HpOutcomeText = "? HP" });
            Check(outcome.IsVisibleInTree() && outcome.Text == "? HP"
                && outcome.GetThemeColor("font_color") == TextMuted, "unknown loss stays unknown");
        }
        finally
        {
            SolverSettings.Update(original);
            SolverController.BeginCombat(combat);
            SetCollapsed(originalCollapsed);
            RefreshControls();
        }
    }

    internal static async Task ExercisePriorityUiForTesting(CombatState combat)
    {
        NGame host = NGame.Instance!;
        SolverSettingsData original = SolverSettings.Current;
        bool wasCollapsed = _collapsed;
        try
        {
            SolverSettings.ApplyForTesting(original with { AutomaticCalculationEnabled = false });
            ShowManualCalculationReady(host, false);
            _actionBar!.AssertLayoutForTesting();
            RefreshControls();
            if (_solverEnabledButton!.GetParent().Name != "Header"
                || !ManualSystemMemoryReleaseButtonConfiguredForTesting
                || _theftPolicyControls!.GetParent() != _body)
                throw new InvalidOperationException("Action controls were not separated from header, strategy and maintenance controls.");
            _solverEnabledButton!.EmitSignal(BaseButton.SignalName.Pressed);
            if (!SolverController.SolverDisabled || _solverEnabledButton.Text != SolverText.Get("求解器：关"))
                throw new InvalidOperationException("Main solver toggle did not disable the solver.");
            _solverEnabledButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (SolverController.SolverDisabled)
                throw new InvalidOperationException("Main solver toggle did not re-enable the solver.");
            SolverController.Reset("test_sl_panel_loss");
            SolverController.MonitorCombatPresence();
            if (!IsVisible || SolverController.IsSearching)
                throw new InvalidOperationException("SL panel recovery ignored manual calculation preference.");
            SolverOverlaySnapshot snapshot = new(1, "UI test", SolverOverlayTone.Success, "", "", 0, 0, true,
                "0 HP", 0, 0, false, [], "", false, null)
            { UnrecoveredLootText = SolverText.Format($"预计未追回：{1} 张牌 / {30} 金币") };
            ShowResult(host, snapshot);
            SetCollapsed(true);
            if (!_stolenResourceOutcomeLabel!.IsVisibleInTree()
                || _stolenResourceOutcomeLabel.GetIndex() >= _hpOutcomeLabel!.GetIndex())
                throw new InvalidOperationException("Unrecovered loot was not shown before HP loss in collapsed layout.");
            if (_routeOutcomePanel!.GetParent() != _mainStack)
                throw new InvalidOperationException("Collapsed layout lost its result card.");
            SetCollapsed(false);
            if (_routeOutcomePanel.GetParent() != _body || _routeOutcomePanel.GetIndex() != 0)
                throw new InvalidOperationException("Expanded result card was not restored above search status.");
            ShowSearching(host, 1, false, 0);
            if (_stolenResourceOutcomeLabel.Visible)
                throw new InvalidOperationException("New search retained stale loot warning.");
            SolverSettingsPanel settings = new();
            host.AddChild(settings);
            try
            {
                await settings.AssertResponsiveHeightForTesting();
                if (!settings.ExerciseThresholdOutsideClickForTesting())
                    throw new InvalidOperationException("Moved performance threshold failed to save on focus loss.");
            }
            finally { host.RemoveChild(settings); settings.QueueFree(); }
        }
        finally
        {
            SolverSettings.Update(original);
            SolverController.SetSolverDisabled(original.SolverDisabled, persist: false);
            SetCollapsed(wasCollapsed);
        }
    }

    internal static async Task<bool> ExerciseGrowthPolicyUiForTesting()
    {
        EnsureCreated(NGame.Instance ?? throw new InvalidOperationException("Growth UI test requires an active game."));
        SolverSettingsData originalPolicy = SolverSettings.Current;
        bool originalGrowth = _growthStrategyVisible;
        bool originalPotion = _potionStrategyVisible;
        bool originalSettings = _settingsVisible;
        bool originalCollapsed = _collapsed;
        try
        {
            SolverSettings.ApplyForTesting(originalPolicy with { AutomaticCalculationEnabled = false });
            if (!_growthStrategyVisible)
                ToggleGrowthStrategy();
            ApplyResponsiveLayout();
            await _growthStrategyPanel!.ToSignal(_growthStrategyPanel.GetTree(), SceneTree.SignalName.ProcessFrame);
            await _growthStrategyPanel.ToSignal(_growthStrategyPanel.GetTree(), SceneTree.SignalName.ProcessFrame);
            Rect2 bounds = _growthStrategyPanel.GetGlobalRect();
            Vector2 viewport = _viewport!.GetVisibleRect().Size;
            bool growthValid = _growthStrategyButton != null && _growthStrategyPanel.Visible
                && ReferenceEquals(_growthStrategyPanel.GetParent(), _layer)
                && !_potionStrategyVisible && !_settingsVisible
                && bounds.Position.X >= 0 && bounds.Position.Y >= 0
                && bounds.End.X <= viewport.X && bounds.End.Y <= viewport.Y
                && _growthStrategyPanel.SettingsConfiguredForTesting
                && ExerciseAcceptableBattleHpLossSettingsForTesting()
                && _growthStrategyPanel.ExerciseOutsideClickForTesting();
            ToggleSettings();
            await _settingsPanel!.ToSignal(_settingsPanel.GetTree(), SceneTree.SignalName.ProcessFrame);
            return growthValid && _settingsPanel.ExerciseThresholdOutsideClickForTesting();
        }
        finally
        {
            SolverSettings.ApplyForTesting(originalPolicy);
            _settingsPanel?.Reload();
            _growthStrategyPanel?.Refresh(false);
            _growthStrategyVisible = originalGrowth;
            _potionStrategyVisible = originalPotion;
            _settingsVisible = originalSettings;
            SetCollapsed(originalCollapsed);
            ApplyContentVisibility();
            QueueResponsiveLayout();
        }
    }

    private static void ToggleGrowthStrategy()
    {
        _relicStrategyVisible = false;
        if (_settingsVisible && _settingsPanel?.CommitPending() == false)
            return;
        if (_collapsed)
            SetCollapsed(false);
        _settingsVisible = false;
        _potionStrategyVisible = false;
        _growthStrategyVisible = !_growthStrategyVisible;
        RefreshControls();
        ApplyContentVisibility();
        QueueResponsiveLayout();
    }

    private static void OnBrightestFlameLimitChanged(int? limit)
    {
        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (host != null && state != null && CombatManager.Instance.IsInProgress)
            SolverController.SetBrightestFlameLimit(host, state, limit);
    }

    private static void OnGrowthPolicyChanged(GrowthValues budgets)
    {
        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (host != null && state != null && CombatManager.Instance.IsInProgress)
            SolverController.SetGrowthPolicy(host, state, budgets);
    }

    private static void ToggleRelicStrategy()
    {
        if (_settingsVisible && _settingsPanel?.CommitPending() == false) return;
        if (_collapsed) SetCollapsed(false);
        _settingsVisible = false;
        _potionStrategyVisible = false;
        _growthStrategyVisible = false;
        _relicStrategyVisible = !_relicStrategyVisible;
        RefreshControls();
        ApplyContentVisibility();
        QueueResponsiveLayout();
    }

    internal static async Task<bool> ExerciseRelicPanelForTesting()
    {
        EnsureCreated(NGame.Instance ?? throw new InvalidOperationException("Relic UI needs a game."));
        ToggleRelicStrategy();
        ApplyResponsiveLayout();
        await _relicStrategyPanel!.ToSignal(_relicStrategyPanel.GetTree(), SceneTree.SignalName.ProcessFrame);
        await _relicStrategyPanel.ToSignal(_relicStrategyPanel.GetTree(), SceneTree.SignalName.ProcessFrame);
        Rect2 bounds = _relicStrategyPanel.GetGlobalRect();
        Vector2 viewport = _viewport!.GetVisibleRect().Size;
        bool valid = _relicStrategyPanel.Visible && !_growthStrategyVisible && !_potionStrategyVisible
            && _relicStrategyButton!.GetIndex() == _growthStrategyButton!.GetIndex() + 1
            && bounds.Position.X >= 0 && bounds.Position.Y >= 0 && bounds.End.X <= viewport.X && bounds.End.Y <= viewport.Y;
        ToggleGrowthStrategy();
        valid &= !_relicStrategyPanel.Visible && _growthStrategyPanel!.Visible;
        ApplyOverlayOpacity();
        foreach (PanelContainer strategy in new PanelContainer[] { _relicStrategyPanel, _growthStrategyPanel!, _potionStrategyPanel! })
        {
            valid &= strategy.Modulate.A == 1f && ((StyleBoxFlat)strategy.GetThemeStylebox("panel")).BgColor.A == 1f;
            IEnumerable<Node> Descendants(Node node) => node.GetChildren().SelectMany(child => new[] { child }.Concat(Descendants(child)));
            valid &= Descendants(strategy).OfType<LineEdit>().All(edit => edit.GetThemeFontSize("font_size") == 16);
        }
        ToggleGrowthStrategy();
        return valid;
    }

    private static void OnRelicPolicyChanged(bool enabled, RelicCounterRule[] rules)
    {
        var host = NGame.Instance;
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (host != null && state != null && CombatManager.Instance.IsInProgress)
            SolverController.SetRelicCounterPolicy(host, state, enabled, rules);
    }

    private static void OnIgnoreLongTermRewardsChanged(bool ignore)
    {
        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (host != null && state != null && CombatManager.Instance.IsInProgress)
            SolverController.SetIgnoreLongTermRewards(host, state, ignore);
    }

    private static void OnPotionDirectiveChanged(
        int slot,
        string potionId,
        SolverPotionDirective directive)
    {
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_ACTION action=potion_directive slot={slot} " +
            $"potion={potionId} directive={directive}");
        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (host == null || state == null || !CombatManager.Instance.IsInProgress)
            return;
        SolverController.SetPotionDirective(host, state, slot, potionId, directive);
        _potionStrategyPanel?.Invalidate();
        RefreshControls();
    }

}
