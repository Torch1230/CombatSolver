using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2)
    throw new ArgumentException("Usage: HealingSourceAudit game.dll output.json");
string dll = Path.GetFullPath(args[0]);
using ModuleDefinition module = ModuleDefinition.ReadModule(dll, new ReaderParameters
    { InMemory = true, ReadSymbols = false });
TypeDefinition[] types = Flatten(module.Types).ToArray();
MethodDefinition[] methods = types.SelectMany(type => type.Methods).ToArray();
var references = new List<Reference>();
var fieldWrites = new List<Reference>();
var fieldAddresses = new List<Reference>();
var asyncMappings = new List<Reference>();
var sinks = new HashSet<string>(StringComparer.Ordinal);
foreach (MethodDefinition method in methods)
{
    if (IsHealthEntry(method)) sinks.Add(method.FullName);
    foreach (CustomAttribute attribute in method.CustomAttributes)
    {
        if (attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
            && attribute.ConstructorArguments.Count == 1
            && attribute.ConstructorArguments[0].Value is TypeReference machine)
        {
            MethodDefinition? body = types.FirstOrDefault(type => type.FullName == machine.FullName)?
                .Methods.FirstOrDefault(candidate => candidate.Name == "MoveNext");
            if (body is not null)
                asyncMappings.Add(new(method.FullName, body.FullName, "async_body", -1, Owner(method.DeclaringType)));
        }
    }
    if (!method.HasBody) continue;
    foreach (Instruction instruction in method.Body.Instructions)
    {
        if (instruction.Operand is MethodReference callee)
        {
            MethodReference definition = callee is GenericInstanceMethod generic ? generic.ElementMethod : callee;
            references.Add(new(method.FullName, definition.FullName, instruction.OpCode.Name,
                instruction.Offset, Owner(method.DeclaringType),
                callee is GenericInstanceMethod constructed
                    ? constructed.GenericArguments.Select(type => type.FullName).ToArray() : []));
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
    }
}
Reference[] directHealthReferences = references.Where(reference => sinks.Contains(reference.Target))
    .OrderBy(reference => reference.Owner, StringComparer.Ordinal)
    .ThenBy(reference => reference.Caller, StringComparer.Ordinal).ThenBy(reference => reference.Offset).ToArray();
// The graph inventories possible static edges, including delegate creation. It
// cannot resolve virtual hook dispatch or prove a target, condition or HP amount.
var audit = new
{
    schemaVersion = 2,
    assembly = module.Assembly.Name.FullName,
    mvid = module.Mvid,
    dllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant(),
    typeCount = types.Length,
    methodCount = methods.Length,
    methodsWithBody = methods.Count(method => method.HasBody),
    referenceCount = references.Count,
    healthEntries = sinks.Order(StringComparer.Ordinal).ToArray(),
    directHealthReferences,
    directCreatureFieldWrites = fieldWrites,
    creatureHealthFieldAddresses = fieldAddresses,
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

sealed record Reference(string Caller, string Target, string Kind, int Offset, string Owner,
    string[]? GenericArguments = null);
