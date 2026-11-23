using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace CombatSolver;

internal readonly record struct ForecastAttackHit(int Damage, int BaseDamage);

internal sealed record ForecastMove(
    Creature Owner,
    MoveState Move,
    IReadOnlyList<ForecastAttackHit> AttackHits);

internal sealed class IntentForecast
{
    public required IReadOnlyList<IReadOnlyList<ForecastMove>> Rounds { get; init; }
    public required bool HasUnsupportedIntent { get; init; }
    public required bool IsExactForModeledDamage { get; init; }
    public required IReadOnlyList<string> UnsupportedDetails { get; init; }
    public required IReadOnlyList<string> ApproximationDetails { get; init; }
    public required IReadOnlyList<int> MonsterAiCountersByRound { get; init; }
}

internal static class IntentForecaster
{
    internal static int CaptureBaseDamage(AttackIntent attack, MonsterModel monster, string moveId)
        => HotImportSupport.CaptureAttackBaseDamage(attack, monster, moveId);

    public static IntentForecast Build(CombatState state, int roundCount = 1)
    {
        List<ForecastMove> currentRound = [];
        foreach (Creature enemy in state.Enemies)
        {
            if (enemy.Monster is not { } monster || monster.NextMove is not { } move)
                continue;
            List<ForecastAttackHit> hits = [];
            foreach (AbstractIntent intent in move.Intents)
            {
                if (intent is not AttackIntent attack)
                    continue;
                int baseDamage = CaptureBaseDamage(attack, monster, move.Id);
                int single = attack.GetSingleDamage(state.PlayerCreatures, enemy);
                for (int i = 0; i < Math.Max(attack.Repeats, 1); i++)
                    hits.Add(new ForecastAttackHit(single, baseDamage));
            }
            currentRound.Add(new ForecastMove(enemy, move, hits));
        }

        return new IntentForecast
        {
            Rounds = roundCount > 0 ? [currentRound] : [],
            HasUnsupportedIntent = false,
            IsExactForModeledDamage = true,
            UnsupportedDetails = [],
            ApproximationDetails = [],
            MonsterAiCountersByRound = [state.RunState.Rng.MonsterAi.ToSerializable().counter],
        };
    }
}
