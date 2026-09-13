using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed partial class ScenarioBuilder
    {
        private ResolvedGeneratedCombatScenario? _generatedScenario;
        private GeneratedScenarioCardSelector? _generatedChoices;

        private IDisposable? BeginGeneratedSetupChoices()
        {
            if (_generatedScenario == null) return null;
            _generatedChoices = new(_generatedScenario.Options.SetupChoices);
            return CardSelectCmd.PushSelector(_generatedChoices, localOnly: true);
        }

        private void PrepareGeneratedScenario()
        {
            if (runner._request.GeneratedScenarioPath is not { } path) return;
            if (string.IsNullOrWhiteSpace(runner._request.EvidenceDirectory))
                throw new InvalidDataException("生成场景必须指定--evidence-directory / -EvidenceDirectory。");
            var options = JsonSerializer.Deserialize<GeneratedCombatScenarioOptions>(
                File.ReadAllText(path), GeneratedCombatScenario.JsonOptions)
                ?? throw new InvalidDataException("生成场景配置为空。");
            if (runner._request.ScenarioId == "GENERATED-SCENARIO-CONTRACT")
                runner._completedChecks.Add(AssertGeneratedScenarioResolver());
            _generatedScenario = GeneratedCombatScenario.Resolve(options);
            runner._request = GeneratedCombatScenario.Apply(runner._request, _generatedScenario);
            runner._protocolHost.ConfigureSearchOverrides(runner._request);
            runner._writer.WriteGeneratedArtifact("generated-scenario.resolved.json", _generatedScenario.Options);
            runner._writer.WriteGeneratedArtifact("generated-scenario.catalog.json", _generatedScenario.Catalog);
            runner._writer.WriteGeneratedArtifact("generated-scenario.request.json", runner._request);
            runner._writer.GeneratedScenario = new()
            {
                ["schemaVersion"] = 1,
                ["mode"] = _generatedScenario.Options.Mode,
                ["catalogFingerprint"] = _generatedScenario.CatalogFingerprint,
                ["resolvedConfiguration"] = "generated-scenario.resolved.json",
                ["loadout"] = "generated-scenario.loadout.json",
                ["opening"] = "generated-scenario.opening.json",
                ["relicObtainEffects"] = _generatedScenario.Options.ApplyRelicObtainEffects,
            };
            runner._completedChecks.Add("GeneratedScenario:Resolved:NativePools:IndependentGeneratorRng");
        }

        private void PrepareGeneratedStartingRelics(Player player)
        {
            if (_generatedScenario?.Options.IncludeStartingRelics != false) return;
            foreach (var relic in player.Relics.ToArray())
                player.RemoveRelicInternal(relic, silent: true);
        }

        private async Task PrepareGeneratedAscendersBaneAsync(RunState runState, Player player)
        {
            if (_generatedScenario is not { } generated) return;
            CardModel[] bane = player.Deck.Cards.Where(c => c is AscendersBane).ToArray();
            if (generated.Options.IncludeAscendersBane)
            {
                if (bane.Length == 0)
                    await InjectRunCardAsync(runState, player, new() { CardId = "ASCENDERS_BANE", Count = 1 });
                else if (bane.Length != 1)
                    throw new InvalidDataException("原生初始牌组出现多张进阶之灾。");
            }
            else
            {
                foreach (CardModel card in bane)
                {
                    player.Deck.RemoveInternal(card, silent: true);
                    runState.RemoveCard(card);
                }
            }
        }

        private void PrepareGeneratedPotionSlots(Player player)
        {
            if (_generatedScenario is not { } generated) return;
            foreach (var potion in player.PotionSlots.ToArray()) potion?.Discard();
            if (generated.Options.PotionSlotCount is { } slots)
                player.SetMaxPotionCountInternal(slots);
            if (runner._request.Potions.Length > player.PotionSlots.Count)
                throw new InvalidDataException($"生成场景请求{runner._request.Potions.Length}瓶药水，但A{generated.Options.Ascension}实际只有{player.PotionSlots.Count}槽；请减少数量或显式指定potionSlotCount。不会覆盖已有药水。");
        }

        private void CaptureGeneratedLoadout(RunState state, Player player)
        {
            if (_generatedScenario is not { } generated) return;
            if (generated.Options.PlayerCurrentHp is { } hp)
            {
                if (hp > player.Creature.MaxHp)
                    throw new InvalidDataException("指定生命超过获取遗物后的实际最大生命。");
                player.Creature.SetCurrentHpInternal(hp);
            }
            if (state.Players.Count != 1
                || player.Deck.Cards.Any(c => !GeneratedCombatScenario.IsSingleplayerCard(c))
                || player.Relics.Any(r => !GeneratedCombatScenario.IsSingleplayerRelic(r)))
                throw new InvalidDataException("生成场景必须为单人，且实际牌组与遗物不能包含多人专用内容。");
            int expectedBane = generated.Options.IncludeAscendersBane ? 1 : 0;
            if (player.Deck.Cards.Count(c => c is AscendersBane) != expectedBane
                || state.AscensionLevel != generated.Options.Ascension)
                throw new InvalidDataException("生成场景的进阶之灾数量或A等级不符合配置。");
            if (!generated.Options.ApplyRelicObtainEffects)
            {
                int expectedDeck = (generated.Options.IncludeStartingDeck ? player.Character.StartingDeck.Count() : 0)
                    + expectedBane + runner._request.RunCards.Sum(c => c.Count);
                int expectedRelics = (generated.Options.IncludeStartingRelics ? player.Character.StartingRelics.Count : 0)
                    + runner._request.Relics.Length;
                if (player.Deck.Cards.Count != expectedDeck || player.Relics.Count != expectedRelics)
                    throw new InvalidDataException("生成场景的牌组或遗物数量不符合合成装备配置。");
            }
            string[] actualPotions = player.PotionSlots.OfType<PotionModel>().Select(p => p.Id.Entry).ToArray();
            if (!actualPotions.SequenceEqual(runner._request.Potions.Select(p => p.PotionId)))
                throw new InvalidDataException("生成场景的药水槽顺序与配置不一致。");
            runner._writer.WriteGeneratedArtifact("generated-scenario.loadout.json", new
            {
                schemaVersion = 1, generated.CatalogFingerprint,
                seed = state.Rng.StringSeed, characterId = player.Character.Id.Entry,
                ascension = state.AscensionLevel, actIndex = generated.Options.ActIndex,
                encounterId = generated.Encounter.Id.Entry, roomType = generated.Encounter.RoomType,
                relicObtainEffects = generated.Options.ApplyRelicObtainEffects,
                playerHp = player.Creature.CurrentHp, playerMaxHp = player.Creature.MaxHp,
                deck = player.Deck.Cards.Select(c => new { id = c.Id.Entry, c.CurrentUpgradeLevel }).ToArray(),
                relics = player.Relics.Select(r => r.Id.Entry).ToArray(),
                potionSlots = player.PotionSlots.Select(p => p?.Id.Entry).ToArray(),
            });
            runner._completedChecks.Add("GeneratedScenario:LoadoutCounts:Ascension:SingleBane:PotionOrder");
        }

        private void CaptureGeneratedOpening(CombatState combat, Player player)
        {
            if (_generatedScenario is not { } generated) return;
            if (combat.Encounter?.Id != generated.Encounter.Id
                || combat.RunState.CurrentRoom?.RoomType != generated.Encounter.RoomType)
                throw new InvalidDataException("生成场景进入了错误遭遇。");
            _generatedChoices!.AssertConsumed();
            _generatedScenario = generated with
            {
                Options = generated.Options with { SetupChoices = _generatedChoices.Choices.ToArray() }
            };
            runner._writer.WriteGeneratedArtifact("generated-scenario.resolved.json", _generatedScenario.Options);
            runner._writer.WriteGeneratedArtifact("generated-scenario.opening.json", new
            {
                schemaVersion = 1, generated.CatalogFingerprint,
                characterId = player.Character.Id.Entry, encounterId = combat.Encounter!.Id.Entry,
                setupChoices = _generatedChoices.Choices,
                continuation = ContinuationStamp.CaptureLive(combat).StateText,
            });
            runner._completedChecks.Add("GeneratedScenario:NativeOpeningCaptured");
        }
    }
}
