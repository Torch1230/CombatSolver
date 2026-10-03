namespace MegaCrit.Sts2.Core.Entities.Cards {
 public enum CardType { Attack, Skill, Power, Status, Curse }
 public enum CardTag { Shiv, OstyAttack }
 public enum CardKeyword { Exhaust, Ethereal }
 public enum CostModifiers { All }
}
namespace MegaCrit.Sts2.Core.Entities.Creatures { public class Creature {} }
namespace MegaCrit.Sts2.Core.Entities.Powers {}
namespace MegaCrit.Sts2.Core.Localization.DynamicVars {
 public class DynamicVar(decimal value) { public decimal BaseValue = value; public int IntValue => (int)BaseValue; }
 public class DynamicVarSet : Dictionary<string,DynamicVar> { public Dictionary<string,DynamicVar> _vars => this; }
}
namespace MegaCrit.Sts2.Core.Models {
 using MegaCrit.Sts2.Core.Entities.Cards;
 using MegaCrit.Sts2.Core.Entities.Creatures;
 using MegaCrit.Sts2.Core.Localization.DynamicVars;
 public class CardId { public string Entry = "STRIKE"; }
 public class Cost { public bool CostsX; public int Value; public int GetWithModifiers(CostModifiers _) => Value; }
 public class CardModel {
  public static int KeywordReads;
  public CardType Type;
  public CardId Id = new();
  public DynamicVarSet DynamicVars = new();
  public HashSet<CardTag> Tags = [];
  public bool Exhaust, Ethereal;
  public Cost EnergyCost = new();
  public IReadOnlySet<CardKeyword> Keywords { get { KeywordReads++; HashSet<CardKeyword> result=[]; if(Exhaust) result.Add(CardKeyword.Exhaust); if(Ethereal) result.Add(CardKeyword.Ethereal); return result; } }
 }
 public class PowerModel { public Creature Owner = new(); }
}
namespace MegaCrit.Sts2.Core.Models.Powers {
 public class NoDrawPower : MegaCrit.Sts2.Core.Models.PowerModel {}
 public class DarkEmbracePower : MegaCrit.Sts2.Core.Models.PowerModel {}
 public class CorruptionPower : MegaCrit.Sts2.Core.Models.PowerModel {}
}
namespace CombatSolver.Engine.Common {
 internal sealed class PredictedCard(MegaCrit.Sts2.Core.Models.CardModel preview) { public MegaCrit.Sts2.Core.Models.CardModel Preview=preview; }
}
namespace CombatSolver {
 using MegaCrit.Sts2.Core.Entities.Cards;
 using MegaCrit.Sts2.Core.Models;
 internal static class CardChoiceSupport {
  internal static double CardValue(CardModel card) {
   double Value(string key)=>card.DynamicVars.TryGetValue(key,out var v)?(double)v.BaseValue:0d;
   double damage=Value("Damage"), block=Value("Block"), draw=Value("Cards"), power=card.Type==CardType.Power?8d:0d;
   return damage+block*.8d+draw*3d+power;
  }
 }
}
