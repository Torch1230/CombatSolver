using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertMirroredHookFilter(CombatState combat, Player player)
    {
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        AbstractModel[] models = ModelDb.All.ToArray();
        var filter = new MirroredHookListenerFilter(enabled: true);
        var snapshot = (MirroredHookListenerSnapshot)filter.Filter(models);
        int checkedMethods = 0;
        foreach (MirroredHookMask mask in Enum.GetValues<MirroredHookMask>())
        {
            if (mask == MirroredHookMask.All)
                continue;
            string name = mask.ToString();
            // Shared bits certify a family, rather than a method with the bit's name.
            string[] hookNames = mask switch
            {
                MirroredHookMask.GoldCallbacks => [nameof(AbstractModel.ModifyGoldGained),
                    nameof(AbstractModel.AfterModifyingGoldGained), nameof(AbstractModel.AfterGoldGained)],
                MirroredHookMask.ExtraTurnCallbacks => [nameof(AbstractModel.ShouldTakeExtraTurn),
                    nameof(AbstractModel.AfterTakingExtraTurn)],
                _ => [name],
            };
            AbstractModel[] expected = models.Where(model =>
                model.GetType().Assembly != typeof(AbstractModel).Assembly
                || model.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Any(method => hookNames.Contains(method.Name)
                        && method.DeclaringType != typeof(AbstractModel)
                        && method.GetBaseDefinition().DeclaringType == typeof(AbstractModel)))
                .ToArray();
            AbstractModel[] selected = Enumerable.Range(0, snapshot.Count)
                .Where(index => (snapshot.Layout.Entries[index].Mask & mask) != 0)
                .Select(index => snapshot[index]).ToArray();
            if (!expected.SequenceEqual(selected, ReferenceEqualityComparer.Instance))
                throw new InvalidOperationException($"Filtered dispatch differs for {name}.");
            if (snapshot.HasAny(mask) != (expected.Length != 0))
                throw new InvalidOperationException($"Empty dispatch differs for {name}.");
            checkedMethods++;
        }

        AbstractModel noOp = ModelDb.Card<StrikeIronclad>();
        AbstractModel external = models.OfType<NoOverrideSubscriber>().Single();
        AbstractModel[] duplicates = [noOp, external, external, noOp];
        var duplicateSnapshot = (MirroredHookListenerSnapshot)filter.Filter(duplicates);
        AbstractModel[] kept = Enumerable.Range(0, duplicateSnapshot.Count)
            .Where(index => duplicateSnapshot.Layout.Entries[index].Mask != 0)
            .Select(index => duplicateSnapshot[index]).ToArray();
        if (kept.Length != 2 || !ReferenceEquals(kept[0], external) || !ReferenceEquals(kept[1], external))
            throw new InvalidOperationException("Filtering removed an external receiver or a duplicate.");
        if (filter.Filter([noOp]).Count != 0)
            throw new InvalidOperationException("A default-only card was not filtered.");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator parent = root.ForkSimulator();
        SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
        parentCombat.SetAmount<StrengthPower>(player.Creature, 2);
        var parentSource = (ICombatPredictionHookListenerSource)parentCombat;
        IReadOnlyList<AbstractModel> parentList = parentSource.MirroredHookListeners;
        StrengthPower parentStrength = parentCombat.GetPower<StrengthPower>(player.Creature)!;
        CombatPredictionSimulator child = parent.Fork();
        SimulatedCombatState childCombat = (SimulatedCombatState)child.State.CombatState;
        var childSource = (ICombatPredictionHookListenerSource)childCombat;
        StrengthPower childStrength = childCombat.GetPower<StrengthPower>(player.Creature)!;
        if (ReferenceEquals(parentStrength, childStrength)
            || !childSource.MirroredHookListeners.Contains(childStrength)
            || childSource.MirroredHookListeners.Contains(parentStrength))
            throw new InvalidOperationException("Filtered receivers retained a parent Power across Fork.");
        AssertSharedHookLayouts(filter, models, parentStrength, childStrength, external);
        childCombat.SetAmount<StrengthPower>(player.Creature, 0);
        if (childSource.MirroredHookListeners.Contains(childStrength)
            || childSource.MirroredRunHookListeners.Contains(childStrength))
            throw new InvalidOperationException("Zeroing a Power retained a filtered receiver.");
        childCombat.SetAmount<StrengthPower>(player.Creature, 1);
        StrengthPower restoredStrength = childCombat.GetPower<StrengthPower>(player.Creature)!;
        if (!childSource.MirroredHookListeners.Contains(restoredStrength)
            || !childSource.MirroredRunHookListeners.Contains(restoredStrength)
            || !ReferenceEquals(parentList, parentSource.MirroredHookListeners)
            || parentStrength.Amount != 2)
            throw new InvalidOperationException("Power restoration or parent isolation changed filtered receivers.");
        PredictedCard generated = PredictedCard.Create(ModelDb.Card<Reflex>(), player);
        child.AddToPile(generated, MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand);
        if (!childSource.MirroredHookListeners.Contains(generated.Preview))
            throw new InvalidOperationException("A generated callback card did not invalidate filtered receivers.");
        AssertListenerSegmentFork(child, generated, player);
        AssertListenerWithoutPrefixAnchor(combat, player);
        AssertAfterPlayCaptureMatchesFacade(combat, player);
        if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new InvalidOperationException("Listener filtering changed the live root.");

        // A base-method patch installed after an earlier capture must disable filtering
        // at the next root, including receivers with no override of their own.
        MethodInfo methodToPatch = typeof(AbstractModel).GetMethod(nameof(AbstractModel.TryModifyEnergyCostInCombat))!;
        MethodInfo prefix = typeof(UnattendedTestRunner).GetMethod(nameof(HookFilterBasePrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
        Harmony harmony = new("CombatSolver.Tests.MirroredHookFilter");
        try
        {
            harmony.Patch(methodToPatch, prefix: new HarmonyMethod(prefix));
            AbstractModel[] source = [noOp];
            if (!ReferenceEquals(source, MirroredHookListenerFilter.Capture().Filter(source)))
                throw new InvalidOperationException("A newly patched base callback was filtered.");
            AssertAfterPlayCaptureMatchesFacade(combat, player);
        }
        finally
        {
            harmony.Unpatch(methodToPatch, prefix);
        }
        if (MirroredHookListenerFilter.Capture().Filter([noOp]).Count != 0)
            throw new InvalidOperationException("Removing the test patch did not restore root filtering.");
        AssertKeywordModifierNoOpGuard(combat, player);
        _completedChecks.Add($"MirroredHookFilter:Methods={checkedMethods}:Models={models.Length}:OrderDuplicatesExternalForkInvalidationPatchRefreshSharedLayoutsSegmentsNoAnchorEffectivePrefixReuse");
    }

    private static void AssertListenerSegmentFork(
        CombatPredictionSimulator parent, PredictedCard generated, Player player)
    {
        SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
        var parentSource = (ICombatPredictionHookListenerSource)parentCombat;
        IReadOnlyList<AbstractModel> before = parentSource.HookListeners;
        int cardIndex = before.ToList().FindIndex(model => ReferenceEquals(model, generated.Preview));
        long reuses = parentCombat.HookListenerSegmentStatistics.PrefixReuses;
        CombatPredictionSimulator child = parent.Fork();
        SimulatedCombatState childCombat = (SimulatedCombatState)child.State.CombatState;
        PredictedCard childCard = child.State.GetPlayerCombatState(player).AllCards
            .Single(card => ReferenceEquals(card.Original, generated.Original));
        IReadOnlyList<PowerModel> childPowers = childCombat.EffectivePowers();
        long effectiveReuses = childCombat.HookListenerSegmentStatistics.EffectivePrefixReuses;
        childCard.MutablePreview.AddKeyword(CardKeyword.Retain);
        IReadOnlyList<AbstractModel> after = ((ICombatPredictionHookListenerSource)childCombat).HookListeners;
        if (cardIndex < 0 || after.Count != before.Count
            || !ReferenceEquals(after[cardIndex], childCard.Preview)
            || ReferenceEquals(childCard.Preview, generated.Preview)
            || after.Contains(generated.Preview)
            || after.Contains(parentCombat.GetPower<StrengthPower>(player.Creature)!)
            || !after.Contains(childCombat.GetPower<StrengthPower>(player.Creature)!)
            || !before.Select(model => model.GetType()).SequenceEqual(after.Select(model => model.GetType()))
            || !ReferenceEquals(before, parentSource.HookListeners)
            || childCombat.HookListenerSegmentStatistics.PrefixReuses <= reuses
            || childCombat.HookListenerSegmentStatistics.EffectivePrefixReuses <= effectiveReuses
            || !ReferenceEquals(childPowers, childCombat.EffectivePowers()))
            throw new InvalidOperationException("Card mutation after Fork changed listener order, ownership or prefix reuse.");
    }

    private static void AssertListenerWithoutPrefixAnchor(CombatState live, Player player)
    {
        RelicModel[] relics = player.Relics.ToArray();
        try
        {
            foreach (RelicModel relic in relics)
                player.RemoveRelicInternal(relic, silent: true);
            CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(live).ForkSimulator();
            SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
            var source = (ICombatPredictionHookListenerSource)combat;
            List<AbstractModel> expected = source.HookListeners.ToList();
            if (expected.Any(model => model is RelicModel
                || model is PotionModel potion && ReferenceEquals(potion.Owner, player)
                || model is PowerModel power && ReferenceEquals(power.Owner, player.Creature)))
                throw new InvalidOperationException("No-prefix-anchor fixture still has a player prefix anchor.");
            int firstCard = expected.FindIndex(model => model is CardModel card && ReferenceEquals(card.Owner, player));
            if (firstCard < 0)
                throw new InvalidOperationException("No-prefix-anchor fixture requires a player card.");
            // Native visits the player's powers before the following creatures.
            // Cards are in the simulated suffix, after that creature prefix.
            int nextCreature = expected.FindIndex(model => model is MonsterModel
                || model is PowerModel power && !ReferenceEquals(power.Owner, player.Creature));
            int insertion = nextCreature < 0 ? firstCard : Math.Min(firstCard, nextCreature);
            long wholeBuilds = combat.HookListenerSegmentStatistics.WholeBuilds;
            combat.SetAmount<StrengthPower>(player.Creature, 1);
            expected.Insert(insertion, combat.GetPower<StrengthPower>(player.Creature)!);
            if (!expected.SequenceEqual(source.HookListeners, ReferenceEqualityComparer.Instance)
                || combat.HookListenerSegmentStatistics.WholeBuilds <= wholeBuilds)
                throw new InvalidOperationException("A Power without a prefix anchor changed its native insertion position.");
        }
        finally
        {
            foreach (RelicModel relic in relics)
                player.AddRelicInternal(relic, silent: true);
        }
    }

    private static void AssertSharedHookLayouts(
        MirroredHookListenerFilter filter,
        AbstractModel[] models,
        PowerModel parentPower,
        PowerModel childPower,
        AbstractModel external)
    {
        AbstractModel[] left = [parentPower, external];
        AbstractModel[] right = [childPower, external];
        var first = (MirroredHookListenerSnapshot)filter.Filter(left);
        var second = (MirroredHookListenerSnapshot)filter.Filter(right);
        if (!ReferenceEquals(first.Layout, second.Layout)
            || !ReferenceEquals(first[0], parentPower)
            || !ReferenceEquals(second[0], childPower))
            throw new InvalidOperationException("Shared hook layouts retained branch receivers or were not reused.");

        // Distinct two-type sequences provide more keys than slots. A collision
        // must retain the exact type sequence on both replacements.
        AbstractModel[][] collision = FindHookLayoutCollision(models);
        foreach (AbstractModel[] source in collision.Concat(collision))
        {
            MirroredHookListenerLayout? layout = null;
            filter.Filter(source, ref layout);
            if (layout is null || !layout.Matches(source))
                throw new InvalidOperationException("A shared hook-layout hash collision aliased runtime types.");
        }

        Parallel.For(0, 64, index =>
        {
            AbstractModel[] source = [models[index], external];
            var result = (MirroredHookListenerSnapshot)filter.Filter(source);
            if (!result.Layout.Matches(source)
                || !ReferenceEquals(result[0], source[0])
                || !ReferenceEquals(result[1], external))
                throw new InvalidOperationException("Concurrent hook-layout reuse changed receivers or type order.");
        });
    }

    private static AbstractModel[][] FindHookLayoutCollision(AbstractModel[] models)
    {
        AbstractModel[] distinct = models.DistinctBy(model => model.GetType()).ToArray();
        Dictionary<int, AbstractModel[]> slots = [];
        foreach (AbstractModel first in distinct)
        {
            foreach (AbstractModel second in distinct)
            {
                uint hash = unchecked(2u * 16777619u
                    ^ (uint)RuntimeHelpers.GetHashCode(first.GetType()));
                hash = unchecked(hash * 16777619u
                    ^ (uint)RuntimeHelpers.GetHashCode(second.GetType()));
                int slot = (int)(hash & (MirroredHookListenerFilter.SharedLayoutSlots - 1));
                AbstractModel[] source = [first, second];
                if (slots.TryGetValue(slot, out AbstractModel[]? previous))
                    return [previous, source];
                slots.Add(slot, source);
            }
        }
        throw new InvalidOperationException("Hook-layout collision fixture requires more type pairs than slots.");
    }

    private static void HookFilterBasePrefix() { }

    private static void AssertKeywordModifierNoOpGuard(CombatState live, Player player)
    {
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        CombatPredictionSimulator parent = CombatRootSnapshot.Capture(live).ForkSimulator();
        SimulatedCombatState combat = (SimulatedCombatState)parent.State.CombatState;
        PredictedCard card = PredictedCard.Create(ModelDb.Card<StrikeIronclad>(), player);
        parent.AddToPile(card, PileType.Hand);
        card.MutablePreview.AddKeyword(CardKeyword.Retain);
        AssertKeywordSets(parent, card, ethereal: false);
        combat.SetAmount<HexPower>(player.Creature, 1);
        parent.Afflict<Hexed>(card, 1);
        AssertKeywordSets(parent, card, ethereal: true);

        CombatPredictionSimulator child = parent.Fork();
        PredictedCard childCard = child.State.GetPlayerCombatState(player).AllCards
            .Single(candidate => ReferenceEquals(candidate.Original, card.Original));
        combat.SetAmount<HexPower>(player.Creature, 0);
        AssertKeywordSets(parent, card, ethereal: false);
        AssertKeywordSets(child, childCard, ethereal: true);
        card.MutablePreview.AddKeyword(CardKeyword.Ethereal);
        AssertKeywordSets(parent, card, ethereal: true);

        MethodInfo hook = typeof(Hook).GetMethod(nameof(Hook.ModifyKeywordsInCombat))!;
        MethodInfo prefix = typeof(UnattendedTestRunner).GetMethod(
            nameof(HookFilterNativeKeywordPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
        Harmony harmony = new("CombatSolver.Tests.NativeKeywordGuard");
        try
        {
            harmony.Patch(hook, prefix: new HarmonyMethod(prefix));
            AbstractModel[] source = [ModelDb.Card<StrikeIronclad>()];
            if (!ReferenceEquals(source, MirroredHookListenerFilter.Capture().Filter(source)))
                throw new InvalidOperationException("Patched native keyword Hook retained the no-op guard.");
            CombatPredictionSimulator patched = CombatRootSnapshot.Capture(live).ForkSimulator();
            PredictedCard patchedCard = PredictedCard.Create(ModelDb.Card<StrikeIronclad>(), player);
            patched.AddToPile(patchedCard, PileType.Hand);
            if (!patchedCard.HasKeyword(patched.State, CardKeyword.Exhaust))
                throw new InvalidOperationException("Native keyword Hook patch was skipped.");
        }
        finally
        {
            harmony.Unpatch(hook, prefix);
        }
        if (MirroredHookListenerFilter.Capture().Filter([ModelDb.Card<StrikeIronclad>()]).Count != 0)
            throw new InvalidOperationException("Native keyword patch removal did not restore filtering.");
    }

    private static void AssertKeywordSets(
        CombatPredictionSimulator simulator, PredictedCard card, bool ethereal)
    {
        HashSet<CardKeyword> original = card.Preview.LocalKeywords.ToHashSet();
        Hook.ModifyKeywordsInCombat(simulator.State.CombatState, card.Preview, original);
        if (!original.SetEquals(card.GetKeywords(simulator.State))
            || original.Contains(CardKeyword.Ethereal) != ethereal
            || !original.Contains(CardKeyword.Retain))
            throw new InvalidOperationException("Keyword set differs from native keyword Hook.");
        foreach (CardKeyword keyword in Enum.GetValues<CardKeyword>())
            if (card.HasKeyword(simulator.State, keyword) != original.Contains(keyword))
                throw new InvalidOperationException($"Keyword membership differs for {keyword}.");
    }

    private static void HookFilterNativeKeywordPrefix(ISet<CardKeyword> keywords)
        => keywords.Add(CardKeyword.Exhaust);
}
