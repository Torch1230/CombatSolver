using System.Reflection;
using System.Reflection.Emit;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

namespace CombatSolver.Engine.Common;

internal static class PredictionUtils
{
    private delegate object MemberwiseCloneInvoker(object source);
    private delegate void ModelCloneStageInvoker(AbstractModel model);
    private delegate List<LocalCostModifier> LocalCostModifiersGetter(CardEnergyCost cost);
    private delegate bool CardBoolGetter(CardModel card);

    private static readonly MemberwiseCloneInvoker InvokeMemberwiseClone = BuildMemberwiseCloneInvoker();
    private static readonly ModelCloneStageInvoker InvokeDeepCloneFields = BuildModelCloneStageInvoker("DeepCloneFields");
    private static readonly ModelCloneStageInvoker InvokeAfterCloned = BuildModelCloneStageInvoker("AfterCloned");
    private static readonly LocalCostModifiersGetter GetLocalCostModifiers = BuildLocalCostModifiersGetter();
    private static readonly CardBoolGetter GetSingleTurnRetain = BuildCardBoolGetter("_hasSingleTurnRetain");
    private static readonly CardBoolGetter GetSingleTurnSly = BuildCardBoolGetter("_hasSingleTurnSly");
    private static readonly Action<CardModel> DetachCardObservers = BuildCardObserverDetacher();

    public static TModel CloneModelForSimulation<TModel>(TModel source)
        where TModel : AbstractModel
    {
        // This helper does not call MutableClone's BaseLib postfix. Audited native card/Power stages
        // can run independently in isolation; nested MutableClone calls still take the
        // original gate through BaseLibCloneConcurrencyPatch.
        bool entered = BaseLibCloneConcurrency.IsRequired
            && !NativeModelCloneConcurrency.CanCloneIndependently(source)
            && BaseLibCloneConcurrency.Enter();
        try
        {
            TModel clone = (TModel)InvokeMemberwiseClone(source);
            clone.IsMutable = true;
            if (clone is CardModel card)
                DetachCardObservers(card);
            InvokeDeepCloneFields(clone);
            InvokeAfterCloned(clone);
            return clone;
        }
        finally
        {
            BaseLibCloneConcurrency.Exit(entered);
        }
    }

    public static CardModel CloneCardStateForSimulation(CardModel source)
    {
        CardModel clone = CloneModelForSimulation(source);
        PredictionModModelSupport.CloneCardAttachedModels(source, clone);
        clone.DeckVersion = source.DeckVersion;
        clone.HasBeenRemovedFromState = source.HasBeenRemovedFromState;
        return clone;
    }

    private static Action<CardModel> BuildCardObserverDetacher()
    {
        DynamicMethod method = new("CombatSolver_DetachCardObservers", typeof(void),
            [typeof(CardModel)], typeof(PredictionUtils).Module, skipVisibility: true);
        ILGenerator il = method.GetILGenerator();
        foreach (EventInfo observer in typeof(CardModel).GetEvents(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            FieldInfo field = typeof(CardModel).GetField(observer.Name,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(CardModel).FullName, observer.Name);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stfld, field);
        }
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Action<CardModel>>();
    }

    private static MemberwiseCloneInvoker BuildMemberwiseCloneInvoker()
    {
        MethodInfo method = typeof(object).GetMethod(
            "MemberwiseClone",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(object).FullName, "MemberwiseClone");
        DynamicMethod dynamicMethod = new(
            "CombatSolver_MemberwiseClone",
            typeof(object),
            [typeof(object)],
            typeof(PredictionUtils).Module,
            skipVisibility: true);
        ILGenerator il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        return dynamicMethod.CreateDelegate<MemberwiseCloneInvoker>();
    }

