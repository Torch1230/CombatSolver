using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace CombatSolver;

internal enum SolverDeploymentFastMode
{
    FollowGame,
    Normal,
    Fast,
    Instant,
}

internal enum SolverSearchCompletionNotificationMode
{
    OnlyWhenGameInBackground,
    Always,
}

internal enum SolverOverlayTheme
{
    Dark,
    Light,
}

internal enum SolverPerformancePreset
{
    Low,
    Medium,
    High,
    VeryHigh,
    Custom,
}

internal enum SolverPotionPolicy
{
    Disabled,
    Smart,
    RequireAtLeastOne,
}

internal enum BossHpStrategy
{
    ProgressionFirst,
    MinimizeHpLoss,
}

internal readonly record struct PersistedPotionDirective(
    int Slot,
    string PotionId,
    SolverPotionDirective Directive);

internal sealed record SolverPerformanceValues(
    SolverSearchProfile ShortProfile,
    SolverSearchProfile DeepProfile);

internal sealed record SolverSettingsData
{
    public bool SolverDisabled { get; init; }
    public bool AutomaticCalculationEnabled { get; init; } = true;
    public bool StopFullAutoOnCombatEnd { get; init; }
    public bool StopFullAutoOnDeathTurn { get; init; } = true;
    public bool StopFullAutoOnWorseRecalculation { get; init; } = true;
    public bool EnableDetailedDiagnosticLogs { get; init; }
    public bool ShowBattleDamagePerformanceHint { get; init; } = true;
    public bool ShowActTransitionBossHpStrategyHint { get; init; } = true;
    public bool ShowFinalBossHpStrategyHint { get; init; } = true;
    public bool SearchCompletionNotificationsEnabled { get; init; } = true;
    public SolverSearchCompletionNotificationMode SearchCompletionNotificationMode { get; init; }
        = SolverSearchCompletionNotificationMode.OnlyWhenGameInBackground;
    [JsonIgnore]
    public SolverPotionPolicy PotionPolicy { get; init; } = SolverPotionPolicy.Smart;
    public PersistedPotionDirective[] PotionDirectives { get; init; } = [];
    public BossHpStrategy ActTransitionBossHpStrategy { get; init; } = BossHpStrategy.ProgressionFirst;
    public BossHpStrategy FinalBossHpStrategy { get; init; } = BossHpStrategy.ProgressionFirst;
    public int PerformanceMigrationVersion { get; init; }
    public SolverPerformancePreset? PerformancePreset { get; init; } = SolverPerformancePreset.Medium;
    public int? SearchMaxDegreeOfParallelism { get; init; }
    public double? ShortTimeLimitSeconds { get; init; }
    public double? DeepTimeLimitSeconds { get; init; }
    public bool EnableNoGcRegion { get; init; } = true;
    public double? NoGcRegionBudgetGigabytes { get; init; } = 16d;
    public int? ShortBeamWidth { get; init; }
    public int? DeepBeamWidth { get; init; }
    // Legacy split fields are read for migration; new writes use Short/DeepBeamWidth.
    public int? ShortPotionFreeBeamWidth { get; init; }
    public int? DeepPotionFreeBeamWidth { get; init; }
    public int? ShortPotionBeamWidth { get; init; }
    public int? DeepPotionBeamWidth { get; init; }
    public int? ShortMaxExpandedNodes { get; init; }
    public int? DeepMaxExpandedNodes { get; init; }
    public int? ShortMaxCardBranchesPerNode { get; init; }
    public int? DeepMaxCardBranchesPerNode { get; init; }
    public int? ShortMaxPileChoiceBranchesPerAction { get; init; }
    public int? DeepMaxPileChoiceBranchesPerAction { get; init; }
    public int? ShortMaxHandChoiceBranchesPerAction { get; init; }
    public int? DeepMaxHandChoiceBranchesPerAction { get; init; }
    public SolverDeploymentFastMode DeploymentFastMode { get; init; } = SolverDeploymentFastMode.FollowGame;
    public double? DeploymentInterActionDelaySeconds { get; init; }
    public float? OverlayPositionX { get; init; }
    public float? OverlayPositionY { get; init; }
    public float? OverlayWidth { get; init; } = 1200f;
    public float? OverlayHeight { get; init; } = 700f;
    public string? ReporterContactQq { get; init; }
    public SolverOverlayTheme OverlayTheme { get; init; } = SolverOverlayTheme.Dark;
    public float OverlayOpacity { get; init; } = 0.65f;
}

