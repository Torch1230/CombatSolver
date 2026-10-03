using System.Reflection;
using System.Reflection.Emit;
using CombatSolver;
using MegaCrit.Sts2.Core.Models;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
Type ModType(string assembly) => AssemblyBuilder.DefineDynamicAssembly(
    new AssemblyName(assembly), AssemblyBuilderAccess.Run).DefineDynamicModule(assembly)
    .DefineType(typeof(Leap).FullName!, TypeAttributes.Public).CreateType()!;
bool Hits(Type type, ModelId expected)
{
    ModelId value = null!;
    return !ModelDbGetIdCachePatch.Prefix(type, ref value) && value == expected;
}

Type first = ModType("FirstContent"), second = ModType("SecondContent");
ModelId vanilla = new("CARD", "LEAP");
ModelDbGetIdCachePatch.Postfix(typeof(Leap), vanilla);
Check(Hits(typeof(Leap), vanilla), "Vanilla IDs must remain cacheable before registration.");
ModelDbGetIdCachePatch.Postfix(first, vanilla); // Registration conflict probe, before prefix assignment.
ModelId transient = null!;
Check(ModelDbGetIdCachePatch.Prefix(first, ref transient), "Registration probe cached an unprefixed mod ID.");
ModelId firstId = new("CARD", "FIRST_CARD_LEAP"), secondId = new("CARD", "SECOND_CARD_LEAP");
ModelDbGetIdCachePatch.Postfix(first, firstId);
Check(ModelDbGetIdCachePatch.Prefix(first, ref transient), "Mod ID cached before registry completion.");
(typeof(ModelDbGetIdCachePatch).GetMethod("MarkModelRegistryInitialized", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new MissingMethodException("Model registry completion signal is missing.")).Invoke(null, null);
ModelDbGetIdCachePatch.Postfix(first, firstId);
ModelDbGetIdCachePatch.Postfix(second, secondId); // Mod registered before the cache was installed.
Check(Hits(first, firstId), "Registration completion did not cache the final prefixed ID.");
Check(Hits(second, secondId) && Hits(typeof(Leap), vanilla), "Same-name types from different assemblies collided.");
Parallel.For(0, 128, _ =>
{
    if (!Hits(first, firstId) || !Hits(second, secondId))
        throw new InvalidOperationException("Concurrent ID lookup changed a registered mapping.");
});
checks++;
Check(ModelDbGetIdCachePatch.Prefix(null!, ref transient), "Null must reach the native failure path.");
ModelDbGetIdCachePatch.Postfix(null!, null!);
Console.WriteLine($"MODEL_ID_CACHE_OK checks={checks}");
