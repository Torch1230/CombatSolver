using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2)
    throw new ArgumentException("Usage: HealingSourceAudit input.dll output.json");
string dll = Path.GetFullPath(args[0]);
using ModuleDefinition module = ModuleDefinition.ReadModule(dll, new ReaderParameters
    { InMemory = true, ReadSymbols = false });
TypeDefinition[] types = Flatten(module.Types).ToArray();
MethodDefinition[] methods = types.SelectMany(type => type.Methods).ToArray();
var typeGroups = types.GroupBy(type => type.FullName, StringComparer.Ordinal).ToArray();
var methodGroups = methods.GroupBy(method => method.FullName, StringComparer.Ordinal).ToArray();
Dictionary<string, TypeDefinition> localTypes = typeGroups.Where(group => group.Count() == 1)
    .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
Dictionary<string, MethodDefinition> localMethods = methodGroups.Where(group => group.Count() == 1)
    .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
HashSet<string> ambiguousTypes = typeGroups.Where(group => group.Count() > 1)
    .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
HashSet<string> ambiguousMethods = methodGroups.Where(group => group.Count() > 1)
    .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
var references = new List<Reference>();
var cardEventAccessorCandidates = new List<EventAccessorCandidate>();
var fieldWrites = new List<Reference>();
var fieldAddresses = new List<Reference>();
var potionSlotFieldReferences = new List<Reference>();
var cardPileFieldReferences = new List<Reference>();
var asyncMappings = new List<Reference>();
var sinks = new HashSet<string>(StringComparer.Ordinal);
var potionInventoryEntries = new HashSet<string>(StringComparer.Ordinal);
var cardPileEntries = new HashSet<string>(StringComparer.Ordinal);
TypeDefinition? playerType = types.FirstOrDefault(type =>
    type.FullName == "MegaCrit.Sts2.Core.Entities.Players.Player");
EventDefinition[] potionInventoryEvents = playerType?.Events.Where(@event =>
    @event.Name.Contains("Potion", StringComparison.Ordinal)).ToArray() ?? [];
EventDefinition[] potionUseEvents = types.FirstOrDefault(type =>
    type.FullName == "MegaCrit.Sts2.Core.Models.PotionModel")?.Events.ToArray() ?? [];
potionInventoryEvents = potionInventoryEvents.Concat(potionUseEvents).ToArray();
var potionEventAccessors = potionInventoryEvents.SelectMany(@event =>
    new[] { @event.AddMethod, @event.RemoveMethod }).Where(method => method is not null)
    .Select(method => method!.FullName).ToHashSet(StringComparer.Ordinal);
EventDefinition[] cardStateEvents = types.Where(type => IsCardStateEventOwner(type.FullName))
    .SelectMany(type => type.Events).ToArray();
HashSet<string> cardEventAccessors = cardStateEvents.SelectMany(@event =>
        new[] { @event.AddMethod, @event.RemoveMethod }).Where(method => method is not null)
    .Select(method => method!.FullName).ToHashSet(StringComparer.Ordinal);