internal sealed record SolverSettingsSnapshot(
    bool SolverDisabled,
    bool StopFullAutoOnCombatEnd,
    bool StopFullAutoOnDeathTurn,
    bool StopFullAutoOnWorseRecalculation,
    bool EnableDetailedDiagnosticLogs,
    SolverPotionPolicy PotionPolicy,
    BossHpStrategy ActTransitionBossHpStrategy,
    BossHpStrategy FinalBossHpStrategy,
    int SearchMaxDegreeOfParallelism,
    SolverSearchProfile ShortProfile,
    SolverSearchProfile DeepProfile,
    bool EnableNoGcRegion,
    long NoGcRegionBudgetBytes,
    SolverDeploymentFastMode DeploymentFastMode,
    double DeploymentInterActionDelaySeconds);

internal static class SolverSettings
{
    public const double DefaultNoGcRegionBudgetGigabytes = 16d;
    public const double MaximumNoGcRegionBudgetGigabytes = 256d;
    public const float MinimumOverlayWidth = 400f;
    public const float MinimumOverlayHeight = 300f;
    public const float MaximumOverlaySize = 100_000f;
    internal const int CurrentPerformanceMigrationVersion = 243;
    private static readonly SolverPerformanceValues LowPerformance = new(
        new SolverSearchProfile(
            SolverSearchPhase.Short,
            BeamWidth: 18,
            MaxExpandedNodes: 1_200,
            MaxCardBranchesPerNode: 14,
            MaxPileChoiceBranchesPerAction: 6,
            MaxHandChoiceBranchesPerAction: 8,
            SoftTimeBudgetMilliseconds: 5_000),
        new SolverSearchProfile(
            SolverSearchPhase.Deep,
            BeamWidth: 45,
            MaxExpandedNodes: 6_000,
            MaxCardBranchesPerNode: 24,
            MaxPileChoiceBranchesPerAction: 12,
            MaxHandChoiceBranchesPerAction: 16,
            SoftTimeBudgetMilliseconds: 60_000));
    private static readonly SolverPerformanceValues MediumPerformance = new(
        SolverSearchProfile.Short,
        SolverSearchProfile.Deep);
    private static readonly SolverPerformanceValues HighPerformance = new(
        new SolverSearchProfile(
            SolverSearchPhase.Short,
            BeamWidth: 36,
            MaxExpandedNodes: 5_000,
            MaxCardBranchesPerNode: 30,
            MaxPileChoiceBranchesPerAction: 16,
            MaxHandChoiceBranchesPerAction: 20,
            SoftTimeBudgetMilliseconds: 12_000),
        new SolverSearchProfile(
            SolverSearchPhase.Deep,
            BeamWidth: 90,
            MaxExpandedNodes: 25_000,
            MaxCardBranchesPerNode: 48,
            MaxPileChoiceBranchesPerAction: 28,
            MaxHandChoiceBranchesPerAction: 36,
            SoftTimeBudgetMilliseconds: 180_000));
    private static readonly SolverPerformanceValues VeryHighPerformance = new(
        new SolverSearchProfile(
            SolverSearchPhase.Short,
            BeamWidth: 54,
            MaxExpandedNodes: 10_000,
            MaxCardBranchesPerNode: 45,
            MaxPileChoiceBranchesPerAction: 24,
            MaxHandChoiceBranchesPerAction: 30,
            SoftTimeBudgetMilliseconds: 20_000),
        new SolverSearchProfile(
            SolverSearchPhase.Deep,
            BeamWidth: 135,
            MaxExpandedNodes: 50_000,
            MaxCardBranchesPerNode: 72,
            MaxPileChoiceBranchesPerAction: 42,
            MaxHandChoiceBranchesPerAction: 54,
            SoftTimeBudgetMilliseconds: 300_000));
    private const string SettingsUri = "user://combat_solver_settings.json";
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static SolverSettingsData _current = new();

