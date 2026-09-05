using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using STS2RitsuLib.Utils.HarmonyIl;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

using CardOnPlayAction = Action<CardModel, CardOnPlayMirrorContext>;

/// <summary>
/// Infers simple, directly invoked vanilla command templates from an unregistered <see cref="CardModel.OnPlay" />.
/// </summary>
internal static class CardOnPlayInferrer
{
    public static CardOnPlayAction? Infer(Type runtimeType, MethodInfo overrideMethod)
    {
        HarmonyIlMethodBody body;
        try
        {
            body = overrideMethod.GetOriginalIl();
        }
        catch (Exception ex)
        {
            EngineDiagnostics.Warn(
                $"Could not inspect original OnPlay IL for inferred card mirror {runtimeType.FullName}: {ex}");
            return null;
        }

        HashSet<EffectKind> effects = [];
        List<CardOnPlayAction> actions = [];

        for (var i = 0; i < body.Instructions.Count; i++)
        {
            if (!HarmonyIl.TryGetCalledMethod(body.Instructions[i], out var calledMethod))
            {
                continue;
            }

            if (IsAttackExecution(calledMethod))
            {
                if (effects.Add(EffectKind.Attack))
                {
                    actions.Add(GeneralCardMirrors.GeneralAttackOnPlay);
                }
            }
            else if (IsBlockGain(calledMethod))
            {
                if (effects.Add(EffectKind.Block))
                {
                    actions.Add(GeneralCardMirrors.GeneralBlockOnPlay);
                }
            }
            else if (TryInferOwnerDraw(body.Instructions, i, calledMethod, out var mirror, out _))
            {
                if (effects.Add(EffectKind.OwnerDraw))
                {
                    actions.Add(mirror);
                }
            }
        }

        if (actions.Count == 0)
        {
            return null;
        }

        return (card, context) => ExecuteInferredActions(actions, card, context);
    }

    internal static void ExecuteInferredActions(
        IReadOnlyList<CardOnPlayAction> actions,
        CardModel card,
        CardOnPlayMirrorContext context)
    {
        foreach (var action in actions)
        {
            action(card, context);
            if (context.Simulator.HasPendingChoice)
                return;
        }
    }

    /// <summary>
    /// Returns a handler only when every reachable gameplay operation in the override belongs to
    /// the supported straight-line recipe. Unknown commands, model writes and gameplay branches reject the recipe.
    /// </summary>
    public static CardOnPlayAction? InferStrict(Type runtimeType, MethodInfo overrideMethod)
    {
        HarmonyIlMethodBody body;
        try
        {
            body = overrideMethod.GetOriginalIl();
        }
        catch
        {
            return null;
        }

        if (HasUnsupportedModelWrite(body.Instructions, overrideMethod))
        {
            return null;
        }

        List<CardEffectKind> effects = [];
        for (int index = 0; index < body.Instructions.Count; index++)
        {
            if (!HarmonyIl.TryGetCalledMethod(body.Instructions[index], out MethodInfo? calledMethod))
                continue;

            CardEffectKind? effect = null;
            if (IsAttackExecution(calledMethod))
                effect = CardEffectKind.Attack;
            else if (IsBlockGain(calledMethod))
                effect = CardEffectKind.Block;
            else if (TryInferOwnerDraw(
                         body.Instructions,
                         index,
                         calledMethod,
                         out _,
                         out CardEffectKind drawEffect))
            {
                effect = drawEffect;
            }

            if (effect is { } value)
            {
                if (effects.Contains(value) || IsEffectConditionallyExecuted(body.Instructions, index))
                    return null;
                effects.Add(value);
                continue;
            }

            if (!IsStrictSupportingCall(calledMethod))
                return null;
        }

        if (effects.Count == 0)
            return null;
        CardEffectRecipe recipe = new(effects);
        return recipe.Execute;
    }

