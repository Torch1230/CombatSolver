using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;

namespace CombatSolver.Beam;

internal static class DeckVersionAccessAnalyzer
{
    private static readonly OpCode[] SingleByteOpCodes = new OpCode[256];
    private static readonly OpCode[] MultiByteOpCodes = new OpCode[256];
    private static readonly MethodInfo? DeckVersionGetter = typeof(CardModel)
        .GetProperty(nameof(CardModel.DeckVersion), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?.GetGetMethod(true);
    private static readonly FieldInfo? DeckVersionField = typeof(CardModel)
        .GetField("_deckVersion", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly ConcurrentDictionary<Type, bool> TypeCache = new();
    private static readonly HashSet<Type> BaseStopTypes =
    [
        typeof(object),
        typeof(AbstractModel),
        typeof(CardModel),
        typeof(EnchantmentModel),
        typeof(RelicModel),
        typeof(PowerModel),
        typeof(MonsterModel),
        typeof(PotionModel),
    ];

    static DeckVersionAccessAnalyzer()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
                continue;
            ushort value = unchecked((ushort)opCode.Value);
            if (value < 0x100)
                SingleByteOpCodes[value] = opCode;
            else if ((value & 0xFF00) == 0xFE00)
                MultiByteOpCodes[value & 0xFF] = opCode;
        }
    }

    public static bool RequiresDeckVersionIdentity(CombatState state, CardModel card)
    {
        if (card.DeckVersion is null)
            return false;
        if (CardOrEnchantmentReferencesDeckVersion(card))
            return true;
        return StateRequiresDeckVersionIdentity(state);
    }

    public static bool CardRequiresDeckVersionIdentity(CardModel card, bool stateRequiresDeckVersion)
    {
        if (card.DeckVersion is null)
            return false;
        return stateRequiresDeckVersion || CardOrEnchantmentReferencesDeckVersion(card);
    }

    public static bool CardOrEnchantmentReferencesDeckVersion(CardModel card)
    {
        if (TypeReferencesDeckVersion(card.GetType()))
            return true;

        IReadOnlyList<EnchantmentModel> enchantments = card.Enchantments;
        for (int i = 0; i < enchantments.Count; i++)
        {
            if (TypeReferencesDeckVersion(enchantments[i].GetType()))
                return true;
        }

        return false;
    }

    public static bool StateRequiresDeckVersionIdentity(CombatState state)
    {
        IReadOnlyList<Player> players = state.Players;
        for (int p = 0; p < players.Count; p++)
        {
            Player player = players[p];
            IReadOnlyList<RelicModel> relics = player.Relics;
            for (int r = 0; r < relics.Count; r++)
            {
                if (TypeReferencesDeckVersion(relics[r].GetType()))
                    return true;
            }

            IReadOnlyList<PowerModel> powers = player.Creature.Powers;
            for (int pw = 0; pw < powers.Count; pw++)
            {
                if (TypeReferencesDeckVersion(powers[pw].GetType()))
                    return true;
            }
        }

        IReadOnlyList<Creature> enemies = state.Enemies;
        for (int e = 0; e < enemies.Count; e++)
        {
            Creature enemy = enemies[e];
            if (enemy.Monster is { } monster && TypeReferencesDeckVersion(monster.GetType()))
                return true;
            IReadOnlyList<PowerModel> powers = enemy.Powers;
            for (int pw = 0; pw < powers.Count; pw++)
            {
                if (TypeReferencesDeckVersion(powers[pw].GetType()))
                    return true;
            }
        }

        return false;
    }

    public static bool TypeReferencesDeckVersion(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return TypeCache.GetOrAdd(type, static targetType => ComputeTypeReferencesDeckVersion(targetType));
    }

    private static bool ComputeTypeReferencesDeckVersion(Type targetType)
    {
        var visitedTypes = new HashSet<Type>();
        for (Type? current = targetType; current is not null && !BaseStopTypes.Contains(current); current = current.BaseType)
        {
            if (ScanTypeAndNested(current, visitedTypes))
                return true;
        }

        return false;
    }

    private static bool ScanTypeAndNested(Type type, HashSet<Type> visitedTypes)
    {
        if (!visitedTypes.Add(type))
            return false;

        const BindingFlags MemberFlags =
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (MethodBase method in type.GetMethods(MemberFlags))
        {
            if (MethodReferencesDeckVersion(method))
                return true;
        }

        foreach (ConstructorInfo ctor in type.GetConstructors(MemberFlags))
        {
            if (MethodReferencesDeckVersion(ctor))
                return true;
        }

        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (ScanTypeAndNested(nested, visitedTypes))
                return true;
        }

        return false;
    }

    private static bool MethodReferencesDeckVersion(MethodBase method)
    {
        MethodBody? body = method.GetMethodBody();
        byte[]? il = body?.GetILAsByteArray();
        if (il is null || il.Length == 0)
            return false;

        Type[]? typeGenericArgs = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : null;
        Type[]? methodGenericArgs = method is MethodInfo { IsGenericMethod: true } genericMethod
            ? genericMethod.GetGenericArguments()
            : null;

        int offset = 0;
        while (offset < il.Length)
        {
            byte instruction = il[offset++];
            OpCode opCode;
            if (instruction == 0xFE)
            {
                if (offset >= il.Length)
                    return true;
                opCode = MultiByteOpCodes[il[offset++]];
            }
            else
            {
                opCode = SingleByteOpCodes[instruction];
            }

            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    offset += 1;
                    break;
                case OperandType.InlineVar:
                    offset += 2;
                    break;
                case OperandType.InlineI:
                case OperandType.ShortInlineR:
                case OperandType.InlineBrTarget:
                case OperandType.InlineString:
                case OperandType.InlineSig:
                case OperandType.InlineType:
                    offset += 4;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    offset += 8;
                    break;
                case OperandType.InlineSwitch:
                    if (offset + 4 > il.Length)
                        return true;
                    int count = BitConverter.ToInt32(il, offset);
                    offset += 4 + checked(count * 4);
                    break;
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineTok:
                    if (offset + 4 > il.Length)
                        return true;
                    int token = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    if (TokenMatchesDeckVersion(method.Module, token, typeGenericArgs, methodGenericArgs))
                        return true;
                    break;
                default:
                    return true;
            }
        }

        return false;
    }

    private static bool TokenMatchesDeckVersion(
        Module module,
        int token,
        Type[]? typeGenericArgs,
        Type[]? methodGenericArgs)
    {
        MemberInfo? member;
        try
        {
            member = module.ResolveMember(token, typeGenericArgs, methodGenericArgs);
        }
        catch (Exception)
        {
            // fail-open: 无法解析元数据令牌时保守判定为访问 DeckVersion，禁止错误剪枝。
            return true;
        }

        if (member is null)
            return false;
        if (ReferenceEquals(member, DeckVersionGetter) || ReferenceEquals(member, DeckVersionField))
            return true;

        return (member is MethodInfo m && m.Name == "get_DeckVersion" && typeof(CardModel).IsAssignableFrom(m.DeclaringType))
            || (member is FieldInfo f && f.Name == "_deckVersion" && f.DeclaringType == typeof(CardModel));
    }
}
