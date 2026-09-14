using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private enum AutoPrePlayStage { Bombardments, Scheduled, Enchantments, WhisperingEarring, Finish }

    private bool ContinueAutoPrePlay(CombatPredictionSimulator simulator, Player player, int turn,
        ISet<uint> deaths, IReadOnlyList<PredictedCard> bombardments, AutoPrePlayStage stage, int nextIndex = 0)
    {
        if (stage == AutoPrePlayStage.Bombardments)
        {
            for (int index = nextIndex; index < bombardments.Count; index++)
            {
                PredictedCard card = bombardments[index];
                if (AutoPlayWithChoice(simulator, card, card.Preview.Id.Entry,
                    $"{card.Preview.Id.Entry}+{card.Preview.CurrentUpgradeLevel}#{index}", ActiveExecutionChoices, deaths)) continue;
                simulator.AppendExecutionContinuation(new AutoPrePlayFrame(player, turn, deaths, bombardments, stage, index + 1));
                return true;
            }
        }
        if (stage <= AutoPrePlayStage.Scheduled && TriggerScheduledAutoPlays(simulator, player, turn, ActiveExecutionChoices, deaths))
        {
            simulator.AppendExecutionContinuation(new AutoPrePlayFrame(player, turn, deaths, bombardments, AutoPrePlayStage.Enchantments));
            return true;
        }
        if (stage <= AutoPrePlayStage.Enchantments && EnchantmentLifecycleSupport.TriggerAutoPrePlay(simulator, this, player, turn, ActiveExecutionChoices, deaths))
        {
            simulator.AppendExecutionContinuation(new AutoPrePlayFrame(player, turn, deaths, bombardments, AutoPrePlayStage.WhisperingEarring));
            return true;
        }
        if (stage <= AutoPrePlayStage.WhisperingEarring)
        {
            bool completed;
            using (simulator.BeginExecutionDispatch()) completed = TriggerWhisperingEarring(simulator, player, turn, deaths);
            if (!completed) return true;
        }
        simulator.State.GetPlayerCombatState(player).Phase = PlayerTurnPhase.Play;
        return false;
    }

    private sealed record AutoPrePlayFrame(Player Player, int Turn, ISet<uint> Deaths,
        IReadOnlyList<PredictedCard> Bombardments, AutoPrePlayStage Stage, int NextIndex = 0) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            ForkExecutionDeaths(Deaths, context);
            foreach (PredictedCard card in Bombardments)
                if (!context.TryRemap(card, out PredictedCard? _)) card.Fork(context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Deaths = context.RequireRemap(Deaths), Bombardments = Bombardments.Select(context.RequireRemap).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
            => !((SimulatedCombatState)simulator.State.CombatState).ContinueAutoPrePlay(simulator, Player, Turn, Deaths, Bombardments, Stage, NextIndex);
    }

    private bool ContinueScheduledAutoPlays(CombatPredictionSimulator simulator, Player player, int turn,
        ISet<uint> deaths, IReadOnlyList<PredictedCard> mayhemCards, int nextIndex)
    {
        for (int index = nextIndex; index < mayhemCards.Count; index++)
        {
            PredictedCard card = mayhemCards[index];
            if (AutoPlayWithChoice(simulator, card, CanonicalModels.Power<MayhemPower>().Id.Entry,
                $"{card.Preview.Id.Entry}+{card.Preview.CurrentUpgradeLevel}#{index}", ActiveExecutionChoices, deaths)) continue;
            simulator.AppendExecutionContinuation(new ScheduledAutoPlayFrame(player, turn, deaths, mayhemCards, index + 1));
            return true;
        }
        if (turn <= 1 || GetPreviousTurnAttack(simulator, player) is not { } previousAttack) return false;
        HistoryCourse[] relics = RelicsOf(player).OfType<HistoryCourse>().Where(static relic => !relic.IsMelted).ToArray();
        return ContinueHistoryCourse(simulator, player, deaths, previousAttack, relics, 0);
    }

    private bool ContinueHistoryCourse(CombatPredictionSimulator simulator, Player player, ISet<uint> deaths,
        PredictedCard previousAttack, IReadOnlyList<HistoryCourse> relics, int nextIndex)
    {
        for (int index = nextIndex; index < relics.Count; index++)
        {
            PredictedCard copy = previousAttack.CreateDupeForPlayer(player);
            if (AutoPlayWithChoice(simulator, copy, relics[index].Id.Entry,
                $"{copy.Preview.Id.Entry}+{copy.Preview.CurrentUpgradeLevel}#0", ActiveExecutionChoices, deaths)) continue;
            simulator.AppendExecutionContinuation(new HistoryCourseFrame(player, deaths, previousAttack, relics, index + 1));
            return true;
        }
        return false;
    }

    private sealed record ScheduledAutoPlayFrame(Player Player, int Turn, ISet<uint> Deaths,
        IReadOnlyList<PredictedCard> MayhemCards, int NextIndex) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            ForkExecutionDeaths(Deaths, context);
            foreach (PredictedCard card in MayhemCards)
                if (!context.TryRemap(card, out PredictedCard? _)) card.Fork(context);
            if (MayhemCards is List<PredictedCard> list) CombatPredictionSimulator.ForkExecutionCardList(list, context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Deaths = context.RequireRemap(Deaths), MayhemCards = MayhemCards is List<PredictedCard> list
                ? context.RequireRemap(list) : MayhemCards.Select(context.RequireRemap).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
            => !((SimulatedCombatState)simulator.State.CombatState).ContinueScheduledAutoPlays(simulator, Player, Turn, Deaths, MayhemCards, NextIndex);
    }

    private sealed record HistoryCourseFrame(Player Player, ISet<uint> Deaths, PredictedCard PreviousAttack,
        IReadOnlyList<HistoryCourse> Relics, int NextIndex) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            ForkExecutionDeaths(Deaths, context);
            if (!context.TryRemap(PreviousAttack, out PredictedCard? _)) PreviousAttack.Fork(context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Deaths = context.RequireRemap(Deaths), PreviousAttack = context.RequireRemap(PreviousAttack), Relics = Relics.Select(context.RemapOrSelf).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
            => !((SimulatedCombatState)simulator.State.CombatState).ContinueHistoryCourse(simulator, Player, Deaths, PreviousAttack, Relics, NextIndex);
    }
}
