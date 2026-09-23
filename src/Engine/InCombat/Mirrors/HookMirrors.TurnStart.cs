using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors;

internal static partial class HookMirrors
{
    // 按原生 Hook 的监听表顺序快照；选择暂停后由既有动作重放恢复。
    public static bool BeforeSideTurnStart(
        CombatPredictionSimulator simulator, CombatSide side, IReadOnlyList<Creature> participants)
    {
        BeforeSideTurnStartMirrors.Seal();
        if (simulator.HasPendingChoice)
            return false;

        bool hasExternal = false;
        foreach (AbstractModel listener in IterateCombatHookListeners(simulator, MirroredHookMask.BeforeSideTurnStart))
        {
            if (listener.GetType().Assembly != typeof(AbstractModel).Assembly)
            {
                hasExternal = true;
                break;
            }
        }
        // 无第三方时保留原版历史批次顺序；扩展路径重用相同单项结算体。
        if (!hasExternal && simulator.State.CombatState is SimulatedCombatState combat)
            return TurnStartRelicSupport.TriggerBeforeSideTurnStart(simulator, combat, participants)
                && !TurnStartPowerSupport.TriggerBeforeSideTurnStart(simulator, combat, participants);

        CardHookReceiver? first = null;
        List<CardHookReceiver>? remaining = null;
        foreach (AbstractModel listener in IterateCombatHookListeners(simulator, MirroredHookMask.BeforeSideTurnStart))
        {
            PredictedCard? card = listener is CardModel model
                ? simulator.State.GetPlayerCombatState(model.Owner).FindCard(model)
                : null;
            var receiver = new CardHookReceiver(listener, card);
            // 单监听者无需分配额外集合。
            if (first is null)
                first = receiver;
            else
                (remaining ??= []).Add(receiver);
        }
        if (first is null)
            return true;

        var context = new BeforeSideTurnStartMirrorContext
        {
            Simulator = simulator,
            Side = side,
            Participants = participants
        };
        // Membership stays fixed even if a callback changes the roster or card previews.
        // Terminal checks belong to the phase boundary, not between already-selected listeners.
        BeforeSideTurnStartMirrors.Invoke(first.Value.Current, context);
        if (simulator.HasPendingChoice)
            return false;
        if (remaining is null)
            return true;
        foreach (CardHookReceiver receiver in remaining)
        {
            BeforeSideTurnStartMirrors.Invoke(receiver.Current, context);
            if (simulator.HasPendingChoice)
                return false;
        }
        return true;
    }
}
