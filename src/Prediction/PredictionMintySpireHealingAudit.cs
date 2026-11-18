using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver;

internal static class PredictionMintySpireHealingAudit
{
    private static readonly MethodInfo LocalCreature =
        AccessTools.Method(typeof(LocalContext), nameof(LocalContext.IsMe), [typeof(Creature)]);
    private static readonly MethodInfo CombatManagerGetter =
        AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.Instance));
    private static readonly MethodInfo CombatActiveGetter =
        AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsInProgress));
    private static readonly ConditionalWeakTable<MethodInfo, ReviewedGuard> ReviewedGuards = new();

    private sealed record ReviewedGuard(MethodInfo? CombatActive);

    internal static bool IsRestSitePreviewPostfix(Patch patch)
        => patch.owner == "MintySpire2"
            && patch.PatchMethod is { Name: "CatchHPChange", IsStatic: true } method
            && method.DeclaringType?.FullName == "MintySpire2.MintySpire2Code.RestHPRender"
            && ReviewedGuards.GetValue(method, ReviewGuard).CombatActive is { } guard
            && Harmony.GetPatchInfo(method) is not { Owners.Count: > 0 }
            && Harmony.GetPatchInfo(guard) is not { Owners.Count: > 0 }
            && Harmony.GetPatchInfo(LocalCreature) is not { Owners.Count: > 0 }
            && Harmony.GetPatchInfo(CombatManagerGetter) is not { Owners.Count: > 0 }
            && Harmony.GetPatchInfo(CombatActiveGetter) is not { Owners.Count: > 0 };

    private static ReviewedGuard ReviewGuard(MethodInfo postfix)
    {
        if (postfix.ReturnType != typeof(void)
            || !postfix.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(Creature), typeof(decimal), typeof(bool)])
            || postfix.GetMethodBody() is not { ExceptionHandlingClauses.Count: 0 })
            return new(null);

        List<CodeInstruction> body = PatchProcessor.GetOriginalInstructions(postfix);
        MethodInfo? guard = AccessTools.Method(postfix.DeclaringType!.Assembly.GetType(
            "MintySpire2.MintySpire2Code.util.Wiz"), "IsInCombat", Type.EmptyTypes);
        return guard is { IsStatic: true, ReturnType: var type } && type == typeof(bool)
            && IsCombatActiveQuery(guard)
            && ReturnsInCombat(body, guard, localCreature: true)
            && ReturnsInCombat(body, guard, localCreature: false)
            ? new(guard) : new(null);
    }

    private static bool IsCombatActiveQuery(MethodInfo guard)
    {
        if (guard.GetMethodBody() is not { ExceptionHandlingClauses.Count: 0 })
            return false;
        var code = PatchProcessor.GetOriginalInstructions(guard).Where(i => i.opcode != OpCodes.Nop).ToArray();
        if (code.Length < 3 || !code[0].Calls(CombatManagerGetter) || !code[1].Calls(CombatActiveGetter))
            return false;
        return code is [_, _, var direct] && direct.opcode == OpCodes.Ret
            || code is [_, _, var store, var jump, var load, var ret]
                && store.opcode == OpCodes.Stloc_0 && load.opcode == OpCodes.Ldloc_0 && ret.opcode == OpCodes.Ret
                && (jump.opcode == OpCodes.Br || jump.opcode == OpCodes.Br_S)
                && jump.operand is Label target && load.labels.Contains(target);
    }

    // Follow the two combat paths through the guard. Only reads, branches and return
    // are allowed before the out-of-combat preview work.
    private static bool ReturnsInCombat(List<CodeInstruction> body, MethodInfo guard, bool localCreature)
    {
        Stack<bool> values = new();
        bool? local = null;
        for (int offset = 0, steps = 0; offset >= 0 && offset < body.Count && steps < body.Count; steps++, offset++)
        {
            var instruction = body[offset];
            OpCode op = instruction.opcode;
            if (op == OpCodes.Ret)
                return values.Count == 0;
            if (op == OpCodes.Nop)
                continue;
            if (op == OpCodes.Ldarg_0 && offset + 1 < body.Count && body[offset + 1].Calls(LocalCreature))
            {
                values.Push(localCreature);
                offset++;
            }
            else if (instruction.Calls(guard) || op == OpCodes.Ldc_I4_1)
                values.Push(true);
            else if (op == OpCodes.Ldc_I4_0)
                values.Push(false);
            else if (op == OpCodes.Stloc_0 && values.TryPop(out bool stored))
                local = stored;
            else if (op == OpCodes.Ldloc_0 && local is { } value)
                values.Push(value);
            else if (instruction.Branches(out Label? target) && target is { } label)
            {
                bool jump;
                if (op == OpCodes.Br || op == OpCodes.Br_S)
                    jump = true;
                else if ((op == OpCodes.Brtrue || op == OpCodes.Brtrue_S) && values.TryPop(out bool whenTrue))
                    jump = whenTrue;
                else if ((op == OpCodes.Brfalse || op == OpCodes.Brfalse_S) && values.TryPop(out bool whenFalse))
                    jump = !whenFalse;
                else
                    return false;
                if (jump)
                {
                    int next = body.FindIndex(i => i.labels.Contains(label));
                    if (next < 0)
                        return false;
                    offset = next - 1;
                }
            }
            else
                return false;
        }
        return false;
    }
}
