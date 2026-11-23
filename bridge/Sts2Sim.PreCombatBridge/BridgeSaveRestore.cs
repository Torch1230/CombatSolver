using Sts2Sim.Core.Map;
using Sts2Sim.Core.Runs;

namespace Sts2Sim.PreCombatBridge;

internal static class BridgeSaveRestore
{
    public static RunState Restore(BridgeRequestDto request)
    {
        BridgeSpoilsMapVerificationDto? spoils = request.SpoilsMapVerification;
        Schema21SpoilsVerification? verification = spoils is null ? null : new(
            spoils.ExpectedDeckCount, spoils.ExpectedCardIds,
            spoils.Entries.Select(entry => new Schema21SpoilsEntry(
                entry.DeckIndex, entry.Coord is { } coord ? new MapCoord(coord.Col, coord.Row) : null)).ToArray());
        var options = new Schema21RestoreOptions(request.HasLiveSidecar, request.LiveActFloor,
            request.NextRoomId, verification);
        try
        {
            return RunState.RestoreSchema21(request.NormalizedSaveJson, options);
        }
        catch (Schema21SaveException error)
        {
            throw new BridgeRequestFailedException(error.FieldPath, error.Message, error);
        }
    }

    public static RunState RestoreLive(string json)
    {
        try
        {
            return RunState.RestoreSchema21ForLiveCombat(json);
        }
        catch (Schema21SaveException error)
        {
            throw new BridgeRequestFailedException(error.FieldPath, error.Message, error);
        }
    }
}
