using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace CombatSolver;

internal static class RunAdviceBadge
{
    internal const string NodeName = "CombatSolverAdvice";

    internal static void Clear(Control owner)
    {
        if (owner.GetNodeOrNull<Control>(NodeName) is not { } old) return;
        owner.RemoveChild(old);
        old.QueueFree();
    }

    internal static Label Show(Control owner, AdviceRating rating, bool compact = false, Func<string>? detail = null, bool titleOnly = false)
    {
        Clear(owner);
        Label label = new()
        {
            Name = NodeName, MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        bool shopCard = compact && owner is NMerchantCard;
        label.AddThemeFontSizeOverride("font_size", shopCard ? 26 : compact ? 20 : 23);
        label.AddThemeColorOverride("font_outline_color", new Color(0.06f, 0.07f, 0.09f, 0.95f));
        label.AddThemeConstantOverride("outline_size", 7);
        label.AddThemeColorOverride("font_color", !rating.Available || !rating.Known
            ? new Color(0.75f, 0.75f, 0.75f)
            : rating.Rank == 1 ? new Color(0.55f, 1f, 0.7f) : new Color(1f, 0.86f, 0.56f));
        void Refresh()
        {
            string title = !rating.Available ? SolverText.Get("暂不可选")
                : rating.Rank == 0 ? SolverText.Get("暂不评级")
                : !rating.Known ? SolverText.Format($"参考 #{rating.Rank}")
                : SolverText.Format($"推荐 #{rating.Rank}");
            string reason = detail?.Invoke() ?? string.Join(" · ",
                (rating.Available ? rating.Reasons.Take(compact ? 1 : 2) : rating.Reasons.TakeLast(2)).Select(SolverText.Get));
            label.Text = titleOnly ? title : title + "\n" + reason;
        }
        SolverLocaleRefresh.Bind(label, Refresh);
        owner.AddChild(label);
        label.Position = shopCard ? new Vector2(-155, -310)
            : compact ? new Vector2(-110, -90) : new Vector2(-155, -275);
        label.Size = compact && !shopCard ? new Vector2(220, 76) : new Vector2(310, 95);
        return label;
    }

    internal static void Summary(Control owner, string source, bool reward = false)
    {
        const string name = "CombatSolverAdviceSummary";
        if (owner.GetNodeOrNull<Control>(name) is { } old)
        {
            owner.RemoveChild(old);
            old.QueueFree();
        }
        Label label = new()
        {
            Name = name, MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        label.AddThemeFontSizeOverride("font_size", 20);
        label.AddThemeConstantOverride("outline_size", 6);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        SolverLocaleRefresh.Bind(label, () => label.Text = SolverText.Get(source));
        owner.AddChild(label);
        label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        label.Position = new Vector2(owner.Size.X * 0.5f - 410, owner.Size.Y - (reward ? 95 : 155));
        label.Size = new Vector2(820, 95);
    }
}
