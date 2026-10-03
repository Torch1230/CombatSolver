using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

if (args.Length != 1) throw new ArgumentException("Usage: ChoiceSourceAudit <output.json>");
Assembly game = typeof(AbstractModel).Assembly;
var rows = new List<object>();
var failures = new List<object>();
var powerOrigins = new List<object>();
int models = 0, roots = 0;
foreach (Type type in game.GetTypes().Where(t => !t.IsAbstract && !t.IsDefined(typeof(CompilerGeneratedAttribute), false)
    && t.Namespace is { } ns && ns.StartsWith("MegaCrit.Sts2.Core.Models.")
    && ns.Split('.').Last() is "Cards" or "Relics" or "Potions" or "Powers" or "Enchantments").OrderBy(t => t.FullName))
{
    models++;
    foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
    {
        if (method.GetMethodBody() is null) continue;
        roots++;
        try
        {
            MethodBase[] callees = Reachable(method, type).SelectMany(m => PatchProcessor.GetOriginalInstructions(m))
                .Select(i => i.operand).OfType<MethodBase>().ToArray();
            string[] powers = callees.OfType<MethodInfo>().Where(m => m.IsGenericMethod && m.DeclaringType?.Name == "PowerCmd"
                    && m.Name.StartsWith("Apply", StringComparison.Ordinal))
                .SelectMany(m => m.GetGenericArguments()).Where(t => typeof(PowerModel).IsAssignableFrom(t))
                .Select(t => t.Name).Distinct().Order().ToArray();
            if (powers.Length > 0) powerOrigins.Add(new { category = type.Namespace!.Split('.').Last(), model = type.Name, method = method.Name, powers });
            string[] calls = callees.Select(m => Describe(m))
                .Where(s => s is not null).Cast<string>().Distinct().Order().ToArray();
            if (calls.Length > 0) rows.Add(new { category = type.Namespace!.Split('.').Last(), model = type.Name,
                method = method.Name, signature = method.ToString(), calls });
        }
        catch (Exception e)
        {
            failures.Add(new { model = type.FullName, method = method.ToString(), error = e.GetType().FullName, detail = e.Message });
        }
    }
}
File.WriteAllText(args[0], JsonSerializer.Serialize(new { schemaVersion = 1, gameModuleId = game.ManifestModule.ModuleVersionId,
    models, roots, entries = rows, powerOrigins, failures, scope = "Declared instance methods plus async/iterator bodies and same-model helpers; original IL, no model execution" },
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"CHOICE_SOURCE_AUDIT models={models} roots={roots} matches={rows.Count} failures={failures.Count}");
if (failures.Count > 0) Environment.ExitCode = 1;

static string? Describe(MethodBase m)
{
    string? type = m.DeclaringType?.Name;
    string kind;
    if (type == "CardSelectCmd") kind = "selection";
    else if (type is "CardCmd" or "CardPileCmd" && m.Name is "AutoPlay" or "AutoPlayFromDrawPile") kind = "autoplay";
    else if (type == "CardFactory" && m.Name is "GetForCombat" or "GetDistinctForCombat" or "GetRandomCard") kind = "random_generation";
    else if (type == "CardPileCmd" && m.Name.Contains("Generated", StringComparison.Ordinal)) kind = "generation";
    else if (m.Name is "CreateCard" or "CreateCardForCombat" or "CreateCardInCombat" or "CreateCards") kind = "creation";
    else if (m.DeclaringType?.Assembly == typeof(AbstractModel).Assembly && m.Name == "Forge") kind = "forge";
    else if (type is "Shiv" or "Soul" or "SovereignBlade" && m.Name.Contains("Create", StringComparison.Ordinal)) kind = "generation_helper";
    else return null;
    return $"{kind}:{m.DeclaringType!.FullName}.{m.Name}";
}

static IEnumerable<MethodInfo> Reachable(MethodInfo root, Type model)
{
    var pending = new Queue<MethodInfo>();
    var visited = new HashSet<MethodInfo>();
    pending.Enqueue(root);
    while (pending.Count > 0)
    {
        MethodInfo method = pending.Dequeue();
        if (!visited.Add(method)) continue;
        Type? machine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? method.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
        MethodInfo? moveNext = machine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (MethodInfo implementation in moveNext is null ? new[] { method } : new[] { method, moveNext })
        {
            if (implementation.GetMethodBody() is null) continue;
            yield return implementation;
            foreach (var instruction in PatchProcessor.GetOriginalInstructions(implementation))
                if (instruction.operand is MethodInfo called &&
                    (called.DeclaringType == model || called.DeclaringType?.DeclaringType == model) && !visited.Contains(called))
                    pending.Enqueue(called);
        }
    }
}
