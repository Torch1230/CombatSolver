// API-shaped test doubles validate capture wiring, not native game behavior.
namespace MegaCrit.Sts2.Core.Entities.Cards
{
    enum CardType { Attack, Skill, Power, Curse, Status }
    enum TargetType { Self, AllEnemies }
    enum CardKeyword { Exhaust }
    enum KeywordSources { Local }
    enum CostModifiers { Local }
}
namespace MegaCrit.Sts2.Core.Models
{
    using MegaCrit.Sts2.Core.Entities.Cards;
    record ModelId(string Entry);
    sealed class Variable { public decimal BaseValue { get; set; } }
    sealed class Cost { public bool CostsX { get; set; } public int Value { get; set; } public int GetWithModifiers(CostModifiers _) => Value; }
    class CardModel
    {
        public ModelId Id { get; set; } = new("TEST");
        public Dictionary<string, Variable> DynamicVars { get; } = [];
        public Cost EnergyCost { get; } = new();
        public CardType Type { get; set; }
        public TargetType TargetType { get; set; }
        public HashSet<CardKeyword> Keywords { get; } = [];
        public IEnumerable<CardKeyword> GetKeywordsWithSources(KeywordSources _) => Keywords;
        public bool IsBasicStrikeOrDefend { get; set; }
        public bool IsRemovable { get; set; } = true;
        public int CurrentUpgradeLevel { get; set; }
        public bool HasStarCostX { get; set; }
        public int CurrentStarCost { get; set; }
    }
    class RelicModel { public ModelId Id { get; set; } = new("TEST"); public Dictionary<string, Variable> DynamicVars { get; } = []; public string Rarity => "Common"; }
    class PotionModel { public ModelId Id { get; set; } = new("TEST"); public string Rarity => "Common"; }
}
namespace MegaCrit.Sts2.Core.Entities.Players
{
    using MegaCrit.Sts2.Core.Models;
    sealed class Deck { public List<CardModel> Cards { get; } = []; }
    sealed class Creature { public int CurrentHp => 50; public int MaxHp => 80; }
    sealed class Run { public int CurrentActIndex => 0; }
    sealed class Player
    {
        public Deck Deck { get; } = new(); public List<RelicModel> Relics { get; } = [];
        public Creature Creature { get; } = new(); public Run RunState { get; } = new();
        public int Gold => 100; public int MaxEnergy { get; set; } = 3;
        public List<PotionModel?> PotionSlots { get; } = [null];
    }
}
namespace MegaCrit.Sts2.Core.Entities.Merchant
{
    using MegaCrit.Sts2.Core.Models;
    class MerchantEntry { public int Cost => 50; public bool IsStocked => true; }
    record Creation(CardModel Card);
    class MerchantCardEntry : MerchantEntry { public Creation? CreationResult { get; set; } }
    class MerchantRelicEntry : MerchantEntry { public RelicModel? Model { get; set; } }
    class MerchantPotionEntry : MerchantEntry { public PotionModel? Model { get; set; } }
    class MerchantCardRemovalEntry : MerchantEntry { }
}
