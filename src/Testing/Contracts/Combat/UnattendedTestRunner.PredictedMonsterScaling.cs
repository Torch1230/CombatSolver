using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static int s_multiplayerScalingCalls;

    /// <summary>
    /// T011 最小边界：单人求解不支持多人生命缩放，原版
    /// <see cref="Creature.ScaleMonsterHpForMultiplayer" /> 在 playerCount==1 时是空操作，
    /// 但搜索 worker 上的预测怪构造仍会调用它，从而执行第三方 Harmony postfix
    /// （报告中 CustomDifficulty 在该调用下访问主线程本地化格式池）。
    /// 夹具用同类 postfix 证明预测怪构造不应进入这条调用。
    /// </summary>
    private void AssertPredictedMonsterScalingBoundary(CombatState combat)
    {
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        MethodInfo target = AccessTools.Method(typeof(Creature), nameof(Creature.ScaleMonsterHpForMultiplayer));
        MethodInfo postfix = AccessTools.Method(
            typeof(UnattendedTestRunner), nameof(PredictedMonsterScalingPostfix));
        Harmony harmony = new("CombatSolver.Tests.PredictedMonsterScaling");
        int hp;
        try
        {
            Interlocked.Exchange(ref s_multiplayerScalingCalls, 0);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            SimulatedCombatState simulated = new(combat);
            CombatPredictionSimulator simulator = new(simulated);
            ToughEgg canonical = ModelDb.Monster<ToughEgg>();
            Creature creature = simulated.CreatePredictedMonster(
                simulator, canonical.ToMutable(), CombatSide.Enemy, slot: null);
            hp = creature.MaxHp;
        }
        finally
        {
            harmony.Unpatch(target, postfix);
        }
        int calls = Interlocked.CompareExchange(ref s_multiplayerScalingCalls, 0, 0);
        if (calls != 0)
            throw new InvalidOperationException(
                "预测怪构造仍调用单人语义为空的 ScaleMonsterHpForMultiplayer；搜索 worker 会因此执行第三方 postfix。");
        ToughEgg egg = ModelDb.Monster<ToughEgg>();
        if (hp < egg.MinInitialHp || hp > egg.MaxInitialHp)
            throw new InvalidOperationException($"预测怪生命越出原版范围：{hp}。");
        if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new InvalidOperationException("预测怪构造边界改变了 live 战斗。");
        _completedChecks.Add($"PredictedMonsterScaling:hp={hp}:range={egg.MinInitialHp}..{egg.MaxInitialHp}:calls=0");
    }

    private static void PredictedMonsterScalingPostfix(Creature __instance)
        => Interlocked.Increment(ref s_multiplayerScalingCalls);
}
