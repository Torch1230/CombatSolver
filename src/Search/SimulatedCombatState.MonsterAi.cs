using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using CombatSolver.Engine.InCombat.Simulation;
using System.Text;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private ForkableDictionary<Creature, BranchMonsterAiState>? _monsterAiStates;

    public IReadOnlyList<ForecastMove> CurrentMonsterMoves()
    {
        List<ForecastMove> moves = new(Enemies.Count);
        foreach (Creature enemy in Enemies)
        {
            if (enemy.Monster == null)
                continue;
            moves.Add(CurrentMonsterMove(enemy));
        }
        return moves;
    }

    public ForecastMove CurrentMonsterMove(Creature creature)
    {
        if (creature.Monster == null)
            throw new InvalidOperationException($"生物 {creature.Name} 不是怪物。");
        return BranchMonsterAi.CurrentMove(GetMonsterAiState(creature), this);
    }

    public string GetPredictedMoveId(Creature creature)
        => GetMonsterAiState(creature).Current.Id;

    public int GetMonsterStaticInt(Creature creature, string name)
    {
        BranchMonsterAiState state = GetMonsterAiState(creature);
        return state.Static.StaticIntValues.TryGetValue(name, out int value)
            ? value
            : throw new InvalidOperationException(
                $"Monster {state.Monster.Id.Entry} has no captured static value {name}.");
    }

    public string GetNextMoveIdFromStateLog(
        Creature creature,
        MegaCrit.Sts2.Core.Random.Rng rng)
    {
        BranchMonsterAiState state = GetMonsterAiState(creature);
        string lastStateId = state.StateLog.LastOrDefault()
            ?? throw new InvalidOperationException($"怪物 {state.Monster.Id.Entry} 没有行动历史。");
        MonsterState lastState = state.Machine.States.GetValueOrDefault(lastStateId)
            ?? throw new InvalidOperationException(
                $"怪物 {state.Monster.Id.Entry} 的行动历史包含未知状态 {lastStateId}。");
        return lastState.GetNextState(creature, rng);
    }

    public void ForceMonsterMove(Creature creature, string moveId)
    {
        BranchMonsterAiState current = GetMonsterAiState(creature);
        MoveState move = current.Machine.States.GetValueOrDefault(moveId) as MoveState
            ?? throw new InvalidOperationException($"怪物 {current.Monster.Id.Entry} 没有行动 {moveId}。");
        ForceMonsterMove(creature, move);
    }

    public void ForceMonsterMove(Creature creature, MoveState move)
    {
        BranchMonsterAiState current = GetMonsterAiState(creature);
        (_monsterAiStates ??= [])[creature] = current with
        {
            Current = move,
            NeedsInitialRoll = false,
        };
    }

    public void ForceStunnedMove(Creature creature, string? nextMoveId = null)
    {
        nextMoveId ??= GetMonsterAiState(creature).StateLog.LastOrDefault()
            ?? GetMonsterAiState(creature).Current.Id;
        MoveState stunned = new("STUNNED", _ => Task.CompletedTask, new StunIntent())
        {
            FollowUpStateId = nextMoveId,
            MustPerformOnceBeforeTransitioning = true,
        };
        ForceMonsterMove(creature, stunned);
    }

    public void AdvanceMonsterAi(Creature creature, CombatPredictionSimulator simulator)
    {
        BranchMonsterAiState current = GetMonsterAiState(creature);
        (_monsterAiStates ??= [])[creature] = BranchMonsterAi.Advance(current, simulator, this);
    }

    public void PrepareMonsterMovesForNextRound(
        CombatPredictionSimulator simulator,
        IReadOnlyDictionary<Creature, MoveState> performedMoves)
    {
        foreach ((Creature enemy, MoveState performedMove) in performedMoves)
            PrepareMonsterMoveForNextRound(simulator, enemy, performedMove);
    }

    public void PrepareMonsterMoveForNextRound(
        CombatPredictionSimulator simulator,
        Creature enemy,
        MoveState? performedMove)
    {
        if (!ContainsCreature(enemy) || !CanPerformMonsterMove(simulator, enemy))
            return;
        if (enemy.Monster == null)
            return;
        BranchMonsterAiState current = GetMonsterAiState(enemy);
        if (current.NeedsInitialRoll)
        {
            (_monsterAiStates ??= [])[enemy] = BranchMonsterAi.RollInitial(current, simulator, this);
            return;
        }
        if (current.Current.Id == "STUNNED" && WillSkipNextMove(enemy))
            return;
        if (current.Current.MustPerformOnceBeforeTransitioning
            && !ReferenceEquals(performedMove, current.Current))
        {
            return;
        }
        AdvanceMonsterAi(enemy, simulator);
    }

    public void RegisterMonsterAi(Creature creature, MoveState current)
    {
        MonsterModel monster = creature.Monster
            ?? throw new InvalidOperationException($"生物 {creature.Name} 不是怪物。");
        MonsterMoveStateMachine machine = monster.MoveStateMachine
            ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
        List<string> log = machine.StateLog.Select(state => state.Id).ToList();
        if (log.Count == 0 || !string.Equals(log[^1], current.Id, StringComparison.Ordinal))
            log.Add(current.Id);
        (_monsterAiStates ??= [])[creature] = new BranchMonsterAiState(
            monster,
            machine,
            current,
            log,
            0,
            BranchMonsterStaticSnapshot.Capture(monster));
    }

    public void RegisterPendingInitialMonsterAi(Creature creature)
    {
        MonsterModel monster = creature.Monster
            ?? throw new InvalidOperationException($"生物 {creature.Name} 不是怪物。");
        MonsterMoveStateMachine machine = monster.MoveStateMachine
            ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
        List<string> log = machine.StateLog.Select(state => state.Id).ToList();
        MoveState? rolledInitial = machine.StateLog.LastOrDefault() as MoveState;
        MoveState initial = rolledInitial
            ?? machine.States.Values.OfType<MoveState>().FirstOrDefault()
            ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有可用行动状态。");
        (_monsterAiStates ??= [])[creature] = new BranchMonsterAiState(
            monster,
            machine,
            initial,
            log,
            0,
            BranchMonsterStaticSnapshot.Capture(monster),
            NeedsInitialRoll: rolledInitial == null);
    }

    private BranchMonsterAiState GetMonsterAiState(Creature creature)
    {
        if (_monsterAiStates?.TryGetValue(creature, out BranchMonsterAiState? state) == true)
            return state;
        if (_rootMaterialized && _rootCreatures.Contains(creature))
            throw new InvalidOperationException($"Root monster AI state was not captured for {creature.Name}.");
        MonsterModel monster = creature.Monster
            ?? throw new InvalidOperationException($"生物 {creature.Name} 不是怪物。");
        state = BranchMonsterAi.Capture(monster);
        (_monsterAiStates ??= []).Add(creature, state);
        return state;
    }

    private void AppendMonsterAiFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        foreach (Creature enemy in Enemies)
        {
            if (enemy.Monster == null)
                continue;
            BranchMonsterAiState state = GetMonsterAiState(enemy);
            fingerprint.Add('M');
            fingerprint.Add(enemy.CombatId ?? uint.MaxValue);
            fingerprint.Add(state.Current.Id);
            fingerprint.Add(state.KnowledgeDemonCurseCounter);
            fingerprint.Add(state.NeedsInitialRoll);
            fingerprint.Add(state.StateLog.Count);
            foreach (string moveId in state.StateLog)
                fingerprint.Add(moveId);
        }
    }

    public void AppendPredictedMonsterAiContinuation(StringBuilder text)
    {
        for (int index = 0; index < Enemies.Count; index++)
        {
            BranchMonsterAiState state = GetMonsterAiState(Enemies[index]);
            text.Append(";AI").Append(index).Append('=')
                .Append(string.Join(',', state.StateLog));
        }
    }

    public static void AppendLiveMonsterAiContinuation(StringBuilder text, IReadOnlyList<Creature> enemies)
    {
        for (int index = 0; index < enemies.Count; index++)
        {
            text.Append(";AI").Append(index).Append('=');
            if (enemies[index].Monster?.MoveStateMachine is { } machine)
                text.Append(string.Join(',', machine.StateLog.Select(state => state.Id)));
        }
    }
}