    public static SolverSettingsData Current
    {
        get
        {
            lock (Sync)
                return _current;
        }
    }

    public static void Load()
    {
        string path = ProjectSettings.GlobalizePath(SettingsUri);
        bool persisted = File.Exists(path);
        SolverSettingsData loaded = persisted
            ? JsonSerializer.Deserialize<SolverSettingsData>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("CombatSolver settings file contained null.")
            : new SolverSettingsData();
        SolverSettingsData migrated = ApplyCurrentPerformanceMigration(loaded);
        Validate(migrated);
        lock (Sync)
        {
            _current = migrated;
            if (!persisted || migrated != loaded)
                SaveLocked(migrated);
        }
        Entry.Logger.Info(
            $"[CombatSolver/Test] SETTINGS_LOADED persisted={persisted} " +
            $"automatic_calculation={migrated.AutomaticCalculationEnabled.ToString().ToLowerInvariant()} " +
            $"performance_migration={loaded.PerformanceMigrationVersion}->{migrated.PerformanceMigrationVersion} " +
            $"solver_disabled={migrated.SolverDisabled} " +
            $"stop_on_combat_end={migrated.StopFullAutoOnCombatEnd} " +
            $"stop_on_death_turn={migrated.StopFullAutoOnDeathTurn} " +
            $"stop_on_worse_recalculation={migrated.StopFullAutoOnWorseRecalculation} " +
            $"detailed_diagnostic_logs={migrated.EnableDetailedDiagnosticLogs} " +
            $"show_battle_damage_performance_hint={migrated.ShowBattleDamagePerformanceHint} " +
            $"act_transition_boss_hp_strategy={migrated.ActTransitionBossHpStrategy} " +
            $"final_boss_hp_strategy={migrated.FinalBossHpStrategy} " +
            $"search_notifications_enabled={migrated.SearchCompletionNotificationsEnabled} " +
            $"search_notification_mode={migrated.SearchCompletionNotificationMode} " +
            $"potion_policy={migrated.PotionPolicy} " +
            $"potion_directives={migrated.PotionDirectives.Length} " +
            $"performance_preset={ResolvePerformancePreset(migrated)} " +
            $"max_dop={Capture().SearchMaxDegreeOfParallelism} " +
            $"short_budget_ms={Capture().ShortProfile.SoftTimeBudgetMilliseconds} " +
            $"deep_budget_ms={Capture().DeepProfile.SoftTimeBudgetMilliseconds} " +
            $"no_gc_enabled={Capture().EnableNoGcRegion.ToString().ToLowerInvariant()} " +
            $"no_gc_budget_bytes={Capture().NoGcRegionBudgetBytes} " +
            $"deployment_fast_mode={migrated.DeploymentFastMode} " +
            $"deployment_delay_seconds={migrated.DeploymentInterActionDelaySeconds ?? 0d:0.###} " +
            $"overlay_theme={migrated.OverlayTheme} " +
            $"overlay_opacity={migrated.OverlayOpacity:0.##}");
    }

