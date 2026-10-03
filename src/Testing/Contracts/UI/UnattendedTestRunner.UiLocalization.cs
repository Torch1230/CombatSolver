using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertLoopDisplayAsync(bool english)
    {
        PlanAction targeted = new(PlanActionKind.PlayCard, 1, CardId: "STRIKE_IRONCLAD",
            TargetIndex: 0, TargetCombatId: 10, TargetName: "Enemy");
        SolverOverlayActionSnapshot targetDisplay = SolverOverlaySnapshot.CaptureAction(targeted, []);
        SolverOverlayActionSnapshot otherCopy = SolverOverlaySnapshot.CaptureAction(
            targeted with { CardOccurrence = 1, TargetIndex = 2 }, []);
        SolverOverlayActionSnapshot otherTarget = SolverOverlaySnapshot.CaptureAction(
            targeted with { TargetCombatId = 11 }, []);
        if (!targetDisplay.HasSamePresentation(otherCopy)
            || targetDisplay.HasSamePresentation(otherTarget)
            || CombatBeamSolver.BuildCycleActionKey(targeted)
                == CombatBeamSolver.BuildCycleActionKey(targeted with { CardOccurrence = 1 }))
            throw new InvalidOperationException("Loop display must ignore physical copy indexes and retain target identity without changing search keys.");
        SolverOverlayActionSnapshot a = SolverOverlaySnapshot.CaptureAction(
            new PlanAction(PlanActionKind.PlayCard, 1, CardId: "IMPATIENCE"), []);
        SolverOverlayActionSnapshot b = SolverOverlaySnapshot.CaptureAction(
            new PlanAction(PlanActionKind.PlayCard, 1, CardId: "FINESSE", ReplayCount: 2), []);
        SolverOverlayTurnSnapshot turn = new(1, [],
            Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? a : b)
                .Append(a with { Kills = ["Enemy"] }).ToArray(), null, 5, 0, 0, 0, true);
        SolverRouteRow row = new(0);
        _host.AddChild(row);
        try
        {
            row.Populate(turn);
            if (row.DeploymentActionCount != 41 || row.ActionFlow.GetChildCount() != 2)
                throw new InvalidOperationException("Loop folding lost executable indexes or kill suffix.");
            var group = (SolverLoopGroup)row.ActionFlow.GetChild(0);
            var actions = group.Actions;
            Label count = group.BadgeLabel;
            if (actions.GetChildCount() != 3 || !count.Text.Contains("20")
                || !group.TooltipText.Contains(english ? "Repeat" : "重复"))
                throw new InvalidOperationException("Loop badge localization failed.");
            foreach (int index in new[] { 0, 1, 2, 37, 38, 39 })
            {
                row.SetDeploymentProgress(index, index);
                row.SettleDeploymentMotionForTesting();
                if (((CanvasItem)actions.GetChild(index % 2)).Modulate
                    != SolverUiTokens.Palette.ActiveActionModulate)
                    throw new InvalidOperationException("Repeated action highlight lost its modulo mapping.");
            }
            row.SetDeploymentProgress(40, 40);
            row.SettleDeploymentMotionForTesting();
            if (((CanvasItem)actions.GetChild(0)).Modulate != SolverUiTokens.Palette.CompletedActionModulate
                || ((CanvasItem)actions.GetChild(1)).Modulate != SolverUiTokens.Palette.CompletedActionModulate
                || group.Badge.Modulate != SolverUiTokens.Palette.CompletedActionModulate
                || ((CanvasItem)row.ActionFlow.GetChild(1)).Modulate != SolverUiTokens.Palette.ActiveActionModulate)
                throw new InvalidOperationException("Loop completion or suffix highlight failed.");
            row.SetDeploymentProgress(41, null);
            if (!SolverRouteRow.ExerciseDeploymentPacingForTesting())
                throw new InvalidOperationException("Deployment highlight pacing does not follow the step interval.");
            ulong id = row.ActionFlow.GetChild(0).GetInstanceId();
            row.Populate(turn with { Actions = turn.Actions.ToArray() });
            if (id != row.ActionFlow.GetChild(0).GetInstanceId()
                || actions.GetChildren().Cast<CanvasItem>().Any(pill => pill.Modulate != Colors.White))
                throw new InvalidOperationException("Folded row reuse failed.");
            // Exercise wide -> narrow -> wide on live Godot containers: the enclosing
            // frame must resize with the flow, without separating the count or clipping actions.
            foreach (bool narrow in new[] { false, true, false })
            {
                row.Size = new Vector2(narrow ? row.GetCombinedMinimumSize().X : 1400, 0);
                for (int frame = 0; frame < 6; frame++)
                    await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
                Control first = actions.GetChild<Control>(0);
                Control second = actions.GetChild<Control>(1);
                bool wrapped = second.Position.Y > first.Position.Y;
                if (wrapped != narrow
                    || !group.GetGlobalRect().Encloses(first.GetGlobalRect())
                    || !group.GetGlobalRect().Encloses(second.GetGlobalRect())
                    || !group.GetGlobalRect().Encloses(count.GetGlobalRect())
                    || group.Size.X > row.ActionFlow.Size.X + 1)
                    throw new InvalidOperationException($"Loop group layout failed: narrow={narrow}, wrapped={wrapped}, group={group.Size}, flow={row.ActionFlow.Size}.");
                if (!narrow && (group.Size.X >= row.ActionFlow.Size.X - 1
                    || group.Size.Y > Mathf.Max(first.Size.Y, second.Size.Y) + 5
                    || count.GlobalPosition.X < second.GetGlobalRect().End.X
                    || row.ActionFlow.GetChild<Control>(1).Position.Y > group.Position.Y + 1))
                    throw new InvalidOperationException($"Loop group should fit content on one line with its suffix: group={group.Size}, flow={row.ActionFlow.Size}.");
            }
            _completedChecks.Add($"LoopDisplay:{(english ? "eng" : "zh")}:41Actions:2OuterControls:CompactInlineCount:EnclosedPeriod:WideNarrowWide:ReplayCount:KillSuffix:Deployment:Reuse");
        }
        finally { row.Free(); }
    }

    private async Task AssertUiLocalizationAsync(CombatState combat)
    {
        using Stream stream = typeof(SolverText).Assembly.GetManifestResourceStream("CombatSolver.UI.English.json")!;
        var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        foreach ((string source, string english) in catalog)
        {
            CompositeFormat original = CompositeFormat.Parse(source);
            CompositeFormat translated = CompositeFormat.Parse(english);
            string[] Fields(string value) => Regex.Matches(value, @"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}")
                .Select(match => match.Value).Order().ToArray();
            if (original.MinimumArgumentCount != translated.MinimumArgumentCount
                || !Fields(source).SequenceEqual(Fields(english)))
                throw new InvalidOperationException($"Localization placeholders differ: {source}");
        }
        string language = LocManager.Instance.Language;
        try
        {
            await AssertCardLanguageRoundTripAsync();
            foreach (string target in new[] { "eng", "zhs", "zht" })
            {
                LocManager.Instance.SetLanguage(target);
                await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
                bool english = target == "eng";
                var displayNames = SolverDisplayNames.Capture(combat);
                CombatRootSnapshot nameRoot = CombatRootSnapshot.Capture(combat);
                using (SimulationNotificationIsolation.Enter())
                {
                    CombatPredictionSimulator simulator = nameRoot.ForkSimulator();
                    var simulated = (SimulatedCombatState)simulator.State.CombatState;
                    bool positionedSpawn = combat.Encounter?.Slots.Contains("wriggler1") == true;
                    var spawned = Enumerable.Range(1, 4).Reverse().Select(number =>
                        MonsterSpawnSupport.Spawn<MegaCrit.Sts2.Core.Models.Monsters.Wriggler>(
                            simulator, simulated, combat.Enemies[0], slot: positionedSpawn ? $"wriggler{number}" : null)).ToArray();
                    string[] names = spawned.Select(creature => displayNames.Creature(creature, simulated.KnownEnemies)).ToArray();
                    var child = simulator.Fork();
                    var childCombat = (SimulatedCombatState)child.State.CombatState;
                    if (names.Distinct(StringComparer.Ordinal).Count() != 4
                        || spawned.Where((creature, index) =>
                            names[index] != displayNames.Creature(
                                childCombat.KnownEnemies.Single(enemy => enemy.CombatId == creature.CombatId), childCombat.KnownEnemies)
                            || !names[index].Contains(english ? "from left" : "左起")).Any())
                        throw new InvalidOperationException("Spawned targets must retain left-to-right labels across Fork.");
                    if (positionedSpawn)
                    {
                        var scene = (Control)typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom)
                            .GetProperty("EncounterSlots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .GetValue(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance)!;
                        var ordered = spawned.OrderBy(creature => scene.GetNode<Marker2D>(creature.SlotName!).GlobalPosition.X).ToArray();
                        for (int index = 0; index < ordered.Length; index++)
                        {
                            string expected = english ? $"#{index + 1} from left" : $"左起{index + 1}";
                            if (!displayNames.Creature(ordered[index], simulated.KnownEnemies).Contains(expected))
                                throw new InvalidOperationException("Spawn label order differs from native slot coordinates.");
                        }
                        simulated.RemoveCreature(ordered[0]);
                        if (spawned.Where((creature, index) => names[index] != displayNames.Creature(creature, simulated.KnownEnemies)).Any())
                            throw new InvalidOperationException("Removing a defeated enemy renumbered the remaining targets.");
                    }
                    _completedChecks.Add($"SpawnedEntityIdentity:{target}:FourWrigglers:LeftToRight:NativeSlots={positionedSpawn}:ForkStable:DeathStable");
                }
                if (combat.Enemies.Select(displayNames.Creature).Distinct().Count() != combat.Enemies.Count)
                    throw new InvalidOperationException("Enemy display names are ambiguous.");
                var plainShiv = SolverOverlaySnapshot.CaptureAction(new PlanAction(PlanActionKind.PlayCard, 1, CardId: "SHIV"), []);
                var inkyShiv = SolverOverlaySnapshot.CaptureAction(new PlanAction(PlanActionKind.PlayCard, 1, CardId: "SHIV", CardEnchantmentId: "INKY"), []);
                if (plainShiv.Title == inkyShiv.Title || !inkyShiv.Tooltip.Contains(ModelDb.Enchantment<MegaCrit.Sts2.Core.Models.Enchantments.Inky>().Title.GetFormattedText()))
                    throw new InvalidOperationException("Inky Shiv lost its enchantment display.");
                _completedChecks.Add($"EntityIdentity:{target}:DistinctEnemies={combat.Enemies.Count}:PlainAndInkyShiv");
                var counters = RelicCounterPolicy.Add(default, new(RelicCounterId.HappyFlower, 2, 2, 0, 3), 2);
                counters = RelicCounterPolicy.Add(counters, new(RelicCounterId.PenNib, 7, 7, 0, 10), 4);
                string outcome = SolverStrategyOutcomeText.Format(counters, new GrowthValues(TheHunt: 1, Feed: 2), true)!;
                string incomplete = SolverStrategyOutcomeText.Format(counters, default, false)!;
                if (!outcome.Contains(english ? "Counters aligned:" : "已卡：")
                    || !outcome.Contains(ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.HappyFlower>().Title.GetFormattedText() + " 2")
                    || !outcome.Contains(ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.PenNib>().Title.GetFormattedText() + (english ? " 7, actual 4" : " 7 实际 4"))
                    || !outcome.Contains(english ? "Targets unmet:" : "未达标：")
                    || !outcome.Contains(ModelDb.Card<TheHunt>().Title)
                    || !outcome.Contains(ModelDb.Card<Feed>().Title + " ×2")
                    || incomplete.Contains(english ? "Counters aligned:" : "已卡：")
                    || SolverStrategyOutcomeText.Format(default, default, true) != null)
                    throw new InvalidOperationException("Strategy outcome omitted achieved/unmet targets or marked partial counters complete.");
                var entries = SolverStrategyOutcomeText.Capture(counters, new GrowthValues(TheHunt: 1), true);
                if (entries.Count != 3 || !entries[0].Satisfied || entries[1].Satisfied || !entries[2].Satisfied
                    || outcome.Contains(english ? "Projected outcome" : "预计路线结果"))
                    throw new InvalidOperationException("Strategy outcome status colors or heading removal changed.");
                _completedChecks.Add($"StrategyOutcome:{target}:AlignedAndUnmet:GrowthCounts:PartialRoute:EmptyHidden");
                await AssertActionAnnotationLocalizationAsync(combat, english);
                AssertLiveTurnStartChoicePreview(english);
                await AssertLoopDisplayAsync(english);
                foreach ((string source, string translated) in catalog)
                {
                    if (SolverText.Get(source) != (english ? translated : source))
                        throw new InvalidOperationException($"Wrong locale selection: {target}/{source}");
                }
                string untouched = "玩家{0}[b]STRIKE[/b]";
                if (SolverText.Format($"联系QQ：{untouched}（可在“求解器设置”里修改）")
                    != (english ? $"QQ: {untouched} (change in Solver Settings)" : $"联系QQ：{untouched}（可在“求解器设置”里修改）"))
                    throw new InvalidOperationException("Localization changed inserted text.");
                string expectedNumber = (1.25).ToString("F1", CultureInfo.CurrentCulture);
                if (SolverText.Format($"已用 {1.25:F1} s") != (english ? $"Elapsed {expectedNumber} s" : $"已用 {expectedNumber} s"))
                    throw new InvalidOperationException("Localization changed numeric formatting.");

                Control harness = new() { Size = new Vector2(820, 900) };
                _host.AddChild(harness);
                try
                {
                    foreach (Func<Control> createPanel in new Func<Control>[]
                    {
                        () => new SolverPotionStrategyPanel(),
                        () => new SolverGrowthStrategyPanel(),
                        () => new SolverRelicStrategyPanel(),
                    })
                    {
                        Control panel = createPanel();
                        harness.AddChild(panel);
                        Button? close = panel.FindChild("CloseStrategyPanel", recursive: true, owned: false) as Button;
                        if (close?.Text != (english ? "Collapse" : "收起"))
                            throw new InvalidOperationException($"Strategy panel collapse localization failed: {target}/{panel.Name}.");
                    }
                    _completedChecks.Add($"StrategyPanels:{target}:Potion:Growth:Relic:Collapse");
                    SolverSettingsPanel settings = new();
                    harness.AddChild(settings);
                    settings.Reload();
                    if (!settings.SettingsTabsConfiguredForTesting || !settings.UploadProgressConfiguredForTesting
                        || !settings.PotionRewardPredictionConfiguredForTesting
                        || !settings.ExerciseSettingsTabSwitchingForTesting())
                        throw new InvalidOperationException($"Settings localization failed: {target}");
                    BugReportUploadDialog dialog = new("");
                    harness.AddChild(dialog);
                    if (!dialog.ExerciseResponsiveBoundsForTesting())
                        throw new InvalidOperationException($"Upload dialog bounds failed: {target}");
                    if (english)
                        AssertEnglishControls(harness);
                    if (!settings.ExerciseUploadCompletionTransitionForTesting())
                        throw new InvalidOperationException($"Upload transitions failed: {target}");
                }
                finally { harness.Free(); }

                SolverOverlay.ShowSearching(_host, 3, false, 0);
                if (SolverOverlay.RouteHeadingForTesting != (english ? "Current candidate (unverified)" : "求解器当前考虑（尚未验证）")
                    || SolverOverlay.AdoptRouteButtonTextForTesting != (english ? "Use candidate" : "采用当前路线"))
                    throw new InvalidOperationException($"Dynamic overlay localization failed: {target}");
                SolverOverlay.ShowSearching(_host, 3, false, 0,
                    new PotionRewardOutlook(0.4f, true, false)
                    {
                        Enabled = true,
                        Forecast = PotionRewardForecast.Drop,
                        ForecastPotionId = "SWIFT_POTION",
                    });
                if (SolverOverlay.RewardOutcomeTextForTesting?.StartsWith(
                        english ? "Expected drop: " : "预计掉落：", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException($"Searching reward forecast missing: {target}");
                SolverOverlay.ShowSearching(_host, 3, false, 0,
                    new PotionRewardOutlook(0.4f, true, false)
                    {
                        Enabled = true,
                        Forecast = PotionRewardForecast.NoDrop,
                    });
                if (SolverOverlay.RewardOutcomeTextForTesting != (english
                        ? "No potion drop expected" : "预计不掉落药水"))
                    throw new InvalidOperationException($"Searching no-drop forecast missing: {target}");
                SolverOverlay.ShowSearching(_host, 3, false, 0);
                if (SolverOverlay.RewardOutcomeTextForTesting != null)
                    throw new InvalidOperationException($"Disabled reward forecast remained visible: {target}");
                if (!SolverOverlay.ExerciseGuidanceHintsForTesting())
                    throw new InvalidOperationException($"Guidance banner localization or dismissal failed: {target}");
                string failure = SolverController.FormatSearchFailureForTesting(new InvalidOperationException(untouched), true);
                if (!failure.Contains(english ? "Search failed" : "计算失败", StringComparison.Ordinal)
                    || !failure.Contains(english ? "Off (one thread)" : "关闭（单线程）", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Failure instructions are not localized: {target}");
                _completedChecks.Add($"UiLocalization:{target}:Catalog{catalog.Count}:Settings:UploadTransitions:DynamicStatus:FailureInstructions");
            }
        }
        finally
        {
            LocManager.Instance.SetLanguage(language);
            SolverOverlay.Hide();
        }
    }

    private void AssertLiveTurnStartChoicePreview(bool english)
    {
        PlanCardChoice powerChoice = new(PlanChoiceEffect.Discard, PileType.Hand, [], "TOOLS_OF_THE_TRADE_POWER");
        PlanCardChoice relicChoice = new(PlanChoiceEffect.Exhaust, PileType.Hand, [], "TOASTY_MITTENS");
        PlanAction[] actions =
        [
            new(PlanActionKind.EndTurn, 1, TurnStartChoices: [powerChoice]),
            new(PlanActionKind.EndTurn, 2, TurnStartChoices: [relicChoice]),
            new(PlanActionKind.EndTurn, 3),
        ];
        SolverFrontierTurn[] turns =
        [
            new(1, [actions[0]], 0, 0, 0, 0, false)
            {
                TurnStartChoices = SolverFrontierTurn.ChoicesForTurn(1, 1, [relicChoice], actions),
            },
            new(2, [actions[1]], 0, 0, 0, 0, false)
            {
                TurnStartChoices = SolverFrontierTurn.ChoicesForTurn(2, 1, [relicChoice], actions),
            },
            new(3, [actions[2]], 0, 0, 0, 0, false)
            {
                TurnStartChoices = SolverFrontierTurn.ChoicesForTurn(3, 1, [relicChoice], actions),
            },
        ];
        SolverSpeculativeRoutePreview route = new(1, 1, 0, 0, false, false, false, turns);
        SolverOverlayTurnSnapshot[] shown = SolverOverlaySnapshot.CaptureSpeculativeRoute(route).Turns.ToArray();
        if (!shown[0].TurnStartChoices.Any(choice => choice.Contains(
                ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.ToastyMittens>().Title.GetFormattedText(), StringComparison.Ordinal))
            || !shown[1].TurnStartChoices.Any(choice => choice.Contains(
                ModelDb.Power<ToolsOfTheTradePower>().Title.GetFormattedText(), StringComparison.Ordinal))
            || !shown[2].TurnStartChoices.Any(choice => choice.Contains(
                ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.ToastyMittens>().Title.GetFormattedText(), StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Live route omitted turn-start power/relic choice: {string.Join('|', shown.SelectMany(turn => turn.TurnStartChoices))}");
        }
        SolverCurrentTurnPreview current = new(1, 1, [actions[0]], 0, 0, 0, 0, false, turns)
        {
            TurnStartChoices = [relicChoice],
        };
        if (!SolverOverlaySnapshot.CaptureCurrentTurn(current).Turns[0].TurnStartChoices.SequenceEqual(
                shown[0].TurnStartChoices)
            || SolverOverlaySnapshot.CaptureSpeculativeRoute(route with
            {
                Turns = [turns[0], turns[1] with { TurnStartChoices = [] }, turns[2]],
            }).Turns[1].TurnStartChoices.Count != 0)
        {
            throw new InvalidOperationException("Live frontier or switched candidate kept stale turn-start choices.");
        }
        _completedChecks.Add($"LiveTurnStartChoicePreview:{(english ? "eng" : "zhs/zht")}:Power:Relic");
    }

    private async Task AssertActionAnnotationLocalizationAsync(CombatState combat, bool english)
    {
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        var sources = new[]
        {
            (CombatDamageSource.For(CombatDamageSourceKind.Poison), ModelDb.Power<PoisonPower>().Title.GetFormattedText()),
            (CombatDamageSource.For(CombatDamageSourceKind.Thorns), ModelDb.Power<ThornsPower>().Title.GetFormattedText()),
            (CombatDamageSource.For(CombatDamageSourceKind.Power, nameof(DoomPower)), ModelDb.Power<DoomPower>().Title.GetFormattedText()),
            (CombatDamageSource.For(CombatDamageSourceKind.Power, ModelDb.Power<DoomPower>().Id.Entry), ModelDb.Power<DoomPower>().Title.GetFormattedText()),
            (CombatDamageSource.For(CombatDamageSourceKind.Orb, nameof(LightningOrb)), ModelDb.Orb<LightningOrb>().Title.GetFormattedText()),
            (CombatDamageSource.For(CombatDamageSourceKind.MonsterMove), english ? "Enemy move" : "敌方行动"),
            (CombatDamageSource.Unknown, english ? "Unknown effect" : "未知效果"),
        };
        string language = LocManager.Instance.Language;
        try
        {
            LocManager.Instance.SetLanguage(english ? "zhs" : "eng");
            foreach (var (source, expected) in sources)
                if (await Task.Run(() => names.DamageSource(source)) != expected)
                    throw new InvalidOperationException("Damage-source names were not captured before worker execution.");
        }
        finally { LocManager.Instance.SetLanguage(language); }

        var effects = new (string Source, string English)[]
        {
            ("：格挡+3", ": Block +3"), ("：格挡-1", ": Block -1"), ("：格挡×1.5", ": Block ×1.5"),
            ("：抽1", ": Draw 1"), ("：敏捷+2", ": Dexterity +2"), ("：能量+1", ": Energy +1"),
            ("：伤害4", ": Damage 4"), ("：伤害+4", ": Damage +4"), ("：伤害×2", ": Damage ×2"),
            ("：全体伤害3", ": Damage to all 3"), ("：力量+1", ": Strength +1"),
            ("：力量+1 敏捷+2", ": Strength +1 Dexterity +2"),
            ("：手牌0费", ": Hand costs 0"), ("：复制到手牌", ": Copy to hand"), ("：升级", ": Upgrade"),
            ("：本张免费", ": Free card"),
            ("：额外回合", ": Extra turn"), ("：复活", ": Revive"), ("×2", "×2"), ("", ""),
            ("第三方：力量宝珠", "第三方：力量宝珠"),
        };
        foreach (var (source, translated) in effects)
        {
            if (SolverRelicEffectText.Format(source) != (english ? translated : source))
                throw new InvalidOperationException($"Relic effect translation failed: {source}");
        }
        PlanCardChoice skip = new(PlanChoiceEffect.Discard, PileType.Hand, []);
        PlanCardChoice choose = skip with { Cards = [new("STRIKE_IRONCLAD", 0, "", 0, 0, "Strike")] };
        PlanAction action = new(PlanActionKind.UsePotion, 1, PotionTitle: "Potion", TargetName: "Enemy",
            Choice: skip, NestedChoices: [choose], NestedChoicesBeforePrimary: 1,
            RelicEffects: [new("TEST_RELIC", "Relic", "：力量+1 敏捷+2")]);
        string kill = $"Enemy（{names.DamageSource(CombatDamageSource.For(CombatDamageSourceKind.Poison))}）";
        SolverOverlayActionSnapshot snapshot = SolverOverlaySnapshot.CaptureAction(action, [kill]);
        if (snapshot.RelicLabels.Single() != "Relic" + (english ? ": Strength +1 Dexterity +2" : "：力量+1 敏捷+2")
            || snapshot.ChoiceText != (english ? $"Choose {ModelDb.Card<StrikeIronclad>().Title} / Skip choice" : $"选 {ModelDb.Card<StrikeIronclad>().Title} / 不选")
            || !snapshot.Tooltip.Contains(snapshot.RelicLabels[0], StringComparison.Ordinal)
            || !snapshot.Tooltip.Contains(english ? "(Potion)" : "（药水）", StringComparison.Ordinal)
            || snapshot.Kills.Single() != kill)
            throw new InvalidOperationException("Secondary capsule labels and tooltips disagree.");
        _completedChecks.Add($"ActionAnnotations:{language}:CapturedDamageSources:{effects.Length}RelicFormats:NestedChoices:Tooltip");
    }

    private async Task AssertCardLanguageRoundTripAsync()
    {
        LocManager.Instance.SetLanguage("eng");
        CardModel upgraded = ModelDb.Card<StrikeIronclad>().ToMutable();
        upgraded.UpgradeInternal();
        upgraded.FinalizeUpgradeInternal();
        PlanAction plan = new(PlanActionKind.PlayCard, 1, CardId: upgraded.Id.Entry,
            CardTitle: upgraded.Title, CardUpgradeLevel: 1,
            Choice: new PlanCardChoice(PlanChoiceEffect.Discard, PileType.Hand,
                [new(upgraded.Id.Entry, 1, "unchanged", 0, 0, upgraded.Title)]));
        string serialized = JsonSerializer.Serialize(plan);
        PlanAction restored = JsonSerializer.Deserialize<PlanAction>(serialized)!;
        SolverOverlayActionSnapshot englishSnapshot = SolverOverlaySnapshot.CaptureAction(restored, []);
        Control pill = SolverActionPill.Create(englishSnapshot);
        int subscriptions = SolverLocaleRefresh.SubscriptionCountForTesting;
        int replans = SolverController.UnexpectedReplanCount;
        bool searching = SolverController.IsSearching;
        _host.AddChild(pill);
        try
        {
            foreach (string language in new[] { "zhs", "eng", "zhs" })
            {
                LocManager.Instance.SetLanguage(language);
                await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
                await _host.ToSignal(_host.GetTree(), SceneTree.SignalName.ProcessFrame);
                string expected = upgraded.Title;
                Label title = (Label)pill.GetChild(0).GetChild(1);
                if (title.Text != expected || !pill.TooltipText.Contains(expected, StringComparison.Ordinal)
                    || !SolverActionTextIdentity.Refresh(englishSnapshot).ChoiceText!.Contains(expected, StringComparison.Ordinal)
                    || SolverOverlaySnapshot.CaptureAction(restored, []).Title != expected)
                    throw new InvalidOperationException($"Retained or restored card name stayed in the previous language: {language}");
                if (restored.CardTitle != plan.CardTitle || restored.CardUpgradeLevel != 1
                    || JsonSerializer.Serialize(restored) != serialized)
                    throw new InvalidOperationException("Locale refresh mutated the saved plan.");
            }
            if (SolverController.UnexpectedReplanCount != replans || SolverController.IsSearching != searching)
                throw new InvalidOperationException("Locale refresh changed search state.");
        }
        finally { pill.Free(); }
        if (SolverLocaleRefresh.SubscriptionCountForTesting != subscriptions)
            throw new InvalidOperationException("Freed pill retained a locale subscription.");
        _completedChecks.Add("CardLocaleRoundTrip:EnglishSnapshot:ChineseEnglishChinese:Upgrade:Choice:SerializedPlan:LivePill:SubscriptionCleanup:NoReplan");
    }

    private static void AssertEnglishControls(Node node)
    {
        static void Check(string text)
        {
            if (text.Any(character => character is >= '\u4e00' and <= '\u9fff'))
                throw new InvalidOperationException($"Untranslated English control: {text}");
        }
        if (node is Control control) Check(control.TooltipText);
        if (node is Label label) Check(label.Text);
        if (node is Button button) Check(button.Text);
        if (node is LineEdit input) Check(input.PlaceholderText);
        if (node is OptionButton options)
            for (int index = 0; index < options.ItemCount; index++) Check(options.GetItemText(index));
        foreach (Node child in node.GetChildren()) AssertEnglishControls(child);
    }
}
