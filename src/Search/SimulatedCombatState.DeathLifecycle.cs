using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal enum PredictedDeathPhase
{
    None,
    Reviving,
    PermanentlyDead,
}

internal sealed partial class SimulatedCombatState
{
    private ForkableDictionary<Creature, PredictedDeathPhase>? _deathPhases;

    public void SpawnStockReplacement(CombatPredictionSimulator simulator, StockPower power)
        => MonsterSpawnSupport.Spawn<Axebot>(simulator, this, power.Owner, power.Owner.SlotName,
            configure: axebot =>
            {
                axebot.ShouldPlaySpawnAnimation = true;
                axebot.StockAmount = power.Amount - 1;
            });

    private static ForkableDictionary<Creature, PredictedDeathPhase>? BuildInitialDeathPhases(
        IReadOnlyList<Creature> enemies)
    {
        ForkableDictionary<Creature, PredictedDeathPhase>? phases = null;
        foreach (Creature enemy in enemies)
        {
            if (enemy.CurrentHp > 0)
                continue;
            (phases ??= [])[enemy] = (PredictedDeathPhase)LiveDeathPhase(enemy);
        }
        return phases;
    }

    public bool CanPerformMonsterMove(CombatPredictionSimulator simulator, Creature creature)
        => simulator.State.GetCreature(creature).IsAlive
            || _deathPhases?.GetValueOrDefault(creature) == PredictedDeathPhase.Reviving;

    private int RevivingEnemyHp(Creature creature, int capturedMaxHp)
    {
        if (_deathPhases?.GetValueOrDefault(creature) != PredictedDeathPhase.Reviving)
            return 0;
        if (creature.Monster is DecimillipedeSegment)
        {
            bool hasSurvivingSegment = GetTeammatesOf(creature)
                .Any(candidate => candidate != creature
                    && GetAmount<ReattachPower>(candidate) > 0
                    && _deathPhases?.GetValueOrDefault(candidate) != PredictedDeathPhase.PermanentlyDead);
            return hasSurvivingSegment ? Math.Max(0, GetAmount<ReattachPower>(creature)) : 0;
        }
        if (creature.Monster is TestSubject)
            return RemainingTestSubjectFormHp(creature, currentHp: 0);
        return GetAmount<IllusionPower>(creature) > 0 ? capturedMaxHp : 0;
    }

    public int RemainingTestSubjectFormHp(Creature creature, int currentHp)
    {
        if (creature.Monster is not TestSubject
            || _deathPhases?.GetValueOrDefault(creature) == PredictedDeathPhase.PermanentlyDead)
        {
            return Math.Max(0, currentHp);
        }

        int remaining = Math.Max(0, currentHp);
        if (GetAmount<AdaptablePower>(creature) <= 0)
            return remaining;

        int respawns = GetMonsterInt(creature, "_respawns");
        if (respawns < 1)
            remaining += ScaleTestSubjectFormHp(creature, "SecondFormHp");
        if (respawns < 2)
            remaining += ScaleTestSubjectFormHp(creature, "ThirdFormHp");
        return remaining;
    }

    private int ScaleTestSubjectFormHp(Creature creature, string member)
        => (int)Creature.ScaleHpForMultiplayer(
            GetMonsterInt(creature, member),
            Encounter,
            Players.Count,
            _currentActIndex);

    public void BeginAdaptableRevive(Creature creature)
    {
        SetDeathPhase(creature, PredictedDeathPhase.Reviving);
        ForceMonsterMove(creature, "RESPAWN_MOVE");
    }

    public void BeginIllusionRevive(Creature creature)
    {
        if (_deathPhases?.GetValueOrDefault(creature) == PredictedDeathPhase.Reviving)
            return;
        BranchMonsterAiState ai = GetMonsterAiState(creature);
        string? followUp = GetPower<IllusionPower>(creature)?.FollowUpStateId
            ?? ai.StateLog.LastOrDefault(moveId => moveId != "REVIVE_MOVE");
        if (followUp == null)
        {
            throw new InvalidOperationException(
                $"幻象 {creature.Name} 进入复活时没有可恢复的正式行动记录。");
        }
        MoveState revive = new("REVIVE_MOVE", _ => Task.CompletedTask, new HealIntent())
        {
            FollowUpStateId = followUp,
            MustPerformOnceBeforeTransitioning = true,
        };
        SetDeathPhase(creature, PredictedDeathPhase.Reviving);
        ForceMonsterMove(creature, revive);
    }

