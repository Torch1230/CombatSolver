using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class CardChoiceSupport
{
    public static void Apply(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard,
        PlanCardChoice choice,
        ISet<uint>? processedEnemyDeaths = null)
    {
        SimPlayerCombatState owner = simulator.State.GetPlayerCombatState(playedCard.Preview.Owner);
        List<PredictedCard> selected;
        if (choice.Effect == PlanChoiceEffect.GenerateToHand)
        {
            CombatPredictionCardGenerationOptionsEntry entry = simulator.History
                .OfType<CombatPredictionCardGenerationOptionsEntry>()
                .LastOrDefault(candidate => playedCard.References(candidate.Trace?.Source))
                ?? throw new InvalidOperationException($"卡牌 {playedCard.Preview.Id.Entry} 缺少生成选项。");
            selected = choice.Cards.Select(token => Find(entry.Options, token)).ToList();
        }
        else
        {
            SimCardPile pile = owner.GetCardPile(choice.SourcePile)
                ?? throw new InvalidOperationException($"找不到模拟牌堆 {choice.SourcePile}。");
            selected = choice.Cards.Select(token => Find(pile.Cards, token)).ToList();
        }

        switch (choice.Effect)
        {
            case PlanChoiceEffect.MoveToHand:
                simulator.AddToPile(selected, PileType.Hand);
                break;
            case PlanChoiceEffect.MoveToDrawTop:
                simulator.AddToPile(selected, PileType.Draw, CardPilePosition.Top);
                break;
            case PlanChoiceEffect.Discard:
                simulator.Discard(selected);
                break;
            case PlanChoiceEffect.Exhaust:
                foreach (PredictedCard card in selected)
                    simulator.Exhaust(card);
                break;
            case PlanChoiceEffect.Upgrade:
                foreach (PredictedCard card in selected)
                    card.Upgrade();
                break;
            case PlanChoiceEffect.Transform:
                TransformSelectedCards(simulator, playedCard.Preview, selected);
                break;
            case PlanChoiceEffect.Duplicate:
                DuplicateSelectedCard(simulator, playedCard.Preview, selected);
                break;
            case PlanChoiceEffect.Modify:
                ModifySelectedCard(playedCard.Preview, selected);
                break;
            case PlanChoiceEffect.Nightmare:
                ApplyNightmare(combat, playedCard.Preview, selected);
                break;
            case PlanChoiceEffect.ApplySly:
                foreach (PredictedCard card in selected)
                    card.MutablePreview.GiveSingleTurnSly();
                break;
            case PlanChoiceEffect.ApplyEthereal:
                foreach (PredictedCard card in selected)
                    card.MutablePreview.AddKeyword(CardKeyword.Ethereal);
                break;
            case PlanChoiceEffect.ApplyRetain:
                foreach (PredictedCard card in selected)
                    card.MutablePreview.AddKeyword(CardKeyword.Retain);
                break;
            case PlanChoiceEffect.AutoPlayRepeated:
                AutoPlayRepeated(
                    simulator,
                    combat,
                    playedCard.Preview,
                    selected,
                    processedEnemyDeaths ?? new HashSet<uint>());
                break;
            case PlanChoiceEffect.GenerateToHand:
                AddGeneratedSelectionToHand(simulator, playedCard.Preview, selected);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(choice));
        }

        ApplyPostChoiceEffects(simulator, combat, playedCard);
    }

    public static void ApplyNoChoiceEffects(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard)
        => ApplyPostChoiceEffects(simulator, combat, playedCard);

    public static void TransformCards(
        CombatPredictionSimulator simulator,
        IEnumerable<PredictedCard> cards,
        CardModel replacementCanonical,
        bool upgradeReplacement)
    {
        foreach (PredictedCard original in cards.ToList())
        {
            PredictedCard replacement = PredictedCard.Create(replacementCanonical, original.Preview.Owner);
            if (upgradeReplacement)
                replacement.Upgrade();
            ReplaceTransformedCard(
                simulator,
                original,
                replacement,
                CardGenerationResultKind.Fixed);
        }
    }

    public static void TransformCardToGeneratedReplacement(
        CombatPredictionSimulator simulator,
        PredictedCard original,
        CardModel generatedReplacement)
        => ReplaceTransformedCard(
            simulator,
            original,
            PredictedCard.FromGenerated(generatedReplacement),
            CardGenerationResultKind.Random);

    private static void TransformSelectedCards(
        CombatPredictionSimulator simulator,
        CardModel source,
        IReadOnlyList<PredictedCard> selected)
    {
        CardModel replacement = source switch
        {
            Begone => CanonicalModels.Card<MinionStrike>(),
            Charge => CanonicalModels.Card<MinionDiveBomb>(),
            Guards => CanonicalModels.Card<MinionSacrifice>(),
            Seance => CanonicalModels.Card<Soul>(),
            _ => throw new InvalidOperationException($"卡牌 {source.Id.Entry} 没有选牌变换定义。"),
        };
        bool upgrade = source.IsUpgraded && source is Begone or Charge or Guards;
        TransformCards(simulator, selected, replacement, upgrade);
    }

    private static void ReplaceTransformedCard(
        CombatPredictionSimulator simulator,
        PredictedCard original,
        PredictedCard replacement,
        CardGenerationResultKind resultKind)
    {
        SimCardPile pile = original.GetPile(simulator.State)
            ?? throw new InvalidOperationException($"变换时找不到 {original.Preview.Id.Entry} 所在牌堆。");
        int index = -1;
        for (int candidateIndex = 0; candidateIndex < pile.Cards.Count; candidateIndex++)
        {
            if (!ReferenceEquals(pile.Cards[candidateIndex], original))
                continue;
            index = candidateIndex;
            break;
        }
        if (index < 0)
            throw new InvalidOperationException($"变换时找不到 {original.Preview.Id.Entry} 的牌堆位置。");

        simulator.RemoveFromCombat(original);
        replacement.MutablePreview.HasBeenRemovedFromState = false;
        replacement.NotifyHookListenerStructureChanged();
        pile.Insert(Math.Min(index, pile.Cards.Count), replacement);
        var generation = simulator.History.CardGenerated(
            replacement,
            replacement.Preview.Owner,
            resultKind);
        if (simulator.State.CombatState is ICombatPredictionCardEventSink eventSink)
            eventSink.AfterCardEnteredCombat(simulator, replacement);
        original.MutablePreview.AfterTransformedFrom();
        replacement.MutablePreview.AfterTransformedTo();
        HookMirrors.AfterCardGeneratedForCombat(
            simulator,
            replacement,
            replacement.Preview.Owner);
        simulator.History.CardGenerationResolved(generation, replacement);
    }

    private static void DuplicateSelectedCard(
        CombatPredictionSimulator simulator,
        CardModel source,
        IReadOnlyList<PredictedCard> selected)
    {
        if (selected.Count == 0)
            return;
        int count = source switch
        {
            DualWield => source.DynamicVars.Cards.IntValue,
            HeirloomHammer => source.DynamicVars.Repeat.IntValue,
            _ => 0,
        };
        List<PredictedCard> copies = new(count);
        for (int index = 0; index < count; index++)
            copies.Add(selected[0].CreateClone());
        simulator.AddGeneratedCardsToCombat(
            copies,
            PileType.Hand,
            source.Owner,
            CardPilePosition.Bottom,
            CardGenerationResultKind.Fixed);
    }

    private static void AutoPlayRepeated(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CardModel source,
        IReadOnlyList<PredictedCard> selected,
        ISet<uint> processedEnemyDeaths)
    {
        if (source is not DecisionsDecisions || selected.Count == 0)
            return;
        for (int index = 0; index < source.DynamicVars.Repeat.IntValue; index++)
        {
            if (!CardExecutionSupport.AutoPlay(
                    simulator,
                    combat,
                    selected[0],
                    target: null,
                    processedEnemyDeaths,
                    nestedChoiceSourceId: source.Id.Entry))
            {
                break;
            }
        }
    }

    private static void AddGeneratedSelectionToHand(
        CombatPredictionSimulator simulator,
        CardModel source,
        IReadOnlyList<PredictedCard> selected)
    {
        if (selected.Count == 0)
            return;
        PredictedCard generated = selected[0].Clone();
        if (source is Abundance or Discovery or Splash)
            generated.SetToFreeThisTurn();
        simulator.AddGeneratedCardToCombat(
            generated,
            PileType.Hand,
            source.Owner,
            resultKind: CardGenerationResultKind.Random);
    }

    private static void ModifySelectedCard(CardModel source, IReadOnlyList<PredictedCard> selected)
    {
        if (source is not Transfigure || selected.Count == 0)
            return;
        CardModel card = selected[0].MutablePreview;
        if (!card.EnergyCost.CostsX && card.EnergyCost.GetWithModifiers(CostModifiers.None) >= 0)
            card.EnergyCost.AddThisCombat(1);
        card.BaseReplayCount++;
    }

    private static void ApplyNightmare(
        SimulatedCombatState combat,
        CardModel source,
        IReadOnlyList<PredictedCard> selected)
    {
        if (source is not Nightmare || selected.Count == 0)
            return;
        NightmarePower power = combat.AddPowerInstance<NightmarePower>(
            source.Owner.Creature,
            3,
            source.Owner.Creature);
        combat.SetNightmareSelection(power, selected[0]);
    }

    private static void ApplyPostChoiceEffects(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard playedCard)
    {
        CardModel source = playedCard.Preview;
        switch (source)
        {
            case HiddenDaggers:
                CardPileOnPlaySupport.GenerateShivs(
                    simulator,
                    source.Owner,
                    source.DynamicVars["Shivs"].IntValue,
                    source.IsUpgraded);
                break;
            case Brand:
                combat.Apply<StrengthPower>(
                    source.Owner.Creature,
                    source.DynamicVars.Strength.IntValue,
                    source.Owner.Creature);
                break;
            case Scavenge:
                combat.AddEnergyNextTurn(source.Owner, source.DynamicVars.Energy.IntValue);
                break;
            case BurningPact:
            {
                bool sourceAlreadyInDiscard = playedCard.GetPile(simulator.State)?.Type == PileType.Discard;
                if (sourceAlreadyInDiscard)
                    simulator.AddToPile(playedCard, PileType.Play);
                simulator.Draw(source.Owner, source.DynamicVars.Cards.IntValue);
                if (sourceAlreadyInDiscard && playedCard.GetPile(simulator.State)?.Type == PileType.Play)
                    simulator.AddToPile(playedCard, PileType.Discard);
                break;
            }
        }
    }

}
