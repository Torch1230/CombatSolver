namespace CombatSolver;

/// <summary>Local replay scripts only. Values are copied from a search branch, never from live combat.</summary>
public readonly record struct StrategyNodeFeatures(
    double BaseScore, int Turn, int ActionCount, int Energy, int Stars,
    int PlayerHp, int ProjectedPlayerHp, int PlayerBlock, int EnemyHp,
    int AliveEnemies, int PersistentBuff, int LatentSetup, int FutureResource,
    int ReplayPotential, int DelayedDamage, int EnemyWeakTurns,
    int EnemyStrengthSuppression, int RetainedAttack, int PotionCount,
    bool Terminal);

public readonly record struct StrategyActionFeatures(
    StrategyNodeFeatures Node, string CardId, int Damage, int Block,
    int EnergyCost, int StarsCost, double NormalizedValue);

public readonly record struct StrategyMember(
    int BeamWidth, bool SecondRankBand = false, bool BaseScoreOnly = false,
    bool AggressivePowerCommitment = false, bool OffensiveRefinement = false,
    bool BoundedRefinement = false);

/// <summary>Scripts may change search heuristics, but not simulation, state equality or final outcomes.</summary>
public interface IDevelopmentSearchStrategy
{
    double Rank(StrategyNodeFeatures node, double builtInScore, IReadOnlyDictionary<string, double> parameters)
        => builtInScore;
    double Prioritize(StrategyActionFeatures action, double builtInPriority, IReadOnlyDictionary<string, double> parameters)
        => builtInPriority;
    double Retain(StrategyNodeFeatures node, IReadOnlyDictionary<string, double> parameters)
        => 0;
    IReadOnlyList<StrategyMember> OrganizeMembers(
        IReadOnlyList<StrategyMember> builtIn, IReadOnlyDictionary<string, double> parameters)
        => builtIn;
}

internal sealed class DevelopmentSearchStrategy(
    IDevelopmentSearchStrategy Script,
    IReadOnlyDictionary<string, double> Parameters)
{
    internal static StrategyNodeFeatures Features(SearchNode node)
    {
        SimulationSnapshot value = node.Snapshot;
        return new StrategyNodeFeatures(node.Score, node.Turn, node.ActionCount,
            value.Energy, value.Stars, value.PlayerHp, value.ProjectedPlayerHp,
            value.PlayerBlock, value.EnemyHp, value.AliveEnemyCount,
            value.PersistentBuffValue, value.LatentSetupValue, value.FutureResourceValue,
            value.ReplayPotentialValue, value.DelayedDamageValue, value.EnemyWeakTurns,
            value.EnemyStrengthSuppression, value.RetainedAttackValue, node.PotionCount,
            node.IsTerminal);
    }

    internal double Rank(SearchNode node, double builtIn)
        => Finite(Script.Rank(Features(node), builtIn, Parameters), "Rank");

    internal double Prioritize(SearchNode node, string cardId, int damage, int block,
        int energyCost, int starsCost, double normalized, double builtIn)
        => Finite(Script.Prioritize(new StrategyActionFeatures(Features(node), cardId,
            damage, block, energyCost, starsCost, normalized), builtIn, Parameters), "Prioritize");

    internal double Retain(SearchNode node)
        => Finite(Script.Retain(Features(node), Parameters), "Retain");

    private static double Finite(double value, string hook)
        => double.IsFinite(value) ? value : throw new InvalidDataException($"Strategy {hook} returned a non-finite score.");

    internal IReadOnlyList<BeamWidthPortfolioMemberSpec> OrganizeMembers(
        IReadOnlyList<BeamWidthPortfolioMemberSpec> builtIn)
    {
        StrategyMember[] input = builtIn.Select(static member => new StrategyMember(
            member.BeamWidth, member.SecondRankBand, member.BaseScoreOnly,
            member.AggressivePowerCommitment, member.OffensiveRefinement,
            member.BoundedRefinement)).ToArray();
        IReadOnlyList<StrategyMember> output = Script.OrganizeMembers(input, Parameters)
            ?? throw new InvalidDataException("Strategy returned null members.");
        if (output.Count is < 1 or > 16 || output.Any(static member => member.BeamWidth is < 1 or > 512))
            throw new InvalidDataException("Strategy member count must be 1..16 and Beam width 1..512.");
        return output.Select(static member => new BeamWidthPortfolioMemberSpec(
            member.BeamWidth, member.SecondRankBand, member.BaseScoreOnly,
            member.AggressivePowerCommitment, member.OffensiveRefinement,
            member.BoundedRefinement)).ToArray();
    }
}