    public void BeginReattach(CombatPredictionSimulator simulator, Creature creature)
    {
        Creature[] otherSegments = GetTeammatesOf(creature)
            .Where(candidate => candidate != creature && GetAmount<ReattachPower>(candidate) > 0)
            .ToArray();
        bool allDead = otherSegments.All(candidate => simulator.State.GetCreature(candidate).IsDead);
        if (allDead)
        {
            foreach (Creature segment in otherSegments.Append(creature))
                SetDeathPhase(segment, PredictedDeathPhase.PermanentlyDead);
            return;
        }
        SetDeathPhase(creature, PredictedDeathPhase.Reviving);
        ForceMonsterMove(creature, "DEAD_MOVE");
    }

    public void CompleteDeathPhase(Creature creature)
    {
        if (_deathPhases?.GetValueOrDefault(creature) is not PredictedDeathPhase.Reviving)
            SetDeathPhase(creature, PredictedDeathPhase.PermanentlyDead);
    }

    public bool HasCompletedDeathEffects(Creature creature)
        => _deathPhases?.GetValueOrDefault(creature) is PredictedDeathPhase.Reviving or PredictedDeathPhase.PermanentlyDead;

    private bool CanReceivePredictedPowers(Creature creature)
    {
        PredictedDeathPhase phase = _deathPhases?.GetValueOrDefault(creature)
            ?? PredictedDeathPhase.None;
        return phase == PredictedDeathPhase.None;
    }

    public void ResolveReviveMove(
        CombatPredictionSimulator simulator,
        Creature creature,
        string moveId)
    {
        switch (creature.Monster)
        {
            case TestSubject when moveId == "RESPAWN_MOVE":
                ResolveTestSubjectRevive(simulator, creature);
                break;
            case DecimillipedeSegment when moveId == "REATTACH_MOVE":
            {
                bool allOthersDead = GetTeammatesOf(creature)
                    .Where(candidate => candidate != creature && GetAmount<ReattachPower>(candidate) > 0)
                    .All(candidate => !CanPerformMonsterMove(simulator, candidate));
                if (!allOthersDead)
                {
                    simulator.Heal(creature, GetAmount<ReattachPower>(creature));
                    SetDeathPhase(creature, PredictedDeathPhase.None);
                }
                break;
            }
            default:
                if (moveId == "REVIVE_MOVE")
                {
                    SimCreatureState state = simulator.State.GetCreature(creature);
                    state.CurrentHp = state.MaxHp;
                    SetDeathPhase(creature, PredictedDeathPhase.None);
                }
                break;
        }
    }

    public void RemovePowersAfterDeath(Creature creature)
    {
        bool hasIllusionHook = EffectivePowers().Any(power =>
            power is IllusionPower
            && power.Amount > 0
            && ReferenceEquals(power.Owner, creature));
        foreach (PowerModel power in EffectivePowers()
                     .Where(power => power.Owner == creature && power.Amount != 0)
                     .ToArray())
        {
            bool keep = !power.ShouldPowerBeRemovedAfterOwnerDeath();
            if (hasIllusionHook)
            {
                keep = power.Type != PowerType.Debuff || power is ITemporaryPower;
            }
            if (!keep)
                SetPowerAmount(power, 0);
        }
        if (!ContainsCreature(creature))
        {
            foreach (PowerModel power in EffectivePowers()
                         .Where(power => power.Owner == creature && power.Amount != 0)
                         .ToArray())
            {
                SetPowerAmount(power, 0);
            }
        }
    }

    private void ResolveTestSubjectRevive(CombatPredictionSimulator simulator, Creature creature)
    {
        int respawns = GetMonsterInt(creature, "_respawns") + 1;
        SetMonsterInt(creature, "_respawns", respawns);
        int hp = respawns switch
        {
            1 => GetMonsterInt(creature, "SecondFormHp"),
            2 => GetMonsterInt(creature, "ThirdFormHp"),
            _ => throw new InvalidOperationException($"测试体出现未知复活阶段 {respawns}。"),
        };
        hp = (int)Creature.ScaleHpForMultiplayer(hp, Encounter, Players.Count, _currentActIndex);
        SimCreatureState state = simulator.State.GetCreature(creature);
        state.SetMaxHp(hp);
        state.CurrentHp = hp;
        SetDeathPhase(creature, PredictedDeathPhase.None);
        if (respawns == 1)
        {
            Apply<PainfulStabsPower>(creature, 1, creature);
        }
        else
        {
            Apply<NemesisPower>(creature, 1, creature);
            SetAmount<AdaptablePower>(creature, 0);
            SetAmount<PainfulStabsPower>(creature, 0);
        }
    }

