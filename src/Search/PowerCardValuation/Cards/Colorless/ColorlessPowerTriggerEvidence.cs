using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>
/// 无色能力触发证据（Draft）。无色能力按实际 CardId 识别，可在任意角色持有。
/// </summary>
internal sealed partial class CombatBeamSolver
{
    private bool ColorlessPowerHasTriggerEvidence(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        int remainingTurns = PowerRemainingTurns(child);
        bool hasAttack = PowerCountType(child, CardType.Attack) > 0;
        bool hasSkill = PowerCountType(child, CardType.Skill) > 0;
        bool hasBlockSkill = PowerMaxBlockValue(child) > 0;
        bool aliveEnemy = child.Snapshot.AliveEnemyCount > 0;
        return cardId switch
        {
            "AUTOMATION" => remainingTurns > 1,
            "CALAMITY" => hasAttack,
            "ENTROPY" => remainingTurns > 1 && PowerHandCards(child).Length > 0,
            "ETERNAL_ARMOR" => remainingTurns > 1,
            "FASTEN" => PowerHasTag(child, CardTag.Defend),
            "MAYHEM" => remainingTurns > 1
                && child.Snapshot.Simulator.State.GetPlayerCombatState(_player)
                    .DrawPile.Cards.Count > 0,
            "NOSTALGIA" => hasAttack || hasSkill,
            "PANACHE" => PowerHandCards(child).Length > 0,
            "PREP_TIME" => hasAttack,
            "PROWESS" => hasAttack || hasBlockSkill,
            "ROLLING_BOULDER" => aliveEnemy && remainingTurns > 1,
            "STRATAGEM" => remainingTurns > 1,
            _ => false,
        };
    }

    private int ColorlessPowerTriggerProjectionFloor(string cardId, SearchNode child)
    {
        int turns = Math.Max(1, PowerRemainingTurns(child) - 1);
        return cardId switch
        {
            "ETERNAL_ARMOR" => SaturatingProduct(
                Math.Max(1, PowerPlayerPowerAmount<PlatingPower>(child)),
                turns),
            "MAYHEM" => 1,
            "PROWESS" => SaturatingProduct(
                Math.Max(1, PowerPlayerPowerAmount<StrengthPower>(child)),
                Math.Max(1, PowerCountType(child, CardType.Attack))),
            "ROLLING_BOULDER" => SaturatingProduct(
                Math.Max(1, PowerPlayerPowerAmount<RollingBoulderPower>(child)),
                turns),
            _ => 0,
        };
    }

    private int ColorlessPowerProgressEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => 0;

    private PowerEvidenceContribution ColorlessPowerRealizedEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => default;
}

