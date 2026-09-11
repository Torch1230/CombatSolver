using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
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

    public void CompletePlayerDeath(MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        RemovePowersAfterDeath(player.Creature);
        CompleteDeathPhase(player.Creature);
    }

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

    private void AppendDeathLifecycleFingerprint(ref StateFingerprintBuilder fingerprint,
        IReadOnlyDictionary<Creature, PredictedDeathPhase>? phases = null)
    {
        phases ??= _deathPhases;
        if (phases == null)
            return;
        foreach ((Creature creature, PredictedDeathPhase phase) in phases
                     .OrderBy(entry => entry.Key.CombatId))
        {
            fingerprint.Add('L');
            fingerprint.Add(creature.CombatId ?? uint.MaxValue);
            fingerprint.Add((int)phase);
        }
    }
}
