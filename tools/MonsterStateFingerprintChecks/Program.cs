using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

string dll = Path.GetFullPath(Option("--dll") ?? ".godot/mono/temp/bin/Release/CombatSolver.dll");
string game = Path.GetFullPath(Option("--game-dir") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".local/share/Steam/steamapps/common/Slay the Spire 2/data_sts2_linuxbsd_x86_64"));
AssemblyLoadContext context = new("monster-fingerprint");
context.Resolving += (_, name) =>
{
    foreach (string directory in new[] { Path.GetDirectoryName(dll)!, game })
    {
        string path = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
Assembly sts2 = context.LoadFromAssemblyPath(Path.Combine(game, "sts2.dll"));
Assembly mod = context.LoadFromAssemblyPath(dll);
Type stateType = mod.GetType("CombatSolver.SimulatedCombatState", true)!;
Type builderType = mod.GetType("CombatSolver.StateFingerprintBuilder", true)!;
Type creatureType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Creatures.Creature", true)!;
Type listType = mod.GetType("CombatSolver.ForkableList`1", true)!.MakeGenericType(creatureType);
Type keyType = typeof(ValueTuple<,>).MakeGenericType(creatureType, typeof(string));
Type dictionaryType = mod.GetType("CombatSolver.ForkableDictionary`2", true)!.MakeGenericType(keyType, typeof(int));
const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
FieldInfo knownEnemies = stateType.GetField("_knownEnemies", PrivateInstance)!;
FieldInfo monsterStates = stateType.GetField("_monsterIntStates", PrivateInstance)!;
FieldInfo combatId = creatureType.GetField("<CombatId>k__BackingField", PrivateInstance)!;
MethodInfo append = stateType.GetMethod("AppendMonsterStateFingerprint", PrivateInstance)!;
MethodInfo finish = builderType.GetMethod("Finish")!;
MethodInfo add = dictionaryType.GetMethod("Add")!;
PropertyInfo item = dictionaryType.GetProperty("Item")!;
int checks = 0;
Check(null, []);
Check(Dictionary([]), []);
foreach (uint? id in new uint?[] { null, 0, 7, uint.MaxValue })
{
    Entry[] entries = [NewEntry(id, "_hasAmalgamDied", -1)];
    Check(Dictionary(entries), entries);
}
// Equal sort keys on distinct creatures must preserve insertion order.
Entry[] ties = [NewEntry(2, "same", 11), NewEntry(null, "z", 12), NewEntry(2, "same", 13),
    NewEntry(2, "a", 14), NewEntry(uint.MaxValue, "z", int.MinValue)];
Check(Dictionary(ties), ties);
Random random = new(20260922);
for (int test = 0; test < 64; test++)
{
    Entry[] entries = Enumerable.Range(0, random.Next(25)).Select(_ => NewEntry(
        random.Next(4) == 0 ? null : (uint)random.Next(4),
        new[] { "a", "z", "状态" }[random.Next(3)], random.Next(-100, 100))).ToArray();
    Check(Dictionary(entries), entries);
}
Entry original = NewEntry(1, "counter", 1);
object parent = Dictionary([original]);
object child = dictionaryType.GetMethod("Fork")!.Invoke(parent, null)!;
item.SetValue(child, 9, [original.Key]);
Entry extra = NewEntry(null, "extra", int.MaxValue);
add.Invoke(child, [extra.Key, extra.Value]);
Check(parent, [original]);
Check(child, [original with { Value = 9 }, extra]);
dictionaryType.GetMethod("Clear")!.Invoke(child, null);
Check(child, []);
Check(parent, [original]);
item.SetValue(parent, 3, [original.Key]);
Check(parent, [original with { Value = 3 }]);
Check(child, []);
Console.WriteLine($"MONSTER_FINGERPRINT_OK checks={checks} dll={dll}");

if (args.Contains("--benchmark"))
{
    // Reflection/boxing is setup-only: each iteration directly invokes the production
    // appender on a stack-local builder and returns a consumed hash word.
    DynamicMethod method = new("AppendFingerprint", typeof(ulong), [typeof(object)], typeof(Program).Module, true);
    ILGenerator il = method.GetILGenerator();
    LocalBuilder builder = il.DeclareLocal(builderType);
    LocalBuilder result = il.DeclareLocal(finish.ReturnType);
    il.Emit(OpCodes.Ldloca, builder);
    il.Emit(OpCodes.Call, builderType.GetConstructor(Type.EmptyTypes)!);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Castclass, stateType);
    il.Emit(OpCodes.Ldloca, builder);
    il.Emit(OpCodes.Call, append);
    il.Emit(OpCodes.Ldloca, builder);
    il.Emit(OpCodes.Call, finish);
    il.Emit(OpCodes.Stloc, result);
    il.Emit(OpCodes.Ldloca, result);
    il.Emit(OpCodes.Call, finish.ReturnType.GetProperty("First")!.GetMethod!);
    il.Emit(OpCodes.Ret);
    var run = method.CreateDelegate<Func<object, ulong>>();
    foreach (int count in new[] { 0, 1, 4 })
    {
        object state = State(Dictionary(ties.Take(count).ToArray()));
        ulong sink = 0;
        for (int warm = 0; warm < 50_000; warm++) sink ^= run(state);
        const int iterations = 500_000;
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int n = 0; n < iterations; n++) sink ^= run(state);
        double ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Console.WriteLine(JsonSerializer.Serialize(new { count, iterations, bytesPerCall = (double)allocated / iterations,
            nanosecondsPerCall = ns, sink }));
    }
}

Entry NewEntry(uint? id, string name, int value)
{
    object creature = RuntimeHelpers.GetUninitializedObject(creatureType);
    combatId.SetValue(creature, id);
    return new(Activator.CreateInstance(keyType, [creature, name])!, id, name, value);
}
object Dictionary(Entry[] entries)
{
    object dictionary = Activator.CreateInstance(dictionaryType)!;
    foreach (Entry entry in entries) add.Invoke(dictionary, [entry.Key, entry.Value]);
    return dictionary;
}
object State(object? dictionary)
{
    object state = RuntimeHelpers.GetUninitializedObject(stateType);
    knownEnemies.SetValue(state, Activator.CreateInstance(listType));
    monsterStates.SetValue(state, dictionary);
    return state;
}
void Check(object? dictionary, Entry[] entries)
{
    object actual = Builder();
    object[] parameters = [actual];
    append.Invoke(State(dictionary), parameters);
    object expected = Builder();
    foreach (Entry entry in entries.OrderBy(x => x.Id).ThenBy(x => x.Name, StringComparer.Ordinal))
    {
        Add(expected, typeof(char), 'm');
        Add(expected, typeof(uint), entry.Id ?? uint.MaxValue);
        Add(expected, typeof(string), entry.Name);
        Add(expected, typeof(int), entry.Value);
    }
    if (!finish.Invoke(parameters[0], null)!.Equals(finish.Invoke(expected, null)))
        throw new InvalidOperationException($"Fingerprint mismatch at check {checks} (count={entries.Length}).");
    checks++;
}
object Builder()
{
    object builder = Activator.CreateInstance(builderType)!;
    Add(builder, typeof(string), "existing-prefix");
    return builder;
}
void Add(object builder, Type type, object value) => builderType.GetMethod("Add", [type])!.Invoke(builder, [value]);
string? Option(string name)
{
    int index = Array.IndexOf(args, name);
    return index < 0 ? null : args[index + 1];
}
record Entry(object Key, uint? Id, string Name, int Value);
