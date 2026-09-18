using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>储君开局能力投影（Draft）。星星、铸造、君王之剑与生成牌来源都读取真实状态。</summary>
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
            "CHILD_OF_THE_STARS" => ChildOfTheStarsPotential(parent, child, incoming),
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
            "ORBIT" => OrbitPotential(parent, child, turns),
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

    /// <summary>群星之子：每花费一点星能获得能力层数格挡，触发次数按实际可花费星能点数计。</summary>
    private int ChildOfTheStarsPotential(SearchNode parent, SearchNode child, int incoming)
    {
        int amount = PowerAmountGain<ChildOfTheStarsPower>(parent, child);
        int triggers = PowerCardProjectionMath.ChildOfTheStarsTriggers(
            PowerStars(child),
            PowerStarSpendCapacity(child));
        int raw = PowerCardProjectionMath.ChildOfTheStarsBlock(amount, triggers);
        return BoundedPrevention(raw, incoming);
    }

    /// <summary>按每回合最大能量估算能量花费，每累计4点返还一次，不按回合数直接当作触发次数。</summary>
    private int OrbitPotential(SearchNode parent, SearchNode child, int turns)
    {
        int amount = PowerAmountGain<OrbitPower>(parent, child);
        if (amount == 0)
            return 0;
        int payouts = PowerCardProjectionMath.OrbitPayouts(
            PowerMaxEnergy(child),
            Math.Max(0, turns));
        return payouts <= 0
            ? 0
            : PowerPerTriggerResourcePotential(
                amount * PowerEnergyUnit(child),
                payouts);
    }
}

