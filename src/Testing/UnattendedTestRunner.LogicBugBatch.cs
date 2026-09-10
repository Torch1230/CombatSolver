using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.Common;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using System.Reflection;
using HarmonyLib;

using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertTurnStartDamageSpiteAsync(CombatState combat, Player player, string cardId = "SPITE")
    {
        Creature source = combat.Enemies.Single();
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = cardId, Pile = "Draw" });
        if (cardId == "TEAR_ASUNDER")
            for (int hit = 0; hit < 2; hit++)
                await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), player.Creature, 1, ValueProp.Unblockable | ValueProp.Unpowered, player.Creature);
        await PowerCmd.Apply<InfernoPower>(new ThrowingPlayerChoiceContext(), player.Creature, 9, player.Creature, null);
        player.Creature.GetPower<InfernoPower>()!.IncrementSelfDamage();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        SimulationSnapshot predicted = InvokeForcedTerminalReplay(driver,
            [new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber),
             new PlanAction(PlanActionKind.PlayCard, root.StartTurnNumber + 1, CardId: cardId, TargetCombatId: source.CombatId)],
            null, root.StartTurnNumber, null);
        try
        {
            CombatPredictionSimulator simulator = predicted.Simulator;
            MoveStateSnapshot expected = CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState, player, source);
            CombatPredictionSimulator fork = simulator.Fork();
            AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, source), "TurnStartDamageSpite", "Fork");
            await AdvanceMercuryActualTurnAsync(combat, player, expectVictory: false);
            if (!FindActualHandCard(player, cardId, 0).TryManualPlay(source))
                throw new InvalidOperationException($"Native {cardId} was not playable.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(expected, CaptureActual(combat, player, source), "TurnStartDamageSpite", "NativeAfterSpite");
            SimulatedCombatState forkState = (SimulatedCombatState)fork.State.CombatState;
            if (!forkState.HasLostHpThisTurn(player.Creature))
                throw new InvalidOperationException("Turn-start damage was absent from fork history.");
            forkState.CurrentSide = CombatSide.Enemy;
            if (forkState.HasLostHpThisTurn(player.Creature))
                throw new InvalidOperationException("Player-side damage leaked into the enemy turn.");
            CombatPredictionSimulator extraTurn = simulator.Fork();
            SimulatedCombatState extraState = (SimulatedCombatState)extraTurn.State.CombatState;
            extraState.AdvancePlayerTurn(player);
            if (extraState.HasLostHpThisTurn(player.Creature))
                throw new InvalidOperationException("Previous-turn damage leaked into an extra player turn.");
        }
        finally { predicted.ReleaseSimulator(); }
    }

    private async Task AssertSummonDeathPowerOrderAsync(CombatState combat, Player player)
    {
        Creature source = combat.Enemies.Single();
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), source, 1, source, null);
        for (int round = 0; round < 2; round++)
        {
            Creature? victim = null;
            if (round == 1)
            {
                victim = combat.Enemies.First(enemy => enemy != source);
                await CreatureCmd.SetCurrentHp(victim, 1);
                await ClearPlayerPilesAsync(player);
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
            }
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
                SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
            var before = CaptureWriteDensity(root.ForkSimulator(), player, source);
            List<PlanAction> actions = [];
            if (victim != null) actions.Add(new PlanAction(PlanActionKind.PlayCard, root.StartTurnNumber,
                CardId: "STRIKE_IRONCLAD", TargetCombatId: victim.CombatId));
            actions.Add(new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber));
            SimulationSnapshot predicted = InvokeForcedTerminalReplay(driver, actions, null, root.StartTurnNumber, null);
            try
            {
                CombatPredictionSimulator simulator = predicted.Simulator;
                SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
                MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, source);
                CombatPredictionSimulator fork = simulator.Fork();
                AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, source),
                    "SummonDeathPowerOrder", "Fork");
                if (victim != null)
                {
                    if (!FindActualHandCard(player, "STRIKE_IRONCLAD", 0).TryManualPlay(victim))
                        throw new InvalidOperationException("Native summon fixture Strike was not playable.");
                    await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                }
                await AdvanceMercuryActualTurnAsync(combat, player, expectVictory: false);
                AssertSnapshotEqual(expected, CaptureActual(combat, player, source), "SummonDeathPowerOrder", "NativeNextTurn");
                RecordWriteDensity(victim is null ? "SummonAndNextTurn" : "KillSummonedEnemyAndNextTurn", before, simulator, player, source);
            }
            finally { predicted.ReleaseSimulator(); }
        }
    }

    private async Task AssertLivingFogSummonIntentAsync(CombatState combat, Player player)
    {
        Creature source = combat.Enemies.Single(enemy => enemy.Monster is MegaCrit.Sts2.Core.Models.Monsters.LivingFog);
        int spawnedCount = MonsterValueReader.ReadInt(source.Monster!, "BloatAmount");
        ConfigureMonsterMove(source, new UnattendedMonsterMoveCheck { MoveId = "BLOAT_MOVE" });
        for (int round = 0; round < 2; round++)
        {
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
                SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
            SimulationSnapshot predicted = InvokeForcedTerminalReplay(driver,
                [new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber)], null, root.StartTurnNumber, null);
            try
            {
                CombatPredictionSimulator simulator = predicted.Simulator;
                SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
                MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, source);
                CombatPredictionSimulator fork = simulator.Fork();
                AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, source),
                    "LivingFogSummonIntent", "Fork");
                await AdvanceMercuryActualTurnAsync(combat, player, expectVictory: false);
                AssertSnapshotEqual(expected, CaptureActual(combat, player, source), "LivingFogSummonIntent", "NativeNextTurn");
                int bombs = shadow.Enemies.Count(enemy => enemy.Monster is MegaCrit.Sts2.Core.Models.Monsters.GasBomb);
                if (bombs != (round == 0 ? spawnedCount : 0))
                    throw new InvalidOperationException($"Gas bomb lifecycle has {bombs} active bombs after round {round}.");
            }
            finally { predicted.ReleaseSimulator(); }
        }
    }

    private async Task AssertRatSummonNextIntentAsync(CombatState combat, Player player)
    {
        Creature[] originals = combat.Enemies.ToArray();
        if (originals.Length != 3 || originals.Any(enemy => enemy.Monster is not MegaCrit.Sts2.Core.Models.Monsters.TwoTailedRat))
            throw new InvalidOperationException("Rat summon fixture requires three original rats.");
        foreach (Creature enemy in originals)
            ConfigureMonsterMove(enemy, new UnattendedMonsterMoveCheck
            {
                MoveId = ReferenceEquals(enemy, originals[^1]) ? "CALL_FOR_BACKUP_MOVE" : "SCREECH_MOVE"
            });
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        SimulationSnapshot predicted = InvokeForcedTerminalReplay(driver,
            [new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber)], null, root.StartTurnNumber, null);
        try
        {
            CombatPredictionSimulator simulator = predicted.Simulator;
            SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
            MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, originals[0]);
            CombatPredictionSimulator fork = simulator.Fork();
            AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, originals[0]),
                "RatSummonNextIntent", "Fork");
            await AdvanceMercuryActualTurnAsync(combat, player, expectVictory: false);
            AssertSnapshotEqual(expected, CaptureActual(combat, player, originals[0]), "RatSummonNextIntent", "NativeNextTurn");
            if (shadow.Enemies.Count != 4 || combat.Enemies.Count != 4)
                throw new InvalidOperationException("Rat summon did not add exactly one enemy.");
        }
        finally
        {
            predicted.ReleaseSimulator();
        }
    }

    private async Task AssertFuneraryMaskBeforeDrawAsync(CombatState combat, Player player)
    {
        await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = "FUNERARY_MASK" });
        await ClearPlayerPilesAsync(player);
        foreach (string cardId in new[] { "STRIKE_NECROBINDER", "DEFEND_NECROBINDER", "POKE", "BODYGUARD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = cardId, Pile = "Draw" });
        Creature enemy = combat.Enemies[0];
        for (int turn = 1; turn <= 2; turn++)
        {
            CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
            if (shadow.PrepareBeforeHandDraw(simulator, player, new TurnStartChoiceCursor(null)))
                throw new InvalidOperationException("Funerary Mask unexpectedly requested a choice.");
            MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, enemy);
            CombatPredictionSimulator fork = simulator.Fork();
            AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy),
                "FuneraryMaskBeforeDraw", "Fork");
            await MegaCrit.Sts2.Core.Hooks.Hook.BeforeHandDraw(combat, player, new BlockingPlayerChoiceContext());
            AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), "FuneraryMaskBeforeDraw", "NativeHook");
            if (simulator.State.GetPlayerCombatState(player).DrawPile.Cards.Count(card => card.Preview is Soul) != 3)
                throw new InvalidOperationException("Funerary Mask did not generate exactly three Souls only on the first turn.");
            if (turn == 1)
                player.PlayerCombatState!.IncrementTurnNumber();
        }
    }

    private async Task AssertCardEnergyGainCommandAsync(CombatState combat, Player player)
    {
        Creature enemy = combat.Enemies[0];
        if (!player.Creature.HasPower<NoEnergyGainPower>())
            throw new InvalidOperationException("Energy command fixture requires NoEnergyGainPower.");
        foreach (string cardId in new[] { "ALIGNMENT", "BORROWED_TIME", "DOUBLE_ENERGY", "FORGOTTEN_RITUAL",
                     "FUEL", "LUMINESCE", "PRODUCTION", "SUPERCRITICAL", "TACTICIAN", "WISP", "TURBO" })
        {
            await ClearPlayerPilesAsync(player);
            SetEnergy(player, 10);
            SetStars(player, 10);
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = cardId, Pile = "Hand" });
            CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
            PlaySimulatedCard(simulator, shadow, FindSimulatedHandCard(simulator, player, cardId, 0), null, combat.Enemies);
            MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, enemy);
            if (!FindActualHandCard(player, cardId, 0).TryManualPlay(null))
                throw new InvalidOperationException($"Native energy fixture card {cardId} was not playable.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), "CardEnergyGainCommand", cardId);
            _completedChecks.Add("CardEnergyGainCommand:" + cardId);
        }
    }

    private async Task AssertMelancholyOstyDeathAsync(CombatState combat, Player player)
    {
        Creature osty = player.Osty ?? throw new InvalidOperationException("Melancholy fixture requires Osty.");
        Creature enemy = combat.Enemies[0];
        if (!osty.IsAlive)
            throw new InvalidOperationException("Osty must be alive before the death comparison.");
        await ClearPlayerPilesAsync(player);
        foreach (string pile in new[] { "Hand", "Draw", "Discard", "Exhaust" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection
            {
                CardId = "MELANCHOLY", Pile = pile, UpgradeLevels = 1,
                EnchantmentId = "SWIFT", EnchantmentAmount = 2,
                AfflictionId = "BOUND", AfflictionAmount = 3
            });
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator direct = root.ForkSimulator();
        CombatPredictionSimulator fork = direct.Fork();
        MoveStateSnapshot initial = CaptureSimulated(direct, (SimulatedCombatState)direct.State.CombatState, player, enemy);
        MoveStateSnapshot? expected = null;
        foreach (CombatPredictionSimulator simulator in new[] { fork, direct })
        {
            simulator.Damage([osty], osty.CurrentHp + 10,
                MegaCrit.Sts2.Core.ValueProps.ValueProp.Unblockable | MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered,
                enemy, null, null);
            MoveStateSnapshot state = CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState, player, enemy);
            if (expected == null)
            {
                expected = state;
                AssertSnapshotEqual(initial, CaptureSimulated(direct, (SimulatedCombatState)direct.State.CombatState, player, enemy),
                    "MelancholyOstyDeath", "ParentIsolation");
            }
            else
                AssertSnapshotEqual(expected, state, "MelancholyOstyDeath", "DirectAndFork");
            CombatPredictionSimulator afterDeath = simulator.Fork();
            AssertSnapshotEqual(state, CaptureSimulated(afterDeath, (SimulatedCombatState)afterDeath.State.CombatState, player, enemy),
                "MelancholyOstyDeath", "ForkAfterDeath");
        }
        await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), osty, osty.CurrentHp + 10,
            MegaCrit.Sts2.Core.ValueProps.ValueProp.Unblockable | MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered,
            enemy, null, null);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        AssertSnapshotEqual(expected!, CaptureActual(combat, player, enemy), "MelancholyOstyDeath", "NativeDeath");
    }

    private async Task AssertQueenInfernoMinionDeathAsync(CombatState combat, Player player)
    {
        Creature minion = combat.Enemies.Single(enemy => enemy.Monster is MegaCrit.Sts2.Core.Models.Monsters.TorchHeadAmalgam);
        Creature queen = combat.Enemies.Single(enemy => enemy.Monster is MegaCrit.Sts2.Core.Models.Monsters.Queen);
        bool terminal = _request.ScenarioId == "QUEEN-INFERNO-TERMINAL";
        if (!terminal)
            await CreatureCmd.SetCurrentHp(queen, queen.MaxHp);
        ConfigureMonsterMove(queen, new UnattendedMonsterMoveCheck
        {
            MoveId = "BURN_BRIGHT_FOR_ME_MOVE"
        });
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
        PlaySimulatedCard(simulator, shadow, FindSimulatedHandCard(simulator, player, "BLOODLETTING", 0),
            null, combat.Enemies);
        MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, minion);
        CombatPredictionSimulator fork = simulator.Fork();
        AssertSnapshotEqual(expected,
            CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, minion),
            "QueenInfernoMinionDeath", "Fork");
        if (terminal)
        {
            MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(method => method.Name == "EndCombatInternal"
                    && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
            PropertyInfo stateProperty = endCombat.GetParameters()[0].ParameterType.GetProperty("State")
                ?? throw new MissingMemberException("CombatTurnState.State");
            MethodInfo prefix = typeof(UnattendedTestRunner).GetMethod(
                nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(ObserveMercuryCombatEndPrefix));
            if (_mercuryTerminalObservation != null)
                throw new InvalidOperationException("Queen terminal observation is already active.");
            Harmony patch = new("CombatSolver.Testing.QueenInfernoTerminal." + _request.RunId);
            MercuryTerminalObservation observation = new(this, combat, player, minion, stateProperty, "QueenInfernoTerminal");
            _mercuryTerminalObservation = observation;
            try
            {
                CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
                patch.Patch(endCombat, prefix: new HarmonyMethod(prefix));
                if (!FindActualHandCard(player, "BLOODLETTING", 0).TryManualPlay(null))
                    throw new InvalidOperationException("Native Bloodletting was not playable.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                while (observation.Snapshot == null || !observation.CombatEnded || CombatManager.Instance.IsInProgress)
                {
                    EnsureWithinDeadline();
                    observation.Failure?.Throw();
                    await NextFrameAsync();
                }
                observation.Failure?.Throw();
                AssertSnapshotEqual(expected, observation.Snapshot, "QueenInfernoTerminal", "NativePreTeardown");
            }
            finally
            {
                patch.Unpatch(endCombat, prefix);
                CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded;
                _mercuryTerminalObservation = null;
            }
            return;
        }
        if (!FindActualHandCard(player, "BLOODLETTING", 0).TryManualPlay(null))
            throw new InvalidOperationException("Native Bloodletting was not playable.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        AssertSnapshotEqual(expected, CaptureActual(combat, player, minion),
            "QueenInfernoMinionDeath", "NativeCard");
        if (shadow.Enemies.Count != 1 || !ReferenceEquals(shadow.Enemies[0], queen)
            || combat.Enemies.Count != 1 || !ReferenceEquals(combat.Enemies[0], queen))
            throw new InvalidOperationException("Queen minion death did not remove the minion from both rosters.");
    }

    private static void AssertNarrowOrderedPileCapacity(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null),
            searchProfile: SolverSearchProfile.Deep);
        List<SimulationSnapshot> snapshots = [];
        try
        {
            for (int a = 0; a < 4; a++)
            for (int b = 0; b < 4; b++)
            for (int c = 0; c < 4; c++)
            {
                if (a == b || a == c || b == c)
                    continue;
                int d = 6 - a - b - c;
                List<int> remaining = [0, 1, 2, 3];
                List<PlanAction> actions = [];
                foreach (int index in new[] { a, b, c, d })
                {
                    int occurrence = remaining.IndexOf(index);
                    actions.Add(new PlanAction(PlanActionKind.PlayCard, root.StartTurnNumber,
                        CardId: "DEFLECT", CardOccurrence: occurrence));
                    remaining.RemoveAt(occurrence);
                }
                actions.Add(new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber));
                snapshots.Add(InvokeForcedTerminalReplay(driver, actions, null, 0, null));
            }
            if (snapshots.Any(snapshot => snapshot.PocketwatchCardThreshold < 0)
                || snapshots.Select(snapshot => snapshot.StateKey).Distinct().Count() < 8
                || snapshots.Select(snapshot => snapshot.ProjectedShuffleOrderKey).Distinct().Count() < 8)
                throw new InvalidOperationException("Narrow retention fixture did not produce eight distinct Pocketwatch pile orders.");
            driver.VerifyNarrowOrderedPileCapacityForTesting(snapshots);
        }
        finally
        {
            foreach (SimulationSnapshot snapshot in snapshots)
                snapshot.ReleaseSimulator();
        }
    }

    private async Task AssertGamblingChipSlyOrderAsync(CombatState combat, Player player)
    {
        Creature enemy = combat.Enemies[0];
        CardModel nativeCard = FindActualHandCard(player, "RICOCHET", 0);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator parent = root.ForkSimulator();
        CombatPredictionSimulator fork = parent.Fork();
        MoveStateSnapshot? expected = null;
        foreach (CombatPredictionSimulator simulator in new[] { parent, fork })
        {
            SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
            TurnStartChoiceCursor choices = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
                CardChoiceSupport.BuildRequestedChoice(request.Spec!, ["RICOCHET"]));
            if (!TurnStartChoiceSupport.ResolveDiscardAndDraw(
                    simulator, shadow, player, choices, "GAMBLING_CHIP"))
                throw new InvalidOperationException("Gambling Chip fixture encountered a choice.");
            MoveStateSnapshot result = CaptureSimulated(simulator, shadow, player, enemy);
            if (expected != null)
                AssertSnapshotEqual(expected, result, "GamblingChipSlyOrder", "Fork");
            expected = result;
        }
        await CardCmd.DiscardAndDraw(new BlockingPlayerChoiceContext(), [nativeCard], 1);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        AssertSnapshotEqual(expected!, CaptureActual(combat, player, enemy),
            "GamblingChipSlyOrder", "NativeDiscardAndDraw");
    }

    private async Task AssertGalvanicGeneratedPowerAsync(CombatState combat, Player player)
    {
        Creature enemy = combat.Enemies[0];
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
        PredictedCard card = PredictedCard.Create(ModelDb.Card<Automation>(), player);
        simulator.AddGeneratedCardToCombat(card, PileType.Hand, player,
            resultKind: CardGenerationResultKind.Fixed);
        shadow.NormalizeCardAfflictions(simulator);
        MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, enemy);
        CardModel actualCard = combat.CreateCard(ModelDb.Card<Automation>(), player);
        CardPileAddResult added = await CardPileCmd.AddGeneratedCardToCombat(actualCard, PileType.Hand, player);
        if (!added.success)
            throw new InvalidOperationException("Native generated power could not enter combat.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy),
            "GalvanicGeneratedPower", "NativeEntry");
        CombatPredictionSimulator fork = simulator.Fork();
        SimulatedCombatState forkState = (SimulatedCombatState)fork.State.CombatState;
        AssertSnapshotEqual(expected, CaptureSimulated(fork, forkState, player, enemy),
            "GalvanicGeneratedPower", "ForkEntry");
        PlaySimulatedCard(fork, forkState, FindSimulatedHandCard(fork, player, "AUTOMATION", 0),
            null, combat.Enemies);
        AssertSnapshotEqual(expected, CaptureSimulated(simulator, shadow, player, enemy),
            "GalvanicGeneratedPower", "ParentAfterForkPlay");
        if (!actualCard.TryManualPlay(null))
            throw new InvalidOperationException("Native generated power was not playable.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        AssertSnapshotEqual(CaptureSimulated(fork, forkState, player, enemy),
            CaptureActual(combat, player, enemy), "GalvanicGeneratedPower", "NativePlay");
    }

    private void AssertTurnEndPowerOrderFork(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator left = root.ForkSimulator();
        CombatPredictionSimulator right = root.ForkSimulator();
        SimulatedCombatState leftState = (SimulatedCombatState)left.State.CombatState;
        SimulatedCombatState rightState = (SimulatedCombatState)right.State.CombatState;
        leftState.Apply<NoDrawPower>(player.Creature, 1);
        leftState.Apply<DarkEmbracePower>(player.Creature, 1);
        rightState.Apply<DarkEmbracePower>(player.Creature, 1);
        rightState.Apply<NoDrawPower>(player.Creature, 1);
        PowerLifecycleSupport.ResolvePowerAmountChanges(left, leftState);
        PowerLifecycleSupport.ResolvePowerAmountChanges(right, rightState);
        StateFingerprintBuilder leftKey = new();
        StateFingerprintBuilder rightKey = new();
        leftState.AppendFingerprint(ref leftKey, left);
        rightState.AppendFingerprint(ref rightKey, right);
        if (leftKey.Finish() == rightKey.Finish())
            throw new InvalidOperationException("Turn-end power order collides in the branch fingerprint.");
        Creature enemy = combat.Enemies[0];
        MoveStateSnapshot parentBefore = CaptureSimulated(left, leftState, player, enemy);
        if (parentBefore.ExactContinuationState
            == CaptureSimulated(right, rightState, player, enemy).ExactContinuationState)
            throw new InvalidOperationException("Turn-end power order collides in continuation state.");
        CombatPredictionSimulator fork = left.Fork();
        SimulatedCombatState forkState = (SimulatedCombatState)fork.State.CombatState;
        StateFingerprintBuilder forkKey = new();
        forkState.AppendFingerprint(ref forkKey, fork);
        if (forkKey.Finish() != leftKey.Finish())
            throw new InvalidOperationException("Fork changed the ordered power fingerprint.");
        if (!PlayerTurnEndLifecycle.RunPhaseTwo(fork, forkState, [player.Creature], 1))
            throw new InvalidOperationException("Turn-end order fixture encountered a choice.");
        AssertSnapshotEqual(parentBefore, CaptureSimulated(left, leftState, player, enemy),
            "TurnEndPowerOrder", "ParentAfterFork");
        if (!PlayerTurnEndLifecycle.RunPhaseTwo(left, leftState, [player.Creature], 1)
            || !PlayerTurnEndLifecycle.RunPhaseTwo(right, rightState, [player.Creature], 1))
            throw new InvalidOperationException("Turn-end order fixture encountered a choice.");
        AssertSnapshotEqual(CaptureSimulated(left, leftState, player, enemy),
            CaptureSimulated(fork, forkState, player, enemy), "TurnEndPowerOrder", "ForkResult");
        if (left.State.GetPlayerCombatState(player).Hand.Cards.Count != 1
            || right.State.GetPlayerCombatState(player).Hand.Cards.Count != 0)
            throw new InvalidOperationException("Power order must distinguish allowed and blocked end-turn draws.");
    }

    private static void AssertBoundCounterFork(CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator parent = root.ForkSimulator();
        SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
        Player player = root.PlayerIdentity;
        int turn = parentCombat.GetPlayerTurnNumber(player);
        ChainsOfBindingPower parentPower = parentCombat.GetPower<ChainsOfBindingPower>(player.Creature)!;
        ChainsOfBindingPredictionState parentState = parent.StateStore.Get(
            parentPower, static () => new ChainsOfBindingPredictionState());
        parentState.RecordBoundCardAfflicted(turn);
        CombatPredictionSimulator child = parent.Fork();
        SimulatedCombatState childCombat = (SimulatedCombatState)child.State.CombatState;
        ChainsOfBindingPower childPower = childCombat.GetPower<ChainsOfBindingPower>(player.Creature)!;
        ChainsOfBindingPredictionState childState = child.StateStore.Get(
            childPower, static () => new ChainsOfBindingPredictionState());
        childState.RecordBoundCardAfflicted(turn);
        if (parentState.GetBoundCardsAfflictedThisTurn(turn) != 1
            || childState.GetBoundCardsAfflictedThisTurn(turn) != 2)
            throw new InvalidOperationException("Bound quota leaked between forks.");
        StateFingerprintBuilder parentKey = new();
        StateFingerprintBuilder childKey = new();
        parentCombat.AppendFingerprint(ref parentKey, parent);
        childCombat.AppendFingerprint(ref childKey, child);
        if (parentKey.Finish() == childKey.Finish())
            throw new InvalidOperationException("Bound quotas collide in the state fingerprint.");
        if (childState.GetBoundCardsAfflictedThisTurn(turn + 1) != 0)
            throw new InvalidOperationException("Bound quota remained spent in a new player turn.");
        childState.RecordBoundCardAfflicted(turn + 1);
        if (childState.GetBoundCardsAfflictedThisTurn(turn + 1) != 1
            || parentState.GetBoundCardsAfflictedThisTurn(turn) != 1)
            throw new InvalidOperationException("Bound quota did not advance independently to the next turn.");
    }

    private async Task AssertEnergyResetPowerOrderAsync(CombatState combat, Player player)
    {
        CombatRootSnapshot emptyRoot = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator left = emptyRoot.ForkSimulator();
        CombatPredictionSimulator right = emptyRoot.ForkSimulator();
        SimulatedCombatState leftState = (SimulatedCombatState)left.State.CombatState;
        SimulatedCombatState rightState = (SimulatedCombatState)right.State.CombatState;
        leftState.Apply<SpinnerPower>(player.Creature, 1);
        leftState.Apply<LightningRodPower>(player.Creature, 1);
        rightState.Apply<LightningRodPower>(player.Creature, 1);
        rightState.Apply<SpinnerPower>(player.Creature, 1);
        StateFingerprintBuilder leftKey = new();
        StateFingerprintBuilder rightKey = new();
        leftState.AppendFingerprint(ref leftKey, left);
        rightState.AppendFingerprint(ref rightKey, right);
        if (leftKey.Finish() == rightKey.Finish())
            throw new InvalidOperationException("Energy-reset power order collides in the branch fingerprint.");
        if (CaptureSimulated(left, leftState, player, combat.Enemies[0]).ExactContinuationState
            == CaptureSimulated(right, rightState, player, combat.Enemies[0]).ExactContinuationState)
            throw new InvalidOperationException("Energy-reset power order collides in continuation state.");
        bool reapply = _request.ScenarioId.EndsWith("-REAPPLY", StringComparison.Ordinal);
        bool overflow = _request.ScenarioId.EndsWith("-OVERFLOW", StringComparison.Ordinal);
        bool reverse = reapply || _request.ScenarioId.EndsWith("-REVERSE", StringComparison.Ordinal);
        string[] powerIds = reverse
            ? ["LIGHTNING_ROD_POWER", "SPINNER_POWER"]
            : ["SPINNER_POWER", "LIGHTNING_ROD_POWER"];
        foreach (string id in powerIds)
            await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            {
                PowerId = id, Target = "Player", Amount = overflow && id == "SPINNER_POWER" ? 3 : 1
            });
        foreach (string id in new[] { "GENESIS_POWER", "STAR_NEXT_TURN_POWER", "RADIANCE_POWER" })
            await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            {
                PowerId = id, Target = "Player", Amount = 1
            });
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
        if (reapply)
        {
            shadow.SetAmount<LightningRodPower>(player.Creature, 0);
            shadow.Apply<LightningRodPower>(player.Creature, 1);
            PowerLifecycleSupport.ResolvePowerAmountChanges(simulator, shadow);
            await PowerCmd.Remove(player.Creature.GetPower<LightningRodPower>()!);
            await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            {
                PowerId = "LIGHTNING_ROD_POWER", Target = "Player", Amount = 1
            });
        }
        CombatPredictionSimulator fork = simulator.Fork();
        if (!PersistentPowerSupport.TriggerAfterEnergyReset(simulator, shadow, player))
            throw new InvalidOperationException("Energy-reset fixture encountered a choice.");
        MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, combat.Enemies[0]);
        SimulatedCombatState forkState = (SimulatedCombatState)fork.State.CombatState;
        if (!PersistentPowerSupport.TriggerAfterEnergyReset(fork, forkState, player))
            throw new InvalidOperationException("Fork energy-reset fixture encountered a choice.");
        AssertSnapshotEqual(expected, CaptureSimulated(fork, forkState, player, combat.Enemies[0]),
            "EnergyResetPowerOrder", "Fork");
        await MegaCrit.Sts2.Core.Hooks.Hook.AfterEnergyReset(combat, player);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        AssertSnapshotEqual(expected, CaptureActual(combat, player, combat.Enemies[0]), "EnergyResetPowerOrder", "NativeHook");
    }

    private async Task AssertReplayStartHistoryAsync(CombatState combat, Player player)
    {
        Creature enemy = combat.Enemies[0];
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        PlanAction action = new(PlanActionKind.PlayCard, player.PlayerCombatState!.TurnNumber,
            CardId: "SLICE", TargetCombatId: enemy.CombatId);
        bool echo = _request.ScenarioId == "REPLAY-START-HISTORY-ECHO";
        SimulationSnapshot[] predictions = echo
            ? [InvokeForcedTerminalReplay(driver, [action], null, 0, null),
               InvokeForcedTerminalReplay(driver, [action, action], null, 0, null)]
            : [InvokeForcedTerminalReplay(driver, [action], null, 0, null)];
        try
        {
            for (int index = 0; index < predictions.Length; index++)
            {
                CombatPredictionSimulator simulator = predictions[index].Simulator;
                SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
                MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, enemy);
                CombatPredictionSimulator fork = simulator.Fork();
                if (!FindActualHandCard(player, "SLICE", 0).TryManualPlay(enemy))
                    throw new InvalidOperationException("Native repeated Slice was not playable.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), "ReplayStartHistory", $"NativeSlice{index}");
                CombatPredictionSimulator recaptured = CombatRootSnapshot.Capture(combat).ForkSimulator();
                foreach (CombatPredictionSimulator branch in new[] { simulator, fork, recaptured })
                {
                    SimulatedCombatState branchState = (SimulatedCombatState)branch.State.CombatState;
                    int expectedStarts = echo ? 3 + 2 * index : 2;
                    if (branchState.GetCardPlaySeriesStartedThisTurn(player.Creature) != index + 1
                        || branchState.GetZeroCostAttackStartsThisTurn(player.Creature) != expectedStarts
                        || branchState.GetManualCardsPlayedThisTurn(player.Creature) != index + 1)
                        throw new InvalidOperationException("Card history must distinguish repeated plays from card series and manual actions.");
                }
            }
        }
        finally
        {
            foreach (SimulationSnapshot prediction in predictions)
                prediction.ReleaseSimulator();
        }
    }

    private async Task AssertDeathEffectsOnceAsync(CombatState combat, Player player)
    {
        Creature killed = combat.Enemies[0];
        Creature survivor = combat.Enemies[1];
        int reward = survivor.GetPower<RavenousPower>()!.Amount;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        PlanAction action = new(PlanActionKind.PlayCard, player.PlayerCombatState!.TurnNumber,
            CardId: "STRIKE_IRONCLAD", TargetCombatId: killed.CombatId);
        SimulationSnapshot prediction = InvokeForcedTerminalReplay(driver, [action], null, 0, null);
        try
        {
            CombatPredictionSimulator simulator = prediction.Simulator;
            SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
            CorePowerSupport.ApplyEnemyDeathPowers(simulator, shadow, shadow.KnownEnemies, new HashSet<uint>());
            if (shadow.GetAmount<StrengthPower>(survivor) != reward)
                throw new InvalidOperationException($"Repeated death notification granted {shadow.GetAmount<StrengthPower>(survivor)} Strength, expected {reward}.");
            CombatPredictionSimulator fork = simulator.Fork();
            SimulatedCombatState forkState = (SimulatedCombatState)fork.State.CombatState;
            CorePowerSupport.ApplyEnemyDeathPowers(fork, forkState, forkState.KnownEnemies, new HashSet<uint>());
            if (forkState.GetAmount<StrengthPower>(survivor) != reward)
                throw new InvalidOperationException("Fork lost completed death identity.");
            MoveStateSnapshot expected = CaptureSimulated(simulator, shadow, player, survivor);
            if (!FindActualHandCard(player, "STRIKE_IRONCLAD", 0).TryManualPlay(killed))
                throw new InvalidOperationException("Native Strike was not playable.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(expected, CaptureActual(combat, player, survivor), "DeathEffectsOnce", "NativeStrike");
        }
        finally { prediction.ReleaseSimulator(); }
    }

    private async Task AssertFeedThornsTerminalAsync(CombatState combat, Player player)
    {
        Creature enemy = combat.Enemies[0];
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        PlanAction action = new(PlanActionKind.PlayCard, player.PlayerCombatState!.TurnNumber,
            CardId: "FEED", TargetCombatId: enemy.CombatId);
        SimulationSnapshot prediction = InvokeForcedTerminalReplay(driver, [action], null, 0, null);
        MoveStateSnapshot? actual = null;
        void OnEnded(CombatRoom room) => actual = CaptureActual(combat, player, enemy);
        try
        {
            if (!prediction.PlayerDead || prediction.AllEnemiesDead || prediction.PlayerHp <= 0
                || prediction.TerminalStamp is not { Outcome: CombatTerminalOutcome.Defeat })
                throw new InvalidOperationException("Feed after fatal thorns must retain defeat despite positive HP.");
            MoveStateSnapshot expected = CaptureSimulated(prediction.Simulator,
                (SimulatedCombatState)prediction.Simulator.State.CombatState, player, enemy);
            CombatManager.Instance.CombatEnded += OnEnded;
            if (!FindActualHandCard(player, "FEED", 0).TryManualPlay(enemy))
                throw new InvalidOperationException("Native Feed was not playable.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            while (actual == null)
            {
                EnsureWithinDeadline();
                await NextFrameAsync();
            }
            AssertSnapshotEqual(expected, actual, "FeedThornsTerminal", "NativePendingLoss");
            if (CombatManager.Instance.IsInProgress || player.Creature.CurrentHp <= 0)
                throw new InvalidOperationException("Native combat must end even though Feed restored HP.");
        }
        finally
        {
            CombatManager.Instance.CombatEnded -= OnEnded;
            prediction.ReleaseSimulator();
        }
    }

    private async Task AssertSearchWaitsForNativeActionAsync(CombatState combat, Player player)
    {
        NGame host = NGame.Instance!;
        ActionExecutor executor = RunManager.Instance.ActionExecutor;
        bool requested = false;
        bool capturedInsideAction = false;
        void BeforeAction(GameAction action)
        {
            if (requested) return;
            requested = true;
            SolverController.RequestSearch(host, combat, SearchReason.Manual);
            capturedInsideAction = SolverController.HasActiveSearchSessionForTesting;
        }
        executor.BeforeActionExecuted += BeforeAction;
        try
        {
            if (!FindActualHandCard(player, "DEFEND_SILENT", 0).TryManualPlay(null))
                throw new InvalidOperationException("Action barrier fixture could not play Defend.");
            await executor.FinishedExecutingActions();
            if (!requested || capturedInsideAction)
                throw new InvalidOperationException("Search captured a root inside the native action queue.");
            long deadline = System.Environment.TickCount64 + 10_000;
            while (SolverController.LastCompletedResultForTesting == null && SolverController.LastSearchFailureForTesting == null)
            {
                if (System.Environment.TickCount64 >= deadline)
                    throw new TimeoutException("Deferred search did not complete after native action.");
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (SolverController.LastSearchFailureForTesting is { } failure)
                throw new InvalidOperationException("Deferred action-barrier search failed.", failure);
        }
        finally
        {
            executor.BeforeActionExecuted -= BeforeAction;
            SolverController.CancelSearchForTesting();
        }
    }

    private static void AssertSurroundedStateIdentity(CombatState combat)
    {
        ConfigureMonsterMove(combat.Enemies.Single(enemy => enemy.Monster is MegaCrit.Sts2.Core.Models.Monsters.Crusher),
            new UnattendedMonsterMoveCheck { MoveId = "THRASH_MOVE" });
        ConfigureMonsterMove(combat.Enemies.Single(enemy => enemy.Monster is MegaCrit.Sts2.Core.Models.Monsters.Rocket),
            new UnattendedMonsterMoveCheck { MoveId = "CHARGE_UP_MOVE" });
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator left = root.ForkSimulator();
        CombatPredictionSimulator right = root.ForkSimulator();
        SimulatedCombatState leftCombat = (SimulatedCombatState)left.State.CombatState;
        SimulatedCombatState rightCombat = (SimulatedCombatState)right.State.CombatState;
        SurroundedPower leftPower = leftCombat.GetPower<SurroundedPower>(root.PlayerIdentity.Creature)!;
        SurroundedPower rightPower = rightCombat.GetPower<SurroundedPower>(root.PlayerIdentity.Creature)!;
        left.StateStore.Get(leftPower, () => new SurroundedPredictionState(leftPower)).Facing = SurroundedPower.Direction.Left;
        right.StateStore.Get(rightPower, () => new SurroundedPredictionState(rightPower)).Facing = SurroundedPower.Direction.Right;
        StateFingerprintBuilder leftKey = new();
        StateFingerprintBuilder rightKey = new();
        leftCombat.AppendFingerprint(ref leftKey, left);
        rightCombat.AppendFingerprint(ref rightKey, right);
        if (leftKey.Finish() == rightKey.Finish())
            throw new InvalidOperationException("Surrounded facing collides in the branch fingerprint.");
        ContinuationStamp leftStamp = ContinuationStamp.CapturePredicted(root.PlayerIdentity, left, 1, root.Forecast, 1);
        ContinuationStamp rightStamp = ContinuationStamp.CapturePredicted(root.PlayerIdentity, right, 1, root.Forecast, 1);
        if (leftStamp == rightStamp)
            throw new InvalidOperationException("Surrounded facing collides in continuation state.");
        CombatPredictionSimulator fork = left.Fork();
        SimulatedCombatState forkCombat = (SimulatedCombatState)fork.State.CombatState;
        SurroundedPower forkPower = forkCombat.GetPower<SurroundedPower>(root.PlayerIdentity.Creature)!;
        fork.StateStore.Get(forkPower, () => new SurroundedPredictionState(forkPower)).Facing = SurroundedPower.Direction.Right;
        if (left.StateStore.Peek(leftPower, () => new SurroundedPredictionState(leftPower)).Facing != SurroundedPower.Direction.Left)
            throw new InvalidOperationException("Surrounded facing leaked across a fork.");
        int leftDamage = CorePowerSupport.AdjustForecastAttack(left, leftCombat, root.Enemies[0], root.PlayerIdentity.Creature, 10);
        int rightDamage = CorePowerSupport.AdjustForecastAttack(right, rightCombat, root.Enemies[0], root.PlayerIdentity.Creature, 10);
        if (leftDamage != 10 || rightDamage != 15)
            throw new InvalidOperationException($"Surrounded forecast damage {leftDamage}/{rightDamage}, expected 10/15.");
        CombatBeamSolver CreateDriver() => new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        SimulationSnapshot Evaluate(CombatBeamSolver driver, CombatPredictionSimulator source)
            => (SimulationSnapshot)(InvokeForcedTerminalMethod(driver, "Snapshot",
                [source.Fork(), root.StartTurnNumber, 0, 0, SearchBoundaryReason.None, new ForkableSet<uint>()])
                ?? throw new InvalidOperationException("Facing score snapshot was not created."));
        List<SimulationSnapshot> snapshots = [];
        try
        {
            CombatBeamSolver shared = CreateDriver();
            SimulationSnapshot sharedLeft = Evaluate(shared, left);
            snapshots.Add(sharedLeft);
            SimulationSnapshot sharedRight = Evaluate(shared, right);
            snapshots.Add(sharedRight);
            SimulationSnapshot isolatedRight = Evaluate(CreateDriver(), right);
            snapshots.Add(isolatedRight);
            SimulationSnapshot cachedLeft = Evaluate(shared, left);
            snapshots.Add(cachedLeft);
            if (sharedLeft.StateKey == sharedRight.StateKey
                || sharedLeft.ProjectedPlayerHp <= sharedRight.ProjectedPlayerHp
                || sharedLeft.Score <= sharedRight.Score
                || sharedRight.StateKey != isolatedRight.StateKey
                || sharedRight.ProjectedPlayerHp != isolatedRight.ProjectedPlayerHp
                || sharedRight.Score != isolatedRight.Score
                || cachedLeft.StateKey != sharedLeft.StateKey
                || cachedLeft.ProjectedPlayerHp != sharedLeft.ProjectedPlayerHp
                || cachedLeft.Score != sharedLeft.Score)
                throw new InvalidOperationException("Facing-dependent threat scores changed with cache population order.");
        }
        finally
        {
            foreach (SimulationSnapshot snapshot in snapshots)
                snapshot.ReleaseSimulator();
        }
        Entry.Logger.Info("[CombatSolver/Test] SURROUNDED_STATE_IDENTITY_OK fingerprint=true continuation=true fork=true damage=10/15");
    }
}
