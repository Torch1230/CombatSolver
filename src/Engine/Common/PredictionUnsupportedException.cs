using MegaCrit.Sts2.Core.Modding;

namespace CombatSolver.Engine.Common;

internal sealed class PredictionUnsupportedException(string message) : NotSupportedException(message)
{
    internal static NotSupportedException ForContent(string message, params Type[] sourceTypes)
    {
        foreach (Type type in sourceTypes)
        {
            var mod = AssemblyInfo.ModForType(type, out bool isBaseGame);
            if (!isBaseGame && mod?.manifest?.id is { Length: > 0 } modId
                && !string.Equals(modId, Entry.ModId, StringComparison.OrdinalIgnoreCase))
            {
                return new IncompatibleGameplayModException(
                    modId, mod.manifest.name ?? modId, message, "combat");
            }
        }
        return new PredictionUnsupportedException(message);
    }
}
