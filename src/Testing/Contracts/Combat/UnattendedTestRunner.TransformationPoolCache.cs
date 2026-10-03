using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    /// <summary>
    /// Contract for the root transform-pool cache: the cached sequence must be the upstream
    /// unlock-filtered order, must be shared immutably across Fork, must reject every changed
    /// pool/constraint/player, and the cached path must produce the same card and advance the
    /// same RNG as the uncached <c>CardFactory</c> path.
    /// </summary>
    private static string AssertTransformationPoolCacheContract(CombatState live, Player player)
    {
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        CombatPredictionSimulator parent = root.ForkSimulator();
        var state = (SimulatedCombatState)parent.State.CombatState;
        var pools = (ICombatPredictionCardGenerationPoolSnapshot)state;
        CardMultiplayerConstraint constraint = state.CardMultiplayerConstraint;
        CardPoolModel pool = player.Character.CardPool;
        string Stamp(CombatPredictionSimulator sim) => ContinuationStamp.CapturePredicted(
            player, sim, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        string parentBefore = Stamp(parent);

        // Ordered identity: exactly the upstream unlock-filtered sequence, same order, same instances.
        if (!pools.TryGetRootUnlockedTransformationCards(player, pool, constraint, out var cached)
            || !cached.SequenceEqual(
                pool.GetUnlockedCards(player.UnlockState, constraint),
                ReferenceEqualityComparer.Instance))
        {
            throw new InvalidOperationException(
                "Transformation pool changed ordered canonical identities.");
        }

        // Shared immutably across Fork.
        CombatPredictionSimulator child = parent.Fork();
        var childPools = (ICombatPredictionCardGenerationPoolSnapshot)child.State.CombatState;
        if (!childPools.TryGetRootUnlockedTransformationCards(player, pool, constraint, out var shared)
            || !ReferenceEquals(cached, shared))
        {
            throw new InvalidOperationException(
                "Transformation pool was not shared immutably across Fork.");
        }

        // A mutable pool, a foreign constraint, or a foreign pool must not be served from the root cache.
        if (pools.TryGetRootUnlockedTransformationCards(player, pool.ToMutable(), constraint, out _))
        {
            throw new InvalidOperationException(
                "Transformation pool accepted a mutable pool.");
        }

        CardMultiplayerConstraint foreignConstraint =
            constraint == CardMultiplayerConstraint.MultiplayerOnly
                ? CardMultiplayerConstraint.SingleplayerOnly
                : CardMultiplayerConstraint.MultiplayerOnly;
        if (pools.TryGetRootUnlockedTransformationCards(player, pool, foreignConstraint, out _))
        {
            throw new InvalidOperationException(
                "Transformation pool accepted a foreign constraint.");
        }

        if (pools.TryGetRootUnlockedTransformationCards(
                player, ModelDb.CardPool<ModCharacterPoolProbe>(), constraint, out _))
        {
            throw new InvalidOperationException(
                "Transformation pool accepted a foreign pool.");
        }

        // The canonical colorless pool is the Quest/Event/Ancient/Token fallback, so it IS served;
        // its cached sequence must still be the upstream order and instances.
        CardPoolModel colorlessPool = ModelDb.CardPool<ColorlessCardPool>();
        if (!ReferenceEquals(colorlessPool, pool)
            && pools.TryGetRootUnlockedTransformationCards(
                player, colorlessPool, constraint, out var colorlessCached)
            && !colorlessCached.SequenceEqual(
                colorlessPool.GetUnlockedCards(player.UnlockState, constraint),
                ReferenceEqualityComparer.Instance))
        {
            throw new InvalidOperationException(
                "Colorless transformation pool changed ordered canonical identities.");
        }

        // Cached path vs uncached CardFactory path: same card, same RNG advance, same full state.
        var hand = parent.State.GetPlayerCombatState(player).Hand.Cards;
        if (hand.Count == 0)
        {
            throw new InvalidOperationException(
                "Transformation pool contract needs at least one hand card in the fixture.");
        }

        int comparisons = 0;
        foreach (PredictedCard handCard in hand)
        {
            CardModel original = handCard.Preview;
            CombatPredictionSimulator nativeRun = parent.Fork(), cachedRun = parent.Fork();
            CardModel expected = CardFactory.CreateRandomCardForTransform(
                original, isInCombat: true, nativeRun.Rng.CombatCardSelection);
            CardModel actual = cachedRun.CreateRandomCardForTransform(
                original, isInCombat: true, cachedRun.Rng.CombatCardSelection);
            if (expected.Id != actual.Id)
            {
                throw new InvalidOperationException(
                    $"Transformation pool cache changed the selected card: {expected.Id} vs {actual.Id}.");
            }

            if (!SameFiveFieldRngState(
                    nativeRun.Rng.CombatCardSelection.CaptureState(),
                    cachedRun.Rng.CombatCardSelection.CaptureState()))
            {
                throw new InvalidOperationException(
                    "Transformation pool cache changed CombatCardSelection RNG consumption.");
            }

            if (Stamp(nativeRun) != Stamp(cachedRun))
            {
                throw new InvalidOperationException(
                    "Transformation pool cache changed the full predicted continuation state.");
            }

            comparisons++;
        }

        if (Stamp(parent) != parentBefore || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
        {
            throw new InvalidOperationException(
                "Transformation pool cache changed its parent or the live root.");
        }

        return $"TransformationPoolCache:comparisons={comparisons}:ordered_identity=true:"
            + "fork_shared_pool=true:mutable_colorless_constraint_bypass=true:"
            + "native_path_equivalent=true:rng_equivalent=true:parent_live_unchanged=true";
    }
}
