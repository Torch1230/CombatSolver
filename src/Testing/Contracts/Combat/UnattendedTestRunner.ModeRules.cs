using CombatSolver.Engine.InCombat.Mirrors;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Dread;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertDreadKnowledgeCurseAsync(CombatState combat, Player player)
    {
        if (combat.RunState.GameMode != GameMode.Dread)
            throw new InvalidOperationException("Dread curse contract requires Dread mode.");
        var enemy = combat.Enemies.Single(creature => creature.Monster is MegaCrit.Sts2.Core.Models.Monsters.KnowledgeDemon);
        for (int counter = 0; counter < 3; counter++)
        {
            SetStage("dread_knowledge_curse_" + counter);
            var monster = ConfigureMonsterMove(enemy, new UnattendedMonsterMoveCheck
                { MoveId = "CURSE_OF_KNOWLEDGE_MOVE" });
            var root = CombatRootSnapshot.Capture(combat);
            var parent = root.ForkSimulator();
            var initial = CaptureSimulated(parent, (SimulatedCombatState)parent.State.CombatState, player, enemy);
            var child = parent.Fork();
            var shadow = (SimulatedCombatState)child.State.CombatState;
            var move = shadow.CurrentMonsterMoves().Single(candidate => candidate.Owner == enemy);
            var request = new KnowledgeDemonChoiceRequest(enemy, counter,
                $"KNOWLEDGE_DEMON:{enemy.CombatId}:{counter}", ["DISINTEGRATION"]);
            var choice = KnowledgeDemonChoiceSupport.BuildChoices(request, SolverDisplayNames.Capture(combat)).Single();
            MonsterMoveSemantics.ApplyForecastMove(child, shadow, move, player.Creature, new HashSet<uint>(), [choice]);
            using (NativeTestCardSelectors.Push(new GeneratedScenarioCardSelector([["DISINTEGRATION"]])))
                await monster.PerformMove();
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(CaptureSimulated(child, shadow, player, enemy), CaptureActual(combat, player, enemy),
                "KnowledgeDemon", "DreadCurse" + counter);
            AssertSnapshotEqual(initial, CaptureSimulated(parent, (SimulatedCombatState)parent.State.CombatState, player, enemy),
                "KnowledgeDemon", "ParentAfterLive");
        }
        _completedChecks.Add("DreadKnowledgeCurse:8:9:10:NativeFullState:ParentAfterLive");
    }

    private async Task AssertDominionHooksAsync(CombatState combat, Player player)
    {
        var run = (RunState)combat.RunState;
        if (run.GameMode != GameMode.Dread) throw new InvalidOperationException("Dominion contract requires Dread.");
        var room = (CombatRoom)run.CurrentRoom!;
        var enemy = combat.Enemies[0];
        var types = typeof(DominionModel).Assembly.GetTypes().Where(type => !type.IsAbstract && typeof(DominionModel).IsAssignableFrom(type)
            && type.Namespace == "MegaCrit.Sts2.Core.Models.Dread.Dominions").OrderBy(type => type.Name).ToArray();
        foreach (Type type in types)
        {
            if (_request.DominionIdsForTest.Length > 0 && !_request.DominionIdsForTest.Contains(ModelDb.GetId(type).Entry)) continue;
            int variants = type.Name == "KnowledgeDemonDominion" ? 2 : 1;
            for (int variant = 0; variant < variants; variant++)
            {
                SetStage("dominion_" + type.Name + "_" + variant);
                await ClearOrderedEffectFixtureAsync(combat, player);
                var canonical = ModelDb.GetById<DominionModel>(ModelDb.GetId(type));
                run.SetDominion(canonical);
                combat.SetDominion(run.Dominion);
                await run.Dominion!.AfterRoomEntered(room);
                await run.Dominion.BeforeCombatStart();
                var root = CombatRootSnapshot.Capture(combat);
                var parent = root.ForkSimulator();
                var child = parent.Fork();
                var sibling = parent.Fork();
                var shadow = (SimulatedCombatState)child.State.CombatState;
                var initial = CaptureSimulated(parent, (SimulatedCombatState)parent.State.CombatState, player, enemy);
                if (root.Rules.GameMode != run.GameMode || root.Rules.AscensionLevel != 10
                    || root.Rules.DominionId != run.Dominion.Id.Entry || !ReferenceEquals(root.Rules, shadow.Rules))
                    throw new InvalidOperationException("Mode rules differ across root and Fork.");
                var names = SolverDisplayNames.Capture(combat);
                string? selectedId = null;
                var cursor = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
                {
                    PlanCardChoice selected = CardChoiceSupport.BuildChoices(request.Spec!, names, 10, 10).ElementAt(variant)
                        with { SourceId = request.SourceId, ContextId = request.ContextId, Timing = request.Timing };
                    selectedId = selected.Cards.Single().CardId;
                    return selected;
                });
                if (!HookMirrors.BeforeHandDraw(child, player, cursor))
                    throw new InvalidOperationException("Dominion choice remained unresolved.");
                using (NativeTestCardSelectors.Push(new GeneratedScenarioCardSelector(selectedId == null ? [] : [[selectedId]])))
                    await Hook.BeforeHandDraw(combat, player, new BlockingPlayerChoiceContext());
                AssertSnapshotEqual(CaptureSimulated(child, shadow, player, enemy), CaptureActual(combat, player, enemy), type.Name, "BeforeHandDraw");
                HookMirrors.AfterCreatureAddedToCombat(child, enemy);
                await Hook.AfterCreatureAddedToCombat(combat, enemy);
                AssertSnapshotEqual(CaptureSimulated(child, shadow, player, enemy), CaptureActual(combat, player, enemy), type.Name, "CreatureAdded");
                AssertSnapshotEqual(initial, CaptureSimulated(parent, (SimulatedCombatState)parent.State.CombatState, player, enemy), type.Name, "ParentAfterLive");
                AssertSnapshotEqual(initial, CaptureSimulated(sibling, (SimulatedCombatState)sibling.State.CombatState, player, enemy), type.Name, "SiblingAfterLive");
                _completedChecks.Add(type.Name + ":NativeBeforeHandDraw:NativeCreatureAdded:Root:Fork:LiveIsolation:choice=" + variant);
            }
        }
        run.SetDominion(null);
        combat.SetDominion(null);
        if (CombatRootSnapshot.Capture(combat).Rules.DominionId != null)
            throw new InvalidOperationException("Removed dominion remained in captured rules.");
    }
}
