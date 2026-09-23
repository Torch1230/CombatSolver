using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace CombatSolver;

internal sealed partial class SolverLoopGroup : PanelContainer
{
    private const float ContentMarginH = 5f;
    private const float ExpandMarginV = 2f;

    public HFlowContainer Actions { get; } = new()
    {
        Name = "LoopActions",
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ExpandFill,
        MouseFilter = MouseFilterEnum.Ignore,
    };
    public PanelContainer Badge => _badge;
    public Label BadgeLabel => _badgeLabel;

    private readonly PanelContainer _badge;
    private readonly Label _badgeLabel;

    public void SetCompleted(bool completed)
    {
        SelfModulate = completed
            ? SolverUiTokens.Palette.CompletedActionModulate
            : Colors.White;
    }

    public float NaturalWidth
    {
        get
        {
            float contentMargin = ContentMarginH * 2f;
            int visibleCount = 0;
            float itemsWidth = 0;
            foreach (Control child in Actions.GetChildren().OfType<Control>().Where(c => c.Visible))
            {
                itemsWidth += child.GetCombinedMinimumSize().X;
                visibleCount++;
            }
            float separation = Mathf.Max(0, visibleCount - 1) * Actions.GetThemeConstant("h_separation");
            return contentMargin + itemsWidth + separation;
        }
    }

    public Vector2 GetWrappedDimensions(float maxAvailableWidth)
    {
        float contentMarginH = ContentMarginH * 2f;
        float maxInnerWidth = float.IsPositiveInfinity(maxAvailableWidth)
            ? float.PositiveInfinity
            : Mathf.Max(0, maxAvailableWidth - contentMarginH);
        float hSep = Actions.GetThemeConstant("h_separation");
        float vSep = Actions.GetThemeConstant("v_separation");
        float currentLineWidth = 0;
        float maxLineWidth = 0;
        int lineCount = 1;
        bool hasChildren = false;

        var cards = Actions.GetChildren().OfType<Control>().Where(c => c != _badge && c.Visible);
        foreach (Control child in cards.Append(_badge))
        {
            hasChildren = true;
            float itemWidth = child.GetCombinedMinimumSize().X;
            if (currentLineWidth > 0 && currentLineWidth + hSep + itemWidth > maxInnerWidth)
            {
                maxLineWidth = Mathf.Max(maxLineWidth, currentLineWidth);
                currentLineWidth = itemWidth;
                lineCount++;
            }
            else
            {
                if (currentLineWidth > 0)
                    currentLineWidth += hSep;
                currentLineWidth += itemWidth;
            }
        }

        if (!hasChildren)
            return new Vector2(contentMarginH, SolverUiTokens.Size.ActionPillHeight);

        maxLineWidth = Mathf.Max(maxLineWidth, currentLineWidth);
        float totalWidth = maxLineWidth + contentMarginH + 1f;
        float totalHeight = lineCount * SolverUiTokens.Size.ActionPillHeight + (lineCount - 1) * vSep;
        return new Vector2(totalWidth, totalHeight);
    }

    public override Vector2 _GetMinimumSize()
    {
        float maxItemWidth = Actions.GetChildren().OfType<Control>().Where(c => c.Visible)
            .Select(c => c.GetCombinedMinimumSize().X).DefaultIfEmpty(0).Max();
        return new Vector2(maxItemWidth + ContentMarginH * 2f, SolverUiTokens.Size.ActionPillHeight);
    }

    public SolverLoopGroup(SolverActionRun run)
    {
        Name = "LoopGroup";
        MouseFilter = MouseFilterEnum.Pass;

        StyleBoxFlat box = new()
        {
            BgColor = SolverUiTokens.IsLightTheme
                ? new Color(0.94f, 0.95f, 0.97f, 0.75f)
                : new Color(0.12f, 0.14f, 0.17f, 0.50f),
            BorderColor = SolverUiTokens.Palette.BorderSubtle,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            CornerRadiusTopLeft = (int)SolverUiTokens.Radius.Medium,
            CornerRadiusTopRight = (int)SolverUiTokens.Radius.Medium,
            CornerRadiusBottomLeft = (int)SolverUiTokens.Radius.Medium,
            CornerRadiusBottomRight = (int)SolverUiTokens.Radius.Medium,
            ContentMarginLeft = ContentMarginH,
            ContentMarginRight = ContentMarginH,
            ContentMarginTop = 0,
            ContentMarginBottom = 0,
            ExpandMarginTop = ExpandMarginV,
            ExpandMarginBottom = ExpandMarginV,
            ExpandMarginLeft = 0,
            ExpandMarginRight = 0,
        };
        AddThemeStyleboxOverride("panel", box);

        Actions.AddThemeConstantOverride("h_separation", 6);
        Actions.AddThemeConstantOverride("v_separation", (int)SolverUiTokens.Spacing.Xs);
        AddChild(Actions);

        _badge = new PanelContainer
        {
            Name = "LoopBadge",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        Color badgeBg = SolverUiTokens.IsLightTheme
            ? SolverUiTokens.Palette.Accent.Lightened(0.85f)
            : SolverUiTokens.Palette.Accent.Darkened(0.70f);
        Color badgeBorder = SolverUiTokens.IsLightTheme
            ? SolverUiTokens.Palette.Accent.Lightened(0.40f)
            : SolverUiTokens.Palette.Accent.Darkened(0.35f);
        _badge.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            badgeBg,
            badgeBorder,
            SolverUiTokens.Radius.Small,
            horizontalPadding: 8,
            verticalPadding: 3,
            borderWidth: 1));

        _badgeLabel = SolverUiTokens.CreateLabel(
            SolverText.Format($"循环 ×{run.Repetitions}"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.Accent,
            FontType.Bold);
        _badgeLabel.Name = "LoopCount";
        _badgeLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _badge.AddChild(_badgeLabel);

        Actions.AddChild(_badge);
        Actions.ChildEnteredTree += child =>
        {
            if (child != _badge)
                Actions.MoveChild(_badge, -1);
        };

        SolverLocaleRefresh.Bind(this, () =>
        {
            _badgeLabel.Text = SolverText.Format($"循环 ×{run.Repetitions}");
            TooltipText = SolverText.Format($"框内 {run.Period} 个动作按顺序重复 {run.Repetitions} 次，共 {run.Count} 个动作");
        });
    }
}
