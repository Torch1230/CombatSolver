using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal static partial class CardPowerOnPlaySupport
{
    public static void Apply(SimulatedCombatState combat, CardModel card)
    {
        Creature owner = card.Owner.Creature;
        switch (card)
        {
            case BlackHole:
                combat.Apply<BlackHolePower>(owner, card.DynamicVars["BlackHolePower"].IntValue, owner);
                break;
            case Arsenal:
                combat.Apply<ArsenalPower>(owner, card.DynamicVars["ArsenalPower"].IntValue, owner);
                break;
            case Automation:
                combat.Apply<AutomationPower>(owner, card.DynamicVars.Energy.IntValue, owner);
                break;
            case Burst:
                combat.Apply<BurstPower>(owner, card.DynamicVars["Skills"].IntValue, owner);
                break;
            case Calcify:
                combat.Apply<CalcifyPower>(owner, card.DynamicVars["CalcifyPower"].IntValue, owner);
                break;
            case CallOfTheVoid:
                combat.Apply<CallOfTheVoidPower>(owner, card.DynamicVars.Cards.IntValue, owner);
                break;
            case Calamity:
                combat.Apply<CalamityPower>(owner, 1, owner);
                break;
            case ChildOfTheStars:
                combat.Apply<ChildOfTheStarsPower>(owner, card.DynamicVars["BlockForStars"].IntValue, owner);
                break;
            case Coolant:
                combat.Apply<CoolantPower>(owner, card.DynamicVars["CoolantPower"].IntValue, owner);
                break;
            case CorrosiveWave:
                combat.Apply<CorrosiveWavePower>(owner, card.DynamicVars["CorrosiveWave"].IntValue, owner);
                break;
            case Countdown:
                combat.Apply<CountdownPower>(owner, card.DynamicVars["CountdownPower"].IntValue, owner);
                break;
            case Cruelty:
                combat.Apply<CrueltyPower>(owner, card.DynamicVars["CrueltyPower"].IntValue, owner);
                break;
            case DanseMacabre:
                combat.Apply<DanseMacabrePower>(owner, card.DynamicVars["DanseMacabrePower"].IntValue, owner);
                break;
            case Demesne:
                combat.Apply<DemesnePower>(owner, card.DynamicVars.Cards.IntValue, owner);
                break;
            case DevourLife:
                combat.Apply<DevourLifePower>(owner, card.DynamicVars["DevourLifePower"].IntValue, owner);
                break;
            case Entropy:
                combat.Apply<EntropyPower>(owner, card.DynamicVars.Cards.IntValue, owner);
                break;
            case EternalArmor:
                combat.Apply<PlatingPower>(owner, card.DynamicVars["PlatingPower"].IntValue, owner);
                break;
            case Fasten:
                combat.Apply<FastenPower>(owner, card.DynamicVars["ExtraBlock"].IntValue, owner);
                break;
            case FanOfKnives:
                combat.Apply<FanOfKnivesPower>(owner, 1, owner);
                break;
            case FeedingFrenzy:
                int feedingFrenzy = card.DynamicVars.Strength.IntValue;
                combat.Apply<StrengthPower>(owner, feedingFrenzy, owner);
                combat.Apply<FeedingFrenzyPower>(owner, feedingFrenzy, owner);
                break;
            case Genesis:
                combat.Apply<GenesisPower>(owner, card.DynamicVars["StarsPerTurn"].IntValue, owner);
                break;
            case ForegoneConclusion:
                combat.Apply<ForegoneConclusionPower>(owner, card.DynamicVars.Cards.IntValue, owner);
                break;
            case ForbiddenGrimoire:
                combat.Apply<ForbiddenGrimoirePower>(owner, 1, owner);
                combat.RecordLongTermResource(50);
                break;
            case Hailstorm:
                combat.Apply<HailstormPower>(owner, card.DynamicVars["HailstormPower"].IntValue, owner);
                break;
            case Haunt:
                combat.Apply<HauntPower>(owner, card.DynamicVars.HpLoss.IntValue, owner);
                break;
            case Hellraiser:
                combat.Apply<HellraiserPower>(owner, 1, owner);
                break;
            case Hotfix:
                combat.ApplyTemporaryFocus<HotfixPower>(
                    owner,
                    card.DynamicVars["FocusPower"].IntValue,
                    owner);
                break;
            case HelloWorld:
                combat.Apply<HelloWorldPower>(owner, 1, owner);
                break;
            case InfiniteBlades:
                combat.Apply<InfiniteBladesPower>(owner, 1, owner);
                break;
            case Iteration:
                combat.Apply<IterationPower>(owner, card.DynamicVars["IterationPower"].IntValue, owner);
                break;
            case Invoke:
                combat.Apply<SummonNextTurnPower>(owner, card.DynamicVars.Summon.IntValue, owner);
                combat.Apply<EnergyNextTurnPower>(owner, card.DynamicVars.Energy.IntValue, owner);
                break;
            case Juggernaut:
                combat.Apply<JuggernautPower>(owner, card.DynamicVars["JuggernautPower"].IntValue, owner);
                break;
            case Lethality:
                combat.Apply<LethalityPower>(owner, card.DynamicVars["LethalityPower"].IntValue, owner);
                break;
            case Loop:
                combat.Apply<LoopPower>(owner, card.DynamicVars["Loop"].IntValue, owner);
                break;
            case MachineLearning:
                combat.Apply<MachineLearningPower>(owner, card.DynamicVars.Cards.IntValue, owner);
                break;
            case MasterPlanner:
                combat.Apply<MasterPlannerPower>(owner, 1, owner);
                break;
            case Mayhem:
                combat.Apply<MayhemPower>(owner, 1, owner);
                break;
            case MonarchsGaze:
                combat.Apply<MonarchsGazePower>(owner, card.DynamicVars["StrengthLoss"].IntValue, owner);
                break;
            case NeutronAegis:
                combat.Apply<PlatingPower>(owner, card.DynamicVars["PlatingPower"].IntValue, owner);
                break;
            case Nostalgia:
                combat.Apply<NostalgiaPower>(owner, 1, owner);
                break;
            case OneTwoPunch:
                combat.Apply<OneTwoPunchPower>(owner, card.DynamicVars["Attacks"].IntValue, owner);
                break;
            case PrepTime:
                combat.Apply<PrepTimePower>(owner, card.DynamicVars["PrepTimePower"].IntValue, owner);
                break;
            case NoxiousFumes:
                combat.Apply<NoxiousFumesPower>(owner, card.DynamicVars["PoisonPerTurn"].IntValue, owner);
                break;
            case Orbit:
                combat.AddPowerInstance<OrbitPower>(owner, card.DynamicVars.Energy.IntValue, owner);
                break;
            case Outmaneuver:
                combat.Apply<EnergyNextTurnPower>(owner, card.DynamicVars.Energy.IntValue, owner);
                break;
            case Pagestorm:
                combat.Apply<PagestormPower>(owner, card.DynamicVars.Cards.IntValue, owner);
                break;
            case PaleBlueDot:
                bool alreadyHadPaleBlueDot = combat.GetAmount<PaleBlueDotPower>(owner) > 0;
                combat.Apply<PaleBlueDotPower>(owner, card.DynamicVars.Cards.IntValue, owner);
                if (!alreadyHadPaleBlueDot)
                {
                    PaleBlueDotPower paleBlueDot = combat.GetPower<PaleBlueDotPower>(owner)
                        ?? throw new InvalidOperationException("苍蓝星球施加后没有对应 Power。");
                    combat.InitializePaleBlueDot(paleBlueDot, activated: false);
                }
                break;
            case Panache:
                combat.Apply<PanachePower>(owner, card.DynamicVars["PanacheDamage"].IntValue, owner);
                break;
            case Parry:
                combat.Apply<ParryPower>(owner, card.DynamicVars["ParryPower"].IntValue, owner);
                break;
            case PhantomBlades:
                combat.Apply<PhantomBladesPower>(owner, card.DynamicVars["PhantomBladesPower"].IntValue, owner);
                break;
            case PillarOfCreation:
                combat.Apply<PillarOfCreationPower>(owner, card.DynamicVars.Block.IntValue, owner);
                break;
            case Pyre:
                combat.Apply<PyrePower>(owner, card.DynamicVars.Energy.IntValue, owner);
                break;
            case Rage:
                combat.Apply<RagePower>(owner, card.DynamicVars["Power"].IntValue, owner);
                break;
            case ReaperForm:
                combat.Apply<ReaperFormPower>(owner, 1, owner);
                break;
            case RollingBoulder:
                combat.Apply<RollingBoulderPower>(owner, card.DynamicVars["RollingBoulderPower"].IntValue, owner);
                break;
            case Royalties:
            {
                int gold = card.DynamicVars.Gold.IntValue;
                combat.Apply<RoyaltiesPower>(owner, gold, owner);
                combat.RecordLongTermResource(gold);
                combat.RecordLongTermGoal(LongTermGoals.PersistentGrowth);
                break;
            }
            case Rupture:
                combat.Apply<RupturePower>(owner, card.DynamicVars.Strength.IntValue, owner);
                break;
        }
        ApplyLate(combat, card, owner);
    }
}
