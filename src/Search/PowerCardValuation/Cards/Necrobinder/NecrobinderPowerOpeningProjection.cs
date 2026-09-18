using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>亡灵契约师开局能力投影（Draft）。奥斯提、灵魂、灾厄与虚无来源都读取真实状态。</summary>
internal sealed partial class CombatBeamSolver
{
    private int NecrobinderPowerOpeningProjectionPotential(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        int turns = Math.Max(0, PowerRemainingTurns(child) - 1);
        int incoming = PowerIncomingDamage(child);
        int souls = PowerCountSouls(child);
        int ethereal = PowerCountWithEthereal(child);
        int doomSources = Math.Max(1, PowerCountWithDoom(child));
        int debuffSources = Math.Max(1, PowerHasDebuffSource(child) ? 1 : 0);
        return cardId switch
        {
            "CALCIFY" => PowerPerTurnDamagePotential(
                child,
                PowerAmountGain<CalcifyPower>(parent, child),
                turns,
                1),
            "CALL_OF_THE_VOID" => PowerPerTurnResourcePotential(
                PowerEnergyUnit(child),
                turns),
            "COUNTDOWN" => PowerPerTurnDamagePotential(
                child,
                PowerAmountGain<CountdownPower>(parent, child),
                turns,
                1),
            "DANSE_MACABRE" => PowerPerTriggerBlockPotential(
                child,
                PowerAmountGain<DanseMacabrePower>(parent, child),
                PowerCountCardsCostAtLeast(child, 2),
                incoming),
            "DEMESNE" => PowerPerTurnResourcePotential(
                PowerAmountGain<DemesnePower>(parent, child) * PowerEnergyUnit(child),
                turns),
            "DEVOUR_LIFE" => PowerPerTriggerResourcePotential(
                PowerAmountGain<DevourLifePower>(parent, child),
                souls),
            "FRIENDSHIP" => PowerPerTurnResourcePotential(
                PowerAmountGain<FriendshipPower>(parent, child) * PowerEnergyUnit(child),
                turns),
            "HAUNT" => PowerPerTriggerDamagePotential(
                child,
                PowerAmountGain<HauntPower>(parent, child),
                souls),
            "LETHALITY" => PowerPerTriggerDamagePotential(
                child,
                1,
                Math.Max(
                    1,
                    PowerMaxAttackDamage(child)
                        * PowerAmountGain<LethalityPower>(parent, child) / 100)),
            "NECRO_MASTERY" => PowerPerTurnResourcePotential(1, turns),
            "NEUROSURGE" => PowerPerTriggerResourcePotential(
                PowerAmountGain<NeurosurgePower>(parent, child) * PowerEnergyUnit(child),
                1),
            "PAGESTORM" => PowerPerTriggerResourcePotential(
                PowerEnergyUnit(child),
                ethereal),
            "REAPER_FORM" => PowerPerTriggerDamagePotential(
                child,
                1,
                PowerTotalAttackDamage(child)),
            "SENTRY_MODE" => PowerPerTurnResourcePotential(
                PowerEnergyUnit(child),
                turns),
            "SHROUD" => PowerPerTriggerBlockPotential(
                child,
                PowerAmountGain<ShroudPower>(parent, child),
                doomSources,
                incoming),
            "SLEIGHT_OF_FLESH" => PowerPerTriggerDamagePotential(
                child,
                PowerAmountGain<SleightOfFleshPower>(parent, child),
                debuffSources),
            "SPIRIT_OF_ASH" => PowerPerTriggerBlockPotential(
                child,
                PowerAmountGain<SpiritOfAshPower>(parent, child),
                ethereal,
                incoming),
            _ => 0,
        };
    }
}

