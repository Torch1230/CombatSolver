using System.Diagnostics;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using BufferCard = MegaCrit.Sts2.Core.Models.Cards.Buffer;

namespace CombatSolver;

internal readonly record struct PrimarySearchIncumbent(
    int StrategicHpDeficit,
    int CombatEndedTurn);

internal sealed partial class CombatBeamSolver(
    CombatRootSnapshot root,
    SolverDisplayNames displayNames,
    BattleDamageSnapshot battleDamage,
    SearchPolicySnapshot policy,
    CancellationToken cancellationToken = default,
    Action<SolverProgress>? progressCallback = null,
    SolverSearchProfile? searchProfile = null,
    int? shortCheckpointMilliseconds = null,
    SolverPotionPolicy? potionPolicyOverride = null,
    PotionFreePolicyBaseline? potionFreePolicyBaseline = null,
    int? maximumPotionUses = null,
    IReadOnlyList<PlanAction>? fixedPrefixActions = null,
    int? minimumPotionUses = null,
    PrimarySearchIncumbent? primaryIncumbent = null)
{
    private readonly SolverSearchProfile _profile = searchProfile ?? SolverSearchProfile.Short;
    private readonly SearchRunContext _run = new(
        policy.MeasurePhasePerformance,
        policy.FramePressureSignal);
    private readonly int? _shortCheckpointMilliseconds = shortCheckpointMilliseconds;
    private readonly bool _includeTurnSetup = policy.IncludeTurnSetup;
    private readonly Player _player = root.PlayerIdentity;
    private readonly IntentForecast _forecast = root.Forecast;
    private readonly int _startTurnNumber = root.StartTurnNumber;
    private readonly int _initialEnemyCount = root.Enemies.Count;
    private readonly bool _isActEndingBoss = root.IsActEndingBoss;
    private readonly BossHpRelief _bossHpRelief = root.BossHpRelief;
    private readonly BossHpRelief _strategicBossHpRelief = ActEndingBossPolicy.ResolveStrategicHpRelief(
        root.BossHpRelief,
        policy.ActTransitionBossHpStrategy,
        policy.FinalBossHpStrategy);
    private readonly int _acceptableBattleHpLoss = policy.AcceptableBattleHpLoss;
    private readonly bool _detailedDiagnostics = policy.DetailedDiagnostics;
    private readonly int? _maximumPotionUses = maximumPotionUses;
    private readonly int _minimumPotionUses = minimumPotionUses ?? 0;
    private readonly PotionFreePolicyBaseline? _potionFreePolicyBaseline = potionFreePolicyBaseline;
    private PrimarySearchIncumbent? _primaryIncumbent = primaryIncumbent;
    private readonly SearchInteractionState? _interaction = policy.Interaction;
    private readonly IReadOnlyList<PlanAction> _fixedPrefixActions = fixedPrefixActions ?? [];
    private readonly string? _progressPhaseOverride = DescribePotionProgressPhase(
        displayNames,
        potionPolicyOverride,
        maximumPotionUses,
        minimumPotionUses,
        fixedPrefixActions);
    private readonly SolverTheftPolicy? _theftPolicy = policy.TheftPolicy;
    private readonly PotionStrategySnapshot _potionStrategy = policy.PotionStrategy;
    private readonly bool _forceAllPotionsDisabled = potionPolicyOverride == SolverPotionPolicy.Disabled;
    private readonly bool _enforcePotionDirectives = potionPolicyOverride == null;
    private readonly SolverPotionPolicy _potionPolicy = potionPolicyOverride
        ?? (policy.TheftPolicy == SolverTheftPolicy.PreserveResources
            ? SolverPotionPolicy.Smart
            : policy.PotionPolicy == SolverPotionPolicy.RequireAtLeastOne
                && battleDamage.PotionsUsedSoFar > 0
                    ? SolverPotionPolicy.Smart
                    : policy.PotionPolicy);
    private BeamRetentionPolicy? _retention;
    private BeamRetentionPolicy Retention => _retention ??= new BeamRetentionPolicy(
        _profile,
        _isActEndingBoss,
        _strategicBossHpRelief,
        root.PostCombatRelicHeal,
        _initialEnemyCount,
        root.InitialPlayerHp,
        root.InitialPlayerMaxHp,
        root.HasUnusedCardReplayAllocator,
        _theftPolicy,
        _potionPolicy,
        _potionStrategy,
        _enforcePotionDirectives,
        root.HasRenewablePotionShapedRock,
        _run,
        EvaluateStandPat);
    private FinalPlanOrdering? _finalOrdering;
    private FinalPlanOrdering FinalOrdering => _finalOrdering ??= new FinalPlanOrdering(
        _potionPolicy,
        _potionStrategy,
        _enforcePotionDirectives,
        root.HasRenewablePotionShapedRock,
        _theftPolicy,
        _strategicBossHpRelief,
        root.PostCombatRelicHeal,
        _potionFreePolicyBaseline,
        root.InitialPlayerMaxHp,
        _minimumPotionUses,
        policy.Diagnostics,
        _detailedDiagnostics,
        battleDamage);

    private bool AllowsPotionUse(int slot, string potionId)
        => _potionStrategy.AllowsExplicitUse(
            slot,
            potionId,
            _potionPolicy,
            _forceAllPotionsDisabled);

    private static int ExplicitPotionUseCount(SearchNode node)
        => PotionUsePolicy.ExplicitUseCount(
            node.PotionCount,
            node.Snapshot.AutomaticPotionUseCount);

    internal static string? DescribePotionProgressPhase(
        SolverDisplayNames displayNames,
        SolverPotionPolicy? potionPolicyOverride,
        int? maximumPotionUses,
        int? minimumPotionUses,
        IReadOnlyList<PlanAction>? fixedPrefixActions)
    {
        if (potionPolicyOverride == SolverPotionPolicy.Disabled)
            return "正在搜索无药路线";

        string[] potionNames = (fixedPrefixActions ?? [])
            .Where(action => action.Kind == PlanActionKind.UsePotion && action.PotionId != null)
            .Select(action => displayNames.Potion(action.PotionId!))
            .ToArray();
        if (potionNames.Length == 1)
            return $"正在搜索使用 {potionNames[0]} 路线";
        if (potionNames.Length == 2)
            return $"正在搜索使用 {potionNames[0]} 和 {potionNames[1]} 路线";
        if (potionNames.Length > 2)
        {
            return $"正在搜索使用 {string.Join("、", potionNames[..^1])} " +
                $"和 {potionNames[^1]} 路线";
        }
        if (potionPolicyOverride == SolverPotionPolicy.RequireAtLeastOne
            || potionPolicyOverride == SolverPotionPolicy.Smart && maximumPotionUses.HasValue)
        {
            if (minimumPotionUses is > 0
                && maximumPotionUses == minimumPotionUses)
            {
                return $"正在搜索恰好 {minimumPotionUses} 瓶药路线";
            }
            return "正在搜索用药路线";
        }
        return null;
    }

}
