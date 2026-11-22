using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.Common;

internal readonly record struct BaseLibCardModifierFingerprintState(
    int Amount,
    int Priority,
    KeyValuePair<string, int>[] IntProperties,
    KeyValuePair<string, string>[] AdditionalProperties);

internal readonly struct CardAttachedModelCollection
{
    private readonly AbstractModel[]? _models;

    public CardAttachedModelCollection(IList models, Type expectedType)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(expectedType);
        if (models.Count == 0)
        {
            _models = null;
            return;
        }

        // StoreSaveData is third-party code and may re-enter BaseLib. Preserve the old
        // snapshot-before-callback semantics while keeping the common empty-list path
        // allocation-free.
        AbstractModel[] snapshot = new AbstractModel[models.Count];
        for (int index = 0; index < snapshot.Length; index++)
        {
            object? value = models[index];
            if (value is not AbstractModel model
                || !expectedType.IsInstanceOfType(model))
            {
                throw new InvalidOperationException(
                    "BaseLib returned an invalid card modifier listener.");
            }
            snapshot[index] = model;
        }
        _models = snapshot;
    }

    public int Count => _models?.Length ?? 0;

    public AbstractModel this[int index] => _models![index];
}

internal static class PredictionModModelSupport
{
    private const string BaseLibCardModifierTypeName = "BaseLib.Abstracts.CardModifier";
    private static readonly Lazy<BaseLibCardModifierAdapter?> BaseLibCardModifiers =
        new(CreateBaseLibCardModifierAdapter);
    private static readonly ConditionalWeakTable<CardModel, object> BaseLibModifierCards = new();
    private static readonly object BaseLibModifierCardMarker = new();
    private static volatile bool _hasRegisteredBaseLibModifierCards;

    public static bool IsBaseLibCardModifier(AbstractModel model)
        => BaseLibCardModifiers.Value?.ModifierType.IsInstanceOfType(model) == true;

    public static bool HasBaseLibCardModifiers(CardModel card)
        => BaseLibCardModifiers.Value is { } adapter && adapter.DirectModifiers(card).Count > 0;

    public static void RegisterBaseLibCardModifierSources(IEnumerable<AbstractModel> subscribers)
    {
        BaseLibCardModifierAdapter? adapter = BaseLibCardModifiers.Value;
        if (adapter == null)
            return;
        foreach (AbstractModel subscriber in subscribers)
        {
            if (!adapter.ModifierType.IsInstanceOfType(subscriber))
                continue;
            CardModel owner = adapter.GetOwner(subscriber)
                ?? throw new InvalidOperationException("BaseLib card modifier has no owner during root capture.");
            RegisterBaseLibCardModifierOwner(owner);
        }
    }

    public static void RegisterBaseLibCardModifierOwner(CardModel owner)
    {
        if (BaseLibCardModifiers.Value == null)
            return;
        _ = BaseLibModifierCards.GetValue(owner, _ => BaseLibModifierCardMarker);
        _hasRegisteredBaseLibModifierCards = true;
    }

    public static void RegisterBaseLibCardModifierOwners(IEnumerable<CardModel> owners)
    {
        foreach (CardModel owner in owners)
            RegisterBaseLibCardModifierOwner(owner);
    }

    public static void CloneCardAttachedModels(CardModel source, CardModel clone)
    {
        BaseLibCardModifierAdapter? adapter = BaseLibCardModifiers.Value;
        if (!_hasRegisteredBaseLibModifierCards
            || adapter == null
            || !BaseLibModifierCards.TryGetValue(source, out _))
            return;
        IList sourceModifiers = adapter.DirectModifiers(source);
        IList clonedModifiers = adapter.DirectModifiers(clone);
        if (clonedModifiers.Count != 0)
            throw new InvalidOperationException("BaseLib populated card modifiers before prediction clone migration.");
        CardAttachedModelCollection sourceSnapshot = new(
            sourceModifiers,
            adapter.ModifierType);
        for (int index = 0; index < sourceSnapshot.Count; index++)
        {
            AbstractModel sourceModel = sourceSnapshot[index];
            AbstractModel clonedModel = PredictionUtils.CloneModelForSimulation(sourceModel);
            adapter.SetOwner(clonedModel, clone);
            clonedModifiers.Add(clonedModel);
            adapter.AfterClonedOnCard(clonedModel, clone);
        }
        _ = BaseLibModifierCards.GetValue(clone, _ => BaseLibModifierCardMarker);
    }

    public static void AppendCardAttachedListeners(CardModel card, List<AbstractModel> listeners)
    {
        CardAttachedModelCollection attached = GetCardAttachedListeners(card);
        for (int index = 0; index < attached.Count; index++)
            listeners.Add(attached[index]);
    }

