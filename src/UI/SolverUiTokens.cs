using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace CombatSolver;

internal enum SolverButtonStyle
{
    Secondary,
    Primary,
    Positive,
    Danger,
}

internal static class SolverUiTokens
{
    private static SolverOverlayTheme _activeTheme = SolverOverlayTheme.Dark;

    public static bool IsLightTheme => _activeTheme == SolverOverlayTheme.Light;

    public static void ConfigureTheme(SolverOverlayTheme theme)
        => _activeTheme = theme;

    public static string BugReportUploadInstruction => SolverText.Get("请在设置中点击“上传问题包”提交日志。");
    public static string BugReportUploadInstructionRichText
        => $"[color={Palette.WarningHex}]{BugReportUploadInstruction}[/color]";
    public static string ParallelSearchFailureInstruction =>
        SolverText.Get("请先在设置中点击“上传问题包”提交日志，再将“搜索并行度”改为“关闭（单线程）”后重试。");
    public static string SearchFailureInstructionRichText(bool parallelSearchWasEnabled)
        => $"[color={Palette.WarningHex}]" +
           $"{(parallelSearchWasEnabled ? ParallelSearchFailureInstruction : BugReportUploadInstruction)}[/color]";

    public static string AdaptRichTextToActiveTheme(string text)
    {
        (string Dark, string Light, string Active)[] colors =
        [
            ("#b8c0cc", "#5f5f5f", Palette.TextSecondaryHex),
            ("#858f9f", "#8a8a8a", Palette.TextMutedHex),
            ("#5c9fc7", "#0078d4", Palette.AccentHex),
            ("#d6a34c", "#9d5d00", Palette.WarningHex),
            ("#e26666", "#c42b1c", Palette.DangerHex),
            ("#69b77d", "#0f7b0f", Palette.SuccessHex),
        ];
        foreach ((string dark, string light, string active) in colors)
        {
            text = text.Replace(dark, active, StringComparison.OrdinalIgnoreCase)
                .Replace(light, active, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
    public static class Spacing
    {
        public const int Xxs = 2;
        public const int Xs = 4;
        public const int Sm = 8;
        public const int Md = 12;
        public const int Lg = 16;
    }

    public static class Radius
    {
        public const int Small = 4;
        public const int Medium = 6;
        public const int Pill = 6;
        public const int Large = 8;
    }

    public static class Type
    {
        public const int Title = 16;
        public const int Metric = 15;
        public const int Body = 14;
        public const int Caption = 13;
        public const int Outline = 0;
    }

    public static class Size
    {
        public const float PanelMargin = 24f;
        public const float ExpandedMaxWidth = 820f;
        public const float ExpandedMaxHeight = 440f;
        public const float ExpandedMinWidth = 560f;
        public const float CollapsedWidth = 520f;
        public const float CollapsedHeight = 120f;
        public const float RouteViewportHeight = 148f;
        public const float RouteViewportHeightWithDetails = 96f;
        public const float RouteRowHeight = 44f;
        public const float ActionPillHeight = 28f;
        public const float TurnColumnWidth = 88f;
        public const float OutcomeColumnWidth = 238f;
        public const float MetricsDamageWidth = 92f;
        public const float MetricsHpWidth = 64f;
        public const float MetricsEnergyWidth = 52f;
        public const float ButtonHeight = 34f;
        public const float ResizeEdgeThickness = 8f;
        public const int ResizeGripSize = 20;
    }

    public static class Palette
    {
        public static Color Background => Pick("101216f5", "f3f3f3ff");
        public static Color Surface => Pick("191c22fa", "ffffffff");
        public static Color SurfaceRaised => Pick("23272ffb", "f7f9fbff");
        public static Color SurfaceHover => Pick("2b3039ff", "efefefff");
        public static Color Border => Pick("3a404aeb", "e0e0e0ff");
        public static Color BorderSubtle => Pick("2a2f38cc", "eaeaeaff");
        public static Color Accent => Pick("5c9fc7ff", "0078d4ff");
        public static Color AccentHover => Pick("73b4d8ff", "106ebeff");
        public static Color TextPrimary => Pick("eef1f6ff", "1b1b1bff");
        public static Color TextSecondary => Pick("b8c0ccff", "5f5f5fff");
        public static Color TextMuted => Pick("858f9fff", "8a8a8aff");
        public static Color TextOutline => Pick("080a0de6", "ffffff00");
        public static Color Warning => Pick("d6a34cff", "9d5d00ff");
        public static Color Danger => Pick("e26666ff", "c42b1cff");
        public static Color Success => Pick("69b77dff", "0f7b0fff");
        public static Color Positive => Pick("34764fff", "107c10ff");
        public static Color PositiveHover => Pick("428d60ff", "0e6e0eff");

        public static Color Attack => Pick("d96363ff", "c42b1cff");
        public static Color Skill => Pick("5b91d1ff", "0067c0ff");
        public static Color Power => Pick("d7a84fff", "9d5d00ff");
        public static Color Negative => Pick("9b70c9ff", "6b4fa0ff");
        public static Color Potion => Pick("55b9a5ff", "00786cff");
        public static Color Choice => Pick("ff8533ff", "d45500ff");
        public static Color ProgressBackground => IsLightTheme ? Color.FromHtml("e8e8e8ff") : Background;
        public static Color ProgressFill => IsLightTheme ? Accent : Accent.Darkened(0.12f);
        public static Color CompletedActionModulate => IsLightTheme
            ? new Color(0.66f, 0.68f, 0.72f, 0.55f)
            : new Color(0.54f, 0.58f, 0.66f, 0.52f);
        public static Color ActiveActionModulate => IsLightTheme
            ? new Color(0.45f, 0.72f, 0.98f, 1f)
            : new Color(1f, 0.88f, 0.48f, 1f);

        public static string TextSecondaryHex => IsLightTheme ? "#5f5f5f" : "#b8c0cc";
        public static string TextMutedHex => IsLightTheme ? "#8a8a8a" : "#858f9f";
        public static string AccentHex => IsLightTheme ? "#0078d4" : "#5c9fc7";
        public static string WarningHex => IsLightTheme ? "#9d5d00" : "#d6a34c";
        public static string DangerHex => IsLightTheme ? "#c42b1c" : "#e26666";
        public static string SuccessHex => IsLightTheme ? "#0f7b0f" : "#69b77d";

        private static Color Pick(string dark, string light)
            => Color.FromHtml(IsLightTheme ? light : dark);
    }

    public static StyleBoxFlat CreateBox(
        Color background,
        Color border,
        int radius = Radius.Medium,
        int horizontalPadding = Spacing.Sm,
        int verticalPadding = Spacing.Sm,
        int borderWidth = 1,
        bool shadow = false)
    {
        return new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomRight = radius,
            CornerRadiusBottomLeft = radius,
            ContentMarginLeft = horizontalPadding,
            ContentMarginTop = verticalPadding,
            ContentMarginRight = horizontalPadding,
            ContentMarginBottom = verticalPadding,
            ShadowColor = shadow
                ? new Color(0f, 0f, 0f, IsLightTheme ? 0.08f : 0.38f)
                : Godot.Colors.Transparent,
            ShadowSize = shadow ? 8 : 0,
        };
    }

    public static Label CreateLabel(
        string text,
        int fontSize,
        Color color,
        FontType fontType = FontType.Bold,
        int outlineSize = -1,
        Color? outlineColor = null)
    {
        Label label = new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        ApplyTextOutline(label, outlineSize, outlineColor);
        label.ApplyLocaleFontSubstitution(fontType, "font");
        return label;
    }

    public static RichTextLabel CreateRichText(
        int fontSize,
        int outlineSize = -1,
        Color? outlineColor = null)
    {
        RichTextLabel label = new()
        {
            BbcodeEnabled = true,
            FitContent = false,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("normal_font_size", fontSize);
        label.AddThemeFontSizeOverride("bold_font_size", fontSize);
        label.AddThemeFontSizeOverride("italics_font_size", fontSize);
        label.AddThemeFontSizeOverride("bold_italics_font_size", fontSize);
        label.AddThemeFontSizeOverride("mono_font_size", fontSize);
        label.AddThemeColorOverride("default_color", Palette.TextPrimary);
        ApplyTextOutline(label, outlineSize, outlineColor);
        label.ApplyLocaleFontSubstitution(FontType.Bold, "normal_font");
        label.ApplyLocaleFontSubstitution(FontType.Bold, "bold_font");
        label.ApplyLocaleFontSubstitution(FontType.Italic, "italics_font");
        return label;
    }

    public static Button CreateButton(string text, SolverButtonStyle style)
    {
        Button button = new()
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(0, Size.ButtonHeight),
        };
        button.AddThemeFontSizeOverride("font_size", Type.Body);
        button.AddThemeColorOverride("font_color", Palette.TextPrimary);
        button.AddThemeColorOverride("font_hover_color", IsLightTheme ? Palette.TextPrimary : Godot.Colors.White);
        button.AddThemeColorOverride("font_pressed_color", IsLightTheme ? Palette.TextPrimary : Godot.Colors.White);
        button.AddThemeColorOverride("font_disabled_color", Palette.TextMuted);
        ApplyTextOutline(button);
        ApplyButtonStyle(button, style);
        return button;
    }

    public static void ApplyButtonStyle(Button button, SolverButtonStyle style)
    {
        const int radius = Radius.Medium;
        const int hPad = Spacing.Sm;
        const int vPad = Spacing.Xs;

        if (!IsLightTheme)
        {
            (Color darkBackground, Color darkBorder, Color darkHover, Color darkPressed) = style switch
            {
                SolverButtonStyle.Primary => (
                    Palette.Accent.Darkened(0.12f),
                    Palette.Accent.Lightened(0.08f),
                    Palette.AccentHover,
                    Palette.Accent.Darkened(0.24f)),
                SolverButtonStyle.Positive => (
                    Palette.Positive,
                    Palette.Success,
                    Palette.PositiveHover,
                    Palette.Positive.Darkened(0.18f)),
                SolverButtonStyle.Danger => (
                    Palette.Danger.Darkened(0.22f),
                    Palette.Danger,
                    Palette.Danger.Lightened(0.08f),
                    Palette.Danger.Darkened(0.32f)),
                _ => (
                    Palette.SurfaceRaised,
                    Palette.BorderSubtle,
                    Palette.SurfaceHover,
                    Palette.Surface),
            };

            button.AddThemeStyleboxOverride("normal", CreateBox(
                darkBackground, darkBorder, radius, hPad, vPad));
            button.AddThemeStyleboxOverride("hover", CreateBox(
                darkHover, darkBorder.Lightened(0.12f), radius, hPad, vPad));
            button.AddThemeStyleboxOverride("pressed", CreateBox(
                darkPressed, darkBorder, radius, hPad, vPad));
            button.AddThemeStyleboxOverride("disabled", CreateBox(
                Palette.Background, Palette.BorderSubtle, radius, hPad, vPad));
            button.AddThemeStyleboxOverride("focus", CreateBox(
                darkHover, Palette.Accent, radius, hPad, vPad));
            button.AddThemeColorOverride("font_color", Palette.TextPrimary);
            button.AddThemeColorOverride("font_hover_color", Godot.Colors.White);
            button.AddThemeColorOverride("font_pressed_color", Godot.Colors.White);
            button.AddThemeColorOverride("font_disabled_color", Palette.TextMuted);
            ApplyButtonFont(button);
            return;
        }

        (Color background, Color border, Color hover, Color pressed, Color font) = style switch
        {
            SolverButtonStyle.Primary => (
                Palette.Accent,
                Palette.Accent,
                Palette.AccentHover,
                Palette.Accent.Darkened(0.22f),
                Godot.Colors.White),
            SolverButtonStyle.Positive => (
                Palette.Positive,
                Palette.Positive,
                Palette.PositiveHover,
                Palette.Positive.Darkened(0.18f),
                Godot.Colors.White),
            SolverButtonStyle.Danger => (
                Palette.Danger,
                Palette.Danger,
                Palette.Danger.Lightened(0.08f),
                Palette.Danger.Darkened(0.18f),
                Godot.Colors.White),
            _ => (
                Palette.Surface,
                Palette.Border,
                Palette.SurfaceHover,
                Palette.BorderSubtle,
                Palette.TextPrimary),
        };
        button.AddThemeStyleboxOverride("normal", CreateBox(background, border, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("hover", CreateBox(hover, border.Darkened(0.12f), radius, hPad, vPad));
        button.AddThemeStyleboxOverride("pressed", CreateBox(pressed, border, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("disabled", CreateBox(Palette.Background, Palette.BorderSubtle, radius, hPad, vPad));
        button.AddThemeStyleboxOverride("focus", CreateBox(hover, Palette.Accent, radius, hPad, vPad));
        button.AddThemeColorOverride("font_color", font);
        button.AddThemeColorOverride("font_hover_color", font);
        button.AddThemeColorOverride("font_pressed_color", font);
        button.AddThemeColorOverride("font_disabled_color", Palette.TextMuted);
        ApplyButtonFont(button);
    }

    private static void ApplyButtonFont(Button button)
    {
        button.ApplyLocaleFontSubstitution(FontType.Bold, "font");
    }

    internal static void StyleStrategyPanel(Control root)
    {
        if (root is PanelContainer)
            root.AddThemeStyleboxOverride("panel", CreateBox(
                new Color(Palette.Surface, 1f), Palette.BorderSubtle, Radius.Medium, 12, 12));
        StyleStrategyText(root);
    }

    internal static void StyleStrategyText(Node root)
    {
        if (root is Control control && root is Label or Button or LineEdit)
        {
            int size = root.Name == "StrategyHeading" ? Type.Title : root is Label label
                && label.GetThemeFontSize("font_size") == Type.Caption ? Type.Caption : Type.Body;
            control.AddThemeFontSizeOverride("font_size", size);
            ApplyTextOutline(control, 0);
            if (control is LineEdit)
            {
                control.ApplyLocaleFontSubstitution(FontType.Regular, "font");
            }
            else
            {
                control.ApplyLocaleFontSubstitution(FontType.Bold, "font");
            }
        }
        foreach (Node child in root.GetChildren()) StyleStrategyText(child);
    }

    public static Texture2D CreateCircleTexture(Color color, int size = 12)
    {
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float center = (size - 1) / 2f;
        float radius = center;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                if (dx * dx + dy * dy <= radius * radius)
                    image.SetPixel(x, y, color);
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    public static Texture2D CreateChevronTexture(Color color, int width = 10, int height = 5)
    {
        Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        int half = width / 2;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (x >= half - 1 - y && x <= half + y)
                    image.SetPixel(x, y, color);
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    public static Texture2D CreateResizeGripTexture(Color color)
    {
        const int size = Size.ResizeGripSize;
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        foreach (int length in new[] { 4, 8, 12 })
        {
            for (int index = 0; index < length; index++)
            {
                int x = size - 3 - index;
                int y = size - 3 - (length - 1 - index);
                image.SetPixel(x, y, color);
                if (x > 0)
                    image.SetPixel(x - 1, y, color);
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static Texture2D? _switchOnDark;
    private static Texture2D? _switchOffDark;
    private static Texture2D? _switchOnLight;
    private static Texture2D? _switchOffLight;

    public static Texture2D GetSwitchTexture(bool isChecked)
    {
        bool light = IsLightTheme;
        if (light)
        {
            return isChecked
                ? (_switchOnLight ??= CreateSwitchTexture(true, true))
                : (_switchOffLight ??= CreateSwitchTexture(false, true));
        }
        return isChecked
            ? (_switchOnDark ??= CreateSwitchTexture(true, false))
            : (_switchOffDark ??= CreateSwitchTexture(false, false));
    }

    public static Texture2D CreateSwitchTexture(bool isChecked, bool isLight)
    {
        const int width = 38;
        const int height = 20;
        Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);

        const float radius = height / 2f;
        const float cx1 = radius - 0.5f;
        const float cx2 = width - radius - 0.5f;
        const float cy = radius - 0.5f;

        Color fill = isChecked
            ? (isLight ? Color.FromHtml("2570d6ff") : Color.FromHtml("2d7bd4ff"))
            : (isLight ? Color.FromHtml("e0e4ebff") : Color.FromHtml("222832ff"));

        Color border = isChecked
            ? (isLight ? Color.FromHtml("1e5cb3ff") : Color.FromHtml("458de6ff"))
            : (isLight ? Color.FromHtml("b8c0ccff") : Color.FromHtml("3c4656ff"));

        float knobX = isChecked ? cx2 : cx1;
        float knobY = cy;
        const float knobRadius = 7.0f;

        for (int y = 0; y < height; y++)
        {
            float dy = y - cy;
            for (int x = 0; x < width; x++)
            {
                float dx = x < cx1 ? x - cx1 : (x > cx2 ? x - cx2 : 0f);
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > radius + 0.5f)
                    continue;

                float edgeAlpha = Math.Clamp(radius + 0.5f - dist, 0f, 1f);
                float borderFactor = Math.Clamp(dist - (radius - 1.2f), 0f, 1f);
                Color track = fill.Lerp(border, borderFactor);
                track.A *= edgeAlpha;

                float kdx = x - knobX;
                float kdy = y - knobY;
                float kdist = MathF.Sqrt(kdx * kdx + kdy * kdy);

                if (kdist <= knobRadius + 0.5f)
                {
                    float knobAlpha = Math.Clamp(knobRadius + 0.5f - kdist, 0f, 1f);
                    Color knobColor = Godot.Colors.White;
                    Color finalColor = track.Lerp(knobColor, knobAlpha);
                    finalColor.A = Math.Max(track.A, knobAlpha * edgeAlpha);
                    image.SetPixel(x, y, finalColor);
                }
                else
                {
                    image.SetPixel(x, y, track);
                }
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    public static void ApplyTextOutline(
        Control control,
        int outlineSize = -1,
        Color? outlineColor = null)
    {
        if (outlineSize < 0)
            outlineSize = IsLightTheme ? 0 : Type.Outline;
        if (outlineSize <= 0)
            return;
        control.AddThemeConstantOverride("outline_size", outlineSize);
        control.AddThemeColorOverride("font_outline_color", outlineColor ?? Palette.TextOutline);
    }
}
