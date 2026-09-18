using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// 静默猎手能力估值模型基类。机制族与准入政策由本卡池路线政策按稳定 CardId 决定，
/// 公共搜索层不依赖静默猎手枚举。
/// </summary>
internal abstract class SilentPowerCardValuationModel<TCard> : PowerCardValuationModel<TCard>
    where TCard : CardModel
{
    public override PowerCardPool Pool => PowerCardPool.Silent;

    public sealed override PowerCommitmentFamily CommitmentFamily
        => SilentPowerRoutePolicy.FamilyFor(
            PowerCardValuationRegistry.CardIdFor(typeof(TCard)));

    public sealed override PowerRouteAdmissionPolicy AdmissionPolicy
        => SilentPowerRoutePolicy.For(
            PowerCardValuationRegistry.CardIdFor(typeof(TCard)));
}
