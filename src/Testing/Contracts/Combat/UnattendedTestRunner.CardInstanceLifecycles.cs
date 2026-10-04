using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Orbs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertFrozenLightningChannelsAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await InjectCardAsync(combat, player, new() { CardId = "VOLTAIC", Pile = "Hand" });
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        for (int i = 0; i < 3; i++)
        {
            simulator.OrbChannel<LightningOrb>(player, 1);
            await OrbCmd.Channel<LightningOrb>(new BlockingPlayerChoiceContext(), player);
        }
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "VOLTAIC", 0));
        _completedChecks.Add("FrozenLightning:RootBeforeNativeAdvance:ThreeBranchChannels:Voltaic:NativeFullState:Fork");
    }

    private async Task<CombatPredictionSimulator> PlayLifecycleCardAsync(CombatPredictionSimulator simulator, CombatState combat, Player player, CardModel card, int targetIndex = 0)
    {
        string key = CardChoiceSupport.ChoiceCardKey(card);
        var predicted = simulator.State.GetPlayerCombatState(player).Hand.Cards
            .First(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == key);
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var target = card.TargetType == TargetType.AnyEnemy ? combat.Enemies[targetIndex] : null;
        PlaySimulatedCard(simulator, shadow, predicted, target, combat.Enemies);
        if (simulator.HasPendingChoice)
            throw new InvalidOperationException($"Lifecycle fixture requires a choice for {card.Id.Entry}.");
        var expected = CaptureSimulated(simulator, shadow, player, combat.Enemies[0]);
        GameAction native = await SolverController.EnqueueAndCaptureActionAsync(
            candidate => candidate is PlayCardAction play && ReferenceEquals(play.NetCombatCard.ToCardModelOrNull(), card),
            () =>
            {
                if (!card.TryManualPlay(target)) throw new InvalidOperationException($"Native lifecycle card was refused: {card.Id.Entry}.");
            }, CancellationToken.None);
        await native.CompletionTask;
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        var actual = CaptureActual(combat, player, combat.Enemies[0]);
        _completedChecks.Add($"Lifecycle:{card.Id.Entry}:EnemyHp={expected.EnemyHp}/{actual.EnemyHp}:NativePlayPile={string.Join(',', player.PlayerCombatState!.PlayPile.Cards.Select(c => c.Id.Entry))}");
        AssertSnapshotEqual(expected, actual, _request.ScenarioId, card.Id.Entry);
        var fork = simulator.Fork();
        AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, combat.Enemies[0]),
            _request.ScenarioId, "Fork");
        return fork;
    }

    private async Task AssertMakeItSoMusicBoxAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await InjectRelicAsync(player, new() { RelicId = "MUSIC_BOX" });
        await InjectCardAsync(combat, player, new() { CardId = "MAKE_IT_SO", UpgradeLevels = 1, TreatAsDeckCard = true, Pile = "Hand" });
        if (_request.ScenarioId == "MAKE-IT-SO-FULL-HAND")
        {
            await InjectCardAsync(combat, player, new() { CardId = "MASTER_OF_STRATEGY", Pile = "Hand" });
            for (int i = 0; i < 2; i++) await InjectCardAsync(combat, player, new() { CardId = "DEFEND_REGENT", Pile = "Hand" });
            for (int i = 0; i < 6; i++) await InjectCardAsync(combat, player, new() { CardId = "WOUND", Pile = "Hand" });
            for (int i = 0; i < 3; i++) await InjectCardAsync(combat, player, new() { CardId = "WOUND", Pile = "Draw" });
            var full = CombatRootSnapshot.Capture(combat).ForkSimulator();
            var fullOriginal = FindActualHandCard(player, "MAKE_IT_SO", 0);
            full = await PlayLifecycleCardAsync(full, combat, player, fullOriginal);
            var fullClone = FindActualHandCard(player, "MAKE_IT_SO", 0);
            full = await PlayLifecycleCardAsync(full, combat, player, FindActualHandCard(player, "DEFEND_REGENT", 0));
            full.AddToPile(full.State.FindCard(fullOriginal)!, PileType.Hand);
            await CardPileCmd.Add(fullOriginal, PileType.Hand);
            full = await PlayLifecycleCardAsync(full, combat, player, fullClone);
            full = await PlayLifecycleCardAsync(full, combat, player, fullOriginal);
            full = await PlayLifecycleCardAsync(full, combat, player, FindActualHandCard(player, "DEFEND_REGENT", 0));
            full = await PlayLifecycleCardAsync(full, combat, player, FindActualHandCard(player, "MASTER_OF_STRATEGY", 0));
            _completedChecks.Add("MakeItSo:MusicBox:FullHand:OrderedReturns:Native:Fork");
            return;
        }
        for (int i = 0; i < 6; i++) await InjectCardAsync(combat, player, new() { CardId = "DEFEND_REGENT", Pile = "Hand" });
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var original = FindActualHandCard(player, "MAKE_IT_SO", 0);
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, original);
        var clone = FindActualHandCard(player, "MAKE_IT_SO", 0);
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, clone);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            for (int i = 0; i < 3; i++) simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "DEFEND_REGENT", 0));
            if (cycle == 0)
            {
                simulator = await PlayLifecycleCardAsync(simulator, combat, player, original);
                simulator = await PlayLifecycleCardAsync(simulator, combat, player, clone);
            }
        }
        _completedChecks.Add("MakeItSo:MusicBox:OriginalAndClone:TwoSkillCycles:Native:Fork");
    }

    private async Task AssertSecondWindNestedDrawAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await InjectPowerAsync(combat, player, new() { PowerId = "HELLRAISER_POWER", Target = "Player", Amount = 1 });
        await InjectPowerAsync(combat, player, new() { PowerId = "DARK_EMBRACE_POWER", Target = "Player", Amount = 1 });
        if (_request.ScenarioId == "SECOND-WIND-REPORT-ROOT")
        {
            await InjectPowerAsync(combat, player, new() { PowerId = "VICIOUS_POWER", Target = "Player", Amount = 2 });
            await InjectPowerAsync(combat, player, new() { PowerId = "WEAK_POWER", Target = "Player", Amount = 1 });
            foreach (var (id, upgraded, deck) in new[]
                     { ("BATTLE_TRANCE", 1, true), ("FEEL_NO_PAIN", 0, true), ("COLOSSUS", 1, false),
                       ("CINDER", 1, false), ("SECOND_WIND", 1, false) })
                await InjectCardAsync(combat, player, new() { CardId = id, UpgradeLevels = upgraded, TreatAsDeckCard = deck, Pile = "Hand" });
            foreach (var (id, upgraded, deck) in new[]
                     { ("SHRUG_IT_OFF", 1, false), ("POMMEL_STRIKE", 1, true), ("CINDER", 1, false),
                       ("THUNDERCLAP", 0, true), ("EXPECT_A_FIGHT", 1, false), ("STOKE", 1, true),
                       ("PERFECTED_STRIKE", 0, true), ("BLOODLETTING", 1, false), ("DEFEND_IRONCLAD", 0, true) })
                await InjectCardAsync(combat, player, new() { CardId = id, UpgradeLevels = upgraded, TreatAsDeckCard = deck, Pile = "Draw" });
            var pommel = player.PlayerCombatState!.DrawPile.Cards.Single(card => card.Id.Entry == "POMMEL_STRIKE");
            var glam = ModelDb.Enchantment<MegaCrit.Sts2.Core.Models.Enchantments.Glam>().ToMutable();
            // Match the completed replay enchantment at this draw boundary.
            CombatSolver.Engine.Common.PredictionUtils.EnchantCard(glam, pommel, 1);
            ((MegaCrit.Sts2.Core.Models.Enchantments.Glam)pommel.Enchantment!)._usedThisCombat = true;
            pommel.Enchantment._status = MegaCrit.Sts2.Core.Entities.Enchantments.EnchantmentStatus.Disabled;
            await InjectCardAsync(combat, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Discard" });
            int[] hps = [45, 47, 12], maxHps = [52, 50, 48];
            for (int i = 0; i < 3; i++)
            {
                await CreatureCmd.SetMaxHp(combat.Enemies[i], maxHps[i]);
                await CreatureCmd.SetCurrentHp(combat.Enemies[i], hps[i]);
                await InjectPowerAsync(combat, player, new() { PowerId = "REATTACH_POWER", Target = "Enemy", TargetIndex = i, Amount = 25 });
                if (i > 0) await InjectPowerAsync(combat, player, new() { PowerId = "STRENGTH_POWER", Target = "Enemy", TargetIndex = i, Amount = 2 });
            }
            SetEnergy(player, 3);
            var report = CombatRootSnapshot.Capture(combat).ForkSimulator();
            report = await PlayLifecycleCardAsync(report, combat, player, FindActualHandCard(player, "SECOND_WIND", 0));
            report = await PlayLifecycleCardAsync(report, combat, player, FindActualHandCard(player, "THUNDERCLAP", 0));
            _completedChecks.Add("SecondWind:NestedDraw:ThreeEnemies:Reattach:Native:Fork");
            return;
        }
        foreach (string id in new[] { "SECOND_WIND", "SECOND_WIND", "DEFEND_IRONCLAD" })
            await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Hand" });
        if (_request.ScenarioId == "SECOND-WIND-FULL-HAND")
            for (int i = 0; i < 7; i++) await InjectCardAsync(combat, player, new() { CardId = "CINDER", Pile = "Hand" });
        foreach (string id in new[] { "POMMEL_STRIKE", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD" })
            await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Draw" });
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "SECOND_WIND", 0));
        _completedChecks.Add("SecondWind:DuplicateInstances:DarkEmbrace:Hellraiser:NestedDraw:Native:Fork");
    }

    private async Task AssertCalculatedGambleInstancesAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        bool sly = _request.ScenarioId == "CALCULATED-GAMBLE-SLY";
        if (_request.ScenarioId == "CALCULATED-GAMBLE-ORDERED-DISCARD")
        {
            foreach (string id in new[] { "PAELS_LEGION", "POLLINOUS_CORE", "FAKE_HAPPY_FLOWER" })
                await InjectRelicAsync(player, new() { RelicId = id });
            await InjectPowerAsync(combat, player, new() { PowerId = "THORNS_POWER", Target = "Player", Amount = 3 });
            foreach (var (id, upgrade) in new[] { ("DEFEND_SILENT", 0), ("STRIKE_SILENT", 0), ("CALCULATED_GAMBLE", 0),
                         ("SURVIVOR", 0), ("BACKFLIP", 0), ("WELL_LAID_PLANS", 1), ("ASCENDERS_BANE", 0),
                         ("FLICK_FLACK", 0), ("STRIKE_SILENT", 0) })
                await InjectCardAsync(combat, player, new() { CardId = id, UpgradeLevels = upgrade, TreatAsDeckCard = true, Pile = "Hand" });
            foreach (var (id, upgrade) in new[] { ("DEFEND_SILENT", 0), ("STRIKE_SILENT", 0), ("CALCULATED_GAMBLE", 0),
                         ("PREPARED", 1), ("DEFEND_SILENT", 0), ("DODGE_AND_ROLL", 0), ("FOOTWORK", 1),
                         ("NEUTRALIZE", 0), ("ACROBATICS", 0), ("DEFEND_SILENT", 0), ("CLOAK_AND_DAGGER", 1),
                         ("DEFEND_SILENT", 0), ("ANTICIPATE", 0), ("STRIKE_SILENT", 0) })
                await InjectCardAsync(combat, player, new() { CardId = id, UpgradeLevels = upgrade, TreatAsDeckCard = true, Pile = "Draw" });
            SetEnergy(player, 3);
            var ordered = CombatRootSnapshot.Capture(combat).ForkSimulator();
            foreach (string id in new[] { "WELL_LAID_PLANS", "CALCULATED_GAMBLE", "FOOTWORK", "CALCULATED_GAMBLE", "CLOAK_AND_DAGGER" })
                ordered = await PlayLifecycleCardAsync(ordered, combat, player, FindActualHandCard(player, id, 0));
            if (player.PlayerCombatState!.ExhaustPile.Cards.Count(card => card.Id.Entry == "CALCULATED_GAMBLE") != 2)
                throw new InvalidOperationException("Calculated Gamble lifecycle lost an exhausted native instance.");
            _completedChecks.Add("CalculatedGamble:OrderedDiscard:NineCardHand:FourteenCardDraw:NestedSly:Relics:TwoExhaustedInstances:Native:Fork");
            return;
        }
        string[] hand = sly
            ? ["CALCULATED_GAMBLE", "FLICK_FLACK", "DEFEND_SILENT", "DEFEND_SILENT"]
            : ["CALCULATED_GAMBLE", "CALCULATED_GAMBLE", "DEFEND_SILENT", "DEFEND_SILENT"];
        foreach (string id in hand)
            await InjectCardAsync(combat, player, new() { CardId = id, TreatAsDeckCard = true, Pile = "Hand" });
        if (sly)
            foreach (string id in new[] { "DEFEND_SILENT", "CALCULATED_GAMBLE", "STRIKE_SILENT", "DEFEND_SILENT", "STRIKE_SILENT", "DEFEND_SILENT" })
                await InjectCardAsync(combat, player, new() { CardId = id, TreatAsDeckCard = true, Pile = "Draw" });
        var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        for (int i = 0; i < 2; i++)
            simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "CALCULATED_GAMBLE", 0));
        _completedChecks.Add(sly
            ? "CalculatedGamble:NestedSlyDiscard:OuterResultPile:Native:Fork"
            : "CalculatedGamble:DuplicateInstances:DiscardShuffleDraw:DeferredResultPile:Native:Fork");
    }

    private async Task AssertPaelsLegionFinishedReferenceAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await InjectRelicAsync(player, new() { RelicId = "PAELS_LEGION" });
        for (int i = 0; i < 3; i++)
            await InjectCardAsync(combat, player, new() { CardId = "DEFEND_SILENT", Pile = "Hand" });
        var relic = player.Relics.OfType<PaelsLegion>().Single();
        var first = CombatRootSnapshot.Capture(combat).ForkSimulator();
        await PlayLifecycleCardAsync(first, combat, player, FindActualHandCard(player, "DEFEND_SILENT", 0));
        var finished = CombatManager.Instance.History.CardPlaysFinished.Last().CardPlay;
        await relic.AfterModifyingBlockAmount(5, finished.Card, finished);
        relic._cooldown = -1;
        if (!ReferenceEquals(relic._affectedCardPlay, finished))
            throw new InvalidOperationException("Pael fixture failed to preserve a finished native reference.");
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "DEFEND_SILENT", 0));
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "DEFEND_SILENT", 0));
        if (!ReferenceEquals(relic._affectedCardPlay, finished) || relic._cooldown != -1)
            throw new InvalidOperationException("Native finished reference changed its later block behavior.");
        _completedChecks.Add("PaelsLegion:FinishedNativeReference:FrozenRoot:LaterBlock:Cooldown:Native:Fork");
    }

    private async Task AssertEndTurnRiskLossAccountingAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await CreatureCmd.SetMaxHp(player.Creature, 45);
        await CreatureCmd.SetCurrentHp(player.Creature, 38);
        await InjectRelicAsync(player, new() { RelicId = "LIZARD_TAIL" });
        await InjectPowerAsync(combat, player, new() { PowerId = "THE_GAMBIT_POWER", Target = "Player", Amount = 1 });
        int turn = player.PlayerCombatState!.TurnNumber;
        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var next = InvokeForcedTerminalReplay(driver, [new PlanAction(PlanActionKind.EndTurn, turn)], null, 0, null);
        try
        {
            var risk = LiveEndTurnRiskEvaluator.Evaluate(combat, null);
            if (risk.HpLost != next.CumulativePlayerHpLost)
                throw new InvalidOperationException($"End-turn risk and route loss differ: {risk.HpLost}/{next.CumulativePlayerHpLost}; hp={risk.HpBefore}/{risk.HpAfter}.");
            if (risk.PlayerDead || risk.HpAfter != next.ProjectedPlayerHp)
                throw new InvalidOperationException("End-turn risk revival outcome differs from its route.");
            var expected = CaptureSimulated(next.Simulator, (SimulatedCombatState)next.Simulator.State.CombatState, player, combat.Enemies[0]);
            CombatManager.Instance.OnEndedTurnLocally();
            var end = new EndPlayerTurnAction(player, turn);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
            await end.CompletionTask;
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
            {
                EnsureWithinDeadline();
                await NextFrameAsync();
            }
            AssertSnapshotEqual(expected, CaptureActual(combat, player, combat.Enemies[0]), "EndTurnRisk", "RevivalNative");
            _completedChecks.Add("EndTurnRisk:DamageLossAxis:Revival:FinalHp:Native");
        }
        finally { next.ReleaseSimulator(); }
    }

    private async Task AssertConstructOstyRiskAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        await CreatureCmd.Kill(combat.Enemies[2]);
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 45);
        await CreatureCmd.SetCurrentHp(combat.Enemies[1], 65);
        ConfigureMonsterMove(combat.Enemies[0], new() { MoveId = "STRONG_PUNCH_MOVE" });
        ConfigureMonsterMove(combat.Enemies[1], new() { MoveId = "REPEATER_BLAST_MOVE_2" });
        await InjectPowerAsync(combat, player, new() { PowerId = "STRENGTH_POWER", Target = "Enemy", TargetIndex = 1, Amount = 4 });
        var osty = player.Osty ?? throw new InvalidOperationException("Construct risk fixture requires Osty.");
        await CreatureCmd.SetMaxHp(osty, 1);
        await CreatureCmd.SetCurrentHp(osty, 1);
        await InjectPowerAsync(combat, player, new() { PowerId = "DIE_FOR_YOU_POWER", Target = "Osty", Amount = 1 });
        await InjectCardAsync(combat, player, new() { CardId = "BODYGUARD", UpgradeLevels = 1, Pile = "Hand" });
        await SetBlockAsync(player.Creature, 4);
        await CreatureCmd.SetCurrentHp(player.Creature, 40);
        int turn = player.PlayerCombatState!.TurnNumber;
        var root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var simulator = root.ForkSimulator();
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "BODYGUARD", 0));
        var next = InvokeForcedTerminalReplay(driver, [new PlanAction(PlanActionKind.PlayCard, turn, CardId: "BODYGUARD"), new PlanAction(PlanActionKind.EndTurn, turn)], null, 0, null);
        try
        {
            var risk = LiveEndTurnRiskEvaluator.Evaluate(combat, null);
            if (risk.HpLost != next.CumulativePlayerHpLost)
                throw new InvalidOperationException($"Construct end-turn risk differs from route: {risk.HpLost}/{next.CumulativePlayerHpLost}.");
            var expected = CaptureSimulated(next.Simulator, (SimulatedCombatState)next.Simulator.State.CombatState, player, combat.Enemies[0]);
            CombatManager.Instance.OnEndedTurnLocally();
            var end = new EndPlayerTurnAction(player, turn);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
            await end.CompletionTask;
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
            {
                EnsureWithinDeadline();
                await NextFrameAsync();
            }
            AssertSnapshotEqual(expected, CaptureActual(combat, player, combat.Enemies[0]), "ConstructRisk", "NativeOsty");
            _completedChecks.Add("ConstructRisk:OstySummon:TwoAttacks:Native:RouteRiskAgreement");
        }
        finally { next.ReleaseSimulator(); }
    }

    private async Task AssertConstructReaperArtifactAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        int[] hps = [55, 65, 41];
        for (int i = 0; i < 3; i++)
        {
            await CreatureCmd.SetCurrentHp(combat.Enemies[i], hps[i]);
            ConfigureMonsterMove(combat.Enemies[i], new() { MoveId = i == 0 ? "STRONG_PUNCH_MOVE" : "REPEATER_BLAST_MOVE_2" });
            if (i < 2) await InjectPowerAsync(combat, player, new() { PowerId = "ARTIFACT_POWER", Target = "Enemy", TargetIndex = i, Amount = 1 });
            if (i > 0) await InjectPowerAsync(combat, player, new() { PowerId = "STRENGTH_POWER", Target = "Enemy", TargetIndex = i, Amount = 4 });
        }
        await InjectPowerAsync(combat, player, new() { PowerId = "VULNERABLE_POWER", Target = "Enemy", TargetIndex = 2, Amount = 1 });
        foreach (var (id, amount) in new[] { ("STRENGTH_POWER", 3), ("REAPER_FORM_POWER", 1), ("FRAIL_POWER", 1) })
            await InjectPowerAsync(combat, player, new() { PowerId = id, Target = "Player", Amount = amount });
        var osty = player.Osty ?? throw new InvalidOperationException("Reaper artifact fixture requires Osty.");
        await CreatureCmd.SetMaxHp(osty, 1);
        await CreatureCmd.SetCurrentHp(osty, 1);
        await InjectPowerAsync(combat, player, new() { PowerId = "DIE_FOR_YOU_POWER", Target = "Osty", Amount = 1 });
        await InjectRelicAsync(player, new() { RelicId = "PEN_NIB" });
        player.Relics.OfType<PenNib>().Single()._attacksPlayed = 8;
        await InjectRelicAsync(player, new() { RelicId = "ORNAMENTAL_FAN" });
        foreach (string id in new[] { "MISERY", "TIMES_UP", "BODYGUARD", "WISP", "FETCH" })
            await InjectCardAsync(combat, player, new() { CardId = id, UpgradeLevels = 1, TreatAsDeckCard = true, Pile = "Hand" });
        await InjectCardAsync(combat, player, new() { CardId = "PECK", UpgradeLevels = 1, TreatAsDeckCard = true, Pile = "Draw" });
        SetEnergy(player, 3);
        await CreatureCmd.SetCurrentHp(player.Creature, 19);
        var root = CombatRootSnapshot.Capture(combat);
        int turn = player.PlayerCombatState!.TurnNumber;
        Creature punch = combat.Enemies[0], cubex = combat.Enemies[2];
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var simulator = root.ForkSimulator();
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "FETCH", 0));
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "MISERY", 0), 2);
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "TIMES_UP", 0));
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "BODYGUARD", 0));
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "WISP", 0));
        simulator = await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, "PECK", 0), 2);
        PlanAction Attack(string id, Creature target) => new(PlanActionKind.PlayCard, turn, CardId: id, TargetCombatId: target.CombatId);
        PlanAction Self(string id) => new(PlanActionKind.PlayCard, turn, CardId: id);
        var next = InvokeForcedTerminalReplay(driver,
            [Attack("FETCH", punch), Attack("MISERY", cubex), Attack("TIMES_UP", punch), Self("BODYGUARD"), Self("WISP"), Attack("PECK", cubex), new(PlanActionKind.EndTurn, turn)],
            null, 0, null);
        try
        {
            var risk = LiveEndTurnRiskEvaluator.Evaluate(combat, null);
            if (risk.HpLost != 13 || next.CumulativePlayerHpLost != risk.HpLost || risk.HpAfter != 6)
                throw new InvalidOperationException($"Reaper route risk differs: route={next.CumulativePlayerHpLost}, risk={risk.HpLost}, hp={risk.HpAfter}.");
            var expected = CaptureSimulated(next.Simulator, (SimulatedCombatState)next.Simulator.State.CombatState, player, punch);
            await AdvanceMercuryActualTurnAsync(combat, player, expectVictory: false);
            AssertSnapshotEqual(expected, CaptureActual(combat, player, punch), "ReaperForm", "RouteRiskNative");
        }
        finally { next.ReleaseSimulator(); }
        _completedChecks.Add("ReaperForm:Osty:Artifact:PenNib:MultiTargetDoom:TimesUp:RouteRisk13:NativeTurn:Fork");
    }

    private async Task AssertGroupDebuffReactiveDrawAsync(CombatState combat, Player player)
    {
        foreach (string cardId in new[] { "THUNDERCLAP", "HIGH_FIVE", "SHOCKWAVE", "METEOR_SHOWER" })
        {
            await ClearOrderedEffectFixtureAsync(combat, player);
            foreach (var enemy in combat.Enemies)
            {
                await CreatureCmd.SetMaxHp(enemy, 500);
                await CreatureCmd.SetCurrentHp(enemy, 500);
            }
            SetStars(player, 10);
            await InjectPowerAsync(combat, player, new() { PowerId = "VICIOUS_POWER", Target = "Player", Amount = 2 });
            await InjectPowerAsync(combat, player, new() { PowerId = "HELLRAISER_POWER", Target = "Player", Amount = 1 });
            await InjectCardAsync(combat, player, new() { CardId = cardId, Pile = "Hand" });
            foreach (string id in new[] { "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "WOUND", "WOUND" })
                await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Draw" });
            var simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
            await PlayLifecycleCardAsync(simulator, combat, player, FindActualHandCard(player, cardId, 0));
        }
        _completedChecks.Add("GroupDebuff:FourSources:Vicious:Hellraiser:PerTargetEffects:Native:Fork");
    }
}
