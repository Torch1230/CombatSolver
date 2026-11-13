using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed record KnowledgeDemonChoiceRequest(
    Creature Source,
    int Counter,
    string SourceId,
    IReadOnlyList<string> OptionIds);

internal static class KnowledgeDemonChoiceSupport
{
    private static readonly string[][] OptionsByCounter =
    [
        [CanonicalModels.Card<Disintegration>().Id.Entry, CanonicalModels.Card<MindRot>().Id.Entry],
        [CanonicalModels.Card<Disintegration>().Id.Entry, CanonicalModels.Card<Sloth>().Id.Entry],
        [CanonicalModels.Card<Disintegration>().Id.Entry, CanonicalModels.Card<WasteAway>().Id.Entry],
    ];

    public static void Resolve(
        SimulatedCombatState combat,
        Creature source,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices)
    {
        int counter = combat.GetKnowledgeDemonCurseCounter(source);
        if ((uint)counter >= (uint)OptionsByCounter.Length)
            throw new InvalidOperationException($"知识恶魔诅咒计数超出范围：{counter}。");

        string sourceId = SourceId(source, counter);
        IReadOnlyList<string> optionIds = OptionsByCounter[counter];
        PlanCardChoice? choice = plannedChoices?.FirstOrDefault(candidate =>
            candidate.Effect == PlanChoiceEffect.ApplyKnowledgeCurse
            && string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));
        if (choice == null)
        {
            combat.SetPendingKnowledgeDemonChoice(new KnowledgeDemonChoiceRequest(
                source,
                counter,
                sourceId,
                optionIds));
            return;
        }
        if (choice.SourcePile != PileType.None || choice.Cards.Count != 1)
            throw new InvalidOperationException($"知识恶魔计划选牌格式无效：{sourceId}。");

        string selectedId = choice.Cards[0].CardId;
        if (!optionIds.Contains(selectedId, StringComparer.Ordinal))
            throw new InvalidOperationException($"知识恶魔当前不能选择 {selectedId}。");

        ApplyCurse(combat, player, selectedId, (combat.Rules.IsDread ? 8 : 6) + counter);

        combat.AdvanceKnowledgeDemonCurseCounter(source);
        combat.ClearPendingKnowledgeDemonChoice();
    }

    internal static void ApplyCurse(SimulatedCombatState combat, Creature player, string selectedId, int disintegrationDamage)
    {
        if (selectedId == CanonicalModels.Card<Disintegration>().Id.Entry)
            combat.Apply<DisintegrationPower>(player, disintegrationDamage, player);
        else if (selectedId == CanonicalModels.Card<MindRot>().Id.Entry)
            combat.Apply<MindRotPower>(player, 1, player);
        else if (selectedId == CanonicalModels.Card<Sloth>().Id.Entry)
            combat.Apply<SlothPower>(player, 3, player);
        else if (selectedId == CanonicalModels.Card<WasteAway>().Id.Entry)
            combat.Apply<WasteAwayPower>(player, 1, player);
        else if (selectedId == CanonicalModels.Card<Amnesia>().Id.Entry)
            combat.Apply<AmnesiaPower>(player, 1, player);
        else
            throw new InvalidOperationException($"知识恶魔诅咒 {selectedId} 没有模拟效果。");

    }

    internal static void ResolveDominion(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        Player player, TurnStartChoiceCursor choices, string sourceId)
    {
        if (combat.GetPlayerTurnNumber(player) > 1 || !simulator.State.GetCreature(player.Creature).IsAlive) return;
        PredictedCard[] options = [PredictedCard.Create(CanonicalModels.Card<Sloth>(), player),
            PredictedCard.Create(CanonicalModels.Card<Amnesia>(), player)];
        var spec = new CardChoiceSpec(PlanChoiceEffect.ApplyKnowledgeCurse, PileType.None, 1, 1, options, [], ReplacementValue: 0d);
        TurnStartChoiceSupport.ResolveCapturedChoice(simulator, combat, player, choices,
            new TurnStartChoiceRequest(sourceId, PlanChoiceEffect.ApplyKnowledgeCurse, PileType.None, 1, spec,
                Timing: combat.ActiveActionChoiceTiming));
    }

    public static IReadOnlyList<PlanCardChoice> BuildChoices(
        KnowledgeDemonChoiceRequest request,
        SolverDisplayNames displayNames)
    {
        List<PlanCardChoice> choices = new(request.OptionIds.Count);
        for (int optionIndex = 0; optionIndex < request.OptionIds.Count; optionIndex++)
        {
            string cardId = request.OptionIds[optionIndex];
            choices.Add(new PlanCardChoice(
                PlanChoiceEffect.ApplyKnowledgeCurse,
                PileType.None,
                [new PlanCardToken(cardId, 0, string.Empty, 0, 0, displayNames.Card(cardId))],
                request.SourceId,
                Timing: PlanChoiceTiming.EnemyTurn));
        }
        return choices;
    }

    private static string SourceId(Creature source, int counter)
        => $"KNOWLEDGE_DEMON:{source.CombatId ?? uint.MaxValue}:{counter}";
}