foreach (MethodDefinition method in methods)
{
    if (IsHealthEntry(method)) sinks.Add(method.FullName);
    if (IsPotionInventoryEntry(method)) potionInventoryEntries.Add(method.FullName);
    if (IsCardPileEntry(method)) cardPileEntries.Add(method.FullName);
    foreach (CustomAttribute attribute in method.CustomAttributes)
    {
        if (attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
            && attribute.ConstructorArguments.Count == 1
            && attribute.ConstructorArguments[0].Value is TypeReference machine)
        {
            MethodDefinition? body = types.FirstOrDefault(type => type.FullName == machine.FullName)?
                .Methods.FirstOrDefault(candidate => candidate.Name == "MoveNext");
            if (body is not null)
                asyncMappings.Add(new(method.FullName, body.FullName, "async_body", -1,
                    Owner(method.DeclaringType), CallerDefinitionToken: method.MetadataToken.ToInt32(),
                    OperandMetadataToken: body.MetadataToken.ToInt32()));
        }
    }
    if (!method.HasBody) continue;
    foreach (Instruction instruction in method.Body.Instructions)
    {
        if (instruction.Operand is MethodReference callee)
        {
            MethodReference definition = callee is GenericInstanceMethod generic ? generic.ElementMethod : callee;
            Reference reference = new(method.FullName, definition.FullName, instruction.OpCode.Name,
                instruction.Offset, Owner(method.DeclaringType),
                callee is GenericInstanceMethod constructed
                    ? constructed.GenericArguments.Select(type => type.FullName).ToArray() : [],
                method.MetadataToken.ToInt32(), callee.MetadataToken.ToInt32());
            references.Add(reference);
            // Dependency inputs do not define the game's events. Keep their
            // apparent accessor references too, including unknown scopes and
            // same-name methods; names alone do not resolve an event definition.
            if (IsCardStateEventOwner(definition.DeclaringType.FullName)
                && (definition.Name.StartsWith("add_", StringComparison.Ordinal)
                    || definition.Name.StartsWith("remove_", StringComparison.Ordinal)))
                cardEventAccessorCandidates.Add(new(reference, definition.DeclaringType.FullName,
                    definition.Name, definition.DeclaringType.Scope?.MetadataScopeType.ToString(),
                    definition.DeclaringType.Scope is ModuleDefinition scopeModule
                        ? scopeModule.Assembly.Name.FullName : definition.DeclaringType.Scope?.ToString(),
                    definition.HasThis, definition.ExplicitThis, definition.CallingConvention.ToString(),
                    definition.GenericParameters.Count, SlotTypeIdentity(definition.ReturnType),
                    definition.Parameters.Select(parameter => SlotTypeIdentity(parameter.ParameterType)).ToArray(),
                    "not-resolved"));
        }
        if (instruction.OpCode.Code is Code.Stfld or Code.Stsfld
            && instruction.Operand is FieldReference field
            && field.DeclaringType.FullName == "MegaCrit.Sts2.Core.Entities.Creatures.Creature"
            && field.Name is "_currentHp" or "_maxHp")
            fieldWrites.Add(new(method.FullName, field.FullName, instruction.OpCode.Name,
                instruction.Offset, Owner(method.DeclaringType)));
        if (instruction.OpCode.Code is Code.Ldflda or Code.Ldsflda
            && instruction.Operand is FieldReference address
            && address.DeclaringType.FullName == "MegaCrit.Sts2.Core.Entities.Creatures.Creature"
            && address.Name is "_currentHp" or "_maxHp")
            fieldAddresses.Add(new(method.FullName, address.FullName, instruction.OpCode.Name,
                instruction.Offset, Owner(method.DeclaringType)));
        // Read access matters too: callers can mutate the List after ldfld, or
        // expose its alias through PotionSlots. A write-only scan misses both.
        if (instruction.Operand is FieldReference potionField
            && potionField.DeclaringType.FullName == "MegaCrit.Sts2.Core.Entities.Players.Player"
            && potionField.Name == "_potionSlots")
            potionSlotFieldReferences.Add(new(method.FullName, potionField.FullName,
                instruction.OpCode.Name, instruction.Offset, Owner(method.DeclaringType)));
        // A read-only Cards/AllPiles view does not prove order independence:
        // fields can expose mutable collection aliases and indirect pile roots.
        if (instruction.Operand is FieldReference pileField && IsCardPileField(pileField))
            cardPileFieldReferences.Add(new(method.FullName, pileField.FullName,
                instruction.OpCode.Name, instruction.Offset, Owner(method.DeclaringType)));
    }
}
Reference[] directHealthReferences = references.Where(reference => sinks.Contains(reference.Target))
    .OrderBy(reference => reference.Owner, StringComparer.Ordinal)
    .ThenBy(reference => reference.Caller, StringComparer.Ordinal).ThenBy(reference => reference.Offset).ToArray();
