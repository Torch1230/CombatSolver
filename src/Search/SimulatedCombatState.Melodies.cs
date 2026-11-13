using System.Text;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private Dictionary<Player, MelodyState>? _melodies;
    internal static readonly CardTag[] MelodyTags = [CardTag.Wah, CardTag.Cho, CardTag.Ki];

    private sealed class MelodyState
    {
        public int[] Started = new int[3];
        public int[] Finished = new int[3];
        public CardPlay?[] First = new CardPlay?[3];
        public int CardsFinished;

        public MelodyState Fork(PredictionForkContext context) => new()
        {
            Started = (int[])Started.Clone(), Finished = (int[])Finished.Clone(),
            First = First.Select(play => play is null ? null : context.RemapOrSelf(play)).ToArray(),
            CardsFinished = CardsFinished
        };
    }

    private void CaptureMelodies(CombatState combat)
    {
        foreach (var entry in _rootHistory.CardPlaysStarted)
            if (entry.HappenedThisTurn(combat)) RecordMelodyStarted(entry.CardPlay);
        foreach (var entry in _rootHistory.CardPlaysFinished)
            if (entry.HappenedThisTurn(combat)) RecordMelodyFinished(entry.CardPlay);
    }

    private void RecordMelodyStarted(CardPlay play)
    {
        for (int index = 0; index < MelodyTags.Length; index++)
        {
            if (!play.Card.Tags.Contains(MelodyTags[index])) continue;
            var state = GetMelodyState(play.Player);
            state.Started[index]++;
            state.First[index] ??= play;
        }
    }

    internal void RecordMelodyFinished(CardPlay play)
    {
        bool any = false;
        for (int index = 0; index < MelodyTags.Length; index++)
        {
            if (!play.Card.Tags.Contains(MelodyTags[index])) continue;
            GetMelodyState(play.Player).Finished[index]++;
            any = true;
        }
        if (any) GetMelodyState(play.Player).CardsFinished++;
    }

    private MelodyState GetMelodyState(Player player)
    {
        _melodies ??= [];
        if (!_melodies.TryGetValue(player, out var state)) _melodies.Add(player, state = new());
        return state;
    }

    public int MelodyStartedCount(Player player, CardTag tag)
        => _melodies?.GetValueOrDefault(player)?.Started[Array.IndexOf(MelodyTags, tag)] ?? 0;
    public bool PlayedMelody(Player player, CardTag tag)
        => (_melodies?.GetValueOrDefault(player)?.Finished[Array.IndexOf(MelodyTags, tag)] ?? 0) > 0;
    public int MelodyCount(Player player) => _melodies?.GetValueOrDefault(player)?.Finished.Count(count => count > 0) ?? 0;
    public int MelodyCardsPlayed(Player player) => _melodies?.GetValueOrDefault(player)?.CardsFinished ?? 0;
    public bool IsFirstMelodyPlay(CardPlay play, CardTag tag)
        => ReferenceEquals(_melodies?.GetValueOrDefault(play.Player)?.First[Array.IndexOf(MelodyTags, tag)], play);

    internal void AppendMelodyState(StringBuilder text, Player player)
    {
        var state = _melodies?.GetValueOrDefault(player);
        AppendMelodyState(text, state?.Started ?? [0, 0, 0], state?.Finished ?? [0, 0, 0], state?.CardsFinished ?? 0);
    }

    internal static void AppendLiveMelodyState(StringBuilder text, Player player, CombatState combat)
    {
        var history = CombatManager.Instance.History;
        var started = history.CardPlaysStarted.Where(entry => entry.HappenedThisTurn(combat) && entry.CardPlay.Player == player).ToArray();
        var finished = history.CardPlaysFinished.Where(entry => entry.HappenedThisTurn(combat) && entry.CardPlay.Player == player).ToArray();
        AppendMelodyState(text, MelodyTags.Select(tag => started.Count(entry => entry.CardPlay.Card.Tags.Contains(tag))).ToArray(),
            MelodyTags.Select(tag => finished.Count(entry => entry.CardPlay.Card.Tags.Contains(tag))).ToArray(),
            finished.Count(entry => MelodyTags.Any(tag => entry.CardPlay.Card.Tags.Contains(tag))));
    }

    private static void AppendMelodyState(StringBuilder text, int[] started, int[] finished, int cards)
        => text.Append(";melodies=").AppendJoin(',', started).Append('/').AppendJoin(',', finished).Append('/').Append(cards);

    internal void AddMelodyFingerprint(ref StateFingerprintBuilder fingerprint, Player player)
    {
        var state = _melodies?.GetValueOrDefault(player);
        for (int index = 0; index < 3; index++)
        {
            fingerprint.Add(state?.Started[index] ?? 0);
            fingerprint.Add(state?.Finished[index] ?? 0);
        }
        fingerprint.Add(state?.CardsFinished ?? 0);
    }
}
