using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Exercise every native move, including moves other than the currently selected one.
    // Branch mutations must not alter an already published forecast or a sibling's AI.
    private static void AssertMonsterForecastIsolation(CombatPredictionSimulator parent, MonsterModel monster)
    {
        var parentCombat = (SimulatedCombatState)parent.State.CombatState;
        var owner = monster.Creature;
        ForecastMove original = parentCombat.CurrentMonsterMove(owner);
        ForecastAttackHit[] originalHits = original.AttackHits.ToArray();
        var left = (SimulatedCombatState)parent.Fork().State.CombatState;
        var right = (SimulatedCombatState)parent.Fork().State.CombatState;
        foreach (MoveState move in monster.MoveStateMachine!.States.Values.OfType<MoveState>())
        {
            left.ForceMonsterMove(owner, move);
            bool dynamicRepeats = monster.GetType().Name == "TestSubject" && move.Id == "MULTI_CLAW_MOVE";
            foreach (int extra in dynamicRepeats ? new[] { -100, 0, 3 } : new[] { 0 })
            {
                if (dynamicRepeats) left.SetMonsterInt(owner, "_extraMultiClawCount", extra);
                ForecastMove actual = left.CurrentMonsterMove(owner);
                List<ForecastAttackHit> expected = [];
                foreach (AttackIntent attack in move.Intents.OfType<AttackIntent>())
                {
                    int damage = Math.Max(0, (int)(attack.DamageCalc?.Invoke() ?? 0m));
                    int repeats = dynamicRepeats
                        ? MonsterValueReader.ReadInt(monster, "BaseMultiClawCount") + extra
                        : attack.Repeats;
                    for (int index = 0; index < Math.Max(repeats, 1); index++)
                        expected.Add(new(damage, damage));
                }
                if (!ReferenceEquals(actual.Owner, owner) || !ReferenceEquals(actual.Move, move)
                    || !actual.AttackHits.SequenceEqual(expected))
                    throw new InvalidOperationException($"Monster forecast changed: {monster.Id.Entry}/{move.Id}/{extra}.");
            }
        }
        // STUNNED is created inside the branch, outside the root's captured move table.
        left.ForceStunnedMove(owner);
        ForecastMove stunned = left.CurrentMonsterMove(owner);
        if (stunned.Move.Id != "STUNNED" || stunned.AttackHits.Count != 0)
            throw new InvalidOperationException("Branch-created stunned move lost its forecast fallback.");
        foreach (var unchanged in new[] { original, parentCombat.CurrentMonsterMove(owner), right.CurrentMonsterMove(owner) })
            if (!ReferenceEquals(unchanged.Move, original.Move) || !unchanged.AttackHits.SequenceEqual(originalHits))
                throw new InvalidOperationException("Monster forecast leaked across sibling branches.");
    }
}
