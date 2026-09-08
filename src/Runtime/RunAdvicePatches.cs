using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

internal static class RunAdvicePresentation
{
    internal static Player? CurrentPlayer()
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        return Entry.Enabled && run?.Players.Count == 1 && !CombatManager.Instance.IsInProgress
            ? LocalContext.GetMe(run) : null;
    }

    internal static void Reward(NCardRewardSelectionScreen screen,
        IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
    {
        Player? player = CurrentPlayer();
        if (player is null) return;
        AdviceOffer[] offers = options.Select((c, i) => new AdviceOffer(
            $"reward:{i}", AdviceKind.Card, c.Card.Id.Entry, Card: RunAdviceCapture.Card(c.Card)))
            .Concat(alternatives.Any(a => a.OptionId.Equals("Skip", StringComparison.OrdinalIgnoreCase))
                ? [new AdviceOffer("skip", AdviceKind.Skip, "skip")] : Array.Empty<AdviceOffer>()).ToArray();
        AdviceRating[] ratings = RunAdvice.Rank(RunAdviceCapture.Capture(player), offers, false);
        var holders = screen.GetNode<Control>("UI/CardRow").GetChildren()
            .OfType<NGridCardHolder>().Where(c => !c.IsQueuedForDeletion()).ToArray();
        for (int i = 0; i < Math.Min(options.Count, holders.Length); i++)
            RunAdviceBadge.Show(holders[i], ratings[i]);
        var skip = ratings.FirstOrDefault(r => r.Offer.Kind == AdviceKind.Skip);
        if (skip is not null)
        {
            int index = alternatives.ToList().FindIndex(a => a.OptionId.Equals("Skip", StringComparison.OrdinalIgnoreCase));
            var buttons = screen.GetNode<Control>("UI/RewardAlternatives").GetChildren()
                .OfType<NCardRewardAlternativeButton>().Where(c => !c.IsQueuedForDeletion()).ToArray();
            if (index < buttons.Length)
            {
                Label label = RunAdviceBadge.Show(buttons[index], skip, titleOnly: true);
                label.Position = new Vector2(0, -38);
                label.Size = new Vector2(buttons[index].Size.X, 35);
            }
        }
        RunAdviceBadge.Summary(screen,
            "评分越高越优先 · 跳过基准为 0 分\n启发式评分，非百分制或胜率", reward: true);
    }

    internal static void Shop(NMerchantInventory shop)
    {
        if (CurrentPlayer() is not { } player || shop.Inventory is null) return;
        NMerchantSlot[] slots = shop.GetAllSlots().ToArray();
        AdviceOffer[] offers = slots.Select((s, i) => RunAdviceCapture.Offer(s.Entry, i))
            .Append(new AdviceOffer("save", AdviceKind.Skip, "save")).ToArray();
        AdviceRating[] ratings = RunAdvice.Rank(RunAdviceCapture.Capture(player), offers, true);
        for (int i = 0; i < slots.Length; i++)
        {
            if (!slots[i].Entry.IsStocked) { RunAdviceBadge.Clear(slots[i]); continue; }
            var removalCard = ratings[i].RemovalDeckIndex is { } index
                ? player.Deck.Cards[index] : null;
            RunAdviceBadge.Show(slots[i], ratings[i], compact: true,
                detail: removalCard is null ? null : () => SolverText.Format($"优先移除：{removalCard.Title}"));
        }
        RunAdviceBadge.Summary(shop,
            "评分越高越优先 · 已计入价格 · 留钱为 0 分\n启发式评分，购买后更新；非百分制或胜率");
    }
}

internal sealed class RewardAdvicePatch : IPatchMethod
{
    public static string PatchId => "combat_solver_reward_advice";
    public static string Description => "奖励选牌优先级";
    public static ModPatchTarget[] GetTargets() =>
        [new(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.RefreshOptions))];
    public static void Postfix(NCardRewardSelectionScreen __instance,
        IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> extraOptions)
        => RunAdvicePresentation.Reward(__instance, options, extraOptions);
}

internal sealed class ShopAdvicePatch : IPatchMethod
{
    public static string PatchId => "combat_solver_shop_advice";
    public static string Description => "商店购买优先级";
    public static ModPatchTarget[] GetTargets() =>
        [new(typeof(NMerchantInventory), nameof(NMerchantInventory.Open)),
         new(typeof(NMerchantInventory), "OnPurchaseCompleted")];
    public static void Postfix(NMerchantInventory __instance)
        => Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(__instance) && __instance.IsInsideTree())
                RunAdvicePresentation.Shop(__instance);
        }).CallDeferred();
}
