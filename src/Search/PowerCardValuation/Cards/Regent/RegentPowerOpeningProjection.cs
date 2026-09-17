using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>鍌ㄥ悰寮€灞€鑳藉姏鎶曞奖锛圖raft锛夈€傛槦鏄熴€侀摳閫犮€佸悰鐜嬩箣鍓戜笌鐢熸垚鐗屾潵婧愰兘璇诲彇鐪熷疄鐘舵€併€?/summary>
internal sealed partial class CombatBeamSolver
{
    private int RegentPowerOpeningProjectionPotential(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        int turns = Math.Max(0, PowerRemainingTurns(child) - 1);
        int incoming = PowerIncomingDamage(child);
        int generatedSources = Math.Max(1, PowerCountCardGeneration(child));
        int attacks = Math.Max(1, PowerCountType(child, CardType.Attack));
        return cardId switch
        {
            "ARSENAL" => PowerGrowthFrontierPotential(
                child,
                damagePerAttack: SaturatingProduct(
                    PowerAmountGain<ArsenalPower>(parent, child),
                    generatedSources)),
            "BLACK_HOLE" => PowerPerTriggerDamagePotential(
                child,
                PowerAmountGain<BlackHolePower>(parent, child),
                PowerStars(child) + PowerCountWithStarCost(child)),
            "CHILD_OF_THE_STARS" => PowerPerTriggerBlockPotential(
                child,
                SaturatingProduct(
                    PowerAmountGain<ChildOfTheStarsPower>(parent, child),
                    Math.Max(1, PowerStars(child) + PowerCountWithStarCost(child))),
                Math.Max(1, PowerStars(child)),
                incoming),
            "FURNACE" => PowerPerTurnResourcePotential(
                PowerAmountGain<FurnacePower>(parent, child),
                turns),
            "GENESIS" => PowerPerTurnResourcePotential(
                PowerAmountGain<GenesisPower>(parent, child),
                turns),
            "MONARCHS_GAZE" => PowerPerTriggerDamagePotential(
                child,
                PowerAmountGain<MonarchsGazePower>(parent, child),
                attacks),
            "NEUTRON_AEGIS" => PowerPerTurnBlockPotential(
                child,
                PowerAmountGain<PlatingPower>(parent, child),
                turns,
                incoming),
            "ORBIT" => PowerPerTriggerResourcePotential(
                PowerEnergyUnit(child),
                Math.Max(1, turns)),
            "PALE_BLUE_DOT" => PowerPerTurnResourcePotential(
                PowerAmountGain<PaleBlueDotPower>(parent, child) * PowerEnergyUnit(child),
                turns),
            "PARRY" => PowerPerTurnBlockPotential(
                child,
                PowerPlayerPowerAmount<ParryPower>(child),
                turns,
                incoming),
            "PILLAR_OF_CREATION" => PowerPerTriggerBlockPotential(
                child,
                PowerAmountGain<PillarOfCreationPower>(parent, child),
                generatedSources,
                incoming),
            "SEEKING_EDGE" => PowerPerTurnResourcePotential(
                PowerEnergyUnit(child),
                turns),
            "SPECTRUM_SHIFT" => PowerPerTurnResourcePotential(
                PowerAmountGain<SpectrumShiftPower>(parent, child) * PowerEnergyUnit(child),
                turns),
            "SWORD_SAGE" => PowerPerTurnDamagePotential(
                child,
                Math.Max(1, PowerMaxAttackDamage(child)),
                turns,
                1),
            "THE_SEALED_THRONE" => PowerPerTriggerResourcePotential(
                PowerAmountGain<TheSealedThronePower>(parent, child) * PowerEnergyUnit(child),
                PowerLiveCards(child).Length),
            "TYRANNY" => PowerPerTurnResourcePotential(
                PowerAmountGain<TyrannyPower>(parent, child) * PowerEnergyUnit(child),
                turns),
            "VOID_FORM" => PowerPerTurnResourcePotential(
                PowerAmountGain<VoidFormPower>(parent, child) * PowerEnergyUnit(child),
                turns),
            _ => 0,
        };
    }
}

