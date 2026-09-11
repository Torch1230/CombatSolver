using CombatSolver;
using System.Text.Json;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static RunStatisticsRecord Run(int n, string outcome = "win", string participation = "full") => new(n.ToString("x32"), "a".PadLeft(32,'a'), n * 1000, outcome == "pending" ? null : n * 1000 + 500,
    "SILENT", 10, "test", participation, outcome, participation == "full", participation != "none", false, ["1:A"], ["1:A"], [], []);
var summary = RunStatisticsAggregation.Summarize([Run(1), Run(2), Run(3,"abandoned"), Run(4), Run(5,"win","partial"), Run(6),Run(7,"pending")]);
Check(summary == new RunStatisticsSummary(4,1,1,1,2,5,.8), "streak and abandonment semantics");
Check(RunStatisticsAggregation.Summarize([]).WinRate == null,"empty rate");
Check(RunStatisticsAggregation.Summarize([Run(1),Run(2,"pending"),Run(3)]).BestStreak==1,"unknown gap breaks streak");
string dir=Path.Combine(Path.GetTempPath(),"cs-run-statistics-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try {
    var store=new RunStatisticsStore(dir);
    store.Save(Run(1,"pending"));store.Save(Run(1));store.Save(Run(1));
    Check(store.Pending(20).Length==1,"repeated event dedup");
    Check(store.Snapshot(Run(1).ProfileId).Solver.Wins==1,"one run one win");
    var reopened=new RunStatisticsStore(dir);
    Check(reopened.Pending(20).Length==1,"offline queue survives restart");
    reopened.Acknowledge(Run(1));
    Check(new RunStatisticsStore(dir).Pending(20).Length==0,"receipt survives restart");
    var history=new HistoricalStatistics(Run(1).ProfileId,123,[new("SILENT",20,10,2,5)]);
    reopened.Import(history);reopened.Import(history with {CapturedAt=456});
    Check(new RunStatisticsStore(dir).Snapshot(history.ProfileId).Historical!.CapturedAt==123,"history is a separate first-import snapshot");
    reopened.Save(Run(2,"pending"));
    string native=Path.Combine(dir,"native");Directory.CreateDirectory(native);
    File.WriteAllText(Path.Combine(native,"2.run"),JsonSerializer.Serialize(new {start_time=2,was_abandoned=true,win=false,run_time=1}));
    reopened.Reconcile(history.ProfileId,native);
    Check(reopened.Find(Run(2).RunId)!.Outcome=="abandoned","recover native settlement after interrupted shutdown");
    Console.WriteLine("Run statistics contracts passed: streaks, gaps, abandonment, dedup, persistence, historical separation, recovery.");
} finally {Directory.Delete(dir,true);}
