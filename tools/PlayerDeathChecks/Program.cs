using CombatSolver;

int checks = 0;
foreach (bool hasOsty in new[] { true, false })
foreach (bool pending in new[] { false, true })
{
    Player player = new();
    Creature enemy = new();
    SimulatedCombatState combat = new();
    PowerModel removed = new(player.Creature, removeOnDeath: true, amount: 3);
    PowerModel preserved = new(player.Creature, removeOnDeath: false, amount: 1);
    PowerModel enemyPower = new(enemy, removeOnDeath: true, amount: 2);
    PowerModel debuff = new(player.Creature, removeOnDeath: true, amount: -2);
    combat.Powers.AddRange([removed, preserved, enemyPower, debuff]);
    combat.Creatures.AddRange([player.Creature, enemy]);
    Simulator sim = new(combat, player, hasOsty, pending);
    sim.OnKill = () =>
    {
        Require(removed.Amount == 0 && debuff.Amount == 0, "Dead player's powers leaked into pet death hooks");
        Require(combat.CompletedDeaths.Contains(player.Creature), "Player death phase must finish before pet cleanup");
        Require(preserved.Amount == 1, "Death-persistent power removed");
        Require(enemyPower.Amount == 2, "Other owner's power removed");
        Require(sim.PlayerState.OrbQueue.Count == 0, "Orbs must clear before pet cleanup");
    };
    Require(sim.Run(player) == !(hasOsty && pending), "Pending pet death result lost");
    Require(removed.Amount == 0 && debuff.Amount == 0, "Player without pet retained removable powers");
    Require(preserved.Amount == 1 && enemyPower.Amount == 2, "Owner/removal policy changed");
    Require(sim.Kills == (hasOsty ? 1 : 0), "Pet cleanup count changed");
    Require(sim.Forced == hasOsty, "Existing forced pet cleanup changed");
    Require(combat.Mutations == 2, "Power removal must occur once per active power");
    Require(sim.PlayerState.OrbQueue.Count == 0, "Orb cleanup missing");
    Require(combat.CompletedDeaths.SetEquals([player.Creature]), "Only the player's death phase should be committed by this entry");
    combat.RemovePowersAfterDeath(player.Creature);
    Require(combat.Mutations == 2, "Repeated cleanup duplicated removals");
}
// A removal veto from the existing Illusion rule must still win over default death removal.
{
    Player player = new();
    SimulatedCombatState combat = new();
    combat.Creatures.Add(player.Creature);
    PowerModel buff = new(player.Creature, true, 2);
    PowerModel debuff = new(player.Creature, true, 1) { Type = PowerType.Debuff };
    combat.Powers.AddRange([new IllusionPower(player.Creature), buff, debuff]);
    Require(new Simulator(combat, player, false, false).Run(player), "Illusion cleanup failed");
    Require(buff.Amount == 2 && debuff.Amount == 0, "Existing removal veto changed");
}
Console.WriteLine($"Passed {checks} player death cleanup checks.");
void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

namespace CombatSolver
{
    internal class Creature { public bool IsDead; public bool IsAlive => !IsDead; }
    internal sealed class Player { public Creature Creature = new() { IsDead = true }; }
    internal enum PowerType { Buff, Debuff }
    internal interface ITemporaryPower;
    internal class PowerModel(Creature owner, bool removeOnDeath, int amount)
    {
        public Creature Owner = owner;
        public int Amount = amount;
        public PowerType Type;
        public bool ShouldPowerBeRemovedAfterOwnerDeath() => removeOnDeath;
    }
    internal sealed class IllusionPower(Creature owner) : PowerModel(owner, false, 1);
    internal interface ICombatPredictionEffectSink { void CompletePlayerDeath(Player player); }
    internal sealed partial class SimulatedCombatState : ICombatPredictionEffectSink
    {
        public List<PowerModel> Powers = [];
        public List<Creature> Creatures = [];
        public HashSet<Creature> CompletedDeaths = [];
        private void CompleteDeathPhase(Creature creature) => CompletedDeaths.Add(creature);
        public int Mutations;
        private IEnumerable<PowerModel> EffectivePowers() => Powers;
        private bool ContainsCreature(Creature owner) => Creatures.Contains(owner);
        private void SetPowerAmount(PowerModel power, int amount) { power.Amount = amount; Mutations++; }
    }
    internal sealed class PlayerState
    {
        public List<object> OrbQueue = [new()];
        public List<object> AllCards = [];
        public int Energy => 0;
        public int Stars => 0;
        public void LoseEnergy(int amount) => throw new Exception("Multiplayer path reached");
        public void LoseStars(int amount) => throw new Exception("Multiplayer path reached");
    }
    internal sealed class PredictionState(SimulatedCombatState combat, Player player, PlayerState playerState, bool hasOsty)
    {
        public object CombatState = combat;
        public Player[] Players = [player];
        private readonly Creature? osty = hasOsty ? new() : null;
        public Creature GetCreature(Creature creature) => creature;
        public Creature? GetOsty(Player _) => osty;
        public PlayerState GetPlayerCombatState(Player _) => playerState;
    }
    internal sealed partial class Simulator
    {
        public readonly PlayerState PlayerState = new();
        private readonly PredictionState State;
        private readonly bool pending;
        private bool HasPendingChoice;
        public int Kills;
        public bool Forced;
        public Action? OnKill;
        public Simulator(SimulatedCombatState combat, Player player, bool hasOsty, bool pending)
        { State = new(combat, player, PlayerState, hasOsty); this.pending = pending; }
        public bool Run(Player player) => HandlePlayerDeath(player);
        private bool Kill(Creature creature, bool force)
        {
            Kills++;
            Forced = force;
            OnKill?.Invoke();
            HasPendingChoice = pending;
            return !pending;
        }
        private void RemoveFromCombat(object[] cards) => throw new Exception("Multiplayer path reached");
    }
}