    private static bool TryInferOwnerDraw(
        IReadOnlyList<CodeInstruction> instructions,
        int callIndex,
        MethodInfo method,
        [NotNullWhen(true)] out CardOnPlayAction? action,
        out CardEffectKind effect)
    {
        action = null;
        effect = default;
        if (method.DeclaringType != typeof(CardPileCmd) || method.Name != nameof(CardPileCmd.Draw) ||
            IsConditionallyGuarded(instructions, callIndex))
        {
            return false;
        }

        var parameters = method.GetParameters();
        // Typical two-argument form (receiver/context loads omitted):
        //   ... load card
        //   callvirt CardModel.get_Owner  // callIndex - 1
        //   call CardPileCmd.Draw         // callIndex
        // The overload itself fixes the count at one, so only the player-producing call needs a positional check.
        if (parameters.Length == 2 &&
            parameters[0].ParameterType == typeof(PlayerChoiceContext) &&
            IsCardModelOwnerGetter(instructions, callIndex - 1))
        {
            action = GeneralCardMirrors.GeneralOwnerDrawOneOnPlay;
            effect = CardEffectKind.OwnerDrawOne;
            return true;
        }

        // Typical four-argument tail after choiceContext and count have been pushed:
        //   ... count recipe ends here     // callIndex - 4
        //   load card                      // callIndex - 3
        //   callvirt CardModel.get_Owner   // callIndex - 2
        //   ldc.i4.0                       // callIndex - 1: fromHandDraw = false
        //   call CardPileCmd.Draw          // callIndex
        // The card load can vary, so the matcher anchors on the two stable instructions immediately before Draw.
        if (parameters.Length != 4 ||
            parameters[0].ParameterType != typeof(PlayerChoiceContext) ||
            parameters[1].ParameterType != typeof(decimal) ||
            !IsCardModelOwnerGetter(instructions, callIndex - 2) ||
            !LoadsFalse(instructions, callIndex - 1))
        {
            return false;
        }

        // With the stable four-argument tail above, the instruction at -4 is also the end of every supported count
        // recipe: decimal.One, Cards.BaseValue, int-to-decimal Cards.IntValue, or the stored async variant.
        if (LoadsDecimalOne(instructions, callIndex - 4))
        {
            action = GeneralCardMirrors.GeneralOwnerDrawOneOnPlay;
            effect = CardEffectKind.OwnerDrawOne;
            return true;
        }

        if (LoadsCardsValue(instructions, callIndex - 4) ||
            LoadsStoredCardsValue(instructions, callIndex - 4))
        {
            action = GeneralCardMirrors.GeneralOwnerDrawOnPlay;
            effect = CardEffectKind.OwnerDrawCards;
            return true;
        }

        return false;
    }

