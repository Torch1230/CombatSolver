namespace HarmonyLib
{
    internal sealed class HarmonyPriorityAttribute(int priority) : Attribute
    {
        public int Priority { get; } = priority;
    }
    internal static class Priority { public const int First = 800; }
}
namespace STS2RitsuLib.Patching.Models
{
    internal interface IPatchMethod;
    internal sealed record ModPatchTarget(Type Type, string Name, Type[] Arguments);
}
namespace MegaCrit.Sts2.Core.Models
{
    internal static class ModelDb;
    internal sealed record ModelId(string Category, string Entry);
    internal sealed class Leap;
}
