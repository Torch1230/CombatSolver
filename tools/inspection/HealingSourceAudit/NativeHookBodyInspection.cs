using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using MetadataExtensions = System.Reflection.Metadata.PEReaderExtensions;

// This records only definition-local instruction shapes. No shape grants a
// runtime certificate: dispatchers, returned aliases, calls and patches remain
// separate proof obligations. In particular, a constant is not necessarily the
// identity value of the caller's operation.
internal static class NativeHookBodyInspection
{
    internal static NativeHookBodyEvidence[] Read(string dll, IEnumerable<MethodDefinition> methods)
    {
        using FileStream stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        var metadata = MetadataExtensions.GetMetadataReader(pe);
        Guid mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        return methods.OrderBy(method => method.MetadataToken.ToInt32()).Select(method =>
        {
            if (method.Module.Mvid != mvid)
                throw new InvalidDataException("Hook definition and PE body belong to different modules.");
            string? digest = method.HasBody
                ? Convert.ToHexString(SHA256.HashData(MetadataExtensions.GetMethodBody(pe, method.RVA).GetILContent().AsSpan()))
                    .ToLowerInvariant() : null;
            return new NativeHookBodyEvidence(method.FullName, method.MetadataToken.ToInt32(), mvid,
                method.IsVirtual, method.HasThis, method.HasBody, method.IsAbstract,
                method.ReturnType.FullName, method.Parameters.Select(parameter => parameter.ParameterType.FullName).ToArray(),
                digest, method.HasBody ? method.Body.ExceptionHandlers.Count : 0,
                method.HasBody ? method.Body.Variables.Count : 0, Classify(method),
                method.HasBody ? method.Body.Instructions.Select(instruction => new NativeHookInstruction(
                    instruction.Offset, instruction.OpCode.Name, OperandText(instruction.Operand),
                    instruction.Operand is MethodReference callee ? callee.MetadataToken.ToInt32() : null,
                    instruction.Operand is MethodReference dependency
                        ? dependency.DeclaringType.Scope?.ToString() : null)).ToArray() : []);
        }).ToArray();
    }

    internal static string Classify(MethodDefinition method)
    {
        if (!method.HasBody || method.Body.ExceptionHandlers.Count != 0 || method.Body.Variables.Count != 0)
            return "unclassified";
        Instruction[] code = method.Body.Instructions.ToArray();
        if (code.Length == 1 && code[0].OpCode.Code == Code.Ret
            && method.ReturnType.MetadataType == MetadataType.Void)
            return "empty-void";
        if (code.Length != 2 || code[1].OpCode.Code != Code.Ret)
            return "unclassified";
        int? ilArgument = code[0].OpCode.Code switch
        {
            Code.Ldarg_0 => 0, Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3,
            Code.Ldarg or Code.Ldarg_S when code[0].Operand is ParameterDefinition parameter
                => parameter.Index + (method.HasThis ? 1 : 0),
            _ => null,
        };
        if (ilArgument is { } argument)
        {
            int parameter = argument - (method.HasThis ? 1 : 0);
            // Returning this, a byref, or a different-scope type needs a separate
            // alias/type proof; do not classify it from a display signature.
            return parameter >= 0 && parameter < method.Parameters.Count
                && method.ReturnType is not ByReferenceType
                && SameType(method.ReturnType, method.Parameters[parameter].ParameterType)
                    ? "return-argument" : "unclassified";
        }
        if (code[0].OpCode.Code is Code.Ldc_I4 or Code.Ldc_I4_S
                or Code.Ldc_I4_M1 or Code.Ldc_I4_0 or Code.Ldc_I4_1 or Code.Ldc_I4_2
                or Code.Ldc_I4_3 or Code.Ldc_I4_4 or Code.Ldc_I4_5 or Code.Ldc_I4_6
                or Code.Ldc_I4_7 or Code.Ldc_I4_8
            && method.ReturnType.MetadataType is MetadataType.Boolean or MetadataType.Int32)
            return "return-int32-constant";
        if (code[0].OpCode.Code == Code.Ldnull && method.ReturnType.MetadataType
            is MetadataType.Class or MetadataType.Object or MetadataType.String or MetadataType.Array)
            return "return-null";
        if (code[0].OpCode.Code == Code.Call && code[0].Operand is MethodReference callee
            && !callee.HasThis && !callee.HasGenericParameters && callee.Parameters.Count == 0
            && method.ReturnType.MetadataType != MetadataType.Void
            && SameType(callee.ReturnType, method.ReturnType))
            // The call is intentionally unresolved. Even CompletedTask's getter
            // requires its own binding/patch proof at the runtime boundary.
            return "return-parameterless-static-call";
        return "unclassified";
    }

