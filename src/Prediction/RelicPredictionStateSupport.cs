using System.Text;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Orb;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class RelicPredictionStateSupport
{
    public static bool IsTracked(RelicModel relic)
        => relic is BeatingRemnant
            or BrilliantScarf
            or BurningSticks
            or CentennialPuzzle
            or DemonTongue
            or IronClub
            or Kunai
            or JossPaper
            or Kusarigama
            or LetterOpener
            or LizardTail
            or Metronome
            or MusicBox
            or Nunchaku
            or OrnamentalFan
            or PaelsLegion
            or PenNib
            or Permafrost
            or RainbowRing
            or Regalite
            or Shuriken
            or ThrowingAxe
            or TuningFork
            or Vambrace
            or VelvetChoker;

    public static void CaptureRootState(
        CombatPredictionSimulator simulator,
        RelicModel predicted,
        RelicModel live)
    {
        switch (predicted, live)
        {
            case (BeatingRemnant target, BeatingRemnant source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new BeatingRemnantPredictionState(source));
                break;
            case (BrilliantScarf target, BrilliantScarf source):
                CaptureCounter(target, source._cardsPlayedThisTurn);
                break;
            case (BurningSticks target, BurningSticks source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new BurningSticksPredictionState(source));
                break;
            case (CentennialPuzzle target, CentennialPuzzle source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new CentennialPuzzlePredictionState(source));
                break;
            case (DemonTongue target, DemonTongue source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new DemonTonguePredictionState(source));
                break;
            case (IronClub target, IronClub source):
                CaptureCounter(target, source.CardsPlayed);
                break;
            case (JossPaper target, JossPaper source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new JossPaperPredictionState(source));
                break;
            case (Kunai target, Kunai source):
                CaptureCounter(target, source._attacksPlayedThisTurn);
                break;
            case (Kusarigama target, Kusarigama source):
                CaptureCounter(target, source._attacksPlayedThisTurn);
                break;
            case (LetterOpener target, LetterOpener source):
                CaptureCounter(target, source._skillsPlayedThisTurn);
                break;
            case (LizardTail target, LizardTail source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new LizardTailPredictionState(source));
                break;
            case (Metronome target, Metronome source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new MetronomePredictionState(source));
                break;
            case (MusicBox target, MusicBox source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new MusicBoxPredictionState(source));
                break;
            case (Nunchaku target, Nunchaku source):
                CaptureCounter(target, source.AttacksPlayed);
                break;
            case (OrnamentalFan target, OrnamentalFan source):
                CaptureCounter(target, source._attacksPlayedThisTurn);
                break;
            case (PaelsLegion target, PaelsLegion source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new PaelsLegionPredictionState(source));
                break;
            case (PenNib target, PenNib source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new PenNibPredictionState(source));
                break;
            case (Permafrost target, Permafrost source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new FlagPredictionState(source._activatedThisCombat));
                break;
            case (RainbowRing target, RainbowRing source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new RainbowRingPredictionState(source));
                break;
            case (Regalite target, Regalite source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new RegalitePredictionState(source));
                break;
            case (Shuriken target, Shuriken source):
                CaptureCounter(target, source._attacksPlayedThisTurn);
                break;
            case (ThrowingAxe target, ThrowingAxe source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new ThrowingAxePredictionState(source));
                break;
            case (TuningFork target, TuningFork source):
                CaptureCounter(target, source.SkillsPlayed);
                break;
            case (Vambrace target, Vambrace source):
                _ = simulator.StateStore.GetReadOnly((AbstractModel)target, () => new VambracePredictionState(source));
                break;
            case (VelvetChoker target, VelvetChoker source):
                CaptureCounter(target, source._cardsPlayedThisTurn);
                break;
            case (_, _) when !IsTracked(predicted):
                break;
            default:
                throw new InvalidOperationException(
                    $"Relic root state type mismatch: predicted={predicted.GetType().FullName} live={live.GetType().FullName}.");
        }

        void CaptureCounter(RelicModel target, int value)
            => _ = simulator.StateStore.GetReadOnly(
                (AbstractModel)target,
                () => new CounterPredictionState(value));
    }

    public static void ResetBeforeSideTurnStart(
        CombatPredictionSimulator simulator,
        RelicModel relic)
    {
        switch (relic)
        {
            case BeatingRemnant value:
                simulator.StateStore
                    .Get(value, static model => new BeatingRemnantPredictionState(model))
                    .DamageReceivedThisTurn = 0m;
                break;
            case BrilliantScarf value:
                Counter(simulator, value, value._cardsPlayedThisTurn).Value = 0;
                break;
            case DemonTongue value:
                simulator.StateStore
                    .Get(value, static model => new DemonTonguePredictionState(model))
                    .TriggeredThisTurn = false;
                break;
            case Kunai value:
                Counter(simulator, value, value._attacksPlayedThisTurn).Value = 0;
                break;
            case MusicBox value:
                {
                    MusicBoxPredictionState state = simulator.StateStore
                        .Get(value, static model => new MusicBoxPredictionState(model));
                    state.WasUsedThisTurn = false;
                    state.CardBeingPlayed = null;
                    break;
                }
            case OrnamentalFan value:
                Counter(simulator, value, value._attacksPlayedThisTurn).Value = 0;
                break;
            case RainbowRing value:
                {
                    RainbowRingPredictionState state = simulator.StateStore
                        .Get(value, static model => new RainbowRingPredictionState(model));
                    state.AttacksPlayedThisTurn = 0;
                    state.SkillsPlayedThisTurn = 0;
                    state.PowersPlayedThisTurn = 0;
                    state.ActivationCountThisTurn = 0;
                    break;
                }
            case Regalite value:
                simulator.StateStore
                    .Get(value, static model => new RegalitePredictionState(model))
                    .UsedThisTurn = false;
                break;
            case Shuriken value:
                Counter(simulator, value, value._attacksPlayedThisTurn).Value = 0;
                break;
            case VelvetChoker value:
                Counter(simulator, value, value._cardsPlayedThisTurn).Value = 0;
                break;
        }
    }

    public static void ResetAfterSideTurnStart(
        CombatPredictionSimulator simulator,
        RelicModel relic,
        int turn)
    {
        switch (relic)
        {
            case LetterOpener value when turn > 1:
                Counter(simulator, value, value._skillsPlayedThisTurn).Value = 0;
                break;
            case PaelsLegion value:
                {
                    PaelsLegionPredictionState state = simulator.StateStore
                        .Get(value, static model => new PaelsLegionPredictionState(model));
                    state.Cooldown--;
                    state.TriggeredBlockLastTurn = false;
                    break;
                }
        }
    }

    public static void ResetAfterSideTurnEnd(
        CombatPredictionSimulator simulator,
        RelicModel relic)
    {
        if (relic is Kusarigama value)
            Counter(simulator, value, value._attacksPlayedThisTurn).Value = 0;
    }

    public static void AppendFingerprint(
        ref StateFingerprintBuilder fingerprint,
        CombatPredictionSimulator simulator,
        RelicModel relic)
    {
        switch (relic)
        {
            case BeatingRemnant value:
                fingerprint.Add(simulator.StateStore
                    .Peek(value, static relic => new BeatingRemnantPredictionState(relic))
                    .DamageReceivedThisTurn);
                break;
            case BrilliantScarf value:
                fingerprint.Add(CounterValueReadOnly(simulator, value, value._cardsPlayedThisTurn));
                break;
            case BurningSticks value:
                fingerprint.Add(simulator.StateStore
                    .Peek(value, static relic => new BurningSticksPredictionState(relic))
                    .WasUsedThisCombat);
                break;
            case CentennialPuzzle value:
                fingerprint.Add(simulator.StateStore
                    .Peek(value, static relic => new CentennialPuzzlePredictionState(relic))
                    .UsedThisCombat);
                break;
            case DemonTongue value:
                fingerprint.Add(simulator.StateStore
                    .GetReadOnly(value, static relic => new DemonTonguePredictionState(relic))
                    .TriggeredThisTurn);
                break;
            case IronClub value:
                fingerprint.Add(CounterValueReadOnly(simulator, value, value.CardsPlayed));
                break;
            case Kunai value:
                fingerprint.Add(CounterValueReadOnly(simulator, value, value._attacksPlayedThisTurn));
                break;
            case JossPaper value:
                fingerprint.Add(JossPaperValueReadOnly(simulator, value));
                break;
            case Kusarigama value:
                fingerprint.Add(CounterValueReadOnly(simulator, value, value._attacksPlayedThisTurn));
                break;
            case LetterOpener value:
                fingerprint.Add(CounterValueReadOnly(simulator, value, value._skillsPlayedThisTurn));
                break;
            case LizardTail value:
                fingerprint.Add(simulator.StateStore
                    .Peek(value, static relic => new LizardTailPredictionState(relic))
                    .WasUsed);
                break;
            case Metronome value:
                fingerprint.Add(simulator.StateStore
                    .Peek(value, static relic => new MetronomePredictionState(relic))
                    .OrbsChanneled);
                break;
            case MusicBox value:
                {
                    if (simulator.StateStore.TryGetReadOnly(
                            (AbstractModel)value,
                            out MusicBoxPredictionState? state))
                    {
                        fingerprint.Add(state!.WasUsedThisTurn);
                        fingerprint.Add(state.CardBeingPlayed?.Id.Entry);
                    }
                    else
                    {
                        fingerprint.Add(value._wasUsedThisTurn);
                        fingerprint.Add((string?)null);
                    }
                    break;
                }
            case Nunchaku value:
                fingerprint.Add(CounterValueReadOnly(simulator, value, value.AttacksPlayed));
                break;
            case OrnamentalFan value:
                fingerprint.Add(CounterValueReadOnly(simulator, value, value._attacksPlayedThisTurn));
                break;
            case PaelsLegion value:
                {
                    PaelsLegionPredictionState state = simulator.StateStore
                        .Peek(value, static relic => new PaelsLegionPredictionState(relic));
                    fingerprint.Add(state.Cooldown);
                    fingerprint.Add(state.TriggeredBlockLastTurn);
                    fingerprint.Add(state.AffectedCardPlay != null);
                    break;
                }
            case PenNib value:
                fingerprint.Add(simulator.StateStore
                    .Peek(value, static relic => new PenNibPredictionState(relic))
                    .AttacksPlayed);
                break;
            case Permafrost value:
                fingerprint.Add(simulator.StateStore
                    .Peek(value, static relic => new FlagPredictionState(relic._activatedThisCombat))
                    .Value);
                break;
            case RainbowRing value:
                {
                    RainbowRingPredictionState state = simulator.StateStore
                        .Peek(value, static relic => new RainbowRingPredictionState(relic));
                    fingerprint.Add(state.AttacksPlayedThisTurn);
                    fingerprint.Add(state.SkillsPlayedThisTurn);
                    fingerprint.Add(state.PowersPlayedThisTurn);
                    fingerprint.Add(state.ActivationCountThisTurn);
                    break;
                }
            case Regalite value:
                fingerprint.Add(simulator.StateStore
                    .Peek(value, static relic => new RegalitePredictionState(relic))
                    .UsedThisTurn);
                break;
            case Shuriken value:
                fingerprint.Add(CounterValueReadOnly(simulator, value, value._attacksPlayedThisTurn));
                break;
            case ThrowingAxe value:
                fingerprint.Add(simulator.StateStore
                    .Peek(value, static relic => new ThrowingAxePredictionState(relic))
                    .UsedThisCombat);
                break;
            case TuningFork value:
                fingerprint.Add(CounterValueReadOnly(simulator, value, value.SkillsPlayed));
                break;
            case Vambrace value:
                fingerprint.Add(simulator.StateStore
                    .Peek(value, static relic => new VambracePredictionState(relic))
                    .BlockGainedThisCombat);
                break;
            case VelvetChoker value:
                fingerprint.Add(CounterValueReadOnly(simulator, value, value._cardsPlayedThisTurn));
                break;
        }
    }

    public static void AppendLiveContinuation(StringBuilder text, Player player)
        => AppendContinuation(text, player.Relics, LiveStateText);

    public static void AppendPredictedContinuation(
        StringBuilder text,
        CombatPredictionSimulator simulator,
        IEnumerable<RelicModel> relics)
        => AppendContinuation(text, relics, relic => PredictedStateText(simulator, relic));

    internal static string LiveStateForVerification(RelicModel relic)
        => LiveStateText(relic);

    public static int GetCounterValue(
        CombatPredictionSimulator simulator,
        RelicModel relic,
        int liveValue)
        => CounterValueReadOnly(simulator, relic, liveValue);

    public static int GetRainbowActivationCount(
        CombatPredictionSimulator simulator,
        RainbowRing relic)
        => simulator.StateStore
            .GetReadOnly((AbstractModel)relic, () => new RainbowRingPredictionState(relic))
            .ActivationCountThisTurn;

    public static int GetJossPaperCardsExhausted(
        CombatPredictionSimulator simulator,
        JossPaper relic)
        => JossPaperValueReadOnly(simulator, relic);

    public static void SetJossPaperCardsExhausted(
        CombatPredictionSimulator simulator,
        JossPaper relic,
        int value)
        => JossPaperState(simulator, relic).CardsExhausted = value;

    private static JossPaperPredictionState JossPaperState(
        CombatPredictionSimulator simulator,
        JossPaper relic)
        => simulator.StateStore.Get(
            (AbstractModel)relic,
            () => new JossPaperPredictionState(relic));

    private static int JossPaperValueReadOnly(
        CombatPredictionSimulator simulator,
        JossPaper relic)
        => simulator.StateStore.TryGetReadOnly(
            (AbstractModel)relic,
            out JossPaperPredictionState? state)
            ? state!.CardsExhausted
            : relic.CardsExhausted;

    private static CounterPredictionState Counter(
        CombatPredictionSimulator simulator,
        RelicModel relic,
        int liveValue)
        => simulator.StateStore.Get((AbstractModel)relic, () => new CounterPredictionState(liveValue));

    private static int CounterValueReadOnly(
        CombatPredictionSimulator simulator,
        RelicModel relic,
        int liveValue)
        => simulator.StateStore.TryGetReadOnly(
            (AbstractModel)relic,
            out CounterPredictionState? state)
            ? state!.Value
            : liveValue;

    private static void AppendContinuation(
        StringBuilder text,
        IEnumerable<RelicModel> relics,
        Func<RelicModel, string> stateText)
    {
        text.Append(";mirrorRelics=");
        bool first = true;
        foreach (RelicModel relic in relics
                     .Where(static relic => !relic.IsMelted && IsTracked(relic))
                     .OrderBy(static relic => relic.Id.Entry, StringComparer.Ordinal))
        {
            if (!first)
                text.Append(',');
            first = false;
            text.Append(relic.Id.Entry).Append('/').Append(stateText(relic));
        }
    }

    private static string LiveStateText(RelicModel relic)
        => relic switch
        {
            BeatingRemnant value => value._damageReceivedThisTurn.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BrilliantScarf value => value._cardsPlayedThisTurn.ToString(),
            BurningSticks value => Bool(value.WasUsedThisCombat),
            CentennialPuzzle value => Bool(value.UsedThisCombat),
            DemonTongue value => Bool(value._triggeredThisTurn),
            IronClub value => value.CardsPlayed.ToString(),
            JossPaper value => value.CardsExhausted.ToString(),
            Kunai value => value._attacksPlayedThisTurn.ToString(),
            Kusarigama value => value._attacksPlayedThisTurn.ToString(),
            LetterOpener value => value._skillsPlayedThisTurn.ToString(),
            LizardTail value => Bool(value.WasUsed),
            Metronome value => value._orbsChanneled.ToString(),
            MusicBox value => Bool(value._wasUsedThisTurn),
            Nunchaku value => value.AttacksPlayed.ToString(),
            OrnamentalFan value => value._attacksPlayedThisTurn.ToString(),
            PaelsLegion value => PaelsLegionText(new PaelsLegionPredictionState(value)),
            PenNib value => value.AttacksPlayed.ToString(),
            Permafrost value => Bool(value._activatedThisCombat),
            RainbowRing value => RainbowRingText(new RainbowRingPredictionState(value)),
            Regalite value => Bool(new RegalitePredictionState(value).UsedThisTurn),
            Shuriken value => value._attacksPlayedThisTurn.ToString(),
            ThrowingAxe value => Bool(value._usedThisCombat),
            TuningFork value => value.SkillsPlayed.ToString(),
            Vambrace value => Bool(value._blockGainedThisCombat),
            VelvetChoker value => value._cardsPlayedThisTurn.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(relic), relic.GetType().FullName),
        };

    private static string PredictedStateText(CombatPredictionSimulator simulator, RelicModel relic)
        => relic switch
        {
            BeatingRemnant value => simulator.StateStore
                .Peek((AbstractModel)value, () => new BeatingRemnantPredictionState(value))
                .DamageReceivedThisTurn.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BrilliantScarf value => CounterValueReadOnly(simulator, value, value._cardsPlayedThisTurn).ToString(),
            BurningSticks value => Bool(simulator.StateStore
                .Peek((AbstractModel)value, () => new BurningSticksPredictionState(value)).WasUsedThisCombat),
            CentennialPuzzle value => Bool(simulator.StateStore
                .Peek((AbstractModel)value, () => new CentennialPuzzlePredictionState(value)).UsedThisCombat),
            DemonTongue value => Bool(simulator.StateStore
                .Peek((AbstractModel)value, () => new DemonTonguePredictionState(value)).TriggeredThisTurn),
            IronClub value => CounterValueReadOnly(simulator, value, value.CardsPlayed).ToString(),
            JossPaper value => JossPaperValueReadOnly(simulator, value).ToString(),
            Kunai value => CounterValueReadOnly(simulator, value, value._attacksPlayedThisTurn).ToString(),
            Kusarigama value => CounterValueReadOnly(simulator, value, value._attacksPlayedThisTurn).ToString(),
            LetterOpener value => CounterValueReadOnly(simulator, value, value._skillsPlayedThisTurn).ToString(),
            LizardTail value => Bool(simulator.StateStore
                .Peek((AbstractModel)value, () => new LizardTailPredictionState(value)).WasUsed),
            Metronome value => simulator.StateStore
                .Peek((AbstractModel)value, () => new MetronomePredictionState(value)).OrbsChanneled.ToString(),
            MusicBox value => Bool(simulator.StateStore
                .Peek((AbstractModel)value, () => new MusicBoxPredictionState(value)).WasUsedThisTurn),
            Nunchaku value => CounterValueReadOnly(simulator, value, value.AttacksPlayed).ToString(),
            OrnamentalFan value => CounterValueReadOnly(simulator, value, value._attacksPlayedThisTurn).ToString(),
            PaelsLegion value => PaelsLegionText(simulator.StateStore
                .Peek((AbstractModel)value, () => new PaelsLegionPredictionState(value))),
            PenNib value => simulator.StateStore
                .Peek((AbstractModel)value, () => new PenNibPredictionState(value)).AttacksPlayed.ToString(),
            Permafrost value => Bool(simulator.StateStore
                .Peek((AbstractModel)value, () => new FlagPredictionState(value._activatedThisCombat)).Value),
            RainbowRing value => RainbowRingText(simulator.StateStore
                .Peek((AbstractModel)value, () => new RainbowRingPredictionState(value))),
            Regalite value => Bool(simulator.StateStore
                .Peek((AbstractModel)value, () => new RegalitePredictionState(value)).UsedThisTurn),
            Shuriken value => CounterValueReadOnly(simulator, value, value._attacksPlayedThisTurn).ToString(),
            ThrowingAxe value => Bool(simulator.StateStore
                .Peek((AbstractModel)value, () => new ThrowingAxePredictionState(value)).UsedThisCombat),
            TuningFork value => CounterValueReadOnly(simulator, value, value.SkillsPlayed).ToString(),
            Vambrace value => Bool(simulator.StateStore
                .Peek((AbstractModel)value, () => new VambracePredictionState(value)).BlockGainedThisCombat),
            VelvetChoker value => CounterValueReadOnly(simulator, value, value._cardsPlayedThisTurn).ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(relic), relic.GetType().FullName),
        };

    private static string PaelsLegionText(PaelsLegionPredictionState state)
        => $"{state.Cooldown}:{Bool(state.TriggeredBlockLastTurn)}";

    private static string RainbowRingText(RainbowRingPredictionState state)
        => $"{state.AttacksPlayedThisTurn}:{state.SkillsPlayedThisTurn}:{state.PowersPlayedThisTurn}:{state.ActivationCountThisTurn}";

    private static string Bool(bool value) => value ? "1" : "0";
}
