using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class CardGenerationCardMirrors
{
    public static void AbundanceOnPlay(Abundance card, CardOnPlayMirrorContext context)
    {
        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .Where(candidate => candidate.Type == CardType.Power)
            .GetDistinctForCombat(
                card.Owner,
                3,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .Select(candidate => candidate.Upgrade())
            .ToList();

        RecordOptions(context, cards);
    }

    public static void BundleOfJoyOnPlay(BundleOfJoy card, CardOnPlayMirrorContext context)
    {
        var cards = card.Owner.GetUnlockedColorlessCards(context.CardMultiplayerConstraint)
            .GetDistinctForCombat(
                card.Owner,
                card.DynamicVars.Cards.IntValue,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void DistractionOnPlay(Distraction card, CardOnPlayMirrorContext context)
    {
        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .Where(candidate => candidate.Type == CardType.Skill)
            .GetDistinctForCombat(
                card.Owner,
                1,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .Select(generatedCard => generatedCard.SetToFreeThisTurn())
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void DiscoveryOnPlay(Discovery card, CardOnPlayMirrorContext context)
    {
        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .GetDistinctForCombat(
                card.Owner,
                3,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .ToList();

        RecordOptions(context, cards);
    }

    public static void InfernalBladeOnPlay(InfernalBlade card, CardOnPlayMirrorContext context)
    {
        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .Where(candidate => candidate.Type == CardType.Attack)
            .GetDistinctForCombat(
                card.Owner,
                1,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .Select(generatedCard => generatedCard.SetToFreeThisTurn())
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void JackOfAllTradesOnPlay(JackOfAllTrades card, CardOnPlayMirrorContext context)
    {
        var cards = card.Owner.GetUnlockedColorlessCards(context.CardMultiplayerConstraint)
            .Where(candidate => candidate is not JackOfAllTrades)
            .GetDistinctForCombat(
                card.Owner,
                card.DynamicVars.Cards.IntValue,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void JackpotOnPlay(Jackpot card, CardOnPlayMirrorContext context)
    {
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;

        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .Where(candidate => candidate.EnergyCost is { Canonical: 0, CostsX: false })
            .GetForCombat(
                card.Owner,
                card.DynamicVars.Cards.IntValue,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .UpgradeIf(card.IsUpgraded)
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void LargesseOnPlay(Largesse card, CardOnPlayMirrorContext context)
    {
        var targetPlayer = context.TargetPlayer;
        var cards = targetPlayer.GetUnlockedColorlessCards(context.CardMultiplayerConstraint)
            .GetDistinctForCombat(
                targetPlayer,
                1,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .UpgradeIf(card.IsUpgraded)
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    /// <summary>
    /// Books the permanent deck upgrade that the Improvement rider will pay out after the fight.
    /// </summary>
    /// <remarks>
    /// <see cref="ImprovementPower"/> is a counter that runs in <c>AfterCombatEnd</c>: for each stack it picks a
    /// random upgradable card out of <see cref="PileType.Deck"/> and upgrades it for good. That is the same
    /// payoff Genetic Algorithm, The Scythe and Royalties are already booked for, so it belongs on the same
    /// axis; without it a Mad Science built as Improvement scored as a plain Power card worth one buff stack,
    /// and the "pursue cross-combat value" setting could not see it at all.
    ///
    /// The value matches The Hunt (<see cref="CorePowerSupport.TheHuntLongTermResourceValue"/>), which is what
    /// one permanent card upgrade is worth on this scale. One simplification: the real power gives nothing when
    /// the deck holds no upgradable card, and deck contents are not part of combat state, so the payoff is
    /// always counted. A deck with nothing left to upgrade is a deck where nobody is holding this card back.
    /// </remarks>
    private static void RecordImprovementGrowth(CardOnPlayMirrorContext context)
    {
        if (context.CombatState is not SimulatedCombatState combat)
            return;
        combat.RecordLongTermResource(CorePowerSupport.TheHuntLongTermResourceValue);
        combat.RecordLongTermGoal(LongTermGoals.PersistentGrowth);
    }

    public static void MadScienceOnPlay(MadScience card, CardOnPlayMirrorContext context)
    {
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("疯狂科学效果缺少可写的预测状态。");
        switch (card.TinkerTimeType)
        {
            case CardType.Attack:
                DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
                    .WithHitCount(card.TinkerTimeRider == TinkerTime.RiderEffect.Violence
                        ? card.DynamicVars["ViolenceHits"].IntValue
                        : 1)
                    .FromCard(card, context.CardPlay)
                    .Targeting(context.Target)
                    .Simulate(context.Simulator);
                break;
            case CardType.Skill:
                context.GainBlock(card.Owner.Creature);
                break;
            case CardType.Power:
                switch (card.TinkerTimeRider)
                {
                    case TinkerTime.RiderEffect.Expertise:
                        effects.ApplyPower(
                            typeof(StrengthPower),
                            card.Owner.Creature,
                            card.DynamicVars["ExpertiseStrength"].IntValue,
                            card.Owner.Creature);
                        effects.ApplyPower(
                            typeof(DexterityPower),
                            card.Owner.Creature,
                            card.DynamicVars["ExpertiseDexterity"].IntValue,
                            card.Owner.Creature);
                        break;
                    case TinkerTime.RiderEffect.Curious:
                        effects.ApplyPower(
                            typeof(CuriousPower),
                            card.Owner.Creature,
                            card.DynamicVars["CuriousReduction"].IntValue,
                            card.Owner.Creature);
                        break;
                    case TinkerTime.RiderEffect.Improvement:
                        effects.ApplyPower(typeof(ImprovementPower), card.Owner.Creature, 1, card.Owner.Creature);
                        RecordImprovementGrowth(context);
                        break;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(card.TinkerTimeType), card.TinkerTimeType, null);
        }
        if (context.Simulator.HasPendingChoice)
            return;
        switch (card.TinkerTimeRider)
        {
            case TinkerTime.RiderEffect.Sapping:
                effects.ApplyPower(
                    typeof(WeakPower),
                    context.Target,
                    card.DynamicVars["SappingWeak"].IntValue,
                    card.Owner.Creature);
                effects.ApplyPower(
                    typeof(VulnerablePower),
                    context.Target,
                    card.DynamicVars["SappingVulnerable"].IntValue,
                    card.Owner.Creature);
                break;
            case TinkerTime.RiderEffect.Choking:
                effects.ApplyPower(
                    typeof(StranglePower),
                    context.Target,
                    card.DynamicVars["ChokingDamage"].IntValue,
                    card.Owner.Creature);
                break;
            case TinkerTime.RiderEffect.Energized:
                context.Simulator.GainEnergy(card.Owner, card.DynamicVars["EnergizedEnergy"].IntValue);
                break;
            case TinkerTime.RiderEffect.Wisdom:
                context.Simulator.Draw(card.Owner, card.DynamicVars["WisdomCards"].IntValue);
                break;
            case TinkerTime.RiderEffect.Chaos:
            {
                var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
                    .GetDistinctForCombat(
                        card.Owner,
                        1,
                        context.Rng.CombatCardGeneration,
                        context.CardMultiplayerConstraint)
                    .Select(generatedCard => generatedCard.SetToFreeThisTurn())
                    .ToList();
                context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
                break;
            }
        }
    }

    public static void ManifestAuthorityOnPlay(ManifestAuthority card, CardOnPlayMirrorContext context)
    {
        context.GainBlock(card.Owner.Creature);
        if (context.Simulator.HasPendingChoice)
            return;

        var cards = context.Simulator
            .GetDistinctUnlockedColorlessForCombat(
                card.Owner,
                1,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .UpgradeIf(card.IsUpgraded)
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void MetamorphosisOnPlay(Metamorphosis card, CardOnPlayMirrorContext context)
    {
        var cards = context.Simulator
            .GetUnlockedCharacterAttacksForCombat(
                card.Owner,
                card.DynamicVars.Cards.IntValue,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .Select(generatedCard => generatedCard.SetToFreeThisCombat())
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(
            cards,
            PileType.Draw,
            card.Owner,
            CardPilePosition.Random);
    }

    public static void QuasarOnPlay(Quasar card, CardOnPlayMirrorContext context)
    {
        var cards = context.Simulator
            .GetDistinctUnlockedColorlessForCombat(
                card.Owner,
                3,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .UpgradeIf(card.IsUpgraded)
            .ToList();

        RecordOptions(context, cards);
    }

    public static void SplashOnPlay(Splash card, CardOnPlayMirrorContext context)
    {
        var pools = card.Owner.UnlockState.CharacterCardPools.ToList();
        if (pools.Count > 1)
        {
            pools.Remove(card.Owner.Character.CardPool);
        }

        var cards = pools
            .SelectMany(pool => card.Owner.GetUnlockedCards(pool, context.CardMultiplayerConstraint))
            .Where(candidate => candidate.Type == CardType.Attack)
            .GetDistinctForCombat(
                card.Owner,
                3,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .UpgradeIf(card.IsUpgraded)
            .ToList();

        RecordOptions(context, cards);
    }

    public static void StokeOnPlay(Stoke card, CardOnPlayMirrorContext context)
    {
        var cardsToExhaust = context.OwnerState.Hand.Cards.ToList();
        foreach (var cardToExhaust in cardsToExhaust)
        {
            context.Simulator.Exhaust(cardToExhaust);
            if (context.Simulator.HasPendingChoice)
                return;
        }

        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .GetForCombat(
                card.Owner,
                cardsToExhaust.Count,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .UpgradeIf(card.IsUpgraded)
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    public static void WhiteNoiseOnPlay(WhiteNoise card, CardOnPlayMirrorContext context)
    {
        var cards = card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
            .Where(candidate => candidate.Type == CardType.Power)
            .GetDistinctForCombat(
                card.Owner,
                1,
                context.Rng.CombatCardGeneration,
                context.CardMultiplayerConstraint)
            .Select(generatedCard => generatedCard.SetToFreeThisTurn())
            .ToList();

        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }

    private static void RecordOptions(CardOnPlayMirrorContext context, IReadOnlyList<PredictedCard> cards)
    {
        if (cards.Count == 0)
        {
            return;
        }

        context.Simulator.History.CardGenerationOptions(cards);
        // Vanilla next asks the player to choose an option. Record the deterministic options first,
        // then mark the unresolved choice so replayed or nested results inherit the uncertainty.
        context.History.RecordRisk(PredictionRiskReason.UnresolvedPlayerChoice);
    }
}
