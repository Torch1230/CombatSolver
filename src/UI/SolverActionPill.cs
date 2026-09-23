using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace CombatSolver;

internal static class SolverActionPill
{
    public static Control Create(SolverOverlayActionSnapshot action)
    {
        action = SolverActionTextIdentity.Refresh(action);
        List<Action<SolverOverlayActionSnapshot>> refreshers = [];
        bool killed = action.Kills.Count > 0;
        Color marker = ActionColor(action.VisualKind);
        if (killed)
            marker = SolverUiTokens.Palette.Success;

        PanelContainer pill = new()
        {
            Name = "ActionPill",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = action.Tooltip,
        };
        pill.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));

        HBoxContainer content = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        content.AddChild(new ColorRect
        {
            Color = marker,
            CustomMinimumSize = new Vector2(3, 14),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        Label titleLabel = SolverUiTokens.CreateLabel(
            action.Title,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary,
            FontType.Bold);
        content.AddChild(titleLabel);
        refreshers.Add(updated => titleLabel.Text = updated.Title);
        if (action.ReplayCount > 0)
        {
            Label replayLabel = SolverUiTokens.CreateLabel(
                SolverText.Format($"重放×{action.ReplayCount}"),
                SolverUiTokens.Type.Caption,
                SolverUiTokens.Palette.Warning,
                FontType.Bold);
            content.AddChild(replayLabel);
            refreshers.Add(updated => replayLabel.Text = SolverText.Format($"重放×{updated.ReplayCount}"));
        }
        if (!string.IsNullOrEmpty(action.TargetName))
        {
            content.AddChild(SolverUiTokens.CreateLabel(
                $"➔  {action.TargetName}",
                SolverUiTokens.Type.Body,
                SolverUiTokens.Palette.TextPrimary,
                FontType.Bold));
        }
        if (action.ChoiceText != null)
        {
            Label choiceLabel = SolverUiTokens.CreateLabel(
                action.ChoiceText,
                SolverUiTokens.Type.Body,
                SolverUiTokens.Palette.Accent,
                FontType.Bold);
            content.AddChild(choiceLabel);
            refreshers.Add(updated => choiceLabel.Text = updated.ChoiceText);
        }
        if (action.RelicLabels.Count > 0)
        {
            const int maxVisibleRelics = 2;
            for (int index = 0; index < Math.Min(action.RelicLabels.Count, maxVisibleRelics); index++)
            {
                int relicIndex = index;
                Label relicLabel = SolverUiTokens.CreateLabel(
                    action.RelicLabels[index],
                    SolverUiTokens.Type.Caption,
                    SolverUiTokens.Palette.Warning,
                    FontType.Bold);
                content.AddChild(relicLabel);
                refreshers.Add(updated => relicLabel.Text = updated.RelicLabels[relicIndex]);
            }
            if (action.RelicLabels.Count > maxVisibleRelics)
            {
                content.AddChild(SolverUiTokens.CreateLabel(
                    $"+{action.RelicLabels.Count - maxVisibleRelics}",
                    SolverUiTokens.Type.Caption,
                    SolverUiTokens.Palette.Warning,
                    FontType.Bold));
            }
        }
        if (killed)
        {
            Label killsLabel = SolverUiTokens.CreateLabel(
                SolverText.Format($"击杀：{string.Join("、", action.Kills)}"),
                SolverUiTokens.Type.Caption,
                SolverUiTokens.Palette.Success,
                FontType.Bold);
            content.AddChild(killsLabel);
            refreshers.Add(updated => killsLabel.Text = SolverText.Format($"击杀：{string.Join("、", updated.Kills)}"));
        }
        pill.AddChild(content);
        SolverLocaleRefresh.Bind(pill, () =>
        {
            SolverOverlayActionSnapshot updated = SolverActionTextIdentity.Refresh(action);
            pill.TooltipText = updated.Tooltip;
            foreach (Action<SolverOverlayActionSnapshot> refresh in refreshers) refresh(updated);
        });
        return pill;
    }

    public static Control CreateChoice(string text)
    {
        PanelContainer pill = new()
        {
            Name = "ChoicePill",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = text,
        };
        pill.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));

        HBoxContainer content = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        content.AddChild(new ColorRect
        {
            Color = SolverUiTokens.Palette.Choice,
            CustomMinimumSize = new Vector2(3, 14),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        content.AddChild(SolverUiTokens.CreateLabel(
            text,
            SolverUiTokens.Type.Body,
            SolverUiTokens.Palette.TextPrimary,
            FontType.Bold));
        pill.AddChild(content);
        return pill;
    }

    public static Control CreateStatus(string text, Color color)
    {
        PanelContainer pill = new()
        {
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        pill.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        pill.AddChild(SolverUiTokens.CreateLabel(text, SolverUiTokens.Type.Body, color, FontType.Bold));
        return pill;
    }

    public static SolverLoopGroup CreateCycle(SolverActionRun run, out HFlowContainer actions)
    {
        SolverLoopGroup group = new(run);
        actions = group.Actions;
        return group;
    }

    private static Color ActionColor(SolverOverlayActionVisualKind kind)
        => kind switch
        {
            SolverOverlayActionVisualKind.Attack => SolverUiTokens.Palette.Attack,
            SolverOverlayActionVisualKind.Skill => SolverUiTokens.Palette.Skill,
            SolverOverlayActionVisualKind.Power => SolverUiTokens.Palette.Power,
            SolverOverlayActionVisualKind.Negative => SolverUiTokens.Palette.Negative,
            SolverOverlayActionVisualKind.Potion => SolverUiTokens.Palette.Potion,
            _ => SolverUiTokens.Palette.Border,
        };
}
