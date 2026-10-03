using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using CombatSolver;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {++checks}: {name}");
}
MethodInfo method = typeof(PowerModel).GetMethod(nameof(PowerModel.GetTypeForAmount))!;
var original = method.CreateDelegate<Func<PowerModel, decimal, PowerType>>();
ProbePower power = (ProbePower)RuntimeHelpers.GetUninitializedObject(typeof(ProbePower));
int[] values = [int.MinValue, -1, 0, 1, 2, 3, int.MaxValue];
decimal[] amounts = [decimal.MinValue, -1m, -.1m, new decimal(0, 0, 0, true, 0), 0m, .1m, 1m, decimal.MaxValue];
var cases = (from stack in values from type in values from allow in new[] { false, true }
             from alternate in new[] { false, true } from amount in amounts
             select new Case(stack, type, allow, alternate, amount)).ToArray();
var expected = cases.Select(input => Observe(original, power, input)).ToArray();
Case common = new(1, 1, false, false, 1m);
long beforeAllocation = Allocated(original, power, common);

var nativeCode = PatchProcessor.GetOriginalInstructions(method).ToArray();
Check(nativeCode.Count(code => code.opcode == OpCodes.Box) == 2, "actual native routine has the two boxed comparisons");
var rewritten = PowerAmountComparisonPatch.Transpiler(nativeCode).ToArray();
Check(PowerAmountComparisonPatch.RewrittenComparisons == 2
    && rewritten.Count(code => code.opcode == OpCodes.Ceq) == 2
    && rewritten.All(code => code.opcode != OpCodes.Box), "production rewrite removes exactly the two boxes");
Check(nativeCode.Count(code => code.opcode == OpCodes.Box) == 2
    && nativeCode.Zip(rewritten).All(pair => pair.First.labels.SequenceEqual(pair.Second.labels)
        && pair.First.blocks.SequenceEqual(pair.Second.blocks)), "input IL and all labels/exception blocks are preserved");
CodeInstruction[] unknown = [new(OpCodes.Ret)];
Check(PowerAmountComparisonPatch.Transpiler(unknown).SequenceEqual(unknown)
    && PowerAmountComparisonPatch.RewrittenComparisons == 0, "unknown IL stays unchanged");
var labeled = nativeCode.Select(code => new CodeInstruction(code)).ToArray();
var generator = new DynamicMethod("label_fixture", typeof(void), []).GetILGenerator();
labeled.First(code => code.opcode == OpCodes.Box).labels.Add(generator.DefineLabel());
Check(PowerAmountComparisonPatch.Transpiler(labeled).SequenceEqual(labeled)
    && PowerAmountComparisonPatch.RewrittenComparisons == 0, "entry into the middle of a comparison keeps the original IL");

Harmony harmony = new("CombatSolver.PowerAmountComparisonChecks");
try
{
    MethodInfo replacement = harmony.Patch(method,
        transpiler: new HarmonyMethod(typeof(PowerAmountComparisonPatch).GetMethod(nameof(PowerAmountComparisonPatch.Transpiler))!));
    var patched = replacement.CreateDelegate<Func<PowerModel, decimal, PowerType>>();
    Check(PowerAmountComparisonPatch.RewrittenComparisons == 2, "actual Harmony patch applies the production rewrite");
    for (int index = 0; index < cases.Length; index++)
    {
        var observed = Observe(patched, power, cases[index]);
        if (observed != expected[index])
            throw new InvalidOperationException($"Output/getter order differs for {cases[index]}: {expected[index]} / {observed}");
    }
    Check(true, $"{cases.Length} combinations match native outputs and getter call order, including changing getters");
    long afterAllocation = Allocated(patched, power, common);
    Check(beforeAllocation > 0 && afterAllocation < beforeAllocation / 100,
        $"100000 calls remove boxing allocation ({beforeAllocation} -> {afterAllocation} bytes)");
}
finally
{
    harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
}
Console.WriteLine($"POWER_AMOUNT_COMPARISON_CHECKS_OK checks={checks}");

static (PowerType Result, string Calls) Observe(
    Func<PowerModel, decimal, PowerType> invoke, ProbePower power, Case input)
{
    power.Reset(input, trace: true);
    PowerType result = invoke(power, input.Amount);
    return (result, new string(power.Calls!.ToArray()));
}

[MethodImpl(MethodImplOptions.NoInlining)]
static long Allocated(Func<PowerModel, decimal, PowerType> invoke, ProbePower power, Case input)
{
    power.Reset(input, trace: false);
    for (int index = 0; index < 10_000; index++) _ = invoke(power, input.Amount);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < 100_000; index++) _ = invoke(power, input.Amount);
    return GC.GetAllocatedBytesForCurrentThread() - before;
}

internal sealed record Case(int Stack, int Type, bool Allow, bool Alternate, decimal Amount);

internal sealed class ProbePower : PowerModel
{
    private Case _input = null!;
    private int _allowReads;
    private int _typeReads;
    public List<char>? Calls;
    public void Reset(Case input, bool trace)
    {
        _input = input;
        _allowReads = _typeReads = 0;
        Calls = trace ? [] : null;
    }
    public override PowerStackType StackType
    {
        get { Calls?.Add('S'); return (PowerStackType)_input.Stack; }
    }
    public override bool AllowNegative
    {
        get
        {
            Calls?.Add('A');
            return _input.Alternate && _allowReads++ != 0 ? !_input.Allow : _input.Allow;
        }
    }
    public override PowerType Type
    {
        get
        {
            Calls?.Add('T');
            return (PowerType)(_input.Alternate && _typeReads++ != 0 ? unchecked(_input.Type + 1) : _input.Type);
        }
    }
}