HashSet<string> nativeHookTargets = references.Where(reference =>
        reference.Owner == "MegaCrit.Sts2.Core.Hooks.Hook"
        && reference.Target.Contains(" MegaCrit.Sts2.Core.Models.AbstractModel::", StringComparison.Ordinal))
    .Select(reference => reference.Target).ToHashSet(StringComparer.Ordinal);
VirtualSlot[] virtualModelSlots = methods.Where(method => method.IsVirtual
        && method.DeclaringType.Namespace.StartsWith("MegaCrit.Sts2.Core.Models", StringComparison.Ordinal))
    .Select(method => InspectLocalVirtualSlot(method, localTypes, localMethods, nativeHookTargets,
        ambiguousTypes, ambiguousMethods)).ToArray();
NativeHookBodyEvidence[] nativeHookBodies = NativeHookBodyInspection.Read(dll,
    methods.Where(method => nativeHookTargets.Contains(method.FullName)));
// The graph inventories possible static edges, including delegate creation. It
// cannot resolve virtual hook dispatch or prove a target, condition or HP amount.
var audit = new
{
    schemaVersion = 8,
    assembly = module.Assembly.Name.FullName,
    mvid = module.Mvid,
    dllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant(),
    typeCount = types.Length,
    methodCount = methods.Length,
    methodsWithBody = methods.Count(method => method.HasBody),
    referenceCount = references.Count,
    // FullName is a display signature. Different metadata rows can render the
    // same text; preserve them and refuse signature-only slot resolution.
    ambiguousTypeSignatures = typeGroups.Where(group => group.Count() > 1)
        .Select(group => new { signature = group.Key,
            tokens = group.Select(type => type.MetadataToken.ToInt32()).ToArray() }).ToArray(),
    ambiguousMethodSignatures = methodGroups.Where(group => group.Count() > 1)
        .Select(group => new { signature = group.Key, methods = group.Select(method => new
        {
            token = method.MetadataToken.ToInt32(), method.IsStatic, method.HasThis,
            callingConvention = method.CallingConvention.ToString(),
            genericParameterCount = method.GenericParameters.Count,
        }).ToArray() }).ToArray(),
    healthEntries = sinks.Order(StringComparer.Ordinal).ToArray(),
    directHealthReferences,
    directCreatureFieldWrites = fieldWrites,
    creatureHealthFieldAddresses = fieldAddresses,
    potionInventoryEntries = potionInventoryEntries.Order(StringComparer.Ordinal).ToArray(),
    directPotionInventoryReferences = references.Where(reference =>
        potionInventoryEntries.Contains(reference.Target)).ToArray(),
    potionSlotFieldDefinitions = playerType?.Fields.Where(field => field.Name == "_potionSlots")
        .Select(field => new { field = field.FullName, field.IsInitOnly, field.IsStatic }).ToArray() ?? [],
    potionSlotFieldReferences,
    cardPileEntries = cardPileEntries.Order(StringComparer.Ordinal).ToArray(),
    directCardPileReferences = references.Where(reference =>
        cardPileEntries.Contains(reference.Target)).ToArray(),
    cardPileFieldDefinitions = types.SelectMany(type => type.Fields).Where(IsCardPileField)
        .Select(field => new { field = field.FullName, field.IsInitOnly, field.IsStatic }).ToArray(),
    cardPileFieldReferences,
    cardStateEventDefinitions = cardStateEvents.Select(@event => new
        { owner = @event.DeclaringType.FullName, @event.Name, type = @event.EventType.FullName,
            add = @event.AddMethod?.FullName, remove = @event.RemoveMethod?.FullName }).ToArray(),
    cardStateEventReferences = references.Where(reference => cardEventAccessors.Contains(reference.Target)).ToArray(),
    cardStateEventAccessorCandidates = cardEventAccessorCandidates,
    potionInventoryEventDefinitions = potionInventoryEvents.Select(@event => new
        { owner = @event.DeclaringType.FullName, @event.Name, type = @event.EventType.FullName, add = @event.AddMethod?.FullName,
            remove = @event.RemoveMethod?.FullName }).ToArray(),
    potionInventoryEventReferences = references.Where(reference =>
        potionEventAccessors.Contains(reference.Target)).ToArray(),
    potionCallbackDefinitions = methods.Where(method => method.IsVirtual
        && method.DeclaringType.Namespace.StartsWith("MegaCrit.Sts2.Core.Models", StringComparison.Ordinal)
        && method.Name is "BeforePotionUsed" or "AfterPotionUsed" or "AfterPotionDiscarded"
            or "AfterPotionProcured" or "ShouldProcurePotion" or "ShouldForcePotionReward")
        .Select(method => new { owner = Owner(method.DeclaringType), method = method.FullName,
            method.HasBody, method.IsAbstract, baseType = method.DeclaringType.BaseType?.FullName })
        .ToArray(),
    sourceOwners = directHealthReferences.Select(reference => reference.Owner)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
    healthCallbackDefinitions = methods.Where(method => method.Name is "ShouldDie"
        or "ShouldDieLate" or "BeforeDeath" or "AfterDeath" or "AfterPreventingDeath"
        or "AfterCurrentHpChanged" or "AfterCombatEnd" or "BeforeCombatStart"
        or "AfterOstyHpChanged" or "AfterCombatVictory" or "AfterCombatVictoryEarly"
        or "AfterPlayerTurnStartLate" or "AfterRoomEntered" or "AfterObtained"
        or "AfterRestSiteHeal" or "AfterDiedToDoom" or "AfterDamageReceived" or "AfterDamageGiven")
        .Select(method => new { owner = Owner(method.DeclaringType), method = method.FullName,
            method.IsVirtual, method.HasBody, baseType = method.DeclaringType.BaseType?.FullName })
        .ToArray(),
    // Keep every virtual model method as an additional inventory. A manually
    // selected health-hook list alone misses indirect gold, permanent-deck,
    // summoning and side-turn callbacks. Neither list is a reachability proof.
    allVirtualModelMethods = methods.Where(method => method.IsVirtual
        && method.DeclaringType.Namespace.StartsWith("MegaCrit.Sts2.Core.Models", StringComparison.Ordinal))
        .Select(method => new { owner = Owner(method.DeclaringType), method = method.FullName,
            method.HasBody, method.IsAbstract, baseType = method.DeclaringType.BaseType?.FullName })
        .ToArray(),
    // Exact IL slots distinguish a reused inherited hook from a same-name
    // declaration. Resolution stays inside the input module; external or
    // ambiguous slots remain explicit unknowns rather than being treated safe.
    nativeHookModelTargets = nativeHookTargets.Order(StringComparer.Ordinal).ToArray(),
    nativeHookModelBodies = nativeHookBodies,
    virtualModelSlots,
    hookDispatchReferences = references.Where(reference =>
        reference.Target.Contains(" MegaCrit.Sts2.Core.Hooks.Hook::", StringComparison.Ordinal)).ToArray(),
    modelTypes = types.Where(type => type.DeclaringType is null
        && type.Namespace.StartsWith("MegaCrit.Sts2.Core.Models.", StringComparison.Ordinal))
        .Select(type => new { type = type.FullName, baseType = type.BaseType?.FullName,
            type.IsAbstract, methods = type.Methods.Select(method => method.FullName).ToArray() })
        .ToArray(),
    asyncMappings,
    staticReferences = references,
    limitations = new[]
    {
        "Inventory only: a reference is not proof of healing, reachability, target or finite bound.",
        "Health setters include damage, initialization, save restoration and network sync.",
        "Static calls/delegate creation cannot resolve virtual hooks, reflection or third-party extensions.",
        "HP amounts, callback dispatch and generation closure require source and native differential review.",
        "Potion slot reads include mutable List aliases; event subscribers and indirect relic acquisition require review.",
        "Pile fields/aliases and commands are an inventory, not proof of order independence or a complete reachable callback closure.",
        "Local virtual-slot identities do not prove hook bodies, active listeners, patches or recursively reachable generation safe.",
        "Card event accessors inventory static subscriptions; reflective subscribers and delegate-body effects require review.",
        "Display signatures are not method identities; ambiguity is retained. Operand tokens belong to this input module and need not identify a resolved target definition.",
        "Event definitions/references are local to the input. Accessor candidates also retain dependency references, but no candidate is resolved or certified from its name; scope/signature metadata requires definition and callback review.",
        "Native hook body shapes describe exact IL definitions, not whole hook identity or order safety. Static-call bodies retain unresolved dependencies; active patches, dispatch, events and future receivers require independent proof.",
    },
};
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine(JsonSerializer.Serialize(new
{
    audit.dllSha256, audit.typeCount, audit.methodCount, audit.methodsWithBody,
    healthEntryCount = sinks.Count, healthReferenceCount = directHealthReferences.Length,
    sourceOwnerCount = audit.sourceOwners.Length, fieldWriteCount = fieldWrites.Count,
    potionEntryCount = potionInventoryEntries.Count,
    potionSlotFieldReferenceCount = potionSlotFieldReferences.Count,
    potionEventReferenceCount = audit.potionInventoryEventReferences.Length,
    cardPileEntryCount = cardPileEntries.Count,
    cardPileFieldReferenceCount = cardPileFieldReferences.Count,
    cardStateEventCount = cardStateEvents.Length,
    cardStateEventReferenceCount = audit.cardStateEventReferences.Length,
    cardStateEventAccessorCandidateCount = cardEventAccessorCandidates.Count,
    nativeHookModelTargetCount = nativeHookTargets.Count,
    virtualModelSlotCount = virtualModelSlots.Length,
    unresolvedVirtualModelSlotCount = virtualModelSlots.Count(slot => slot.Status != "resolved-local"),
    nativeHookBodyCount = nativeHookBodies.Length,
}));

