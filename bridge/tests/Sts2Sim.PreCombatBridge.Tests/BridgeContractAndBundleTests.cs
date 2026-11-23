using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CombatSolver.Beam;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Runs;

namespace Sts2Sim.PreCombatBridge.Tests;

public sealed class BridgeContractAndBundleTests
{
    [Fact]
    public void PublicSurface_ExposesZeroCoreOrSolverTypes_AndExactEntryMethods()
    {
        Assembly bridgeAsm = typeof(BridgeEntry).Assembly;
        Type[] publicTypes = bridgeAsm.GetExportedTypes();

        Assert.All(publicTypes, t => Assert.Equal("Sts2Sim.PreCombatBridge", t.Namespace));
        Assert.Equal(new[] { nameof(BridgeEntry), nameof(BundleManifestWriter) },
            publicTypes.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());

        MethodInfo[] entryMethods = typeof(BridgeEntry)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(BridgeEntry.GetAbiVersion),
                nameof(BridgeEntry.GetBundleIdentityJson),
                nameof(BridgeEntry.GetInCombatAbiVersion),
                nameof(BridgeEntry.Prewarm),
                nameof(BridgeEntry.RunInCombatJsonAsync),
                nameof(BridgeEntry.RunJsonAsync),
            },
            entryMethods.Select(m => m.Name).ToArray());

        foreach (Type exportedType in publicTypes)
        {
            AssertNoForbiddenAssemblyTypes(exportedType.BaseType);
            foreach (MemberInfo member in exportedType.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member is MethodBase method)
                {
                    if (method is MethodInfo mi) AssertNoForbiddenAssemblyTypes(mi.ReturnType);
                    foreach (ParameterInfo p in method.GetParameters())
                        AssertNoForbiddenAssemblyTypes(p.ParameterType);
                }
                else if (member is PropertyInfo prop)
                {
                    AssertNoForbiddenAssemblyTypes(prop.PropertyType);
                }
                else if (member is FieldInfo field)
                {
                    AssertNoForbiddenAssemblyTypes(field.FieldType);
                }
            }
        }
    }

    [Fact]
    public void IdentityAndAbiVersion_MatchLiveModuleVersionIds()
    {
        Assert.Equal(1, BridgeEntry.GetAbiVersion());
        Assert.Equal(1, BridgeEntry.GetInCombatAbiVersion());
        using JsonDocument doc = JsonDocument.Parse(BridgeEntry.GetBundleIdentityJson());
        JsonElement root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("abiVersion").GetInt32());
        Assert.Equal("v0.113.1", root.GetProperty("supportedGameVersion").GetString());
        Assert.Equal("d09d48e5", root.GetProperty("supportedGameCommit").GetString());
        Assert.Equal(typeof(BridgeEntry).Module.ModuleVersionId.ToString("D"), root.GetProperty("bridgeMvid").GetString());
        Assert.Equal(typeof(BeamSearch).Module.ModuleVersionId.ToString("D"), root.GetProperty("solverMvid").GetString());
        Assert.Equal(typeof(CombatState).Module.ModuleVersionId.ToString("D"), root.GetProperty("coreMvid").GetString());
    }

    [Fact]
    public void BundleManifestWriter_WritesSha256AndMvids_IncludingOptionalHashingWhenPresent()
    {
        string sandboxDir = Path.Combine(AppContext.BaseDirectory, "bundle-manifest-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandboxDir);
        try
        {
            string testBinDir = Path.GetDirectoryName(typeof(BridgeEntry).Assembly.Location)!;
            string config = new DirectoryInfo(testBinDir).Parent!.Name;
            string repoRoot = FindRepoRoot();
            string bridgeBinDir = Path.Combine(repoRoot, "bridge", "Sts2Sim.PreCombatBridge", "bin", config, "net9.0");
            Assert.True(Directory.Exists(bridgeBinDir), $"Expected bridge output at {bridgeBinDir}");

            foreach (string required in new[]
            {
                "Sts2Sim.PreCombatBridge.dll",
                "Sts2Sim.Solver.dll",
                "Sts2Sim.Core.dll",
                "Sts2Sim.PreCombatBridge.deps.json",
            })
            {
                string src = File.Exists(Path.Combine(bridgeBinDir, required))
                    ? Path.Combine(bridgeBinDir, required)
                    : Path.Combine(testBinDir, required);
                File.Copy(src, Path.Combine(sandboxDir, required), overwrite: true);
            }

            string? sourceHashing = File.Exists(Path.Combine(bridgeBinDir, "System.IO.Hashing.dll"))
                ? Path.Combine(bridgeBinDir, "System.IO.Hashing.dll")
                : Directory.EnumerateFiles(testBinDir, "System.IO.Hashing.dll").FirstOrDefault();
            if (sourceHashing is not null)
                File.Copy(sourceHashing, Path.Combine(sandboxDir, "System.IO.Hashing.dll"), overwrite: true);

            string manifestPath = BundleManifestWriter.WriteBundleManifest(sandboxDir);
            Assert.True(File.Exists(manifestPath));

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("abiVersion").GetInt32());
            Assert.Equal("v0.113.1", root.GetProperty("supportedGameVersion").GetString());
            Assert.Equal("d09d48e5", root.GetProperty("supportedGameCommit").GetString());

            Assert.Matches("^[0-9a-f]{40}$", root.GetProperty("simulatorCommit").GetString());

            var filesByName = root.GetProperty("files")
                .EnumerateArray()
                .ToDictionary(el => el.GetProperty("name").GetString()!, StringComparer.Ordinal);

            Assert.True(filesByName.ContainsKey("Sts2Sim.PreCombatBridge.dll"));
            Assert.True(filesByName.ContainsKey("Sts2Sim.Solver.dll"));
            Assert.True(filesByName.ContainsKey("Sts2Sim.Core.dll"));
            Assert.True(filesByName.ContainsKey("Sts2Sim.PreCombatBridge.deps.json"));
            Assert.Equal(sourceHashing is not null, filesByName.ContainsKey("System.IO.Hashing.dll"));

            foreach ((string name, JsonElement entry) in filesByName)
            {
                string fullPath = Path.Combine(sandboxDir, name);
                string actualSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)));
                Assert.Equal(actualSha, entry.GetProperty("sha256").GetString());

                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    using FileStream fs = File.OpenRead(fullPath);
                    using var pe = new PEReader(fs);
                    MetadataReader md = pe.GetMetadataReader();
                    string actualMvid = md.GetGuid(md.GetModuleDefinition().Mvid).ToString("D");
                    Assert.Equal(actualMvid, entry.GetProperty("mvid").GetString());
                }
                else
                {
                    Assert.Equal(JsonValueKind.Null, entry.GetProperty("mvid").ValueKind);
                }
            }

            File.Delete(Path.Combine(sandboxDir, "Sts2Sim.Core.dll"));
            Assert.Throws<FileNotFoundException>(() => BundleManifestWriter.WriteBundleManifest(sandboxDir));
        }
        finally
        {
            if (Directory.Exists(sandboxDir))
                Directory.Delete(sandboxDir, recursive: true);
        }
    }

    [Fact]
    public void SharedAbiV1Fixtures_MatchBridgeWireContractsAndNullability()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "abi-v1-fixtures.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fixturePath));
        JsonElement root = doc.RootElement;

        string[] fixtureCodes = root.GetProperty("allowlistedFailureCodes")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        Assert.Equal(BridgeAbi.AllowlistedFailureCodes.Order(StringComparer.Ordinal),
            fixtureCodes.Order(StringComparer.Ordinal));

        BridgeRequestDto? sampleRequest = JsonSerializer.Deserialize<BridgeRequestDto>(
            root.GetProperty("sampleRequest").GetRawText(), BridgeAbi.JsonOptions);
        Assert.NotNull(sampleRequest);
        Assert.Equal(1, sampleRequest!.AbiVersion);
        Assert.Equal(0UL, sampleRequest.SimulationSeed);

        JsonElement responses = root.GetProperty("responses");
        foreach (string key in new[] { "succeededVictory", "succeededParallelVictory", "succeededDeathOnly", "succeededBounded", "succeededMixedPoolForcedPotionLoss", "capabilityMiss", "cancelled", "timedOut", "failed" })
        {
            JsonElement elem = responses.GetProperty(key);
            BridgeResponseDto? dto = JsonSerializer.Deserialize<BridgeResponseDto>(elem.GetRawText(), BridgeAbi.JsonOptions);
            Assert.NotNull(dto);
            byte[] reserialized = JsonSerializer.SerializeToUtf8Bytes(dto, BridgeAbi.JsonOptions);
            using JsonDocument roundTrip = JsonDocument.Parse(reserialized);
            Assert.Equal(elem.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
                roundTrip.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

            if (dto!.Outcome == "Succeeded")
            {
                Assert.NotNull(dto.ProjectedHpLoss);
                Assert.NotNull(dto.FinalHp);
                Assert.NotNull(dto.OnlyDeathRoutes);
                Assert.NotNull(dto.Confidence);
                Assert.NotNull(dto.SearchBoundary);
                Assert.NotNull(dto.PotionUses);
                Assert.NotNull(dto.ExpandedNodes);
                Assert.True(dto.EffectiveDegreeOfParallelism is >= 1 and <= 64);
                if (key == "succeededParallelVictory")
                    Assert.Equal(4, dto.EffectiveDegreeOfParallelism);
                Assert.NotNull(dto.SearchElapsedMilliseconds);
                Assert.Null(dto.FailureCode);
                if (dto.OnlyDeathRoutes == true) Assert.Equal("DeathOnly", dto.Confidence);
                else if (dto.CombatEndedTurn.HasValue) Assert.Equal("Complete", dto.Confidence);
                else Assert.Equal("Bounded", dto.Confidence);
            }
            else
            {
                Assert.Null(dto.ProjectedHpLoss);
                Assert.Null(dto.FinalHp);
                Assert.Null(dto.CombatEndedTurn);
                Assert.Null(dto.OnlyDeathRoutes);
                Assert.Null(dto.Confidence);
                Assert.Null(dto.SearchBoundary);
                Assert.Null(dto.PotionUses);
                Assert.Null(dto.ExpandedNodes);
                Assert.Null(dto.SearchElapsedMilliseconds);
                if (dto.Outcome == "CapabilityMiss")
                {
                    Assert.NotNull(dto.FailureCode);
                    Assert.Contains(dto.FailureCode!, (IReadOnlySet<string>)BridgeAbi.AllowlistedFailureCodes);
                }
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => BridgeResponseDto.CapabilityMiss("unregistered_code", "field", "err", 1.0));
    }

    [Fact]
    public async Task SharedAbiV1Fixtures_InvalidSavePayloadsReturnFailed_AndInvalidResponsesHavePopulatedMetrics()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "abi-v1-fixtures.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fixturePath));
        JsonElement root = doc.RootElement;

        BridgeRequestDto sampleRequest = JsonSerializer.Deserialize<BridgeRequestDto>(
            root.GetProperty("sampleRequest").GetRawText(), BridgeAbi.JsonOptions)!;

        foreach (JsonProperty entry in root.GetProperty("invalidSavePayloads").EnumerateObject())
        {
            string badSaveJson = entry.Value.GetString()!;
            BridgeRequestDto badRequest = sampleRequest with
            {
                RequestId = $"fixture-invalid-{entry.Name}",
                NormalizedSaveJson = badSaveJson,
            };
            byte[] responseBytes = await BridgeEntry.RunJsonAsync(
                JsonSerializer.SerializeToUtf8Bytes(badRequest, BridgeAbi.JsonOptions),
                CancellationToken.None);
            BridgeResponseDto? response = JsonSerializer.Deserialize<BridgeResponseDto>(responseBytes, BridgeAbi.JsonOptions);
            Assert.NotNull(response);
            Assert.Equal("Failed", response!.Outcome);
        }

        foreach (JsonProperty entry in root.GetProperty("invalidResponses").EnumerateObject())
        {
            BridgeResponseDto? invalidDto = JsonSerializer.Deserialize<BridgeResponseDto>(
                entry.Value.GetRawText(), BridgeAbi.JsonOptions);
            Assert.NotNull(invalidDto);
            bool hasInvalidMissMetrics = invalidDto!.Outcome == "CapabilityMiss"
                && (invalidDto.ExpandedNodes is not null || invalidDto.SearchElapsedMilliseconds is not null);
            bool hasImpossibleVictory = invalidDto.Outcome == "Succeeded"
                && invalidDto.CombatEndedTurn is not null
                && (invalidDto.OnlyDeathRoutes == true || invalidDto.FinalHp < 1 || invalidDto.SearchBoundary != "None");
            bool hasInvalidBoundedBoundary = invalidDto.Outcome == "Succeeded"
                && invalidDto.OnlyDeathRoutes == false
                && invalidDto.CombatEndedTurn is null
                && invalidDto.FinalHp > 0
                && invalidDto.SearchBoundary == "None";
            bool hasMissingPotionField = invalidDto.Outcome == "Succeeded"
                && entry.Value.TryGetProperty("potionUses", out JsonElement potionArr)
                && potionArr.ValueKind == JsonValueKind.Array
                && potionArr.EnumerateArray().Any(item =>
                    !item.TryGetProperty("slot", out _) || !item.TryGetProperty("turn", out _));
            bool hasInvalidParallelism = invalidDto.EffectiveDegreeOfParallelism is not (>= 1 and <= 64);
            Assert.True(
                hasInvalidMissMetrics || hasImpossibleVictory || hasInvalidBoundedBoundary || hasMissingPotionField || hasInvalidParallelism,
                $"Invalid response fixture '{entry.Name}' did not match an expected contract violation.");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("null")]
    public async Task MalformedOrEmptyRequest_ReturnsFailed(string rawJson)
    {
        byte[] responseBytes = await BridgeEntry.RunJsonAsync(Encoding.UTF8.GetBytes(rawJson), CancellationToken.None);
        BridgeResponseDto? dto = JsonSerializer.Deserialize<BridgeResponseDto>(responseBytes, BridgeAbi.JsonOptions);
        Assert.NotNull(dto);
        Assert.Equal("Failed", dto!.Outcome);
        Assert.Equal(1, dto.EffectiveDegreeOfParallelism);
        Assert.Null(dto.ProjectedHpLoss);
        Assert.False(string.IsNullOrWhiteSpace(dto.Error));
    }

    [Fact]
    public async Task WrongAbiVersion_ReturnsFailed()
    {
        RunState run = TestSaveBuilder.CreateRun();
        MapPoint target = TestSaveBuilder.FindFirstUnvisitedPoint(run, MapPointType.Monster);
        BridgeRequestDto req = TestSaveBuilder.CreateRequest(run, "NIBBITS_WEAK", target.coord) with { AbiVersion = 2 };
        (BridgeResponseDto dto, _) = await TestSaveBuilder.RunAsync(req);
        Assert.Equal("Failed", dto.Outcome);
        Assert.Equal("abiVersion", dto.FieldPath);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CombatSolver.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate CombatSolver.csproj above test directory.");
    }

    private static void AssertNoForbiddenAssemblyTypes(Type? type)
    {
        if (type is null) return;
        string ns = type.Namespace ?? string.Empty;
        Assert.False(ns.StartsWith("Sts2Sim.Core", StringComparison.Ordinal) ||
                     ns.StartsWith("CombatSolver", StringComparison.Ordinal),
            $"Forbidden type leaked on public bridge API: {type.FullName}");
        if (type.IsGenericType)
        {
            foreach (Type arg in type.GetGenericArguments())
                AssertNoForbiddenAssemblyTypes(arg);
        }
        if (type.HasElementType)
            AssertNoForbiddenAssemblyTypes(type.GetElementType());
    }
}
