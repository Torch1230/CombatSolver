namespace CombatSolver;

/// <summary>
/// 能力路线尚未兑现时使用的启动投资。这里只折算战斗前两回合的能量机会成本；真实掉血仍按原值记账。
/// 跑图越靠后，构筑越依赖已经成型的引擎，开局启动的中间惩罚越低。
/// </summary>
internal static class PowerActivationInvestmentPolicy
{
    internal const int ValuePerEnergy = 8;

    internal static int EnergyInvestment(
        int spentEnergy,
        int totalFloor,
        int combatTurnOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(spentEnergy);
        ArgumentOutOfRangeException.ThrowIfNegative(totalFloor);
        ArgumentOutOfRangeException.ThrowIfNegative(combatTurnOffset);
        int investment = checked(spentEnergy * ValuePerEnergy);
        if (combatTurnOffset > 1 || investment == 0)
            return investment;

        int numerator = totalFloor switch
        {
            <= 15 => 8,
            <= 30 => 5,
            _ => 3,
        };
        return (investment * numerator + 7) / 8;
    }
}
