using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using STS2RitsuLib.Cards.DynamicVars;
using STS2RitsuLib.Utils;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertDynamicVarMetadata()
    {
        Type extensions = AccessTools.TypeByName("BaseLib.Extensions.DynamicVarExtensions")
            ?? throw new InvalidOperationException("This contract requires the player's BaseLib dependency.");
        ConditionalWeakTable<DynamicVar, object?> Table(string name)
        {
            object field = extensions.GetField(name)!.GetValue(null)!;
            return (ConditionalWeakTable<DynamicVar, object?>)field.GetType()
                .GetField("_table", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(field)!;
        }
        var tips = Table("DynamicVarTips");
        var upgrades = Table("DynamicVarUpgrades");
        var ritsu = (AttachedState<DynamicVar, Func<DynamicVar, IHoverTip>?>)typeof(DynamicVarTooltipRegistry)
            .GetField("TooltipFactories", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        int Entries(DynamicVar value) => (tips.TryGetValue(value, out _) ? 1 : 0)
            + (upgrades.TryGetValue(value, out _) ? 1 : 0) + (ritsu.ContainsKey(value) ? 1 : 0);

        DynamicVar original = new("MetadataContract", 17);
        DynamicVar liveClone = original.Clone();
        if (Entries(original) != 3 || !tips.TryGetValue(liveClone, out _) || !upgrades.TryGetValue(liveClone, out _))
            throw new InvalidOperationException("Original live clone empty-entry baseline changed.");
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        using (SimulationNotificationIsolation.Enter())
        {
            DynamicVar source = new("SparseMetadata", 17);
            for (int i = 0; i < 2000; i++)
            {
                DynamicVar clone = source.Clone();
                if (Entries(source) != 0 || Entries(clone) != 0 || clone.BaseValue != 17 || clone.Name != source.Name)
                    throw new InvalidOperationException("Real DynamicVar.Clone created empty metadata or changed values.");
                source = clone;
            }
            Func<DynamicVar, IHoverTip> factory = _ => null!;
            tips.AddOrUpdate(source, factory);
            upgrades.AddOrUpdate(source, (decimal)3.5);
            ritsu.Set(source, factory);
            DynamicVar child = source.Clone();
            DynamicVar grandchild = child.Clone();
            foreach (DynamicVar value in new[] { child, grandchild })
                if (!tips.TryGetValue(value, out object? tip) || !ReferenceEquals(tip, factory)
                    || !upgrades.TryGetValue(value, out object? upgrade) || !Equals(upgrade, (decimal)3.5)
                    || !ritsu.TryGetValue(value, out var copied) || !ReferenceEquals(copied, factory))
                    throw new InvalidOperationException("Custom tooltip or upgrade metadata was lost across clones.");
            upgrades.AddOrUpdate(child, (decimal)9);
            if (!upgrades.TryGetValue(source, out object? parentUpgrade) || !Equals(parentUpgrade, (decimal)3.5))
                throw new InvalidOperationException("Child upgrade changed the parent metadata.");
        }
        _completedChecks.Add($"DynamicVarMetadata:LiveBaseline=3SourceEntries:2000SparseClones=0Entries:CustomTooltipAndUpgrade:ForkIndependent:Allocated={GC.GetAllocatedBytesForCurrentThread() - allocated}");
    }
}