    public static SolverSettingsSnapshot Capture()
    {
        SolverSettingsData data = Current;
        SolverPerformanceValues performance = ResolvePerformanceValues(data);
        SolverSearchProfile shortProfile = performance.ShortProfile;
        SolverSearchProfile deepProfile = performance.DeepProfile;
        double noGcGigabytes = data.NoGcRegionBudgetGigabytes
            ?? DefaultNoGcRegionBudgetGigabytes;
        long noGcBytes = checked((long)Math.Round(
            noGcGigabytes * 1_000_000_000d,
            MidpointRounding.AwayFromZero));
        return new SolverSettingsSnapshot(
            data.SolverDisabled,
            data.StopFullAutoOnCombatEnd,
            data.StopFullAutoOnDeathTurn,
            data.StopFullAutoOnWorseRecalculation,
            data.EnableDetailedDiagnosticLogs,
            data.PotionPolicy,
            data.ActTransitionBossHpStrategy,
            data.FinalBossHpStrategy,
            data.SearchMaxDegreeOfParallelism
                ?? SolverWeights.DefaultSearchMaxDegreeOfParallelism,
            shortProfile,
            deepProfile,
            data.EnableNoGcRegion,
            noGcBytes,
            data.DeploymentFastMode,
            data.DeploymentInterActionDelaySeconds ?? 0d);
    }

    public static SolverPerformancePreset ResolvePerformancePreset(SolverSettingsData data)
    {
        if (data.PerformancePreset is { } configured)
            return configured;
        if (!HasExplicitPerformanceValues(data))
            return SolverPerformancePreset.Medium;

        SolverPerformanceValues legacy = BuildCustomPerformance(data);
        if (legacy == LowPerformance)
            return SolverPerformancePreset.Low;
        if (legacy == MediumPerformance)
            return SolverPerformancePreset.Medium;
        if (legacy == HighPerformance)
            return SolverPerformancePreset.High;
        if (legacy == VeryHighPerformance)
            return SolverPerformancePreset.VeryHigh;
        return SolverPerformancePreset.Custom;
    }

    public static SolverPerformanceValues ResolvePerformanceValues(SolverSettingsData data)
        => ResolvePerformancePreset(data) switch
        {
            SolverPerformancePreset.Low => LowPerformance,
            SolverPerformancePreset.Medium => MediumPerformance,
            SolverPerformancePreset.High => HighPerformance,
            SolverPerformancePreset.VeryHigh => VeryHighPerformance,
            SolverPerformancePreset.Custom => BuildCustomPerformance(data),
            _ => throw new ArgumentOutOfRangeException(nameof(data.PerformancePreset)),
        };

    public static SolverSettingsData ApplyPerformancePreset(
        SolverSettingsData data,
        SolverPerformancePreset preset)
    {
        SolverPerformanceValues values = preset == SolverPerformancePreset.Custom
            ? ResolvePerformanceValues(data)
            : preset switch
            {
                SolverPerformancePreset.Low => LowPerformance,
                SolverPerformancePreset.Medium => MediumPerformance,
                SolverPerformancePreset.High => HighPerformance,
                SolverPerformancePreset.VeryHigh => VeryHighPerformance,
                _ => throw new ArgumentOutOfRangeException(nameof(preset)),
            };
        return data with
        {
            PerformancePreset = preset,
            ShortTimeLimitSeconds = values.ShortProfile.SoftTimeBudgetMilliseconds / 1000d,
            DeepTimeLimitSeconds = values.DeepProfile.SoftTimeBudgetMilliseconds / 1000d,
            ShortBeamWidth = values.ShortProfile.BeamWidth,
            DeepBeamWidth = values.DeepProfile.BeamWidth,
            ShortPotionFreeBeamWidth = null,
            DeepPotionFreeBeamWidth = null,
            ShortPotionBeamWidth = null,
            DeepPotionBeamWidth = null,
            ShortMaxExpandedNodes = values.ShortProfile.MaxExpandedNodes,
            DeepMaxExpandedNodes = values.DeepProfile.MaxExpandedNodes,
            ShortMaxCardBranchesPerNode = values.ShortProfile.MaxCardBranchesPerNode,
            DeepMaxCardBranchesPerNode = values.DeepProfile.MaxCardBranchesPerNode,
            ShortMaxPileChoiceBranchesPerAction = values.ShortProfile.MaxPileChoiceBranchesPerAction,
            DeepMaxPileChoiceBranchesPerAction = values.DeepProfile.MaxPileChoiceBranchesPerAction,
            ShortMaxHandChoiceBranchesPerAction = values.ShortProfile.MaxHandChoiceBranchesPerAction,
            DeepMaxHandChoiceBranchesPerAction = values.DeepProfile.MaxHandChoiceBranchesPerAction,
        };
    }