static IEnumerable<TypeDefinition> Flatten(IEnumerable<TypeDefinition> types)
{
    foreach (TypeDefinition type in types)
    {
        yield return type;
        foreach (TypeDefinition nested in Flatten(type.NestedTypes)) yield return nested;
    }
}

static string Owner(TypeDefinition type)
{
    while (type.DeclaringType is not null) type = type.DeclaringType;
    return type.FullName;
}

static bool IsHealthEntry(MethodDefinition method)
    => method.DeclaringType.FullName == "MegaCrit.Sts2.Core.Commands.CreatureCmd"
        && method.Name is "Heal" or "GainMaxHp" or "LoseMaxHp" or "SetCurrentHp"
            or "SetMaxHp" or "SetMaxAndCurrentHp"
        || method.DeclaringType.FullName == "MegaCrit.Sts2.Core.Entities.Creatures.Creature"
        && method.Name is "HealInternal" or "SetCurrentHpInternal" or "SetMaxHpInternal"
            or "set_CurrentHp" or "set_MaxHp";

static bool IsPotionInventoryEntry(MethodDefinition method)
    => method.DeclaringType.FullName == "MegaCrit.Sts2.Core.Commands.PotionCmd"
        && method.Name is "TryToProcure" or "Discard"
        || method.DeclaringType.FullName == "MegaCrit.Sts2.Core.Entities.Players.Player"
        && method.Name is "AddPotionInternal" or "DiscardPotionInternal" or "RemoveUsedPotionInternal"
            or "RemovePotionInternal" or "SetMaxPotionCountInternal" or "AddToMaxPotionCount"
            or "SubtractFromMaxPotionCount" or "LoadPotions" or "PopulateStartingInventory"
        || method.DeclaringType.FullName == "MegaCrit.Sts2.Core.Commands.PlayerCmd"
        && method.Name is "GainMaxPotionCount" or "LoseMaxPotionCount"
        || method.DeclaringType.FullName == "MegaCrit.Sts2.Core.Models.PotionModel"
        && method.Name is "Discard" or "RemoveBeforeUse" or "OnUseWrapper" or "EnqueueManualUse";

