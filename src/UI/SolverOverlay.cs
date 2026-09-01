using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal static class SolverOverlay
{
    private const string LayerName = "CombatSolverOverlay";
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
    private static ProgressBar? _searchProgressBar;
    private static HBoxContainer? _routeHeadingRow;
    private static readonly SolverRouteRow[] RouteRows = new SolverRouteRow[SolverWeights.UiTurnRows];
    private static PanelContainer? _detailsPanel;
    private static Label? _deathOutcomeLabel;
    private static Label? _potionOutcomeLabel;
    private static Label? _hpOutcomeLabel;
    private static RichTextLabel? _detailsText;
    private static SolverDetailsButton? _detailsButton;
    private static Button? _recalculateButton;
    private static Button? _executeButton;
    private static Button? _fullAutoButton;
    private static Button? _collapseButton;
    private static Button? _settingsButton;
    private static PanelContainer? _feedbackBanner;
    private static Label? _feedbackBannerLabel;
    private static HBoxContainer? _theftPolicyControls;
    private static HFlowContainer? _potionBanControls;
    private static string? _renderedPotionBanState;
    private static Button? _preserveResourcesButton;
    private static Button? _letEscapeButton;
    private static bool _collapsed;
    private static bool _settingsVisible;
    private static bool _deployQueued;
    private static bool _detailsVisible;
    private static bool _dragging;
    private static bool _layoutQueued;
    private static bool? _renderedFullAutoStyle;
    private static SolverButtonStyle? _renderedExecuteButtonStyle;
    private static SolverTheftPolicy? _renderedTheftPolicy;
    private static SolverOverlaySnapshot? _lastSnapshot;
    private static string? _lastMessageText;
    private static int _lastSearchingTurn;
    private static bool _lastSearchDeployWhenReady;
    private static bool _themeRefreshQueued;
    private static int _remainingLayoutPasses;
    private static Vector2 _dragOffset;
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
    internal static bool MessageWrappingEnabledForTesting
        => _summaryText is { FitContent: true, AutowrapMode: TextServer.AutowrapMode.WordSmart };
    internal static bool UploadProgressConfiguredForTesting
        => _settingsPanel?.UploadProgressConfiguredForTesting == true;
    internal static bool SearchCompletionNotificationSettingsConfiguredForTesting
        => _settingsPanel?.SearchCompletionNotificationSettingsConfiguredForTesting == true;
    internal static bool SettingsTabsConfiguredForTesting
        => _settingsPanel?.SettingsTabsConfiguredForTesting == true;
    internal static bool VisualSettingsConfiguredForTesting
        => _settingsPanel?.VisualSettingsConfiguredForTesting == true;
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

    public static void Show(Node host, string text)
    {
        _lastSnapshot = null;
        _lastMessageText = text;
        EnsureCreated(host);
        _deployQueued = false;
        SetStatus("求解器消息", TextMuted);
        const string legacyTitle = "[b]战斗路线求解器[/b]\n";
        SetMessageContent(text.StartsWith(legacyTitle, StringComparison.Ordinal) ? text[legacyTitle.Length..] : text);
        ShowLayer();
        RefreshControls();
    }

    public static void ShowDisabled(Node host)
    {
        _lastSnapshot = null;
        _lastMessageText = null;
        EnsureCreated(host);
        _deployQueued = false;
        SetStatus("求解器已禁用", TextMuted);
        SetMessageContent($"[color={SolverUiTokens.Palette.TextSecondaryHex}]自动搜索和路线执行已暂停。[/color]");
        ShowLayer();
        RefreshControls();
    }

    public static void ShowSearchStopped(Node host)
    {
        _lastSnapshot = null;
        _lastMessageText = null;
        EnsureCreated(host);
        _deployQueued = false;
        SetStatus("计算已停止", Danger);
        SetMessageContent(
            $"[color={SolverUiTokens.Palette.DangerHex}]本场自动计算已暂停。点击“重新计算”后恢复当前及后续回合搜索。[/color]");
        ShowLayer();
        RefreshControls();
    }

    public static void ShowProgress(SolverProgress progress, bool deployWhenReady)
    {
        if (_layer == null || !GodotObject.IsInstanceValid(_layer) || !_layer.Visible)
            return;
        _deployQueued = deployWhenReady;
        _lastSearchingTurn = progress.CurrentTurnNumber;
        _lastSearchDeployWhenReady = deployWhenReady;
        SetStatus(
            "后台计算中",
            Accent,
            deployWhenReady
                ? $"第 {progress.CurrentTurnNumber} 回合    已排队执行"
                : $"第 {progress.CurrentTurnNumber} 回合");
        if (_summaryText != null)
        {
            _summaryText.Visible = false;
        }
        if (_progressText != null)
        {
            _progressText.Visible = true;
            _progressText.Text = $"已用 {progress.ElapsedMilliseconds / 1000d:F1} s";
        }
        if (_searchProgressBar != null)
        {
            _searchProgressBar.Visible = true;
            _searchProgressBar.MaxValue = Math.Max(1, progress.MaxNodes);
            _searchProgressBar.Value = Math.Clamp(progress.ExpandedNodes, 0, progress.MaxNodes);
        }
        RefreshControls();
    }

    public static void ShowSearching(Node host, int turn, bool deployWhenReady)
    {
        _lastSnapshot = null;
        _lastMessageText = null;
        _lastSearchingTurn = turn;
        _lastSearchDeployWhenReady = deployWhenReady;
        EnsureCreated(host);
        _deployQueued = deployWhenReady;
        SetStatus(
            "后台计算中",
            Accent,
            deployWhenReady ? $"第 {turn} 回合    已排队执行" : $"第 {turn} 回合");
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = $"[color={SolverUiTokens.Palette.TextSecondaryHex}]正在准备搜索…[/color]";
        }
        if (_progressText != null)
            _progressText.Visible = false;
        if (_searchProgressBar != null)
        {
            _searchProgressBar.Visible = true;
            _searchProgressBar.MaxValue = SolverSearchProfile.Short.MaxExpandedNodes;
            _searchProgressBar.Value = 0;
        }
        SetRouteVisibility(true);
        if (_potionOutcomeLabel != null)
            _potionOutcomeLabel.Visible = false;
        if (_hpOutcomeLabel != null)
            _hpOutcomeLabel.Visible = false;
        if (_deathOutcomeLabel != null)
            _deathOutcomeLabel.Visible = false;
        for (int index = 0; index < SolverWeights.UiTurnRows; index++)
        {
            SetRouteRowVisible(index, index < 3);
            if (index >= 3)
                continue;
            RouteRows[index].TurnLabel.Text = $"第 {turn + index} 回合";
            RouteRows[index].ShowStatus("等待搜索结果…");
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
        _lastSnapshot = snapshot;
        _lastMessageText = null;
        EnsureCreated(host);
        _deployQueued = false;
        Color statusColor = snapshot.StatusTone switch
        {
            SolverOverlayTone.Danger => Danger,
            SolverOverlayTone.Success => Success,
            _ => Accent,
        };
        SetStatus(
            snapshot.StatusText,
            statusColor,
            $"第 {snapshot.StartTurnNumber} 回合");
        if (_summaryPanel != null)
            _summaryPanel.Visible = true;
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = SolverUiTokens.AdaptRichTextToActiveTheme(snapshot.SummaryText);
        }
        if (_progressText != null)
            _progressText.Visible = false;
        if (_searchProgressBar != null)
            _searchProgressBar.Visible = false;

        SetRouteVisibility(true);
        if (_potionOutcomeLabel != null)
        {
            _potionOutcomeLabel.Visible = snapshot.ProjectedBattlePotionCount > 0;
            _potionOutcomeLabel.Text = $"预计用{snapshot.ProjectedBattlePotionCount}瓶药";
        }
        if (_hpOutcomeLabel != null)
            _hpOutcomeLabel.Visible = true;
        if (_deathOutcomeLabel != null)
            _deathOutcomeLabel.Visible = snapshot.OnlyDeathRoutesFound;
        for (int index = 0; index < SolverWeights.UiTurnRows; index++)
        {
            SetRouteRowVisible(index, index < snapshot.Turns.Count);
            if (index >= snapshot.Turns.Count)
                continue;
            SolverOverlayTurnSnapshot turn = snapshot.Turns[index];
            RouteRows[index].TurnLabel.Text = $"第 {turn.Turn} 回合";
            RouteRows[index].Populate(turn);
            string outcome = turn.CombatEnded
                ? "战斗结束"
                : turn.HpLoss > 0
                    ? $"-{turn.HpLoss} HP"
                    : "0 HP";
            RouteRows[index].SetOutcome(
                outcome,
                turn.CombatEnded ? Success : turn.HpLoss > 0 ? Danger : TextMuted,
                $"余 {turn.EnergyLeft} 费");
        }
        if (_routeScroll != null)
            _routeScroll.ScrollVertical = 0;

        if (_hpOutcomeLabel != null)
        {
            _hpOutcomeLabel.Text = snapshot.HpOutcomeText;
            _hpOutcomeLabel.AddThemeColorOverride("font_color", snapshot.ProjectedBattleHpLost > 0 ? Danger : Success);
        }
        if (_detailsButton != null)
            _detailsButton.Visible = true;
        if (_detailsText != null)
            _detailsText.Text = SolverUiTokens.AdaptRichTextToActiveTheme(snapshot.DetailsText);
        SetDetailsVisible(false);
        ShowLayer();
        RefreshControls();
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_STATE state=ready turn={snapshot.StartTurnNumber} " +
            $"risk={snapshot.HasRisk} only_death_routes={snapshot.OnlyDeathRoutesFound}");
    }

    public static void ShowDeploying(Node host, int turn, int actionCount)
    {
        EnsureCreated(host);
        _deployQueued = false;
        SetStatus("正在执行", Warning, $"第 {turn} 回合");
        if (_summaryPanel != null)
            _summaryPanel.Visible = true;
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = $"[color={SolverUiTokens.Palette.TextSecondaryHex}]按推荐顺序执行 [b]{actionCount}[/b] 张牌，完成后结束本回合。[/color]";
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
                $"[color={SolverUiTokens.Palette.TextSecondaryHex}]正在执行 [b]{completedActions + 1}/{actionCount}[/b]：[/color][color={SolverUiTokens.Palette.WarningHex}] {currentCardTitle}[/color]";
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
                $"[color={SolverUiTokens.Palette.TextSecondaryHex}]正在执行：[/color]" +
                $"[color={SolverUiTokens.Palette.WarningHex}] 结束回合[/color]";
        }
        Entry.Logger.Info("[CombatSolver/Test] UI_DEPLOYMENT_END_TURN state=active");
    }

    public static void ShowDeploymentComplete(Node host, int turn, int actionCount, bool endedTurn)
    {
        EnsureCreated(host);
        ShowDeploymentStep(actionCount, actionCount, null);
        RouteRows[0].SetEndTurnDeploymentState(active: false, completed: endedTurn);
        _deployQueued = false;
        SetStatus("执行完成", Accent, $"第 {turn} 回合");
        if (_summaryPanel != null)
            _summaryPanel.Visible = true;
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = endedTurn
                ? $"已按推荐路线打出 [b]{actionCount}[/b] 张牌，并提交结束回合动作。"
                : $"已打出 [b]{actionCount}[/b] 张牌；战斗或当前回合已在执行期间结束。";
        }
        if (_progressText != null)
            _progressText.Visible = false;
        ShowLayer();
        RefreshControls();
        Entry.Logger.Info($"[CombatSolver/Test] UI_STATE state=deployment_complete turn={turn} card_count={actionCount} end_turn={endedTurn}");
    }

    public static void ShowFullAutoStoppedAtCombatEnd(int turn)
    {
        SetStatus("方案就绪", Success, $"第 {turn} 回合    全自动已暂停");
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = $"[color={SolverUiTokens.Palette.SuccessHex}]当前方案预计结束战斗，操作权已交还。[/color]";
        }
    }

    public static void ShowFullAutoStoppedAtDeathTurn(int turn)
    {
        SetStatus("已暂停执行", Danger, $"第 {turn} 回合    预计死亡");
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text = $"[color={SolverUiTokens.Palette.DangerHex}]当前方案预计本回合死亡，操作权已交还。[/color]";
        }
    }

    public static void ShowFullAutoStoppedAfterWorseRecalculation(
        int turn,
        int? previousProjectedBattleHpLost,
        int projectedBattleHpLost)
    {
        SetStatus("已暂停执行", Danger, $"第 {turn} 回合    重算后战损上升");
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text =
                $"[color={SolverUiTokens.Palette.DangerHex}]完整路线原预计 {previousProjectedBattleHpLost} HP，" +
                $"重算后为 {projectedBattleHpLost} HP；全自动已暂停。[/color]\n" +
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
            "已暂停执行",
            Danger,
            playerDead
                ? $"第 {turn} 回合    实机复核将死亡"
                : $"第 {turn} 回合    实机复核战损上升");
        if (_summaryText != null)
        {
            _summaryText.Visible = true;
            _summaryText.Text =
                $"[color={SolverUiTokens.Palette.DangerHex}]路线预计掉血 {plannedHpLoss} HP，" +
                $"结束回合前实机复核为 {liveHpLoss} HP；全自动未提交结束回合。[/color]\n" +
                SolverUiTokens.BugReportUploadInstructionRichText;
        }
    }

    public static void RefreshControls()
    {
        if (_recalculateButton == null || _executeButton == null || _fullAutoButton == null)
            return;

        RefreshFeedbackBanner();

        bool solverDisabled = SolverController.SolverDisabled;
        _recalculateButton.Disabled = solverDisabled || SolverController.IsSearching || SolverController.IsDeploying;
        _executeButton.Disabled = solverDisabled || SolverController.IsDeploying;
        if (SolverController.IsDeploying)
            _executeButton.Text = "执行中…";
        else if (SolverController.IsSearching)
            _executeButton.Text = "停止计算";
        else if (_deployQueued)
            _executeButton.Text = "已排队执行";
        else
            _executeButton.Text = "执行本回合";
        SolverButtonStyle executeStyle = SolverController.IsSearching
            ? SolverButtonStyle.Danger
            : SolverButtonStyle.Primary;
        if (_renderedExecuteButtonStyle != executeStyle)
        {
            SolverUiTokens.ApplyButtonStyle(_executeButton, executeStyle);
            _renderedExecuteButtonStyle = executeStyle;
        }

        _fullAutoButton.Text = SolverController.FullAutoEnabled ? "全自动：运行中" : "全自动：关";
        _fullAutoButton.Disabled = solverDisabled;
        if (_renderedFullAutoStyle != SolverController.FullAutoEnabled)
        {
            SolverUiTokens.ApplyButtonStyle(
                _fullAutoButton,
                SolverController.FullAutoEnabled ? SolverButtonStyle.Positive : SolverButtonStyle.Secondary);
            _fullAutoButton.AddThemeColorOverride(
                "font_color",
                SolverUiTokens.IsLightTheme && SolverController.FullAutoEnabled
                    ? Colors.White
                    : TextPrimary);
            _renderedFullAutoStyle = SolverController.FullAutoEnabled;
        }

        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        RefreshPotionBanControls(combat, solverDisabled);
        bool showTheftPolicy = combat != null
            && CombatManager.Instance.IsInProgress
            && TheftEncounterStrategy.IsApplicable(combat);
        if (_theftPolicyControls != null)
            _theftPolicyControls.Visible = showTheftPolicy;
        if (!showTheftPolicy || _preserveResourcesButton == null || _letEscapeButton == null)
        {
            _renderedTheftPolicy = null;
            return;
        }

        SolverTheftPolicy policy = SolverController.TheftPolicy ?? SolverTheftPolicy.PreserveResources;
        _preserveResourcesButton.Disabled = solverDisabled || SolverController.IsDeploying;
        _letEscapeButton.Disabled = solverDisabled || SolverController.IsDeploying;
        if (_renderedTheftPolicy != policy)
        {
            SolverUiTokens.ApplyButtonStyle(
                _preserveResourcesButton,
                policy == SolverTheftPolicy.PreserveResources
                    ? SolverButtonStyle.Positive
                    : SolverButtonStyle.Secondary);
            SolverUiTokens.ApplyButtonStyle(
                _letEscapeButton,
                policy == SolverTheftPolicy.LetEscape
                    ? SolverButtonStyle.Primary
                    : SolverButtonStyle.Secondary);
            _renderedTheftPolicy = policy;
        }
    }

    /// <summary>
    /// One toggle per occupied potion slot, letting the player take a specific bottle off the table for this
    /// combat. Keyed by slot because two copies of the same potion must be distinguishable.
    /// </summary>
    private static void RefreshPotionBanControls(CombatState? combat, bool solverDisabled)
    {
        if (_potionBanControls == null || !GodotObject.IsInstanceValid(_potionBanControls))
            return;
        Player? player = combat == null ? null : LocalContext.GetMe(combat);
        bool show = player != null && CombatManager.Instance.IsInProgress;
        _potionBanControls.Visible = show;
        if (!show)
        {
            _renderedPotionBanState = null;
            return;
        }

        IReadOnlySet<int> banned = SolverController.BannedPotionSlots;
        List<(int Slot, string Title)> slots = [];
        for (int slot = 0; slot < player!.PotionSlots.Count; slot++)
        {
            if (player.GetPotionAtSlotIndex(slot) is { } potion)
                slots.Add((slot, potion.Title.GetFormattedText()));
        }
        string state = string.Join(
            '|',
            slots.Select(item => $"{item.Slot}:{item.Title}:{banned.Contains(item.Slot)}"));
        bool disabled = solverDisabled || SolverController.IsDeploying;
        if (_renderedPotionBanState == state)
        {
            foreach (Node child in _potionBanControls.GetChildren())
            {
                if (child is Button existing)
                    existing.Disabled = disabled;
            }
            return;
        }
        _renderedPotionBanState = state;
        foreach (Node child in _potionBanControls.GetChildren())
        {
            _potionBanControls.RemoveChild(child);
            child.QueueFree();
        }
        foreach ((int slot, string title) in slots)
        {
            bool isBanned = banned.Contains(slot);
            Button button = CreateButton(isBanned ? $"✕ {title}" : title, false);
            button.CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ButtonHeight);
            button.TooltipText = isBanned
                ? "本场禁止使用这瓶药水，点击解除"
                : "点击后本场不再考虑这瓶药水";
            button.Disabled = disabled;
            int captured = slot;
            button.Pressed += () => OnPotionBanPressed(captured);
            SolverUiTokens.ApplyButtonStyle(
                button,
                isBanned ? SolverButtonStyle.Danger : SolverButtonStyle.Secondary);
            _potionBanControls.AddChild(button);
        }
    }

    private static void OnPotionBanPressed(int slot)
    {
        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        if (combat == null || NGame.Instance is not { } host)
            return;
        SolverController.TogglePotionSlotBan(host, combat, slot);
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
        _panel.Modulate = new Color(1f, 1f, 1f, opacity);
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
        bool wasCollapsed = _collapsed;
        bool wereDetailsVisible = _detailsVisible;
        CanvasLayer oldLayer = _layer;
        oldLayer.Visible = false;
        oldLayer.QueueFree();
        if (_viewport != null && GodotObject.IsInstanceValid(_viewport))
            _viewport.SizeChanged -= ApplyResponsiveLayout;
        _layer = null;
        _panel = null;
        _viewport = null;
        _layoutQueued = false;
        _remainingLayoutPasses = 0;
        _renderedFullAutoStyle = null;
        _renderedExecuteButtonStyle = null;
        _renderedTheftPolicy = null;

        if (_lastSnapshot is { } snapshot)
        {
            ShowResult(host, snapshot);
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
            ShowSearching(host, _lastSearchingTurn, _lastSearchDeployWhenReady);
        }
        else
        {
            Show(host, _lastMessageText ?? "界面主题已应用。");
        }

        _settingsVisible = wasSettingsVisible;
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
        };
        root.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        panel.AddChild(root);

        root.AddChild(CreateHeader());
        root.AddChild(CreateFeedbackBanner());
        root.AddChild(CreateDivider());

        VBoxContainer lowerStack = new()
        {
            Name = "ContentAndActions",
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        lowerStack.AddThemeConstantOverride("separation", 0);
        root.AddChild(lowerStack);
        _mainStack = lowerStack;

        _settingsPanel = new SolverSettingsPanel
        {
            Visible = false,
        };
        _settingsPanel.ResetPositionRequested += ResetOverlayPosition;
        root.AddChild(_settingsPanel);

        _body = new VBoxContainer
        {
            Name = "Body",
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _body.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        lowerStack.AddChild(_body);

        _body.AddChild(CreateSummarySection());
        _routeHeadingRow = new HBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        Label routeHeading = CreateTextLabel(
            "推荐路线",
            SolverUiTokens.Type.Body,
            TextPrimary,
            FontType.Bold);
        routeHeading.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _routeHeadingRow.AddChild(routeHeading);
        _deathOutcomeLabel = CreateTextLabel(
            "未找到生还路线",
            SolverUiTokens.Type.Body,
            Danger,
            FontType.Bold);
        _deathOutcomeLabel.Visible = false;
        _deathOutcomeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _routeHeadingRow.AddChild(_deathOutcomeLabel);
        _potionOutcomeLabel = CreateTextLabel("预计用1瓶药", SolverUiTokens.Type.Body, Warning, FontType.Bold);
        _potionOutcomeLabel.Visible = false;
        _potionOutcomeLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _potionOutcomeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _routeHeadingRow.AddChild(_potionOutcomeLabel);
        _hpOutcomeLabel = CreateTextLabel("本局扣血  0 HP", SolverUiTokens.Type.Body, Success, FontType.Bold);
        _hpOutcomeLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _hpOutcomeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _routeHeadingRow.AddChild(_hpOutcomeLabel);
        _body.AddChild(_routeHeadingRow);
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
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
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
        Entry.Logger.Info(
            $"[CombatSolver/Test] UI_POSITION_LOADED persisted={SolverSettings.OverlayPosition.HasValue} " +
            $"x={_panelPosition.X:F1} y={_panelPosition.Y:F1}");
        _dragging = false;
        _settingsVisible = false;
        SetCollapsed(false);
        Entry.Logger.Info("[CombatSolver/Test] UI_CREATE responsive=true content_fit_height=true minimum_size_reflow=true draggable=true drag_coordinates=viewport drag_relayout=release_only max_width=820 max_height=440 route_row_height=44 route_viewport_height=148 visible_unwrapped_route_rows=3 cached_route_rows=16 all_searched_turns=true route_scroll=true persistent_status_card=true compact_title=true compact_footer=true collapsed_action_buttons=true footer_pause_toggles=false settings_pause_toggles=true footer_top_margin=8 details_in_status_row=true battle_hp_in_route_heading=true sold_hp_summary=false three_column_routes=true semantic_action_pills=true full_target_names=true whole_pill_kill_highlight=true text_outline_px=2 wrapped_summary=true summary_bold_metric=true flat_collapse=true plain_details_button=true full_auto_positive_toggle=true no_middle_dot=true status_badge=true plain_action_buttons=true always_show_energy=true plain_route_heading=true settings_button=true settings_persisted=true settings_tabs=general+performance+feedback performance_advanced=collapsed notification_policy=three_state performance_presets=low+medium+high+very_high+custom kill_pill=green_with_target_names status_badge=content_width deployment_speed_settings=true search_status=fixed_columns_seconds only_death_marker=true relic_action_labels=true position_persisted=true theft_policy_buttons=contextual stop_search_button=true");
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

        Label title = CreateTextLabel("战斗路线求解器", SolverUiTokens.Type.Title, TextPrimary, FontType.Bold);
        title.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        header.AddChild(title);

        Control spacer = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        header.AddChild(spacer);

        _settingsButton = CreateHeaderButton("设置", 54);
        _settingsButton.Pressed += ToggleSettings;
        if (SolverUiTokens.IsLightTheme)
        {
            _settingsButton.AddThemeColorOverride("font_color", Accent);
            _settingsButton.AddThemeColorOverride("font_hover_color", Accent);
            _settingsButton.AddThemeColorOverride("font_pressed_color", Accent);
        }
        header.AddChild(_settingsButton);

        _collapseButton = CreateHeaderButton("−  收起", 54);
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
        Button button = new()
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(minimumWidth, 24),
        };
        button.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Caption);
        button.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextSecondary);
        button.AddThemeColorOverride("font_hover_color", TextPrimary);
        button.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            Colors.Transparent,
            Colors.Transparent,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Xs,
            SolverUiTokens.Spacing.Xxs,
            borderWidth: 0));
        button.AddThemeStyleboxOverride("hover", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceHover,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Xs,
            SolverUiTokens.Spacing.Xxs));
        button.AddThemeStyleboxOverride("pressed", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Xs,
            SolverUiTokens.Spacing.Xxs));
        SolverUiTokens.ApplyTextOutline(button);
        button.ApplyLocaleFontSubstitution(FontType.Regular, "font");
        return button;
    }

    private static Control CreateSummarySection()
    {
        _summaryPanel = CreateSectionPanel("SummaryPanel");
        _summaryPanel.MouseFilter = Control.MouseFilterEnum.Pass;
        _summaryPanel.CustomMinimumSize = new Vector2(0, 42);
        VBoxContainer layout = new() { MouseFilter = Control.MouseFilterEnum.Pass };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xxs);
        HBoxContainer statusRow = new()
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        statusRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        _summaryStatusBadge = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(0, 26),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        _summaryStateLabel = CreateTextLabel(
            "等待战斗状态",
            SolverUiTokens.Type.Metric,
            TextMuted,
            FontType.Bold);
        _summaryStatusBadge.AddChild(_summaryStateLabel);
        statusRow.AddChild(_summaryStatusBadge);
        _summaryContextLabel = CreateTextLabel(
            string.Empty,
            SolverUiTokens.Type.Metric,
            SolverUiTokens.Palette.TextSecondary,
            FontType.Bold);
        _summaryContextLabel.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        _summaryContextLabel.CustomMinimumSize = new Vector2(104, 24);
        _summaryContextLabel.ClipText = true;
        statusRow.AddChild(_summaryContextLabel);
        _summaryText = CreateRichText(SolverUiTokens.Type.Metric);
        _summaryText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _summaryText.FitContent = true;
        _summaryText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _summaryText.CustomMinimumSize = new Vector2(0, 24);
        _summaryText.ApplyLocaleFontSubstitution(FontType.Bold, "normal_font");
        _progressText = CreateTextLabel(string.Empty, SolverUiTokens.Type.Metric, TextPrimary, FontType.Bold);
        _progressText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _progressText.CustomMinimumSize = new Vector2(0, 24);
        _progressText.ClipText = true;
        _progressText.Visible = false;
        statusRow.AddChild(_progressText);
        _detailsButton = new SolverDetailsButton
        {
            Visible = false,
        };
        _detailsButton.Pressed += ToggleDetails;
        MarginContainer detailsSlot = new()
        {
            CustomMinimumSize = new Vector2(96, 24),
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
        };
        detailsSlot.AddChild(_detailsButton);
        statusRow.AddChild(detailsSlot);
        layout.AddChild(statusRow);
        layout.AddChild(_summaryText);
        _searchProgressBar = new ProgressBar
        {
            Name = "SearchProgress",
            MinValue = 0,
            MaxValue = SolverSearchProfile.Short.MaxExpandedNodes,
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
        HFlowContainer footer = new()
        {
            Name = "Footer",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ButtonHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        footer.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Sm);
        footer.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Xs);

        _theftPolicyControls = new HBoxContainer
        {
            Name = "TheftPolicy",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _theftPolicyControls.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        _preserveResourcesButton = CreateButton("保牌/保钱", false);
        _preserveResourcesButton.CustomMinimumSize = new Vector2(112, SolverUiTokens.Size.ButtonHeight);
        _preserveResourcesButton.Pressed += () => OnTheftPolicyPressed(SolverTheftPolicy.PreserveResources);
        _theftPolicyControls.AddChild(_preserveResourcesButton);
        _letEscapeButton = CreateButton("放走", false);
        _letEscapeButton.CustomMinimumSize = new Vector2(72, SolverUiTokens.Size.ButtonHeight);
        _letEscapeButton.Pressed += () => OnTheftPolicyPressed(SolverTheftPolicy.LetEscape);
        _theftPolicyControls.AddChild(_letEscapeButton);
        footer.AddChild(_theftPolicyControls);

        _potionBanControls = new HFlowContainer
        {
            Name = "PotionBans",
            Visible = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _potionBanControls.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        footer.AddChild(_potionBanControls);

        _recalculateButton = CreateButton("重新计算", false);
        _recalculateButton.CustomMinimumSize = new Vector2(112, SolverUiTokens.Size.ButtonHeight);
        _recalculateButton.Pressed += OnRecalculatePressed;
        footer.AddChild(_recalculateButton);

        _executeButton = CreateButton("执行本回合", true);
        _renderedExecuteButtonStyle = SolverButtonStyle.Primary;
        _executeButton.CustomMinimumSize = new Vector2(132, SolverUiTokens.Size.ButtonHeight);
        _executeButton.Pressed += OnExecutePressed;
        footer.AddChild(_executeButton);

        _fullAutoButton = CreateButton("全自动：关", false);
        SolverUiTokens.ApplyButtonStyle(_fullAutoButton, SolverButtonStyle.Secondary);
        _renderedFullAutoStyle = false;
        _fullAutoButton.CustomMinimumSize = new Vector2(124, SolverUiTokens.Size.ButtonHeight);
        _fullAutoButton.Pressed += OnFullAutoPressed;
        footer.AddChild(_fullAutoButton);
        return footer;
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
            _summaryContextLabel.Text = context;
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
            text = "你打出了比求解器更好的世界线。" +
                   SolverUiTokens.BugReportUploadInstruction +
                   "这可以更好地推动算法进步！";
            tone = Success;
        }
        else if (SolverController.UnexpectedReplanCount > 0)
        {
            text = "出现计划外重算，可能是模拟或算法问题。" +
                   SolverUiTokens.BugReportUploadInstruction;
            tone = Danger;
        }
        else if (SolverController.BugReportUploadRecommended)
        {
            text = "求解器记录到需要反馈的异常。" +
                   SolverUiTokens.BugReportUploadInstruction;
            tone = Danger;
        }
        else
        {
            text = null;
            tone = TextMuted;
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
        if (_settingsVisible && _settingsPanel?.CommitPending() == false)
            return;
        if (_collapsed)
            SetCollapsed(false);
        _settingsVisible = !_settingsVisible;
        if (_settingsVisible)
            _settingsPanel?.Reload();
        ApplyContentVisibility();
        QueueResponsiveLayout();
        Entry.Logger.Info($"[CombatSolver/Test] UI_ACTION action=settings visible={_settingsVisible}");
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
            _collapseButton.Text = collapsed ? "+  展开" : "−  收起";
        ApplyContentVisibility();
        ApplyResponsiveLayout();
    }

    private static void ApplyContentVisibility()
    {
        if (_mainStack != null)
            _mainStack.Visible = !_settingsVisible || _collapsed;
        if (_body != null)
            _body.Visible = !_collapsed && !_settingsVisible;
        if (_footerDivider != null)
            _footerDivider.Visible = !_collapsed && !_settingsVisible;
        if (_settingsPanel != null)
            _settingsPanel.Visible = !_collapsed && _settingsVisible;
        if (_settingsButton != null)
        {
            _settingsButton.Text = _settingsVisible ? "返回" : "设置";
            _settingsButton.AddThemeColorOverride(
                "font_color",
                _settingsVisible || SolverUiTokens.IsLightTheme
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
        float availableWidth = Math.Max(360f, viewportSize.X - SolverUiTokens.Size.PanelMargin * 2f);
        float availableHeight = Math.Max(SolverUiTokens.Size.CollapsedHeight, viewportSize.Y - SolverUiTokens.Size.PanelMargin * 2f);
        float width = _collapsed
            ? Math.Min(SolverUiTokens.Size.CollapsedWidth, availableWidth)
            : Math.Min(SolverUiTokens.Size.ExpandedMaxWidth, Math.Max(SolverUiTokens.Size.ExpandedMinWidth, viewportSize.X * 0.58f));
        width = Math.Min(width, availableWidth);
        float desiredHeight = _collapsed
            ? SolverUiTokens.Size.CollapsedHeight
            : _panel.GetCombinedMinimumSize().Y;
        float maximumHeight = _settingsVisible ? 540f : SolverUiTokens.Size.ExpandedMaxHeight;
        float height = Math.Min(desiredHeight, Math.Min(maximumHeight, availableHeight));

        ApplyPanelBounds(viewportSize, width, height);
    }

    private static void ApplyPanelBounds(Vector2 viewportSize, float width, float height)
    {
        if (_panel == null)
            return;
        const float edge = 8f;
        float maxX = Math.Max(edge, viewportSize.X - width - edge);
        float maxY = Math.Max(edge, viewportSize.Y - height - edge);
        _panelPosition = new Vector2(
            Math.Clamp(_panelPosition.X, edge, maxX),
            Math.Clamp(_panelPosition.Y, edge, maxY));
        _panel.OffsetLeft = _panelPosition.X;
        _panel.OffsetTop = _panelPosition.Y;
        _panel.OffsetRight = _panelPosition.X + width;
        _panel.OffsetBottom = _panelPosition.Y + height;
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

    private static void ResetOverlayPosition()
    {
        _panelPosition = new Vector2(SolverUiTokens.Size.PanelMargin, SolverUiTokens.Size.PanelMargin);
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
                Show(host, "当前没有进行中的战斗。");
            return;
        }
        SolverController.RequestSearch(host, state, SearchReason.Manual);
    }

    private static void OnExecutePressed()
    {
        NGame? host = NGame.Instance;
        if (host != null && SolverController.IsSearching)
        {
            Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=stop_search");
            SolverController.StopSearchByUser(host);
            return;
        }

        Entry.Logger.Info("[CombatSolver/Test] UI_ACTION action=deploy");
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (host == null || state == null || !CombatManager.Instance.IsInProgress)
        {
            if (host != null)
                Show(host, "当前没有进行中的战斗。");
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
                Show(host, "当前没有进行中的战斗。");
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

}
