namespace CombatSolver.Api;

internal readonly record struct PreCombatExtraFieldsState(
    bool StartedWithNeow,
    bool FreedRepy,
    int TestSubjectKills,
    bool UsedDebugCommand,
    int WongoPoints,
    bool IsPlayingRotatingCharacter);

internal static partial class PreCombatSts2SimEligibility
{
    internal static bool HasUnsupportedExtraFields(in PreCombatExtraFieldsState extraFields) =>
        !extraFields.StartedWithNeow
        || extraFields.FreedRepy
        || extraFields.UsedDebugCommand
        || extraFields.WongoPoints != 0
        || extraFields.IsPlayingRotatingCharacter;
}
