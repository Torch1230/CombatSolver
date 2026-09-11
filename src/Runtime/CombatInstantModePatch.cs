using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

// Override the speed read by combat timing code, not the preference that is serialized.
// Map, rewards, transitions, settings and console commands retain their original reads.
internal sealed class CombatInstantModePatch : IPatchMethod
{
    private static readonly MethodInfo Getter = AccessTools.PropertyGetter(typeof(PrefsSave), nameof(PrefsSave.FastMode));
    public static string PatchId => "combat_solver_combat_instant_mode";
    public static string Description => "瞬间速度覆盖战斗内玩家与怪物行动，局外沿用游戏设置";

    public static ModPatchTarget[] GetTargets()
    {
        List<ModPatchTarget> targets = [];
        foreach (Type type in typeof(CombatManager).Assembly.GetTypes().Where(IsCombatTimingType))
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (!ContainsSpeedRead(method)) continue;
            if (method.ContainsGenericParameters)
                throw new InvalidOperationException($"Open generic combat timing method: {method}");
            targets.Add(new(type, method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray()));
        }
        if (targets.Count == 0) throw new MissingMethodException("Native combat speed readers were not found.");
        return targets.ToArray();
    }

    private static bool IsCombatTimingType(Type type)
    {
        string name = type.FullName!;
        return name.StartsWith("MegaCrit.Sts2.Core.Combat.", StringComparison.Ordinal)
            || name.StartsWith("MegaCrit.Sts2.Core.Commands.", StringComparison.Ordinal)
            || name.StartsWith("MegaCrit.Sts2.Core.Models.Cards.", StringComparison.Ordinal)
            || name.StartsWith("MegaCrit.Sts2.Core.Models.Monsters.", StringComparison.Ordinal)
            || name.StartsWith("MegaCrit.Sts2.Core.Nodes.Combat.", StringComparison.Ordinal)
            || name.StartsWith("MegaCrit.Sts2.Core.Nodes.Cards.", StringComparison.Ordinal)
            || name.StartsWith("MegaCrit.Sts2.Core.Nodes.Vfx.NMonsterDeathVfx", StringComparison.Ordinal);
    }

    private static bool ContainsSpeedRead(MethodInfo method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null) return false;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if ((il[i] == OpCodes.Call.Value || il[i] == OpCodes.Callvirt.Value)
                && BitConverter.ToInt32(il, i + 1) == Getter.MetadataToken)
                return PatchProcessor.GetOriginalInstructions(method).Any(instruction => instruction.Calls(Getter));
        }
        return false;
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(Getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(CombatInstantModePatch), nameof(Resolve));
            }
            yield return instruction;
        }
    }

    internal static FastModeType Resolve(PrefsSave prefs)
        => Entry.Enabled && !SolverController.SolverDisabled
           && SolverSettings.Current.DeploymentFastMode == SolverDeploymentFastMode.Instant
           && CombatManager.Instance.IsInProgress && !SolverController.IsMultiplayerSession
            ? FastModeType.Instant
            : prefs.FastMode;
}
