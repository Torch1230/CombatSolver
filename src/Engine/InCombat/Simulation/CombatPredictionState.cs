using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed class CombatPredictionState
{
    public ICombatState CombatState { get; }

    private readonly Dictionary<Creature, SimCreatureState> _creatures;

    // 绝大多数分叉里没有任何生物被移出预测名册。惰性建表让 Fork 少一次空 HashSet 分配。
    private HashSet<Creature>? _removedCreatures;

    private readonly Dictionary<Player, SimPlayerCombatState> _playerCombatStates;
    private HittableEnemyView? _hittableEnemies;

    private sealed class HittableEnemyView(CombatPredictionState owner) : IReadOnlyList<Creature>
    {
        public int Count
        {
            get
            {
                int count = 0;
                foreach (Creature enemy in owner.CombatState.Enemies)
                {
                    if (owner.IsHittable(enemy))
                        count++;
                }
                return count;
            }
        }

        public Creature this[int index]
        {
            get
            {
                foreach (Creature enemy in owner.CombatState.Enemies)
                {
                    if (!owner.IsHittable(enemy))
                        continue;
                    if (index-- == 0)
                        return enemy;
                }
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public IEnumerator<Creature> GetEnumerator()
        {
            foreach (Creature enemy in owner.CombatState.Enemies)
            {
                if (owner.IsHittable(enemy))
                    yield return enemy;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public CombatPredictionState(ICombatState combatState)
    {
        CombatState = combatState;
        _creatures = [];
        _playerCombatStates = [];
        if (combatState is ICombatPredictionStateOwner owner)
            owner.AttachPredictionState(this);
    }

    private CombatPredictionState(
        ICombatState combatState,
        Dictionary<Creature, SimCreatureState> creatures,
        HashSet<Creature>? removedCreatures,
        Dictionary<Player, SimPlayerCombatState> playerCombatStates)
    {
        CombatState = combatState;
        _creatures = creatures;
        _removedCreatures = removedCreatures;
        _playerCombatStates = playerCombatStates;
        if (combatState is ICombatPredictionStateOwner owner)
            owner.AttachPredictionState(this);
    }

    public IReadOnlyList<Creature> Allies => !RequiresRemovedCreatureFiltering
        ? CombatState.Allies
        : [.. ExcludeRemoved(CombatState.Allies)];

    public IReadOnlyList<Creature> Enemies => !RequiresRemovedCreatureFiltering
        ? CombatState.Enemies
        : [.. ExcludeRemoved(CombatState.Enemies)];

    public IReadOnlyList<Creature> Creatures => !RequiresRemovedCreatureFiltering
        ? CombatState.Creatures
        : [.. ExcludeRemoved(CombatState.Creatures)];

    // SimulatedCombatState updates its allies/enemies rosters, but PlayerCreatures is an
    // immutable root projection and can still contain a removed player-side summon.
    public IReadOnlyList<Creature> PlayerCreatures => _removedCreatures is not { Count: > 0 }
        ? CombatState.PlayerCreatures
        : [.. ExcludeRemoved(CombatState.PlayerCreatures)];

    public IReadOnlyList<Player> Players => CombatState.Players;

    public IReadOnlyList<Creature> HittableEnemies => _hittableEnemies ??= new HittableEnemyView(this);

    public SimCreatureState GetCreature(Creature creature)
    {
        if (!_creatures.TryGetValue(creature, out var state))
        {
            if (CombatState is ICombatPredictionRootCaptureBoundary boundary)
                boundary.AssertCanCaptureCreature(creature);
            state = new SimCreatureState(creature);
            _creatures.Add(creature, state);
        }

        return state;
    }

    public Creature? GetOsty(Player player)
        => CombatState is ICombatPredictionPetState pets
            ? pets.GetOsty(player)
            : player.Osty;

    public bool IsHittable(Creature creature)
    {
        if (_removedCreatures?.Contains(creature) == true || !GetCreature(creature).IsAlive)
            return false;
        return CombatState is ICombatPredictionCreatureSemantics semantics
            ? semantics.IsHittable(creature)
            : Hook.ShouldAllowHitting(CombatState, creature);
    }

    public IReadOnlyList<Creature> GetOpponentsOf(Creature creature)
        => !RequiresRemovedCreatureFiltering
            ? CombatState.GetOpponentsOf(creature)
            : [.. ExcludeRemoved(CombatState.GetOpponentsOf(creature))];

    public IReadOnlyList<Creature> GetTeammatesOf(Creature creature)
        => !RequiresRemovedCreatureFiltering
            ? CombatState.GetTeammatesOf(creature)
            : [.. ExcludeRemoved(CombatState.GetTeammatesOf(creature))];

    public IReadOnlyList<Creature> GetCreaturesOnSide(CombatSide side)
        => !RequiresRemovedCreatureFiltering
            ? CombatState.GetCreaturesOnSide(side)
            : [.. ExcludeRemoved(CombatState.GetCreaturesOnSide(side))];

    // A prediction roster sink has already removed the creature from its mutable side rosters.
    // Filtering those views again used to rebuild LINQ iterators and arrays on every read after
    // the first death, which dominates long multi-enemy searches. Keep the fallback for combat
    // state implementations that cannot update their own roster.
    private bool RequiresRemovedCreatureFiltering
        => _removedCreatures is { Count: > 0 } && CombatState is not ICombatPredictionRosterSink;

    public IReadOnlyList<AbstractModel> IterateHookListeners()
    {
        if (CombatState is ICombatPredictionHookListenerSource source)
            return source.HookListeners;
        return CombatState.IterateHookListeners().ToArray();
    }

    public SimPlayerCombatState GetPlayerCombatState(Player player)
    {
        if (!_playerCombatStates.TryGetValue(player, out var state))
        {
            if (CombatState is ICombatPredictionRootCaptureBoundary boundary)
                boundary.AssertCanCapturePlayer(player);
            var liveState = player.PlayerCombatState
                ?? throw new InvalidOperationException($"Player {player.Creature.Name} has no combat state to simulate.");
            state = new SimPlayerCombatState(liveState);
            _playerCombatStates.Add(player, state);
        }

        return state;
    }

    public void RemoveCreature(Creature creature)
    {
        if (Creatures.Contains(creature))
        {
            (_removedCreatures ??= []).Add(creature);
            if (CombatState is ICombatPredictionRosterSink roster)
                roster.RemoveCreatureFromPrediction(creature);
        }
    }

    public PredictedCard? FindCard(CardModel card)
    {
        return GetPlayerCombatState(card.Owner).FindCard(card);
    }

    internal void MaterializeRoot()
    {
        foreach (Creature creature in CombatState.Creatures)
            _ = GetCreature(creature);
        foreach (Player player in CombatState.Players)
        {
            if (player.Osty is { } osty)
                _ = GetCreature(osty);
            GetPlayerCombatState(player).MaterializeRoot();
        }
    }

    private IEnumerable<Creature> ExcludeRemoved(IEnumerable<Creature> creatures)
    {
        return creatures.Where(creature => _removedCreatures?.Contains(creature) != true);
    }

    internal CombatPredictionState Fork(PredictionForkContext context)
    {
        Dictionary<Creature, SimCreatureState> creatures = new(_creatures.Count);
        foreach ((Creature creature, SimCreatureState state) in _creatures)
            creatures.Add(creature, state.Fork(context));

        Dictionary<Player, SimPlayerCombatState> players = new(_playerCombatStates.Count);
        foreach ((Player player, SimPlayerCombatState state) in _playerCombatStates)
            players.Add(player, state.Fork(context));

        if (CombatState is not ICombatPredictionForkableState forkableCombatState)
        {
            throw new InvalidOperationException(
                $"Combat state {CombatState.GetType().FullName} does not implement " +
                $"{nameof(ICombatPredictionForkableState)}.");
        }
        ICombatState combatState = forkableCombatState.Fork(context);
        CombatPredictionState fork = new(
            combatState,
            creatures,
            _removedCreatures is null ? null : new HashSet<Creature>(_removedCreatures),
            players);
        context.Register(this, fork);
        return fork;
    }
}
