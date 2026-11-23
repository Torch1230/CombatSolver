using System.Reflection;
using System.Text.Json.Nodes;
using CombatSolver.Beam;
using CombatSolver.Beam.Policy;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.MonsterMoves.Intents;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.ValueProps;

namespace Sts2Sim.PreCombatBridge.Tests;

public sealed partial class ForecastSearchAndOpeningTests
{
    [Fact]
    public async Task ThreatProjection_SandpitOneStackWithAliveOwner_AndZeroAttackIntents_ForcesDeathOnly()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(actIndex: 1);
        Assert.True(EncounterCatalog.TryGet("THE_INSATIABLE_BOSS", out EncounterCatalogEntry entry));

        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Boss, run.Map.BossMapPoint.coord.row + 1, run.Map.BossMapPoint.coord.col, MapPointType.Boss, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        Creature playerCreature = combat.Players[0].Creature;
        Creature enemy = combat.Enemies.First(e => e.IsAlive);

        // TheInsatiable starts combat with LIQUIFY_GROUND_MOVE (zero attack intents); add Sandpit amount=1 targeting player.
        Assert.True(enemy.Monster!.NextMove?.Intents.All(i => i is not AttackIntent));
        SandpitPower sandpit = (await PowerCmd.Apply<SandpitPower>(combat, enemy, 1m, enemy, null))!;
        sandpit.Target = playerCreature;

        var evaluator = new StateEvaluator(combat, CombatPolicy.Default);
        StateEvaluator.ThreatProjection projection = evaluator.ProjectHpAfterThreat(combat);
        Assert.Equal(0, projection.Hp);

        SearchNode rootNode = SearchNode.Create(combat.Clone(), null, null, evaluator);
        Assert.Equal(0, evaluator.ComputeProjectedPlayerHp(rootNode, ForecastSearchBoundary.None));

