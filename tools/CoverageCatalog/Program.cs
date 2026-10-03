using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using CombatSolver.Engine.Common.Mirrors;
using STS2RitsuLib.Utils.HarmonyIl;

string repositoryRoot = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal))
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
bool verify = args.Contains("--verify", StringComparer.Ordinal);
bool verifyEffective = args.Contains("--verify-effective", StringComparer.Ordinal);
bool verifyNoRescan = args.Contains("--verify-no-rescan", StringComparer.Ordinal);
bool verifyRuntimeEvidence = args.Contains("--verify-runtime-evidence", StringComparer.Ordinal);
bool verifyBranchStateReads = args.Contains("--verify-branch-state-reads", StringComparer.Ordinal);
bool verifyStateFields = args.Contains("--verify-state-fields", StringComparer.Ordinal);
bool verifyStateWrites = args.Contains("--verify-state-writes", StringComparer.Ordinal);
bool verifyPrePlayChoices = args.Contains("--verify-pre-play-choices", StringComparer.Ordinal);
bool verifyCombatChoices = args.Contains("--verify-combat-choices", StringComparer.Ordinal);
bool verifyAutoPlaySources = args.Contains("--verify-autoplay-sources", StringComparer.Ordinal);
bool verifyRosterSources = args.Contains("--verify-roster-sources", StringComparer.Ordinal);
bool generateSimpleCardFixture = args.Contains("--generate-simple-card-fixture", StringComparer.Ordinal);
bool generateExactCardFixture = args.Contains("--generate-exact-card-fixture", StringComparer.Ordinal);
bool generateSimpleMonsterMoveFixture = args.Contains("--generate-simple-monster-move-fixture", StringComparer.Ordinal);
bool generateStateMutationFixtures = args.Contains("--generate-state-mutation-fixtures", StringComparer.Ordinal);
string coverageDirectory = Path.Combine(repositoryRoot, "coverage");
string overridePath = Path.Combine(coverageDirectory, "classifications.json");
string evidencePath = Path.Combine(coverageDirectory, "test-evidence.json");
string catalogPath = Path.Combine(coverageDirectory, "combat-hooks.json");
string boundaryPath = Path.Combine(coverageDirectory, "search-boundaries.json");
string runtimeGapPath = Path.Combine(coverageDirectory, "runtime-evidence-gaps.json");
string branchStateReadRiskPath = Path.Combine(coverageDirectory, "branch-state-read-risks.json");
string stateFieldPath = Path.Combine(coverageDirectory, "state-fields.json");
string stateMutationPath = Path.Combine(coverageDirectory, "state-mutations.json");
string prePlayChoiceGapPath = Path.Combine(coverageDirectory, "pre-play-choice-gaps.json");
string combatChoiceSourcePath = Path.Combine(coverageDirectory, "combat-choice-sources.json");
string autoPlaySourcePath = Path.Combine(coverageDirectory, "combat-autoplay-sources.json");
string rosterSourcePath = Path.Combine(coverageDirectory, "combat-roster-sources.json");
string reportPath = Path.Combine(repositoryRoot, "docs", "COMBAT_HOOK_COVERAGE.md");
string manifestPath = Path.Combine(repositoryRoot, "CombatSolver.json");

Directory.CreateDirectory(coverageDirectory);
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

Dictionary<string, CoverageClassification> classifications = File.Exists(overridePath)
    ? JsonSerializer.Deserialize<Dictionary<string, CoverageClassification>>(
        File.ReadAllText(overridePath), JsonOptions()) ?? []
    : [];
Dictionary<string, CoverageTestEvidence> testEvidence = File.Exists(evidencePath)
    ? JsonSerializer.Deserialize<Dictionary<string, CoverageTestEvidence>>(
        File.ReadAllText(evidencePath), JsonOptions()) ?? []
    : [];
using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
string combatSolverVersion = manifest.RootElement.GetProperty("version").GetString()
    ?? throw new InvalidOperationException("CombatSolver.json version is null.");

Type[] gameModelTypes = typeof(AbstractModel).Assembly.GetTypes()
    .Where(type => !type.IsAbstract && typeof(AbstractModel).IsAssignableFrom(type))
    .ToArray();
ModelDb.Init(gameModelTypes);
EngineMirrorInventory engine = ReadEngineMirrorInventory();
HashSet<string> compensatedCardTypes = ReadCompensatedCardTypes();
Dictionary<string, string> machineCardEvidence = ReadMachineCardEvidence();
foreach ((string type, string testId) in ReadFixtureCardEvidence(repositoryRoot, testEvidence))
    machineCardEvidence.TryAdd(type, testId);
Dictionary<string, string> machineModelHookEvidence = ReadFixtureModelHookEvidence<CardModel>(
    repositoryRoot,
    testEvidence,
    "coveredCardHooks",
    "cardId");
foreach ((string key, string testId) in ReadFixtureModelHookEvidence<PotionModel>(
             repositoryRoot,
             testEvidence,
             "coveredPotionHooks",
             "potionId"))
{
    machineModelHookEvidence.TryAdd(key, testId);
}
foreach ((string key, string testId) in ReadFixtureModelHookEvidence<OrbModel>(
             repositoryRoot,
             testEvidence,
             "coveredOrbHooks",
             "orbId"))
{
    machineModelHookEvidence.TryAdd(key, testId);
}
foreach ((string key, string testId) in ReadFixtureModelHookEvidence<EnchantmentModel>(
             repositoryRoot,
             testEvidence,
             "coveredEnchantmentHooks",
             "enchantmentId"))
{
    machineModelHookEvidence.TryAdd(key, testId);
}
foreach ((string key, string testId) in ReadFixtureModelHookEvidence<MonsterModel>(
             repositoryRoot,
             testEvidence,
             "coveredMonsterHooks",
             "monsterId"))
{
    machineModelHookEvidence.TryAdd(key, testId);
}
foreach ((string key, string testId) in ReadFixtureModelHookEvidence<PowerModel>(
             repositoryRoot,
             testEvidence,
             "coveredPowerHooks",
             "powerId"))
{
    machineModelHookEvidence.TryAdd(key, testId);
}
foreach ((string key, string testId) in ReadFixtureModelHookEvidence<RelicModel>(
             repositoryRoot,
             testEvidence,
             "coveredRelicHooks",
             "relicId"))
{
    machineModelHookEvidence.TryAdd(key, testId);
}
Dictionary<string, string> machineMonsterMoveEvidence = ReadFixtureMonsterMoveEvidence(
    repositoryRoot,
    testEvidence);
HashSet<string> calculatedCardTypes = ReadCalculatedCardTypes();
string[] discoveredCalculatedCardTypes = typeof(CardModel).Assembly.GetTypes()
    .Where(type => !type.IsAbstract && typeof(CardModel).IsAssignableFrom(type))
    .Select(type => (Type: type, Card: CanonicalModel(type) as CardModel))
    .Where(pair => pair.Card?.DynamicVars.Values.Any(value => value is CalculatedVar) == true)
    .Select(pair => pair.Type.FullName!)
    .Order(StringComparer.Ordinal)
    .ToArray();
string[] missingCalculatedCardTypes = discoveredCalculatedCardTypes
    .Except(calculatedCardTypes, StringComparer.Ordinal)
    .Order(StringComparer.Ordinal)
    .ToArray();
string[] persistentRelicStateGaps = AuditPersistentRelicPredictionStates();
List<CoverageEntry> entries = DiscoverGameHooks(engine, classifications);
entries.AddRange(DiscoverMonsterMoves(classifications));
entries = entries.Select(entry => EnrichEffectiveSupport(
    entry,
    testEvidence,
    compensatedCardTypes,
    machineCardEvidence,
    machineModelHookEvidence,
    machineMonsterMoveEvidence)).ToList();
entries.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
BranchStateReadRisk[] branchStateReadRisks = AuditBranchSensitiveOriginalFallbacks(entries);
StateFieldCatalog stateFields = AuditStateFields(combatSolverVersion);
StateMutationCatalog stateMutations = AuditStateMutations(entries, combatSolverVersion);
CombatChoiceSourceCatalog combatChoiceSources = AuditCombatChoiceSources(combatSolverVersion);
AutoPlaySourceCatalog autoPlaySources = AuditAutoPlaySources(combatSolverVersion);
RosterSourceCatalog rosterSources = AuditRosterSources(combatSolverVersion);
string[] unknownClassificationKeys = classifications.Keys
    .Except(entries.Select(static entry => entry.Key), StringComparer.Ordinal)
    .Order(StringComparer.Ordinal)
    .ToArray();
if (unknownClassificationKeys.Length > 0)
    throw new InvalidOperationException(
        $"Coverage classifications reference unknown keys:{Environment.NewLine}{string.Join(Environment.NewLine, unknownClassificationKeys)}");
string[] unknownTestIds = classifications.Values
    .Select(classification => classification.TestId)
    .Where(static testId => testId != null)
    .Cast<string>()
    .Distinct(StringComparer.Ordinal)
    .Except(testEvidence.Keys, StringComparer.Ordinal)
    .Order(StringComparer.Ordinal)
    .ToArray();
if (unknownTestIds.Length > 0)
    throw new InvalidOperationException(
        $"Coverage classifications reference unknown test evidence:{Environment.NewLine}{string.Join(Environment.NewLine, unknownTestIds)}");
string[] missingTestIds = classifications
    .Where(static pair => string.IsNullOrWhiteSpace(pair.Value.TestId))
    .Select(static pair => pair.Key)
    .Order(StringComparer.Ordinal)
    .ToArray();
string[] nonPassingTestIds = classifications.Values
    .Select(static classification => classification.TestId)
    .Where(static testId => testId != null)
    .Cast<string>()
    .Distinct(StringComparer.Ordinal)
    .Where(testId => testEvidence[testId].Status is VerificationStatus.Pending or VerificationStatus.Failed)
    .Order(StringComparer.Ordinal)
    .ToArray();
CoverageCatalog catalog = new(
    SchemaVersion: 3,
    CombatSolverVersion: combatSolverVersion,
    GameVersion: "0.111.0",
    SimulationEngine: "embedded",
    Entries: entries);

File.WriteAllText(catalogPath, JsonSerializer.Serialize(catalog, JsonOptions()), new UTF8Encoding(false));
SearchBoundaryCatalog boundaries = new(
    SchemaVersion: 1,
    CombatSolverVersion: combatSolverVersion,
    GameVersion: "0.111.0",
    NativeAutoRescan: entries
        .Where(entry => entry.EffectiveStatus == EffectiveSupportStatus.NativeAutoRescan)
        .Select(entry => new SearchBoundaryEntry(
            entry.Key,
            entry.Category,
            entry.EntityName,
            entry.Hook,
            entry.Source,
            entry.Notes))
        .ToArray());
File.WriteAllText(boundaryPath, JsonSerializer.Serialize(boundaries, JsonOptions()), new UTF8Encoding(false));
File.WriteAllText(
    reportPath,
    BuildReport(
        catalog,
        discoveredCalculatedCardTypes.Length,
        missingCalculatedCardTypes,
        persistentRelicStateGaps),
    new UTF8Encoding(false));

int unclassified = entries.Count(entry => entry.Status == CoverageStatus.Unanalyzed);
int pendingImplementation = entries.Count(entry => entry.Status == CoverageStatus.PendingImplementation);
CoverageEntry[] activeWithoutRuntimeEvidence = entries.Where(entry =>
        entry.EffectiveStatus == EffectiveSupportStatus.Exact
        && entry.Verification != VerificationKind.Runtime
        && entry.Capability is "EngineMirror" or "SolverCompensation")
    .ToArray();
RuntimeEvidenceGapCatalog runtimeGaps = new(
    SchemaVersion: 1,
    CombatSolverVersion: combatSolverVersion,
    GameVersion: "0.111.0",
    Entries: activeWithoutRuntimeEvidence.Select(ToRuntimeEvidenceGap).ToArray());
File.WriteAllText(runtimeGapPath, JsonSerializer.Serialize(runtimeGaps, JsonOptions()), new UTF8Encoding(false));
BranchStateReadRiskCatalog branchStateReadRiskCatalog = new(
    SchemaVersion: 1,
    CombatSolverVersion: combatSolverVersion,
    GameVersion: "0.111.0",
    Entries: branchStateReadRisks);
File.WriteAllText(
    branchStateReadRiskPath,
    JsonSerializer.Serialize(branchStateReadRiskCatalog, JsonOptions()),
    new UTF8Encoding(false));
File.WriteAllText(
    stateFieldPath,
    JsonSerializer.Serialize(stateFields, JsonOptions()),
    new UTF8Encoding(false));
File.WriteAllText(
    stateMutationPath,
    JsonSerializer.Serialize(stateMutations, JsonOptions()),
    new UTF8Encoding(false));
File.WriteAllText(
    combatChoiceSourcePath,
    JsonSerializer.Serialize(combatChoiceSources, JsonOptions()),
    new UTF8Encoding(false));
File.WriteAllText(
    autoPlaySourcePath,
    JsonSerializer.Serialize(autoPlaySources, JsonOptions()),
    new UTF8Encoding(false));
File.WriteAllText(
    rosterSourcePath,
    JsonSerializer.Serialize(rosterSources, JsonOptions()),
    new UTF8Encoding(false));
