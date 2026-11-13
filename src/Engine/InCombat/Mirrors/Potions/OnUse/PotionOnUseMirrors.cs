using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Potions.OnUse;

using Registry = MethodMirrorRegistry<PotionModel, PotionOnUseMirrorContext>;

/// <summary>Owns exact-runtime-type dispatch for mirrored <see cref="PotionModel.OnUse"/> behavior.</summary>
internal static class PotionOnUseMirrors
{
    private static readonly MirrorMethodSpec OnUse = new(
        typeof(PotionModel),
        "OnUse",
        BindingFlags.Instance | BindingFlags.NonPublic,
        [typeof(PlayerChoiceContext), typeof(Creature)]);

    private static readonly Registry Registry = CreateRegistry();

    /// <summary>
    /// Returns whether the potion's exact runtime type has a registered handler without opening a trace or recording risk.
    /// </summary>
    public static bool CanMirror(PotionModel potion)
    {
        return Registry.HasRegisteredHandler(potion);
    }

    public static bool IsOnUseInvocation(PredictionInvocation invocation)
    {
        return ReferenceEquals(invocation.Method, OnUse.BaseMethod);
    }

    /// <summary>Invokes the exact registered handler inside a potion <see cref="PotionModel.OnUse"/> method frame.</summary>
    /// <remarks>
    /// Unsupported gameplay overrides record <see cref="PredictionRiskReason.MethodNotMirrored"/> risk through the
    /// supplied simulator.
    /// </remarks>
    public static MirrorDispatchResult Invoke(
        CombatPredictionSimulator simulator,
        PotionModel potion,
        Creature? target)
    {
        return Registry.Invoke(potion, new PotionOnUseMirrorContext
        {
            Simulator = simulator,
            Potion = potion,
            Target = target
        });
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(OnUse);
        registry.Register<BottledSong>(FoolPotionMirrors.BottledSong);
        registry.Register<CrystallineEuphoria>(FoolPotionMirrors.CrystallineEuphoria);
        registry.Register<PrecisionPotion>(FoolPotionMirrors.Precision);

        registry.Register<AttackPotion>(CardGenerationPotionMirrors.OnUse);
        registry.Register<SkillPotion>(CardGenerationPotionMirrors.OnUse);
        registry.Register<PowerPotion>(CardGenerationPotionMirrors.OnUse);
        registry.Register<ColorlessPotion>(CardGenerationPotionMirrors.OnUse);
        registry.Register<CosmicConcoction>(CardGenerationPotionMirrors.OnUse);
        registry.Register<OrobicAcid>(CardGenerationPotionMirrors.OnUse);

        registry.Register<EntropicBrew>(EntropicBrewMirrors.OnUse);

        registry.Register<BottledPotential>(DrawPotionMirrors.BottledPotentialOnUse);
        registry.Register<Clarity>(DrawPotionMirrors.ClarityOnUse);
        registry.Register<CureAll>(DrawPotionMirrors.CureAllOnUse);
        registry.Register<GlowwaterPotion>(DrawPotionMirrors.GlowwaterPotionOnUse);
        registry.Register<SneckoOil>(DrawPotionMirrors.SneckoOilOnUse);
        registry.Register<SwiftPotion>(DrawPotionMirrors.SwiftPotionOnUse);

        registry.Register<DistilledChaos>(AutoPlayPotionMirrors.DistilledChaosOnUse);

        registry.Register<EssenceOfDarkness>(OrbPotionMirrors.EssenceOfDarknessOnUse);

        return registry;
    }
}
