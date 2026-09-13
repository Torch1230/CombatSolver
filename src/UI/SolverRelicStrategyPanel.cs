using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class SolverRelicStrategyPanel : PanelContainer
{
    internal const float PreferredWidth = 390f;
    private sealed record Row(RelicCounterCatalog.Entry Entry, CheckButton Enabled, SpinBox Minimum, SpinBox Maximum, SpinBox Hp, SpinBox Priority, Label Status, Control Card, TextureRect Icon);
    private readonly List<Row> _rows = [];
    private readonly CheckButton _enabled;
    private bool _refreshing;
    private readonly VBoxContainer _cards = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly CheckButton _showUnowned;
    private readonly Label _empty;
    private ulong _lastOwnedMask = ulong.MaxValue;
    public event Action<bool, RelicCounterRule[]>? PolicyChanged;

    public SolverRelicStrategyPanel()
    {
        Name = "RelicStrategyPanel";
        Visible = false;
        CustomMinimumSize = new(PreferredWidth, 0);
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.BorderSubtle, SolverUiTokens.Radius.Medium, SolverUiTokens.Spacing.Sm, SolverUiTokens.Spacing.Sm));
        VBoxContainer layout = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        layout.AddThemeConstantOverride("separation", 10);
        AddChild(layout);
        HBoxContainer master = new();
        Label title = SolverUiTokens.CreateLabel(SolverText.Get("遗物计数策略"), SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.Name = "StrategyHeading";
        master.AddChild(title);
        _enabled = SolverSettingsPanel.CreateToggle();
        _enabled.Name = "RelicStrategyEnabled";
        master.AddChild(_enabled);
        layout.AddChild(master);
        HBoxContainer filter = new();
        filter.AddChild(Text("显示未持有"));
        _showUnowned = SolverSettingsPanel.CreateToggle();
        filter.AddChild(_showUnowned);
        layout.AddChild(filter);
        ScrollContainer scroll = new() { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new(0, 160) };
        VBoxContainer rows = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _cards.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _empty = Text("当前没有可卡数的遗物。打开“显示未持有”可提前配置。");
        rows.AddChild(_empty);
        rows.AddChild(_cards);
        foreach (var entry in RelicCounterCatalog.All)
        {
            RelicModel relic = entry.Canonical();
            PanelContainer card = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            card.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(SolverUiTokens.Palette.Surface,
                SolverUiTokens.Palette.BorderSubtle, SolverUiTokens.Radius.Medium, 10, 8));
            VBoxContainer group = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            group.AddThemeConstantOverride("separation", 10);
            card.AddChild(group);
            HBoxContainer heading = new();
            heading.AddThemeConstantOverride("separation", 10);
            TextureRect icon = RelicIcon(relic, 40);
            heading.AddChild(icon);
            VBoxContainer identity = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            Label name = SolverUiTokens.CreateLabel(relic.Title.GetFormattedText(), SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            identity.AddChild(name);
            Label status = Text("未持有");
            identity.AddChild(status);
            heading.AddChild(identity);
            CheckButton toggle = SolverSettingsPanel.CreateToggle();
            toggle.Name = entry.Id + "Enabled";
            heading.AddChild(toggle);
            group.AddChild(heading);
            HBoxContainer values = new();
            values.AddThemeConstantOverride("separation", 12);
            VBoxContainer range = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.6f };
            range.AddChild(Text("结束计数范围"));
            HBoxContainer bounds = new();
            SpinBox minimum = Number(bounds, entry.Period - 1, 62);
            bounds.AddChild(Text("—", localized: true));
            SpinBox maximum = Number(bounds, entry.Period - 1, 62);
            minimum.TooltipText = SolverText.Get("最小");
            maximum.TooltipText = SolverText.Get("最大");
            range.AddChild(bounds);
            values.AddChild(range);
            VBoxContainer cost = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            cost.AddChild(Text("愿付额外战损"));
            HBoxContainer costInput = new();
            SpinBox hp = Number(costInput, 1000, 100);
            hp.Suffix = "HP";
            cost.AddChild(costInput);
            values.AddChild(cost);
            group.AddChild(values);
            HBoxContainer priorityRow = new();
            priorityRow.AddChild(Text("优先级（3 最高）"));
            SpinBox priority = Number(priorityRow, 3, 70);
            priority.MinValue = 1;
            group.AddChild(priorityRow);
            if (entry.Id == RelicCounterId.MeatOnTheBone)
            {
                range.Visible = false;
                values.Visible = false;
                priorityRow.Visible = false;
                group.AddChild(Text("结合其他策略比较净战损，仅为有收益的半血回血额外卖血。"));
            }
            _cards.AddChild(card);
            Row row = new(entry, toggle, minimum, maximum, hp, priority, status, card, icon);
            _rows.Add(row);
            toggle.Toggled += _ => Publish();
            minimum.ValueChanged += _ => { if (!_refreshing && minimum.Value > maximum.Value) maximum.SetValueNoSignal(minimum.Value); Publish(); };
            maximum.ValueChanged += _ => { if (!_refreshing && maximum.Value < minimum.Value) minimum.SetValueNoSignal(maximum.Value); Publish(); };
            hp.ValueChanged += _ => Publish();
            priority.ValueChanged += _ => Publish();
        }
        Button others = new() { Text = SolverText.Get("查看其他计数遗物"), ToggleMode = true };
        VBoxContainer inventory = new() { Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (RelicModel relic in ModelDb.AllRelics.Where(relic => RelicCounterCatalog.Identify(relic) == null
            && relic.GetType().GetProperty(nameof(RelicModel.DisplayAmount))!.DeclaringType != typeof(RelicModel)))
            AddUnavailable(inventory, relic, RelicCounterCatalog.UnavailableReason(relic));
        AddUnavailable(inventory, ModelDb.Relic<Lantern>(), "首回合触发，没有跨战斗计数。");
        others.Toggled += visible => inventory.Visible = visible;
        rows.AddChild(others);
        rows.AddChild(inventory);
        scroll.AddChild(rows);
        layout.AddChild(scroll);
        _enabled.Toggled += _ => Publish();
        _showUnowned.Toggled += _ => Refresh(SolverController.IsDeploying);
        SolverUiTokens.StyleStrategyPanel(this);
        Refresh(false);
    }

    private static Label Text(string text, bool localized = false)
    {
        Label label = SolverUiTokens.CreateLabel(localized ? text : SolverText.Get(text), SolverUiTokens.Type.Caption, SolverUiTokens.Palette.TextSecondary);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    private static TextureRect RelicIcon(RelicModel relic, int size) => new()
    {
        Texture = relic.Icon, CustomMinimumSize = new(size, size),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        SizeFlagsVertical = SizeFlags.ShrinkCenter,
        TooltipText = relic.Title.GetFormattedText(),
    };

    private static void AddUnavailable(VBoxContainer parent, RelicModel relic, string reason)
    {
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(RelicIcon(relic, 32));
        VBoxContainer description = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        description.AddChild(SolverUiTokens.CreateLabel(relic.Title.GetFormattedText(), SolverUiTokens.Type.Body, SolverUiTokens.Palette.TextPrimary));
        description.AddChild(Text(reason));
        row.AddChild(description);
        parent.AddChild(row);
    }

    private static SpinBox Number(HBoxContainer parent, int maximum, int width)
    {
        SpinBox input = new() { MinValue = 0, MaxValue = maximum, Step = 1, Rounded = true,
            CustomMinimumSize = new(width, 40), SizeFlagsHorizontal = SizeFlags.ExpandFill, UpdateOnTextChanged = false };
        input.GetLineEdit().FocusExited += input.Apply;
        parent.AddChild(input);
        return input;
    }

    public void Refresh(bool disabled)
    {
        _refreshing = true;
        try
        {
            var settings = SolverSettings.Current;
            _enabled.Disabled = disabled;
            _enabled.SetPressedNoSignal(settings.RelicStrategyEnabled);
            var state = CombatManager.Instance.DebugOnlyGetState();
            ulong ownedMask = 0;
            foreach (Row row in _rows)
            {
                var rule = settings.RelicCounterRules.SingleOrDefault(rule => rule.Id == row.Entry.Id)
                    ?? new RelicCounterRule(row.Entry.Id, false, row.Entry.Period - 1, row.Entry.Period - 1, 0);
                row.Enabled.Disabled = disabled || !settings.RelicStrategyEnabled;
                row.Enabled.SetPressedNoSignal(rule.Enabled);
                bool editable = !disabled && settings.RelicStrategyEnabled && rule.Enabled;
                foreach (var pair in new[] { (row.Minimum, rule.Minimum), (row.Maximum, rule.Maximum), (row.Hp, rule.HpAllowance), (row.Priority, rule.Priority) })
                {
                    pair.Item1.Editable = editable;
                    if (!pair.Item1.GetLineEdit().HasFocus()) pair.Item1.SetValueNoSignal(pair.Item2);
                }
                bool owned = state?.Players.SelectMany(player => player.Relics).Any(relic => !relic.IsMelted && RelicCounterCatalog.Identify(relic) == row.Entry.Id) == true;
                row.Status.Text = SolverText.Get(owned ? "已持有" : "未持有");
                row.Card.Visible = owned || _showUnowned.ButtonPressed;
                if (owned) ownedMask |= 1UL << (int)row.Entry.Id;
            }
            if (ownedMask != _lastOwnedMask)
            {
                int position = 0;
                foreach (Row row in _rows.Where(row => (ownedMask & (1UL << (int)row.Entry.Id)) != 0)
                    .Concat(_rows.Where(row => (ownedMask & (1UL << (int)row.Entry.Id)) == 0)))
                    _cards.MoveChild(row.Card, position++);
                _lastOwnedMask = ownedMask;
            }
            _empty.Visible = ownedMask == 0 && !_showUnowned.ButtonPressed;
        }
        finally { _refreshing = false; }
    }

    private void Publish()
    {
        if (_refreshing) return;
        var rules = _rows.Select(row => new RelicCounterRule(row.Entry.Id, row.Enabled.ButtonPressed,
            (int)row.Minimum.Value, (int)row.Maximum.Value, (int)row.Hp.Value, (int)row.Priority.Value)).ToArray();
        PolicyChanged?.Invoke(_enabled.ButtonPressed, rules);
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (IsVisibleInTree() && inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click
            && GetViewport().GuiGetFocusOwner() is LineEdit focused && IsAncestorOf(focused)
            && !new Rect2(Vector2.Zero, focused.Size).HasPoint(focused.GetGlobalTransformWithCanvas().AffineInverse() * click.Position))
            focused.ReleaseFocus();
    }

    internal bool ExerciseControlsForTesting()
    {
        bool? enabled = null;
        RelicCounterRule[]? changed = null;
        PolicyChanged += (on, rules) => { enabled = on; changed = rules; };
        Row first = _rows[0];
        first.Hp.Value = 7;
        first.Enabled.ButtonPressed = false;
        bool independent = changed is { Length: 11 } && !changed[0].Enabled && changed[0].HpAllowance == 7
            && changed.Skip(1).Where(rule => rule.Id != RelicCounterId.MeatOnTheBone).All(rule => rule.Enabled);
        _enabled.ButtonPressed = false;
        return independent && enabled == false && changed![0].HpAllowance == 7
            && _rows.All(row => row.Icon.Texture != null && row.Card.GetParent() == _cards);
    }
}
