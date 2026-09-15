using CombatSolver;
int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
var w1 = new BfwsResearchNovelty(1, 1000);
Check(w1.Evaluate((0,0), ["a","b"]) == 1, "first generated state");
Check(w1.Evaluate((0,0), ["b","a","a"]) == 2, "fact order and duplicates irrelevant");
Check(w1.Evaluate((0,0), ["a","c"]) == 1, "unary new atom");
Check(w1.Evaluate((1,0), ["a","b"]) == 1, "partition by h");
Check(w1.Evaluate((0,1), ["a","b"]) == 1, "partition by potion label");
var w2 = new BfwsResearchNovelty(2, 1000);
Check(w2.Evaluate((0,0), ["a","b"]) == 1, "width2 first");
Check(w2.Evaluate((0,0), ["c","d"]) == 1, "width2 second");
Check(w2.Evaluate((0,0), ["b","c"]) == 2, "old facts new pair");
Check(w2.Evaluate((0,0), ["c","b"]) == 3, "pair canonical order");
Check(w2.Evaluate((0,0), ["a","b"]) == 3, "first novelty1 state records every pair");
Check(w2.Entries == 7, "exact total unary and pair count");
var cap = new BfwsResearchNovelty(2, 2);
cap.Evaluate((0,0), ["a","b","c"]);
Check(cap.LimitReached && cap.Entries == 2, "table cap explicit and hard");
var independent = new BfwsResearchNovelty(1, 1000);
Check(independent.Evaluate((0,0), ["a"]) == 1, "no cross run history");
var q = new PriorityQueue<string,(double,double,long)>();
q.Enqueue("familiar high heuristic",(2,-100,0)); q.Enqueue("new low heuristic",(1,0,1));
q.Enqueue("new better heuristic",(1,-10,2)); q.Enqueue("new equal later",(1,-10,3));
Check(q.Dequeue() == "new better heuristic", "w then h");
Check(q.Dequeue() == "new equal later", "stable sequence tie");
Check(q.Dequeue() == "new low heuristic", "novelty before heuristic");
Check(q.Dequeue() == "familiar high heuristic", "familiar state retained in open");
Check(!BfwsResearchNovelty.IsPrunedByWidth(3, 1, false), "unpruned BFWS retains familiar states");
Check(BfwsResearchNovelty.IsPrunedByWidth(2, 1, true), "k1 prunes above threshold");
Check(!BfwsResearchNovelty.IsPrunedByWidth(2, 2, true), "k2 retains new pair");
Check(BfwsResearchNovelty.IsPrunedByWidth(3, 2, true), "k2 prunes familiar");
Console.WriteLine($"Passed {checks} novelty and scheduling contracts.");

var rootAllowance = new BfwsEscapeBudget(2);
var siblingAllowance = rootAllowance;
Check(rootAllowance.TryAdmit(), "root admits first familiar child");
Check(siblingAllowance.TryAdmit(), "sibling consumes same ancestor allowance");
Check(!rootAllowance.TryAdmit(), "familiar grandchild cannot reset allowance");
var novelAllowance = new BfwsEscapeBudget(2);
Check(novelAllowance.TryAdmit() && novelAllowance.TryAdmit(), "novel child starts an independent allowance");
Check(!novelAllowance.TryAdmit() && !rootAllowance.TryAdmit(), "both exhausted independently");
Check(!new BfwsEscapeBudget(0).TryAdmit(), "zero allowance is strict width pruning");
Console.WriteLine("Passed 6 shared familiar-descendant allowance contracts.");

