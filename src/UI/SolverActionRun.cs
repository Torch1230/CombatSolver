namespace CombatSolver;

/// <summary>A display range; executable actions stay flat and keep their original indexes.</summary>
internal readonly record struct SolverActionRun(int Start, int Period, int Repetitions)
{
    public int Count => Period * Repetitions;
    public int End => Start + Count;
    public bool IsActive(int offset, int? activeIndex)
        => activeIndex is { } index && index >= Start && index < End
            && (index - Start) % Period == offset;
    public bool IsCompleted(int offset, int completedCount)
        => Start + (Repetitions - 1) * Period + offset < completedCount;
}

internal static class SolverActionRuns
{
    // Bounded period search; compare full values, never hash equality alone.
    public static IReadOnlyList<SolverActionRun> Capture<T>(IReadOnlyList<T> actions, Func<T, T, bool> equals)
    {
        List<SolverActionRun> runs = [];
        for (int start = 0; start < actions.Count;)
        {
            SolverActionRun best = new(start, 1, 1);
            int bestSaved = 0;
            for (int period = 1; period <= 32 && start + 3 * period <= actions.Count; period++)
            {
                int matched = period;
                while (start + matched < actions.Count
                    && equals(actions[start + matched % period], actions[start + matched]))
                    matched++;
                int repeats = matched / period;
                int saved = period * (repeats - 1) - 1; // include the repetition badge
                if (repeats >= 3 && saved > bestSaved)
                {
                    best = new(start, period, repeats);
                    bestSaved = saved;
                }
            }
            runs.Add(best);
            start = best.End;
        }
        return runs;
    }
}
