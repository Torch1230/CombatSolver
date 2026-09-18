using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;

namespace CombatSolver;

/// <summary>
/// 故障机器人能力触发证据（Draft）。球、集中、状态牌与能量牌都按冻结后的真实状态判定。
/// </summary>
internal sealed partial class CombatBeamSolver
{
    private bool DefectPowerHasTriggerEvidence(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        int remainingTurns = PowerRemainingTurns(child);
        int orbCount = PowerOrbCount(child);
        int channelSources = PowerCountOrbChannel(child);
        bool hasAttack = PowerCountType(child, CardType.Attack) > 0;
        bool hasBlockSkill = PowerMaxBlockValue(child) > 0;
        bool hasPowerSource = PowerCountType(child, CardType.Power) > 0;
        int statusSources = PowerCountWithStatusVar(child);
        return cardId switch
        {
            "BIASED_COGNITION" => orbCount > 0 || channelSources > 0,
            "BUFFER" => PowerIncomingDamage(child) > 0
                || PowerForecastIncomingHits(child, 1) > 0,
            "BULK_UP" => hasAttack && hasBlockSkill,
            "CAPACITOR" => orbCount > 0 || channelSources > 0,
            "CONSUMING_SHADOW" => orbCount > 0,
            "COOLANT" => PowerDistinctOrbTypes(child) > 0,
            "CREATIVE_AI" => remainingTurns > 1,
            "DEFRAGMENT" => orbCount > 0 || channelSources > 0,
            "ECHO_FORM" => remainingTurns > 1 && (hasAttack || hasBlockSkill),
            "FERAL" => PowerCountZeroCostAttack(child) > 0,
            "HAILSTORM" => PowerOrbCount<FrostOrb>(child) > 0,
            "ITERATION" => PowerHasStatusOrCurseInHand(child) || statusSources > 0,
            "LOOP" => orbCount > 0,
            "MACHINE_LEARNING" => remainingTurns > 1,
            "SMOKESTACK" => statusSources > 0,
            "SPINNER" => remainingTurns > 1,
            "STORM" => hasPowerSource,
            "SUBROUTINE" => hasPowerSource,
            "THUNDER" => PowerOrbCount<LightningOrb>(child) > 0,
            "TRASH_TO_TREASURE" => statusSources > 0,
            _ => false,
        };
    }

    private int DefectPowerTriggerProjectionFloor(string cardId, SearchNode child)
    {
        int turns = Math.Max(1, PowerRemainingTurns(child) - 1);
        return cardId switch
        {
            "BIASED_COGNITION" => SaturatingProduct(
                Math.Max(1, PowerFocus(child)),
                Math.Max(1, PowerOrbCount(child))),
            "DEFRAGMENT" => SaturatingProduct(
                Math.Max(1, PowerFocus(child)),
                Math.Max(1, PowerOrbCount(child))),
            "BULK_UP" => SaturatingProduct(
                Math.Max(1, PowerCountType(child, CardType.Attack)),
                3),
            "CAPACITOR" or "CONSUMING_SHADOW" or "COOLANT" or "CREATIVE_AI"
                or "ECHO_FORM" or "LOOP" or "MACHINE_LEARNING" or "SPINNER"
                or "HAILSTORM" => 1,
            _ => 0,
        };
    }

    private int DefectPowerProgressEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => 0;

    private PowerEvidenceContribution DefectPowerRealizedEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => default;
}

