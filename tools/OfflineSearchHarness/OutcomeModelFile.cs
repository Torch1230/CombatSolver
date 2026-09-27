using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

// Offline file/selection boundary. Search receives one already validated head;
// it never parses files, infers a character, or switches models inside a search.
internal sealed class OutcomeModelFile
{
    internal sealed record CharacterDocument(int CharacterSchema,
        Dictionary<string, SearchOutcomeValueModel.Document> CharacterModels);

    private readonly SearchOutcomeValueModel? _shared;
    private readonly Dictionary<string, SearchOutcomeValueModel>? _characters;
    internal bool IsConditional => _characters != null;

    private OutcomeModelFile(SearchOutcomeValueModel shared) => _shared = shared;
    private OutcomeModelFile(Dictionary<string, SearchOutcomeValueModel> characters) => _characters = characters;

    internal static OutcomeModelFile Read(string path) => Parse(File.ReadAllText(path));

    internal static OutcomeModelFile Parse(string json)
    {
        using var input = JsonDocument.Parse(json);
        if (!input.RootElement.TryGetProperty("CharacterSchema", out _))
            return new(SearchOutcomeValueModel.Load(input.RootElement.Deserialize<SearchOutcomeValueModel.Document>()
                ?? throw new InvalidDataException("Missing outcome model.")));
        var document = input.RootElement.Deserialize<CharacterDocument>();
        if (document is not { CharacterSchema: 1, CharacterModels.Count: > 0 })
            throw new InvalidDataException("Invalid character-conditioned outcome model.");
        Dictionary<string, SearchOutcomeValueModel> models = new(StringComparer.Ordinal);
        foreach (var (character, head) in document.CharacterModels)
        {
            if (string.IsNullOrWhiteSpace(character) || head == null)
                throw new InvalidDataException("Invalid character head.");
            // Validate every head, including its game MVID and feature/tree
            // schema. A corrupt unused head must not hide until another battle.
            models.Add(character, SearchOutcomeValueModel.Load(head));
        }
        return new(models);
    }

    internal SearchOutcomeValueModel Select(string? character)
    {
        if (_shared != null) return _shared;
        if (character == null || !_characters!.TryGetValue(character, out var model))
            throw new InvalidDataException("No trained outcome head for character: " + character);
        return model;
    }

    internal static string CharacterOf(IReadOnlyList<SearchOutcomeValueModel.TrainingRow> rows)
    {
        string? character = null;
        foreach (var row in rows)
        {
            var identities = row.Features.Where(p => p.Key.StartsWith("character/", StringComparison.Ordinal)).ToArray();
            if (identities.Length != 1 || identities[0].Value != 1 || identities[0].Key.Length == "character/".Length)
                throw new InvalidDataException("Missing or ambiguous native character observation.");
            string current = identities[0].Key["character/".Length..];
            if (character != null && character != current)
                throw new InvalidDataException("One actual root contains different characters.");
            character = current;
        }
        return character ?? throw new InvalidDataException("Cannot identify an empty training root.");
    }
}