    private static bool HasUnsupportedModelWrite(
        IReadOnlyList<CodeInstruction> instructions,
        MethodInfo overrideMethod)
    {
        Type? stateMachine = overrideMethod.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode != OpCodes.Stfld && instruction.opcode != OpCodes.Stsfld)
                continue;
            if (instruction.operand is not FieldInfo field || field.DeclaringType == stateMachine)
                continue;
            if (field.DeclaringType?.Namespace?.StartsWith("MegaCrit.Sts2.Core", StringComparison.Ordinal) == true)
                return true;
        }
        return false;
    }

    private static bool IsEffectConditionallyExecuted(
        IReadOnlyList<CodeInstruction> instructions,
        int effectIndex)
    {
        Dictionary<Label, int> labelIndices = [];
        for (int index = 0; index < instructions.Count; index++)
        {
            foreach (Label label in instructions[index].labels)
                labelIndices[label] = index;
        }
        for (int index = 0; index < instructions.Count; index++)
        {
            CodeInstruction instruction = instructions[index];
            if (instruction.opcode.FlowControl != FlowControl.Cond_Branch)
                continue;
            if (index < 14)
                continue;
            if (index > 0
                && HarmonyIl.TryGetCalledMethod(instructions[index - 1], out MethodInfo? previous)
                && previous.Name == "get_IsCompleted")
            {
                continue;
            }
            IEnumerable<Label> targets = instruction.operand switch
            {
                Label label => [label],
                Label[] labels => labels,
                _ => [],
            };
            foreach (Label target in targets)
            {
                if (!labelIndices.TryGetValue(target, out int targetIndex))
                    continue;
                int start = Math.Min(index, targetIndex);
                int end = Math.Max(index, targetIndex);
                if (effectIndex > start && effectIndex < end)
                    return true;
            }
        }
        return false;
    }

    private static bool IsStrictSupportingCall(MethodInfo method)
    {
        Type? declaringType = method.DeclaringType;
        string? ns = declaringType?.Namespace;
        if (ns?.StartsWith("MegaCrit.Sts2.Core", StringComparison.Ordinal) != true)
            return true;
        if (method.IsSpecialName && method.Name.StartsWith("get_", StringComparison.Ordinal))
            return true;
        if (declaringType == typeof(DamageCmd))
            return method.Name == nameof(DamageCmd.Attack);
        if (declaringType == typeof(CreatureCmd))
            return method.Name == nameof(CreatureCmd.TriggerAnim);
        if (declaringType == typeof(Cmd))
            return method.Name == nameof(Cmd.Wait);
        if (declaringType == typeof(VfxCmd) || declaringType == typeof(SfxCmd))
            return true;
        if (declaringType == typeof(CardCmd))
            return method.Name == nameof(CardCmd.PreviewCardPileAdd);
        if (declaringType == typeof(ForgeCmd))
            return method.Name == nameof(ForgeCmd.PlayCombatRoomForgeVfx);
        if (declaringType == typeof(AttackCommand))
            return method.Name != nameof(AttackCommand.Execute);
        if (declaringType?.Name.EndsWith("Vfx", StringComparison.Ordinal) == true
            || declaringType?.Name.EndsWith("VFX", StringComparison.Ordinal) == true)
        {
            return true;
        }
        if (declaringType?.Name is "NCombatRoom" && method.Name == "GetCreatureNode")
            return true;
        if (declaringType?.Name is "NCreature" && method.Name == "GetBottomOfHitbox")
            return true;
        if (ns?.Contains(".Models.Characters", StringComparison.Ordinal) == true
            && (method.Name.Contains("Anim", StringComparison.Ordinal)
                || method.Name.Contains("Delay", StringComparison.Ordinal)))
        {
            return true;
        }
        if (typeof(DynamicVar).IsAssignableFrom(declaringType)
            || declaringType == typeof(DynamicVarSet))
        {
            return true;
        }
        if (declaringType == typeof(CardModel)
            && method.Name is nameof(CardModel.ResolveEnergyXValue) or nameof(CardModel.ResolveStarXValue))
        {
            return true;
        }
        return false;
    }

    private static bool IsConditionallyGuarded(IReadOnlyList<CodeInstruction> instructions, int callIndex)
    {
        // A directly guarded draw usually starts its argument preparation like this:
        //   ... load condition
        //   brfalse label                   // contextLoadIndex - 2
        //   ldarg.0                         // contextLoadIndex - 1: async state machine
        //   ldfld PlayerChoiceContext       // contextLoadIndex
        //   ... remaining Draw arguments
        //   call CardPileCmd.Draw           // callIndex
        // We search backward from Draw for the context field because the count/player recipes have different lengths.
        var contextLoadIndex = -1;
        for (var i = callIndex - 1; i >= Math.Max(0, callIndex - 16); i--)
        {
            if (instructions[i].operand is FieldInfo { FieldType: var fieldType } &&
                fieldType == typeof(PlayerChoiceContext))
            {
                contextLoadIndex = i;
                break;
            }
        }

        var branchIndex = contextLoadIndex - 2;
        if (branchIndex < 0 || instructions[branchIndex].opcode.FlowControl != FlowControl.Cond_Branch)
        {
            return false;
        }

        // The initial async state dispatch also branches around the first await. It is compiler control flow,
        // not a gameplay condition, and always reads the state stored in local 0 near the start of MoveNext.
        // Typical prefix:
        //   ldloc.0
        //   brfalse ...                     // branchIndex
        // Treat this early local-0 branch as the state-machine switch rather than a conditional card effect.
        if (branchIndex < 14)
        {
            for (var i = Math.Max(0, branchIndex - 2); i < branchIndex; i++)
            {
                if (HarmonyIl.TryGetLocalLoadIndex(instructions[i], out var localIndex) && localIndex == 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsCardModelOwnerGetter(IReadOnlyList<CodeInstruction> instructions, int index)
    {
        return index >= 0 &&
            HarmonyIl.TryGetCalledMethod(instructions[index], out var method) &&
            method.DeclaringType == typeof(CardModel) &&
            method.Name == $"get_{nameof(CardModel.Owner)}";
    }

    private static bool LoadsFalse(IReadOnlyList<CodeInstruction> instructions, int index)
    {
        return index >= 0 && HarmonyIl.LoadsInt32(instructions[index], 0);
    }

    private static bool LoadsDecimalOne(IReadOnlyList<CodeInstruction> instructions, int index)
    {
        return index >= 0 && instructions[index].opcode == OpCodes.Ldsfld &&
            instructions[index].operand is FieldInfo { DeclaringType: var declaringType, Name: nameof(decimal.One) } &&
            declaringType == typeof(decimal);
    }

    private static bool LoadsCardsValue(IReadOnlyList<CodeInstruction> instructions, int valueEndIndex)
    {
        // Direct decimal recipe:
        //   call DynamicVarSet.get_Cards    // valueEndIndex - 1
        //   call DynamicVar.get_BaseValue   // valueEndIndex
        if (IsDynamicVarGetter(instructions, valueEndIndex, nameof(DynamicVar.BaseValue)))
        {
            return IsCardsGetter(instructions, valueEndIndex - 1);
        }

        // Direct integer recipe, converted to CardPileCmd.Draw's decimal count:
        //   call DynamicVarSet.get_Cards    // valueEndIndex - 2
        //   call DynamicVar.get_IntValue    // valueEndIndex - 1
        //   call decimal.op_Implicit(int)   // valueEndIndex
        return IsDecimalFromInt(instructions, valueEndIndex) &&
            IsDynamicVarGetter(instructions, valueEndIndex - 1, nameof(DynamicVar.IntValue)) &&
            IsCardsGetter(instructions, valueEndIndex - 2);
    }

    private static bool LoadsStoredCardsValue(IReadOnlyList<CodeInstruction> instructions, int valueEndIndex)
    {
        // Some async methods, such as Prepared.OnPlay, preserve the count in a generated state-machine field:
        //   call DynamicVarSet.get_Cards
        //   call DynamicVar.get_IntValue
        //   stfld int32 <...>count          // searched assignment
        //   ...
        //   ldfld int32 <...>count          // valueEndIndex - 1
        //   call decimal.op_Implicit(int)   // valueEndIndex
        // Match the same FieldInfo at both ends, then verify that the nearby assignment came from Cards.IntValue.
        if (!IsDecimalFromInt(instructions, valueEndIndex) || valueEndIndex < 1 ||
            instructions[valueEndIndex - 1].operand is not FieldInfo countField)
        {
            return false;
        }

        for (var i = valueEndIndex - 2; i >= Math.Max(2, valueEndIndex - 32); i--)
        {
            if (instructions[i].opcode == OpCodes.Stfld && Equals(instructions[i].operand, countField))
            {
                return IsDynamicVarGetter(instructions, i - 1, nameof(DynamicVar.IntValue)) &&
                    IsCardsGetter(instructions, i - 2);
            }
        }

        return false;
    }

    private static bool IsCardsGetter(IReadOnlyList<CodeInstruction> instructions, int index)
    {
        return index >= 0 &&
            HarmonyIl.TryGetCalledMethod(instructions[index], out var method) &&
            method.DeclaringType == typeof(DynamicVarSet) &&
            method.Name == $"get_{nameof(DynamicVarSet.Cards)}";
    }

    private static bool IsDynamicVarGetter(
        IReadOnlyList<CodeInstruction> instructions,
        int index,
        string propertyName)
    {
        return index >= 0 &&
            HarmonyIl.TryGetCalledMethod(instructions[index], out var method) &&
            typeof(DynamicVar).IsAssignableFrom(method.DeclaringType) &&
            method.Name == $"get_{propertyName}";
    }

    private static bool IsDecimalFromInt(IReadOnlyList<CodeInstruction> instructions, int index)
    {
        return index >= 0 &&
            HarmonyIl.TryGetCalledMethod(instructions[index], out var method) &&
            method.DeclaringType == typeof(decimal) &&
            method.Name == "op_Implicit" &&
            method.GetParameters() is [var param] &&
            param.ParameterType == typeof(int);
    }

    private static bool IsAttackExecution(MethodInfo method)
    {
        return method.DeclaringType == typeof(AttackCommand) && method.Name == nameof(AttackCommand.Execute);
    }

    private static bool IsBlockGain(MethodInfo method)
    {
        return method.DeclaringType == typeof(CreatureCmd) && method.Name == nameof(CreatureCmd.GainBlock);
    }

    private enum EffectKind
    {
        Attack,
        Block,
        OwnerDraw
    }
}
