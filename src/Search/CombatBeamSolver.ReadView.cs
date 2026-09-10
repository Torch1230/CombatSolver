using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private StateFingerprint BuildPileValuesFingerprint(IReadOnlyList<PredictedCard> cards)
    {
        StateFingerprintBuilder key = new();
        key.Add(cards.Count);
        for (int index = 0; index < cards.Count; index++)
        {
            PredictedCard card = cards[index];
            StateFingerprint item = BuildCardStateFingerprint(card);
            key.Add(item.First);
            key.Add(item.Second);
        }
        return key.Finish();
    }

    private void AppendPileValues(ref StateFingerprintBuilder key, IReadOnlyList<PredictedCard> cards, char marker)
    {
        key.Add(marker);
        key.Add(cards.Count);
        StateFingerprint pile = BuildPileValuesFingerprint(cards);
        key.Add(pile.First);
        key.Add(pile.Second);
    }

    private static StateFingerprint BuildCyclePileShapeKey(IReadOnlyList<PredictedCard> hand,
        IReadOnlyList<PredictedCard> draw, IReadOnlyList<PredictedCard> discard, IReadOnlyList<PredictedCard> exhaust)
    {
        StateFingerprintBuilder key = new();
        AppendCyclePileValues(ref key, hand, 'H');
        AppendCyclePileValues(ref key, draw, 'D');
        AppendCyclePileValues(ref key, discard, 'C');
        AppendCyclePileValues(ref key, exhaust, 'X');
        return key.Finish();
    }

    private static (ulong First, ulong Second) CyclePileValues(IReadOnlyList<PredictedCard> cards)
    {
        ulong first = 0, second = 0;
        for (int index = 0; index < cards.Count; index++)
        {
            PredictedCard card = cards[index];
            StateFingerprintBuilder item = new();
            item.Add(card.Preview.Id.Entry);
            item.Add(card.Preview.CurrentUpgradeLevel);
            StateFingerprint value = item.Finish();
            first += StateFingerprintBuilder.MixFirst(value.First);
            second += StateFingerprintBuilder.MixSecond(value.Second);
        }
        return (first, second);
    }

    private static void AppendCyclePileValues(ref StateFingerprintBuilder key, IReadOnlyList<PredictedCard> cards, char marker)
    {
        var (first, second) = CyclePileValues(cards);
        key.Add(marker);
        key.Add(cards.Count);
        key.Add(first);
        key.Add(second);
    }
}
