using CombatSolver;

// Local development example: copy this file, edit it and pass --script to strategy-session run.
public sealed class ExampleSearchStrategy : IDevelopmentSearchStrategy
{
    public double Rank(StrategyNodeFeatures node, double builtInScore,
        IReadOnlyDictionary<string, double> parameters)
        => builtInScore + parameters.GetValueOrDefault("persistentBuffWeight") * node.PersistentBuff;

    public double Prioritize(StrategyActionFeatures action, double builtInPriority,
        IReadOnlyDictionary<string, double> parameters)
        => builtInPriority + parameters.GetValueOrDefault("powerCardPriority")
            * (action.CardId == "DEMON_FORM" ? 1 : 0);

    public double Retain(StrategyNodeFeatures node, IReadOnlyDictionary<string, double> parameters)
        => node.PersistentBuff >= parameters.GetValueOrDefault("retainBuffAtLeast", double.PositiveInfinity)
            ? node.PersistentBuff : 0;

    public IReadOnlyList<StrategyMember> OrganizeMembers(IReadOnlyList<StrategyMember> builtIn,
        IReadOnlyDictionary<string, double> parameters) => builtIn;
}
