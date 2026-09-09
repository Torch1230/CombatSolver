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
            Name = NodeName, MouseFilter = titleOnly ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Pass,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        bool shopCard = compact && owner is NMerchantCard;
        float width = compact && !shopCard ? 220 : 310;
        label.Size = new Vector2(width, 0);
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
                : !rating.Known || rating.Offer.Kind == AdviceKind.Card ? SolverText.Format($"参考分 {rating.Score:F1}")
                : SolverText.Format($"评分 {rating.Score:F1}");
            string reason = detail?.Invoke() ?? string.Join(" · ",
                (rating.Available ? rating.Reasons.Take(compact ? 1 : 2) : rating.Reasons.TakeLast(2)).Select(SolverText.Get));
            string compactText = title + "\n" + reason;
            if (rating.Offer.Card is { } card)
            {
                string coverage = SolverText.Get(card.Coverage == AdviceCoverage.Partial
                    ? "部分效果已建模" : "仅通用牌面参考");
                title += " · " + coverage;
            }
            if (!titleOnly && rating.Parts is { } parts)
                reason = SolverText.Format($"基础 {parts.Base:F1} / 配合 {parts.Synergy:F1} / 价格 {parts.Price:F1}")
                    + "\n" + reason;
            label.TooltipText = title + "\n" + reason + "\n"
                + string.Join("\n", rating.Reasons.Select(SolverText.Get));
            label.Text = titleOnly ? title : compact ? compactText : title + "\n" + reason;
            if (!titleOnly)
            {
                label.Size = new Vector2(width, 0);
                float bottom = shopCard ? -245 : compact ? -35 : -230;
                label.Position = new Vector2(-width / 2, bottom - label.GetCombinedMinimumSize().Y);
            }
        }
        SolverLocaleRefresh.Bind(label, Refresh);
        owner.AddChild(label);
        return label;
    }

    internal static void Summary(Control owner, string source, bool reward = false, DeckProfileSnapshot? profile = null)
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
        SolverLocaleRefresh.Bind(label, () =>
        {
            label.Text = SolverText.Get(source);
            if (profile is null) return;
            var active = profile.Mechanisms.Where(a => a.Balance.Supply > 0 && a.Balance.Payoffs > 0)
                .OrderByDescending(a => a.Balance.Readiness).Take(2);
            string axes = string.Join(" / ", active.Select(a => SolverText.Get(a.Name)
                + " " + SolverText.Format($"配合指数 {100 * a.Balance.Readiness:F1}")));
            label.Text += "\n" + (axes.Length == 0 ? SolverText.Get("尚未识别成型配合") : axes)
                + " · " + SolverText.Format($"抽牌 {profile.DrawCards} / 产能 {profile.EnergyCards} / 格挡 {profile.DefensiveCards}");
            List<string> shortages = [];
            if (profile.DrawShortage) shortages.Add(SolverText.Get("抽牌组件偏少"));
            if (profile.EnergyShortage) shortages.Add(SolverText.Get("高费牌多，产能组件不足"));
            if (profile.DefenseShortage) shortages.Add(SolverText.Get("直接格挡组件偏少"));
            label.TooltipText = string.Join("\n", profile.Mechanisms.Select(a =>
                SolverText.Get(a.Name) + ": " + SolverText.Format($"配合指数 {100 * a.Balance.Readiness:F1}")
                + " · " + SolverText.Format($"供给权重 {a.Balance.Supply:F1} / 兑现权重 {a.Balance.Payoffs:F1}")))
                + "\n" + string.Join(" / ", shortages)
                + "\n" + SolverText.Get("配合指数为 0–100 的启发式供需指标，不是胜率；权重包含效果数量、延迟与一次性折算")
                + "\n" + SolverText.Format($"部分审查 {profile.ReviewedCards}/{profile.DeckSize} 张；未覆盖的机制不计入指数");
        });
        if (profile is not null) label.MouseFilter = Control.MouseFilterEnum.Pass;
        owner.AddChild(label);
        label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        label.Position = new Vector2(owner.Size.X * 0.5f - 410, owner.Size.Y - (reward ? 125 : 185));
        label.Size = new Vector2(820, 125);
    }
}
