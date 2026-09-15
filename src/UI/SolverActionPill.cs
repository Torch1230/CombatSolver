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
        (Color border, Color background) = ActionColors(action.VisualKind, killed);

        PanelContainer pill = new()
        {
            Name = "ActionPill",
            CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.ActionPillHeight),
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = action.Tooltip,
        };
        pill.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            background,
            border,
            SolverUiTokens.Radius.Pill,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));

        HBoxContainer content = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Begin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Xs);
        ColorRect colorBar = new()
        {
            Color = border,
            CustomMinimumSize = new Vector2(3, 14),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        content.AddChild(colorBar);

        Label titleLabel = SolverUiTokens.CreateLabel(
            action.Title,
            SolverUiTokens.Type.Metric,
            ActionTextColor(action.VisualKind, killed),
            FontType.Bold);
        content.AddChild(titleLabel);
        refreshers.Add(updated => titleLabel.Text = updated.Title);

        Label? replayLabel = null;
        if (action.ReplayCount > 0)
        {
            replayLabel = SolverUiTokens.CreateLabel(
                SolverText.Format($"重放×{action.ReplayCount}"),
                SolverUiTokens.Type.Caption,
                SolverUiTokens.Palette.Warning,
                FontType.Bold);
            content.AddChild(replayLabel);
            refreshers.Add(updated => replayLabel.Text = SolverText.Format($"重放×{updated.ReplayCount}"));
        }

        Label? targetLabel = null;
        if (!string.IsNullOrEmpty(action.TargetName))
        {
            targetLabel = SolverUiTokens.CreateLabel(
                $"➔  {action.TargetName}",
                SolverUiTokens.Type.Body,
                SolverUiTokens.Palette.TextPrimary);
            content.AddChild(targetLabel);
        }

        Label? choiceLabel = null;
        if (action.ChoiceText != null)
        {
            choiceLabel = SolverUiTokens.CreateLabel(
                action.ChoiceText,
                SolverUiTokens.Type.Body,
                SolverUiTokens.Palette.Accent);
            content.AddChild(choiceLabel);
            refreshers.Add(updated => choiceLabel.Text = updated.ChoiceText);
        }

        List<Label> relicLabels = [];
        Label? extraRelicLabel = null;
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
                relicLabels.Add(relicLabel);
                refreshers.Add(updated => relicLabel.Text = updated.RelicLabels[relicIndex]);
            }
            if (action.RelicLabels.Count > maxVisibleRelics)
            {
                extraRelicLabel = SolverUiTokens.CreateLabel(
                    $"+{action.RelicLabels.Count - maxVisibleRelics}",
                    SolverUiTokens.Type.Caption,
                    SolverUiTokens.Palette.Warning,
                    FontType.Bold);
                content.AddChild(extraRelicLabel);
            }
        }

        Label? killsLabel = null;
        if (killed)
        {
            killsLabel = SolverUiTokens.CreateLabel(
                SolverText.Format($"击杀：{string.Join("、", action.Kills)}"),
                SolverUiTokens.Type.Caption,
                SolverUiTokens.Palette.Success,
                FontType.Bold);
            content.AddChild(killsLabel);
            refreshers.Add(updated => killsLabel.Text = SolverText.Format($"击杀：{string.Join("、", updated.Kills)}"));
        }

        pill.AddChild(content);

        void ApplyTheme()
        {
            if (!GodotObject.IsInstanceValid(pill)) return;
            (Color currentBorder, Color currentBg) = ActionColors(action.VisualKind, killed);
            pill.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
                currentBg,
                currentBorder,
                SolverUiTokens.Radius.Pill,
                SolverUiTokens.Spacing.Sm,
                SolverUiTokens.Spacing.Xs));
            colorBar.Color = currentBorder;
            titleLabel.AddThemeColorOverride("font_color", ActionTextColor(action.VisualKind, killed));
            SolverUiTokens.ApplyTextOutline(titleLabel);

            if (replayLabel != null)
            {
                replayLabel.AddThemeColorOverride("font_color", SolverUiTokens.Palette.Warning);
                SolverUiTokens.ApplyTextOutline(replayLabel);
            }
            if (targetLabel != null)
            {
                targetLabel.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
                SolverUiTokens.ApplyTextOutline(targetLabel);
            }
            if (choiceLabel != null)
            {
                choiceLabel.AddThemeColorOverride("font_color", SolverUiTokens.Palette.Accent);
                SolverUiTokens.ApplyTextOutline(choiceLabel);
            }
            foreach (Label rLabel in relicLabels)
            {
                rLabel.AddThemeColorOverride("font_color", SolverUiTokens.Palette.Warning);
                SolverUiTokens.ApplyTextOutline(rLabel);
            }
            if (extraRelicLabel != null)
            {
                extraRelicLabel.AddThemeColorOverride("font_color", SolverUiTokens.Palette.Warning);
                SolverUiTokens.ApplyTextOutline(extraRelicLabel);
            }
            if (killsLabel != null)
            {
                killsLabel.AddThemeColorOverride("font_color", SolverUiTokens.Palette.Success);
                SolverUiTokens.ApplyTextOutline(killsLabel);
            }
        }

        Action themeListener = ApplyTheme;
        pill.TreeEntered += () => SolverUiTokens.ThemeChanged += themeListener;
        pill.TreeExiting += () => SolverUiTokens.ThemeChanged -= themeListener;
        pill.SetMeta("apply_theme", Callable.From(ApplyTheme));

        SolverLocaleRefresh.Bind(pill, () =>
        {
            SolverOverlayActionSnapshot updated = SolverActionTextIdentity.Refresh(action);
            pill.TooltipText = updated.Tooltip;
            foreach (Action<SolverOverlayActionSnapshot> refresh in refreshers) refresh(updated);
        });
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
            SolverUiTokens.Radius.Pill,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        Label label = SolverUiTokens.CreateLabel(text, SolverUiTokens.Type.Body, color);
        pill.AddChild(label);

        void ApplyTheme()
        {
            if (!GodotObject.IsInstanceValid(pill)) return;
            pill.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
                SolverUiTokens.Palette.SurfaceRaised,
                SolverUiTokens.Palette.BorderSubtle,
                SolverUiTokens.Radius.Pill,
                SolverUiTokens.Spacing.Sm,
                SolverUiTokens.Spacing.Xs));
            label.AddThemeColorOverride("font_color", color);
            SolverUiTokens.ApplyTextOutline(label);
        }

        Action themeListener = ApplyTheme;
        pill.TreeEntered += () => SolverUiTokens.ThemeChanged += themeListener;
        pill.TreeExiting += () => SolverUiTokens.ThemeChanged -= themeListener;
        pill.SetMeta("apply_theme", Callable.From(ApplyTheme));

        return pill;
    }

    public static void ApplyThemeToPill(Control pill)
    {
        if (pill.HasMeta("apply_theme"))
        {
            pill.GetMeta("apply_theme").AsCallable().Call();
        }
    }

    private static (Color Border, Color Background) ActionColors(SolverOverlayActionVisualKind kind, bool killed)
    {
        if (killed)
            return (SolverUiTokens.Palette.Success, SolverUiTokens.Palette.KillBackground);
        return kind switch
        {
            SolverOverlayActionVisualKind.Attack => (SolverUiTokens.Palette.Attack, SolverUiTokens.Palette.AttackBackground),
            SolverOverlayActionVisualKind.Skill => (SolverUiTokens.Palette.Skill, SolverUiTokens.Palette.SkillBackground),
            SolverOverlayActionVisualKind.Power => (SolverUiTokens.Palette.Power, SolverUiTokens.Palette.PowerBackground),
            SolverOverlayActionVisualKind.Negative => (SolverUiTokens.Palette.Negative, SolverUiTokens.Palette.NegativeBackground),
            SolverOverlayActionVisualKind.Potion => (SolverUiTokens.Palette.Potion, SolverUiTokens.Palette.PotionBackground),
            _ => (SolverUiTokens.Palette.Border, SolverUiTokens.Palette.SurfaceRaised),
        };
    }

    private static Color ActionTextColor(SolverOverlayActionVisualKind kind, bool killed)
    {
        if (killed)
            return SolverUiTokens.Palette.KillText;
        if (!SolverUiTokens.IsLightTheme)
            return SolverUiTokens.Palette.TextPrimary;
        return kind switch
        {
            SolverOverlayActionVisualKind.Attack => SolverUiTokens.Palette.AttackText,
            SolverOverlayActionVisualKind.Skill => SolverUiTokens.Palette.SkillText,
            SolverOverlayActionVisualKind.Power => SolverUiTokens.Palette.PowerText,
            SolverOverlayActionVisualKind.Negative => SolverUiTokens.Palette.NegativeText,
            SolverOverlayActionVisualKind.Potion => SolverUiTokens.Palette.PotionText,
            _ => SolverUiTokens.Palette.TextPrimary,
        };
    }
}
