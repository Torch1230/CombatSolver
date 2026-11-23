using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Godot;
using System.Reflection;

namespace CombatSolver;

internal sealed class SolverDisplayNames
{
    private static string? _canonicalCardLanguage;
    private static IReadOnlyDictionary<(string Id, int Upgrade), string>? _canonicalCardNames;
    private static string? _canonicalMonsterLanguage;
    private static IReadOnlyDictionary<string, string>? _canonicalMonsterNames;
    private static string? _pendingWarmupLanguage;
    private static CardModel[]? _pendingWarmupCards;
    private static int _pendingWarmupIndex;
    private static Dictionary<(string Id, int Upgrade), string>? _pendingWarmupCardNames;

    private readonly IReadOnlyDictionary<(string Id, int Upgrade), string> _canonicalCards;
    private readonly Dictionary<(string Id, int Upgrade), string> _combatCards;
    private readonly DingyDaggerDisplayNames _daggerNames;
    private readonly Dictionary<string, string> _potions;
    private readonly Dictionary<string, string> _relics;
    private readonly bool _english;
    private readonly IReadOnlyDictionary<string, string> _monsters;
    private readonly Dictionary<uint, string> _creatures;
    private readonly Dictionary<string, int> _slotOrder;
    private readonly bool _positioned;
    private static readonly PropertyInfo EncounterSlotsProperty = typeof(NCombatRoom).GetProperty(
        "EncounterSlots", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

    private SolverDisplayNames(
        IReadOnlyDictionary<(string Id, int Upgrade), string> canonicalCards,
        Dictionary<(string Id, int Upgrade), string> combatCards,
        DingyDaggerDisplayNames daggerNames,
        Dictionary<string, string> potions,
        Dictionary<string, string> relics,
        IReadOnlyDictionary<string, string> monsters,
        Dictionary<uint, string> creatures,
        Dictionary<string, int> slotOrder,
        bool positioned,
        bool english)
    {
        _canonicalCards = canonicalCards;
        _combatCards = combatCards;
        _daggerNames = daggerNames;
        _potions = potions;
        _relics = relics;
        _english = english;
        _monsters = monsters;
        _creatures = creatures;
        _slotOrder = slotOrder;
        _positioned = positioned;
    }

    /// <summary>
    /// Incrementally warms canonical monster and card display names on idle main-thread frames
    /// (e.g. main menu / map) so the first combat never stalls formatting all ~600 cards at once.
    /// Runs strictly on the main thread because <c>LocManager.SmartFormat</c> is not thread-safe.
    /// </summary>
    internal static void WarmUpNextBatch(int maxCards = 32)
    {
        NGame? game = NGame.Instance;
        if (game == null || (game.MainMenu == null && !RunManager.Instance.IsInProgress))
            return;
        string? language = LocManager.Instance?.Language;
        if (string.IsNullOrEmpty(language))
            return;

        if (_canonicalMonsterNames == null
            || !string.Equals(_canonicalMonsterLanguage, language, StringComparison.Ordinal))
        {
            _ = CanonicalMonsterNames(language);
            return;
        }

        if (_canonicalCardNames != null
            && string.Equals(_canonicalCardLanguage, language, StringComparison.Ordinal))
        {
            return;
        }

        if (_pendingWarmupCards == null
            || _pendingWarmupCardNames == null
            || !string.Equals(_pendingWarmupLanguage, language, StringComparison.Ordinal))
        {
            _pendingWarmupLanguage = language;
            _pendingWarmupCards = ModelDb.AllCards.ToArray();
            _pendingWarmupIndex = 0;
            _pendingWarmupCardNames = new Dictionary<(string Id, int Upgrade), string>(_pendingWarmupCards.Length * 2);
        }

        int end = Math.Min(_pendingWarmupIndex + maxCards, _pendingWarmupCards.Length);
        for (int i = _pendingWarmupIndex; i < end; i++)
            CaptureCardTitles(_pendingWarmupCardNames, _pendingWarmupCards[i], overwrite: false);
        _pendingWarmupIndex = end;

        if (_pendingWarmupIndex >= _pendingWarmupCards.Length)
        {
            _canonicalCardNames = _pendingWarmupCardNames;
            _canonicalCardLanguage = language;
            _pendingWarmupCards = null;
            _pendingWarmupCardNames = null;
            _pendingWarmupLanguage = null;
            _pendingWarmupIndex = 0;
        }
    }

    public static SolverDisplayNames Capture(CombatState state)
    {
        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        PlayerCombatState combat = player.PlayerCombatState
            ?? throw new InvalidOperationException("找不到玩家战斗状态。");
        IEnumerable<CardModel> cards = combat.Hand.Cards
            .Concat(combat.DrawPile.Cards)
            .Concat(combat.DiscardPile.Cards)
            .Concat(combat.ExhaustPile.Cards)
            .Concat(combat.PlayPile.Cards);
        string language = LocManager.Instance.Language;
        IReadOnlyDictionary<(string Id, int Upgrade), string> canonicalCards = CanonicalCardNames(language);
        Dictionary<(string Id, int Upgrade), string> combatCards = [];
        foreach (CardModel card in cards)
            CaptureCardTitles(combatCards, card, overwrite: true);

        IReadOnlyDictionary<string, string> monsterNames = CanonicalMonsterNames(language);
        Dictionary<uint, string> creatureNames = [];
        NCombatRoom? room = NCombatRoom.Instance;
        Control? slots = room == null ? null : (Control?)EncounterSlotsProperty.GetValue(room);
        Dictionary<string, int> slotOrder = [];
        IReadOnlyList<string> encounterSlots = state.Encounter?.Slots ?? [];
        // 只声明在原生场景里的槽位（如 GremlinMercNormal 的 merc/sneaky/fat）不在模型
        // Encounter.Slots 中；没有模型槽位时按场景 Marker 的横坐标冻结顺序。
        IEnumerable<string> orderedSlots = slots == null ? encounterSlots
            : encounterSlots.Count > 0
                ? encounterSlots.OrderBy(slot => slots.GetNode<Marker2D>(slot).GlobalPosition.X)
                : slots.FindChildren("*", nameof(Marker2D), recursive: true, owned: false)
                    .OfType<Marker2D>()
                    .OrderBy(marker => marker.GlobalPosition.X)
                    .Select(marker => marker.Name.ToString());
        foreach (string slot in orderedSlots) slotOrder.TryAdd(slot, slotOrder.Count);
        Dictionary<string, int> enemyTypeCounts = state.Enemies
            .GroupBy(creature => CaptureCreatureBaseName(creature, monsterNames), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Dictionary<string, int> enemyTypeNumbers = new(StringComparer.Ordinal);
        foreach (Creature creature in state.Creatures.OrderBy(creature => NCombatRoom.Instance?.GetCreatureNode(creature)?.GlobalPosition.X ?? 0))
        {
            if (creature.CombatId is not uint combatId)
                continue;
            string baseName = CaptureCreatureBaseName(creature, monsterNames);
            if (creature.Side == CombatSide.Enemy
                && enemyTypeCounts.GetValueOrDefault(baseName) > 1)
            {
                string typeKey = baseName;
                int number = enemyTypeNumbers.GetValueOrDefault(typeKey) + 1;
                enemyTypeNumbers[typeKey] = number;
                bool positioned = NCombatRoom.Instance?.GetCreatureNode(creature) != null;
                string position = positioned
                    ? LocManager.Instance.Language is "zhs" or "zht" ? $"左起{number}" : $"#{number} from left"
                    : $"#{number}";
                creatureNames[combatId] = $"{baseName}（{position}）";
            }
            else
            {
                creatureNames[combatId] = baseName;
            }
        }
        // A player can legally hold more than one potion of the same model. Display names are
        // type-scoped, while planning and deployment continue to distinguish the actual slots.
        Dictionary<string, string> potionNames = new(StringComparer.Ordinal);
        foreach (PotionModel potion in player.Potions)
            potionNames.TryAdd(potion.Id.Entry, potion.Title.GetFormattedText());
        Dictionary<string, string> relicNames = new(StringComparer.Ordinal);
        foreach (RelicModel relic in player.Relics)
            relicNames.TryAdd(relic.Id.Entry, relic.Title.GetFormattedText());
        return new SolverDisplayNames(canonicalCards, combatCards, DingyDaggerDisplayNames.Capture(), potionNames, relicNames, monsterNames, creatureNames,
            slotOrder, room != null,
            language is not ("zhs" or "zht"));
    }

    public string Card(CardModel card)
        => Card(card.Id.Entry, card.CurrentUpgradeLevel);

    public string Card(string cardId, int upgradeLevel = 0)
    {
        if (cardId == "DINGY_DAGGER")
            return _daggerNames.Format(upgradeLevel);
        (string Id, int Upgrade) key = (cardId, upgradeLevel);
        if (_combatCards.TryGetValue(key, out string? combatTitle))
            return combatTitle;
        return _canonicalCards.GetValueOrDefault(key, cardId);
    }

    private static IReadOnlyDictionary<string, string> CanonicalMonsterNames(string language)
    {
        if (_canonicalMonsterNames == null
            || !string.Equals(_canonicalMonsterLanguage, language, StringComparison.Ordinal))
        {
            Dictionary<string, string> monsterNames = new(StringComparer.Ordinal);
            foreach (MonsterModel monster in ModelDb.Monsters)
                monsterNames.TryAdd(monster.Id.Entry, monster.Title.GetFormattedText());
            _canonicalMonsterNames = monsterNames;
            _canonicalMonsterLanguage = language;
        }
        return _canonicalMonsterNames;
    }

    private static IReadOnlyDictionary<(string Id, int Upgrade), string> CanonicalCardNames(string language)
    {
        if (_canonicalCardNames == null || !string.Equals(_canonicalCardLanguage, language, StringComparison.Ordinal))
        {
            Dictionary<(string Id, int Upgrade), string> names;
            if (_pendingWarmupCards != null
                && _pendingWarmupCardNames != null
                && string.Equals(_pendingWarmupLanguage, language, StringComparison.Ordinal))
            {
                names = _pendingWarmupCardNames;
                for (int i = _pendingWarmupIndex; i < _pendingWarmupCards.Length; i++)
                    CaptureCardTitles(names, _pendingWarmupCards[i], overwrite: false);
                _pendingWarmupCards = null;
                _pendingWarmupCardNames = null;
                _pendingWarmupLanguage = null;
                _pendingWarmupIndex = 0;
            }
            else
            {
                names = [];
                foreach (CardModel canonical in ModelDb.AllCards)
                    CaptureCardTitles(names, canonical, overwrite: false);
            }
            _canonicalCardNames = names;
            _canonicalCardLanguage = language;
        }

        return _canonicalCardNames;
    }

    private static void CaptureCardTitles(
        IDictionary<(string Id, int Upgrade), string> names,
        CardModel source,
        bool overwrite)
    {
        CardModel card = source.IsMutable
            ? (CardModel)source.ClonePreservingMutability()
            : source.ToMutable();
        while (true)
        {
            (string Id, int Upgrade) key = (card.Id.Entry, card.CurrentUpgradeLevel);
            if (overwrite || !names.ContainsKey(key))
                names[key] = card.Title;
            if (card is DingyDagger || !card.IsUpgradable)
                return;
            card.UpgradeInternal();
            card.FinalizeUpgradeInternal();
        }
    }

    public string Potion(PotionModel potion)
        => _potions.GetValueOrDefault(potion.Id.Entry, potion.Id.Entry);

    public string Potion(string potionId)
        => _potions.GetValueOrDefault(potionId, potionId);

    public string Relic(string relicId)
        => _relics.GetValueOrDefault(relicId, relicId);

    public string Monster(string monsterId)
        => _monsters.GetValueOrDefault(monsterId, monsterId);

    public string Creature(Creature? creature) => Creature(creature, null);

    public string Creature(Creature? creature, IReadOnlyList<Creature>? knownEnemies)
    {
        if (creature is null)
            return string.Empty;
        string fallback = creature.Monster?.Id.Entry is { } monsterId
            ? Monster(monsterId)
            : creature.Player?.Character?.Id.Entry ?? "玩家";
        if (creature.CombatId is uint id && _creatures.TryGetValue(id, out string? captured))
            return captured;
        if (knownEnemies == null || creature.Side != CombatSide.Enemy)
            return fallback;
        // Slot ranks are frozen from the native scene. Unslotted encounters use
        // insertion order, as native PositionEnemies does. KnownEnemies retains
        // dead participants so the labels stay stable through the route. A predicted
        // summon may carry a scene-only slot beyond the frozen ranks; it keeps the
        // roster insertion order after known slots instead of failing the name read.
        var peers = knownEnemies.Where(enemy => CaptureCreatureBaseName(enemy, _monsters) == fallback)
            .OrderBy(enemy => enemy.SlotName is { } slot
                ? _slotOrder.GetValueOrDefault(slot, int.MaxValue)
                : 0).ToArray();
        int index = Array.FindIndex(peers, enemy => enemy.CombatId == creature.CombatId);
        if (index < 0) throw new InvalidOperationException("生成敌人缺少所属预测阵容，无法确定显示位置。");
        string position = _positioned
            ? _english ? $"#{index + 1} from left" : $"左起{index + 1}"
            : $"#{index + 1}";
        return $"{fallback}（{position}）";
    }

    private static string CreatureTypeKey(Creature creature)
        => creature.Monster?.Id.Entry ?? creature.Player?.Character?.Id.Entry ?? "PLAYER";

    private static string CaptureCreatureBaseName(
        Creature creature,
        IReadOnlyDictionary<string, string> monsterNames)
        => creature.Monster?.Id.Entry is { } monsterId
            ? monsterNames.GetValueOrDefault(monsterId, monsterId)
            : creature.Name;
}
