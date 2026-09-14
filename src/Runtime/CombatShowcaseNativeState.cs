using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;

namespace CombatSolver;

internal static class CombatShowcaseNativeState
{
    private static ReadOnlySpan<byte> FormatMarker => "CombatSolverNativeV1\n"u8;

    internal static byte[] CaptureNormalized(CombatState state)
    {
        NetFullCombatState native = NetFullCombatState.FromRun(
            state.RunState,
            justFinishedAction: null);
        native.nextChoiceIds.Clear();
        native.nextRewardIds.Clear();
        byte[] body = Encoding.UTF8.GetBytes(native.ToString().ReplaceLineEndings("\n"));
        byte[] result = new byte[FormatMarker.Length + body.Length];
        FormatMarker.CopyTo(result);
        body.CopyTo(result.AsSpan(FormatMarker.Length));
        return result;
    }

    internal static bool AssertMatches(CombatState state, string path)
    {
        byte[] expected = File.ReadAllBytes(path);
        if (!expected.AsSpan().StartsWith(FormatMarker))
        {
            // Protocol 1 originally stored patched PacketWriter bytes. Auxiliary
            // save extensions make those bytes impossible to decode across mod sets.
            // The caller has already completed the exact replay-state comparison.
            return false;
        }

        byte[] actual = CaptureNormalized(state);
        if (CanonicalBytesMatch(expected, actual))
            return true;

        int offset = 0;
        while (offset < Math.Min(expected.Length, actual.Length)
               && expected[offset] == actual[offset])
            offset++;
        throw new InvalidDataException(
            $"showcase_native_state_mismatch:byte={offset}:" +
            $"expected_bytes={expected.Length}:actual_bytes={actual.Length}");
    }

    internal static void VerifyContractForTesting(CombatState state)
    {
        byte[] captured = CaptureNormalized(state);
        string path = Path.Combine(
            Path.GetTempPath(),
            $"CombatSolverShowcaseNative-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, captured);
            if (!AssertMatches(state, path))
                throw new InvalidOperationException("录像规范原生状态无法通过自身校验。");
            captured[^1] ^= 1;
            File.WriteAllBytes(path, captured);
            try
            {
                AssertMatches(state, path);
            }
            catch (InvalidDataException)
            {
                return;
            }
            throw new InvalidOperationException("录像规范原生状态损坏检查未生效。");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool CanonicalBytesMatch(
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual)
        => expected.StartsWith(FormatMarker)
           && actual.StartsWith(FormatMarker)
           && expected.SequenceEqual(actual);
}