    private static ModelCloneStageInvoker BuildModelCloneStageInvoker(string methodName)
    {
        MethodInfo method = typeof(AbstractModel).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(AbstractModel).FullName, methodName);
        DynamicMethod dynamicMethod = new(
            $"CombatSolver_{methodName}",
            typeof(void),
            [typeof(AbstractModel)],
            typeof(PredictionUtils).Module,
            skipVisibility: true);
        ILGenerator il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, method);
        il.Emit(OpCodes.Ret);
        return dynamicMethod.CreateDelegate<ModelCloneStageInvoker>();
    }

    private static LocalCostModifiersGetter BuildLocalCostModifiersGetter()
    {
        FieldInfo field = typeof(CardEnergyCost).GetField(
            "_localModifiers",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(CardEnergyCost).FullName, "_localModifiers");
        DynamicMethod dynamicMethod = new(
            "CombatSolver_GetLocalCostModifiers",
            typeof(List<LocalCostModifier>),
            [typeof(CardEnergyCost)],
            typeof(PredictionUtils).Module,
            skipVisibility: true);
        ILGenerator il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return dynamicMethod.CreateDelegate<LocalCostModifiersGetter>();
    }

    private static CardBoolGetter BuildCardBoolGetter(string fieldName)
    {
        FieldInfo field = typeof(CardModel).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(CardModel).FullName, fieldName);
        DynamicMethod dynamicMethod = new(
            $"CombatSolver_Get{fieldName}",
            typeof(bool),
            [typeof(CardModel)],
            typeof(PredictionUtils).Module,
            skipVisibility: true);
        ILGenerator il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return dynamicMethod.CreateDelegate<CardBoolGetter>();
    }

    public static bool NeedsEndOfTurnCleanup(CardModel card)
    {
        if (card.ExhaustOnNextPlay
            || GetSingleTurnRetain(card)
            || GetSingleTurnSly(card))
        {
            return true;
        }

        CardEnergyCost cost = card.EnergyCost;
        List<LocalCostModifier> localModifiers = GetLocalCostModifiers(cost);
        if (localModifiers.Count == 0)
            return false;
        foreach (LocalCostModifier modifier in localModifiers)
        {
            if (modifier.Expiration.HasFlag(LocalCostModifierExpiration.EndOfTurn))
                return true;
        }
        return false;
    }

    public static CardModel CreateCard(CardModel card, Player player)
    {
        card = CloneModelForSimulation(card);
        card.Owner = player;
        return card;
    }

    /// <summary>
    /// Mirrors <see cref="CardCmd.Upgrade(CardModel, MegaCrit.Sts2.Core.Nodes.CommonUi.CardPreviewStyle)"/>.
    /// Does nothing if the card is not upgradable.
    /// </summary>
    public static void UpgradeCard(CardModel card)
    {
        if (!card.IsUpgradable)
        {
            return;
        }

        card.UpgradeInternal();
        card.FinalizeUpgradeInternal();
    }

    /// <summary>
    /// Same as <see cref="UpgradeCard"/>, but returns a new upgraded card instead of modifying the original card.
    /// Returns the original card if it is not upgradable.
    /// </summary>
    public static CardModel CreateUpgradedCard(CardModel card)
    {
        if (!card.IsUpgradable)
        {
            return card;
        }

        var previewCard = CloneModelForSimulation(card);
        UpgradeCard(previewCard);
        return previewCard;
    }

    /// <summary>
    /// Mirrors <see cref="CardCmd.Enchant(EnchantmentModel, CardModel, decimal)"/>.
    /// Does nothing if the card cannot be enchanted by the given enchantment.
    /// </summary>
    public static void EnchantCard(EnchantmentModel enchantment, CardModel card, decimal amount)
    {
        if (!enchantment.CanEnchant(card))
        {
            return;
        }

        if (card.Enchantment is null)
        {
            card.EnchantInternal(enchantment, amount);
            enchantment.ModifyCard();
        }
        else
        {
            // The CanEnchant check above ensures that the existing enchantment is the same type as the new enchantment.
            card.Enchantment.Amount += (int)amount;
        }

        card.FinalizeUpgradeInternal();
    }

    /// <summary>
    /// Same as <see cref="EnchantCard"/>, but returns a new enchanted card instead of modifying the original card.
    /// Returns the original card if it cannot be enchanted by the given enchantment.
    /// </summary>
    /// <returns></returns>
    public static CardModel CreateEnchantedCard(EnchantmentModel enchantment, CardModel card, decimal amount)
    {
        if (!enchantment.CanEnchant(card))
        {
            return card;
        }

        var previewCard = CloneModelForSimulation(card);
        EnchantCard(enchantment, previewCard, amount);
        return previewCard;
    }

    public static RelicModel CreateRelic(RelicModel relic, Player player)
    {
        relic = CloneModelForSimulation(relic);
        relic.Owner = player;
        return relic;
    }

    public static PotionModel CreatePotion(PotionModel potion, Player player)
    {
        potion = CloneModelForSimulation(potion);
        potion.Owner = player;
        return potion;
    }

    public static CardModel PredictTransformResult(CardModel original, Rng rng, bool isInCombat)
    {
        var options = CardFactory.GetDefaultTransformationOptions(original, isInCombat);
        var result = rng.NextItem(options)
            ?? throw new InvalidOperationException($"Could not predict a transform result for {original.Id}.");
        return result;
    }

    public static IReadOnlyList<PotionModel> PredictPotionRewards(Player player, int count, Rng rng)
    {
        return Enumerable.Range(0, count)
            .Select(_ => PotionFactory.CreateRandomPotionOutOfCombat(player, rng))
            .ToList();
    }
}