static bool IsCardPileEntry(MethodDefinition method)
    => method.DeclaringType.FullName is "MegaCrit.Sts2.Core.Entities.Cards.CardPile"
        or "MegaCrit.Sts2.Core.Commands.CardPileCmd" or "MegaCrit.Sts2.Core.Commands.CardCmd"
        || ContainsCardPileType(method.ReturnType)
        || method.DeclaringType.FullName == "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState"
        && method.Name is "get_AllPiles" or "get_AllCards" or "get_ExhaustPile"
        || method.DeclaringType.FullName == "MegaCrit.Sts2.Core.Entities.Players.Player"
        && method.Name == "get_Piles"
        || method.DeclaringType.FullName == "MegaCrit.Sts2.Core.Entities.Cards.PileTypeExtensions"
        && method.Name == "GetPile"
        || method.DeclaringType.FullName == "MegaCrit.Sts2.Core.Combat.CombatState"
        && method.Name == "IterateHookListeners";

static bool IsCardPileField(FieldReference field)
    => field.DeclaringType.FullName == "MegaCrit.Sts2.Core.Entities.Cards.CardPile"
        || ContainsCardPileType(field.FieldType);

static bool IsCardStateEventOwner(string fullName)
    => fullName is "MegaCrit.Sts2.Core.Models.AbstractModel" or "MegaCrit.Sts2.Core.Models.CardModel"
        or "MegaCrit.Sts2.Core.Models.EnchantmentModel" or "MegaCrit.Sts2.Core.Models.AfflictionModel"
        or "MegaCrit.Sts2.Core.Entities.Cards.CardPile"
        or "MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost"
        or "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState";

