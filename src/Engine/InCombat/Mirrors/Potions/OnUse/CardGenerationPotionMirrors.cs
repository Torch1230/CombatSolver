using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Potions.OnUse;

/// <summary>
/// Contains the generated card snapshots shared by combat simulation and out-of-combat potion previews.
/// </summary>
/// <param name="Cards">Generated prediction-owned cards in vanilla RNG order.</param>
/// <param name="AddsToHand">
/// Whether the potion adds every generated card to the combat hand instead of presenting player-choice options.
/// </param>
internal sealed record PotionCardGenerationResult(
    IReadOnlyList<PredictedCard> Cards,
    bool AddsToHand);

internal static class CardGenerationPotionMirrors
{
    public static void OnUse(PotionModel potion, PotionOnUseMirrorContext context)
    {
        var player = context.TargetPlayer;
        var result = Generate(
            potion,
            player,
            context.Rng.CombatCardGeneration,
            context.CardMultiplayerConstraint,
            context.Simulator)
            ?? throw new UnreachableException($"No card generation policy for registered potion {potion.Id}.");

        if (result.Cards.Count == 0)
        {
            return;
        }

        if (result.AddsToHand)
        {
            context.Simulator.AddGeneratedCardsToCombat(result.Cards, PileType.Hand, potion.Owner);
        }
        else
        {
            context.History.CardGenerationOptions(result.Cards);
            // The options are deterministic, but the selected card and its addition to hand remain unresolved.
            context.History.RecordRisk(PredictionRiskReason.UnresolvedPlayerChoice);
        }
    }

    /// <summary>
    /// Generates prediction-owned cards without advancing real RNG or applying combat piles and hooks.
    /// </summary>
    /// <param name="potion">The supported mutable potion whose runtime behavior determines the result shape.</param>
    /// <param name="target">The player whose unlocks, card pool constraints and ownership apply.</param>
    /// <param name="rng">A prediction-owned clone of the target run's combat-card-generation RNG.</param>
    /// <returns>The generated result, or <see langword="null"/> for an unsupported potion type.</returns>
    public static PotionCardGenerationResult? Generate(
        PotionModel potion,
        Player target,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint,
        CombatPredictionSimulator? simulator = null)
    {
        return potion switch
        {
            AttackPotion => new(
                GenerateCharacterCards(target, CardType.Attack, 3, rng, multiplayerConstraint),
                AddsToHand: false),
            SkillPotion => new(
                GenerateCharacterCards(target, CardType.Skill, 3, rng, multiplayerConstraint),
                AddsToHand: false),
            PowerPotion => new(
                GenerateCharacterCards(target, CardType.Power, 3, rng, multiplayerConstraint),
                AddsToHand: false),
            ColorlessPotion => new(GenerateColorlessCards(target, 3, rng, multiplayerConstraint, simulator), AddsToHand: false),
            CosmicConcoction => new(
                [.. GenerateColorlessCards(target, potion.DynamicVars.Cards.IntValue, rng, multiplayerConstraint, simulator)
                    .Select(static card => card.Upgrade())],
                AddsToHand: true),
            OrobicAcid => new(GenerateOrobicAcidCards(target, rng, multiplayerConstraint), AddsToHand: true),
            _ => null
        };
    }

    private static List<PredictedCard> GenerateCharacterCards(
        Player player,
        CardType type,
        int count,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        return [.. player.GetUnlockedCharacterCards(multiplayerConstraint)
            .Where(candidate => candidate.Type == type)
            .GetDistinctForCombat(player, count, rng, multiplayerConstraint)];
    }

    private static List<PredictedCard> GenerateColorlessCards(
        Player player,
        int count,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint,
        CombatPredictionSimulator? simulator)
    {
        if (simulator is not null)
            return [.. simulator.GetDistinctUnlockedColorlessForCombat(
                player, count, rng, multiplayerConstraint)];
        return [.. player.GetUnlockedColorlessCards(multiplayerConstraint)
            .GetDistinctForCombat(player, count, rng, multiplayerConstraint)];
    }

    private static List<PredictedCard> GenerateOrobicAcidCards(
        Player player,
        Rng rng,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        List<PredictedCard> cards = [];
        CardType[] types = [CardType.Attack, CardType.Skill, CardType.Power];

        foreach (var type in types)
        {
            cards.AddRange(player.GetUnlockedCharacterCards(multiplayerConstraint)
                .Where(candidate => candidate.Type == type)
                .GetDistinctForCombat(player, 1, rng, multiplayerConstraint));
        }

        foreach (var card in cards)
        {
            card.SetToFreeThisTurn();
        }

        return cards;
    }
}
