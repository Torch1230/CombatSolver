using System.Diagnostics;
using CombatSolver;
using CombatSolver.HotKernel;
using CombatSolver.HotSearch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;

namespace OfflineSearchHarness;

/// <summary>
/// 端到端策略政策校验器（<c>--verify-strategy-policies</c>）：
/// 完整执行 4 组确定性离线回归套件（共 25 项校验），逐项记录并汇总 <c>[PASS]</c>/<c>[FAIL]</c> 结果，
/// 任意断言失败不遮蔽后续校验，全部执行结束后若存在失败则返回非零退出码：
/// <list type="bullet">
///   <item><description>Suite A：HotKernel 全部 11 项局外成长来源单步精算（<c>s.Growth[0..10]</c> 与 <c>s.LongTermResourceValue</c>）。</description></item>
///   <item><description>Suite B：HotSearch 局外成长端到端（Stage-1 禁止 0 成长早停、零预算禁止卖血吃 Feed、<c>IgnoreLongTermRewards</c> 含燃烧之血覆盖）。</description></item>
///   <item><description>Suite C：HotSearch 卡遗物端到端（攻击计数遗物 Nunchaku/PenNib、回合计数遗物 HappyFlower、肉块阈值净收益 vs 保本/净亏卖血）。</description></item>
///   <item><description>Suite D：<c>BrightestFlameMaxHpLossLimit</c> 端到端（对照组放行 vs 上限 0 绝对禁打）。</description></item>
/// </list>
/// </summary>
internal static class StrategyPolicyVerifier
{
    private const int GrowthCount = 11;
    private const string SuiteAName = "Suite A: HotKernel All 11 Out-of-Combat Growth Sources";
    private const string SuiteBName = "Suite B: HotSearch Out-of-Combat Growth End-to-End";
    private const string SuiteCName = "Suite C: HotSearch Relic Counter End-to-End";
    private const string SuiteDName = "Suite D: BrightestFlameMaxHpLossLimit End-to-End";
    private static readonly string[] AllSuiteNames = [SuiteAName, SuiteBName, SuiteCName, SuiteDName];

    internal sealed record CheckOutcome(string Suite, string Name, bool Passed, long ElapsedMs, string? Error);