int comparisons = 0;
foreach (int width in new[] { 1, 2 })
foreach (int capacity in new[] { 0, 1, 7, 100, 1_000_000 })
{
    var random = new Random(8100 + width * 7 + capacity);
    var reference = new BfwsResearchNovelty(width, capacity);
    var packed = new BfwsPackedNovelty(width, capacity);
    var delta = new BfwsPackedNovelty(width, capacity);
    List<((int, int) Partition, BfwsFact[] Facts)> history = [];
    for (int step = 0; step < 1200; step++)
    {
        (int, int) partition = (random.Next(5), random.Next(2));
        List<BfwsFact> facts = [];
        if (history.Count > 0)
        {
            var parent = history[random.Next(history.Count)];
            delta.PrepareParent(parent.Partition, parent.Facts);
            if (random.Next(4) != 0) partition = parent.Partition;
            facts.AddRange(parent.Facts.Where(_ => random.Next(4) != 0));
        }
        for (int add = 0; add < random.Next(10); add++)
            facts.Add(new((BfwsFactKind)random.Next(7), random.Next(12), Key: "id" + random.Next(4), Value: random.Next(6), Detail: random.Next(4)));
        if (facts.Count > 0) facts.Add(facts[random.Next(facts.Count)]);
        facts = facts.OrderBy(_ => random.Next()).Take(30).ToList();
        BfwsFact[] frozen = facts.ToArray();
        int expected = reference.Evaluate(partition, facts.Select(f => $"{f.Kind}:{f.Slot}:{f.Key}:{f.Value}:{f.Detail}"));
        int actualPacked = packed.Evaluate(partition, frozen);
        int actualDelta = delta.Evaluate(partition, frozen);
        if (expected != actualPacked || expected != actualDelta || reference.Entries != packed.Entries
            || reference.Entries != delta.Entries || reference.AtomCount != delta.AtomCount
            || reference.Partitions != delta.Partitions || reference.LimitReached != delta.LimitReached
            || reference.Unary != delta.Unary || reference.Binary != delta.Binary || reference.Familiar != delta.Familiar)
            throw new Exception($"Packed/delta mismatch width={width}, capacity={capacity}, step={step}");
        history.Add((partition, frozen));
        comparisons++;
    }
    if (capacity == 1_000_000 && width == 2 && delta.PairVisits >= packed.PairVisits)
        throw new Exception("Parent tuples were not skipped in the unrestricted stream.");
}
Console.WriteLine($"Passed {comparisons} generated-state packed/delta comparisons against original novelty, including history caps and cross-partition parents.");

var frontier = new BfwsBoundedOpen<string>(2);
Check(!frontier.Enqueue("old", (1, 0, 0), out _), "first frontier admission");
Check(!frontier.Enqueue("worst", (2, -10, 1), out _), "frontier fills");
Check(frontier.Enqueue("new", (1, 0, 2), out string dropped) && dropped == "worst", "evict worst immediately");
Check(frontier.Count == 2 && frontier.Dequeue() == "old" && frontier.Dequeue() == "new", "bounded frontier preserves stable best order");
Check(!frontier.Enqueue("kept", (1, 0, 3), out _), "empty frontier reuse");
frontier.Enqueue("kept2", (1, 0, 4), out _);
Check(frontier.Enqueue("rejected", (3, 0, 5), out dropped) && dropped == "rejected", "inferior newcomer loses");
Check(frontier.Values.SequenceEqual(new[] { "kept", "kept2" }), "no evicted graph retained");
Console.WriteLine("Passed 7 bounded OPEN contracts.");

var shared = SolverSearchProfile.Default with { MaxExpandedNodes = 24000, SoftTimeBudgetMilliseconds = 10000 };
var scout = NoveltyPortfolioBudget.Default.Exploration(shared)!;
Check(scout == shared with { MaxExpandedNodes = 2500, SoftTimeBudgetMilliseconds = 5000 }, "scout changes only bounded request work");
Check(NoveltyPortfolioBudget.Default.Exploration(shared, actEndingBoss: true)!.SoftTimeBudgetMilliseconds == 2500, "boss preserves a larger Beam share");
Check(NoveltyPortfolioBudget.Default.Exploration(shared with { SoftTimeBudgetMilliseconds = 1999 }) == null, "short request keeps original Beam");
Check(NoveltyPortfolioBudget.Default.Exploration(shared with { MaxExpandedNodes = 999 }) == null, "small node request keeps original Beam");
Check(NoveltyPortfolioBudget.Default.Exploration(shared with { MaxExpandedNodes = 1000 })!.MaxExpandedNodes == 250, "scout cannot consume all nodes");
Check(NoveltyPortfolioBudget.Default.Exploration(shared with { SoftTimeBudgetMilliseconds = 120000 })!.SoftTimeBudgetMilliseconds == 5000, "long requests keep a bounded scout");
Check(NoveltyPortfolioBudget.Remaining(shared, 1100, 230) == shared with { SoftTimeBudgetMilliseconds = 8900, MaxExpandedNodes = 23770 }, "deduct actual work rather than reserved allowance");
Check(NoveltyPortfolioBudget.Remaining(shared, 10000, 1) == null && NoveltyPortfolioBudget.Remaining(shared, 10001, 1) == null, "no restarted deadline after a drained parent overshoots");
Check(NoveltyPortfolioBudget.Remaining(shared, 1, 24000) == null, "no restarted node budget");
Console.WriteLine("Passed 9 shared request budget contracts.");
