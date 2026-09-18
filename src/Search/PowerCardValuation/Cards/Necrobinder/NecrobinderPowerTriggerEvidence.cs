using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>
/// 亡灵契约师能力触发证据（Draft）。奥斯提、灵魂、灾厄、虚无与 2 费牌来源都按真实状态判定。
/// 纯战后收益的 FORBIDDEN_GRIMOIRE 不创建承诺。
/// </summary>
internal sealed partial class CombatBeamSolver
{
    private bool NecrobinderPowerHasTriggerEvidence(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        int remainingTurns = PowerRemainingTurns(child);
        bool hasAttack = PowerCountType(child, CardType.Attack) > 0;
        bool aliveEnemy = child.Snapshot.AliveEnemyCount > 0;
        int souls = PowerCountSouls(child);
        bool hasOsty = PowerHasOstyOrSummonSource(child);
        int ethereal = PowerCountWithEthereal(child);
        return cardId switch
        {
            "CALCIFY" => PowerHasOsty(child),
            "CALL_OF_THE_VOID" => remainingTurns > 1,
            "COUNTDOWN" => aliveEnemy && remainingTurns > 1,
            "DANSE_MACABRE" => remainingTurns > 1 && PowerHasEnergyCostAtLeast(child, 2),
            "DEMESNE" => remainingTurns > 1,
            "DEVOUR_LIFE" => souls > 0 || hasOsty,
            "FRIENDSHIP" => remainingTurns > 1 && hasAttack,
            "HAUNT" => souls > 0,
            "LETHALITY" => hasAttack,
            "NECRO_MASTERY" => hasOsty,
            "NEUROSURGE" => hasAttack || remainingTurns > 1,
            "PAGESTORM" => ethereal > 0,
            "REAPER_FORM" => hasAttack,
            "SENTRY_MODE" => hasOsty || remainingTurns > 1,
            "SHROUD" => PowerCountWithDoom(child) > 0,
            "SLEIGHT_OF_FLESH" => hasAttack || PowerHasDebuffSource(child),
            "SPIRIT_OF_ASH" => ethereal > 0,
            _ => false,
        };
    }

    private int NecrobinderPowerTriggerProjectionFloor(string cardId, SearchNode child)
    {
        int turns = Math.Max(1, PowerRemainingTurns(child) - 1);
        return cardId switch
        {
            "COUNTDOWN" => SaturatingProduct(
                Math.Max(1, PowerPlayerPowerAmount<CountdownPower>(child)),
                turns),
            "LETHALITY" => SaturatingProduct(
                Math.Max(1, PowerMaxAttackDamage(child)),
                2),
            "CALL_OF_THE_VOID" or "DEMESNE" or "FRIENDSHIP" or "NECRO_MASTERY"
                or "NEUROSURGE" or "REAPER_FORM" or "SENTRY_MODE" => 1,
            _ => 0,
        };
    }

    private int NecrobinderPowerProgressEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => 0;

    private PowerEvidenceContribution NecrobinderPowerRealizedEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
        => default;
}

