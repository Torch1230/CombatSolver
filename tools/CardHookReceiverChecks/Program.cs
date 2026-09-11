using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

bool legacy = args.Contains("--legacy");
int checks = 0;
void Require(bool value, string message)
{
    checks++;
    if (!value) throw new InvalidOperationException(message);
}
foreach (bool fork in new[] { false, true })
foreach (int size in new[] { 1, 3, 8 })
{
    var originals = Enumerable.Range(0, size).Select(_ => new CardModel()).ToArray();
    SimCardPile parent = new(PileType.Hand, originals.Select(c => new PredictedCard(c)));
    // Existing previews, as after drawing cards under Chains of Binding.
    foreach (PredictedCard c in parent) c.MutablePreview.Bound = true;
    SimCardPile hand = fork ? parent.Fork(new()) : parent;
    AbstractModel power = new();
    var frozen = hand.Cards.Select(c => c.Preview).ToArray();
    var receivers = hand.Cards.Select(c => new CardHookReceiver(c.Preview, c)).ToArray();
    CardHookReceiver powerReceiver = new(power, null);
    // The earlier power clears afflictions, obtaining writable branch previews.
    foreach (PredictedCard c in hand) c.MutablePreview.Bound = false;
    for (int i = 0; i < receivers.Length; i++)
    {
        CardModel current = legacy ? frozen[i] : (CardModel)receivers[i].Current;
        if (hand.Find(current) is { } card) card.MutablePreview.CardsInHand = hand.Cards.Count;
    }
    for (int i = 0; i < size; i++)
    {
        Require(hand.Cards[i].Preview.CardsInHand == size,
            $"Regret missed its hand snapshot after Bound cleanup: fork={fork}, hand={size}.");
        Require(!originals[i].Bound && originals[i].CardsInHand == 0, "Live identity mutated.");
        if (fork)
            Require(parent.Cards[i].Preview.Bound && parent.Cards[i].Preview.CardsInHand == 0,
                "Child changed parent state.");
    }
    Require(ReferenceEquals(powerReceiver.Current, power), "Non-card receiver changed.");
    // Membership stays frozen: a newly added card must not acquire a slot in this dispatch.
    PredictedCard added = new(new CardModel());
    hand.Add(added);
    Require(receivers.Length == size && added.Preview.CardsInHand == 0, "New listener joined an in-flight snapshot.");
    // A moved card retains its exact identity, but a hand-only handler must skip it.
    PredictedCard removed = hand.Cards[0];
    hand.Remove(removed);
    SimCardPile discard = new(PileType.Discard, new[] { removed });
    removed.MutablePreview.CardsInHand = 0;
    Require(hand.Find((CardModel)receivers[0].Current) == null
        && ReferenceEquals(discard.Find((CardModel)receivers[0].Current), removed), "Moved card matched a same-type sibling.");
}
Console.WriteLine($"CARD_HOOK_RECEIVER_CHECKS_OK checks={checks}");
