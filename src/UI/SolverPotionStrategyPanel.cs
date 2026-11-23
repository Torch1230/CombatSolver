using CombatSolver.HotKernel;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class SolverPotionStrategyPanel : PanelContainer
{
    internal const float PreferredWidth = 360f;
    private const float CardMinimumWidth = 280f;
    private readonly GridContainer _cards;
    private readonly Button _onlyForced;
    private readonly Button _resetAllThresholds;
    private readonly List<ThresholdRowBinding> _thresholdRows = [];
    public event Action? CloseRequested;
    private string? _renderedSignature;
    private bool _refreshingThresholds;

    private readonly record struct HeldPotionRow(
        int Slot,
        PotionModel Potion,
        SolverPotionDirective Directive,
        bool Searchable,
        int DefaultThreshold);

    private readonly record struct ThresholdRowBinding(
        string PotionId,
        SolverPotionDirective Directive,
        int DefaultThreshold,
        SpinBox Input,
        Button ResetButton);

    public SolverPotionStrategyPanel()
    {
        Name = "PotionStrategyPanel";
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(PreferredWidth, 0);
        SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Sm));

        VBoxContainer layout = new()
        {
            Name = "PotionStrategyLayout",
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        Label heading = SolverUiTokens.CreateLabel(
            SolverText.Get("药水策略"),
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary,
            FontType.Bold);
        heading.Name = "StrategyHeading";
        heading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        HBoxContainer headingRow = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        headingRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        headingRow.AddChild(heading);

        _resetAllThresholds = SolverUiTokens.CreateButton(SolverText.Get("重置阈值"), SolverButtonStyle.Secondary);
        _resetAllThresholds.Name = "ResetPotionThresholds";
        _resetAllThresholds.TooltipText = SolverText.Get("恢复全部药水的默认省血阈值");
        _resetAllThresholds.Pressed += ResetAllThresholds;
        headingRow.AddChild(_resetAllThresholds);

        Button close = SolverUiTokens.CreateButton(SolverText.Get("收起"), SolverButtonStyle.Secondary);
        close.Name = "CloseStrategyPanel";
        close.Pressed += () => CloseRequested?.Invoke();
        headingRow.AddChild(close);
        layout.AddChild(headingRow);

        GridContainer presets = new()
        {
            Name = "PotionPresets",
            Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        presets.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Sm);
        presets.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Sm);
        presets.AddChild(CreatePresetButton(SolverText.Get("全部智能"), PotionStrategyPreset.AllSmart));
        presets.AddChild(CreatePresetButton(SolverText.Get("全部保护"), PotionStrategyPreset.AllProtected));
        presets.AddChild(CreatePresetButton(SolverText.Get("全部强制"), PotionStrategyPreset.AllForced));
        _onlyForced = CreatePresetButton(SolverText.Get("仅用已强制"), PotionStrategyPreset.OnlyForced);
        presets.AddChild(_onlyForced);
        layout.AddChild(presets);

        _cards = new GridContainer
        {
            Name = "PotionCards",
            Columns = 1,
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _cards.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Sm);
        _cards.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Sm);
        ScrollContainer scroll = new()
        {
            Name = "PotionCardScroll",
            CustomMinimumSize = new Vector2(0, 220),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            MouseFilter = MouseFilterEnum.Pass,
        };
        scroll.AddChild(_cards);
        layout.AddChild(scroll);
        AddChild(layout);
        SolverUiTokens.StyleStrategyPanel(this);
        Resized += UpdateGridColumns;
        SyncThresholdInputs(controlsDisabled: false);
    }

    public event Action<int, string, SolverPotionDirective>? DirectiveChanged;
    public event Action<PotionStrategyPreset>? PresetRequested;

    internal int RowCountForTesting { get; private set; }
    internal bool RowsUseIconAndTextForTesting { get; private set; }
    internal bool UsesGridCardsForTesting { get; private set; }
    internal bool IsSlimForTesting
        => CustomMinimumSize.X == PreferredWidth && _cards.Columns == 1;
    internal bool HasPresetControlsForTesting { get; private set; }

    public void Refresh(CombatState? state, bool controlsDisabled)
    {
        Player? player = state == null ? null : LocalContext.GetMe(state);
        bool hasRenewableRock = player?.Relics.Any(static r => !r.IsMelted && r is PetrifiedToad) ?? false;
        int playerMaxHp = player?.Creature.MaxHp ?? 0;

        List<HeldPotionRow> potions = [];
        if (player != null)
        {
            for (int slot = 0; slot < player.PotionSlots.Count; slot++)
            {
                PotionModel? potion = player.GetPotionAtSlotIndex(slot);
                if (potion == null)
                    continue;
                HotCustomPotionId catalogId = HotPotionCatalog.FindByEntry(potion.Id.Entry);
                bool searchable = catalogId != HotCustomPotionId.None
                    && HotPotionCatalog.Get(catalogId).IsCombatUsable;
                int defaultThreshold = searchable
                    ? PotionValuationRegistry.DefaultStrategicHpCost(potion, hasRenewableRock, playerMaxHp)
                    : 0;
                potions.Add(new HeldPotionRow(
                    slot,
                    potion,
                    SolverController.ResolvePotionDirective(state!, slot, potion.Id.Entry),
                    searchable,
                    defaultThreshold));
            }
        }

        string signature = string.Join('|', potions.Select(item =>
            $"{item.Slot}:{item.Potion.Id.Entry}:{item.Directive}:{item.Searchable}:{item.DefaultThreshold}"))
            + $";disabled={controlsDisabled}";
        if (!string.Equals(signature, _renderedSignature, StringComparison.Ordinal))
        {
            _renderedSignature = signature;
            RebuildRows(potions, controlsDisabled);
            _onlyForced.Disabled = controlsDisabled
                || !potions.Any(item => item.Searchable && item.Directive == SolverPotionDirective.Force);
            HasPresetControlsForTesting = !controlsDisabled
                && GetNode<GridContainer>("PotionStrategyLayout/PotionPresets").GetChildCount() == 4;
        }

        SyncThresholdInputs(controlsDisabled);
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (IsVisibleInTree()
            && inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click
            && GetViewport().GuiGetFocusOwner() is LineEdit focused
            && IsAncestorOf(focused)
            && !new Rect2(Vector2.Zero, focused.Size).HasPoint(
                focused.GetGlobalTransformWithCanvas().AffineInverse() * click.Position))
        {
            focused.ReleaseFocus();
        }
    }

    private void SyncThresholdInputs(bool controlsDisabled)
    {
        _refreshingThresholds = true;
        try
        {
            PotionHpThresholds current = SolverSettings.ResolvePotionThresholds(SolverSettings.Current);
            _resetAllThresholds.Disabled = controlsDisabled || current.IsEmpty;

            foreach (ThresholdRowBinding row in _thresholdRows)
            {
                bool hasOverride = current.TryGetOverride(row.PotionId, out int overrideHp);
                int effectiveValue = hasOverride ? overrideHp : row.DefaultThreshold;
                row.Input.Editable = !controlsDisabled && row.Directive == SolverPotionDirective.Smart;
                if (!row.Input.GetLineEdit().HasFocus())
                    row.Input.SetValueNoSignal(effectiveValue);
                row.ResetButton.Visible = hasOverride;
                row.ResetButton.Disabled = controlsDisabled;
            }
        }
        finally
        {
            _refreshingThresholds = false;
        }
    }

    private void PublishThresholdEdit(string potionId, int value, int defaultThreshold)
    {
        if (_refreshingThresholds)
            return;
        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance?.DebugOnlyGetState();
        SolverController.SetPotionThresholdOverride(host, state, potionId, value, defaultThreshold);
        SyncThresholdInputs(controlsDisabled: false);
    }

    private void ResetSingleThreshold(string potionId)
    {
        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance?.DebugOnlyGetState();
        SolverController.SetPotionThresholdOverride(host, state, potionId, thresholdHp: null);
        SyncThresholdInputs(controlsDisabled: false);
    }

    private void ResetAllThresholds()
    {
        NGame? host = NGame.Instance;
        CombatState? state = CombatManager.Instance?.DebugOnlyGetState();
        SolverController.ResetAllPotionThresholdOverrides(host, state);
        SyncThresholdInputs(controlsDisabled: false);
    }

    private Button CreatePresetButton(string text, PotionStrategyPreset preset)
    {
        Button button = SolverUiTokens.CreateButton(text, SolverButtonStyle.Secondary);
        button.Name = preset.ToString();
        button.CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ButtonHeight);
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        button.Pressed += () => PresetRequested?.Invoke(preset);
        return button;
    }

    public void Invalidate() => _renderedSignature = null;

    private void RebuildRows(
        IReadOnlyList<HeldPotionRow> potions,
        bool controlsDisabled)
    {
        _thresholdRows.Clear();
        foreach (Node child in _cards.GetChildren())
        {
            _cards.RemoveChild(child);
            child.QueueFree();
        }

        RowCountForTesting = potions.Count;
        RowsUseIconAndTextForTesting = potions.Count > 0;
        UsesGridCardsForTesting = potions.Count > 0;
        if (potions.Count == 0)
        {
            Label empty = SolverUiTokens.CreateLabel(
                SolverText.Get("当前没有药水"),
                SolverUiTokens.Type.Body,
                SolverUiTokens.Palette.TextMuted);
            empty.CustomMinimumSize = new Vector2(0, 32);
            _cards.AddChild(empty);
            return;
        }

        PotionHpThresholds currentThresholds = SolverSettings.ResolvePotionThresholds(SolverSettings.Current);
        foreach (HeldPotionRow row in potions)
            _cards.AddChild(CreatePotionCard(row, currentThresholds, controlsDisabled));
        Callable.From(UpdateGridColumns).CallDeferred();
    }

    private Control CreatePotionCard(
        HeldPotionRow row,
        PotionHpThresholds currentThresholds,
        bool controlsDisabled)
    {
        int slot = row.Slot;
        PotionModel potion = row.Potion;
        SolverPotionDirective directive = row.Directive;
        bool searchable = row.Searchable;
        int defaultThreshold = row.DefaultThreshold;
        string potionId = potion.Id.Entry;
        string potionTitle = potion.Title.GetFormattedText();

        PanelContainer card = new()
        {
            Name = $"PotionStrategyCard{slot}",
            CustomMinimumSize = new Vector2(CardMinimumWidth, 64),
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        card.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Sm));
        VBoxContainer content = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 8);
        HBoxContainer layout = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Begin,
        };
        layout.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);

        TextureRect icon = new()
        {
            Name = "PotionIcon",
            Texture = potion.Image,
            CustomMinimumSize = new Vector2(44, 44),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        layout.AddChild(icon);

        Label title = SolverUiTokens.CreateLabel(
            $"#{slot + 1} {potionTitle}",
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary,
            FontType.Bold);
        title.Name = "PotionTitle";
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.HorizontalAlignment = HorizontalAlignment.Left;
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        title.CustomMinimumSize = new Vector2(0, 44);
        title.TooltipText = potionTitle;
        layout.AddChild(title);

        Button input = CreateDirectiveToggle(directive);
        input.Name = "PotionDirectiveToggle";
        input.Disabled = controlsDisabled || !searchable;
        if (!searchable)
        {
            input.TooltipText = SolverText.Get("该药水不可指定使用策略");
        }
        else
        {
            input.Pressed += () =>
            {
                directive = NextDirective(directive);
                ApplyDirectiveToggle(input, directive);
                DirectiveChanged?.Invoke(slot, potionId, directive);
            };
        }
        content.AddChild(layout);
        content.AddChild(input);

        if (searchable)
        {
            bool hasOverride = currentThresholds.TryGetOverride(potionId, out int overrideHp);
            int effectiveValue = hasOverride ? overrideHp : defaultThreshold;
            string tooltip = directive == SolverPotionDirective.Smart
                ? SolverText.Format($"{potionTitle}：智能模式下使用该药水需至少省下这么多等价血量（默认 {defaultThreshold} HP）。按药水类型保存，修改后立即重新计算。")
                : SolverText.Format($"{potionTitle}：当前为禁用或强制模式，省血阈值暂不生效（切回智能使用后生效，默认 {defaultThreshold} HP）。");

            HBoxContainer thresholdRow = new()
            {
                Name = "PotionThresholdRow",
                MouseFilter = MouseFilterEnum.Pass,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 38),
            };
            thresholdRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);

            Label thresholdLabel = SolverUiTokens.CreateLabel(
                SolverText.Get("省血阈值"),
                SolverUiTokens.Type.Body,
                directive == SolverPotionDirective.Smart
                    ? SolverUiTokens.Palette.TextPrimary
                    : SolverUiTokens.Palette.TextMuted);
            thresholdLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            thresholdLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            thresholdLabel.TooltipText = tooltip;
            thresholdRow.AddChild(thresholdLabel);

            SpinBox thresholdInput = new()
            {
                Name = "PotionThresholdInput",
                MinValue = PotionHpThresholds.MinValue,
                MaxValue = PotionHpThresholds.MaxValue,
                Step = 1,
                Rounded = true,
                CustomMinimumSize = new Vector2(108, 36),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                Suffix = "HP",
                UpdateOnTextChanged = false,
                Editable = !controlsDisabled && directive == SolverPotionDirective.Smart,
                TooltipText = tooltip,
            };
            thresholdInput.SetValueNoSignal(effectiveValue);
            thresholdInput.GetLineEdit().FocusExited += thresholdInput.Apply;
            thresholdInput.ValueChanged += value =>
                PublishThresholdEdit(potionId, (int)Math.Round(value), defaultThreshold);
            thresholdRow.AddChild(thresholdInput);

            Button resetButton = SolverUiTokens.CreateButton(
                SolverText.Get("默认"),
                SolverButtonStyle.Secondary);
            resetButton.Name = "ResetPotionThreshold";
            resetButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            resetButton.Visible = hasOverride;
            resetButton.Disabled = controlsDisabled;
            resetButton.TooltipText = SolverText.Format($"恢复默认省血阈值（{defaultThreshold} HP）");
            resetButton.Pressed += () => ResetSingleThreshold(potionId);
            thresholdRow.AddChild(resetButton);

            _thresholdRows.Add(new ThresholdRowBinding(
                potionId,
                directive,
                defaultThreshold,
                thresholdInput,
                resetButton));
            content.AddChild(thresholdRow);
        }

        card.AddChild(content);
        SolverUiTokens.StyleStrategyText(card);
        return card;
    }

    private void UpdateGridColumns()
        => _cards.Columns = 1;

    private static Button CreateDirectiveToggle(SolverPotionDirective directive)
    {
        Button input = new()
        {
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(40, 40),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        input.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Body);
        input.ApplyLocaleFontSubstitution(FontType.Bold, "font");
        SolverUiTokens.ApplyTextOutline(input);
        ApplyDirectiveToggle(input, directive);
        return input;
    }

    private static SolverPotionDirective NextDirective(SolverPotionDirective directive)
        => directive switch
        {
            SolverPotionDirective.Smart => SolverPotionDirective.Disabled,
            SolverPotionDirective.Disabled => SolverPotionDirective.Force,
            _ => SolverPotionDirective.Smart,
        };

    private static void ApplyDirectiveToggle(Button input, SolverPotionDirective directive)
    {
        (string symbol, string description, Color tone) = directive switch
        {
            SolverPotionDirective.Disabled => ("✕", SolverText.Get("禁用 / 保护"), SolverUiTokens.Palette.Danger),
            SolverPotionDirective.Force => ("✓", SolverText.Get("强制使用"), SolverUiTokens.Palette.Success),
            _ => ("—", SolverText.Get("智能使用"), SolverUiTokens.Palette.TextSecondary),
        };
        input.Text = $"{symbol}  {description}";
        input.TooltipText = SolverText.Format($"{description}（点击切换）");
        input.AddThemeColorOverride("font_color", tone);
        input.AddThemeColorOverride("font_hover_color", tone.Lightened(0.12f));
        input.AddThemeColorOverride("font_pressed_color", tone.Darkened(0.12f));
        input.AddThemeColorOverride("font_disabled_color", SolverUiTokens.Palette.TextMuted);

        Color background = SolverUiTokens.Palette.SurfaceRaised;
        Color border = SolverUiTokens.Palette.BorderSubtle;
        Color hoverBg = SolverUiTokens.Palette.SurfaceHover;
        Color hoverBorder = SolverUiTokens.Palette.Border;

        input.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            background,
            border,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.AddThemeStyleboxOverride("hover", SolverUiTokens.CreateBox(
            hoverBg,
            hoverBorder,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.AddThemeStyleboxOverride("pressed", SolverUiTokens.CreateBox(
            background.Darkened(0.12f),
            border,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.AddThemeStyleboxOverride("disabled", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Background,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
    }
}
