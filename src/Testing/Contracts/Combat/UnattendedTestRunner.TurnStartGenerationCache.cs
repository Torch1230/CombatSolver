using Godot;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Unlocks;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static string AssertTurnStartGenerationCacheContract(CombatState live, Player player)
    {
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        CombatPredictionSimulator parent = root.ForkSimulator();
        var state = (SimulatedCombatState)parent.State.CombatState;
        var pools = (ICombatPredictionCardGenerationPoolSnapshot)state;
        CardMultiplayerConstraint constraint = state.CardMultiplayerConstraint;
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string parentBefore = Stamp(parent);
        CardPoolModel pool = player.Character.CardPool;
        int comparisons = 0;
        foreach (CharacterCombatGenerationPool selection in Enum.GetValues<CharacterCombatGenerationPool>())
        {
            IEnumerable<CardModel> OriginalOptions()
            {
                IEnumerable<CardModel> unlocked = pool.GetUnlockedCards(player.UnlockState, constraint);
                return selection switch
                {
                    CharacterCombatGenerationPool.NonBasicAndAncient => unlocked.Where(
                        card => card.Rarity is not (CardRarity.Basic or CardRarity.Ancient)),
                    CharacterCombatGenerationPool.Powers => unlocked.Where(card => card.Type == CardType.Power),
                    CharacterCombatGenerationPool.Common => unlocked.Where(card => card.Rarity == CardRarity.Common),
                    _ => throw new ArgumentOutOfRangeException(),
                };
            }
            if (!pools.TryGetRootEligibleCharacterCards(player, pool, constraint, selection, out var eligible)
                || !eligible.SequenceEqual(OriginalOptions().FilterForCombatAndPlayerCount(constraint),
                    ReferenceEqualityComparer.Instance))
                throw new InvalidOperationException("Turn-start generation pool changed ordered canonical identities.");
            CombatPredictionSimulator child = parent.Fork();
            var childPools = (ICombatPredictionCardGenerationPoolSnapshot)child.State.CombatState;
            if (!childPools.TryGetRootEligibleCharacterCards(player, pool, constraint, selection, out var shared)
                || !ReferenceEquals(eligible, shared))
                throw new InvalidOperationException("Turn-start generation pool was not shared immutably across Fork.");
            if (pools.TryGetRootEligibleCharacterCards(player, pool.ToMutable(), constraint, selection, out _)
                || pools.TryGetRootEligibleCharacterCards(player, ModelDb.CardPool<ModCharacterPoolProbe>(),
                    constraint, selection, out _)
                || pools.TryGetRootEligibleCharacterCards(player, pool,
                    CardMultiplayerConstraint.MultiplayerOnly, selection, out _))
                throw new InvalidOperationException("Turn-start generation pool accepted a changed pool or constraint.");
            // A mutable third-party pool can observe unlock filtering. Preserve one
            // eager GetUnlockedCards call per trigger even when generating three times.
            var beforePool = (ChangingTurnStartPoolProbe)ModelDb.CardPool<ChangingTurnStartPoolProbe>().ToMutable();
            var afterPool = (ChangingTurnStartPoolProbe)ModelDb.CardPool<ChangingTurnStartPoolProbe>().ToMutable();
            beforePool.Options = afterPool.Options = pool.GetUnlockedCards(player.UnlockState, constraint).ToArray();
            CombatPredictionSimulator fallbackBefore = parent.Fork(), fallbackAfter = parent.Fork();
            IEnumerable<CardModel> originalUnlocked = beforePool.GetUnlockedCards(player.UnlockState, constraint);
            IEnumerable<CardModel> originalFiltered = selection switch
            {
                CharacterCombatGenerationPool.NonBasicAndAncient => originalUnlocked.Where(
                    card => card.Rarity is not (CardRarity.Basic or CardRarity.Ancient)),
                CharacterCombatGenerationPool.Powers => originalUnlocked.Where(card => card.Type == CardType.Power),
                CharacterCombatGenerationPool.Common => originalUnlocked.Where(card => card.Rarity == CardRarity.Common),
                _ => throw new ArgumentOutOfRangeException(),
            };
            var fallbackCandidates = fallbackAfter.PrepareCharacterGenerationCandidates(
                player, afterPool, selection, constraint);
            if (beforePool.UnlockReads != 1 || afterPool.UnlockReads != 1)
                throw new InvalidOperationException("Turn-start fallback changed eager unlock filtering.");
            for (int index = 0; index < 3; index++)
            {
                PredictedCard expected = originalFiltered.GetDistinctForCombat(
                    player, 1, fallbackBefore.Rng.CombatCardGeneration, constraint).Single();
                PredictedCard actual = fallbackCandidates.GetDistinctForCombat(
                    player, 1, fallbackAfter.Rng.CombatCardGeneration).Single();
                if (expected.Preview.Id != actual.Preview.Id
                    || !SameFiveFieldRngState(fallbackBefore.Rng.CombatCardGeneration.CaptureState(),
                        fallbackAfter.Rng.CombatCardGeneration.CaptureState())
                    || ReferenceEquals(expected.Original, actual.Original))
                    throw new InvalidOperationException("Turn-start fallback changed generated cards or RNG.");
            }
            if (beforePool.UnlockReads != 1 || afterPool.UnlockReads != 1)
                throw new InvalidOperationException("Turn-start fallback repeated unlock filtering within one trigger.");
            foreach (int count in new[] { 0, 1, 3 })
            foreach (int skip in new[] { 0, 1, 17 })
            {
                CombatPredictionSimulator before = parent.Fork(), after = parent.Fork();
                var beforeCombat = (SimulatedCombatState)before.State.CombatState;
                var afterCombat = (SimulatedCombatState)after.State.CombatState;
                Type powerType = selection switch
                {
                    CharacterCombatGenerationPool.NonBasicAndAncient => typeof(CallOfTheVoidPower),
                    CharacterCombatGenerationPool.Powers => typeof(CreativeAiPower),
                    CharacterCombatGenerationPool.Common => typeof(HelloWorldPower),
                    _ => throw new ArgumentOutOfRangeException(),
                };
                beforeCombat.ApplyPower(powerType, player.Creature, count);
                afterCombat.ApplyPower(powerType, player.Creature, count);
                beforeCombat.SnapshotPowerAmountsAtTurnStart([player.Creature]);
                afterCombat.SnapshotPowerAmountsAtTurnStart([player.Creature]);
                before.Rng.CombatCardGeneration.Advance(skip);
                after.Rng.CombatCardGeneration.Advance(skip);
                // Frozen original call-site pipeline: one TakeRandom(1) per amount for
                // CallOfTheVoid/CreativeAi; HelloWorld takes the whole distinct set once.
                if (count > 0)
                {
                    IEnumerable<CardModel> originalOptions = OriginalOptions();
                    List<PredictedCard> generated = [];
                    if (selection == CharacterCombatGenerationPool.Common)
                        generated = originalOptions.GetDistinctForCombat(
                            player, count, before.Rng.CombatCardGeneration, constraint).ToList();
                    else
                        for (int index = 0; index < count; index++)
                        {
                            PredictedCard? card = originalOptions.GetDistinctForCombat(
                                player, 1, before.Rng.CombatCardGeneration, constraint).FirstOrDefault();
                            if (card != null) generated.Add(card);
                        }
                    if (selection == CharacterCombatGenerationPool.NonBasicAndAncient)
                        foreach (PredictedCard card in generated)
                            card.MutablePreview.AddKeyword(CardKeyword.Ethereal);
                    before.AddGeneratedCardsToCombat(generated, PileType.Hand, player,
                        CardPilePosition.Bottom, CardGenerationResultKind.Random);
                }
                if (TurnStartPowerSupport.TriggerBeforeHandDraw(after, afterCombat, player, new(null)))
                    throw new InvalidOperationException("Turn-start generation unexpectedly opened a choice.");
                if (Stamp(before) != Stamp(after)
                    || !SameFiveFieldRngState(before.Rng.CombatCardGeneration.CaptureState(),
                        after.Rng.CombatCardGeneration.CaptureState())
                    || !before.History.Select(entry => entry.GetType()).SequenceEqual(
                        after.History.Select(entry => entry.GetType())))
                    throw new InvalidOperationException("Turn-start pool cache changed full state, RNG, or history order.");
                var beforeCards = before.State.GetPlayerCombatState(player).Hand.Cards;
                var afterCards = after.State.GetPlayerCombatState(player).Hand.Cards;
                for (int index = parent.State.GetPlayerCombatState(player).Hand.Cards.Count;
                     index < beforeCards.Count; index++)
                    if (ReferenceEquals(beforeCards[index], afterCards[index])
                        || ReferenceEquals(beforeCards[index].Original, afterCards[index].Original))
                        throw new InvalidOperationException("Turn-start generation shared mutable cards.");
                comparisons++;
            }
        }
        if (Stamp(parent) != parentBefore || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
            throw new InvalidOperationException("Turn-start generation changed its parent or live root.");
        return $"TurnStartGenerationCache:comparisons={comparisons}:ordered_identity=true:fork_shared_pool=true:mutable_mod_constraint_bypass=true:fallback_unlock_reads_once=true:full_state_rng_history=true:generated_cards_independent=true:parent_live_unchanged=true";
    }

    private sealed class ChangingTurnStartPoolProbe : CardPoolModel
    {
        public CardModel[] Options { get; set; } = [];
        public int UnlockReads { get; private set; }
        public override string Title => "combat_solver_turn_start_pool_probe";
        public override string EnergyColorName => "colorless";
        public override string CardFrameMaterialPath => "card_frame_colorless";
        public override Color DeckEntryCardColor => Colors.White;
        public override bool IsColorless => false;
        protected override CardModel[] GenerateAllCards() => [];
        protected override IEnumerable<CardModel> FilterThroughEpochs(
            UnlockState unlockState, IEnumerable<CardModel> cards)
        {
            int shift = UnlockReads++ % Options.Length;
            return Options.Skip(shift).Concat(Options.Take(shift));
        }
    }

}
