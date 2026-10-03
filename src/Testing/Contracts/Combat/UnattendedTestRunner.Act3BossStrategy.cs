using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Commands;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task DescribeAct3OpeningEffectsAsync(CombatState combat, Player player)
    {
        var root = CombatRootSnapshot.Capture(combat);
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        ContinuationStamp before = ContinuationStamp.CaptureLive(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            MaxDegreeOfParallelism = 1,
        };
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(
            Math.Max(1, _request.TimeoutSeconds - _stopwatch.Elapsed.TotalSeconds)));
        _completedChecks.AddRange(await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
            cancellation.Token, searchProfile: policy.Profile).DescribeOpeningActionEffectsForTesting()));
        if (ContinuationStamp.CaptureLive(combat) != before)
            throw new InvalidOperationException("Opening effect diagnostics changed the live combat.");
    }

    private async Task AssertAct3BossStrategyAsync(CombatState combat, Player player)
    {
        foreach (string id in new[] { "TEST_SUBJECT_BOSS", "AEONGLASS_BOSS", "QUEEN_BOSS" })
            if (!SearchPolicySnapshot.IsAct3BossEncounter(2, id) || SearchPolicySnapshot.IsAct3BossEncounter(1, id))
                throw new InvalidOperationException("Act 3 boss scope mismatch.");
        if (SearchPolicySnapshot.IsAct3BossEncounter(2, "FUZZY_WURM_CRAWLER_WEAK"))
            throw new InvalidOperationException("Ordinary encounters must keep their existing search.");
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "BURNING_PACT", "FINESSE", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "SHIV", Pile = "Draw", Count = 6 });
        SetEnergy(player, 1);
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 12);
        var root = CombatRootSnapshot.Capture(combat);
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            Act3BossStrategy = true, FixedBudget = true, VerifyIncrementalSearch = true,
            StopAtAcceptableBattleHpLoss = true, AcceptableBattleHpLoss = 0,
            BudgetOverrideMilliseconds = 3000, MaxDegreeOfParallelism = 1, PotionPolicy = SolverPotionPolicy.Disabled,
            Profile = new SolverSearchProfile(60, 256, 32, 18, 24, 3000),
        };
        var result = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
            searchProfile: policy.Profile).Solve());
        if (!result.Snapshot.AllEnemiesDead || result.ProjectedBattleHpLost != 0
            || !result.BestNode.Actions.Any(action => action.CardId == "FINESSE")
            || !result.BestNode.Actions.Any(action => action.CardId == "BURNING_PACT")
            || result.BestNode.Actions.Where(action => action.CardId == "BURNING_PACT")
                .SelectMany(action => action.GetActionChoicesInExecutionOrder())
                .Where(choice => choice.Effect == PlanChoiceEffect.Exhaust)
                .SelectMany(choice => choice.Cards).Any(card => card.CardId == "FINESSE"))
            throw new InvalidOperationException("The actual search must keep and play the draw engine to win without damage.");

        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "NO_DRAW_POWER", Target = "Player", Amount = 1 });
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 6);
        root = CombatRootSnapshot.Capture(combat); names = SolverDisplayNames.Capture(combat);
        result = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
            searchProfile: policy.Profile).Solve());
        if (!result.Snapshot.AllEnemiesDead || result.ProjectedBattleHpLost != 0
            || result.BestNode.Actions.Any(action => action.CardId == "BURNING_PACT"))
            throw new InvalidOperationException("Blocked draw must not displace the immediate lethal attack.");
        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);


        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "WRAITH_FORM", "STRIKE_IRONCLAD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand", UpgradeLevels = id == "STRIKE_IRONCLAD" ? 1 : 0 });
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 9);
        SetEnergy(player, 3);
        root = CombatRootSnapshot.Capture(combat); names = SolverDisplayNames.Capture(combat); damage = BattleDamageTracker.Observe(combat);
        result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null));
        if (!result.Snapshot.AllEnemiesDead || result.ProjectedBattleHpLost != 0
            || result.BestNode.Actions.Any(a => a.CardId == "WRAITH_FORM")
            || result.ExpandedNodes > policy.Profile.MaxExpandedNodes)
            throw new InvalidOperationException("An unnecessary costly power must lose to the ordinary zero-loss kill.");
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "WRAITH_FORM" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Draw", UpgradeLevels = 1 });
        await CreatureCmd.SetCurrentHp(player.Creature, 1);
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 10);
        SetEnergy(player, 1);
        root = CombatRootSnapshot.Capture(combat); names = SolverDisplayNames.Capture(combat); damage = BattleDamageTracker.Observe(combat);
        result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null));
        if (!result.Snapshot.AllEnemiesDead || result.Snapshot.PlayerHp != 1
            || result.BestNode.Actions.First().CardId != "DEFEND_IRONCLAD")
            throw new InvalidOperationException("The actual search must defend before taking the next-turn lethal draw.");
        await AssertAct3BossInteractionsAsync(combat, player, policy);
        _completedChecks.Add("Act3Strategy:Scope:ExhaustKeepsDrawEngine:NoDrawLethal:UpgradedStrikeKill:EssentialDefend:IncrementalReplay:BossInteractions");
    }

    private async Task AssertStrategicContextDemandAsync(CombatState combat, Player player)
    {
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, false, null);
        async Task<StrategicEffectVector> Capture(bool specialized)
        {
            var snapshot = CombatRootSnapshot.Capture(combat);
            var displayNames = SolverDisplayNames.Capture(combat);
            var battleDamage = BattleDamageTracker.Observe(combat);
            return await Task.Run(() => new CombatBeamSolver(snapshot, displayNames, battleDamage,
                policy with { Act3BossStrategy = specialized }, searchProfile: policy.Profile)
                .CaptureStrategicEffectsForTesting());
        }
        foreach (var power in player.Creature.Powers.ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        SetEnergy(player, 3);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = "LETHALITY_POWER", Target = "Player", Amount = 75 });
        if ((await Capture(true)).DamagePotential != 0)
            throw new InvalidOperationException("Lethality requires an attack to realize its multiplier.");
        await InjectCardAsync(combat, player, new UnattendedCardInjection
            { CardId = "ERADICATE", Pile = "Draw", Count = 1 });
        if ((await Capture(true)).DamagePotential <= (await Capture(false)).DamagePotential)
            throw new InvalidOperationException("Native first-attack evaluation lost payable Eradicate hits.");
        foreach (var power in player.Creature.Powers.ToArray())
            await PowerCmd.Remove(power);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = "PLATING_POWER", Target = "Player", Amount = 1 });
        // Registration lasts for this disposable unattended process. An external evaluator
        // may consume an existing context field without a new requirement flag.
        StrategicEffectMirrors.Register<MegaCrit.Sts2.Core.Models.Powers.PlatingPower>(
            StrategicEffectRequirements.None,
            (_, context) => new StrategicEffectVector(context.FirstAttackDamage, 0, 0, 0, 0));
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        foreach (bool specialized in new[] { false, true })
        {
            StrategicEffectVector value = await Task.Run(() => new CombatBeamSolver(
                root, names, damage, policy with { Act3BossStrategy = specialized },
                searchProfile: policy.Profile).CaptureStrategicEffectsForTesting());
            if (specialized ? value.DamagePotential <= 0 : value.DamagePotential != 0)
                throw new InvalidOperationException("Registered evaluation lost the original first-attack context.");
        }
        _completedChecks.Add("StrategicContextDemand:NativeLethality:Eradicate:OrdinaryPolicy:RegisteredField");
    }

    private async Task AssertAct3BossInteractionsAsync(CombatState combat, Player player, SearchPolicySnapshot policy)
    {
        async Task<StrategicEffectVector> Capture(bool specialized)
        {
            var root = CombatRootSnapshot.Capture(combat);
            var names = SolverDisplayNames.Capture(combat);
            var damage = BattleDamageTracker.Observe(combat);
            var frozen = policy with { Act3BossStrategy = specialized };
            return await Task.Run(() => new CombatBeamSolver(root, names, damage, frozen,
                searchProfile: frozen.Profile).CaptureStrategicEffectsForTesting());
        }

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        SetEnergy(player, 3);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Draw", Count = 6 });
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "PAGESTORM_POWER", Target = "Player", Amount = 1 });
        var noSource = await Capture(true);
        var ordinary = await Capture(false);
        if (noSource.CardAccessPotential != 0 || ordinary.CardAccessPotential != 0)
            throw new InvalidOperationException("Pagestorm needs an actual future ethereal draw source.");
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DAZED", Pile = "Draw", Count = 2 });
        var source = await Capture(true);
        if (source.CardAccessPotential <= 0 || (await Capture(false)) != ordinary)
            throw new InvalidOperationException("The boss interaction must recognize ethereal draw while preserving the ordinary power value.");
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Draw", Count = 6 });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DAZED", Pile = "Hand", Count = 2 });
        if ((await Capture(true)).CardAccessPotential != 0)
            throw new InvalidOperationException("Already-drawn ethereal cards must not create a future draw trigger.");

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand", Count = 3 });
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "DANSE_MACABRE_POWER", Target = "Player", Amount = 6 });
        if ((await Capture(true)).PreventionPotential != 0)
            throw new InvalidOperationException("One-energy cards must not activate the high-energy block interaction.");
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BASH", Pile = "Hand" });
        if ((await Capture(true)).PreventionPotential <= 0 || (await Capture(false)).PreventionPotential != 0)
            throw new InvalidOperationException("The boss interaction must recognize an affordable two-energy play without changing ordinary evaluation.");
        SetEnergy(player, 1);
        if ((await Capture(true)).PreventionPotential != 0)
            throw new InvalidOperationException("An unaffordable attack must not provide current-turn block potential.");
        SetEnergy(player, 3);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "FREE_ATTACK_POWER", Target = "Player", Amount = 1 });
        if ((await Capture(true)).PreventionPotential != 0)
            throw new InvalidOperationException("A free attack must not trigger the two-energy block interaction merely because of its printed cost.");

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BACKFLIP", Pile = "Hand" });
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "FASTEN_POWER", Target = "Player", Amount = 3 });
        if ((await Capture(true)).PreventionPotential != 0)
            throw new InvalidOperationException("Fasten must recognize the Defend tag, not every card that grants block.");
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand", Count = 2 });
        if ((await Capture(true)) != (await Capture(false)))
            throw new InvalidOperationException("Powers outside the validated interaction set must keep their ordinary value.");

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 500);
        SetEnergy(player, 3);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BASH", Pile = "Draw", Count = 8 });
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "DEMESNE_POWER", Target = "Player", Amount = 1 });
        var demesne = await Capture(true);
        var ordinaryDemesne = await Capture(false);
        if (demesne.ResourcePotential <= 0 || demesne.CardAccessPotential <= 0
            || ordinaryDemesne.ResourcePotential != 0 || ordinaryDemesne.CardAccessPotential != 0)
            throw new InvalidOperationException("Demesne must recognize useful future energy and draws only in boss specialization.");
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "SHIV", Pile = "Draw", Count = 8 });
        if ((await Capture(true)).ResourcePotential != 0)
            throw new InvalidOperationException("A zero-energy deck must not receive extra energy value from Demesne.");
        await ClearPlayerPilesAsync(player);
        var emptyDemesne = await Capture(true);
        if (emptyDemesne.ResourcePotential != 0 || emptyDemesne.CardAccessPotential != 0)
            throw new InvalidOperationException("An empty deck must not create future cards or energy demand.");

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = "PREP_TIME_POWER", Target = "Player", Amount = 6 });
        if ((await Capture(true)).DamagePotential != 0)
            throw new InvalidOperationException("Recurring Vigor needs a remaining attack to realize damage.");
        var ordinaryPrepTime = await Capture(false);
        await InjectCardAsync(combat, player, new UnattendedCardInjection
            { CardId = "STRIKE_IRONCLAD", Pile = "Draw", Count = 6 });
        if ((await Capture(true)).DamagePotential <= 6 || (await Capture(false)) != ordinaryPrepTime)
            throw new InvalidOperationException("Recurring Vigor must value future attacks only in boss specialization.");

        foreach (var power in player.Creature.Powers.ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = "LETHALITY_POWER", Target = "Player", Amount = 75 });
        if ((await Capture(true)).DamagePotential != 0)
            throw new InvalidOperationException("Lethality requires an attack to realize its multiplier.");
        await InjectCardAsync(combat, player, new UnattendedCardInjection
            { CardId = "ERADICATE", Pile = "Draw", Count = 1 });
        if ((await Capture(true)).DamagePotential <= (await Capture(false)).DamagePotential)
            throw new InvalidOperationException("First-attack evaluation must account for payable Eradicate hits.");
    }
}
