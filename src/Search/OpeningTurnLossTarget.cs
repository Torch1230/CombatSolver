namespace CombatSolver;

internal static class OpeningTurnLossTarget
{
    internal const int MinimumOpeningLoss = 8;
    internal const int AllowedLaterLoss = 2;

    internal static bool IsReached(
        int? exhaustiveOpeningLoss,
        int firstTurnLoss,
        int battleLoss,
        int recoveredHp)
        => exhaustiveOpeningLoss is >= MinimumOpeningLoss
            && firstTurnLoss == exhaustiveOpeningLoss.Value
            && battleLoss <= exhaustiveOpeningLoss.Value + AllowedLaterLoss
            && recoveredHp == 0;
}
