namespace CombatSolver;

internal static class PowerCardValuationModels
{
    internal static PowerCardValuationRegistry Registry { get; } = new(PowerCardValuationCatalog.AllDescriptors);
}
