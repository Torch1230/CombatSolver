using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed record RecordedPotionUse(string PotionId, uint? TargetCombatId, int Turn, int? SlotIndex, string Origin);
internal sealed record CombatReplayOutcomeSnapshot(
    int InitialHp, int FinalHp, int HpLost, int HpHealed, int SelfDamage,
    bool CombatEnded, bool Survived, int FinalEnemyHp, RecordedPotionUse[] Potions, int UnattributedHpLoss);

// Independent observation ledger; recording must not advance the solver's damage accounting.
internal sealed class CombatReplayOutcome : IDisposable
{
    private Creature? _player;
    private readonly int _initialHp;
    private readonly int _historyStart;
    private ActionQueueSet? _actions;
    private readonly Dictionary<PotionModel, (int Slot, string Origin)> _potionInputs = [];
    private int _hpLost;
    private int _hpHealed;
    private CombatReplayOutcomeSnapshot? _finished;

    public CombatReplayOutcome(CombatState state)
    {
        _player = state.Players.Single().Creature;
        _initialHp = _player.CurrentHp;
        _historyStart = CombatManager.Instance.History.Entries.Count();
        _actions = RunManager.Instance.ActionQueueSet;
        _actions.ActionEnqueued += OnAction;
        _player.CurrentHpChanged += OnHpChanged;
    }

    private void OnHpChanged(int before, int after)
    {
        _hpLost += Math.Max(0, before - after);
        _hpHealed += Math.Max(0, after - before);
    }
    private void OnAction(GameAction action)
    {
        if (action is UsePotionAction use && ReferenceEquals(use.Player.Creature, _player)
            && use.Player.GetPotionAtSlotIndex((int)use.PotionIndex) is PotionModel potion)
            _potionInputs[potion] = ((int)use.PotionIndex, SolverController.IsDeploying ? "solver" : "player");
    }

    public CombatReplayOutcomeSnapshot Capture(CombatState state, bool ended)
    {
        if (_finished != null)
            return _finished;
        Creature player = _player ?? throw new InvalidOperationException("Outcome observation has been disposed.");
        var history = CombatManager.Instance.History.Entries.Skip(_historyStart).ToArray();
        // UnblockedDamage is already capped at the receiver's actual HP loss.
        // OverkillDamage is the separate excess; subtracting it again hides real
        // damage, including lethal hits followed by a death-save revival.
        int accountedDamage = history.OfType<DamageReceivedEntry>().Where(entry => ReferenceEquals(entry.Receiver, player))
            .Sum(entry => Math.Max(0, entry.Result.UnblockedDamage));
        return new CombatReplayOutcomeSnapshot(
            _initialHp, player.CurrentHp, _hpLost, _hpHealed,
            history.OfType<DamageReceivedEntry>().Where(entry => ReferenceEquals(entry.Receiver, player)
                && ReferenceEquals(entry.Dealer, player)).Sum(entry => Math.Max(0, entry.Result.UnblockedDamage)),
            ended, player.CurrentHp > 0, state.Enemies.Sum(enemy => Math.Max(0, enemy.CurrentHp)),
            history.OfType<PotionUsedEntry>().Where(entry => ReferenceEquals(entry.Actor, player))
                .Select(entry => _potionInputs.TryGetValue(entry.Potion, out var input)
                    ? new RecordedPotionUse(entry.Potion.Id.Entry, entry.Target?.CombatId, entry.RoundNumber, input.Slot, input.Origin)
                    : new RecordedPotionUse(entry.Potion.Id.Entry, entry.Target?.CombatId, entry.RoundNumber, null, "system")).ToArray(),
            Math.Max(0, _hpLost - accountedDamage));
    }

    public void Complete(CombatState state)
    {
        _finished = Capture(state, ended: true);
        Dispose();
    }

    public void Dispose()
    {
        if (_actions != null) { _actions.ActionEnqueued -= OnAction; _actions = null; }
        _potionInputs.Clear();
        if (_player != null)
        {
            _player.CurrentHpChanged -= OnHpChanged;
            _player = null;
        }
    }
}