    public static void Update(SolverSettingsData data)
    {
        Validate(data);
        lock (Sync)
        {
            _current = data;
            SaveLocked(data);
        }
    }

    public static SolverPotionDirective ResolvePotionDirective(int slot, string potionId)
    {
        foreach (PersistedPotionDirective directive in Current.PotionDirectives)
        {
            if (directive.Slot == slot
                && string.Equals(directive.PotionId, potionId, StringComparison.Ordinal))
            {
                return directive.Directive;
            }
        }
        return SolverPotionDirective.Smart;
    }

    public static SolverSettingsData ApplyPotionDirective(
        SolverSettingsData data,
        int slot,
        string potionId,
        SolverPotionDirective directive)
    {
        if (slot < 0)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (string.IsNullOrWhiteSpace(potionId))
            throw new ArgumentException("Potion ID must not be empty.", nameof(potionId));
        if (!Enum.IsDefined(directive))
            throw new ArgumentOutOfRangeException(nameof(directive));

        List<PersistedPotionDirective> directives = data.PotionDirectives
            .Where(item => item.Slot != slot)
            .ToList();
        if (directive != SolverPotionDirective.Smart)
            directives.Add(new PersistedPotionDirective(slot, potionId, directive));
        return data with
        {
            PotionDirectives = directives
                .OrderBy(item => item.Slot)
                .ThenBy(item => item.PotionId, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    internal static void ApplyForTesting(SolverSettingsData data)
    {
        Validate(data);
        lock (Sync)
            _current = data;
    }

    internal static SolverSettingsData RoundTripForTesting(SolverSettingsData data)
        => JsonSerializer.Deserialize<SolverSettingsData>(
               JsonSerializer.Serialize(data, JsonOptions),
               JsonOptions)
           ?? throw new InvalidDataException("CombatSolver settings round-trip returned null.");

    internal static SolverSettingsData DeserializeForTesting(string json)
        => JsonSerializer.Deserialize<SolverSettingsData>(json, JsonOptions)
           ?? throw new InvalidDataException("CombatSolver settings test JSON returned null.");

    public static void ResetToDefaults() => Update(CreateCurrentDefaults());

    internal static SolverSettingsData ApplyCurrentPerformanceMigrationForTesting(
        SolverSettingsData data)
        => ApplyCurrentPerformanceMigration(data);

    public static Vector2? OverlayPosition
    {
        get
        {
            SolverSettingsData data = Current;
            return data.OverlayPositionX is { } x && data.OverlayPositionY is { } y
                ? new Vector2(x, y)
                : null;
        }
    }

    public static void SetOverlayPosition(Vector2 position)
        => Update(Current with
        {
            OverlayPositionX = position.X,
            OverlayPositionY = position.Y,
        });

    public static Vector2? OverlaySize
    {
        get
        {
            SolverSettingsData data = Current;
            return data.OverlayWidth is { } width && data.OverlayHeight is { } height
                ? new Vector2(width, height)
                : null;
        }
    }

    public static void SetOverlayBounds(Vector2 position, Vector2 size)
        => Update(Current with
        {
            OverlayPositionX = position.X,
            OverlayPositionY = position.Y,
            OverlayWidth = size.X,
            OverlayHeight = size.Y,
        });

    public static string FormatSeconds(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void SaveLocked(SolverSettingsData data)
    {
        string path = ProjectSettings.GlobalizePath(SettingsUri);
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("CombatSolver settings path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporary, path, true);
        Entry.Logger.Info("[CombatSolver/Test] SETTINGS_SAVED");
    }

    private static void Validate(SolverSettingsData data)
    {
        if (data.PerformanceMigrationVersion < 0)
            throw new InvalidDataException("PerformanceMigrationVersion must be non-negative.");
        ValidateRange(data.ShortTimeLimitSeconds, 0.1d, 600d, nameof(data.ShortTimeLimitSeconds));
        ValidateRange(data.DeepTimeLimitSeconds, 0.1d, 600d, nameof(data.DeepTimeLimitSeconds));
        ValidateRange(
            data.NoGcRegionBudgetGigabytes,
            1d,
            MaximumNoGcRegionBudgetGigabytes,
            nameof(data.NoGcRegionBudgetGigabytes));
        ValidateRange(
            data.SearchMaxDegreeOfParallelism,
            1,
            SolverWeights.MaximumSearchMaxDegreeOfParallelism,
            nameof(data.SearchMaxDegreeOfParallelism));
        ValidateRange(data.ShortBeamWidth, 1, 512, nameof(data.ShortBeamWidth));
        ValidateRange(data.DeepBeamWidth, 1, 512, nameof(data.DeepBeamWidth));
        ValidateRange(data.ShortPotionFreeBeamWidth, 1, 256, nameof(data.ShortPotionFreeBeamWidth));
        ValidateRange(data.DeepPotionFreeBeamWidth, 1, 256, nameof(data.DeepPotionFreeBeamWidth));
        ValidateRange(data.ShortPotionBeamWidth, 1, 256, nameof(data.ShortPotionBeamWidth));
        ValidateRange(data.DeepPotionBeamWidth, 1, 256, nameof(data.DeepPotionBeamWidth));
        ValidateRange(data.ShortMaxExpandedNodes, 100, 100_000, nameof(data.ShortMaxExpandedNodes));
        ValidateRange(data.DeepMaxExpandedNodes, 100, 100_000, nameof(data.DeepMaxExpandedNodes));
        ValidateRange(data.ShortMaxCardBranchesPerNode, 1, 100, nameof(data.ShortMaxCardBranchesPerNode));
        ValidateRange(data.DeepMaxCardBranchesPerNode, 1, 100, nameof(data.DeepMaxCardBranchesPerNode));
        ValidateRange(data.ShortMaxPileChoiceBranchesPerAction, 1, 100,
            nameof(data.ShortMaxPileChoiceBranchesPerAction));
        ValidateRange(data.DeepMaxPileChoiceBranchesPerAction, 1, 100,
            nameof(data.DeepMaxPileChoiceBranchesPerAction));
        ValidateRange(data.ShortMaxHandChoiceBranchesPerAction, 1, 100,
            nameof(data.ShortMaxHandChoiceBranchesPerAction));
        ValidateRange(data.DeepMaxHandChoiceBranchesPerAction, 1, 100,
            nameof(data.DeepMaxHandChoiceBranchesPerAction));
        if (!Enum.IsDefined(data.DeploymentFastMode))
            throw new InvalidDataException($"Unknown deployment fast mode {data.DeploymentFastMode}.");
        if (!Enum.IsDefined(data.SearchCompletionNotificationMode))
        {
            throw new InvalidDataException(
                $"Unknown search completion notification mode {data.SearchCompletionNotificationMode}.");
        }
        if (!Enum.IsDefined(data.PotionPolicy))
            throw new InvalidDataException($"Unknown potion policy {data.PotionPolicy}.");
        if (!Enum.IsDefined(data.ActTransitionBossHpStrategy))
        {
            throw new InvalidDataException(
                $"Unknown act transition boss HP strategy {data.ActTransitionBossHpStrategy}.");
        }
        if (!Enum.IsDefined(data.FinalBossHpStrategy))
            throw new InvalidDataException($"Unknown final boss HP strategy {data.FinalBossHpStrategy}.");
        HashSet<(int Slot, string PotionId)> potionDirectiveKeys = [];
        foreach (PersistedPotionDirective directive in data.PotionDirectives)
        {
            if (directive.Slot < 0)
                throw new InvalidDataException("Potion directive slot must be non-negative.");
            if (string.IsNullOrWhiteSpace(directive.PotionId))
                throw new InvalidDataException("Potion directive ID must not be empty.");
            if (!Enum.IsDefined(directive.Directive))
                throw new InvalidDataException($"Unknown potion directive {directive.Directive}.");
            if (!potionDirectiveKeys.Add((directive.Slot, directive.PotionId)))
            {
                throw new InvalidDataException(
                    $"Duplicate potion directive {directive.PotionId}@{directive.Slot}.");
            }
        }
        ValidateRange(data.DeploymentInterActionDelaySeconds, 0d, 3d,
            nameof(data.DeploymentInterActionDelaySeconds));
        if (data.PerformancePreset is { } performancePreset && !Enum.IsDefined(performancePreset))
            throw new InvalidDataException($"Unknown performance preset {performancePreset}.");
        if (data.OverlayPositionX.HasValue != data.OverlayPositionY.HasValue)
            throw new InvalidDataException("OverlayPositionX and OverlayPositionY must both be set or both be null.");
        ValidateRange(data.OverlayPositionX, -100_000f, 100_000f, nameof(data.OverlayPositionX));
        ValidateRange(data.OverlayPositionY, -100_000f, 100_000f, nameof(data.OverlayPositionY));
        if (data.OverlayWidth.HasValue != data.OverlayHeight.HasValue)
            throw new InvalidDataException("OverlayWidth and OverlayHeight must both be set or both be null.");
        ValidateRange(
            data.OverlayWidth,
            MinimumOverlayWidth,
            MaximumOverlaySize,
            nameof(data.OverlayWidth));
        ValidateRange(
            data.OverlayHeight,
            MinimumOverlayHeight,
            MaximumOverlaySize,
            nameof(data.OverlayHeight));
        if (!Enum.IsDefined(data.OverlayTheme))
            throw new InvalidDataException($"Unknown overlay theme {data.OverlayTheme}.");
        ValidateRange(data.OverlayOpacity, 0.25f, 1f, nameof(data.OverlayOpacity));
        if (data.ReporterContactQq is { Length: > 64 })
            throw new InvalidDataException($"{nameof(data.ReporterContactQq)} must be at most 64 characters.");
    }

    private static void ValidateRange(double? value, double minimum, double maximum, string name)
    {
        if (value is { } actual && (actual < minimum || actual > maximum || double.IsNaN(actual)))
            throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
    }

    private static void ValidateRange(int? value, int minimum, int maximum, string name)
    {
        if (value is { } actual && (actual < minimum || actual > maximum))
            throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
    }

    private static void ValidateRange(float? value, float minimum, float maximum, string name)
    {
        if (value is { } actual && (actual < minimum || actual > maximum || float.IsNaN(actual)))
            throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
    }

    private static int ResolveBeamWidth(
        int? unified,
        int? legacyPotionFree,
        int? legacyPotion,
        int currentDefault,
        int legacyPotionFreeDefault,
        int legacyPotionDefault)
    {
        if (unified is { } configured)
            return configured;
        if (!legacyPotionFree.HasValue && !legacyPotion.HasValue)
            return currentDefault;
        return checked(
            (legacyPotionFree ?? legacyPotionFreeDefault)
            + (legacyPotion ?? legacyPotionDefault));
    }

    private static SolverSettingsData CreateCurrentDefaults()
        => ApplyCurrentPerformanceMigration(new SolverSettingsData());

    private static SolverSettingsData ApplyCurrentPerformanceMigration(SolverSettingsData data)
    {
        if (data.PerformanceMigrationVersion >= CurrentPerformanceMigrationVersion)
            return data;

        return ApplyPerformancePreset(
            data with
            {
                PerformanceMigrationVersion = CurrentPerformanceMigrationVersion,
                NoGcRegionBudgetGigabytes = DefaultNoGcRegionBudgetGigabytes,
            },
            SolverPerformancePreset.Medium);
    }

    private static SolverPerformanceValues BuildCustomPerformance(SolverSettingsData data)
    {
        SolverSearchProfile shortProfile = MediumPerformance.ShortProfile with
        {
            BeamWidth = ResolveBeamWidth(
                data.ShortBeamWidth,
                data.ShortPotionFreeBeamWidth,
                data.ShortPotionBeamWidth,
                MediumPerformance.ShortProfile.BeamWidth,
                legacyPotionFreeDefault: 9,
                legacyPotionDefault: 3),
            MaxExpandedNodes = data.ShortMaxExpandedNodes ?? MediumPerformance.ShortProfile.MaxExpandedNodes,
            MaxCardBranchesPerNode = data.ShortMaxCardBranchesPerNode
                ?? MediumPerformance.ShortProfile.MaxCardBranchesPerNode,
            MaxPileChoiceBranchesPerAction = data.ShortMaxPileChoiceBranchesPerAction
                ?? MediumPerformance.ShortProfile.MaxPileChoiceBranchesPerAction,
            MaxHandChoiceBranchesPerAction = data.ShortMaxHandChoiceBranchesPerAction
                ?? MediumPerformance.ShortProfile.MaxHandChoiceBranchesPerAction,
            SoftTimeBudgetMilliseconds = data.ShortTimeLimitSeconds is { } shortSeconds
                ? checked((int)Math.Round(shortSeconds * 1000d, MidpointRounding.AwayFromZero))
                : MediumPerformance.ShortProfile.SoftTimeBudgetMilliseconds,
        };
        SolverSearchProfile deepProfile = MediumPerformance.DeepProfile with
        {
            BeamWidth = ResolveBeamWidth(
                data.DeepBeamWidth,
                data.DeepPotionFreeBeamWidth,
                data.DeepPotionBeamWidth,
                MediumPerformance.DeepProfile.BeamWidth,
                legacyPotionFreeDefault: 22,
                legacyPotionDefault: 7),
            MaxExpandedNodes = data.DeepMaxExpandedNodes ?? MediumPerformance.DeepProfile.MaxExpandedNodes,
            MaxCardBranchesPerNode = data.DeepMaxCardBranchesPerNode
                ?? MediumPerformance.DeepProfile.MaxCardBranchesPerNode,
            MaxPileChoiceBranchesPerAction = data.DeepMaxPileChoiceBranchesPerAction
                ?? MediumPerformance.DeepProfile.MaxPileChoiceBranchesPerAction,
            MaxHandChoiceBranchesPerAction = data.DeepMaxHandChoiceBranchesPerAction
                ?? MediumPerformance.DeepProfile.MaxHandChoiceBranchesPerAction,
            SoftTimeBudgetMilliseconds = data.DeepTimeLimitSeconds is { } deepSeconds
                ? checked((int)Math.Round(deepSeconds * 1000d, MidpointRounding.AwayFromZero))
                : MediumPerformance.DeepProfile.SoftTimeBudgetMilliseconds,
        };
        return new SolverPerformanceValues(shortProfile, deepProfile);
    }

    private static bool HasExplicitPerformanceValues(SolverSettingsData data)
        => data.ShortTimeLimitSeconds.HasValue
            || data.DeepTimeLimitSeconds.HasValue
            || data.ShortBeamWidth.HasValue
            || data.DeepBeamWidth.HasValue
            || data.ShortPotionFreeBeamWidth.HasValue
            || data.DeepPotionFreeBeamWidth.HasValue
            || data.ShortPotionBeamWidth.HasValue
            || data.DeepPotionBeamWidth.HasValue
            || data.ShortMaxExpandedNodes.HasValue
            || data.DeepMaxExpandedNodes.HasValue
            || data.ShortMaxCardBranchesPerNode.HasValue
            || data.DeepMaxCardBranchesPerNode.HasValue
            || data.ShortMaxPileChoiceBranchesPerAction.HasValue
            || data.DeepMaxPileChoiceBranchesPerAction.HasValue
            || data.ShortMaxHandChoiceBranchesPerAction.HasValue
            || data.DeepMaxHandChoiceBranchesPerAction.HasValue;
}
