using System.Diagnostics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Random;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static string AssertExtraGenerationCacheContract(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        CombatPredictionSimulator parent = root.ForkSimulator();
        CardMultiplayerConstraint constraint =
            ((ICombatPredictionRunSnapshot)parent.State.CombatState).CardMultiplayerConstraint;
        AssertRootColorlessGenerationPoolCache(parent, player);
        AssertRootCharacterAttackGenerationPoolCache(parent, player);
        string Stamp(CombatPredictionSimulator simulator) => ContinuationStamp.CapturePredicted(
            player, simulator, 1, root.Forecast, root.StartTurnNumber).StateText;
        string parentBefore = Stamp(parent);
        int comparisons = 0;
        foreach (bool attacks in new[] { false, true })
        foreach (bool upgraded in new[] { false, true })
        foreach (int skip in new[] { 0, 1, 7 })
        {
            CombatPredictionSimulator baseline = parent.Fork(), candidate = parent.Fork();
            baseline.Rng.CombatCardGeneration.Advance(skip);
            candidate.Rng.CombatCardGeneration.Advance(skip);
            CardModel model = attacks ? CanonicalModels.Card<InfernalBlade>()
                : CanonicalModels.Card<BundleOfJoy>();
            PredictedCard beforeCard = PredictedCard.Create(model, player);
            PredictedCard afterCard = PredictedCard.Create(model, player);
            if (upgraded) { beforeCard.Upgrade(); afterCard.Upgrade(); }
            CardOnPlayMirrorContext beforeContext = new()
            {
                Simulator = baseline,
                Card = beforeCard,
                CardPlay = CreatePendingTailCardPlay(beforeCard, player),
            };
            CardOnPlayMirrorContext afterContext = new()
            {
                Simulator = candidate,
                Card = afterCard,
                CardPlay = CreatePendingTailCardPlay(afterCard, player),
            };
            ApplyGenerationCacheBaseline(beforeCard.Preview, beforeContext, attacks);
            if (attacks)
                CardGenerationCardMirrors.InfernalBladeOnPlay((InfernalBlade)afterCard.Preview, afterContext);
            else
                CardGenerationCardMirrors.BundleOfJoyOnPlay((BundleOfJoy)afterCard.Preview, afterContext);
            if (Stamp(baseline) != Stamp(candidate)
                || !SameFiveFieldRngState(baseline.Rng.CombatCardGeneration.CaptureState(),
                    candidate.Rng.CombatCardGeneration.CaptureState()))
                throw new InvalidOperationException("Generation cache changed a complete predicted state or RNG field.");
            comparisons++;
        }

        foreach (int count in new[] { 0, 1, 3, 128 })
        {
            Rng beforeRng = parent.Rng.CombatCardGeneration.Clone();
            Rng afterRng = parent.Rng.CombatCardGeneration.Clone();
            PredictedCard[] before = player.GetUnlockedCharacterCards(constraint)
                .Where(static card => card.Type == CardType.Attack)
                .GetDistinctForCombat(player, count, beforeRng, constraint).ToArray();
            PredictedCard[] after = parent.GetDistinctUnlockedCharacterAttacksForCombat(
                player, count, afterRng, constraint).ToArray();
            if (before.Length != after.Length
                || !SameFiveFieldRngState(beforeRng.CaptureState(), afterRng.CaptureState()))
                throw new InvalidOperationException("Distinct attack cache changed count or RNG consumption.");
            for (int index = 0; index < before.Length; index++)
            {
                if (ReferenceEquals(before[index], after[index])
                    || ReferenceEquals(before[index].Original, after[index].Original)
                    || CombatBeamSolver.CaptureCardStateFingerprintForTesting(before[index])
                        != CombatBeamSolver.CaptureCardStateFingerprintForTesting(after[index]))
                    throw new InvalidOperationException("Distinct attack cache changed card state or shared mutable cards.");
            }
            comparisons++;
        }
        if (Stamp(parent) != parentBefore)
            throw new InvalidOperationException("Generation cache test changed its parent branch.");

        return $"ExtraGenerationCache:comparisons={comparisons}:full_state=true:rng_five_fields=true:distinct=true:mutable_cards_independent=true:parent_unchanged=true";
    }

    private static string MeasureExtraGenerationCache(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        CombatPredictionSimulator parent = root.ForkSimulator();
        CardMultiplayerConstraint constraint =
            ((ICombatPredictionRunSnapshot)parent.State.CombatState).CardMultiplayerConstraint;
        ICombatPredictionCardGenerationPoolSnapshot pools =
            (ICombatPredictionCardGenerationPoolSnapshot)parent.State.CombatState;
        if (!pools.TryGetRootEligibleCharacterAttackCards(player, player.Character.CardPool, constraint, out _)
            || !pools.TryGetRootEligibleCards(player,
                ModelDb.CardPool<MegaCrit.Sts2.Core.Models.CardPools.ColorlessCardPool>(), constraint, out _))
            throw new InvalidOperationException("Generation cache measurement requires both cache hits.");
        const int iterations = 1000;
        List<object> measurements = [];
        foreach (bool attacks in new[] { false, true })
        foreach (string label in new[] { "A1", "B1", "B2", "A2" })
        {
            Rng rng = parent.Rng.CombatCardGeneration.Clone();
            Func<PredictedCard[]> generate = label[0] == 'A'
                ? () => (attacks
                    ? player.GetUnlockedCharacterCards(constraint).Where(static c => c.Type == CardType.Attack)
                    : player.GetUnlockedColorlessCards(constraint))
                    .GetDistinctForCombat(player, 1, rng, constraint).ToArray()
                : () => (attacks
                    ? parent.GetDistinctUnlockedCharacterAttacksForCombat(player, 1, rng, constraint)
                    : parent.GetDistinctUnlockedColorlessForCombat(player, 1, rng, constraint)).ToArray();
            for (int index = 0; index < 32; index++) GC.KeepAlive(generate());
            long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
            for (int index = 0; index < iterations; index++) GC.KeepAlive(generate());
            measurements.Add(new { attacks, label, iterations,
                milliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency,
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated });
        }
        return "ExtraGenerationCacheMeasurements:" + System.Text.Json.JsonSerializer.Serialize(new
        {
            measurements,
            scope = "native helper allocation and elapsed time; not whole-search timing",
        });
    }

    // Frozen call-site pipelines from 415da12. The production cache helper is not used here.
    private static void ApplyGenerationCacheBaseline(
        CardModel card, CardOnPlayMirrorContext context, bool attacks)
    {
        List<PredictedCard> cards = attacks
            ? card.Owner.GetUnlockedCharacterCards(context.CardMultiplayerConstraint)
                .Where(candidate => candidate.Type == CardType.Attack)
                .GetDistinctForCombat(card.Owner, 1, context.Rng.CombatCardGeneration,
                    context.CardMultiplayerConstraint)
                .Select(generatedCard => generatedCard.SetToFreeThisTurn()).ToList()
            : card.Owner.GetUnlockedColorlessCards(context.CardMultiplayerConstraint)
                .GetDistinctForCombat(card.Owner, card.DynamicVars.Cards.IntValue,
                    context.Rng.CombatCardGeneration, context.CardMultiplayerConstraint).ToList();
        context.Simulator.AddGeneratedCardsToCombat(cards, PileType.Hand, card.Owner);
    }
}
