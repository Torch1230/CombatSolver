using System.Reflection;
using HarmonyLib;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertProjectedHookReceiverConstruction(CombatState live, Player player)
    {
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        CombatPredictionSimulator parent = root.ForkSimulator();
        for (int index = 0; index < 300; index++)
            parent.AddToPile(PredictedCard.Create(ModelDb.Card<StrikeIronclad>(), player), PileType.Draw);
        parent.AddToPile(PredictedCard.Create(ModelDb.Card<Reflex>(), player), PileType.Draw);
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        CombatPredictionSimulator candidate = parent.Fork();
        CombatPredictionSimulator whole = parent.Fork();
        DisableMirroredFilterForOracle(whole);
        int comparisons = 0;
        int projectedBuilds = 0;
        var filter = new MirroredHookListenerFilter(enabled: true);

        void Compare(CombatPredictionSimulator simulator)
        {
            var source = (ICombatPredictionHookListenerSource)simulator.State.CombatState;
            // Request the projection before constructing the complete oracle snapshot.
            IReadOnlyList<AbstractModel> combat = source.MirroredHookListeners;
            IReadOnlyList<AbstractModel> run = source.MirroredRunHookListeners;
            IReadOnlyList<AbstractModel> complete = source.HookListeners;
            filter.VerifyProjectedReceivers(complete, combat);
            filter.VerifyProjectedReceivers(source.RunHookListeners, run);
            if (combat.Count < complete.Count / 2)
                projectedBuilds++;
            foreach (MirroredHookMask mask in Enum.GetValues<MirroredHookMask>())
            {
                AbstractModel[] Select(IReadOnlyList<AbstractModel> listeners)
                {
                    if (listeners is not MirroredHookListenerSnapshot snapshot)
                        return [];
                    return Enumerable.Range(0, snapshot.Count)
                        .Where(index => (snapshot.Layout.Entries[index].Mask & mask) != 0)
                        .Select(index => snapshot[index]).ToArray();
                }
                if (!Select(filter.Filter(complete)).SequenceEqual(Select(combat), ReferenceEqualityComparer.Instance)
                    || !Select(filter.Filter(source.RunHookListeners)).SequenceEqual(Select(run), ReferenceEqualityComparer.Instance))
                    throw new InvalidOperationException($"Projected dispatch differs for {mask}.");
            }
            comparisons++;
        }
        void ComparePair()
        {
            Compare(candidate);
            if (DescribeContinuationContractState(candidate, root, player)
                != DescribeContinuationContractState(whole, root, player))
                throw new InvalidOperationException("Projected listeners changed full state, fingerprint, history or RNG.");
        }
        void Mutate(Action<CombatPredictionSimulator, PredictedCard> action)
        {
            foreach (CombatPredictionSimulator simulator in new[] { candidate, whole })
                action(simulator, simulator.State.GetPlayerCombatState(player).DrawPile.TopCard!);
            ComparePair();
        }
        ComparePair();
        foreach (CombatPredictionSimulator simulator in new[] { candidate, whole })
        {
            PredictedCard attack = simulator.State.GetPlayerCombatState(player).Hand.Cards
                .First(card => card.Preview is StrikeIronclad);
            SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
            combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
            try
            {
                using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                    if (!simulator.ManualPlay(attack, root.Enemies[0], out _))
                        throw new InvalidOperationException("Projected listener fixture could not play its attack.");
                if (!CombatBeamSolver.SettleReplayActionBoundary(simulator, combat))
                    throw new InvalidOperationException("Projected listener fixture unexpectedly opened a choice.");
            }
            finally { combat.EndActionChoices(); }
        }
        ComparePair();
        Mutate((_, card) => card.MutablePreview.BaseReplayCount++);
        Mutate((_, card) => card.Afflict(ModelDb.Affliction<Hexed>().ToMutable(), 1));
        Mutate((_, card) => card.Enchant(ModelDb.Enchantment<Swift>().ToMutable(), 1));
        Mutate((_, card) => card.ClearAffliction());
        Mutate((simulator, card) => simulator.AddToPile(card, PileType.Discard, CardPilePosition.Random));
        Mutate((_, card) => card.MutablePreview.HasBeenRemovedFromState = true);
        foreach (CombatPredictionSimulator simulator in new[] { candidate, whole })
            ((SimulatedCombatState)simulator.State.CombatState).SetAmount<StrengthPower>(player.Creature, 3);
        ComparePair();
        CombatPredictionSimulator[] siblings = Enumerable.Range(0, 16).Select(_ => candidate.Fork()).ToArray();
        Parallel.ForEach(siblings, simulator =>
        {
            using IDisposable workerIsolation = SimulationNotificationIsolation.Enter();
            SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
            combat.SetAmount<StrengthPower>(player.Creature, 4);
            PredictedCard card = simulator.State.GetPlayerCombatState(player).DrawPile.TopCard!;
            card.MutablePreview.BaseReplayCount++;
            var source = (ICombatPredictionHookListenerSource)combat;
            IReadOnlyList<AbstractModel> projected = source.MirroredHookListeners;
            filter.VerifyProjectedReceivers(source.HookListeners, projected);
        });
        if (projectedBuilds == 0 && !FastLaneVerification.Enabled)
            throw new InvalidOperationException("Large listener fixture did not exercise projected construction.");
        if (DescribeContinuationContractState(parent, root, player) != parentBefore
            || DescribeContinuationContractState(candidate, root, player)
                != DescribeContinuationContractState(whole, root, player)
            || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
            throw new InvalidOperationException("Projected listeners escaped sibling, parent or live isolation.");
        AssertProjectedGetterPatchFallback(live, player);
        _completedChecks.Add($"ProjectedHookReceivers:Comparisons={comparisons}:ProjectedBuilds={projectedBuilds}:Masks=64:FullStateFingerprintHistoryRng:MovesRemovedAttachmentsPowerOrder:Fork16ParentLive:GetterPatchFallback");
    }

    private static void DisableMirroredFilterForOracle(CombatPredictionSimulator simulator)
    {
        // Clone immutable root metadata rather than changing the shared capture of siblings.
        // Only the dispatch optimisation is disabled; all captured policies/models stay identical.
        FieldInfo subscribers = typeof(SimulatedCombatState).GetField("_modHookSubscribers",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object original = subscribers.GetValue(simulator.State.CombatState)!;
        object copy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(original, null)!;
        typeof(PredictionModHookSubscriberCapture).GetField("<MirroredHookFilter>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(copy, new MirroredHookListenerFilter(enabled: false));
        subscribers.SetValue(simulator.State.CombatState, copy);
    }

    private static void AssertProjectedGetterPatchFallback(CombatState live, Player player)
    {
        MethodInfo getter = typeof(CardModel).GetProperty(nameof(CardModel.HasBeenRemovedFromState))!.GetMethod!;
        MethodInfo prefix = typeof(UnattendedTestRunner).GetMethod(nameof(ProjectedGetterPrefix),
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Harmony harmony = new("CombatSolver.Tests.ProjectedGetterGuard");
        try
        {
            harmony.Patch(getter, prefix: new HarmonyMethod(prefix));
            if (MirroredHookListenerFilter.Capture().CanProjectReceivers)
                throw new InvalidOperationException("A patched card getter retained projected construction.");
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(live).ForkSimulator();
            for (int index = 0; index < 300; index++)
                simulator.AddToPile(PredictedCard.Create(ModelDb.Card<StrikeIronclad>(), player), PileType.Draw);
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            combat.SetAmount<StrengthPower>(player.Creature, 3);
            var source = (ICombatPredictionHookListenerSource)combat;
            IReadOnlyList<AbstractModel> mirrored = source.MirroredHookListeners;
            if (mirrored.Count != source.HookListeners.Count)
                throw new InvalidOperationException("Getter-patched root did not use the complete producer.");
        }
        finally { harmony.Unpatch(getter, prefix); }
        if (!MirroredHookListenerFilter.Capture().CanProjectReceivers)
            throw new InvalidOperationException("Getter patch removal did not restore root projection eligibility.");
    }

    private static void ProjectedGetterPrefix() { }
}