if (generateSimpleCardFixture)
{
    string fixturePath = Path.Combine(
        coverageDirectory,
        "unattended",
        "generated-inferred-card-on-play-audit.json");
    Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
    IReadOnlyList<SimpleCardAuditCheck> checks = BuildSimpleCardAuditChecks(activeWithoutRuntimeEvidence);
    File.WriteAllText(
        fixturePath,
        JsonSerializer.Serialize(checks, JsonOptions()),
        new UTF8Encoding(false));
    Console.WriteLine(fixturePath);
    const int chunkSize = 15;
    for (int offset = 0, part = 1; offset < checks.Count; offset += chunkSize, part++)
    {
        string partPath = Path.Combine(
            coverageDirectory,
            "unattended",
            $"generated-inferred-card-on-play-audit-part-{part}.json");
        File.WriteAllText(
            partPath,
            JsonSerializer.Serialize(checks.Skip(offset).Take(chunkSize).ToArray(), JsonOptions()),
            new UTF8Encoding(false));
        Console.WriteLine(partPath);
    }
}
if (generateExactCardFixture)
{
    string fixturePath = Path.Combine(
        coverageDirectory,
        "unattended",
        "generated-exact-card-on-play-audit.json");
    Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
    IReadOnlyList<SimpleCardAuditCheck> checks = BuildExactCardAuditChecks(activeWithoutRuntimeEvidence);
    File.WriteAllText(
        fixturePath,
        JsonSerializer.Serialize(checks, JsonOptions()),
        new UTF8Encoding(false));
    Console.WriteLine(fixturePath);
    const int chunkSize = 12;
    for (int offset = 0, part = 1; offset < checks.Count; offset += chunkSize, part++)
    {
        string partPath = Path.Combine(
            coverageDirectory,
            "unattended",
            $"generated-exact-card-on-play-audit-part-{part}.json");
        File.WriteAllText(
            partPath,
            JsonSerializer.Serialize(checks.Skip(offset).Take(chunkSize).ToArray(), JsonOptions()),
            new UTF8Encoding(false));
        Console.WriteLine(partPath);
    }
}
if (generateSimpleMonsterMoveFixture)
{
    string fixturePath = Path.Combine(
        coverageDirectory,
        "unattended",
        "generated-solver-monster-move-audit.json");
    Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
    IReadOnlyList<SimpleMonsterMoveAuditCheck> checks = BuildSimpleMonsterMoveAuditChecks(
        activeWithoutRuntimeEvidence);
    File.WriteAllText(
        fixturePath,
        JsonSerializer.Serialize(checks, JsonOptions()),
        new UTF8Encoding(false));
    Console.WriteLine(fixturePath);
    const int chunkSize = 12;
    for (int offset = 0, part = 1; offset < checks.Count; offset += chunkSize, part++)
    {
        string partPath = Path.Combine(
            coverageDirectory,
            "unattended",
            $"generated-solver-monster-move-audit-part-{part}.json");
        File.WriteAllText(
            partPath,
            JsonSerializer.Serialize(checks.Skip(offset).Take(chunkSize).ToArray(), JsonOptions()),
            new UTF8Encoding(false));
        Console.WriteLine(partPath);
    }
}
if (generateStateMutationFixtures)
{
    IReadOnlyList<StateMutationCardCheck> checks = BuildCardUpgradeMutationChecks(stateMutations);
    string fixtureDirectory = Path.Combine(coverageDirectory, "unattended");
    Directory.CreateDirectory(fixtureDirectory);
    string fullPath = Path.Combine(fixtureDirectory, "generated-card-upgrade-state-audit.json");
    File.WriteAllText(
        fullPath,
        JsonSerializer.Serialize(checks, JsonOptions()),
        new UTF8Encoding(false));
    Console.WriteLine(fullPath);
    const int chunkSize = 12;
    for (int offset = 0, part = 1; offset < checks.Count; offset += chunkSize, part++)
    {
        string partPath = Path.Combine(
            fixtureDirectory,
            $"generated-card-upgrade-state-audit-part-{part}.json");
        File.WriteAllText(
            partPath,
            JsonSerializer.Serialize(checks.Skip(offset).Take(chunkSize).ToArray(), JsonOptions()),
            new UTF8Encoding(false));
        Console.WriteLine(partPath);
    }
}
Console.WriteLine(
    $"Combat hook catalog: {entries.Count} entries, {unclassified} unanalyzed, {pendingImplementation} pending implementation, " +
    $"{missingTestIds.Length} manual classifications without evidence, {nonPassingTestIds.Length} referenced non-passing tests, " +
    $"{missingCalculatedCardTypes.Length} calculated cards without branch-local specs, " +
    $"{persistentRelicStateGaps.Length} persistent relic state gaps, " +
    $"{activeWithoutRuntimeEvidence.Length} active exact hooks without runtime evidence, " +
    $"{branchStateReadRisks.Length} exact original fallbacks reading live branch state, " +
    $"{stateFields.UnclassifiedCount} unclassified state fields, " +
    $"{stateMutations.UnverifiedCount} required state-writing hooks without runtime evidence, " +
    $"{stateMutations.SnapshotOnlyWithoutRuntimeEvidenceCount} snapshot state-writing hooks outside the replay horizon, " +
    $"{stateMutations.StaticConfigurationWithoutRuntimeEvidenceCount} static move-graph builders.");
Console.WriteLine(catalogPath);
Console.WriteLine($"Search boundaries: {boundaries.NativeAutoRescan.Count} native auto-rescan entries.");
Console.WriteLine(boundaryPath);
Console.WriteLine(runtimeGapPath);
Console.WriteLine(branchStateReadRiskPath);
Console.WriteLine(stateFieldPath);
Console.WriteLine(stateMutationPath);
int unresolvedPrePlayChoices = 0;
if (File.Exists(prePlayChoiceGapPath))
{
    using JsonDocument prePlayChoiceGaps = JsonDocument.Parse(File.ReadAllText(prePlayChoiceGapPath));
    if (prePlayChoiceGaps.RootElement.TryGetProperty("unresolved", out JsonElement unresolved))
        unresolvedPrePlayChoices = unresolved.GetArrayLength();
}
Console.WriteLine($"Pre-play choices: {unresolvedPrePlayChoices} unresolved player interventions.");
Console.WriteLine(prePlayChoiceGapPath);
Console.WriteLine(
    $"Combat card selections: {combatChoiceSources.Entries.Count} call sites, " +
    $"{combatChoiceSources.UnresolvedCount} unresolved in-scope sources.");
Console.WriteLine(combatChoiceSourcePath);
Console.WriteLine(
    $"Combat auto-play sources: {autoPlaySources.Entries.Count} call sites, " +
    $"{autoPlaySources.UnresolvedCount} unresolved in-scope sources.");
Console.WriteLine(autoPlaySourcePath);
Console.WriteLine(
    $"Combat roster sources: {rosterSources.Entries.Count} call sites, " +
    $"{rosterSources.UnresolvedCount} unresolved in-scope sources.");
Console.WriteLine(rosterSourcePath);
if (verify && (unclassified > 0 || pendingImplementation > 0 || missingTestIds.Length > 0 || nonPassingTestIds.Length > 0))
{
    Console.Error.WriteLine(
        "Coverage verification failed: every hook must be classified, deterministic semantics cannot remain pending, " +
        "and every manual classification must reference passing evidence.");
    return 2;
}
int effectiveGaps = entries.Count(entry =>
    entry.EffectiveStatus is EffectiveSupportStatus.NeedsReview or EffectiveSupportStatus.Unsupported);
if (verifyEffective && (effectiveGaps > 0
                        || missingCalculatedCardTypes.Length > 0
                        || persistentRelicStateGaps.Length > 0
                        || branchStateReadRisks.Length > 0
                        || stateFields.UnclassifiedCount > 0))
{
    Console.Error.WriteLine(
        $"Effective-support verification failed: {effectiveGaps} deterministic hooks and " +
        $"{missingCalculatedCardTypes.Length} calculated cards plus " +
        $"{persistentRelicStateGaps.Length} persistent relic states plus " +
        $"{branchStateReadRisks.Length} live branch-state reads plus " +
        $"{stateFields.UnclassifiedCount} state fields still require review or implementation.");
    return 3;
}
if (verifyNoRescan && boundaries.NativeAutoRescan.Count > 0)
{
    Console.Error.WriteLine(
        $"No-rescan verification failed: {boundaries.NativeAutoRescan.Count} in-scope hooks still require native resolution.");
    return 4;
}
if (verifyRuntimeEvidence && activeWithoutRuntimeEvidence.Length > 0)
{
    Console.Error.WriteLine(
        $"Runtime-evidence verification failed: {activeWithoutRuntimeEvidence.Length} active exact hooks " +
        "still rely on static classification, inferred behavior, or unverified mirror registration.");
    foreach (CoverageEntry entry in activeWithoutRuntimeEvidence.Take(50))
        Console.Error.WriteLine(entry.Key);
    if (activeWithoutRuntimeEvidence.Length > 50)
        Console.Error.WriteLine($"... and {activeWithoutRuntimeEvidence.Length - 50} more; see combat-hooks.json.");
    return 5;
}
if (verifyBranchStateReads && branchStateReadRisks.Length > 0)
{
    Console.Error.WriteLine(
        $"Branch-state-read verification failed: {branchStateReadRisks.Length} exact original-hook fallbacks " +
        "still read live combat state.");
    foreach (BranchStateReadRisk risk in branchStateReadRisks.Take(50))
        Console.Error.WriteLine($"{risk.Key}: {string.Join(", ", risk.Reads)}");
    if (branchStateReadRisks.Length > 50)
        Console.Error.WriteLine($"... and {branchStateReadRisks.Length - 50} more; see branch-state-read-risks.json.");
    return 6;
}
if (verifyStateFields && stateFields.UnclassifiedCount > 0)
{
    Console.Error.WriteLine(
        $"State-field verification failed: {stateFields.UnclassifiedCount} dynamic state fields are not classified.");
    foreach (StateFieldEntry entry in stateFields.Entries.Where(entry => entry.Role == "Unclassified").Take(50))
        Console.Error.WriteLine($"{entry.EntityType}.{entry.FieldName}: {entry.Notes}");
    if (stateFields.UnclassifiedCount > 50)
        Console.Error.WriteLine($"... and {stateFields.UnclassifiedCount - 50} more; see state-fields.json.");
    return 7;
}
if (verifyStateWrites && stateMutations.UnverifiedCount > 0)
{
    Console.Error.WriteLine(
        $"State-write verification failed: {stateMutations.UnverifiedCount} combat hooks mutate state without runtime evidence.");
    foreach (StateMutationEntry entry in stateMutations.Entries
                 .Where(entry => entry.RequiresRuntimeEvidence && !entry.RuntimeVerified)
                 .Take(50))
        Console.Error.WriteLine($"{entry.Key}: {string.Join(", ", entry.Writes)}");
    if (stateMutations.UnverifiedCount > 50)
        Console.Error.WriteLine($"... and {stateMutations.UnverifiedCount - 50} more; see state-mutations.json.");
    return 8;
}
if (verifyPrePlayChoices && unresolvedPrePlayChoices > 0)
{
    Console.Error.WriteLine(
        $"Pre-play choice verification failed: {unresolvedPrePlayChoices} first-turn choices still require player intervention.");
    return 9;
}
if ((verifyCombatChoices || verify) && combatChoiceSources.UnresolvedCount > 0)
{
    Console.Error.WriteLine(
        $"Combat-choice verification failed: {combatChoiceSources.UnresolvedCount} CardSelectCmd call sites " +
        "are in single-player combat scope without an explicit solver/deployment classification.");
    foreach (CombatChoiceSourceEntry entry in combatChoiceSources.Entries
                 .Where(entry => entry.Classification == "Unresolved")
                 .Take(50))
    {
        Console.Error.WriteLine(entry.Key);
    }
    return 10;
}
if ((verifyAutoPlaySources || verify) && autoPlaySources.UnresolvedCount > 0)
{
    Console.Error.WriteLine(
        $"Auto-play verification failed: {autoPlaySources.UnresolvedCount} CardCmd/CardPileCmd auto-play call sites " +
        "are in single-player combat scope without an explicit simulation classification.");
    foreach (AutoPlaySourceEntry entry in autoPlaySources.Entries
                 .Where(entry => entry.Classification == "Unresolved")
                 .Take(50))
    {
        Console.Error.WriteLine(entry.Key);
    }
    return 11;
}
if ((verifyRosterSources || verify) && rosterSources.UnresolvedCount > 0)
{
    Console.Error.WriteLine(
        $"Roster-source verification failed: {rosterSources.UnresolvedCount} creature/pet summon or escape call sites " +
        "lack an explicit simulation or snapshot classification.");
    foreach (RosterSourceEntry entry in rosterSources.Entries
                 .Where(entry => entry.Classification == "Unresolved")
                 .Take(50))
    {
        Console.Error.WriteLine(entry.Key);
    }
    return 12;
}

