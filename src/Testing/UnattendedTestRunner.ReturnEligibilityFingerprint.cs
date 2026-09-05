using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // An isolated fingerprint invariant, not a fabricated completed-play history or
    // an actual-game replay. The two forks differ only in the eligibility membership.
    private static void AssertReturningEligibilityDistinguishesCardInstances(
        CombatState combat,
        Player player)
    {
        CombatPredictionSimulator source = CreateIsolatedDrawSimulator(combat, player);
        source.RemoveFromCombat(source.State.GetPlayerCombatState(player).AllCards.ToArray());
        PredictedCard first = PredictedCard.Create(ModelDb.Card<Bolas>(), player);
        PredictedCard second = PredictedCard.Create(ModelDb.Card<Bolas>(), player);
        second.MutablePreview.BaseReplayCount++;
        source.AddGeneratedCardToCombat(
            first, PileType.Discard, player, resultKind: CardGenerationResultKind.Fixed);
        source.AddGeneratedCardToCombat(
            second, PileType.Discard, player, resultKind: CardGenerationResultKind.Fixed);
        if (first.Preview.Id != second.Preview.Id
            || first.Preview.CurrentUpgradeLevel != second.Preview.CurrentUpgradeLevel
            || CardChoiceSupport.ChoiceCardKey(first) == CardChoiceSupport.ChoiceCardKey(second))
        {
            throw new InvalidOperationException("回手资格夹具没有建立同名同升级、语义不同的两个实例。");
        }

        CombatPredictionSimulator left = source.Fork();
        CombatPredictionSimulator right = source.Fork();
        SimPlayerCombatState leftState = left.State.GetPlayerCombatState(player);
        SimPlayerCombatState rightState = right.State.GetPlayerCombatState(player);
        PredictedCard leftFirst = leftState.DiscardPile.Cards[0];
        PredictedCard rightFirst = rightState.DiscardPile.Cards[0];
        PredictedCard rightSecond = rightState.DiscardPile.Cards[1];
        string[] originalPiles = CaptureReturningEligibilityPiles(source, player);
        StateFingerprint originalFingerprint = CaptureReturningEligibilityFingerprint(source);
        if (!originalPiles.SequenceEqual(CaptureReturningEligibilityPiles(left, player))
            || !originalPiles.SequenceEqual(CaptureReturningEligibilityPiles(right, player))
            || originalFingerprint != CaptureReturningEligibilityFingerprint(left)
            || originalFingerprint != CaptureReturningEligibilityFingerprint(right))
        {
            throw new InvalidOperationException("资格注入前两个 Fork 的牌堆或战斗指纹已不同。");
        }

        FieldInfo eligibilityField = typeof(SimulatedCombatState).GetField(
            "_returnToHandNextTurn", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(SimulatedCombatState), "_returnToHandNextTurn");
        void SetEligibility(CombatPredictionSimulator simulator, PredictedCard card)
            => eligibilityField.SetValue(
                simulator.State.CombatState,
                new HashSet<PredictedCard>(ReferenceEqualityComparer.Instance) { card });

        // The equal-membership control rules out per-Fork identity noise. Do not use
        // RecordCardLifecycle here: that would alter counters outside the tested field.
        SetEligibility(left, leftFirst);
        SetEligibility(right, rightFirst);
        if (CaptureReturningEligibilityFingerprint(left)
            != CaptureReturningEligibilityFingerprint(right))
        {
            throw new InvalidOperationException("相同回手资格映射在两个 Fork 中产生了不同指纹。");
        }
        SetEligibility(right, rightSecond);
        StateFingerprint leftBeforeReturn = CaptureReturningEligibilityFingerprint(left);
        StateFingerprint rightBeforeReturn = CaptureReturningEligibilityFingerprint(right);
        if (!originalPiles.SequenceEqual(CaptureReturningEligibilityPiles(left, player))
            || !originalPiles.SequenceEqual(CaptureReturningEligibilityPiles(right, player))
            || originalFingerprint != CaptureReturningEligibilityFingerprint(source)
            || left.History.Entries.Count != source.History.Entries.Count
            || right.History.Entries.Count != source.History.Entries.Count)
        {
            throw new InvalidOperationException("只注入回手资格却改变了牌堆、历史或源分支。");
        }

        ResolveReturningCards(left, player);
        ResolveReturningCards(right, player);
        if (leftState.Hand.Cards.Count != 1
            || rightState.Hand.Cards.Count != 1
            || !ReferenceEquals(leftState.Hand.Cards[0], leftFirst)
            || !ReferenceEquals(rightState.Hand.Cards[0], rightSecond)
            || CardChoiceSupport.ChoiceCardKey(leftState.Hand.Cards[0])
                == CardChoiceSupport.ChoiceCardKey(rightState.Hand.Cards[0])
            || CaptureReturningEligibilityPiles(left, player)
                .SequenceEqual(CaptureReturningEligibilityPiles(right, player)))
        {
            throw new InvalidOperationException("不同实例的回手资格没有形成预期的不同有序牌堆结果。");
        }
        if (leftBeforeReturn == rightBeforeReturn)
        {
            throw new InvalidOperationException(
                "返回手牌资格未区分同名同升级同牌堆的不同语义实例；" +
                $"未来结果不同却共享战斗指纹 {leftBeforeReturn.First:x16}/{leftBeforeReturn.Second:x16}。");
        }
    }

    private static StateFingerprint CaptureReturningEligibilityFingerprint(
        CombatPredictionSimulator simulator)
    {
        StateFingerprintBuilder fingerprint = new();
        ((SimulatedCombatState)simulator.State.CombatState).AppendFingerprint(ref fingerprint, simulator);
        return fingerprint.Finish();
    }

    private static string[] CaptureReturningEligibilityPiles(
        CombatPredictionSimulator simulator,
        Player player)
        => simulator.State.GetPlayerCombatState(player).AllPiles
            .Select(pile => $"{pile.Type}:{string.Join(';', pile.Cards.Select(CardChoiceSupport.ChoiceCardKey))}")
            .ToArray();
}
