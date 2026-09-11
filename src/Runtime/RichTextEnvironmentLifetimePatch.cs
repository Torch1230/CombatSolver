using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.RichTextTags;
using STS2RitsuLib.Patching.Models;
using EnvironmentDictionary = Godot.Collections.Dictionary;

namespace CombatSolver;

// CharFXTransform.Env returns an owned wrapper. Keep the native effect's calculations,
// but release that wrapper at the end of the synchronous callback instead of on the finalizer thread.
internal sealed class RichTextEnvironmentLifetimePatch : IPatchMethod
{
    [ThreadStatic] private static List<EnvironmentDictionary>? _environments;
    public static string PatchId => "combat_solver_rich_text_environment_lifetime";
    public static string Description => "及时释放原版文字特效的临时环境字典";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(RichTextSine), nameof(RichTextSine._ProcessCustomFX), [typeof(CharFXTransform)]),
        new(typeof(RichTextFlyIn), nameof(RichTextFlyIn._ProcessCustomFX), [typeof(CharFXTransform)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Prefix(out int __state) => __state = _environments?.Count ?? 0;

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo getter = AccessTools.PropertyGetter(typeof(CharFXTransform), nameof(CharFXTransform.Env));
        if (code.Count(i => i.Calls(getter)) != 1
            || code.Any(i => i.operand is MethodInfo m && m.Name == nameof(IDisposable.Dispose)))
            throw new InvalidOperationException("Native rich text environment ownership has changed.");
        foreach (CodeInstruction instruction in code)
        {
            if (instruction.Calls(getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(RichTextEnvironmentLifetimePatch), nameof(CaptureEnvironment));
            }
        }
        return code;
    }

    private static EnvironmentDictionary CaptureEnvironment(CharFXTransform transform)
    {
        EnvironmentDictionary environment = transform.Env;
        (_environments ??= []).Add(environment);
        return environment;
    }

    public static void Finalizer(int __state)
    {
        if (_environments == null) return;
        while (_environments.Count > __state)
        {
            int index = _environments.Count - 1;
            EnvironmentDictionary environment = _environments[index];
            _environments.RemoveAt(index);
            environment.Dispose();
        }
    }
}