        // Only Victory or non-choice terminal boundaries (UnsupportedEffect / EventDefeat) suppress projection;
        // PendingChoice keeps live pre-choice lethal threat intact.
        Assert.Equal(0, evaluator.ComputeProjectedPlayerHp(rootNode, ForecastSearchBoundary.PendingChoice));
        Assert.Equal(playerCreature.CurrentHp, evaluator.ComputeProjectedPlayerHp(rootNode, ForecastSearchBoundary.UnsupportedEffect));
    }

    [Fact]
    public async Task ThreatProjection_FairyAndLizardTailSaveFromLethal_AndGambitKillsAfterDamage()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun();
        var potion = (PotionModel)ModelDb.Potion<FairyInABottle>().MutableClone();
        potion.AssignOwner(run.Players[0]);
        run.Players[0].AddPotionInternal(potion);
        var lizardTail = (RelicModel)ModelDb.Relic<LizardTail>().MutableClone();
        lizardTail.AssignOwner(run.Players[0]);
        run.Players[0].AddRelicInternal(lizardTail);

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry);
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        Creature playerCreature = combat.Players[0].Creature;
        playerCreature.SetCurrentHpInternal(5);
        Creature enemy = combat.Enemies.First(e => e.IsAlive);

        typeof(MonsterModel).GetProperty("NextMove", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(enemy.Monster!, new MoveState("BIG_HIT", _ => Task.CompletedTask, new SingleAttackIntent(40)));
        foreach (Creature other in combat.Enemies.Where(e => !ReferenceEquals(e, enemy)))
            await CreatureCmd.Kill(other);

        var evaluator = new StateEvaluator(combat, CombatPolicy.Default);
        StateEvaluator.ThreatProjection savedProjection = evaluator.ProjectHpAfterThreat(combat);
        Assert.True(savedProjection.Hp > 0);
        Assert.Equal(1, savedProjection.DeathSaveUseCount);
    }

    [Fact]
    public async Task ThreatProjection_RedirectionUnsupported_ThrowsProjectionUnsupported()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        BridgeRequestDto req = TestSaveBuilder.CreateRequest(
            run,
            "NIBBITS_WEAK",
            target.coord,
            playerCurrentHpOverride: 5,
            mutateSave: root =>
            {
                ((JsonArray)root["players"]![0]!["relics"]!).Add(new JsonObject
                {
                    ["id"] = "RELIC.WUPPY",
                    ["props"] = new JsonObject
                    {
                        ["ints"] = new JsonArray
                        {
                            new JsonObject { ["name"] = "CurrentHp", ["value"] = 10 },
                            new JsonObject { ["name"] = "MaxHp", ["value"] = 10 },
                        },
                    },
                });
            });

        (BridgeResponseDto dto, _) = await TestSaveBuilder.RunAsync(req);
        // Wuppy grants Loyalty/PreBlock redirection; if triggered during projection, maps cleanly to CapabilityMiss("projection_unsupported").
        if (dto.Outcome == "CapabilityMiss")
            Assert.Equal("projection_unsupported", dto.FailureCode);
        else
            Assert.Equal("Succeeded", dto.Outcome);
    }

    [Theory]
    [InlineData(5, 1, 0, false, new[] { 8, 8 }, 0)]
    [InlineData(5, 2, 0, false, new[] { 1, 8, 8 }, 0)]
    [InlineData(5, 1, 1, false, new[] { 8, 8 }, 4)]
    [InlineData(25, 0, 0, true, new[] { 15, 15 }, 5)]
    [InlineData(5, 0, 0, false, new[] { 999_999_999, 999_999_999, 999_999_999 }, 0)]
    [InlineData(10, 0, 0, true, new[] { 18, 15 }, 5, true)]
    [InlineData(1, 1, 0, false, new[] { 8, 8 }, 19, true)]
    [InlineData(10, 0, 0, true, new[] { 21, 15 }, 5, true, 3)]
    [InlineData(10, 0, 0, false, new[] { 18, 3 }, 0, true, 0, true)]
    [InlineData(10, 0, 0, false, new[] { 2, 3 }, 17, true, 0, true)]
    public async Task ThreatProjection_MultiHitSlipperyBufferAndBeatingRemnant_MatchesNativePostDamageTiming(
        int initialHp,
        int slipperyStacks,
        int bufferStacks,
        bool addBeatingRemnant,
        int[] incomingHits,
        int expectedProjectedHp,
        bool hasLizardTail = false,
        int ostyHp = 0,
        bool hasGambit = false)
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun();
        LizardTail? tail = null;
        if (hasLizardTail)
        {
            tail = (LizardTail)ModelDb.Relic<LizardTail>().MutableClone();
            tail.AssignOwner(run.Players[0]);
            run.Players[0].AddRelicInternal(tail);
        }
        if (addBeatingRemnant)
        {
            var remnant = (RelicModel)ModelDb.Relic<BeatingRemnant>().MutableClone();
            remnant.AssignOwner(run.Players[0]);
            run.Players[0].AddRelicInternal(remnant);
        }

        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        Assert.True(EncounterCatalog.TryGet("NIBBITS_WEAK", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Monster, target.coord.row + 1, target.coord.col, MapPointType.Monster, false),
            new ForecastOpeningRunner(CancellationToken.None),
            null);

        CombatState combat = entered.Room.Engine.State;
        Creature playerCreature = combat.Players[0].Creature;
        await CreatureCmd.SetMaxHp(playerCreature, 40);
        playerCreature.SetCurrentHpInternal(initialHp);
        if (ostyHp > 0)
            await OstyCmd.Summon(combat.Players[0], ostyHp, null);
        if (hasGambit)
            await PowerCmd.Apply<TheGambitPower>(combat, playerCreature, 1m, playerCreature, null);

        if (slipperyStacks > 0)
            await PowerCmd.Apply<SlipperyPower>(combat, playerCreature, slipperyStacks, playerCreature, null);
        if (bufferStacks > 0)
            await PowerCmd.Apply<BufferPower>(combat, playerCreature, bufferStacks, playerCreature, null);

        Creature enemy = combat.Enemies.First(e => e.IsAlive);
        foreach (Creature other in combat.Enemies.Where(e => !ReferenceEquals(e, enemy)))
            await CreatureCmd.Kill(other);

        AbstractIntent[] intents = incomingHits.Select(dmg => (AbstractIntent)new SingleAttackIntent(dmg)).ToArray();
        typeof(MonsterModel).GetProperty("NextMove", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(enemy.Monster!, new MoveState("TEST_MULTI_HIT", _ => Task.CompletedTask, intents));

        var evaluator = new StateEvaluator(combat, CombatPolicy.Default);
        StateEvaluator.ThreatProjection projection = evaluator.ProjectHpAfterThreat(combat);
        Assert.Equal(initialHp, playerCreature.CurrentHp);
        Assert.Equal(ostyHp, combat.Players[0].Osty?.CurrentHp ?? 0);
        if (tail is not null) Assert.False(tail.IsUsedUp);

        foreach (int incoming in incomingHits)
            await CreatureCmd.Damage(combat, [playerCreature], incoming, ValueProp.Move, enemy, null, null);

        Assert.Equal(expectedProjectedHp, playerCreature.CurrentHp);
        Assert.Equal(playerCreature.CurrentHp, projection.Hp);
        Assert.Equal(tail?.IsUsedUp == true ? 1 : 0, projection.DeathSaveUseCount);
        Assert.Equal(tail?.IsUsedUp == true ? 20 : 0, projection.DeathSaveHpRestored);
    }

    [Fact]
    public async Task ThreatProjection_UnpoweredMiasmaBypassesOstyAndGambit()
    {
        TestSaveBuilder.EnsurePrewarmed();
        RunState run = TestSaveBuilder.CreateRun(actIndex: 1, gameMode: GameMode.Dread);
        Assert.True(EncounterCatalog.TryGet("THE_INSATIABLE_BOSS", out EncounterCatalogEntry entry));
        ForecastRoomEntryResult entered = await run.EnterForecastCombatAsync(
            new ForecastTargetSpec(entry.Definition, RoomType.Boss, run.Map.BossMapPoint.coord.row + 1, run.Map.BossMapPoint.coord.col, MapPointType.Boss, false),
            new ForecastOpeningRunner(CancellationToken.None), null);
        CombatState combat = entered.Room.Engine.State;
        var player = combat.Players[0];
        Assert.All(combat.Enemies, enemy => Assert.Empty(enemy.Monster!.NextMove!.Intents.OfType<AttackIntent>()));
        player.Creature.SetCurrentHpInternal(10);
        await OstyCmd.Summon(player, 3, null);
        await PowerCmd.Apply<TheGambitPower>(combat, player.Creature, 1, player.Creature, null);
        var miasma = (await PowerCmd.Apply<InsatiableDominionPower>(combat, player.Creature, 1, null, null))!;

        var projected = new StateEvaluator(combat, CombatPolicy.Default).ProjectHpAfterThreat(combat);
        int expected = 10 - miasma.Damage;
        await miasma.AfterSideTurnEndLate(CombatSide.Player, [player.Creature]);
        Assert.Equal(expected, player.Creature.CurrentHp);
        Assert.Equal(3, player.Osty!.CurrentHp);
        Assert.NotNull(player.Creature.GetPower<TheGambitPower>());
        Assert.Equal(player.Creature.CurrentHp, projected.Hp);
    }
}
