using System.Reflection;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // A read-only inventory of the complete original input and generation roots.
    // Type membership is reported separately from full state/hook admission.
    private void AssertCompactGenerationClosureAudit(CombatState combat, Player player)
    {
        var encounter = combat.Encounter ?? throw new InvalidOperationException("Generation audit requires an encounter.");
        if (player.Character.Id.Entry != "NECROBINDER" || encounter.Id.Entry != "AEONGLASS_BOSS"
            || player.Deck.Cards.Count != 38 || _request.Relics.Length != 19
            || _request.Relics.Any(injection => !player.Relics.Any(relic => relic.Id.Entry == injection.RelicId))
            || player.PotionSlots.Count(potion => potion != null) != 2)
            throw new InvalidOperationException($"Generation closure audit input differs: character={player.Character.Id.Entry}; "
                + $"encounter={encounter.Id.Entry}; deck={player.Deck.Cards.Count}; relics={player.Relics.Count}; "
                + $"potions={player.PotionSlots.Count(potion => potion != null)}.");
        var enemy = combat.Enemies.Single();
        var before = CaptureActual(combat, player, enemy);
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var constraint = combat.RunState.CardMultiplayerConstraint;
        if (constraint != CardMultiplayerConstraint.SingleplayerOnly)
            throw new InvalidOperationException("Generation audit only supports its original single-player input.");
        var admitted = (HashSet<Type>)typeof(CompactCardProgramCompiler)
            .GetField("AdmittedTypes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var callOptions = player.Character.CardPool.GetUnlockedCards(player.UnlockState, constraint)
            .Where(card => card.Rarity is not (CardRarity.Basic or CardRarity.Ancient)).ToArray();
        var colorlessOptions = ModelDb.CardPool<ColorlessCardPool>()
            .GetUnlockedCards(player.UnlockState, constraint).ToArray();
        List<object> pools = [];
        foreach (var source in new[] { (Name: "CallOfTheVoid", Cards: callOptions), (Name: "ColorlessPotion", Cards: colorlessOptions) })
        {
            // Native factory filters player count, then combat eligibility, preserving order.
            var nativeEligible = CardFactory.FilterForCombat(source.Cards.Where(card =>
                card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)).ToArray();
            var legacyEligible = source.Cards.FilterForCombatAndPlayerCount(constraint).ToArray();
            if (!nativeEligible.SequenceEqual(legacyEligible) || nativeEligible.Length == 0)
                throw new InvalidOperationException($"Generation pool order/filter mismatch: {source.Name}.");
            List<object> samples = [];
            using (SimulationNotificationIsolation.Enter())
            {
                var nativeRng = simulator.Rng.CombatCardGeneration.Clone();
                var modelRng = simulator.Rng.CombatCardGeneration.Clone();
                for (int sample = 0; sample < 12; sample++)
                {
                    int requested = source.Name == "ColorlessPotion" ? 3 : 1;
                    var start = nativeRng.CaptureState();
                    var selected = nativeEligible.TakeRandom(requested, nativeRng).ToArray();
                    var predicted = source.Cards.GetDistinctForCombat(player, requested, modelRng, constraint).ToArray();
                    if (!selected.Select(card => card.Id).SequenceEqual(predicted.Select(card => card.Preview.Id))
                        || !SameFiveFieldRngState(nativeRng.CaptureState(), modelRng.CaptureState()))
                        throw new InvalidOperationException($"Generation selection/RNG mismatch: {source.Name}/{sample}.");
                    samples.Add(new { requested, ids = selected.Select(card => card.Id.Entry).ToArray(),
                        counterDelta = nativeRng.CaptureState().Counter - start.Counter });
                }
            }
            pools.Add(new { source.Name, unlockedSourceCount = source.Cards.Length,
                eligibleCount = nativeEligible.Length,
                exactTypeCount = nativeEligible.Count(card => admitted.Contains(card.GetType())),
                cards = nativeEligible.Select(card => new { id = card.Id.Entry, type = card.GetType().Name,
                    category = card.Type.ToString(), rarity = card.Rarity.ToString(),
                    currentExactType = admitted.Contains(card.GetType()) }).ToArray(), samples });
        }
        bool rootAdmitted;
        string rejection;
        using (SimulationNotificationIsolation.Enter())
            rootAdmitted = CompactCombatRoot.TryCreate(simulator, player, out _, out rejection);
        if (rootAdmitted) throw new InvalidOperationException("Audit unexpectedly admitted the full unrepresented generation closure.");
        AssertSnapshotEqual(before, CaptureActual(combat, player, enemy), "GenerationClosureAudit", "ActualUnchanged");
        if (string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
            throw new InvalidOperationException("Generation audit requires an evidence directory.");
        Directory.CreateDirectory(_request.EvidenceDirectory);
        File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "generation-closure-audit.json"),
            JsonSerializer.Serialize(new { character = player.Character.Id.Entry, encounter = encounter.Id.Entry,
                seed = _request.Seed, deckCount = player.Deck.Cards.Count,
                combatCards = player.PlayerCombatState!.AllCards.Select(card => card.Id.Entry).ToArray(),
                injectedRelicCount = _request.Relics.Length,
                actualRelicCount = player.Relics.Count,
                relics = player.Relics.Select(relic => relic.Id.Entry).ToArray(),
                potions = player.PotionSlots.Select(potion => potion?.Id.Entry).ToArray(),
                rootAdmitted, rejection, pools,
                limitation = "Exact type membership is not full state, hook, or transitive generated-pool admission." },
                new JsonSerializerOptions { WriteIndented = true }));
        _completedChecks.Add($"CompactGenerationClosureAudit:Original38Cards19InjectedRelics2Potions:ActualRelics{player.Relics.Count}:CompleteOrderedPools:24SelectionSamples:FiveFieldRng:ActualUnchanged:FullRootExplicitlyRejected");
    }
}
