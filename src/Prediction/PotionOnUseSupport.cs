using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Potions.OnUse;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class PotionOnUseSupport
{
    public static bool CanSearch(PotionModel potion)
        => potion is AttackPotion
            or SkillPotion
            or PowerPotion
            or ColorlessPotion
            or Ambergris
            or Ashwater
            or BeetleJuice
            or BlessingOfTheForge
            or BlockPotion
            or BloodPotion
            or BoneBrew
            or CunningPotion
            or DexterityPotion
            or DropletOfPrecognition
            or Duplicator
            or EnergyPotion
            or EntropicBrew
            or ExplosiveAmpoule
            or FirePotion
            or FlexPotion
            or FocusPotion
            or Fortifier
            or FoulPotion
            or FruitJuice
            or FyshOil
            or GamblersBrew
            or GhostInAJar
            or GigantificationPotion
            or HeartOfIron
            or KingsCourage
            or LiquidBronze
            or LiquidMemories
            or LuckyTonic
            or MazalethsGift
            or PoisonPotion
            or PotOfGhouls
            or PotionOfBinding
            or PotionOfCapacity
            or PotionOfDoom
            or PotionShapedRock
            or PowderedDemise
            or RadiantTincture
            or RegenPotion
            or ShacklingPotion
            or ShipInABottle
            or SoldiersStew
            or SpeedPotion
            or StableSerum
            or StarPotion
            or StrengthPotion
            or TouchOfInsanity
            or VulnerablePotion
            or WeakPotion
            || PotionOnUseMirrors.CanMirror(potion);

    public static void Use(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PotionModel potion,
        Creature? target)
    {
        Creature owner = potion.Owner.Creature;
        Creature? resolvedTarget = target ?? (potion.IsValidTarget(owner) ? owner : null);
        Creature playerTarget = resolvedTarget ?? owner;
        Player PlayerTarget()
            => playerTarget.Player
                ?? throw new InvalidOperationException($"药水 {potion.Id.Entry} 的目标不是玩家。");
        switch (potion)
        {
            case AttackPotion or SkillPotion or PowerPotion or ColorlessPotion:
            {
                PotionCardGenerationResult generated = CardGenerationPotionMirrors.Generate(
                    potion,
                    PlayerTarget(),
                    simulator.Rng.CombatCardGeneration,
                    combat.CardMultiplayerConstraint)
                    ?? throw new InvalidOperationException($"药水 {potion.Id.Entry} 没有生成牌策略。");
                simulator.History.CardGenerationOptions(generated.Cards);
                break;
            }
            case Ambergris value:
                simulator.Heal(
                    playerTarget,
                    simulator.State.GetCreature(playerTarget).MaxHp
                        * value.DynamicVars["HealPercent"].BaseValue / 100m);
                combat.Apply<AmbergrisPower>(playerTarget, 1, owner);
                break;
            case Ashwater or DropletOfPrecognition or GamblersBrew or LiquidMemories or TouchOfInsanity:
                break;
            case BeetleJuice value when target != null:
                combat.Apply<ShrinkPower>(target, value.DynamicVars.Repeat.IntValue, owner);
                break;
            case BlessingOfTheForge:
                foreach (PredictedCard card in simulator.State.GetPlayerCombatState(PlayerTarget()).Hand)
                {
                    if (card.Preview.IsUpgradable)
                    {
                        card.MutablePreview.UpgradeInternal();
                        card.MutablePreview.FinalizeUpgradeInternal();
                    }
                }
                break;
            case BlockPotion value:
                simulator.GainBlock(playerTarget, value.DynamicVars.Block.BaseValue, ValueProp.Unpowered);
                break;
            case BloodPotion value:
                simulator.Heal(
                    playerTarget,
                    simulator.State.GetCreature(playerTarget).MaxHp
                        * value.DynamicVars["HealPercent"].BaseValue / 100m);
                break;
            case BoneBrew value:
                combat.SummonOsty(simulator, PlayerTarget(), value.DynamicVars.Summon.IntValue);
                break;
            case CunningPotion value:
                CardPileOnPlaySupport.GenerateShivs(
                    simulator,
                    PlayerTarget(),
                    value.DynamicVars.Cards.IntValue,
                    upgraded: true);
                break;
            case DexterityPotion value:
                combat.Apply<DexterityPower>(playerTarget, value.DynamicVars.Dexterity.IntValue, owner);
                break;
            case Duplicator:
                combat.Apply<DuplicationPower>(playerTarget, 1, playerTarget);
                break;
            case EnergyPotion value:
                simulator.GainEnergy(PlayerTarget(), value.DynamicVars.Energy.IntValue);
                break;
            case EntropicBrew:
            {
                Player recipient = PlayerTarget();
                while (combat.HasOpenPotionSlot(recipient))
                {
                    PotionModel generated = PotionFactory.CreateRandomPotionOutOfCombat(
                        recipient,
                        simulator.Rng.CombatPotionGeneration);
                    if (!combat.TryProcurePotion(recipient, generated))
                        break;
                }
                break;
            }
            case ExplosiveAmpoule value:
                foreach (Creature enemy in combat.HittableEnemies.ToArray())
                {
                    if (simulator.State.IsHittable(enemy))
                        simulator.Damage(enemy, value.DynamicVars.Damage.BaseValue, value.DynamicVars.Damage.Props, owner);
                }
                break;
            case FirePotion value when target != null:
                simulator.Damage(target, value.DynamicVars.Damage.BaseValue, value.DynamicVars.Damage.Props, owner);
                break;
            case FlexPotion value:
                combat.ApplyTemporaryStrengthGain<FlexPotionPower>(
                    playerTarget, value.DynamicVars.Strength.IntValue, owner);
                break;
            case FocusPotion value:
                combat.Apply<FocusPower>(playerTarget, value.DynamicVars["FocusPower"].IntValue, owner);
                break;
            case Fortifier:
                SimCreatureState fortified = simulator.State.GetCreature(playerTarget);
                simulator.GainBlock(playerTarget, fortified.Block * 2, ValueProp.Unpowered);
                break;
            case FoulPotion value:
                foreach (Creature creature in combat.Creatures.ToArray())
                {
                    if (!creature.IsPet && simulator.State.GetCreature(creature).IsAlive)
                    {
                        simulator.Damage(
                            creature,
                            value.DynamicVars.Damage.BaseValue,
                            value.DynamicVars.Damage.Props,
                            owner);
                    }
                }
                break;
            case FruitJuice value:
            {
                SimCreatureState creature = simulator.State.GetCreature(playerTarget);
                int gained = value.DynamicVars.MaxHp.IntValue;
                creature.SetMaxHp(creature.MaxHp + gained);
                creature.Heal(gained);
                break;
            }
            case FyshOil value:
                combat.Apply<StrengthPower>(playerTarget, value.DynamicVars.Strength.IntValue, owner);
                combat.Apply<DexterityPower>(playerTarget, value.DynamicVars.Dexterity.IntValue, owner);
                break;
            case GhostInAJar value:
                combat.Apply<IntangiblePower>(playerTarget, value.DynamicVars["IntangiblePower"].IntValue, owner);
                break;
            case GigantificationPotion value:
                combat.Apply<GigantificationPower>(
                    playerTarget,
                    value.DynamicVars["GigantificationPower"].IntValue,
                    owner);
                break;
            case HeartOfIron value:
                combat.Apply<PlatingPower>(playerTarget, value.DynamicVars["PlatingPower"].IntValue, owner);
                break;
            case LiquidBronze value:
                combat.Apply<ThornsPower>(playerTarget, value.DynamicVars["ThornsPower"].IntValue, owner);
                break;
            case KingsCourage value:
                PersistentPowerSupport.Forge(simulator, PlayerTarget(), value.DynamicVars.Forge.IntValue);
                break;
            case LuckyTonic value:
                combat.Apply<BufferPower>(playerTarget, value.DynamicVars["BufferPower"].IntValue, owner);
                break;
            case MazalethsGift value:
                combat.Apply<RitualPower>(playerTarget, value.DynamicVars["RitualPower"].IntValue, owner);
                break;
            case PoisonPotion value when target != null:
                combat.Apply<PoisonPower>(target, value.DynamicVars.Poison.IntValue, owner);
                break;
            case PotOfGhouls value:
            {
                List<PredictedCard> souls = new(value.DynamicVars.Cards.IntValue);
                for (int index = 0; index < value.DynamicVars.Cards.IntValue; index++)
                    souls.Add(PredictedCard.Create(CanonicalModels.Card<Soul>(), PlayerTarget()));
                simulator.AddGeneratedCardsToCombat(
                    souls,
                    PileType.Hand,
                    PlayerTarget(),
                    CardPilePosition.Bottom,
                    CardGenerationResultKind.Fixed);
                break;
            }
            case PotionOfBinding value:
                foreach (Creature enemy in combat.HittableEnemies.ToArray())
                {
                    if (!simulator.State.IsHittable(enemy))
                        continue;
                    combat.Apply<WeakPower>(enemy, value.DynamicVars["VulnerablePower"].IntValue, owner);
                    combat.Apply<VulnerablePower>(enemy, value.DynamicVars["WeakPower"].IntValue, owner);
                }
                break;
            case PotionOfCapacity value:
                simulator.AddOrbSlots(PlayerTarget(), value.DynamicVars.Repeat.IntValue);
                break;
            case PotionOfDoom value when target != null:
                combat.Apply<DoomPower>(target, value.DynamicVars.Doom.IntValue, owner);
                break;
            case PotionShapedRock value when target != null:
                simulator.Damage(target, value.DynamicVars.Damage.BaseValue, value.DynamicVars.Damage.Props, owner);
                break;
            case PowderedDemise value when target != null:
                combat.Apply<DemisePower>(target, value.DynamicVars["Demise"].IntValue, owner);
                break;
            case RadiantTincture value:
                simulator.GainEnergy(PlayerTarget(), value.DynamicVars.Energy.IntValue);
                combat.Apply<RadiancePower>(playerTarget, value.DynamicVars["RadiancePower"].IntValue, owner);
                break;
            case RegenPotion value:
                combat.Apply<RegenPower>(playerTarget, value.DynamicVars["RegenPower"].IntValue, owner);
                break;
            case ShacklingPotion value:
                foreach (Creature enemy in combat.HittableEnemies.ToArray())
                {
                    if (simulator.State.GetCreature(enemy).IsAlive)
                    {
                        combat.ApplyTemporaryStrengthLoss<ShacklingPotionPower>(
                            enemy, value.DynamicVars.Strength.IntValue, owner);
                    }
                }
                break;
            case SpeedPotion value:
                combat.ApplyTemporaryDexterity<SpeedPotionPower>(
                    playerTarget, value.DynamicVars.Dexterity.IntValue, owner);
                break;
            case StableSerum value:
                combat.Apply<RetainHandPower>(playerTarget, value.DynamicVars.Repeat.IntValue, owner);
                break;
            case ShipInABottle value:
                simulator.GainBlock(playerTarget, value.DynamicVars.Block.BaseValue, ValueProp.Unpowered);
                combat.Apply<BlockNextTurnPower>(playerTarget, value.DynamicVars.Block.IntValue, owner);
                break;
            case SoldiersStew:
                foreach (PredictedCard card in simulator.State.GetPlayerCombatState(PlayerTarget()).AllCards)
                {
                    if (card.Preview.Tags.Contains(CardTag.Strike))
                        card.MutablePreview.BaseReplayCount++;
                }
                break;
            case StarPotion value:
                simulator.GainStars(PlayerTarget(), value.DynamicVars.Stars.IntValue);
                break;
            case StrengthPotion value:
                combat.Apply<StrengthPower>(playerTarget, value.DynamicVars.Strength.IntValue, owner);
                break;
            case VulnerablePotion value when target != null:
                combat.Apply<VulnerablePower>(target, value.DynamicVars.Vulnerable.IntValue, owner);
                break;
            case WeakPotion value when target != null:
                combat.Apply<WeakPower>(target, value.DynamicVars.Weak.IntValue, owner);
                break;
            default:
                if (PotionOnUseMirrors.CanMirror(potion))
                {
                    PotionOnUseMirrors.Invoke(simulator, potion, resolvedTarget);
                    break;
                }

                throw new NotSupportedException($"药水 {potion.Id.Entry} 尚未进入确定性搜索支持表。");
        }
    }
}
