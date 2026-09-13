namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private readonly record struct TranspositionLabel(
        int PotionCount,
        int PotionStrategicCost,
        int FutureSoldHp,
        int CumulativePlayerHpLost,
        int ActionCount,
        double Score);

    private sealed class TranspositionFrontier(TranspositionLabel first)
    {
        // Most retained states have one nondominated label. Keep it in this object:
        // neither an empty tail nor a one-element List/array needs to survive a GC.
        private TranspositionLabel _single = first;
        private List<TranspositionLabel>? _labels;

        public bool TryAccept(TranspositionLabel next)
        {
            if (_labels == null)
            {
                if (Dominates(_single, next))
                    return false;
                if (Dominates(next, _single))
                    _single = next;
                else
                    _labels = [_single, next];
                return true;
            }
            foreach (TranspositionLabel current in _labels)
            {
                if (Dominates(current, next))
                    return false;
            }
            _labels.RemoveAll(current => Dominates(next, current));
            _labels.Add(next);
            if (_labels.Count == 1)
            {
                _single = next;
                _labels = null;
            }
            return true;
        }

        private static bool Dominates(TranspositionLabel left, TranspositionLabel right)
            => left.PotionCount <= right.PotionCount
                && left.PotionStrategicCost <= right.PotionStrategicCost
                && left.FutureSoldHp <= right.FutureSoldHp
                && left.CumulativePlayerHpLost <= right.CumulativePlayerHpLost
                && left.ActionCount <= right.ActionCount
                && left.Score >= right.Score;
    }

}
