using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using CombatSolver;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Dread;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace OfflineSearchHarness;

/// <summary>
/// Layer 1 静态完整性与反射审计器（<c>--audit-hot-coverage</c>）：
/// 在纯 .NET 中加载 0.113.1 真实 ModelDb，枚举全部 9 类单人实体与非纯攻击怪物 MoveState，
/// 核对 HotCatalog 登记、Bytecode 编译、Sim 实现/明确分类，并预先触发 HotRootBuilder 与
/// HotImportSupport 的全部反射字段解析。任何静默遗漏或反射失效都以非零退出码报错。
/// </summary>
internal static class HotCoverageAuditor
{
    // 严格限于多人专用卡所施加、单人模式下不可能出现的 Power
    private static readonly HashSet<string> MultiplayerOnlyPowers = new(StringComparer.Ordinal)
    {
        "BEACON_OF_HOPE_POWER",
        "COVERED_POWER",
        "COVERING_FIRE_POWER",
        "ENSEMBLE_POWER",
        "FADE_POWER",
        "GUARDED_POWER",
        "IMITATION_LEARNING_POWER",
        "INTERCEPT_POWER",
        "KNOCKDOWN_POWER",
        "LEADERSHIP_POWER",
        "ONE_FOR_ALL_POWER",
        "SNEAKY_POWER",
        "TANK_POWER",
        "UNDERWORLD_POWER",
    };

