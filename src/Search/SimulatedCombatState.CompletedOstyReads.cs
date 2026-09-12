using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    // Lane-owned compatibility values for the captured pet. No summon, damage, death
    // or Power command runs here. The max-HP map's original absence is part of the key.
    internal sealed class CompletedOstyReadBinding
    {
        private readonly SimulatedCombatState _combat;
        private readonly Creature _osty;
        private readonly SimCreatureState _values;
        private readonly ForkableDictionary<Creature, int> _maxHp = [];
        private readonly bool _hadMap, _hadValue;
        private readonly int _rootMaxHp;

        internal CompletedOstyReadBinding(CombatPredictionSimulator context, Player player)
        {
            _combat = (SimulatedCombatState)context.State.CombatState;
            _combat.AssertForkable();
            _osty = _combat.GetOsty(player) ?? throw new InvalidOperationException("Pet reading requires a captured identity.");
            _values = context.State.GetCreature(_osty);
            if (_combat._simulatedOstyMaxHp?.Keys.Any(creature => creature != _osty) == true)
                throw new NotSupportedException("Pet reading has unrepresented max-HP identities.");
            _hadMap = _combat._simulatedOstyMaxHp != null;
            _hadValue = _combat._simulatedOstyMaxHp?.TryGetValue(_osty, out _rootMaxHp) == true;
        }

        internal void Read(int hp, int maxHp, int block, bool summoned)
        {
            _values.SetMaxHp(maxHp);
            _values.CurrentHp = hp;
            _values.DamageBlock(_values.Block, ValueProp.Unpowered);
            _values.GainBlock(block);
            _maxHp.Clear();
            if (summoned || _hadValue) _maxHp[_osty] = summoned ? maxHp : _rootMaxHp;
            _combat._simulatedOstyMaxHp = summoned || _hadMap ? _maxHp : null;
        }
    }
}
