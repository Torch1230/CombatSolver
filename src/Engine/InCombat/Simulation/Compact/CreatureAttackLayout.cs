namespace CombatSolver.Engine.InCombat.Simulation.Compact;

// Immutable locations only. Vitals, presence, death completion and terminal state all belong
// to the caller's journal. The optional captured pet follows the primary enemy range;
// its identity survives death and revival, but it never participates in enemy victory checks.
internal sealed class CreatureAttackLayout
{
    private readonly CreatureValueSlots[] _creatures;
    private readonly int _deathStart, _terminalSlot, _petSummonedSlot;
    internal int Count => _creatures.Length;
    internal int Pet { get; }
    internal int EnemyEnd => Pet < 0 ? Count : Pet;

    internal CreatureAttackLayout(ReversibleValueState state, IReadOnlyList<CreatureVitals> creatures, int pet = -1)
    {
        if (creatures.Count is < 2 or > 254 || pet != -1 && (pet != creatures.Count - 1 || pet < 2)
            || creatures.Where((c, index) => index != pet).Any(c => c.CurrentHp <= 0)
            || creatures.Any(c => c.CurrentHp < 0 || c.MaxHp < 1 || c.MaxHp < c.CurrentHp || c.Block < 0))
            throw new ArgumentException("Attack roots require a living player, living primary enemies and an optional captured pet.");
        Pet = pet;
        _creatures = creatures.Select(c => CreatureValueSlots.Allocate(state, c, true)).ToArray();
        _deathStart = state.Allocate(creatures.Count);
        _terminalSlot = state.Allocate(1);
        _petSummonedSlot = pet < 0 ? -1 : state.Allocate(1);
        if (pet >= 0 && creatures[pet].CurrentHp == 0) state.Write(_deathStart + pet, 1);
    }

    internal CreatureVitals Read(ReversibleValueState state, int creature) => _creatures[creature].Read(state);
    internal void Write(ReversibleValueState state, int creature, CreatureVitals values) => _creatures[creature].Write(state, values);
    internal bool Present(ReversibleValueState state, int creature) => _creatures[creature].IsPresent(state);
    internal bool DeathCompleted(ReversibleValueState state, int creature) => state[_deathStart + creature] != 0;
    internal bool Terminal(ReversibleValueState state) => state[_terminalSlot] != 0;
    internal bool DefeatTerminal(ReversibleValueState state) => state[_terminalSlot] == 2;
    internal bool PetSummoned(ReversibleValueState state) => Pet >= 0 && state[_petSummonedSlot] != 0;

    internal DamageValues Damage(ReversibleValueState state, int target, decimal amount, bool unblockable = false)
        => Damage(state, target, amount, out _, unblockable);

    internal DamageValues Damage(ReversibleValueState state, int target, decimal amount,
        out DamageValues? redirected, bool unblockable = false, bool redirectToPet = false)
    {
        int blockTarget = target == Pet ? 0 : target;
        CreatureVitals blockValues = Read(state, blockTarget);
        decimal blocked = blockValues.DamageBlock(amount, unblockable);
        Write(state, blockTarget, blockValues);
        CreatureVitals values = Read(state, target);
        decimal unblocked = Math.Max(amount - blocked, 0m);
        bool blockBroken = values.Block <= 0 && blocked > 0m;
        bool fullyBlocked = !unblockable && (blocked > 0m || values.Block > 0) && (int)unblocked == 0;
        redirected = null;
        if (redirectToPet)
        {
            if (target != 0 || Pet < 0 || Read(state, Pet).CurrentHp <= 0)
                throw new InvalidOperationException("Damage redirection requires the living captured pet and its owner.");
            CreatureVitals pet = Read(state, Pet);
            HpLossValues petLoss = pet.LoseHp(unblocked);
            Write(state, Pet, pet);
            redirected = new(0, petLoss.UnblockedDamage, petLoss.OverkillDamage, petLoss.WasTargetKilled, false, false);
            unblocked = petLoss.OverkillDamage;
        }
        HpLossValues loss = values.LoseHp(unblocked);
        Write(state, target, values);
        return new((int)blocked, loss.UnblockedDamage, loss.OverkillDamage, loss.WasTargetKilled,
            blockBroken, fullyBlocked);
    }

    internal void SummonPet(ReversibleValueState state, int amount)
    {
        if (Pet < 0 || amount <= 0) throw new InvalidOperationException("Summoning requires a captured pet and a positive amount.");
        CreatureVitals pet = Read(state, Pet);
        int healing = amount;
        if (pet.CurrentHp > 0)
        {
            int beforeMaxHp = pet.MaxHp;
            pet.SetMaxHp((int)Math.Min(999_999_999L, (long)beforeMaxHp + amount));
            healing = pet.MaxHp - beforeMaxHp;
        }
        else pet.SetMaxHp(amount);
        pet.Heal(healing);
        Write(state, Pet, pet);
        state.Write(_deathStart + Pet, 0);
        state.Write(_petSummonedSlot, 1);
    }

    internal void CompleteDeath(ReversibleValueState state, int target)
    {
        if (Read(state, target).CurrentHp != 0 || DeathCompleted(state, target))
            throw new InvalidOperationException("Death completion requires an unprocessed dead creature.");
        // A dead player and its pet remain in the combat roster until native teardown.
        if (target != 0 && target != Pet) _creatures[target].SetPresent(state, false);
        state.Write(_deathStart + target, 1);
    }

    internal bool IsEnding(ReversibleValueState state)
    {
        if (DeathCompleted(state, 0)) return true;
        for (int index = 1; index < EnemyEnd; index++)
            if (Present(state, index)) return false;
        return true;
    }

    internal bool CheckWinCondition(ReversibleValueState state)
    {
        if (Terminal(state)) return true;
        if (!IsEnding(state)) return false;
        state.Write(_terminalSlot, DeathCompleted(state, 0) ? 2 : 1);
        return true;
    }
}

internal readonly record struct DamageValues(int Blocked, int Unblocked, int Overkill,
    bool Killed, bool BlockBroken, bool FullyBlocked)
{
    internal int Flags => (Killed ? 1 : 0) | (BlockBroken ? 2 : 0) | (FullyBlocked ? 4 : 0);
}
