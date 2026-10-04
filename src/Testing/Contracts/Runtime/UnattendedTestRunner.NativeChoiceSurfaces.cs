using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using CombatSolver.Engine.Common;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertNativeChooseOpenGateAsync(CombatState combat, Player player)
    {
        CardModel[] options = [PredictionUtils.CreateCard(ModelDb.Card<Catastrophe>(), player),
            PredictionUtils.CreateCard(ModelDb.Card<JackOfAllTrades>(), player),
            PredictionUtils.CreateCard(ModelDb.Card<Production>(), player)];
        var screen = NChooseACardSelectionScreen.ShowScreen(options, canSkip: true)
            ?? throw new InvalidOperationException("Native choose fixture has no selection screen.");
        Task<IEnumerable<CardModel>> native = screen.CardsSelected();
        var request = new NativeChoiceRequest(1, NativeChoiceSurfaceKind.ChooseCard, player, options, null,
            0, 1, true, true, false, "COLORLESS_POTION");
        request.Completion = native;
        try
        {
            using var surface = await NativeChoiceSurface.WaitAndLockAsync(_host, request, CancellationToken.None);
            ulong started = Time.GetTicksMsec();
            Task selecting = NativeChoiceSurface.SelectAsync(_host, surface, request, [options[1]], CancellationToken.None);
            while (Time.GetTicksMsec() - started < 300)
            {
                EnsureWithinDeadline();
                await NextFrameAsync();
            }
            screen.AfterOverlayOpened();
            await selecting;
            if (!native.IsCompletedSuccessfully || !(await native).SequenceEqual([options[1]], ReferenceEqualityComparer.Instance))
                throw new InvalidOperationException("Native choose driver returned before the reopened page accepted JackOfAllTrades.");
            _completedChecks.Add("NativeChoose:ReopenedPage:NativeOpenGate:AcceptedIdentity");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(screen) && screen.IsInsideTree())
                NOverlayStack.Instance!.Remove(screen);
        }
        var stale = NChooseACardSelectionScreen.ShowScreen(options, canSkip: true)!;
        try
        {
            var other = request with { Options = options.Reverse().ToArray() };
            using var surface = await NativeChoiceSurface.WaitAndLockAsync(_host, other, CancellationToken.None);
            bool rejected = false;
            try { await NativeChoiceSurface.SelectAsync(_host, surface, other, [options[1]], CancellationToken.None); }
            catch (NativeChoiceSurfaceMismatchException) { rejected = true; }
            if (!rejected || !stale.IsInsideTree())
                throw new InvalidOperationException("Native choose fixture must reject a different request and retain its page.");
            _completedChecks.Add("NativeChoose:CandidateOrderMismatch:PageRetained");
        }
        finally { NOverlayStack.Instance!.Remove(stale); }
        var skipped = NChooseACardSelectionScreen.ShowScreen(options, canSkip: true)!;
        Task<IEnumerable<CardModel>> skippedResult = skipped.CardsSelected();
        try
        {
            using var surface = await NativeChoiceSurface.WaitAndLockAsync(_host, request, CancellationToken.None);
            await NativeChoiceSurface.SelectAsync(_host, surface, request, [], CancellationToken.None);
            if (!skippedResult.IsCompletedSuccessfully || (await skippedResult).Any())
                throw new InvalidOperationException("Native choose fixture did not accept the skip.");
            _completedChecks.Add("NativeChoose:Skip:NativeAccepted");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(skipped) && skipped.IsInsideTree())
                NOverlayStack.Instance!.Remove(skipped);
        }
    }
}