    private void SetDeathPhase(Creature creature, PredictedDeathPhase phase)
        => (_deathPhases ??= [])[creature] = phase;

    // One entry per creature that has started dying. Enough headroom for a full enemy roster plus
    // summons; anything larger falls back to the heap rather than growing the stack frame.
    private const int InlineDeathPhaseCapacity = 32;

    private void AppendDeathLifecycleFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        if (_deathPhases is not { Count: > 0 })
            return;

        // This runs once per state key, so once per expanded node. OrderBy allocated an iterator,
        // a key array and a buffer array every time to sort a handful of entries. Sorting the
        // primitives by hand on a stack buffer produces the same sequence with no allocation.
        int count = _deathPhases.Count;
        Span<long> sortKeys = count <= InlineDeathPhaseCapacity
            ? stackalloc long[InlineDeathPhaseCapacity]
            : new long[count];
        Span<uint> ids = count <= InlineDeathPhaseCapacity
            ? stackalloc uint[InlineDeathPhaseCapacity]
            : new uint[count];
        Span<int> phases = count <= InlineDeathPhaseCapacity
            ? stackalloc int[InlineDeathPhaseCapacity]
            : new int[count];

        int next = 0;
        foreach ((Creature creature, PredictedDeathPhase phase) in _deathPhases)
        {
            uint? combatId = creature.CombatId;
            // Comparer<uint?>.Default, which OrderBy used, sorts null before every value; widening
            // to long and mapping null to -1 reproduces that ordering exactly.
            sortKeys[next] = combatId.HasValue ? combatId.Value : -1L;
            ids[next] = combatId ?? uint.MaxValue;
            phases[next] = (int)phase;
            next++;
        }

        // Insertion sort: OrderBy is stable, so creatures sharing a CombatId must keep dictionary
        // enumeration order. It is also the right algorithm for this many elements.
        for (int index = 1; index < count; index++)
        {
            long key = sortKeys[index];
            uint id = ids[index];
            int phase = phases[index];
            int scan = index - 1;
            while (scan >= 0 && sortKeys[scan] > key)
            {
                sortKeys[scan + 1] = sortKeys[scan];
                ids[scan + 1] = ids[scan];
                phases[scan + 1] = phases[scan];
                scan--;
            }
            sortKeys[scan + 1] = key;
            ids[scan + 1] = id;
            phases[scan + 1] = phase;
        }

        if (FastLaneVerification.Enabled)
            VerifyDeathLifecycleOrder(ids[..count], phases[..count]);

        for (int index = 0; index < count; index++)
        {
            fingerprint.Add('L');
            fingerprint.Add(ids[index]);
            fingerprint.Add(phases[index]);
        }
    }

    // Reconciles the hand-sorted sequence against the OrderBy it replaced, entry by entry.
    private void VerifyDeathLifecycleOrder(ReadOnlySpan<uint> ids, ReadOnlySpan<int> phases)
    {
        int index = 0;
        foreach ((Creature creature, PredictedDeathPhase phase) in _deathPhases!
                     .OrderBy(entry => entry.Key.CombatId))
        {
            uint expectedId = creature.CombatId ?? uint.MaxValue;
            if (index >= ids.Length || ids[index] != expectedId || phases[index] != (int)phase)
            {
                throw new InvalidOperationException(
                    "Death lifecycle fingerprint fast lane diverged from OrderBy at index "
                    + $"{index}: expected ({expectedId},{(int)phase}), got "
                    + (index < ids.Length ? $"({ids[index]},{phases[index]})" : "<past end>") + ".");
            }
            index++;
        }
        if (index != ids.Length)
        {
            throw new InvalidOperationException(
                $"Death lifecycle fingerprint fast lane produced {ids.Length} entries, OrderBy produced {index}.");
        }
    }
}
