using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;

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
            or WeakPotion;
}
