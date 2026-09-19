using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using System.Reflection;

namespace CombatSolver;

internal sealed record BranchMonsterAiState(
    MonsterModel Monster,
    MonsterMoveStateMachine Machine,
    MoveState Current,
    IReadOnlyList<string> StateLog,
    int KnowledgeDemonCurseCounter,
    BranchMonsterStaticSnapshot Static,
    bool NeedsInitialRoll = false);

internal readonly record struct BranchMonsterAttack(int BaseDamage, int Repeats);

internal sealed record BranchMonsterStaticSnapshot(
    IReadOnlyDictionary<string, IReadOnlyList<BranchMonsterAttack>> AttacksByMove,
    IReadOnlyDictionary<MoveState, ForecastMove> ForecastMoves,
    IReadOnlyDictionary<(string BranchId, string StateId), float> RandomBaseWeights,
    IReadOnlyDictionary<string, string> ConditionalSelections,
    IReadOnlyDictionary<string, int> StaticIntValues,
    int TestSubjectBaseMultiClawCount)
{
    [ThreadStatic]
    private static int _allowUnreachableConditionalsForTesting;

    internal static IDisposable AllowUnreachableConditionalsForTesting()
    {
        _allowUnreachableConditionalsForTesting++;
        return new UnreachableConditionalScope();
    }

    public static BranchMonsterStaticSnapshot Capture(MonsterModel monster)
    {
        MonsterMoveStateMachine machine = monster.MoveStateMachine
            ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
        Dictionary<string, IReadOnlyList<BranchMonsterAttack>> attacks = new(StringComparer.Ordinal);
        Dictionary<MoveState, ForecastMove> forecasts = new(ReferenceEqualityComparer.Instance);
        Dictionary<(string BranchId, string StateId), float> weights = [];
        Dictionary<string, string> conditionals = new(StringComparer.Ordinal);
        foreach (MonsterState state in machine.States.Values)
        {
            if (state is MoveState move)
            {
                attacks[move.Id] = move.Intents
                    .OfType<AttackIntent>()
                    .Select(attack => new BranchMonsterAttack(
                        Math.Max(0, (int)(attack.DamageCalc?.Invoke() ?? 0m)),
                        Math.Max(1, attack.Repeats)))
                    .ToArray();
                // Damage/repeat metadata is already frozen here. Ordinary moves can share
                // these immutable values across snapshots; branch-dependent repeats are
                // still rebuilt by CurrentMove. Never cache branch-modified damage.
                List<ForecastAttackHit> hits = [];
                foreach (BranchMonsterAttack attack in attacks[move.Id])
                    for (int index = 0; index < attack.Repeats; index++)
                        hits.Add(new(attack.BaseDamage, attack.BaseDamage));
                forecasts.Add(move, new(monster.Creature, move, hits.AsReadOnly()));
            }
            else if (state is RandomBranchState random)
            {
                foreach (RandomBranchState.StateWeight weight in random.States)
                    weights.Add((random.Id, weight.stateId), weight.GetWeight());
            }
            else if (state is ConditionalBranchState conditional)
            {
                try
                {
                    conditionals.Add(
                        conditional.Id,
                        conditional.GetNextState(monster.Creature, new Rng(0)));
                }
                catch (InvalidOperationException ex) when (
                    _allowUnreachableConditionalsForTesting > 0
                    && ex.Message == "No valid next state found.")
                {
                    // Monster differential fixtures can combine creatures from unrelated
                    // encounters. Leave such an artificial branch unresolved; execution still
                    // fails explicitly if the tested path actually reaches it.
                }
            }
        }
        int baseMultiClawCount = monster.GetType().Name == "TestSubject"
            ? MonsterValueReader.ReadInt(monster, "BaseMultiClawCount")
            : 0;
        return new BranchMonsterStaticSnapshot(
            attacks,
            forecasts,
            weights,
            conditionals,
            MonsterMoveEffects.CaptureStaticIntValues(monster),
            baseMultiClawCount);
    }

    private sealed class UnreachableConditionalScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_allowUnreachableConditionalsForTesting <= 0)
                throw new InvalidOperationException("怪物条件分支测试作用域计数下溢。");
            _allowUnreachableConditionalsForTesting--;
        }
    }
}

internal static class BranchMonsterAi
{
    private static readonly FieldInfo InitialStateField = typeof(MonsterMoveStateMachine)
        .GetField("_initialState", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(MonsterMoveStateMachine).FullName, "_initialState");

    public static BranchMonsterAiState Capture(MonsterModel monster)
    {
        MonsterMoveStateMachine machine = monster.MoveStateMachine
            ?? throw new InvalidOperationException($"怪物 {monster.Id.Entry} 没有行动状态机。");
        return new(
            monster,
            machine,
            monster.NextMove,
            machine.StateLog.Select(state => state.Id).ToArray(),
            monster.GetType().Name == "KnowledgeDemon"
                ? MonsterValueReader.ReadInt(monster, "_curseOfKnowledgeCounter")
                : 0,
            BranchMonsterStaticSnapshot.Capture(monster));
    }

