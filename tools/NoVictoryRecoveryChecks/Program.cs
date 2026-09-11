using System.Diagnostics;
using CombatSolver;

OriginalChecks.Run();
int checks = 0;
void Require(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
var root = new CombatRootSnapshot(); var policy = new SearchPolicySnapshot();
SolverResult primary = new(false, 10);
SolverResult Execute(Func<SolverSearchProfile, Stopwatch, SolverResult> pass, SolverResult? start = null, Func<bool>? stop = null)
    => CombatSearchCoordinator.EscalateSearchWhenNoVictory(root,policy,SolverSearchProfile.Deep,Stopwatch.StartNew(),start ?? primary,pass,stop ?? (()=>false));
var adopted = new SolverResult(false, 20, SolverResultScope.RouteAdoption);
Require(ReferenceEquals(Execute((_,_)=>adopted), adopted), "Escalation discarded explicit route adoption.");
int calls=0;
var winner=new SolverResult(true,0);
Require(ReferenceEquals(Execute((_,_)=>{calls++;return winner;}),winner)&&calls==1,"Winner must stop escalation.");
calls=0;
Require(ReferenceEquals(Execute((_,_)=>{calls++;return winner;},winner),winner)&&calls==0,"Existing victory retried.");
Require(ReferenceEquals(Execute((_,_)=>throw new Exception("Stopped request ran"),stop:()=>true),primary),"Stopped request changed.");
calls=0;
Require(ReferenceEquals(Execute((_,_)=>{calls++;return new(false,11);}),primary)&&calls==1,"Worse result replaced original.");
calls=0;
var improved=Execute((_,_)=>new(false,10-++calls));
Require(calls==2&&improved.Quality==8,"Escalation count exceeded or lost improvement.");
var capped=SolverSearchProfile.Deep with {BeamWidth=300,MaxExpandedNodes=int.MaxValue,MaxCardBranchesPerNode=100,MaxPileChoiceBranchesPerAction=100,MaxHandChoiceBranchesPerAction=100};
Require(CombatSearchCoordinator.BuildNoVictoryEscalationProfile(capped,1,1,1)==null,"Identical saturated second pass repeated.");
var branchOnly=capped with {BeamWidth=512,MaxCardBranchesPerNode=10};
Require(CombatSearchCoordinator.BuildNoVictoryEscalationProfile(branchOnly,0,1,1)?.MaxCardBranchesPerNode==20,"Branch-only expansion was skipped.");
Console.WriteLine($"NO_VICTORY_RECOVERY_OK request_checks={checks}; original policy assertions passed");

namespace CombatSolver {
internal sealed class CombatRootSnapshot;
internal sealed class SearchPolicySnapshot {internal Sink Diagnostics {get;}=new();}
internal sealed class Sink {internal void Info(string message) {}}
internal enum SolverResultScope {SearchCompletion,RouteAdoption}
internal sealed record SolverResult(bool Won,int Quality,SolverResultScope ResultScope=SolverResultScope.SearchCompletion);
internal static partial class CombatSearchCoordinator {
 internal static bool IsCompleteVictory(SolverResult result)=>result.Won;
 internal static int CompareCompletedResultPrimaryQuality(CombatRootSnapshot root,SearchPolicySnapshot policy,SolverResult a,SolverResult b)=> a.Won!=b.Won?(a.Won?-1:1):a.Quality.CompareTo(b.Quality);
}
}
