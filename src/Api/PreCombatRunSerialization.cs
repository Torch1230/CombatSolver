using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Saves;

namespace CombatSolver.Api;

internal static class PreCombatRunSerialization
{
    public static byte[] SerializeNormalized(SerializableRun save)
    {
        save.SaveTime = 0;
        save.StartTime = 0;
        save.RunTime = 0;
        save.WinTime = 0;
        save.NumReloads = 0;
        save.PlatformType = default;
        save.MapDrawings = null;
        save.PreFinishedRoom = null;

        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(
            save,
            JsonSerializationUtility.GetTypeInfo<SerializableRun>());
        JsonObject root = JsonNode.Parse(serialized)?.AsObject()
            ?? throw new InvalidDataException("The normalized run snapshot is empty.");

        // SerializableMapPoint omits false CanBeModified values but initializes an absent value to true.
        // Materialize the omitted false value so this private worker snapshot round-trips exactly.
        if (root["acts"] is JsonArray acts)
        {
            foreach (JsonObject act in acts.OfType<JsonObject>())
            {
                if (act["saved_map"]?["points"] is not JsonArray points)
                    continue;
                foreach (JsonObject point in points.OfType<JsonObject>())
                {
                    if (!point.ContainsKey("can_modify"))
                        point["can_modify"] = false;
                }
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(root);
    }
}
