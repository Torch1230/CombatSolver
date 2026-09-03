using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed class SolverDisplayNames
{
    private static string? _canonicalCardLanguage;
    private static Dictionary<(string Id, int Upgrade), string>? _canonicalCardNames;

    private readonly Dictionary<(string Id, int Upgrade), string> _cards;
    private readonly Dictionary<string, string> _potions;
    private readonly Dictionary<string, string> _relics;
    private readonly Dictionary<string, string> _powers;
    private readonly Dictionary<string, string> _monsters;
    private readonly Dictionary<uint, string> _creatures;

    private SolverDisplayNames(
        Dictionary<(string Id, int Upgrade), string> cards,
        Dictionary<string, string> potions,
        Dictionary<string, string> relics,
        Dictionary<string, string> powers,
        Dictionary<string, string> monsters,
        Dictionary<uint, string> creatures)
    {
        _cards = cards;
        _potions = potions;
        _relics = relics;
        _powers = powers;
        _monsters = monsters;
        _creatures = creatures;
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
        Dictionary<(string Id, int Upgrade), string> cardNames = CanonicalCardNames();
        foreach (CardModel card in cards)
            CaptureCardTitles(cardNames, card, overwrite: true);

        Dictionary<string, string> monsterNames = new(StringComparer.Ordinal);
        foreach (MonsterModel monster in ModelDb.Monsters)
            monsterNames.TryAdd(monster.Id.Entry, monster.Title.GetFormattedText());
        Dictionary<uint, string> creatureNames = [];
        Dictionary<string, int> enemyTypeCounts = state.Enemies
            .GroupBy(CreatureTypeKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Dictionary<string, int> enemyTypeNumbers = new(StringComparer.Ordinal);
        foreach (Creature creature in state.Creatures)
        {
            if (creature.CombatId is not uint combatId)
                continue;
            string baseName = CaptureCreatureBaseName(creature, monsterNames);
            if (creature.Side == CombatSide.Enemy
                && enemyTypeCounts.GetValueOrDefault(CreatureTypeKey(creature)) > 1)
            {
                string typeKey = CreatureTypeKey(creature);
                int number = enemyTypeNumbers.GetValueOrDefault(typeKey) + 1;
                enemyTypeNumbers[typeKey] = number;
                creatureNames[combatId] = $"{baseName} {number}";
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
        Dictionary<string, string> powerNames = new(StringComparer.Ordinal);
        foreach (PowerModel power in ModelDb.AllPowers)
            powerNames.TryAdd(power.Id.Entry, power.Title.GetFormattedText());
        return new SolverDisplayNames(cardNames, potionNames, relicNames, powerNames, monsterNames, creatureNames);
    }

    public string Card(CardModel card)
        => _cards.GetValueOrDefault((card.Id.Entry, card.CurrentUpgradeLevel), card.Id.Entry);

    public string Card(string cardId, int upgradeLevel = 0)
        => _cards.GetValueOrDefault((cardId, upgradeLevel), cardId);

    private static Dictionary<(string Id, int Upgrade), string> CanonicalCardNames()
    {
        string language = LocManager.Instance.Language;
        if (_canonicalCardNames == null || !string.Equals(_canonicalCardLanguage, language, StringComparison.Ordinal))
        {
            Dictionary<(string Id, int Upgrade), string> names = [];
            foreach (CardModel canonical in ModelDb.AllCards)
                CaptureCardTitles(names, canonical, overwrite: false);
            _canonicalCardNames = names;
            _canonicalCardLanguage = language;
        }

        return new Dictionary<(string Id, int Upgrade), string>(_canonicalCardNames);
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
            if (!card.IsUpgradable)
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

    public string DamageSource(CombatDamageSource source)
        => source.Kind switch
        {
            CombatDamageSourceKind.Card => Card(source.Id ?? "卡牌"),
            CombatDamageSourceKind.Potion => Potion(source.Id ?? "药水"),
            CombatDamageSourceKind.Relic => Relic(source.Id ?? "遗物"),
            CombatDamageSourceKind.Power => _powers.GetValueOrDefault(source.Id ?? string.Empty, source.Id ?? "能力"),
            CombatDamageSourceKind.Poison => "毒",
            CombatDamageSourceKind.Thorns => "荆棘",
            CombatDamageSourceKind.Orb => $"球 {source.Id ?? string.Empty}".TrimEnd(),
            CombatDamageSourceKind.MonsterMove => "敌方行动",
            _ => "未知效果",
        };

    public string Creature(Creature? creature)
    {
        if (creature is null)
            return string.Empty;
        string fallback = creature.Monster?.Id.Entry is { } monsterId
            ? Monster(monsterId)
            : creature.Player?.Character?.Id.Entry ?? "玩家";
        return creature.CombatId is uint combatId
            ? _creatures.GetValueOrDefault(combatId, fallback)
            : fallback;
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