static RosterSourceCatalog AuditRosterSources(string combatSolverVersion)
{
    HashSet<string> supported =
    [
        "Afterlife", "Bodyguard", "Cleanse", "Dirge", "NecroMastery", "PullAggro", "Reanimate", "Spur",
        "BoneBrew", "BattlewornDummyTimeLimitPower", "DevourLifePower", "InfestedPower", "SicEmPower",
        "StockPower", "SummonNextTurnPower", "SurprisePower", "BoundPhylactery", "Byrdpip",
        "PaelsLegion", "PhylacteryUnbound", "Fabricator", "FatGremlin", "Fogmog", "LivingFog",
        "Ovicopter", "TheObscura", "ThievingHopper", "TwoTailedRat",
    ];
    Assembly game = typeof(AbstractModel).Assembly;
    List<RosterSourceEntry> entries = [];
    foreach (Type type in game.GetTypes()
                 .Where(type => !type.IsAbstract
                     && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                     && !type.Name.StartsWith("<", StringComparison.Ordinal)
                     && type.Namespace != null
                     && type.Namespace.StartsWith("MegaCrit.Sts2.Core.Models.", StringComparison.Ordinal)))
    {
        string modelNamespace = type.Namespace!;
        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            bool changesRoster;
            try
            {
                changesRoster = GetReachableImplementationMethods(method, type)
                    .SelectMany(implementation => implementation.GetOriginalIl().Instructions)
                    .Any(instruction => instruction.operand is MethodBase called
                        && IsRosterMutationCall(called));
            }
            catch
            {
                continue;
            }
            if (!changesRoster)
                continue;

            string classification;
            string evidence;
            if (modelNamespace.Contains(".Mocks", StringComparison.Ordinal))
            {
                classification = "Mock";
                evidence = "Test-only model namespace";
            }
            else if (type.Name == "LegionOfBone")
            {
                classification = "Multiplayer";
                evidence = "LegionOfBone is MultiplayerOnly";
            }
            else if (supported.Contains(type.Name))
            {
                classification = "Supported";
                evidence = "MonsterSpawnSupport, DeathPowerSupport, Osty lifecycle, escape semantics, or initial snapshot";
            }
            else
            {
                classification = "Unresolved";
                evidence = "No matching roster mutation simulation";
            }
            entries.Add(new RosterSourceEntry(
                $"{type.FullName}.{Signature(method)}",
                modelNamespace.Split('.').Last(),
                type.FullName!,
                Signature(method),
                classification,
                evidence));
        }
    }
    entries.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
    return new RosterSourceCatalog(
        1,
        combatSolverVersion,
        "0.111.0",
        entries.Count(entry => entry.Classification == "Unresolved"),
        entries);
}

static bool IsRosterMutationCall(MethodBase called)
{
    string type = called.DeclaringType?.FullName ?? string.Empty;
    return type switch
    {
        "MegaCrit.Sts2.Core.Commands.CreatureCmd" => called.Name is "Add" or "Escape",
        "MegaCrit.Sts2.Core.Commands.PlayerCmd" => called.Name == "AddPet",
        "MegaCrit.Sts2.Core.Commands.OstyCmd" => called.Name == "Summon",
        _ => false,
    };
}

static AutoPlaySourceCatalog AuditAutoPlaySources(string combatSolverVersion)
{
    HashSet<string> supported =
    [
        "BeatDown", "Bombardment", "Cascade", "Catastrophe", "DecisionsDecisions", "Eidolon", "Havoc",
        "HowlFromBeyond", "IAmInvincible", "KnifeTrap", "Uproar", "Imbued", "DistilledChaos",
        "HellraiserPower", "MayhemPower", "StampedePower", "HistoryCourse", "WhisperingEarring",
    ];
    Assembly game = typeof(AbstractModel).Assembly;
    List<AutoPlaySourceEntry> entries = [];
    foreach (Type type in game.GetTypes()
                 .Where(type => !type.IsAbstract
                     && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                     && !type.Name.StartsWith("<", StringComparison.Ordinal)
                     && type.Namespace != null
                     && type.Namespace.StartsWith("MegaCrit.Sts2.Core.Models.", StringComparison.Ordinal)))
    {
        string modelNamespace = type.Namespace!;
        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            bool callsAutoPlay;
            try
            {
                callsAutoPlay = GetReachableImplementationMethods(method, type)
                    .SelectMany(implementation => implementation.GetOriginalIl().Instructions)
                    .Any(instruction => instruction.operand is MethodBase called
                        && (called.DeclaringType?.FullName is
                            "MegaCrit.Sts2.Core.Commands.CardCmd" or
                            "MegaCrit.Sts2.Core.Commands.CardPileCmd")
                        && (called.Name is "AutoPlay" or "AutoPlayFromDrawPile"));
            }
            catch
            {
                continue;
            }
            if (!callsAutoPlay)
                continue;

            string classification;
            string evidence;
            if (modelNamespace.Contains(".Mocks", StringComparison.Ordinal))
            {
                classification = "Mock";
                evidence = "Test-only model namespace";
            }
            else if (type.Name == "ImitationLearningPower")
            {
                classification = "Multiplayer";
                evidence = "ImitationLearningPower is produced only by a MultiplayerOnly card";
            }
            else if (supported.Contains(type.Name))
            {
                classification = "Supported";
                evidence = "Prediction AutoPlay lifecycle with nested-choice resolution";
            }
            else
            {
                classification = "Unresolved";
                evidence = "No matching auto-play simulation path";
            }

            entries.Add(new AutoPlaySourceEntry(
                $"{type.FullName}.{Signature(method)}",
                modelNamespace.Split('.').Last(),
                type.FullName!,
                Signature(method),
                classification,
                evidence));
        }
    }
    entries.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
    return new AutoPlaySourceCatalog(
        1,
        combatSolverVersion,
        "0.111.0",
        entries.Count(entry => entry.Classification == "Unresolved"),
        entries);
}

static CombatChoiceSourceCatalog AuditCombatChoiceSources(string combatSolverVersion)
{
    HashSet<string> supportedCards =
    [
        "Abundance", "Acrobatics", "Armaments", "Begone", "Brand", "BurningPact", "Charge",
        "Cleanse", "CosmicIndifference", "DaggerThrow", "DecisionsDecisions", "Discovery", "Dredge",
        "DualWield", "Glimmer", "Graveblast", "Guards", "HandTrick", "Headbutt", "HeirloomHammer",
        "HiddenDaggers", "Hologram", "NeowsFury", "Nightmare", "PhotonCut", "Prepared", "Purity",
        "Quasar", "Scavenge", "SculptingStrike", "Seance", "SecretTechnique", "SecretWeapon",
        "SeekerStrike", "Snap", "Splash", "Survivor", "ThinkingAhead", "Transfigure", "TrueGrit",
        "Wish",
    ];
    HashSet<string> supportedPotions =
    [
        "Ashwater", "AttackPotion", "ColorlessPotion", "DropletOfPrecognition", "GamblersBrew",
        "LiquidMemories", "PowerPotion", "SkillPotion", "TouchOfInsanity",
    ];
    HashSet<string> supportedPowers =
    [
        "EntropyPower", "ForegoneConclusionPower", "StratagemPower", "ToolsOfTheTradePower", "TyrannyPower",
    ];
    HashSet<string> supportedRelics =
    [
        "ChoicesParadox", "GamblingChip", "ToastyMittens", "Toolbox", "WhisperingEarring",
    ];
    HashSet<string> nativeCombatChoiceMethods =
    [
        "FromChooseACardScreen", "FromSimpleGridForRewards", "FromSimpleGrid", "FromCombatPile",
        "FromHand", "FromHandForDiscard", "FromHandForUpgrade", "PushSelector",
    ];

    Assembly game = typeof(AbstractModel).Assembly;
    List<CombatChoiceSourceEntry> entries = [];
    foreach (Type type in game.GetTypes()
                 .Where(type => !type.IsAbstract
                     && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                     && !type.Name.StartsWith("<", StringComparison.Ordinal)
                     && type.Namespace != null
                     && type.Namespace.StartsWith("MegaCrit.Sts2.Core.Models.", StringComparison.Ordinal)
                     && type.Namespace.Split('.').Last() is "Cards" or "Potions" or "Powers" or "Relics" or "Enchantments"))
    {
        string modelNamespace = type.Namespace!;
        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            IReadOnlyList<string> selectionMethods;
            try
            {
                selectionMethods = GetReachableImplementationMethods(method, type)
                    .SelectMany(implementation => implementation.GetOriginalIl().Instructions)
                    .Select(instruction => instruction.operand as MethodBase)
                    .Where(called => called?.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Commands.CardSelectCmd")
                    .Select(called => called!.Name)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
            }
            catch
            {
                continue;
            }
            if (selectionMethods.Count == 0)
                continue;

            string category = modelNamespace.Split('.').Last();
            string classification;
            string evidence;
            bool hasNativeSurfaceGap = selectionMethods.Any(name => !nativeCombatChoiceMethods.Contains(name));
            if (modelNamespace.Contains(".Mocks", StringComparison.Ordinal))
            {
                classification = "Mock";
                evidence = "Test-only model namespace";
            }
            else if (type.Name == "Tutor")
            {
                classification = "Multiplayer";
                evidence = "Tutor is MultiplayerOnly";
            }
            else if (category == "Relics" && method.Name == "AfterObtained")
            {
                classification = "OutsideCombat";
                evidence = "Relic acquisition choice resolves outside an active combat route";
            }
            else if (hasNativeSurfaceGap)
            {
                classification = "Unresolved";
                evidence = "Combat choice method has no NativeChoiceRuntime surface: "
                    + string.Join(',', selectionMethods.Where(name => !nativeCombatChoiceMethods.Contains(name)));
            }
            else if (category == "Cards" && supportedCards.Contains(type.Name))
            {
                classification = "Supported";
                evidence = "CardChoiceSupport and NativeChoiceRuntime";
            }
            else if (category == "Potions" && supportedPotions.Contains(type.Name))
            {
                classification = "Supported";
                evidence = "PotionChoiceSupport and NativeChoiceRuntime";
            }
            else if (category == "Powers" && supportedPowers.Contains(type.Name))
            {
                classification = "Supported";
                evidence = "TurnStartChoiceSupport and PlayerTurnSetupCoordinator";
            }
            else if (category == "Relics" && supportedRelics.Contains(type.Name))
            {
                classification = "Supported";
                evidence = "PlayerTurnSetupCoordinator or vanilla deterministic selector";
            }
            else
            {
                classification = "Unresolved";
                evidence = "No matching single-player choice implementation";
            }

            entries.Add(new CombatChoiceSourceEntry(
                $"{type.FullName}.{Signature(method)}",
                category,
                type.FullName!,
                Signature(method),
                selectionMethods,
                classification,
                evidence));
        }
    }
    entries.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
    return new CombatChoiceSourceCatalog(
        2,
        combatSolverVersion,
        "0.111.0",
        entries.Count(entry => entry.Classification == "Unresolved"),
        entries);
}

