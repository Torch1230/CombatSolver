using System.Reflection;
using System.Runtime.ExceptionServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static KnownExoskeletonsNativeObservation? _knownExoskeletonsNativeObservation;

    private sealed record KnownExoskeletonsActualPrefix(
        int Turn, IReadOnlyList<KnownExoskeletonsEnemyState> Enemies,
        int HpLost, int PotionsUsed, int ShuffleEvents, bool PlayerDead);

    // Test-only, scoped to one exact CombatState and its four original Creature identities.
    // This does not change the existing Mercury/Soul/RelicStat observation contract.
    private sealed class KnownExoskeletonsNativeObservation(
        UnattendedTestRunner runner, CombatState combat, Player player,
        IReadOnlyList<Creature> enemies, PropertyInfo turnStateCombatProperty)
    {
        public CombatState Combat { get; } = combat;
        public Player Player { get; } = player;
        public PropertyInfo TurnStateCombatProperty { get; } = turnStateCombatProperty;
        public KnownExoskeletonsActualPrefix? Terminal { get; private set; }
        public bool CombatEnded { get; private set; }
        public ExceptionDispatchInfo? Failure { get; private set; }
        public int ShuffleEvents { get; private set; }
        private readonly int _historyStart = CombatManager.Instance.History.Entries.Count();
        private readonly CombatRoom _room = combat.RunState.CurrentRoom as CombatRoom
            ?? throw new InvalidOperationException("外骨骼虫观察必须在当前战斗房间内建立。");

        public void ObserveCombatEnded(CombatRoom room)
        {
            if (ReferenceEquals(room, _room))
                CombatEnded = true;
        }

        public void ObserveShuffle() => ShuffleEvents++;

        public KnownExoskeletonsActualPrefix Capture()
        {
            int turn = Player.PlayerCombatState?.TurnNumber
                ?? throw new InvalidOperationException("外骨骼虫观察发生在玩家战斗状态清理之后。");
            var history = CombatManager.Instance.History.Entries.ToArray();
            if (history.Length < _historyStart)
                throw new InvalidOperationException("外骨骼虫原版历史在终局快照之前被重置。");
            KnownExoskeletonsEnemyState[] states = enemies.Select(enemy => new KnownExoskeletonsEnemyState(
                enemy.CombatId ?? throw new InvalidOperationException("外骨骼虫原始敌人缺少 CombatId。"),
                Combat.Enemies.Contains(enemy), enemy.IsDead,
                enemy.Monster?.NextMove.Id ?? throw new InvalidOperationException("外骨骼虫原始敌人缺少当前行动。"),
                CaptureActual(Combat, Player, enemy))).ToArray();
            // History catches damage followed by healing within the same action; final HP
            // alone would not prove zero cumulative loss. These are root-relative counters.
            int hpLost = history.Skip(_historyStart).OfType<DamageReceivedEntry>()
                .Where(entry => ReferenceEquals(entry.Receiver, Player.Creature))
                .Sum(entry => Math.Max(0, entry.Result.UnblockedDamage));
            int potionsUsed = history.Skip(_historyStart).OfType<PotionUsedEntry>().Count();
            return new KnownExoskeletonsActualPrefix(turn, Array.AsReadOnly(states),
                hpLost, potionsUsed, ShuffleEvents, Player.Creature.IsDead);
        }

        public void CaptureTerminal()
        {
            try
            {
                if (Terminal != null)
                    throw new InvalidOperationException("外骨骼虫重复进入同一战斗的原版结束入口。");
                Terminal = Capture();
                runner._completedChecks.Add("KnownExoskeletonsNative:AllFourOriginalEnemies:NativePreTeardownSnapshot");
            }
            catch (Exception error)
            {
                Failure = ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }
    }

    // Observation only: do not replace the async method, mutate its turn state or delay cleanup.
    private static void ObserveKnownExoskeletonsCombatEndPrefix(CombatManager __instance, object __0)
    {
        KnownExoskeletonsNativeObservation? observation = _knownExoskeletonsNativeObservation;
        if (observation == null || SimulationNotificationIsolation.IsActive
            || !ReferenceEquals(__instance, CombatManager.Instance)
            || !ReferenceEquals(observation.TurnStateCombatProperty.GetValue(__0), observation.Combat))
            return;
        observation.CaptureTerminal();
    }

    // Mirror only the native Shuffle entry's IsOverOrEnding guard. Counts are sampled after
    // completed native actions and compared with simulator events, not RNG-call counts or
    // Search's ShufflesCrossed (which can collapse several shuffles into one card action).
    private static void ObserveKnownExoskeletonsShufflePrefix(Player __1)
    {
        KnownExoskeletonsNativeObservation? observation = _knownExoskeletonsNativeObservation;
        if (observation == null || SimulationNotificationIsolation.IsActive
            || !ReferenceEquals(__1, observation.Player)
            || !ReferenceEquals(__1.Creature.CombatState, observation.Combat)
            || CombatManager.Instance.IsOverOrEnding)
            return;
        observation.ObserveShuffle();
    }
}
