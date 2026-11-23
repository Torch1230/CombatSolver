// B1 contract for Torch1230/CombatSolver src/Search/PowerCardValuation/Commitments/PowerCommitmentSeatPolicy.cs @ d231e9e5; full port in B2.
namespace CombatSolver;

internal static class PowerCommitmentSeatPolicy
{
    internal static int SeatQuota(int beamWidth, bool aggressive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(beamWidth);
        int ordinaryFloor = (int)(((long)beamWidth + 1L) / 2L);
        int maximumPowerSeats = beamWidth - ordinaryFloor;
        int requested = aggressive
            ? (int)Math.Min(beamWidth / 2, Math.Max(4L, ((long)beamWidth + 2L) / 3L))
            : Math.Clamp(beamWidth / 12, 2, 12);
        return Math.Min(maximumPowerSeats, requested);
    }
}