static StateMutationCatalog AuditStateMutations(
    IReadOnlyList<CoverageEntry> entries,
    string combatSolverVersion)
{
    Assembly game = typeof(AbstractModel).Assembly;
    List<StateMutationEntry> mutations = [];
    foreach (CoverageEntry entry in entries.Where(entry =>
                 entry.ScopeGuess == "Combat"
                 && entry.Category != "MonsterMove"
                 && entry.EffectiveStatus != EffectiveSupportStatus.OutOfScope))
    {
        Type? type = game.GetType(entry.EntityType, throwOnError: false);
        if (type == null)
            continue;
        MethodInfo? method = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .SingleOrDefault(candidate => Signature(candidate) == entry.Hook);
        if (method == null)
            continue;

        string[] writes;
        try
        {
            writes = GetReachableImplementationMethods(method, type)
                .SelectMany(implementation => implementation.GetOriginalIl().Instructions)
                .Select(DescribeStateMutation)
                .Where(description => description != null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            continue;
        }
        if (writes.Length == 0)
            continue;
        string phase = StateMutationPhase(entry);
        bool requiresRuntimeEvidence = phase is "FutureLifecycle" or "ModelTransformation";
        mutations.Add(new StateMutationEntry(
            entry.Key,
            entry.EntityType,
            entry.Hook,
            phase,
            entry.EffectiveStatus.ToString(),
            entry.Capability,
            requiresRuntimeEvidence,
            entry.Verification == VerificationKind.Runtime,
            writes));
    }
    mutations.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
    return new StateMutationCatalog(
        SchemaVersion: 1,
        CombatSolverVersion: combatSolverVersion,
        GameVersion: "0.111.0",
        UnverifiedCount: mutations.Count(entry => entry.RequiresRuntimeEvidence && !entry.RuntimeVerified),
        SnapshotOnlyWithoutRuntimeEvidenceCount: mutations.Count(entry =>
            entry.Phase == "InitialSnapshot" && !entry.RuntimeVerified),
        StaticConfigurationWithoutRuntimeEvidenceCount: mutations.Count(entry =>
            entry.Phase == "StaticConfiguration" && !entry.RuntimeVerified),
        Entries: mutations);
}

static IReadOnlyList<StateMutationCardCheck> BuildCardUpgradeMutationChecks(StateMutationCatalog catalog)
{
    List<StateMutationCardCheck> checks = [];
    foreach (StateMutationEntry entry in catalog.Entries.Where(entry =>
                 entry.Phase == "ModelTransformation"
                 && entry.Hook == "OnUpgrade()"
                 && !entry.RuntimeVerified))
    {
        Type type = typeof(AbstractModel).Assembly.GetType(entry.EntityType, throwOnError: true)!;
        CardModel card = ModelDb.All.OfType<CardModel>().Single(model => model.GetType() == type);
        checks.Add(new StateMutationCardCheck(
            MonsterId: "BigDummy",
            MoveId: "NOTHING",
            EnemyHpBefore: 999,
            ClearPlayerHandBeforeMove: true,
            CardAfterMove: new SimpleCardInjection(card.Id.Entry, "Hand") with { UpgradeLevels = 1 },
            CoveredCardHooks: [new ModelHookDescriptor(card.Id.Entry, "OnUpgrade")]));
    }
    return checks;
}

static string StateMutationPhase(CoverageEntry entry)
{
    if (entry.Hook == "GenerateMoveStateMachine()")
        return "StaticConfiguration";
    if (entry.Hook is "BeforeCombatStart()" or "BeforeCombatStartLate()")
        return "InitialSnapshot";
    if (entry.Hook == "AfterCloned()" && entry.Category == "Relic")
        return "InitialSnapshot";
    if (entry.EntityName is "ChoicesParadox" or "FestivePopper" or "GamblingChip"
            or "JeweledMask" or "PowerCell" or "Toolbox" or "TwistedFunnel"
            or "VexingPuzzlebox" or "WhisperingEarring"
        && entry.Hook is not "OnUpgrade()")
    {
        return "InitialSnapshot";
    }
    if (entry.Hook is "OnUpgrade()" or "AfterDowngraded()" or "OnEnchant()")
        return "ModelTransformation";
    return "FutureLifecycle";
}

static IEnumerable<MethodInfo> GetImplementationMethods(MethodInfo method)
{
    yield return method;
    Type? stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
        ?? method.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
    MethodInfo? moveNext = stateMachine?.GetMethod(
        "MoveNext",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (moveNext != null)
        yield return moveNext;
}

static IEnumerable<MethodInfo> GetReachableImplementationMethods(MethodInfo root, Type modelType)
{
    Queue<MethodInfo> pending = new();
    HashSet<MethodInfo> visited = [];
    pending.Enqueue(root);
    while (pending.Count > 0)
    {
        MethodInfo method = pending.Dequeue();
        if (!visited.Add(method))
            continue;
        foreach (MethodInfo implementation in GetImplementationMethods(method))
        {
            if (visited.Add(implementation) || ReferenceEquals(implementation, method))
                yield return implementation;
            foreach (var instruction in implementation.GetOriginalIl().Instructions)
            {
                if (instruction.operand is MethodInfo called
                    && (called.DeclaringType == modelType || called.DeclaringType?.DeclaringType == modelType)
                    && !visited.Contains(called))
                {
                    pending.Enqueue(called);
                }
            }
        }
    }
}

static string? DescribeStateMutation(HarmonyLib.CodeInstruction instruction)
{
    if (instruction.opcode == OpCodes.Stfld
        && instruction.operand is FieldInfo field
        && field.DeclaringType?.Assembly == typeof(AbstractModel).Assembly
        && !field.DeclaringType.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
    {
        return $"field:{field.DeclaringType.FullName}.{field.Name}";
    }
    if (instruction.operand is not MethodBase called || !IsStateMutationCall(called))
        return null;
    return $"call:{called.DeclaringType?.FullName}.{called.Name}";
}

static bool IsStateMutationCall(MethodBase method)
{
    Type? type = method.DeclaringType;
    string typeName = type?.FullName ?? string.Empty;
    string methodName = method.Name;
    if (type?.Assembly == typeof(AbstractModel).Assembly)
    {
        if (methodName.StartsWith("set_", StringComparison.Ordinal))
            return true;
        if (typeName.StartsWith("MegaCrit.Sts2.Core.Commands.", StringComparison.Ordinal)
            && type?.Name is "CardCmd" or "CardPileCmd" or "CreatureCmd" or "PlayerCmd" or "PowerCmd" or "PotionCmd" or "OrbCmd")
        {
            return true;
        }
        if (typeName.StartsWith("MegaCrit.Sts2.Core.Combat.History", StringComparison.Ordinal)
            && !methodName.StartsWith("get_", StringComparison.Ordinal))
        {
            return true;
        }
        if (typeName.StartsWith("MegaCrit.Sts2.Core.Random", StringComparison.Ordinal))
        {
            return true;
        }
        string[] prefixes =
        [
            "Add", "Afflict", "Apply", "Discard", "Draw", "Enchant", "Exhaust", "Forge", "Gain",
            "Kill", "Lose", "Procure", "Remove", "Revive", "Set", "Shuffle", "Spawn", "Summon",
            "Transform", "Upgrade",
        ];
        if (prefixes.Any(prefix => methodName.StartsWith(prefix, StringComparison.Ordinal))
            && (typeof(AbstractModel).IsAssignableFrom(type)
                || typeName.StartsWith("MegaCrit.Sts2.Core.Entities.", StringComparison.Ordinal)
                || typeName.StartsWith("MegaCrit.Sts2.Core.Combat.", StringComparison.Ordinal)))
        {
            return true;
        }
    }
    return false;
}

static StateFieldCatalog AuditStateFields(string combatSolverVersion)
{
    if (!ModelDb.All.Any())
    {
        Type[] gameModelTypes = typeof(AbstractModel).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(AbstractModel).IsAssignableFrom(type))
            .ToArray();
        ModelDb.Init(gameModelTypes);
    }
    Assembly solver = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "CombatSolver.dll"));
    Type policy = solver.GetType("CombatSolver.SemanticStateFieldPolicy", throwOnError: true)!;
    MethodInfo classify = policy.GetMethod(
        "Classify",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(policy.FullName, "Classify");
    MethodInfo classifyString = policy.GetMethod(
        "ClassifyString",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(policy.FullName, "ClassifyString");

    List<StateFieldEntry> fields = [];
    IEnumerable<AbstractModel> models = ModelDb.All
        .Where(model => model is CardModel or PowerModel)
        .DistinctBy(model => model.GetType())
        .OrderBy(model => model.GetType().FullName, StringComparer.Ordinal);
    foreach (AbstractModel model in models)
    {
        Type type = model.GetType();
        IReadOnlyDictionary<string, DynamicVar>? dynamicVars = null;
        Exception? dynamicVarsError = null;
        try
        {
            dynamicVars = model switch
            {
                CardModel card => card.DynamicVars,
                PowerModel power => power.DynamicVars,
                _ => throw new UnreachableException(),
            };
        }
        catch (Exception exception)
        {
            dynamicVarsError = exception;
        }

        if (dynamicVars != null)
        {
            foreach ((string fieldName, DynamicVar value) in dynamicVars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AddStateField(fields, classify, model, type, fieldName, value.GetType(), value);
            }
            continue;
        }

        IReadOnlyList<(string Name, Type VarType)> staticFields = InspectCanonicalDynamicVars(type);
        if (staticFields.Count == 0)
        {
            fields.Add(new StateFieldEntry(
                type.FullName!,
                model.Id.Entry,
                model is CardModel ? "Card" : "Power",
                "<dynamic-vars>",
                typeof(DynamicVar).FullName!,
                "Unclassified",
                $"字段求值失败且无法从 CanonicalVars 提取：{dynamicVarsError}"));
            continue;
        }
        foreach ((string fieldName, Type varType) in staticFields)
            AddStateField(fields, classifyString, model, type, fieldName, varType, value: null);
    }

    fields.Sort(static (left, right) =>
    {
        int type = string.CompareOrdinal(left.EntityType, right.EntityType);
        return type != 0 ? type : string.CompareOrdinal(left.FieldName, right.FieldName);
    });
    return new StateFieldCatalog(
        SchemaVersion: 1,
        CombatSolverVersion: combatSolverVersion,
        GameVersion: "0.111.0",
        UnclassifiedCount: fields.Count(field => field.Role == "Unclassified"),
        Entries: fields);
}

static void AddStateField(
    ICollection<StateFieldEntry> fields,
    MethodInfo classifier,
    AbstractModel model,
    Type modelType,
    string fieldName,
    Type varType,
    DynamicVar? value)
{
    string role;
    string? notes = null;
    try
    {
        object?[] parameters = value == null
            ? [modelType, fieldName]
            : [model, fieldName, value];
        role = classifier.Invoke(null, parameters)?.ToString()
            ?? throw new InvalidOperationException("状态字段分类器返回 null。");
    }
    catch (TargetInvocationException exception)
    {
        role = "Unclassified";
        notes = exception.InnerException?.Message ?? exception.Message;
    }
    fields.Add(new StateFieldEntry(
        modelType.FullName!,
        model.Id.Entry,
        model is CardModel ? "Card" : "Power",
        fieldName,
        varType.FullName!,
        role,
        notes));
}

static IReadOnlyList<(string Name, Type VarType)> InspectCanonicalDynamicVars(Type modelType)
{
    MethodInfo? getter = modelType.GetProperty(
        "CanonicalVars",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(nonPublic: true);
    if (getter == null)
        return [];

    List<(string Name, Type VarType)> result = [];
    string? firstStringSinceLastVar = null;
    foreach (var instruction in getter.GetOriginalIl().Instructions)
    {
        if (instruction.opcode == OpCodes.Ldstr && firstStringSinceLastVar == null)
        {
            firstStringSinceLastVar = instruction.operand as string;
            continue;
        }
        if (instruction.opcode == OpCodes.Newobj
            && instruction.operand is ConstructorInfo constructor
            && constructor.DeclaringType is { } dynamicVarType
            && typeof(DynamicVar).IsAssignableFrom(dynamicVarType))
        {
            if (firstStringSinceLastVar == null)
                throw new InvalidOperationException($"{modelType.FullName}.CanonicalVars 创建 {dynamicVarType.Name} 时缺少字段名。");
            result.Add((firstStringSinceLastVar, dynamicVarType));
            firstStringSinceLastVar = null;
        }
    }
    return result;
}

static BranchStateReadRisk[] AuditBranchSensitiveOriginalFallbacks(
    IReadOnlyList<CoverageEntry> entries)
{
    Assembly game = typeof(AbstractModel).Assembly;
    List<BranchStateReadRisk> risks = [];
    foreach (CoverageEntry entry in entries.Where(entry =>
                 entry.EffectiveStatus == EffectiveSupportStatus.Exact
                 && entry.EngineDispatch == EngineDispatch.Exact
                 && entry.EngineRegistration == EngineRegistration.None
                 && entry.Category != "MonsterMove"
                 && !entry.Hook.StartsWith("OnPlay(", StringComparison.Ordinal)))
    {
        Type? type = game.GetType(entry.EntityType, throwOnError: false);
        if (type == null)
            continue;
        MethodInfo? method = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .SingleOrDefault(candidate => Signature(candidate) == entry.Hook);
        if (method == null)
            continue;

        IReadOnlyList<string> reads;
        try
        {
            reads = method.GetOriginalIl().Instructions
                .Select(instruction => HarmonyIl.TryGetCalledMethod(instruction, out MethodInfo? called)
                    ? called
                    : null)
                .Where(called => called != null && IsBranchSensitiveRead(called))
                .Select(called => $"{called!.DeclaringType?.FullName}.{called.Name}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            continue;
        }
        if (reads.Count > 0)
            risks.Add(new BranchStateReadRisk(entry.Key, entry.EntityType, entry.Hook, reads));
    }
    return risks.OrderBy(risk => risk.Key, StringComparer.Ordinal).ToArray();
}

static bool IsBranchSensitiveRead(MethodInfo method)
{
    string type = method.DeclaringType?.FullName ?? string.Empty;
    string name = method.Name;
    if (type == "MegaCrit.Sts2.Core.Entities.Creatures.Creature")
    {
        return name.StartsWith("get_", StringComparison.Ordinal)
            || name.StartsWith("HasPower", StringComparison.Ordinal)
            || name.StartsWith("GetPower", StringComparison.Ordinal)
            || name.StartsWith("ContainsPower", StringComparison.Ordinal);
    }
    if (type == "MegaCrit.Sts2.Core.Entities.Players.Player"
        || type == "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState"
        || type == "MegaCrit.Sts2.Core.Entities.Cards.CardPile"
        || type == "MegaCrit.Sts2.Core.Entities.Orbs.OrbQueue")
    {
        return name.StartsWith("get_", StringComparison.Ordinal);
    }
    if (type == "MegaCrit.Sts2.Core.Models.CardModel")
    {
        return name is "get_Pile" or "get_CombatState" or "get_CurrentTarget";
    }
    if (type == "MegaCrit.Sts2.Core.Combat.CombatManager")
        return name.StartsWith("get_", StringComparison.Ordinal);
    if (type.StartsWith("MegaCrit.Sts2.Core.Combat.History", StringComparison.Ordinal))
        return true;
    if (type is "MegaCrit.Sts2.Core.Combat.ICombatState" or "MegaCrit.Sts2.Core.Combat.CombatState")
        return name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("Get", StringComparison.Ordinal);
    if (type.StartsWith("MegaCrit.Sts2.Core.Random", StringComparison.Ordinal))
        return true;
    return false;
}

static string[] AuditPersistentRelicPredictionStates()
{
    Assembly solver = typeof(CombatSolver.Entry).Assembly;
    Type forkable = solver.GetType("CombatSolver.Engine.Common.IPredictionStateForkable")
        ?? throw new InvalidOperationException("IPredictionStateForkable is unavailable.");
    Type support = solver.GetType("CombatSolver.RelicPredictionStateSupport")
        ?? throw new InvalidOperationException("RelicPredictionStateSupport is unavailable.");
    MethodInfo isTracked = support.GetMethod(
        "IsTracked",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("RelicPredictionStateSupport.IsTracked is unavailable.");
    MethodInfo verifyLive = support.GetMethod(
        "LiveStateForVerification",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("RelicPredictionStateSupport.LiveStateForVerification is unavailable.");

    HashSet<Type> dedicatedRelics = solver.GetTypes()
        .Where(type => !type.IsAbstract && forkable.IsAssignableFrom(type))
        .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        .Select(constructor => constructor.GetParameters())
        .Where(parameters => parameters.Length == 1 && typeof(RelicModel).IsAssignableFrom(parameters[0].ParameterType))
        .Select(parameters => parameters[0].ParameterType)
        .Where(type => type != typeof(RelicModel))
        .ToHashSet();

    List<string> gaps = [];
    foreach (Type relicType in dedicatedRelics.OrderBy(type => type.FullName, StringComparer.Ordinal))
    {
        if (CanonicalModel(relicType) is not RelicModel relic)
        {
            gaps.Add($"{relicType.FullName}:cannot_construct");
            continue;
        }
        if (isTracked.Invoke(null, [relic]) is not true)
        {
            gaps.Add($"{relicType.FullName}:not_tracked");
            continue;
        }
        try
        {
            _ = verifyLive.Invoke(null, [relic]);
        }
        catch (TargetInvocationException ex)
        {
            gaps.Add($"{relicType.FullName}:descriptor_failed:{ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
        }
    }
    return gaps.ToArray();
}

static List<CoverageEntry> DiscoverMonsterMoves(
    IReadOnlyDictionary<string, CoverageClassification> classifications)
{
    Assembly game = typeof(AbstractModel).Assembly;
    Type monsterBase = typeof(MonsterModel);
    FieldInfo performCallbackField = typeof(MoveState).GetField(
        "_onPerform",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MoveState._onPerform is unavailable in this game version.");
    List<CoverageEntry> entries = [];
    foreach (Type type in game.GetTypes()
                 .Where(type => !type.IsAbstract
                                && monsterBase.IsAssignableFrom(type)
                                && type.Namespace?.Contains(".Mocks", StringComparison.Ordinal) != true
                                && !type.Name.Contains("Mock", StringComparison.Ordinal)
                                && !type.Name.Contains("Test", StringComparison.Ordinal))
                 .OrderBy(type => type.FullName, StringComparer.Ordinal))
    {
        MonsterModel canonical = (MonsterModel)CanonicalModel(type);
        MonsterModel monster = canonical.ToMutable();
        MethodInfo generate = type.GetMethod(
            "GenerateMoveStateMachine",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Monster {type.FullName} has no move-state-machine generator.");
        MonsterMoveStateMachine machine;
        try
        {
            machine = (MonsterMoveStateMachine)(generate.Invoke(monster, null)
                ?? throw new InvalidOperationException("Generator returned null."));
        }
        catch (TargetInvocationException ex)
        {
            throw new InvalidOperationException(
                $"Could not enumerate moves for {type.FullName}.",
                ex.InnerException ?? ex);
        }

        foreach (MoveState move in machine.States.Values.OfType<MoveState>().Distinct()
                     .OrderBy(move => move.Id, StringComparer.Ordinal))
        {
            string key = $"MonsterMove|{type.FullName}|{move.Id}";
            CoverageClassification? classification = classifications.GetValueOrDefault(key);
            string intents = string.Join(",", move.Intents.Select(DescribeIntent));
            Delegate callback = (Delegate)(performCallbackField.GetValue(move)
                ?? throw new InvalidOperationException($"Move {key} has no perform callback."));
            string callbackName = $"{callback.Method.DeclaringType?.FullName}.{callback.Method.Name}";
            entries.Add(new CoverageEntry(
                Key: key,
                Category: "MonsterMove",
                EntityType: type.FullName!,
                EntityName: type.Name,
                HookOwner: type.FullName!,
                Hook: move.Id,
                ScopeGuess: "Combat",
                EngineRegistration: EngineRegistration.None,
                EngineDispatch: EngineDispatch.None,
                Status: classification?.Status ?? CoverageStatus.Unanalyzed,
                Source: classification?.Source ?? string.Empty,
                Notes: classification?.Notes ?? $"Callback: {callbackName}; Intents: {intents}",
                TestId: classification?.TestId)
            {
                Capability = classification?.Capability ?? DefaultCapability(classification?.Status ?? CoverageStatus.Unanalyzed),
                MethodHash = HashMethod(callback.Method),
            });
        }
    }
    return entries;
}

static string DescribeIntent(AbstractIntent intent)
{
    return intent is AttackIntent attack
        ? $"Attack({attack.Repeats})"
        : intent.IntentType.ToString();
}
return 0;

static List<CoverageEntry> DiscoverGameHooks(
    EngineMirrorInventory engine,
    IReadOnlyDictionary<string, CoverageClassification> classifications)
{
    Assembly game = typeof(AbstractModel).Assembly;
    Type[] entityBases =
    [
        typeof(CardModel),
        Resolve("MegaCrit.Sts2.Core.Models.PowerModel"),
        Resolve("MegaCrit.Sts2.Core.Models.RelicModel"),
        Resolve("MegaCrit.Sts2.Core.Models.PotionModel"),
        Resolve("MegaCrit.Sts2.Core.Models.OrbModel"),
        Resolve("MegaCrit.Sts2.Core.Models.EnchantmentModel"),
        Resolve("MegaCrit.Sts2.Core.Models.AfflictionModel"),
        Resolve("MegaCrit.Sts2.Core.Models.MonsterModel"),
    ];

    List<CoverageEntry> entries = [];
    foreach (Type type in game.GetTypes()
                 .Where(type => !type.IsAbstract && entityBases.Any(baseType => baseType.IsAssignableFrom(type)))
                 .OrderBy(type => type.FullName, StringComparer.Ordinal))
    {
        string category = Category(type, entityBases);
        bool multiplayerOnly = category == "Card"
            && CanonicalModel(type) is CardModel card
            && card.MultiplayerConstraint == MegaCrit.Sts2.Core.Entities.Cards.CardMultiplayerConstraint.MultiplayerOnly;
        bool deprecatedPlaceholder = type == typeof(DeprecatedCard);
        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (!method.IsVirtual || method.IsSpecialName || method.GetBaseDefinition() == method)
                continue;

            MethodInfo baseMethod = method.GetBaseDefinition();
            string signature = Signature(baseMethod);
            string key = $"{category}|{type.FullName}|{baseMethod.DeclaringType?.FullName}.{signature}";
            EngineMirrorAnalysis engineAnalysis = AnalyzeEngine(engine, type, method, baseMethod);
            CoverageClassification? classification = classifications.GetValueOrDefault(key);
            string scope = multiplayerOnly
                ? "MultiplayerOnly"
                : deprecatedPlaceholder
                    ? "DeprecatedPlaceholder"
                    : ScopeGuess(type, baseMethod);
            BuiltInClassification? builtIn = BuiltInStatus(category, baseMethod.Name);
            CoverageStatus status = classification?.Status ?? scope switch
            {
                "TestOrMock" or "OutOfCombat" or "MultiplayerOnly" or "DeprecatedPlaceholder"
                    => CoverageStatus.NotCombatRelevant,
                _ when builtIn != null => builtIn.Status,
                _ when engineAnalysis.Dispatch == EngineDispatch.Exact => CoverageStatus.EngineExact,
                _ when engineAnalysis.Dispatch == EngineDispatch.Inferred => CoverageStatus.EngineInferred,
                _ => CoverageStatus.Unanalyzed,
            };
            string source = classification?.Source ?? builtIn?.Source ?? status switch
            {
                CoverageStatus.NotCombatRelevant => "CoverageCatalog scope classifier",
                CoverageStatus.NativeRuntimeState => "CombatSolver embedded engine PredictionUtils.UpgradeCard",
                CoverageStatus.EngineExact => "CombatSolver embedded engine explicit registry",
                CoverageStatus.EngineInferred => "CombatSolver embedded engine method inferrer",
                _ => string.Empty,
            };
            string notes = classification?.Notes ?? engineAnalysis.Notes;
            entries.Add(new CoverageEntry(
                Key: key,
                Category: category,
                EntityType: type.FullName!,
                EntityName: type.Name,
                HookOwner: baseMethod.DeclaringType?.FullName ?? string.Empty,
                Hook: signature,
                ScopeGuess: scope,
                EngineRegistration: engineAnalysis.Registration,
                EngineDispatch: engineAnalysis.Dispatch,
                Status: status,
                Source: source,
                Notes: notes,
                TestId: classification?.TestId)
            {
                Capability = classification?.Capability ?? DefaultCapability(status),
                MethodHash = HashMethod(method),
            });
        }
    }
    return entries;

    Type Resolve(string fullName) => game.GetType(fullName, throwOnError: true)!;
}

static BuiltInClassification? BuiltInStatus(string category, string methodName)
{
    if (category == "Card" && methodName is "OnUpgrade" or "AfterDowngraded")
        return new(CoverageStatus.NativeRuntimeState, "Native CardModel mutable clone state");
    if (category == "Monster" && methodName is "GenerateMoveStateMachine")
        return new(CoverageStatus.NativeRuntimeState, "Live MonsterModel move state machine");
    if (category == "Monster" && methodName is
        "GenerateAnimator" or "SetupSkins" or "ShouldShowMoveInBestiary" or "GenerateBestiaryMoveList" or
        "SegmentAttack")
    {
        return new(CoverageStatus.NotCombatRelevant, "Visual or bestiary-only MonsterModel hook");
    }
    if (category == "Relic" && methodName is
        "AfterObtained" or "AfterCombatEnd" or "AfterCombatVictory" or "IsAllowed" or "IsAllowedAtNeow")
    {
        return new(CoverageStatus.NotCombatRelevant, "Outside single-combat search lifecycle");
    }
    if (category == "Relic" && methodName is "BeforeCombatStart" or "AfterCloned")
        return new(CoverageStatus.NativeRuntimeState, "Captured in live relic state before solver snapshot");
    if (category == "Power" && methodName is "InitInternalData" or "BeforeCombatStart")
        return new(CoverageStatus.NativeRuntimeState, "Captured in live Power state before solver snapshot");
    if (category == "Power" && methodName is "AfterCombatEnd" or "GetScaledAmountForMultiplayer")
        return new(CoverageStatus.NotCombatRelevant, "Outside supported single-player combat route");
    if (category == "Enchantment" && methodName is "CanEnchant" or "CanEnchantCardType" or "OnEnchant")
        return new(CoverageStatus.NativeRuntimeState, "Captured in enchanted card state before simulation");
    if (category == "Card" && methodName == "OnEnqueuePlayVfx")
        return new(CoverageStatus.NotCombatRelevant, "Visual-only card hook");
    return null;
}

static CoverageEntry EnrichEffectiveSupport(
    CoverageEntry entry,
    IReadOnlyDictionary<string, CoverageTestEvidence> evidence,
    IReadOnlySet<string> compensatedCardTypes,
    IReadOnlyDictionary<string, string> machineCardEvidence,
    IReadOnlyDictionary<string, string> machineModelHookEvidence,
    IReadOnlyDictionary<string, string> machineMonsterMoveEvidence)
{
    VerificationKind verification = VerificationKind.None;
    if (entry.TestId is { } testId && evidence.TryGetValue(testId, out CoverageTestEvidence? item))
    {
        verification = item.Status switch
        {
            VerificationStatus.Passed => VerificationKind.Runtime,
            VerificationStatus.PassedWithDocumentedBoundaries => VerificationKind.RuntimeWithDocumentedBoundaries,
            VerificationStatus.PassedWithDocumentedPerformanceRegression => VerificationKind.RuntimeWithDocumentedPerformanceRegression,
            VerificationStatus.StaticPassed => VerificationKind.Static,
            _ => VerificationKind.None,
        };
    }

    EffectiveSupportStatus effective = entry.Status switch
    {
        CoverageStatus.NotCombatRelevant => EffectiveSupportStatus.OutOfScope,
        CoverageStatus.SearchPolicyExcluded => EffectiveSupportStatus.NativeAutoRescan,
        CoverageStatus.EngineInferred when verification == VerificationKind.Runtime => EffectiveSupportStatus.Exact,
        CoverageStatus.EngineInferred => EffectiveSupportStatus.NeedsReview,
        CoverageStatus.Unanalyzed or CoverageStatus.PendingImplementation => EffectiveSupportStatus.Unsupported,
        _ => EffectiveSupportStatus.Exact,
    };
    bool cardCompensation = entry.Category == "Card"
        && entry.Status != CoverageStatus.NotCombatRelevant
        && entry.Hook.StartsWith("OnPlay(", StringComparison.Ordinal)
        && compensatedCardTypes.Contains(entry.EntityType);
    bool cardOnPlay = entry.Category == "Card"
        && entry.Status != CoverageStatus.NotCombatRelevant
        && entry.Hook.StartsWith("OnPlay(", StringComparison.Ordinal);
    string? effectiveTestId = entry.TestId;
    if (verification != VerificationKind.Runtime
        && cardOnPlay
        && machineCardEvidence.TryGetValue(entry.EntityType, out string? machineTestId)
        && evidence.TryGetValue(machineTestId, out CoverageTestEvidence? machineEvidence)
        && machineEvidence.Status == VerificationStatus.Passed)
    {
        verification = VerificationKind.Runtime;
        effectiveTestId = machineTestId;
        if (entry.Status == CoverageStatus.EngineInferred)
            effective = EffectiveSupportStatus.Exact;
    }
    if (verification != VerificationKind.Runtime
        && entry.Category == "MonsterMove"
        && machineMonsterMoveEvidence.TryGetValue(
            MonsterMoveEvidenceKey(entry.EntityType, entry.Hook),
            out string? monsterMoveTestId)
        && evidence.TryGetValue(monsterMoveTestId, out CoverageTestEvidence? monsterMoveEvidence)
        && monsterMoveEvidence.Status == VerificationStatus.Passed)
    {
        verification = VerificationKind.Runtime;
        effectiveTestId = monsterMoveTestId;
    }
    if (verification != VerificationKind.Runtime
        && machineModelHookEvidence.TryGetValue(
            ModelHookEvidenceKey(entry.EntityType, HookName(entry.Hook)),
            out string? modelHookTestId)
        && evidence.TryGetValue(modelHookTestId, out CoverageTestEvidence? modelHookEvidence)
        && modelHookEvidence.Status == VerificationStatus.Passed)
    {
        verification = VerificationKind.Runtime;
        effectiveTestId = modelHookTestId;
    }
    if (cardCompensation)
        effective = EffectiveSupportStatus.Exact;
    return entry with
    {
        EffectiveStatus = effective,
        Verification = verification,
        Capability = cardCompensation ? "SolverCompensation" : entry.Capability,
        TestId = effectiveTestId,
    };
}

static HashSet<string> ReadCompensatedCardTypes()
{
    Assembly assembly = Assembly.Load("CombatSolver");
    HashSet<string> result = new(StringComparer.Ordinal);
    string[] catalogNames =
    [
        "CombatSolver.CardOnPlayCompensationCatalog",
        "CombatSolver.CardEffectSpecRegistry",
        "CombatSolver.CalculatedVarSpecRegistry",
    ];
    foreach (string catalogName in catalogNames)
    {
        Type catalog = assembly.GetType(catalogName, throwOnError: true)!;
        object value = catalog.GetProperty(
                "SupportedTypes",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null)
            ?? throw new InvalidOperationException($"{catalogName}.SupportedTypes is unavailable.");
        foreach (object item in (IEnumerable)value)
        {
            if (item is Type { FullName: { } fullName })
                result.Add(fullName);
        }
    }
    return result;
}

static HashSet<string> ReadCalculatedCardTypes()
{
    Assembly assembly = Assembly.Load("CombatSolver");
    Type catalog = assembly.GetType("CombatSolver.CalculatedVarSpecRegistry", throwOnError: true)!;
    object value = catalog.GetProperty(
            "SupportedTypes",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?.GetValue(null)
        ?? throw new InvalidOperationException("CalculatedVarSpecRegistry.SupportedTypes is unavailable.");
    return ((IEnumerable)value).Cast<Type>()
        .Select(type => type.FullName!)
        .ToHashSet(StringComparer.Ordinal);
}

static Dictionary<string, string> ReadMachineCardEvidence()
{
    Assembly assembly = Assembly.Load("CombatSolver");
    Dictionary<string, string> result = new(StringComparer.Ordinal);
    string[] catalogNames =
    [
        "CombatSolver.CardOnPlayCompensationCatalog",
        "CombatSolver.CardEffectSpecRegistry",
        "CombatSolver.CalculatedVarSpecRegistry",
    ];
    foreach (string catalogName in catalogNames)
    {
        Type catalog = assembly.GetType(catalogName, throwOnError: true)!;
        if (catalog.GetProperty(
                "EvidenceByType",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null) is not IEnumerable values)
        {
            continue;
        }
        foreach (object item in values)
        {
            Type itemType = item.GetType();
            if (itemType.GetProperty("Key")?.GetValue(item) is Type { FullName: { } fullName }
                && itemType.GetProperty("Value")?.GetValue(item) is string evidenceId)
            {
                result[fullName] = evidenceId;
            }
        }
    }
    return result;
}

static Dictionary<string, string> ReadFixtureCardEvidence(
    string root,
    IReadOnlyDictionary<string, CoverageTestEvidence> evidence)
{
    Dictionary<string, string> cardTypeById = typeof(CardModel).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && typeof(CardModel).IsAssignableFrom(type))
        .Select(type => (Type: type, Card: (CardModel?)CanonicalModel(type)))
        .Where(pair => pair.Card != null)
        .ToDictionary(pair => pair.Card!.Id.Entry, pair => pair.Type.FullName!, StringComparer.Ordinal);
    Dictionary<string, string> result = new(StringComparer.Ordinal);
    foreach ((string testId, CoverageTestEvidence item) in evidence)
    {
        if (item.Status != VerificationStatus.Passed)
            continue;
        foreach (string path in EnumerateEvidenceFixturePaths(root, item.Evidence))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            HashSet<string> playedCardIds = [];
            CollectPlayedCardIds(document.RootElement, descriptor: false, playedCardIds);
            foreach (string cardId in playedCardIds)
            {
                if (cardTypeById.TryGetValue(cardId, out string? entityType))
                    result.TryAdd(entityType, testId);
            }
        }
    }
    return result;
}

static Dictionary<string, string> ReadFixtureMonsterMoveEvidence(
    string root,
    IReadOnlyDictionary<string, CoverageTestEvidence> evidence)
{
    Dictionary<string, string> monsterTypeByIdentifier = typeof(MonsterModel).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && typeof(MonsterModel).IsAssignableFrom(type))
        .Select(type => (Type: type, Monster: (MonsterModel?)CanonicalModel(type)))
        .Where(pair => pair.Monster != null)
        .SelectMany(pair => new[]
        {
            (Identifier: pair.Type.Name, Type: pair.Type.FullName!),
            (Identifier: pair.Monster!.Id.Entry, Type: pair.Type.FullName!),
        })
        .GroupBy(pair => pair.Identifier, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Select(pair => pair.Type).Distinct(StringComparer.Ordinal).Count() == 1)
        .ToDictionary(
            group => group.Key,
            group => group.First().Type,
            StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> result = new(StringComparer.Ordinal);
    foreach ((string testId, CoverageTestEvidence item) in evidence)
    {
        if (item.Status != VerificationStatus.Passed)
            continue;
        foreach (string path in EnumerateEvidenceFixturePaths(root, item.Evidence))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            CollectMonsterMoveEvidence(document.RootElement, monsterTypeByIdentifier, testId, result);
        }
    }
    return result;
}

static Dictionary<string, string> ReadFixtureModelHookEvidence<TModel>(
    string root,
    IReadOnlyDictionary<string, CoverageTestEvidence> evidence,
    string descriptorProperty,
    string idProperty)
    where TModel : AbstractModel
{
    Dictionary<string, string> modelTypeById = typeof(AbstractModel).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && typeof(TModel).IsAssignableFrom(type))
        .Select(type => (Type: type, Model: (TModel?)CanonicalModel(type)))
        .Where(pair => pair.Model != null)
        .ToDictionary(pair => pair.Model!.Id.Entry, pair => pair.Type.FullName!, StringComparer.Ordinal);
    Dictionary<string, string> result = new(StringComparer.Ordinal);
    foreach ((string testId, CoverageTestEvidence item) in evidence)
    {
        if (item.Status != VerificationStatus.Passed)
            continue;
        foreach (string path in EnumerateEvidenceFixturePaths(root, item.Evidence))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            CollectModelHookEvidence(
                document.RootElement,
                modelTypeById,
                testId,
                descriptorProperty,
                idProperty,
                result);
        }
    }
    return result;
}

static void CollectModelHookEvidence(
    JsonElement element,
    IReadOnlyDictionary<string, string> modelTypeById,
    string testId,
    string descriptorProperty,
    string idProperty,
    IDictionary<string, string> result)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Array:
            foreach (JsonElement item in element.EnumerateArray())
                CollectModelHookEvidence(
                    item,
                    modelTypeById,
                    testId,
                    descriptorProperty,
                    idProperty,
                    result);
            break;
        case JsonValueKind.Object:
            if (element.TryGetProperty(descriptorProperty, out JsonElement hooks)
                && hooks.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement descriptor in hooks.EnumerateArray())
                {
                    string modelId = descriptor.GetProperty(idProperty).GetString()
                        ?? throw new InvalidOperationException($"{testId} 的 {descriptorProperty} 缺少 {idProperty}。");
                    string hook = descriptor.GetProperty("hook").GetString()
                        ?? throw new InvalidOperationException($"{testId} 的 {descriptorProperty} 缺少 hook。");
                    if (!modelTypeById.TryGetValue(modelId, out string? modelType))
                        throw new InvalidOperationException($"{testId} 的 {descriptorProperty} 引用了未知模型 {modelId}。");
                    result.TryAdd(ModelHookEvidenceKey(modelType, hook), testId);
                }
            }
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name != descriptorProperty)
                {
                    CollectModelHookEvidence(
                        property.Value,
                        modelTypeById,
                        testId,
                        descriptorProperty,
                        idProperty,
                        result);
                }
            }
            break;
    }
}