static bool ContainsCardPileType(TypeReference type)
    => type.FullName == "MegaCrit.Sts2.Core.Entities.Cards.CardPile"
        || type is GenericInstanceType generic && generic.GenericArguments.Any(ContainsCardPileType)
        || type is TypeSpecification specification && ContainsCardPileType(specification.ElementType);

static VirtualSlot InspectLocalVirtualSlot(MethodDefinition method,
    IReadOnlyDictionary<string, TypeDefinition> localTypes,
    IReadOnlyDictionary<string, MethodDefinition> localMethods,
    IReadOnlySet<string> nativeHookTargets,
    IReadOnlySet<string> ambiguousTypes,
    IReadOnlySet<string> ambiguousMethods)
{
    var chain = new List<string>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    MethodDefinition current = method;
    while (true)
    {
        if (ambiguousTypes.Contains(current.DeclaringType.FullName))
            return Result("ambiguous-declaring-type", current.DeclaringType.FullName);
        if (ambiguousMethods.Contains(current.FullName))
            return Result("ambiguous-method-identity", current.FullName);
        if (!seen.Add(current.FullName)) return Result("cyclic-slot", null);
        chain.Add(current.FullName);
        // Explicit overrides can name an interface slot. Do not replace them
        // with a heuristic name match, including when several are present.
        if (current.Overrides.Count > 0)
        {
            if (current.Overrides.Count != 1) return Result("multiple-explicit-slots", null);
            MethodReference target = current.Overrides[0];
            if (IsLocalTypeScope(target.DeclaringType, method.Module)
                && ambiguousMethods.Contains(target.FullName))
                return Result("ambiguous-explicit-slot", target.FullName);
            if (!IsLocalTypeScope(target.DeclaringType, method.Module)
                || !localMethods.TryGetValue(target.FullName, out MethodDefinition? overridden))
                return Result("external-or-unresolved-explicit-slot", target.FullName);
            current = overridden;
            continue;
        }
        if (current.IsNewSlot) return Result("resolved-local", current.FullName);

        TypeReference? parent = current.DeclaringType.BaseType;
        while (parent is not null)
        {
            if (IsLocalTypeScope(parent, method.Module) && ambiguousTypes.Contains(parent.FullName))
                return Result("ambiguous-base-type", parent.FullName);
            if (!IsLocalTypeScope(parent, method.Module)
                || !localTypes.TryGetValue(parent.FullName, out TypeDefinition? definition))
                return Result("external-or-unresolved-base", parent.FullName);
            MethodDefinition[] candidates = definition.Methods.Where(candidate => candidate.IsVirtual
                && HasSameSlotParameters(candidate, current)).ToArray();
            if (candidates.Length > 1) return Result("ambiguous-base-slot", null);
            if (candidates.Length == 1)
            {
                if (SlotTypeIdentity(candidates[0].ReturnType) != SlotTypeIdentity(current.ReturnType))
                    return Result("covariant-or-unresolved-return", candidates[0].FullName);
                current = candidates[0];
                break;
            }
            parent = definition.BaseType;
        }
        if (parent is null) return Result("missing-base-slot", null);
    }

    VirtualSlot Result(string status, string? rootOrUnresolved)
        => new(Owner(method.DeclaringType), method.FullName, method.IsNewSlot, method.IsFinal,
            method.IsAbstract, method.HasBody, method.Overrides.Select(@override => @override.FullName).ToArray(),
            status, rootOrUnresolved, chain.ToArray(),
            status == "resolved-local" && rootOrUnresolved is not null && nativeHookTargets.Contains(rootOrUnresolved));
}

