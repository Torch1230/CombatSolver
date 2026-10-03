using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static bool AssertNativeCheckpoint(CombatState state, string? nativePath, bool differentModelEncoding = false)
    {
        if (string.IsNullOrWhiteSpace(nativePath))
            return false;
        byte[] expected = File.ReadAllBytes(nativePath);
        NetFullCombatState actual = NetFullCombatState.FromRun(state.RunState, justFinishedAction: null);
        PacketWriter writer = new() { WarnOnGrow = false };
        actual.Serialize(writer);
        writer.ZeroByteRemainder();
        ReadOnlySpan<byte> bytes = writer.Buffer.AsSpan(0, writer.BytePosition);
        if (bytes.SequenceEqual(expected))
            return true;
        if (differentModelEncoding)
            return false;
        int offset = 0;
        while (offset < Math.Min(expected.Length, bytes.Length) && expected[offset] == bytes[offset])
            offset++;
        PacketReader reader = new();
        reader.Reset(expected);
        NetFullCombatState saved = reader.Read<NetFullCombatState>();
        InvalidDataException mismatch = new($"native_state_mismatch:byte={offset}:expected_bytes={expected.Length}:actual_bytes={bytes.Length}");
        mismatch.Data["firstDifference"] = new System.Text.Json.Nodes.JsonObject
        {
            ["field"] = $"nativeState.bytes[{offset}]", ["expected"] = saved.ToString(), ["actual"] = actual.ToString(),
            ["expectedBytes"] = expected.Length, ["actualBytes"] = bytes.Length,
        };
        throw mismatch;
    }
}
