using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class MonsterAiChecks
{
    internal static void Run()
    {
        int[] next = [1, 2, 3, 1], rootLog = [0, 1, 2];
        var ai = new DeterministicMonsterAi(1, 2, next, rootLog);
        next[2] = 0; rootLog[2] = 0; // The captured graph owns its arrays.
        var block = new ResumableDiscardProgram.Card(0, new([new(CardInstructionKind.GainBlock, 1)]));
        var moves = Enumerable.Range(0, 4).Select(_ => new MonsterEffectProgram([])).ToArray();
        var lane = new ResumableDiscardProgram([block], [[0], [], [], [], []], 3, 0, 0,
            creatures: [new(50, 50, 0), new(50, 50, 0)], powers: [], monsterMoves: moves, monsterAi: ai);
        var root = lane.Freeze(); var mark = lane.State.Mark();
        for (int index = 0; index < 130; index++) lane.AdvanceMonsterMove(1);
        var middle = lane.Freeze();
        for (int index = 130; index < 300; index++) lane.AdvanceMonsterMove(1);
        if (lane.MonsterMoveLogCount != 303 || lane.CurrentMonsterMove != 2
            || !Enumerable.Range(0, 3).Select(lane.MonsterMoveLogAt).SequenceEqual([0, 1, 2])
            || Enumerable.Range(3, 300).Any(index => lane.MonsterMoveLogAt(index) != (index - 1) % 3 + 1))
            throw new InvalidOperationException("Captured graph, long log or deterministic cycle differs.");
        var final = lane.Freeze();
        lane.State.Rollback(mark);
        if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("AI selection failed to roll back with its log.");
        Parallel.For(0, 8, _ =>
        {
            var worker = middle.Open();
            for (int index = 130; index < 300; index++) worker.AdvanceMonsterMove(1);
            if (!worker.State.Freeze().ContentEquals(final.Open().State.Freeze()))
                throw new InvalidOperationException("Frozen AI continuation differs.");
            root.RestoreInto(worker);
            if (worker.CurrentMonsterMove != 2 || worker.MonsterMoveLogCount != 3)
                throw new InvalidOperationException("AI root restore retained future log.");
        });
        Console.WriteLine("COMPACT_MONSTER_AI_CHECKS_OK copied_graph=true noninitial_root=true log_steps=300 rollback=true frozen_workers=8");
    }
}
