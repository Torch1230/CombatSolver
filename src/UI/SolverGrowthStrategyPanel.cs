using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Localization;

namespace CombatSolver;

internal sealed partial class SolverGrowthStrategyPanel : PanelContainer
{
    internal const float PreferredWidth = 390f;
    private readonly Dictionary<GrowthSource, SpinBox> _budgets = [];
    private readonly List<(GrowthSourceHandle Source, SpinBox Input)> _extraBudgets = [];
    private readonly CheckButton _ignoreLongTermRewards;
    private readonly CheckButton _limitBrightestFlame;
    private readonly SpinBox _brightestFlameLimit;
    private bool _refreshing;
    private bool _disabled;

    public event Action<GrowthValues>? PolicyChanged;
    public event Action<int?>? BrightestFlameLimitChanged;

    /// <summary>「不考虑局外收益」这个总开关变了。</summary>
    public event Action<bool>? IgnoreLongTermRewardsChanged;

    public SolverGrowthStrategyPanel()
    {
        Name = "GrowthStrategyPanel";
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(PreferredWidth, 0);
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface, SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium, SolverUiTokens.Spacing.Sm, SolverUiTokens.Spacing.Sm));
        VBoxContainer layout = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        layout.AddThemeConstantOverride("separation", 12);
        Label heading = SolverUiTokens.CreateLabel(SolverText.Get("成长策略"), 18, SolverUiTokens.Palette.TextPrimary);
        heading.Name = "StrategyHeading";
        layout.AddChild(heading);
        HBoxContainer ignoreRow = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Label ignoreLabel = SolverUiTokens.CreateLabel(
            SolverText.Get("不考虑局外收益"), SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary);
        ignoreLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ignoreLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        ignoreRow.AddChild(ignoreLabel);
        _ignoreLongTermRewards = SolverSettingsPanel.CreateToggle();
        _ignoreLongTermRewards.Name = "IgnoreLongTermRewards";
        _ignoreLongTermRewards.TooltipText =
            SolverText.Get("打开后，金币、永久升级这类只在战斗之外兑现的收益一律不参与打分：既不付出任何战损去换，")
            + SolverText.Get("也不再靠它们在搜索里保留路线。白拿的收益照样拿——最终选择里它仍然排在战损之后当平局的分先手。")
            + SolverText.Get("后期没有商店、不需要这些收益时打开它；收益额度暂停生效，最大生命消耗限制仍然有效。");
        ignoreRow.AddChild(_ignoreLongTermRewards);
        layout.AddChild(ignoreRow);
        VBoxContainer flameRows = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        CardModel flame = ModelDb.Card<BrightestFlame>();
        HBoxContainer flameToggleRow = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Label flameLabel = SolverUiTokens.CreateLabel(SolverText.Format($"限制{flame.Title}的最大生命消耗"), SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary);
        flameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        flameLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        flameToggleRow.AddChild(flameLabel);
        _limitBrightestFlame = SolverSettingsPanel.CreateToggle();
        _limitBrightestFlame.Name = "LimitBrightestFlame";
        _limitBrightestFlame.TooltipText = SolverText.Get("关闭时不限；开启后按整场战斗累计消耗计算，重算和跨回合不会恢复额度。修改后重新计算路线。");
        flameToggleRow.AddChild(_limitBrightestFlame);
        flameRows.AddChild(flameToggleRow);
        _brightestFlameLimit = AddBudgetRow(flameRows, SolverText.Get("每场最多消耗"), flame.Portrait, 1000);
        _brightestFlameLimit.Name = "BrightestFlameMaxHpLossLimit";
        _brightestFlameLimit.TooltipText = SolverText.Get("至亮之焰每场允许消耗的最大生命总量。0 表示求解器不再使用；已手动消耗的额度也计入，其他来源增加最大生命不会返还额度。");
        _limitBrightestFlame.Toggled += _ => PublishFlameLimit();
        _brightestFlameLimit.ValueChanged += _ => PublishFlameLimit();
        Label allowanceLabel = SolverUiTokens.CreateLabel(SolverText.Get("每次收益允许的额外战损"), SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary);
        allowanceLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        ScrollContainer scroll = new()
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            CustomMinimumSize = new Vector2(0, 160),
        };
        VBoxContainer rows = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        flameRows.AddThemeConstantOverride("separation", 8);
        rows.AddChild(flameRows);
        rows.AddChild(new HSeparator());
        rows.AddChild(allowanceLabel);
        foreach (GrowthSource source in Enum.GetValues<GrowthSource>())
        {
            CardModel card = SourceCard(source);
            string title = source == GrowthSource.Goopy
                ? ModelDb.Enchantment<Goopy>().Title.GetFormattedText() + (SolverText.IsEnglish ? " " : "") + card.Title
                : card.Title;
            SpinBox input = AddBudgetRow(rows, title, card.Portrait, 1000);
            input.Name = source.ToString();
            input.TooltipText = SolverText.Format($"{title}：每次实际获得局外收益允许的额外战损（HP）。0 仍优先获取同等战损下的收益；多次成功触发逐次累计。");
            _budgets.Add(source, input);
            input.ValueChanged += _ => Publish();
        }
        // 第三方登记的来源排在原版九行之后，按登记顺序。
        foreach (GrowthSourceMirrors.Entry entry in GrowthSourceMirrors.All)
        {
            (string title, Texture2D? portrait) = ResolveThirdPartyRow(entry);
            SpinBox input = AddBudgetRow(rows, title, portrait, 1000);
            input.Name = entry.Id;
            input.TooltipText = SolverText.Format($"{title}：每次实际获得局外收益允许的额外战损（HP）。0 仍优先获取同等战损下的收益；多次成功触发逐次累计。");
            _extraBudgets.Add((new GrowthSourceHandle(entry.Id), input));
            input.ValueChanged += _ => Publish();
        }
        scroll.AddChild(rows);
        layout.AddChild(scroll);
        AddChild(layout);
        SolverUiTokens.StyleStrategyPanel(this);
        _ignoreLongTermRewards.Toggled += ignore =>
        {
            if (_refreshing)
                return;
            IgnoreLongTermRewardsChanged?.Invoke(ignore);
        };
        Refresh(false);
    }

    private static CardModel SourceCard(GrowthSource source) => source switch
    {
        GrowthSource.HandOfGreed => ModelDb.Card<HandOfGreed>(),
        GrowthSource.TheHunt => ModelDb.Card<TheHunt>(),
        GrowthSource.Feed => ModelDb.Card<Feed>(),
        GrowthSource.Royalties => ModelDb.Card<Royalties>(),
        GrowthSource.Alchemize => ModelDb.Card<Alchemize>(),
        GrowthSource.GeneticAlgorithm => ModelDb.Card<GeneticAlgorithm>(),
        GrowthSource.TheScythe => ModelDb.Card<TheScythe>(),
        GrowthSource.Goopy => ModelDb.Card<DefendIronclad>(),
        GrowthSource.ForbiddenGrimoire => ModelDb.Card<ForbiddenGrimoire>(),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>
    /// 取第三方来源这一行的标题和图标。取牌函数是 mod 提供的，抛异常不该连带整个侧栏起不来：
    /// 这一行退化成「没有图标、标题显示 id」，额度照样能填、照样进搜索。
    /// </summary>
    private static (string Title, Texture2D? Portrait) ResolveThirdPartyRow(GrowthSourceMirrors.Entry entry)
    {
        try
        {
            CardModel card = entry.Card();
            return (entry.Title?.Invoke(card) ?? card.Title, card.Portrait);
        }
        catch (Exception exception)
        {
            Entry.Logger.Warn(
                $"[CombatSolver] 第三方成长来源 {entry.Id} 的取牌或取标题函数抛了异常，"
                + $"这一行退化成纯文字：{exception}");
            return (entry.Id, null);
        }
    }

    private static SpinBox AddBudgetRow(VBoxContainer parent, string title, Texture2D? texture, int maximum)
    {
        PanelContainer card = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.BorderSubtle, SolverUiTokens.Radius.Medium, 10, 8));
        HBoxContainer row = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 48) };
        row.AddThemeConstantOverride("separation", 12);
        if (texture != null)
            row.AddChild(new TextureRect
            {
                Texture = texture, CustomMinimumSize = new Vector2(44, 44),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            });
        Label label = SolverUiTokens.CreateLabel(title, SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(label);
        SpinBox input = new()
        {
            MinValue = 0, MaxValue = maximum, Step = 1, Rounded = true,
            CustomMinimumSize = new Vector2(112, 40), SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Suffix = "HP", UpdateOnTextChanged = false,
        };
        row.AddChild(input);
        input.GetLineEdit().FocusExited += input.Apply;
        card.AddChild(row);
        parent.AddChild(card);
        return input;
    }

    public void Refresh(bool disabled)
    {
        _refreshing = true;
        _disabled = disabled;
        try
        {
            SolverSettingsData settings = SolverSettings.Current;
            _ignoreLongTermRewards.Disabled = disabled;
            _ignoreLongTermRewards.ButtonPressed = settings.IgnoreLongTermRewards;
            _limitBrightestFlame.Disabled = disabled;
            _limitBrightestFlame.SetPressedNoSignal(settings.BrightestFlameMaxHpLossLimit.HasValue);
            _brightestFlameLimit.Editable = !disabled && settings.BrightestFlameMaxHpLossLimit.HasValue;
            if (!_brightestFlameLimit.GetLineEdit().HasFocus())
                _brightestFlameLimit.SetValueNoSignal(settings.BrightestFlameMaxHpLossLimit ?? 2);
            // 总开关打开时下面每一项都不生效，所以灰掉：不是为了拦住输入，是让「填了没用」看得见。
            bool budgetsUsable = !disabled && !settings.IgnoreLongTermRewards;
            foreach ((GrowthSource source, SpinBox input) in _budgets)
            {
                input.Editable = budgetsUsable;
                if (!input.GetLineEdit().HasFocus())
                    input.SetValueNoSignal(settings.GrowthBudgets.Get(source));
            }
            foreach ((GrowthSourceHandle source, SpinBox input) in _extraBudgets)
            {
                input.Editable = budgetsUsable;
                if (!input.GetLineEdit().HasFocus())
                    input.SetValueNoSignal(settings.GrowthBudgets.Get(source));
            }
        }
        finally { _refreshing = false; }
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (IsVisibleInTree() && inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click
            && GetViewport().GuiGetFocusOwner() is LineEdit focused && IsAncestorOf(focused)
            && !new Rect2(Vector2.Zero, focused.Size).HasPoint(focused.GetGlobalTransformWithCanvas().AffineInverse() * click.Position))
            focused.ReleaseFocus();
    }

    internal bool ExerciseOutsideClickForTesting()
    {
        SpinBox input = _budgets[GrowthSource.GeneticAlgorithm];
        LineEdit edit = input.GetLineEdit();
        edit.GrabFocus();
        edit.Text = "7";
        using InputEventMouseButton click = new() { Pressed = true, ButtonIndex = MouseButton.Left, Position = new Vector2(-1, -1) };
        _Input(click);
        return !edit.HasFocus() && input.Value == 7 && SolverSettings.Current.GrowthBudgets.GeneticAlgorithm == 7;
    }

    private void PublishFlameLimit()
    {
        if (_refreshing) return;
        BrightestFlameLimitChanged?.Invoke(_limitBrightestFlame.ButtonPressed
            ? checked((int)_brightestFlameLimit.Value) : null);
    }

    private void Publish()
    {
        if (_refreshing)
            return;
        GrowthValues budgets = default;
        foreach ((GrowthSource source, SpinBox input) in _budgets)
            budgets = budgets.With(source, checked((int)input.Value));
        foreach ((GrowthSourceHandle source, SpinBox input) in _extraBudgets)
            budgets = budgets.With(source, checked((int)input.Value));
        // 侧栏只认得已登记的来源；玩家临时停用某个 mod 期间，设置文件里它那份额度原样留着。
        budgets = budgets with { Extras = SolverSettings.Current.GrowthBudgets.Extras.MergeUnregistered(budgets.Extras) };
        PolicyChanged?.Invoke(budgets);
    }

    internal bool SettingsConfiguredForTesting
        => _budgets.All(pair => (int)pair.Value.Value == SolverSettings.Current.GrowthBudgets.Get(pair.Key))
            && _extraBudgets.All(row => (int)row.Input.Value == SolverSettings.Current.GrowthBudgets.Get(row.Source)
                && row.Input.Editable == (!SolverSettings.Current.IgnoreLongTermRewards && !_disabled))
            && _ignoreLongTermRewards.ButtonPressed == SolverSettings.Current.IgnoreLongTermRewards
            && _budgets.Values.All(input =>
                input.Editable == (!SolverSettings.Current.IgnoreLongTermRewards && !_disabled));

    internal IReadOnlyList<(GrowthSourceHandle Source, SpinBox Input)> ThirdPartyRowsForTesting => _extraBudgets;

    /// <summary>点一下总开关，返回它发出去的新值。</summary>
    internal bool ToggleIgnoreLongTermRewardsForTesting()
    {
        _ignoreLongTermRewards.ButtonPressed = !_ignoreLongTermRewards.ButtonPressed;
        return _ignoreLongTermRewards.ButtonPressed;
    }
}
