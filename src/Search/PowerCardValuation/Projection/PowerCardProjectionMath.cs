namespace CombatSolver;

/// <summary>
/// 生产投影使用的纯计算。把需要合同覆盖的数值公式从 solver partial 抽出，
/// 使它们能进入不依赖游戏引擎的合同检查。
/// </summary>
internal static class PowerCardProjectionMath
{
    /// <summary>自动化：每抽 10 张牌返 1 次能量，按实际抽牌量和剩余回合折算。</summary>
    internal static int AutomationPayouts(int drawsPerTurn, int turns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(drawsPerTurn);
        ArgumentOutOfRangeException.ThrowIfNegative(turns);
        return drawsPerTurn * turns / 10;
    }

    /// <summary>环绕轨道：每累计花费 4 点能量返 1 次，按每回合最大能量和剩余回合估算。</summary>
    internal static int OrbitPayouts(int maxEnergy, int turns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEnergy);
        ArgumentOutOfRangeException.ThrowIfNegative(turns);
        return maxEnergy * turns / 4;
    }

    /// <summary>群星之子：触发次数 = 实际可花费星能点数（受当前星能上限约束），按点数而非牌张数。</summary>
    internal static int ChildOfTheStarsTriggers(int stars, int starSpendCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stars);
        ArgumentOutOfRangeException.ThrowIfNegative(starSpendCapacity);
        return Math.Min(stars, starSpendCapacity);
    }

    /// <summary>群星之子：每花费一点星能获得能力层数格挡。</summary>
    internal static int ChildOfTheStarsBlock(int amountPerStar, int triggers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amountPerStar);
        ArgumentOutOfRangeException.ThrowIfNegative(triggers);
        return (int)Math.Min(int.MaxValue, (long)amountPerStar * triggers);
    }

    /// <summary>
    /// 缓冲：只阻止若干次生命损失，单次按平均伤害计；多段小伤害不会按总伤害整体抵消。
    /// </summary>
    internal static int BufferPrevention(int charges, int incomingDamage, int incomingHits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(charges);
        ArgumentOutOfRangeException.ThrowIfNegative(incomingDamage);
        ArgumentOutOfRangeException.ThrowIfNegative(incomingHits);
        if (charges <= 0 || incomingDamage <= 0)
            return 0;
        int hits = Math.Max(1, incomingHits);
        int perHit = Math.Max(1, incomingDamage / hits);
        return (int)Math.Min(incomingDamage, (long)charges * perHit);
    }

    /// <summary>凶恶：每次给予易伤抽牌；触发次数按可打出易伤来源数计，不按敌人数放大。</summary>
    internal static int ViciousDrawTriggers(int vulnerableSources)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vulnerableSources);
        return vulnerableSources;
    }
}
