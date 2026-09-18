namespace CombatSolver;

internal static class SilentCardFlowFacts
{
    internal static int DrawCount(
        string cardId,
        int cards,
        int handCount)
        => cardId switch
        {
            "DAGGER_THROW" or "ESCAPE_PLAN" => 1,
            "PREPARED" or "ADRENALINE" or "ACROBATICS" or "BACKFLIP"
                or "EXPERTISE" or "REFLEX" => Math.Max(0, cards),
            "CALCULATED_GAMBLE" => Math.Max(0, handCount - 1),
            _ => 0,
        };
}
