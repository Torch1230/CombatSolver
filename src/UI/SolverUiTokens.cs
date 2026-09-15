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

    public static event Action? ThemeChanged;

    public static bool IsLightTheme => _activeTheme == SolverOverlayTheme.Light;

    public static void ConfigureTheme(SolverOverlayTheme theme)
    {
        bool changed = _activeTheme != theme;
        _activeTheme = theme;
        if (changed)
            ThemeChanged?.Invoke();
    }

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
            ("#b8c0cc", "#4a5568", Palette.TextSecondaryHex),
            ("#858f9f", "#64748b", Palette.TextMutedHex),
            ("#5c9fc7", "#0078d4", Palette.AccentHex),
            ("#d6a34c", "#b45309", Palette.WarningHex),
            ("#e26666", "#dc2626", Palette.DangerHex),
            ("#69b77d", "#16a34a", Palette.SuccessHex),
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
        public const int Small = 6;
        public const int Medium = 8;
        public const int Pill = 10;
        public const int Large = 12;
    }

    public static class Type
    {
        public const int Title = 15;
        public const int Metric = 14;
        public const int Body = 13;
        public const int Caption = 12;
        public const int Outline = 2;
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
        public const float ButtonHeight = 34f;
        public const float ResizeEdgeThickness = 8f;
        public const int ResizeGripSize = 20;
    }

    public static class Palette
    {
        public static Color Background => Pick("101216f5", "f4f6f9fa");
        public static Color Surface => Pick("191c22fa", "fffffffa");
        public static Color SurfaceRaised => Pick("23272ffb", "f0f3f7fb");
        public static Color SurfaceHover => Pick("2b3039ff", "e2e8f0ff");
        public static Color Border => Pick("3a404aeb", "cbd5e1eb");
        public static Color BorderSubtle => Pick("2a2f38cc", "e2e8f0cc");
        public static Color Accent => Pick("5c9fc7ff", "0078d4ff");
        public static Color AccentHover => Pick("73b4d8ff", "106ebeff");
        public static Color TextPrimary => Pick("eef1f6ff", "1a202cff");
        public static Color TextSecondary => Pick("b8c0ccff", "4a5568ff");
        public static Color TextMuted => Pick("858f9fff", "64748bff");
        public static Color TextOutline => Pick("080a0de6", "00000000");
        public static Color Warning => Pick("d6a34cff", "b45309ff");
        public static Color Danger => Pick("e26666ff", "dc2626ff");
        public static Color Success => Pick("69b77dff", "16a34aff");
        public static Color Positive => Pick("34764fff", "16a34aff");
        public static Color PositiveHover => Pick("428d60ff", "15803dff");

        public static Color Attack => Pick("d96363ff", "dc2626ff");
        public static Color AttackBackground => Pick("2b171bf8", "fef2f2ff");
        public static Color AttackText => Pick("eef1f6ff", "991b1bff");

        public static Color Skill => Pick("5b91d1ff", "2563ebff");
        public static Color SkillBackground => Pick("172235f8", "eff6ffff");
        public static Color SkillText => Pick("eef1f6ff", "1e40afff");

        public static Color Power => Pick("d7a84fff", "d97706ff");
        public static Color PowerBackground => Pick("2b2417f8", "fffbebff");
        public static Color PowerText => Pick("eef1f6ff", "92400eff");

        public static Color Negative => Pick("9b70c9ff", "7c3aedff");
        public static Color NegativeBackground => Pick("241b30f8", "f5f3ffff");
        public static Color NegativeText => Pick("eef1f6ff", "5b21b6ff");

        public static Color Potion => Pick("55b9a5ff", "059669ff");
        public static Color PotionBackground => Pick("152a27f8", "ecfdf5ff");
        public static Color PotionText => Pick("eef1f6ff", "065f46ff");

        public static Color Kill => Pick("69b77dff", "16a34aff");
        public static Color KillBackground => Pick("14291ffb", "f0fdf4ff");
        public static Color KillText => Pick("eef1f6ff", "166534ff");

        public static Color ProgressBackground => Pick("101216f5", "e2e8f0ff");
        public static Color ProgressFill => IsLightTheme ? Accent : Accent.Darkened(0.12f);
        public static Color CompletedActionModulate => IsLightTheme
            ? new Color(0.60f, 0.64f, 0.70f, 0.65f)
            : new Color(0.54f, 0.58f, 0.66f, 0.52f);
        public static Color ActiveActionModulate => IsLightTheme
            ? new Color(0.15f, 0.50f, 0.95f, 1f)
            : new Color(1f, 0.88f, 0.48f, 1f);

        public static string TextSecondaryHex => IsLightTheme ? "#4a5568" : "#b8c0cc";
        public static string TextMutedHex => IsLightTheme ? "#64748b" : "#858f9f";
        public static string AccentHex => IsLightTheme ? "#0078d4" : "#5c9fc7";
        public static string WarningHex => IsLightTheme ? "#b45309" : "#d6a34c";
        public static string DangerHex => IsLightTheme ? "#dc2626" : "#e26666";
        public static string SuccessHex => IsLightTheme ? "#16a34a" : "#69b77d";

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
                ? new Color(0f, 0f, 0f, IsLightTheme ? 0.14f : 0.52f)
                : Godot.Colors.Transparent,
            ShadowSize = shadow ? (IsLightTheme ? 16 : 10) : 0,
        };
    }

    public static Label CreateLabel(
        string text,
        int fontSize,
        Color color,
        FontType fontType = FontType.Regular,
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
        label.ApplyLocaleFontSubstitution(FontType.Regular, "normal_font");
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
        ApplyButtonStyle(button, style);
        return button;
    }

    public static void ApplyButtonStyle(Button button, SolverButtonStyle style)
    {
        if (!IsLightTheme)
        {
            (Color darkBackground, Color darkBorder, Color darkHover) = style switch
            {
                SolverButtonStyle.Primary => (
                    Palette.Accent.Darkened(0.12f),
                    Palette.Accent.Lightened(0.08f),
                    Palette.AccentHover),
                SolverButtonStyle.Positive => (Palette.Positive, Palette.Success, Palette.PositiveHover),
                SolverButtonStyle.Danger => (
                    Palette.Danger.Darkened(0.22f),
                    Palette.Danger,
                    Palette.Danger.Lightened(0.08f)),
                _ => (Palette.SurfaceRaised, Palette.Border, Palette.SurfaceHover),
            };
            button.AddThemeStyleboxOverride("normal", CreateBox(
                darkBackground, darkBorder, Radius.Medium, Spacing.Sm, Spacing.Xs));
            button.AddThemeStyleboxOverride("hover", CreateBox(
                darkHover, darkBorder.Lightened(0.12f), Radius.Medium, Spacing.Sm, Spacing.Xs));
            button.AddThemeStyleboxOverride("pressed", CreateBox(
                darkBackground.Darkened(0.16f), darkBorder, Radius.Medium, Spacing.Sm, Spacing.Xs));
            button.AddThemeStyleboxOverride("disabled", CreateBox(
                Palette.Background, Palette.BorderSubtle, Radius.Medium, Spacing.Sm, Spacing.Xs));
            button.AddThemeColorOverride("font_color", Palette.TextPrimary);
            button.AddThemeColorOverride("font_hover_color", Godot.Colors.White);
            button.AddThemeColorOverride("font_pressed_color", Godot.Colors.White);
            button.AddThemeColorOverride("font_disabled_color", Palette.TextMuted);
            ApplyTextOutline(button);
            ApplyButtonFont(button, style);
            return;
        }

        (Color background, Color border, Color hover, Color pressed, Color font, Color hoverFont) = style switch
        {
            SolverButtonStyle.Primary => (
                Palette.Accent,
                Palette.Accent,
                Palette.AccentHover,
                Palette.Accent.Darkened(0.18f),
                Godot.Colors.White,
                Godot.Colors.White),
            SolverButtonStyle.Positive => (
                Palette.Positive,
                Palette.Positive,
                Palette.PositiveHover,
                Palette.Positive.Darkened(0.16f),
                Godot.Colors.White,
                Godot.Colors.White),
            SolverButtonStyle.Danger => (
                Palette.Danger,
                Palette.Danger,
                Palette.Danger.Lightened(0.08f),
                Palette.Danger.Darkened(0.16f),
                Godot.Colors.White,
                Godot.Colors.White),
            _ => (
                Palette.SurfaceRaised,
                Palette.Border,
                Palette.SurfaceHover,
                Palette.SurfaceHover.Darkened(0.06f),
                Palette.TextPrimary,
                Palette.TextPrimary),
        };
        button.AddThemeStyleboxOverride("normal", CreateBox(background, border, Radius.Medium, Spacing.Sm, Spacing.Xs));
        button.AddThemeStyleboxOverride("hover", CreateBox(hover, border.Darkened(0.06f), Radius.Medium, Spacing.Sm, Spacing.Xs));
        button.AddThemeStyleboxOverride("pressed", CreateBox(pressed, border, Radius.Medium, Spacing.Sm, Spacing.Xs));
        button.AddThemeStyleboxOverride("disabled", CreateBox(Palette.Background, Palette.BorderSubtle, Radius.Medium, Spacing.Sm, Spacing.Xs));
        button.AddThemeColorOverride("font_color", font);
        button.AddThemeColorOverride("font_hover_color", hoverFont);
        button.AddThemeColorOverride("font_pressed_color", hoverFont);
        button.AddThemeColorOverride("font_disabled_color", Palette.TextMuted);
        ApplyTextOutline(button);
        ApplyButtonFont(button, style);
    }

    private static void ApplyButtonFont(Button button, SolverButtonStyle style)
    {
        button.ApplyLocaleFontSubstitution(FontType.Bold, "font");
    }

    public static PanelContainer CreateMetricPill(string text, Color bg, Color border, Color textCol, int fontSize = Type.Caption)
    {
        PanelContainer pill = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0, Size.ActionPillHeight),
        };
        pill.AddThemeStyleboxOverride("panel", CreateBox(
            bg, border, Radius.Pill, Spacing.Sm, Spacing.Xxs, borderWidth: 1));
        Label label = CreateLabel(text, fontSize, textCol, FontType.Bold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        pill.AddChild(label);
        return pill;
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
            int size = root.Name == "StrategyHeading" ? 18 : root is Label label
                && label.GetThemeFontSize("font_size") == Type.Caption ? 14 : 16;
            control.AddThemeFontSizeOverride("font_size", size);
            ApplyTextOutline(control, 0);
            control.ApplyLocaleFontSubstitution(FontType.Regular, "font");
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

    public static void ApplyTextOutline(
        Control control,
        int outlineSize = -1,
        Color? outlineColor = null)
    {
        if (outlineSize < 0)
            outlineSize = IsLightTheme ? 0 : Type.Outline;
        if (outlineSize <= 0)
        {
            control.RemoveThemeConstantOverride("outline_size");
            control.RemoveThemeColorOverride("font_outline_color");
            return;
        }
        control.AddThemeConstantOverride("outline_size", outlineSize);
        control.AddThemeColorOverride("font_outline_color", outlineColor ?? Palette.TextOutline);
    }
}
