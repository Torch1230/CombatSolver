using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CombatSolver;
using CombatSolver.HotKernel;
using CombatSolver.HotSearch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace OfflineSearchHarness;

internal sealed class LockstepChoiceMismatchException(string message) : InvalidOperationException(message);

/// <summary>
/// 锁步选牌器：按 HotCombatEngine.Step 选出的 RecordedFuzzChoice 序列，
/// 为实机 sts2.dll 的 CardSelectCmd 精确喂回相同卡牌实例。
/// </summary>
internal sealed class LockstepCardSelector(
    IReadOnlyList<RecordedFuzzChoice> plannedChoices,
    IReadOnlyList<bool>? autoSkippable = null) : ICardSelector
{
    private readonly IReadOnlyList<RecordedFuzzChoice> _queue = plannedChoices;
    private readonly IReadOnlyList<bool>? _autoSkippable = autoSkippable;
    private int _cursor;

    public int ConsumedCount => _cursor;
    public Exception? LastError { get; private set; }

    private LockstepChoiceMismatchException Fail(string message)
    {
        LockstepChoiceMismatchException ex = new(message);
        LastError ??= ex;
        return ex;
    }

    private bool IsAutoSkippable(int index)
        => _autoSkippable != null && index >= 0 && index < _autoSkippable.Count && _autoSkippable[index];

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        List<CardModel> optList = options.ToList();

        // 原版 CardSelectCmd（FromHand / FromCombatPile / FromSimpleGrid / FromHandForUpgrade）在
        // !prefs.RequireManualConfirmation && eligibleCount <= prefs.MinSelect 时会直接自动返回全部候选而不调用 ICardSelector。
        // 当实机本次候选数大于 minSelect 时，队首标记为可自动跳过的强制窗口必然已被原版跳过；
        // 优先对齐到跳过前缀后的首个匹配窗口，避免同牌名误撞或 minSelect==0 时漏消耗队首。
        if (optList.Count > minSelect && IsAutoSkippable(_cursor))
        {
            int skipEnd = _cursor;
            while (skipEnd < _queue.Count && IsAutoSkippable(skipEnd))
                skipEnd++;

            if (skipEnd < _queue.Count
                && TryConsumeWindow(skipEnd, optList, minSelect, maxSelect, out List<CardModel> skippedPicked, out int skippedNext, out _))
            {
                _cursor = skippedNext;
                return Task.FromResult<IEnumerable<CardModel>>(skippedPicked);
            }
        }

        if (TryConsumeWindow(_cursor, optList, minSelect, maxSelect, out List<CardModel> picked, out int nextCursor, out string? error))
        {
            _cursor = nextCursor;
            return Task.FromResult<IEnumerable<CardModel>>(picked);
        }

        // 兜底对齐（镜像 DeploymentSoakRunner.PlanDrivenCardSelector）：
        // 若前置强制窗口被原版隐式自动选择跳过（包含后续窗口 RequireManualConfirmation 导致 optList.Count <= minSelect 的情形），
        // 跳过可自动跳过的队首条目并尝试命中当前候选窗口。
        if (_autoSkippable != null)
        {
            int candidateCursor = _cursor;
            while (candidateCursor < _queue.Count && IsAutoSkippable(candidateCursor))
            {
                candidateCursor++;
                if (candidateCursor < _queue.Count
                    && TryConsumeWindow(candidateCursor, optList, minSelect, maxSelect, out List<CardModel> alignedPicked, out int alignedNext, out _))
                {
                    _cursor = alignedNext;
                    return Task.FromResult<IEnumerable<CardModel>>(alignedPicked);
                }
            }
        }
        else if (_cursor + 1 < _queue.Count
            && _queue[_cursor].Kind != HotChoiceKind.GeneratedCard
            && _queue[_cursor].Kind != HotChoiceKind.None
            && TryConsumeWindow(_cursor + 1, optList, minSelect, maxSelect, out List<CardModel> fallbackPicked, out int fallbackNext, out _))
        {
            _cursor = fallbackNext;
            return Task.FromResult<IEnumerable<CardModel>>(fallbackPicked);
        }

        throw Fail(error ?? "LockstepCardSelector 选牌失败。");
    }

    private bool TryConsumeWindow(
        int startCursor,
        List<CardModel> optList,
        int minSelect,
        int maxSelect,
        out List<CardModel> picked,
        out int nextCursor,
        out string? error)
    {
        picked = new List<CardModel>(Math.Min(maxSelect, optList.Count));
        HashSet<CardModel> pickedSet = new(ReferenceEqualityComparer.Instance);
        int cursor = startCursor;

        while (picked.Count < maxSelect && cursor < _queue.Count)
        {
            RecordedFuzzChoice spec = _queue[cursor];
            if (spec.Kind == HotChoiceKind.None)
            {
                cursor++;
                break;
            }

            CardModel? match = ResolveOption(spec, optList, pickedSet);
            if (match == null)
            {
                // 若当前已满足 minSelect，可能是下一个独立选牌窗口的选项，停止消费本轮
                if (picked.Count >= minSelect)
                    break;

                string available = string.Join(", ", optList.Select(static c => $"{c.Id.Entry}+{c.CurrentUpgradeLevel}"));
                nextCursor = cursor;
                error =
                    $"LockstepCardSelector 无法在选项 [{available}] 中找到 {spec.Kind}:{spec.CardEntry}+{spec.UpgradeLevel}#opt{spec.OptionOccurrence} " +
                    $"(genIdx={spec.GeneratedOptionIndex}, min={minSelect}, max={maxSelect}, cursor={cursor}/{_queue.Count})。";
                return false;
            }

            picked.Add(match);
            pickedSet.Add(match);
            cursor++;
        }

        if (picked.Count < minSelect)
        {
            string available = string.Join(", ", optList.Select(static c => $"{c.Id.Entry}+{c.CurrentUpgradeLevel}"));
            nextCursor = cursor;
            error =
                $"LockstepCardSelector 选出 {picked.Count} 张牌，少于要求最小数 {minSelect} " +
                $"(max={maxSelect}, cursor={cursor}/{_queue.Count}, options=[{available}])。";
            return false;
        }

        nextCursor = cursor;
        error = null;
        return true;
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
        => new() { card = options.Count > 0 ? options[0].Card : null };

    private static CardModel? ResolveOption(
        RecordedFuzzChoice spec,
        List<CardModel> optList,
        HashSet<CardModel> pickedSet)
    {
        if (spec.Kind == HotChoiceKind.GeneratedCard)
        {
            if (spec.GeneratedOptionIndex >= 0 && spec.GeneratedOptionIndex < optList.Count)
            {
                CardModel byIdx = optList[spec.GeneratedOptionIndex];
                if (!pickedSet.Contains(byIdx)
                    && string.Equals(byIdx.Id.Entry, spec.CardEntry, StringComparison.Ordinal))
                {
                    return byIdx;
                }
            }

            foreach (CardModel c in optList)
            {
                if (!pickedSet.Contains(c) && string.Equals(c.Id.Entry, spec.CardEntry, StringComparison.Ordinal))
                    return c;
            }
            return null;
        }

        int targetOcc = Math.Max(0, spec.OptionOccurrence);

        // 1. 精确 StateKey + OptionOccurrence 匹配（按完整 optList 计次，与 CardSelectCmd 多选语义一致）
        if (!string.IsNullOrEmpty(spec.StateKey))
        {
            int seen = 0;
            CardModel? firstUnpicked = null;
            foreach (CardModel c in optList)
            {
                if (!string.Equals(HotImportSupport.FormatLiveChoiceCardKey(c), spec.StateKey, StringComparison.Ordinal))
                    continue;
                if (seen == targetOcc && !pickedSet.Contains(c))
                    return c;
                if (firstUnpicked == null && !pickedSet.Contains(c))
                    firstUnpicked = c;
                seen++;
            }
            if (firstUnpicked != null)
                return firstUnpicked;
        }

        // 2. 回退到 (CardEntry, UpgradeLevel) + OptionOccurrence 匹配
        {
            int seen = 0;
            CardModel? firstUnpicked = null;
            foreach (CardModel c in optList)
            {
                if (!string.Equals(c.Id.Entry, spec.CardEntry, StringComparison.Ordinal)
                    || c.CurrentUpgradeLevel != spec.UpgradeLevel)
                {
                    continue;
                }
                if (seen == targetOcc && !pickedSet.Contains(c))
                    return c;
                if (firstUnpicked == null && !pickedSet.Contains(c))
                    firstUnpicked = c;
                seen++;
            }
            if (firstUnpicked != null)
                return firstUnpicked;
        }

        // 3. 最终仅按 CardEntry 匹配（例如状态刚变更前后的升级/费用差异）
        foreach (CardModel c in optList)
        {
            if (pickedSet.Contains(c))
                continue;
            if (string.Equals(c.Id.Entry, spec.CardEntry, StringComparison.Ordinal))
                return c;
        }

        return null;
    }
}