static void CollectMonsterMoveEvidence(
    JsonElement element,
    IReadOnlyDictionary<string, string> monsterTypeByIdentifier,
    string testId,
    IDictionary<string, string> result)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Array:
            foreach (JsonElement item in element.EnumerateArray())
                CollectMonsterMoveEvidence(item, monsterTypeByIdentifier, testId, result);
            break;
        case JsonValueKind.Object:
            if (element.TryGetProperty("monsterId", out JsonElement monsterIdElement)
                && monsterIdElement.ValueKind == JsonValueKind.String
                && monsterIdElement.GetString() is { Length: > 0 } monsterId
                && element.TryGetProperty("moveId", out JsonElement moveIdElement)
                && moveIdElement.ValueKind == JsonValueKind.String
                && moveIdElement.GetString() is { Length: > 0 } moveId
                && monsterTypeByIdentifier.TryGetValue(monsterId, out string? monsterType))
            {
                result.TryAdd(MonsterMoveEvidenceKey(monsterType, moveId), testId);
            }
            foreach (JsonProperty property in element.EnumerateObject())
                CollectMonsterMoveEvidence(property.Value, monsterTypeByIdentifier, testId, result);
            break;
    }
}

static IEnumerable<string> EnumerateEvidenceFixturePaths(string root, string evidence)
{
    string fixtureDirectory = Path.Combine(root, "coverage", "unattended");
    HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    foreach (Match match in Regex.Matches(
                 evidence,
                 @"(?:coverage/unattended/)?(?<file>[A-Za-z0-9._-]+\.json)",
                 RegexOptions.CultureInvariant))
    {
        string path = Path.Combine(fixtureDirectory, match.Groups["file"].Value);
        if (File.Exists(path) && paths.Add(path))
            yield return path;
    }
}