    private static bool SameType(TypeReference left, TypeReference right)
        => !ContainsGenericParameter(left) && !ContainsGenericParameter(right)
            && TypeIdentity(left) == TypeIdentity(right);

    private static bool ContainsGenericParameter(TypeReference type)
        => type switch
        {
            GenericParameter => true,
            GenericInstanceType generic => generic.GenericArguments.Any(ContainsGenericParameter),
            RequiredModifierType required => ContainsGenericParameter(required.ModifierType)
                || ContainsGenericParameter(required.ElementType),
            OptionalModifierType optional => ContainsGenericParameter(optional.ModifierType)
                || ContainsGenericParameter(optional.ElementType),
            FunctionPointerType pointer => ContainsGenericParameter(pointer.ReturnType)
                || pointer.Parameters.Any(parameter => ContainsGenericParameter(parameter.ParameterType)),
            TypeSpecification specification => ContainsGenericParameter(specification.ElementType),
            _ => false,
        };

    private static string TypeIdentity(TypeReference type)
        => type switch
        {
            GenericParameter => throw new InvalidOperationException("Unresolved generic parameter in body shape."),
            GenericInstanceType generic => TypeIdentity(generic.ElementType) + "<"
                + string.Join(",", generic.GenericArguments.Select(TypeIdentity)) + ">",
            RequiredModifierType required => "modreq:" + TypeIdentity(required.ModifierType)
                + ":" + TypeIdentity(required.ElementType),
            OptionalModifierType optional => "modopt:" + TypeIdentity(optional.ModifierType)
                + ":" + TypeIdentity(optional.ElementType),
            FunctionPointerType pointer => $"fn:{pointer.CallingConvention}:{pointer.HasThis}:{pointer.ExplicitThis}:"
                + TypeIdentity(pointer.ReturnType) + "("
                + string.Join(",", pointer.Parameters.Select(parameter => TypeIdentity(parameter.ParameterType))) + ")",
            TypeSpecification specification => type.FullName + "@" + TypeIdentity(specification.ElementType),
            _ => type.FullName + "@" + (type.Scope is ModuleDefinition module
                ? module.Assembly.Name.FullName : type.Scope?.ToString()),
        };

    private static string? OperandText(object? operand)
        => operand switch
        {
            null => null,
            MethodReference method => method.FullName,
            FieldReference field => field.FullName,
            TypeReference type => type.FullName,
            ParameterDefinition parameter => $"argument:{parameter.Index}",
            VariableDefinition variable => $"local:{variable.Index}",
            Instruction target => $"IL_{target.Offset:X4}",
            Instruction[] targets => string.Join(",", targets.Select(target => $"IL_{target.Offset:X4}")),
            IFormattable value => value.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => operand.ToString(),
        };
}

internal sealed record NativeHookBodyEvidence(string Method, int DefinitionToken, Guid ModuleMvid,
    bool IsVirtual, bool HasThis, bool HasBody, bool IsAbstract, string ReturnType,
    string[] ParameterTypes, string? IlSha256, int ExceptionHandlerCount, int LocalCount,
    string Shape, NativeHookInstruction[] Instructions);

internal sealed record NativeHookInstruction(int Offset, string OpCode, string? Operand,
    int? CallOperandToken, string? CallDeclaringScope);