/// <summary>
/// Layer 2 覆盖引导单人差分模糊引擎（<c>--fuzz-combat</c> 与 <c>--replay-fuzz</c>）：
/// 在每个动作之后（而非仅回合末）对比 HotCombatEngine.Step 与真实 sts2.dll 的完整 ContinuationStamp
/// 与 TerminalOutcome，按 (ActionEntityId, FirstDiffSection, ActionKind, ExceptionType) 归桶去重，
/// 并把每个桶的最小可复现 JSON 落盘到 .local/fuzz-failures/。
/// </summary>
internal static class CombatDiffFuzzer
{
    private sealed class BucketRecord
    {
        public required string BucketKey { get; init; }
        public required bool IsCrash { get; init; }
        public required string ActionEntityId { get; init; }
        public required string FirstDiffSection { get; init; }
        public required string ActionKind { get; init; }
        public required string ExceptionType { get; init; }
        public int Count { get; set; }
        public required string FirstDiffLine { get; set; }
        public required string SavedPath { get; set; }
        public int SavedSteps { get; set; }
    }

    private sealed class CoverageTracker
    {
        public HashSet<string> Cards { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ExecutedCards { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Powers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Relics { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Potions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ExecutedPotions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Monsters { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Enchantments { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Afflictions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Orbs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Dominions { get; } = new(StringComparer.Ordinal);

        public void ObserveCombat(CombatState combat)
        {
            Player? player = LocalContext.GetMe(combat);
            if (player == null)
                return;

            foreach (RelicModel r in player.Relics)
                Relics.Add(r.Id.Entry);
            foreach (PotionModel? p in player.PotionSlots)
            {
                if (p != null)
                    Potions.Add(p.Id.Entry);
            }
            if (combat.RunState.Dominion is { } dom)
                Dominions.Add(dom.Id.Entry);

            if (player.PlayerCombatState is { } pcs)
            {
                foreach (CardModel c in pcs.AllCards)
                {
                    Cards.Add(c.Id.Entry);
                    if (c.Enchantment is { } e) Enchantments.Add(e.Id.Entry);
                    if (c.Affliction is { } a) Afflictions.Add(a.Id.Entry);
                }
                foreach (OrbModel o in pcs.OrbQueue.Orbs)
                    Orbs.Add(o.Id.Entry);
            }

            foreach (PowerModel pw in player.Creature.Powers)
                Powers.Add(pw.Id.Entry);

            foreach (Creature enemy in combat.Enemies)
            {
                if (enemy.Monster is { } m)
                    Monsters.Add(m.Id.Entry);
                foreach (PowerModel pw in enemy.Powers)
                    Powers.Add(pw.Id.Entry);
            }
        }
    }

    public static int RunFuzz(string[] args)
    {
        int fights = 120;
        int seed = 2026;
        string phase = "all";
        int maxTurns = 8;
        string repoRoot = ResolveRepoRoot();
        string dumpDir = Path.Combine(repoRoot, ".local", "fuzz-failures");

        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string NextVal() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"--fuzz-combat 选项 {key} 缺少参数。");
            switch (key)
            {
                case "--fights": fights = Math.Max(1, int.Parse(NextVal())); break;
                case "--seed": seed = int.Parse(NextVal()); break;
                case "--phase": phase = NextVal().ToLowerInvariant(); break;
                case "--max-turns": maxTurns = Math.Max(1, int.Parse(NextVal())); break;
                case "--dump-dir": dumpDir = Path.GetFullPath(NextVal()); break;
                default: throw new ArgumentException($"--fuzz-combat 未知选项：{key}");
            }
        }
        if (phase is not ("all" or "sweep" or "synergy" or "high-risk"))
            throw new ArgumentException("--phase 只支持 all|sweep|synergy|high-risk。");

        Directory.CreateDirectory(dumpDir);

        Stopwatch sw = Stopwatch.StartNew();
        Console.WriteLine($"=== --fuzz-combat (fights={fights}, seed={seed}, phase={phase}, maxTurns={maxTurns}, dumpDir={dumpDir}) ===");

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        GameBootstrap.EnsureBootstrapped(loop);

        List<FuzzScenarioSpec> scenarios = FuzzScenarioGenerator.Generate(fights, seed, phase, maxTurns);
        CoverageTracker coverage = new();
        Dictionary<string, BucketRecord> buckets = new(StringComparer.Ordinal);
        HotExecScratch scratch = new();
        HotAction[] legalBuf = new HotAction[128];
        HotChoiceSelection[] choiceOptBuf = new HotChoiceSelection[64];

        int fightsRun = 0;
        int passFights = 0;
        long totalSteps = 0;

        try
        {
            for (int f = 0; f < scenarios.Count; f++)
            {
                FuzzScenarioSpec spec = scenarios[f];
                fightsRun++;
                bool passed = RunSingleFight(
                    loop, spec, replaySteps: null, coverage, buckets, dumpDir,
                    scratch, legalBuf, choiceOptBuf, verboseReplay: false, out int stepsExecuted);
                totalSteps += stepsExecuted;
                if (passed)
                    passFights++;

                if ((f + 1) % 20 == 0 || f + 1 == scenarios.Count)
                {
                    int diffCount = buckets.Values.Count(static b => !b.IsCrash);
                    int crashCount = buckets.Values.Count(static b => b.IsCrash);
                    Console.WriteLine(
                        $"[fuzz] {f + 1}/{scenarios.Count} fights | steps={totalSteps} | pass={passFights} | " +
                        $"diff_buckets={diffCount} | crash_buckets={crashCount} | " +
                        $"covered(C={coverage.Cards.Count},P={coverage.Powers.Count},R={coverage.Relics.Count}," +
                        $"Pot={coverage.Potions.Count},M={coverage.Monsters.Count},E={coverage.Enchantments.Count}," +
                        $"A={coverage.Afflictions.Count},D={coverage.Dominions.Count}) | {sw.Elapsed.TotalSeconds:F1}s");
                }
            }
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
        }

        int diffBuckets = buckets.Values.Count(static b => !b.IsCrash);
        int crashBuckets = buckets.Values.Count(static b => b.IsCrash);

        Console.WriteLine();
        Console.WriteLine("=== --fuzz-combat SUMMARY ===");
        Console.WriteLine($"fights_run={fightsRun} pass_fights={passFights} total_steps={totalSteps} elapsed={sw.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine(
            $"unique_entities_covered: Cards={coverage.Cards.Count} (played={coverage.ExecutedCards.Count}), " +
            $"Powers={coverage.Powers.Count}, Relics={coverage.Relics.Count}, " +
            $"Potions={coverage.Potions.Count} (used={coverage.ExecutedPotions.Count}), " +
            $"Monsters={coverage.Monsters.Count}, Enchantments={coverage.Enchantments.Count}, " +
            $"Afflictions={coverage.Afflictions.Count}, Orbs={coverage.Orbs.Count}, Dominions={coverage.Dominions.Count}");
        Console.WriteLine($"diff_buckets={diffBuckets} crash_buckets={crashBuckets}");

        if (buckets.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Top failure buckets:");
            foreach (BucketRecord b in buckets.Values
                .OrderByDescending(static x => x.Count)
                .ThenBy(static x => x.BucketKey, StringComparer.Ordinal)
                .Take(30))
            {
                string kindTag = b.IsCrash ? "CRASH" : "DIFF";
                Console.WriteLine($"  [{kindTag}] x{b.Count} {b.BucketKey}");
                Console.WriteLine($"         first_diff: {b.FirstDiffLine}");
                Console.WriteLine($"         replay:     --replay-fuzz \"{b.SavedPath}\"");
            }
        }

        // 保存总览摘要 JSON
        string summaryPath = Path.Combine(dumpDir, "_fuzz-summary.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(new
        {
            fights_run = fightsRun,
            pass_fights = passFights,
            total_steps = totalSteps,
            elapsed_seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
            unique_entities_covered = new
            {
                cards = coverage.Cards.Count,
                cards_played = coverage.ExecutedCards.Count,
                powers = coverage.Powers.Count,
                relics = coverage.Relics.Count,
                potions = coverage.Potions.Count,
                potions_used = coverage.ExecutedPotions.Count,
                monsters = coverage.Monsters.Count,
                enchantments = coverage.Enchantments.Count,
                afflictions = coverage.Afflictions.Count,
                orbs = coverage.Orbs.Count,
                dominions = coverage.Dominions.Count,
            },
            diff_buckets = diffBuckets,
            crash_buckets = crashBuckets,
            buckets = buckets.Values
                .OrderByDescending(static x => x.Count)
                .Select(static x => new
                {
                    x.BucketKey,
                    x.IsCrash,
                    x.Count,
                    x.ActionEntityId,
                    x.FirstDiffSection,
                    x.ActionKind,
                    x.ExceptionType,
                    x.FirstDiffLine,
                    x.SavedPath,
                })
                .ToArray(),
        }, FuzzScenarioGenerator.JsonOptions));

        return (diffBuckets == 0 && crashBuckets == 0) ? 0 : 1;
    }

    public static int RunReplay(string jsonPath)
    {
        string fullPath = Path.GetFullPath(jsonPath);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"找不到回放文件：{fullPath}");
            return 2;
        }

