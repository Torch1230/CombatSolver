using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertHpLossClamping(CombatState live, Player player)
    {
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        Creature liveCreature = player.Creature;
        decimal[] amounts = [-3.25m, -1m, -.25m, 0m, .25m, .999m, 1m, 1.999m,
            75m, 75.999m, 999_999_999m, 1_000_000_000m, decimal.MinValue, decimal.MaxValue];
        ValueProp[] props = [ValueProp.Move, ValueProp.Unblockable | ValueProp.Unpowered];
        int cases = 0;
        foreach (int hp in new[] { 75, 1, 0 })
        foreach (int block in new[] { 0, 7 })
        foreach (ValueProp valueProp in props)
        foreach (decimal amount in amounts)
        {
            // The player constructor does not replace player.Creature. These detached native
            // receivers exercise the installed game's primitive without advancing live combat.
            Creature native = new(player, hp, 100);
            native.GainBlockInternal(block);
            SimCreatureState predicted = new(native);
            List<(int Before, int After)> changes = [];
            native.CurrentHpChanged += (before, after) => changes.Add((before, after));
            DamageResult actual = native.LoseHpInternal(amount, valueProp);
            int nativeNotifications = changes.Count;
            DamageResult simulated = predicted.LoseHp(amount, valueProp);
            AssertHpLossResult(native, predicted, actual, simulated,
                $"hp={hp}, block={block}, amount={amount}, props={valueProp}");
            if (changes.Count != nativeNotifications
                || nativeNotifications != (native.CurrentHp == hp ? 0 : 1)
                || (nativeNotifications == 1 && changes[0] != (hp, native.CurrentHp)))
                throw new InvalidOperationException("HP loss changed native notification settlement.");
            cases++;
        }

        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        CombatPredictionSimulator parent = root.ForkSimulator();
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        CombatPredictionSimulator[] children = Enumerable.Range(0, 16).Select(_ => parent.Fork()).ToArray();
        Parallel.For(0, children.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 }, index =>
        {
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            CombatPredictionSimulator child = children[index];
            SimCreatureState predicted = child.State.GetCreature(liveCreature);
            Creature native = new(player, predicted.CurrentHp, predicted.MaxHp);
            native.GainBlockInternal(predicted.Block);
            native.HpDisplay = predicted.HpDisplay;
            decimal amount = index % 2 == 0 ? -3.25m : 1.75m;
            DamageResult actual = native.LoseHpInternal(amount, ValueProp.Move);
            DamageResult simulated = predicted.LoseHp(amount, ValueProp.Move);
            AssertHpLossResult(native, predicted, actual, simulated, $"fork={index}");
            string after = DescribeContinuationContractState(child, root, player);
            if (index % 2 == 0 && after != parentBefore)
                throw new InvalidOperationException("Negative HP loss changed a full branch state.");
            if (DescribeContinuationContractState(child.Fork(), root, player) != after)
                throw new InvalidOperationException("HP loss was not preserved by a subsequent Fork.");
        });
        if (DescribeContinuationContractState(parent, root, player) != parentBefore
            || ContinuationStamp.CaptureLive(live).StateText != liveBefore
            || !ReferenceEquals(player.Creature, liveCreature))
            throw new InvalidOperationException("HP loss changed the parent, live state or player identity.");
        _completedChecks.Add($"HpLossClamping:NativeCases={cases}:NegativeFractionZeroCeilingExtremes:AllDamageFields:Notifications:FullStateRNGForkDOP16ParentLiveIsolation");
    }

    private static void AssertHpLossResult(Creature native, SimCreatureState predicted,
        DamageResult actual, DamageResult simulated, string context)
    {
        if (native.CurrentHp != predicted.CurrentHp || native.MaxHp != predicted.MaxHp
            || native.Block != predicted.Block || native.HpDisplay != predicted.HpDisplay
            || native.IsAlive != predicted.IsAlive || native.IsDead != predicted.IsDead
            || !ReferenceEquals(actual.Receiver, native)
            || !ReferenceEquals(simulated.Receiver, predicted.Creature)
            || actual.Props != simulated.Props
            || actual.BlockedDamage != simulated.BlockedDamage
            || actual.UnblockedDamage != simulated.UnblockedDamage
            || actual.OverkillDamage != simulated.OverkillDamage
            || actual.TotalDamage != simulated.TotalDamage
            || actual.WasBlockBroken != simulated.WasBlockBroken
            || actual.WasFullyBlocked != simulated.WasFullyBlocked
            || actual.WasTargetKilled != simulated.WasTargetKilled)
            throw new InvalidOperationException($"Native/simulated HP loss differs ({context}): "
                + $"HP {native.CurrentHp}/{predicted.CurrentHp}, "
                + $"unblocked {actual.UnblockedDamage}/{simulated.UnblockedDamage}, "
                + $"overkill {actual.OverkillDamage}/{simulated.OverkillDamage}, "
                + $"killed {actual.WasTargetKilled}/{simulated.WasTargetKilled}.");
    }
}
