using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors;

internal static partial class HookMirrors
{
    /// <summary>
    /// 按原生监听顺序判断额外回合，在第一个返回 true 的来源处结束。
    /// </summary>
    public static bool ShouldTakeExtraTurn(
        CombatPredictionSimulator simulator, SimulatedCombatState combat, Player player)
        => ShouldTakeExtraTurn(simulator, combat, player, out _);

    public static bool ShouldTakeExtraTurn(
        CombatPredictionSimulator simulator, SimulatedCombatState combat, Player player,
        out AbstractModel? source)
    {
        ExtraTurnMirrors.Seal();
        var context = new ExtraTurnMirrorContext { Simulator = simulator, Player = player };
        foreach (AbstractModel listener in IterateCombatHookListeners(simulator, MirroredHookMask.ExtraTurnCallbacks))
        {
            if (ExtraTurnMirrors.ShouldTakeExtraTurnEarly(listener, context))
            {
                source = listener;
                return true;
            }
        }
        foreach (AbstractModel listener in IterateCombatHookListeners(simulator, MirroredHookMask.ExtraTurnCallbacks))
        {
            if (ExtraTurnMirrors.ShouldTakeExtraTurn(listener, context))
            {
                source = listener;
                return true;
            }
        }
        source = null;
        return false;
    }

    /// <summary>
    /// 结算被选中的额外回合来源，选择暂停由动作重放恢复。
    /// </summary>
    public static bool AfterTakingExtraTurn(
        CombatPredictionSimulator simulator, SimulatedCombatState combat, Player player, AbstractModel source)
    {
        ExtraTurnMirrors.Seal();
        if (simulator.HasPendingChoice)
            return false;
        var context = new ExtraTurnMirrorContext { Simulator = simulator, Player = player };
        PredictedCard? card = source is CardModel model ? simulator.State.FindCard(model) : null;
        ExtraTurnMirrors.AfterTakingExtraTurn(card?.Preview ?? source, context);
        return !simulator.HasPendingChoice;
    }
}
