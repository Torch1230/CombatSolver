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
    int? maximumPotionUses = null)
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
    private readonly bool _detailedDiagnostics = policy.DetailedDiagnostics;
    private readonly int? _maximumPotionUses = maximumPotionUses;
    private readonly SolverTheftPolicy? _theftPolicy = policy.TheftPolicy;
    private readonly LongTermGoals _pursuedLongTermGoals = policy.PursuedLongTermGoals;
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
        _initialEnemyCount,
        _theftPolicy,
        _potionPolicy,
        _run,
        EvaluateStandPat);
    private FinalPlanOrdering? _finalOrdering;
    private FinalPlanOrdering FinalOrdering => _finalOrdering ??= new FinalPlanOrdering(
        _potionPolicy,
        _theftPolicy,
        _pursuedLongTermGoals,
        _bossHpRelief,
        potionFreePolicyBaseline,
        root.InitialPlayerMaxHp,
        policy.Diagnostics,
        _detailedDiagnostics,
        battleDamage);

}
