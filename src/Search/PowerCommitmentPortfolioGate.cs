namespace CombatSolver;

internal static class PowerCommitmentPortfolioGate
{
    internal const string SkippedNoReachablePower = "NoReachableRegisteredPower";

    /// <summary>
    /// 能力成员不是可选的宽度精炼。根牌区存在已登记能力时，它一定进入求解；节点、时间和内存
    /// 由专用预留及搜索自身的安全检查点控制，不能再用基线累计分配量把整条路线拒绝掉。
    /// </summary>
    internal static string? Reject(bool hasReachablePower)
        => hasReachablePower ? null : SkippedNoReachablePower;
}