static string MonsterMoveEvidenceKey(string monsterType, string moveId)
    => $"{monsterType}|{moveId}";

static string ModelHookEvidenceKey(string modelType, string hook)
    => $"{modelType}|{hook}";

static string HookName(string hook)
{
    int separator = hook.IndexOf('(');
    return separator < 0 ? hook : hook[..separator];
}

static void CollectPlayedCardIds(JsonElement element, bool descriptor, ISet<string> result)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Array:
            foreach (JsonElement item in element.EnumerateArray())
                CollectPlayedCardIds(item, descriptor, result);
            break;
        case JsonValueKind.Object:
            if (descriptor
                && element.TryGetProperty("cardId", out JsonElement cardId)
                && cardId.ValueKind == JsonValueKind.String
                && cardId.GetString() is { Length: > 0 } value)
            {
                result.Add(value);
            }
            foreach (JsonProperty property in element.EnumerateObject())
            {
                bool childDescriptor = property.Name.StartsWith("cardPlayChecks", StringComparison.Ordinal)
                    || property.Name == "playCardAfterMove";
                CollectPlayedCardIds(property.Value, descriptor || childDescriptor, result);
            }
            break;
    }
}

static string DefaultCapability(CoverageStatus status)
    => status switch
    {
        CoverageStatus.EngineExact => "EngineMirror",
        CoverageStatus.EngineInferred => "InferredEffect",
        CoverageStatus.SolverCompensation => "SolverCompensation",
        CoverageStatus.SearchPolicyExcluded => "DynamicBoundary",
        CoverageStatus.NativeRuntimeState => "NativeSnapshot",
        CoverageStatus.NotCombatRelevant => "OutOfScope",
        _ => "Unsupported",
    };

static RuntimeEvidenceGapEntry ToRuntimeEvidenceGap(CoverageEntry entry)
{
    Type? type = typeof(AbstractModel).Assembly.GetType(entry.EntityType, throwOnError: false);
    AbstractModel? model = type is { IsAbstract: false }
        ? CanonicalModel(type)
        : null;
    return new RuntimeEvidenceGapEntry(
        entry.Key,
        entry.Category,
        entry.EntityName,
        model?.Id.Entry,
        model is CardModel card ? card.TargetType.ToString() : null,
        entry.Status.ToString(),
        entry.Hook,
        entry.Capability,
        entry.MethodHash);
}

