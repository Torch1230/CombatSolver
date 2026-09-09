namespace CombatSolver;

// Reviewed value-only facts shared by run advice and branch-local combat evaluation.
internal static class CardMechanismFacts
{
    internal static int AttackHits(string id, int repeat) => id switch
    {
        "TWIN_STRIKE" or "RIP_AND_TEAR" => 2,
        "SWORD_BOOMERANG" or "SOVEREIGN_BLADE" => Math.Max(0, repeat),
        _ => 1,
    };

    internal static int ImmediateShivSupply(string id, int cards, int shivs) => id switch
    {
        "BLADE_DANCE" or "CLOAK_AND_DAGGER" => Math.Max(0, cards),
        "FAN_OF_KNIVES" => Math.Max(0, shivs),
        _ => 0,
    };

    internal static int EstimatedShivPlays(int existing, int generated, int generators, int deckSize, int actions, int reusableExisting = 0)
    {
        if (actions <= 0 || deckSize <= 0) return 0;
        int cycleSize = deckSize + generated;
        int setupActions = ShivSetupActions(generators, cycleSize, actions);
        int reusable = Math.Clamp(reusableExisting, 0, existing);
        int oneShot = existing - reusable;
        int existingPlays = Math.Min(oneShot, (int)Math.Ceiling((double)oneShot * actions / cycleSize))
            + (int)Math.Ceiling((double)reusable * actions / cycleSize);
        int generatedPlays = generated == 0 ? 0
            : (int)Math.Ceiling((double)generated * actions / cycleSize);
        return Math.Min(actions - setupActions, existingPlays + generatedPlays);
    }

    internal static int EstimatedAttackHits(int ordinaryHits, int existingShivs, int generatedShivs,
        int generators, int deckSize, int actions, int reusableShivs = 0)
    {
        if (actions <= 0 || deckSize <= 0) return 0;
        int shivPlays = EstimatedShivPlays(existingShivs, generatedShivs, generators, deckSize, actions, reusableShivs);
        int setup = ShivSetupActions(generators, deckSize + generatedShivs, actions);
        int remainingActions = Math.Max(0, actions - shivPlays - setup);
        int ordinaryDeckSize = Math.Max(1, deckSize - existingShivs - generators);
        return shivPlays + (int)Math.Ceiling((double)ordinaryHits * remainingActions / ordinaryDeckSize);
    }

    private static int ShivSetupActions(int generators, int cycleSize, int actions) => generators == 0 ? 0
        : Math.Min(actions, (int)Math.Ceiling((double)generators * actions / cycleSize));
}
