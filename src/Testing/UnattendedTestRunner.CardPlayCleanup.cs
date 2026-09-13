using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertCardPlayCleanupContract(CombatState combat, Player player)
    {
        CardModel card = player.PlayerCombatState!.Hand.Cards.First();
        CombatPredictionSimulator simulator = new(new SimulatedCombatState(combat));
        CardPlay Play(int index) => new()
        {
            Card = card, Player = player, Target = null, ResultPile = PileType.Discard,
            Resources = default, IsAutoPlay = false, PlayIndex = index, PlayCount = 1
        };
        CardPlay first = Play(0), other = Play(1);
        BeforeCardPlayedMirrors.CompleteOrAbort(simulator, first);
        AfterCardPlayedMirrors.CompleteOrAbort(simulator, first, completed: true);
        AfterCardPlayedMirrors.CompleteOrAbort(simulator, first, completed: false);
        if (simulator.StateStore.HasEntries<CardPlayPairPredictionState>()
            || simulator.StateStore.HasEntries<PaelsLegionPredictionState>())
            throw new InvalidOperationException("Empty card cleanup inserted prediction state.");

        CardPlayPairPredictionState pair = simulator.StateStore.Get<CardPlayPairPredictionState>(card);
        pair.Amounts.Add(first, 7);
        pair.Amounts.Add(other, 9);
        BeforeCardPlayedMirrors.CompleteOrAbort(simulator, first);
        if (pair.Amounts.Count != 1 || pair.Amounts[other] != 9)
            throw new InvalidOperationException("Paired card cleanup changed another CardPlay.");
        BeforeCardPlayedMirrors.CompleteOrAbort(simulator, other);
        if (pair.Amounts.Count != 0)
            throw new InvalidOperationException("Paired card cleanup left pending state.");
        simulator.StateStore.Remove<CardPlayPairPredictionState>(card);
        BeforeCardPlayedMirrors.CompleteOrAbort(simulator, first);
        if (simulator.StateStore.HasEntries<CardPlayPairPredictionState>())
            throw new InvalidOperationException("Removed pair state was recreated by cleanup.");

        PaelsLegion relic = ModelDb.All.OfType<PaelsLegion>().Single();
        PaelsLegionPredictionState state = simulator.StateStore.Get(
            relic, static value => new PaelsLegionPredictionState(value));
        state.AffectedCardPlay = other;
        state.Cooldown = 3;
        state.TriggeredBlockLastTurn = false;
        AfterCardPlayedMirrors.CompleteOrAbort(simulator, first, completed: true);
        if (!ReferenceEquals(state.AffectedCardPlay, other) || state.Cooldown != 3 || state.TriggeredBlockLastTurn)
            throw new InvalidOperationException("Relic cleanup changed another CardPlay.");
        AfterCardPlayedMirrors.CompleteOrAbort(simulator, other, completed: false);
        if (state.AffectedCardPlay != null || state.Cooldown != 3 || state.TriggeredBlockLastTurn)
            throw new InvalidOperationException("Aborted relic cleanup committed a trigger.");
        state.AffectedCardPlay = first;
        AfterCardPlayedMirrors.CompleteOrAbort(simulator, first, completed: true);
        if (state.AffectedCardPlay != null || state.Cooldown != relic.DynamicVars["Turns"].IntValue
            || !state.TriggeredBlockLastTurn)
            throw new InvalidOperationException("Completed relic cleanup failed to commit.");

        CombatPredictionSimulator child = simulator.Fork();
        child.StateStore.Remove<PaelsLegionPredictionState>(relic);
        AfterCardPlayedMirrors.CompleteOrAbort(child, first, completed: true);
        if (child.StateStore.HasEntries<PaelsLegionPredictionState>()
            || !simulator.StateStore.HasEntries<PaelsLegionPredictionState>()
            || state.Cooldown != relic.DynamicVars["Turns"].IntValue || !state.TriggeredBlockLastTurn)
            throw new InvalidOperationException("Removed child state leaked into its parent.");
    }
}