static IReadOnlyList<SimpleCardAuditCheck> BuildSimpleCardAuditChecks(IEnumerable<CoverageEntry> gaps)
{
    HashSet<string> choiceCards =
    [
        nameof(Acrobatics), nameof(Armaments), nameof(CosmicIndifference), nameof(DaggerThrow),
        nameof(Glimmer), nameof(GrandFinale), nameof(Graveblast), nameof(Headbutt), nameof(Hologram), nameof(NeowsFury),
        nameof(PhotonCut), nameof(Prepared), nameof(Survivor), nameof(ThinkingAhead),
    ];
    List<SimpleCardAuditCheck> checks = [];
    foreach (CoverageEntry gap in gaps.Where(entry =>
                 entry.Category == "Card"
                 && (entry.Status == CoverageStatus.EngineInferred
                     || entry.EntityName is nameof(Dismantle) or nameof(LeadingStrike) or nameof(Maul) or nameof(Spite) or nameof(TheScythe))
                 && entry.Hook.StartsWith("OnPlay(", StringComparison.Ordinal)
                 && !choiceCards.Contains(entry.EntityName)))
    {
        Type type = typeof(CardModel).Assembly.GetType(gap.EntityType, throwOnError: true)!;
        CardModel card = (CardModel)CanonicalModel(type);
        IReadOnlyDictionary<string, string>? enumMembers = card is MadScience
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(MadScience.TinkerTimeType)] = CardType.Attack.ToString(),
                [nameof(MadScience.TinkerTimeRider)] = TinkerTime.RiderEffect.Sapping.ToString(),
            }
            : null;
        string target = card is MadScience ? "Enemy" : card.TargetType switch
        {
            MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyEnemy => "Enemy",
            MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyAlly => "Player",
            _ => "None",
        };
        checks.Add(new SimpleCardAuditCheck(
            MonsterId: "FuzzyWurmCrawler",
            MoveId: "INHALE",
            EnemyHpBefore: 999,
            ClearPlayerHandBeforeMove: true,
            PlayerEnergyBefore: card.EnergyCost.CostsX ? 1 : 99,
            PlayerStarsBefore: 99,
            OstyHpBefore: card.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.OstyAttack) ? 12 : null,
            CardsBeforeMove: [new SimpleCardInjection(
                card.Id.Entry,
                "Hand",
                enumMembers ?? new Dictionary<string, string>(StringComparer.Ordinal))],
            CardPlayChecksAfterMove: [new SimpleCardPlayCheck(card.Id.Entry, target)]));
    }
    return checks;
}

static IReadOnlyList<SimpleCardAuditCheck> BuildExactCardAuditChecks(IEnumerable<CoverageEntry> gaps)
{
    HashSet<string> manualFixtureCards = [nameof(SeekerStrike)];
    List<SimpleCardAuditCheck> checks = [];
    foreach (CoverageEntry gap in gaps.Where(entry =>
                 entry.Category == "Card"
                 && entry.Status == CoverageStatus.EngineExact
                 && entry.Hook.StartsWith("OnPlay(", StringComparison.Ordinal)
                 && !manualFixtureCards.Contains(entry.EntityName)))
    {
        Type type = typeof(CardModel).Assembly.GetType(gap.EntityType, throwOnError: true)!;
        CardModel card = (CardModel)CanonicalModel(type);
        IReadOnlyDictionary<string, string>? enumMembers = card is MadScience
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(MadScience.TinkerTimeType)] = CardType.Attack.ToString(),
                [nameof(MadScience.TinkerTimeRider)] = TinkerTime.RiderEffect.Sapping.ToString(),
            }
            : null;
        string target = card is MadScience ? "Enemy" : card.TargetType switch
        {
            MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyEnemy => "Enemy",
            MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyAlly => "Player",
            _ => "None",
        };
        checks.Add(new SimpleCardAuditCheck(
            MonsterId: "FuzzyWurmCrawler",
            MoveId: "INHALE",
            EnemyHpBefore: 999,
            ClearPlayerHandBeforeMove: true,
            PlayerEnergyBefore: card.EnergyCost.CostsX ? 1 : 99,
            PlayerStarsBefore: 99,
            OstyHpBefore: card.Tags.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardTag.OstyAttack) ? 12 : null,
            CardsBeforeMove: [new SimpleCardInjection(
                card.Id.Entry,
                "Hand",
                enumMembers ?? new Dictionary<string, string>(StringComparer.Ordinal))],
            CardPlayChecksAfterMove: [new SimpleCardPlayCheck(card.Id.Entry, target)]));
    }
    return checks;
}

static IReadOnlyList<SimpleMonsterMoveAuditCheck> BuildSimpleMonsterMoveAuditChecks(
    IEnumerable<CoverageEntry> gaps)
    => gaps
        .Where(entry => entry.Category == "MonsterMove"
            && entry.Status == CoverageStatus.SolverCompensation)
        .Select(entry => new SimpleMonsterMoveAuditCheck(
            MonsterId: entry.EntityName,
            MoveId: entry.Hook,
            PlayerHpBefore: 80,
            PlayerBlockBefore: 0,
            EnemyHpBefore: 999))
        .ToArray();

