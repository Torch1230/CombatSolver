using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class PowerPredictionStateSupport
{
    public static SurroundedPower.Direction SurroundedFacing(CombatPredictionSimulator simulator, SurroundedPower power)
        => simulator.StateStore.Peek(power, () => new SurroundedPredictionState(power)).Facing;

    public static bool PanacheAlreadyApplied(CombatPredictionSimulator? simulator, PanachePower power)
        => simulator == null ? power.GetInternalData<PanachePower.Data>().alreadyApplied
            : simulator.StateStore.Peek(power, static value => new PanachePredictionState(value)).AlreadyApplied;

    public static void CaptureRootState(
        CombatPredictionSimulator simulator,
        PowerModel target,
        PowerModel source)
    {
        switch (target, source)
        {
            case (SkittishPower value, SkittishPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new SkittishPredictionState(original));
                break;
            case (PanachePower value, PanachePower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new PanachePredictionState(original));
                break;
            case (SoulboundPower value, SoulboundPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new SoulboundPredictionState(original));
                break;
            case (HellraiserPower value, HellraiserPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new HellraiserPredictionState(original));
                break;
            case (CacophonyPower value, CacophonyPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new CacophonyPredictionState(original));
                break;
            case (AutomationPower value, AutomationPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new AutomationPredictionState(original));
                break;
            case (JugglingPower value, JugglingPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new JugglingPredictionState(original));
                break;
            case (ChainsOfBindingPower value, ChainsOfBindingPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => ChainsOfBindingPredictionState.CaptureRoot(original));
                break;
            case (SurroundedPower value, SurroundedPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new SurroundedPredictionState(original));
                break;
            case (VoidFormPower value, VoidFormPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new VoidFormPredictionState(original));
                break;
            case (FeralPower value, FeralPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new FeralPredictionState(original));
                break;
            case (HardenedShellPower value, HardenedShellPower original):
                _ = simulator.StateStore.GetReadOnly(value, () => new HardenedShellPredictionState(original));
                break;
        }
        // 上面这个 switch 按原版类型写死，第三方登记不进去。克隆会把 _internalData 重置成
        // InitInternalData()，所以靠它保存状态的第三方 Power 同样必须在这里把实机实例的值搬进
        // StateStore，否则模拟一开始读到的就是初值。
        PowerHiddenStateMirrors.CaptureRootState(simulator, target, source);
    }
}
