using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using STS2RitsuLib.Models.Capabilities;
using CombatSolver.Engine.Common;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static readonly MethodInfo RitsuHasStarCostContributorsMethod =
        (typeof(ModelCapabilities).Assembly.GetType(
                "STS2RitsuLib.Models.Capabilities.CardModelCapabilityHost")
            ?? throw new TypeLoadException(
                "STS2RitsuLib.Models.Capabilities.CardModelCapabilityHost"))
        .GetMethod(
            "HasStarCostContributors",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(CardModel)],
            modifiers: null)
        ?? throw new MissingMethodException(
            "STS2RitsuLib.Models.Capabilities.CardModelCapabilityHost",
            "HasStarCostContributors(CardModel)");

    private static readonly MethodInfo CardIsPlayableGetterForRitsuFastPathTest =
        typeof(CardModel).GetProperty(
            "IsPlayable",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetMethod
        ?? throw new MissingMethodException(typeof(CardModel).FullName, "get_IsPlayable");

    // Kept in a separate partial so the shared fork-boundary test can invoke it without
    // mixing patch-specific reflection and probe capability definitions into that fixture.
    private static void AssertRitsuExtendedCapabilityFastPaths(Player player)
    {
        CardModel preview = PredictionUtils.CreateCard(ModelDb.Card<DefendRegent>(), player);
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        if (!RitsuEmptyCapabilityFastPath.CanSkip(preview))
            throw new InvalidOperationException("空 Ritsu capability 卡牌没有进入扩展快路径。");

        CardRarity originalRarity = preview.Rarity;
        int originalStarCost = preview.CurrentStarCost;
        bool originalPlayable = ReadCardIsPlayable(preview);
        if (HasStarCostContributors(preview))
            throw new InvalidOperationException("空 Ritsu capability 卡牌错误报告了星能费用贡献者。");

        CardRarity overrideRarity = originalRarity == CardRarity.Rare
            ? CardRarity.Common
            : CardRarity.Rare;
        int overrideStarCost = originalStarCost >= 0 ? originalStarCost + 3 : 3;
        bool overridePlayable = !originalPlayable;
        RitsuExtendedCardCapability capability = new(
            overrideRarity,
            overrideStarCost,
            overridePlayable);
        ModelCapabilitySet capabilities = ModelCapabilities.Get(preview);
        List<IModelCapability> attached = (List<IModelCapability>)(typeof(ModelCapabilitySet)
            .GetField("_capabilities", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(capabilities)
            ?? throw new MissingFieldException(typeof(ModelCapabilitySet).FullName, "_capabilities"));
        FieldInfo attachedSnapshot = typeof(ModelCapabilitySet).GetField(
                "_attachedSnapshot",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ModelCapabilitySet).FullName, "_attachedSnapshot");
        capability.Attach(preview, isInternal: true);
        attached.Add(capability);
        attachedSnapshot.SetValue(capabilities, null);
        try
        {
            if (RitsuEmptyCapabilityFastPath.CanSkip(preview)
                || preview.Rarity != overrideRarity
                || preview.CurrentStarCost != overrideStarCost
                || ReadCardIsPlayable(preview) != overridePlayable
                || !HasStarCostContributors(preview))
            {
                throw new InvalidOperationException(
                    "非空 Ritsu capability 卡牌没有保留稀有度、星能费用或可打出状态贡献逻辑。");
            }
        }
        finally
        {
            attached.Remove(capability);
            capability.Detach(isInternal: true);
            attachedSnapshot.SetValue(capabilities, null);
        }

        if (!RitsuEmptyCapabilityFastPath.CanSkip(preview)
            || preview.Rarity != originalRarity
            || preview.CurrentStarCost != originalStarCost
            || ReadCardIsPlayable(preview) != originalPlayable
            || HasStarCostContributors(preview))
        {
            throw new InvalidOperationException("Ritsu capability 移除后扩展快路径没有恢复原始结果。");
        }
    }

    private static bool ReadCardIsPlayable(CardModel card)
        => (bool)(CardIsPlayableGetterForRitsuFastPathTest.Invoke(card, null)
            ?? throw new InvalidOperationException("CardModel.IsPlayable 没有返回布尔值。"));

    private static bool HasStarCostContributors(CardModel card)
        => (bool)(RitsuHasStarCostContributorsMethod.Invoke(null, [card])
            ?? throw new InvalidOperationException(
                "Ritsu HasStarCostContributors 没有返回布尔值。"));

    private sealed class RitsuExtendedCardCapability(
        CardRarity rarity,
        int starCost,
        bool playable)
        : IModelCapability, ICardPropertyContributor, ICardStarCostContributor,
          ICardPlayStateContributor
    {
        public string CapabilityId => "combat_solver_test_extended_card_properties";
        public AbstractModel? Owner { get; private set; }

        public void Attach(AbstractModel owner, bool isInternal = false)
            => Owner = owner;

        public void Detach(bool isInternal = false)
            => Owner = null;

        public CardRarity? GetCardRarity(CardModel card)
            => rarity;

        public int ModifyStarCost(CardModel card, int currentCost)
            => starCost;

        public bool? CanPlay(CardModel card)
            => playable;
    }
}
