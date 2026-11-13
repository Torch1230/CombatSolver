using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void VerifySavedRunRules(SerializableRun save, CombatState state)
    {
        if (state.RunState.GameMode != save.GameMode || state.RunState.AscensionLevel != save.Ascension
            || state.RunState.CurrentActIndex != save.CurrentActIndex)
            throw new InvalidDataException("native_replay_run_rules_mismatch");
        string? dominionId = null;
        if (save.GameMode == GameMode.Dread)
        {
            var rooms = save.Acts[save.CurrentActIndex].SerializableRooms;
            var encounter = state.Encounter ?? throw new InvalidDataException("native_replay_missing_encounter");
            bool boss = encounter.RoomType == RoomType.Boss
                && (encounter.Id == rooms.BossId || encounter.Id == rooms.SecondBossId);
            if (!boss)
            {
                ModelId bossId = rooms.SecondBossId ?? rooms.BossId
                    ?? throw new InvalidDataException("native_replay_missing_dread_boss");
                dominionId = ModelDb.GetById<EncounterModel>(bossId).GetDominion()?.Id.Entry
                    ?? throw new InvalidDataException("native_replay_missing_dread_dominion");
            }
        }
        if (state.Dominion?.Id.Entry != dominionId)
            throw new InvalidDataException("native_replay_saved_dominion_mismatch");
    }

    private static string CompareReplayRulesFromSave(JsonElement replay, string expected,
        string actual, CombatState state, string savePath)
    {
        SerializableRun save = JsonSerializer.Deserialize(File.ReadAllText(savePath),
            JsonSerializationUtility.GetTypeInfo<SerializableRun>())
            ?? throw new InvalidDataException("replay_missing_run_save");
        VerifySavedRunRules(save, state);
        if (replay.TryGetProperty("combatRules", out JsonElement recorded)
            && recorded.Deserialize<CombatRulesSnapshot>(UnattendedTestFiles.JsonOptions) != CombatRulesSnapshot.Capture(state))
            throw new InvalidDataException("replay_snapshot_combat_rules_mismatch");
        if (expected.Contains(";combat_rules=", StringComparison.Ordinal)) return actual;
        int index = actual.LastIndexOf(";combat_rules=", StringComparison.Ordinal);
        if (index < 0) throw new InvalidDataException("replay_missing_captured_rules");
        return actual[..index];
    }
}