static string HashMethod(MethodInfo method)
{
    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    Append(method);
    if (method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType is { } stateMachine)
    {
        MethodInfo moveNext = stateMachine.GetMethod(
            nameof(IAsyncStateMachine.MoveNext),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(stateMachine.FullName, nameof(IAsyncStateMachine.MoveNext));
        Append(moveNext);
    }
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

    void Append(MethodInfo value)
    {
        byte[] identity = Encoding.UTF8.GetBytes(
            $"{value.DeclaringType?.FullName}.{Signature(value)}|{value.ReturnType.FullName}");
        hash.AppendData(identity);
        MethodBody? body = value.GetMethodBody();
        if (body?.GetILAsByteArray() is { } il)
            hash.AppendData(il);
        foreach (LocalVariableInfo local in body?.LocalVariables ?? [])
            hash.AppendData(Encoding.UTF8.GetBytes(local.LocalType.FullName ?? local.LocalType.Name));
    }
}

static AbstractModel CanonicalModel(Type type)
    => ModelDb.GetById<AbstractModel>(ModelDb.GetId(type));

static EngineMirrorInventory ReadEngineMirrorInventory()
{
    Assembly engineAssembly = Assembly.Load("CombatSolver");
    Dictionary<string, List<EngineRegistryInfo>> registries = new(StringComparer.Ordinal);
    foreach (Type mirrorType in engineAssembly.GetTypes()
                 .Where(type => !type.ContainsGenericParameters
                     && type.Namespace?.Contains(".Mirrors", StringComparison.Ordinal) == true))
    {
        try
        {
            RuntimeHelpers.RunClassConstructor(mirrorType.TypeHandle);
        }
        catch
        {
            continue;
        }

        foreach (FieldInfo registryField in mirrorType.GetFields(
                     BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (registryField.FieldType.ContainsGenericParameters)
                continue;
            object? registry = registryField.GetValue(null);
            if (registry is not IMethodMirrorRegistryDescriptorProvider descriptorProvider)
                continue;
            MethodMirrorRegistryDescriptor descriptor = descriptorProvider.DescribeMirrorSupport();
            Dictionary<Type, EngineRegistration> explicitRegistrations = descriptor.Registrations
                .ToDictionary(
                    registration => registration.ReceiverType,
                    registration => registration.Kind switch
                    {
                        MethodMirrorRegistrationKind.Handled => EngineRegistration.Registered,
                        MethodMirrorRegistrationKind.Ignored => EngineRegistration.Ignored,
                        _ => throw new ArgumentOutOfRangeException(nameof(registration.Kind), registration.Kind, null),
                    });
            string hookKey = HookKey(descriptor.BaseMethod);
            if (!registries.TryGetValue(hookKey, out List<EngineRegistryInfo>? hookRegistries))
            {
                hookRegistries = [];
                registries.Add(hookKey, hookRegistries);
            }
            hookRegistries.Add(new EngineRegistryInfo(
                descriptor.ReceiverType,
                explicitRegistrations,
                descriptor.StrictInferrer,
                descriptor.Inferrer,
                $"{mirrorType.FullName}.{registryField.Name}"));
        }
    }
    return new EngineMirrorInventory(registries);
}

static EngineMirrorAnalysis AnalyzeEngine(EngineMirrorInventory inventory, Type type, MethodInfo overrideMethod, MethodInfo baseMethod)
{
    if (!inventory.Registries.TryGetValue(HookKey(baseMethod), out List<EngineRegistryInfo>? registries))
        return new(EngineRegistration.None, EngineDispatch.None, string.Empty);

    EngineRegistration registration = EngineRegistration.None;
    bool inferred = false;
    bool strictInferred = false;
    bool applicable = false;
    List<string> unsupported = [];
    foreach (EngineRegistryInfo registry in registries)
    {
        if (!registry.ReceiverType.IsAssignableFrom(type))
            continue;
        applicable = true;
        if (registry.Registrations.TryGetValue(type, out EngineRegistration explicitRegistration))
        {
            if (explicitRegistration == EngineRegistration.Registered)
                registration = EngineRegistration.Registered;
            else if (registration == EngineRegistration.None)
                registration = EngineRegistration.Ignored;
            continue;
        }

        bool handled = false;
        if (registry.StrictInferrer != null)
        {
            try
            {
                if (registry.StrictInferrer.DynamicInvoke(type, overrideMethod) != null)
                {
                    strictInferred = true;
                    handled = true;
                }
            }
            catch (TargetInvocationException ex)
            {
                unsupported.Add($"{registry.Source}: strict inferrer threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
                continue;
            }
        }
        if (!handled && registry.Inferrer != null)
        {
            try
            {
                if (registry.Inferrer.DynamicInvoke(type, overrideMethod) != null)
                {
                    inferred = true;
                    handled = true;
                }
            }
            catch (TargetInvocationException ex)
            {
                unsupported.Add($"{registry.Source}: inferrer threw {ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
                continue;
            }
        }
        if (handled)
            continue;
        unsupported.Add(registry.Source);
    }

    if (!applicable)
        return new(EngineRegistration.None, EngineDispatch.None, string.Empty);
    if (unsupported.Count > 0)
        return new(registration, EngineDispatch.Unsupported, $"Engine unsupported registry: {string.Join(", ", unsupported)}");
    if (inferred)
        return new(registration, EngineDispatch.Inferred, "Engine best-effort method inference; requires semantic verification.");
    if (strictInferred)
        return new(registration, EngineDispatch.Exact, "Engine strict method recipe covers the complete override.");
    return new(registration, EngineDispatch.Exact, registration == EngineRegistration.Ignored
        ? "The embedded engine explicitly ignores this override as simulation-neutral."
        : string.Empty);
}

static string HookKey(MethodInfo method) =>
    $"{method.DeclaringType?.FullName}.{Signature(method)}";

static string Category(Type type, IReadOnlyList<Type> bases)
{
    string[] names = ["Card", "Power", "Relic", "Potion", "Orb", "Enchantment", "Affliction", "Monster"];
    for (int index = 0; index < bases.Count; index++)
        if (bases[index].IsAssignableFrom(type))
            return names[index];
    throw new InvalidOperationException($"Unknown combat entity category: {type.FullName}");
}

static string ScopeGuess(Type type, MethodInfo method)
{
    if (type.Namespace?.Contains(".Mocks", StringComparison.Ordinal) == true
        || type.Name.Contains("Mock", StringComparison.Ordinal)
        || type.Name.Contains("Test", StringComparison.Ordinal))
    {
        return "TestOrMock";
    }

    string name = method.Name;
    string[] outOfCombatTokens =
    [
        "Act", "Deck", "Map", "Merchant", "RestSite", "Reward", "Room", "Treasure", "Ancient", "Event",
    ];
    return outOfCombatTokens.Any(name.Contains) ? "OutOfCombat" : "Combat";
}

static string Signature(MethodInfo method)
{
    string parameters = string.Join(",", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType)));
    return $"{method.Name}({parameters})";
}

static string TypeName(Type type)
{
    if (type.IsByRef)
        return TypeName(type.GetElementType()!) + "&";
    if (!type.IsGenericType)
        return type.FullName ?? type.Name;
    string generic = type.GetGenericTypeDefinition().FullName ?? type.Name;
    generic = generic[..generic.IndexOf('`')];
    return $"{generic}<{string.Join(',', type.GetGenericArguments().Select(TypeName))}>";
}

static string BuildReport(
    CoverageCatalog catalog,
    int calculatedCardCount,
    IReadOnlyList<string> missingCalculatedCardTypes,
    IReadOnlyList<string> persistentRelicStateGaps)
{
    StringBuilder text = new();
    text.AppendLine("# CombatSolver 战斗钩子覆盖目录");
    text.AppendLine();
    text.AppendLine($"生成来源：CombatSolver `{catalog.CombatSolverVersion}`，游戏 `{catalog.GameVersion}`，模拟核心 `{catalog.SimulationEngine}`。本文件由 `tools/CoverageCatalog` 生成，不手工编辑。");
    text.AppendLine();
    text.AppendLine("## 汇总");
    text.AppendLine();
    text.AppendLine("| 分类 | 条目 | 未分析 | 待实现 | 引擎精确 | 引擎推断 | 引擎不支持 |");
    text.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
    foreach (IGrouping<string, CoverageEntry> group in catalog.Entries.GroupBy(entry => entry.Category))
    {
        text.AppendLine($"| {group.Key} | {group.Count()} | {group.Count(entry => entry.Status == CoverageStatus.Unanalyzed)} | " +
                        $"{group.Count(entry => entry.Status == CoverageStatus.PendingImplementation)} | " +
                        $"{group.Count(entry => entry.EngineDispatch == EngineDispatch.Exact)} | " +
                        $"{group.Count(entry => entry.EngineDispatch == EngineDispatch.Inferred)} | " +
                        $"{group.Count(entry => entry.EngineDispatch == EngineDispatch.Unsupported)} |");
    }
    text.AppendLine();
    text.AppendLine("## 有效支持状态");
    text.AppendLine();
    text.AppendLine("| 状态 | Hook 数 | 实机/运行时证据 | 静态证据 | 无独立证据 |");
    text.AppendLine("|---|---:|---:|---:|---:|");
    foreach (IGrouping<EffectiveSupportStatus, CoverageEntry> group in catalog.Entries.GroupBy(entry => entry.EffectiveStatus))
    {
        text.AppendLine($"| {group.Key} | {group.Count()} | " +
                        $"{group.Count(entry => entry.Verification == VerificationKind.Runtime)} | " +
                        $"{group.Count(entry => entry.Verification == VerificationKind.Static)} | " +
                        $"{group.Count(entry => entry.Verification == VerificationKind.None)} |");
    }
    text.AppendLine();
    text.AppendLine("## 主动效果运行证据");
    text.AppendLine();
    text.AppendLine("带边界或性能退化的证据状态分别保留，不计入无条件 Runtime 通过；生成目录只核对登记和历史证据，不重新执行战斗测试。");
    text.AppendLine($"带已记录边界的 Hook：{catalog.Entries.Count(entry => entry.Verification == VerificationKind.RuntimeWithDocumentedBoundaries)}；带已记录性能退化的 Hook：{catalog.Entries.Count(entry => entry.Verification == VerificationKind.RuntimeWithDocumentedPerformanceRegression)}。");
    text.AppendLine();
    text.AppendLine("只有 `EngineMirror` 与 `SolverCompensation` 的运行时差分证据才计入本节；仅完成注册或静态分类不代表跨回合时序正确。");
    text.AppendLine();
    text.AppendLine("| 分类 | 主动 Exact | 有运行证据 | 尚无运行证据 |");
    text.AppendLine("|---|---:|---:|---:|");
    foreach (IGrouping<string, CoverageEntry> group in catalog.Entries
                 .Where(entry => entry.EffectiveStatus == EffectiveSupportStatus.Exact
                     && entry.Capability is "EngineMirror" or "SolverCompensation")
                 .GroupBy(entry => entry.Category))
    {
        text.AppendLine($"| {group.Key} | {group.Count()} | " +
                        $"{group.Count(entry => entry.Verification == VerificationKind.Runtime)} | " +
                        $"{group.Count(entry => entry.Verification != VerificationKind.Runtime)} |");
    }
    text.AppendLine();
    text.AppendLine("## 分支内计算变量");
    text.AppendLine();
    text.AppendLine($"- 含 CalculatedVar 的卡牌：{calculatedCardCount}");
    text.AppendLine($"- 缺少分支内公式：{missingCalculatedCardTypes.Count}");
    foreach (string type in missingCalculatedCardTypes)
        text.AppendLine($"- `{type}`");
    text.AppendLine();
    text.AppendLine("## 持久遗物预测状态");
    text.AppendLine();
    text.AppendLine($"- 缺少统一指纹/续用描述：{persistentRelicStateGaps.Count}");
    foreach (string gap in persistentRelicStateGaps)
        text.AppendLine($"- `{gap}`");
    text.AppendLine();
    text.AppendLine("## 原生结算后自动重搜");
    text.AppendLine();
    text.AppendLine("以下行为可无人值守执行，但不会在同一条静态路线中跨过原生动态结算边界。");
    text.AppendLine();
    foreach (IGrouping<string, CoverageEntry> category in catalog.Entries
                 .Where(entry => entry.EffectiveStatus == EffectiveSupportStatus.NativeAutoRescan)
                 .GroupBy(entry => entry.Category))
    {
        text.AppendLine($"### {category.Key}");
        text.AppendLine();
        foreach (CoverageEntry entry in category)
        {
            string notes = entry.Notes.Replace("|", "\\|", StringComparison.Ordinal)
                .ReplaceLineEndings(" ");
            text.AppendLine($"- `{entry.EntityName}.{entry.Hook}` — {notes}");
        }
        text.AppendLine();
    }
    text.AppendLine("## 明确排除范围");
    text.AppendLine();
    foreach (IGrouping<string, CoverageEntry> scope in catalog.Entries
                 .Where(entry => entry.EffectiveStatus == EffectiveSupportStatus.OutOfScope)
                 .GroupBy(entry => entry.ScopeGuess)
                 .OrderBy(group => group.Key, StringComparer.Ordinal))
    {
        text.AppendLine($"- {scope.Key}：{scope.Count()} Hook");
    }
    text.AppendLine();
    text.AppendLine("## 待有效适配");
    text.AppendLine();
    foreach (CoverageEntry entry in catalog.Entries.Where(entry =>
                 entry.EffectiveStatus is EffectiveSupportStatus.NeedsReview or EffectiveSupportStatus.Unsupported))
    {
        text.AppendLine($"- `{entry.Key}` · Effective={entry.EffectiveStatus} · Capability={entry.Capability} · IL={entry.MethodHash}");
    }
    text.AppendLine();
    text.AppendLine("## 未分析条目");
    text.AppendLine();
    foreach (CoverageEntry entry in catalog.Entries.Where(entry => entry.Status == CoverageStatus.Unanalyzed))
        text.AppendLine($"- `{entry.Key}` · Engine={entry.EngineDispatch}/{entry.EngineRegistration} · 范围推断={entry.ScopeGuess}");
    return text.ToString().TrimEnd() + Environment.NewLine;
}

static JsonSerializerOptions JsonOptions() => new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
};

internal enum CoverageStatus
{
    Unanalyzed,
    EngineExact,
    EngineInferred,
    SolverCompensation,
    PendingImplementation,
    SearchPolicyExcluded,
    NativeRuntimeState,
    NotCombatRelevant,
}

internal enum EngineRegistration
{
    None,
    Registered,
    Ignored,
}

internal enum EngineDispatch
{
    None,
    Exact,
    Inferred,
    Unsupported,
}

internal sealed record CoverageClassification(
    CoverageStatus Status,
    string Source,
    string Notes,
    string? TestId)
{
    public string? Capability { get; init; }
}

internal enum EffectiveSupportStatus
{
    Exact,
    NativeAutoRescan,
    NeedsReview,
    Unsupported,
    OutOfScope,
}

internal enum VerificationKind
{
    None,
    Static,
    Runtime,
    RuntimeWithDocumentedBoundaries,
    RuntimeWithDocumentedPerformanceRegression,
}

internal enum VerificationStatus
{
    Pending,
    StaticPassed,
    Passed,
    PassedWithDocumentedBoundaries,
    PassedWithDocumentedPerformanceRegression,
    Failed,
}

internal sealed record CoverageTestEvidence(
    VerificationStatus Status,
    string Level,
    string Procedure,
    string Expected,
    string Observed,
    string Evidence);

internal sealed record CoverageEntry(
    string Key,
    string Category,
    string EntityType,
    string EntityName,
    string HookOwner,
    string Hook,
    string ScopeGuess,
    EngineRegistration EngineRegistration,
    EngineDispatch EngineDispatch,
    CoverageStatus Status,
    string Source,
    string Notes,
    string? TestId)
{
    public EffectiveSupportStatus EffectiveStatus { get; init; }
    public VerificationKind Verification { get; init; }
    public string Capability { get; init; } = string.Empty;
    public string MethodHash { get; init; } = string.Empty;
}

internal sealed record CoverageCatalog(
    int SchemaVersion,
    string CombatSolverVersion,
    string GameVersion,
    string SimulationEngine,
    IReadOnlyList<CoverageEntry> Entries);

internal sealed record SearchBoundaryCatalog(
    int SchemaVersion,
    string CombatSolverVersion,
    string GameVersion,
    IReadOnlyList<SearchBoundaryEntry> NativeAutoRescan);

internal sealed record RuntimeEvidenceGapCatalog(
    int SchemaVersion,
    string CombatSolverVersion,
    string GameVersion,
    IReadOnlyList<RuntimeEvidenceGapEntry> Entries);

internal sealed record BranchStateReadRiskCatalog(
    int SchemaVersion,
    string CombatSolverVersion,
    string GameVersion,
    IReadOnlyList<BranchStateReadRisk> Entries);

internal sealed record BranchStateReadRisk(
    string Key,
    string EntityType,
    string Hook,
    IReadOnlyList<string> Reads);

internal sealed record StateFieldCatalog(
    int SchemaVersion,
    string CombatSolverVersion,
    string GameVersion,
    int UnclassifiedCount,
    IReadOnlyList<StateFieldEntry> Entries);

internal sealed record StateFieldEntry(
    string EntityType,
    string EntityId,
    string Category,
    string FieldName,
    string DynamicVarType,
    string Role,
    string? Notes);

internal sealed record StateMutationCatalog(
    int SchemaVersion,
    string CombatSolverVersion,
    string GameVersion,
    int UnverifiedCount,
    int SnapshotOnlyWithoutRuntimeEvidenceCount,
    int StaticConfigurationWithoutRuntimeEvidenceCount,
    IReadOnlyList<StateMutationEntry> Entries);

internal sealed record StateMutationEntry(
    string Key,
    string EntityType,
    string Hook,
    string Phase,
    string EffectiveStatus,
    string Capability,
    bool RequiresRuntimeEvidence,
    bool RuntimeVerified,
    IReadOnlyList<string> Writes);

internal sealed record CombatChoiceSourceCatalog(
    int SchemaVersion,
    string CombatSolverVersion,
    string GameVersion,
    int UnresolvedCount,
    IReadOnlyList<CombatChoiceSourceEntry> Entries);

internal sealed record CombatChoiceSourceEntry(
    string Key,
    string Category,
    string EntityType,
    string Method,
    IReadOnlyList<string> SelectionMethods,
    string Classification,
    string Evidence);

internal sealed record AutoPlaySourceCatalog(
    int SchemaVersion,
    string CombatSolverVersion,
    string GameVersion,
    int UnresolvedCount,
    IReadOnlyList<AutoPlaySourceEntry> Entries);

internal sealed record AutoPlaySourceEntry(
    string Key,
    string Category,
    string EntityType,
    string Method,
    string Classification,
    string Evidence);

internal sealed record RosterSourceCatalog(
    int SchemaVersion,
    string CombatSolverVersion,
    string GameVersion,
    int UnresolvedCount,
    IReadOnlyList<RosterSourceEntry> Entries);

internal sealed record RosterSourceEntry(
    string Key,
    string Category,
    string EntityType,
    string Method,
    string Classification,
    string Evidence);

internal sealed record RuntimeEvidenceGapEntry(
    string Key,
    string Category,
    string EntityName,
    string? EntityId,
    string? TargetType,
    string Status,
    string Hook,
    string Capability,
    string MethodHash);

internal sealed record SimpleCardAuditCheck(
    string MonsterId,
    string MoveId,
    int EnemyHpBefore,
    bool ClearPlayerHandBeforeMove,
    int PlayerEnergyBefore,
    int PlayerStarsBefore,
    int? OstyHpBefore,
    IReadOnlyList<SimpleCardInjection> CardsBeforeMove,
    IReadOnlyList<SimpleCardPlayCheck> CardPlayChecksAfterMove);

internal sealed record SimpleCardInjection(
    string CardId,
    string Pile,
    IReadOnlyDictionary<string, string> EnumMembers)
{
    public int Count { get; init; } = 1;
    public int UpgradeLevels { get; init; }

    public SimpleCardInjection(string cardId, string pile)
        : this(cardId, pile, new Dictionary<string, string>(StringComparer.Ordinal))
    {
    }
}

internal sealed record StateMutationCardCheck(
    string MonsterId,
    string MoveId,
    int EnemyHpBefore,
    bool ClearPlayerHandBeforeMove,
    SimpleCardInjection CardAfterMove,
    IReadOnlyList<ModelHookDescriptor> CoveredCardHooks);

internal sealed record ModelHookDescriptor(string CardId, string Hook);

internal sealed record SimpleCardPlayCheck(string CardId, string Target);

internal sealed record SimpleMonsterMoveAuditCheck(
    string MonsterId,
    string MoveId,
    int PlayerHpBefore,
    int PlayerBlockBefore,
    int EnemyHpBefore);

internal sealed record SearchBoundaryEntry(
    string Key,
    string Category,
    string EntityName,
    string Hook,
    string Source,
    string Notes);

internal sealed record EngineMirrorInventory(
    Dictionary<string, List<EngineRegistryInfo>> Registries);

internal sealed record EngineRegistryInfo(
    Type ReceiverType,
    Dictionary<Type, EngineRegistration> Registrations,
    Delegate? StrictInferrer,
    Delegate? Inferrer,
    string Source);

internal sealed record EngineMirrorAnalysis(
    EngineRegistration Registration,
    EngineDispatch Dispatch,
    string Notes);

internal sealed record BuiltInClassification(
    CoverageStatus Status,
    string Source);
