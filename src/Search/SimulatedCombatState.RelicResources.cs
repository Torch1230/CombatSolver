using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private ForkableDictionary<Player, int>? _simulatedPlayerGold;

    public int GetPlayerGold(Player player)
    {
        if (_simulatedPlayerGold?.TryGetValue(player, out int gold) == true)
            return gold;
        throw new InvalidOperationException($"Player {player.NetId} is outside the captured gold state.");
    }

    public void LosePlayerGold(Player player, int amount)
    {
        if (amount <= 0)
            return;
        (_simulatedPlayerGold ??= [])[player] = Math.Max(0, GetPlayerGold(player) - amount);
    }

    public void GainPlayerGold(Player player, int amount)
    {
        if (amount <= 0)
            return;
        (_simulatedPlayerGold ??= [])[player] = checked(GetPlayerGold(player) + amount);
    }

    public bool TriggerRelicsAfterStarsSpent(
        CombatPredictionSimulator simulator,
        Player player,
        int amount)
    {
        if (amount <= 0)
            return true;
        foreach (MiniRegent relic in RelicsOf(player)
                     .OfType<MiniRegent>()
                     .Where(static relic => !relic.IsMelted))
        {
            StatefulRelicState state = GetStatefulRelicState(relic);
            if (state.Current != 0)
                continue;
            SetStatefulRelicState(relic, state with { Current = 1 });
            Apply<StrengthPower>(player.Creature, relic.DynamicVars.Strength.IntValue, player.Creature);
        }
        foreach (GalacticDust relic in RelicsOf(player)
                     .OfType<GalacticDust>()
                     .Where(static relic => !relic.IsMelted))
        {
            StatefulRelicState state = GetStatefulRelicState(relic);
            int total = state.Current + amount;
            int threshold = relic.DynamicVars.Stars.IntValue;
            int triggers = total / threshold;
            SetStatefulRelicState(relic, state with { Current = total % threshold });
            if (triggers > 0)
            {
                simulator.GainBlock(
                    player.Creature,
                    triggers * relic.DynamicVars.Block.IntValue,
                    MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered);
                if (simulator.HasPendingChoice)
                    return false;
            }
        }
        return true;
    }

    private void AppendRelicResourceFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        fingerprint.Add('G');
        foreach (Player player in Players)
        {
            fingerprint.Add(player.NetId);
            fingerprint.Add(GetPlayerGold(player));
        }
    }
}