    public static CardAttachedModelCollection GetCardAttachedListeners(CardModel card)
    {
        BaseLibCardModifierAdapter? adapter = BaseLibCardModifiers.Value;
        if (!_hasRegisteredBaseLibModifierCards
            || adapter == null
            || !BaseLibModifierCards.TryGetValue(card, out _))
        {
            return default;
        }
        return new CardAttachedModelCollection(
            adapter.DirectModifiers(card),
            adapter.ModifierType);
    }

    public static BaseLibCardModifierFingerprintState CaptureBaseLibCardModifierFingerprintState(
        AbstractModel modifier)
    {
        BaseLibCardModifierAdapter adapter = BaseLibCardModifiers.Value
            ?? throw new InvalidOperationException("BaseLib CardModifier adapter is unavailable.");
        if (!adapter.ModifierType.IsInstanceOfType(modifier))
            throw new InvalidOperationException("Model is not a BaseLib CardModifier.");
        return adapter.CaptureFingerprintState(modifier);
    }

    public static bool AppendBaseLibCardModifierState(
        StringBuilder text,
        CardModel card,
        bool discoverUnregistered)
    {
        BaseLibCardModifierAdapter? adapter = BaseLibCardModifiers.Value;
        if (adapter == null)
            return false;

        bool registered = BaseLibModifierCards.TryGetValue(card, out _);
        if (!registered && !discoverUnregistered)
            return false;
        IList modifiers = adapter.DirectModifiers(card);
        // Registration is an ownership/isolation implementation detail, not semantic card
        // state. Encode every empty modifier list identically so root-wide owner registration
        // cannot make an otherwise unchanged live stamp drift.
        if (modifiers.Count == 0)
            return false;
        CardAttachedModelCollection snapshot = new(modifiers, adapter.ModifierType);
        if (!registered)
            RegisterBaseLibCardModifierOwner(card);
        text.Append(snapshot.Count).Append('{');
        for (int index = 0; index < snapshot.Count; index++)
        {
            AbstractModel modifier = snapshot[index];
            Type type = modifier.GetType();
            AppendToken(text, type.Assembly.GetName().Name ?? string.Empty);
            AppendToken(text, type.FullName ?? type.Name);
            BaseLibCardModifierFingerprintState state = adapter.CaptureFingerprintState(modifier);
            text.Append(state.Amount).Append(':').Append(state.Priority).Append('[');
            foreach ((string name, int propertyValue) in state.IntProperties)
            {
                AppendToken(text, name);
                text.Append(propertyValue).Append(';');
            }
            text.Append("][");
            foreach ((string name, string propertyValue) in state.AdditionalProperties)
            {
                AppendToken(text, name);
                AppendToken(text, propertyValue);
            }
            text.Append("];");
        }
        text.Append('}');
        return true;
    }

    private static void AppendToken(StringBuilder text, string value)
        => text.Append(value.Length).Append(':').Append(value).Append(';');