    private static string ResolveRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));

    public static int Run(string[] args)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine($"--verify-strategy-policies 不接受额外参数：{string.Join(" ", args)}");
            return 2;
        }

        Stopwatch totalSw = Stopwatch.StartNew();
        Console.WriteLine("=== --verify-strategy-policies ===");

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        string repoRoot = ResolveRepoRoot();
        MainLoopContext loop = new();
        HarnessOptions modOptions = new()
        {
            Profile = "Medium",
            MaxDegreeOfParallelism = 1,
            BudgetMilliseconds = 15_000,
            PotionPolicy = "Smart",
            SearchMode = "Coordinator",
            WorkspaceDirectory = Path.Combine(repoRoot, ".local", "offline-harness"),
            OutputDirectory = Path.Combine(repoRoot, ".local", "offline-harness", "strategy-policy-verifier"),
        };

        try
        {
            GameBootstrap.EnsureBootstrapped(loop, modOptions);

            List<CheckOutcome> outcomes = VerifyAll(loop);

            Console.WriteLine();
            Console.WriteLine("=== --verify-strategy-policies SUMMARY ===");
            foreach (CheckOutcome outcome in outcomes)
            {
                if (outcome.Passed)
                    Console.WriteLine($"[PASS] {outcome.Suite} | {outcome.Name} ({outcome.ElapsedMs} ms)");
                else
                    Console.WriteLine($"[FAIL] {outcome.Suite} | {outcome.Name} ({outcome.ElapsedMs} ms): {outcome.Error}");
            }

            int totalChecks = outcomes.Count;
            int passedChecks = outcomes.Count(static o => o.Passed);
            int failedChecks = totalChecks - passedChecks;
            int passedSuites = AllSuiteNames.Count(suite =>
            {
                List<CheckOutcome> suiteOutcomes = outcomes.Where(o => o.Suite == suite).ToList();
                return suiteOutcomes.Count > 0 && suiteOutcomes.All(static o => o.Passed);
            });

            if (failedChecks > 0)
            {
                Console.Error.WriteLine(
                    $"[FAIL] --verify-strategy-policies: {passedSuites}/{AllSuiteNames.Length} suites passed, " +
                    $"{passedChecks}/{totalChecks} checks passed, {failedChecks} failed in {totalSw.ElapsedMilliseconds} ms");
                foreach (CheckOutcome failure in outcomes.Where(static o => !o.Passed))
                    Console.Error.WriteLine($"  [FAIL] {failure.Suite} | {failure.Name}: {failure.Error}");
                return 1;
            }

            Console.WriteLine(
                $"ALL --verify-strategy-policies CHECKS PASSED: {passedSuites}/{AllSuiteNames.Length} suites, " +
                $"{passedChecks}/{totalChecks} checks in {totalSw.ElapsedMilliseconds} ms");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] --verify-strategy-policies: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            RealCombatDriver.CleanupActiveRun();
            ModRuntime.Session?.Dispose();
            ModRuntime.FlushDiagnostics();
        }
    }

    internal static List<CheckOutcome> VerifyAll(MainLoopContext loop)
    {
        List<CheckOutcome> outcomes = new(25);

        RunSuite(outcomes, SuiteAName, () => RunSuiteA_HotKernelAll11GrowthSources(loop, outcomes));
        RunSuite(outcomes, SuiteBName, () => RunSuiteB_HotSearchGrowthEndToEnd(loop, outcomes));
        RunSuite(outcomes, SuiteCName, () => RunSuiteC_HotSearchRelicCountersEndToEnd(loop, outcomes));
        RunSuite(outcomes, SuiteDName, () => RunSuiteD_BrightestFlameLimitEndToEnd(loop, outcomes));

        return outcomes;
    }

    private static void RunSuite(List<CheckOutcome> outcomes, string suiteName, Action runSuite)
    {
        int startIndex = outcomes.Count;
        runSuite();
        int suiteTotal = outcomes.Count - startIndex;
        int suitePassed = 0;
        for (int i = startIndex; i < outcomes.Count; i++)
        {
            if (outcomes[i].Passed)
                suitePassed++;
        }

        string status = suiteTotal > 0 && suitePassed == suiteTotal ? "PASS" : "FAIL";
        Console.WriteLine($"[{status}] {suiteName} ({suitePassed}/{suiteTotal} checks)");
    }

    private static void RunCheck(List<CheckOutcome> outcomes, string suiteName, string checkName, Action body)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            body();
            sw.Stop();
            outcomes.Add(new CheckOutcome(suiteName, checkName, Passed: true, sw.ElapsedMilliseconds, Error: null));
            Console.WriteLine($"  [PASS] {checkName} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            string error = $"{ex.GetType().Name}: {ex.Message}";
            outcomes.Add(new CheckOutcome(suiteName, checkName, Passed: false, sw.ElapsedMilliseconds, Error: error));
            Console.Error.WriteLine($"  [FAIL] {checkName} ({sw.ElapsedMilliseconds} ms): {error}");
            RealCombatDriver.CleanupActiveRun();
        }
    }

    // =========================================================================
    // Suite A: HotKernel All 11 Out-of-Combat Growth Sources
    // =========================================================================

    private static void RunSuiteA_HotKernelAll11GrowthSources(MainLoopContext loop, List<CheckOutcome> outcomes)
    {
        // A0: HandOfGreed (Fatal kill) -> Growth[0] == 1, LongTermResourceValue == 20
        RunCheck(outcomes, SuiteAName, "A0/HandOfGreed", () =>
        {
            HotExecScratch scratch = new();
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA00",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PlayerMaxHp = 80,
                    PlayerHp = 70,
                    Cards = [new CardInjectionSpec("HAND_OF_GREED")],
                },
                enemyHpBySlot: [10]);

            HotRootBundle root = HotRootBuilder.BuildFromLive(combat);
            CombatHotState s = root.RootState;
            HotColdSidecar? cold = root.RootCold;
            int goldBefore = s.Gold;

            PlayCardInHotKernel(ref s, ref cold, root.Rules, scratch, "HAND_OF_GREED", targetEnemySlot: 0);
            AssertExactGrowth(in s, expectedIndex: 0, expectedCount: 1, expectedLongTermValue: 20, "A0/HandOfGreed");
            Check(s.Gold == goldBefore + 20, $"A0/HandOfGreed: expected Gold={goldBefore + 20}, got {s.Gold}.");
        });

        // A1: TheHunt (Fatal kill) -> Growth[1] == 1, LongTermResourceValue == 25
        RunCheck(outcomes, SuiteAName, "A1/TheHunt", () =>
        {
            HotExecScratch scratch = new();
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "SILENT",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA01",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PlayerMaxHp = 70,
                    PlayerHp = 60,
                    Cards = [new CardInjectionSpec("THE_HUNT")],
                },
                enemyHpBySlot: [5]);

            HotRootBundle root = HotRootBuilder.BuildFromLive(combat);
            CombatHotState s = root.RootState;
            HotColdSidecar? cold = root.RootCold;

            PlayCardInHotKernel(ref s, ref cold, root.Rules, scratch, "THE_HUNT", targetEnemySlot: 0);
            AssertExactGrowth(in s, expectedIndex: 1, expectedCount: 1, expectedLongTermValue: 25, "A1/TheHunt");
        });

        // A2: Feed (Fatal kill) -> Growth[2] == 1, LongTermResourceValue == 0, Player.MaxHp += 3
        RunCheck(outcomes, SuiteAName, "A2/Feed", () =>
        {
            HotExecScratch scratch = new();
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA02",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PlayerMaxHp = 80,
                    PlayerHp = 70,
                    Cards = [new CardInjectionSpec("FEED")],
                },
                enemyHpBySlot: [5]);

            HotRootBundle root = HotRootBuilder.BuildFromLive(combat);
            CombatHotState s = root.RootState;
            HotColdSidecar? cold = root.RootCold;
            int maxHpBefore = s.Player.MaxHp;

            PlayCardInHotKernel(ref s, ref cold, root.Rules, scratch, "FEED", targetEnemySlot: 0);
            AssertExactGrowth(in s, expectedIndex: 2, expectedCount: 1, expectedLongTermValue: 0, "A2/Feed");
            Check(s.Player.MaxHp == maxHpBefore + 3,
                $"A2/Feed: expected Player.MaxHp={maxHpBefore + 3}, got {s.Player.MaxHp}.");
        });

        // A3: Royalties x2 plays -> Growth[3] == 2, LongTermResourceValue == 60
        RunCheck(outcomes, SuiteAName, "A3/Royalties-x2", () =>
        {
            HotExecScratch scratch = new();
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "REGENT",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA03",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PlayerMaxHp = 75,
                    PlayerHp = 65,
                    ExtraEnergy = 2,
                    Cards = [new CardInjectionSpec("ROYALTIES"), new CardInjectionSpec("ROYALTIES")],
                });

            HotRootBundle root = HotRootBuilder.BuildFromLive(combat);
            CombatHotState s = root.RootState;
            HotColdSidecar? cold = root.RootCold;

            PlayCardInHotKernel(ref s, ref cold, root.Rules, scratch, "ROYALTIES", targetEnemySlot: -1);
            PlayCardInHotKernel(ref s, ref cold, root.Rules, scratch, "ROYALTIES", targetEnemySlot: -1);
            AssertExactGrowth(in s, expectedIndex: 3, expectedCount: 2, expectedLongTermValue: 60, "A3/Royalties-x2");
        });

        // A4: Alchemize (open belt == 1/20, Sozu == 0/0, full belt == 0/0)
        RunCheck(outcomes, SuiteAName, "A4/Alchemize", () =>
        {
            HotExecScratch scratch = new();

            // A4a: open belt
            CombatState openCombat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "SILENT",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA04A",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards = [new CardInjectionSpec("ALCHEMIZE")],
                });
            HotRootBundle openRoot = HotRootBuilder.BuildFromLive(openCombat);
            CombatHotState sOpen = openRoot.RootState;
            HotColdSidecar? coldOpen = openRoot.RootCold;
            PlayCardInHotKernel(ref sOpen, ref coldOpen, openRoot.Rules, scratch, "ALCHEMIZE", targetEnemySlot: -1);
            AssertExactGrowth(in sOpen, expectedIndex: 4, expectedCount: 1, expectedLongTermValue: 20, "A4a/Alchemize-OpenBelt");

            // A4b: Sozu blocks potion procurement -> 0
            CombatState sozuCombat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "SILENT",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA04B",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Relics = ["SOZU"],
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards = [new CardInjectionSpec("ALCHEMIZE")],
                });
            HotRootBundle sozuRoot = HotRootBuilder.BuildFromLive(sozuCombat);
            CombatHotState sSozu = sozuRoot.RootState;
            HotColdSidecar? coldSozu = sozuRoot.RootCold;
            PlayCardInHotKernel(ref sSozu, ref coldSozu, sozuRoot.Rules, scratch, "ALCHEMIZE", targetEnemySlot: -1);
            AssertExactGrowth(in sSozu, expectedIndex: 4, expectedCount: 0, expectedLongTermValue: 0, "A4b/Alchemize-Sozu");

            // A4c: full belt blocks potion procurement -> 0 (RealCombatDriver keeps >=3 slots, so fill all 3)
            CombatState fullCombat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "SILENT",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA04C",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PotionSlotCount = 3,
                    Potions = ["FIRE_POTION", "BLOCK_POTION", "ENERGY_POTION"],
                    Cards = [new CardInjectionSpec("ALCHEMIZE")],
                });
            Player fullPlayer = LocalContext.GetMe(fullCombat)!;
            Check(fullPlayer.PotionSlots.Count == 3 && fullPlayer.PotionSlots.All(static p => p != null),
                $"A4c/Alchemize-FullBelt: expected full 3-slot potion belt (0 empty slots), got slots={fullPlayer.PotionSlots.Count}, empty={fullPlayer.PotionSlots.Count(static p => p == null)}.");
            HotRootBundle fullRoot = HotRootBuilder.BuildFromLive(fullCombat);
            CombatHotState sFull = fullRoot.RootState;
            HotColdSidecar? coldFull = fullRoot.RootCold;
            PlayCardInHotKernel(ref sFull, ref coldFull, fullRoot.Rules, scratch, "ALCHEMIZE", targetEnemySlot: -1);
            AssertExactGrowth(in sFull, expectedIndex: 4, expectedCount: 0, expectedLongTermValue: 0, "A4c/Alchemize-FullBelt");
        });

        // A5: GeneticAlgorithm (HasDeckVersion true vs false)
        RunCheck(outcomes, SuiteAName, "A5/GeneticAlgorithm", () =>
        {
            HotExecScratch scratch = new();
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "DEFECT",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA05",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Cards = [new CardInjectionSpec("GENETIC_ALGORITHM")],
                });
            HotRootBundle root = HotRootBuilder.BuildFromLive(combat);

            CombatHotState sDeck = root.RootState;
            HotColdSidecar? coldDeck = root.RootCold;
            int idxDeck = FindHandCardIndex(in sDeck, root.Rules, "GENETIC_ALGORITHM");
            Check((sDeck.Cards[idxDeck].Flags & HotCardFlags.HasDeckVersion) != 0,
                "A5/GeneticAlgorithm: expected deck-injected card to have HasDeckVersion=true.");
            PlayCardInHotKernel(ref sDeck, ref coldDeck, root.Rules, scratch, "GENETIC_ALGORITHM", targetEnemySlot: -1);
            AssertExactGrowth(in sDeck, expectedIndex: 5, expectedCount: 1, expectedLongTermValue: 3, "A5a/GeneticAlgorithm-DeckVersion");

            CombatHotState sTemp = root.RootState;
            HotColdSidecar? coldTemp = root.RootCold;
            int idxTemp = FindHandCardIndex(in sTemp, root.Rules, "GENETIC_ALGORITHM");
            sTemp.Cards[idxTemp].Flags &= ~HotCardFlags.HasDeckVersion;
            PlayCardInHotKernel(ref sTemp, ref coldTemp, root.Rules, scratch, "GENETIC_ALGORITHM", targetEnemySlot: -1);
            AssertExactGrowth(in sTemp, expectedIndex: 5, expectedCount: 0, expectedLongTermValue: 0, "A5b/GeneticAlgorithm-NoDeckVersion");
        });

        // A6: TheScythe (HasDeckVersion true vs false)
        RunCheck(outcomes, SuiteAName, "A6/TheScythe", () =>
        {
            HotExecScratch scratch = new();
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "NECROBINDER",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA06",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Cards = [new CardInjectionSpec("THE_SCYTHE")],
                },
                enemyHpBySlot: [40]);
            HotRootBundle root = HotRootBuilder.BuildFromLive(combat);

            CombatHotState sDeck = root.RootState;
            HotColdSidecar? coldDeck = root.RootCold;
            int idxDeck = FindHandCardIndex(in sDeck, root.Rules, "THE_SCYTHE");
            Check((sDeck.Cards[idxDeck].Flags & HotCardFlags.HasDeckVersion) != 0,
                "A6/TheScythe: expected deck-injected card to have HasDeckVersion=true.");
            PlayCardInHotKernel(ref sDeck, ref coldDeck, root.Rules, scratch, "THE_SCYTHE", targetEnemySlot: 0);
            AssertExactGrowth(in sDeck, expectedIndex: 6, expectedCount: 1, expectedLongTermValue: 5, "A6a/TheScythe-DeckVersion");

            CombatHotState sTemp = root.RootState;
            HotColdSidecar? coldTemp = root.RootCold;
            int idxTemp = FindHandCardIndex(in sTemp, root.Rules, "THE_SCYTHE");
            sTemp.Cards[idxTemp].Flags &= ~HotCardFlags.HasDeckVersion;
            PlayCardInHotKernel(ref sTemp, ref coldTemp, root.Rules, scratch, "THE_SCYTHE", targetEnemySlot: 0);
            AssertExactGrowth(in sTemp, expectedIndex: 6, expectedCount: 0, expectedLongTermValue: 0, "A6b/TheScythe-NoDeckVersion");
        });

        // A7: Goopy enchantment (HasDeckVersion true vs false)
        RunCheck(outcomes, SuiteAName, "A7/Goopy", () =>
        {
            HotExecScratch scratch = new();
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA07",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Cards = [new CardInjectionSpec("DEFEND_IRONCLAD", EnchantmentId: "GOOPY", EnchantmentAmount: 1)],
                });
            HotRootBundle root = HotRootBuilder.BuildFromLive(combat);

            CombatHotState sDeck = root.RootState;
            HotColdSidecar? coldDeck = root.RootCold;
            int idxDeck = FindHandCardIndex(in sDeck, root.Rules, "DEFEND_IRONCLAD");
            Check((sDeck.Cards[idxDeck].Flags & HotCardFlags.HasDeckVersion) != 0,
                "A7/Goopy: expected deck-injected card to have HasDeckVersion=true.");
            Check(sDeck.Cards[idxDeck].EnchId != 0,
                "A7/Goopy: expected DEFEND_IRONCLAD to carry GOOPY enchantment.");
            PlayCardInHotKernel(ref sDeck, ref coldDeck, root.Rules, scratch, "DEFEND_IRONCLAD", targetEnemySlot: -1);
            AssertExactGrowth(in sDeck, expectedIndex: 7, expectedCount: 1, expectedLongTermValue: 1, "A7a/Goopy-DeckVersion");

            CombatHotState sTemp = root.RootState;
            HotColdSidecar? coldTemp = root.RootCold;
            int idxTemp = FindHandCardIndex(in sTemp, root.Rules, "DEFEND_IRONCLAD");
            sTemp.Cards[idxTemp].Flags &= ~HotCardFlags.HasDeckVersion;
            PlayCardInHotKernel(ref sTemp, ref coldTemp, root.Rules, scratch, "DEFEND_IRONCLAD", targetEnemySlot: -1);
            AssertExactGrowth(in sTemp, expectedIndex: 7, expectedCount: 0, expectedLongTermValue: 0, "A7b/Goopy-NoDeckVersion");
        });

        // A8: ForbiddenGrimoire x2 plays -> Growth[8] == 2, LongTermResourceValue == 40
        RunCheck(outcomes, SuiteAName, "A8/ForbiddenGrimoire-x2", () =>
        {
            HotExecScratch scratch = new();
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "NECROBINDER",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA08",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    ExtraEnergy = 4,
                    Cards = [new CardInjectionSpec("FORBIDDEN_GRIMOIRE"), new CardInjectionSpec("FORBIDDEN_GRIMOIRE")],
                });
            HotRootBundle root = HotRootBuilder.BuildFromLive(combat);
            CombatHotState s = root.RootState;
            HotColdSidecar? cold = root.RootCold;

            PlayCardInHotKernel(ref s, ref cold, root.Rules, scratch, "FORBIDDEN_GRIMOIRE", targetEnemySlot: -1);
            PlayCardInHotKernel(ref s, ref cold, root.Rules, scratch, "FORBIDDEN_GRIMOIRE", targetEnemySlot: -1);
            AssertExactGrowth(in s, expectedIndex: 8, expectedCount: 2, expectedLongTermValue: 40, "A8/ForbiddenGrimoire-x2");
        });

        // A9: MadScience (Improvement rider; MadScienceUpgradeCapacity = 0 vs 1)
        RunCheck(outcomes, SuiteAName, "A9/MadScience", () =>
        {
            HotExecScratch scratch = new();

            // A9a: only upgraded card in Deck -> MadScienceUpgradeCapacity == 0 -> Growth[9] == 0
            CombatState cap0Combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA09A",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Cards = [new CardInjectionSpec("MAD_SCIENCE", UpgradeLevel: 1)],
                });
            ConfigureMadScienceImprovement(cap0Combat);
            HotRootBundle cap0Root = HotRootBuilder.BuildFromLive(cap0Combat);
            Check(cap0Root.Rules.MadScienceUpgradeCapacity == 0,
                $"A9a/MadScience-Cap0: expected MadScienceUpgradeCapacity=0, got {cap0Root.Rules.MadScienceUpgradeCapacity}.");
            CombatHotState sCap0 = cap0Root.RootState;
            HotColdSidecar? coldCap0 = cap0Root.RootCold;
            PlayCardInHotKernel(ref sCap0, ref coldCap0, cap0Root.Rules, scratch, "MAD_SCIENCE", targetEnemySlot: -1);
            AssertExactGrowth(in sCap0, expectedIndex: 9, expectedCount: 0, expectedLongTermValue: 0, "A9a/MadScience-Cap0");

            // A9b: 1 un-upgraded card in Deck -> MadScienceUpgradeCapacity == 1 -> Growth[9] == 1, LTV == 20
            CombatState cap1Combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA09B",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Cards =
                    [
                        new CardInjectionSpec("MAD_SCIENCE", UpgradeLevel: 1),
                        new CardInjectionSpec("STRIKE_IRONCLAD", UpgradeLevel: 0),
                    ],
                });
            ConfigureMadScienceImprovement(cap1Combat);
            HotRootBundle cap1Root = HotRootBuilder.BuildFromLive(cap1Combat);
            Check(cap1Root.Rules.MadScienceUpgradeCapacity == 1,
                $"A9b/MadScience-Cap1: expected MadScienceUpgradeCapacity=1, got {cap1Root.Rules.MadScienceUpgradeCapacity}.");
            CombatHotState sCap1 = cap1Root.RootState;
            HotColdSidecar? coldCap1 = cap1Root.RootCold;
            PlayCardInHotKernel(ref sCap1, ref coldCap1, cap1Root.Rules, scratch, "MAD_SCIENCE", targetEnemySlot: -1);
            AssertExactGrowth(in sCap1, expectedIndex: 9, expectedCount: 1, expectedLongTermValue: 20, "A9b/MadScience-Cap1");
        });

        // A10: ShowOff (normal kill -> Growth[10] == 1 / LTV == 2; Reattach reviving kill -> Growth[10] == 0 / LTV == 0)
        RunCheck(outcomes, SuiteAName, "A10/ShowOff", () =>
        {
            HotExecScratch scratch = new();

            // A10a: normal Fatal kill
            CombatState normalCombat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "FOOL",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATA10A",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    ExtraEnergy = 1,
                    Cards = [new CardInjectionSpec("SHOW_OFF"), new CardInjectionSpec("STRIKE_IRONCLAD")],
                },
                enemyHpBySlot: [5]);
            HotRootBundle normalRoot = HotRootBuilder.BuildFromLive(normalCombat);
            CombatHotState sNormal = normalRoot.RootState;
            HotColdSidecar? coldNormal = normalRoot.RootCold;
            PlayCardInHotKernel(ref sNormal, ref coldNormal, normalRoot.Rules, scratch, "SHOW_OFF", targetEnemySlot: -1);
            PlayCardInHotKernel(ref sNormal, ref coldNormal, normalRoot.Rules, scratch, "STRIKE_IRONCLAD", targetEnemySlot: 0);
            AssertExactGrowth(in sNormal, expectedIndex: 10, expectedCount: 1, expectedLongTermValue: 2, "A10a/ShowOff-NormalKill");
            Check(sNormal.ShowOffRewards == 2,
                $"A10a/ShowOff-NormalKill: expected ShowOffRewards=2, got {sNormal.ShowOffRewards}.");

            // A10b: DecimillipedeElite segment kill with living Reattach teammates -> reviving kill (non-Fatal) -> 0
            CombatState reattachCombat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "FOOL",
                    EncounterId = "DECIMILLIPEDE_ELITE",
                    Seed = "STRATA10B",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    ExtraEnergy = 1,
                    Cards = [new CardInjectionSpec("SHOW_OFF"), new CardInjectionSpec("STRIKE_IRONCLAD")],
                },
                enemyHpBySlot: [5]);
            Check(reattachCombat.Enemies.Count >= 2,
                $"A10b/ShowOff-Reattach: expected >=2 Decimillipede segments, got {reattachCombat.Enemies.Count}.");
            HotRootBundle reattachRoot = HotRootBuilder.BuildFromLive(reattachCombat);
            CombatHotState sRevive = reattachRoot.RootState;
            HotColdSidecar? coldRevive = reattachRoot.RootCold;
            PlayCardInHotKernel(ref sRevive, ref coldRevive, reattachRoot.Rules, scratch, "SHOW_OFF", targetEnemySlot: -1);
            PlayCardInHotKernel(ref sRevive, ref coldRevive, reattachRoot.Rules, scratch, "STRIKE_IRONCLAD", targetEnemySlot: 0);
            AssertExactGrowth(in sRevive, expectedIndex: 10, expectedCount: 0, expectedLongTermValue: 0, "A10b/ShowOff-ReattachRevive");
            Check(sRevive.ShowOffRewards == 0,
                $"A10b/ShowOff-ReattachRevive: expected ShowOffRewards=0, got {sRevive.ShowOffRewards}.");
        });
    }

    // =========================================================================
    // Suite B: HotSearch Out-of-Combat Growth End-to-End
    // =========================================================================

    private static void RunSuiteB_HotSearchGrowthEndToEnd(MainLoopContext loop, List<CheckOutcome> outcomes)
    {
        // B1: Stage-1 0-HP non-growth Turn-1 lethal (depth 1 Bludgeon) vs depth-2 growth lethal
        //     with 0 potions on belt -> MUST NOT early-exit on 0-growth win; MUST materialize GrowthRewards.Total > 0.
        GrowthValues activeGrowthBudgets = new(HandOfGreed: 15, Feed: 15, Alchemize: 15, Royalties: 15);

        // B1a: Feed
        RunCheck(outcomes, SuiteBName, "B1a/Feed-NoEarlyExit", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATB1A",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PlayerMaxHp = 80,
                    PlayerHp = 70,
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards =
                    [
                        new CardInjectionSpec("BLUDGEON"),
                        new CardInjectionSpec("STRIKE_IRONCLAD"),
                        new CardInjectionSpec("FEED"),
                    ],
                },
                enemyHpBySlot: [12]);

            SolverResult result = SolveWithPolicy(combat, growthBudgets: activeGrowthBudgets, stopAtZeroLoss: true);
            AssertVictory(result, "B1a/Feed-NoEarlyExit");
            Check(result.Snapshot.GrowthRewards.Feed == 1 && result.Snapshot.GrowthRewards.Total > 0,
                $"B1a/Feed-NoEarlyExit: expected Feed growth > 0, got {result.Snapshot.GrowthRewards}, actions=[{FormatActions(result)}].");
        });

        // B1b: HandOfGreed
        RunCheck(outcomes, SuiteBName, "B1b/HandOfGreed-NoEarlyExit", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATB1B",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PlayerMaxHp = 80,
                    PlayerHp = 70,
                    ExtraEnergy = 1,
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards =
                    [
                        new CardInjectionSpec("BLUDGEON"),
                        new CardInjectionSpec("STRIKE_IRONCLAD"),
                        new CardInjectionSpec("HAND_OF_GREED"),
                    ],
                },
                enemyHpBySlot: [25]);

            SolverResult result = SolveWithPolicy(combat, growthBudgets: activeGrowthBudgets, stopAtZeroLoss: true);
            AssertVictory(result, "B1b/HandOfGreed-NoEarlyExit");
            Check(result.Snapshot.GrowthRewards.HandOfGreed == 1
                && result.Snapshot.GrowthRewards.Total > 0
                && result.Snapshot.LongTermResourceValue == 20,
                $"B1b/HandOfGreed-NoEarlyExit: expected HandOfGreed=1 & LTV=20, got {result.Snapshot.GrowthRewards}, LTV={result.Snapshot.LongTermResourceValue}, actions=[{FormatActions(result)}].");
        });

        // B1c: Alchemize (0 potions on belt, open slots)
        RunCheck(outcomes, SuiteBName, "B1c/Alchemize-NoEarlyExit", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "SILENT",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATB1C",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PlayerMaxHp = 70,
                    PlayerHp = 60,
                    ExtraEnergy = 1,
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards =
                    [
                        new CardInjectionSpec("BLUDGEON"),
                        new CardInjectionSpec("ALCHEMIZE"),
                    ],
                },
                enemyHpBySlot: [20]);

            SolverResult result = SolveWithPolicy(combat, growthBudgets: activeGrowthBudgets, stopAtZeroLoss: true);
            AssertVictory(result, "B1c/Alchemize-NoEarlyExit");
            Check(result.Snapshot.GrowthRewards.Alchemize == 1 && result.Snapshot.GrowthRewards.Total > 0,
                $"B1c/Alchemize-NoEarlyExit: expected Alchemize=1 & Total>0, got {result.Snapshot.GrowthRewards}, actions=[{FormatActions(result)}].");
        });

        // B1d: Royalties (0 potions on belt)
        RunCheck(outcomes, SuiteBName, "B1d/Royalties-NoEarlyExit", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "REGENT",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATB1D",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PlayerMaxHp = 75,
                    PlayerHp = 65,
                    ExtraEnergy = 1,
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards =
                    [
                        new CardInjectionSpec("BLUDGEON"),
                        new CardInjectionSpec("ROYALTIES"),
                    ],
                },
                enemyHpBySlot: [20]);

            SolverResult result = SolveWithPolicy(combat, growthBudgets: activeGrowthBudgets, stopAtZeroLoss: true);
            AssertVictory(result, "B1d/Royalties-NoEarlyExit");
            Check(result.Snapshot.GrowthRewards.Royalties == 1
                && result.Snapshot.GrowthRewards.Total > 0
                && result.Snapshot.LongTermResourceValue == 30,
                $"B1d/Royalties-NoEarlyExit: expected Royalties=1 & LTV=30, got {result.Snapshot.GrowthRewards}, LTV={result.Snapshot.LongTermResourceValue}, actions=[{FormatActions(result)}].");
        });

        // B2: Feed with GrowthBudgets.Feed = 0 vs taking 5 HP damage -> MUST NOT trade 5 HP for Feed
        //     (+ positive control with Feed budget = 10 to prove the 5-HP Feed route exists and is selectable when budgeted).
        RunCheck(outcomes, SuiteBName, "B2/Feed-ZeroBudget", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATB2",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PlayerMaxHp = 80,
                    PlayerHp = 70,
                    PotionSlotCount = 2,
                    Potions = [],
                    // AcidGoop base 4 + 1 Strength = exact 5 damage on Turn 1; Turn 2 is Inhale (0 damage).
                    InitialPowers = [new PowerInjectionSpec("STRENGTH_POWER", 1, OnPlayer: false, EnemyIndex: 0)],
                    Cards =
                    [
                        new CardInjectionSpec("BLUDGEON"),
                        new CardInjectionSpec("STRIKE_IRONCLAD", UpgradeLevel: 1),
                        new CardInjectionSpec("FEED"),
                    ],
                },
                enemyHpBySlot: [25]);

            // Budget = 0: taking 5 HP for Feed (+3 MaxHp/+3 Hp -> net -2 HP) MUST lose to 0-HP Turn-1 Bludgeon kill.
            SolverResult zeroBudgetResult = SolveWithPolicy(
                combat,
                growthBudgets: new GrowthValues(Feed: 0),
                stopAtZeroLoss: true);
            AssertVictory(zeroBudgetResult, "B2/Feed-ZeroBudget");
            Check(zeroBudgetResult.Snapshot.GrowthRewards.Feed == 0
                && zeroBudgetResult.Snapshot.CumulativePlayerHpLost == 0,
                $"B2/Feed-ZeroBudget: MUST NOT trade 5 HP for Feed when GrowthBudgets.Feed=0; got Feed={zeroBudgetResult.Snapshot.GrowthRewards.Feed}, HpLost={zeroBudgetResult.Snapshot.CumulativePlayerHpLost}, actions=[{FormatActions(zeroBudgetResult)}].");

            // Control with Budget = 10: confirms the 5-HP Feed route is genuinely reachable and picked when budgeted.
            SolverResult fundedResult = SolveWithPolicy(
                combat,
                growthBudgets: new GrowthValues(Feed: 10),
                stopAtZeroLoss: true);
            AssertVictory(fundedResult, "B2/Feed-FundedControl");
            Check(fundedResult.Snapshot.GrowthRewards.Feed == 1,
                $"B2/Feed-FundedControl: expected Feed=1 when GrowthBudgets.Feed=10, got {fundedResult.Snapshot.GrowthRewards}, actions=[{FormatActions(fundedResult)}].");
        });

        // B3: IgnoreLongTermRewards = true (even with BurningBlood healing covering the 5 HP damage!)
        //     -> MUST NOT pay extra HP/turns for growth, and GrowthRewards.Total == 0 && LongTermResourceValue == 0.
        RunCheck(outcomes, SuiteBName, "B3/IgnoreLongTermRewards", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATB3",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Relics = ["BURNING_BLOOD"],
                    PlayerMaxHp = 80,
                    PlayerHp = 80,
                    PotionSlotCount = 2,
                    Potions = [],
                    // Turn 1 AcidGoop deals 5 damage (covered by BurningBlood +6 heal after victory).
                    InitialPowers = [new PowerInjectionSpec("STRENGTH_POWER", 1, OnPlayer: false, EnemyIndex: 0)],
                    Cards =
                    [
                        new CardInjectionSpec("BLUDGEON"),
                        new CardInjectionSpec("STRIKE_IRONCLAD"),
                        new CardInjectionSpec("HAND_OF_GREED"),
                    ],
                },
                enemyHpBySlot: [30]);

            // Control with IgnoreLongTermRewards = false: BurningBlood absorbs the 5 HP so solver takes Turn 2 HandOfGreed.
            SolverResult enabledResult = SolveWithPolicy(
                combat,
                growthBudgets: new GrowthValues(HandOfGreed: 15),
                ignoreLongTermRewards: false,
                stopAtZeroLoss: true);
            AssertVictory(enabledResult, "B3/IgnoreLongTerm-ControlEnabled");
            Check(enabledResult.Snapshot.GrowthRewards.HandOfGreed == 1 && enabledResult.CombatEndedTurn == 2,
                $"B3/IgnoreLongTerm-ControlEnabled: expected Turn-2 HandOfGreed kill with BurningBlood, got turn={enabledResult.CombatEndedTurn}, growth={enabledResult.Snapshot.GrowthRewards}, actions=[{FormatActions(enabledResult)}].");

            // Target check: IgnoreLongTermRewards = true -> fast Turn-1 0-HP Bludgeon kill, zero materialized growth/LTV.
            SolverResult ignoredResult = SolveWithPolicy(
                combat,
                growthBudgets: new GrowthValues(HandOfGreed: 15),
                ignoreLongTermRewards: true,
                stopAtZeroLoss: true);
            AssertVictory(ignoredResult, "B3/IgnoreLongTerm-True");
            Check(ignoredResult.CombatEndedTurn == 1
                && ignoredResult.Snapshot.CumulativePlayerHpLost == 0
                && ignoredResult.Snapshot.GrowthRewards.Total == 0
                && ignoredResult.Snapshot.LongTermResourceValue == 0,
                $"B3/IgnoreLongTerm-True: expected Turn-1 0-HP kill with Growth=0 & LTV=0, got turn={ignoredResult.CombatEndedTurn}, hpLost={ignoredResult.Snapshot.CumulativePlayerHpLost}, growth={ignoredResult.Snapshot.GrowthRewards}, LTV={ignoredResult.Snapshot.LongTermResourceValue}, actions=[{FormatActions(ignoredResult)}].");
        });
    }

    // =========================================================================
    // Suite C: HotSearch Relic Counter ("卡遗物") End-to-End
    // =========================================================================

    private static void RunSuiteC_HotSearchRelicCountersEndToEnd(MainLoopContext loop, List<CheckOutcome> outcomes)
    {
        // C1a: Attack counter relic (Nunchaku starting at 8, target 9..9)
        //      Greedy 0-HP 2-Attack kill (Strike + Strike -> 10/0) vs 1-Attack aligned kill (Hemokinesis -2 HP -> 9).
        RunCheck(outcomes, SuiteCName, "C1a/Nunchaku-Aligned", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATC1A",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Relics = ["NUNCHAKU"],
                    PlayerMaxHp = 80,
                    PlayerHp = 70,
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards =
                    [
                        new CardInjectionSpec("STRIKE_IRONCLAD"),
                        new CardInjectionSpec("STRIKE_IRONCLAD"),
                        new CardInjectionSpec("HEMOKINESIS"),
                    ],
                },
                enemyHpBySlot: [12]);

            Player player = LocalContext.GetMe(combat)!;
            Nunchaku nunchaku = player.Relics.OfType<Nunchaku>().Single();
            nunchaku.AttacksPlayed = 8;

            // Control: with relic strategy disabled, greedy 0-HP Strike+Strike wins.
            SolverResult control = SolveWithPolicy(combat, relicStrategyEnabled: false, stopAtZeroLoss: true);
            AssertVictory(control, "C1a/Nunchaku-ControlDisabled");
            Check(control.Snapshot.CumulativePlayerHpLost == 0,
                $"C1a/Nunchaku-ControlDisabled: expected 0-HP greedy kill, got HpLost={control.Snapshot.CumulativePlayerHpLost}, actions=[{FormatActions(control)}].");

            // Enabled: target 9..9 with HpAllowance=10 -> MUST pick 1-Attack aligned Hemokinesis route.
            RelicCounterRule[] rules = [new RelicCounterRule(RelicCounterId.Nunchaku, Enabled: true, Minimum: 9, Maximum: 9, HpAllowance: 10, Priority: 1)];
            SolverResult aligned = SolveWithPolicy(combat, relicStrategyEnabled: true, relicCounterRules: rules, stopAtZeroLoss: true);
            AssertVictory(aligned, "C1a/Nunchaku-Aligned");
            ulong expectedBit = 1UL << (int)RelicCounterId.Nunchaku;
            Check((aligned.Snapshot.RelicCounters.SatisfiedMask & expectedBit) != 0
                && aligned.Snapshot.RelicCounters.Value(RelicCounterId.Nunchaku) == 9
                && aligned.Snapshot.RelicCounters.HpCredit == 10,
                $"C1a/Nunchaku-Aligned: expected SatisfiedMask bit & value=9 & HpCredit=10, got mask=0x{aligned.Snapshot.RelicCounters.SatisfiedMask:X}, val={aligned.Snapshot.RelicCounters.Value(RelicCounterId.Nunchaku)}, hpCredit={aligned.Snapshot.RelicCounters.HpCredit}, actions=[{FormatActions(aligned)}].");

            string? uiSummary = SolverStrategyOutcomeText.Format(
                aligned.Snapshot.RelicCounters, aligned.Snapshot.GrowthRewards, victory: true);
            Check(!string.IsNullOrWhiteSpace(uiSummary) && uiSummary.Contains("9", StringComparison.Ordinal),
                $"C1a/Nunchaku-Aligned: expected non-empty UI summary containing '9', got '{uiSummary ?? "<null>"}'.");
        });

        // C1b: Attack counter relic (PenNib starting at 8, target 9..9)
        RunCheck(outcomes, SuiteCName, "C1b/PenNib-Aligned", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATC1B",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Relics = ["PEN_NIB"],
                    PlayerMaxHp = 80,
                    PlayerHp = 70,
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards =
                    [
                        new CardInjectionSpec("STRIKE_IRONCLAD"),
                        new CardInjectionSpec("STRIKE_IRONCLAD"),
                        new CardInjectionSpec("HEMOKINESIS"),
                    ],
                },
                enemyHpBySlot: [12]);

            Player player = LocalContext.GetMe(combat)!;
            PenNib penNib = player.Relics.OfType<PenNib>().Single();
            for (int i = 0; i < 8; i++)
                penNib.NotifyAttackPlayed();

            RelicCounterRule[] rules = [new RelicCounterRule(RelicCounterId.PenNib, Enabled: true, Minimum: 9, Maximum: 9, HpAllowance: 10, Priority: 1)];
            SolverResult aligned = SolveWithPolicy(combat, relicStrategyEnabled: true, relicCounterRules: rules, stopAtZeroLoss: true);
            AssertVictory(aligned, "C1b/PenNib-Aligned");
            ulong expectedBit = 1UL << (int)RelicCounterId.PenNib;
            Check((aligned.Snapshot.RelicCounters.SatisfiedMask & expectedBit) != 0
                && aligned.Snapshot.RelicCounters.Value(RelicCounterId.PenNib) == 9
                && aligned.Snapshot.RelicCounters.HpCredit == 10,
                $"C1b/PenNib-Aligned: expected SatisfiedMask bit & value=9 & HpCredit=10, got mask=0x{aligned.Snapshot.RelicCounters.SatisfiedMask:X}, val={aligned.Snapshot.RelicCounters.Value(RelicCounterId.PenNib)}, hpCredit={aligned.Snapshot.RelicCounters.HpCredit}, actions=[{FormatActions(aligned)}].");

            string? uiSummary = SolverStrategyOutcomeText.Format(
                aligned.Snapshot.RelicCounters, aligned.Snapshot.GrowthRewards, victory: true);
            Check(!string.IsNullOrWhiteSpace(uiSummary) && uiSummary.Contains("9", StringComparison.Ordinal),
                $"C1b/PenNib-Aligned: expected non-empty UI summary containing '9', got '{uiSummary ?? "<null>"}'.");
        });

        // C2: TurnTick relic (HappyFlower starting at 1, target 2..2)
        //     Greedy Turn-1 0-HP kill (counter=1) vs safe-block Turn-1->Turn-2 stall (counter=2, 0 HP lost).
        RunCheck(outcomes, SuiteCName, "C2/HappyFlower-StallTurn2", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATC2",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Relics = ["HAPPY_FLOWER"],
                    PlayerMaxHp = 80,
                    PlayerHp = 70,
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards =
                    [
                        new CardInjectionSpec("STRIKE_IRONCLAD"),
                        new CardInjectionSpec("DEFEND_IRONCLAD"),
                    ],
                },
                enemyHpBySlot: [6]);

            Player player = LocalContext.GetMe(combat)!;
            HappyFlower flower = player.Relics.OfType<HappyFlower>().Single();
            flower.TurnsSeen = 1;

            RelicCounterRule[] rules = [new RelicCounterRule(RelicCounterId.HappyFlower, Enabled: true, Minimum: 2, Maximum: 2, HpAllowance: 8, Priority: 1)];
            SolverResult aligned = SolveWithPolicy(combat, relicStrategyEnabled: true, relicCounterRules: rules, stopAtZeroLoss: true);
            AssertVictory(aligned, "C2/HappyFlower-StallTurn2");
            ulong expectedBit = 1UL << (int)RelicCounterId.HappyFlower;
            Check(aligned.CombatEndedTurn == 2
                && aligned.Snapshot.CumulativePlayerHpLost == 0
                && (aligned.Snapshot.RelicCounters.SatisfiedMask & expectedBit) != 0
                && aligned.Snapshot.RelicCounters.Value(RelicCounterId.HappyFlower) == 2
                && aligned.Snapshot.RelicCounters.HpCredit == 8,
                $"C2/HappyFlower-StallTurn2: expected Turn-2 0-HP aligned kill (val=2, HpCredit=8), got turn={aligned.CombatEndedTurn}, hpLost={aligned.Snapshot.CumulativePlayerHpLost}, mask=0x{aligned.Snapshot.RelicCounters.SatisfiedMask:X}, val={aligned.Snapshot.RelicCounters.Value(RelicCounterId.HappyFlower)}, hpCredit={aligned.Snapshot.RelicCounters.HpCredit}, actions=[{FormatActions(aligned)}].");

            string? uiSummary = SolverStrategyOutcomeText.Format(
                aligned.Snapshot.RelicCounters, aligned.Snapshot.GrowthRewards, victory: true);
            Check(!string.IsNullOrWhiteSpace(uiSummary) && uiSummary.Contains("2", StringComparison.Ordinal),
                $"C2/HappyFlower-StallTurn2: expected non-empty UI summary containing '2', got '{uiSummary ?? "<null>"}'.");
        });

        // C3: MeatOnTheBone wounded threshold (<= 50% MaxHp triggers +12 post-combat heal)
        RelicCounterRule[] meatRules =
            [new RelicCounterRule(RelicCounterId.MeatOnTheBone, Enabled: true, Minimum: 1, Maximum: 1, HpAllowance: 12, Priority: 1)];
        ulong meatBit = 1UL << (int)RelicCounterId.MeatOnTheBone;

        // C3a: Net-positive HP selling (41/80 -> sell 2 HP via Hemokinesis -> 39/80 <= 40 -> +12 heal = 51/80, net +10 HP).
        //      MUST prefer wounded route with HealingHpCredit == 12 while StrategyGoalHpCredit == 0 (HpCredit excludes MeatOnTheBone).
        RunCheck(outcomes, SuiteCName, "C3a/MeatOnTheBone-NetPositive", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATC3A",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Relics = ["MEAT_ON_THE_BONE"],
                    PlayerMaxHp = 80,
                    PlayerHp = 41,
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards =
                    [
                        new CardInjectionSpec("BLUDGEON"),
                        new CardInjectionSpec("HEMOKINESIS"),
                    ],
                },
                enemyHpBySlot: [12]);

            SolverResult result = SolveWithPolicy(combat, relicStrategyEnabled: true, relicCounterRules: meatRules, stopAtZeroLoss: true);
            AssertVictory(result, "C3a/MeatOnTheBone-NetPositive");
            Check(result.Snapshot.PlayerHp == 39
                && (result.Snapshot.RelicCounters.SatisfiedMask & meatBit) != 0
                && result.Snapshot.RelicCounters.HealingHpCredit == 12
                && result.Snapshot.RelicCounters.HpCredit == 0
                && result.Snapshot.StrategyGoalHpCredit == 0
                && result.Snapshot.StrategicHpCredit == 12
                && result.PostCombatRelicHeal == 12,
                $"C3a/MeatOnTheBone-NetPositive: expected PlayerHp=39, HealingHpCredit=12, HpCredit=0, StrategyGoalHpCredit=0, PostCombatRelicHeal=12; got hp={result.Snapshot.PlayerHp}, mask=0x{result.Snapshot.RelicCounters.SatisfiedMask:X}, healCredit={result.Snapshot.RelicCounters.HealingHpCredit}, hpCredit={result.Snapshot.RelicCounters.HpCredit}, goalCredit={result.Snapshot.StrategyGoalHpCredit}, postHeal={result.PostCombatRelicHeal}, actions=[{FormatActions(result)}].");
        });

        // C3b: Break-even HP selling (52/80 -> Offering + Offering loses 12 HP -> 40/80 + 12 heal = 52/80)
        //      MUST NOT beat keeping 52/80 with 0 raw HP loss!
        RunCheck(outcomes, SuiteCName, "C3b/MeatOnTheBone-BreakEven", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATC3B",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Relics = ["MEAT_ON_THE_BONE"],
                    PlayerMaxHp = 80,
                    PlayerHp = 52,
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards =
                    [
                        new CardInjectionSpec("BLUDGEON"),
                        new CardInjectionSpec("OFFERING"),
                        new CardInjectionSpec("OFFERING"),
                    ],
                },
                enemyHpBySlot: [12]);

            SolverResult result = SolveWithPolicy(combat, relicStrategyEnabled: true, relicCounterRules: meatRules, stopAtZeroLoss: true);
            AssertVictory(result, "C3b/MeatOnTheBone-BreakEven");
            Check(result.Snapshot.PlayerHp == 52
                && result.Snapshot.CumulativePlayerHpLost == 0
                && result.Snapshot.RelicCounters.HealingHpCredit == 0
                && result.Snapshot.RelicCounters.SatisfiedMask == 0,
                $"C3b/MeatOnTheBone-BreakEven: break-even 12-HP sell MUST NOT beat keeping 52/80; got hp={result.Snapshot.PlayerHp}, hpLost={result.Snapshot.CumulativePlayerHpLost}, healCredit={result.Snapshot.RelicCounters.HealingHpCredit}, mask=0x{result.Snapshot.RelicCounters.SatisfiedMask:X}, actions=[{FormatActions(result)}].");
        });

        // C3c: Net-loss HP selling (53/80 -> Offering + Offering + Hemokinesis loses 14 HP -> 39/80 + 12 = 51/80, net -2 HP)
        //      MUST NOT beat keeping 53/80!
        RunCheck(outcomes, SuiteCName, "C3c/MeatOnTheBone-NetLoss", () =>
        {
            CombatState combat = StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATC3C",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    Relics = ["MEAT_ON_THE_BONE"],
                    PlayerMaxHp = 80,
                    PlayerHp = 53,
                    PotionSlotCount = 2,
                    Potions = [],
                    Cards =
                    [
                        new CardInjectionSpec("BLUDGEON"),
                        new CardInjectionSpec("OFFERING"),
                        new CardInjectionSpec("OFFERING"),
                        new CardInjectionSpec("HEMOKINESIS"),
                    ],
                },
                enemyHpBySlot: [12]);

            SolverResult result = SolveWithPolicy(combat, relicStrategyEnabled: true, relicCounterRules: meatRules, stopAtZeroLoss: true);
            AssertVictory(result, "C3c/MeatOnTheBone-NetLoss");
            Check(result.Snapshot.PlayerHp == 53
                && result.Snapshot.CumulativePlayerHpLost == 0
                && result.Snapshot.RelicCounters.HealingHpCredit == 0
                && result.Snapshot.RelicCounters.SatisfiedMask == 0,
                $"C3c/MeatOnTheBone-NetLoss: net-loss 14-HP sell MUST NOT beat keeping 53/80; got hp={result.Snapshot.PlayerHp}, hpLost={result.Snapshot.CumulativePlayerHpLost}, healCredit={result.Snapshot.RelicCounters.HealingHpCredit}, mask=0x{result.Snapshot.RelicCounters.SatisfiedMask:X}, actions=[{FormatActions(result)}].");
        });
    }

    // =========================================================================
    // Suite D: BrightestFlameMaxHpLossLimit End-to-End
    // =========================================================================

    private static void RunSuiteD_BrightestFlameLimitEndToEnd(MainLoopContext loop, List<CheckOutcome> outcomes)
    {
        static CombatState CreateBrightestFlameCombat(MainLoopContext loop)
            => StartConfiguredCombat(
                loop,
                new CombatSetupSpec
                {
                    CharacterId = "IRONCLAD",
                    EncounterId = "FUZZY_WURM_CRAWLER_WEAK",
                    Seed = "STRATD1",
                    ClearStarterDeck = true,
                    ClearStarterRelics = true,
                    PlayerMaxHp = 80,
                    PlayerHp = 70,
                    PotionSlotCount = 2,
                    Potions = [],
                    // AcidGoop base 4 + 10 Strength = 14 damage on Turn 1 -> huge incentive to play BrightestFlame when allowed.
                    InitialPowers = [new PowerInjectionSpec("STRENGTH_POWER", 10, OnPlayer: false, EnemyIndex: 0)],
                    Cards =
                    [
                        new CardInjectionSpec("BRIGHTEST_FLAME"),
                        new CardInjectionSpec("BASH"),
                        new CardInjectionSpec("BLUDGEON"),
                    ],
                },
                enemyHpBySlot: [40]);

        // D1: Control: Unlimited (null) -> MUST play BrightestFlame for Turn-1 0-HP lethal (-2 MaxHp beats -14 HP).
        RunCheck(outcomes, SuiteDName, "D1/BrightestFlame-ControlUnlimited", () =>
        {
            CombatState combat = CreateBrightestFlameCombat(loop);
            SolverResult unlimitedResult = SolveWithPolicy(combat, brightestFlameLimit: null, stopAtZeroLoss: true);
            AssertVictory(unlimitedResult, "D1/BrightestFlame-ControlUnlimited");
            Check(unlimitedResult.BestNode.Actions.Any(static a => a.Kind == PlanActionKind.PlayCard && a.CardId == "BRIGHTEST_FLAME")
                && unlimitedResult.CombatEndedTurn == 1
                && unlimitedResult.Snapshot.PlayerMaxHp == 78,
                $"D1/BrightestFlame-ControlUnlimited: expected BrightestFlame played on Turn 1 (MaxHp=78), got turn={unlimitedResult.CombatEndedTurn}, maxHp={unlimitedResult.Snapshot.PlayerMaxHp}, actions=[{FormatActions(unlimitedResult)}].");
        });

        // D2: Enforced: BrightestFlameMaxHpLossLimit = 0 -> returned plan MUST NEVER play BrightestFlame.
        RunCheck(outcomes, SuiteDName, "D2/BrightestFlame-LimitZero", () =>
        {
            CombatState combat = CreateBrightestFlameCombat(loop);
            SolverResult blockedResult = SolveWithPolicy(combat, brightestFlameLimit: 0, stopAtZeroLoss: true);
            AssertVictory(blockedResult, "D2/BrightestFlame-LimitZero");
            Check(!blockedResult.BestNode.Actions.Any(static a => a.Kind == PlanActionKind.PlayCard && a.CardId == "BRIGHTEST_FLAME")
                && blockedResult.Snapshot.PlayerMaxHp == 80,
                $"D2/BrightestFlame-LimitZero: BrightestFlame MUST NEVER be played when BrightestFlameMaxHpLossLimit=0; got maxHp={blockedResult.Snapshot.PlayerMaxHp}, actions=[{FormatActions(blockedResult)}].");
        });
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static CombatState StartConfiguredCombat(
        MainLoopContext loop,
        CombatSetupSpec spec,
        int[]? enemyHpBySlot = null)
    {
        CombatState combat = RealCombatDriver.StartCombat(loop, spec);
        Check(RealCombatDriver.GetLiveOutcome(combat) == HotCombatOutcome.Ongoing,
            $"StartConfiguredCombat({spec.Seed}): combat unexpectedly ended during setup ({RealCombatDriver.GetLiveOutcome(combat)}).");

        if (enemyHpBySlot != null)
        {
            for (int i = 0; i < enemyHpBySlot.Length && i < combat.Enemies.Count; i++)
            {
                int targetHp = enemyHpBySlot[i];
                if (targetHp <= 0)
                    continue;
                Creature enemy = combat.Enemies[i];
                if (enemy.MaxHp < targetHp)
                    enemy.SetMaxHpInternal(targetHp);
                enemy.SetCurrentHpInternal(targetHp);
            }
        }

        return combat;
    }

    private static void ConfigureMadScienceImprovement(CombatState combat)
    {
        Player player = LocalContext.GetMe(combat)!;
        int configured = 0;
        foreach (MadScience ms in player.Deck.Cards.OfType<MadScience>())
        {
            ms.TinkerTimeType = CardType.Power;
            ms.TinkerTimeRider = TinkerTime.RiderEffect.Improvement;
            configured++;
        }
        foreach (MadScience ms in player.PlayerCombatState!.AllCards.OfType<MadScience>())
        {
            ms.TinkerTimeType = CardType.Power;
            ms.TinkerTimeRider = TinkerTime.RiderEffect.Improvement;
            configured++;
        }
        Check(configured > 0, "ConfigureMadScienceImprovement: no MadScience card found in deck or combat piles.");
    }

    private static int FindHandCardIndex(in CombatHotState state, HotRules rules, string cardEntry)
    {
        for (int i = 0; i < state.HandCount; i++)
        {
            ushort defId = state.Cards[i].CardDefId;
            if (defId < rules.Cards.Length && string.Equals(rules.Cards[defId].Entry, cardEntry, StringComparison.Ordinal))
                return i;
        }
        List<string> handEntries = new(state.HandCount);
        for (int i = 0; i < state.HandCount; i++)
        {
            ushort defId = state.Cards[i].CardDefId;
            handEntries.Add(defId < rules.Cards.Length ? rules.Cards[defId].Entry : "?");
        }
        throw new InvalidOperationException($"Hand does not contain expected card '{cardEntry}'; hand=[{string.Join(", ", handEntries)}].");
    }

    private static string FormatGrowthVector(in CombatHotState state)
    {
        ushort[] values = new ushort[GrowthCount];
        for (int i = 0; i < GrowthCount; i++)
            values[i] = state.Growth[i];
        return string.Join(",", values);
    }

    private static void PlayCardInHotKernel(
        ref CombatHotState state,
        ref HotColdSidecar? cold,
        HotRules rules,
        HotExecScratch scratch,
        string cardEntry,
        int targetEnemySlot)
    {
        int handIdx = FindHandCardIndex(in state, rules, cardEntry);
        HotStepResult res = HotCombatEngine.Step(
            ref state, ref cold, rules, scratch, HotAction.PlayCard(handIdx, targetEnemySlot));
        Check(res.Executed && !res.NeedsChoice,
            $"HotCombatEngine.Step({cardEntry}, slot={handIdx}, target={targetEnemySlot}) failed: Executed={res.Executed}, NeedsChoice={res.NeedsChoice}, Outcome={res.Outcome}.");
    }

    private static void AssertExactGrowth(
        in CombatHotState state,
        int expectedIndex,
        int expectedCount,
        int expectedLongTermValue,
        string label)
    {
        for (int i = 0; i < GrowthCount; i++)
        {
            int expected = i == expectedIndex ? expectedCount : 0;
            int actual = state.Growth[i];
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"{label}: Growth[{i}] mismatch: expected {expected}, got {actual} (full Growth=[{FormatGrowthVector(in state)}], LTV={state.LongTermResourceValue}).");
            }
        }

        if (state.LongTermResourceValue != expectedLongTermValue)
        {
            throw new InvalidOperationException(
                $"{label}: LongTermResourceValue mismatch: expected {expectedLongTermValue}, got {state.LongTermResourceValue} (Growth=[{FormatGrowthVector(in state)}]).");
        }
    }

    private static SolverResult SolveWithPolicy(
        CombatState combat,
        GrowthValues growthBudgets = default,
        bool relicStrategyEnabled = true,
        RelicCounterRule[]? relicCounterRules = null,
        int? brightestFlameLimit = null,
        bool ignoreLongTermRewards = false,
        bool stopAtZeroLoss = true)
    {
        SolverSearchProfile profile = SolverSearchProfile.Default;
        SolverSettings.ApplyForTesting(new SolverSettingsData
        {
            PerformanceMigrationVersion = SolverSettings.CurrentPerformanceMigrationVersion,
            PerformancePreset = SolverPerformancePreset.Medium,
            SearchMaxExpandedNodes = profile.MaxExpandedNodes,
            SearchBeamWidth = profile.BeamWidth,
            SearchTimeLimitSeconds = 15,
            SearchMaxDegreeOfParallelism = 1,
            SearchMaxCardBranchesPerNode = profile.MaxCardBranchesPerNode,
            SearchMaxPileChoiceBranchesPerAction = profile.MaxPileChoiceBranchesPerAction,
            SearchMaxHandChoiceBranchesPerAction = profile.MaxHandChoiceBranchesPerAction,
            StopAtAcceptableBattleHpLoss = stopAtZeroLoss,
            AcceptableBattleHpLoss = 0,
            OnlineStatisticsEnabled = false,
            SearchCompletionNotificationsEnabled = false,
            PotionPolicy = SolverPotionPolicy.Smart,
            GrowthBudgets = growthBudgets,
            RelicStrategyEnabled = relicStrategyEnabled,
            RelicCounterRules = relicCounterRules ?? [],
            BrightestFlameMaxHpLossLimit = brightestFlameLimit,
            IgnoreLongTermRewards = ignoreLongTermRewards,
        });

        BattleDamageTracker.Reset();
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SolverTheftPolicy? theftPolicy = SolverController.ResolveTheftPolicy(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            settings, combat, includeTurnSetup: false, theftPolicy: theftPolicy);

        return HotSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null);
    }

    private static void AssertVictory(SolverResult result, string label)
    {
        Check(result.Snapshot.AllEnemiesDead && !result.Snapshot.PlayerDead,
            $"{label}: expected Victory, got AllEnemiesDead={result.Snapshot.AllEnemiesDead}, PlayerDead={result.Snapshot.PlayerDead}, EnemyHp={result.Snapshot.EnemyHp}, Boundary={result.BoundaryReason}, actions=[{FormatActions(result)}].");
    }

    private static string FormatActions(SolverResult result)
        => string.Join(" -> ", result.BestNode.Actions.Select(static a =>
            $"T{a.Turn}:{a.Kind}:{(string.IsNullOrEmpty(a.CardId) ? a.PotionId : a.CardId)}"));

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
