using System.Diagnostics;
using CombatSolver;

OriginalChecks.Run();
int checks = 0;
void Require(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
var root = new CombatRootSnapshot(); var policy = new SearchPolicySnapshot();
var context = new SearchPassContext(policy, SolverSearchProfile.Default, Stopwatch.StartNew(), root);
SearchPassResult primary = new(new SolverResult(false, 10), 10);
SearchPassResult Execute(Func<SearchPassContext, SearchPassResult> pass, SearchPassResult? start = null, Func<bool>? stop = null)
    => CombatSearchCoordinator.EscalateSearchWhenNoVictory(context, start ?? primary, pass, stop ?? (()=>false));

// 入口/退出走 `with` 返回新对象，退出语义只对 Result/Quality 成员断言；候选被直接采纳时才返回原引用。
var adopted = new SearchPassResult(new(false, 20, SolverResultScope.RouteAdoption), 0);
int calls=0;
Require(ReferenceEquals(Execute(_=>{calls++;return adopted;}), adopted) && calls==1, "Route adoption candidate discarded.");
var winner = new SearchPassResult(new(true, 0), 0);
calls=0;
Require(Execute(_=>{calls++;return winner;}).Result == winner.Result && calls==1, "Winner must stop escalation.");
Require(Execute(_=>{throw new Exception("Existing victory ran a pass");}, winner).Result == winner.Result, "Existing victory retried.");
Require(Execute(_=>{throw new Exception("Stopped request ran");}, stop:()=>true).Result == primary.Result, "Stopped request changed.");
calls=0;
var worse = Execute(_=>{calls++;return new SearchPassResult(new(false,11), 11);});
Require(worse.Result == primary.Result && worse.Quality == primary.Quality && calls==1, "Worse result replaced original.");
calls=0;
var improved = Execute(_=>{int q=10-++calls;return new SearchPassResult(new(false,q),q);});
Require(calls==2 && improved.Result.Quality==8, "Escalation count exceeded or lost improvement.");
var capped=SolverSearchProfile.Default with {BeamWidth=300,MaxExpandedNodes=int.MaxValue,MaxCardBranchesPerNode=100,MaxPileChoiceBranchesPerAction=100,MaxHandChoiceBranchesPerAction=100};
Require(CombatSearchCoordinator.BuildNoVictoryEscalationProfile(capped,1,1,1)==null,"Identical saturated second pass repeated.");
var branchOnly=capped with {BeamWidth=512,MaxCardBranchesPerNode=10};
Require(CombatSearchCoordinator.BuildNoVictoryEscalationProfile(branchOnly,0,1,1)?.MaxCardBranchesPerNode==20,"Branch-only expansion was skipped.");
Console.WriteLine($"NO_VICTORY_RECOVERY_OK request_checks={checks}; original policy assertions passed");

namespace CombatSolver {
internal sealed class CombatRootSnapshot;
internal sealed class SearchPolicySnapshot {internal Sink Diagnostics {get;}=new();}
internal sealed class Sink {internal void Info(string message) {}}
internal sealed class ContextualRankingModel;
internal enum SolverResultScope {SearchCompletion,RouteAdoption}
internal sealed record SolverResult(bool Won,int Quality,SolverResultScope ResultScope=SolverResultScope.SearchCompletion);
internal sealed record SearchPassResult(SolverResult Result,int Quality,object? TakeoverResult=null,bool Settled=false);
internal sealed record SearchPassContext(SearchPolicySnapshot Policy,SolverSearchProfile Profile,Stopwatch Clock,CombatRootSnapshot Root);
internal static partial class CombatSearchCoordinator {
 internal static bool IsCompleteVictory(SolverResult result)=>result.Won;
 internal static int CompareCompletedResultPrimaryQuality(CombatRootSnapshot root,SearchPolicySnapshot policy,SolverResult a,SolverResult b)=> a.Won!=b.Won?(a.Won?-1:1):a.Quality.CompareTo(b.Quality);
}
}
