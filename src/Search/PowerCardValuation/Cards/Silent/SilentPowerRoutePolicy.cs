namespace CombatSolver;

internal enum SilentPowerRoutePriority
{
    Low,
    Normal,
    Strong,
    Core,
    Dedicated,
}

internal readonly record struct SilentPowerRoutePolicy(
    SilentPowerRoutePriority Priority,
    int MinimumProjection = 1,
    bool AllowTriggerBackedProjectionFloor = false,
    bool PreferSlyActivation = false,
    bool RequireFreeOrSpareActivation = false,
    bool RequireImmediateDefenseGain = false,
    bool PreferDedicatedSearch = false)
{
    internal static SilentPowerRoutePolicy For(SilentPowerCardIdentity card)
        => card switch
        {
            SilentPowerCardIdentity.Abrasive => new(
                SilentPowerRoutePriority.Low,
                PreferSlyActivation: true),
            SilentPowerCardIdentity.Accelerant => new(
                SilentPowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            SilentPowerCardIdentity.Accuracy => new(
                SilentPowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            SilentPowerCardIdentity.Afterimage => new(
                SilentPowerRoutePriority.Strong,
                MinimumProjection: 5),
            SilentPowerCardIdentity.Envenom => new(
                SilentPowerRoutePriority.Low,
                RequireFreeOrSpareActivation: true),
            SilentPowerCardIdentity.FanOfKnives => new(
                SilentPowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            SilentPowerCardIdentity.Footwork => new(
                SilentPowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            SilentPowerCardIdentity.InfiniteBlades => new(
                SilentPowerRoutePriority.Low,
                AllowTriggerBackedProjectionFloor: true,
                RequireFreeOrSpareActivation: true),
            SilentPowerCardIdentity.MasterPlanner => new(SilentPowerRoutePriority.Normal),
            SilentPowerCardIdentity.NoxiousFumes => new(
                SilentPowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            SilentPowerCardIdentity.PhantomBlades => new(
                SilentPowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            SilentPowerCardIdentity.SerpentForm => new(
                SilentPowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            SilentPowerCardIdentity.Speedster => new(
                SilentPowerRoutePriority.Normal,
                AllowTriggerBackedProjectionFloor: true),
            SilentPowerCardIdentity.ToolsOfTheTrade => new(
                SilentPowerRoutePriority.Core,
                AllowTriggerBackedProjectionFloor: true),
            SilentPowerCardIdentity.Tracking => new(
                SilentPowerRoutePriority.Strong,
                AllowTriggerBackedProjectionFloor: true),
            SilentPowerCardIdentity.WellLaidPlans => new(
                SilentPowerRoutePriority.Dedicated,
                AllowTriggerBackedProjectionFloor: true,
                PreferDedicatedSearch: true),
            SilentPowerCardIdentity.WraithForm => new(
                SilentPowerRoutePriority.Strong,
                RequireImmediateDefenseGain: true),
            _ => throw new ArgumentOutOfRangeException(nameof(card), card, null),
        };

    internal static SilentPowerRoutePriority HighestPriority(
        SilentPowerCardIdentity cards)
    {
        SilentPowerRoutePriority priority = SilentPowerRoutePriority.Low;
        foreach (SilentPowerCardIdentity card in Enum.GetValues<SilentPowerCardIdentity>())
        {
            if (card == SilentPowerCardIdentity.None || !cards.HasFlag(card))
                continue;
            priority = Max(priority, For(card).Priority);
        }
        return priority;
    }

    private static SilentPowerRoutePriority Max(
        SilentPowerRoutePriority left,
        SilentPowerRoutePriority right)
        => left >= right ? left : right;
}
