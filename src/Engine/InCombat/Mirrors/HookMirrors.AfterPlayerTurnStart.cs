using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors;

internal static partial class HookMirrors
{
    public static bool AfterPlayerTurnStart(
        CombatPredictionSimulator simulator, Player player, TurnStartChoiceCursor choices)
    {
        AfterPlayerTurnStartMirrors.Seal();
        if (simulator.HasPendingChoice) return false;

        // 普通阶段可能生成新的第三方监听者，Late 会重新枚举。
        // 只要存在外部登记，就从阶段入口按原生三轮顺序派发。
        bool hasExternal = AfterPlayerTurnStartMirrors.HasExternalRegistrations;
        const MirroredHookMask all = MirroredHookMask.AfterPlayerTurnStartEarly
            | MirroredHookMask.AfterPlayerTurnStart | MirroredHookMask.AfterPlayerTurnStartLate;
        foreach (AbstractModel listener in IterateCombatHookListeners(simulator, all))
        {
            if (listener.GetType().Assembly != typeof(AbstractModel).Assembly
                && AfterPlayerTurnStartMirrors.HasOverride(listener))
            {
                hasExternal = true;
                break;
            }
        }
        // 没有第三方覆写时，保留原版的批次顺序和选择续执行帧；两条路径共享单项结算体。
        if (!hasExternal && simulator.State.CombatState is SimulatedCombatState combat)
            return !combat.TriggerAfterPlayerTurnStartVanilla(simulator, player, choices);

        var context = new AfterPlayerTurnStartMirrorContext { Simulator = simulator, Player = player, Choices = choices };
        for (int phase = 0; phase < 3; phase++)
        {
            MirroredHookMask mask = phase switch
            {
                0 => MirroredHookMask.AfterPlayerTurnStartEarly,
                1 => MirroredHookMask.AfterPlayerTurnStart,
                _ => MirroredHookMask.AfterPlayerTurnStartLate
            };
            // 原生 Hook 在每轮重新枚举；轮内固定成员，卡牌接收者仍跟随当前 COW Preview。
            var receivers = new List<CardHookReceiver>();
            foreach (AbstractModel listener in IterateCombatHookListeners(simulator, mask))
            {
                PredictedCard? card = listener is CardModel model
                    ? simulator.State.GetPlayerCombatState(model.Owner).FindCard(model) : null;
                receivers.Add(new(listener, card));
            }
            foreach (CardHookReceiver receiver in receivers)
            {
                AfterPlayerTurnStartMirrors.Invoke(receiver.Current, context, phase);
                if (simulator.HasPendingChoice)
                {
                    // 任意第三方回调的内部进度不属于原版帧合同，回到稳定父节点完整重放。
                    simulator.RejectExecutionContinuation();
                    return false;
                }
            }
        }
        return true;
    }
}
