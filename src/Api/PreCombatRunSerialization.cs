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

        NormalizeRoot(root);
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    internal static void NormalizeRoot(JsonObject root)
    {

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

        // Empty event-choice variables are serialized by the live run, but the game restores them as null and
        // omits them on the next save. They carry no localization values and do not affect gameplay or RNG, so
        // normalize only the empty-object form while retaining every populated variable map verbatim.
        if (root["map_point_history"] is JsonArray actHistories)
        {
            foreach (JsonArray actHistory in actHistories.OfType<JsonArray>())
            {
                foreach (JsonObject historyEntry in actHistory.OfType<JsonObject>())
                {
                    if (historyEntry["player_stats"] is not JsonArray playerStats)
                        continue;
                    foreach (JsonObject playerStat in playerStats.OfType<JsonObject>())
                    {
                        if (playerStat["event_choices"] is not JsonArray eventChoices)
                            continue;
                        foreach (JsonObject eventChoice in eventChoices.OfType<JsonObject>())
                        {
                            if (eventChoice["variables"] is JsonObject { Count: 0 })
                                eventChoice.Remove("variables");
                        }
                    }
                }
            }
        }

    }
}
