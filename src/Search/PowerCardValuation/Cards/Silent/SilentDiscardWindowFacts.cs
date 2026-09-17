namespace CombatSolver;

internal static class SilentDiscardWindowFacts
{
    internal static int Capacity(
        string cardId,
        int selectedCards,
        int handCount)
        => cardId switch
        {
            "SURVIVOR" or "ACROBATICS" or "DAGGER_THROW" => 1,
            "PREPARED" or "HIDDEN_DAGGERS" => Math.Max(0, selectedCards),
            "STORM_OF_STEEL" or "SHADOW_STEP" or "CALCULATED_GAMBLE"
                => Math.Max(0, handCount - 1),
            _ => 0,
        };
}
