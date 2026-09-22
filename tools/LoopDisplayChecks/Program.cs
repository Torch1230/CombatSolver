using CombatSolver;

int checks = 0;
void Check(bool condition) { if (!condition) throw new Exception("Loop display mapping failed"); checks++; }
void Verify(int[] actions)
{
    var runs = SolverActionRuns.Capture(actions, static (a, b) => a == b);
    var restored = runs.SelectMany(r => Enumerable.Range(0, r.Count).Select(i => actions[r.Start + i % r.Period])).ToArray();
    Check(restored.SequenceEqual(actions));
    Check(runs.Sum(r => r.Count) == actions.Length);
    for (int active = 0; active < actions.Length; active++)
    {
        var mapped = runs.SelectMany(r => Enumerable.Range(0, r.Period).Where(i => r.IsActive(i, active)).Select(i => (r, i))).ToArray();
        Check(mapped.Length == 1);
        Check(actions[mapped[0].r.Start + mapped[0].i] == actions[active]);
        Check(!mapped[0].r.IsCompleted(mapped[0].i, active));
    }
    foreach (var r in runs)
        for (int i = 0; i < r.Period; i++)
        {
            Check(!r.IsActive(i, null));
            Check(r.IsCompleted(i, actions.Length));
        }
}
Verify([]); Verify([1]); Verify([1, 1]); Verify([1, 2, 1, 2, 9]);
Check(SolverActionRuns.Capture(new[] { 1, 2, 1, 2 }, static (a, b) => a == b)
    .All(run => run.Repetitions == 1));
Check(SolverActionRuns.Capture(new[] { 1, 2, 1, 2, 1, 2 }, static (a, b) => a == b)
    .Single() == new SolverActionRun(0, 2, 3));
int[] longLoop = Enumerable.Range(0, 1600).Select(i => i % 8).Append(99).ToArray();
Verify(longLoop);
var folded = SolverActionRuns.Capture(longLoop, static (a,b) => a == b);
Check(folded[0] == new SolverActionRun(0, 8, 200)); Check(folded.Count == 2);
Random random = new(921);
for (int sample = 0; sample < 500; sample++) Verify(Enumerable.Range(0, random.Next(100)).Select(_ => random.Next(5)).ToArray());
Console.WriteLine($"LOOP_DISPLAY_OK {checks} assertions; 1600 repeated actions -> 8 pills + badge, suffix preserved.");
