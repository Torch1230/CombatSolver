namespace CombatSolver;

internal static class SilentPowerCardValuationModels
{
    internal static IReadOnlyList<IPowerCardValuationModel> All { get; } =
    [
        new AbrasivePowerCardValuationModel(),
        new AccelerantPowerCardValuationModel(),
        new AccuracyPowerCardValuationModel(),
        new AfterimagePowerCardValuationModel(),
        new EnvenomPowerCardValuationModel(),
        new FanOfKnivesPowerCardValuationModel(),
        new FootworkPowerCardValuationModel(),
        new InfiniteBladesPowerCardValuationModel(),
        new MasterPlannerPowerCardValuationModel(),
        new NoxiousFumesPowerCardValuationModel(),
        new PhantomBladesPowerCardValuationModel(),
        new SerpentFormPowerCardValuationModel(),
        new SpeedsterPowerCardValuationModel(),
        new ToolsOfTheTradePowerCardValuationModel(),
        new TrackingPowerCardValuationModel(),
        new WellLaidPlansPowerCardValuationModel(),
        new WraithFormPowerCardValuationModel(),
    ];
}
