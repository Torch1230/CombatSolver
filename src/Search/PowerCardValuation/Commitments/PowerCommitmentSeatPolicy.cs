namespace CombatSolver;

internal static class PowerCommitmentSeatPolicy
{
    internal static int SeatQuota(int beamWidth, bool aggressive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(beamWidth);
        int ordinaryFloor = (beamWidth + 1) / 2;
        int maximumPowerSeats = beamWidth - ordinaryFloor;
        int requested = aggressive
            ? Math.Min(beamWidth / 2, Math.Max(4, (beamWidth + 2) / 3))
            : Math.Clamp(beamWidth / 12, 2, 12);
        return Math.Min(maximumPowerSeats, requested);
    }
}