        FuzzFailureArtifact artifact = JsonSerializer.Deserialize<FuzzFailureArtifact>(
            File.ReadAllText(fullPath), FuzzScenarioGenerator.JsonOptions)
            ?? throw new InvalidDataException($"回放 JSON 为空：{fullPath}");

        Stopwatch sw = Stopwatch.StartNew();
        Console.WriteLine($"=== --replay-fuzz {Path.GetFileName(fullPath)} ===");
        Console.WriteLine($"Original bucket: {artifact.BucketKey}");
        Console.WriteLine($"Original diff:   {artifact.FirstDiffLine}");
        Console.WriteLine($"Scenario:        {artifact.Scenario.Tag} ({artifact.Scenario.Setup.CharacterId} vs {artifact.Scenario.Setup.EncounterId}, mode={artifact.Scenario.Setup.GameMode}, seed={artifact.Scenario.Setup.Seed})");
        Console.WriteLine($"Recorded steps:  {artifact.Steps.Length}");

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        GameBootstrap.EnsureBootstrapped(loop);

        CoverageTracker coverage = new();
        Dictionary<string, BucketRecord> buckets = new(StringComparer.Ordinal);
        HotExecScratch scratch = new();
        HotAction[] legalBuf = new HotAction[128];
        HotChoiceSelection[] choiceOptBuf = new HotChoiceSelection[64];

        try
        {
            bool passed = RunSingleFight(
                loop, artifact.Scenario, artifact.Steps, coverage, buckets, dumpDir: null,
                scratch, legalBuf, choiceOptBuf, verboseReplay: true, out int stepsExecuted);

            if (passed)
            {
                Console.WriteLine($"[PASS] Replay completed {stepsExecuted} steps with 0 diffs and 0 crashes in {sw.ElapsedMilliseconds} ms (original issue fixed!).");
                return 0;
            }

            Console.WriteLine($"[REPRODUCED] Replay failed after {stepsExecuted} steps in {sw.ElapsedMilliseconds} ms.");
            foreach (BucketRecord b in buckets.Values)
            {
                Console.WriteLine($"  bucket={b.BucketKey}");
                Console.WriteLine($"  detail={b.FirstDiffLine}");
            }
            return 1;
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
        }
    }

