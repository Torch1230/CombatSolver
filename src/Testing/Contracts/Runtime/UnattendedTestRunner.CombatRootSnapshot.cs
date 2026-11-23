using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertCombatRootSnapshotAsync(CombatState combat, Player player)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("根快照测试必须从主线程开始。");
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("根快照测试找不到玩家战斗状态。");
        int capturedEnergy = playerState.Energy;
        string liveBeforeCaptureText = ContinuationStamp.CaptureLive(combat).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);

        bool workerCaptureRejected = await Task.Run(() =>
        {
            try
            {
                _ = CombatRootSnapshot.Capture(combat);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("main thread", StringComparison.Ordinal);
            }
        });
        if (!workerCaptureRejected)
            throw new InvalidOperationException("后台线程创建 combat root snapshot 未被拒绝。");

        if (ContinuationStamp.CaptureLive(combat).StateText != liveBeforeCaptureText)
            throw new InvalidOperationException("根捕获改动了实机状态。");

        try
        {
            playerState.GainEnergy(1);
            (int hotEnergy, string roundTripText) = await Task.Run(() =>
            {
                CombatHotState state = root.HotRoot.RootState;
                HotColdSidecar? cold = root.HotRoot.RootCold;
                byte[] packed = new byte[HotStatePacker.ComputePackedSize(in state)];
                int written = HotStatePacker.Pack(ref state, ref cold, packed, canonicalize: true);
                CombatHotState unpacked = default;
                HotStatePacker.Unpack(packed.AsSpan(0, written), ref unpacked);
                string text = HotContinuationStamp.CaptureFromHot(in unpacked, cold, root.HotRoot.Rules);
                return ((int)unpacked.Energy, text);
            });
            if (hotEnergy != capturedEnergy)
            {
                throw new InvalidOperationException(
                    $"根快照在实机状态变化后回读了能量：captured={capturedEnergy} hot={hotEnergy}。");
            }
            if (!string.Equals(root.ContinuationStamp.StateText, roundTripText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "根快照 Pack/Unpack 与捕获状态不一致：" +
                    root.ContinuationStamp.DescribeFirstDifference(new ContinuationStamp(roundTripText)));
            }
        }
        finally
        {
            SetEnergy(player, capturedEnergy);
        }

        Entry.Logger.Info(
            $"[CombatSolver/Unattended] COMBAT_ROOT_SNAPSHOT_OK " +
            $"turn={root.StartTurnNumber} enemies={root.HotRoot.RootState.EnemyCount} energy={capturedEnergy}");
    }
}
