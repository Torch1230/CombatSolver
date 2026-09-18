using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>
/// 储君能力触发证据（Draft）。星星、铸造、君王之剑与生成牌来源都按真实牌区与 Power 判定。
/// 纯战后收益的 ROYALTIES 不创建承诺（准入政策 NoInCombatCommitment）。
/// </summary>
internal sealed partial class CombatBeamSolver
{
    private bool RegentPowerHasTriggerEvidence(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        int remainingTurns = PowerRemainingTurns(child);
        bool hasAttack = PowerCountType(child, MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack) > 0;
        bool aliveEnemy = child.Snapshot.AliveEnemyCount > 0;
        bool hasGeneration = PowerHasCardGenerationSource(child);
        bool hasStarCost = PowerHasStarCostCard(child);
        bool hasStarGain = PowerHasStarGainSource(child);
        bool hasForge = PowerHasForgeSource(child);
        bool hasBlade = PowerHasSovereignBladeSource(child);
        return cardId switch
        {
            "ARSENAL" => hasGeneration && remainingTurns > 1,
            "BLACK_HOLE" => (hasStarCost || hasStarGain) && aliveEnemy,
            "CHILD_OF_THE_STARS" => hasStarCost,
            "FURNACE" => hasForge || remainingTurns > 1,
            "GENESIS" => remainingTurns > 1,
            "MONARCHS_GAZE" => hasAttack && aliveEnemy,
            "NEUTRON_AEGIS" => remainingTurns > 1,
            "ORBIT" => remainingTurns > 1,
            "PALE_BLUE_DOT" => remainingTurns > 1 && PowerHandCards(child).Length > 0,
            "PARRY" => hasBlade,
            "PILLAR_OF_CREATION" => hasGeneration,
            "SEEKING_EDGE" => hasForge || remainingTurns > 1,
            "SPECTRUM_SHIFT" => remainingTurns > 1,
            "SWORD_SAGE" => hasBlade,
            "THE_SEALED_THRONE" => remainingTurns > 1 && PowerLiveCards(child).Length > 0,
            "TYRANNY" => remainingTurns > 1 && PowerHandCards(child).Length > 0,
            "VOID_FORM" => remainingTurns > 1,
            _ => false,
        };
    }

    private int RegentPowerTriggerProjectionFloor(string cardId, SearchNode child)
    {
        int turns = Math.Max(1, PowerRemainingTurns(child) - 1);
        return cardId switch
        {
            "FURNACE" => SaturatingProduct(
                Math.Max(1, PowerPlayerPowerAmount<FurnacePower>(child)),
                turns),
            "GENESIS" => SaturatingProduct(
                Math.Max(1, PowerPlayerPowerAmount<GenesisPower>(child)),
                turns),
            "NEUTRON_AEGIS" => Math.Max(1, PowerPlayerPowerAmount<PlatingPower>(child)),
            "ARSENAL" or "SEEKING_EDGE" or "SPECTRUM_SHIFT" or "TYRANNY"
                or "VOID_FORM" => 1,
            _ => 0,
        };
    }

    private int RegentPowerProgressEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => 0;

    private PowerEvidenceContribution RegentPowerRealizedEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => default;
}