    private static bool RunSingleFight(
        MainLoopContext loop,
        FuzzScenarioSpec spec,
        RecordedFuzzAction[]? replaySteps,
        CoverageTracker coverage,
        Dictionary<string, BucketRecord> buckets,
        string? dumpDir,
        HotExecScratch scratch,
        HotAction[] legalBuf,
        HotChoiceSelection[] choiceOptBuf,
        bool verboseReplay,
        out int stepsExecuted)
    {
        stepsExecuted = 0;
        List<RecordedFuzzAction> recorded = [];
        string lastEntityId = "START_COMBAT";
        string lastActionKind = "Setup";

        try
        {
            CombatState combat = RealCombatDriver.StartCombat(loop, spec.Setup);
            coverage.ObserveCombat(combat);
            if (RealCombatDriver.GetLiveOutcome(combat) != HotCombatOutcome.Ongoing)
                return true;

            HotRootBundle bundle = HotRootBuilder.BuildFromLive(combat);
            CombatHotState hot = bundle.RootState;
            HotColdSidecar? hotCold = bundle.RootCold;
            HotRules rules = bundle.Rules;

            // 首帧根捕获自洽检查
            string rootHotStamp = HotContinuationStamp.CaptureFromHot(in hot, hotCold, rules);
            string rootLiveStamp = HotContinuationStamp.StripModStampSections(ContinuationStamp.CaptureLive(combat).StateText);
            if (!string.Equals(rootHotStamp, rootLiveStamp, StringComparison.Ordinal))
            {
                RecordDiffBucket(buckets, dumpDir, spec, recorded, "ROOT_CAPTURE", "RootCapture",
                    rootHotStamp, rootLiveStamp, HotCombatOutcome.Ongoing, HotCombatOutcome.Ongoing, verboseReplay);
                return false;
            }

            Random rng = new(spec.ActionRngSeed);
            int maxSteps = replaySteps != null ? replaySteps.Length : Math.Max(20, spec.MaxTurns * 20);
            int actionsThisTurn = 0;
            int lastTurnNumber = hot.PlayerTurnNumber;

            for (int step = 0; step < maxSteps; step++)
            {
                if (hot.TerminalOutcome != HotCombatOutcome.Ongoing)
                    break;
                if (replaySteps == null && hot.PlayerTurnNumber > spec.MaxTurns)
                    break;

                if (hot.PlayerTurnNumber != lastTurnNumber)
                {
                    lastTurnNumber = hot.PlayerTurnNumber;
                    actionsThisTurn = 0;
                }

                HotParallelExpander.AssignDeterministicCardUids(ref hot);
                int legalCount = HotCombatEngine.EnumerateLegalActions(in hot, hotCold, rules, legalBuf, includePotions: true);
                if (legalCount == 0)
                    break;

                HotAction chosen;
                RecordedFuzzChoice[] chosenSpecs;
                bool[] autoSkippableChoices;
                CombatHotState nextHot;
                HotColdSidecar? nextCold;
                string entityId;

                if (replaySteps != null)
                {
                    RecordedFuzzAction rec = replaySteps[step];
                    chosen = new HotAction(rec.Kind, (byte)rec.SlotOrHandIdx, (sbyte)rec.TargetEnemySlot);
                    entityId = rec.EntityId;
                    ResolveReplayChoices(in hot, hotCold, rules, scratch, chosen, rec.Choices, choiceOptBuf,
                        out nextHot, out nextCold, out chosenSpecs, out autoSkippableChoices);
                }
                else
                {
                    chosen = PickCoverageGuidedAction(
                        in hot, rules, legalBuf.AsSpan(0, legalCount), actionsThisTurn, coverage, rng);
                    ResolveRandomChoices(
                        in hot, hotCold, rules, scratch, ref chosen, choiceOptBuf, coverage, rng,
                        out nextHot, out nextCold, out chosenSpecs, out autoSkippableChoices);
                    entityId = DescribeActionEntity(in hot, rules, chosen);
                }

                lastEntityId = entityId;
                lastActionKind = chosen.Kind.ToString();

                // 在实机推进前把 HotAction 绑定到真实 CardModel / PotionModel / Creature 目标
                Player player = LocalContext.GetMe(combat)!;
                RealCombatAction liveAction = BindToLiveAction(combat, player, in hot, rules, chosen, entityId);

                RecordedFuzzAction stepRecord = new(
                    step, hot.PlayerTurnNumber, chosen.Kind, chosen.SlotOrHandIdx, chosen.TargetEnemySlot,
                    entityId, chosenSpecs);
                recorded.Add(stepRecord);

                if (verboseReplay)
                {
                    string chDesc = chosenSpecs.Length == 0
                        ? ""
                        : $" choices=[{string.Join(",", chosenSpecs.Select(static c => c.Kind is HotChoiceKind.None or HotChoiceKind.GeneratedCard ? $"{c.Kind}:{c.CardEntry}+{c.UpgradeLevel}" : $"{c.Kind}:{c.CardEntry}+{c.UpgradeLevel}#opt{c.OptionOccurrence}"))}]";
                    Console.WriteLine($"  step {step}: T{hot.PlayerTurnNumber} {chosen.Kind} {entityId} slot={chosen.SlotOrHandIdx} target={chosen.TargetEnemySlot}{chDesc}");
                }

                // 同步在实机 sts2.dll 上执行同一个动作与相同选牌
                HotCombatOutcome liveOutcome;
                LockstepCardSelector lockstepSelector = new(chosenSpecs, autoSkippableChoices);
                using (RunManager.Instance.CardSelectorStack.PushSelector(player, lockstepSelector))
                {
                    liveOutcome = RealCombatDriver.RunRealCombatAction(
                        loop, combat, liveAction, externalErrorProbe: () => lockstepSelector.LastError);
                }

                hot = nextHot;
                hotCold = nextCold;
                stepsExecuted++;
                actionsThisTurn++;

                if (chosen.Kind == HotActionKind.PlayCard)
                    coverage.ExecutedCards.Add(entityId);
                else if (chosen.Kind == HotActionKind.UsePotion)
                    coverage.ExecutedPotions.Add(entityId);
                coverage.ObserveCombat(combat);

                // 动作后逐项比对 TerminalOutcome 与 ContinuationStamp
                if (hot.TerminalOutcome != liveOutcome)
                {
                    RecordOutcomeBucket(buckets, dumpDir, spec, recorded, entityId, chosen.Kind.ToString(),
                        hot.TerminalOutcome, liveOutcome, verboseReplay);
                    return false;
                }

                if (liveOutcome == HotCombatOutcome.Ongoing)
                {
                    string hotStamp = HotContinuationStamp.CaptureFromHot(in hot, hotCold, rules);
                    string liveStamp = HotContinuationStamp.StripModStampSections(
                        ContinuationStamp.CaptureLive(combat).StateText);
                    if (!string.Equals(hotStamp, liveStamp, StringComparison.Ordinal))
                    {
                        RecordDiffBucket(buckets, dumpDir, spec, recorded, entityId, chosen.Kind.ToString(),
                            hotStamp, liveStamp, hot.TerminalOutcome, liveOutcome, verboseReplay);
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Exception root = Unwrap(ex);
            RecordCrashBucket(buckets, dumpDir, spec, recorded, lastEntityId, lastActionKind, root, verboseReplay);
            return false;
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
        }
    }

    private static HotAction PickCoverageGuidedAction(
        in CombatHotState hot,
        HotRules rules,
        ReadOnlySpan<HotAction> legal,
        int actionsThisTurn,
        CoverageTracker coverage,
        Random rng)
    {
        // 每回合最多出 14 个非 EndTurn 动作，避免 0 费无限循环导致卡死在单回合
        if (actionsThisTurn >= 14)
        {
            for (int i = 0; i < legal.Length; i++)
            {
                if (legal[i].Kind == HotActionKind.EndTurn)
                    return legal[i];
            }
        }

        // 若存在未执行过的药水或卡牌，高概率优先探索以最大化实体覆盖
        List<int> unseenPotionIdx = [];
        List<int> unseenCardIdx = [];
        List<int> potionIdx = [];
        List<int> cardIdx = [];
        int endTurnIdx = -1;

        for (int i = 0; i < legal.Length; i++)
        {
            HotAction a = legal[i];
            switch (a.Kind)
            {
                case HotActionKind.UsePotion:
                    potionIdx.Add(i);
                    string potEntry = DescribeActionEntity(in hot, rules, a);
                    if (!coverage.ExecutedPotions.Contains(potEntry))
                        unseenPotionIdx.Add(i);
                    break;
                case HotActionKind.PlayCard:
                    cardIdx.Add(i);
                    string cardEntry = DescribeActionEntity(in hot, rules, a);
                    if (!coverage.ExecutedCards.Contains(cardEntry))
                        unseenCardIdx.Add(i);
                    break;
                case HotActionKind.EndTurn:
                    endTurnIdx = i;
                    break;
            }
        }

        if (unseenPotionIdx.Count > 0 && rng.NextDouble() < 0.65)
            return legal[unseenPotionIdx[rng.Next(unseenPotionIdx.Count)]];
        if (unseenCardIdx.Count > 0 && rng.NextDouble() < 0.75)
            return legal[unseenCardIdx[rng.Next(unseenCardIdx.Count)]];

        double roll = rng.NextDouble();
        if (potionIdx.Count > 0 && roll < 0.28)
            return legal[potionIdx[rng.Next(potionIdx.Count)]];
        if (cardIdx.Count > 0 && (endTurnIdx < 0 || roll < 0.88))
            return legal[cardIdx[rng.Next(cardIdx.Count)]];
        if (endTurnIdx >= 0)
            return legal[endTurnIdx];
        return legal[rng.Next(legal.Length)];
    }

    private readonly record struct ChoiceWindowMeta(
        ushort GroupSeq,
        bool IsOptional,
        bool IsGenerated,
        int RawEligibleCount);

    private static int ComputeRawEligibleChoiceCount(
        HotChoiceSelection[] choiceOptBuf,
        ReadOnlySpan<byte> classMultBuf,
        int optCount)
    {
        int rawEligible = HotCombatEngine.GetPendingExcludedChoiceUids().Length;
        for (int i = 0; i < optCount; i++)
        {
            if (choiceOptBuf[i].Kind != HotChoiceKind.None)
                rawEligible += classMultBuf[i];
        }
        return rawEligible;
    }

    private static ChoiceWindowMeta CaptureChoiceWindowMeta(
        HotExecScratch scratch,
        HotChoiceSelection[] choiceOptBuf,
        ReadOnlySpan<byte> classMultBuf,
        int optCount)
    {
        bool isOptional = scratch.PendingChoiceMin == 0
            || (scratch.PendingChoiceGroupFlags & HotChoiceGroupFlags.Optional) != 0;
        bool isGenerated = scratch.PendingChoiceKind is HotChoiceKind.GeneratedCard or HotChoiceKind.None;
        int rawEligible = isGenerated
            ? 0
            : ComputeRawEligibleChoiceCount(choiceOptBuf, classMultBuf, optCount);
        return new ChoiceWindowMeta(scratch.PendingChoiceGroupSeq, isOptional, isGenerated, rawEligible);
    }

    private static bool[] BuildAutoSkippableFlags(
        IReadOnlyList<RecordedFuzzChoice> specs,
        IReadOnlyList<ChoiceWindowMeta> metas)
    {
        bool[] flags = new bool[specs.Count];
        int i = 0;
        while (i < specs.Count)
        {
            ChoiceWindowMeta first = metas[i];
            int j = i + 1;
            while (j < specs.Count && first.GroupSeq != 0 && metas[j].GroupSeq == first.GroupSeq)
                j++;

            int pickedNonNone = 0;
            for (int k = i; k < j; k++)
            {
                if (specs[k].Kind != HotChoiceKind.None)
                    pickedNonNone++;
            }

            // 原版 CardSelectCmd（FromHand / FromCombatPile / FromSimpleGrid / FromHandForUpgrade）在
            // !prefs.RequireManualConfirmation && eligibleCount <= prefs.MinSelect 时自动返回全部候选而不调 ICardSelector。
            if (!first.IsOptional
                && !first.IsGenerated
                && pickedNonNone > 0
                && first.RawEligibleCount > 0
                && first.RawEligibleCount <= pickedNonNone)
            {
                for (int k = i; k < j; k++)
                    flags[k] = true;
            }

            i = j;
        }
        return flags;
    }

    private static void ResolveRandomChoices(
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        ref HotAction action,
        HotChoiceSelection[] choiceOptBuf,
        CoverageTracker coverage,
        Random rng,
        out CombatHotState nextHot,
        out HotColdSidecar? nextCold,
        out RecordedFuzzChoice[] recordedChoices,
        out bool[] autoSkippableChoices)
    {
        List<HotChoiceSelection> selections = [];
        List<RecordedFuzzChoice> specs = [];
        List<ChoiceWindowMeta> windowMetas = [];
        HashSet<(HotChoiceKind Kind, ushort Uid)> pickedInAction = [];
        byte[] classMultBuf = new byte[choiceOptBuf.Length];

        while (true)
        {
            CombatHotState probe = state;
            HotColdSidecar? probeCold = cold;
            HotStepResult res = HotCombatEngine.Step(
                ref probe, ref probeCold, rules, scratch, action,
                CollectionsMarshal.AsSpan(selections));

            if (!res.NeedsChoice)
            {
                if (!res.Executed)
                {
                    throw new InvalidOperationException(
                        $"HotCombatEngine.Step returned Executed=false for {action.Kind} slot={action.SlotOrHandIdx} target={action.TargetEnemySlot}.");
                }
                nextHot = probe;
                nextCold = probeCold;
                recordedChoices = specs.ToArray();
                autoSkippableChoices = BuildAutoSkippableFlags(specs, windowMetas);
                return;
            }

            if (selections.Count >= HotLimits.MaxChoicesPerAction)
            {
                // 超出单动作选牌上限则切回 EndTurn 避免死循环
                action = HotAction.EndTurn();
                selections.Clear();
                specs.Clear();
                windowMetas.Clear();
                pickedInAction.Clear();
                continue;
            }

            int optCount = HotCombatEngine.EnumerateChoiceOptionsWithClasses(
                in probe, probeCold, rules, scratch, choiceOptBuf, classMultBuf);
            if (optCount == 0)
            {
                if (scratch.PendingChoiceMin == 0)
                {
                    selections.Add(new HotChoiceSelection(HotChoiceKind.None, 0, 0));
                    specs.Add(new RecordedFuzzChoice(HotChoiceKind.None, string.Empty, 0, string.Empty, -1, 0));
                    windowMetas.Add(new ChoiceWindowMeta(scratch.PendingChoiceGroupSeq, IsOptional: true, IsGenerated: true, RawEligibleCount: 0));
                    continue;
                }
                action = HotAction.EndTurn();
                selections.Clear();
                specs.Clear();
                windowMetas.Clear();
                pickedInAction.Clear();
                continue;
            }

            ChoiceWindowMeta windowMeta = CaptureChoiceWindowMeta(scratch, choiceOptBuf, classMultBuf, optCount);

            // 过滤已在本动作内选过的同 UID 牌（多选弹窗防止重复选同一张牌）
            List<int> validIndices = new(optCount);
            List<int> unseenIndices = [];
            for (int i = 0; i < optCount; i++)
            {
                HotChoiceSelection opt = choiceOptBuf[i];
                if (opt.Kind != HotChoiceKind.None && opt.Kind != HotChoiceKind.GeneratedCard
                    && pickedInAction.Contains((opt.Kind, opt.CardUidOrIndex)))
                {
                    continue;
                }
                validIndices.Add(i);
                if (opt.CardDefId > 0 && opt.CardDefId < rules.Cards.Length
                    && !coverage.ExecutedCards.Contains(rules.Cards[opt.CardDefId].Entry))
                {
                    unseenIndices.Add(i);
                }
            }

            int pickIdx;
            if (validIndices.Count == 0)
            {
                pickIdx = 0;
            }
            else if (unseenIndices.Count > 0 && rng.NextDouble() < 0.6)
            {
                pickIdx = unseenIndices[rng.Next(unseenIndices.Count)];
            }
            else
            {
                pickIdx = validIndices[rng.Next(validIndices.Count)];
            }

            HotChoiceSelection chosenOpt = choiceOptBuf[pickIdx];
            selections.Add(chosenOpt);
            if (chosenOpt.Kind != HotChoiceKind.None && chosenOpt.Kind != HotChoiceKind.GeneratedCard)
                pickedInAction.Add((chosenOpt.Kind, chosenOpt.CardUidOrIndex));

            specs.Add(CaptureChoiceSpec(in probe, probeCold, rules, scratch, chosenOpt));
            windowMetas.Add(windowMeta);
        }
    }

    private static void ResolveReplayChoices(
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        HotAction action,
        RecordedFuzzChoice[] recordedChoices,
        HotChoiceSelection[] choiceOptBuf,
        out CombatHotState nextHot,
        out HotColdSidecar? nextCold,
        out RecordedFuzzChoice[] usedChoices,
        out bool[] autoSkippableChoices)
    {
        List<HotChoiceSelection> selections = [];
        List<RecordedFuzzChoice> usedSpecs = new(recordedChoices.Length);
        List<ChoiceWindowMeta> windowMetas = new(recordedChoices.Length);
        byte[] classMultBuf = new byte[choiceOptBuf.Length];
        int recCursor = 0;

        while (true)
        {
            CombatHotState probe = state;
            HotColdSidecar? probeCold = cold;
            HotStepResult res = HotCombatEngine.Step(
                ref probe, ref probeCold, rules, scratch, action,
                CollectionsMarshal.AsSpan(selections));

            if (!res.NeedsChoice)
            {
                if (!res.Executed)
                    throw new InvalidOperationException($"回放动作 {action.Kind} 在 HotCombatEngine 中返回 Executed=false。");
                nextHot = probe;
                nextCold = probeCold;
                usedChoices = usedSpecs.ToArray();
                autoSkippableChoices = BuildAutoSkippableFlags(usedSpecs, windowMetas);
                return;
            }

            if (recCursor >= recordedChoices.Length)
                throw new InvalidOperationException($"回放选牌不足：HotCombatEngine 请求第 {recCursor + 1} 个选择，但录像只有 {recordedChoices.Length} 个。");

            RecordedFuzzChoice target = recordedChoices[recCursor++];
            int optCount = HotCombatEngine.EnumerateChoiceOptionsWithClasses(
                in probe, probeCold, rules, scratch, choiceOptBuf, classMultBuf);
            ChoiceWindowMeta windowMeta = CaptureChoiceWindowMeta(scratch, choiceOptBuf, classMultBuf, optCount);
            HotChoiceSelection matched = MatchRecordedChoiceOption(
                in probe, probeCold, rules, scratch, choiceOptBuf.AsSpan(0, optCount), target, out RecordedFuzzChoice resolvedSpec);
            selections.Add(matched);
            usedSpecs.Add(resolvedSpec);
            windowMetas.Add(windowMeta);
        }
    }

    private static HotChoiceSelection MatchRecordedChoiceOption(
        in CombatHotState probe,
        HotColdSidecar? probeCold,
        HotRules rules,
        HotExecScratch scratch,
        ReadOnlySpan<HotChoiceSelection> options,
        RecordedFuzzChoice target,
        out RecordedFuzzChoice resolvedSpec)
    {
        if (target.Kind == HotChoiceKind.None)
        {
            resolvedSpec = new RecordedFuzzChoice(HotChoiceKind.None, string.Empty, 0, string.Empty, -1, 0);
            return new HotChoiceSelection(HotChoiceKind.None, 0, 0);
        }

        int fallbackStateKeyIdx = -1;
        RecordedFuzzChoice? fallbackStateKeySpec = null;
        for (int i = 0; i < options.Length; i++)
        {
            HotChoiceSelection opt = options[i];
            if (opt.Kind != target.Kind)
                continue;
            RecordedFuzzChoice spec = CaptureChoiceSpec(in probe, probeCold, rules, scratch, opt);
            if (target.Kind == HotChoiceKind.GeneratedCard)
            {
                if (spec.GeneratedOptionIndex == target.GeneratedOptionIndex
                    && string.Equals(spec.CardEntry, target.CardEntry, StringComparison.Ordinal))
                {
                    resolvedSpec = spec;
                    return opt;
                }
            }
            else if (!string.IsNullOrEmpty(target.StateKey)
                && string.Equals(spec.StateKey, target.StateKey, StringComparison.Ordinal))
            {
                if (spec.OptionOccurrence == target.OptionOccurrence)
                {
                    resolvedSpec = spec;
                    return opt;
                }
                if (fallbackStateKeyIdx < 0)
                {
                    fallbackStateKeyIdx = i;
                    fallbackStateKeySpec = spec;
                }
            }
        }

        if (fallbackStateKeyIdx >= 0 && fallbackStateKeySpec != null)
        {
            resolvedSpec = fallbackStateKeySpec;
            return options[fallbackStateKeyIdx];
        }

        for (int i = 0; i < options.Length; i++)
        {
            HotChoiceSelection opt = options[i];
            if (opt.Kind == target.Kind && opt.CardDefId < rules.Cards.Length
                && string.Equals(rules.Cards[opt.CardDefId].Entry, target.CardEntry, StringComparison.Ordinal))
            {
                resolvedSpec = CaptureChoiceSpec(in probe, probeCold, rules, scratch, opt);
                return opt;
            }
        }

        throw new InvalidOperationException(
            $"回放无法在 HotCombatEngine 候选中找到 {target.Kind}:{target.CardEntry}+{target.UpgradeLevel}#opt{target.OptionOccurrence}。");
    }

    private static RecordedFuzzChoice CaptureChoiceSpec(
        in CombatHotState probe,
        HotColdSidecar? probeCold,
        HotRules rules,
        HotExecScratch scratch,
        HotChoiceSelection opt)
    {
        if (opt.Kind == HotChoiceKind.None)
            return new RecordedFuzzChoice(HotChoiceKind.None, string.Empty, 0, string.Empty, -1, 0);

        string entry = opt.CardDefId < rules.Cards.Length ? rules.Cards[opt.CardDefId].Entry : string.Empty;
        if (opt.Kind == HotChoiceKind.GeneratedCard)
            return new RecordedFuzzChoice(HotChoiceKind.GeneratedCard, entry, 0, string.Empty, opt.CardUidOrIndex, 0);

        HotPileKind pile = opt.Kind switch
        {
            HotChoiceKind.HandCard => HotPileKind.Hand,
            HotChoiceKind.DrawPile => HotPileKind.Draw,
            HotChoiceKind.DiscardPile => HotPileKind.Discard,
            HotChoiceKind.ExhaustPile => HotPileKind.Exhaust,
            _ => HotPileKind.Hand,
        };
        int idx = HotCombatEngine.FindCardIndexByUid(in probe, pile, opt.CardUidOrIndex);
        if (idx < 0)
            idx = HotCombatEngine.FindCardIndexByUidAnyPile(in probe, opt.CardUidOrIndex);
        if (idx >= 0)
        {
            ref readonly HotCard hc = ref probe.Cards[idx];
            string key = HotContinuationStamp.FormatChoiceCardKey(in hc, probeCold, rules);
            int optionOccurrence = ComputeStateKeyOptionOccurrence(in probe, probeCold, rules, scratch, pile, idx, key);
            return new RecordedFuzzChoice(opt.Kind, entry, hc.UpgradeLevel, key, -1, optionOccurrence);
        }

        return new RecordedFuzzChoice(opt.Kind, entry, 0, string.Empty, -1, 0);
    }

    /// <summary>
    /// 计算同 <paramref name="targetKey"/> 牌在本次选牌候选列表中的 0-based 出现次序：
    /// 同 StateKey 卡牌在原版 <c>CardSelectCmd.FromCombatPile(PileType.Draw)</c> 的稳定排序
    /// <c>OrderBy(Rarity).ThenBy(CardModel.CompareTo)</c> 下相对顺序与牌堆原序完全一致；
    /// 刻意不跳过 <c>GetPendingExcludedChoiceUids()</c>，因为实机多选弹窗一次性传入完整候选列表。
    /// </summary>
    private static int ComputeStateKeyOptionOccurrence(
        in CombatHotState probe,
        HotColdSidecar? probeCold,
        HotRules rules,
        HotExecScratch scratch,
        HotPileKind pile,
        int targetIdx,
        string targetKey)
    {
        int zoneStart = HotPiles.ZoneStart(in probe, pile);
        int zoneCount = HotPiles.ZoneCount(in probe, pile);
        if (targetIdx < zoneStart || targetIdx >= zoneStart + zoneCount)
            return 0;

        ushort sourceDefId = scratch.PendingChoiceSourceCardDefId;
        HotCustomCardId sourceCardId = sourceDefId > 0 && sourceDefId < rules.Cards.Length
            ? rules.Cards[sourceDefId].CardId
            : HotCustomCardId.None;
        if (sourceCardId == HotCustomCardId.KiChant && pile == HotPileKind.Draw)
        {
            int topLimit = scratch.PendingChoiceMax > 0 ? scratch.PendingChoiceMax : 3;
            if (targetIdx < zoneStart + topLimit && zoneCount > topLimit)
                zoneCount = topLimit;
        }

        if (targetIdx >= zoneStart + zoneCount)
            return 0;

        ReadOnlySpan<ushort> allowedUids = HotCombatEngine.GetPendingFilteredChoiceUids();
        ushort targetDefId = probe.Cards[targetIdx].CardDefId;
        byte targetUpgrade = probe.Cards[targetIdx].UpgradeLevel;
        int occurrence = 0;

        for (int i = zoneStart; i < targetIdx; i++)
        {
            ref readonly HotCard prior = ref probe.Cards[i];
            if (prior.CardDefId != targetDefId || prior.UpgradeLevel != targetUpgrade)
                continue;
            if (!allowedUids.IsEmpty && !ContainsUid(allowedUids, prior.CardUid))
                continue;
            if (string.Equals(HotContinuationStamp.FormatChoiceCardKey(in prior, probeCold, rules), targetKey, StringComparison.Ordinal))
                occurrence++;
        }

        return occurrence;
    }

    private static bool ContainsUid(ReadOnlySpan<ushort> uids, ushort uid)
    {
        for (int i = 0; i < uids.Length; i++)
        {
            if (uids[i] == uid)
                return true;
        }
        return false;
    }

    private static RealCombatAction BindToLiveAction(
        CombatState combat,
        Player player,
        in CombatHotState hot,
        HotRules rules,
        HotAction action,
        string entityId)
    {
        Creature? target = null;
        if (action.TargetEnemySlot >= 0)
        {
            uint targetCid = hot.Enemies[action.TargetEnemySlot].Vital.CombatId;
            target = combat.GetCreature(targetCid)
                ?? throw new InvalidOperationException($"实机找不到目标敌人 CombatId={targetCid} (slot={action.TargetEnemySlot})。");
        }

        if (action.Kind == HotActionKind.EndTurn)
            return RealCombatAction.EndTurn();

        if (action.Kind == HotActionKind.UsePotion)
        {
            PotionModel potion = player.GetPotionAtSlotIndex(action.SlotOrHandIdx)
                ?? throw new InvalidOperationException($"实机药水槽 {action.SlotOrHandIdx} 为空，预期 {entityId}。");
            if (!string.Equals(potion.Id.Entry, entityId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"实机药水槽 {action.SlotOrHandIdx} 为 {potion.Id.Entry}，与 HotKernel 预期 {entityId} 不一致。");
            }
            return RealCombatAction.UsePotion(potion, target);
        }

        IReadOnlyList<CardModel> hand = player.PlayerCombatState!.Hand.Cards;
        if (action.SlotOrHandIdx >= hand.Count)
        {
            throw new InvalidOperationException(
                $"实机手牌数量 {hand.Count} 小于 HotKernel 手牌下标 {action.SlotOrHandIdx}（预期 {entityId}）。");
        }
        CardModel card = hand[action.SlotOrHandIdx];
        if (!string.Equals(card.Id.Entry, entityId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"实机手牌[{action.SlotOrHandIdx}] 为 {card.Id.Entry}，与 HotKernel 预期 {entityId} 不一致。");
        }
        return RealCombatAction.PlayCard(card, target);
    }

    private static string DescribeActionEntity(in CombatHotState hot, HotRules rules, HotAction action)
    {
        return action.Kind switch
        {
            HotActionKind.PlayCard when action.SlotOrHandIdx < hot.HandCount
                && hot.Cards[action.SlotOrHandIdx].CardDefId < rules.Cards.Length
                => rules.Cards[hot.Cards[action.SlotOrHandIdx].CardDefId].Entry,
            HotActionKind.UsePotion when action.SlotOrHandIdx < hot.PotionSlotCount
                && hot.Potions[action.SlotOrHandIdx] < rules.Potions.Length
                => rules.Potions[hot.Potions[action.SlotOrHandIdx]].Entry,
            HotActionKind.EndTurn => "END_TURN",
            _ => action.Kind.ToString(),
        };
    }

    private static void RecordDiffBucket(
        Dictionary<string, BucketRecord> buckets,
        string? dumpDir,
        FuzzScenarioSpec spec,
        List<RecordedFuzzAction> steps,
        string entityId,
        string actionKind,
        string hotStamp,
        string liveStamp,
        HotCombatOutcome expectedOutcome,
        HotCombatOutcome actualOutcome,
        bool verbose)
    {
        var diffs = new ContinuationStamp(hotStamp).DescribeDifferences(new ContinuationStamp(liveStamp), 3);
        string firstLine = diffs.Count > 0 ? diffs[0] : "stamp_mismatch";
        string section = ExtractDiffSection(firstLine);

        if (verbose)
        {
            Console.WriteLine($"[DIFF] action={actionKind}:{entityId} section={section}");
            foreach (string d in diffs)
                Console.WriteLine($"  -> {d}");
        }

        RecordBucket(
            buckets, dumpDir, isCrash: false,
            entityId, section, actionKind, exceptionType: "None",
            firstDiffLine: firstLine,
            expectedOutcome.ToString(), actualOutcome.ToString(),
            ex: null, spec, steps);
    }

    private static void RecordOutcomeBucket(
        Dictionary<string, BucketRecord> buckets,
        string? dumpDir,
        FuzzScenarioSpec spec,
        List<RecordedFuzzAction> steps,
        string entityId,
        string actionKind,
        HotCombatOutcome expectedOutcome,
        HotCombatOutcome actualOutcome,
        bool verbose)
    {
        string line = $"field=TerminalOutcome expected={expectedOutcome} actual={actualOutcome}";
        if (verbose)
            Console.WriteLine($"[DIFF] {line}");

        RecordBucket(
            buckets, dumpDir, isCrash: false,
            entityId, "TerminalOutcome", actionKind, exceptionType: "None",
            firstDiffLine: line,
            expectedOutcome.ToString(), actualOutcome.ToString(),
            ex: null, spec, steps);
    }

    private static void RecordCrashBucket(
        Dictionary<string, BucketRecord> buckets,
        string? dumpDir,
        FuzzScenarioSpec spec,
        List<RecordedFuzzAction> steps,
        string entityId,
        string actionKind,
        Exception ex,
        bool verbose)
    {
        string exType = ex.GetType().Name;
        string line = $"{exType}: {ex.Message}";
        if (verbose)
        {
            Console.WriteLine($"[CRASH] action={actionKind}:{entityId} {line}");
            Console.WriteLine(ex.StackTrace);
        }

        RecordBucket(
            buckets, dumpDir, isCrash: true,
            entityId, "Exception", actionKind, exceptionType: exType,
            firstDiffLine: line,
            expectedOutcome: "NoException", actualOutcome: exType,
            ex: ex, spec, steps);
    }

    private static void RecordBucket(
        Dictionary<string, BucketRecord> buckets,
        string? dumpDir,
        bool isCrash,
        string entityId,
        string section,
        string actionKind,
        string exceptionType,
        string firstDiffLine,
        string expectedOutcome,
        string actualOutcome,
        Exception? ex,
        FuzzScenarioSpec spec,
        List<RecordedFuzzAction> steps)
    {
        string key = $"{actionKind}|{entityId}|{section}|{exceptionType}";
        if (buckets.TryGetValue(key, out BucketRecord? existing))
        {
            existing.Count++;
            // 若新复现步骤更短则覆盖为更小复现包
            if (dumpDir != null && steps.Count < existing.SavedSteps)
            {
                SaveFailureArtifact(existing.SavedPath, key, entityId, section, actionKind, exceptionType,
                    firstDiffLine, expectedOutcome, actualOutcome, ex, spec, steps);
                existing.SavedSteps = steps.Count;
                existing.FirstDiffLine = firstDiffLine;
            }
            return;
        }

        string safeName = SanitizeFileName($"{actionKind}__{entityId}__{section}__{exceptionType}") + ".json";
        string savedPath = dumpDir != null ? Path.Combine(dumpDir, safeName) : string.Empty;
        if (dumpDir != null)
        {
            SaveFailureArtifact(savedPath, key, entityId, section, actionKind, exceptionType,
                firstDiffLine, expectedOutcome, actualOutcome, ex, spec, steps);
        }

        buckets[key] = new BucketRecord
        {
            BucketKey = key,
            IsCrash = isCrash,
            ActionEntityId = entityId,
            FirstDiffSection = section,
            ActionKind = actionKind,
            ExceptionType = exceptionType,
            Count = 1,
            FirstDiffLine = firstDiffLine,
            SavedPath = savedPath,
            SavedSteps = steps.Count,
        };
    }

    private static void SaveFailureArtifact(
        string path,
        string bucketKey,
        string entityId,
        string section,
        string actionKind,
        string exceptionType,
        string firstDiffLine,
        string expectedOutcome,
        string actualOutcome,
        Exception? ex,
        FuzzScenarioSpec spec,
        List<RecordedFuzzAction> steps)
    {
        FuzzFailureArtifact artifact = new()
        {
            BucketKey = bucketKey,
            ActionEntityId = entityId,
            FirstDiffSection = section,
            ActionKind = actionKind,
            ExceptionType = exceptionType,
            Summary = $"{actionKind} {entityId} -> {section} ({exceptionType}) at step {steps.Count}",
            FirstDiffLine = firstDiffLine,
            ExpectedOutcome = expectedOutcome,
            ActualOutcome = actualOutcome,
            ExceptionMessage = ex?.Message,
            ExceptionStack = ex?.StackTrace,
            Scenario = spec,
            Steps = steps.ToArray(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(artifact, FuzzScenarioGenerator.JsonOptions));
    }

    private static string ExtractDiffSection(string diffLine)
    {
        // ContinuationStamp.DescribeDifferences 输出形如 "field=E0.hp expected=..." 或 "field=H[2] ..."
        if (diffLine.StartsWith("field=", StringComparison.Ordinal))
        {
            int end = diffLine.IndexOf(' ');
            string token = end > 6 ? diffLine[6..end] : diffLine[6..];
            int bracket = token.IndexOf('[');
            if (bracket > 0)
                token = token[..bracket];
            return token;
        }
        int sp = diffLine.IndexOf(' ');
        return sp > 0 ? diffLine[..sp] : "unknown";
    }

    private static string SanitizeFileName(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = raw.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] is '|' or ' ' or ':' or '/' or '\\')
                chars[i] = '_';
        }
        string cleaned = new(chars);
        return cleaned.Length > 140 ? cleaned[..140] : cleaned;
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is System.Reflection.TargetInvocationException or AggregateException && ex.InnerException != null)
            ex = ex.InnerException;
        return ex;
    }

    private static string ResolveRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
}
