namespace CombatSolver;

internal static class PowerCardValuationCatalog
{
    private static readonly (PowerCardPool Pool, string CardId, PowerCommitmentFamily Family, PowerRouteAdmissionPolicy Policy)[] Entries =
    [
        // Silent (17)
        (PowerCardPool.Silent, "ABRASIVE", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Low, PreferFreeActivation: true)),
        (PowerCardPool.Silent, "ACCELERANT", PowerCommitmentFamily.PoisonEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Silent, "ACCURACY", PowerCommitmentFamily.ShivEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Silent, "AFTERIMAGE", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Strong, MinimumProjection: 5)),
        (PowerCardPool.Silent, "ENVENOM", PowerCommitmentFamily.PoisonEngine, new(PowerRoutePriority.Low, RequireFreeOrSpareActivation: true)),
        (PowerCardPool.Silent, "FAN_OF_KNIVES", PowerCommitmentFamily.ShivEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Silent, "FOOTWORK", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Silent, "INFINITE_BLADES", PowerCommitmentFamily.ShivEngine, new(PowerRoutePriority.Low, AllowTriggerBackedProjectionFloor: true, RequireFreeOrSpareActivation: true)),
        (PowerCardPool.Silent, "MASTER_PLANNER", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Normal, RequirePositiveProjection: true)),
        (PowerCardPool.Silent, "NOXIOUS_FUMES", PowerCommitmentFamily.PoisonEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Silent, "PHANTOM_BLADES", PowerCommitmentFamily.ShivEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Silent, "SERPENT_FORM", PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Silent, "SPEEDSTER", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Normal, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Silent, "TOOLS_OF_THE_TRADE", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Silent, "TRACKING", PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Silent, "WELL_LAID_PLANS", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Dedicated, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Silent, "WRAITH_FORM", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Strong, RequireImmediateDefenseGain: true)),

        // Ironclad (19)
        (PowerCardPool.Ironclad, "AGGRESSION", PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Normal, PreferDedicatedSearch: true)),
        (PowerCardPool.Ironclad, "BARRICADE", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Ironclad, "CORRUPTION", PowerCommitmentFamily.CostReductionEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Ironclad, "CRIMSON_MANTLE", PowerCommitmentFamily.BlockTriggerEngine | PowerCommitmentFamily.LifeInvestment, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Ironclad, "CRUELTY", PowerCommitmentFamily.StatusAmplifier, new(PowerRoutePriority.Normal, RequirePositiveProjection: true)),
        (PowerCardPool.Ironclad, "DARK_EMBRACE", PowerCommitmentFamily.ExhaustEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Ironclad, "DEMON_FORM", PowerCommitmentFamily.StrengthGrowth, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Ironclad, "FEEL_NO_PAIN", PowerCommitmentFamily.ExhaustEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Ironclad, "HELLRAISER", PowerCommitmentFamily.AutoPlayEngine, new(PowerRoutePriority.Low)),
        (PowerCardPool.Ironclad, "INFERNO", PowerCommitmentFamily.LifeInvestment | PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Ironclad, "INFLAME", PowerCommitmentFamily.StrengthGrowth, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Ironclad, "JUGGERNAUT", PowerCommitmentFamily.BlockTriggerEngine, new(PowerRoutePriority.Normal, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Ironclad, "JUGGLING", PowerCommitmentFamily.AutoPlayEngine | PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Low)),
        (PowerCardPool.Ironclad, "PYRE", PowerCommitmentFamily.EnergyEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Ironclad, "RUPTURE", PowerCommitmentFamily.StrengthGrowth | PowerCommitmentFamily.LifeInvestment, new(PowerRoutePriority.Normal, RequireFreeOrSpareActivation: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Ironclad, "STAMPEDE", PowerCommitmentFamily.AutoPlayEngine, new(PowerRoutePriority.Normal)),
        (PowerCardPool.Ironclad, "STONE_ARMOR", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Ironclad, "UNMOVABLE", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Ironclad, "VICIOUS", PowerCommitmentFamily.CardGenerationEngine | PowerCommitmentFamily.StatusAmplifier, new(PowerRoutePriority.Core, RequirePositiveProjection: true)),

        // Defect (20)
        (PowerCardPool.Defect, "BIASED_COGNITION", PowerCommitmentFamily.FocusEngine, new(PowerRoutePriority.Normal, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Defect, "BUFFER", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Strong, RequireImmediateDefenseGain: true)),
        (PowerCardPool.Defect, "BULK_UP", PowerCommitmentFamily.StrengthGrowth | PowerCommitmentFamily.DexterityGrowth, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Defect, "CAPACITOR", PowerCommitmentFamily.OrbEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Defect, "CONSUMING_SHADOW", PowerCommitmentFamily.OrbEngine, new(PowerRoutePriority.Low, RequireFreeOrSpareActivation: true)),
        (PowerCardPool.Defect, "COOLANT", PowerCommitmentFamily.OrbEngine | PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Low, RequireFreeOrSpareActivation: true)),
        (PowerCardPool.Defect, "CREATIVE_AI", PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Normal, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Defect, "DEFRAGMENT", PowerCommitmentFamily.FocusEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Defect, "ECHO_FORM", PowerCommitmentFamily.AutoPlayEngine | PowerCommitmentFamily.CostReductionEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Defect, "FERAL", PowerCommitmentFamily.CostReductionEngine, new(PowerRoutePriority.Normal)),
        (PowerCardPool.Defect, "HAILSTORM", PowerCommitmentFamily.OrbEngine | PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Defect, "ITERATION", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Defect, "LOOP", PowerCommitmentFamily.OrbEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Defect, "MACHINE_LEARNING", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Defect, "SMOKESTACK", PowerCommitmentFamily.CardGenerationEngine | PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Normal)),
        (PowerCardPool.Defect, "SPINNER", PowerCommitmentFamily.OrbEngine, new(PowerRoutePriority.Normal, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Defect, "STORM", PowerCommitmentFamily.OrbEngine, new(PowerRoutePriority.Core, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Defect, "SUBROUTINE", PowerCommitmentFamily.EnergyEngine, new(PowerRoutePriority.Core, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Defect, "THUNDER", PowerCommitmentFamily.OrbEngine | PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Defect, "TRASH_TO_TREASURE", PowerCommitmentFamily.OrbEngine | PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Normal)),

        // Regent (18)
        (PowerCardPool.Regent, "ARSENAL", PowerCommitmentFamily.StrengthGrowth | PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Regent, "BLACK_HOLE", PowerCommitmentFamily.StarEngine | PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Regent, "CHILD_OF_THE_STARS", PowerCommitmentFamily.StarEngine | PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Strong, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Regent, "FURNACE", PowerCommitmentFamily.StarEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Regent, "GENESIS", PowerCommitmentFamily.StarEngine | PowerCommitmentFamily.EnergyEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Regent, "MONARCHS_GAZE", PowerCommitmentFamily.StatusAmplifier, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Regent, "NEUTRON_AEGIS", PowerCommitmentFamily.DefenseEfficiency | PowerCommitmentFamily.StarEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Regent, "ORBIT", PowerCommitmentFamily.EnergyEngine, new(PowerRoutePriority.Core, PreferDedicatedSearch: true)),
        (PowerCardPool.Regent, "PALE_BLUE_DOT", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Normal, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Regent, "PARRY", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Regent, "PILLAR_OF_CREATION", PowerCommitmentFamily.BlockTriggerEngine | PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Core, RequirePositiveProjection: true)),
        (PowerCardPool.Regent, "ROYALTIES", PowerCommitmentFamily.CrossCombatGrowth, new(PowerRoutePriority.Low, NoInCombatCommitment: true)),
        (PowerCardPool.Regent, "SEEKING_EDGE", PowerCommitmentFamily.DamageEngine | PowerCommitmentFamily.StarEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Regent, "SPECTRUM_SHIFT", PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Regent, "SWORD_SAGE", PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Regent, "THE_SEALED_THRONE", PowerCommitmentFamily.StarEngine, new(PowerRoutePriority.Core, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Regent, "TYRANNY", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Regent, "VOID_FORM", PowerCommitmentFamily.CostReductionEngine | PowerCommitmentFamily.AutoPlayEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),

        // Necrobinder (18)
        (PowerCardPool.Necrobinder, "CALCIFY", PowerCommitmentFamily.SummonEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Necrobinder, "CALL_OF_THE_VOID", PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Necrobinder, "COUNTDOWN", PowerCommitmentFamily.DoomEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Necrobinder, "DANSE_MACABRE", PowerCommitmentFamily.BlockTriggerEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Necrobinder, "DEMESNE", PowerCommitmentFamily.EnergyEngine | PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Necrobinder, "DEVOUR_LIFE", PowerCommitmentFamily.SummonEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Necrobinder, "FORBIDDEN_GRIMOIRE", PowerCommitmentFamily.CrossCombatGrowth, new(PowerRoutePriority.Low, NoInCombatCommitment: true)),
        (PowerCardPool.Necrobinder, "FRIENDSHIP", PowerCommitmentFamily.EnergyEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Necrobinder, "HAUNT", PowerCommitmentFamily.DoomEngine | PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Necrobinder, "LETHALITY", PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Necrobinder, "NECRO_MASTERY", PowerCommitmentFamily.SummonEngine | PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Normal, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Necrobinder, "NEUROSURGE", PowerCommitmentFamily.EnergyEngine | PowerCommitmentFamily.LifeInvestment, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Necrobinder, "PAGESTORM", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Normal, RequirePositiveProjection: true)),
        (PowerCardPool.Necrobinder, "REAPER_FORM", PowerCommitmentFamily.DoomEngine | PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Necrobinder, "SENTRY_MODE", PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Necrobinder, "SHROUD", PowerCommitmentFamily.BlockTriggerEngine | PowerCommitmentFamily.DoomEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Necrobinder, "SLEIGHT_OF_FLESH", PowerCommitmentFamily.DamageEngine | PowerCommitmentFamily.StatusAmplifier, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Necrobinder, "SPIRIT_OF_ASH", PowerCommitmentFamily.BlockTriggerEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),

        // Colorless (12)
        (PowerCardPool.Colorless, "AUTOMATION", PowerCommitmentFamily.EnergyEngine, new(PowerRoutePriority.Core, PreferDedicatedSearch: true)),
        (PowerCardPool.Colorless, "CALAMITY", PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Normal, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Colorless, "ENTROPY", PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Strong)),
        (PowerCardPool.Colorless, "ETERNAL_ARMOR", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Strong, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Colorless, "FASTEN", PowerCommitmentFamily.DexterityGrowth | PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Core, RequirePositiveProjection: true)),
        (PowerCardPool.Colorless, "MAYHEM", PowerCommitmentFamily.AutoPlayEngine, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Colorless, "NOSTALGIA", PowerCommitmentFamily.RetainEngine | PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Strong, PreferDedicatedSearch: true)),
        (PowerCardPool.Colorless, "PANACHE", PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Colorless, "PREP_TIME", PowerCommitmentFamily.StrengthGrowth | PowerCommitmentFamily.DexterityGrowth, new(PowerRoutePriority.Normal, RequirePositiveProjection: true)),
        (PowerCardPool.Colorless, "PROWESS", PowerCommitmentFamily.StrengthGrowth | PowerCommitmentFamily.DexterityGrowth, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Colorless, "ROLLING_BOULDER", PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Normal, AllowTriggerBackedProjectionFloor: true)),
        (PowerCardPool.Colorless, "STRATAGEM", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true, PreferDedicatedSearch: true)),

        // Fool (18)
        (PowerCardPool.Fool, "ALL_IS_WELL", PowerCommitmentFamily.BlockTriggerEngine, new(PowerRoutePriority.Low, RequirePositiveProjection: true)),
        (PowerCardPool.Fool, "ARCHERY", PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Normal, RequirePositiveProjection: true)),
        (PowerCardPool.Fool, "ECSTASY", PowerCommitmentFamily.CardGenerationEngine, new(PowerRoutePriority.Normal, RequirePositiveProjection: true)),
        (PowerCardPool.Fool, "ENNUI", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Normal, RequirePositiveProjection: true)),
        (PowerCardPool.Fool, "EXPLOSIVE_ARROWS", PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Fool, "HIDDEN_ROOTS", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Fool, "HONING", PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Core, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Fool, "LOST_LUTE", PowerCommitmentFamily.AutoPlayEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Fool, "LULLABY", PowerCommitmentFamily.DefenseEfficiency, new(PowerRoutePriority.Core, AllowTriggerBackedProjectionFloor: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Fool, "MOTIF", PowerCommitmentFamily.HandEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Fool, "MYTHIC_FORM", PowerCommitmentFamily.CardGenerationEngine | PowerCommitmentFamily.RandomGeneration, new(PowerRoutePriority.Strong, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Fool, "QUIVER", PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Fool, "SERRATED_ARROWS", PowerCommitmentFamily.StatusAmplifier, new(PowerRoutePriority.Strong, RequirePositiveProjection: true)),
        (PowerCardPool.Fool, "SIMPLETON", PowerCommitmentFamily.AutoPlayEngine, new(PowerRoutePriority.Low, RequirePositiveProjection: true)),
        (PowerCardPool.Fool, "SONG_PATH", PowerCommitmentFamily.DefenseEfficiency | PowerCommitmentFamily.BlockTriggerEngine, new(PowerRoutePriority.Core, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Fool, "TUNE", PowerCommitmentFamily.CostReductionEngine, new(PowerRoutePriority.Normal, RequirePositiveProjection: true)),
        (PowerCardPool.Fool, "VIOLENT_CHORD", PowerCommitmentFamily.DamageEngine, new(PowerRoutePriority.Strong, RequirePositiveProjection: true, PreferDedicatedSearch: true)),
        (PowerCardPool.Fool, "YAH", PowerCommitmentFamily.EnergyEngine, new(PowerRoutePriority.Low, RequirePositiveProjection: true)),
    ];

    internal static IReadOnlyList<PowerCommitmentDescriptor> AllDescriptors { get; } = BuildDescriptors();

    private static PowerCommitmentDescriptor[] BuildDescriptors()
    {
        var result = new PowerCommitmentDescriptor[Entries.Length];
        for (int i = 0; i < Entries.Length; i++)
        {
            (PowerCardPool pool, string cardId, PowerCommitmentFamily family, PowerRouteAdmissionPolicy policy) = Entries[i];
            result[i] = new PowerCommitmentDescriptor(pool, cardId, family, policy);
        }
        return result;
    }
}