    public static ForecastMove CurrentMove(BranchMonsterAiState state, SimulatedCombatState combat)
    {
        bool dynamicRepeats = state.Monster.GetType().Name == "TestSubject"
            && state.Current.Id == "MULTI_CLAW_MOVE";
        if (!dynamicRepeats && state.Static.ForecastMoves.TryGetValue(state.Current, out ForecastMove? frozen))
            return frozen;

        if (!state.Static.AttacksByMove.TryGetValue(
                state.Current.Id,
                out IReadOnlyList<BranchMonsterAttack>? attacks))
        {
            attacks = [];
        }
        List<ForecastAttackHit> hits = [];
        foreach (BranchMonsterAttack attack in attacks)
        {
            int repeats = dynamicRepeats
                    ? state.Static.TestSubjectBaseMultiClawCount
                        + combat.GetMonsterInt(state.Monster.Creature, "_extraMultiClawCount")
                    : attack.Repeats;
            for (int index = 0; index < Math.Max(repeats, 1); index++)
                hits.Add(new ForecastAttackHit(attack.BaseDamage, attack.BaseDamage));
        }
        return new ForecastMove(state.Monster.Creature, state.Current, hits);
    }

    public static BranchMonsterAiState Advance(
        BranchMonsterAiState source,
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
    {
        Rng rng = simulator.Rng.MonsterAi;
        MonsterMoveStateMachine machine = source.Machine;
        int knowledgeCounter = source.KnowledgeDemonCurseCounter;
        List<string> log = new(source.StateLog);

        if (source.Monster.GetType().Name == "KnowledgeDemon")
        {
            if (source.Current.Id == "CURSE_OF_KNOWLEDGE_MOVE")
                knowledgeCounter++;
            if (source.Current.Id == "PONDER_MOVE")
            {
                string nextId = knowledgeCounter < 3 ? "CURSE_OF_KNOWLEDGE_MOVE" : "SLAP_MOVE";
                MoveState next = (MoveState)machine.States[nextId];
                log.Add(next.Id);
                return source with { Current = next, StateLog = log, KnowledgeDemonCurseCounter = knowledgeCounter };
            }
        }

        MonsterState nextState = ResolveFollowUp(machine, source.Current);
        for (int guard = 0; guard < 32; guard++)
        {
            switch (nextState)
            {
                case MoveState move:
                    log.Add(move.Id);
                    return source with { Current = move, StateLog = log, KnowledgeDemonCurseCounter = knowledgeCounter };
                case RandomBranchState random:
                    nextState = machine.States[MonsterRandomBranchResolver.Pick(
                        machine,
                        random,
                        log,
                        rng,
                        state => GetBranchWeight(state, random.Id, source, combat))];
                    break;
                case ConditionalBranchState conditional:
                    nextState = machine.States[ResolveConditional(
                        conditional,
                        source,
                        combat,
                        simulator)];
                    break;
                default:
                    throw new PredictionUnsupportedException(
                        $"Unsupported monster state {nextState.GetType().FullName} for {source.Monster.Id.Entry}.");
            }
        }

        throw new InvalidOperationException(
            $"怪物 {source.Monster.Id.Entry} 的分支内行动状态机未能在 32 步内落到行动节点。");
    }

    public static BranchMonsterAiState RollInitial(
        BranchMonsterAiState source,
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
    {
        MonsterState nextState = (MonsterState?)InitialStateField.GetValue(source.Machine)
            ?? throw new InvalidOperationException($"怪物 {source.Monster.Id.Entry} 没有初始行动状态。");
        List<string> log = new(source.StateLog);
        for (int guard = 0; guard < 32; guard++)
        {
            switch (nextState)
            {
                case MoveState move:
                    log.Add(move.Id);
                    return source with
                    {
                        Current = move,
                        StateLog = log,
                        NeedsInitialRoll = false,
                    };
                case RandomBranchState random:
                    nextState = source.Machine.States[MonsterRandomBranchResolver.Pick(
                        source.Machine,
                        random,
                        log,
                        simulator.Rng.MonsterAi,
                        state => GetBranchWeight(state, random.Id, source, combat))];
                    break;
                case ConditionalBranchState conditional:
                    nextState = source.Machine.States[ResolveConditional(
                        conditional,
                        source,
                        combat,
                        simulator)];
                    break;
                default:
                    throw new PredictionUnsupportedException(
                        $"Unsupported initial monster state {nextState.GetType().FullName} " +
                        $"for {source.Monster.Id.Entry}.");
            }
        }
        throw new InvalidOperationException(
            $"怪物 {source.Monster.Id.Entry} 的初始状态未能在 32 步内落到行动节点。");
    }

    private static MonsterState ResolveFollowUp(MonsterMoveStateMachine machine, MoveState move)
    {
        string id = move.FollowUpState?.Id ?? move.FollowUpStateId
            ?? throw new InvalidOperationException($"行动 {move.Id} 没有后继状态。");
        return machine.States[id];
    }

    private static string ResolveConditional(
        ConditionalBranchState branch,
        BranchMonsterAiState source,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        Creature owner = source.Monster.Creature;
        string monster = owner.Monster?.GetType().Name ?? string.Empty;
        if (monster == "FrogKnight" && branch.Id == "HALF_HEALTH")
        {
            bool charged = combat.GetMonsterBool(owner, "_hasBeetleCharged");
            SimCreatureState creature = simulator.State.GetCreature(owner);
            return charged || creature.CurrentHp >= creature.MaxHp / 2 ? "TONGUE_LASH" : "BEETLE_CHARGE";
        }
        if (monster == "Fabricator" && branch.Id == "fabricateBranch")
        {
            int aliveTeammates = combat.GetTeammatesOf(owner)
                .Count(creature => simulator.State.GetCreature(creature).IsAlive);
            return aliveTeammates < 4 ? "RAND" : "DISINTEGRATE_MOVE";
        }
        if (monster == "TestSubject" && branch.Id == "REVIVE_BRANCH")
            return combat.GetMonsterInt(owner, "_respawns") < 2 ? "MULTI_CLAW_MOVE" : "PHASE3_LACERATE_MOVE";
        if (monster == "Ovicopter" && branch.Id == "SUMMON_BRANCH_STATE")
        {
            int alive = combat.GetTeammatesOf(owner)
                .Count(creature => simulator.State.GetCreature(creature).IsAlive);
            return alive <= 3 ? "LAY_EGGS_MOVE" : "NUTRITIONAL_PASTE_MOVE";
        }
        if (monster == "LagavulinMatriarch" && branch.Id == "SLEEP_BRANCH")
            return combat.GetAmount<MegaCrit.Sts2.Core.Models.Powers.AsleepPower>(owner) > 0
                ? "SLEEP_MOVE"
                : "SLASH_MOVE";
        if (monster == "SlumberingBeetle" && branch.Id == "SNORE_NEXT")
            return combat.GetAmount<MegaCrit.Sts2.Core.Models.Powers.SlumberPower>(owner) > 0
                ? "SNORE_MOVE"
                : "ROLL_OUT_MOVE";
        if (monster == "Queen" && branch.Id is "YOURE_MINE_NOW_BRANCH" or "BURN_BRIGHT_FOR_ME_BRANCH")
            return combat.GetMonsterBool(owner, "_hasAmalgamDied")
                ? "OFF_WITH_YOUR_HEAD_MOVE"
                : "BURN_BRIGHT_FOR_ME_MOVE";
        if (monster == "LivingShield" && branch.Id == "SHIELD_SLAM_BRANCH")
        {
            bool hasAliveAlly = combat.GetTeammatesOf(owner)
                .Any(creature => creature != owner && simulator.State.GetCreature(creature).IsAlive);
            return hasAliveAlly ? "SHIELD_SLAM_MOVE" : "SMASH_MOVE";
        }
        if (monster == "BowlbugRock" && branch.Id == "POST_HEADBUTT")
            return "HEADBUTT_MOVE";
        return source.Static.ConditionalSelections.TryGetValue(branch.Id, out string? selected)
            ? selected
            : throw new InvalidOperationException(
                $"怪物 {source.Monster.Id.Entry} 的条件分支 {branch.Id} 没有根选择。");
    }

    private static float GetBranchWeight(
        RandomBranchState.StateWeight state,
        string branchId,
        BranchMonsterAiState source,
        SimulatedCombatState combat)
    {
        Creature owner = source.Monster.Creature;
        if (owner.Monster?.GetType().Name != "TwoTailedRat")
        {
            return source.Static.RandomBaseWeights.TryGetValue((branchId, state.stateId), out float weight)
                ? weight
                : throw new InvalidOperationException(
                    $"怪物 {source.Monster.Id.Entry} 的随机分支 {branchId}.{state.stateId} 没有根权重。");
        }
        bool canSummon = CanTwoTailedRatSummon(owner, combat);
        return state.stateId switch
        {
            "CALL_FOR_BACKUP_MOVE" => canSummon ? 0.75f : 0f,
            "SCREECH_MOVE" or "SCRATCH_MOVE" or "DISEASE_BITE_MOVE"
                => canSummon ? 1f / 12f : 1f,
            _ => state.GetWeight(),
        };
    }

    private static bool CanTwoTailedRatSummon(Creature owner, SimulatedCombatState combat)
    {
        if (combat.GetMonsterInt(owner, "_turnsUntilSummonable") > 0
            || combat.GetMonsterInt(owner, "_callForBackupCount") >= 3
            || string.IsNullOrEmpty(combat.NextFreeSlot()))
        {
            return false;
        }
        return combat.GetTeammatesOf(owner)
            .Where(creature => creature != owner && creature.Monster?.GetType().Name == "TwoTailedRat")
            .All(creature => combat.GetPredictedMoveId(creature) != "CALL_FOR_BACKUP_MOVE");
    }

}
