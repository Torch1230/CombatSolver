using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

using Registry = MethodMirrorRegistry<CardModel, CardOnPlayMirrorContext>;

// Simulation-facing facade and central registration index for mirrored CardModel.OnPlay behavior.
internal static class CardOnPlayMirrors
{
    private static readonly MirrorMethodSpec OnPlay = new(
        typeof(CardModel),
        "OnPlay",
        BindingFlags.Instance | BindingFlags.NonPublic,
        [typeof(PlayerChoiceContext), typeof(CardPlay)]);

    private static readonly Registry Registry = CreateRegistry();

    public static bool AllowInference
    {
        get => Registry.AllowInference;
        set => Registry.AllowInference = value;
    }

    public static bool CanMirror(CardModel card)
    {
        return Registry.HasRegisteredHandler(card);
    }

    public static bool IsOnPlayInvocation(PredictionInvocation invocation)
    {
        return ReferenceEquals(invocation.Method, OnPlay.BaseMethod);
    }

    public static MirrorDispatchResult Invoke(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        CardPlay cardPlay)
    {
        // The mutable preview is the receiver because OnPlay handlers may mutate the played card.
        // CardOnPlayMirrorContext maps its source back to the original card and exposes that same
        // original model as the StateStore key.
        MirrorDispatchResult result = Registry.Invoke(card.MutablePreview, new()
        {
            Simulator = simulator,
            Card = card,
            CardPlay = cardPlay
        });
        if (simulator.State.CombatState is SimulatedCombatState combat)
            CardEffectSpecRegistry.Apply(simulator, combat, card, cardPlay.Target);
        return result;
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(OnPlay);

        registry.Register<HowlFromBeyond>(GeneralCardMirrors.GeneralAttackOnPlay);
        registry.Register<Mangle>(GeneralCardMirrors.GeneralAttackOnPlay);
        registry.Register<IAmInvincible>(GeneralCardMirrors.GeneralBlockOnPlay);
        registry.Register<AstralPulse>(BespokeCardMirrors.AstralPulseOnPlay);
        registry.Register<DaggerSpray>(BespokeCardMirrors.DaggerSprayOnPlay);
        registry.Register<PactsEnd>(BespokeCardMirrors.PactsEndOnPlay);
        registry.Register<TwinStrike>(BespokeCardMirrors.TwinStrikeOnPlay);
        registry.Register<Dismantle>(BespokeCardMirrors.DismantleOnPlay);
        registry.Register<Entrench>(BespokeCardMirrors.EntrenchOnPlay);
        registry.Register<FiendFire>(BespokeCardMirrors.FiendFireOnPlay);
        registry.Register<LeadingStrike>(BespokeCardMirrors.LeadingStrikeOnPlay);
        registry.Register<Maul>(BespokeCardMirrors.MaulOnPlay);
        registry.Register<Sacrifice>(BespokeCardMirrors.SacrificeOnPlay);
        registry.Register<Spite>(BespokeCardMirrors.SpiteOnPlay);
        registry.Register<TheScythe>(BespokeCardMirrors.TheScytheOnPlay);
        registry.Register<SecondWind>(BespokeCardMirrors.SecondWindOnPlay);
        registry.Register<SovereignBlade>(BespokeCardMirrors.SovereignBladeOnPlay);
        registry.Register<Havoc>(AutoPlayCardMirrors.HavocOnPlay);
        registry.Register<Cascade>(AutoPlayCardMirrors.CascadeOnPlay);

        registry.Register<Alchemize>(PotionGenerationCardMirrors.AlchemizeOnPlay);

        registry.Register<CalculatedGamble>(CardDrawCardMirrors.CalculatedGambleOnPlay);
        registry.Register<BurningPact>(static (_, _) => { });
        registry.Register<CompileDriver>(CardDrawCardMirrors.CompileDriverOnPlay);
        registry.Register<Constellation>(CardDrawCardMirrors.ConstellationOnPlay);
        registry.Register<EscapePlan>(CardDrawCardMirrors.EscapePlanOnPlay);
        registry.Register<Expertise>(CardDrawCardMirrors.ExpertiseOnPlay);
        registry.Register<Fetch>(CardDrawCardMirrors.FetchOnPlay);
        registry.Register<Ftl>(CardDrawCardMirrors.FtlOnPlay);
        registry.Register<HuddleUp>(CardDrawCardMirrors.HuddleUpOnPlay);
        registry.Register<Impatience>(CardDrawCardMirrors.ImpatienceOnPlay);
        registry.Register<Pillage>(CardDrawCardMirrors.PillageOnPlay);
        registry.Register<Reboot>(CardDrawCardMirrors.RebootOnPlay);
        registry.Register<Restlessness>(CardDrawCardMirrors.RestlessnessOnPlay);
        registry.Register<Scrape>(CardDrawCardMirrors.ScrapeOnPlay);
        registry.Register<Scrawl>(CardDrawCardMirrors.ScrawlOnPlay);

        registry.Register<FlakCannon>(RandomTargetAttackCardMirrors.FlakCannonOnPlay);
        registry.Register<Ricochet>(RandomTargetAttackCardMirrors.RicochetOnPlay);
        registry.Register<RipAndTear>(RandomTargetAttackCardMirrors.RipAndTearOnPlay);
        registry.Register<Stardust>(RandomTargetAttackCardMirrors.StardustOnPlay);
        registry.Register<SweepingGaze>(RandomTargetAttackCardMirrors.SweepingGazeOnPlay);
        registry.Register<SwordBoomerang>(RandomTargetAttackCardMirrors.SwordBoomerangOnPlay);
        registry.Register<Volley>(RandomTargetAttackCardMirrors.VolleyOnPlay);

        registry.Register<BallLightning>(OrbCardMirrors.BallLightningOnPlay);
        registry.Register<Chaos>(OrbCardMirrors.ChaosOnPlay);
        registry.Register<Chill>(OrbCardMirrors.ChillOnPlay);
        registry.Register<ColdSnap>(OrbCardMirrors.ColdSnapOnPlay);
        registry.Register<ConsumingShadow>(OrbCardMirrors.ConsumingShadowOnPlay);
        registry.Register<Coolheaded>(OrbCardMirrors.CoolheadedOnPlay);
        registry.Register<Darkness>(OrbCardMirrors.DarknessOnPlay);
        registry.Register<Dualcast>(OrbCardMirrors.DualcastOnPlay);
        registry.Register<Fusion>(OrbCardMirrors.FusionOnPlay);
        registry.Register<Glacier>(OrbCardMirrors.GlacierOnPlay);
        registry.Register<Glasswork>(OrbCardMirrors.GlassworkOnPlay);
        registry.Register<IceLance>(OrbCardMirrors.IceLanceOnPlay);
        registry.Register<Ignition>(OrbCardMirrors.IgnitionOnPlay);
        registry.Register<MeteorStrike>(OrbCardMirrors.MeteorStrikeOnPlay);
        registry.Register<MultiCast>(OrbCardMirrors.MultiCastOnPlay);
        registry.Register<Null>(OrbCardMirrors.NullOnPlay);
        registry.Register<Quadcast>(OrbCardMirrors.QuadcastOnPlay);
        registry.Register<Rainbow>(OrbCardMirrors.RainbowOnPlay);
        registry.Register<Refract>(OrbCardMirrors.RefractOnPlay);
        registry.Register<ShadowShield>(OrbCardMirrors.ShadowShieldOnPlay);
        registry.Register<Shatter>(OrbCardMirrors.ShatterOnPlay);
        registry.Register<Spinner>(OrbCardMirrors.SpinnerOnPlay);
        registry.Register<Tempest>(OrbCardMirrors.TempestOnPlay);
        registry.Register<TeslaCoil>(OrbCardMirrors.TeslaCoilOnPlay);
        registry.Register<Voltaic>(OrbCardMirrors.VoltaicOnPlay);
        registry.Register<Zap>(OrbCardMirrors.ZapOnPlay);

        registry.Register<Anointed>(CardSelectionCardMirrors.AnointedOnPlay);
        registry.Register<BeatDown>(CardSelectionCardMirrors.BeatDownOnPlay);
        registry.Register<Catastrophe>(CardSelectionCardMirrors.CatastropheOnPlay);
        registry.Register<Cinder>(CardSelectionCardMirrors.CinderOnPlay);
        registry.Register<DrainPower>(CardSelectionCardMirrors.DrainPowerOnPlay);
        registry.Register<HiddenGem>(CardSelectionCardMirrors.HiddenGemOnPlay);
        registry.Register<SeekerStrike>(CardSelectionCardMirrors.SeekerStrikeOnPlay);
        registry.Register<Thrash>(CardSelectionCardMirrors.ThrashOnPlay);
        registry.Register<TrueGrit>(CardSelectionCardMirrors.TrueGritOnPlay);
        registry.Register<Uproar>(CardSelectionCardMirrors.UproarOnPlay);

        registry.Register<Abundance>(CardGenerationCardMirrors.AbundanceOnPlay);
        registry.Register<BundleOfJoy>(CardGenerationCardMirrors.BundleOfJoyOnPlay);
        registry.Register<Distraction>(CardGenerationCardMirrors.DistractionOnPlay);
        registry.Register<Discovery>(CardGenerationCardMirrors.DiscoveryOnPlay);
        registry.Register<InfernalBlade>(CardGenerationCardMirrors.InfernalBladeOnPlay);
        registry.Register<JackOfAllTrades>(CardGenerationCardMirrors.JackOfAllTradesOnPlay);
        registry.Register<Jackpot>(CardGenerationCardMirrors.JackpotOnPlay);
        registry.Register<Largesse>(CardGenerationCardMirrors.LargesseOnPlay);
        registry.Register<MadScience>(CardGenerationCardMirrors.MadScienceOnPlay);
        registry.Register<ManifestAuthority>(CardGenerationCardMirrors.ManifestAuthorityOnPlay);
        registry.Register<Metamorphosis>(CardGenerationCardMirrors.MetamorphosisOnPlay);
        registry.Register<Quasar>(CardGenerationCardMirrors.QuasarOnPlay);
        registry.Register<Splash>(CardGenerationCardMirrors.SplashOnPlay);
        registry.Register<Stoke>(CardGenerationCardMirrors.StokeOnPlay);
        registry.Register<WhiteNoise>(CardGenerationCardMirrors.WhiteNoiseOnPlay);

        registry.Register<StrikeIronclad>(GeneralCardMirrors.GeneralAttackOnPlay);
        registry.Register<StrikeSilent>(GeneralCardMirrors.GeneralAttackOnPlay);
        registry.Register<StrikeRegent>(GeneralCardMirrors.GeneralAttackOnPlay);
        registry.Register<StrikeNecrobinder>(GeneralCardMirrors.GeneralAttackOnPlay);
        registry.Register<StrikeDefect>(GeneralCardMirrors.GeneralAttackOnPlay);
        registry.Register<Fisticuffs>(GeneralCardMirrors.GeneralAttackOnPlay);

        registry.Register<DefendIronclad>(GeneralCardMirrors.GeneralBlockOnPlay);
        registry.Register<DefendSilent>(GeneralCardMirrors.GeneralBlockOnPlay);
        registry.Register<DefendRegent>(GeneralCardMirrors.GeneralBlockOnPlay);
        registry.Register<DefendNecrobinder>(GeneralCardMirrors.GeneralBlockOnPlay);
        registry.Register<DefendDefect>(GeneralCardMirrors.GeneralBlockOnPlay);

        registry.RegisterStrictInferrer(CardOnPlayInferrer.InferStrict);
        registry.RegisterInferrer(CardOnPlayInferrer.Infer);

        return registry;
    }
}
