using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCrossCharacterArrowAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        if (player.Character.BaseArrowSlotCount != 0 || player.PlayerCombatState!.Arrows.Count != 0)
            throw new InvalidOperationException("Cross-character fixture requires an empty native arrow inventory.");
        var enemy = combat.Enemies.Single();
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var initial = CaptureActual(combat, player, enemy);
        var cards = await InjectCardAsync(combat, player, new() { CardId = "TRICK_SHOT", Pile = "Draw" });
        child.AddToPile(new CombatSolver.Engine.Common.PredictedCard(cards.Single()), PileType.Draw);
        var bulk = parent.Fork();
        bulk.AddToPile(new[] { new CombatSolver.Engine.Common.PredictedCard(cards.Single()) }, PileType.Draw);
        AssertSnapshotEqual(CaptureActual(combat, player, enemy),
            CaptureSimulated(bulk, (SimulatedCombatState)bulk.State.CombatState, player, enemy),
            _request.ScenarioId, "BulkShootCardArrow");
        AssertSnapshotEqual(CaptureActual(combat, player, enemy),
            CaptureSimulated(child, (SimulatedCombatState)child.State.CombatState, player, enemy),
            _request.ScenarioId, "FirstShootCardArrow");
        var grandchild = child.Fork();
        child.AddToPile(child.State.GetPlayerCombatState(player).DrawPile.Cards.Single(), PileType.Hand);
        await CardPileCmd.Add(cards.Single(), PileType.Hand);
        if (child.State.GetPlayerCombatState(player).Arrows.Count != 1 || player.PlayerCombatState.Arrows.Count != 1)
            throw new InvalidOperationException("Moving the shoot card changed the arrow slot count.");
        if (ReferenceEquals(child.State.GetPlayerCombatState(player).Arrows[0], grandchild.State.GetPlayerCombatState(player).Arrows[0]))
            throw new InvalidOperationException("Cross-character arrow slot is shared by child branches.");
        AssertSnapshotEqual(initial, CaptureSimulated(parent, (SimulatedCombatState)parent.State.CombatState, player, enemy),
            _request.ScenarioId, "ParentAfterLiveAdvance");
        AssertSnapshotEqual(initial, CaptureSimulated(sibling, (SimulatedCombatState)sibling.State.CombatState, player, enemy),
            _request.ScenarioId, "SiblingAfterLiveAdvance");
        _completedChecks.Add("CrossCharacterArrow:FirstShootEntry:SingleSlot:NativeState:Fork:LiveAdvance");
    }

    private async Task AssertFoolBranchStateAsync(CombatState combat, Player player)
    {
        SetStage("fool_branch_setup");
        await ClearOrderedEffectFixtureAsync(combat, player);
        var enemy = combat.Enemies.Single();
        await CreatureCmd.SetMaxHp(enemy, 999);
        await CreatureCmd.SetCurrentHp(enemy, 999);
        SetEnergy(player, 99);
        foreach (string id in new[] { "QUIVER", "FOOLISH_SHOT", "FOOLISH_SHOT", "WAH_CHO_KI", "BOUNCE",
                     "STRIKE_FOOL", "STRIKE_FOOL", "STRIKE_FOOL" })
            await InjectCardAsync(combat, player, new() { CardId = id, Pile = "Hand" });
        await InjectCardAsync(combat, player, new() { CardId = "DEFEND_FOOL", Pile = "Draw", Count = 5 });
        await InjectPowerAsync(combat, player, new() { PowerId = "ARCHERY_POWER", Target = "Player", Amount = 3 });
        await InjectPowerAsync(combat, player, new() { PowerId = "HIDDEN_ROOTS_POWER", Target = "Player", Amount = 4 });
        await InjectPowerAsync(combat, player, new() { PowerId = "YAH_POWER", Target = "Player", Amount = 1 });
        await InjectPowerAsync(combat, player, new() { PowerId = "LULLABY_POWER", Target = "Player", Amount = 3 });
        foreach (string id in new[] { "QUIVER", "FOOLISH_SHOT", "WAH_CHO_KI", "BOUNCE" })
        {
            SetStage("fool_native_" + id);
            await PlayNativeCardAsync(FindActualHandCard(player, id, 0), id == "FOOLISH_SHOT" || id == "WAH_CHO_KI" ? enemy : null);
        }
        SetStage("fool_branch_capture");
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var sibling = parent.Fork();
        var initial = CaptureActual(combat, player, enemy);
        var shadow = (SimulatedCombatState)child.State.CombatState;
        foreach (var branch in new[] { parent, child, sibling })
        {
            var state = (SimulatedCombatState)branch.State.CombatState;
            if (state.MelodyCount(player) != 3) throw new InvalidOperationException("Multi-tag melody root was not captured.");
            var power = branch.ImpaledBy(enemy, player) ?? throw new InvalidOperationException("Root is missing its impaled arrow.");
            if (power.Arrows.Any(arrow => !branch.State.GetPlayerCombatState(player).Arrows.Contains(arrow)))
                throw new InvalidOperationException("Impaled arrow points outside its branch slots.");
        }
        if (ReferenceEquals(parent.State.GetPlayerCombatState(player).Arrows[0], child.State.GetPlayerCombatState(player).Arrows[0]))
            throw new InvalidOperationException("Arrow slots are shared across mutable branches.");
        var strikes = player.PlayerCombatState!.Hand.Cards.Where(card => card.Id.Entry == "STRIKE_FOOL").ToArray();
        SetStage("fool_branch_live_advance");
        await PlayNativeCardAsync(strikes[0], enemy);
        foreach (var card in strikes) PlaySimulatedCard(child, shadow, child.State.FindCard(card)!, enemy, combat.Enemies);
        AssertSnapshotEqual(initial, CaptureSimulated(parent, (SimulatedCombatState)parent.State.CombatState, player, enemy),
            _request.ScenarioId, "ArrowParentAfterLiveAdvance");
        AssertSnapshotEqual(initial, CaptureSimulated(sibling, (SimulatedCombatState)sibling.State.CombatState, player, enemy),
            _request.ScenarioId, "ArrowSiblingIsolation");
        foreach (var card in strikes.Skip(1)) await PlayNativeCardAsync(card, enemy);
        AssertSnapshotEqual(CaptureSimulated(child, shadow, player, enemy), CaptureActual(combat, player, enemy),
            _request.ScenarioId, "NativeRetrieval");
        _ = child.Fork();
        _completedChecks.Add("FoolBranchState:ArrowIdentity:ImpaledReferences:LiveAdvance:ParentSibling:MultiTag:Retrieval:YahLullabyFlags:FullContinuation");
        if (_request.ScenarioId == "FOOL-BRANCH-STATE") return;
        for (int cycle = 1; cycle <= 2; cycle++)
        {
            SetStage("fool_simulated_turn_end_" + cycle);
            int ethereal = shadow.CountEtherealCardsInHand(child, player);
            if (!PlayerTurnEndLifecycle.RunPhaseOne(child, shadow, player, [player.Creature]))
                throw new InvalidOperationException("Fool turn end reached a choice.");
            shadow.NormalizeAeonglassWithers(child);
            shadow.NormalizeCardAfflictions(child);
            CorePowerSupport.FlushPlayerHandAtTurnEnd(child, shadow, player);
            if (!PlayerTurnEndLifecycle.RunPhaseTwo(child, shadow, [player.Creature], ethereal))
                throw new InvalidOperationException("Fool late turn end reached a choice.");
            SetStage("fool_native_turn_end_" + cycle);
            await CombatManager.Instance.EndPlayerTurnPhaseOneInternal();
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            await CombatManager.Instance.EndPlayerTurnPhaseTwoInternal();
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(CaptureSimulated(child, shadow, player, enemy), CaptureActual(combat, player, enemy),
                _request.ScenarioId, "TurnEnd" + cycle);
            SetStage("fool_simulated_setup_" + cycle);
            TriggerSimulatedPlayerSetup(child, shadow, player, []);
            SetStage("fool_native_setup_" + cycle);
            await TriggerActualPlayerSetupAsync(combat, player, []);
            AssertSnapshotEqual(CaptureSimulated(child, shadow, player, enemy), CaptureActual(combat, player, enemy),
                _request.ScenarioId, "PlayerSetup" + cycle);
            _ = child.Fork();
        }
        if (shadow.MelodyCount(player) != 0 || ((SimulatedCombatState)parent.State.CombatState).MelodyCount(player) != 3)
            throw new InvalidOperationException("Melody reset changed a prior branch.");
        _completedChecks.Add("FoolBranchState:ArrowIdentity:ImpaledReferences:LiveAdvance:ParentSibling:MultiTag:Retrieval:TwoTurns:FullContinuation");
    }
}
