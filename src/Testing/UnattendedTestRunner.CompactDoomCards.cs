using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Deathbringer, NegativePulse, Scourge, Putrefy and Fear are the native Necrobinder
    // cards whose complete OnPlay is one ordered Doom/Weak/Vulnerable command per effect.
    // This fixture drives the production compiler, the compact lane built from it, its
    // materialization and the real game through the same ordered actions. The card-applied
    // Doom additionally reaches the native enemy-side kill boundary in its own scenario.
    private static readonly string[] CompactDoomCardIds = ["DEATHBRINGER", "NEGATIVE_PULSE", "SCOURGE", "PUTREFY", "FEAR"];

    private async Task PrepareCompactDoomCardsAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, mode, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        foreach (string id in CompactDoomCardIds)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand", UpgradeLevels = mode });
        // Scourge's draw fodder and a later attack that must consume the applied Vulnerable.
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_NECROBINDER", Pile = "Hand" });
        for (int index = 0; index < 4; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = index < 2 ? "Draw" : "Discard" });
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private async Task AssertCompactDoomCardsAsync(CombatState combat, Player player)
    {
        object compilation = AssertCompactDoomCardCompilation(combat, player, out int admitted, out int unsupported, out string firstUnsupported);
        List<object> evidence = [];
        for (int mode = 0; mode < 2; mode++)
        {
            await PrepareCompactDoomCardsAsync(combat, player, mode);
            int doom = mode == 0 ? 21 : 26, pulseDoom = mode == 0 ? 7 : 11, pulseBlock = mode == 0 ? 5 : 6;
            int scourgeDoom = mode == 0 ? 13 : 16, putrefy = mode == 0 ? 2 : 3, vulnerable = mode == 0 ? 1 : 2;
            int scourgeDraw = mode == 0 ? 1 : 2;
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactDoomCards", "compact-doom-cards",
                [("DEATHBRINGER", mode), ("NEGATIVE_PULSE", mode), ("SCOURGE", mode), ("PUTREFY", mode), ("FEAR", mode), ("STRIKE_NECROBINDER", 0)],
                (lane, before, step) =>
                {
                    switch (step)
                    {
                        case 0:
                            if (PowerAmount(lane, 1, BasicPowerKind.Doom) != doom || PowerAmount(lane, 1, BasicPowerKind.Weak) != 1
                                || HasPower(lane, 0, BasicPowerKind.Doom))
                                throw new InvalidOperationException("Deathbringer did not apply its exact Doom/Weak roster.");
                            break;
                        case 1:
                            if (PowerAmount(lane, 1, BasicPowerKind.Doom) != doom + pulseDoom
                                || lane.Block - before.Block != pulseBlock)
                                throw new InvalidOperationException("NegativePulse did not pay block before its all-enemy Doom.");
                            break;
                        case 2:
                            if (PowerAmount(lane, 1, BasicPowerKind.Doom) != doom + pulseDoom + scourgeDoom
                                || lane.Count(ResumableDiscardProgram.Pile.Hand) != before.Count(ResumableDiscardProgram.Pile.Hand) - 1 + scourgeDraw
                                || lane.Count(ResumableDiscardProgram.Pile.Draw) != before.Count(ResumableDiscardProgram.Pile.Draw) - scourgeDraw)
                                throw new InvalidOperationException("Scourge did not apply its Doom before drawing its native count:"
                                    + $" doom={PowerAmount(lane, 1, BasicPowerKind.Doom)}/{doom + pulseDoom + scourgeDoom}"
                                    + $" hand={lane.Count(ResumableDiscardProgram.Pile.Hand)}/{before.Count(ResumableDiscardProgram.Pile.Hand)}"
                                    + $" draw={lane.Count(ResumableDiscardProgram.Pile.Draw)}/{before.Count(ResumableDiscardProgram.Pile.Draw)}.");
                            break;
                        case 3:
                            if (PowerAmount(lane, 1, BasicPowerKind.Weak) != 1 + putrefy
                                || PowerAmount(lane, 1, BasicPowerKind.Vulnerable) != putrefy)
                                throw new InvalidOperationException("Putrefy did not apply both debuffs to the chosen enemy.");
                            break;
                        case 4:
                            // Fear's own hit already uses Putrefy's Vulnerable:
                            // (7|8 printed + 11 Strength) x 1.5, minus the enemy's 3 block.
                            if (PowerAmount(lane, 1, BasicPowerKind.Vulnerable) != putrefy + vulnerable
                                || before.Creature(1).CurrentHp - lane.Creature(1).CurrentHp != 24 + mode
                                || !Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt)
                                    .Any(item => item.Kind == ResumableDiscardProgram.EventKind.AttackFinish && item.Dealer == 0))
                                throw new InvalidOperationException("Fear did not attack before applying its Vulnerable.");
                            break;
                        case 5:
                            // (6 printed + 11 Strength) x 1.5 truncated, with the block already gone.
                            if (PowerAmount(lane, 1, BasicPowerKind.Vulnerable) != putrefy + vulnerable
                                || before.Creature(1).CurrentHp - lane.Creature(1).CurrentHp != 25)
                                throw new InvalidOperationException("A later attack lost the applied Vulnerable multiplier.");
                            break;
                        default:
                            // Each full round ends the enemy side, whose native boundary is the
                            // only place that decrements the applied duration debuffs.
                            if (PowerAmount(lane, 1, BasicPowerKind.Doom) != doom + pulseDoom + scourgeDoom
                                || PowerAmount(lane, 1, BasicPowerKind.Vulnerable) != Math.Max(0, PowerAmount(before, 1, BasicPowerKind.Vulnerable) - 1)
                                || PowerAmount(lane, 1, BasicPowerKind.Weak) != Math.Max(0, PowerAmount(before, 1, BasicPowerKind.Weak) - 1))
                                throw new InvalidOperationException("A full round decayed Doom or skipped the applied duration tick.");
                            break;
                    }
                }, verifyAttackStarts: true);
            _completedChecks.Add($"CompactDoomCards:Mode{mode}:CardAppliedDoomWeakVulnerable:CompilerLaneMaterializationNative:"
                + "DoomCounterWithoutDuration:VulnerableMultiplier:ScourgeDrawOrder:PutrefyExhaust:FearEthereal:AttackStarts");
            evidence.Add(new { mode, doom, pulseDoom, scourgeDoom, putrefy, vulnerable });
        }
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-doom-cards.json"),
                JsonSerializer.Serialize(new { character = player.Character.Id.Entry, encounter = combat.Encounter?.Id.Entry,
                    seed = _request.Seed, compilation, census = new { admitted, unsupported, firstUnsupported }, modes = evidence },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // The real card play must leave the enemy doomed and the enemy-side hook must be the
    // first command that kills it. Damage, block and history stay untouched by Doom.
    private async Task AssertCompactDoomCardKillAsync(CombatState combat, Player player)
    {
        await PrepareCompactOstyAsync(combat, player, 0, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEATHBRINGER", Pile = "Hand" });
        var enemy = combat.Enemies.Single();
        await CreatureCmd.SetMaxHp(enemy, 40); await CreatureCmd.SetCurrentHp(enemy, 20);
        await SetBlockAsync(enemy, 3);
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var card = player.PlayerCombatState!.Hand.Cards.Single();
        var captured = CombatRootSnapshot.Capture(combat);
        var original = CaptureActual(combat, player, enemy);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        MoveStateSnapshot expected, projectedValues;
        SimulationSnapshot expectedEvaluation;
        string[] expectedPowers;
        List<object> evidence = [];
        using (SimulationNotificationIsolation.Enter())
        {
            var oracle = captured.ForkSimulator();
            var shadow = (SimulatedCombatState)oracle.State.CombatState;
            if (!oracle.ManualPlay(oracle.State.FindCard(card)!, null, out _))
                throw new InvalidOperationException("Doom card kill oracle unexpectedly suspended.");
            HookMirrors.BeforeSideTurnEnd(oracle, CombatSide.Enemy, [enemy]);
            if (oracle.HasPendingChoice)
                throw new InvalidOperationException("Doom card kill hook oracle unexpectedly suspended.");
            if (!CorePowerSupport.ApplyEnemyDeathPowers(oracle, shadow, shadow.KnownEnemies, new HashSet<uint>()))
                throw new InvalidOperationException("Doom card death cleanup unexpectedly suspended.");
            // The native safe point locks the terminal on its first killing check; the model
            // oracle must do the same before its evaluation can be compared with the lane.
            if (!oracle.CheckWinCondition(captured.StartTurnNumber))
                throw new InvalidOperationException("Doom card kill oracle did not reach the victory safe point.");
            expected = CaptureSimulated(oracle, shadow, player, enemy);
            var compact = new CompactCombatRoot(captured.ForkSimulator(), player);
            var adapter = compact.Adapter; var lane = adapter.Program;
            var mark = lane.State.Mark();
            lane.Begin(adapter.IndexOf(card)); lane.Run();
            if (!lane.CreaturePresent(adapter.CreatureIndex(enemy)))
                throw new InvalidOperationException("The card itself killed an enemy that only Doom may kill.");
            lane.BeforeEndSidePowerEffects(true);
            if (lane.CreaturePresent(adapter.CreatureIndex(enemy)) || !lane.CheckWinCondition() || lane.DefeatTerminal)
                throw new InvalidOperationException("Card-applied Doom did not reach the native terminal boundary.");
            var projected = adapter.Materialize(lane);
            adapter.AssertValues(lane, oracle); adapter.AssertValues(lane, projected);
            projectedValues = CaptureSimulated(projected, (SimulatedCombatState)projected.State.CombatState, player, enemy);
            AssertSnapshotEqual(expected, projectedValues, "CompactDoomCardKill", "Projection");
            expectedPowers = CompactPowerValues(shadow.EffectivePowers()).ToArray();
            string[] projectedPowers = CompactPowerValues(((SimulatedCombatState)projected.State.CombatState).EffectivePowers());
            if (!expectedPowers.SequenceEqual(projectedPowers))
                throw new InvalidOperationException("Card-applied Doom changed Power lifetime: expected=["
                    + string.Join(';', expectedPowers) + "] projected=[" + string.Join(';', projectedPowers) + "].");
            string[] expectedHistory = CompactHistory(oracle, adapter).ToArray();
            string[] projectedHistory = CompactHistory(projected, adapter).ToArray();
            if (!expectedHistory.SequenceEqual(projectedHistory))
                throw new InvalidOperationException("Card-applied Doom changed the projected damage path:\n"
                    + string.Join("\n", expectedHistory.Zip(projectedHistory).Where(item => item.First != item.Second)
                        .Select(item => $"expected={item.First} projected={item.Second}")));
            AssertCompactRngSet(oracle.Rng, projected.Rng);
            var evaluator = new CompactEvaluationDriver(captured, display, damage, policy);
            var reader = adapter.CreateReadView(); reader.Read(lane);
            expectedEvaluation = Release(evaluator.Evaluate(oracle));
            AssertCompactEvaluation(expectedEvaluation, Release(evaluator.Evaluate(projected)));
            AssertCompactEvaluation(expectedEvaluation, evaluator.Evaluate(reader));
            lane.State.Rollback(mark);
            if (!lane.State.Freeze().ContentEquals(compact.Initial.Open().State.Freeze()))
                throw new InvalidOperationException("Card-applied Doom rollback leaked its Power or death state.");
        }
        // The native card play leaves the doomed enemy alive; only the real enemy-side
        // hook may kill it, and it keeps its block and produces no damage history.
        if (!card.TryManualPlay(null)) throw new InvalidOperationException("Native Doom card play was rejected.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (enemy.IsDead) throw new InvalidOperationException("The native card play killed the enemy through damage.");
        await Hook.BeforeSideTurnEnd(combat, CombatSide.Enemy, [enemy]);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var actual = CaptureActual(combat, player, enemy);
        var actualPowers = CompactPowerValues(combat.Creatures.SelectMany(creature => creature.Powers)).ToArray();
        evidence.Add(new { expected, projected = projectedValues, actual, expectedPowers, actualPowers });
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-doom-card-kill.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
        AssertSnapshotEqual(expected, actual, "CompactDoomCardKill", "Native");
        if (!expectedPowers.SequenceEqual(actualPowers))
            throw new InvalidOperationException("Native card-applied Doom Power metadata differs.");
        _completedChecks.Add("CompactDoomCardKill:CardAppliedDoom:NativeEnemySideBoundary:TerminalLock:NoDamageOrBlockLoss:"
            + "PowerRetirement:FullKeysEvaluation:Rng:Rollback:FrozenRootAfterNative");
    }

    // Production compilation is the admitted frontier. Every instruction below is one
    // decompiled native command of these five cards, and any state the native play cannot
    // produce stays rejected instead of being narrowed into the admitted shape.
    private static object AssertCompactDoomCardCompilation(CombatState combat, Player player, out int admitted, out int unsupported, out string firstUnsupported)
    {
        (string Id, int Cost, CardCategory Category, ResumableDiscardProgram.Pile Pile, bool Ethereal,
            CardInstruction[] Base, CardInstruction[] Upgraded)[] cases =
        [
            ("DEATHBRINGER", 2, CardCategory.Skill, ResumableDiscardProgram.Pile.Discard, false,
                [new(CardInstructionKind.ApplyBasicPower, 21, BasicPowerKind.Doom, CardInstructionTarget.AllEnemies),
                    new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.Weak, CardInstructionTarget.AllEnemies)],
                [new(CardInstructionKind.ApplyBasicPower, 26, BasicPowerKind.Doom, CardInstructionTarget.AllEnemies),
                    new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.Weak, CardInstructionTarget.AllEnemies)]),
            ("NEGATIVE_PULSE", 1, CardCategory.Skill, ResumableDiscardProgram.Pile.Discard, false,
                [new(CardInstructionKind.GainBlock, 5),
                    new(CardInstructionKind.ApplyBasicPower, 7, BasicPowerKind.Doom, CardInstructionTarget.AllEnemies)],
                [new(CardInstructionKind.GainBlock, 6),
                    new(CardInstructionKind.ApplyBasicPower, 11, BasicPowerKind.Doom, CardInstructionTarget.AllEnemies)]),
            ("SCOURGE", 1, CardCategory.Skill, ResumableDiscardProgram.Pile.Discard, false,
                [new(CardInstructionKind.ApplyBasicPower, 13, BasicPowerKind.Doom, CardInstructionTarget.ChosenEnemy),
                    new(CardInstructionKind.Draw, 1)],
                [new(CardInstructionKind.ApplyBasicPower, 16, BasicPowerKind.Doom, CardInstructionTarget.ChosenEnemy),
                    new(CardInstructionKind.Draw, 2)]),
            ("PUTREFY", 1, CardCategory.Skill, ResumableDiscardProgram.Pile.Exhaust, false,
                [new(CardInstructionKind.ApplyBasicPower, 2, BasicPowerKind.Weak, CardInstructionTarget.ChosenEnemy),
                    new(CardInstructionKind.ApplyBasicPower, 2, BasicPowerKind.Vulnerable, CardInstructionTarget.ChosenEnemy)],
                [new(CardInstructionKind.ApplyBasicPower, 3, BasicPowerKind.Weak, CardInstructionTarget.ChosenEnemy),
                    new(CardInstructionKind.ApplyBasicPower, 3, BasicPowerKind.Vulnerable, CardInstructionTarget.ChosenEnemy)]),
            ("FEAR", 1, CardCategory.Attack, ResumableDiscardProgram.Pile.Discard, true,
                [new(CardInstructionKind.AttackTarget, 7),
                    new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.Vulnerable, CardInstructionTarget.ChosenEnemy)],
                [new(CardInstructionKind.AttackTarget, 8),
                    new(CardInstructionKind.ApplyBasicPower, 2, BasicPowerKind.Vulnerable, CardInstructionTarget.ChosenEnemy)]),
        ];
        List<object> results = [];
        foreach (var item in cases)
        {
            CardModel canonical = CompactDoomCanonical(item.Id);
            ResumableDiscardProgram.Card program = CompactCardProgramCompiler.Compile(canonical, includeAttacks: true);
            if (program.Cost != item.Cost || program.Category != item.Category || program.ResultPile != item.Pile
                || program.Ethereal != item.Ethereal || program.Sly || program.CostsX || program.Retain || program.Unplayable
                || program.DrawCost != null || program.Effects.Count != item.Base.Length
                || !Enumerable.Range(0, item.Base.Length).All(index => program.Effects[index] == item.Base[index]))
                throw new InvalidOperationException($"Compact doom card program differs from the native OnPlay: {item.Id}.");
            CardModel upgraded = PredictionUtils.CreateCard(canonical, player);
            PredictionUtils.UpgradeCard(upgraded);
            ResumableDiscardProgram.Card upgradedProgram = CompactCardProgramCompiler.Compile(upgraded, includeAttacks: true);
            if (!upgraded.IsUpgraded || upgradedProgram.Effects.Count != item.Upgraded.Length
                || !Enumerable.Range(0, item.Upgraded.Length).All(index => upgradedProgram.Effects[index] == item.Upgraded[index]))
                throw new InvalidOperationException($"Compact doom card upgrade changed its admitted commands: {item.Id}.");
            CardModel unrepresented = PredictionUtils.CreateCard(canonical, player);
            unrepresented.AddKeyword(CardKeyword.Eternal);
            ExpectDoomCompileRejected(unrepresented, item.Id);
            ExpectDoomCompileRejected(canonical, item.Id, includeAttacks: false);
            results.Add(new { item.Id, cost = program.Cost, category = program.Category.ToString(), pile = program.ResultPile.ToString(),
                ethereal = program.Ethereal, instructions = item.Base.Select(instruction => instruction.ToString()).ToArray() });
        }
        // The same frozen character pool as the CallOfTheVoid census: this batch must not
        // shrink it, and the five admitted types move the exact-closure count only.
        var census = CombatRootSnapshot.Capture(combat).ForkSimulator();
        if (!census.TryGetRootEligibleCharacterCardsForCombat(player, combat.RunState.CardMultiplayerConstraint, out var pool) || pool.Count != 78)
            throw new InvalidOperationException("Compact doom card census requires the frozen 78-candidate character pool.");
        admitted = 0; unsupported = 0; firstUnsupported = "";
        foreach (CardModel candidate in pool)
        {
            try { _ = CompactCardProgramCompiler.Compile(candidate, includeAttacks: true); admitted++; }
            catch (NotSupportedException)
            {
                unsupported++;
                if (firstUnsupported.Length == 0) firstUnsupported = candidate.Id.Entry;
            }
        }
        if (admitted == 0 || unsupported == 0 || !pool.Any(card => CompactDoomCardIds.Contains(card.Id.Entry)))
            throw new InvalidOperationException("Compact doom card census lost its admitted or unsupported candidates.");
        return new { instructions = results, poolSize = pool.Count };

        static void ExpectDoomCompileRejected(CardModel card, string id, bool includeAttacks = true)
        {
            try { _ = CompactCardProgramCompiler.Compile(card, includeAttacks); }
            catch (NotSupportedException) { return; }
            throw new InvalidOperationException($"Compact doom card state {id} was not explicitly rejected.");
        }
    }

    private static CardModel CompactDoomCanonical(string id) => id switch
    {
        "DEATHBRINGER" => CanonicalModels.Card<Deathbringer>(),
        "NEGATIVE_PULSE" => CanonicalModels.Card<NegativePulse>(),
        "SCOURGE" => CanonicalModels.Card<Scourge>(),
        "PUTREFY" => CanonicalModels.Card<Putrefy>(),
        _ => CanonicalModels.Card<Fear>()
    };

    private static bool HasPower(ResumableDiscardProgram lane, int owner, BasicPowerKind kind)
        => PowerSlots(lane, owner, kind).Length != 0;

    private static int PowerAmount(ResumableDiscardProgram lane, int owner, BasicPowerKind kind)
    {
        int[] slots = PowerSlots(lane, owner, kind);
        if (slots.Length != 1) throw new InvalidOperationException($"Compact lane has {slots.Length} {kind} slots for creature {owner}.");
        return lane.Power(slots[0]).Amount;
    }

    private static int[] PowerSlots(ResumableDiscardProgram lane, int owner, BasicPowerKind kind)
        => Enumerable.Range(0, lane.PowerCount).Where(index => lane.PowerDefinition(index).Owner == owner
            && lane.PowerDefinition(index).Kind == kind).ToArray();
}
