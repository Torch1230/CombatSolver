using CombatSolver;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
var module=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ExternalCardFixture"),AssemblyBuilderAccess.Run).DefineDynamicModule("External");
Type external=module.DefineType("ExternalCard",TypeAttributes.Public,typeof(CardModel)).CreateType()!;
CardModel Card(string id,CardType type,bool exhaust=false,bool shiv=false,bool mod=false) {
 CardModel c=mod?(CardModel)Activator.CreateInstance(external)!:new();
 c.Id.Entry=id;c.Type=type;c.Exhaust=exhaust;c.EnergyCost.Value=1;
 c.DynamicVars["Damage"]=new(7);c.DynamicVars["Block"]=new(4);c.DynamicVars["Cards"]=new(3);c.DynamicVars["Shivs"]=new(2);c.DynamicVars["Repeat"]=new(2);c.DynamicVars["Weak"]=new(1);
 if(shiv)c.Tags.Add(CardTag.Shiv);
 return c;
}
List<PredictedCard> cards=[
 new(Card("STRIKE",CardType.Attack)),new(Card("SHIV",CardType.Attack,true,true)),
 new(Card("SHIV",CardType.Attack,false,true)),new(Card("BLADE_DANCE",CardType.Skill)),
 new(Card("CLOAK_AND_DAGGER",CardType.Skill,true)),new(Card("FAN_OF_KNIVES",CardType.Power)),
 new(Card("BLADE_DANCE",CardType.Skill,true,mod:true)),new(Card("DEBUFF",CardType.Skill)),
 new(Card("STATUS",CardType.Status)),new(Card("CURSE",CardType.Curse))];
int cases=0;long oldReads=0,newReads=0;
void Check(IReadOnlyList<PredictedCard> deck,StrategicEffectRequirements mask,bool skillsExhaust,int enemy=90) {
 CardModel.KeywordReads=0;var a=Baseline.Build(deck,enemy,23,4,mask,skillsExhaust);oldReads+=CardModel.KeywordReads;
 CardModel.KeywordReads=0;var b=StrategicEffectContext.Build(deck,enemy,23,4,mask,skillsExhaust);newReads+=CardModel.KeywordReads;
 if(a!=b)throw new InvalidOperationException($"Context differs: {mask}/{skillsExhaust}: {a} / {b}");
 if(CardModel.KeywordReads>deck.Count)throw new InvalidOperationException("More than one keyword read per card");
 cases++;
}
for(int mask=0;mask<1<<16;mask++) foreach(bool exhaust in new[]{false,true})Check(cards,(StrategicEffectRequirements)mask,exhaust);
// The same cards change between invocations; there is no persistent cached keyword value.
foreach(var card in cards) {card.Preview.Exhaust=!card.Preview.Exhaust;card.Preview.Type=CardType.Skill;}
for(int mask=0;mask<1<<16;mask+=17)Check(cards,(StrategicEffectRequirements)mask,true,-1);
Check([], (StrategicEffectRequirements)65535,false);
Check([new(Card("STRIKE",CardType.Attack))],StrategicEffectRequirements.AttackHits,false);
if(CardModel.KeywordReads!=0)throw new InvalidOperationException("Unused keywords were read");
Console.WriteLine(JsonSerializer.Serialize(new {passed=true,cases,oldReads,newReads,scope="all context fields; all 65536 requirement masks; intrinsic exhaust versus skill exhaust, shivs, generators, external types, mutation between builds"}));
