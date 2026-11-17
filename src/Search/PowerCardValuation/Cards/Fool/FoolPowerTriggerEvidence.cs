using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>
/// 愚者能力触发证据（Specified）。旋律、射箭、悲伤与敌人数量都按打出能力后的真实分支牌区与状态判定；
/// 条件逐条对应玩家说明里的“什么时候值得开”。兑现证据统一回退到通用路线进展。
/// </summary>
internal sealed partial class CombatBeamSolver
{
    private bool FoolPowerHasTriggerEvidence(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        _ = parent;
        int remainingTurns = PowerRemainingTurns(child);
        int shoots = FoolCountTag(child, CardTag.Shoot);
        int enemies = child.Snapshot.AliveEnemyCount;
        bool ennuiActive = PowerPlayerPowerAmount<EnnuiPower>(child) > 0;
        return cardId switch
        {
            "ALL_IS_WELL" => FoolCountSadness(child, handOnly: false) > 0 || ennuiActive,
            "ARCHERY" => shoots > 0 && remainingTurns > 2,
            "ECSTASY" => FoolCountSadness(child, handOnly: true) > 0 || ennuiActive,
            // 只在预计五回合内结束的短线战斗保护；狂喜已生效时悲伤会被持续转化，不受此限。
            "ENNUI" => remainingTurns > 1
                && (remainingTurns <= FoolMaximumShortFightTurns
                    || PowerPlayerPowerAmount<EcstasyPower>(child) > 0),
            "EXPLOSIVE_ARROWS" => shoots > 0 && enemies >= 2,
            "HIDDEN_ROOTS" => remainingTurns > 1,
            "HONING" => shoots > 0,
            "LOST_LUTE" => FoolCountMelodyCards(child) >= 2,
            "LULLABY" => FoolDistinctMelodyTags(child) == SimulatedCombatState.MelodyTags.Length,
            "MOTIF" => FoolCountMelodyCards(child) >= 2,
            "MYTHIC_FORM" => remainingTurns > 2,
            "QUIVER" => shoots >= 2 && remainingTurns > 1,
            "SERRATED_ARROWS" => shoots > 0 && FoolCountNonShootAttacks(child) >= 2,
            "SIMPLETON" => remainingTurns > 1 && PowerHandCards(child).Length <= 3,
            "SONG_PATH" => FoolCountTag(child, CardTag.Cho) >= 2,
            "TUNE" => remainingTurns > 1 && FoolCountTag(child, CardTag.Ki) > 0,
            "VIOLENT_CHORD" => FoolCountTag(child, CardTag.Wah) is var wah
                && (wah >= 2 || wah >= 1 && enemies >= 2),
            "YAH" => shoots > 0 && remainingTurns > 1,
            _ => false,
        };
    }

    private int FoolPowerTriggerProjectionFloor(string cardId, SearchNode child)
        => cardId switch
        {
            "LULLABY" => Math.Max(1, PowerPlayerPowerAmount<LullabyPower>(child)),
            _ => 0,
        };

    private int FoolPowerProgressEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => 0;

    private PowerEvidenceContribution FoolPowerRealizedEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => default;
}