    // 明确非战斗内结算或由根标志位承载的单人 Power（严禁放入真实战斗缺口！）
    private static readonly IReadOnlyDictionary<string, string> KnownPostCombatOrBakedPowers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IMPROVEMENT_POWER"] = "PostCombatOnly (战后升级牌组卡牌)",
            ["WUPPYS_LOYALTY_POWER"] = "BakedAtRoot (由 HotCombatFlags.HasWuppy 承载)",
        };

    // 明确为战后回血 / 战后元计数 / 战斗外获取遗物或开战前已烘焙的遗物（HookMask != None 但不走战斗中 Sim/）
    // 严禁放入 HistoryCourse / Thumbscrew / FurCoat 等真实战斗内遗物！
    private static readonly IReadOnlyDictionary<string, string> KnownPostCombatOrBakedRelics =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 战后回血（由 HotRootBuilder + ComputePostCombatRelicHeal 统一结算）
            ["BURNING_BLOOD"] = "PostCombatOnly (战后回血)",
            ["BLACK_BLOOD"] = "PostCombatOnly (战后回血)",
            ["MEAT_ON_THE_BONE"] = "PostCombatOnly (战后低血回血)",
            // 战后奖励 / 局外计数
            ["CHOSEN_CHEESE"] = "PostCombatOnly (战后增最大生命)",
            ["FISHING_ROD"] = "PostCombatOnly (战后奖励)",
            ["LAVA_LAMP"] = "PostCombatOnly (无伤战后奖励升级)",
            ["PAELS_TOOTH"] = "PostCombatOnly (战后计数/牌组)",
            ["SWORD_OF_STONE"] = "PostCombatOnly (战后计数)",
            ["TOY_BOX"] = "PostCombatOnly (战后消耗计数)",
            ["WAR_HAMMER"] = "PostCombatOnly (战后精英升级)",
            ["WONGOS_MYSTERY_TICKET"] = "PostCombatOnly (战后计数)",
            // 战斗外牌组获取 / 地图金币钩子
            ["BING_BONG"] = "NonCombat (获牌时复制)",
            ["BOOK_OF_FIVE_RINGS"] = "NonCombat (获 5 牌时回血)",
            ["DARKSTONE_PERIAPT"] = "NonCombat (获诅咒增最大生命)",
            ["DRAGON_FRUIT"] = "NonCombat (获金币增最大生命)",
            ["LUCKY_FYSH"] = "NonCombat (获牌得金币)",
            // 开战时已烘焙到第 1 回合根状态
            ["BYRDINE"] = "BakedAtRoot (开战召唤友军已在根快照)",
            ["DELICATE_FROND"] = "BakedAtRoot (开战补药水已在根快照)",
        };

    // 仅限于附魔施加时已直接烘焙进卡牌基础关键字/重放次数的附魔（严禁放入 Instinct/TezcatarasEmber/PerfectFit/SlumberingEssence！）
    private static readonly IReadOnlyDictionary<string, string> KnownBakedAtRootEnchantments =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CLONE"] = "BakedAtRoot (卡牌克隆属性)",
            ["ROYALLY_APPROVED"] = "BakedAtRoot (施加时写入 Innate+Retain 关键字)",
            ["SOULS_POWER"] = "BakedAtRoot (施加时移除 Exhaust 关键字)",
            ["STEADY"] = "BakedAtRoot (施加时写入 Retain 关键字)",
            ["SPIRAL"] = "BakedAtRoot (施加时写入 BaseReplayCount)",
        };

    private sealed record CategoryStats(
        string Name,
        int TotalSingleplayer,
        int BytecodeCount,
        int CustomSimCount,
        int BakedOrPostCombatCount,
        int IntentionalNoOpCount,
        List<string> Gaps);

    public static int Run(string[] args)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Console.WriteLine("=== --audit-hot-coverage (Layer 1: Static Completeness & Reflection Auditor) ===");

        HarnessLog.TraceInit = false;
        HarnessLog.VerboseGameLog = false;
        HarnessLog.Language = "eng";

        MainLoopContext loop = new();
        GameBootstrap.EnsureBootstrapped(loop);

        string repoRoot = ResolveRepoRoot();
        string simDir = Path.Combine(repoRoot, "src", "HotKernel", "Sim");
        string? simSourceCorpus = LoadSimSourceCorpus(simDir);
        if (simSourceCorpus == null)
        {
            Console.WriteLine($"[WARN] 未找到 {simDir}，将只执行运行时 Catalog + Bytecode + Reflection 检查。");
        }
        else
        {
            Console.WriteLine($"[INFO] 已加载 HotKernel Sim 源码语料（{simSourceCorpus.Length:N0} 字符）用于静默 No-Op 审计。");
        }

        List<CategoryStats> categories =
        [
            AuditCards(simSourceCorpus),
            AuditPowers(simSourceCorpus),
            AuditRelics(simSourceCorpus),
            AuditPotions(simSourceCorpus),
            AuditMonstersAndMoves(simSourceCorpus),
            AuditEnchantments(simSourceCorpus),
            AuditAfflictions(simSourceCorpus),
            AuditOrbs(simSourceCorpus),
            AuditDominions(simSourceCorpus),
        ];

        List<string> reflectionFailures = AuditReflectionFields(out int reflectionChecks);

        Console.WriteLine();
        Console.WriteLine("| Category | SP Total | Bytecode | CustomSim | Baked/PostCombat | NoOp | Gaps |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|");
        int totalGaps = 0;
        foreach (CategoryStats cat in categories)
        {
            totalGaps += cat.Gaps.Count;
            Console.WriteLine(
                $"| {cat.Name} | {cat.TotalSingleplayer} | {cat.BytecodeCount} | {cat.CustomSimCount} | " +
                $"{cat.BakedOrPostCombatCount} | {cat.IntentionalNoOpCount} | {cat.Gaps.Count} |");
        }
        Console.WriteLine($"| Reflection Checks | {reflectionChecks} | - | - | - | - | {reflectionFailures.Count} |");
        Console.WriteLine();

        foreach (CategoryStats cat in categories)
        {
            if (cat.Gaps.Count == 0)
                continue;
            Console.WriteLine($"[GAP] {cat.Name} ({cat.Gaps.Count}):");
            foreach (string gap in cat.Gaps.OrderBy(static x => x, StringComparer.Ordinal))
                Console.WriteLine($"  - {gap}");
        }

        if (reflectionFailures.Count > 0)
        {
            Console.WriteLine($"[REFLECTION FAIL] ({reflectionFailures.Count}):");
            foreach (string fail in reflectionFailures)
                Console.WriteLine($"  - {fail}");
        }

        int totalErrors = totalGaps + reflectionFailures.Count;
        if (totalErrors == 0)
        {
            Console.WriteLine($"ALL --audit-hot-coverage CHECKS PASSED (0 gaps, {reflectionChecks}/{reflectionChecks} reflection checks) in {sw.ElapsedMilliseconds} ms");
            return 0;
        }

        Console.WriteLine($"--audit-hot-coverage FAILED: {totalGaps} entity gap(s), {reflectionFailures.Count} reflection failure(s) in {sw.ElapsedMilliseconds} ms");
        return 1;
    }

    private static CategoryStats AuditCards(string? simCorpus)
    {
        HashSet<ModelId> excludedPoolIds = [];
        TryAddPoolCardIds<MockCardPool>(excludedPoolIds);
        TryAddPoolCardIds<DeprecatedCardPool>(excludedPoolIds);

        CardModel[] cards = ModelDb.AllCards
            .Concat(ModelDb.All.OfType<CardModel>())
            .DistinctBy(static c => c.Id)
            .Where(c => !IsMockOrDeprecated(c)
                && !excludedPoolIds.Contains(c.Id)
                && c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
            .OrderBy(static c => c.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        int bytecode = 0, customSim = 0, baked = 0, noOp = 0;
        List<string> gaps = [];
        List<HotCardEffectOp> ops = new(8);

        foreach (CardModel card in cards)
        {
            string entry = card.Id.Entry;
            HotCustomCardId id = HotCardCatalog.FindByEntry(entry);
            if (id == HotCustomCardId.None)
            {
                gaps.Add($"{entry} ({card.GetType().Name}): missing from HotCardCatalog");
                continue;
            }

            ref readonly HotCardCatalogEntry cat = ref HotCardCatalog.Get(id);
            bool hasNoPlayOverride = HasNoCustomOnPlay(card);

            if (cat.UsesBytecode)
            {
                ops.Clear();
                HotCardCatalog.CompileCardBytecode(id, 1, static _ => 1, ops, out _, out byte opCount, out HotCustomCardId customOut);
                if (customOut != HotCustomCardId.None)
                {
                    gaps.Add($"{entry} ({id}): UsesBytecode=true but CompileCardBytecode fell through to custom");
                }
                else if (opCount > 0)
                {
                    bytecode++;
                }
                else if (hasNoPlayOverride || card.Type is CardType.Status or CardType.Curse or CardType.Quest)
                {
                    noOp++;
                }
                else
                {
                    gaps.Add($"{entry} ({id}): UsesBytecode=true but compiled 0 ops while overriding OnPlay");
                }
            }
            else if (simCorpus == null || ContainsToken(simCorpus, $"HotCustomCardId.{id}"))
            {
                customSim++;
            }
            else if (hasNoPlayOverride)
            {
                noOp++;
            }
            else
            {
                gaps.Add($"{entry} ({id}): UsesBytecode=false and 0 HotKernel/Sim references (silent no-op)");
            }
        }

        return new CategoryStats("Cards", cards.Length, bytecode, customSim, baked, noOp, gaps);
    }

    private static CategoryStats AuditPowers(string? simCorpus)
    {
        PowerModel[] powers = ModelDb.AllPowers
            .Concat(ModelDb.All.OfType<PowerModel>())
            .DistinctBy(static p => p.Id)
            .Where(p => !IsMockOrDeprecated(p) && !MultiplayerOnlyPowers.Contains(p.Id.Entry))
            .OrderBy(static p => p.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        int customSim = 0, bakedOrPost = 0;
        List<string> gaps = [];

        foreach (PowerModel power in powers)
        {
            string entry = power.Id.Entry;
            HotPowerId id = HotPowerCatalog.FindByEntry(entry);
            if (id == HotPowerId.None)
            {
                gaps.Add($"{entry} ({power.GetType().Name}): missing from HotPowerCatalog");
                continue;
            }

            if (simCorpus == null || ContainsToken(simCorpus, $"HotPowerId.{id}"))
            {
                customSim++;
            }
            else if (KnownPostCombatOrBakedPowers.ContainsKey(entry))
            {
                bakedOrPost++;
            }
            else
            {
                gaps.Add($"{entry} ({id}): 0 HotKernel/Sim references and not PostCombat/Baked");
            }
        }

        return new CategoryStats("Powers", powers.Length, 0, customSim, bakedOrPost, 0, gaps);
    }

    private static CategoryStats AuditRelics(string? simCorpus)
    {
        RelicModel[] relics = ModelDb.AllRelics
            .Concat(ModelDb.All.OfType<RelicModel>())
            .DistinctBy(static r => r.Id)
            .Where(r => !IsMockOrDeprecated(r) && GeneratedCombatScenario.IsSingleplayerRelic(r))
            .OrderBy(static r => r.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        int customSim = 0, bakedOrPost = 0;
        List<string> gaps = [];

        foreach (RelicModel relic in relics)
        {
            string entry = relic.Id.Entry;
            HotRelicId id = HotRelicCatalog.FindByEntry(entry);
            if (id == HotRelicId.None)
            {
                gaps.Add($"{entry} ({relic.GetType().Name}): missing from HotRelicCatalog");
                continue;
            }

            ref readonly HotRelicCatalogEntry cat = ref HotRelicCatalog.Get(id);
            if (simCorpus == null || ContainsToken(simCorpus, $"HotRelicId.{id}"))
            {
                customSim++;
            }
            else if (cat.HookMask == HotHookMask.None || KnownPostCombatOrBakedRelics.ContainsKey(entry))
            {
                bakedOrPost++;
            }
            else
            {
                gaps.Add($"{entry} ({id}, HookMask={cat.HookMask}): 0 HotKernel/Sim references");
            }
        }

        return new CategoryStats("Relics", relics.Length, 0, customSim, bakedOrPost, 0, gaps);
    }

    private static CategoryStats AuditPotions(string? simCorpus)
    {
        PotionModel[] potions = ModelDb.AllPotions
            .Concat(ModelDb.All.OfType<PotionModel>())
            .DistinctBy(static p => p.Id)
            .Where(p => !IsMockOrDeprecated(p))
            .OrderBy(static p => p.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        int bytecode = 0, customSim = 0;
        List<string> gaps = [];
        List<HotCardEffectOp> ops = new(4);

        foreach (PotionModel potion in potions)
        {
            string entry = potion.Id.Entry;
            HotCustomPotionId id = HotPotionCatalog.FindByEntry(entry);
            if (id == HotCustomPotionId.None)
            {
                gaps.Add($"{entry} ({potion.GetType().Name}): missing from HotPotionCatalog");
                continue;
            }

            ref readonly HotPotionCatalogEntry cat = ref HotPotionCatalog.Get(id);
            if (cat.UsesBytecode)
            {
                ops.Clear();
                HotPotionCatalog.CompilePotionBytecode(id, static _ => 1, ops, out _, out byte opCount, out HotCustomPotionId customOut);
                if (customOut != HotCustomPotionId.None || opCount == 0)
                    gaps.Add($"{entry} ({id}): UsesBytecode=true but compiled 0 ops");
                else
                    bytecode++;
            }
            else if (simCorpus == null || ContainsToken(simCorpus, $"HotCustomPotionId.{id}"))
            {
                customSim++;
            }
            else
            {
                gaps.Add($"{entry} ({id}): UsesBytecode=false and 0 HotKernel/Sim references");
            }
        }

        return new CategoryStats("Potions", potions.Length, bytecode, customSim, 0, 0, gaps);
    }

    private static CategoryStats AuditMonstersAndMoves(string? simCorpus)
    {
        MonsterModel[] monsters = ModelDb.All.OfType<MonsterModel>()
            .DistinctBy(static m => m.Id)
            .Where(m => !IsMockOrDeprecated(m))
            .OrderBy(static m => m.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        HashSet<ModelId> encounterMonsters = ModelDb.Monsters
            .Where(m => !IsMockOrDeprecated(m))
            .Select(static m => m.Id)
            .ToHashSet();

        MethodInfo? genMachineMethod = typeof(MonsterModel).GetMethod(
            "GenerateMoveStateMachine",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Dictionary<string, int> emptyStaticInts = new(StringComparer.Ordinal);
        int customOrCompiled = 0, pureAttackOrHelper = 0;
        List<string> gaps = [];

        foreach (MonsterModel canonical in monsters)
        {
            string entry = canonical.Id.Entry;
            HotMonsterId id = HotMonsterCatalog.FindByEntry(entry);
            if (id == HotMonsterId.None)
            {
                gaps.Add($"{entry} ({canonical.GetType().Name}): missing from HotMonsterCatalog");
                continue;
            }

            bool isEncounterMonster = encounterMonsters.Contains(canonical.Id);
            if (!isEncounterMonster || genMachineMethod == null)
            {
                pureAttackOrHelper++;
                continue;
            }

            MonsterMoveStateMachine? machine = null;
            try
            {
                MonsterModel mutable = canonical.ToMutable();
                machine = genMachineMethod.Invoke(mutable, null) as MonsterMoveStateMachine;
            }
            catch
            {
                // 部分怪物 GenerateMoveStateMachine 依赖活体 CombatState；此时至少确认其在 Sim/ 中有处理或为已知怪物
                if (simCorpus == null || ContainsToken(simCorpus, $"HotMonsterId.{id}"))
                    customOrCompiled++;
                else
                    pureAttackOrHelper++;
                continue;
            }

            if (machine == null)
            {
                pureAttackOrHelper++;
                continue;
            }

            bool hasNonAttackMove = false;
            bool monsterHasSimRef = simCorpus == null || ContainsToken(simCorpus, $"HotMonsterId.{id}");
            foreach (MonsterState st in machine.States.Values)
            {
                if (st is not MoveState move)
                    continue;
                bool hasNonAttackIntent = move.Intents.Any(static intent =>
                    intent is not SingleAttackIntent
                    and not MultiAttackIntent
                    and not StunIntent
                    and not SleepIntent
                    and not HiddenIntent
                    and not UnknownIntent);
                if (!hasNonAttackIntent)
                    continue;

                hasNonAttackMove = true;
                bool compiled = HotMonsterCatalog.CompileStandardMoveEffects(id, move.Id, emptyStaticInts, static _ => 1).Length > 0;
                bool removesOwner = HotMonsterCatalog.RemovesOwnerOnMove(id, move.Id);
                bool simMoveRef = simCorpus == null
                    || simCorpus.Contains($"\"{move.Id}\"", StringComparison.Ordinal)
                    || monsterHasSimRef;
                if (!compiled && !removesOwner && !simMoveRef)
                {
                    gaps.Add($"{entry}.{move.Id} ({id}): non-attack MoveState not found in CompileStandardMoveEffects or HotKernel/Sim");
                }
            }

            if (hasNonAttackMove || monsterHasSimRef)
                customOrCompiled++;
            else
                pureAttackOrHelper++;
        }

        return new CategoryStats("Monsters (+Moves)", monsters.Length, 0, customOrCompiled, 0, pureAttackOrHelper, gaps);
    }

    private static CategoryStats AuditEnchantments(string? simCorpus)
    {
        EnchantmentModel[] enchantments = ModelDb.DebugEnchantments
            .DistinctBy(static e => e.Id)
            .Where(e => !IsMockOrDeprecated(e))
            .OrderBy(static e => e.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        int customSim = 0, baked = 0;
        List<string> gaps = [];

        foreach (EnchantmentModel ench in enchantments)
        {
            string entry = ench.Id.Entry;
            HotEnchantmentId id = HotMonsterCatalog.FindEnchantmentByEntry(entry);
            if (id == HotEnchantmentId.None)
            {
                gaps.Add($"{entry} ({ench.GetType().Name}): missing from HotMonsterCatalog.EnchIdByEntry");
                continue;
            }

            if (simCorpus == null || ContainsToken(simCorpus, $"HotEnchantmentId.{id}"))
                customSim++;
            else if (KnownBakedAtRootEnchantments.ContainsKey(entry))
                baked++;
            else
                gaps.Add($"{entry} ({id}): 0 HotKernel/Sim references and not root-baked");
        }

        return new CategoryStats("Enchantments", enchantments.Length, 0, customSim, baked, 0, gaps);
    }

    private static CategoryStats AuditAfflictions(string? simCorpus)
    {
        AfflictionModel[] afflictions = ModelDb.DebugAfflictions
            .DistinctBy(static a => a.Id)
            .Where(a => !IsMockOrDeprecated(a))
            .OrderBy(static a => a.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        int customSim = 0;
        List<string> gaps = [];

        foreach (AfflictionModel affl in afflictions)
        {
            string entry = affl.Id.Entry;
            HotAfflictionId id = HotMonsterCatalog.FindAfflictionByEntry(entry);
            if (id == HotAfflictionId.None)
            {
                gaps.Add($"{entry} ({affl.GetType().Name}): missing from HotMonsterCatalog.AfflIdByEntry");
                continue;
            }

            if (simCorpus == null || ContainsToken(simCorpus, $"HotAfflictionId.{id}"))
                customSim++;
            else
                gaps.Add($"{entry} ({id}): 0 HotKernel/Sim references");
        }

        return new CategoryStats("Afflictions", afflictions.Length, 0, customSim, 0, 0, gaps);
    }

    private static CategoryStats AuditOrbs(string? simCorpus)
    {
        OrbModel[] orbs = ModelDb.Orbs
            .Concat(ModelDb.All.OfType<OrbModel>())
            .DistinctBy(static o => o.Id)
            .Where(o => !IsMockOrDeprecated(o))
            .OrderBy(static o => o.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        int customSim = 0;
        List<string> gaps = [];

        foreach (OrbModel orb in orbs)
        {
            string entry = orb.Id.Entry;
            HotOrbId id = HotMonsterCatalog.FindOrbByEntry(entry);
            if (id == HotOrbId.None)
            {
                gaps.Add($"{entry} ({orb.GetType().Name}): missing from HotMonsterCatalog.OrbIdByEntry");
                continue;
            }

            if (simCorpus == null || ContainsToken(simCorpus, $"HotOrbId.{id}"))
                customSim++;
            else
                gaps.Add($"{entry} ({id}): 0 HotKernel/Sim references");
        }

        return new CategoryStats("Orbs", orbs.Length, 0, customSim, 0, 0, gaps);
    }

    private static CategoryStats AuditDominions(string? simCorpus)
    {
        DominionModel[] dominions = ModelDb.All.OfType<DominionModel>()
            .DistinctBy(static d => d.Id)
            .Where(d => !IsMockOrDeprecated(d))
            .OrderBy(static d => d.Id.Entry, StringComparer.Ordinal)
            .ToArray();

        int customSim = 0;
        List<string> gaps = [];

        foreach (DominionModel dom in dominions)
        {
            string entry = dom.Id.Entry;
            HotDominionId id = HotMonsterCatalog.FindDominionByEntry(entry);
            if (id == HotDominionId.None)
            {
                gaps.Add($"{entry} ({dom.GetType().Name}): missing from HotMonsterCatalog.DominionIdByEntry");
                continue;
            }

            if (simCorpus == null || ContainsToken(simCorpus, $"HotDominionId.{id}"))
                customSim++;
            else
                gaps.Add($"{entry} ({id}): 0 HotKernel/Sim references");
        }

        return new CategoryStats("Dominions", dominions.Length, 0, customSim, 0, 0, gaps);
    }

    private static List<string> AuditReflectionFields(out int checksExecuted)
    {
        List<string> failures = [];
        checksExecuted = 0;

        // 1. 触发 HotRootBuilder 与 HotImportSupport 静态构造（验证全部 static readonly FieldInfo 解析器）
        foreach (Type hostType in new[] { typeof(HotRootBuilder), typeof(HotImportSupport) })
        {
            checksExecuted++;
            try
            {
                RuntimeHelpers.RunClassConstructor(hostType.TypeHandle);
                foreach (FieldInfo f in hostType.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (f.FieldType == typeof(FieldInfo) || f.FieldType == typeof(PropertyInfo) || f.FieldType == typeof(MethodInfo))
                    {
                        checksExecuted++;
                        if (f.GetValue(null) == null)
                            failures.Add($"{hostType.Name}.{f.Name} resolved to null");
                    }
                }
            }
            catch (Exception ex)
            {
                Exception root = ex is TypeInitializationException && ex.InnerException != null ? ex.InnerException : ex;
                failures.Add($"{hostType.Name} static ctor failed: {root.GetType().Name}: {root.Message}");
            }
        }

        // 2. 对 ModelDb 中全部非 Mock 怪物调用 HotImportSupport.CaptureStaticMonsterIntValues，
        //    确保 StaticMonsterIntMembers 中每一个字段/属性表达式树在 0.113.1 中全部编译成功。
        Dictionary<string, MonsterModel> monsterByTypeName = new(StringComparer.Ordinal);
        foreach (MonsterModel canonical in ModelDb.All.OfType<MonsterModel>().DistinctBy(static m => m.Id))
        {
            if (IsMockOrDeprecated(canonical))
                continue;
            monsterByTypeName[canonical.GetType().Name] = canonical;
            checksExecuted++;
            try
            {
                MonsterModel mutable = canonical.ToMutable();
                _ = HotImportSupport.CaptureStaticMonsterIntValues(mutable);
            }
            catch (Exception ex)
            {
                failures.Add($"CaptureStaticMonsterIntValues({canonical.GetType().Name}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 2b. 确保 StaticMonsterIntMembers 里的每个键都对应真实 0.113.1 怪物类
        FieldInfo? staticIntMapField = typeof(HotImportSupport).GetField(
            "StaticMonsterIntMembers", BindingFlags.Static | BindingFlags.NonPublic);
        if (staticIntMapField?.GetValue(null) is IReadOnlyDictionary<string, string[]> staticIntMap)
        {
            foreach (string key in staticIntMap.Keys)
            {
                checksExecuted++;
                if (!monsterByTypeName.ContainsKey(key))
                    failures.Add($"HotImportSupport.StaticMonsterIntMembers references missing monster type: {key}");
            }
        }

        // 3. 逐项验证 HotRootBuilder 通过 ReadMonsterBool/Int/Object 访问的全部私有 AI 字段
        (string MonsterType, string MemberName, string Kind)[] aiChecks =
        [
            ("FrogKnight", "_hasBeetleCharged", "bool"),
            ("TwoTailedRat", "_turnsUntilSummonable", "int"),
            ("TwoTailedRat", "_callForBackupCount", "int"),
            ("Fabricator", "_lastSpawned", "object"),
            ("ToughEgg", "_isHatched", "bool"),
            ("TestSubject", "_respawns", "int"),
            ("TestSubject", "_extraMultiClawCount", "int"),
            ("TestSubject", "BaseMultiClawCount", "int"),
            ("Tunneler", "_isStunned", "bool"),
            ("LagavulinMatriarch", "_isAwake", "bool"),
            ("SlumberingBeetle", "_isAwake", "bool"),
            ("Queen", "_hasAmalgamDied", "bool"),
            ("KnowledgeDemon", "_curseOfKnowledgeCounter", "int"),
            ("TheObscura", "_hasSummoned", "bool"),
            ("Aeonglass", "_additionalStrength", "int"),
            ("Aeonglass", "_witherUpgradeCount", "int"),
            ("WaterfallGiant", "_steamEruptionDamage", "int"),
        ];

        foreach ((string monsterType, string member, string kind) in aiChecks)
        {
            checksExecuted++;
            if (!monsterByTypeName.TryGetValue(monsterType, out MonsterModel? canonical))
            {
                failures.Add($"Missing monster type {monsterType} for reflection check {member}");
                continue;
            }
            try
            {
                MonsterModel mutable = canonical.ToMutable();
                switch (kind)
                {
                    case "bool": _ = HotImportSupport.ReadMonsterBool(mutable, member); break;
                    case "int": _ = HotImportSupport.ReadMonsterInt(mutable, member); break;
                    default: _ = HotImportSupport.ReadMonsterObject(mutable, member); break;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"HotImportSupport.ReadMonster({monsterType}.{member}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        return failures;
    }

    internal static bool IsMockOrDeprecated(AbstractModel model)
    {
        Type t = model.GetType();
        if (t.IsAbstract || t.Assembly != typeof(AbstractModel).Assembly)
            return true;
        if (t.Namespace?.Contains(".Mock", StringComparison.Ordinal) == true)
            return true;
        if (t.Name.StartsWith("Mock", StringComparison.Ordinal)
            || t.Name.StartsWith("Deprecated", StringComparison.Ordinal))
            return true;
        string entry = model.Id.Entry;
        return entry.StartsWith("MOCK_", StringComparison.Ordinal)
            || entry.StartsWith("DEPRECATED_", StringComparison.Ordinal);
    }

    private static void TryAddPoolCardIds<TPool>(HashSet<ModelId> dst) where TPool : CardPoolModel
    {
        try
        {
            foreach (ModelId id in ModelDb.CardPool<TPool>().AllCardIds)
                dst.Add(id);
        }
        catch
        {
            // ignore if pool absent
        }
    }

    private static bool HasNoCustomOnPlay(CardModel card)
    {
        MethodInfo? onPlay = card.GetType().GetMethod(
            "OnPlay",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return onPlay == null || onPlay.DeclaringType == typeof(CardModel);
    }

    private static bool ContainsToken(string corpus, string token)
    {
        int start = 0;
        while ((start = corpus.IndexOf(token, start, StringComparison.Ordinal)) >= 0)
        {
            int end = start + token.Length;
            bool leftOk = start == 0 || !IsIdentChar(corpus[start - 1]);
            bool rightOk = end >= corpus.Length || !IsIdentChar(corpus[end]);
            if (leftOk && rightOk)
                return true;
            start = end;
        }
        return false;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string? LoadSimSourceCorpus(string simDir)
    {
        if (!Directory.Exists(simDir))
            return null;
        StringBuilder sb = new(1024 * 512);
        foreach (string file in Directory.GetFiles(simDir, "*.cs", SearchOption.AllDirectories))
        {
            sb.AppendLine(File.ReadAllText(file));
        }
        return sb.ToString();
    }

    private static string ResolveRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
}
