namespace CombatSolver;

// Reviewed value-only facts shared by run advice and branch-local combat evaluation.
internal static class CardMechanismFacts
{
    internal static int ImmediateShivSupply(string id, int cards, int shivs) => id switch
    {
        "BLADE_DANCE" or "CLOAK_AND_DAGGER" => Math.Max(0, cards),
        "FAN_OF_KNIVES" => Math.Max(0, shivs),
        _ => 0,
    };

}
