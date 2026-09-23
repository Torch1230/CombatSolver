using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// 读取生产 DLL 的常量，避免最小替身枚举掩盖变基后的监听位碰撞。
static class HookMaskChecks
{
    public static void Run(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        MetadataReader metadata = pe.GetMetadataReader();
        TypeDefinition type = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
            .Single(t => metadata.GetString(t.Name) == "MirroredHookMask");
        var bits = new Dictionary<ulong, string>();
        var names = new HashSet<string>();
        foreach (FieldDefinitionHandle handle in type.GetFields())
        {
            FieldDefinition field = metadata.GetFieldDefinition(handle);
            string name = metadata.GetString(field.Name);
            if (field.GetDefaultValue().IsNil || name is "None" or "All") continue;
            Constant constant = metadata.GetConstant(field.GetDefaultValue());
            ulong bit = metadata.GetBlobReader(constant.Value).ReadUInt64();
            if (bit == 0 || (bit & (bit - 1)) != 0 || !bits.TryAdd(bit, name))
                throw new InvalidDataException("无效或重复的监听位：" + name);
            names.Add(name);
        }
        if (!names.Contains("BeforeSideTurnStart") || !names.Contains("AfterEnergyReset")
            || !names.Contains("AfterPlayerTurnStartEarly") || !names.Contains("AfterPlayerTurnStart") || !names.Contains("AfterPlayerTurnStartLate"))
            throw new InvalidDataException("生产 DLL 缺少待核对的回合监听位。");
        Console.WriteLine($"TURN_PHASE_MASK_OK distinct_bits={bits.Count}");
    }
}
