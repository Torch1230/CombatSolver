using System.Text.Json.Nodes;
using CombatSolver.Replay;

if (args is ["self-test"])
    return ArchiveContractTests.Run();

if (args.Length < 2 || args[0] is not ("preflight" or "prepare" or "batch"))
{
    Console.Error.WriteLine("CheckpointTool preflight ARCHIVE [SELECTOR] | prepare ARCHIVE SELECTOR OUTPUT | batch INPUT [--mode RestoreOnly --output DIR --resume]");
    return 2;
}
try
{
    if (args[0] == "batch") return await BatchRunner.Run(args[1..]);
    string selector = args.Length > 2 ? args[2] : CheckpointArchive.DefaultFixtureSelector;
    JsonObject result = args[0] == "prepare"
        ? CheckpointArchive.Prepare(args[1], selector, args.Length == 4 ? args[3]
            : throw new ArgumentException("prepare requires OUTPUT"))
        : CheckpointArchive.Inspect(args[1], selector);
    Console.WriteLine(result.ToJsonString(CheckpointArchive.JsonOptions));
    return result["status"]!.GetValue<string>() == "materials_valid" ? 0 : 2;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException
    or InvalidOperationException or ArgumentException)
{
    Console.WriteLine(new JsonObject
    {
        ["status"] = "invalid_archive", ["reason"] = exception.Message,
        ["restorationVerified"] = false,
    }.ToJsonString(CheckpointArchive.JsonOptions));
    return 2;
}