static bool HasSameSlotParameters(MethodDefinition left, MethodDefinition right)
    => left.Name == right.Name && left.GenericParameters.Count == right.GenericParameters.Count
        && left.Parameters.Select(parameter => parameter.ParameterType.FullName)
            .SequenceEqual(right.Parameters.Select(parameter => parameter.ParameterType.FullName), StringComparer.Ordinal)
        && left.Parameters.Select(parameter => SlotTypeIdentity(parameter.ParameterType))
            .SequenceEqual(right.Parameters.Select(parameter => SlotTypeIdentity(parameter.ParameterType)), StringComparer.Ordinal);

static string SlotTypeIdentity(TypeReference type)
    => type switch
    {
        GenericParameter parameter => $"{parameter.Type}:{parameter.Position}",
        GenericInstanceType generic => SlotTypeIdentity(generic.ElementType) + "<"
            + string.Join(",", generic.GenericArguments.Select(SlotTypeIdentity)) + ">",
        RequiredModifierType required => "modreq:" + SlotTypeIdentity(required.ModifierType)
            + ":" + SlotTypeIdentity(required.ElementType),
        OptionalModifierType optional => "modopt:" + SlotTypeIdentity(optional.ModifierType)
            + ":" + SlotTypeIdentity(optional.ElementType),
        FunctionPointerType pointer => $"fn:{pointer.CallingConvention}:{pointer.HasThis}:{pointer.ExplicitThis}:"
            + SlotTypeIdentity(pointer.ReturnType) + "("
            + string.Join(",", pointer.Parameters.Select(parameter => SlotTypeIdentity(parameter.ParameterType))) + ")",
        TypeSpecification specification => specification.FullName + ":" + SlotTypeIdentity(specification.ElementType),
        _ => type.FullName + "@" + (type.Scope is ModuleDefinition definition
            ? definition.Assembly.Name.FullName : type.Scope?.ToString()),
    };

static bool IsLocalTypeScope(TypeReference type, ModuleDefinition module)
    => type.Scope switch
    {
        ModuleDefinition definition => ReferenceEquals(definition, module),
        AssemblyNameReference assembly => assembly.FullName == module.Assembly.Name.FullName,
        _ => false,
    };

sealed record VirtualSlot(string Owner, string Method, bool IsNewSlot, bool IsFinal,
    bool IsAbstract, bool HasBody, string[] ExplicitOverrides, string Status,
    string? RootOrUnresolved, string[] Chain, bool IsNativeHookTarget);

sealed record Reference(string Caller, string Target, string Kind, int Offset, string Owner,
    string[]? GenericArguments = null, int? CallerDefinitionToken = null, int? OperandMetadataToken = null);

sealed record EventAccessorCandidate(Reference Reference, string DeclaringType, string AccessorName,
    string? ScopeKind, string? ScopeIdentity, bool HasThis, bool ExplicitThis, string CallingConvention,
    int GenericParameterCount, string ReturnTypeIdentity, string[] ParameterTypeIdentities,
    string DefinitionResolution);
