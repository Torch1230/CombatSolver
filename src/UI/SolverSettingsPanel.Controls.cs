using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace CombatSolver;

internal sealed partial class SolverSettingsPanel
{
    private static VBoxContainer CreatePageContent(string name)
    {
        VBoxContainer content = new()
        {
            Name = name,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass,
        };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Md);
        return content;
    }

    private static ScrollContainer CreatePageScroll(Control content)
    {
        ScrollContainer scroll = new()
        {
            CustomMinimumSize = new Vector2(0, 120),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            MouseFilter = MouseFilterEnum.Pass,
        };
        scroll.AddChild(content);
        return scroll;
    }

    private static GridContainer CreateSettingsGrid()
    {
        GridContainer grid = new()
        {
            Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Pass,
        };
        grid.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Lg);
        grid.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Sm);
        return grid;
    }

    private static void AddSettingsSection(VBoxContainer page, string title, string description, Control content)
    {
        PanelContainer panel = new() { MouseFilter = MouseFilterEnum.Pass, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised, SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Medium, SolverUiTokens.Spacing.Sm, SolverUiTokens.Spacing.Sm));
        VBoxContainer section = CreatePageContent("SettingsSection");
        section.AddChild(CreateSectionHeading(title));
        Label hint = SolverUiTokens.CreateLabel(description, SolverUiTokens.Type.Caption, SolverUiTokens.Palette.TextMuted);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        section.AddChild(hint);
        section.AddChild(content);
        panel.AddChild(section);
        page.AddChild(panel);
    }

    private static OptionButton CreateOptionInput(float minimumWidth = 126)
    {
        OptionButton input = new()
        {
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(minimumWidth, 32),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        input.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Body);
        input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        input.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme
                ? SolverUiTokens.Palette.Surface
                : SolverUiTokens.Palette.Background,
            SolverUiTokens.IsLightTheme
                ? SolverUiTokens.Palette.Border
                : SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.AddThemeStyleboxOverride("hover", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.Accent,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        if (SolverUiTokens.IsLightTheme)
        {
            input.AddThemeIconOverride(
                "arrow",
                SolverUiTokens.CreateChevronTexture(SolverUiTokens.Palette.TextSecondary));
        }
        SolverUiTokens.ApplyTextOutline(input);
        input.ApplyLocaleFontSubstitution(FontType.Regular, "font");
        return input;
    }

    private LineEdit CreateOptionalDoubleInput(
        double defaultValue,
        Func<SolverSettingsData, double?> getter,
        Func<SolverSettingsData, double?, SolverSettingsData> setter,
        double minimum,
        double maximum)
    {
        LineEdit input = CreateInput(SolverSettings.FormatSeconds(defaultValue));
        _reloadInputs.Add(data => input.Text = getter(data) is { } value
            ? SolverSettings.FormatSeconds(value)
            : string.Empty);
        bool Commit()
        {
            string text = input.Text.Trim();
            if (text.Length == 0)
            {
                if (getter(SolverSettings.Current) == null)
                    return KeepUnchanged(input);
                return SaveSetting(input, setter(SolverSettings.Current, null), SolverText.Get("已保存，下次执行生效"));
            }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || value < minimum || value > maximum)
            {
                ShowInvalid(input, SolverText.Format($"请输入 {minimum:0.###}–{maximum:0.###} 的数字"));
                return false;
            }
            if (getter(SolverSettings.Current) is { } current && current.Equals(value))
                return KeepUnchanged(input);
            return SaveSetting(input, setter(SolverSettings.Current, value), SolverText.Get("已保存，下次执行生效"));
        }
        input.FocusExited += () => Commit();
        input.TextSubmitted += _ => Commit();
        _commitInputs.Add(Commit);
        return input;
    }

    private static LineEdit CreateInput(string defaultText)
    {
        LineEdit input = new()
        {
            PlaceholderText = defaultText,
            ClearButtonEnabled = true,
            SelectAllOnFocus = true,
            CustomMinimumSize = new Vector2(126, 32),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        input.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Body);
        input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        input.AddThemeColorOverride("font_placeholder_color", SolverUiTokens.Palette.TextMuted);
        input.AddThemeColorOverride("caret_color", SolverUiTokens.Palette.Accent);
        input.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            SolverUiTokens.IsLightTheme
                ? SolverUiTokens.Palette.Surface
                : SolverUiTokens.Palette.Background,
            SolverUiTokens.IsLightTheme
                ? SolverUiTokens.Palette.Border
                : SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        input.AddThemeStyleboxOverride("focus", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.Accent,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        SolverUiTokens.ApplyTextOutline(input);
        input.ApplyLocaleFontSubstitution(FontType.Regular, "font");
        input.TextChanged += _ => input.AddThemeColorOverride(
            "font_color",
            SolverUiTokens.Palette.TextPrimary);
        return input;
    }

    private static Label CreateRowLabel(string text, float minimumWidth = 170f)
    {
        Label label = SolverUiTokens.CreateLabel(
            text,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextSecondary,
            FontType.Bold);
        label.CustomMinimumSize = new Vector2(minimumWidth, 32);
        return label;
    }

    /// <summary>成长侧栏也用这个开关，保持两处外观一致。</summary>
    internal static CheckButton CreateToggle()
    {
        CheckButton toggle = new()
        {
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            CustomMinimumSize = SolverUiTokens.IsLightTheme
                ? new Vector2(40, 20)
                : new Vector2(126, 32),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
        };
        toggle.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        toggle.AddThemeColorOverride(
            "font_hover_color",
            SolverUiTokens.IsLightTheme ? SolverUiTokens.Palette.TextPrimary : Colors.White);
        if (SolverUiTokens.IsLightTheme)
        {
            Color trackOff = Color.FromHtml("d9d9d9ff");
            Color trackOn = SolverUiTokens.Palette.Accent;
            toggle.AddThemeStyleboxOverride("base", SolverUiTokens.CreateBox(
                trackOff,
                trackOff,
                SolverUiTokens.Radius.Pill,
                horizontalPadding: 4,
                verticalPadding: 3));
            Texture2D knob = SolverUiTokens.CreateCircleTexture(Colors.White);
            toggle.AddThemeIconOverride("checked", knob);
            toggle.AddThemeIconOverride("unchecked", knob);
            toggle.Toggled += enabled => toggle.AddThemeStyleboxOverride(
                "base",
                SolverUiTokens.CreateBox(
                    enabled ? trackOn : trackOff,
                    enabled ? trackOn : trackOff,
                    SolverUiTokens.Radius.Pill,
                    horizontalPadding: 4,
                    verticalPadding: 3));
        }
        SolverUiTokens.ApplyTextOutline(toggle);
        return toggle;
    }

    private static void StyleSlider(HSlider slider)
    {
        Color track = SolverUiTokens.IsLightTheme
            ? Color.FromHtml("e0e0e0ff")
            : SolverUiTokens.Palette.Border;
        slider.AddThemeStyleboxOverride("slider", SolverUiTokens.CreateBox(
            track,
            track,
            SolverUiTokens.Radius.Pill,
            horizontalPadding: 0,
            verticalPadding: 3,
            borderWidth: 0));
        slider.AddThemeStyleboxOverride("grabber_area", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Accent,
            SolverUiTokens.Palette.Accent,
            SolverUiTokens.Radius.Pill,
            horizontalPadding: 0,
            verticalPadding: 3,
            borderWidth: 0));
        slider.AddThemeStyleboxOverride("grabber_area_highlight", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.AccentHover,
            SolverUiTokens.Palette.AccentHover,
            SolverUiTokens.Radius.Pill,
            horizontalPadding: 0,
            verticalPadding: 3,
            borderWidth: 0));
        slider.AddThemeConstantOverride("center_grabber", 1);
        Texture2D grabber = SolverUiTokens.CreateCircleTexture(SolverUiTokens.Palette.Accent, 16);
        slider.AddThemeIconOverride("grabber", grabber);
        slider.AddThemeIconOverride(
            "grabber_highlight",
            SolverUiTokens.CreateCircleTexture(SolverUiTokens.Palette.AccentHover, 16));
        slider.AddThemeIconOverride(
            "grabber_disabled",
            SolverUiTokens.CreateCircleTexture(SolverUiTokens.Palette.TextMuted, 16));
    }

    private static void AddBasicRow(
        GridContainer grid,
        string label,
        Control input,
        string? tooltip = null)
    {
        Label rowLabel = CreateRowLabel(label, 170);
        rowLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rowLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (!string.IsNullOrEmpty(tooltip))
        {
            ApplyTooltip(rowLabel, tooltip);
            ApplyTooltip(input, tooltip);
        }
        grid.AddChild(rowLabel);
        grid.AddChild(input);
    }

    private static void ApplyTooltip(Control control, string tooltip)
    {
        control.TooltipText = tooltip;
        if (control is Label)
            control.MouseFilter = MouseFilterEnum.Pass;
    }

    private static Control CreateSectionHeading(string text)
    {
        HBoxContainer row = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        row.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        row.AddChild(SolverUiTokens.CreateLabel(
            text,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary,
            FontType.Bold));
        ColorRect divider = new()
        {
            Color = SolverUiTokens.Palette.BorderSubtle,
            CustomMinimumSize = new Vector2(0, 1),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        row.AddChild(divider);
        return row;
    }

    private static void AddGridHeader(GridContainer grid, string text)
    {
        Label label = SolverUiTokens.CreateLabel(
            text,
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextMuted,
            FontType.Bold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        grid.AddChild(label);
    }

    private bool SaveSetting(LineEdit input, SolverSettingsData data, string message)
    {
        if (_loading)
            return true;
        if (data != SolverSettings.Current)
            SolverSettings.Update(data);
        input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        SetStatus(message, SolverUiTokens.Palette.Success);
        return true;
    }

    private static bool KeepUnchanged(LineEdit input)
    {
        input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        return true;
    }

    private void ShowInvalid(LineEdit input, string message)
    {
        input.AddThemeColorOverride("font_color", SolverUiTokens.Palette.Danger);
        SetStatus(message, SolverUiTokens.Palette.Danger);
        input.GrabFocus();
    }
}
