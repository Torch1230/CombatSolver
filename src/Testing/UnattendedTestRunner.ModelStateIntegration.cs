using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Modifiers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class IntegrationModelState(int count, PredictedCard card) : IPredictionStateForkable
    {
        public int Count = count;
        public List<int> Values = [count];
        public PredictedCard Card = card;
        public List<PredictedCard?> References = [card, null, card];

        public object Fork(PredictionForkContext context)
            => new IntegrationModelState(Count, context.RequireRemap(Card))
            {
                Values = [.. Values],
                References = PredictionCardReferences.Remap(References, context)!,
            };
    }

    // A dedicated fresh-process fixture registers before its first root. Production effects
    // stay native; the test state exercises ownership, observation and continuation plumbing.
    private static void RegisterModelStateIntegrationAdapters()
    {
        static IntegrationModelState Capture(CombatPredictionSimulator simulator, Player owner, int count)
        {
            var cards = simulator.State.GetPlayerCombatState(owner);
            return new(count, PredictionCardReferences.RequireCard(cards, cards.AllCards.First().Original));
        }
        static void WriteValues(int count, IReadOnlyList<int> values, ref ModelPredictionStateWriter writer)
        {
            writer.Add("counter", (long)count);
            writer.Add("length", (long)values.Count);
            foreach (int value in values) writer.Add("value", (long)value);
        }
        static void WriteState(IntegrationModelState state, ref ModelPredictionStateWriter writer)
            => WriteValues(state.Count, state.Values, ref writer);
        ModelPredictionStateMirrors.RegisterRelic<BurningBlood, IntegrationModelState>("integration-v1",
            (simulator, relic) => Capture(simulator, relic.Owner, relic.Owner.Gold),
            (BurningBlood relic, ref ModelPredictionStateWriter writer) => WriteValues(relic.Owner.Gold, [relic.Owner.Gold], ref writer),
            WriteState);
        ModelPredictionStateMirrors.RegisterModifier<BigGameHunter, IntegrationModelState>("integration-v1",
            (simulator, _) => Capture(simulator, simulator.State.CombatState.Players.Single(), 1),
            (BigGameHunter _, ref ModelPredictionStateWriter writer) => WriteValues(1, [1], ref writer), WriteState);
    }

    private async Task AssertModelStateIntegrationAsync(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator parent = root.ForkSimulator();
        CombatPredictionSimulator child = parent.Fork();
        var parentCombat = (SimulatedCombatState)parent.State.CombatState;
        var childCombat = (SimulatedCombatState)child.State.CombatState;
        static StateFingerprint Fingerprint(CombatPredictionSimulator simulator)
        {
            StateFingerprintBuilder builder = new();
            ((SimulatedCombatState)simulator.State.CombatState).AppendFingerprint(ref builder, simulator);
            return builder.Finish();
        }
        string Stamp(CombatPredictionSimulator simulator) => ContinuationStamp.CapturePredicted(
            player, simulator, root.StartTurnNumber, root.Forecast, root.StartTurnNumber).StateText;
        StateFingerprint initial = Fingerprint(parent);
        string initialStamp = Stamp(parent);
        AssertCardReferenceObservation(combat, player, parent, child);
        if (initial != Fingerprint(child) || initialStamp != Stamp(child))
            throw new InvalidOperationException("Full simulator fork changed registered model state.");
        AbstractModel[] models = [parentCombat.RelicsOf(player).OfType<BurningBlood>().Single(), parentCombat.Modifiers.OfType<BigGameHunter>().Single()];
        AbstractModel[] childModels = [childCombat.RelicsOf(player).OfType<BurningBlood>().Single(), childCombat.Modifiers.OfType<BigGameHunter>().Single()];
        for (int i = 0; i < models.Length; i++)
        {
            IntegrationModelState before = ModelPredictionStateMirrors.Get<IntegrationModelState>(parent, models[i]);
            IntegrationModelState after = ModelPredictionStateMirrors.Get<IntegrationModelState>(child, childModels[i]);
            if (ReferenceEquals(before, after) || ReferenceEquals(before.Values, after.Values)
                || ReferenceEquals(before.Card, after.Card)
                || ReferenceEquals(before.References, after.References)
                || !after.References.SequenceEqual(new PredictedCard?[] { after.Card, null, after.Card })
                || !child.State.GetPlayerCombatState(player).AllCards.Any(card => ReferenceEquals(card, after.Card)))
                throw new InvalidOperationException("Registered model state retained a parent branch reference.");
            after.Count++;
            after.Values[0]++;
            if (Fingerprint(child) == initial || Stamp(child) == initialStamp
                || Fingerprint(parent) != initial || Stamp(parent) != initialStamp)
                throw new InvalidOperationException("Model state mutation lost fingerprint/continuation identity or leaked to parent.");
            after.Count--;
            after.Values[0]--;
        }
        _completedChecks.Add("RegisteredRelicAndModifier:FullSimulatorFork:CardRemapping:MutableIsolation:FingerprintAndContinuation");
        await AssertReportRoundAsync(combat, player);
        _completedChecks.Add("RegisteredModelState:NativeVsPredictedTurn1To2:FullSnapshotAndContinuation");
    }

    private static void AssertCardReferenceObservation(CombatState live, Player player,
        CombatPredictionSimulator parent, CombatPredictionSimulator child)
    {
        var parentCards = parent.State.GetPlayerCombatState(player);
        var childCards = child.State.GetPlayerCombatState(player);
        PredictedCard before = parentCards.AllCards.First();
        PredictedCard after = PredictionCardReferences.RequireCard(childCards, before.Original);
        System.Text.StringBuilder actual = new(), predicted = new();
        ModelPredictionStateWriter actualWriter = new(new(), actual), predictedWriter = new(new(), predicted);
        actualWriter.BindCardReferences(live, null);
        predictedWriter.BindCardReferences(child.State.CombatState, child);
        actualWriter.AddCards("references", new CardModel?[] { before.Original, null, before.Original });
        predictedWriter.AddCards("references", new PredictedCard?[] { after, null, after });
        if (actual.ToString() != predicted.ToString())
            throw new InvalidOperationException("Live/predicted card reference positions differ.");
        CardModel parentPreview = before.Preview;
        CardModel childPreview = after.MutablePreview;
        if (ReferenceEquals(parentPreview, childPreview)
            || PredictionCardReferences.RequireCard(childCards, childPreview) != after
            || PredictionCardReferences.RequireCard(childCards, before.Original) != after)
            throw new InvalidOperationException("Card reference resolution failed after preview COW.");
    }
}
