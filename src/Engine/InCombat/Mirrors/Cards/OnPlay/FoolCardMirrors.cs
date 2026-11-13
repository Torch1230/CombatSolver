using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class FoolCardMirrors
{
    internal static void Register(MethodMirrorRegistry<CardModel, CardOnPlayMirrorContext> registry)
    {
        registry.Register<BlossomShot>(FoolArrowCardMirrors.Blossom);
        registry.Register<RainShot>(FoolArrowCardMirrors.Rain);
        registry.Register<ShowOff>((card, c) =>
        {
            var combat = (SimulatedCombatState)c.CombatState;
            combat.ApplyPowerFromSource(typeof(AimPower), card.Owner.Creature, card.DynamicVars["AimPower"].IntValue,
                card.Owner.Creature, card);
            if (!c.Simulator.HasPendingChoice && !c.Simulator.IsEnding) combat.ApplyShowOff(c.Card);
        });
        registry.Register<KiChant>((card, c) =>
        {
            var draw = c.OwnerState.DrawPile;
            if (draw.Cards.Count == 0) c.Simulator.ShuffleIfNecessary(card.Owner);
            else if (draw.Cards.Count < card.DynamicVars.Cards.IntValue && !c.OwnerState.DiscardPile.IsEmpty)
            {
                var top = draw.Cards.ToArray();
                c.Simulator.Shuffle(card.Owner);
                if (c.Simulator.HasPendingChoice) return;
                c.Simulator.AddToPile(top.Where(candidate => candidate.GetPile(c.State) == draw).Reverse().ToArray(),
                    PileType.Draw, CardPilePosition.Top);
            }
        });
        registry.Register<Recall>((card, c) =>
        {
            bool impaled = c.Target is not null && c.Simulator.ImpaledBy(c.Target, card.Owner) is not null;
            if (impaled)
            {
                while (c.Simulator.ImpaledBy(c.Target!, card.Owner) is not null)
                {
                    c.Simulator.DislodgeArrow(c.Target!, card.Owner, c.Card);
                    if (c.Simulator.HasPendingChoice) return;
                }
            }
            c.History.CardsSelected(impaled ? c.OwnerState.DiscardPile.Cards.ToArray() : []);
        });
        OwnerPower<AllIsWell, AllIsWellPower>(registry, "AllIsWellPower");
        OwnerPower<Archery, ArcheryPower>(registry, "AimPower");
        OwnerPower<Ecstasy, EcstasyPower>(registry, "Cards");
        OwnerPower<HiddenRoots, HiddenRootsPower>(registry, "HiddenRootsPower");
        OwnerPower<Honing, HoningPower>(registry, "HoningPower");
        OwnerPower<LostLute, LostLutePower>(registry, "LostLutePower");
        OwnerPower<Lullaby, LullabyPower>(registry, "LullabyPower");
        OwnerPower<Motif, MotifPower>(registry, "MotifPower");
        OwnerPower<Quietus, QuietusPower>(registry, "QuietusPower");
        OwnerPower<SerratedArrows, SerratedArrowsPower>(registry, "SerratedArrowsPower");
        OwnerPower<Simpleton, SimpletonPower>(registry, "SimpletonPower");
        OwnerPower<SongPath, SongPathPower>(registry, "SongPathPower");
        OwnerPower<Tune, TunePower>(registry, "Energy");
        OwnerPower<ViolentChord, ViolentChordPower>(registry, "ViolentChordPower");
        OwnerPower<Yah, YahPower>(registry, "Energy");
        OwnerPower<ExplosiveArrows, ExplosiveArrowsPower>(registry, "ExplosiveArrowsPower");
        OwnerPower<KiVerse, KiVersePower>(registry, "KiVersePower");
        registry.Register<BrightenUp>(Play);
        registry.Register<MourningBlow>((card, c) =>
        {
            c.AttackSingle();
            if (!c.Simulator.HasPendingChoice && c.CardPlay.IsLastInSeries)
                c.MutablePreviewCard.TransformIntoAfterPlay = PredictionUtils.CreateCard(ModelDb.Card<Sadness>(), card.Owner);
        });
        registry.Register<Florescence>((card, c) =>
        {
            var cards = c.OwnerState.AllCards.Where(candidate => candidate.Preview.Tags.Contains(CardTag.Shoot)
                && candidate.GetPile(c.State)?.Type != PileType.Exhaust).ToArray();
            foreach (var item in cards) { c.Simulator.Exhaust(item); if (c.Simulator.HasPendingChoice) return; }
            var shot = PredictedCard.Create(ModelDb.Card<TheShot>(), card.Owner);
            if (card.IsUpgraded) shot.Upgrade();
            ((TheShot)shot.MutablePreview).BuffFromFlorescence(cards.Length);
            c.Simulator.AddGeneratedCardToCombat(shot, PileType.Hand, card.Owner);
        });
        registry.Register<Encore>((card, c) =>
        {
            c.Simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);
            if (c.Simulator.HasPendingChoice) return;
            c.Simulator.Draw(card.Owner, card.DynamicVars.Cards.IntValue);
            if (!c.Simulator.HasPendingChoice)
                ((SimulatedCombatState)c.CombatState).ApplyPowerFromSource(typeof(EncorePower), card.Owner.Creature,
                    card.DynamicVars["EncorePower"].IntValue, card.Owner.Creature, card);
        });
        registry.Register<Ennui>((card, c) =>
        {
            var combat = (SimulatedCombatState)c.CombatState;
            combat.ApplyPowerFromSource(typeof(EnnuiPower), card.Owner.Creature, card.DynamicVars["EnnuiPower"].IntValue, card.Owner.Creature, card);
            combat.GetPower<EnnuiPower>(card.Owner.Creature)?.IncrementSadnessAmount();
        });
        registry.Register<MythicForm>((card, c) =>
        {
            var combat = (SimulatedCombatState)c.CombatState;
            combat.ApplyPowerFromSource(typeof(MythicFormPower), card.Owner.Creature, 1, card.Owner.Creature, card);
            if (card.IsUpgraded) combat.GetPower<MythicFormPower>(card.Owner.Creature)?.IncrementUpgraded();
        });
        OwnerPower<Bounce, StretchPower>(registry, "StretchPower");
        registry.Register<ChoChorus>((card, c) =>
        {
            c.GainBlock(card.Owner.Creature);
            if (c.Simulator.HasPendingChoice) return;
            var cards = c.OwnerState.Hand.Cards.Where(candidate => candidate.Preview.Tags.Contains(CardTag.Wah)
                || candidate.Preview.Tags.Contains(CardTag.Ki)).ToArray();
            foreach (var candidate in cards)
            {
                c.Simulator.AutoPlay(candidate, nestedChoiceSourceId: card.Id.Entry);
                if (c.Simulator.HasPendingChoice) return;
            }
        });
        registry.Register<Brace>(Play);
        registry.Register<Joy>(Play);
        registry.Register<ChoBarbs>(Play);
        registry.Register<ChoFirm>(Play);
        registry.Register<ChoKi>(Play);
        registry.Register<ChoReady>(Play);
        registry.Register<ChoLimbs>(Play);
        registry.Register<DeepStretch>(Play);
        registry.Register<DislikeYou>(Play);
        registry.Register<KiHop>(Play);
        registry.Register<KiWhip>(Play);
        registry.Register<KiRest>(Play);
        registry.Register<BowSmack>(Play);
        registry.Register<PunchOfJoy>(Play);
        registry.Register<SorrowfulStrike>(Play);
        registry.Register<WahCho>(Play);
        registry.Register<WahChoKi>(Play);
        registry.Register<WahKi>(Play);
        registry.Register<WahSweep>(Play);
        registry.Register<WahTouch>(Play);
        registry.Register<Timber>(Play);
        registry.Register<Jest>(Play);
        registry.Register<Nail>(Play);
        registry.Register<Rummage>(Play);
        registry.Register<Vitriol>(Play);
        registry.Register<Growth>(Play);
        registry.Register<Uproot>(Play);
        registry.Register<Wrest>(Play);
        registry.Register<BlotOutTheSky>(Play);
        registry.Register<LockedIn>((_, _) => { });
        registry.Register<ArrowStab>((_, c) => c.AttackSingle());
        registry.Register<WahHammer>((_, c) => c.AttackSingle());
        registry.Register<WahBlast>((card, c) => c.AttackAllOpponents(
            ((SimulatedCombatState)c.CombatState).PlayedMelody(card.Owner, CardTag.Cho)
            || ((SimulatedCombatState)c.CombatState).PlayedMelody(card.Owner, CardTag.Ki) ? 2 : 1));
        registry.Register<SteadyCadence>((card, c) =>
        {
            c.AttackSingle();
            if (!c.Simulator.HasPendingChoice)
                c.Simulator.Draw(card.Owner, card.DynamicVars.CalculationBase.IntValue
                    + card.DynamicVars.CalculationExtra.IntValue * ((SimulatedCombatState)c.CombatState).MelodyCount(card.Owner));
        });
    }

    private static void OwnerPower<TCard, TPower>(MethodMirrorRegistry<CardModel, CardOnPlayMirrorContext> registry, string key)
        where TCard : CardModel where TPower : PowerModel
        => registry.Register<TCard>((card, c) => ((SimulatedCombatState)c.CombatState).ApplyPowerFromSource(
            typeof(TPower), card.Owner.Creature, card.DynamicVars[key].IntValue, card.Owner.Creature, card));

    private static void Play(CardModel card, CardOnPlayMirrorContext c)
    {
        var sim = c.Simulator;
        var combat = (SimulatedCombatState)c.CombatState;
        var owner = card.Owner;
        void Apply<T>(int amount, bool enemy = false) where T : PowerModel
            => combat.ApplyPowerFromSource(typeof(T), enemy ? c.Target! : owner.Creature, amount, owner.Creature, card);
        void Generate<T>(int count) where T : CardModel
            => sim.CreateAndAddGeneratedCardsToCombat<T>(owner, PileType.Hand, count, owner);
        bool Impaled() => c.Target is not null && combat.EffectivePowers().OfType<ImpaledPower>()
            .Any(power => power.Amount > 0 && power.Owner == c.Target);

        switch (card)
        {
            case BrightenUp:
                Generate<Sadness>(card.DynamicVars["Sadness"].IntValue);
                if (!sim.HasPendingChoice) Generate<Joy>(card.DynamicVars["Joy"].IntValue);
                break;
            case Brace:
                for (int i = 0, count = c.Card.ResolveEnergyXValue(c.State); i < count; i++)
                {
                    Apply<AimPower>(card.DynamicVars["AimPower"].IntValue);
                    if (sim.HasPendingChoice) return;
                }
                break;
            case Joy:
                sim.GainEnergy(owner, card.DynamicVars.Energy.IntValue);
                if (!sim.HasPendingChoice) sim.Draw(owner, card.DynamicVars.Cards.IntValue);
                break;
            case ChoBarbs:
                c.GainBlock(owner.Creature);
                if (!sim.HasPendingChoice) Apply<ChoBarbsPower>(card.DynamicVars["ChoBarbsPower"].IntValue);
                break;
            case ChoFirm:
            case ChoLimbs:
                int blocks = card is ChoLimbs || sim.AvailableArrows(owner) > 0 ? 2 : 1;
                for (int i = 0; i < blocks; i++) { c.GainBlock(owner.Creature); if (sim.HasPendingChoice) return; }
                break;
            case ChoKi:
                int stretch = combat.GetAmount<StretchPower>(owner.Creature);
                c.GainBlock(owner.Creature);
                if (!sim.HasPendingChoice && stretch > 0) Apply<StretchPower>(stretch);
                break;
            case ChoReady:
                c.GainBlock(owner.Creature);
                if (!sim.HasPendingChoice) Apply<AimPower>(card.DynamicVars["AimPower"].IntValue);
                break;
            case DeepStretch:
                Apply<StretchPower>(card.DynamicVars["StretchPower"].IntValue);
                if (!sim.HasPendingChoice) sim.Draw(owner, card.DynamicVars.Cards.IntValue);
                break;
            case DislikeYou:
                c.AttackSingle();
                if (!sim.HasPendingChoice) Apply<DislikeYouPower>(card.DynamicVars["DislikeYouPower"].IntValue, true);
                break;
            case KiHop:
            case BowSmack:
                bool impaled = Impaled();
                c.AttackSingle();
                if (sim.HasPendingChoice || !impaled) return;
                if (card is KiHop) sim.Draw(owner, card.DynamicVars.Cards.IntValue);
                else sim.GainEnergy(owner, card.DynamicVars.Energy.IntValue);
                break;
            case KiWhip:
            case WahKi:
                c.AttackSingle(card is WahKi ? card.DynamicVars.HitCount.IntValue : 1);
                if (sim.HasPendingChoice) return;
                Apply<EnergyNextTurnPower>(card.DynamicVars.Energy.IntValue);
                if (!sim.HasPendingChoice && card is WahKi) Apply<DrawCardsNextTurnPower>(card.DynamicVars.Cards.IntValue);
                break;
            case KiRest:
                Apply<EnergyNextTurnPower>(card.DynamicVars.Energy.IntValue);
                if (!sim.HasPendingChoice && (combat.PlayedMelody(owner, CardTag.Wah) || combat.PlayedMelody(owner, CardTag.Cho)))
                    Apply<RetainHandPower>(1);
                break;
            case PunchOfJoy:
                c.AttackSingle();
                if (sim.HasPendingChoice) return;
                var sadness = c.OwnerState.Hand.Cards.Where(candidate => candidate.Preview is Sadness).ToList();
                if (sadness.Count > 0)
                {
                    var chosen = sadness.StableShuffle(c.Rng.CombatCardSelection, sim).First();
                    var replacement = PredictionUtils.CreateCard(ModelDb.Card<Joy>(), owner);
                    CardChoiceSupport.TransformCardToGeneratedReplacement(sim, chosen, replacement);
                }
                break;
            case SorrowfulStrike:
                c.AttackSingle();
                if (!sim.HasPendingChoice) Generate<Sadness>(card.DynamicVars.Cards.IntValue);
                break;
            case WahCho:
            case WahChoKi:
                c.GainBlock(owner.Creature);
                if (sim.HasPendingChoice) return;
                c.AttackSingle();
                if (!sim.HasPendingChoice && card is WahChoKi) sim.Draw(owner, card.DynamicVars.Cards.IntValue);
                break;
            case WahSweep:
                c.AttackAllOpponents();
                break;
            case WahTouch:
                c.AttackSingle();
                if (!sim.HasPendingChoice) Apply<VulnerablePower>(card.DynamicVars.Vulnerable.IntValue, true);
                break;
            case Timber:
                var timberAttack = DamageCmd.Attack(card.DynamicVars.Damage.BaseValue).FromCard(card, c.CardPlay).Targeting(c.Target);
                timberAttack.Simulate(sim);
                if (!sim.HasPendingChoice && timberAttack.Results.SelectMany(results => results)
                    .Any(result => result.WasTargetKilled))
                    DamageCmd.Attack(card.DynamicVars["OnKillDamage"].BaseValue).FromCard(card, c.CardPlay)
                        .TargetingAllOpponents(c.CombatState).Simulate(sim);
                break;
            case Jest:
                c.GainBlock(owner.Creature);
                if (!sim.HasPendingChoice) Generate<Sadness>(card.DynamicVars.Cards.IntValue);
                break;
            case Nail:
                if (!Impaled()) return;
                c.AttackSingle();
                if (sim.HasPendingChoice) return;
                Apply<WeakPower>(card.DynamicVars["Power"].IntValue, true);
                if (!sim.HasPendingChoice) Apply<VulnerablePower>(card.DynamicVars["Power"].IntValue, true);
                break;
            case Rummage:
                sim.Draw(owner, card.DynamicVars.Cards.IntValue);
                if (!sim.HasPendingChoice) Generate<Sadness>(1);
                break;
            case Vitriol:
                Apply<WeakPower>(card.DynamicVars.Weak.IntValue, true);
                if (!sim.HasPendingChoice) Generate<Sadness>(1);
                break;
            case Growth:
                var exhausted = c.OwnerState.Hand.Cards.Where(candidate => candidate.Preview is Sadness).ToArray();
                foreach (var candidate in exhausted) { sim.Exhaust(candidate); if (sim.HasPendingChoice) return; }
                Apply<StretchPower>(card.DynamicVars.CalculationExtra.IntValue * exhausted.Length);
                break;
            case Uproot:
            case Wrest:
                if (sim.ImpaledBy(c.Target!, owner) is null) return;
                while (sim.ImpaledBy(c.Target!, owner) is not null)
                { sim.DislodgeArrow(c.Target!, owner, c.Card); if (sim.HasPendingChoice) return; }
                if (card is Uproot) Apply<VulnerablePower>(card.DynamicVars.Vulnerable.IntValue, true);
                else sim.Draw(owner, card.DynamicVars.Cards.IntValue);
                break;
            case BlotOutTheSky:
                var generated = sim.GetDistinctUnlockedCharacterCardsForCombat(owner, 1, c.Rng.CombatCardGeneration,
                    c.CardMultiplayerConstraint, candidate => candidate.Tags.Contains(CardTag.Shoot));
                sim.AddGeneratedCardsToCombat(generated.Select(candidate => candidate.SetToFreeThisTurn()).ToList(), PileType.Hand, owner);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(card), card.GetType().FullName);
        }
    }
}
