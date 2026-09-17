using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>
/// 閾佺敳鎴樺＋鑳藉姏瑙﹀彂璇佹嵁锛圖raft锛夈€傚彧璇诲彇鍐荤粨鍚庣殑鐪熷疄鐗屽尯銆佹晫浜轰笌 Power 浜嬪疄锛?/// 娌℃湁瀵瑰簲鏉ユ簮鏃朵笉鍒涘缓鎵胯銆傚厬鐜拌瘉鎹部鐢ㄩ€氱敤鐘舵€佹敼鍠勫洖閫€銆?/// </summary>
internal sealed partial class CombatBeamSolver
{
    private bool IroncladPowerHasTriggerEvidence(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        int remainingTurns = PowerRemainingTurns(child);
        int attackCount = PowerCountType(child, CardType.Attack);
        bool hasAttack = attackCount > 0;
        bool hasBlockSkill = PowerMaxBlockValue(child) > 0;
        bool hasExhaust = PowerCountWithExhaust(child) > 0;
        bool hasVulnerable = PowerCountWithVulnerable(child) > 0
            || PowerEnemyCountWithPower<VulnerablePower>(child) > 0;
        bool aliveEnemy = child.Snapshot.AliveEnemyCount > 0;
        return cardId switch
        {
            "AGGRESSION" => hasAttack && remainingTurns > 1,
            "BARRICADE" => hasBlockSkill,
            "CORRUPTION" => PowerCountType(child, CardType.Skill) > 0,
            "CRIMSON_MANTLE" => remainingTurns > 1,
            "CRUELTY" => hasVulnerable && hasAttack,
            "DARK_EMBRACE" => hasExhaust && remainingTurns > 1,
            "DEMON_FORM" => hasAttack && remainingTurns > 1,
            "FEEL_NO_PAIN" => hasExhaust,
            "HELLRAISER" => PowerCountStrike(child) > 0,
            "INFERNO" => remainingTurns > 1 && aliveEnemy,
            "INFLAME" => hasAttack,
            "JUGGERNAUT" => hasBlockSkill && aliveEnemy,
            "JUGGLING" => attackCount >= 3 || hasAttack && remainingTurns > 1,
            "PYRE" => remainingTurns > 1,
            "RUPTURE" => PowerCountWithSelfDamage(child) > 0,
            "STAMPEDE" => hasAttack && remainingTurns > 1,
            "STONE_ARMOR" => remainingTurns > 1,
            "UNMOVABLE" => hasBlockSkill,
            "VICIOUS" => hasVulnerable && remainingTurns > 1,
            _ => false,
        };
    }

    private int IroncladPowerTriggerProjectionFloor(string cardId, SearchNode child)
    {
        int attacks = PowerCountType(child, CardType.Attack);
        int exhaustSources = PowerCountWithExhaust(child);
        int turns = Math.Max(1, PowerRemainingTurns(child) - 1);
        return cardId switch
        {
            "DEMON_FORM" => SaturatingProduct(4, Math.Max(1, attacks)),
            "INFLAME" => SaturatingProduct(3, Math.Max(1, attacks)),
            "FEEL_NO_PAIN" => SaturatingProduct(4, Math.Max(1, exhaustSources)),
            "JUGGERNAUT" => SaturatingProduct(8, Math.Max(1, PowerCountWithBlockVar(child))),
            "PYRE" => SaturatingProduct(2, turns),
            "STONE_ARMOR" => SaturatingProduct(6, turns),
            "BARRICADE" or "CORRUPTION" or "CRIMSON_MANTLE" or "DARK_EMBRACE"
                or "INFERNO" or "UNMOVABLE" => 1,
            _ => 0,
        };
    }

    private int IroncladPowerProgressEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => 0;

    private PowerEvidenceContribution IroncladPowerRealizedEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => default;
}

