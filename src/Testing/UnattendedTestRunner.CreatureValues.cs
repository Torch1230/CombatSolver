using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCreatureValuesAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "STRIKE_SILENT", "DEFEND_SILENT" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        AssertCreatureReadValues(combat, player);
        Creature enemy = combat.Enemies[0];
        var samples = new List<object>();
        for (int index = 0; index < 12; index++)
        {
            Creature target = index < 6 ? enemy : player.Creature;
            int block = new[] { 0, 5, 5, 5, 9, 2 }[index % 6];
            decimal amount = new[] { 0m, 3.75m, 5m, 8.25m, 6.5m, 11.5m }[index % 6];
            ValueProp props = index % 6 == 4 ? ValueProp.Unblockable | ValueProp.Unpowered : ValueProp.Unpowered;
            await CreatureCmd.SetMaxHp(target, 80);
            await CreatureCmd.SetCurrentHp(target, 60);
            await SetBlockAsync(target, block);
            CombatPredictionSimulator predicted = CombatRootSnapshot.Capture(combat).ForkSimulator();
            var shadow = (SimulatedCombatState)predicted.State.CombatState;
            using (SimulationNotificationIsolation.Enter()) predicted.Damage(target, amount, props, player.Creature);
            MoveStateSnapshot expected = CaptureSimulated(predicted, shadow, player, enemy);
            await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), target, amount, props, player.Creature);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), "CreatureValues", $"NativeDamage{index}");
            // Decimal truncation is independently checked against native values before read-view tests.
            CreatureVitals values = new(60, 80, block);
            decimal blocked = values.DamageBlock(amount, props.HasFlag(ValueProp.Unblockable));
            HpLossValues loss = values.LoseHp(Math.Max(0m, amount - blocked));
            if (values.CurrentHp != target.CurrentHp || values.Block != target.Block || values.MaxHp != target.MaxHp)
                throw new InvalidOperationException("Value arithmetic differs from native damage.");
            samples.Add(new { index, target = target == enemy ? "enemy" : "player", amount, initialBlock = block,
                blocked, loss, values.CurrentHp, values.MaxHp, finalBlock = values.Block });
        }
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "creature-values.json"),
                JsonSerializer.Serialize(new { scope = "shared scalar arithmetic and completed reads; no compact attack/death hooks", samples }, new JsonSerializerOptions { WriteIndented = true }));
        }
        _completedChecks.Add("CreatureValues:12NativeDamageCases:FullState:FractionalAndUnblockable:PlayerAndEnemy");
    }

    private void AssertCreatureReadValues(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        CombatPredictionSimulator metadata = root.ForkSimulator();
        var shadow = (SimulatedCombatState)metadata.State.CombatState;
        Creature[] identities = [player.Creature, .. shadow.KnownEnemies];
        if (shadow.KnownEnemies.Count != 3) throw new InvalidOperationException("Creature read fixture requires three enemies.");
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        var evaluator = new CompactEvaluationDriver(root, display, damage, policy);
        var view = new CreatureValuesReadView(metadata, player, identities) { Invariants = new() };
        var initial = view.State.Freeze();
        var expectedRoot = Release(evaluator.Evaluate(metadata));
        List<SimulationSnapshot> expected = [], actual = [];
        for (int sample = 0; sample < 16; sample++)
        {
            view.State.Restore(initial);
            var oracle = metadata.Fork();
            for (int index = 0; index < identities.Length; index++)
            {
                Creature creature = identities[index];
                CreatureVitals values = view.Slots[index].Read(view.State);
                var mutable = oracle.State.GetCreature(creature);
                int maximum = sample == 0 ? values.MaxHp : Math.Max(1, values.MaxHp - sample);
                values.SetMaxHp(maximum); mutable.SetMaxHp(maximum);
                decimal hpLoss = sample == 0 ? 0 : sample is 14 or 15 || index > 0 && sample % 4 == 0 ? values.CurrentHp : sample * 2;
                values.LoseHp(hpLoss); mutable.LoseHp(hpLoss, ValueProp.Unblockable);
                values.GainBlock(sample * (index + 1)); mutable.GainBlock(sample * (index + 1));
                view.Slots[index].Write(view.State, values);
                if (index > 0 && sample % 4 == 0 && sample != 0 && (sample == 8 || index % 2 != 0))
                {
                    view.Slots[index].SetPresent(view.State, false);
                    oracle.State.RemoveCreature(creature);
                }
            }
            view.Terminal = null;
            if (sample == 14)
            {
                oracle.LoseCombat(); oracle.CheckWinCondition(root.StartTurnNumber);
                view.Terminal = new(root.StartTurnNumber, CombatTerminalOutcome.Defeat);
            }
            if (sample == 15)
            {
                // Restore player HP before committing victory; zero HP in all enemies is sufficient
                // here because the fixture has no revival Powers or additional enemy lifecycle.
                var values = view.Slots[0].Read(view.State) with { CurrentHp = 1 };
                view.Slots[0].Write(view.State, values);
                oracle.State.GetCreature(player.Creature).CurrentHp = 1;
                if (!oracle.CheckWinCondition(root.StartTurnNumber))
                    throw new InvalidOperationException("Read-view fixture failed to commit victory.");
                view.Terminal = new(root.StartTurnNumber, CombatTerminalOutcome.Victory);
            }
            SimulationSnapshot legacy = Release(new CompactEvaluationDriver(root, display, damage, policy).Evaluate(oracle));
            SimulationSnapshot direct = evaluator.Evaluate(view);
            try { AssertCompactEvaluation(legacy, direct); }
            catch (InvalidOperationException error)
            {
                StateFingerprintBuilder oldKey = new(), rootKey = new();
                ((SimulatedCombatState)oracle.State.CombatState).AppendFingerprint(ref oldKey, oracle);
                shadow.AppendFingerprint(ref rootKey, metadata, view.CardHistory, view.EnemyRoster);
                throw new InvalidOperationException($"Creature read sample={sample}, old_combat={oldKey.Finish()}, root_combat={rootKey.Finish()}, values="
                    + string.Join(";", identities.Select(c => $"{c.CombatId}:{CreatureReadValues.Capture(oracle, c)}/{view.ReadCreature(c)}")), error);
            }
            expected.Add(legacy); actual.Add(direct);
            var retained = view.State.Freeze();
            var mark = view.State.Mark();
            foreach (var slot in view.Slots) slot.Write(view.State, new(999, 999, 999));
            view.State.Rollback(mark);
            if (!retained.ContentEquals(view.State.Freeze()))
                throw new InvalidOperationException("Creature journal failed to restore a completed state.");
        }
        expected.Sort(CompareCompactEvaluation); actual.Sort(CompareCompactEvaluation);
        for (int index = 0; index < expected.Count; index++) AssertCompactEvaluation(expected[index], actual[index]);
        if (view.Invariants!.EnemyBuilds != 0 || view.Invariants.FocusBuilds != 0)
            throw new InvalidOperationException("Mutable enemy values entered the root invariant cache.");
        view.State.Restore(initial); view.Terminal = null;
        try { AssertCompactEvaluation(expectedRoot, evaluator.Evaluate(view)); }
        catch (InvalidOperationException error) { throw new InvalidOperationException("Creature root restored read failed.", error); }
        AssertCompactEvaluation(expectedRoot, Release(evaluator.Evaluate(metadata)));
        AssertSandpitCreatureReads(metadata, player, identities);
        _completedChecks.Add("CreatureReadView:16States:AllSnapshotProperties:OriginalKeyAndSort:HpMaxBlockRosterTerminal:Rollback:RootUnchanged:MutableEnemyCacheBypass");
        _completedChecks.Add("CreatureReadView:SandpitScalarHelper:OwnerTargetAmountFilters:ChangedHpWithoutRosterOrPowerChanges");
    }

    private static void AssertSandpitCreatureReads(CombatPredictionSimulator metadata, Player player, Creature[] identities)
    {
        var root = metadata.Fork();
        var combat = (SimulatedCombatState)root.State.CombatState;
        combat.ApplyTargeted<SandpitPower>(identities[1], player.Creature, 3, identities[1]);
        combat.ApplyTargeted<SandpitPower>(identities[2], player.Creature, -2, identities[2]);
        combat.ApplyTargeted<SandpitPower>(identities[3], identities[3], 11, identities[3]);
        var powers = combat.EffectivePowers();
        var view = new CreatureValuesReadView(root, player, identities);
        if (CombatBeamSolver.ReadSandpitRemaining(root, powers, player.Creature, null) != 3
            || CombatBeamSolver.ReadSandpitRemaining(root, powers, player.Creature, view) != 3)
            throw new InvalidOperationException("Sandpit scalar helper did not filter target/negative amount.");
        var mark = view.State.Mark();
        view.Slots[1].Write(view.State, view.Slots[1].Read(view.State) with { CurrentHp = 0 });
        if (CombatBeamSolver.ReadSandpitRemaining(root, powers, player.Creature, view) != 0
            || CombatBeamSolver.ReadSandpitRemaining(root, powers, player.Creature, null) != 3)
            throw new InvalidOperationException("Sandpit scalar helper read stale owner HP or changed its root.");
        view.State.Rollback(mark);
        if (CombatBeamSolver.ReadSandpitRemaining(root, powers, player.Creature, view) != 3)
            throw new InvalidOperationException("Sandpit scalar helper missed restored owner HP.");
    }

    // A reading-contract fixture, not an effect adapter: only scalar mutations made by this
    // test are represented. No claim that damage histories, Power/death hooks or AI were migrated.
    private sealed class CreatureValuesReadView : CompletedStateReadView
    {
        private readonly CombatPredictionSimulator _root;
        private readonly Player _player;
        private readonly Dictionary<Creature, int> _indices;
        private readonly SimPlayerCombatState _cards;
        private readonly IReadOnlyList<PredictionGap> _gaps;
        internal readonly ReversibleValueState State = new(0);
        internal readonly CreatureValueSlots[] Slots;
        internal CombatTerminalStamp? Terminal;
        internal CreatureValuesReadView(CombatPredictionSimulator root, Player player, Creature[] identities)
        {
            _root = root; _player = player;
            _cards = root.State.GetPlayerCombatState(player);
            _indices = identities.Select((creature, index) => (creature, index)).ToDictionary(x => x.creature, x => x.index);
            Slots = identities.Select(creature =>
            {
                CreatureReadValues values = CreatureReadValues.Capture(root, creature);
                return CreatureValueSlots.Allocate(State, new(values.CurrentHp, values.MaxHp, values.Block), values.Present);
            }).ToArray();
            _gaps = PredictionCoverage.Collect(root);
        }
        internal override CombatPredictionSimulator EvaluationContext => _root;
        internal override int Energy => _cards.Energy;
        internal override int Block => ReadCreature(_player.Creature).Block;
        internal override CreatureReadValues ReadCreature(Creature creature)
        {
            CreatureValueSlots slots = Slots[_indices[creature]];
            CreatureVitals values = slots.Read(State);
            return new(values.CurrentHp, values.MaxHp, values.Block, slots.IsPresent(State));
        }
        internal override IReadOnlyList<Creature> EnemyRoster => _indices.Keys
            .Where(creature => creature != _player.Creature && ReadCreature(creature).Present).ToArray();
        internal override bool EnemyValuesInvariant => false;
        internal override CombatTerminalStamp? TerminalStamp => Terminal;
        internal override int HistoryEntries => _root.History.Entries.Count;
        internal override IReadOnlyList<PredictedCard> Hand => _cards.Hand.Cards;
        internal override IReadOnlyList<PredictedCard> Draw => _cards.DrawPile.Cards;
        internal override IReadOnlyList<PredictedCard> Discard => _cards.DiscardPile.Cards;
        internal override IReadOnlyList<PredictedCard> Exhaust => _cards.ExhaustPile.Cards;
        internal override IReadOnlyList<PredictionGap> PredictionGaps => _gaps;
        internal override CardHistoryReadValues CardHistory => new(_player, null, null, null, null, null, null, null, null, null);
        internal override PredictionRngState ShuffleRng => _root.Rng.Shuffle.CaptureState();
    }
}
