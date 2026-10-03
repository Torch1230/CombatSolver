using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private const string RelicStatTerminalScenarioId = "relic-stat-terminal-v0111";

    // Two explicit single-action roots cover both sides of PowerCmd.Apply's ending guard.
    // Counters are fixture inputs, not search policy: each third attack also completes the ring.
    private async Task<int> RunRelicStatTerminalAsync(CombatState combat, Player player)
    {
        if (combat.Players.Count != 1 || combat.Enemies.Count != 1
            || combat.Enemies[0].Monster is not FuzzyWurmCrawler
            || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || _mercuryTerminalObservation != null || CardSelectCmd.Selector != null)
            throw new InvalidOperationException("遗物属性终局夹具要求独占的单人 Play 毛毛虫根。");
        Creature enemy = combat.Enemies[0];
        foreach (RelicModel relic in player.Relics.ToArray())
            await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await SetBlockAsync(enemy, 0);
        await SetBlockAsync(player.Creature, 0);
        await CreatureCmd.SetMaxHp(enemy, 100);
        await CreatureCmd.SetCurrentHp(enemy, 100);
        SetEnergy(player, 3);
        for (int index = 0; index < 2; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        foreach (string id in new[] { "KUNAI", "SHURIKEN", "RAINBOW_RING" })
            await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = id });
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        Kunai kunai = player.Relics.OfType<Kunai>().Single();
        Shuriken shuriken = player.Relics.OfType<Shuriken>().Single();
        RainbowRing ring = player.Relics.OfType<RainbowRing>().Single();
        int turn = player.PlayerCombatState!.TurnNumber;

        MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "EndCombatInternal"
                && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
        PropertyInfo stateProperty = endCombat.GetParameters()[0].ParameterType.GetProperty("State")
            ?? throw new MissingMemberException("CombatTurnState.State");
        MethodInfo prefix = typeof(UnattendedTestRunner).GetMethod(
            nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ObserveMercuryCombatEndPrefix));
        Harmony patch = new("CombatSolver.Testing.RelicStatTerminal." + _request.RunId);
        MercuryTerminalObservation observation = new(this, combat, player, enemy, stateProperty, "RelicStatTerminal");
        _mercuryTerminalObservation = observation;
        try
        {
            CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
            patch.Patch(endCombat, prefix: new HarmonyMethod(prefix));
            for (int variant = 0; variant < 2; variant++)
            {
                bool lethal = variant == 1;
                EnsureWithinDeadline();
                if (lethal)
                    await CreatureCmd.SetCurrentHp(enemy, 1);
                // The second root intentionally retains the first root's successfully gained stats.
                kunai._attacksPlayedThisTurn = shuriken._attacksPlayedThisTurn = 2;
                ring._attacksPlayedThisTurn = 0;
                ring._skillsPlayedThisTurn = ring._powersPlayedThisTurn = 1;
                ring._activationCountThisTurn = 0;
                MoveStateSnapshot actualBefore = CaptureActual(combat, player, enemy);
                int strengthBefore = player.Creature.GetPower<StrengthPower>()?.Amount ?? 0;
                int dexterityBefore = player.Creature.GetPower<DexterityPower>()?.Amount ?? 0;
                CardModel card = FindActualHandCard(player, "STRIKE_IRONCLAD", 0);
                PlanAction action = new(PlanActionKind.PlayCard, turn, CardId: card.Id.Entry,
                    TargetIndex: 0, TargetCombatId: enemy.CombatId,
                    CardStateKey: CardChoiceSupport.ChoiceCardKey(card));
                CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
                CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat),
                    BattleDamageTracker.Observe(combat),
                    SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat,
                        includeTurnSetup: false, theftPolicy: null));
                List<(string Name, MoveStateSnapshot State)> predictions = [];
                SimulationSnapshot? before = null, full = null, incremental = null;
                using (SimulationNotificationIsolation.Enter())
                {
                    try
                    {
                        before = InvokeForcedTerminalReplay(driver, [], null, 0, null);
                        full = InvokeForcedTerminalReplay(driver, [action], null, 0, null);
                        incremental = InvokeForcedTerminalReplay(driver, [action], before, turn, null);
                        _ = InvokeForcedTerminalMethod(driver, "AssertIncrementalEquivalent",
                            [action, new[] { action }, incremental, full]);
                        foreach ((string name, SimulationSnapshot snapshot) in new[] { ("full", full), ("incremental", incremental) })
                        {
                            SimulatedCombatState state = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
                            if (snapshot.HasRisk || snapshot.PredictionGaps.Any(gap => !gap.Compensated)
                                || snapshot.BoundaryReason != SearchBoundaryReason.None || state.HasPendingChoice
                                || snapshot.AllEnemiesDead != lethal || snapshot.Turn != turn
                                || snapshot.PlayerDead || (lethal && snapshot.CombatEndedTurn != turn))
                                throw new InvalidOperationException("遗物属性夹具未得到严格稳定的目标结算边界。");
                            state.AssertForkable();
                            predictions.Add((name, CaptureSimulated(snapshot.Simulator, state, player, enemy)));
                            int expectedGain = lethal ? 0 : 2;
                            if (state.GetAmount<StrengthPower>(player.Creature) != strengthBefore + expectedGain
                                || state.GetAmount<DexterityPower>(player.Creature) != dexterityBefore + expectedGain)
                                throw new InvalidOperationException($"遗物属性 {name}/lethal={lethal} 的力量或敏捷不正确。");
                        }
                        AssertSnapshotEqual(CaptureSimulated(before.Simulator,
                            (SimulatedCombatState)before.Simulator.State.CombatState, player, enemy),
                            actualBefore, "RelicStatTerminal", $"root-unchanged:{variant}");
                    }
                    finally
                    {
                        before?.ReleaseSimulator();
                        full?.ReleaseSimulator();
                        incremental?.ReleaseSimulator();
                    }
                }
                AssertSnapshotEqual(CaptureActual(combat, player, enemy), actualBefore,
                    "RelicStatTerminal", $"live-unchanged:{variant}");
                KnownSoulNativeSelector selector = new(player, []);
                using (CardSelectCmd.PushSelector(selector))
                {
                    if (!card.TryManualPlay(enemy))
                        throw new InvalidOperationException("原版拒绝遗物属性夹具的唯一指定攻击。");
                    await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                    selector.AssertConsumed();
                }
                observation.Failure?.Throw();
                if (lethal)
                {
                    while (observation.Snapshot == null || !observation.CombatEnded || CombatManager.Instance.IsInProgress)
                    {
                        EnsureWithinDeadline();
                        observation.Failure?.Throw();
                        await NextFrameAsync();
                    }
                }
                else if (!CombatManager.Instance.IsInProgress || observation.Snapshot != null)
                    throw new InvalidOperationException("遗物属性非致死对照意外终局。");
                MoveStateSnapshot actual = lethal ? observation.Snapshot! : CaptureActual(combat, player, enemy);
                foreach ((string name, MoveStateSnapshot predicted) in predictions)
                {
                    AssertSnapshotEqual(predicted, actual, "RelicStatTerminal", $"{name}:lethal={lethal}");
                    _completedChecks.Add($"RelicStatTerminal:KunaiShurikenRainbow:{name}:lethal={lethal}:StrictState");
                }
                Entry.Logger.Info($"[CombatSolver/Test] RELIC_STAT_TERMINAL lethal={lethal} turn={turn} " +
                    $"hp={actual.PlayerHp} enemy_hp={actual.EnemyHp}");
            }
            if (observation.Turn != turn)
                throw new InvalidOperationException("遗物属性原版终局回合错误。");
            return observation.Turn;
        }
        finally
        {
            try { patch.Unpatch(endCombat, prefix); }
            finally
            {
                CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded;
                _mercuryTerminalObservation = null;
            }
        }
    }
}
