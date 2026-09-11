using System.Reflection;
using System.Runtime.ExceptionServices;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task PrepareCompactPagestormAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, 1, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        if (mode > 0)
        {
            var power = await PowerCmd.Apply<PagestormPower>(new BlockingPlayerChoiceContext(), player.Creature, mode, player.Creature, null);
            power!.AmountOnTurnStart = 37;
        }
        string[] ids = ["PAGESTORM", "PAGESTORM", "ESCAPE_PLAN", "SCULPTING_STRIKE", "SURVIVOR", "PREPARED", "DEFEND_NECROBINDER"];
        for (int index = 0; index < ids.Length; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection
            {
                CardId = ids[index], Pile = "Hand", UpgradeLevels = index is 1 or 5 or 6 ? 1 : 0,
                EnchantmentId = index == 3 ? "SWIFT" : null, EnchantmentAmount = index == 3 ? 1 : 0
            });
        player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == "PREPARED").AddKeyword(CardKeyword.Sly);
        // Both definitions are natively Ethereal; Veilpiercer only references the
        // keyword in its Power tooltip and cannot serve as this recursive witness.
        foreach (string id in new[] { "DEFILE", "LETHALITY" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection
            { CardId = id, Pile = "Draw", EnchantmentId = "SLITHER", EnchantmentAmount = 1 });
        for (int index = 0; index < 4; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection
            { CardId = "DEFEND_NECROBINDER", Pile = "Discard", EnchantmentId = index == 0 ? "SLITHER" : null,
                EnchantmentAmount = index == 0 ? 1 : 0 });
        var enemy = combat.Enemies.Single();
        await CreatureCmd.SetMaxHp(enemy, 10_000);
        await CreatureCmd.SetCurrentHp(enemy, 10_000);
        SetEnergy(player, 20);
    }

    private async Task AssertCompactPagestormAsync(CombatState combat, Player player)
    {
        for (int mode = 0; mode < 3; mode++)
        {
            await PrepareCompactPagestormAsync(combat, player, mode);
            var outerCards = player.PlayerCombatState!.DrawPile.Cards.ToArray();
            int nativePending = 0;
            bool handDraw = false, powerDraw = false, slyDraw = false;
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactPagestorm", "compact-pagestorm",
                [("PAGESTORM", 0), ("ESCAPE_PLAN", 0), ("PAGESTORM", 1), ("SCULPTING_STRIKE", 0), ("DEFEND_NECROBINDER", 1), ("SURVIVOR", 0)],
                (lane, before, step) =>
                {
                    var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                    int amount = Enumerable.Range(0, lane.PowerCount).Where(index => lane.PowerDefinition(index).Kind == BasicPowerKind.Pagestorm)
                        .Select(index => lane.Power(index).Amount).Single();
                    if (amount != mode + (step < 2 ? 1 : 2))
                        throw new InvalidOperationException("Pagestorm root/new stacking lost its captured amount.");
                    if (step == 1)
                    {
                        int[] originals = before.Cards(ResumableDiscardProgram.Pile.Draw);
                        var costs = events.Where(item => item.Kind == ResumableDiscardProgram.EventKind.CostChanged).ToArray();
                        if (lane.Block != before.Block || costs.Length < 2 || costs[^1].Card != originals[0] || costs[^2].Card != originals[1]
                            || events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.DrawPowerStart) < 2)
                            throw new InvalidOperationException($"Nested Pagestorm draw witness failed: block={before.Block}->{lane.Block}; "
                                + $"originals={string.Join(',', originals)}; costs={string.Join(',', costs.Select(item => item.Card))}; "
                                + $"hooks={events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.DrawPowerStart)}.");
                    }
                    if (step == 4 && !events.Any(item => item.Kind == ResumableDiscardProgram.EventKind.Finish && (item.Flags & 1) != 0))
                        throw new InvalidOperationException("Sculpting Strike's newly Ethereal card lost its current keyword.");
                    if (step == 5)
                        slyDraw |= events.Any(item => item.Kind == ResumableDiscardProgram.EventKind.Start && item.Automatic)
                            && events.Any(item => item.Kind == ResumableDiscardProgram.EventKind.Draw);
                    if (step >= 6)
                    {
                        handDraw |= events.Any(item => item.Kind == ResumableDiscardProgram.EventKind.Draw && item.Value == 1);
                        int depth = 0;
                        foreach (var item in events)
                        {
                            if (item.Kind == ResumableDiscardProgram.EventKind.DrawPowerStart) depth++;
                            else if (item.Kind == ResumableDiscardProgram.EventKind.DrawPowerFinish) depth--;
                            else if (item.Kind == ResumableDiscardProgram.EventKind.Draw && depth > 0 && item.Value == 0) powerDraw = true;
                        }
                    }
                }, forceOpeningPower: true, verifyEnergyCosts: true, usePreparedCardBranches: true, chooseBranch: (action, step) => step switch
                {
                    3 => action.GetActionChoicesInExecutionOrder().SelectMany(choice => choice.Cards)
                        .Any(token => token.CardId == "DEFEND_NECROBINDER" && token.UpgradeLevel == 1),
                    5 => action.GetActionChoicesInExecutionOrder().SelectMany(choice => choice.Cards).Any(token => token.CardId == "PREPARED"),
                    _ => true
                }, observeNativeChoice: (action, _) =>
                {
                    if (action.CardId != "ESCAPE_PLAN") return false;
                    if (player.Creature.GetPowerAmount<PagestormPower>() != mode + 1
                        || outerCards.Any(card => card.EnergyCost._localModifiers.Count != 0 || card.Pile?.Type != PileType.Hand))
                        throw new InvalidOperationException("Native nested shuffle must precede both parent Slither callbacks.");
                    nativePending++;
                    return true;
                });
            // The first root witnesses a nested Power draw during the next turn.
            // Larger initial amounts can exhaust those cards before the next draw;
            // their full native/model comparison must preserve that different route.
            if (nativePending == 0 || !handDraw || !slyDraw || mode == 0 && !powerDraw)
                throw new InvalidOperationException($"Pagestorm witness omitted coverage: choices={nativePending}; "
                    + $"handDraw={handDraw}; roundPowerDraw={powerDraw}; slyDraw={slyDraw}.");
            _completedChecks.Add($"CompactPagestorm:Mode{mode}:NativeNestedChoice:ReverseSlitherOrder:ParentDrawReturn:NewAndRootPower:Swift:Sly:AddedEthereal:HandDraw:RoundPowerDraw{powerDraw}:TwoRounds");
        }
    }

    // A semantic witness can be discarded by action dominance/retention. Use the same
    // prepared actions, replay and choice resolvers before that policy stage. The separate
    // fixed-work search continues to test the unmodified complete policy.
    private static IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> EnumerateCompactCardEffectBranches(
        CombatBeamSolver driver, SearchNode node, (string Id, int? Upgrade) required)
    {
        var prepared = (System.Collections.IEnumerable)Invoke("PrepareCardActions", [node])!;
        foreach (object item in prepared)
        {
            object? Property(string name) => item.GetType().GetProperty(name)!.GetValue(item);
            var action = (PlanAction)Property("Action")!;
            if (action.CardId != required.Id || required.Upgrade != null && action.CardUpgradeLevel != required.Upgrade) continue;
            var probe = (SimulationSnapshot)Invoke("ReplayAction", [node, action, null, null])!;
            bool transferred = false;
            try
            {
                var choice = (CardChoiceSpec?)Invoke("BuildPrimaryCardChoiceSpec", [probe]);
                if (choice == null && (bool)Property("RequiresUnsupportedExistingChoice")!)
                    throw new InvalidOperationException("Semantic witness requires an unsupported existing choice.");
                var empty = (PlanCardChoice?)Property("RequiredEmptyChoice");
                var primary = choice ?? (CardChoiceSpec?)Invoke("BuildRequiredEmptyChoiceSpec", [empty]);
                bool beforePrimary = (bool)Invoke("HasChoiceBeforePrimary", [probe, primary])!;
                var branches = (IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>)(beforePrimary
                    ? Invoke("ResolveRoundChoiceBranches", [node, action, probe, Invoke("BuildPrimaryChoiceMatch", [primary]), null, primary, null],
                        method => (Nullable.GetUnderlyingType(method.GetParameters()[4].ParameterType)
                            ?? method.GetParameters()[4].ParameterType).Name == "WholeActionChoiceBudget")
                    : Invoke("ResolvePrimaryCardChoiceBranches", [node, action, probe, choice, empty]))!;
                transferred = true;
                foreach (var branch in branches) yield return branch;
            }
            finally { if (!transferred) probe.ReleaseSimulator(); }
        }

        object? Invoke(string name, object?[] arguments, Func<MethodInfo, bool>? filter = null)
        {
            var method = typeof(CombatBeamSolver).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length && (filter?.Invoke(method) ?? true));
            try { return method.Invoke(driver, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
