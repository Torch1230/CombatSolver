using System.Text;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

internal static class CardReferenceChecks
{
    private sealed class ReferenceRelic : RelicModel { public List<CardModel?> Cards = []; }
    private sealed class ReferenceModifier : ModifierModel { public List<CardModel?> Cards = []; }
    private sealed class ReferenceState(List<PredictedCard?>? cards) : IPredictionStateForkable
    {
        public List<PredictedCard?>? Cards = cards;
        public object Fork(PredictionForkContext context) => new ReferenceState(PredictionCardReferences.Remap(Cards, context));
    }

    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException) { checks++; return; }
            throw new InvalidOperationException(message);
        }
        Player player = new(17);
        CardModel a = new(), b = new() { Upgrade = 1 }, preview = new();
        PredictedCard pa = new(a, preview), pb = new(b);
        player.PlayerCombatState.Hand.Cards.AddRange([a, b]);
        SimPlayerCombatState piles = new();
        piles.AllPiles[0].Cards.AddRange([pa, pb]);
        CombatPredictionSimulator parent = new();
        parent.State.Players.Add(player, piles);
        ReferenceRelic relic = new() { Cards = [a, null, b, a] };
        ReferenceModifier modifier = new() { Cards = [b, a] };
        player.Relics.Add(relic);
        SimulatedCombatState combat = new([player], [modifier]);
        static ReferenceState Capture(CombatPredictionSimulator simulator, Player player, List<CardModel?> cards)
            => new(cards.Select(card => card is null ? null : PredictionCardReferences.RequireCard(
                simulator.State.GetPlayerCombatState(player), card)).ToList());
        static void Write(ReferenceState state, ref ModelPredictionStateWriter writer) => writer.AddCards("cards", state.Cards);
        ModelPredictionStateMirrors.RegisterRelic<ReferenceRelic, ReferenceState>("card-references-v1",
            (sim, value) => Capture(sim, player, value.Cards),
            (ReferenceRelic value, ref ModelPredictionStateWriter writer) => writer.AddCards("cards", value.Cards), Write);
        ModelPredictionStateMirrors.RegisterModifier<ReferenceModifier, ReferenceState>("card-references-v1",
            (sim, value) => Capture(sim, player, value.Cards),
            (ReferenceModifier value, ref ModelPredictionStateWriter writer) => writer.AddCards("cards", value.Cards), Write);
        ModelPredictionStateMirrors.CaptureRootState(parent, relic, relic);
        ModelPredictionStateMirrors.CaptureRootState(parent, modifier, modifier);
        (StateFingerprint, string) Observe(CombatPredictionSimulator simulator)
        {
            StateFingerprintBuilder builder = new();
            StringBuilder text = new();
            ModelPredictionStateMirrors.AppendPredicted(ref builder, text, simulator, combat);
            return (builder.Finish(), text.ToString());
        }
        string Live()
        {
            StringBuilder text = new();
            ModelPredictionStateMirrors.AppendLiveContinuation(text, combat);
            return text.ToString();
        }
        var initial = Observe(parent);
        Check(initial.Item2 == Live(), "Root live/predicted reference state differs.");
        Check(PredictionCardReferences.RequireCard(piles, a) == pa, "Original identity lost.");
        Check(PredictionCardReferences.RequireCard(piles, preview) == pa, "Preview identity lost.");
        Check(PredictionCardReferences.RequireCard(piles, b) == pb, "Same-ID instances conflated.");
        Reject(() => PredictionCardReferences.RequireCard(piles, new CardModel()), "Absent card resolved by type.");
        Reject(() => PredictionCardReferences.RequireCard(new[] { pa, new PredictedCard(a) }, a), "Ambiguous identity accepted.");
        Reject(() => PredictionCardReferences.Remap([pa], new()), "Missing fork mapping accepted.");
        Check(PredictionCardReferences.Remap(null, new()) is null, "Null list changed.");
        Check(PredictionCardReferences.Remap([], new())!.Count == 0, "Empty list changed.");
        CombatPredictionSimulator Fork(out PredictedCard ca, out PredictedCard cb)
        {
            PredictionForkContext context = new();
            ca = new(a, new CardModel()); cb = new(b, new CardModel());
            context.Register(pa, ca); context.Register(pb, cb);
            CombatPredictionSimulator child = new(parent.StateStore.Fork(context));
            SimPlayerCombatState childPiles = new();
            childPiles.AllPiles[0].Cards.AddRange([ca, cb]);
            child.State.Players.Add(player, childPiles);
            return child;
        }
        var child = Fork(out var childA, out var childB);
        var sibling = Fork(out var siblingA, out _);
        var childState = ModelPredictionStateMirrors.Get<ReferenceState>(child, relic);
        Check(Observe(child) == initial && Observe(sibling) == initial, "Fork changed identity.");
        Check(childState.Cards!.SequenceEqual(new PredictedCard?[] { childA, null, childB, childA }), "Null/multiplicity/order lost on fork.");
        Check(!ReferenceEquals(childState.Cards, ModelPredictionStateMirrors.Get<ReferenceState>(parent, relic).Cards), "List alias retained.");
        Check(childA != pa && childA != siblingA, "Sibling wrappers aliased.");
        childState.Cards![0] = childB;
        Check(Observe(child) != initial, "Reference change absent from fingerprint/continuation.");
        Check(Observe(parent) == initial && Observe(sibling) == initial, "Child mutation leaked.");
        relic.Cards.Clear(); a.Upgrade = 8;
        Check(Observe(parent) == initial, "Live mutation leaked into root references.");

        (StateFingerprint, string) Describe(IReadOnlyList<PredictedCard?>? cards, bool unordered = false)
        {
            StringBuilder text = new();
            ModelPredictionStateWriter writer = new(new(), text);
            writer.BindCardReferences(combat, parent);
            writer.AddCards("cards", cards, unordered);
            return (writer.Fingerprint.Finish(), text.ToString());
        }
        Check(Describe([pa, pb]) != Describe([pb, pa]), "Ordered reference list collapsed.");
        Check(Describe([pa, pb], true) == Describe([pb, pa], true), "Unordered list depends on enumeration order.");
        Check(Describe([pa, null, pa], true) == Describe([null, pa, pa], true), "Unordered null/multiplicity unstable.");
        Check(Describe([pa, pa], true) != Describe([pa], true), "Duplicate references collapsed.");
        Check(Describe(null) != Describe([]) && Describe([]) != Describe([null]), "Null/empty/null-element collapsed.");
        Check(Describe([pa]) != Describe([pb]), "Same-ID instance references collapsed.");
        Check(Describe([pa]) != Describe([pa], true), "Collection semantics not encoded.");
        Reject(() => Describe([childA]), "Foreign branch reference accepted.");
        piles.AllPiles[0].Cards.Remove(pa);
        Reject(() => Describe([pa]), "Removed reference silently described.");
        piles.AllPiles[0].Cards.Insert(0, pa);
        var beforeSwap = Describe([pa]);
        piles.AllPiles[0].Cards.Reverse();
        Check(Describe([pa]) != beforeSwap, "Pile movement did not alter reference relation.");
        ModelPredictionStateWriter wrongSide = new(new());
        wrongSide.BindCardReferences(combat, parent);
        Reject(() => wrongSide.AddCard("card", a), "Predicted writer accepted live model.");
        wrongSide.BindCardReferences(combat, null);
        Reject(() => wrongSide.AddCard("card", pa), "Live writer accepted predicted wrapper.");
        Console.WriteLine($"MODEL_CARD_REFERENCES_OK checks={checks}");
    }
}
