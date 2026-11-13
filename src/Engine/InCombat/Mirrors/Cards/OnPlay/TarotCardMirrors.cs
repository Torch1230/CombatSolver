using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class TarotCardMirrors
{
    internal static void Register(MethodMirrorRegistry<CardModel, CardOnPlayMirrorContext> registry)
    {
        registry.Register<Death>(Play); registry.Register<Judgement>(Play); registry.Register<Strength>(Play);
        registry.Register<TheDevil>(Play); registry.Register<TheEmperor>(Play); registry.Register<TheEmpress>(Play);
        registry.Register<TheFool>(Play); registry.Register<TheHangedMan>(Play); registry.Register<TheHierophant>(Play);
        registry.Register<TheLovers>(Play); registry.Register<TheMagician>(Play); registry.Register<TheMoon>(Play);
        registry.Register<TheStar>(Play); registry.Register<TheSun>(Play); registry.Register<TheWorld>(Play);
        registry.Register<WheelOfFortune>(Play); registry.Register<TheHermit>((_, _) => { });
        registry.Register<MightyShield>(Play); registry.Register<MightyWeapon>(Play); registry.Register<GodByrd>(Play);
    }

    private static void Play(CardModel card, CardOnPlayMirrorContext c)
    {
        var sim = c.Simulator;
        var combat = (SimulatedCombatState)c.CombatState;
        var owner = card.Owner;
        void Apply<T>(int amount, bool enemy = false) where T : PowerModel
            => combat.ApplyPowerFromSource(typeof(T), enemy ? c.Target! : owner.Creature, amount, owner.Creature, card);
        switch (card)
        {
            case Death: Apply<DoomPower>(card.DynamicVars.Doom.IntValue, true); break;
            case Strength: Apply<StrengthPower>(card.DynamicVars["StrengthPower"].IntValue); break;
            case Judgement:
                if (sim.State.GetCreature(c.Target!).IsAlive && sim.State.GetCreature(c.Target!).CurrentHp <= card.DynamicVars["Threshold"].IntValue)
                    sim.Kill(c.Target!);
                break;
            case TheDevil:
                var exhausted = c.OwnerState.Hand.Cards.ToArray();
                foreach (var item in exhausted) { sim.Exhaust(item); if (sim.HasPendingChoice) return; }
                sim.AutoPlayFromDrawPile(owner, exhausted.Length, CardPilePosition.Top, upgrade: card.IsUpgraded);
                break;
            case TheEmperor: sim.GainEnergy(owner, card.DynamicVars.Energy.IntValue); break;
            case TheEmpress:
                Apply<WeakPower>(card.DynamicVars["Power"].IntValue, true);
                if (!sim.HasPendingChoice) Apply<VulnerablePower>(card.DynamicVars["Power"].IntValue, true);
                break;
            case TheFool:
                for (int index = 0; index < card.DynamicVars.Cards.BaseValue; index++)
                {
                    var selected = c.OwnerState.Hand.Cards.Where(candidate => !candidate.HasKeyword(c.State, CardKeyword.Unplayable))
                        .ToList().StableShuffle(c.Rng.CombatCardSelection, sim).FirstOrDefault();
                    selected ??= c.OwnerState.Hand.Cards.ToList().StableShuffle(c.Rng.CombatCardSelection, sim).FirstOrDefault();
                    if (selected is null) continue;
                    sim.AutoPlay(selected, nestedChoiceSourceId: card.Id.Entry);
                    if (sim.HasPendingChoice) return;
                }
                break;
            case TheHangedMan:
                sim.Damage([owner.Creature], card.DynamicVars.HpLoss.BaseValue,
                    ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move, owner.Creature, c.Card, c.CardPlay);
                if (!sim.HasPendingChoice) combat.Apply<ExtraTurnPower>(owner.Creature, 1, owner.Creature);
                break;
            case TheHierophant:
                foreach (var generated in sim.GetDistinctUnlockedColorlessForCombat(owner, card.DynamicVars.Cards.IntValue,
                             c.Rng.CombatCardGeneration, c.CardMultiplayerConstraint).ToArray())
                {
                    generated.MutablePreview.EnergyCost.AddThisTurnOrUntilPlayed(-1);
                    sim.AddGeneratedCardToCombat(generated, PileType.Hand, owner);
                    if (sim.HasPendingChoice) return;
                }
                break;
            case TheLovers:
                var loversCards = combat.OtherCharacterCards(owner).GetForCombat(owner,
                    sim.GetMaxHandSize(owner) - c.OwnerState.Hand.Cards.Count, c.Rng.CombatCardGeneration, c.CardMultiplayerConstraint, combat.Rules).ToArray();
                if (card.IsUpgraded) foreach (var generated in loversCards) generated.Upgrade();
                sim.AddGeneratedCardsToCombat(loversCards, PileType.Hand, owner);
                break;
            case TheMagician:
                c.OwnerState.OrbQueue.AddCapacity(card.DynamicVars.Repeat.IntValue);
                for (int index = 0; index < card.DynamicVars.Repeat.IntValue; index++)
                {
                    sim.OrbChannel(owner, OrbModel.GetRandomOrb(c.Rng.CombatOrbGeneration).ToMutable());
                    if (sim.HasPendingChoice) return;
                }
                break;
            case TheMoon:
                var shivs = sim.CreateAndAddGeneratedCardsToCombat<Shiv>(owner, PileType.Hand,
                    sim.GetMaxHandSize(owner) - c.OwnerState.Hand.Cards.Count, owner);
                if (sim.HasPendingChoice) return;
                if (card.IsUpgraded) foreach (var result in shivs) result.CardAdded.Upgrade();
                break;
            case TheStar:
                Apply<EnergyNextTurnPower>(card.DynamicVars.Energy.IntValue);
                if (!sim.HasPendingChoice) Apply<DrawCardsNextTurnPower>(card.DynamicVars.Cards.IntValue);
                break;
            case TheSun:
                sim.Draw(owner, card.DynamicVars.Cards.IntValue);
                if (sim.HasPendingChoice) return;
                sim.GainEnergy(owner, card.DynamicVars.Energy.IntValue);
                if (!sim.HasPendingChoice) sim.CreateAndAddGeneratedCardsToCombat<Soot>(owner, PileType.Discard, 1, owner);
                break;
            case TheWorld:
                int count = Math.Max(0, Math.Min(card.DynamicVars.Cards.IntValue, sim.GetMaxHandSize(owner) - c.OwnerState.Hand.Cards.Count));
                sim.AddToPile(c.OwnerState.DiscardPile.Cards.TakeRandom(count, c.Rng.CombatCardSelection).ToList(), PileType.Hand);
                break;
            case WheelOfFortune:
                var hand = c.OwnerState.Hand.Cards.ToArray();
                if (hand.Length > 0) sim.DiscardAndDraw(hand, card.DynamicVars.Cards.IntValue);
                else sim.Draw(owner, card.DynamicVars.Cards.IntValue);
                break;
            case MightyShield:
            case MightyWeapon:
                if (card is MightyShield) c.GainBlock(owner.Creature); else c.AttackAllOpponents();
                if (sim.HasPendingChoice) return;
                for (int i = 0; i < card.DynamicVars.Cards.IntValue; i++)
                {
                    sim.CreateAndAddGeneratedCardsToCombat<Dazed>(owner, PileType.Draw, 1, owner, CardPilePosition.Random);
                    if (sim.HasPendingChoice) return;
                }
                break;
            case GodByrd: Apply<GodByrdPower>(card.DynamicVars.Damage.IntValue); break;
            default: throw new ArgumentOutOfRangeException(nameof(card), card.GetType().FullName);
        }
    }
}