    private static BaseLibCardModifierAdapter? CreateBaseLibCardModifierAdapter()
    {
        Type? modifierType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(BaseLibCardModifierTypeName, throwOnError: false))
            .FirstOrDefault(type => type != null);
        if (modifierType == null)
            return null;
        MethodInfo directModifiers = modifierType.GetMethod(
            "DirectModifiers",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(CardModel)],
            modifiers: null)
            ?? throw new MissingMethodException(BaseLibCardModifierTypeName, "DirectModifiers(CardModel)");
        MethodInfo setOwner = modifierType.GetProperty("Owner", BindingFlags.Instance | BindingFlags.Public)?
            .GetSetMethod(nonPublic: true)
            ?? throw new MissingMethodException(BaseLibCardModifierTypeName, "set_Owner(CardModel)");
        MethodInfo getOwner = modifierType.GetProperty("Owner", BindingFlags.Instance | BindingFlags.Public)?
            .GetGetMethod(nonPublic: true)
            ?? throw new MissingMethodException(BaseLibCardModifierTypeName, "get_Owner()");
        MethodInfo getAmount = modifierType.GetProperty("Amount", BindingFlags.Instance | BindingFlags.Public)?
            .GetGetMethod(nonPublic: true)
            ?? throw new MissingMethodException(BaseLibCardModifierTypeName, "get_Amount()");
        MethodInfo getPriority = modifierType.GetProperty("Priority", BindingFlags.Instance | BindingFlags.Public)?
            .GetGetMethod(nonPublic: true)
            ?? throw new MissingMethodException(BaseLibCardModifierTypeName, "get_Priority()");
        Type modifierSaveType = modifierType.GetNestedType("ModifierSave", BindingFlags.Public)
            ?? throw new MissingMemberException(BaseLibCardModifierTypeName, "ModifierSave");
        ConstructorInfo modifierSaveConstructor = modifierSaveType.GetConstructor(Type.EmptyTypes)
            ?? throw new MissingMethodException(modifierSaveType.FullName, ".ctor()");
        MethodInfo storeSaveData = modifierType.GetMethod(
            "StoreSaveData",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [modifierSaveType],
            modifiers: null)
            ?? throw new MissingMethodException(BaseLibCardModifierTypeName, "StoreSaveData(ModifierSave)");
        MethodInfo getIntProperties = modifierSaveType
            .GetProperty("IntProperties", BindingFlags.Instance | BindingFlags.Public)?
            .GetGetMethod()
            ?? throw new MissingMethodException(modifierSaveType.FullName, "get_IntProperties()");
        MethodInfo getAdditionalProperties = modifierSaveType
            .GetProperty("AdditionalProperties", BindingFlags.Instance | BindingFlags.Public)?
            .GetGetMethod()
            ?? throw new MissingMethodException(modifierSaveType.FullName, "get_AdditionalProperties()");
        MethodInfo afterClonedOnCard = modifierType.GetMethod(
            "AfterClonedOnCard",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(CardModel)],
            modifiers: null)
            ?? throw new MissingMethodException(BaseLibCardModifierTypeName, "AfterClonedOnCard(CardModel)");
        return new BaseLibCardModifierAdapter(
            modifierType,
            directModifiers,
            getOwner,
            setOwner,
            getAmount,
            getPriority,
            modifierSaveConstructor,
            storeSaveData,
            getIntProperties,
            getAdditionalProperties,
            afterClonedOnCard);
    }

    private sealed class BaseLibCardModifierAdapter(
        Type modifierType,
        MethodInfo directModifiers,
        MethodInfo getOwner,
        MethodInfo setOwner,
        MethodInfo getAmount,
        MethodInfo getPriority,
        ConstructorInfo modifierSaveConstructor,
        MethodInfo storeSaveData,
        MethodInfo getIntProperties,
        MethodInfo getAdditionalProperties,
        MethodInfo afterClonedOnCard)
    {
        private readonly Func<CardModel, IList> _directModifiers =
            CompileDirectModifiers(directModifiers);

        public Type ModifierType => modifierType;

        public IList DirectModifiers(CardModel card)
            => _directModifiers(card)
                ?? throw new InvalidOperationException("BaseLib DirectModifiers did not return a list.");

        private static Func<CardModel, IList> CompileDirectModifiers(MethodInfo method)
        {
            ParameterExpression card = Expression.Parameter(typeof(CardModel), "card");
            UnaryExpression result = Expression.Convert(
                Expression.Call(method, card),
                typeof(IList));
            return Expression.Lambda<Func<CardModel, IList>>(result, card).Compile();
        }

        public CardModel? GetOwner(AbstractModel modifier)
            => getOwner.Invoke(modifier, null) as CardModel;

        public void SetOwner(AbstractModel modifier, CardModel owner)
            => setOwner.Invoke(modifier, [owner]);

        public BaseLibCardModifierFingerprintState CaptureFingerprintState(AbstractModel modifier)
        {
            int amount = (int)(getAmount.Invoke(modifier, null)
                ?? throw new InvalidOperationException("BaseLib CardModifier Amount is null."));
            int priority = (int)(getPriority.Invoke(modifier, null)
                ?? throw new InvalidOperationException("BaseLib CardModifier Priority is null."));
            object save = modifierSaveConstructor.Invoke(null);
            _ = storeSaveData.Invoke(modifier, [save]);
            IDictionary intProperties = getIntProperties.Invoke(save, null) as IDictionary
                ?? throw new InvalidOperationException("BaseLib ModifierSave IntProperties is not a dictionary.");
            IDictionary additionalProperties = getAdditionalProperties.Invoke(save, null) as IDictionary
                ?? throw new InvalidOperationException(
                    "BaseLib ModifierSave AdditionalProperties is not a dictionary.");
            return new BaseLibCardModifierFingerprintState(
                amount,
                priority,
                CaptureProperties<int>(intProperties, "IntProperties"),
                CaptureProperties<string>(additionalProperties, "AdditionalProperties"));
        }

        private static KeyValuePair<string, TValue>[] CaptureProperties<TValue>(
            IDictionary source,
            string label)
        {
            List<KeyValuePair<string, TValue>> entries = new(source.Count);
            foreach (DictionaryEntry entry in source)
            {
                if (entry.Key is not string key || entry.Value is not TValue value)
                    throw new InvalidOperationException($"BaseLib ModifierSave {label} has an invalid entry.");
                entries.Add(new KeyValuePair<string, TValue>(key, value));
            }
            entries.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
            return entries.ToArray();
        }

        public void AfterClonedOnCard(AbstractModel modifier, CardModel card)
            => afterClonedOnCard.Invoke(modifier, [card]);
    }
}
